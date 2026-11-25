// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace SpatialBlend.Motion
{
    [Flags]
    public enum RigPoseDofs { Position = 1, Rotation = 2, Pose = Position | Rotation, ReceiverPlanarPosition = 4 }

    public readonly struct RigJointClaim
    {
        public RigJointClaim(string name, int index, RigPoseDofs dofs)
        { Name = name; Index = index; Dofs = dofs; }
        public string Name { get; }
        public int Index { get; }
        public RigPoseDofs Dofs { get; }
    }

    public readonly struct RigJointPose
    {
        public RigJointPose(string name, int index, Matrix4x4 parentFromJoint)
        { Name = name; Index = index; ParentFromJoint = parentFromJoint; }
        public string Name { get; }
        public int Index { get; }
        public Matrix4x4 ParentFromJoint { get; }
    }

    /// <summary>Observed live lease (read-only copy).</summary>
    public readonly struct RigLeaseObservation
    {
        public RigLeaseObservation(string controllerId, ulong generation, long validUntil, IReadOnlyList<RigJointClaim> claims)
        { ControllerId = controllerId; Generation = generation; ValidUntilNativeNanoseconds = validUntil; Claims = claims; }
        public string ControllerId { get; }
        public ulong Generation { get; }
        public long ValidUntilNativeNanoseconds { get; }
        public IReadOnlyList<RigJointClaim> Claims { get; }
    }

    /// <summary>Owner-recorded identity of the most recent applied write.</summary>
    public readonly struct RigAppliedBatchObservation
    {
        public RigAppliedBatchObservation(string controllerId, ulong generation, string label, ulong revision)
        { ControllerId = controllerId; Generation = generation; Label = label; Revision = revision; }
        public string ControllerId { get; }
        public ulong Generation { get; }
        public string Label { get; }
        public ulong Revision { get; }
    }

    /// <summary>Immutable actuator readback, not recorded body evidence or a MotionSequence.</summary>
    public sealed class RigPoseReadback
    {
        internal RigPoseReadback(CalibratedTransformRigOutputOwner owner, ulong revision,
            RigJointPose[] local, Matrix4x4[] world)
            : this(owner, revision, local, world, false, Matrix4x4.identity,
                owner == null ? Matrix4x4.identity : owner.InputProfile.ReceiverWorldFromBody, Matrix4x4.identity) { }
        internal RigPoseReadback(CalibratedTransformRigOutputOwner owner, ulong revision,
            RigJointPose[] local, Matrix4x4[] world, bool carrier, Matrix4x4 carrierLocal,
            Matrix4x4 receiverFromBody, Matrix4x4 receiverFromLocomotion)
        { Owner = owner; Revision = revision; LocalPoses = Array.AsReadOnly(local); WorldPoses = Array.AsReadOnly(world);
            HasLocomotionCarrier = carrier; CarrierLocalPose = carrierLocal;
            ReceiverWorldFromRigBody = receiverFromBody; ReceiverWorldFromLocomotionRoot = receiverFromLocomotion; }
        internal CalibratedTransformRigOutputOwner Owner { get; }
        public ulong Revision { get; }
        public IReadOnlyList<RigJointPose> LocalPoses { get; }
        public IReadOnlyList<Matrix4x4> WorldPoses { get; }
        public bool HasLocomotionCarrier { get; }
        public Matrix4x4 CarrierLocalPose { get; }
        public Matrix4x4 ReceiverWorldFromRigBody { get; }
        public Matrix4x4 ReceiverWorldFromLocomotionRoot { get; }
    }

    /// <summary>
    /// Explicit manual-Transform output adapter for one isolated, calibrated rig.
    /// Not an Animator/SDK adapter: only Transform/render/static-collider components
    /// are supported, with no independently scheduled writers. All supported writes
    /// use this owner. Direct/external pose divergence revokes it, never rolls it back.
    /// It consumes controller-computed local rigid poses, never interprets rich goals.
    /// </summary>
    public sealed class CalibratedTransformRigOutputOwner : IDisposable
    {
        private sealed class ReferenceComparer : IEqualityComparer<Transform>
        {
            public bool Equals(Transform a, Transform b) => ReferenceEquals(a, b);
            public int GetHashCode(Transform value) => RuntimeHelpers.GetHashCode(value);
        }
        private static readonly Dictionary<Transform, CalibratedTransformRigOutputOwner> Owners =
            new Dictionary<Transform, CalibratedTransformRigOutputOwner>(new ReferenceComparer());
        private static readonly object RegistryLock = new object();
        private readonly ReceiverBodyInputBinding _input;
        private readonly Transform _origin;
        private readonly Transform _carrier;
        private readonly Matrix4x4 _carrierFromLocomotionRoot, _rootAtBind;
        private readonly Transform[] _joints;
        private readonly Matrix4x4 _originWorld;
        private readonly Transform[] _ancestors;
        private readonly HashSet<int> _reserved;
        private readonly List<RigWriteLease> _leases = new List<RigWriteLease>();
        private readonly ConditionalWeakTable<RigPoseReadback, object> _issuedReadbacks =
            new ConditionalWeakTable<RigPoseReadback, object>();
        private readonly int _thread;
        private Pose[] _expected;
        private Pose _expectedCarrier;
        private ulong _revision = 1;
        private long _lastClock;
        private RigAppliedBatchObservation _lastApplied;
        private bool _closed;

        private CalibratedTransformRigOutputOwner(ReceiverBodyInputBinding input, Transform origin,
            Transform[] joints, HashSet<int> reserved, Matrix4x4 worldFromReceiverWorld, string evidence,
            Transform carrier, Matrix4x4 carrierFromRoot, ReceiverBodyRealizationCheckpoint rootCalibration)
        {
            _input = input; _origin = origin; _joints = joints; _reserved = reserved;
            _carrier = carrier; HasLocomotionCarrier = !ReferenceEquals(carrier, null);
            _carrierFromLocomotionRoot = carrierFromRoot; LocomotionCalibration = rootCalibration;
            _originWorld = origin.localToWorldMatrix; _thread = Thread.CurrentThread.ManagedThreadId;
            WorldFromReceiverWorld = worldFromReceiverWorld;
            CalibrationEvidence = evidence; _expected = ReadLocal();
            _expectedCarrier = HasLocomotionCarrier ? new Pose(carrier.localPosition, carrier.localRotation) : Pose.identity;
            _rootAtBind = HasLocomotionCarrier ? worldFromReceiverWorld.inverse * carrier.localToWorldMatrix * carrierFromRoot : Matrix4x4.identity;
            var ancestors = new List<Transform>();
            for (Transform ancestor = origin.parent; ancestor != null; ancestor = ancestor.parent) ancestors.Add(ancestor);
            _ancestors = ancestors.ToArray();
            ProfileFingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write(HasLocomotionCarrier ? "spatialblend-manual-rig-carrier-v1-unity-metres" : "spatialblend-manual-rig-output-v1-unity-metres");
                writer.Write(input.Profile.Fingerprint); writer.Write(evidence);
                ReceiverBodyHash.WriteMatrix(writer, _originWorld);
                ReceiverBodyHash.WriteMatrix(writer, worldFromReceiverWorld);
                writer.Write(joints.Length);
                for (int i = 0; i < joints.Length; ++i) writer.Write(reserved.Contains(i));
                if (HasLocomotionCarrier)
                {
                    ReceiverBodyHash.WriteMatrix(writer, carrierFromRoot);
                    ReceiverBodyHash.WriteMatrix(writer, _rootAtBind);
                }
            });
        }
        public ReceiverBodyProfile InputProfile => _input.Profile;
        public Matrix4x4 WorldFromReceiverWorld { get; }
        public string CalibrationEvidence { get; }
        public string ProfileFingerprint { get; }
        public bool HasLocomotionCarrier { get; }
        public Matrix4x4 CarrierFromLocomotionRoot => _carrierFromLocomotionRoot;
        // Historical calibration evidence, not a current observation certificate.
        public ReceiverBodyRealizationCheckpoint LocomotionCalibration { get; }
        public const int LocomotionCarrierIndex = -1;
        // Local terminal state, not a positive currentness/deadline certificate.
        public bool IsClosed => _closed;

        /// <summary>
        /// Bind at the explicit reference pose, one actual named/indexed joint per
        /// declared skeleton row, direct topology and no undeclared descendants.
        /// Origin must equal WorldFromReceiverWorld * Profile.ReceiverWorldFromBody.
        /// Does not disable or take over any existing writer/component.
        /// </summary>
        public static bool TryBind(ReceiverBodyInputBinding input, Transform origin,
            IReadOnlyList<Transform> joints, IReadOnlyList<int> reservedWorldJoints,
            Matrix4x4 worldFromReceiverWorld, string calibrationEvidence,
            out CalibratedTransformRigOutputOwner owner, out string error)
            => TryBindCore(input, origin, null, Matrix4x4.identity, null, joints, reservedWorldJoints,
                worldFromReceiverWorld, calibrationEvidence, false, out owner, out error);

        /// <summary>
        /// Explicit planar locomotion carrier between the fixed calibration
        /// origin and skeleton root. It is not the pelvis or a detached marker.
        /// Calibrate its root against one exact current independent observation;
        /// later generated output is never forced to equal or republished as input.
        /// </summary>
        public static bool TryBindWithLocomotion(ReceiverBodyInputBinding input, Transform origin,
            Transform carrier, Matrix4x4 carrierFromLocomotionRoot, ReceiverBodyRealizationCheckpoint calibration,
            IReadOnlyList<Transform> joints, IReadOnlyList<int> reservedWorldJoints,
            Matrix4x4 worldFromReceiverWorld, string calibrationEvidence,
            out CalibratedTransformRigOutputOwner owner, out string error)
            => TryBindCore(input, origin, carrier, carrierFromLocomotionRoot, calibration, joints,
                reservedWorldJoints, worldFromReceiverWorld, calibrationEvidence, true, out owner, out error);

        /// <summary>
        /// Main-thread, non-mutating reference-rig validation before receiver
        /// activation. This is not an ownership reservation or agreement with
        /// a future independently acquired root sample.
        /// </summary>
        public static bool TryPreflightWithLocomotion(SpatialBlendActor receiver, ReceiverBodyProfileSpec spec, Transform origin,
            Transform carrier, Matrix4x4 carrierFromRoot, IReadOnlyList<Transform> joints,
            IReadOnlyList<int> reservedWorldJoints, Matrix4x4 worldFromReceiverWorld,
            string calibrationEvidence, out string error)
        {
            error = null;
            if (ReferenceEquals(receiver, null) || !receiver.IsOwnerThread)
                return Fail("Reference-rig preflight requires the receiver's Unity owner thread.", out error);
            if (spec == null || !TryCopy(joints, ReceiverBodySkeleton.MaximumJoints, out var copy, out error) ||
                !TryCopy(reservedWorldJoints, ReceiverBodySkeleton.MaximumJoints, out var reserved, out error))
                return Fail(error ?? "An explicit immutable body specification is required.", out error);
            return ValidateReferenceLayout(spec.Skeleton, spec.ReceiverWorldFromBody, spec.RootAnchor,
                origin, carrier, carrierFromRoot, true, copy, reserved, worldFromReceiverWorld,
                calibrationEvidence, out _, out error);
        }

        private static bool ValidateReferenceLayout(ReceiverBodySkeleton skeleton, Matrix4x4 receiverFromBody,
            string rootName, Transform origin, Transform carrier, Matrix4x4 carrierFromRoot, bool requireCarrier,
            Transform[] joints, int[] reservedIndices, Matrix4x4 worldFromReceiver, string evidence,
            out HashSet<int> reserved, out string error)
        {
            reserved = null; error = null;
            if (skeleton == null || !ReceiverInputValidation.ValidName(evidence) ||
                !ReceiverInputValidation.TryNativePose(worldFromReceiver, out _) ||
                origin == null || joints.Length != skeleton.Joints.Count ||
                !Near(origin.localToWorldMatrix, worldFromReceiver * receiverFromBody))
                return Fail("The actual output origin must match the explicit receiver/body calibration chain.", out error);
            if (requireCarrier && (carrier == null || ReferenceEquals(carrier, origin) ||
                !ReferenceEquals(carrier.parent, origin) || carrier.name != rootName ||
                origin.childCount != 1 || carrier.childCount != 1 ||
                !Near(Matrix4x4.TRS(carrier.localPosition, carrier.localRotation, carrier.localScale), Matrix4x4.identity) ||
                !carrier.localScale.Equals(Vector3.one) || !ReceiverInputValidation.TryNativePose(carrierFromRoot, out _)))
                return Fail("Carrier needs the exact named identity-at-bind topology and rigid calibration.", out error);
            var all = origin.GetComponentsInChildren<Transform>(true);
            if (all.Length != joints.Length + (requireCarrier ? 2 : 1))
                return Fail("Manual output hierarchy contains missing or undeclared transforms.", out error);
            foreach (Transform node in all)
                if (!SupportedComponents(node, out error) || !ActualWorldProper(node.localToWorldMatrix))
                    return Fail(error ?? "Actual rig transforms must be finite and proper.", out error);
            for (Transform ancestor = origin.parent; ancestor != null; ancestor = ancestor.parent)
                if (!SupportedComponents(ancestor, out error)) return false;
            var seen = new HashSet<Transform>(new ReferenceComparer());
            for (int i = 0; i < joints.Length; ++i)
            {
                var actual = joints[i]; var definition = skeleton.Joints[i];
                if (actual == null || !seen.Add(actual) || actual.name != definition.Name ||
                    !actual.localScale.Equals(Vector3.one) ||
                    !ReferenceEquals(actual.parent, definition.ParentIndex < 0 ? (requireCarrier ? carrier : origin) : joints[definition.ParentIndex]) ||
                    !Near(Matrix4x4.TRS(actual.localPosition, actual.localRotation, actual.localScale), definition.ParentFromReferenceJoint))
                    return Fail("Actual joint names/order/direct parents/reference poses must match the declared calibration.", out error);
            }
            reserved = new HashSet<int>();
            foreach (int index in reservedIndices)
                if (index < 0 || index >= joints.Length || !reserved.Add(index))
                    return Fail("Reserved world joints must be distinct actual indices.", out error);
            lock (RegistryLock)
                foreach (var entry in Owners)
                    if (!entry.Value._closed && entry.Key != null &&
                        (ReferenceEquals(entry.Key, origin) || origin.IsChildOf(entry.Key) || entry.Key.IsChildOf(origin)))
                        return Fail("Another output owner already reserves this hierarchy or an ancestor/descendant.", out error);
            return true;
        }

        private static bool TryBindCore(ReceiverBodyInputBinding input, Transform origin, Transform carrier,
            Matrix4x4 carrierFromRoot, ReceiverBodyRealizationCheckpoint calibration,
            IReadOnlyList<Transform> joints, IReadOnlyList<int> reservedWorldJoints,
            Matrix4x4 worldFromReceiverWorld, string calibrationEvidence, bool requireCarrier,
            out CalibratedTransformRigOutputOwner owner, out string error)
        {
            owner = null; error = null;
            // Native reader enforces the existing input's owner thread BEFORE
            // any Transform, component or Unity fake-null operation below.
            if (input == null || !input.TryReadAcquisitionClock(out long now, out error)) return false;
            if (!ReceiverInputValidation.ValidName(calibrationEvidence) ||
                !ReceiverInputValidation.TryNativePose(worldFromReceiverWorld, out _))
                return Fail("An explicit calibrated complete manual rig is required.", out error);
            // Freeze caller-owned collections before inspecting actual scene state.
            if (!TryCopy(joints, ReceiverBodySkeleton.MaximumJoints, out var copy, out error) ||
                !TryCopy(reservedWorldJoints, ReceiverBodySkeleton.MaximumJoints, out var reservedCopy, out error)) return false;
            if (!input.TryReadAcquisitionClock(out now, out error)) return false;
            if (!ValidateReferenceLayout(input.Profile.Skeleton, input.Profile.ReceiverWorldFromBody,
                input.Profile.Input.RootAnchor, origin, carrier, carrierFromRoot, requireCarrier, copy,
                reservedCopy, worldFromReceiverWorld, calibrationEvidence, out var reserved, out error)) return false;
            if (requireCarrier)
            {
                if (!TryCalibrationRoot(input, calibration, out var observedRoot, out error)) return false;
                if (!Near(worldFromReceiverWorld.inverse * carrier.localToWorldMatrix * carrierFromRoot, observedRoot))
                    return Fail("The actual calibrated carrier root does not match the independent reference observation.", out error);
            }
            var candidate = new CalibratedTransformRigOutputOwner(input, origin, copy, reserved,
                worldFromReceiverWorld, calibrationEvidence, carrier, carrierFromRoot, calibration);
            if (!candidate.Check(out now, out error)) return false;
            if (requireCarrier && !TryCalibrationRoot(input, calibration, out _, out error)) return false;
            lock (RegistryLock)
            {
                // Destroyed Unity objects retain managed reference identity. Retire
                // their handles before using native Transform hierarchy operations.
                foreach (var prior in new List<CalibratedTransformRigOutputOwner>(Owners.Values))
                {
                    if (prior._thread != candidate._thread)
                        return Fail("Manual rig owners must share their Unity owner thread.", out error);
                    if (prior._closed || prior._origin == null) prior.TryClose(out _);
                }
                foreach (var entry in Owners)
                    if (!entry.Value._closed && (ReferenceEquals(entry.Key, origin) ||
                        origin.IsChildOf(entry.Key) || entry.Key.IsChildOf(origin)))
                        return Fail("Another output owner already reserves this hierarchy or an ancestor/descendant.", out error);
                Owners.Add(origin, candidate);
            }
            candidate._lastClock = now; owner = candidate; return true;
        }

        public bool TryReadPose(out RigPoseReadback pose, out string error)
        {
            pose = null;
            if (!Check(out _, out error)) return false;
            var local = new RigJointPose[_joints.Length]; var world = new Matrix4x4[_joints.Length];
            for (int i = 0; i < local.Length; ++i)
            {
                local[i] = new RigJointPose(InputProfile.Skeleton.Joints[i].Name, i,
                    Matrix4x4.TRS(_expected[i].position, _expected[i].rotation, Vector3.one));
                world[i] = _joints[i].localToWorldMatrix;
            }
            Matrix4x4 body = WorldFromReceiverWorld.inverse * (HasLocomotionCarrier ? _carrier.localToWorldMatrix : _originWorld);
            pose = new RigPoseReadback(this, _revision, local, world, HasLocomotionCarrier,
                HasLocomotionCarrier ? Matrix4x4.TRS(_carrier.localPosition, _carrier.localRotation, Vector3.one) : Matrix4x4.identity,
                body, HasLocomotionCarrier ? body * _carrierFromLocomotionRoot : Matrix4x4.identity);
            _issuedReadbacks.Add(pose, this); return true;
        }

        /// <summary>
        /// Read-only observation of the currently live write leases (controller,
        /// generation, deadline, exact claims), after the same lifetime service as
        /// every read. For independent observers; grants and renews nothing.
        /// </summary>
        public bool TryObserveLeases(out IReadOnlyList<RigLeaseObservation> leases, out string error)
        {
            leases = null;
            if (!Check(out _, out error)) return false;
            var result = new List<RigLeaseObservation>(_leases.Count);
            foreach (RigWriteLease lease in _leases)
                if (!lease.IsClosed)
                    result.Add(new RigLeaseObservation(lease.ControllerId, lease.Generation, lease.ValidUntilNativeNanoseconds, lease.Claims));
            leases = result.AsReadOnly(); return true;
        }

        /// <summary>
        /// Read-only record of the most recent applied write on this rig: its
        /// lease controller/generation, its label (null if unlabelled) and the
        /// rig revision it produced. False before any write.
        /// </summary>
        public bool TryObserveLastApplied(out RigAppliedBatchObservation applied, out string error)
        {
            applied = default;
            if (!Check(out _, out error)) return false;
            if (_lastApplied.Revision == 0) return Fail("No write has been applied to this rig.", out error);
            applied = _lastApplied; return true;
        }

        /// <summary>
        /// Owner-thread deadline/lifetime service, also run by read/acquire/apply.
        /// No autonomous timer: a host may call this per frame. Expiry revokes
        /// further writes, never resets bones or manufactures a standing motion.
        /// Pose readback always describes actual transforms, not a live plan.
        /// </summary>
        public bool TryRefreshOwnership(out string error) => Check(out _, out error);

        /// <summary>Same native input clock, with current rig/lease checks; no caller-supplied time.</summary>
        public bool TryReadAcquisitionClock(out long nanoseconds, out string error) => Check(out nanoseconds, out error);

        // Issuance only, not currentness: trajectory oracles retain the exact
        // Begin readback while each write still needs a current issued basis.
        internal bool Issued(RigPoseReadback pose) => pose != null && ReferenceEquals(pose.Owner, this) &&
            _issuedReadbacks.TryGetValue(pose, out _);

        /// <summary>
        /// Acquire local XYZ/full-quaternion writes and their WORLD hierarchy
        /// effects. Ancestor effects conflict with reserved/tracked descendants
        /// and other leases; a single explicit composer may own overlapping effects.
        /// Native expiry is bounded and immutable. New generation needs a new lease.
        /// </summary>
        public bool TryAcquire(string controllerId, ulong generation, long validUntilNativeNanoseconds,
            IReadOnlyList<RigJointClaim> claims, out RigWriteLease lease, out string error)
        {
            lease = null;
            if (!Check(out long now, out error)) return false;
            if (!ReceiverInputValidation.ValidName(controllerId) || generation == 0 ||
                validUntilNativeNanoseconds <= now || validUntilNativeNanoseconds - now > InputProfile.Input.MaximumAgeNanoseconds)
                return Fail("A named, bounded, generation-qualified actuator request is required.", out error);
            if (!TryCopy(claims, _joints.Length + (HasLocomotionCarrier ? 1 : 0), out var copy, out error)) return false;
            if (copy.Length == 0) return Fail("At least one explicit joint claim is required.", out error);
            var indices = new HashSet<int>();
            var effects = new HashSet<(int, RigPoseDofs)>();
            for (int i = 0; i < copy.Length; ++i)
            {
                RigJointClaim claim = copy[i];
                if (claim.Index == LocomotionCarrierIndex && HasLocomotionCarrier)
                {
                    if (!indices.Add(claim.Index) || claim.Name != InputProfile.Input.RootAnchor || claim.Dofs != RigPoseDofs.ReceiverPlanarPosition)
                        return Fail("The carrier owns only explicit receiver-plane translation, never local XYZ or root yaw.", out error);
                    if (_reserved.Count != 0) return Fail("Carrier translation would move a reserved world joint.", out error);
                    for (int j = -1; j < _joints.Length; ++j) effects.Add((j, RigPoseDofs.Position));
                    continue;
                }
                if (claim.Index < 0 || claim.Index >= _joints.Length || !indices.Add(claim.Index) ||
                    claim.Name != InputProfile.Skeleton.Joints[claim.Index].Name ||
                    claim.Dofs == 0 || (claim.Dofs & ~RigPoseDofs.Pose) != 0)
                    return Fail("Claims need distinct exact joint names/indices and whole XYZ/quaternion DOFs.", out error);
                for (int j = claim.Index; j < _joints.Length; ++j)
                {
                    if (!DescendsFrom(j, claim.Index)) continue;
                    if (_reserved.Contains(j)) return Fail("A local write would move a reserved world joint.", out error);
                    if ((claim.Dofs & RigPoseDofs.Position) != 0 || (j != claim.Index && (claim.Dofs & RigPoseDofs.Rotation) != 0))
                        effects.Add((j, RigPoseDofs.Position));
                    if ((claim.Dofs & RigPoseDofs.Rotation) != 0) effects.Add((j, RigPoseDofs.Rotation));
                }
            }
            if (!Check(out now, out error)) return false;
            if (now >= validUntilNativeNanoseconds)
                return Fail("Actuator request expired during validation.", out error);
            foreach (RigWriteLease existing in _leases)
                if (existing.ConflictsWith(effects))
                    return Fail("Physical hierarchy effects overlap another writer; use one explicit composer.", out error);
            lease = new RigWriteLease(this, controllerId, generation, validUntilNativeNanoseconds, copy, effects);
            _leases.Add(lease); return true;
        }

        public sealed class RigWriteLease : IDisposable
        {
            private readonly CalibratedTransformRigOutputOwner _owner;
            private readonly HashSet<(int, RigPoseDofs)> _effects;
            private ulong _lastSequence;
            // Refreshed by owner operations; false alone does not establish liveness.
            public bool IsClosed { get; private set; }
            internal RigWriteLease(CalibratedTransformRigOutputOwner owner, string id, ulong generation,
                long expiry, RigJointClaim[] claims, HashSet<(int, RigPoseDofs)> effects)
            { _owner = owner; ControllerId = id; Generation = generation; ValidUntilNativeNanoseconds = expiry;
                Claims = Array.AsReadOnly((RigJointClaim[])claims.Clone());
                _effects = new HashSet<(int, RigPoseDofs)>(effects); }
            internal bool ConflictsWith(HashSet<(int, RigPoseDofs)> effects) => _effects.Overlaps(effects);
            public string ControllerId { get; }
            public ulong Generation { get; }
            public long ValidUntilNativeNanoseconds { get; }
            public IReadOnlyList<RigJointClaim> Claims { get; }

            /// <summary>
            /// Apply an independently computed complete local write-set, not rich
            /// XYZ/yaw as a manufactured body. The current preparation is checked
            /// again; caller separately owns controller capability/semantic admission.
            /// Basis must be this exact owner's current readback. No callback runs
            /// inside the main-thread write phase; it is not a render-thread transaction.
            /// </summary>
            public bool TryApply(ulong generation, ulong sequence, RigPoseReadback basis,
                IReadOnlyList<RigJointPose> poses, ReceiverBodyRealizationCheckpoint currentInput,
                out string error)
                => TryApplyWithin(generation, sequence, basis, poses, currentInput,
                    0, ValidUntilNativeNanoseconds, out error);

            /// <summary>
            /// As TryApply, and the owner records <paramref name="batchLabel"/>
            /// with this lease's controller/generation and the resulting rig
            /// revision, atomically with the write. Observers read it back with
            /// TryObserveLastApplied; a later write replaces it.
            /// </summary>
            public bool TryApply(ulong generation, ulong sequence, RigPoseReadback basis,
                IReadOnlyList<RigJointPose> poses, ReceiverBodyRealizationCheckpoint currentInput,
                string batchLabel, out string error)
            {
                if (!ReceiverInputValidation.ValidName(batchLabel))
                    return Fail("A labelled write needs a bounded nonempty label.", out error);
                return TryApplyWithin(generation, sequence, basis, poses, currentInput,
                    0, ValidUntilNativeNanoseconds, out error, batchLabel);
            }

            // Additional restriction only: a trajectory's sampled target cannot
            // outlive its own time window, even if preparation/copying stalls.
            // Native owner time is checked after those operations and before
            // setters. This never renews input evidence or the original lease.
            internal bool TryApplyWithin(ulong generation, ulong sequence, RigPoseReadback basis,
                IReadOnlyList<RigJointPose> poses, ReceiverBodyRealizationCheckpoint currentInput,
                long targetAtNativeNanoseconds, long targetValidUntilNativeNanoseconds, out string error,
                string batchLabel = null)
            {
                if (!_owner.Check(out long now, out error)) return false;
                if (targetAtNativeNanoseconds < 0 || targetValidUntilNativeNanoseconds <= targetAtNativeNanoseconds ||
                    now < targetAtNativeNanoseconds || now >= targetValidUntilNativeNanoseconds)
                    return Fail("Actuator target window is invalid, not yet valid or expired.", out error);
                if (IsClosed || !_owner._leases.Contains(this) || generation != Generation || sequence == 0 || sequence <= _lastSequence ||
                    now >= ValidUntilNativeNanoseconds || basis == null || !ReferenceEquals(basis.Owner, _owner) ||
                    !_owner._issuedReadbacks.TryGetValue(basis, out _) ||
                    basis.Revision != _owner._revision || _owner._revision == ulong.MaxValue ||
                    currentInput == null || currentInput.Body == null || currentInput.Delivery == null || currentInput.IsWithdrawn ||
                    !currentInput.IsOwnedBy(_owner._input) || !ReferenceEquals(currentInput.Profile, _owner.InputProfile))
                    return Fail("Obsolete/foreign lease, generation, sequence, pose basis or current body preparation.", out error);
                if (!TryCopy(poses, Claims.Count, out var copied, out error)) return false;
                if (copied.Length != Claims.Count)
                    return Fail("Proposal must include every leased joint exactly once.", out error);
                var desired = (Pose[])_owner._expected.Clone(); var desiredCarrier = _owner._expectedCarrier;
                var seen = new HashSet<int>();
                for (int i = 0; i < copied.Length; ++i)
                {
                    RigJointPose pose = copied[i]; RigJointClaim? found = null;
                    foreach (RigJointClaim claim in Claims) if (claim.Index == pose.Index) { found = claim; break; }
                    if (!found.HasValue || !seen.Add(pose.Index) || pose.Name != found.Value.Name ||
                        !ReceiverInputValidation.TryNativePose(pose.ParentFromJoint, out _))
                        return Fail("Controller proposal must contain each leased joint once as a finite proper local pose.", out error);
                    Vector3 position = pose.ParentFromJoint.GetColumn(3);
                    Quaternion rotation = Quaternion.Normalize(pose.ParentFromJoint.rotation);
                    RigPoseDofs dofs = found.Value.Dofs;
                    if (pose.Index == LocomotionCarrierIndex)
                    {
                        Matrix4x4 receiverFromOrigin = RigPoseMath.ComposeValidatedRigid(
                            RigPoseMath.InverseValidatedRigid(_owner.WorldFromReceiverWorld), _owner._originWorld);
                        Matrix4x4 root = RigPoseMath.ComposeValidatedRigid(
                            RigPoseMath.ComposeValidatedRigid(receiverFromOrigin, pose.ParentFromJoint),
                            _owner._carrierFromLocomotionRoot);
                        Matrix4x4 priorRotation = Matrix4x4.Rotate(desiredCarrier.rotation);
                        if (!_owner.HasLocomotionCarrier || dofs != RigPoseDofs.ReceiverPlanarPosition ||
                            !ReceiverInputValidation.TryNativePose(root, out _) || !SameAxes(pose.ParentFromJoint, priorRotation) ||
                            Math.Abs(root.m13 - _owner._rootAtBind.m13) > .00001)
                            return Fail("Carrier proposal changes unowned receiver height/orientation or invalid calibration.", out error);
                        desiredCarrier = new Pose(position, desiredCarrier.rotation);
                        continue;
                    }
                    if (((dofs & RigPoseDofs.Position) == 0 && !Near(position, desired[pose.Index].position)) ||
                        ((dofs & RigPoseDofs.Rotation) == 0 && Quaternion.Angle(rotation, desired[pose.Index].rotation) > .001f))
                        return Fail("Proposal changes unowned local DOFs; no implicit discard or compensation is permitted.", out error);
                    desired[pose.Index] = new Pose((dofs & RigPoseDofs.Position) != 0 ? position : desired[pose.Index].position,
                        (dofs & RigPoseDofs.Rotation) != 0 ? rotation : desired[pose.Index].rotation);
                }
                // Finite local matrices alone do not guarantee a finite composed
                // hierarchy. Validate all affected descendants before any write.
                // The owner rebuilds each local from the admitted proposal's
                // position and normalised rotation with RigPoseMath.DerivedRigid
                // (double evaluation, one rounding per element), not Unity's
                // float TRS: a strict proposal's TRS rebuild can exceed the strict
                // Gram bound (a real run: hips proposed 6e-15, TRS
                // rebuild 1.3e-12). Every check here stays on the strict gate.
                var world = new Matrix4x4[desired.Length];
                Matrix4x4 carrierLocal = _owner.HasLocomotionCarrier
                    ? RigPoseMath.DerivedRigid(desiredCarrier.position, desiredCarrier.rotation) : Matrix4x4.identity;
                if (!ReceiverInputValidation.TryNativePose(carrierLocal, out _))
                    return Fail("Carrier local pose must be finite and proper before composition.", out error);
                Matrix4x4 bodyWorld = RigPoseMath.ComposeValidatedRigid(_owner._originWorld, carrierLocal);
                if (!ReceiverInputValidation.TryNativePose(bodyWorld, out _))
                    return Fail("Carrier proposal cannot compose a finite proper body frame.", out error);
                for (int i = 0; i < desired.Length; ++i)
                {
                    int parent = _owner.InputProfile.Skeleton.Joints[i].ParentIndex;
                    Matrix4x4 local = RigPoseMath.DerivedRigid(desired[i].position, desired[i].rotation);
                    if (!ReceiverInputValidation.TryNativePose(local, out _))
                        return Fail("Controller local pose must be finite and proper before composition.", out error);
                    world[i] = RigPoseMath.ComposeValidatedRigid(parent < 0 ? bodyWorld : world[parent], local);
                    if (!ReceiverInputValidation.TryNativePose(world[i], out _))
                        return Fail("Controller proposal cannot compose a finite proper world hierarchy.", out error);
                }
                // Recheck after copying a caller collection and the native reads.
                if (!_owner._input.TryRevalidatePreparation(currentInput, out _, out error))
                { currentInput.TryWithdraw(out _); return false; }
                if (!_owner.Check(out now, out error)) return false;
                if (now < targetAtNativeNanoseconds || now >= targetValidUntilNativeNanoseconds)
                    return Fail("Actuator target window expired during proposal validation.", out error);
                if (IsClosed || !_owner._leases.Contains(this) || now >= ValidUntilNativeNanoseconds ||
                    basis.Revision != _owner._revision || sequence <= _lastSequence)
                    return Fail("Actuator ownership or basis changed during proposal validation.", out error);
                if (seen.Contains(LocomotionCarrierIndex))
                    _owner._carrier.SetLocalPositionAndRotation(desiredCarrier.position, desiredCarrier.rotation);
                for (int i = 0; i < desired.Length; ++i)
                {
                    if (!seen.Contains(i)) continue;
                    _owner._joints[i].SetLocalPositionAndRotation(desired[i].position, desired[i].rotation);
                }
                _owner._expected = _owner.ReadLocal();
                if (_owner.HasLocomotionCarrier)
                    _owner._expectedCarrier = new Pose(_owner._carrier.localPosition, _owner._carrier.localRotation);
                ++_owner._revision; _lastSequence = sequence;
                _owner._lastApplied = new RigAppliedBatchObservation(ControllerId, Generation, batchLabel, _owner._revision);
                return true;
            }
            public bool TryClose(out string error)
            {
                if (Thread.CurrentThread.ManagedThreadId != _owner._thread)
                    return Fail("Rig lease cleanup requires its owner thread.", out error);
                if (!IsClosed && !_owner._leases.Contains(this))
                    return Fail("This lease was not issued by the owner.", out error);
                IsClosed = true; _owner._leases.Remove(this); error = null; return true;
            }
            public void Dispose() { if (!TryClose(out string error)) throw new InvalidOperationException(error); }
            internal void Revoke() { IsClosed = true; }
        }

        public bool TryClose(out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                return Fail("Rig output cleanup requires its owner thread.", out error);
            _closed = true;
            foreach (RigWriteLease lease in _leases) lease.Revoke();
            _leases.Clear();
            lock (RegistryLock)
                if (Owners.TryGetValue(_origin, out var current) && ReferenceEquals(current, this)) Owners.Remove(_origin);
            error = null; return true;
        }
        public void Dispose() { if (!TryClose(out string error)) throw new InvalidOperationException(error); }

        private bool Check(out long now, out string error)
        {
            now = 0;
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                return Fail("Rig output requires its Unity owner thread.", out error);
            if (_closed) return Fail("Rig output owner is closed.", out error);
            if (!_input.TryReadAcquisitionClock(out now, out error) || now < _lastClock || !ValidateStructure(out error))
            { TryClose(out _); if (error == null) error = "Rig clock or structure changed."; return false; }
            _lastClock = now;
            for (int i = _leases.Count - 1; i >= 0; --i)
                if (_leases[i].IsClosed || now >= _leases[i].ValidUntilNativeNanoseconds)
                { _leases[i].Revoke(); _leases.RemoveAt(i); }
            for (int i = 0; i < _joints.Length; ++i)
                if (!_joints[i].localPosition.Equals(_expected[i].position) || !_joints[i].localRotation.Equals(_expected[i].rotation))
                { TryClose(out _); return Fail("An unsupported direct/external writer changed the rig; output ownership was revoked without rollback.", out error); }
            if (HasLocomotionCarrier && (!_carrier.localPosition.Equals(_expectedCarrier.position) ||
                !_carrier.localRotation.Equals(_expectedCarrier.rotation)))
            { TryClose(out _); return Fail("An unsupported carrier writer changed the locomotion frame; ownership was revoked without rollback.", out error); }
            return true;
        }

        private bool ValidateStructure(out string error)
        {
            error = null;
            if (_origin == null || !ActualWorldProper(_origin.localToWorldMatrix) ||
                !_origin.localToWorldMatrix.Equals(_originWorld))
                return Fail("Output origin/calibration changed or is not a proper rigid frame.", out error);
            var all = _origin.GetComponentsInChildren<Transform>(true);
            if (all.Length != _joints.Length + (HasLocomotionCarrier ? 2 : 1)) return Fail("Manual output hierarchy contains missing or undeclared transforms.", out error);
            if (HasLocomotionCarrier && (_carrier == null || !ReferenceEquals(_carrier.parent, _origin) ||
                _carrier.name != InputProfile.Input.RootAnchor || _origin.childCount != 1 || _carrier.childCount != 1 ||
                !_carrier.localScale.Equals(Vector3.one) || !ActualWorldProper(_carrier.localToWorldMatrix)))
                return Fail("The calibrated locomotion carrier topology, name, scale or lifetime changed.", out error);
            foreach (Transform node in all) if (!SupportedComponents(node, out error)) return false;
            Transform parent = _origin.parent;
            foreach (Transform ancestor in _ancestors)
            {
                if (ancestor == null || !ReferenceEquals(parent, ancestor) || !SupportedComponents(ancestor, out error))
                    return Fail("Output ancestor hierarchy or its supported writer configuration changed.", out error);
                parent = ancestor.parent;
            }
            if (parent != null) return Fail("Output origin was reparented outside its admitted ancestry.", out error);
            for (int i = 0; i < _joints.Length; ++i)
            {
                ReceiverBodyJoint joint = InputProfile.Skeleton.Joints[i]; Transform actual = _joints[i];
                if (actual == null || actual.name != joint.Name || !actual.localScale.Equals(Vector3.one) ||
                    !ActualWorldProper(actual.localToWorldMatrix) ||
                    !ReferenceEquals(actual.parent, joint.ParentIndex < 0 ? (HasLocomotionCarrier ? _carrier : _origin) : _joints[joint.ParentIndex]))
                    return Fail("Manual rig name, topology, lifetime or scale changed.", out error);
            }
            return true;
        }
        // Actual Transform world matrices are composed by Unity in float. A
        // chain of admitted proper local poses (each passing the strict native
        // gate) can exceed that gate's Gram bound (1e-12) at hierarchy depth
        // 4+, which revoked ownership on a legitimate write. Actual world
        // frames are therefore held to a float-composition bound: Gram <= 1e-10
        // (~3e-6 per axis element), |det - 1| <= 1e-5, exact affine row. Inputs,
        // proposals and calibration matrices keep the strict native gate.
        internal const double ActualWorldGramBound = 1e-10;
        internal static bool ActualWorldProper(Matrix4x4 m)
        {
            for (int i = 0; i < 16; ++i) if (float.IsNaN(m[i]) || float.IsInfinity(m[i])) return false;
            if (m.m30 != 0 || m.m31 != 0 || m.m32 != 0 || m.m33 != 1) return false;
            double gram = 0;
            for (int a = 0; a < 3; ++a)
                for (int b = 0; b < 3; ++b)
                {
                    double dot = 0;
                    for (int r = 0; r < 3; ++r) dot += (double)m[r, a] * m[r, b];
                    dot -= a == b ? 1 : 0; gram += dot * dot;
                }
            double det = (double)m.m00 * ((double)m.m11 * m.m22 - (double)m.m12 * m.m21) -
                (double)m.m01 * ((double)m.m10 * m.m22 - (double)m.m12 * m.m20) +
                (double)m.m02 * ((double)m.m10 * m.m21 - (double)m.m11 * m.m20);
            return gram <= ActualWorldGramBound && Math.Abs(det - 1) <= 1e-5;
        }
        private static bool SupportedComponents(Transform node, out string error)
        {
            foreach (Component component in node.GetComponents<Component>())
                if (!(component is Transform) && !(component is Renderer) && !(component is MeshFilter) && !(component is Collider))
                    return Fail("Manual rig adapter supports only Transform/render/static-collider components, never Animator/SDK/scripts/physics writers.", out error);
            error = null; return true;
        }
        private bool DescendsFrom(int joint, int ancestor)
        { for (int p = joint; p >= 0; p = InputProfile.Skeleton.Joints[p].ParentIndex) if (p == ancestor) return true; return false; }
        private Pose[] ReadLocal()
        { var result = new Pose[_joints.Length]; for (int i = 0; i < result.Length; ++i) result[i] = new Pose(_joints[i].localPosition, _joints[i].localRotation); return result; }
        private static bool TryCopy<T>(IReadOnlyList<T> source, int maximum, out T[] copy, out string error)
        {
            copy = null; error = null;
            if (source == null) return Fail("An explicit bounded collection is required.", out error);
            try
            {
                int count = source.Count;
                if (count < 0 || count > maximum) return Fail("Collection exceeds the declared rig bounds.", out error);
                var values = new T[count];
                for (int i = 0; i < count; ++i) values[i] = source[i];
                if (source.Count != count) return Fail("Collection changed during copying.", out error);
                copy = values; return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IndexOutOfRangeException || exception is InvalidOperationException)
            { return Fail("Collection changed or could not be copied.", out error); }
        }
        private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= .00001f;
        private static bool SameAxes(Matrix4x4 a, Matrix4x4 b) =>
            Vector3.Distance(a.GetColumn(0), b.GetColumn(0)) <= .00001f &&
            Vector3.Distance(a.GetColumn(1), b.GetColumn(1)) <= .00001f &&
            Vector3.Distance(a.GetColumn(2), b.GetColumn(2)) <= .00001f;
        private static bool TryCalibrationRoot(ReceiverBodyInputBinding input, ReceiverBodyRealizationCheckpoint checkpoint,
            out Matrix4x4 root, out string error)
        {
            root = default; error = null;
            if (checkpoint == null || checkpoint.Body == null || checkpoint.Delivery == null || checkpoint.IsWithdrawn ||
                !ReferenceEquals(checkpoint.Profile, input.Profile))
                return Fail("Locomotion calibration needs this input's exact complete current body preparation.", out error);
            // Revalidate through the known live binding, not a potentially forged
            // checkpoint's owner pointer. The binding proves exact delivery/body.
            if (!input.TryRevalidatePreparation(checkpoint, out _, out error)) return false;
            foreach (var anchor in checkpoint.Body.GeometricInput.Anchors)
                if (anchor.Name == input.Profile.Input.RootAnchor && anchor.Available)
                { root = anchor.WorldPose; return true; }
            return Fail("Locomotion calibration has no independent available root.", out error);
        }
        private static bool Near(Matrix4x4 a, Matrix4x4 b)
        { if (!ReceiverInputValidation.TryNativePose(a, out _) || !ReceiverInputValidation.TryNativePose(b, out _)) return false;
            for (int i = 0; i < 16; ++i) if (Mathf.Abs(a[i] - b[i]) > .00001f) return false; return true; }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
