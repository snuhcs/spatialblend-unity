// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace SpatialBlend
{
    // Mirrors sb_rich_snapshot_status_t.  "Empty" is a native result state, not
    // a managed-lifetime notification.
    public enum RichSnapshotStatus { Complete = 1, Empty = 2, Failed = 3 }
    public enum RichSourceProvenanceMode { LegacyUnknown = 0, TimestampedObservation = 1, ObservationReset = 2 }
    public enum RichRelationFamily { Facing = 0, Spacing = 1, AffordancePose = 2 }
    public enum RichSolverStatus { Converged = 0, Fallback = 1, Failed = 2 }
    public enum RichGeometry { NotApplicable = 0, ExactSupport = 1, BlendedSupport = 2, ReachProjected = 3, LegacyCenterNonContact = 4, Unsupported = 5 }
    public enum RichGoalKind { None = 0, RootPosition = 1, FacingDirection = 2, SupportPose = 3, SoftGeometricPose = 4 }
    // Mirrors sb_rich_contributor_disposition_t. AwaitingEngagement (5): the
    // activation gate is open but the source has not engaged yet (emitted on
    // approach frames) — a non-failure "no target yet" state. Unrecognized is
    // Unity-only: any native value this build does not know (a newer core)
    // maps to it, with the raw value kept in RawDisposition, instead of throwing.
    public enum RichContributorDisposition { Unrecognized = -1, Included = 0, Disabled = 1, Unmapped = 2, GroundingFailed = 3, Unsupported = 4, AwaitingEngagement = 5 }

    /// <summary>Wire names of contributor dispositions (snake_case of the enum member).</summary>
    public static class RichContributorDispositionWire
    {
        public static string ToWire(RichContributorDisposition value)
        {
            switch (value)
            {
                case RichContributorDisposition.Included: return "included";
                case RichContributorDisposition.Disabled: return "disabled";
                case RichContributorDisposition.Unmapped: return "unmapped";
                case RichContributorDisposition.GroundingFailed: return "grounding_failed";
                case RichContributorDisposition.Unsupported: return "unsupported";
                case RichContributorDisposition.AwaitingEngagement: return "awaiting_engagement";
                default: return "unrecognized";
            }
        }
        /// <summary>Unknown names map to Unrecognized; never throws.</summary>
        public static RichContributorDisposition FromWire(string name)
        {
            switch (name)
            {
                case "included": return RichContributorDisposition.Included;
                case "disabled": return RichContributorDisposition.Disabled;
                case "unmapped": return RichContributorDisposition.Unmapped;
                case "grounding_failed": return RichContributorDisposition.GroundingFailed;
                case "unsupported": return RichContributorDisposition.Unsupported;
                case "awaiting_engagement": return RichContributorDisposition.AwaitingEngagement;
                default: return RichContributorDisposition.Unrecognized;
            }
        }
        /// <summary>Native int → enum; values unknown to this build map to Unrecognized.</summary>
        public static RichContributorDisposition FromNative(int value) =>
            value >= 0 && value <= 5 ? (RichContributorDisposition)value : RichContributorDisposition.Unrecognized;
    }
    public enum RichGroundingMode { ExplicitSubRegion = 0, WholePlanarBoundary = 1, LegacyCenterFallback = 2 }

    public readonly struct RichKnownUInt64
    {
        public RichKnownUInt64(bool known, ulong value = 0) { Known = known; Value = value; }
        public bool Known { get; } public ulong Value { get; }
    }
    public readonly struct RichKnownInt64
    {
        public RichKnownInt64(bool known, long value = 0) { Known = known; Value = value; }
        public bool Known { get; } public long Value { get; }
    }
    public readonly struct RichKnownDouble
    {
        public RichKnownDouble(bool known, double value = 0)
        {
            if (!Finite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Known = known; Value = value;
        }
        public bool Known { get; } public double Value { get; }
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
    public readonly struct RichPosition
    {
        public RichPosition(double x, double y, double z)
        {
            if (!RichKnownDouble.Finite(x) || !RichKnownDouble.Finite(y) || !RichKnownDouble.Finite(z))
                throw new ArgumentOutOfRangeException(nameof(x));
            X = x; Y = y; Z = z;
        }
        public double X { get; } public double Y { get; } public double Z { get; }
    }
    public sealed class RichMatrix4x4
    {
        private readonly ReadOnlyCollection<double> _values;
        public RichMatrix4x4(IReadOnlyList<double> values)
        {
            if (values == null || values.Count != 16) throw new ArgumentException("Matrix must have 16 values.", nameof(values));
            var copy = new double[16];
            for (int i = 0; i < copy.Length; ++i)
            {
                if (!RichKnownDouble.Finite(values[i])) throw new ArgumentOutOfRangeException(nameof(values));
                copy[i] = values[i];
            }
            _values = Array.AsReadOnly(copy);
        }
        public IReadOnlyList<double> Values => _values;
    }
    public sealed class RichRealizationHeader
    {
        public RichRealizationHeader(long receiverSpaceId, long receiverActorId, ulong producerLifetime,
                                     ulong revision, RichSnapshotStatus snapshotStatus,
                                     RichSourceProvenanceMode sourceProvenanceMode,
                                     RichKnownUInt64 sourceEpoch, RichKnownInt64 sourceSequence,
                                     RichKnownInt64 sourceObservationSequence,
                                     RichKnownDouble sourceTimeSeconds, RichKnownDouble sourceClockSeconds,
                                     RichKnownUInt64 nativeClockId, RichKnownUInt64 nativeClockEpoch,
                                     RichKnownUInt64 nativeReceiptNanoseconds, RichKnownUInt64 nativeSolveNanoseconds,
                                     RichKnownInt64 sourceActorSpace, RichKnownInt64 sourceActorElement,
                                     RichKnownInt64 relaySourceSpace, RichKnownUInt64 policyRevision,
                                     RichKnownUInt64 receiverTreeRevision, RichKnownUInt64 counterpartRevision,
                                     RichKnownUInt64 supportRevision, uint knownRevisionMask,
                                     uint unknownRevisionMask, uint failureCode)
        {
            if (producerLifetime == 0 || revision == 0) throw new ArgumentOutOfRangeException(nameof(producerLifetime));
            if (snapshotStatus != RichSnapshotStatus.Complete && snapshotStatus != RichSnapshotStatus.Empty && snapshotStatus != RichSnapshotStatus.Failed)
                throw new ArgumentOutOfRangeException(nameof(snapshotStatus));
            if (sourceProvenanceMode != RichSourceProvenanceMode.LegacyUnknown && sourceProvenanceMode != RichSourceProvenanceMode.TimestampedObservation && sourceProvenanceMode != RichSourceProvenanceMode.ObservationReset)
                throw new ArgumentOutOfRangeException(nameof(sourceProvenanceMode));
            if (nativeClockId.Known != nativeClockEpoch.Known || (nativeClockId.Known && (nativeClockId.Value == 0 || nativeClockEpoch.Value == 0)) || nativeReceiptNanoseconds.Known != nativeSolveNanoseconds.Known || (nativeReceiptNanoseconds.Known && nativeReceiptNanoseconds.Value > nativeSolveNanoseconds.Value))
                throw new ArgumentException("Native clock identity and receipt/solve interval are required.");
            if (sourceProvenanceMode != RichSourceProvenanceMode.LegacyUnknown && (!sourceEpoch.Known || sourceEpoch.Value == 0 || !sourceSequence.Known || sourceSequence.Value <= 0))
                throw new ArgumentException("Causal observations require epoch and positive relation sequence.");
            if (sourceProvenanceMode == RichSourceProvenanceMode.TimestampedObservation && (!sourceTimeSeconds.Known || (sourceObservationSequence.Known && sourceObservationSequence.Value < 0)))
                throw new ArgumentException("Timestamped observations require epoch, positive sequence, and source time.");
            if (sourceProvenanceMode != RichSourceProvenanceMode.TimestampedObservation && sourceObservationSequence.Known)
                throw new ArgumentException("Only timestamped observations carry an input observation sequence.");
            if ((sourceTimeSeconds.Known && sourceTimeSeconds.Value < 0) || (sourceClockSeconds.Known && sourceClockSeconds.Value <= 0))
                throw new ArgumentException("Known source times must be non-negative/positive respectively.");
            if (failureCode > 1 || (failureCode == 1 && snapshotStatus != RichSnapshotStatus.Failed))
                throw new ArgumentException("Failure code is incompatible with snapshot status.");
            if ((knownRevisionMask | unknownRevisionMask) != 15 || (knownRevisionMask & ~15u) != 0 || (unknownRevisionMask & ~15u) != 0)
                throw new ArgumentException("Revision masks must classify all known revision slots.");
            if (policyRevision.Known != ((knownRevisionMask & 1) != 0) || receiverTreeRevision.Known != ((knownRevisionMask & 2) != 0) || counterpartRevision.Known != ((knownRevisionMask & 4) != 0) || supportRevision.Known != ((knownRevisionMask & 8) != 0))
                throw new ArgumentException("Revision value knownness must match revision masks.");
            ReceiverSpaceId = receiverSpaceId; ReceiverActorId = receiverActorId;
            ProducerLifetime = producerLifetime; Revision = revision; SnapshotStatus = snapshotStatus;
            SourceProvenanceMode = sourceProvenanceMode; SourceEpoch = sourceEpoch; SourceSequence = sourceSequence; SourceObservationSequence = sourceObservationSequence;
            SourceTimeSeconds = sourceTimeSeconds; SourceClockSeconds = sourceClockSeconds;
            NativeClockId = nativeClockId; NativeClockEpoch = nativeClockEpoch;
            NativeReceiptNanoseconds = nativeReceiptNanoseconds; NativeSolveNanoseconds = nativeSolveNanoseconds;
            SourceActorSpace = sourceActorSpace; SourceActorElement = sourceActorElement; RelaySourceSpace = relaySourceSpace;
            if ((knownRevisionMask & unknownRevisionMask) != 0) throw new ArgumentException("Revision masks overlap.");
            PolicyRevision = policyRevision;
            ReceiverTreeRevision = receiverTreeRevision; CounterpartRevision = counterpartRevision;
            SupportRevision = supportRevision; KnownRevisionMask = knownRevisionMask;
            UnknownRevisionMask = unknownRevisionMask; FailureCode = failureCode;
        }
        public long ReceiverSpaceId { get; } public long ReceiverActorId { get; }
        public ulong ProducerLifetime { get; } public ulong Revision { get; }
        public RichSnapshotStatus SnapshotStatus { get; } public RichSourceProvenanceMode SourceProvenanceMode { get; }
        public RichKnownUInt64 SourceEpoch { get; } public RichKnownInt64 SourceSequence { get; }
        public RichKnownInt64 SourceObservationSequence { get; }
        public RichKnownDouble SourceTimeSeconds { get; } public RichKnownDouble SourceClockSeconds { get; }
        public RichKnownUInt64 NativeClockId { get; } public RichKnownUInt64 NativeClockEpoch { get; }
        public RichKnownUInt64 NativeReceiptNanoseconds { get; } public RichKnownUInt64 NativeSolveNanoseconds { get; }
        public RichKnownInt64 SourceActorSpace { get; } public RichKnownInt64 SourceActorElement { get; }
        public RichKnownInt64 RelaySourceSpace { get; } public RichKnownUInt64 PolicyRevision { get; }
        public RichKnownUInt64 ReceiverTreeRevision { get; } public RichKnownUInt64 CounterpartRevision { get; }
        public RichKnownUInt64 SupportRevision { get; } public uint KnownRevisionMask { get; }
        public uint UnknownRevisionMask { get; } public uint FailureCode { get; }
    }
    public sealed class RichRealizationContributor
    {
        public RichRealizationContributor(RichKnownInt64 referentId, RichKnownInt64 sourceSpace,
                                           RichKnownInt64 sourceElement, RichKnownInt64 resolvedLocalElement,
                                           RichKnownUInt64 bindingRevision, uint snapshotOrdinal,
                                           double appliedWeight, bool hasMappedSupport,
                                           RichPosition mappedSupport, RichGroundingMode groundingMode,
                                           RichContributorDisposition disposition, string reason,
                                           bool chartVersionKnown, bool bindingRevisionKnown,
                                           string supportIdentifier, bool supportPlaneKnown,
                                           RichMatrix4x4 supportPlaneToWorld)
        {
            if (!RichKnownDouble.Finite(appliedWeight) || appliedWeight < 0) throw new ArgumentOutOfRangeException(nameof(appliedWeight));
            if (!Enum.IsDefined(typeof(RichGroundingMode), groundingMode))
                throw new ArgumentOutOfRangeException(nameof(groundingMode));
            // A disposition value unknown to this build is kept explicitly, never thrown.
            RawDisposition = (int)disposition;
            if (!Enum.IsDefined(typeof(RichContributorDisposition), disposition)) disposition = RichContributorDisposition.Unrecognized;
            if (supportPlaneToWorld == null) throw new ArgumentNullException(nameof(supportPlaneToWorld));
            if (supportPlaneKnown && !IsAffine(supportPlaneToWorld)) throw new ArgumentException("Known support matrix must be affine.", nameof(supportPlaneToWorld));
            // v1 exposes only a known flag, not the chart version value.  A true
            // flag would make a supposedly lossless export omit required data.
            if (chartVersionKnown) throw new ArgumentException("v1 cannot represent a known chart version without its value.", nameof(chartVersionKnown));
            if (bindingRevision.Known != bindingRevisionKnown)
                throw new ArgumentException("Binding revision knownness must agree with its wrapper.", nameof(bindingRevisionKnown));
            RichWire.ValidateUtf8(reason, nameof(reason)); RichWire.ValidateUtf8(supportIdentifier, nameof(supportIdentifier));
            ReferentId = referentId; SourceSpace = sourceSpace; SourceElement = sourceElement;
            ResolvedLocalElement = resolvedLocalElement; BindingRevision = bindingRevision; SnapshotOrdinal = snapshotOrdinal;
            AppliedWeight = appliedWeight; HasMappedSupport = hasMappedSupport;
            MappedSupport = mappedSupport; GroundingMode = groundingMode; Disposition = disposition; Reason = reason;
            ChartVersionKnown = chartVersionKnown; BindingRevisionKnown = bindingRevisionKnown;
            SupportIdentifier = supportIdentifier; SupportPlaneKnown = supportPlaneKnown; SupportPlaneToWorld = supportPlaneToWorld;
        }
        public RichKnownInt64 ReferentId { get; } public RichKnownInt64 SourceSpace { get; }
        public RichKnownInt64 SourceElement { get; } public RichKnownInt64 ResolvedLocalElement { get; }
        public RichKnownUInt64 BindingRevision { get; } public uint SnapshotOrdinal { get; } public double AppliedWeight { get; }
        public bool HasMappedSupport { get; } public RichPosition MappedSupport { get; }
        public RichGroundingMode GroundingMode { get; } public RichContributorDisposition Disposition { get; }
        /// <summary>The disposition value as received (differs from Disposition only when Unrecognized).</summary>
        public int RawDisposition { get; }
        /// <summary>Non-failure "no target yet": the gate is open, the source is not engaged.</summary>
        public bool IsAwaitingEngagement => Disposition == RichContributorDisposition.AwaitingEngagement;
        public string Reason { get; } public bool ChartVersionKnown { get; } public bool BindingRevisionKnown { get; }
        public string SupportIdentifier { get; } public bool SupportPlaneKnown { get; } public RichMatrix4x4 SupportPlaneToWorld { get; }
        private static bool IsAffine(RichMatrix4x4 matrix)
        {
            return Math.Abs(matrix.Values[12]) <= 1e-9 && Math.Abs(matrix.Values[13]) <= 1e-9 &&
                Math.Abs(matrix.Values[14]) <= 1e-9 && Math.Abs(matrix.Values[15] - 1) <= 1e-9;
        }
    }
    public sealed class RichRealizationChannel
    {
        private readonly ReadOnlyCollection<RichRealizationContributor> _contributors;
        public RichRealizationChannel(string anchor, RichRelationFamily family, RichSolverStatus status,
                                      RichGeometry geometry, RichGoalKind goalKind, bool hasGoal,
                                      RichKnownInt64 referentId, double arrivalSeconds, RichPosition admittedPosition, bool hasZ, double yawRadians, double weight,
                                      bool requestedPositionKnown, double requestedX, double requestedY,
                                      bool reachProjected, double reachDisplacementMetres,
                                      IEnumerable<RichRealizationContributor> contributors)
        {
            RichWire.ValidateUtf8(anchor, nameof(anchor));
            if (!Enum.IsDefined(typeof(RichRelationFamily), family) ||
                !Enum.IsDefined(typeof(RichSolverStatus), status) ||
                !Enum.IsDefined(typeof(RichGeometry), geometry) ||
                !Enum.IsDefined(typeof(RichGoalKind), goalKind))
                throw new ArgumentOutOfRangeException(nameof(family));
            if (string.IsNullOrEmpty(anchor) || !RichKnownDouble.Finite(arrivalSeconds) || arrivalSeconds < 0 || !RichKnownDouble.Finite(yawRadians) || !RichKnownDouble.Finite(weight) || weight < 0 ||
                !RichKnownDouble.Finite(requestedX) || !RichKnownDouble.Finite(requestedY) ||
                !RichKnownDouble.Finite(reachDisplacementMetres) || reachDisplacementMetres < 0)
                throw new ArgumentOutOfRangeException(nameof(anchor));
            if (reachProjected != (geometry == RichGeometry.ReachProjected))
                throw new ArgumentException("Reach-projected flag and geometry must agree.", nameof(reachProjected));
            if (contributors == null) throw new ArgumentNullException(nameof(contributors));
            var copy = new List<RichRealizationContributor>();
            foreach (RichRealizationContributor contributor in contributors)
            {
                if (contributor == null) throw new ArgumentException("Null contributor.", nameof(contributors));
                copy.Add(contributor);
            }
            Anchor = anchor; Family = family; Status = status; Geometry = geometry; GoalKind = goalKind;
            HasGoal = hasGoal; ReferentId = referentId; ArrivalSeconds = arrivalSeconds; AdmittedPosition = admittedPosition; HasZ = hasZ; YawRadians = yawRadians; Weight = weight;
            RequestedPositionKnown = requestedPositionKnown; RequestedX = requestedX; RequestedY = requestedY;
            ReachProjected = reachProjected; ReachDisplacementMetres = reachDisplacementMetres;
            _contributors = copy.AsReadOnly();
        }
        public string Anchor { get; } public RichRelationFamily Family { get; } public RichSolverStatus Status { get; }
        public RichGeometry Geometry { get; } public RichGoalKind GoalKind { get; } public bool HasGoal { get; }
        public RichKnownInt64 ReferentId { get; } public double ArrivalSeconds { get; } public RichPosition AdmittedPosition { get; } public bool HasZ { get; } public double YawRadians { get; } public double Weight { get; }
        public bool RequestedPositionKnown { get; } public double RequestedX { get; } public double RequestedY { get; }
        public bool ReachProjected { get; } public double ReachDisplacementMetres { get; }
        public IReadOnlyList<RichRealizationContributor> Contributors => _contributors;
    }
    public sealed class RichRealizationSnapshot
    {
        public const string Schema = "spatialblend.rich_realization";
        public const uint SchemaVersion = 1;
        public const uint FeatureVersion = 1;
        public const string CoordinateSystem = "spatialblend-z-up";
        public const string Units = "metres";
        private readonly ReadOnlyCollection<RichRealizationChannel> _channels;
        public RichRealizationSnapshot(RichRealizationHeader header, IEnumerable<RichRealizationChannel> channels)
        {
            Header = header ?? throw new ArgumentNullException(nameof(header));
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            var copy = new List<RichRealizationChannel>(); var keys = new HashSet<string>(StringComparer.Ordinal);
            var contributorOrdinals = new HashSet<uint>();
            int contributorCount = 0;
            foreach (RichRealizationChannel channel in channels)
            {
                if (channel == null || !keys.Add(channel.Anchor + "\n" + channel.Family)) throw new ArgumentException("Channels must be non-null and unique by anchor/family.", nameof(channels));
                if (copy.Count == RichWire.MaxChannels)
                    throw new ArgumentException("Snapshot channel limit exceeded.", nameof(channels));
                foreach (RichRealizationContributor contributor in channel.Contributors)
                {
                    if (contributorCount == RichWire.MaxContributors)
                        throw new ArgumentException("Snapshot contributor limit exceeded.", nameof(channels));
                    if (!contributorOrdinals.Add(contributor.SnapshotOrdinal))
                        throw new ArgumentException("Contributor ordinals must be unique across a snapshot.", nameof(channels));
                    ++contributorCount;
                }
                copy.Add(channel);
            }
            if (header.SnapshotStatus == RichSnapshotStatus.Empty && copy.Count != 0)
                throw new ArgumentException("Empty snapshots have no channels.", nameof(channels));
            _channels = copy.AsReadOnly();
        }
        public RichRealizationHeader Header { get; } public IReadOnlyList<RichRealizationChannel> Channels => _channels;
        public string ToDeterministicJson() => RichWire.Write(this);
    }
    internal static class RichWire
    {
        internal const int MaxChannels = 1024;
        internal const int MaxContributors = 8192;
        internal const int MaxUtf8Bytes = 1024 * 1024;
        internal const int MaxJsonUtf8Bytes = 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static void ValidateUtf8(string value, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            try
            {
                if (StrictUtf8.GetByteCount(value) > MaxUtf8Bytes)
                    throw new ArgumentException("UTF-8 field exceeds the rich wire limit.", name);
            }
            catch (EncoderFallbackException exception) { throw new ArgumentException("Invalid UTF-16/UTF-8 string.", name, exception); }
        }
        internal static string Write(RichRealizationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var b = new StringBuilder(); b.Append("{\"schema\":\"").Append(RichRealizationSnapshot.Schema).Append("\",\"schema_version\":1,\"feature_version\":1,\"coordinate_system\":\"").Append(RichRealizationSnapshot.CoordinateSystem).Append("\",\"units\":\"").Append(RichRealizationSnapshot.Units).Append("\",\"header\":{");
            RichRealizationHeader h = snapshot.Header;
            // Mailbox diagnostics may carry an intentionally unknown native
            // clock in memory so they fail closed locally.  Such a complete
            // diagnostic is not a valid wire realization: never serialize it
            // as though a calibrated native receipt existed.
            if (!h.NativeClockId.Known || !h.NativeClockEpoch.Known ||
                h.NativeClockId.Value == 0 || h.NativeClockEpoch.Value == 0)
                throw new InvalidOperationException("Rich wire snapshots require a known native clock identity.");
            if (h.SnapshotStatus == RichSnapshotStatus.Complete &&
                (!h.NativeReceiptNanoseconds.Known || !h.NativeSolveNanoseconds.Known ||
                 h.NativeClockId.Value == 0 || h.NativeClockEpoch.Value == 0))
                throw new InvalidOperationException("Complete rich snapshots require a known native clock and receipt interval on the wire.");
            Pair64(b,"receiver_space_id",h.ReceiverSpaceId); Comma(b); Pair64(b,"receiver_actor_id",h.ReceiverActorId); Comma(b); Pair64(b,"producer_lifetime",h.ProducerLifetime); Comma(b); Pair64(b,"revision",h.Revision); Comma(b); PairString(b,"snapshot_status",h.SnapshotStatus); Comma(b); PairString(b,"source_provenance_mode",h.SourceProvenanceMode); Comma(b);
            Known(b,"source_epoch",h.SourceEpoch); Comma(b); Known(b,"source_sequence",h.SourceSequence); Comma(b); Known(b,"source_observation_sequence",h.SourceObservationSequence); Comma(b); Known(b,"source_actor_space",h.SourceActorSpace); Comma(b); Known(b,"source_actor_element",h.SourceActorElement); Comma(b); Known(b,"relay_source_space",h.RelaySourceSpace); Comma(b); Known(b,"source_time_seconds",h.SourceTimeSeconds); Comma(b); Known(b,"source_clock_seconds",h.SourceClockSeconds); Comma(b); Known(b,"native_clock_id",h.NativeClockId); Comma(b); Known(b,"native_clock_epoch",h.NativeClockEpoch); Comma(b); Known(b,"native_receipt_nanoseconds",h.NativeReceiptNanoseconds); Comma(b); Known(b,"native_solve_nanoseconds",h.NativeSolveNanoseconds); Comma(b); Known(b,"policy_revision",h.PolicyRevision); Comma(b); Known(b,"receiver_tree_revision",h.ReceiverTreeRevision); Comma(b); Known(b,"counterpart_revision",h.CounterpartRevision); Comma(b); Known(b,"support_revision",h.SupportRevision); Comma(b); b.Append("\"known_revision_mask\":\"").Append(h.KnownRevisionMask.ToString(CultureInfo.InvariantCulture)).Append("\",\"unknown_revision_mask\":\"").Append(h.UnknownRevisionMask.ToString(CultureInfo.InvariantCulture)).Append("\",\"failure_code\":\"").Append(h.FailureCode.ToString(CultureInfo.InvariantCulture)).Append("\"},\"channels\":[");
            for (int i=0;i<snapshot.Channels.Count;i++) { if(i>0)Comma(b); Channel(b,snapshot.Channels[i]); }
            string json = b.Append("]}").ToString();
            if (StrictUtf8.GetByteCount(json) > MaxJsonUtf8Bytes)
                throw new InvalidOperationException("Rich JSON payload exceeds the wire limit.");
            return json;
        }
        private static void Channel(StringBuilder b, RichRealizationChannel c)
        { b.Append('{'); PairString(b,"anchor",c.Anchor); Comma(b); PairString(b,"family",c.Family); Comma(b); PairString(b,"status",c.Status); Comma(b); PairString(b,"geometry",c.Geometry); Comma(b); PairString(b,"goal_kind",c.GoalKind); Comma(b); PairBool(b,"has_goal",c.HasGoal); Comma(b); Known(b,"referent_id",c.ReferentId); b.Append(",\"arrival_seconds\":").Append(Number(c.ArrivalSeconds)).Append(",\"admitted_position\":"); Position(b,c.AdmittedPosition); b.Append(",\"has_z\":").Append(c.HasZ?"true":"false").Append(",\"yaw_radians\":").Append(Number(c.YawRadians)).Append(",\"weight\":").Append(Number(c.Weight)).Append(",\"requested_position\":{\"known\":").Append(c.RequestedPositionKnown?"true":"false"); if(c.RequestedPositionKnown)b.Append(",\"x\":").Append(Number(c.RequestedX)).Append(",\"y\":").Append(Number(c.RequestedY)); b.Append("},\"reach_projected\":").Append(c.ReachProjected?"true":"false").Append(",\"reach_displacement_metres\":").Append(Number(c.ReachDisplacementMetres)).Append(",\"contributors\":["); for(int i=0;i<c.Contributors.Count;i++){if(i>0)Comma(b); Contributor(b,c.Contributors[i]);} b.Append("]}"); }
        private static void Contributor(StringBuilder b, RichRealizationContributor c)
        { b.Append('{'); Known(b,"referent_id",c.ReferentId); Comma(b); Known(b,"source_space",c.SourceSpace); Comma(b); Known(b,"source_element",c.SourceElement); Comma(b); Known(b,"resolved_local_element",c.ResolvedLocalElement); Comma(b); Known(b,"binding_revision",c.BindingRevision); b.Append(",\"snapshot_ordinal\":\"").Append(c.SnapshotOrdinal.ToString(CultureInfo.InvariantCulture)).Append("\",\"applied_weight\":").Append(Number(c.AppliedWeight)).Append(",\"has_mapped_support\":").Append(c.HasMappedSupport?"true":"false").Append(",\"mapped_support\":"); Position(b,c.MappedSupport); b.Append(",\"grounding_mode\":"); String(b,Name(c.GroundingMode)); b.Append(",\"disposition\":"); String(b,RichContributorDispositionWire.ToWire(c.Disposition)); b.Append(",\"chart_version_known\":").Append(c.ChartVersionKnown?"true":"false").Append(",\"binding_revision_known\":").Append(c.BindingRevisionKnown?"true":"false").Append(",\"reason\":"); String(b,c.Reason); b.Append(",\"support_identifier\":"); String(b,c.SupportIdentifier); b.Append(",\"support_plane_to_world\":{\"known\":").Append(c.SupportPlaneKnown?"true":"false").Append(",\"row_major\":[");for(int i=0;i<16;i++){if(i>0)Comma(b);b.Append(Number(c.SupportPlaneToWorld.Values[i]));} b.Append("]}}"); }
        private static void Position(StringBuilder b, RichPosition p) { b.Append("{\"x\":").Append(Number(p.X)).Append(",\"y\":").Append(Number(p.Y)).Append(",\"z\":").Append(Number(p.Z)).Append('}'); }
        private static void Known(StringBuilder b,string n,RichKnownUInt64 v){b.Append('"').Append(n).Append("\":{\"known\":").Append(v.Known?"true":"false");if(v.Known)b.Append(",\"value\":\"").Append(v.Value.ToString(CultureInfo.InvariantCulture)).Append('"');b.Append('}');}
        private static void Known(StringBuilder b,string n,RichKnownInt64 v){b.Append('"').Append(n).Append("\":{\"known\":").Append(v.Known?"true":"false");if(v.Known)b.Append(",\"value\":\"").Append(v.Value.ToString(CultureInfo.InvariantCulture)).Append('"');b.Append('}');}
        private static void Known(StringBuilder b,string n,RichKnownDouble v){b.Append('"').Append(n).Append("\":{\"known\":").Append(v.Known?"true":"false");if(v.Known)b.Append(",\"value\":").Append(Number(v.Value));b.Append('}');}
        private static void Pair64(StringBuilder b,string n,long v){b.Append('"').Append(n).Append("\":\"").Append(v.ToString(CultureInfo.InvariantCulture)).Append('"');}
        private static void Pair64(StringBuilder b,string n,ulong v){b.Append('"').Append(n).Append("\":\"").Append(v.ToString(CultureInfo.InvariantCulture)).Append('"');}
        private static void PairString(StringBuilder b,string n,string v){b.Append('"').Append(n).Append("\":");String(b,v);}
        private static void PairString(StringBuilder b,string n,Enum v){b.Append('"').Append(n).Append("\":");String(b,Name(v));} private static void PairBool(StringBuilder b,string n,bool v){b.Append('"').Append(n).Append("\":").Append(v?"true":"false");}
        private static string Name(Enum value){var s=value.ToString();var b=new StringBuilder();for(int i=0;i<s.Length;i++){if(i>0&&char.IsUpper(s[i]))b.Append('_');b.Append(char.ToLowerInvariant(s[i]));}return b.ToString();}
        private static string Number(double value){if(!RichKnownDouble.Finite(value))throw new ArgumentOutOfRangeException(nameof(value));return value.ToString("R",CultureInfo.InvariantCulture);}
        private static void String(StringBuilder b,string v){ValidateUtf8(v,nameof(v));b.Append('"');foreach(char c in v){switch(c){case '"':b.Append("\\\"");break;case '\\':b.Append("\\\\");break;case '\b':b.Append("\\b");break;case '\f':b.Append("\\f");break;case '\n':b.Append("\\n");break;case '\r':b.Append("\\r");break;case '\t':b.Append("\\t");break;default:if(c<' ')b.Append("\\u").Append(((int)c).ToString("x4",CultureInfo.InvariantCulture));else b.Append(c);break;}}b.Append('"');}
        private static void Comma(StringBuilder b){b.Append(',');}
    }
}
