// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

// Sb/Annotation/Line
// Constant-pixel-width, anti-aliased line segments drawn as one quad each (one draw call for a whole box).
// The mesh is built in world space by AnnotationVisual. Each vertex carries its own end (POSITION), the other end of the
// segment (NORMAL) and a side/end flag (TEXCOORD0); the vertex shader pushes the corners out in screen space, so the
// line is `_WidthPx` pixels wide at any distance, and the fragment shader draws a capsule signed-distance field:
// a crisp core, a dark hairline casing (keeps the line readable on bright passthrough) and a soft glow.
// No textures, no lighting, no bloom pass.
Shader "Sb/Annotation/Line"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _CoreColor ("Core colour", Color) = (1,1,1,1)
        _CoreMix ("Core whiteness", Range(0,1)) = 0.35
        _WidthPx ("Core width (px)", Float) = 2.5
        _GlowPx ("Glow extent (px)", Float) = 5
        _GlowAlpha ("Glow alpha", Range(0,1)) = 0.5
        _CasingColor ("Casing colour (a = strength)", Color) = (0.02,0.12,0.1,0.5)
        _CasingPx ("Casing width (px)", Float) = 1.4
        _DepthBias ("Depth bias (ndc)", Float) = 0.0004
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        _EnvironmentDepthBias ("Environment depth bias", Float) = 0.06
    }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest [_ZTest]
        // Coverage alpha over the passthrough underlay: colour by alpha, alpha = a + dst * (1 - a) (not a * a). See PlacementHologram.
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // Passthrough occlusion by the Meta Depth API (real furniture hides the line). Off in the editor.
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "UnityCG.cginc"
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #endif

            fixed4 _Color, _CoreColor, _CasingColor;
            float _CasingPx;
            float _CoreMix, _WidthPx, _GlowPx, _GlowAlpha, _DepthBias, _EnvironmentDepthBias;

            struct appdata
            {
                float3 pos : POSITION;       // this end, world space
                float3 other : NORMAL;       // the other end, world space
                float2 side : TEXCOORD0;     // x: -1/+1 across the line, y: 0 = segment start, 1 = segment end
                float2 info : TEXCOORD1;     // x: segment length (m), y: width scale
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 q : TEXCOORD0;        // (across px, along px, 1) * w, so the fragment can undo perspective
                float2 params : TEXCOORD1;   // x: segment px length, y: core half width px
                fixed4 color : COLOR;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                META_DEPTH_VERTEX_OUTPUT(2)
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 cT = mul(UNITY_MATRIX_VP, float4(v.pos, 1));
                float4 cO = mul(UNITY_MATRIX_VP, float4(v.other, 1));
                float wT = max(cT.w, 1e-4);
                float wO = max(cO.w, 1e-4);
                float2 halfScr = 0.5 * _ScreenParams.xy;
                float2 sT = cT.xy / wT * halfScr;
                float2 sO = cO.xy / wO * halfScr;

                float2 d = sO - sT;
                float len = length(d);
                d = len > 1e-4 ? d / len : float2(1, 0);
                bool isStart = v.side.y < 0.5;
                float2 dirSeg = isStart ? d : -d;           // screen direction start -> end
                float2 nrm = float2(-dirSeg.y, dirSeg.x);

                float coreHalf = 0.5 * _WidthPx * v.info.y;
                float ext = coreHalf + max(_GlowPx, _CasingPx) + 1.0;        // +1px for the anti-aliasing fringe
                float2 off = nrm * (v.side.x * ext) + dirSeg * (isStart ? -ext : ext);

                o.pos = cT;
                o.pos.xy += off / halfScr * cT.w;
                #if UNITY_REVERSED_Z
                o.pos.z += _DepthBias * cT.w;
                #else
                o.pos.z -= _DepthBias * cT.w;
                #endif

                float segPx = len;
                float across = v.side.x * ext;
                float along = isStart ? -ext : segPx + ext;
                o.q = float3(across, along, 1.0) * cT.w;
                o.params = float2(segPx, coreHalf);
                o.color = v.color;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, float4(v.pos, 1));
                #endif
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float across = i.q.x / i.q.z;
                float along = i.q.y / i.q.z;
                float segPx = i.params.x;
                float coreHalf = i.params.y;

                float dAlong = along - clamp(along, 0.0, segPx);
                float dist = length(float2(across, dAlong));      // capsule distance, in pixels
                float core = saturate(coreHalf - dist + 0.5);
                float glow = saturate(1.0 - max(dist - coreHalf, 0.0) / max(_GlowPx, 0.001));
                glow = glow * glow * _GlowAlpha;

                fixed3 tint = i.color.rgb * _Color.rgb;
                fixed3 coreRgb = lerp(tint, _CoreColor.rgb, _CoreMix);
                // layers, back to front: glow (accent), casing (dark hairline keeps the line readable on bright passthrough), core
                float cas = saturate(coreHalf + _CasingPx - dist + 0.5) * _CasingColor.a;
                float a2 = cas + glow * (1.0 - cas);
                fixed3 rgb2 = (_CasingColor.rgb * cas + tint * glow * (1.0 - cas)) / max(a2, 1e-4);
                float a = core + a2 * (1.0 - core);
                fixed3 rgb = (coreRgb * core + rgb2 * a2 * (1.0 - core)) / max(a, 1e-4);
                float alpha = a * i.color.a * _Color.a;

                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                alpha *= META_DEPTH_GET_OCCLUSION_VALUE(i, _EnvironmentDepthBias);
                #endif
                return fixed4(rgb, alpha);
            }
            ENDCG
        }
    }
}
