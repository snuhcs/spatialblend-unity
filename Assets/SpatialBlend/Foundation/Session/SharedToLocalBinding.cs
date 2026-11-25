// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>
    /// The lifecycle supplied by the authenticated room-object adapter.  This
    /// is deliberately not a Unity object lifecycle: a tombstone is durable
    /// room state and cannot be revived by a local prefab becoming available.
    /// </summary>
    public enum SharedLogicalObjectLifecycle
    {
        Active,
        Tombstoned
    }

    /// <summary>
    /// A committed logical-object reference received from the authenticated
    /// N6/N0 boundary.  Constructing this value does not authenticate a room;
    /// callers must only pass values copied from that boundary.  The registry
    /// never treats a claimed ID, reconnect, or a Unity object as authority.
    /// </summary>
    public sealed class AuthenticatedLogicalObjectReference
    {
        public AuthenticatedLogicalObjectReference(string roomId, ulong roomEpoch,
                                                    string objectId, ulong objectGeneration,
                                                    ulong objectRevision, ulong authorityEpoch,
                                                    string schemaId,
                                                    SharedLogicalObjectLifecycle lifecycle)
        {
            RoomId = roomId;
            RoomEpoch = roomEpoch;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            ObjectRevision = objectRevision;
            AuthorityEpoch = authorityEpoch;
            SchemaId = schemaId;
            Lifecycle = lifecycle;
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public ulong ObjectRevision { get; }
        public ulong AuthorityEpoch { get; }
        public string SchemaId { get; }
        public SharedLogicalObjectLifecycle Lifecycle { get; }

        public bool IsStructurallyValid => ValidId(RoomId) && ValidId(ObjectId) &&
                                           ValidId(SchemaId) && RoomEpoch != 0 &&
                                           ObjectGeneration != 0 && ObjectRevision != 0 &&
                                           AuthorityEpoch != 0;

        internal static bool ValidId(string value) => !string.IsNullOrEmpty(value) &&
                                                      value.Length <= 96 &&
                                                      value.IndexOf('\0') < 0;
    }

    /// <summary>
    /// Explicit receiver-local evidence.  All identifiers are declarations,
    /// not GameObject names, instance IDs, Transform paths, or physical poses.
    /// Thus a chair source semantic can be intentionally mapped to a sofa-seat
    /// counterpart without moving either local object or inferring proximity.
    /// </summary>
    public sealed class LocalBindingEvidence
    {
        public LocalBindingEvidence(string localEndpointId, string localChartId,
                                    string localSupportId, string localCalibrationId,
                                    ulong counterpartMapRevision,
                                    ulong supportChartRevision,
                                    ulong calibrationRevision, bool available)
        {
            LocalEndpointId = localEndpointId;
            LocalChartId = localChartId;
            LocalSupportId = localSupportId;
            LocalCalibrationId = localCalibrationId;
            CounterpartMapRevision = counterpartMapRevision;
            SupportChartRevision = supportChartRevision;
            CalibrationRevision = calibrationRevision;
            Available = available;
        }

        public string LocalEndpointId { get; }
        public string LocalChartId { get; }
        public string LocalSupportId { get; }
        public string LocalCalibrationId { get; }
        public ulong CounterpartMapRevision { get; }
        public ulong SupportChartRevision { get; }
        public ulong CalibrationRevision { get; }
        public bool Available { get; }

        public bool IsStructurallyValid => ValidId(LocalEndpointId) && ValidId(LocalChartId) &&
                                           ValidId(LocalSupportId) && ValidId(LocalCalibrationId) &&
                                           CounterpartMapRevision != 0 && SupportChartRevision != 0 &&
                                           CalibrationRevision != 0;

        internal bool SameAs(LocalBindingEvidence other) => other != null &&
            LocalEndpointId == other.LocalEndpointId && LocalChartId == other.LocalChartId &&
            LocalSupportId == other.LocalSupportId && LocalCalibrationId == other.LocalCalibrationId &&
            CounterpartMapRevision == other.CounterpartMapRevision &&
            SupportChartRevision == other.SupportChartRevision &&
            CalibrationRevision == other.CalibrationRevision && Available == other.Available;

        private static bool ValidId(string value) => AuthenticatedLogicalObjectReference.ValidId(value);
    }

    /// <summary>
    /// Explicitly associates one durable logical-object lifetime and authority
    /// epoch with one local counterpart declaration.  The authority epoch is
    /// intentionally part of the declaration: reconnect cannot reactivate a
    /// mapping merely by yielding a new membership/authority epoch.
    /// </summary>
    public sealed class SharedToLocalBindingDeclaration
    {
        public SharedToLocalBindingDeclaration(string roomId, ulong roomEpoch,
                                               string objectId, ulong objectGeneration,
                                               ulong authorityEpoch, string schemaId,
                                               LocalBindingEvidence evidence)
        {
            RoomId = roomId;
            RoomEpoch = roomEpoch;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            AuthorityEpoch = authorityEpoch;
            SchemaId = schemaId;
            Evidence = evidence;
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public ulong AuthorityEpoch { get; }
        public string SchemaId { get; }
        public LocalBindingEvidence Evidence { get; }

        public bool IsStructurallyValid => AuthenticatedLogicalObjectReference.ValidId(RoomId) &&
            AuthenticatedLogicalObjectReference.ValidId(ObjectId) &&
            AuthenticatedLogicalObjectReference.ValidId(SchemaId) && RoomEpoch != 0 &&
            ObjectGeneration != 0 && AuthorityEpoch != 0 && Evidence != null &&
            Evidence.IsStructurallyValid;

        internal BindingKey Key => new BindingKey(RoomId, RoomEpoch, ObjectId,
                                                   ObjectGeneration, AuthorityEpoch, SchemaId);
        internal bool SameAs(SharedToLocalBindingDeclaration other) => other != null &&
            Key.Equals(other.Key) && Evidence.SameAs(other.Evidence);
    }

    /// <summary>
    /// Detached local capability.  It contains only copied logical and local
    /// declaration values; no Transform, MonoBehaviour, native handle, source
    /// clock, participant, grant, or connection identity can escape through it.
    /// </summary>
    public sealed class SharedToLocalBindingReceipt
    {
        internal SharedToLocalBindingReceipt(AuthenticatedLogicalObjectReference logical,
                                             LocalBindingEvidence evidence, ulong generation)
        {
            RoomId = logical.RoomId;
            RoomEpoch = logical.RoomEpoch;
            ObjectId = logical.ObjectId;
            ObjectGeneration = logical.ObjectGeneration;
            ObjectRevision = logical.ObjectRevision;
            AuthorityEpoch = logical.AuthorityEpoch;
            SchemaId = logical.SchemaId;
            LocalEndpointId = evidence.LocalEndpointId;
            LocalChartId = evidence.LocalChartId;
            LocalSupportId = evidence.LocalSupportId;
            LocalCalibrationId = evidence.LocalCalibrationId;
            LocalBindingGeneration = generation;
            CounterpartMapRevision = evidence.CounterpartMapRevision;
            SupportChartRevision = evidence.SupportChartRevision;
            CalibrationRevision = evidence.CalibrationRevision;
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ObjectId { get; }
        public ulong ObjectGeneration { get; }
        public ulong ObjectRevision { get; }
        public ulong AuthorityEpoch { get; }
        public string SchemaId { get; }
        public string LocalEndpointId { get; }
        public string LocalChartId { get; }
        public string LocalSupportId { get; }
        public string LocalCalibrationId { get; }
        public ulong LocalBindingGeneration { get; }
        public ulong CounterpartMapRevision { get; }
        public ulong SupportChartRevision { get; }
        public ulong CalibrationRevision { get; }
    }

    /// <summary>
    /// Owner-thread local binding registry.  It has no transport, no Unity
    /// scene lookup, no callback dispatch and no authority/reconnect policy.
    /// Every local declaration change replaces its entry before a new local
    /// generation can be issued, making old receipts immediately unusable.
    /// </summary>
    public sealed class SharedToLocalBindingRegistry : IDisposable
    {
        private sealed class Entry
        {
            internal SharedToLocalBindingDeclaration Declaration;
            internal ulong Generation;
            internal ulong LatestObjectRevision;
            internal SharedLogicalObjectLifecycle Lifecycle;
        }

        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<BindingKey, Entry> _entries =
            new Dictionary<BindingKey, Entry>();
        private readonly Dictionary<string, BindingKey> _endpointOwners =
            new Dictionary<string, BindingKey>(StringComparer.Ordinal);
        private ulong _nextGeneration;
        private bool _disposed;

        public bool TryRegister(SharedToLocalBindingDeclaration declaration, out string error)
        {
            if (!OnOwnerThread(out error) || declaration == null || !declaration.IsStructurallyValid)
            {
                if (error == null) error = "A complete explicit local binding declaration is required.";
                return false;
            }

            BindingKey key = declaration.Key;
            if (_endpointOwners.TryGetValue(declaration.Evidence.LocalEndpointId, out BindingKey owner) &&
                !owner.Equals(key))
            {
                error = "One local endpoint cannot implicitly serve two logical objects.";
                return false;
            }
            if (_entries.TryGetValue(key, out Entry current))
            {
                if (current.Declaration.SameAs(declaration))
                {
                    error = null;
                    return true;
                }
                // Invalidate-first: remove the active entry and endpoint claim
                // before publishing its replacement generation.
                _entries.Remove(key);
                _endpointOwners.Remove(current.Declaration.Evidence.LocalEndpointId);
            }
            _entries.Add(key, new Entry
            {
                Declaration = declaration,
                Generation = NextGeneration(),
                LatestObjectRevision = 0,
                Lifecycle = SharedLogicalObjectLifecycle.Active
            });
            _endpointOwners.Add(declaration.Evidence.LocalEndpointId, key);
            error = null;
            return true;
        }

        public bool TryWithdraw(SharedToLocalBindingDeclaration declaration, out string error)
        {
            if (!OnOwnerThread(out error) || declaration == null || !declaration.IsStructurallyValid)
            {
                if (error == null) error = "A complete explicit local binding declaration is required.";
                return false;
            }
            if (!_entries.TryGetValue(declaration.Key, out Entry entry))
            {
                error = "The local binding declaration is not active.";
                return false;
            }
            _entries.Remove(declaration.Key);
            _endpointOwners.Remove(entry.Declaration.Evidence.LocalEndpointId);
            error = null;
            return true;
        }

        public bool TryResolve(AuthenticatedLogicalObjectReference logical,
                               out SharedToLocalBindingReceipt receipt, out string error)
        {
            receipt = null;
            if (!OnOwnerThread(out error) || logical == null || !logical.IsStructurallyValid)
            {
                if (error == null) error = "A complete committed logical-object reference is required.";
                return false;
            }
            BindingKey key = new BindingKey(logical.RoomId, logical.RoomEpoch, logical.ObjectId,
                                            logical.ObjectGeneration, logical.AuthorityEpoch,
                                            logical.SchemaId);
            if (!_entries.TryGetValue(key, out Entry entry))
            {
                error = "No explicit local declaration matches this room/object/authority lifetime.";
                return false;
            }
            if (logical.ObjectRevision < entry.LatestObjectRevision)
            {
                error = "The committed logical-object revision is stale.";
                return false;
            }
            if (entry.Lifecycle == SharedLogicalObjectLifecycle.Tombstoned)
            {
                error = "The logical-object lifetime was tombstoned locally.";
                return false;
            }
            // A tombstone is terminal for this exact durable generation.  It
            // revokes old receipts even where the declared local evidence stays.
            if (logical.Lifecycle == SharedLogicalObjectLifecycle.Tombstoned)
            {
                entry.LatestObjectRevision = logical.ObjectRevision;
                entry.Lifecycle = SharedLogicalObjectLifecycle.Tombstoned;
                error = "The committed logical object is tombstoned.";
                return false;
            }
            if (!entry.Declaration.Evidence.Available)
            {
                error = "The explicitly declared local counterpart is unavailable.";
                return false;
            }
            entry.LatestObjectRevision = logical.ObjectRevision;
            receipt = new SharedToLocalBindingReceipt(logical, entry.Declaration.Evidence,
                                                       entry.Generation);
            error = null;
            return true;
        }

        public bool IsCurrent(SharedToLocalBindingReceipt receipt)
        {
            if (!OnOwnerThread(out _) || receipt == null) return false;
            var key = new BindingKey(receipt.RoomId, receipt.RoomEpoch, receipt.ObjectId,
                                     receipt.ObjectGeneration, receipt.AuthorityEpoch,
                                     receipt.SchemaId);
            return _entries.TryGetValue(key, out Entry entry) && !_disposed &&
                   entry.Lifecycle == SharedLogicalObjectLifecycle.Active &&
                   entry.Declaration.Evidence.Available &&
                   entry.Generation == receipt.LocalBindingGeneration &&
                   entry.LatestObjectRevision == receipt.ObjectRevision &&
                   entry.Declaration.Evidence.CounterpartMapRevision == receipt.CounterpartMapRevision &&
                   entry.Declaration.Evidence.SupportChartRevision == receipt.SupportChartRevision &&
                   entry.Declaration.Evidence.CalibrationRevision == receipt.CalibrationRevision &&
                   entry.Declaration.Evidence.LocalEndpointId == receipt.LocalEndpointId &&
                   entry.Declaration.Evidence.LocalChartId == receipt.LocalChartId &&
                   entry.Declaration.Evidence.LocalSupportId == receipt.LocalSupportId &&
                   entry.Declaration.Evidence.LocalCalibrationId == receipt.LocalCalibrationId;
        }

        public void Dispose()
        {
            if (_disposed || Thread.CurrentThread.ManagedThreadId != _ownerThread) return;
            _entries.Clear();
            _endpointOwners.Clear();
            _disposed = true;
        }

        private bool OnOwnerThread(out string error)
        {
            error = null;
            if (_disposed) { error = "The local binding registry is disposed."; return false; }
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
            {
                error = "Local binding access requires its owner thread.";
                return false;
            }
            return true;
        }

        private ulong NextGeneration()
        {
            checked { ++_nextGeneration; }
            if (_nextGeneration == 0) throw new InvalidOperationException("Local binding generation overflow.");
            return _nextGeneration;
        }
    }

    internal struct BindingKey : IEquatable<BindingKey>
    {
        private readonly string _roomId;
        private readonly ulong _roomEpoch;
        private readonly string _objectId;
        private readonly ulong _objectGeneration;
        private readonly ulong _authorityEpoch;
        private readonly string _schemaId;

        internal BindingKey(string roomId, ulong roomEpoch, string objectId, ulong objectGeneration,
                            ulong authorityEpoch, string schemaId)
        {
            _roomId = roomId; _roomEpoch = roomEpoch; _objectId = objectId;
            _objectGeneration = objectGeneration; _authorityEpoch = authorityEpoch; _schemaId = schemaId;
        }

        public bool Equals(BindingKey other) => _roomId == other._roomId && _roomEpoch == other._roomEpoch &&
            _objectId == other._objectId && _objectGeneration == other._objectGeneration &&
            _authorityEpoch == other._authorityEpoch && _schemaId == other._schemaId;
        public override bool Equals(object obj) => obj is BindingKey && Equals((BindingKey)obj);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _roomId == null ? 0 : _roomId.GetHashCode();
                hash = (hash * 397) ^ _roomEpoch.GetHashCode();
                hash = (hash * 397) ^ (_objectId == null ? 0 : _objectId.GetHashCode());
                hash = (hash * 397) ^ _objectGeneration.GetHashCode();
                hash = (hash * 397) ^ _authorityEpoch.GetHashCode();
                return (hash * 397) ^ (_schemaId == null ? 0 : _schemaId.GetHashCode());
            }
        }
    }
}
