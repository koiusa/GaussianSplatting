// Gaussian Splatting — shared vertex/fragment HLSL.
// Include the pipeline-specific header BEFORE #include-ing this file.

struct SplatData
{
    float3 pos;      // local space
    float  opacity;
    float4 rot;      // quaternion: x=w, y=qx, z=qy, w=qz
    float3 scale;
    float  pad;
    float3 color;    // linear RGB
    float  pad2;
};

StructuredBuffer<SplatData> _SplatBuffer;
StructuredBuffer<uint>      _SortedIndices;
float4x4 _LocalToWorld;
float4 _MmdDropShadowCenterRadius; // xyz: feet center, w: footprint radius
float4 _MmdDropShadowAxis;         // xy: projected model-height axis on the XZ plane
float4 _MmdDropShadowParams;       // x/y: height tolerance, z: hard core radius [0, 0.95]
float  _MmdDropShadowOpacity;
sampler2D _MmdMeshShadowMap;
float4 _MmdShadowRight, _MmdShadowUp, _MmdShadowDepth;
float4 _MmdMeshShadowTexelSize;
float _MmdMeshShadowEnabled, _MmdMeshShadowBias;

struct v2f
{
    float4 pos      : SV_POSITION;
    float2 uv       : TEXCOORD0;  // [-1,1] quad 座標
    float4 color    : COLOR;
    // r_clamped / r_raw: クランプ後と真の半径の比率。
    // フラグメントで正確な Mahalanobis 距離を求めるために使う。
    float2 covScale : TEXCOORD1;
    float3 worldPos : TEXCOORD2;
};

void QuaternionAxes(float4 q, out float3 axisX, out float3 axisY, out float3 axisZ)
{
    float qw = q.x, qx = q.y, qy = q.z, qz = q.w;
    // Rotated local basis vectors (matrix columns), written explicitly to avoid
    // row/column-major indexing differences in D3D HLSL -> Metal translation.
    axisX = float3(1 - 2*(qy*qy + qz*qz), 2*(qx*qy + qw*qz), 2*(qx*qz - qw*qy));
    axisY = float3(2*(qx*qy - qw*qz), 1 - 2*(qx*qx + qz*qz), 2*(qy*qz + qw*qx));
    axisZ = float3(2*(qx*qz + qw*qy), 2*(qy*qz - qw*qx), 1 - 2*(qx*qx + qy*qy));
}

