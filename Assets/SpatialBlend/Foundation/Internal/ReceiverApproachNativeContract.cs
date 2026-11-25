// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    // Internal C3 ingress values are already in Core Space Z-up metres.  They
    // intentionally contain no Transform, source packet, body pose or output
    // controller reference.
    internal sealed class ReceiverApproachPolygonInput
    {
        internal ReceiverApproachPolygonInput(ulong element, ulong region, IReadOnlyList<double> affine,
            IReadOnlyList<double> vertices)
        {
            NavigationElement = element; Region = region;
            LocalToSpace = Copy(affine, 9); VerticesXY = Copy(vertices, -1);
        }
        internal ulong NavigationElement { get; }
        internal ulong Region { get; }
        internal double[] LocalToSpace { get; }
        internal double[] VerticesXY { get; }

        internal bool TryValidate(out string error)
        {
            error = null;
            if (NavigationElement == 0 || Region == 0 || LocalToSpace == null || LocalToSpace.Length != 9 ||
                VerticesXY == null || VerticesXY.Length < 6 || (VerticesXY.Length & 1) != 0 ||
                VerticesXY.Length / 2 > SpatialBlendAPI.ReceiverApproachMaximumVerticesPerPolygon)
                return ReceiverInputValidation.Fail("Receiver approach polygons require bounded explicit native identities and vertices.", out error);
            for (int i = 0; i < LocalToSpace.Length; ++i)
                if (!RichKnownDouble.Finite(LocalToSpace[i])) return ReceiverInputValidation.Fail("Receiver approach affine is not finite.", out error);
            for (int i = 0; i < VerticesXY.Length; ++i)
                if (!RichKnownDouble.Finite(VerticesXY[i])) return ReceiverInputValidation.Fail("Receiver approach polygon is not finite.", out error);
            // Native v1 uses homogeneous row-major XY affine.  Do not silently
            // accept a perspective/another-basis matrix.
            if (Math.Abs(LocalToSpace[6]) > 1e-12 || Math.Abs(LocalToSpace[7]) > 1e-12 || Math.Abs(LocalToSpace[8] - 1) > 1e-12)
                return ReceiverInputValidation.Fail("Receiver approach local-to-space map must be affine XY.", out error);
            double determinant = LocalToSpace[0] * LocalToSpace[4] - LocalToSpace[1] * LocalToSpace[3];
            if (!RichKnownDouble.Finite(determinant) || Math.Abs(determinant) <= 1e-12)
                return ReceiverInputValidation.Fail("Receiver approach local-to-space map is singular.", out error);
            return true;
        }

        private static double[] Copy(IReadOnlyList<double> values, int expected)
        {
            if (values == null || (expected >= 0 && values.Count != expected)) return null;
            var copy = new double[values.Count];
            for (int i = 0; i < copy.Length; ++i) copy[i] = values[i];
            return copy;
        }
    }

    internal sealed class ReceiverApproachContextInput
    {
        internal ReceiverApproachContextInput(ulong expectedRegistrationGeneration, ulong inputEpoch, ulong inputSequence,
            ulong geometryRevision, long requestedValidUntilNanoseconds, string navigationRoot,
            IReadOnlyList<double> navigationRootPose, IReadOnlyList<ReceiverApproachPolygonInput> navigable,
            IReadOnlyList<ReceiverApproachPolygonInput> obstacles, double footprintRadius, double requiredClearance,
            long destinationSpace, ulong destinationRegion, long destinationObject, ulong destinationAffordance,
            ulong supportRevision, ReceiverApproachPolygonInput localSupport, IReadOnlyList<double> safeApproachPose)
        {
            ExpectedRegistrationGeneration = expectedRegistrationGeneration; InputEpoch = inputEpoch; InputSequence = inputSequence;
            GeometryRevision = geometryRevision; RequestedValidUntilNanoseconds = requestedValidUntilNanoseconds;
            NavigationRoot = navigationRoot; NavigationRootPose = Copy(navigationRootPose, 16);
            Navigable = CopyPolygons(navigable); Obstacles = CopyPolygons(obstacles);
            FootprintRadius = footprintRadius; RequiredClearance = requiredClearance;
            DestinationSpace = destinationSpace; DestinationRegion = destinationRegion; DestinationObject = destinationObject;
            DestinationAffordance = destinationAffordance; SupportRevision = supportRevision; LocalSupport = localSupport;
            SafeApproachPose = Copy(safeApproachPose, 16);
        }
        internal ulong ExpectedRegistrationGeneration { get; }
        internal ulong InputEpoch { get; }
        internal ulong InputSequence { get; }
        internal ulong GeometryRevision { get; }
        internal long RequestedValidUntilNanoseconds { get; }
        internal string NavigationRoot { get; }
        internal double[] NavigationRootPose { get; }
        internal ReceiverApproachPolygonInput[] Navigable { get; }
        internal ReceiverApproachPolygonInput[] Obstacles { get; }
        internal double FootprintRadius { get; }
        internal double RequiredClearance { get; }
        internal long DestinationSpace { get; }
        internal ulong DestinationRegion { get; }
        internal long DestinationObject { get; }
        internal ulong DestinationAffordance { get; }
        internal ulong SupportRevision { get; }
        internal ReceiverApproachPolygonInput LocalSupport { get; }
        internal double[] SafeApproachPose { get; }

        internal bool TryValidate(long expectedSpace, out string error)
        {
            error = null;
            if (InputEpoch == 0 || InputSequence == 0 || GeometryRevision == 0 || RequestedValidUntilNanoseconds <= 0 ||
                !ReceiverInputValidation.ValidName(NavigationRoot) || NavigationRootPose == null || SafeApproachPose == null ||
                Navigable == null || Obstacles == null || Navigable.Length == 0 || Obstacles.Length == 0 ||
                Navigable.Length > SpatialBlendAPI.ReceiverApproachMaximumPolygons || Obstacles.Length > SpatialBlendAPI.ReceiverApproachMaximumPolygons ||
                !RichKnownDouble.Finite(FootprintRadius) || !RichKnownDouble.Finite(RequiredClearance) || FootprintRadius <= 0 || RequiredClearance < 0 ||
                DestinationSpace != expectedSpace || DestinationRegion == 0 || DestinationObject <= 0 || DestinationAffordance == 0 || SupportRevision == 0 || LocalSupport == null)
                return ReceiverInputValidation.Fail("Receiver approach context is incomplete or belongs to another receiver space.", out error);
            if (!RigidAffine(NavigationRootPose) || !RigidAffine(SafeApproachPose))
                return ReceiverInputValidation.Fail("Receiver approach root and safe-approach poses must be finite proper native rigid poses.", out error);
            int vertices = 0;
            foreach (var value in Navigable) { if (value == null || !value.TryValidate(out error)) return false; vertices += value.VerticesXY.Length / 2; }
            foreach (var value in Obstacles) { if (value == null || !value.TryValidate(out error)) return false; vertices += value.VerticesXY.Length / 2; }
            if (!LocalSupport.TryValidate(out error)) return false;
            vertices += LocalSupport.VerticesXY.Length / 2;
            if (vertices > SpatialBlendAPI.ReceiverApproachMaximumTotalVertices)
                return ReceiverInputValidation.Fail("Receiver approach context exceeds the native total vertex bound.", out error);
            return true;
        }

        private static bool RigidAffine(double[] values)
        {
            if (values == null || values.Length != 16) return false;
            for (int i = 0; i < values.Length; ++i) if (!RichKnownDouble.Finite(values[i])) return false;
            if (Math.Abs(values[12]) > 1e-12 || Math.Abs(values[13]) > 1e-12 || Math.Abs(values[14]) > 1e-12 || Math.Abs(values[15] - 1) > 1e-12) return false;
            double determinant = values[0] * (values[5] * values[10] - values[6] * values[9]) - values[1] * (values[4] * values[10] - values[6] * values[8]) + values[2] * (values[4] * values[9] - values[5] * values[8]);
            return RichKnownDouble.Finite(determinant) && Math.Abs(determinant - 1) <= 1e-6;
        }

        private static double[] Copy(IReadOnlyList<double> values, int count)
        {
            if (values == null || values.Count != count) return null;
            var copy = new double[count]; for (int i = 0; i < count; ++i) copy[i] = values[i]; return copy;
        }
        private static ReceiverApproachPolygonInput[] CopyPolygons(IReadOnlyList<ReceiverApproachPolygonInput> values)
        {
            if (values == null) return null;
            var copy = new ReceiverApproachPolygonInput[values.Count]; for (int i = 0; i < copy.Length; ++i) copy[i] = values[i]; return copy;
        }
    }

    internal sealed class ReceiverApproachExpectedIdentity
    {
        internal ReceiverApproachExpectedIdentity(long session, long space, long actor, ulong context, ulong inputEpoch,
            ulong inputSequence, ulong geometry, ulong support, long maximumValidUntil, ulong nativeClockDomain = 1)
        { Session = session; Space = space; Actor = actor; ContextRevision = context; InputEpoch = inputEpoch; InputSequence = inputSequence; GeometryRevision = geometry; SupportRevision = support; MaximumValidUntilNanoseconds = maximumValidUntil; NativeClockDomain = nativeClockDomain; }
        internal long Session { get; } internal long Space { get; } internal long Actor { get; }
        internal ulong ContextRevision { get; } internal ulong InputEpoch { get; } internal ulong InputSequence { get; }
        internal ulong GeometryRevision { get; } internal ulong SupportRevision { get; }
        internal long MaximumValidUntilNanoseconds { get; } internal ulong NativeClockDomain { get; }
        internal bool MatchesCall(long session, long space, long actor, ulong context) => session == Session && space == Space && actor == Actor && context == ContextRevision && MaximumValidUntilNanoseconds > 0;
    }

    internal sealed class ReceiverApproachNativeCopy
    {
        private readonly double[] _route;
        internal ReceiverApproachNativeCopy(SpatialBlendAPI.SbReceiverApproachFeasibilityHeader header, double[] route)
        { Header = header; _route = (double[])route.Clone(); }
        internal SpatialBlendAPI.SbReceiverApproachFeasibilityHeader Header { get; }
        internal IReadOnlyList<double> RouteXY => Array.AsReadOnly((double[])_route.Clone());
    }

    internal sealed class ReceiverApproachPinnedInput : IDisposable
    {
        private readonly List<GCHandle> _pins = new List<GCHandle>();
        private readonly List<IntPtr> _allocations = new List<IntPtr>();
        internal ReceiverApproachPinnedInput(ReceiverApproachContextInput input)
        {
            byte[] root = ReceiverInputValidation.Utf8.GetBytes(input.NavigationRoot);
            Native = new SpatialBlendAPI.SbReceiverApproachContextRegistration
            {
                struct_size = (uint)Marshal.SizeOf(typeof(SpatialBlendAPI.SbReceiverApproachContextRegistration)), feature_version = SpatialBlendAPI.ReceiverApproachFeatureVersion,
                expected_registration_generation = input.ExpectedRegistrationGeneration, expected_receiver_input_epoch = input.InputEpoch,
                expected_receiver_input_sequence = input.InputSequence, geometry_revision = input.GeometryRevision,
                requested_valid_until_ns = input.RequestedValidUntilNanoseconds, navigation_root_utf8 = Pin(root), navigation_root_bytes = (uint)root.Length,
                coordinate_convention = SpatialBlendAPI.ReceiverApproachCoordinateConvention, navigation_root_pose_row_major = (double[])input.NavigationRootPose.Clone(),
                navigable_regions = MarshalPolygons(input.Navigable), navigable_region_count = (uint)input.Navigable.Length,
                obstacle_regions = MarshalPolygons(input.Obstacles), obstacle_region_count = (uint)input.Obstacles.Length,
                footprint_radius_m = input.FootprintRadius, required_clearance_m = input.RequiredClearance,
                destination = new SpatialBlendAPI.SbReceiverApproachDestination
                {
                    struct_size = (uint)Marshal.SizeOf(typeof(SpatialBlendAPI.SbReceiverApproachDestination)), space = input.DestinationSpace,
                    region_id = input.DestinationRegion, @object = input.DestinationObject, affordance_id = input.DestinationAffordance,
                    support_revision = input.SupportRevision, local_support = MarshalPolygon(input.LocalSupport),
                    coordinate_convention = SpatialBlendAPI.ReceiverApproachCoordinateConvention, reserved = 0,
                    safe_approach_pose_row_major = (double[])input.SafeApproachPose.Clone()
                }
            };
        }
        internal SpatialBlendAPI.SbReceiverApproachContextRegistration Native { get; }

        private IntPtr Pin(Array value) { var handle = GCHandle.Alloc(value, GCHandleType.Pinned); _pins.Add(handle); return handle.AddrOfPinnedObject(); }
        private SpatialBlendAPI.SbReceiverApproachPolygon MarshalPolygon(ReceiverApproachPolygonInput input) => new SpatialBlendAPI.SbReceiverApproachPolygon
        {
            struct_size = (uint)Marshal.SizeOf(typeof(SpatialBlendAPI.SbReceiverApproachPolygon)), vertex_count = (uint)(input.VerticesXY.Length / 2),
            nav_element_id = input.NavigationElement, region_id = input.Region, coordinate_convention = SpatialBlendAPI.ReceiverApproachCoordinateConvention,
            affine_convention = SpatialBlendAPI.ReceiverApproachAffineConvention, local_to_space_row_major = (double[])input.LocalToSpace.Clone(), vertices_xy = Pin(input.VerticesXY)
        };
        private IntPtr MarshalPolygons(ReceiverApproachPolygonInput[] inputs)
        {
            int stride = Marshal.SizeOf(typeof(SpatialBlendAPI.SbReceiverApproachPolygon));
            IntPtr result = Marshal.AllocHGlobal(checked(stride * inputs.Length)); _allocations.Add(result);
            for (int i = 0; i < inputs.Length; ++i) Marshal.StructureToPtr(MarshalPolygon(inputs[i]), IntPtr.Add(result, i * stride), false);
            return result;
        }
        public void Dispose()
        {
            foreach (var handle in _pins) if (handle.IsAllocated) handle.Free();
            foreach (var value in _allocations) if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
            _pins.Clear(); _allocations.Clear();
        }
    }
}
