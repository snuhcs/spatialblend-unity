// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverReevaluationFeature
        {
            public uint descriptor_size, feature_version, required_receiver_input_version;
            public uint required_rich_version, guarantees, reserved;
        }
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_feature")]
        private static extern uint ReevaluationFeatureRelease(out SbReceiverReevaluationFeature feature, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_feature")]
        private static extern uint ReevaluationFeatureDebug(out SbReceiverReevaluationFeature feature, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_field_count")]
        private static extern uint ReevaluationCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_field_count")]
        private static extern uint ReevaluationCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_field_offset")]
        private static extern uint ReevaluationOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_reevaluation_field_offset")]
        private static extern uint ReevaluationOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_reevaluate_receiver_realization")]
        private static extern int ReevaluateReceiverRelease(ulong token, uint version, ulong epoch, ulong sequence);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_reevaluate_receiver_realization")]
        private static extern int ReevaluateReceiverDebug(ulong token, uint version, ulong epoch, ulong sequence);

        private static readonly string[] ReevaluationFields = { "descriptor_size", "feature_version",
            "required_receiver_input_version", "required_rich_version", "guarantees", "reserved" };

        internal static bool TryGetReceiverReevaluationFeature(out SbReceiverReevaluationFeature feature, out string error)
        {
            feature = default; error = null;
            try
            {
                uint size = (uint)Marshal.SizeOf(typeof(SbReceiverReevaluationFeature));
                uint got = useDebugLibrary ? ReevaluationFeatureDebug(out feature, size) : ReevaluationFeatureRelease(out feature, size);
                if (got != size) return ReceiverInputValidation.Fail("Receiver reevaluation descriptor size mismatch.", out error);
                return ValidateReceiverReevaluationFeature(feature,
                    kind => useDebugLibrary ? ReevaluationCountDebug(kind) : ReevaluationCountRelease(kind),
                    (kind, index) => useDebugLibrary ? ReevaluationOffsetDebug(kind, index) : ReevaluationOffsetRelease(kind, index), out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver reevaluation feature unavailable: " + e.Message, out error); }
        }

        internal static bool ValidateReceiverReevaluationFeature(SbReceiverReevaluationFeature feature,
            Func<uint, uint> count, Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            var type = typeof(SbReceiverReevaluationFeature);
            if (feature.descriptor_size != Marshal.SizeOf(type) || feature.feature_version != 1 ||
                feature.required_receiver_input_version != 1 || feature.required_rich_version != 1 ||
                feature.guarantees != 7 || feature.reserved != 0 || count == null || offset == null)
                return ReceiverInputValidation.Fail("Receiver reevaluation descriptor is incompatible.", out error);
            if (count(1) != ReevaluationFields.Length)
                return ReceiverInputValidation.Fail("Receiver reevaluation field count mismatch.", out error);
            for (int i = 0; i < ReevaluationFields.Length; ++i)
                if (offset(1, (uint)i) != (uint)Marshal.OffsetOf(type, ReevaluationFields[i]).ToInt64())
                    return ReceiverInputValidation.Fail("Receiver reevaluation field offset mismatch.", out error);
            return true;
        }

        internal static bool TryReevaluateReceiverRealization(ulong lifetime, ReceiverInputEpoch input,
            ulong sequence, out int nativeResult, out string error)
        {
            nativeResult = -1; error = null;
            if (lifetime == 0 || input == null || sequence == 0)
                return ReceiverInputValidation.Fail("Reevaluation requires the exact active rich lifetime and admitted input.", out error);
            if (!TryGetReceiverReevaluationFeature(out _, out error) ||
                !TryReadReceiverInputTime(input, out _, out error)) return false;
            try
            {
                nativeResult = useDebugLibrary ? ReevaluateReceiverDebug(lifetime, 1, input.Epoch, sequence)
                                              : ReevaluateReceiverRelease(lifetime, 1, input.Epoch, sequence);
                return true; // Status is explicit; OK is not body/actuation admission.
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver reevaluation unavailable: " + e.Message, out error); }
        }
    }
}
