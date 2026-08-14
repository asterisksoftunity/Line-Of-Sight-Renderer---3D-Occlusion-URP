// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Post processing passes. Indices must match LineOfSightRenderFeature:
//   0 VisibilityMask (per pixel sight ray march), 1 DirectionalBlur,
//   2 Temporal, 3 Composite, 4 DilateTop, 5 DilateBottom, 6 BottomPropagate.

Shader "Hidden/LineOfSight/Post"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        #define LOS_TWO_PI 6.28318530718
        #define LOS_EMPTY  -5000.0   // height-map texels below this are "no occluder"

        TEXTURE2D(_LOS_OccluderTex);   // R = column top height, relative to eye
        TEXTURE2D(_LOS_BottomTex);     // R = column underside height (sentinel > 4000 = solid to ground)
        TEXTURE2D(_LOS_MaskTex);
        TEXTURE2D(_LOS_RawMaskTex);
        TEXTURE2D(_LOS_HistoryTex);

        float4x4 _LOS_InvVP;       // camera inverse view-projection (GPU proj)
        float4   _LOS_Center;      // xy: snapped capture center XZ, z: capture radius, w: 1/radius
        float4   _LOS_Player;      // xy: eye XZ, zw: view direction XZ (normalized)
        float    _LOS_EyeY;        // eye world height (heights in the captures are relative to this)
        float4   _LOS_Cone;        // x: half angle (rad), y: angular feather (rad), z: view distance, w: range feather (world)
        float4   _LOS_Prox;        // x: proximity radius, y: feather, z: sees-through-walls flag, w: occluder surface boost
        float4   _LOS_Shadow;      // x: penumbra softness (slope), y: facing shadow strength, z: slope bias, w: affect sky flag
        float4   _LOS_March;       // x: step length (world), y: max steps, z: start skip (world), w: 1/occluder map resolution
        float4   _LOS_Temporal;    // x: reveal lerp factor, y: hide lerp factor, z: history valid flag
        float4   _LOS_DepthSize;   // xy: camera depth texture size, zw: 1/size
        float4   _LOS_BlurParams;  // xy: blur offset in UV
        float4   _LOS_HiddenColor; // rgb: tint (linear), a: intensity
        float    _LOS_Desat;
        float    _LOS_Debug;

        float InterleavedGradientNoise(float2 pixel)
        {
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }

        // Shared sight-ray march. Returns blocking amount [0,1] and reports
        // the strongest hit's raw underside value and distance so the
        // MarchDiagnostic debug view can attribute WHY a pixel was blocked.
        // Marches the 3D segment from the eye to the target, testing the
        // ray height against each occluder column's [bottom, top] interval.
        //
        // Parameterized by fraction t of the SEGMENT, not by horizontal
        // distance: a target directly overhead has ~zero horizontal span, so
        // a horizontal parameterization degenerates and cannot represent a
        // vertical sightline (roofs then never block what is above you).
        float LosMarchBlock(float3 posWS, float2 dirW, float dist, float2 pixelCoord,
                            out float hitBottomRaw, out float hitDist)
        {
            hitBottomRaw = 0.0;
            hitDist = 0.0;

            float3 eyePos = float3(_LOS_Player.x, _LOS_EyeY, _LOS_Player.y);
            float3 seg = posWS - eyePos;
            float len3D = max(length(seg), 1e-4);
            float block = 0.0;

            int steps = (int)clamp(ceil(len3D / _LOS_March.x), 4.0, _LOS_March.y);
            float ign = InterleavedGradientNoise(pixelCoord);

            // World-space slack along the ray: occluder surfaces within
            // "boost" of the target do not block it, so the target's own
            // surface and the near faces of walls stay lit.
            float boost = max(_LOS_Prox.w, 1e-3);

            [loop]
            for (int j = 0; j < steps; j++)
            {
                float tSeg = (j + ign) / steps;
                float travelled = tSeg * len3D;
                if (travelled < _LOS_March.z)
                    continue;

                float3 p = eyePos + seg * tSeg;
                float2 uvH = (p.xz - _LOS_Center.xy) * (_LOS_Center.w * 0.5) + 0.5;
                float top = SAMPLE_TEXTURE2D_LOD(_LOS_OccluderTex, sampler_PointClamp, uvH, 0).r;
                if (top > LOS_EMPTY)
                {
                    float bottomRaw = SAMPLE_TEXTURE2D_LOD(_LOS_BottomTex, sampler_PointClamp, uvH, 0).r;
                    float bot = bottomRaw > 4000.0 ? -100000.0 : bottomRaw;

                    // Ray height at this point, plus the slope bias applied
                    // over the horizontal distance travelled.
                    float pRel = (p.y - _LOS_EyeY) + _LOS_Shadow.z * (tSeg * dist);
                    float eps = max(0.02, _LOS_Shadow.x * travelled);

                    float hit = smoothstep(top + eps, top - eps, pRel)
                              * smoothstep(bot - eps, bot + eps, pRel);

                    // Fade blocking out within "boost" of the target.
                    hit *= smoothstep(0.0, boost, len3D - travelled);

                    if (hit > block)
                    {
                        block = hit;
                        hitBottomRaw = bottomRaw;
                        hitDist = travelled;
                    }
                    if (block > 0.997)
                        break;
                }
            }
            return block;
        }
        ENDHLSL

        // Pass 0
        Pass
        {
            Name "LOS VisibilityMask"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragVisibility

            float4 FragVisibility(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                // Snap to the nearest full-res depth texel CENTER. At even
                // downsample factors, mask texel centers land exactly on
                // depth texel BOUNDARIES, and point-sampling tie-breaks
                // flip row-coherently -> solid horizontal line artifacts.
                // Snapping removes the ambiguity at every downsample factor
                // and pairs the reconstructed position exactly with the
                // sampled depth texel.
                uv = (floor(uv * _LOS_DepthSize.xy + 0.25) + 0.5) * _LOS_DepthSize.zw; // +0.25 breaks integer-boundary floor ties (even downsamples land exactly on texel edges)
                float rawDepth = SampleSceneDepth(uv);

                #if UNITY_REVERSED_Z
                    float deviceDepth = rawDepth;
                    bool isSky = rawDepth < 1e-6;
                #else
                    bool isSky = rawDepth > 1.0 - 1e-6;
                    float deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif

                if (isSky)
                    return _LOS_Shadow.w > 0.5 ? 0.0 : 1.0;

                float3 posWS = ComputeWorldSpacePosition(uv, deviceDepth, _LOS_InvVP);

                float2 dP = posWS.xz - _LOS_Player.xy;
                float dist = length(dP);
                float2 dirW = dist > 1e-4 ? dP / dist : float2(1, 0);

                // Exact 3D occlusion: march the eye->pixel sight ray
                //      across the height-interval maps and test it against
                //      every occupied column on the way. The march stops
                //      "surface boost" short of the pixel so the visible
                //      faces of occluders are themselves lit.
                float hitBottomRaw, hitDist;
                float block = LosMarchBlock(posWS, dirW, dist, uv * _LOS_DepthSize.xy, hitBottomRaw, hitDist);
                float shadowTerm = 1.0 - block;

                // Surface facing: a surface can only be seen if it faces the
                // eye - hides roof tops, wall top rims and wall back sides.
                // Gated to pixels that ARE an occluder surface (their height
                // matches the captured interval in their column), so a
                // non-occluder object standing UNDER a roof is not darkened
                // by the roof's presence in the same column.
                // Strength in _LOS_Shadow.y (0 disables).
                float2 uvP = (posWS.xz - _LOS_Center.xy) * (_LOS_Center.w * 0.5) + 0.5;
                float isOccluderSurface = 0.0;
                if (all(uvP == saturate(uvP)))
                {
                    float colTop = SAMPLE_TEXTURE2D_LOD(_LOS_OccluderTex, sampler_PointClamp, uvP, 0).r;
                    float colBotRaw = SAMPLE_TEXTURE2D_LOD(_LOS_BottomTex, sampler_PointClamp, uvP, 0).r;
                    float pixRel = posWS.y - _LOS_EyeY;
                    // This pixel counts as occluder geometry if its height
                    // lies within the column's captured interval (not merely
                    // at its exact top/bottom): a roof surface with an object
                    // standing on it sits BELOW the column top, yet is still
                    // occluder geometry that must obey the facing test.
                    // A non-occluder standing under a roof is far below the
                    // interval, so it is still exempt.
                    float colBot = (colBotRaw < 4000.0) ? colBotRaw : -100000.0;
                    if (colTop > LOS_EMPTY)
                        isOccluderSurface = (pixRel > colBot - 0.15 && pixRel < colTop + 0.15) ? 1.0 : 0.0;
                }

                float3 geoN = normalize(cross(ddy(posWS), ddx(posWS)));
                float3 toCam = normalize(_WorldSpaceCameraPos.xyz - posWS);
                geoN = dot(geoN, toCam) < 0.0 ? -geoN : geoN; // visible surfaces face the camera
                float3 toEye = normalize(float3(_LOS_Player.x, _LOS_EyeY, _LOS_Player.y) - posWS);
                float facing = smoothstep(-0.12, 0.05, dot(geoN, toEye));
                shadowTerm *= lerp(1.0, facing, isOccluderSurface * saturate(_LOS_Shadow.y));

                // FOV cone + range
                float angDiff = acos(clamp(dot(dirW, _LOS_Player.zw), -1.0, 1.0));
                float cone = 1.0 - smoothstep(_LOS_Cone.x - _LOS_Cone.y, _LOS_Cone.x, angDiff);
                float rangeT = 1.0 - smoothstep(_LOS_Cone.z - _LOS_Cone.w, _LOS_Cone.z, dist);
                float coneVis = cone * rangeT;

                // Omnidirectional proximity circle (also 3D-shadowed)
                float prox = 1.0 - smoothstep(_LOS_Prox.x - _LOS_Prox.y, _LOS_Prox.x, dist);

                float vis = max(coneVis, prox) * shadowTerm;
                // optionally let proximity ignore walls (x-ray circle)
                vis = lerp(vis, max(vis, prox), step(0.5, _LOS_Prox.z));

                return float4(vis, vis, vis, 1);
            }
            ENDHLSL
        }

        // Pass 1
        Pass
        {
            Name "LOS DirectionalBlur"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur

            float SampleMask(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, saturate(uv), 0).r;
            }

            float4 FragBlur(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 o1 = _LOS_BlurParams.xy * 1.3846153846;
                float2 o2 = _LOS_BlurParams.xy * 3.2307692308;
                float2 uv = input.texcoord;
                float r = SampleMask(uv) * 0.2270270270
                        + (SampleMask(uv + o1) + SampleMask(uv - o1)) * 0.3162162162
                        + (SampleMask(uv + o2) + SampleMask(uv - o2)) * 0.0702702703;
                return float4(r, r, r, 1);
            }
            ENDHLSL
        }

        // Pass 2
        Pass
        {
            Name "LOS Temporal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTemporal

            float4 FragTemporal(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float cur = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).r;

                if (_LOS_Temporal.z < 0.5)
                    return float4(cur.xxx, 1);

                // Reprojection-free temporal smoothing.
                //
                // The visibility mask is a fresh function of the world every
                // frame: "cur" is always geometrically correct for this
                // pixel right now. We only want to SOFTEN how fast a pixel
                // transitions between revealed and hidden - not to carry old
                // world data around the screen. So history is read at the
                // SAME uv (no camera-matrix reprojection), which makes screen
                // seams impossible at any resolution or camera motion - the
                // reprojected approach produced fade-on-move seams wherever
                // last frame's history didn't map cleanly to this frame.
                //
                // Trade-off: during fast camera moves a pixel's history
                // briefly belonged to different world content, giving mild
                // transient ghosting (bounded by Hide Time). For top-down /
                // isometric cameras that are static or slow-following, this
                // is imperceptible, and it is strictly cheaper (one texture
                // read; no depth reconstruct, no matrix multiply).
                float hist = SAMPLE_TEXTURE2D_LOD(_LOS_HistoryTex, sampler_LinearClamp, uv, 0).r;
                float k = cur > hist ? _LOS_Temporal.x : _LOS_Temporal.y;
                float outv = lerp(hist, cur, saturate(k));
                return float4(outv.xxx, 1);
            }
            ENDHLSL
        }

        // Pass 3
        Pass
        {
            Name "LOS Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            float4 FragComposite(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float4 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
                float vis = saturate(SAMPLE_TEXTURE2D_LOD(_LOS_MaskTex, sampler_LinearClamp, uv, 0).r);

                int dbg = (int)_LOS_Debug;
                if (dbg == 1) // VisibilityMask (final, after temporal)
                    return float4(vis.xxx, 1);
                if (dbg == 2) // OccluderHeightMap (top-down; screen right = +X, screen up = +Z)
                {
                    // Point sampling: show exactly what the march sees -
                    // bilinear here would blend edge texels with the empty
                    // clear value into misleading colors.
                    float top = SAMPLE_TEXTURE2D_LOD(_LOS_OccluderTex, sampler_PointClamp, uv, 0).r;
                    if (top <= LOS_EMPTY)
                        return float4(0.05, 0.05, 0.05, 1);
                    float bottomRaw = SAMPLE_TEXTURE2D_LOD(_LOS_BottomTex, sampler_PointClamp, uv, 0).r;
                    bool seeUnder = bottomRaw < 4000.0; // blue = real underside (native or propagated), rays can pass under
                    // green = occluder top above eye level, red = below
                    float t = saturate(top * 0.15);
                    return float4(saturate(-top * 0.3), 0.25 + 0.75 * t, seeUnder ? 0.9 : 0.15, 1);
                }
                if (dbg == 3) // VisibilityMaskRaw (before temporal - compare with 1 to verify the lag)
                {
                    float raw = saturate(SAMPLE_TEXTURE2D_LOD(_LOS_RawMaskTex, sampler_LinearClamp, uv, 0).r);
                    return float4(raw.xxx, 1);
                }
                if (dbg == 4) // MarchDiagnostic: WHY and WHERE pixels get blocked
                {
                    // RED   = blocked by a paper-thin column (solid rule) -> capture defect class
                    // GREEN = blocked by a genuinely thick interval        -> ray/data expectation class
                    // BLUE  = hit position along the ray (dim = near player, bright = near the pixel)
                    float dRaw = SampleSceneDepth(uv);
                    #if UNITY_REVERSED_Z
                        float dd = dRaw;
                        bool sky = dRaw < 1e-6;
                    #else
                        bool sky = dRaw > 1.0 - 1e-6;
                        float dd = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, dRaw);
                    #endif
                    if (sky)
                        return float4(0.05, 0.05, 0.05, 1);

                    float3 pWS = ComputeWorldSpacePosition(uv, dd, _LOS_InvVP);
                    float2 dP = pWS.xz - _LOS_Player.xy;
                    float dist = length(dP);
                    float2 dirW = dist > 1e-4 ? dP / dist : float2(1, 0);

                    float hitBottomRaw, hitDist;
                    float b = LosMarchBlock(pWS, dirW, dist, uv * _LOS_DepthSize.xy, hitBottomRaw, hitDist);
                    if (b < 0.02)
                        return float4(0.0, 0.0, 0.12, 1); // unblocked
                    bool solidRule = hitBottomRaw > 4000.0; // no real underside -> solid-to-ground hit
                    return float4(solidRule ? b : 0.0,
                                  solidRule ? 0.0 : b,
                                  saturate(hitDist / max(dist, 0.01)),
                                  1);
                }

                float3 hidden = lerp(scene.rgb, Luminance(scene.rgb).xxx, _LOS_Desat) * _LOS_HiddenColor.rgb;
                float3 col = lerp(scene.rgb, hidden, saturate(_LOS_HiddenColor.a * (1.0 - vis)));
                // Interleaved gradient noise dither breaks up 8-bit banding
                // in the smooth light-to-dark gradients.
                float ign = InterleavedGradientNoise(uv * _LOS_DepthSize.xy);
                col += (ign - 0.5) * (1.5 / 255.0);

                return float4(col, scene.a);
            }
            ENDHLSL
        }
        // Pass 4
        Pass
        {
            Name "LOS DilateTop"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDilateTop

            // 3x3 MAX of the top map. Empty texels are the clear value
            // (-10000), so max naturally ignores them AND grows occupied
            // footprints by one texel. This repairs the degenerate border
            // texels left by opposite-winding top/bottom faces resolving
            // rasterization fill rules differently (which otherwise become
            // zero-thickness "solid" pillars that graze rays into streaks).
            float4 FragDilateTop(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float px = _LOS_March.w;
                float best = -10000.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float v = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp,
                            input.texcoord + float2(x, y) * px, 0).r;
                        best = max(best, v);
                    }
                }
                return float4(best, 0, 0, 1);
            }
            ENDHLSL
        }

        // Pass 5
        Pass
        {
            Name "LOS DilateBottom"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDilateBottom

            // Fill-only 3x3 pass on the bottom map: a texel with a real
            // underside keeps it; texels without one (including the ring
            // grown by DilateTop) adopt the lowest real underside among
            // their neighbors. Same rule as the propagation pass - an
            // unconditional min would smear grounded bottoms into adjacent
            // see-under intervals.
            float4 FragDilateBottom(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float px = _LOS_March.w;

                float self = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0).r;
                if (self < 4000.0)
                    return float4(self, 0, 0, 1);

                float best = 10000.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float v = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp,
                            input.texcoord + float2(x, y) * px, 0).r;
                        best = min(best, v);
                    }
                }
                return float4(best, 0, 0, 1);
            }
            ENDHLSL
        }
        // Pass 6
        Pass
        {
            Name "LOS BottomPropagate"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBottomPropagate

            // Fill-only underside flood: a column that already has a REAL
            // captured underside keeps it unconditionally. Only sentinel
            // columns (occupied but missing underside data, e.g. the fringe
            // of tilted/warped geometry whose bottom face doesn't reach)
            // adopt the lowest underside among their occupied neighbors.
            // Occupancy comes from the original top map, so values never
            // hop across empty ground to a different object. Iterated N
            // times by the feature.
            // CRITICAL: propagation must never REPLACE a real underside -
            // an unconditional min would flood a grounded pillar's bottom
            // across a connected roof, turning the roof around every pillar
            // into a false ground-to-roof wall.
            // Freestanding hollow walls are unaffected - no column in them
            // has a real underside, so nothing propagates and the +10000
            // sentinel (solid to ground) survives.
            float4 FragBottomPropagate(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float px = _LOS_March.w;

                float selfTop = SAMPLE_TEXTURE2D_LOD(_LOS_OccluderTex, sampler_PointClamp, input.texcoord, 0).r;
                if (selfTop <= LOS_EMPTY)
                    return float4(10000.0, 0, 0, 1); // empty stays empty

                float selfBot = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0).r;
                if (selfBot < 4000.0)
                    return float4(selfBot, 0, 0, 1); // real underside is authoritative

                float best = 10000.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 o = float2(x, y) * px;
                        float nTop = SAMPLE_TEXTURE2D_LOD(_LOS_OccluderTex, sampler_PointClamp, input.texcoord + o, 0).r;
                        if (nTop > LOS_EMPTY)
                        {
                            float nBot = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord + o, 0).r;
                            best = min(best, nBot);
                        }
                    }
                }
                return float4(best, 0, 0, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
