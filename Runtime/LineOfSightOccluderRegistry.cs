// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Deterministic CPU registry of occluder renderers and terrains. The render
// feature draws entries directly with CommandBuffer.DrawRenderer, bypassing
// frustum culling, shader tag matching and GPU driven rendering, so an in
// range occluder can never be silently skipped.

using System.Collections.Generic;
using UnityEngine;

namespace AsteriskSoft.LineOfSight
{
    public static class LineOfSightOccluderRegistry
    {
        public struct Entry
        {
            public Renderer renderer;
            public int subMeshCount;
        }

        static readonly List<Entry> s_Entries = new List<Entry>(256);
        static readonly List<Terrain> s_Terrains = new List<Terrain>(8);
        static double s_NextRefresh = double.NegativeInfinity;
        static bool s_Dirty = true;

        // Statics survive play-mode transitions when Domain Reload is
        // disabled, leaving the registry full of destroyed renderers with
        // the "already scanned" state intact - occluders would silently
        // stop working until the next interval. Reset explicitly.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay()
        {
            s_Entries.Clear();
            s_Terrains.Clear();
            s_Dirty = true;
            s_NextRefresh = double.NegativeInfinity;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void EditorInit()
        {
            UnityEditor.EditorApplication.playModeStateChanged += _ => RequestRefresh();
        }
#endif

        /// <summary>
        /// Call after spawning or destroying occluder geometry at runtime to
        /// re-scan immediately instead of waiting for the refresh interval.
        /// </summary>
        public static void RequestRefresh() => s_Dirty = true;

        /// <summary>Active terrains, refreshed together with GetEntries.</summary>
        public static List<Terrain> GetTerrains() => s_Terrains;

        public static List<Entry> GetEntries(float refreshInterval)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (s_Dirty || now >= s_NextRefresh)
            {
                Rebuild();
                s_Dirty = false;
                s_NextRefresh = refreshInterval <= 0f ? double.NegativeInfinity : now + refreshInterval;
            }
            return s_Entries;
        }

        static void Rebuild()
        {
            s_Entries.Clear();
            s_Terrains.Clear();
            s_Terrains.AddRange(Terrain.activeTerrains);

            var meshRenderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude,FindObjectsSortMode.None);
            foreach (var mr in meshRenderers)
            {
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null)
                    continue;
                s_Entries.Add(new Entry { renderer = mr, subMeshCount = mesh.subMeshCount });
            }

            var skinnedRenderers = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude,FindObjectsSortMode.None);
            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh == null)
                    continue;
                s_Entries.Add(new Entry { renderer = smr, subMeshCount = smr.sharedMesh.subMeshCount });
            }
        }
    }
}
