// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The viewed participant's room in the spectator's 3-D view, drawn from that participant's room snapshot (the minimap's data):
    /// a dark floor, the walls as low light strips (the room outline), the furniture as boxes (shared real objects in the shared-real
    /// violet), and every person as a proxy: a capsule body, a head with a nose for its heading, a floor disc (the room's owner, the
    /// person physically there) or a ring of beads (another participant's avatar as realized in that room), in the participant's
    /// minimap colour and with its P-label. It is aligned on the spectator's scene the same way as the minimap's camera wedge (a placed
    /// Fusion object both have), else drawn at the room's own coordinates. The Mac has no Meta Avatars binary, so proxies stand in.
    ///
    /// The snapshot carries each person's root and head pose (no hands) at 2 Hz. Proxies ease toward each new sample, so a walk reads
    /// as a walk; a sample more than <see cref="JumpMeters"/> away is shown as the jump it is (snapped, and logged), never smoothed over.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class SpectatorRoomView : MonoBehaviour
    {
        public const float JumpMeters = 1.0f;
        public const float EaseRate = 8f;
        public const float WallStripHeight = 0.25f;

        private FreeformSpectatorSession _session;
        private SpectatorCamera _camera;
        private Transform _root;
        private readonly List<GameObject> _geometry = new List<GameObject>();
        private readonly Dictionary<string, Proxy> _proxies = new Dictionary<string, Proxy>();
        private readonly List<string> _stale = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        private int _furnitureHash;
        private string _builtRoom;
        private readonly List<Material> _materials = new List<Material>();
        private readonly Dictionary<Color, Material> _colourMaterials = new Dictionary<Color, Material>();
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private readonly HashSet<Transform> _posed = new HashSet<Transform>();
        private readonly List<Renderer> _rendererScratch = new List<Renderer>();

        /// <summary>Test seam: the spectator's own object for a snapshot key; null uses the minimap's scene cache.</summary>
        internal System.Func<string, Transform> LocalObjectResolver;

        /// <summary>How often the room geometry was built (only when the room or its furniture content changes).</summary>
        public int GeometryBuilds { get; private set; }
        /// <summary>How often a posed object's transform was written (only when its room pose changed).</summary>
        public int PoseWrites { get; private set; }
        /// <summary>The materials this view owns now (released on <see cref="Clear"/> and on destroy).</summary>
        public int LiveMaterialCount => _materials.Count;
        private int _builtPoses = -1;
        private Bounds _bounds;
        private bool _hasBounds, _framed;
        private Material _floor, _wall, _furniture, _shared;
        private Mesh _cube;

        public int JumpCount { get; private set; }
        public int ProxyCount => _proxies.Count;
        public bool TryGetProxy(string participantId, out MinimapMarkerKind kind, out Vector3 head)
        {
            kind = default; head = default;
            if (!_proxies.TryGetValue(participantId, out Proxy p)) return false;
            kind = p.Kind; head = p.HeadTarget;
            return true;
        }
        public int GeometryCount => _geometry.Count;

        private sealed class Proxy
        {
            public GameObject Root;
            public Transform Torso, Head, Nose, Floor, LegL, LegR, HandL, HandR;
            public TMP_Text Label;
            public Material Material;
            public Vector3 Head0, HeadTarget;
            public Quaternion Yaw0, YawTarget;
            public bool Placed;
            public MinimapMarkerKind Kind;
        }

        public static SpectatorRoomView Create(FreeformSpectatorSession session, SpectatorCamera camera)
        {
            var go = new GameObject("SpectatorRoomView");
            var view = go.AddComponent<SpectatorRoomView>();
            view._session = session;
            view._camera = camera;
            view._root = go.transform;
            return view;
        }

        /// <summary>The bounds of the drawn room, for framing (F, and once when it first appears).</summary>
        public bool TryGetBounds(out Bounds bounds)
        {
            bounds = _bounds;
            return _hasBounds;
        }

        private Material NewMaterial(Color color)
        {
            if (_cube == null)
            {
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = probe.GetComponent<MeshFilter>().sharedMesh;
                _baseMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                DestroySafe(probe);
            }
            var material = new Material(_baseMaterial) { color = color };
            _materials.Add(material);
            return material;
        }

        /// <summary>One material per participant colour (the palette has eight), shared by every proxy of that colour.</summary>
        private Material ColourMaterial(Color color)
        {
            if (!_colourMaterials.TryGetValue(color, out Material m) || m == null) { m = NewMaterial(color); _colourMaterials[color] = m; }
            return m;
        }

        private static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        private Material _baseMaterial;

        private void EnsureMaterials()
        {
            if (_floor != null) return;
            var p = UiTheme.Palette;
            _floor = NewMaterial(UiTheme.Spectator.RoomFloor);
            _wall = NewMaterial(UiTheme.Spectator.RoomWall);
            _furniture = NewMaterial(UiTheme.Spectator.RoomFurniture);
            _shared = NewMaterial(p.SharedReal);
        }

        private void LateUpdate() => Step(Time.unscaledDeltaTime);

        /// <summary>One frame of the view (LateUpdate; tests call it directly).</summary>
        internal void Step(float dt)
        {
            if (_session == null) return;
            var room = _session.Phase == SpectatorPhase.Spectating ? _session.Rooms.Rooms.Selected : null;
            if (room == null) { if (_builtRoom != null || _materials.Count > 0) Clear(); return; }
            EnsureMaterials();
            bool roomChanged = _builtRoom != room.ParticipantId;
            var minimap = _session.Minimap;
            var isShared = minimap != null ? minimap.IsSharedFurniture : null;
            // The participant resends its furniture every 2 s as a new list: rebuild only when its content changed.
            int hash = FurnitureHash(room.Furniture, isShared);
            if (roomChanged || hash != _furnitureHash) BuildGeometry(room, hash, isShared);
            if (roomChanged)
            {
                // Another room is another frame: every proxy moves at once, which is not a jump of the person.
                foreach (var proxy in _proxies.Values) proxy.Placed = false;
                _builtPoses = -1;
                _framed = false;
            }
            bool newSample = _builtPoses != _session.Rooms.Rooms.PoseRevision;
            PoseObjects(room, newSample);
            if (newSample) { _builtPoses = _session.Rooms.Rooms.PoseRevision; UpdateTargets(room); }
            Ease(dt);
            if (!_framed && _hasBounds && _camera != null) { _framed = true; _camera.Frame(_bounds); }
        }

        /// <summary>A content hash of a room's furniture (ids, labels, poses and sizes to the millimetre, and which are shared).</summary>
        internal static int FurnitureHash(IReadOnlyList<ObserverFurniture> furniture, System.Func<ObserverFurniture, bool> isShared)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < furniture.Count; ++i)
                {
                    ObserverFurniture f = furniture[i];
                    h = h * 31 + (f.Id?.GetHashCode() ?? 0);
                    h = h * 31 + (f.Label?.GetHashCode() ?? 0);
                    h = h * 31 + Mm(f.Position.x); h = h * 31 + Mm(f.Position.y); h = h * 31 + Mm(f.Position.z);
                    h = h * 31 + Mm(f.Size.x); h = h * 31 + Mm(f.Size.y); h = h * 31 + Mm(f.Size.z);
                    h = h * 31 + Mm(f.Rotation.x * 1000f); h = h * 31 + Mm(f.Rotation.y * 1000f);
                    h = h * 31 + Mm(f.Rotation.z * 1000f); h = h * 31 + Mm(f.Rotation.w * 1000f);
                    h = h * 31 + (isShared != null && isShared(f) ? 1 : 0);
                }
                return h * 31 + furniture.Count;
            }
        }

        private static int Mm(float metres) => Mathf.RoundToInt(metres * 1000f);

        // The spectator's world is the viewed room's frame (the objects are posed into it, PoseObjects): a room point is a world point.
        private static Vector3 ToWorld(Vector3 roomPoint) => roomPoint;
        private static Quaternion ToWorld(Quaternion roomRotation) => roomRotation;

        /// <summary>
        /// Puts the spectator's own copies of the placed objects (Chess, the Whiteboard, received native objects) at their poses in the
        /// viewed room, every frame after the core's state write (this component runs late). Without it they would stand at the spectator's own
        /// space pose: the Whiteboard at its scene default on the floor, half under it, because the
        /// spectator has no room of its own in which the core could place it on a wall. The same as the desktop observer does.
        /// </summary>
        private void PoseObjects(ParticipantRoomSnapshot room, bool newSample)
        {
            _posed.Clear();
            for (int i = 0; i < room.Objects.Count; ++i)
            {
                ObserverPlacedObject o = room.Objects[i];
                if (!TryResolve(o.Key, out Transform local)) continue;
                if (_camera != null && local == _camera.transform) continue;
                _posed.Add(local);
                // Written only when the room pose differs from where it stands: the manager pushes a received copy's placement to the
                // core on every move, so an unconditional write each frame would refit its region 60 times a second.
                if ((local.position - o.Position).sqrMagnitude < PoseEpsilonMeters * PoseEpsilonMeters &&
                    Quaternion.Angle(local.rotation, o.Rotation) < PoseEpsilonDegrees) continue;
                local.SetPositionAndRotation(o.Position, o.Rotation);
                PoseWrites++;
            }
            if (newSample) HideUnposedContent();
        }

        public const float PoseEpsilonMeters = 0.001f, PoseEpsilonDegrees = 0.1f;

        private bool TryResolve(string key, out Transform local)
        {
            local = null;
            if (LocalObjectResolver != null) { local = LocalObjectResolver(key); return local != null; }
            var minimap = _session.Minimap;
            return minimap != null && minimap.TryFindLocalObject(key, out local);
        }

        /// <summary>
        /// Received content the viewed room's snapshot does not place (an object the room does not list) has only its pose in the
        /// spectator's own space, which means nothing in this room: its renderers are hidden while the room is drawn, and shown again
        /// once the room places it or the view clears.
        /// </summary>
        private void HideUnposedContent()
        {
            var app = _session.App;
            if (app == null) return;
            foreach (var r in _hidden) if (r != null) r.enabled = true;
            _hidden.Clear();
            foreach (var go in app.ReceivedObjects)
            {
                if (go == null || FreeformSpectatorApp.IsSharedSceneAnchor(go) || IsPosed(go.transform)) continue;
                go.GetComponentsInChildren(false, _rendererScratch);
                foreach (var r in _rendererScratch) if (r.enabled) { r.enabled = false; _hidden.Add(r); }
            }
        }

        private bool IsPosed(Transform t)
        {
            foreach (var posed in _posed) if (posed != null && (t == posed || t.IsChildOf(posed) || posed.IsChildOf(t))) return true;
            return false;
        }

        /// <summary>Renderers hidden because the viewed room does not place their object.</summary>
        public int HiddenRendererCount => _hidden.Count;

        /// <summary>Removes everything the view made (geometry, proxies, materials) and shows hidden content again.</summary>
        internal void Clear()
        {
            foreach (var g in _geometry) DestroySafe(g);
            _geometry.Clear();
            foreach (var proxy in _proxies.Values) DestroySafe(proxy.Root);
            _proxies.Clear();
            foreach (var r in _hidden) if (r != null) r.enabled = true;
            _hidden.Clear();
            foreach (var m in _materials) DestroySafe(m);
            _materials.Clear();
            _colourMaterials.Clear();
            _floor = _wall = _furniture = _shared = null;
            _builtRoom = null;
            _furnitureHash = 0;
            _builtPoses = -1;
            _hasBounds = false;
        }

        private void BuildGeometry(ParticipantRoomSnapshot room, int hash, System.Func<ObserverFurniture, bool> isShared)
        {
            foreach (var g in _geometry) DestroySafe(g);
            _geometry.Clear();
            GeometryBuilds++;
            _builtRoom = room.ParticipantId;
            _furnitureHash = hash;
            _hasBounds = false;
            for (int i = 0; i < room.Furniture.Count; ++i)
            {
                ObserverFurniture f = room.Furniture[i];
                if (SpectatorMinimapLayout.IsCeiling(f)) continue;
                var kind = SpectatorMinimapLayout.Classify(f, isShared);
                SpectatorMinimapLayout.Footprint(f, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                Vector2 u = b - a, v = d - a;
                float width = u.magnitude, depth = v.magnitude;
                Vector2 center2 = (a + c) * 0.5f;
                float yaw = width > 1e-4f ? -Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg : 0f;
                float height, bottom;
                Material material;
                switch (kind)
                {
                    // At the room's own floor height (its top 5 mm above it, so it shows over the spectator's reference floor at y = 0).
                    case MinimapFootprintKind.Floor: height = 0.01f; bottom = f.Position.y - 0.005f; material = _floor; break;
                    case MinimapFootprintKind.Wall: height = WallStripHeight; bottom = 0f; depth = Mathf.Max(depth, 0.04f); material = _wall; break;
                    case MinimapFootprintKind.SharedFurniture: height = VerticalSize(f); bottom = f.Position.y - height * 0.5f; material = _shared; break;
                    default: height = VerticalSize(f); bottom = f.Position.y - height * 0.5f; material = _furniture; break;
                }
                if (width < 0.02f || height < 0.005f) continue;
                Vector3 roomCenter = new Vector3(center2.x, bottom + height * 0.5f, center2.y);
                if (kind == MinimapFootprintKind.SharedFurniture)
                {
                    // A shared real object: the app's own SharedAnchorBox look (edges, corner brackets, faint violet fill) at its true
                    // pose and size in this room, as the participants see it.
                    var frame = new GameObject("SharedReal:" + f.Label);
                    frame.transform.SetParent(_root, false);
                    frame.transform.SetPositionAndRotation(ToWorld(roomCenter), ToWorld(Quaternion.Euler(0f, yaw, 0f)));
                    SharedAnchorBox.Build(frame.transform, Vector3.zero, new Vector3(width, height, Mathf.Max(depth, 0.02f)), "SharedAnchorBox");
                    _geometry.Add(frame);
                    Encapsulate(new Bounds(ToWorld(roomCenter), new Vector3(Mathf.Max(width, depth), height, Mathf.Max(width, depth))));
                    continue;
                }
                var box = NewBox("RoomBox:" + f.Label, material);
                box.transform.SetPositionAndRotation(ToWorld(roomCenter), ToWorld(Quaternion.Euler(0f, yaw, 0f)));
                box.transform.localScale = new Vector3(width, height, Mathf.Max(depth, 0.02f));
                _geometry.Add(box);
                Encapsulate(box.GetComponent<Renderer>().bounds);
            }
        }

        private static float VerticalSize(ObserverFurniture f)
        {
            Vector3 s = new Vector3(Mathf.Abs(f.Size.x), Mathf.Abs(f.Size.y), Mathf.Abs(f.Size.z));
            Vector3 ax = f.Rotation * Vector3.right, ay = f.Rotation * Vector3.up, az = f.Rotation * Vector3.forward;
            float vx = Mathf.Abs(ax.y), vy = Mathf.Abs(ay.y), vz = Mathf.Abs(az.y);
            if (vx >= vy && vx >= vz) return s.x;
            if (vy >= vx && vy >= vz) return s.y;
            return s.z;
        }

        private void Encapsulate(Bounds b)
        {
            if (!_hasBounds) { _bounds = b; _hasBounds = true; }
            else _bounds.Encapsulate(b);
        }

        private GameObject NewBox(string name, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(_root, false);
            go.GetComponent<MeshFilter>().sharedMesh = _cube;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private void UpdateTargets(ParticipantRoomSnapshot room)
        {
            _seen.Clear();
            if (room.HasHead) Target(room.ParticipantId, MinimapMarkerKind.Person, room.HeadPosition, room.HeadRotation);
            for (int i = 0; i < room.Avatars.Count; ++i)
            {
                ObserverAvatarPose a = room.Avatars[i];
                bool owner = a.ParticipantId == room.ParticipantId;
                if (owner && room.HasHead) continue;
                Vector3 head = a.HasHead ? a.HeadPosition : a.RootPosition + Vector3.up * 1.6f;
                Target(a.ParticipantId, owner ? MinimapMarkerKind.Person : MinimapMarkerKind.Avatar, head, a.HasHead ? a.HeadRotation : a.RootRotation);
            }
            _stale.Clear();
            foreach (var id in _proxies.Keys) if (!_seen.Contains(id)) _stale.Add(id);
            foreach (var id in _stale) { DestroySafe(_proxies[id].Root); _proxies.Remove(id); }
        }

        private void Target(string id, MinimapMarkerKind kind, Vector3 roomHead, Quaternion roomHeadRotation)
        {
            _seen.Add(id);
            if (!_proxies.TryGetValue(id, out Proxy proxy) || proxy.Kind != kind)
            {
                if (proxy != null) DestroySafe(proxy.Root);
                proxy = NewProxy(id, kind);
                _proxies[id] = proxy;
            }
            Vector3 head = ToWorld(roomHead);
            Vector2 heading = SpectatorMinimapLayout.Heading(ToWorld(roomHeadRotation));
            Quaternion yaw = heading == Vector2.zero ? proxy.YawTarget : Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up);
            if (!proxy.Placed) { proxy.Head0 = head; proxy.Yaw0 = yaw; proxy.Placed = true; proxy.HeadTarget = head; }
            else if (Vector3.Distance(new Vector3(proxy.HeadTarget.x, 0f, proxy.HeadTarget.z), new Vector3(head.x, 0f, head.z)) > JumpMeters)
            {
                // Shown as the jump it is: not eased over.
                JumpCount++;
                Debug.LogWarning($"[SpectatorRoomView] {SpectatorMinimapLayout.ParticipantLabel(id)} jumped {Vector3.Distance(proxy.HeadTarget, head):0.00} m between two room samples");
                proxy.Head0 = head;
                proxy.Yaw0 = yaw;
            }
            proxy.HeadTarget = head;
            proxy.YawTarget = yaw;
        }

        private void Ease(float dt)
        {
            float k = 1f - Mathf.Exp(-EaseRate * dt);
            var cam = _camera != null ? _camera.transform : null;
            foreach (var proxy in _proxies.Values)
            {
                proxy.Head0 = Vector3.Lerp(proxy.Head0, proxy.HeadTarget, k);
                proxy.Yaw0 = Quaternion.Slerp(proxy.Yaw0, proxy.YawTarget, k);
                Pose(proxy, cam);
            }
        }

        /// <summary>
        /// A stylised humanoid under the head sample: torso from the neck to the hips (about half the head height), two legs to the floor,
        /// two hands at rest beside the hips (the snapshot carries no hands: they are drawn, not tracked), the head and its nose.
        /// A head sample below 1 m (a desktop participant has no headset) is drawn at a standing 1.6 m.
        /// </summary>
        private static void Pose(Proxy p, Transform cam)
        {
            Vector3 head = p.Head0;
            float h = head.y >= 1.0f ? head.y : 1.6f;
            float neck = h - 0.16f, hips = h * 0.52f;
            p.Root.transform.SetPositionAndRotation(new Vector3(head.x, 0f, head.z), p.Yaw0);
            p.Torso.localPosition = new Vector3(0f, (neck + hips) * 0.5f, 0f);
            p.Torso.localScale = new Vector3(0.34f, (neck - hips) * 0.5f + 0.08f, 0.22f);
            float leg = hips * 0.5f;
            p.LegL.localPosition = new Vector3(-0.1f, leg, 0f);
            p.LegR.localPosition = new Vector3(0.1f, leg, 0f);
            p.LegL.localScale = p.LegR.localScale = new Vector3(0.12f, leg, 0.12f);
            p.HandL.localPosition = new Vector3(-0.27f, hips + 0.02f, 0.06f);
            p.HandR.localPosition = new Vector3(0.27f, hips + 0.02f, 0.06f);
            p.Head.localPosition = new Vector3(0f, h, 0f);
            p.Nose.localPosition = new Vector3(0f, h - 0.02f, 0.14f);
            head = new Vector3(head.x, h, head.z);
            if (p.Label != null)
            {
                p.Label.transform.position = new Vector3(head.x, head.y + 0.32f, head.z);
                if (cam != null) p.Label.transform.rotation = Quaternion.LookRotation(p.Label.transform.position - cam.position, Vector3.up);
            }
        }

        private Proxy NewProxy(string id, MinimapMarkerKind kind)
        {
            Color color = SpectatorMinimapLayout.ParticipantColor(id);
            var proxy = new Proxy { Kind = kind, Material = ColourMaterial(color) };
            var root = new GameObject("Proxy:" + SpectatorMinimapLayout.ParticipantLabel(id));
            root.transform.SetParent(_root, false);
            proxy.Root = root;
            proxy.Torso = Primitive(PrimitiveType.Capsule, root.transform, proxy.Material).transform;
            proxy.LegL = Primitive(PrimitiveType.Capsule, root.transform, proxy.Material).transform;
            proxy.LegR = Primitive(PrimitiveType.Capsule, root.transform, proxy.Material).transform;
            proxy.HandL = Primitive(PrimitiveType.Sphere, root.transform, proxy.Material).transform;
            proxy.HandR = Primitive(PrimitiveType.Sphere, root.transform, proxy.Material).transform;
            proxy.HandL.localScale = proxy.HandR.localScale = Vector3.one * 0.1f;
            proxy.Head = Primitive(PrimitiveType.Sphere, root.transform, proxy.Material).transform;
            proxy.Head.localScale = Vector3.one * 0.24f;
            proxy.Nose = Primitive(PrimitiveType.Cube, root.transform, _wall).transform;
            proxy.Nose.localScale = new Vector3(0.06f, 0.06f, 0.12f);
            if (kind == MinimapMarkerKind.Person)
            {
                // A filled floor disc: the person who is physically in this room.
                proxy.Floor = Primitive(PrimitiveType.Cylinder, root.transform, proxy.Material).transform;
                proxy.Floor.localScale = new Vector3(0.6f, 0.004f, 0.6f);
                proxy.Floor.localPosition = new Vector3(0f, 0.012f, 0f);
            }
            else
            {
                // A ring of beads: another participant's avatar as realized in this room.
                for (int i = 0; i < 12; ++i)
                {
                    float a = i * Mathf.PI * 2f / 12f;
                    var bead = Primitive(PrimitiveType.Sphere, root.transform, proxy.Material).transform;
                    bead.localScale = Vector3.one * 0.09f;
                    bead.localPosition = new Vector3(Mathf.Cos(a) * 0.32f, 0.05f, Mathf.Sin(a) * 0.32f);
                }
            }
            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(root.transform, false);
            var label = labelGo.AddComponent<TextMeshPro>();
            UiFonts.Apply(label, true);
            label.text = SpectatorMinimapLayout.ParticipantLabel(id) + (kind == MinimapMarkerKind.Person ? " (here)" : " (avatar)");
            label.fontSize = UiTheme.Spectator.WorldLabelEmMetres;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.rectTransform.sizeDelta = new Vector2(2f, 0.4f);
            proxy.Label = label;
            return proxy;
        }

        private static GameObject Primitive(PrimitiveType type, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            var collider = go.GetComponent<Collider>();
            DestroySafe(collider);
            go.transform.SetParent(parent, false);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private void OnDestroy() => Clear();
    }
}
