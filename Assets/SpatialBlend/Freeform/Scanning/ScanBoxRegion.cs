// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>
    /// What an annotated box declares about the object it scopes, written in that object's local frame.
    /// v1 turned the box into an AffordanceVolume polygon. v2 reads it as spatial definition:
    /// the Containment Region is the box's footprint extent, the footprint is the box base, and the box
    /// top is an optional tagged surface. The object adds no margin of its own.
    ///
    /// The value is plain data. <c>BoxAnnotationAuthoring</c> writes it into an <c>SbSpatialAuthoring</c>
    /// declaration, and <see cref="Encode"/> carries it with the shared content so the receiver writes the
    /// same declaration. Only local-frame numbers cross the wire, never a world polygon.
    /// </summary>
    public sealed class ScanBoxRegion
    {
        /// <summary>Id of the tagged surface the box top declares.</summary>
        public const string TopSurfaceId = "BoxTop";
        /// <summary>Element tag of the box top. It is the tag the GLB content prefab gives its own top.</summary>
        public const string TopSurfaceTag = "Placeable";
        /// <summary>A box shallower than this declares no top surface.</summary>
        public const float MinTopHeightMetres = 0.02f;
        /// <summary>The smallest footprint edge a box may declare.</summary>
        public const float MinEdgeMetres = 1e-3f;
        public const int MaxFootprintVertices = 16;

        private const int WireMagic = 0x4252474E;
        private const int WireVersion = 1;
        private const float MinFootprintArea = 1e-6f;

        private readonly Vector2[] _footprint;

        private ScanBoxRegion(Vector2[] footprint, float baseY, float topY)
        {
            _footprint = footprint;
            BaseY = baseY;
            TopY = topY;
        }

        /// <summary>Box base height in the object's local frame.</summary>
        public float BaseY { get; }

        /// <summary>Box top height in the object's local frame.</summary>
        public float TopY { get; }

        /// <summary>True when the box is tall enough to declare its top as a surface.</summary>
        public bool HasTopSurface => TopY - BaseY >= MinTopHeightMetres;

        /// <summary>The box base in local XZ, counter-clockwise.</summary>
        public IReadOnlyList<Vector2> Footprint => _footprint;

        public Vector2[] FootprintPolygon() => (Vector2[])_footprint.Clone();

        /// <summary>The Containment Region: the extent of the box footprint. It equals the footprint.</summary>
        public Vector2[] ExtentPolygon() => (Vector2[])_footprint.Clone();

        /// <summary>The box top: the footprint at <see cref="TopY"/>, wound so its normal points up.</summary>
        public Vector3[] TopSurface()
        {
            var surface = new Vector3[_footprint.Length];
            for (int index = 0; index < surface.Length; ++index)
            {
                surface[index] = new Vector3(_footprint[index].x, TopY, _footprint[index].y);
            }
            return surface;
        }

        /// <summary>
        /// A box given in the object's own frame: <paramref name="center"/> and <paramref name="size"/>
        /// are local, and its edges follow the local axes.
        /// </summary>
        public static bool TryFromLocalBox(Vector3 center, Vector3 size, out ScanBoxRegion region)
        {
            region = null;
            if (!IsFinite(center) || !IsFinite(size)) return false;
            float halfX = Mathf.Abs(size.x) * 0.5f;
            float halfY = Mathf.Abs(size.y) * 0.5f;
            float halfZ = Mathf.Abs(size.z) * 0.5f;
            if (halfX * 2f < MinEdgeMetres || halfZ * 2f < MinEdgeMetres) return false;
            region = new ScanBoxRegion(
                new[]
                {
                    new Vector2(center.x - halfX, center.z - halfZ),
                    new Vector2(center.x + halfX, center.z - halfZ),
                    new Vector2(center.x + halfX, center.z + halfZ),
                    new Vector2(center.x - halfX, center.z + halfZ),
                },
                center.y - halfY,
                center.y + halfY);
            return true;
        }

        /// <summary>
        /// A box the user drew in the world, as a scan anchor carries it: a world centre and a world
        /// size, axis aligned (v1 read the same two values). <paramref name="worldToObject"/> takes it into
        /// the local frame of the object that is bound to it, so the declaration does not depend on the
        /// pose of any one copy of that object.
        /// </summary>
        public static bool TryFromWorldBox(Vector3 center, Vector3 size, Matrix4x4 worldToObject,
                                           out ScanBoxRegion region) =>
            TryFromWorldBox(center, size, Quaternion.identity, worldToObject, out region);

        /// <summary>
        /// The same for a box turned by <paramref name="rotation"/> about the vertical (its size is along its turned axes): the
        /// footprint is the turned box exactly, not the bounding box of it (the scan is where it was drawn).
        /// </summary>
        public static bool TryFromWorldBox(Vector3 center, Vector3 size, Quaternion rotation, Matrix4x4 worldToObject,
                                           out ScanBoxRegion region)
        {
            region = null;
            if (!IsFinite(center) || !IsFinite(size) || !IsFinite(worldToObject)) return false;
            float halfX = Mathf.Abs(size.x) * 0.5f;
            float halfY = Mathf.Abs(size.y) * 0.5f;
            float halfZ = Mathf.Abs(size.z) * 0.5f;
            if (halfX * 2f < MinEdgeMetres || halfZ * 2f < MinEdgeMetres) return false;

            float baseWorldY = center.y - halfY;
            float topWorldY = center.y + halfY;
            var basePoint = new Vector3(center.x, baseWorldY, center.z);
            var corners = new[]
            {
                basePoint + rotation * new Vector3(-halfX, 0f, -halfZ),
                basePoint + rotation * new Vector3(+halfX, 0f, -halfZ),
                basePoint + rotation * new Vector3(+halfX, 0f, +halfZ),
                basePoint + rotation * new Vector3(-halfX, 0f, +halfZ),
            };
            var polygon = new Vector2[4];
            float baseY = 0f;
            for (int index = 0; index < corners.Length; ++index)
            {
                Vector3 local = worldToObject.MultiplyPoint3x4(corners[index]);
                polygon[index] = new Vector2(local.x, local.z);
                baseY += local.y;
            }
            baseY *= 0.25f;
            float topY = worldToObject.MultiplyPoint3x4(new Vector3(center.x, topWorldY, center.z)).y;

            // A mirrored object frame reverses the winding. Keep it counter-clockwise so the top faces up.
            float area = SignedArea(polygon);
            if (Mathf.Abs(area) < MinFootprintArea) return false;
            if (area < 0f) Array.Reverse(polygon);
            if (!AllFinite(polygon) || float.IsNaN(topY) || float.IsInfinity(topY)) return false;
            region = new ScanBoxRegion(polygon, baseY, Mathf.Max(topY, baseY));
            return true;
        }

        /// <summary>The wire form: magic, version, vertex count, the vertices, then base and top height.</summary>
        public byte[] Encode()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(WireMagic);
                writer.Write(WireVersion);
                writer.Write(_footprint.Length);
                foreach (Vector2 point in _footprint)
                {
                    writer.Write(point.x);
                    writer.Write(point.y);
                }
                writer.Write(BaseY);
                writer.Write(TopY);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Reads <see cref="Encode"/> output. Anything else, a short buffer, an unknown version, a non-finite
        /// number or a footprint with no area, is refused, so a bad payload never writes a declaration.
        /// </summary>
        public static bool TryDecode(byte[] data, out ScanBoxRegion region)
        {
            region = null;
            if (data == null || data.Length < 3 * sizeof(int) + 2 * sizeof(float)) return false;
            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != WireMagic) return false;
                    if (reader.ReadInt32() != WireVersion) return false;
                    int count = reader.ReadInt32();
                    if (count < 3 || count > MaxFootprintVertices) return false;
                    long expected = 3L * sizeof(int) + (2L * count + 2L) * sizeof(float);
                    if (data.Length != expected) return false;
                    var polygon = new Vector2[count];
                    for (int index = 0; index < count; ++index)
                    {
                        polygon[index] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    }
                    float baseY = reader.ReadSingle();
                    float topY = reader.ReadSingle();
                    if (!AllFinite(polygon) || float.IsNaN(baseY) || float.IsInfinity(baseY) ||
                        float.IsNaN(topY) || float.IsInfinity(topY) || topY < baseY)
                    {
                        return false;
                    }
                    if (SignedArea(polygon) < MinFootprintArea) return false;
                    region = new ScanBoxRegion(polygon, baseY, topY);
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        private static float SignedArea(Vector2[] polygon)
        {
            float twice = 0f;
            for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
            {
                twice += polygon[previous].x * polygon[index].y - polygon[index].x * polygon[previous].y;
            }
            return twice * 0.5f;
        }

        private static bool AllFinite(Vector2[] polygon)
        {
            foreach (Vector2 point in polygon)
            {
                if (float.IsNaN(point.x) || float.IsInfinity(point.x) ||
                    float.IsNaN(point.y) || float.IsInfinity(point.y))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            !(float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) ||
              float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z));

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; ++index)
            {
                if (float.IsNaN(matrix[index]) || float.IsInfinity(matrix[index])) return false;
            }
            return true;
        }
    }
}
