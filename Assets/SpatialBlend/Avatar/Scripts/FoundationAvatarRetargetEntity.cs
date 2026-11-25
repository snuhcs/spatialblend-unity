// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Foundation-only consumer for a receiver actor's realization output and
    /// recorded tracking observations.  It deliberately owns neither a Fusion
    /// entity nor a Meta avatar mesh: the existing retarget transform remains the
    /// SDK tracking source and <see cref="SbRetargetAnchorTargetBridge"/> remains
    /// the body-target consumer.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FoundationAvatarRetargetEntity : MonoBehaviour
    {
        private const int MinimumObservationAnchorCount = 4;
        private const int MaximumObservationAnchorCount = 16;
        private readonly HashSet<string> _knownAnchors = new HashSet<string>(StringComparer.Ordinal);

        private SpatialBlendActor _receiver;
        private AnchorTargetController _targets;
        private SbRetargetTransform _retarget;
        private SbRetargetAnchorTargetBridge _ownedBridge;
        private Pose _sourceReference;
        private Pose _sourceChart;
        private Pose _receiverReference;
        private Pose _placementChart;
        private string _rootAnchorName;
        private long _lastSequence = -1;
        private double _lastTime = double.NegativeInfinity;
        private long _generation;
        private Action<SpatialBlendRootTarget> _rootHandler;
        private Action _rootClearedHandler;

        public bool IsConfigured { get; private set; }
        public string LastError { get; private set; }

        /// <summary>
        /// Binds one receiver to the original retarget/tracking path.  The supplied
        /// poses are explicit source and receiver charts: sourceReference maps to
        /// receiverReference until a receiver root realization supplies a newer
        /// placement chart.
        /// </summary>
        public bool TryConfigure(SpatialBlendActor receiver, AnchorTargetController targets,
                                 SbRetargetTransform retarget, Pose sourceReference,
                                 Pose receiverReference, string rootAnchorName,
                                 out string error)
        {
            return TryConfigure(receiver, targets, retarget, sourceReference, receiverReference,
                rootAnchorName, new[]
                {
                    rootAnchorName,
                    SbRetargetAnchorTargetBridge.HeadAnchor,
                    SbRetargetAnchorTargetBridge.LeftHandAnchor,
                    SbRetargetAnchorTargetBridge.RightHandAnchor
                }, out error);
        }

        /// <summary>
        /// Binds one receiver with an explicitly admitted, immutable observation chart.
        /// Extra chart channels remain observations for frame admission only; this
        /// component maps only the measured head and hand SDK inputs.
        /// </summary>
        public bool TryConfigure(SpatialBlendActor receiver, AnchorTargetController targets,
                                 SbRetargetTransform retarget, Pose sourceReference,
                                 Pose receiverReference, string rootAnchorName,
                                 IEnumerable<string> admittedObservationAnchorIds,
                                 out string error)
        {
            if (receiver == null || targets == null || retarget == null ||
                !IsValidAnchorId(rootAnchorName) || !IsFinitePose(sourceReference) ||
                !IsFinitePose(receiverReference) || retarget.headTransform == null ||
                retarget.leftHandTransform == null || retarget.rightHandTransform == null)
            {
                return Fail("Receiver, targets, retarget transforms, finite reference poses, and root anchor are required.", out error);
            }
            if (string.Equals(rootAnchorName, SbRetargetAnchorTargetBridge.HeadAnchor,
                              StringComparison.Ordinal) ||
                string.Equals(rootAnchorName, SbRetargetAnchorTargetBridge.LeftHandAnchor,
                              StringComparison.Ordinal) ||
                string.Equals(rootAnchorName, SbRetargetAnchorTargetBridge.RightHandAnchor,
                              StringComparison.Ordinal))
            {
                return Fail("The receiver root anchor must be distinct from tracked head and hand anchors.", out error);
            }

            if (!TryFreezeKnownAnchors(rootAnchorName, admittedObservationAnchorIds,
                                       out string[] knownAnchors, out string chartError))
            {
                return Fail(chartError, out error);
            }

            // Validate before touching a live binding.  A bridge on another object
            // is visible through the retarget's override state, while local
            // consumers are fenced by its explicit owner lease.
            if (!retarget.CanAcceptFoundationRetargetOwner(this))
            {
                return Fail("Retarget target overrides are already owned by another consumer.", out error);
            }

            Pose normalizedSource = Normalize(sourceReference);
            Pose normalizedReceiver = Normalize(receiverReference);
            Release();
            if (!retarget.TryAcquireFoundationRetargetOwner(this))
            {
                return Fail("Retarget ownership changed while configuring the local consumer.", out error);
            }

            _receiver = receiver;
            _targets = targets;
            _retarget = retarget;
            _sourceReference = normalizedSource;
            _sourceChart = normalizedSource;
            _receiverReference = normalizedReceiver;
            _placementChart = _receiverReference;
            _rootAnchorName = rootAnchorName;
            _knownAnchors.Clear();
            foreach (string anchor in knownAnchors)
            {
                _knownAnchors.Add(anchor);
            }
            _lastSequence = -1;
            _lastTime = double.NegativeInfinity;

            _ownedBridge = retarget.gameObject.AddComponent<SbRetargetAnchorTargetBridge>();
            if (_ownedBridge == null ||
                !retarget.TryAuthorizeFoundationBridge(this, _ownedBridge))
            {
                retarget.ReleaseFoundationRetargetOwner(this);
                DetachWithoutMutating();
                return Fail("Could not authorize the local retarget bridge.", out error);
            }
            // Authorization deliberately precedes Configure/OnEnable delivery: a
            // same-frame foreign bridge can never claim an unguarded write window.
            _ownedBridge.Configure(targets, retarget);
            long generation = ++_generation;
            SpatialBlendActor expected = receiver;
            _rootHandler = target => OnRootTarget(expected, generation, target);
            _rootClearedHandler = () => OnRootTargetsCleared(expected, generation);
            receiver.RootTargetAvailable += _rootHandler;
            receiver.RootTargetsCleared += _rootClearedHandler;
            IsConfigured = true;
            _retarget.ApplyRootRealizationTransform(_placementChart.position, _placementChart.rotation);
            PublishPlacement();
            LastError = null;
            error = null;
            return true;
        }

        /// <summary>
        /// Applies one complete, immutable source-observation snapshot.  Missing
        /// tracked channels explicitly clear only their own SDK-active flag.
        /// </summary>
        public bool TryApplyObservationFrame(AnchorPoseFrame frame, out string error)
        {
            if (!IsConfigured)
            {
                return Fail("Consumer is not configured.", out error);
            }
            if (!OwnsRetarget())
            {
                DetachWithoutMutating();
                return Fail("Consumer retarget ownership is no longer live.", out error);
            }
            if (!ValidateAndCopyFrame(frame, out AnchorPoseFrame copy, out error))
            {
                string validationError = error;
                return Fail(validationError, out error);
            }

            Pose sourceChart = _sourceChart;
            var samples = new Dictionary<string, AnchorPoseSample>(StringComparer.Ordinal);
            foreach (AnchorPoseSample sample in copy.anchors)
            {
                samples.Add(sample.anchorId, sample);
                if (string.Equals(sample.anchorId, _rootAnchorName, StringComparison.Ordinal))
                    sourceChart = new Pose(sample.worldPosition, sample.worldRotation);
            }

            bool hasHead = samples.TryGetValue(SbRetargetAnchorTargetBridge.HeadAnchor,
                                                out AnchorPoseSample head);
            bool hasLeft = samples.TryGetValue(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                                                out AnchorPoseSample left);
            bool hasRight = samples.TryGetValue(SbRetargetAnchorTargetBridge.RightHandAnchor,
                                                 out AnchorPoseSample right);
            Pose mappedHead = default;
            Pose mappedLeft = default;
            Pose mappedRight = default;
            // Mapping is part of frame admission, not an individual channel write:
            // finite source coordinates can still overflow a receiver chart.
            if ((hasHead && !TryMap(sourceChart, head, out mappedHead)) ||
                (hasLeft && !TryMap(sourceChart, left, out mappedLeft)) ||
                (hasRight && !TryMap(sourceChart, right, out mappedRight)))
            {
                return Fail("Mapped observation pose is not finite.", out error);
            }
            if (!_retarget.TryApplyFoundationObservations(hasHead, mappedHead, hasLeft,
                                                           mappedLeft, hasRight, mappedRight))
            {
                return Fail("Retarget observations are no longer fully available.", out error);
            }

            _lastSequence = copy.sequence;
            _lastTime = copy.timeSeconds;
            _sourceChart = sourceChart;
            LastError = null;
            error = null;
            return true;
        }

        /// <summary>Clears tracked baselines and restores configured charts for a new replay.</summary>
        public void ClearObservations()
        {
            if (!OwnsRetarget())
            {
                DetachWithoutMutating();
                return;
            }
            _retarget.TryClearFoundationObservations();
            _lastSequence = -1;
            _lastTime = double.NegativeInfinity;
            _sourceChart = _sourceReference;
            _placementChart = _receiverReference;
            _retarget.ApplyRootRealizationTransform(_placementChart.position, _placementChart.rotation);
            PublishPlacement();
            LastError = null;
        }

        /// <summary>Releases only subscriptions and bridge attachment created by this component.</summary>
        public void Release()
        {
            ++_generation;
            if (_receiver != null)
            {
                if (_rootHandler != null) _receiver.RootTargetAvailable -= _rootHandler;
                if (_rootClearedHandler != null) _receiver.RootTargetsCleared -= _rootClearedHandler;
            }
            _rootHandler = null;
            _rootClearedHandler = null;
            SbRetargetTransform retarget = _retarget;
            SbRetargetAnchorTargetBridge bridge = _ownedBridge;
            bool ownsRetarget = retarget != null && retarget.IsFoundationRetargetOwner(this);
            if (ownsRetarget)
            {
                // Individual clears remain safe if an SDK Transform was destroyed
                // during teardown; no replacement owner can be touched below.
                retarget.ClearHeadObservation();
                retarget.ClearLeftHandObservation();
                retarget.ClearRightHandObservation();
            }
            if (bridge != null)
            {
                // This is explicit rather than relying on deferred OnDisable.  If
                // a new consumer owns the retarget now, its identity gate makes the
                // retired bridge's cleanup a subscription-only operation.
                bridge.DetachFoundationBinding(retarget);
            }
            if (ownsRetarget)
            {
                retarget.ReleaseFoundationBridge(this, bridge);
                retarget.ReleaseFoundationRetargetOwner(this);
            }
            if (bridge != null)
            {
                bridge.enabled = false;
                if (Application.isPlaying) Destroy(bridge);
                else DestroyImmediate(bridge);
            }
            _ownedBridge = null;
            _receiver = null;
            _targets = null;
            _retarget = null;
            _knownAnchors.Clear();
            IsConfigured = false;
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void OnRootTarget(SpatialBlendActor expected, long generation,
                                  SpatialBlendRootTarget target)
        {
            if (!IsCurrent(expected, generation) || !IsFinitePose(new Pose(target.Position, target.Rotation)))
                return;
            _placementChart = Normalize(new Pose(target.Position, target.Rotation));
            // This is realization only.  It recomposes cached observations but does
            // not mark headset/controllers active or create a tracking baseline.
            _retarget.ApplyRootRealizationTransform(_placementChart.position, _placementChart.rotation);
            PublishPlacement();
        }

        // This component places the receiver's body, so it is the one that publishes where the body stands: the
        // core solves a receiver only from a published pose.
        private void PublishPlacement()
        {
            if (_receiver != null)
            {
                _receiver.TryPublishEmbodiedPose(_placementChart.position, _placementChart.rotation.eulerAngles.y, out _);
            }
        }

        private void OnRootTargetsCleared(SpatialBlendActor expected, long generation)
        {
            if (!IsCurrent(expected, generation)) return;
            // A native root clear retires the command, not the receiver's already
            // realized placement.  Retaining this chart keeps a later body-only
            // observation in the same receiver frame without fabricating presence.
        }

        private bool IsCurrent(SpatialBlendActor expected, long generation) =>
            IsConfigured && ReferenceEquals(expected, _receiver) && generation == _generation &&
            expected != null && OwnsRetarget();

        private bool OwnsRetarget() => _retarget != null &&
            _retarget.IsFoundationRetargetOwner(this);

        private void DetachWithoutMutating()
        {
            // Release handles both owner and already-replaced lifetimes.  In the
            // latter case it still disposes our subscriptions/attachment, but the
            // retarget-side bridge identity gate forbids any old-state mutation.
            Release();
        }

        private bool TryMap(Pose sourceChart, AnchorPoseSample sample, out Pose mapped)
        {
            mapped = default;
            Quaternion sourceInverse = Quaternion.Inverse(sourceChart.rotation);
            Vector3 localPosition = sourceInverse * (sample.worldPosition - sourceChart.position);
            Quaternion localRotation = sourceInverse * sample.worldRotation;
            Vector3 worldPosition = _placementChart.position + _placementChart.rotation * localPosition;
            Quaternion worldRotation = _placementChart.rotation * localRotation;
            if (!IsFinite(localPosition) || !IsFinite(localRotation) || !IsFinite(worldPosition) ||
                !IsUsableRotation(worldRotation))
            {
                return false;
            }
            mapped = new Pose(worldPosition, Normalized(worldRotation));
            return true;
        }

        private bool ValidateAndCopyFrame(AnchorPoseFrame frame, out AnchorPoseFrame copy,
                                          out string error)
        {
            copy = null;
            error = null;
            if (frame == null || frame.schema != AnchorPoseFrame.CurrentSchema ||
                !string.Equals(frame.frame, AnchorPoseFrame.UnityWorldFrame, StringComparison.Ordinal) ||
                frame.sequence < 0 || frame.sequence <= _lastSequence || !IsFinite(frame.timeSeconds) ||
                frame.timeSeconds < 0d || frame.timeSeconds < _lastTime || frame.anchors == null)
            {
                error = "Frame requires the current schema, unity-world coordinates, and monotonic sequence/time.";
                return false;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var anchors = new AnchorPoseSample[frame.anchors.Length];
            for (int index = 0; index < frame.anchors.Length; ++index)
            {
                AnchorPoseSample sample = frame.anchors[index];
                if (sample == null || !IsValidAnchorId(sample.anchorId) || !_knownAnchors.Contains(sample.anchorId) ||
                    !ids.Add(sample.anchorId) || !IsFinite(sample.worldPosition) ||
                    !IsUnitRotation(sample.worldRotation))
                {
                    error = "Frame samples require unique known IDs, finite poses, and unit rotations.";
                    return false;
                }
                anchors[index] = new AnchorPoseSample
                {
                    anchorId = sample.anchorId,
                    worldPosition = sample.worldPosition,
                    worldRotation = sample.worldRotation
                };
            }
            copy = new AnchorPoseFrame
            {
                schema = frame.schema,
                frame = frame.frame,
                sequence = frame.sequence,
                timeSeconds = frame.timeSeconds,
                anchors = anchors
            };
            return true;
        }

        private static bool TryFreezeKnownAnchors(string rootAnchorName,
                                                  IEnumerable<string> admittedAnchorIds,
                                                  out string[] frozen, out string error)
        {
            frozen = null;
            error = null;
            if (admittedAnchorIds == null)
            {
                error = "An admitted observation chart is required.";
                return false;
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            var copied = new List<string>();
            foreach (string anchor in admittedAnchorIds)
            {
                if (copied.Count == MaximumObservationAnchorCount)
                {
                    error = "The admitted observation chart exceeds its bounded anchor count.";
                    return false;
                }
                if (!IsValidAnchorId(anchor) || !unique.Add(anchor))
                {
                    error = "The admitted observation chart requires unique valid anchor IDs.";
                    return false;
                }
                copied.Add(anchor);
            }

            if (copied.Count < MinimumObservationAnchorCount ||
                !unique.Contains(rootAnchorName) ||
                !unique.Contains(SbRetargetAnchorTargetBridge.HeadAnchor) ||
                !unique.Contains(SbRetargetAnchorTargetBridge.LeftHandAnchor) ||
                !unique.Contains(SbRetargetAnchorTargetBridge.RightHandAnchor))
            {
                error = "The admitted observation chart must include root, head, and both hands.";
                return false;
            }

            frozen = copied.ToArray();
            return true;
        }

        private bool Fail(string message, out string error)
        {
            LastError = message;
            error = message;
            return false;
        }

        private static Pose Normalize(Pose value) => new Pose(value.position, Normalized(value.rotation));
        private static Quaternion Normalized(Quaternion value) { value.Normalize(); return value; }
        private static bool IsValidAnchorId(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        private static bool IsFinitePose(Pose value) => IsFinite(value.position) && IsUsableRotation(value.rotation);
        private static float RotationMagnitudeSquared(Quaternion value) => value.x * value.x +
            value.y * value.y + value.z * value.z + value.w * value.w;
        private static bool IsUsableRotation(Quaternion value) => IsFinite(value) &&
            RotationMagnitudeSquared(value) > .000001f;
        private static bool IsUnitRotation(Quaternion value) => IsUsableRotation(value) &&
            Math.Abs(RotationMagnitudeSquared(value) - 1f) <= .001f;
    }
}
