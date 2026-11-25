// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;
using UnityEngine.Serialization;

namespace SpatialBlend
{
    public enum SpatialBlendActorAnchorRole { Root, Body }
    public enum SpatialBlendActorAnchorPositionDimension
    {
        RoleDefault = 0,
        Horizontal2D = 2,
        Spatial3D = 3
    }

    [Serializable]
    public sealed class SpatialBlendActorAnchor
    {
        [SerializeField] private string _name;
        [SerializeField] private SpatialBlendActorAnchorRole _role;
        [SerializeField] private Vector3 _localOffset;
        [SerializeField] private string _parentName;
        [FormerlySerializedAs("_scope")] [SerializeField] private SpatialBlendObject _support;
        [SerializeField] private SpatialBlendActorAnchorPositionDimension _positionDimension =
            SpatialBlendActorAnchorPositionDimension.RoleDefault;
        public string Name => _name; public SpatialBlendActorAnchorRole Role => _role;
        public Vector3 LocalOffset => _localOffset; public string ParentName => _parentName;
        /// <summary>The authored support this anchor is explicitly mapped through (a support correspondence). It is not a relation scope and is never passed to a bind.</summary>
        public SpatialBlendObject Support => _support;
        public SpatialBlendActorAnchorPositionDimension PositionDimension => _positionDimension;
        public SpatialBlendActorAnchor() { }
        public SpatialBlendActorAnchor(string name, SpatialBlendActorAnchorRole role, Vector3 offset,
                                    string parent = null, SpatialBlendObject support = null,
                                    SpatialBlendActorAnchorPositionDimension positionDimension =
                                        SpatialBlendActorAnchorPositionDimension.RoleDefault)
        {
            _name = name;
            _role = role;
            _localOffset = offset;
            _parentName = parent;
            _support = support;
            _positionDimension = positionDimension;
        }

