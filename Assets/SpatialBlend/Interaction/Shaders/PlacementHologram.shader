// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

Shader "Sb/PlacementHologram"
{
    // The drop preview's ghost (PlacementPreview): a translucent copy of the held object where the drop lands. Unlit, a faint fill
    // with a fresnel rim and a slow, subtle scanline, tinted by the preview's state. Built-in pipeline, single-pass instanced stereo
    // (OpenXR render mode 1). Its alpha channel is written as coverage, which the passthrough underlay is composited by.
    Properties
    {
        _Color ("Tint", Color) = (0.45, 0.95, 0.85, 1)
        _Fill ("Fill alpha", Range(0, 1)) = 0.12
        _Rim ("Rim alpha", Range(0, 2)) = 0.85
        _RimPower ("Rim power", Range(0.5, 8)) = 2.5
        _Lines ("Scanlines per metre", Float) = 40
        _LineAlpha ("Scanline alpha", Range(0, 1)) = 0.08
        _EnvironmentDepthBias ("Environment depth bias", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Cull Back
        ZWrite Off
        ZTest LEqual
        // Colour blends by the hologram's alpha; the ALPHA channel accumulates coverage (a + dst * (1 - a)) instead of a * a. The
        // Quest compositor lays the eye buffer over the passthrough underlay by its alpha: with plain SrcAlpha the buffer's alpha
        // was a squared (0.12 fill -> 0.014), so the passthrough replaced the hologram and only a faint tint showed on the device.
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // A real surface in front of the landing spot hides the ghost as it would hide the object (Meta Depth API).
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "UnityCG.cginc"
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #endif

            fixed4 _Color;
            float _Fill;
            float _Rim;
            float _RimPower;
            float _Lines;
            float _LineAlpha;
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
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
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
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(worldPos);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = worldPos;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                o.posWorld = worldPos;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.worldNormal);
                float3 v = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                float rim = pow(1.0 - saturate(abs(dot(n, v))), _RimPower);
                float scan = step(0.5, frac(i.worldPos.y * _Lines - _Time.y * 0.5)) * _LineAlpha;
                fixed4 col = _Color;
                col.rgb = lerp(col.rgb, 1.0, rim * 0.35);
                col.a = saturate(_Color.a * (_Fill + rim * _Rim + scan));
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
