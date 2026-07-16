using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>Supplies optional world-space caster bounds without coupling the renderer to a model library.</summary>
    public interface IGaussianSplatShadowSource
    {
        bool TryGetShadowCasters(out Renderer[] renderers, out Bounds bounds);
    }
}
