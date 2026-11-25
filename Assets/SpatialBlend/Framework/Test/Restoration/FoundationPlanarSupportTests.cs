// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private SpatialBlendObject Sofa => _created.Single(go => go.name == "sofa").GetComponent<SpatialBlendObject>();
        private static RichRealizationContributor SeatContributor(ReceiverBodyRealizationCheckpoint checkpoint) =>
            checkpoint.Snapshot.Channels.Single(channel => channel.Anchor == "seat_contact" &&
                channel.Family == RichRelationFamily.AffordancePose).Contributors.Single(value =>
                    value.Disposition == RichContributorDisposition.Included && value.HasMappedSupport);

        private OwnedAffordanceSupportRead ReadSeatSupport(ReceiverBodyRealizationCheckpoint checkpoint)
        {
            Assert.That(_world.TryReadAffordanceSupport(checkpoint, SeatContributor(checkpoint), Sofa,
                out var read, out string error), Is.True, error);
            return read;
        }

        [Test]
        public void PlanarSupportNegotiatesEveryManagedLayoutFieldAndRejectsMismatch()
        {
            Assert.That(SpatialBlendAPI.TryGetPlanarSupportFeature(out var feature, out string error), Is.True, error);
            Type[] types = { typeof(SpatialBlendAPI.SbPlanarSupportFeature), typeof(SpatialBlendAPI.SbPlanarSupportHeader) };
            string[][] fields = {
                new[] { "descriptor_size", "feature_version", "header_size", "max_points", "coordinate_convention", "plane_convention" },
                new[] { "struct_size", "feature_version", "definition_kind", "geometry_role", "space", "element", "tree_revision",
                    "support_revision", "point_count", "plane_convention", "plane_to_world_row_major" }
            };
            uint Count(uint kind) => (uint)fields[kind - 1].Length;
            uint Offset(uint kind, uint index) => (uint)Marshal.OffsetOf(types[kind - 1], fields[kind - 1][index]).ToInt64();
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(feature, Count, Offset, out error), Is.True, error);
            for (uint kind = 1; kind <= 2; ++kind)
            {
                uint selected = kind;
                Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(feature,
                    k => Count(k) + (k == selected ? 1u : 0), Offset, out _), Is.False);
                for (uint index = 0; index < Count(kind); ++index)
                {
                    uint field = index;
                    Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(feature, Count,
                        (k, i) => Offset(k, i) + (k == selected && i == field ? 1u : 0), out _), Is.False);
                }
            }
            var changed = feature; changed.max_points++;
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(changed, Count, Offset, out _), Is.False);
            changed = feature; changed.feature_version++;
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(changed, Count, Offset, out _), Is.False);
            changed = feature; changed.plane_convention++;
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportFeature(changed, Count, Offset, out _), Is.False);
        }

        [Test]
        public void PlanarSupportReadsNativeOrderedGeometryAndPreservesUnknownProvenance()
        {
            Sofa.transform.SetPositionAndRotation(new Vector3(.4f, .6f, -.2f), Quaternion.Euler(11, 23, -7));
            Assert.That(Sofa.TryRefresh(out string error), Is.True, error);
            var checkpoint = Prepare(out var snapshot);
            var read = ReadSeatSupport(checkpoint);
            Assert.That(read.Checkpoint, Is.SameAs(checkpoint));
            Assert.That(read.Contributor, Is.SameAs(SeatContributor(checkpoint)));
            Assert.That(read.TryConsume(out var geometry, out error), Is.True, error);
            Assert.That(geometry.PointCount, Is.EqualTo(4));
            Assert.That(geometry.NativeElement, Is.EqualTo(Sofa.Registration.NativeElement));
            Assert.That(geometry.TreeRevision, Is.EqualTo(snapshot.Header.ReceiverTreeRevision.Value));
            Assert.That(geometry.SupportRevision, Is.EqualTo(snapshot.Header.SupportRevision.Value));
            Assert.That(geometry.DefinitionKind, Is.EqualTo(1)); Assert.That(geometry.NativeGeometryRole, Is.EqualTo(4));
            Assert.That(SpatialBlendAPI.SamePlanarMatrix(geometry.PlaneToWorld.Values,
                read.Contributor.SupportPlaneToWorld.Values), Is.True);
            var corners = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
            for (int i = 0; i < corners.Length; ++i)
            {
                Vector3 actual = Sofa.transform.TransformPoint(corners[i]);
                double[] expected = { actual.x, actual.z, actual.y };
                for (int row = 0; row < 3; ++row)
                {
                    var m = geometry.PlaneToWorld.Values;
                    double world = m[row * 4] * geometry.BoundaryXY[i * 2] +
                        m[row * 4 + 1] * geometry.BoundaryXY[i * 2 + 1] + m[row * 4 + 3];
                    Assert.That(world, Is.EqualTo(expected[row]).Within(2e-6));
                }
            }
            Assert.Throws<NotSupportedException>(() => ((IList<double>)geometry.BoundaryXY)[0] = 99);
            Assert.That(read.Contributor.ChartVersionKnown, Is.False);
            // The C API binds through the owned path, which certifies the binding revision (in force at the
            // ABI 3.1 baseline too); only a Binding* exposed as mutable is unknown. The chart version is not surfaced.
            Assert.That(read.Contributor.BindingRevisionKnown, Is.True);
            Assert.That(read.Contributor.SupportIdentifier, Is.EqualTo("whole-planar-boundary"));
            Assert.That(checkpoint.Snapshot, Is.SameAs(snapshot));
            Assert.That(read.TryConsume(out geometry, out _), Is.False); Assert.That(geometry, Is.Null);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(checkpoint.Body));
        }

        [Test]
        public void PlanarSupportRejectsMovedGeometryAndReadsFreshNativeInsteadOfCache()
        {
            var old = Prepare(out _); var read = ReadSeatSupport(old);
            var support = Sofa; LocalObjectRegistration registration = support.Registration;
            Vector3 before = support.transform.position;
            // Bypass managed registration cache and Transform intentionally.
            var vertices = new[] { new Vector3(-1, .75f, -1), new Vector3(1, .75f, -1),
                new Vector3(1, .75f, 1), new Vector3(-1, .75f, 1) };
            SpatialBlendAPI.SetElementWorldSurface(registration.NativeSpace, registration.NativeElement, vertices, SbGeometryRole.Surface);
            Assert.That(read.TryConsume(out var missing, out _), Is.False); Assert.That(missing, Is.Null);
            Assert.That(_world.TryReadAffordanceSupport(old, SeatContributor(old), support, out _, out _), Is.False);
            var next = Solve();
            Assert.That(_binding.TryPrepareRealization(next, 1000000000, out var fresh, out string error), Is.True, error);
            var freshRead = ReadSeatSupport(fresh);
            Assert.That(freshRead.TryConsume(out var geometry, out error), Is.True, error);
            Assert.That(geometry.PlaneToWorld.Values[11], Is.EqualTo(.75).Within(1e-12));
            Assert.That(support.transform.position, Is.EqualTo(before), "The query must not fabricate a Transform update.");
            Assert.That(geometry.SupportRevision, Is.GreaterThan(old.Snapshot.Header.SupportRevision.Value));
        }

        [Test]
        public void PlanarSupportRejectsCopiedContributorOtherObjectAndOtherWorld()
        {
            var checkpoint = Prepare(out _); var c = SeatContributor(checkpoint);
            var copied = new RichRealizationContributor(c.ReferentId, c.SourceSpace, c.SourceElement, c.ResolvedLocalElement,
                c.BindingRevision, c.SnapshotOrdinal, c.AppliedWeight, c.HasMappedSupport, c.MappedSupport, c.GroundingMode,
                c.Disposition, c.Reason, c.ChartVersionKnown, c.BindingRevisionKnown, c.SupportIdentifier, c.SupportPlaneKnown, c.SupportPlaneToWorld);
            Assert.That(_world.TryReadAffordanceSupport(checkpoint, copied, Sofa, out _, out _), Is.False);
            var chair = _created.Single(go => go.name == "chair").GetComponent<SpatialBlendObject>();
            Assert.That(_world.TryReadAffordanceSupport(checkpoint, c, chair, out _, out _), Is.False);
            var otherWorld = Create<SpatialBlendLocalWorld>("other-support-world");
            Assert.That(otherWorld.TryReadAffordanceSupport(checkpoint, c, Sofa, out _, out _), Is.False);
            otherWorld.Shutdown();
            Assert.That(ReadSeatSupport(checkpoint).TryConsume(out _, out string error), Is.True, error);
        }

        [Test]
        public void PlanarSupportObjectReplacementCannotReviveOldRead()
        {
            var checkpoint = Prepare(out _); var read = ReadSeatSupport(checkpoint);
            var support = Sofa; var oldRegistration = support.Registration;
            support.Unregister();
            Assert.That(support.TryRegister(out string error), Is.True, error);
            Assert.That(support.Registration, Is.Not.SameAs(oldRegistration));
            Assert.That(read.TryConsume(out var geometry, out _), Is.False); Assert.That(geometry, Is.Null);
            Assert.That(support.IsRegistered, Is.True, "Retiring a read cannot clear replacement geometry.");
        }

        [Test]
        public void PlanarSupportWorkerCallsFailBeforeUnityAndLeaveOwnerReadUsable()
        {
            var checkpoint = Prepare(out _); var c = SeatContributor(checkpoint); var support = Sofa;
            var read = ReadSeatSupport(checkpoint);
            bool copied = true, consumed = true;
            Exception exception = null;
            var thread = new Thread(() => {
                try {
                    copied = _world.TryReadAffordanceSupport(checkpoint, c, support, out _, out _);
                    consumed = read.TryConsume(out _, out _);
                } catch (Exception e) { exception = e; }
            });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(exception, Is.Null); Assert.That(copied || consumed, Is.False);
            Assert.That(read.TryConsume(out _, out string error), Is.True, error);
        }

        [Test]
        public void PlanarSupportWithdrawnBodyOrCheckpointCannotSupplyGeometry()
        {
            var checkpoint = Prepare(out var snapshot); var read = ReadSeatSupport(checkpoint);
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var other, out string error), Is.True, error);
            Assert.That(checkpoint.TryWithdraw(out error), Is.True, error);
            Assert.That(read.TryConsume(out _, out _), Is.False);
            var otherRead = ReadSeatSupport(other);
            Assert.That(_binding.TryClear(out error), Is.True, error);
            Assert.That(otherRead.TryConsume(out _, out _), Is.False);
        }

        [Test]
        public void PlanarSupportHeaderRejectsMalformedPlanesAndComparesExactDoubles()
        {
            var matrix = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, .7, 0, 0, 0, 1 };
            var header = new SpatialBlendAPI.SbPlanarSupportHeader {
                struct_size = (uint)Marshal.SizeOf(typeof(SpatialBlendAPI.SbPlanarSupportHeader)), feature_version = 1,
                definition_kind = 1, geometry_role = 4, space = 3, element = 7, tree_revision = 11,
                support_revision = 13, point_count = 4, plane_convention = 1, plane_to_world_row_major = matrix
            };
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(header, 3, 7, 11, 13, out string error), Is.True, error);
            foreach (uint count in new uint[] { 0, 2, 65537 }) {
                var bad = header; bad.point_count = count;
                Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(bad, 3, 7, 11, 13, out _), Is.False);
            }
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(header, 3, 7, 11, 14, out _), Is.False);
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(header, 3, 8, 11, 13, out _), Is.False);
            foreach (int index in new[] { 0, 3, 11, 15 }) {
                var bad = header; bad.plane_to_world_row_major = (double[])matrix.Clone(); bad.plane_to_world_row_major[index] = double.NaN;
                Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(bad, 3, 7, 11, 13, out _), Is.False);
            }
            var singular = header; singular.plane_to_world_row_major = (double[])matrix.Clone(); singular.plane_to_world_row_major[0] = 0;
            Assert.That(SpatialBlendAPI.ValidatePlanarSupportHeader(singular, 3, 7, 11, 13, out _), Is.False);
            var altered = (double[])matrix.Clone(); altered[11] += 1e-15;
            Assert.That(SpatialBlendAPI.SamePlanarMatrix(matrix, altered), Is.False, "No float/tolerance substitute for native frame identity.");
            altered = (double[])matrix.Clone(); altered[1] = -0.0;
            Assert.That(SpatialBlendAPI.SamePlanarMatrix(matrix, altered), Is.False);
            Assert.That(SpatialBlendAPI.SamePlanarMatrix(matrix, (double[])matrix.Clone()), Is.True);
        }
    }
}
#endif
