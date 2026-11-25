// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SpatialHierarchyAndAnchorPoseContractTests
    {
        [Test]
        public void ValidatedSnapshotDoesNotExposeMutableNodeOrPolygonStorage()
        {
            SpatialHierarchyDescriptor descriptor = ChairDescriptor("immutable-space");
            MethodInfo create = typeof(SpatialHierarchyDescriptor).GetMethod("TryCreateSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(create, Is.Not.Null);
            object[] arguments = { null, null };
            Assert.That(create.Invoke(descriptor, arguments), Is.True);
            object snapshot = arguments[0];
            PropertyInfo nodesProperty = snapshot.GetType().GetProperty("Nodes");
            Assert.That(nodesProperty, Is.Not.Null);
            var first = (SpatialHierarchyNode[])nodesProperty.GetValue(snapshot);
            string originalId = first[0].id;
            SpatialHierarchyNode support = Array.Find(first,
                node => node.kind == SpatialHierarchyNodeKind.Affordance);
            string supportId = support.id;
            Vector3 originalVertex = support.localSupportPolygon[0];
            first[0].id = "mutated-copy";
            support.localSupportPolygon[0] = Vector3.one * 900f;
            first[0] = null;
            descriptor.nodes[0].id = "mutated-authoring";

            var second = (SpatialHierarchyNode[])nodesProperty.GetValue(snapshot);
            Assert.That(second[0].id, Is.EqualTo(originalId));
            SpatialHierarchyNode preservedSupport = Array.Find(second, node => node.id == supportId);
            Assert.That(preservedSupport.localSupportPolygon[0], Is.EqualTo(originalVertex));
        }

        [Test]
        public void ExplicitCrossSpaceMap_ExportsLiveAuthoredSupportEndpoints()
        {
            var sourceGo = new GameObject("source-hierarchy-manager");
            var targetGo = new GameObject("target-hierarchy-manager");
            var sourceManager = sourceGo.AddComponent<RecordingHierarchyManager>();
            var targetManager = targetGo.AddComponent<RecordingHierarchyManager>();
            try
            {
                sourceManager.ConfigureConnected(100, 7);
                targetManager.ConfigureConnected(200, 9);
                SpatialHierarchyDescriptor source = ChairDescriptor("space-chair");
                SpatialHierarchyDescriptor target = SofaDescriptor("space-sofa");
                var sink = new RecordingCorrespondenceSink();
                Assert.That(sourceManager.RegisterLocalSpatialHierarchy(source,
                    out SpatialHierarchyRegistration sourceRegistration), Is.True);
                Assert.That(targetManager.RegisterLocalSpatialHierarchy(target,
                    out SpatialHierarchyRegistration targetRegistration), Is.True);
                Assert.That(SpatialBlendManager.TryPublishSpatialSupportCorrespondences(sourceRegistration,
                    targetRegistration, new[] { new SpatialSupportCorrespondence
                    {
                        sourceSupportId = "chair.seatA", targetSupportId = "sofa.supportA"
                    } }, sink), Is.True);
                Assert.That(sourceRegistration.TryGetSupportElement("chair.seatA", out long chair), Is.True);
                Assert.That(targetRegistration.TryGetSupportElement("sofa.supportA", out long sofaA), Is.True);
                Assert.That(sink.Published, Has.Count.EqualTo(1));
                Assert.That(sink.Published[0].SourceElementId, Is.EqualTo(chair));
                Assert.That(sink.Published[0].TargetElementId, Is.EqualTo(sofaA));
                Assert.That(sink.Published[0].SourceNativeSpace, Is.EqualTo(100));
                Assert.That(sink.Published[0].TargetNativeSpace, Is.EqualTo(200));

                sourceManager.ConfigureConnected(101, 8);
                Assert.That(SpatialBlendManager.TryPublishSpatialSupportCorrespondences(sourceRegistration,
                    targetRegistration, new[] { new SpatialSupportCorrespondence
                    {
                        sourceSupportId = "chair.seatA", targetSupportId = "sofa.supportA"
                    } }, sink), Is.False, "Stale generation must not export endpoints.");
            }
            finally { UnityEngine.Object.DestroyImmediate(targetGo); UnityEngine.Object.DestroyImmediate(sourceGo); }
        }

        [Test]
        public void InvalidHierarchyAndRejectedMap_MutateNoNativeState()
        {
            SpatialHierarchyDescriptor invalid = ChairDescriptor("invalid-space");
            invalid.nodes[2].parentId = "missing";
            Assert.That(invalid.TryValidate(out _), Is.False);

            var go = new GameObject("hierarchy-manager-invalid");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(10, 1);
                Assert.That(manager.RegisterLocalSpatialHierarchy(invalid, out _), Is.False);
                Assert.That(manager.Parents, Is.Empty);

                SpatialHierarchyDescriptor valid = ChairDescriptor("valid-space");
                manager.ThrowAfterFirstTransform = true;
                LogAssert.Expect(LogType.Error,
                    "[SpatialBlend][Geometry] Failed to register explicit spatial hierarchy: injected set failure");
                Assert.That(manager.RegisterLocalSpatialHierarchy(valid, out _), Is.False);
                Assert.That(manager.Removed, Is.EqualTo(new[] { 10L }),
                    "A set-hook failure must remove the captured allocation.");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RecordedFrame_ValidatesWholeFrameBeforeSingleSinkMutation()
        {
            var go = new GameObject("recorded-anchor-source");
            var source = go.AddComponent<RecordedAnchorPoseSource>();
            var sink = new RecordingFrameSink();
            try
            {
                source.SetKnownAnchorIds(new[] { "head", "hand_l" });
                source.SetSink(sink);
                AnchorPoseFrame invalid = Frame(1, 1d, "head", "head");
                Assert.That(source.TrySubmit(invalid, out _), Is.False);
                Assert.That(sink.Calls, Is.EqualTo(0));

                AnchorPoseFrame valid = Frame(2, 2d, "head", "hand_l");
                Assert.That(source.TrySubmit(valid, out string error), Is.True, error);
                Assert.That(sink.Calls, Is.EqualTo(1));
                Assert.That(source.TrySubmit(Frame(2, 3d, "head"), out _), Is.False);
                Assert.That(sink.Calls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void SnapshotAndCapturedSpaceSurviveMutationAndReentry()
        {
            var go = new GameObject("hierarchy-manager-reentry");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(30, 3);
                SpatialHierarchyDescriptor descriptor = ChairDescriptor("snapshot-space");
                Matrix4x4 original = descriptor.nodes[1].localTransform;
                manager.OnAdd = () =>
                {
                    descriptor.nodes[1].localTransform = Matrix4x4.Translate(new Vector3(99f, 0f, 0f));
                    manager.ConfigureConnected(31, 4);
                };
                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out _), Is.False);
                Assert.That(manager.AddSpaces, Is.EqualTo(new[] { 30L }));
                Assert.That(manager.RemovedSpaces, Is.Empty,
                    "A retired generation must not receive rollback calls on its captured native space.");
                Assert.That(manager.Transforms, Has.Count.EqualTo(0),
                    "Generation changes after allocation must stop before any later mutation.");
                Assert.That(original, Is.Not.EqualTo(descriptor.nodes[1].localTransform));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RotatedAuthoredLocalTransformsReachOnlyTheirOwnHierarchyElements()
        {
            var go = new GameObject("hierarchy-manager-rotated");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(40, 5);
                SpatialHierarchyDescriptor descriptor = ChairDescriptor("rotated-space");
                Matrix4x4 rotatedObject = Matrix4x4.TRS(new Vector3(3f, 0f, -2f),
                    Quaternion.Euler(0f, 37f, 0f), Vector3.one);
                descriptor.nodes[1].localTransform = rotatedObject;
                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out _), Is.True);
                Assert.That(manager.Transforms[1], Is.EqualTo(rotatedObject));
                Assert.That(manager.AddSpaces, Has.All.EqualTo(40L));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void SupportVerticesAreBakedWorldCoordinatesFromImmutableLocalSnapshot()
        {
            var go = new GameObject("hierarchy-manager-world-surface");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(45, 5);
                SpatialHierarchyDescriptor descriptor = ChairDescriptor("world-surface-space");
                descriptor.nodes[0].localTransform = Matrix4x4.TRS(new Vector3(10f, 2f, 20f),
                    Quaternion.Euler(0f, 90f, 0f), Vector3.one);
                descriptor.nodes[1].localTransform = Matrix4x4.Translate(new Vector3(1f, 3f, 4f));
                descriptor.nodes[2].localTransform = Matrix4x4.TRS(new Vector3(2f, .5f, -1f),
                    Quaternion.Euler(0f, 90f, 0f), Vector3.one);
                descriptor.nodes[2].localSupportPolygon = new[]
                {
                    new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(0f, 0f, 1f)
                };
                manager.OnAdd = () => descriptor.nodes[2].localSupportPolygon[0] =
                    new Vector3(99f, 99f, 99f);

                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out _), Is.True);
                Assert.That(manager.Supports, Has.Count.EqualTo(1));
                Vector3[] actual = manager.Supports[0];
                // Independent hand calculation: region R_y(90) composes chair
                // and affordance offsets to (13, 5.5, 17); their rotations compose
                // to R_y(180), producing this asymmetric, nonzero-height triangle.
                AssertVector(actual[0], new Vector3(13f, 5.5f, 17f));
                AssertVector(actual[1], new Vector3(11f, 5.5f, 17f));
                AssertVector(actual[2], new Vector3(13f, 5.5f, 16f));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void OverflowedLocalOrComposedGeometry_IsRejectedBeforeNativeAllocation()
        {
            var go = new GameObject("hierarchy-manager-overflow");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(50, 6);
                SpatialHierarchyDescriptor determinantOverflow = ChairDescriptor("overflow-det");
                determinantOverflow.nodes[0].localTransform = Matrix4x4.Scale(
                    new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));
                Assert.That(determinantOverflow.TryValidate(out _), Is.False);
                Assert.That(manager.RegisterLocalSpatialHierarchy(determinantOverflow, out _), Is.False);

                SpatialHierarchyDescriptor composedOverflow = ChairDescriptor("overflow-world");
                composedOverflow.nodes[0].localTransform = Matrix4x4.Translate(
                    new Vector3(float.MaxValue, 0f, 0f));
                composedOverflow.nodes[1].localTransform = Matrix4x4.Translate(
                    new Vector3(float.MaxValue, 0f, 0f));
                Assert.That(composedOverflow.TryValidate(out _), Is.False);
                Assert.That(manager.RegisterLocalSpatialHierarchy(composedOverflow, out _), Is.False);

                SpatialHierarchyDescriptor supportOverflow = ChairDescriptor("overflow-support");
                supportOverflow.nodes[0].localTransform = Matrix4x4.Translate(
                    new Vector3(float.MaxValue, 0f, 0f));
                supportOverflow.nodes[2].localSupportPolygon = new[]
                {
                    new Vector3(float.MaxValue, 0f, 0f),
                    new Vector3(float.MaxValue, 0f, 1f),
                    new Vector3(float.MaxValue, 1f, 0f)
                };
                Assert.That(supportOverflow.TryValidate(out _), Is.False);
                Assert.That(manager.RegisterLocalSpatialHierarchy(supportOverflow, out _), Is.False);
                Assert.That(manager.AddSpaces, Is.Empty);
                Assert.That(manager.Transforms, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void NestedRegionsAndStandaloneObjectsRegisterParentFirstFromUnorderedForest()
        {
            var go = new GameObject("nested-forest-manager");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(60, 7);
                var outer = Node("outer", SpatialHierarchyNodeKind.Region, null);
                outer.localTransform = Matrix4x4.TRS(new Vector3(10, 2, 20),
                    Quaternion.Euler(0, 90, 0), Vector3.one);
                var inner = Node("inner", SpatialHierarchyNodeKind.Region, "outer");
                inner.localTransform = Matrix4x4.Translate(new Vector3(1, 0, 2));
                var chair = Node("chair", SpatialHierarchyNodeKind.Object, "inner");
                chair.localTransform = Matrix4x4.Translate(new Vector3(3, .8f, -1));
                var seat = Support("seat", "chair", "chair.seat");
                seat.localTransform = Matrix4x4.Translate(new Vector3(.5f, .1f, -.5f));
                seat.localSupportPolygon = new[] { Vector3.zero,
                    new Vector3(.4f, 0, 0), new Vector3(0, 0, .3f) };
                var standalone = Node("standalone", SpatialHierarchyNodeKind.Object, null);
                var descriptor = new SpatialHierarchyDescriptor
                {
                    spaceId = "nested-room",
                    nodes = new[] { seat, chair, inner, outer, standalone }
                };
                Assert.That(descriptor.TryValidate(out string error), Is.True, error);
                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out var registration), Is.True);
                Assert.That(registration.TryGetElement("outer", out long outerId), Is.True);
                Assert.That(registration.TryGetElement("inner", out long innerId), Is.True);
                Assert.That(registration.TryGetElement("chair", out long chairId), Is.True);
                Assert.That(manager.Parents, Is.EqualTo(new[] { 1L, 1L, outerId, innerId, chairId }),
                    "Both roots attach to the space; every authored ancestor is allocated first.");
                Assert.That(manager.Supports, Has.Count.EqualTo(1));
                // Independent R_y(90) composition of the three local offsets:
                // (4.5,.9,.5) becomes (.5,.9,-4.5), then adds (10,2,20).
                AssertVector(manager.Supports[0][0], new Vector3(10.5f, 2.9f, 15.5f));
                AssertVector(manager.Supports[0][1], new Vector3(10.5f, 2.9f, 15.1f));
                AssertVector(manager.Supports[0][2], new Vector3(10.8f, 2.9f, 15.5f));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase("cycle")]
        [TestCase("region-under-object")]
        [TestCase("root-affordance")]
        [TestCase("unknown-kind")]
        [TestCase("nul-space")]
        [TestCase("nul-node")]
        [TestCase("nul-parent")]
        [TestCase("nul-support")]
        [TestCase("self-crossing-nonzero-area")]
        [TestCase("duplicate-polygon-vertex")]
        public void SharedAuthoringValidationRejectsMalformedForestBeforeAllocation(string defect)
        {
            var descriptor = ChairDescriptor("invalid-forest");
            switch (defect)
            {
                case "cycle":
                    descriptor.nodes[1].kind = SpatialHierarchyNodeKind.Region;
                    descriptor.nodes[0].parentId = descriptor.nodes[1].id;
                    descriptor.nodes[2].kind = SpatialHierarchyNodeKind.Object;
                    descriptor.nodes[2].supportId = null;
                    descriptor.nodes[2].localSupportPolygon = null;
                    break;
                case "region-under-object": descriptor.nodes[0].parentId = "chair"; break;
                case "root-affordance": descriptor.nodes[2].parentId = null; break;
                case "unknown-kind": descriptor.nodes[0].kind = (SpatialHierarchyNodeKind)99; break;
                case "nul-space": descriptor.spaceId = "room\0alias"; break;
                case "nul-node": descriptor.nodes[2].id = "seat\0alias"; break;
                case "nul-parent": descriptor.nodes[2].parentId = "chair\0alias"; break;
                case "nul-support": descriptor.nodes[2].supportId = "seat\0alias"; break;
                case "self-crossing-nonzero-area":
                    descriptor.nodes[2].localSupportPolygon = new[] { Vector3.zero,
                        new Vector3(3, 0, 0), new Vector3(0, 0, 2), new Vector3(2, 0, 2) };
                    break;
                case "duplicate-polygon-vertex":
                    descriptor.nodes[2].localSupportPolygon = new[] { Vector3.zero,
                        new Vector3(2, 0, 0), new Vector3(2, 0, 2), new Vector3(2, 0, 2),
                        new Vector3(0, 0, 2) };
                    break;
            }
            var go = new GameObject("invalid-forest-manager");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(70, 8);
                Assert.That(descriptor.TryValidate(out _), Is.False, defect);
                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out _), Is.False);
                Assert.That(manager.Parents, Is.Empty);
                Assert.That(manager.Transforms, Is.Empty);
                Assert.That(manager.Supports, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void DeepReverseOrderedRegionsDoNotRequireRecursiveWorldComposition()
        {
            const int depth = 2048;
            var nodes = new List<SpatialHierarchyNode>();
            for (int index = 0; index < depth; ++index)
                nodes.Add(Node("region-" + index, SpatialHierarchyNodeKind.Region,
                    index == 0 ? null : "region-" + (index - 1)));
            nodes.Add(Node("chair", SpatialHierarchyNodeKind.Object, "region-" + (depth - 1)));
            nodes.Add(Support("seat", "chair", "seat.top"));
            nodes.Reverse();
            var descriptor = new SpatialHierarchyDescriptor { spaceId = "deep", nodes = nodes.ToArray() };
            var go = new GameObject("deep-forest-manager");
            var manager = go.AddComponent<RecordingHierarchyManager>();
            try
            {
                manager.ConfigureConnected(80, 9);
                Assert.That(manager.RegisterLocalSpatialHierarchy(descriptor, out var registration), Is.True);
                Assert.That(manager.Parents, Has.Count.EqualTo(depth + 2));
                Assert.That(registration.TryGetSupportElement("seat.top", out _), Is.True);
                Assert.That(manager.Supports, Has.Count.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        private static SpatialHierarchyDescriptor ChairDescriptor(string spaceId) => new SpatialHierarchyDescriptor
        {
            spaceId = spaceId,
            nodes = new[]
            {
                Node("region.living", SpatialHierarchyNodeKind.Region, null),
                Node("chair", SpatialHierarchyNodeKind.Object, "region.living"),
                Support("chair.seat", "chair", "chair.seatA"),
            }
        };

        private static SpatialHierarchyDescriptor SofaDescriptor(string spaceId) => new SpatialHierarchyDescriptor
        {
            spaceId = spaceId,
            nodes = new[]
            {
                Node("region.living", SpatialHierarchyNodeKind.Region, null),
                Node("sofa", SpatialHierarchyNodeKind.Object, "region.living"),
                Support("sofa.seatA", "sofa", "sofa.supportA"),
            }
        };

        private static SpatialHierarchyNode Node(string id, SpatialHierarchyNodeKind kind, string parent) =>
            new SpatialHierarchyNode { id = id, kind = kind, parentId = parent };

        private static SpatialHierarchyNode Support(string id, string parent, string supportId) =>
            new SpatialHierarchyNode
            {
                id = id,
                kind = SpatialHierarchyNodeKind.Affordance,
                parentId = parent,
                supportId = supportId,
                localSupportPolygon = new[]
                {
                    new Vector3(-.5f, 0f, -.5f), new Vector3(.5f, 0f, -.5f),
                    new Vector3(.5f, 0f, .5f), new Vector3(-.5f, 0f, .5f)
                }
            };

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
        }

        private static AnchorPoseFrame Frame(long sequence, double time, params string[] anchorIds)
        {
            var samples = new AnchorPoseSample[anchorIds.Length];
            for (int i = 0; i < anchorIds.Length; ++i)
            {
                samples[i] = new AnchorPoseSample
                {
                    anchorId = anchorIds[i], worldPosition = new Vector3(i, 1f, 2f),
                    worldRotation = Quaternion.identity
                };
            }
            return new AnchorPoseFrame { sequence = sequence, timeSeconds = time, anchors = samples };
        }

        private sealed class RecordingHierarchyManager : SpatialBlendManager
        {
            public readonly List<long> Parents = new List<long>();
            public readonly List<long> Removed = new List<long>();
            public readonly List<long> AddSpaces = new List<long>();
            public readonly List<long> RemovedSpaces = new List<long>();
            public readonly List<Matrix4x4> Transforms = new List<Matrix4x4>();
            public readonly List<Vector3[]> Supports = new List<Vector3[]>();
            private long _next = 10;
            public bool ThrowAfterFirstTransform;
            public Action OnAdd;
            protected override void InitializeNativeLibrary() { }
            public override void Host(int port) { }
            public void ConfigureConnected(long space, long generation)
            {
                SetPrivate("_initialized", true);
                SetPrivate("_connectionState", SpatialBlendConnectionState.Connected);
                SetPrivate("_spaceID", space);
                SetPrivate("_lifecycleGeneration", generation);
            }
            protected override long EnsureSpatialHierarchyRoom(long space) => 1;
            protected override long AddSpatialHierarchyElement(long space, long parent)
            {
                AddSpaces.Add(space);
                Parents.Add(parent);
                OnAdd?.Invoke();
                OnAdd = null;
                return _next++;
            }
            protected override void SetSpatialHierarchyTransform(long space, long element, Matrix4x4 transform)
            {
                Transforms.Add(transform);
                if (ThrowAfterFirstTransform) throw new InvalidOperationException("injected set failure");
            }
            protected override void SetSpatialHierarchySupport(long space, long element, Vector3[] vertices) =>
                Supports.Add((Vector3[])vertices.Clone());
            protected override bool RemoveSpatialHierarchyElement(long space, long element)
            {
                RemovedSpaces.Add(space);
                Removed.Add(element);
                return true;
            }
            private void SetPrivate(string name, object value) => typeof(SpatialBlendManager)
                .GetField(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).SetValue(this, value);
        }

        private sealed class RecordingCorrespondenceSink : ISpatialSupportCorrespondenceSink
        {
            public bool Accept = true;
            public readonly List<ResolvedSpatialSupportCorrespondence> Published =
                new List<ResolvedSpatialSupportCorrespondence>();
            public bool TryPublish(IReadOnlyList<ResolvedSpatialSupportCorrespondence> correspondences)
            {
                if (!Accept) return false;
                Published.AddRange(correspondences);
                return true;
            }
        }

        private sealed class RecordingFrameSink : IAnchorPoseFrameSink
        {
            public int Calls;
            public bool TryApplyWorldFrame(AnchorPoseFrame frame) { ++Calls; return true; }
            public void ClearWorldAnchor(string anchorId) { }
        }
    }
}
#endif
