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
        private GaussianSplatOffAxisController _offAxisController;
        private GaussianMeshShadowRenderer _meshShadowRenderer;
        private IGaussianSplatShadowSource _shadowSource;

        /// <summary>
        /// Compatibility entry point for model integrations. A source is forwarded only when
        /// an optional <see cref="GaussianMeshShadowRenderer"/> is explicitly attached.
        /// </summary>
        public IGaussianSplatShadowSource ShadowSource
        {
            get => _meshShadowRenderer != null
                ? _meshShadowRenderer.ShadowSource
                : _shadowSource;
            set
            {
                _shadowSource = value;
                if (_meshShadowRenderer == null)
                    TryGetComponent(out _meshShadowRenderer);
                if (_meshShadowRenderer != null)
                    _meshShadowRenderer.ShadowSource = value;
            }
        }

        public bool   IsLoaded       => _splats != null && _splatBuffer != null;
        public int    SplatCount     => _splats?.Length ?? 0;
        public string LoadedFilePath { get; private set; }
        public Bounds LocalBounds    { get; private set; }

        // ------------------------------------------------------------------ //

        private void OnEnable() => CacheOptionalComponents();

        private void CacheOptionalComponents()
        {
            TryGetComponent(out _offAxisController);
            TryGetComponent(out _meshShadowRenderer);
            if (_meshShadowRenderer != null && _shadowSource != null)
                _meshShadowRenderer.ShadowSource = _shadowSource;
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

            if (_offAxisController == null)
                TryGetComponent(out _offAxisController);

            bool useOffAxis = _offAxisController != null && _offAxisController.isActiveAndEnabled;
            Vector3 eyeWorld;
            Vector3 viewForwardWorld;
            if (useOffAxis)
                _offAxisController.GetView(cam, out eyeWorld, out viewForwardWorld);
            else
            {
                eyeWorld = cam.transform.position;
                viewForwardWorld = cam.transform.forward;
            }

            float maxCullUpdatesPerSecond = useOffAxis ? _offAxisController.MaxCullUpdatesPerSecond : 30f;
            float cullPositionThreshold = useOffAxis ? _offAxisController.CullPositionThreshold : 0.01f;
            float cullAngleThreshold = useOffAxis ? _offAxisController.CullAngleThreshold : 0.1f;
            float maxSortUpdatesPerSecond = useOffAxis ? _offAxisController.MaxSortUpdatesPerSecond : 30f;
            float sortPositionThreshold = useOffAxis ? _offAxisController.SortPositionThreshold : 0.01f;
            float sortAngleThreshold = useOffAxis ? _offAxisController.SortAngleThreshold : 0.1f;

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

                    // The sort payload is already independent from the culling source buffer,
                    // so publish it progressively even if the tracked eye moved meanwhile.
                    // Holding the previous payload made the display remain visibly coarse.
                    _activeSortSlot = _buildingSortSlot;
                    _activeDrawCount = _buildingDrawCount;
                    _lastSortEyeWorld = _buildingEyeWorld;
                    _lastSortForwardWorld = _buildingForwardWorld;
                    _lastSortSplatPosition = _buildingSplatPosition;
                    _lastSortSplatRotation = _buildingSplatRotation;
                    _lastSortSplatScale = _buildingSplatScale;
                    _hasSorted = true;
                    _buildingSort = false;

                    if (useOffAxis && _offAxisController.LogDiagnostics)
                        Debug.Log($"[GaussianSplat OffAxis] sort publish frame={Time.frameCount} "
                            + $"visible={_activeDrawCount} eye={_lastSortEyeWorld:F4}", this);

                    // Do not let transform-following sorts starve a required recull.
                    bool cullAnchorInvalid = HasLeftCullEnvelope(
                        eyeWorld, viewForwardWorld,
                        cullPositionThreshold, cullAngleThreshold);
                    if (transformMovedDuringSort && _culler.Ready && !cullAnchorInvalid)
                    {
                        BeginSort(camLocalPos, camLocalForward, eyeWorld, viewForwardWorld);
                        return;
                    }
                }
            }

            bool cullResultChanged = !_buildingSort && _culler.ConsumeResultChanged();
            if (cullResultChanged && _culler.Ready)
            {
                bool staleCullResult = useOffAxis
                    && ((eyeWorld - _lastCullEyeWorld).sqrMagnitude
                            >= cullPositionThreshold * cullPositionThreshold
                        || Vector3.Angle(viewForwardWorld, _lastCullForwardWorld)
                            >= cullAngleThreshold);
                if (useOffAxis && _offAxisController.LogDiagnostics)
                    Debug.Log($"[GaussianSplat OffAxis] cull ready frame={Time.frameCount} "
                        + $"visible={_culler.VisibleCount}/{_splats.Length} eye={eyeWorld:F4} "
                        + $"stale={staleCullResult}", this);
                if (!staleCullResult)
                    BeginSort(camLocalPos, camLocalForward, eyeWorld, viewForwardWorld);
            }

            if (_buildingSort || cullResultChanged) return;

            float now = Time.unscaledTime;
            bool cullIntervalElapsed = !_hasCullAnchor
                || now - _lastCullTime >= 1f / Mathf.Max(0.1f, maxCullUpdatesPerSecond);
            bool leftCullEnvelope = HasLeftCullEnvelope(
                eyeWorld, viewForwardWorld,
                cullPositionThreshold, cullAngleThreshold);

            // 視点が前回の保守的なカリング範囲を出たときだけ、最大指定Hzで再カリングする。
            if (cullIntervalElapsed && leftCullEnvelope && _culler.CanDispatch)
            {
                // Camera.transform ではなく、Off-Axis が上書きした実際の View/Projection 行列から
                // 視錐台を構築する。眼位置が基準カメラ位置から動いた分も全平面へ加算し、
                // 非同期カリング～Radix Sort 完了までの間に欠けにくい保守的な範囲にする。
                Matrix4x4 viewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
                var planes = GeometryUtility.CalculateFrustumPlanes(viewProjection);
                float eyeOffset = useOffAxis ? Vector3.Distance(eyeWorld, cam.transform.position) : 0f;
                var lossyScale = transform.lossyScale;
                float worldScale = Mathf.Max(Mathf.Abs(lossyScale.x),
                    Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
                Vector3 boundsCenterWorld = transform.TransformPoint(LocalBounds.center);
                float boundsRadiusWorld = LocalBounds.extents.magnitude * worldScale;
                float angularMargin = (Vector3.Distance(eyeWorld, boundsCenterWorld)
                    + boundsRadiusWorld) * Mathf.Tan(cullAngleThreshold * Mathf.Deg2Rad);
                float cullMargin = eyeOffset
                    + Mathf.Max(useOffAxis ? _offAxisController.CullSafetyMargin : 0f,
                                cullPositionThreshold)
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
                if (useOffAxis && _offAxisController.LogDiagnostics)
                    Debug.Log($"[GaussianSplat OffAxis] cull dispatch frame={Time.frameCount} "
                        + $"margin={cullMargin:F4} eye={eyeWorld:F4}", this);
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

        private bool HasLeftCullEnvelope(Vector3 eyeWorld, Vector3 forwardWorld,
                                         float positionThreshold, float angleThreshold)
        {
            return !_hasCullAnchor
                || (eyeWorld - _lastCullEyeWorld).sqrMagnitude
                    >= positionThreshold * positionThreshold
                || Vector3.Angle(forwardWorld, _lastCullForwardWorld) >= angleThreshold
                || HasTransformChanged(_lastCullSplatPosition,
                                       _lastCullSplatRotation,
                                       _lastCullSplatScale);
        }

        private int EffectiveSplatCount => _activeSortSlot >= 0 ? _activeDrawCount : 0;

        // Queue a normal procedural renderer before cameras are rendered. Unity then inserts
        // it into the active Built-in/URP/HDRP pipeline at the material's transparent queue.
        // This avoids SRP event timing issues (draws before clear or after target release).
        private void Update()
        {
            if (!IsLoaded || material == null || EffectiveSplatCount <= 0) return;
            var cam = Camera.main;
            if (cam == null) return;

            SetMaterialProperties();
            Graphics.DrawProcedural(material, TransformBounds(LocalBounds, transform.localToWorldMatrix),
                MeshTopology.Triangles, EffectiveSplatCount * 6, 1, cam, null,
                ShadowCastingMode.Off, false, gameObject.layer);
        }

        private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(local.center);
            var extents = local.extents;
            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            extents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, extents * 2f);
        }

        private void SetMaterialProperties()
        {
            material.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
            material.SetBuffer("_SplatBuffer",   _splatBuffer);
            if (_activeSortSlot >= 0)
                material.SetBuffer("_SortedIndices", _sorters[_activeSortSlot].SortedIndexBuffer);
            if (_meshShadowRenderer == null)
                TryGetComponent(out _meshShadowRenderer);
            if (_meshShadowRenderer != null && _shadowSource != null
                && !ReferenceEquals(_meshShadowRenderer.ShadowSource, _shadowSource))
                _meshShadowRenderer.ShadowSource = _shadowSource;
            if (_meshShadowRenderer != null && _meshShadowRenderer.isActiveAndEnabled)
                _meshShadowRenderer.ApplyMaterialProperties(material);
            else
            {
                material.SetFloat("_MmdDropShadowOpacity", 0f);
                material.SetFloat("_MmdMeshShadowEnabled", 0f);
            }
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