        internal bool TryResolvePositionDimension(out int dimension)
        {
            dimension = 0;
            SpatialBlendActorAnchorPositionDimension resolved = _positionDimension;
            if (resolved == SpatialBlendActorAnchorPositionDimension.RoleDefault)
            {
                resolved = _role == SpatialBlendActorAnchorRole.Root
                    ? SpatialBlendActorAnchorPositionDimension.Horizontal2D
                    : SpatialBlendActorAnchorPositionDimension.Spatial3D;
            }
            if (resolved != SpatialBlendActorAnchorPositionDimension.Horizontal2D &&
                resolved != SpatialBlendActorAnchorPositionDimension.Spatial3D)
            {
                return false;
            }
            if (_role == SpatialBlendActorAnchorRole.Root &&
                resolved != SpatialBlendActorAnchorPositionDimension.Horizontal2D)
            {
                return false;
            }
            dimension = (int)resolved;
            return true;
        }
    }
    public readonly struct SpatialBlendRootTarget
    {
        public SpatialBlendRootTarget(Vector3 p, Quaternion r, double arrival, double delivered)
        { Position = p; Rotation = r; ArrivalTime = arrival; DeliveredAt = delivered; }
        public Vector3 Position { get; } public Quaternion Rotation { get; }
        public double ArrivalTime { get; } public double DeliveredAt { get; }
    }
    public sealed partial class SpatialBlendActor : MonoBehaviour
    {
        private sealed class Sink : IAnchorPoseFrameCheckedReplaySink, IAnchorPoseFrameRetiredReplaySink
        {
            private readonly SpatialBlendActor _owner; private readonly LocalActorRegistration _registration;
            private readonly bool _motion;
            private readonly long _motionLease;
            internal Sink(SpatialBlendActor owner, LocalActorRegistration registration, bool motion,
                          long motionLease = 0)
            { _owner = owner; _registration = registration; _motion = motion; _motionLease = motionLease; }
            public bool TryApplyWorldFrame(AnchorPoseFrame frame) => _motion
                ? _owner.ApplyMotion(_registration, _motionLease, frame) : _owner.Apply(_registration, frame);
            public void ClearWorldAnchor(string anchor)
            {
                if (_motion) _owner.ClearMotion(_registration, _motionLease, anchor);
                else _owner.ClearLegacy(_registration, anchor);
            }
            public void ResetWorldAnchorReplay()
            {
                if (_motion) _owner.ResetMotion(_registration, _motionLease);
                else _owner.ResetCaptured(_registration);
            }
            public bool TryResetWorldAnchorReplay()
            {
                if (!_motion) return _owner.TryResetCaptured(_registration);
                return _owner.TryResetMotion(_registration, _motionLease);
            }
            public bool IsReplaySinkRetired => _motion && _owner.IsMotionSinkRetired(_registration, _motionLease);
        }
        [SerializeField] private SpatialBlendObject _owner;
        [SerializeField] private string _actorId;
        [SerializeField] private bool _isSource = true;
        [SerializeField] private SpatialBlendActorAnchor[] _anchors = Array.Empty<SpatialBlendActorAnchor>();
        [SerializeField] private AnchorTargetController _bodyTargets;
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, SpatialBlendActorAnchorRole> _roles = new Dictionary<string, SpatialBlendActorAnchorRole>(StringComparer.Ordinal);
        private readonly HashSet<string> _observed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _ownedTargets = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnchorTargetController.TargetLease> _bodyLeases =
            new Dictionary<string, AnchorTargetController.TargetLease>(StringComparer.Ordinal);
        private LocalActorRegistration _registration;
        private long _lastSequence = -1; private double _lastTime = double.NegativeInfinity;
        private ulong _motionEpoch; private long _motionSinkLease;
        // A C1 setup token is owned by SpatialBlendLocalWorld, never by an
        // arbitrary source sink.  While nonzero it fences every legacy ingress
        // entry point so one old writer cannot silently supersede the native
        // setup-owned epoch.
        private long _observedSourceSetupToken;
        private bool _applying; private bool _releasingOutputs; private long _outputRevision;
        private string _cueStreamId;
        private int _mainThread;
        public event Action<SpatialBlendRootTarget> RootTargetAvailable;
        public event Action RootTargetsCleared;
        /// <summary>
        /// Receives immutable-by-delivery post-realizer cue snapshots. Observers get
        /// independent copies and cannot alter legacy realization or one another.
        /// </summary>
        public event Action<SpatialControlCueFrame> SpatialControlCueFrameAvailable;
        /// <summary>Explicit rich-boundary observation only; never drives body targets.</summary>
        public event Action<RichRealizationMailboxEnvelope> RichRealizationAvailable;
        public string ActorId => _actorId;
        public bool IsRegistered => Current(_registration);
        internal LocalActorRegistration Registration => _registration;
        internal bool IsOwnerThread => Main();
        // Declaration-only C1 preflight. It neither begins an epoch nor enters
        // legacy source input or receiver realization paths.
        internal bool IsDeclaredSourceAnchorForC1(string name) => Main() && _isSource &&
            Current(_registration) && name != null && _known.Contains(name);
        internal string RootAnchorNameForC1() => Main() && _isSource && Current(_registration)
            ? RootAnchorName() : null;
        internal bool IsDeclaredSourceBodyAnchor(string name) => Main() && _isSource &&
            Current(_registration) && name != null && _roles.TryGetValue(name, out var role) &&
            role == SpatialBlendActorAnchorRole.Body;
        private void Awake() { _mainThread = Thread.CurrentThread.ManagedThreadId; }
        public void Configure(SpatialBlendObject owner, string id, bool source, SpatialBlendActorAnchor[] anchors, AnchorTargetController targets = null)
        {
            if (!Main()) throw new InvalidOperationException("Actor configuration requires main thread.");
            if (IsRegistered) throw new InvalidOperationException("Cannot reconfigure a live actor.");
            _owner = owner; _actorId = id; _isSource = source;
            if (anchors == null) _anchors = Array.Empty<SpatialBlendActorAnchor>();
            else
            {
                _anchors = new SpatialBlendActorAnchor[anchors.Length];
                for (int index = 0; index < anchors.Length; ++index)
                    _anchors[index] = anchors[index] == null ? null : new SpatialBlendActorAnchor(
                        anchors[index].Name, anchors[index].Role, anchors[index].LocalOffset,
                        anchors[index].ParentName, anchors[index].Support,
                        anchors[index].PositionDimension);
            }
            _bodyTargets = targets;
        }
        /// <summary>The actor's own element: the person leaf it was registered with. It is never a region and never moves.</summary>
        public SpatialBlendObject Element => _registration?.OwnerObject?.Owner ?? _owner;
        public bool TryRegister(out string error)
        {
            error = null;
            if (IsRegistered) return true;
            if (!Main() || _owner == null || !_owner.IsRegistered || string.IsNullOrWhiteSpace(_actorId) || _actorId.IndexOf('\0') >= 0)
            { error = "Actor needs a live owner, valid ID, and main thread."; return false; }
            _known.Clear(); _roles.Clear();
            int rootCount = 0;
            foreach (SpatialBlendActorAnchor anchor in _anchors)
            {
                if (anchor == null || !AnchorPoseFrameValidation.IsValidAnchorId(anchor.Name) || !_known.Add(anchor.Name) || !Finite(anchor.LocalOffset))
                { error = "Actor anchors require unique finite names."; return false; }
                if (anchor.Role != SpatialBlendActorAnchorRole.Root && anchor.Role != SpatialBlendActorAnchorRole.Body)
                { error = "Actor anchor role is invalid."; return false; }
                if (!anchor.TryResolvePositionDimension(out _))
                { error = "Root anchors are horizontal-only; anchor position dimension is invalid."; return false; }
                if (anchor.Role == SpatialBlendActorAnchorRole.Root) ++rootCount;
                _roles.Add(anchor.Name, anchor.Role);
            }
            if (_known.Count == 0 || rootCount > 1) { error = "Actor needs anchors and at most one root."; return false; }
            foreach (SpatialBlendActorAnchor anchor in _anchors)
                if (!string.IsNullOrEmpty(anchor.ParentName) && (!_known.Contains(anchor.ParentName) || anchor.ParentName == anchor.Name))
                { error = "Actor anchor parent is invalid."; return false; }
            foreach (SpatialBlendActorAnchor anchor in _anchors)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { anchor.Name };
                string parent = anchor.ParentName;
                while (!string.IsNullOrEmpty(parent))
                {
                    if (!seen.Add(parent)) { error = "Actor anchor parents form a cycle."; return false; }
                    foreach (SpatialBlendActorAnchor candidate in _anchors)
                        if (candidate.Name == parent) { parent = candidate.ParentName; break; }
                }
            }
            if (!TryAcquireBodyLeases(out Dictionary<string, AnchorTargetController.TargetLease> leases,
                                       out error)) return false;
            if (!_owner.World.TryRegisterActor(this, _actorId, _owner.Registration, _anchors, _isSource,
                                                out _registration, out error))
            {
                DisposeLeases(leases);
                return false;
            }
            foreach (var lease in leases) _bodyLeases.Add(lease.Key, lease.Value);
            _cueStreamId = Guid.NewGuid().ToString("N");
            _motionRegistrationId = Guid.NewGuid();
            _motionEpoch = 0;
            if (Reset(_registration)) return true;
            LocalActorRegistration failed = _registration;
            failed.World.UnregisterActor(failed);
            error = "Native body-anchor inactive setup failed.";
            return false;
        }
        public bool TryCreateAnchorPoseInput(out IAnchorPoseFrameSink sink)
        { sink = null; if (!Main() || !_isSource || !IsRegistered || _observedSourceSetupToken != 0) return false; sink = new Sink(this, _registration, false); return true; }
        public bool TryCreateMotionInput(out IAnchorPoseFrameSink sink)
        {
            sink = null;
            if (!Main() || _applying || !_isSource || !IsRegistered || _observedSourceSetupToken != 0 || !HasMotionCompatibleRoot()) return false;
            LocalActorRegistration expected = _registration;
            Action retired;
            _applying = true;
            try
            {
                if (!expected.World.TryBeginActorObservationEpoch(expected, RootAnchorName(),
                    out ulong epoch, out _) || !Current(expected)) return false;
                retired = DetachCommittedMotion(CommittedMotionRetirementReason.WriterReplaced);
                _motionEpoch = epoch;
                _lastSequence = -1; _lastTime = double.NegativeInfinity;
                _observed.Clear();
                sink = new Sink(this, expected, true, ++_motionSinkLease);
            }
            finally { _applying = false; }
            retired();
            return true;
        }
        /// <summary>
        /// Explicit target-only receiver input. Requires a zero-offset planar root;
        /// rig/calibration IDs identify caller versions, not calibrated body admission.
        /// The returned writer never substitutes source replay or generated transforms.
        /// </summary>
        public bool TryBeginReceiverInput(ulong rigRevision, ulong calibrationRevision,
            out ReceiverInputWriter writer, out string error)
        {
            writer = null; error = null;
            if (!Main() || !IsRegistered || _isSource || !HasMotionCompatibleRoot())
            {
                error = "Only a live main-thread receiver with an unparented zero-offset planar root may begin receiver input.";
                return false;
            }
            var names = new string[_anchors.Length];
            for (int i = 0; i < names.Length; ++i) names[i] = _anchors[i].Name;
            return _registration.World.TryBeginReceiverInput(_registration, RootAnchorName(), names,
                rigRevision, calibrationRevision, out writer, out error);
        }

