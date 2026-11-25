// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpatialBlend
{
    /// <summary>
    /// Counts the expensive work that must happen at most once per actual change (a NavMesh bake, an anchor outline built, the
    /// room registered, a furniture box staged), so a test or a device session can see when something runs again with nothing
    /// changed. Counting is always on and costs an increment. With <see cref="LogEnabled"/> (the debug flag,
    /// <see cref="SpatialBlendSettings.logRuntimeWork"/>) every counted event is also logged with its duration.
    /// </summary>
    public static class RuntimeWorkCounters
    {
        /// <summary>The debug flag: log each counted event (tag <c>[RuntimeWork]</c>) with its duration in milliseconds.</summary>
        public static bool LogEnabled;

        /// <summary>NavMesh bakes actually run (<see cref="SceneNavMeshBaker"/>).</summary>
        public static int NavMeshBakes;
        /// <summary>Bake requests (MRUK scene loaded, room created/updated/removed, the Rebuild button) before coalescing.</summary>
        public static int NavMeshBakeRequests;
        /// <summary>Requests answered without a bake because the room geometry had not changed since the last one.</summary>
        public static int NavMeshBakesSkipped;
        /// <summary>Wire boxes built around a shared scene anchor (its 30% wires) or a pending/highlight outline.</summary>
        public static int AnchorOutlineBuilds;
        /// <summary>Line materials created for those outlines (one shared material is expected for the whole session).</summary>
        public static int OutlineMaterialsCreated;
        /// <summary>The room registered with the core (<see cref="SpatialBlendManager.RegisterRoomRegion"/>).</summary>
        public static int RoomRegistrations;
        /// <summary>The MRUK room's scene-geometry watcher bound (which stages every furniture box again).</summary>
        public static int SceneGeometryBinds;
        /// <summary>Furniture boxes staged or re-staged in the core.</summary>
        public static int SceneGeometryStages;
        /// <summary>Furniture updates answered without re-staging because the anchor's geometry had not changed.</summary>
        public static int SceneGeometryUnchanged;

        public static void Reset()
        {
            NavMeshBakes = NavMeshBakeRequests = NavMeshBakesSkipped = 0;
            AnchorOutlineBuilds = OutlineMaterialsCreated = 0;
            RoomRegistrations = SceneGeometryBinds = SceneGeometryStages = SceneGeometryUnchanged = 0;
        }

        /// <summary>A one-line summary, for a log or a test message.</summary>
        public static string Summary() =>
            $"navmesh bakes={NavMeshBakes} (requests={NavMeshBakeRequests}, unchanged={NavMeshBakesSkipped}), " +
            $"outlines={AnchorOutlineBuilds} (materials={OutlineMaterialsCreated}), room registrations={RoomRegistrations}, " +
            $"geometry binds={SceneGeometryBinds}, stages={SceneGeometryStages} (unchanged={SceneGeometryUnchanged})";

        /// <summary>Starts timing an event when the debug flag is on (zero cost otherwise).</summary>
        internal static long Start() => LogEnabled ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>Logs a counted event and, when <paramref name="started"/> came from <see cref="Start"/>, how long it took.</summary>
        internal static void Log(string what, long started = 0L)
        {
            if (!LogEnabled) return;
            if (started == 0L)
            {
                Debug.Log($"[RuntimeWork] {what} (frame {Time.frameCount})");
                return;
            }
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            Debug.Log($"[RuntimeWork] {what}: {ms:0.00} ms (frame {Time.frameCount})");
        }
    }
}
