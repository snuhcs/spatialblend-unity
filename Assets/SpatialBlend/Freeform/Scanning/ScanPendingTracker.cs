// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Freeform.Scanning
{
    public enum ScanState
    {
        Pending = 0,
        Succeeded,
        Failed,
        TimedOut,
        Cancelled,
    }

    public enum ScanCompleteOutcome
    {
        /// <summary>The handle was pending and is now terminal.</summary>
        Accepted,
        /// <summary>No request with this handle was ever tracked.</summary>
        Unknown,
        /// <summary>The request already timed out, was cancelled, or completed. The late result is dropped.</summary>
        Late,
    }

    public sealed class ScanPendingEntry
    {
        public long EventHandle;
        public ScanKind Kind;
        public ScanState State;
        public double StartedAt;
        public double Deadline;
        /// <summary>Caller data: the output path, image size, and anchor.</summary>
        public object Context;
    }

    /// <summary>
    /// The pending-state machine. Pending moves to exactly one terminal state, once. It holds no
    /// clock of its own: every call passes the time. Not thread-safe; call it from one thread.
    /// </summary>
    public sealed class ScanPendingTracker
    {
        public const int TerminalMemory = 64;

        private readonly Dictionary<long, ScanPendingEntry> _pending = new Dictionary<long, ScanPendingEntry>();
        private readonly Dictionary<long, ScanState> _terminal = new Dictionary<long, ScanState>();
        private readonly Queue<long> _terminalOrder = new Queue<long>();

        public int PendingCount => _pending.Count;

        public bool IsPending(long eventHandle) => _pending.ContainsKey(eventHandle);

        public bool TryGetPending(long eventHandle, out ScanPendingEntry entry) =>
            _pending.TryGetValue(eventHandle, out entry);

        public bool TryGetTerminalState(long eventHandle, out ScanState state) =>
            _terminal.TryGetValue(eventHandle, out state);

        /// <summary>False when the handle is not positive or is already pending.</summary>
        public bool Begin(long eventHandle, ScanKind kind, double now, double timeoutSeconds, object context,
                          out ScanPendingEntry entry)
        {
            entry = null;
            if (eventHandle <= 0 || timeoutSeconds <= 0.0 || _pending.ContainsKey(eventHandle)) return false;

            entry = new ScanPendingEntry
            {
                EventHandle = eventHandle,
                Kind = kind,
                State = ScanState.Pending,
                StartedAt = now,
                Deadline = now + timeoutSeconds,
                Context = context,
            };
            _pending[eventHandle] = entry;
            _terminal.Remove(eventHandle);
            return true;
        }

        public ScanCompleteOutcome Complete(long eventHandle, bool success, out ScanPendingEntry entry)
        {
            if (_pending.TryGetValue(eventHandle, out entry))
            {
                Finish(entry, success ? ScanState.Succeeded : ScanState.Failed);
                return ScanCompleteOutcome.Accepted;
            }
            return _terminal.ContainsKey(eventHandle) ? ScanCompleteOutcome.Late : ScanCompleteOutcome.Unknown;
        }

        public bool Cancel(long eventHandle, out ScanPendingEntry entry)
        {
            if (!_pending.TryGetValue(eventHandle, out entry)) return false;
            Finish(entry, ScanState.Cancelled);
            return true;
        }

        /// <summary>Moves every pending entry whose deadline has passed to TimedOut and returns them.</summary>
        public List<ScanPendingEntry> Expire(double now)
        {
            var expired = new List<ScanPendingEntry>();
            foreach (ScanPendingEntry entry in _pending.Values)
            {
                if (now >= entry.Deadline) expired.Add(entry);
            }
            expired.Sort((a, b) => a.EventHandle.CompareTo(b.EventHandle));
            foreach (ScanPendingEntry entry in expired) Finish(entry, ScanState.TimedOut);
            return expired;
        }

        public List<ScanPendingEntry> CancelAll()
        {
            var all = new List<ScanPendingEntry>(_pending.Values);
            all.Sort((a, b) => a.EventHandle.CompareTo(b.EventHandle));
            foreach (ScanPendingEntry entry in all) Finish(entry, ScanState.Cancelled);
            return all;
        }

        private void Finish(ScanPendingEntry entry, ScanState state)
        {
            entry.State = state;
            _pending.Remove(entry.EventHandle);
            _terminal[entry.EventHandle] = state;
            _terminalOrder.Enqueue(entry.EventHandle);
            while (_terminalOrder.Count > TerminalMemory)
                _terminal.Remove(_terminalOrder.Dequeue());
        }
    }
}
