// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;
using Meta.XR.BuildingBlocks;

namespace SpatialBlend
{
    public partial class SbRetargetTransform : MonoBehaviour
    {
        // Input: Retargeted transform from the SpatialBlend system
        // Output: Apply the transform to the RemoteAvatar
        private Vector3 prevLeftHandPos;
        private Vector3 prevRightHandPos;
        private Quaternion prevLeftHandRot;
        private Quaternion prevRightHandRot;

        // These are the most recently received tracked inputs, before any optional
        // C-anchor realization is layered over them. Root realization and tracked
        // observations are separate inputs. The legacy Fusion stream encodes hands
        // relative to the actual headset, while the explicit world-observation APIs
        // retain their root-relative basis.
        private Pose _rootPlacement;
        private Vector3 _headLocalPosition;
        private Quaternion _headLocalRotation;
        private Vector3 _leftHandLocalPosition;
        private Vector3 _rightHandLocalPosition;
        private Quaternion _leftHandLocalRotation;
        private Quaternion _rightHandLocalRotation;
        private bool _hasRootPlacement;
        private bool _hasHeadBaseline;
        private bool _hasLeftHandBaseline;
        private bool _hasRightHandBaseline;
        private bool _leftHandBaselineUsesHeadTransform;
        private bool _rightHandBaselineUsesHeadTransform;

        private bool _anchorTargetOverridesEnabled;
        private bool _headTargetActive;
        private bool _leftHandTargetActive;
        private bool _rightHandTargetActive;
        private Pose _headTarget;
        private Pose _leftHandTarget;
        private Pose _rightHandTarget;
        private float _headTargetWeight;
        private float _leftHandTargetWeight;
        private float _rightHandTargetWeight;
        // A Foundation local consumer is exclusive because its bridge clears
        // target overrides on teardown.  Keeping the lease on the retarget rather
        // than on a scene object makes same-name replacement and deferred Destroy
        // harmless without changing legacy/Fusion callers.
        private FoundationAvatarRetargetEntity _foundationRetargetOwner;
        private SbRetargetAnchorTargetBridge _foundationAuthorizedBridge;
        // This is intentionally sticky.  Once Foundation has supplied a real
        // observation consumer, removing its target bridge must not make the
        // SDK mistake the retained avatar transforms for fresh observations.
        // Untouched/Fusion retargets retain the original direct-transform rule.
        private bool _foundationObservationPresenceMode;

        private SbAvatarBehaviourFusion _avatarBehaviour;

        [SerializeField]
        public Transform headTransform;

        [SerializeField]
        public Transform leftHandTransform;

        [SerializeField]
        public Transform rightHandTransform;

        public virtual void Start()
        {
            prevLeftHandPos = Vector3.zero;
            prevRightHandPos = Vector3.zero;
            prevLeftHandRot = Quaternion.identity;
            prevRightHandRot = Quaternion.identity;
        }

        /// <summary>
        /// An active target bridge, and a retarget that has previously hosted a
        /// Foundation consumer, report tracking only after a real baseline was
        /// seen. A target is a realization command, not a fabricated observation.
        /// Untouched direct-transform users retain the original transform-presence
        /// behavior; Foundation keeps explicit presence after its bridge releases.
        /// </summary>
        public bool HasHeadObservation => (_foundationObservationPresenceMode || _anchorTargetOverridesEnabled)
            ? _hasHeadBaseline : headTransform != null;
        public bool HasLeftHandObservation => (_foundationObservationPresenceMode || _anchorTargetOverridesEnabled)
            ? _hasLeftHandBaseline : leftHandTransform != null;
        public bool HasRightHandObservation => (_foundationObservationPresenceMode || _anchorTargetOverridesEnabled)
            ? _hasRightHandBaseline : rightHandTransform != null;

        internal bool CanAcceptFoundationRetargetOwner(FoundationAvatarRetargetEntity owner)
        {
            return _realizationLease == null && owner != null && (_foundationRetargetOwner == null
                ? !_anchorTargetOverridesEnabled
                : ReferenceEquals(_foundationRetargetOwner, owner));
        }

        internal bool TryAcquireFoundationRetargetOwner(FoundationAvatarRetargetEntity owner)
        {
            if (!CanAcceptFoundationRetargetOwner(owner)) return false;
            _foundationRetargetOwner = owner;
            return true;
        }

