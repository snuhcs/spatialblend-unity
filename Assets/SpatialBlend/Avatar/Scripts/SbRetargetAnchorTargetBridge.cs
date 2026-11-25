// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Opt-in adapter from the C anchor-target boundary to the original Meta-avatar
    /// retarget input consumer. It maps only explicitly named tracked head and hand
    /// channels; it does not calibrate a rig, and pelvis/sitting targets remain the
    /// responsibility of a real articulated-rig integration.
    /// </summary>
    public sealed class SbRetargetAnchorTargetBridge : MonoBehaviour
    {
        public const string HeadAnchor = "head";
        public const string LeftHandAnchor = "hand_l";
        public const string RightHandAnchor = "hand_r";

        private readonly Dictionary<string, AnchorTargetController.Target> _active =
            new Dictionary<string, AnchorTargetController.Target>(StringComparer.Ordinal);
        private AnchorTargetController _controller;
        private AnchorTargetController _subscribedController;
        private SbRetargetTransform _retarget;
        private SbRetargetTransform _boundRetarget;
        private Action<string, Pose, bool, double> _appliedHandler;
        private Action<string> _releasedHandler;
        private long _subscriptionGeneration;
        private object _checkedOwner;

        // Only the caller owning this same entity's legacy controller may request
        // this handoff. Never detach a cross-object/foreign Foundation binding.
        internal bool TrySuspendForCheckedRealization(object owner, AnchorTargetController controller,
                                                       SbRetargetTransform retarget)
        {
            if (owner == null || _checkedOwner != null || controller == null || retarget == null ||
                !ReferenceEquals(controller.gameObject, gameObject) || !retarget.CanSuspendLegacyBridge ||
                (_controller != null && !ReferenceEquals(_controller, controller)) ||
                (_subscribedController != null && !ReferenceEquals(_subscribedController, controller)) ||
                (_boundRetarget != null && !ReferenceEquals(_boundRetarget, retarget)) ||
                !ReferenceEquals(_retarget != null ? _retarget : FindRetarget(), retarget)) return false;
            _checkedOwner = owner;
            UnsubscribeAndClear();
            _active.Clear();
            return true;
        }

        internal void EndCheckedSuspension(object owner, bool resumeLegacy)
        {
            if (!ReferenceEquals(_checkedOwner, owner)) return;
            _active.Clear();
            if (!resumeLegacy) enabled = false;
            _checkedOwner = null;
            if (resumeLegacy && isActiveAndEnabled) RefreshBinding();
        }

        /// <summary>Injectable configuration point for late avatar construction.</summary>
        public void Configure(AnchorTargetController controller, SbRetargetTransform retarget = null)
        {
            if (!ReferenceEquals(_controller, controller))
            {
                UnsubscribeAndClear();
                _controller = controller;
                _active.Clear();
            }
            _retarget = retarget;
            if (isActiveAndEnabled)
            {
                RefreshBinding();
            }
        }

        // The Foundation consumer calls this before disabling/destroying its owned
        // bridge.  It releases subscriptions even when its retarget lease has
        // already been replaced, while the retarget-side identity gate prevents a
        // retired bridge from clearing a replacement owner's targets.
        internal void DetachFoundationBinding(SbRetargetTransform expectedRetarget)
        {
            if (expectedRetarget != null && !ReferenceEquals(_retarget, expectedRetarget) &&
                !ReferenceEquals(_boundRetarget, expectedRetarget))
            {
                return;
            }
            UnsubscribeAndClear();
            _active.Clear();
            _controller = null;
            _retarget = null;
        }

        private void OnEnable()
        {
            RefreshBinding();
        }

        private void Update()
        {
            // Meta creates the retarget object after a remote avatar arrives.  Poll
            // only for this local readiness edge; commands remain owned by the
            // controller and are replayed from its current snapshot once available.
            RefreshBinding();
        }

        private void OnDisable()
        {
            UnsubscribeAndClear();
        }

        private void OnDestroy()
        {
            UnsubscribeAndClear();
        }

        private void RefreshBinding()
        {
            if (!isActiveAndEnabled || _checkedOwner != null)
            {
                return;
            }
            AnchorTargetController desiredController = _controller != null
                ? _controller : GetComponent<AnchorTargetController>();
            if (!ReferenceEquals(desiredController, _subscribedController))
            {
                UnsubscribeAndClear();
                // A destroyed/replaced controller is a new source incarnation.
                // Never replay its cached commands if retarget readiness changes in
                // the same frame; only PrimeCurrentTargets may populate this again.
                _active.Clear();
                _subscribedController = desiredController;
                if (_subscribedController != null)
                {
                    long generation = ++_subscriptionGeneration;
                    AnchorTargetController source = _subscribedController;
                    _appliedHandler = (anchor, pose, hasHeight, weight) =>
                        OnTargetApplied(source, generation, anchor, pose, hasHeight, weight);
                    _releasedHandler = anchor => OnTargetReleased(source, generation, anchor);
                    _subscribedController.TargetApplied += _appliedHandler;
                    _subscribedController.TargetReleased += _releasedHandler;
                    PrimeCurrentTargets(_subscribedController);
                }
            }

            SbRetargetTransform desiredRetarget = _retarget != null
                ? _retarget : FindRetarget();
            if (!ReferenceEquals(desiredRetarget, _boundRetarget))
            {
                if (_boundRetarget != null)
                {
                    ClearBoundRetarget(_boundRetarget);
                    _boundRetarget.SetAnchorTargetOverridesEnabled(this, false);
                }
                _boundRetarget = desiredRetarget;
                if (_boundRetarget != null)
                {
                    _boundRetarget.SetAnchorTargetOverridesEnabled(this, true);
                    ApplyCurrentTargets();
                }
            }
            else if (_boundRetarget != null)
            {
                // AddComponent may have invoked OnEnable before a Foundation owner
                // authorizes this bridge.  Retry the identity-gated opt-in after
                // Configure instead of leaving a same-instance bridge half-bound.
                _boundRetarget.SetAnchorTargetOverridesEnabled(this, true);
                ApplyCurrentTargets();
            }
        }

        private SbRetargetTransform FindRetarget()
        {
            var direct = GetComponent<SbRetargetTransform>();
            if (direct != null)
            {
                return direct;
            }

            var meta = GetComponent<MetaAvatarEntity>();
            return meta != null && meta._sbAvatar != null
                ? meta._sbAvatar._retargetTransform
                : null;
        }

        private void PrimeCurrentTargets(AnchorTargetController controller)
        {
            _active.Clear();
            Prime(controller, HeadAnchor);
            Prime(controller, LeftHandAnchor);
            Prime(controller, RightHandAnchor);
            ApplyCurrentTargets();
        }

        private void Prime(AnchorTargetController controller, string anchor)
        {
            if (controller.TryGetTarget(anchor, out AnchorTargetController.Target target) &&
                target.HasHeight)
            {
                _active[anchor] = target;
            }
        }

        private void OnTargetApplied(AnchorTargetController source, long generation,
                                     string anchor, Pose pose, bool hasHeight, double weight)
        {
            if (!isActiveAndEnabled || !ReferenceEquals(source, _subscribedController) ||
                generation != _subscriptionGeneration || !IsSupported(anchor))
            {
                return;
            }
            if (!hasHeight || !IsFinite(pose.position) || !IsFinite(pose.rotation) ||
                double.IsNaN(weight) || double.IsInfinity(weight))
            {
                // A newer malformed/no-height delivery supersedes the prior command
                // for this anchor; retaining that old target would make stale C
                // ownership look valid.
                _active.Remove(anchor);
                if (_boundRetarget != null)
                {
                    _boundRetarget.ClearAnchorTarget(this, anchor);
                }
                return;
            }
            _active[anchor] = new AnchorTargetController.Target(pose.position, pose.rotation,
                hasHeight, weight, Time.unscaledTimeAsDouble);
            Apply(anchor);
        }

        private void OnTargetReleased(AnchorTargetController source, long generation,
                                      string anchor)
        {
            if (!isActiveAndEnabled || !ReferenceEquals(source, _subscribedController) ||
                generation != _subscriptionGeneration || !IsSupported(anchor))
            {
                return;
            }
            _active.Remove(anchor);
            if (_boundRetarget != null)
            {
                _boundRetarget.ClearAnchorTarget(this, anchor);
            }
        }

        private void ApplyCurrentTargets()
        {
            Apply(HeadAnchor);
            Apply(LeftHandAnchor);
            Apply(RightHandAnchor);
        }

        private void Apply(string anchor)
        {
            if (_boundRetarget == null || !_active.TryGetValue(anchor,
                out AnchorTargetController.Target target))
            {
                return;
            }
            _boundRetarget.SetAnchorTarget(this, anchor,
                new Pose(target.Position, target.Rotation), (float)target.Weight);
        }

        private void UnsubscribeAndClear()
        {
            if (_subscribedController != null)
            {
                if (_appliedHandler != null)
                {
                    _subscribedController.TargetApplied -= _appliedHandler;
                }
                if (_releasedHandler != null)
                {
                    _subscribedController.TargetReleased -= _releasedHandler;
                }
            }
            _subscribedController = null;
            _appliedHandler = null;
            _releasedHandler = null;
            ++_subscriptionGeneration;
            if (_boundRetarget != null)
            {
                ClearBoundRetarget(_boundRetarget);
                _boundRetarget.SetAnchorTargetOverridesEnabled(this, false);
            }
            _boundRetarget = null;
        }

        private void ClearBoundRetarget(SbRetargetTransform retarget)
        {
            retarget.ClearAnchorTarget(this, HeadAnchor);
            retarget.ClearAnchorTarget(this, LeftHandAnchor);
            retarget.ClearAnchorTarget(this, RightHandAnchor);
        }

        private static bool IsSupported(string anchor)
        {
            return string.Equals(anchor, HeadAnchor, StringComparison.Ordinal) ||
                   string.Equals(anchor, LeftHandAnchor, StringComparison.Ordinal) ||
                   string.Equals(anchor, RightHandAnchor, StringComparison.Ordinal);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
                   !float.IsNaN(value.w) && !float.IsInfinity(value.w);
        }
    }
}
