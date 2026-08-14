// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// URP renderer feature implementing the line of sight effect.
// Pipeline: occluder height capture (direct draws into MRT top/bottom height
// maps), underside propagation and dilation, per pixel sight ray march against
// the maps, Gaussian blur, temporal response, composite. Also feeds the CPU
// gameplay queries (LineOfSightQuery) from the same maps.
// Implemented with the URP render graph system (an unsafe pass, which exposes a
// standard CommandBuffer). This works on every URP 17 version, with or without
// the legacy Compatibility Mode setting, so no version defines are needed.

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AsteriskSoft.LineOfSight
{
    public class LineOfSightRenderFeature : ScriptableRendererFeature
    {
        public enum DebugView
        {
            None = 0,
            VisibilityMask = 1,
            OccluderHeightMap = 2,
            VisibilityMaskRaw = 3,
            MarchDiagnostic = 4
        }

        [Serializable]
        public class LosSettings
        {
            [Header("Occluders")]
            [Tooltip("Only renderers whose Rendering Layer Mask overlaps this mask will block vision. Defaults to rendering layer index 1 (the second layer), leaving the Default layer free for everything else. Name your layers in Project Settings > Tags and Layers > Rendering Layers, then enable the matching layer on each occluder's Renderer under Additional Settings > Rendering Layer Mask.")]
            public RenderingLayerMask occluderRenderingLayers = 1u << 1;

            [Tooltip("Resolution of the top-down occluder height capture. Higher = crisper shadow silhouettes at range.")]
            [Range(128, 2048)] public int occluderMapResolution = 512;

            [Tooltip("Seconds between automatic re-scans of the scene for occluder renderers. Newly spawned occluders appear after at most this long, or immediately if you call LineOfSightOccluderRegistry.RequestRefresh(). 0 = re-scan every frame (fully dynamic scenes).")]
            [Range(0f, 30f)] public float occluderRefreshInterval = 3f;

            [Tooltip("Include Unity Terrains as occluders (hills block sight). Terrains ignore the Rendering Layer Mask filter - this toggle controls them.")]
            public bool includeTerrain = true;

            [Tooltip("World units subtracted from captured terrain height, so flat ground never catches grazing sight rays - only terrain rising above this bias occludes.")]
            [Min(0f)] public float terrainHeightBias = 0.35f;

            [Tooltip("Vertical capture range ABOVE the agent's eye. Keep LARGE (default 100) - occluders register via their horizontal faces, so this must comfortably exceed your tallest occluder or walls will pop out of the capture as the viewer's height changes. Only reduce to deliberately exclude high geometry (e.g. upper floors).")]
            [Min(1f)] public float captureRangeAbove = 100f;

            [Tooltip("Vertical capture range BELOW the agent's eye. Keep LARGE (default 100). Only reduce to deliberately exclude low geometry (e.g. lower floors).")]
            [Min(1f)] public float captureRangeBelow = 100f;

            [Tooltip("Extra margin captured around the agent, as a fraction of the vision distance.")]
            [Range(0f, 0.5f)] public float capturePadding = 0.3f;

            [Header("Shadowing")]
            [Tooltip("Upper limit on per-pixel raymarch samples along a sight ray. Below the cap, samples are spaced ~1.25 occluder texels apart; when a long ray hits the cap, spacing grows and DISTANT THIN walls may be stepped over (dithered sparkle). 128 covers most scenes; raise only for thin walls at long view distances.")]
            [Range(32, 1024)] public int raymarchSteps = 128;

            [Tooltip("Iterations of underside propagation across connected occluder geometry. Repairs overhangs whose top and bottom surfaces don't share the same top-down silhouette (warped/curved/tilted slabs and roofs) - without this, their one-surface fringe columns degenerate into false full-height walls that streak the mask. Raise if red streaks appear in Debug View > MarchDiagnostic, especially at high occluder map resolutions.")]
            [Range(0, 8)] public int undersideFillIterations = 4;

            [Tooltip("World-space slack along the sight ray: occluder surfaces within this distance of the target do not block it, so the visible faces of walls stay lit instead of self-shadowing. KEEP SMALL - it must be well under the thickness of your thinnest occluder, or rays crossing that occluder (e.g. a roof viewed from directly below) are only partially blocked and leak light. A few centimetres is normally right.")]
            [Range(0.005f, 0.5f)] public float occluderSurfaceBoost = 0.3f;

            [Tooltip("Penumbra softness of the visibility test (in slope units). Softens the reveal of surfaces near the grazing angle, e.g. wall tops.")]
            [Range(0.001f, 0.5f)] public float shadowSoftness = 0.04f;

            [Tooltip("Small upward bias applied to each pixel's sight-line slope. Prevents surfaces that belong to occluder meshes themselves (e.g. floors merged into wall prefabs) from sitting exactly on their own silhouette and flickering half-lit. The slope-space equivalent of shadow bias.")]
            [Range(0f, 0.2f)] public float shadowSlopeBias = 0.02f;

            [Tooltip("Strength of the surface-facing shadow that hides roof tops, wall top rims and wall back sides which cannot geometrically face the eye. Applies only where occluder geometry is captured, so non-occluder objects (the player, props) are never darkened by it. 0 disables.")]
            [Range(0f, 1f)] public float facingShadowStrength = 1f;

            [Header("Screen Mask")]
            [Tooltip("Divides screen resolution for the visibility mask. 2 = half resolution (recommended). The mask is bilinearly upsampled on composite.")]
            [Range(1, 4)] public int maskDownsample = 2;

            [Tooltip("Separable Gaussian blur iterations applied to the screen-space mask.")]
            [Range(0, 4)] public int blurIterations = 2;

            [Tooltip("Blur kernel spread multiplier.")]
            [Range(0.1f, 4f)] public float blurSpread = 1.25f;

            [Header("Temporal Response")]
            [Tooltip("Approximate seconds for newly-seen areas to FULLY light up (95% settled). 0 = instant. Verify visually by comparing Debug View > VisibilityMask (lagged) against VisibilityMaskRaw (instant).")]
            [Range(0f, 3f)] public float revealTime = 0.0f;

            [Tooltip("Approximate seconds for areas leaving view to FULLY fade back to hidden (95% settled). 0 = instant.")]
            [Range(0f, 3f)] public float hideTime = 0.0f;

            [Header("Composite")]
            [Tooltip("Tint applied to hidden areas.")]
            public Color hiddenColor = new Color(0.045f, 0.06f, 0.09f, 1f);

            [Tooltip("How strongly hidden areas are darkened. 1 = fully apply the hidden look.")]
            [Range(0f, 1f)] public float hiddenIntensity = 0.65f;

            [Tooltip("Desaturation of hidden areas before tinting.")]
            [Range(0f, 1f)] public float hiddenDesaturation = 0.85f;

            [Tooltip("If enabled, the proximity circle reveals through walls (x-ray). If disabled, the proximity circle respects occluders like the cone does.")]
            public bool proximitySeesThroughWalls = false;

            [Tooltip("If enabled, sky / background pixels are darkened too. Usually off for isometric games with full ground coverage.")]
            public bool affectSky = false;

            [Header("General")]
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

            [Tooltip("Also render the effect in the Scene view (uses the same agent). Temporal response stays Game-view only.")]
            public bool renderInSceneView = false;

            public DebugView debugView = DebugView.None;

            [Header("Shaders (auto-located - leave alone)")]
            public Shader postShader;
            public Shader occluderShader;
        }

        public LosSettings settings = new LosSettings();

        LosPass m_Pass;
        Material m_PostMaterial;
        Material m_OccluderMaterial;

        public override void Create()
        {
            TryLocateShaders();
            m_Pass?.Dispose();
            m_Pass = new LosPass();
        }

        void TryLocateShaders()
        {
            // Auto-wiring: shaders are found by name once in the editor and
            // then serialized on this asset so they are included in builds.
            if (settings.postShader == null)
                settings.postShader = Shader.Find("Hidden/LineOfSight/Post");
            if (settings.occluderShader == null)
                settings.occluderShader = Shader.Find("Hidden/LineOfSight/Occluder");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Camera filtering happens in RecordRenderGraph, where the camera
            // data is available without using deprecated APIs.

            // Auto-discovered agent - no manual references needed.
            var agent = LineOfSightAgent.Active;
            if (agent == null || !agent.isActiveAndEnabled)
                return;

            TryLocateShaders();
            if (settings.postShader == null || settings.occluderShader == null)
                return;

            if (m_PostMaterial == null || m_PostMaterial.shader != settings.postShader)
            {
                CoreUtils.Destroy(m_PostMaterial);
                m_PostMaterial = CoreUtils.CreateEngineMaterial(settings.postShader);
            }
            if (m_OccluderMaterial == null || m_OccluderMaterial.shader != settings.occluderShader)
            {
                CoreUtils.Destroy(m_OccluderMaterial);
                m_OccluderMaterial = CoreUtils.CreateEngineMaterial(settings.occluderShader);
            }

            m_Pass.renderPassEvent = settings.renderPassEvent;
            m_Pass.Setup(settings, agent, m_PostMaterial, m_OccluderMaterial);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
            CoreUtils.Destroy(m_PostMaterial);
            m_PostMaterial = null;
            CoreUtils.Destroy(m_OccluderMaterial);
            m_OccluderMaterial = null;
        }

        //  The render pass (legacy / compatibility-mode Execute path)
        sealed class LosPass : ScriptableRenderPass
        {
            const int kPassVisibility = 0;
            const int kPassBlur = 1;
            const int kPassTemporal = 2;
            const int kPassComposite = 3;
            const int kPassDilateTop = 4;
            const int kPassDilateBottom = 5;
            const int kPassBottomPropagate = 6;

            static readonly ProfilingSampler s_Sampler = new ProfilingSampler("Line Of Sight");

            static class Ids
            {
                public static readonly int InvVP       = Shader.PropertyToID("_LOS_InvVP");
                public static readonly int Center      = Shader.PropertyToID("_LOS_Center");
                public static readonly int Player      = Shader.PropertyToID("_LOS_Player");
                public static readonly int EyeY        = Shader.PropertyToID("_LOS_EyeY");
                public static readonly int Cone        = Shader.PropertyToID("_LOS_Cone");
                public static readonly int Prox        = Shader.PropertyToID("_LOS_Prox");
                public static readonly int Shadow      = Shader.PropertyToID("_LOS_Shadow");
                public static readonly int March       = Shader.PropertyToID("_LOS_March");
                public static readonly int Temporal    = Shader.PropertyToID("_LOS_Temporal");
                public static readonly int DepthSize   = Shader.PropertyToID("_LOS_DepthSize");
                public static readonly int BlurParams  = Shader.PropertyToID("_LOS_BlurParams");
                public static readonly int HiddenColor = Shader.PropertyToID("_LOS_HiddenColor");
                public static readonly int Desat       = Shader.PropertyToID("_LOS_Desat");
                public static readonly int Debug       = Shader.PropertyToID("_LOS_Debug");
                public static readonly int OccluderTex = Shader.PropertyToID("_LOS_OccluderTex");
                public static readonly int BottomTex   = Shader.PropertyToID("_LOS_BottomTex");
                public static readonly int MaskTex     = Shader.PropertyToID("_LOS_MaskTex");
                public static readonly int RawMaskTex  = Shader.PropertyToID("_LOS_RawMaskTex");
                public static readonly int HistoryTex  = Shader.PropertyToID("_LOS_HistoryTex");
                public static readonly int TerrainHeight = Shader.PropertyToID("_LOS_TerrainHeight");
                public static readonly int TerrainRect   = Shader.PropertyToID("_LOS_TerrainRect");
                public static readonly int TerrainParams = Shader.PropertyToID("_LOS_TerrainParams");
            }

            LosSettings m_Settings;
            LineOfSightAgent m_Agent;
            Material m_PostMat;
            Material m_OccluderMat;

            RTHandle m_HeightTop;
            RTHandle m_HeightBot;
            RTHandle m_HeightTopDilated;
            RTHandle m_HeightBotDilated;
            RTHandle m_HeightDepth;
            RTHandle m_MaskA, m_MaskB;
            RTHandle m_HistoryA, m_HistoryB;
            RTHandle m_SceneCopy;

            readonly RenderTargetIdentifier[] m_HeightMRT = new RenderTargetIdentifier[2];

            bool m_HistoryValid;

            public void Setup(LosSettings settings, LineOfSightAgent agent, Material postMat, Material occluderMat)
            {
                m_Settings = settings;
                m_Agent = agent;
                m_PostMat = postMat;
                m_OccluderMat = occluderMat;

                // Ask URP for the camera depth texture (needed for world
                // position reconstruction and temporal reprojection).
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            static bool HistoryMatches(RTHandle handle, int width, int height)
            {
                return handle != null && handle.rt != null
                       && handle.rt.width == width && handle.rt.height == height;
            }

            class PassData
            {
                internal LosPass pass;
                internal UniversalCameraData cameraData;
                internal TextureHandle activeColor;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Agent == null || m_PostMat == null || m_OccluderMat == null)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                var camType = cameraData.cameraType;
                bool sceneViewOk = m_Settings.renderInSceneView && camType == CameraType.SceneView;
                if (camType != CameraType.Game && !sceneViewOk)
                    return;
                if (cameraData.renderType != CameraRenderType.Base)
                    return;

                // An unsafe pass gives us a real CommandBuffer, so the whole
                // pipeline below (direct renderer draws, custom view/projection
                // matrices, Blitter calls, async readback) works unchanged.
                using (var builder = renderGraph.AddUnsafePass<PassData>("Line Of Sight", out var passData))
                {
                    passData.pass = this;
                    passData.cameraData = cameraData;
                    passData.activeColor = resourceData.activeColorTexture;

                    builder.UseTexture(passData.activeColor, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                    {
                        var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                        var cd = data.cameraData;
                        data.pass.ExecuteCore(nativeCmd, cd.camera, cd.cameraTargetDescriptor,
                                              cd.cameraType, data.activeColor);
                    });
                }
            }

#if LOS_URP_COMPAT_API
            // Compatibility Mode (Render Graph disabled) entry point. This API
            // only exists in URP 17.0 to 17.2; the version define keeps it out
            // of newer versions where it was removed. Unity calls either this
            // or RecordRenderGraph, never both.
#pragma warning disable CS0618, CS0672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (m_Agent == null || m_PostMat == null || m_OccluderMat == null)
                    return;

                ref var cameraData = ref renderingData.cameraData;
                var camType = cameraData.cameraType;
                bool sceneViewOk = m_Settings.renderInSceneView && camType == CameraType.SceneView;
                if (camType != CameraType.Game && !sceneViewOk)
                    return;
                if (cameraData.renderType != CameraRenderType.Base)
                    return;

                var cmd = CommandBufferPool.Get();
                ExecuteCore(cmd, cameraData.camera, cameraData.cameraTargetDescriptor, camType,
                            cameraData.renderer.cameraColorTargetHandle);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0618, CS0672
#endif

            // Shared implementation, deliberately free of any URP camera-data
            // type so both the render graph and the Compatibility Mode entry
            // points can call it.
            void ExecuteCore(CommandBuffer cmd, Camera camera, RenderTextureDescriptor targetDesc,
                             CameraType cameraType, RTHandle camColor)
            {
                using (new ProfilingScope(cmd, s_Sampler))
                {
                    // Derive
                    float captureRadius = Mathf.Max(m_Agent.viewDistance, m_Agent.proximityRadius)
                                          * (1f + m_Settings.capturePadding) + 0.01f;
                    Vector3 eye = m_Agent.EyePosition;
                    Vector3 viewDir = m_Agent.ViewDirection;

                    int occRes = m_Settings.occluderMapResolution;

                    // Snap the capture center to the occluder texel grid so
                    // the rasterized silhouettes never shimmer as the player
                    // moves (the classic shadow-map stabilisation trick).
                    float texelWorld = (captureRadius * 2f) / occRes;
                    Vector2 snapped = new Vector2(
                        Mathf.Round(eye.x / texelWorld) * texelWorld,
                        Mathf.Round(eye.z / texelWorld) * texelWorld);

                    const float kNear = 0.05f;
                    float camY = eye.y + m_Settings.captureRangeAbove + kNear;
                    float far = kNear + m_Settings.captureRangeAbove + m_Settings.captureRangeBelow;

                    // Targets
                    var occDesc = new RenderTextureDescriptor(occRes, occRes, RenderTextureFormat.RHalf, 0)
                    { sRGB = false, msaaSamples = 1 };
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_HeightTop, occDesc,
                        FilterMode.Point, TextureWrapMode.Clamp, name: "_LOS_HeightTop");
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_HeightBot, occDesc,
                        FilterMode.Point, TextureWrapMode.Clamp, name: "_LOS_HeightBot");
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_HeightTopDilated, occDesc,
                        FilterMode.Point, TextureWrapMode.Clamp, name: "_LOS_HeightTopDilated");
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_HeightBotDilated, occDesc,
                        FilterMode.Point, TextureWrapMode.Clamp, name: "_LOS_HeightBotDilated");

                    var depthDesc = new RenderTextureDescriptor(occRes, occRes, RenderTextureFormat.Depth, 16)
                    { msaaSamples = 1 };
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_HeightDepth, depthDesc,
                        FilterMode.Point, TextureWrapMode.Clamp, name: "_LOS_HeightDepth");

                    var maskDesc = targetDesc;
                    maskDesc.depthBufferBits = 0;
                    maskDesc.msaaSamples = 1;
                    maskDesc.sRGB = false;
                    maskDesc.colorFormat = RenderTextureFormat.RHalf;
                    maskDesc.width = Mathf.Max(1, maskDesc.width / m_Settings.maskDownsample);
                    maskDesc.height = Mathf.Max(1, maskDesc.height / m_Settings.maskDownsample);
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_MaskA, maskDesc,
                        FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_LOS_MaskA");
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_MaskB, maskDesc,
                        FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_LOS_MaskB");

                    var copyDesc = targetDesc;
                    copyDesc.depthBufferBits = 0;
                    copyDesc.msaaSamples = 1;
                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_SceneCopy, copyDesc,
                        FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_LOS_SceneCopy");

                    // 1) occluder height capture
                    // Orthographic top-down "virtual shadow camera". Camera
                    // right = world +X, camera up = world +Z, so occluder UV
                    // maps 1:1 onto world XZ around the snapped center.
                    Matrix4x4 captureView = Matrix4x4.TRS(
                        new Vector3(snapped.x, camY, snapped.y),
                        Quaternion.LookRotation(Vector3.down, Vector3.forward),
                        new Vector3(1f, 1f, -1f)).inverse;
                    // NOTE: pass the RAW ortho matrix. SetViewProjectionMatrices
                    // performs the platform GPU conversion (y-flip / z-range)
                    // internally - running it through GL.GetGPUProjectionMatrix
                    // first double-flips on D3D-style APIs and mirrors the
                    // capture, which puts every shadow on the wrong side.
                    Matrix4x4 captureProj = Matrix4x4.Ortho(
                        -captureRadius, captureRadius, -captureRadius, captureRadius, kNear, far);

                    // Clear each height target to its blend identity, then
                    // bind both as MRT (TOP uses Max blending, BOTTOM uses
                    // Min blending - see the occluder shader).
                    CoreUtils.SetRenderTarget(cmd, m_HeightTop, ClearFlag.Color, new Color(-10000f, 0f, 0f, 0f));
                    CoreUtils.SetRenderTarget(cmd, m_HeightBot, ClearFlag.Color, new Color(10000f, 0f, 0f, 0f));
                    m_HeightMRT[0] = m_HeightTop.nameID;
                    m_HeightMRT[1] = m_HeightBot.nameID;
                    cmd.SetRenderTarget(m_HeightMRT, m_HeightDepth.nameID);
                    cmd.SetViewProjectionMatrices(captureView, captureProj);
                    cmd.SetGlobalFloat(Ids.EyeY, eye.y); // occluder shader stores heights relative to the eye

                    // Deterministic direct draws: every registered renderer
                    // whose Rendering Layer Mask matches and whose bounds
                    // overlap the capture volume is drawn, unconditionally.
                    uint layerMask = m_Settings.occluderRenderingLayers.value;
                    float slabTop = eye.y + m_Settings.captureRangeAbove;
                    float slabBottom = eye.y - m_Settings.captureRangeBelow;
                    float minX = snapped.x - captureRadius, maxX = snapped.x + captureRadius;
                    float minZ = snapped.y - captureRadius, maxZ = snapped.y + captureRadius;

                    var entries = LineOfSightOccluderRegistry.GetEntries(m_Settings.occluderRefreshInterval);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var r = entries[i].renderer;
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                            continue;
                        if ((r.renderingLayerMask & layerMask) == 0)
                            continue;

                        Bounds b = r.bounds;
                        if (b.max.x < minX || b.min.x > maxX || b.max.z < minZ || b.min.z > maxZ)
                            continue;
                        if (b.max.y < slabBottom || b.min.y > slabTop)
                            continue;

                        int subMeshes = entries[i].subMeshCount;
                        for (int si = 0; si < subMeshes; si++)
                            cmd.DrawRenderer(r, m_OccluderMat, si, 0);
                    }

                    // Terrains: splat each in-range terrain's GPU heightmap
                    // into the capture via a procedural quad (occluder
                    // shader pass 1). Solid below - hills block sight.
                    if (m_Settings.includeTerrain)
                    {
                        var terrains = LineOfSightOccluderRegistry.GetTerrains();
                        for (int i = 0; i < terrains.Count; i++)
                        {
                            var t = terrains[i];
                            if (t == null || !t.isActiveAndEnabled)
                                continue;
                            var td = t.terrainData;
                            if (td == null || td.heightmapTexture == null)
                                continue;

                            Vector3 tp = t.transform.position;
                            Vector3 ts = td.size;
                            if (tp.x + ts.x < minX || tp.x > maxX || tp.z + ts.z < minZ || tp.z > maxZ)
                                continue;

                            cmd.SetGlobalTexture(Ids.TerrainHeight, td.heightmapTexture);
                            cmd.SetGlobalVector(Ids.TerrainRect, new Vector4(tp.x, tp.z, ts.x, ts.z));
                            // heightmapTexture stores normalized height * 0.5,
                            // so world height = sample * 2 * size.y.
                            cmd.SetGlobalVector(Ids.TerrainParams, new Vector4(
                                tp.y, ts.y * 2f, m_Settings.terrainHeightBias, 0f));
                            cmd.DrawProcedural(Matrix4x4.identity, m_OccluderMat, 1, MeshTopology.Triangles, 6, 1);
                        }
                    }

                    // Restore the camera matrices (raw - the command buffer
                    // performs the platform conversion itself).
                    cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);

                    // Global uniforms
                    Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                    Matrix4x4 vp = gpuProj * camera.worldToCameraMatrix;
                    cmd.SetGlobalMatrix(Ids.InvVP, vp.inverse);
                    var fullDesc = targetDesc;
                    cmd.SetGlobalVector(Ids.DepthSize, new Vector4(
                        fullDesc.width, fullDesc.height, 1f / fullDesc.width, 1f / fullDesc.height));
                    cmd.SetGlobalVector(Ids.Center, new Vector4(snapped.x, snapped.y, captureRadius, 1f / captureRadius));
                    cmd.SetGlobalVector(Ids.Player, new Vector4(eye.x, eye.z, viewDir.x, viewDir.z));

                    float feather = Mathf.Max(0.002f, m_Agent.angleFeather * Mathf.Deg2Rad);
                    float halfAngle = m_Agent.viewAngle * 0.5f * Mathf.Deg2Rad;
                    if (m_Agent.viewAngle >= 359.5f)
                        halfAngle = Mathf.PI + feather + 0.05f; // fully omnidirectional
                    float rangeFeather = Mathf.Max(0.01f, m_Agent.rangeFeather * m_Agent.viewDistance);
                    cmd.SetGlobalVector(Ids.Cone, new Vector4(halfAngle, feather, m_Agent.viewDistance, rangeFeather));
                    cmd.SetGlobalVector(Ids.Prox, new Vector4(
                        m_Agent.proximityRadius,
                        Mathf.Max(0.01f, m_Agent.proximityFeather),
                        m_Settings.proximitySeesThroughWalls ? 1f : 0f,
                        m_Settings.occluderSurfaceBoost));
                    cmd.SetGlobalVector(Ids.Shadow, new Vector4(
                        m_Settings.shadowSoftness,
                        m_Settings.facingShadowStrength,
                        m_Settings.shadowSlopeBias,
                        m_Settings.affectSky ? 1f : 0f));
                    // Raymarch: step ~1.25 occluder texels so thin walls are
                    // reliably sampled; capped by raymarchSteps for range.
                    cmd.SetGlobalVector(Ids.March, new Vector4(
                        texelWorld * 1.25f,
                        m_Settings.raymarchSteps,
                        0.08f,
                        1f / occRes));
                    Color hc = m_Settings.hiddenColor.linear;
                    cmd.SetGlobalVector(Ids.HiddenColor, new Vector4(hc.r, hc.g, hc.b, m_Settings.hiddenIntensity));
                    cmd.SetGlobalFloat(Ids.Desat, m_Settings.hiddenDesaturation);
                    cmd.SetGlobalFloat(Ids.Debug, (float)m_Settings.debugView);

                    // Underside propagation: flood true undersides across
                    // connected geometry so one-surface overhang fringes
                    // (warped/curved/tilted slabs) become real intervals.
                    // Occupancy mask = the ORIGINAL top map, bound first.
                    cmd.SetGlobalTexture(Ids.OccluderTex, m_HeightTop);
                    RTHandle botSrc = m_HeightBot, botDst = m_HeightBotDilated;
                    for (int i = 0; i < m_Settings.undersideFillIterations; i++)
                    {
                        Blitter.BlitCameraTexture(cmd, botSrc, botDst, m_PostMat, kPassBottomPropagate);
                        (botSrc, botDst) = (botDst, botSrc);
                    }

                    // Interval dilation: 3x3 max on tops / min on bottoms
                    // repairs degenerate zero-thickness border texels (see
                    // the dilation passes in the shader) and makes every
                    // occluder one texel more conservative.
                    Blitter.BlitCameraTexture(cmd, m_HeightTop, m_HeightTopDilated, m_PostMat, kPassDilateTop);
                    Blitter.BlitCameraTexture(cmd, botSrc, botDst, m_PostMat, kPassDilateBottom);
                    cmd.SetGlobalTexture(Ids.OccluderTex, m_HeightTopDilated);
                    cmd.SetGlobalTexture(Ids.BottomTex, botDst);

                    // Gameplay visibility: read the FINAL maps back to the
                    // CPU (async, in-buffer so it matches this frame's data)
                    // whenever any LineOfSightVisibilityTracker exists. The
                    // readback drives LineOfSightQuery - one source of truth
                    // for visuals and gameplay.
                    if (LineOfSightQuery.WantsData && cameraType == CameraType.Game)
                    {
                        var qp = new LineOfSightQuery.FrameParams
                        {
                            center = snapped,
                            radius = captureRadius,
                            eyeXZ = new Vector2(eye.x, eye.z),
                            eyeY = eye.y,
                            viewDirXZ = new Vector2(viewDir.x, viewDir.z),
                            halfAngle = halfAngle,
                            angleFeather = feather,
                            viewDistance = m_Agent.viewDistance,
                            rangeFeather = rangeFeather,
                            proximityRadius = m_Agent.proximityRadius,
                            proximityFeather = Mathf.Max(0.01f, m_Agent.proximityFeather),
                            proximityThroughWalls = m_Settings.proximitySeesThroughWalls,
                            surfaceBoost = m_Settings.occluderSurfaceBoost,
                            softness = m_Settings.shadowSoftness,
                            slopeBias = m_Settings.shadowSlopeBias,
                            stepWorld = texelWorld * 1.25f,
                            maxSteps = m_Settings.raymarchSteps,
                            mapResolution = occRes
                        };
                        LineOfSightQuery.BeginRequest(cmd, m_HeightTopDilated.rt, botDst.rt, qp);
                    }

                    // 2) screen-space visibility mask + blur
                    Blitter.BlitCameraTexture(cmd, m_HeightTopDilated, m_MaskA, m_PostMat, kPassVisibility);
                    RTHandle src = m_MaskA, dst = m_MaskB;
                    for (int i = 0; i < m_Settings.blurIterations; i++)
                    {
                        float spread = m_Settings.blurSpread * (1f + i);
                        cmd.SetGlobalVector(Ids.BlurParams, new Vector4(spread / maskDesc.width, 0f, 0f, 0f));
                        Blitter.BlitCameraTexture(cmd, src, dst, m_PostMat, kPassBlur);
                        (src, dst) = (dst, src);
                        cmd.SetGlobalVector(Ids.BlurParams, new Vector4(0f, spread / maskDesc.height, 0f, 0f));
                        Blitter.BlitCameraTexture(cmd, src, dst, m_PostMat, kPassBlur);
                        (src, dst) = (dst, src);
                    }
                    cmd.SetGlobalTexture(Ids.RawMaskTex, src);

                    // 3) temporal response
                    RTHandle finalMask = src;
                    bool temporalOn = (m_Settings.revealTime > 0.001f || m_Settings.hideTime > 0.001f)
                                      && cameraType == CameraType.Game;
                    if (temporalOn)
                    {
                        // Only reallocate history when the mask DIMENSIONS
                        // actually change - descriptor-comparison churn in
                        // ReAllocateHandleIfNeeded must never silently reset
                        // the history every frame (that would make the
                        // temporal response invisible).
                        if (!HistoryMatches(m_HistoryA, maskDesc.width, maskDesc.height) ||
                            !HistoryMatches(m_HistoryB, maskDesc.width, maskDesc.height))
                        {
                            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryA, maskDesc,
                                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_LOS_HistoryA");
                            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HistoryB, maskDesc,
                                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_LOS_HistoryB");
                            if (m_HistoryValid)
                                Debug.Log("[LOS] Temporal history reset (mask resolution changed). If this message spams every frame, please report it.");
                            m_HistoryValid = false;
                        }

                        float dt = Application.isPlaying ? Mathf.Max(Time.deltaTime, 1e-4f) : (1f / 60f);
                        // Times mean "seconds to ~95% settled", so the
                        // exponential rate is 3/T (1 - e^-3 = 0.95).
                        float kReveal = m_Settings.revealTime <= 0.001f ? 1f : 1f - Mathf.Exp(-3f * dt / m_Settings.revealTime);
                        float kHide   = m_Settings.hideTime   <= 0.001f ? 1f : 1f - Mathf.Exp(-3f * dt / m_Settings.hideTime);

                        cmd.SetGlobalVector(Ids.Temporal, new Vector4(kReveal, kHide, m_HistoryValid ? 1f : 0f, 0f));
                        cmd.SetGlobalTexture(Ids.HistoryTex, m_HistoryA);
                        Blitter.BlitCameraTexture(cmd, src, m_HistoryB, m_PostMat, kPassTemporal);
                        finalMask = m_HistoryB;

                        (m_HistoryA, m_HistoryB) = (m_HistoryB, m_HistoryA);
                        m_HistoryValid = true;
                    }
                    else
                    {
                        m_HistoryValid = false;
                    }
                    cmd.SetGlobalTexture(Ids.MaskTex, finalMask);

                    // 4) composite
                    Blitter.BlitCameraTexture(cmd, camColor, m_SceneCopy);
                    Blitter.BlitCameraTexture(cmd, m_SceneCopy, camColor, m_PostMat, kPassComposite);
                }
            }

            public void Dispose()
            {
                m_HeightTop?.Release();        m_HeightTop = null;
                m_HeightBot?.Release();        m_HeightBot = null;
                m_HeightTopDilated?.Release(); m_HeightTopDilated = null;
                m_HeightBotDilated?.Release(); m_HeightBotDilated = null;
                m_HeightDepth?.Release();      m_HeightDepth = null;
                m_MaskA?.Release();       m_MaskA = null;
                m_MaskB?.Release();       m_MaskB = null;
                m_HistoryA?.Release();    m_HistoryA = null;
                m_HistoryB?.Release();    m_HistoryB = null;
                m_SceneCopy?.Release();   m_SceneCopy = null;
                m_HistoryValid = false;
            }
        }
    }
}
