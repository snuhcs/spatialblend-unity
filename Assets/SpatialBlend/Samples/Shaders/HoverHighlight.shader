// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

Shader "SpatialBlend/HoverHighlight"
{
    Properties
    {
        _Color ("Color", Color) = (0.1, 0.8, 1, 1)
        _Emission ("Emission Intensity", Range(0, 5)) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue"      = "Transparent"
        }

        LOD 100
        Cull Back
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // VR / instancing friendly
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Emission)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
            #ifdef LIGHTMAP_ON
                float2 uv : TEXCOORD0;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_FOG_COORDS(0)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 color = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  emission = UNITY_ACCESS_INSTANCED_PROP(Props, _Emission);

                fixed4 c = color;
                c.rgb *= emission;

                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDHLSL
        }
    }
}


