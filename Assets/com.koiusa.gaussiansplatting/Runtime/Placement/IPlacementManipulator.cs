using UnityEngine;

namespace Koiusa.Placement
{
    /// <summary>移動・回転・拡縮を行う配置調整対象の共通API。</summary>
    public interface IPlacementManipulator
    {
        bool EditMode { get; set; }
        bool JoystickActive { get; set; }
        bool LockMoveX { get; set; }
        bool LockMoveY { get; set; }
        bool LockMoveZ { get; set; }
        bool LockRotX { get; set; }
        bool LockRotY { get; set; }
        bool LockRotZ { get; set; }
        bool LockScaleX { get; set; }
        bool LockScaleY { get; set; }
        bool LockScaleZ { get; set; }
        float MoveSensitivity { get; set; }
        float RotSensitivity { get; set; }
        Transform TargetTransform { get; }

        void ApplyMove(Vector3 worldDelta);
        void ApplyRotate(float x, float y, float z);
        void ApplyScale(float factor);
        void ResetTransform();
    }
}