        internal void EnterReceiverInputMode(LocalActorRegistration expected)
        {
            if (!Current(expected)) return;
            long revision = ++_outputRevision;
            Dictionary<string, AnchorTargetController.TargetLease> leases = DetachBodyLeases();
            List<AnchorTargetController.TargetLease> targets = DetachOwnedTargetLeases(leases);
            CancelAndDispose(targets, leases);
            if (!Current(expected) || revision != _outputRevision) return;
            InvokeCleared(expected, 0, revision, false, false);
            if (!Current(expected) || revision != _outputRevision) return;
            InvokeCue(expected, 0, revision, false, false, Array.Empty<RealizationTarget>());
        }

        public bool TryEnableRichRealization(RichRealizationSubscriptionOptions options, out string error)
            => TryEnableRichRealizationOwned(options, out _, out error);

        internal bool TryEnableRichRealizationOwned(RichRealizationSubscriptionOptions options,
            out ulong lifetime, out string error)
        {
            lifetime = 0; error = null;
            if (!Main() || !IsRegistered || _isSource)
            {
                error = "Only a live receiver actor may opt into rich realization.";
                return false;
            }
            return _registration.World.TryEnableRichRealization(_registration, options,
                DeliverRichRealization, out lifetime, out error);
        }

        public void DisableRichRealization()
        {
            if (Main() && _registration != null)
                _registration.World.DisableRichRealization(_registration);
        }

        /// <summary>
        /// Optionally copies the latest native snapshot into the existing rich
        /// admission. Polling never recalibrates or extends its native-age bound.
        /// </summary>
        public bool TryPollRichRealization(out string error)
        {
            error = null;
            if (!Main() || !IsRegistered || _isSource)
            {
                error = "Only a live receiver actor may poll rich realization.";
                return false;
            }
            return _registration.World.TryPollRichRealization(_registration, out error);
        }

