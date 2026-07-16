using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>
    /// Gaussian Splatting レンダラー。Built-in / URP / HDRP の全 RP に対応。
    ///
    /// Inspector 必須:
    ///   material   — GaussianSplatting/GaussianSplat シェーダーを使ったマテリアル
    ///   sortShader — GaussianSplatSort.compute (null 時は CPU ソートにフォールバック)
    /// </summary>
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [SerializeField] public Material      material;
        [SerializeField] public ComputeShader sortShader;
        [SerializeField] public ComputeShader cullShader; // GaussianSplatCull.compute（null なら視錐台カリングなし）
        [SerializeField, Min(4096)] private int asyncUploadChunkSize = 65536;
        [SerializeField, Min(0f), Tooltip("Off-Axis の眼位置移動に備えて視錐台を広げる追加マージン（ワールド単位）")]
        private float offAxisCullSafetyMargin = 0.1f;
        [Header("Off-Axis Update Throttling")]
        [SerializeField, Min(0.1f)] private float maxCullUpdatesPerSecond = 5f;
        [SerializeField, Min(0f)] private float cullPositionThreshold = 0.1f;
        [SerializeField, Range(0f, 30f)] private float cullAngleThreshold = 2f;
        [SerializeField, Min(0.1f)] private float maxSortUpdatesPerSecond = 12f;
        [SerializeField, Min(0f)] private float sortPositionThreshold = 0.03f;
        [SerializeField, Range(0f, 30f)] private float sortAngleThreshold = 0.75f;
        [Header("MMD Drop Shadow")]
        [SerializeField] private bool receiveMmdDropShadow = true;
        [SerializeField, Range(0f, 1f)] private float dropShadowOpacity = 0.9f;
        [SerializeField, Range(0f, 0.95f), Tooltip("影の中心からこの比率までは濃さを維持する")]
        private float dropShadowHardness = 0.6f;
        [SerializeField, Min(0.05f)] private float dropShadowRadiusScale = 0.85f;
        [SerializeField, Min(0.01f), Tooltip("足元より上にあるSplatへ影を許容する、モデル高に対する比率")]
        private float dropShadowAboveTolerance = 0.04f;
        [SerializeField, Min(0.01f), Tooltip("足元より下にある地面へ影を届かせる、モデル高に対する比率")]
        private float dropShadowBelowTolerance = 0.2f;

        private ComputeBuffer _splatBuffer;
        private GaussianSplatGPU[] _splats;
        private readonly GpuRadixSorter[] _sorters = new GpuRadixSorter[2];
        private GpuFrustumCuller     _culler;
        private int                  _loadGeneration;
        private int _activeSortSlot = -1;
        private int _buildingSortSlot;
        private int _activeDrawCount;
        private int _buildingDrawCount;
        private bool _buildingSort;
        private Vector3 _buildingEyeWorld;
        private Vector3 _buildingForwardWorld;
        private Vector3 _buildingSplatPosition;
        private Quaternion _buildingSplatRotation;
        private Vector3 _buildingSplatScale;

        public string LastLoadError { get; private set; }

        // HDRP は OnRenderObject を呼ばないため、SRP のコールバックで代替する
        private bool _isHDRP;

        // 顔追跡の微小ノイズでは前回結果を再利用し、カリングとソートを別々の頻度で更新する。
        private bool    _hasSorted;
        private Vector3 _lastSortEyeWorld;
        private Vector3 _lastSortForwardWorld;
        private Vector3 _lastSortSplatPosition;
        private Quaternion _lastSortSplatRotation;
        private Vector3 _lastSortSplatScale;
        private Vector3 _lastCullEyeWorld;
        private Vector3 _lastCullForwardWorld;
        private Vector3 _lastCullSplatPosition;
        private Quaternion _lastCullSplatRotation;
        private Vector3 _lastCullSplatScale;
        private float _lastSortTime;
        private float _lastCullTime;
        private bool _hasCullAnchor;
        private Light _shadowLight;

        /// <summary>
        /// Optional project-specific source for the drop-shadow receiver data.
        /// The package deliberately does not depend on LibMMD or another model system.
        /// </summary>
        public IGaussianSplatShadowSource ShadowSource { get; set; }

        public bool   IsLoaded       => _splats != null && _splatBuffer != null;
        public int    SplatCount     => _splats?.Length ?? 0;
        public string LoadedFilePath { get; private set; }
        public Bounds LocalBounds    { get; private set; }

        // ------------------------------------------------------------------ //

        private void OnEnable()
        {
            if (GetComponent<GaussianMeshShadowRenderer>() == null)
                gameObject.AddComponent<GaussianMeshShadowRenderer>();
            _isHDRP = DetectHDRP();
            if (_isHDRP)
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRenderingHDRP;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRenderingHDRP;
        }

        // ------------------------------------------------------------------ //

        public void LoadSplats(GaussianSplatGPU[] splats, string filePath = "")
        {
            Release();
            if (splats == null || splats.Length == 0) return;

            _splats        = splats;
            LoadedFilePath = filePath;
            LocalBounds    = ComputeLocalBounds(splats);

            const int stride = 64;
            _splatBuffer = new ComputeBuffer(splats.Length, stride);
            _splatBuffer.SetData(splats);

            InitializeGpuPipeline(splats.Length);

            _hasSorted = false;
        }

        /// <summary>
        /// 大規模な managed 配列を複数フレームに分けて GPU へ転送する。
        /// PLY 解析自体は呼び出し側のワーカースレッドで完了済みであることを想定する。
        /// </summary>
        public IEnumerator LoadSplatsAsync(GaussianSplatGPU[] splats, string filePath = "",
                                            Action<float> onProgress = null)
        {
            Release();
            LastLoadError = null;
            if (splats == null || splats.Length == 0) yield break;

            int generation = _loadGeneration;
            const int stride = 64;
            int chunkSize = Mathf.Max(4096, asyncUploadChunkSize);
            var boundsMin = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            var boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            bool allocationFailed = false;
            try
            {
                _splatBuffer = new ComputeBuffer(splats.Length, stride);
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                allocationFailed = true;
            }
            if (allocationFailed)
            {
                Release();
                yield break;
            }

            for (int offset = 0; offset < splats.Length; offset += chunkSize)
            {
                if (generation != _loadGeneration) yield break;
                int count = Mathf.Min(chunkSize, splats.Length - offset);
                bool uploadFailed = false;
                try
                {
                    _splatBuffer.SetData(splats, offset, offset, count);
                    int end = offset + count;
                    for (int i = offset; i < end; i++)
                    {
                        boundsMin = Vector3.Min(boundsMin, splats[i].position);
                        boundsMax = Vector3.Max(boundsMax, splats[i].position);
                    }
                }
                catch (Exception ex)
                {
                    LastLoadError = ex.Message;
                    uploadFailed = true;
                }
                if (uploadFailed)
                {
                    Release();
                    yield break;
                }

                onProgress?.Invoke((offset + count) / (float)splats.Length);
                yield return null;
            }

            if (generation != _loadGeneration) yield break;
            _splats        = splats;
            LoadedFilePath = filePath;
            var bounds = new Bounds();
            bounds.SetMinMax(boundsMin, boundsMax);
            LocalBounds = bounds;

            InitializeGpuPipeline(splats.Length);

            _hasSorted = false;
            onProgress?.Invoke(1f);
        }

        public void Clear()
        {
            Release();
            LoadedFilePath = "";
        }

        // ------------------------------------------------------------------ //

        private void LateUpdate()
        {
            if (!IsLoaded || _culler == null || _sorters[0] == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            // Off-Axis ProjectionではCamera.transformと実際のView Matrixの眼位置・向きが異なる。
            // 描画シェーダーと同じworldToCameraMatrixからソート基準を復元する。
            Matrix4x4 cameraToWorld = cam.worldToCameraMatrix.inverse;
            Vector3 eyeWorld = cameraToWorld.MultiplyPoint3x4(Vector3.zero);
            Vector3 viewForwardWorld = -(Vector3)cameraToWorld.GetColumn(2);
            viewForwardWorld.Normalize();

            var camLocalPos = transform.InverseTransformPoint(eyeWorld);
            var camLocalForward = transform.InverseTransformDirection(viewForwardWorld);

            // 裏側のソートを進め、完了した瞬間に IndexBuffer と描画数を同時交換する。
            if (_buildingSort)
            {
                _sorters[_buildingSortSlot].Tick();
                if (!_sorters[_buildingSortSlot].IsBusy)
                {
                    bool transformMovedDuringSort = HasTransformChanged(
                        _buildingSplatPosition, _buildingSplatRotation, _buildingSplatScale);

                    // Publish each completed result even if the root moved meanwhile. Dropping
                    // it froze the visible ordering until manipulation stopped. A slightly
                    // delayed result is preferable and lets rotation update every eight frames.
                    _activeSortSlot = _buildingSortSlot;
                    _activeDrawCount = _buildingDrawCount;
                    _lastSortEyeWorld = _buildingEyeWorld;
                    _lastSortForwardWorld = _buildingForwardWorld;
                    _lastSortSplatPosition = _buildingSplatPosition;
                    _lastSortSplatRotation = _buildingSplatRotation;
                    _lastSortSplatScale = _buildingSplatScale;
                    _hasSorted = true;
                    _buildingSort = false;

                    // Keep one radix pass per frame, but immediately start the next eight-pass
                    // cycle from the current transform. Reuse the conservative visible set
                    // while manipulation continues; culling catches up when rotation settles.
                    if (transformMovedDuringSort && _culler.Ready)
                    {
                        BeginSort(camLocalPos, camLocalForward, eyeWorld, viewForwardWorld);
                        return;
                    }
                }
            }

            bool cullResultChanged = !_buildingSort && _culler.ConsumeResultChanged();
            if (cullResultChanged && _culler.Ready)
            {
                BeginSort(camLocalPos, camLocalForward, eyeWorld, viewForwardWorld);
            }

            if (_buildingSort || cullResultChanged) return;

            float now = Time.unscaledTime;
            bool cullIntervalElapsed = !_hasCullAnchor
                || now - _lastCullTime >= 1f / Mathf.Max(0.1f, maxCullUpdatesPerSecond);
            bool leftCullEnvelope = !_hasCullAnchor
                || (eyeWorld - _lastCullEyeWorld).sqrMagnitude
                    >= cullPositionThreshold * cullPositionThreshold
                || Vector3.Angle(viewForwardWorld, _lastCullForwardWorld) >= cullAngleThreshold
                || HasTransformChanged(_lastCullSplatPosition,
                                       _lastCullSplatRotation,
                                       _lastCullSplatScale);

            // 視点が前回の保守的なカリング範囲を出たときだけ、最大指定Hzで再カリングする。
            if (cullIntervalElapsed && leftCullEnvelope && _culler.CanDispatch)
            {
                // Camera.transform ではなく、Off-Axis が上書きした実際の View/Projection 行列から
                // 視錐台を構築する。眼位置が基準カメラ位置から動いた分も全平面へ加算し、
                // 非同期カリング～Radix Sort 完了までの間に欠けにくい保守的な範囲にする。
                Matrix4x4 viewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
                var planes = GeometryUtility.CalculateFrustumPlanes(viewProjection);
                float eyeOffset = Vector3.Distance(eyeWorld, cam.transform.position);
                var lossyScale = transform.lossyScale;
                float worldScale = Mathf.Max(Mathf.Abs(lossyScale.x),
                    Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
                Vector3 boundsCenterWorld = transform.TransformPoint(LocalBounds.center);
                float boundsRadiusWorld = LocalBounds.extents.magnitude * worldScale;
                float angularMargin = (Vector3.Distance(eyeWorld, boundsCenterWorld)
                    + boundsRadiusWorld) * Mathf.Tan(cullAngleThreshold * Mathf.Deg2Rad);
                float cullMargin = eyeOffset
                    + Mathf.Max(offAxisCullSafetyMargin, cullPositionThreshold)
                    + angularMargin;
                _culler.Dispatch(_splatBuffer, _splats.Length, transform.localToWorldMatrix,
                    planes, worldScale, cullMargin);
                _lastCullEyeWorld = eyeWorld;
                _lastCullForwardWorld = viewForwardWorld;
                _lastCullSplatPosition = transform.position;
                _lastCullSplatRotation = transform.rotation;
                _lastCullSplatScale = transform.lossyScale;
                _lastCullTime = now;
                _hasCullAnchor = true;
                return;
            }

            // カリング範囲内の小さな移動では可視リストを再利用し、ソートだけ低頻度で更新する。
            bool sortIntervalElapsed = now - _lastSortTime
                >= 1f / Mathf.Max(0.1f, maxSortUpdatesPerSecond);
            bool needsResort = _hasSorted
                && ((eyeWorld - _lastSortEyeWorld).sqrMagnitude
                        >= sortPositionThreshold * sortPositionThreshold
                    || Vector3.Angle(viewForwardWorld, _lastSortForwardWorld) >= sortAngleThreshold);
            needsResort = needsResort || (_hasSorted && HasTransformChanged(
                _lastSortSplatPosition, _lastSortSplatRotation, _lastSortSplatScale));
            if (sortIntervalElapsed && needsResort && _culler.Ready)
                BeginSort(camLocalPos, camLocalForward, eyeWorld, viewForwardWorld);
        }

        private void BeginSort(Vector3 camLocalPos, Vector3 camLocalForward,
                               Vector3 eyeWorld, Vector3 forwardWorld)
        {
            _buildingSortSlot = _activeSortSlot == 0 ? 1 : 0;
            _buildingDrawCount = _culler.VisibleCount;
            _buildingEyeWorld = eyeWorld;
            _buildingForwardWorld = forwardWorld;
            _buildingSplatPosition = transform.position;
            _buildingSplatRotation = transform.rotation;
            _buildingSplatScale = transform.lossyScale;
            _sorters[_buildingSortSlot].Sort(_splatBuffer, _splats, _buildingDrawCount,
                camLocalPos, camLocalForward, _culler.VisibleIndexBuffer);
            _buildingSort = true;
            _lastSortTime = Time.unscaledTime;
        }

        private bool HasTransformChanged(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            return (transform.position - position).sqrMagnitude > 1e-10f
                || Quaternion.Angle(transform.rotation, rotation) > 0.001f
                || (transform.lossyScale - scale).sqrMagnitude > 1e-10f;
        }

        private int EffectiveSplatCount => _activeSortSlot >= 0 ? _activeDrawCount : 0;

        // Built-in RP および URP: 両パイプラインとも OnRenderObject を呼ぶ
        private void OnRenderObject()
        {
            if (_isHDRP || !IsLoaded || material == null) return;

            var cam = Camera.current;
            if (cam == null || cam != Camera.main) return;

            SetMaterialProperties();
            material.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, EffectiveSplatCount * 6);
        }

        // HDRP: OnRenderObject が呼ばれないため SRP コールバックで描画する
        private void OnBeginCameraRenderingHDRP(ScriptableRenderContext ctx, Camera cam)
        {
            if (!IsLoaded || material == null || cam != Camera.main) return;

            SetMaterialProperties();

            var cmd = new CommandBuffer { name = "GaussianSplat" };
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, EffectiveSplatCount * 6);
            ctx.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        private void SetMaterialProperties()
        {
            material.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
            material.SetBuffer("_SplatBuffer",   _splatBuffer);
            if (_activeSortSlot >= 0)
                material.SetBuffer("_SortedIndices", _sorters[_activeSortSlot].SortedIndexBuffer);
            SetDropShadowProperties();
        }

        private void SetDropShadowProperties()
        {
            if (!receiveMmdDropShadow || ShadowSource == null
                || !ShadowSource.TryGetShadowCasters(out _, out var bounds))
            {
                material.SetFloat("_MmdDropShadowOpacity", 0f);
                return;
            }

            float footprint = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float radius = Mathf.Max(0.05f, footprint * dropShadowRadiusScale);
            float modelHeight = Mathf.Max(bounds.size.y, 0.1f);
            Vector2 projectedAxis = GetProjectedShadowAxis(modelHeight);
            material.SetVector("_MmdDropShadowCenterRadius",
                new Vector4(bounds.center.x, bounds.min.y, bounds.center.z, radius));
            material.SetVector("_MmdDropShadowAxis",
                new Vector4(projectedAxis.x, projectedAxis.y, 0f, 0f));
            material.SetVector("_MmdDropShadowParams",
                new Vector4(
                    Mathf.Max(0.01f, modelHeight * dropShadowAboveTolerance),
                    Mathf.Max(0.01f, modelHeight * dropShadowBelowTolerance),
                    dropShadowHardness, 0f));
            material.SetFloat("_MmdDropShadowOpacity", dropShadowOpacity);
        }

        private Vector2 GetProjectedShadowAxis(float modelHeight)
        {
            if (_shadowLight == null || !_shadowLight.isActiveAndEnabled
                || _shadowLight.type != LightType.Directional)
            {
                _shadowLight = RenderSettings.sun;
                if (_shadowLight == null || _shadowLight.type != LightType.Directional)
                {
                    foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    {
                        if (light.isActiveAndEnabled && light.type == LightType.Directional)
                        {
                            _shadowLight = light;
                            break;
                        }
                    }
                }
            }

            if (_shadowLight == null) return Vector2.zero;

            // A directional light's forward vector is the direction travelled by its rays.
            // Project a ray from the top of the model down to the feet plane.  Clamp grazing
            // angles so an almost-horizontal light cannot create an unbounded shadow.
            Vector3 ray = _shadowLight.transform.forward.normalized;
            float downward = Mathf.Max(0.15f, -ray.y);
            var axis = new Vector2(ray.x, ray.z) * (modelHeight / downward);
            return Vector2.ClampMagnitude(axis, modelHeight * 3f);
        }

        // ------------------------------------------------------------------ //

        private void Release()
        {
            _loadGeneration++;
            _splatBuffer?.Release(); _splatBuffer = null;
            for (int i = 0; i < 2; i++)
            {
                _sorters[i]?.Dispose();
                _sorters[i] = null;
            }
            _culler?.Dispose();      _culler      = null;
            _splats     = null;
            LocalBounds = default;
            _activeSortSlot = -1;
            _activeDrawCount = 0;
            _buildingSort = false;
            _hasSorted = false;
            _hasCullAnchor = false;
            _lastSortTime = 0f;
            _lastCullTime = 0f;
        }

        private void InitializeGpuPipeline(int splatCount)
        {
            for (int i = 0; i < 2; i++)
            {
                _sorters[i]?.Dispose();
                _sorters[i] = null;
            }
            _culler?.Dispose();
            _culler = null;

            if (!SystemInfo.supportsComputeShaders || sortShader == null || cullShader == null)
            {
                LastLoadError = "GPUカリング・ソート用Compute Shaderが利用できません。";
                Debug.LogError($"[GaussianSplatting] {LastLoadError}");
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                _sorters[i] = new GpuRadixSorter(sortShader);
                _sorters[i].Initialize(splatCount);
            }
            _culler = new GpuFrustumCuller(cullShader);
            _culler.Initialize(splatCount);
            _activeSortSlot = -1;
            _activeDrawCount = 0;
            _buildingSort = false;
            _hasSorted = false;
            _hasCullAnchor = false;
        }

        private static bool DetectHDRP()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            return pipeline != null && pipeline.GetType().FullName.Contains("HDRenderPipeline");
        }

        private static Bounds ComputeLocalBounds(GaussianSplatGPU[] splats)
        {
            var min = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var s in splats)
            {
                min = Vector3.Min(min, s.position);
                max = Vector3.Max(max, s.position);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        private void OnDestroy() => Release();
    }
}
