// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    public class SpatialBlendAbiTests
    {
        [Test]
        [Category("DeviceFree")]
        public void AnchorImports_IncludeTheNativeParentParameter()
        {
            // Independent C header contract: space, actor, name, x, y, z, parent.
            // Omitting the final pointer reads an arbitrary register on native
            // entry even when every struct layout check happens to pass.
            foreach (string library in new[] { "Release", "DebugApi" })
            {
                Type imports = typeof(SpatialBlendAPI).GetNestedType(library, BindingFlags.NonPublic);
                MethodInfo method = imports.GetMethod("sb_add_anchor", BindingFlags.Static | BindingFlags.NonPublic);
                ParameterInfo[] args = method.GetParameters();
                Assert.That(args.Length, Is.EqualTo(7), library);
                Assert.That(args[6].ParameterType, Is.EqualTo(typeof(string)), library);
            }
        }

        [Test]
        [Category("NativeIntegration")]
        public void RealizationTargetLayout_MatchesNativeLibrary()
        {
            SpatialBlendAPI.Initialize();
            var native = SpatialBlendAPI.GetNativeAbiLayout();
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(native), Is.Empty);
        }

        [Test]
        [Category("DeviceFree")]
        public void RealizationTargetLayout_MatchesAuthored64BitAbi2Offsets()
        {
            // This ABI fixture is deliberately independent of Marshal.OffsetOf.
            // NativeIntegration above remains the cross-language authority.
            if (IntPtr.Size != 8)
            {
                Assert.Ignore("The authored fixture describes the 64-bit host ABI.");
            }
            var native = new SbV2AbiLayout
            {
                descriptor_size = 64, abi_version = 4, abi_minor = 12,
                realization_target_size = 72, pointer_size = 8,
                kernel_type_size = 4, arrival_time_offset = 0,
                anchor_name_offset = 8, position_x_offset = 16,
                position_y_offset = 24, position_z_offset = 32,
                yaw_offset = 40, weight_offset = 48, has_z_offset = 56,
                kernel_type_offset = 60, referent_id_offset = 64
            };
            // 8-byte alignment: the final int64 ends at byte 72.
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(native), Is.Empty);
        }

        [Test]
        [Category("DeviceFree")]
        public void LayoutValidator_AcceptsMatchingManagedDescriptor()
        {
            SbV2AbiLayout authored = GetExpectedLayout();
            var mismatches = SpatialBlendAPI.GetAbiLayoutMismatches(authored);

            Assert.That(mismatches, Is.Empty,
                "Validator rejected a descriptor matching the managed layout.");
        }

        [Test]
        [Category("DeviceFree")]
        public void LayoutValidator_ReportsAbiVersionMismatch()
        {
            SbV2AbiLayout authored = GetExpectedLayout();
            authored.abi_version += 1;

            var mismatches = SpatialBlendAPI.GetAbiLayoutMismatches(authored);

            Assert.That(mismatches, Has.Some.Contains("abi_version"));
        }

        [Test]
        [Category("DeviceFree")]
        public void LayoutValidator_RequiresAbi4AndAcceptsLaterMinors()
        {
            SbV2AbiLayout authored = GetExpectedLayout();
            Assert.That(SpatialBlendAPI.ExpectedAbiMajor, Is.EqualTo(4u), "The managed ABI descriptor reports major 4.");
            Assert.That(authored.abi_version, Is.EqualTo(4u));
            authored.abi_version = 3;
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(authored), Has.Some.Contains("abi_version"),
                "ABI 3 is refused: the binding mirrors ABI 4.");
            authored.abi_version = 4;
            authored.abi_minor = SpatialBlendAPI.RequiredAbiMinor;
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(authored), Is.Empty);
            authored.abi_minor = 3;
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(authored), Has.Some.Contains("abi_minor"),
                "ABI 4.3 lacks the source-side transfer fact and judges shape equivalence at declaration (4.4).");
            authored.abi_minor = SpatialBlendAPI.RequiredAbiMinor - 1;
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(authored), Has.Some.Contains("abi_minor"),
                "ABI 4.11 has no signed turn: the driver turns by the proposal's yaw_turn (4.12).");
            authored.abi_minor = SpatialBlendAPI.RequiredAbiMinor + 1;
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(authored), Is.Empty);
        }

        [Test]
        [Category("DeviceFree")]
        public void LayoutValidator_ReportsOffsetMismatch()
        {
            SbV2AbiLayout authored = GetExpectedLayout();
            authored.position_z_offset += 1;

            var mismatches = SpatialBlendAPI.GetAbiLayoutMismatches(authored);

            Assert.That(mismatches, Has.Some.Contains("position_z_offset"));
        }

        [Test]
        [Category("Conversion")]
        public void ConvertRealizationTarget_MapsZUpPositionAndHeight()
        {
            IntPtr anchorName = Marshal.StringToHGlobalAnsi("hand_r");
            try
            {
                var native = new SbRealizationTarget
                {
                    arrival_time = 1.5,
                    anchor_name = anchorName,
                    position_x = 1.25,
                    position_y = -2.5,
                    position_z = 0.75,
                    yaw = Math.PI / 2.0,
                    weight = 0.8,
                    has_z = 1,
                    kernel_type = SbKernelType.Facing,
                    referent_id = 77
                };

                bool ok = SpatialBlendAPI.TryConvertRealizationTarget(native, out var managed);
                Assert.That(ok, Is.True);
                Assert.That(managed.ArrivalTime, Is.EqualTo(1.5));
                Assert.That(managed.AnchorName, Is.EqualTo("hand_r"));
                Assert.That(managed.Position.x, Is.EqualTo(1.25f).Within(1e-6f));
                Assert.That(managed.Position.y, Is.EqualTo(0.75f).Within(1e-6f));
                Assert.That(managed.Position.z, Is.EqualTo(-2.5f).Within(1e-6f));
                Assert.That(managed.HasHeight, Is.True);
                Assert.That(managed.KernelType, Is.EqualTo(SbKernelType.Facing));
                Assert.That(managed.ReferentId, Is.EqualTo(77));
                Assert.That(managed.Weight, Is.EqualTo(0.8).Within(1e-6f));
                // Sb +Y heading maps to Unity +Z: zero Unity yaw.
                Assert.That(Quaternion.Angle(managed.Rotation, Quaternion.identity), Is.LessThan(1e-5f));
            }
            finally
            {
                Marshal.FreeHGlobal(anchorName);
            }
        }

        [Test]
        [Category("Conversion")]
        public void ConvertRealizationTarget_HeightFlagIsPreservedWhenFalse()
        {
            var native = new SbRealizationTarget
            {
                arrival_time = 0.5,
                anchor_name = IntPtr.Zero,
                position_x = -3.0,
                position_y = 4.0,
                position_z = 12.0,
                yaw = 1.0,
                weight = 1.0,
                has_z = 0,
                kernel_type = SbKernelType.Spacing,
                referent_id = 202
            };

            bool ok = SpatialBlendAPI.TryConvertRealizationTarget(native, out var managed);

            Assert.That(ok, Is.True);
            Assert.That(managed.HasHeight, Is.False);
            Assert.That(managed.ReferentId, Is.EqualTo(202));
            Assert.That(managed.KernelType, Is.EqualTo(SbKernelType.Spacing));
        }

        [Test]
        [Category("Conversion")]
        public void ConvertRealizationTarget_RejectsInvalidValues()
        {
            Assert.That(
                SpatialBlendAPI.TryConvertRealizationTarget(
                    new SbRealizationTarget
                    {
                        position_x = 1,
                        position_y = 2,
                        position_z = double.PositiveInfinity,
                        yaw = 0.5,
                        weight = 0.5
                    },
                    out _),
                Is.False);

            Assert.That(
                SpatialBlendAPI.TryConvertRealizationTarget(
                    new SbRealizationTarget
                    {
                        position_x = 1,
                        position_y = 2,
                        position_z = 3,
                        yaw = double.NaN,
                        weight = 0.5
                    },
                    out _),
                Is.False);

            Assert.That(
                SpatialBlendAPI.TryConvertRealizationTarget(
                    new SbRealizationTarget
                    {
                        position_x = 1,
                        position_y = 2,
                        position_z = 3,
                        yaw = 0.5,
                        weight = -0.1
                    },
                    out _),
                Is.False);
        }

        [Test]
        [Category("Conversion")]
        public void ConvertRealizationTarget_RejectsFiniteDoublePositionOverflow()
        {
            var native = new SbRealizationTarget { weight = 1.0 };
            native.position_x = double.MaxValue;
            Assert.That(SpatialBlendAPI.TryConvertRealizationTarget(native, out _), Is.False);
            native.position_x = 0.0;
            native.position_y = -double.MaxValue;
            Assert.That(SpatialBlendAPI.TryConvertRealizationTarget(native, out _), Is.False);
            native.position_y = 0.0;
            native.position_z = double.MaxValue;
            Assert.That(SpatialBlendAPI.TryConvertRealizationTarget(native, out _), Is.False);
        }

        [Test]
        [Category("Conversion")]
        public void ConvertRealizationTarget_ReducesFiniteYawBeforeFloatConversion()
        {
            var native = new SbRealizationTarget
            {
                yaw = double.MaxValue, weight = 1.0
            };
            Assert.That(SpatialBlendAPI.TryConvertRealizationTarget(native, out var managed), Is.True);
            Assert.That(float.IsNaN(managed.Rotation.y) || float.IsInfinity(managed.Rotation.y), Is.False);
            Assert.That(float.IsNaN(managed.Rotation.w) || float.IsInfinity(managed.Rotation.w), Is.False);
            Assert.That(Quaternion.Dot(managed.Rotation, managed.Rotation), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        [Category("DeviceFree")]
        public void TargetsWrapper_UsesRegisteredOpaqueTokenRatherThanZeroUserData()
        {
            const long space = 987654;
            int calls = 0;
            OnTargetsDelegate callback = (_, __, targets) => calls++;
            Type api = typeof(SpatialBlendAPI);
            var callbacks = (IDictionary)api.GetField("targetCallbacks",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var tokens = (IDictionary)api.GetField("targetCallbackTokens",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            MethodInfo register = api.GetMethod("RegisterCallback",
                BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(OnTargetsDelegate));
            MethodInfo wrapper = api.GetMethod("OnTargetsWrapper",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo invalidate = api.GetMethod("InvalidateCallbacksForSpace",
                BindingFlags.Static | BindingFlags.NonPublic);
            long token = 0;
            try
            {
                token = (long)register.Invoke(null, new object[] { callbacks, tokens, space, callback });
                Assert.That(token, Is.Not.EqualTo(0));

                wrapper.Invoke(null, new object[] { space, 1L, IntPtr.Zero, 0L, IntPtr.Zero });
                wrapper.Invoke(null, new object[] { space, 1L, IntPtr.Zero, 0L, new IntPtr(token) });
                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                invalidate.Invoke(null, new object[] { space });
            }
        }

        private static SbV2AbiLayout GetExpectedLayout()
        {
            return new SbV2AbiLayout
            {
                descriptor_size = (uint)Marshal.SizeOf<SbV2AbiLayout>(),
                abi_version = 4,
                abi_minor = 12,
                realization_target_size = (uint)Marshal.SizeOf<SbRealizationTarget>(),
                pointer_size = (uint)IntPtr.Size,
                kernel_type_size = (uint)sizeof(int),
                arrival_time_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("arrival_time").ToInt64(),
                anchor_name_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("anchor_name").ToInt64(),
                position_x_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("position_x").ToInt64(),
                position_y_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("position_y").ToInt64(),
                position_z_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("position_z").ToInt64(),
                yaw_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("yaw").ToInt64(),
                weight_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("weight").ToInt64(),
                has_z_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("has_z").ToInt64(),
                kernel_type_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("kernel_type").ToInt64(),
                referent_id_offset = (uint)Marshal.OffsetOf<SbRealizationTarget>("referent_id").ToInt64()
            };
        }
    }

    [TestFixture]
    [Category("DeviceFree")]
    public class SpatialBlendPipelineTests
    {
        [Test]
        public void PipelineKey_UsesAnchorKernelAndReferent()
        {
            var rootSpacing =
                new RealizationPipelineKey("root", SbKernelType.Spacing, 200);
            var same =
                new RealizationPipelineKey("root", SbKernelType.Spacing, 200);
            var handSpacing =
                new RealizationPipelineKey("hand_r", SbKernelType.Spacing, 200);
            var rootFacing =
                new RealizationPipelineKey("root", SbKernelType.Facing, 200);
            var otherReferent =
                new RealizationPipelineKey("root", SbKernelType.Spacing, 201);

            Assert.That(rootSpacing, Is.EqualTo(same));
            Assert.That(rootSpacing.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(rootSpacing, Is.Not.EqualTo(handSpacing));
            Assert.That(rootSpacing, Is.Not.EqualTo(rootFacing));
            Assert.That(rootSpacing, Is.Not.EqualTo(otherReferent));
        }

        [Test]
        public void PipelineKey_HasDeterministicOrdinalOrdering()
        {
            var hand =
                new RealizationPipelineKey("hand_r", SbKernelType.Facing, 200);
            var root =
                new RealizationPipelineKey("root", SbKernelType.Spacing, 200);

            Assert.That(hand.CompareTo(root), Is.LessThan(0));
            Assert.That(root.CompareTo(hand), Is.GreaterThan(0));
        }

        [Test]
        public void RealizationTarget_ExposesItsPipelineKey()
        {
            var target = new RealizationTarget
            {
                AnchorName = "hand_r",
                KernelType = SbKernelType.Facing,
                ReferentId = 200
            };

            Assert.That(
                target.PipelineKey,
                Is.EqualTo(new RealizationPipelineKey(
                    "hand_r", SbKernelType.Facing, 200)));
        }
    }
}
#endif
