// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    [Serializable]
    public sealed class AnchorPoseSample
    {
        public string anchorId;
        public Vector3 worldPosition;
        public Quaternion worldRotation = Quaternion.identity;
    }

    /// <summary>Portable recorded or synthetic tracked-anchor input in Unity world space.</summary>
    [Serializable]
    public sealed class AnchorPoseFrame
    {
        public const int CurrentSchema = 1;
        public const string UnityWorldFrame = "unity-world";

        public int schema = CurrentSchema;
        public string frame = UnityWorldFrame;
        public long sequence;
        public double timeSeconds;
        public AnchorPoseSample[] anchors = Array.Empty<AnchorPoseSample>();
    }

    /// <summary>
    /// Full-snapshot boundary: implementations receive every currently observed
    /// anchor. Anchors omitted by a newer accepted frame are no longer observed.
    /// </summary>
    public interface IAnchorPoseFrameSink
    {
        bool TryApplyWorldFrame(AnchorPoseFrame frame);
        void ClearWorldAnchor(string anchorId);
    }

    internal static class AnchorPoseFrameValidation
    {
        internal static bool TryValidateFrame(AnchorPoseFrame frame, long lastSequence,
                                              double lastTime, ISet<string> known,
                                              out string error)
        {
            error = null;
            if (frame == null || frame.schema != AnchorPoseFrame.CurrentSchema ||
                !string.Equals(frame.frame, AnchorPoseFrame.UnityWorldFrame,
                    StringComparison.Ordinal) || frame.sequence < 0 ||
                frame.sequence <= lastSequence || !IsFinite(frame.timeSeconds) ||
                frame.timeSeconds < 0d || frame.timeSeconds < lastTime ||
                frame.anchors == null || known == null || known.Count == 0)
            {
                error = "Frame requires schema=1, unity-world coordinates, monotonic sequence/time, anchors, and known IDs.";
                return false;
            }
            var frameIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (AnchorPoseSample sample in frame.anchors)
            {
                float rotationMagnitudeSquared = sample == null ? 0f :
                    sample.worldRotation.x * sample.worldRotation.x +
                    sample.worldRotation.y * sample.worldRotation.y +
                    sample.worldRotation.z * sample.worldRotation.z +
                    sample.worldRotation.w * sample.worldRotation.w;
                if (sample == null || !IsValidAnchorId(sample.anchorId) ||
                    !known.Contains(sample.anchorId) || !frameIds.Add(sample.anchorId) ||
                    !IsFinite(sample.worldPosition) ||
                    !IsFinite(sample.worldRotation) ||
                    !IsFinite(rotationMagnitudeSquared) ||
                    Math.Abs(rotationMagnitudeSquared - 1f) > 0.001f)
                {
                    error = "Anchor samples require unique known IDs, finite world poses, and unit quaternions.";
                    return false;
                }
            }
            return true;
        }

        internal static AnchorPoseFrame DeepCopy(AnchorPoseFrame frame)
        {
            var copiedAnchors = new AnchorPoseSample[frame.anchors.Length];
            for (int i = 0; i < copiedAnchors.Length; ++i)
            {
                AnchorPoseSample source = frame.anchors[i];
                copiedAnchors[i] = new AnchorPoseSample
                {
                    anchorId = source.anchorId,
                    worldPosition = source.worldPosition,
                    worldRotation = source.worldRotation
                };
            }
            return new AnchorPoseFrame
            {
                schema = frame.schema,
                frame = frame.frame,
                sequence = frame.sequence,
                timeSeconds = frame.timeSeconds,
                anchors = copiedAnchors
            };
        }

        internal static bool IsValidAnchorId(string id) =>
            !string.IsNullOrWhiteSpace(id) && id.IndexOf('\0') < 0;

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        internal static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }
}
