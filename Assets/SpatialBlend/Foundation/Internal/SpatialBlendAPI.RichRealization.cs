// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using AOT;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SpatialBlend.Internal
{
    // Additive feature-1 binding.  This file deliberately contains no ABI2
    // type, callback, or library-selection changes.
    internal static partial class SpatialBlendAPI
    {
        private const uint RichFeatureVersion = 1;
        private const uint RichCoordinateZUp = 1;
        private const uint RichUnknownRevisionBits = 15;
        private const uint RichMaxChannels = 1024;
        private const uint RichMaxContributors = 8192;
        private const uint RichMaxUtf8 = 1048576;
        private const int RichMaxCallbackRegistrations = 1024;

        private enum RichLayoutKind : uint { Feature = 1, Header = 2, Channel = 3, Contributor = 4, Snapshot = 5, Copy = 6 }

        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichFeature
        {
            public uint descriptor_size, feature_version, snapshot_size, channel_size, contributor_size, copy_size;
            public uint snapshot_channels_offset, snapshot_contributors_offset, snapshot_utf8_offset, snapshot_channel_count_offset, snapshot_contributor_count_offset, snapshot_utf8_bytes_offset;
            public uint copy_channels_offset, copy_contributors_offset, copy_utf8_offset, copy_header_offset;
            public uint channel_anchor_offset_offset, channel_contributor_offset_offset, channel_has_z_offset, channel_requested_position_offset;
            public uint contributor_reason_offset_offset, contributor_support_identifier_offset, contributor_support_plane_to_world_offset;
            public uint max_channels, max_contributors, max_utf8_bytes, coordinate_convention;
            public ulong monotonic_clock_id, monotonic_clock_epoch;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichHeader
        {
            public ulong producer_lifetime, revision, monotonic_clock_id, monotonic_clock_epoch, receiver_ingress_monotonic_ns, receiver_solve_monotonic_ns, source_epoch;
            public long source_sequence, source_observation_sequence, source_actor_space, source_actor_element, relay_source_space, receiver_space, receiver_actor;
            public ulong policy_revision, tree_revision, counterpart_revision, support_revision;
            public double source_time_seconds, source_clock_seconds;
            public uint status, source_timing_mode, source_time_known, source_clock_known, source_observation_sequence_known, known_revision_mask, unknown_revision_mask, failure_code;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichChannel
        {
            public uint anchor_offset, anchor_length, contributor_offset, contributor_count;
            public int family, solver_status, geometry, goal_kind;
            public uint has_goal, has_z, has_requested_position, reach_projected;
            public long referent_id;
            public double arrival_seconds, position_x, position_y, position_z, yaw, weight, reach_displacement_m, requested_position_x, requested_position_y;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichContributor
        {
            public long referent_id, source_space, source_element, resolved_local_element;
            public ulong binding_revision;
            public uint snapshot_ordinal;
            public int disposition, grounding_mode;
            public uint has_mapped_support, chart_version_known, binding_revision_known, reason_offset, reason_length, support_identifier_offset, support_identifier_length;
            public double applied_weight, mapped_support_x, mapped_support_y, mapped_support_z;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] support_plane_to_world_row_major;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichSnapshot
        {
            public SbRichHeader header;
            public IntPtr channels, contributors, utf8;
            public uint channel_count, contributor_count, utf8_bytes, reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichCopy
        {
            public uint struct_size, channel_capacity, contributor_capacity, utf8_capacity;
            public IntPtr channels, contributors, utf8;
            public SbRichHeader header;
            public uint required_channels, required_contributors, required_utf8_bytes, reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnRichNativeDelegate(IntPtr snapshot, IntPtr userData);

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_feature")]
        private static extern uint GetRichFeatureRelease(out SbRichFeature value, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_feature")]
        private static extern uint GetRichFeatureDebug(out SbRichFeature value, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_field_count")]
        private static extern uint GetRichFieldCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_field_count")]
        private static extern uint GetRichFieldCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_field_offset")]
        private static extern uint GetRichFieldOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_field_offset")]
        private static extern uint GetRichFieldOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_activate_rich_realization")]
        private static extern ulong ActivateRichRelease(long space, long actor, uint version);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_activate_rich_realization")]
        private static extern ulong ActivateRichDebug(long space, long actor, uint version);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_register_on_rich_realization")]
        private static extern void RegisterRichRelease(long space, OnRichNativeDelegate callback, IntPtr data);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_register_on_rich_realization")]
        private static extern void RegisterRichDebug(long space, OnRichNativeDelegate callback, IntPtr data);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_poll_rich_realization")]
        private static extern int PollRichRelease(ulong token, ref SbRichCopy copy);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_poll_rich_realization")]
        private static extern int PollRichDebug(ulong token, ref SbRichCopy copy);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_retire_rich_realization")]
        private static extern void RetireRichRelease(ulong token);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_retire_rich_realization")]
        private static extern void RetireRichDebug(ulong token);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_release_rich_realization")]
        private static extern int ReleaseRichRelease(ulong token);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_release_rich_realization")]
        private static extern int ReleaseRichDebug(ulong token);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_read_rich_monotonic_ns")]
        private static extern ulong ReadRichNsRelease();
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_read_rich_monotonic_ns")]
        private static extern ulong ReadRichNsDebug();

        // Separate C library constants keep ABI2's selection rules intact.
        private const string ReleaseRich = SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY;
        private const string DebugRich = SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY;
        private static readonly object richCallbackLock = new object();
        private static readonly Dictionary<long, Action<RichRealizationSnapshot>> richCallbacks = new Dictionary<long, Action<RichRealizationSnapshot>>();
        private static readonly Dictionary<long, long> richCallbackTokenBySpace = new Dictionary<long, long>();
        private static readonly Dictionary<long, long> richCallbackSpaceByToken = new Dictionary<long, long>();
        // P/Invoke retains only the function pointer. Keep the exact delegate
        // instance rooted for every native callback registration lifetime.
        private static readonly OnRichNativeDelegate richNativeCallback = OnRichCallback;
        private static long nextRichCallbackToken;
        internal static event Action<long, string> RichCallbackRejected;

        internal static bool TryActivateRichRealization(long space, long actor, out ulong lifetime, out string error)
        {
            lifetime = 0;
            if (!TryGetRichFeature(out _, out error)) return false;
            try { lifetime = useDebugLibrary ? ActivateRichDebug(space, actor, RichFeatureVersion) : ActivateRichRelease(space, actor, RichFeatureVersion); }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich realization feature unavailable: " + e.Message; return false; }
            if (lifetime == 0) { error = "Native rich realization activation returned no lifetime token."; return false; }
            error = null; return true;
        }

        internal static bool TryRegisterOnRichRealization(long space, Action<RichRealizationSnapshot> callback, out string error)
        {
            if (callback == null) { error = "Rich callback is required."; return false; }
            if (!TryGetRichFeature(out _, out error)) return false;
            lock (richCallbackLock)
            {
                long previous = 0;
                Action<RichRealizationSnapshot> previousCallback = null;
                if (richCallbackTokenBySpace.TryGetValue(space, out previous)) richCallbacks.TryGetValue(previous, out previousCallback);
                if (previous == 0 && richCallbackTokenBySpace.Count >= RichMaxCallbackRegistrations)
                {
                    error = "Rich callback registration limit reached.";
                    return false;
                }
                if (previous != 0) { richCallbacks.Remove(previous); richCallbackSpaceByToken.Remove(previous); }
                long token = ++nextRichCallbackToken;
                if (token == 0) token = ++nextRichCallbackToken;
                richCallbacks[token] = callback;
                richCallbackTokenBySpace[space] = token;
                richCallbackSpaceByToken[token] = space;
                // Core only replaces a stored callback under its callback
                // mutex; it never invokes application code from this call.
                // Keep the managed token maps and native registration in this
                // one critical section so another register/unregister cannot
                // leave native userdata pointing at a removed token.
                try
                {
                    if (useDebugLibrary) RegisterRichDebug(space, richNativeCallback, new IntPtr(token));
                    else RegisterRichRelease(space, richNativeCallback, new IntPtr(token));
                    error = null;
                    return true;
                }
                catch (Exception e) when (IsRichLoadFailure(e))
                {
                    // No interleaving can have replaced this token while the
                    // native call ran. Restore only this failed registration.
                    richCallbacks.Remove(token);
                    richCallbackSpaceByToken.Remove(token);
                    if (previous != 0 && previousCallback != null)
                    {
                        richCallbacks[previous] = previousCallback;
                        richCallbackTokenBySpace[space] = previous;
                        richCallbackSpaceByToken[previous] = space;
                    }
                    else richCallbackTokenBySpace.Remove(space);
                    error = "Rich callback registration unavailable: " + e.Message;
                    return false;
                }
            }
        }

        internal static bool TryUnregisterOnRichRealization(long space, out string error)
        {
            lock (richCallbackLock)
            {
                if (!richCallbackTokenBySpace.TryGetValue(space, out long token))
                {
                    error = null;
                    return true;  // Preserve the old ABI's no-registration no-op.
                }
                // As above, Core's register call only mutates its callback
                // slot. Holding this lock prevents it from nulling a callback
                // installed by a concurrent managed replacement.
                try
                {
                    if (useDebugLibrary) RegisterRichDebug(space, null, IntPtr.Zero);
                    else RegisterRichRelease(space, null, IntPtr.Zero);
                }
                catch (Exception e) when (IsRichLoadFailure(e))
                {
                    error = "Rich callback unregistration unavailable: " + e.Message;
                    return false;
                }
                richCallbackTokenBySpace.Remove(space);
                richCallbackSpaceByToken.Remove(token);
                richCallbacks.Remove(token);
                error = null;
                return true;
            }
        }

        // Managed-only teardown hooks.  Call only after native session/global
        // teardown has run: an already-entered callback may still finish with
        // its deep-owned snapshot, but no old userdata can resolve afterwards.
        internal static void ForgetRichCallbackForSpace(long space)
        {
            lock (richCallbackLock)
            {
                if (richCallbackTokenBySpace.TryGetValue(space, out long token))
                {
                    richCallbackTokenBySpace.Remove(space);
                    richCallbackSpaceByToken.Remove(token);
                    richCallbacks.Remove(token);
                }
            }
        }

        internal static void ForgetAllRichCallbacks()
        {
            lock (richCallbackLock)
            {
                richCallbacks.Clear();
                richCallbackTokenBySpace.Clear();
                richCallbackSpaceByToken.Clear();
            }
        }

        internal static bool TryPollRichRealization(ulong lifetime, out RichRealizationSnapshot snapshot, out int nativeResult, out string error)
        {
            snapshot = null; nativeResult = -1; error = null;
            if (lifetime == 0) { error = "Rich lifetime token is required."; return false; }
            if (!TryGetRichFeature(out SbRichFeature feature, out error)) return false;
            IntPtr channels = IntPtr.Zero, contributors = IntPtr.Zero, utf8 = IntPtr.Zero;
            try
            {
                channels = Marshal.AllocHGlobal(checked((int)(feature.max_channels * (uint)Marshal.SizeOf(typeof(SbRichChannel)))));
                contributors = Marshal.AllocHGlobal(checked((int)(feature.max_contributors * (uint)Marshal.SizeOf(typeof(SbRichContributor)))));
                utf8 = Marshal.AllocHGlobal(checked((int)feature.max_utf8_bytes));
                var copy = new SbRichCopy { struct_size = (uint)Marshal.SizeOf(typeof(SbRichCopy)), channel_capacity = feature.max_channels, contributor_capacity = feature.max_contributors, utf8_capacity = feature.max_utf8_bytes, channels = channels, contributors = contributors, utf8 = utf8 };
                nativeResult = useDebugLibrary ? PollRichDebug(lifetime, ref copy) : PollRichRelease(lifetime, ref copy);
                if (nativeResult == 0) return true;
                if (nativeResult != 1) { error = "Native rich poll failed with result " + nativeResult + "."; return false; }
                var view = new SbRichSnapshot { header = copy.header, channels = channels, contributors = contributors, utf8 = utf8, channel_count = copy.required_channels, contributor_count = copy.required_contributors, utf8_bytes = copy.required_utf8_bytes };
                return TryCopyRichSnapshot(ref view, feature, out snapshot, out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich poll unavailable: " + e.Message; return false; }
            catch (OverflowException e) { error = "Invalid negotiated rich buffer capacity: " + e.Message; return false; }
            finally { if (channels != IntPtr.Zero) Marshal.FreeHGlobal(channels); if (contributors != IntPtr.Zero) Marshal.FreeHGlobal(contributors); if (utf8 != IntPtr.Zero) Marshal.FreeHGlobal(utf8); }
        }

        internal static bool TryRetireRichRealization(ulong lifetime, out string error)
        {
            if (lifetime == 0) { error = "Rich lifetime token is required."; return false; }
            try { if (useDebugLibrary) RetireRichDebug(lifetime); else RetireRichRelease(lifetime); error = null; return true; }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich retire unavailable: " + e.Message; return false; }
        }
        internal static bool TryReleaseRichRealization(ulong lifetime, out int nativeResult, out string error)
        {
            nativeResult = -1; if (lifetime == 0) { error = "Rich lifetime token is required."; return false; }
            try { nativeResult = useDebugLibrary ? ReleaseRichDebug(lifetime) : ReleaseRichRelease(lifetime); error = nativeResult == 1 ? null : "Native rich release failed with result " + nativeResult + "."; return nativeResult == 1; }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich release unavailable: " + e.Message; return false; }
        }
        internal static bool TryReadRichMonotonicNanoseconds(out ulong nanoseconds, out ulong clockId, out ulong clockEpoch, out string error)
        {
            nanoseconds = clockId = clockEpoch = 0;
            if (!TryGetRichFeature(out SbRichFeature f, out error)) return false;
            try { nanoseconds = useDebugLibrary ? ReadRichNsDebug() : ReadRichNsRelease(); clockId = f.monotonic_clock_id; clockEpoch = f.monotonic_clock_epoch; error = null; return true; }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich clock unavailable: " + e.Message; return false; }
        }

        private static bool TryGetRichFeature(out SbRichFeature feature, out string error)
        {
            feature = default(SbRichFeature); error = null;
            try
            {
                uint got = useDebugLibrary ? GetRichFeatureDebug(out feature, (uint)Marshal.SizeOf(typeof(SbRichFeature))) : GetRichFeatureRelease(out feature, (uint)Marshal.SizeOf(typeof(SbRichFeature)));
                if (got != (uint)Marshal.SizeOf(typeof(SbRichFeature)) || feature.descriptor_size != got || feature.feature_version != RichFeatureVersion || feature.coordinate_convention != RichCoordinateZUp || feature.max_channels == 0 || feature.max_channels > RichMaxChannels || feature.max_contributors > RichMaxContributors || feature.max_utf8_bytes > RichMaxUtf8 || feature.monotonic_clock_id == 0 || feature.monotonic_clock_epoch == 0) { error = "Native rich feature descriptor is incompatible."; return false; }
                if (feature.snapshot_size != (uint)Marshal.SizeOf(typeof(SbRichSnapshot)) || feature.channel_size != (uint)Marshal.SizeOf(typeof(SbRichChannel)) || feature.contributor_size != (uint)Marshal.SizeOf(typeof(SbRichContributor)) || feature.copy_size != (uint)Marshal.SizeOf(typeof(SbRichCopy))) { error = "Native rich feature struct sizes are incompatible."; return false; }
                return ValidateRichLayouts(feature, out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e)) { error = "Rich feature unavailable: " + e.Message; return false; }
        }

        private static bool ValidateRichLayouts(SbRichFeature f, out string error)
        {
            error = null;
            Type[] types = { typeof(SbRichFeature), typeof(SbRichHeader), typeof(SbRichChannel), typeof(SbRichContributor), typeof(SbRichSnapshot), typeof(SbRichCopy) };
            // A prior Core revision incorrectly reported 22 while exposing an
            // unreported requested-Z field.  v1's frozen header has no Z and
            // therefore exactly 22 channel fields; either other shape fails.
            int[] counts = { 29, 28, 22, 20, 8, 12 };
            for (int k = 0; k < types.Length; ++k)
            {
                uint actual = useDebugLibrary ? GetRichFieldCountDebug((uint)(k + 1)) : GetRichFieldCountRelease((uint)(k + 1));
                if (actual != counts[k]) { error = "Native rich layout field count mismatch for kind " + (k + 1) + "."; return false; }
                string[] names = RichFieldNames[k];
                for (uint i = 0; i < actual; ++i)
                {
                    uint nativeOffset = useDebugLibrary ? GetRichFieldOffsetDebug((uint)(k + 1), i) : GetRichFieldOffsetRelease((uint)(k + 1), i);
                    uint managedOffset = (uint)Marshal.OffsetOf(types[k], names[i]).ToInt64();
                    if (nativeOffset != managedOffset) { error = "Native rich layout offset mismatch for kind " + (k + 1) + ", field " + i + "."; return false; }
                }
            }
            // The descriptor redundantly identifies the fields callers must use.
            if (f.snapshot_channels_offset != Offset<SbRichSnapshot>("channels") || f.snapshot_contributors_offset != Offset<SbRichSnapshot>("contributors") || f.snapshot_utf8_offset != Offset<SbRichSnapshot>("utf8") || f.snapshot_channel_count_offset != Offset<SbRichSnapshot>("channel_count") || f.snapshot_contributor_count_offset != Offset<SbRichSnapshot>("contributor_count") || f.snapshot_utf8_bytes_offset != Offset<SbRichSnapshot>("utf8_bytes") || f.copy_channels_offset != Offset<SbRichCopy>("channels") || f.copy_contributors_offset != Offset<SbRichCopy>("contributors") || f.copy_utf8_offset != Offset<SbRichCopy>("utf8") || f.copy_header_offset != Offset<SbRichCopy>("header") || f.channel_anchor_offset_offset != Offset<SbRichChannel>("anchor_offset") || f.channel_contributor_offset_offset != Offset<SbRichChannel>("contributor_offset") || f.channel_has_z_offset != Offset<SbRichChannel>("has_z") || f.channel_requested_position_offset != Offset<SbRichChannel>("requested_position_x") || f.contributor_reason_offset_offset != Offset<SbRichContributor>("reason_offset") || f.contributor_support_identifier_offset != Offset<SbRichContributor>("support_identifier_offset") || f.contributor_support_plane_to_world_offset != Offset<SbRichContributor>("support_plane_to_world_row_major")) { error = "Native rich descriptor offsets are incompatible."; return false; }
            return true;
        }
        private static uint Offset<T>(string field) { return (uint)Marshal.OffsetOf(typeof(T), field).ToInt64(); }
        private static readonly string[][] RichFieldNames = {
            new[] { "descriptor_size","feature_version","snapshot_size","channel_size","contributor_size","copy_size","snapshot_channels_offset","snapshot_contributors_offset","snapshot_utf8_offset","snapshot_channel_count_offset","snapshot_contributor_count_offset","snapshot_utf8_bytes_offset","copy_channels_offset","copy_contributors_offset","copy_utf8_offset","copy_header_offset","channel_anchor_offset_offset","channel_contributor_offset_offset","channel_has_z_offset","channel_requested_position_offset","contributor_reason_offset_offset","contributor_support_identifier_offset","contributor_support_plane_to_world_offset","max_channels","max_contributors","max_utf8_bytes","coordinate_convention","monotonic_clock_id","monotonic_clock_epoch" },
            new[] { "producer_lifetime","revision","monotonic_clock_id","monotonic_clock_epoch","receiver_ingress_monotonic_ns","receiver_solve_monotonic_ns","source_epoch","source_sequence","source_observation_sequence","source_actor_space","source_actor_element","relay_source_space","receiver_space","receiver_actor","policy_revision","tree_revision","counterpart_revision","support_revision","source_time_seconds","source_clock_seconds","status","source_timing_mode","source_time_known","source_clock_known","source_observation_sequence_known","known_revision_mask","unknown_revision_mask","failure_code" },
            new[] { "anchor_offset","anchor_length","contributor_offset","contributor_count","family","solver_status","geometry","goal_kind","has_goal","has_z","has_requested_position","reach_projected","referent_id","arrival_seconds","position_x","position_y","position_z","yaw","weight","reach_displacement_m","requested_position_x","requested_position_y" },
            new[] { "referent_id","source_space","source_element","resolved_local_element","binding_revision","snapshot_ordinal","disposition","grounding_mode","has_mapped_support","chart_version_known","binding_revision_known","reason_offset","reason_length","support_identifier_offset","support_identifier_length","applied_weight","mapped_support_x","mapped_support_y","mapped_support_z","support_plane_to_world_row_major" },
            new[] { "header","channels","contributors","utf8","channel_count","contributor_count","utf8_bytes","reserved" },
            new[] { "struct_size","channel_capacity","contributor_capacity","utf8_capacity","channels","contributors","utf8","header","required_channels","required_contributors","required_utf8_bytes","reserved" }
        };

        [MonoPInvokeCallback(typeof(OnRichNativeDelegate))]
        private static void OnRichCallback(IntPtr snapshotPointer, IntPtr data)
        {
            Action<RichRealizationSnapshot> callback = null; long space = 0;
            lock (richCallbackLock) { richCallbacks.TryGetValue(data.ToInt64(), out callback); richCallbackSpaceByToken.TryGetValue(data.ToInt64(), out space); }
            if (callback == null) return; // stale userdata may never select a replacement.
            RichRealizationSnapshot snapshot; string error;
            if (!TryGetRichFeature(out SbRichFeature feature, out error) || !TryCopyRichSnapshotPointer(snapshotPointer, feature, out snapshot, out error)) { ReportRichCallbackFailure(space, error); return; }
            InvokeRichCallbackObservers(space, callback, snapshot);
        }
        // Action may be multicast. A single observer failure is isolated so it
        // cannot prevent another owner from receiving the same frozen native
        // transaction. This runs after callback-map lookup and snapshot copy,
        // never under richCallbackLock.
        private static void InvokeRichCallbackObservers(long space,
                                                        Action<RichRealizationSnapshot> callback,
                                                        RichRealizationSnapshot snapshot)
        {
            foreach (Delegate observer in callback.GetInvocationList())
            {
                try { ((Action<RichRealizationSnapshot>)observer)(snapshot); }
                catch (Exception e) { ReportRichCallbackFailure(space, "Rich callback threw: " + e.Message); }
            }
        }
        private static void ReportRichCallbackFailure(long space, string error) { Action<long, string> sink = RichCallbackRejected; if (sink != null) { try { sink(space, error); } catch { } } }
        private static bool TryCopyRichSnapshotPointer(IntPtr pointer, SbRichFeature feature, out RichRealizationSnapshot snapshot, out string error)
        { snapshot = null; if (pointer == IntPtr.Zero) { error = "Native rich callback supplied null snapshot."; return false; } try { SbRichSnapshot view = (SbRichSnapshot)Marshal.PtrToStructure(pointer, typeof(SbRichSnapshot)); return TryCopyRichSnapshot(ref view, feature, out snapshot, out error); } catch (Exception e) { error = "Malformed native rich snapshot: " + e.Message; return false; } }
        private static bool TryCopyRichSnapshot(ref SbRichSnapshot v, SbRichFeature f, out RichRealizationSnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (v.reserved != 0 || v.channel_count > f.max_channels || v.contributor_count > f.max_contributors || v.utf8_bytes > f.max_utf8_bytes || (v.channel_count != 0 && v.channels == IntPtr.Zero) || (v.contributor_count != 0 && v.contributors == IntPtr.Zero) || (v.utf8_bytes != 0 && v.utf8 == IntPtr.Zero)) { error = "Malformed native rich snapshot bounds."; return false; }
            try
            {
                byte[] bytes = new byte[v.utf8_bytes]; if (bytes.Length != 0) Marshal.Copy(v.utf8, bytes, 0, bytes.Length);
                var contributors = new SbRichContributor[v.contributor_count]; for (int i = 0; i < contributors.Length; ++i) contributors[i] = (SbRichContributor)Marshal.PtrToStructure(IntPtr.Add(v.contributors, checked(i * Marshal.SizeOf(typeof(SbRichContributor)))), typeof(SbRichContributor));
                var rows = new SbRichChannel[v.channel_count]; var covered = new bool[contributors.Length]; var ordinals = new HashSet<uint>();
                for (int i = 0; i < rows.Length; ++i)
                {
                    rows[i] = (SbRichChannel)Marshal.PtrToStructure(IntPtr.Add(v.channels, checked(i * Marshal.SizeOf(typeof(SbRichChannel)))), typeof(SbRichChannel));
                    if (rows[i].contributor_offset > contributors.Length || rows[i].contributor_count > contributors.Length - rows[i].contributor_offset) throw new ArgumentException("Invalid native contributor range.");
                    for (int j = 0; j < rows[i].contributor_count; ++j) { int index = checked((int)rows[i].contributor_offset + j); if (covered[index]) throw new ArgumentException("Native contributor ranges overlap."); covered[index] = true; if (!ordinals.Add(contributors[index].snapshot_ordinal)) throw new ArgumentException("Native contributor ordinals are not unique."); }
                }
                for (int i = 0; i < covered.Length; ++i) if (!covered[i]) throw new ArgumentException("Native contributor ranges are not exhaustive.");
                var channels = new List<RichRealizationChannel>((int)v.channel_count); for (int i = 0; i < rows.Length; ++i) channels.Add(CopyChannel(rows[i], contributors, bytes));
                snapshot = new RichRealizationSnapshot(CopyHeader(v.header), channels); return true;
            }
            catch (Exception e) { error = "Malformed native rich snapshot: " + e.Message; return false; }
        }
        private static RichRealizationHeader CopyHeader(SbRichHeader h)
        {
            if (h.producer_lifetime == 0 || h.revision == 0 || h.monotonic_clock_id == 0 || h.monotonic_clock_epoch == 0 || h.receiver_ingress_monotonic_ns > h.receiver_solve_monotonic_ns || h.status < 1 || h.status > 3 || h.source_timing_mode > 2 || h.failure_code > 1 || !Bool(h.source_time_known) || !Bool(h.source_clock_known) || !Bool(h.source_observation_sequence_known) || (h.known_revision_mask & h.unknown_revision_mask) != 0 || (h.known_revision_mask | h.unknown_revision_mask) != RichUnknownRevisionBits) throw new ArgumentException("Invalid native rich header.");
            bool time = h.source_time_known != 0, clock = h.source_clock_known != 0;
            bool hasReceiverTimes = h.receiver_ingress_monotonic_ns != 0 || h.receiver_solve_monotonic_ns != 0;
            if (hasReceiverTimes && (h.receiver_ingress_monotonic_ns == 0 || h.receiver_solve_monotonic_ns == 0)) throw new ArgumentException("Partial native receipt/solve time.");
            bool sourceCausal = h.source_timing_mode == 1 || h.source_timing_mode == 2;
            if (h.source_observation_sequence_known != 0 && (h.source_timing_mode != 1 || h.source_observation_sequence < 0)) throw new ArgumentException("Invalid native source observation sequence.");
            return new RichRealizationHeader(h.receiver_space, h.receiver_actor, h.producer_lifetime, h.revision, (RichSnapshotStatus)h.status, (RichSourceProvenanceMode)h.source_timing_mode, new RichKnownUInt64(sourceCausal, h.source_epoch), new RichKnownInt64(sourceCausal, h.source_sequence), new RichKnownInt64(h.source_observation_sequence_known != 0, h.source_observation_sequence), new RichKnownDouble(time, h.source_time_seconds), new RichKnownDouble(clock, h.source_clock_seconds), new RichKnownUInt64(true, h.monotonic_clock_id), new RichKnownUInt64(true, h.monotonic_clock_epoch), new RichKnownUInt64(hasReceiverTimes, h.receiver_ingress_monotonic_ns), new RichKnownUInt64(hasReceiverTimes, h.receiver_solve_monotonic_ns), UnknownId(h.source_actor_space), UnknownId(h.source_actor_element), UnknownId(h.relay_source_space), Revision(h.policy_revision, h.known_revision_mask, 1), Revision(h.tree_revision, h.known_revision_mask, 2), Revision(h.counterpart_revision, h.known_revision_mask, 4), Revision(h.support_revision, h.known_revision_mask, 8), h.known_revision_mask, h.unknown_revision_mask, h.failure_code);
        }
        private static RichRealizationChannel CopyChannel(SbRichChannel c, SbRichContributor[] all, byte[] utf8)
        {
            if (!Bool(c.has_goal) || !Bool(c.has_z) || !Bool(c.has_requested_position) || !Bool(c.reach_projected) || c.family < 0 || c.family > 2 || c.solver_status < 0 || c.solver_status > 2 || c.geometry < 0 || c.geometry > 5 || c.goal_kind < 0 || c.goal_kind > 4 || !Finite(c.arrival_seconds) || !Finite(c.position_x) || !Finite(c.position_y) || !Finite(c.position_z) || !Finite(c.yaw) || !Finite(c.weight) || !Finite(c.reach_displacement_m) || !Finite(c.requested_position_x) || !Finite(c.requested_position_y) || c.weight < 0 || c.reach_displacement_m < 0 || c.contributor_offset > all.Length || c.contributor_count > all.Length - c.contributor_offset) throw new ArgumentException("Invalid native rich channel.");
            var list = new List<RichRealizationContributor>((int)c.contributor_count); for (int i = 0; i < c.contributor_count; ++i) list.Add(CopyContributor(all[c.contributor_offset + i], utf8));
            return new RichRealizationChannel(Utf8(utf8, c.anchor_offset, c.anchor_length, true), (RichRelationFamily)c.family, (RichSolverStatus)c.solver_status, (RichGeometry)c.geometry, (RichGoalKind)c.goal_kind, c.has_goal != 0, UnknownId(c.referent_id), c.arrival_seconds, new RichPosition(c.position_x, c.position_y, c.position_z), c.has_z != 0, c.yaw, c.weight, c.has_requested_position != 0, c.requested_position_x, c.requested_position_y, c.reach_projected != 0, c.reach_displacement_m, list);
        }
        private static RichRealizationContributor CopyContributor(SbRichContributor c, byte[] utf8)
        {
            if (!Bool(c.has_mapped_support) || !Bool(c.chart_version_known) || !Bool(c.binding_revision_known) || c.grounding_mode < 0 || c.grounding_mode > 2 || !Finite(c.applied_weight) || c.applied_weight < 0) throw new ArgumentException("Invalid native rich contributor.");
            if (c.support_plane_to_world_row_major == null || c.support_plane_to_world_row_major.Length != 16) throw new ArgumentException("Invalid native rich matrix.");
            for (int i = 0; i < 16; ++i) if (!Finite(c.support_plane_to_world_row_major[i])) throw new ArgumentException("Non-finite native rich matrix.");
            if (c.has_mapped_support != 0 && (!Near(c.support_plane_to_world_row_major[12], 0) || !Near(c.support_plane_to_world_row_major[13], 0) || !Near(c.support_plane_to_world_row_major[14], 0) || !Near(c.support_plane_to_world_row_major[15], 1))) throw new ArgumentException("Known support matrix is not affine.");
            return new RichRealizationContributor(UnknownId(c.referent_id), UnknownId(c.source_space), UnknownId(c.source_element), UnknownId(c.resolved_local_element), new RichKnownUInt64(c.binding_revision_known != 0, c.binding_revision), c.snapshot_ordinal, c.applied_weight, c.has_mapped_support != 0, new RichPosition(c.mapped_support_x, c.mapped_support_y, c.mapped_support_z), (RichGroundingMode)c.grounding_mode, (RichContributorDisposition)c.disposition /* unknown values → Unrecognized in the constructor */, Utf8(utf8, c.reason_offset, c.reason_length, false), c.chart_version_known != 0, c.binding_revision_known != 0, Utf8(utf8, c.support_identifier_offset, c.support_identifier_length, false), c.has_mapped_support != 0, new RichMatrix4x4(c.support_plane_to_world_row_major));
        }
        private static RichKnownInt64 UnknownId(long id) { return new RichKnownInt64(id != -1, id); }
        private static RichKnownUInt64 Revision(ulong value, uint known, uint bit) { return new RichKnownUInt64((known & bit) != 0, value); }
        private static bool Bool(uint value) { return value == 0 || value == 1; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Near(double value, double expected) { return Math.Abs(value - expected) <= 1e-9; }
        private static string Utf8(byte[] bytes, uint offset, uint length, bool nonEmpty)
        { if (offset > bytes.Length || length > bytes.Length - offset || (nonEmpty && length == 0)) throw new ArgumentException("Invalid UTF-8 range."); return new UTF8Encoding(false, true).GetString(bytes, (int)offset, (int)length); }
        private static bool IsRichLoadFailure(Exception e) { return e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException; }
    }
}
