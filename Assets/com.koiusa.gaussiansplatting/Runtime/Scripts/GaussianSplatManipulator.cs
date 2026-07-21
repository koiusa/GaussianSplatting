using UnityEngine;
using UnityEngine.Rendering;
using Koiusa.Placement;

namespace GaussianSplatting
{
    /// <summary>共通Transform操作にGaussian Splat固有のBounds表示を加えるManipulator。</summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class GaussianSplatManipulator : TransformManipulator
    {
        private const int EdgeCount = 12;
        private static readonly int[] EdgeIndices =
        {
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };

        private GaussianSplatRenderer _renderer;
        private Material _lineMaterial;
        private LineRenderer[] _edges;

        private void Awake()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;

            _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMaterial.SetColor("_Color", new Color(0.2f, 1f, 0.55f, 0.9f));
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _lineMaterial.renderQueue = 3001;

            _edges = new LineRenderer[EdgeCount];
            for (int i = 0; i < EdgeCount; i++)
            {
                var edgeObject = new GameObject("BoundsEdge");
                edgeObject.hideFlags = HideFlags.HideAndDontSave;
                edgeObject.transform.SetParent(transform, false);

                var edge = edgeObject.AddComponent<LineRenderer>();
                edge.useWorldSpace = false;
                edge.positionCount = 2;
                edge.sharedMaterial = _lineMaterial;
                edge.numCapVertices = 0;
                edge.shadowCastingMode = ShadowCastingMode.Off;
                edge.receiveShadows = false;
                edge.lightProbeUsage = LightProbeUsage.Off;
                edge.reflectionProbeUsage = ReflectionProbeUsage.Off;
                edge.enabled = false;
                _edges[i] = edge;
            }
        }

        private void LateUpdate()
        {
            bool visible = EditMode && _renderer != null && _renderer.IsLoaded;
            SetBoundsVisible(visible);
            if (!visible || _edges == null) return;

            Bounds bounds = _renderer.LocalBounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            var corners = new[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(min.x, max.y, max.z)
            };

            float width = Mathf.Max(bounds.size.magnitude * 0.0015f, 0.001f);
            for (int i = 0; i < EdgeCount; i++)
            {
                LineRenderer edge = _edges[i];
                edge.startWidth = width;
                edge.endWidth = width;
                edge.SetPosition(0, corners[EdgeIndices[i * 2]]);
                edge.SetPosition(1, corners[EdgeIndices[i * 2 + 1]]);
            }
        }

        private void SetBoundsVisible(bool visible)
        {
            if (_edges == null) return;
            foreach (LineRenderer edge in _edges)
                if (edge != null) edge.enabled = visible;
        }

        private void OnDestroy()
        {
            if (_edges != null)
                foreach (LineRenderer edge in _edges)
                    if (edge != null) Destroy(edge.gameObject);
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }
    }
}
