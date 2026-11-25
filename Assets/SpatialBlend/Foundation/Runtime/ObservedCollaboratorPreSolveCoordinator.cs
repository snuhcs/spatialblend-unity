// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SpatialBlend
{
    /// <summary>
    /// Input-derived notification emitted only after the one native C1 call
    /// accepted. It is not a Core receipt and carries no readback, receiver,
    /// realization, transport, authority, or Freeform state.
    /// </summary>
    internal readonly struct AcceptedObservedCollaboratorAnchor
    {
        internal AcceptedObservedCollaboratorAnchor(AnchorPoseSample source)
        {
            AnchorId = source.anchorId;
            WorldPosition = source.worldPosition;
            WorldRotation = source.worldRotation;
        }

        internal string AnchorId { get; }
        internal UnityEngine.Vector3 WorldPosition { get; }
        internal UnityEngine.Quaternion WorldRotation { get; }
    }

    /// <summary>
    /// Immutable projection of a successful C1 input frame. It intentionally
    /// does not expose AnchorPoseFrame/AnchorPoseSample, whose public fields
    /// would let a notification consumer mutate a supposedly accepted value.
    /// </summary>
    internal sealed class AcceptedObservedCollaboratorFrame
    {
        internal AcceptedObservedCollaboratorFrame(AnchorPoseFrame source)
        {
            Schema = source.schema;
            CoordinateFrame = source.frame;
            Sequence = source.sequence;
            TimeSeconds = source.timeSeconds;
            var anchors = new AcceptedObservedCollaboratorAnchor[source.anchors.Length];
            for (int index = 0; index < anchors.Length; ++index)
                anchors[index] = new AcceptedObservedCollaboratorAnchor(source.anchors[index]);
            Anchors = new ReadOnlyCollection<AcceptedObservedCollaboratorAnchor>(anchors);
        }

        internal int Schema { get; }
        internal string CoordinateFrame { get; }
        internal long Sequence { get; }
        internal double TimeSeconds { get; }
        internal IReadOnlyList<AcceptedObservedCollaboratorAnchor> Anchors { get; }
    }

    internal sealed class AcceptedObservedCollaboratorBatch
    {
        internal AcceptedObservedCollaboratorBatch(ulong batchId,
            IReadOnlyDictionary<string, AnchorPoseFrame> frames)
        {
            BatchId = batchId;
            var copied = new Dictionary<string, AcceptedObservedCollaboratorFrame>(StringComparer.Ordinal);
            foreach (var item in frames)
                copied.Add(item.Key, new AcceptedObservedCollaboratorFrame(item.Value));
            Frames = new ReadOnlyDictionary<string, AcceptedObservedCollaboratorFrame>(copied);
        }

        internal ulong BatchId { get; }
        internal IReadOnlyDictionary<string, AcceptedObservedCollaboratorFrame> Frames { get; }
    }

    /// <summary>
    /// Framework-only C1 pre-solve ingress. It owns no actor epoch or source
    /// point lease: those belong to the supplied local-world setup lifetime.
    /// Every accepted batch takes the barrier's exact detached snapshots through
    /// one native batch request; there is no single-actor fallback path.
    /// </summary>
    internal sealed class ObservedCollaboratorPreSolveCoordinator : IDisposable
    {
        private readonly SpatialBlendLocalWorld _world;
        private readonly ObservedSourceSetupLease _setup;
        private readonly ObservedCollaboratorAdmissionBarrier _barrier;
        private ObservedCollaboratorStagedBatch _ready;
        private bool _disposed;

        internal ObservedCollaboratorPreSolveCoordinator(ObservedSourceSetupLease setup,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> participants)
        {
            if (setup == null || !setup.IsCurrent || setup.Actors.Count < 2 ||
                participants == null || participants.Count != setup.Actors.Count)
                throw new ArgumentException("C1 coordinator requires one current setup and every setup actor.");
            _world = setup.World;
            _setup = setup;
            var expectedActors = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocalActorRegistration actor in setup.Actors)
            {
                if (actor == null || !expectedActors.Add(actor.ActorId) ||
                    !participants.TryGetValue(actor.ActorId, out var anchors) || anchors == null)
                    throw new ArgumentException("C1 participant identities must exactly match setup actors.");
            }
            foreach (ObservedSourceAnchorPointDeclaration declaration in setup.Declarations)
            {
                if (!participants.TryGetValue(declaration.Actor.ActorId, out var anchors) ||
                    !ContainsAnchor(anchors, declaration.AnchorId))
                    throw new ArgumentException("Every declared C1 point anchor must be staged by its actor.");
            }
            _barrier = new ObservedCollaboratorAdmissionBarrier(participants);
        }

        internal ulong LastAcknowledgedBatchId => _barrier.LastAcknowledgedBatchId;
        internal bool HasPendingBatch => !_disposed && _barrier.HasPendingBatch;

        internal ObservedCollaboratorStageResult TryStage(ulong batchId, string actorId,
            AnchorPoseFrame frame, out string error)
        {
            error = null;
            if (_disposed) { error = "C1 coordinator is disposed."; return ObservedCollaboratorStageResult.Rejected; }
            ObservedCollaboratorStageResult result = _barrier.TryStage(batchId, actorId, frame,
                out ObservedCollaboratorStagedBatch ready, out error);
            if (result == ObservedCollaboratorStageResult.Ready) _ready = ready;
            return result;
        }

        internal bool TryCommitReady(out AcceptedObservedCollaboratorBatch accepted, out string error)
        {
            accepted = null; error = null;
            if (_disposed || _ready == null)
            { error = "No ready C1 batch is available for native admission."; return false; }
            ObservedCollaboratorStagedBatch batch = _ready;
            IReadOnlyDictionary<string, AnchorPoseFrame> frames = batch.CopyFramesForNative();
            if (!_world.TryApplyObservedSourceSetupBatch(_setup, frames, out error))
            {
                _barrier.TryAbort(batch.BatchId, out _);
                _ready = null;
                return false;
            }
            if (!_barrier.TryAcknowledgeNativeAdmission(batch, out error))
            {
                // Native success is already committed, so never report a false
                // rollback. This path only withholds the managed notification.
                _ready = null;
                return false;
            }
            _ready = null;
            accepted = new AcceptedObservedCollaboratorBatch(batch.BatchId, frames);
            return true;
        }

        internal bool TryAbort(ulong batchId, out string error)
        {
            error = null;
            if (_disposed) { error = "C1 coordinator is disposed."; return false; }
            bool aborted = _barrier.TryAbort(batchId, out error);
            if (aborted) _ready = null;
            return aborted;
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_ready != null) _barrier.TryAbort(_ready.BatchId, out _);
            _ready = null;
            _barrier.Dispose();
            _disposed = true;
        }

        private static bool ContainsAnchor(IReadOnlyCollection<string> anchors, string value)
        {
            foreach (string anchor in anchors)
                if (string.Equals(anchor, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