        private void DeliverRichRealization(RichRealizationMailboxEnvelope envelope)
        {
            Action<RichRealizationMailboxEnvelope> handlers = RichRealizationAvailable;
            if (handlers == null) return;
            LocalActorRegistration deliveryRegistration = _registration;
            foreach (Action<RichRealizationMailboxEnvelope> handler in handlers.GetInvocationList())
            {
                // Reentrant disable/rebind/shutdown may retire this exact token
                // between multicast handlers. Do not pass an old Complete to a
                // later observer; terminal envelopes remain attributable to their
                // captured old token and are still fanned out.
                if (envelope.Reason == RichMailboxReason.NativeSnapshot &&
                    (deliveryRegistration == null ||
                     !deliveryRegistration.World.IsRichRealizationDeliveryCurrent(
                         deliveryRegistration, envelope.ProducerLifetime)))
                    break;
                try { handler(envelope); }
                catch { }
            }
        }
        internal bool TryApplyFrameForRegistration(LocalActorRegistration expected, AnchorPoseFrame frame) => Apply(expected, frame);
        private void ResetCaptured(LocalActorRegistration expected)
        { TryResetCaptured(expected); }
        private bool TryResetCaptured(LocalActorRegistration expected)
        {
            if (!Main() || _applying || !_isSource || _observedSourceSetupToken != 0 || !Current(expected)) return false;
            _applying = true;
            try
            {
                DetachCommittedMotion(CommittedMotionRetirementReason.LegacyMutation)();
                if (!Current(expected)) return false;
                foreach (string n in new List<string>(_observed))
                    if (!Clear(expected, n)) return false;
                return Reset(expected);
            }
            finally { _applying = false; }
        }
        // The identity of the poses this receiver's body publishes to the core (epoch, sequence, placement flag).
        private EmbodiedPoseClock _embodiedClock = new EmbodiedPoseClock();

