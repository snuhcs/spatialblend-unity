// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        // Mirrors the ABI2 owned-scene preflight. A void native setter cannot
        // report rejection, so reject oversized managed input before marshalling.
        internal const int MaxSceneVertices = 65536;
        private static class SceneInputsRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_remove_local_element(long space, long element);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_surface(long space, long element,
                double[] vertices, int count, SbGeometryRole role);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_anchor_transform(long space, long actor,
                string name, double x, double y, double z, double yaw);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_clear_anchor_transform(long space, long actor, string name);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_sub_region(long space, long element,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string tag,
                double[] pointsXy, int count, double[] transform16);
        }

        private static class SceneInputsDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_remove_local_element(long space, long element);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_surface(long space, long element,
                double[] vertices, int count, SbGeometryRole role);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_anchor_transform(long space, long actor,
                string name, double x, double y, double z, double yaw);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_clear_anchor_transform(long space, long actor, string name);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_sub_region(long space, long element,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string tag,
                double[] pointsXy, int count, double[] transform16);
        }

        // Deliberately does not catch EntryPointNotFoundException: an old plugin
        // cannot claim successful retirement while leaving geometry behind.
        public static void AddSubRegion(long space, long element, string name, string tag,
                                        IReadOnlyList<Vector2> polygon) =>
            AddSubRegion(space, element, name, tag, polygon, 0f);

        /// <summary>
        /// Adds a patch whose own frame is the definition's turned by <paramref name="rotationDegrees"/> about the plane normal
        /// (counter-clockwise in the polygon plane), through the row-major <c>transform16</c> of <c>sb_add_sub_region</c>. The
        /// polygon is in the patch's own frame. Zero passes no transform (the identity), exactly as before.
        /// </summary>
        public static void AddSubRegion(long space, long element, string name, string tag,
                                        IReadOnlyList<Vector2> polygon, float rotationDegrees)
        {
            if (string.IsNullOrEmpty(name) || polygon == null || polygon.Count < 3) return;
            if (polygon.Count > MaxSceneVertices)
                throw new ArgumentOutOfRangeException(nameof(polygon),
                    "A sub-region cannot exceed the native limit of 65536 vertices.");
            var points = new double[polygon.Count * 2];
            for (int index = 0; index < polygon.Count; ++index)
            {
                Vector2 point = polygon[index];
                if (!IsFiniteSceneFloat(point.x) || !IsFiniteSceneFloat(point.y))
                    throw new ArgumentException("Sub-region vertices must be finite.", nameof(polygon));
                points[index * 2] = point.x;
                points[index * 2 + 1] = point.y;
            }
            if (float.IsNaN(rotationDegrees) || float.IsInfinity(rotationDegrees))
                throw new ArgumentException("The sub-region rotation must be finite.", nameof(rotationDegrees));
            double[] transform = null;
            if (rotationDegrees != 0f)
            {
                double radians = rotationDegrees * Math.PI / 180.0;
                double c = Math.Cos(radians), s = Math.Sin(radians);
                transform = new[] { c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            }
            if (useDebugLibrary)
                SceneInputsDebug.sb_add_sub_region(space, element, name, tag ?? string.Empty, points, polygon.Count, transform);
            else
                SceneInputsRelease.sb_add_sub_region(space, element, name, tag ?? string.Empty, points, polygon.Count, transform);
        }

        public static bool RemoveLocalElement(long space, long element) =>
            (useDebugLibrary ? SceneInputsDebug.sb_remove_local_element(space, element)
                : SceneInputsRelease.sb_remove_local_element(space, element)) != 0;

        /// <summary>
        /// Sets a baked-world surface. The native SurfaceSpatialDefinition stores
        /// these vertices as world coordinates and deliberately does not compose
        /// the element transform again (spatialblend.h, ABI 3.1: the one world-baked geometry setter besides a
        /// NAVIGABLE polygon). Compose the vertices into the world and call again when the element or an ancestor moves.
        /// </summary>
        public static void SetElementWorldSurface(long space, long element,
            IReadOnlyList<Vector3> worldVertices, SbGeometryRole role)
        {
            if (worldVertices == null || worldVertices.Count < 3)
                throw new ArgumentException("A surface requires at least three world vertices.", nameof(worldVertices));
            if (worldVertices.Count > MaxSceneVertices)
                throw new ArgumentOutOfRangeException(nameof(worldVertices),
                    "A surface cannot exceed the native limit of 65536 vertices.");
            var points = new double[checked(worldVertices.Count * 3)];
            for (int i = 0; i < worldVertices.Count; ++i)
            {
                Vector3 point = worldVertices[i];
                if (!IsFiniteScenePoint(point))
                    throw new ArgumentException("Surface vertices must be finite.", nameof(worldVertices));
                CoordinateConverter.UnityToSpatialBlend3D(point,
                    out points[i * 3], out points[i * 3 + 1], out points[i * 3 + 2]);
            }
            NoteSceneStatus(space, useDebugLibrary ? SceneInputsDebug.sb_set_element_surface(space, element, points, worldVertices.Count, role)
                : SceneInputsRelease.sb_set_element_surface(space, element, points, worldVertices.Count, role), "sb_set_element_surface", element);
        }

        /// <summary>Source observation; does not solve, propagate, or move the actor root.</summary>
        public static void SetAnchorTransform(long space, long actor, string name,
            Vector3 worldPosition, float unityYawDegrees)
        {
            ValidateAnchorName(name);
            if (!IsFiniteScenePoint(worldPosition) || !IsFiniteSceneFloat(unityYawDegrees))
                throw new ArgumentException("Anchor world pose must be finite.");
            CoordinateConverter.UnityToSpatialBlend3D(worldPosition, out double x, out double y, out double z);
            double yaw = CoordinateConverter.UnityToSpatialBlendAngle(unityYawDegrees) * Math.PI / 180d;
            if (useDebugLibrary) SceneInputsDebug.sb_set_anchor_transform(space, actor, name, x, y, z, yaw);
            else SceneInputsRelease.sb_set_anchor_transform(space, actor, name, x, y, z, yaw);
        }

        public static void ClearAnchorTransform(long space, long actor, string name)
        {
            ValidateAnchorName(name);
            if (useDebugLibrary) SceneInputsDebug.sb_clear_anchor_transform(space, actor, name);
            else SceneInputsRelease.sb_clear_anchor_transform(space, actor, name);
        }

        private static void ValidateAnchorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0)
                throw new ArgumentException("Anchor name must be nonempty and contain no NUL.", nameof(name));
        }

        private static bool IsFiniteSceneFloat(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFiniteScenePoint(Vector3 value) =>
            IsFiniteSceneFloat(value.x) && IsFiniteSceneFloat(value.y) && IsFiniteSceneFloat(value.z);
    }
}
