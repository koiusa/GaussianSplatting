Shader "Hidden/GaussianSplatting/MeshShadow"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float4 _MmdShadowRight, _MmdShadowUp, _MmdShadowDepth;
            sampler2D _MmdShadowMainTex;
            float _MmdShadowOpacity;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float depth : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                float4 wp = float4(world, 1.0);
                float2 uv = float2(dot(wp, _MmdShadowRight), dot(wp, _MmdShadowUp));
                o.depth = saturate(dot(wp, _MmdShadowDepth));
#if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
                float z = o.depth * 2.0 - 1.0;
#else
                float z = o.depth;
#endif
                o.pos = float4(uv * 2.0 - 1.0, z, 1.0);
                o.uv = v.uv;
                return o;
            }
            float frag(v2f i) : SV_Target
            {
                clip(tex2D(_MmdShadowMainTex, i.uv).a * _MmdShadowOpacity - 0.25);
                return i.depth;
            }
            ENDHLSL
        }
    }
}
