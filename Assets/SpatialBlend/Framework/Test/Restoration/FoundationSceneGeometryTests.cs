// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using SpatialBlend.Internal;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Analytic contract tests for the Foundation-owned observed-room seam. Native
    /// acceptance is intentionally covered by the serialized native fixture: these
    /// assertions prove the descriptor chart supplied to that fixture, rather than
    /// treating a managed fake as native geometry evidence.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationSceneGeometryTests
    {
        private GameObject _root;

        private sealed class OversizedScenePoints<T> : IReadOnlyList<T>
        {
            public int Count => SpatialBlendAPI.MaxSceneVertices + 1;
            public T this[int index] => throw new System.InvalidOperationException("Do not read oversized input.");
            public IEnumerator<T> GetEnumerator() => throw new System.InvalidOperationException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Test]
        public void OversizedSceneBuffersAreRejectedBeforeIterationOrNativeCall()
        {
            var surface = Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                SpatialBlendAPI.SetElementWorldSurface(0, 0,
                    new OversizedScenePoints<Vector3>(), SbGeometryRole.Surface));
            StringAssert.Contains("65536", surface.Message);
            var polygon = Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                SpatialBlendAPI.SetElementPolygon(0, 0, SbGeometryRole.Navigable,
                    new OversizedScenePoints<Vector2>()));
            StringAssert.Contains("65536", polygon.Message);
        }

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("foundation-scene-geometry");
        }

        [TearDown]
        public void TearDown()
        {
            SpatialBlendLocalWorld world = _root.GetComponent<SpatialBlendLocalWorld>();
            if (world != null)
            {
                Field("_session").SetValue(world, 0L);
                ((Dictionary<string, long>)Field("_spaces").GetValue(world)).Clear();
                ((System.Collections.IDictionary)Field("_nativeSpaceRoots").GetValue(world)).Clear();
            }
            Object.DestroyImmediate(_root);
            SpatialBlendLocalWorld.TestOnlySceneGeometryNativeOperations = null;
        }

        [Test]
        public void Descriptor_PreservesFullAffineChartAndMeasuredBoundary()
        {
            Matrix4x4 chart = Matrix4x4.TRS(new Vector3(3f, 2f, -4f),
                Quaternion.Euler(17f, 31f, -12f), new Vector3(1.5f, .75f, 2f));
            Vector2[] measured =
            {
                new Vector2(-.4f, -.2f), new Vector2(.6f, -.2f), new Vector2(.1f, .7f)
            };
            Assert.That(SceneGeometryDescriptor.TryCreate(71, chart,
                new Bounds(Vector3.zero, new Vector3(2f, 1f, 3f)), measured, null,
                out SceneGeometryDescriptor descriptor), Is.True);
            Assert.That(descriptor.LocalToWorld, Is.EqualTo(chart));
            Assert.That(descriptor.PlaneBoundaryIsRectApproximation, Is.False);
            CollectionAssert.AreEqual(measured, descriptor.PlaneBoundary);
        }

        [Test]
        public void ScopeAdmission_RejectsUninitializedOrShutdownWorldWithoutNativeWrites()
        {
            SpatialBlendLocalWorld world = _root.AddComponent<SpatialBlendLocalWorld>();
            Assert.That(world.TryCreateSceneGeometryScope("room", "observed-room", out _, out _), Is.False);
            world.Shutdown();
            Assert.That(world.TryCreateSceneGeometryScope("room", "observed-room", out _, out _), Is.False);
        }

        [Test]
        public void Descriptor_InvalidInputsDoNotProduceAKeyedGeometryWriteCandidate()
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(0, Matrix4x4.identity,
                new Bounds(Vector3.zero, Vector3.one), null, null, out _), Is.False);
            Assert.That(SceneGeometryDescriptor.TryCreate(72, Matrix4x4.identity,
                null, new[] { Vector2.zero, Vector2.right }, null, out _), Is.False);
        }

        [Test]
        public void PartialStage_ClaimsSameSpaceKeyUntilItsExactTreeRetries()
        {
            // prepopulated fake spaces lazily receive their one Foundation root
            // before the two scope roots; fail the first staged plane child.
            var native = new RecordingSceneGeometryNative { FailAddAttempt = 5 };
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-a", out LocalSceneGeometryScope a, out _), Is.True);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-b", out LocalSceneGeometryScope b, out _), Is.True);
            SceneGeometryDescriptor descriptor = Plane(301, Matrix4x4.TRS(new Vector3(2f, 1f, -3f),
                Quaternion.Euler(12f, 29f, -7f), new Vector3(1.5f, .8f, 2f)));

            Assert.That(a.UpsertLocalSceneGeometry(descriptor), Is.False,
                "The failed child allocation retains the parent native tree for retry.");
            int afterPartialStage = native.AddCalls;
            Assert.That(b.UpsertLocalSceneGeometry(descriptor), Is.False,
                "A second scope must not stage the same native-space source key beside that partial tree.");
            Assert.That(native.AddCalls, Is.EqualTo(afterPartialStage));

            native.FailAddAttempt = -1;
            Assert.That(a.UpsertLocalSceneGeometry(descriptor), Is.True);
            Assert.That(b.UpsertLocalSceneGeometry(descriptor), Is.False,
                "The completed replacement remains exclusively owned by its original scope.");
            Assert.That(a.RemoveLocalSceneGeometry(descriptor.SourceKey), Is.True);
            Assert.That(b.UpsertLocalSceneGeometry(descriptor), Is.True,
                "Ownership releases only after the partial/active tree has actually retired.");
        }

        [Test]
        public void ReplacementFailedDeleteAndRollback_RetainsOldActiveTreeAndKey()
        {
            var native = new RecordingSceneGeometryNative();
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-a", out LocalSceneGeometryScope a, out _), Is.True);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-b", out LocalSceneGeometryScope b, out _), Is.True);
            SceneGeometryDescriptor initial = Volume(302, Vector3.zero);
            SceneGeometryDescriptor replacement = Volume(302, new Vector3(4f, 0f, 0f));
            Assert.That(a.UpsertLocalSceneGeometry(initial), Is.True);
            Assert.That(a.TryGetNativeGeometry(302, out long oldParent, out _), Is.True);

            native.FailRemoval = true;
            Assert.That(a.UpsertLocalSceneGeometry(replacement), Is.False,
                "Both failed old-tree removal and staged rollback must preserve exact retry state.");
            Assert.That(a.TryGetNativeGeometry(302, out long retainedParent, out _), Is.True);
            Assert.That(retainedParent, Is.EqualTo(oldParent), "The previous active tree remains authoritative.");
            Assert.That(b.UpsertLocalSceneGeometry(replacement), Is.False,
                "The failed staged tree still reserves this same-space source key.");

            native.FailRemoval = false;
            Assert.That(a.UpsertLocalSceneGeometry(replacement), Is.True);
            Assert.That(a.TryGetNativeGeometry(302, out long newParent, out _), Is.True);
            Assert.That(newParent, Is.Not.EqualTo(oldParent));
        }

        [Test]
        public void PlaneStage_BakesMeasuredBoundaryThroughItsFullAffineChart()
        {
            var native = new RecordingSceneGeometryNative();
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-affine", out LocalSceneGeometryScope scope, out _), Is.True);
            Matrix4x4 chart = Matrix4x4.TRS(new Vector3(3f, 2f, -4f),
                Quaternion.Euler(17f, 31f, -12f), new Vector3(1.5f, .75f, 2f));
            SceneGeometryDescriptor descriptor = Plane(303, chart);

            Assert.That(scope.UpsertLocalSceneGeometry(descriptor), Is.True);

            // The baked 3D surface (rendering, scan baking) goes through the full affine chart. It is kept apart.
            Vector3[] observed = descriptor.BakedPlaneSurface;
            Assert.That(observed[0], Is.EqualTo(chart.MultiplyPoint3x4(new Vector3(-.4f, -.2f, 0f))));
            Assert.That(observed[1], Is.EqualTo(chart.MultiplyPoint3x4(new Vector3(.6f, -.2f, 0f))));
            Assert.That(observed[2], Is.EqualTo(chart.MultiplyPoint3x4(new Vector3(.1f, .7f, 0f))));

            // The registered footprint is a separate matter: no baked surface reaches the core, the element is at the app's
            // own transform (plus the plane-chart basis change) and the outline is its local polygon.
            Assert.That(native.WorldSurfaces, Is.Empty, "a footprint is never registered as a baked 3D surface");
            Assert.That(native.Polygons, Has.Count.EqualTo(1));
            Matrix4x4 registered = native.Transforms[native.Transforms.Count - 1];
            Assert.That(registered, Is.EqualTo(SceneGeometryChart.ElementTransform(chart)));
            Vector2[] polygon = native.Polygons[0];
            for (int i = 0; i < 3; ++i)
            {
                Vector2 outline = descriptor.PlaneBoundary[i];
                Vector2 local = polygon[2 - i]; // reversed, y negated
                Vector3 viaRegistration = registered.MultiplyPoint3x4(new Vector3(local.x, 0f, local.y));
                Assert.That(Vector3.Distance(viaRegistration, chart.MultiplyPoint3x4(new Vector3(outline.x, outline.y, 0f))),
                    Is.LessThan(1e-4f), "vertex " + i + ": the registration puts it where the app's chart did");
            }
        }

        [Test]
        public void PrepopulatedSpace_UsesOneIdentityRootAndRemovableScopeChildren()
        {
            var native = new RecordingSceneGeometryNative();
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-a", out LocalSceneGeometryScope a, out _), Is.True);
            Assert.That(world.TryCreateSceneGeometryScope("room", "scope-b", out LocalSceneGeometryScope b, out _), Is.True);

            Assert.That(native.AddParents, Has.Count.EqualTo(3));
            long spaceRoot = native.CreatedElements[0];
            Assert.That(native.AddParents[0], Is.Zero, "Only the Foundation space root is parentless.");
            Assert.That(native.AddParents[1], Is.EqualTo(spaceRoot));
            Assert.That(native.AddParents[2], Is.EqualTo(spaceRoot));
            long closedRoomRoot = a.RoomRoot;
            Assert.That(a.TryClose(out _), Is.True);
            Assert.That(native.RemovedElements, Does.Contain(closedRoomRoot),
                "Closing removes only its own scope child, never the Foundation space root.");
            Assert.That(native.RemovedElements.Contains(spaceRoot), Is.False);
        }

        [Test]
        public void FailedIdentityWrite_RetriesTheTrackedRootWithoutAllocatingAnotherParentlessElement()
        {
            var native = new RecordingSceneGeometryNative { FailTransformAttempt = 1 };
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out _, out _), Is.False);
            Assert.That(native.AddParents, Is.EqualTo(new[] { 0L }));

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out LocalSceneGeometryScope scope, out _), Is.True);
            Assert.That(native.AddParents, Is.EqualTo(new[] { 0L, native.CreatedElements[0] }),
                "A failed identity write retains its session-tracked root for an exact retry.");
            Assert.That(scope.TryClose(out _), Is.True);
        }

        [Test]
        public void FailedScopeSetupAndCleanup_RetriesItsExactPendingRoomRoot()
        {
            var native = new RecordingSceneGeometryNative { FailTagAttempt = 1, FailRemoval = true };
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out LocalSceneGeometryScope failed,
                out string error), Is.False);
            Assert.That(failed, Is.Null, "Failed setup must not expose a usable active scope.");
            Assert.That(error, Does.Contain("Injected scope-tag failure"));
            Assert.That(native.AddParents, Is.EqualTo(new[] { 0L, native.CreatedElements[0] }));
            long pendingRoomRoot = native.CreatedElements[1];

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out LocalSceneGeometryScope scope,
                out _), Is.True);
            Assert.That(scope.RoomRoot, Is.EqualTo(pendingRoomRoot));
            Assert.That(native.AddParents, Is.EqualTo(new[] { 0L, native.CreatedElements[0] }),
                "Retry must configure the retained child rather than allocate a replacement root.");

            native.FailTagAttempt = native.TagCalls + 1;
            Assert.That(world.TryCreateSceneGeometryScope("room", "pending-shutdown", out _, out _), Is.False);
            Assert.That(((System.Collections.IDictionary)Field("_pendingSceneGeometryScopes").GetValue(world)).Count,
                Is.EqualTo(1), "Failed cleanup retains the unconfigured room root until shutdown.");
            Field("_shuttingDown").SetValue(world, true);
            Method("RetireSceneGeometryScopesForShutdown").Invoke(world, null);
            Assert.That(scope.IsLive, Is.False,
                "Shutdown retirement makes live scope ownership unusable before session teardown.");
            Assert.That(((System.Collections.IDictionary)Field("_pendingSceneGeometryScopes").GetValue(world)).Count,
                Is.Zero, "Shutdown retirement drops pending managed ownership only as the session is being retired.");
        }

        [Test]
        public void ThrowingRootAllocation_IsReportedAndThePrepopulatedSpaceCanRetry()
        {
            var native = new RecordingSceneGeometryNative { ThrowAddAttempt = 1 };
            SpatialBlendLocalWorld world = CreateInitializedWorld(native);

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out _, out string error), Is.False);
            Assert.That(error, Does.Contain("Injected root-allocation failure"));

            Assert.That(world.TryCreateSceneGeometryScope("room", "scope", out LocalSceneGeometryScope scope, out _), Is.True);
            Assert.That(native.AddParents, Is.EqualTo(new[] { 0L, 0L, native.CreatedElements[0] }),
                "The failed allocation was not retained as a fake root; retry creates one root then its child.");
            Assert.That(scope.TryClose(out _), Is.True);
        }

        private SpatialBlendLocalWorld CreateInitializedWorld(RecordingSceneGeometryNative native)
        {
            SpatialBlendLocalWorld.TestOnlySceneGeometryNativeOperations = native;
            SpatialBlendLocalWorld world = _root.AddComponent<SpatialBlendLocalWorld>();
            Field("_mainThread").SetValue(world, System.Threading.Thread.CurrentThread.ManagedThreadId);
            Field("_session").SetValue(world, 1L);
            Field("_generation").SetValue(world, 1);
            var spaces = (Dictionary<string, long>)Field("_spaces").GetValue(world);
            spaces.Add("room", 41L);
            return world;
        }

        private static SceneGeometryDescriptor Volume(int key, Vector3 position)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(key, Matrix4x4.Translate(position),
                new Bounds(Vector3.zero, Vector3.one), null, null, out SceneGeometryDescriptor descriptor), Is.True);
            return descriptor;
        }

        private static SceneGeometryDescriptor Plane(int key, Matrix4x4 chart)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(key, chart, null, new[]
            {
                new Vector2(-.4f, -.2f), new Vector2(.6f, -.2f), new Vector2(.1f, .7f)
            }, null, out SceneGeometryDescriptor descriptor), Is.True);
            return descriptor;
        }

        private static FieldInfo Field(string name) => typeof(SpatialBlendLocalWorld).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static MethodInfo Method(string name) => typeof(SpatialBlendLocalWorld).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class RecordingSceneGeometryNative : ISceneGeometryNativeOperations
        {
            private long _next = 100;
            public int AddCalls;
            public int FailAddAttempt = -1;
            public int ThrowAddAttempt = -1;
            public int TransformCalls;
            public int FailTransformAttempt = -1;
            public int TagCalls;
            public int FailTagAttempt = -1;
            public bool FailRemoval;
            public readonly List<long> AddParents = new List<long>();
            public readonly List<long> CreatedElements = new List<long>();
            public readonly List<long> RemovedElements = new List<long>();
            public readonly List<IReadOnlyList<Vector3>> WorldSurfaces = new List<IReadOnlyList<Vector3>>();
            public readonly List<Vector2[]> Polygons = new List<Vector2[]>();
            public readonly List<Matrix4x4> Transforms = new List<Matrix4x4>();
            public void SetElementPolygon(long space, long element, SbGeometryRole role, IReadOnlyList<Vector2> points) =>
                Polygons.Add(new List<Vector2>(points).ToArray());

            public long AddElement(long space, long parent)
            {
                ++AddCalls;
                AddParents.Add(parent);
                if (AddCalls == ThrowAddAttempt)
                    throw new System.InvalidOperationException("Injected root-allocation failure.");
                if (AddCalls == FailAddAttempt) return 0;
                long created = _next++;
                CreatedElements.Add(created);
                return created;
            }
            public void SetElementTag(long space, long element, string tag)
            {
                ++TagCalls;
                if (TagCalls == FailTagAttempt)
                    throw new System.InvalidOperationException("Injected scope-tag failure.");
            }
            public void SetElementTransform(long space, long element, Matrix4x4 transform)
            {
                ++TransformCalls;
                Transforms.Add(transform);
                if (TransformCalls == FailTransformAttempt)
                    throw new System.InvalidOperationException("Injected identity-chart failure.");
            }
            public void SetElementBox(long space, long element, SbGeometryRole role, Vector3 min, Vector3 max) { }
            public void SetElementWorldSurface(long space, long element, SbGeometryRole role,
                                               IReadOnlyList<Vector3> worldVertices)
            {
                WorldSurfaces.Add(new List<Vector3>(worldVertices));
            }
            public bool RemoveLocalElement(long space, long element)
            {
                RemovedElements.Add(element);
                return !FailRemoval;
            }
        }
    }
}
#endif