        internal bool IsFoundationRetargetOwner(FoundationAvatarRetargetEntity owner) =>
            owner != null && ReferenceEquals(_foundationRetargetOwner, owner);

        internal bool TryAuthorizeFoundationBridge(FoundationAvatarRetargetEntity owner,
                                                   SbRetargetAnchorTargetBridge bridge)
        {
            if (!IsFoundationRetargetOwner(owner) || bridge == null ||
                (_foundationAuthorizedBridge != null &&
                 !ReferenceEquals(_foundationAuthorizedBridge, bridge)))
            {
                return false;
            }
            _foundationAuthorizedBridge = bridge;
            _foundationObservationPresenceMode = true;
            return true;
        }

        internal void ReleaseFoundationBridge(FoundationAvatarRetargetEntity owner,
                                              SbRetargetAnchorTargetBridge bridge)
        {
            if (IsFoundationRetargetOwner(owner) &&
                ReferenceEquals(_foundationAuthorizedBridge, bridge))
            {
                _foundationAuthorizedBridge = null;
            }
        }

        // A bridge may live on another GameObject.  The retarget, rather than a
        // component search, is therefore the sole authority for writes while a
        // Foundation owner holds its lease.
        internal bool CanBridgeWrite(SbRetargetAnchorTargetBridge bridge)
        {
            return _realizationLease == null && bridge != null && (_foundationRetargetOwner == null ||
                ReferenceEquals(_foundationAuthorizedBridge, bridge));
        }

        internal void ReleaseFoundationRetargetOwner(FoundationAvatarRetargetEntity owner)
        {
            if (IsFoundationRetargetOwner(owner))
            {
                _foundationAuthorizedBridge = null;
                _foundationRetargetOwner = null;
            }
        }

        private void ConvertToAbsolute(ref Vector3 leftHandPose, ref Vector3 rightHandPose,
                                       Quaternion headRotation)
        {
            Vector3 headForward = headRotation * Vector3.forward;
            Vector3 headRight = headRotation * Vector3.right;
            Vector3 headUp = headRotation * Vector3.up;

            leftHandPose =
                leftHandPose.x * headRight +
                leftHandPose.y * headUp +
                leftHandPose.z * headForward;

            rightHandPose =
                rightHandPose.x * headRight +
                rightHandPose.y * headUp +
                rightHandPose.z * headForward;
        }
        
        public void SetHeadTransform(Vector3 position, Quaternion rotation)
        {
            if (RejectUnleasedHeadMutation()) return;
            SetHeadTransformCore(position, rotation);
        }

        private void SetHeadTransformCore(Vector3 position, Quaternion rotation)
        {
            if (headTransform == null || !IsFinite(position) || !IsUsableRotation(rotation))
            {
                ClearHeadObservation();
                return;
            }

            rotation.Normalize();
            _rootPlacement = new Pose(position, rotation);
            _hasRootPlacement = true;
            _headLocalPosition = Vector3.zero;
            _headLocalRotation = Quaternion.identity;
            _hasHeadBaseline = true;
            // Preserve the original public setter contract when the opt-in bridge
            // is absent: it must write its supplied head pose even if a caller has
            // directly changed the Transform since the prior sample.
            if (!_anchorTargetOverridesEnabled)
            {
                headTransform.SetPositionAndRotation(position, rotation);
            }
            ApplyAllBaselines();
        }

        /// <summary>
        /// Records an independently observed headset pose in world coordinates.
        /// Unlike <see cref="SetHeadTransform"/>, this never changes the root
        /// realization chart.  Foundation receivers use this path because an
        /// observed head may be offset from a floor/root placement target.
        /// </summary>
        public bool SetHeadObservation(Vector3 position, Quaternion rotation)
        {
            if (RejectUnleasedHeadMutation()) return false;
            if (headTransform == null || !IsFinite(position) || !IsUsableRotation(rotation))
            {
                ClearHeadObservation();
                return false;
            }
            SetHeadBaseline(new Pose(position, Normalized(rotation)));
            ApplyAllBaselines();
            return true;
        }

        /// <summary>Records one independent left-controller world observation.</summary>
        public bool SetLeftHandObservation(Vector3 position, Quaternion rotation)
        {
            if (!CanWriteUnrelatedHands()) return false;
            if (leftHandTransform == null || !IsFinite(position) || !IsUsableRotation(rotation))
            {
                ClearLeftHandObservation();
                return false;
            }
            SetLeftHandBaseline(new Pose(position, Normalized(rotation)));
            ApplyLeftHand();
            return true;
        }

