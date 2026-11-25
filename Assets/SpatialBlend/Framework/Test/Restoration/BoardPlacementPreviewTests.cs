// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The real Chess and Whiteboard prefabs in a lobby room (a FreeformCollaborationApp with a 6 m square room and one piece of
    /// furniture, no session), dropped through their motifs' own release code. EditMode runs no Awake or Start, so the fixture wires
    /// what they would find (the entity, the eye, the floor under a ray).
    /// </summary>
    internal sealed class BoardRoom
    {
        public const string ChessPrefab = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        public const string WhiteboardPrefab = "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab";
        public static readonly Vector2[] Furniture =
        {
            new Vector2(-0.5f, 1.0f), new Vector2(0.5f, 1.0f), new Vector2(0.5f, 1.8f), new Vector2(-0.5f, 1.8f),
        };

        public readonly List<Object> Objects = new List<Object>();
        public FreeformCollaborationApp App;
        public RoomRegion Room;
        public GameObject Chess, Whiteboard, Eye;
        public ObjectPlacementMotif ChessMotif;
        public PanelPlacementMotif WhiteboardMotif;
        public readonly List<PlacementDrop> Drops = new List<PlacementDrop>();
        /// <summary>The 6 m square room with one piece of furniture (<see cref="Furniture"/>).</summary>
        public static RoomRegion SquareRoom(float half = 3f, params Vector2[][] furniture) =>
            RoomRegion.Build(new[] { new Vector2(-half, -half), new Vector2(half, -half), new Vector2(half, half), new Vector2(-half, half) }, null,
                furniture.Length > 0 ? furniture : new[] { Furniture });

        /// <summary>A square room with no furniture.</summary>
        public static RoomRegion EmptyRoom(float half) =>
            RoomRegion.Build(new[] { new Vector2(-half, -half), new Vector2(half, -half), new Vector2(half, half), new Vector2(-half, half) }, null, null);

        public static Vector2[] Box(float x0, float z0, float x1, float z1) =>
            new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };

        public static BoardRoom Create(RoomRegion region = null)
        {
            var room = new BoardRoom();
            var appObject = new GameObject("app");
            room.Objects.Add(appObject);
            room.App = appObject.AddComponent<FreeformCollaborationApp>();
            room.Room = region ?? SquareRoom();
            typeof(FreeformCollaborationApp).GetField("_localRoom", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(room.App, room.Room);
            room.App.FloorHeightProvider = () => 0f;
            room.Eye = new GameObject("eye");
            room.Objects.Add(room.Eye);
            room.Eye.transform.position = new Vector3(0f, 1.6f, -2.5f);
            room.Chess = room.Spawn(ChessPrefab, "Chess");
            room.Whiteboard = room.Spawn(WhiteboardPrefab, "Whiteboard");
            room.ChessMotif = (ObjectPlacementMotif)room.Wire(room.Chess);
            room.WhiteboardMotif = (PanelPlacementMotif)room.Wire(room.Whiteboard);
            // The floor is the plane y = 0 (the drop's raycast; MRUK or depth on a device).
            room.ChessMotif.FloorSource = (ray, max) =>
            {
                if (ray.direction.y >= 0f) return null;
                if (Mathf.Abs(ray.origin.x) > 8f || Mathf.Abs(ray.origin.z) > 8f) return null;   // no floor far outside the room
                float t = -ray.origin.y / ray.direction.y;
                return t <= max ? ray.origin + ray.direction * t : (Vector3?)null;
            };
            // Out of the way, on the open floor.
            room.Chess.transform.position = new Vector3(-2f, 0.5f, -2f);
            room.Whiteboard.transform.position = new Vector3(2f, 1f, -2f);
            PlacementMotif.Dropped += room.OnDropped;
            return room;
        }

        private void OnDropped(PlacementDrop drop) => Drops.Add(drop);

        public GameObject Spawn(string path, string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            GameObject go = Object.Instantiate(prefab);
            go.name = name;
            Objects.Add(go);
            SbEntity entity = go.GetComponentInChildren<SbEntity>(true);
            return entity != null ? entity.gameObject : go;
        }

        public PlacementMotif Wire(GameObject root)
        {
            PlacementMotif motif = root.GetComponentInChildren<PlacementMotif>(true);
            Assert.That(motif, Is.Not.Null, root.name);
            SetField(typeof(PlacementMotif), motif, "_entityTransform", root.transform);
            SetField(typeof(PlacementMotif), motif, "_centerEyeAnchor", Eye.transform);
            SetField(motif.GetType(), motif, "_objectPlacedClip", null);   // no one-shot audio object in EditMode
            return motif;
        }

        public static void SetField(System.Type type, object target, string name, object value) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static object GetField(System.Type type, object target, string name) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        /// <summary>Holds <paramref name="motif"/>'s object at <paramref name="pose"/> (a grab in progress).</summary>
        public void Hold(PlacementMotif motif, Pose pose, bool onWall = false)
        {
            // A new grab notes where the object was (a release with no valid spot returns it there), as the motif's claim does.
            if (!motif.IsClaimed) motif.NoteGrabStart();
            SetField(typeof(InteractionMotif), motif, "_isGrabbed", true);
            if (motif is PanelPlacementMotif) SetField(typeof(PanelPlacementMotif), motif, "_onWall", onWall);
            Transform t = ((Component)typeof(PlacementMotif).GetField("_entityTransform", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motif)).transform;
            t.SetPositionAndRotation(pose.position, pose.rotation);
        }

        /// <summary>Lets go through the motif's own release (its Unselect), and returns the drop it reported.</summary>
        public PlacementDrop Release(PlacementMotif motif)
        {
            Drops.Clear();
            var interactable = (Oculus.Interaction.RayInteractable)GetField(motif.GetType(), motif, "_rayInteractable");
            if (motif is ObjectPlacementMotif o) o.OnInteractorUnselect(null, interactable);
            else ((PanelPlacementMotif)motif).OnInteractorUnselect(null, interactable);
            Assert.That(Drops.Count, Is.EqualTo(1), "one drop reported");
            return Drops[0];
        }

        /// <summary>The wall pose of the whiteboard on the room's wall z = 3 at <paramref name="x"/>.</summary>
        public Pose WallPose(float x)
        {
            foreach ((Vector2 a, Vector2 b, Vector2 inward) e in Room.WallEdges)
                if (Mathf.Abs(e.a.y - 3f) < 1e-3f && Mathf.Abs(e.b.y - 3f) < 1e-3f)
                    return WhiteboardMotif.WallPose(new Vector3(x, 0f, 3f), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
            Assert.Fail("no wall at z = 3");
            return default;
        }

        public void Dispose()
        {
            PlacementMotif.Dropped -= OnDropped;
            for (int i = Objects.Count - 1; i >= 0; --i) if (Objects[i] != null) Object.DestroyImmediate(Objects[i]);
            foreach (var preview in Object.FindObjectsByType<PlacementPreview>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(preview.gameObject);
            Objects.Clear();
        }
    }

    /// <summary>
    /// The drop preview: it shows exactly where the drop lands, for the chessboard (floor, v1 hover) and the whiteboard
    /// (wall snap, floor fallback), and leaves the held object where it is.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class BoardPlacementPreviewTests
    {
        private BoardRoom _room;
        private readonly List<string> Kinds = new List<string>();

        [SetUp]
        public void SetUp() { _room = BoardRoom.Create(); Kinds.Clear(); }

        [TearDown]
        public void TearDown() => _room.Dispose();

        private static void AssertSame(Pose expected, Pose actual, string what)
        {
            Assert.That(Vector3.Distance(expected.position, actual.position), Is.LessThan(1e-4f), what + ": position " + expected.position + " vs " + actual.position);
            Assert.That(Quaternion.Angle(expected.rotation, actual.rotation), Is.LessThan(0.01f), what + ": rotation");
        }

        // The preview of each held pose, then the real release from the same pose: the same landing, the same verdict.
        private void PreviewEqualsDrop(PlacementMotif motif, Pose held, bool onWall, string what)
        {
            _room.Hold(motif, held, onWall);
            // A new grab: the preview's inputs are prepared once.
            typeof(PlacementMotif).GetMethod("ResetPreview", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);
            PlacementDrop? preview = motif.RefreshPreview();
            Assert.That(preview.HasValue, Is.True, what);
            Transform t = motif == _room.ChessMotif ? _room.Chess.transform : _room.Whiteboard.transform;
            AssertSame(held, new Pose(t.position, t.rotation), what + ": the preview leaves the held object where it is");
            Assert.That(motif.PreviewInputs, Is.Not.Null, what + ": the room's inputs are prepared for the grab");
            bool visible = motif.Preview.Visible;
            Pose ghost = motif.Preview.GhostPose;
            Color colour = motif.Preview.Colour;
            int regionPoints = motif.Preview.RegionPointCount;
            PlacementDrop drop = _room.Release(motif);
            PlacementDrop pv = preview.Value;
            Kinds.Add(!pv.Approximate ? "exact" : pv.Placed ? "rough" : "unknown");
            Assert.That(pv.Settles, Is.EqualTo(drop.Settles), what + ": settles");
            if (!pv.Approximate)
            {
                // The preview found the drop's own answer: the same pose, the same verdict.
                Assert.That(pv.Placed, Is.EqualTo(drop.Placed), what + ": placed");
                Assert.That(pv.OnWall, Is.EqualTo(drop.OnWall), what + ": on a wall");
                AssertSame(drop.Landed, pv.Landed, what + ": the landing");
            }
            else if (pv.Placed)
            {
                // The coarse spot shown: the release lands exactly there (what the user saw), a valid spot.
                Assert.That(drop.Placed, Is.True, what + ": placed");
                AssertSame(drop.Landed, pv.Landed, what + ": the release lands at the hologram");
            }
            if (drop.Settles) Assert.That(colour, Is.EqualTo(PlacementPreview.ColourOf(pv)), what + ": its colour");
            if (drop.Settles && !(pv.Approximate && !pv.Placed))
            {
                Assert.That(visible, Is.True, what + ": the ghost is shown");
                AssertSame(pv.Landed, ghost, what + ": the ghost stands where the preview lands");
                Assert.That(regionPoints, Is.GreaterThanOrEqualTo(3), what + ": the region outline");
            }
            else Assert.That(visible, Is.False, what + ": no ghost (it stays where it is, or it goes farther than the preview looks)");
            Assert.That(motif.Preview.Visible, Is.False, what + ": the release hides it");
        }

        [Test]
        public void TheChessboardsPreviewIsItsDrop()
        {
            Quaternion yaw = Quaternion.Euler(0f, 30f, 0f);
            PreviewEqualsDrop(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), yaw), false, "open floor");
            PreviewEqualsDrop(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, 1.4f), yaw), false, "over the furniture");
            PreviewEqualsDrop(_room.ChessMotif, new Pose(new Vector3(2.95f, 0.6f, 0f), yaw), false, "against a wall");
            PreviewEqualsDrop(_room.ChessMotif, new Pose(new Vector3(0.2f, 0.6f, 0.9f), yaw), false, "at the furniture's edge");
            PreviewEqualsDrop(_room.ChessMotif, new Pose(new Vector3(0f, 1.5f, 0f), yaw), false, "too high above the floor");
            Assert.That(Kinds, Does.Contain("exact").And.Contain("unknown"), string.Join(",", Kinds));   // "rough" is TheChessboardsPreviewTellsValidPulledAndTooHigh
        }

        [Test]
        public void TheChessboardsPreviewTellsValidPulledAndTooHigh()
        {
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            PlacementDrop open = _room.ChessMotif.RefreshPreview().Value;
            Assert.That(open.Settles && open.Placed, Is.True);
            Assert.That(open.Landed.position.y, Is.EqualTo(0.5f).Within(1e-4f), "at v1's absolute hover height");

            // Toward the furniture until the region first touches it: a short pull, the coarse spot shown.
            PlacementDrop pulled = default;
            for (float z = -1.2f; z < 1.0f; z += 0.05f)
            {
                _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, z), Quaternion.identity));
                pulled = _room.ChessMotif.RefreshPreview().Value;
                if (pulled.Pulled || pulled.Approximate) break;
            }
            Assert.That(pulled.Placed && pulled.Pulled && pulled.Approximate, Is.True, "just over the furniture: pulled off it, to the coarse spot");
            Assert.That(_room.ChessMotif.Preview.Colour, Is.EqualTo(PlacementPreview.PulledColor));
            Assert.That(_room.ChessMotif.Preview.Visible, Is.True, "with its ghost");

            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 1.5f, -1.2f), Quaternion.identity));
            PlacementDrop high = _room.ChessMotif.RefreshPreview().Value;
            Assert.That(high.Settles && high.Placed, Is.True, "let go high, it is set on the floor (no snap distance limit any more)");
            Assert.That(high.Landed.position.y, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(_room.ChessMotif.Preview.Visible, Is.True);
        }

        [Test]
        public void TheWhiteboardsPreviewIsItsDrop()
        {
            PreviewEqualsDrop(_room.WhiteboardMotif, _room.WallPose(-1.5f), true, "on a wall");
            PreviewEqualsDrop(_room.WhiteboardMotif, _room.WallPose(2.95f), true, "on a wall at the corner");
            PreviewEqualsDrop(_room.WhiteboardMotif, _room.WallPose(0f), true, "on a wall behind the furniture");
            PreviewEqualsDrop(_room.WhiteboardMotif, new Pose(new Vector3(-1f, 1f, -1f), Quaternion.Euler(0f, 180f, 0f)), false, "on the open floor");
            PreviewEqualsDrop(_room.WhiteboardMotif, new Pose(new Vector3(0f, 1f, 1.4f), Quaternion.identity), false, "over the furniture");
        }

        [Test]
        public void AWhiteboardDroppedOnAWallLandsOnAWall()
        {
            _room.Hold(_room.WhiteboardMotif, _room.WallPose(-1.5f), true);
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed && drop.OnWall, Is.True);
            Assert.That(drop.Motif, Is.SameAs(_room.WhiteboardMotif));
        }

        [Test]
        public void ThePreviewIsThrottledAndHiddenOnRelease()
        {
            Assert.That(PlacementMotif.PreviewIntervalSeconds, Is.LessThanOrEqualTo(0.1f), "at most ten times a second");
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            _room.ChessMotif.RefreshPreview();
            Assert.That(_room.ChessMotif.Preview.Visible, Is.True);
            _room.Release(_room.ChessMotif);
            Assert.That(_room.ChessMotif.Preview.Visible, Is.False, "the drop hides it");
            Assert.That(_room.ChessMotif.PreviewInputs, Is.Null, "and the next grab prepares its inputs again");
        }
    }

    /// <summary>The preview's bounded search and the drop's edge cases.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class PlacementPreviewEdgeTests
    {
        private BoardRoom _room;

        [TearDown]
        public void TearDown() { _room?.Dispose(); _room = null; }

        private static void AssertSame(Pose a, Pose b, string what)
        {
            Assert.That(Vector3.Distance(a.position, b.position), Is.LessThan(1e-4f), what);
            Assert.That(Quaternion.Angle(a.rotation, b.rotation), Is.LessThan(0.01f), what);
        }

        [Test]
        public void ARoomTooSmallForTheChessboard_ShowsRed_AndTheDropFindsNoSpotEither()
        {
            _room = BoardRoom.Create(BoardRoom.EmptyRoom(0.2f));
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, 0f), Quaternion.identity));
            PlacementDrop preview = _room.ChessMotif.RefreshPreview().Value;
            Assert.That(preview.Settles, Is.True);
            Assert.That(preview.Placed, Is.False, "nothing fits");
            Assert.That(preview.Approximate, Is.False, "and the preview can say so: larger than the room every way it can turn");
            Assert.That(_room.ChessMotif.Preview.Colour, Is.EqualTo(PlacementPreview.UnfitColor));
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            Assert.That(drop.Placed, Is.False, "the full search agrees");
            Assert.That(_room.ChessMotif.Preview.Visible, Is.False, "there was no hologram");
            Assert.That(Vector3.Distance(drop.Landed.position, new Vector3(-2f, 0.5f, -2f)), Is.LessThan(1e-4f),
                "no valid spot and no hologram: back where it was before the grab (the less surprising of staying invalid or going back)");
        }

        // ---- the release lands where the preview showed

        // On device the hologram must show over passthrough. The Quest compositor lays the eye buffer over the passthrough
        // underlay by its ALPHA; a plain SrcAlpha blend wrote alpha * alpha (a 12% fill became 1.4%). The alpha channel now accumulates
        // coverage. Single-pass instanced stereo (OpenXR render mode 1) needs the instance and stereo macros in both stages.
        [Test]
        public void TheHologramShaderWritesCoverageAlphaForPassthrough_AndIsSinglePassInstancedStereo()
        {
            string shader = System.IO.File.ReadAllText("Assets/SpatialBlend/Interaction/Shaders/PlacementHologram.shader");
            Assert.That(shader, Does.Contain("Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha"), "alpha = a + dst * (1 - a)");
            foreach (string macro in new[] { "UNITY_VERTEX_INPUT_INSTANCE_ID", "UNITY_SETUP_INSTANCE_ID(v)", "UNITY_VERTEX_OUTPUT_STEREO",
                                             "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o)", "UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i)" })
                Assert.That(shader, Does.Contain(macro), macro);
            Assert.That(System.IO.File.ReadAllText("ProjectSettings/GraphicsSettings.asset"), Does.Contain("m_CustomRenderPipeline: {fileID: 0}"),
                "the built-in pipeline the CG shader is written for");
        }

        // ---- the release never lands somewhere other than where the preview shows

        // The chessboard carried along z (x = 0) until its hologram is first pulled (shown, amber): returns that hologram.
        private PlacementDrop FirstPulledHologram(float zFrom, float zTo)
        {
            for (float z = zFrom; z < zTo; z += 0.05f)
            {
                _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, z), Quaternion.identity));
                PlacementDrop d = _room.ChessMotif.RefreshPreview().Value;
                if (d.Pulled && _room.ChessMotif.Preview.Visible) return d;
            }
            Assert.Fail("no pulled hologram between z " + zFrom + " and " + zTo);
            return default;
        }

        // The preview runs at most every 0.1 s; the object moves on in between. A release then searched afresh from where it was let go
        // and could land on another spot than the hologram on screen.
        [Test]
        public void TheChessboardLandsAtTheHologramShown_EvenWhenItMovedSinceThePreviewTick()
        {
            _room = BoardRoom.Create();
            // Toward the furniture until the hologram is pulled off it.
            PlacementDrop shown = FirstPulledHologram(-1.2f, 1.0f);
            Vector3 at = _room.Chess.transform.position;
            // Moved on before the next tick (deeper over the furniture), then let go: a fresh search from here pulls somewhere else.
            _room.Hold(_room.ChessMotif, new Pose(at + new Vector3(0.3f, 0f, 0.25f), Quaternion.identity));
            PlacementDrop fresh = _room.ChessMotif.PlanDrop(false).Value;
            Assert.That(fresh.Pulled, Is.True, "not valid where it is let go");
            Assert.That(Vector3.Distance(fresh.Landed.position, shown.Landed.position), Is.GreaterThan(0.02f),
                "a fresh search from here would land elsewhere (what the release did before)");
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            Assert.That(drop.Placed, Is.True);
            AssertSame(shown.Landed, drop.Landed, "lands at the hologram the user saw");
        }

        [Test]
        public void AWhiteboardValidWhereItIsLetGo_LandsThere_NotAtAnOlderHologram()
        {
            _room = BoardRoom.Create();
            _room.Hold(_room.WhiteboardMotif, _room.WallPose(-1.5f), true);
            PlacementDrop shown = _room.WhiteboardMotif.RefreshPreview().Value;
            Assert.That(_room.WhiteboardMotif.Preview.Visible && shown.OnWall && !shown.Pulled, Is.True, "cyan: valid where it is");
            Pose moved = _room.WallPose(-1.1f);                                   // eased 40 cm along the wall since the tick, still valid
            _room.Hold(_room.WhiteboardMotif, moved, true);
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed && drop.OnWall, Is.True);
            AssertSame(moved, drop.Landed, "cyan means it lands where it is let go, not back at the hologram of 0.1 s before");
        }

        [Test]
        public void AWhiteboardThatWouldBeMoved_LandsAtTheAmberHologramShown()
        {
            // Furniture against the middle of the z = 3 wall: aimed there, it slides along the wall (an amber hologram).
            _room = BoardRoom.Create(BoardRoom.SquareRoom(3f, BoardRoom.Box(-0.6f, 2.5f, 0.6f, 3f)));
            PlacementDrop shown = default;
            for (float x = -1.2f; x < 0.1f; x += 0.05f)
            {
                _room.Hold(_room.WhiteboardMotif, _room.WallPose(x), true);
                shown = _room.WhiteboardMotif.RefreshPreview().Value;
                if (shown.Pulled && _room.WhiteboardMotif.Preview.Visible) break;
            }
            Assert.That(shown.Pulled && shown.OnWall, Is.True, "an amber hologram on the wall");
            Pose moved = _room.WallPose(_room.Whiteboard.transform.position.x + 0.2f);   // deeper behind the furniture since the tick
            _room.Hold(_room.WhiteboardMotif, moved, true);
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed && drop.OnWall, Is.True);
            AssertSame(shown.Landed, drop.Landed, "lands at the hologram the user saw");
        }

        // In a session the obstacles are the core tree's (RoomFit.Obstacles: the manager's RegionObstacles). The preview uses the same
        // source, so the hologram is clear of an obstacle only the core knows, and the pose the release writes is one the manager's own
        // fit accepts (its share-time check), so the core's refusal and its revert to the pose before the grab do not happen.
        [Test]
        public void InASession_TheHologramUsesTheCoresObstacles_AndTheReleaseLandsThereAtAPoseTheManagerAccepts()
        {
            _room = BoardRoom.Create(BoardRoom.EmptyRoom(3f));   // the lobby's room has no furniture
            var managerObject = new GameObject("session-manager");
            managerObject.SetActive(false);
            _room.Objects.Add(managerObject);
            var manager = managerObject.AddComponent<ManagerContainmentContractTests.ContainmentManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            manager.Walkable = new WalkableRegion(_room.Room.Ring, null);
            // An obstacle only the core tree has (a sibling region of the other room's shared object), not in the lobby room.
            Vector2[] coreOnly = BoardRoom.Furniture;
            manager.SiblingFootprints.Add(coreOnly);
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_room.App, manager);

            // Toward the core-only obstacle until the hologram is pulled off it (the lobby room has nothing there).
            PlacementDrop shown = FirstPulledHologram(-1.2f, 1.0f);
            Vector3 at = _room.Chess.transform.position;
            _room.Hold(_room.ChessMotif, new Pose(at + new Vector3(0.1f, 0f, 0.15f), Quaternion.identity));
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            AssertSame(shown.Landed, drop.Landed, "lands at the hologram");

            // The manager's own fit (its share-time check against the core tree's obstacles) at the landing.
            Transform t = _room.Chess.transform;
            var was = new Pose(t.position, t.rotation);
            t.SetPositionAndRotation(drop.Landed.position, drop.Landed.rotation);
            Assert.That(RoomFit.TryRegion(_room.Chess, out Vector2[] region), Is.True);
            RoomFit.TryFootprint(_room.Chess, out Vector2[] footprint);
            t.SetPositionAndRotation(was.position, was.rotation);
            var shape = new RegionPull.Shape { Region = region, RegionObstacles = manager.RegionObstacles(0), Footprint = footprint, FootprintObstacles = manager.RoomWalls };
            Assert.That(RegionPull.IsValid(shape, Vector2.zero, manager.Walkable.Boundary, SpatialBlendManager.RegionFitMarginMeters), Is.True,
                "valid by the session's rule: no refusal, no revert to before the grab");
        }

        // The panel's preview accepted a pose valid as is with a 0 margin while the release re-checked it with the manager's 1 cm: a
        // hologram that close to an obstacle failed at the release and the drop went elsewhere. One margin for both now.
        [Test]
        public void ThePreviewAndTheReleaseCheckWithOneMargin_NeverUnderTheManagersFitMargin()
        {
            _room = BoardRoom.Create();
            _room.Hold(_room.WhiteboardMotif, _room.WallPose(-1.5f), true);
            _room.WhiteboardMotif.RefreshPreview();
            Assert.That(_room.WhiteboardMotif.PreviewInputs.Margin, Is.GreaterThanOrEqualTo(SpatialBlendManager.RegionFitMarginMeters));
        }

        [Test]
        public void LetGoHigh_ItLandsAtTheHologramOnTheFloor_NotInTheAirOrBackAtTheStart()
        {
            _room = BoardRoom.Create();
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0.4f, 1.6f, -1.0f), Quaternion.identity));
            PlacementDrop shown = _room.ChessMotif.RefreshPreview().Value;
            Assert.That(_room.ChessMotif.Preview.Visible, Is.True);
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            Assert.That(drop.Settles && drop.Placed, Is.True);
            AssertSame(shown.Landed, drop.Landed, "where the hologram was");
        }

        [Test]
        public void WhenTheHologramIsNoLongerValidAtTheRelease_ItIsNotPlacedThere_ItGoesBack()
        {
            _room = BoardRoom.Create();
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            PlacementDrop shown = _room.ChessMotif.RefreshPreview().Value;
            Assert.That(_room.ChessMotif.Preview.Visible, Is.True);
            // Between the last preview and the release the room changed (a session's refusal, a room re-registered): nothing fits now,
            // and the object moved a little since the hologram was computed.
            typeof(FreeformCollaborationApp).GetField("_localRoom", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_room.App, BoardRoom.EmptyRoom(0.2f));
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0.05f, 0.6f, -1.2f), Quaternion.identity));
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            Assert.That(drop.Placed, Is.False, "the hologram, checked again with the room as it is now, is not valid: not placed there");
            Assert.That(drop.Reverted, Is.True);
            Assert.That(drop.OffFloor, Is.False);
            Assert.That(Vector3.Distance(drop.Landed.position, new Vector3(-2f, 0.5f, -2f)), Is.LessThan(1e-4f), "and the full search found nothing: back to before the grab");
        }

        [Test]
        public void LetGoWhereTheHologramWasComputed_ItLandsExactlyThere_EvenACoarseSpot()
        {
            _room = BoardRoom.Create();
            PlacementDrop shown = default;
            for (float z = -1.2f; z < 1.0f; z += 0.05f)
            {
                _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, z), Quaternion.identity));
                shown = _room.ChessMotif.RefreshPreview().Value;
                if (shown.Approximate) break;
            }
            Assert.That(shown.Approximate && shown.Placed, Is.True, "a coarse spot shown");
            PlacementDrop drop = _room.Release(_room.ChessMotif);
            AssertSame(shown.Landed, drop.Landed, "exactly what the user saw");
        }

        // ---- the hologram

        [Test]
        public void TheHologramIsAVisualCopyOnly_OneSharedMaterial_TintedByTheState_AndGoesWithTheGrab()
        {
            _room = BoardRoom.Create();
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            _room.ChessMotif.RefreshPreview();
            Transform ghost = _room.ChessMotif.Preview.Ghost;
            Assert.That(ghost, Is.Not.Null);
            int meshes = _room.Chess.GetComponentsInChildren<MeshRenderer>().Count(r => r.enabled && r.gameObject.activeInHierarchy);
            MeshRenderer[] copies = ghost.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(copies.Length, Is.EqualTo(meshes), "one copy per visible mesh");
            foreach (Component c in ghost.GetComponentsInChildren<Component>(true))
                Assert.That(c is Transform || c is MeshFilter || c is MeshRenderer, Is.True, c.GetType().Name + ": no collider, no behaviour, no network");
            Material shared = PlacementPreview.HologramMaterial();
            Assert.That(shared.shader.name, Is.EqualTo(PlacementPreview.HologramShader));
            Assert.That(copies.SelectMany(r => r.sharedMaterials).All(m => m == shared), Is.True, "one shared material");
            Assert.That(ghost.GetComponentsInChildren<MeshFilter>().All(f => f.sharedMesh != null), Is.True, "the object's shared meshes");
            var block = new MaterialPropertyBlock();
            copies[0].GetPropertyBlock(block);
            AssertColour(block.GetColor("_Color"), PlacementPreview.ValidColor, "valid where it is: soft cyan");
            AssertSame(new Pose(ghost.position, ghost.rotation), _room.ChessMotif.Preview.GhostPose, "at the landing");

            // Pulled: amber; the same copy, moved (built once per grab).
            for (float z = -1.2f; z < 1.0f; z += 0.05f)
            {
                _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, z), Quaternion.identity));
                if (_room.ChessMotif.RefreshPreview().Value.Pulled) break;
            }
            Assert.That(_room.ChessMotif.Preview.Ghost, Is.SameAs(ghost), "the copy is reused within the grab");
            copies[0].GetPropertyBlock(block);
            AssertColour(block.GetColor("_Color"), PlacementPreview.PulledColor, "will be moved: amber");

            _room.Release(_room.ChessMotif);
            Assert.That(ghost == null, Is.True, "destroyed on the release");
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            _room.ChessMotif.RefreshPreview();
            Transform second = _room.ChessMotif.Preview.Ghost;
            Assert.That(second, Is.Not.Null.And.Not.SameAs(ghost), "the next grab builds its own");
            typeof(PlacementMotif).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_room.ChessMotif, null);
            Assert.That(second == null, Is.True, "destroyed when the motif is disabled");
        }

        private static void AssertColour(Color actual, Color expected, string what) =>
            Assert.That(new[] { actual.r, actual.g, actual.b, actual.a }, Is.EqualTo(new[] { expected.r, expected.g, expected.b, expected.a }).Within(1e-3f), what);

        [Test]
        public void TheRegionOutlineIsThinAndFaint_AndNothingFitsShowsNoHologram()
        {
            Assert.That(PlacementPreview.LineWidth, Is.LessThanOrEqualTo(0.003f));
            Assert.That(PlacementPreview.RegionAlpha, Is.LessThanOrEqualTo(0.4f));
            _room = BoardRoom.Create(BoardRoom.EmptyRoom(0.2f));
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, 0f), Quaternion.identity));
            _room.ChessMotif.RefreshPreview();
            Assert.That(_room.ChessMotif.Preview.Visible, Is.False, "no hologram where nothing fits");
            Assert.That(_room.ChessMotif.Preview.RegionVisible, Is.True, "a faint red outline");
            Assert.That(_room.ChessMotif.Preview.Colour, Is.EqualTo(PlacementPreview.UnfitColor));
        }

        [Test]
        public void ASearchThatFoundNothing_IsNotRepeatedInTheGrab()
        {
            _room = BoardRoom.Create(BoardRoom.EmptyRoom(0.2f));
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0f, 0.6f, 0f), Quaternion.identity));
            _room.ChessMotif.RefreshPreview();
            Assert.That(_room.ChessMotif.PreviewInputs.NoSpot, Is.True, "remembered for the grab");
            _room.Hold(_room.ChessMotif, new Pose(new Vector3(0.1f, 0.6f, 0f), Quaternion.Euler(0f, 40f, 0f)));
            Assert.That(_room.ChessMotif.RefreshPreview().Value.Placed, Is.False, "whatever the yaw, the answer is remembered");
        }

        [Test]
        public void WhereNoWallFitsTheWhiteboard_ItStandsOnTheFloor_AndThatCountsForTheTask()
        {
            // Furniture along every wall: no wall spot for the board's standing region; the open middle is free.
            _room = BoardRoom.Create(BoardRoom.SquareRoom(3f,
                BoardRoom.Box(-3f, 2.2f, 3f, 3f), BoardRoom.Box(-3f, -3f, 3f, -2.2f), BoardRoom.Box(-3f, -2.2f, -2.2f, 2.2f), BoardRoom.Box(2.2f, -2.2f, 3f, 2.2f)));
            Assert.That(_room.App.AnyWallFits(_room.Whiteboard, _room.WhiteboardMotif, SpatialBlendManager.RegionFitMarginMeters), Is.False);
            _room.Hold(_room.WhiteboardMotif, _room.WallPose(0f), true);
            PlacementDrop preview = _room.WhiteboardMotif.RefreshPreview().Value;
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed, Is.True, "pulled onto the open floor");
            Assert.That(drop.OnWall, Is.False);
            Assert.That(preview.OnWall, Is.False, "the preview does not show it on a wall either");
            Assert.That(PlacementPreview.ColourOf(preview), Is.EqualTo(PlacementPreview.PulledColor), "it will be moved");
            var verdict = Samples.FreeformCollaboration.Tutorial.TutorialController.JudgeBoardDrop(false, true, drop,
                () => _room.App.AnyWallFits(_room.Whiteboard, _room.WhiteboardMotif, SpatialBlendManager.RegionFitMarginMeters));
            Assert.That(verdict.signal, Is.EqualTo(Samples.FreeformCollaboration.Tutorial.TutorialSignal.WhiteboardPlaced), "the floor fallback is the valid pose here");
        }

        [Test]
        public void AWhiteboardAimedAtAWallWhoseFreeSpotIsFarther_ShowsAmberWithNoGhost_AndTheDropSlidesThere()
        {
            // Furniture against the middle of the z = 3 wall: the nearest free wall spot from its middle is over a metre away.
            _room = BoardRoom.Create(BoardRoom.SquareRoom(3f, BoardRoom.Box(-1.5f, 2.5f, 1.5f, 3f)));
            _room.Hold(_room.WhiteboardMotif, _room.WallPose(0f), true);
            PlacementDrop preview = _room.WhiteboardMotif.RefreshPreview().Value;
            Assert.That(preview.Approximate && !preview.Placed, Is.True, "farther than the preview looks: not known where");
            Assert.That(_room.WhiteboardMotif.Preview.Visible, Is.False, "no ghost (never one on the floor while the drop slides along the wall)");
            Assert.That(_room.WhiteboardMotif.Preview.RegionVisible, Is.True);
            Assert.That(_room.WhiteboardMotif.Preview.Colour, Is.EqualTo(PlacementPreview.PulledColor));
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed && drop.OnWall, Is.True, "the drop slides along the wall");
            Assert.That(Mathf.Abs(drop.Landed.position.x), Is.GreaterThan(PlacementMotifReach()), "to a spot beyond the preview's reach");
        }

        private static float PlacementMotifReach() => RoomFit.PreviewReachMeters;

        [Test]
        public void AValidPoseMidRoomIsNotOnAWall_EvenWhenTheGrabLastAimedAtOne()
        {
            _room = BoardRoom.Create();
            // Released while easing from a wall toward the user: valid where it is, the wall's facing, but a metre from any wall.
            Pose wall = _room.WallPose(-1.5f);
            _room.Hold(_room.WhiteboardMotif, new Pose(wall.position + new Vector3(0f, 0f, -1.5f), wall.rotation), true);
            PlacementDrop drop = _room.Release(_room.WhiteboardMotif);
            Assert.That(drop.Placed, Is.True);
            Assert.That(drop.OnWall, Is.False, "on a wall means at a wall");
            _room.Hold(_room.WhiteboardMotif, wall, true);
            Assert.That(_room.Release(_room.WhiteboardMotif).OnWall, Is.True);
        }

        [Test]
        public void ThePreviewIsComputedOnlyWhenDueAndMoved_AndItsInputsAreTakenAgainEachSecond()
        {
            _room = BoardRoom.Create();
            PlacementMotif motif = _room.ChessMotif;
            _room.Hold(motif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            motif.TickPreview(10f);
            Assert.That(motif.PreviewComputations, Is.EqualTo(1));
            RoomFit.Prepared inputs = motif.PreviewInputs;
            motif.TickPreview(10.05f);
            Assert.That(motif.PreviewComputations, Is.EqualTo(1), "not due yet");
            motif.TickPreview(10.2f);
            Assert.That(motif.PreviewComputations, Is.EqualTo(1), "due, but the object did not move");
            _room.Chess.transform.position += new Vector3(0.05f, 0f, 0f);
            motif.TickPreview(10.3f);
            Assert.That(motif.PreviewComputations, Is.EqualTo(2), "moved");
            Assert.That(motif.PreviewInputs, Is.SameAs(inputs), "the inputs are reused within the second");
            motif.TickPreview(11.1f);
            Assert.That(motif.PreviewComputations, Is.EqualTo(3), "a second on: the inputs are taken again (a remote object may have moved)");
            Assert.That(motif.PreviewInputs, Is.Not.SameAs(inputs));
        }

        [Test]
        public void AnObjectWithNoAuthoredRegion_ShowsNoPreview()
        {
            _room = BoardRoom.Create();
            // The preset object without its spatial authoring: an object that declares no region (its drop falls back to its collider).
            GameObject plain = _room.Spawn("Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab", "Plain");
            foreach (SbSpatialAuthoring authoring in plain.GetComponentsInChildren<SbSpatialAuthoring>(true))
                Object.DestroyImmediate(authoring);
            PlacementMotif motif = _room.Wire(plain);
            Assert.That(RoomFit.TryRegion(plain, out _), Is.False, "the object declares no region (its drop falls back to its collider)");
            if (motif is ObjectPlacementMotif o) o.FloorSource = _room.ChessMotif.FloorSource;
            _room.Hold(motif, new Pose(new Vector3(0f, 0.6f, -1.2f), Quaternion.identity));
            motif.RefreshPreview();
            Assert.That(motif.PreviewInputs, Is.Null);
            Assert.That(motif.Preview.Visible, Is.False, "no red ghost for what the drop still places");
        }
    }

    /// <summary>What a board's drop means for the tutorial's "Place the boards" task.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class BoardDropVerdictTests
    {
        private static PlacementDrop Drop(bool settles, bool placed, bool onWall) =>
            new PlacementDrop(null, Pose.identity, Pose.identity, settles, placed, onWall);

        [Test]
        public void TheChessboardCountsOnAValidDrop()
        {
            Assert.That(TutorialController.JudgeBoardDrop(true, false, Drop(true, true, false), null), Is.EqualTo(((TutorialSignal?)TutorialSignal.ChessPlaced, (string)null)));
            Assert.That(TutorialController.JudgeBoardDrop(true, false, Drop(false, false, false), null).hint, Is.EqualTo(TutorialController.LowerItText));
            Assert.That(TutorialController.JudgeBoardDrop(true, false, Drop(true, false, false), null).hint, Is.EqualTo(TutorialController.NoSpotText));
        }

        [Test]
        public void TheWhiteboardCountsOnAWall_OrOnTheFloorWhereNoWallFits()
        {
            Assert.That(TutorialController.JudgeBoardDrop(false, true, Drop(true, true, true), () => true).signal, Is.EqualTo(TutorialSignal.WhiteboardPlaced));
            var floor = TutorialController.JudgeBoardDrop(false, true, Drop(true, true, false), () => true);
            Assert.That(floor.signal, Is.Null, "a wall fits it: hang it there");
            Assert.That(floor.hint, Is.EqualTo(TutorialController.HangItText));
            Assert.That(TutorialController.JudgeBoardDrop(false, true, Drop(true, true, false), () => false).signal, Is.EqualTo(TutorialSignal.WhiteboardPlaced),
                "no wall fits it in this room: the floor is its valid pose");
        }

        [Test]
        public void ALetGoWithNoFloorUnderIt_SaysToPointAtTheFloor_NotThatNothingFits()
        {
            var off = new PlacementDrop(null, Pose.identity, Pose.identity, true, false, false, false, true, true);
            Assert.That(TutorialController.JudgeBoardDrop(true, false, off, null).hint, Is.EqualTo(TutorialController.LowerItText));
            var nowhere = new PlacementDrop(null, Pose.identity, Pose.identity, true, false, false, false, true, false);
            Assert.That(TutorialController.JudgeBoardDrop(true, false, nowhere, null).hint, Is.EqualTo(TutorialController.NoSpotText));
        }

        [Test]
        public void AnotherObjectsDropIsIgnored()
        {
            Assert.That(TutorialController.JudgeBoardDrop(false, false, Drop(true, true, false), null), Is.EqualTo(((TutorialSignal?)null, (string)null)));
        }
    }
}
#endif
