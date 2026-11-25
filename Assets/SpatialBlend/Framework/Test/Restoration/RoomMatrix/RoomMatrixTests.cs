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
using System.Text;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests.RoomMatrix
{
    /// <summary>
    /// The room matrix: Spacing must work in every case, and shared objects must always exist at valid places in
    /// each space. Every MRUK sample room (exported by <see cref="MrukRoomExport"/> through MRUK's own loaders) is rebuilt as an
    /// MRUKRoom with its MRUKAnchors and driven through the PRODUCT's code against the REAL native core: the app's room registration
    /// (FreeformCollaborationApp.TryRegisterCurrentRoomGeometry: MrukRoomRegion, SpatialBlendManager.RegisterRoomRegion, the MRUK
    /// scene-geometry adapter, the host's furniture share), the first-spawn placement of Chess and the Whiteboard (their prefabs'
    /// PlacementMotif.SpawnPose and RoomFit, as FreeformCollaborationApp.PlaceVirtualObjectsInFront), the manager's share-time fit and
    /// authoring (TryFitAuthoredObjectIntoWalkable, TryApplySpatialAuthoring: the calls ShareGameObject makes, with the shared ids
    /// Fusion's network ids give them), the joiner's link of the host's furniture (the app's OnShare), the avatar share and its
    /// relations (ShareGameObject, RegisterRelation with FreeformCollaborationApp.AvatarRelationBindings), and the receiver's
    /// proposals (sb_read_proposals). Two managers run in this process, hosted and joined over loopback TCP (the LAN path).
    /// Invariants: I0 no core rejection of any kind; I1 the room and every anchor accepted; I2 Chess and the Whiteboard at valid,
    /// accepted spots from three user poses; I3 free space non-empty with the start pose in it; I4 the shared objects link; I5 the
    /// receiver's Spacing proposals have a shared referent, a target in free space, and preserve the distance.
    /// Results: <see cref="MrukRoomExport.ExportDirectory"/>/results-*.tsv.
    /// </summary>
    [TestFixture]
    [Category("RoomMatrix")]
    [Explicit("The room matrix runs only when asked for (its category or name): it is minutes long and needs the exported rooms.")]
    public sealed class RoomMatrixTests
    {
        private const string ChessPrefab = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private const string WhiteboardPrefab = "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab";
        // Fusion's network ids 1 and 2 (SpatialBlendManager.ResolveSharedElementId: V2NetworkSharedIdBase + raw id).
        private const long ChessId = 1000000000000L + 1;
        private const long WhiteboardId = 1000000000000L + 2;
        /// <summary>The core's body radius: the root keeps this from every obstacle (ObstacleFreeSpace).</summary>
        private const float BodyRadius = 0.2f;
        /// <summary>
        /// The Spacing tolerance on the core's own spacing error at the target (err_spacing_target, metres). 5 cm: the solver's
        /// convergence plus the single-precision round trip of two rooms' coordinates is well under it (same-room pairs measure
        /// 0.000-0.014 m), while a target the solver had to move off an obstacle or a wall shows tens of centimetres.
        /// </summary>
        private const float SpacingTolerance = 0.05f;
        private static int s_port = 47900;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private readonly List<string> _errors = new List<string>();
        private readonly List<Peer> _livePeers = new List<Peer>();
        private int _editorProfile = int.MinValue;

        // ---------------------------------------------------------------- rooms

        private static List<MrukRoomExport.RoomData> LoadRooms()
        {
            string dir = MrukRoomExport.ExportDirectory;
            Assert.That(Directory.Exists(dir), Is.True, "run the RoomMatrixExport category first: " + dir);
            var rooms = Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => JsonUtility.FromJson<MrukRoomExport.RoomData>(File.ReadAllText(p))).ToList();
            string filter = Environment.GetEnvironmentVariable("SPATIALBLEND_ROOM_MATRIX_FILTER");
            if (!string.IsNullOrEmpty(filter))
                rooms = rooms.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.name, filter)).ToList();
            return rooms;
        }

        private static readonly PropertyInfo AnchorLabel = typeof(MRUKAnchor).GetProperty("Label");
        private static readonly PropertyInfo AnchorRect = typeof(MRUKAnchor).GetProperty("PlaneRect");
        private static readonly PropertyInfo AnchorVolume = typeof(MRUKAnchor).GetProperty("VolumeBounds");
        private static readonly PropertyInfo AnchorBoundary = typeof(MRUKAnchor).GetProperty("PlaneBoundary2D");
        private static readonly PropertyInfo AnchorRoom = typeof(MRUKAnchor).GetProperty("Room");
        private static readonly PropertyInfo RoomFloor = typeof(MRUKRoom).GetProperty("FloorAnchor");
        private static readonly PropertyInfo RoomCeiling = typeof(MRUKRoom).GetProperty("CeilingAnchor");

        /// <summary>The exported room as MRUK objects: an MRUKRoom and one MRUKAnchor per anchor, exactly as MRUK held them.</summary>
        private MRUKRoom Build(MrukRoomExport.RoomData data, string tag)
        {
            var root = new GameObject($"room-{tag}-{data.name}");
            root.SetActive(false);
            _objects.Add(root);
            MRUKRoom room = root.AddComponent<MRUKRoom>();
            foreach (MrukRoomExport.AnchorData a in data.anchors)
            {
                var go = new GameObject(a.name);
                go.SetActive(false);
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(a.position, a.rotation);
                MRUKAnchor anchor = go.AddComponent<MRUKAnchor>();
                AnchorLabel.SetValue(anchor, (MRUKAnchor.SceneLabels)a.label);
                AnchorRect.SetValue(anchor, a.hasPlane ? (Rect?)a.planeRect : null);
                AnchorVolume.SetValue(anchor, a.hasVolume ? (Bounds?)new Bounds(a.volumeCenter, a.volumeSize) : null);
                AnchorBoundary.SetValue(anchor, new List<Vector2>(a.boundary));
                AnchorRoom.SetValue(anchor, room);
                room.Anchors.Add(anchor);
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE)) room.WallAnchors.Add(anchor);
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR)) RoomFloor.SetValue(room, anchor);
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING)) RoomCeiling.SetValue(room, anchor);
            }
            return room;
        }

        // ---------------------------------------------------------------- one participant

        private sealed class Peer
        {
            public string Name;
            public SpatialBlendManager Manager;
            public FreeformCollaborationApp App;
            public MRUKRoom Room;
            public RoomRegion Region;
            public GameObject Chess, Whiteboard, Avatar;
            public Vector3 Eye, Forward;
        }

        private static readonly FieldInfo ConnectOnAwake = typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InitLibrary = typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AppManager = typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AppStarted = typeof(FreeformCollaborationApp).GetField("_appStarted", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RegisterRoomGeometry = typeof(FreeformCollaborationApp).GetMethod("TryRegisterCurrentRoomGeometry", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo AppUpdate = typeof(FreeformCollaborationApp).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ActorByEntity = typeof(SpatialBlendManager).GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic);

        private Peer MakePeer(string name, MRUKRoom room)
        {
            var peer = new Peer { Name = name, Room = room };
            _livePeers.Add(peer);
            var managerGo = new GameObject("manager-" + name);
            managerGo.SetActive(false);
            _objects.Add(managerGo);
            peer.Manager = managerGo.AddComponent<SpatialBlendManager>();
            ConnectOnAwake.SetValue(peer.Manager, false);
            Assert.That((bool)InitLibrary.Invoke(peer.Manager, null), Is.True, "the native library");
            var appGo = new GameObject("app-" + name);
            appGo.SetActive(false);
            _objects.Add(appGo);
            peer.App = appGo.AddComponent<FreeformCollaborationApp>();
            AppManager.SetValue(peer.App, peer.Manager);
            peer.App.RoomProvider = () => room;
            peer.App.UseSceneAnchorCatalog(new MrukSceneAnchorCatalog(() => room));
            peer.App.UserViewProvider = () => (peer.Eye, peer.Forward);
            peer.App.FloorHeightProvider = () => room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
            // A prefab or JSON room's anchors have no platform id: their share keys are derived from the anchor (stable per run).
            peer.App.SessionKeyProvider = r => $"{name}{MRUKSceneGeometryAdapter.StableKey(r.Anchor.GetComponent<MRUKAnchor>()):x8}";
            peer.Manager.RegisterApp(peer.App);
            peer.Manager.WaitsForLocalSceneGeometry = true;
            peer.Region = MrukRoomRegion.Build(room);
            return peer;
        }

        // The core link between the two managers: the product's tunneled path (the device carries these bytes over Photon), pumped
        // in this process. A TCP-joined space here cannot add elements of its own; the tunneled join is what devices run.
        private static CoreLink s_hostLink, s_joinLink;
        private static readonly byte[] s_buffer = new byte[1 << 16];
        private static readonly List<byte> s_toJoiner = new List<byte>(), s_toHost = new List<byte>();

        private static void Shuttle()
        {
            if (s_hostLink == null || s_joinLink == null) return;
            Move(s_hostLink, s_joinLink, s_toJoiner);
            Move(s_joinLink, s_hostLink, s_toHost);
        }

        private static void Move(CoreLink from, CoreLink to, List<byte> pending)
        {
            for (int guard = 0; guard < 64 && from.Read(s_buffer, out int n) == CoreLinkRead.Open && n > 0; ++guard)
                for (int i = 0; i < n; ++i) pending.Add(s_buffer[i]);
            if (pending.Count == 0) return;
            byte[] bytes = pending.ToArray();
            if (to.Write(bytes, bytes.Length) == CoreLinkWrite.Accepted) pending.Clear();
        }

        /// <summary>
        /// Hosts (and joins, over the tunneled core link) and starts the apps: FreeformCollaborationApp.OnAppStart registers the room,
        /// its furniture and queues the host's furniture shares, as on a device. Nothing else is pumped, so the caller's authored
        /// objects go first, as the scene's boards do at app start.
        /// </summary>
        private IEnumerator Connect(Peer host, Peer joiner, Action beforeJoin = null)
        {
            s_hostLink = s_joinLink = null;
            s_toJoiner.Clear();
            s_toHost.Clear();
            host.Manager.HostTunneled();
            Assert.That(host.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), "host: " + host.Manager.ConnectionError);
            // The host's app starts in its room first (it shares its scene's boards and its marked furniture), as a device hosts alone.
            host.Manager.StartApps();
            beforeJoin?.Invoke();
            if (joiner != null)
            {
                s_hostLink = host.Manager.AcceptCoreLink();
                s_joinLink = joiner.Manager.JoinTunneled();
                Assert.That(s_hostLink, Is.Not.Null);
                Assert.That(s_joinLink, Is.Not.Null);
                for (int frame = 0; frame < 3000 && joiner.Manager.ConnectionState != SpatialBlendConnectionState.Connected; ++frame)
                {
                    Shuttle();
                    joiner.Manager.PollTunneledJoin();
                    yield return null;
                }
                Assert.That(joiner.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), "joiner: " + joiner.Manager.ConnectionError);
            }
            joiner?.Manager.StartApps();
        }

        /// <summary>The app's room registration: room ring and walls, the adapter's furniture, then (host) the marked furniture.</summary>
        private static void RegisterRoom(Peer peer)
        {
            AppStarted.SetValue(peer.App, true);
            RegisterRoomGeometry.Invoke(peer.App, null);
        }

        /// <summary>
        /// Runs the managers and apps <paramref name="frames"/> frames (by frame count, never by wall-clock time,
        /// so two runs do the same work). <paramref name="seconds"/> overloads keep the call sites readable at 30 frames a second.
        /// </summary>
        // The I5 step (frames at 30 a second); SPATIALBLEND_ROOM_MATRIX_STEP overrides it (seconds).
        private static readonly float StepSeconds = float.TryParse(Environment.GetEnvironmentVariable("SPATIALBLEND_ROOM_MATRIX_STEP"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float step) ? step : 0.1f;

        // 150 frames a "second": about the frame count a batch-mode Editor ran in the wall-clock pumps this replaced, so the
        // deferred links and the core's proposals settle as before (30 left a joiner's deferred TABLE link unresolved).
        private const float FramesPerSecond = 150f;

        private static IEnumerator Pump(float seconds, params Peer[] peers) => PumpFrames(Mathf.Max(1, Mathf.RoundToInt(seconds * FramesPerSecond)), peers);

        private static readonly MethodInfo DispatchUpdate = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);

        private static IEnumerator PumpFrames(int frames, params Peer[] peers)
        {
            LogAssert.ignoreFailingMessages = true;
            for (int f = 0; f < frames; ++f)
            {
                PumpOnce(peers);
                yield return null;
            }
        }

        private static void PumpOnce(Peer[] peers)
        {
            DispatchUpdate.Invoke(UnityMainThreadDispatcher.Instance, null);
            Shuttle();
            foreach (Peer p in peers)
            {
                if (p == null) continue;
                p.Manager.Update();
                p.Manager.LateUpdate();
                AppUpdate?.Invoke(p.App, null);
            }
        }

        private void Teardown(params Peer[] peers)
        {
            s_hostLink = s_joinLink = null;
            // What the teardown logs (a retired session's removals) is not a placement's rejection.
            Application.logMessageReceived -= Capture;
            foreach (Peer p in _livePeers.Concat(peers ?? new Peer[0]).Distinct().ToList())
            {
                if (p == null) continue;
                try { p.Manager.Disconnect(); } catch (Exception e) { Debug.LogWarning("disconnect: " + e.Message); }
                try { p.Manager.OnDestroy(); } catch (Exception e) { Debug.LogWarning("destroy: " + e.Message); }
            }
            _livePeers.Clear();
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            // Whatever the session created that this test did not track (a received box, a replica) goes too: the next case's
            // lobby reads every authored object in the scene (PlacementOthers), so a leftover would change its spawn.
            foreach (SbSpatialAuthoring left in UnityEngine.Object.FindObjectsByType<SbSpatialAuthoring>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (left != null) UnityEngine.Object.DestroyImmediate(left.transform.root.gameObject);
            foreach (SbEntity left in UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (left != null) UnityEngine.Object.DestroyImmediate(left.transform.root.gameObject);
            _errors.Clear();
            Application.logMessageReceived += Capture;
        }

        // ---------------------------------------------------------------- virtual objects

        private GameObject Spawn(string path, string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            GameObject go = UnityEngine.Object.Instantiate(prefab);
            go.name = name;
            _objects.Add(go);
            return go;
        }

        /// <summary>The Fusion network id the scene object has on every device (Fusion assigns it; this test has no runner).</summary>
        private static void SetNetworkId(GameObject entity, uint raw)
        {
            var networkObject = entity.GetComponent<Fusion.NetworkObject>();
            Assert.That(networkObject, Is.Not.Null, entity.name + " is a network object");
            object id = Activator.CreateInstance(typeof(Fusion.NetworkId));
            typeof(Fusion.NetworkId).GetField("Raw").SetValue(id, raw);
            PropertyInfo property = typeof(Fusion.NetworkObject).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property.GetSetMethod(true) != null) { property.GetSetMethod(true).Invoke(networkObject, new[] { id }); return; }
            // Fusion reads the id from the object's header in its runner's memory (NetworkObjectHeader, the id first). With no runner,
            // the header is a block of this test's own (kept for the process).
            IntPtr header = System.Runtime.InteropServices.Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i += 4) System.Runtime.InteropServices.Marshal.WriteInt32(header, i, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(header, 0, (int)raw);
            FieldInfo ptr = typeof(Fusion.NetworkObject).GetField("Ptr", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = new System.Reflection.Emit.DynamicMethod("SetPtr", typeof(void),
                new[] { typeof(Fusion.NetworkObject), typeof(IntPtr) }, typeof(Fusion.NetworkObject), true);
            System.Reflection.Emit.ILGenerator il = setter.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Stfld, ptr);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            setter.Invoke(null, new object[] { networkObject, header });
            Assert.That(networkObject.Id.Raw, Is.EqualTo(raw));
        }

        /// <summary>Whether the share put the object in the room with its region (never "shared without its region").</summary>
        private static bool SharedWithRegion(Peer peer, GameObject root, out string why)
        {
            SbEntity entity = root.GetComponent<SbEntity>();
            why = null;
            if (entity == null || !entity.IsAttached()) { why = "not attached"; return false; }
            if (!peer.Manager.TryGetAuthoredRegion(entity.ID, out long region) || region <= 0) { why = "shared without its region"; return false; }
            return true;
        }

        private static GameObject EntityRoot(GameObject prefabRoot)
        {
            SbEntity entity = prefabRoot.GetComponentInChildren<SbEntity>(true);
            return entity != null ? entity.gameObject : prefabRoot;
        }

        [System.Runtime.InteropServices.DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int sb_get_element_parent(long space, long elem, out long parent);

        // The element's ancestry in the core (diagnostics): "59<58<1".
        private static string Ancestry(Peer peer, long id)
        {
            var chain = new List<long> { id };
            for (long e = id; chain.Count < 8 && sb_get_element_parent(peer.Manager.ID, e, out long p) == 1 && p > 0; e = p) chain.Add(p);
            return string.Join("<", chain);
        }

        /// <summary>What the core's "element N" in an overlap refusal is, in the joiner's space (a wall group, a region of whose object).</summary>
        private static string Identify(Peer peer, List<string> errors)
        {
            var ids = new HashSet<long>();
            foreach (string e in errors)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(e, @"element (\d+) and element (\d+)"))
                {
                    ids.Add(long.Parse(m.Groups[1].Value));
                    ids.Add(long.Parse(m.Groups[2].Value));
                }
            if (ids.Count == 0) return "";
            var walls = (IList)typeof(SpatialBlendManager).GetField("_wallElementIDs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer.Manager);
            var states = (IDictionary)typeof(SpatialBlendManager).GetField("_authoredStateByObject", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer.Manager);
            var sb = new StringBuilder(" [ids:");
            foreach (long id in ids)
            {
                string what = "?";
                foreach (object w in walls) if (Convert.ToInt64(w) == id || Convert.ToInt64(w) + 1 == id) what = "WALL";
                foreach (DictionaryEntry de in states)
                {
                    object st = de.Value;
                    long reg = (long)st.GetType().GetField("RegionElement").GetValue(st), obj = (long)st.GetType().GetField("ObjectElement").GetValue(st);
                    bool owned = (bool)st.GetType().GetField("Owned").GetValue(st);
                    if (reg == id || obj == id)
                        what = $"{(reg == id ? "region" : "object")} of {peer.Manager.GetEntity(obj)?.name ?? obj.ToString()}{(owned ? " (owned)" : "")}";
                }
                var ownedGeo = (IDictionary)typeof(SpatialBlendManager).GetField("_ownedLocalSceneGeometry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer.Manager);
                foreach (DictionaryEntry de in ownedGeo)
                {
                    long par = (long)de.Value.GetType().GetField("Parent").GetValue(de.Value), pl = (long)de.Value.GetType().GetField("Plane").GetValue(de.Value);
                    string anchorName = peer.Room.Anchors.FirstOrDefault(an => an != null && MRUKSceneGeometryAdapter.StableKey(an) == (int)de.Key)?.name ?? de.Key.ToString();
                    if (par == id) what = $"scene anchor {anchorName} (its element)";
                    else if (pl == id) what = $"scene anchor {anchorName} (its top surface)";
                    else if (par > 0 && Ancestry(peer, id).Contains("<" + par)) what = $"under scene anchor {anchorName}";
                }
                sb.Append($" {id}={what} ({Ancestry(peer, id)})");
            }
            foreach (DictionaryEntry de in states)
            {
                object st = de.Value;
                var inv = (Matrix4x4)st.GetType().GetField("RegionParentWorldInverse").GetValue(st);
                sb.Append($" state[{de.Key}: obj {st.GetType().GetField("ObjectElement").GetValue(st)} reg {st.GetType().GetField("RegionElement").GetValue(st)} owned {st.GetType().GetField("Owned").GetValue(st)} nested {inv != Matrix4x4.identity} {peer.Manager.GetEntity((long)st.GetType().GetField("ObjectElement").GetValue(st))?.name}]");
            }
            // The joiner's installed boxes and what of its room they overlap.
            foreach (SbMRUKAnchorEntity box in UnityEngine.Object.FindObjectsByType<SbMRUKAnchorEntity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).OrderBy(x => x.name, StringComparer.Ordinal))
            {
                if (box == null || box.transform.IsChildOf(peer.Room.transform) || peer.Manager.GetEntity(box.ID) != box) continue;
                if (!box.TryGetMeasuredBox(out Vector3 c, out Vector3 size)) continue;
                Vector2[] foot = BoxFootprint(box.transform, c, size);
                sb.Append($" box[{string.Join(" ", foot.Select(v => v.ToString("0.##")))}] inRing={foot.All(v => WalkablePlacementSnap.Contains(peer.Region.Ring, v))}");
                for (int i = 0; i < peer.Region.Furniture.Length; ++i)
                {
                    Vector2[] f = peer.Region.Furniture[i];
                    double a = f != null && f.Length >= 3 ? SceneFootprintLayout.IntersectionArea(f, foot) : 0;
                    if (a > 1e-4) sb.Append($" overFurniture#{i}={a:0.###}[{string.Join(" ", f.Select(v => v.ToString("0.##")))}]");
                }
                for (int i = 0; i < peer.Region.Walls.Length; ++i)
                {
                    Vector2[] w = peer.Region.Walls[i];
                    double a = w != null && w.Length >= 3 ? SceneFootprintLayout.IntersectionArea(w, foot) : 0;
                    if (a > 1e-4) sb.Append($" overWall#{i}={a:0.###}");
                }
            }
            return sb.Append(']').ToString();
        }

        private static readonly FieldInfo AuthoredStates = typeof(SpatialBlendManager).GetField("_authoredStateByObject", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The region the core holds for <paramref name="root"/> (the polygon written, trimmed for a physical object), world ground ring.</summary>
        private static Vector2[] RegisteredRegion(Peer peer, GameObject root)
        {
            SbEntity entity = root.GetComponentInParent<SbEntity>();
            var states = (IDictionary)AuthoredStates.GetValue(peer.Manager);
            if (entity != null && states.Contains(entity.ID))
            {
                object st = states[entity.ID];
                var polygon = (Vector2[])st.GetType().GetField("RegionPolygon").GetValue(st);
                var frame = (Matrix4x4)st.GetType().GetField("RegionFrameWorld").GetValue(st);
                if (polygon != null && polygon.Length >= 3)
                    return polygon.Select(v => { Vector3 w = frame.MultiplyPoint3x4(new Vector3(v.x, 0f, v.y)); return new Vector2(w.x, w.z); }).ToArray();
            }
            return Region(root);
        }

        /// <summary>Every MRUK anchor's volume footprint in the room now (late furniture included), read from MRUK here.</summary>
        private static List<Vector2[]> FurnitureFeet(Peer peer) =>
            peer.Room.Anchors.Where(a => a != null && a.VolumeBounds.HasValue &&
                                         !a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE))
                .Select(a => BoxFootprint(a.transform, a.VolumeBounds.Value.center, a.VolumeBounds.Value.size)).ToList();

        /// <summary>
        /// The independent placement check (IndependentGeometry): each virtual region inside the room's ring, clear of every MRUK
        /// footprint and of every other region; each virtual object's footprint on the floor; each physical object's region clear
        /// of the furniture, and its footprint clear of the virtual regions. 1 cm of touching is allowed.
        /// </summary>
        private static List<string> Check(Peer peer, IReadOnlyList<GameObject> virtuals, IReadOnlyList<GameObject> physicals)
        {
            var failures = new List<string>();
            List<Vector2[]> furniture = FurnitureFeet(peer);
            var all = virtuals.Concat(physicals).ToList();
            foreach (GameObject v in virtuals)
            {
                Vector2[] region = RegisteredRegion(peer, v), foot = Footprint(v);
                if (region == null) { failures.Add($"{v.name}: no region"); continue; }
                if (!IndependentGeometry.Contained(region, peer.Region.Ring, 0.01f, out string why)) failures.Add($"{v.name}: region leaves the room ({why})");
                for (int i = 0; i < furniture.Count; ++i)
                {
                    float o = IndependentGeometry.Overlap(region, furniture[i], 0.01f);
                    if (o > 1e-3f) failures.Add($"{v.name}: region overlaps furniture #{i} by {o:0.###} m^2");
                }
                if (foot != null && !IndependentGeometry.Contained(foot, peer.Region.Floor, 0.03f, out string off)) failures.Add($"{v.name}: footprint off the floor ({off})");
                foreach (GameObject other in all)
                {
                    if (other == v || all.IndexOf(other) < all.IndexOf(v) && virtuals.Contains(other)) continue;
                    Vector2[] r2 = physicals.Contains(other) ? Footprint(other) : RegisteredRegion(peer, other);
                    if (r2 == null) continue;
                    float o = IndependentGeometry.Overlap(region, r2, 0.01f);
                    if (o > 1e-3f) failures.Add($"{v.name}: region overlaps {(physicals.Contains(other) ? "the footprint of " : "")}{other.name} by {o:0.###} m^2");
                }
            }
            foreach (GameObject p in physicals)
            {
                Vector2[] region = RegisteredRegion(peer, p);
                if (region == null) { failures.Add($"{p.name}: no region"); continue; }
                for (int i = 0; i < furniture.Count; ++i)
                {
                    float o = IndependentGeometry.Overlap(region, furniture[i], 0.01f);
                    if (o > 1e-3f) failures.Add($"{p.name} (physical): region overlaps furniture #{i} by {o:0.###} m^2");
                }
            }
            return failures;
        }

        // A stable per-room seed (string.GetHashCode differs between runs).
        private static int Seed(string text)
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (char c in text) h = (h ^ c) * 16777619;
                return h;
            }
        }

        /// <summary>
        /// The motif's drop rule (PlacementMotif.ClampPositionToWalkable: the object moved to <paramref name="at"/> and pulled once by
        /// SbApp.TryPullPlacement). Called here because EditMode never runs the motif's Start, which finds its entity.
        /// </summary>
        private static Vector3 Drop(Peer peer, GameObject root, Vector3 at)
        {
            Vector3 from = root.transform.position;
            bool pulled = peer.App.TryPullPlacement(root, new Vector2(at.x - from.x, at.z - from.z), 0f, out Vector2 shift);
            return pulled ? new Vector3(at.x + shift.x, at.y, at.z + shift.y) : at;
        }
        private static readonly PropertyInfo RoomAnchorCreated = typeof(MRUKRoom).GetProperty("AnchorCreatedEvent");

        /// <summary>
        /// I6, the product paths in one room: drops at seeded random poses (the motif's own drop rule),
        /// a Whiteboard dropped on a random wall (it stays on a wall), an own scan installed against a piece of furniture (never
        /// moved; virtual objects step off it), and furniture arriving after the objects (they step off it). Each is checked by the
        /// core's acceptance and IndependentGeometry. (The scan box has no room rule.)
        /// </summary>
        private IEnumerator Cases(Peer peer, GameObject chess, GameObject board, Row row)
        {
            var rng = new System.Random(Seed(row.Key));
            var physicals = new List<GameObject>();
            void Verify(string what)
            {
                List<string> errs = TakeErrors(peer);
                foreach (string e in errs) row.Fail("I6", $"{what}: {e}");
                if (errs.Count > 0) row.Fail("I6", $"{what}: ids" + Identify(peer, errs));
                foreach (string f in Check(peer, new[] { chess, board }, physicals)) row.Fail("I6", $"{what}: {f}");
            }
            Vector2[] floor = peer.Region.Floor;
            Vector2 fMin = floor.Aggregate(Vector2.Min), fMax = floor.Aggregate(Vector2.Max);

            // Drops (Chess is dropped back where it spawned after them, so the later cases start from the spawn's arrangement).
            Pose chessSpawn = new Pose(chess.transform.position, chess.transform.rotation);
            for (int i = 0; i < 4; ++i)
            {
                var at = new Vector3(Mathf.Lerp(fMin.x, fMax.x, (float)rng.NextDouble()), chess.transform.position.y, Mathf.Lerp(fMin.y, fMax.y, (float)rng.NextDouble()));
                chess.transform.position = Drop(peer, chess, at);
                yield return Pump(0.3f, peer);
                Verify($"drop {i} at ({at.x:0.##}, {at.z:0.##})");
            }

            chess.transform.SetPositionAndRotation(Drop(peer, chess, chessSpawn.position), chessSpawn.rotation);
            yield return Pump(0.3f, peer);
            Verify("drop back");

            // In session, a drop with Chess's centre in the Whiteboard's region, and one in the shared TABLE's stand-off
            // off its box. Neither rests on furniture, so both are pulled clear; the core accepts the result and Unity matches it.
            Vector2[] boardRegion = RegisteredRegion(peer, board);
            var dropTargets = new List<(string, Vector2)>();
            if (boardRegion != null) dropTargets.Add(("into the Whiteboard's region", Centroid(boardRegion)));
            SbMRUKAnchorEntity table = peer.Room.GetComponentsInChildren<SbMRUKAnchorEntity>(true).Where(t => t.IsAttached()).OrderBy(t => t.name, StringComparer.Ordinal).FirstOrDefault();
            if (table != null && table.TryGetMeasuredBox(out Vector3 tc, out Vector3 ts))
            {
                Vector2[] tableBox = BoxFootprint(table.transform, tc, ts);
                Vector2[] tableRegion = RegisteredRegion(peer, table.gameObject);
                if (tableRegion != null)
                {
                    // A point of the stand-off: from the box's centre toward the region's farthest corner, past the box.
                    Vector2 bc = Centroid(tableBox), far = tableRegion.OrderByDescending(v => (v - bc).sqrMagnitude).First();
                    Vector2 standOff = bc + (far - bc) * 0.8f;
                    if (!IndependentGeometry.Inside(standOff, tableBox) && IndependentGeometry.Inside(standOff, tableRegion))
                        dropTargets.Add(("into the shared TABLE's stand-off", standOff));
                }
            }
            foreach ((string what, Vector2 at) in dropTargets)
            {
                Vector3 to = new Vector3(at.x, chess.transform.position.y, at.y);
                chess.transform.position = Drop(peer, chess, to);
                yield return Pump(0.3f, peer);
                Vector3 settled = chess.transform.position;
                yield return Pump(0.3f, peer);
                if (Vector3.Distance(settled, chess.transform.position) > 1e-3f) row.Fail("I6", $"drop {what}: the core refused it and Chess was put back");
                Verify("drop " + what);
                row.Cells["standOffDrop"] = dropTargets.Count + "";
            }
            // An in-session drop of Chess onto the shared TABLE: furniture is an obstacle for a floor object, so it is pulled off;
            // never refused.
            if (table != null && table.TryGetMeasuredBox(out Vector3 tc2, out Vector3 ts2))
            {
                Vector3 onTable = table.transform.TransformPoint(tc2);
                chess.transform.position = Drop(peer, chess, new Vector3(onTable.x, chess.transform.position.y, onTable.z));
                yield return Pump(0.3f, peer);
                Vector3 settled = chess.transform.position;
                yield return Pump(0.3f, peer);
                if (Vector3.Distance(settled, chess.transform.position) > 1e-3f) row.Fail("I6", "drop onto the shared TABLE: the core refused it and Chess was put back");
                Verify("drop onto the shared TABLE");
            }

            // Carrying Chess (a held grab) across the furniture and the Whiteboard's region never sticks: nothing is
            // pushed while the motif holds it; the drop at the end is accepted or pulled.
            PlacementMotif chessMotif = chess.GetComponentInChildren<PlacementMotif>(true);
            FieldInfo grabbed = typeof(InteractionMotif).GetField("_isGrabbed", BindingFlags.Instance | BindingFlags.NonPublic);
            var waypoints = new List<Vector2> { new Vector2(chess.transform.position.x, chess.transform.position.z) };
            if (table != null && table.TryGetMeasuredBox(out Vector3 tc3, out _)) { Vector3 w = table.transform.TransformPoint(tc3); waypoints.Add(new Vector2(w.x, w.z)); }
            if (boardRegion != null) waypoints.Add(Centroid(boardRegion));
            waypoints.Add(Centroid(floor));
            grabbed.SetValue(chessMotif, true);
            int stuck = 0, carried = 0;
            _errors.Clear();
            for (int w = 1; w < waypoints.Count; ++w)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(waypoints[w - 1], waypoints[w]) / 0.05f));
                for (int i = 1; i <= n; ++i)
                {
                    Vector2 q = Vector2.Lerp(waypoints[w - 1], waypoints[w], i / (float)n);
                    var set = new Vector3(q.x, chess.transform.position.y, q.y);
                    chess.transform.position = set;
                    yield return PumpFrames(2, peer);
                    ++carried;
                    if (Vector3.Distance(chess.transform.position, set) > 1e-4f) ++stuck;
                }
            }
            List<string> carryErrors = TakeErrors(peer);
            grabbed.SetValue(chessMotif, false);
            if (stuck > 0) row.Fail("I6", $"carry: Chess stuck {stuck} of {carried} steps");
            foreach (string e in carryErrors) row.Fail("I6", "carry: " + e);
            chess.transform.position = Drop(peer, chess, chess.transform.position);
            yield return Pump(0.3f, peer);
            Vector3 rest = chess.transform.position;
            yield return Pump(0.3f, peer);
            if (Vector3.Distance(rest, chess.transform.position) > 1e-3f) row.Fail("I6", "carry: the drop at the end was refused and Chess put back");
            Verify("carry and drop");
            row.Cells["carry"] = $"{carried}-{stuck}";

            chess.transform.SetPositionAndRotation(Drop(peer, chess, chessSpawn.position), chessSpawn.rotation);
            yield return Pump(0.3f, peer);

            // A Whiteboard dropped on a wall stays on a wall.
            PlacementMotif boardMotif = board.GetComponentInChildren<PlacementMotif>(true);
            if (peer.Region.WallEdges.Length > 0)
            {
                (Vector2 a, Vector2 b, Vector2 inward) e = peer.Region.WallEdges[rng.Next(peer.Region.WallEdges.Length)];
                Vector2 q = Vector2.Lerp(e.a, e.b, (float)rng.NextDouble());
                Pose pose = boardMotif.WallPose(new Vector3(q.x, 0f, q.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
                Pose boardBefore = new Pose(board.transform.position, board.transform.rotation);
                board.transform.SetPositionAndRotation(pose.position, pose.rotation);
                bool onWall = peer.App.TryPlaceOnWall(board, boardMotif, 0f);
                bool pulled = onWall || peer.App.TryPullPlacement(board, Vector2.zero, 0f, out _);
                if (!onWall && pulled) board.transform.position = Drop(peer, board, board.transform.position);
                if (!pulled)
                {
                    // No spot on any wall nor on the floor at that wall's facing (a small room with Chess in it): the user takes
                    // the board back; the case is recorded, not failed.
                    board.transform.SetPositionAndRotation(boardBefore.position, boardBefore.rotation);
                    row.Cells["wallDrop"] = "nospot";
                }
                yield return Pump(0.3f, peer);
                var bp = new Vector2(board.transform.position.x, board.transform.position.z);
                float toWall = peer.Region.WallEdges.Min(w => DistanceToSegment(bp, w.a, w.b));
                if (onWall && toWall > 0.12f) row.Fail("I6", $"wall drop: the Whiteboard left the wall ({toWall:0.##} m from it)");
                if (pulled) row.Cells["wallDrop"] = onWall ? "wall" : "floor";
                Verify("wall drop");
            }

            // An own scan against the largest piece of furniture: installed where it was scanned, never moved.
            List<Vector2[]> furniture = FurnitureFeet(peer);
            if (furniture.Count > 0)
            {
                Vector2[] big = furniture.OrderByDescending(f => Mathf.Abs(PolygonBoolean.SignedArea(f))).First();
                Vector2 c = Centroid(big), toRoom = (Centroid(floor) - c).sqrMagnitude > 1e-6f ? (Centroid(floor) - c).normalized : Vector2.right;
                float reach = big.Max(v => Vector2.Dot(v - c, toRoom));
                GameObject scan = EntityRoot(Spawn(ChessPrefab, "Scan"));
                scan.AddComponent<PhysicalCounterpart>();
                SetNetworkId(scan, 3);
                // A scan of an object on the floor beside the furniture: the spot nearest the furniture's side where the scan's
                // footprint is clear of every piece of furniture and on the floor (checked here, independently).
                Vector2[] scanFoot = Footprint(scan);
                Vector2 sc = Centroid(scanFoot);
                float half = scanFoot.Max(v => Vector2.Dot(v - sc, toRoom));
                Vector2 beside = c + toRoom * (reach + half + 0.02f);
                Vector2? spot = NearestFree(peer.Region, beside, q =>
                {
                    Vector2[] foot = scanFoot.Select(v => v - sc + q).ToArray();
                    return IndependentGeometry.Contained(foot, floor, 0f, out _) && furniture.All(f => IndependentGeometry.Overlap(foot, f, 0f) < 1e-4f);
                });
                if (spot == null)
                {
                    row.Cells["scan"] = "no free spot";
                    UnityEngine.Object.DestroyImmediate(scan.transform.root.gameObject);
                }
                else
                {
                    scan.transform.position += new Vector3(spot.Value.x - sc.x, 0f, spot.Value.y - sc.y);
                    row.Cells["scan"] = $"{Vector2.Distance(spot.Value, beside):0.##}m-off";
                    Vector3 installed = scan.transform.position;
                    peer.Manager.ShareGameObject(scan, true, true);
                    yield return Pump(0.5f, peer);
                    if (Vector3.Distance(scan.transform.position, installed) > 1e-3f)
                        row.Fail("I6", $"own scan: moved {Vector3.Distance(scan.transform.position, installed):0.###} m from where it was installed");
                    if (!SharedWithRegion(peer, scan, out string sw)) row.Fail("I6", $"own scan: {sw}");
                    physicals.Add(scan);
                    Verify("own scan");
                }
            }

            // Late furniture under Chess's region (beside its footprint): Chess steps off it.
            Vector2[] chessFoot = Footprint(chess);
            if (chessFoot != null)
            {
                Vector2 cc = Centroid(chessFoot);
                Vector2 side = new Vector2(chess.transform.right.x, chess.transform.right.z).normalized;
                float halfW = chessFoot.Max(v => Vector2.Dot(v - cc, side));
                Vector2 at = cc + side * (halfW + 0.3f);
                var go = new GameObject("OTHER-late");
                go.SetActive(false);
                go.transform.SetParent(peer.Room.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(at.x, 0f, at.y), Quaternion.Euler(-90f, 0f, 0f));
                MRUKAnchor anchor = go.AddComponent<MRUKAnchor>();
                AnchorLabel.SetValue(anchor, MRUKAnchor.SceneLabels.OTHER);
                AnchorVolume.SetValue(anchor, (Bounds?)new Bounds(new Vector3(0f, 0f, 0.3f), new Vector3(0.4f, 0.4f, 0.6f)));
                AnchorBoundary.SetValue(anchor, new List<Vector2>());
                AnchorRoom.SetValue(anchor, peer.Room);
                peer.Room.Anchors.Add(anchor);
                ((UnityEngine.Events.UnityEvent<MRUKAnchor>)RoomAnchorCreated.GetValue(peer.Room)).Invoke(anchor);
                yield return Pump(0.5f, peer);
                Verify("late furniture");
            }
            row.Cells["trim"] = $"{peer.Manager.VirtualRegionsTrimmed}/{peer.Manager.VirtualRegionsWithNoFreePart}";
            row.Pass("I6");
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // I5's walk (the core's ratio continuation): steps of at most 5 cm, each one source update pumped StepFrames frames twice.
        private const float WalkStepMeters = 0.05f;
        private const int StepFrames = 6;
        // (a) 0.10 m: two walk steps; the receiver's target is the core's, which may be clamped off an obstacle on the way.
        private const float RatioToleranceMeters = 0.10f;
        // (b) 0.10 m at the end of the approach; a start counts as matching within the same 0.10 m.
        private const float EndToleranceMeters = 0.10f;
        private const float MatchingStartMeters = 0.10f;
        // (c) 0.05 m per step: walking away (or standing) issues no request, so the held target may move only by the solver's own
        // re-projection. (b) inexact ends (a multi-referent compromise) are reported in the "inexact" column, not failed.
        private const float HeldToleranceMeters = 0.05f;

        private sealed class StepResult
        {
            public bool Got;
            public Vector2 Target;
            public EmbodiedStatus Status;
            public float Error;
            public long Referent;
        }

        // Every walk's criteria, for the distributions written with the pairs table.
        private static readonly List<(string pair, string obj, float a, float b, bool matching, float c, int notFree, bool inexact, int otherReferent)> s_criteria =
            new List<(string, string, float, float, bool, float, int, bool, int)>();

        private static readonly List<float> s_stepA = new List<float>(), s_stepC = new List<float>();
        private static readonly Dictionary<string, int> s_targetClasses = new Dictionary<string, int>();
        private static readonly MethodInfo CollectFurniture = typeof(SpatialBlendManager).GetMethod("CollectFurnitureFootprints", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// (d), by the core's free space (the registered walkable ring, minus the body radius, minus the REGISTERED obstacle-role
        /// footprints: the walls and the adapter's furniture), with its design exemptions. Classes:
        /// "free"; "exempt: referent" (on or touching the driving referent's own footprint, which the source stands at);
        /// "exempt: bridge" (in the walkable ring outside the floor outline: a sticking-out child and its bridge);
        /// "exempt: not an obstacle" (inside a matrix obstacle the core does not hold as one, e.g. near a footprint but clear of the
        /// registered ones by the body radius); "FAIL core" (within the body radius of a registered obstacle or outside the ring);
        /// "FAIL unregistered" (inside an MRUK footprint Unity did not register as an obstacle: a registration bug).
        /// The core's own feasibility query (sb_copy_receiver_approach_feasibility) needs a receiver-approach context this test's
        /// avatar does not have, so the registered polygons are compared here instead.
        /// </summary>
        private static string ClassifyTarget(Peer peer, Vector2 t, long referent, Dictionary<long, (Vector2[] host, Vector2[] joiner)> referents)
        {
            var registered = new List<Vector2[]>();
            CollectFurniture.Invoke(peer.Manager, new object[] { registered, 0L });
            registered.AddRange(peer.Manager.RoomWalls);
            Vector2[] ring = peer.Manager.RegisteredWalkable?.Boundary ?? peer.Region.Ring;
            if (referents.TryGetValue(referent, out var rf) && rf.joiner != null &&
                (WalkablePlacementSnap.Contains(rf.joiner, t) || DistanceToRing(t, rf.joiner) < BodyRadius)) return "exempt: referent";
            float clearance = registered.Where(o => o != null && o.Length >= 3)
                .Select(o => WalkablePlacementSnap.Contains(o, t) ? -1f : DistanceToRing(t, o)).DefaultIfEmpty(99f).Min();
            bool inRing = WalkablePlacementSnap.Contains(ring, t);
            bool onFloor = IndependentGeometry.Inside(t, peer.Region.Floor);
            if (!inRing) return "FAIL core: outside the walkable ring";
            // The core's body radius is the actor's (not the matrix's 0.2 m): a target clear of every registered obstacle is in
            // its free space up to that radius; one inside or touching a registered obstacle is outside it whatever the radius.
            if (clearance <= 0.01f) return onFloor ? "FAIL core: inside or touching a registered obstacle" : "exempt: bridge";
            if (clearance < BodyRadius - 0.02f) return onFloor ? "near: within the body radius of a registered obstacle (0.01-0.18 m)" : "exempt: bridge";
            if (FurnitureFeet(peer).Any(f => IndependentGeometry.Inside(t, f)) && clearance >= 0f) return "FAIL unregistered: inside an MRUK footprint";
            if (!onFloor) return "exempt: bridge";
            return IsFree(peer.Region, t, 0.01f) ? "free" : "exempt: not an obstacle";
        }

        private static void WriteCriteria()
        {
            File.WriteAllText(Path.Combine(MrukRoomExport.ExportDirectory, "results-i5-targets.tsv"),
                "class\tcount\n" + string.Concat(s_targetClasses.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}\t{k.Value}\n")));
            File.WriteAllText(Path.Combine(MrukRoomExport.ExportDirectory, "results-i5-steps.tsv"),
                "kind\terror_m\n" + string.Concat(s_stepA.Select(e => "a\t" + e.ToString("0.####", CultureInfo.InvariantCulture) + "\n")) +
                string.Concat(s_stepC.Select(e => "c\t" + e.ToString("0.####", CultureInfo.InvariantCulture) + "\n")));
            var sb = new StringBuilder("pair\tobject\ta_worst_m\tb_err_m\tb_kind\tc_worst_m\td_not_free\tinexact\ttransitions\n");
            foreach (var c in s_criteria)
                sb.Append($"{c.pair}\t{c.obj}\t{c.a.ToString("0.###", CultureInfo.InvariantCulture)}\t{c.b.ToString("0.###", CultureInfo.InvariantCulture)}\t{(c.matching ? "distance" : "ratio")}\t" +
                          $"{c.c.ToString("0.###", CultureInfo.InvariantCulture)}\t{c.notFree}\t{c.inexact}\t{c.otherReferent}\n");
            Directory.CreateDirectory(MrukRoomExport.ExportDirectory);
            File.WriteAllText(Path.Combine(MrukRoomExport.ExportDirectory, "results-i5.tsv"), sb.ToString());
        }

        private sealed class CoreCourse : IEmbodiedCourse
        {
            private readonly long _space, _actor;
            private readonly double[] _scratch = new double[2048];
            public CoreCourse(long space, long actor) { _space = space; _actor = actor; }
            public bool TryQueryCourse(Vector2 from, Vector2 to, List<Vector2> waypoints) =>
                SpatialBlendAPI.TryQueryEmbodiedCourse(_space, _actor, from, to, _scratch, waypoints);
        }

        /// <summary>
        /// The root goal of the embodied driver (EmbodiedTargetGoal, v1 pacing) walking from <paramref name="from"/> to the root proposal
        /// along the core's course, 60 frames a second for up to 20 s; null when it arrives within 5 cm, else what happened. The
        /// walk uses no separate proposal-following kinematics. The checked driver's own root step takes no
        /// course (TypedRootPositionUsesDirectV1StepWithoutCourseQuery): this measures whether the target is reachable along the
        /// core's course, not the production step.
        /// </summary>
        private static float LastClamp;

        private static string DriveTo(long space, long actor, Vector2 from, AnchorProposal root)
        {
            var goal3 = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            goal3.Set(new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target, default,
                TargetChannels.Position, root.Position, 0d));
            var course = new CoreCourse(space, actor);
            var position = new Vector3(from.x, 0f, from.y);
            var goal = new Vector2(root.Position.x, root.Position.z);
            for (int frame = 0; frame < 1200; ++frame)
            {
                position = goal3.StepPosition(position, 1f / 60f, true, course, out _);
                if (Vector2.Distance(new Vector2(position.x, position.z), goal) < 0.05f) return null;
            }
            // The core's course ends at the free point nearest the target (the NavMesh clamp): arriving there is not a stall.
            var end = new List<Vector2>();
            if (course.TryQueryCourse(from, goal, end) && end.Count > 0 &&
                Vector2.Distance(new Vector2(position.x, position.z), end[end.Count - 1]) < 0.05f)
            {
                LastClamp = Vector2.Distance(end[end.Count - 1], goal);
                return null;
            }
            var waypoints = new List<Vector2>();
            bool ok = course.TryQueryCourse(new Vector2(position.x, position.z), goal, waypoints);
            return $"after 20 s it is {Vector2.Distance(new Vector2(position.x, position.z), goal):0.##} m from it (course {(ok ? waypoints.Count + " waypoints, ending " + (waypoints.Count > 0 ? Vector2.Distance(waypoints[waypoints.Count - 1], goal).ToString("0.##") + " m from it" : "-") : "refused")})";
        }

        /// <summary>
        /// The largest gap any child had to bridge in this peer's tree (metres): how far a region reaches outside the room's ring
        /// (its Navigable parent), or an object's footprint outside its region. 0 when the enlarged room and the pull keep every
        /// child inside its parent, so no result depends on the core's parent-scope extension (its bridge).
        /// </summary>
        private static float LargestGap(Peer p, out string which)
        {
            which = null;
            float worst = 0f;
            Vector2[] room = p.Manager.RegisteredWalkable?.Boundary;
            if (room == null) return 0f;
            var states = (IDictionary)typeof(SpatialBlendManager).GetField("_authoredStateByObject", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p.Manager);
            foreach (object state in states.Values)
            {
                Type t = state.GetType();
                long regionElement = (long)t.GetField("RegionElement").GetValue(state);
                if (regionElement <= 0) continue;
                var plan = (SpatialAuthoringPlan)t.GetField("Plan").GetValue(state);
                var frame = (Matrix4x4)t.GetField("RegionFrameWorld").GetValue(state);
                var parentInverse = (Matrix4x4)t.GetField("RegionParentWorldInverse").GetValue(state);
                var polygon = (Vector2[])t.GetField("RegionPolygon").GetValue(state);
                Vector2[] region = polygon != null
                    ? polygon.Select(v => { Vector3 w = frame.MultiplyPoint3x4(new Vector3(v.x, 0f, v.y)); return new Vector2(w.x, w.z); }).ToArray()
                    : SpatialBlendManager.WorldGroundRing(plan, frame, SpatialHierarchyNodeKind.Region);
                if (region == null) continue;
                if (parentInverse == Matrix4x4.identity)
                {
                    float gap = region.Max(v => WalkablePlacementSnap.Contains(room, v) ? 0f : DistanceToRing(v, room));
                    if (gap > worst) { worst = gap; which = plan.ObjectId + " region outside the room"; }
                }
                Vector2[] footprint = SpatialBlendManager.WorldGroundRing(plan, frame, SpatialHierarchyNodeKind.Object);
                if (footprint != null && polygon == null)
                {
                    float gap = footprint.Max(v => WalkablePlacementSnap.Contains(region, v) ? 0f : DistanceToRing(v, region));
                    if (gap > worst) { worst = gap; which = plan.ObjectId + " footprint outside its region"; }
                }
            }
            return worst;
        }

        private static void RecordGap(Row row, params Peer[] peers)
        {
            float worst = row.Cells.TryGetValue("gap", out string g) ? float.Parse(g, CultureInfo.InvariantCulture) : 0f;
            foreach (Peer p in peers)
            {
                float gap = LargestGap(p, out string which);
                if (gap > 0.01f && gap > worst) row.Failures.Add($"GAP {gap:0.###} m ({p.Name}: {which}): depends on the core's bridge");
                worst = Mathf.Max(worst, gap);
            }
            row.Cells["gap"] = worst.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static Vector2[] BoxFootprint(Transform anchor, Vector3 center, Vector3 size)
        {
            // An MRUK volume's local +Z is up: its ground footprint is the box's local x and y.
            Vector3 h = size * 0.5f;
            var ring = new Vector2[4];
            Vector3[] corners = { new Vector3(-h.x, -h.y, 0f), new Vector3(h.x, -h.y, 0f), new Vector3(h.x, h.y, 0f), new Vector3(-h.x, h.y, 0f) };
            for (int i = 0; i < 4; ++i) { Vector3 w = anchor.TransformPoint(center + corners[i]); ring[i] = new Vector2(w.x, w.z); }
            if (PolygonBoolean.SignedArea(ring) < 0f) Array.Reverse(ring);
            return ring;
        }

        private static Vector2[] Region(GameObject root) => RoomFit.TryRegion(root, out Vector2[] ring) ? ring : null;

        private static Vector2[] Footprint(GameObject root)
        {
            SbSpatialAuthoring authoring = root.GetComponent<SbSpatialAuthoring>();
            if (authoring == null || !authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration d, out _) ||
                !SpatialAuthoringCompiler.TryCompile(d, out SpatialAuthoringPlan plan, out _)) return null;
            return SpatialBlendManager.WorldGroundRing(plan, authoring.transform.localToWorldMatrix * plan.FrameToObject, SpatialHierarchyNodeKind.Object);
        }

        // ---------------------------------------------------------------- free space (the core's: the ring minus the obstacles, at the body radius)

        private static bool IsFree(RoomRegion room, Vector2 p, float radius = BodyRadius)
        {
            if (!WalkablePlacementSnap.Contains(room.Ring, p) || DistanceToRing(p, room.Ring) < radius - 1e-3f) return false;
            foreach (Vector2[] o in room.Obstacles())
            {
                if (o == null || o.Length < 3) continue;
                if (WalkablePlacementSnap.Contains(o, p) || DistanceToRing(p, o) < radius - 1e-3f) return false;
            }
            return true;
        }

        private static float DistanceToRing(Vector2 p, IReadOnlyList<Vector2> ring)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Count; ++i)
            {
                Vector2 a = ring[i], e = ring[(i + 1) % ring.Count] - a;
                float t = e.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, e) / e.sqrMagnitude) : 0f;
                best = Mathf.Min(best, (p - (a + e * t)).magnitude);
            }
            return best;
        }

        private static float DistanceToFootprint(Vector2 p, Vector2[] ring) =>
            WalkablePlacementSnap.Contains(ring, p) ? 0f : DistanceToRing(p, ring);

        private static Vector2 Centroid(Vector2[] ring)
        {
            Vector2 c = Vector2.zero;
            foreach (Vector2 p in ring) c += p;
            return c / ring.Length;
        }

        /// <summary>The free point nearest <paramref name="target"/> (5 cm grid over the ring's bounds), or null when there is none.</summary>
        private static Vector2? NearestFree(RoomRegion room, Vector2 target, Func<Vector2, bool> extra = null)
        {
            Vector2 min = room.Ring[0], max = room.Ring[0];
            foreach (Vector2 p in room.Ring) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Vector2? best = null;
            float bestD = float.MaxValue;
            for (float x = min.x; x <= max.x; x += 0.05f)
            for (float y = min.y; y <= max.y; y += 0.05f)
            {
                var p = new Vector2(x, y);
                float d = (p - target).sqrMagnitude;
                if (d >= bestD || !IsFree(room, p) || !WalkablePlacementSnap.Contains(room.Floor, p)) continue;
                if (extra != null && !extra(p)) continue;
                best = p;
                bestD = d;
            }
            return best;
        }

        // The three user poses of I2: the room centre, near the longest wall, in a corner (each the nearest free spot), looking
        // at the centre (the centre pose looks along the longest wall's inward normal).
        private static List<(string name, Vector2 at, Vector2 look)> UserPoses(RoomRegion room)
        {
            var poses = new List<(string, Vector2, Vector2)>();
            Vector2 centre = Centroid(room.Floor);
            Vector2? c = NearestFree(room, centre);
            if (c == null) return poses;
            int longest = 0;
            for (int i = 0; i < room.Floor.Length; ++i)
                if ((room.Floor[(i + 1) % room.Floor.Length] - room.Floor[i]).sqrMagnitude >
                    (room.Floor[(longest + 1) % room.Floor.Length] - room.Floor[longest]).sqrMagnitude) longest = i;
            Vector2 a = room.Floor[longest], b = room.Floor[(longest + 1) % room.Floor.Length];
            Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized;  // left of a counter-clockwise edge: inward
            poses.Add(("centre", c.Value, n));
            Vector2? w = NearestFree(room, (a + b) * 0.5f + n * 0.5f);
            if (w != null) poses.Add(("wall", w.Value, (c.Value - w.Value).normalized));
            Vector2 corner = room.Floor[0];
            Vector2 inward = (centre - corner).normalized;
            Vector2? k = NearestFree(room, corner + inward * 0.5f);
            if (k != null) poses.Add(("corner", k.Value, (c.Value - k.Value).normalized));
            return poses;
        }

        private static void Look(Peer peer, Vector2 at, Vector2 look)
        {
            float floor = peer.Room.FloorAnchor != null ? peer.Room.FloorAnchor.transform.position.y : 0f;
            peer.Eye = new Vector3(at.x, floor + 1.6f, at.y);
            if (look.sqrMagnitude < 1e-6f) look = Vector2.up;
            peer.Forward = new Vector3(look.x, 0f, look.y).normalized;
        }

        // ---------------------------------------------------------------- results

        private sealed class Row
        {
            public string Key;
            public readonly SortedDictionary<string, string> Cells = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> Failures = new List<string>();
            public bool Excluded;
            public string ExcludedWhy;
            public void Pass(string inv) { if (!Cells.ContainsKey(inv)) Cells[inv] = "ok"; }
            public void Fail(string inv, string why) { Cells[inv] = "FAIL"; Failures.Add(inv + ": " + why); }
        }

        private static void Write(string file, IEnumerable<Row> rows, string[] invariants)
        {
            var sb = new StringBuilder();
            sb.AppendLine("case\t" + string.Join("\t", invariants) + "\tnotes");
            foreach (Row r in rows)
            {
                sb.Append(r.Key);
                foreach (string inv in invariants) sb.Append('\t').Append(r.Excluded ? "excl" : r.Cells.TryGetValue(inv, out string v) ? v : "-");
                sb.Append('\t').Append(r.Excluded ? "excluded: " + r.ExcludedWhy : string.Join(" | ", r.Failures));
                sb.AppendLine();
            }
            Directory.CreateDirectory(MrukRoomExport.ExportDirectory);
            File.WriteAllText(Path.Combine(MrukRoomExport.ExportDirectory, file), sb.ToString());
        }

        [SetUp]
        public void SetUp()
        {
            UnityMainThreadDispatcher.Initialize();
            // EditMode runs no Awake or Update: the dispatcher learns the main thread here and is pumped with the managers.
            typeof(UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            _errors.Clear();
            Application.logMessageReceived += Capture;
            _editorProfile = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, int.MinValue);
            EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, (int)FreeformCollaborationRuntimeProfile.Fusion);
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDownAll()
        {
            Application.logMessageReceived -= Capture;
            if (_editorProfile == int.MinValue) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
            else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, _editorProfile);
            Teardown();
            LogAssert.ignoreFailingMessages = false;
        }

        private void Capture(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors.Add(message);
            else if (type == LogType.Warning && message.StartsWith("[RoomPlacement]")) _placementWarnings.Add(message);
            if (message.StartsWith("[RoomPlacement] initial '")) _initialLogs.Add(message);
            // Any share-time move: spawned objects and received copies are never moved when they are shared.
            if (message.Contains("ShareGameObject: moved") || message.Contains("ShareGameObject: turned")) _shareMoves.Add(message);
        }

        private readonly List<string> _placementWarnings = new List<string>();
        private readonly List<string> _shareMoves = new List<string>();
        private readonly List<string> _initialLogs = new List<string>();

        private static readonly MethodInfo OnRoomLoaded = typeof(FreeformCollaborationApp).GetMethod("OnRoomLoaded", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// The lobby, through the product's own path: FreeformCollaborationApp.OnRoomLoaded reads the room
        /// from MRUK, places the scene's virtual objects (PlaceVirtualObjectsInFront: every active board in the scene) and opens the
        /// furniture menu. Returns the objects the app reported it found no spot for.
        /// </summary>
        private List<GameObject> Lobby(Peer peer, params GameObject[] objects)
        {
            _placementWarnings.Clear();
            _initialLogs.Clear();
            OnRoomLoaded.Invoke(peer.App, null);
            return objects.Where(o => _placementWarnings.Any(w => w.Contains("'" + o.name + "' has no valid spot"))).ToList();
        }

        /// <summary>Why a board fits nowhere: for a wall-mounted one, each wall's longest valid stretch against the region's size.</summary>
        private static string WhyUnfit(Peer peer, GameObject obj)
        {
            PlacementMotif motif = obj.GetComponentInChildren<PlacementMotif>(true);
            Vector2[] region = Region(obj);
            string size = "?";
            if (region != null)
            {
                Transform t = obj.transform;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector2 v in region)
                {
                    Vector3 l = t.InverseTransformPoint(new Vector3(v.x, t.position.y, v.y));
                    minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x); minZ = Mathf.Min(minZ, l.z); maxZ = Mathf.Max(maxZ, l.z);
                }
                size = $"{maxX - minX:0.##} x {maxZ - minZ:0.##} m";
            }
            if (motif == null || !motif.WallPose(Vector3.zero, Vector3.forward).HasValue)
            {
                // Alone (the other board absent): the first spawn's own search, unturned then turned.
                bool alone = RoomFit.PlaceForSpawn(obj, motif, peer.Eye, peer.Forward, peer.Region, null, null, SpatialBlendManager.RegionFitMarginMeters);
                return $"region {size}; alone (the board absent) it {(alone ? "FITS: the room is too small for both boards together" : "fits nowhere either: the room is too small for it")}";
            }
            float[] runs = RoomFit.FreeWallLengths(obj, motif, peer.Region, null, SpatialBlendManager.RegionFitMarginMeters);
            float[] lengths = peer.Region.WallEdges.Select(e => Vector2.Distance(e.a, e.b)).ToArray();
            return $"region {size}; walls {string.Join(",", lengths.Select(x => x.ToString("0.#")))} m long, longest valid stretch on each " +
                   $"{string.Join(",", runs.Select(x => x.ToString("0.#")))} m (a valid spot needs a stretch of one 0.1 m step)";
        }

        /// <summary>A core rejection or a refusal the product logs: every error is one (I0), the expected-free ones are filtered.</summary>
        private List<string> TakeErrors(Peer a, Peer b = null)
        {
            // The test's avatar is not a Meta network avatar, so the received one's embodied driver is not admitted (the test publishes
            // its pose itself): that refusal is this test's, not the product's.
            var list = _errors.Where(e => !e.Contains("checked target-event admission refused")).ToList();
            _errors.Clear();
            foreach (Peer p in new[] { a, b })
                if (p != null && p.Manager.SceneRegistrationError != null) list.Add("core: " + p.Manager.SceneRegistrationError);
            return list.Distinct().ToList();
        }

        // ---------------------------------------------------------------- I0-I3: every room

        [UnityTest, Timeout(int.MaxValue)]
        public IEnumerator EveryRoom()
        {
            LogAssert.ignoreFailingMessages = true;
            var rows = new List<Row>();
            foreach (MrukRoomExport.RoomData data in LoadRooms())
            {
                var row = new Row { Key = data.name };
                rows.Add(row);
                MRUKRoom room = Build(data, "single");
                Peer peer = MakePeer("single", room);
                if (peer.Region == null)
                {
                    row.Fail("I1", "the room has no usable floor outline");
                    Teardown(peer);
                    continue;
                }
                List<(string name, Vector2 at, Vector2 look)> poses = UserPoses(peer.Region);
                // The boards where a scene puts them, not where the first spawn would (the Whiteboard at the middle of the
                // longest wall, whatever stands there): the app start re-places an invalid one by the initial rule before it is
                // shared (PlaceInvalidVirtualObjects), so the share moves nothing and the board is on a wall (or, no wall fitting,
                // on the floor).
                if (poses.Count > 0) poses.Add(("scene", poses[0].at, poses[0].look));
                // I3: free space, and the start pose (the centre pose) in it.
                if (poses.Count == 0)
                {
                    row.Excluded = true;
                    row.ExcludedWhy = "no free point at the body radius (0.2 m) on the floor";
                    Teardown(peer);
                    continue;
                }
                row.Pass("I3");
                bool firstPose = true;
                foreach ((string name, Vector2 at, Vector2 look) pose in poses)
                {
                    if (!firstPose) { Teardown(peer); room = Build(data, "single"); peer = MakePeer("single", room); }
                    firstPose = false;
                    Look(peer, pose.at, pose.look);
                    // The lobby, the product's path: OnRoomLoaded reads the room from MRUK and places the scene's boards in front of
                    // the user, pulled to valid spots (PlaceVirtualObjectsInFront).
                    GameObject chess = EntityRoot(Spawn(ChessPrefab, "Chess")), board = EntityRoot(Spawn(WhiteboardPrefab, "Whiteboard"));
                    SetNetworkId(chess, 1);
                    SetNetworkId(board, 2);
                    List<GameObject> unfit = Lobby(peer, chess, board);
                    bool chessFits = !unfit.Contains(chess), boardFits = !unfit.Contains(board);
                    if (pose.name == "scene")
                    {
                        if (peer.Region.WallEdges.Length > 0)
                        {
                            var e = peer.Region.WallEdges.OrderByDescending(w => Vector2.Distance(w.a, w.b)).First();
                            Vector2 mid = (e.a + e.b) * 0.5f;
                            Pose wp = board.GetComponentInChildren<PlacementMotif>(true).WallPose(new Vector3(mid.x, 0f, mid.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
                            board.transform.SetPositionAndRotation(wp.position, wp.rotation);
                        }
                        chessFits = boardFits = true;
                    }
                    var spawned = new[] { chess, board }.ToDictionary(o => o, o => new Pose(o.transform.position, o.transform.rotation));
                    _shareMoves.Clear();
                    if (pose.name != "scene" && chessFits && boardFits)
                    {
                        // The initial poses: the Whiteboard on a wall (within 5 cm of its spot, facing in or out
                        // squarely), Chess on the open floor (its footprint clear of every MRUK footprint).
                        var bp0 = new Vector2(board.transform.position.x, board.transform.position.z);
                        var nearest = peer.Region.WallEdges.OrderBy(w => DistanceToSegment(bp0, w.a, w.b)).FirstOrDefault();
                        float off = peer.Region.WallEdges.Length > 0 ? DistanceToSegment(bp0, nearest.a, nearest.b) : 99f;
                        Vector3 fwd = board.transform.forward;
                        float square = Mathf.Abs(Vector2.Dot(new Vector2(fwd.x, fwd.z).normalized, nearest.inward));
                        // No wall fits → the Whiteboard stands on the open floor (the v1 panel fallback).
                        bool boardOnFloor = _initialLogs.Any(l => l.Contains("'" + board.name + "'") && l.Contains("no wall fits"));
                        if (boardOnFloor) row.Cells["boardFloor"] = "floor";
                        else if (off > 0.1f + 0.05f || square < 0.95f) row.Fail("I2", $"{pose.name}: the initial Whiteboard is not on a wall ({off:0.##} m from it, facing {square:0.##})");
                        foreach (GameObject o in boardOnFloor ? new[] { chess, board } : new[] { chess })
                        {
                            Vector2[] cf = Footprint(o);
                            foreach (Vector2[] f in FurnitureFeet(peer))
                                if (cf != null && IndependentGeometry.Overlap(cf, f, 0.01f) > 1e-3f) row.Fail("I2", $"{pose.name}: the initial {o.name} stands on furniture");
                        }
                    }
                    float WallDistance(GameObject o) => peer.Region.WallEdges.Length > 0
                        ? peer.Region.WallEdges.Min(w => DistanceToSegment(new Vector2(o.transform.position.x, o.transform.position.z), w.a, w.b)) : 0f;
                    bool boardOnWallAtSpawn = WallDistance(board) <= 0.15f;
                    _errors.Clear();
                    _initialLogs.Clear();
                    // The session: the app start shares the boards (the manager pulls them again against the core's tree), registers
                    // the room and its furniture, then shares the marked furniture.
                    yield return Connect(peer, null);
                    RegisterRoom(peer);
                    if (peer.Manager.RegisteredRoom == null) row.Fail("I1", "the room ring was refused");
                    if (!chessFits || !boardFits)
                    {
                        row.Excluded = true;
                        row.ExcludedWhy = $"no non-overlapping spot for {(!chessFits ? "Chess" : "")}{(!chessFits && !boardFits ? " and " : "")}{(!boardFits ? "the Whiteboard" : "")} (pose {pose.name})" +
                                          (!boardFits ? "; Whiteboard: " + WhyUnfit(peer, board) : "") + (!chessFits ? "; Chess: " + WhyUnfit(peer, chess) : "");
                        break;
                    }
                    yield return Pump(0.7f, peer);
                    bool chessOk = SharedWithRegion(peer, chess, out string chessWhy);
                    bool boardOk = SharedWithRegion(peer, board, out string boardWhy);
                    List<string> placement = TakeErrors(peer);
                    RecordGap(row, peer);
                    if (peer.Manager.RegisteredRoom != null && !placement.Any(e => e.Contains("scene") || e.Contains("Scene") || e.Contains("Geometry"))) row.Pass("I1");
                    else if (placement.Count > 0) row.Fail("I1", string.Join("; ", placement.Take(3)));
                    if (!chessOk) row.Fail("I2", $"{pose.name}: Chess {chessWhy}");
                    if (!boardOk) row.Fail("I2", $"{pose.name}: Whiteboard {boardWhy}");
                    if (placement.Count > 0) row.Fail("I0", $"{pose.name}: " + string.Join("; ", placement));
                    // The placements are valid by a second, separate rule (IndependentGeometry, not RoomFit's): the core accepted
                    // them (above), and the regions lie inside the room, clear of the furniture MRUK holds and of each other, the
                    // objects on the floor.
                    foreach (string f in Check(peer, new[] { chess, board }, new GameObject[0])) row.Fail("I2", $"{pose.name}: {f}");
                    // One validity rule: what the first spawn placed validly is never moved when it is shared, and the Whiteboard
                    // stays on its wall. ("scene": the poses were not validated by a spawn; the app start re-placed an invalid one.)
                    foreach (string m in _shareMoves) row.Fail("I2", $"{pose.name}: {m}");
                    if (pose.name == "scene")
                        row.Cells["sceneFix"] = _initialLogs.Count == 0 ? "none"
                            : string.Join("+", _initialLogs.Select(l => l.Contains("'" + board.name + "'") ? "board" : "chess"));
                    foreach (var sp in spawned)
                    {
                        // The scene's poses are re-placed at the app start when invalid (before the share): only the share is judged.
                        if (pose.name == "scene") continue;
                        float moved = Vector3.Distance(sp.Key.transform.position, sp.Value.position);
                        if (moved > 0.01f) row.Fail("I2", $"{pose.name}: {sp.Key.name} moved {moved:0.##} m between its spawn and its share");
                    }
                    var bp = new Vector2(board.transform.position.x, board.transform.position.z);
                    float toWall = peer.Region.WallEdges.Length > 0 ? peer.Region.WallEdges.Min(w => DistanceToSegment(bp, w.a, w.b)) : 0f;
                    bool boardFloorNow = row.Cells.TryGetValue("boardFloor", out string bf) && bf == "floor" ||
                                         _initialLogs.Any(l => l.Contains("'" + board.name + "'") && l.Contains("no wall fits"));
                    if ((pose.name == "scene" ? !boardFloorNow : boardOnWallAtSpawn) && toWall > 0.1f + 0.05f) row.Fail("I2", $"{pose.name}: the Whiteboard {(pose.name == "scene" ? "was put on a wall" : "spawned on a wall")} and is {toWall:0.##} m from it after its share");
                    row.Cells["toWall"] = Mathf.Max(row.Cells.TryGetValue("toWall", out string tw) ? float.Parse(tw, CultureInfo.InvariantCulture) : 0f, toWall).ToString("0.##", CultureInfo.InvariantCulture);
                    if (SpatialBlendManager.IsPhysicalObject(chess) == false && peer.Manager.TryGetAuthoredRegion(chess.GetComponent<SbEntity>().ID, out long creg))
                    {
                        var st = ((IDictionary)AuthoredStates.GetValue(peer.Manager))[chess.GetComponent<SbEntity>().ID];
                        if (st != null && (Matrix4x4)st.GetType().GetField("RegionParentWorldInverse").GetValue(st) != Matrix4x4.identity)
                            row.Cells["chessOn"] = "furniture";
                    }
                    row.Pass("I2");
                    row.Pass("I0");
                    // I6 (the first pose): the drop, the wall drop, an own scan beside furniture, late furniture.
                    if (pose.Equals(poses[0]))
                    {
                        IEnumerator cases = Cases(peer, chess, board, row);
                        while (cases.MoveNext()) yield return cases.Current;
                    }
                }
                Teardown(peer);
                Write("results-rooms.tsv", rows, new[] { "I0", "I1", "I2", "I3", "I6", "gap", "wallDrop", "scan", "trim", "toWall", "chessOn", "standOffDrop", "carry", "boardFloor", "sceneFix" });
            }
            Write("results-rooms.tsv", rows, new[] { "I0", "I1", "I2", "I3", "I6", "gap", "wallDrop", "scan", "trim", "toWall", "chessOn", "standOffDrop", "carry", "boardFloor", "sceneFix" });
            List<string> failures = rows.Where(r => !r.Excluded).SelectMany(r => r.Failures.Where(f => !f.StartsWith("GAP")).Select(f => r.Key + " " + f)).ToList();
            Debug.Log($"[RoomMatrix] rooms: {rows.Count}, excluded {rows.Count(r => r.Excluded)}, failing {rows.Count(r => !r.Excluded && r.Failures.Count > 0)}");
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)));
        }

        // ---------------------------------------------------------------- I0, I4, I5: pairs

        private static IEnumerable<(MrukRoomExport.RoomData host, MrukRoomExport.RoomData joiner)> Pairs(List<MrukRoomExport.RoomData> rooms)
        {
            var seen = new HashSet<string>();
            var byName = rooms.ToDictionary(r => r.name);
            // SPATIALBLEND_ROOM_MATRIX_PAIRS="Host|Joiner;Host|Joiner": exactly these pairs.
            string only = Environment.GetEnvironmentVariable("SPATIALBLEND_ROOM_MATRIX_PAIRS");
            if (!string.IsNullOrEmpty(only))
            {
                foreach (string pair in only.Split(';'))
                {
                    string[] two = pair.Split('|');
                    if (two.Length == 2 && byName.ContainsKey(two[0]) && byName.ContainsKey(two[1])) yield return (byName[two[0]], byName[two[1]]);
                }
                yield break;
            }
            // The Windows harness's pair: the scene's MRUK RoomPrefabs 1 and 8 (LivingRoom01, Office08), both ways.
            foreach ((string h, string j) in new[] { ("LivingRoom01", "Office08"), ("Office08", "LivingRoom01") })
                if (byName.ContainsKey(h) && byName.ContainsKey(j) && seen.Add(h + "|" + j)) yield return (byName[h], byName[j]);
            for (int i = 0; i < rooms.Count; ++i)
            {
                // Every room as host against five diverse joiners (spread over the list), and against itself.
                var joiners = new List<int> { i };
                for (int k = 1; k <= 5; ++k) joiners.Add((i + k * Math.Max(1, rooms.Count / 6)) % rooms.Count);
                foreach (int j in joiners)
                    if (seen.Add(rooms[i].name + "|" + rooms[j].name)) yield return (rooms[i], rooms[j]);
            }
            var other = rooms.Where(r => r.source.Contains("/Other/")).ToList();
            foreach (var h in other)
            foreach (var j in other)
                if (seen.Add(h.name + "|" + j.name)) yield return (h, j);
        }

        private static readonly string[] PairColumns = { "I0", "I4", "I5", "gap", "link", "hostChessOn", "place", "trim", "inexact", "near", "clamp", "walk" };

        [UnityTest, Timeout(int.MaxValue)]
        public IEnumerator EveryPair()
        {
            s_criteria.Clear();
            s_stepA.Clear();
            s_stepC.Clear();
            s_targetClasses.Clear();
            LogAssert.ignoreFailingMessages = true;
            var rows = new List<Row>();
            List<MrukRoomExport.RoomData> rooms = LoadRooms();
            int limit = int.TryParse(Environment.GetEnvironmentVariable("SPATIALBLEND_ROOM_MATRIX_PAIR_LIMIT"), out int l) ? l : int.MaxValue;
            foreach ((MrukRoomExport.RoomData h, MrukRoomExport.RoomData j) in Pairs(rooms).Take(limit))
            {
                var row = new Row { Key = h.name + " x " + j.name };
                rows.Add(row);
                IEnumerator run = RunPair(h, j, row);
                while (true)
                {
                    object current;
                    try
                    {
                        if (!run.MoveNext()) break;
                        current = run.Current;
                    }
                    catch (Exception e)
                    {
                        row.Fail("I0", "exception: " + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    yield return current;
                }
                Teardown();
                Write("results-pairs.tsv", rows, PairColumns);
                WriteCriteria();
            }
            Write("results-pairs.tsv", rows, PairColumns);
            WriteCriteria();
            List<string> failures = rows.Where(r => !r.Excluded).SelectMany(r => r.Failures.Where(f => !f.StartsWith("GAP")).Select(f => r.Key + " " + f)).ToList();
            Debug.Log($"[RoomMatrix] pairs: {rows.Count}, excluded {rows.Count(r => r.Excluded)}, failing {rows.Count(r => !r.Excluded && r.Failures.Count > 0)}");
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)));
        }

        private IEnumerator RunPair(MrukRoomExport.RoomData hostData, MrukRoomExport.RoomData joinerData, Row row)
        {
            Peer host = MakePeer("host", Build(hostData, "host"));
            Peer joiner = MakePeer("joiner", Build(joinerData, "joiner"));
            if (host.Region == null || joiner.Region == null) { row.Excluded = true; row.ExcludedWhy = "no floor outline"; yield break; }
            var hostPoses = UserPoses(host.Region);
            var joinerPoses = UserPoses(joiner.Region);
            if (hostPoses.Count == 0 || joinerPoses.Count == 0) { row.Excluded = true; row.ExcludedWhy = "no free space"; yield break; }
            Look(host, hostPoses[0].at, hostPoses[0].look);
            Look(joiner, joinerPoses[0].at, joinerPoses[0].look);
            // One Unity world holds both devices' scenes: each app's lobby (PlaceVirtualObjectsInFront) and start
            // (RegisterSceneObjects, Fusion's network-id lookup) must see only its own device's boards, so the other device's are
            // inactive meanwhile; the host's stay inactive after.
            void Active(Peer p, bool on) { p.Chess.transform.root.gameObject.SetActive(on); p.Whiteboard.transform.root.gameObject.SetActive(on); }
            foreach (Peer p in new[] { host, joiner })
            {
                p.Chess = EntityRoot(Spawn(ChessPrefab, "Chess-" + p.Name));
                p.Whiteboard = EntityRoot(Spawn(WhiteboardPrefab, "Whiteboard-" + p.Name));
                SetNetworkId(p.Chess, 1);
                SetNetworkId(p.Whiteboard, 2);
            }
            foreach (Peer p in new[] { host, joiner })
            {
                Active(host, p == host);
                Active(joiner, p == joiner);
                List<GameObject> unfit = Lobby(p, p.Chess, p.Whiteboard);
                if (unfit.Count > 0) { row.Excluded = true; row.ExcludedWhy = $"{p.Name} room too small/cluttered for the shared objects"; yield break; }
            }
            Active(host, true);
            Active(joiner, false);
            _errors.Clear();
            _shareMoves.Clear();
            yield return Connect(host, joiner, () => { Active(host, false); Active(joiner, true); });
            RegisterRoom(host);
            RegisterRoom(joiner);
            yield return Pump(1.2f, host, joiner);
            // Condition-driven, not a fixed budget: the first pair of a fresh Editor (JIT, core start) resolves the joiner's deferred
            // TABLE link later; wait until every TABLE the host shared is linked or boxed here (at most 20 s worth of frames).
            for (int f = 0; f < 3000; f += 30)
            {
                int hostShared = host.App.ShareableAnchors().Count(r => FurnitureSharePolicy.IsDesk(r) && host.App.ShareStateOf(r) == AnchorShareState.Shared);
                int resolved = joiner.App.ShareableAnchors().Count(r => FurnitureSharePolicy.IsDesk(r) && joiner.App.ShareStateOf(r) == AnchorShareState.Linked)
                               + joiner.App.SharedBoxes.Count;
                if (resolved >= hostShared) break;
                yield return PumpFrames(30, host, joiner);
            }
            foreach (Peer p in new[] { host, joiner })
            {
                if (!SharedWithRegion(p, p.Chess, out string w1)) row.Fail("I4", $"{p.Name} Chess: {w1}");
                if (!SharedWithRegion(p, p.Whiteboard, out string w2)) row.Fail("I4", $"{p.Name} Whiteboard: {w2}");
            }

            foreach (string m in _shareMoves) row.Fail("I4", "share-time move: " + m);
            RecordGap(row, host, joiner);
            // I4: the host's marked TABLEs are linked to the joiner's TABLEs, or arrive there as boxes to install.
            List<SceneAnchorRecord> hostTables = host.App.ShareableAnchors().Where(FurnitureSharePolicy.IsDesk).ToList();
            var shared = hostTables.Where(r => host.App.ShareStateOf(r) == AnchorShareState.Shared).ToList();
            int expected = Math.Min(FurnitureSharePolicy.DefaultDeskCount, hostTables.Count);
            if (shared.Count != expected) row.Fail("I4", $"host shared {shared.Count} of its {hostTables.Count} TABLEs, expected {expected}");
            List<SceneAnchorRecord> joinerTables = joiner.App.ShareableAnchors().Where(FurnitureSharePolicy.IsDesk).ToList();
            int linked = joinerTables.Count(r => joiner.App.ShareStateOf(r) == AnchorShareState.Linked);
            int boxes = joiner.App.SharedBoxes.Count;
            int wantLinked = Math.Min(shared.Count, joinerTables.Count);
            if (linked != wantLinked || linked + boxes != shared.Count)
                row.Fail("I4", $"joiner linked {linked} TABLEs (want {wantLinked}) and has {boxes} boxes, host shared {shared.Count}");
            // Largest first: the joiner's linked tables are its largest ones.
            var joinerOrder = joinerTables.OrderBy(r => r, Comparer<SceneAnchorRecord>.Create(FurnitureSharePolicy.CompareDesks)).ToList();
            for (int i = 0; i < Math.Min(linked, joinerOrder.Count); ++i)
                if (joiner.App.ShareStateOf(joinerOrder[i]) != AnchorShareState.Linked)
                    row.Fail("I4", $"the joiner's {i + 1}th largest TABLE is not linked");
            // The link itself: each shared host TABLE's identity resolves in the joiner's space, to its linked TABLE or its box.
            foreach (SceneAnchorRecord r in shared)
            {
                var e = r.Anchor.GetComponent<SbMRUKAnchorEntity>();
                long id = SharedAnchorName.SharedId(e.SceneLabel, e.ShareKey);
                if (SpatialBlendAPI.FindElementBySharedId(joiner.Manager.ID, id) <= 0)
                    row.Fail("I4", $"host TABLE {e.ShareKey} has no element in the joiner's space");
                if (!joiner.Manager.TryGetAuthoredRegion(SpatialBlendAPI.FindElementBySharedId(joiner.Manager.ID, id), out long reg) || reg <= 0)
                {
                    // A linked anchor's region is the joiner's own (owned); a box's is its own authored one.
                    bool linkedHere = joinerTables.Any(t => joiner.App.ShareStateOf(t) == AnchorShareState.Linked &&
                        t.Anchor.GetComponent<SbMRUKAnchorEntity>()?.ShareKey == e.ShareKey);
                    if (!linkedHere) row.Fail("I4", $"host TABLE {e.ShareKey} arrived without a region in the joiner's space");
                }
            }
            row.Cells["link"] = linked > 0 ? "table" : boxes > 0 ? "box" : "-";
            {
                var hs = (IDictionary)AuthoredStates.GetValue(host.Manager);
                long hid = host.Chess.GetComponent<SbEntity>().ID;
                bool nested = hs.Contains(hid) && (Matrix4x4)hs[hid].GetType().GetField("RegionParentWorldInverse").GetValue(hs[hid]) != Matrix4x4.identity;
                row.Cells["hostChessOn"] = nested ? "furniture" : "-";
                if (nested) row.Fail("I4", "the host's Chess is nested under furniture (Chess is never on a table)");
            }
            void CountTrims() => row.Cells["trim"] = $"{joiner.Manager.VirtualRegionsTrimmed}/{joiner.Manager.VirtualRegionsWithNoFreePart}";
            row.Pass("I4");

            // The box the joiner installs over its real table (a spot MRUK has no anchor for: free floor): placed exactly where the
            // user puts it, never pulled; its region fitted around it, the virtual objects stepped off it.
            foreach (SharedRoomObjectBox box in joiner.App.SharedBoxes.ToList())
            {
                SbMRUKAnchorEntity e = box.Entity;
                if (e == null || !e.TryGetMeasuredBox(out Vector3 bc, out Vector3 bs)) continue;
                _errors.Clear();
                Vector2 c0 = Centroid(BoxFootprint(e.transform, bc, bs));
                Vector2? spot = NearestFree(joiner.Region, Centroid(joiner.Region.Floor), q =>
                {
                    Vector2[] foot = BoxFootprint(e.transform, bc, bs).Select(v => v - c0 + q).ToArray();
                    return IndependentGeometry.Contained(foot, joiner.Region.Floor, 0f, out _) &&
                           FurnitureFeet(joiner).All(f => IndependentGeometry.Overlap(foot, f, 0f) < 1e-4f);
                });
                if (spot == null) { row.Cells["place"] = "no free spot"; continue; }
                e.transform.position += new Vector3(spot.Value.x - c0.x, 0f, spot.Value.y - c0.y);
                Vector3 dropped = e.transform.position;
                box.Place();
                yield return Pump(0.5f, host, joiner);
                if (Vector3.Distance(e.transform.position, dropped) > 1e-3f)
                    row.Fail("I4", $"box placement: the box moved {Vector3.Distance(e.transform.position, dropped):0.###} m from where it was placed");
                foreach (string err in TakeErrors(joiner)) row.Fail("I0", "box placement: " + err + Identify(joiner, new List<string> { err }));
                foreach (string f in Check(joiner, new[] { joiner.Chess, joiner.Whiteboard }, new[] { e.gameObject })) row.Fail("I4", "box placement: " + f);
                row.Cells["place"] = "ok";
            }
            CountTrims();

            // The avatar: the host's user shares its avatar and the app's relations; the joiner's app adds it as a new replica.
            host.Avatar = new GameObject("avatar-host") { tag = "Avatar" };
            _objects.Add(host.Avatar);
            host.Avatar.transform.position = new Vector3(hostPoses[0].at.x, 0f, hostPoses[0].at.y);
            host.Avatar.AddComponent<SbLocalEntity>();
            host.Manager.ShareGameObject(host.Avatar, true, true);
            yield return Pump(0.5f, host, joiner);
            foreach ((string anchor, RelationKind kind) in FreeformCollaborationApp.AvatarRelationBindings)
                host.Manager.RegisterRelation(new SpatialRelation(host.Avatar, anchor, kind));
            yield return Pump(1.0f, host, joiner);
            List<string> setup = TakeErrors(host, joiner);
            if (setup.Count > 0) row.Fail("I0", string.Join("; ", setup.Take(5)) + Identify(joiner, setup));

            // The receiver: the joiner's actor for the host's avatar replica (the received copy, not a source of its own),
            // selected explicitly, never "the last entry".
            var actors = (IDictionary)ActorByEntity.GetValue(joiner.Manager);
            var received = actors.Keys.Cast<long>().Where(id => joiner.Manager.GetEntity(id) is SbEntity en && !en.IsSource).OrderBy(id => id).ToList();
            if (received.Count != 1) { row.Fail("I5", $"the joiner has {received.Count} actors for received avatars, expected exactly the host's"); yield break; }
            long receiver = (long)actors[received[0]];

            // I5: the source at 0.5, 1 and 1.5 m from Chess, and far from it.
            Vector2[] chessFoot = Footprint(host.Chess);
            var referents = new Dictionary<long, (Vector2[] host, Vector2[] joiner)>
            {
                [ChessId] = (Footprint(host.Chess), Footprint(joiner.Chess)),
                [SpatialAuthoringCompiler.RegionSharedId(ChessId)] = (Footprint(host.Chess), Footprint(joiner.Chess)),
                [WhiteboardId] = (Footprint(host.Whiteboard), Footprint(joiner.Whiteboard)),
                [SpatialAuthoringCompiler.RegionSharedId(WhiteboardId)] = (Footprint(host.Whiteboard), Footprint(joiner.Whiteboard)),
            };
            SbAnchorProposal[] scratch = null;
            var set = new ProposalSet();
            // The received avatar's realized pose, as its embodied driver publishes it each frame (the driver needs a network
            // avatar with a retarget rig, which this test's avatar is not): it starts at the joiner's own spawn and follows each
            // root proposal (never reset to the source's pose).
            var receiverAt = new Vector3(joinerPoses[0].at.x, 0f, joinerPoses[0].at.y);
            ulong sequence = 0;
            void PublishReceiver() =>
                SpatialBlendAPI.TryPublishAnchorPose(joiner.Manager.ID, receiver, 0, new EmbodiedPose(receiverAt, 0f),
                    (sequence + 1) * 0.1, ++sequence, 1, sequence == 1);   // deterministic sample times (wall-clock ones gave the same targets)
            // The shared furniture's footprints, by shared id: each peer's anchors registered under a share key (linked or shared),
            // and a box the joiner installs for a host table it has none of.
            foreach (Peer p in new[] { host, joiner })
                foreach (SbMRUKAnchorEntity anchor in UnityEngine.Object.FindObjectsByType<SbMRUKAnchorEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => x.name, StringComparer.Ordinal))
                {
                    if (anchor == null || string.IsNullOrEmpty(anchor.ShareKey) || !anchor.IsAttached()) continue;
                    bool mine = anchor.transform.IsChildOf(p.Room.transform) || p.Manager.GetEntity(anchor.ID) == anchor;
                    if (!mine) continue;
                    long id = SharedAnchorName.SharedId(anchor.SceneLabel, anchor.ShareKey);
                    if (!anchor.TryGetMeasuredBox(out Vector3 c, out Vector3 size)) continue;
                    Vector2[] foot = BoxFootprint(anchor.transform, c, size);
                    foreach (long key in new[] { id, SpatialAuthoringCompiler.RegionSharedId(id) })
                    {
                        referents.TryGetValue(key, out var feet);
                        referents[key] = p == host ? (foot, feet.joiner) : (feet.host, foot);
                    }
                }
            long sourceActor = (long)((IDictionary)ActorByEntity.GetValue(host.Manager))[host.Avatar.GetComponent<SbEntity>().ID];
            var states = new List<RelationStateV2Row>();
            // I5 by the core's contract (ratio continuation, remote_update.h / relation_solver.h): the source
            // WALKS, at most 5 cm an update, to each shared object from far to 0.5 m of it and back out; the receiver follows.
            //  (a) approaching, the receiver's distance to the counterpart tracks the source's ratio: d_rcv = d_rcv0 * d_src / d_src0;
            //  (b) at the end of the approach the distance is preserved when the walk started at matching distances, else the ratio;
            //  (c) walking away holds the target; (d) the target is in free space.
            // (e) The core installs an async scope-extension build on whichever step it finishes: settle after the scene changes
            // before stepping (a residual run-to-run difference remains and is reported, not failed).
            yield return Pump(1.0f, host, joiner);
            var current = new StepResult();
            IEnumerator StepTo(Vector2 q)
            {
                host.Avatar.transform.position = new Vector3(q.x, 0f, q.y);
                current.Got = false;
                for (int k = 0; k < 2; ++k)
                {
                    PublishReceiver();
                    yield return PumpFrames(StepFrames, host, joiner);
                    if (!SpatialBlendAPI.TryReadProposals(joiner.Manager.ID, receiver, ref scratch, set) || !set.HasRoot) continue;
                    if (set.Root.PositionUnspecified || !(set.Root.Status == EmbodiedStatus.Exact || set.Root.Status == EmbodiedStatus.Inexact)) continue;
                    receiverAt = new Vector3(set.Root.Position.x, 0f, set.Root.Position.z);
                    current.Got = true;
                    current.Target = new Vector2(receiverAt.x, receiverAt.z);
                    current.Status = set.Root.Status;
                    current.Error = (float)scratch.First(x => x.anchor_id == 0).err_spacing_target;
                    // The DRIVING referent of the root's solve (sb_anchor_proposal_t::referent_shared_id, the heaviest relation;
                    // filled by the core), not the nearest object.
                    current.Referent = scratch.First(x => x.anchor_id == 0).referent_shared_id;
                }
            }
            IEnumerable<Vector2> Walk(Vector2 from, Vector2 to)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / WalkStepMeters));
                for (int i = 1; i <= n; ++i) yield return Vector2.Lerp(from, to, i / (float)n);
            }
            foreach ((string label, GameObject hostObj, GameObject joinObj, long id) in new[]
                     { ("Chess", host.Chess, joiner.Chess, ChessId), ("Whiteboard", host.Whiteboard, joiner.Whiteboard, WhiteboardId) })
            {
                Vector2[] hf = Footprint(hostObj), jf = Footprint(joinObj);
                if (hf == null || jf == null) { row.Fail("I5", $"{label}: no footprint"); continue; }
                var ids = new HashSet<long> { id, SpatialAuthoringCompiler.RegionSharedId(id) };
                Vector2 hc = Centroid(hf), outward = (Centroid(host.Region.Floor) - hc).sqrMagnitude > 1e-6f ? (Centroid(host.Region.Floor) - hc).normalized : Vector2.right;
                Vector2? near = NearestFree(host.Region, hc + outward * 0.9f, q => Mathf.Abs(DistanceToFootprint(q, hf) - 0.5f) < 0.05f);
                Vector2? far = NearestFree(host.Region, hc + outward * 4f, q => DistanceToFootprint(q, hf) > 1.5f);
                if (near == null || far == null) { row.Cells["walk"] = "no path"; continue; }
                // To the start, then settle (the jump is not measured).
                yield return StepTo(far.Value);
                yield return StepTo(far.Value);
                if (!current.Got) { row.Fail("I5", $"{label}: no root proposal with a target at the walk's start (status {set.Root.Status})"); continue; }
                // Each step is judged against the referent the core used at that step (sb_get_relation_states_v2's dominant active
                // Spacing row), by the contract: approaching it, the receiver's distance to its counterpart scales by the source's
                // ratio (a); moving away from it or standing, the target holds (c). A step where the referent changed is a transition.
                long prevRef = current.Referent;
                Vector2 prevSrc = far.Value, prevTarget = current.Target;
                float worstA = 0f, worstC = 0f;
                int measuredA = 0, measuredC = 0, transitions = 0, notFree = 0;
                string lastNotFree = null;
                // (b): the last unbroken run of steps with this object as the referent, from its first step to the walk's end.
                float runS = float.NaN, runR = float.NaN;
                bool ended = false;
                float errB = float.NaN;
                bool matching = false, inexact = false;
                foreach ((Vector2 q, bool approach) in Walk(far.Value, near.Value).Select(x => (x, true)).Concat(Walk(near.Value, far.Value).Select(x => (x, false))))
                {
                    yield return StepTo(q);
                    if (!current.Got) continue;
                    long r = current.Referent;
                    string dClass = ClassifyTarget(joiner, current.Target, r, referents);
                    s_targetClasses[dClass] = (s_targetClasses.TryGetValue(dClass, out int dc) ? dc : 0) + 1;
                    if (dClass.StartsWith("FAIL")) { ++notFree; lastNotFree = dClass; }
                    // The step's kind: approaching the driving referent (a), approaching no shared object at all (c: the target
                    // holds), or approaching another object than the driving one / a change of the driving referent (a transition).
                    bool approachesAny = referents.Values.Where(v => v.host != null).Distinct()
                        .Any(v => DistanceToFootprint(q, v.host) < DistanceToFootprint(prevSrc, v.host) - 1e-4f);
                    bool known = referents.TryGetValue(r, out var rf) && rf.host != null && rf.joiner != null;
                    float dsOld = known ? DistanceToFootprint(prevSrc, rf.host) : 0f, dsNew = known ? DistanceToFootprint(q, rf.host) : 0f;
                    if (!approachesAny)
                    {
                        float e = Vector2.Distance(current.Target, prevTarget);
                        s_stepC.Add(e);
                        worstC = Mathf.Max(worstC, e);
                        ++measuredC;
                    }
                    else if (known && r == prevRef && dsNew < dsOld - 1e-4f)
                    {
                        float drOld = DistanceToFootprint(prevTarget, rf.joiner), drNew = DistanceToFootprint(current.Target, rf.joiner);
                        float e = Mathf.Abs(drNew - drOld * dsNew / Mathf.Max(dsOld, 1e-3f));
                        s_stepA.Add(e);
                        worstA = Mathf.Max(worstA, e);
                        ++measuredA;
                        if (approach && ids.Contains(r) && float.IsNaN(runS)) { runS = dsOld; runR = drOld; }
                    }
                    else
                    {
                        ++transitions;
                        if (approach) runS = runR = float.NaN;
                    }
                    if (approach && !ids.Contains(r)) runS = runR = float.NaN;
                    if (approach && q == near.Value && !ended)
                    {
                        ended = true;
                        inexact = current.Status == EmbodiedStatus.Inexact;
                        if (!float.IsNaN(runS))
                        {
                            float sEnd = DistanceToFootprint(q, hf), rEnd = DistanceToFootprint(current.Target, jf);
                            matching = Mathf.Abs(runR - runS) <= MatchingStartMeters;
                            errB = matching ? Mathf.Abs(rEnd - sEnd) : Mathf.Abs(rEnd - runR * sEnd / Mathf.Max(runS, 1e-3f));
                        }
                    }
                    prevRef = r;
                    prevSrc = q;
                    prevTarget = current.Target;
                }
                s_criteria.Add((row.Key, label, measuredA > 0 ? worstA : float.NaN, errB, matching, measuredC > 0 ? worstC : float.NaN, notFree, inexact, transitions));
                Debug.Log($"[RoomMatrix] I5 {row.Key} {label}: (a) worst {worstA:0.###} m over {measuredA} approach steps; (b) {(float.IsNaN(errB) ? "n/a (the walk did not end on it)" : $"{(matching ? "distance" : "ratio")} error {errB:0.###} m")}; " +
                          $"(c) worst {worstC:0.###} m over {measuredC} away steps; {transitions} transitions; (d) {notFree} failing targets; status {current.Status}");
                bool endOk = float.IsNaN(errB) || errB <= EndToleranceMeters;
                // An Inexact end is the core's multi-referent compromise: reported in "inexact", not a failure when the
                // nearest-referent check (b) passes.
                bool excused = inexact && endOk;
                if (inexact) row.Cells["inexact"] = (row.Cells.TryGetValue("inexact", out string ix) ? ix + "," : "") + label + (endOk ? ":ok" : ":miss");
                if (!excused && measuredA > 0 && worstA > RatioToleranceMeters) row.Fail("I5", $"{label}: (a) an approach step left the ratio by {worstA:0.###} m");
                if (!endOk) row.Fail("I5", $"{label}: (b) the end of the approach misses by {errB:0.###} m ({(matching ? "distance" : "ratio")}, status {current.Status})");
                if (!excused && measuredC > 0 && worstC > HeldToleranceMeters) row.Fail("I5", $"{label}: (c) a step away moved the target {worstC:0.###} m");
                if (notFree > 0) row.Fail("I5", $"{label}: (d) {notFree} targets outside the core's free space or inside an unregistered footprint (last: {lastNotFree})");
                if (!current.Got) continue;
                if (!IsFree(joiner.Region, current.Target, BodyRadius - 0.02f))
                {
                    LastClamp = 0f;
                    string stall = DriveTo(joiner.Manager.ID, receiver, joinerPoses[0].at, set.Root);
                    if (stall != null) row.Fail("I5", $"{label}: the driver stalls on a target near an obstacle: {stall}");
                    else row.Cells["near"] = (row.Cells.TryGetValue("near", out string nn) ? int.Parse(nn) : 0) + 1 + "";
                }
            }
            RecordGap(row, host, joiner);
            List<string> late = TakeErrors(host, joiner);
            if (late.Count > 0) row.Fail("I0", string.Join("; ", late.Take(5)) + Identify(joiner, late));
            row.Pass("I5");
            row.Pass("I0");
        }
    }
}
#endif
