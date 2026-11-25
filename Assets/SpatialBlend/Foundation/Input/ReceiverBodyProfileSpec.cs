// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Immutable independently supplied body definition, validated before native
    /// receiver activation. It grants no input epoch, provider attestation or
    /// output authority. Materialization requires the exact live writer epoch.
    /// </summary>
    public sealed class ReceiverBodyProfileSpec
    {
        private ReceiverBodyProfileSpec(string root, string[] layout, ulong rig, ulong calibration,
            string provider, ulong providerEpoch, ReceiverBodyClockMapping clock,
            ReceiverBodyEvidenceKind evidence, string evidenceReference, ReceiverBodySkeleton skeleton,
            Matrix4x4 receiverFromBody, ReceiverBodyAnchorBinding[] anchors)
        {
            RootAnchor = root; AnchorNames = Array.AsReadOnly(layout);
            RigRevision = rig; CalibrationRevision = calibration; ProviderId = provider;
            ProviderEpoch = providerEpoch; ClockMapping = clock; EvidenceKind = evidence;
            EvidenceReference = evidenceReference; Skeleton = skeleton;
            ReceiverWorldFromBody = receiverFromBody; Anchors = Array.AsReadOnly(anchors);
        }
        public string RootAnchor { get; }
        public IReadOnlyList<string> AnchorNames { get; }
        public ulong RigRevision { get; }
        public ulong CalibrationRevision { get; }
        public string ProviderId { get; }
        public ulong ProviderEpoch { get; }
        public ReceiverBodyClockMapping ClockMapping { get; }
        public ReceiverBodyEvidenceKind EvidenceKind { get; }
        public string EvidenceReference { get; }
        public ReceiverBodySkeleton Skeleton { get; }
        public Matrix4x4 ReceiverWorldFromBody { get; }
        public IReadOnlyList<ReceiverBodyAnchorBinding> Anchors { get; }

        public static bool TryCreate(string root, IReadOnlyList<string> layout, ulong rigRevision,
            ulong calibrationRevision, string providerId, ulong providerEpoch, ReceiverBodyClockMapping clock,
            ReceiverBodyEvidenceKind evidence, string evidenceReference, ReceiverBodySkeleton skeleton,
            Matrix4x4 receiverWorldFromBody, IReadOnlyList<ReceiverBodyAnchorBinding> anchors,
            out ReceiverBodyProfileSpec spec, out string error)
        {
            spec = null; error = null;
            if (rigRevision == 0 || calibrationRevision == 0)
                return ReceiverInputValidation.Fail("A versioned bounded input definition is required before activation.", out error);
            if (!TryFreeze(layout, out string[] names, out error) ||
                !TryFreeze(anchors, out ReceiverBodyAnchorBinding[] bindings, out error)) return false;
            if (!ReceiverBodyProfile.TryCopyDefinition(root, names, providerId, providerEpoch, clock,
                evidence, evidenceReference, skeleton, receiverWorldFromBody, bindings, out var copy, out error)) return false;
            spec = new ReceiverBodyProfileSpec(root, names, rigRevision, calibrationRevision, providerId,
                providerEpoch, clock, evidence, evidenceReference, skeleton, receiverWorldFromBody, copy);
            return true;
        }

        public bool TryMaterialize(ReceiverInputEpoch input, out ReceiverBodyProfile profile, out string error)
        {
            profile = null; error = null;
            if (input == null || input.RootAnchor != RootAnchor || input.RigRevision != RigRevision ||
                input.CalibrationRevision != CalibrationRevision || input.AnchorNames.Count != AnchorNames.Count)
                return ReceiverInputValidation.Fail("The admitted input must match the complete preflighted definition.", out error);
            for (int i = 0; i < AnchorNames.Count; ++i)
                if (input.AnchorNames[i] != AnchorNames[i])
                    return ReceiverInputValidation.Fail("The admitted receiver layout order changed after preflight.", out error);
            return ReceiverBodyProfile.TryCreate(input, ProviderId, ProviderEpoch, ClockMapping,
                EvidenceKind, EvidenceReference, Skeleton, ReceiverWorldFromBody, Anchors, out profile, out error);
        }

        private static bool TryFreeze<T>(IReadOnlyList<T> source, out T[] copy, out string error)
        {
            copy = null; error = null;
            try
            {
                int count = source == null ? -1 : source.Count;
                if (count < 0 || count > ReceiverInputValidation.MaximumAnchors)
                    return ReceiverInputValidation.Fail("A bounded immutable input collection is required.", out error);
                var values = new T[count];
                for (int i = 0; i < count; ++i) values[i] = source[i];
                if (source.Count != count)
                    return ReceiverInputValidation.Fail("Body definition changed while copying.", out error);
                copy = values; return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IndexOutOfRangeException || exception is InvalidOperationException)
            { return ReceiverInputValidation.Fail("Body definition could not be frozen.", out error); }
        }
    }
}
