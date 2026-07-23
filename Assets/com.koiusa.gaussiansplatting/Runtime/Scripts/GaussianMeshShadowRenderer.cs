using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>Renders supplied animated meshes into a light-space depth map for splat receivers.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class GaussianMeshShadowRenderer : MonoBehaviour
    {
        [Header("MMD Drop Shadow")]
        [SerializeField] private bool receiveDropShadow = true;
        [SerializeField, Range(0f, 1f)] private float dropShadowOpacity = 0.9f;
        [SerializeField, Range(0f, 0.95f), Tooltip("影の中心からこの比率までは濃さを維持する")]
        private float dropShadowHardness = 0.6f;
        [SerializeField, Min(0.05f)] private float dropShadowRadiusScale = 0.85f;
        [SerializeField, Min(0.01f), Tooltip("足元より上にあるSplatへ影を許容する、モデル高に対する比率")]
        private float dropShadowAboveTolerance = 0.04f;
        [SerializeField, Min(0.01f), Tooltip("足元より下にある地面へ影を届かせる、モデル高に対する比率")]
        private float dropShadowBelowTolerance = 0.2f;
        [Header("MMD Mesh Shadow Map")]
        [SerializeField, Range(256, 2048)] private int resolution = 1024;
        [SerializeField, Range(0f, 0.005f), Tooltip("Light空間の正規化深度Bias。MMDとGaussianは別ジオメトリなので通常は0でよい")]
        private float depthBias = 0f;

        private GaussianSplatRenderer _splat;
        private RenderTexture _map;
        private Material _depthMaterial;
        private Light _shadowLight;
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> _bakedMeshes =
            new Dictionary<SkinnedMeshRenderer, Mesh>();

        /// <summary>
        /// Project-specific MMD/model adapter. The Gaussian package has no LibMMD dependency.
        /// </summary>
        public IGaussianSplatShadowSource ShadowSource { get; set; }

        private void Awake() => _splat = GetComponent<GaussianSplatRenderer>();

        private void LateUpdate()
        {
            if (_splat == null || !_splat.IsLoaded || _splat.material == null
                || ShadowSource == null
                || !ShadowSource.TryGetShadowCasters(out var renderers, out var bounds)
                || renderers == null || renderers.Length == 0)
            {
                if (_splat != null && _splat.material != null)
                    _splat.material.SetFloat("_MmdMeshShadowEnabled", 0f);
                return;
            }

            Light light = FindDirectionalLight();
            if (light == null || !EnsureResources())
            {
                _splat.material.SetFloat("_MmdMeshShadowEnabled", 0f);
                return;
            }

            Vector3 forward = light.transform.forward.normalized;
            Vector3 hint = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f
                ? Vector3.forward : Vector3.up;
            Vector3 right = Vector3.Cross(hint, forward).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;

            ProjectBounds(bounds, right, up, forward, out var min, out var max);
            Bounds receiverBounds = TransformBounds(_splat.LocalBounds, _splat.transform.localToWorldMatrix);
            ProjectBounds(receiverBounds, right, up, forward, out var receiverMin, out var receiverMax);
            min.z = Mathf.Min(min.z, receiverMin.z);
            max.z = Mathf.Max(max.z, receiverMax.z);
            float padding = Mathf.Max(0.02f, Mathf.Max(max.x - min.x, max.y - min.y) * 0.05f);
            float width = Mathf.Max(0.01f, max.x - min.x + padding * 2f);
            float height = Mathf.Max(0.01f, max.y - min.y + padding * 2f);
            float depthRange = Mathf.Max(0.01f, max.z - min.z + padding * 2f);
            Vector3 origin = right * ((min.x + max.x) * 0.5f)
                + up * ((min.y + max.y) * 0.5f) + forward * (min.z - padding);

            Vector4 rowX = MakeProjectionRow(right, origin, width, 0.5f);
            Vector4 rowY = MakeProjectionRow(up, origin, height, 0.5f);
            Vector4 rowZ = MakeProjectionRow(forward, origin, depthRange, 0f);

            var cmd = new CommandBuffer { name = "Gaussian MMD Mesh Shadow" };
            cmd.SetRenderTarget(_map);
            cmd.ClearRenderTarget(true, true, Color.white);
            cmd.SetGlobalVector("_MmdShadowRight", rowX);
            cmd.SetGlobalVector("_MmdShadowUp", rowY);
            cmd.SetGlobalVector("_MmdShadowDepth", rowZ);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                var materials = renderer.sharedMaterials;
                var skinnedRenderer = renderer as SkinnedMeshRenderer;
                var bakedMesh = skinnedRenderer != null ? GetBakedMesh(skinnedRenderer) : null;
                for (int subMesh = 0; subMesh < materials.Length; subMesh++)
                {
                    var source = materials[subMesh];
                    Texture texture = source != null && source.HasProperty("_MainTex")
                        ? source.GetTexture("_MainTex") : null;
                    if (texture == null) texture = Texture2D.whiteTexture;
                    float opacity = source != null && source.HasProperty("_Opacity")
                        ? source.GetFloat("_Opacity") : 1f;
                    cmd.SetGlobalTexture("_MmdShadowMainTex", texture);
                    cmd.SetGlobalFloat("_MmdShadowOpacity", opacity);
                    if (bakedMesh != null)
                    {
                        // DrawRenderer with an override material can use the bind-pose vertex
                        // stream. Drawing the baked mesh guarantees VMD skinning is reflected
                        // in the Gaussian shadow map.
                        cmd.DrawMesh(bakedMesh, skinnedRenderer.localToWorldMatrix,
                            _depthMaterial, subMesh, 0);
                    }
                    else
                    {
                        cmd.DrawRenderer(renderer, _depthMaterial, subMesh, 0);
                    }
                }
            }
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var material = _splat.material;
            material.SetTexture("_MmdMeshShadowMap", _map);
            material.SetVector("_MmdShadowRight", rowX);
            material.SetVector("_MmdShadowUp", rowY);
            material.SetVector("_MmdShadowDepth", rowZ);
            material.SetVector("_MmdMeshShadowTexelSize", new Vector4(1f / _map.width, 1f / _map.height, 0f, 0f));
            material.SetFloat("_MmdMeshShadowBias", depthBias);
            material.SetFloat("_MmdMeshShadowEnabled", 1f);
        }

        /// <summary>Applies the optional analytic MMD shadow receiver parameters.</summary>
        public void ApplyMaterialProperties(Material material)
        {
            if (material == null) return;
            if (!receiveDropShadow || ShadowSource == null
                || !ShadowSource.TryGetShadowCasters(out _, out var bounds))
            {
                material.SetFloat("_MmdDropShadowOpacity", 0f);
                material.SetFloat("_MmdMeshShadowEnabled", 0f);
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
                _shadowLight = FindDirectionalLight();

            if (_shadowLight == null) return Vector2.zero;

            Vector3 ray = _shadowLight.transform.forward.normalized;
            float downward = Mathf.Max(0.15f, -ray.y);
            var axis = new Vector2(ray.x, ray.z) * (modelHeight / downward);
            return Vector2.ClampMagnitude(axis, modelHeight * 3f);
        }

        private Mesh GetBakedMesh(SkinnedMeshRenderer renderer)
        {
            if (!_bakedMeshes.TryGetValue(renderer, out var mesh) || mesh == null)
            {
                mesh = new Mesh
                {
                    name = renderer.name + " Gaussian Shadow",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _bakedMeshes[renderer] = mesh;
            }
            renderer.BakeMesh(mesh);
            return mesh;
        }

        private bool EnsureResources()
        {
            int size = Mathf.Clamp(resolution, 256, 2048);
            if (_map == null || _map.width != size)
            {
                ReleaseMap();
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
                    ? RenderTextureFormat.RFloat : RenderTextureFormat.RHalf;
                _map = new RenderTexture(size, size, 24, format)
                {
                    name = "Gaussian MMD Mesh Shadow",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                };
                _map.Create();
            }
            if (_depthMaterial == null)
            {
                var shader = Resources.Load<Shader>("GaussianSplatMeshShadow");
                if (shader != null)
                    _depthMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            return _map != null && _map.IsCreated() && _depthMaterial != null;
        }

        private static Light FindDirectionalLight()
        {
            if (RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional)
                return RenderSettings.sun;
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.isActiveAndEnabled && light.type == LightType.Directional) return light;
            return null;
        }

        private static Vector4 MakeProjectionRow(Vector3 axis, Vector3 origin, float size, float offset)
            => new Vector4(axis.x / size, axis.y / size, axis.z / size,
                -Vector3.Dot(origin, axis) / size + offset);

        private static void ProjectBounds(Bounds b, Vector3 x, Vector3 y, Vector3 z,
                                          out Vector3 minResult, out Vector3 maxResult)
        {
            minResult = Vector3.one * float.MaxValue;
            maxResult = Vector3.one * float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = new Vector3((i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 q = new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z));
                minResult = Vector3.Min(minResult, q);
                maxResult = Vector3.Max(maxResult, q);
            }
        }

        private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(local.center);
            Vector3 extents = local.extents;
            Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            extents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, extents * 2f);
        }

        private void ReleaseMap()
        {
            if (_map == null) return;
            _map.Release(); Destroy(_map); _map = null;
        }

        private void OnDestroy()
        {
            ReleaseMap();
            if (_depthMaterial != null) Destroy(_depthMaterial);
            foreach (var mesh in _bakedMeshes.Values)
                if (mesh != null) Destroy(mesh);
            _bakedMeshes.Clear();
        }
    }
}
