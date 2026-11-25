// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    // Process-local endpoints for an explicit native correspondence. These are
    // session-local handles, never network/wire identities.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbSupportCorrespondence
    {
        public long SourceSpace;
        public long SourceElement;
        public long TargetSpace;
        public long TargetElement;

        public SbSupportCorrespondence(long sourceSpace, long sourceElement,
            long targetSpace, long targetElement)
        {
            SourceSpace = sourceSpace;
            SourceElement = sourceElement;
            TargetSpace = targetSpace;
            TargetElement = targetElement;
        }
    }

    internal static partial class SpatialBlendAPI
    {
        private static class SupportsRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern long sb_register_support_correspondences(
                long session, SbSupportCorrespondence[] correspondences, long count);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_dispose_support_correspondences(long registration);
        }

        private static class SupportsDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern long sb_register_support_correspondences(
                long session, SbSupportCorrespondence[] correspondences, long count);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_dispose_support_correspondences(long registration);
        }

        // A nonzero return is an opaque local lease. Native validates the
        // complete batch before mutation; it returns zero on invalid endpoints
        // or a conflicting source/target-space slot.
        public static long RegisterSupportCorrespondences(long session,
            IReadOnlyList<SbSupportCorrespondence> correspondences)
        {
            if (correspondences == null)
                throw new ArgumentNullException(nameof(correspondences));

            var nativeBatch = new SbSupportCorrespondence[correspondences.Count];
            for (int i = 0; i < nativeBatch.Length; ++i)
            {
                nativeBatch[i] = correspondences[i];
            }

            return useDebugLibrary
                ? SupportsDebug.sb_register_support_correspondences(
                    session, nativeBatch, nativeBatch.LongLength)
                : SupportsRelease.sb_register_support_correspondences(
                    session, nativeBatch, nativeBatch.LongLength);
        }

        // Native disposal is idempotent and ignores stale session-local leases.
        public static void DisposeSupportCorrespondences(long registration)
        {
            if (useDebugLibrary) SupportsDebug.sb_dispose_support_correspondences(registration);
            else SupportsRelease.sb_dispose_support_correspondences(registration);
        }
    }
}
