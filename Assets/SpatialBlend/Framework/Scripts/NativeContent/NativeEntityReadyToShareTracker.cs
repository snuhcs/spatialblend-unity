// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace SpatialBlend
{
    /// <summary>What the app should do with a native entity at a scan or ready point.</summary>
    public enum NativeShareDecision
    {
        /// <summary>Not shareable now: remote replica, inactive, already attached, or already shared.</summary>
        Skip,
        /// <summary>Content bytes are not ready yet. Wait for ContentReady rather than share a name-only placeholder.</summary>
        Defer,
        /// <summary>Register it now. The tracker has already recorded the share, so it is returned once per key.</summary>
        Share,
    }

    /// <summary>The observable facts about one entity, read by the caller from the scene.</summary>
    public readonly struct NativeEntityShareState
    {
        public NativeEntityShareState(bool isRemoteReplica, bool isActive, bool isAttached, bool hasContent)
        {
            IsRemoteReplica = isRemoteReplica;
            IsActive = isActive;
            IsAttached = isAttached;
            HasContent = hasContent;
        }

        public bool IsRemoteReplica { get; }
        public bool IsActive { get; }
        public bool IsAttached { get; }
        public bool HasContent { get; }
    }

    /// <summary>
    /// Pure share-once logic for entities created before connect (as
    /// in v1). An entity is shared exactly once, only after its
    /// content bytes are ready, and never when it is already registered.
    /// </summary>
    public sealed class NativeEntityReadyToShareTracker<TKey>
    {
        private readonly HashSet<TKey> _pending = new HashSet<TKey>();
        private readonly HashSet<TKey> _shared = new HashSet<TKey>();

        public IEnumerable<TKey> Pending => _pending;
        public int PendingCount => _pending.Count;
        public bool IsPending(TKey key) => _pending.Contains(key);
        public bool WasShared(TKey key) => _shared.Contains(key);

        /// <summary>Evaluate an entity found by a scan of the scene.</summary>
        public NativeShareDecision Evaluate(TKey key, in NativeEntityShareState state)
        {
            if (state.IsRemoteReplica || !state.IsActive || state.IsAttached || _shared.Contains(key))
            {
                _pending.Remove(key);
                return NativeShareDecision.Skip;
            }
            if (!state.HasContent)
            {
                _pending.Add(key);
                return NativeShareDecision.Defer;
            }
            _pending.Remove(key);
            _shared.Add(key);
            return NativeShareDecision.Share;
        }

        /// <summary>
        /// A watched entity raised ContentReady. When the app cannot share yet
        /// (not started, not connected, not host) the entity leaves the pending
        /// set unshared, so a later scan can still pick it up.
        /// </summary>
        public NativeShareDecision OnContentReady(TKey key, in NativeEntityShareState state, bool canShare)
        {
            _pending.Remove(key);
            return canShare ? Evaluate(key, state) : NativeShareDecision.Skip;
        }

        /// <summary>Registration failed. Allow the entity to be evaluated again.</summary>
        public void Forget(TKey key)
        {
            _pending.Remove(key);
            _shared.Remove(key);
        }

        /// <summary>Drop the pending set only, for example when the app is disabled.</summary>
        public void ClearPending() => _pending.Clear();

        /// <summary>A new session starts: nothing is shared or pending.</summary>
        public void Reset()
        {
            _pending.Clear();
            _shared.Clear();
        }
    }
}
