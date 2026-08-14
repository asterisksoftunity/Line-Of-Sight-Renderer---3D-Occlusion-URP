// Line of Sight Renderer
// Copyright (c) 2026 アスタリスクSoft (Asterisk Soft). All rights reserved.
//
// Occluder splat shaders. Pass 0 renders occluder geometry into the top and
// bottom height maps (MRT, Max/Min blending); pass 1 splats terrain
// heightmaps. Undersides come only from downward facing surfaces; columns
// without one are treated as solid to the ground.

Shader "Hidden/LineOfSight/Occluder"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "LOS Occluder Height Splat"
            Cull Off
            ZWrite Off
            ZTest Always

            Blend 0 One One
            BlendOp 0 Max
            ColorMask R 0

            Blend 1 One One
            BlendOp 1 Min
            ColorMask R 1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _LOS_EyeY;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  worldY     : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            struct FragOut
            {
                half top    : SV_Target0;
                half bottom : SV_Target1;
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(posWS);
                o.worldY = posWS.y;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            FragOut frag(Varyings input)
            {
                half rel = (half)clamp(input.worldY - _LOS_EyeY, -4000.0, 4000.0);
                FragOut o;
                o.top = rel;
                // Only genuinely downward-facing surfaces define a REAL
                // underside you could see beneath. Everything else writes
                // the +10000 sentinel, which loses the Min blend whenever a
                // real underside exists and otherwise marks the column
                // "solid to the ground" (bottomless/hollow wall meshes).
                // Fringe columns that miss the underside face (tilt/warp
                // overhangs, rasterization stragglers) are repaired by the
                // propagation pass, which spreads real undersides across
                // connected geometry.
                o.bottom = input.normalWS.y < -0.25 ? rel : half(10000.0);
                return o;
            }
            ENDHLSL
        }
        // Pass 1: Terrain
        Pass
        {
            Name "LOS Terrain Splat"
            Cull Off
            ZWrite Off
            ZTest Always

            Blend 0 One One
            BlendOp 0 Max
            ColorMask R 0

            Blend 1 One One
            BlendOp 1 Min
            ColorMask R 1

            HLSLPROGRAM
            #pragma vertex vertTerrain
            #pragma fragment fragTerrain
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _LOS_EyeY;
            TEXTURE2D(_LOS_TerrainHeight);
            SAMPLER(sampler_LOS_TerrainHeight);
            float4 _LOS_TerrainRect;   // x: min world X, y: min world Z, z: size X, w: size Z
            float4 _LOS_TerrainParams; // x: terrain world Y, y: height scale (2 * size.y), z: capture height bias, w: unused

            struct TerrainVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct TerrainFragOut
            {
                half top    : SV_Target0;
                half bottom : SV_Target1;
            };

            // Procedural quad (2 triangles) spanning the terrain's world
            // rect, drawn while the capture view/projection is bound.
            TerrainVaryings vertTerrain(uint vid : SV_VertexID)
            {
                const float2 corners[6] = {
                    float2(0, 0), float2(1, 0), float2(1, 1),
                    float2(0, 0), float2(1, 1), float2(0, 1)
                };
                float2 c = corners[vid];
                float3 posWS = float3(
                    _LOS_TerrainRect.x + c.x * _LOS_TerrainRect.z,
                    _LOS_EyeY, // any height inside the capture range; ZTest Always
                    _LOS_TerrainRect.y + c.y * _LOS_TerrainRect.w);

                TerrainVaryings o;
                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = c;
                return o;
            }

            TerrainFragOut fragTerrain(TerrainVaryings input)
            {
                float h = SAMPLE_TEXTURE2D_LOD(_LOS_TerrainHeight, sampler_LOS_TerrainHeight, input.uv, 0).r;
                // Bias lowers captured terrain so flat ground never catches
                // grazing sight rays - only terrain that RISES occludes.
                float worldY = _LOS_TerrainParams.x + h * _LOS_TerrainParams.y - _LOS_TerrainParams.z;

                TerrainFragOut o;
                o.top = (half)clamp(worldY - _LOS_EyeY, -4000.0, 4000.0);
                o.bottom = half(10000.0); // terrain is solid to the ground
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
