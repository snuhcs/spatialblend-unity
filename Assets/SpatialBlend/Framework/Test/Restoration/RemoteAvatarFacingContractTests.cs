// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A Facing proposal must reach the received avatar. Facing is bound to an anchor the avatar samples (the
    /// core answers NO_SAMPLE for one it does not), and the embodied driver writes each anchor proposal through a real
    /// writer or reports it as unsupported. These use the real <see cref="SbNetworkAvatarEntity"/>, the real
    /// <see cref="OvrAvatarEmbodiedDriver"/>, and the writer the Freeform app attaches to every arriving avatar
    /// (<see cref="AnchorTargetController"/> and <see cref="SbRetargetAnchorTargetBridge"/>), not a fake entity that
    /// happens to have the anchor.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RemoteAvatarFacingContractTests
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

        // The production received avatar on the checked target-event path, the only one: the writer the app attaches,
        // the SDK avatar inactive (no native SDK load in EditMode), the retarget on an active object (its realization lease needs it),
        // the entity sampling the retarget head, admitted through TryEnableTargetEvents against a scripted target-event core.
        private OvrAvatarEmbodiedDriver BuildCheckedAvatar(out ManagerContainmentContractTests.CheckedCore core)
        {
            _avatar = new GameObject("received-avatar");
            _entity = _avatar.AddComponent<SbNetworkAvatarEntity>();
            var meta = _avatar.AddComponent<MetaAvatarEntity>();
            _sb = new GameObject("sb-avatar");
            _sb.SetActive(false);
            var sbAvatar = _sb.AddComponent<SbAvatarEntity>();
            _rigRoot = new GameObject("checked-retarget");
            _retarget = _rigRoot.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = Part(_rigRoot, "head");
            _retarget.leftHandTransform = Part(_rigRoot, "left");
            _retarget.rightHandTransform = Part(_rigRoot, "right");
            sbAvatar._retargetTransform = _retarget;
            meta._sbAvatar = sbAvatar;
            _retarget.SetHeadTransform(new Vector3(1f, 1.58f, 2f), Quaternion.identity);
            _entity.SetTransform(_retarget.headTransform);   // as SbNetworkAvatarEntity.SyncRemote leaves a received avatar
            var controller = _avatar.AddComponent<AnchorTargetController>();
            var bridge = _avatar.AddComponent<SbRetargetAnchorTargetBridge>();
            bridge.Configure(controller, _retarget);
            core = new ManagerContainmentContractTests.CheckedCore();
            var driver = new OvrAvatarEmbodiedDriver(_entity, Actor, core, EmbodiedDriverConfig.Default);
            Assert.That(driver.TryEnableTargetEvents(SbRetargetTransform.RealizationContract.SynchronousExclusiveRootAndHead,
                out string error), Is.True, error);
            driver.PublishPose();
            return driver;
        }

        private static Transform Part(GameObject parent, string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(parent.transform, false);
            return part;
        }

        // One frame of the received avatar as the manager runs it: publish, read the set, drain the typed events, plan, apply.
        private static void CheckedFrame(OvrAvatarEmbodiedDriver driver, float dt)
        {
            var proposals = new ProposalSet();
            EmbodiedPose embodied = driver.PublishPose();
            driver.TryReadProposals(proposals);
            var events = new List<ActorEvent>();
            driver.PollEvents(events);
            driver.Apply(driver.Plan(embodied, proposals, events, dt));
        }

        private static ActorEvent RootPosition(ulong revision, Vector3 position) =>
            EmbodiedTargetConsumerTests.Request(ActorEventKind.PositionRequest, 0, revision, TargetChannels.Position, position);

        private static ActorEvent RootYaw(OvrAvatarEmbodiedDriver driver, ulong revision, double unityYaw) =>
            EmbodiedTargetConsumerTests.Request(ActorEventKind.YawRequest, 0, revision, TargetChannels.Yaw, unityYaw: unityYaw,
                sample: driver.TargetConsumer.Current(0));

        private static ActorEvent HeadYaw(OvrAvatarEmbodiedDriver driver, ulong revision, double unityYaw) =>
            EmbodiedTargetConsumerTests.Request(ActorEventKind.YawRequest, ManagerContainmentContractTests.CheckedCore.HeadAnchor,
                revision, TargetChannels.Yaw, unityYaw: unityYaw,
                sample: driver.TargetConsumer.Current(ManagerContainmentContractTests.CheckedCore.HeadAnchor));

        // The production received avatar, as FreeformCollaborationApp.EnsureAnchorTargetController leaves it.
        private void BuildAvatar(bool withWriter)
        {
            _avatar = new GameObject("received-avatar");
            _avatar.SetActive(false);
            _entity = _avatar.AddComponent<SbNetworkAvatarEntity>();
            var meta = _avatar.AddComponent<MetaAvatarEntity>();
            _sb = new GameObject("sb-avatar");
            _sb.SetActive(false);
            var sbAvatar = _sb.AddComponent<SbAvatarEntity>();
            _retarget = _sb.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = Part("head");
            _retarget.leftHandTransform = Part("left");
            _retarget.rightHandTransform = Part("right");
            sbAvatar._retargetTransform = _retarget;
            meta._sbAvatar = sbAvatar;
            _retarget.SetHeadTransform(new Vector3(1f, 1.58f, 2f), Quaternion.identity);
            if (!withWriter) return;
            _avatar.SetActive(true); // the bridge binds only while active and enabled
            var controller = _avatar.AddComponent<AnchorTargetController>();
            var bridge = _avatar.AddComponent<SbRetargetAnchorTargetBridge>();
            bridge.Configure(controller, _retarget);
        }

        private Transform Part(string name) => Part(_sb, name);

        [Test]
        public void EveryRelationTheAppRegistersIsOnAnAnchorTheProductionAvatarSamples()
        {
            BuildAvatar(withWriter: true);
            var sampled = new HashSet<string>(_entity.RealizedAnchorNames) { "root" }; // the root is GetRealizedPose
            int facing = 0;
            foreach ((string anchor, RelationKind kind) in FreeformCollaborationApp.AvatarRelationBindings)
            {
                Assert.That(sampled, Does.Contain(anchor), $"{kind} is bound to '{anchor}', which the received avatar never samples: " +
                    "the core answers NO_SAMPLE and the avatar never turns toward the collaborator");
                if (kind == RelationKind.Facing) ++facing;
            }
            Assert.That(facing, Is.EqualTo(1));
        }

        // A Facing request on the head (a typed yaw request for the direct head) turns the received avatar's head at the driver's own
        // angular bound, through its checked realization, and carries no position.
        [Test]
        public void AFacingProposalOnTheHeadTurnsTheReceivedAvatarThroughTheRealWriterAtTheDriversBounds()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            Assert.That(core.HeadPublications, Is.Not.Empty, "the head was sampled, so the core can answer a relation on it");

            Vector3 before = _retarget.headTransform.position;
            core.Enqueue(HeadYaw(driver, 1, 90d));
            float dt = 1f / 30f;
            CheckedFrame(driver, dt);

            float yaw = Mathf.DeltaAngle(0f, _retarget.headTransform.rotation.eulerAngles.y);
            Assert.That(yaw, Is.GreaterThan(1f), "the avatar turned toward the collaborator");
            Assert.That(yaw, Is.LessThanOrEqualTo(EmbodiedDriverConfig.Default.MaxAngularSpeedDegreesPerSecond * dt + 1e-3f),
                "at the driver's own bound");
            Assert.That(Vector3.Distance(_retarget.headTransform.position, before), Is.LessThan(1e-4f),
                "Facing carries no position: the head did not move");
            Assert.That(driver.UnsupportedTargetAnchors, Is.Empty);
        }

        // A Facing-only anchor carries no position of its own. In world space it once pinned the head where the avatar arrived while the root
        // walked (a two-process run over the real Photon Cloud showed it). The head holds its place ON THE BODY: it moves with the root.
        [Test]
        public void AFacingOnlyAnchorHoldsItsPlaceOnTheBodyWhileTheRootWalks()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            Vector3 start = _retarget.headTransform.position;
            Vector3 destination = new Vector3(start.x + 4f, start.y, start.z + 1f);
            core.Enqueue(RootPosition(1, destination));
            core.Enqueue(HeadYaw(driver, 1, 0d));   // the head has a Facing goal (its current yaw) and no position goal
            float dt = 1f / 30f;
            int frames = 30;   // one second
            for (int frame = 0; frame < frames; ++frame) CheckedFrame(driver, dt);
            float travelled = Vector3.Distance(new Vector3(start.x, 0f, start.z), new Vector3(_retarget.headTransform.position.x, 0f, _retarget.headTransform.position.z));
            // The driver's bound from rest: the speed grows by a*dt a frame to the 2 m/s cap (0.5 s), then holds;
            // 4.1 m away the brake is not reached in a second (it begins cap^2 / 2a = 0.5 m out). That is 1.53 m, not 2 m.
            float cap = EmbodiedDriverConfig.Default.MaxLinearSpeedMetersPerSecond;
            float accel = EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared;
            float bound = 0f;
            for (int frame = 1; frame <= frames; ++frame) bound += Mathf.Min(cap, frame * accel * dt) * dt;
            Assert.That(Vector3.Distance(start, destination) - bound,
                Is.GreaterThan(cap * cap / (2f * EmbodiedDriverConfig.MaxLinearDecelerationMetersPerSecondSquared)));
            Assert.That(travelled, Is.EqualTo(bound).Within(1e-3f),
                "the head walked with the root, at the driver's bound (" + travelled + " of " + bound + " m)");
            Assert.That(_retarget.headTransform.position.y, Is.EqualTo(start.y).Within(1e-4f), "and kept its height");
        }

        // ---------------------------------------------------------------- locomotion heading and upper-body yaw

        private float BodyHeading()
        {
            _entity.GetRealizedPose(out _, out Quaternion body);
            return body.eulerAngles.y;
        }

        private float UpperBodyYaw() => _retarget.headTransform.rotation.eulerAngles.y;

        // Moving between objects (room scope): Spacing drives the root, and the root HEADS along its motion (no root yaw is requested).
        // Facing turns the UPPER body, the head (SB section 6), on top of the heading. When the avatar then stands, its body keeps its heading.
        [Test]
        public void WalkingBetweenObjectsTheRootHeadsAlongTheCourseWhileFacingTurnsOnlyTheUpperBody()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            Vector3 start = _retarget.headTransform.position;
            Vector3 destination = new Vector3(start.x + 6f, start.y, start.z);   // due +x: Unity heading 90 degrees
            const float facingYaw = 120f;   // the object to look at is elsewhere: 30 degrees off the heading
            float dt = 1f / 30f;
            core.Enqueue(RootPosition(1, destination));
            core.Enqueue(HeadYaw(driver, 1, facingYaw));
            for (int frame = 0; frame < 40; ++frame) CheckedFrame(driver, dt);   // 1.3 s: 2.7 m at the 2 m/s bound
            Assert.That(_retarget.headTransform.position.x - start.x, Is.GreaterThan(2f), "Spacing walked the root");
            Assert.That(Mathf.DeltaAngle(BodyHeading(), 90f), Is.EqualTo(0f).Within(1f), "the root heads along the walking direction (+x), not along the Facing yaw");
            Assert.That(Mathf.DeltaAngle(UpperBodyYaw(), facingYaw), Is.EqualTo(0f).Within(1f), "the upper body (head) is on the Facing yaw");
            Assert.That(Mathf.DeltaAngle(BodyHeading(), UpperBodyYaw()), Is.EqualTo(30f).Within(2f), "the upper body is turned on top of the heading, 30 degrees");

            // The avatar is told to stand where it is: the body keeps its heading, the upper body stays on Facing.
            Vector3 stopped = _retarget.headTransform.position;
            core.Enqueue(RootPosition(2, stopped));
            for (int frame = 0; frame < 20; ++frame) CheckedFrame(driver, dt);
            Assert.That(Mathf.DeltaAngle(BodyHeading(), 90f), Is.EqualTo(0f).Within(1f), "a standing body keeps its heading; it does not turn to the Facing yaw");
            Assert.That(Mathf.DeltaAngle(UpperBodyYaw(), facingYaw), Is.EqualTo(0f).Within(1f));
            Assert.That(Vector3.Distance(_retarget.headTransform.position, stopped), Is.LessThan(0.05f));
        }

        // Native SDK startup is unavailable on this host. Invoke the production provider initialization,
        // then read it through the actual SDK entity/input-manager API, without substituting a pose provider.
        public sealed class ContractTrackingInput : SbTransformInputManager
        {
            public void InitializeProviders() => base.OnTrackingInitialized();
        }

        [Test]
        public void PoseDiagnosticsExposeSdkInputAndDoNotInventAnAnimatorOrRenderedVisibility()
        {
            BuildAvatar(withWriter: false);
            var input = _sb.AddComponent<ContractTrackingInput>();
            input.SbTransform = _retarget;
            input.InitializeProviders();
            var sdkAvatar = _sb.GetComponent<SbAvatarEntity>();
            sdkAvatar.SetInputManager(input);
            var text = new System.Text.StringBuilder("{");
            AvatarPoseLogger.AppendSdkState(text, "sb", sdkAvatar);
            AvatarPoseLogger.AppendSdkState(text, "missing", null);
            AvatarPoseLogger.AppendAnimationState(text, null, null, null, false);
            string diagnostic = text.ToString();
            Assert.That(diagnostic, Does.Contain("\"is_local\":true"));
            Assert.That(diagnostic, Does.Contain("\"created\":false"));
            Assert.That(diagnostic, Does.Contain("\"input_available\":true"));
            Assert.That(diagnostic, Does.Contain("\"input_active\":true"));
            Assert.That(diagnostic, Does.Contain("\"input_head\":[1,1.58,2]"));
            Assert.That(diagnostic, Does.Contain("\"missing\":null"));
            Assert.That(diagnostic, Does.Contain("\"mecanim_found\":false"));
            Assert.That(diagnostic, Does.Contain("\"animator_readable\":false"));
            Assert.That(diagnostic, Does.Not.Contain("\"is_moving\""));
            Assert.That(diagnostic, Does.Not.Contain("\"move_speed\""));
        }

        // Exercise the production record writer without a live Fusion runner or native SDK startup.
        // Missing Sb, retarget, or head must not suppress the SDK/visibility state that is available.
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void RemotePoseDiagnosticsKeepRecordsWhenSbRetargetOrHeadIsMissing(bool hasSb, bool hasRetarget, bool hasHead)
        {
            BuildAvatar(withWriter: false);
            var meta = _avatar.GetComponent<MetaAvatarEntity>();
            var sb = _sb.GetComponent<SbAvatarEntity>();
            meta._sbAvatar = hasSb ? sb : null;
            sb._retargetTransform = hasRetarget ? _retarget : null;
            if (!hasHead) _retarget.headTransform = null;
            _retarget.leftHandTransform.position = Vector3.zero;
            _retarget.rightHandTransform.position = Vector3.zero;
            _entity.SetTransform(_avatar.transform);
            var behaviour = _avatar.AddComponent<SbAvatarBehaviourFusion>();
            var logger = _avatar.AddComponent<AvatarPoseLogger>();
            const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var sourceType = typeof(AvatarPoseLogger).GetNestedType("RemoteSource", System.Reflection.BindingFlags.NonPublic);
            Assert.That(sourceType, Is.Not.Null);
            object source = System.Activator.CreateInstance(sourceType, true);
            sourceType.GetField("Behaviour", instance).SetValue(source, behaviour);
            sourceType.GetField("Meta", instance).SetValue(source, meta);
            sourceType.GetField("Entity", instance).SetValue(source, _entity);
            sourceType.GetField("Sb", instance).SetValue(source, hasSb ? sb : null);
            var write = typeof(AvatarPoseLogger).GetMethod("LogRemote", instance);
            Assert.That(write, Is.Not.Null);
            string diagnostic = null;
            Application.LogCallback capture = (message, trace, type) =>
            {
                if (message.StartsWith("AVATARLOG ")) diagnostic = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                Assert.DoesNotThrow(() => write.Invoke(logger, new object[]
                    { 123L, 7, false, true, source, hasRetarget ? _retarget : null }));
            }
            finally { Application.logMessageReceived -= capture; }
            Assert.That(diagnostic, Does.StartWith("AVATARLOG {\"v\":1,\"kind\":\"pose\",\"who\":\"remote\",\"id\":7,\"ts_ms\":123"));
            Assert.That(diagnostic, Does.EndWith("}}"));
            Assert.That(diagnostic, Does.Contain(hasHead ? "\"head\":[1,1.58,2]" : "\"head\":null"));
            Assert.That(diagnostic, Does.Contain(hasHead ? "\"head_fwd\":[0,0,1]" : "\"head_fwd\":null"));
            Assert.That(diagnostic, Does.Contain(hasRetarget ? "\"lh\":[0,0,0]" : "\"lh\":null"));
            Assert.That(diagnostic, Does.Contain(hasRetarget ? "\"rh\":[0,0,0]" : "\"rh\":null"));
            Assert.That(diagnostic, Does.Contain("\"root\":[0,0,0]"));
            Assert.That(diagnostic, Does.Contain("\"root_fwd\":[0,0,1]"));
            Assert.That(diagnostic, Does.Contain("\"display_mode\":" + (int)behaviour.DisplayModeForDiagnostics));
            Assert.That(diagnostic, Does.Contain("\"interacting\":false,\"input_authority\":false,\"state_authority\":true"));
            Assert.That(diagnostic, Does.Contain(hasSb ? "\"sb\":{\"is_local\":" : "\"sb\":null"));
            Assert.That(diagnostic, Does.Contain("\"meta\":{\"is_local\":"));
            Assert.That(diagnostic, Does.Contain("\"created\":false,\"hidden\":"));
            Assert.That(diagnostic, Does.Contain("\"active\":false"));
            Assert.That(diagnostic, Does.Contain("\"mecanim_found\":false"));
            Assert.That(diagnostic, Does.Contain("\"animator_readable\":false"));
            Assert.That(diagnostic, Does.Not.Contain("\"is_moving\""));
        }

        // The visible body of a received avatar is the raw Meta body, moved by ContinuousCompensation so that its head joint is over the head
        // joint of the realized (Sb) avatar, which the SDK poses from the retarget head. The driver's root write must move the visible body.
        [Test]
        public void TheRootTheDriverWritesMovesTheVisibleMetaBodyThroughTheContinuousCompensation()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            var metaBody = new GameObject("meta-body").transform;
            Transform metaHead = new GameObject("meta-head").transform;
            metaHead.SetParent(metaBody, false);
            metaHead.localPosition = new Vector3(0f, 1.58f, 0f);
            metaBody.position = new Vector3(-7f, 0f, 9f);   // the raw stream plays wherever the source was recorded
            var compensation = new ContinuousCompensation();
            Assert.That(compensation.Start(metaBody, metaHead, _retarget.headTransform), Is.True);
            try
            {
                Vector3 start = _retarget.headTransform.position;
                core.Enqueue(RootPosition(1, new Vector3(start.x + 3f, start.y, start.z + 1f)));
                for (int frame = 0; frame < 60; ++frame)
                {
                    CheckedFrame(driver, 1f / 30f);
                    Assert.That(compensation.Apply(), Is.True);
                    Assert.That(Vector2.Distance(new Vector2(metaHead.position.x, metaHead.position.z),
                        new Vector2(_retarget.headTransform.position.x, _retarget.headTransform.position.z)), Is.LessThan(1e-3f),
                        "the visible body's head is over the realized head, frame " + frame);
                }
                Assert.That(metaHead.position.x - (-7f), Is.GreaterThan(5f), "the visible body went with the root (it started where the stream was recorded)");
                Assert.That(_retarget.headTransform.position.x - start.x, Is.GreaterThan(2f), "and the root really walked");
            }
            finally { Object.DestroyImmediate(metaBody.gameObject); }
        }

        // The upper body takes a turn above half a turn the way the core requested it (ABI 4.12; the typed yaw request carries the signed
        // turn from the accepted sample), not the short way round.
        [Test]
        public void TheUpperBodyTurnsByTheSignedTurnAboveHalfATurn()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            float previous = UpperBodyYaw();
            float travelled = 0f;
            core.Enqueue(HeadYaw(driver, 1, previous + 200d));   // +200 degrees from the sampled yaw, in the continuous domain
            for (int frame = 0; frame < 60; ++frame)   // 60 frames at 5 degrees
            {
                CheckedFrame(driver, 1f / 30f);
                float now = UpperBodyYaw();
                float step = Mathf.DeltaAngle(previous, now);
                Assert.That(step, Is.GreaterThanOrEqualTo(-1e-3f), "never against the requested direction");
                travelled += step;
                previous = now;
            }
            Assert.That(travelled, Is.EqualTo(200f).Within(1f), "the head turned 200 degrees the requested (positive) way, through 180");
        }

        // Near an object, an engaged chart gives the root its pose (position and yaw requests), and for an identical object that is the
        // source's relative transform, applied as it is: the root is not left heading along its walk, and not left short of the pose.
        [Test]
        public void NearAnIdenticalObjectTheRootTakesTheChartPositionAndYawAsTheyAre()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            Vector3 start = _retarget.headTransform.position;
            Vector3 chartPosition = new Vector3(start.x + 1.5f, start.y, start.z - 1.0f);
            const float chartYaw = 250f;
            float dt = 1f / 30f;
            float yawAtFirstFrames = float.NaN;
            core.Enqueue(RootPosition(1, chartPosition));
            core.Enqueue(RootYaw(driver, 1, BodyHeading() + Mathf.DeltaAngle(BodyHeading(), chartYaw)));
            for (int frame = 0; frame < 90; ++frame)
            {
                CheckedFrame(driver, dt);
                if (frame == 5) yawAtFirstFrames = BodyHeading();
            }
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yawAtFirstFrames, chartYaw)), Is.LessThan(Mathf.Abs(Mathf.DeltaAngle(0f, chartYaw))),
                "while it approaches, the root turns toward the chart's yaw (it does not head along its walk: the yaw is specified)");
            _retarget.headTransform.GetPositionAndRotation(out Vector3 position, out _);
            Assert.That(Vector2.Distance(new Vector2(position.x, position.z), new Vector2(chartPosition.x, chartPosition.z)), Is.LessThan(1e-3f),
                "the root's position is the chart pose exactly");
            Assert.That(Mathf.DeltaAngle(BodyHeading(), chartYaw), Is.EqualTo(0f).Within(0.2f), "and its heading is the chart's yaw exactly");
        }

        // A placement of the root moves the root by the request, and the head, which holds its place on the body, moves with it in the
        // same frame (it was left behind for a frame before).
        [Test]
        public void AFacingOnlyAnchorMovesWithARootPlacedByATeleportRequest()
        {
            OvrAvatarEmbodiedDriver driver = BuildCheckedAvatar(out var core);
            Vector3 start = _retarget.headTransform.position;
            Vector3 target = new Vector3(start.x + 3f, start.y, start.z - 2f);
            core.Enqueue(new ActorEvent { Kind = ActorEventKind.TeleportRequest, Revision = 1, AnchorId = 0, Position = target, YawDegrees = 0f });
            CheckedFrame(driver, 1f / 30f);
            Assert.That(Vector3.Distance(_retarget.headTransform.position, target), Is.LessThan(1e-3f), "the head arrived with the root, in the same frame");
        }

        // The head target is a yaw. It used to replace the whole rotation, so the pitch the root write carried (the source's head pitch,
        // passed through by the core) was zeroed whenever a head target was active: the received avatar looked level at everything.
        [Test]
        public void AHeadYawTargetKeepsTheHeadPitchTheRootWriteCarries()
        {
            BuildAvatar(withWriter: true);
            _retarget.ApplyRootRealizationTransform(new Vector3(1f, 1.58f, 2f), Quaternion.Euler(30f, 10f, 0f));
            _entity.ApplyAnchorTarget("head", new Vector3(1f, 1.58f, 2f), 90f, true, 1.0);
            Vector3 euler = _retarget.headTransform.rotation.eulerAngles;
            Assert.That(Mathf.DeltaAngle(euler.y, 90f), Is.EqualTo(0f).Within(0.01f), "the yaw is the target's");
            Assert.That(Mathf.DeltaAngle(euler.x, 30f), Is.EqualTo(0f).Within(0.01f), "the pitch is the root write's");
        }

        // An entity with no writer for an anchor target must say so. The checked driver writes the root and the head through its
        // own realization lease, so this is the realization-target handler's path: SbEntity.ApplyAnchorTarget with no AnchorTargetController.
        [Test]
        public void AnAnchorProposalForAnEntityWithNoWriterIsReportedAsUnsupportedNotDroppedSilently()
        {
            BuildAvatar(withWriter: false);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no anchor-target writer.*'head'"));
            _entity.ApplyAnchorTarget("head", new Vector3(1f, 1.58f, 2f), 90f, true, 1.0);
            _entity.ApplyAnchorTarget("head", new Vector3(1f, 1.58f, 2f), 90f, true, 1.0); // reported once per anchor, not every frame

            Assert.That(_entity.UnsupportedAnchorTargets, Is.EquivalentTo(new[] { "head" }));
        }
    }
}
#endif
