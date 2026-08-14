// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// For changing entity material color.

using UnityEngine;

namespace AsteriskSoft.LineOfSight.Demo
{
    [RequireComponent(typeof(Renderer))]
    [RequireComponent(typeof(LineOfSightVisibilityTracker))]
    public class SetColorOnVisible : MonoBehaviour
    {
        public Color visibleColor = Color.green;
        public Color invisibleColor = Color.red;

        private MaterialPropertyBlock m_PropertyBlock;
        private Renderer m_Renderer;
        private LineOfSightVisibilityTracker m_LosTracker;
        
        
        void OnEnable()
        {
            m_Renderer  = GetComponent<Renderer>();
            m_LosTracker = GetComponent<LineOfSightVisibilityTracker>();
            m_PropertyBlock = new MaterialPropertyBlock();
            m_LosTracker.VisibilityChanged += OnVisibilityChanged;
            OnVisibilityChanged(m_LosTracker.IsVisible);
        }

        void OnDisable()
        {
            m_LosTracker.VisibilityChanged -= OnVisibilityChanged;
        }
        
        void OnVisibilityChanged(bool visible)
        {
            if(visible) SetColor(visibleColor);
            else SetColor(invisibleColor);
        }
        
        void SetColor(Color color)
        {
            m_Renderer.GetPropertyBlock(m_PropertyBlock);
            m_PropertyBlock.SetColor("_BaseColor", color);
            m_Renderer.SetPropertyBlock(m_PropertyBlock);
        }
        
    }
}
