// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Player vision component. Attach to the character; the render feature
// discovers the most recently enabled agent automatically. Holds per character
// vision parameters (cone, distance, proximity) and the facing API
// (SetViewDirection / LookAtPoint).

using System.Collections.Generic;
using UnityEngine;

namespace AsteriskSoft.LineOfSight
{
    [AddComponentMenu("Rendering/Line Of Sight Agent")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class LineOfSightAgent : MonoBehaviour
    {
        static readonly List<LineOfSightAgent> s_Agents = new List<LineOfSightAgent>();

        /// <summary>The agent the render feature currently uses (most recently enabled).</summary>
        public static LineOfSightAgent Active => s_Agents.Count > 0 ? s_Agents[s_Agents.Count - 1] : null;

        /// <summary>All enabled agents, in registration order.</summary>
        public static IReadOnlyList<LineOfSightAgent> All => s_Agents;

        public enum FacingSource
        {
            TransformForward,
            CustomDirection
        }

        [Header("Vision Cone")]
        [Tooltip("Full field-of-view angle in degrees. 360 = omnidirectional vision.")]
        [Range(1f, 360f)] public float viewAngle = 120f;

        [Tooltip("Maximum vision distance in world units.")]
        [Min(0.1f)] public float viewDistance = 16f;

        [Tooltip("Angular falloff at the cone edges, in degrees.")]
        [Range(0f, 45f)] public float angleFeather = 8f;

        [Tooltip("Distance falloff at the far edge, as a fraction of View Distance.")]
        [Range(0f, 1f)] public float rangeFeather = 0.2f;

        [Header("Proximity (visible in every direction)")]
        [Tooltip("Radius around the character that is always visible, regardless of facing.")]
        [Min(0f)] public float proximityRadius = 3f;

        [Tooltip("Falloff width at the edge of the proximity circle.")]
        [Min(0f)] public float proximityFeather = 1.5f;

        [Header("Setup")]
        [Tooltip("Vertical offset from the transform position used as the eye position (also anchors the occluder capture height band).")]
        public float eyeHeight = 1.5f;

        public FacingSource facingSource = FacingSource.TransformForward;

        [Tooltip("Used when Facing Source is Custom Direction. Set from code via SetViewDirection() / LookAtPoint().")]
        public Vector3 customDirection = Vector3.forward;

        public Vector3 EyePosition => transform.position + Vector3.up * eyeHeight;

        public Vector3 ViewDirection
        {
            get
            {
                Vector3 d = facingSource == FacingSource.CustomDirection ? customDirection : transform.forward;
                d.y = 0f;
                return d.sqrMagnitude < 1e-6f ? Vector3.forward : d.normalized;
            }
        }

        /// <summary>Point the vision cone along a world direction (e.g. towards the mouse cursor).</summary>
        public void SetViewDirection(Vector3 worldDirection)
        {
            facingSource = FacingSource.CustomDirection;
            customDirection = worldDirection;
        }

        /// <summary>Point the vision cone at a world position.</summary>
        public void LookAtPoint(Vector3 worldPosition) => SetViewDirection(worldPosition - EyePosition);

        void OnEnable()
        {
            if (!s_Agents.Contains(this))
                s_Agents.Add(this);
        }

        void OnDisable()
        {
            s_Agents.Remove(this);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Vector3 pos = EyePosition;
            Vector3 dir = ViewDirection;

            var cone = new Color(1f, 0.85f, 0.25f, 0.9f);
            var prox = new Color(0.3f, 0.9f, 1f, 0.9f);

            UnityEditor.Handles.color = cone;
            float half = Mathf.Min(viewAngle * 0.5f, 180f);
            Vector3 from = Quaternion.AngleAxis(-half, Vector3.up) * dir;
            UnityEditor.Handles.DrawWireArc(pos, Vector3.up, from, viewAngle, viewDistance);
            if (viewAngle < 359.5f)
            {
                UnityEditor.Handles.DrawLine(pos, pos + from * viewDistance);
                UnityEditor.Handles.DrawLine(pos, pos + Quaternion.AngleAxis(half, Vector3.up) * dir * viewDistance);
            }

            UnityEditor.Handles.color = prox;
            UnityEditor.Handles.DrawWireDisc(pos, Vector3.up, proximityRadius);
        }
#endif
    }
}
