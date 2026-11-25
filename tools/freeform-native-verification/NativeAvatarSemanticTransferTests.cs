// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Semantic target transfer of a remote avatar between two DIFFERENT room layouts: when user A walks to / reaches for /
    // looks at a shared object in A's room, does A's avatar in B's room reach / look at B's COUNTERPART of that object?
    //
    // Real: two SpatialBlendManagers linked over the core transport (loopback), the real native core solving the relations the
    // Freeform app binds on its avatar (FreeformCollaborationApp.AvatarRelationBindings: Spacing on root, Facing on head), the
    // real embodied driver (2 m/s, 150 deg/s), the real SbRetargetTransform hand path (SetHandTransform, the head-relative
    // wire format MetaInputTrackingDelegate sends and SbAvatarBehaviourFusion.OnHandPositionsChanged applies).
    // The receiver is the exact production SbNetworkAvatarEntity with a genuine retarget baseline and checked lease.
    // Only source tracking and the in-process local-UID reconstruction stand in for devices/Fusion. An inactive SDK holder
    // supplies the entity's normal retarget reference, as in TargetEventDriverIntegrationTests; no SDK avatar is initialized.
    // This is checked-native root/direct-head coverage, not Fusion serialization, rendered skeleton or device coverage.
    //
    // The two layouts place the same three shared objects (table, chess board, whiteboard) differently, with different
    // proportions, so the mapping is not a single offset. Both directions are run (A acts and B observes, then B acts and A
    // observes). Every frame is written as JSONL (tools/avatar-multiuser/analyze_avatar_log.py reads the same schema from a
    // device run); the asserts here are the driver's own bounds, and the semantic errors are reported, not thresholded, where
    // the solver defines no target relation for them.
    public partial class NativeManagerIntegrationTests
    {
        private const long TableId = 201, ChessId = 202, WhiteboardId = 203;
        private const float StepSeconds = 0.1f;          // one source update and one driver frame (DriverTickSeconds)
        private const float WalkMetersPerSecond = 1.2f;
        private const float HeadHeight = 1.58f;           // EmbodiedDriverConfig.SpawnHeightMeters, the head anchor's height

        private sealed class SemanticObject
        {
            public string Name; public long SharedId; public Vector2[] Footprint; public float TouchHeight;
            public Vector2 Centroid { get { var c = Vector2.zero; foreach (var p in Footprint) c += p; return c / Footprint.Length; } }
            public Vector3? TouchOverride;
            public Vector3 TouchPoint => TouchOverride ?? new Vector3(Centroid.x, TouchHeight, Centroid.y);
        }

        private sealed class SemanticLayout
        {
            public string Name; public Vector2 FloorSize; public SemanticObject[] Objects;
            public SemanticObject Find(string name) => Objects.Single(o => o.Name == name);
        }

        private static Vector2[] Box(float x0, float z0, float x1, float z1) =>
            new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };

        // Room A: 6 x 5 m. Room B: 8 x 4.5 m, the objects elsewhere and the chess board and whiteboard swapped in relative position.
        private static SemanticLayout LayoutA() => new SemanticLayout
        {
            Name = "A", FloorSize = new Vector2(6f, 5f),
            Objects = new[]
            {
                new SemanticObject { Name = "table", SharedId = TableId, Footprint = Box(1.8f, 3.2f, 3.0f, 4.0f), TouchHeight = 0.75f },
                new SemanticObject { Name = "chess", SharedId = ChessId, Footprint = Box(4.2f, 1.2f, 4.8f, 1.8f), TouchHeight = 0.80f },
                new SemanticObject { Name = "whiteboard", SharedId = WhiteboardId, Footprint = Box(0.1f, 1.5f, 0.3f, 3.0f), TouchHeight = 1.30f },
            }
        };

        private static SemanticLayout LayoutB() => new SemanticLayout
        {
            Name = "B", FloorSize = new Vector2(8f, 4.5f),
            Objects = new[]
            {
                new SemanticObject { Name = "table", SharedId = TableId, Footprint = Box(5.4f, 0.8f, 6.8f, 1.6f), TouchHeight = 0.75f },
                new SemanticObject { Name = "chess", SharedId = ChessId, Footprint = Box(1.2f, 2.8f, 1.8f, 3.4f), TouchHeight = 0.80f },
                new SemanticObject { Name = "whiteboard", SharedId = WhiteboardId, Footprint = Box(7.7f, 1.0f, 7.9f, 3.0f), TouchHeight = 1.30f },
            }
        };

        // The same layout with only the named shared objects (the falsifier for a multi-referent weighting cause).
        private static SemanticLayout Only(SemanticLayout layout, params string[] names) => new SemanticLayout
        {
            Name = layout.Name + "-" + string.Join("+", names), FloorSize = layout.FloorSize,
            Objects = layout.Objects.Where(o => names.Contains(o.Name)).ToArray()
        };

        // ---- source tracking / local-UID transport; the receive-side owner is never this custom entity -------------------

        public sealed class SemanticAvatarEntity : SbLocalEntity
        {
            internal Func<SbNetworkAvatarEntity> CreateReceivedAvatar;

            internal override void PrepareSync(IntPtr objectData)
            {
                // Keep the existing local-entity UID wire format, but reconstruct the supported production owner instead
                // of cloning this source stand-in. The real manager attaches it and performs checked/native admission.
                Assert.That(CreateReceivedAvatar, Is.Not.Null);
                SbNetworkAvatarEntity received = CreateReceivedAvatar();
                Assert.That(received, Is.TypeOf<SbNetworkAvatarEntity>());
                Marshal.WriteInt32(objectData, received.gameObject.GetInstanceID());
            }

            public Transform Head;
            public SbRetargetTransform Retarget;
            private static readonly string[] HeadOnly = { SbRetargetAnchorTargetBridge.HeadAnchor };
            private static readonly MethodInfo SetEntityTransform =
                typeof(SbEntity).GetMethod("SetTransform", BindingFlags.Instance | BindingFlags.NonPublic);

            public void PlaceForArrival()
            {
                SetEntityTransform.Invoke(this, new object[] { Head });
                Retarget.SetHeadTransform(new Vector3(0f, 1.6f, 2f), Quaternion.LookRotation(Vector3.back, Vector3.up));
            }

            // As SbNetworkAvatarEntity: the root is written through the retarget head.
            public override void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation) =>
                Retarget.ApplyRootRealizationTransform(worldPosition, worldRotation);

            public override IReadOnlyList<string> RealizedAnchorNames => HeadOnly;

            public override bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
            {
                position = default; rotation = Quaternion.identity;
                if (anchorName != SbRetargetAnchorTargetBridge.HeadAnchor) return false;
                Head.GetPositionAndRotation(out position, out rotation);
                return true;
            }
        }

        public sealed class SemanticAvatarApp : SbApp
        {
            public readonly List<GameObject> Added = new List<GameObject>();
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType type)
            {
                if (type != OnObjectAddedType.Added) return;
                var entity = gameObject.GetComponent<SbNetworkAvatarEntity>();
                Assert.That(entity, Is.TypeOf<SbNetworkAvatarEntity>(), "The received owner must not be the source stand-in.");
                SbRetargetTransform retarget = entity.CheckedRetarget;
                Assert.That(retarget, Is.Not.Null);
                Assert.That(retarget.isActiveAndEnabled, Is.True);
                Assert.That(retarget.CanSuspendLegacyBridge, Is.True, "No foreign Foundation or checked owner may be displaced.");
                entity.SetTransform(retarget.headTransform);
                // The actual app arrival placement establishes the root/head baseline before manager admission.
                retarget.SetHeadTransform(new Vector3(0f, 1.6f, 2f), Quaternion.LookRotation(Vector3.back, Vector3.up));
                Added.Add(gameObject);
            }
            public override void OnRemove(GameObject gameObject) { }
        }

        private sealed class SemanticRig
        {
            public SpatialBlendManager Source, Receiver;
            public SemanticLayout SourceLayout, ReceiverLayout;
            public SemanticAvatarEntity SourceAvatar;
            public SbNetworkAvatarEntity Remote;
            public SbRetargetTransform Retarget => Remote.CheckedRetarget;
            public Transform Head => Retarget.headTransform;
            public OvrAvatarEmbodiedDriver Driver;
            public uint HeadAnchorId;
            public long SourceActor, ReceiverActor;
            public SemanticAvatarApp ReceiverApp;
        }

        private SemanticRig BuildSemanticRig(SemanticLayout sourceLayout, SemanticLayout receiverLayout)
        {
            SpatialBlendManager source = CreateManager("sem-source-" + sourceLayout.Name);
            SpatialBlendManager receiver = CreateManager("sem-receiver-" + receiverLayout.Name);
            int port = FreePort();
            source.Host(port);
            AssertConnected(source);
            receiver.Connect(LoopbackHost, port);
            AssertConnected(receiver);
            receiver.TestOnlyDriverSeconds = StepSeconds;

            foreach ((SpatialBlendManager manager, SemanticLayout layout) in new[] { (source, sourceLayout), (receiver, receiverLayout) })
            {
                Vector2 f = layout.FloorSize;
                Assert.That(manager.RegisterWalkableMesh(new[]
                {
                    new Vector3(0f, 0f, 0f), new Vector3(f.x, 0f, 0f), new Vector3(f.x, 0f, f.y), new Vector3(0f, 0f, f.y)
                }, new[] { 0, 1, 2, 0, 2, 3 }), Is.True);
                long room = (long)Field(manager, "_roomElementID");
                foreach (SemanticObject o in layout.Objects)
                {
                    long element = Convert.ToInt64(Api("AddElement", manager.ID, room));
                    Assert.That(element, Is.GreaterThan(0));
                    Api("SetElementSharedId", manager.ID, element, o.SharedId);
                    Api("SetElementTag", manager.ID, element, o.Name);
                    Api("SetElementPolygon", manager.ID, element, EnumValue("SbGeometryRole", 2), o.Footprint);
                }
            }

            var sourceApp = InactiveObject("sem-source-app").AddComponent<SemanticAvatarApp>();
            var receiverApp = InactiveObject("sem-receiver-app").AddComponent<SemanticAvatarApp>();
            var register = typeof(SpatialBlendManager).GetMethod("RegisterApp", BindingFlags.Instance | BindingFlags.NonPublic);
            register.Invoke(source, new object[] { sourceApp });
            register.Invoke(receiver, new object[] { receiverApp });

            // The avatar joins where the user's motion starts: a source actor's first update is its JOIN (placed at the mapped pose). Core 4.11
            // pursues the source's approach (a proposal is the pursued target, a hold stays), so a receiver that joined elsewhere and was then
            // never told of a jump would not catch up; the app tells it with a source exception on every real discontinuity, never silently.
            SemanticAvatarEntity avatar = CreateSemanticAvatar("sem-avatar", Script(sourceLayout)[0].Root);
            var rig = new SemanticRig { Source = source, Receiver = receiver, SourceLayout = sourceLayout, ReceiverLayout = receiverLayout,
                SourceAvatar = avatar, ReceiverApp = receiverApp };
            source.ShareGameObject(avatar.gameObject, true);
            Assert.That(WaitSemantic(rig, () => receiverApp.Added.Count == 1), Is.True, "The receiver never reconstructed the shared avatar.");
            RequireCheckedSemanticOwner(rig);
            // The relations the Freeform app binds on its avatar (FreeformCollaborationApp.AvatarRelationBindings).
            Assert.That(FreeformApp_AvatarRelationBindings(), Is.EqualTo(new[] { "root:Spacing", "head:Facing" }),
                "The fixture must bind exactly what the app binds.");
            Api("Bind", source.ID, rig.SourceActor, "root", EnumValue("SbKernelType", 0));
            Api("Bind", source.ID, rig.SourceActor, "head", EnumValue("SbKernelType", 1));
            return rig;
        }

        private static string[] FreeformApp_AvatarRelationBindings() =>
            SpatialBlend.Samples.FreeformCollaborationApp.AvatarRelationBindings.Select(b => b.Anchor + ":" + b.Kind).ToArray();

        private SemanticAvatarEntity CreateSemanticAvatar(string name, Vector3 position)
        {
            var avatar = new GameObject(name);
            _objects.Add(avatar);
            avatar.tag = "Avatar";
            avatar.transform.position = position;
            Transform head = new GameObject("head").transform;
            Transform left = new GameObject("left").transform;
            Transform right = new GameObject("right").transform;
            foreach (Transform part in new[] { head, left, right }) part.SetParent(avatar.transform, false);
            var retarget = avatar.AddComponent<SbRetargetTransform>();
            retarget.headTransform = head; retarget.leftHandTransform = left; retarget.rightHandTransform = right;
            var entity = avatar.AddComponent<SemanticAvatarEntity>();
            entity.AllowManipulation = false;
            // As FreeformCollaborationApp.EnsureAnchorTargetController: the app routes the core's anchor proposals (the head's
            // Facing) to the retarget through this controller and bridge.
            var controller = avatar.AddComponent<AnchorTargetController>();
            var bridge = avatar.AddComponent<SbRetargetAnchorTargetBridge>();
            bridge.Configure(controller, retarget);
            entity.Head = head; entity.Retarget = retarget;
            entity.CreateReceivedAvatar = () => CreateReceivedSemanticAvatar(name + "-received");
            return entity;
        }

        private SbNetworkAvatarEntity CreateReceivedSemanticAvatar(string name)
        {
            var avatar = new GameObject(name);
            _objects.Add(avatar);
            avatar.tag = "Avatar";
            var entity = avatar.AddComponent<SbNetworkAvatarEntity>();
            entity.AllowManipulation = false;
            // Never activate/load the SDK in this EditMode native gate. Only its normal retarget reference is needed.
            var sdk = InactiveObject(name + "-sdk-holder").AddComponent<SbAvatarEntity>();
            var root = new GameObject(name + "-retarget");
            _objects.Add(root);
            var retarget = root.AddComponent<SbRetargetTransform>();
            Transform Part(string part)
            {
                var child = new GameObject(part).transform;
                child.SetParent(root.transform, false);
                return child;
            }
            retarget.headTransform = Part("head");
            retarget.leftHandTransform = Part("left");
            retarget.rightHandTransform = Part("right");
            sdk._retargetTransform = retarget;
            typeof(SbNetworkAvatarEntity).GetField("_sbAvatar", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(entity, sdk);
            var controller = avatar.AddComponent<AnchorTargetController>();
            avatar.AddComponent<SbRetargetAnchorTargetBridge>().Configure(controller, retarget);
            return entity;
        }

        private static void RequireCheckedSemanticOwner(SemanticRig rig)
        {
            rig.Remote = rig.ReceiverApp.Added.Single().GetComponent<SbNetworkAvatarEntity>();
            Assert.That(rig.Remote, Is.TypeOf<SbNetworkAvatarEntity>());
            Assert.That(rig.Remote.IsSource, Is.False);
            Assert.That(rig.Remote.EntityTransform, Is.SameAs(rig.Head), "Root sampling must use the actual final head.");
            var actors = (IDictionary)Field(rig.Receiver, "_v2ActorByEntity");
            Assert.That(actors.Contains(rig.Remote.ID), Is.True, "The real receiver must register this entity's actor.");
            rig.ReceiverActor = (long)actors[rig.Remote.ID];
            rig.SourceActor = (long)((IDictionary)Field(rig.Source, "_v2ActorByEntity"))[rig.SourceAvatar.ID];
            var drivers = (IDictionary)Field(rig.Receiver, "_v2EmbodiedDrivers");
            Assert.That(drivers.Count, Is.EqualTo(1), rig.Receiver.EmbodiedDriverError);
            Assert.That(drivers.Contains(rig.ReceiverActor), Is.True, "No driver was admitted for the received entity.");
            Assert.That(drivers[rig.ReceiverActor], Is.TypeOf<OvrAvatarEmbodiedDriver>());
            rig.Driver = (OvrAvatarEmbodiedDriver)drivers[rig.ReceiverActor];
            Assert.That(rig.Driver.DrivenEntity, Is.SameAs(rig.Remote));
            Assert.That(rig.Receiver.EmbodiedDriverDegraded, Is.False, rig.Receiver.EmbodiedDriverError);
            var core = (IEmbodiedCore)Field(rig.Receiver, "_embodiedCore");
            Assert.That(core, Is.TypeOf<NativeEmbodiedCore>(), "A fake core cannot certify this native gate.");
            Assert.That(core.TryGetAnchorId(rig.ReceiverActor, "head", out rig.HeadAnchorId), Is.True);
            Assert.That(rig.HeadAnchorId, Is.Not.EqualTo(0u));
            ReadCheckedSemanticSample(rig);
        }

        private static SbRetargetTransform.RealizedSample ReadCheckedSemanticSample(SemanticRig rig)
        {
            Assert.That(((IDictionary)Field(rig.Receiver, "_v2EmbodiedDrivers"))[rig.ReceiverActor], Is.SameAs(rig.Driver));
            Assert.That(rig.Driver.TargetEventsEnabled && rig.Driver.TargetLifetimeOpen, Is.True, rig.Driver.TargetEventError);
            Assert.That(rig.Driver.TargetEventError, Is.Null);
            Assert.That(rig.Driver.TargetConsumer.DrainFailed, Is.False);
            Assert.That(rig.Driver.UnsupportedTargetAnchors, Is.Empty);
            var lease = (SbRetargetTransform.RealizationLease)PrivateField(rig.Driver, "_checkedLease");
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease.Owner, Is.SameAs(rig.Driver));
            Assert.That(lease.TryRead(out var sample), Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
            Assert.That(sample.Head.position, Is.EqualTo(rig.Head.position));
            Assert.That(sample.Head.rotation, Is.EqualTo(rig.Head.rotation));
            foreach (uint anchor in new[] { 0u, rig.HeadAnchorId })
            {
                var accepted = rig.Driver.TargetConsumer.Current(anchor);
                Assert.That(accepted, Is.Not.Null, "The native artifact must accept both root and direct-head publications.");
                Assert.That(accepted.Actor, Is.EqualTo(rig.ReceiverActor));
                Assert.That(accepted.Anchor, Is.EqualTo(anchor));
                Assert.That(accepted.Stamp.Sequence, Is.GreaterThan(0ul));
            }
            return sample;
        }

        private static bool LaterSemanticStamp(EmbodiedTargetConsumer.Stamp after, EmbodiedTargetConsumer.Stamp before) =>
            after.Epoch > before.Epoch || (after.Epoch == before.Epoch && after.Sequence > before.Sequence);

        [TestCase("layout")]
        [TestCase("authored-source")]
        [TestCase("authored-received")]
        public void NativeSemanticRigsAdmitSupportedCheckedOwnersBeforeMotion(string setup)
        {
            using (new MainThreadLease())
            {
                SemanticRig rig = setup == "layout" ? BuildSemanticRig(LayoutA(), LayoutB()) :
                    BuildAuthoredRig(9811L, Matrix4x4.Translate(new Vector3(4.5f, 0f, 1.5f)),
                        Matrix4x4.Translate(new Vector3(-2.5f, 0f, 6f)), setup == "authored-received");
                Assert.That(rig.SourceAvatar.IsSource, Is.True);
                Assert.That(ReadCheckedSemanticSample(rig).Incarnation, Is.GreaterThan(0ul));
            }
        }

        private static bool WaitSemantic(SemanticRig rig, Func<bool> done, int timeoutMs = 8000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (done()) return true;
                rig.Source.Update(); rig.Receiver.Update(); rig.Receiver.LateUpdate();
                System.Threading.Thread.Sleep(10);
            }
            return done();
        }

        // ---- the scripted source: a user's body motion -----------------------------------------------------------------

        private struct SourceSample
        {
            public string Phase, Target;
            public Vector3 Root;          // floor point under the head (Unity)
            public float YawDegrees, PitchDegrees;
            public Vector3 LeftHand, RightHand;   // world
        }

        private static Vector3 Head(SourceSample s) => s.Root + Vector3.up * HeadHeight;
        private static Quaternion HeadRotation(SourceSample s) => Quaternion.Euler(s.PitchDegrees, s.YawDegrees, 0f);

        private static float YawToward(Vector3 from, Vector3 to) => Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
        private static float PitchToward(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            return -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;   // Unity: positive pitch looks down
        }

        // The stand point 0.6 m from the object's nearest footprint edge, on the side of the room the user comes from.
        private static Vector3 StandPoint(SemanticObject o, Vector3 from, float distance = 0.6f)
        {
            Vector2 c = o.Centroid;
            Vector2 toward = new Vector2(from.x, from.z) - c;
            // The footprint's nearest face along the dominant axis of the approach.
            Vector2 min = o.Footprint[0], max = o.Footprint[0];
            foreach (var p in o.Footprint) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Vector2 half = (max - min) * 0.5f;
            Vector2 stand;
            if (Mathf.Abs(toward.x) / Mathf.Max(half.x, 0.01f) > Mathf.Abs(toward.y) / Mathf.Max(half.y, 0.01f))
                stand = new Vector2(c.x + Mathf.Sign(toward.x) * (half.x + distance), c.y);
            else
                stand = new Vector2(c.x, c.y + Mathf.Sign(toward.y) * (half.y + distance));
            return new Vector3(stand.x, 0f, stand.y);
        }

        // Hands at rest: 0.25 m to each side of the head, 0.55 m below it, 0.25 m forward, in the body-yaw frame.
        private static Vector3 RestHand(SourceSample s, float side) =>
            s.Root + Quaternion.Euler(0f, s.YawDegrees, 0f) * new Vector3(side * 0.25f, HeadHeight - 0.55f, 0.25f);

        private static List<SourceSample> Script(SemanticLayout layout)
        {
            var samples = new List<SourceSample>();
            Vector3 position = new Vector3(layout.FloorSize.x * 0.5f, 0f, layout.FloorSize.y * 0.5f);
            float yaw = 0f, pitch = 0f;
            Vector3 touchLeft = default;

            void Emit(string phase, string target, Vector3 rightHand, bool rightRaised)
            {
                var s = new SourceSample { Phase = phase, Target = target, Root = position, YawDegrees = yaw, PitchDegrees = pitch };
                s.LeftHand = RestHand(s, -1f);
                s.RightHand = rightRaised ? rightHand : RestHand(s, +1f);
                samples.Add(s);
            }

            void Hold(string phase, string target, float seconds)
            {
                for (float t = 0f; t < seconds; t += StepSeconds) Emit(phase, target, default, false);
            }

            void Turn(string phase, string target, float toYaw, float toPitch, float degreesPerSecond = 90f)
            {
                float dYaw = Mathf.DeltaAngle(yaw, toYaw);
                int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(dYaw) / (degreesPerSecond * StepSeconds)));
                float startYaw = yaw, startPitch = pitch;
                for (int i = 1; i <= steps; ++i)
                {
                    yaw = startYaw + dYaw * i / steps; pitch = Mathf.Lerp(startPitch, toPitch, (float)i / steps);
                    Emit(phase, target, default, false);
                }
            }

            void Walk(string phase, string target, Vector3 to, SemanticObject facing)
            {
                float distance = Vector3.Distance(position, to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(distance / (WalkMetersPerSecond * StepSeconds)));
                Vector3 start = position;
                float startYaw = yaw;
                // The user turns to the walking direction, walks, and ends facing the object.
                float walkYaw = YawToward(start, to);
                for (int i = 1; i <= steps; ++i)
                {
                    position = Vector3.Lerp(start, to, (float)i / steps);
                    float k = Mathf.Min(1f, i / (float)Mathf.Max(1, steps / 3));
                    yaw = startYaw + Mathf.DeltaAngle(startYaw, walkYaw) * k;
                    Emit(phase, target, default, false);
                }
                if (facing != null)
                {
                    float faceYaw = YawToward(position + Vector3.up * HeadHeight, facing.TouchPoint);
                    Turn(phase, target, faceYaw, 0f);
                }
            }

            void Reach(SemanticObject o)
            {
                Vector3 headPos = position + Vector3.up * HeadHeight;
                Vector3 rest = RestHand(new SourceSample { Root = position, YawDegrees = yaw }, +1f);
                Vector3 touch = o.TouchPoint;
                float lookYaw = YawToward(headPos, touch), lookPitch = PitchToward(headPos, touch);
                yaw = lookYaw; pitch = lookPitch;
                int steps = 8;
                for (int i = 1; i <= steps; ++i) Emit("reach", o.Name, Vector3.Lerp(rest, touch, (float)i / steps), true);
                for (float t = 0f; t < 3.0f; t += StepSeconds) Emit("touch", o.Name, touch, true);
                for (int i = steps - 1; i >= 0; --i) Emit("retract", o.Name, Vector3.Lerp(rest, touch, (float)i / steps), true);
                pitch = 0f;
                _ = touchLeft;
            }

            // Reach order table, whiteboard, chess; gaze order chess, whiteboard, chess; each limited to the objects the layout has.
            Func<string, SemanticObject> find = n => layout.Objects.FirstOrDefault(o => o.Name == n);
            var reachOrder = new[] { "table", "whiteboard", "chess" }.Select(find).Where(o => o != null).ToList();
            var lookOrder = new[] { "chess", "whiteboard", "chess" }.Select(find).Where(o => o != null).ToList();
            if (lookOrder.Count == 0) lookOrder.Add(reachOrder[0]);
            Hold("stand", "none", 2.0f);
            Walk("walk_" + reachOrder[0].Name, reachOrder[0].Name, StandPoint(reachOrder[0], position), reachOrder[0]);
            Reach(reachOrder[0]);
            foreach (SemanticObject o in lookOrder)
            {
                Vector3 headPos = position + Vector3.up * HeadHeight;
                Turn("look_" + o.Name, o.Name, YawToward(headPos, o.TouchPoint), PitchToward(headPos, o.TouchPoint));
                Hold("look_" + o.Name, o.Name, 3.0f);
            }
            foreach (SemanticObject o in reachOrder.Skip(1))
            {
                Walk("walk_" + o.Name, o.Name, StandPoint(o, position), o);
                Reach(o);
            }
            Hold("end", "none", 1.0f);
            return samples;
        }

        // ---- measurement -----------------------------------------------------------------------------------------------

        private sealed class FrameRecord
        {
            public int Index; public float T; public string Phase, Target;
            public SourceSample Source;
            public Vector3 RemoteHead, RemoteLeft, RemoteRight, RemoteHeadForward;
            public bool HasProposal; public Vector3 ProposalPosition; public float ProposalYaw; public bool ProposalPositionUnspecified;
            public AnchorProposal Root;
            public ulong CheckedRevision;
            public EmbodiedTargetConsumer.Stamp AcceptedRoot, AcceptedHead;
        }

        // Smallest plan-view angle between the gaze and any direction to the footprint (0 when the gaze ray crosses it).
        private static float GazeAnglePlan(Vector3 head, Vector3 forward, Vector2[] polygon)
        {
            Vector2 origin = new Vector2(head.x, head.z), dir = new Vector2(forward.x, forward.z);
            if (dir.sqrMagnitude < 1e-8f) return 180f;
            dir.Normalize();
            float best = 180f;
            for (int i = 0; i < polygon.Length; ++i)
            {
                Vector2 v = polygon[i] - origin;
                best = Mathf.Min(best, Vector2.Angle(dir, v));
            }
            if (RayCrossesPolygon(origin, dir, polygon)) return 0f;
            return best;
        }

        private static bool RayCrossesPolygon(Vector2 o, Vector2 d, Vector2[] poly)
        {
            for (int i = 0; i < poly.Length; ++i)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Length];
                Vector2 e = b - a;
                float denom = d.x * e.y - d.y * e.x;
                if (Mathf.Abs(denom) < 1e-9f) continue;
                Vector2 ao = a - o;
                float t = (ao.x * e.y - ao.y * e.x) / denom;
                float u = (ao.x * d.y - ao.y * d.x) / denom;
                if (t >= 0f && u >= 0f && u <= 1f) return true;
            }
            return false;
        }

        private static float DistancePointPolygon(Vector2 p, Vector2[] poly)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Length; ++i)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Length], e = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, e) / e.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + e * t));
            }
            return best;
        }

        private static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "[{0:0.####},{1:0.####},{2:0.####}]", v.x, v.y, v.z);
        private static string Poly(Vector2[] p) => "[" + string.Join(",", p.Select(q => string.Format(CultureInfo.InvariantCulture, "[{0:0.###},{1:0.###}]", q.x, q.y))) + "]";

        private sealed class DirectionResult
        {
            public string Direction;
            public List<FrameRecord> Frames = new List<FrameRecord>();
            public string JsonlPath;
        }

        private DirectionResult RunDirection(SemanticLayout sourceLayout, SemanticLayout receiverLayout, string outDir) =>
            RunScript(BuildSemanticRig(sourceLayout, receiverLayout), Script(sourceLayout), sourceLayout, receiverLayout, outDir);

        private DirectionResult RunScript(SemanticRig rig, List<SourceSample> script, SemanticLayout sourceLayout, SemanticLayout receiverLayout, string outDir)
        {
            var initial = ReadCheckedSemanticSample(rig);
            var firstRoot = rig.Driver.TargetConsumer.Current(0).Stamp;
            var firstHead = rig.Driver.TargetConsumer.Current(rig.HeadAnchorId).Stamp;
            ulong previousRevision = initial.Revision;
            string direction = sourceLayout.Name + ">" + receiverLayout.Name;
            var result = new DirectionResult { Direction = direction, JsonlPath = Path.Combine(outDir, "frames-" + sourceLayout.Name + "-to-" + receiverLayout.Name + ".jsonl") };
            using var jsonl = new StreamWriter(result.JsonlPath, false, new UTF8Encoding(false));
            jsonl.WriteLine("{\"v\":1,\"kind\":\"header\",\"dir\":\"" + direction + "\",\"step_s\":" + StepSeconds.ToString(CultureInfo.InvariantCulture) +
                ",\"delivery\":\"checked-target-events\",\"checked_incarnation\":" + initial.Incarnation +
                ",\"objects_src\":{" + string.Join(",", sourceLayout.Objects.Select(o => "\"" + o.Name + "\":{\"poly\":" + Poly(o.Footprint) + ",\"touch\":" + V(o.TouchPoint) + "}")) +
                "},\"objects_rcv\":{" + string.Join(",", receiverLayout.Objects.Select(o => "\"" + o.Name + "\":{\"poly\":" + Poly(o.Footprint) + ",\"touch\":" + V(o.TouchPoint) + "}")) +
                "},\"limits\":{\"max_linear_mps\":" + MaxMetersPerSecond.ToString(CultureInfo.InvariantCulture) +
                ",\"max_angular_dps\":" + MaxDegreesPerSecond.ToString(CultureInfo.InvariantCulture) + "}}");

            var scratch = (SbAnchorProposal[])null;
            var set = new ProposalSet();
            float lerp = StepSeconds / 0.5f;   // Runner.DeltaTime / LERP_TIME of SbAvatarBehaviourFusion.OnHandPositionsChanged
            // The tail lets the receiver settle after the last sample.
            int tail = 25;
            for (int i = 0; i < script.Count + tail; ++i)
            {
                SourceSample s = script[Mathf.Min(i, script.Count - 1)];
                // The user's body is the source avatar's transform: the manager reads it each frame and sends the actor's pose, as in the app. This
                // used to call UpdateActorPose as well, a second writer of the same actor: the manager also sent the (unmoved) entity transform,
                // so the source alternated between the user's pose and the spawn pose. A core that pursues the source's approach (4.11) counts
                // its approach from consecutive updates, and the alternation made the receiver pursue a source that was not walking.
                rig.SourceAvatar.transform.SetPositionAndRotation(s.Root, Quaternion.Euler(s.PitchDegrees, s.YawDegrees, 0f));

                // The hand stream: head-relative, in the head's axes, exactly MetaInputTrackingDelegate.UpdateHandPositionsToFusion.
                Quaternion hr = HeadRotation(s);
                Vector3 head = Head(s);
                Vector3 lLocal = Quaternion.Inverse(hr) * (s.LeftHand - head), rLocal = Quaternion.Inverse(hr) * (s.RightHand - head);
                rig.Retarget.SetHandTransform(lLocal, rLocal, Quaternion.identity, Quaternion.identity, lerp);

                // One frame: the source emits (the core limits how often), the receiver drains, the driver steps and writes.
                System.Threading.Thread.Sleep(30);
                rig.Source.Update(); rig.Source.LateUpdate();
                rig.Receiver.Update(); rig.Receiver.LateUpdate(); rig.Receiver.Update();
                var observed = ReadCheckedSemanticSample(rig);
                Assert.That(observed.Incarnation, Is.EqualTo(initial.Incarnation), direction + " frame " + i);
                Assert.That(observed.Revision, Is.GreaterThanOrEqualTo(previousRevision), direction + " frame " + i);
                previousRevision = observed.Revision;

                var rec = new FrameRecord { Index = i, T = i * StepSeconds, Phase = i < script.Count ? s.Phase : "settle", Target = s.Target, Source = s };
                rec.RemoteHead = rig.Head.position;
                rec.RemoteHeadForward = rig.Head.rotation * Vector3.forward;
                rec.RemoteLeft = rig.Retarget.leftHandTransform.position;
                rec.RemoteRight = rig.Retarget.rightHandTransform.position;
                rec.CheckedRevision = observed.Revision;
                rec.AcceptedRoot = rig.Driver.TargetConsumer.Current(0).Stamp;
                rec.AcceptedHead = rig.Driver.TargetConsumer.Current(rig.HeadAnchorId).Stamp;
                if (SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, set) && set.HasRoot)
                {
                    rec.HasProposal = true; rec.Root = set.Root;
                    rec.ProposalPosition = set.Root.Position; rec.ProposalYaw = set.Root.YawDegrees;
                    rec.ProposalPositionUnspecified = set.Root.PositionUnspecified;
                }
                result.Frames.Add(rec);

                SemanticObject so = sourceLayout.Objects.FirstOrDefault(o => o.Name == rec.Target);
                SemanticObject ro = receiverLayout.Objects.FirstOrDefault(o => o.Name == rec.Target);
                jsonl.WriteLine("{\"v\":1,\"kind\":\"frame\",\"dir\":\"" + direction + "\",\"i\":" + i + ",\"t\":" + rec.T.ToString("0.###", CultureInfo.InvariantCulture) +
                    ",\"phase\":\"" + rec.Phase + "\",\"target\":\"" + rec.Target + "\"" +
                    ",\"checked\":{\"revision\":" + rec.CheckedRevision +
                        ",\"root_epoch\":" + rec.AcceptedRoot.Epoch + ",\"root_sequence\":" + rec.AcceptedRoot.Sequence +
                        ",\"head_epoch\":" + rec.AcceptedHead.Epoch + ",\"head_sequence\":" + rec.AcceptedHead.Sequence + "}" +
                    ",\"src\":{\"root\":" + V(s.Root) + ",\"head\":" + V(Head(s)) + ",\"head_fwd\":" + V(hr * Vector3.forward) + ",\"lh\":" + V(s.LeftHand) + ",\"rh\":" + V(s.RightHand) + "}" +
                    ",\"rcv\":{\"head\":" + V(rec.RemoteHead) + ",\"head_fwd\":" + V(rec.RemoteHeadForward) + ",\"lh\":" + V(rec.RemoteLeft) + ",\"rh\":" + V(rec.RemoteRight) + "}" +
                    (rec.HasProposal ? ",\"proposal\":{\"pos\":" + V(rec.ProposalPosition) + ",\"yaw\":" + rec.ProposalYaw.ToString("0.##", CultureInfo.InvariantCulture) +
                        ",\"pos_unspecified\":" + (rec.ProposalPositionUnspecified ? "true" : "false") +
                        ",\"cause\":\"" + rec.Root.Cause + "\",\"status\":\"" + rec.Root.Status + "\",\"referent\":" + rec.Root.ReferentSharedId + "}" : "") + "}");
                if (so != null && ro == null) Assert.Fail("counterpart missing");
            }
            Assert.That(previousRevision, Is.GreaterThan(initial.Revision), direction + ": no checked motion was committed.");
            Assert.That(LaterSemanticStamp(result.Frames.Last().AcceptedRoot, firstRoot), Is.True,
                direction + ": native root publication history did not advance.");
            Assert.That(LaterSemanticStamp(result.Frames.Last().AcceptedHead, firstHead), Is.True,
                direction + ": native direct-head publication history did not advance.");
            Assert.That(result.Frames.Any(f => f.HasProposal), Is.True, direction + ": no native proposal reached the measurement lane.");
            return result;
        }

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        [Test]
        public void AvatarSemanticTransferBetweenDifferentRoomLayoutsIsMeasuredInBothDirections()
        {
            string outDir = Environment.GetEnvironmentVariable("SPATIALBLEND_AVATAR_METRICS_DIR");
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "avatar-semantic-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
            Directory.CreateDirectory(outDir);
            var summary = new StringBuilder();
            using (new MainThreadLease())
            {
                // The first run is the control: the same layout on both sides, where the core promises identical-copy exactness.
                var control = LayoutA(); control.Name = "A2";
                foreach ((SemanticLayout src, SemanticLayout rcv) in new[]
                {
                    (LayoutA(), control), (LayoutA(), LayoutB()), (LayoutB(), LayoutA()),
                    // Falsifiers: the same two rooms with fewer shared referents.
                    (Only(LayoutB(), "whiteboard"), Only(LayoutA(), "whiteboard")),
                    (Only(LayoutB(), "table", "whiteboard"), Only(LayoutA(), "table", "whiteboard")),
                    (Only(LayoutA(), "whiteboard"), Only(LayoutB(), "whiteboard")),
                    (Only(LayoutB(), "table"), Only(LayoutA(), "table")),
                    (Only(LayoutA(), "table"), Only(LayoutB(), "table")),
                })
                {
                    DirectionResult r = RunDirection(src, rcv, outDir);
                    summary.AppendLine(Summarize(r, src, rcv));
                    AssertDriverBounds(r);
                    if (rcv.Name == "A2")
                    {
                        // The control: identical rooms. The core promises identical-copy exactness, so every touch and look transfers.
                        foreach (string name in new[] { "table", "whiteboard", "chess" })
                        {
                            var m = SteadyTouch(r, src, rcv, name);
                            Assert.That(m.hand, Is.LessThanOrEqualTo(ExactReachToleranceMeters), "control reach " + name);
                            Assert.That(m.bodyReceiver, Is.EqualTo(m.bodySource).Within(PositionToleranceMeters), "control standoff " + name);
                            Assert.That(m.gazePlan, Is.LessThanOrEqualTo(YawToleranceDegrees), "control gaze " + name);
                        }
                    }
                    // Teardown between directions: both managers of the first run.
                    foreach (GameObject item in _objects.ToArray())
                        if (item != null && item.TryGetComponent(out SpatialBlendManager manager)) manager.Disconnect();
                }
            }
            File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
            TestContext.Out.WriteLine("AVATAR_SEMANTIC_OUT=" + outDir);
            TestContext.Out.WriteLine(summary.ToString());
        }

        // ---- the authored object: a shared Chess placed at a different pose in each room --------------------------------
        //
        // The Freeform scene authors its shared objects as regions that carry the object (SbSpatialAuthoring). Inside such a
        // region the core maps the avatar's OBJECT-RELATIVE pose (the transfer leaf), so the reach and
        // the gaze at the object are expected to be reproduced at the counterpart. Outside it the scene-level Spacing and Facing
        // relations apply (the test above). The path crosses the region edge on the way in and on the way out.

        // The two rooms, each with the Chess authored at its own pose (a region that carries the object), a floor and one more referent,
        // exactly as CreateAvatarRig builds them, and the avatar the Freeform app shares: root and head anchors, Spacing and Facing bound.
        // sourceReceivedItsCopy: the avatar's own room is the one that RECEIVED the Chess from the other (the joiner of a real room), instead
        // of the one that authored and shared it. The avatar's source is a participant of either kind in a real room.
        private SemanticRig BuildAuthoredRig(long sharedId, Matrix4x4 sourceWorld, Matrix4x4 receiverWorld, bool sourceReceivedItsCopy = false)
        {
            var source = CreateManager("auth-source-" + sharedId);
            var receiver = CreateManager("auth-receiver-" + sharedId);
            int port = FreePort();
            receiver.Host(port); AssertConnected(receiver);
            source.Connect(LoopbackHost, port); AssertConnected(source);
            receiver.TestOnlyDriverSeconds = StepSeconds;
            InvokePrivate(source, "EnsureRoomElement"); InvokePrivate(receiver, "EnsureRoomElement");
            GiveTheRoomAFloor(source); GiveTheRoomAFloor(receiver);
            foreach (SpatialBlendManager side in new[] { source, receiver }) AddBoardReferent(side);
            SpatialAuthoringPlan plan = CompileChessPlan();
            if (!sourceReceivedItsCopy)
            {
                Assert.That(source.TryApplySpatialAuthoring(plan, sharedId, 0, sourceWorld, null, out SpatialAuthoringBinding binding), Is.True);
                GiveTheReceiverTheObject(source, receiver, plan, sharedId, binding, receiverWorld);
            }
            else
            {
                Assert.That(receiver.TryApplySpatialAuthoring(plan, sharedId, 0, receiverWorld, null, out SpatialAuthoringBinding binding), Is.True);
                GiveTheReceiverTheObject(receiver, source, plan, sharedId, binding, sourceWorld);
            }

            var sourceApp = InactiveObject("auth-source-app").AddComponent<SemanticAvatarApp>();
            var receiverApp = InactiveObject("auth-receiver-app").AddComponent<SemanticAvatarApp>();
            var register = typeof(SpatialBlendManager).GetMethod("RegisterApp", BindingFlags.Instance | BindingFlags.NonPublic);
            register.Invoke(source, new object[] { sourceApp }); register.Invoke(receiver, new object[] { receiverApp });
            SemanticAvatarEntity avatar = CreateSemanticAvatar("auth-avatar", new Vector3(0f, 0f, 0f));
            var rig = new SemanticRig { Source = source, Receiver = receiver, SourceAvatar = avatar, ReceiverApp = receiverApp };
            source.ShareGameObject(avatar.gameObject, true);
            Assert.That(WaitSemantic(rig, () => receiverApp.Added.Count == 1), Is.True, "The receiver never reconstructed the shared avatar.");
            RequireCheckedSemanticOwner(rig);
            Api("Bind", source.ID, rig.SourceActor, "root", EnumValue("SbKernelType", 0));
            Api("Bind", source.ID, rig.SourceActor, "head", EnumValue("SbKernelType", 1));
            return rig;
        }

        private static readonly Vector3 ChessTouchLocal = new Vector3(0.1f, 0.508f, 0f);

        private static SemanticLayout AuthoredChessLayout(string name, Matrix4x4 world) => new SemanticLayout
        {
            Name = name, FloorSize = Vector2.zero,
            Objects = new[]
            {
                new SemanticObject
                {
                    Name = "chess", SharedId = 0, TouchOverride = world.MultiplyPoint3x4(ChessTouchLocal),
                    Footprint = new[] { new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.25f, 0f, -0.25f), new Vector3(0.25f, 0f, 0.25f), new Vector3(-0.25f, 0f, 0.25f) }
                        .Select(q => { Vector3 w = world.MultiplyPoint3x4(q); return new Vector2(w.x, w.z); }).ToArray()
                }
            }
        };

        // The user's motion in object-local coordinates, mapped to the source room's world: walk in, reach, look away, look back, walk out
        // (still looking at the object: it backs out of the region).
        private static List<SourceSample> AuthoredScript(Matrix4x4 sourceWorld)
        {
            var list = new List<SourceSample>();
            float baseYaw = sourceWorld.rotation.eulerAngles.y;
            Vector3 stand = new Vector3(0f, 0f, -0.7f), outside = new Vector3(0f, 0f, -4f);
            Vector3 restOffset(float side, float yawLocal) => Quaternion.Euler(0f, yawLocal, 0f) * new Vector3(side * 0.25f, HeadHeight - 0.55f, 0.25f);
            void Emit(string phase, Vector3 local, float yawLocal, float pitch, Vector3? handLocal)
            {
                Vector3 root = sourceWorld.MultiplyPoint3x4(local);
                float yaw = baseYaw + yawLocal;
                var s = new SourceSample { Phase = phase, Target = phase.StartsWith("walk") || phase == "stand" || phase == "look_away_hold" || phase == "look_away" ? "none" : "chess",
                    Root = root, YawDegrees = yaw, PitchDegrees = pitch };
                s.LeftHand = root + Quaternion.Euler(0f, yaw, 0f) * new Vector3(-0.25f, HeadHeight - 0.55f, 0.25f);
                s.RightHand = handLocal.HasValue ? sourceWorld.MultiplyPoint3x4(handLocal.Value) : root + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0.25f, HeadHeight - 0.55f, 0.25f);
                list.Add(s);
            }
            int n = Mathf.CeilToInt(3.3f / (WalkMetersPerSecond * StepSeconds));
            for (int i = 1; i <= n; ++i) Emit("walk_in", Vector3.Lerp(outside, stand, (float)i / n), 0f, 0f, null);
            float reachPitch = PitchToward(stand + Vector3.up * HeadHeight, ChessTouchLocal);
            Vector3 rest = stand + restOffset(1f, 0f);
            for (float t = 0f; t < 1f; t += StepSeconds) Emit("stand", stand, 0f, 0f, null);
            for (int i = 1; i <= 8; ++i) Emit("reach", stand, 0f, reachPitch, Vector3.Lerp(rest, ChessTouchLocal, i / 8f));
            for (float t = 0f; t < 3f; t += StepSeconds) Emit("touch", stand, 0f, reachPitch, ChessTouchLocal);
            for (int i = 7; i >= 0; --i) Emit("retract", stand, 0f, reachPitch, Vector3.Lerp(rest, ChessTouchLocal, i / 8f));
            for (int i = 1; i <= 10; ++i) Emit("look_away", stand, 70f * i / 10f, 0f, null);
            for (float t = 0f; t < 3f; t += StepSeconds) Emit("look_away_hold", stand, 70f, 0f, null);
            for (int i = 1; i <= 10; ++i) Emit("look_back", stand, 70f * (1f - i / 10f), reachPitch, null);
            for (float t = 0f; t < 3f; t += StepSeconds) Emit("look_back_hold", stand, 0f, reachPitch, null);
            for (int i = 1; i <= n; ++i) Emit("walk_out", Vector3.Lerp(stand, outside, (float)i / n), 0f, 0f, null);
            return list;
        }

        [Test]
        public void AvatarReachAndGazeAtAnAuthoredObjectArePreservedAcrossTwoRoomPosesInBothDirections()
        {
            string outDir = Environment.GetEnvironmentVariable("SPATIALBLEND_AVATAR_METRICS_DIR");
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "avatar-semantic-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
            Directory.CreateDirectory(outDir);
            Matrix4x4 roomA = Matrix4x4.TRS(new Vector3(4.5f, 0f, 1.5f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 roomB = Matrix4x4.TRS(new Vector3(-2.5f, 0f, 6f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);
            var summary = new StringBuilder();
            using (new MainThreadLease())
            {
                foreach ((string name, long id, Matrix4x4 src, Matrix4x4 rcv, bool received) in new[]
                {
                    ("chess-A", 9801L, roomA, roomB, false), ("chess-B", 9802L, roomB, roomA, false),
                    // The reverse of the above: the avatar's own room received the Chess from the other (the joiner of a real room).
                    ("chess-A-recv", 9803L, roomA, roomB, true), ("chess-B-recv", 9804L, roomB, roomA, true),
                })
                {
                    SemanticLayout s = AuthoredChessLayout(name, src), r = AuthoredChessLayout(name + "-observer", rcv);
                    DirectionResult result = RunScript(BuildAuthoredRig(id, src, rcv, received), AuthoredScript(src), s, r, outDir);
                    summary.AppendLine(Summarize(result, s, r));
                    AssertDriverBounds(result);
                    // Inside the region the object-relative pose transfers: the reach, the stand-off and the
                    // gaze at the Chess are reproduced at the counterpart in the other room, whatever its pose.
                    var m = SteadyTouch(result, s, r, "chess");
                    Assert.That(m.hand, Is.LessThanOrEqualTo(ExactReachToleranceMeters), name + " reach");
                    Assert.That(m.bodyReceiver, Is.EqualTo(m.bodySource).Within(PositionToleranceMeters), name + " stand-off");
                    Assert.That(m.gazePlan, Is.LessThanOrEqualTo(YawToleranceDegrees), name + " gaze");
                    foreach (GameObject item in _objects.ToArray())
                        if (item != null && item.TryGetComponent(out SpatialBlendManager manager)) manager.Disconnect();
                }
            }
            File.AppendAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
            TestContext.Out.WriteLine(summary.ToString());
        }

        // The driver's own bounds (EmbodiedDriverConfig: 2 m/s, 150 deg/s) on every realised frame step: never a teleport after the join
        // placement (frame 0), never a turn faster than the cap. A solver jump is reported by the analysis, not hidden: the body walks
        // there at the cap.
        private static void AssertDriverBounds(DirectionResult r)
        {
            float maxStep = MaxMetersPerSecond * StepSeconds + 1e-3f, maxTurn = MaxDegreesPerSecond * StepSeconds + 0.1f;
            for (int i = 2; i < r.Frames.Count; ++i)   // frame 0 -> 1 is the join placement (SB_EXCEPTION_JOIN, a teleport by design)
            {
                Assert.That(FlatDistance(r.Frames[i].RemoteHead, r.Frames[i - 1].RemoteHead), Is.LessThanOrEqualTo(maxStep), r.Direction + " frame " + i);
                float y0 = Mathf.Atan2(r.Frames[i - 1].RemoteHeadForward.x, r.Frames[i - 1].RemoteHeadForward.z) * Mathf.Rad2Deg;
                float y1 = Mathf.Atan2(r.Frames[i].RemoteHeadForward.x, r.Frames[i].RemoteHeadForward.z) * Mathf.Rad2Deg;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(y0, y1)), Is.LessThanOrEqualTo(maxTurn), r.Direction + " frame " + i);
            }
        }

        // The steady state of one touch: the last frame of the hold. Hand error is the distance of the receiver's hand to the
        // counterpart's touch point; body error is how far the receiver stands from the counterpart's footprint compared to the source.
        private static (float hand, float bodySource, float bodyReceiver, float gazePlan) SteadyTouch(DirectionResult r, SemanticLayout src, SemanticLayout rcv, string name)
        {
            SemanticObject so = src.Objects.First(o => o.Name == name), ro = rcv.Objects.First(o => o.Name == name);
            FrameRecord f = r.Frames.Last(x => x.Phase == "touch" && x.Target == name);
            return (Vector3.Distance(f.RemoteRight, ro.TouchPoint),
                DistancePointPolygon(new Vector2(f.Source.Root.x, f.Source.Root.z), so.Footprint),
                DistancePointPolygon(new Vector2(f.RemoteHead.x, f.RemoteHead.z), ro.Footprint),
                GazeAnglePlan(f.RemoteHead, f.RemoteHeadForward, ro.Footprint));
        }

        // What the driver can do at its best: it lands inside POSITION_SNAP_THRESHOLD = 0.01 m of the proposal, and the hand is then
        // placed in the head's frame, so a head yaw error (within YAW_SNAP_THRESHOLD = 0.1 deg) times the 0.7 m reach adds about 1 mm. The
        // control run measures 0.02 m; 0.03 m is the bound used for exactness checks (identical copies, object-relative transfer).
        private const float ExactReachToleranceMeters = 0.03f;

        private string Summarize(DirectionResult r, SemanticLayout src, SemanticLayout rcv)
        {
            var sb = new StringBuilder();
            sb.AppendLine("== direction " + r.Direction + " frames=" + r.Frames.Count);
            // 1. Driver bounds on the realised body.
            float maxStep = 0f, maxTurn = 0f;
            for (int i = 1; i < r.Frames.Count; ++i)
            {
                maxStep = Mathf.Max(maxStep, FlatDistance(r.Frames[i].RemoteHead, r.Frames[i - 1].RemoteHead));
                float y0 = Mathf.Atan2(r.Frames[i - 1].RemoteHeadForward.x, r.Frames[i - 1].RemoteHeadForward.z) * Mathf.Rad2Deg;
                float y1 = Mathf.Atan2(r.Frames[i].RemoteHeadForward.x, r.Frames[i].RemoteHeadForward.z) * Mathf.Rad2Deg;
                maxTurn = Mathf.Max(maxTurn, Mathf.Abs(Mathf.DeltaAngle(y0, y1)));
            }
            sb.AppendLine($"driver: max body step/frame {Num(maxStep)} m (bound {Num(MaxMetersPerSecond * StepSeconds)}), max yaw step/frame {Num(maxTurn)} deg (bound {Num(MaxDegreesPerSecond * StepSeconds)})");
            // 2. Semantic: reach and gaze against the counterpart.
            foreach (string name in new[] { "table", "whiteboard", "chess" })
            {
                SemanticObject so = src.Objects.FirstOrDefault(o => o.Name == name), ro = rcv.Objects.FirstOrDefault(o => o.Name == name);
                if (so == null || ro == null) continue;
                FrameRecord touch = r.Frames.LastOrDefault(f => f.Phase == "touch" && f.Target == name);
                if (touch != null)
                {
                    float srcHand = Vector3.Distance(touch.Source.RightHand, so.TouchPoint);
                    float rcvHand = Vector3.Distance(touch.RemoteRight, ro.TouchPoint);
                    float srcBody = DistancePointPolygon(new Vector2(touch.Source.Root.x, touch.Source.Root.z), so.Footprint);
                    float rcvBody = DistancePointPolygon(new Vector2(touch.RemoteHead.x, touch.RemoteHead.z), ro.Footprint);
                    float gaze = GazeAnglePlan(touch.RemoteHead, touch.RemoteHeadForward, ro.Footprint);
                    Vector3 toTarget = ro.TouchPoint - touch.RemoteHead;
                    float gaze3 = Vector3.Angle(touch.RemoteHeadForward, toTarget);
                    sb.AppendLine($"touch {name}: source hand-to-object {Num(srcHand)} m; receiver hand-to-counterpart {Num(rcvHand)} m; " +
                        $"body-to-footprint source {Num(srcBody)} m vs receiver {Num(rcvBody)} m; receiver gaze error plan {Num(gaze)} deg, 3D {Num(gaze3)} deg");
                }
                FrameRecord look = r.Frames.LastOrDefault(f => f.Phase == "look_" + name);
                if (look != null)
                {
                    sb.AppendLine($"look {name}: receiver gaze error plan {Num(GazeAnglePlan(look.RemoteHead, look.RemoteHeadForward, ro.Footprint))} deg, " +
                        $"3D {Num(Vector3.Angle(look.RemoteHeadForward, ro.TouchPoint - look.RemoteHead))} deg");
                }
            }
            return sb.ToString();
        }
    }
}
#endif
