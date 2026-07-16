using UnityEngine;

namespace GaussianSplatting.Samples
{
    /// <summary>Creates a small splat grid so the renderer can be verified without an external PLY file.</summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class MinimalGaussianSplatSample : MonoBehaviour
    {
        [SerializeField, Range(2, 32)] private int gridSize = 12;
        [SerializeField, Min(0.01f)] private float spacing = 0.18f;
        [SerializeField, Min(0.001f)] private float splatScale = 0.075f;

        private void Start()
        {
            var renderer = GetComponent<GaussianSplatRenderer>();
            var splats = new GaussianSplatGPU[gridSize * gridSize];
            float offset = (gridSize - 1) * spacing * 0.5f;

            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    float u = gridSize > 1 ? x / (gridSize - 1f) : 0f;
                    float v = gridSize > 1 ? y / (gridSize - 1f) : 0f;
                    splats[y * gridSize + x] = new GaussianSplatGPU
                    {
                        position = new Vector3(x * spacing - offset, y * spacing - offset, 0f),
                        opacity = 0.95f,
                        rotation = new Vector4(1f, 0f, 0f, 0f),
                        scale = new Vector3(splatScale, splatScale, splatScale * 0.35f),
                        color = new Vector3(u, 0.35f + 0.5f * v, 1f - u * 0.7f)
                    };
                }
            }

            renderer.LoadSplats(splats, "Procedural Minimal Sample");
        }
    }
}
