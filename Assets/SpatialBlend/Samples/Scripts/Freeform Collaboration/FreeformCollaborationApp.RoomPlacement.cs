// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The room and the placement of things in it ("simple room"):
    /// <list type="bullet">
    /// <item>The room is read from MRUK once when it loads (<see cref="MrukRoomRegion"/>): the floor grown by a margin, every anchor
    /// and its stand-off, the walls. Before a session it is what placement checks against; in a session it is registered with
    /// the core as the room's Navigable polygon with the walls as obstacles (<see cref="SpatialBlendManager.RegisterRoomRegion"/>).
    /// The NavMesh is only the avatar's walking surface.</item>
    /// <item>Validity is computed once, when the room loads (first spawn) and on a drop, never polled and never during a grab.
    /// Nothing is refused: a region that does not fit is pulled to the nearest spot that does (<see cref="RoomFit"/>).</item>
    /// <item>Furniture: the host shares its marked furniture (by default the single biggest TABLE, <see cref="FurnitureSharePolicy"/>;
    /// the per-object "Share when connected" menu changes that), largest first; a joiner links each to its own of the same label.</item>
    /// </list>
    /// </summary>
    public partial class FreeformCollaborationApp
    {
        private RoomRegion _localRoom;
        private MRUKRoom _localRoomSource;
        private FurnitureShareMenu _furnitureMenu;

        /// <summary>The room placement checks against: the registered one in a session, the one read from MRUK before.</summary>
        public override RoomRegion PlacementRoom => base.PlacementRoom ?? _localRoom;

        /// <summary>The floor of this room (the MRUK floor anchor's height), where a drop preview draws a region.</summary>
        public override float PlacementFloorY => CurrentFloorHeight();

        /// <summary>The virtual objects whose regions a placement keeps clear of: every authored object but avatars and furniture.</summary>
        public override IEnumerable<GameObject> PlacementOthers()
        {
            foreach (SbSpatialAuthoring authoring in FindObjectsByType<SbSpatialAuthoring>(FindObjectsSortMode.None))
            {
                if (authoring == null || !authoring.isActiveAndEnabled) continue;
                if (authoring.GetComponentInParent<SbNetworkAvatarEntity>() != null) continue;
                if (authoring.GetComponentInParent<SbMRUKAnchorEntity>() != null) continue;
                SbEntity entity = authoring.GetComponentInParent<SbEntity>();
                yield return entity != null ? entity.gameObject : authoring.gameObject;
            }
        }

        /// <summary>The room this app is in: MRUK's current room; a test hands its own (an exported MRUK sample room).</summary>
        internal System.Func<MRUKRoom> RoomProvider { get; set; }

        // The confirmed room (MrukRoomTracker): the same one the anchor catalog reads, so registration and the catalog agree.
        private MRUKRoom CurrentRoom() =>
            RoomProvider != null ? RoomProvider() : MrukRoomTracker.Current(MRUK.Instance);

        private MRUKRoom _furnitureRoom;
        private int _furnitureAnchorCount = -1;
        private List<Vector2[]> _furniture;

        /// <summary>The room's furniture now, anchors that arrived after the registration included (read again when the room or its anchor count changes).</summary>
        public override IReadOnlyList<Vector2[]> PlacementFurniture()
        {
            MRUKRoom room = CurrentRoom();
            if (room == null) return base.PlacementFurniture();
            int count = room.Anchors != null ? room.Anchors.Count : 0;
            if (room != _furnitureRoom || count != _furnitureAnchorCount || _furniture == null)
            {
                _furnitureRoom = room;
                _furnitureAnchorCount = count;
                _furniture = MrukRoomRegion.Furniture(room);
            }
            return _furniture;
        }

        private bool InSharedSession() =>
            _spaceManager != null && _spaceManager.ConnectionState == SpatialBlendConnectionState.Connected;

        /// <summary>
        /// MRUK loaded a room: it is read once, the virtual objects are first placed in front of the user (pulled to valid spots), and
        /// the furniture menu offers its anchors. A desktop observer and Foundation Local have no room of their own here.
        /// </summary>
        private void OnRoomLoaded()
        {
            if (IsDesktopObserver || UsesFoundationLocal || _isDestroying) return;
            MRUKRoom room = CurrentRoom();
            if (room == null || room == _localRoomSource) return;
            _localRoomSource = room;
            _localRoom = MrukRoomRegion.Build(room);
            if (_localRoom == null) Debug.LogWarning("[RoomPlacement] The MRUK room has no usable floor outline.");
            PlaceVirtualObjectsInFront();
            EnsureFurnitureMenu();
        }

        /// <summary>
        /// First spawn: every virtual scene object with a placement motif is put in front of the user (its motif's
        /// <see cref="PlacementMotif.SpawnPose"/>) and pulled to the nearest valid spot. Objects already in the session are left alone.
        /// </summary>
        internal void PlaceVirtualObjectsInFront()
        {
            if (PlacementRoom == null) return;
            var view = CurrentUserView();
            Vector3 eye = view.HasValue ? view.Value.eye : Vector3.zero;
            Vector3 forward = view.HasValue ? view.Value.forward : Vector3.forward;
            var spawn = new List<(GameObject, PlacementMotif)>();
            foreach (SbNetworkEntity entity in FindObjectsByType<SbNetworkEntity>(FindObjectsSortMode.InstanceID))
            {
                if (entity == null || entity.IsAttached() || entity.GetComponent<SbNetworkAvatarEntity>() != null) continue;
                PlacementMotif motif = entity.GetComponentInChildren<PlacementMotif>(true);
                if (motif != null) spawn.Add((entity.gameObject, motif));
            }
            var spawned = new HashSet<GameObject>(spawn.Select(o => o.Item1));
            var reasons = new Dictionary<GameObject, string>();
            foreach (GameObject failed in RoomFit.SpawnAll(spawn, eye, forward, PlacementRoom, _spaceManager,
                         PlacementOthers().Where(o => !spawned.Contains(o)), SpatialBlendManager.RegionFitMarginMeters, reasons))
                Debug.LogWarning($"[RoomPlacement] '{failed.name}' has no valid spot in this room; it stays in front of the user.");
            foreach (var pair in reasons.OrderBy(p => p.Key.name, System.StringComparer.Ordinal))
            {
                SbTrace.Line($"[RoomPlacement] initial '{pair.Key.name}': {pair.Value} at {pair.Key.transform.position}");
                _initialPoses[pair.Key] = new Pose(pair.Key.transform.position, pair.Key.transform.rotation);
            }
        }

        /// <summary>
        /// Before the scene's boards are shared (OnAppStart): every one still at a pose the one rule rejects (a scene or prefab pose
        /// no first spawn replaced: on a device the network boards may appear after the room loaded) gets the same initial
        /// placement as a first spawn, so the share never moves anything. A wall-mounted board not on
        /// a wall counts as invalid when some wall fits it. Wall-mounted boards first, then by name: deterministic.
        /// </summary>
        // The poses the first spawn chose (by the whole initial rule, wall or floor): a board still there is not second-guessed at the
        // app start, only re-checked for validity.
        private readonly Dictionary<GameObject, Pose> _initialPoses = new Dictionary<GameObject, Pose>();

        private bool AtItsInitialPose(GameObject go) =>
            _initialPoses.TryGetValue(go, out Pose p) && (go.transform.position - p.position).sqrMagnitude < 1e-6f &&
            Quaternion.Angle(go.transform.rotation, p.rotation) < 0.1f;

        internal void PlaceInvalidVirtualObjects()
        {
            if (PlacementRoom == null) return;
            var view = CurrentUserView();
            Vector3 eye = view.HasValue ? view.Value.eye : Vector3.zero;
            Vector3 forward = view.HasValue ? view.Value.forward : Vector3.forward;
            var boards = new List<(GameObject go, PlacementMotif motif, bool wall)>();
            foreach (SbNetworkEntity entity in FindObjectsByType<SbNetworkEntity>(FindObjectsSortMode.None))
            {
                if (entity == null || entity.IsAttached() || entity.GetComponent<SbNetworkAvatarEntity>() != null ||
                    SpatialBlendManager.IsPhysicalObject(entity.gameObject)) continue;
                PlacementMotif motif = entity.GetComponentInChildren<PlacementMotif>(true);
                if (motif != null) boards.Add((entity.gameObject, motif, motif.WallPose(Vector3.zero, Vector3.forward).HasValue));
            }
            boards = boards.OrderByDescending(b => b.wall).ThenBy(b => b.go.name, System.StringComparer.Ordinal).ToList();
            foreach ((GameObject go, PlacementMotif motif, bool wall) in boards)
            {
                var others = PlacementOthers().Where(o => o != go && !o.transform.IsChildOf(go.transform)).ToList();
                // Valid, and (wall-mounted) on a wall, or standing on the floor because no wall fits (the floor fallback): kept.
                if (RoomFit.IsValid(go, PlacementRoom, _spaceManager, others, SpatialBlendManager.RegionFitMarginMeters) &&
                    (!wall || OnAWall(go.transform.position) || AtItsInitialPose(go) ||
                     !RoomFit.AnyWallFits(go, motif, PlacementRoom, _spaceManager, others, SpatialBlendManager.RegionFitMarginMeters)))
                    continue;
                Vector3 was = go.transform.position;
                if (RoomFit.InitialPlace(go, motif, eye, forward, PlacementRoom, _spaceManager, others,
                        SpatialBlendManager.RegionFitMarginMeters, out string reason))
                    SbTrace.Line($"[RoomPlacement] initial '{go.name}' (its pose {was} was not valid): {reason} at {go.transform.position}");
                else
                    Debug.LogWarning($"[RoomPlacement] '{go.name}' has no valid spot in this room; it stays at {was}.");
            }
        }

        // Whether a point stands within 15 cm of a wall's bottom edge (a wall-mounted board on its wall).
        private bool OnAWall(Vector3 p)
        {
            var q = new Vector2(p.x, p.z);
            foreach ((Vector2 a, Vector2 b, Vector2 inward) e in PlacementRoom.WallEdges)
            {
                Vector2 ab = e.b - e.a;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(q - e.a, ab) / ab.sqrMagnitude) : 0f;
                if (Vector2.Distance(q, e.a + ab * t) <= 0.15f) return true;
            }
            return false;
        }

        /// <summary>
        /// A received copy of a virtual object (the host's Chess or Whiteboard arriving as new here) is placed by the same initial
        /// rule as a first spawn: a wall-mounted one on this room's best wall stretch, any other one on the open
        /// floor at its hover height, at the valid spot nearest where it arrived. Its pose is set before its region is written.
        /// </summary>
        internal void PlaceReceivedCopy(GameObject copy)
        {
            // Only the scene's boards (network objects); a received scan has its own placement flow (ReceivedCopy).
            if (copy == null || PlacementRoom == null || SpatialBlendManager.IsPhysicalObject(copy) ||
                copy.GetComponent<SbNativeEntity>() != null || copy.GetComponent<SbNetworkEntity>() == null) return;
            PlacementMotif motif = copy.GetComponentInChildren<PlacementMotif>(true);
            if (motif == null) return;
            Vector3 arrived = copy.transform.position;
            var others = PlacementOthers().Where(o => o != copy && !o.transform.IsChildOf(copy.transform)).ToList();
            string reason;
            bool placed;
            if (motif.WallPose(Vector3.zero, Vector3.forward).HasValue)
            {
                var view = CurrentUserView();
                Vector3 eye = view.HasValue ? view.Value.eye : arrived;
                Vector3 forward = view.HasValue ? view.Value.forward : Vector3.forward;
                placed = RoomFit.InitialPlace(copy, motif, eye, forward, PlacementRoom, _spaceManager, others,
                    SpatialBlendManager.RegionFitMarginMeters, out reason);
            }
            else
            {
                copy.transform.position = new Vector3(arrived.x, motif.SpawnHeight, arrived.z);
                // THE placement (RoomFit.Place), against the obstacles the share-time check uses (the core tree in a session), so
                // the authoring that follows finds it valid and never moves it again (the joiner case).
                placed = RoomFit.Place(copy, PlacementRoom, _spaceManager, others, SpatialBlendManager.RegionFitMarginMeters, null, null, out reason);
            }
            if (placed) SbTrace.Line($"[RoomPlacement] received '{copy.name}': {reason} at {copy.transform.position}");
            else Debug.LogWarning($"[RoomPlacement] received '{copy.name}' has no valid spot in this room.");
        }

        /// <summary>
        /// The host shares its marked furniture (by default the single biggest TABLE), largest first within each label, so a joiner's
        /// largest of the label links to the host's largest. A joiner shares none of its own: it links to the host's.
        /// </summary>
        private void ShareChosenRoomAnchors()
        {
            if (IsDesktopObserver || _spaceManager == null || !_spaceManager.IsHost()) return;
            List<SceneAnchorRecord> records = ShareableAnchors();
            FurnitureSharePolicy.ApplyDefaults(records);
            records.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Label, b.Label);
                return c != 0 ? c : FurnitureSharePolicy.CompareDesks(a, b);
            });
            foreach (SceneAnchorRecord record in records)
            {
                if (FurnitureSharePolicy.WillShare(record)) TryShareAnchor(record);
            }
        }

        /// <summary>The session ended: the furniture menu is populated again for the room the user is in.</summary>
        internal void OnReturnedToLobby()
        {
            if (IsDesktopObserver || UsesFoundationLocal || _isDestroying || CurrentRoom() == null) return;
            EnsureFurnitureMenu();
        }

        private void EnsureFurnitureMenu()
        {
            if (InSharedSession()) return;
            if (_furnitureMenu == null)
            {
                var host = new GameObject("FurnitureShareMenu");
                _furnitureMenu = host.AddComponent<FurnitureShareMenu>();
                _furnitureMenu.InSession = InSharedSession;
            }
            List<SceneAnchorRecord> records = ShareableAnchors();
            FurnitureSharePolicy.ApplyDefaults(records);
            _furnitureMenu.SetAnchors(records);
            _furnitureMenu.gameObject.SetActive(true);
        }
    }
}
