// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

// Local override of app_functions.hlsl to support _AlphaFade for avatar fade transitions.

void AppSpecificVertexPostManipulation(AvatarVertexInput i, inout VertexToFragment o) {
}

void AppSpecificPreManipulation(inout avatar_FragmentInput i) {
}

void AppSpecificFragmentComponentManipulation(avatar_FragmentInput i, inout float3 punctualSpecular, inout float3 punctualDiffuse, inout float3 ambientSpecular, inout float3 ambientDiffuse) {
}

void AppSpecificPostManipulation(avatar_FragmentInput i, inout avatar_FragmentOutput o) {
    o.color.a = _AlphaFade;
}
