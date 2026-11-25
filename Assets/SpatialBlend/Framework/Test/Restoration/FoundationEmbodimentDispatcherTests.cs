// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Motion;

namespace SpatialBlend.Tests
{
    // Routing only, on hand-authored correct targets
    // (component-contracts C: routing tests may inspect goals). No backend
    // is driven here and no body claim is made.
    [Category("FoundationReceiverInput")]
    public sealed class FoundationEmbodimentDispatcherTests
    {
        private sealed class Scopes : IEmbodimentScopeResolver
        {
            private readonly Dictionary<long, EmbodimentScopeLevel> _levels;
            internal Scopes(Dictionary<long, EmbodimentScopeLevel> levels) { _levels = levels; }
            public EmbodimentScopeLevel Resolve(RichKnownInt64 referent) =>
                referent.Known && _levels.TryGetValue(referent.Value, out var level) ? level : EmbodimentScopeLevel.Unknown;
        }
        private const long Room = 1, Region = 2, Sofa = 3, SeatPart = 4, Unregistered = 99;
        private static readonly Scopes Levels = new Scopes(new Dictionary<long, EmbodimentScopeLevel> {
            [Room] = EmbodimentScopeLevel.Room, [Region] = EmbodimentScopeLevel.Region,
            [Sofa] = EmbodimentScopeLevel.Object, [SeatPart] = EmbodimentScopeLevel.Affordance });

        private static RichRealizationChannel Channel(string anchor, RichRelationFamily family, RichGoalKind goal,
            long? referent, bool hasGoal = true, RichSolverStatus status = RichSolverStatus.Converged, double weight = 1,
            RichContributorDisposition? contributor = null, uint ordinal = 0) =>
            new RichRealizationChannel(anchor, family, status, RichGeometry.NotApplicable, goal, hasGoal,
                referent.HasValue ? new RichKnownInt64(true, referent.Value) : new RichKnownInt64(false),
                0, new RichPosition(0, 0, 0), false, 0, weight, false, 0, 0, false, 0,
                contributor.HasValue ? new[] { new RichRealizationContributor(new RichKnownInt64(true, 4), new RichKnownInt64(true, 1),
                    new RichKnownInt64(true, 2), new RichKnownInt64(true, 3), new RichKnownUInt64(false), ordinal, 1, false, new RichPosition(0, 0, 0),
                    RichGroundingMode.ExplicitSubRegion, contributor.Value, "gate", false, false, "", false, new RichMatrix4x4(new double[16])) }
                : new RichRealizationContributor[0]);

        private static RichRealizationSnapshot Snapshot(RichSnapshotStatus status, params RichRealizationChannel[] channels)
        {
            var unknown64 = new RichKnownInt64(false); var unknownU = new RichKnownUInt64(false); var unknownD = new RichKnownDouble(false);
            var header = new RichRealizationHeader(1, 2, 3, 4, status, RichSourceProvenanceMode.LegacyUnknown,
                unknownU, new RichKnownInt64(true, 5), unknown64, unknownD, unknownD, unknownU, unknownU, unknownU, unknownU,
                unknown64, unknown64, unknown64, unknownU, unknownU, unknownU, unknownU, 0, 15, status == RichSnapshotStatus.Failed ? 1u : 0u);
            return new RichRealizationSnapshot(header, channels);
        }

        [Test]
        public void DispatcherRoutesByFamilyGoalAndScopeLevelPerC1()
        {
            var cases = new (RichRealizationChannel, EmbodimentBackend, EmbodimentScopeLevel)[] {
                (Channel("locomotion", RichRelationFamily.Spacing, RichGoalKind.RootPosition, Room), EmbodimentBackend.Navigation, EmbodimentScopeLevel.Room),
                (Channel("head_facing", RichRelationFamily.Facing, RichGoalKind.FacingDirection, Sofa), EmbodimentBackend.UpperBodyFacing, EmbodimentScopeLevel.Object),
                (Channel("seat_contact", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, SeatPart), EmbodimentBackend.Retargeting, EmbodimentScopeLevel.Affordance),
                (Channel("hand_r", RichRelationFamily.AffordancePose, RichGoalKind.SoftGeometricPose, SeatPart), EmbodimentBackend.Retargeting, EmbodimentScopeLevel.Affordance),
            };
            var routes = EmbodimentDispatcher.Route(Snapshot(RichSnapshotStatus.Complete, cases.Select(c => c.Item1).ToArray()), Levels);
            Assert.That(routes.Count, Is.EqualTo(cases.Length));
            for (int i = 0; i < cases.Length; ++i)
            {
                Assert.That(routes[i].Backend, Is.EqualTo(cases[i].Item2), routes[i].Anchor);
                Assert.That(routes[i].Scope, Is.EqualTo(cases[i].Item3), routes[i].Anchor);
                Assert.That(routes[i].Reason, Is.EqualTo("routed"));
            }
            // The same root Spacing goal is navigation at room/region scope and
            // root adjustment once the referent is an object or affordance.
            foreach (var (referent, expected) in new[] { (Region, EmbodimentBackend.Navigation), (Sofa, EmbodimentBackend.RootAdjustment),
                (SeatPart, EmbodimentBackend.RootAdjustment) })
                Assert.That(EmbodimentDispatcher.Route(Snapshot(RichSnapshotStatus.Complete,
                    Channel("locomotion", RichRelationFamily.Spacing, RichGoalKind.RootPosition, referent)), Levels)[0].Backend,
                    Is.EqualTo(expected), "referent " + referent);
        }

