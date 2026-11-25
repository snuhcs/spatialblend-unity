// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// What releasing an object does, against v1.
    /// v1 gates the floor snap on a serialized <c>_snapOn</c>, off by default and on for the chessboard. An
    /// object without it keeps the height it was released at and is only clamped to the walkable region.
    /// abi4 dropped the flag and raycast the floor for every object.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ObjectPlacementSnapTests
    {
        private const string Chess = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private const string Preset = "Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab";

        [Test]
        public void ReleasePlanFollowsV1()
        {
            Assert.That(ObjectPlacementMotif.PlanRelease(true, true), Is.EqualTo(ObjectPlacementMotif.ReleasePlan.SnapToFloor));
            Assert.That(ObjectPlacementMotif.PlanRelease(true, false), Is.EqualTo(ObjectPlacementMotif.ReleasePlan.SnapToFloor));
            Assert.That(ObjectPlacementMotif.PlanRelease(false, true), Is.EqualTo(ObjectPlacementMotif.ReleasePlan.ClampInPlace));
            Assert.That(ObjectPlacementMotif.PlanRelease(false, false), Is.EqualTo(ObjectPlacementMotif.ReleasePlan.Stay));
        }

        [Test]
        public void SnapIsOffByDefault()
        {
            var go = new GameObject("object-motif");
            try
            {
                var motif = go.AddComponent<ObjectPlacementMotif>();
                Assert.That(Field(motif, "_snapOn"), Is.EqualTo(false));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ANonSnappingObjectWithoutAWalkableClampStaysWhereItWasReleased()
        {
            var go = new GameObject("released-object");
            try
            {
                var motif = go.AddComponent<ObjectPlacementMotif>();
                var released = new Vector3(1f, 1.4f, 2f);
                go.transform.position = released;
                SetField(motif, "_entityTransform", go.transform);
                SetField(motif, "_clampToWalkable", false);
                SetField(motif, "_snapOn", false);

                object moved = typeof(ObjectPlacementMotif)
                    .GetMethod("PerformRaycastAndSnap", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(motif, null);

                Assert.That(moved, Is.EqualTo(false), "v1 :201: no snap and no clamp, so there is nothing to do.");
                Assert.That(go.transform.position, Is.EqualTo(released));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheChessboardSnapsAndTheOtherObjectsDoNot()
        {
            // v1 prefabs: Chess.prefab:6357 _snapOn: 1, ObjectPreset.prefab:76 _snapOn: 0. The others do not set it.
            Assert.That(SnapOn(Chess), Is.True);
            Assert.That(SnapOn(Preset), Is.False);
            Assert.That(SnapOn("Assets/SpatialBlend/Interaction/Prefabs/ObjectPlacementMotif.prefab"), Is.False);
        }

        [Test]
        public void ASnappedObjectSettlesAtTheAbsoluteHoverHeightNotAboveTheHit()
        {
            // As v1: (hit.x, _hoverDistance, hit.z).
            // A floor hit at y = 0.3 does not lift the target, and the collider offset is not added.
            Vector3 target = ObjectPlacementMotif.SnapTarget(new Vector3(1.5f, 0.3f, -2f), 0.5f);
            Assert.That(target, Is.EqualTo(new Vector3(1.5f, 0.5f, -2f)));
            Assert.That(ObjectPlacementMotif.SnapTarget(new Vector3(0f, 0f, 0f), 0.1f).y, Is.EqualTo(0.1f));
        }

        [Test]
        public void TheChessboardHoverDistanceIsItsShippedValue()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Chess);
            var motif = prefab.GetComponentInChildren<ObjectPlacementMotif>(true);
            Assert.That(new SerializedObject(motif).FindProperty("_hoverDistance").floatValue, Is.EqualTo(0.5f));
        }

        private static bool SnapOn(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            var motif = prefab.GetComponentInChildren<ObjectPlacementMotif>(true);
            Assert.That(motif, Is.Not.Null, path + " carries no ObjectPlacementMotif");
            return new SerializedObject(motif).FindProperty("_snapOn").boolValue;
        }

        private static FieldInfo Find(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(name);
        }

        private static object Field(object target, string name) => Find(target.GetType(), name).GetValue(target);
        private static void SetField(object target, string name, object value) => Find(target.GetType(), name).SetValue(target, value);
    }
}
#endif
