// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

Shader "Sb/OutlineGlow"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.0, 0.8, 1.0, 1.0)
        _Thickness ("Thickness (world units)", Range(0.0, 0.1)) = 0.02
        _Alpha ("Alpha", Range(0,1)) = 1
        _EnvironmentDepthBias ("Environment depth bias", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

        Cull Front
        ZWrite Off
        ZTest LEqual
        // Coverage alpha over the passthrough underlay: colour by alpha, alpha = a + dst * (1 - a) (not a * a). See PlacementHologram.
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // The outline hugs a virtual object, so a real surface in front of the object hides the outline too (Meta Depth API).
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "UnityCG.cginc"
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #endif

            fixed4 _OutlineColor;
            float _Thickness;
            float _Alpha;
            float _EnvironmentDepthBias;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                META_DEPTH_VERTEX_OUTPUT(0)
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // World-space normal extrusion keeps outline thickness consistent in world units
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz + worldNormal * _Thickness;
                o.pos = UnityWorldToClipPos(worldPos);
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                o.posWorld = worldPos;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 col = _OutlineColor;
                col.a *= _Alpha;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                col.a *= META_DEPTH_GET_OCCLUSION_VALUE(i, _EnvironmentDepthBias);
                #endif
                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}


