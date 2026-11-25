// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public sealed class FoundationRichRealizationTests
    {
        private static RichRealizationSnapshot Snapshot()
        {
            var header = new RichRealizationHeader(9223372036854775806L, -9, 18446744073709551614UL,
                18446744073709551613UL, RichSnapshotStatus.Complete,
                RichSourceProvenanceMode.TimestampedObservation, new RichKnownUInt64(true, 3),
                new RichKnownInt64(true, 4), new RichKnownInt64(true, 0), new RichKnownDouble(true, 1.25),
                new RichKnownDouble(true, 2.5), new RichKnownUInt64(true, 1),
                new RichKnownUInt64(true, 9), new RichKnownUInt64(true, 99),
                new RichKnownUInt64(true, 101), new RichKnownInt64(true, 12),
                new RichKnownInt64(true, 13), new RichKnownInt64(true, 14),
                new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), 0, 15, 0);
            var contributor = new RichRealizationContributor(new RichKnownInt64(true, -11),
                new RichKnownInt64(true, 12), new RichKnownInt64(true, 13),
                new RichKnownInt64(true, 14), new RichKnownUInt64(false), 0, .5, true,
                new RichPosition(1, 2, 3), RichGroundingMode.ExplicitSubRegion,
                RichContributorDisposition.Included, "valid", false, false, "", false,
                new RichMatrix4x4(new double[16]));
            var channel = new RichRealizationChannel("hand", RichRelationFamily.AffordancePose,
                RichSolverStatus.Converged, RichGeometry.ExactSupport, RichGoalKind.SupportPose,
                true, new RichKnownInt64(true, -11), .25, new RichPosition(1, 2, 3), true,
                .5, .75, true, 4, 5, false, 0, new[] { contributor });
            return new RichRealizationSnapshot(header, new[] { channel });
        }

        [Test]
        public void JsonIsDeterministicInvariantAndLosslessFor64BitValues()
        {
            CultureInfo old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string json = Snapshot().ToDeterministicJson();
                StringAssert.Contains("\"schema\":\"spatialblend.rich_realization\"", json);
                StringAssert.Contains("\"producer_lifetime\":\"18446744073709551614\"", json);
                StringAssert.Contains("\"receiver_actor_id\":\"-9\"", json);
                StringAssert.Contains("\"native_solve_nanoseconds\":{\"known\":true,\"value\":\"101\"}", json);
                StringAssert.Contains("\"arrival_seconds\":0.25", json);
                StringAssert.DoesNotContain("0,25", json);
            }
            finally { CultureInfo.CurrentCulture = old; }
        }

        private static RichRealizationContributor Contributor(RichContributorDisposition disposition) =>
            new RichRealizationContributor(new RichKnownInt64(true, -11), new RichKnownInt64(true, 12), new RichKnownInt64(true, 13),
                new RichKnownInt64(true, 14), new RichKnownUInt64(false), 0, .5, true, new RichPosition(1, 2, 3),
                RichGroundingMode.ExplicitSubRegion, disposition, "gate open", false, false, "", false, new RichMatrix4x4(new double[16]));

        private static string DispositionOnWire(RichRealizationContributor contributor)
        {
            var channel = new RichRealizationChannel("seat_contact", RichRelationFamily.AffordancePose, RichSolverStatus.Converged,
                RichGeometry.ExactSupport, RichGoalKind.SupportPose, true, new RichKnownInt64(true, -11), .25, new RichPosition(1, 2, 3),
                true, .5, .75, true, 4, 5, false, 0, new[] { contributor });
            string json = new RichRealizationSnapshot(Snapshot().Header, new[] { channel }).ToDeterministicJson();
            Assert.That(AnchorPoseJson.TryParse(json, out var root, out string error), Is.True, error);
            return root.Object["channels"].Array[0].Object["contributors"].Array[0].Object["disposition"].String;
        }

        // Core disposition 5 = SB_RICH_DISPOSITION_AWAITING_ENGAGEMENT (gate open,
        // source not engaged). Wire name: "awaiting_engagement".
        [Test]
        public void AwaitingEngagementDispositionRoundTripsAndUnknownValuesNeverThrow()
        {
            var awaiting = Contributor((RichContributorDisposition)5);
            Assert.That(awaiting.Disposition, Is.EqualTo(RichContributorDisposition.AwaitingEngagement));
            Assert.That(awaiting.IsAwaitingEngagement, Is.True);
            Assert.That(awaiting.RawDisposition, Is.EqualTo(5));
            string wire = DispositionOnWire(awaiting);
            Assert.That(wire, Is.EqualTo("awaiting_engagement"));
            Assert.That(RichContributorDispositionWire.FromWire(wire), Is.EqualTo(RichContributorDisposition.AwaitingEngagement));
            Assert.That((int)RichContributorDispositionWire.FromWire(wire), Is.EqualTo(5));
            Assert.That(RichContributorDispositionWire.FromNative(5), Is.EqualTo(RichContributorDisposition.AwaitingEngagement));
            // Existing values keep their names and numbers (backward compatible with the current core).
            foreach (var (value, name) in new[] { (0, "included"), (1, "disabled"), (2, "unmapped"), (3, "grounding_failed"), (4, "unsupported") })
            {
                var contributor = Contributor((RichContributorDisposition)value);
                Assert.That((int)contributor.Disposition, Is.EqualTo(value));
                Assert.That(DispositionOnWire(contributor), Is.EqualTo(name));
                Assert.That((int)RichContributorDispositionWire.FromWire(name), Is.EqualTo(value));
            }
            // A future core value: explicit Unrecognized with the raw value kept, never an exception.
            RichRealizationContributor future = null;
            Assert.DoesNotThrow(() => future = Contributor((RichContributorDisposition)9));
            Assert.That(future.Disposition, Is.EqualTo(RichContributorDisposition.Unrecognized));
            Assert.That(future.RawDisposition, Is.EqualTo(9));
            Assert.That(DispositionOnWire(future), Is.EqualTo("unrecognized"));
            Assert.That(RichContributorDispositionWire.FromNative(9), Is.EqualTo(RichContributorDisposition.Unrecognized));
            Assert.That(RichContributorDispositionWire.FromNative(-3), Is.EqualTo(RichContributorDisposition.Unrecognized));
            Assert.That(RichContributorDispositionWire.FromWire("gate_something_new"), Is.EqualTo(RichContributorDisposition.Unrecognized));
        }

        [Test]
        public void EmptyRequiresNoChannelsAndOverlappingRevisionMasksReject()
        {
            var header = new RichRealizationHeader(1, 2, 3, 4, RichSnapshotStatus.Empty,
                RichSourceProvenanceMode.LegacyUnknown, new RichKnownUInt64(false), new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownDouble(false), new RichKnownDouble(false), new RichKnownUInt64(true, 1),
                new RichKnownUInt64(true, 2), new RichKnownUInt64(true, 3), new RichKnownUInt64(true, 4),
                new RichKnownInt64(false), new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), 0, 15, 0);
            Assert.That(new RichRealizationSnapshot(header, Array.Empty<RichRealizationChannel>()).Channels, Is.Empty);
            Assert.Throws<ArgumentException>(() => new RichRealizationHeader(1, 2, 3, 4,
                RichSnapshotStatus.Complete, RichSourceProvenanceMode.LegacyUnknown,
                new RichKnownUInt64(false), new RichKnownInt64(false), new RichKnownInt64(false), new RichKnownDouble(false),
                new RichKnownDouble(false), new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), new RichKnownInt64(false),
                new RichKnownInt64(false), new RichKnownInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), new RichKnownUInt64(false), 1, 1, 0));
        }

        [Test]
        public void InvalidUtf8AndNonFiniteValuesRejectBeforeSerialization()
        {
            Assert.Throws<ArgumentException>(() => new RichRealizationChannel("\ud800", RichRelationFamily.Facing,
                RichSolverStatus.Converged, RichGeometry.NotApplicable, RichGoalKind.FacingDirection, false,
                new RichKnownInt64(false), 0, new RichPosition(0, 0, 0), false, 0, 0,
                false, 0, 0, false, 0, Array.Empty<RichRealizationContributor>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RichKnownDouble(true, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RichKnownDouble(false, double.NaN));
        }

        [Test]
        public void TimestampedHeaderRejectsImpossibleClockAndSequence()
        {
            Assert.Throws<ArgumentException>(() => new RichRealizationHeader(1, 2, 3, 4,
                RichSnapshotStatus.Complete, RichSourceProvenanceMode.TimestampedObservation,
                new RichKnownUInt64(true, 1), new RichKnownInt64(true, 0), new RichKnownInt64(true, 0),
                new RichKnownDouble(true, 1), new RichKnownDouble(false),
                new RichKnownUInt64(true, 1), new RichKnownUInt64(true, 2),
                new RichKnownUInt64(true, 4), new RichKnownUInt64(true, 3),
                new RichKnownInt64(false), new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), 0, 15, 0));
        }

        [Test]
        public void WireRejectsUnknownTerminalClockAndOversizedUtf8Field()
        {
            var unknownClock = new RichRealizationHeader(1, 2, 3, 4,
                RichSnapshotStatus.Empty, RichSourceProvenanceMode.LegacyUnknown,
                new RichKnownUInt64(false), new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownDouble(false), new RichKnownDouble(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownInt64(false), new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), 0, 15, 0);
            Assert.Throws<InvalidOperationException>(() =>
                new RichRealizationSnapshot(unknownClock, Array.Empty<RichRealizationChannel>()).ToDeterministicJson());
            Assert.Throws<ArgumentException>(() => new RichRealizationChannel(
                new string('x', RichWire.MaxUtf8Bytes + 1), RichRelationFamily.Facing,
                RichSolverStatus.Converged, RichGeometry.NotApplicable, RichGoalKind.FacingDirection,
                false, new RichKnownInt64(false), 0, new RichPosition(0, 0, 0), false, 0, 0,
                false, 0, 0, false, 0, Array.Empty<RichRealizationContributor>()));
            var tooManyChannels = new List<RichRealizationChannel>();
            for (int index = 0; index <= RichWire.MaxChannels; ++index)
                tooManyChannels.Add(new RichRealizationChannel("cap-" + index,
                    RichRelationFamily.Spacing, RichSolverStatus.Converged,
                    RichGeometry.NotApplicable, RichGoalKind.RootPosition, true,
                    new RichKnownInt64(false), 0, new RichPosition(0, 0, 0), false,
                    0, 0, false, 0, 0, false, 0, Array.Empty<RichRealizationContributor>()));
            Assert.Throws<ArgumentException>(() => new RichRealizationSnapshot(Snapshot().Header, tooManyChannels));
        }

        [Test]
        public void InvalidEnumAndDuplicateContributorOrdinalReject()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RichRealizationChannel("bad",
                (RichRelationFamily)99, RichSolverStatus.Converged, RichGeometry.NotApplicable,
                RichGoalKind.None, false, new RichKnownInt64(false), 0, new RichPosition(0, 0, 0),
                false, 0, 0, false, 0, 0, false, 0, Array.Empty<RichRealizationContributor>()));
            RichRealizationSnapshot snapshot = Snapshot();
            RichRealizationChannel first = snapshot.Channels[0];
            var duplicateOrdinal = new RichRealizationChannel("other", first.Family, first.Status,
                first.Geometry, first.GoalKind, first.HasGoal, first.ReferentId, first.ArrivalSeconds,
                first.AdmittedPosition, first.HasZ, first.YawRadians, first.Weight,
                first.RequestedPositionKnown, first.RequestedX, first.RequestedY,
                first.ReachProjected, first.ReachDisplacementMetres, first.Contributors);
            Assert.Throws<ArgumentException>(() => new RichRealizationSnapshot(snapshot.Header,
                new[] { first, duplicateOrdinal }));
        }
    }

    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationRichRealizationNativeTests
    {
        [Test]
        public void FrozenNativeFeatureNegotiatesEveryLayoutAndClockEpoch()
        {
            Assert.That(SpatialBlend.Internal.SpatialBlendAPI.TryReadRichMonotonicNanoseconds(
                out ulong first, out ulong clockId, out ulong clockEpoch, out string error), Is.True, error);
            Assert.That(clockId, Is.Not.EqualTo(0));
            Assert.That(clockEpoch, Is.Not.EqualTo(0));
            Assert.That(SpatialBlend.Internal.SpatialBlendAPI.TryReadRichMonotonicNanoseconds(
                out ulong second, out ulong secondClockId, out ulong secondClockEpoch, out error), Is.True, error);
            Assert.That(secondClockId, Is.EqualTo(clockId));
            Assert.That(secondClockEpoch, Is.EqualTo(clockEpoch));
            Assert.That(second, Is.GreaterThanOrEqualTo(first));
        }

        [Test]
        public void NativeSourceReceiverCapturesResetTimestampedAndRetirementTransactions()
        {
            long session = SpatialBlendAPI.CreateSession();
            ulong lifetime = 0;
            long target = 0;
            var firstCallback = new List<RichRealizationSnapshot>();
            var replacementCallback = new List<RichRealizationSnapshot>();
            try
            {
                Assert.That(session, Is.Not.EqualTo(0));
                long source = SpatialBlendAPI.AddSpace(session);
                target = SpatialBlendAPI.AddSpace(session);
                ConfigureSpace(source, 900000000000000001L, 900000000000000003L,
                    out long sourceRoom, out _);
                ConfigureSpace(target, 900000000000000001L, 900000000000000003L,
                    out long targetRoom, out _);
                long sourceActor = SpatialBlendAPI.CreateActor(source);
                long receiverActor = SpatialBlendAPI.CreateActor(target);
                SpatialBlendAPI.AddAnchor(source, sourceActor, "root", Vector3.zero);
                SpatialBlendAPI.AddAnchor(target, receiverActor, "root", Vector3.zero);
                SpatialBlendAPI.SetActorElement(source, sourceActor, sourceRoom);
                SpatialBlendAPI.SetActorElement(target, receiverActor, targetRoom);
                SpatialBlendAPI.Bind(source, sourceActor, "root", SbKernelType.Spacing);
                // The core solves a receiver only from a pose its driver published (ABI 3 and 4: the driver is the only
                // writer of the sampling port); without one the receiver answers NO_SAMPLE and the snapshot is Failed. This
                // fixture plays the driver at the receiver's root, inside the room.
                Assert.That(SpatialBlendAPI.TryPublishAnchorPose(target, receiverActor, 0u,
                    new EmbodiedPose(new Vector3(1f, 0f, 1f), 0f), 0d, 1UL, 1UL, false), Is.True);
                Assert.That(SpatialBlendAPI.EnableActorObservationFrames(session), Is.True);
                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(target,
                    snapshot => firstCallback.Add(snapshot), out string error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryActivateRichRealization(target, receiverActor,
                    out lifetime, out error), Is.True, error);

                ulong epoch = SpatialBlendAPI.BeginActorObservationEpoch(source, sourceActor, "root");
                Assert.That(epoch, Is.Not.EqualTo(0));
                Assert.That(firstCallback.Count, Is.EqualTo(1));
                Assert.That(firstCallback[0].Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Empty));
                Assert.That(firstCallback[0].Header.SourceProvenanceMode,
                    Is.EqualTo(RichSourceProvenanceMode.ObservationReset));
                Assert.That(firstCallback[0].Header.SourceEpoch.Known, Is.True);
                Assert.That(firstCallback[0].Header.SourceEpoch.Value, Is.EqualTo(epoch));

                var good = new[] { new SpatialBlendAPI.SbActorObservation
                { anchor_name = "root", position_x = 2, position_y = 2, position_z = 0, yaw = 0 } };
                Assert.That(SpatialBlendAPI.ApplyActorObservationFrame(source, sourceActor,
                    epoch, 0, 12.5, good), Is.True);
                Assert.That(firstCallback.Count, Is.EqualTo(2));
                RichRealizationSnapshot complete = firstCallback[1];
                Assert.That(complete.Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Complete));
                Assert.That(complete.Header.SourceProvenanceMode,
                    Is.EqualTo(RichSourceProvenanceMode.TimestampedObservation));
                Assert.That(complete.Header.SourceEpoch.Value, Is.EqualTo(epoch));
                Assert.That(complete.Header.SourceSequence.Value, Is.EqualTo(2));
                Assert.That(complete.Header.SourceObservationSequence.Known, Is.True);
                Assert.That(complete.Header.SourceObservationSequence.Value, Is.EqualTo(0));
                Assert.That(complete.Header.SourceTimeSeconds.Value, Is.EqualTo(12.5));
                Assert.That(complete.Header.NativeClockId.Value, Is.Not.EqualTo(0));
                Assert.That(complete.Header.NativeClockEpoch.Value, Is.Not.EqualTo(0));
                Assert.That(complete.Channels.Count, Is.GreaterThan(0));
                Assert.That(SpatialBlendAPI.TryPollRichRealization(lifetime, out RichRealizationSnapshot polled,
                    out int pollResult, out error), Is.True, error);
                Assert.That(pollResult, Is.EqualTo(1));
                Assert.That(polled.Header.Revision, Is.EqualTo(complete.Header.Revision));
                Assert.That(polled.Header.NativeReceiptNanoseconds.Value,
                    Is.EqualTo(complete.Header.NativeReceiptNanoseconds.Value));
                ulong completeRevision = complete.Header.Revision;

                Assert.That(SpatialBlendAPI.ApplyActorObservationFrame(source, sourceActor,
                    epoch, 1, 12.6, new[] { good[0], new SpatialBlendAPI.SbActorObservation
                    { anchor_name = "unknown", position_x = 0, position_y = 0, position_z = 0, yaw = double.NaN } }), Is.False);
                Assert.That(firstCallback.Count, Is.EqualTo(2));
                Assert.That(SpatialBlendAPI.TryPollRichRealization(lifetime, out _, out pollResult, out error), Is.True, error);
                Assert.That(pollResult, Is.EqualTo(0));

                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(target,
                    snapshot => replacementCallback.Add(snapshot), out error), Is.True, error);
                ulong resetEpoch = SpatialBlendAPI.BeginActorObservationEpoch(source, sourceActor, "root");
                Assert.That(resetEpoch, Is.GreaterThan(epoch));
                Assert.That(firstCallback.Count, Is.EqualTo(2), "replaced callback token must be fenced");
                Assert.That(replacementCallback.Count, Is.EqualTo(1));
                Assert.That(complete.Header.Revision, Is.EqualTo(completeRevision),
                    "borrowed native callback data must have been deeply copied");
                Assert.That(replacementCallback[0].Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Empty));
                Assert.That(replacementCallback[0].Header.SourceProvenanceMode,
                    Is.EqualTo(RichSourceProvenanceMode.ObservationReset));

                Assert.That(SpatialBlendAPI.TryRetireRichRealization(lifetime, out error), Is.True, error);
                Assert.That(replacementCallback.Count, Is.EqualTo(2));
                RichRealizationSnapshot retired = replacementCallback[1];
                Assert.That(retired.Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Empty));
                Assert.That(retired.Header.ProducerLifetime, Is.EqualTo(lifetime));
                ExportNativeFixtures(new Dictionary<string, RichRealizationSnapshot>
                {
                    { "observation_reset_empty", firstCallback[0] },
                    { "timestamped_complete", complete },
                    { "activation_retirement_empty", retired }
                });
                Assert.That(SpatialBlendAPI.TryReleaseRichRealization(lifetime, out int release, out error), Is.True, error);
                Assert.That(release, Is.EqualTo(1));
                lifetime = 0;
            }
            finally
            {
                if (target != 0) SpatialBlendAPI.TryUnregisterOnRichRealization(target, out _);
                if (lifetime != 0)
                {
                    SpatialBlendAPI.TryRetireRichRealization(lifetime, out _);
                    SpatialBlendAPI.TryReleaseRichRealization(lifetime, out _, out _);
                }
                if (session != 0) SpatialBlendAPI.DestroySession(session);
            }
        }

        private static void ConfigureSpace(long space, long roomId, long objectId,
                                           out long room, out long obstacle)
        {
            room = SpatialBlendAPI.AddElement(space);
            SpatialBlendAPI.SetElementSharedId(space, room, roomId);
            SpatialBlendAPI.SetElementPolygon(space, room, SbGeometryRole.Navigable,
                new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) });
            obstacle = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, obstacle, objectId);
            SpatialBlendAPI.SetElementBox(space, obstacle, SbGeometryRole.Obstacle,
                new Vector3(4, 0, 4), new Vector3(6, 1, 6));
        }

        private static void ExportNativeFixtures(IReadOnlyDictionary<string, RichRealizationSnapshot> cases)
        {
            string directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                "rich-realization-fixtures");
            Directory.CreateDirectory(directory);
            var manifest = new List<string>();
            var ordered = new List<KeyValuePair<string, RichRealizationSnapshot>>(cases);
            ordered.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
            foreach (KeyValuePair<string, RichRealizationSnapshot> entry in ordered)
            {
                string file = entry.Key + ".json";
                string json = entry.Value.ToDeterministicJson();
                File.WriteAllText(Path.Combine(directory, file), json, new UTF8Encoding(false));
                using (SHA256 sha = SHA256.Create())
                    manifest.Add("{\"label\":\"" + entry.Key + "\",\"evidence_type\":\"native_capture\",\"path\":\"" + file + "\",\"sha256\":\"" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant() + "\",\"snapshot_status\":\"" + WireName(entry.Value.Header.SnapshotStatus) + "\",\"source_provenance_mode\":\"" + WireName(entry.Value.Header.SourceProvenanceMode) + "\"}");
            }
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"schema\":\"spatialblend.rich_realization.native_manifest\",\"schema_version\":1,\"cases\":[" + string.Join(",", manifest) + "]}", new UTF8Encoding(false));
        }

        private static string WireName(Enum value)
        {
            var result = new StringBuilder();
            string name = value.ToString();
            for (int index = 0; index < name.Length; ++index)
            {
                if (index != 0 && char.IsUpper(name[index])) result.Append('_');
                result.Append(char.ToLowerInvariant(name[index]));
            }
            return result.ToString();
        }

        [Test]
        public void ManagedRichCleanupForgetsOnlyRequestedSpaceThenAllSpaces()
        {
            Type api = typeof(SpatialBlendAPI);
            var callbacks = (Dictionary<long, Action<RichRealizationSnapshot>>)api.GetField(
                "richCallbacks", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var tokens = (Dictionary<long, long>)api.GetField("richCallbackTokenBySpace",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var spaces = (Dictionary<long, long>)api.GetField("richCallbackSpaceByToken",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            SpatialBlendAPI.ForgetAllRichCallbacks();
            callbacks[101] = _ => Assert.Fail("stale callback invoked");
            callbacks[202] = _ => Assert.Fail("other session callback invoked");
            tokens[11] = 101; tokens[22] = 202;
            spaces[101] = 11; spaces[202] = 22;
            SpatialBlendAPI.ForgetRichCallbackForSpace(11);
            Assert.That(callbacks.ContainsKey(101), Is.False);
            Assert.That(tokens.ContainsKey(11), Is.False);
            Assert.That(spaces.ContainsKey(101), Is.False);
            Assert.That(callbacks.ContainsKey(202), Is.True, "other session remains live");
            MethodInfo stale = api.GetMethod("OnRichCallback", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.DoesNotThrow(() => stale.Invoke(null, new object[] { IntPtr.Zero, new IntPtr(101) }));
            Assert.That(SpatialBlendAPI.TryUnregisterOnRichRealization(11, out string error), Is.True, error);
            SpatialBlendAPI.ForgetAllRichCallbacks();
            Assert.That(callbacks, Is.Empty);
            Assert.That(tokens, Is.Empty);
            Assert.That(spaces, Is.Empty);
        }

        [Test]
        public void DestroySessionForgetsOnlyOwnedRichCallbackThenCleanupAllForgetsRemaining()
        {
            long firstSession = SpatialBlendAPI.CreateSession();
            long secondSession = SpatialBlendAPI.CreateSession();
            try
            {
                long firstSpace = SpatialBlendAPI.AddSpace(firstSession);
                long secondSpace = SpatialBlendAPI.AddSpace(secondSession);
                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(firstSpace, _ => { }, out string error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(secondSpace, _ => { }, out error), Is.True, error);
                Type api = typeof(SpatialBlendAPI);
                var callbacks = (Dictionary<long, Action<RichRealizationSnapshot>>)api.GetField(
                    "richCallbacks", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var tokens = (Dictionary<long, long>)api.GetField("richCallbackTokenBySpace",
                    BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(tokens.TryGetValue(firstSpace, out long oldToken), Is.True);
                Assert.That(tokens.ContainsKey(secondSpace), Is.True);
                SpatialBlendAPI.DestroySession(firstSession);
                firstSession = 0;
                Assert.That(tokens.ContainsKey(firstSpace), Is.False);
                Assert.That(callbacks.ContainsKey(oldToken), Is.False);
                Assert.That(tokens.ContainsKey(secondSpace), Is.True, "other session callback must survive");
                MethodInfo stale = api.GetMethod("OnRichCallback", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.DoesNotThrow(() => stale.Invoke(null, new object[] { IntPtr.Zero, new IntPtr(oldToken) }));
                SpatialBlendAPI.CleanupAll();
                secondSession = 0;
                Assert.That(callbacks, Is.Empty);
                Assert.That(tokens, Is.Empty);
            }
            finally
            {
                if (firstSession != 0) SpatialBlendAPI.DestroySession(firstSession);
                if (secondSession != 0) SpatialBlendAPI.DestroySession(secondSession);
                SpatialBlendAPI.ForgetAllRichCallbacks();
            }
        }
    }
}
