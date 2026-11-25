// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The head's chart and Facing on the driven (Sb) avatar head: the driver composes
    /// chart and Facing.
    ///
    /// Inside a containment-transfer leaf the core does not make the leaf (or its non-surface descendants) a Facing referent; instead it
    /// outputs, per anchor, the source anchor's own pose replayed through the leaf (ABI 4.14 anchor chart). One generic rule for any
    /// transfer leaf, by PRECEDENCE (not a blend): while the head anchor's proposal carries a chart, the checked head channel's yaw goal
    /// is the chart's yaw and Facing's typed yaw goal is suppressed (kept, not stepped); without a chart (outside any leaf) the head's
    /// yaw is Facing's goal, re-based to the nearest turn of the head's actual yaw when the chart ends (so it never unwinds the turns
    /// the chart made). Position is untouched (the head position stays the lease's composition); only the yaw is composed, so the rule
    /// does not need congruent leaves (the chart's VERIFIED flag is about the mapped position).
    ///
    /// The chart is read from the plan's own proposal set BEFORE the target frame is planned (so a chart alone is head work this frame)
    /// and paced like every other checked goal (EmbodiedTargetGoal: the v1 angular pace latched per proposal revision; the goal is
    /// yaw-only, its position is unused). Self-contained for a later lift: this file plus three hooks (the read in Plan, the work and
    /// step gates, the ordinary head step) and the pace resets beside the head goal's.
    /// </summary>
    internal sealed partial class OvrAvatarEmbodiedDriver
    {
        // The head chart's yaw (Unity degrees) and the revision of the proposal set it came from; null outside any transfer leaf.
        private (float yawDegrees, ulong revision)? _headChartYaw;
        private EmbodiedTargetGoal _headChartGoal;
        // The last unwrapped chart target (continuous radians): a new chart yaw is unwrapped against it, not against the actual yaw, so
        // a source yaw near the antipode does not flip the turn's direction between frames.
        private double? _headChartTarget;
        // The chart ended while Facing held a yaw goal: re-base that goal to the head's actual yaw at the next head step.
        private bool _rebaseFacingAfterChart;

        private bool HeadChartActive => _headChartYaw.HasValue;

        // Hook 1 (Plan, before PlanTargetFrame): the head anchor's chart of this set; a stale read keeps the last one (unknown, not
        // absent); no head row, or a row without a chart, ends it.
        private void ReadHeadChart(ProposalSet proposals)
        {
            if (!_targetHeadId.HasValue) { ClearHeadChartYaw(); return; }
            for (int i = 0; proposals != null && i < proposals.Anchors.Count; ++i)
            {
                AnchorProposal proposal = proposals.Anchors[i];
                if (proposal.AnchorId != _targetHeadId.Value) continue;
                if (proposal.HasChart && !float.IsNaN(proposal.ChartYawDegrees) && !float.IsInfinity(proposal.ChartYawDegrees))
                    _headChartYaw = (proposal.ChartYawDegrees, proposals.Revision);
                else if (!proposal.ChartStale)
                    ClearHeadChartYaw();
                return;
            }
            ClearHeadChartYaw();
        }

        private void ClearHeadChartYaw()
        {
            if (_headChartYaw.HasValue) _rebaseFacingAfterChart = true;
            _headChartYaw = null;
            _headChartGoal = null;   // the next leaf re-latches its pace
            _headChartTarget = null;
        }

        // A placement or a new epoch: the chart's pace starts afresh (as the head goal's), and the next chart yaw is unwrapped against the
        // head's yaw after the placement, not the target before it.
        private void ResetHeadChartPace()
        {
            _headChartGoal?.ResetPace();
            _headChartTarget = null;
        }

        // Hook 3 (the ordinary head step): the head's yaw step while it has a chart; false outside any transfer leaf (Facing decides).
        private bool TryStepHeadChartYaw(Vector3 headPosition, double actualYawRadians, float dt, out double step)
        {
            step = 0d;
            if (!_headChartYaw.HasValue)
            {
                if (_rebaseFacingAfterChart)
                {
                    _rebaseFacingAfterChart = false;
                    _headGoal?.RebaseYaw(actualYawRadians);
                }
                return false;
            }
            (float yawDegrees, ulong revision) = _headChartYaw.Value;
            double target = EmbodiedTargetConsumer.Nearest(_headChartTarget ?? actualYawRadians, yawDegrees * Math.PI / 180d);
            _headChartTarget = target;
            _headChartGoal ??= new EmbodiedTargetGoal(Config);
            _headChartGoal.Track(headPosition, target, revision);   // yaw-only: the position is not stepped
            step = _headChartGoal.StepYaw(actualYawRadians, dt);
            return true;
        }
    }
}
