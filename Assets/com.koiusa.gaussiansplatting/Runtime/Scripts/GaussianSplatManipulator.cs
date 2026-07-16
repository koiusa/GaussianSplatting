using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace GaussianSplatting
{
    /// <summary>
    /// タッチ / マウスで Gaussian Splat の TRS を操作するコンポーネント。
    ///
    /// Touch:
    ///   1本指ドラッグ          → 移動 (水平面)
    ///   2本指ピンチ            → スケール
    ///   2本指ひねり            → ヨー (Y回転)
    ///   2本指中点の上下        → ピッチ (X回転)
    ///   2本指中点の左右        → ロール (Z回転)
    ///
    /// Mouse:
    ///   左ドラッグ             → 移動 (カメラ向き平面)
    ///   スクロール             → スケール
    ///   右ドラッグ X           → ヨー (Y回転)
    ///   右ドラッグ Y           → ピッチ (X回転)
    ///   中ボタンドラッグ X     → ロール (Z回転)
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianSplatManipulator : MonoBehaviour
    {
        public enum TranslatePlane { Floor, CameraFacing }

        private bool _editMode;
        public bool EditMode
        {
            get => _editMode;
            set { _editMode = value; if (!value) ResetState(); }
        }

        public TranslatePlane MovePlane       { get; set; } = TranslatePlane.Floor;
        public bool           LockMoveX       { get; set; } = false;
        public bool           LockMoveY       { get; set; } = false;
        public bool           LockMoveZ       { get; set; } = false;
        public bool           LockRotX        { get; set; } = false;
        public bool           LockRotY        { get; set; } = false;
        public bool           LockRotZ        { get; set; } = false;
        public bool           LockScaleX      { get; set; } = false;
        public bool           LockScaleY      { get; set; } = false;
        public bool           LockScaleZ      { get; set; } = false;
        public float          MoveSensitivity { get; set; } = 1f;
        public float          RotSensitivity  { get; set; } = 1f;

        /// <summary>ジョイスティック使用中はタッチ操作を無効にする。</summary>
        public bool           JoystickActive  { get; set; } = false;

        [SerializeField] [Range(0.01f, 1f)]
        private float _touchMoveSensitivity = 0.3f;

        [SerializeField] [Range(0.1f, 20f)]
        private float _joystickMoveSpeed = 5f;

        private GaussianSplatRenderer _renderer;
        private Material              _lineMaterial;

        // ----- touch state -----
        private float   _lastPinchDist;
        private float   _lastTwistAngle;
        private Vector2 _lastMidpoint;
        private bool    _twoFingerInit;

        // ----- mouse state -----
        private Vector2 _lastMousePosL;
        private Vector2 _lastMousePosR;
        private Vector2 _lastMousePosM;
        private bool    _draggingLeft;
        private bool    _draggingRight;
        private bool    _draggingMiddle;

        // ------------------------------------------------------------------ //

        private void Awake()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();

            _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull",     (int)CullMode.Off);
            _lineMaterial.SetInt("_ZWrite",   0);
            _lineMaterial.SetInt("_ZTest",    (int)CompareFunction.LessEqual);
        }

        private void OnDestroy()
        {
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        private void OnEnable()  => EnhancedTouchSupport.Enable();
        private void OnDisable() => EnhancedTouchSupport.Disable();

        // ------------------------------------------------------------------ //

        private void Update()
        {
            if (!_editMode) return;

#if UNITY_EDITOR || UNITY_STANDALONE
            HandleMouse();
#else
            HandleTouch();
#endif
        }

        private void OnRenderObject()
        {
            if (!_editMode || _renderer == null || !_renderer.IsLoaded) return;
            if (Camera.current != Camera.main) return;

            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(transform.localToWorldMatrix);
            DrawWireCube(_renderer.LocalBounds);
            GL.PopMatrix();
        }

        // ================================================================== //
        // Bounding box
        // ================================================================== //

        private static void DrawWireCube(Bounds b)
        {
            Vector3 min = b.min;
            Vector3 max = b.max;

            GL.Begin(GL.LINES);
            GL.Color(new Color(0.2f, 1f, 0.55f, 0.9f));

            Line(min.x, min.y, min.z,  max.x, min.y, min.z);
            Line(max.x, min.y, min.z,  max.x, min.y, max.z);
            Line(max.x, min.y, max.z,  min.x, min.y, max.z);
            Line(min.x, min.y, max.z,  min.x, min.y, min.z);

            Line(min.x, max.y, min.z,  max.x, max.y, min.z);
            Line(max.x, max.y, min.z,  max.x, max.y, max.z);
            Line(max.x, max.y, max.z,  min.x, max.y, max.z);
            Line(min.x, max.y, max.z,  min.x, max.y, min.z);

            Line(min.x, min.y, min.z,  min.x, max.y, min.z);
            Line(max.x, min.y, min.z,  max.x, max.y, min.z);
            Line(max.x, min.y, max.z,  max.x, max.y, max.z);
            Line(min.x, min.y, max.z,  min.x, max.y, max.z);

            GL.End();
        }

        private static void Line(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            GL.Vertex3(x0, y0, z0);
            GL.Vertex3(x1, y1, z1);
        }

        // ================================================================== //
        // Touch
        // ================================================================== //

        private void HandleTouch()
        {
            if (JoystickActive) return;

            var touches = Touch.activeTouches;
            int count   = touches.Count;

            if (count == 1)
            {
                _twoFingerInit = false;
                TranslateWithScreenDelta(touches[0].screenPosition, touches[0].delta * _touchMoveSensitivity);
            }
            else if (count >= 2)
            {
                HandleTwoFingerTouch(touches[0], touches[1]);
            }
            else
            {
                _twoFingerInit = false;
            }
        }

        private void HandleTwoFingerTouch(Touch t0, Touch t1)
        {
            float   dist  = Vector2.Distance(t0.screenPosition, t1.screenPosition);
            float   angle = Mathf.Atan2(
                t1.screenPosition.y - t0.screenPosition.y,
                t1.screenPosition.x - t0.screenPosition.x) * Mathf.Rad2Deg;
            Vector2 mid   = (t0.screenPosition + t1.screenPosition) * 0.5f;

            if (!_twoFingerInit || t1.phase == UnityEngine.InputSystem.TouchPhase.Began)
            {
                _lastPinchDist  = dist;
                _lastTwistAngle = angle;
                _lastMidpoint   = mid;
                _twoFingerInit  = true;
                return;
            }

            // スケール
            ApplyScale(dist, _lastPinchDist);

            // ヨー (Z軸まわり: ひねり)
            ApplyWorldRotation(0f, Mathf.DeltaAngle(_lastTwistAngle, angle), 0f);

            // ピッチ・ロール (中点の移動量を画面の高さで正規化)
            Vector2 midDelta = mid - _lastMidpoint;
            float   sens     = 180f / Screen.height;
            ApplyWorldRotation( midDelta.y * sens,   // ピッチ (X): 上→前傾
                                0f,
                               -midDelta.x * sens);  // ロール (Z): 右→右傾

            _lastPinchDist  = dist;
            _lastTwistAngle = angle;
            _lastMidpoint   = mid;
        }

        // ================================================================== //
        // Mouse
        // ================================================================== //

        private void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Vector2 pos = mouse.position.ReadValue();

            // スクロール → スケール
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                ApplyScale(1f + Mathf.Sign(scroll) * 0.05f);

            // 左ドラッグ → 移動
            if (mouse.leftButton.wasPressedThisFrame)  { _lastMousePosL = pos; _draggingLeft   = true;  }
            if (mouse.leftButton.isPressed && _draggingLeft)
            {
                TranslateWithScreenDelta(pos, pos - _lastMousePosL);
                _lastMousePosL = pos;
            }
            if (mouse.leftButton.wasReleasedThisFrame) _draggingLeft  = false;

            // 右ドラッグ X→ヨー, Y→ピッチ
            if (mouse.rightButton.wasPressedThisFrame)  { _lastMousePosR = pos; _draggingRight  = true;  }
            if (mouse.rightButton.isPressed && _draggingRight)
            {
                Vector2 d = pos - _lastMousePosR;
                ApplyWorldRotation(d.y * 0.4f, -d.x * 0.4f, 0f);
                _lastMousePosR = pos;
            }
            if (mouse.rightButton.wasReleasedThisFrame) _draggingRight = false;

            // 中ボタンドラッグ X → ロール
            if (mouse.middleButton.wasPressedThisFrame)  { _lastMousePosM = pos; _draggingMiddle = true;  }
            if (mouse.middleButton.isPressed && _draggingMiddle)
            {
                float dx = pos.x - _lastMousePosM.x;
                ApplyWorldRotation(0f, 0f, -dx * 0.4f);
                _lastMousePosM = pos;
            }
            if (mouse.middleButton.wasReleasedThisFrame) _draggingMiddle = false;
        }

        // ================================================================== //
        // 公開 Transform API（ジョイスティック等の外部入力から呼ぶ）
        // ================================================================== //

        /// <summary>ワールド空間デルタ移動を軸制約付きで適用する。</summary>
        public void ApplyMove(Vector3 worldDelta)
        {
            if (LockMoveX) worldDelta.x = 0f;
            if (LockMoveY) worldDelta.y = 0f;
            if (LockMoveZ) worldDelta.z = 0f;
            transform.position += worldDelta;
        }

        /// <summary>ワールド空間回転を軸制約付きで適用する。感度は呼び出し元で乗算済みであること。</summary>
        public void ApplyRotate(float rx, float ry, float rz)
        {
            if (LockRotX) rx = 0f;
            if (LockRotY) ry = 0f;
            if (LockRotZ) rz = 0f;
            if (Mathf.Abs(rx) > 0.01f || Mathf.Abs(ry) > 0.01f || Mathf.Abs(rz) > 0.01f)
                transform.Rotate(rx, ry, rz, Space.World);
        }

        /// <summary>均一スケール係数（1より大きい = 拡大）を軸制約付きで適用する。</summary>
        public void ApplyScale(float factor)
        {
            factor = Mathf.Max(0.001f, factor);
            var ls = transform.localScale;
            if (!LockScaleX) ls.x = Mathf.Max(0.001f, ls.x * factor);
            if (!LockScaleY) ls.y = Mathf.Max(0.001f, ls.y * factor);
            if (!LockScaleZ) ls.z = Mathf.Max(0.001f, ls.z * factor);
            transform.localScale = ls;
        }

        // ================================================================== //
        // 共通ヘルパー
        // ================================================================== //

        /// <summary>
        /// スクリーン入力（ドラッグ）による移動。常にカメラ向き平面に投影する。
        /// </summary>
        private void TranslateWithScreenDelta(Vector2 currentScreen, Vector2 delta)
        {
            if (delta.sqrMagnitude < 0.01f) return;
            var cam = Camera.main;
            if (cam == null) return;

            var plane = new Plane(-cam.transform.forward, transform.position);

            if (ScreenToPlane(cam, currentScreen - delta, plane, out Vector3 wp0) &&
                ScreenToPlane(cam, currentScreen,         plane, out Vector3 wp1))
            {
                var move = (wp1 - wp0) * MoveSensitivity;
                if (LockMoveX) move.x = 0f;
                if (LockMoveY) move.y = 0f;
                if (LockMoveZ) move.z = 0f;
                transform.position += move;
            }
        }

        /// <summary>
        /// ジョイスティック入力による移動。カメラ水平方向基準で地面 (XZ) を移動する。
        /// input は正規化済みスティック入力 (x=右, y=前)。
        /// </summary>
        public void MoveOnFloor(Vector2 input, float deltaTime, float? speed = null)
        {
            if (!_editMode || input.sqrMagnitude < 0.001f) return;

            var cam = Camera.main;
            if (cam == null) return;

            Vector3 forward = cam.transform.forward; forward.y = 0f; forward.Normalize();
            Vector3 right   = cam.transform.right;   right.y   = 0f; right.Normalize();

            float s  = (speed ?? _joystickMoveSpeed) * MoveSensitivity;
            var move = (right * input.x + forward * input.y) * (s * deltaTime);
            if (LockMoveX) move.x = 0f;
            if (LockMoveZ) move.z = 0f;
            transform.position += move;
        }

        private void ApplyScale(float current, float last)
        {
            if (last <= 1f || current <= 1f) return;
            float s  = current / last;
            var   ls = transform.localScale;
            if (!LockScaleX) ls.x = Mathf.Max(0.001f, ls.x * s);
            if (!LockScaleY) ls.y = Mathf.Max(0.001f, ls.y * s);
            if (!LockScaleZ) ls.z = Mathf.Max(0.001f, ls.z * s);
            transform.localScale = ls;
        }

        private void ApplyWorldRotation(float x, float y, float z)
        {
            if (LockRotX) x = 0f;
            if (LockRotY) y = 0f;
            if (LockRotZ) z = 0f;
            x *= RotSensitivity; y *= RotSensitivity; z *= RotSensitivity;
            if (Mathf.Abs(x) > 0.01f || Mathf.Abs(y) > 0.01f || Mathf.Abs(z) > 0.01f)
                transform.Rotate(x, y, z, Space.World);
        }

        private void ResetState()
        {
            _twoFingerInit  = false;
            _draggingLeft   = false;
            _draggingRight  = false;
            _draggingMiddle = false;
        }

        private static bool ScreenToPlane(Camera cam, Vector2 screen, Plane plane, out Vector3 world)
        {
            var ray = cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            if (plane.Raycast(ray, out float enter))
            {
                world = ray.GetPoint(enter);
                return true;
            }
            world = Vector3.zero;
            return false;
        }
    }
}
