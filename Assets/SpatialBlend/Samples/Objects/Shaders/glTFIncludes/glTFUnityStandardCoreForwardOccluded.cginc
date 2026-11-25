// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#ifndef SPATIALBLEND_GLTF_STANDARD_CORE_FORWARD_OCCLUDED_INCLUDED
#define SPATIALBLEND_GLTF_STANDARD_CORE_FORWARD_OCCLUDED_INCLUDED

#include "UnityStandardConfig.cginc"

float _EnvironmentDepthBias;

inline half4 ApplyEnvironmentDepthOcclusion(float3 worldPos, half4 color)
{
#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
    META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY_WORLDPOS(worldPos, color, _EnvironmentDepthBias)
    clip(color.a - 0.01);
#endif
    return color;
}

// UNITY_STANDARD_SIMPLE: glTFast's own glTFUnityStandardCoreForward.cginc defines it to 1 when
// UNITY_NO_FULL_STANDARD_SHADER is set (the lowest Standard Shader Quality tier) and selects the Simple
// pipeline. This copy leaves that define out on purpose. The occlusion shader is compiled at
// "#pragma target 3.0" and its passes are written for the full path, so it always takes the #else branch
// below unless the config defines UNITY_STANDARD_SIMPLE itself. The Simple branch is kept, wrapped the same way,
// for that case. It has not been built or run on the Low tier.
#if UNITY_STANDARD_SIMPLE
    #include "Packages/com.unity.cloud.gltfast/Runtime/Shader/Built-In/glTFIncludes/glTFUnityStandardCoreForwardSimple.cginc"
    VertexOutputBaseSimple vertBase (VertexInput v) { return vertForwardBaseSimple(v); }
    VertexOutputForwardAddSimple vertAdd (VertexInput v) { return vertForwardAddSimple(v); }
    half4 fragBase (VertexOutputBaseSimple i) : SV_Target { return fragForwardBaseSimpleInternal(i); }
    half4 fragAdd (VertexOutputForwardAddSimple i) : SV_Target { return fragForwardAddSimpleInternal(i); }

    half4 fragBaseFacing (VertexOutputBaseSimple i, half facing : VFACE) : SV_Target
    {
        i.normalWorld.xyz *= facing;
#ifdef _NORMALMAP
        i.tangentSpaceLightDir *= facing;
    #if SPECULAR_HIGHLIGHTS
        i.tangentSpaceEyeVec *= facing;
    #endif
#endif
        half4 outputColor = fragBase(i);
        return ApplyEnvironmentDepthOcclusion(IN_WORLDPOS(i), outputColor);
    }

    half4 fragAddFacing (VertexOutputForwardAddSimple i, half facing : VFACE) : SV_Target
    {
#if defined(_NORMALMAP)
    #if SPECULAR_HIGHLIGHTS
        i.tangentSpaceEyeVec *= facing;
    #endif
#else
        i.normalWorld *= facing;
#endif
        half4 outputColor = fragAdd(i);
        return ApplyEnvironmentDepthOcclusion(IN_WORLDPOS_FWDADD(i), outputColor);
    }
#else
    #include "Packages/com.unity.cloud.gltfast/Runtime/Shader/Built-In/glTFIncludes/glTFUnityStandardCore.cginc"
    VertexOutputForwardBase vertBase (VertexInput v) { return vertForwardBase(v); }
    VertexOutputForwardAdd vertAdd (VertexInput v) { return vertForwardAdd(v); }
    half4 fragBase (VertexOutputForwardBase i) : SV_Target { return fragForwardBaseInternal(i); }
    half4 fragAdd (VertexOutputForwardAdd i) : SV_Target { return fragForwardAddInternal(i); }

    half4 fragBaseFacing (VertexOutputForwardBase i, half facing : VFACE) : SV_Target
    {
#ifdef _TANGENT_TO_WORLD
        i.tangentToWorldAndPackedData[0].xyz *= facing;
        i.tangentToWorldAndPackedData[1].xyz *= facing;
#endif
        i.tangentToWorldAndPackedData[2].xyz *= facing;
        half4 outputColor = fragBase(i);
        return ApplyEnvironmentDepthOcclusion(IN_WORLDPOS(i), outputColor);
    }

    half4 fragAddFacing (VertexOutputForwardAdd i, half facing : VFACE) : SV_Target
    {
#ifdef _TANGENT_TO_WORLD
        i.tangentToWorldAndLightDir[0].xyz *= facing;
        i.tangentToWorldAndLightDir[1].xyz *= facing;
#endif
        i.tangentToWorldAndLightDir[2].xyz *= facing;
        half4 outputColor = fragAdd(i);
        return ApplyEnvironmentDepthOcclusion(IN_WORLDPOS_FWDADD(i), outputColor);
    }
#endif

#endif // SPATIALBLEND_GLTF_STANDARD_CORE_FORWARD_OCCLUDED_INCLUDED