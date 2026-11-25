// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>What a minimap marker stands for.</summary>
    public enum MinimapMarkerKind
    {
        /// <summary>The room's owner, physically in that room (their headset): a filled circle.</summary>
        Person,
        /// <summary>Another participant's avatar as the core realized it in that room: a ring.</summary>
        Avatar,
    }

    /// <summary>One person on the minimap: where, which way, and how it is drawn. Positions are in panel units (0..size, y up).</summary>
    public struct MinimapMarker
    {
        public string ParticipantId;
        public string Label;
        public MinimapMarkerKind Kind;
        public Vector2 Position;
        /// <summary>The heading in the panel, a unit vector (the head's forward, else the root's).</summary>
        public Vector2 Heading;
        public Color Color;
    }

    /// <summary>A footprint (furniture, a wall, a placed object) as a quadrilateral in panel units.</summary>
    public struct MinimapFootprint
    {
        public Vector2 A, B, C, D;
        public MinimapFootprintKind Kind;
        public string Label;
    }

    public enum MinimapFootprintKind
    {
        Wall,
        Floor,
        Furniture,
        SharedFurniture,
        VirtualObject,
    }

    /// <summary>
    /// The pure part of the spectator's minimap: a top-down orthographic map of ONE participant's room, drawn from that participant's
    /// room snapshot (<see cref="ParticipantRoomSnapshot"/>, the same data the desktop observer receives). Unity world is Y-up; the map
    /// shows the X/Z plane with world +X to the right and world +Z up, a fixed room axis (the map never turns with the camera). The
    /// room's bounds (its furniture and walls, the people and the objects) are fitted into a square panel with a margin, keeping the
    /// aspect ratio. No allocation per <see cref="Build"/> once the lists have grown.
    /// </summary>
    public sealed class SpectatorMinimapLayout
    {
        /// <summary>The side of the square the layout is computed in; the graphic scales it to its rect.</summary>
        public const float ReferenceSide = 256f;
        /// <summary>The panel's margin, as a fraction of its side.</summary>
        public const float MarginFraction = 0.06f;
        /// <summary>The smallest room side the map shows, metres (a lone marker is not blown up to the whole panel).</summary>
        public const float MinimumSpanMeters = 3f;
        /// <summary>A virtual object's icon side, metres.</summary>
        public const float ObjectIconMeters = 0.35f;

        private readonly List<MinimapMarker> _markers = new List<MinimapMarker>();
        private readonly List<MinimapFootprint> _footprints = new List<MinimapFootprint>();
        private Vector2 _min, _max;
        private float _scale;
        private Vector2 _offset;

        public IReadOnlyList<MinimapMarker> Markers => _markers;
        public IReadOnlyList<MinimapFootprint> Footprints => _footprints;
        /// <summary>The panel side in panel units.</summary>
        public float Size { get; private set; }
        /// <summary>Panel units per metre.</summary>
        public float Scale => _scale;
        public bool HasRoom { get; private set; }

        /// <summary>
        /// The participants' colours: eight fixed, distinct colours that keep clear of the shared-real violet (about 230-300 degrees) and
        /// the accent (about 150-185 degrees) the map uses for shared furniture and placed objects: red, orange, yellow, green (110),
        /// sky (205), pink (330), white and a light brown.
        /// </summary>
        public static Color[] ParticipantPalette => UiTheme.Spectator.ParticipantPalette;

        /// <summary>The colour of a participant: the palette entry at its player id modulo the palette size, the same in both rooms (their circle in theirs, their ring in the other).</summary>
        public static Color ParticipantColor(string participantId)
        {
            int n = 0;
            if (!string.IsNullOrEmpty(participantId) && !int.TryParse(participantId, out n))
            {
                unchecked { foreach (char c in participantId) n = n * 31 + c; }
            }
            int count = ParticipantPalette.Length;
            return ParticipantPalette[((n % count) + count) % count];
        }

        private static readonly Dictionary<string, string> s_labels = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The short label of a participant: "P" and the player id (Photon names no person). Cached per id: no allocation per refresh.</summary>
        public static string ParticipantLabel(string participantId)
        {
            participantId = participantId ?? "?";
            if (!s_labels.TryGetValue(participantId, out string label))
            {
                if (s_labels.Count > 256) s_labels.Clear();
                label = "P" + participantId;
                s_labels[participantId] = label;
            }
            return label;
        }

        /// <summary>Orders rooms by their participant's player id as a number (P2 before P10), then by id text.</summary>
        public static int CompareRooms(ParticipantRoomSnapshot a, ParticipantRoomSnapshot b)
        {
            bool na = int.TryParse(a.ParticipantId, out int ia), nb = int.TryParse(b.ParticipantId, out int ib);
            if (na && nb) return ia.CompareTo(ib);
            if (na != nb) return na ? -1 : 1;
            return string.CompareOrdinal(a.ParticipantId, b.ParticipantId);
        }

        /// <summary>Unity world (Y-up) to the X/Z ground plane: world +X right, world +Z up.</summary>
        public static Vector2 Ground(Vector3 world) => new Vector2(world.x, world.z);

        /// <summary>A world direction's heading on the map (unit), or zero when it is vertical.</summary>
        public static Vector2 Heading(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            var flat = new Vector2(forward.x, forward.z);
            return flat.sqrMagnitude < 1e-8f ? Vector2.zero : flat.normalized;
        }

        /// <summary>A world point to panel units, after <see cref="Build"/> (or <see cref="Fit"/>).</summary>
        public Vector2 ToPanel(Vector3 world) => ToPanel(Ground(world));

        public Vector2 ToPanel(Vector2 ground) => (ground - _min) * _scale + _offset;

        /// <summary>
        /// Fits the ground rectangle [<paramref name="min"/>, <paramref name="max"/>] into a square panel of side <paramref name="size"/>:
        /// one scale for both axes (the aspect ratio is kept), centred, with <see cref="MarginFraction"/> on the longer side.
        /// </summary>
        public void Fit(Vector2 min, Vector2 max, float size)
        {
            Size = size;
            Vector2 span = max - min;
            if (span.x < MinimumSpanMeters) { float grow = (MinimumSpanMeters - span.x) * 0.5f; min.x -= grow; max.x += grow; }
            if (span.y < MinimumSpanMeters) { float grow = (MinimumSpanMeters - span.y) * 0.5f; min.y -= grow; max.y += grow; }
            span = max - min;
            float usable = size * (1f - 2f * MarginFraction);
            _scale = usable / Mathf.Max(span.x, span.y);
            _min = min;
            _max = max;
            // Centre the shorter axis.
            _offset = new Vector2((size - span.x * _scale) * 0.5f, (size - span.y * _scale) * 0.5f);
        }

        /// <summary>
        /// Lays out <paramref name="room"/> in a panel of side <paramref name="size"/>. <paramref name="isShared"/> says whether a piece of
        /// furniture is a shared real object (drawn in the shared-real violet); <paramref name="objectLabel"/> names a placed object's key
        /// (null skips the object). Returns false (and lays out nothing) without a room. A CEILING is left out (it would cover the room).
        /// </summary>
        public bool Build(ParticipantRoomSnapshot room, float size, Func<ObserverFurniture, bool> isShared = null, Func<string, string> objectLabel = null)
        {
            _markers.Clear();
            _footprints.Clear();
            HasRoom = room != null;
            if (room == null) { Fit(Vector2.zero, Vector2.zero, size); return false; }

            // Bounds: the furniture boxes (walls included), the people and the objects.
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            bool any = false;
            void Grow(Vector2 p) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); any = true; }
            for (int i = 0; i < room.Furniture.Count; ++i)
            {
                if (IsCeiling(room.Furniture[i])) continue;
                Footprint(room.Furniture[i], out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                Grow(a); Grow(b); Grow(c); Grow(d);
            }
            if (room.HasHead) Grow(Ground(room.HeadPosition));
            for (int i = 0; i < room.Avatars.Count; ++i) Grow(Ground(AvatarPoint(room.Avatars[i])));
            for (int i = 0; i < room.Objects.Count; ++i) Grow(Ground(room.Objects[i].Position));
            if (!any) { min = max = Vector2.zero; }
            Fit(min, max, size);

            // Floor first, then walls, furniture, objects (draw order).
            for (int pass = 0; pass < 3; ++pass)
            {
                for (int i = 0; i < room.Furniture.Count; ++i)
                {
                    ObserverFurniture f = room.Furniture[i];
                    if (IsCeiling(f)) continue;
                    MinimapFootprintKind kind = Classify(f, isShared);
                    int order = kind == MinimapFootprintKind.Floor ? 0 : kind == MinimapFootprintKind.Wall ? 1 : 2;
                    if (order != pass) continue;
                    Footprint(f, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                    _footprints.Add(new MinimapFootprint { A = ToPanel(a), B = ToPanel(b), C = ToPanel(c), D = ToPanel(d), Kind = kind, Label = null });
                }
            }
            if (objectLabel != null)
            {
                float h = ObjectIconMeters * 0.5f;
                for (int i = 0; i < room.Objects.Count; ++i)
                {
                    ObserverPlacedObject o = room.Objects[i];
                    string label = objectLabel(o.Key);
                    if (label == null) continue;
                    Vector2 c = Ground(o.Position);
                    Vector2 fwd = Heading(o.Rotation);
                    if (fwd == Vector2.zero) fwd = Vector2.up;
                    Vector2 right = new Vector2(fwd.y, -fwd.x);
                    _footprints.Add(new MinimapFootprint
                    {
                        A = ToPanel(c + (-right - fwd) * h), B = ToPanel(c + (right - fwd) * h),
                        C = ToPanel(c + (right + fwd) * h), D = ToPanel(c + (-right + fwd) * h),
                        Kind = MinimapFootprintKind.VirtualObject, Label = label,
                    });
                }
            }

            // People: the room's owner (their headset) is a filled circle; every other avatar realized in this room is a ring.
            if (room.HasHead)
            {
                _markers.Add(new MinimapMarker
                {
                    ParticipantId = room.ParticipantId, Label = ParticipantLabel(room.ParticipantId), Kind = MinimapMarkerKind.Person,
                    Position = ToPanel(room.HeadPosition), Heading = Heading(room.HeadRotation), Color = ParticipantColor(room.ParticipantId),
                });
            }
            for (int i = 0; i < room.Avatars.Count; ++i)
            {
                ObserverAvatarPose a = room.Avatars[i];
                // The owner's own avatar is the person already drawn (or, with no headset sample, drawn here as the person).
                bool owner = a.ParticipantId == room.ParticipantId;
                if (owner && room.HasHead) continue;
                Vector2 heading = a.HasHead ? Heading(a.HeadRotation) : Heading(a.RootRotation);
                _markers.Add(new MinimapMarker
                {
                    ParticipantId = a.ParticipantId, Label = ParticipantLabel(a.ParticipantId),
                    Kind = owner ? MinimapMarkerKind.Person : MinimapMarkerKind.Avatar,
                    Position = ToPanel(AvatarPoint(a)), Heading = heading, Color = ParticipantColor(a.ParticipantId),
                });
            }
            return true;
        }

        private static Vector3 AvatarPoint(ObserverAvatarPose a) => a.HasHead ? a.HeadPosition : a.RootPosition;

        public static bool IsCeiling(ObserverFurniture f) => (f.Label ?? string.Empty).IndexOf("CEILING", StringComparison.OrdinalIgnoreCase) >= 0;

        public static MinimapFootprintKind Classify(ObserverFurniture f, Func<ObserverFurniture, bool> isShared)
        {
            string label = f.Label ?? string.Empty;
            if (label.IndexOf("WALL_FACE", StringComparison.OrdinalIgnoreCase) >= 0) return MinimapFootprintKind.Wall;
            if (label.IndexOf("FLOOR", StringComparison.OrdinalIgnoreCase) >= 0) return MinimapFootprintKind.Floor;
            return isShared != null && isShared(f) ? MinimapFootprintKind.SharedFurniture : MinimapFootprintKind.Furniture;
        }

        /// <summary>
        /// One shared real object as the spectator received it (<see cref="SharedAnchorName"/>: its label, the sharer's key and measured
        /// size). A room's furniture is that shared object when it carries the key (the sharer's own anchor) or, for the room that linked
        /// its own anchor to it under the sharer's key, has the same label and the same measured size (each sorted side within
        /// <see cref="SharedSizeToleranceMeters"/>): the linking room shares its anchor under the key with its own measure.
        /// </summary>
        public const float SharedSizeToleranceMeters = 0.03f;

        public static bool MatchesShared(ObserverFurniture f, string sharedLabel, string sharedKey, Vector3 sharedSize)
        {
            if (!string.IsNullOrEmpty(sharedKey) && f.Id == sharedKey) return true;
            if (!string.Equals(f.Label, sharedLabel, StringComparison.OrdinalIgnoreCase)) return false;
            Sorted(f.Size, out float a0, out float a1, out float a2);
            Sorted(sharedSize, out float b0, out float b1, out float b2);
            return Mathf.Abs(a0 - b0) <= SharedSizeToleranceMeters && Mathf.Abs(a1 - b1) <= SharedSizeToleranceMeters &&
                   Mathf.Abs(a2 - b2) <= SharedSizeToleranceMeters;
        }

        private static void Sorted(Vector3 v, out float a, out float b, out float c)
        {
            a = Mathf.Abs(v.x); b = Mathf.Abs(v.y); c = Mathf.Abs(v.z);
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
        }

        /// <summary>
        /// A box's footprint on the ground: of its three local axes (scaled by its size), the one closest to vertical is dropped and the other
        /// two span the rectangle. A wall (a plane, no depth) is a thin line; a table, its top; a floor plane, its rectangle.
        /// </summary>
        public static void Footprint(ObserverFurniture f, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d)
        {
            Vector3 size = new Vector3(Mathf.Abs(f.Size.x), Mathf.Abs(f.Size.y), Mathf.Abs(f.Size.z));
            Vector3 ax = f.Rotation * Vector3.right, ay = f.Rotation * Vector3.up, az = f.Rotation * Vector3.forward;
            float vx = Mathf.Abs(ax.y), vy = Mathf.Abs(ay.y), vz = Mathf.Abs(az.y);
            Vector3 u, v;
            if (vx >= vy && vx >= vz) { u = ay * size.y; v = az * size.z; }
            else if (vy >= vx && vy >= vz) { u = ax * size.x; v = az * size.z; }
            else { u = ax * size.x; v = ay * size.y; }
            Vector2 center = Ground(f.Position);
            Vector2 hu = new Vector2(u.x, u.z) * 0.5f, hv = new Vector2(v.x, v.z) * 0.5f;
            a = center - hu - hv;
            b = center + hu - hv;
            c = center + hu + hv;
            d = center - hu + hv;
        }

        /// <summary>
        /// The 2-D rigid map from the spectator's own world to this room, found from one object both have (a placed object's pose in the
        /// room and the same object's pose in the spectator's scene): yaw and translation on the ground. False without a common object.
        /// </summary>
        public static bool TryAlign(Vector3 spectatorPosition, Quaternion spectatorRotation, Vector3 roomPosition, Quaternion roomRotation,
                                    out GroundAlignment alignment)
        {
            alignment = default;
            Vector2 hs = Heading(spectatorRotation), hr = Heading(roomRotation);
            if (hs == Vector2.zero || hr == Vector2.zero) return false;
            float yaw = Vector2.SignedAngle(hs, hr);
            alignment = new GroundAlignment(Ground(spectatorPosition), Ground(roomPosition), yaw);
            return true;
        }
    }

    /// <summary>A yaw and a translation on the ground plane, from the spectator's world to one room.</summary>
    public readonly struct GroundAlignment
    {
        public GroundAlignment(Vector2 fromPivot, Vector2 toPivot, float yawDegrees)
        {
            FromPivot = fromPivot;
            ToPivot = toPivot;
            YawDegrees = yawDegrees;
        }

        public Vector2 FromPivot { get; }
        public Vector2 ToPivot { get; }
        /// <summary>Counter-clockwise on the map (x right, z up).</summary>
        public float YawDegrees { get; }

        public Vector2 Point(Vector2 ground) => Rotate(ground - FromPivot) + ToPivot;

        public Vector2 Rotate(Vector2 v)
        {
            float r = YawDegrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }
    }
}
