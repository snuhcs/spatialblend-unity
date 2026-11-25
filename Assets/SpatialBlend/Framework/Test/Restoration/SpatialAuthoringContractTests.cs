// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SpatialAuthoringContractTests
    {
        [Test]
        public void DeclarationMapsOntoRegionObjectAffordanceAndSubRegion()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            Assert.That(plan.Mappings, Has.Length.EqualTo(3));

            SpatialElementMapping region = plan.Mappings[0];
            Assert.That(region.id, Is.EqualTo("ChessArea"));
            Assert.That(region.kind, Is.EqualTo(SpatialHierarchyNodeKind.Region));
            Assert.That(region.parentId, Is.EqualTo(SpatialAuthoringCompiler.RoomParentId));
            Assert.That(region.role, Is.EqualTo(SpatialElementRole.Containment));
            Assert.That(region.tag, Is.EqualTo("ChessArea"));

            SpatialElementMapping board = plan.Mappings[1];
            Assert.That(board.id, Is.EqualTo("Chess"));
            Assert.That(board.kind, Is.EqualTo(SpatialHierarchyNodeKind.Object));
            Assert.That(board.parentId, Is.EqualTo("ChessArea"));
            Assert.That(board.role, Is.EqualTo(SpatialElementRole.ColliderEnvelope));
            Assert.That(board.tag, Is.EqualTo("Chess"));
            AssertPolygon(board.polygon, Footprint());

            SpatialElementMapping playable = plan.Mappings[2];
            Assert.That(playable.kind, Is.EqualTo(SpatialHierarchyNodeKind.Affordance));
            Assert.That(playable.parentId, Is.EqualTo("Chess"));
            Assert.That(playable.role, Is.EqualTo(SpatialElementRole.Surface));
            Assert.That(playable.tag, Is.EqualTo("Playable"));
            Assert.That(playable.subRegions, Has.Length.EqualTo(1));
            Assert.That(playable.subRegions[0].name, Is.EqualTo("Face"));
            Assert.That(playable.subRegions[0].tag, Is.EqualTo("Playable"));
        }

        [Test]
        public void FallbackMarginIsHalfAMetreAroundTheFootprintWhenNoExtentIsDeclared()
        {
            Assert.That(SpatialAuthoringCompiler.DefaultRegionMarginMeters, Is.EqualTo(0.5f));
            Assert.That(SbSpatialAuthoring.DefaultRegionMarginMeters, Is.EqualTo(0.5f));
            SpatialAuthoringDeclaration declaration = ChessDeclaration();
            declaration.declaredExtent = null;
            Assert.That(declaration.fallbackMarginMeters, Is.EqualTo(0.5f));
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            Assert.That(plan.FallbackMarginMeters, Is.EqualTo(0.5f));
            Assert.That(plan.AppliedMarginMeters, Is.EqualTo(0.5f));
            Assert.That(plan.ExtentIsDeclared, Is.False);
            Assert.That(plan.ExtentSource, Is.EqualTo(SpatialExtentSource.FootprintMargin));
            Vector2[] expanded = SpatialAuthoringCompiler.ExpandConvex(Footprint(), 0.5f);
            SpatialElementMapping region = plan.Mappings[0];
            SpatialElementMapping board = plan.Mappings[1];
            AssertPolygon(region.polygon, expanded);
            AssertPolygon(board.polygon, Footprint());
            Assert.That(Contains(board.polygon, new Vector2(0.75f, 0f)), Is.False);
            Assert.That(Contains(region.polygon, new Vector2(0.75f, 0f)), Is.True);
        }

        [Test]
        public void SourceAndReceiverWriteTheSameLocalGeometry()
        {
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(2f, 0f, 3f), Quaternion.identity, Vector3.one);
            var source = new GameObject("source-authoring");
            var receiver = new GameObject("receiver-authoring");
            var sourceManager = source.AddComponent<RecordingAuthoringManager>();
            var receiverManager = receiver.AddComponent<RecordingAuthoringManager>();
            try
            {
                sourceManager.ConfigureConnected(10, 1);
                receiverManager.ConfigureConnected(20, 1);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                long sharedId = 1001;
                Assert.That(sourceManager.TryApplySpatialAuthoring(plan, sharedId, 0, world, null, out SpatialAuthoringBinding sourceBinding),
                    Is.True);
                receiverManager.Seed(SpatialAuthoringCompiler.RegionSharedId(sharedId), 77);
                Assert.That(receiverManager.TryApplySpatialAuthoring(plan, sharedId, 500, world, null, out SpatialAuthoringBinding receiverBinding),
                    Is.True);

                Assert.That(sourceManager.Parents, Is.EqualTo(new[] { 1L, sourceBinding.RegionElement, sourceBinding.ObjectElement }));
                Assert.That(sourceBinding.RegionElement, Is.Not.EqualTo(1L));
                Assert.That(receiverManager.AddedElements, Is.EqualTo(new[] { receiverManager.AffordanceElement }));
                Assert.That(receiverManager.Parents, Is.EqualTo(new[] { 500L }));
                Assert.That(receiverBinding.RegionElement, Is.EqualTo(77L));
                Assert.That(receiverBinding.ObjectElement, Is.EqualTo(500L));
                AssertPolygon(receiverManager.Boundaries[77], sourceManager.Boundaries[sourceBinding.RegionElement]);
                AssertPolygon(receiverManager.Footprints[500], sourceManager.Footprints[sourceBinding.ObjectElement]);
                Assert.That(receiverManager.Tags[77], Is.EqualTo("ChessArea"));
                Assert.That(receiverManager.Tags[500], Is.EqualTo("Chess"));
                Assert.That(receiverManager.Tags[receiverManager.AffordanceElement], Is.EqualTo("Playable"));
                Assert.That(receiverManager.SubRegionTags, Is.EqualTo(new[] { "Playable" }));
                AssertSurface(sourceManager.Supports[0], receiverManager.Supports[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(receiver);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void ScopedBindingUsesTheDeclaredRegionParent()
        {
            var go = new GameObject("scoped-authoring");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(30, 2);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                Assert.That(manager.TryApplySpatialAuthoring(plan, 1001, 0, Matrix4x4.identity, null, out SpatialAuthoringBinding binding),
                    Is.True);
                Assert.That(manager.TryGetAuthoredRegion(binding.ObjectElement, out long region), Is.True);
                Assert.That(region, Is.EqualTo(binding.RegionElement));
                Assert.That(plan.Mappings[1].parentId, Is.EqualTo(plan.RegionId));

                // Scope belongs to the relation family; only AffordancePose keeps its tag.
                RelationBinding tagged = SpatialAuthoringCompiler.PlanRelation(RelationKind.AffordancePose, "root", "Playable");
                Assert.That(tagged.Tag, Is.EqualTo("Playable"));

                RelationBinding spacing = SpatialAuthoringCompiler.PlanRelation(RelationKind.Spacing, "root", "Playable");
                Assert.That(spacing.Tag, Is.Null);
                Assert.That(spacing.Anchor, Is.EqualTo("root"));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void LegacyFacingBindsOnTheHeadTheReceivedAvatarSamples()
        {
            Assert.That(SpatialAuthoringCompiler.TryPlanLegacyInteraction(ProximityType.Angular1D, out RelationBinding facing), Is.True);
            Assert.That(facing.Kind, Is.EqualTo(RelationKind.Facing));
            Assert.That(facing.Anchor, Is.EqualTo("head"));
            Assert.That(SpatialAuthoringCompiler.TryPlanLegacyInteraction(ProximityType.Angular2D, out RelationBinding facing2), Is.True);
            Assert.That(facing2.Anchor, Is.EqualTo("head"));
            Assert.That(SpatialAuthoringCompiler.TryPlanLegacyInteraction(ProximityType.Euclidean2D, out RelationBinding spacing), Is.True);
            Assert.That(spacing.Kind, Is.EqualTo(RelationKind.Spacing));
            Assert.That(spacing.Anchor, Is.EqualTo("root"));
        }

        [Test]
        public void ExistingObjectElementIsNotAllocatedAgain()
        {
            var go = new GameObject("existing-element");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(40, 3);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                SpatialHierarchyNode[] nodes = plan.Descriptor.nodes;
                SpatialHierarchyNode board = Array.Find(nodes, node => node.kind == SpatialHierarchyNodeKind.Object);
                board.existingElementId = 42;
                Assert.That(manager.RegisterLocalSpatialHierarchy(plan.Descriptor, out SpatialHierarchyRegistration registration), Is.True);
                Assert.That(registration.TryGetElement("Chess", out long chess), Is.True);
                Assert.That(chess, Is.EqualTo(42L));
                Assert.That(manager.AddedElements, Has.No.Member(42L));
                Assert.That(manager.Footprints.ContainsKey(42), Is.True);
                Assert.That(manager.Boundaries.Count, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // --- The declared extent is the region; the margin is the fallback.

        [Test]
        public void DeclaredExtentIsTheRegionAndTheMarginIsIgnored()
        {
            SpatialAuthoringDeclaration declaration = ChessDeclaration();
            declaration.fallbackMarginMeters = 3f;
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            Assert.That(plan.ExtentIsDeclared, Is.True);
            Assert.That(plan.ExtentSource, Is.EqualTo(SpatialExtentSource.Declared));
            Assert.That(plan.AppliedMarginMeters, Is.EqualTo(0f));
            Assert.That(plan.FallbackMarginMeters, Is.EqualTo(3f));
            AssertPolygon(plan.Mappings[0].polygon, declaration.declaredExtent);

            declaration.declaredExtent = null;
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan fallback, out error),
                Is.True, error);
            Assert.That(fallback.ExtentIsDeclared, Is.False);
            Assert.That(fallback.AppliedMarginMeters, Is.EqualTo(3f));
            AssertPolygon(fallback.Mappings[0].polygon, SpatialAuthoringCompiler.ExpandConvex(Footprint(), 3f));
        }

        [Test]
        public void DeclaredExtentMustContainTheFootprintAndEverySurface()
        {
            SpatialAuthoringDeclaration smallExtent = ChessDeclaration();
            smallExtent.declaredExtent = new[]
            {
                new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f),
                new Vector2(0.2f, 0.2f), new Vector2(-0.2f, 0.2f),
            };
            Assert.That(SpatialAuthoringCompiler.TryCompile(smallExtent, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("contain"));

            // The footprint fits, one affordance vertex does not.
            SpatialAuthoringDeclaration wideSurface = ChessDeclaration();
            wideSurface.footprint = new[]
            {
                new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f),
                new Vector2(0.2f, 0.2f), new Vector2(-0.2f, 0.2f),
            };
            wideSurface.declaredExtent = new[]
            {
                new Vector2(-0.3f, -0.3f), new Vector2(0.3f, -0.3f),
                new Vector2(0.3f, 0.3f), new Vector2(-0.3f, 0.3f),
            };
            Assert.That(SpatialAuthoringCompiler.TryCompile(wideSurface, out _, out error), Is.False);
            Assert.That(error, Does.Contain("surface"));

            // An extent that only touches the footprint boundary is accepted.
            SpatialAuthoringDeclaration touching = ChessDeclaration();
            touching.declaredExtent = Footprint();
            touching.affordances = Array.Empty<SpatialAffordanceDeclaration>();
            Assert.That(SpatialAuthoringCompiler.TryCompile(touching, out _, out error), Is.True, error);
        }

        [Test]
        public void ComponentCarriesTheDeclaredExtentAndFallsBackToItsMargin()
        {
            var go = new GameObject("component-extent");
            try
            {
                var authoring = go.AddComponent<SbSpatialAuthoring>();
                authoring.footprintPolygon = Footprint();
                authoring.fallbackMarginMeters = 0.15f;
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                    Is.True, error);
                Assert.That(declaration.declaredExtent, Is.Null.Or.Empty);
                Assert.That(declaration.fallbackMarginMeters, Is.EqualTo(0.15f));
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                    Is.True, error);
                Assert.That(plan.ExtentIsDeclared, Is.False);
                AssertPolygon(plan.Mappings[0].polygon, SpatialAuthoringCompiler.ExpandConvex(Footprint(), 0.15f));

                authoring.declaredExtent = ChessDeclaration().declaredExtent;
                Assert.That(authoring.TryBuildDeclaration(out declaration, out error), Is.True, error);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out plan, out error), Is.True, error);
                Assert.That(plan.ExtentIsDeclared, Is.True);
                AssertPolygon(plan.Mappings[0].polygon, ChessDeclaration().declaredExtent);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // --- The three things the core needs for an object-relative pose (AffordancePose A4).

        [Test]
        public void SourceAndReceiverPlansCarryIdenticalLocalGeometry()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan source, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan receiver, out error),
                Is.True, error);
            Assert.That(source.LocalGeometrySignature, Is.Not.Empty);
            Assert.That(receiver.LocalGeometrySignature, Is.EqualTo(source.LocalGeometrySignature));
            Assert.That(source.PosePreserving, Is.True);

            SpatialAuthoringDeclaration moved = ChessDeclaration();
            moved.affordances[0].surface[2].x += 0.01f;
            Assert.That(SpatialAuthoringCompiler.TryCompile(moved, out SpatialAuthoringPlan different, out error),
                Is.True, error);
            Assert.That(different.LocalGeometrySignature, Is.Not.EqualTo(source.LocalGeometrySignature));
        }

        [Test]
        public void MappedSupportIsTheIdentityInTheObjectFrameWhateverTheCopyPose()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            Vector3[] local = plan.Mappings[2].surface;
            foreach (Matrix4x4 world in CopyPoses())
            {
                Vector3[] baked = SpatialAuthoringCompiler.BakeSurface(local, world);
                Matrix4x4 toObject = world.inverse;
                for (int index = 0; index < local.Length; ++index)
                {
                    Assert.That((toObject.MultiplyPoint3x4(baked[index]) - local[index]).magnitude, Is.LessThan(1e-4f));
                }
            }
        }

        [Test]
        public void SurfaceFrameYawTurnsWithTheObjectYaw()
        {
            Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                Is.True, error);
            Vector3[] local = plan.Mappings[2].surface;
            float localHeading = SpatialAuthoringCompiler.SurfaceFrameHeadingDegrees(local);
            Assert.That(localHeading, Is.Not.NaN);
            foreach (float yaw in new[] { 0f, 90f, 200f })
            {
                Matrix4x4 world = Matrix4x4.TRS(new Vector3(3f, 0f, -2f), Quaternion.Euler(0f, yaw, 0f), Vector3.one);
                float heading = SpatialAuthoringCompiler.SurfaceFrameHeadingDegrees(
                    SpatialAuthoringCompiler.BakeSurface(local, world));
                Assert.That(Mathf.DeltaAngle(localHeading + yaw, heading), Is.EqualTo(0f).Within(1e-2f), "yaw " + yaw);
            }
        }

        [Test]
        public void ColliderDerivedGeometryDoesNotDependOnTheCopyPose()
        {
            Vector2[] reference = null;
            Vector3[] referenceSurface = null;
            foreach (Matrix4x4 world in CopyPoses())
            {
                var go = new GameObject("collider-pose");
                try
                {
                    go.transform.SetPositionAndRotation(world.GetPosition(), world.rotation);
                    var sphere = go.AddComponent<SphereCollider>();
                    sphere.center = new Vector3(0.1f, 0.2f, 0f);
                    sphere.radius = 0.3f;
                    var authoring = go.AddComponent<SbSpatialAuthoring>();
                    authoring.footprint = sphere;
                    authoring.affordances = new[]
                    {
                        new AuthoredAffordanceSurface { id = "Top", tag = "Placeable", supportId = "top", surface = sphere },
                    };
                    Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                        Is.True, error);
                    if (reference == null)
                    {
                        reference = declaration.footprint;
                        referenceSurface = declaration.affordances[0].surface;
                        AssertPolygon(reference, new[]
                        {
                            new Vector2(-0.2f, -0.3f), new Vector2(0.4f, -0.3f),
                            new Vector2(0.4f, 0.3f), new Vector2(-0.2f, 0.3f),
                        });
                        continue;
                    }
                    AssertPolygon(declaration.footprint, reference);
                    AssertSurface(declaration.affordances[0].surface, referenceSurface);
                }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            }
        }

        [Test]
        public void SurfaceIsBakedAgainWhenTheCopyMoves()
        {
            var go = new GameObject("rebake-authoring");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(60, 1);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                Matrix4x4 first = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
                Matrix4x4 second = Matrix4x4.TRS(new Vector3(-4f, 0f, 1f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);
                Assert.That(manager.TryApplySpatialAuthoring(plan, 1001, 0, first, null, out SpatialAuthoringBinding binding), Is.True);
                Assert.That(manager.Supports.Count, Is.EqualTo(1));

                Assert.That(manager.RefreshAuthoredSupports(binding.ObjectElement, first), Is.False, "An unmoved copy is not baked again.");
                Assert.That(manager.Supports.Count, Is.EqualTo(1));
                Assert.That(manager.RefreshAuthoredSupports(binding.ObjectElement, second), Is.True);
                Assert.That(manager.Supports.Count, Is.EqualTo(2));
                AssertSurface(manager.Supports[1], SpatialAuthoringCompiler.BakeSurface(plan.Mappings[2].surface, second));
                Assert.That(manager.RefreshAuthoredSupports(binding.ObjectElement, second), Is.False);
                Assert.That(manager.RefreshAuthoredSupports(9999, second), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // --- Gaps T3.0 reported.

        [Test]
        public void FootprintPolygonWithoutColliderIsWrittenAndAColliderTakesPrecedence()
        {
            var go = new GameObject("footprint-authoring");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            var colliderObject = new GameObject("footprint-collider");
            try
            {
                manager.ConfigureConnected(70, 1);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                Assert.That(manager.TryApplySpatialAuthoring(plan, 1001, 0, Matrix4x4.identity, null, out SpatialAuthoringBinding polygonOnly), Is.True);
                AssertPolygon(manager.Footprints[polygonOnly.ObjectElement], Footprint());
                Assert.That(manager.FootprintColliders, Is.Empty);

                Collider collider = colliderObject.AddComponent<BoxCollider>();
                Assert.That(manager.TryApplySpatialAuthoring(plan, 1002, 0, Matrix4x4.identity, collider, out SpatialAuthoringBinding withCollider), Is.True);
                Assert.That(manager.FootprintColliders, Is.EqualTo(new[] { withCollider.ObjectElement }));
                Assert.That(manager.Footprints.ContainsKey(withCollider.ObjectElement), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(colliderObject);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void LinkedObjectKeepsItsRegionAndTheSourceRegionIsResolvedOntoIt()
        {
            var go = new GameObject("linked-authoring");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(80, 1);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                Matrix4x4 world = Matrix4x4.TRS(new Vector3(2f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);

                // The receiver registered its own object, with its own local id, before the link.
                const long localSharedId = 2001;
                const long sourceSharedId = 1001;
                Assert.That(manager.TryApplySpatialAuthoring(plan, localSharedId, 0, world, null, out SpatialAuthoringBinding local), Is.True);
                int addedBeforeLink = manager.AddedElements.Count;
                long affordance = manager.AffordanceElement;

                // The source's region arrived as a received element carrying the source's shared id.
                manager.Seed(SpatialAuthoringCompiler.RegionSharedId(sourceSharedId), 77);

                Assert.That(manager.TryLinkSpatialAuthoring(local.ObjectElement, sourceSharedId, world, out SpatialAuthoringBinding linked), Is.True);
                Assert.That(manager.AddedElements.Count, Is.EqualTo(addedBeforeLink), "No sibling region is made.");
                Assert.That(linked.RegionElement, Is.EqualTo(local.RegionElement));
                Assert.That(manager.TryGetAuthoredRegion(local.ObjectElement, out long region), Is.True);
                Assert.That(region, Is.EqualTo(local.RegionElement));
                Assert.That(manager.Resolved, Is.EqualTo(new[] { new KeyValuePair<long, long>(77, local.RegionElement) }));
                Assert.That(manager.SharedIds[local.RegionElement], Is.EqualTo(SpatialAuthoringCompiler.RegionSharedId(sourceSharedId)));
                Assert.That(manager.SharedIds[affordance], Is.EqualTo(SpatialAuthoringCompiler.AffordanceSharedId(sourceSharedId, 0)));
                Assert.That(manager.Transforms[local.ObjectElement], Is.EqualTo(Matrix4x4.identity), "The object stays the identity under its region.");
                Assert.That(manager.Transforms[local.RegionElement], Is.EqualTo(world));
                AssertPolygon(manager.Boundaries[local.RegionElement], plan.Mappings[0].polygon);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void LinkedObjectWithoutAnAuthoredRegionIsNotGivenASiblingRegion()
        {
            var go = new GameObject("unauthored-link");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(90, 1);
                Assert.That(manager.TryLinkSpatialAuthoring(500, 1001, Matrix4x4.identity, out _), Is.False);
                Assert.That(manager.AddedElements, Is.Empty);
                Assert.That(manager.Resolved, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ReceivedObjectWithoutASharedRegionIsAuthoredWithoutASiblingRegion()
        {
            var go = new GameObject("no-region-arrival");
            var manager = go.AddComponent<RecordingAuthoringManager>();
            try
            {
                manager.ConfigureConnected(95, 1);
                Assert.That(SpatialAuthoringCompiler.TryCompile(ChessDeclaration(), out SpatialAuthoringPlan plan, out string error),
                    Is.True, error);
                Assert.That(manager.TryApplySpatialAuthoring(plan, 1001, 500, Matrix4x4.identity, null, out SpatialAuthoringBinding binding), Is.True);
                Assert.That(binding.RegionElement, Is.EqualTo(0L));
                Assert.That(binding.ObjectElement, Is.EqualTo(500L));
                Assert.That(manager.Boundaries, Is.Empty);
                Assert.That(manager.Parents, Is.EqualTo(new[] { 500L }), "Only the affordance is created, under the received object.");
                AssertPolygon(manager.Footprints[500], Footprint());
                Assert.That(manager.TryGetAuthoredRegion(500, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        private static IEnumerable<Matrix4x4> CopyPoses()
        {
            foreach (Vector3 position in new[] { Vector3.zero, new Vector3(2f, 0f, 3f), new Vector3(-5f, 0.2f, 1.5f), new Vector3(0.7f, 0f, -6f) })
            {
                foreach (float yaw in new[] { 0f, 90f, 200f })
                {
                    yield return Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw, 0f), Vector3.one);
                }
            }
        }

        private static SpatialAuthoringDeclaration ChessDeclaration()
        {
            return new SpatialAuthoringDeclaration
            {
                objectId = "Chess",
                regionId = "ChessArea",
                footprint = Footprint(),
                fallbackMarginMeters = SpatialAuthoringCompiler.DefaultRegionMarginMeters,
                declaredExtent = new[]
                {
                    new Vector2(-0.5f, -0.9f), new Vector2(0.5f, -0.9f),
                    new Vector2(0.5f, 0.9f), new Vector2(-0.5f, 0.9f),
                },
                correspondenceKey = "chess.board",
                affordances = new[]
                {
                    new SpatialAffordanceDeclaration
                    {
                        id = "ChessTop",
                        tag = "Playable",
                        supportId = "chess.top",
                        surface = new[]
                        {
                            new Vector3(-0.4f, 0.05f, -0.4f), new Vector3(0.4f, 0.05f, -0.4f),
                            new Vector3(0.4f, 0.05f, 0.4f), new Vector3(-0.4f, 0.05f, 0.4f),
                        },
                        subRegions = new[]
                        {
                            new SpatialSubRegion
                            {
                                name = "Face",
                                tag = "Playable",
                                localPolygon = new[]
                                {
                                    new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f),
                                    new Vector2(0.2f, 0.2f), new Vector2(-0.2f, 0.2f),
                                },
                            },
                        },
                    },
                },
            };
        }

        private static Vector2[] Footprint() => new[]
        {
            new Vector2(-0.4f, -0.4f), new Vector2(0.4f, -0.4f),
            new Vector2(0.4f, 0.4f), new Vector2(-0.4f, 0.4f),
        };

        private static void AssertPolygon(Vector2[] actual, Vector2[] expected)
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(expected, Is.Not.Null);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int index = 0; index < expected.Length; ++index)
            {
                Assert.That(actual[index].x, Is.EqualTo(expected[index].x).Within(1e-4f));
                Assert.That(actual[index].y, Is.EqualTo(expected[index].y).Within(1e-4f));
            }
        }

        private static void AssertSurface(Vector3[] actual, Vector3[] expected)
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(expected, Is.Not.Null);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int index = 0; index < expected.Length; ++index)
            {
                Assert.That((actual[index] - expected[index]).sqrMagnitude, Is.LessThan(1e-8f));
            }
        }

        private static bool Contains(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
            {
                Vector2 a = polygon[previous];
                Vector2 b = polygon[index];
                if (((a.y > point.y) != (b.y > point.y)) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y + 0f) + a.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private sealed class RecordingAuthoringManager : SpatialBlendManager
        {
            public readonly List<long> Parents = new List<long>();
            public readonly List<long> AddedElements = new List<long>();
            public readonly Dictionary<long, Vector2[]> Boundaries = new Dictionary<long, Vector2[]>();
            public readonly Dictionary<long, Vector2[]> Footprints = new Dictionary<long, Vector2[]>();
            public readonly Dictionary<long, string> Tags = new Dictionary<long, string>();
            public readonly List<Vector3[]> Supports = new List<Vector3[]>();
            public readonly List<string> SubRegionTags = new List<string>();
            public readonly List<long> FootprintColliders = new List<long>();
            public readonly Dictionary<long, Matrix4x4> Transforms = new Dictionary<long, Matrix4x4>();
            public readonly Dictionary<long, long> SharedIds = new Dictionary<long, long>();
            public readonly List<KeyValuePair<long, long>> Resolved = new List<KeyValuePair<long, long>>();
            public long AffordanceElement;
            private readonly Dictionary<long, long> _bySharedId = new Dictionary<long, long>();
            private long _next = 10;

            public void ConfigureConnected(long space, long generation)
            {
                SetPrivate("_initialized", true);
                SetPrivate("_connectionState", SpatialBlendConnectionState.Connected);
                SetPrivate("_spaceID", space);
                SetPrivate("_lifecycleGeneration", generation);
            }

            public void Seed(long sharedId, long element) => _bySharedId[sharedId] = element;

            protected override void InitializeNativeLibrary() { }
            public override void Host(int port) { }
            protected override long EnsureSpatialHierarchyRoom(long space) => 1;
            protected override long AddSpatialHierarchyElement(long space, long parent)
            {
                long element = _next++;
                Parents.Add(parent);
                AddedElements.Add(element);
                AffordanceElement = element;
                return element;
            }
            protected override void SetSpatialHierarchyTransform(long space, long element, Matrix4x4 transform) =>
                Transforms[element] = transform;
            protected override void SetSpatialHierarchySupport(long space, long element, Vector3[] vertices) =>
                Supports.Add((Vector3[])vertices.Clone());
            protected override void SetSpatialHierarchyTag(long space, long element, string tag) => Tags[element] = tag;
            protected override bool SetSpatialHierarchyBoundary(long space, long element, Vector2[] polygon)
            {
                Boundaries[element] = (Vector2[])polygon.Clone();
                return true;
            }
            protected override void SetSpatialHierarchyFootprintPolygon(long space, long element, Vector2[] footprint) =>
                Footprints[element] = (Vector2[])footprint.Clone();
            protected override void SetSpatialHierarchyFootprintCollider(long space, long element, Collider collider) =>
                FootprintColliders.Add(element);
            protected override void ResolveSpatialHierarchyElement(long space, long received, long resolved) =>
                Resolved.Add(new KeyValuePair<long, long>(received, resolved));
            protected override void AddSpatialHierarchySubRegion(long space, long element, string name, string tag, Vector2[] polygon) =>
                SubRegionTags.Add(tag);
            protected override void SetSpatialHierarchySharedId(long space, long element, long sharedId)
            {
                _bySharedId[sharedId] = element;
                SharedIds[element] = sharedId;
            }
            protected override long FindSpatialHierarchyElement(long space, long sharedId) =>
                _bySharedId.TryGetValue(sharedId, out long element) ? element : 0;
            protected override bool RemoveSpatialHierarchyElement(long space, long element) => true;
            protected override void ShareSpatialIdentity(long space, long element) { }

            private void SetPrivate(string name, object value) => typeof(SpatialBlendManager)
                .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(this, value);
        }
    }
}
#endif
