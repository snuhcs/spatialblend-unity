// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// One anchor's replay through the chart of the shared object that holds its source sample (ABI 4.14, <c>sb_read_anchor_chart</c>):
    /// what the source anchor does relative to the object, carried as is. The core computes it beside the Spacing position and the
    /// Facing yaw and does not choose between them; the entity composes it with what the driver writes (the hands of a Meta avatar are
    /// streamed relative to its head, so they are composed on the head's chart yaw while its head keeps the Facing yaw).
    /// </summary>
    internal readonly struct EmbodiedAnchorChart
    {
        internal readonly string AnchorName;
        internal readonly Vector3 Position;
        internal readonly float YawDegrees;

        internal EmbodiedAnchorChart(string anchorName, Vector3 position, float yawDegrees)
        {
            AnchorName = anchorName;
            Position = position;
            YawDegrees = yawDegrees;
        }
    }

    /// <summary>What one driver frame asks the avatar to do: the checked target frame and the anchor charts.</summary>
    internal sealed class EmbodiedFramePlan
    {
        private List<EmbodiedAnchorChart> _charts;   // allocated only by a frame that has a chart
        /// <summary>The anchors of this frame's proposal set that carry a chart (ABI 4.14). Empty: none does.</summary>
        internal IReadOnlyList<EmbodiedAnchorChart> Charts =>
            (IReadOnlyList<EmbodiedAnchorChart>)_charts ?? System.Array.Empty<EmbodiedAnchorChart>();
        internal void AddChart(in EmbodiedAnchorChart chart) => (_charts ??= new List<EmbodiedAnchorChart>(2)).Add(chart);
        /// <summary>The previous plan carried charts and this one carries none: the entity drops them.</summary>
        internal bool ChartsCleared;
        internal bool HasChartWork => Charts.Count > 0 || ChartsCleared;
        // Typed plans retain double goals and ordered immutable event history. They
        // never tunnel continuous yaw authority through the legacy Quaternion writes.
        internal EmbodiedTargetFrame TargetEvents;

        /// <summary>Nothing to write but (possibly) the charts.</summary>
        internal bool IsEmptyWithoutCharts => TargetEvents == null || !TargetEvents.HasWork;

        internal bool IsEmpty => IsEmptyWithoutCharts && !HasChartWork;
    }

    /// <summary>
    /// The app side of the core contract for one received avatar, and the only one (design A*): publish the embodied
    /// poses, read the latest proposals (status metadata and charts), drain the typed target events, apply them. A frame is <see cref="PublishPose"/>,
    /// <see cref="TryReadProposals"/>, <see cref="PollEvents"/>, <see cref="Plan"/> (no side effects on the avatar)
    /// and <see cref="Apply"/>. The driver is the only writer of the avatar's anchors, and the only publisher of their
    /// poses. <see cref="OvrAvatarEmbodiedDriver"/> is the implementation.
    /// </summary>
    internal interface IEmbodiedDriver
    {
        /// <summary>The epoch of the root poses this driver publishes; it bumps whenever the body is placed from outside.</summary>
        ulong Epoch { get; }

        /// <summary>Publishes where the root and the direct head stand now, each with its own epoch, sequence and placement flag. Returns the root's pose.</summary>
        EmbodiedPose PublishPose();

        /// <summary>Reads the core's latest proposal set for this avatar. False when there is none to read.</summary>
        bool TryReadProposals(ProposalSet proposals);

        /// <summary>Drains the core's pending typed events for this avatar into the driver's goals (<paramref name="events"/> stays empty).</summary>
        void PollEvents(List<ActorEvent> events);

        /// <summary>What the avatar should do this frame. No side effects on the avatar.</summary>
        EmbodiedFramePlan Plan(in EmbodiedPose embodied, ProposalSet proposals, IReadOnlyList<ActorEvent> events, float dtSeconds);

        /// <summary>Applies a planned frame to the avatar.</summary>
        void Apply(EmbodiedFramePlan plan);

        /// <summary>The body is placed here from outside (the arrival): written, then a new epoch is published.</summary>
        void Place(Vector3 position, Quaternion rotation);

        /// <summary>The body was moved from outside: a new epoch, so the core drops what it derived from the old pose.</summary>
        void Reset();

        /// <summary>True when this driver writes the anchor (the root, the direct head, a bone-bound anchor), so no other path may.</summary>
        bool OwnsAnchor(string anchorName);
    }
}