        /// <summary>Records one independent right-controller world observation.</summary>
        public bool SetRightHandObservation(Vector3 position, Quaternion rotation)
        {
            if (!CanWriteUnrelatedHands()) return false;
            if (rightHandTransform == null || !IsFinite(position) || !IsUsableRotation(rotation))
            {
                ClearRightHandObservation();
                return false;
            }
            SetRightHandBaseline(new Pose(position, Normalized(rotation)));
            ApplyRightHand();
            return true;
        }

        /// <summary>
        /// Commits a complete Foundation observation snapshot as one operation.
        /// All source poses and destination transforms are admitted before any
        /// baseline is changed, so a destroyed hand cannot leave a new head behind.
        /// </summary>
        internal bool TryApplyFoundationObservations(bool hasHead, Pose head,
                                                     bool hasLeft, Pose left,
                                                     bool hasRight, Pose right)
        {
            if (RejectUnleasedHeadMutation()) return false;
            if (headTransform == null || leftHandTransform == null || rightHandTransform == null ||
                (hasHead && !IsValidPose(head)) || (hasLeft && !IsValidPose(left)) ||
                (hasRight && !IsValidPose(right)))
            {
                return false;
            }

            if (hasHead) SetHeadBaseline(new Pose(head.position, Normalized(head.rotation)));
            else _hasHeadBaseline = false;
            if (hasLeft) SetLeftHandBaseline(new Pose(left.position, Normalized(left.rotation)));
            else _hasLeftHandBaseline = false;
            if (hasRight) SetRightHandBaseline(new Pose(right.position, Normalized(right.rotation)));
            else _hasRightHandBaseline = false;
            ApplyAllBaselines();
            return true;
        }

        internal bool TryClearFoundationObservations()
        {
            return TryApplyFoundationObservations(false, default, false, default, false, default);
        }

        /// <summary>
        /// Applies the legacy avatar-entity/root realization path without claiming
        /// that a composed visible head is a newly observed tracked headset pose.
        /// The root and tracked-input channels have historically shared this
        /// Transform; while an explicit head target is active the latter wins rather
        /// than allowing root writes to feed an override back into its baseline.
        /// </summary>
        public void ApplyRootRealizationTransform(Vector3 position, Quaternion rotation)
        {
            if (RejectUnleasedHeadMutation()) return;
            ApplyRootRealizationTransformCore(position, rotation);
        }

        private void ApplyRootRealizationTransformCore(Vector3 position, Quaternion rotation)
        {
            if (headTransform == null || !IsFinite(position) || !IsUsableRotation(rotation))
            {
                return;
            }
            rotation.Normalize();
            _rootPlacement = new Pose(position, rotation);
            _hasRootPlacement = true;
            // A root-only realization has no headset observation, but it is still
            // an actual visible placement.  In legacy mode write it directly before
            // composing dependent hand transforms.
            if (!_anchorTargetOverridesEnabled)
            {
                headTransform.SetPositionAndRotation(position, rotation);
            }
            // Compose through every channel: root movement cannot disappear merely
            // because head/one-hand target realization is presently active.
            ApplyAllBaselines();
        }

        /// <summary>
        /// The BODY placement the driver last wrote: where the root stands and which way it HEADS (the locomotion direction, or the pose an
        /// engaged chart gives it). The visible head transform is the avatar's one headset input; while a head target (the Facing yaw) is
        /// active it carries the UPPER body's yaw instead, so reading the root's heading from the head would report the upper body's.
        /// False until the driver has written a root or placed the head.
        /// </summary>
        public bool TryGetBodyPlacement(out Quaternion bodyRotation)
        {
            bodyRotation = _rootPlacement.rotation;
            return _hasRootPlacement;
        }

