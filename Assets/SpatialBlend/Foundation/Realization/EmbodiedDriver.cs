// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The one declared configuration of the embodied driver: the kinetic caps and the v1 pacing conventions.
    /// Design A* puts all of these in the driver. The core never sees them. Every number a reader might ask about is
    /// declared here, with its v1 citation, and nowhere else.
    /// </summary>
    internal readonly struct EmbodiedDriverConfig
    {
        /// <summary>Position speed cap, m/s: v1 <c>maxPositionVelocity</c> as <c>MetaAvatarPrefab</c> shipped it.</summary>
        internal const float DefaultMaxLinearSpeedMetersPerSecond = 2f;

        /// <summary>Yaw speed cap, degrees per second: v1 <c>maxAngularVelocity</c> as <c>MetaAvatarPrefab</c> shipped it.</summary>
        internal const float DefaultMaxAngularSpeedDegreesPerSecond = 150f;

        /// <summary>
        /// The duration the remaining distance and yaw are paced over: v1 <c>SbNetworkAvatarEntity._batchDuration</c>
        /// (0.033 s, one source update). v1's interpolator set the speed to <c>min(distance / _batchDuration, cap)</c>
        /// (<c>ApplyBatchPositionTarget</c>, one-sample branch) and <c>delta / _batchDuration</c> for yaw capped the same
        /// way (<c>ApplyBatchYawTarget</c>). It had no arrival budget: ABI 4 deletes the core's too,
        /// so this is the whole pacing rule.
        /// </summary>
        internal const float PacingBatchDurationSeconds = 0.033f;

        /// <summary>
        /// v1 <c>POSITION_SNAP_THRESHOLD</c>. Inside it the root's residual gives no motion heading, so its yaw is kept
        /// (<c>EmbodiedTargetGoal.StepPosition</c>).
        /// </summary>
        internal const float SnapPositionMeters = 0.01f;

        /// <summary>
        /// Linear acceleration bound, m/s^2, of every checked goal (root and head). v1 had none: its
        /// interpolator jumped to <c>min(distance / _batchDuration, cap)</c> from rest, which with a dense ~30 Hz stream was the
        /// human's own speed, but with one latched far target is an instant 0 to 2 m/s start that pops the body and skates the feet
        /// (the vendored legs controller only smooths the animation's speed parameter; it cannot move the body). It bounds the speed
        /// along the path, not a change of direction; there is no turn acceleration.
        /// </summary>
        internal const float MaxLinearAccelerationMetersPerSecondSquared = 4f;

        /// <summary>
        /// Linear braking bound, m/s^2, applied RELATIVE TO THE TARGET'S OWN MOTION, NavMeshAgent-style:
        /// <c>v &lt;= v_target_along + sqrt(2 * a_dec * d)</c> (stepped, so no step slows by more than <c>a_dec * dt</c>), where <c>v_target_along</c> is the target's velocity (from the goal's
        /// successive target positions, <see cref="TargetVelocityWindowSeconds"/>) along the direction to it and <c>d</c> the distance
        /// left. A stationary target gets a clean stop; a target moving with a dense stream is followed at its own speed, never trailing
        /// it by <c>v^2 / 2a</c> as a distance-only brake would.
        /// </summary>
        internal const float MaxLinearDecelerationMetersPerSecondSquared = 4f;

        /// <summary>
        /// The window, s, over which a goal measures its target's velocity: from the oldest target position within it to the newest,
        /// timed by arrival (the target events carry no source time), so that arrival jitter and frame aliasing average out. A dense
        /// source stream updates every ~0.033 s (v1 <c>_batchDuration</c>), so the window spans about 7 of its positions. A target
        /// with no other position within the window, or none renewed within it (a held target, a far v2 target, a stream sparser
        /// than 4 Hz), is at rest. The estimate is clamped to the speed cap; a displacement faster than twice the cap is a jump: the
        /// last estimate is held and the measurement restarts from it.
        /// </summary>
        internal const float TargetVelocityWindowSeconds = 0.25f;

        /// <summary>
        /// Height of a received avatar's head when it arrives: v1 <c>SbRetargetTransform.AVATAR_HEIGHT</c>
        /// (<c>SetHeadTransform</c> forced it), applied at spawn placement.
        /// </summary>
        internal const float SpawnHeightMeters = 1.58f;

        internal float MaxLinearSpeedMetersPerSecond { get; }
        internal float MaxAngularSpeedDegreesPerSecond { get; }

        private EmbodiedDriverConfig(float maxLinear, float maxAngularDegrees)
        {
            MaxLinearSpeedMetersPerSecond = maxLinear;
            MaxAngularSpeedDegreesPerSecond = maxAngularDegrees;
        }

        internal static EmbodiedDriverConfig Default => new EmbodiedDriverConfig(
            DefaultMaxLinearSpeedMetersPerSecond, DefaultMaxAngularSpeedDegreesPerSecond);

        /// <summary>
        /// Maps caps (m/s, degrees per second). Anything that is not finite and above zero is refused, because a
        /// driver with no bound cannot keep one.
        /// </summary>
        internal static bool TryFromCaps(float maxLinearMetersPerSecond, float maxAngularDegreesPerSecond,
                                         out EmbodiedDriverConfig config)
        {
            config = default;
            if (!IsFinitePositive(maxLinearMetersPerSecond) || !IsFinitePositive(maxAngularDegreesPerSecond))
            {
                return false;
            }
            config = new EmbodiedDriverConfig(maxLinearMetersPerSecond, maxAngularDegreesPerSecond);
            return true;
        }

        private static bool IsFinitePositive(float value) =>
            value > 0f && !float.IsInfinity(value) && !float.IsNaN(value);
    }

    /// <summary>
    /// One anchor's pose in Unity space (X right, Y up, Z forward, yaw about +Y in degrees). The root's height is its
    /// own; a pitch is carried only by an anchor that has one (the head).
    /// </summary>
    internal readonly struct EmbodiedPose
    {
        internal readonly Vector3 Position;
        internal readonly float YawDegrees;
        internal readonly bool HasPitch;
        internal readonly float PitchDegrees;

        internal EmbodiedPose(Vector3 position, float yawDegrees)
        {
            Position = position;
            YawDegrees = yawDegrees;
            HasPitch = false;
            PitchDegrees = 0f;
        }

        internal EmbodiedPose(Vector3 position, float yawDegrees, float pitchDegrees)
        {
            Position = position;
            YawDegrees = yawDegrees;
            HasPitch = true;
            PitchDegrees = pitchDegrees;
        }
    }

    /// <summary>How the core answered for one anchor. Mirrors <c>sb_proposal_status_t</c> (ABI 4).</summary>
    internal enum EmbodiedStatus
    {
        /// <summary>Nothing proposed yet, or the source released every relation: no target.</summary>
        None = 0,
        /// <summary>The driver has not published a valid pose for this anchor, so the core proposed nothing for it.</summary>
        NoSample = 1,
        /// <summary>The target is an exact minimiser (for an AffordancePose anchor: the mapped contact).</summary>
        Exact = 2,
        /// <summary>The best minimiser found; the relation error is not zero.</summary>
        Inexact = 3,
        // 4 (HOLD) is retired in ABI 4 and not renumbered.
        /// <summary>The solve hit its loop guard. Not a minimiser: the pose fields are diagnostic, not a target.</summary>
        NotConverged = 5
    }

    /// <summary>Why THIS proposal differs from the previous one. Mirrors <c>SB_PROPOSAL_CAUSE_*</c> (ABI 4).</summary>
    [Flags]
    internal enum EmbodiedCause : uint
    {
        None = 0,
        FirstProposal = 0x001,
        WeightSwitch = 0x002,
        ReferentSetChange = 0x004,
        // 0x008 (the old FOLD / BASIN_LOST, never set) is removed from the ABI; 0x80000 is the ABI 4.11 FOLD below.
        /// <summary>The source app raised an exception (a teleport, the first join, ...): the proposals are the absolute solve.</summary>
        SourceException = 0x010,
        ContainEnter = 0x020,
        ContainExit = 0x040,
        /// <summary>An AffordancePose anchor touched its surface (on that anchor's proposal).</summary>
        SeatEngage = 0x080,
        SeatRelease = 0x100,
        EmbodiedReset = 0x200,
        /// <summary>The Facing +-theta sign root changed. A normal bounded move for the driver.</summary>
        FacingRootSwitch = 0x400,
        /// <summary>Nothing proposed became a target, or the reverse. A normal bounded move for the driver.</summary>
        StatusChange = 0x800,
        /// <summary>The receiver scene changed. A normal bounded move for the driver.</summary>
        SceneChange = 0x1000,
        ClampBranch = 0x2000,
        DecodeBranch = 0x4000,
        Progress = 0x8000,
        ChartSnap = 0x10000,
        NoRequest = 0x20000,
        /// <summary>The Facing bearings tie within the arithmetic's error (or the path passes a look-at pole): the yaw may differ by the pass alone. A normal bounded move.</summary>
        FacingTie = 0x40000,
        /// <summary>
        /// ABI 4.11: an anchor's inner solve answered on another branch of a weighted pose term's distance with the same driving pose: the
        /// continuation crossed a fold of the objective. A normal bounded move for the driver (the proposal is a target like any other).
        /// </summary>
        Fold = 0x80000
    }

    /// <summary>Mirrors <c>SB_ANCHOR_PROPOSAL_FLAG_*</c> (ABI 4).</summary>
    [Flags]
    internal enum AnchorProposalFlags : uint
    {
        None = 0,
        /// <summary>Nothing authors this anchor's yaw: the driver faces its direction of motion, as v1 did.</summary>
        YawUnspecified = 0x1,
        /// <summary>The relation's geometric condition holds (on / touching the referent). No hysteresis.</summary>
        Engaged = 0x2,
        /// <summary>This anchor's own sample was published.</summary>
        Sampled = 0x4,
        /// <summary>The sampled anchor already satisfies the relation.</summary>
        Arrived = 0x8,
        /// <summary>
        /// ABI 4.1, NOT SET by the product since ABI 4.11: a no-request update (a static, receding or non-closing
        /// source) carries the anchor's CURRENT position as the proposal (a hold, cause <see cref="EmbodiedCause.NoRequest"/>), so every
        /// proposal with a target is a position to realize. The value stays reserved in the ABI; the driver never reads it, and a hold is
        /// never recognised by this flag.
        /// </summary>
        PositionUnspecified = 0x10,
        /// <summary>
        /// ABI 4.3, root anchor only: this update's root proposal IS the transfer mapping.
        /// Set exactly when the source root is inside a transfer leaf and this space has that leaf's counterpart. Recomputed
        /// by the core on every update; nothing is stored for it. The app switches the representation on
        /// <see cref="AnchorProposal.TransferArrived"/>, not on this flag alone.
        /// </summary>
        Transfer = 0x20,
        /// <summary>
        /// ABI 4.11: this anchor's proposal is its INNER solve: its position among its object's affordance poses inside the object's
        /// spatial definition (the source anchor is in the SD and not in contact), by the same relation solve as the root's, one level down.
        /// Position only: <see cref="YawUnspecified"/> is set. A target like any other for the driver; details in <c>sb_read_anchor_solve</c>.
        /// </summary>
        Inner = 0x40,
        /// <summary>
        /// ABI 4.11: the anchor is ENGAGED (seated) or INNER and the root has a Facing yaw: <c>sb_anchor_solve_t.facing_yaw</c> is the
        /// direction the head and upper body follow (core's Facing, not the user's raw head yaw).
        /// </summary>
        FacingYaw = 0x80
    }

    /// <summary>
    /// The proposal of ONE relation-bearing anchor, in Unity space. The core holds no pose: it computes this from the
    /// poses the driver published, and the driver does not store it as authority.
    /// </summary>
    internal struct AnchorProposal
    {
        /// <summary>The core's interned id of the anchor; 0 is the actor's root anchor.</summary>
        internal uint AnchorId;
        internal EmbodiedStatus Status;
        internal AnchorProposalFlags Flags;
        internal EmbodiedCause Cause;
        /// <summary>The proposed position. The root's Y is ignored (the driver keeps the body's own height); another anchor's Y is its own height.</summary>
        internal Vector3 Position;
        internal float YawDegrees;
        /// <summary>The proposed pitch, in degrees: the source's head pitch passed through by the core; valid when <see cref="HasPitch"/>.</summary>
        internal float PitchDegrees;
        internal bool HasPitch;
        internal long ReferentSharedId;
        /// <summary>
        /// ABI 4.12 (<c>sb_read_anchor_turn</c>): the SIGNED, UNWRAPPED yaw turn of this proposal from the yaw the anchor was sampled at,
        /// in Unity degrees (positive turns the way Unity yaw increases); valid when <see cref="HasYawTurn"/>. It may exceed 180 degrees
        /// while the core pursues (a Facing stepping the long way round, which the absolute <see cref="YawDegrees"/> carries as the shorter
        /// turn the other way); it is the shortest arc on arrival, a placement or an engaged pose, and 0 on a hold. False when the turn
        /// was not read (the call failed or answered another proposal): the driver then takes the shortest arc to <see cref="YawDegrees"/>.
        /// </summary>
        internal float YawTurnDegrees;
        internal bool HasYawTurn;
        /// <summary>
        /// The anchor carries no Spacing and no AffordancePose relation (the native <c>err_spacing_*</c> and <c>err_pose_*</c> are -1): it is a
        /// Facing-only anchor (<c>head</c>, <c>chest</c>), whose x, y, z are the position the core SAMPLED, not a goal.
        /// </summary>
        internal bool NoPositionRelation;
        /// <summary>
        /// ABI 4.14 (<c>sb_read_anchor_chart</c>): the source anchor's OWN sample replayed through the chart of the containment-transfer leaf
        /// that holds it (the full per-anchor transform near a shared object), a separate output beside the Spacing position and the Facing
        /// yaw: the driver composes them, the core does not choose. False when the anchor is in no leaf this space maps, or the library has
        /// no chart output.
        /// </summary>
        internal bool HasChart;
        /// <summary>The chart-mapped position of the source anchor, Unity world; valid with <see cref="HasChart"/>.</summary>
        internal Vector3 ChartPosition;
        /// <summary>The chart-mapped yaw of the source anchor, Unity degrees; valid with <see cref="HasChart"/>.</summary>
        internal float ChartYawDegrees;
        /// <summary>The transfer leaf (shared id) the chart maps through.</summary>
        internal long ChartLeafSharedId;
        /// <summary>The source's footprint of the leaf rode the update and is congruent with this space's counterpart.</summary>
        internal bool ChartVerified;
        /// <summary>The chart read answered another revision of the set (it was replaced between the two calls): unknown, not absent.</summary>
        internal bool ChartStale;

        internal bool YawUnspecified => (Flags & AnchorProposalFlags.YawUnspecified) != 0;
        internal bool Engaged => (Flags & AnchorProposalFlags.Engaged) != 0;
        internal bool Sampled => (Flags & AnchorProposalFlags.Sampled) != 0;
        internal bool Arrived => (Flags & AnchorProposalFlags.Arrived) != 0;
        /// <summary>Reserved since ABI 4.11 (never set); kept so a log shows it. The driver does not read it.</summary>
        internal bool PositionUnspecified => (Flags & AnchorProposalFlags.PositionUnspecified) != 0;
        internal bool Inner => (Flags & AnchorProposalFlags.Inner) != 0;
        internal bool FacingYaw => (Flags & AnchorProposalFlags.FacingYaw) != 0;
        internal bool Transfer => (Flags & AnchorProposalFlags.Transfer) != 0;
        /// <summary>
        /// The interaction value of the transfer: the transfer flag
        /// AND this anchor's ARRIVED, that is, on the realised body and not on the proposal. The representation switch, the
        /// local animation enable and the state-filter bypass read this; Start / Terminate are its frame-to-frame change.
        /// </summary>
        internal bool TransferArrived => Transfer && Arrived;
        internal bool CarriesTarget => Status == EmbodiedStatus.Exact || Status == EmbodiedStatus.Inexact;
    }

    /// <summary>The inner level of one anchor's proposal (ABI 4.11, <c>sb_anchor_solve_t</c>), in Unity space.</summary>
    internal struct AnchorSolve
    {
        internal uint AnchorId;
        internal AnchorProposalFlags Flags;
        internal int Iterations;
        internal long ObjectSharedId;
        /// <summary>The receiver SD's scale D, metres.</summary>
        internal double SdScaleMeters;
        /// <summary>J at the answer, metres squared.</summary>
        internal double ObjectiveJ;
        /// <summary>The direction the head and upper body follow, Unity yaw in degrees; valid with <see cref="AnchorProposalFlags.FacingYaw"/>.</summary>
        internal float FacingYawDegrees;
        internal string DrivingPoseTag;
    }

    /// <summary>
    /// One consistent answer of the core (<c>sb_proposal_set_t</c> and its anchors): every relation-bearing anchor of
    /// the actor, the root first, from one solve. Reused between frames.
    /// </summary>
    internal sealed class ProposalSet
    {
        internal ulong Revision;
        internal long SourceSequence;
        internal bool SampleValid;
        internal uint TotalAnchorCount;
        internal readonly List<AnchorProposal> Anchors = new List<AnchorProposal>();

        internal bool HasRoot => Anchors.Count > 0 && Anchors[0].AnchorId == 0;
        internal AnchorProposal Root => Anchors[0];

        internal void Clear()
        {
            Revision = 0;
            SourceSequence = 0;
            SampleValid = false;
            TotalAnchorCount = 0;
            Anchors.Clear();
        }

        internal bool TryGet(uint anchorId, out AnchorProposal proposal)
        {
            foreach (AnchorProposal anchor in Anchors)
            {
                if (anchor.AnchorId == anchorId)
                {
                    proposal = anchor;
                    return true;
                }
            }
            proposal = default;
            return false;
        }
    }

    /// <summary>What an actor event is. Mirrors <c>sb_actor_event_kind_t</c>.</summary>
    internal enum ActorEventKind
    {
        /// <summary>The source app raised an exception: an id and an opaque payload.</summary>
        SourceException = 1,
        /// <summary>Core asks this app to teleport an anchor to a pose: the maximum latency elapsed and it has not arrived.</summary>
        TeleportRequest = 2,
        /// <summary>ABI 4.13, opt-in: replace only this anchor's position target.</summary>
        PositionRequest = 3,
        /// <summary>ABI 4.13, opt-in: replace only this anchor's yaw target, preserving the signed producing turn.</summary>
        YawRequest = 4,
        /// <summary>ABI 4.13, opt-in: cancel only the named channels, even without a proposal snapshot.</summary>
        TargetRelease = 5
    }

    /// <summary>One event polled from the core. Legacy pose fields are Unity space; typed requests retain native double yaw.</summary>
    internal struct ActorEvent
    {
        internal ActorEventKind Kind;
        /// <summary>The proposal-set revision this event was raised with.</summary>
        internal ulong Revision;
        internal long SourceSequence;
        internal long SourceSharedId;
        /// <summary>SOURCE_EXCEPTION: the app-defined id (<see cref="SpatialBlendAPI.ExceptionJoin"/> for the first join).</summary>
        internal uint ExceptionId;
        /// <summary>SOURCE_EXCEPTION: the opaque payload, never interpreted by the core.</summary>
        internal byte[] Payload;
        /// <summary>TELEPORT_REQUEST: the <c>sb_interaction_kind_t</c> that timed out.</summary>
        internal int InteractionKind;
        /// <summary>TELEPORT_REQUEST: the anchor to place, and the pose to place it at.</summary>
        internal uint AnchorId;
        internal Vector3 Position;
        internal float YawDegrees;
        internal float PitchDegrees;
        internal bool HasPitch;
        /// <summary>POSITION_REQUEST / YAW_REQUEST / TARGET_RELEASE only; authoritative instead of the legacy float pose fields.</summary>
        internal TargetRequest TargetRequest;
    }

    /// <summary>
    /// The identity of the poses one embodied anchor publishes: the epoch (bumps when the anchor was placed or reset),
    /// the sequence (strictly increasing within an epoch, restarting at 1) and whether the next report follows a
    /// placement (<c>SB_EMBODIED_TELEPORTED</c>). One per anchor: every anchor has its own port.
    /// </summary>
    internal sealed class EmbodiedPoseClock
    {
        private ulong _sequence;
        private bool _teleported;

        internal ulong Epoch { get; private set; } = 1;

        internal ulong NextSequence() => ++_sequence;

        /// <summary>The anchor was placed or respawned: a new epoch, sequence restarting, the next report flagged.</summary>
        internal void NewEpoch()
        {
            ++Epoch;
            _sequence = 0;
            _teleported = true;
        }

        /// <summary>True once for the report that follows a placement.</summary>
        internal bool ConsumeTeleportedFlag()
        {
            bool flag = _teleported;
            _teleported = false;
            return flag;
        }
    }

    /// <summary>A stateless geometry query: a walkable course from one ground point to another, for one avatar.</summary>
    internal interface IEmbodiedCourse
    {
        /// <summary>Fills <paramref name="waypoints"/> (excluding <paramref name="from"/>, ending at or near <paramref name="to"/>). False when there is no course.</summary>
        bool TryQueryCourse(Vector2 from, Vector2 to, List<Vector2> waypoints);
    }

    /// <summary>
    /// The anchor-id constant of the embodied ABI. The proposal-following kinematics that lived here (v1 pacing toward each proposal,
    /// placement, the per-anchor pose clocks) are removed with the legacy driver frame: the checked driver's goals
    /// are EmbodiedTargetGoal (EmbodiedTargetConsumer.cs), paced by <see cref="EmbodiedDriverConfig"/>.
    /// </summary>
    internal static class EmbodiedDriver
    {
        /// <summary>The root anchor's id: 0 in every actor (<c>sb_get_anchor_id</c>).</summary>
        internal const uint RootAnchorId = 0;
    }
}
