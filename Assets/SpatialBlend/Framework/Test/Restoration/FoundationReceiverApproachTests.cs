// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // C4 has an independent native fixture.  It never solves a realization or
    // uses a source packet/Transform as an approach substitute.
    [Category("FoundationReceiverApproach")]
    public sealed class FoundationReceiverApproachTests
    {
        private long _session, _space, _room, _actor;
        private ReceiverInputEpoch _input;
        private ReceiverInputFrame _frame;

        [SetUp]
        public void SetUp()
        {
            _session = SpatialBlendAPI.CreateSession(); _space = SpatialBlendAPI.AddSpace(_session);
            _room = SpatialBlendAPI.AddElement(_space);
            SpatialBlendAPI.SetElementPolygon(_space, _room, SbGeometryRole.Navigable,
                new[] { new Vector2(-5, -3), new Vector2(5, -3), new Vector2(5, 3), new Vector2(-5, 3) });
            _actor = SpatialBlendAPI.CreateActor(_space); SpatialBlendAPI.SetActorElement(_space, _actor, _room);
            SpatialBlendAPI.AddAnchor(_space, _actor, "root", Vector3.zero); SpatialBlendAPI.AddAnchor(_space, _actor, "pelvis", Vector3.zero);
            Assert.That(SpatialBlendAPI.TryBeginReceiverInput(_space, _actor, "root", new[] { "root", "pelvis" }, 3, 5, out _input, out string error), Is.True, error);
            Assert.That(SpatialBlendAPI.TryReadReceiverInputTime(_input, out long now, out error), Is.True, error);
            Assert.That(ReceiverInputFrame.TryCreate(_input, 1, now, now + 1000000000L,
                new[] { new ReceiverAnchorPose("root", true, Matrix4x4.TRS(new Vector3(-3, 0, 0), Quaternion.identity, Vector3.one)),
                    new ReceiverAnchorPose("pelvis", true, Matrix4x4.TRS(new Vector3(-3, 0, 0), Quaternion.identity, Vector3.one)) }, out _frame, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(_frame, out error), Is.True, error);
        }

        [TearDown]
        public void TearDown() { if (_session != 0) SpatialBlendAPI.DestroySession(_session); }

        [Test]
        public void EveryC3cAndC3aLayoutFieldNegotiatesAndDescriptorMismatchRejects()
        {
            Assert.That(SpatialBlendAPI.TryGetReceiverApproachFeatures(out var context, out var feasibility, out string error), Is.True, error);
            Type[] contextTypes = { typeof(SpatialBlendAPI.SbReceiverApproachContextFeature), typeof(SpatialBlendAPI.SbReceiverApproachContextRegistration), typeof(SpatialBlendAPI.SbReceiverApproachPolygon), typeof(SpatialBlendAPI.SbReceiverApproachDestination) };
            string[][] contextFields = {
                new[] { "descriptor_size", "feature_version", "registration_size", "polygon_size", "destination_size", "max_polygons", "max_vertices_per_polygon", "max_total_vertices", "max_root_name_bytes", "coordinate_convention", "affine_convention", "required_receiver_input_version", "native_clock_domain" },
                new[] { "struct_size", "feature_version", "expected_registration_generation", "expected_receiver_input_epoch", "expected_receiver_input_sequence", "geometry_revision", "requested_valid_until_ns", "navigation_root_utf8", "navigation_root_bytes", "coordinate_convention", "navigation_root_pose_row_major", "navigable_regions", "navigable_region_count", "obstacle_regions", "obstacle_region_count", "footprint_radius_m", "required_clearance_m", "destination" },
                new[] { "struct_size", "vertex_count", "nav_element_id", "region_id", "coordinate_convention", "affine_convention", "local_to_space_row_major", "vertices_xy" },
                new[] { "struct_size", "space", "region_id", "object", "affordance_id", "support_revision", "local_support", "coordinate_convention", "reserved", "safe_approach_pose_row_major" } };
            uint ContextCount(uint kind) => (uint)contextFields[kind - 1].Length;
            uint ContextOffset(uint kind, uint index) => (uint)Marshal.OffsetOf(contextTypes[kind - 1], contextFields[kind - 1][index]).ToInt64();
            Assert.That(SpatialBlendAPI.ValidateReceiverApproachContextFeature(context, ContextCount, ContextOffset, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.ValidateReceiverApproachContextFeature(context, k => ContextCount(k) + (k == 3 ? 1u : 0), ContextOffset, out _), Is.False);
            Type[] feasibilityTypes = { typeof(SpatialBlendAPI.SbReceiverApproachFeasibilityFeature), typeof(SpatialBlendAPI.SbReceiverApproachFeasibilityHeader) };
            string[][] feasibilityFields = {
                new[] { "descriptor_size", "feature_version", "header_size", "max_route_points", "coordinate_convention", "route_point_stride", "native_clock_domain", "required_context_version" },
                new[] { "struct_size", "feature_version", "feasibility_status", "coordinate_convention", "session", "space", "receiver_actor", "registration_session_id", "registration_generation", "receiver_input_epoch", "receiver_input_sequence", "receiver_input_revision", "geometry_revision", "support_revision", "context_revision", "calculation_revision", "owner_lifetime", "native_clock_domain", "issued_at_ns", "valid_until_ns", "route_point_count", "route_point_stride", "start_x", "start_y", "safe_approach_x", "safe_approach_y", "route_length_m" } };
            uint FeasibilityCount(uint kind) => (uint)feasibilityFields[kind - 1].Length;
            uint FeasibilityOffset(uint kind, uint index) => (uint)Marshal.OffsetOf(feasibilityTypes[kind - 1], feasibilityFields[kind - 1][index]).ToInt64();
            Assert.That(SpatialBlendAPI.ValidateReceiverApproachFeasibilityFeature(feasibility, FeasibilityCount, FeasibilityOffset, out error), Is.True, error);
            var broken = feasibility; broken.route_point_stride++;
            Assert.That(SpatialBlendAPI.ValidateReceiverApproachFeasibilityFeature(broken, FeasibilityCount, FeasibilityOffset, out _), Is.False);
        }

        [Test]
        public void CompleteC3cRegistrationAndPinnedTwoPassC3aCopyDeepOwnRouteAndProvenance()
        {
            ReceiverApproachContextInput input = Context();
            Assert.That(SpatialBlendAPI.TryRegisterReceiverApproachContext(_session, _space, _actor, input,
                out ulong context, out int registrationResult, out string error), Is.True, error);
            Assert.That(registrationResult, Is.EqualTo(1)); Assert.That(context, Is.GreaterThan(0));
            var expected = new ReceiverApproachExpectedIdentity(_session, _space, _actor, context, _input.Epoch, _frame.Sequence,
                11, 12, input.RequestedValidUntilNanoseconds);
            Assert.That(SpatialBlendAPI.TryCopyReceiverApproachFeasibility(_session, _space, _actor, context, expected, 0,
                out ReceiverApproachNativeCopy copy, out int copyResult, out error), Is.True, error);
            Assert.That(copyResult, Is.EqualTo(1)); Assert.That(copy.Header.calculation_revision, Is.GreaterThan(0));
            Assert.That(copy.Header.route_point_count, Is.GreaterThanOrEqualTo(2));
            Assert.That(copy.Header.valid_until_ns, Is.LessThanOrEqualTo(input.RequestedValidUntilNanoseconds));
            Assert.That(copy.RouteXY[0], Is.EqualTo(-3).Within(1e-6));
            Assert.That(copy.RouteXY[copy.RouteXY.Count - 2], Is.EqualTo(3).Within(1e-6));
            // The returned route is detached every time; neither a caller array
            // nor a DTO cast can turn a complete receipt into a mutable prefix.
            var route = (IList<double>)copy.RouteXY;
            Assert.Throws<NotSupportedException>(() => route[0] = 99);
            Assert.That(copy.RouteXY[0], Is.EqualTo(-3).Within(1e-6));
        }

        [Test]
        public void ChangedInputOrContextIdentityRejectsBeforeAnyDetachedReceipt()
        {
            ReceiverApproachContextInput input = Context();
            Assert.That(SpatialBlendAPI.TryRegisterReceiverApproachContext(_session, _space, _actor, input, out ulong context, out _, out string error), Is.True, error);
            var expected = new ReceiverApproachExpectedIdentity(_session, _space, _actor, context, _input.Epoch, _frame.Sequence, 11, 12, input.RequestedValidUntilNanoseconds);
            Assert.That(SpatialBlendAPI.TryCopyReceiverApproachFeasibility(_session, _space, _actor, context, expected, 0, out var first, out _, out error), Is.True, error);
            Assert.That(ReceiverInputFrame.TryCreate(_input, 2, _frame.ObservedAtNanoseconds + 1, _frame.ValidUntilNanoseconds,
                _frame.Anchors, out var newer, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(newer, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TryCopyReceiverApproachFeasibility(_session, _space, _actor, context, expected, 0, out var stale, out int result, out _), Is.False);
            Assert.That(stale, Is.Null); Assert.That(result, Is.EqualTo(-3));
            Assert.That(SpatialBlendAPI.TryCopyReceiverApproachFeasibility(_session, _space, _actor + 1, context, expected, 0, out var forged, out _, out _), Is.False);
            Assert.That(forged, Is.Null); Assert.That(first.RouteXY.Count, Is.GreaterThan(1));
        }

        private ReceiverApproachContextInput Context()
        {
            Assert.That(ReceiverInputValidation.TryNativePose(_frame.Anchors[0].WorldPose, out double[] root), Is.True);
            var nav = new ReceiverApproachPolygonInput(100, 30, Identity3(), new double[] { -5, -3, 5, -3, 5, 3, -5, 3 });
            var obstacle = new ReceiverApproachPolygonInput(101, 30, Identity3(), new double[] { 20, 20, 21, 20, 20, 21 });
            var support = new ReceiverApproachPolygonInput(102, 30, Identity3(), new double[] { -.5, -.5, .5, -.5, .5, .5, -.5, .5 });
            double[] safe = Identity4(); safe[3] = 3;
            return new ReceiverApproachContextInput(0, _input.Epoch, _frame.Sequence, 11, _frame.ObservedAtNanoseconds + 500000000L,
                "root", root, new[] { nav }, new[] { obstacle }, .25, .15, _space, 30, _room, 50, 12, support, safe);
        }
        private static double[] Identity3() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        private static double[] Identity4() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    }

    [Category("FoundationReceiverApproach")]
    public sealed class FoundationReceiverApproachRuntimeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        [SetUp] public void SetUp() { _world = Create<SpatialBlendLocalWorld>("approach-world"); }
        [TearDown] public void TearDown() { _world.Shutdown(); foreach (var value in _created) UnityEngine.Object.DestroyImmediate(value); }

        [Test]
        public void OwnerIssuedReceiptRejectsWorkerNewInputAndWriterRetirementWithoutOutput()
        {
            SpatialBlendObject owner = Create<SpatialBlendObject>("owner"); owner.Configure(_world, "receiver", "owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("actor");
            actor.Configure(owner, "receiver", false, new[] { new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero) });
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryBeginReceiverInput(3, 5, out var writer, out error), Is.True, error);
            ReceiverInputFrame frame = Frame(writer, 1);
            Assert.That(writer.TrySubmit(frame, out error), Is.True, error);
            var input = Context(writer, frame, owner.Registration.NativeElement, 0);
            Assert.That(writer.TryRegisterReceiverApproach(input, out var lease, out int nativeResult, out error), Is.True, error);
            Assert.That(nativeResult, Is.EqualTo(1));
            Assert.That(lease.TryCopy(0, out var receipt, out nativeResult, out error), Is.True, error);
            Assert.That(receipt.TryRevalidate(out error), Is.True, error);
            bool worker = true; var thread = new Thread(() => worker = receipt.TryRevalidate(out _)); thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(worker, Is.False);
            Assert.That(writer.TrySubmit(Frame(writer, 2), out error), Is.True, error);
            Assert.That(receipt.TryRevalidate(out _), Is.False);
            Assert.That(writer.TryClose(out error), Is.True, error);
            Assert.That(lease.TryCopy(0, out _, out _, out _), Is.False);
        }

        private ReceiverApproachContextInput Context(ReceiverInputWriter writer, ReceiverInputFrame frame, long destination, ulong prior)
        {
            Assert.That(ReceiverInputValidation.TryNativePose(frame.Anchors[0].WorldPose, out double[] root), Is.True);
            var nav = new ReceiverApproachPolygonInput(100, 30, Identity3(), new double[] { -5, -3, 5, -3, 5, 3, -5, 3 });
            var obstacle = new ReceiverApproachPolygonInput(101, 30, Identity3(), new double[] { 20, 20, 21, 20, 20, 21 });
            var support = new ReceiverApproachPolygonInput(102, 30, Identity3(), new double[] { -.5, -.5, .5, -.5, .5, .5, -.5, .5 });
            double[] safe = Identity4(); safe[3] = 3;
            return new ReceiverApproachContextInput(prior, writer.Input.Epoch, frame.Sequence, 11,
                frame.ObservedAtNanoseconds + 500000000L, "root", root, new[] { nav }, new[] { obstacle }, .25, .15,
                writer.Registration.NativeSpace, 30, destination, 50, 12, support, safe);
        }
        private ReceiverInputFrame Frame(ReceiverInputWriter writer, ulong sequence)
        {
            Assert.That(writer.TryReadAcquisitionClock(out long now, out string error), Is.True, error);
            Assert.That(ReceiverInputFrame.TryCreate(writer.Input, sequence, now, now + 1000000000L,
                new[] { new ReceiverAnchorPose("root", true, Matrix4x4.TRS(new Vector3(-3, 0, 0), Quaternion.identity, Vector3.one)) }, out var frame, out error), Is.True, error);
            return frame;
        }
        private T Create<T>(string name) where T : Component
        { var go = new GameObject(name); _created.Add(go); var value = go.AddComponent<T>(); typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null); return value; }
        private static double[] Identity3() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        private static double[] Identity4() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    }
}
#endif
