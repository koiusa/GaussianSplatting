using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Optional Off-Axis view adapter for <see cref="GaussianSplatRenderer"/>.
    /// Add this component only when another system overrides the camera view/projection matrices.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public sealed class GaussianSplatOffAxisController : MonoBehaviour
    {
        [SerializeField, Min(0f), Tooltip("眼位置移動に備えて視錐台を広げる追加マージン（ワールド単位）")]
        private float cullSafetyMargin = 0.1f;
        [Header("Update Throttling")]
        [SerializeField, Min(0.1f)] private float maxCullUpdatesPerSecond = 5f;
        [SerializeField, Min(0f)] private float cullPositionThreshold = 0.1f;
        [SerializeField, Range(0f, 30f)] private float cullAngleThreshold = 2f;
        [SerializeField, Min(0.1f)] private float maxSortUpdatesPerSecond = 12f;
        [SerializeField, Min(0f)] private float sortPositionThreshold = 0.03f;
        [SerializeField, Range(0f, 30f)] private float sortAngleThreshold = 0.75f;

        public float CullSafetyMargin => cullSafetyMargin;
        public float MaxCullUpdatesPerSecond => maxCullUpdatesPerSecond;
        public float CullPositionThreshold => cullPositionThreshold;
        public float CullAngleThreshold => cullAngleThreshold;
        public float MaxSortUpdatesPerSecond => maxSortUpdatesPerSecond;
        public float SortPositionThreshold => sortPositionThreshold;
        public float SortAngleThreshold => sortAngleThreshold;

        public void GetView(Camera camera, out Vector3 eyeWorld, out Vector3 forwardWorld)
        {
            Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
            eyeWorld = cameraToWorld.MultiplyPoint3x4(Vector3.zero);
            forwardWorld = -(Vector3)cameraToWorld.GetColumn(2);
            forwardWorld.Normalize();
        }
    }
}
