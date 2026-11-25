// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

// Sb/Annotation/Face
// Translucent faces for a unit cube scaled to the box: a faint fill that fades toward the top, and a soft glow that hugs the
// box edges (measured in metres from the object scale, so it is the same width on every face). Back faces are drawn first,
// dimmer, then front faces. Unlit; two cheap passes over only the box's own pixels.
Shader "Sb/Annotation/Face"
{
    Properties
    {
        _FillColor ("Fill colour", Color) = (0.12,0.66,0.55,1)
        _FillAlpha ("Fill alpha", Range(0,1)) = 0.075
        _EdgeAlpha ("Edge glow alpha", Range(0,1)) = 0.26
        _EdgeWidth ("Edge glow width (m)", Float) = 0.04
        _BackFactor ("Back face alpha factor", Range(0,1)) = 0.5
        _TopFade ("Alpha at the top (1 = none)", Range(0,1)) = 0.5
        _EnvironmentDepthBias ("Environment depth bias", Float) = 0.06
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        // Coverage alpha over the passthrough underlay: colour by alpha, alpha = a + dst * (1 - a) (not a * a). See PlacementHologram.
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        CGINCLUDE
        #include "UnityCG.cginc"
        #pragma multi_compile_instancing
        // Passthrough occlusion by the Meta Depth API (real furniture hides the face). Off in the editor.
        #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
        #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
        #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
        #endif

        fixed4 _FillColor;
        float _FillAlpha, _EdgeAlpha, _EdgeWidth, _BackFactor, _TopFade, _EnvironmentDepthBias;

        struct appdata
        {
            float4 vertex : POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct v2f
        {
            float4 pos : SV_POSITION;
            float3 lp : TEXCOORD0;       // local position, unit cube -0.5..0.5
            float3 scale : TEXCOORD1;    // box size in metres
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            META_DEPTH_VERTEX_OUTPUT(2)
            #endif
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vertFace (appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_OUTPUT(v2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.pos = UnityObjectToClipPos(v.vertex);
            o.lp = v.vertex.xyz;
            o.scale = float3(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22));
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, v.vertex);
            #endif
            return o;
        }

        float median3(float a, float b, float c) { return max(min(a, b), min(max(a, b), c)); }

        fixed4 shade (v2f i, float backScale)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float3 halfExt = 0.5 * i.scale;
            float3 d = halfExt - abs(i.lp * i.scale);             // metres to each pair of faces
            float edge = median3(d.x, d.y, d.z);                  // metres from the nearest box edge, along this face
            float glow = exp(-max(edge, 0.0) / max(_EdgeWidth, 1e-4));
            float topFade = lerp(1.0, _TopFade, saturate(i.lp.y + 0.5));
            float a = (_FillAlpha * topFade + _EdgeAlpha * glow) * backScale;
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            a *= META_DEPTH_GET_OCCLUSION_VALUE(i, _EnvironmentDepthBias);
            #endif
            return fixed4(_FillColor.rgb, saturate(a));
        }
        ENDCG

        Pass
        {
            Cull Front
            CGPROGRAM
            #pragma vertex vertFace
            #pragma fragment fragBack
            fixed4 fragBack (v2f i) : SV_Target { return shade(i, _BackFactor); }
            ENDCG
        }
        Pass
        {
            Cull Back
            CGPROGRAM
            #pragma vertex vertFace
            #pragma fragment fragFront
            fixed4 fragFront (v2f i) : SV_Target { return shade(i, 1.0); }
            ENDCG
        }
    }
}
