// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The fourth point of a box sets its height from the controller ray (v1 BoxAnnotationMotif, the
    /// P3 comment: "computes height from the ray directly, skip floor projection", and the cap of the
    /// height at _maxHeight, 2.5 m). The user aims up to make the box tall, and a ray that aims up never meets
    /// the floor plane. abi4 projected every ray to the floor first and returned when it failed, so the height
    /// froze at its first value as soon as the user aimed upward.
    ///
    /// The rays are recorded controller poses written by hand. The base is a 0.5 m by 0.4 m rectangle on the
    /// floor, P0 at the origin, so its centre is (0.25, 0, 0.2). A ray from (0.25, 1, -1) that rises by r for
    /// each metre it advances in z reaches z = 0.2, the base centre's depth, at 1.2 m, and is nearest the
    /// vertical line through the centre there. Its height is 1 + 1.2 r.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class BoxAnnotationHeightTests
    {
        private const float Tolerance = 1e-4f;

        // The rays that set the base: each hits the floor at the point the stage wants.
        private static readonly Vector3 RayOrigin = new Vector3(0f, 1.5f, 0f);
        private static readonly Vector3[] BaseRays =
        {
            new Vector3(0f, -1.5f, 0f),     // P0 at (0, 0, 0)
            new Vector3(0.5f, -1.5f, 0f),   // P1 at (0.5, 0, 0)
            new Vector3(0f, -1.5f, 0.4f),   // P2 at (0, 0, 0.4)
        };

        // Recorded height rays: origin, direction, expected height.
        private static readonly object[] HeightRays =
        {
            new object[] { new Vector3(0.25f, 1f, -1f), new Vector3(0f, 0.1f, 1f), 1.12f },  // aims a little up
            new object[] { new Vector3(0.25f, 1f, -1f), new Vector3(0f, 0.5f, 1f), 1.6f },
            new object[] { new Vector3(0.25f, 1f, -1f), new Vector3(0f, 1f, 1f), 2.2f },
            new object[] { new Vector3(0.25f, 1f, -1f), new Vector3(0f, -0.1f, 1f), 0.88f }, // aims a little down, still above the floor at the centre
        };

        private GameObject _host;
        private GameObject _rayAnchor;
        private BoxAnnotationMotif _motif;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("box-motif");
            _motif = _host.AddComponent<BoxAnnotationMotif>();
            _rayAnchor = new GameObject("recorded-controller");
            SetField(_motif, "_rayOriginOverride", _rayAnchor.transform);
            // The ray is the recorded one, not the scene's interactor.
            Assert.That(SpatialBlend.Interactions.InteractionModeManager.Interactor, Is.Null);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_host);
            UnityEngine.Object.DestroyImmediate(_rayAnchor);
        }

        [TestCaseSource(nameof(HeightRays))]
        public void AimingUpSetsTheHeightFromTheRay(object[] recorded)
        {
            Vector3 origin = (Vector3)recorded[0];
            Vector3 direction = (Vector3)recorded[1];
            float expected = (float)recorded[2];
            PlaceTheBase();

            Aim(origin, direction);
            Invoke(_motif, "UpdateCreate");

            Vector3 p3 = (Vector3)GetField(_motif, "_p3");
            Assert.That(p3.x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(p3.z, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(p3.y, Is.EqualTo(expected).Within(Tolerance),
                "The height follows the ray, as in v1.");
        }

        [Test]
        public void TheHeightKeepsFollowingAsTheUserAimsHigherAndLower()
        {
            PlaceTheBase();
            float previous = 0f;
            foreach (float rise in new[] { 0.1f, 0.5f, 1f })
            {
                Aim(new Vector3(0.25f, 1f, -1f), new Vector3(0f, rise, 1f));
                Invoke(_motif, "UpdateCreate");
                float height = ((Vector3)GetField(_motif, "_p3")).y;
                Assert.That(height, Is.GreaterThan(previous), $"rise {rise}: the height froze");
                previous = height;
            }
            // And back down: the height is not latched at its largest.
            Aim(new Vector3(0.25f, 1f, -1f), new Vector3(0f, 0.1f, 1f));
            Invoke(_motif, "UpdateCreate");
            Assert.That(((Vector3)GetField(_motif, "_p3")).y, Is.EqualTo(1.12f).Within(Tolerance));
        }

        [Test]
        public void TheHeightIsCappedAtTwoAndAHalfMetres()
        {
            // 1 + 1.2 * 2 = 3.4 m from the ray. v1 caps it (BoxAnnotationMotif._maxHeight).
            PlaceTheBase();
            Aim(new Vector3(0.25f, 1f, -1f), new Vector3(0f, 2f, 1f));
            Invoke(_motif, "UpdateCreate");
            Assert.That(((Vector3)GetField(_motif, "_p3")).y, Is.EqualTo(2.5f).Within(Tolerance));
        }

        [Test]
        public void TheBaseStagesStillNeedTheFloor()
        {
            // The first three points are placed on the floor, so a ray that misses it places nothing.
            PlaceTheBase();
            SetField(_motif, "_currentPointIndex", 1);
            Vector3 before = (Vector3)GetField(_motif, "_p1");
            Aim(RayOrigin, new Vector3(0.5f, 1.5f, 0f));
            Invoke(_motif, "UpdateCreate");
            Assert.That((Vector3)GetField(_motif, "_p1"), Is.EqualTo(before));
        }

        // helpers

        /// <summary>Plays the three base rays: P0 (0,0,0), P1 (0.5,0,0), P2 (0,0,0.4). Leaves the motif at the height stage.</summary>
        private void PlaceTheBase()
        {
            Invoke(_motif, "BeginCreation", Vector3.zero, Vector3.up);
            for (int stage = 0; stage < BaseRays.Length; stage++)
            {
                Aim(RayOrigin, BaseRays[stage]);
                Invoke(_motif, "UpdateCreate");
                Invoke(_motif, "LockCurrentPoint");
            }
            Assert.That(GetField(_motif, "_currentPointIndex"), Is.EqualTo(3));
            AssertVector((Vector3)GetField(_motif, "_p0"), Vector3.zero);
            AssertVector((Vector3)GetField(_motif, "_p1"), new Vector3(0.5f, 0f, 0f));
            AssertVector((Vector3)GetField(_motif, "_p2"), new Vector3(0f, 0f, 0.4f));
        }

        private void Aim(Vector3 origin, Vector3 direction)
        {
            _rayAnchor.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction.normalized));
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"expected {expected}, was {actual}");
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(name);
        }

        private static object GetField(object target, string name) => FindField(target.GetType(), name).GetValue(target);

        private static void SetField(object target, string name, object value) =>
            FindField(target.GetType(), name).SetValue(target, value);

        private static void Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(name);
            method.Invoke(target, args);
        }
    }
}
#endif
