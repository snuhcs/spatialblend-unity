// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

Shader "Avatar/Style2Meta_Occlusion"
{
    Properties
    {
        [Header(Compute Skinning Support)]
        u_AttributeTexture("Vertex Attribute map", 3D) = "white" {}

        [Header(PBR Textures)]

        [NoScaleOffset] u_BaseColorSampler("Base Color", 2D) = "white" {}
        u_BaseColorUVSet("Base Color UV Set", Int) = 0

        [NoScaleOffset]  u_MetallicRoughnessSampler("Metallic Roughness", 2D) = "white" {}

        [NoScaleOffset] u_NormalSampler("Normal map", 2D) = "bump" {}
        u_NormalUVSet("Normal UV Set", Int) = 0
        u_NormalScale("Normal map scale", Range(0, 2)) = 1.0

        u_Exposure("Material Exposure", Range(0, 2)) = 1.0

        [Space]
        [Header(SSS Curvature)]
        [ShowIfKeyword(SKIN_ON)]
        u_SSSCurvatureScaleBias("SSS Curvature Scale Bias", Vector) = (12.84, 0.0186, 0.0)

        [Space]
        [Header(Variants)]

        [KeywordEnum(2 Light, 2 Standard, 2 Experimental, 1 Light, 1 Standard)] Style("Style", Float) = -1
        [KeywordProxy(EYE_GLINTS_ON, STYLE_1_LIGHT, STYLE_1_STANDARD, STYLE_2_LIGHT, STYLE_2_STANDARD, STYLE_2_EXPERIMENTAL)] EyeGlints("Eye Glints", Float) = 1
        [KeywordProxy(SKIN_ON, STYLE_1_LIGHT, STYLE_1_STANDARD, STYLE_2_LIGHT, STYLE_2_STANDARD, STYLE_2_EXPERIMENTAL)] Skin("Skin", Float) = 1
        [KeywordProxy(HAS_NORMAL_MAP_ON, STYLE_1_STANDARD, STYLE_2_LIGHT, STYLE_2_STANDARD, STYLE_2_EXPERIMENTAL)] HasNormalMap("Normal Map", Float) = 1
        [KeywordProxy(ENABLE_HAIR_ON, STYLE_1_STANDARD, STYLE_2_STANDARD, STYLE_2_EXPERIMENTAL)] EnableHair("Hair", Float) = 1
        [KeywordProxy(ENABLE_RIM_LIGHT_ON, STYLE_2_STANDARD, STYLE_2_EXPERIMENTAL)] EnableRimLight("Rim Light", Float) = 1
        [KeywordProxy(SIMPLE_OCCLUSION, STYLE_2_LIGHT)] SimpleOcclusion("Simplified Occlusion Model", Float) = 1

        [KeywordEnumWithToggle(DEBUG_MODE_ON, None, BaseColorSRGB, BaseColorLinear, Alpha, Occlusion, Metallic, Roughness, Thickness, Normal, NormalGeometry, NormalWorld, Tangent, Bitangent, F0, EmissiveSrgb, EmissiveLinear, SpecularSrgb, DiffuseSrgb, ClearcoatSrgb, SheenSrgb, TransmissionSrgb, Ambient, AmbientDiffuse, AmbientSpecular, IBLBrdf, Punctual, PunctualDiffuse, PunctualSpecular, Anisotropy, AnisotropyDirection, AnisotropyTangent, AnisotropyBitangent, View, SubsurfaceScattering, AmbientOcclusion, SSSCurvature, SSSCurvaturePunctual, SSSCurvatureAmbient, TexCoord0, TexCoord1, SHIrradiance, Submeshes, MaterialType, Shadows)] Debug("Debug Render", Float) = 0

        [KeywordEnum(Texture, Vertex)] Material_Mode("Material Mode", Float) = 0

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2

        [Space]
        [Header(Additional parameters)]

        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSubsurfaceColor("Hair Sub-Surface Color", Color) = (1, 1, 1, 1)
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularColorFactor("Hair Specular Color Factor", Color) = (1, 1, 1, 1)
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularShiftIntensity("Hair Specular Shift Intensity", Range(-1,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularWhiteIntensity("Hair Specular White Intensity", Range(0,10)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularColorIntensity("Hair Specular Color Intensity", Range(0,10)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularColorOffset("Hair Specular Color Offset", Range(-1,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairRoughness("Hair Roughness", Range(0,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairColorRoughness("Hair Color Roughness", Range(0,1)) = .4
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairAnisotropicIntensity("Hair Anistropic Intensity", Range(-1,1)) = .5
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairSpecularNormalIntensity("Hair Specular Normal Intensity", Range(0,1)) = 1.
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_HairDiffusedIntensity("Hair Diffuse Intensity", Range(0,10)) = .25

        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSubsurfaceColor("Facial Hair Sub-Surface Color", Color) = (1, 1, 1, 1)
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularColorFactor("Facial Hair Specular Color Factor", Color) = (1, 1, 1, 1)
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularShiftIntensity("Facial Hair Specular Shift Intensity", Range(-1,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularWhiteIntensity("Facial Hair Specular White Intensity", Range(0,10)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularColorIntensity("Facial Hair Specular Color Intensity", Range(0,10)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularColorOffset("Facial Hair Specular Color Offset", Range(-1,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairRoughness("Facial Hair Roughness", Range(0,1)) = .2
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairColorRoughness("Facial Hair Color Roughness", Range(0,1)) = .4
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairAnisotropicIntensity("Facial Hair Anistropic Intensity", Range(-1,1)) = .5
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairSpecularNormalIntensity("Facial Hair Specular Normal Intensity", Range(0,1)) = 1.
        [ShowIfKeyword(ENABLE_HAIR_ON)]
        u_FacialHairDiffusedIntensity("Facial Hair Diffuse Intensity", Range(0,10)) = .25

        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightIntensity("Rim Light Intensity", Range(0,1)) = 0.0
        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightBias("Rim Light Bias", Range(0.0,1.0)) = 0.5
        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightColor("Rim Light Color", Color) = (1,1,1)
        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightTransition("Rim Light Transition", Range(0,0.2)) = 0.2
        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightStartPosition("Rim Light Start Position", Range(0,1)) = 0.5
        [ShowIfKeyword(ENABLE_RIM_LIGHT_ON)]
        u_RimLightEndPosition("Rim Light End Position", Range(0,1)) = 0.5

        [ShowIfKeyword(EYE_GLINTS_ON)]
        u_EyeGlintLightColor("EyeGlintLightColor", Color) = (1, 1, 1)

        u_EmissiveColor("EmissiveColor", Color) = (0, 0, 0)
        [Toggle] u_EmissiveUseBaseColor("EmissiveUseBaseColor", Int) = 0

        [Space]
        [Header(Environment Depth Occlusion)]
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.0
        _AlphaFade ("Alpha Fade", Range(0, 1)) = 1.0
    }

    // =========================================================================
    // Unity Built-in Render Pipeline, shader target 5.0
    // =========================================================================
    SubShader
    {
        Tags { "RenderPipeline" = "" "RenderType" = "Transparent" "Queue" = "Transparent" }
        LOD 100
        Cull[_Cull]

        // Depth Pre-Pass: write depth only to occlude interior geometry during alpha fade
        Pass
        {
            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 5.0

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }

        // Single Light
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            // Coverage alpha over the passthrough underlay: colour by alpha, alpha = a + dst * (1 - a) (not a * a). See PlacementHologram.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 5.0

            // DepthAPI Environment Occlusion
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }

        // Up to 4 Additive Lights
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            // An additive light pass leaves the alpha (coverage) the base pass wrote.
            Blend SrcAlpha One, Zero One
            ZWrite Off

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 5.0

            // DepthAPI Environment Occlusion
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }
    }

    // =========================================================================
    // Unity Built-in Render Pipeline, shader target 3.5 compatibility mode
    // =========================================================================
    SubShader
    {
        Tags { "RenderPipeline" = "" "RenderType" = "Transparent" "Queue" = "Transparent" }
        LOD 100
        Cull[_Cull]

        // Depth Pre-Pass: write depth only to occlude interior geometry during alpha fade
        Pass
        {
            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 3.5

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }

        // Single Light
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            // Coverage alpha over the passthrough underlay: colour by alpha, alpha = a + dst * (1 - a) (not a * a). See PlacementHologram.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 3.5

            // DepthAPI Environment Occlusion
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }

        // Up to 4 Additive Lights
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            // An additive light pass leaves the alpha (coverage) the base pass wrote.
            Blend SrcAlpha One, Zero One
            ZWrite Off

            CGPROGRAM
            #pragma editor_sync_compilation

            #pragma vertex Vertex_main_instancing
            #pragma fragment Fragment_main

            #pragma target 3.5

            // DepthAPI Environment Occlusion
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include_with_pragmas "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_variants.hlsl"
            #include "Packages/com.meta.xr.sdk.avatars/Scripts/Common/Shaders/Recommended/app_specific/app_declarations.hlsl"
            #include_with_pragmas "Style2MetaAvatarCore_Occlusion.hlsl"
            #include "LocalAvatarAppFunctions.hlsl"
            ENDCG
        }
    }
}
