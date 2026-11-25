// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// ABI 4.14: the head anchor's chart (<c>sb_read_anchor_chart</c>, the source head's own sample replayed through the shared object
    /// that holds it) travels from the native read through the proposal set, the driver's plan and apply and the entity to the retarget,
    /// where it is state for the raw Meta body visible during an interaction (ContinuousCompensationTests). The chart's YAW
    /// (OvrAvatarEmbodiedDriver.HeadChart.cs) also turns the Sb avatar's head while it is present (Facing's
    /// goal is suppressed inside the transfer leaf); the hands compose on that head as always.
    ///
    /// The native chart is built from the core's Z-up convention written out in this file (yaw_core = 90 - yaw_unity, degrees), not
    /// from <see cref="CoordinateConverter"/>.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarHandChartTests
    {
        private const long Actor = 7;
        private GameObject _avatar, _sb, _rigRoot;
        private SbNetworkAvatarEntity _entity;
        private SbRetargetTransform _retarget;

        [TearDown]
        public void TearDown()
        {
            if (_avatar != null) Object.DestroyImmediate(_avatar);
            if (_sb != null) Object.DestroyImmediate(_sb);
            if (_rigRoot != null) Object.DestroyImmediate(_rigRoot);
        }

        // The production received avatar on the checked target-event path, the only one, as
        // RemoteAvatarFacingContractTests builds it: the SDK avatar inactive (no native SDK load in EditMode), the retarget on an active
        // object (its realization lease needs it). No anchor-target writer: the checked driver writes the head through its lease.
        private void BuildAvatar()
        {
            _avatar = new GameObject("received-avatar");
            _entity = _avatar.AddComponent<SbNetworkAvatarEntity>();
            var meta = _avatar.AddComponent<MetaAvatarEntity>();
            _sb = new GameObject("sb-avatar");
            _sb.SetActive(false);
            var sbAvatar = _sb.AddComponent<SbAvatarEntity>();
            _rigRoot = new GameObject("checked-retarget");
            _retarget = _rigRoot.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = Part("head");
            _retarget.leftHandTransform = Part("left");
            _retarget.rightHandTransform = Part("right");
            sbAvatar._retargetTransform = _retarget;
            meta._sbAvatar = sbAvatar;
        }

        private Transform Part(string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(_rigRoot.transform, false);
            return part;
        }

        // The core's yaw (Z-up, x right, y forward, counter-clockwise) of a Unity yaw (Y-up, clockwise from +z), radians.
        private static double CoreYaw(float unityYawDegrees) => (90.0 - unityYawDegrees) * System.Math.PI / 180.0;

        // The source's head sample in its object's chart, mapped through the receiver's object, as sb_read_anchor_chart returns it.
        private static SbAnchorChart NativeHeadChart(uint head, ulong revision, float sourceObjectYaw, float receiverObjectYaw,
                                                     float sourceHeadYaw)
        {
            double yaw = CoreYaw(receiverObjectYaw) + (CoreYaw(sourceHeadYaw) - CoreYaw(sourceObjectYaw));
            return new SbAnchorChart
            {
                struct_size = 88, anchor_id = head, flags = SpatialBlendAPI.AnchorChartFlagVerified, leaf_shared_id = 900,
                x = 1.0, y = 2.0, z = 1.6, revision = revision,
                orientation = new SbOrientation { struct_size = 32, type = (int)SbOrientationType.YawPitch, yaw = yaw },
            };
        }

        [Test]
        public void TheNativeChartStructIsTheCoresLayout()
        {
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SbAnchorChart>(), Is.EqualTo(88),
                "sb_anchor_chart_t: four uint32, an int64, three doubles, sb_orientation_t (32), a uint64");
        }

        [Test]
        public void AChartOfAnotherSetOrAnotherAnchorIsNotTaken()
        {
            var proposal = new AnchorProposal { AnchorId = 3 };
            SbAnchorChart chart = NativeHeadChart(3, 5, 0f, 90f, 10f);
            Assert.That(SpatialBlendAPI.TryConvertAnchorChart(chart, 3, 6, ref proposal), Is.False, "another revision");
            Assert.That(SpatialBlendAPI.TryConvertAnchorChart(chart, 4, 5, ref proposal), Is.False, "another anchor");
            Assert.That(proposal.HasChart, Is.False);
            Assert.That(SpatialBlendAPI.TryConvertAnchorChart(chart, 3, 5, ref proposal), Is.True);
            Assert.That(proposal.HasChart && proposal.ChartVerified && proposal.ChartLeafSharedId == 900, Is.True);
        }

        // The chart's ground point in Unity: sb_anchor_chart_t (x, y, z) = (1, 2, 1.6) Z-up is (1, 1.6, 2) Y-up.
        private static readonly Vector3 ChartGround = new Vector3(1f, 0f, 2f);
        private const float FacingOffset = 75f;
        private const float Dt = 1f / 30f;

        private sealed class Rig
        {
            internal ManagerContainmentContractTests.CheckedCore Core;
            internal OvrAvatarEmbodiedDriver Driver;
            internal uint Head;
            internal readonly ProposalSet Proposals = new ProposalSet();
        }

        // The head is the avatar's observed headset pose before the driver is admitted: once the checked lease is held, only the driver
        // writes the head (a foreign head write invalidates the lease). Hand observations stay the app's.
        private Rig MakeRig(Vector3? head = null, Quaternion? headRotation = null)
        {
            BuildAvatar();
            _retarget.SetHeadTransform(head ?? Vector3.zero, headRotation ?? Quaternion.identity);
            _entity.SetTransform(_retarget.headTransform);   // as SbNetworkAvatarEntity.SyncRemote leaves a received avatar
            var rig = new Rig { Core = new ManagerContainmentContractTests.CheckedCore() };
            rig.Driver = new OvrAvatarEmbodiedDriver(_entity, Actor, rig.Core, EmbodiedDriverConfig.Default);
            Assert.That(rig.Driver.TryEnableTargetEvents(SbRetargetTransform.RealizationContract.SynchronousExclusiveRootAndHead,
                out string error), Is.True, error);
            rig.Driver.PublishPose();
            Assert.That(rig.Core.TryGetAnchorId(Actor, "head", out rig.Head), Is.True);
            rig.Core.Proposal = new AnchorProposal { AnchorId = 0, Status = EmbodiedStatus.None };
            return rig;
        }

        // One driver frame through the real path: publish, read the set, drain the typed events, plan, apply (as the manager does: a
        // plan that is empty is not applied).
        private static EmbodiedFramePlan Frame(Rig rig)
        {
            ++rig.Core.Revision;
            EmbodiedPose embodied = rig.Driver.PublishPose();
            Assert.That(rig.Driver.TryReadProposals(rig.Proposals), Is.True);
            var events = new List<ActorEvent>();
            rig.Driver.PollEvents(events);
            EmbodiedFramePlan plan = rig.Driver.Plan(embodied, rig.Proposals, events, Dt);
            if (!plan.IsEmpty) rig.Driver.Apply(plan);
            return plan;
        }

        private static void SetHeadChart(Rig rig, SbAnchorChart native)
        {
            var proposal = new AnchorProposal { AnchorId = rig.Head, Status = EmbodiedStatus.None };
            native.revision = rig.Core.Revision + 1;   // the set Frame reads next
            Assert.That(SpatialBlendAPI.TryConvertAnchorChart(native, rig.Head, native.revision, ref proposal), Is.True);
            rig.Core.Others.Clear();
            rig.Core.Others.Add(proposal);
        }

        private static void SetNoChart(Rig rig)
        {
            rig.Core.Others.Clear();
            rig.Core.Others.Add(new AnchorProposal { AnchorId = rig.Head, Status = EmbodiedStatus.None });
        }

        // The whole receiver path for 0/90/180/-90 degree object turns: the chart reaches the retarget as state with the chart-mapped
        // yaw and the chart's ground point, and the Sb avatar's head turns toward the chart's yaw at the driver's angular bound (option
        // A); its hands compose on that head.
        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(180f)]
        [TestCase(-90f)]
        public void TheHeadChartIsStateAndTurnsTheSbHeadTowardItsYaw(float objectTurn)
        {
            const float sourceObjectYaw = 30f, sourceHeadYaw = 10f;
            float chartYaw = sourceObjectYaw + objectTurn + (sourceHeadYaw - sourceObjectYaw);
            var head = new Vector3(1.3f, 1.58f, 1.8f);
            Quaternion facing = Quaternion.Euler(25f, chartYaw + FacingOffset, 0f);
            Rig rig = MakeRig(head, facing);
            var local = new Vector3(-0.2f, -0.5f, 0.4f);
            Quaternion rotation = Quaternion.Euler(10f, 35f, -40f);
            _retarget.SetHandTransform(local, -local, rotation, rotation, 1f);

            SetHeadChart(rig, NativeHeadChart(rig.Head, 0, sourceObjectYaw, sourceObjectYaw + objectTurn, sourceHeadYaw));
            EmbodiedFramePlan plan = Frame(rig);
            Assert.That(plan.Charts.Count, Is.EqualTo(1), "the head's chart rides the plan");
            _retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds);

            string label = $"turn {objectTurn}";
            Assert.That(_retarget.TryGetHeadChart(out Vector3 position, out float yaw, out float weight), Is.True, label);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, chartYaw)), Is.LessThan(0.01f), label + ": the chart-mapped head yaw");
            Assert.That(new Vector2(position.x - ChartGround.x, position.z - ChartGround.z).magnitude, Is.LessThan(1e-4f), label);
            Assert.That(weight, Is.EqualTo(1f));
            // One frame: the head turned toward the chart's yaw (75 degrees away) by the angular bound, the short way.
            float turned = Mathf.DeltaAngle(facing.eulerAngles.y, _retarget.headTransform.eulerAngles.y);
            float bound = EmbodiedDriverConfig.DefaultMaxAngularSpeedDegreesPerSecond * Dt;
            Assert.That(turned, Is.EqualTo(-Mathf.Min(bound, FacingOffset)).Within(0.05f), label + ": the head turns toward the chart's yaw");
            AssertYawOnlyAndHandsOnHead(head, facing.eulerAngles.y - Mathf.Min(bound, FacingOffset), local, rotation, label + " (first frame)");
            for (int i = 0; i < 60; ++i) Frame(rig);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(_retarget.headTransform.eulerAngles.y, chartYaw)), Is.LessThan(0.05f),
                label + ": the head reached the chart's yaw");
            AssertYawOnlyAndHandsOnHead(head, chartYaw, local, rotation, label + " (reached)");
        }

        // Option A turns the head's YAW only: its position stays, its pitch (25) and roll (0) are Facing's baseline; the hands are
        // head-relative and compose on the head the retarget holds (no re-set by the test).
        private void AssertYawOnlyAndHandsOnHead(Vector3 head, float expectedYaw, Vector3 local, Quaternion rotation, string label)
        {
            Assert.That(Vector3.Distance(_retarget.headTransform.position, head), Is.LessThan(1e-5f), label + ": the head position stays");
            Assert.That(Quaternion.Angle(_retarget.headTransform.rotation, Quaternion.Euler(25f, expectedYaw, 0f)), Is.LessThan(0.05f),
                label + ": yaw only (pitch 25, roll 0 kept)");
            Transform h = _retarget.headTransform;
            Assert.That(Vector3.Distance(_retarget.leftHandTransform.position, h.position + h.rotation * local), Is.LessThan(1e-4f),
                label + ": the left hand composes on the head (position)");
            Assert.That(Quaternion.Angle(_retarget.leftHandTransform.rotation, h.rotation * rotation), Is.LessThan(0.05f),
                label + ": the left hand composes on the head (rotation)");
        }

        // Outside any transfer leaf there is no head chart: the rule before option A holds unchanged. The Sb head keeps its Facing
        // pose exactly, the hands are not recomposed, nothing is written, and no chart state exists.
        [Test]
        public void OutsideATransferLeafTheSbHeadKeepsItsFacingAndNothingIsWritten()
        {
            var head = new Vector3(1.3f, 1.58f, 1.8f);
            Quaternion facing = Quaternion.Euler(25f, 40f, 0f);
            Rig rig = MakeRig(head, facing);
            var local = new Vector3(-0.2f, -0.5f, 0.4f);
            Quaternion rotation = Quaternion.Euler(10f, 35f, -40f);
            _retarget.SetHandTransform(local, -local, rotation, rotation, 1f);
            Pose left = new Pose(_retarget.leftHandTransform.position, _retarget.leftHandTransform.rotation);
            for (int i = 0; i < 30; ++i)
            {
                SetNoChart(rig);
                EmbodiedFramePlan plan = Frame(rig);
                Assert.That(plan.Charts.Count, Is.EqualTo(0), "no chart outside a leaf");
            }
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.False, "no chart state");
            Assert.That(Vector3.Distance(_retarget.headTransform.position, head), Is.LessThan(1e-5f), "the head position is untouched");
            Assert.That(Quaternion.Angle(_retarget.headTransform.rotation, facing), Is.LessThan(0.05f), "the head keeps Facing");
            Assert.That(Vector3.Distance(_retarget.leftHandTransform.position, left.position), Is.LessThan(1e-4f), "the hands are untouched");
            Assert.That(Quaternion.Angle(_retarget.leftHandTransform.rotation, left.rotation), Is.LessThan(0.05f), "the hands are untouched");
        }

        // The share blends over HeadChartBlendSeconds both ways (the Sb head's own turn toward the chart is the driver's, pinned above).
        [Test]
        public void TheHeadChartShareBlendsInAndOut()
        {
            Rig rig = MakeRig(new Vector3(1f, 1.6f, 2f));
            _retarget.SetHandTransform(new Vector3(-0.2f, -0.5f, 0.4f), Vector3.forward, Quaternion.identity, Quaternion.identity, 1f);
            Quaternion hand = _retarget.leftHandTransform.rotation;
            SetHeadChart(rig, NativeHeadChart(rig.Head, 0, 0f, 90f, 0f));
            Frame(rig);
            float step = SbRetargetTransform.HeadChartBlendSeconds / 2f;
            Assert.That(Weight(), Is.EqualTo(0f), "nothing at once");
            _retarget.AdvanceHeadChartBlend(step);
            Assert.That(Weight(), Is.EqualTo(0.5f).Within(1e-5f));
            _retarget.AdvanceHeadChartBlend(step);
            Assert.That(Weight(), Is.EqualTo(1f));
            SetNoChart(rig);
            Assert.That(Frame(rig).ChartsCleared, Is.True);
            Assert.That(Weight(), Is.EqualTo(1f), "leaving does not pop");
            _retarget.AdvanceHeadChartBlend(step);
            Assert.That(Weight(), Is.EqualTo(0.5f).Within(1e-5f));
            _retarget.AdvanceHeadChartBlend(step);
            Assert.That(_retarget.TryGetHeadChart(out _, out _, out _), Is.False, "out");
            Assert.That(Quaternion.Angle(_retarget.leftHandTransform.rotation, _retarget.headTransform.rotation * hand), Is.LessThan(0.05f),
                "the hands compose on the head");
        }

        // The retarget's root placement (the checked root), read without writing.
        private Pose RootPlacement() => (Pose)typeof(SbRetargetTransform)
            .GetMethod("CurrentRootPlacement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(_retarget, null);

        private float Weight()
        {
            _retarget.TryGetHeadChartState(out _, out _, out float weight);
            return weight;
        }

        private void AssertChartIn(Rig rig)
        {
            SetHeadChart(rig, NativeHeadChart(rig.Head, 0, 0f, 90f, 0f));
            Frame(rig);
            _retarget.AdvanceHeadChartBlend(SbRetargetTransform.HeadChartBlendSeconds);
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out float w) && w == 1f, Is.True, "the chart is in");
        }

        private void AssertNoChart(string why)
        {
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out float weight), Is.False, why);
            Assert.That(weight, Is.EqualTo(0f), why + ": at once, no blend");
        }

        // A receiver frame discontinuity resets every driver: the reset forgets the bookkeeping and the next plan decides. A close drops
        // the chart at once, and writes exactly the lease's release (ReleaseRealization): the head back to its genuine local baseline
        // over the lease's root (here the observed pre-admission pose, since nothing moved the root), both hands recomposed on it, no
        // root write. Before option A the chart never turned the head, so that release
        // rewrote nothing; inside a leaf the chart has turned the head, and the release restores it, at once (the lease's rule for any
        // head it held, ReleaseRealization).
        [Test]
        public void AResetLetsTheNextPlanDecideAndACloseDropsAtOnceAndRestoresOnlyTheObservedHead()
        {
            var observed = new Pose(new Vector3(0.4f, 1.6f, -0.3f), Quaternion.Euler(0f, 20f, 0f));
            Rig rig = MakeRig(observed.position, observed.rotation);
            _retarget.SetHandTransform(new Vector3(-0.2f, -0.5f, 0.4f), Vector3.forward, Quaternion.identity, Quaternion.identity, 1f);
            AssertChartIn(rig);
            rig.Driver.Reset();
            Assert.That(Weight(), Is.EqualTo(1f), "a reset writes nothing");
            AssertChartIn(rig);
            rig.Driver.Reset();
            SetNoChart(rig);
            Assert.That(Frame(rig).ChartsCleared, Is.True);
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.False);
            Assert.That(Weight(), Is.EqualTo(1f), "the next set has none: released, it blends out");
            AssertChartIn(rig);
            Assert.That(Quaternion.Angle(_retarget.headTransform.rotation, observed.rotation), Is.GreaterThan(1f),
                "inside the leaf the chart turned the head (option A)");
            Pose rootBefore = RootPlacement();
            var leftLocal = new Vector3(-0.2f, -0.5f, 0.4f);
            rig.Driver.Close();
            AssertNoChart("a close");
            Assert.That(Vector3.Distance(_retarget.headTransform.position, observed.position), Is.LessThan(1e-5f), "the observed head position");
            Assert.That(Quaternion.Angle(_retarget.headTransform.rotation, observed.rotation), Is.LessThan(1e-3f), "the observed head yaw, at once");
            Pose rootAfter = RootPlacement();
            Assert.That(Vector3.Distance(rootAfter.position, rootBefore.position), Is.LessThan(1e-6f), "the root is not written");
            Assert.That(Quaternion.Angle(rootAfter.rotation, rootBefore.rotation), Is.LessThan(1e-4f), "the root is not turned");
            Transform h = _retarget.headTransform;
            Assert.That(Vector3.Distance(_retarget.leftHandTransform.position, h.position + h.rotation * leftLocal), Is.LessThan(1e-4f),
                "the left hand is recomposed on the restored head (position)");
            Assert.That(Vector3.Distance(_retarget.rightHandTransform.position, h.position + h.rotation * Vector3.forward), Is.LessThan(1e-4f),
                "the right hand is recomposed on the restored head (position)");
            Assert.That(Quaternion.Angle(_retarget.leftHandTransform.rotation, h.rotation), Is.LessThan(0.05f),
                "the left hand is recomposed on the restored head (rotation)");
            Assert.That(Quaternion.Angle(_retarget.rightHandTransform.rotation, h.rotation), Is.LessThan(0.05f),
                "the right hand is recomposed on the restored head (rotation)");
        }

        // A closed target lifetime (a fault or a refused teardown leaves the checked lifetime closed): Plan answers a plan that drops
        // the chart.
        [Test]
        public void AClosedTargetLifetimeDropsTheChart()
        {
            Rig rig = MakeRig();
            AssertChartIn(rig);
            typeof(OvrAvatarEmbodiedDriver).GetField("_targetClosed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(rig.Driver, true);
            EmbodiedFramePlan plan = Frame(rig);
            Assert.That(plan.ChartsCleared && !plan.IsEmpty, Is.True, "the manager applies it");
            AssertNoChart("a closed lifetime");
        }

        [Test]
        public void NoSetReleasesTheChartAndAReplacedSetKeepsIt()
        {
            Rig rig = MakeRig();
            AssertChartIn(rig);
            // The chart call answered another revision (the set was replaced between the two calls): unknown, so the last one stays.
            rig.Core.Others.Clear();
            rig.Core.Others.Add(new AnchorProposal { AnchorId = rig.Head, Status = EmbodiedStatus.None, ChartStale = true });
            EmbodiedFramePlan plan = Frame(rig);
            Assert.That(plan.Charts.Count, Is.EqualTo(1), "a stale read keeps the last chart");
            Assert.That(_retarget.TryGetHeadChartState(out _, out float yaw, out _), Is.True);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, 90f)), Is.LessThan(0.05f));
            // No set at all: the manager plans from an empty set, so the chart is released, not dropped.
            // Through the manager's frame: TargetEventDriverIntegrationTests.TheManagersFrameCarriesTheHeadChartAndAFrameWithoutASetReleasesIt.
            EmbodiedFramePlan release = rig.Driver.Plan(rig.Driver.PublishPose(), new ProposalSet(), new List<ActorEvent>(), Dt);
            Assert.That(release.ChartsCleared && !release.IsEmpty, Is.True);
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.True, "planning changes nothing");
            rig.Driver.Apply(release);
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out float weight), Is.False);
            Assert.That(weight, Is.EqualTo(1f), "released, not dropped: it blends out");
            EmbodiedFramePlan again = rig.Driver.Plan(rig.Driver.PublishPose(), new ProposalSet(), new List<ActorEvent>(), Dt);
            Assert.That(again.ChartsCleared, Is.False, "nothing more to release");
        }

        // AttachAnchorCharts with a scripted read in place of the native call.
        [Test]
        public void AttachingChartsMarksAReplacedSetStaleAndAMissingEntryPointReadsNothing()
        {
            const uint head = 3;
            var set = new ProposalSet { Revision = 10 };
            set.Anchors.Add(new AnchorProposal { AnchorId = 0 });
            set.Anchors.Add(new AnchorProposal { AnchorId = head });
            SbAnchorChart Native(ulong revision) => NativeHeadChart(head, revision, 0f, 90f, 0f);

            Assert.That(SpatialBlendAPI.AttachAnchorCharts(set, 1, 7, (long space, long actor, uint id, ref SbAnchorChart c) =>
            {
                Assert.That(space == 1 && actor == 7, Is.True);
                if (id != head) return 0;
                c = Native(10); return 1;
            }), Is.True);
            Assert.That(set.Anchors[1].HasChart && !set.Anchors[1].ChartStale, Is.True, "the set's own revision");
            Assert.That(set.Anchors[0].HasChart || set.Anchors[0].ChartStale, Is.False, "no chart for the root");

            set.Anchors[1] = new AnchorProposal { AnchorId = head };
            Assert.That(SpatialBlendAPI.AttachAnchorCharts(set, 1, 7, (long space, long actor, uint id, ref SbAnchorChart c) =>
            {
                if (id != head) return 0;
                c = Native(11); return 1;
            }), Is.True);
            Assert.That(!set.Anchors[1].HasChart && set.Anchors[1].ChartStale, Is.True, "another revision: stale, not absent");

            set.Anchors[1] = new AnchorProposal { AnchorId = head };
            Assert.That(SpatialBlendAPI.AttachAnchorCharts(set, 1, 7, (long space, long actor, uint id, ref SbAnchorChart c) =>
                throw new System.EntryPointNotFoundException("sb_read_anchor_chart")), Is.False, "a library before 4.14");
            Assert.That(set.Anchors[1].HasChart || set.Anchors[1].ChartStale, Is.False);
        }
    }
}
#endif
