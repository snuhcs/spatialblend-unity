// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The managed mirror of <c>spatialblend_containment_transfer.h</c> without a native library: sizes
    /// and offsets as the header declares them, the negotiation that refuses a mismatch, and the
    /// conversion into Unity axes. The same layout is checked against the real library by the native
    /// case <c>ContainmentTransferBindingsMatchTheCoreLayoutAndOffsets</c> and by the registered
    /// <c>sb_get_containment_transfer_field_offset</c> probes.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ContainmentTransferBindingTests
    {
        // From the header (ABI 4): sb_containment_transfer_feature_t is 4 x uint32 (16 bytes). The per-actor state
        // (sb_actor_containment_t, sb_get_actor_containment) is removed with the contained pose.
        private static readonly uint[] FeatureOffsets = { 0, 4, 8, 12 };

        private static SbContainmentTransferFeature GoodFeature() => new SbContainmentTransferFeature
        {
            descriptor_size = 16,
            feature_version = 4,
            coordinate_convention = 1,
            reserved = 0,
        };

        private static uint Count(uint kind) => kind == 1 ? (uint)FeatureOffsets.Length : 0u;

        private static uint Offset(uint kind, uint index) =>
            kind == 1 && index < FeatureOffsets.Length ? FeatureOffsets[index] : uint.MaxValue;

        [Test]
        public void ManagedStructsHaveTheHeaderSizes()
        {
            Assert.That(Marshal.SizeOf<SbContainmentTransferFeature>(), Is.EqualTo(16));
            Assert.That(SpatialBlendAPI.ContainmentFeatureVersion, Is.EqualTo(4u));
        }

        [Test]
        public void ManagedOffsetsAreTheHeaderOffsetsInDeclarationOrder()
        {
            AssertOffsets(typeof(SbContainmentTransferFeature), SpatialBlendAPI.ContainmentFeatureFields, FeatureOffsets);
        }

        private static void AssertOffsets(Type managed, string[] names, uint[] expected)
        {
            Assert.That(names.Length, Is.EqualTo(expected.Length), managed.Name);
            FieldInfo[] declared = managed.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(declared.Length, Is.EqualTo(names.Length), managed.Name + " has exactly the header's fields");
            for (int index = 0; index < names.Length; ++index)
            {
                Assert.That(declared[index].Name, Is.EqualTo(names[index]), managed.Name + " field " + index);
                Assert.That((uint)Marshal.OffsetOf(managed, names[index]).ToInt32(), Is.EqualTo(expected[index]),
                    managed.Name + "." + names[index]);
            }
        }

        [Test]
        public void NegotiationAcceptsTheCoreLayout()
        {
            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(GoodFeature(), 16, Count, Offset, out string error),
                Is.True, error);
        }

        [Test]
        public void NegotiationRefusesAnyMismatchWithAReason()
        {
            SbContainmentTransferFeature feature;

            feature = GoodFeature();
            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(feature, 8, Count, Offset, out string error), Is.False);
            Assert.That(error, Does.Contain("size"));

            feature = GoodFeature();
            feature.feature_version = 3;
            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(feature, 16, Count, Offset, out error), Is.False);
            Assert.That(error, Does.Contain("version"), "Feature version 3 is the ABI 4.3 feature without the source-side transfer fact.");

            feature = GoodFeature();
            feature.coordinate_convention = 2;
            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(feature, 16, Count, Offset, out error), Is.False);
            Assert.That(error, Does.Contain("Z-up"));

            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(GoodFeature(), 16,
                kind => Count(kind) + 1u, Offset, out error), Is.False);
            Assert.That(error, Does.Contain("field count"));

            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(GoodFeature(), 16, Count,
                (kind, index) => kind == 1 && index == 2 ? 40u : Offset(kind, index), out error), Is.False);
            Assert.That(error, Does.Contain("coordinate_convention"), "The failing field is named.");

            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(GoodFeature(), 16, null, Offset, out error), Is.False);
            Assert.That(SpatialBlendAPI.ValidateContainmentFeature(GoodFeature(), 16, Count, null, out error), Is.False);
        }

        [Test]
        public void TheTransferFlagIsTheOnlyContainedStateTheBindingReads()
        {
            // ABI 4.3: the contained state is the TRANSFER flag of the root proposal.
            Assert.That((uint)AnchorProposalFlags.Transfer, Is.EqualTo(0x20u)); // SB_ANCHOR_PROPOSAL_FLAG_TRANSFER
            var proposal = new AnchorProposal { Flags = AnchorProposalFlags.Transfer };
            Assert.That(proposal.Transfer, Is.True);
            Assert.That(proposal.TransferArrived, Is.False, "The proposal alone is not the realised body.");
            proposal.Flags |= AnchorProposalFlags.Arrived;
            Assert.That(proposal.TransferArrived, Is.True);
            proposal.Flags = AnchorProposalFlags.Arrived;
            Assert.That(proposal.TransferArrived, Is.False, "ARRIVED without TRANSFER is an ordinary arrival.");
            // Shape equivalence is part of the flag (ABI 4.4), not a declaration refusal: no scene status exists for it.
            Assert.That(Enum.GetValues(typeof(SbSceneStatus)).Length, Is.EqualTo(4));
        }

        [Test]
        public void ThereIsNoContainedPoseOrAlphaOrHandoverOrStateReadLeftInTheBinding()
        {
            string binding = System.IO.File.ReadAllText(
                "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.ContainmentTransfer.cs");
            foreach (string removed in new[]
            {
                "sb_get_actor_containment", "SbActorContainment", "SbContainmentState", "Handover", "Alpha",
                "WorldPosition", "WorldYawDegrees", "TryGetActorContainment", "blend_band_m", "state_size",
            })
            {
                Assert.That(binding, Does.Not.Contain(removed), removed + " is removed with the contained pose (ABI 4)");
            }
        }

        [Test]
        public void AMissingLibraryIsReportedAsUnavailableNeverThrown()
        {
            Assert.DoesNotThrow(() => SpatialBlendAPI.TryValidateContainmentAbi(out _));
            // The writers fall back to false instead of throwing on an older library.
            Assert.DoesNotThrow(() => SpatialBlendAPI.TrySetElementContainmentTransfer(-1, -1, true));
        }

        [Test]
        public void EveryHeaderEntryPointIsBoundOnBothLibrariesAndThereIsNoContainedEventLeft()
        {
            string binding = System.IO.File.ReadAllText(
                "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.ContainmentTransfer.cs");
            foreach (string entry in new[]
            {
                "sb_get_containment_transfer_feature", "sb_get_containment_transfer_field_count",
                "sb_get_containment_transfer_field_offset", "sb_set_element_local_polygon",
                "sb_set_element_containment_transfer", "sb_get_source_transfer",
            })
            {
                Assert.That(System.Text.RegularExpressions.Regex.Matches(binding, "extern [^;]*" + entry + "\\(").Count,
                    Is.EqualTo(2), entry + " is declared for the release and the debug library");
            }
            Assert.That(binding, Does.Contain("RELEASE_LIBRARY"));
            Assert.That(binding, Does.Contain("DEBUG_LIBRARY"));

            // ABI 4.3 removed the contained event and everything that existed only for it: the entry point, the delegate, the
            // event type, the token-fenced registration and its teardown.
            string api = System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.cs");
            foreach (string removed in new[]
            {
                "sb_register_on_containment_event", "OnContainmentEventNativeDelegate", "OnContainmentEventDelegate",
                "ContainmentEvent", "containmentEventCallbacks", "containmentCallbackSpaceByToken",
                "DropContainmentCallback", "ReleaseContainmentCallbackNative", "ForgetContainmentCallbacks",
                "ContainmentCallbackCount", "TryRegisterOnContainmentEvent", "UnregisterOnContainmentEvent",
            })
            {
                Assert.That(binding, Does.Not.Contain(removed), removed);
                Assert.That(api, Does.Not.Contain(removed), removed);
            }
            // The binding's only managed-to-native callback was the event's wrapper.
            Assert.That(binding, Does.Not.Contain("MonoPInvokeCallback"));
        }
    }
}
#endif
