// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A region polygon that does not
    /// contain the object's footprint is refused with a visible error. The compiler drops it silently at share time.
    /// The object is a 1 x 1 m box at the origin.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RegionPolygonRefusalTests
    {
        private GameObject _target;
        private GameObject _motifHost;
        private GameObject _contextHost;
        private AffordanceAnnotationMotif _motif;
        private readonly List<string> _refusals = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _target = new GameObject("object");
            var box = _target.AddComponent<BoxCollider>();
            box.size = Vector3.one;
            _contextHost = new GameObject("context");
            var context = _contextHost.AddComponent<ManipulationContext>();
            typeof(ManipulationContext).GetField("_targetObject", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, _target);
            _motifHost = new GameObject("motif");
            _motif = _motifHost.AddComponent<AffordanceAnnotationMotif>();
            _motif.SetContext(context);
            _refusals.Clear();
            AffordanceAnnotationMotif.RegionRefused += _refusals.Add;
        }

        [TearDown]
        public void TearDown()
        {
            AffordanceAnnotationMotif.RegionRefused -= _refusals.Add;
            UnityEngine.Object.DestroyImmediate(_motifHost);
            UnityEngine.Object.DestroyImmediate(_contextHost);
            UnityEngine.Object.DestroyImmediate(_target);
        }

        private static void ExpectRefusalLog() =>
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Region not created"));

        private void Finalize(params Vector3[] ring) =>
            typeof(AffordanceAnnotationMotif).GetMethod("OnFinalized", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_motif, new object[] { ring, new Plane(Vector3.up, Vector3.zero) });

        private static Vector3[] Square(float half) => new[]
        {
            new Vector3(-half, 0f, -half), new Vector3(half, 0f, -half), new Vector3(half, 0f, half), new Vector3(-half, 0f, half),
        };

        [Test]
        public void APolygonInsideTheFootprintIsRefusedWithAVisibleError()
        {
            ExpectRefusalLog();
            Finalize(Square(0.2f)); // smaller than the 1 x 1 footprint

            Assert.That(_refusals, Has.Count.EqualTo(1));
            StringAssert.Contains("must contain the object footprint", _refusals[0]);
            Assert.That(AffordanceAnnotationMotif.LastRefusal, Is.EqualTo(_refusals[0]));
            Assert.That(_target.GetComponent<SbSpatialAuthoring>(), Is.Null, "nothing the refused polygon made is kept");
            Assert.That(_target.GetComponent<DeclaredRegionVisualizer>(), Is.Null);
        }

        [Test]
        public void APolygonThatContainsTheFootprintIsAccepted()
        {
            Finalize(Square(1f));

            var authoring = _target.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring, Is.Not.Null);
            Assert.That(authoring.declaredExtent, Has.Length.EqualTo(4));
            Assert.That(_refusals, Is.Empty);
            Assert.That(_target.GetComponent<DeclaredRegionVisualizer>(), Is.Not.Null);
        }

        [Test]
        public void ARefusalDoesNotTouchAnExtentTheObjectAlreadyDeclared()
        {
            Finalize(Square(1f));
            var authoring = _target.GetComponent<SbSpatialAuthoring>();
            Vector2[] declared = (Vector2[])authoring.declaredExtent.Clone();

            ExpectRefusalLog();
            Finalize(Square(0.1f));

            Assert.That(_refusals, Has.Count.EqualTo(1));
            Assert.That(authoring.declaredExtent, Is.EqualTo(declared));
        }

        [Test]
        public void TheValidationNamesTheSimplePolygonRule()
        {
            var authoring = _target.AddComponent<SbSpatialAuthoring>();
            var bowtie = new[] { new Vector2(-1f, -1f), new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, 1f) };
            Assert.That(authoring.TryValidateDeclaredExtent(bowtie, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(authoring.TryValidateDeclaredExtent(new[] { new Vector2(0f, 0f), new Vector2(1f, 0f) }, out error), Is.False);
        }
    }
}
#endif
