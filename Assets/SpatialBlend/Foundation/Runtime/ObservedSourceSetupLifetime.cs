// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// World-owned C1 capability. It is deliberately not IDisposable: callers
    /// cannot accidentally turn an admitted native setup into a claimed rollback.
    /// The local world performs explicit truthful retire or release transitions.
    /// </summary>
    internal sealed class ObservedSourceSetupLease
    {
        internal ObservedSourceSetupLease(SpatialBlendLocalWorld world, int generation,
            long setup, LocalActorRegistration[] actors,
            ObservedSourceAnchorPoint[] points,
            ObservedSourceAnchorPointDeclaration[] declarations,
            SpatialBlendAPI.ObservedSourceSetupReceipt nativeReceipt)
        {
            World = world; Generation = generation; Setup = setup; Actors = actors;
            Points = points; Declarations = declarations; NativeReceipt = nativeReceipt;
        }

        internal SpatialBlendLocalWorld World { get; }
        internal int Generation { get; }
        internal long Setup { get; }
        internal IReadOnlyList<LocalActorRegistration> Actors { get; }
        internal IReadOnlyList<ObservedSourceAnchorPoint> Points { get; }
        internal IReadOnlyList<ObservedSourceAnchorPointDeclaration> Declarations { get; }
        // This contains only native setup outputs (actor epoch and point lease).
        // It is not a C1 batch receipt and exposes no readback state.
        internal SpatialBlendAPI.ObservedSourceSetupReceipt NativeReceipt { get; }
        internal bool Retired { get; set; }
        internal bool IsCurrent { get; set; } = true;
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly Dictionary<LocalActorRegistration, ObservedSourceSetupLease>
            _observedSourceSetupsByActor =
                new Dictionary<LocalActorRegistration, ObservedSourceSetupLease>();

        /// <summary>
        /// Opens one fresh C1 actor/point setup after framework-only preflight.
        /// This is a mode transition, not a frame or point publication path.
        /// </summary>
        internal bool TryBeginObservedSourceSetup(
            IReadOnlyList<ObservedSourceAnchorPoint> points,
            out ObservedSourceSetupLease lease, out string error)
        {
            lease = null; error = null;
            if (!OnObservedSourceSetupOwnerThread() || !IsInitialized || points == null ||
                points.Count == 0 || points.Count > 64)
                return Fail("C1 source setup requires 1..64 current points on its local-world owner thread.", out error);

            var declarations = new ObservedSourceAnchorPointDeclaration[points.Count];
            var actorOrder = new List<LocalActorRegistration>();
            var roots = new List<string>();
            var expectedPriorEpochs = new List<ulong>();
            var seenPoints = new HashSet<LocalObjectRegistration>();
            var seenActors = new HashSet<LocalActorRegistration>();
            for (int index = 0; index < points.Count; ++index)
            {
                ObservedSourceAnchorPoint point = points[index];
                if (point == null || !point.TryGetCurrentDeclaration(out var declaration, out error) ||
                    declaration == null || declaration.Point.World != this ||
                    declaration.Actor.World != this || !seenPoints.Add(declaration.Point))
                    return Fail(error ?? "C1 source setup points must be distinct and current in one local world.", out error);
                declarations[index] = declaration;
                if (!seenActors.Add(declaration.Actor))
                    return Fail("C1 source setup requires distinct source actors and points.", out error);
                if (_observedSourceSetupsByActor.ContainsKey(declaration.Actor) ||
                    !declaration.Actor.Owner.TryGetFreshObservedSourceSetupParticipant(
                        out LocalActorRegistration actor, out string root,
                        out ulong expectedPriorEpoch, out error) ||
                    !ReferenceEquals(actor, declaration.Actor))
                    return Fail(error ?? "A C1 source actor is already owned or not fresh.", out error);
                actorOrder.Add(actor); roots.Add(root); expectedPriorEpochs.Add(expectedPriorEpoch);
            }

            // The declaration preflight establishes these facts before any native
            // epoch/point change. Recheck every captured component immediately
            // before the one native request; no Unity callback runs in between.
            for (int index = 0; index < points.Count; ++index)
                if (!points[index].TryRevalidateDeclaration(declarations[index], out error))
                    return Fail(error ?? "A C1 source setup declaration became stale.", out error);
            long nativeSpace = actorOrder[0].NativeSpace;
            for (int index = 0; index < actorOrder.Count; ++index)
                if (!actorOrder[index].IsLive || actorOrder[index].Generation != _generation ||
                    actorOrder[index].NativeSpace != nativeSpace)
                    return Fail("C1 source actors must remain live in one native space.", out error);

            var participants = new List<SpatialBlendAPI.ObservedSourceSetupParticipantInput>();
            for (int index = 0; index < actorOrder.Count; ++index)
            {
                LocalActorRegistration actor = actorOrder[index];
                participants.Add(new SpatialBlendAPI.ObservedSourceSetupParticipantInput(actor.NativeActor,
                    roots[index], expectedPriorEpochs[index]));
            }
            var nativePoints = new List<SpatialBlendAPI.ObservedSourceSetupPointInput>();
            foreach (ObservedSourceAnchorPointDeclaration declaration in declarations)
                nativePoints.Add(new SpatialBlendAPI.ObservedSourceSetupPointInput(
                    declaration.Point.NativeElement, declaration.Actor.NativeActor,
                    declaration.AnchorId, declaration.MaximumAgeNanoseconds));

            if (!TryEnableObservedSourceSetupFrames(out error) ||
                !SpatialBlendAPI.TryBeginObservedSourceSetup(nativeSpace, participants, nativePoints,
                    out var nativeReceipt, out error))
                return Fail(error ?? "Native C1 source setup was rejected.", out error);

            var candidate = new ObservedSourceSetupLease(this, _generation, nativeReceipt.Setup,
                actorOrder.ToArray(), CopyPoints(points), declarations, nativeReceipt);
            var entered = new List<LocalActorRegistration>();
            var legacyRetirements = new List<Action>();
            for (int index = 0; index < actorOrder.Count; ++index)
            {
                LocalActorRegistration actor = actorOrder[index];
                if (!actor.Owner.TryEnterObservedSourceSetup(actor, nativeReceipt.Setup,
                    expectedPriorEpochs[index], out Action legacyRetirement, out error))
                {
                    foreach (LocalActorRegistration prior in entered)
                        prior.Owner.ExitObservedSourceSetup(prior, nativeReceipt.Setup);
                    // Nothing was admitted, so Core's truthful atomic retirement is
                    // the only permitted cleanup. Its release then makes the token inert.
                    SpatialBlendAPI.TryRetireObservedSourceSetup(nativeSpace, nativeReceipt.Setup, out _);
                    SpatialBlendAPI.TryReleaseObservedSourceSetup(nativeReceipt.Setup, out _);
                    return Fail(error ?? "C1 source setup owner transition was rejected.", out error);
                }
                entered.Add(actor);
                if (legacyRetirement != null) legacyRetirements.Add(legacyRetirement);
            }
            foreach (LocalActorRegistration actor in actorOrder)
                _observedSourceSetupsByActor.Add(actor, candidate);
            // Native C1 admission and every actor guard are established before
            // the legacy terminal callbacks run. A callback therefore cannot
            // race a stale legacy sink back into the newly selected C1 mode.
            foreach (Action legacyRetirement in legacyRetirements) legacyRetirement();
            lease = candidate;
            return true;
        }

        /// <summary>Truthfully cancels an untouched setup and then releases its token.</summary>
        internal bool TryRetireObservedSourceSetup(ObservedSourceSetupLease lease, out string error)
        {
            error = null;
            if (!IsCurrentObservedSourceSetup(lease))
                return Fail("C1 source setup lease is stale or foreign.", out error);
            if (!lease.Retired && !SpatialBlendAPI.TryRetireObservedSourceSetup(
                    lease.Actors[0].NativeSpace, lease.Setup, out error))
                return Fail(error ?? "Native C1 source setup cannot be retired after admission.", out error);
            lease.Retired = true;
            if (!SpatialBlendAPI.TryReleaseObservedSourceSetup(lease.Setup, out error))
                return Fail(error ?? "Retired C1 source setup token could not be released.", out error);
            ForgetObservedSourceSetup(lease);
            return true;
        }

        /// <summary>
        /// Deliberately drops an admitted/terminal token. It never claims to
        /// cancel Core state; a later legacy writer may begin its own epoch.
        /// </summary>
        internal bool TryReleaseObservedSourceSetup(ObservedSourceSetupLease lease, out string error)
        {
            error = null;
            if (!IsCurrentObservedSourceSetup(lease))
                return Fail("C1 source setup lease is stale or foreign.", out error);
            if (!SpatialBlendAPI.TryReleaseObservedSourceSetup(lease.Setup, out error))
                return Fail(error ?? "C1 source setup remains cancellable and cannot be released yet.", out error);
            ForgetObservedSourceSetup(lease);
            return true;
        }

        // A point registration is part of the native C1 capability, not merely
        // a later batch-validation input.  Before its owning object is removed
        // or a component rewrites that declaration, end the whole setup
        // truthfully. Core refuses a second live lease for the same point, so
        // at most one current setup can match this registration.
        internal bool TryTerminateObservedSourceSetupForPoint(
            LocalObjectRegistration point, out string error)
        {
            error = null;
            if (!OnObservedSourceSetupOwnerThread() || point == null || point.World != this)
                return Fail("C1 source-point teardown requires its current local-world owner thread.",
                    out error);
            ObservedSourceSetupLease matching = null;
            foreach (ObservedSourceSetupLease candidate in _observedSourceSetupsByActor.Values)
            {
                foreach (ObservedSourceAnchorPointDeclaration declaration in candidate.Declarations)
                    if (ReferenceEquals(declaration.Point, point))
                    {
                        matching = candidate;
                        break;
                    }
                if (matching != null) break;
            }
            if (matching == null) return true;
            if (TryRetireObservedSourceSetup(matching, out _)) return true;
            return TryReleaseObservedSourceSetup(matching, out error);
        }

        // C1 pre-solve ingress. This method never creates epochs or leases:
        // those belong exclusively to the setup lease. It converts the exact
        // staged Unity source snapshot and makes one native batch call.
        internal bool TryApplyObservedSourceSetupBatch(ObservedSourceSetupLease lease,
            IReadOnlyDictionary<string, AnchorPoseFrame> frames, out string error)
        {
            error = null;
            if (!IsCurrentObservedSourceSetup(lease) || frames == null ||
                frames.Count != lease.Actors.Count)
                return Fail("C1 batch requires one current staged frame for every setup actor.", out error);
            for (int index = 0; index < lease.Points.Count; ++index)
                if (lease.Points[index] == null || !lease.Points[index].TryRevalidateDeclaration(
                        lease.Declarations[index], out error))
                    return Fail(error ?? "A C1 source point declaration became stale before admission.", out error);

            var epochs = new Dictionary<long, ulong>();
            foreach (var actorResult in lease.NativeReceipt.Actors)
                if (epochs.ContainsKey(actorResult.Actor))
                    return Fail("C1 setup receipt contains duplicate native actors.", out error);
                else epochs.Add(actorResult.Actor, actorResult.Epoch);
            var nativeFrames = new List<SpatialBlendAPI.ObservedCollaboratorFrameInput>();
            foreach (LocalActorRegistration actor in lease.Actors)
            {
                if (!frames.TryGetValue(actor.ActorId, out AnchorPoseFrame frame) || frame == null ||
                    !epochs.TryGetValue(actor.NativeActor, out ulong epoch) ||
                    !TryBuildObservedCollaboratorFrame(actor, epoch, frame, out var nativeFrame,
                        out error)) return Fail(error ?? "C1 staged actor frame is invalid.", out error);
                nativeFrames.Add(nativeFrame);
            }
            if (!SpatialBlendAPI.TryReadRichMonotonicNanoseconds(out ulong now, out _, out _, out error) ||
                now > long.MaxValue) return Fail(error ?? "C1 native monotonic time is unavailable.", out error);
            var leases = new Dictionary<long, ulong>();
            foreach (var pointResult in lease.NativeReceipt.Points)
                if (leases.ContainsKey(pointResult.Element))
                    return Fail("C1 setup receipt contains duplicate native points.", out error);
                else leases.Add(pointResult.Element, pointResult.Lease);
            var publications = new List<SpatialBlendAPI.ObservedCollaboratorPublicationInput>();
            foreach (ObservedSourceAnchorPointDeclaration declaration in lease.Declarations)
            {
                if (!leases.TryGetValue(declaration.Point.NativeElement, out ulong pointLease) ||
                    declaration.MaximumAgeNanoseconds > long.MaxValue - (long)now)
                    return Fail("C1 source point lease or lifetime is invalid.", out error);
                publications.Add(new SpatialBlendAPI.ObservedCollaboratorPublicationInput(
                    declaration.Point.NativeElement, pointLease, (long)now,
                    (long)now + declaration.MaximumAgeNanoseconds));
            }
            return SpatialBlendAPI.TryApplyObservedCollaboratorBatch(
                lease.Actors[0].NativeSpace, nativeFrames, publications, out error) ||
                Fail(error ?? "Native coherent C1 batch was rejected.", out error);
        }

        private bool TryBuildObservedCollaboratorFrame(LocalActorRegistration actor, ulong epoch,
            AnchorPoseFrame frame, out SpatialBlendAPI.ObservedCollaboratorFrameInput nativeFrame,
            out string error)
        {
            nativeFrame = default; error = null;
            if (actor == null || !actor.IsLive || actor.World != this || epoch == 0 ||
                frame.anchors == null || frame.anchors.Length == 0)
            { error = "C1 actor registration or frame is invalid."; return false; }
            var observations = new SpatialBlendAPI.SbActorObservation[frame.anchors.Length];
            foreach (AnchorPoseSample sample in frame.anchors)
            {
                if (sample == null || !AnchorPoseFrameValidation.IsValidAnchorId(sample.anchorId) ||
                    float.IsNaN(sample.worldPosition.x) || float.IsInfinity(sample.worldPosition.x) ||
                    float.IsNaN(sample.worldPosition.y) || float.IsInfinity(sample.worldPosition.y) ||
                    float.IsNaN(sample.worldPosition.z) || float.IsInfinity(sample.worldPosition.z) ||
                    !actor.Owner.IsDeclaredSourceAnchorForC1(sample.anchorId))
                { error = "C1 actor frame has an invalid anchor sample."; return false; }
            }
            string root = actor.Owner.RootAnchorNameForC1();
            if (root == null) { error = "C1 actor has no current root anchor."; return false; }
            for (int index = 0; index < frame.anchors.Length; ++index)
            {
                AnchorPoseSample sample = frame.anchors[index];
                var position = sample.anchorId == root
                    ? new UnityEngine.Vector3(sample.worldPosition.x, 0f, sample.worldPosition.z)
                    : sample.worldPosition;
                CoordinateConverter.UnityToSpatialBlend3D(position, out double x, out double y, out double z);
                observations[index] = new SpatialBlendAPI.SbActorObservation
                {
                    anchor_name = sample.anchorId, position_x = x, position_y = y, position_z = z,
                    yaw = CoordinateConverter.UnityToSpatialBlendAngle(
                        sample.worldRotation.eulerAngles.y) * Math.PI / 180d
                };
            }
            nativeFrame = new SpatialBlendAPI.ObservedCollaboratorFrameInput(actor.NativeActor,
                epoch, frame.sequence, frame.timeSeconds, observations);
            return true;
        }

        private bool IsCurrentObservedSourceSetup(ObservedSourceSetupLease lease)
        {
            if (!OnObservedSourceSetupOwnerThread() || lease == null || !lease.IsCurrent ||
                !ReferenceEquals(lease.World, this) || lease.Generation != _generation ||
                lease.Actors.Count == 0) return false;
            foreach (LocalActorRegistration actor in lease.Actors)
                if (actor == null || !actor.IsLive ||
                    !_observedSourceSetupsByActor.TryGetValue(actor, out var current) ||
                    !ReferenceEquals(current, lease)) return false;
            return true;
        }

        private void ForgetObservedSourceSetup(ObservedSourceSetupLease lease)
        {
            if (lease == null || !lease.IsCurrent) return;
            foreach (LocalActorRegistration actor in lease.Actors)
            {
                if (_observedSourceSetupsByActor.TryGetValue(actor, out var current) &&
                    ReferenceEquals(current, lease)) _observedSourceSetupsByActor.Remove(actor);
                actor.Owner.ExitObservedSourceSetup(actor, lease.Setup);
            }
            lease.IsCurrent = false;
        }

        // Actor teardown is an external terminal transition for a C1 setup. Try
        // truthful cancellation first; if any evidence was admitted, release is
        // permitted but is never described as cancellation. A rare release
        // refusal is retried after the actor's native destruction made the token
        // terminal.
        private ObservedSourceSetupLease RetireObservedSourceSetupForActor(
            LocalActorRegistration actor)
        {
            if (actor == null || !_observedSourceSetupsByActor.TryGetValue(actor,
                    out ObservedSourceSetupLease lease) || !lease.IsCurrent) return null;
            if (TryRetireObservedSourceSetup(lease, out _)) return null;
            if (TryReleaseObservedSourceSetup(lease, out _)) return null;
            return lease;
        }

        private ObservedSourceSetupLease RetireObservedSourceSetupForPoint(
            LocalObjectRegistration point)
        {
            if (point == null) return null;
            ObservedSourceSetupLease matching = null;
            foreach (ObservedSourceSetupLease candidate in _observedSourceSetupsByActor.Values)
            {
                foreach (ObservedSourceAnchorPointDeclaration declaration in candidate.Declarations)
                    if (ReferenceEquals(declaration.Point, point))
                    {
                        matching = candidate;
                        break;
                    }
                if (matching != null) break;
            }
            if (matching == null) return null;
            if (TryRetireObservedSourceSetup(matching, out _)) return null;
            if (TryReleaseObservedSourceSetup(matching, out _)) return null;
            return matching;
        }

        private void ReleaseTerminalObservedSourceSetupAfterActorDestroy(
            ObservedSourceSetupLease lease)
        {
            if (lease == null || !lease.IsCurrent) return;
            if (SpatialBlendAPI.TryReleaseObservedSourceSetup(lease.Setup, out string error))
            {
                ForgetObservedSourceSetup(lease);
                return;
            }
            LastError = error ?? "A terminal C1 source setup token could not be released.";
        }

        private void ReleaseTerminalObservedSourceSetupAfterPointDestroy(
            ObservedSourceSetupLease lease)
        {
            if (lease == null || !lease.IsCurrent) return;
            if (SpatialBlendAPI.TryReleaseObservedSourceSetup(lease.Setup, out string error))
            {
                ForgetObservedSourceSetup(lease);
                return;
            }
            LastError = error ?? "A terminal C1 source setup token could not be released.";
        }

        private bool TryEnableObservedSourceSetupFrames(out string error)
        {
            error = null;
            if (!SpatialBlendAPI.TryEnsureActorObservationFrameAbi(out error) ||
                !SpatialBlendAPI.TryEnsureObservedSourceSetupAbi(out error)) return false;
            try
            {
                if (_actorObservationFramesEnabled) return true;
                if (!SpatialBlendAPI.EnableActorObservationFrames(_session))
                { error = "Native local observation-frame mode was rejected."; return false; }
                _actorObservationFramesEnabled = true;
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        private bool OnObservedSourceSetupOwnerThread() =>
            Thread.CurrentThread.ManagedThreadId == _mainThread;

        private static ObservedSourceAnchorPoint[] CopyPoints(
            IReadOnlyList<ObservedSourceAnchorPoint> points)
        {
            var copied = new ObservedSourceAnchorPoint[points.Count];
            for (int index = 0; index < copied.Length; ++index) copied[index] = points[index];
            return copied;
        }
    }
}
