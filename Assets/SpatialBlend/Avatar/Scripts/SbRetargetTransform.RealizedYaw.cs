// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend
{
    public partial class SbRetargetTransform
    {
        internal enum RealizationContract
        {
            ReadOnlyOrAsynchronous = 0,
            SynchronousExclusiveRootAndHead = 1
        }

        internal enum RealizationResult
        {
            Committed,
            Unsupported,
            InvalidInput,
            AmbiguousMotion,
            StaleLease,
            Busy,
            Invalidated
        }

        /// <summary>
        /// Committed application realization, not SDK skeletal heading, planned motion,
        /// or a native-accepted sample. Incarnation changes with the exclusive owner;
        /// revision changes only after a checked root/head commit completes.
        /// </summary>
        internal readonly struct RealizedSample
        {
            internal readonly Pose RootPlacement;
            internal readonly Pose Head;
            internal readonly double RootYawRadians;
            internal readonly double HeadYawRadians;
            internal readonly ulong Incarnation;
            internal readonly ulong Revision;

            internal RealizedSample(Pose root, Pose head, double rootYaw, double headYaw,
                                    ulong incarnation, ulong revision)
            {
                RootPlacement = root;
                Head = head;
                RootYawRadians = rootYaw;
                HeadYawRadians = headYaw;
                Incarnation = incarnation;
                Revision = revision;
            }
        }

        /// <summary>
        /// Explicit admission asserts that this synchronous writer owns every root/head
        /// mutation, including their parents, on the Unity thread. Quaternion endpoints
        /// alone never establish that contract. Old methods cannot write these channels
        /// while the lease is held.
        /// </summary>
        internal sealed class RealizationLease : IDisposable
        {
            private readonly SbRetargetTransform _retarget;
            internal readonly object Owner;
            internal readonly ulong Incarnation;
            internal readonly Transform Head;
            internal readonly Transform Parent;
            internal bool Invalid;
            internal bool ReleaseRequested;
            internal Pose RootPose;
            internal Pose HeadPose;
            internal double RootYaw;
            internal double HeadYaw;
            internal ulong Revision;

            internal RealizationLease(SbRetargetTransform retarget, object owner, ulong incarnation,
                                      Pose root, Pose head)
            {
                _retarget = retarget;
                Owner = owner;
                Incarnation = incarnation;
                Head = retarget.headTransform;
                Parent = Head.parent;
                RootPose = root;
                HeadPose = head;
                RootYaw = WrappedYaw(root.rotation);
                HeadYaw = WrappedYaw(head.rotation);
            }

            internal RealizationResult TryRead(out RealizedSample sample) =>
                _retarget.ReadRealization(this, out sample);

            /// <summary>
            /// A signed per-commit path witness, not a target or a remaining turn. Both
            /// the witness and the measured increment must be strictly below pi.
            /// </summary>
            internal RealizationResult TryMoveRoot(Vector3 position, Quaternion rotation,
                                                   double signedYawStepRadians) =>
                _retarget.MoveRealizedRoot(this, position, rotation, signedYawStepRadians, false);

            internal RealizationResult TryPlaceRoot(Vector3 position, Quaternion rotation) =>
                _retarget.MoveRealizedRoot(this, position, rotation, 0d, true);

            internal RealizationResult TryMoveHead(Vector3 position, double signedYawStepRadians) =>
                _retarget.MoveRealizedHead(this, position, signedYawStepRadians, false);

            internal RealizationResult TryReleaseHead(double signedYawStepRadians) =>
                _retarget.MoveRealizedHead(this, default, signedYawStepRadians, true);

            /// <summary>Absolute placement, lifted by actual readback into the same continuous domain.</summary>
            internal RealizationResult TryPlaceHead(Vector3 position, double absoluteUnityYawRadians) =>
                _retarget.PlaceRealizedHead(this, position, absoluteUnityYawRadians);

            public void Dispose() => _retarget.ReleaseRealization(this);
        }

        // Numeric validation of float Transform projection, not a motion/arrival threshold.
        private const double RealizationYawRoundoff = 0.00001d;
        private const double Tau = 2d * Math.PI;
        private RealizationLease _realizationLease;
        private ulong _realizationIncarnation;
        private bool _realizationCommitInProgress;

        internal bool TryAcquireCheckedRealization(object owner, RealizationContract contract,
                                                   out RealizationLease lease)
        {
            lease = null;
            if (owner == null || contract != RealizationContract.SynchronousExclusiveRootAndHead ||
                GetType() != typeof(SbRetargetTransform) || !isActiveAndEnabled ||
                _realizationLease != null || _foundationRetargetOwner != null ||
                _foundationObservationPresenceMode || _anchorTargetOverridesEnabled ||
                _headTargetActive || !_hasRootPlacement || !_hasHeadBaseline || headTransform == null ||
                _realizationIncarnation == ulong.MaxValue ||
                (_hasHeadBaseline && !SameRotation(_headLocalRotation, Quaternion.identity)) ||
                HandCanMoveHead(leftHandTransform) || HandCanMoveHead(rightHandTransform))
            {
                return false;
            }

            var head = new Pose(headTransform.position, headTransform.rotation);
            Pose expected = HeadAfterRoot(_rootPlacement, false, default);
            if (!IsValidPose(_rootPlacement) || !IsValidPose(head) || !HasYaw(_rootPlacement.rotation) ||
                !HasYaw(head.rotation) || !SamePose(head, expected)) return false;

            lease = new RealizationLease(this, owner, ++_realizationIncarnation, _rootPlacement, head);
            _realizationLease = lease;
            // As with the existing bridge, root and head now have distinct composition
            // inputs. No Transform is changed by admission.
            _anchorTargetOverridesEnabled = true;
            return true;
        }

        // A legacy bridge must not clear a foreign Foundation owner's targets even
        // transiently while preparing a checked admission that will be refused.
        internal bool CanSuspendLegacyBridge => _realizationLease == null &&
            _foundationRetargetOwner == null && !_foundationObservationPresenceMode;

        private bool HandCanMoveHead(Transform hand) =>
            hand != null && (ReferenceEquals(hand, headTransform) || headTransform.IsChildOf(hand));

        private bool RejectUnleasedHeadMutation()
        {
            if (_realizationLease == null) return false;
            _realizationLease.Invalid = true;
            return true;
        }

        private bool CanWriteUnrelatedHands()
        {
            if (_realizationLease == null) return true;
            RealizationResult result = CheckRealization(_realizationLease);
            if (result == RealizationResult.Committed) return true;
            _realizationLease.Invalid = true;
            return false;
        }

        private RealizationResult CheckRealization(RealizationLease lease)
        {
            if (!ReferenceEquals(_realizationLease, lease)) return RealizationResult.StaleLease;
            if (_realizationCommitInProgress) return RealizationResult.Busy;
            if (lease.Invalid) return RealizationResult.Invalidated;
            if (this == null || !isActiveAndEnabled || headTransform == null ||
                !ReferenceEquals(headTransform, lease.Head) ||
                !ReferenceEquals(headTransform.parent, lease.Parent) ||
                HandCanMoveHead(leftHandTransform) || HandCanMoveHead(rightHandTransform) ||
                !SameObservedPose(_rootPlacement, lease.RootPose) ||
                !SameObservedPose(new Pose(headTransform.position, headTransform.rotation), lease.HeadPose))
            {
                lease.Invalid = true;
                return RealizationResult.Invalidated;
            }
            return RealizationResult.Committed;
        }

        private RealizationResult ReadRealization(RealizationLease lease, out RealizedSample sample)
        {
            sample = default;
            RealizationResult result = CheckRealization(lease);
            if (result != RealizationResult.Committed) return result;
            sample = new RealizedSample(lease.RootPose, lease.HeadPose, lease.RootYaw, lease.HeadYaw,
                lease.Incarnation, lease.Revision);
            return result;
        }

        private RealizationResult MoveRealizedRoot(RealizationLease lease, Vector3 position,
                                                   Quaternion rotation, double step, bool placement)
        {
            RealizationResult result = CheckRealization(lease);
            if (result != RealizationResult.Committed) return result;
            if (!IsFinite(position) || !IsUsableRotation(rotation) || !Finite(step)) return RealizationResult.InvalidInput;
            if (!placement && Math.Abs(step) >= Math.PI) return RealizationResult.AmbiguousMotion;
            rotation.Normalize();
            if (!HasYaw(rotation)) return RealizationResult.InvalidInput;
            var root = new Pose(position, rotation);
            Pose head = HeadAfterRoot(root, placement, default);
            if (!IsValidPose(head) || !HasYaw(head.rotation)) return RealizationResult.InvalidInput;
            double rootDelta = Delta(WrappedYaw(lease.RootPose.rotation), WrappedYaw(rotation));
            double headDelta = Delta(WrappedYaw(lease.HeadPose.rotation), WrappedYaw(head.rotation));
            if (!placement)
            {
                double headStep = _headTargetActive ? 0d : step;
                result = CheckStep(rootDelta, step);
                if (result != RealizationResult.Committed) return result;
                result = CheckStep(headDelta, headStep);
                if (result != RealizationResult.Committed) return result;
            }
            return CommitRealization(lease, root, head, placement, () =>
            {
                if (placement) SetHeadTransformCore(position, rotation);
                else ApplyRootRealizationTransformCore(position, rotation);
            });
        }

        private RealizationResult MoveRealizedHead(RealizationLease lease, Vector3 position,
                                                   double step, bool release)
        {
            RealizationResult result = CheckRealization(lease);
            if (result != RealizationResult.Committed) return result;
            if (!Finite(step) || (!release && !IsFinite(position))) return RealizationResult.InvalidInput;
            if (Math.Abs(step) >= Math.PI) return RealizationResult.AmbiguousMotion;
            double yaw = lease.HeadYaw + step;
            if (!Finite(yaw)) return RealizationResult.InvalidInput;
            var target = new Pose(position, Quaternion.Euler(0f, (float)(Wrap(yaw) * 180d / Math.PI), 0f));
            Pose head = HeadAfterRoot(_rootPlacement, false, release ? (Pose?)null : target, true);
            if (!IsValidPose(head) || !HasYaw(head.rotation)) return RealizationResult.InvalidInput;
            double actual = Delta(WrappedYaw(lease.HeadPose.rotation), WrappedYaw(head.rotation));
            result = CheckStep(actual, step);
            if (result != RealizationResult.Committed) return result;
            return CommitRealization(lease, _rootPlacement, head, false, () =>
            {
                if (release) ClearAnchorTargetCore(SbRetargetAnchorTargetBridge.HeadAnchor);
                else SetAnchorTargetCore(SbRetargetAnchorTargetBridge.HeadAnchor, target, 1f);
            });
        }

        private RealizationResult PlaceRealizedHead(RealizationLease lease, Vector3 position, double absoluteYaw)
        {
            RealizationResult result = CheckRealization(lease);
            if (result != RealizationResult.Committed) return result;
            if (!IsFinite(position) || !Finite(absoluteYaw)) return RealizationResult.InvalidInput;
            var target = new Pose(position, Quaternion.Euler(0f, (float)(Wrap(absoluteYaw) * 180d / Math.PI), 0f));
            Pose head = HeadAfterRoot(_rootPlacement, false, target, true);
            if (!IsValidPose(head) || !HasYaw(head.rotation)) return RealizationResult.InvalidInput;
            // No ordinary path witness is invented for a placement, including +pi.
            // CommitRealization measures the completed final head, preserving baseline
            // pitch/roll and the existing exact-half-turn positive tie convention.
            return CommitRealization(lease, _rootPlacement, head, true, () =>
                SetAnchorTargetCore(SbRetargetAnchorTargetBridge.HeadAnchor, target, 1f));
        }

        // The checked path admits only the existing direct-head baseline (identity
        // local rotation) and full-authority head target; asynchronous baselines and
        // partial-weight controllers do not gain a winding capability by observation.
        private Pose HeadAfterRoot(Pose root, bool placement, Pose? target, bool replaceTarget = false)
        {
            Pose baseline = !placement && _hasHeadBaseline
                ? new Pose(root.position + root.rotation * _headLocalPosition, root.rotation * _headLocalRotation)
                : root;
            bool active = replaceTarget ? target.HasValue : _headTargetActive;
            Pose goal = replaceTarget ? target.GetValueOrDefault() : _headTarget;
            float weight = replaceTarget ? 1f : _headTargetWeight;
            return new Pose(BlendPosition(baseline, goal, weight, active),
                BlendHeadRotation(baseline, goal, weight, active));
        }

        private RealizationResult CommitRealization(RealizationLease lease, Pose expectedRoot,
                                                    Pose expectedHead, bool placement, Action write)
        {
            if (lease.Revision == ulong.MaxValue)
            {
                lease.Invalid = true;
                return RealizationResult.Invalidated;
            }
            _realizationCommitInProgress = true;
            try
            {
                write();
                if (lease.Invalid || !ReferenceEquals(_realizationLease, lease) || this == null ||
                    !isActiveAndEnabled || headTransform == null ||
                    !ReferenceEquals(headTransform, lease.Head) ||
                    !ReferenceEquals(headTransform.parent, lease.Parent) ||
                    HandCanMoveHead(leftHandTransform) || HandCanMoveHead(rightHandTransform))
                {
                    lease.Invalid = true;
                    return RealizationResult.Invalidated;
                }
                var head = new Pose(headTransform.position, headTransform.rotation);
                if (!SamePose(_rootPlacement, expectedRoot) || !SamePose(head, expectedHead))
                {
                    lease.Invalid = true;
                    return RealizationResult.Invalidated;
                }
                double rootDelta = Delta(WrappedYaw(lease.RootPose.rotation), WrappedYaw(_rootPlacement.rotation));
                double headDelta = Delta(WrappedYaw(lease.HeadPose.rotation), WrappedYaw(head.rotation));
                if (!placement && (Math.Abs(rootDelta) >= Math.PI || Math.Abs(headDelta) >= Math.PI))
                {
                    lease.Invalid = true;
                    return RealizationResult.Invalidated;
                }
                // Only the completed readback advances continuous state. A pending
                // target, refused step or native publication cannot change it.
                lease.RootYaw += rootDelta;
                lease.HeadYaw += headDelta;
                lease.RootPose = _rootPlacement;
                lease.HeadPose = head;
                ++lease.Revision;
                return RealizationResult.Committed;
            }
            catch
            {
                lease.Invalid = true;
                throw;
            }
            finally
            {
                _realizationCommitInProgress = false;
                if (lease.ReleaseRequested) ReleaseRealization(lease);
            }
        }

        private void ReleaseRealization(RealizationLease lease)
        {
            if (!ReferenceEquals(_realizationLease, lease)) return;
            if (_realizationCommitInProgress)
            {
                lease.Invalid = true;
                lease.ReleaseRequested = true;
                return;
            }
            try
            {
                if (CheckRealization(lease) == RealizationResult.Committed)
                {
                    // Restore from the checked root and genuine local baseline while
                    // ownership is still held. After overrides are disabled, ApplyHead
                    // would instead use the targeted visible head as the legacy root.
                    Pose baseline = HeadAfterRoot(lease.RootPose, false, null, true);
                    lease.Head.SetPositionAndRotation(baseline.position, baseline.rotation);
                    // Fusion hand baselines are head-relative. Clear the retired
                    // Facing target before recomposing them from the restored actual
                    // head, but leave each explicit hand target's own priority intact.
                    _headTargetActive = false;
                    _headTargetWeight = 0f;
                    ApplyLeftHand();
                    ApplyRightHand();
                }
            }
            finally
            {
                // An invalidated owner releases its bookkeeping without rewriting
                // foreign state. Disposal never invents a new observation or revision.
                lease.Invalid = true;
                _realizationLease = null;
                _headTargetActive = false;
                _headTargetWeight = 0f;
                _anchorTargetOverridesEnabled = false;
            }
        }

        private static RealizationResult CheckStep(double measured, double witness)
        {
            if (Math.Abs(measured) >= Math.PI) return RealizationResult.AmbiguousMotion;
            return Math.Abs(measured - witness) <= RealizationYawRoundoff
                ? RealizationResult.Committed : RealizationResult.InvalidInput;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double WrappedYaw(Quaternion rotation) => rotation.eulerAngles.y * Math.PI / 180d;
        private static double Wrap(double value) => value - Tau * Math.Floor(value / Tau);
        private static double Delta(double from, double to)
        {
            double value = Wrap(to - from);
            return value > Math.PI ? value - Tau : value; // exact half-turn chooses +pi
        }

        private static bool HasYaw(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            // Only the float-projection singularity is excluded; no motion cap or
            // smoothing threshold is introduced by the measurement capability.
            return forward.x * forward.x + forward.z * forward.z > 0.000000000001f;
        }

        private static bool SameObservedPose(Pose a, Pose b) =>
            a.position.Equals(b.position) && (a.rotation.Equals(b.rotation) ||
                (a.rotation.x == -b.rotation.x && a.rotation.y == -b.rotation.y &&
                 a.rotation.z == -b.rotation.z && a.rotation.w == -b.rotation.w));

        private static bool SameRotation(Quaternion a, Quaternion b) =>
            IsUsableRotation(a) && IsUsableRotation(b) && Math.Abs(Quaternion.Dot(a, b)) >= 1f - 0.000001f;

        private static bool SamePose(Pose a, Pose b) =>
            IsValidPose(a) && IsValidPose(b) && (a.position - b.position).sqrMagnitude <= 0.0000000001f &&
            SameRotation(a.rotation, b.rotation);
    }
}
