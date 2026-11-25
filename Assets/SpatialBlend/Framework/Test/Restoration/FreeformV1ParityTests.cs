// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using SpatialBlend.Interactions;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// One EditMode check per freeform v1 parity row. Unfinished rows stay
    /// [Explicit] until their owning task lands. Out-of-scope rows stay [Ignore].
    /// Future types are looked up by name so this file compiles before they exist.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformV1ParityTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private const string AppPath =
            "Assets/SpatialBlend/Samples/Scripts/Freeform Collaboration/FreeformCollaborationApp.cs";
        private const BindingFlags AnyMember =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // v1: ControlPanel Connect calls FusionBootstrap.StartSharedClient on the fixed
        //     session MultiplayerSession (v1 Assets/SpatialBlend/Samples/Scripts/UI/ControlPanel.cs).
        // v2: FusionSessionBrowser starts that same Shared client after a lobby listing.
        // property: the Freeform scene has a Fusion bootstrap, and the browser starts a Shared session.
        // difference: the product join path is the room list (row M1), while the bootstrap asset
        //     still stores DefaultRoomName MultiplayerSession until a room is chosen.
        [Test]
        public void N1_SharedFusionSessionCanBeStarted()
        {
            string browser = File.ReadAllText("Assets/SpatialBlend/Samples/Scripts/FusionSessionBrowser.cs");
            Assert.That(browser, Does.Contain("SessionLobby.Shared"));
            Assert.That(browser, Does.Contain("StartSharedClient()"));
            Assert.That(typeof(FusionSessionBrowser).GetMethod("OpenLobbyAsync"), Is.Not.Null);

            using (var scene = FreeformScene.Open())
            {
                Assert.That(scene.Count<FusionBootstrap>(), Is.EqualTo(1));
                Assert.That(scene.Count<FusionConnectionManager>(), Is.EqualTo(1));
            }
        }

        // v1: the master hosts, publishes its address with RpcConnectToHost, and clients Connect
        //     (v1 Assets/SpatialBlend/Samples/Scripts/FusionConnectionManager.cs). The settings
        //     default port is 45000 (v1 Assets/SpatialBlend/Framework/Scripts/SpatialBlendSettings.cs).
        // v2: FusionConnectionManager hosts through SpatialBlendManager with no listening socket and
        //     announces the round with RpcJoinRoundEpoch (no address); the core link's bytes ride Fusion
        //     reliable data between the two peers.
        // property: the settings name no host or port, and the connection manager exposes the round RPC
        //     and the tunneled join request.
        // difference: v1 published an address (api.ipify.org) and every client dialed it. v2 advertises and
        //     dials nothing; hosting and joining work across NAT. HostAddressPolicy and the address UI are
        //     gone. Constants.DEFAULT_PORT (49152) is only the scene manager's field default.
        [Test]
        public void N2_CoreLinkIsCarriedByFusionRpc()
        {
            Assert.That(typeof(SpatialBlendSettings).GetField("defaultPort"), Is.Null, "the settings name no port");
            Assert.That(typeof(SpatialBlendSettings).GetField("defaultHostIp"), Is.Null, "the settings name no host");

            // Behaviour and surface, not source text: the round announcement carries a round number and
            // nothing else (no address, no port); the manager hosts and joins through the tunneled seams and has no member
            // that connects to an address or resolves one.
            MethodInfo announce = typeof(FusionConnectionManager).GetMethod("RpcJoinRoundEpoch", AnyMember);
            Assert.That(announce, Is.Not.Null);
            Assert.That(announce.GetParameters().Select(p => p.ParameterType.Name),
                Is.EqualTo(new[] { "Int64", "RpcInfo" }), "the announcement is (epoch, RpcInfo): it carries no address");
            Assert.That(typeof(FusionConnectionManager).GetMethod("RequestJoinTunneled", AnyMember), Is.Not.Null);
            Assert.That(typeof(FusionConnectionManager).GetMethod("RequestHostTunneled", AnyMember), Is.Not.Null);
            foreach (MethodInfo method in typeof(FusionConnectionManager).GetMethods(AnyMember | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.Name, Does.Not.Contain("ResolveHostIp").And.Not.Contain("RequestConnect"), method.Name);
                Assert.That(method.GetParameters().Select(p => p.Name.ToLowerInvariant()),
                    Does.Not.Contain("hostaddress").And.Not.Contain("ipaddress").And.Not.Contain("port"), method.Name);
            }
            // The hosting and joining behaviour itself (no listening socket, one link per player, the round announced
            // after the link exists) is FusionConnectionContractTests, which drives the real manager.

            using (var scene = FreeformScene.Open())
            {
                SpatialBlendManager manager = scene.Components<SpatialBlendManager>().Single();
                Assert.That(new SerializedObject(manager).FindProperty("_port").intValue, Is.EqualTo(8888));
            }
        }

        // v1: a Photon Voice speaker feeds LipSyncSpeaker, and RemoteAvatarVoiceManager matches
        //     it through FreeformCollaborationApp.OnRemoteAvatarAdded
        //     (v1 Assets/SpatialBlend/Avatar/Scripts/RemoteAvatarVoiceManager.cs).
        // v2: the same matcher and speaker types remain on the Freeform scene and voice prefab.
        // property: the scene has one voice matcher, the app raises OnRemoteAvatarAdded, and
        //     the voice prefab carries a LipSyncSpeaker.
        // difference: none.
        [Test]
        public void N3_VoiceSpeakerMatchesTheRemoteAvatar()
        {
            Type matcher = RequireType("SpatialBlend.Avatar.RemoteAvatarVoiceManager");
            Type speaker = RequireType("SpatialBlend.Avatar.LipSyncSpeaker");
            Assert.That(matcher.GetMethod("RegisterRemoteSpeaker"), Is.Not.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetEvent("OnRemoteAvatarAdded"), Is.Not.Null);

            using (var scene = FreeformScene.Open())
            {
                Assert.That(scene.Count(matcher), Is.EqualTo(1));
            }

            var voicePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Avatar/Prefabs/VoicePrefab.prefab");
            Assert.That(voicePrefab, Is.Not.Null);
            Assert.That(voicePrefab.GetComponentsInChildren<Component>(true)
                .Any(component => component != null && component.GetType() == speaker), Is.True);
        }

        // v1: ControlPanel exposes UsePresetAvatar and SelectedAvatarPreset from
        //     _usePresetAvatarToggle and _avatarPresetSlider
        //     (v1 Assets/SpatialBlend/Samples/Scripts/UI/ControlPanel.cs).
        // v2: SbLocalPresetAvatarFactory already builds a local preset. T2.4 restores the
        //     selection as the generated ControlPanel's Avatars tab, a preset toggle and slider
        //     driven by AvatarSettingsPresenter.
        // property: ControlPanelView has the preset toggle and slider, and ControlPanel keeps the
        //     two public selectors.
        // difference: the legacy _usePresetAvatarToggle and _avatarPresetSlider prefab fields are
        //     replaced by the generated rows. The factory existing does not satisfy the UI.
        [Test]
        public void A1_LocalPresetAvatarUiIsRestored()
        {
            Assert.That(FindType("SpatialBlend.SbLocalPresetAvatarFactory"), Is.Not.Null);
            Type view = typeof(ControlPanelView);
            Assert.That(view.GetProperty("UsePreset"), Is.Not.Null);
            Assert.That(view.GetProperty("Preset"), Is.Not.Null);
            Assert.That(typeof(AvatarSettingsPresenter).GetProperty("UsePreset"), Is.Not.Null);
            Assert.That(typeof(AvatarSettingsPresenter).GetProperty("PresetIndex"), Is.Not.Null);
            Type panel = typeof(ControlPanel);
            Assert.That(panel.GetProperty("UsePresetAvatar"), Is.Not.Null);
            Assert.That(panel.GetProperty("SelectedAvatarPreset"), Is.Not.Null);
        }

        // v1: SbNetworkAvatarEntity interpolates a received avatar toward its target at the caps the
        //     prefab ships, 2 m/s and 150 deg/s (the code defaults, 1 m/s and 30 deg/s, are overridden by
        //     MetaAvatarPrefab in v1).
        // v2: the embodied driver (design A*) owns the received root. Every frame it publishes the realized
        //     pose, reads the core's proposal and moves the body toward it within one declared
        //     EmbodiedDriverConfig, defaulting to the prefab's 2 m/s and 150 deg/s. The core holds no pose
        //     and paces nothing, and the avatar entity has no interpolator of its own.
        // property: the driver steps within the declared bounds (EmbodiedDriverTests), the avatar entity's
        //     caps default to that config, and no second integrator, core pacer or apply-avatar-state feedback
        //     exists on the receiver path.
        // difference: v1 interpolated twice (the core planner, then the app) and wrote the replica from
        //     two places. The driver is the single owner of the embodied pose.
        [Test]
        public void A2_RemoteSbAvatarIsMovedByTheEmbodiedDriverWithinOneDeclaredConfig()
        {
            Assert.That(typeof(SbRetargetAnchorTargetBridge), Is.Not.Null);
            Assert.That(SpatialBlend.Internal.EmbodiedDriverConfig.DefaultMaxLinearSpeedMetersPerSecond, Is.EqualTo(2f));
            Assert.That(SpatialBlend.Internal.EmbodiedDriverConfig.DefaultMaxAngularSpeedDegreesPerSecond, Is.EqualTo(150f));
            string manager = ManagerSources();
            Assert.That(manager, Does.Contain("EmbodiedDriver"));
            Assert.That(manager, Does.Contain("PublishPose"));
            Assert.That(manager, Does.Not.Contain("TryTickAvatar"));
            Assert.That(manager, Does.Not.Contain("ApplyAvatarState"));
            Assert.That(manager, Does.Not.Contain("SetAvatarController"));
            string entity = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs");
            Assert.That(entity, Does.Not.Contain("maxPositionVelocity"), "The bounds are declared once, in EmbodiedDriverConfig.");
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Avatar/Prefabs/MetaAvatarPrefab.prefab"),
                Does.Not.Contain("maxPositionVelocity"));
            Assert.That(entity, Does.Not.Contain("MoveTowards"), "The v1 interpolator is gone.");
            Assert.That(entity, Does.Not.Contain("_targetPosition"));
        }

        // v1: Euclidean2D OnInteraction Start/Terminate drives the avatar display switch
        //     (v1 Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs OnInteraction,
        //     and SbNetworkAvatarEntity.SetOnInteractionCallback).
        // v2: the core's interaction value of a received avatar: the TRANSFER flag of its root proposal AND that
        //     anchor's ARRIVED, read from the proposal set the driver reads every frame. Start is raised when the
        //     value turns true and Terminate when it turns false.
        // property: the manager folds the value through the pure ContainmentInteractionMapper. A receiver with no
        //     counterpart of the leaf has the flag clear, and a body that is still walking has not arrived, so
        //     neither is interaction. ABI 4.3 has no contained event: the value is the only source.
        // difference: containment replaces the v1 Euclidean2D distance threshold and the T1.3b
        //     AffordancePose near-field phase. The leaf edge is the only boundary, with no dwell, as v1 had
        //     none.
        [Test]
        public void A3_ContainedStateRaisesInteractionStartAndTerminate()
        {
            string sources = ManagerSources() +
                File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbEntity.cs");
            Assert.That(sources, Does.Contain("ContainmentInteractionMapper"));
            Assert.That(sources, Does.Contain("OnInteraction"));
            Assert.That(sources, Does.Contain("ObserveTransferState"));
            Assert.That(sources, Does.Contain("TransferArrived"));
            Assert.That(sources, Does.Not.Contain("ApplyContainmentEvent"));
            Assert.That(sources, Does.Not.Contain("ContainmentEvent"));
            Assert.That(sources, Does.Not.Contain("ReadActorContainment"));
            Assert.That(File.Exists(
                "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.ContainmentTransfer.cs"), Is.True);

            var mapper = new ContainmentInteractionMapper();
            var transitions = new System.Collections.Generic.List<ContainmentInteractionTransition>();
            mapper.Observe(1, 2, true, transitions);
            mapper.Observe(1, 2, false, transitions);
            Assert.That(transitions.Select(t => t.Signal),
                Is.EqualTo(new[] { InteractionSignal.Start, InteractionSignal.Terminate }));
        }

        // v1: AvatarDisplayMode.Default shows the Sb avatar when idle and the Meta avatar
        //     when interacting. ZoneBased fades the Meta avatar over the configured duration
        //     and keeps the Sb avatar hidden. Spawned hides both for 3 seconds, and
        //     OnUserAvatarLoaded hides both for 5 seconds
        //     (v1 Assets/SpatialBlend/Framework/Types/AvatarDisplayMode.cs and
        //     SbAvatarBehaviourFusion.ApplyRemoteAvatarVisibility).
        // v2: RemoteAvatarVisibilityPolicy evaluates both modes. SbAvatarBehaviourFusion
        //     starts the 3 second spawn hide, then the 5 second user-avatar hide.
        // property: Default swaps Sb and Meta. ZoneBased hides Sb. Either hide window
        //     hides both avatars and does not start a fade.
        // difference: the two hide windows share one clock, so a later hide replaces the
        //     earlier coroutine. While a hide is running it wins over interaction.
        [Test]
        public void A4_DefaultAndZoneBasedDisplayModesExist()
        {
            Assert.That(Enum.GetNames(typeof(AvatarDisplayMode)),
                Is.SupersetOf(new[] { "Default", "ZoneBased" }));
            Assert.That(RemoteAvatarVisibilityPolicy.SpawnedHideSeconds, Is.EqualTo(3f));
            Assert.That(RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds, Is.EqualTo(5f));

            RemoteAvatarVisibilityDecision idle = Visibility(AvatarDisplayMode.Default, false, 3f, 3f);
            Assert.That(idle.SbVisible, Is.True);
            Assert.That(idle.MetaVisible, Is.False);
            RemoteAvatarVisibilityDecision talking = Visibility(AvatarDisplayMode.Default, true, 3f, 3f);
            Assert.That(talking.SbVisible, Is.False);
            Assert.That(talking.MetaVisible, Is.True);

            RemoteAvatarVisibilityDecision zone = Visibility(AvatarDisplayMode.ZoneBased, true, 5f, 5f);
            Assert.That(zone.SbVisible, Is.False);
            Assert.That(zone.MetaVisible, Is.True);

            RemoteAvatarVisibilityDecision spawnHide = Visibility(
                AvatarDisplayMode.Default, true, 1f, RemoteAvatarVisibilityPolicy.SpawnedHideSeconds, 1f);
            Assert.That(spawnHide.SbVisible, Is.False);
            Assert.That(spawnHide.MetaVisible, Is.False);
            Assert.That(spawnHide.StartFade, Is.False);

            // The 5 second window still hides at a time the 3 second window has already ended.
            RemoteAvatarVisibilityDecision loadedHide = Visibility(
                AvatarDisplayMode.ZoneBased, true, 4f,
                RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds, 1f);
            Assert.That(loadedHide.SbVisible, Is.False);
            Assert.That(loadedHide.MetaVisible, Is.False);
            Assert.That(loadedHide.StartFade, Is.False);

            string behaviour = File.ReadAllText(
                "Assets/SpatialBlend/Avatar/Scripts/SbAvatarBehaviourFusion.cs");
            Assert.That(behaviour, Does.Contain(
                "StartRemoteHide(RemoteAvatarVisibilityPolicy.SpawnedHideSeconds)"));
            Assert.That(behaviour, Does.Contain(
                "StartRemoteHide(RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds)"));
            Assert.That(behaviour, Does.Contain("StopCoroutine(_hideCoroutine)"));
        }

        // v1: controller prefabs parent to both avatars' wrist joints, using the offsets in
        //     SbAvatarBehaviourFusion, and wait for OnSkeletonLoadedCallback before the
        //     Sb controllers attach (v1 Assets/SpatialBlend/Avatar/Scripts/SbAvatarBehaviourFusion.cs,
        //     SetupSbControllers).
        // v2: T2.2 ports that attachment as RemoteAvatarControllerAttachment, bound by
        //     SbAvatarBehaviourFusion. Controllers follow the visibility policy from A4.
        // property: SbAvatarBehaviourFusion owns a RemoteAvatarControllerAttachment and binds it,
        //     and the Sb wrists attach from the skeleton-loaded callback.
        // difference: SetupSbControllers is replaced by RemoteAvatarControllerAttachment.Bind.
        [Test]
        public void A5_RemoteControllersAttachToAvatarWrists()
        {
            Type behaviour = RequireType("SpatialBlend.SbAvatarBehaviourFusion");
            Assert.That(behaviour.GetMethod("BindRemoteControllers", AnyMember), Is.Not.Null);
            Assert.That(behaviour.GetField("_controllers", AnyMember)?.FieldType,
                Is.EqualTo(typeof(RemoteAvatarControllerAttachment)));
            Assert.That(behaviour.GetField("leftControllerPrefab", AnyMember), Is.Not.Null);
            Assert.That(behaviour.GetField("rightControllerPrefab", AnyMember), Is.Not.Null);
            Assert.That(typeof(RemoteAvatarControllerAttachment).GetMethod("Bind"), Is.Not.Null);
        }

        // v1: there is no full-body retarget on the Freeform avatars.
        // v2: SbMotion can drive a separate manual rig. Full-body SbMotion (T5.3) is left out.
        // property: full-body SbMotion is not a Freeform parity requirement.
        // difference: the optional demo is out of scope, so this row stays Ignored.
        [Test]
        [Ignore("Out of scope. Full-body SbMotion (T5.3) is outside parity.")]
        public void A6_FullBodyOsMotion_IsOutOfParityScope()
        {
            Assert.Fail("A6 stays Ignored. Full-body SbMotion is not a parity row.");
        }

        // v1: Chess.prefab syncs 32 piece poses through a NetworkArray of capacity 32,
        //     driven by ISDK grab, with no game rules
        //     (v1 Assets/SpatialBlend/Samples/Objects/Chessboard/Scripts/ChessBoardHandlerMotif.cs).
        // v2: in Fusion mode the Fusion motif is the only writer and the local backend never
        //     binds. Foundation Local suppresses it and writes the local chess model instead
        //     (T3.5: FreeformContentWriterPolicy, the gate's PermitsLocalContentWriter, and the
        //     app's EnforceSingleContentWriter).
        // property: the prefab has SbNetworkEntity, ChessBoardHandlerMotif, and 32 pieces,
        //     and the local backend can suppress that motif. The mode by writer table allows
        //     exactly one writer (FreeformContentWriterContractTests).
        // difference: a run has exactly one writer. v1 had only the Fusion writer.
        [Test]
        public void O1_ChessHasOneWriterPerRun()
        {
            AssertLegacyContent(
                "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab",
                "ChessBoardHandlerMotif",
                "FreeformSceneChessDefinition",
                32);
        }

        // v1: WhiteboardManagerMotif sends strokes with RPC_DrawLine, and a late joiner
        //     receives the snapshot (v1
        //     Assets/SpatialBlend/Samples/Objects/Whiteboard/Scripts/WhiteboardManagerMotif.cs).
        // v2: in Fusion mode the Fusion motif is the only writer and the local backend never
        //     binds. Foundation Local suppresses it (T3.5).
        // property: the prefab carries WhiteboardManagerMotif and RPC_DrawLine, and the
        //     local backend can suppress that motif. The mode by writer table allows exactly
        //     one writer.
        // difference: a run has exactly one writer. v1 had only the Fusion writer.
        [Test]
        public void O2_WhiteboardHasOneWriterPerRun()
        {
            AssertLegacyContent(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab",
                "WhiteboardManagerMotif",
                "FreeformSceneWhiteboardDefinition",
                0);
            Type motif = RequireType("SpatialBlend.Samples.Objects.Whiteboard.WhiteboardManagerMotif");
            Assert.That(motif.GetMethod("RPC_DrawLine"), Is.Not.Null);
        }

        // v1: SbNativeEntity shares GLB bytes through user data once the content is ready,
        //     and the app registers each entity once
        //     (v1 FreeformCollaborationApp.RegisterNativeEntities).
        // v2: NativeEntityReadyToShareTracker decides Skip, Defer, or Share once per entity.
        //     The app watches ContentReady, and RegisterReadyNativeEntity skips an entity that is
        //     already attached. GltfLoader registers only after SbApp.IsAppStarted. The bytes
        //     travel in the content serialization block and the receiver instantiates them.
        // property: empty content is deferred, and an attached entity is not registered again.
        // difference: the share goes through the v2 element API, and every participant shares
        //     its pre-existing content (v1 5995433). v1's receiver-side ready gate has no v2
        //     counterpart because only a source writes state.
        [Test]
        public void O3_NativeContentIsSharedOnceWhenReady()
        {
            Assert.That(typeof(SbNativeEntity).GetEvent("ContentReady"), Is.Not.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetMethod(
                "RegisterNativeEntities", AnyMember), Is.Not.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetMethod(
                "RegisterReadyNativeEntity", AnyMember), Is.Not.Null);
            Assert.That(FindType("SpatialBlend.Samples.Objects.GltfLoader"), Is.Not.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetMethod("WatchNativeContent", AnyMember), Is.Not.Null);
            // The decision the app takes for each entity, as behaviour: an attached entity is not registered again,
            // empty content is deferred, and a ready one is shared exactly once (as v1).
            var tracker = new NativeEntityReadyToShareTracker<SbNativeEntity>();
            var entityObject = new GameObject("o3-native-entity");
            try
            {
                var entity = entityObject.AddComponent<SbNativeEntity>();
                Assert.That(tracker.Evaluate(entity, new NativeEntityShareState(false, true, true, true)), Is.EqualTo(NativeShareDecision.Skip),
                    "an attached entity is not registered again");
                Assert.That(tracker.Evaluate(entity, new NativeEntityShareState(false, true, false, false)), Is.EqualTo(NativeShareDecision.Defer),
                    "empty content is deferred, never shared as a name-only placeholder");
                Assert.That(tracker.OnContentReady(entity, new NativeEntityShareState(false, true, false, true), true), Is.EqualTo(NativeShareDecision.Share));
                Assert.That(tracker.OnContentReady(entity, new NativeEntityShareState(false, true, false, true), true), Is.EqualTo(NativeShareDecision.Skip),
                    "shared exactly once");
            }
            finally { UnityEngine.Object.DestroyImmediate(entityObject); }
            Assert.That(typeof(SbApp).GetProperty("IsAppStarted"), Is.Not.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetProperty("IsAppStarted").DeclaringType,
                Is.EqualTo(typeof(FreeformCollaborationApp)));
            Assert.That(FindType("SpatialBlend.Interactions.GltfObjectControlPanelMotif"), Is.Not.Null,
                "The v1 GLB control panel is ported.");
            Assert.That(FindType("SpatialBlend.Interactions.UI.GltfObjectControlPopUp"), Is.Not.Null);
        }

        // v1: the host shares the biggest MRUK TABLE anchor as SbMRUKAnchorEntity. The
        //     receiver LinkToExisting on the local anchor with the same label, or rejects
        //     (v1 FreeformCollaborationApp.cs). v1 never registered a polygon for the
        //     TABLE with the core (v1 SpatialBlendManager.cs).
        // v2: the same label link. The entity is attached to the element the MRUK scene-geometry
        //     adapter already created, so there is one element per anchor. The core records the
        //     host-to-local element link when LinkToExisting resolves (core space.cc), and the
        //     receiver decodes through that link. The app registers no owned
        //     correspondence: the owned API needs both spaces in the session's context and a free
        //     link slot (owned_correspondence_c.inc, element_linker.cc), so it is
        //     refused after a resolve.
        // property: SbMRUKAnchorEntity exists, links by label, binds the adapter's elements, and
        //     registers no owned correspondence.
        // difference: DECLARED UPGRADE. v1 did not map the table at all; v2 carries a position
        //     on one table to the same normalized chart position on the other.
        [Test]
        public void O4_TableAnchorLinksToTheAdapterElementWithoutOwnedRegistration()
        {
            Assert.That(FindType("SpatialBlend.SbMRUKAnchorEntity"), Is.Not.Null);
            // Surface, not source text. The label link answers LinkToExisting onto an element the adapter made;
            // the app and the manager have no owned-correspondence registration.
            Assert.That(typeof(OnShareResponse).GetMethod("LinkToExisting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(typeof(SbMRUKAnchorEntity).GetMethod("SetSceneGeometryKey", AnyMember), Is.Not.Null);
            Assert.That(typeof(SpatialBlendManager).GetMethod("TryGetLocalSceneGeometryElements", AnyMember), Is.Not.Null);
            Assert.That(typeof(SpatialBlendAPI).GetMethod("ResolveSharedElement", AnyMember), Is.Not.Null,
                "the core records the link when LinkToExisting resolves");
            foreach (Type type in new[] { typeof(FreeformCollaborationApp), typeof(SpatialBlendManager) })
            {
                foreach (MethodInfo method in type.GetMethods(AnyMember))
                {
                    Assert.That(method.Name, Does.Not.Contain("TryRegisterOwned").And.Not.Contain("TryRegisterLinkedTableCorrespondence"),
                        type.Name + "." + method.Name);
                }
            }
        }

        // v1: RegisterWalkable submits the NavMesh triangulation, so obstacle holes stay
        //     in the mesh (v1 SpatialBlendManager.RegisterWalkable, RegisterMeshGeometry).
        // v2: NavMeshWalkableExtractor keeps the outer ring and each opposite-winding hole.
        //     The NavMesh is not the room; RegisterWalkableMesh registers its outer ring only.
        // property: a square with one inner ring yields one hole, and RegisterWalkableMesh
        //     registers no obstacle for it (the room's obstacles are its walls and furniture).
        [Test]
        public void G1_WalkableBoundaryKeepsNavMeshHoles()
        {
            Assert.That(NavMeshWalkableExtractor.TryExtract(
                WalkableHoleVertices(), WalkableHoleIndices(), out WalkableRegion region), Is.True);
            Assert.That(region.HasBoundary, Is.True);
            Assert.That(region.Boundary[0], Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(region.Holes, Has.Length.EqualTo(1));
            Assert.That(region.Holes[0], Is.EqualTo(new[]
            {
                new Vector2(1f, 1f), new Vector2(1f, 3f), new Vector2(3f, 3f), new Vector2(3f, 1f),
            }));

            var go = new GameObject("parity-walkable-hole");
            var manager = go.AddComponent<RecordingWalkableManager>();
            try
            {
                Assert.That(manager.RegisterWalkableMesh(WalkableHoleVertices(), WalkableHoleIndices()), Is.True);
                Assert.That(manager.Obstacles, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // v1: InteractionModeManager offers SceneEdit and Interaction. Selection uses the
        //     ISDK ray on the right controller
        //     (v1 Assets/SpatialBlend/Interaction/Scripts/Core/InteractionModeManager.cs).
        //     Placement is PanelPlacementMotif or ObjectPlacementMotif.
        // v2: the same mode manager, ray, and placement motifs stay in the Freeform scene.
        // property: the scene has one InteractionModeManager whose ray interactor is assigned,
        //     and both placement motif types exist.
        // difference: none. Users still walk physically.
        [Test]
        public void G2_PlacementModesAndIsdkRayStayInTheScene()
        {
            Assert.That(Enum.GetNames(typeof(SpatialBlend.Interactions.InteractionMode)),
                Is.SupersetOf(new[] { "SceneEdit", "Interaction" }));
            Assert.That(FindType("SpatialBlend.Interactions.PanelPlacementMotif"), Is.Not.Null);
            Assert.That(FindType("SpatialBlend.Interactions.ObjectPlacementMotif"), Is.Not.Null);

            using (var scene = FreeformScene.Open())
            {
                InteractionModeManager manager = scene.Components<InteractionModeManager>().Single();
                Assert.That(new SerializedObject(manager).FindProperty("_interactor").objectReferenceValue,
                    Is.InstanceOf<Oculus.Interaction.RayInteractor>());
            }
        }

        // v1: ControlPanel has Connect, Rebuild NavMesh, anchor visibility, manipulation,
        //     the library indicator, and passthrough brightness
        //     (v1 Assets/SpatialBlend/Samples/Scripts/UI/ControlPanel.cs).
        // v2: those controls stay on ControlPanel.prefab. Connect opens the room list
        //     before starting Fusion. The connect label is the status line.
        // property: the prefab assigns each of those controls, and ControlPanel can open
        //     the session list.
        // difference: preset, zone, and fade controls are rows A1 and A4 (T2.4). The
        //     advertised host address on the panel is T4.2.
        [Test]
        public void U1_ControlPanelKeepsTheSharedSessionControls()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab");
            ControlPanel panel = prefab.GetComponentInChildren<ControlPanel>(true);
            var serialized = new SerializedObject(panel);
            foreach (string name in new[]
            {
                "_connect", "_rebuildNavMesh", "_sceneAnchorVisible", "_enableManipulationToggle",
                "_libraryLoaded", "_passthroughBrightnessSlider", "_connectLabel",
            })
            {
                Assert.That(serialized.FindProperty(name).objectReferenceValue, Is.Not.Null, name);
            }

            Assert.That(typeof(ControlPanel).GetMethod("TryShowSessionList", AnyMember), Is.Not.Null);
        }

        // v1: SbPermissionManager requests CAMERA, HEADSET_CAMERA, storage read/write,
        //     RECORD_AUDIO, and USE_SCENE before the scene loads
        //     (v1 Assets/SpatialBlend/Framework/Scripts/SbPermissionManager.cs).
        // v2: SbPermissionManager.RequestedPermissions is that list, and AutoInitialize
        //     still runs before the scene loads on Android.
        // property: RequestedPermissions equals the six v1 permissions, in order.
        // difference: none.
        [Test]
        public void U2_PermissionManagerMatchesTheV1List()
        {
            Assert.That(SbPermissionManager.RequestedPermissions, Is.EqualTo(new[]
            {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA",
                "android.permission.READ_EXTERNAL_STORAGE",
                "android.permission.WRITE_EXTERNAL_STORAGE",
                "android.permission.RECORD_AUDIO",
                "com.oculus.permission.USE_SCENE",
            }));
            Assert.That(typeof(SbPermissionManager).GetMethod("AutoInitialize", AnyMember), Is.Not.Null);
        }

        // v1: SessionManager runs SAM3 segmentation and SAM3D reconstruction through spatialblend_session,
        //     one handler per pipeline, against the lab server. GltfLoader.ConfigureAffordance turned the
        //     scan box into an AffordanceVolume
        //     (v1 Assets/SpatialBlend/Framework/Scripts/SessionManager.cs,
        //     Framework/Internal/SessionNativeAPI.cs, Samples/Inference/Scripts/*SessionHandler.cs).
        // v2: ScanService (Freeform/Scanning) drives the pipelines behind IScanSessionTransport.
        //     NativeScanSessionTransport is the only caller of spatialblend_session, a library separate
        //     from libspatialblend. The server address comes from SpatialBlendSettings. A reconstruction
        //     reaches an IScanResultSink with the box as a ScanRegionHint, not an AffordanceVolume.
        // property: the service, the transport seam and the sink exist, SessionLibrary loads only
        //     spatialblend_session, the default settings name the public lab endpoint over TLS, and
        //     GltfLoader no longer references AffordanceVolume.
        // difference: no AffordanceVolume is built. The hint becomes an SbSpatialAuthoring declaration
        //     (row S2). Requests also time out (v1 waited forever), and the panel stays open after a
        //     failed send.
        [Test]
        public void S1_ScanSessionsRunBehindTheSessionSeam()
        {
            Assert.That(FindType("SpatialBlend.SessionManager"), Is.Not.Null);
            Assert.That(FindType("SpatialBlend.Freeform.Scanning.ScanService"), Is.Not.Null);
            Assert.That(FindType("SpatialBlend.Freeform.Scanning.IScanSessionTransport"), Is.Not.Null);
            Assert.That(FindType("SpatialBlend.Freeform.Scanning.IScanResultSink"), Is.Not.Null);

            string native = File.ReadAllText("Assets/SpatialBlend/Framework/Internal/SessionLibrary.cs");
            Assert.That(native, Does.Contain("LIB_NAME = \"spatialblend_session\""));
            Assert.That(native, Does.Not.Contain("DllImport(\"spatialblend\""));

            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(settings.sessionServerAddress, Is.EqualTo(SpatialBlendSettings.DefaultSessionServerAddress));
                Assert.That(settings.sessionUseTls, Is.True);
                Assert.That(settings.sessionFallbackServerAddress, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }

            Assert.That(File.ReadAllText("Assets/SpatialBlend/Samples/Objects/Scripts/GltfLoader.cs"),
                Does.Not.Contain("AffordanceVolume"));
        }

        // v1: box annotation created the AffordanceVolume that scoped the scanned object
        //     (v1 GltfLoader.ConfigureAffordance, BoxAnnotationManager).
        // v2: the sink binds the loaded GLB to the box's SbSpatialAuthoring (T3.0). The Containment
        //     Region is the box footprint extent, the footprint is the box base, and the
        //     box top is a tagged surface. The declaration is written before the object is registered and
        //     travels with the shared content, so the receiver writes the same authoring.
        // property: an SbSpatialAuthoring type exists, the scan sink path binds it, and the scan result
        //     compiles to a declared region on the loaded object (BoxAnnotationAuthoringContractTests).
        // difference: the region has no 0.15 m margin. The box extent is the definition, and v1 added the
        //     margin to a volume that was not one. A scan without a box keeps the content prefab's authoring.
        [Test]
        public void S2_ScanBoxBecomesAnExplicitRegion()
        {
            Assert.That(FindTypeBySimpleName("SbSpatialAuthoring"), Is.Not.Null);
            Assert.That(FindTypeBySimpleName("BoxAnnotationAuthoring"), Is.Not.Null);
            string sink = File.ReadAllText("Assets/SpatialBlend/Samples/Scanning/Scripts/GltfScanResultSink.cs");
            Assert.That(sink, Does.Contain("SbSpatialAuthoring"));
            Assert.That(sink, Does.Contain("BoxAnnotationAuthoring.Declare"));
            Assert.That(sink, Does.Not.Contain("TODO(T5.2)"));

            var hint = new SpatialBlend.Freeform.Scanning.ScanRegionHint(new Vector3(0f, 0.3f, 0f), new Vector3(1f, 0.6f, 0.4f));
            var loaded = new GameObject("S2-scan-object");
            try
            {
                var result = new SpatialBlend.Freeform.Scanning.ScanReconstructionResult
                {
                    ObjectName = "S2-scan-object", OutputPrefix = "scan/S2", HasRegionHint = true, RegionHint = hint,
                };
                SbSpatialAuthoring authoring = SpatialBlend.Samples.Scanning.GltfScanResultSink.Bind(result, loaded);
                Assert.That(authoring, Is.Not.Null);
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                    Is.True, error);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                    Is.True, error);
                Assert.That(plan.ExtentIsDeclared, Is.True);
                Assert.That(plan.AppliedMarginMeters, Is.EqualTo(0f));
            }
            finally { UnityEngine.Object.DestroyImmediate(loaded); }
        }

        // v1: the Quest player is Android arm64 and loads libspatialblend
        //     (v1 Assets/Plugins and SpatialBlendLibraryConfig).
        // v2: T1.4 publishes libspatialblend (sb_* C API), T1.5 drops the v1 core API and the
        //     v1 plugin, and T4.5 checks the APK. Il2CppBuildCheck is the EditMode build hook.
        // property: the library config names libspatialblend.so for Android, and no runtime
        //     script calls the v1 core API (spatialblend_* symbols, NativeAPI).
        // difference: none. The packaged APK itself is checked by tools/check_apk_contents.py (T4.5).
        [Test]
        public void P1_QuestBuildSelectsTheCorePlugin()
        {
            Assert.That(FindType("SpatialBlend.Editor.Il2CppBuildCheck"), Is.Not.Null);
            string config = File.ReadAllText(
                "Assets/SpatialBlend/Foundation/Internal/SpatialBlendLibraryConfig.cs");
            Assert.That(config, Does.Contain("libspatialblend.so"));
            Assert.That(config, Does.Not.Contain("\"Android/libspatialblend.so\""));
            Assert.That(config, Does.Not.Contain("\"Android/libspatialblend_d.so\""));
            var calls = RuntimeV1CoreApiCalls().ToArray();
            Assert.That(calls, Is.Empty, string.Join("\n", calls));
        }

        // v1: Windows standalone is a build target beside Android.
        // v2: the Windows build (T5.4) is left out.
        // property: Windows standalone is not a parity requirement.
        // difference: Quest Android is the completion bar.
        [Test]
        [Ignore("Out of scope. The Windows standalone build (T5.4) is left out.")]
        public void P2_WindowsStandalone_IsOutOfScope()
        {
            Assert.Fail("P2 stays Ignored. Windows standalone is not a parity row.");
        }

        // v1: the Editor opens the same Freeform scene. There is no separate desktop input path.
        // v2: Foundation Local is the checked-in editor path, and FreeformLocalApp is the
        //     desktop demo with no Photon or Meta dependency.
        // property: the enabled build scene's FreeformCollaborationApp is FoundationLocal,
        //     and FreeformLocalApp exists.
        // difference: T4.1 will make player builds use Fusion mode. The Editor default
        //     stays on FoundationLocal. Local simulator scenes (L1) are dropped.
        [Test]
        public void P3_EditorDesktopStaysOnFoundationLocal()
        {
            Assert.That(FindType("SpatialBlend.Freeform.FreeformLocalApp"), Is.Not.Null);
            EditorBuildSettingsScene buildScene = EditorBuildSettings.scenes
                .Single(scene => scene.path == ScenePath);
            Assert.That(buildScene.enabled, Is.True);

            using (var scene = FreeformScene.Open())
            {
                FreeformCollaborationApp app = scene.Components<FreeformCollaborationApp>().Single();
                Assert.That(app.RuntimeProfile, Is.EqualTo(FreeformCollaborationRuntimeProfile.FoundationLocal));
            }
        }

        // v1: Connect auto-joins the single fixed session MultiplayerSession
        //     (v1 ControlPanel.StartSharedClient and the scene DefaultRoomName).
        // v2: FusionSessionBrowser lists rooms. CreateRoom hosts a new named session and
        //     that player becomes the core host. JoinAsync joins an existing room as a
        //     core client. CloseLobby leaves the list.
        // property: list, host, join, and leave are on FusionSessionBrowser and SessionListPanel,
        //     and hosting waits until the lobby has delivered a list, including an empty one.
        // difference: the room list carries no core address and nothing needs one: the core link rides the
        //     Photon session (RpcJoinRoundEpoch announces the round). The bootstrap asset still stores
        //     MultiplayerSession until StartSession replaces DefaultRoomName.
        [Test]
        public void M1_MatchmakingListsHostsAndJoinsRooms()
        {
            Type browser = typeof(FusionSessionBrowser);
            Assert.That(browser.GetMethod("OpenLobbyAsync"), Is.Not.Null);
            Assert.That(browser.GetMethod("CreateRoom"), Is.Not.Null);
            Assert.That(browser.GetMethod("JoinAsync"), Is.Not.Null);
            Assert.That(browser.GetMethod("CloseLobby"), Is.Not.Null);
            Assert.That(typeof(SessionListPanel), Is.Not.Null);
            Assert.That(typeof(ControlPanel).GetMethod("TryShowSessionList", AnyMember), Is.Not.Null);

            string source = File.ReadAllText("Assets/SpatialBlend/Samples/Scripts/FusionSessionBrowser.cs");
            Assert.That(source, Does.Contain("HasSessionList"));
            Assert.That(source, Does.Contain("DefaultRoomName = sessionName"));
            Assert.That(source, Does.Contain("StartSharedClient()"));
            Assert.That(source, Does.Contain(
                "new FusionSessionEntry(s.Name, s.PlayerCount, s.MaxPlayers, s.IsOpen, s.IsVisible)"));
            Assert.That(typeof(FusionConnectionManager).GetMethod("RpcJoinRoundEpoch", AnyMember), Is.Not.Null);

            using (var scene = FreeformScene.Open())
            {
                FusionBootstrap bootstrap = scene.Components<FusionBootstrap>().Single();
                Assert.That(bootstrap.DefaultRoomName, Is.EqualTo("MultiplayerSession"));
            }
        }

        // v1: two Quests join a room, link the core, and exercise the product.
        // v2: T4.6 is the two-headset device checklist.
        // property: the device session is not an EditMode test.
        // difference: every other row is self-verified. This one stays Ignored and is run by hand.
        [Test]
        [Ignore("Run by hand. T4.6 is the two-headset checklist and is outside modular verification.")]
        public void T46_DeviceAcceptance_IsOwnerRun()
        {
            Assert.Fail("T4.6 stays Ignored. The device session is run by hand.");
        }

        // v1: Euclidean2D Start/Terminate when the root enters or leaves the volume
        //     (v1 proximity/euclidean_proximity.cc, threshold 0.999).
        // v2: Default-mode interacting is containment: the actor's root is inside the declared
        //     Containment Region of a shared object, which is then its scope. The app holds
        //     no hysteresis and no binding of its own.
        // property: one Start per entry and one Terminate per exit, on the read that changes the value. The
        //     native Start and Terminate are the G-N case
        //     TransferFlagRaisesStartThenTerminateWhenTheClientAvatarEntersAndLeavesTheObjectRegion.
        //     This row checks the managed wiring.
        // difference: v1 had no hysteresis and neither does containment (the core has no band any more, and the
        //     flag is recomputed on every update). The T1.3b AffordancePose phase (1.5 m enter, 2.0 m
        //     release, 0.3 s dwell) is gone, and so is the AffordancePose binding on the root.
        [Test]
        public void AV1_InteractionFollowsEngagementRegion()
        {
            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.ContainmentTransfer.cs");
            Assert.That(manager, Does.Contain("ContainmentInteractionMapper"));
            Assert.That(manager, Does.Contain("IsTransferInteracting"));
            Assert.That(manager, Does.Not.Contain("TryRegisterContainmentCallback"));
            Assert.That(manager, Does.Not.Contain("BindScoped"));
            Assert.That(manager, Does.Not.Contain("RegisterRegionInteraction"));
            Assert.That(manager, Does.Not.Contain("InteractionHysteresis"));
            Assert.That(manager, Does.Not.Contain("SetRelationPhaseConfig"));
            Assert.That(File.Exists("Assets/SpatialBlend/Foundation/InteractionHysteresis.cs"), Is.False);
            Assert.That(File.Exists("Assets/SpatialBlend/Foundation/InteractionPhaseMapper.cs"), Is.False);
            Assert.That(File.ReadAllText(AppPath), Does.Not.Contain("RegisterRegionInteraction"));

            var mapper = new ContainmentInteractionMapper();
            var transitions = new System.Collections.Generic.List<ContainmentInteractionTransition>();
            mapper.Observe(1, 9, true, transitions);
            mapper.Observe(1, 9, true, transitions);
            mapper.Observe(1, 0, false, transitions);
            Assert.That(transitions.Select(t => t.Signal),
                Is.EqualTo(new[] { InteractionSignal.Start, InteractionSignal.Terminate }));
        }

        // v1: ZoneBased is true while the avatar is inside any scene AffordanceVolume,
        //     every frame, with no hysteresis
        //     (v1 SbNetworkAvatarEntity.IsInsideAnyAffordanceVolume).
        // v2: the same declared Containment Region, switching instantly. It is the paper's fixed-zone
        //     baseline, so ZoneBased still ignores the core
        //     interaction event. For a received avatar it reads the core's interaction value (the TRANSFER flag
        //     of its root proposal AND that anchor's ARRIVED); for the local avatar it
        //     reads the source-side transfer fact the core computed on its latest update.
        //     There is no pure point test in the app at all: the core is the single judgment. v1
        //     ZoneBased was that point test for every avatar (v1
        //     SbNetworkAvatarEntity.OnTestAvatarInteraction); v2 replaced it with the core's declared
        //     region and a peer with no congruent counterpart has the flag clear, as v1 had nothing to be inside
        //     when the volume was not in the scene.
        // property: the flag flips on the first sample inside or outside the declared region, and a
        //     scene volume that is not declared does not change it.
        // difference: the zone is the authored region, not a scene volume scan. Volumes with no
        //     declaration (the TABLE host, annotations) are no longer zones. A region marks the
        //     object's own extent, so the 0.5 m margin is only the authoring default.
        [Test]
        public void AV2_ZoneBasedUsesV2ScopeNotPolygons()
        {
            string entity = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs");
            Assert.That(entity, Does.Not.Contain("AffordanceVolume"));
            Assert.That(entity, Does.Contain("IsTransferInteracting"));
            Assert.That(entity, Does.Not.Contain("TryGetContainmentInteraction"));
            Assert.That(entity, Does.Not.Contain("SbSpatialAuthoring.ActiveRegions"), "No point test in the app.");
            Assert.That(entity, Does.Contain("DeclaredRegionZone.Switch"));

            var zone = new DeclaredRegionZone.Switch();
            // The core's interaction value drives the switch with instant semantics.
            Assert.That(zone.Set(true), Is.True);
            Assert.That(zone.Inside, Is.True);
            Assert.That(zone.Set(false), Is.True);
            Assert.That(zone.Inside, Is.False);
        }

        private sealed class AxisAlignedRegion : IDeclaredRegion
        {
            private readonly Vector2[] _polygon;

            public AxisAlignedRegion(float minX, float minZ, float maxX, float maxZ)
            {
                _polygon = new[]
                {
                    new Vector2(minX, minZ), new Vector2(maxX, minZ),
                    new Vector2(maxX, maxZ), new Vector2(minX, maxZ),
                };
            }

            public bool TryGetRegion(out Vector2[] localPolygon, out Matrix4x4 worldToLocal)
            {
                localPolygon = _polygon;
                worldToLocal = Matrix4x4.identity;
                return true;
            }
        }

        // v1: Spacing R=1 at the margin-expanded volume edge
        //     (v1 AffordanceVolume.GenerateVolume and euclidean_proximity.cc).
        // v2: the collider envelope is the support and the Region is Containment (T3.0).
        // property: the support polygon is the footprint. With no declared extent the region is that
        //     footprint plus the 0.5 m fallback margin. A declared extent is the region as written.
        // difference: v1 encoded R=1 at the expanded edge. v2 encodes it at the physical edge.
        //     Native support_distance_m is not measured in this EditMode fixture.
        [Test]
        public void AV3_SpacingSupportIsPhysicalFootprint()
        {
            SpatialAuthoringDeclaration declaration = BoardDeclaration(declaredExtent: false);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            SpatialElementMapping region = plan.Mappings[0];
            SpatialElementMapping board = plan.Mappings[1];
            Assert.That(board.role, Is.EqualTo(SpatialElementRole.ColliderEnvelope));
            Assert.That(region.role, Is.EqualTo(SpatialElementRole.Containment));
            AssertXz(board.polygon, BoardFootprint());
            AssertXz(region.polygon, SpatialAuthoringCompiler.ExpandConvex(BoardFootprint(), 0.5f));
            Assert.That(MaxAbs(board.polygon), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(MaxAbs(region.polygon), Is.EqualTo(0.9f).Within(1e-4f));
        }

        // v1: Angular1D aims at the nearest point on the expanded boundary
        //     (v1 euclidean/angular proximity against the PRESET polygon).
        // v2: Facing grounds on the object's footprint, the collider envelope, never on the region.
        //     The whiteboard declares that footprint as the panel slab with its front edge on the
        //     writable face, and tags the face Writable (T3.7).
        // property: the whiteboard Facing contact lies on the writable face, and the face is the
        //     side the declared extent reaches into.
        // difference: v1 faced the expanded boundary, up to 0.8 m in front of the panel. v2 faces
        //     the physical face. Native contact points are not read in this EditMode fixture.
        [Test]
        public void AV4_FacingContactOnWritableFace()
        {
            SpatialAuthoringPlan plan = CompilePrefab(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            SpatialElementMapping face = plan.Mappings.Single(
                mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance && mapping.tag == "Writable");
            SpatialElementMapping panel = plan.Mappings.Single(
                mapping => mapping.kind == SpatialHierarchyNodeKind.Object);
            SpatialElementMapping region = plan.Mappings.Single(
                mapping => mapping.kind == SpatialHierarchyNodeKind.Region);

            // The writable face is the plane z = 0, and the footprint's front edge is that plane.
            Assert.That(face.surface.All(vertex => Mathf.Abs(vertex.z) < 1e-4f), Is.True);
            Assert.That(panel.polygon.Min(point => point.y), Is.EqualTo(0f).Within(1e-4f));
            // The face looks to -Z, the side the declared extent reaches into.
            Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(face.surface).z, Is.LessThan(-0.99f));
            Assert.That(region.polygon.Min(point => point.y), Is.LessThan(-0.5f));
            Assert.That(region.polygon.Max(point => point.y), Is.LessThan(0.2f));
            Assert.That(plan.ExtentIsDeclared, Is.True);
        }

        // v1: with polygon mapping on, the receiver registers its own volume or adopts
        //     the source polygon (v1 SpatialBlendManager.RegisterVolume).
        // v2: both sides compile the same declaration onto the shared element (T3.0).
        // property: source and receiver plans carry the same local support, and AddAsNew
        //     applies that plan to the received element.
        // difference: v1 could adopt the source polygon. v2 never copies geometry across the wire.
        [Test]
        public void AV5_ReceiverInstantiatesOwnSupport()
        {
            SpatialAuthoringDeclaration source = BoardDeclaration(declaredExtent: true);
            SpatialAuthoringDeclaration receiver = BoardDeclaration(declaredExtent: true);
            Assert.That(SpatialAuthoringCompiler.TryCompile(source, out SpatialAuthoringPlan sourcePlan, out string sourceError),
                Is.True, sourceError);
            Assert.That(SpatialAuthoringCompiler.TryCompile(receiver, out SpatialAuthoringPlan receiverPlan, out string receiverError),
                Is.True, receiverError);
            AssertXz(receiverPlan.Mappings[1].polygon, sourcePlan.Mappings[1].polygon);
            AssertXz(receiverPlan.Mappings[0].polygon, sourcePlan.Mappings[0].polygon);
            Assert.That(receiverPlan.LocalGeometrySignature, Is.EqualTo(sourcePlan.LocalGeometrySignature));
            Assert.That(MaxAbs(receiverPlan.Mappings[1].polygon), Is.LessThan(MaxAbs(receiverPlan.Mappings[0].polygon)));

            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            Assert.That(manager, Does.Contain("TryApplyReceivedAuthoring(entity, entity_id, sharedId)"));
            Assert.That(manager, Does.Contain("TryApplySpatialAuthoring"));
            Assert.That(typeof(SpatialBlendManager).GetMethod("TryApplySpatialAuthoring", AnyMember), Is.Not.Null);
        }

        // v1: RemapApproach maps a contact through the radial/MVC chart of the two volumes.
        // v2: the share link plus identical local support is the identity map in the object
        //     frame. The native radial chart is the decode (T3.0).
        // property: a contact's normalized footprint coordinate is the same on both plans.
        // difference: v1 used radial/MVC. v2 uses the regularized radial chart. This EditMode
        //     row checks the identical local support that decode requires. The native chart
        //     was not executed.
        [Test]
        public void AV6_ChessContactMapsByChart()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(BoardDeclaration(declaredExtent: false),
                out SpatialAuthoringPlan source, out string sourceError), Is.True, sourceError);
            Assert.That(SpatialAuthoringCompiler.TryCompile(BoardDeclaration(declaredExtent: false),
                out SpatialAuthoringPlan receiver, out string receiverError), Is.True, receiverError);
            Vector2 contact = new Vector2(0.2f, 0.1f);
            Vector2 sourceChart = NormalizeToFootprint(contact, source.Mappings[1].polygon);
            Vector2 receiverChart = NormalizeToFootprint(contact, receiver.Mappings[1].polygon);
            Assert.That(sourceChart.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(sourceChart.y, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(receiverChart.x, Is.EqualTo(sourceChart.x).Within(1e-4f));
            Assert.That(receiverChart.y, Is.EqualTo(sourceChart.y).Within(1e-4f));
            Assert.That(ContainsXz(source.Mappings[1].polygon, contact), Is.True);
            Assert.That(ContainsXz(source.Mappings[0].polygon, new Vector2(0.75f, 0f)), Is.True);
            Assert.That(ContainsXz(source.Mappings[1].polygon, new Vector2(0.75f, 0f)), Is.False);
        }

        // v1: the TABLE is linked by label and has no polygon mapping
        //     (v1 FreeformCollaborationApp and the MRUK skip in RegisterVolume).
        // v2: label link (declared exception) plus explicit authoring on both
        //     sides: the table top is the Tabletop support surface and the region is the table's
        //     own extent. The link the core records on resolve is the correspondence.
        // SB concept: correspondence plus the chart M between the two supports.
        // property: one element per anchor, a source position maps to the same normalized chart
        //     position on the receiver table, and the link ends with the unshare. The native
        //     checks are CrossDeviceTableChartIsPreservedForDifferentTables and
        //     TableAnchorHostSharesReceiverLinksOrRejectsAndUnshareKeepsLocal (G-N).
        // difference: DECLARED UPGRADE over v1, which never mapped the table.
        [Test]
        public void AV7_TabletopCorrespondenceIsAuthoredAndLinked()
        {
            var go = new GameObject("TABLE");
            try
            {
                var entity = go.AddComponent<SbMRUKAnchorEntity>();
                entity.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
                entity.ApplySupport(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(1.2f, 0.8f, 0.05f));
                SbSpatialAuthoring authoring = go.GetComponent<SbSpatialAuthoring>();
                Assert.That(authoring, Is.Not.Null);
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration,
                    out string declarationError), Is.True, declarationError);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan,
                    out string planError), Is.True, planError);
                // The region is the tabletop plus the 0.5 m fallback margin.
                Vector2[] region = plan.Mappings[0].polygon;
                Vector2[] footprint = plan.Mappings[1].polygon;
                float stand = SceneAnchorSpatialAuthoring.StandOffMeters;
                Assert.That(region.Max(p => p.x) - region.Min(p => p.x),
                    Is.EqualTo(footprint.Max(p => p.x) - footprint.Min(p => p.x) + 2f * stand).Within(1e-3f));
                Assert.That(region.Max(p => p.y) - region.Min(p => p.y),
                    Is.EqualTo(footprint.Max(p => p.y) - footprint.Min(p => p.y) + 2f * stand).Within(1e-3f));
                Assert.That(plan.Mappings[2].tag, Is.EqualTo(SceneAnchorSpatialAuthoring.TabletopTag));
                Assert.That(plan.Mappings[2].role, Is.EqualTo(SpatialElementRole.Surface));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            Assert.That(File.ReadAllText(AppPath), Does.Not.Contain("TryRegisterOwned"));
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.SpatialHierarchy.cs"),
                Does.Contain("SpatialAuthoringExistingElements"));
        }

        // v1: "user to any object" was scoped only by which entities had a volume
        //     (v1 FreeformCollaborationApp wildcard and RegisterVolume's skip).
        // v2 (ABI 4.2): a bind takes no scope; the tag is the AffordancePose binding's only.
        // difference: v1 had no family scopes. Native sb_get_scope_records was not read here.
        //     The app no longer binds AffordancePose on the root to a region (T3.8). Facing stays a
        //     global relation in the core and the app does not filter it by where its referent is,
        //     and Facing does not drive the root.
        [Test]
        public void AV8_FamilyScopesFollowHierarchy()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(BoardDeclaration(declaredExtent: true),
                out SpatialAuthoringPlan plan, out string error), Is.True, error);
            Assert.That(plan.Mappings[0].parentId, Is.EqualTo(SpatialAuthoringCompiler.RoomParentId));
            Assert.That(plan.Mappings[1].parentId, Is.EqualTo(plan.RegionId));
            Assert.That(plan.Mappings[2].parentId, Is.EqualTo(plan.ObjectId));

            // A binding names no scope, and a tag survives only on AffordancePose.
            RelationBinding tagged = SpatialAuthoringCompiler.PlanRelation(RelationKind.AffordancePose, "root", "Playable");
            Assert.That(tagged.Tag, Is.EqualTo("Playable"));
            Assert.That(SpatialAuthoringCompiler.PlanRelation(RelationKind.Spacing, "root", "Playable").Tag, Is.Null);

            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            int start = manager.IndexOf("public void RegisterRelation", StringComparison.Ordinal);
            int end = manager.IndexOf("private void DispatchRelationBinding", StringComparison.Ordinal);
            string relation = manager.Substring(start, end - start);
            Assert.That(relation, Does.Not.Contain("_roomElementID"));
            Assert.That(relation, Does.Contain("PlanRelation"));
            Assert.That(manager, Does.Contain("BindAffordance"));
        }

        // v1: the volume type name was the only label on the interaction region.
        // v2: element tags and sub-regions name the affordance (T3.7).
        // property: Chess, Whiteboard, and Tabletop tags are present on both sides.
        // difference: tags are new. v1 had none. The Tabletop tag comes from the TABLE authoring
        //     helper because the MRUK anchor has no prefab.
        [Test]
        public void AV9_AffordanceTagsAuthored()
        {
            // "Both sides" are two compilations of the same declaration, source and receiver.
            foreach (var expected in new[]
            {
                ("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab", "Playable"),
                ("Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab", "Writable"),
            })
            {
                SpatialAuthoringPlan source = CompilePrefab(expected.Item1);
                SpatialAuthoringPlan receiver = CompilePrefab(expected.Item1);
                Assert.That(source.Mappings.Any(mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance &&
                    mapping.tag == expected.Item2), Is.True, expected.Item1);
                Assert.That(receiver.LocalGeometrySignature, Is.EqualTo(source.LocalGeometrySignature));
            }

            var anchor = new GameObject("TABLE");
            try
            {
                Assert.That(SceneAnchorSpatialAuthoring.Apply(anchor, new Vector3(0f, 0f, -0.35f),
                    new Vector3(1.2f, 0.8f, 0.7f)), Is.Not.Null);
                SpatialAuthoringPlan table = CompileComponent(anchor.GetComponent<SbSpatialAuthoring>());
                Assert.That(table.Mappings.Any(mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance &&
                    mapping.tag == "Tabletop"), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchor); }
        }

        // v1: placement keeps every volume vertex on the Unity NavMesh, 0.2 m inward.
        // v2: the snap uses the authored Region against the walkable region registered with the core
        //     (boundary plus holes, T3.3), kept by the manager and read through the app (T3.3b).
        // property: after placement every region vertex is inside the boundary and outside holes,
        //     0.2 m clear of both. WalkablePlacementSnapTests covers the math.
        // difference: the snap target is the core region, not the Unity NavMesh, so a NavMesh obstacle
        //     hole and a core obstacle hole are the same thing.
        [Test]
        public void AV10_PlacementSnapsTheAuthoredRegion()
        {
            string placement = File.ReadAllText(
                "Assets/SpatialBlend/Interaction/Scripts/Placement/PlacementMotif.cs");
            Assert.That(placement, Does.Not.Contain("AffordanceVolume"));
            Assert.That(placement, Does.Not.Contain("NavMesh"));
            Assert.That(placement, Does.Contain("SbSpatialAuthoring"));
            // The drop pulls the region to the nearest valid spot of the room (RoomFit, RegionPull).
            Assert.That(placement, Does.Contain("TryPullPlacement"));

            // The data is the one T3.3 registered: the same ring and holes, not a second copy.
            var walkable = new WalkableRegion(
                new[] { new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(10f, 10f), new Vector2(0f, 10f) },
                new[] { new[] { new Vector2(4f, 4f), new Vector2(6f, 4f), new Vector2(6f, 6f), new Vector2(4f, 6f) } });
            var region = new[] { new Vector2(9f, 1f), new Vector2(11f, 1f), new Vector2(11f, 2f), new Vector2(9f, 2f) };
            Vector2 shift = WalkablePlacementSnap.Snap(region, walkable, 0.2f);
            Assert.That(shift.x, Is.EqualTo(-1.2f).Within(1e-3f));
        }

        // v1: a GLB volume is the box-annotation footprint plus 0.15 m, or the collider.
        // v2: the receiver's own reconstructed bounds are the support (T3.4, T3.7). The content
        //     prefab declares the footprint as its root collider, which the loader fits to the
        //     mesh each side loads, and a 0.15 m fallback margin for the region.
        // property: the receiver GLB element has a definition from its own bounds.
        // difference: the extent of loaded content cannot be written into a prefab, so the GLB is
        //     the one object whose region is the footprint plus a margin. The margin is the v1 GLB
        //     0.15 m, not the 0.5 m authoring default.
        [Test]
        public void AV11_NativeContentHasLocalSupport()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab");
            Assert.That(prefab, Is.Not.Null);
            SbSpatialAuthoring authoring = prefab.GetComponentInChildren<SbSpatialAuthoring>(true);
            Assert.That(authoring, Is.Not.Null);
            Assert.That(authoring.footprint, Is.TypeOf<BoxCollider>(), "The footprint is the fitted root collider.");
            Assert.That(authoring.footprintPolygon, Is.Null.Or.Empty, "No bounds are copied from another space.");
            Assert.That(authoring.declaredExtent, Is.Null.Or.Empty);
            Assert.That(authoring.fallbackMarginMeters, Is.EqualTo(0.15f));

            // Each side fits its own collider to its own mesh. Different content sizes give
            // different footprints, and the region follows each one.
            var content = new GameObject("content");
            try
            {
                var box = content.AddComponent<BoxCollider>();
                var local = content.AddComponent<SbSpatialAuthoring>();
                local.footprint = box;
                local.fallbackMarginMeters = 0.15f;
                box.size = new Vector3(0.6f, 0.4f, 0.3f);
                SpatialAuthoringPlan small = CompileComponent(local);
                box.size = new Vector3(1.2f, 0.4f, 0.8f);
                SpatialAuthoringPlan large = CompileComponent(local);
                Assert.That(small.Mappings[1].polygon.Max(point => point.x), Is.EqualTo(0.3f).Within(1e-4f));
                Assert.That(large.Mappings[1].polygon.Max(point => point.x), Is.EqualTo(0.6f).Within(1e-4f));
                Assert.That(small.Mappings[0].polygon.Max(point => point.x), Is.EqualTo(0.45f).Within(1e-4f));
                Assert.That(large.Mappings[0].polygon.Max(point => point.x), Is.EqualTo(0.75f).Within(1e-4f));
                Assert.That(small.ExtentIsDeclared, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(content); }
        }

        // v1: box and floor annotation create an AffordanceVolume, often with no entity,
        //     so the core never sees it.
        // v2: the annotated box becomes a Region, a footprint and a tagged top through
        //     SbSpatialAuthoring (T5.2). The box declares itself while it exists, a deleted box declares
        //     nothing, and the scanned object it produced takes the declaration and shares it.
        // property: an annotated box compiles to a declared Containment region, and no component on the
        //     annotation path is an AffordanceVolume.
        // difference: annotations now reach the core through the object they produce. In v1 they did not.
        //     The floor-polygon motif is a separate v1 path; the box mode of the manipulation panel never
        //     runs it, and its removal belongs to T3.6.
        [Test]
        public void AV12_AnnotationBecomesARegion()
        {
            string instance = File.ReadAllText(
                "Assets/SpatialBlend/Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs");
            Assert.That(instance, Does.Contain("BoxAnnotationAuthoring.Declare"));
            Assert.That(instance, Does.Contain("BoxAnnotationAuthoring.Withdraw"));
            string authoringSource = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/BoxAnnotationAuthoring.cs");
            Assert.That(authoringSource, Does.Contain("SbSpatialAuthoring"));
            Assert.That(string.Concat(authoringSource.Split('\n')
                    .Select(line => line.Split(new[] { "//" }, StringSplitOptions.None)[0])),
                Does.Not.Contain("AffordanceVolume"));

            var box = new GameObject("AV12-box");
            try
            {
                var collider = box.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, 0.3f, 0f);
                collider.size = new Vector3(1f, 0.6f, 0.4f);
                SbSpatialAuthoring authoring = BoxAnnotationAuthoring.Declare(box, collider);
                Assert.That(authoring, Is.Not.Null);
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                    Is.True, error);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                    Is.True, error);
                Assert.That(plan.Mappings[0].role, Is.EqualTo(SpatialElementRole.Containment));
                Assert.That(plan.ExtentIsDeclared, Is.True);
                Assert.That(box.GetComponents<Component>().Select(component => component.GetType().Name),
                    Does.Not.Contain("AffordanceVolume"));
            }
            finally { UnityEngine.Object.DestroyImmediate(box); }
        }

        // v1: AffordanceVolume drives registration, ZoneBased, and placement.
        // v2: no runtime path reads it. An editor migration helper is the exception (T3.6).
        // property: runtime code does not read AffordanceVolume.
        // difference: none. The python guard is the check.
        [Test]
        public void AV13_NoRuntimeAffordanceVolume()
        {
            Assert.That(File.Exists("tools/test_no_runtime_affordance_volume.py"), Is.True);
            Assert.That(File.Exists("Assets/SpatialBlend/Editor/AffordanceVolumeMigration.cs"), Is.True,
                "Old content converts through the Editor helper.");
            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            Assert.That(manager, Does.Not.Contain("AffordanceVolume"));
            Assert.That(manager, Does.Not.Contain("RegisterVolume"));
        }

        // v1: inside an object's AffordanceVolume the avatar's transform relative to the object was
        //     kept, and the raw Meta avatar stream played there, aligned through
        //     MetaAvatarEntity.StartContinuousCompensation. The volume stood for the avatar's scope
        //     and frame while it was near the object.
        // v2: containment transfer (T3.8). The region is written element-local and
        //     flagged on both sides. While the root is contained the core sends its pose in the
        //     object's frame, the receiver maps it through its counterpart (the identity for identical
        //     copies, a non-trivial chart for the TABLE), and a 0.2 m band blends it with the
        //     scene-level target. The remote root is locked to that pose and the raw Meta body is kept
        //     on the realized root (T2.6). Facing stays global meanwhile.
        // property: the remote avatar's object-relative transform is preserved within 1 cm and 2 degrees
        //     for any receiver placement of an identical object (the G-N cases
        //     IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw*).
        //     This row checks the managed wiring and the pure pieces.
        // difference: v1 preserved it by copying the avatar transform inside the volume. v2 transfers
        //     it as the frame of the contained object, so the TABLE (different physical tables) maps
        //     non-trivially on the same path. Tagged surfaces are optional metadata and do not change
        //     what happens inside the region. A peer without the object keeps the scene-level targets.
        [Test]
        public void AV14_ContainmentTransfersTheObjectRelativePose()
        {
            string spatial = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.SpatialHierarchy.cs");
            Assert.That(spatial, Does.Contain("WriteAuthoredRegion("));
            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.ContainmentTransfer.cs");
            Assert.That(manager, Does.Contain("SetSpatialHierarchyLocalBoundary"));
            Assert.That(manager, Does.Contain("SetSpatialHierarchyContainmentTransfer"));
            // The core proposes the pose blended to the mapped contained pose; the driver follows it like any target.
            string driverSource = File.ReadAllText("Assets/SpatialBlend/Foundation/Realization/EmbodiedDriver.cs");
            Assert.That(driverSource, Does.Not.Contain("Lock"), "The embodied driver has no contained lock.");
            Assert.That(driverSource, Does.Not.Contain("ContainedPosition"));
            Assert.That(driverSource, Does.Not.Contain("ContainState"));
            string targets = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            Assert.That(targets, Does.Not.Contain("ContainedFacingPolicy"), "Facing is global; the app has no scope override (U4).");
            Assert.That(targets, Does.Not.Contain("ApplyContainedRoot("));
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/MetaAvatarEntity.cs"),
                Does.Contain("StartContinuousCompensation"));
        }

        // v1: SAM3D scanning and a shared object arriving show a portal effect (an Asset Store particle package).
        //     Sam3dPendingIndicatorManager holds the two prefabs in the scene.
        // v2: the same manager, as a scene root with no v1 parent; the two prefabs are the repository's own particle
        //     indicators (the Asset Store portals cannot be redistributed). A remote replica shows the shared-object
        //     indicator while its content loads (SbNativeEntity.LoadContentAsync).
        // property: the Freeform scene has one manager, its scan indicator is ScanPendingIndicator, its shared-object
        //     indicator is SharedObjectPendingIndicator, and the replica load spawns and clears it.
        // difference: v1 also moved a remote object to a deterministic staging ring before its state arrived.
        //     v2's core places the replica, so the staging position is not ported. The detail is in
        //     PresentationPortContractTests.
        [Test]
        public void E1_PortalEffectsMarkScansAndSharedObjects()
        {
            using (var scene = FreeformScene.Open())
            {
                var manager = scene.Components<SpatialBlend.Samples.Scanning.Sam3dPendingIndicatorManager>().Single();
                var serialized = new SerializedObject(manager);
                Assert.That(AssetDatabase.GetAssetPath(
                        serialized.FindProperty("_sam3dPendingIndicatorPrefab").objectReferenceValue),
                    Does.EndWith("Samples/Scanning/Indicators/ScanPendingIndicator.prefab"));
                Assert.That(AssetDatabase.GetAssetPath(
                        serialized.FindProperty("_sharedObjectPendingIndicatorPrefab").objectReferenceValue),
                    Does.EndWith("Samples/Scanning/Indicators/SharedObjectPendingIndicator.prefab"));
            }
            string entity = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNativeEntity.cs");
            // The spawn, failure and re-entry behaviour is run in PresentationPortContractTests.
            Assert.That(entity, Does.Contain("RunWithSharedObjectPortal("));
        }

        // v1: scan, arrival, annotation and placement play spatial clips from Resources/SFX.
        // v2: the same eight clips under Samples/Resources/SFX with main's GUIDs; the annotation and placement
        //     motifs play them as v1 did.
        // property: every clip a runtime script loads from Resources resolves to an AudioClip.
        // difference: none. The three clips main never carried a .meta for were given new GUIDs.
        [Test]
        public void E2_SpatialClipsResolve()
        {
            foreach (string name in new[]
            {
                "AnnotationCreated", "AnnotationPointPlaced", "ObjectPlaced", "RemoteAvatarArrived",
                "RemoteObjectArrived", "ScanComplete", "ScanInProgress", "ScanTrigger",
            })
            {
                Assert.That(Resources.Load<AudioClip>("SFX/" + name), Is.Not.Null, name);
            }
        }

        // v1: avatars and imported glTF content are hidden by the real environment through depth occlusion.
        // v2: the avatar shaders were never dropped. The glTF occlusion shader is ported, and both glTF import
        //     paths build their materials through GltfOcclusionMaterial.
        // property: SpatialBlend/GLTF/PbrMetallicRoughnessOccluded compiles, is in the always-included list, and
        //     the generator gives an imported material that shader.
        // difference: none. A missing shader falls back to the glTFast default materials.
        [Test]
        public void E3_ImportedContentIsOccludedByTheEnvironment()
        {
            Shader shader = Shader.Find(SpatialBlend.Objects.GltfOcclusionMaterial.ShaderName);
            Assert.That(shader, Is.Not.Null);
            Material material = SpatialBlend.Objects.GltfOcclusionMaterial.CreateDefaultMaterial(shader);
            Assert.That(material.shader, Is.SameAs(shader));
            Assert.That(File.ReadAllText("ProjectSettings/GraphicsSettings.asset"),
                Does.Contain("guid: " + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader))));
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/MetaAvatarEntity.cs"), Does.Contain("_AlphaFade"));
        }

        // v1: the Quest app has a Freeform splash, icon and the OS splash flag.
        // v2: the same two images with main's GUIDs, assigned in ProjectSettings, and the manifest flag.
        // property: the splash and icon load as textures and ProjectSettings references them.
        // difference: the company name stays DefaultCompany. Changing it would move persistentDataPath.
        [Test]
        public void E4_QuestSplashAndIconAreFreeforms()
        {
            string splash = "Assets/Resources/FreeformCollaboration_Splash.png";
            string icon = "Assets/Resources/FreeformCollaboration_Icon.png";
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(splash), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(icon), Is.Not.Null);
            string settings = File.ReadAllText("ProjectSettings/ProjectSettings.asset");
            Assert.That(settings, Does.Contain("androidSplashScreen: {fileID: 2800000, guid: " + AssetDatabase.AssetPathToGUID(splash)));
            Assert.That(File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml"),
                Does.Contain("com.oculus.ossplash\" android:value=\"true\""));
            Assert.That(File.ReadAllText("Assets/XR/Settings/OpenXR Package Settings.asset"),
                Does.Contain("systemSplashScreen: {fileID: 2800000, guid: " + AssetDatabase.AssetPathToGUID(splash)));
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Editor/SbSplashBackground.cs"),
                Does.Contain("passthrough-contextual"));
        }

        // v1: a box annotation can be deleted from its manipulation pop-up.
        // v2: ported with the T5.2 annotation work. ManipulationPopUp has a delete toggle that selects mode 3,
        //     and ManipulationContext.ExecuteDeleteMode destroys the box. The pop-up prefab is unchanged.
        // property: the toggle is wired in the prefab and the mode is handled.
        // difference: none. The redesigned ControlPanel does not host this pop-up, so its look is unaffected.
        [Test]
        public void E5_BoxDeleteButtonIsOnThePopUp()
        {
            string popup = File.ReadAllText("Assets/SpatialBlend/Interaction/Scripts/UI/ManipulationPopUp.cs");
            Assert.That(popup, Does.Contain("public UnityEvent OnDeleteSelected;"));
            string context = File.ReadAllText("Assets/SpatialBlend/Interaction/Scripts/Manipulation/ManipulationContext.cs");
            Assert.That(context, Does.Contain("popup.OnDeleteSelected.AddListener(() => SetSelectedMode(3));"));
            Assert.That(context, Does.Contain("private void ExecuteDeleteMode()"));
            Assert.That(File.ReadAllText("Assets/SpatialBlend/Interaction/Prefabs/UI/ManipulationPopUpUI.prefab"),
                Does.Contain("_deleteToggle: {fileID: 2531212046790681921}"));
        }

        private static void AssertLegacyContent(string prefabPath, string motifName,
            string definitionName, int pieceCount)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null, prefabPath);
            Assert.That(prefab.GetComponentInChildren<SbNetworkEntity>(true), Is.Not.Null, prefabPath);
            Assert.That(HasComponentNamed(prefab, motifName), Is.True, motifName);

            Component definition = prefab.GetComponents<Component>()
                .Single(component => component != null && component.GetType().Name == definitionName);
            if (pieceCount > 0)
            {
                var pieces = definition.GetType().GetProperty("Pieces").GetValue(definition) as Array;
                Assert.That(pieces, Is.Not.Null);
                Assert.That(pieces.Length, Is.EqualTo(pieceCount));
            }

            var legacy = definition.GetType().GetProperty("LegacyContentBehaviours").GetValue(definition);
            bool suppressesMotif = false;
            foreach (UnityEngine.Object behaviour in (System.Collections.IEnumerable)legacy)
            {
                if (behaviour != null && behaviour.GetType().Name == motifName)
                {
                    suppressesMotif = true;
                }
            }
            Assert.That(suppressesMotif, Is.True, motifName + " is a legacy writer the local backend can suppress.");

            Type backend = RequireType("SpatialBlend.Samples.FreeformCollaboration.FreeformSceneLocalBackend");
            Assert.That(backend.GetMethod("SuppressLegacyContent", AnyMember), Is.Not.Null);
            string app = File.ReadAllText(AppPath);
            Assert.That(app, Does.Contain("if (UsesFoundationLocal)"));
        }

        private static bool HasComponentNamed(GameObject prefab, string typeName)
        {
            return prefab.GetComponentsInChildren<Component>(true)
                .Any(component => component != null && component.GetType().Name == typeName);
        }

        private static string ManagerSources()
        {
            return string.Concat(Directory.GetFiles(
                "Assets/SpatialBlend/Framework/Scripts", "SpatialBlendManager*.cs")
                .Select(File.ReadAllText));
        }

        // v1 is told apart by content: its spatialblend_* entry points and the NativeAPI class.
        // The managed binding to the current core is called SpatialBlendAPI and is not v1.
        private static readonly System.Text.RegularExpressions.Regex V1CoreApi =
            new System.Text.RegularExpressions.Regex(
                @"\bNativeAPI\.|\bspatialblend_(create_space|connect_to_space|destroy_space|cleanup_all|create_entity|get_entity_state)\b");

        private static System.Collections.Generic.IEnumerable<string> RuntimeV1CoreApiCalls()
        {
            foreach (string path in Directory.GetFiles("Assets/SpatialBlend", "*.cs", SearchOption.AllDirectories))
            {
                string normalized = path.Replace('\\', '/');
                if (normalized.Contains("/Test/"))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; ++i)
                {
                    if (V1CoreApi.IsMatch(lines[i]))
                    {
                        yield return normalized + ":" + (i + 1);
                    }
                }
            }
        }

        private static Type RequireType(string fullName)
        {
            Type type = FindType(fullName);
            Assert.That(type, Is.Not.Null, fullName);
            return type;
        }

        private static RemoteAvatarVisibilityDecision Visibility(
            AvatarDisplayMode mode, bool interacting, float elapsed, float hideSeconds, float fadeDuration = 0f)
        {
            return RemoteAvatarVisibilityPolicy.Evaluate(new RemoteAvatarVisibilityInput(
                mode, interacting, false, elapsed, hideSeconds, fadeDuration,
                RemoteAvatarVisibilityPolicy.NoFadeSample));
        }

        private static Vector3[] WalkableHoleVertices()
        {
            return new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f),
                new Vector3(1f, 0f, 1f), new Vector3(3f, 0f, 1f),
                new Vector3(3f, 0f, 3f), new Vector3(1f, 0f, 3f),
            };
        }

        private static int[] WalkableHoleIndices()
        {
            return new[]
            {
                0, 1, 5, 0, 5, 4,
                1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6,
                3, 0, 4, 3, 4, 7,
            };
        }

        private sealed class RecordingWalkableManager : SpatialBlendManager
        {
            public readonly List<Vector2[]> Obstacles = new List<Vector2[]>();
            public readonly List<int> ObstacleRoles = new List<int>();

            protected override void InitializeNativeLibrary() { }

            public override void Host(int port) { }

            protected override long EnsureWalkableRoom() => 7;

            protected override long AddWallElement(long parent) => 20;

            protected override bool SetWalkablePolygon(long element, int role, IReadOnlyList<Vector2> points)
            {
                if (role != (int)SbGeometryRole.Obstacle)
                {
                    return true;
                }

                var copy = new Vector2[points.Count];
                for (int i = 0; i < points.Count; ++i)
                {
                    copy[i] = points[i];
                }

                ObstacleRoles.Add(role);
                Obstacles.Add(copy);
                return true;
            }

            protected override bool RemoveWallElement(long element) => true;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch (ReflectionTypeLoadException)
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static Type FindTypeBySimpleName(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }

                if (types == null)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type != null && type.Name == simpleName)
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        // Loads the prefab, compiles the declaration on its entity, and unloads it.
        private static SpatialAuthoringPlan CompilePrefab(string path)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                SbSpatialAuthoring authoring = root.GetComponentInChildren<SbSpatialAuthoring>(true);
                Assert.That(authoring, Is.Not.Null, path);
                return CompileComponent(authoring);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static SpatialAuthoringPlan CompileComponent(SbSpatialAuthoring authoring)
        {
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            return plan;
        }

        private static SpatialAuthoringDeclaration BoardDeclaration(bool declaredExtent)
        {
            return new SpatialAuthoringDeclaration
            {
                objectId = "Chess",
                regionId = "ChessArea",
                footprint = BoardFootprint(),
                fallbackMarginMeters = SpatialAuthoringCompiler.DefaultRegionMarginMeters,
                declaredExtent = declaredExtent
                    ? new[]
                    {
                        new Vector2(-0.5f, -0.9f), new Vector2(0.5f, -0.9f),
                        new Vector2(0.5f, 0.9f), new Vector2(-0.5f, 0.9f),
                    }
                    : null,
                affordances = new[]
                {
                    new SpatialAffordanceDeclaration
                    {
                        id = "ChessTop",
                        tag = "Playable",
                        supportId = "chess.top",
                        surface = new[]
                        {
                            new Vector3(-0.4f, 0.05f, -0.4f), new Vector3(0.4f, 0.05f, -0.4f),
                            new Vector3(0.4f, 0.05f, 0.4f), new Vector3(-0.4f, 0.05f, 0.4f),
                        },
                    },
                },
            };
        }

        private static Vector2[] BoardFootprint() => new[]
        {
            new Vector2(-0.4f, -0.4f), new Vector2(0.4f, -0.4f),
            new Vector2(0.4f, 0.4f), new Vector2(-0.4f, 0.4f),
        };

        private static void AssertXz(Vector2[] actual, Vector2[] expected)
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(expected, Is.Not.Null);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int index = 0; index < expected.Length; ++index)
            {
                Assert.That(actual[index].x, Is.EqualTo(expected[index].x).Within(1e-4f));
                Assert.That(actual[index].y, Is.EqualTo(expected[index].y).Within(1e-4f));
            }
        }

        private static float MaxAbs(Vector2[] polygon)
        {
            float max = 0f;
            foreach (Vector2 point in polygon)
            {
                max = Mathf.Max(max, Mathf.Abs(point.x), Mathf.Abs(point.y));
            }
            return max;
        }

        private static Vector2 NormalizeToFootprint(Vector2 contact, Vector2[] footprint)
        {
            float halfX = 0f;
            float halfY = 0f;
            foreach (Vector2 point in footprint)
            {
                halfX = Mathf.Max(halfX, Mathf.Abs(point.x));
                halfY = Mathf.Max(halfY, Mathf.Abs(point.y));
            }
            return new Vector2(contact.x / halfX, contact.y / halfY);
        }

        private static bool ContainsXz(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
            {
                Vector2 start = polygon[previous];
                Vector2 end = polygon[index];
                if (((start.y > point.y) != (end.y > point.y)) &&
                    point.x < (end.x - start.x) * (point.y - start.y) / (end.y - start.y) + start.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private sealed class FreeformScene : IDisposable
        {
            private readonly Scene _scene;
            private readonly bool _opened;

            private FreeformScene(Scene scene, bool opened)
            {
                _scene = scene;
                _opened = opened;
            }

            public static FreeformScene Open()
            {
                Scene scene = SceneManager.GetSceneByPath(ScenePath);
                bool opened = !scene.isLoaded;
                if (opened)
                {
                    scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                }

                return new FreeformScene(scene, opened);
            }

            public T[] Components<T>() where T : Component
            {
                return _scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<T>(true))
                    .ToArray();
            }

            public int Count<T>() where T : Component
            {
                return Components<T>().Length;
            }

            public int Count(Type type)
            {
                return _scene.GetRootGameObjects()
                    .Sum(root => root.GetComponentsInChildren(type, true).Length);
            }

            public void Dispose()
            {
                if (_opened && _scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(_scene, true);
                }
            }
        }
    }
}
#endif
