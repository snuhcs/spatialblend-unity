// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using Oculus.Avatar2;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend
{
    /// <summary>
    /// Immutable-snapshot body-tracking input for a Meta Avatar entity. This is an
    /// input context, never a writer of the SDK's animated skeleton transforms.
    /// Its owner must publish from one Unity main-thread seam; SDK callbacks only
    /// copy an already validated managed snapshot into SDK-owned spans.
    /// Bone transforms are SDK Local (parent-relative) coordinates, already
    /// converted into the SDK basis by the caller. This class performs no Unity
    /// coordinate conversion or calibration. Snapshot reads are atomic per
    /// callback, not a transaction across the SDK's separate callbacks; a future
    /// input-manager integration must define the publication/update boundary.
    /// </summary>
    public sealed class SbArticulatedBodyTrackingContext : OvrAvatarBodyTrackingContextBase
    {
        public const int MaximumBones = 128;

        public sealed class SkeletonDefinition
        {
            public readonly int Version;
            public readonly CAPI.ovrAvatar2Vector3f Forward;
            public readonly CAPI.ovrAvatar2Bone[] Bones;
            public readonly CAPI.ovrAvatar2Transform[] LocalReferencePose;

            public SkeletonDefinition(int version, CAPI.ovrAvatar2Vector3f forward,
                                      CAPI.ovrAvatar2Bone[] bones,
                                      CAPI.ovrAvatar2Transform[] localReferencePose)
            {
                Version = version;
                Forward = forward;
                Bones = bones;
                LocalReferencePose = localReferencePose;
            }
        }

        public sealed class Snapshot
        {
            internal Func<bool> Validity { get; }
            public readonly long Sequence;
            public readonly CAPI.ovrAvatar2TrackingBodyModality Modality;
            public readonly CAPI.ovrAvatar2InputTrackingState InputTrackingState;
            public readonly CAPI.ovrAvatar2InputControlState InputControlState;
            public readonly CAPI.ovrAvatar2HandInputType LeftHandInputType;
            public readonly CAPI.ovrAvatar2HandInputType RightHandInputType;
            public readonly float LeftHandScale;
            public readonly float RightHandScale;
            public readonly CAPI.ovrAvatar2Transform[] LocalTransforms;

            public Snapshot(long sequence, CAPI.ovrAvatar2TrackingBodyModality modality,
                            CAPI.ovrAvatar2InputTrackingState inputTrackingState,
                            CAPI.ovrAvatar2InputControlState inputControlState,
                            CAPI.ovrAvatar2HandInputType leftHandInputType,
                            CAPI.ovrAvatar2HandInputType rightHandInputType,
                            float leftHandScale, float rightHandScale,
                            CAPI.ovrAvatar2Transform[] localTransforms)
                : this(sequence, modality, inputTrackingState, inputControlState, leftHandInputType,
                    rightHandInputType, leftHandScale, rightHandScale, localTransforms, null) { }

            internal Snapshot(long sequence, CAPI.ovrAvatar2TrackingBodyModality modality,
                            CAPI.ovrAvatar2InputTrackingState inputTrackingState,
                            CAPI.ovrAvatar2InputControlState inputControlState,
                            CAPI.ovrAvatar2HandInputType leftHandInputType,
                            CAPI.ovrAvatar2HandInputType rightHandInputType,
                            float leftHandScale, float rightHandScale,
                            CAPI.ovrAvatar2Transform[] localTransforms, Func<bool> validity)
            {
                Validity = validity;
                Sequence = sequence;
                Modality = modality;
                InputTrackingState = inputTrackingState;
                InputControlState = inputControlState;
                LeftHandInputType = leftHandInputType;
                RightHandInputType = rightHandInputType;
                LeftHandScale = leftHandScale;
                RightHandScale = rightHandScale;
                LocalTransforms = localTransforms;
            }
        }

        private readonly int _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        private SkeletonDefinition _skeleton;
        private long _lastPublishedSequence = -1;
        private Snapshot _snapshot;
        private int _retired;
        private object _publisher;

        public bool IsConfigured => Volatile.Read(ref _retired) == 0 && _skeleton != null;
        public bool HasPublishedPose => TryReadSnapshot(out _);

        internal bool TryClaimPublisher(object publisher, out string error)
        {
            if (IsRetired(out error) || !IsOwnerThread(out error)) return false;
            if (publisher == null || _publisher != null || _lastPublishedSequence >= 0)
            { error = "The SDK context requires one fresh publication owner."; return false; }
            _publisher = publisher;
            return true;
        }

        /// <summary>Configures exactly one frozen local-space tracking skeleton.</summary>
        public bool TryConfigure(SkeletonDefinition definition, out string error)
        {
            error = null;
            if (IsRetired(out error)) return false;
            if (!IsOwnerThread(out error)) return false;
            if (_skeleton != null)
            {
                error = "A body-tracking context has one immutable skeleton for its lifetime.";
                return false;
            }
            if (!TryFreezeSkeleton(definition, out SkeletonDefinition frozen, out error)) return false;
            _skeleton = frozen;
            return true;
        }

        /// <summary>
        /// Atomically publishes explicitly supplied body-state input and a complete
        /// Local-space pose. It does not infer headset/controller presence from seat data.
        /// </summary>
        public bool TryPublish(Snapshot snapshot, out string error)
            => TryPublishOwned(null, snapshot, out error);

        internal bool TryPublishOwned(object publisher, Snapshot snapshot, out string error)
        {
            if (!TryPrepareSnapshot(publisher, snapshot, out Snapshot frozen, out error)) return false;
            try
            {
                if (frozen.Validity != null && !frozen.Validity())
                { error = "The coordinated body input expired or lost publication ownership."; return false; }
            }
            catch (Exception)
            { error = "The coordinated body input validity fence failed."; return false; }
            _lastPublishedSequence = frozen.Sequence;
            Volatile.Write(ref _snapshot, frozen);
            return true;
        }

        // Numerical validation/deep copying is usable before a coordinated
        // native submit. It cannot consume a sequence or publish a callback.
        internal bool TryPrepareSnapshot(object publisher, Snapshot snapshot, out Snapshot frozen, out string error)
        {
            frozen = null;
            error = null;
            if (IsRetired(out error)) return false;
            if (!IsOwnerThread(out error)) return false;
            if (!ReferenceEquals(_publisher, publisher))
            { error = "Publish through this SDK context's exact owner."; return false; }
            if (_skeleton == null)
            {
                error = "Configure a skeleton before publishing body input.";
                return false;
            }
            if (snapshot == null || snapshot.Sequence < 0 ||
                snapshot.Sequence <= _lastPublishedSequence ||
                !IsValidModality(snapshot.Modality) ||
                !IsValidHandInput(snapshot.LeftHandInputType) ||
                !IsValidHandInput(snapshot.RightHandInputType) ||
                !FinitePositive(snapshot.LeftHandScale) || !FinitePositive(snapshot.RightHandScale) ||
                !IsValidInputTrackingState(snapshot.InputTrackingState) ||
                !IsValidInputControlState(snapshot.InputControlState) ||
                !TryFreezeTransforms(snapshot.LocalTransforms, _skeleton.Bones.Length,
                                    out CAPI.ovrAvatar2Transform[] transforms))
            {
                error = "Snapshot requires a newer sequence, explicit valid input state, and complete local transforms.";
                return false;
            }

            frozen = new Snapshot(snapshot.Sequence, snapshot.Modality,
                snapshot.InputTrackingState, snapshot.InputControlState,
                snapshot.LeftHandInputType, snapshot.RightHandInputType,
                snapshot.LeftHandScale, snapshot.RightHandScale, transforms, snapshot.Validity);
            return true;
        }

        /// <summary>
        /// Makes all SDK body callbacks report no input until a newer snapshot is
        /// published. The sequence remains monotonic within this context lifetime.
        /// </summary>
        public bool ClearPose(out string error)
            => ClearPoseOwned(null, out error);

        internal bool ClearPoseOwned(object publisher, out string error)
        {
            error = null;
            if (IsRetired(out error)) return false;
            if (!IsOwnerThread(out error)) return false;
            if (!ReferenceEquals(_publisher, publisher))
            { error = "Clear through this SDK context's exact publisher or owning manager."; return false; }
            Volatile.Write(ref _snapshot, null);
            return true;
        }

        protected override bool GetBodyState(OvrAvatarTrackingBodyState bodyState)
        {
            if (!TryReadSnapshot(out Snapshot snapshot)) return false;
            SkeletonDefinition skeleton = _skeleton;
            if (snapshot == null || skeleton == null) return false;

            bodyState.inputTrackingState = snapshot.InputTrackingState;
            bodyState.inputControlState = snapshot.InputControlState;
            bodyState.handInputType[(int)CAPI.ovrAvatar2Side.Left] = snapshot.LeftHandInputType;
            bodyState.handInputType[(int)CAPI.ovrAvatar2Side.Right] = snapshot.RightHandInputType;
            bodyState.handScale[(int)CAPI.ovrAvatar2Side.Left] = snapshot.LeftHandScale;
            bodyState.handScale[(int)CAPI.ovrAvatar2Side.Right] = snapshot.RightHandScale;
            bodyState.skeletonVersion = skeleton.Version;
            bodyState.numBones = skeleton.Bones.Length;
            bodyState.bodyModality = snapshot.Modality;
            return IsSnapshotCurrent(snapshot);
        }

        protected override bool GetBodySkeleton(ref OvrAvatarTrackingSkeleton skeleton)
        {
            if (!TryReadSnapshot(out Snapshot snapshot)) return false;
            SkeletonDefinition definition = _skeleton;
            if (snapshot == null || definition == null ||
                skeleton.bones.Length != definition.Bones.Length ||
                skeleton.bones.Address == IntPtr.Zero ||
                skeleton.referencePose.transforms.Length != definition.LocalReferencePose.Length ||
                skeleton.referencePose.transforms.Address == IntPtr.Zero)
            {
                return false;
            }

            skeleton.forwardDir = definition.Forward;
            skeleton.referencePose.space = CAPI.ovrAvatar2Space.Local;
            for (int index = 0; index < definition.Bones.Length; ++index)
            {
                if (!skeleton.SetBone(definition.Bones[index], index) ||
                    !skeleton.referencePose.SetTransform(definition.LocalReferencePose[index], index))
                {
                    return false;
                }
            }
            return IsSnapshotCurrent(snapshot);
        }

        protected override bool GetBodyPose(ref OvrAvatarTrackingPose pose)
        {
            if (!TryReadSnapshot(out Snapshot snapshot)) return false;
            if (snapshot == null || pose.transforms.Length != snapshot.LocalTransforms.Length ||
                pose.transforms.Address == IntPtr.Zero)
            {
                return false;
            }

            pose.space = CAPI.ovrAvatar2Space.Local;
            for (int index = 0; index < snapshot.LocalTransforms.Length; ++index)
            {
                if (!pose.SetTransform(snapshot.LocalTransforms[index], index)) return false;
            }
            return IsSnapshotCurrent(snapshot);
        }

        private bool TryReadSnapshot(out Snapshot snapshot)
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (snapshot == null || !IsSnapshotCurrent(snapshot)) { snapshot = null; return false; }
            return true;
        }

        private bool IsSnapshotCurrent(Snapshot snapshot)
        {
            bool valid = Volatile.Read(ref _retired) == 0 &&
                ReferenceEquals(Volatile.Read(ref _snapshot), snapshot);
            // A callback validity fence is pure managed identity + the pinned
            // native monotonic clock. Never read Unity objects or solve here.
            try { valid = valid && (snapshot.Validity == null || snapshot.Validity()); }
            catch (Exception) { valid = false; }
            if (!valid) Interlocked.CompareExchange(ref _snapshot, null, snapshot);
            return valid;
        }

        protected override void Dispose(bool disposing)
        {
            // The SDK's callback registry is not concurrent. Installation and
            // detachment must share this context's owner thread too. A caller
            // must detach the SDK entity before disposing the context.
            if (disposing && !IsOwnerThread(out string error))
                throw new InvalidOperationException(error);
            if (disposing && _publisher != null)
                throw new InvalidOperationException("Detach the owning receiver-body manager before disposing its SDK context.");
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                Volatile.Write(ref _snapshot, null);
            }
            base.Dispose(disposing);
        }

        internal void DisposeAfterDetach()
        {
            if (!IsOwnerThread(out string error)) throw new InvalidOperationException(error);
            _publisher = null;
            Dispose();
        }

        private bool IsRetired(out string error)
        {
            error = Volatile.Read(ref _retired) == 0 ? null : "This body-tracking context has been retired.";
            return error != null;
        }

        private bool IsOwnerThread(out string error)
        {
            error = Thread.CurrentThread.ManagedThreadId == _ownerThreadId ? null :
                "Configure, publish, and clear must run on this context's owner thread.";
            return error == null;
        }

        private static bool TryFreezeSkeleton(SkeletonDefinition source,
                                              out SkeletonDefinition frozen, out string error)
        {
            frozen = null;
            error = null;
            if (source == null || source.Version < 0 || source.Bones == null ||
                source.LocalReferencePose == null || source.Bones.Length == 0 ||
                source.Bones.Length > MaximumBones ||
                source.Bones.Length != source.LocalReferencePose.Length || !FiniteNonZero(source.Forward))
            {
                error = "Skeleton requires a bounded versioned hierarchy and matching local reference pose.";
                return false;
            }

            var ids = new HashSet<CAPI.ovrAvatar2JointType>();
            var bones = new CAPI.ovrAvatar2Bone[source.Bones.Length];
            for (int index = 0; index < source.Bones.Length; ++index)
            {
                CAPI.ovrAvatar2Bone bone = source.Bones[index];
                if (!IsRealBoneId(bone.boneId) || !ids.Add(bone.boneId) ||
                    (bone.parentBoneIndex != -1 &&
                     (bone.parentBoneIndex < 0 || bone.parentBoneIndex >= index)))
                {
                    error = "Skeleton bones require unique real IDs and earlier parents or one root marker.";
                    return false;
                }
                bones[index] = bone;
            }
            if (bones[0].parentBoneIndex != -1)
            {
                error = "The first tracking bone must be the sole root.";
                return false;
            }
            for (int index = 1; index < bones.Length; ++index)
            {
                if (bones[index].parentBoneIndex == -1)
                {
                    error = "Tracking skeletons require exactly one root.";
                    return false;
                }
            }
            if (!TryFreezeTransforms(source.LocalReferencePose, bones.Length,
                                     out CAPI.ovrAvatar2Transform[] reference))
            {
                error = "Skeleton reference transforms must be complete finite Local-space transforms.";
                return false;
            }
            frozen = new SkeletonDefinition(source.Version, source.Forward, bones, reference);
            return true;
        }

        private static bool TryFreezeTransforms(CAPI.ovrAvatar2Transform[] source, int expectedLength,
                                                out CAPI.ovrAvatar2Transform[] frozen)
        {
            frozen = null;
            if (source == null || source.Length != expectedLength) return false;
            frozen = new CAPI.ovrAvatar2Transform[source.Length];
            for (int index = 0; index < source.Length; ++index)
            {
                if (!IsValidTransform(source[index]))
                {
                    frozen = null;
                    return false;
                }
                frozen[index] = source[index];
            }
            return true;
        }

        private static bool IsRealBoneId(CAPI.ovrAvatar2JointType id) =>
            id > CAPI.ovrAvatar2JointType.Invalid && id < CAPI.ovrAvatar2JointType.Count;
        private static bool IsValidModality(CAPI.ovrAvatar2TrackingBodyModality modality) =>
            modality == CAPI.ovrAvatar2TrackingBodyModality.Sitting ||
            modality == CAPI.ovrAvatar2TrackingBodyModality.Standing;
        private static bool IsValidHandInput(CAPI.ovrAvatar2HandInputType input) =>
            input == CAPI.ovrAvatar2HandInputType.Controller ||
            input == CAPI.ovrAvatar2HandInputType.Tracking ||
            input == CAPI.ovrAvatar2HandInputType.Custom;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool FinitePositive(float value) => Finite(value) && value > 0f;
        private static bool FiniteNonZero(CAPI.ovrAvatar2Vector3f value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z) &&
            (value.x != 0f || value.y != 0f || value.z != 0f);
        private static bool IsFiniteTransform(CAPI.ovrAvatar2Transform value) =>
            Finite(value.position.x) && Finite(value.position.y) && Finite(value.position.z) &&
            Finite(value.orientation.x) && Finite(value.orientation.y) &&
            Finite(value.orientation.z) && Finite(value.orientation.w) &&
            Finite(value.scale.x) && Finite(value.scale.y) && Finite(value.scale.z);
        private static bool IsValidTransform(CAPI.ovrAvatar2Transform value)
        {
            float magnitude = value.orientation.x * value.orientation.x + value.orientation.y * value.orientation.y +
                value.orientation.z * value.orientation.z + value.orientation.w * value.orientation.w;
            return IsFiniteTransform(value) && Math.Abs(magnitude - 1f) <= .001f &&
                FinitePositive(value.scale.x) && FinitePositive(value.scale.y) && FinitePositive(value.scale.z);
        }

        private static bool IsValidInputTrackingState(CAPI.ovrAvatar2InputTrackingState state)
        {
            // Inactive default-zero transforms do not assert presence. Still
            // refuse nonfinite bytes rather than sending NaNs to the SDK.
            // Active or visible inputs must also be usable rigid poses.
            return IsFiniteTransform(state.headset) && IsFiniteTransform(state.leftController) &&
                IsFiniteTransform(state.rightController) &&
                (!state.headsetActive || IsValidTransform(state.headset)) &&
                (!(state.leftControllerActive || state.leftControllerVisible) ||
                 IsValidTransform(state.leftController)) &&
                (!(state.rightControllerActive || state.rightControllerVisible) ||
                 IsValidTransform(state.rightController));
        }

        private static bool IsValidInputControlState(CAPI.ovrAvatar2InputControlState state)
        {
            return state.type != CAPI.ovrAvatar2ControllerType.Invalid &&
                Enum.IsDefined(typeof(CAPI.ovrAvatar2ControllerType), state.type) &&
                IsValidControllerState(state.leftControllerState) &&
                IsValidControllerState(state.rightControllerState);
        }

        private static bool IsValidControllerState(CAPI.ovrAvatar2ControllerState state)
        {
            const int buttonMask = (int)(CAPI.ovrAvatar2Button.One | CAPI.ovrAvatar2Button.Two |
                CAPI.ovrAvatar2Button.Three | CAPI.ovrAvatar2Button.Joystick);
            const int touchMask = (int)(CAPI.ovrAvatar2Touch.One | CAPI.ovrAvatar2Touch.Two |
                CAPI.ovrAvatar2Touch.Joystick | CAPI.ovrAvatar2Touch.ThumbRest |
                CAPI.ovrAvatar2Touch.Index | CAPI.ovrAvatar2Touch.Pointing | CAPI.ovrAvatar2Touch.ThumbUp);
            return (((int)state.buttonMask & ~buttonMask) == 0) &&
                (((int)state.touchMask & ~touchMask) == 0) && Finite(state.joystickX) &&
                Finite(state.joystickY) && Finite(state.indexTrigger) && Finite(state.handTrigger);
        }
    }
}
