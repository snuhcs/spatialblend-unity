// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;
using UnityEngine.TestTools;
using Contract = SpatialBlend.SbRetargetTransform.RealizationContract;
using Object = UnityEngine.Object;
using static SpatialBlend.Tests.EmbodiedTargetConsumerTests;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TargetEventDriverIntegrationTests
    {
        private const long Actor = 7;
        private const uint Head = 1;
        private GameObject _avatar, _sdk, _root, _managerObject;
        private SbNetworkAvatarEntity _entity;
        private SbRetargetTransform _retarget;
        private SbAvatarEntity _sdkEntity;
        private SbRetargetAnchorTargetBridge _bridge;
        private OvrAvatarEmbodiedDriver _driver;
        private TargetCore _core;

        public sealed class TargetTrackingInput : SbTransformInputManager
        {
            public void InitializeProviders() => base.OnTrackingInitialized();
        }

        // No session is opened by the private collect/apply test below.
        public sealed class TargetManager : SpatialBlendManager
        {
            public override void OnDestroy() { }
            internal readonly List<double> SourceSteps = new List<double>();
            protected override void SetNativeSourceStep(double seconds) => SourceSteps.Add(seconds);
        }

        // Each actor tick adds the frame's REAL duration to the source's own time (sb_set_update_interval), not the
        // native default 1/30 s, which would count a 60-90 Hz app at two to three times real time and run the receivers' 5 s interaction
        // latency limit out early. A hitch counts at most MaxSourceStepSeconds; no space, no step.
        [Test]
        public void EachFrameStepsTheSourceClockByItsRealDuration()
        {
            _managerObject = new GameObject("inactive-clock-manager");
            _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<TargetManager>();
            SetManagerField(manager, "_connectOnAwake", false);
            manager.TestOnlySourceStepSeconds = 1f / 72f;
            manager.LateUpdate();
            Assert.That(manager.SourceSteps, Is.Empty, "no space yet");
            SetManagerField(manager, "_spaceID", 1L);
            manager.LateUpdate();
            manager.TestOnlySourceStepSeconds = 2f;
            manager.LateUpdate();
            manager.TestOnlySourceStepSeconds = 0f;
            manager.LateUpdate();
            Assert.That(manager.SourceSteps.Count, Is.EqualTo(2));
            Assert.That(manager.SourceSteps[0], Is.EqualTo(1.0 / 72.0).Within(1e-6));
            Assert.That(manager.SourceSteps[1], Is.EqualTo(SpatialBlendManager.MaxSourceStepSeconds));
        }

        private sealed class NoEmbodiment : IEmbodimentTextSource
        {
            public bool TryRead(string name, out string text) { text = null; return false; }
        }

        private sealed class TargetCore : IEmbodiedTargetEventCore
        {
            internal readonly Queue<(ActorEventPollResult result, ActorEvent message, ActorEventPollFailure failure)> Events =
                new Queue<(ActorEventPollResult, ActorEvent, ActorEventPollFailure)>();
            internal readonly List<(uint anchor, ulong epoch, ulong sequence, bool teleported, double yaw, Vector3 position)> Publications =
                new List<(uint, ulong, ulong, bool, double, Vector3)>();
            internal readonly List<bool> Subscriptions = new List<bool>();
            // Native EmbodiedPort retains these across delivery toggles. A new actor
            // gets a distinct entry; creating another driver never clears an entry.
            internal readonly Dictionary<(long actor, uint anchor), (ulong epoch, ulong sequence)> Clocks =
                new Dictionary<(long, uint), (ulong, ulong)>();
            internal bool RefuseEnable, RefuseDisable, RefusePublication, ThrowEnable, ThrowDisable;
            internal readonly List<Vector2> CoursePoints = new List<Vector2>();
            internal int BoolPolls, LegacyPublications, TypedPolls, SnapshotReads, StalePublications, CourseQueries;
            public bool TrySetTargetEventDelivery(long actor, bool enabled, out string error)
            {
                Assert.That(actor, Is.GreaterThan(0));
                Subscriptions.Add(enabled);
                if ((enabled && ThrowEnable) || (!enabled && ThrowDisable)) throw new InvalidOperationException("injected subscription failure");
                error = enabled && RefuseEnable ? "queued legacy exception" : !enabled && RefuseDisable ? "injected disable refusal" : null;
                return error == null;
            }
            internal void Enqueue(ActorEvent actorEvent) => Events.Enqueue((ActorEventPollResult.Event, actorEvent, null));
            public ActorEventPollResult PollEvent(long actor, out ActorEvent actorEvent, out ActorEventPollFailure failure)
            {
                ++TypedPolls;
                actorEvent = default; failure = null;
                if (Events.Count == 0) return ActorEventPollResult.NativeEmpty;
                var item = Events.Dequeue(); actorEvent = item.message; failure = item.failure; return item.result;
            }
            public bool TryGetAnchorId(long actor, string name, out uint id) { id = name == "root" ? 0u : name == "head" ? Head : 5u; return true; }
            public bool TryGetAnchorName(long actor, uint id, out string name) { name = id == 0 ? "root" : id == Head ? "head" : "bone"; return true; }
            public void PublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double time, ulong sequence, ulong epoch, bool teleported) => ++LegacyPublications;
            public bool TryPublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double time, ulong sequence, ulong epoch, bool teleported) =>
                throw new InvalidOperationException("Continuous publication must use the double overload.");
            public bool TryPublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double yaw, double time,
                ulong sequence, ulong epoch, bool teleported, out double nativeYaw)
            {
                nativeYaw = Radians(90d - yaw);
                if (RefusePublication || sequence == 0) return false;
                if (Clocks.TryGetValue((actor, anchor), out var accepted) &&
                    (epoch < accepted.epoch || (epoch == accepted.epoch && sequence <= accepted.sequence)))
                {
                    ++StalePublications;
                    return false;
                }
                Clocks[(actor, anchor)] = (epoch, sequence);
                Publications.Add((anchor, epoch, sequence, teleported, yaw, pose.Position));
                return true;
            }
            // A proposal set to answer with (ABI 4.14 chart tests); null: there is none to read, as for every other test here.
            internal List<AnchorProposal> Snapshot;
            internal ulong SnapshotRevision;
            public bool TryReadProposals(long actor, ProposalSet proposals)
            {
                ++SnapshotReads; proposals.Clear();
                if (Snapshot == null) return false;
                proposals.Revision = ++SnapshotRevision;
                proposals.Anchors.AddRange(Snapshot);
                proposals.TotalAnchorCount = (uint)Snapshot.Count;
                return true;
            }
            public bool TryPollEvent(long actor, out ActorEvent actorEvent) { ++BoolPolls; actorEvent = default; return false; }
            public bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> points)
            {
                ++CourseQueries;
                if (CoursePoints.Count == 0) points.Add(to);
                else points.AddRange(CoursePoints);
                return true;
            }
            public EmbodimentRegistrationResult RegisterEmbodiment(long actor, string json, out string error) { error = null; return EmbodimentRegistrationResult.Unsupported; }
            public bool TryGetBoneId(long actor, string name, out int id) { id = 0; return false; }
            public void PublishBonePose(long actor, int bone, in EmbodiedBonePose pose, double time, ulong sequence, ulong epoch, bool teleported) =>
                throw new InvalidOperationException("Quaternion bone samples are not winding observations.");
            public bool TryPublishBonePose(long actor, int bone, in EmbodiedBonePose pose, double time, ulong sequence, ulong epoch, bool teleported) => false;
            public bool TryAnchorPoseFromBone(long actor, string name, in EmbodiedBonePose bone, out EmbodiedBonePose anchor) { anchor = default; return false; }
            public bool TryBonePoseFromAnchor(long actor, string name, in EmbodiedBonePose anchor, out EmbodiedBonePose bone) { bone = default; return false; }
        }

        [SetUp]
        public void SetUp()
        {
            _avatar = new GameObject("typed-received-avatar");
            _entity = _avatar.AddComponent<SbNetworkAvatarEntity>();
            _sdk = new GameObject("inactive-sdk-avatar");
            _sdk.SetActive(false); // Do not initialize/load a native SDK avatar in EditMode.
            _sdkEntity = _sdk.AddComponent<SbAvatarEntity>();
            _root = new GameObject("active-checked-retarget");
            _retarget = _root.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = Part("head");
            _retarget.leftHandTransform = Part("left");
            _retarget.rightHandTransform = Part("right");
            _retarget.SetHeadTransform(new Vector3(1f, 1.6f, 2f), Quaternion.identity);
            _sdkEntity._retargetTransform = _retarget;
            typeof(SbNetworkAvatarEntity).GetField("_sbAvatar", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_entity, _sdkEntity);
            _entity.SetTransform(_retarget.headTransform);
            var controller = _avatar.AddComponent<AnchorTargetController>();
            _bridge = _avatar.AddComponent<SbRetargetAnchorTargetBridge>();
            _bridge.Configure(controller, _retarget);
            _core = new TargetCore();
            _driver = new OvrAvatarEmbodiedDriver(_entity, Actor, _core, EmbodiedDriverConfig.Default, new NoEmbodiment());
        }

        [TearDown]
        public void TearDown()
        {
            _driver?.Close();
            if (_managerObject != null)
            {
                SetManagerField(_managerObject.GetComponent<TargetManager>(), "_spaceID", 0L);
                _entity.Detach();
            }
            Object.DestroyImmediate(_avatar);
            Object.DestroyImmediate(_sdk);
            Object.DestroyImmediate(_root);
            if (_managerObject != null) Object.DestroyImmediate(_managerObject);
        }

        private Transform Part(string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(_root.transform, false);
            return part;
        }

        private void Enable()
        {
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out string error), Is.True, error);
            _driver.PublishPose();
        }

        private EmbodiedFramePlan Plan(float dt)
        {
            EmbodiedPose actual = _driver.PublishPose();
            var proposals = new ProposalSet();
            Assert.That(_driver.TryReadProposals(proposals), Is.False);
            var events = new List<ActorEvent>();
            _driver.PollEvents(events);
            return _driver.Plan(actual, proposals, events, dt);
        }

        private void Frame(float dt) => _driver.Apply(Plan(dt));

        // ABI 4.14 on the checked (target-event) path: the head's chart rides the plan to the hands, and an empty set releases it. The
        // chart-only plan does not run the target frame (no extra publication).
        [Test]
        public void TheCheckedPathCarriesTheHeadChartToTheHandsAndReleasesIt()
        {
            Enable();
            var proposals = new ProposalSet { Revision = 1 };
            proposals.Anchors.Add(new AnchorProposal
            {
                AnchorId = Head, Status = EmbodiedStatus.None, HasChart = true, ChartPosition = new Vector3(1f, 1.6f, 2f),
                ChartYawDegrees = 90f,
            });
            EmbodiedPose actual = _driver.PublishPose();
            var events = new List<ActorEvent>();
            _driver.PollEvents(events);
            EmbodiedFramePlan plan = _driver.Plan(actual, proposals, events, 1f / 30f);
            Assert.That(plan.Charts.Count, Is.EqualTo(1));
            Assert.That(plan.IsEmpty, Is.False);
            int publications = _core.Publications.Count;
            _driver.Apply(plan);
            // A head chart is head work (inside a transfer leaf the Sb head takes the chart's yaw): the
            // target frame runs and publishes.
            Assert.That(_core.Publications.Count, Is.GreaterThan(publications), "a head chart runs the target frame");
            Assert.That(_retarget.TryGetHeadChartState(out Vector3 position, out float yaw, out _), Is.True);
            Assert.That(yaw, Is.EqualTo(90f));
            Assert.That(position, Is.EqualTo(new Vector3(1f, 1.6f, 2f)));

            proposals.Clear();
            actual = _driver.PublishPose();
            _driver.PollEvents(events);
            plan = _driver.Plan(actual, proposals, events, 1f / 30f);
            Assert.That(plan.ChartsCleared, Is.True);
            _driver.Apply(plan);
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.False, "released");
        }

        // ---------------------------------------------------------------- the head chart composed with Facing (option A, HeadChart.cs)

        private ulong _chartRevision = 100;

        // One plan/apply with a proposal set carrying one row for `anchor`: a chart at `chartYaw` (Unity degrees), a stale chart read, or
        // (both null/false) no row at all.
        private void ChartFrame(float? chartYaw, bool stale = false, uint anchor = Head, float dt = 1f / 30f)
        {
            EmbodiedPose actual = _driver.PublishPose();
            var proposals = new ProposalSet { Revision = ++_chartRevision };
            if (chartYaw.HasValue || stale)
                proposals.Anchors.Add(new AnchorProposal
                {
                    AnchorId = anchor, Status = EmbodiedStatus.None, HasChart = chartYaw.HasValue && !stale, ChartStale = stale,
                    ChartPosition = new Vector3(0f, 1.6f, 0f), ChartYawDegrees = chartYaw ?? 0f,
                });
            var events = new List<ActorEvent>();
            _driver.PollEvents(events);
            _driver.Apply(_driver.Plan(actual, proposals, events, dt));
            Assert.That(_driver.TargetEventError, Is.Null);
        }

        private double HeadYawDegrees() => ReadSample().HeadYawRadians * 180d / System.Math.PI;

        [Test]
        public void AHeadChartAloneTurnsTheHeadTowardItsYawWithinTheAngularBound()
        {
            Enable();
            double start = HeadYawDegrees();
            float chart = (float)(start + 90d);
            double cap = EmbodiedDriverConfig.DefaultMaxAngularSpeedDegreesPerSecond / 30d + 1e-3;
            double previous = start;
            for (int i = 0; i < 90; ++i)
            {
                ChartFrame(chart);
                double now = HeadYawDegrees();
                Assert.That(now - previous, Is.GreaterThanOrEqualTo(-1e-6).And.LessThanOrEqualTo(cap), "frame " + i + ": monotonic, bounded");
                previous = now;
            }
            Assert.That(Mathf.DeltaAngle((float)previous, chart), Is.EqualTo(0f).Within(0.01f), "the head reached the chart's yaw");
        }

        [Test]
        public void AHeadChartTurnsTheShortWayAcrossTheWrap()
        {
            Enable();
            double start = HeadYawDegrees();
            float chart = (float)(start - 2d + 360d);   // 2 degrees clockwise, stated a full turn away
            for (int i = 0; i < 30; ++i) ChartFrame(chart);
            Assert.That(HeadYawDegrees() - start, Is.EqualTo(-2d).Within(0.01d), "the short way, never a 358 degree swing");
        }

        [Test]
        public void TheChartSuppressesFacingInsideTheLeafAndFacingResumesTheShortWayAfterIt()
        {
            Enable();
            double start = HeadYawDegrees();
            double facing = start + 30d;
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw, unityYaw: facing, sample: _driver.TargetConsumer.Current(Head)));
            // The chart drives the head 300 degrees round, in steps, while Facing's goal (start + 30) is held.
            float chart = (float)start;
            for (int i = 0; i < 300; ++i)
            {
                chart += 1f;
                ChartFrame(chart);
            }
            Assert.That(HeadYawDegrees() - start, Is.EqualTo(300d).Within(1d), "the chart, not Facing, turned the head");
            double left = HeadYawDegrees();
            // The chart ends: Facing resumes from where the head is, the short way, never unwinding the chart's turns.
            for (int i = 0; i < 120; ++i) ChartFrame(null);
            double end = HeadYawDegrees();
            Assert.That(Mathf.DeltaAngle((float)end, (float)facing), Is.EqualTo(0f).Within(0.01f), "Facing's yaw again");
            Assert.That(System.Math.Abs(end - left), Is.LessThanOrEqualTo(180d), "the short way from the chart's end");
        }

        [Test]
        public void AStaleChartReadKeepsTheChartAndAnAbsentOneEndsIt()
        {
            Enable();
            double start = HeadYawDegrees();
            ChartFrame((float)(start + 60d));
            double afterOne = HeadYawDegrees();
            Assert.That(afterOne, Is.GreaterThan(start), "the chart turns the head");
            ChartFrame(null, stale: true);
            Assert.That(HeadYawDegrees(), Is.GreaterThan(afterOne), "a stale read keeps steering to the last chart");
            double held = HeadYawDegrees();
            ChartFrame(null);   // no head row: no chart, and no Facing goal: the head is carried, not turned
            ChartFrame(null);
            Assert.That(HeadYawDegrees(), Is.EqualTo(held).Within(1e-6), "an absent chart stops the chart");
        }

        [Test]
        public void OnlyTheHeadAnchorsChartTurnsTheHead()
        {
            Enable();
            double start = HeadYawDegrees();
            for (int i = 0; i < 10; ++i) ChartFrame((float)(start + 90d), anchor: 7);
            Assert.That(HeadYawDegrees(), Is.EqualTo(start).Within(1e-6), "another anchor's chart is not the head's");
        }

        private static CheckedRootPathDiagnostic CheckedRootPath(bool rootPositionEventReceived, bool rootGoalHasPosition,
                                                                 bool rootCommitSucceeded, bool rootTranslationObserved,
                                                                 bool targetFaultPresent) =>
            new CheckedRootPathDiagnostic(rootPositionEventReceived, rootGoalHasPosition, rootCommitSucceeded,
                rootTranslationObserved, targetFaultPresent);

        private CheckedRootPathDiagnostic LastCheckedRootPathDiagnostic()
        {
            Assert.That(_driver.LastCheckedRootPathDiagnostic.HasValue, Is.True);
            return _driver.LastCheckedRootPathDiagnostic.Value;
        }

        [TestCase(90f, 120f)]
        [TestCase(0f, 120f)]
        [TestCase(-90f, 45f)]
        public void StoppedSpacingAndFacingReachRealSdkTrackingDelegateWithoutPinningPosition(float courseYaw, float facingYaw)
        {
            var input = _sdk.AddComponent<TargetTrackingInput>();
            input.SbTransform = _retarget;
            input.InitializeProviders();
            _sdkEntity.SetInputManager(input);
            Assert.That(_sdkEntity.InputManager, Is.SameAs(input));
            var provider = _sdkEntity.InputManager.InputTrackingProvider;
            Assert.That(provider, Is.TypeOf<Oculus.Avatar2.OvrAvatarInputTrackingDelegatedProvider>());
            Assert.That(provider.GetInputTrackingState(out var initial), Is.True);
            Vector3 start = initial.headset.position;
            Enable();
            Vector3 tangent = Quaternion.Euler(0f, courseYaw, 0f) * Vector3.forward;
            Vector3 destination = start + tangent * 3f;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, destination));
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw,
                unityYaw: facingYaw, sample: _driver.TargetConsumer.Current(Head)));
            double previousHeadYaw = ReadSample().HeadYawRadians;
            for (int frame = 0; frame < 180; ++frame)
            {
                Frame(1f / 60f); // No further source events or proposal snapshots.
                double actualHeadYaw = ReadSample().HeadYawRadians;
                Assert.That(Math.Abs(Degrees(actualHeadYaw - previousHeadYaw)), Is.LessThanOrEqualTo(2.5d + .003d),
                    "including the first frame: a root turn cannot spend a second Facing increment");
                previousHeadYaw = actualHeadYaw;
                Assert.That(provider.GetInputTrackingState(out var tracking), Is.True);
                Assert.That(tracking.headsetActive, Is.True);
                Assert.That(Vector3.Distance((Vector3)tracking.headset.position, _retarget.headTransform.position), Is.LessThan(1e-4f));
            }
            Assert.That(provider.GetInputTrackingState(out var final), Is.True);
            Assert.That(Vector3.Distance((Vector3)final.headset.position, destination), Is.LessThan(.002f));
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion body), Is.True);
            Assert.That(Mathf.DeltaAngle(body.eulerAngles.y, courseYaw), Is.EqualTo(0f).Within(.05f));
            Quaternion facing = final.headset.orientation;
            Assert.That(Mathf.DeltaAngle(facing.eulerAngles.y, facingYaw), Is.EqualTo(0f).Within(.05f));
            Assert.That(_sdkEntity.transform.position, Is.EqualTo(Vector3.zero));
            Assert.That(_core.BoolPolls + _core.LegacyPublications, Is.Zero);
            // SDK ingress only: no rendered skeleton, Mecanim execution, or rc2 diagnosis is asserted.
        }

        [Test]
        public void TypedRootPositionUsesDirectV1StepWithoutCourseQuery()
        {
            Enable();
            Vector3 start = _retarget.headTransform.position;
            Vector3 destination = start + Vector3.forward * 4f;
            // This would turn right before reaching the destination if the checked
            // receiver re-queried its course service.
            _core.CoursePoints.Add(new Vector2(start.x + 4f, start.z));
            _core.CoursePoints.Add(new Vector2(destination.x, destination.z));
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, destination));

            Frame(.1f);

            // One 0.1 s step from rest: the speed is a*dt (0.4 m/s) under the acceleration bound, not the 2 m/s cap.
            Vector3 expected = Vector3.MoveTowards(start, destination, Mathf.Min(EmbodiedDriverConfig.DefaultMaxLinearSpeedMetersPerSecond,
                EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared * .1f) * .1f);
            Assert.That(_core.CourseQueries, Is.Zero);
            Assert.That(Vector3.Distance(_retarget.headTransform.position, expected), Is.LessThan(1e-4f));
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion body), Is.True);
            Assert.That(Mathf.DeltaAngle(body.eulerAngles.y, 0f), Is.EqualTo(0f).Within(.05f));
            Assert.That(_driver.TargetEventError, Is.Null);
        }

        // The kinematic bound on the checked path: a placement starts the body from rest. A root walking at the
        // cap is placed; its next far target is walked from rest (a*dt in the first frame), not at the speed it had.
        [Test]
        public void ARootPlacementRestartsTheWalkFromRest()
        {
            Enable();
            const float dt = 1f / 60f;
            float cap = EmbodiedDriverConfig.DefaultMaxLinearSpeedMetersPerSecond;
            float accel = EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared;
            Vector3 start = _retarget.headTransform.position;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, start + Vector3.right * 10f));
            Vector3 first = start;
            for (int frame = 0; frame < 60; ++frame)
            {
                first = ReadSample().Head.position;
                Frame(dt);
            }
            Assert.That(Vector3.Distance(ReadSample().Head.position, first), Is.EqualTo(cap * dt).Within(1e-4f), "walking at the cap");

            var placed = new Vector3(3f, 1.6f, 4f);
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 2, TargetChannels.Position, placed, placement: true));
            Frame(dt);
            Assert.That(ReadSample().Head.position, Is.EqualTo(placed), "placed, and held there");
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 3, TargetChannels.Position, placed + Vector3.forward * 4f));
            Frame(dt);
            Assert.That(Vector3.Distance(ReadSample().Head.position, placed), Is.EqualTo(accel * dt * dt).Within(1e-5f),
                "the walk after the placement starts from rest");
            Assert.That(_driver.TargetEventError, Is.Null);
        }

        [Test]
        public void HistoricalPlacementThenOrdinary440AndLatePrior620CompleteInOneContinuousDomain()
        {
            _retarget.SetHeadTransform(new Vector3(1f, 1.6f, 2f), Quaternion.Euler(0f, 170f, 0f));
            Enable();
            var first = _driver.TargetConsumer.Current(0);
            Vector3 destination = new Vector3(4f, 1.6f, 3f);
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, destination, placement: true));
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw, unityYaw: -170d, placement: true));
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw, unityYaw: 440d, sample: first));
            Frame(0f);
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(190d).Within(.002d));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(destination));
            Assert.That(_driver.TargetConsumer.Previous(0), Is.SameAs(first));
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 2, TargetChannels.Yaw, unityYaw: 620d, sample: first));
            for (int frame = 0; frame < 240; ++frame) Frame(1f / 60f);
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(620d).Within(.005d));
            Assert.That(_driver.TargetConsumer.DrainFailed, Is.False);
        }

        [Test]
        public void LifecycleAndTypedPlacementApplyOnceAndPublishOnlyFinalRealization()
        {
            Enable();
            _core.Enqueue(new ActorEvent { Kind = ActorEventKind.SourceException, Revision = 1, ExceptionId = SpatialBlendManager.JoinExceptionId });
            _core.Enqueue(new ActorEvent { Kind = ActorEventKind.TeleportRequest, Revision = 1, AnchorId = 0, Position = Vector3.one * 99f });
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, new Vector3(3f, 1.6f, 4f), placement: true));
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw, unityYaw: 90d, placement: true));
            Frame(0f);
            Assert.That(_driver.Epoch, Is.EqualTo(2ul), "one combined root placement, not two typed writes plus the legacy teleport");
            Assert.That(_retarget.headTransform.position, Is.EqualTo(new Vector3(3f, 1.6f, 4f)));
            Assert.That(ReadSample().Revision, Is.EqualTo(2ul), "one root commit and one actual head carry");
            Assert.That(_core.Publications.Count, Is.EqualTo(4), "initial root/head plus final root/head only");
            Assert.That(_core.Publications[2].teleported, Is.True);
            Assert.That(_core.Publications[2].position, Is.EqualTo(_retarget.headTransform.position));
        }

        [Test]
        public void PositionAndYawReleaseOnlyTheirNamedChannels()
        {
            Enable();
            Vector3 start = _retarget.headTransform.position;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, start + Vector3.right * 4f));
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw, unityYaw: 120d, sample: _driver.TargetConsumer.Current(Head)));
            Frame(.1f);
            _core.Enqueue(Request(ActorEventKind.TargetRelease, Head, 2, TargetChannels.Yaw));
            Frame(.1f);
            Vector3 beforeRelease = _retarget.headTransform.position;
            _core.Enqueue(Request(ActorEventKind.TargetRelease, 0, 3, TargetChannels.Position));
            Frame(.1f);
            Vector3 released = _retarget.headTransform.position;
            Frame(.5f);
            Assert.That(Vector3.Distance(released, beforeRelease), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(_retarget.headTransform.position, released), Is.LessThan(1e-5f));
            Assert.That(released.x, Is.GreaterThan(start.x));
        }

        [Test]
        public void CheckedRootPathDiagnosticReportsPositionGoalAndActualTranslation()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position,
                _retarget.headTransform.position + Vector3.right * 4f));
            Frame(.1f);
            var diagnostic = LastCheckedRootPathDiagnostic();
            Assert.That(diagnostic.RootPositionEventReceived, Is.True);
            Assert.That(diagnostic.RootGoalHasPosition, Is.True);
            Assert.That(diagnostic.RootCommitSucceeded, Is.True);
            Assert.That(diagnostic.RootTranslationObserved, Is.True);
            Assert.That(diagnostic.TargetFaultPresent, Is.False);
        }

        [Test]
        public void CheckedRootPathDiagnosticDistinguishesYawOnlyRootCommit()
        {
            Enable();
            var sample = _driver.TargetConsumer.Current(0);
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw,
                unityYaw: 60d, sample: sample));
            Frame(.1f);
            var diagnostic = LastCheckedRootPathDiagnostic();
            Assert.That(diagnostic.RootPositionEventReceived, Is.False);
            Assert.That(diagnostic.RootGoalHasPosition, Is.False);
            Assert.That(diagnostic.RootCommitSucceeded, Is.True);
            Assert.That(diagnostic.RootTranslationObserved, Is.False);
        }

        [Test]
        public void CheckedRootPathDiagnosticKeepsTheRetainedGoalAfterItsEvent()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position,
                _retarget.headTransform.position + Vector3.forward * 4f));
            Frame(.1f);
            Assert.That(LastCheckedRootPathDiagnostic(), Is.EqualTo(CheckedRootPath(true, true, true, true, false)));
            Frame(.1f);
            Assert.That(LastCheckedRootPathDiagnostic(), Is.EqualTo(CheckedRootPath(false, true, true, true, false)));
            Frame(.1f);
            var diagnostic = LastCheckedRootPathDiagnostic();
            Assert.That(diagnostic.RootPositionEventReceived, Is.False);
            Assert.That(diagnostic.RootGoalHasPosition, Is.True);
            Assert.That(diagnostic.RootCommitSucceeded, Is.True);
            Assert.That(diagnostic.RootTranslationObserved, Is.True);
        }

        [Test]
        public void CheckedRootPathDiagnosticRecordsTheIdleObservation()
        {
            Enable();
            Frame(0f);
            Frame(0f);
            var diagnostic = LastCheckedRootPathDiagnostic();
            Assert.That(diagnostic.RootPositionEventReceived, Is.False);
            Assert.That(diagnostic.RootGoalHasPosition, Is.False);
            Assert.That(diagnostic.RootCommitSucceeded, Is.False);
            Assert.That(diagnostic.RootTranslationObserved, Is.False);
            Assert.That(diagnostic.TargetFaultPresent, Is.False);
        }

        [Test]
        public void CheckedRootPathDiagnosticReportsFailedDrainAsFault()
        {
            Enable();
            _core.Events.Enqueue((ActorEventPollResult.NativeError, default, new ActorEventPollFailure(-1, "injected")));
            Frame(0f);
            Assert.That(_driver.TargetConsumer.DrainFailed, Is.True);
            Assert.That(LastCheckedRootPathDiagnostic().TargetFaultPresent, Is.True);
        }

        [Test]
        public void CheckedRootPathDiagnosticReportsRefusedRootApplyAsFault()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, Vector3.one,
                placement: true));
            var plan = Plan(0f);
            typeof(SbRetargetTransform).GetField("_realizationCommitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_retarget, true);
            try
            {
                // The injected in-progress commit makes the lease refuse every operation, and the apply reads the actual
                // root/head back before it places anything, so the refusal (the fault) is reported at that readback.
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("checked target realization stopped: Actual root/head readback refused: Busy"));
                _driver.Apply(plan);
            }
            finally
            {
                typeof(SbRetargetTransform).GetField("_realizationCommitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(_retarget, false);
            }
            Assert.That(LastCheckedRootPathDiagnostic().TargetFaultPresent, Is.True);
        }

        // A refusal of the placement itself (not the readback before it): a placement pitched straight down leaves the root no
        // heading, so the lease's TryPlaceRoot refuses it as invalid input after a committed readback. The root is not moved.
        [Test]
        public void CheckedRootPathDiagnosticReportsRefusedRootPlacementAsFault()
        {
            Enable();
            var before = ReadSample();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, before.Head.position + Vector3.right,
                placement: true, pitch: 90d, orientationType: (int)SbOrientationType.YawPitch));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "checked target realization stopped: Absolute root placement refused: InvalidInput"));
            Frame(0f);
            var diagnostic = LastCheckedRootPathDiagnostic();
            Assert.That(diagnostic.RootPositionEventReceived, Is.True);
            Assert.That(diagnostic.RootCommitSucceeded, Is.False);
            Assert.That(diagnostic.RootTranslationObserved, Is.False);
            Assert.That(diagnostic.TargetFaultPresent, Is.True);
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion body), Is.True);
            Assert.That(Quaternion.Angle(body, before.RootPlacement.rotation), Is.LessThan(.001f), "a refused placement writes nothing");
            Assert.That(Vector3.Distance(_retarget.headTransform.position, before.Head.position), Is.LessThan(.00001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PollFailureThenLaterEmptyCannotRetireCorrespondenceOrAcceptThirdSample(bool conversion)
        {
            ActorEventPollResult result = conversion ? ActorEventPollResult.ConversionError : ActorEventPollResult.NativeError;
            Enable();
            var first = _driver.TargetConsumer.Current(0);
            Frame(0f); // Genuine fence, with no movement: no post-apply publication for an empty plan.
            _driver.PublishPose();
            var second = _driver.TargetConsumer.Current(0);
            _core.Events.Enqueue((result, default, new ActorEventPollFailure(-1, "injected")));
            Frame(0f);
            int published = _core.Publications.Count;
            Frame(0f); // NativeEmpty after the failure is not success.
            Assert.That(_driver.TargetConsumer.Previous(0), Is.SameAs(first));
            Assert.That(_driver.TargetConsumer.Current(0), Is.SameAs(second));
            Assert.That(_core.Publications.Count, Is.EqualTo(published));
            Assert.That(_driver.TargetConsumer.Failures.Count, Is.EqualTo(1));
        }

        [Test]
        public void RefusedPublicationRetainsPendingPlacementStampUntilSuccessfulAcceptance()
        {
            Enable();
            _core.RefusePublication = true;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, new Vector3(5f, 1.6f, 4f), placement: true));
            Frame(0f);
            Assert.That(_driver.TargetConsumer.Current(0).Stamp.Epoch, Is.EqualTo(1ul));
            Assert.That(_driver.TargetConsumer.TryProposePublication(0, out var pending), Is.True);
            Assert.That(pending.Epoch, Is.EqualTo(2ul));
            Assert.That(pending.Sequence, Is.EqualTo(1ul));
            Assert.That(pending.Teleported, Is.True);
            _core.RefusePublication = false;
            _driver.PublishPose();
            Assert.That(_driver.TargetConsumer.Current(0).Stamp.Teleported, Is.True);
        }

        [Test]
        public void RefusedCheckedPlacementDoesNotAcknowledgeItsImmutableOperation()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, Vector3.one, placement: true));
            var plan = Plan(0f);
            typeof(SbRetargetTransform).GetField("_realizationCommitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_retarget, true);
            try { _driver.Apply(plan); }
            finally
            {
                typeof(SbRetargetTransform).GetField("_realizationCommitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(_retarget, false);
            }
            Assert.That(_driver.TargetConsumer.PendingCount, Is.EqualTo(1));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(new Vector3(1f, 1.6f, 2f)));
            Assert.That(_driver.TargetEventError, Is.Not.Null);
        }

        [Test]
        public void PartialPlacementFailureRetainsHistoryAndStopsRatherThanClaimingRollback()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position,
                new Vector3(5f, 1.6f, 4f), placement: true));
            var plan = Plan(0f);
            var lease = (SbRetargetTransform.RealizationLease)typeof(OvrAvatarEmbodiedDriver)
                .GetField("_checkedLease", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_driver);
            lease.Revision = ulong.MaxValue - 1;
            // Bind this authored failure injection to the plan's actual pre-write revision.
            plan = _driver.Plan(default, new ProposalSet(), new List<ActorEvent>(), 0f);
            int published = _core.Publications.Count;
            _driver.Apply(plan);
            Assert.That(_driver.TargetConsumer.PendingCount, Is.EqualTo(1));
            Assert.That(_driver.TargetEventError, Is.Not.Null);
            Assert.That(_core.Publications.Count, Is.EqualTo(published));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(new Vector3(5f, 1.6f, 4f)),
                "the root really committed before head revision exhaustion; no synthetic rollback is reported");
            Assert.That(_driver.Plan(default, new ProposalSet(), new List<ActorEvent>(), .1f).IsEmpty, Is.True);
        }

        [Test]
        public void NewPlanAndCloseRejectStaleApplicationAndOldOwnerCannotWriteReplacement()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, Vector3.right * 8f));
            var stale = Plan(.1f);
            var current = Plan(.1f);
            Vector3 before = _retarget.headTransform.position;
            _driver.Apply(stale);
            Assert.That(_retarget.headTransform.position, Is.EqualTo(before));
            _driver.Apply(current);
            Assert.That(_retarget.headTransform.position, Is.Not.EqualTo(before));
            var after = _retarget.headTransform.position;
            _driver.Close();
            _driver.Apply(current);
            Assert.That(_retarget.headTransform.position, Is.EqualTo(after));
            Assert.That(_bridge.enabled, Is.False);
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, false }));
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out var replacement), Is.True);
            replacement.Dispose();
        }

        [Test]
        public void RefusedOptInRestoresOwnedBridgeAndNeverPollsAwayLegacyHistory()
        {
            _core.RefuseEnable = true;
            _core.Enqueue(new ActorEvent { Kind = ActorEventKind.SourceException, Revision = 1 });
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            Assert.That(_core.Events.Count, Is.EqualTo(1));
            Assert.That(_core.TypedPolls + _core.BoolPolls, Is.Zero);
            Assert.That(_bridge.enabled, Is.True);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False,
                "legacy bridge override ownership was restored, not silently stolen");
        }

        [Test]
        public void ThrowingOptInAttemptsTeardownAndCannotFallBackToLegacyOrReadmit()
        {
            _core.ThrowEnable = true;
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, false }));
            Assert.That(_bridge.enabled, Is.False);
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            var replacement = new OvrAvatarEmbodiedDriver(_entity, Actor, _core, EmbodiedDriverConfig.Default, new NoEmbodiment());
            Assert.That(replacement.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out string error), Is.False);
            Assert.That(error, Does.Contain("already has checked history"));
            replacement.Close();
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void AsynchronousContractIsRefusedBeforeSubscription()
        {
            Assert.That(_driver.TryEnableTargetEvents(Contract.ReadOnlyOrAsynchronous, out _), Is.False);
            Assert.That(_core.Subscriptions, Is.Empty);
        }

        [Test]
        public void ManagerDrainsAndCompletesStoppedGoalsWithoutProposalSnapshots()
        {
            Enable();
            _managerObject = new GameObject("inactive-typed-manager");
            _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<TargetManager>();
            SetManagerField(manager, "_connectOnAwake", false);
            SetManagerField(manager, "_spaceID", 1L);
            SetManagerField(manager, "_embodiedCore", _core);
            _entity.Attach(manager, 11L, false);
            ((IDictionary)GetManagerField(manager, "_v2ActorByEntity")).Add(11L, Actor);
            ((IDictionary)GetManagerField(manager, "_v2EntityByActor")).Add(Actor, 11L);
            ((IDictionary)GetManagerField(manager, "_v2EmbodiedDrivers")).Add(Actor, _driver);
            manager.TestOnlyDriverSeconds = .1f;
            Vector3 start = _retarget.headTransform.position;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, start + Vector3.forward));
            for (int frame = 0; frame < 10; ++frame)
            {
                object[] args = { _entity, Actor, null };
                typeof(SpatialBlendManager).GetMethod("CollectDrivenAvatarWrite", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, args);
                typeof(SpatialBlendManager).GetMethod("ApplyDrivenAvatarWrites", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, new[] { args[2] });
            }
            Assert.That(_core.SnapshotReads, Is.GreaterThanOrEqualTo(10));
            Assert.That(_core.TypedPolls, Is.GreaterThanOrEqualTo(10));
            Assert.That(_retarget.headTransform.position.z, Is.EqualTo(start.z + 1f).Within(.001f));
            Assert.That(_core.BoolPolls, Is.Zero);
        }

        // ABI 4.14 through the manager's frame (CollectDrivenAvatarWrite under its entity lock, ApplyDrivenAvatarWrites outside it), on
        // the checked path, the only one: a set with the head's chart gives the hands the chart; a frame with no set releases it.
        // Collecting writes nothing: the chart reaches the retarget only in the deferred apply.
        [Test]
        public void TheManagersFrameCarriesTheHeadChartAndAFrameWithoutASetReleasesIt()
        {
            Enable();
            TargetManager manager = AttachManager();
            manager.TestOnlyDriverSeconds = 1f / 30f;
            MethodInfo collect = typeof(SpatialBlendManager).GetMethod("CollectDrivenAvatarWrite", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo apply = typeof(SpatialBlendManager).GetMethod("ApplyDrivenAvatarWrites", BindingFlags.Instance | BindingFlags.NonPublic);
            object Collect() { object[] args = { _entity, Actor, null }; collect.Invoke(manager, args); return args[2]; }

            _core.Snapshot = new List<AnchorProposal>
            {
                new AnchorProposal { AnchorId = 0, Status = EmbodiedStatus.None },
                new AnchorProposal
                {
                    AnchorId = Head, Status = EmbodiedStatus.None, HasChart = true, ChartPosition = new Vector3(1f, 1.6f, 2f),
                    ChartYawDegrees = 90f,
                },
            };
            object writes = Collect();
            Assert.That(writes, Is.Not.Null, "a plan with a chart is a write");
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.False, "collecting writes nothing");
            apply.Invoke(manager, new[] { writes });
            Assert.That(_retarget.TryGetHeadChartState(out _, out float yaw, out _), Is.True);
            Assert.That(yaw, Is.EqualTo(90f));

            _core.Snapshot = null;
            writes = Collect();
            Assert.That(writes, Is.Not.Null, "the release is a write");
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.True, "collecting writes nothing");
            apply.Invoke(manager, new[] { writes });
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out float weight), Is.False, "released");
            Assert.That(weight, Is.EqualTo(0f), "it had not blended in yet, so nothing is left to blend out");
            object again = Collect();
            if (again != null) apply.Invoke(manager, new[] { again });
            Assert.That(_retarget.TryGetHeadChartState(out _, out _, out _), Is.False);
        }

        private TargetManager AttachManager(long actor = Actor)
        {
            _managerObject = new GameObject("inactive-lifetime-manager");
            _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<TargetManager>();
            SetManagerField(manager, "_connectOnAwake", false);
            SetManagerField(manager, "_spaceID", 1L);
            SetManagerField(manager, "_embodiedCore", _core);
            _entity.Attach(manager, 11L, false);
            ((IDictionary)GetManagerField(manager, "_v2ActorByEntity")).Add(11L, actor);
            ((IDictionary)GetManagerField(manager, "_v2EntityByActor")).Add(actor, 11L);
            ((IDictionary)GetManagerField(manager, "_v2EmbodiedDrivers")).Add(actor, _driver);
            return manager;
        }

        private void ReconfigureRetainedActor(TargetManager manager)
        {
            // Exercise the actual common tail of LinkToExisting/CompleteDeferredShare,
            // including EnsureV2Actor(existing), without invoking native share APIs.
            _avatar.tag = "Avatar";
            typeof(SpatialBlendManager).GetMethod("EnsureV2ActorForAvatar", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { _avatar, _entity.ID });
        }

        private OvrAvatarEmbodiedDriver ManagerDriver(TargetManager manager, long actor = Actor) =>
            (OvrAvatarEmbodiedDriver)((IDictionary)GetManagerField(manager, "_v2EmbodiedDrivers"))[actor];

        private void PlaceTwice()
        {
            for (ulong revision = 1; revision <= 2; ++revision)
            {
                _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, revision, TargetChannels.Position,
                    new Vector3(1f + revision, 1.6f, 2f), placement: true));
                Frame(0f);
            }
            Assert.That(_core.Clocks[(Actor, 0u)].epoch, Is.EqualTo(3ul));
        }

        [Test]
        public void RetainedClockRejectsOldEpochAndSequenceEvenAcrossDeliveryToggles()
        {
            Enable();
            PlaceTwice();
            var clock = _core.Clocks[(Actor, 0u)];
            Assert.That(_core.TrySetTargetEventDelivery(Actor, false, out _), Is.True);
            Assert.That(_core.TrySetTargetEventDelivery(Actor, true, out _), Is.True);
            var pose = new EmbodiedPose(_retarget.headTransform.position, 0f);
            Assert.That(_core.TryPublishAnchorPose(Actor, 0, pose, 0d, 0d, 999, clock.epoch - 1, false, out _), Is.False);
            Assert.That(_core.TryPublishAnchorPose(Actor, 0, pose, 0d, 0d, clock.sequence, clock.epoch, false, out _), Is.False);
            Assert.That(_core.Clocks[(Actor, 0u)], Is.EqualTo(clock));
            Assert.That(_core.StalePublications, Is.EqualTo(2));
        }

        [Test]
        public void ExistingActorShareTailPreservesAcceptedHistoryLeaseGoalsAndOrdinaryYawAfterTwoPlacements()
        {
            Enable();
            PlaceTwice();
            var manager = AttachManager();
            var consumer = _driver.TargetConsumer;
            var current = consumer.Current(0);
            var previous = consumer.Previous(0);
            var before = ReadSample();
            object registration = _driver.RegistrationToken;
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 3, TargetChannels.Yaw,
                unityYaw: Degrees(before.RootYawRadians) + 30d, sample: current));
            // Detach an ordinary goal before reconfiguration; it must keep progressing
            // after the source stops, not disappear with a replacement consumer.
            Frame(.01f);
            var retainedCurrent = consumer.Current(0);
            var retainedPrevious = consumer.Previous(0);
            ReconfigureRetainedActor(manager);
            Assert.That(ManagerDriver(manager), Is.SameAs(_driver));
            Assert.That(_driver.TargetConsumer, Is.SameAs(consumer));
            Assert.That(consumer.Current(0), Is.SameAs(retainedCurrent));
            Assert.That(consumer.Previous(0), Is.SameAs(retainedPrevious));
            Assert.That(ReadSample().Incarnation, Is.EqualTo(before.Incarnation));
            Assert.That(ManagerDriver(manager).RegistrationToken, Is.SameAs(registration));
            Assert.That(current.Stamp.Epoch, Is.EqualTo(3ul));
            Assert.That(previous.Stamp.Epoch, Is.EqualTo(2ul));
            for (int i = 0; i < 60; ++i) Frame(1f / 60f);
            // And consume a newly emitted ordinary turn against the retained native
            // clock after reconfiguration, not just the already detached goal.
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 4, TargetChannels.Yaw,
                unityYaw: Degrees(before.RootYawRadians) + 45d, sample: consumer.Current(0)));
            for (int i = 0; i < 60; ++i) Frame(1f / 60f);
            Assert.That(Degrees(ReadSample().RootYawRadians - before.RootYawRadians), Is.EqualTo(45d).Within(.005d));
            Assert.That(consumer.DrainFailed, Is.False);
            Assert.That(_driver.TargetEventError, Is.Null);
            Assert.That(_core.StalePublications, Is.Zero);
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true }));
            Assert.That(manager.EmbodiedDriverDegraded, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UncertainUnsubscribeCannotReconfigureOrAdmitNewHistoryForSameActor(bool throws)
        {
            Enable();
            PlaceTwice();
            var manager = AttachManager();
            _core.ThrowDisable = throws;
            _core.RefuseDisable = !throws;
            _driver.Close();
            int transitions = _core.Subscriptions.Count;
            int publications = _core.Publications.Count;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("checked actor cannot be reconfigured"));
            ReconfigureRetainedActor(manager);
            Assert.That(ManagerDriver(manager), Is.SameAs(_driver));
            Assert.That(manager.EmbodiedDriverDegraded, Is.True);
            var replacement = new OvrAvatarEmbodiedDriver(_entity, Actor, _core, EmbodiedDriverConfig.Default, new NoEmbodiment());
            Assert.That(replacement.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out string error), Is.False);
            Assert.That(error, Does.Contain("already has checked history"));
            replacement.PublishPose();
            replacement.PollEvents(new List<ActorEvent>());
            replacement.Close();
            Assert.That(_core.Subscriptions.Count, Is.EqualTo(transitions));
            Assert.That(_core.Publications.Count, Is.EqualTo(publications));
            Assert.That(_core.BoolPolls + _core.LegacyPublications, Is.Zero);
        }

        [Test]
        public void RemovalAfterUncertainUnsubscribeCannotReadmitSameNativeActorThroughManager()
        {
            Enable();
            PlaceTwice();
            var manager = AttachManager();
            _core.ThrowDisable = true;
            _entity.Detach();
            int transitions = _core.Subscriptions.Count;
            _entity.Attach(manager, 12L, false);
            ((IDictionary)GetManagerField(manager, "_v2ActorByEntity")).Add(12L, Actor);
            ((IDictionary)GetManagerField(manager, "_v2EntityByActor")).Add(Actor, 12L);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("checked target-event admission refused:.*already has checked history"));
            manager.ConfigureEmbodiedDriver(_entity, Actor);
            Assert.That(((IDictionary)GetManagerField(manager, "_v2EmbodiedDrivers")).Contains(Actor), Is.False);
            Assert.That(manager.EmbodiedDriverDegraded, Is.True);
            Assert.That(_core.Subscriptions.Count, Is.EqualTo(transitions));
            Assert.That(_core.Clocks[(Actor, 0u)].epoch, Is.EqualTo(3ul));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedDrainCannotBeRepairedBySameActorConfigureOrReplacement(bool conversion)
        {
            Enable();
            PlaceTwice();
            var manager = AttachManager();
            var consumer = _driver.TargetConsumer;
            _core.Events.Enqueue((conversion ? ActorEventPollResult.ConversionError : ActorEventPollResult.NativeError,
                default, new ActorEventPollFailure(-1, "injected lifetime failure")));
            Frame(0f);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("checked actor cannot be reconfigured"));
            ReconfigureRetainedActor(manager);
            Assert.That(ManagerDriver(manager), Is.SameAs(_driver));
            Assert.That(_driver.TargetConsumer, Is.SameAs(consumer));
            Frame(0f); // A later true empty still cannot heal the failed drain.
            Assert.That(consumer.DrainFailed, Is.True);
            _driver.Close();
            var replacement = new OvrAvatarEmbodiedDriver(_entity, Actor, _core, EmbodiedDriverConfig.Default, new NoEmbodiment());
            Assert.That(replacement.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out string error), Is.False);
            Assert.That(error, Does.Contain("already has checked history"));
            replacement.Close();
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void RemovalWithdrawsHeadTargetAndFreshNativeActorCanStartWithoutReusingOldClock()
        {
            Enable();
            PlaceTwice();
            var manager = AttachManager();
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 3, TargetChannels.Yaw,
                unityYaw: 60d, sample: _driver.TargetConsumer.Current(Head)));
            for (int i = 0; i < 60; ++i) Frame(1f / 60f);
            Assert.That(Mathf.DeltaAngle(_retarget.headTransform.eulerAngles.y, 60f), Is.EqualTo(0f).Within(.01f));
            var oldClock = _core.Clocks[(Actor, 0u)];
            _entity.Detach(); // Production RemoveEntity -> ForgetEmbodiedDriverLocked -> Close.
            Assert.That(((IDictionary)GetManagerField(manager, "_v2EmbodiedDrivers")).Contains(Actor), Is.False);
            Assert.That(Mathf.DeltaAngle(_retarget.headTransform.eulerAngles.y, 0f), Is.EqualTo(0f).Within(.01f),
                "clean target withdrawal really restores the established body/head baseline");
            var stale = new OvrAvatarEmbodiedDriver(_entity, Actor, _core, EmbodiedDriverConfig.Default, new NoEmbodiment());
            Assert.That(stale.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            stale.Close();
            // Model a genuinely allocated new native actor (Space::CreateActor has
            // monotonically new IDs and a new mailbox), not a subscription reset.
            const long freshActor = Actor + 1;
            _entity.Attach(manager, 12L, false);
            ((IDictionary)GetManagerField(manager, "_v2ActorByEntity")).Add(12L, freshActor);
            ((IDictionary)GetManagerField(manager, "_v2EntityByActor")).Add(freshActor, 12L);
            manager.ConfigureEmbodiedDriver(_entity, freshActor);
            _driver = ManagerDriver(manager, freshActor);
            Assert.That(_driver.TargetLifetimeOpen, Is.True);
            Assert.That(_core.Clocks[(freshActor, 0u)].epoch, Is.EqualTo(1ul));
            Assert.That(_core.Clocks[(Actor, 0u)], Is.EqualTo(oldClock));
            Assert.That(_driver.TargetConsumer.Current(0).Actor, Is.EqualTo(freshActor));
            Assert.That(manager.EmbodiedDriverDegraded, Is.False);
            _core.Enqueue(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw,
                unityYaw: 15d, sample: _driver.TargetConsumer.Current(0)));
            for (int i = 0; i < 60; ++i) Frame(1f / 60f);
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(15d).Within(.005d));
            Assert.That(_driver.TargetEventError, Is.Null);
        }

        [TestCase(10000000d, .02d)]
        [TestCase(-10000000d, -.02d)]
        public void HugeContinuousRootDomainKeepsTinyMeasuredTurnsWithoutFloatNarrowingOrRebase(double turns, double stepDegrees)
        {
            Enable();
            Frame(0f); // Complete the initial publication fence.
            var lease = (SbRetargetTransform.RealizationLease)typeof(OvrAvatarEmbodiedDriver)
                .GetField("_checkedLease", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_driver);
            // Synthetic historical-domain fixture, NOT a claim that a requested
            // angle was observed or ten million turns were executed in this test.
            // Integral turns change only the domain origin; all poses stay actual.
            lease.RootYaw += turns * 2d * Math.PI;
            lease.HeadYaw += turns * 2d * Math.PI;
            _driver.PublishPose();
            double start = ReadSample().RootYawRadians;
            Assert.That(Math.Abs(start), Is.GreaterThan(1000000d));
            for (ulong revision = 1; revision <= 3; ++revision)
            {
                var before = ReadSample();
                var accepted = _driver.TargetConsumer.Current(0);
                _core.Enqueue(Request(ActorEventKind.YawRequest, 0, revision, TargetChannels.Yaw,
                    unityYaw: Degrees(before.RootYawRadians) + stepDegrees, sample: accepted));
                Frame(.1f);
                var after = ReadSample();
                double measured = Mathf.DeltaAngle(before.RootPlacement.rotation.eulerAngles.y,
                    after.RootPlacement.rotation.eulerAngles.y) * Math.PI / 180d;
                Assert.That(Degrees(measured), Is.EqualTo(stepDegrees).Within(.001d));
                Assert.That(after.RootYawRadians - before.RootYawRadians, Is.EqualTo(measured).Within(.000001d));
                Assert.That(after.RootYawRadians, Is.EqualTo(start + Radians(revision * stepDegrees)).Within(.00001d));
                Assert.That(_driver.TargetEventError, Is.Null);
                Assert.That(_driver.TargetConsumer.DrainFailed, Is.False);
                Assert.That(_driver.TargetConsumer.Current(0).UnityYawRadians, Is.EqualTo(after.RootYawRadians));
            }
        }

        [Test]
        public void DirectHeadAbsoluteHalfTurnUsesPlacementNotAnAmbiguousOrdinaryWitness()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw, unityYaw: -180d, placement: true));
            Frame(0f);
            Assert.That(Degrees(ReadSample().HeadYawRadians), Is.EqualTo(180d).Within(.003d));
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 2, TargetChannels.Yaw, unityYaw: 0d, placement: true));
            Frame(0f);
            Assert.That(Degrees(ReadSample().HeadYawRadians), Is.EqualTo(360d).Within(.003d));
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(0d));
        }

        [Test]
        public void SourcePitchIsPassedToBaselineWhileFacingRemainsIndependent()
        {
            Enable();
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw,
                unityYaw: 90d, sample: _driver.TargetConsumer.Current(Head), pitch: 20d, orientationType: 1));
            for (int i = 0; i < 60; ++i) Frame(1f / 60f);
            Vector3 angles = _retarget.headTransform.rotation.eulerAngles;
            Assert.That(Mathf.DeltaAngle(angles.x, 20f), Is.EqualTo(0f).Within(.05f));
            Assert.That(Mathf.DeltaAngle(angles.y, 90f), Is.EqualTo(0f).Within(.05f));
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(0d).Within(.003d));
        }

        [Test]
        public void CheckedRootKeepsExistingHeadSampleOriginWithNonzeroTrackedOffset()
        {
            // A genuine independently observed offset, not RootPlacement.position
            // relabeled as the existing entity sample origin.
            _bridge.enabled = false;
            _retarget.ApplyRootRealizationTransform(new Vector3(1f, 0f, 2f), Quaternion.identity);
            Assert.That(_retarget.SetHeadObservation(new Vector3(1.2f, 1.6f, 2.1f), Quaternion.identity), Is.True);
            _bridge.enabled = true;
            Assert.That(_retarget.headTransform.position, Is.EqualTo(new Vector3(1.2f, 1.6f, 2.1f)),
                "the genuine observed head is consistent before the real bridge suspension");
            Assert.That(_core.Publications, Is.Empty);
            Enable();
            var sample = ReadSample();
            Assert.That(sample.RootPlacement.position, Is.EqualTo(new Vector3(1f, 0f, 2f)));
            Assert.That(sample.Head.position, Is.EqualTo(new Vector3(1.2f, 1.6f, 2.1f)),
                "suspension and admission do not compose the tracked offset a second time");
            Assert.That(sample.Revision, Is.Zero);
            Assert.That(_core.Publications[0].anchor, Is.Zero);
            Assert.That(_core.Publications[1].anchor, Is.EqualTo(Head));
            Assert.That(_core.Publications[1].position, Is.EqualTo(sample.Head.position));
            Assert.That(sample.Head.position, Is.Not.EqualTo(sample.RootPlacement.position));
            Assert.That(_core.Publications[0].position, Is.EqualTo(sample.Head.position));
            Vector3 destination = sample.Head.position + Vector3.right;
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, destination));
            _core.Enqueue(Request(ActorEventKind.YawRequest, Head, 1, TargetChannels.Yaw,
                unityYaw: 120d, sample: _driver.TargetConsumer.Current(Head)));
            for (int i = 0; i < 90; ++i) Frame(1f / 60f);
            Assert.That(Vector3.Distance(ReadSample().Head.position, destination), Is.LessThan(.002f));
            // No root yaw is requested: the root turns toward the walk only while it walks (the course heading,
            // EmbodiedTargetGoal.StepPosition/StepYaw), at the v1 angular cap, and keeps its yaw on arrival. The walk starts from
            // rest under the acceleration bound, so it gives a heading for at least as long as a speed of k*a*dt
            // in step k takes to cover the metre less the snap distance (inside it there is no heading): 0.70 s, past the
            // 90 / 150 = 0.6 s the turn needs. The root now completes the 90 deg heading (v1's instant 2 m/s walk lasted 0.5 s: 75 deg).
            double frame = 1d / 60d, accel = EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared;
            double headingDistance = 1d - EmbodiedDriverConfig.SnapPositionMeters, covered = 0d;
            int headingSteps = 0;
            while (covered < headingDistance) covered += ++headingSteps * accel * frame * frame;
            double courseTurn = Math.Min(90d, EmbodiedDriverConfig.DefaultMaxAngularSpeedDegreesPerSecond * headingSteps * frame);
            Assert.That(courseTurn, Is.EqualTo(90d), "the walk lasts past the course heading");
            Assert.That(Degrees(ReadSample().RootYawRadians), Is.EqualTo(courseTurn).Within(.01d));
            Assert.That(Degrees(ReadSample().HeadYawRadians), Is.EqualTo(120d).Within(.01d));
        }

        [Test]
        public void BridgeSuspensionWithdrawsHeadAndBothHandsFromRetainedRootBeforeModeChange()
        {
            var baseline = SeedBridgeBaselines();
            Assert.That(_retarget.SetAnchorTarget("head",
                new Pose(Vector3.one * 8f, Quaternion.Euler(0f, 120f, 0f)), 1f), Is.True);
            Assert.That(_retarget.SetAnchorTarget("hand_l",
                new Pose(Vector3.one * 9f, Quaternion.Euler(0f, 130f, 0f)), 1f), Is.True);
            Assert.That(_retarget.SetAnchorTarget("hand_r",
                new Pose(Vector3.one * 10f, Quaternion.Euler(0f, 140f, 0f)), 1f), Is.True);
            Assert.That(_retarget.headTransform.position, Is.Not.EqualTo(baseline.head.position));
            Assert.That(_retarget.leftHandTransform.position, Is.Not.EqualTo(baseline.left.position));
            Assert.That(_retarget.rightHandTransform.position, Is.Not.EqualTo(baseline.right.position));
            var owner = new object();
            Assert.That(_bridge.TrySuspendForCheckedRealization(owner,
                _avatar.GetComponent<AnchorTargetController>(), _retarget), Is.True);
            AssertBridgeBaselines(baseline); // Localizes the boundary before lease acquisition.
            Assert.That(_entity.TryAcquireCheckedRealization(owner,
                Contract.SynchronousExclusiveRootAndHead, out var lease), Is.True);
            using (lease)
            {
                Assert.That(lease.TryRead(out var sample), Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
                Assert.That(sample.RootPlacement.position, Is.EqualTo(baseline.root.position));
                Assert.That(sample.Revision, Is.Zero);
                AssertBridgeBaselines(baseline);
                _bridge.EndCheckedSuspension(new object(), true); // A stale owner cannot resume the bridge.
                Assert.That(lease.TryRead(out var unchanged), Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
                Assert.That(unchanged.Revision, Is.Zero);
                AssertBridgeBaselines(baseline);
            }
            _bridge.EndCheckedSuspension(owner, false);
            _bridge.EndCheckedSuspension(owner, false);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            AssertBridgeBaselines(baseline);
            Object.DestroyImmediate(_bridge);
            AssertBridgeBaselines(baseline);
        }

        [TestCase("enabled", false)]
        [TestCase("enabled", true)]
        [TestCase("disabled", true)]
        [TestCase("absent", true)]
        public void BridgeAdmissionControlsPreserveActualOffsetOriginWithoutRebasing(string bridgeState, bool offset)
        {
            var baseline = SeedBridgeBaselines(offset);
            if (bridgeState != "enabled") SetBridgeEnabledAsInPlayMode(false);
            if (bridgeState == "absent") DestroyBridgeAsInPlayMode();
            AssertBridgeBaselines(baseline);
            Enable();
            var sample = ReadSample();
            Assert.That(sample.RootPlacement.position, Is.EqualTo(baseline.root.position));
            Assert.That(Vector3.Distance(sample.Head.position, baseline.head.position), Is.LessThan(.00001f));
            Assert.That(_core.Publications[0].position, Is.EqualTo(sample.Head.position));
            Assert.That(sample.Revision, Is.Zero);
            AssertBridgeBaselines(baseline);
            _driver.Close();
            _driver.Close();
            AssertBridgeBaselines(baseline);
        }

        [Test]
        public void DisabledBoundBridgeCleanupCannotRecomposeOrOverwriteDirectLegacyPoses()
        {
            var baseline = SeedBridgeBaselines();
            _retarget.SetAnchorTargetOverridesEnabled(false); // The bridge is still bound.
            AssertBridgeBaselines(baseline);
            var head = new Pose(Vector3.one * 4f, Quaternion.Euler(12f, 70f, 6f));
            var left = new Pose(Vector3.one * 5f, Quaternion.Euler(3f, 50f, 8f));
            var right = new Pose(Vector3.one * 6f, Quaternion.Euler(4f, 40f, 7f));
            _retarget.headTransform.SetPositionAndRotation(head.position, head.rotation);
            _retarget.leftHandTransform.SetPositionAndRotation(left.position, left.rotation);
            _retarget.rightHandTransform.SetPositionAndRotation(right.position, right.rotation);
            Assert.That(_retarget.ClearAnchorTarget("head"), Is.True);
            Assert.That(_retarget.ClearAnchorTarget("hand_l"), Is.True);
            Assert.That(_retarget.ClearAnchorTarget("hand_r"), Is.True);
            _bridge.enabled = false; // Per-anchor clears precede another disable.
            _retarget.SetAnchorTargetOverridesEnabled(false);
            Object.DestroyImmediate(_bridge);
            AssertTrackedPose(_retarget.headTransform, head);
            AssertTrackedPose(_retarget.leftHandTransform, left);
            AssertTrackedPose(_retarget.rightHandTransform, right);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            AssertTrackedPose(_retarget.headTransform, head);
        }

        [Test]
        public void RefusedNativeEnableUnwindsAndRetriesWithoutHeadOrHandOffsetDrift()
        {
            var baseline = SeedBridgeBaselines();
            _core.RefuseEnable = true;
            for (int attempt = 0; attempt < 2; ++attempt)
            {
                Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead,
                    out string error), Is.False);
                Assert.That(error, Is.EqualTo("queued legacy exception"));
                Assert.That(_core.Publications, Is.Empty);
                Assert.That(_core.Clocks, Is.Empty);
                AssertBridgeBaselines(baseline);
            }
            _core.RefuseEnable = false;
            Enable();
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, true, true }));
            Assert.That(ReadSample().RootPlacement.position, Is.EqualTo(baseline.root.position));
            Assert.That(ReadSample().Revision, Is.Zero);
            AssertBridgeBaselines(baseline);
            _driver.Close();
            _driver.Close();
            AssertBridgeBaselines(baseline);
            Object.DestroyImmediate(_bridge);
            AssertBridgeBaselines(baseline);
        }

        [Test]
        public void ThrowingNativeEnableWithOffsetWithdrawsWithoutDriftAndCannotReadmit()
        {
            var baseline = SeedBridgeBaselines();
            _core.ThrowEnable = true;
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead,
                out string error), Is.False);
            Assert.That(error, Does.Contain("injected subscription failure"));
            Assert.That(_core.Subscriptions, Is.EqualTo(new[] { true, false }));
            Assert.That(_bridge.enabled, Is.False);
            AssertBridgeBaselines(baseline);
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            var replacement = new OvrAvatarEmbodiedDriver(_entity, Actor, _core,
                EmbodiedDriverConfig.Default, new NoEmbodiment());
            Assert.That(replacement.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead,
                out string replacementError), Is.False);
            Assert.That(replacementError, Does.Contain("already has checked history"));
            replacement.Close();
            _driver.Close();
            Assert.That(_core.Publications, Is.Empty);
            Assert.That(_core.Clocks, Is.Empty);
            AssertBridgeBaselines(baseline);
            Object.DestroyImmediate(_bridge);
            AssertBridgeBaselines(baseline);
        }

        [Test]
        public void NonidentityHeadLocalRotationStillRefusesBeforeSubscriptionWithoutOffsetDrift()
        {
            var baseline = SeedBridgeBaselines();
            baseline.head = new Pose(baseline.head.position,
                baseline.root.rotation * Quaternion.Euler(0f, 20f, 0f));
            Assert.That(_retarget.SetHeadObservation(baseline.head.position, baseline.head.rotation), Is.True);
            for (int attempt = 0; attempt < 2; ++attempt)
            {
                Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead,
                    out string error), Is.False);
                Assert.That(error, Does.Contain("admission refused"));
                Assert.That(_core.Subscriptions, Is.Empty);
                Assert.That(_core.Publications, Is.Empty);
                AssertBridgeBaselines(baseline);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ForeignFoundationGateCannotWithdrawOrRewriteObservedHeadAndHands(bool presenceOnly)
        {
            var baseline = SeedBridgeBaselines();
            SetBridgeEnabledAsInPlayMode(false);
            var owner = _root.AddComponent<FoundationAvatarRetargetEntity>();
            var presence = typeof(SbRetargetTransform).GetField("_foundationObservationPresenceMode",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (presenceOnly) presence.SetValue(_retarget, true);
            else Assert.That(_retarget.TryAcquireFoundationRetargetOwner(owner), Is.True);
            try
            {
                SetBridgeEnabledAsInPlayMode(true);
                Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead,
                    out _), Is.False);
                AssertBridgeBaselines(baseline);
                Assert.That(_core.Subscriptions, Is.Empty);
                Assert.That(_core.Publications, Is.Empty);
                if (presenceOnly) Assert.That(presence.GetValue(_retarget), Is.True);
                else Assert.That(_retarget.IsFoundationRetargetOwner(owner), Is.True);
            }
            finally
            {
                if (presenceOnly) presence.SetValue(_retarget, false);
                else _retarget.ReleaseFoundationRetargetOwner(owner);
            }
        }

        // EditMode does not dispatch OnEnable/OnDisable/OnDestroy to a MonoBehaviour without [ExecuteAlways]
        // (SbRetargetAnchorTargetBridge has none). A case whose subject is what the bridge's own lifecycle
        // releases dispatches it explicitly, as PlayMode would; otherwise a disabled or destroyed bridge would
        // leave its retarget override claimed, a state production never reaches.
        private void SetBridgeEnabledAsInPlayMode(bool enabled)
        {
            _bridge.enabled = enabled;
            InvokeBridgeLifecycle(enabled ? "OnEnable" : "OnDisable");
        }

        private void DestroyBridgeAsInPlayMode()
        {
            if (_bridge.enabled) SetBridgeEnabledAsInPlayMode(false);
            InvokeBridgeLifecycle("OnDestroy");
            Object.DestroyImmediate(_bridge);
        }

        private void InvokeBridgeLifecycle(string callback)
        {
            MethodInfo method = typeof(SbRetargetAnchorTargetBridge).GetMethod(callback,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, callback);
            method.Invoke(_bridge, null);
        }

        private (Pose root, Pose head, Pose left, Pose right) SeedBridgeBaselines(bool offset = true)
        {
            _bridge.enabled = false;
            var root = new Pose(new Vector3(1f, 0f, 2f), Quaternion.Euler(17f, 25f, 9f));
            _retarget.ApplyRootRealizationTransform(root.position, root.rotation);
            var head = new Pose(root.position + root.rotation * (offset ? new Vector3(.2f, 1.6f, .1f) : Vector3.zero),
                root.rotation);
            Assert.That(_retarget.SetHeadObservation(head.position, head.rotation), Is.True);
            _bridge.enabled = true;
            var left = new Pose(root.position + root.rotation * new Vector3(-.4f, 1.1f, .3f),
                root.rotation * Quaternion.Euler(4f, 8f, 2f));
            var right = new Pose(root.position + root.rotation * new Vector3(.5f, 1.2f, .2f),
                root.rotation * Quaternion.Euler(3f, -7f, 5f));
            Assert.That(_retarget.SetLeftHandObservation(left.position, left.rotation), Is.True);
            Assert.That(_retarget.SetRightHandObservation(right.position, right.rotation), Is.True);
            var baseline = (root, head, left, right);
            AssertBridgeBaselines(baseline);
            return baseline;
        }

        private void AssertBridgeBaselines((Pose root, Pose head, Pose left, Pose right) baseline)
        {
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion root), Is.True);
            Assert.That(Quaternion.Angle(root, baseline.root.rotation), Is.LessThan(.05f));
            AssertTrackedPose(_retarget.headTransform, baseline.head);
            AssertTrackedPose(_retarget.leftHandTransform, baseline.left);
            AssertTrackedPose(_retarget.rightHandTransform, baseline.right);
        }

        private static void AssertTrackedPose(Transform actual, Pose expected)
        {
            Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(.00001f));
            Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThan(.05f));
        }

        [Test]
        public void UnsupportedBoneEventsAreReportedWhileSupportedRootKeepsMoving()
        {
            Enable();
            Vector3 start = _retarget.headTransform.position;
            _core.Enqueue(Request(ActorEventKind.YawRequest, 5, 1, TargetChannels.Yaw));
            _core.Enqueue(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, start + Vector3.forward));
            Frame(.1f);
            Assert.That(_driver.UnsupportedTargetAnchors, Does.Contain(5u));
            Assert.That(_driver.TargetConsumer.DrainFailed, Is.False);
            Assert.That(_retarget.headTransform.position.z, Is.GreaterThan(start.z));
        }

        [Test]
        public void ForeignFoundationPresenceCannotBeClearedToForceAdmission()
        {
            var field = typeof(SbRetargetTransform).GetField("_foundationObservationPresenceMode", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_retarget, true);
            Assert.That(_driver.TryEnableTargetEvents(Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            Assert.That(field.GetValue(_retarget), Is.True);
            Assert.That(_core.Subscriptions, Is.Empty);
            field.SetValue(_retarget, false);
        }

        [Test]
        public void TeardownExceptionStillReleasesManagedOwnershipWithoutLegacyFallback()
        {
            Enable();
            _core.ThrowDisable = true;
            _driver.Close();
            int count = _core.Publications.Count;
            _driver.PublishPose();
            _driver.PollEvents(new List<ActorEvent>());
            Assert.That(_core.Publications.Count, Is.EqualTo(count));
            Assert.That(_core.BoolPolls, Is.Zero);
            Assert.That(_bridge.enabled, Is.False);
        }

        private SbRetargetTransform.RealizedSample ReadSample()
        {
            var lease = (SbRetargetTransform.RealizationLease)typeof(OvrAvatarEmbodiedDriver)
                .GetField("_checkedLease", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_driver);
            Assert.That(lease.TryRead(out var sample), Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
            return sample;
        }

        private static object GetManagerField(TargetManager manager, string name) =>
            typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
        private static void SetManagerField(TargetManager manager, string name, object value) =>
            typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, value);
    }
}
#endif
