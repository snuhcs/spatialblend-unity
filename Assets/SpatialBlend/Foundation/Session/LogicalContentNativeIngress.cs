// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Immutable managed copy of one native N6/N2 recovery record.  Its byte
    /// fields are copied on construction and no field is an authority, local
    /// binding, scene object, pose, source clock, or transport handle.
    /// </summary>
    public sealed class NativeLogicalContentRecoveryCopy
    {
        public NativeLogicalContentRecoveryCopy(byte[] roomId, ulong roomEpoch, byte[] objectId,
                                                ulong objectGeneration, byte[] schemaId, uint schema,
                                                ulong committedRevision, ulong tailWatermark,
                                                ulong compactedThroughRevision, bool tombstoned,
                                                IEnumerable<NativeLogicalContentEntryCopy> entries,
                                                ulong exclusiveWatermark, ulong terminalRevision,
                                                IEnumerable<NativeLogicalContentMutationCopy> mutations)
        {
            _roomId = Copy(roomId); RoomEpoch = roomEpoch; _objectId = Copy(objectId);
            ObjectGeneration = objectGeneration; _schemaId = Copy(schemaId); Schema = schema;
            CommittedRevision = committedRevision; TailWatermark = tailWatermark;
            CompactedThroughRevision = compactedThroughRevision; Tombstoned = tombstoned;
            Entries = CopyEntries(entries); ExclusiveWatermark = exclusiveWatermark;
            TerminalRevision = terminalRevision; Mutations = CopyMutations(mutations);
        }

        public byte[] RoomId { get { return Copy(_roomId); } }
        public ulong RoomEpoch { get; }
        public byte[] ObjectId { get { return Copy(_objectId); } }
        public ulong ObjectGeneration { get; }
        public byte[] SchemaId { get { return Copy(_schemaId); } }
        public uint Schema { get; }
        public ulong CommittedRevision { get; }
        public ulong TailWatermark { get; }
        public ulong CompactedThroughRevision { get; }
        public bool Tombstoned { get; }
        public IReadOnlyList<NativeLogicalContentEntryCopy> Entries { get; }
        public ulong ExclusiveWatermark { get; }
        public ulong TerminalRevision { get; }
        public IReadOnlyList<NativeLogicalContentMutationCopy> Mutations { get; }

        private readonly byte[] _roomId;
        private readonly byte[] _objectId;
        private readonly byte[] _schemaId;

        private static byte[] Copy(byte[] value)
        {
            if (value == null) return null;
            var copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }
        private static IReadOnlyList<NativeLogicalContentEntryCopy> CopyEntries(
            IEnumerable<NativeLogicalContentEntryCopy> values)
        {
            var copied = new List<NativeLogicalContentEntryCopy>();
            if (values != null) foreach (NativeLogicalContentEntryCopy value in values)
                copied.Add(value == null ? null : new NativeLogicalContentEntryCopy(value.Key, value.Value));
            return copied.AsReadOnly();
        }
        private static IReadOnlyList<NativeLogicalContentMutationCopy> CopyMutations(
            IEnumerable<NativeLogicalContentMutationCopy> values)
        {
            var copied = new List<NativeLogicalContentMutationCopy>();
            if (values != null) foreach (NativeLogicalContentMutationCopy value in values)
                copied.Add(value == null ? null : new NativeLogicalContentMutationCopy(value.PredecessorRevision,
                    value.CommittedRevision, value.Kind, value.Key, value.Value));
            return copied.AsReadOnly();
        }
    }

    public sealed class NativeLogicalContentEntryCopy
    {
        private readonly byte[] _key, _value;
        public NativeLogicalContentEntryCopy(byte[] key, byte[] value) { _key = Clone(key); _value = Clone(value); }
        public byte[] Key { get { return Clone(_key); } }
        public byte[] Value { get { return Clone(_value); } }
        private static byte[] Clone(byte[] value) { return value == null ? null : (byte[])value.Clone(); }
    }

    public sealed class NativeLogicalContentMutationCopy
    {
        private readonly byte[] _key, _value;
        public NativeLogicalContentMutationCopy(ulong predecessorRevision, ulong committedRevision, uint kind,
                                                byte[] key, byte[] value)
        { PredecessorRevision = predecessorRevision; CommittedRevision = committedRevision; Kind = kind; _key = Clone(key); _value = Clone(value); }
        public ulong PredecessorRevision { get; }
        public ulong CommittedRevision { get; }
        public uint Kind { get; }
        public byte[] Key { get { return Clone(_key); } }
        public byte[] Value { get { return Clone(_value); } }
        private static byte[] Clone(byte[] value) { return value == null ? null : (byte[])value.Clone(); }
    }

    /// <summary>
    /// Owner-thread adapter from the native read-only N6 mailbox to the U1a
    /// reducer.  The opaque native token is supplied by an authenticated N6
    /// host; this class cannot create, destroy, submit, authenticate, or
    /// reconnect it.  A current U1 receipt remains a required explicit input.
    /// </summary>
    public sealed class LogicalContentNativeIngressAdapter : IDisposable
    {
        private readonly LogicalContentBindingReducer _reducer;
        private readonly ulong _nativeIngress;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private bool _disposed;

        public LogicalContentNativeIngressAdapter(LogicalContentBindingReducer reducer, ulong nativeIngress)
        {
            _reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
            if (nativeIngress == 0) throw new ArgumentOutOfRangeException(nameof(nativeIngress));
            _nativeIngress = nativeIngress;
        }

        public bool TryPollAndApply(SharedToLocalBindingReceipt binding, ILogicalContentSink sink, out string error)
        {
            error = null;
            if (!OnOwnerThread(out error)) return false;
            if (!SpatialBlendAPI.TryPollLogicalContentCopy(_nativeIngress,
                out NativeLogicalContentRecoveryCopy copied, out error)) return false;
            return TryApplyCopied(copied, binding, sink, out error);
        }

        /// <summary>Allows a host that already copied a callback record to
        /// dispatch it without granting it any local binding authority.</summary>
        public bool TryApplyCopied(NativeLogicalContentRecoveryCopy copied,
                                   SharedToLocalBindingReceipt binding,
                                   ILogicalContentSink sink, out string error)
        {
            error = null;
            if (!OnOwnerThread(out error) || copied == null) { if (error == null) error = "A copied native recovery is required."; return false; }
            if (!TryBuildRecovery(copied, binding, out VerifiedLogicalContentRecovery recovery, out error)) return false;
            return _reducer.TryRecoverAndApply(recovery, sink, out error);
        }

        public void Dispose() { if (!_disposed && Thread.CurrentThread.ManagedThreadId == _ownerThread) _disposed = true; }

        private bool OnOwnerThread(out string error)
        {
            error = null;
            if (_disposed) { error = "The native logical-content ingress adapter is disposed."; return false; }
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread) { error = "Native logical-content ingress requires its owner thread."; return false; }
            return true;
        }

        private static bool TryBuildRecovery(NativeLogicalContentRecoveryCopy copy,
                                             SharedToLocalBindingReceipt binding,
                                             out VerifiedLogicalContentRecovery recovery, out string error)
        {
            recovery = null; error = null;
            if (binding == null || !TryUtf8(copy.RoomId, LogicalContentBindingReducer.MaxIdBytes, true, out string room) ||
                !TryUtf8(copy.ObjectId, LogicalContentBindingReducer.MaxIdBytes, true, out string objectId) ||
                !TryUtf8(copy.SchemaId, LogicalContentBindingReducer.MaxIdBytes, true, out string schemaId) ||
                !TrySchema(copy.Schema, out LogicalContentSchema schema))
            { error = "The copied native logical-content identity or schema is invalid."; return false; }
            var entries = new List<LogicalContentEntry>();
            if (copy.Entries.Count > LogicalContentBindingReducer.MaxEntries) { error = "The copied native snapshot exceeds its entry bound."; return false; }
            foreach (NativeLogicalContentEntryCopy entry in copy.Entries)
            {
                if (entry == null || !TryUtf8(entry.Key, LogicalContentBindingReducer.MaxIdBytes, true, out string key) ||
                    !TryUtf8(entry.Value, LogicalContentBindingReducer.MaxValueBytes, true, out string value))
                { error = "The copied native snapshot entry is invalid UTF-8 or exceeds its bound."; return false; }
                entries.Add(new LogicalContentEntry(key, value));
            }
            var mutations = new List<VerifiedLogicalContentMutation>();
            if (copy.Mutations.Count > LogicalContentBindingReducer.MaxTailMutations) { error = "The copied native tail exceeds its mutation bound."; return false; }
            foreach (NativeLogicalContentMutationCopy mutation in copy.Mutations)
            {
                if (mutation == null || !TryKind(mutation.Kind, out LogicalContentOperationKind kind) ||
                    !TryUtf8(mutation.Key, LogicalContentBindingReducer.MaxIdBytes, false, out string key) ||
                    !TryUtf8(mutation.Value, LogicalContentBindingReducer.MaxValueBytes, false, out string value))
                { error = "The copied native tail mutation is invalid UTF-8, kind, or bound."; return false; }
                mutations.Add(new VerifiedLogicalContentMutation(mutation.PredecessorRevision,
                    mutation.CommittedRevision, kind, key, value));
            }
            var snapshot = new VerifiedLogicalContentSnapshot(room, copy.RoomEpoch, objectId,
                copy.ObjectGeneration, schemaId, schema, copy.CommittedRevision, copy.TailWatermark,
                copy.CompactedThroughRevision, copy.Tombstoned, entries);
            var tail = new VerifiedLogicalContentTail(room, copy.RoomEpoch, objectId, copy.ObjectGeneration,
                schemaId, copy.ExclusiveWatermark, copy.TerminalRevision, mutations);
            recovery = new VerifiedLogicalContentRecovery(binding, snapshot, tail);
            return true;
        }

        private static bool TrySchema(uint value, out LogicalContentSchema schema)
        { schema = value == 1 ? LogicalContentSchema.BoardStrokesV1 : LogicalContentSchema.KeyedValuesV1; return value == 1 || value == 2; }
        private static bool TryKind(uint value, out LogicalContentOperationKind kind)
        { kind = value == 1 ? LogicalContentOperationKind.Upsert : value == 2 ? LogicalContentOperationKind.Erase : LogicalContentOperationKind.Despawn; return value >= 1 && value <= 3; }
        private static bool TryUtf8(byte[] value, int maximum, bool required, out string text)
        {
            text = null;
            if (value == null || value.Length > maximum || (required && value.Length == 0)) return false;
            try { text = new UTF8Encoding(false, true).GetString(value); }
            catch (DecoderFallbackException) { return false; }
            return text.IndexOf('\0') < 0;
        }
    }
}

