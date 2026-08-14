// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.

using UnityEngine;

namespace AsteriskSoft.LineOfSight.Demo
{
    public class DestroyOnPlay : MonoBehaviour
    {
        private void Awake()
        {
            Destroy(gameObject);
        }
    }
}
