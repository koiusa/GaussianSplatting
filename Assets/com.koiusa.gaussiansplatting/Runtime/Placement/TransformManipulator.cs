using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Koiusa.Placement
{
    /// <summary>任意のTransformに対する配置調整とマウス／タッチ入力の共通実装。</summary>
    public class TransformManipulator : MonoBehaviour, IPlacementManipulator
    {
        public enum TranslatePlane { Floor, CameraFacing }

        private bool _editMode;
        public bool EditMode
        {
            get => _editMode;
            set { _editMode = value; if (!value) ResetInputState(); }
        }

        public bool JoystickActive { get; set; }
        public bool LockMoveX { get; set; }
        public bool LockMoveY { get; set; }
        public bool LockMoveZ { get; set; }
        public bool LockRotX { get; set; }
        public bool LockRotY { get; set; }
        public bool LockRotZ { get; set; }
        public bool LockScaleX { get; set; }
        public bool LockScaleY { get; set; }
        public bool LockScaleZ { get; set; }
        public float MoveSensitivity { get; set; } = 1f;
        public float RotSensitivity { get; set; } = 1f;
        public Transform TargetTransform => transform;
        public TranslatePlane MovePlane { get; set; } = TranslatePlane.Floor;

        [SerializeField, Range(0.01f, 1f)] private float _touchMoveSensitivity = 0.3f;
        [SerializeField, Range(0.1f, 20f)] private float _joystickMoveSpeed = 5f;

        private float _lastPinchDistance;
        private float _lastTwistAngle;
        private Vector2 _lastMidpoint;
        private bool _twoFingerInitialized;
        private Vector2 _lastLeftMouse;
        private Vector2 _lastRightMouse;
        private Vector2 _lastMiddleMouse;
        private bool _draggingLeft;
        private bool _draggingRight;
        private bool _draggingMiddle;

        protected virtual void OnEnable() => EnhancedTouchSupport.Enable();
        protected virtual void OnDisable() => EnhancedTouchSupport.Disable();

        protected virtual void Update()
        {
            if (!EditMode) return;
#if UNITY_EDITOR || UNITY_STANDALONE
            HandleMouse();
#else
            HandleTouch();
#endif
        }

        public void ApplyMove(Vector3 delta)
        {
            if (LockMoveX) delta.x = 0f;
            if (LockMoveY) delta.y = 0f;
            if (LockMoveZ) delta.z = 0f;
            transform.position += delta;
        }

        public void ApplyRotate(float x, float y, float z)
        {
            if (LockRotX) x = 0f;
            if (LockRotY) y = 0f;
            if (LockRotZ) z = 0f;
            if (Mathf.Abs(x) > 0.01f || Mathf.Abs(y) > 0.01f || Mathf.Abs(z) > 0.01f)
                transform.Rotate(x, y, z, Space.World);
        }

        public void ApplyScale(float factor)
        {
            factor = Mathf.Max(0.001f, factor);
            var scale = transform.localScale;
            if (!LockScaleX) scale.x = Mathf.Max(0.001f, scale.x * factor);
            if (!LockScaleY) scale.y = Mathf.Max(0.001f, scale.y * factor);
            if (!LockScaleZ) scale.z = Mathf.Max(0.001f, scale.z * factor);
            transform.localScale = scale;
        }

        public void ResetTransform()
        {
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        /// <summary>カメラの水平方向を基準に、正規化済み入力でXZ平面を移動する。</summary>
        public void MoveOnFloor(Vector2 input, float deltaTime, float? speed = null)
        {
            if (!EditMode || input.sqrMagnitude < 0.001f) return;
            var camera = Camera.main;
            if (camera == null) return;
            Vector3 forward = camera.transform.forward;
            Vector3 right = camera.transform.right;
            forward.y = 0f;
            right.y = 0f;
            if (forward.sqrMagnitude > 0.001f) forward.Normalize();
            if (right.sqrMagnitude > 0.001f) right.Normalize();
            ApplyMove((right * input.x + forward * input.y) *
                      ((speed ?? _joystickMoveSpeed) * MoveSensitivity * deltaTime));
        }

        private void HandleTouch()
        {
            if (JoystickActive) return;
            var touches = Touch.activeTouches;
            if (touches.Count == 1)
            {
                _twoFingerInitialized = false;
                TranslateWithScreenDelta(touches[0].screenPosition, touches[0].delta * _touchMoveSensitivity);
            }
            else if (touches.Count >= 2)
            {
                HandleTwoFinger(touches[0], touches[1]);
            }
            else _twoFingerInitialized = false;
        }

        private void HandleTwoFinger(Touch first, Touch second)
        {
            float distance = Vector2.Distance(first.screenPosition, second.screenPosition);
            float angle = Mathf.Atan2(second.screenPosition.y - first.screenPosition.y,
                second.screenPosition.x - first.screenPosition.x) * Mathf.Rad2Deg;
            Vector2 midpoint = (first.screenPosition + second.screenPosition) * 0.5f;
            if (!_twoFingerInitialized || second.phase == UnityEngine.InputSystem.TouchPhase.Began)
            {
                _lastPinchDistance = distance;
                _lastTwistAngle = angle;
                _lastMidpoint = midpoint;
                _twoFingerInitialized = true;
                return;
            }

            if (_lastPinchDistance > 1f && distance > 1f) ApplyScale(distance / _lastPinchDistance);
            ApplySensitiveRotation(0f, Mathf.DeltaAngle(_lastTwistAngle, angle), 0f);
            Vector2 delta = midpoint - _lastMidpoint;
            float sensitivity = 180f / Screen.height;
            ApplySensitiveRotation(delta.y * sensitivity, 0f, -delta.x * sensitivity);
            _lastPinchDistance = distance;
            _lastTwistAngle = angle;
            _lastMidpoint = midpoint;
        }

        private void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 position = mouse.position.ReadValue();
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) ApplyScale(1f + Mathf.Sign(scroll) * 0.05f);

            if (mouse.leftButton.wasPressedThisFrame) { _lastLeftMouse = position; _draggingLeft = true; }
            if (mouse.leftButton.isPressed && _draggingLeft)
            {
                TranslateWithScreenDelta(position, position - _lastLeftMouse);
                _lastLeftMouse = position;
            }
            if (mouse.leftButton.wasReleasedThisFrame) _draggingLeft = false;

            if (mouse.rightButton.wasPressedThisFrame) { _lastRightMouse = position; _draggingRight = true; }
            if (mouse.rightButton.isPressed && _draggingRight)
            {
                Vector2 delta = position - _lastRightMouse;
                ApplySensitiveRotation(delta.y * 0.4f, -delta.x * 0.4f, 0f);
                _lastRightMouse = position;
            }
            if (mouse.rightButton.wasReleasedThisFrame) _draggingRight = false;

            if (mouse.middleButton.wasPressedThisFrame) { _lastMiddleMouse = position; _draggingMiddle = true; }
            if (mouse.middleButton.isPressed && _draggingMiddle)
            {
                ApplySensitiveRotation(0f, 0f, -(position.x - _lastMiddleMouse.x) * 0.4f);
                _lastMiddleMouse = position;
            }
            if (mouse.middleButton.wasReleasedThisFrame) _draggingMiddle = false;
        }

        private void TranslateWithScreenDelta(Vector2 current, Vector2 delta)
        {
            if (delta.sqrMagnitude < 0.01f) return;
            var camera = Camera.main;
            if (camera == null) return;
            var plane = new Plane(-camera.transform.forward, transform.position);
            if (ScreenToPlane(camera, current - delta, plane, out var from) &&
                ScreenToPlane(camera, current, plane, out var to))
                ApplyMove((to - from) * MoveSensitivity);
        }

        private void ApplySensitiveRotation(float x, float y, float z) =>
            ApplyRotate(x * RotSensitivity, y * RotSensitivity, z * RotSensitivity);

        private void ResetInputState()
        {
            _twoFingerInitialized = false;
            _draggingLeft = false;
            _draggingRight = false;
            _draggingMiddle = false;
        }

        private static bool ScreenToPlane(Camera camera, Vector2 screen, Plane plane, out Vector3 world)
        {
            var ray = camera.ScreenPointToRay(screen);
            if (plane.Raycast(ray, out float distance))
            {
                world = ray.GetPoint(distance);
                return true;
            }
            world = default;
            return false;
        }
    }
}
