// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// CPU mirror of the GPU visibility test, fed by asynchronous readback of the
// same height maps the shader marches. One source of truth for visuals and
// gameplay. TryComputeVisibility must stay in sync with the VisibilityMask
// pass in LineOfSightPost.shader.

using UnityEngine;
using UnityEngine.Rendering;

namespace AsteriskSoft.LineOfSight
{
    public static class LineOfSightQuery
    {
        public struct FrameParams
        {
            public Vector2 center;          // snapped capture center XZ
            public float radius;            // capture radius
            public Vector2 eyeXZ;
            public float eyeY;
            public Vector2 viewDirXZ;
            public float halfAngle;         // radians
            public float angleFeather;      // radians
            public float viewDistance;
            public float rangeFeather;      // world units
            public float proximityRadius;
            public float proximityFeather;
            public bool proximityThroughWalls;
            public float surfaceBoost;
            public float softness;
            public float slopeBias;
            public float stepWorld;
            public int maxSteps;
            public int mapResolution;
        }

        /// <summary>
        /// If gameplay visibility appears mirrored front-to-back relative to
        /// the visuals, the platform flips readback rows - toggle this.
        /// </summary>
        public static bool FlipReadbackY = false;

        static ushort[] s_Top, s_Bot;           // published (consistent pair)
        static ushort[] s_TopPending, s_BotPending;
        static FrameParams s_Params, s_ParamsPending;
        static bool s_HasData;
        static int s_PendingCount;
        static int s_PendingSinceFrame;
        static int s_Consumers;

        // Statics survive play-mode transitions when Domain Reload is
        // disabled; an interrupted readback could leave the pipeline
        // permanently blocked. Reset explicitly.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay()
        {
            s_PendingCount = 0;
            s_HasData = false;
            s_Consumers = 0;
        }

        public static bool HasData => s_HasData;
        public static bool WantsData => s_Consumers > 0;

        public static void AddConsumer() => s_Consumers++;
        public static void RemoveConsumer() => s_Consumers = Mathf.Max(0, s_Consumers - 1);

        /// <summary>
        /// Called by the render feature: enqueues readbacks of the final
        /// (dilated/propagated) height maps inside the command buffer, so
        /// the data matches exactly what the shader marches this frame.
        /// </summary>
        internal static void BeginRequest(CommandBuffer cmd, RenderTexture top, RenderTexture bot, in FrameParams p)
        {
            // Safety valve: if a previous readback died mid-flight (device
            // reset, play-mode interruption), don't stay blocked forever.
            if (s_PendingCount > 0 && Time.frameCount - s_PendingSinceFrame > 8)
                s_PendingCount = 0;

            if (s_PendingCount > 0 || top == null || bot == null)
                return;
            s_ParamsPending = p;
            s_PendingCount = 2;
            s_PendingSinceFrame = Time.frameCount;
            cmd.RequestAsyncReadback(top, 0, OnTopReadback);
            cmd.RequestAsyncReadback(bot, 0, OnBotReadback);
        }

        static void OnTopReadback(AsyncGPUReadbackRequest req)
        {
            Copy(req, ref s_TopPending);
            Finish();
        }

        static void OnBotReadback(AsyncGPUReadbackRequest req)
        {
            Copy(req, ref s_BotPending);
            Finish();
        }

        static void Copy(AsyncGPUReadbackRequest req, ref ushort[] target)
        {
            if (req.hasError)
                return;
            var data = req.GetData<ushort>();
            if (target == null || target.Length != data.Length)
                target = new ushort[data.Length];
            data.CopyTo(target);
        }

        static void Finish()
        {
            s_PendingCount--;
            if (s_PendingCount > 0)
                return;

            int expected = s_ParamsPending.mapResolution * s_ParamsPending.mapResolution;
            if (s_TopPending != null && s_BotPending != null
                && s_TopPending.Length >= expected && s_BotPending.Length >= expected)
            {
                // Swap pending <-> published so both maps + params stay a
                // consistent set and arrays are reused without allocation.
                (s_Top, s_TopPending) = (s_TopPending, s_Top);
                (s_Bot, s_BotPending) = (s_BotPending, s_Bot);
                s_Params = s_ParamsPending;
                s_HasData = true;
            }
        }

