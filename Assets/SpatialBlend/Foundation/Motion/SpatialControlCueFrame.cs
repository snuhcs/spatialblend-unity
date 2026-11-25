// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Post-realizer, receiver-local spatial guidance. This is intentionally not
    /// an observation frame, skeleton pose, authority protocol, or model driver.
    /// </summary>
    [Serializable]
    public sealed class SpatialControlCueFrame
    {
        public const int CurrentSchema = 1;
        public const string UnityWorldFrame = "unity-world";
        public const string ReceiverUnscaledClock = "unity-unscaled-time";

        public int schema = CurrentSchema;
        public string frame = UnityWorldFrame;
        public string actorId;
        public string spaceId;
        public string streamId;
        public string clockId = ReceiverUnscaledClock;
        public long revision;
        public double issuedAtSeconds;
        public SpatialControlCue[] cues = Array.Empty<SpatialControlCue>();

        /// <summary>Returns a defensive snapshot suitable for another observer.</summary>
        public SpatialControlCueFrame DeepCopy()
        {
            var copied = new SpatialControlCue[cues == null ? 0 : cues.Length];
            for (int index = 0; index < copied.Length; ++index)
                copied[index] = cues[index] == null ? null : cues[index].DeepCopy();
            return new SpatialControlCueFrame
            {
                schema = schema, frame = frame, actorId = actorId, spaceId = spaceId,
                streamId = streamId, clockId = clockId, revision = revision,
                issuedAtSeconds = issuedAtSeconds, cues = copied
            };
        }

        internal static bool TryCreate(string actorId, string spaceId, string streamId,
                                       long revision, double issuedAtSeconds,
                                       IReadOnlyList<RealizationTarget> targets,
                                       out SpatialControlCueFrame frame)
        {
            frame = null;
            if (!ValidId(actorId) || !ValidId(spaceId) || !ValidId(streamId) || revision < 0 ||
                !Finite(issuedAtSeconds) || issuedAtSeconds < 0 || targets == null)
                return false;
            var cues = new SpatialControlCue[targets.Count];
            var pipelines = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < targets.Count; ++index)
            {
                if (!SpatialControlCue.TryCreate(targets[index], out SpatialControlCue cue))
                    return false;
                string pipeline = cue.anchorId + "\n" + cue.family + "\n" + cue.referentToken;
                if (!pipelines.Add(pipeline)) return false;
                cues[index] = cue;
            }
            frame = new SpatialControlCueFrame
            {
                actorId = actorId,
                spaceId = spaceId,
                streamId = streamId,
                revision = revision,
                issuedAtSeconds = issuedAtSeconds,
                cues = cues
            };
            return true;
        }

        private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.IndexOf('\0') < 0;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    [Serializable]
    public sealed class SpatialControlCue
    {
        public string anchorId;
        // Exact v1 wire vocabulary: spacing, facing, affordance_pose.
        public string family;
        // A string preserves the native signed 64-bit token in JSON consumers.
        public string referentToken;
        public SpatialControlCuePosition position;
        public bool hasHeight;
        public float yawDegrees;
        public double policyWeight;
        public double arrivalBudgetSeconds;

        internal SpatialControlCue DeepCopy() => new SpatialControlCue
        {
            anchorId = anchorId, family = family, referentToken = referentToken,
            position = position == null ? null : position.DeepCopy(), hasHeight = hasHeight,
            yawDegrees = yawDegrees, policyWeight = policyWeight,
            arrivalBudgetSeconds = arrivalBudgetSeconds
        };

        internal static bool TryCreate(RealizationTarget target, out SpatialControlCue cue)
        {
            cue = null;
            if (!TryFamily(target.KernelType, out string family) ||
                !ValidId(target.AnchorName) || !SpatialControlCueFrame.Finite(target.Position) ||
                !SpatialControlCueFrame.Finite(target.Rotation.eulerAngles.y) ||
                !SpatialControlCueFrame.Finite(target.Weight) || target.Weight < 0d ||
                target.Weight > 1d || !SpatialControlCueFrame.Finite(target.ArrivalTime) ||
                target.ArrivalTime < 0d)
                return false;
            cue = new SpatialControlCue
            {
                anchorId = target.AnchorName,
                family = family,
                referentToken = target.ReferentId.ToString(CultureInfo.InvariantCulture),
                position = new SpatialControlCuePosition(target.Position),
                hasHeight = target.HasHeight,
                yawDegrees = target.Rotation.eulerAngles.y,
                policyWeight = target.Weight,
                arrivalBudgetSeconds = target.ArrivalTime
            };
            return true;
        }

        private static bool TryFamily(SbKernelType kernel, out string family)
        {
            switch (kernel)
            {
            case SbKernelType.Spacing: family = "spacing"; return true;
            case SbKernelType.Facing: family = "facing"; return true;
            case SbKernelType.AffordancePose: family = "affordance_pose"; return true;
            default: family = null; return false;
            }
        }
        private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.IndexOf('\0') < 0;
    }

    [Serializable]
    public sealed class SpatialControlCuePosition
    {
        public float x;
        public float y;
        public float z;
        public SpatialControlCuePosition() { }
        internal SpatialControlCuePosition(Vector3 value) { x = value.x; y = value.y; z = value.z; }
        internal SpatialControlCuePosition DeepCopy() => new SpatialControlCuePosition
        { x = x, y = y, z = z };
    }
}
