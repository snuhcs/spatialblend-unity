// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SpatialBlend.Motion;
using UnityEngine;
using Lease = SpatialBlend.SbRetargetTransform.RealizationLease;
using Sample = SpatialBlend.SbRetargetTransform.RealizedSample;
using Result = SpatialBlend.SbRetargetTransform.RealizationResult;
using Contract = SpatialBlend.SbRetargetTransform.RealizationContract;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Privacy-safe observation of one checked root-path drain/apply boundary.
    /// It contains only booleans: no actor identity, pose, request payload, native
    /// error text, or publication history is retained or emitted.
    /// </summary>
    internal readonly struct CheckedRootPathDiagnostic : IEquatable<CheckedRootPathDiagnostic>
    {
        internal readonly bool RootPositionEventReceived;
        internal readonly bool RootGoalHasPosition;
        internal readonly bool RootCommitSucceeded;
        internal readonly bool RootTranslationObserved;
        internal readonly bool TargetFaultPresent;

        internal CheckedRootPathDiagnostic(bool rootPositionEventReceived, bool rootGoalHasPosition,
                                           bool rootCommitSucceeded, bool rootTranslationObserved,
                                           bool targetFaultPresent)
        {
            RootPositionEventReceived = rootPositionEventReceived;
            RootGoalHasPosition = rootGoalHasPosition;
            RootCommitSucceeded = rootCommitSucceeded;
            RootTranslationObserved = rootTranslationObserved;
            TargetFaultPresent = targetFaultPresent;
        }

        public bool Equals(CheckedRootPathDiagnostic other) =>
            RootPositionEventReceived == other.RootPositionEventReceived &&
            RootGoalHasPosition == other.RootGoalHasPosition &&
            RootCommitSucceeded == other.RootCommitSucceeded &&
            RootTranslationObserved == other.RootTranslationObserved &&
            TargetFaultPresent == other.TargetFaultPresent;

        public override bool Equals(object obj) => obj is CheckedRootPathDiagnostic other && Equals(other);

        public override int GetHashCode()
        {
            int result = RootPositionEventReceived ? 1 : 0;
            result = result * 2 + (RootGoalHasPosition ? 1 : 0);
            result = result * 2 + (RootCommitSucceeded ? 1 : 0);
            result = result * 2 + (RootTranslationObserved ? 1 : 0);
            return result * 2 + (TargetFaultPresent ? 1 : 0);
        }
    }

    internal sealed class EmbodiedTargetFrame
    {
        internal readonly object Owner;
        internal readonly ulong Incarnation, RealizedRevision;
        internal readonly float Seconds;
        internal readonly IReadOnlyList<EmbodiedTargetConsumer.Operation> Operations;
        internal readonly bool RootPositionEventReceived;
        internal readonly bool HasWork;

        internal EmbodiedTargetFrame(object owner, Sample sample, float seconds,
                                     EmbodiedTargetConsumer.Operation[] operations, bool goals,
                                     bool rootPositionEventReceived)
        {
            Owner = owner; Incarnation = sample.Incarnation; RealizedRevision = sample.Revision;
            Seconds = seconds; Operations = Array.AsReadOnly(operations);
            RootPositionEventReceived = rootPositionEventReceived;
            HasWork = goals || operations.Length > 0;
        }
    }

    internal sealed partial class OvrAvatarEmbodiedDriver
    {
        private IEmbodiedTargetEventCore _targetCore;
        private EmbodiedTargetConsumer _targetConsumer;
        private EmbodiedTargetGoal _rootGoal, _headGoal;
        private SbNetworkAvatarEntity _checkedEntity;
        private SbRetargetTransform _checkedRetarget;
        private SbRetargetAnchorTargetBridge _suspendedBridge;
        private Lease _checkedLease;
        private uint? _targetHeadId;
        private bool _targetClosed, _targetDraining, _targetApplying, _targetPublishing;
        private string _targetFault;
        private float? _targetPitchDegrees;
        private bool _checkedHeadTargetEstablished;
        private Sample _lastCheckedSample;
        private readonly HashSet<uint> _unsupportedTargetAnchors = new HashSet<uint>();

        // The diagnostic state is observational only. It does not participate in
        // polling, goal ownership, write admission, publication, or fault policy.
        private bool _currentDrainRootPositionEventReceived;
        private bool _diagnosticApplyingTargetFrame;
        private bool _diagnosticRootCommitSucceeded;
        private bool _diagnosticRootTranslationObserved;
        private CheckedRootPathDiagnostic? _lastCheckedRootPathDiagnostic;

        // The manager keeps one core adapter for the entire native space lifetime.
        // Unsubscribe, failed drains and driver removal do not retire native clocks.
        // Never admit new history for an actor already claimed in that lifetime;
        // normal reconfiguration retains this driver instead. Weak keys let a
        // destroyed session and all its actor tombstones be collected together.
        private static readonly ConditionalWeakTable<IEmbodiedCore, HashSet<long>> TargetActorLifetimes =
            new ConditionalWeakTable<IEmbodiedCore, HashSet<long>>();

        internal bool TargetEventsEnabled { get; private set; }
        internal bool TargetLifetimeOpen => TargetEventsEnabled && !_targetClosed;
        internal string TargetEventError => _targetFault ?? _targetConsumer?.Error;
        internal EmbodiedTargetConsumer TargetConsumer => _targetConsumer;
        internal IReadOnlyCollection<uint> UnsupportedTargetAnchors => _unsupportedTargetAnchors;
        internal CheckedRootPathDiagnostic? LastCheckedRootPathDiagnostic => _lastCheckedRootPathDiagnostic;
        private ulong TargetEpoch => _targetConsumer.Epoch(EmbodiedDriver.RootAnchorId);

        /// <summary>
        /// Clean-boundary admission only, before legacy publication/polling. The caller
        /// explicitly owns synchronous root/head/parent mutations for this actor.
        /// SpatialBlendManager.ConfigureEmbodiedDriver calls this for every received avatar: the checked
        /// path is the only production path; an avatar it refuses is not driven.
        /// </summary>
        internal bool TryEnableTargetEvents(Contract contract, out string error)
        {
            error = null;
            if (TargetEventsEnabled || _targetClosed || _currentMotionPlan != null ||
                contract != Contract.SynchronousExclusiveRootAndHead ||
                !(_core is IEmbodiedTargetEventCore capability) ||
                !(_entity is SbNetworkAvatarEntity network) || _entity.GetType() != typeof(SbNetworkAvatarEntity))
            {
                error = "Typed delivery requires a clean synchronous network-avatar owner, not legacy or asynchronous ownership.";
                return false;
            }
            HashSet<long> lifetimes = TargetActorLifetimes.GetValue(_core, _ => new HashSet<long>());
            lock (lifetimes)
            {
                if (lifetimes.Contains(_actor))
                {
                    error = "This native actor already has checked history; retain its driver or replace the native actor.";
                    _targetClosed = true;
                    return false;
                }
            }
            SbRetargetTransform retarget = network.CheckedRetarget;
            if (retarget == null || !ReferenceEquals(network.EntityTransform, retarget.headTransform))
            {
                error = "The checked root sample must use the actual retarget head origin.";
                return false;
            }
            SbRetargetAnchorTargetBridge bridge = _entity.GetComponent<SbRetargetAnchorTargetBridge>();
            if (bridge != null && !bridge.TrySuspendForCheckedRealization(this,
                    _entity.GetComponent<AnchorTargetController>(), retarget))
            {
                error = "The legacy bridge is not an exclusively owned local binding.";
                return false;
            }
            bool admitted = false, subscriptionAttempted = false, uncertainSubscription = false;
            Lease lease = null;
            try
            {
                if (!network.TryAcquireCheckedRealization(this, contract, out lease) ||
                    lease.TryRead(out Sample sample) != Result.Committed)
                {
                    error = "Checked root/head realization admission refused; no observation or ownership was manufactured.";
                    return false;
                }
                // Finish fallible managed preparation before the subscription changes.
                var consumer = new EmbodiedTargetConsumer(_actor);
                var rootGoal = new EmbodiedTargetGoal(Config);
                var headGoal = new EmbodiedTargetGoal(Config);
                uint? directHead = null;
                if (!_boundByAnchorName.ContainsKey(SbRetargetAnchorTargetBridge.HeadAnchor) &&
                    TryAnchorId(SbRetargetAnchorTargetBridge.HeadAnchor, out uint headId) && headId != 0)
                    directHead = headId;
                foreach (BoneBinding binding in _bound) ReportUnsupportedTypedAnchor(binding.AnchorId);
                // Native may refuse a queued legacy exception. Never drain/drop it to
                // force this transition, and never switch to bool polling afterwards.
                lock (lifetimes)
                {
                    if (!lifetimes.Add(_actor))
                    {
                        error = "The native actor was claimed during checked admission; replace it.";
                        _targetClosed = true;
                        return false;
                    }
                }
                subscriptionAttempted = true;
                if (!capability.TrySetTargetEventDelivery(_actor, true, out error))
                {
                    // Native false is a refused transition, unlike an exception
                    // whose side effects are unknown. No sample was published.
                    lock (lifetimes) lifetimes.Remove(_actor);
                    return false;
                }
                _checkedEntity = network; _checkedRetarget = retarget; _checkedLease = lease;
                _targetCore = capability;
                _targetConsumer = consumer; _rootGoal = rootGoal; _headGoal = headGoal;
                _lastCheckedSample = sample; _suspendedBridge = bridge; _targetHeadId = directHead;
                TargetEventsEnabled = true;
                admitted = true;
                return true;
            }
            catch (Exception exception)
            {
                error = "Checked admission failed: " + exception.Message;
                if (subscriptionAttempted)
                {
                    // An exception does not prove native made no change. Never allow
                    // legacy reuse of this driver or replay the bridge after uncertainty.
                    uncertainSubscription = true;
                    _targetClosed = true;
                    try
                    {
                        if (!capability.TrySetTargetEventDelivery(_actor, false, out string teardown))
                            error += "; native teardown refused: " + teardown;
                    }
                    catch (Exception teardown) { error += "; native teardown failed: " + teardown.Message; }
                }
                return false;
            }
            finally
            {
                if (!admitted)
                {
                    lease?.Dispose();
                    // A destroyed bridge is Unity-null; ?. would call into it.
                    if (bridge != null) bridge.EndCheckedSuspension(this, !uncertainSubscription);
                }
            }
        }

        private bool OwnsCheckedAnchor(string name) =>
            string.Equals(name, "root", StringComparison.Ordinal) ||
            (_targetHeadId.HasValue && string.Equals(name, SbRetargetAnchorTargetBridge.HeadAnchor, StringComparison.Ordinal)) ||
            _boundByAnchorName.ContainsKey(name ?? string.Empty);

        private bool SupportedTarget(uint anchor) => anchor == 0 || (_targetHeadId.HasValue && anchor == _targetHeadId.Value);

        private void ReportUnsupportedTypedAnchor(uint anchor)
        {
            if (!_unsupportedTargetAnchors.Add(anchor)) return;
            TryAnchorName(anchor, out string name);
            Debug.LogWarning($"[EmbodiedDriver] typed target '{name ?? anchor.ToString()}' is unsupported: " +
                             "only checked root/direct-head winding is available; bone/quaternion observations do not prove continuous yaw. " +
                             "Supported root/direct-head channels remain enabled.");
        }

        private void TargetFault(string message)
        {
            if (_targetFault != null) return;
            _targetFault = message;
            Debug.LogWarning("[EmbodiedDriver] checked target realization stopped: " + message +
                             " Actor replacement is required; retained history is not reinterpreted as an empty drain.");
        }

        private static bool SameRootPosition(Vector3 first, Vector3 second) =>
            first.x == second.x && first.y == second.y && first.z == second.z;

        private void RecordCheckedRootCommit()
        {
            if (_diagnosticApplyingTargetFrame) _diagnosticRootCommitSucceeded = true;
        }

        private void RecordCheckedRootTranslation(Sample before, Sample after)
        {
            if (_diagnosticApplyingTargetFrame &&
                !SameRootPosition(before.RootPlacement.position, after.RootPlacement.position))
            {
                _diagnosticRootTranslationObserved = true;
            }
        }

        private void ObserveCheckedRootPath(bool rootPositionEventReceived, bool rootGoalHasPosition,
                                            bool rootCommitSucceeded, bool rootTranslationObserved)
        {
            _lastCheckedRootPathDiagnostic = new CheckedRootPathDiagnostic(rootPositionEventReceived, rootGoalHasPosition,
                rootCommitSucceeded, rootTranslationObserved, TargetEventError != null);
        }

        private bool ReadChecked(out Sample sample)
        {
            sample = default;
            if (_targetClosed || _checkedEntity == null || _checkedRetarget == null ||
                !ReferenceEquals(_checkedEntity.CheckedRetarget, _checkedRetarget) ||
                !ReferenceEquals(_checkedEntity.EntityTransform, _checkedLease.Head))
            {
                TargetFault("Actor retarget/sample-origin identity changed."); return false;
            }
            Result result = _checkedLease.TryRead(out sample);
            if (result != Result.Committed)
            {
                TargetFault("Actual root/head readback refused: " + result); return false;
            }
            _lastCheckedSample = sample;
            return true;
        }

        private EmbodiedPose PublishCheckedPose()
        {
            if (!ReadChecked(out Sample sample)) return RootSample(_lastCheckedSample);
            EmbodiedPose root = RootSample(sample);
            if (_targetPublishing || _targetDraining || _targetClosed) return root;
            _targetPublishing = true;
            try
            {
                PublishCheckedAnchor(0, root, sample.RootYawRadians);
                if (_targetHeadId.HasValue)
                {
                    var head = new EmbodiedPose(sample.Head.position, (float)(sample.HeadYawRadians * 180d / Math.PI),
                        HeadOrientation.PitchDegrees(sample.Head.rotation));
                    PublishCheckedAnchor(_targetHeadId.Value, head, sample.HeadYawRadians);
                }
                // Bone-bound anchors deliberately have no synthetic exact-yaw sample.
                // Publishing their quaternion endpoints as winding would be a false capability.
            }
            finally { _targetPublishing = false; }
            return root;
        }

        private static EmbodiedPose RootSample(Sample sample) => new EmbodiedPose(sample.Head.position,
            (float)(sample.RootYawRadians * 180d / Math.PI), HeadOrientation.PitchDegrees(sample.RootPlacement.rotation));

        private void PublishCheckedAnchor(uint anchor, EmbodiedPose pose, double actualYaw)
        {
            if (!_targetConsumer.TryProposePublication(anchor, out EmbodiedTargetConsumer.Stamp stamp)) return;
            if (_targetCore.TryPublishAnchorPose(_actor, anchor, pose, actualYaw * 180d / Math.PI,
                    Time.timeAsDouble, stamp.Sequence, stamp.Epoch, stamp.Teleported, out double nativeYaw))
                _targetConsumer.AcceptPublication(anchor, stamp, actualYaw, nativeYaw);
        }

        private void DrainTargetEvents()
        {
            _currentMotionPlan = null;
            _currentDrainRootPositionEventReceived = false;
            if (_targetClosed) return;
            if (_targetDraining || _targetApplying || _targetPublishing)
            {
                _targetConsumer.Fail("Reentrant polling is not a solve-complete drain."); return;
            }
            _targetDraining = true;
            try
            {
                // No snapshot gate, budget, synchronous-solve callback, or bool-empty inference.
                while (true)
                {
                    ActorEventPollResult result = _targetCore.PollEvent(_actor, out ActorEvent actorEvent,
                        out ActorEventPollFailure failure);
                    if (result == ActorEventPollResult.NativeEmpty)
                    {
                        _targetConsumer.NativeEmpty();
                        return;
                    }
                    if (result != ActorEventPollResult.Event)
                    {
                        _targetConsumer.Fail("Incomplete typed drain: " + result, failure);
                        return;
                    }
                    if (actorEvent.Kind == ActorEventKind.PositionRequest &&
                        actorEvent.AnchorId == EmbodiedDriver.RootAnchorId)
                    {
                        _currentDrainRootPositionEventReceived = true;
                    }
                    bool supported = actorEvent.Kind == ActorEventKind.SourceException || SupportedTarget(actorEvent.AnchorId);
                    if (!supported) ReportUnsupportedTypedAnchor(actorEvent.AnchorId);
                    _targetConsumer.Receive(actorEvent, supported);
                }
            }
            finally { _targetDraining = false; }
        }

        private EmbodiedFramePlan PlanTargetFrame(float dt)
        {
            var plan = new EmbodiedFramePlan();
            _currentMotionPlan = plan;
            bool rootPositionEventReceived = _currentDrainRootPositionEventReceived;
            if (!ReadChecked(out Sample sample) || _targetFault != null ||
                !EmbodiedTargetConsumer.Finite(dt) || dt < 0f)
            {
                ObserveCheckedRootPath(rootPositionEventReceived, _rootGoal != null && _rootGoal.HasPosition,
                    false, false);
                return plan;
            }
            plan.TargetEvents = new EmbodiedTargetFrame(_registrationToken, sample, dt, _targetConsumer.Pending(),
                _rootGoal.HasPosition || _rootGoal.HasYaw || _headGoal.HasPosition || _headGoal.HasYaw || HeadChartActive,
                rootPositionEventReceived);
            if (!plan.TargetEvents.HasWork)
            {
                // An idle/empty drain has no Apply call in the production manager, but
                // must still establish (and later compare) the observable diagnostic state.
                ObserveCheckedRootPath(rootPositionEventReceived, _rootGoal.HasPosition, false, false);
            }
            return plan;
        }

        private bool Committed(Result result, string operation)
        {
            if (result == Result.Committed) return true;
            // A refused write never subtracts a requested step or acknowledges an
            // event. Even a partial multi-channel application keeps its exact history.
            TargetFault(operation + " refused: " + result);
            return false;
        }

        private void ApplyTargetFrame(EmbodiedFramePlan plan)
        {
            EmbodiedTargetFrame frame = plan.TargetEvents;
            // Single-use even if an event/write is refused. A later plan can only use
            // actual readback and the still-unacknowledged history, never a replay token.
            _currentMotionPlan = null;
            // The frame's time counts for the goals' measure of their targets' motion even where it is not stepped (a refused or
            // stale frame, a refused write): ElapseUnstepped in every exit.
            double rootClock = _rootGoal?.Clock ?? 0d, headClock = _headGoal?.Clock ?? 0d;
            if (frame == null || _targetClosed || _targetApplying || _targetFault != null ||
                !ReferenceEquals(frame.Owner, _registrationToken) || !ReadChecked(out Sample before) ||
                frame.Incarnation != before.Incarnation || frame.RealizedRevision != before.Revision)
            {
                ObserveCheckedRootPath(frame != null && frame.RootPositionEventReceived,
                    _rootGoal != null && _rootGoal.HasPosition, false, false);
                if (frame != null && !_targetApplying) ElapseUnstepped(frame.Seconds, rootClock, headClock);
                return;
            }
            _targetApplying = true;
            _diagnosticApplyingTargetFrame = true;
            _diagnosticRootCommitSucceeded = false;
            _diagnosticRootTranslationObserved = false;
            try
            {
                for (int i = 0; i < frame.Operations.Count; ++i)
                {
                    EmbodiedTargetConsumer.Operation operation = frame.Operations[i];
                    if (!_targetConsumer.IsNext(operation)) return;
                    EmbodiedTargetGoal goal = operation.Event.AnchorId == 0 ? _rootGoal : _headGoal;
                    if (operation.Kind == EmbodiedTargetConsumer.OperationKind.Target)
                    {
                        ObserveTypedPitch(operation.Event);
                        goal.Set(operation);
                        // A fresh Facing yaw is relative to the current head: no re-base after a chart.
                        if (ReferenceEquals(goal, _headGoal) && (operation.Channels & TargetChannels.Yaw) != 0) _rebaseFacingAfterChart = false;
                    }
                    else if (operation.Kind == EmbodiedTargetConsumer.OperationKind.Release)
                        goal.Release(operation.Channels);
                    else if (operation.Kind == EmbodiedTargetConsumer.OperationKind.Placement)
                    {
                        // A native placement emits separate position/yaw channel
                        // events. Apply one absolute placement, retain/ack both exact
                        // envelopes, and never also apply the legacy teleport marker.
                        int end = i;
                        TargetChannels channels = operation.Channels;
                        Vector3 position = operation.Position;
                        double yaw = operation.YawRadians;
                        ActorEvent metadata = operation.Event;
                        while (end + 1 < frame.Operations.Count)
                        {
                            var next = frame.Operations[end + 1];
                            if (next.Kind != EmbodiedTargetConsumer.OperationKind.Placement ||
                                next.Event.AnchorId != operation.Event.AnchorId || next.Event.Revision != operation.Event.Revision) break;
                            channels |= next.Channels;
                            if ((next.Channels & TargetChannels.Position) != 0) position = next.Position;
                            if ((next.Channels & TargetChannels.Yaw) != 0) { yaw = next.YawRadians; metadata = next.Event; }
                            ++end;
                        }
                        if (!ApplyCheckedPlacement(operation.Event.AnchorId, channels, position, yaw, metadata, out Sample placed)) return;
                        double actualYaw = operation.Event.AnchorId == 0 ? placed.RootYawRadians : placed.HeadYawRadians;
                        goal.Set(new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target,
                            metadata, channels, position, actualYaw));
                        for (; i < end; ++i) _targetConsumer.Acknowledge(frame.Operations[i]);
                    }
                    // Lifecycle source-exception markers are history only: recorded and
                    // acknowledged in order, never a write (there is no app handler). The
                    // immutable typed placement events, not latest snapshots, do the write.
                    _targetConsumer.Acknowledge(frame.Operations[i]);
                }
                float remaining = frame.Seconds;
                // Only root/head goals, a typed pitch or the head chart are stepped: a frame with none of them (an Apply of an idle
                // plan) does not rewrite the head with its own carried pose (option A's step gate; on feat/affordance-pose-catchup
                // the same gate also excludes the bone goals).
                if (!(_rootGoal.HasPosition || _rootGoal.HasYaw || _headGoal.HasPosition || _headGoal.HasYaw ||
                      _targetPitchDegrees.HasValue || HeadChartActive)) remaining = 0f;
                // Subdivide elapsed time, not angular intent: each ordinary checked
                // witness is observable (< pi), without changing v1's speed/time budget.
                float quantum = (float)(90d / Config.MaxAngularSpeedDegreesPerSecond);
                while (remaining > 0f)
                {
                    float dt = Math.Min(remaining, quantum);
                    if (!StepCheckedGoals(dt)) return;
                    float next = remaining - dt;
                    if (next == remaining) { TargetFault("Elapsed time cannot be subdivided representably."); return; }
                    remaining = next;
                }
                // Exactly one feedback attempt per supported anchor, after the whole
                // ordered placement batch and ordinary movement, not per placement.
                PublishCheckedPose();
            }
            finally
            {
                ElapseUnstepped(frame.Seconds, rootClock, headClock);
                ObserveCheckedRootPath(frame.RootPositionEventReceived, _rootGoal.HasPosition,
                    _diagnosticRootCommitSucceeded, _diagnosticRootTranslationObserved);
                _diagnosticApplyingTargetFrame = false;
                _targetApplying = false;
            }
        }

        private void ElapseUnstepped(float seconds, double rootClock, double headClock)
        {
            if (!(seconds > 0f)) return;
            if (_rootGoal != null) _rootGoal.Elapse((float)(seconds - (_rootGoal.Clock - rootClock)));
            if (_headGoal != null) _headGoal.Elapse((float)(seconds - (_headGoal.Clock - headClock)));
        }

        // headHeight: the placement names the head's height too (the arrival at the v1 spawn height). A core placement
        // request carries no head height, so it keeps the observed one.
        private bool ApplyCheckedPlacement(uint anchor, TargetChannels channels, Vector3 position, double yaw,
                                           ActorEvent metadata, out Sample sample, bool headHeight = false)
        {
            sample = default;
            if (!ReadChecked(out Sample before)) return false;
            ObserveTypedPitch(metadata);
            if (anchor == 0)
            {
                Quaternion rotation = PlacementRotation(before.RootPlacement.rotation, channels, yaw);
                Quaternion delta = rotation * Quaternion.Inverse(before.RootPlacement.rotation);
                Vector3 desiredHead = (channels & TargetChannels.Position) != 0
                    ? new Vector3(position.x, headHeight ? position.y : before.Head.position.y, position.z) : before.Head.position;
                Vector3 rootPosition = desiredHead - delta * (before.Head.position - before.RootPlacement.position);
                if (!Committed(_checkedLease.TryPlaceRoot(rootPosition, rotation), "Absolute root placement")) return false;
                RecordCheckedRootCommit();
                _targetConsumer.Placed(0);
                if (!ReadChecked(out Sample rootPlaced)) return false;
                RecordCheckedRootTranslation(before, rootPlaced);
                // A full-authority world head would otherwise pin the root's sample
                // origin. Carry only channels without a separate head goal, using the
                // actual completed root move, not the commanded pose.
                Vector3 carried = Carry(before, rootPlaced, before.Head.position);
                Vector3 headPosition = _headGoal.HasPosition ? rootPlaced.Head.position : carried;
                double headYaw = _headGoal.HasYaw ? before.HeadYawRadians
                    : before.HeadYawRadians + (rootPlaced.RootYawRadians - before.RootYawRadians);
                if (!Committed(_checkedLease.TryPlaceHead(headPosition, headYaw), "Root placement head carry")) return false;
                _checkedHeadTargetEstablished = true;
                if (_targetHeadId.HasValue) _targetConsumer.Placed(_targetHeadId.Value);
                // The root placement placed the head too: both start from rest (EmbodiedTargetGoal.ResetPace).
                _rootGoal.ResetPace(); _headGoal.ResetPace(); ResetHeadChartPace();
            }
            else
            {
                Vector3 headPosition = (channels & TargetChannels.Position) != 0 ? position : before.Head.position;
                double headYaw = (channels & TargetChannels.Yaw) != 0 ? yaw : before.HeadYawRadians;
                if (!Committed(_checkedLease.TryPlaceHead(headPosition, headYaw), "Absolute direct-head placement")) return false;
                _checkedHeadTargetEstablished = true;
                _targetConsumer.Placed(anchor);
                _headGoal.ResetPace(); ResetHeadChartPace();
            }
            return ReadChecked(out sample);
        }

        private void ObserveTypedPitch(ActorEvent metadata)
        {
            if (metadata.Kind == ActorEventKind.TeleportRequest)
            {
                if (metadata.HasPitch) _targetPitchDegrees = metadata.PitchDegrees;
            }
            else if (metadata.TargetRequest.OrientationType >= (int)SbOrientationType.YawPitch)
                _targetPitchDegrees = HeadOrientation.PitchDegreesFromRadians(metadata.TargetRequest.TargetPitchRadians);
            // As in v1, the direct headset input carries source pitch at once, with
            // independent target yaw. Roll remains the established baseline's; the
            // detached wire metadata is not a newly claimed roll-realization port.
        }

        private Quaternion PlacementRotation(Quaternion current, TargetChannels channels, double yaw)
        {
            Vector3 euler = current.eulerAngles;
            if ((channels & TargetChannels.Yaw) != 0) euler.y = ProjectYawDegrees(yaw);
            if (_targetPitchDegrees.HasValue) euler.x = _targetPitchDegrees.Value;
            return Quaternion.Euler(euler);
        }

        // Only the float Transform projection is periodic. Keep the lease, goals
        // and publication correspondence in their unwrapped double domain.
        private static float ProjectYawDegrees(double yaw)
        {
            double tau = 2d * Math.PI;
            double wrapped = yaw - tau * Math.Floor(yaw / tau);
            return (float)(wrapped * 180d / Math.PI);
        }

        private static Vector3 Carry(Sample before, Sample after, Vector3 point)
        {
            Quaternion delta = after.RootPlacement.rotation * Quaternion.Inverse(before.RootPlacement.rotation);
            return after.RootPlacement.position + delta * (point - before.RootPlacement.position);
        }

        private bool StepCheckedGoals(float dt)
        {
            if (!ReadChecked(out Sample before)) return false;
            if (!_checkedHeadTargetEstablished && (_headGoal.HasPosition || _headGoal.HasYaw))
            {
                // The first root move must not spend an extra head-yaw increment
                // before an independent Facing goal gets its own speed budget.
                if (!Committed(_checkedLease.TryMoveHead(before.Head.position, 0d), "Initial direct-head channel ownership")) return false;
                _checkedHeadTargetEstablished = true;
            }
            Vector3 rootSample = before.Head.position;
            // Typed root positions are realized as v1's direct MoveTowards target.
            // The legacy receiver did not re-query a Unity/native course after it
            // received a target. StepPosition still returns the direct motion heading
            // so an otherwise-unspecified body yaw follows movement, while an explicit
            // root yaw and the independent head Facing channel retain their own goals.
            Vector3 desired = _rootGoal.StepPosition(rootSample, dt, true, null, out double? movementYaw);
            double step = _rootGoal.StepYaw(before.RootYawRadians, dt, movementYaw);
            if (_rootGoal.HasPosition || _rootGoal.HasYaw || _targetPitchDegrees.HasValue)
            {
                Vector3 euler = before.RootPlacement.rotation.eulerAngles;
                if (_targetPitchDegrees.HasValue) euler.x = _targetPitchDegrees.Value;
                euler.y = ProjectYawDegrees(before.RootYawRadians + step);
                Quaternion rotation = Quaternion.Euler(euler);
                Quaternion delta = rotation * Quaternion.Inverse(before.RootPlacement.rotation);
                Vector3 origin = desired - delta * (before.Head.position - before.RootPlacement.position);
                if (!Committed(_checkedLease.TryMoveRoot(origin, rotation, step), "Ordinary root move")) return false;
                RecordCheckedRootCommit();
            }
            if (!ReadChecked(out Sample afterRoot)) return false;
            RecordCheckedRootTranslation(before, afterRoot);
            Vector3 carried = Carry(before, afterRoot, before.Head.position);
            Vector3 headPosition = _headGoal.HasPosition
                ? _headGoal.StepPosition(afterRoot.Head.position, dt, false, null, out _)
                : carried;
            // Inside a transfer leaf the head's yaw is its chart's; Facing's goal is suppressed there (OvrAvatarEmbodiedDriver.HeadChart.cs).
            double headStep = TryStepHeadChartYaw(headPosition, afterRoot.HeadYawRadians, dt, out double chartStep) ? chartStep
                : _headGoal.HasYaw ? _headGoal.StepYaw(afterRoot.HeadYawRadians, dt)
                : before.HeadYawRadians + (afterRoot.RootYawRadians - before.RootYawRadians) - afterRoot.HeadYawRadians;
            // MoveHead sets the final world input at full authority; no absent position
            // event becomes a stale sampled-world position command. Pitch/roll stay
            // in the established retarget baseline composition.
            if (!Committed(_checkedLease.TryMoveHead(headPosition, headStep), "Ordinary direct-head/carry move")) return false;
            _checkedHeadTargetEstablished = true;
            return true;
        }

        private void PlaceCheckedBody(Vector3 position, Quaternion rotation)
        {
            _currentMotionPlan = null;
            if (_targetClosed || _targetApplying || _targetFault != null) return;
            var metadata = new ActorEvent { Kind = ActorEventKind.TeleportRequest, HasPitch = true,
                PitchDegrees = HeadOrientation.PitchDegrees(rotation) };
            double yaw = rotation.eulerAngles.y * Math.PI / 180d;
            // The body is placed with its head at the given pose, height included (as the legacy SetHeadTransform did):
            // the arrival's v1 spawn height, as before.
            if (!ApplyCheckedPlacement(0, TargetChannels.Position | TargetChannels.Yaw, position, yaw, metadata, out Sample sample,
                    headHeight: true)) return;
            _rootGoal.Set(new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target,
                metadata, TargetChannels.Position | TargetChannels.Yaw, sample.Head.position, sample.RootYawRadians));
            // Publication is deferred to the normal successful-drain boundary. This
            // entry point never polls synchronously or discards pre-placement history.
        }

        private void ResetCheckedEpochs()
        {
            _currentMotionPlan = null;
            if (!ReadChecked(out _)) return;
            _targetConsumer.Placed(0);
            if (_targetHeadId.HasValue) _targetConsumer.Placed(_targetHeadId.Value);
            // A new epoch is a placement: the goals start from rest (their targets and yaw are kept); the head chart's pace too.
            _rootGoal.ResetPace(); _headGoal.ResetPace(); ResetHeadChartPace();
            // An epoch change does not rebase continuous yaw or discard correspondences.
        }

        private void CloseTargetEvents()
        {
            if (!TargetEventsEnabled || _targetClosed) return;
            _targetClosed = true;
            _currentMotionPlan = null;
            try
            {
                if (!_targetCore.TrySetTargetEventDelivery(_actor, false, out string error))
                    TargetFault("Native target-delivery teardown refused: " + error);
            }
            catch (Exception exception)
            {
                TargetFault("Native target-delivery teardown failed: " + exception.Message);
            }
            finally
            {
                _checkedLease.Dispose();
                if (_suspendedBridge != null) _suspendedBridge.EndCheckedSuspension(this, false);
                _targetConsumer.Close();
            }
        }
    }
}
