// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Smoothed isometric follow camera for the demo scene.

using UnityEngine;

namespace AsteriskSoft.LineOfSight.Demo
{
    [AddComponentMenu("Line Of Sight/Demo/Isometric Camera Follow")]
    public class IsometricCameraFollow : MonoBehaviour
    {
        [Tooltip("Transform to follow (the player).")]
        public Transform target;

        [Tooltip("World space offset from the target.")]
        public Vector3 offset = new Vector3(-8f, 11f, -8f);

        [Tooltip("Position smoothing time in seconds.")]
        public float smoothTime = 0.15f;

        Vector3 m_Velocity;

        void LateUpdate()
        {
            if (target == null)
                return;

            Vector3 desired = target.position + offset;
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref m_Velocity, smoothTime);
            transform.rotation = Quaternion.LookRotation(
                (target.position + Vector3.up) - transform.position, Vector3.up);
        }
    }
}
