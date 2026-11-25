// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Interactions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpatialBlend.Tests.RoomMatrix
{
    /// <summary>
    /// The placement's cost: the worst wall-clock milliseconds of a first spawn (RoomFit.SpawnAll, Chess and
    /// the Whiteboard) and of a drop (RoomFit.TryPull at seeded random points) in the exported FullFloor rooms (the largest), on
    /// the desktop Editor. Lobby rules (no core), which is what the user waits for on a drop. Results: placement-cost.tsv.
    /// </summary>
    [TestFixture]
    [Category("RoomMatrixCost")]
    [Explicit("Measured on demand: it needs the exported rooms.")]
    public sealed class RoomPlacementCost
    {
        private const string ChessPrefab = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private const string WhiteboardPrefab = "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab";
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (UnityEngine.Object o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private MRUKRoom Build(MrukRoomExport.RoomData data)
        {
            var root = new GameObject("room-" + data.name);
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
                typeof(MRUKAnchor).GetProperty("Label").SetValue(anchor, (MRUKAnchor.SceneLabels)a.label);
                typeof(MRUKAnchor).GetProperty("PlaneRect").SetValue(anchor, a.hasPlane ? (Rect?)a.planeRect : null);
                typeof(MRUKAnchor).GetProperty("VolumeBounds").SetValue(anchor, a.hasVolume ? (Bounds?)new Bounds(a.volumeCenter, a.volumeSize) : null);
                typeof(MRUKAnchor).GetProperty("PlaneBoundary2D").SetValue(anchor, new List<Vector2>(a.boundary));
                typeof(MRUKAnchor).GetProperty("Room").SetValue(anchor, room);
                room.Anchors.Add(anchor);
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR)) typeof(MRUKRoom).GetProperty("FloorAnchor").SetValue(room, anchor);
            }
            return room;
        }

        private GameObject Spawn(string path, string name)
        {
            GameObject go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            go.name = name;
            _objects.Add(go);
            SbEntity entity = go.GetComponentInChildren<SbEntity>(true);
            return entity != null ? entity.gameObject : go;
        }

        [Test]
        public void FullFloorRooms()
        {
            string dir = MrukRoomExport.ExportDirectory;
            Assert.That(Directory.Exists(dir), Is.True, dir);
            var rows = new List<string> { "room\tspawn_ms\tdrop_worst_ms\tdrop_median_ms" };
            var rng = new System.Random(12345);
            foreach (string path in Directory.GetFiles(dir, "FullFloor*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var data = JsonUtility.FromJson<MrukRoomExport.RoomData>(File.ReadAllText(path));
                MRUKRoom room = Build(data);
                RoomRegion region = MrukRoomRegion.Build(room);
                if (region == null) continue;
                GameObject chess = Spawn(ChessPrefab, "Chess"), board = Spawn(WhiteboardPrefab, "Whiteboard");
                Vector2 c = region.Floor.Aggregate(Vector2.zero, (s, v) => s + v) / region.Floor.Length;
                var objects = new[] { (chess, chess.GetComponentInChildren<PlacementMotif>(true)), (board, board.GetComponentInChildren<PlacementMotif>(true)) };
                RoomFit.SpawnAll(objects, new Vector3(c.x, 1.6f, c.y), Vector3.forward, region, null, null, SpatialBlendManager.RegionFitMarginMeters); // warm-up
                var watch = Stopwatch.StartNew();
                RoomFit.SpawnAll(objects, new Vector3(c.x, 1.6f, c.y), Vector3.forward, region, null, null, SpatialBlendManager.RegionFitMarginMeters);
                double spawn = watch.Elapsed.TotalMilliseconds;
                Vector2 min = region.Floor.Aggregate(Vector2.Min), max = region.Floor.Aggregate(Vector2.Max);
                var drops = new List<double>();
                for (int i = 0; i < 20; ++i)
                {
                    var at = new Vector3(Mathf.Lerp(min.x, max.x, (float)rng.NextDouble()), chess.transform.position.y, Mathf.Lerp(min.y, max.y, (float)rng.NextDouble()));
                    var offset = new Vector2(at.x - chess.transform.position.x, at.z - chess.transform.position.z);
                    watch.Restart();
                    RoomFit.TryPull(chess, offset, region, null, new[] { board }, SpatialBlendManager.RegionFitMarginMeters, out Vector2 dbgShift);
                    drops.Add(watch.Elapsed.TotalMilliseconds);
                }
                drops.Sort();
                // Where a drop's time goes: the shapes' compile, the obstacle sweep, the pull itself.
                watch.Restart(); for (int k = 0; k < 10; ++k) RoomFit.TryRegion(chess, out _); double compile = watch.Elapsed.TotalMilliseconds / 10;
                watch.Restart(); for (int k = 0; k < 10; ++k) RoomFit.Obstacles(region, null, chess, new[] { board }); double sweep = watch.Elapsed.TotalMilliseconds / 10;
                Debug.Log($"[RoomPlacementCost] {data.name}: compile {compile:0.00} ms, obstacles {sweep:0.00} ms, ring {region.Ring.Length} pts, furniture {region.Furniture.Length}, walls {region.Walls.Length}");
                rows.Add($"{data.name}\t{spawn:0.0}\t{drops.Last():0.0}\t{drops[drops.Count / 2]:0.0}\t{compile:0.00}\t{sweep:0.00}");
                UnityEngine.Object.DestroyImmediate(chess.transform.root.gameObject);
                UnityEngine.Object.DestroyImmediate(board.transform.root.gameObject);
            }
            File.WriteAllText(Path.Combine(dir, "placement-cost.tsv"), string.Join("\n", rows) + "\n");
            Debug.Log("[RoomPlacementCost]\n" + string.Join("\n", rows));
        }

        private static readonly MethodInfo ResetPreview = typeof(PlacementMotif).GetMethod("ResetPreview", BindingFlags.Instance | BindingFlags.NonPublic);

        // One grab of a board: its first preview (the inputs prepared), nine more ticks each 5 cm on, then the tick a second on that
        // takes the inputs again (PlacementMotif.PreviewInputsSeconds). Milliseconds.
        private static (double first, List<double> ticks, double reprepare) Grab(BoardRoom room, PlacementMotif motif, Pose at, bool onWall, Vector3 step)
        {
            room.Hold(motif, at, onWall);
            ResetPreview.Invoke(motif, null);
            var watch = Stopwatch.StartNew();
            motif.RefreshPreview();
            double first = watch.Elapsed.TotalMilliseconds;
            var ticks = new List<double>();
            for (int k = 1; k < 10; ++k)
            {
                room.Hold(motif, new Pose(at.position + step * k, at.rotation), onWall);
                watch.Restart();
                motif.RefreshPreview();
                ticks.Add(watch.Elapsed.TotalMilliseconds);
            }
            motif.TickPreview(100f);                               // the inputs taken at t = 100 s
            room.Hold(motif, new Pose(at.position + step * 10, at.rotation), onWall);
            watch.Restart();
            motif.TickPreview(100f + PlacementMotif.PreviewInputsSeconds + 0.05f);   // a second on: taken again, then the preview
            double reprepare = watch.Elapsed.TotalMilliseconds;
            room.Release(motif);
            return (first, ticks, reprepare);
        }

        /// <summary>
        /// The drop preview's cost per tick (target: under 3 ms on the desktop), in the FullFloor rooms: Chess carried at
        /// seeded random points, the Whiteboard along the walls; the first tick of a grab and the tick each second that takes the inputs
        /// again are reported apart. Then a large room cluttered with furniture (0.5 m aisles), where most of the chessboard's pulls fail
        /// within the preview's reach (RoomFit.PreviewReachMeters: the bounded search's whole cost each tick). Results: preview-cost.tsv.
        /// </summary>
        [Test]
        public void PreviewTicks()
        {
            string dir = MrukRoomExport.ExportDirectory;
            Assert.That(Directory.Exists(dir), Is.True, dir);
            var rows = new List<string> { "room\tobject\tfirst_worst_ms\treprepare_worst_ms\ttick_worst_ms\ttick_p95_ms\ttick_median_ms\tticks" };
            var rng = new System.Random(4242);
            var reprepares = new List<double>();
            void Row(string name, string what, List<double> firsts, List<double> ticks)
            {
                firsts.Sort(); ticks.Sort();
                rows.Add($"{name}\t{what}\t{firsts.Last():0.00}\t{reprepares.Max():0.00}\t{ticks.Last():0.00}\t{ticks[(int)(ticks.Count * 0.95)]:0.00}\t{ticks[ticks.Count / 2]:0.00}\t{ticks.Count}");
                reprepares.Clear();
            }
            foreach (string path in Directory.GetFiles(dir, "FullFloor*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var data = JsonUtility.FromJson<MrukRoomExport.RoomData>(File.ReadAllText(path));
                RoomRegion region = MrukRoomRegion.Build(Build(data));
                if (region == null) continue;
                BoardRoom room = BoardRoom.Create(region);
                try
                {
                    Vector2 min = region.Floor.Aggregate(Vector2.Min), max = region.Floor.Aggregate(Vector2.Max);
                    // Warm-up (the JIT, the first compile).
                    Grab(room, room.ChessMotif, new Pose(new Vector3((min.x + max.x) / 2, 0.6f, (min.y + max.y) / 2), Quaternion.identity), false, Vector3.right * 0.05f);
                    reprepares.Clear();
                    var firsts = new List<double>(); var ticks = new List<double>();
                    for (int i = 0; i < 20; ++i)
                    {
                        var at = new Vector3(Mathf.Lerp(min.x, max.x, (float)rng.NextDouble()), 0.6f, Mathf.Lerp(min.y, max.y, (float)rng.NextDouble()));
                        var g = Grab(room, room.ChessMotif, new Pose(at, Quaternion.Euler(0f, rng.Next(360), 0f)), false, Vector3.right * 0.05f);
                        firsts.Add(g.first); ticks.AddRange(g.ticks); reprepares.Add(g.reprepare);
                    }
                    Row(data.name, "Chess", firsts, ticks);
                    firsts = new List<double>(); ticks = new List<double>();
                    foreach ((Vector2 a, Vector2 b, Vector2 inward) e in region.WallEdges.Take(12))
                    {
                        Vector2 q = Vector2.Lerp(e.a, e.b, (float)rng.NextDouble());
                        Pose wall = room.WhiteboardMotif.WallPose(new Vector3(q.x, 0f, q.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
                        Vector3 along = new Vector3(e.b.x - e.a.x, 0f, e.b.y - e.a.y).normalized * 0.05f;
                        var g = Grab(room, room.WhiteboardMotif, wall, true, along);
                        firsts.Add(g.first); ticks.AddRange(g.ticks); reprepares.Add(g.reprepare);
                    }
                    Row(data.name, "Whiteboard", firsts, ticks);
                }
                finally { room.Dispose(); }
            }

            // The worst case: a 10 m room tiled with furniture leaving 0.5 m aisles: the chessboard fits nowhere unturned.
            var tiles = new List<Vector2[]>();
            for (float x = -5f; x < 5f; x += 1.5f)
                for (float z = -5f; z < 5f; z += 1.5f)
                    tiles.Add(BoardRoom.Box(x, z, x + 1f, z + 1f));
            RoomRegion cluttered = RoomRegion.Build(new[] { new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f) }, null, tiles);
            BoardRoom worst = BoardRoom.Create(cluttered);
            try
            {
                var firsts = new List<double>(); var ticks = new List<double>();
                for (int i = 0; i < 5; ++i)
                {
                    var g = Grab(worst, worst.ChessMotif, new Pose(new Vector3(-4.5f + i, 0.6f, -4.5f + i), Quaternion.identity), false, Vector3.right * 0.05f);
                    firsts.Add(g.first); ticks.AddRange(g.ticks); reprepares.Add(g.reprepare);
                }
                Row("Cluttered10m", "Chess", firsts, ticks);
            }
            finally { worst.Dispose(); }
            File.WriteAllText(Path.Combine(dir, "preview-cost.tsv"), string.Join("\n", rows) + "\n");
            Debug.Log("[PreviewCost]\n" + string.Join("\n", rows));
        }
    }
}
#endif
