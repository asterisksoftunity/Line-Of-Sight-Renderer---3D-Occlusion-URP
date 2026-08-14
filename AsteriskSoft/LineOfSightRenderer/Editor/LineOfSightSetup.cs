// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// One click setup and validation:
// Tools > Line of Sight > Setup (Add Feature + Validate)
// Installs the renderer feature on the active URP renderer, wires the
// shaders and reports any remaining configuration problems.

using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AsteriskSoft.LineOfSight.Editor
{
    public static class LineOfSightSetup
    {
        const string kMenuRoot = "Tools/Line of Sight/";

        [MenuItem(kMenuRoot + "Setup (Add Feature + Validate)")]
        public static void FullSetup()
        {
            AddFeatureToActiveRenderer();
            Validate();
        }

        public static void AddFeatureToActiveRenderer()
        {
            var rendererData = GetActiveRendererData();
            if (rendererData == null)
            {
                Debug.LogError("[LOS] Could not locate an active URP Renderer Data asset. Is a URP asset assigned in Project Settings > Graphics (and the active Quality level)?");
                return;
            }

            if (rendererData.rendererFeatures.Any(f => f is LineOfSightRenderFeature))
            {
                Debug.Log($"[LOS] Line Of Sight feature is already present on renderer '{rendererData.name}'.", rendererData);
                return;
            }

            var feature = ScriptableObject.CreateInstance<LineOfSightRenderFeature>();
            feature.name = "Line Of Sight";
            Undo.RegisterCreatedObjectUndo(feature, "Add Line Of Sight Feature");

            if (EditorUtility.IsPersistent(rendererData))
                AssetDatabase.AddObjectToAsset(feature, rendererData);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(rendererData);
            so.Update();
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();

            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssetIfDirty(rendererData);
            Debug.Log($"[LOS] Added Line Of Sight renderer feature to '{rendererData.name}'.", feature);
        }

        public static void Validate()
        {
            bool ok = true;

            // URP present?
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                Debug.LogError("[LOS] The active render pipeline is not URP. Assign a URP asset in Project Settings > Graphics.");
                return;
            }

            // Both render paths are supported, so Compatibility Mode is not
            // an error either way. It is only reported for information, and
            // only on versions where the setting still exists (URP 17.0 to
            // 17.2); it was deprecated in 6.4 and removed afterwards.
#if LOS_URP_COMPAT_API
            try
            {
                var rg = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
                if (rg != null)
                {
                    Debug.Log(rg.enableRenderCompatibilityMode
                        ? "[LOS] Using the Compatibility Mode render path."
                        : "[LOS] Using the render graph path.");
                }
            }
            catch
            {
                // Setting unavailable: the render graph path is in use.
            }
#endif

            // Feature on the active renderer?
            var rendererData = GetActiveRendererData();
            var feature = rendererData != null
                ? rendererData.rendererFeatures.FirstOrDefault(f => f is LineOfSightRenderFeature) as LineOfSightRenderFeature
                : null;

            if (feature == null)
            {
                ok = false;
                Debug.LogWarning("[LOS] Render feature not found on the active renderer. Run Tools > Line of Sight > Setup (Add Feature + Validate).");
            }
            else
            {
                if (feature.settings.postShader == null)
                    feature.settings.postShader = Shader.Find("Hidden/LineOfSight/Post");
                if (feature.settings.occluderShader == null)
                    feature.settings.occluderShader = Shader.Find("Hidden/LineOfSight/Occluder");

                if (feature.settings.postShader == null || feature.settings.occluderShader == null)
                {
                    ok = false;
                    Debug.LogError("[LOS] Shaders not found. Make sure LineOfSightPost.shader and LineOfSightOccluder.shader are imported without errors.", feature);
                }
                if (feature.settings.occluderRenderingLayers.value == 0)
                {
                    ok = false;
                    Debug.LogWarning("[LOS] The Occluder Rendering Layers mask on the feature is empty - nothing will block vision.", feature);
                }
                EditorUtility.SetDirty(feature);
            }

            // Agent in the scene?
            var agent = Object.FindAnyObjectByType<LineOfSightAgent>(FindObjectsInactive.Include);
            if (agent == null)
            {
                ok = false;
                Debug.LogWarning("[LOS] No LineOfSightAgent found in the open scene(s). Add a Line Of Sight Agent component to your player GameObject.");
            }

            if (ok)
                Debug.Log("[LOS] Setup looks good. Enter Play mode (Game view) and set your wall renderers' Rendering Layer Mask to match the feature's Occluder Rendering Layers.");
        }

        static ScriptableRendererData GetActiveRendererData()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
                asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
                return null;

            var so = new SerializedObject(asset);
            var listProp = so.FindProperty("m_RendererDataList");
            var idxProp = so.FindProperty("m_DefaultRendererIndex");
            if (listProp == null || listProp.arraySize == 0)
                return null;

            int idx = Mathf.Clamp(idxProp != null ? idxProp.intValue : 0, 0, listProp.arraySize - 1);
            return listProp.GetArrayElementAtIndex(idx).objectReferenceValue as ScriptableRendererData;
        }
    }
}
