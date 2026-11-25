// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Oculus.Avatar2;
using UnityEngine;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Where the Meta/OVR avatar's embodiment comes from, and the one assumption about its bones' Unity frame.
    /// Everything about the body is DATA, in <c>Resources/Embodiment/</c>: the structure (<c>ovr-avatar.embodiment.json</c>:
    /// skeleton, anchors, bones, families), the measurement artefact (<c>ovr-avatar.measurement.json</c>: the offsets, with their
    /// procedure and the asset sha256) and the chain artefact (<c>ovr-avatar.chain.json</c>: each joint's rest pose). No body
    /// number is in code.
    /// </summary>
    internal static class OvrAvatarEmbodiment
    {
        internal static readonly EmbodimentFiles Files = new EmbodimentFiles(
            "ovr-avatar.embodiment", "ovr-avatar.measurement", "ovr-avatar.chain", "ovr-avatar.sitbone-presets");

        internal static IEmbodimentTextSource DefaultTextSource { get; } = new ResourcesEmbodimentTextSource();

        /// <summary>
        /// The frames of a received avatar's bones: <paramref name="nativeBasisRows"/> is the measurement artefact's native-to-canonical
        /// basis B (glTF/OVR: the asset's right-handed x-left, y-up, z-forward axes to the core's x-forward, y-left, z-up).
        ///
        /// ASSUMPTION A1 (from the SDK sources, NOT verified at run time): the Unity transforms of an avatar's skeleton are the
        /// asset's native bone transforms converted to the Unity client space by the SDK's <c>ConvertSpace</c> (a reflection of z; the
        /// SDK is initialised with clientSpaceForwardAxis = Unity forward, whose SDK counterpart is -z, OvrAvatarAPI_Avatar.cs), so a
        /// Unity bone's local axes are the native ones relabelled by that reflection. Z is read off the SDK's own function here, not
        /// retyped. If A1 is wrong, the offsets registered in the measurement artefact are mirrored: an editor check of a posed avatar
        /// against the measured points settles it.
        /// </summary>
        internal static EmbodimentFrames CreateFrames(double[] nativeBasisRows)
        {
            Mat3 nativeToCanonical = Mat3.FromRows(nativeBasisRows);
            CAPI.ovrAvatar2Vector3f x = new CAPI.ovrAvatar2Vector3f(1f, 0f, 0f).ConvertSpace();
            CAPI.ovrAvatar2Vector3f y = new CAPI.ovrAvatar2Vector3f(0f, 1f, 0f).ConvertSpace();
            CAPI.ovrAvatar2Vector3f z = new CAPI.ovrAvatar2Vector3f(0f, 0f, 1f).ConvertSpace();
            Mat3 unityLocalFromNativeLocal = Mat3.FromColumns((x.x, x.y, x.z), (y.x, y.y, y.z), (z.x, z.y, z.z));
            return new EmbodimentFrames(nativeToCanonical, unityLocalFromNativeLocal);
        }
    }
}
