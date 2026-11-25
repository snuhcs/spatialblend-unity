// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpatialBlend.Motion
{
    /// <summary>Body-control backend a realized target is routed to (C-1).</summary>
    public enum EmbodimentBackend
    {
        Unrouted = 0,
        Navigation = 1,        // root travel through walkable space (room / region scope)
        UpperBodyFacing = 2,   // head/chest orientation toward a facing target
        RootAdjustment = 3,    // local root placement at an object / affordance
        Retargeting = 4,       // body pose on a support (K2 executor / seat pose)
    }

    /// <summary>Interaction-hierarchy level of a channel's referent.</summary>
    public enum EmbodimentScopeLevel { Unknown = 0, Room = 1, Region = 2, Object = 3, Affordance = 4 }

    /// <summary>Maps a native referent element to its hierarchy level. Supplied by the app's scene hierarchy.</summary>
    public interface IEmbodimentScopeResolver
    {
        EmbodimentScopeLevel Resolve(RichKnownInt64 referent);
    }

    /// <summary>One routing decision for one rich (anchor, family) channel.</summary>
    public readonly struct EmbodimentRoute
    {
        public EmbodimentRoute(string anchor, RichRelationFamily family, RichGoalKind goal, RichKnownInt64 referent,
            EmbodimentScopeLevel scope, EmbodimentBackend backend, string reason, double weight)
        { Anchor = anchor; Family = family; GoalKind = goal; Referent = referent; Scope = scope; Backend = backend; Reason = reason; Weight = weight; }
        public string Anchor { get; }
        public RichRelationFamily Family { get; }
        public RichGoalKind GoalKind { get; }
        public RichKnownInt64 Referent { get; }
        public EmbodimentScopeLevel Scope { get; }
        public EmbodimentBackend Backend { get; }
        /// <summary>Stable reason code: routed, no-goal, awaiting-engagement (non-failure: source not engaged yet), unrecognized-disposition, zero-weight, solver-failed, scope-unknown, scope-mismatch, goal-family-mismatch, snapshot-not-complete.</summary>
        public string Reason { get; }
        public double Weight { get; }
    }

    /// <summary>
    /// Routes each realized rich channel to one body-control
    /// backend by family, goal kind and referent scope level:
    ///   Spacing/RootPosition at Room or Region scope → Navigation;
    ///   Spacing/RootPosition at Object or Affordance scope → RootAdjustment;
    ///   Facing/FacingDirection at Region, Object or Affordance scope → UpperBodyFacing;
    ///   AffordancePose/SupportPose|SoftGeometricPose at Object or Affordance scope → Retargeting.
    /// A zero-weight channel is not routed. Anything else is Unrouted with an
    /// explicit reason (scope-unknown, scope-mismatch, ...); a scope is never guessed. Routing only: it drives no backend and makes no body
    /// claim (C-4: body claims are verified on the actual rig).
    /// </summary>
    public static class EmbodimentDispatcher
    {
        public const string LogSchema = "OSROUTING1";

        public static IReadOnlyList<EmbodimentRoute> Route(RichRealizationSnapshot snapshot, IEmbodimentScopeResolver scopes)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (scopes == null) throw new ArgumentNullException(nameof(scopes));
            var routes = new List<EmbodimentRoute>(snapshot.Channels.Count);
            bool complete = snapshot.Header.SnapshotStatus == RichSnapshotStatus.Complete;
            foreach (var channel in snapshot.Channels)
            {
                EmbodimentScopeLevel scope = channel.ReferentId.Known ? scopes.Resolve(channel.ReferentId) : EmbodimentScopeLevel.Unknown;
                EmbodimentBackend backend = EmbodimentBackend.Unrouted;
                string reason;
                if (!complete) reason = "snapshot-not-complete";
                else if (channel.Status == RichSolverStatus.Failed) reason = "solver-failed";
                else if (HasContributor(channel, RichContributorDisposition.Unrecognized)) reason = "unrecognized-disposition";
                else if (!HasContributor(channel, RichContributorDisposition.Included) &&
                         HasContributor(channel, RichContributorDisposition.AwaitingEngagement)) reason = "awaiting-engagement";
                else if (!channel.HasGoal || channel.GoalKind == RichGoalKind.None) reason = "no-goal";
                else if (!(channel.Weight > 0)) reason = "zero-weight";
                else if (scope == EmbodimentScopeLevel.Unknown) reason = "scope-unknown";
                else
                {
                    switch (channel.Family)
                    {
                        case RichRelationFamily.Spacing when channel.GoalKind == RichGoalKind.RootPosition:
                            backend = scope == EmbodimentScopeLevel.Room || scope == EmbodimentScopeLevel.Region
                                ? EmbodimentBackend.Navigation : EmbodimentBackend.RootAdjustment;
                            reason = "routed"; break;
                        case RichRelationFamily.Facing when channel.GoalKind == RichGoalKind.FacingDirection:
                            if (scope == EmbodimentScopeLevel.Room) reason = "scope-mismatch";
                            else { backend = EmbodimentBackend.UpperBodyFacing; reason = "routed"; }
                            break;
                        case RichRelationFamily.AffordancePose when channel.GoalKind == RichGoalKind.SupportPose ||
                                                                   channel.GoalKind == RichGoalKind.SoftGeometricPose:
                            if (scope == EmbodimentScopeLevel.Object || scope == EmbodimentScopeLevel.Affordance)
                            { backend = EmbodimentBackend.Retargeting; reason = "routed"; }
                            else reason = "scope-mismatch";
                            break;
                        default:
                            reason = "goal-family-mismatch"; break;
                    }
                }
                routes.Add(new EmbodimentRoute(channel.Anchor, channel.Family, channel.GoalKind, channel.ReferentId,
                    scope, backend, reason, channel.Weight));
            }
            return routes.AsReadOnly();
        }

        private static bool HasContributor(RichRealizationChannel channel, RichContributorDisposition disposition)
        {
            foreach (var contributor in channel.Contributors) if (contributor.Disposition == disposition) return true;
            return false;
        }

        /// <summary>One routing-log JSONL row for a snapshot (per receiver tick).</summary>
        public static string ToLogLine(RichRealizationSnapshot snapshot, IReadOnlyList<EmbodimentRoute> routes)
        {
            var inv = CultureInfo.InvariantCulture;
            var row = new StringBuilder(256 + 160 * routes.Count);
            var h = snapshot.Header;
            row.Append("{\"schema\":\"").Append(LogSchema).Append("\",\"revision\":").Append(h.Revision.ToString(inv))
               .Append(",\"source_sequence\":").Append(h.SourceSequence.Known ? h.SourceSequence.Value.ToString(inv) : "null")
               .Append(",\"routes\":[");
            for (int i = 0; i < routes.Count; ++i)
            {
                var r = routes[i];
                if (i > 0) row.Append(',');
                row.Append("{\"anchor\":\"").Append(r.Anchor.Replace("\\", "\\\\").Replace("\"", "\\\""))
                   .Append("\",\"family\":\"").Append(r.Family).Append("\",\"goal\":\"").Append(r.GoalKind)
                   .Append("\",\"referent\":").Append(r.Referent.Known ? r.Referent.Value.ToString(inv) : "null")
                   .Append(",\"scope\":\"").Append(r.Scope).Append("\",\"backend\":\"").Append(r.Backend)
                   .Append("\",\"reason\":\"").Append(r.Reason).Append("\",\"weight\":").Append(r.Weight.ToString("R", inv)).Append('}');
            }
            return row.Append("]}").ToString();
        }
    }
}
