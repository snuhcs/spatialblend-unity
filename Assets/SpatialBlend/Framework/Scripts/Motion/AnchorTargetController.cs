// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Motion
{
    /// <summary>
    /// A small, non-generative C boundary for realization targets that address a
    /// body anchor. It owns goal transforms and acquire/release notifications, but
    /// deliberately does not write an Animator, IK constraint, or tracked-avatar
    /// input. Applications can attach their existing rig to a goal or react to its
    /// events without giving this path ownership of tracked body parts.
    /// </summary>
    public sealed class AnchorTargetController : MonoBehaviour
    {
        public readonly struct Target
        {
            public Target(Vector3 position, Quaternion rotation, bool hasHeight,
                          double weight, double updatedAt)
            {
                Position = position;
                Rotation = rotation;
                HasHeight = hasHeight;
                Weight = weight;
                UpdatedAt = updatedAt;
            }

            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public bool HasHeight { get; }
            public double Weight { get; }
            public double UpdatedAt { get; }
        }

        private sealed class ActiveTarget
        {
            public Transform Goal;
            public Target Value;
            public long Version;
        }

        [SerializeField]
        [Tooltip("Optional parent for generated body-anchor goals. Defaults to this object.")]
        private Transform _goalRoot;

        private readonly Dictionary<string, ActiveTarget> _active =
            new Dictionary<string, ActiveTarget>(StringComparer.Ordinal);
        private readonly Dictionary<string, Transform> _goals =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        // Controller-local receipt watermark. Production calls use the main-thread
        // receipt clock because the current C boundary has no source sequence number;
        // this prevents duplicate/queued local deliveries, not a claim of network
        // source-ordering correctness.
        private readonly Dictionary<string, double> _lastAcceptedAt =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, TargetLease> _leases =
            new Dictionary<string, TargetLease>(StringComparer.Ordinal);
        private bool _tearingDown;
        private long _nextLeaseEpoch;
        private long _nextCommandVersion;

        public sealed class TargetLease : IDisposable
        {
            private AnchorTargetController _controller;
            private readonly string _anchor;
            private readonly long _epoch;
            private double _lastAcceptedAt = double.NegativeInfinity;

            internal TargetLease(AnchorTargetController controller, string anchor, long epoch)
            { _controller = controller; _anchor = anchor; _epoch = epoch; }
            internal string AnchorName => _anchor;
            internal long Epoch => _epoch;

            public bool TryApply(Vector3 position, float yaw, bool hasHeight, double weight, double now)
            {
                if (_controller == null || !_controller.IsCurrentLease(_anchor, _epoch, this) ||
                    !IsFinite(position) || !IsFinite(yaw) || !IsFinite(weight) || !IsFinite(now) ||
                    weight < 0d || weight > 1d || now <= _lastAcceptedAt) return false;
                _lastAcceptedAt = now;
                if (!_controller.ApplyLeased(this, position, yaw, hasHeight, weight, now)) return false;
                return true;
            }

            public void Cancel()
            {
                if (_controller != null) _controller.CancelLease(_anchor, _epoch, this, false);
            }

            public void Dispose()
            {
                if (_controller != null) _controller.CancelLease(_anchor, _epoch, this, true);
                _controller = null;
            }
        }

        /// <summary>Raised when a non-root target becomes active or changes.</summary>
        public event Action<string, Pose, bool, double> TargetApplied;

        /// <summary>Raised exactly once when a target is explicitly cancelled or expires.</summary>
        public event Action<string> TargetReleased;

        private Transform GoalRoot => _goalRoot != null ? _goalRoot : transform;

        public bool TryAcquireLease(string anchorName, out TargetLease lease)
        {
            lease = null;
            if (_tearingDown || string.IsNullOrEmpty(anchorName) || _leases.ContainsKey(anchorName) ||
                _active.ContainsKey(anchorName)) return false;
            if (!isActiveAndEnabled) return false;
            long epoch = ++_nextLeaseEpoch;
            lease = new TargetLease(this, anchorName, epoch);
            _leases.Add(anchorName, lease);
            return true;
        }

        /// <summary>
        /// Sets the parent used for generated goals. Existing goals keep their world
        /// poses when moved. This is useful when an avatar's rig exposes a dedicated
        /// goal container; the controller still owns and cleans up only its own goals.
        /// </summary>
        public void SetGoalRoot(Transform goalRoot)
        {
            _goalRoot = goalRoot;
            foreach (Transform goal in _goals.Values)
            {
                if (goal != null)
                {
                    goal.SetParent(GoalRoot, true);
                }
            }
        }

        /// <summary>
        /// Accepts one command from an already-selected authoritative snapshot. This
        /// method never infers absence from another anchor's update: the manager owns
        /// snapshot cancellation and calls <see cref="CancelTarget"/> explicitly.
        /// </summary>
        public void ApplyTarget(string anchorName, Vector3 worldPosition, float yaw,
                                bool hasHeight, double weight)
        {
            ApplyTarget(anchorName, worldPosition, yaw, hasHeight, weight,
                        Time.unscaledTimeAsDouble);
        }

        /// <summary>Clock-injected form for deterministic offline tests.</summary>
        public void ApplyTarget(string anchorName, Vector3 worldPosition, float yaw,
                                bool hasHeight, double weight, double now)
        {
            if (_tearingDown || !isActiveAndEnabled || string.IsNullOrEmpty(anchorName) ||
                string.Equals(anchorName, "root", StringComparison.Ordinal) ||
                _leases.ContainsKey(anchorName) ||
                !IsFinite(worldPosition) || !IsFinite(yaw) || !IsFinite(weight) ||
                !IsFinite(now) || weight < 0d || weight > 1d)
            {
                // Root locomotion remains SbEntity.ApplyRealizationTarget's path.
                // Invalid commands are rejected without changing a live target.
                return;
            }

            // Do not let a duplicate or older local delivery overwrite a newer target
            // or resurrect one that was explicitly released. Equal receipt times are
            // intentionally ignored rather than treated as an observable refresh.
            if (_lastAcceptedAt.TryGetValue(anchorName, out double lastAcceptedAt) &&
                now <= lastAcceptedAt)
            {
                return;
            }

            if (!_active.TryGetValue(anchorName, out ActiveTarget active))
            {
                if (!_goals.TryGetValue(anchorName, out Transform goal))
                {
                    var goalObject = new GameObject("SbAnchorGoal_" + anchorName);
                    goal = goalObject.transform;
                    goal.SetParent(GoalRoot, false);
                    _goals.Add(anchorName, goal);
                }
                goal.gameObject.SetActive(true);
                active = new ActiveTarget { Goal = goal };
                _active.Add(anchorName, active);
            }

            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            active.Goal.SetPositionAndRotation(worldPosition, rotation);
            active.Value = new Target(worldPosition, rotation, hasHeight, weight, now);
            active.Version = ++_nextCommandVersion;
            _lastAcceptedAt[anchorName] = now;
            RaiseApplied(anchorName, new Pose(worldPosition, rotation), hasHeight, weight,
                         null, active, active.Version);
        }

        /// <summary>Explicit authoritative absence release from SpatialBlendManager.</summary>
        public void CancelTarget(string anchorName)
        {
            if (string.IsNullOrEmpty(anchorName) ||
                _leases.ContainsKey(anchorName) ||
                !_active.TryGetValue(anchorName, out ActiveTarget active))
            {
                return;
            }
            _active.Remove(anchorName);

            if (active.Goal != null)
            {
                active.Goal.gameObject.SetActive(false);
            }
            RaiseReleased(anchorName, active);
        }

        /// <summary>
        /// Reapplies every stored world-space target, so movement of an avatar or an external goal parent cannot drag a
        /// goal. A target never expires by time: it is released only by an explicit cancel (the core's authoritative
        /// absence, <see cref="CancelTarget"/>) or by teardown. A time-based expiry has no v1 or paper basis.
        /// </summary>
        public void Tick(double now)
        {
            if (!IsFinite(now))
            {
                return;
            }

            foreach (KeyValuePair<string, ActiveTarget> pair in _active)
            {
                ActiveTarget active = pair.Value;
                if (active.Goal != null)
                {
                    active.Goal.SetPositionAndRotation(active.Value.Position,
                                                       active.Value.Rotation);
                }
            }
        }

        public bool TryGetTarget(string anchorName, out Target target)
        {
            if (_active.TryGetValue(anchorName, out ActiveTarget active))
            {
                target = active.Value;
                return true;
            }
            target = default;
            return false;
        }

        public Transform GetGoal(string anchorName)
        {
            return _goals.TryGetValue(anchorName, out Transform goal)
                ? goal
                : null;
        }

        private void Update()
        {
            Tick(Time.unscaledTimeAsDouble);
        }

        private void OnEnable()
        {
            _tearingDown = false;
        }

        private void OnDisable()
        {
            _tearingDown = true;
            RevokeLeases();
            ReleaseAllTargets();
        }

        private void OnDestroy()
        {
            _tearingDown = true;
            RevokeLeases();
            ReleaseAllTargets();
            foreach (Transform goal in _goals.Values)
            {
                if (goal == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(goal.gameObject);
                }
                else
                {
                    DestroyImmediate(goal.gameObject);
                }
            }
            _goals.Clear();
        }

        /// <summary>
        /// Releases the targets active when this method begins. It is safe to call
        /// repeatedly and does not claim ownership of targets applied reentrantly by
        /// a release observer; those are newer commands and remain active.
        /// </summary>
        public void ReleaseAllTargets()
        {
            if (_active.Count == 0)
            {
                return;
            }

            // Retire the entire entry snapshot before calling user code. A callback
            // may refresh another entry's key; a key-only cancellation loop would
            // then incorrectly cancel that newer command (or disable its reused goal).
            var targets = new List<KeyValuePair<string, ActiveTarget>>(_active);
            foreach (var pair in targets)
                if (!_leases.ContainsKey(pair.Key)) _active.Remove(pair.Key);
            foreach (var pair in targets)
            {
                if (!_leases.ContainsKey(pair.Key) && pair.Value.Goal != null)
                {
                    pair.Value.Goal.gameObject.SetActive(false);
                }
            }
            foreach (var pair in targets)
            {
                if (!_leases.ContainsKey(pair.Key)) RaiseReleased(pair.Key, pair.Value);
            }
        }

        private bool IsCurrentLease(string anchor, long epoch, TargetLease lease) =>
            !_tearingDown && isActiveAndEnabled && _leases.TryGetValue(anchor, out TargetLease current) &&
            ReferenceEquals(current, lease) && epoch == lease.Epoch;
        private bool ApplyLeased(TargetLease lease, Vector3 position, float yaw, bool hasHeight, double weight, double now)
        {
            if (!isActiveAndEnabled || !_leases.TryGetValue(lease.AnchorName, out TargetLease current) || !ReferenceEquals(current, lease)) return false;
            ApplyTargetCore(lease.AnchorName, position, yaw, hasHeight, weight, now);
            return true;
        }
        private void CancelLease(string anchor, long epoch, TargetLease lease, bool dispose)
        {
            if (!_leases.TryGetValue(anchor, out TargetLease current) || !ReferenceEquals(current, lease) || epoch != lease.Epoch) return;
            if (dispose) _leases.Remove(anchor);
            if (_active.TryGetValue(anchor, out ActiveTarget active))
            {
                _active.Remove(anchor);
                if (active.Goal != null) active.Goal.gameObject.SetActive(false);
                RaiseReleased(anchor, active);
            }
        }
        private void RevokeLeases()
        {
            foreach (TargetLease lease in new List<TargetLease>(_leases.Values)) lease.Dispose();
            _leases.Clear();
        }
        private void ApplyTargetCore(string anchorName, Vector3 worldPosition, float yaw, bool hasHeight, double weight, double now)
        {
            if (!_active.TryGetValue(anchorName, out ActiveTarget active))
            {
                if (!_goals.TryGetValue(anchorName, out Transform goal))
                { var goalObject = new GameObject("SbAnchorGoal_" + anchorName); goal = goalObject.transform; goal.SetParent(GoalRoot, false); _goals.Add(anchorName, goal); }
                goal.gameObject.SetActive(true); active = new ActiveTarget { Goal = goal }; _active.Add(anchorName, active);
            }
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            active.Goal.SetPositionAndRotation(worldPosition, rotation);
            active.Value = new Target(worldPosition, rotation, hasHeight, weight, now);
            active.Version = ++_nextCommandVersion;
            RaiseApplied(anchorName, new Pose(worldPosition, rotation), hasHeight, weight,
                         _leases.TryGetValue(anchorName, out TargetLease lease) ? lease : null,
                         active, active.Version);
        }
        private void RaiseApplied(string name, Pose pose, bool height, double weight, TargetLease lease,
                                  ActiveTarget expected, long expectedVersion)
        {
            foreach (Delegate item in TargetApplied?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<string, Pose, bool, double>)item)(name, pose, height, weight); } catch { }
                if (!_active.TryGetValue(name, out ActiveTarget current) ||
                    !ReferenceEquals(current, expected) || current.Version != expectedVersion ||
                    (lease != null && !IsCurrentLease(name, lease.Epoch, lease))) return;
            }
        }
        private void RaiseReleased(string name, ActiveTarget retired)
        {
            foreach (Delegate item in TargetReleased?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<string>)item)(name); } catch { }
                if (_active.TryGetValue(name, out ActiveTarget replacement) &&
                    !ReferenceEquals(replacement, retired)) return;
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