        /// <summary>
        /// Computes visibility [0,1] of a world position using the same
        /// rules as the visual effect (cone + proximity + height-map march).
        /// Returns false while no readback data is available yet.
        /// </summary>
        public static bool TryComputeVisibility(Vector3 worldPos, out float visibility)
        {
            visibility = 1f;
            if (!s_HasData)
                return false;

            FrameParams P = s_Params;
            int res = P.mapResolution;

            Vector2 dP = new Vector2(worldPos.x - P.eyeXZ.x, worldPos.z - P.eyeXZ.y);
            float dist = dP.magnitude;
            Vector2 dirW = dist > 1e-4f ? dP / dist : Vector2.right;

            // Height-map march (mirrors the VisibilityMask pass in the
            // shader): traverses the 3D eye->target segment, so overhead
            // targets behind a roof are occluded correctly.
            Vector3 eyePos = new Vector3(P.eyeXZ.x, P.eyeY, P.eyeXZ.y);
            Vector3 seg = worldPos - eyePos;
            float len3D = Mathf.Max(seg.magnitude, 1e-4f);
            float block = 0f;
            float boost = Mathf.Max(P.surfaceBoost, 1e-3f);
            {
                int steps = Mathf.Clamp(Mathf.CeilToInt(len3D / P.stepWorld), 4, P.maxSteps);
                for (int j = 0; j < steps; j++)
                {
                    float tSeg = (j + 0.5f) / steps;
                    float travelled = tSeg * len3D;
                    if (travelled < 0.08f)
                        continue;

                    Vector3 p = eyePos + seg * tSeg;
                    float u = (p.x - P.center.x) / P.radius * 0.5f + 0.5f;
                    float v = (p.z - P.center.y) / P.radius * 0.5f + 0.5f;
                    int tx = Mathf.Clamp((int)(u * res), 0, res - 1);
                    int ty = Mathf.Clamp((int)(v * res), 0, res - 1);
                    if (FlipReadbackY)
                        ty = res - 1 - ty;

                    int idx = ty * res + tx;
                    float top = Mathf.HalfToFloat(s_Top[idx]);
                    if (top > -5000f)
                    {
                        float bottomRaw = Mathf.HalfToFloat(s_Bot[idx]);
                        float bot = bottomRaw > 4000f ? -100000f : bottomRaw;

                        float pRel = (p.y - P.eyeY) + P.slopeBias * (tSeg * dist);
                        float eps = Mathf.Max(0.02f, P.softness * travelled);

                        float hit = Smoothstep(top + eps, top - eps, pRel)
                                  * Smoothstep(bot - eps, bot + eps, pRel);
                        hit *= Smoothstep(0f, boost, len3D - travelled);

                        if (hit > block)
                            block = hit;
                        if (block > 0.997f)
                            break;
                    }
                }
            }
            float shadow = 1f - block;

            // Cone + range + proximity (mirrors the shader)
            float angDiff = Mathf.Acos(Mathf.Clamp(Vector2.Dot(dirW, P.viewDirXZ), -1f, 1f));
            float cone = 1f - Smoothstep(P.halfAngle - P.angleFeather, P.halfAngle, angDiff);
            float range = 1f - Smoothstep(P.viewDistance - P.rangeFeather, P.viewDistance, dist);
            float prox = 1f - Smoothstep(P.proximityRadius - P.proximityFeather, P.proximityRadius, dist);

            float vis = Mathf.Max(cone * range, prox) * shadow;
            if (P.proximityThroughWalls)
                vis = Mathf.Max(vis, prox);

            visibility = Mathf.Clamp01(vis);
            return true;
        }

        static float Smoothstep(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }
    }
}
