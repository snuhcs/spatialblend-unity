// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SpatialBlendObjectContractTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        private T Create<T>(string name)
            where T : Component
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go.AddComponent<T>();
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = _created.Count - 1; index >= 0; --index)
                Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void InvalidHierarchyIsRejectedWithoutNativeInitialization()
        {
            var world = Create<SpatialBlendLocalWorld>("world");
            var other = Create<SpatialBlendLocalWorld>("other-world");
            var parent = Create<SpatialBlendObject>("parent");
            parent.Configure(other, "local", "parent", kind: SpatialBlendObjectKind.Region);
            var child = Create<SpatialBlendObject>("child");
            Assert.That(child.TryConfigure(world, "local", "child", parent,
                                           SpatialBlendObjectKind.Object, out _),
                        Is.False);
            Assert.That(child.TryConfigure(world, "local", "child", child,
                                           SpatialBlendObjectKind.Region, out _),
                        Is.False);
            Assert.That(child.TryConfigure(world, "local", "bad\0id", null,
                                           SpatialBlendObjectKind.Object, out _),
                        Is.False);
            Assert.That(world.IsInitialized, Is.False);
            Assert.That(other.IsInitialized, Is.False);
        }

        [Test]
        public void NestedRegionsAndStandaloneObjectsHaveExplicitKinds()
        {
            var world = Create<SpatialBlendLocalWorld>("world");
            var outer = Create<SpatialBlendObject>("outer");
            var inner = Create<SpatialBlendObject>("inner");
            var chair = Create<SpatialBlendObject>("chair");
            outer.Configure(world, "room", "outer", kind: SpatialBlendObjectKind.Region);
            inner.Configure(world, "room", "inner", outer, SpatialBlendObjectKind.Region);
            chair.Configure(world, "room", "chair", inner);
            Assert.That(chair.Kind, Is.EqualTo(SpatialBlendObjectKind.Object));
            Assert.That(outer.TryConfigure(world, "room", "outer", inner,
                                           SpatialBlendObjectKind.Region, out _),
                        Is.False, "Ancestor cycles must fail before mutation.");
            Assert.That(world.IsInitialized, Is.False);
        }

        [Test]
        public void InvalidSupportNeverAllocatesAndDoesNotInheritMeshVertices()
        {
            var world = Create<SpatialBlendLocalWorld>("world");
            var chair = Create<SpatialBlendObject>("chair");
            var seat = Create<SpatialBlendObject>("seat");
            chair.Configure(world, "source", "chair");
            seat.Configure(world, "source", "seat", chair, SpatialBlendObjectKind.Affordance);
            Assert.That(seat.TryRegister(out _), Is.False);
            Assert.That(world.IsInitialized, Is.False);
            Assert.That(seat.TrySetSupportPolygon(new[] { Vector3.zero, new Vector3(1, 0, 1),
                                                          Vector3.right, Vector3.forward },
                                                  out _),
                        Is.False, "Self-crossing boundaries are not polygons.");
            Assert.That(seat.TrySetSupportPolygon(new[] { Vector3.zero, Vector3.right,
                                                          new Vector3(1, 1, 1), Vector3.forward },
                                                  out _),
                        Is.False, "Nonplanar supports are invalid.");
            Assert.That(
                seat.TrySetSupportPolygon(
                    new[] { Vector3.zero, Vector3.right, new Vector3(float.NaN, 0, 1) }, out _),
                Is.False);
        }

        [Test]
        public void ConfigurePreservesIndependentPlacementAndFailedCandidate()
        {
            var world = Create<SpatialBlendLocalWorld>("world");
            var value = Create<SpatialBlendObject>("chair");
            value.transform.SetPositionAndRotation(new Vector3(3, 4, 5),
                                                   Quaternion.Euler(0, 37, 0));
            value.Configure(world, "room", "chair");
            Vector3 before = value.transform.position;
            Quaternion rotation = value.transform.rotation;
            Assert.That(
                value.TryConfigure(world, "room", "", null, SpatialBlendObjectKind.Object, out _),
                Is.False);
            Assert.That(value.ObjectId, Is.EqualTo("chair"));
            Assert.That(value.transform.position, Is.EqualTo(before));
            Assert.That(value.transform.rotation, Is.EqualTo(rotation));
        }
    }

    // Deliberately NOT FreeformRestoration: these tests require the explicitly
    // supplied current dylib in tools/run-foundation-tests.sh.
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class SpatialBlendObjectNativeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        [SetUp]
        public void SetUp() => _world = Create<SpatialBlendLocalWorld>("native-local-world");

        [TearDown]
        public void TearDown()
        {
            _world.Shutdown();
            for (int index = _created.Count - 1; index >= 0; --index)
                Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        private T Create<T>(string name)
            where T : Component
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go.AddComponent<T>();
        }

        private SpatialBlendObject ObjectIn(string space, string id, SpatialBlendObject parent = null,
                                         SpatialBlendObjectKind kind = SpatialBlendObjectKind.Object)
        {
            var value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, parent, kind);
            return value;
        }

        private SpatialBlendObject Support(string space, string id, SpatialBlendObject parent)
        {
            var value = ObjectIn(space, id, parent, SpatialBlendObjectKind.Affordance);
            Assert.That(value.TrySetSupportPolygon(
                            new[] { new Vector3(-1, 0.4f, -0.5f), new Vector3(1, 0.4f, -0.5f),
                                    new Vector3(1, 0.4f, 0.5f), new Vector3(-1, 0.4f, 0.5f) },
                            out string error),
                        Is.True, error);
            return value;
        }

        [Test]
        public void PlainObjectRegistersRemovesAndReregistersWithoutMovingUnityTransform()
        {
            var chair = ObjectIn("source", "chair");
            chair.gameObject.AddComponent<BoxCollider>();
            chair.transform.SetPositionAndRotation(new Vector3(3, 0.5f, 7),
                                                   Quaternion.Euler(0, 37, 0));
            Assert.That(chair.TryRegister(out string error), Is.True, error);
            LocalObjectRegistration old = chair.Registration;
            Assert.That(old.NativeElement, Is.GreaterThan(0));
            Assert.That(chair.transform.position, Is.EqualTo(new Vector3(3, 0.5f, 7)));
            chair.Unregister();
            Assert.That(chair.IsRegistered, Is.False);
            Assert.That(old.IsLive, Is.False);
            Assert.That(SpatialBlendAPI.RemoveLocalElement(old.NativeSpace, old.NativeElement),
                        Is.False, "The public component must really remove its native element.");
            Assert.That(chair.TryRegister(out error), Is.True, error);
            LocalObjectRegistration replacement = chair.Registration;
            _world.Unregister(old);
            Assert.That(chair.Registration, Is.SameAs(replacement));
            Assert.That(chair.IsRegistered, Is.True);
        }

        [Test]
        public void FailedDuplicateAndStaleCleanupCannotRemoveOriginalOrReplacementClaim()
        {
            var first = ObjectIn("source", "same-id");
            var duplicate = ObjectIn("source", "same-id");
            Assert.That(first.TryRegister(out string error), Is.True, error);
            Assert.That(duplicate.TryRegister(out _), Is.False);
            duplicate.Unregister();
            Assert.That(duplicate.TryRegister(out _), Is.False);
            Assert.That(first.IsRegistered, Is.True);
            LocalObjectRegistration old = first.Registration;
            first.Unregister();
            Assert.That(duplicate.TryRegister(out error), Is.True, error);
            _world.Unregister(old);
            var third = ObjectIn("source", "same-id");
            Assert.That(third.TryRegister(out _), Is.False);
            Assert.That(duplicate.IsRegistered, Is.True);
        }

        [Test]
        public void AuthoredParentRemovalCascadesEvenWhenUnityTransformsAreIndependent()
        {
            var region = ObjectIn("source", "region", kind: SpatialBlendObjectKind.Region);
            var chair = ObjectIn("source", "chair", region);
            var seat = Support("source", "chair-seat", chair);
            region.transform.SetPositionAndRotation(new Vector3(4, 0, -3),
                                                    Quaternion.Euler(0, 27, 0));
            chair.transform.position = new Vector3(7, 0, 2);
            seat.transform.position = new Vector3(7, 0, 2);
            Assert.That(seat.transform.parent, Is.Null,
                        "Authored parents need not be Unity parents.");
            Assert.That(seat.TryRegister(out string error), Is.True, error);
            LocalObjectRegistration oldSeat = seat.Registration;
            region.Unregister();
            Assert.That(chair.IsRegistered, Is.False);
            Assert.That(seat.IsRegistered, Is.False);
            Assert.That(oldSeat.IsLive, Is.False);
            Assert.That(
                SpatialBlendAPI.RemoveLocalElement(oldSeat.NativeSpace, oldSeat.NativeElement),
                Is.False);
            Assert.That(seat.TryRegister(out error), Is.True, error);
            Assert.That(chair.transform.position, Is.EqualTo(new Vector3(7, 0, 2)));
        }

        [Test]
        public void SupportLeaseOwnsExactEndpointsAndIsReleasedBeforeEndpointRemoval()
        {
            var chair = ObjectIn("source", "chair");
            var seat = Support("source", "chair-seat", chair);
            var sofa = ObjectIn("target", "sofa");
            var sofaSeat = Support("target", "sofa-seat", sofa);
            Assert.That(seat.TryRegister(out string error), Is.True, error);
            Assert.That(sofaSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            seat, sofaSeat, out IDisposable oldLease, out error),
                        Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(seat, sofaSeat, out _, out _),
                        Is.False);
            sofa.Unregister();
            Assert.That(sofaSeat.IsRegistered, Is.False);
            Assert.That(sofaSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            seat, sofaSeat, out IDisposable newLease, out error),
                        Is.True, error);
            oldLease.Dispose();
            Assert.That(_world.TryRegisterSupportCorrespondence(seat, sofaSeat, out _, out _),
                        Is.False);
            newLease.Dispose();
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            seat, sofaSeat, out IDisposable finalLease, out error),
                        Is.True, error);
            finalLease.Dispose();
        }

        [Test]
        public void ShutdownInvalidatesAllObjectsAndOldLeaseCannotTouchNewGeneration()
        {
            var seat = Support("source", "seat", ObjectIn("source", "chair"));
            var target = Support("target", "sofa-support", ObjectIn("target", "sofa"));
            Assert.That(seat.TryRegister(out string error), Is.True, error);
            Assert.That(target.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            seat, target, out IDisposable oldLease, out error),
                        Is.True, error);
            _world.Shutdown();
            Assert.That(seat.IsRegistered, Is.False);
            Assert.That(target.IsRegistered, Is.False);
            Assert.That(seat.TryRegister(out error), Is.True, error);
            Assert.That(target.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            seat, target, out IDisposable newLease, out error),
                        Is.True, error);
            oldLease.Dispose();
            Assert.That(_world.TryRegisterSupportCorrespondence(seat, target, out _, out _),
                        Is.False);
            newLease.Dispose();
        }

        [Test]
        public void FailedConfigurationLeavesLiveRegistrationAndIndependentPlacementIntact()
        {
            var chair = ObjectIn("source", "chair");
            chair.transform.position = new Vector3(-2, 0.2f, 9);
            Assert.That(chair.TryRegister(out string error), Is.True, error);
            LocalObjectRegistration registration = chair.Registration;
            Assert.That(
                chair.TryConfigure(_world, "source", "", null, SpatialBlendObjectKind.Object, out _),
                Is.False);
            Assert.That(chair.Registration, Is.SameAs(registration));
            Assert.That(chair.IsRegistered, Is.True);
            Assert.That(chair.transform.position, Is.EqualTo(new Vector3(-2, 0.2f, 9)));
        }

        [Test]
        public void DuplicateDescendantClaimDoesNotAllocateItsUnregisteredAncestors()
        {
            var original = ObjectIn("source", "chair");
            Assert.That(original.TryRegister(out string error), Is.True, error);
            var newRegion = ObjectIn("source", "new-region", kind: SpatialBlendObjectKind.Region);
            var duplicate = ObjectIn("source", "chair", newRegion);
            Assert.That(duplicate.TryRegister(out _), Is.False);
            Assert.That(newRegion.IsRegistered, Is.False);
            Assert.That(newRegion.Registration, Is.Null);
            Assert.That(original.IsRegistered, Is.True);
            Assert.That(
                newRegion.TryRegister(out error), Is.True, error,
                "The failed graph must not have left an ancestor's native/managed claim behind.");
        }

        [Test]
        public void ReconfiguringLiveIdentityRequiresExplicitUnregistration()
        {
            var first = ObjectIn("source", "chair");
            var second = ObjectIn("source", "table");
            Assert.That(first.TryRegister(out string error), Is.True, error);
            Assert.That(second.TryRegister(out error), Is.True, error);
            LocalObjectRegistration before = first.Registration;
            Assert.That(first.TryConfigure(_world, "source", "table", null,
                                           SpatialBlendObjectKind.Object, out _),
                        Is.False);
            Assert.That(first.Registration, Is.SameAs(before));
            Assert.That(first.TryConfigure(_world, "source", "chair", null,
                                           SpatialBlendObjectKind.Object, out error),
                        Is.True, error);
            Assert.That(first.Registration, Is.SameAs(before),
                        "Same configuration is an idempotent no-op.");
        }

        [Test]
        public void MeshColliderGeometryOptOutPreservesPhysicsAndRequiresExplicitLifecycleChange()
        {
            var contentRoot = ObjectIn("source", "content-root");
            MeshCollider originalCollider = contentRoot.gameObject.AddComponent<MeshCollider>();
            Mesh originalMesh = CreateTriangleMesh();
            originalCollider.sharedMesh = originalMesh;
            originalCollider.enabled = true;
            try
            {
                Assert.That(contentRoot.ColliderGeometryEnabled, Is.True);
                Assert.That(contentRoot.TryRegister(out _), Is.False,
                            "Default collider admission must continue rejecting MeshCollider geometry.");
                Assert.That(contentRoot.IsRegistered, Is.False);

                Assert.That(contentRoot.TrySetColliderGeometryEnabled(false, out string error), Is.True,
                            error);
                Assert.That(contentRoot.ColliderGeometryEnabled, Is.False);
                Assert.That(contentRoot.TryRegister(out error), Is.True, error);
                LocalObjectRegistration registration = contentRoot.Registration;
                contentRoot.transform.SetPositionAndRotation(new Vector3(2f, .5f, -3f),
                                                              Quaternion.Euler(0f, 31f, 0f));
                Assert.That(contentRoot.TryRefresh(out error), Is.True, error);
                Assert.That(contentRoot.Registration, Is.SameAs(registration));
                Assert.That(contentRoot.GetComponent<MeshCollider>(), Is.SameAs(originalCollider));
                Assert.That(originalCollider.enabled, Is.True);
                Assert.That(originalCollider.sharedMesh, Is.SameAs(originalMesh));

                Assert.That(contentRoot.TrySetColliderGeometryEnabled(true, out _), Is.False,
                            "A live registration cannot silently change its native geometry policy.");
                Assert.That(contentRoot.ColliderGeometryEnabled, Is.False);
                Assert.That(contentRoot.Registration, Is.SameAs(registration));
                Assert.That(originalCollider.enabled, Is.True);
                Assert.That(originalCollider.sharedMesh, Is.SameAs(originalMesh));

                contentRoot.Unregister();
                Assert.That(contentRoot.TrySetColliderGeometryEnabled(true, out error), Is.True, error);
                Assert.That(contentRoot.ColliderGeometryEnabled, Is.True);
                Assert.That(contentRoot.TryRegister(out _), Is.False,
                            "Re-enabling collider geometry after unregister must restore default rejection.");
                Assert.That(contentRoot.IsRegistered, Is.False);
                Assert.That(contentRoot.GetComponent<MeshCollider>(), Is.SameAs(originalCollider));
                Assert.That(originalCollider.enabled, Is.True);
                Assert.That(originalCollider.sharedMesh, Is.SameAs(originalMesh));
            }
            finally
            {
                Object.DestroyImmediate(originalMesh);
            }
        }

        [Test]
        public void LiveMeshOptOutRejectsSerializedFalseToTruePolicyMutationBeforeRefreshWrites()
        {
            var contentRoot = ObjectIn("source", "mesh-content-root");
            MeshCollider originalCollider = contentRoot.gameObject.AddComponent<MeshCollider>();
            Mesh originalMesh = CreateTriangleMesh();
            originalCollider.sharedMesh = originalMesh;
            try
            {
                Assert.That(contentRoot.TrySetColliderGeometryEnabled(false, out string error), Is.True,
                            error);
                Assert.That(contentRoot.TryRegister(out error), Is.True, error);
                LocalObjectRegistration registration = contentRoot.Registration;
                SetSerializedColliderGeometryEnabled(contentRoot, true);
                contentRoot.transform.position = new Vector3(4f, .25f, -2f);

                Assert.That(contentRoot.ColliderGeometryEnabled, Is.False,
                            "A live object reports its admitted policy, not an Inspector candidate.");
                Assert.That(contentRoot.TryRefresh(out _), Is.False,
                            "A serialized policy drift must fail before the native transform write.");
                Assert.That(contentRoot.Registration, Is.SameAs(registration));
                Assert.That(contentRoot.IsRegistered, Is.True);
                Assert.That(contentRoot.TrySetColliderGeometryEnabled(true, out _), Is.False,
                            "The setter must compare against admitted false, not mutated candidate true.");
                Assert.That(ReadSerializedColliderGeometryEnabled(contentRoot), Is.True);
                Assert.That(contentRoot.TrySetColliderGeometryEnabled(false, out error), Is.True, error);
                Assert.That(contentRoot.TryRefresh(out error), Is.True, error);
                Assert.That(originalCollider.enabled, Is.True);
                Assert.That(originalCollider.sharedMesh, Is.SameAs(originalMesh));
            }
            finally
            {
                Object.DestroyImmediate(originalMesh);
            }
        }

        [Test]
        public void LivePrimitiveColliderRejectsSerializedTrueToFalsePolicyMutationBeforeRefreshWrites()
        {
            var contentRoot = ObjectIn("source", "primitive-content-root");
            BoxCollider originalCollider = contentRoot.gameObject.AddComponent<BoxCollider>();
            Assert.That(contentRoot.TryRegister(out string error), Is.True, error);
            LocalObjectRegistration registration = contentRoot.Registration;
            SetSerializedColliderGeometryEnabled(contentRoot, false);
            contentRoot.transform.position = new Vector3(-3f, .5f, 6f);

            Assert.That(contentRoot.ColliderGeometryEnabled, Is.True,
                        "A live object reports its admitted primitive-collider policy.");
            Assert.That(contentRoot.TryRefresh(out _), Is.False,
                        "A serialized policy drift must fail before the native transform write.");
            Assert.That(contentRoot.Registration, Is.SameAs(registration));
            Assert.That(contentRoot.IsRegistered, Is.True);
            Assert.That(contentRoot.TrySetColliderGeometryEnabled(false, out _), Is.False,
                        "The setter must compare against admitted true, not mutated candidate false.");
            Assert.That(ReadSerializedColliderGeometryEnabled(contentRoot), Is.False);
            Assert.That(contentRoot.TrySetColliderGeometryEnabled(true, out error), Is.True, error);
            Assert.That(contentRoot.TryRefresh(out error), Is.True, error);
            Assert.That(contentRoot.GetComponent<BoxCollider>(), Is.SameAs(originalCollider));
            Assert.That(originalCollider.enabled, Is.True);
        }

        [Test]
        public void LiveSessionCannotSwitchToDebugLibrary()
        {
            Assert.That(_world.TryInitialize(out string error), Is.True, error);
            Assert.Throws<InvalidOperationException>(() => SpatialBlendAPI.Initialize(true));
            Assert.That(_world.IsInitialized, Is.True);
            var chair = ObjectIn("source", "chair");
            Assert.That(chair.TryRegister(out error), Is.True, error);
        }

        private static Mesh CreateTriangleMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        private static void SetSerializedColliderGeometryEnabled(SpatialBlendObject value, bool enabled)
        {
            var serialized = new SerializedObject(value);
            SerializedProperty property = serialized.FindProperty("_colliderGeometryEnabled");
            Assert.That(property, Is.Not.Null);
            property.boolValue = enabled;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool ReadSerializedColliderGeometryEnabled(SpatialBlendObject value)
        {
            var serialized = new SerializedObject(value);
            SerializedProperty property = serialized.FindProperty("_colliderGeometryEnabled");
            Assert.That(property, Is.Not.Null);
            return property.boolValue;
        }
    }
}
#endif
