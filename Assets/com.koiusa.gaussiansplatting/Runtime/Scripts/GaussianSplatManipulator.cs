using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>共通Transform操作にGaussian Splat固有のBounds表示を加えるManipulator。</summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class GaussianSplatManipulator : TransformManipulator
    {
        private GaussianSplatRenderer _renderer;
        private Material _lineMaterial;

        private void Awake()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        private void OnDestroy()
        {
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        private void OnRenderObject()
        {
            if (!EditMode || _renderer == null || !_renderer.IsLoaded || Camera.current != Camera.main) return;
            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(transform.localToWorldMatrix);
            DrawWireCube(_renderer.LocalBounds);
            GL.PopMatrix();
        }

        private static void DrawWireCube(Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            GL.Begin(GL.LINES);
            GL.Color(new Color(0.2f, 1f, 0.55f, 0.9f));
            Line(min.x, min.y, min.z, max.x, min.y, min.z);
            Line(max.x, min.y, min.z, max.x, min.y, max.z);
            Line(max.x, min.y, max.z, min.x, min.y, max.z);
            Line(min.x, min.y, max.z, min.x, min.y, min.z);
            Line(min.x, max.y, min.z, max.x, max.y, min.z);
            Line(max.x, max.y, min.z, max.x, max.y, max.z);
            Line(max.x, max.y, max.z, min.x, max.y, max.z);
            Line(min.x, max.y, max.z, min.x, max.y, min.z);
            Line(min.x, min.y, min.z, min.x, max.y, min.z);
            Line(max.x, min.y, min.z, max.x, max.y, min.z);
            Line(max.x, min.y, max.z, max.x, max.y, max.z);
            Line(min.x, min.y, max.z, min.x, max.y, max.z);
            GL.End();
        }

        private static void Line(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            GL.Vertex3(x0, y0, z0);
            GL.Vertex3(x1, y1, z1);
        }
    }
}
