// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_owned_correspondence_feature_version")]
        private static extern uint OwnedCorrespondenceFeatureRelease();
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_owned_correspondence_feature_version")]
        private static extern uint OwnedCorrespondenceFeatureDebug();
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_register_owned_correspondences")]
        private static extern long OwnedCorrespondenceRegisterRelease(long session, uint version, [In] SbSupportCorrespondence[] pairs, uint count);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_register_owned_correspondences")]
        private static extern long OwnedCorrespondenceRegisterDebug(long session, uint version, [In] SbSupportCorrespondence[] pairs, uint count);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_validate_owned_correspondences")]
        private static extern int OwnedCorrespondenceValidateRelease(long session, long registration, uint version);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_validate_owned_correspondences")]
        private static extern int OwnedCorrespondenceValidateDebug(long session, long registration, uint version);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_dispose_owned_correspondences")]
        private static extern int OwnedCorrespondenceDisposeRelease(long session, long registration, uint version);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_dispose_owned_correspondences")]
        private static extern int OwnedCorrespondenceDisposeDebug(long session, long registration, uint version);

        internal static bool TryGetOwnedCorrespondenceFeature(out string error)
        {
            error = null;
            try
            {
                if ((useDebugLibrary ? OwnedCorrespondenceFeatureDebug() : OwnedCorrespondenceFeatureRelease()) == 1 &&
                    Marshal.SizeOf<SbSupportCorrespondence>() == 32) return true;
                error = "Incompatible optional owned correspondence feature.";
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { error = "Optional owned correspondence feature unavailable: " + e.Message; }
            return false;
        }

        internal static long TryRegisterOwnedCorrespondences(long session, SbSupportCorrespondence[] pairs)
        {
            if (session <= 0 || pairs == null || pairs.Length == 0 || pairs.Length > 128 ||
                !TryGetOwnedCorrespondenceFeature(out _)) return 0;
            return useDebugLibrary ? OwnedCorrespondenceRegisterDebug(session, 1, pairs, (uint)pairs.Length)
                : OwnedCorrespondenceRegisterRelease(session, 1, pairs, (uint)pairs.Length);
        }
        internal static int TryValidateOwnedCorrespondences(long session, long handle)
            => useDebugLibrary ? OwnedCorrespondenceValidateDebug(session, handle, 1)
                : OwnedCorrespondenceValidateRelease(session, handle, 1);
        internal static int TryDisposeOwnedCorrespondences(long session, long handle)
            => useDebugLibrary ? OwnedCorrespondenceDisposeDebug(session, handle, 1)
                : OwnedCorrespondenceDisposeRelease(session, handle, 1);
    }
}