        [Test]
        public void DispatcherNeverGuessesAndLogsExplicitUnroutedReasons()
        {
            var snapshot = Snapshot(RichSnapshotStatus.Complete,
                Channel("locomotion", RichRelationFamily.Spacing, RichGoalKind.RootPosition, Unregistered),
                Channel("chest", RichRelationFamily.Spacing, RichGoalKind.RootPosition, null),
                Channel("head_facing", RichRelationFamily.Facing, RichGoalKind.RootPosition, Sofa),
                Channel("seat_contact", RichRelationFamily.AffordancePose, RichGoalKind.None, SeatPart, hasGoal: false),
                Channel("hand_l", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, SeatPart, status: RichSolverStatus.Failed));
            var routes = EmbodimentDispatcher.Route(snapshot, Levels);
            Assert.That(routes.Select(r => r.Backend), Is.All.EqualTo(EmbodimentBackend.Unrouted));
            Assert.That(routes.Select(r => r.Reason), Is.EqualTo(new[] { "scope-unknown", "scope-unknown", "goal-family-mismatch", "no-goal", "solver-failed" }));
            // C-1 scope rules: Facing never routes at room scope, AffordancePose
            // only at object/affordance scope, and zero-weight channels never route.
            var scoped = EmbodimentDispatcher.Route(Snapshot(RichSnapshotStatus.Complete,
                Channel("head_facing", RichRelationFamily.Facing, RichGoalKind.FacingDirection, Room),
                Channel("seat_contact", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, Region),
                Channel("hand_r", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, Unregistered),
                Channel("locomotion", RichRelationFamily.Spacing, RichGoalKind.RootPosition, Room, weight: 0),
                Channel("chest", RichRelationFamily.Facing, RichGoalKind.FacingDirection, Region)), Levels);
            Assert.That(scoped.Select(r => r.Reason), Is.EqualTo(new[] { "scope-mismatch", "scope-mismatch", "scope-unknown", "zero-weight", "routed" }));
            Assert.That(scoped[4].Backend, Is.EqualTo(EmbodimentBackend.UpperBodyFacing));
            // Disposition 5 (awaiting engagement) is a non-failure "no target yet";
            // an unknown future disposition is explicit, never an exception.
            var gated = EmbodimentDispatcher.Route(Snapshot(RichSnapshotStatus.Complete,
                Channel("seat_contact", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, SeatPart, contributor: (RichContributorDisposition)5),
                Channel("hand_r", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, SeatPart, contributor: (RichContributorDisposition)9, ordinal: 1),
                Channel("hand_l", RichRelationFamily.AffordancePose, RichGoalKind.SupportPose, SeatPart, contributor: RichContributorDisposition.Included, ordinal: 2)), Levels);
            Assert.That(gated.Select(r => r.Reason), Is.EqualTo(new[] { "awaiting-engagement", "unrecognized-disposition", "routed" }));
            var failed = Snapshot(RichSnapshotStatus.Failed, Channel("locomotion", RichRelationFamily.Spacing, RichGoalKind.RootPosition, Room));
            Assert.That(EmbodimentDispatcher.Route(failed, Levels)[0].Reason, Is.EqualTo("snapshot-not-complete"));
            Assert.That(EmbodimentDispatcher.Route(Snapshot(RichSnapshotStatus.Empty), Levels), Is.Empty);

            string line = EmbodimentDispatcher.ToLogLine(snapshot, routes);
            Assert.That(AnchorPoseJson.TryParse(line, out var row, out string error), Is.True, error);
            Assert.That(row.Object["schema"].String, Is.EqualTo(EmbodimentDispatcher.LogSchema));
            Assert.That(row.Object["routes"].Array.Count, Is.EqualTo(5));
            Assert.That(row.Object["routes"].Array[1].Object["referent"].String, Is.Null);
            Assert.That(row.Object["routes"].Array[0].Object["backend"].String, Is.EqualTo("Unrouted"));
            Assert.That(row.Object["routes"].Array[0].Object["reason"].String, Is.EqualTo("scope-unknown"));
        }
    }
}
#endif