v2f vert(uint vertexID : SV_VertexID)
{
    uint corner = vertexID % 6u;
    float2 uv;
    uv.x = (corner == 1u || corner == 2u || corner == 4u) ?  1.0 : -1.0;
    uv.y = (corner == 2u || corner == 4u || corner == 5u) ?  1.0 : -1.0;

    uint splatID = _SortedIndices[vertexID / 6u];
    SplatData s  = _SplatBuffer[splatID];

    float3 localAxisX, localAxisY, localAxisZ;
    QuaternionAxes(s.rot, localAxisX, localAxisY, localAxisZ);
    localAxisX *= s.scale.x;
    localAxisY *= s.scale.y;
    localAxisZ *= s.scale.z;
    float3 posWorld = mul(_LocalToWorld, float4(s.pos, 1.0)).xyz;

    // HDRP camera-relative rendering: _LocalToWorld is world-space, UNITY_MATRIX_V
    // expects camera-relative world positions, so subtract the camera position first.
#if defined(SHADEROPTIONS_CAMERA_RELATIVE_RENDERING) && SHADEROPTIONS_CAMERA_RELATIVE_RENDERING == 1
    float4 viewPos4 = mul(UNITY_MATRIX_V, float4(posWorld - _WorldSpaceCameraPos.xyz, 1.0));
#else
    float4 viewPos4 = mul(mul(UNITY_MATRIX_V, _LocalToWorld), float4(s.pos, 1.0));
#endif

    float3 t = viewPos4.xyz;

    // Preserve the projection matrix signs. Metal and D3D can use different screen-Y
    // conventions; abs() erased that information and forced an unreliable API-specific
    // correction later in the covariance.
    float2 focal = float2(
        UNITY_MATRIX_P[0][0] * _ScreenParams.x * 0.5,
        UNITY_MATRIX_P[1][1] * _ScreenParams.y * 0.5
    );

    float tz = t.z;

    // tz がゼロ付近/正（カメラ平面上または背後）だと下の J（ヤコビアン）が発散し、
    // 巨大または NaN を含むスクリーン半径を生み得る。通常の対称パース投影では
    // 近クリップ面より手前の splat は事前に弾かれ起こりにくいが、オフ軸投影
    // （非対称フラスタム、視点がスクリーンに極端に近づくケースなど）では実際に
    // 発生し得る。発散したジオメトリは巨大な overdraw を生み GPU タイムアウト(TDR)
    // を誘発し得るため、該当 splat は退化三角形として安全にクリップする。
    // Off-Axisでは眼位置がCamera.transformと異なり、splat中心がnear planeへ実際に接近する。
    // nearより手前の中心をヤコビアンへ渡すと1/z, 1/z^2が急増するため安全に除外する。
    if (tz >= -max(_ProjectionParams.y, 1e-3))
    {
        v2f o;
        o.pos      = float4(2.0, 2.0, 1.0, 1.0);
        o.uv       = float2(0.0, 0.0);
        o.color    = float4(0.0, 0.0, 0.0, 0.0);
        o.covScale = float2(0.0, 0.0);
        o.worldPos = float3(0.0, 0.0, 0.0);
        return o;
    }

    float3x3 localToView = (float3x3)mul(UNITY_MATRIX_V, _LocalToWorld);
    float3 viewAxisX = mul(localToView, localAxisX);
    float3 viewAxisY = mul(localToView, localAxisY);
    float3 viewAxisZ = mul(localToView, localAxisZ);

    // Project each scaled quaternion basis vector through the perspective Jacobian,
    // then sum their outer products. This is algebraically J*W*Sigma*W^T*J^T,
    // without constructing/indexing intermediate matrices whose layout can differ on Metal.
    float2 projectedX = float2(
        focal.x * (viewAxisX.x / (-tz) + t.x * viewAxisX.z / (tz*tz)),
        focal.y * (viewAxisX.y / (-tz) + t.y * viewAxisX.z / (tz*tz)));
    float2 projectedY = float2(
        focal.x * (viewAxisY.x / (-tz) + t.x * viewAxisY.z / (tz*tz)),
        focal.y * (viewAxisY.y / (-tz) + t.y * viewAxisY.z / (tz*tz)));
    float2 projectedZ = float2(
        focal.x * (viewAxisZ.x / (-tz) + t.x * viewAxisZ.z / (tz*tz)),
        focal.y * (viewAxisZ.y / (-tz) + t.y * viewAxisZ.z / (tz*tz)));

    float a = dot(float3(projectedX.x, projectedY.x, projectedZ.x),
                  float3(projectedX.x, projectedY.x, projectedZ.x)) + 0.3;
    float bv = dot(float3(projectedX.x, projectedY.x, projectedZ.x),
                   float3(projectedX.y, projectedY.y, projectedZ.y));
    float c = dot(float3(projectedX.y, projectedY.y, projectedZ.y),
                  float3(projectedX.y, projectedY.y, projectedZ.y)) + 0.3;

    float tr   = a + c;
    float det  = a * c - bv * bv;
    float disc = sqrt(max(0.01, tr * tr * 0.25 - det));
    float l1   = tr * 0.5 + disc;
    float l2   = tr * 0.5 - disc;

    // クランプ前の真の 3σ 半径
    float r1_raw = 3.0 * sqrt(max(0.0, l1));
    float r2_raw = 3.0 * sqrt(max(0.0, l2));

    // スクリーン空間の最大半径クランプ。カメラが splat 群に極端に接近すると、多数の splat が
    // この上限近くまで膨らみ、1 枚ごとが画面の大部分を覆う巨大な四角形になってオーバードローが
    // 深刻化する（GPU タイムアウトの一因になり得る）。splat を間引かず（穴を開けず）に最悪ケースの
    // 重さを抑えるため、上限を 1024px から 256px へ引き下げる（最大面積で 16 分の1）。
    // min() は NaN の扱いが GPU/ドライバ依存で確実にクランプされない可能性があるため、
    // NaN 比較は常に false になる性質を利用した三項演算子で確実にクランプする。
    const float kMaxScreenRadius = 128.0;
    float r1 = (r1_raw < kMaxScreenRadius) ? r1_raw : kMaxScreenRadius;
    float r2 = (r2_raw < kMaxScreenRadius) ? r2_raw : kMaxScreenRadius;

    // クランプ比率: フラグメントで正確な alpha を求めるために渡す
    // d^2 = 9 * dot(uv * covScale, uv * covScale)  →  exp(-4.5 * dot(uv*s, uv*s))
    // クランプなし時は covScale = (1,1) で従来式と一致する
    // クランプ時にr/r_rawを使うと減衰が弱まり、quad全面が高alphaになってTDRを誘発する。
    // 安全上限内で正規化Gaussianとして評価する。
    float2 covScale = float2(1.0, 1.0);

    float2 ev = float2(bv, l1 - a);
    float2 v1 = (length(ev) > 1e-4) ? normalize(ev) : float2(1.0, 0.0);
    float2 v2 = float2(-v1.y, v1.x);

    float2 offset2D = uv.x * r1 * v1 + uv.y * r2 * v2;

    float4 clipPos = mul(UNITY_MATRIX_P, viewPos4);
    clipPos.xy += (offset2D / (_ScreenParams.xy * 0.5)) * clipPos.w;

    v2f o;
    o.pos      = clipPos;
    o.uv       = uv;
    o.color    = float4(s.color, s.opacity);
    o.covScale = covScale;
    o.worldPos = posWorld;
    return o;
}