namespace SpatialBlend.Internal
{
    // Kept internal so only the U1a adapter can interpret native callback
    // pointers.  Managed callers receive a detached copied record instead.
    internal static partial class SpatialBlendAPI
    {
        private const int Ok = 1;
        private const int Empty = -6;
        [StructLayout(LayoutKind.Sequential)] private struct Bytes { internal IntPtr Pointer; internal uint Length; }
        [StructLayout(LayoutKind.Sequential)] private struct Entry { internal Bytes Key, Value; }
        [StructLayout(LayoutKind.Sequential)] private struct Mutation { internal ulong Predecessor, Committed; internal uint Kind, Reserved; internal Bytes Key, Value; }
        [StructLayout(LayoutKind.Sequential)] private struct Snapshot {
            internal uint StructSize, FeatureVersion; internal Bytes Room; internal ulong RoomEpoch; internal Bytes Object;
            internal ulong ObjectGeneration; internal Bytes SchemaId; internal uint Schema, Tombstoned; internal ulong Committed, Watermark, Compacted;
            internal IntPtr Entries; internal uint EntryCount, EntrySize;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Tail {
            internal uint StructSize, FeatureVersion; internal Bytes Room; internal ulong RoomEpoch; internal Bytes Object;
            internal ulong ObjectGeneration; internal Bytes SchemaId; internal uint Reserved; internal ulong Watermark, Terminal;
            internal IntPtr Mutations; internal uint MutationCount, MutationSize;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Callback(IntPtr snapshot, IntPtr tail, IntPtr userData);
        private sealed class CopyState { internal NativeLogicalContentRecoveryCopy Value; internal string Error; }
        private static readonly Callback callback = CopyCallback;

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_poll_logical_content_recovery")]
        private static extern int PollRelease(ulong ingress, Callback visitor, IntPtr userData);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_poll_logical_content_recovery")]
        private static extern int PollDebug(ulong ingress, Callback visitor, IntPtr userData);

        internal static bool TryPollLogicalContentCopy(ulong ingress, out NativeLogicalContentRecoveryCopy copy, out string error)
        {
            copy = null; error = null;
            if (!HasCompatibleAbi(out error)) return false;
            var state = new CopyState(); var handle = GCHandle.Alloc(state);
            try
            {
                int result = SpatialBlendAPI.useDebugLibrary ? PollDebug(ingress, callback, GCHandle.ToIntPtr(handle)) : PollRelease(ingress, callback, GCHandle.ToIntPtr(handle));
                if (result == Ok && state.Value != null) { copy = state.Value; return true; }
                error = state.Error ?? (result == Empty ? "No copied native logical-content recovery is queued." : "Native logical-content recovery polling failed (" + result + ").");
                return false;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            { error = "Native logical-content ingress feature unavailable: " + e.Message; return false; }
            finally { handle.Free(); }
        }

        private static bool HasCompatibleAbi(out string error)
        {
            error = null;
            try
            {
                uint version = 0, snapshotSize = 0, tailSize = 0, entrySize = 0, mutationSize = 0;
                uint written = SpatialBlendAPI.useDebugLibrary
                    ? GetFeatureDebug(ref version, sizeof(uint), out snapshotSize, out tailSize, out entrySize, out mutationSize)
                    : GetFeatureRelease(ref version, sizeof(uint), out snapshotSize, out tailSize, out entrySize, out mutationSize);
                if (written != sizeof(uint) * 6 || version != 1 || snapshotSize != Marshal.SizeOf<Snapshot>() ||
                    tailSize != Marshal.SizeOf<Tail>() || entrySize != Marshal.SizeOf<Entry>() ||
                    mutationSize != Marshal.SizeOf<Mutation>())
                {
                    error = "Native logical-content ingress ABI feature is incompatible.";
                    return false;
                }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            { error = "Native logical-content ingress feature unavailable: " + e.Message; return false; }
        }

        private static int CopyCallback(IntPtr snapshotPointer, IntPtr tailPointer, IntPtr userData)
        {
            var state = (CopyState)GCHandle.FromIntPtr(userData).Target;
            try
            {
                if (snapshotPointer == IntPtr.Zero || tailPointer == IntPtr.Zero)
                    throw new InvalidOperationException("Native logical-content recovery pointers are null.");
                var snapshot = Marshal.PtrToStructure<Snapshot>(snapshotPointer); var tail = Marshal.PtrToStructure<Tail>(tailPointer);
                if (snapshot.StructSize != Marshal.SizeOf<Snapshot>() || tail.StructSize != Marshal.SizeOf<Tail>() || snapshot.FeatureVersion != 1 || tail.FeatureVersion != 1 ||
                    snapshot.EntrySize != Marshal.SizeOf<Entry>() || tail.MutationSize != Marshal.SizeOf<Mutation>() || snapshot.EntryCount > LogicalContentBindingReducer.MaxEntries || tail.MutationCount > LogicalContentBindingReducer.MaxTailMutations ||
                    (snapshot.EntryCount != 0 && snapshot.Entries == IntPtr.Zero) || (tail.MutationCount != 0 && tail.Mutations == IntPtr.Zero)) throw new InvalidOperationException("Native logical-content ingress ABI layout is incompatible.");
                var entries = new List<NativeLogicalContentEntryCopy>();
                for (int index = 0; index < snapshot.EntryCount; ++index) { var entry = Marshal.PtrToStructure<Entry>(IntPtr.Add(snapshot.Entries, index * (int)snapshot.EntrySize)); entries.Add(new NativeLogicalContentEntryCopy(Read(entry.Key, LogicalContentBindingReducer.MaxIdBytes), Read(entry.Value, LogicalContentBindingReducer.MaxValueBytes))); }
                var mutations = new List<NativeLogicalContentMutationCopy>();
                for (int index = 0; index < tail.MutationCount; ++index) { var mutation = Marshal.PtrToStructure<Mutation>(IntPtr.Add(tail.Mutations, index * (int)tail.MutationSize)); mutations.Add(new NativeLogicalContentMutationCopy(mutation.Predecessor, mutation.Committed, mutation.Kind, Read(mutation.Key, LogicalContentBindingReducer.MaxIdBytes), Read(mutation.Value, LogicalContentBindingReducer.MaxValueBytes))); }
                state.Value = new NativeLogicalContentRecoveryCopy(Read(snapshot.Room, LogicalContentBindingReducer.MaxIdBytes), snapshot.RoomEpoch, Read(snapshot.Object, LogicalContentBindingReducer.MaxIdBytes), snapshot.ObjectGeneration,
                    Read(snapshot.SchemaId, LogicalContentBindingReducer.MaxIdBytes), snapshot.Schema, snapshot.Committed, snapshot.Watermark, snapshot.Compacted, snapshot.Tombstoned != 0,
                    entries, tail.Watermark, tail.Terminal, mutations);
                return Ok;
            }
            catch (Exception e) { state.Error = "Native logical-content recovery copy failed: " + e.GetType().Name; return -7; }
        }
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_logical_content_ingress_feature")]
        private static extern uint GetFeatureRelease(ref uint featureVersion, uint featureSize, out uint snapshotSize,
            out uint tailSize, out uint entrySize, out uint mutationSize);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_logical_content_ingress_feature")]
        private static extern uint GetFeatureDebug(ref uint featureVersion, uint featureSize, out uint snapshotSize,
            out uint tailSize, out uint entrySize, out uint mutationSize);

        private static byte[] Read(Bytes value, int maximum)
        {
            if (value.Length > maximum || (value.Length != 0 && value.Pointer == IntPtr.Zero)) throw new InvalidOperationException("Native logical-content bytes are invalid.");
            var copied = new byte[value.Length]; if (value.Length != 0) Marshal.Copy(value.Pointer, copied, 0, (int)value.Length); return copied;
        }
    }
}
