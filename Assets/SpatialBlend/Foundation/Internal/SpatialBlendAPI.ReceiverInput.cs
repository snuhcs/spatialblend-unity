// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverFeature
        {
            public uint descriptor_size, feature_version, anchor_size, frame_size, stamp_size, validity_size;
            public uint max_anchors, max_name_bytes, coordinate_convention, reserved;
            public ulong max_age_ns, monotonic_clock_id, monotonic_clock_epoch;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbReceiverAnchor
        {
            public IntPtr name_utf8;
            public uint name_bytes, available;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] world_pose_row_major;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbReceiverFrame
        {
            public uint struct_size, feature_version, anchor_count, anchor_stride;
            public ulong epoch, sequence, monotonic_clock_id, monotonic_clock_epoch;
            public long observed_at_ns, valid_until_ns;
            public IntPtr anchors;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbReceiverStamp
        {
            public ulong epoch, revision, sequence, rig_revision, calibration_revision;
            public long observed_at_ns, valid_until_ns;
            public uint known, reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbReceiverValidity
        {
            public ulong producer_lifetime, revision;
            public SbReceiverStamp captured, current;
            public uint changed, valid;
        }

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_feature")]
        private static extern uint ReceiverFeatureRelease(out SbReceiverFeature feature, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_feature")]
        private static extern uint ReceiverFeatureDebug(out SbReceiverFeature feature, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_field_count")]
        private static extern uint ReceiverCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_field_count")]
        private static extern uint ReceiverCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_field_offset")]
        private static extern uint ReceiverOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_field_offset")]
        private static extern uint ReceiverOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_begin_receiver_input")]
        private static extern int BeginReceiverRelease(long space, long actor, uint version,
            [In] byte[] root, uint rootSize, ulong rig, ulong calibration, out ulong epoch);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_begin_receiver_input")]
        private static extern int BeginReceiverDebug(long space, long actor, uint version,
            [In] byte[] root, uint rootSize, ulong rig, ulong calibration, out ulong epoch);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_submit_receiver_input")]
        private static extern int SubmitReceiverRelease(long space, long actor, ref SbReceiverFrame frame, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_submit_receiver_input")]
        private static extern int SubmitReceiverDebug(long space, long actor, ref SbReceiverFrame frame, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_clear_receiver_input")]
        private static extern int ClearReceiverRelease(long space, long actor, ulong epoch);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_clear_receiver_input")]
        private static extern int ClearReceiverDebug(long space, long actor, ulong epoch);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_validity")]
        private static extern int ReceiverValidityRelease(ulong lifetime, ulong revision, out SbReceiverValidity validity, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_input_validity")]
        private static extern int ReceiverValidityDebug(ulong lifetime, ulong revision, out SbReceiverValidity validity, uint size);

        private static readonly Type[] ReceiverTypes = { typeof(SbReceiverFeature), typeof(SbReceiverAnchor),
            typeof(SbReceiverFrame), typeof(SbReceiverStamp), typeof(SbReceiverValidity) };
        private static readonly string[][] ReceiverFields =
        {
            new[] { "descriptor_size", "feature_version", "anchor_size", "frame_size", "stamp_size", "validity_size",
                "max_anchors", "max_name_bytes", "coordinate_convention", "reserved", "max_age_ns", "monotonic_clock_id", "monotonic_clock_epoch" },
            new[] { "name_utf8", "name_bytes", "available", "world_pose_row_major" },
            new[] { "struct_size", "feature_version", "anchor_count", "anchor_stride", "epoch", "sequence",
                "monotonic_clock_id", "monotonic_clock_epoch", "observed_at_ns", "valid_until_ns", "anchors" },
            new[] { "epoch", "revision", "sequence", "rig_revision", "calibration_revision", "observed_at_ns", "valid_until_ns", "known", "reserved" },
            new[] { "producer_lifetime", "revision", "captured", "current", "changed", "valid" }
        };

        internal static bool TryGetReceiverInputFeature(out SbReceiverFeature feature, out string error)
        {
            feature = default; error = null;
            try
            {
                uint size = (uint)Marshal.SizeOf(typeof(SbReceiverFeature));
                uint got = useDebugLibrary ? ReceiverFeatureDebug(out feature, size) : ReceiverFeatureRelease(out feature, size);
                if (got != size) return ReceiverInputValidation.Fail("Receiver feature descriptor size mismatch.", out error);
                return ValidateReceiverInputFeature(feature,
                    kind => useDebugLibrary ? ReceiverCountDebug(kind) : ReceiverCountRelease(kind),
                    (kind, index) => useDebugLibrary ? ReceiverOffsetDebug(kind, index) : ReceiverOffsetRelease(kind, index), out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver input feature unavailable: " + e.Message, out error); }
        }

        internal static bool ValidateReceiverInputFeature(SbReceiverFeature feature,
            Func<uint, uint> count, Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            if (feature.descriptor_size != Marshal.SizeOf(ReceiverTypes[0]) || feature.feature_version != 1 ||
                feature.anchor_size != Marshal.SizeOf(ReceiverTypes[1]) || feature.frame_size != Marshal.SizeOf(ReceiverTypes[2]) ||
                feature.stamp_size != Marshal.SizeOf(ReceiverTypes[3]) || feature.validity_size != Marshal.SizeOf(ReceiverTypes[4]) ||
                feature.max_anchors != ReceiverInputValidation.MaximumAnchors || feature.max_name_bytes != ReceiverInputValidation.MaximumNameBytes ||
                feature.max_age_ns != (ulong)ReceiverInputValidation.MaximumAgeNanoseconds || feature.coordinate_convention != 1 ||
                feature.reserved != 0 || feature.monotonic_clock_id == 0 || feature.monotonic_clock_epoch == 0 || count == null || offset == null)
                return ReceiverInputValidation.Fail("Receiver input feature descriptor is incompatible.", out error);
            for (int kind = 0; kind < ReceiverTypes.Length; ++kind)
            {
                if (count((uint)kind + 1) != ReceiverFields[kind].Length)
                    return ReceiverInputValidation.Fail("Receiver input field count mismatch.", out error);
                for (int index = 0; index < ReceiverFields[kind].Length; ++index)
                    if (offset((uint)kind + 1, (uint)index) != (uint)Marshal.OffsetOf(ReceiverTypes[kind], ReceiverFields[kind][index]).ToInt64())
                        return ReceiverInputValidation.Fail("Receiver input field offset mismatch.", out error);
            }
            return true;
        }

        internal static bool TryBeginReceiverInput(long space, long actor, string root,
            IReadOnlyList<string> layout, ulong rig, ulong calibration, out ReceiverInputEpoch input,
            out string error)
        {
            input = null; error = null;
            if (space == 0 || actor == 0 || rig == 0 || calibration == 0 || !ReceiverInputValidation.ValidName(root) ||
                layout == null || layout.Count == 0 || layout.Count > ReceiverInputValidation.MaximumAnchors)
                return ReceiverInputValidation.Fail("Receiver admission requires a live actor, bounded layout, root and rig/calibration identities.", out error);
            var names = new string[layout.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; ++i)
                if (!ReceiverInputValidation.ValidName(names[i] = layout[i]) || !seen.Add(names[i]))
                    return ReceiverInputValidation.Fail("Receiver layout names must be unique bounded UTF-8.", out error);
            if (!seen.Contains(root)) return ReceiverInputValidation.Fail("Receiver root is absent from the complete layout.", out error);
            if (!TryGetReceiverInputFeature(out SbReceiverFeature feature, out error)) return false;
            try
            {
                byte[] bytes = ReceiverInputValidation.Utf8.GetBytes(root);
                ulong epoch;
                int result = useDebugLibrary
                    ? BeginReceiverDebug(space, actor, 1, bytes, (uint)bytes.Length, rig, calibration, out epoch)
                    : BeginReceiverRelease(space, actor, 1, bytes, (uint)bytes.Length, rig, calibration, out epoch);
                if (result != 1 || epoch == 0)
                    return ReceiverInputValidation.Fail("Native receiver Begin rejected the actor role/layout/planar root (result " + result + ").", out error);
                input = new ReceiverInputEpoch(space, actor, root, names, epoch, rig, calibration,
                    feature.monotonic_clock_id, feature.monotonic_clock_epoch, (long)feature.max_age_ns);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver Begin unavailable: " + e.Message, out error); }
        }

        internal static bool TryReadReceiverInputTime(ReceiverInputEpoch input, out long nanoseconds, out string error)
        {
            nanoseconds = 0; error = null;
            if (input == null) return ReceiverInputValidation.Fail("Receiver epoch is required.", out error);
            if (!TryReadRichMonotonicNanoseconds(out ulong now, out ulong id, out ulong epoch, out error)) return false;
            if (id != input.ClockId || epoch != input.ClockEpoch || now == 0 || now > long.MaxValue)
                return ReceiverInputValidation.Fail("Receiver clock identity/epoch or timestamp is incompatible.", out error);
            nanoseconds = (long)now;
            return true;
        }

        // Freeze the negotiated library selection on the Unity owner thread.
        // The returned callback reads ONLY std::chrono::steady_clock in that
        // same process/binary, not an actor, session, mutable Unity object or
        // feature descriptor. No allocation/layout probing on the SDK thread.
        internal static bool TryCreateReceiverCallbackClock(ReceiverInputEpoch input,
            out Func<long> clock, out string error)
        {
            clock = null;
            if (!TryReadReceiverInputTime(input, out _, out error)) return false;
            bool debug = useDebugLibrary;
            clock = () =>
            {
                try
                {
                    ulong now = debug ? ReadRichNsDebug() : ReadRichNsRelease();
                    return now > 0 && now <= long.MaxValue ? (long)now : 0;
                }
                catch (Exception e) when (IsRichLoadFailure(e)) { return 0; }
            };
            return true;
        }

        internal static bool TrySubmitReceiverInput(ReceiverInputFrame frame, out string error)
        {
            error = null;
            if (frame == null) return ReceiverInputValidation.Fail("Receiver frame is required.", out error);
            if (!TryReadReceiverInputTime(frame.Input, out long now, out error)) return false;
            if (frame.ObservedAtNanoseconds > now || frame.ValidUntilNanoseconds <= now)
                return ReceiverInputValidation.Fail("Receiver acquisition is future or the original deadline has expired.", out error);
            int stride = Marshal.SizeOf(typeof(SbReceiverAnchor));
            IntPtr rows = IntPtr.Zero;
            var names = new List<GCHandle>();
            try
            {
                rows = Marshal.AllocHGlobal(checked(stride * frame.Anchors.Count));
                for (int i = 0; i < frame.Anchors.Count; ++i)
                {
                    ReceiverAnchorPose row = frame.Anchors[i];
                    byte[] bytes = ReceiverInputValidation.Utf8.GetBytes(row.Name);
                    GCHandle name = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    names.Add(name);
                    if (!ReceiverInputValidation.TryNativePose(row.WorldPose, out double[] pose))
                        return ReceiverInputValidation.Fail("Receiver pose is no longer valid.", out error);
                    var value = new SbReceiverAnchor { name_utf8 = name.AddrOfPinnedObject(),
                        name_bytes = (uint)bytes.Length, available = row.Available ? 1u : 0u, world_pose_row_major = pose };
                    Marshal.StructureToPtr(value, IntPtr.Add(rows, i * stride), false);
                }
                var native = new SbReceiverFrame { struct_size = (uint)Marshal.SizeOf(typeof(SbReceiverFrame)),
                    feature_version = 1, anchor_count = (uint)frame.Anchors.Count, anchor_stride = (uint)stride,
                    epoch = frame.Input.Epoch, sequence = frame.Sequence,
                    monotonic_clock_id = frame.Input.ClockId, monotonic_clock_epoch = frame.Input.ClockEpoch,
                    observed_at_ns = frame.ObservedAtNanoseconds, valid_until_ns = frame.ValidUntilNanoseconds, anchors = rows };
                int result = useDebugLibrary
                    ? SubmitReceiverDebug(frame.Input.NativeSpace, frame.Input.NativeActor, ref native, native.struct_size)
                    : SubmitReceiverRelease(frame.Input.NativeSpace, frame.Input.NativeActor, ref native, native.struct_size);
                return result == 1 || ReceiverInputValidation.Fail("Native receiver frame rejected (result " + result + ").", out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver submission unavailable: " + e.Message, out error); }
            finally
            {
                foreach (GCHandle name in names) if (name.IsAllocated) name.Free();
                if (rows != IntPtr.Zero) Marshal.FreeHGlobal(rows);
            }
        }

        internal static bool TryClearReceiverInput(ReceiverInputEpoch input, out string error)
        {
            error = null;
            if (input == null) return ReceiverInputValidation.Fail("Receiver epoch is required.", out error);
            try
            {
                int result = useDebugLibrary ? ClearReceiverDebug(input.NativeSpace, input.NativeActor, input.Epoch)
                    : ClearReceiverRelease(input.NativeSpace, input.NativeActor, input.Epoch);
                return result == 1 || ReceiverInputValidation.Fail("Native receiver clear rejected (result " + result + ").", out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver clear unavailable: " + e.Message, out error); }
        }

        internal static bool TryGetReceiverInputValidity(ulong lifetime, ulong revision,
            out ReceiverInputValidity validity, out int nativeResult, out string error)
        {
            validity = null; nativeResult = -1; error = null;
            if (lifetime == 0 || revision == 0) return ReceiverInputValidation.Fail("Rich lifetime and revision are required.", out error);
            if (!TryGetReceiverInputFeature(out _, out error)) return false;
            try
            {
                SbReceiverValidity native;
                uint size = (uint)Marshal.SizeOf(typeof(SbReceiverValidity));
                nativeResult = useDebugLibrary ? ReceiverValidityDebug(lifetime, revision, out native, size)
                    : ReceiverValidityRelease(lifetime, revision, out native, size);
                if (nativeResult == -2 || nativeResult == -6) return true;
                if (nativeResult != 1 && nativeResult != -5)
                    return ReceiverInputValidation.Fail("Native receiver validity rejected (result " + nativeResult + ").", out error);
                if (nativeResult == -5 && native.producer_lifetime == 0 && native.revision == 0) return true;
                bool changed = native.captured.epoch != native.current.epoch || native.captured.revision != native.current.revision;
                bool expectedValid = native.captured.known == 1 && native.current.known == 1 && !changed;
                if (native.producer_lifetime != lifetime || native.revision != revision ||
                    !ValidReceiverStamp(native.captured) || !ValidReceiverStamp(native.current) ||
                    native.changed > 1 || native.valid > 1 || (native.changed == 1) != changed ||
                    (native.valid == 1) != expectedValid ||
                    (nativeResult == 1) != (native.captured.known == 1 && native.current.known == 1))
                    return ReceiverInputValidation.Fail("Malformed native receiver-input comparison.", out error);
                validity = new ReceiverInputValidity(lifetime, revision, CopyReceiverStamp(native.captured),
                    CopyReceiverStamp(native.current), changed, expectedValid);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver validity unavailable: " + e.Message, out error); }
        }

        private static bool ValidReceiverStamp(SbReceiverStamp stamp) => stamp.known <= 1 && stamp.reserved == 0 &&
            (stamp.known == 0 || (stamp.epoch != 0 && stamp.revision != 0 && stamp.sequence != 0 &&
                stamp.rig_revision != 0 && stamp.calibration_revision != 0 && stamp.observed_at_ns > 0 &&
                stamp.valid_until_ns > stamp.observed_at_ns &&
                stamp.valid_until_ns - stamp.observed_at_ns <= ReceiverInputValidation.MaximumAgeNanoseconds));
        private static ReceiverInputStamp CopyReceiverStamp(SbReceiverStamp stamp) => new ReceiverInputStamp(
            stamp.epoch, stamp.revision, stamp.sequence, stamp.rig_revision, stamp.calibration_revision,
            stamp.observed_at_ns, stamp.valid_until_ns, stamp.known == 1);
    }
}
