// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationNative")]
    public sealed class FoundationRichSurfaceNativeTests
    {
        private readonly List<GameObject> created = new List<GameObject>();
        private SpatialBlendLocalWorld world;

        [SetUp]
        public void SetUp() { world = Create<SpatialBlendLocalWorld>("rich-surface-world"); }

        [TearDown]
        public void TearDown()
        {
            world.Shutdown();
            for (int i = created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(created[i]);
            created.Clear();
        }

        [Test]
        public void TranslatedRotatedScaledSupportsProduceAffordanceAndFacingRichChannels()
        {
            Vector2[] square = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            // ABI 4: scope belongs to the relation family. The
            // supports are descendants of the actor's own element, which is placed with its support (below), not named
            // by the anchors; Facing is global, so the facing referent below is one visible referent among the others.
            SpatialBlendObject sourceOwner = ObjectIn("rich-source", "source-owner");
            SpatialBlendObject targetOwner = ObjectIn("rich-target", "target-owner");
            SpatialBlendObject sourceSupport = Support(sourceOwner, "source-seat", new Vector3(8, .4f, 3), 27, new Vector3(1.4f, 1, .65f), square);
            SpatialBlendObject targetSupport = Support(targetOwner, "target-seat", new Vector3(-.7f, .8f, 1.2f), -52, new Vector3(.7f, 1, 1.6f), square);
            Assert.That(sourceSupport.TryRegister(out string error), Is.True, error);
            Assert.That(targetSupport.TryRegister(out error), Is.True, error);
            Assert.That(world.TryRegisterSupportCorrespondence(sourceSupport, targetSupport, out IDisposable lease, out error), Is.True, error);
            ulong lifetime = 0;
            try
            {
                // A distinct logical referent for facing uses the same shared
                // identifier in both spaces.  It is not either physical seat
                // and deliberately has no support-correspondence lease.
                SpatialBlendObject sourceFacingReferent = ObjectIn("rich-source", "facing-referent");
                SpatialBlendObject targetFacingReferent = ObjectIn("rich-target", "facing-referent");
                // The receiver root must share its target support's placement:
                // otherwise the mapped hand lies outside the bounded reach policy.
                sourceOwner.transform.SetPositionAndRotation(sourceSupport.transform.position, sourceSupport.transform.rotation);
                targetOwner.transform.SetPositionAndRotation(targetSupport.transform.position, targetSupport.transform.rotation);
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(targetOwner.TryRegister(out error), Is.True, error);
                Assert.That(sourceFacingReferent.TryRegister(out error), Is.True, error);
                Assert.That(targetFacingReferent.TryRegister(out error), Is.True, error);
                const long facingSharedId = 900000000000000973L;
                SpatialBlendAPI.SetElementSharedId(sourceFacingReferent.Registration.NativeSpace,
                    sourceFacingReferent.Registration.NativeElement, facingSharedId);
                SpatialBlendAPI.SetElementSharedId(targetFacingReferent.Registration.NativeSpace,
                    targetFacingReferent.Registration.NativeElement, facingSharedId);
                SpatialBlendActor source = Create<SpatialBlendActor>("rich-source-actor");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("rich-target-actor");
                source.Configure(sourceOwner, "rich-person", true, new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("hand", SpatialBlendActorAnchorRole.Body, Vector3.zero, positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                });
                receiver.Configure(targetOwner, "rich-person", false, new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("hand", SpatialBlendActorAnchorRole.Body, Vector3.zero, positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                });
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                // The core solves a receiver only from a published pose; the driver is the only writer of it (ABI 3 and 4),
                // so the fixture plays the driver at the receiver's root.
                Assert.That(receiver.TryPublishEmbodiedPose(targetSupport.transform.position, -52f, out error), Is.True, error);
                // Facing requires an admitted current receiver anchor.  This is
                // authored fixture calibration, not a claim that production
                // auto-enables/reset-disabled body anchors.
                Vector3 receiverHand = targetSupport.transform.TransformPoint(new Vector3(.35f, 0, -.2f));
                SpatialBlendAPI.SetAnchorTransform(receiver.Registration.NativeSpace,
                    receiver.Registration.NativeActor, "hand", receiverHand, -52f);
                Assert.That(SpatialBlendAPI.SetAnchorActive(receiver.Registration.NativeSpace,
                    receiver.Registration.NativeActor, "hand", true), Is.True);
                // Facing is global: every visible referent is in scope, the distinct logical
                // referent among them, so it is selected below by its shared identifier, not taken as the first contributor.
                SpatialBlendAPI.Bind(source.Registration.NativeSpace, source.Registration.NativeActor, "hand",
                    SbKernelType.Facing);
                var captures = new List<RichRealizationSnapshot>();
                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(receiver.Registration.NativeSpace, captures.Add, out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryActivateRichRealization(receiver.Registration.NativeSpace, receiver.Registration.NativeActor, out lifetime, out error), Is.True, error);
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink input), Is.True);
                Vector3 sourcePoint = sourceSupport.transform.TransformPoint(new Vector3(.35f, 0, -.2f));
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 0, timeSeconds = 17d, anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = sourceSupport.transform.position, worldRotation = sourceSupport.transform.rotation },
                        new AnchorPoseSample { anchorId = "hand", worldPosition = sourcePoint, worldRotation = Quaternion.Euler(0, 61, 0) }
                    }
                }), Is.True);
                world.DrainRealizations(18d);
                RichRealizationSnapshot snapshot = captures.Find(value => value.Header.SnapshotStatus == RichSnapshotStatus.Complete);
                Assert.That(snapshot, Is.Not.Null, "No complete rich realization arrived.");
                RichRealizationChannel affordance = Find(snapshot, RichRelationFamily.AffordancePose, "hand");
                RichRealizationChannel facing = Find(snapshot, RichRelationFamily.Facing, "hand");
                Assert.That(affordance.Geometry, Is.EqualTo(RichGeometry.ExactSupport));
                Assert.That(affordance.GoalKind, Is.EqualTo(RichGoalKind.SupportPose));
                Assert.That(affordance.HasZ, Is.True);
                string facingDiagnostic = "expected facing shared-id=" + facingSharedId + " source-facing element=" +
                    sourceFacingReferent.Registration.NativeElement + " resolved-facing element=" +
                    targetFacingReferent.Registration.NativeElement + " source-support element=" +
                    sourceSupport.Registration.NativeElement + " target-support element=" +
                    targetSupport.Registration.NativeElement + " rich-json=" + snapshot.ToDeterministicJson();
                Assert.That(facing.GoalKind, Is.EqualTo(RichGoalKind.FacingDirection), facingDiagnostic);
                Assert.That(facing.HasGoal, Is.True, facingDiagnostic);
                Assert.That(facing.Contributors.Count, Is.GreaterThan(0));
                RichRealizationContributor facingContributor = null;
                foreach (RichRealizationContributor candidate in facing.Contributors)
                    if (candidate.ReferentId.Known && candidate.ReferentId.Value == facingSharedId) facingContributor = candidate;
                Assert.That(facingContributor, Is.Not.Null, facingDiagnostic);
                Assert.That(facingContributor.SourceElement.Value,
                    Is.EqualTo(sourceFacingReferent.Registration.NativeElement), facingDiagnostic);
                Assert.That(facingContributor.ResolvedLocalElement.Value,
                    Is.EqualTo(targetFacingReferent.Registration.NativeElement), facingDiagnostic);
                RichRealizationContributor contributor = affordance.Contributors[0];
                Assert.That(contributor.HasMappedSupport, Is.True);
                Assert.That(contributor.SupportPlaneKnown, Is.True);
                Assert.That(contributor.SourceSpace.Known, Is.True);
                Assert.That(contributor.SourceElement.Known, Is.True);
                Assert.That(contributor.ResolvedLocalElement.Known, Is.True);
                Assert.That(contributor.SourceSpace.Value, Is.EqualTo(source.Registration.NativeSpace));
                Assert.That(contributor.SourceElement.Value, Is.EqualTo(sourceSupport.Registration.NativeElement));
                Assert.That(contributor.ResolvedLocalElement.Value, Is.EqualTo(targetSupport.Registration.NativeElement));
                Assert.That(contributor.SupportPlaneToWorld.Values.Count, Is.EqualTo(16));
                Assert.That(contributor.SupportPlaneToWorld.Values[12], Is.EqualTo(0).Within(1e-9));
                Assert.That(contributor.SupportPlaneToWorld.Values[13], Is.EqualTo(0).Within(1e-9));
                Assert.That(contributor.SupportPlaneToWorld.Values[14], Is.EqualTo(0).Within(1e-9));
                Assert.That(contributor.SupportPlaneToWorld.Values[15], Is.EqualTo(1).Within(1e-9));
                // Polygon3D reports a normalized support frame: authored scale
                // is baked into chart boundary extents, never this matrix.
                double angle = 52d * Math.PI / 180d;
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                Assert.That(contributor.SupportPlaneToWorld.Values[0], Is.EqualTo(cosine).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[1], Is.EqualTo(-sine).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[4], Is.EqualTo(sine).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[5], Is.EqualTo(cosine).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[3], Is.EqualTo(-.7).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[7], Is.EqualTo(1.2).Within(2e-5));
                Assert.That(contributor.SupportPlaneToWorld.Values[11], Is.EqualTo(.8).Within(2e-5));
                // Independent radial-chart oracle.  The off-centre source
                // contact has metric (.35*1.4, -.2*.65)=(.49,-.13); its ray
                // hits the source X edge first, so normalized radius=.35.
                // Target X extent=.7 decodes metric (.245,-.065), then the
                // unit Z-up target frame applies its rotation/translation.
                double sourceMetricX = .35d * 1.4d;
                double sourceMetricY = -.2d * .65d;
                Assert.That(Math.Abs(sourceMetricX / 1.4d), Is.EqualTo(.35d).Within(1e-12));
                Assert.That(Math.Abs(sourceMetricY / .65d), Is.LessThan(.35d));
                double targetMetricX = .35d * .7d;
                double targetMetricY = -.2d * .65d / 1.4d * .7d;
                double expectedX = cosine * targetMetricX - sine * targetMetricY - .7d;
                double expectedY = sine * targetMetricX + cosine * targetMetricY + 1.2d;
                Assert.That(contributor.MappedSupport.X, Is.EqualTo(expectedX).Within(2e-5));
                Assert.That(contributor.MappedSupport.Y, Is.EqualTo(expectedY).Within(2e-5));
                Assert.That(contributor.MappedSupport.Z, Is.EqualTo(.8d).Within(2e-5));
                Export(snapshot);
            }
            finally
            {
                if (lifetime != 0) { SpatialBlendAPI.TryRetireRichRealization(lifetime, out _); SpatialBlendAPI.TryReleaseRichRealization(lifetime, out _, out _); }
                lease.Dispose();
            }
        }

        private static RichRealizationChannel Find(RichRealizationSnapshot snapshot, RichRelationFamily family, string anchor)
        {
            RichRealizationChannel channel = null;
            foreach (RichRealizationChannel value in snapshot.Channels) if (value.Family == family && value.Anchor == anchor) { channel = value; break; }
            Assert.That(channel, Is.Not.Null, "Missing " + family + " channel for " + anchor);
            return channel;
        }

        private void Export(RichRealizationSnapshot snapshot)
        {
            string directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "rich-realization-support-fixtures");
            Directory.CreateDirectory(directory);
            string json = snapshot.ToDeterministicJson();
            string file = "translated_rotated_scaled_affordance_facing.json";
            File.WriteAllText(Path.Combine(directory, file), json, new UTF8Encoding(false));
            using (SHA256 sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(directory, "native-support-manifest.json"), "{\"schema\":\"spatialblend.rich_realization.native_manifest\",\"schema_version\":1,\"cases\":[{\"label\":\"translated_rotated_scaled_affordance_facing\",\"evidence_type\":\"native_capture\",\"path\":\"" + file + "\",\"sha256\":\"" + hash + "\",\"snapshot_status\":\"complete\",\"source_provenance_mode\":\"timestamped_observation\"}]}", new UTF8Encoding(false));
            }
        }

        private SpatialBlendObject Support(SpatialBlendObject owner, string id, Vector3 position, float yaw, Vector3 scale, Vector2[] polygon)
        {
            string space = owner.SpaceId;
            SpatialBlendObject support = Create<SpatialBlendObject>(id);
            support.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
            support.transform.localScale = scale;
            support.Configure(world, space, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetColliderGeometryEnabled(false, out string error), Is.True, error);
            var vertices = new Vector3[polygon.Length];
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = new Vector3(polygon[i].x, 0, polygon[i].y);
            Assert.That(support.TrySetSupportPolygon(vertices, out error), Is.True, error);
            return support;
        }

        private SpatialBlendObject ObjectIn(string space, string id)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(world, space, id, null, SpatialBlendObjectKind.Object);
            return value;
        }

        private T Create<T>(string name) where T : Component
        {
            var value = new GameObject(name); created.Add(value);
            T component = value.AddComponent<T>();
            typeof(T).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(component, null);
            return component;
        }
    }
}
#endif