        public void SetHandTransform(Vector3 leftHandPose, Vector3 rightHandPose, Quaternion leftHandRot, Quaternion rightHandRot, float lerpTime)
        {
            if (!CanWriteUnrelatedHands()) return;
            if (leftHandTransform == null || rightHandTransform == null || headTransform == null)
            {
                return;
            }

            bool validLeft = IsFinite(leftHandPose) && IsUsableRotation(leftHandRot);
            bool validRight = IsFinite(rightHandPose) && IsUsableRotation(rightHandRot);
            if (float.IsNaN(lerpTime) || float.IsInfinity(lerpTime))
            {
                validLeft = false;
                validRight = false;
            }
            if (!validLeft)
            {
                ClearLeftHandObservation();
            }
            if (!validRight)
            {
                ClearRightHandObservation();
            }
            if (!validLeft && !validRight)
            {
                return;
            }

            // Fusion sends both hand poses relative to the source headset. Rebuild
            // them from the actual realized head transform, not the planned body
            // root: a Facing target can move the head independently of the body.
            Pose head = CurrentHandPlacement();
            Quaternion headRotation = head.rotation;
            ConvertToAbsolute(ref leftHandPose, ref rightHandPose, headRotation);
            if (validLeft)
            {
                leftHandRot.Normalize();
                Vector3 offset = Vector3.Lerp(prevLeftHandPos, leftHandPose, lerpTime);
                Quaternion rotation = Quaternion.Lerp(prevLeftHandRot, leftHandRot, lerpTime);
                _leftHandLocalPosition = Quaternion.Inverse(headRotation) * offset;
                _leftHandLocalRotation = rotation;
                _leftHandBaselineUsesHeadTransform = true;
                _hasLeftHandBaseline = true;
                prevLeftHandPos = leftHandPose;
                prevLeftHandRot = leftHandRot;
                ApplyLeftHand();
            }
            if (validRight)
            {
                rightHandRot.Normalize();
                Vector3 offset = Vector3.Lerp(prevRightHandPos, rightHandPose, lerpTime);
                Quaternion rotation = Quaternion.Lerp(prevRightHandRot, rightHandRot, lerpTime);
                _rightHandLocalPosition = Quaternion.Inverse(headRotation) * offset;
                _rightHandLocalRotation = rotation;
                _rightHandBaselineUsesHeadTransform = true;
                _hasRightHandBaseline = true;
                prevRightHandPos = rightHandPose;
                prevRightHandRot = rightHandRot;
                ApplyRightHand();
            }
        }

        /// <summary>Marks a missing headset sample without inventing a zero pose.</summary>
        public void ClearHeadObservation()
        {
            if (RejectUnleasedHeadMutation()) return;
            _hasHeadBaseline = false;
            if (!_headTargetActive)
            {
                ApplyAllBaselines();
            }
        }

        /// <summary>Marks a missing left-controller sample independently.</summary>
        public void ClearLeftHandObservation()
        {
            if (!CanWriteUnrelatedHands()) return;
            _hasLeftHandBaseline = false;
            _leftHandBaselineUsesHeadTransform = false;
            if (!_leftHandTargetActive)
            {
                ApplyLeftHand();
            }
        }

        /// <summary>Marks a missing right-controller sample independently.</summary>
        public void ClearRightHandObservation()
        {
            if (!CanWriteUnrelatedHands()) return;
            _hasRightHandBaseline = false;
            _rightHandBaselineUsesHeadTransform = false;
            if (!_rightHandTargetActive)
            {
                ApplyRightHand();
            }
        }

        /// <summary>Called only by the explicit anchor bridge.</summary>
        public void SetAnchorTargetOverridesEnabled(bool enabled)
        {
            if (RejectUnleasedHeadMutation()) return;
            if (_foundationRetargetOwner != null) return;
            SetAnchorTargetOverridesEnabledCore(enabled);
        }

        internal bool SetAnchorTargetOverridesEnabled(SbRetargetAnchorTargetBridge bridge,
                                                       bool enabled)
        {
            if (!CanBridgeWrite(bridge)) return false;
            SetAnchorTargetOverridesEnabledCore(enabled);
            return true;
        }

        private void SetAnchorTargetOverridesEnabledCore(bool enabled)
        {
            if (!enabled)
            {
                _headTargetActive = false;
                _leftHandTargetActive = false;
                _rightHandTargetActive = false;
                // Withdraw every channel while CurrentRootPlacement still uses the
                // retained root, including after a bridge's preceding per-anchor clears.
                // Once disabled, the visible head is the legacy basis: applying again
                // would compose its observation offset twice or overwrite a direct write.
                if (_anchorTargetOverridesEnabled) ApplyAllBaselines();
            }
            _anchorTargetOverridesEnabled = enabled;
        }

