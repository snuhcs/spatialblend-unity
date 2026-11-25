// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// ABI and layout checks for the per-anchor port bindings (ABI 4): sb_get_anchor_id / sb_get_anchor_name,
    /// sb_publish_anchor_pose, sb_read_proposals, sb_raise_source_exception, sb_poll_actor_event and sb_query_course.
    /// The expected shapes come from spatialblend.h and spatialblend_type.h for ABI 4 (offsets and sizes computed
    /// from that header's declarations), not from the managed declarations. The same sizes and every field offset are
    /// compared with the real library by <see cref="FoundationEmbodiedPortNativeTests"/> through sb_sizeof and
    /// sb_get_struct_field_offset.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationEmbodiedPortBindingTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        [Test]
        public void AnchorPoseMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbAnchorPose>(), Is.EqualTo(72));
            AssertOffsets(typeof(SbAnchorPose),
                ("struct_size", 0), ("flags", 4), ("x", 8), ("y", 16), ("z", 24), ("orientation", 32), ("epoch", 64));
            Assert.That(SpatialBlendAPI.EmbodiedTeleported, Is.EqualTo(0x1u));
        }

        [Test]
        public void AnchorProposalMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbAnchorProposal>(), Is.EqualTo(144));
            AssertOffsets(typeof(SbAnchorProposal),
                ("struct_size", 0), ("anchor_id", 4), ("status", 8), ("flags", 12), ("cause", 16), ("reserved", 20),
                ("x", 24), ("y", 32), ("z", 40), ("orientation", 48), ("err_spacing_target", 80),
                ("err_spacing_sample", 88), ("err_facing_target", 96), ("err_facing_sample", 104),
                ("err_pose_target", 112), ("err_pose_sample", 120), ("err_scale_m", 128), ("referent_shared_id", 136));
            Assert.That(Marshal.SizeOf<SbOrientation>(), Is.EqualTo(32));
            AssertOffsets(typeof(SbOrientation), ("struct_size", 0), ("type", 4), ("yaw", 8), ("pitch", 16), ("roll", 24));
        }

        [Test]
        public void ProposalSetMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbProposalSet>(), Is.EqualTo(64));
            AssertOffsets(typeof(SbProposalSet),
                ("struct_size", 0), ("anchor_count", 4), ("total_anchor_count", 8), ("sample_valid", 12), ("revision", 16),
                ("source_sequence", 24), ("sample_sequence", 32), ("sample_epoch", 40), ("sample_timestamp_s", 48),
                ("sample_age_s", 56));
        }

        [Test]
        public void AnchorSolveMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbAnchorSolve>(), Is.EqualTo(80));
            AssertOffsets(typeof(SbAnchorSolve),
                ("struct_size", 0), ("anchor_id", 4), ("flags", 8), ("iterations", 12), ("object_shared_id", 16),
                ("sd_scale_m", 24), ("objective_j", 32), ("facing_yaw", 40), ("driving_pose_tag", 48));
        }

        [Test]
        public void AnchorTurnMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbAnchorTurn>(), Is.EqualTo(32));
            AssertOffsets(typeof(SbAnchorTurn),
                ("struct_size", 0), ("anchor_id", 4), ("flags", 8), ("reserved", 12), ("yaw", 16), ("yaw_turn", 24));
        }

        // The turn is a second call: it is accepted only when it answers the proposal just read, and its sign flips into Unity yaw.
        [Test]
        public void ASignedTurnPairsOnlyWithTheProposalItAnswersAndFlipsIntoUnityYaw()
        {
            SbAnchorProposal proposal = NativeProposal(EmbodiedStatus.Exact);
            proposal.anchor_id = 3; proposal.flags = (uint)AnchorProposalFlags.Sampled; proposal.orientation.yaw = 0.7;
            var turn = new SbAnchorTurn { struct_size = 32, anchor_id = 3, flags = proposal.flags, yaw = 0.7, yaw_turn = 3.56 };

            Assert.That(SpatialBlendAPI.TryConvertAnchorTurn(turn, proposal, out float degrees), Is.True);
            Assert.That(degrees, Is.EqualTo(-3.56f * 180f / Mathf.PI).Within(1e-3f), "a turn above pi keeps its size; its sign flips with the yaw map");
            Assert.That(degrees, Is.LessThan(-180f));

            SbAnchorTurn other = turn; other.yaw = 0.71;
            Assert.That(SpatialBlendAPI.TryConvertAnchorTurn(other, proposal, out _), Is.False, "another proposal's turn (the set was replaced in between)");
            other = turn; other.anchor_id = 4;
            Assert.That(SpatialBlendAPI.TryConvertAnchorTurn(other, proposal, out _), Is.False);
            other = turn; other.flags = 0;
            Assert.That(SpatialBlendAPI.TryConvertAnchorTurn(other, proposal, out _), Is.False);
            other = turn; other.yaw_turn = double.NaN;
            Assert.That(SpatialBlendAPI.TryConvertAnchorTurn(other, proposal, out _), Is.False);
        }

        [Test]
        public void ActorEventMirrorsTheCStruct()
        {
            Assert.That(Marshal.SizeOf<SbActorEvent>(), Is.EqualTo(360));
            AssertOffsets(typeof(SbActorEvent),
                ("struct_size", 0), ("kind", 4), ("revision", 8), ("source_sequence", 16), ("source_shared_id", 24),
                ("exception_id", 32), ("payload_size", 36), ("interaction_kind", 40), ("anchor_id", 44), ("x", 48),
                ("y", 56), ("z", 64), ("orientation", 72), ("payload", 104));
            Assert.That(SpatialBlendAPI.ExceptionPayloadMax, Is.EqualTo(256u), "SB_EXCEPTION_PAYLOAD_MAX");
            Assert.That(SpatialBlendAPI.ExceptionJoin, Is.EqualTo(1u), "SB_EXCEPTION_JOIN");
        }

        [Test]
        public void TheFieldNameTablesAreInDeclarationOrderAndCoverEveryManagedField()
        {
            // sb_get_struct_field_offset indexes fields in declaration order: the tables the native check uses must be
            // exactly the managed structs' fields, in order.
            AssertNames(typeof(SbAnchorPose), SpatialBlendAPI.AnchorPoseFields);
            AssertNames(typeof(SbAnchorProposal), SpatialBlendAPI.AnchorProposalFields);
            AssertNames(typeof(SbProposalSet), SpatialBlendAPI.ProposalSetFields);
            AssertNames(typeof(SbActorEvent), SpatialBlendAPI.ActorEventFields);
            AssertNames(typeof(SbAnchorSolve), SpatialBlendAPI.AnchorSolveFields);
            AssertNames(typeof(SbAnchorTurn), SpatialBlendAPI.AnchorTurnFields);
            AssertNames(typeof(SbTargetRequestPayload), SpatialBlendAPI.TargetRequestPayloadFields);
        }

        [Test]
        public void StructIdsMirrorTheHeader()
        {
            Assert.That((int)SbStructId.Orientation, Is.EqualTo(21));
            Assert.That((int)SbStructId.AnchorPose, Is.EqualTo(22));       // SB_STRUCT_ANCHOR_POSE
            Assert.That((int)SbStructId.AnchorProposal, Is.EqualTo(23));   // SB_STRUCT_ANCHOR_PROPOSAL
            Assert.That((int)SbStructId.ProposalSet, Is.EqualTo(24));      // SB_STRUCT_PROPOSAL_SET
            Assert.That((int)SbStructId.ActorEvent, Is.EqualTo(25));       // SB_STRUCT_ACTOR_EVENT
            Assert.That((int)SbStructId.AnchorSolve, Is.EqualTo(30));      // SB_STRUCT_ANCHOR_SOLVE (ABI 4.11)
            Assert.That((int)SbStructId.AnchorTurn, Is.EqualTo(31));       // SB_STRUCT_ANCHOR_TURN (ABI 4.12)
            Assert.That((int)SbStructId.TargetRequestPayload, Is.EqualTo(32)); // SB_STRUCT_TARGET_REQUEST_PAYLOAD (ABI 4.13)
            // 19 (embodied pose) and 20 (proposal) are removed and not reused.
            Assert.That(Enum.IsDefined(typeof(SbStructId), 19), Is.False);
            Assert.That(Enum.IsDefined(typeof(SbStructId), 20), Is.False);
        }

        [Test]
        public void StatusFlagAndCauseValuesMirrorTheHeader()
        {
            Assert.That((int)EmbodiedStatus.None, Is.EqualTo(0));         // SB_PROPOSAL_NONE
            Assert.That((int)EmbodiedStatus.NoSample, Is.EqualTo(1));     // SB_PROPOSAL_NO_SAMPLE
            Assert.That((int)EmbodiedStatus.Exact, Is.EqualTo(2));        // SB_PROPOSAL_EXACT
            Assert.That((int)EmbodiedStatus.Inexact, Is.EqualTo(3));      // SB_PROPOSAL_INEXACT
            Assert.That((int)EmbodiedStatus.NotConverged, Is.EqualTo(5)); // SB_PROPOSAL_NOT_CONVERGED; 4 (HOLD) is retired
            Assert.That(Enum.IsDefined(typeof(EmbodiedStatus), 4), Is.False);

            Assert.That((uint)AnchorProposalFlags.YawUnspecified, Is.EqualTo(0x1u)); // SB_ANCHOR_PROPOSAL_FLAG_YAW_UNSPECIFIED
            Assert.That((uint)AnchorProposalFlags.Engaged, Is.EqualTo(0x2u));        // _ENGAGED
            Assert.That((uint)AnchorProposalFlags.Sampled, Is.EqualTo(0x4u));        // _SAMPLED
            Assert.That((uint)AnchorProposalFlags.Arrived, Is.EqualTo(0x8u));        // _ARRIVED
            Assert.That((uint)AnchorProposalFlags.PositionUnspecified, Is.EqualTo(0x10u)); // _POSITION_UNSPECIFIED (ABI 4.1, R4)
            Assert.That((uint)AnchorProposalFlags.Transfer, Is.EqualTo(0x20u));            // _TRANSFER (ABI 4.3)
            Assert.That((uint)AnchorProposalFlags.Inner, Is.EqualTo(0x40u));               // _INNER (ABI 4.11)
            Assert.That((uint)AnchorProposalFlags.FacingYaw, Is.EqualTo(0x80u));           // _FACING_YAW (ABI 4.11)

            Assert.That((uint)EmbodiedCause.FirstProposal, Is.EqualTo(0x001u));
            Assert.That((uint)EmbodiedCause.WeightSwitch, Is.EqualTo(0x002u));
            Assert.That((uint)EmbodiedCause.ReferentSetChange, Is.EqualTo(0x004u));
            Assert.That((uint)EmbodiedCause.SourceException, Is.EqualTo(0x010u), "The old SOURCE_DISCONTINUITY bit, renamed.");
            Assert.That((uint)EmbodiedCause.ContainEnter, Is.EqualTo(0x020u));
            Assert.That((uint)EmbodiedCause.ContainExit, Is.EqualTo(0x040u));
            Assert.That((uint)EmbodiedCause.SeatEngage, Is.EqualTo(0x080u));
            Assert.That((uint)EmbodiedCause.SeatRelease, Is.EqualTo(0x100u));
            Assert.That((uint)EmbodiedCause.EmbodiedReset, Is.EqualTo(0x200u));
            Assert.That((uint)EmbodiedCause.FacingRootSwitch, Is.EqualTo(0x400u));
            Assert.That((uint)EmbodiedCause.StatusChange, Is.EqualTo(0x800u));
            Assert.That((uint)EmbodiedCause.SceneChange, Is.EqualTo(0x1000u));
            Assert.That((uint)EmbodiedCause.ClampBranch, Is.EqualTo(0x2000u));
            Assert.That((uint)EmbodiedCause.DecodeBranch, Is.EqualTo(0x4000u));
            Assert.That((uint)EmbodiedCause.Progress, Is.EqualTo(0x8000u));
            Assert.That((uint)EmbodiedCause.ChartSnap, Is.EqualTo(0x10000u));
            Assert.That((uint)EmbodiedCause.NoRequest, Is.EqualTo(0x20000u));
            Assert.That((uint)EmbodiedCause.FacingTie, Is.EqualTo(0x40000u));
            Assert.That((uint)EmbodiedCause.Fold, Is.EqualTo(0x80000u), "ABI 4.11 FOLD. The old 0x8 FOLD / BASIN_LOST stays removed.");
            Assert.That(Enum.IsDefined(typeof(EmbodiedCause), 0x8u), Is.False);

            Assert.That((int)ActorEventKind.SourceException, Is.EqualTo(1)); // SB_ACTOR_EVENT_SOURCE_EXCEPTION
            Assert.That((int)ActorEventKind.TeleportRequest, Is.EqualTo(2)); // SB_ACTOR_EVENT_TELEPORT_REQUEST
            Assert.That((int)ActorEventKind.PositionRequest, Is.EqualTo(3));
            Assert.That((int)ActorEventKind.YawRequest, Is.EqualTo(4));
            Assert.That((int)ActorEventKind.TargetRelease, Is.EqualTo(5));
            Assert.That(Enum.IsDefined(typeof(ActorEventKind), 6), Is.False, "No synthetic publication COMMIT event.");
            Assert.That((uint)TargetChannels.Position, Is.EqualTo(1u));
            Assert.That((uint)TargetChannels.Yaw, Is.EqualTo(2u));
            Assert.That((uint)TargetRequestFlags.Placement, Is.EqualTo(1u));
        }

        [TestCase("EmbodiedPortRelease")]
        [TestCase("EmbodiedPortDebug")]
        public void AnchorIdImportsMatchTheCSignatures(string imports)
        {
            // int32_t sb_get_anchor_id(space, actor, const char* name, uint32_t* id)
            MethodInfo id = Import(imports, "sb_get_anchor_id");
            Assert.That(id.ReturnType, Is.EqualTo(typeof(int)));
            ParameterInfo[] p = id.GetParameters();
            Assert.That(p.Length, Is.EqualTo(4));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(string)));
            Assert.That(p[3].ParameterType, Is.EqualTo(typeof(uint).MakeByRefType()));
            Assert.That(p[3].IsOut, Is.True);
            AssertPInvoke(id);

            // int32_t sb_get_anchor_name(space, actor, uint32_t anchor_id, char* buf, int32_t cap)
            MethodInfo name = Import(imports, "sb_get_anchor_name");
            Assert.That(name.ReturnType, Is.EqualTo(typeof(int)));
            p = name.GetParameters();
            Assert.That(p.Length, Is.EqualTo(5));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(uint)));
            Assert.That(p[3].ParameterType, Is.EqualTo(typeof(byte[])));
            Assert.That(p[4].ParameterType, Is.EqualTo(typeof(int)));
            AssertPInvoke(name);
        }

        [TestCase("EmbodiedPortRelease")]
        [TestCase("EmbodiedPortDebug")]
        public void PublishImportMatchesTheCSignature(string imports)
        {
            // int32_t sb_publish_anchor_pose(space, actor, uint32_t anchor_id, const sb_anchor_pose_t*, double timestamp_s, uint64_t sequence)
            MethodInfo method = Import(imports, "sb_publish_anchor_pose");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(int)));
            ParameterInfo[] p = method.GetParameters();
            Assert.That(p.Length, Is.EqualTo(6));
            Assert.That(p[0].ParameterType, Is.EqualTo(typeof(long)));
            Assert.That(p[1].ParameterType, Is.EqualTo(typeof(long)));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(uint)));
            Assert.That(p[3].ParameterType, Is.EqualTo(typeof(SbAnchorPose).MakeByRefType()));
            Assert.That(p[4].ParameterType, Is.EqualTo(typeof(double)));
            Assert.That(p[5].ParameterType, Is.EqualTo(typeof(ulong)));
            AssertPInvoke(method);
        }

        [TestCase("EmbodiedPortRelease")]
        [TestCase("EmbodiedPortDebug")]
        public void ReadProposalsImportMatchesTheCSignature(string imports)
        {
            // int32_t sb_read_proposals(space, actor, sb_proposal_set_t* set, sb_anchor_proposal_t* out, int32_t cap)
            MethodInfo method = Import(imports, "sb_read_proposals");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(int)));
            ParameterInfo[] p = method.GetParameters();
            Assert.That(p.Length, Is.EqualTo(5));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(SbProposalSet).MakeByRefType()));
            Assert.That(p[3].ParameterType, Is.EqualTo(typeof(SbAnchorProposal[])));
            Assert.That(p[4].ParameterType, Is.EqualTo(typeof(int)));
            AssertPInvoke(method);
        }

        [TestCase("EmbodiedPortRelease")]
        [TestCase("EmbodiedPortDebug")]
        public void ExceptionEventAndCourseImportsMatchTheCSignatures(string imports)
        {
            // int32_t sb_raise_source_exception(space, source_actor, uint32_t exception_id, const void* payload, uint32_t payload_size)
            MethodInfo raise = Import(imports, "sb_raise_source_exception");
            Assert.That(raise.ReturnType, Is.EqualTo(typeof(int)));
            ParameterInfo[] p = raise.GetParameters();
            Assert.That(p.Length, Is.EqualTo(5));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(uint)));
            Assert.That(p[3].ParameterType, Is.EqualTo(typeof(byte[])));
            Assert.That(p[4].ParameterType, Is.EqualTo(typeof(uint)));
            AssertPInvoke(raise);

            // int32_t sb_poll_actor_event(space, actor, sb_actor_event_t* out)
            MethodInfo poll = Import(imports, "sb_poll_actor_event");
            Assert.That(poll.ReturnType, Is.EqualTo(typeof(int)));
            p = poll.GetParameters();
            Assert.That(p.Length, Is.EqualTo(3));
            Assert.That(p[2].ParameterType, Is.EqualTo(typeof(SbActorEvent).MakeByRefType()));
            AssertPInvoke(poll);

            // int32_t sb_query_course(space, actor, from_x, from_y, to_x, to_y, double* xy_out, int32_t cap)
            MethodInfo course = Import(imports, "sb_query_course");
            Assert.That(course.ReturnType, Is.EqualTo(typeof(int)));
            p = course.GetParameters();
            Assert.That(p.Length, Is.EqualTo(8));
            Assert.That(p[0].ParameterType, Is.EqualTo(typeof(long)));
            Assert.That(p[1].ParameterType, Is.EqualTo(typeof(long)));
            for (int i = 2; i < 6; ++i) Assert.That(p[i].ParameterType, Is.EqualTo(typeof(double)), "parameter " + i);
            Assert.That(p[6].ParameterType, Is.EqualTo(typeof(double[])));
            Assert.That(p[7].ParameterType, Is.EqualTo(typeof(int)));
            AssertPInvoke(course);
        }

        [Test]
        public void TheEntryPointsABI4RemovesAreNotBoundAnywhere()
        {
            foreach (string imports in new[] { "EmbodiedPortRelease", "EmbodiedPortDebug" })
            {
                Type type = typeof(SpatialBlendAPI).GetNestedType(imports, BindingFlags.NonPublic);
                foreach (string removed in new[] { "sb_publish_embodied_pose", "sb_read_proposal", "sb_declare_source_discontinuity" })
                {
                    Assert.That(type.GetMethod(removed, Static), Is.Null, imports + "." + removed);
                }
            }
            string all = string.Concat(
                System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.EmbodiedPort.cs"),
                System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.ContainmentTransfer.cs"));
            foreach (string removed in new[]
            {
                "sb_publish_embodied_pose", "sb_declare_source_discontinuity", "sb_get_actor_containment",
                "sb_set_anchor_gating", "sb_set_engagement_distances", "SbEmbodiedPose", "SbProposal ",
            })
            {
                Assert.That(all, Does.Not.Contain(removed), removed);
            }
        }

        // ------------------------------------------------------------------ conversions

        private static SbAnchorProposal NativeProposal(EmbodiedStatus status, uint anchorId = 0)
        {
            return new SbAnchorProposal
            {
                struct_size = (uint)Marshal.SizeOf<SbAnchorProposal>(),
                anchor_id = anchorId,
                status = (int)status,
                x = 3.0,
                y = 4.0,
                z = 0.0,
                orientation = new SbOrientation { struct_size = 32, type = (int)SbOrientationType.Yaw, yaw = Math.PI / 2.0 },
            };
        }

        [Test]
        public void ATargetConvertsFromZUpRadiansToUnityWithTheAnchorsOwnHeight()
        {
            // Z-up (3, 4, 0.5) is Unity (3, 0.5, 4). Z-up yaw 90 degrees is Unity yaw 0.
            SbAnchorProposal native = NativeProposal(EmbodiedStatus.Exact, 1);
            native.z = 0.5;
            native.cause = (uint)(EmbodiedCause.WeightSwitch | EmbodiedCause.SeatEngage);
            native.flags = (uint)(AnchorProposalFlags.Engaged | AnchorProposalFlags.Sampled | AnchorProposalFlags.Arrived);
            native.referent_shared_id = 9132;

            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal proposal), Is.True);

            Assert.That(proposal.AnchorId, Is.EqualTo(1u));
            Assert.That(proposal.Status, Is.EqualTo(EmbodiedStatus.Exact));
            Assert.That(proposal.Position.x, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(proposal.Position.y, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(proposal.Position.z, Is.EqualTo(4f).Within(1e-5f));
            Assert.That(Mathf.DeltaAngle(proposal.YawDegrees, 0f), Is.EqualTo(0f).Within(1e-3f));
            Assert.That(proposal.Cause, Is.EqualTo(EmbodiedCause.WeightSwitch | EmbodiedCause.SeatEngage));
            Assert.That(proposal.Engaged, Is.True);
            Assert.That(proposal.Sampled, Is.True);
            Assert.That(proposal.Arrived, Is.True);
            Assert.That(proposal.YawUnspecified, Is.False);
            Assert.That(proposal.ReferentSharedId, Is.EqualTo(9132L));
        }

        [Test]
        public void CauseBitsTheBindingDoesNotKnowYetPassThrough()
        {
            SbAnchorProposal native = NativeProposal(EmbodiedStatus.Exact);
            native.cause = 0x010u | 0x2000u | 0x80000000u;

            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal proposal), Is.True);

            Assert.That((proposal.Cause & EmbodiedCause.SourceException) != 0, Is.True);
            Assert.That((uint)proposal.Cause, Is.EqualTo(0x80002010u), "Unknown bits are kept, not rejected.");
        }

        [Test]
        public void TheYawUnspecifiedFlagAndTheSourcesPitchAreBound()
        {
            Assert.That((int)SbOrientationType.Yaw, Is.EqualTo(0));
            Assert.That((int)SbOrientationType.YawPitch, Is.EqualTo(1));
            Assert.That((int)SbOrientationType.YawPitchRoll, Is.EqualTo(2));

            SbAnchorProposal native = NativeProposal(EmbodiedStatus.Exact);
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal plain), Is.True);
            Assert.That(plain.YawUnspecified, Is.False);
            Assert.That(plain.HasPitch, Is.False, "A yaw-only feed carries no pitch.");

            native.flags = (uint)AnchorProposalFlags.YawUnspecified;
            native.orientation.type = (int)SbOrientationType.YawPitch;
            native.orientation.pitch = -10.0 * Math.PI / 180.0;
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal full), Is.True);
            Assert.That(full.YawUnspecified, Is.True);
            Assert.That(full.HasPitch, Is.True);
            Assert.That(full.PitchDegrees, Is.EqualTo(-10f).Within(1e-4f));

            Assert.That(plain.PositionUnspecified, Is.False);
            native.flags = (uint)AnchorProposalFlags.PositionUnspecified;
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal held), Is.True);
            Assert.That(held.PositionUnspecified, Is.True, "Reserved since ABI 4.11 (never emitted): the bit is still mirrored, the driver does not read it.");

            Assert.That(held.Transfer, Is.False);
            native.flags = (uint)(AnchorProposalFlags.Transfer | AnchorProposalFlags.Arrived);
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal transfer), Is.True);
            Assert.That(transfer.Transfer, Is.True, "ABI 4.3: the root proposal is the transfer mapping.");
            Assert.That(transfer.TransferArrived, Is.True, "The realised body satisfies it.");

            native.orientation.pitch = double.NaN;
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal bad), Is.True);
            Assert.That(bad.HasPitch, Is.False, "A non-finite pitch is dropped, never applied.");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void AStatusWithoutATargetCarriesOnlyTheStatusFlagsAndCause(int statusValue)
        {
            var status = (EmbodiedStatus)statusValue;
            SbAnchorProposal native = NativeProposal(status);
            native.x = native.y = native.z = native.orientation.yaw = 0.0;

            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(native, out AnchorProposal proposal), Is.True);

            Assert.That(proposal.Status, Is.EqualTo(status));
            Assert.That(proposal.CarriesTarget, Is.False);
            Assert.That(proposal.Position, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void AnUnknownStatusAndANonFiniteTargetAreRefused()
        {
            SbAnchorProposal unknown = NativeProposal(EmbodiedStatus.Exact);
            unknown.status = 99;
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(unknown, out _), Is.False);
            SbAnchorProposal retired = NativeProposal(EmbodiedStatus.Exact);
            retired.status = 4;   // HOLD is retired
            Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(retired, out _), Is.False);

            foreach (double bad in new[] { double.NaN, double.PositiveInfinity })
            {
                SbAnchorProposal x = NativeProposal(EmbodiedStatus.Exact);
                x.x = bad;
                Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(x, out _), Is.False);
                SbAnchorProposal y = NativeProposal(EmbodiedStatus.Inexact);
                y.y = bad;
                Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(y, out _), Is.False);
                SbAnchorProposal z = NativeProposal(EmbodiedStatus.Exact);
                z.z = bad;
                Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(z, out _), Is.False);
                SbAnchorProposal yaw = NativeProposal(EmbodiedStatus.Exact);
                yaw.orientation.yaw = bad;
                Assert.That(SpatialBlendAPI.TryConvertAnchorProposal(yaw, out _), Is.False);
            }
        }

        [Test]
        public void ASetKeepsTheRootFirstTheRevisionAndEveryAnchorsProposal()
        {
            var header = new SbProposalSet
            {
                struct_size = (uint)Marshal.SizeOf<SbProposalSet>(),
                anchor_count = 2,
                total_anchor_count = 2,
                sample_valid = 1,
                revision = 17,
                source_sequence = 42,
            };
            SbAnchorProposal root = NativeProposal(EmbodiedStatus.Exact, 0);
            SbAnchorProposal pelvis = NativeProposal(EmbodiedStatus.Exact, 3);
            pelvis.z = 0.45;
            var set = new ProposalSet();

            Assert.That(SpatialBlendAPI.TryConvertProposalSet(header, new[] { root, pelvis, default(SbAnchorProposal) }, set), Is.True);

            Assert.That(set.Revision, Is.EqualTo(17UL));
            Assert.That(set.SourceSequence, Is.EqualTo(42L));
            Assert.That(set.SampleValid, Is.True);
            Assert.That(set.Anchors.Count, Is.EqualTo(2), "anchor_count proposals, not the whole buffer.");
            Assert.That(set.HasRoot, Is.True);
            Assert.That(set.Root.AnchorId, Is.EqualTo(0u));
            Assert.That(set.TryGet(3, out AnchorProposal found), Is.True);
            Assert.That(found.Position.y, Is.EqualTo(0.45f).Within(1e-5f));
            Assert.That(set.TryGet(9, out _), Is.False);
        }

        [Test]
        public void ASetWithAnUnknownStatusOnOneAnchorLosesThatRelationOnlyAndReportsIt()
        {
            var header = new SbProposalSet { anchor_count = 2, total_anchor_count = 2, revision = 5 };
            SbAnchorProposal bad = NativeProposal(EmbodiedStatus.Exact, 1);
            bad.status = 99;
            var set = new ProposalSet();

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("anchor 1 has status 99"));
            Assert.That(SpatialBlendAPI.TryConvertProposalSet(header, new[] { NativeProposal(EmbodiedStatus.Exact, 0), bad }, set), Is.True,
                "the set is readable, so the events behind it are polled");

            Assert.That(set.Anchors.Select(a => a.AnchorId), Is.EqualTo(new[] { 0u }), "the relation that could not be read is left out, the other stays");
        }

        [Test]
        public void AnAnchorPoseIsPublishedInZUpRadiansWithItsOwnHeightAndEpoch()
        {
            // Unity (3, 0.45, 4), yaw 0 is Z-up (3, 4, 0.45), yaw pi/2.
            SbAnchorPose native = SpatialBlendAPI.ToNativeAnchorPose(
                new EmbodiedPose(new Vector3(3f, 0.45f, 4f), 0f, 10f), epoch: 7, teleported: true);

            Assert.That(native.struct_size, Is.EqualTo(72u));
            Assert.That(native.x, Is.EqualTo(3.0).Within(1e-6));
            Assert.That(native.y, Is.EqualTo(4.0).Within(1e-6));
            Assert.That(native.z, Is.EqualTo(0.45).Within(1e-6));
            Assert.That(native.orientation.yaw, Is.EqualTo(Math.PI / 2.0).Within(1e-6));
            Assert.That(native.orientation.type, Is.EqualTo((int)SbOrientationType.YawPitch));
            Assert.That(native.orientation.pitch, Is.EqualTo(10.0 * Math.PI / 180.0).Within(1e-6));
            Assert.That(native.epoch, Is.EqualTo(7UL));
            Assert.That(native.flags, Is.EqualTo(SpatialBlendAPI.EmbodiedTeleported));

            SbAnchorPose rootPose = SpatialBlendAPI.ToNativeAnchorPose(new EmbodiedPose(Vector3.zero, 0f), 1, false);
            Assert.That(rootPose.orientation.type, Is.EqualTo((int)SbOrientationType.Yaw), "A root carries a yaw only.");
            Assert.That(rootPose.flags, Is.EqualTo(0u));
        }

        [TestCase(0.0)]
        [TestCase(90.0)]
        [TestCase(-90.0)]
        [TestCase(1440.123456789)]
        [TestCase(-1440.123456789)]
        [TestCase(1234567890.1234567)]
        [TestCase(-1234567890.1234567)]
        [TestCase(double.MaxValue)]
        [TestCase(-double.MaxValue)]
        public void ContinuousDoubleYawUsesTheCanonicalBasisWithoutNarrowingOrWrapping(double unityYaw)
        {
            // Legacy yaw is deliberately unusable: the explicit committed double is authoritative.
            var pose = new EmbodiedPose(new Vector3(3f, 0.45f, 4f), float.NaN, 10f);
            SbAnchorPose native = SpatialBlendAPI.ToNativeAnchorPose(pose, unityYaw, 7, true);
            double expected = (-unityYaw + 90.0) * (Math.PI / 180.0);
            Assert.That(native.orientation.yaw, Is.EqualTo(expected));
            Assert.That(double.IsNaN(native.orientation.yaw) || double.IsInfinity(native.orientation.yaw), Is.False);
            Assert.That(native.x, Is.EqualTo(3.0));
            Assert.That(native.y, Is.EqualTo(4.0));
            Assert.That(native.z, Is.EqualTo((double)pose.Position.y));
            Assert.That(native.orientation.pitch, Is.EqualTo(HeadOrientation.PitchRadiansFromDegrees(10f)));
            Assert.That(native.epoch, Is.EqualTo(7UL));
            Assert.That(native.flags, Is.EqualTo(SpatialBlendAPI.EmbodiedTeleported));
            if (Math.Abs(unityYaw) > 720.0)
                Assert.That(Math.Abs(native.orientation.yaw), Is.GreaterThan(2.0 * Math.PI));
            if (Math.Abs(unityYaw) < 1e12 && unityYaw != (double)(float)unityYaw)
            {
                SbAnchorPose narrowed = SpatialBlendAPI.ToNativeAnchorPose(new EmbodiedPose(Vector3.zero, (float)unityYaw), 7, true);
                Assert.That(native.orientation.yaw, Is.Not.EqualTo(narrowed.orientation.yaw));
            }
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void NonFiniteExplicitDoubleYawIsRefusedBeforeCallingAnyNativeLibrary(double unityYaw)
        {
            IEmbodiedTargetEventCore core = new NativeEmbodiedCore(0);
            var pose = new EmbodiedPose(Vector3.zero, 0f);
            Assert.That(core.TryPublishAnchorPose(0, 0, pose, unityYaw, 0.0, 1, 1, false, out double submitted), Is.False);
            Assert.That(double.IsNaN(submitted), Is.True, "There was no native submission or accepted sample.");
        }

        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(-90f)]
        [TestCase(45.25f)]
        public void TheDoubleYawConverterSharesTheLegacyReferenceAxisAndHandedness(float unityYaw)
        {
            Assert.That(CoordinateConverter.UnityToSpatialBlendAngle((double)unityYaw),
                Is.EqualTo((double)CoordinateConverter.UnityToSpatialBlendAngle(unityYaw)));
        }

        [Test]
        public void ASourceExceptionEventCarriesItsIdAndItsOpaquePayloadUnchanged()
        {
            var payload = new byte[(int)SpatialBlendAPI.ExceptionPayloadMax];
            for (int i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 7 + 3);
            var native = new SbActorEvent
            {
                struct_size = (uint)Marshal.SizeOf<SbActorEvent>(),
                kind = (int)ActorEventKind.SourceException,
                revision = 12,
                source_sequence = 99,
                source_shared_id = 4100,
                exception_id = 0xABCD,
                payload_size = 5,
                payload = payload,
            };

            Assert.That(SpatialBlendAPI.TryConvertActorEvent(native, out ActorEvent actorEvent), Is.True);

            Assert.That(actorEvent.Kind, Is.EqualTo(ActorEventKind.SourceException));
            Assert.That(actorEvent.Revision, Is.EqualTo(12UL));
            Assert.That(actorEvent.SourceSequence, Is.EqualTo(99L));
            Assert.That(actorEvent.SourceSharedId, Is.EqualTo(4100L));
            Assert.That(actorEvent.ExceptionId, Is.EqualTo(0xABCDu));
            Assert.That(actorEvent.Payload, Is.EqualTo(new[] { payload[0], payload[1], payload[2], payload[3], payload[4] }),
                "payload_size bytes, never interpreted.");

            native.payload_size = 1000;   // a corrupt size is bounded by the wire capacity
            Assert.That(SpatialBlendAPI.TryConvertActorEvent(native, out actorEvent), Is.True);
            Assert.That(actorEvent.Payload.Length, Is.EqualTo((int)SpatialBlendAPI.ExceptionPayloadMax));
        }

        [Test]
        public void ATeleportRequestEventCarriesTheAnchorAndItsTargetPoseInUnityAxes()
        {
            var native = new SbActorEvent
            {
                struct_size = (uint)Marshal.SizeOf<SbActorEvent>(),
                kind = (int)ActorEventKind.TeleportRequest,
                revision = 3,
                source_shared_id = 4100,
                interaction_kind = 1,   // SB_INTERACTION_AFFORDANCE_POSE_ENGAGEMENT
                anchor_id = 2,
                x = 3.0,
                y = 4.0,
                z = 0.45,
                orientation = new SbOrientation { struct_size = 32, type = (int)SbOrientationType.Yaw, yaw = Math.PI },
                payload = new byte[(int)SpatialBlendAPI.ExceptionPayloadMax],
            };

            Assert.That(SpatialBlendAPI.TryConvertActorEvent(native, out ActorEvent actorEvent), Is.True);

            Assert.That(actorEvent.Kind, Is.EqualTo(ActorEventKind.TeleportRequest));
            Assert.That(actorEvent.AnchorId, Is.EqualTo(2u));
            Assert.That(actorEvent.InteractionKind, Is.EqualTo(1));
            Assert.That(actorEvent.Position.x, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(actorEvent.Position.y, Is.EqualTo(0.45f).Within(1e-5f));
            Assert.That(actorEvent.Position.z, Is.EqualTo(4f).Within(1e-5f));
            Assert.That(Mathf.DeltaAngle(actorEvent.YawDegrees, -90f), Is.EqualTo(0f).Within(1e-3f), "Z-up yaw 180 is Unity yaw -90.");
            Assert.That(actorEvent.Payload, Is.Null, "A teleport request has no payload.");
        }

        [Test]
        public void AnUnknownEventKindAndANonFiniteRequestPoseAreRefused()
        {
            var unknown = new SbActorEvent { kind = 99, payload = new byte[(int)SpatialBlendAPI.ExceptionPayloadMax] };
            Assert.That(SpatialBlendAPI.TryConvertActorEvent(unknown, out _), Is.False);

            var request = new SbActorEvent
            {
                kind = (int)ActorEventKind.TeleportRequest,
                x = double.NaN,
                payload = new byte[(int)SpatialBlendAPI.ExceptionPayloadMax],
            };
            Assert.That(SpatialBlendAPI.TryConvertActorEvent(request, out _), Is.False, "There is nothing to place at.");
        }

        [Test]
        public void ACourseDropsItsStartAndConvertsTheRestToUnityGroundPoints()
        {
            // Z-up course (0,0) -> (1,0) -> (1,2): Unity ground points (1, 0) then (1, 2) after the start.
            var xy = new double[] { 0, 0, 1, 0, 1, 2, 99, 99 };
            var waypoints = new List<Vector2>();

            Assert.That(SpatialBlendAPI.TryConvertCourse(xy, 3, waypoints), Is.True);

            Assert.That(waypoints.Count, Is.EqualTo(2));
            Assert.That(waypoints[0].x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(waypoints[0].y, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(waypoints[1].x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(waypoints[1].y, Is.EqualTo(2f).Within(1e-5f));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(1)]
        public void NoCourseOrAStartOnlyCourseIsNotACourse(int count)
        {
            var waypoints = new List<Vector2>();
            Assert.That(SpatialBlendAPI.TryConvertCourse(new double[] { 0, 0, 1, 1 }, count, waypoints), Is.False);
            Assert.That(waypoints, Is.Empty);
        }

        [Test]
        public void ACourseLongerThanTheBufferIsTruncatedToWhatWasWritten()
        {
            var xy = new double[] { 0, 0, 1, 0, 2, 0 };
            var waypoints = new List<Vector2>();

            Assert.That(SpatialBlendAPI.TryConvertCourse(xy, 40, waypoints), Is.True);

            Assert.That(waypoints.Count, Is.EqualTo(2), "min(count, cap) waypoints are written.");
        }

        [Test]
        public void ANonFiniteCourseIsRefusedWhole()
        {
            var waypoints = new List<Vector2>();
            Assert.That(SpatialBlendAPI.TryConvertCourse(new double[] { 0, 0, 1, double.NaN }, 2, waypoints), Is.False);
            Assert.That(waypoints, Is.Empty);
        }

        [Test]
        public void TargetRequestPayloadMirrorsAllElevenCFieldsWithoutChangingTheEventEnvelope()
        {
            Assert.That(Marshal.SizeOf<SbTargetRequestPayload>(), Is.EqualTo(64));
            AssertOffsets(typeof(SbTargetRequestPayload),
                ("struct_size", 0), ("channels", 4), ("sample_epoch", 8), ("sample_sequence", 16),
                ("sample_yaw", 24), ("yaw_turn", 32), ("proposal_flags", 40), ("proposal_cause", 44),
                ("facing_yaw", 48), ("proposal_status", 56), ("request_flags", 60));
            Assert.That(Marshal.SizeOf<SbActorEvent>(), Is.EqualTo(360));
            Assert.That(SpatialBlendAPI.TargetRequestPayloadSize, Is.EqualTo(64u));
            Assert.That(SpatialBlendAPI.TargetEventAbiMinor, Is.EqualTo(13u));
            Assert.That(SpatialBlendAPI.RequiredAbiMinor, Is.EqualTo(12u), "Legacy drivers are not silently opted into 4.13.");
        }

        [TestCase("EmbodiedPortRelease")]
        [TestCase("EmbodiedPortDebug")]
        public void TargetDeliveryImportIsAnExplicitInt32Subscription(string imports)
        {
            MethodInfo method = Import(imports, "sb_set_target_event_delivery");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(int)));
            Assert.That(method.GetParameters().Select(p => p.ParameterType),
                Is.EqualTo(new[] { typeof(long), typeof(long), typeof(int) }));
            AssertPInvoke(method);
            Assert.That(typeof(IEmbodiedCore).IsAssignableFrom(typeof(IEmbodiedTargetEventCore)), Is.True);
            Assert.That(typeof(IEmbodiedTargetEventCore).IsAssignableFrom(typeof(NativeEmbodiedCore)), Is.True);
            MethodInfo doubleYaw = typeof(IEmbodiedTargetEventCore).GetMethods()
                .Single(m => m.Name == "TryPublishAnchorPose" && m.GetParameters().Length == 9);
            Assert.That(doubleYaw.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(doubleYaw.GetParameters().Select(p => p.ParameterType), Is.EqualTo(new[]
            {
                typeof(long), typeof(uint), typeof(EmbodiedPose).MakeByRefType(), typeof(double), typeof(double),
                typeof(ulong), typeof(ulong), typeof(bool), typeof(double).MakeByRefType()
            }));
            Assert.That(doubleYaw.GetParameters()[8].IsOut, Is.True);
        }

        [TestCase(12u, true)]
        [TestCase(13u, false)]
        [TestCase(14u, false)]
        public void TypedAdmissionRequiresMinor13SeparatelyFromLegacyAdmission(uint minor, bool refused)
        {
            var descriptor = new SbV2AbiLayout { abi_version = 4, abi_minor = minor };
            // Other descriptor/layout fields are deliberately absent here; this isolates the additional minor gate.
            Assert.That(SpatialBlendAPI.GetTargetEventAbiMismatches(descriptor)
                .Any(m => m.StartsWith("target-event abi_minor:", StringComparison.Ordinal)), Is.EqualTo(refused));
        }

        [Test]
        public void SignedTypedYawRetainsExactNativeIdentityAndDoublePrecisionWithoutUsingLegacyFloatFields()
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.YawRequest, 2);
            double sample = BitConverter.ToDouble(native.payload, 24);
            double turn = BitConverter.ToDouble(native.payload, 32);
            ulong epoch = BitConverter.ToUInt64(native.payload, 8);
            ulong sequence = BitConverter.ToUInt64(native.payload, 16);

            Assert.That(SpatialBlendAPI.TryConvertCheckedActorEvent(native, out ActorEvent read, out string error), Is.True, error);
            TargetRequest request = read.TargetRequest;
            Assert.That(read.Kind, Is.EqualTo(ActorEventKind.YawRequest));
            Assert.That(read.AnchorId, Is.EqualTo(7u));
            Assert.That(read.Revision, Is.EqualTo(42UL));
            Assert.That(read.SourceSequence, Is.EqualTo(19L));
            Assert.That(request.SampleEpoch, Is.EqualTo(epoch));
            Assert.That(request.SampleSequence, Is.EqualTo(sequence));
            Assert.That(request.SampleYawRadians, Is.EqualTo(sample));
            Assert.That(request.YawTurnRadians, Is.EqualTo(turn));
            Assert.That(request.YawTurnRadians, Is.GreaterThan(2.0 * Math.PI));
            Assert.That(request.SampleYawRadians, Is.Not.EqualTo((double)(float)sample));
            Assert.That(request.TargetYawRadians, Is.EqualTo(native.orientation.yaw));
            Assert.That(request.TargetPitchRadians, Is.EqualTo(native.orientation.pitch));
            Assert.That(request.TargetRollRadians, Is.EqualTo(native.orientation.roll));
            Assert.That(request.Position, Is.EqualTo(new Vector3(3f, 0.45f, 4f)));
            Assert.That(request.Placement, Is.False);
            Assert.That(read.Position, Is.EqualTo(Vector3.zero), "Typed realization must use its detached request, not legacy pose fields.");
            Assert.That(read.YawDegrees, Is.Zero);

            native.payload[24] ^= 0x7f;
            native.orientation.yaw = -2.0;
            Assert.That(request.SampleYawRadians, Is.EqualTo(sample), "No retained reference to the native scratch bytes.");
            Assert.That(request.TargetYawRadians, Is.Not.EqualTo(native.orientation.yaw));
        }

        [TestCase(1UL)]
        [TestCase(0xfedcba9876543210UL)]
        public void SampledOrdinaryYawAcceptsEpochZeroAndPreservesItsExactNativeCorrespondence(ulong sequence)
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.YawRequest, 2);
            Put(native.payload, 8, BitConverter.GetBytes(0UL));
            Put(native.payload, 16, BitConverter.GetBytes(sequence));
            double sampleYaw = BitConverter.ToDouble(native.payload, 24);
            double turn = BitConverter.ToDouble(native.payload, 32);

            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(1, native, out ActorEvent read,
                out ActorEventPollFailure failure), Is.EqualTo(ActorEventPollResult.Event));
            Assert.That(failure, Is.Null);
            Assert.That(read.Kind, Is.EqualTo(ActorEventKind.YawRequest));
            Assert.That(read.TargetRequest.SampleEpoch, Is.Zero);
            Assert.That(read.TargetRequest.SampleSequence, Is.EqualTo(sequence));
            Assert.That(read.TargetRequest.SampleYawRadians, Is.EqualTo(sampleYaw));
            Assert.That(read.TargetRequest.YawTurnRadians, Is.EqualTo(turn));
            Assert.That(read.TargetRequest.ProposalFlags & AnchorProposalFlags.Sampled, Is.EqualTo(AnchorProposalFlags.Sampled));
            Assert.That(read.TargetRequest.Placement, Is.False, "Epoch zero does not make an ordinary request a placement.");
        }

        [Test]
        public void PlacementYawUsesItsImmutableAbsoluteOrientationWithoutManufacturingASampleOrigin()
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.YawRequest, 2);
            Put(native.payload, 8, BitConverter.GetBytes(0UL));
            Put(native.payload, 16, BitConverter.GetBytes(0UL));
            Put(native.payload, 24, BitConverter.GetBytes(500.0));
            Put(native.payload, 32, BitConverter.GetBytes(600.0));
            Put(native.payload, 40, BitConverter.GetBytes(0u));
            Put(native.payload, 60, BitConverter.GetBytes(1u));

            Assert.That(SpatialBlendAPI.TryConvertCheckedActorEvent(native, out ActorEvent read, out string error), Is.True, error);
            Assert.That(read.TargetRequest.Placement, Is.True);
            Assert.That(read.TargetRequest.SampleEpoch, Is.Zero);
            Assert.That(read.TargetRequest.SampleSequence, Is.Zero);
            Assert.That(read.TargetRequest.TargetYawRadians, Is.EqualTo(native.orientation.yaw));
            Assert.That(read.TargetRequest.TargetYawRadians,
                Is.Not.EqualTo(read.TargetRequest.SampleYawRadians + read.TargetRequest.YawTurnRadians));
        }

        [Test]
        public void FirstProposalCauseAloneIsNotAnAbsolutePlacementAndPositionNeedsNoYawCorrespondence()
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.PositionRequest, 1);
            Put(native.payload, 8, BitConverter.GetBytes(0UL));
            Put(native.payload, 16, BitConverter.GetBytes(0UL));
            Put(native.payload, 40, BitConverter.GetBytes(0u));
            Put(native.payload, 44, BitConverter.GetBytes((uint)EmbodiedCause.FirstProposal));
            Assert.That(SpatialBlendAPI.TryConvertCheckedActorEvent(native, out ActorEvent read, out string error), Is.True, error);
            Assert.That(read.TargetRequest.Placement, Is.False);
            Assert.That(read.TargetRequest.Channels, Is.EqualTo(TargetChannels.Position));
            Assert.That(read.TargetRequest.Position, Is.EqualTo(new Vector3(3f, 0.45f, 4f)));
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        public void ReleaseCancelsOnlyItsNamedChannelsWithoutNeedingASnapshotPose(uint channels)
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.TargetRelease, channels);
            Put(native.payload, 8, BitConverter.GetBytes(0UL));
            Put(native.payload, 16, BitConverter.GetBytes(0UL));
            Put(native.payload, 56, BitConverter.GetBytes(0));
            native.x = double.NaN;
            native.orientation.yaw = double.NaN;
            Assert.That(SpatialBlendAPI.TryConvertCheckedActorEvent(native, out ActorEvent read, out string error), Is.True, error);
            Assert.That(read.TargetRequest.Channels, Is.EqualTo((TargetChannels)channels));
            Assert.That(read.TargetRequest.Placement, Is.False);
        }

        [TestCase("outer_size")]
        [TestCase("payload_size")]
        [TestCase("short_buffer")]
        [TestCase("payload_struct_size")]
        [TestCase("unknown_request_flag")]
        [TestCase("empty_channels")]
        [TestCase("unknown_channel")]
        [TestCase("wrong_channel")]
        [TestCase("unknown_status")]
        [TestCase("no_target_status")]
        [TestCase("zero_sequence")]
        [TestCase("unsampled")]
        [TestCase("zero_epoch_zero_sequence")]
        [TestCase("zero_epoch_unsampled")]
        [TestCase("unspecified_yaw")]
        [TestCase("sample_nan")]
        [TestCase("turn_nan")]
        [TestCase("sample_infinity")]
        [TestCase("turn_infinity")]
        [TestCase("resolved_yaw_overflow")]
        [TestCase("target_nan")]
        [TestCase("position_overflow")]
        [TestCase("orientation_size")]
        [TestCase("orientation_type")]
        [TestCase("pitch_nan")]
        [TestCase("roll_nan")]
        [TestCase("facing_nan")]
        public void MalformedTypedRequestsAreConversionErrorsNeverNativeEmpty(string malformed)
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.YawRequest, 2);
            switch (malformed)
            {
                case "outer_size": native.struct_size = 0; break;
                case "payload_size": native.payload_size = 63; break;
                case "short_buffer": native.payload = new byte[63]; break;
                case "payload_struct_size": Put(native.payload, 0, BitConverter.GetBytes(60u)); break;
                case "unknown_request_flag": Put(native.payload, 60, BitConverter.GetBytes(2u)); break;
                case "empty_channels": Put(native.payload, 4, BitConverter.GetBytes(0u)); break;
                case "unknown_channel": Put(native.payload, 4, BitConverter.GetBytes(4u)); break;
                case "wrong_channel": Put(native.payload, 4, BitConverter.GetBytes(1u)); break;
                case "unknown_status": Put(native.payload, 56, BitConverter.GetBytes(4)); break;
                case "no_target_status": Put(native.payload, 56, BitConverter.GetBytes(1)); break;
                case "zero_sequence": Put(native.payload, 16, BitConverter.GetBytes(0UL)); break;
                case "unsampled": Put(native.payload, 40, BitConverter.GetBytes(0u)); break;
                case "zero_epoch_zero_sequence":
                    Put(native.payload, 8, BitConverter.GetBytes(0UL));
                    Put(native.payload, 16, BitConverter.GetBytes(0UL));
                    break;
                case "zero_epoch_unsampled":
                    Put(native.payload, 8, BitConverter.GetBytes(0UL));
                    Put(native.payload, 40, BitConverter.GetBytes(0u));
                    break;
                case "unspecified_yaw": Put(native.payload, 40, BitConverter.GetBytes(5u)); break;
                case "sample_nan": Put(native.payload, 24, BitConverter.GetBytes(double.NaN)); break;
                case "turn_nan": Put(native.payload, 32, BitConverter.GetBytes(double.NaN)); break;
                case "sample_infinity": Put(native.payload, 24, BitConverter.GetBytes(double.PositiveInfinity)); break;
                case "turn_infinity": Put(native.payload, 32, BitConverter.GetBytes(double.NegativeInfinity)); break;
                case "resolved_yaw_overflow":
                    Put(native.payload, 24, BitConverter.GetBytes(double.MaxValue));
                    Put(native.payload, 32, BitConverter.GetBytes(double.MaxValue));
                    break;
                case "target_nan": native.orientation.yaw = double.NaN; break;
                case "position_overflow": native.x = double.MaxValue; break;
                case "orientation_size": native.orientation.struct_size = 0; break;
                case "orientation_type": native.orientation.type = 3; break;
                case "pitch_nan": native.orientation.pitch = double.NaN; break;
                case "roll_nan": native.orientation.roll = double.NaN; break;
                case "facing_nan":
                    Put(native.payload, 40, BitConverter.GetBytes((uint)(AnchorProposalFlags.Sampled | AnchorProposalFlags.FacingYaw)));
                    Put(native.payload, 48, BitConverter.GetBytes(double.NaN));
                    break;
                default: Assert.Fail(malformed); break;
            }
            ActorEventPollResult result = SpatialBlendAPI.ConvertActorEventPollResult(1, native, out _, out ActorEventPollFailure failure);
            Assert.That(result, Is.EqualTo(ActorEventPollResult.ConversionError));
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.NativeResult, Is.EqualTo(1));
            Assert.That(failure.Error, Is.Not.Empty);
            Assert.That(failure.RejectedEventBytes.Count, Is.EqualTo(360));
        }

        [TestCase(-1)]
        [TestCase(-2)]
        [TestCase(int.MinValue)]
        [TestCase(2)]
        [TestCase(37)]
        public void NativeNegativeAndUnknownPositiveResultsAreNeverEmpty(int nativeResult)
        {
            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(nativeResult, NativeTargetEvent(ActorEventKind.YawRequest, 2),
                out _, out ActorEventPollFailure failure), Is.EqualTo(ActorEventPollResult.NativeError));
            Assert.That(failure.NativeResult, Is.EqualTo(nativeResult));
            Assert.That(failure.RejectedEventBytes, Is.Null, "No event was reported as popped.");
            Assert.That(default(ActorEventPollResult), Is.EqualTo(ActorEventPollResult.NativeError));
        }

        [Test]
        public void OnlyNativeZeroIsEmptyAndAConversionFailureRemainsDetachedAfterLaterEmpty()
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.YawRequest, 2);
            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(1, native, out _, out ActorEventPollFailure success),
                Is.EqualTo(ActorEventPollResult.Event));
            Assert.That(success, Is.Null);
            native.kind = 99;
            native.payload[255] = 173;
            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(1, native, out _, out ActorEventPollFailure retained),
                Is.EqualTo(ActorEventPollResult.ConversionError));
            native.payload[255] = 0;
            Assert.That(retained.RejectedEventBytes[104 + 255], Is.EqualTo(173));
            Assert.That(retained.PayloadBufferLength, Is.EqualTo(256));
            Assert.That(retained.Error, Does.Contain("99"));
            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(0, native, out _, out ActorEventPollFailure empty),
                Is.EqualTo(ActorEventPollResult.NativeEmpty));
            Assert.That(empty, Is.Null);
            Assert.That(retained.Error, Does.Contain("99"), "The caller must preserve this incomplete-drain failure, not retire on the later zero.");
        }

        [Test]
        public void CheckedLifecycleDecodeRejectsOversizePayloadWithoutChangingTheLegacyCompatibilityDecoder()
        {
            var native = new SbActorEvent
            {
                struct_size = 360, kind = (int)ActorEventKind.SourceException,
                payload_size = 257, payload = new byte[256]
            };
            Assert.That(SpatialBlendAPI.TryConvertCheckedActorEvent(native, out _, out _), Is.False);
            Assert.That(SpatialBlendAPI.TryConvertActorEvent(native, out ActorEvent legacy), Is.True);
            Assert.That(legacy.Payload.Length, Is.EqualTo(256), "Existing lifecycle-only callers keep their old behavior.");
        }

        [Test]
        public void CheckedTeleportRefusesPitchThatWouldOverflowItsLegacyFloatDegrees()
        {
            SbActorEvent native = NativeTargetEvent(ActorEventKind.TeleportRequest, 1);
            native.payload_size = 0;
            native.orientation.pitch = double.MaxValue;
            Assert.That(SpatialBlendAPI.ConvertActorEventPollResult(1, native, out _, out ActorEventPollFailure failure),
                Is.EqualTo(ActorEventPollResult.ConversionError));
            Assert.That(failure.RejectedEventBytes.Count, Is.EqualTo(360));
        }

        [TestCase(3, 1u)]
        [TestCase(4, 2u)]
        [TestCase(5, 3u)]
        public void LegacyDecoderNeverMistakesANewTypedKindForALatencyTeleport(int kind, uint channels)
        {
            Assert.That(SpatialBlendAPI.TryConvertActorEvent(NativeTargetEvent((ActorEventKind)kind, channels), out _), Is.False);
        }

        // ------------------------------------------------------------------ helpers

        private static SbActorEvent NativeTargetEvent(ActorEventKind kind, uint channels)
        {
            // Independent native-layout bytes, from the C header's literal offsets, not marshalled from the mirror.
            var payload = new byte[256];
            const double sample = 1.234567890123456;
            const double turn = 8.76543210987654;
            Put(payload, 0, BitConverter.GetBytes(64u));
            Put(payload, 4, BitConverter.GetBytes(channels));
            Put(payload, 8, BitConverter.GetBytes(0xfedcba9876543210UL));
            Put(payload, 16, BitConverter.GetBytes(0x0123456789abcdefUL));
            Put(payload, 24, BitConverter.GetBytes(sample));
            Put(payload, 32, BitConverter.GetBytes(turn));
            Put(payload, 40, BitConverter.GetBytes(4u));
            Put(payload, 44, BitConverter.GetBytes(0x8000u));
            Put(payload, 48, BitConverter.GetBytes(-0.345678901234567));
            Put(payload, 56, BitConverter.GetBytes(2));
            Put(payload, 60, BitConverter.GetBytes(0u));
            return new SbActorEvent
            {
                struct_size = 360, kind = (int)kind, revision = 42, source_sequence = 19, source_shared_id = 4100,
                anchor_id = 7, x = 3.0, y = 4.0, z = 0.45,
                orientation = new SbOrientation
                {
                    struct_size = 32, type = 2, yaw = (sample + turn) % (2.0 * Math.PI),
                    pitch = 0.123456789012345, roll = -0.234567890123456
                },
                payload_size = 64, payload = payload
            };
        }

        private static void Put(byte[] into, int offset, byte[] value) => Array.Copy(value, 0, into, offset, value.Length);

        private static void AssertOffsets(Type type, params (string field, int offset)[] expected)
        {
            foreach ((string field, int offset) in expected)
            {
                Assert.That(Marshal.OffsetOf(type, field).ToInt32(), Is.EqualTo(offset), type.Name + "." + field);
            }
            Assert.That(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Length, Is.EqualTo(expected.Length),
                type.Name + " has exactly the fields of the C struct.");
        }

        private static void AssertNames(Type type, string[] names)
        {
            FieldInfo[] declared = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            Assert.That(declared.Length, Is.EqualTo(names.Length), type.Name);
            for (int i = 0; i < names.Length; ++i)
            {
                Assert.That(declared[i].Name, Is.EqualTo(names[i]), type.Name + " field " + i);
            }
        }

        private static MethodInfo Import(string nestedType, string name)
        {
            Type imports = typeof(SpatialBlendAPI).GetNestedType(nestedType, BindingFlags.NonPublic);
            Assert.That(imports, Is.Not.Null, nestedType);
            MethodInfo method = imports.GetMethod(name, Static);
            Assert.That(method, Is.Not.Null, nestedType + "." + name);
            return method;
        }

        // DllImport is a pseudo-attribute and not reliably visible to GetCustomAttribute on
        // every runtime; the PinvokeImpl flag is.
        private static void AssertPInvoke(MethodInfo method)
        {
            Assert.That((method.Attributes & MethodAttributes.PinvokeImpl) != 0, Is.True, method.Name);
        }
    }

    /// <summary>
    /// The same calls against the real library. Deliberately not FreeformRestoration: it needs the dylib that
    /// tools/run-foundation-tests.sh supplies, built from a core with the per-anchor port (ABI 4).
    /// </summary>
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationEmbodiedPortNativeTests
    {
        private long _session;
        private long _space;
        private long _actor;

        [SetUp]
        public void SetUp()
        {
            SpatialBlendAPI.Initialize();
            _session = SpatialBlendAPI.CreateSession();
            Assert.That(_session, Is.GreaterThan(0));
            _space = SpatialBlendAPI.AddSpace(_session);
            Assert.That(_space, Is.GreaterThan(0));
            _actor = SpatialBlendAPI.CreateActor(_space);
            Assert.That(_actor, Is.GreaterThan(0));
        }

        [TearDown]
        public void TearDown()
        {
            SpatialBlendAPI.DestroySession(_session);
        }

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong sb_sizeof(int structId);

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_get_struct_field_count(int structId);

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_get_struct_field_offset(int structId, uint field);

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_abi_version();

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_abi_minor_version();

        [Test]
        public void TheLibraryIsAbi4AndEveryStructSizeAndOffsetAgreesWithTheManagedMirror()
        {
            Assert.That(sb_abi_version(), Is.EqualTo(SpatialBlendAPI.ExpectedAbiMajor));
            Assert.That(sb_abi_version(), Is.EqualTo(4u));
            Assert.That(sb_abi_minor_version(), Is.GreaterThanOrEqualTo(SpatialBlendAPI.RequiredAbiMinor));
            CheckLayout(22, typeof(SbAnchorPose), SpatialBlendAPI.AnchorPoseFields);       // SB_STRUCT_ANCHOR_POSE
            CheckLayout(23, typeof(SbAnchorProposal), SpatialBlendAPI.AnchorProposalFields); // SB_STRUCT_ANCHOR_PROPOSAL
            CheckLayout(24, typeof(SbProposalSet), SpatialBlendAPI.ProposalSetFields);     // SB_STRUCT_PROPOSAL_SET
            CheckLayout(25, typeof(SbActorEvent), SpatialBlendAPI.ActorEventFields);       // SB_STRUCT_ACTOR_EVENT
            Assert.That(SpatialBlendAPI.TryCheckEmbodiedPort(out string portError), Is.True, portError);
            Assert.That(SpatialBlendAPI.GetAbiLayoutMismatches(SpatialBlendAPI.GetNativeAbiLayout()), Is.Empty);
        }

        private static void CheckLayout(int id, Type managed, string[] fields)
        {
            Assert.That(sb_sizeof(id), Is.EqualTo((ulong)Marshal.SizeOf(managed)), managed.Name + " size");
            Assert.That(sb_get_struct_field_count(id), Is.EqualTo((uint)fields.Length), managed.Name + " field count");
            for (uint i = 0; i < fields.Length; ++i)
            {
                Assert.That(sb_get_struct_field_offset(id, i), Is.EqualTo((uint)Marshal.OffsetOf(managed, fields[i]).ToInt32()),
                    managed.Name + "." + fields[i]);
            }
        }

        [Test]
        public void TheTypedCapabilityChecksNativeLayoutsThenAdmitsAndDistinguishesAnEmptyFenceFromAnInvalidActor()
        {
            CheckLayout(32, typeof(SbTargetRequestPayload), SpatialBlendAPI.TargetRequestPayloadFields);
            Assert.That(SpatialBlendAPI.TryCheckTargetEventPort(out string layoutError), Is.True, layoutError);
            IEmbodiedTargetEventCore core = new NativeEmbodiedCore(_space);
            Assert.That(core.TryGetAnchorId(_actor, "root", out uint root), Is.True);
            Assert.That(root, Is.Zero);
            Assert.That(core.TrySetTargetEventDelivery(_actor, true, out string admissionError), Is.True, admissionError);
            Assert.That(core.PollEvent(_actor, out ActorEvent release, out ActorEventPollFailure failure),
                Is.EqualTo(ActorEventPollResult.Event));
            Assert.That(failure, Is.Null);
            Assert.That(release.Kind, Is.EqualTo(ActorEventKind.TargetRelease));
            Assert.That(release.TargetRequest.Channels, Is.EqualTo(TargetChannels.Position | TargetChannels.Yaw));
            Assert.That(core.PollEvent(_actor, out _, out _), Is.EqualTo(ActorEventPollResult.NativeEmpty));
            Assert.That(core.TrySetTargetEventDelivery(_actor, true, out _), Is.True, "Repeated enable is not a new lifecycle.");
            Assert.That(core.PollEvent(_actor, out _, out _), Is.EqualTo(ActorEventPollResult.NativeEmpty));
            Assert.That(core.PollEvent(987654, out _, out failure), Is.EqualTo(ActorEventPollResult.NativeError));
            Assert.That(failure.NativeResult, Is.EqualTo(-1));
            Assert.That(core.TrySetTargetEventDelivery(987654, true, out _), Is.False);
            Assert.That(core.TrySetTargetEventDelivery(_actor, false, out _), Is.True);
        }

        [Test]
        public void TheExplicitCapabilityReturnsNativePublicationAcceptanceInsteadOfDiscardingIt()
        {
            IEmbodiedTargetEventCore core = new NativeEmbodiedCore(_space);
            var pose = new EmbodiedPose(new Vector3(1f, 0.9f, 2f), 30f);
            Assert.That(core.TryPublishAnchorPose(_actor, 0, pose, 0.0, 1, 1, false), Is.True);
            Assert.That(core.TryPublishAnchorPose(_actor, 0, pose, 0.1, 1, 1, false), Is.False, "Native refused this stale stamp.");
            Assert.That(core.TryPublishAnchorPose(987654, 0, pose, 0.0, 1, 1, false), Is.False);
            Assert.That(core.TryPublishBonePose(_actor, 9999, default, 0.0, 1, 1, false), Is.False,
                "An unregistered bone cannot be acknowledged as accepted.");
            const double continuousYaw = -1440.123456789;
            Assert.That(core.TryPublishAnchorPose(_actor, 0, pose, continuousYaw, 0.2, 2, 1, false, out double submitted), Is.True);
            Assert.That(submitted, Is.EqualTo(SpatialBlendAPI.ToNativeAnchorPose(pose, continuousYaw, 1, false).orientation.yaw));
            Assert.That(Math.Abs(submitted), Is.GreaterThan(2.0 * Math.PI), "The accepted submission was not wrapped.");
            Assert.That(core.TryPublishAnchorPose(_actor, 0, pose, continuousYaw, 0.3, 2, 1, false, out _), Is.False,
                "A double yaw does not bypass native stamp refusal or authorize advancing accepted correspondence.");
        }

        [Test]
        public void ANativeAcceptedEpochZeroSampleProducesOrdinaryYawEventsAndARealEmptyFence()
        {
            // Room/object geometry and source turns follow c/c_api_test.cc's SetupTwoSpaces and
            // APursuingFacingProposalCarriesItsSignedTurn. Events come from the real source solve, not injected bytes.
            long sourceSpace = SpatialBlendAPI.AddSpace(_session);
            long sourceRoom = BuildEpochZeroFacingRoom(sourceSpace, true);
            long receiverRoom = BuildEpochZeroFacingRoom(_space, false);
            long source = SpatialBlendAPI.CreateActor(sourceSpace);
            SpatialBlendAPI.AddAnchor(sourceSpace, source, "root", Vector3.zero);
            SpatialBlendAPI.SetActorElement(sourceSpace, source, sourceRoom);
            Assert.That(SpatialBlendAPI.Bind(sourceSpace, source, "root", SbKernelType.Spacing), Is.True);
            Assert.That(SpatialBlendAPI.Bind(sourceSpace, source, "root", SbKernelType.Facing), Is.True);
            SpatialBlendAPI.AddAnchor(_space, _actor, "root", Vector3.zero);
            SpatialBlendAPI.SetActorElement(_space, _actor, receiverRoom);

            IEmbodiedTargetEventCore core = new NativeEmbodiedCore(_space);
            const ulong epoch = 0;
            const ulong sequence = 1;
            const double committedYaw = 810.123456789;
            var pose = new EmbodiedPose(Vector3.zero, 0f);
            Assert.That(core.TryPublishAnchorPose(_actor, 0, pose, committedYaw, 0.0, sequence, epoch, false,
                out double submittedYaw), Is.True, "Native permits an initial epoch of zero; sequence, not epoch, must be nonzero.");
            Assert.That(core.TrySetTargetEventDelivery(_actor, true, out string error), Is.True, error);
            Assert.That(core.PollEvent(_actor, out ActorEvent release, out ActorEventPollFailure failure),
                Is.EqualTo(ActorEventPollResult.Event));
            Assert.That(failure, Is.Null);
            Assert.That(release.Kind, Is.EqualTo(ActorEventKind.TargetRelease));
            Assert.That(core.PollEvent(_actor, out _, out failure), Is.EqualTo(ActorEventPollResult.NativeEmpty));
            Assert.That(failure, Is.Null);

            int ordinaryYawEvents = 0;
            for (int step = 0; step <= 30; ++step)
            {
                float sourceYawDegrees = 90f - (float)((-0.6 + 0.05 * step) * 180.0 / Math.PI);
                SpatialBlendAPI.UpdateActorState(sourceSpace, source, new Vector3(10f, 0f, 10f), sourceYawDegrees);
                while (true)
                {
                    ActorEventPollResult result = core.PollEvent(_actor, out ActorEvent actorEvent, out failure);
                    if (result == ActorEventPollResult.NativeEmpty)
                    {
                        Assert.That(failure, Is.Null);
                        break;
                    }
                    Assert.That(result, Is.EqualTo(ActorEventPollResult.Event), failure?.Error);
                    if (actorEvent.Kind != ActorEventKind.YawRequest || actorEvent.TargetRequest.Placement) continue;
                    ++ordinaryYawEvents;
                    TargetRequest request = actorEvent.TargetRequest;
                    Assert.That(actorEvent.AnchorId, Is.Zero);
                    Assert.That(request.SampleEpoch, Is.EqualTo(epoch));
                    Assert.That(request.SampleSequence, Is.EqualTo(sequence));
                    Assert.That(request.SampleYawRadians, Is.EqualTo(submittedYaw));
                    Assert.That(request.ProposalFlags & AnchorProposalFlags.Sampled, Is.EqualTo(AnchorProposalFlags.Sampled));
                    Assert.That(double.IsNaN(request.YawTurnRadians) || double.IsInfinity(request.YawTurnRadians), Is.False);
                }
            }
            Assert.That(ordinaryYawEvents, Is.GreaterThan(0), "Placement alone does not exercise ordinary epoch-zero correspondence.");
            Assert.That(core.PollEvent(_actor, out _, out failure), Is.EqualTo(ActorEventPollResult.NativeEmpty));
            Assert.That(failure, Is.Null, "This fence came from native return zero, not a converted failure.");
        }

        private static long BuildEpochZeroFacingRoom(long space, bool source)
        {
            long room = SpatialBlendAPI.AddElement(space);
            SpatialBlendAPI.SetElementSharedId(space, room, 100);
            float side = source ? 20f : 12f;
            SpatialBlendAPI.SetElementPolygon(space, room, SbGeometryRole.Navigable, new[]
            {
                Vector2.zero, new Vector2(side, 0f), new Vector2(side, side), new Vector2(0f, side)
            });
            AddEpochZeroFacingReferent(space, room, 200,
                source ? new Vector2(5f, 15f) : new Vector2(10f, 3f),
                source ? new Vector2(3f, 13f) : new Vector2(8f, 1f),
                source ? new Vector2(7f, 17f) : new Vector2(12f, 5f), "Writable");
            AddEpochZeroFacingReferent(space, room, 300,
                source ? new Vector2(15f, 5f) : new Vector2(3f, 10f),
                source ? new Vector2(13f, 3f) : new Vector2(1f, 8f),
                source ? new Vector2(17f, 7f) : new Vector2(5f, 12f), "Tabletop");
            return room;
        }

        private static void AddEpochZeroFacingReferent(long space, long room, long sharedId, Vector2 center,
                                                       Vector2 minimum, Vector2 maximum, string tag)
        {
            long element = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, element, sharedId);
            SpatialBlendAPI.SetElementTransform(space, element, Matrix4x4.Translate(new Vector3(center.x, 0f, center.y)));
            // A containment polygon is in the element's own frame (ABI 3.1), so the world box is given relative to the
            // element's translation. In world coordinates it would sit at twice the offset, outside the room, and the core
            // refuses it (ContainmentEscapesParent: a child region extends its parent's scope only within 0.5 m).
            Vector2 lo = minimum - center;
            Vector2 hi = maximum - center;
            SpatialBlendAPI.SetElementPolygon(space, element, SbGeometryRole.Containment, new[]
            {
                lo, new Vector2(hi.x, lo.y), hi, new Vector2(lo.x, hi.y)
            });
            long child = SpatialBlendAPI.AddElement(space, element);
            SpatialBlendAPI.SetElementSharedId(space, child, sharedId + 1);
            SpatialBlendAPI.SetElementTag(space, child, tag);
        }

        [Test]
        public void AnAnchorIdIsInternedOnFirstUseStableAndTheRootIsZero()
        {
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, _actor, "root", out uint root), Is.True);
            Assert.That(root, Is.EqualTo(0u), "Anchor id 0 is the actor's root anchor.");
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, _actor, "pelvis", out uint pelvis), Is.True);
            Assert.That(pelvis, Is.Not.EqualTo(0u));
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, _actor, "head", out uint head), Is.True);
            Assert.That(head, Is.Not.EqualTo(pelvis));
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, _actor, "pelvis", out uint again), Is.True);
            Assert.That(again, Is.EqualTo(pelvis), "Ids are stable for the actor's lifetime.");

            Assert.That(SpatialBlendAPI.TryGetAnchorName(_space, _actor, pelvis, out string name), Is.True);
            Assert.That(name, Is.EqualTo("pelvis"));
            Assert.That(SpatialBlendAPI.TryGetAnchorName(_space, _actor, 9999, out _), Is.False);
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, 987654, "pelvis", out _), Is.False, "An unknown actor.");
        }

        [Test]
        public void AProposalSetBeforeAnyPoseIsPublishedIsNoSampleOrNoneAndNeverASubstitutedPose()
        {
            SbAnchorProposal[] scratch = null;
            var set = new ProposalSet();

            Assert.That(SpatialBlendAPI.TryReadProposals(_space, _actor, ref scratch, set), Is.True);

            Assert.That(set.Anchors.Count, Is.LessThanOrEqualTo(1));
            foreach (AnchorProposal anchor in set.Anchors)
            {
                Assert.That(anchor.Status == EmbodiedStatus.None || anchor.Status == EmbodiedStatus.NoSample, Is.True,
                    "status " + anchor.Status);
                Assert.That(anchor.Position, Is.EqualTo(Vector3.zero));
            }
        }

        [Test]
        public void APublishedPoseIsAcceptedPerAnchorAndAStaleSequenceIsRefusedPerAnchor()
        {
            Assert.That(SpatialBlendAPI.TryGetAnchorId(_space, _actor, "pelvis", out uint pelvis), Is.True);
            EmbodiedPose root = new EmbodiedPose(new Vector3(1f, 0.9f, 2f), 30f);
            EmbodiedPose seat = new EmbodiedPose(new Vector3(1f, 0.45f, 2f), 30f);

            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.0, 1, 1, false), Is.True);
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.1, 2, 1, false), Is.True);
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.2, 2, 1, false), Is.False,
                "A sequence that is not newer within the epoch is stale.");
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.3, 0, 1, false), Is.False, "Sequence 0 is invalid.");

            // The pelvis has its own port: its sequence starts at 1 whatever the root's is.
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, pelvis, seat, 0.0, 1, 1, false), Is.True);
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, pelvis, seat, 0.1, 1, 1, false), Is.False);

            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.4, 1, 2, true), Is.True,
                "A new epoch restarts the sequence.");
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, root, 0.5, 5, 1, false), Is.False, "An older epoch is stale.");
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 9999, seat, 0.0, 1, 1, false), Is.False, "An unknown anchor.");
        }

        [Test]
        public void ANonFinitePoseAndAnUnknownActorAreRefused()
        {
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, _actor, 0, new EmbodiedPose(new Vector3(float.NaN, 0f, 0f), 0f), 0.0, 1, 1, false), Is.False);
            Assert.That(SpatialBlendAPI.TryPublishAnchorPose(_space, 987654, 0, new EmbodiedPose(Vector3.zero, 0f), 0.0, 1, 1, false), Is.False);
            SbAnchorProposal[] scratch = null;
            Assert.That(SpatialBlendAPI.TryReadProposals(_space, 987654, ref scratch, new ProposalSet()), Is.False);
        }

        [Test]
        public void ASourceExceptionOfAnUnknownActorOrAnOversizePayloadIsRefusedAndOfAKnownOneAccepted()
        {
            Assert.That(SpatialBlendAPI.TryRaiseSourceException(_space, 987654, 0x1000, null), Is.False);
            Assert.That(SpatialBlendAPI.TryRaiseSourceException(_space, _actor, 0x1000, null), Is.True);
            Assert.That(SpatialBlendAPI.TryRaiseSourceException(_space, _actor, 0x1001, new byte[] { 1, 2, 3 }), Is.True);
            Assert.That(SpatialBlendAPI.TryRaiseSourceException(_space, _actor, 0x1002,
                new byte[(int)SpatialBlendAPI.ExceptionPayloadMax]), Is.True, "The wire capacity itself is accepted.");
            Assert.That(SpatialBlendAPI.TryRaiseSourceException(_space, _actor, 0x1003,
                new byte[(int)SpatialBlendAPI.ExceptionPayloadMax + 1]), Is.False);
        }

        [Test]
        public void NoEventIsPendingForAnActorNothingHasHappenedTo()
        {
            Assert.That(SpatialBlendAPI.TryPollActorEvent(_space, _actor, out _), Is.False);
            Assert.That(SpatialBlendAPI.TryPollActorEvent(_space, 987654, out _), Is.False);
        }

        [Test]
        public void ACourseInAnEmptySceneIsTheStraightSegmentOrNone()
        {
            var waypoints = new List<Vector2>();
            bool found = SpatialBlendAPI.TryQueryEmbodiedCourse(_space, _actor, Vector2.zero, new Vector2(3f, 0f),
                new double[2 * 64], waypoints);

            if (found)
            {
                Assert.That(waypoints[waypoints.Count - 1].x, Is.EqualTo(3f).Within(1e-4f), "The course ends at the goal.");
            }
            else
            {
                Assert.That(waypoints, Is.Empty);
            }
        }
    }
}
#endif
