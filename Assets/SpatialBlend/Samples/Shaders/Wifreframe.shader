// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

Shader "Custom/Wireframe"
{
    Properties
    {
        _Color ("Wire Color", Color) = (0,1,1,0.9)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma geometry geom
            #pragma target 4.0
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2g { float4 pos : POSITION; };
            struct g2f { float4 pos : SV_POSITION; float3 bary : TEXCOORD0; };

            v2g vert(appdata v)
            {
                v2g o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> triStream)
            {
                float3 bary[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                for (int i = 0; i < 3; i++)
                {
                    g2f o;
                    o.pos = input[i].pos;
                    o.bary = bary[i];
                    triStream.Append(o);
                }
            }

            fixed4 frag(g2f i) : SV_Target
            {
                // Edge factor via barycentric coords
                float3 d = fwidth(i.bary);
                float3 a3 = smoothstep(0.0, d * 1.5, i.bary);
                float edge = min(min(a3.x, a3.y), a3.z);
                // 0 = wire, 1 = fill; we keep only wire
                return lerp(_Color, fixed4(0,0,0,0), edge);
            }
            ENDCG
        }
    }
}