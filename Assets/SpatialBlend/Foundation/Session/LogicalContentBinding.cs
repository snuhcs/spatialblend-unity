// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>
    /// The two durable, geometry-free logical schemas owned by Core N2.  This
    /// Foundation seam deliberately does not parse either payload: board
    /// coordinates and game-piece values remain canonical schema data.
    /// </summary>
    public enum LogicalContentSchema
    {
        BoardStrokesV1,
        KeyedValuesV1
    }

    public enum LogicalContentOperationKind
    {
        Upsert,
        Erase,
        Despawn
    }

    /// <summary>Detached canonical logical entry, never a Unity object or pose.</summary>
    public sealed class LogicalContentEntry
    {
        public LogicalContentEntry(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public string Value { get; }
    }

    /// <summary>
    /// A verified N2 mutation.  The N2/native ingress is responsible for
    /// authentication and provenance validation before constructing this DTO;
    /// this reducer only proves local snapshot/tail continuity and schema.
    /// </summary>
    public sealed class VerifiedLogicalContentMutation
    {
        public VerifiedLogicalContentMutation(ulong predecessorRevision, ulong committedRevision,
                                               LogicalContentOperationKind kind, string key, string value)
        {
            PredecessorRevision = predecessorRevision;
            CommittedRevision = committedRevision;
            Kind = kind;
            Key = key;
            Value = value;
        }

        public ulong PredecessorRevision { get; }
        public ulong CommittedRevision { get; }
        public LogicalContentOperationKind Kind { get; }
        public string Key { get; }
        public string Value { get; }
    }

    /// <summary>
    /// N2 snapshot fields needed by the Unity-independent recovery consumer.
    /// Its identity is deliberately copied separately from the U1 receipt so a
    /// stale snapshot cannot borrow a newer room/object lifetime.
    /// </summary>
    public sealed class VerifiedLogicalContentSnapshot
    {
        public VerifiedLogicalContentSnapshot(string roomId, ulong roomEpoch, string objectId,
                                              ulong objectGeneration, string schemaId,
                                              LogicalContentSchema schema, ulong committedRevision,
                                              ulong tailWatermark, ulong compactedThroughRevision,
                                              bool tombstoned, IEnumerable<LogicalContentEntry> entries)
        {
            RoomId = roomId;
            RoomEpoch = roomEpoch;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            SchemaId = schemaId;
            Schema = schema;
            CommittedRevision = committedRevision;
            TailWatermark = tailWatermark;
            CompactedThroughRevision = compactedThroughRevision;
            Tombstoned = tombstoned;
            Entries = CopyEntries(entries);
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public string SchemaId { get; }
        public LogicalContentSchema Schema { get; }
        public ulong CommittedRevision { get; }
        public ulong TailWatermark { get; }
        public ulong CompactedThroughRevision { get; }
        public bool Tombstoned { get; }
        public IReadOnlyList<LogicalContentEntry> Entries { get; }

        private static IReadOnlyList<LogicalContentEntry> CopyEntries(IEnumerable<LogicalContentEntry> entries)
        {
            var copied = new List<LogicalContentEntry>();
            if (entries != null)
            {
                foreach (LogicalContentEntry entry in entries)
                    copied.Add(entry == null ? null : new LogicalContentEntry(entry.Key, entry.Value));
            }
            return copied.AsReadOnly();
        }
    }

    /// <summary>
    /// An exact N2 tail after a snapshot watermark.  `ExclusiveWatermark`
    /// must be the snapshot revision and `TerminalRevision` must describe all
    /// mutations supplied here; prefixes and unordered tails are rejected.
    /// </summary>
    public sealed class VerifiedLogicalContentTail
    {
        public VerifiedLogicalContentTail(string roomId, ulong roomEpoch, string objectId,
                                          ulong objectGeneration, string schemaId,
                                          ulong exclusiveWatermark, ulong terminalRevision,
                                          IEnumerable<VerifiedLogicalContentMutation> mutations)
        {
            RoomId = roomId;
            RoomEpoch = roomEpoch;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            SchemaId = schemaId;
            ExclusiveWatermark = exclusiveWatermark;
            TerminalRevision = terminalRevision;
            Mutations = CopyMutations(mutations);
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public string SchemaId { get; }
        public ulong ExclusiveWatermark { get; }
        public ulong TerminalRevision { get; }
        public IReadOnlyList<VerifiedLogicalContentMutation> Mutations { get; }

        private static IReadOnlyList<VerifiedLogicalContentMutation> CopyMutations(
            IEnumerable<VerifiedLogicalContentMutation> mutations)
        {
            var copied = new List<VerifiedLogicalContentMutation>();
            if (mutations != null)
            {
                foreach (VerifiedLogicalContentMutation mutation in mutations)
                {
                    copied.Add(mutation == null ? null : new VerifiedLogicalContentMutation(
                        mutation.PredecessorRevision, mutation.CommittedRevision, mutation.Kind,
                        mutation.Key, mutation.Value));
                }
            }
            return copied.AsReadOnly();
        }
    }

    /// <summary>
    /// Input supplied by the authenticated N2/N3 adapter and the U1 local
    /// binding registry.  It has no socket, participant, source clock,
    /// Transform, GameObject, or authority-creation mechanism.
    /// </summary>
    public sealed class VerifiedLogicalContentRecovery
    {
        public VerifiedLogicalContentRecovery(SharedToLocalBindingReceipt binding,
                                              VerifiedLogicalContentSnapshot snapshot,
                                              VerifiedLogicalContentTail tail)
        {
            Binding = binding;
            Snapshot = snapshot;
            Tail = tail;
        }

        public SharedToLocalBindingReceipt Binding { get; }
        public VerifiedLogicalContentSnapshot Snapshot { get; }
        public VerifiedLogicalContentTail Tail { get; }
    }

    /// <summary>Immutable, local-only state given to an application adapter.</summary>
    public sealed class LogicalContentProjection
    {
        internal LogicalContentProjection(SharedToLocalBindingReceipt binding, LogicalContentSchema schema,
                                          ulong revision, bool tombstoned,
                                          IDictionary<string, string> entries)
        {
            RoomId = binding.RoomId;
            RoomEpoch = binding.RoomEpoch;
            ObjectId = binding.ObjectId;
            ObjectGeneration = binding.ObjectGeneration;
            SchemaId = binding.SchemaId;
            LocalEndpointId = binding.LocalEndpointId;
            LocalBindingGeneration = binding.LocalBindingGeneration;
            Schema = schema;
            CommittedRevision = revision;
            Tombstoned = tombstoned;
            var copied = new List<LogicalContentEntry>();
            foreach (KeyValuePair<string, string> entry in entries)
                copied.Add(new LogicalContentEntry(entry.Key, entry.Value));
            Entries = copied.AsReadOnly();
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public string SchemaId { get; }
        public string LocalEndpointId { get; }
        public ulong LocalBindingGeneration { get; }
        public LogicalContentSchema Schema { get; }
        public ulong CommittedRevision { get; }
        public bool Tombstoned { get; }
        public IReadOnlyList<LogicalContentEntry> Entries { get; }
    }

    /// <summary>
    /// The only application boundary.  An implementation must apply or reject
    /// one complete projection atomically; it cannot receive a physical pose,
    /// remote authority, or a partial tail from this framework component.
    /// </summary>
    public interface ILogicalContentSink
    {
        bool TryApply(LogicalContentProjection projection, out string error);
    }

    /// <summary>
    /// Owner-thread, receiver-local N2 recovery reducer.  It turns an exact
    /// snapshot + contiguous tail into a single local content projection only
    /// while the matching U1 binding generation remains current.  It does not
    /// create Unity objects, replicate transforms, resolve names, or make a
    /// network arrival spatially fresh.
    /// </summary>
    public sealed class LogicalContentBindingReducer : IDisposable
    {
        public const int MaxEntries = 512;
        public const int MaxTailMutations = 512;
        public const int MaxIdBytes = 96;
        public const int MaxValueBytes = 4096;

        private readonly SharedToLocalBindingRegistry _bindings;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private AppliedState _applied;
        private bool _applying;
        private bool _disposed;

        private sealed class AppliedState
        {
            internal SharedToLocalBindingReceipt Binding;
            internal LogicalContentSchema Schema;
            internal ulong Revision;
            internal bool Tombstoned;
            internal Dictionary<string, string> Entries;
        }

        public LogicalContentBindingReducer(SharedToLocalBindingRegistry bindings)
        {
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        }

        public bool TryRecoverAndApply(VerifiedLogicalContentRecovery recovery,
                                       ILogicalContentSink sink, out string error)
        {
            error = null;
            if (!OnOwnerThread(out error) || recovery == null || sink == null)
            {
                if (error == null) error = "A recovery input and local content sink are required.";
                return false;
            }
            if (_applying)
            {
                error = "Content sink reentry is not permitted.";
                return false;
            }
            if (!TryBuildCandidate(recovery, out AppliedState candidate, out error)) return false;
            if (SameApplied(_applied, candidate)) return true;

            _applying = true;
            bool applied;
            try
            {
                applied = sink.TryApply(new LogicalContentProjection(candidate.Binding, candidate.Schema,
                    candidate.Revision, candidate.Tombstoned, candidate.Entries), out error);
            }
            catch (Exception exception)
            {
                error = "Local content sink threw: " + exception.GetType().Name;
                applied = false;
            }
            finally
            {
                _applying = false;
            }
            if (!applied)
            {
                if (string.IsNullOrEmpty(error)) error = "Local content sink rejected the projection.";
                return false;
            }
            // A callback may have retired/replaced the local declaration.  Do
            // not publish a successful state behind that newer generation.
            if (!candidate.Tombstoned && !_bindings.IsCurrent(candidate.Binding))
            {
                error = "The exact local binding generation changed during sink application.";
                return false;
            }
            if (candidate.Tombstoned && !MatchesAppliedBinding(_applied, candidate.Binding))
            {
                error = "The tombstone no longer matches the locally applied binding generation.";
                return false;
            }
            _applied = candidate;
            return true;
        }

        /// <summary>
        /// Explicit local teardown for an unavailable local endpoint.  It is
        /// intentionally not a remote tombstone or a replacement binding.
        /// </summary>
        public void InvalidateLocalBinding()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread || _disposed || _applying) return;
            _applied = null;
        }

        public void Dispose()
        {
            if (_disposed || Thread.CurrentThread.ManagedThreadId != _ownerThread || _applying) return;
            _applied = null;
            _disposed = true;
        }

        private bool TryBuildCandidate(VerifiedLogicalContentRecovery recovery, out AppliedState candidate,
                                       out string error)
        {
            candidate = null;
            error = null;
            SharedToLocalBindingReceipt binding = recovery.Binding;
            VerifiedLogicalContentSnapshot snapshot = recovery.Snapshot;
            VerifiedLogicalContentTail tail = recovery.Tail;
            if (binding == null || snapshot == null || tail == null ||
                !SameObject(binding, snapshot.RoomId, snapshot.RoomEpoch, snapshot.ObjectId,
                    snapshot.ObjectGeneration, snapshot.SchemaId) ||
                !SameObject(binding, tail.RoomId, tail.RoomEpoch, tail.ObjectId,
                    tail.ObjectGeneration, tail.SchemaId) ||
                snapshot.CommittedRevision == 0 || snapshot.TailWatermark != snapshot.CommittedRevision ||
                snapshot.CompactedThroughRevision > snapshot.CommittedRevision ||
                tail.ExclusiveWatermark != snapshot.CommittedRevision ||
                tail.TerminalRevision < snapshot.CommittedRevision ||
                tail.Mutations.Count > MaxTailMutations || !SchemaMatches(snapshot.Schema, binding.SchemaId))
            {
                error = "The N2 snapshot/tail and exact local binding identity are invalid.";
                return false;
            }
            bool terminalTombstone = snapshot.Tombstoned;
            if (terminalTombstone && (snapshot.Entries.Count != 0 || tail.Mutations.Count != 0 ||
                                      tail.TerminalRevision != snapshot.CommittedRevision))
            {
                error = "A tombstoned snapshot cannot contain a tail or content entries.";
                return false;
            }
            // An active recovery must be fenced by the exact current U1
            // capability at its terminal revision.  Tombstones instead clear
            // only an already-applied exact generation, even after U1 has
            // withdrawn that generation in response to the terminal record.
            if (!terminalTombstone &&
                (!_bindings.IsCurrent(binding) || binding.ObjectRevision != tail.TerminalRevision))
            {
                error = "The active recovery does not hold the current local binding generation.";
                return false;
            }
            if (terminalTombstone &&
                (!MatchesAppliedBinding(_applied, binding) || snapshot.CommittedRevision < binding.ObjectRevision))
            {
                error = "A tombstone can only clear its exact locally applied binding generation.";
                return false;
            }
            // A duplicate recovery may legitimately begin from an older
            // compact snapshot and replay its complete tail to the same
            // terminal revision.  Only a *terminal* prefix is stale.
            if (_applied != null && SameBindingLifetime(_applied.Binding, binding) &&
                tail.TerminalRevision < _applied.Revision)
            {
                error = "The recovered snapshot is stale relative to local content.";
                return false;
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!TryCopySnapshotEntries(snapshot, entries, out error)) return false;
            ulong expected = snapshot.CommittedRevision;
            foreach (VerifiedLogicalContentMutation mutation in tail.Mutations)
            {
                if (mutation == null || mutation.PredecessorRevision != expected ||
                    mutation.CommittedRevision != expected + 1 ||
                    !TryApplyMutation(snapshot.Schema, entries, ref terminalTombstone, mutation, out error))
                    return false;
                expected = mutation.CommittedRevision;
            }
            if (expected != tail.TerminalRevision)
            {
                error = "The N2 tail is incomplete or out of order.";
                return false;
            }
            if (terminalTombstone && entries.Count != 0)
            {
                error = "A despawn must remove every logical content entry.";
                return false;
            }
            candidate = new AppliedState
            {
                Binding = binding,
                Schema = snapshot.Schema,
                Revision = tail.TerminalRevision,
                Tombstoned = terminalTombstone,
                Entries = entries
            };
            return true;
        }

        private static bool TryCopySnapshotEntries(VerifiedLogicalContentSnapshot snapshot,
                                                   IDictionary<string, string> entries, out string error)
        {
            error = null;
            if (snapshot.Entries.Count > MaxEntries)
            {
                error = "The N2 snapshot exceeds its logical entry bound.";
                return false;
            }
            foreach (LogicalContentEntry entry in snapshot.Entries)
            {
                if (!ValidId(entry == null ? null : entry.Key) || !ValidValue(entry == null ? null : entry.Value) ||
                    entries.ContainsKey(entry.Key))
                {
                    error = "The N2 snapshot contains an invalid or duplicate logical entry.";
                    return false;
                }
                entries.Add(entry.Key, entry.Value);
            }
            return true;
        }

        private static bool TryApplyMutation(LogicalContentSchema schema, IDictionary<string, string> entries,
                                             ref bool tombstoned, VerifiedLogicalContentMutation mutation,
                                             out string error)
        {
            error = null;
            if (tombstoned)
            {
                error = "A tombstoned logical object cannot receive a tail mutation.";
                return false;
            }
            if (mutation.Kind == LogicalContentOperationKind.Despawn)
            {
                if (!string.IsNullOrEmpty(mutation.Key) || !string.IsNullOrEmpty(mutation.Value))
                {
                    error = "A despawn mutation cannot carry content payload.";
                    return false;
                }
                entries.Clear();
                tombstoned = true;
                return true;
            }
            if (!ValidId(mutation.Key))
            {
                error = "A logical content mutation requires a bounded key.";
                return false;
            }
            if (mutation.Kind == LogicalContentOperationKind.Upsert)
            {
                if (!ValidValue(mutation.Value) || (!entries.ContainsKey(mutation.Key) && entries.Count >= MaxEntries))
                {
                    error = "The logical content upsert is invalid or exceeds entry capacity.";
                    return false;
                }
                entries[mutation.Key] = mutation.Value;
                return true;
            }
            if (mutation.Kind == LogicalContentOperationKind.Erase && string.IsNullOrEmpty(mutation.Value))
            {
                entries.Remove(mutation.Key);
                return true;
            }
            error = "The logical content operation is invalid.";
            return false;
        }

        private bool OnOwnerThread(out string error)
        {
            error = null;
            if (_disposed) { error = "The logical content reducer is disposed."; return false; }
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
            {
                error = "Logical content binding requires its owner thread.";
                return false;
            }
            return true;
        }

        private static bool ValidId(string value) => AuthenticatedLogicalObjectReference.ValidId(value);
        private static bool ValidValue(string value) => !string.IsNullOrEmpty(value) && value.Length <= MaxValueBytes &&
                                                       value.IndexOf('\0') < 0;
        private static bool SameObject(SharedToLocalBindingReceipt binding, string roomId, ulong roomEpoch,
                                       string objectId, ulong objectGeneration, string schemaId) =>
            binding.RoomId == roomId && binding.RoomEpoch == roomEpoch && binding.ObjectId == objectId &&
            binding.ObjectGeneration == objectGeneration && binding.SchemaId == schemaId;
        private static bool SameBindingLifetime(SharedToLocalBindingReceipt left, SharedToLocalBindingReceipt right) =>
            left != null && right != null && left.RoomId == right.RoomId && left.RoomEpoch == right.RoomEpoch &&
            left.ObjectId == right.ObjectId && left.ObjectGeneration == right.ObjectGeneration &&
            left.AuthorityEpoch == right.AuthorityEpoch && left.SchemaId == right.SchemaId &&
            left.LocalBindingGeneration == right.LocalBindingGeneration;
        private static bool MatchesAppliedBinding(AppliedState applied, SharedToLocalBindingReceipt binding) =>
            applied != null && SameBindingLifetime(applied.Binding, binding) &&
            applied.Binding.LocalEndpointId == binding.LocalEndpointId;
        private static bool SchemaMatches(LogicalContentSchema schema, string schemaId) =>
            (schema == LogicalContentSchema.BoardStrokesV1 && schemaId == "board-strokes/v1") ||
            (schema == LogicalContentSchema.KeyedValuesV1 && schemaId == "keyed-values/v1");
        private static bool SameApplied(AppliedState left, AppliedState right)
        {
            if (left == null || right == null || !SameBindingLifetime(left.Binding, right.Binding) ||
                left.Revision != right.Revision || left.Schema != right.Schema || left.Tombstoned != right.Tombstoned ||
                left.Entries.Count != right.Entries.Count) return false;
            foreach (KeyValuePair<string, string> entry in left.Entries)
                if (!right.Entries.TryGetValue(entry.Key, out string value) || value != entry.Value) return false;
            return true;
        }
    }
}