        /// <summary>
        /// Applies a world-space realization target on one supported tracked input.
        /// This never feeds a previous partially blended output back in as a new
        /// baseline, so a stable partial weight remains stable across frames.
        /// </summary>
        public bool SetAnchorTarget(string anchorName, Pose target, float weight)
        {
            if (RejectUnleasedHeadMutation()) return false;
            if (_foundationRetargetOwner != null) return false;
            return SetAnchorTargetCore(anchorName, target, weight);
        }

        internal bool SetAnchorTarget(SbRetargetAnchorTargetBridge bridge, string anchorName,
                                      Pose target, float weight)
        {
            return CanBridgeWrite(bridge) && SetAnchorTargetCore(anchorName, target, weight);
        }

        private bool SetAnchorTargetCore(string anchorName, Pose target, float weight)
        {
            if (!IsFinite(target.position) || !IsUsableRotation(target.rotation) ||
                float.IsNaN(weight) || float.IsInfinity(weight))
            {
                return false;
            }

            weight = Mathf.Clamp01(weight);
            switch (anchorName)
            {
                case SbRetargetAnchorTargetBridge.HeadAnchor:
                    _headTarget = new Pose(target.position, Normalized(target.rotation));
                    _headTargetWeight = weight;
                    _headTargetActive = true;
                    ApplyAllBaselines();
                    return true;
                case SbRetargetAnchorTargetBridge.LeftHandAnchor:
                    _leftHandTarget = new Pose(target.position, Normalized(target.rotation));
                    _leftHandTargetWeight = weight;
                    _leftHandTargetActive = true;
                    ApplyLeftHand();
                    return true;
                case SbRetargetAnchorTargetBridge.RightHandAnchor:
                    _rightHandTarget = new Pose(target.position, Normalized(target.rotation));
                    _rightHandTargetWeight = weight;
                    _rightHandTargetActive = true;
                    ApplyRightHand();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Returns one channel to its latest tracked baseline.</summary>
        public bool ClearAnchorTarget(string anchorName)
        {
            if (RejectUnleasedHeadMutation()) return false;
            if (_foundationRetargetOwner != null) return false;
            return ClearAnchorTargetCore(anchorName);
        }

        internal bool ClearAnchorTarget(SbRetargetAnchorTargetBridge bridge, string anchorName)
        {
            return CanBridgeWrite(bridge) && ClearAnchorTargetCore(anchorName);
        }

        private bool ClearAnchorTargetCore(string anchorName)
        {
            // In direct legacy mode an inactive clear cannot claim another baseline
            // write, including a bound bridge's repeated disable/destroy cleanup.
            switch (anchorName)
            {
                case SbRetargetAnchorTargetBridge.HeadAnchor:
                    if (!_headTargetActive && !_anchorTargetOverridesEnabled) return true;
                    _headTargetActive = false;
                    ApplyAllBaselines();
                    return true;
                case SbRetargetAnchorTargetBridge.LeftHandAnchor:
                    if (!_leftHandTargetActive && !_anchorTargetOverridesEnabled) return true;
                    _leftHandTargetActive = false;
                    ApplyLeftHand();
                    return true;
                case SbRetargetAnchorTargetBridge.RightHandAnchor:
                    if (!_rightHandTargetActive && !_anchorTargetOverridesEnabled) return true;
                    _rightHandTargetActive = false;
                    ApplyRightHand();
                    return true;
                default:
                    return false;
            }
        }

        private void ApplyAllBaselines()
        {
            ApplyHead();
            ApplyLeftHand();
            ApplyRightHand();
        }

        private void ApplyHead()
        {
            if (_realizationLease != null && !_realizationCommitInProgress)
            {
                _realizationLease.Invalid = true;
                return;
            }
            if (headTransform == null || (!_hasRootPlacement && !_hasHeadBaseline &&
                                          !_headTargetActive))
            {
                return;
            }
            Pose baseline = _hasHeadBaseline ? CurrentHeadBaseline() : CurrentRootPlacement();
            headTransform.SetPositionAndRotation(
                BlendPosition(baseline, _headTarget, _headTargetWeight, _headTargetActive),
                BlendHeadRotation(baseline, _headTarget, _headTargetWeight, _headTargetActive));
        }

        // A head target is a YAW: the controller builds its rotation as Euler(0, yaw, 0), and the Facing solve has no pitch. Taking that
        // rotation whole zeroed the pitch the root write had set (the source's head pitch, passed through by the core) whenever a head
        // target was active, which is always for a received avatar: it looked level at everything. The yaw comes from the target (blended
        // by its weight), the pitch and the roll stay the baseline's. A head's pitch is within +-90 degrees (HeadOrientation), where the
        // Euler decomposition is unambiguous.
        private static Quaternion BlendHeadRotation(Pose baseline, Pose target, float weight, bool active)
        {
            if (!active)
            {
                return baseline.rotation;
            }
            Vector3 b = baseline.rotation.eulerAngles;
            float yaw = Mathf.LerpAngle(b.y, target.rotation.eulerAngles.y, weight);
            return Quaternion.Euler(b.x, yaw, b.z);
        }

        private void ApplyLeftHand()
        {
            if (leftHandTransform == null || (!_hasLeftHandBaseline && !_leftHandTargetActive))
            {
                return;
            }
            Pose baseline = _hasLeftHandBaseline ? CurrentLeftHandBaseline()
                                                 : CurrentRootPlacement();
            leftHandTransform.SetPositionAndRotation(
                BlendPosition(baseline, _leftHandTarget, _leftHandTargetWeight,
                              _leftHandTargetActive),
                BlendRotation(baseline, _leftHandTarget, _leftHandTargetWeight,
                              _leftHandTargetActive));
        }

        private void ApplyRightHand()
        {
            if (rightHandTransform == null || (!_hasRightHandBaseline && !_rightHandTargetActive))
            {
                return;
            }
            Pose baseline = _hasRightHandBaseline ? CurrentRightHandBaseline()
                                                  : CurrentRootPlacement();
            rightHandTransform.SetPositionAndRotation(
                BlendPosition(baseline, _rightHandTarget, _rightHandTargetWeight,
                              _rightHandTargetActive),
                BlendRotation(baseline, _rightHandTarget, _rightHandTargetWeight,
                              _rightHandTargetActive));
        }

        private static Vector3 BlendPosition(Pose baseline, Pose target, float weight, bool active)
        {
            return active ? Vector3.Lerp(baseline.position, target.position, weight)
                          : baseline.position;
        }

        private static Quaternion BlendRotation(Pose baseline, Pose target, float weight, bool active)
        {
            return active ? Quaternion.Slerp(baseline.rotation, target.rotation, weight)
                          : baseline.rotation;
        }

        private Pose CurrentRootPlacement()
        {
            // No bridge means exact legacy direct-transform ownership: callers may
            // write headTransform outside SetHeadTransform (including the original
            // avatar EntityTransform path), and the next relative hand sample must
            // use that current visible pose rather than an earlier cached setter.
            if (!_anchorTargetOverridesEnabled)
            {
                return new Pose(headTransform != null ? headTransform.position : Vector3.zero,
                    headTransform != null ? headTransform.rotation : Quaternion.identity);
            }
            return _hasRootPlacement ? _rootPlacement : new Pose(
                headTransform != null ? headTransform.position : Vector3.zero,
                headTransform != null ? headTransform.rotation : Quaternion.identity);
        }

        private Pose CurrentHandPlacement()
        {
            // The Fusion packet is head-relative. This is intentionally the actual
            // visible pose after a head target has been applied, never the planned
            // body/root placement that may now have a different heading or origin.
            return new Pose(headTransform != null ? headTransform.position : Vector3.zero,
                headTransform != null ? headTransform.rotation : Quaternion.identity);
        }

        /// <summary>The time the head chart takes to take over the raw body's placement, and to give it back.</summary>
        public const float HeadChartBlendSeconds = 0.25f;

        private bool _hasHeadChart;
        private Vector3 _headChartPosition;
        private float _headChartYawDegrees;
        private float _headChartWeight;

        /// <summary>
        /// STATE ONLY (no transform is written): the head anchor's chart (ABI 4.14), the source head's own sample replayed through the
        /// shared object that holds it, in Unity world (a position and an absolute yaw), or <paramref name="has"/> false for none. It is
        /// what the raw Meta body is placed by during an interaction (<see cref="TryGetHeadChart"/>, ContinuousCompensation). The Sb
        /// avatar (this retarget's head and hands) is not touched by it: Facing governs it.
        /// </summary>
        public void SetHeadChart(bool has, Vector3 headChartPosition, float headChartYawDegrees)
        {
            has = has && IsFinite(headChartPosition) && !float.IsNaN(headChartYawDegrees) && !float.IsInfinity(headChartYawDegrees);
            _hasHeadChart = has;
            if (!has) return;
            _headChartPosition = headChartPosition;
            _headChartYawDegrees = headChartYawDegrees;
        }

        /// <summary>STATE ONLY: moves the chart's share toward 1 (a chart is set) or 0 (none) by <paramref name="seconds"/>.</summary>
        public void AdvanceHeadChartBlend(float seconds)
        {
            if (float.IsNaN(seconds) || seconds <= 0f) return;
            _headChartWeight = Mathf.MoveTowards(_headChartWeight, _hasHeadChart ? 1f : 0f, seconds / HeadChartBlendSeconds);
        }

        /// <summary>STATE ONLY: drops the chart at once (the driver was closed).</summary>
        public void ClearHeadChart()
        {
            _hasHeadChart = false;
            _headChartWeight = 0f;
        }

        private void LateUpdate() => AdvanceHeadChartBlend(Time.deltaTime);

        /// <summary>
        /// The head chart and its current share, while it has one (set, or blending out): what the raw Meta body is placed by during an
        /// interaction (<see cref="ContinuousCompensation.UseChartReference"/>). False when the share is 0.
        /// </summary>
        public bool TryGetHeadChart(out Vector3 position, out float yawDegrees, out float weight)
        {
            position = _headChartPosition;
            yawDegrees = _headChartYawDegrees;
            weight = _headChartWeight;
            return _headChartWeight > 0f;
        }

        /// <summary>The head chart as last set (true while one is set, whatever its share), and its share.</summary>
        public bool TryGetHeadChartState(out Vector3 position, out float yawDegrees, out float weight)
        {
            position = _headChartPosition;
            yawDegrees = _headChartYawDegrees;
            weight = _headChartWeight;
            return _hasHeadChart;
        }

        private Pose CurrentLeftHandBaseline()
        {
            Pose basis = _leftHandBaselineUsesHeadTransform
                ? CurrentHandPlacement()
                : CurrentRootPlacement();
            return new Pose(basis.position + basis.rotation * _leftHandLocalPosition,
                basis.rotation * _leftHandLocalRotation);
        }

        private Pose CurrentHeadBaseline()
        {
            Pose root = CurrentRootPlacement();
            return new Pose(root.position + root.rotation * _headLocalPosition,
                root.rotation * _headLocalRotation);
        }

        private void SetHeadBaseline(Pose world)
        {
            Pose root = CurrentRootPlacement();
            Quaternion inverse = Quaternion.Inverse(root.rotation);
            _headLocalPosition = inverse * (world.position - root.position);
            _headLocalRotation = inverse * world.rotation;
            _hasHeadBaseline = true;
        }

        private void SetLeftHandBaseline(Pose world)
        {
            Pose root = CurrentRootPlacement();
            Quaternion inverse = Quaternion.Inverse(root.rotation);
            _leftHandLocalPosition = inverse * (world.position - root.position);
            _leftHandLocalRotation = inverse * world.rotation;
            _leftHandBaselineUsesHeadTransform = false;
            _hasLeftHandBaseline = true;
        }

        private void SetRightHandBaseline(Pose world)
        {
            Pose root = CurrentRootPlacement();
            Quaternion inverse = Quaternion.Inverse(root.rotation);
            _rightHandLocalPosition = inverse * (world.position - root.position);
            _rightHandLocalRotation = inverse * world.rotation;
            _rightHandBaselineUsesHeadTransform = false;
            _hasRightHandBaseline = true;
        }

        private Pose CurrentRightHandBaseline()
        {
            Pose basis = _rightHandBaselineUsesHeadTransform
                ? CurrentHandPlacement()
                : CurrentRootPlacement();
            return new Pose(basis.position + basis.rotation * _rightHandLocalPosition,
                basis.rotation * _rightHandLocalRotation);
        }

        private static Quaternion Normalized(Quaternion value)
        {
            value.Normalize();
            return value;
        }

        private static bool IsUsableRotation(Quaternion value)
        {
            return IsFinite(value) && !(value.x == 0f && value.y == 0f &&
                                        value.z == 0f && value.w == 0f);
        }

        private static bool IsValidPose(Pose value) => IsFinite(value.position) &&
                                                     IsUsableRotation(value.rotation);

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
