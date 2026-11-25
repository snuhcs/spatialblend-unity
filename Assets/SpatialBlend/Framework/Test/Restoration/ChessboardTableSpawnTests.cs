// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Samples.Objects;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As in v1, the chessboard spawns at <c>anchor.position + offset</c>.
    /// A TABLE anchor's origin is already at its top surface, so abi4's extra half-height lift, and its
    /// rotation of the offset by the anchor, put the board too high and off its place on a turned table.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ChessboardTableSpawnTests
    {
        private GameObject _anchorObject;
        private GameObject _board;

        [TearDown]
        public void TearDown()
        {
            if (_anchorObject != null) UnityEngine.Object.DestroyImmediate(_anchorObject);
            if (_board != null) UnityEngine.Object.DestroyImmediate(_board);
        }

        [Test]
        public void TheBoardSitsAtTheTableOriginPlusTheOffsetWithNoHalfHeightLift()
        {
            MRUKAnchor table = MakeTable(new Vector3(2f, 0.75f, -1f), Quaternion.identity, new Vector3(1.2f, 0.75f, 0.8f));
            SpawnOnMRUKAnchor spawn = MakeSpawn(offset: new Vector3(0f, 0.1f, 0f));

            Place(spawn, table);

            Assert.That(Vector3.Distance(_board.transform.position, new Vector3(2f, 0.85f, -1f)), Is.LessThan(1e-5f),
                "origin (2, 0.75, -1) + offset (0, 0.1, 0); a half-height lift would give y = 1.225");
        }

        [Test]
        public void TheOffsetIsNotRotatedByATurnedTable()
        {
            MRUKAnchor table = MakeTable(new Vector3(0f, 0.7f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector3(1f, 0.7f, 1f));
            SpawnOnMRUKAnchor spawn = MakeSpawn(offset: new Vector3(0.3f, 0.1f, 0f));

            Place(spawn, table);

            Assert.That(Vector3.Distance(_board.transform.position, new Vector3(0.3f, 0.8f, 0f)), Is.LessThan(1e-5f),
                "v1 adds the offset in world space; TransformPoint would turn it to (0, 0.8, -0.3)");
            Assert.That(_board.transform.rotation, Is.EqualTo(Quaternion.identity), "a horizontal surface keeps an upright board");
        }

        private MRUKAnchor MakeTable(Vector3 position, Quaternion rotation, Vector3 size)
        {
            _anchorObject = new GameObject("TABLE");
            _anchorObject.transform.SetPositionAndRotation(position, rotation);
            var anchor = _anchorObject.AddComponent<MRUKAnchor>();
            typeof(MRUKAnchor).GetProperty("VolumeBounds", BindingFlags.Instance | BindingFlags.Public)
                .SetValue(anchor, (Bounds?)new Bounds(Vector3.zero, size));
            return anchor;
        }

        private SpawnOnMRUKAnchor MakeSpawn(Vector3 offset)
        {
            _board = new GameObject("chessboard");
            var spawn = _board.AddComponent<SpawnOnMRUKAnchor>();
            Set(spawn, "_targetLabel", MRUKAnchor.SceneLabels.TABLE);
            Set(spawn, "_positionOffset", offset);
            return spawn;
        }

        private static void Place(SpawnOnMRUKAnchor spawn, MRUKAnchor anchor)
        {
            typeof(SpawnOnMRUKAnchor).GetMethod("ApplyAnchorTransform", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(spawn, new object[] { anchor });
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
#endif
