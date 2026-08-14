// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Simple isometric character controller for the demo scene. Moves relative
// to the camera and aims the vision cone at the mouse cursor. Supports both
// the legacy Input Manager and the Input System package.

using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AsteriskSoft.LineOfSight.Demo
{
    [RequireComponent(typeof(CharacterController))]
    [AddComponentMenu("Line Of Sight/Demo/Isometric Character Controller")]
    public class IsometricCharacterController : MonoBehaviour
    {
        [Tooltip("Movement speed in units per second.")]
        public float moveSpeed = 5f;

        [Tooltip("Rotate the character to face the mouse cursor and drive the vision cone with it. If disabled, the character faces its movement direction.")]
        public bool mouseAim = true;

        [Tooltip("Camera used for movement direction and mouse aim. Defaults to Camera.main.")]
        public Camera viewCamera;

        CharacterController m_Controller;
        LineOfSightAgent m_Agent;
        float m_VerticalVelocity;

        void Awake()
        {
            m_Controller = GetComponent<CharacterController>();
            m_Agent = GetComponent<LineOfSightAgent>();
        }

        void Update()
        {
            Camera cam = viewCamera != null ? viewCamera : Camera.main;

            Vector2 input = ReadMove();
            Vector3 move = Vector3.zero;
            if (cam != null && input.sqrMagnitude > 0.0001f)
            {
                Vector3 fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
                Vector3 right = cam.transform.right; right.y = 0f; right.Normalize();
                move = Vector3.ClampMagnitude(fwd * input.y + right * input.x, 1f) * moveSpeed;
            }

            m_VerticalVelocity = m_Controller.isGrounded ? -1f : m_VerticalVelocity + Physics.gravity.y * Time.deltaTime;
            move.y = m_VerticalVelocity;
            m_Controller.Move(move * Time.deltaTime);

            if (mouseAim && cam != null)
            {
                AimAtMouse(cam);
            }
            else
            {
                Vector3 flat = move; flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
            }
        }

        void AimAtMouse(Camera cam)
        {
            Ray ray = cam.ScreenPointToRay(ReadMousePosition());
            var plane = new Plane(Vector3.up, transform.position);
            if (!plane.Raycast(ray, out float enter))
                return;

            Vector3 point = ray.GetPoint(enter);
            Vector3 dir = point - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
                return;

            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            if (m_Agent != null)
                m_Agent.LookAtPoint(point);
        }

        static Vector2 ReadMove()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null)
                return Vector2.zero;
            float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
            float y = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
#else
            return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
        }

        static Vector3 ReadMousePosition()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null ? (Vector3)m.position.ReadValue() : Vector3.zero;
#else
            return Input.mousePosition;
#endif
        }
    }
}