        /// <summary>
        /// Receiver: publishes where this actor's body stands now (Unity space, yaw in degrees) to the core's
        /// root anchor's sampling port (anchor id 0). The core solves a receiver only from a published pose and never substitutes one, so a
        /// receiver's owner publishes at placement and whenever the body moves. Refused for a source, for an
        /// unregistered actor, and for a receiver-input actor, whose stamped frames are the only writer of its
        /// pose. <paramref name="placed"/> says the body was placed instead of moved: a new epoch.
        /// </summary>
        public bool TryPublishEmbodiedPose(Vector3 position, float yawDegrees, out string error, bool placed = false)
        {
            error = null;
            LocalActorRegistration registration = _registration;
            if (!Main() || _isSource || registration == null || !IsRegistered || registration.ReceiverInputTargetOnly ||
                !Finite(position) || float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees))
            {
                error = "Embodied pose publishing requires a finite registered receiver on the main thread.";
                return false;
            }
            if (placed) _embodiedClock.NewEpoch();
            if (!SpatialBlendAPI.TryPublishAnchorPose(registration.NativeSpace, registration.NativeActor, 0u,
                    new EmbodiedPose(position, yawDegrees), Time.unscaledTimeAsDouble, _embodiedClock.NextSequence(),
                    _embodiedClock.Epoch, _embodiedClock.ConsumeTeleportedFlag()))
            {
                error = "The core refused the embodied pose.";
                return false;
            }
            return true;
        }

        public bool TryUpdateRoot(Vector3 position, float yaw, out string error)
        {
            error = null;
            if (!Main() || _applying || !_isSource || _observedSourceSetupToken != 0 || !IsRegistered || !Finite(position) || float.IsNaN(yaw) || float.IsInfinity(yaw))
            { error = "Root update requires finite live source on main thread."; return false; }
            LocalActorRegistration expected = _registration;
            _applying = true;
            try
            {
                DetachCommittedMotion(CommittedMotionRetirementReason.LegacyMutation)();
                if (!Current(expected)) { error = "Source retired during legacy transition."; return false; }
                return expected.World.TryUpdateActorRoot(expected, position, yaw, out error);
            }
            finally { _applying = false; }
        }
        internal void ClearRegistration(LocalActorRegistration registration)
        {
            if (!ReferenceEquals(_registration, registration)) return;
            _embodiedClock = new EmbodiedPoseClock();
            Action retired = DetachCommittedMotion(CommittedMotionRetirementReason.RegistrationRetired);
            Dictionary<string, AnchorTargetController.TargetLease> retiredLeases = DetachBodyLeases();
            List<AnchorTargetController.TargetLease> retiredTargets = DetachOwnedTargetLeases(retiredLeases);
            _registration = null;
            _motionEpoch = 0;
            _observedSourceSetupToken = 0;
            ++_outputRevision;
            Reset(null);
            CancelAndDispose(retiredTargets, retiredLeases);
            InvokeCue(null, 0, _outputRevision, true, false, Array.Empty<RealizationTarget>());
            _cueStreamId = null;
            retired();
        }
        internal void ReleaseOwnedOutputs(LocalActorRegistration expected, bool detached)
        {
            if (_releasingOutputs) return;
            if (detached)
            {
                if (_registration != null) return;
            }
            else if (!ReferenceEquals(_registration, expected)) return;
            _releasingOutputs = true;
            try
            {
                long revision = ++_outputRevision;
                if (!CancelAll(revision)) return;
                // Source retirement has deliberately advanced the shared output
                // epoch before this forced clear; ordinary native empty snapshots
                // below retain their epoch fence.
                InvokeCleared(expected, 0, _outputRevision, detached, false);
                InvokeCue(expected, 0, _outputRevision, detached, false,
                          Array.Empty<RealizationTarget>());
            }
            finally { _releasingOutputs = false; }
        }
        internal void DeliverRealization(LocalActorRegistration expected, long epoch, IReadOnlyList<RealizationTarget> targets, double delivered)
        {
            if (!Main() || !Current(expected) || !expected.World.IsCurrentActorOutput(expected, epoch)) return;
            // The reduced callback has no receiver-input sidecar or admission
            // join. Opted receivers use diagnostic rich output only; even after
            // Clear/Close they cannot silently resume this legacy actuation path.
            if (expected.ReceiverInputTargetOnly) return;
            long revision = ++_outputRevision;
            var bodyNow = new HashSet<string>(StringComparer.Ordinal); bool root = false;
            foreach (RealizationTarget target in targets)
            {
                if (!CurrentOutput(expected, epoch, revision)) return;
                // The Spacing target of the root. Core 4.11 names it by the actor's own root anchor (an anonymous relation never aliases a named
                // root), so a localized root ("根") answers under that name; "root" stays the name of an actor whose root is the legacy one.
                string rootName = RootAnchorName();
                bool canonicalRoot = target.KernelType == SbKernelType.Spacing && rootName != null &&
                    (target.AnchorName == "root" || target.AnchorName == rootName);
                if (canonicalRoot)
                {
                    root = true;
                    InvokeRoot(expected, epoch, revision,
                        new SpatialBlendRootTarget(target.Position, target.Rotation, target.ArrivalTime, delivered));
                    if (!CurrentOutput(expected, epoch, revision)) return;
                }
                else if (_roles.TryGetValue(target.AnchorName, out SpatialBlendActorAnchorRole role) &&
                         role == SpatialBlendActorAnchorRole.Body &&
                         target.KernelType == SbKernelType.AffordancePose &&
                         _bodyLeases.TryGetValue(target.AnchorName, out AnchorTargetController.TargetLease lease))
                {
                    bodyNow.Add(target.AnchorName);
                    if (lease.TryApply(target.Position, target.Rotation.eulerAngles.y, target.HasHeight,
                                       target.Weight, delivered))
                    {
                        if (!CurrentOutput(expected, epoch, revision)) return;
                        _ownedTargets.Add(target.AnchorName);
                    }
                }
            }
            if (!CurrentOutput(expected, epoch, revision)) return;
            foreach (string old in new List<string>(_ownedTargets))
            {
                if (bodyNow.Contains(old)) continue;
                if (!CurrentOutput(expected, epoch, revision)) return;
                Cancel(old);
                if (!CurrentOutput(expected, epoch, revision)) return;
            }
            if (!root) InvokeCleared(expected, epoch, revision, false, true);
            // Cues observe the same native snapshot after legacy root/body delivery.
            // A malformed cue projection never changes that established path.
            InvokeCue(expected, epoch, revision, false, true, targets);
        }
        public void Unregister() { if (!Main()) return; LocalActorRegistration r = _registration; if (r != null) r.World.UnregisterActor(r); }
        private bool Apply(LocalActorRegistration expected, AnchorPoseFrame frame)
        {
            if (!Main() || !_isSource || !Current(expected) || _applying || _observedSourceSetupToken != 0 || !AnchorPoseFrameValidation.TryValidateFrame(frame, _lastSequence, _lastTime, _known, out _)) return false;
            AnchorPoseFrame copy = AnchorPoseFrameValidation.DeepCopy(frame); _applying = true;
            try
            {
                DetachCommittedMotion(CommittedMotionRetirementReason.LegacyMutation)();
                if (!Current(expected)) return false;
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (AnchorPoseSample s in copy.anchors)
                {
                    if (!Current(expected)) return false;
                    if (_roles[s.anchorId] != SpatialBlendActorAnchorRole.Body) continue;
                    present.Add(s.anchorId);
                    SpatialBlendAPI.SetAnchorTransform(expected.NativeSpace, expected.NativeActor, s.anchorId, s.worldPosition, s.worldRotation.eulerAngles.y);
                    if (!SpatialBlendAPI.SetAnchorActive(expected.NativeSpace, expected.NativeActor, s.anchorId, true)) return false;
                }
                foreach (string old in new List<string>(_observed))
                    if (!present.Contains(old) && !Clear(expected, old)) return false;
                _observed.Clear(); _observed.UnionWith(present); _lastSequence = copy.sequence; _lastTime = copy.timeSeconds; return true;
            }
            catch { return false; } finally { _applying = false; }
        }
        private bool ApplyMotion(LocalActorRegistration expected, long lease, AnchorPoseFrame frame)
        {
            string rootName = RootAnchorName();
            if (!Main() || rootName == null || !Current(expected) || _applying || _observedSourceSetupToken != 0 ||
                lease != _motionSinkLease || _motionEpoch == 0 ||
                !AnchorPoseFrameValidation.TryValidateFrame(frame, _lastSequence, _lastTime,
                    _known, out _)) return false;
            AnchorPoseFrame copy = AnchorPoseFrameValidation.DeepCopy(frame);
            ulong epoch = _motionEpoch;
            CommittedMotionObservationLease[] observers = _motionObservers.ToArray();
            var sourceObservers = _sourceInteractionObservers.ToArray();
            CommittedMotionObservation observation = null;
            SourceInteractionEvidence evidence = null;
            var evidenceStatus = SourceInteractionEvidenceStatus.Unavailable;
            long revision = 0;
            bool accepted = false;
            _applying = true;
            try
            {
                double receipt = MotionReceiptSeconds();
                if (double.IsNaN(receipt) || double.IsInfinity(receipt) || receipt < 0d) return false;
                if (observers.Length != 0 || sourceObservers.Length != 0)
                    observation = FrameObservation(observers.Length != 0 ? observers[0].Source : sourceObservers[0].Source,
                        epoch, copy, rootName, receipt);
                PrepareSourceInteractionFrame(expected, epoch);
#if UNITY_EDITOR
                TestOnlyBeforeMotionNativeAdmission?.Invoke();
#endif
                if (!Current(expected) || lease != _motionSinkLease || epoch != _motionEpoch) return false;
                // Native admission validates every member and commits this complete
                // snapshot atomically, including actual recording sequence/time.
                if (!expected.World.TryApplyActorObservationFrame(expected, epoch, copy,
                    rootName, out _)) return false;
                accepted = true;
                if (!Current(expected) || lease != _motionSinkLease || epoch != _motionEpoch) return true;
                // Freeze the exact native frame BEFORE any managed observer can
                // submit a newer frame. Never rebuild evidence from a later scene.
                if (sourceObservers.Length != 0)
                    evidenceStatus = expected.World.TryCopySourceEvidence(expected, epoch, copy.sequence, out evidence);
                _observed.Clear();
                foreach (AnchorPoseSample sample in copy.anchors) _observed.Add(sample.anchorId);
                _lastSequence = copy.sequence; _lastTime = copy.timeSeconds;
                revision = ++_motionPublicationRevision;
            }
            catch { return accepted; }
            finally { _applying = false; FlushSourceEvidenceDisable(); }
            if (observation != null)
            {
                DeliverSourceInteraction(sourceObservers, observation, evidence, evidenceStatus, expected, lease, epoch, revision);
                DeliverCommittedMotion(observers, observation, expected, lease, epoch, revision);
            }
            return true;
        }
        private bool DeactivateBody(LocalActorRegistration expected, string name)
        {
            try
            {
                SpatialBlendAPI.ClearAnchorTransform(expected.NativeSpace, expected.NativeActor, name);
                return SpatialBlendAPI.SetAnchorActive(expected.NativeSpace, expected.NativeActor,
                                                       name, false);
            }
            catch { return false; }
        }
        private static bool SetRootActive(LocalActorRegistration expected, string name, bool active)
        {
            try { return SpatialBlendAPI.SetAnchorActive(expected.NativeSpace, expected.NativeActor, name, active); }
            catch { return false; }
        }
        private void ClearMotion(LocalActorRegistration expected, long lease, string name)
        {
            if (!Main() || _applying || !_isSource || !Current(expected) || lease != _motionSinkLease ||
                _motionEpoch == 0 || !_known.Contains(name)) return;
            ulong epoch = _motionEpoch;
            CommittedMotionObservationLease[] observers = _motionObservers.ToArray();
            var sourceObservers = _sourceInteractionObservers.ToArray();
            long revision;
            _applying = true;
            try
            {
                if (!expected.World.TryClearActorObservationAnchor(expected, epoch, name, out _) ||
                    !Current(expected) || lease != _motionSinkLease || epoch != _motionEpoch) return;
                _observed.Remove(name);
                revision = ++_motionPublicationRevision;
            }
            finally { _applying = false; }
            DeliverSourceInteractionControl(sourceObservers, CommittedMotionObservationKind.Cleared, expected, lease, epoch, revision, name);
            if (observers.Length != 0)
                DeliverCommittedMotion(observers, ControlObservation(observers[0].Source,
                    CommittedMotionObservationKind.Cleared, epoch, name), expected, lease, epoch, revision);
        }
        private void ResetMotion(LocalActorRegistration expected, long lease)
        { TryResetMotion(expected, lease); }
        private bool TryResetMotion(LocalActorRegistration expected, long lease)
        {
            if (!Main() || _applying || !_isSource || _observedSourceSetupToken != 0 || !Current(expected) || lease != _motionSinkLease) return false;
            CommittedMotionObservationLease[] observers = _motionObservers.ToArray();
            var sourceObservers = _sourceInteractionObservers.ToArray();
            ulong epoch;
            long revision;
            _applying = true;
            try
            {
                if (!expected.World.TryBeginActorObservationEpoch(expected, RootAnchorName(),
                    out epoch, out _) || !Current(expected) || lease != _motionSinkLease) return false;
                _motionEpoch = epoch;
                ResetSourceInteractionEpoch();
                _observed.Clear(); _lastSequence = -1; _lastTime = double.NegativeInfinity;
                revision = ++_motionPublicationRevision;
            }
            finally { _applying = false; }
            DeliverSourceInteractionControl(sourceObservers, CommittedMotionObservationKind.Reset, expected, lease, epoch, revision);
            if (observers.Length != 0)
                DeliverCommittedMotion(observers, ControlObservation(observers[0].Source,
                    CommittedMotionObservationKind.Reset, epoch), expected, lease, epoch, revision);
            return true;
        }
        private bool FlushMotion(LocalActorRegistration expected)
        {
            if (!Main() || !_isSource || !Current(expected) ||
                !SpatialBlendAPI.TryReadActorState(expected.NativeSpace, expected.NativeActor,
                                                    out Vector3 position, out float yaw)) return false;
            return expected.World.TryUpdateActorRoot(expected, position, yaw, out _);
        }
        private string RootAnchorName()
        {
            string root = null;
            foreach (var item in _roles)
            {
                if (item.Value != SpatialBlendActorAnchorRole.Root) continue;
                if (root != null) return null;
                root = item.Key;
            }
            return root;
        }
        private bool HasMotionCompatibleRoot()
        {
            SpatialBlendActorAnchor root = null;
            foreach (SpatialBlendActorAnchor anchor in _anchors)
            {
                if (anchor == null || anchor.Role != SpatialBlendActorAnchorRole.Root) continue;
                if (root != null) return false;
                root = anchor;
            }
            return root != null && root.LocalOffset.Equals(Vector3.zero) &&
                   string.IsNullOrEmpty(root.ParentName);
        }
        private void ClearLegacy(LocalActorRegistration expected, string name)
        {
            if (!Main() || _applying || !_isSource || _observedSourceSetupToken != 0 || !Current(expected) ||
                !_roles.TryGetValue(name, out SpatialBlendActorAnchorRole role) ||
                role != SpatialBlendActorAnchorRole.Body) return;
            _applying = true;
            try
            {
                DetachCommittedMotion(CommittedMotionRetirementReason.LegacyMutation)();
                if (Current(expected)) Clear(expected, name);
            }
            finally { _applying = false; }
        }
        private bool Clear(LocalActorRegistration expected, string name)
        {
            if (!Main() || !_isSource || !Current(expected) || !_roles.TryGetValue(name, out SpatialBlendActorAnchorRole role) || role != SpatialBlendActorAnchorRole.Body) return false;
            try
            {
                SpatialBlendAPI.ClearAnchorTransform(expected.NativeSpace, expected.NativeActor, name);
                if (!SpatialBlendAPI.SetAnchorActive(expected.NativeSpace, expected.NativeActor, name, false)) return false;
                _observed.Remove(name);
                return true;
            }
            catch { return false; }
        }
        private bool Reset(LocalActorRegistration r)
        {
            _observed.Clear(); _lastSequence = -1; _lastTime = double.NegativeInfinity;
            if (r == null || !r.IsLive) return true;
            foreach (var item in _roles)
                if (item.Value == SpatialBlendActorAnchorRole.Body)
                    try { if (!SpatialBlendAPI.SetAnchorActive(r.NativeSpace, r.NativeActor, item.Key, false)) return false; }
                    catch { return false; }
            return true;
        }
        private bool Current(LocalActorRegistration r) => r != null && r.IsLive && ReferenceEquals(r, _registration);

        internal bool TryEnterObservedSourceSetup(LocalActorRegistration expected, long setup,
            ulong expectedPriorEpoch, out Action legacyRetirement, out string error)
        {
            error = null; legacyRetirement = null;
            if (!Main() || setup <= 0 || !Current(expected) || _observedSourceSetupToken != 0 ||
                _motionEpoch != expectedPriorEpoch)
            { error = "C1 source setup no longer matches its explicitly preflighted legacy epoch."; return false; }
            if (expectedPriorEpoch != 0)
            {
                // Core has atomically replaced this exact epoch as part of the
                // setup transaction. Retire the managed legacy writer before
                // callbacks, but defer callback delivery until every C1 guard
                // is installed by the world.
                legacyRetirement = DetachCommittedMotion(CommittedMotionRetirementReason.WriterReplaced);
                _motionEpoch = 0;
                ++_motionSinkLease;
                _lastSequence = -1; _lastTime = double.NegativeInfinity;
                _observed.Clear();
            }
            _observedSourceSetupToken = setup;
            return true;
        }

        internal void ExitObservedSourceSetup(LocalActorRegistration expected, long setup)
        {
            if (Main() && Current(expected) && setup > 0 && _observedSourceSetupToken == setup)
                _observedSourceSetupToken = 0;
        }

        internal bool TryGetFreshObservedSourceSetupParticipant(
            out LocalActorRegistration registration, out string rootAnchor,
            out ulong expectedPriorEpoch, out string error)
        {
            registration = null; rootAnchor = null; expectedPriorEpoch = 0; error = null;
            if (!Main() || !_isSource || !IsRegistered || _observedSourceSetupToken != 0 ||
                !HasMotionCompatibleRoot())
            { error = "C1 source setup requires a fresh live source actor with a compatible root."; return false; }
            rootAnchor = RootAnchorName();
            if (rootAnchor == null) { error = "C1 source setup has no declared root anchor."; return false; }
            expectedPriorEpoch = _motionEpoch;
            bool hasNativeEpoch = SpatialBlendAPI.TryGetActorObservationClock(
                _registration.NativeSpace, _registration.NativeActor, out var clock);
            if ((expectedPriorEpoch == 0 && hasNativeEpoch) ||
                (expectedPriorEpoch != 0 && (!hasNativeEpoch || clock.Epoch != expectedPriorEpoch)))
            { error = "C1 source setup cannot replace an unmanaged or changed native observation epoch."; return false; }
            registration = _registration;
            return true;
        }
        private bool IsMotionSinkRetired(LocalActorRegistration expected, long lease) =>
            !Current(expected) || lease != _motionSinkLease;
        private bool CurrentOutput(LocalActorRegistration registration, long epoch, long revision) =>
            revision == _outputRevision && Current(registration) &&
            registration.World.IsCurrentActorOutput(registration, epoch);
        private bool Main() => _mainThread == Thread.CurrentThread.ManagedThreadId;
        private bool TryAcquireBodyLeases(out Dictionary<string, AnchorTargetController.TargetLease> leases,
                                          out string error)
        {
            leases = new Dictionary<string, AnchorTargetController.TargetLease>(StringComparer.Ordinal);
            error = null;
            if (_isSource || _bodyTargets == null) return true;
            foreach (var role in _roles)
            {
                if (role.Value != SpatialBlendActorAnchorRole.Body) continue;
                if (_bodyTargets.TryAcquireLease(role.Key, out AnchorTargetController.TargetLease lease))
                    leases.Add(role.Key, lease);
                else
                {
                    DisposeLeases(leases);
                    error = "A body-anchor target lease is already owned or unavailable.";
                    return false;
                }
            }
            return true;
        }
        private static void DisposeLeases(Dictionary<string, AnchorTargetController.TargetLease> leases)
        {
            foreach (AnchorTargetController.TargetLease lease in leases.Values) lease.Dispose();
            leases.Clear();
        }
        private Dictionary<string, AnchorTargetController.TargetLease> DetachBodyLeases()
        {
            var detached = new Dictionary<string, AnchorTargetController.TargetLease>(_bodyLeases,
                StringComparer.Ordinal);
            _bodyLeases.Clear();
            return detached;
        }
        private List<AnchorTargetController.TargetLease> DetachOwnedTargetLeases(
            Dictionary<string, AnchorTargetController.TargetLease> leases)
        {
            var targets = new List<AnchorTargetController.TargetLease>();
            var names = new List<string>(_ownedTargets);
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
                if (leases.TryGetValue(name, out AnchorTargetController.TargetLease lease)) targets.Add(lease);
            _ownedTargets.Clear();
            return targets;
        }
        private static void CancelAndDispose(List<AnchorTargetController.TargetLease> targets,
                                             Dictionary<string, AnchorTargetController.TargetLease> leases)
        {
            foreach (AnchorTargetController.TargetLease lease in targets) lease.Cancel();
            DisposeLeases(leases);
        }
        private void Cancel(string name)
        {
            if (_ownedTargets.Remove(name) &&
                _bodyLeases.TryGetValue(name, out AnchorTargetController.TargetLease lease))
                lease.Cancel();
        }
        private bool CancelAll(long revision)
        {
            var names = new List<string>(_ownedTargets);
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (revision != _outputRevision) return false;
                Cancel(name);
                if (revision != _outputRevision) return false;
            }
            return true;
        }
        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        private void InvokeCleared(LocalActorRegistration expected, long epoch, long revision, bool detached,
                                   bool requireOutputEpoch)
        {
            Action callbacks = RootTargetsCleared;
            if (callbacks == null) return;
            foreach (Action callback in callbacks.GetInvocationList())
            {
                try { callback(); } catch { }
                if (detached)
                {
                    if (_registration != null && !ReferenceEquals(_registration, expected)) return;
                }
                else if (!Current(expected) || revision != _outputRevision ||
                         (requireOutputEpoch && !CurrentOutput(expected, epoch, revision))) return;
            }
        }
        private void InvokeRoot(LocalActorRegistration expected, long epoch, long revision,
                                SpatialBlendRootTarget value)
        {
            Action<SpatialBlendRootTarget> callbacks = RootTargetAvailable;
            if (callbacks == null) return;
            foreach (Action<SpatialBlendRootTarget> callback in callbacks.GetInvocationList())
            {
                try { callback(value); } catch { }
                if (!CurrentOutput(expected, epoch, revision)) return;
            }
        }
        private void InvokeCue(LocalActorRegistration expected, long epoch, long revision,
                               bool detached, bool requireOutputEpoch,
                               IReadOnlyList<RealizationTarget> targets)
        {
            Action<SpatialControlCueFrame> callbacks = SpatialControlCueFrameAvailable;
            if (callbacks == null || string.IsNullOrEmpty(_cueStreamId) || _owner == null ||
                !SpatialControlCueFrame.TryCreate(_actorId, _owner.SpaceId, _cueStreamId,
                                                   revision, Time.unscaledTimeAsDouble, targets,
                                                   out SpatialControlCueFrame frame)) return;
            foreach (Action<SpatialControlCueFrame> callback in callbacks.GetInvocationList())
            {
                try { callback(frame.DeepCopy()); } catch { }
                if (detached)
                {
                    if (_registration != null && !ReferenceEquals(_registration, expected)) return;
                }
                else if (!Current(expected) || revision != _outputRevision ||
                         (requireOutputEpoch && !CurrentOutput(expected, epoch, revision))) return;
            }
        }
        private void OnDisable() { Unregister(); } private void OnDestroy() { Unregister(); }
    }
}
