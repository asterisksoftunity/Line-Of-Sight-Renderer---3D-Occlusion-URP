// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Per object visibility events driven by LineOfSightQuery. Exposes IsVisible,
// CurrentVisibility, a C# VisibilityChanged event, UnityEvents, hysteresis
// thresholds and an optional on screen debug label.

using System;
using UnityEngine;
using UnityEngine.Events;

namespace AsteriskSoft.LineOfSight
{
    [AddComponentMenu("Rendering/Line Of Sight Visibility Tracker")]
    public class LineOfSightVisibilityTracker : MonoBehaviour
    {
        [Tooltip("Local-space offset of the point tested for visibility (e.g. chest height).")]
        public Vector3 sampleOffset = new Vector3(0f, 0.5f, 0f);

        [Tooltip("Visibility above this switches the object to VISIBLE.")]
        [Range(0f, 1f)] public float enterThreshold = 0.5f;

        [Tooltip("Visibility below this switches the object to HIDDEN. Keep below Enter Threshold for hysteresis (prevents flickering at edges).")]
        [Range(0f, 1f)] public float exitThreshold = 0.35f;

        [Tooltip("Draw a debug label above the object showing its visibility state.")]
        public bool debugLabel = true;

        public bool IsVisible { get; private set; }
        public float CurrentVisibility { get; private set; }

        /// <summary>Fired with the new state whenever visibility flips.</summary>
        public event Action<bool> VisibilityChanged;

        public UnityEvent onBecameVisible;
        public UnityEvent onBecameInvisible;

        public Vector3 SamplePoint => transform.TransformPoint(sampleOffset);

        void OnEnable()
        {
            LineOfSightQuery.AddConsumer(); // readbacks only run while consumers exist
        }

        void OnDisable()
        {
            LineOfSightQuery.RemoveConsumer();
        }

        void Update()
        {
            if (!LineOfSightQuery.TryComputeVisibility(SamplePoint, out float vis))
                return; // no readback data yet

            CurrentVisibility = vis;

            if (!IsVisible && vis >= enterThreshold)
                SetVisible(true);
            else if (IsVisible && vis <= exitThreshold)
                SetVisible(false);
        }

        void SetVisible(bool visible)
        {
            IsVisible = visible;
            VisibilityChanged?.Invoke(visible);
            if (visible)
                onBecameVisible?.Invoke();
            else
                onBecameInvisible?.Invoke();
        }

        void OnGUI()
        {
            if (!debugLabel)
                return;
            var cam = Camera.main;
            if (cam == null)
                return;

            Vector3 sp = cam.WorldToScreenPoint(SamplePoint + Vector3.up * 0.6f);
            if (sp.z <= 0f)
                return;

            Color prev = GUI.color;
            GUI.color = IsVisible ? Color.green : Color.red;
            string text = (IsVisible ? "VISIBLE " : "HIDDEN ") + CurrentVisibility.ToString("0.00");
            GUI.Label(new Rect(sp.x - 50f, Screen.height - sp.y - 11f, 140f, 22f), text);
            GUI.color = prev;
        }
    }
}
