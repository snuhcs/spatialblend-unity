// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T2.6: the raw Meta body stays aligned with the realized root. The Meta avatar SDK is not
    /// involved: the body, its head joint and the realized avatar's head joint are plain
    /// transforms, which is all <see cref="ContinuousCompensation"/> reads and writes.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ContinuousCompensationTests
    {
        private const float PositionTolerance = 1e-3f;
        private const float YawToleranceDegrees = 0.05f;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject item in _objects)
            {
                if (item != null) Object.DestroyImmediate(item);
            }
            _objects.Clear();
        }

        private Transform Make(string name, Vector3 position, float yawDegrees, Transform parent = null)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            Transform transform = item.transform;
            transform.SetParent(parent, false);
            if (parent == null)
            {
                transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
            }
            else
            {
                transform.localPosition = position;
                transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            }
            return transform;
        }

        // The Meta body at an arbitrary stream frame, with its head joint 1.6 m up and 0.1 m in front,
        // turned 20 degrees from the body.
        private Transform MakeMetaBody(out Transform head)
        {
            Transform body = Make("meta-body", new Vector3(1f, 0.2f, 2f), 30f);
            head = Make("meta-head", new Vector3(0f, 1.6f, 0.1f), 20f, body);
            return body;
        }

        private static float Yaw(Transform transform) => transform.rotation.eulerAngles.y;

        private static void AssertHeadOver(Transform metaHead, Transform referenceHead, string label = "")
        {
            Assert.That(metaHead.position.x, Is.EqualTo(referenceHead.position.x).Within(PositionTolerance), label);
            Assert.That(metaHead.position.z, Is.EqualTo(referenceHead.position.z).Within(PositionTolerance), label);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Yaw(metaHead), Yaw(referenceHead))),
                Is.LessThan(YawToleranceDegrees), label);
        }

        [Test]
        public void OneFrameTurnsTheBodyToTheReferenceYawAndMovesItsHeadOverTheReferenceHead()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 100f);
            var compensation = new ContinuousCompensation();
            Assert.That(compensation.Start(body, metaHead, reference), Is.True);
            float bodyHeight = body.position.y;

            Assert.That(compensation.Apply(), Is.True);

            AssertHeadOver(metaHead, reference);
            Assert.That(body.position.y, Is.EqualTo(bodyHeight), "Height belongs to the stream.");
        }

        [Test]
        public void PositionIsReadAfterTheRotationBecauseTurningTheBodyMovesItsHead()
        {
            // The head is 0.5 m off the body origin, so the yaw correction swings it. A position
            // read taken before the turn would leave it 0.5 m away.
            Transform body = Make("meta-body", new Vector3(0f, 0f, 0f), 0f);
            Transform metaHead = Make("meta-head", new Vector3(0.5f, 1.6f, 0f), 0f, body);
            Transform reference = Make("sb-head", new Vector3(3f, 1.7f, 4f), 180f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, reference);

            compensation.Apply();

            AssertHeadOver(metaHead, reference);
        }

        [Test]
        public void ItFollowsAMovingReferenceEveryFrame()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 100f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, reference);

            for (int frame = 0; frame < 20; ++frame)
            {
                reference.position += new Vector3(0.1f, 0f, -0.05f);
                reference.rotation = Quaternion.Euler(0f, 100f + frame * 7f, 0f);
                compensation.Apply();
                AssertHeadOver(metaHead, reference, "frame " + frame);
            }
        }

        [Test]
        public void AnAlignedBodyIsLeftWhereItIs()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 100f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, reference);
            compensation.Apply();
            Vector3 position = body.position;
            Quaternion rotation = body.rotation;

            compensation.Apply();

            Assert.That(Vector3.Distance(body.position, position), Is.LessThan(PositionTolerance));
            Assert.That(Quaternion.Angle(body.rotation, rotation), Is.LessThan(YawToleranceDegrees));
        }

        [Test]
        public void ObjectRelativePoseOfTheRawBodyIsTheSameForAnyPlacementOfTheObject()
        {
            // While contained, the realized root (the reference head) is the source's object-relative
            // pose carried onto this peer's copy of the object. The raw Meta body must end up with
            // the same object-relative head pose, whatever frame its stream was recorded in.
            var relativeHead = new Vector3(0.3f, 1.65f, -0.7f);
            const float relativeYaw = 25f;
            Vector3 expectedLocal = Vector3.zero;
            float expectedYaw = 0f;
            bool first = true;

            foreach (float objectYaw in new[] { 0f, 90f, 200f })
            {
                foreach (Vector3 objectPosition in new[] { new Vector3(4f, 0f, -3f), new Vector3(-2.5f, 0f, 6f) })
                {
                    Transform copy = Make("object-copy", objectPosition, objectYaw);
                    Transform reference = Make("sb-head", relativeHead, relativeYaw, copy);
                    Transform body = MakeMetaBody(out Transform metaHead);
                    var compensation = new ContinuousCompensation();
                    compensation.Start(body, metaHead, reference);

                    compensation.Apply();

                    Vector3 local = copy.InverseTransformPoint(metaHead.position);
                    float localYaw = Mathf.DeltaAngle(objectYaw, Yaw(metaHead));
                    if (first)
                    {
                        expectedLocal = local;
                        expectedYaw = localYaw;
                        first = false;
                        // Ground-plane position and yaw equal the declared object-relative pose.
                        Assert.That(local.x, Is.EqualTo(relativeHead.x).Within(0.01f));
                        Assert.That(local.z, Is.EqualTo(relativeHead.z).Within(0.01f));
                        Assert.That(localYaw, Is.EqualTo(relativeYaw).Within(2f));
                    }
                    string label = "object at " + objectPosition + " yaw " + objectYaw;
                    Assert.That(local.x, Is.EqualTo(expectedLocal.x).Within(0.01f), label);
                    Assert.That(local.z, Is.EqualTo(expectedLocal.z).Within(0.01f), label);
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(localYaw, expectedYaw)), Is.LessThan(2f), label);
                }
            }
        }

        [Test]
        public void StopLeavesTheBodyAloneAndApplyReportsNothingDone()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 100f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, reference);
            compensation.Stop();
            Vector3 position = body.position;

            Assert.That(compensation.Enabled, Is.False);
            Assert.That(compensation.Apply(), Is.False);
            Assert.That(body.position, Is.EqualTo(position));
        }

        [Test]
        public void NeverStartedDoesNothing()
        {
            Transform body = MakeMetaBody(out _);
            Vector3 position = body.position;
            var compensation = new ContinuousCompensation();
            Assert.That(compensation.Apply(), Is.False);
            Assert.That(body.position, Is.EqualTo(position));
        }

        [Test]
        public void StartRefusesAMissingTransformAndStaysOff()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", Vector3.zero, 0f);
            var compensation = new ContinuousCompensation();

            Assert.That(compensation.Start(null, metaHead, reference), Is.False);
            Assert.That(compensation.Start(body, null, reference), Is.False);
            Assert.That(compensation.Start(body, metaHead, null), Is.False);
            Assert.That(compensation.Enabled, Is.False);
        }

        [Test]
        public void ADestroyedTransformStopsItInsteadOfThrowing()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 100f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, reference);
            Object.DestroyImmediate(reference.gameObject);

            Assert.That(compensation.Apply(), Is.False);
            Assert.That(compensation.Enabled, Is.False);
        }

        [Test]
        public void StartingAgainRebindsToTheNewReference()
        {
            Transform body = MakeMetaBody(out Transform metaHead);
            Transform first = Make("sb-head-1", new Vector3(5f, 1.7f, -2f), 100f);
            Transform second = Make("sb-head-2", new Vector3(-4f, 1.7f, 7f), 300f);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, first);
            compensation.Apply();
            compensation.Start(body, metaHead, second);

            compensation.Apply();

            AssertHeadOver(metaHead, second);
        }

        [Test]
        public void CompensatePositionAndRotationKeepHeightAndPitch()
        {
            Transform body = Make("body", new Vector3(1f, 2f, 3f), 0f);
            body.rotation = Quaternion.Euler(10f, 30f, 5f);

            ContinuousCompensation.CompensatePosition(body, new Vector3(1f, 99f, -1f));
            ContinuousCompensation.CompensateRotation(body, new Vector3(50f, 20f, 50f));

            Assert.That(body.position, Is.EqualTo(new Vector3(2f, 2f, 2f)));
            Vector3 euler = body.rotation.eulerAngles;
            Assert.That(euler.x, Is.EqualTo(10f).Within(1e-3f));
            Assert.That(euler.y, Is.EqualTo(50f).Within(1e-3f));
            Assert.That(euler.z, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void MetaAvatarEntityAndTheBehaviourUseTheContinuousPort()
        {
            string meta = System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/MetaAvatarEntity.cs");
            Assert.That(meta, Does.Contain("public void StartContinuousCompensation()"));
            Assert.That(meta, Does.Contain("public void StopContinuousCompensation()"));
            Assert.That(meta, Does.Contain("_compensation.Apply()"));
            string behaviour = System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/SbAvatarBehaviourFusion.cs");
            Assert.That(behaviour, Does.Contain("StartContinuousCompensation()"));
            Assert.That(behaviour, Does.Contain("StopContinuousCompensation()"));
            Assert.That(behaviour, Does.Not.Contain("CompensateForFrames"), "v1 had no 100-frame variant.");
        }

        // ---- the heading by flattened joint axes; the raw body placed by the head anchor's chart inside an interaction ----

        // Joint frames of a skeleton: axes are the face's up to a permutation and signs (the OVR head joint's rest frame is 120 degrees
        // about (1, 1, 1)); one has an axis straight up, where Unity's Euler y jumps by half a turn with a few degrees of pitch.
        private static readonly Quaternion[] JointRestFrames =
        {
            Quaternion.identity,
            Quaternion.AngleAxis(120f, new Vector3(1f, 1f, 1f).normalized),
            Quaternion.Euler(-90f, 0f, 0f),
            Quaternion.Euler(90f, 0f, 0f) * Quaternion.Euler(0f, 0f, 90f),
        };

        private Transform MakeJoint(string name, Transform parent, Vector3 localPosition, Quaternion localRotation)
        {
            Transform joint = Make(name, localPosition, 0f, parent);
            joint.localRotation = localRotation;
            return joint;
        }

        // Head joint = Y(heading) * X(pitch) * Z(roll) * rest. Oracle: the facing axis Inverse(rest) * forward, flattened.
        private static float Heading(Quaternion joint, Quaternion rest)
        {
            Vector3 f = joint * (Quaternion.Inverse(rest) * Vector3.forward);
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // Without an input (no retarget wired): the flattest joint axis, exact for any pitch difference in a skeleton's frames. The old
        // Euler-y difference read rest frame 2 at pitch -40 as a half turn.
        [Test]
        public void TheHeadingsMeetWhateverTheJointFrameAndThePitchOfEither()
        {
            for (int f = 0; f < JointRestFrames.Length; ++f)
            {
                Quaternion rest = JointRestFrames[f];
                foreach (float pitch in new[] { -40f, -8f, 15f, 60f })
                foreach (float target in new[] { 0f, 75f, 180f, -120f })
                {
                    Transform body = Make("meta-body", new Vector3(1f, 0f, 2f), 30f);
                    Transform metaHead = MakeJoint("meta-head", body, new Vector3(0f, 1.6f, 0.1f),
                        Quaternion.Euler(0f, 20f, 0f) * Quaternion.Euler(pitch, 0f, 0f) * rest);
                    Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 0f);
                    reference.rotation = Quaternion.Euler(0f, target, 0f) * Quaternion.Euler(5f, 0f, 0f) * rest;
                    var compensation = new ContinuousCompensation();
                    compensation.Start(body, metaHead, reference);
                    compensation.Apply();
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), target)), Is.LessThan(0.05f),
                        $"rest frame {f}, pitch {pitch}, target {target}");
                }
            }
        }

        // With the retarget wired (as MetaAvatarEntity does), the face axis comes from the input: exact with roll too.
        [Test]
        public void TheHeadingsMeetWithPitchAndRollWhenTheInputGivesTheFace()
        {
            for (int f = 0; f < JointRestFrames.Length; ++f)
            {
                Quaternion rest = JointRestFrames[f];
                foreach (float pitch in new[] { -40f, -8f, 15f, 60f })
                foreach (float target in new[] { 0f, 75f, 180f, -120f })
                {
                    Transform body = Make("meta-body", new Vector3(1f, 0f, 2f), 30f);
                    Transform metaHead = MakeJoint("meta-head", body, new Vector3(0f, 1.6f, 0.1f),
                        Quaternion.Euler(0f, 20f, 0f) * Quaternion.Euler(pitch, 0f, 0f) * Quaternion.Euler(0f, 0f, 12f) * rest);
                    Quaternion input = Quaternion.Euler(0f, target, 0f) * Quaternion.Euler(5f, 0f, 0f) * Quaternion.Euler(0f, 0f, -9f);
                    SbRetargetTransform retarget = MakeRetarget(new Vector3(5f, 1.7f, -2f), input);
                    Transform reference = Make("sb-head", new Vector3(5f, 1.7f, -2f), 0f);
                    reference.rotation = input * rest;
                    var compensation = new ContinuousCompensation();
                    compensation.Start(body, metaHead, reference);
                    MetaAvatarEntity.WireChartReference(compensation, retarget);
                    compensation.Apply();
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), target)), Is.LessThan(0.05f),
                        $"rest frame {f}, pitch {pitch}, target {target}");
                    Assert.That(Vector3.Angle(body.up, Vector3.up), Is.LessThan(YawToleranceDegrees), "the body stays upright");
                }
            }
        }

        private SbRetargetTransform MakeRetarget(Vector3 headPosition, Quaternion headRotation)
        {
            var go = new GameObject("retarget");
            _objects.Add(go);
            var retarget = go.AddComponent<SbRetargetTransform>();
            retarget.headTransform = Make("retarget-head", Vector3.zero, 0f, go.transform);
            retarget.leftHandTransform = Make("retarget-left", Vector3.zero, 0f, go.transform);
            retarget.rightHandTransform = Make("retarget-right", Vector3.zero, 0f, go.transform);
            retarget.SetHeadTransform(headPosition, headRotation);
            return retarget;
        }

        // As v1 intended, inside an interaction the raw Meta body replays the source exactly. It is placed by the head
        // anchor's chart (the source head's own sample through the object), while the Sb avatar's head keeps the Facing yaw. With the
        // Facing yaw 75 degrees off the chart's, the visible head and wrists are the source's moved with the object, for the receiver's
        // object turned 0, 90, 180 and -90 degrees, whatever the head joint's frame. Wired as MetaAvatarEntity wires it.
        [Test]
        public void InsideAnInteractionTheVisibleHeadAndWristsAreTheChartMappedSource()
        {
            const float sourceObjectYaw = 30f, sourceBodyYaw = 10f, headTurn = 25f, pitch = 20f;
            var headJointInHeadset = new Vector3(0f, -0.05f, -0.08f);   // the head joint behind and below the headset
            var wristLocal = new Vector3(-0.25f, 1.1f, 0.45f);
            Quaternion wristLocalRotation = Quaternion.Euler(20f, -35f, 70f);
            var chartGround = new Vector3(2f, 0f, -1f);
            for (int f = 0; f < JointRestFrames.Length; ++f)
            {
                Quaternion rest = JointRestFrames[f];
                Quaternion headLocal = Quaternion.Euler(0f, headTurn, 0f) * Quaternion.Euler(pitch, 0f, 0f) * rest;
                foreach (float turn in new[] { 0f, 90f, 180f, -90f })
                {
                    // The source as its stream plays it (its own room is the stream frame here).
                    Transform source = Make("source-body", new Vector3(0.5f, 0f, -0.5f), sourceBodyYaw);
                    Transform sourceHead = MakeJoint("source-head", source, new Vector3(0f, 1.6f, 0.1f), headLocal);
                    Transform sourceWrist = MakeJoint("source-wrist", source, wristLocal, wristLocalRotation);
                    float sourceHeadYaw = sourceBodyYaw + headTurn;

                    // The receiver: the Sb avatar's head carries the Facing yaw; the head anchor's chart is the source head relative
                    // to the object, on the receiver's object.
                    float chartYaw = sourceObjectYaw + turn + (sourceHeadYaw - sourceObjectYaw);
                    float facingYaw = chartYaw + 75f;
                    SbRetargetTransform retarget = MakeRetarget(new Vector3(4f, 1.7f, 3f), Quaternion.Euler(pitch, facingYaw, 0f));
                    Transform sbHead = Make("sb-head", Vector3.zero, 0f);
                    sbHead.rotation = Quaternion.Euler(0f, facingYaw, 0f) * Quaternion.Euler(pitch, 0f, 0f) * rest;
                    sbHead.position = retarget.headTransform.position + Quaternion.Euler(0f, facingYaw, 0f) * headJointInHeadset;
                    retarget.SetHeadChart(true, chartGround, chartYaw);
                    retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds);

                    Transform body = Make("meta-body", new Vector3(6f, 0f, 6f), 140f);   // wherever the stream's entity stood
                    Transform metaHead = MakeJoint("meta-head", body, new Vector3(0f, 1.6f, 0.1f), headLocal);
                    Transform metaWrist = MakeJoint("meta-wrist", body, wristLocal, wristLocalRotation);
                    var compensation = new ContinuousCompensation();
                    compensation.Start(body, metaHead, sbHead);
                    MetaAvatarEntity.WireChartReference(compensation, retarget);
                    compensation.Apply();

                    string label = $"rest frame {f}, object turned {turn}";
                    Quaternion moved = Quaternion.Euler(0f, turn, 0f);
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), chartYaw)), Is.LessThan(0.05f),
                        label + ": the visible head heads the chart's way");
                    Vector3 expectedHead = chartGround + Quaternion.Euler(0f, chartYaw, 0f) * headJointInHeadset;
                    Assert.That(new Vector2(metaHead.position.x - expectedHead.x, metaHead.position.z - expectedHead.z).magnitude,
                        Is.LessThan(PositionTolerance), label + ": the visible head stands at the chart's point");
                    Assert.That(Quaternion.Angle(metaWrist.rotation, moved * sourceWrist.rotation), Is.LessThan(YawToleranceDegrees),
                        label + ": the visible wrist is the source's, moved with the object");
                    Assert.That(Vector3.Angle(metaWrist.forward, moved * sourceWrist.forward), Is.LessThan(YawToleranceDegrees), label);
                    Vector3 expectedWrist = metaHead.position + moved * (sourceWrist.position - sourceHead.position);
                    Assert.That(Vector3.Distance(metaWrist.position, expectedWrist), Is.LessThan(PositionTolerance), label);
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(retarget.headTransform.rotation.eulerAngles.y, facingYaw)), Is.LessThan(0.05f),
                        label + ": the Sb avatar's head keeps the Facing yaw");
                }
            }
        }

        // The switch is blended (the chart's share on the retarget, 0.25 s), and with no chart the reference head joint places the body.
        [Test]
        public void TheChartReferenceBlendsAndWithoutAChartTheReferenceHeadPlacesTheBody()
        {
            Quaternion rest = JointRestFrames[1];
            const float facingYaw = 40f, chartYaw = 130f;
            SbRetargetTransform retarget = MakeRetarget(new Vector3(4f, 1.7f, 3f), Quaternion.Euler(0f, facingYaw, 0f));
            Transform sbHead = Make("sb-head", retarget.headTransform.position, 0f);
            sbHead.rotation = Quaternion.Euler(0f, facingYaw, 0f) * rest;
            Transform body = Make("meta-body", Vector3.zero, 0f);
            Transform metaHead = MakeJoint("meta-head", body, new Vector3(0f, 1.6f, 0f), rest);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, sbHead);
            MetaAvatarEntity.WireChartReference(compensation, retarget);

            compensation.Apply();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), facingYaw)), Is.LessThan(0.05f), "no chart");

            retarget.SetHeadChart(true, new Vector3(4f, 0f, 3f), chartYaw);
            compensation.Apply();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), facingYaw)), Is.LessThan(0.05f), "no pop");
            retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds / 2f);
            compensation.Apply();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), (facingYaw + chartYaw) / 2f)), Is.LessThan(0.05f),
                "half way");
            retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds / 2f);
            compensation.Apply();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), chartYaw)), Is.LessThan(0.05f), "in");

            retarget.SetHeadChart(false, default, 0f);
            retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds);
            compensation.Apply();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(Heading(metaHead.rotation, rest), facingYaw)), Is.LessThan(0.05f), "out");
        }

        // With the chart about half a turn off the Facing yaw, a degree of jitter flips the shortest turn between +179 and -179. The
        // blend must keep the direction it started with (no swing by the share of a full turn), in and out, at 170/179/181/190 degrees.
        [TestCase(170f)]
        [TestCase(179f)]
        [TestCase(181f)]
        [TestCase(190f)]
        public void ABlendNearAHalfTurnKeepsItsDirectionThroughJitter(float difference)
        {
            Quaternion rest = JointRestFrames[1];
            const float facingYaw = 40f;
            SbRetargetTransform retarget = MakeRetarget(new Vector3(4f, 1.7f, 3f), Quaternion.Euler(0f, facingYaw, 0f));
            Transform sbHead = Make("sb-head", retarget.headTransform.position, 0f);
            sbHead.rotation = Quaternion.Euler(0f, facingYaw, 0f) * rest;
            Transform body = Make("meta-body", Vector3.zero, 0f);
            Transform metaHead = MakeJoint("meta-head", body, new Vector3(0f, 1.6f, 0f), rest);
            var compensation = new ContinuousCompensation();
            compensation.Start(body, metaHead, sbHead);
            MetaAvatarEntity.WireChartReference(compensation, retarget);
            compensation.Apply();

            // The spec: the turn is the shortest one when a blend starts (in, or out), then continues without a jump while the share
            // is partial. Oracle: that turn followed frame to frame.
            const int steps = 10;
            float step = SbRetargetTransform.HeadChartBlendSeconds / steps;
            float previous = Heading(metaHead.rotation, rest);
            float lastChart = 0f;
            for (int phase = 0; phase < 2; ++phase)   // in, then out
            {
                float? turn = null;
                for (int k = 1; k <= steps; ++k)
                {
                    float jitter = (k % 2 == 0 ? 1f : -1f) * 1.5f;
                    if (phase == 0) lastChart = difference + jitter;   // out: the chart is no longer set, the last one is kept
                    retarget.SetHeadChart(phase == 0, new Vector3(4f, 0f, 3f), facingYaw + lastChart);
                    retarget.AdvanceHeadChartBlend(step);
                    retarget.TryGetHeadChartState(out _, out _, out float weight);
                    compensation.Apply();
                    turn = turn.HasValue ? turn.Value + Mathf.DeltaAngle(turn.Value, lastChart) : Mathf.DeltaAngle(0f, lastChart);
                    float heading = Heading(metaHead.rotation, rest);
                    float expected = facingYaw + weight * turn.Value;
                    string label = $"difference {difference}, phase {phase}, step {k}, share {weight}";
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(heading, expected)), Is.LessThan(0.5f), label);
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(heading, previous)), Is.LessThan(25f), label + ": no swing");
                    previous = heading;
                }
            }
        }
    }
}
#endif