float4 frag(v2f i) : SV_Target
{
    // 正確な楕円 Gaussian: d^2 = 9 * |uv * covScale|^2
    // covScale=(1,1) のとき exp(-4.5*dot(uv,uv)) と等価
    float2 scaled = i.uv * i.covScale;
    float alpha = exp(-4.5 * dot(scaled, scaled)) * i.color.a;
    clip(alpha - 0.003);

    // Built-in RP already renders the animated MMD mesh into the directional light's
    // shadow map. Sampling it at each splat's world position preserves the actual posed
    // mesh silhouette, including limbs, hair and clothing.
    float4 shadowWorld = float4(i.worldPos, 1.0);
    float2 shadowUv = float2(dot(shadowWorld, _MmdShadowRight), dot(shadowWorld, _MmdShadowUp));
    float receiverDepth = dot(shadowWorld, _MmdShadowDepth);
    float insideShadowMap = step(0.0, shadowUv.x) * step(shadowUv.x, 1.0)
        * step(0.0, shadowUv.y) * step(shadowUv.y, 1.0)
        * step(0.0, receiverDepth) * step(receiverDepth, 1.0);
    float meshShadow = 0.0;
    [unroll] for (int shadowY = -1; shadowY <= 1; shadowY++)
    [unroll] for (int shadowX = -1; shadowX <= 1; shadowX++)
    {
        float casterDepth = tex2D(_MmdMeshShadowMap,
            shadowUv + float2(shadowX, shadowY) * _MmdMeshShadowTexelSize.xy).r;
        meshShadow += step(casterDepth + _MmdMeshShadowBias, receiverDepth);
    }
    meshShadow = meshShadow / 9.0 * insideShadowMap;
    float shadow = saturate(meshShadow * _MmdDropShadowOpacity * _MmdMeshShadowEnabled);

    // Fall back to the analytic contact shadow until the dedicated mesh map is ready.
    if (_MmdMeshShadowEnabled < 0.5)
    {
    // SRP fallback: package-independent analytic approximation until a pipeline-specific
    // shadow-map adapter is supplied.
    float radius = max(_MmdDropShadowCenterRadius.w, 1e-4);
    // Distance to the projected model-height segment.  This turns the old circular
    // contact blob into a whole-model cast shadow whose direction and length follow
    // the scene's directional light.
    float2 fromFeet = i.worldPos.xz - _MmdDropShadowCenterRadius.xz;
    float2 axis = _MmdDropShadowAxis.xy;
    float axisLengthSq = dot(axis, axis);
    float alongAxis = axisLengthSq > 1e-6
        ? saturate(dot(fromFeet, axis) / axisLengthSq)
        : 0.0;
    float2 shadowDelta = (fromFeet - axis * alongAxis) / radius;
    // Keep a solid core and soften only the rim.  The previous polynomial faded
    // across the entire footprint and was almost invisible on bright splats.
    float radialDistance = length(shadowDelta);
    float radial = 1.0 - smoothstep(
        min(_MmdDropShadowParams.z, 0.95), 1.0, radialDistance);
    float heightDelta = i.worldPos.y - _MmdDropShadowCenterRadius.y;
    float receiverAbove = 1.0 - smoothstep(0.0, max(_MmdDropShadowParams.x, 1e-4), heightDelta);
    float receiverBelow = 1.0 - smoothstep(0.0, max(_MmdDropShadowParams.y, 1e-4), -heightDelta);
    float receiver = heightDelta >= 0.0 ? receiverAbove : receiverBelow;
        shadow = saturate(radial * receiver * _MmdDropShadowOpacity);
    }
    return float4(i.color.rgb * (1.0 - shadow), alpha);
}
