// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public class ArticulatedTargetControllerTests
    {
        private GameObject _actor;
        private AnchorTargetController _targets;
        private ArticulatedTargetController _consumer;
        private ArticulatedTargetController.Rig _rig;

        [SetUp]
        public void SetUp()
        {
            _actor = new GameObject("actor-root");
            _targets = _actor.AddComponent<AnchorTargetController>();
            _consumer = _actor.AddComponent<ArticulatedTargetController>();
            _rig = BuildSyntheticCalibratedRig(_actor.transform);
            _consumer.Configure(_targets, _rig);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_actor);
        }

        [Test]
        public void HandTarget_SolvesConfiguredChain_PreservingSegmentLengthsAndOtherTracking()
        {
            float upperLength =
                Vector3.Distance(_rig.LeftArm.Upper.position, _rig.LeftArm.Lower.position);
            float lowerLength =
                Vector3.Distance(_rig.LeftArm.Lower.position, _rig.LeftArm.End.position);
            Vector3 trackedHead = _rig.Head.position;
            Vector3 rightHand = _rig.RightArm.End.position;
            Vector3 target = _rig.LeftArm.Upper.position + new Vector3(-0.45f, 0.1f, 1.55f);

            _targets.ApplyTarget("hand_l", target, 0f, true, 1d, 10d);

            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, target), Is.LessThan(0.002f));
            Assert.That(Vector3.Distance(_rig.LeftArm.Upper.position, _rig.LeftArm.Lower.position),
                        Is.EqualTo(upperLength).Within(0.002f));
            Assert.That(Vector3.Distance(_rig.LeftArm.Lower.position, _rig.LeftArm.End.position),
                        Is.EqualTo(lowerLength).Within(0.002f));
            Assert.That(_rig.Head.position, Is.EqualTo(trackedHead));
            Assert.That(_rig.RightArm.End.position, Is.EqualTo(rightHand));
        }

        [Test]
        public void UnreachableHandTarget_IsRejectedWithoutPartialBoneMutation()
        {
            Quaternion before = _rig.LeftArm.Upper.rotation;
            int rejected = 0;
            _consumer.TargetRejected += (_, _) => rejected++;

            _targets.ApplyTarget("hand_l", _rig.LeftArm.Upper.position + Vector3.forward * 10f, 0f,
                                 true, 1d, 10d);

            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(_rig.LeftArm.Upper.rotation, Is.EqualTo(before));
        }

        [Test]
        public void ExplicitSeatContact_CalibratesNonzeroOffset_KeepsRootAndUnownedHeadWorldStable()
        {
            _rig.SeatContact.localPosition = new Vector3(0f, -0.25f, 0.1f);
            Vector3 root = _actor.transform.position;
            Vector3 trackedHead = _rig.Head.position;
            Vector3 contact = _rig.SeatContact.position + new Vector3(0.1f, 0f, 0f);

            _targets.ApplyTarget("seat_contact", contact, 0f, true, 1d, 10d);

            Assert.That(_consumer.HasSeatCalibration, Is.True);
            Assert.That(_consumer.HipToSeatContact.sqrMagnitude, Is.GreaterThan(0.001f));
            Assert.That(Vector3.Distance(_rig.SeatContact.position, contact), Is.LessThan(0.002f));
            Assert.That(_actor.transform.position, Is.EqualTo(root));
            Assert.That(_rig.Head.position, Is.EqualTo(trackedHead));
        }

        [Test]
        public void SyntheticCalibratedRig_HandAuthoredMappedContactPreservesFeetAndUnownedTracking()
        {
            // This toy rig is deliberately synthetic and calibrated.  The goal is a
            // hand-authored mapped contact, not a captured device pose or finished clip.
            _rig.SeatContact.localPosition = new Vector3(.18f, -.28f, .12f);
            Pose headBefore = new Pose(_rig.Head.position, _rig.Head.rotation);
            Pose otherHandBefore = new Pose(_rig.RightArm.End.position, _rig.RightArm.End.rotation);
            Vector3 rootBefore = _actor.transform.position;
            Vector3 leftFootBefore = _rig.LeftLeg.End.position;
            Vector3 rightFootBefore = _rig.RightLeg.End.position;
            float leftUpper = Vector3.Distance(_rig.LeftLeg.Upper.position, _rig.LeftLeg.Lower.position);
            float leftLower = Vector3.Distance(_rig.LeftLeg.Lower.position, _rig.LeftLeg.End.position);
            float rightUpper = Vector3.Distance(_rig.RightLeg.Upper.position, _rig.RightLeg.Lower.position);
            float rightLower = Vector3.Distance(_rig.RightLeg.Lower.position, _rig.RightLeg.End.position);
            Quaternion contactFacing = Quaternion.Euler(0f, 70f, 0f);
            Vector3 contactPlaneGoal = _rig.SeatContact.position + new Vector3(.10f, -.08f, .12f);
            Vector3 calibratedOffset = _rig.SeatContact.localPosition;
            Vector3 expectedHip = contactPlaneGoal - contactFacing * calibratedOffset;
            var standing = new GameObject("synthetic-calibrated-standing-reference");
            try
            {
                standing.transform.SetPositionAndRotation(expectedHip + new Vector3(.03f, 0f, -.02f),
                    Quaternion.Euler(0f, 18f, 0f));
                _rig.StandingHipReference = standing.transform;

                _targets.ApplyTarget("seat_contact", contactPlaneGoal, 70f, true, 1d, 10d);

                Assert.That(_consumer.HasSeatCalibration, Is.True);
                Assert.That(Vector3.Distance(_consumer.HipToSeatContact, calibratedOffset),
                    Is.LessThan(.002f), "Calibration must retain the nonzero hip-to-contact offset.");
                Assert.That(Vector3.Distance(_rig.SeatContact.position, contactPlaneGoal), Is.LessThan(.002f),
                    "The carried contact must align with the hand-authored mapped goal.");
                Assert.That(Vector3.Distance(_rig.Hip.position, expectedHip), Is.LessThan(.002f));
                Assert.That(Quaternion.Angle(_rig.Hip.rotation, contactFacing), Is.LessThan(.02f));
                Assert.That(_actor.transform.position, Is.EqualTo(rootBefore));
                Assert.That(Vector3.Distance(_rig.Head.position, headBefore.position),
                    Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(_rig.Head.rotation, headBefore.rotation),
                    Is.LessThan(.02f));
                // Restoring a child after its parent rotates can differ by IEEE
                // float round-off even when its world pose is preserved.
                Assert.That(Vector3.Distance(_rig.RightArm.End.position, otherHandBefore.position),
                    Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(_rig.RightArm.End.rotation, otherHandBefore.rotation),
                    Is.LessThan(.02f));
                Assert.That(Vector3.Distance(_rig.LeftLeg.End.position, leftFootBefore),
                    Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(_rig.RightLeg.End.position, rightFootBefore),
                    Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(_rig.LeftLeg.Upper.position, _rig.LeftLeg.Lower.position),
                    Is.EqualTo(leftUpper).Within(.002f));
                Assert.That(Vector3.Distance(_rig.LeftLeg.Lower.position, _rig.LeftLeg.End.position),
                    Is.EqualTo(leftLower).Within(.002f));
                Assert.That(Vector3.Distance(_rig.RightLeg.Upper.position, _rig.RightLeg.Lower.position),
                    Is.EqualTo(rightUpper).Within(.002f));
                Assert.That(Vector3.Distance(_rig.RightLeg.Lower.position, _rig.RightLeg.End.position),
                    Is.EqualTo(rightLower).Within(.002f));

                _targets.CancelTarget("seat_contact");
                _consumer.Tick(20d);
                _consumer.Tick(20.3d);
                Assert.That(Vector3.Distance(_rig.Hip.position, standing.transform.position),
                    Is.LessThan(.002f), "Release must use the live standing reference.");
                Assert.That(Quaternion.Angle(_rig.Hip.rotation, standing.transform.rotation), Is.LessThan(.02f));
            }
            finally { Object.DestroyImmediate(standing); }
        }

        [Test]
        public void SeatRelease_UsesLiveStandingReferenceInsteadOfCapturedPose()
        {
            var standing = new GameObject("live-standing-reference");
            standing.transform.position = _rig.Hip.position + new Vector3(0.15f, 0f, 0f);
            _rig.StandingHipReference = standing.transform;
            _targets.ApplyTarget("pelvis", _rig.Hip.position + new Vector3(-0.1f, 0f, 0f), 0f, true,
                                 1d, 10d);
            standing.transform.position += new Vector3(0.05f, 0f, 0f);
            _targets.CancelTarget("pelvis");
            _consumer.Tick(10d);
            _consumer.Tick(10.3d);

            Assert.That(Vector3.Distance(_rig.Hip.position, standing.transform.position),
                        Is.LessThan(0.002f));
            Object.DestroyImmediate(standing);
        }

        [Test]
        public void CancelRelease_RelinquishesLimbWithoutRestoringAnOldTrackedPose()
        {
            Vector3 target = _rig.LeftArm.Upper.position + new Vector3(-0.3f, 0f, 1.6f);
            _targets.ApplyTarget("hand_l", target, 0f, true, 1d, 10d);
            Quaternion newerTrackedRotation = Quaternion.Euler(12f, 18f, 0f);
            _rig.LeftArm.Upper.rotation = newerTrackedRotation;

            _targets.Tick(1e9d);
            Assert.That(_targets.TryGetTarget("hand_l", out _), Is.True, "no time-based expiry");
            _targets.CancelTarget("hand_l");

            Assert.That(_targets.TryGetTarget("hand_l", out _), Is.False);
            Assert.That(_rig.LeftArm.Upper.rotation, Is.EqualTo(newerTrackedRotation),
                        "Release must only relinquish ownership; it cannot replay a captured " +
                            "pre-target tracking pose.");
        }

        [Test]
        public void ActiveHandAndPelvis_ConvergeInEitherCallbackOrder()
        {
            Vector3 hand = _rig.LeftArm.Upper.position + new Vector3(-0.3f, 0f, 1.6f);
            Vector3 hip = _rig.Hip.position + new Vector3(0.1f, 0f, 0f);
            Vector3 leftFoot = _rig.LeftLeg.End.position;
            float leftUpper =
                Vector3.Distance(_rig.LeftLeg.Upper.position, _rig.LeftLeg.Lower.position);
            int rejected = 0;
            _consumer.TargetRejected += (_, _) => rejected++;
            _targets.ApplyTarget("hand_l", hand, 0f, true, 1d, 10d);
            _targets.ApplyTarget("pelvis", hip, 0f, true, 1d, 11d);
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, hand), Is.LessThan(.003f));
            Assert.That(_rig.Hip.position, Is.EqualTo(hip));
            Assert.That(Vector3.Distance(_rig.LeftLeg.End.position, leftFoot), Is.LessThan(.002f));
            Assert.That(Vector3.Distance(_rig.LeftLeg.Upper.position, _rig.LeftLeg.Lower.position),
                        Is.EqualTo(leftUpper).Within(.003f));
            Assert.That(rejected, Is.EqualTo(0));

            Object.DestroyImmediate(_actor);
            SetUp();
            hand = _rig.LeftArm.Upper.position + new Vector3(-0.3f, 0f, 1.6f);
            hip = _rig.Hip.position + new Vector3(0.1f, 0f, 0f);
            _targets.ApplyTarget("pelvis", hip, 0f, true, 1d, 10d);
            _targets.ApplyTarget("hand_l", hand, 0f, true, 1d, 11d);
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, hand), Is.LessThan(.003f));
            Assert.That(Vector3.Distance(_rig.Hip.position, hip), Is.LessThan(.002f));
        }

        [Test]
        public void FullyExtendedLegsCannotMoveHipSidewaysWithFixedFeet()
        {
            foreach (var leg in new[] { _rig.LeftLeg, _rig.RightLeg })
            {
                leg.Lower.localPosition = Vector3.forward;
                leg.End.localPosition = Vector3.forward;
            }
            var before = Snapshot(_rig);
            int rejected = 0;
            _consumer.TargetRejected += (_, _) => ++rejected;
            _targets.ApplyTarget("pelvis", _rig.Hip.position + Vector3.right * .1f,
                0f, true, 1d, 10d);
            Assert.That(rejected, Is.EqualTo(1));
            AssertSnapshot(before);
        }

        [Test]
        public void FractionalTargetsAreNotReblendedByAnotherBodyPartAndApplyHandOrientation()
        {
            Vector3 initialHip = _rig.Hip.position;
            Vector3 initialHand = _rig.LeftArm.End.position;
            Quaternion initialHandRotation = _rig.LeftArm.End.rotation;
            Vector3 handGoal = _rig.LeftArm.Upper.position + new Vector3(-.3f, 0, 1.6f);
            Vector3 hipGoal = initialHip + Vector3.right * .1f;
            Vector3 expectedHand = Vector3.Lerp(initialHand, handGoal, .5f);
            Quaternion expectedRotation = Quaternion.Slerp(initialHandRotation, Quaternion.Euler(0, 65, 0), .5f);
            _targets.ApplyTarget("hand_l", handGoal, 65f, true, .5d, 10d);
            _targets.ApplyTarget("pelvis", hipGoal, 0f, true, .5d, 11d);
            Vector3 expectedHip = Vector3.Lerp(initialHip, hipGoal, .5f);
            Assert.That(Vector3.Distance(_rig.Hip.position, expectedHip), Is.LessThan(.002f));
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, expectedHand), Is.LessThan(.002f));
            Assert.That(Quaternion.Angle(_rig.LeftArm.End.rotation, expectedRotation), Is.LessThan(.01f));

            _targets.ApplyTarget("head", _rig.Head.position, 10f, true, .5d, 12d);
            Assert.That(Vector3.Distance(_rig.Hip.position, expectedHip), Is.LessThan(.002f));
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, expectedHand), Is.LessThan(.002f));

            Object.DestroyImmediate(_actor);
            SetUp();
            _targets.ApplyTarget("pelvis", hipGoal, 0f, true, .5d, 10d);
            _targets.ApplyTarget("hand_l", handGoal, 65f, true, .5d, 11d);
            Assert.That(Vector3.Distance(_rig.Hip.position, expectedHip), Is.LessThan(.002f));
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, expectedHand), Is.LessThan(.002f));
            Assert.That(Quaternion.Angle(_rig.LeftArm.End.rotation, expectedRotation), Is.LessThan(.01f));
        }

        [Test]
        public void StandingIsTickPartitionIndependentAndContinuesDuringHandUpdates()
        {
            var reference = new GameObject("standing-reference");
            try
            {
                Vector3 start = _rig.Hip.position;
                reference.transform.position = start + Vector3.right * .2f;
                _rig.StandingHipReference = reference.transform;
                _targets.ApplyTarget("pelvis", start + Vector3.left * .1f, 0f, true, 1d, 10d);
                _targets.CancelTarget("pelvis");
                _consumer.Tick(20d);
                _consumer.Tick(20.1d);
                Vector3 oneTick = _rig.Hip.position;

                Object.DestroyImmediate(_actor);
                SetUp();
                _rig.StandingHipReference = reference.transform;
                _targets.ApplyTarget("pelvis", start + Vector3.left * .1f, 0f, true, 1d, 10d);
                _targets.CancelTarget("pelvis");
                _consumer.Tick(20d);
                _consumer.Tick(20.05d);
                _targets.ApplyTarget("hand_l", _rig.LeftArm.Upper.position + Vector3.forward * 1.5f,
                    20f, true, 1d, 20.05d);
                _consumer.Tick(20.1d);
                Assert.That(Vector3.Distance(_rig.Hip.position, oneTick), Is.LessThan(.002f));
                _consumer.Tick(20.3d);
                Assert.That(Vector3.Distance(_rig.Hip.position, reference.transform.position), Is.LessThan(.002f));
            }
            finally { Object.DestroyImmediate(reference); }
        }

        [Test]
        public void VerticalTargetWithParallelBendNormal_SolvesWithoutNaN()
        {
            _rig.LeftArm.BendNormal = Vector3.up;
            Vector3 target = _rig.LeftArm.Upper.position + Vector3.up * 1.5f;
            _targets.ApplyTarget("hand_l", target, 0f, true, 1d, 10d);
            Assert.That(Vector3.Distance(_rig.LeftArm.End.position, target), Is.LessThan(.003f));
            Assert.That(float.IsNaN(_rig.LeftArm.Lower.position.x), Is.False);
        }

        [Test]
        public void InvalidSeatContactAndSharedChain_RejectBeforeMutation()
        {
            _rig.SeatContact.SetParent(_actor.transform, true);
            Quaternion before = _rig.Hip.rotation;
            int rejected = 0;
            _consumer.TargetRejected += (_, _) => rejected++;
            _targets.ApplyTarget("seat_contact", _rig.SeatContact.position, 0f, true, 1d, 10d);
            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(_rig.Hip.rotation, Is.EqualTo(before));

            _rig.SeatContact.SetParent(_rig.Hip, true);
            _rig.RightArm.Upper = _rig.LeftArm.Upper;
            _targets.ApplyTarget("hand_r", _rig.RightArm.End.position, 0f, true, 1d, 11d);
            Assert.That(rejected, Is.EqualTo(2));

            _rig.RightArm.Upper =
                Child(_rig.Hip, "replacement-right-arm", new Vector3(.4f, .7f, 0f));
            _rig.RightArm.Lower.SetParent(_rig.RightArm.Upper, true);
            _rig.Head = _rig.Hip;
            _targets.ApplyTarget("head", _rig.Hip.position, 0f, true, 1d, 12d);
            Assert.That(rejected, Is.EqualTo(3));
        }

        [Test]
        public void UnreachableLiveStandingReference_RejectsWithoutHipMutation()
        {
            var standing = new GameObject("bad-standing");
            standing.transform.position = _rig.Hip.position + Vector3.right * 10f;
            _rig.StandingHipReference = standing.transform;
            _targets.ApplyTarget("pelvis", _rig.Hip.position + Vector3.right * .1f, 0f, true, 1d,
                                 10d);
            _targets.CancelTarget("pelvis");
            Vector3 before = _rig.Hip.position;
            int rejected = 0;
            _consumer.TargetRejected += (_, _) => rejected++;
            _consumer.Tick(10d);
            _consumer.Tick(10.3d);
            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(_rig.Hip.position, Is.EqualTo(before));
            Object.DestroyImmediate(standing);
        }

        [Test]
        public void DisableAndCancel_RelinquishWithoutFurtherArticulatedWrites()
        {
            Vector3 target = _rig.LeftArm.Upper.position + new Vector3(-.3f, 0f, 1.6f);
            _targets.ApplyTarget("hand_l", target, 0f, true, 1d, 10d);
            Quaternion tracked = Quaternion.Euler(4f, 9f, 0f);
            _rig.LeftArm.Upper.rotation = tracked;
            _consumer.enabled = false;
            _targets.CancelTarget("hand_l");
            Assert.That(_rig.LeftArm.Upper.rotation, Is.EqualTo(tracked));
        }

        // The 0.2 s SmoothStep stand fade is removed. A released hip returns to its live standing
        // reference on the next tick, in one step, and a cancelled fractional hip target is never replayed.
        [Test]
        public void FractionalHipCancellation_DoesNotReplayStaleTarget_AndTheNextTickStandsAtOnce()
        {
            var standing = new GameObject("standing");
            standing.transform.position = _rig.Hip.position + Vector3.right * .2f;
            _rig.StandingHipReference = standing.transform;
            _targets.ApplyTarget("pelvis", _rig.Hip.position + Vector3.left * .2f, 0f, true, .5d,
                                 10d);
            Vector3 cancelledPose = _rig.Hip.position;
            _targets.CancelTarget("pelvis");
            Assert.That(_rig.Hip.position, Is.EqualTo(cancelledPose));
            _consumer.Tick(100d);
            Assert.That(Vector3.Distance(_rig.Hip.position, standing.transform.position), Is.LessThan(.002f),
                        "no fade: the first tick after the release places the hip on the reference");
            Object.DestroyImmediate(standing);
        }

        [Test]
        public void InvalidSharedOrHeadAlias_LeavesEveryConfiguredTransformUntouched()
        {
            var before = Snapshot(_rig);
            _rig.RightArm.Upper = _rig.LeftArm.Upper;
            _targets.ApplyTarget("hand_r", _rig.RightArm.End.position, 0f, true, 1d, 10d);
            AssertSnapshot(before);

            _rig.RightArm.Upper = Child(_rig.Hip, "new-right", new Vector3(.4f, .7f, 0f));
            _rig.RightArm.Lower.SetParent(_rig.RightArm.Upper, true);
            before = Snapshot(_rig);
            _rig.Head = _rig.Hip;
            _targets.ApplyTarget("head", _rig.Hip.position, 0f, true, 1d, 11d);
            AssertSnapshot(before);
        }

        [Test]
        public void ConfigureReplacementRig_ResetsSeatCalibration()
        {
            _rig.SeatContact.localPosition = new Vector3(0f, -.2f, .1f);
            Assert.That(_consumer.CalibrateSeatContact(_rig.SeatContact.position), Is.True);
            var replacementRoot = new GameObject("replacement-rig");
            var replacement = BuildSyntheticCalibratedRig(replacementRoot.transform);
            replacement.SeatContact.localPosition = new Vector3(0f, -.4f, 0f);
            _consumer.Configure(_targets, replacement);
            Assert.That(_consumer.HasSeatCalibration, Is.False);
            Assert.That(_consumer.CalibrateSeatContact(replacement.SeatContact.position), Is.True);
            Assert.That(_consumer.HipToSeatContact.y, Is.EqualTo(-.4f).Within(.001f));
            Object.DestroyImmediate(replacementRoot);
        }

        private static Dictionary<Transform, Pose> Snapshot(ArticulatedTargetController.Rig rig)
        {
            var poses = new Dictionary<Transform, Pose>();
            foreach (Transform transform in Configured(rig))
                poses[transform] = new Pose(transform.position, transform.rotation);
            return poses;
        }

        private static void AssertSnapshot(Dictionary<Transform, Pose> before)
        {
            foreach (var pair in before)
            {
                Assert.That(pair.Key.position, Is.EqualTo(pair.Value.position));
                Assert.That(pair.Key.rotation, Is.EqualTo(pair.Value.rotation));
            }
        }

        private static IEnumerable<Transform> Configured(ArticulatedTargetController.Rig rig)
        {
            yield return rig.Hip;
            yield return rig.Head;
            foreach (var limb in new[] { rig.LeftArm, rig.RightArm, rig.LeftLeg, rig.RightLeg })
            {
                yield return limb.Upper;
                yield return limb.Lower;
                yield return limb.End;
            }
        }

        // Test-only calibrated toy rig; it is not a claim about a production avatar or capture clip.
        private static ArticulatedTargetController.Rig BuildSyntheticCalibratedRig(Transform root)
        {
            Transform hip = Child(root, "hip", new Vector3(0f, 2f, 0f));
            var rig = new ArticulatedTargetController.Rig {
                Hip = hip, Head = Child(hip, "head", new Vector3(0f, 1f, 0f))
            };
            rig.LeftArm = Limb(hip, "left-arm", new Vector3(-0.4f, 0.7f, 0f));
            rig.RightArm = Limb(hip, "right-arm", new Vector3(0.4f, 0.7f, 0f));
            rig.LeftLeg = Leg(hip, "left-leg", new Vector3(-0.25f, -0.1f, 0f));
            rig.RightLeg = Leg(hip, "right-leg", new Vector3(0.25f, -0.1f, 0f));
            rig.SeatContact = Child(hip, "seat-contact", Vector3.zero);
            return rig;
        }

        private static ArticulatedTargetController.Limb Limb(Transform parent, string name,
                                                             Vector3 localPosition)
        {
            Transform upper = Child(parent, name + "-upper", localPosition);
            Transform lower = Child(upper, name + "-lower", Vector3.forward);
            Transform end = Child(lower, name + "-end", Vector3.forward);
            return new ArticulatedTargetController.Limb { Upper = upper, Lower = lower, End = end,
                                                          BendNormal = Vector3.up };
        }

        // Bent stance leaves lateral hip room; straight, fully extended legs do not.
        private static ArticulatedTargetController.Limb Leg(Transform parent, string name,
                                                            Vector3 localPosition)
        {
            Transform upper = Child(parent, name + "-upper", localPosition);
            Transform lower = Child(upper, name + "-lower", new Vector3(0f, -.8f, .4f));
            Transform end = Child(lower, name + "-end", new Vector3(0f, -.8f, -.4f));
            return new ArticulatedTargetController.Limb { Upper = upper, Lower = lower, End = end,
                                                          BendNormal = Vector3.right };
        }

        private static Transform Child(Transform parent, string name, Vector3 localPosition)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = localPosition;
            return child;
        }
    }
}
#endif
