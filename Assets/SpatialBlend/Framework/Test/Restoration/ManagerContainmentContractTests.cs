// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T3.8, ABI 4.3: the manager side of containment transfer. A fake native layer stands in for the core's
    /// proposals, so these run without a library: the interaction value (the TRANSFER flag AND the root's ARRIVED, read from
    /// the proposal set every frame) that raises Start and Terminate and that ZoneBased reads (ABI 4.3 has no contained event
    /// and no state to read), the authoring that writes the region element-local and declares it a transfer leaf, the remote
    /// driver frame (publish, read, poll, follow) with its source exceptions and teleport requests, and the Facing rule.
    /// The native behaviour is the G-N cases.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ManagerContainmentContractTests
    {
        private const long ChessObject = 9100;
        private static readonly long ChessRegion = SpatialAuthoringCompiler.RegionSharedId(ChessObject);
        private const long Actor = 99L;

        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
            {
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            }
        }

        // ------------------------------------------------------------------ lifecycle

        [Test]
        public void HostNegotiatesTheFeatureBeforeConnectedAndDisconnectRetiresBeforeDestroy()
        {
            ContainmentManager manager = NewManager();
            manager.ConnectionStateChanged += state => manager.Order.Add(state.ToString());
            manager.Host(1);

            Assert.That(manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
            Assert.That(manager.Order.IndexOf("containment"), Is.GreaterThan(manager.Order.IndexOf("Connecting")));
            Assert.That(manager.Order.IndexOf("containment"), Is.LessThan(manager.Order.IndexOf("Connected")));
            Assert.That(manager.ContainmentTransferAvailable, Is.True);

            manager.Disconnect();
            Assert.That(manager.Order.IndexOf("retire"), Is.LessThan(manager.Order.IndexOf("release")));
            Assert.That(manager.Order.IndexOf("release"), Is.LessThan(manager.Order.IndexOf("destroy")));
            Assert.That(manager.Order.IndexOf("destroy"), Is.LessThan(manager.Order.LastIndexOf("Disconnected")));
            Assert.That(manager.ContainmentTransferAvailable, Is.False, "A new session negotiates again.");
        }

        [Test]
        public void ALibraryWithoutTheFeatureDeclaresNoLeafAndNoAvatarIsEverInteracting()
        {
            ContainmentManager manager = NewManager();
            manager.FeatureAvailable = false;
            manager.Host(1);

            Assert.That(manager.ContainmentTransferAvailable, Is.False);
            // The region keeps the legacy polygon and is not declared a leaf.
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null,
                out SpatialAuthoringBinding binding), Is.True);
            Assert.That(manager.LocalBoundaries, Is.Empty);
            Assert.That(manager.Flags, Is.Empty);
            Assert.That(manager.Boundaries.ContainsKey(binding.RegionElement), Is.True);
            Assert.That(manager.ContainmentRegionCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ interaction value

        // The value is the TRANSFER flag of the root proposal
        // AND that anchor's ARRIVED, from the proposal set the driver reads this frame. Start and Terminate are its
        // frame-to-frame change.
        [Test]
        public void TheInteractionValueIsTheTransferFlagAndTheRootsArrivedAndStartTerminateAreItsChange()
        {
            CheckedRig rig = NewCheckedRig();
            var events = new List<string>();
            rig.Entity.SetOnInteractionCallback((type, referent, proximity, _) =>
            {
                Assert.That(proximity, Is.EqualTo(ProximityType.Euclidean2D));
                events.Add(type + ":" + referent);
            });

            // No counterpart on this peer: the core leaves the flag clear. Arrived alone is an ordinary arrival.
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Arrived);
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False);
            Assert.That(events, Is.Empty);

            // The counterpart appears late: the flag is set on the next update, but the body is still walking. The
            // representation does not switch on the proposal.
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer, ChessRegion);
            rig.Frame();
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False, "The flag alone is the proposal, not the body.");
            Assert.That(events, Is.Empty, "No switch while the body is still walking.");

            // The body arrives: the display switches once, on the realised body.
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                ChessRegion);
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.True);
            Assert.That(events, Is.EqualTo(new[] { "Start:" + ChessRegion }));
            rig.Frame();
            rig.Frame();
            Assert.That(events.Count, Is.EqualTo(1), "Holding the value raises nothing.");

            // A hop to another leaf keeps TRANSFER and ARRIVED: the value did not change, so there is no signal.
            long board = SpatialAuthoringCompiler.RegionSharedId(9200);
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                board);
            rig.Frame();
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.True);

            // Leaving the leaf clears the flag (the body still counts as arrived): the display switches back.
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Arrived);
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False);
            Assert.That(events, Is.EqualTo(new[] { "Start:" + ChessRegion, "Terminate:" + ChessRegion }),
                "Terminate names the scope its Start did.");
            rig.Frame();
            Assert.That(events.Count, Is.EqualTo(2), "Leaving twice adds nothing.");
        }

        [Test]
        public void ABodyThatLeavesBeforeItArrivedNeverSwitched()
        {
            CheckedRig rig = NewCheckedRig();
            var events = new List<InteractionEventType>();
            rig.Entity.SetOnInteractionCallback((type, _, __, ___) => events.Add(type));

            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer, ChessRegion);
            rig.Frame();
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled);
            rig.Frame();

            Assert.That(events, Is.Empty);
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False);
        }

        [Test]
        public void ASetThatCannotBeReadChangesNothingAndAnArrivedRootWithoutTheFlagIsNotInteraction()
        {
            CheckedRig rig = NewCheckedRig();
            var events = new List<InteractionEventType>();
            rig.Entity.SetOnInteractionCallback((type, _, __, ___) => events.Add(type));
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                ChessRegion);
            rig.Frame();
            Assert.That(events, Is.EqualTo(new[] { InteractionEventType.Start }));

            // The core has no set to give this frame (no sample yet, say): the app learns nothing and keeps the last value.
            rig.Core.HasProposal = false;
            rig.Frame();
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.True);
        }

        [Test]
        public void TheRootsFlagIsReadNotAnotherAnchors()
        {
            CheckedRig rig = NewCheckedRig();
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Arrived);
            rig.Core.Others.Add(new AnchorProposal
            {
                AnchorId = 1, Status = EmbodiedStatus.Exact,
                Flags = AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
            });
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False, "TRANSFER is a root-anchor flag.");
        }

        [Test]
        public void AnAvatarWithNoDriverAndALocalAvatarAreNeverTransferInteracting()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SbEntity entity = BindAvatar(manager, new List<InteractionEventType>());
            Assert.That(manager.IsTransferInteracting(entity), Is.False, "No driver, no proposal set: the core says nothing.");
            Assert.That(manager.IsTransferInteracting(null), Is.False);
        }

        // The local avatar's value is the source-side transfer fact the core computed
        // on its latest update. It drives the local animation enable and the state-filter bypass (Start / Terminate).
        [Test]
        public void TheLocalAvatarsValueIsTheCoresSourceSideTransferFactAndStartTerminateAreItsChange()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var events = new List<string>();
            var avatar = new GameObject("containment-local");
            avatar.SetActive(false);
            _objects.Add(avatar);
            var entity = avatar.AddComponent<SbNativeEntity>();
            entity.AllowManipulation = false;
            entity.SetOnInteractionCallback((type, referent, proximity, _) => events.Add(type + ":" + referent));
            Map(manager, entity, isSource: true);

            manager.SourceLeaf = 0;
            manager.LateUpdate();
            Assert.That(manager.IsTransferInteracting(entity), Is.False);
            Assert.That(events, Is.Empty);

            manager.SourceLeaf = ChessRegion;
            manager.LateUpdate();
            Assert.That(manager.IsTransferInteracting(entity), Is.True);
            Assert.That(events, Is.EqualTo(new[] { "Start:" + ChessRegion }));
            manager.LateUpdate();
            Assert.That(events.Count, Is.EqualTo(1), "Holding the fact raises nothing.");

            // A library that cannot answer leaves the value as it was.
            manager.SourceAnswers = false;
            manager.SourceLeaf = 0;
            manager.LateUpdate();
            Assert.That(manager.IsTransferInteracting(entity), Is.True);
            manager.SourceAnswers = true;

            manager.LateUpdate();
            Assert.That(manager.IsTransferInteracting(entity), Is.False);
            Assert.That(events, Is.EqualTo(new[] { "Start:" + ChessRegion, "Terminate:" + ChessRegion }));
        }

        [Test]
        public void ManagerHoldsNoRegionBindingNoHysteresisNoPhaseMapperAndNoContainedEvent()
        {
            string text = System.IO.File.ReadAllText(
                "Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.ContainmentTransfer.cs");
            foreach (string removed in new[]
            {
                "InteractionHysteresis", "EnterR", "ExitR", "SetRelationPhaseConfig", "InteractionPhaseMapper",
                "RegisterRegionInteraction", "BindRegionInteractions", "SbKernelType.AffordancePose",
                "BindScoped", "PollAffordancePoseRelations",
                // ABI 4 has no per-actor containment state: no read, no poll, no contained pose.
                "ReadActorContainment", "PollContainment", "ActorContainment", "Alpha",
                // ABI 4.3 has no contained event: the copies it fed and its handlers are gone.
                "ContainmentEvent", "_containedActors", "_containedUnmappedActors", "ApplyContainmentEvent",
                "HandleContainmentEvent", "TryRegisterContainmentCallback", "RegisterContainmentEvents",
                "RetireContainmentEvents", "TryGetContainmentInteraction", "IsMapped",
            })
            {
                Assert.That(text, Does.Not.Contain(removed), removed);
            }
            Assert.That(text, Does.Contain("ContainmentInteractionMapper"));
            Assert.That(System.IO.File.Exists("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.RelationEvents.cs"),
                Is.False);
            Assert.That(System.IO.File.Exists("Assets/SpatialBlend/Foundation/InteractionHysteresis.cs"), Is.False);
            string app = System.IO.File.ReadAllText(
                "Assets/SpatialBlend/Samples/Scripts/Freeform Collaboration/FreeformCollaborationApp.cs");
            Assert.That(app, Does.Not.Contain("RegisterRegionInteraction"));
            Assert.That(System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbApp.cs"),
                Does.Not.Contain("RegisterRegionInteraction"));
        }

        // ------------------------------------------------------------------ ZoneBased reads the value

        [Test]
        public void ZoneBasedForAReceivedAvatarReadsTheSameValueAsTheDefaultModeAndHasNoPureRegionFallback()
        {
            CheckedRig rig = NewCheckedRig();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False, "Before the first read the core has said nothing.");

            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                ChessRegion);
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.True,
                "ZoneBased switches on the read the realised body is in the leaf.");

            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Arrived);
            rig.Frame();
            Assert.That(rig.Manager.IsTransferInteracting(rig.Entity), Is.False);
        }

        [Test]
        public void ZoneBasedSwitchFollowsTheCoresValueInstantlyAndTheAppHoldsNoPointTest()
        {
            var zone = new DeclaredRegionZone.Switch();
            Assert.That(zone.Set(true), Is.True);
            Assert.That(zone.Set(true), Is.False, "No change, no event.");
            Assert.That(zone.Inside, Is.True);
            Assert.That(zone.Set(false), Is.True);
            Assert.That(zone.Inside, Is.False);

            string entity = System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs");
            Assert.That(entity, Does.Contain("IsTransferInteracting"));
            Assert.That(entity, Does.Not.Contain("TryGetContainmentInteraction"));
            // The core is the single judgment, for the local avatar too. There is no v1 point test.
            Assert.That(entity, Does.Not.Contain("SbSpatialAuthoring.ActiveRegions"));
            Assert.That(entity, Does.Not.Contain("_zoneSwitch.Update"));
            string zoneText = System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/DeclaredRegionZone.cs");
            foreach (string removed in new[] { "IsInsideAny", "Contains(", "Update(" })
            {
                Assert.That(zoneText, Does.Not.Contain(removed), removed);
            }
            int handler = entity.IndexOf("private void OnAvatarInteraction(", StringComparison.Ordinal);
            Assert.That(entity.IndexOf("if (IsZoneBasedMode)", handler, StringComparison.Ordinal),
                Is.LessThan(entity.IndexOf("SetInteractionState", handler, StringComparison.Ordinal)),
                "ZoneBased still ignores the core interaction event.");
        }

        // ------------------------------------------------------------------ authoring

        [Test]
        public void ChessRegionIsWrittenElementLocalAndFlagged()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringPlan plan = CompileChess();
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);

            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, world, null,
                out SpatialAuthoringBinding binding), Is.True);

            AssertRegionLocalAndFlagged(manager, plan, binding);
            Assert.That(manager.Boundaries, Is.Empty, "The world-reading polygon setter is never used.");
            Assert.That(manager.ContainmentRegionCount, Is.EqualTo(1));
            // The region carries the pose and the polygon stays in the object frame.
            Assert.That(manager.Transforms[binding.RegionElement], Is.EqualTo(world));
        }

        [Test]
        public void TheFootprintPolygonFallbackIsAlsoElementLocalAndNeverFlagged()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringPlan plan = CompileChess();
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);

            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, world, null,
                out SpatialAuthoringBinding binding), Is.True);

            SpatialElementMapping objectMapping = plan.Mappings.Single(m => m.kind == SpatialHierarchyNodeKind.Object);
            Assert.That(manager.LocalBoundaries.ContainsKey(binding.ObjectElement), Is.True);
            Assert.That(manager.LocalBoundaries[binding.ObjectElement], Is.EqualTo(objectMapping.polygon));
            Assert.That(manager.Flags.ContainsKey(binding.ObjectElement), Is.False, "Only the region is the scope.");
        }

        [Test]
        public void TheReceiverFlagsTheRegionTheSourceSharedAsWell()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringPlan plan = CompileChess();
            // The source's shared region and object arrive first.
            manager.Seed(ChessRegion, 41);
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(4f, 0f, -3f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);

            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 500, world, null,
                out SpatialAuthoringBinding binding), Is.True);

            Assert.That(binding.RegionElement, Is.EqualTo(41));
            AssertRegionLocalAndFlagged(manager, plan, binding);
        }

        [Test]
        public void LinkingALocalObjectWritesAndFlagsTheRegionAgainWithTheSourceIdentity()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringPlan plan = CompileChess();
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(2f, 0f, 1f), Quaternion.identity, Vector3.one);
            Assert.That(manager.TryApplySpatialAuthoring(plan, 7000, 0, world, null, out SpatialAuthoringBinding local),
                Is.True);
            manager.LocalBoundaries.Clear();
            manager.Flags.Clear();

            Assert.That(manager.TryLinkSpatialAuthoring(local.ObjectElement, ChessObject, world,
                out SpatialAuthoringBinding linked), Is.True);

            AssertRegionLocalAndFlagged(manager, plan, linked);
            Assert.That(manager.SharedIds[linked.RegionElement], Is.EqualTo(ChessRegion));
        }

        [Test]
        public void EveryAuthoredObjectIsFlaggedChessWhiteboardGlbAndTable()
        {
            var plans = new Dictionary<string, SpatialAuthoringPlan>
            {
                ["Chess"] = CompilePrefab("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab"),
                ["Whiteboard"] = CompilePrefab("Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab"),
            };

            // GLB content declares its footprint as a collider that the loader fits to each side's mesh.
            var content = new GameObject("glb");
            _objects.Add(content);
            var box = content.AddComponent<BoxCollider>();
            box.size = new Vector3(0.6f, 0.4f, 0.3f);
            var glb = content.AddComponent<SbSpatialAuthoring>();
            glb.footprint = box;
            glb.fallbackMarginMeters = 0.15f;
            plans["GLB"] = Compile(glb);

            // TABLE is an MRUK anchor with local +Z up, authored by the scene-anchor helper.
            var anchor = new GameObject("TABLE");
            _objects.Add(anchor);
            Assert.That(SceneAnchorSpatialAuthoring.Apply(anchor, new Vector3(0f, 0f, -0.35f),
                new Vector3(1.2f, 0.8f, 0.7f)), Is.Not.Null);
            plans["TABLE"] = Compile(anchor.GetComponent<SbSpatialAuthoring>());

            long objectShared = 8000;
            foreach (KeyValuePair<string, SpatialAuthoringPlan> entry in plans)
            {
                ContainmentManager manager = NewManager();
                manager.Host(1);
                Matrix4x4 world = Matrix4x4.TRS(new Vector3(3f, 0f, -1f), Quaternion.Euler(0f, 75f, 0f), Vector3.one);

                Assert.That(manager.TryApplySpatialAuthoring(entry.Value, ++objectShared, 0, world, null,
                    out SpatialAuthoringBinding binding), Is.True, entry.Key);

                AssertRegionLocalAndFlagged(manager, entry.Value, binding, entry.Key);
            }
        }

        [Test]
        public void ARefusedFlagLeavesTheRegionOutOfTheContainmentSet()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            manager.RefuseFlag = true;

            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null, out _),
                Is.True, "Authoring still succeeds without the flag.");

            Assert.That(manager.ContainmentRegionCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ region inside the walkable ring

        // When the host's core refuses a region (here ChessArea, ContainmentEscapesParent), the manager must not keep and share the
        // region element with no geometry and no transfer flag. A refused region is not kept, and the error names it.
        [Test]
        public void ARegionTheCoreRefusesIsNotKeptAndTheErrorNamesIt()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            manager.RefuseRegionPolygon = true;

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("region '.+' was not registered \\(containment: .*sticks out of its parent"));
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null,
                out SpatialAuthoringBinding binding), Is.False);

            Assert.That(binding, Is.Null);
            Assert.That(manager.Flags, Is.Empty);
            Assert.That(manager.ContainmentRegionCount, Is.EqualTo(0));
        }

        // The source's region, received, cannot be removed here. Refused, it is left inert at the identity, so the received
        // object (natively its child when the source shared the region first) still sits at the authoring frame's pose.
        [Test]
        public void ARefusedReceivedRegionLeavesTheObjectAtItsPose()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringPlan plan = CompileChess();
            manager.Seed(ChessRegion, 600);
            manager.RefuseRegionPolygon = true;
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(1.5f, 0.7f, -2f), Quaternion.Euler(0f, 40f, 0f), Vector3.one);
            Matrix4x4 frameWorld = world * plan.FrameToObject;

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("received region .* left inert"));
            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 500, world, null,
                out SpatialAuthoringBinding binding), Is.True);

            Assert.That(binding.RegionElement, Is.EqualTo(0));
            Assert.That(manager.Flags, Is.Empty);
            Matrix4x4 objectWorld = manager.Transforms[600] * manager.Transforms[500];
            for (int i = 0; i < 16; ++i)
                Assert.That(objectWorld[i], Is.EqualTo(frameWorld[i]).Within(1e-5f), "element " + i);
        }

        // A virtual object this peer places is moved at registration so its region lies inside the ring.
        [Test]
        public void AVirtualObjectIsMovedSoItsRegionLiesInsideTheWalkableRing()
        {
            ContainmentManager manager = NewManager();
            SpatialAuthoringPlan plan = CompileChess();
            var ring = new[] { new Vector2(-3f, -3f), new Vector2(3f, -3f), new Vector2(3f, 3f), new Vector2(-3f, 3f) };
            manager.Walkable = new WalkableRegion(ring, null);
            var go = new GameObject("Chess");
            _objects.Add(go);
            go.transform.position = new Vector3(2.9f, 0.7f, 0f);
            Assert.That(WalkableRegionFit.Contains(ring, WorldRegion(plan, go.transform.localToWorldMatrix)), Is.False);

            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan), Is.True);

            Assert.That(go.transform.position.y, Is.EqualTo(0.7f), "Only the ground position moves.");
            Assert.That(WalkableRegionFit.Contains(ring, WorldRegion(plan, go.transform.localToWorldMatrix)), Is.True);
        }

        // Each room places its own copy: the received copy is fitted into THIS room's ring before its
        // region is written, like a local share. The source's pose is not used.
        [Test]
        public void AReceivedCopyIsSnappedIntoThisRoomsRing()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var ring = new[] { new Vector2(-3f, -3f), new Vector2(3f, -3f), new Vector2(3f, 3f), new Vector2(-3f, 3f) };
            var walkable = new WalkableRegion(ring, null);
            manager.Walkable = walkable;
            typeof(SpatialBlendManager).GetField("_registeredWalkable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, walkable);
            manager.Seed(ChessRegion, 600);
            GameObject root = PrefabUtility.LoadPrefabContents("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            try
            {
                SbSpatialAuthoring authoring = root.GetComponentInChildren<SbSpatialAuthoring>(true);
                SbEntity entity = authoring.GetComponent<SbEntity>();
                Assert.That(entity, Is.Not.Null);
                SpatialAuthoringPlan plan = Compile(authoring);
                entity.transform.position = new Vector3(2.9f, 0.7f, 0f);
                Assert.That(WalkableRegionFit.Contains(ring, WorldRegion(plan, entity.transform.localToWorldMatrix)), Is.False);

                bool applied = (bool)typeof(SpatialBlendManager).GetMethod("TryApplyReceivedAuthoring",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, new object[] { entity, 500L, ChessObject });

                Assert.That(applied, Is.True);
                Vector2[] placed = WorldRegion(plan, entity.transform.localToWorldMatrix);
                Assert.That(WalkableRegionFit.Contains(ring, placed), Is.True);
                Assert.That(entity.transform.position.y, Is.EqualTo(0.7f));
                Vector2[] registered = ToWorld(manager.LocalBoundaries[600], manager.Transforms[600]);
                for (int i = 0; i < placed.Length; ++i)
                    Assert.That((registered[i] - placed[i]).magnitude, Is.LessThan(1e-4f), "The region is written where the copy now is.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // No peer syncs a shared virtual object's world pose: the network entity's sync carries its
        // id only, and the prefabs' networked state is object-relative (pieces in the board's frame, strokes in UV). So no
        // network update can move a copy this room placed.
        [Test]
        public void NoNetworkPathPlacesASharedVirtualObject()
        {
            string sync = System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkEntity.cs");
            int syncRemote = sync.IndexOf("SyncRemote(", StringComparison.Ordinal);
            Assert.That(syncRemote, Is.GreaterThan(0));
            string body = sync.Substring(syncRemote);
            Assert.That(body, Does.Not.Match(@"\.(position|rotation|localPosition|localRotation)\s*=(?!=)"));
            Assert.That(body, Does.Not.Contain("SetPositionAndRotation"));

            var allowed = new[] { "ChessBoardHandlerMotif.cs", "PointerDrawingMotif.cs", "WhiteboardManagerMotif.cs" };
            foreach (string prefab in new[]
                     {
                         "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab",
                         "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab",
                     })
            {
                string text = System.IO.File.ReadAllText(prefab);
                foreach (System.Text.RegularExpressions.Match list in System.Text.RegularExpressions.Regex.Matches(text,
                             @"NetworkedBehaviours:\n((?:  - \{fileID: -?\d+\}\n)*)"))
                {
                    foreach (System.Text.RegularExpressions.Match id in System.Text.RegularExpressions.Regex.Matches(
                                 list.Groups[1].Value, @"fileID: (-?\d+)"))
                    {
                        System.Text.RegularExpressions.Match script = System.Text.RegularExpressions.Regex.Match(text,
                            "--- !u!114 &" + id.Groups[1].Value + @"\n(?:.*\n)*?  m_Script: \{fileID: \d+, guid: ([0-9a-f]+)");
                        Assert.That(script.Success, Is.True, prefab + " " + id.Groups[1].Value);
                        string path = AssetDatabase.GUIDToAssetPath(script.Groups[1].Value);
                        Assert.That(allowed.Any(path.EndsWith), Is.True, prefab + " syncs " + path);
                    }
                }
            }
            string chess = System.IO.File.ReadAllText("Assets/SpatialBlend/Samples/Objects/Chessboard/Scripts/ChessBoardHandlerMotif.cs");
            Assert.That(chess, Does.Contain("ChessPiecePositions.Set(i, localPosition)"), "Pieces are synced in the board's frame.");
            string board = System.IO.File.ReadAllText("Assets/SpatialBlend/Samples/Objects/Whiteboard/Scripts/WhiteboardManagerMotif.cs");
            Assert.That(board, Does.Contain("RPC_DrawLine(Vector2 startUV, Vector2 endUV"), "Strokes are synced in UV.");
        }

        // A sibling under the room (an MRUK furniture box) is refused to overlap by the core, so the fit avoids it.
        [Test]
        public void AVirtualObjectsRegionIsMovedOffASiblingBox()
        {
            ContainmentManager manager = NewManager();
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            Vector2[] before = WorldRegion(plan, go.transform.localToWorldMatrix);
            var center = new Vector2((before.Min(p => p.x) + before.Max(p => p.x)) * 0.5f,
                (before.Min(p => p.y) + before.Max(p => p.y)) * 0.5f);
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            // A desk box reaching into the region from its right, with no hole carved for it.
            var box = new[]
            {
                center + new Vector2(0.2f, -0.3f), center + new Vector2(1.2f, -0.3f),
                center + new Vector2(1.2f, 0.3f), center + new Vector2(0.2f, 0.3f),
            };
            manager.SiblingFootprints.Add(box);

            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan), Is.True);

            Vector2[] placed = WorldRegion(plan, go.transform.localToWorldMatrix);
            foreach (Vector2 corner in box)
                Assert.That(WalkablePlacementSnap.Contains(placed, corner), Is.False, "box corner " + corner);
            foreach (Vector2 vertex in placed)
                Assert.That(WalkablePlacementSnap.Contains(box, vertex), Is.False, "region vertex " + vertex);
            Assert.That(WalkableRegionFit.Contains(ring, placed), Is.True);
        }

        // A local object on an observed anchor nests under it and is left alone; a received copy is never nested (the core
        // does not reparent a received replica), so the anchor is a sibling it is moved clear of.
        [Test]
        public void AReceivedCopyOnAnAnchorIsMovedClearOfIt()
        {
            ContainmentManager manager = NewManager();
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            Vector2[] before = WorldRegion(plan, go.transform.localToWorldMatrix);
            var center = new Vector2((before.Min(p => p.x) + before.Max(p => p.x)) * 0.5f,
                (before.Min(p => p.y) + before.Max(p => p.y)) * 0.5f);
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            }, null);
            var table = new[]
            {
                center + new Vector2(-0.3f, -0.3f), center + new Vector2(0.3f, -0.3f),
                center + new Vector2(0.3f, 0.3f), center + new Vector2(-0.3f, 0.3f),
            };
            manager.SiblingFootprints.Add(table);

            // Furniture is an obstacle for a floor object everywhere, a local share and a received copy alike
            // (Chess is never on a table): both are moved clear of it, by the one validity rule.
            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan),
                Is.True);
            Assert.That(go.transform.position, Is.Not.EqualTo(Vector3.zero), "A local object over the table is moved clear of it.");
            go.transform.position = Vector3.zero;

            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan),
                Is.True);
            Vector2[] placed = WorldRegion(plan, go.transform.localToWorldMatrix);
            Assert.That(go.transform.position, Is.Not.EqualTo(Vector3.zero));
            foreach (Vector2 corner in table)
                Assert.That(WalkablePlacementSnap.Contains(placed, corner), Is.False, "table corner " + corner);
            Assert.That(WalkableRegionFit.EdgesCross(placed, table), Is.False);
        }

        // The joiner case: a received copy is placed against the obstacles its share-time check uses (the
        // core tree in a session: here a linked TABLE's stand-off the room's MRUK furniture does not hold), so the authoring that
        // follows finds it valid and does not move it again.
        [Test]
        public void AReceivedCopyIsPlacedAgainstTheCoreTreeSoItsAuthoringDoesNotMoveIt()
        {
            ContainmentManager manager = NewManager();
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            // The linked TABLE's stand-off, in the core tree only (a sibling region), right where the copy arrives.
            var standOff = new[] { new Vector2(-0.9f, -0.9f), new Vector2(0.9f, -0.9f), new Vector2(0.9f, 0.9f), new Vector2(-0.9f, 0.9f) };
            manager.SiblingFootprints.Add(standOff);

            var appObject = new GameObject("received-copy-app");
            _objects.Add(appObject);
            var app = appObject.AddComponent<SpatialBlend.Samples.FreeformCollaborationApp>();
            app.UserViewProvider = () => (new Vector3(0f, 1.6f, -3f), Vector3.forward);
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
            typeof(SpatialBlend.Samples.FreeformCollaborationApp).GetField("_localRoom", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(app, RoomRegion.Build(ring, null, null));

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            _objects.Add(instance);
            SbEntity entity = instance.GetComponentInChildren<SbEntity>(true);
            Assert.That(entity, Is.InstanceOf<SbNetworkEntity>());
            GameObject copy = entity.gameObject;
            copy.transform.position = new Vector3(0f, 0.7f, 0f);

            app.PlaceReceivedCopy(copy);

            Vector3 placed = copy.transform.position;
            Assert.That(placed, Is.Not.EqualTo(new Vector3(0f, 0.7f, 0f)), "moved off the stand-off it arrived on");
            Assert.That(RoomFit.TryRegion(copy, out Vector2[] region), Is.True);
            Assert.That(WalkableRegionFit.EdgesCross(region, standOff) || standOff.Any(c => WalkablePlacementSnap.Contains(region, c)) ||
                        region.Any(c => WalkablePlacementSnap.Contains(standOff, c)), Is.False, "clear of the core tree's stand-off");

            SbSpatialAuthoring authoring = copy.GetComponentInChildren<SbSpatialAuthoring>(true);
            var args = new object[] { authoring, null };
            Assert.That((bool)typeof(SpatialBlendManager).GetMethod("TryPlanAuthoring", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args), Is.True);
            var plan = (SpatialAuthoringPlan)args[1];
            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(copy.transform, copy.transform, copy.name, plan), Is.True);
            Assert.That(Vector3.Distance(copy.transform.position, placed), Is.LessThan(1e-4f), "the authoring does not move it again");
        }

        // Nothing is refused; a region is pulled to the nearest valid spot however far it is.
        [Test]
        public void AFitPullsTheRegionToTheNearestValidSpotWithNoDistanceLimit()
        {
            ContainmentManager manager = NewManager();
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            Vector2[] region = WorldRegion(plan, go.transform.localToWorldMatrix);
            // The room's only floor big enough for the region starts 3 m to the right.
            float left = region.Max(p => p.x) + 3f;
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(left - 3.5f, -5f), new Vector2(left + 5f, -5f), new Vector2(left + 5f, 5f), new Vector2(left - 3.5f, 5f),
            }, null);
            var blocked = new[]
            {
                new Vector2(left - 3.5f, -5f), new Vector2(left, -5f), new Vector2(left, 5f), new Vector2(left - 3.5f, 5f),
            };
            manager.SiblingFootprints.Add(blocked);

            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan),
                Is.True);
            Vector2[] placed = WorldRegion(plan, go.transform.localToWorldMatrix);
            Assert.That(placed.Min(p => p.x), Is.GreaterThanOrEqualTo(left - 1e-3f), "pulled clear of the blocked floor");
            Assert.That(placed.Min(p => p.x), Is.LessThan(left + 0.1f), "to the nearest spot");
            Assert.That(WalkableRegionFit.Contains(manager.Walkable.Boundary, placed), Is.True);
        }

        // The fit moves the same transform manual placement moves: the SbEntity's own (PlacementMotif), on both paths.
        // On the Whiteboard prefab that is the "Whiteboard" child; its sibling "Pointer Drawing" only parents the hit
        // indicator, which is put at each ray hit in world space, so it holds no geometry the move must carry.
        [Test]
        public void TheFitMovesWhatManualPlacementMoves()
        {
            string placement = System.IO.File.ReadAllText("Assets/SpatialBlend/Interaction/Scripts/Placement/PlacementMotif.cs");
            Assert.That(placement, Does.Contain("_entityTransform = parentEntity != null ? parentEntity.transform : transform;"));
            string manager = System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            Assert.That(manager, Does.Contain("TryFitAuthoredObjectIntoWalkable(sbEntity.transform, sbEntity.transform,"));
            Assert.That(manager, Does.Contain("TryFitAuthoredObjectIntoWalkable(pose, entity.transform,"));
            string spatial = System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.SpatialHierarchy.cs");
            Assert.That(spatial, Does.Contain("Transform root = entity.transform;"));
            string pointer = System.IO.File.ReadAllText("Assets/SpatialBlend/Samples/Objects/Whiteboard/Scripts/PointerHandlerMotif.cs");
            Assert.That(pointer, Does.Contain("m_hitIndicator.transform.position = closestHit.point;"));

            GameObject root = PrefabUtility.LoadPrefabContents("Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            try
            {
                SbEntity entity = root.GetComponentInChildren<SbEntity>(true);
                Assert.That(entity.GetComponent<SbSpatialAuthoring>(), Is.Not.Null, "The region frame is the entity's.");
                Transform drawing = root.transform.Find("Pointer Drawing");
                Assert.That(drawing, Is.Not.Null);
                Assert.That(drawing.GetComponentsInChildren<Renderer>(true).Length, Is.EqualTo(0),
                    "Pointer Drawing carries no placed geometry.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // A shared scene anchor registers a region automatically (v1's SURROUND volume). The adapter-owned
        // anchor is this peer's own furniture, not a received object: its region is made here, and holds its box, which moves
        // under it before the region's polygon is checked (core: a region may leave the room's ring over its furniture).
        [Test]
        public void ASceneAnchorRegistersARegionThatHoldsItsBox()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var go = new GameObject("TABLE");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var anchor = go.AddComponent<SbMRUKAnchorEntity>();
            anchor.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            anchor.ApplySupport(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f));
            SpatialAuthoringPlan plan = Compile(go.GetComponent<SbSpatialAuthoring>());
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            }, null);
            manager.AnchorWorld = go.transform.localToWorldMatrix;
            // A neighbour 0.2 m past the table's +x end.
            manager.SiblingFootprints.Add(new[]
            {
                new Vector2(0.8f, -1f), new Vector2(1.5f, -1f), new Vector2(1.5f, 1f), new Vector2(0.8f, 1f),
            });

            Assert.That(manager.TryApplySpatialAuthoring(plan, 7700, 500, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding, new SpatialAuthoringExistingElements { SourceKey = 3 }), Is.True);

            long region = binding.RegionElement;
            Assert.That(region, Is.GreaterThan(0), "The anchor's region is made, not skipped as a received one.");
            Assert.That(binding.ObjectElement, Is.EqualTo(500L), "The adapter's element stays the object.");
            Assert.That(manager.Reparents, Is.EqualTo(new[] { (500L, region) }));
            Assert.That(manager.CallOrder.IndexOf("reparent:500>" + region),
                Is.LessThan(manager.CallOrder.IndexOf("local:" + region)), "The box is under the region before its polygon.");
            Vector2[] written = ToWorld(manager.LocalBoundaries[region], manager.Transforms[region]);
            Assert.That(written.Max(p => p.x), Is.LessThanOrEqualTo(0.8f - ManagerAnchorMargin + 1e-3f));
            Assert.That(written.Min(p => p.x), Is.LessThan(-0.6f - 0.4f), "The free side keeps the stand-off.");
        }

        private const float ManagerAnchorMargin = 0.005f;

        // A fit moves a region at most the maximum move, so obstacles farther than that are left out (a fit with 16 of them took 233 ms).
        [Test]
        public void ObstaclesBeyondReachAreLeftOutOfTheFit()
        {
            var region = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            var near = new[] { new Vector2(2.5f, 0f), new Vector2(3f, 0f), new Vector2(3f, 1f), new Vector2(2.5f, 1f) };
            var far = new[] { new Vector2(4.5f, 0f), new Vector2(5f, 0f), new Vector2(5f, 1f), new Vector2(4.5f, 1f) };
            var rings = new List<Vector2[]> { near, far };
            SpatialBlendManager.KeepNear(rings, region, 2f);
            Assert.That(rings, Is.EqualTo(new[] { near }));
        }

        [Test]
        public void AnAnchorRegionsReachCoversABoxTurnedFortyFiveDegrees()
        {
            // A 1 m box turned 45° (a diamond) and its region, the box grown by the stand-off in its own frame.
            float s = SceneAnchorSpatialAuthoring.StandOffMeters;
            Vector2[] Turned(float half) => new[]
            {
                new Vector2(half * 1.41421356f, 0f), new Vector2(0f, half * 1.41421356f),
                new Vector2(-half * 1.41421356f, 0f), new Vector2(0f, -half * 1.41421356f),
            };
            Vector2[] box = Turned(0.5f), region = Turned(0.5f + s);
            // A neighbour past the box's bounding box by more than the stand-off, yet under the region's corner.
            float x = box[0].x + s + 0.05f;
            var neighbour = new[] { new Vector2(x, -0.02f), new Vector2(x + 0.3f, -0.02f), new Vector2(x + 0.3f, 0.02f), new Vector2(x, 0.02f) };
            Assert.That(x, Is.LessThan(region[0].x), "the neighbour overlaps the region");
            var rings = new List<Vector2[]> { neighbour };
            SpatialBlendManager.KeepNear(rings, box, s + SpatialBlendManager.AnchorRegionMarginMeters);
            Assert.That(rings, Is.Empty, "the stand-off alone leaves it out");
            rings.Add(neighbour);
            SpatialBlendManager.KeepNear(rings, box, SpatialBlendManager.AnchorRegionReachMeters);
            Assert.That(rings, Is.EqualTo(new[] { neighbour }));
        }

        // An authored TABLE anchor whose box (element 500, scene-geometry key 3) is under its region.
        private SpatialAuthoringBinding AuthorTableAnchor(ContainmentManager manager, out GameObject go, out SpatialAuthoringPlan plan)
        {
            go = new GameObject("TABLE");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var anchor = go.AddComponent<SbMRUKAnchorEntity>();
            anchor.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            anchor.ApplySupport(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f));
            plan = Compile(go.GetComponent<SbSpatialAuthoring>());
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            }, null);
            manager.AnchorWorld = go.transform.localToWorldMatrix;
            Assert.That(manager.TryApplySpatialAuthoring(plan, 7700, 500, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding, new SpatialAuthoringExistingElements { SourceKey = 3 }), Is.True);
            Assert.That(binding.RegionElement, Is.GreaterThan(0));
            return binding;
        }

        // Retiring a shared anchor removes its region, which would take the box under it along: the box is parked first and
        // set down under the room afterwards, and the region's record goes.
        [Test]
        public void RetiringASceneAnchorTakesItsBoxOutOfTheRegionFirst()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringBinding binding = AuthorTableAnchor(manager, out _, out _);
            long region = binding.RegionElement;
            Assert.That(manager.AnchorRegionCount, Is.EqualTo(1));

            manager.RetireSceneAnchor(500, 3, region);

            Assert.That(manager.RemovedLocal, Is.EqualTo(new[] { region }));
            Assert.That(manager.Reparents.Count, Is.EqualTo(3));
            Assert.That(manager.Reparents[1].element, Is.EqualTo(500L));
            Assert.That(manager.Reparents[1].parent, Is.Not.EqualTo(region).And.Not.EqualTo(1L), "Parked first.");
            Assert.That(manager.Reparents[2], Is.EqualTo((500L, 1L)), "Then back under the room.");
            Assert.That(manager.AnchorRegionCount, Is.EqualTo(0));
        }

        // A region linked across spaces cannot be removed: it stays, inert (no transfer leaf), and keeps holding the box.
        [Test]
        public void ALinkedAnchorRegionStaysInertAndKeepsItsBox()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringBinding binding = AuthorTableAnchor(manager, out _, out _);
            long region = binding.RegionElement;
            manager.RefuseLocalRemoval = true;

            manager.RetireSceneAnchor(500, 3, region);

            Assert.That(manager.Reparents[manager.Reparents.Count - 1], Is.EqualTo((500L, region)), "Back under the region.");
            Assert.That(manager.Flags[region], Is.False, "No longer a transfer leaf.");
            Assert.That(manager.ContainmentRegionCount, Is.EqualTo(0));
        }

        // A shared anchor that holds a region does not follow a pose update (its box is shared, so the core keeps the old one,
        // and a new box beside it under the region would overlap it): the update is refused, the region stays as it is.
        [Test]
        public void ASharedAnchorWithARegionKeepsItsPoseOnAnUpdate()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            Assert.That(SceneGeometryDescriptor.TryCreate(3, Matrix4x4.Translate(new Vector3(0f, 0.75f, 0f)),
                new Bounds(Vector3.zero, new Vector3(1.2f, 0.6f, 0.75f)), null, null, out SceneGeometryDescriptor staged), Is.True);
            Assert.That(manager.UpsertLocalSceneGeometry(staged), Is.True);
            Assert.That(manager.TryGetLocalSceneGeometryElements(3, out long box, out _), Is.True);
            var go = new GameObject("TABLE");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var anchor = go.AddComponent<SbMRUKAnchorEntity>();
            anchor.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            anchor.ApplySupport(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f));
            SpatialAuthoringPlan plan = Compile(go.GetComponent<SbSpatialAuthoring>());
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            }, null);
            manager.AnchorWorld = go.transform.localToWorldMatrix;
            Assert.That(manager.TryApplySpatialAuthoring(plan, 7700, box, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding, new SpatialAuthoringExistingElements { SourceKey = 3 }), Is.True);
            Assert.That(binding.RegionElement, Is.GreaterThan(0));
            int reparents = manager.Reparents.Count;
            Assert.That(SceneGeometryDescriptor.TryCreate(3, Matrix4x4.Translate(new Vector3(0.3f, 0.75f, 0f)),
                new Bounds(Vector3.zero, new Vector3(1.2f, 0.6f, 0.75f)), null, null, out SceneGeometryDescriptor moved), Is.True);

            Assert.That(manager.UpsertLocalSceneGeometry(moved), Is.False);

            Assert.That(manager.TryGetLocalSceneGeometryElements(3, out long still, out _), Is.True);
            Assert.That(still, Is.EqualTo(box), "The shared box stays.");
            Assert.That(manager.Reparents.Count, Is.EqualTo(reparents));
            Assert.That(manager.AnchorRegionCount, Is.EqualTo(1));
        }

        // Furniture that arrives over a scene anchor's region: the anchor cannot move, so its stand-off is trimmed off it.
        [Test]
        public void ArrivingFurnitureTrimsAnAnchorRegion()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            SpatialAuthoringBinding binding = AuthorTableAnchor(manager, out _, out _);
            manager.SetRegisteredWalkable(manager.Walkable);
            long region = binding.RegionElement;
            Vector2[] before = ToWorld(manager.LocalBoundaries[region], manager.Transforms[region]);
            float right = before.Max(p => p.x);
            // A cabinet in the table's +x stand-off.
            Assert.That(SceneGeometryDescriptor.TryCreate(9, Matrix4x4.Translate(new Vector3(right - 0.1f + 0.3f, 0.5f, 0f)),
                new Bounds(Vector3.zero, new Vector3(0.6f, 0.6f, 0.6f)), null, null, out SceneGeometryDescriptor cabinet), Is.True);
            Assert.That(SceneGeometryFootprint.TryRing(cabinet, out Vector2[] footprint), Is.True);

            typeof(SpatialBlendManager).GetMethod("MakeRoomForSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { cabinet });

            Vector2[] after = ToWorld(manager.LocalBoundaries[region], manager.Transforms[region]);
            Assert.That(after.Max(p => p.x), Is.LessThan(right));
            Assert.That(WalkableRegionFit.EdgesCross(after, footprint), Is.False);
            foreach (Vector2 corner in footprint)
                Assert.That(WalkablePlacementSnap.Contains(after, corner), Is.False, "cabinet corner " + corner);
        }

        // An anchor with no floor beside it (or none clear of its neighbours) stays as it was: shared, with no region.
        [Test]
        public void ASceneAnchorWithNoFloorBesideItIsSharedWithoutARegion()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var go = new GameObject("TABLE");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var anchor = go.AddComponent<SbMRUKAnchorEntity>();
            anchor.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            anchor.ApplySupport(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f));
            SpatialAuthoringPlan plan = Compile(go.GetComponent<SbSpatialAuthoring>());
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(10f, 10f), new Vector2(20f, 10f), new Vector2(20f, 20f), new Vector2(10f, 20f),
            }, null);
            manager.AnchorWorld = go.transform.localToWorldMatrix;

            Assert.That(manager.TryApplySpatialAuthoring(plan, 7700, 500, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding, new SpatialAuthoringExistingElements { SourceKey = 3 }), Is.True);

            Assert.That(binding.RegionElement, Is.EqualTo(0));
            Assert.That(binding.ObjectElement, Is.EqualTo(500L));
            Assert.That(manager.Reparents, Is.Empty);
        }

        // A region the core refuses leaves the box where it was: it goes back under the room before the region is removed.
        [Test]
        public void ARefusedAnchorRegionPutsTheBoxBackUnderTheRoom()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var go = new GameObject("TABLE");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var anchor = go.AddComponent<SbMRUKAnchorEntity>();
            anchor.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            anchor.ApplySupport(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f));
            SpatialAuthoringPlan plan = Compile(go.GetComponent<SbSpatialAuthoring>());
            manager.Walkable = new WalkableRegion(new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            }, null);
            manager.AnchorWorld = go.transform.localToWorldMatrix;
            manager.RefuseRegionPolygon = true;

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("scene anchor's region was not registered"));
            Assert.That(manager.TryApplySpatialAuthoring(plan, 7700, 500, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding, new SpatialAuthoringExistingElements { SourceKey = 3 }), Is.True);

            Assert.That(binding.RegionElement, Is.EqualTo(0));
            Assert.That(manager.Reparents.Count, Is.EqualTo(2));
            Assert.That(manager.Reparents[1], Is.EqualTo((500L, 1L)), "Back under the room (element 1).");
        }

        [Test]
        public void ARegionJustOutsideTheRingIsMovedToTheMargin()
        {
            ContainmentManager manager = NewManager();
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            Vector2[] region = WorldRegion(plan, go.transform.localToWorldMatrix);
            float maxX = region.Max(p => p.x) - 0.0005f;
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(maxX, -5f), new Vector2(maxX, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            Assert.That(WalkableRegionFit.Contains(ring, region), Is.True, "The loose test accepts it.");

            Assert.That(manager.TryFitAuthoredObjectIntoWalkable(go.transform, go.transform, go.name, plan), Is.True);

            Assert.That(WorldRegion(plan, go.transform.localToWorldMatrix).Max(p => p.x),
                Is.LessThanOrEqualTo(maxX - SpatialBlendManager.RegionFitMarginMeters + 1e-4f));
        }

        // Only the inbound share whose own local counterpart waits for the ring is held; any other share goes on.
        [Test]
        public void OnlyTheInboundShareOfAParkedObjectWaits()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var go = new GameObject("Board");
            _objects.Add(go);
            go.AddComponent<RecordingEntity>();
            go.AddComponent<SbSpatialAuthoring>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("waits for the room's walkable"));
            manager.ShareGameObject(go, false);
            manager.Update();
            long parkedId = (long)typeof(SpatialBlendManager).GetMethod("ResolveSharedElementId",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { go });

            Assert.That(manager.InboundShareWaitsForParkedCounterpart(parkedId), Is.True);
            Assert.That(manager.InboundShareWaitsForParkedCounterpart(1000000000000L + 5), Is.False,
                "A network object's share (an avatar's too) goes on.");
            Assert.That(manager.InboundShareWaitsForParkedCounterpart(parkedId + 1), Is.True,
                "A per-instance id differs across peers, so such a share waits while such an object is parked.");

            UnityEngine.Object.DestroyImmediate(go);
            Assert.That(manager.InboundShareWaitsForParkedCounterpart(parkedId), Is.False, "A destroyed object no longer holds it.");
        }

        // On B the regions were fitted before the MRUK adapter staged B's furniture, and the core refused seven
        // furniture boxes against them. With the app's scene geometry expected, an authored share waits for it as well.
        [Test]
        public void AnAuthoredShareAlsoWaitsForTheSceneGeometry()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            manager.WaitsForLocalSceneGeometry = true;
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.SetRegisteredWalkable(new WalkableRegion(ring, null));
            var go = new GameObject("Board");
            _objects.Add(go);
            go.AddComponent<RecordingEntity>();
            go.AddComponent<SbSpatialAuthoring>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("waits for the room's walkable"));

            manager.ShareGameObject(go, false);
            manager.Update();

            Assert.That(manager.AuthoringReady, Is.False, "The walkable ring alone is not enough.");
            var parked = (IList)typeof(SpatialBlendManager).GetField("_sharesAwaitingWalkable",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Assert.That(parked.Count, Is.EqualTo(1));
            manager.MarkLocalSceneGeometryRegistered();
            Assert.That(manager.AuthoringReady, Is.True);
        }

        // Furniture staged after a region was fitted (a room scanned late, an anchor that moved): the virtual object moves clear
        // of the arriving box before it is staged, so the core admits the box.
        [Test]
        public void ArrivingFurnitureMovesAVirtualRegionOutOfItsWay()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            manager.SetRegisteredWalkable(manager.Walkable);
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            RecordingEntity entity = go.AddComponent<RecordingEntity>();
            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding), Is.True);
            manager.AuthoredEntities[binding.ObjectElement] = entity;
            Vector2[] before = WorldRegion(plan, go.transform.localToWorldMatrix);
            var center = new Vector2((before.Min(p => p.x) + before.Max(p => p.x)) * 0.5f,
                (before.Min(p => p.y) + before.Max(p => p.y)) * 0.5f);
            Assert.That(SceneGeometryDescriptor.TryCreate(9, Matrix4x4.Translate(new Vector3(center.x, 0.5f, center.y)),
                new Bounds(Vector3.zero, new Vector3(0.6f, 0.6f, 0.6f)), null, null, out SceneGeometryDescriptor box), Is.True);
            Assert.That(SceneGeometryFootprint.TryRing(box, out Vector2[] footprint), Is.True);

            typeof(SpatialBlendManager).GetMethod("MakeRoomForSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { box });

            Vector2[] placed = WorldRegion(plan, go.transform.localToWorldMatrix);
            Assert.That(go.transform.position, Is.Not.EqualTo(Vector3.zero), "The object moved.");
            Assert.That(manager.CheckedTransforms.Any(t => t.element == binding.RegionElement), Is.True, "Its region was rewritten.");
            foreach (Vector2 corner in footprint)
                Assert.That(WalkablePlacementSnap.Contains(placed, corner), Is.False, "box corner " + corner);
            Assert.That(WalkableRegionFit.EdgesCross(placed, footprint), Is.False);
        }

        // Furniture arriving while the user holds the object. The object is not moved under the hand, but the
        // pose the core holds for it (its pre-grab one) is refitted clear of the box, so the core does not refuse the furniture.
        [Test]
        public void ArrivingFurnitureRefitsTheCorePoseOfAHeldObjectButNotItsTransform()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            manager.SetRegisteredWalkable(manager.Walkable);
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            RecordingEntity entity = go.AddComponent<RecordingEntity>();
            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, go.transform.localToWorldMatrix, null,
                out SpatialAuthoringBinding binding), Is.True);
            manager.AuthoredEntities[binding.ObjectElement] = entity;
            Vector2[] before = WorldRegion(plan, go.transform.localToWorldMatrix);
            var center = new Vector2((before.Min(p => p.x) + before.Max(p => p.x)) * 0.5f,
                (before.Min(p => p.y) + before.Max(p => p.y)) * 0.5f);
            // The user holds it, carried 3 m away from where the core holds it.
            var motif = go.AddComponent<SpatialBlend.Interactions.ObjectPlacementMotif>();
            typeof(SpatialBlend.Interactions.InteractionMotif).GetField("_isGrabbed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(motif, true);
            Assert.That(motif.InMotion, Is.True);
            var held = new Vector3(3f, 0.9f, 3f);
            go.transform.position = held;
            Assert.That(SceneGeometryDescriptor.TryCreate(9, Matrix4x4.Translate(new Vector3(center.x, 0.5f, center.y)),
                new Bounds(Vector3.zero, new Vector3(0.6f, 0.6f, 0.6f)), null, null, out SceneGeometryDescriptor box), Is.True);
            Assert.That(SceneGeometryFootprint.TryRing(box, out Vector2[] footprint), Is.True);

            typeof(SpatialBlendManager).GetMethod("MakeRoomForSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { box });

            Assert.That(Vector3.Distance(go.transform.position, held), Is.LessThan(1e-5f), "not moved under the hand");
            Assert.That(manager.CheckedTransforms.Any(t => t.element == binding.RegionElement), Is.True, "its core pose was rewritten");
            Vector2[] core = ToWorld(manager.LocalBoundaries[binding.RegionElement], manager.Transforms[binding.RegionElement]);
            foreach (Vector2 corner in footprint)
                Assert.That(WalkablePlacementSnap.Contains(core, corner), Is.False, "box corner " + corner);
            Assert.That(WalkableRegionFit.EdgesCross(core, footprint), Is.False, "the core's region is clear of the arriving box");
        }

        // The held object's root is not its authoring frame (a child offset and turned from it), and the user
        // turned it during the grab. The core's pose is refitted from the pre-grab core pose, in the frame: a pure translation clear of
        // the box, never the hand's turn or the child offset applied in the wrong frame.
        [Test]
        public void TheHeldRefitComposesTheRootFromTheCoreFrameForAnOffsetChildFrame()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var ring = new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) };
            manager.Walkable = new WalkableRegion(ring, null);
            manager.SetRegisteredWalkable(manager.Walkable);
            SpatialAuthoringPlan plan = CompileChess();
            var go = new GameObject("Chess");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(-1f, 0f, 0.5f), Quaternion.Euler(0f, 15f, 0f));
            var child = new GameObject("frame").transform;
            child.SetParent(go.transform, false);
            child.localPosition = new Vector3(0.4f, 0f, 0.3f);
            child.localRotation = Quaternion.Euler(0f, 20f, 0f);
            RecordingEntity entity = go.AddComponent<RecordingEntity>();
            typeof(SbEntity).GetField("_entityTransform", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, child);
            Matrix4x4 frameWorld = child.localToWorldMatrix;
            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, frameWorld, null,
                out SpatialAuthoringBinding binding), Is.True, "authored");
            manager.AuthoredEntities[binding.ObjectElement] = entity;
            Matrix4x4 coreBefore = manager.Transforms[binding.RegionElement];
            Vector2[] before = WorldRegion(plan, frameWorld);
            var center = new Vector2((before.Min(p => p.x) + before.Max(p => p.x)) * 0.5f,
                (before.Min(p => p.y) + before.Max(p => p.y)) * 0.5f);
            // Held, carried away and turned 50 degrees by the user.
            var motif = go.AddComponent<SpatialBlend.Interactions.ObjectPlacementMotif>();
            typeof(SpatialBlend.Interactions.InteractionMotif).GetField("_isGrabbed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(motif, true);
            var heldP = new Vector3(3f, 0.9f, 3f);
            Quaternion heldR = Quaternion.Euler(0f, 65f, 0f);
            go.transform.SetPositionAndRotation(heldP, heldR);
            Assert.That(SceneGeometryDescriptor.TryCreate(9, Matrix4x4.TRS(new Vector3(center.x, 0.6f, center.y), Quaternion.Euler(-90f, 0f, 0f), Vector3.one),
                new Bounds(Vector3.zero, new Vector3(0.6f, 0.6f, 0.6f)), null, null, out SceneGeometryDescriptor box), Is.True, "box");
            Assert.That(SceneGeometryFootprint.TryRing(box, out Vector2[] footprint), Is.True, "ring");

            typeof(SpatialBlendManager).GetMethod("MakeRoomForSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { box });

            Assert.That(Vector3.Distance(go.transform.position, heldP), Is.LessThan(1e-5f), "not moved under the hand");
            Assert.That(Quaternion.Angle(go.transform.rotation, heldR), Is.LessThan(0.01f), "not turned under the hand");
            Matrix4x4 coreAfter = manager.Transforms[binding.RegionElement];
            Assert.That(Quaternion.Angle(coreAfter.rotation, coreBefore.rotation), Is.LessThan(0.5f),
                "the core pose is moved from the pre-grab one (no turn needed), not turned by the hand or the child offset");
            Vector3 moved = (Vector3)coreAfter.GetColumn(3) - (Vector3)coreBefore.GetColumn(3);
            Assert.That(Mathf.Abs(moved.y), Is.LessThan(1e-4f), "on the floor plane");
            Assert.That(new Vector2(moved.x, moved.z).magnitude, Is.GreaterThan(0.01f).And.LessThan(2f), "pulled the short way clear of the box");
            Vector2[] core = ToWorld(manager.LocalBoundaries[binding.RegionElement], coreAfter);
            foreach (Vector2 corner in footprint)
                Assert.That(WalkablePlacementSnap.Contains(core, corner), Is.False, "box corner " + corner);
            Assert.That(WalkableRegionFit.EdgesCross(core, footprint), Is.False, "edges");
        }

        // The core checks a region against the room's ring only once the ring exists, so an authored share waits for it.
        [Test]
        public void AnAuthoredShareWaitsForTheWalkableRing()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            var go = new GameObject("Board");
            _objects.Add(go);
            RecordingEntity entity = go.AddComponent<RecordingEntity>();
            go.AddComponent<SbSpatialAuthoring>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("waits for the room's walkable"));

            manager.ShareGameObject(go, false);
            manager.Update();

            Assert.That(entity.IsAttached(), Is.False);
            var parked = (IList)typeof(SpatialBlendManager).GetField("_sharesAwaitingWalkable",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Assert.That(parked.Count, Is.EqualTo(1));
        }

        private static Vector2[] WorldRegion(SpatialAuthoringPlan plan, Matrix4x4 objectWorld)
        {
            SpatialElementMapping mapping = Array.Find(plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Region);
            return ToWorld(mapping.polygon, objectWorld * plan.FrameToObject);
        }

        private static Vector2[] ToWorld(Vector2[] local, Matrix4x4 frameWorld) =>
            local.Select(p =>
            {
                Vector3 w = frameWorld.MultiplyPoint3x4(new Vector3(p.x, 0f, p.y));
                return new Vector2(w.x, w.z);
            }).ToArray();

        // ------------------------------------------------------------------ remote root

        // There is no raw-apply path: without a driver a received avatar is never moved by the core's target.
        [Test]
        public void WithoutADriverTheCoreRootTargetIsNeverApplied()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity entity = BindRecordingAvatar(manager);

            manager.Realize(Actor, new[] { Root(new Vector3(2f, 0f, 3f), 40f) });
            manager.LateUpdate();

            Assert.That(entity.Roots, Is.Empty);
        }

        // A core with no usable embodied port cannot drive a received avatar, and there is nothing to fall back
        // to: the avatar does not move, and the manager says so.
        [Test]
        public void AnAvatarTheCoreCannotDriveIsNotMovedAtAllAndTheManagerIsVisiblyDegraded()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity entity = BindRecordingAvatar(manager);
            Assert.That(manager.EmbodiedDriverDegraded, Is.False);
            Assert.That(manager.EmbodiedDriverError, Is.Null);

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("cannot be driven"));
            manager.ConfigureEmbodiedDriver(entity, Actor);   // the fake manager's CreateEmbodiedCore gives none

            Assert.That(manager.IsDriven(Actor), Is.False);
            Assert.That(manager.EmbodiedDriverDegraded, Is.True);
            Assert.That(manager.EmbodiedDriverError, Is.Not.Empty);
            manager.Realize(Actor, new[] { Root(new Vector3(2f, 0f, 3f), 40f) });
            manager.LateUpdate();
            Assert.That(entity.Roots, Is.Empty, "No raw fallback.");
        }

        // Single owner on the checked path: the realization-target handler never applies the root or the direct head (the checked
        // driver's anchors); an anchor the driver does not own still goes to the entity's anchor-target writer.
        [Test]
        public void ADrivenAvatarsRootTargetIsNeverAppliedByTheTargetHandler()
        {
            CheckedRig rig = NewCheckedRig();
            Vector3 head = rig.Retarget.headTransform.position;

            rig.Realize(new[] { Root(new Vector3(2f, 0f, 3f), 40f), Anchor("head", SbKernelType.AffordancePose, 4242),
                Anchor("hand", SbKernelType.AffordancePose, 4242) });

            Assert.That(rig.Retarget.headTransform.position, Is.EqualTo(head), "The driver writes the root itself, from its typed events.");
            Assert.That(rig.Applied, Is.EqualTo(new[] { "hand" }), "Neither the root nor the driver's head is the handler's.");
        }

        [Test]
        public void ALocalAvatarsHeadYawAndPitchAreSentTogetherAndAnObjectsAreNot()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity avatar = BindLocalAvatar(manager);
            avatar.transform.rotation = Quaternion.Euler(18f, 70f, 0f);

            manager.UpdateV2EntityState(avatar);

            Assert.That(manager.StateUpdates.Count, Is.EqualTo(1));
            Assert.That(manager.StateUpdates[0].yaw, Is.EqualTo(70f).Within(1e-3f));
            Assert.That(manager.StateUpdates[0].pitch, Is.EqualTo(18f).Within(1e-3f));
        }

        // The core's source tick is counted in calls: its movement gate sends nothing for a source that
        // stands still, and its heartbeat sends the source's relations again every kHeartbeatInterval calls, so that the
        // receivers re-solve against where their own bodies are by then. A local avatar that is pushed only when its Unity
        // transform changed starves it: the observed avatar's head kept answering the sample it had when the source last
        // moved, for as long as the source stood still.
        [Test]
        public void AStationaryLocalAvatarStillTicksTheCoresSourceEveryFrame()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity avatar = BindLocalAvatar(manager);
            avatar.transform.rotation = Quaternion.Euler(0f, 70f, 0f);

            manager.LateUpdate();
            Assert.That(manager.StateUpdates.Count, Is.EqualTo(1), "the first frame pushes the avatar's state");
            for (int frame = 0; frame < 5; ++frame)
            {
                manager.LateUpdate();
            }

            Assert.That(manager.StateUpdates.Count, Is.EqualTo(6), "a frame in which the avatar did not move is a tick of the core all the same");
            Assert.That(manager.StateUpdates.TrueForAll(u => Mathf.Abs(Mathf.DeltaAngle(u.yaw, 70f)) < 1e-3f), Is.True);
        }

        [Test]
        public void ARefusedSceneChangeIsVisibleOnTheManager()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            Assert.That(manager.SceneRegistrationError, Is.Null);

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not registered"));
            SpatialBlendAPI.ReportSceneStatus(manager.ID, 3, "sb_set_element_polygon", 12, () => "element 12 escapes its parent 4");

            Assert.That(manager.SceneRegistrationError, Does.Contain("element 12 escapes its parent 4"));
            manager.Disconnect();
            Assert.That(SpatialBlendAPI.TryGetSceneError(manager.ID, out _), Is.False, "A new session starts clean.");
        }

        // The first join places the receiver at the join; it does not walk there. On the checked
        // path the core sends the join's absolute solve as a PLACEMENT position request in the same revision as the JOIN source exception
        // (core c_api_test.cc CApiProposalTest.TypedTargetEventsAreOptInAndCarryTheirOwnCheckedLayout; JOIN on the first update:
        // anchor_proposals_test.cc SourceException.FirstUpdateOfASourceIsItsJoinException). Through the manager's real frame.
        [Test]
        public void TheFirstJoinPlacesTheReceiverThroughItsTypedPlacement()
        {
            CheckedRig rig = NewCheckedRig();
            Vector3 start = rig.Retarget.headTransform.position;
            Vector3 join = start + new Vector3(6f, 0f, 7f);
            rig.Core.Enqueue(new ActorEvent
            {
                Kind = ActorEventKind.SourceException, Revision = 1, ExceptionId = SpatialBlendManager.JoinExceptionId, Payload = new byte[0],
            });
            rig.Core.Enqueue(EmbodiedTargetConsumerTests.Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, join,
                placement: true));

            rig.Frame();

            Assert.That(rig.Retarget.headTransform.position.x, Is.EqualTo(join.x).Within(1e-4f), "placed at the join, not walked");
            Assert.That(rig.Retarget.headTransform.position.z, Is.EqualTo(join.z).Within(1e-4f));
            var placed = rig.Core.RootPublications.First(r => r.epoch == 2UL);
            Assert.That(placed.teleported, Is.True, "SB_EMBODIED_TELEPORTED with a new epoch");
            Assert.That(placed.position.x, Is.EqualTo(join.x).Within(1e-4f));

            // The JOIN itself is history only: a later ordinary request walks again, within the bound.
            rig.Core.Enqueue(EmbodiedTargetConsumerTests.Request(ActorEventKind.PositionRequest, 0, 2, TargetChannels.Position,
                join + new Vector3(5f, 0f, 0f)));
            rig.Frame();
            Assert.That(rig.Retarget.headTransform.position.x - join.x,
                Is.LessThanOrEqualTo(EmbodiedDriverConfig.Default.MaxLinearSpeedMetersPerSecond / 60f + 1e-4f));
        }

        // The rig's head already stands at the spawn height, so the spawn height is proven by
        // AnArrivalObservedAtAnotherHeightIsPlacedAtTheSpawnHeight, not here.
        [Test]
        public void ReceivedAvatarsArrivalIsPlacedThroughTheDriverAndTheCoreLearnsIt()
        {
            CheckedRig rig = NewCheckedRig();
            rig.Frame();
            ulong epochBefore = rig.Core.RootPublications[rig.Core.RootPublications.Count - 1].epoch;

            Assert.That(rig.Manager.PlaceReceivedAvatar(rig.Entity, new Vector3(2f, 0f, 5f), Quaternion.Euler(0f, 180f, 0f)), Is.True);
            Assert.That(rig.Retarget.headTransform.position.x, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(rig.Retarget.headTransform.position.z, Is.EqualTo(5f).Within(1e-4f));
            Assert.That(rig.Retarget.TryGetBodyPlacement(out Quaternion body), Is.True);
            Assert.That(Mathf.DeltaAngle(body.eulerAngles.y, 180f), Is.EqualTo(0f).Within(1e-3f));

            // The placement is published at the next frame, with a new epoch flagged as a placement.
            rig.Frame();
            var published = rig.Core.RootPublications.First(r => r.epoch == epochBefore + 1);
            Assert.That(published.teleported, Is.True, "SB_EMBODIED_TELEPORTED");
            Assert.That(published.sequence, Is.EqualTo(1UL), "A new epoch restarts the sequence.");
            Assert.That(published.position.x, Is.EqualTo(2f).Within(1e-4f), "The core learns the placed pose.");
            Assert.That(published.position.z, Is.EqualTo(5f).Within(1e-4f));
        }

        // An arriving body is placed with its head at the v1 spawn height whatever height it was observed at:
        // PlaceAtArrival names y = SpawnHeightMeters and the checked placement applies it.
        [Test]
        public void AnArrivalObservedAtAnotherHeightIsPlacedAtTheSpawnHeight()
        {
            CheckedRig rig = NewCheckedRig(headHeight: 1.2f);
            rig.Frame();

            Assert.That(rig.Manager.PlaceReceivedAvatar(rig.Entity, new Vector3(2f, 0.4f, 5f), Quaternion.identity), Is.True);
            rig.Frame();

            Assert.That(rig.Retarget.headTransform.position.y, Is.EqualTo(EmbodiedDriverConfig.SpawnHeightMeters).Within(1e-4f));
        }

        // Merges the former ResettingTheDriverGivesEveryPortANewEpochAndFlagsItsNextReport: on the checked path the driver's
        // ports are the root and the direct head, and a reset gives both a new epoch and flags their next report.
        [Test]
        public void TheAppPlacingTheBodyResetsTheDriverAndTheNextPublishCarriesANewEpoch()
        {
            CheckedRig rig = NewCheckedRig();
            rig.Frame();
            rig.Frame();

            Assert.That(rig.Manager.ResetEmbodiedDriver(rig.Entity), Is.True);
            rig.Frame();
            rig.Frame();

            var roots = rig.Core.RootPublications;
            Assert.That((roots[0].epoch, roots[0].teleported), Is.EqualTo((1UL, false)));
            Assert.That((roots[1].epoch, roots[1].teleported), Is.EqualTo((1UL, false)));
            Assert.That((roots[2].epoch, roots[2].teleported), Is.EqualTo((2UL, true)), "The first pose of the new epoch says the body was placed.");
            Assert.That((roots[3].epoch, roots[3].teleported), Is.EqualTo((2UL, false)));
            Assert.That(roots[2].sequence, Is.EqualTo(1UL), "A new epoch restarts the sequence.");
            Assert.That(roots[3].sequence, Is.EqualTo(2UL));
            var heads = rig.Core.HeadPublications;
            Assert.That((heads[2].epoch, heads[2].teleported), Is.EqualTo((2UL, true)), "Every port of the driver, the head's too.");
            Assert.That((heads[3].epoch, heads[3].teleported), Is.EqualTo((2UL, false)));
        }

        [Test]
        public void ResettingAnEntityWithNoDriverDoesNothing()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity entity = BindRecordingAvatar(manager);

            Assert.That(manager.ResetEmbodiedDriver(entity), Is.False);
            Assert.That(manager.ResetEmbodiedDriver(null), Is.False);
        }

        [Test]
        public void TheBoundsAreTheOneDeclaredConfigAndNothingPerAvatarTeleportsIt()
        {
            var avatar = new GameObject("config-avatar");
            _objects.Add(avatar);
            avatar.SetActive(false);
            var entity = avatar.AddComponent<SbNetworkAvatarEntity>();

            EmbodiedDriverConfig defaults = SpatialBlendManager.ResolveEmbodiedDriverConfig(entity);
            Assert.That(defaults.MaxLinearSpeedMetersPerSecond, Is.EqualTo(EmbodiedDriverConfig.Default.MaxLinearSpeedMetersPerSecond));
            Assert.That(defaults.MaxAngularSpeedDegreesPerSecond, Is.EqualTo(EmbodiedDriverConfig.Default.MaxAngularSpeedDegreesPerSecond));
            Assert.That(typeof(SbNetworkAvatarEntity).GetField("maxPositionVelocity"), Is.Null, "No second copy of the bounds.");
            Assert.That(typeof(SbNetworkAvatarEntity).GetField("maxAngularVelocity"), Is.Null);
            // The only teleports are core's request and a source exception. No per-avatar opt-in survives.
            Assert.That(typeof(SbNetworkAvatarEntity).GetField("teleportOnEngagement"), Is.Null);
            Assert.That(typeof(EmbodiedDriverConfig).GetProperty("TeleportOnEngagement", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), Is.Null);
        }

        [Test]
        public void NoProposalToReadMeansNoWriteAndStepsNothing()
        {
            CheckedRig rig = NewCheckedRig();
            Vector3 head = rig.Retarget.headTransform.position;
            rig.Core.HasProposal = false;

            rig.Frame();

            Assert.That(rig.Retarget.headTransform.position, Is.EqualTo(head));
            Assert.That(rig.Core.RootPublications.Count, Is.EqualTo(1), "The pose is still published.");
        }

        // The raising half of the round trip. There is no receiving half (an app OnSourceException handler):
        // the receiver's checked driver realizes the exception from the core's typed stream.
        [Test]
        public void ASourceExceptionIsRaisedForTheLocalAvatarWithItsAppIdAndOpaquePayload()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            RecordingEntity local = BindLocalAvatar(manager);
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            uint id = 0x7001;   // an app-defined id, opaque to the core

            Assert.That(manager.RaiseSourceException(local, id, payload), Is.True);

            Assert.That(manager.Declared, Is.EqualTo(new[] { LocalActor }));
            Assert.That(manager.ExceptionIds, Is.EqualTo(new[] { id }));
            Assert.That(manager.ExceptionPayloads[0], Is.EqualTo(payload));
        }

        [Test]
        public void OnlyALocalAvatarMayRaiseASourceException()
        {
            CheckedRig rig = NewCheckedRig();

            Assert.That(rig.Manager.RaiseSourceException(rig.Entity, 1), Is.False, "A received avatar is not a source.");
            Assert.That(rig.Manager.RaiseSourceException(null, 1), Is.False);
            Assert.That(rig.Manager.Declared, Is.Empty);
        }

        // The native gate's fixtures compile into the gate project with the product, so a removed hook there breaks the whole
        // gate. In the repository they are tools/freeform-native-verification/*.cs; run-freeform-native-tests.sh copies them into
        // the gate project's Restoration folder, where tools/ is absent. Either way they are read, and never none.
        private static IEnumerable<string> NativeGateFixtures()
        {
            const string repository = "tools/freeform-native-verification";
            string[] fixtures = System.IO.Directory.Exists(repository)
                ? System.IO.Directory.GetFiles(repository, "*.cs")
                : System.IO.Directory.GetFiles("Assets/SpatialBlend/Framework/Test/Restoration", "Native*Tests.cs");
            Assert.That(fixtures, Is.Not.Empty, "the native gate fixtures were not found");
            return fixtures;
        }

        [Test]
        public void TheDriverContractHasNoCompatibilityShimForTheRemovedPaths()
        {
            string text = string.Concat(
                System.IO.Directory.GetFiles("Assets/SpatialBlend/Framework/Scripts", "SpatialBlendManager*.cs").Select(System.IO.File.ReadAllText),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/Realization/EmbodiedDriver.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/Realization/EmbodiedCore.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/Embodied/OvrAvatarEmbodiedDriver.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/Embodied/IEmbodiedDriver.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/Embodied/OvrAvatarEmbodiedDriver.TargetEvents.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs"),
                string.Concat(NativeGateFixtures().Select(System.IO.File.ReadAllText)));
            foreach (string removed in new[]
            {
                "TeleportOnEngagement", "teleportOnEngagement", "ArrivalBudget", "arrival_budget", "SeatYaw", "SeatEngaged",
                "contain_valid", "ContainAlpha", "SourceDiscontinuity", "DeclareSourceDiscontinuity", "sb_declare_source_discontinuity",
                "contained_x", "WithTeleportOnEngagement",
                // Absent: the checked path is the only one.
                "OnSourceException", "SourceExceptionHandler", "IEmbodiedMotionSource", "EmbodiedMotionSourceRegistry",
                "CheckedTargetEventsSelected", "NotifyAvatarSdkFinalUpdate",
            })
            {
                Assert.That(text, Does.Not.Contain(removed), removed);
            }
        }

        // ------------------------------------------------------------------ local discontinuities

        private const long LocalActor = 55L;

        private RecordingEntity BindLocalAvatar(ContainmentManager manager)
        {
            var avatar = new GameObject("containment-local-avatar");
            avatar.tag = "Avatar";
            avatar.SetActive(false);
            _objects.Add(avatar);
            var entity = avatar.AddComponent<RecordingEntity>();
            entity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(entity, new object[] { manager, 8L, true });
            ((IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager)).Add(entity.ID, LocalActor);
            return entity;
        }

        // Single owner, as behaviour: nothing but the driver writes a received avatar's root.
        // With no typed event the manager writes nothing; with a root position request, exactly the driver's bounded step lands.
        [Test]
        public void TheDriverIsTheOnlyWriterOfAReceivedRoot()
        {
            CheckedRig rig = NewCheckedRig();
            Vector3 start = rig.Retarget.headTransform.position;
            ulong idle = rig.Revision;
            rig.Frame();
            rig.Frame();
            Assert.That(rig.Retarget.headTransform.position, Is.EqualTo(start), "no request, no write: the manager has no root write of its own");
            Assert.That(rig.Revision, Is.EqualTo(idle), "nothing was committed");

            rig.Core.Enqueue(EmbodiedTargetConsumerTests.Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position,
                start + new Vector3(5f, 0f, 0f)));
            rig.Frame();
            // One driver step: one root move and the head carried with it, each one committed write of the lease.
            Assert.That(rig.Revision, Is.EqualTo(idle + 2), "one write, the driver's step (the root and its head carry)");
            float step = rig.Retarget.headTransform.position.x - start.x;
            Assert.That(step, Is.GreaterThan(0f));
            Assert.That(step, Is.LessThanOrEqualTo(EmbodiedDriverConfig.Default.MaxLinearSpeedMetersPerSecond / 60f + 1e-4f),
                "at the driver's own bound, not a jump to the target");
        }

        [Test]
        public void PlacementAndResetGoThroughTheDriverAndNothingElseMovesTheBody()
        {
            CheckedRig rig = NewCheckedRig();
            rig.Frame();
            Vector3 head = rig.Retarget.headTransform.position;

            // Reset moves nothing: it tells the core the body was placed from outside (a new epoch).
            Assert.That(rig.Manager.ResetEmbodiedDriver(rig.Entity), Is.True);
            Assert.That(rig.Retarget.headTransform.position, Is.EqualTo(head));
            rig.Core.HasProposal = false;
            rig.Frame();
            var last = rig.Core.RootPublications.Last();
            Assert.That((last.epoch, last.teleported), Is.EqualTo((2UL, true)), "the next report carries the new epoch and TELEPORTED");

            // Placement writes the body once, through the driver, and publishes it as a placement.
            Assert.That(rig.Manager.PlaceReceivedAvatar(rig.Entity, new Vector3(2f, 0f, 5f), Quaternion.identity), Is.True);
            Assert.That(rig.Retarget.headTransform.position.x, Is.EqualTo(2f).Within(1e-4f));
            rig.Frame();
            Assert.That(rig.Core.RootPublications.First(r => r.epoch == 3UL).teleported, Is.True);
            Assert.That(rig.Manager.ResetEmbodiedDriver(null), Is.False);
        }

        // The head anchor's height is v1's avatar height, 1.58 m (v1 SbRetargetTransform's
        // AVATAR_HEIGHT, which SetHeadTransform forced), and it is the one height the driver places a received avatar at.
        [Test]
        public void TheHeadAnchorHeightIsV1sAvatarHeightAndTheDriversSpawnHeight()
        {
            Assert.That(SpatialBlendManager.HeadAnchorHeightMeters, Is.EqualTo(1.58f));
            Assert.That(SpatialBlendManager.HeadAnchorHeightMeters, Is.EqualTo(EmbodiedDriverConfig.SpawnHeightMeters));
        }

        [Test]
        public void ARecenterRaisesNoSourceExceptionAndResetsEveryReceivedAvatarsDriverAsTeleported()
        {
            CheckedRig rig = NewCheckedRig();
            BindLocalAvatar(rig.Manager);
            rig.Frame();

            rig.Manager.OnOvrRecentered();
            rig.Frame();

            Assert.That(rig.Manager.Declared, Is.Empty, "a frame change is not body movement (2A): never a source exception");
            Assert.That(rig.Manager.ExceptionIds, Is.Empty);
            var roots = rig.Core.RootPublications;
            Assert.That((roots[0].epoch, roots[0].teleported), Is.EqualTo((1UL, false)));
            Assert.That((roots[1].epoch, roots[1].teleported), Is.EqualTo((2UL, true)), "SB_EMBODIED_TELEPORTED with the new epoch.");
        }

        [Test]
        public void TheFirstMrukSceneLoadIsCatchUpAndEveryLaterOneIsAReAnchoring()
        {
            CheckedRig rig = NewCheckedRig();
            BindLocalAvatar(rig.Manager);

            rig.Manager.OnMrukSceneLoaded();
            Assert.That(rig.Manager.Declared, Is.Empty);

            rig.Manager.OnMrukSceneLoaded();
            Assert.That(rig.Manager.Declared, Is.Empty, "a re-anchoring is a frame change: it raises no source exception");
            rig.Frame();
            var first = rig.Core.RootPublications[0];
            Assert.That((first.epoch, first.teleported), Is.EqualTo((2UL, true)));
        }

        [Test]
        public void AnExplicitAppTeleportPlacesAndRaisesAnExceptionForOnlyThatAvatarAndLeavesTheReceivedDriversAlone()
        {
            CheckedRig rig = NewCheckedRig();
            RecordingEntity local = BindLocalAvatar(rig.Manager);

            Assert.That(rig.Manager.TeleportLocalAvatar(local, new Vector3(3f, 0f, 4f), 90f), Is.True);

            Assert.That(local.transform.position.x, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(local.transform.rotation.eulerAngles.y, Is.EqualTo(90f).Within(1e-3f));
            Assert.That(rig.Manager.Declared, Is.EqualTo(new[] { LocalActor }));
            Assert.That(rig.Manager.ExceptionIds, Is.EqualTo(new[] { SpatialBlendManager.ExceptionIdOf(LocalDiscontinuity.AppTeleport) }));
            Assert.That(rig.Manager.Pushes, Is.EqualTo(1), "The update that carries the exception follows the placement.");
            rig.Frame();
            var first = rig.Core.RootPublications[0];
            Assert.That((first.epoch, first.teleported), Is.EqualTo((1UL, false)), "A source teleport is not a jump of the receivers' frame.");
            Assert.That(rig.Manager.TeleportLocalAvatar(rig.Entity, Vector3.zero, 0f), Is.False, "A received avatar is not teleported by the app.");
        }

        [Test]
        public void AReceivedAvatarNeverRaisesASourceException()
        {
            CheckedRig rig = NewCheckedRig();

            rig.Manager.ReportLocalDiscontinuity(LocalDiscontinuity.TrackingOriginReset);

            Assert.That(rig.Manager.Declared, Is.Empty);
        }

        // There is no explicit SessionRejoin exception. A rejoined peer's local
        // avatar is a new source actor, and its first update carries the implicit SB_EXCEPTION_JOIN.
        [Test]
        public void AReconnectRaisesNoExplicitExceptionBecauseTheImplicitJoinCoversIt()
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            BindLocalAvatar(manager);
            manager.TickDiscontinuities();
            Assert.That(manager.Declared, Is.Empty, "The first connection raises nothing.");

            manager.Disconnect();
            manager.Host(1);
            manager.TickDiscontinuities();
            BindLocalAvatar(manager);
            manager.TickDiscontinuities();
            manager.TickDiscontinuities();
            Assert.That(manager.Declared, Is.Empty, "A rejoin raises no explicit exception: the first update carries JOIN.");
            Assert.That(System.Enum.GetNames(typeof(LocalDiscontinuity)), Does.Not.Contain("SessionRejoin"));
        }

        [Test]
        public void TheHookSitesAreWiredToTheHeadsetAndMrukEvents()
        {
            string source = System.IO.File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.Discontinuity.cs");
            Assert.That(source, Does.Contain("display.RecenteredPose += OnOvrRecentered"));
            Assert.That(source, Does.Contain("OVRManager.TrackingOriginChangePending += OnTrackingOriginChangePending"));
            Assert.That(source, Does.Contain("mruk.RegisterSceneLoadedCallback(OnMrukSceneLoaded)"));
            Assert.That(source, Does.Contain("-= OnOvrRecentered"));
            Assert.That(source, Does.Contain("RemoveListener(OnMrukSceneLoaded)"));
        }

        // ------------------------------------------------------------------ Facing

        // Facing is global; the app does not drop Facing targets by
        // where their referent is, so the interaction value filters nothing.
        [Test]
        public void FacingStaysGlobalWhileContained()
        {
            CheckedRig rig = NewCheckedRig();
            ContainmentManager manager = rig.Manager;
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null, out _),
                Is.True);
            long outsideShared = 4242;
            long boardShared = ChessObject;
            long surfaceShared = SpatialAuthoringCompiler.AffordanceSharedId(ChessObject, 0);

            rig.Realize(new[] { Root(new Vector3(0f, 0f, 0f), 0f), Chest(outsideShared) });
            Assert.That(rig.Applied, Is.EqualTo(new[] { "chest" }));

            // Contained: Facing to the outside referent still applies, and so does Facing to the object or its surface.
            rig.Applied.Clear();
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                ChessRegion);
            rig.Frame();
            Assert.That(manager.IsTransferInteracting(rig.Entity), Is.True);
            rig.Realize(new[] { Root(new Vector3(0f, 0f, 0f), 0f), Chest(outsideShared) });
            rig.Realize(new[] { Root(new Vector3(0f, 0f, 0f), 0f), Chest(boardShared) });
            rig.Realize(new[] { Root(new Vector3(0f, 0f, 0f), 0f), Chest(surfaceShared) });
            Assert.That(rig.Applied, Is.EqualTo(new[] { "chest", "chest", "chest" }),
                "each Facing target (outside, the object, its surface) is applied");
        }

        [Test]
        public void TheInteractionValueFiltersNoTargetWhetherOrNotThisPeerMapsTheObject()
        {
            CheckedRig rig = NewCheckedRig();
            ContainmentManager manager = rig.Manager;
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null, out _),
                Is.True);

            // A hand AffordancePose target and a Spacing target to something outside both apply.
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled | AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived,
                ChessRegion);
            rig.Frame();
            Assert.That(manager.IsTransferInteracting(rig.Entity), Is.True);
            rig.Realize(new[]
            {
                Root(Vector3.zero, 0f),
                Anchor("hand", SbKernelType.AffordancePose, 4242),
                Anchor("pelvis", SbKernelType.Spacing, 4242),
            });
            Assert.That(rig.Applied, Is.EquivalentTo(new[] { "hand", "pelvis" }));

            // Contained at the source only (no counterpart here, so the flag is clear): the scene-level targets all stand.
            rig.Applied.Clear();
            rig.Core.Proposal = Mapped(AnchorProposalFlags.Sampled);
            rig.Frame();
            Assert.That(manager.IsTransferInteracting(rig.Entity), Is.False);
            rig.Realize(new[] { Root(Vector3.zero, 0f), Chest(4242) });
            Assert.That(rig.Applied, Is.EqualTo(new[] { "chest" }));
        }

        // ------------------------------------------------------------------ driven rig

        // A received avatar on the checked target-event path, the only one: the production entity type with its
        // retarget head as the sample origin, admitted by the manager's own ConfigureEmbodiedDriver against a target-event core.
        private sealed class CheckedRig
        {
            public ContainmentManager Manager;
            public SbNetworkAvatarEntity Entity;
            public SbRetargetTransform Retarget;
            public AnchorTargetController Controller;
            public CheckedCore Core;
            // Every non-root anchor target the manager's realization-target handler handed the entity's writer, in order.
            public readonly List<string> Applied = new List<string>();

            public void Frame() => Manager.LateUpdate();

            // The revision of the driver's checked realization lease: it advances once per committed root or head write.
            public ulong Revision
            {
                get
                {
                    var driver = (OvrAvatarEmbodiedDriver)((IDictionary)typeof(SpatialBlendManager).GetField("_v2EmbodiedDrivers",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Manager))[Actor];
                    var lease = (SbRetargetTransform.RealizationLease)typeof(OvrAvatarEmbodiedDriver)
                        .GetField("_checkedLease", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                    Assert.That(lease.TryRead(out var sample), Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
                    return sample.Revision;
                }
            }

            // EditMode time does not advance within a test, and the controller ignores an equal-time redelivery of an anchor.
            // Forget its receipt clock so that each delivery of this call is observable, as one frame later it would be.
            public void Realize(IReadOnlyList<RealizationTarget> targets)
            {
                ((IDictionary)typeof(AnchorTargetController).GetField("_lastAcceptedAt",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Controller)).Clear();
                Manager.Realize(Actor, targets);
            }
        }

        private CheckedRig NewCheckedRig(float headHeight = EmbodiedDriverConfig.SpawnHeightMeters)
        {
            ContainmentManager manager = NewManager();
            manager.Host(1);
            manager.TestOnlyDriverSeconds = 1f / 60f;
            var core = new CheckedCore();
            manager.Core = core;

            var avatar = new GameObject("containment-checked-avatar");
            _objects.Add(avatar);
            var entity = avatar.AddComponent<SbNetworkAvatarEntity>();
            var controller = avatar.AddComponent<AnchorTargetController>();
            var sdk = new GameObject("inactive-sdk-avatar");
            sdk.SetActive(false); // Do not initialize/load a native SDK avatar in EditMode.
            _objects.Add(sdk);
            var sdkEntity = sdk.AddComponent<SbAvatarEntity>();
            var root = new GameObject("containment-checked-retarget");
            _objects.Add(root);
            var retarget = root.AddComponent<SbRetargetTransform>();
            retarget.headTransform = Part(root, "head");
            retarget.leftHandTransform = Part(root, "left");
            retarget.rightHandTransform = Part(root, "right");
            retarget.SetHeadTransform(new Vector3(0f, headHeight, 0f), Quaternion.identity);
            sdkEntity._retargetTransform = retarget;
            typeof(SbNetworkAvatarEntity).GetField("_sbAvatar", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(entity, sdkEntity);
            entity.SetTransform(retarget.headTransform);
            Map(manager, entity);

            var rig = new CheckedRig { Manager = manager, Entity = entity, Retarget = retarget, Controller = controller, Core = core };
            controller.TargetApplied += (anchor, _, __, ___) => rig.Applied.Add(anchor);
            manager.ConfigureEmbodiedDriver(entity, Actor);
            Assert.That(manager.IsDriven(Actor), Is.True, manager.EmbodiedDriverError);
            Assert.That(core.Subscriptions, Is.EqualTo(new[] { true }), "admitted to the typed target-event stream");
            // The arrival pose is published as soon as the driver exists. A port publishes once per successful drain (the
            // fence, EmbodiedTargetConsumer.NativeEmpty), so the first frame only drains; the tests count the frames after it,
            // each of which publishes.
            Assert.That(core.RootPublications.Count, Is.EqualTo(1));
            Assert.That((core.RootPublications[0].epoch, core.RootPublications[0].teleported), Is.EqualTo((1UL, false)));
            rig.Frame();
            Assert.That(core.RootPublications.Count, Is.EqualTo(1), "the frame after admission is the drain fence");
            core.Publications.Clear();
            rig.Applied.Clear();
            return rig;
        }

        private static Transform Part(GameObject parent, string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(parent.transform, false);
            return part;
        }

        /// <summary>
        /// A scripted target-event core: the proposal set it is given (status metadata only on the checked path), the typed
        /// events it is told to queue, and every accepted publication with its stamp. Anchor 0 is the root, 1 the head.
        /// </summary>
        internal sealed class CheckedCore : IEmbodiedTargetEventCore
        {
            public const uint HeadAnchor = 1;
            public readonly List<(uint anchor, ulong epoch, ulong sequence, bool teleported, Vector3 position)> Publications =
                new List<(uint, ulong, ulong, bool, Vector3)>();
            public List<(ulong epoch, ulong sequence, bool teleported, Vector3 position)> RootPublications => Of(0);
            public List<(ulong epoch, ulong sequence, bool teleported, Vector3 position)> HeadPublications => Of(HeadAnchor);
            public readonly List<bool> Subscriptions = new List<bool>();
            public readonly List<AnchorProposal> Others = new List<AnchorProposal>();
            public AnchorProposal Proposal;
            public ulong Revision = 1;
            public bool HasProposal = true;
            private readonly Queue<ActorEvent> _events = new Queue<ActorEvent>();

            private List<(ulong, ulong, bool, Vector3)> Of(uint anchor) =>
                Publications.Where(p => p.anchor == anchor).Select(p => (p.epoch, p.sequence, p.teleported, p.position)).ToList();

            public void Enqueue(ActorEvent actorEvent) => _events.Enqueue(actorEvent);

            public bool TrySetTargetEventDelivery(long actor, bool enabled, out string error)
            {
                Subscriptions.Add(enabled);
                error = null;
                return true;
            }

            public ActorEventPollResult PollEvent(long actor, out ActorEvent actorEvent, out ActorEventPollFailure failure)
            {
                failure = null;
                if (_events.Count == 0) { actorEvent = default; return ActorEventPollResult.NativeEmpty; }
                actorEvent = _events.Dequeue();
                return ActorEventPollResult.Event;
            }

            public bool TryGetAnchorId(long actor, string name, out uint id)
            {
                id = name == "root" ? 0u : name == "head" ? HeadAnchor : 5u;
                return true;
            }

            public bool TryGetAnchorName(long actor, uint id, out string name)
            {
                name = id == 0 ? "root" : id == HeadAnchor ? "head" : "bone";
                return true;
            }

            public void PublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double time, ulong sequence, ulong epoch,
                                          bool teleported) =>
                throw new InvalidOperationException("The checked path publishes through the exact-yaw overload only.");

            public bool TryPublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double time, ulong sequence, ulong epoch,
                                             bool teleported) =>
                throw new InvalidOperationException("Continuous publication must use the double overload.");

            public bool TryPublishAnchorPose(long actor, uint anchor, in EmbodiedPose pose, double yaw, double time,
                ulong sequence, ulong epoch, bool teleported, out double nativeYaw)
            {
                nativeYaw = (90d - yaw) * Math.PI / 180d;
                // The native port's rule (spatialblend/space/embodied_port.h, kStale/kInvalid): a sample must be newer than the last
                // accepted one of that anchor, by epoch, then by sequence within the epoch; sequence 0 is invalid.
                var key = (actor, anchor);
                if (sequence == 0 || (_accepted.TryGetValue(key, out var have) &&
                                      (epoch < have.epoch || (epoch == have.epoch && sequence <= have.sequence))))
                {
                    Refused.Add((anchor, epoch, sequence));
                    return false;
                }
                _accepted[key] = (epoch, sequence);
                Publications.Add((anchor, epoch, sequence, teleported, pose.Position));
                return true;
            }

            private readonly Dictionary<(long actor, uint anchor), (ulong epoch, ulong sequence)> _accepted =
                new Dictionary<(long, uint), (ulong, ulong)>();
            /// <summary>Publications the native rule would refuse as stale or invalid (not in <see cref="Publications"/>).</summary>
            public readonly List<(uint anchor, ulong epoch, ulong sequence)> Refused = new List<(uint, ulong, ulong)>();

            public bool TryReadProposals(long actor, ProposalSet proposals)
            {
                proposals.Clear();
                if (!HasProposal) return false;
                proposals.Revision = Revision;
                proposals.Anchors.Add(Proposal);
                proposals.Anchors.AddRange(Others);
                proposals.TotalAnchorCount = (uint)proposals.Anchors.Count;
                return true;
            }

            public bool TryPollEvent(long actor, out ActorEvent actorEvent) =>
                throw new InvalidOperationException("The checked path never polls the legacy bool stream.");

            public bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> points)
            {
                points.Add(to);
                return true;
            }

            public EmbodimentRegistrationResult RegisterEmbodiment(long actor, string json, out string error)
            {
                error = null;
                return EmbodimentRegistrationResult.Unsupported;
            }

            public bool TryGetBoneId(long actor, string name, out int id) { id = 0; return false; }
            public void PublishBonePose(long actor, int bone, in EmbodiedBonePose pose, double time, ulong sequence, ulong epoch,
                                        bool teleported) { }
            public bool TryPublishBonePose(long actor, int bone, in EmbodiedBonePose pose, double time, ulong sequence, ulong epoch,
                                           bool teleported) => false;
            public bool TryAnchorPoseFromBone(long actor, string name, in EmbodiedBonePose bone, out EmbodiedBonePose anchor)
            {
                anchor = default;
                return false;
            }
            public bool TryBonePoseFromAnchor(long actor, string name, in EmbodiedBonePose anchor, out EmbodiedBonePose bone)
            {
                bone = default;
                return false;
            }
        }

        /// <summary>
        /// A scripted core: it records the order of the calls, answers with the proposals it was given and queues the
        /// events it is told to. Anchor ids are interned in order of first use; 0 is the root.
        /// </summary>
        internal sealed class FakeEmbodiedCore : IEmbodiedCore
        {
            public readonly List<string> Calls = new List<string>();
            // Root publishes only (anchor 0), in order.
            public readonly List<(long actor, EmbodiedPose pose, ulong sequence)> Published =
                new List<(long actor, EmbodiedPose pose, ulong sequence)>();
            public readonly List<(ulong epoch, bool teleported)> Epochs = new List<(ulong epoch, bool teleported)>();
            // Every publish of a non-root anchor, by anchor id.
            public readonly List<(uint anchor, EmbodiedPose pose, ulong sequence, ulong epoch, bool teleported)> AnchorPublished =
                new List<(uint anchor, EmbodiedPose pose, ulong sequence, ulong epoch, bool teleported)>();
            public readonly List<ActorEvent> Events = new List<ActorEvent>();
            public readonly List<AnchorProposal> Others = new List<AnchorProposal>();
            private readonly List<string> _anchors = new List<string> { "root" };
            public AnchorProposal Proposal;
            public ulong Revision = 1;
            public bool HasProposal = true;

            public bool TryGetAnchorId(long actor, string anchorName, out uint anchorId)
            {
                int index = _anchors.IndexOf(anchorName);
                if (index < 0)
                {
                    _anchors.Add(anchorName);
                    index = _anchors.Count - 1;
                }
                anchorId = (uint)index;
                return true;
            }

            public bool TryGetAnchorName(long actor, uint anchorId, out string anchorName)
            {
                anchorName = anchorId < _anchors.Count ? _anchors[(int)anchorId] : null;
                return anchorName != null;
            }

            public void PublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds,
                                          ulong sequence, ulong epoch, bool teleported)
            {
                if (anchorId != 0)
                {
                    AnchorPublished.Add((anchorId, pose, sequence, epoch, teleported));
                    return;
                }
                Calls.Add("publish");
                Published.Add((actor, pose, sequence));
                Epochs.Add((epoch, teleported));
            }

            public bool TryReadProposals(long actor, ProposalSet proposals)
            {
                Calls.Add("read");
                proposals.Clear();
                if (!HasProposal) return false;
                proposals.Revision = Revision;
                proposals.Anchors.Add(Proposal);
                proposals.Anchors.AddRange(Others);
                proposals.TotalAnchorCount = (uint)proposals.Anchors.Count;
                return true;
            }

            public bool TryPollEvent(long actor, out ActorEvent actorEvent)
            {
                Calls.Add("poll");
                if (Events.Count == 0)
                {
                    actorEvent = default;
                    return false;
                }
                actorEvent = Events[0];
                Events.RemoveAt(0);
                return true;
            }

            public bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> waypoints) => false;

            // Embodiment registration (ABI 4.7): this scripted core has none, so the driver keeps the legacy anchors.
            public EmbodimentRegistrationResult RegisterEmbodiment(long actor, string specJson, out string error)
            {
                error = null;
                return EmbodimentRegistrationResult.Unsupported;
            }

            public bool TryGetBoneId(long actor, string boneName, out int boneId)
            {
                boneId = -1;
                return false;
            }

            public void PublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence,
                                        ulong epoch, bool teleported)
            {
            }

            public bool TryAnchorPoseFromBone(long actor, string anchorName, in EmbodiedBonePose bone, out EmbodiedBonePose anchor)
            {
                anchor = default;
                return false;
            }

            public bool TryBonePoseFromAnchor(long actor, string anchorName, in EmbodiedBonePose anchor, out EmbodiedBonePose bone)
            {
                bone = default;
                return false;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertRegionLocalAndFlagged(ContainmentManager manager, SpatialAuthoringPlan plan,
                                                        SpatialAuthoringBinding binding, string label = "")
        {
            SpatialElementMapping region = plan.Mappings.Single(m => m.kind == SpatialHierarchyNodeKind.Region);
            Assert.That(manager.LocalBoundaries.ContainsKey(binding.RegionElement), Is.True, label);
            Assert.That(manager.LocalBoundaries[binding.RegionElement], Is.EqualTo(region.polygon),
                label + ": the region polygon is the one authored in the object frame, not baked to world.");
            Assert.That(manager.Flags.TryGetValue(binding.RegionElement, out bool flagged) && flagged, Is.True, label);
            Assert.That(manager.CallOrder.IndexOf("local:" + binding.RegionElement),
                Is.LessThan(manager.CallOrder.IndexOf("flag:" + binding.RegionElement)),
                label + ": the flag needs the Containment definition first.");
        }

        // A root proposal of a mapped body: the flags the core sets (SAMPLED, TRANSFER, ARRIVED) and the leaf it names.
        private static AnchorProposal Mapped(AnchorProposalFlags flags, long referent = 0) => new AnchorProposal
        {
            AnchorId = 0,
            Status = EmbodiedStatus.Exact,
            Position = Vector3.zero,
            YawDegrees = 0f,
            Flags = flags,
            ReferentSharedId = referent,
        };

        private static RealizationTarget Root(Vector3 position, float yawDegrees) => new RealizationTarget
        {
            AnchorName = "root",
            Position = position,
            Rotation = Quaternion.Euler(0f, yawDegrees, 0f),
            KernelType = SbKernelType.Spacing,
            Weight = 1.0,
        };

        private static RealizationTarget Chest(long referentShared) => Anchor("chest", SbKernelType.Facing, referentShared);

        private static RealizationTarget Anchor(string name, SbKernelType kernel, long referentShared) =>
            new RealizationTarget
            {
                AnchorName = name,
                Position = new Vector3(1f, 1.2f, 1f),
                Rotation = Quaternion.identity,
                HasHeight = true,
                KernelType = kernel,
                ReferentId = referentShared,
                Weight = 1.0,
            };

        private static SpatialAuthoringPlan Compile(SbSpatialAuthoring authoring)
        {
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            return plan;
        }

        private static SpatialAuthoringPlan CompileChess() =>
            CompilePrefab("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");

        private static SpatialAuthoringPlan CompilePrefab(string path)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                SbSpatialAuthoring authoring = root.GetComponentInChildren<SbSpatialAuthoring>(true);
                Assert.That(authoring, Is.Not.Null, path);
                return Compile(authoring);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private ContainmentManager NewManager()
        {
            var managerObject = new GameObject("containment-contract");
            managerObject.SetActive(false);
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<ContainmentManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            return manager;
        }

        private SbEntity BindAvatar(ContainmentManager manager, List<InteractionEventType> events,
                                      Action<InteractionEventType, long> detail = null)
        {
            var avatar = new GameObject("containment-avatar");
            avatar.SetActive(false);
            _objects.Add(avatar);
            var entity = avatar.AddComponent<SbNativeEntity>();
            entity.AllowManipulation = false;
            entity.SetOnInteractionCallback((type, referent, proximity, __) =>
            {
                Assert.That(proximity, Is.EqualTo(ProximityType.Euclidean2D));
                events.Add(type);
                detail?.Invoke(type, referent);
            });
            Map(manager, entity);
            return entity;
        }

        private RecordingEntity BindRecordingAvatar(ContainmentManager manager)
        {
            var avatar = new GameObject("containment-recording-avatar");
            avatar.SetActive(false);
            _objects.Add(avatar);
            var entity = avatar.AddComponent<RecordingEntity>();
            entity.AllowManipulation = false;
            Map(manager, entity);
            return entity;
        }

        private static void Map(ContainmentManager manager, SbEntity entity, bool isSource = false)
        {
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(entity, new object[] { manager, 7L, isSource });
            IDictionary byEntity = (IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            IDictionary byActor = (IDictionary)typeof(SpatialBlendManager).GetField("_v2EntityByActor",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            byEntity.Add(entity.ID, Actor);
            byActor.Add(Actor, entity.ID);
        }

        public sealed class RecordingEntity : SbLocalEntity
        {
            public readonly List<(Vector3 position, Quaternion rotation)> Roots =
                new List<(Vector3 position, Quaternion rotation)>();
            public readonly List<(string name, long referent)> AnchorApplies = new List<(string name, long referent)>();
            public readonly List<string> AnchorCancels = new List<string>();
            // The body anchors this entity can sample (ABI 4), with the pose each has now.
            public readonly List<string> SampledAnchors = new List<string>();
            public readonly Dictionary<string, (Vector3 position, Quaternion rotation)> AnchorPoses =
                new Dictionary<string, (Vector3 position, Quaternion rotation)>();
            public readonly List<(string name, Vector3 position, float yaw)> AnchorWrites =
                new List<(string name, Vector3 position, float yaw)>();
            private long _lastReferent;

            public override IReadOnlyList<string> RealizedAnchorNames => SampledAnchors;

            public override bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
            {
                if (AnchorPoses.TryGetValue(anchorName, out var pose))
                {
                    position = pose.position;
                    rotation = pose.rotation;
                    return true;
                }
                position = default;
                rotation = Quaternion.identity;
                return false;
            }

            public override void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation) =>
                Roots.Add((worldPosition, worldRotation));

            public override void ApplyAnchorTarget(string anchorName, Vector3 worldPosition, float yaw,
                                                   bool hasHeight, double weight)
            {
                AnchorApplies.Add((anchorName, _lastReferent));
                AnchorWrites.Add((anchorName, worldPosition, yaw));
            }

            public override void CancelAnchorTarget(string anchorName) => AnchorCancels.Add(anchorName);

            public int CancelCalls;
            public override void CancelRealizationTarget() => ++CancelCalls;

            internal void RememberReferent(long referent) => _lastReferent = referent;
        }

        public sealed class ContainmentManager : SpatialBlendManager
        {
            private static long _next = 830000;
            public readonly List<string> Order = new List<string>();
            public readonly List<string> CallOrder = new List<string>();
            public bool FeatureAvailable = true;
            public bool RefuseFlag;
            // The core refuses the region's polygon, as it does for a region that sticks out of the room
            // (ContainmentEscapesParent).
            public bool RefuseRegionPolygon;
            public WalkableRegion Walkable;
            protected override WalkableRegion AuthoringWalkable => Walkable;
            protected override void SetNativeSourceStep(double seconds) { }
            public Matrix4x4? AnchorWorld;
            protected override bool TryGetTopLevelSceneGeometryWorld(int key, out Matrix4x4 world)
            {
                world = AnchorWorld ?? Matrix4x4.identity;
                return AnchorWorld.HasValue;
            }
            public readonly List<(long element, long parent)> Reparents = new List<(long element, long parent)>();
            protected override bool ReparentSpatialHierarchyElement(long space, long element, long parent, Matrix4x4 local)
            {
                CallOrder.Add("reparent:" + element + ">" + parent);
                Reparents.Add((element, parent));
                return true;
            }
            public readonly Dictionary<long, SbEntity> AuthoredEntities = new Dictionary<long, SbEntity>();
            protected override SbEntity FindAuthoredEntity(long element) =>
                AuthoredEntities.TryGetValue(element, out SbEntity entity) ? entity : null;
            public readonly List<(long element, Matrix4x4 local)> CheckedTransforms = new List<(long element, Matrix4x4 local)>();
            protected override bool TrySetSpatialHierarchyTransform(long space, long element, Matrix4x4 local)
            {
                CheckedTransforms.Add((element, local));
                Transforms[element] = local;
                return true;
            }
            public void SetRegisteredWalkable(WalkableRegion walkable) =>
                typeof(SpatialBlendManager).GetField("_registeredWalkable", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(this, walkable);
            public bool AuthoringReady => (bool)typeof(SpatialBlendManager).GetProperty("AuthoringInputsReady",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
            // Scene-geometry seams: the adapter's elements without a library.
            public readonly List<long> RemovedLocal = new List<long>();
            public bool RefuseLocalRemoval;
            protected override long EnsureLocalSceneGeometryRoom(long space) => 1;
            protected override long AddLocalSceneGeometryElement(long space, long parent) => _nextElement++;
            protected override void SetLocalSceneGeometryTransform(long space, long element, Matrix4x4 transform) { }
            protected override void SetLocalSceneGeometryBox(long space, long element, int role, Vector3 min, Vector3 max) { }
            protected override void SetLocalSceneGeometryPolygon(long space, long element, int role, IReadOnlyList<Vector2> points) { }
            protected override bool RemoveLocalSceneGeometryElement(long space, long element)
            {
                RemovedLocal.Add(element);
                return !RefuseLocalRemoval;
            }
            public int AnchorRegionCount => ((IDictionary)typeof(SpatialBlendManager).GetField("_anchorRegionByKey",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this)).Count;
            public void RetireSceneAnchor(long objectElement, int key, long region) =>
                typeof(SpatialBlendManager).GetMethod("RetireSceneAnchor", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(this, new object[] { ID, objectElement, key, region, null });
            public readonly List<Vector2[]> SiblingFootprints = new List<Vector2[]>();
            protected override void CollectSiblingFootprints(List<Vector2[]> into, long excludeObjectElement)
            {
                base.CollectSiblingFootprints(into, excludeObjectElement);
                into.AddRange(SiblingFootprints);
            }
            public long SourceLeaf;
            public bool SourceAnswers = true;
            protected override bool TryReadSourceTransfer(long actor, out long leafSharedId)
            {
                leafSharedId = SourceLeaf;
                return SourceAnswers;
            }
            internal IEmbodiedCore Core;
            public readonly List<long> Declared = new List<long>();
            public readonly List<uint> ExceptionIds = new List<uint>();
            public readonly List<byte[]> ExceptionPayloads = new List<byte[]>();
            public int Pushes;
            public readonly List<(long actor, float yaw, float? pitch)> StateUpdates = new List<(long actor, float yaw, float? pitch)>();

            protected override void UpdateNativeActorState(long actor, Vector3 position, float yawDegrees, float? headPitchDegrees) =>
                StateUpdates.Add((actor, yawDegrees, headPitchDegrees));

            protected override bool RaiseNativeSourceException(long actor, uint exceptionId, byte[] payload)
            {
                Declared.Add(actor);
                ExceptionIds.Add(exceptionId);
                ExceptionPayloads.Add(payload);
                return true;
            }

            protected override void PushSourceState(SbEntity entity) => ++Pushes;

            public void TickDiscontinuities() =>
                typeof(SpatialBlendManager).GetMethod("TickLocalDiscontinuities", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(this, null);
            public readonly Dictionary<long, Vector2[]> Boundaries = new Dictionary<long, Vector2[]>();
            public readonly Dictionary<long, Vector2[]> LocalBoundaries = new Dictionary<long, Vector2[]>();
            public readonly Dictionary<long, bool> Flags = new Dictionary<long, bool>();
            public readonly Dictionary<long, Matrix4x4> Transforms = new Dictionary<long, Matrix4x4>();
            public readonly Dictionary<long, long> SharedIds = new Dictionary<long, long>();
            private readonly Dictionary<long, long> _bySharedId = new Dictionary<long, long>();
            private long _nextElement = 10;

            public void Seed(long sharedId, long element) => _bySharedId[sharedId] = element;

            // Runs the private target handler exactly as the native callback does, then drains the queue.
            internal void Realize(long actor, IReadOnlyList<RealizationTarget> targets)
            {
                foreach (RealizationTarget target in targets)
                {
                    if (target.AnchorName != "root") RememberReferentOnEntities(target.ReferentId);
                }
                typeof(SpatialBlendManager).GetMethod("OnRealizationTargets",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this,
                    new object[] { ID, actor, targets, ConnectionGeneration });
                Update();
            }

            private void RememberReferentOnEntities(long referent)
            {
                foreach (RecordingEntity entity in FindObjectsByType<RecordingEntity>(FindObjectsInactive.Include,
                             FindObjectsSortMode.None))
                {
                    entity.RememberReferent(referent);
                }
            }

            internal override IEmbodiedCore CreateEmbodiedCore(long space) => Core;

            public bool IsDriven(long actor) =>
                ((IDictionary)typeof(SpatialBlendManager).GetField("_v2EmbodiedDrivers",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this)).Contains(actor);

            protected override void PollNativeRealizationTargets(long actor) { }

            protected override void InitializeNativeLibrary() { }
            protected override long CreateNativeSession() => ++_next;
            protected override long AddNativeSpace(long session) => ++_next;
            protected override void RegisterTransportCallbacks(long space, long generation) => Order.Add("transport");
            protected override void NegotiateContainmentFeature()
            {
                Order.Add("containment");
                base.NegotiateContainmentFeature();
            }
            protected override bool ProbeContainmentFeature(out string error)
            {
                error = FeatureAvailable ? string.Empty : "fake library without the header";
                return FeatureAvailable;
            }
            protected override long HostNativeSpace(long space, int port) => -1;
            protected override bool WaitForNativeConnection(long handle, out string error)
            {
                error = string.Empty;
                return true;
            }
            protected override void RetireContainmentState()
            {
                base.RetireContainmentState();
                Order.Add("retire");
            }
            protected override void ReleaseNativeCallbacks(long space) => Order.Add("release");
            protected override void DestroyNativeSession(long session) => Order.Add("destroy");

            // Authoring seams.
            protected override long EnsureSpatialHierarchyRoom(long space) => 1;
            protected override long AddSpatialHierarchyElement(long space, long parent) => _nextElement++;
            protected override void SetSpatialHierarchyTransform(long space, long element, Matrix4x4 transform) =>
                Transforms[element] = transform;
            protected override void SetSpatialHierarchySupport(long space, long element, Vector3[] vertices) { }
            protected override void SetSpatialHierarchyTag(long space, long element, string tag) { }
            protected override bool SetSpatialHierarchyBoundary(long space, long element, Vector2[] polygon)
            {
                if (RefuseRegionPolygon) return false;
                Boundaries[element] = (Vector2[])polygon.Clone();
                return true;
            }
            protected override bool SetSpatialHierarchyLocalBoundary(long space, long element, Vector2[] polygon)
            {
                if (RefuseRegionPolygon) return false;
                LocalBoundaries[element] = (Vector2[])polygon.Clone();
                CallOrder.Add("local:" + element);
                return true;
            }
            protected override string SpatialHierarchySceneError(long space) =>
                RefuseRegionPolygon ? "containment: element 11 ('ChessArea') sticks out of its parent element 1 ('room')" : null;
            protected override bool SetSpatialHierarchyContainmentTransfer(long space, long element, bool enabled)
            {
                CallOrder.Add("flag:" + element);
                if (RefuseFlag) return false;
                Flags[element] = enabled;
                return true;
            }
            protected override void SetSpatialHierarchyFootprintCollider(long space, long element, Collider collider) { }
            protected override void ResolveSpatialHierarchyElement(long space, long received, long resolved) { }
            protected override void AddSpatialHierarchySubRegion(long space, long element, string name, string tag,
                                                                 Vector2[] polygon) { }
            protected override void SetSpatialHierarchySharedId(long space, long element, long sharedId)
            {
                _bySharedId[sharedId] = element;
                SharedIds[element] = sharedId;
            }
            protected override long FindSpatialHierarchyElement(long space, long sharedId) =>
                _bySharedId.TryGetValue(sharedId, out long element) ? element : 0;
            protected override bool RemoveSpatialHierarchyElement(long space, long element) => true;
            protected override void ShareSpatialIdentity(long space, long element) { }
        }
    }
}
#endif
