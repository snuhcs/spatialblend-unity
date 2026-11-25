// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend
{
    /// <summary>Domains that a native rich realization validity query may require.</summary>
    [Flags]
    public enum RichRealizationValidityDomain : uint
    {
        None = 0,
        Policy = 1u << 0,
        Tree = 1u << 1,
        Counterpart = 1u << 2,
        Support = 1u << 3,
        ReceiverInput = 1u << 4,
        All = Policy | Tree | Counterpart | Support | ReceiverInput
    }

    /// <summary>
    /// Immutable comparison between one frozen native rich revision and current
    /// receiver state. This is diagnostic only; it never admits body input or
    /// refreshes a native snapshot, receipt time, or poll cursor.
    /// </summary>
    public sealed class RichRealizationValidity
    {
        internal RichRealizationValidity(ulong producerLifetime, ulong revision,
            ulong capturedPolicyRevision, ulong capturedTreeRevision,
            ulong capturedCounterpartRevision, ulong capturedSupportRevision,
            ulong currentPolicyRevision, ulong currentTreeRevision,
            ulong currentCounterpartRevision, ulong currentSupportRevision,
            RichRealizationValidityDomain requestedDomains,
            RichRealizationValidityDomain capturedKnownDomains,
            RichRealizationValidityDomain currentKnownDomains,
            RichRealizationValidityDomain changedDomains,
            RichRealizationValidityDomain uncheckedDomains, bool valid)
        {
            if (producerLifetime == 0 || revision == 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (!IsDeclared(requestedDomains) || requestedDomains == RichRealizationValidityDomain.None ||
                !IsDeclared(capturedKnownDomains) || !IsDeclared(currentKnownDomains) ||
                !IsDeclared(changedDomains) || !IsDeclared(uncheckedDomains))
                throw new ArgumentOutOfRangeException(nameof(requestedDomains));
            if (valid && (((changedDomains | uncheckedDomains) & requestedDomains) != 0 ||
                (capturedKnownDomains & requestedDomains) != requestedDomains ||
                (currentKnownDomains & requestedDomains) != requestedDomains))
                throw new ArgumentException("A valid native comparison cannot contain changed, unchecked, or unknown requested domains.");

            ProducerLifetime = producerLifetime;
            Revision = revision;
            CapturedPolicyRevision = capturedPolicyRevision;
            CapturedTreeRevision = capturedTreeRevision;
            CapturedCounterpartRevision = capturedCounterpartRevision;
            CapturedSupportRevision = capturedSupportRevision;
            CurrentPolicyRevision = currentPolicyRevision;
            CurrentTreeRevision = currentTreeRevision;
            CurrentCounterpartRevision = currentCounterpartRevision;
            CurrentSupportRevision = currentSupportRevision;
            RequestedDomains = requestedDomains;
            CapturedKnownDomains = capturedKnownDomains;
            CurrentKnownDomains = currentKnownDomains;
            ChangedDomains = changedDomains;
            UncheckedDomains = uncheckedDomains;
            Valid = valid;
        }

        public ulong ProducerLifetime { get; }
        public ulong Revision { get; }
        public ulong CapturedPolicyRevision { get; }
        public ulong CapturedTreeRevision { get; }
        public ulong CapturedCounterpartRevision { get; }
        public ulong CapturedSupportRevision { get; }
        public ulong CurrentPolicyRevision { get; }
        public ulong CurrentTreeRevision { get; }
        public ulong CurrentCounterpartRevision { get; }
        public ulong CurrentSupportRevision { get; }
        public RichRealizationValidityDomain RequestedDomains { get; }
        public RichRealizationValidityDomain CapturedKnownDomains { get; }
        public RichRealizationValidityDomain CurrentKnownDomains { get; }
        public RichRealizationValidityDomain ChangedDomains { get; }
        public RichRealizationValidityDomain UncheckedDomains { get; }
        public bool Valid { get; }

        internal static bool IsDeclared(RichRealizationValidityDomain value)
        {
            return (value & ~RichRealizationValidityDomain.All) == 0;
        }
    }
}

namespace SpatialBlend.Internal
{
    // Separately negotiated Stage-A validity boundary. This partial class does
    // not alter ABI2 or rich-feature-v1 P/Invoke structs/callback behavior.
    internal static partial class SpatialBlendAPI
    {
        private const uint RichValidityFeatureVersion = 1;
        private const int RichValidityResultOk = 1;
        private const int RichValidityResultUnknownToken = -2;
        private const int RichValidityResultUnavailable = -5;
        private const int RichValidityResultStaleSnapshot = -6;

        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichValidityFeature
        {
            public uint descriptor_size, feature_version, validity_size, supported_domain_mask;
            public uint validity_producer_lifetime_offset, validity_revision_offset;
            public uint validity_captured_known_mask_offset, validity_current_known_mask_offset;
            public uint validity_changed_mask_offset, validity_unchecked_mask_offset;
            public uint validity_valid_offset, reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SbRichValidity
        {
            public ulong producer_lifetime, revision;
            public ulong captured_policy_revision, captured_tree_revision, captured_counterpart_revision, captured_support_revision;
            public ulong current_policy_revision, current_tree_revision, current_counterpart_revision, current_support_revision;
            public uint requested_domain_mask, captured_known_mask, current_known_mask, changed_mask, unchecked_mask, valid;
        }

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_feature")]
        private static extern uint GetRichValidityFeatureRelease(out SbRichValidityFeature value, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_feature")]
        private static extern uint GetRichValidityFeatureDebug(out SbRichValidityFeature value, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_field_count")]
        private static extern uint GetRichValidityFieldCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_field_count")]
        private static extern uint GetRichValidityFieldCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_field_offset")]
        private static extern uint GetRichValidityFieldOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity_field_offset")]
        private static extern uint GetRichValidityFieldOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity")]
        private static extern int GetRichValidityRelease(ulong token, ulong revision, uint requestedDomains,
            out SbRichValidity validity, uint validitySize);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_rich_realization_validity")]
        private static extern int GetRichValidityDebug(ulong token, ulong revision, uint requestedDomains,
            out SbRichValidity validity, uint validitySize);
        // Existing C policy configuration is declared here only for the
        // isolated native validity fixture; no world/actor adapter consumes it
        // or treats this comparison as body admission.
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_set_realization_policy")]
        private static extern void SetRichValidityPolicyRelease(long space, uint policy);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_set_realization_policy")]
        private static extern void SetRichValidityPolicyDebug(long space, uint policy);

        internal static bool TryGetRichRealizationValidity(ulong lifetime, ulong revision,
            RichRealizationValidityDomain requestedDomains, out RichRealizationValidity validity,
            out int nativeResult, out string error)
        {
            validity = null;
            nativeResult = -1;
            error = null;
            if (lifetime == 0 || revision == 0)
            {
                error = "Rich lifetime token and revision are required.";
                return false;
            }
            if (requestedDomains == RichRealizationValidityDomain.None ||
                !RichRealizationValidity.IsDeclared(requestedDomains))
            {
                error = "Rich validity requires a nonzero mask containing only declared domains.";
                return false;
            }
            if (!TryGetRichValidityFeature(out _, out error)) return false;
            try
            {
                SbRichValidity native;
                nativeResult = useDebugLibrary
                    ? GetRichValidityDebug(lifetime, revision, (uint)requestedDomains, out native,
                        (uint)Marshal.SizeOf(typeof(SbRichValidity)))
                    : GetRichValidityRelease(lifetime, revision, (uint)requestedDomains, out native,
                        (uint)Marshal.SizeOf(typeof(SbRichValidity)));
                if (nativeResult == RichValidityResultOk || nativeResult == RichValidityResultUnavailable)
                {
                    // A retired token is unavailable without a comparison
                    // payload. Unknown requested domains instead return a
                    // populated diagnostic comparison with the same code.
                    if (nativeResult == RichValidityResultUnavailable &&
                        native.producer_lifetime == 0 && native.revision == 0 &&
                        native.requested_domain_mask == 0)
                        return true;
                    if (native.producer_lifetime != lifetime || native.revision != revision ||
                        native.requested_domain_mask != (uint)requestedDomains)
                    {
                        error = "Native rich validity comparison identity does not match the query.";
                        return false;
                    }
                    validity = CopyRichValidity(native);
                    return true;
                }
                // These are native lifecycle/fence outcomes, not load errors.
                if (nativeResult == RichValidityResultUnknownToken ||
                    nativeResult == RichValidityResultStaleSnapshot) return true;
                error = "Native rich validity query failed with result " + nativeResult + ".";
                return false;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                error = "Rich validity feature unavailable: " + e.Message;
                return false;
            }
            catch (ArgumentException e)
            {
                validity = null;
                error = "Malformed native rich validity comparison: " + e.Message;
                return false;
            }
        }

        internal static bool TrySetRichValidityPolicyForTest(long space, uint policy, out string error)
        {
            error = null;
            if (space == 0 || policy > 2) { error = "A valid space and policy are required."; return false; }
            try
            {
                if (useDebugLibrary) SetRichValidityPolicyDebug(space, policy);
                else SetRichValidityPolicyRelease(space, policy);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                error = "Rich validity policy fixture hook unavailable: " + e.Message;
                return false;
            }
        }

        private static bool TryGetRichValidityFeature(out SbRichValidityFeature feature, out string error)
        {
            feature = default(SbRichValidityFeature);
            error = null;
            try
            {
                uint expected = (uint)Marshal.SizeOf(typeof(SbRichValidityFeature));
                uint got = useDebugLibrary
                    ? GetRichValidityFeatureDebug(out feature, expected)
                    : GetRichValidityFeatureRelease(out feature, expected);
                if (got != expected || feature.descriptor_size != expected ||
                    feature.feature_version != RichValidityFeatureVersion ||
                    feature.validity_size != (uint)Marshal.SizeOf(typeof(SbRichValidity)) ||
                    feature.supported_domain_mask != (uint)RichRealizationValidityDomain.All)
                {
                    error = "Native rich validity feature descriptor is incompatible.";
                    return false;
                }
                return ValidateRichValidityLayouts(feature, out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                // An older rich library has no validity entry points. This is a
                // declared unsupported feature, never a false successful query.
                error = "Rich validity feature unavailable: " + e.Message;
                return false;
            }
        }

        private static bool ValidateRichValidityLayouts(SbRichValidityFeature feature, out string error)
        {
            error = null;
            Type[] types = { typeof(SbRichValidityFeature), typeof(SbRichValidity) };
            int[] counts = { 12, 16 };
            string[][] names = RichValidityFieldNames;
            for (int kind = 0; kind < types.Length; ++kind)
            {
                uint nativeCount = useDebugLibrary
                    ? GetRichValidityFieldCountDebug((uint)(kind + 1))
                    : GetRichValidityFieldCountRelease((uint)(kind + 1));
                if (nativeCount != counts[kind])
                {
                    error = "Native rich validity field count mismatch for kind " + (kind + 1) + ".";
                    return false;
                }
                for (uint index = 0; index < nativeCount; ++index)
                {
                    uint nativeOffset = useDebugLibrary
                        ? GetRichValidityFieldOffsetDebug((uint)(kind + 1), index)
                        : GetRichValidityFieldOffsetRelease((uint)(kind + 1), index);
                    uint managedOffset = (uint)Marshal.OffsetOf(types[kind], names[kind][index]).ToInt64();
                    if (nativeOffset != managedOffset)
                    {
                        error = "Native rich validity offset mismatch for kind " + (kind + 1) + ", field " + index + ".";
                        return false;
                    }
                }
            }
            if (feature.validity_producer_lifetime_offset != RichValidityOffset("producer_lifetime") ||
                feature.validity_revision_offset != RichValidityOffset("revision") ||
                feature.validity_captured_known_mask_offset != RichValidityOffset("captured_known_mask") ||
                feature.validity_current_known_mask_offset != RichValidityOffset("current_known_mask") ||
                feature.validity_changed_mask_offset != RichValidityOffset("changed_mask") ||
                feature.validity_unchecked_mask_offset != RichValidityOffset("unchecked_mask") ||
                feature.validity_valid_offset != RichValidityOffset("valid"))
            {
                error = "Native rich validity descriptor offsets are incompatible.";
                return false;
            }
            return true;
        }

        private static RichRealizationValidity CopyRichValidity(SbRichValidity native)
        {
            if (native.valid > 1 || native.producer_lifetime == 0 || native.revision == 0)
                throw new ArgumentException("Malformed native rich validity result.");
            return new RichRealizationValidity(native.producer_lifetime, native.revision,
                native.captured_policy_revision, native.captured_tree_revision,
                native.captured_counterpart_revision, native.captured_support_revision,
                native.current_policy_revision, native.current_tree_revision,
                native.current_counterpart_revision, native.current_support_revision,
                (RichRealizationValidityDomain)native.requested_domain_mask,
                (RichRealizationValidityDomain)native.captured_known_mask,
                (RichRealizationValidityDomain)native.current_known_mask,
                (RichRealizationValidityDomain)native.changed_mask,
                (RichRealizationValidityDomain)native.unchecked_mask, native.valid != 0);
        }

        private static uint RichValidityOffset(string name)
        {
            return (uint)Marshal.OffsetOf(typeof(SbRichValidity), name).ToInt64();
        }

        private static readonly string[][] RichValidityFieldNames =
        {
            new[] { "descriptor_size", "feature_version", "validity_size", "supported_domain_mask",
                "validity_producer_lifetime_offset", "validity_revision_offset",
                "validity_captured_known_mask_offset", "validity_current_known_mask_offset",
                "validity_changed_mask_offset", "validity_unchecked_mask_offset", "validity_valid_offset", "reserved" },
            new[] { "producer_lifetime", "revision", "captured_policy_revision", "captured_tree_revision",
                "captured_counterpart_revision", "captured_support_revision", "current_policy_revision",
                "current_tree_revision", "current_counterpart_revision", "current_support_revision",
                "requested_domain_mask", "captured_known_mask", "current_known_mask", "changed_mask",
                "unchecked_mask", "valid" }
        };
    }
}
