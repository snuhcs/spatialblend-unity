// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>A receiver-world Unity Y-up rigid anchor pose; unavailable is explicit.</summary>
    public readonly struct ReceiverAnchorPose
    {
        public ReceiverAnchorPose(string name, bool available, Matrix4x4 worldPose)
        { Name = name; Available = available; WorldPose = worldPose; }
        public string Name { get; }
        public bool Available { get; }
        public Matrix4x4 WorldPose { get; }
    }

    /// <summary>
    /// Opaque, immutable identity from an admitted native receiver epoch. Rig and
    /// calibration revisions are caller identities, not verified measurements.
    /// Reference identity also fences native epoch-number reuse after rebind.
    /// </summary>
    public sealed class ReceiverInputEpoch
    {
        internal ReceiverInputEpoch(long space, long actor, string root, string[] names,
            ulong epoch, ulong rig, ulong calibration, ulong clockId, ulong clockEpoch,
            long maximumAgeNanoseconds)
        {
            NativeSpace = space; NativeActor = actor; RootAnchor = root;
            AnchorNames = Array.AsReadOnly((string[])names.Clone());
            Epoch = epoch; RigRevision = rig; CalibrationRevision = calibration;
            ClockId = clockId; ClockEpoch = clockEpoch;
            MaximumAgeNanoseconds = maximumAgeNanoseconds;
        }
        internal long NativeSpace { get; }
        internal long NativeActor { get; }
        public string RootAnchor { get; }
        public IReadOnlyList<string> AnchorNames { get; }
        public ulong Epoch { get; }
        public ulong RigRevision { get; }
        public ulong CalibrationRevision { get; }
        public ulong ClockId { get; }
        public ulong ClockEpoch { get; }
        public long MaximumAgeNanoseconds { get; }
    }

    /// <summary>
    /// Complete, deep-owned geometric input, not a source replay or complete body.
    /// Acquisition and expiry are already in Epoch's native monotonic domain.
    /// Creating/submitting a frame never replaces acquisition time with now.
    /// </summary>
    public sealed class ReceiverInputFrame
    {
        private ReceiverInputFrame(ReceiverInputEpoch input, ulong sequence, long observed,
            long expiry, ReceiverAnchorPose[] anchors)
        {
            Input = input; Sequence = sequence; ObservedAtNanoseconds = observed;
            ValidUntilNanoseconds = expiry; Anchors = Array.AsReadOnly(anchors);
        }
        public ReceiverInputEpoch Input { get; }
        public ulong Sequence { get; }
        public long ObservedAtNanoseconds { get; }
        public long ValidUntilNanoseconds { get; }
        public IReadOnlyList<ReceiverAnchorPose> Anchors { get; }

        public static bool TryCreate(ReceiverInputEpoch input, ulong sequence,
            long observedAtNanoseconds, long validUntilNanoseconds,
            IReadOnlyList<ReceiverAnchorPose> anchors, out ReceiverInputFrame frame,
            out string error)
        {
            frame = null; error = null;
            if (input == null || sequence == 0 || observedAtNanoseconds <= 0 ||
                validUntilNanoseconds <= observedAtNanoseconds ||
                validUntilNanoseconds - observedAtNanoseconds > input.MaximumAgeNanoseconds)
                return ReceiverInputValidation.Fail("Receiver epoch, positive sequence and bounded native observation/expiry times are required.", out error);
            if (anchors == null || anchors.Count == 0 ||
                anchors.Count > ReceiverInputValidation.MaximumAnchors ||
                anchors.Count != input.AnchorNames.Count)
                return ReceiverInputValidation.Fail("A complete bounded receiver frame is required, including unavailable anchors.", out error);
            var names = new HashSet<string>(input.AnchorNames, StringComparer.Ordinal);
            var copy = new ReceiverAnchorPose[anchors.Count];
            for (int i = 0; i < copy.Length; ++i)
            {
                ReceiverAnchorPose row = anchors[i];
                if (!ReceiverInputValidation.ValidName(row.Name) || !names.Remove(row.Name) ||
                    !ReceiverInputValidation.TryNativePose(row.WorldPose, out double[] pose))
                    return ReceiverInputValidation.Fail("Receiver rows require unique configured names and finite proper rigid world poses, even when unavailable.", out error);
                if (row.Name == input.RootAnchor && row.Available &&
                    pose[0] * pose[0] + pose[4] * pose[4] < 1e-12)
                    return ReceiverInputValidation.Fail("The available receiver root must have a defined planar heading.", out error);
                copy[i] = row;
            }
            frame = new ReceiverInputFrame(input, sequence, observedAtNanoseconds,
                validUntilNanoseconds, copy);
            return true;
        }
    }

    public readonly struct ReceiverInputStamp
    {
        internal ReceiverInputStamp(ulong epoch, ulong revision, ulong sequence, ulong rig,
            ulong calibration, long observed, long expiry, bool known)
        {
            Epoch = epoch; Revision = revision; Sequence = sequence; RigRevision = rig;
            CalibrationRevision = calibration; ObservedAtNanoseconds = observed;
            ValidUntilNanoseconds = expiry; Known = known;
        }
        public ulong Epoch { get; }
        public ulong Revision { get; }
        public ulong Sequence { get; }
        public ulong RigRevision { get; }
        public ulong CalibrationRevision { get; }
        public long ObservedAtNanoseconds { get; }
        public long ValidUntilNanoseconds { get; }
        public bool Known { get; }
    }

    /// <summary>Diagnostic as-of-query comparison, never a body or multi-domain actuation lease.</summary>
    public sealed class ReceiverInputValidity
    {
        internal ReceiverInputValidity(ulong lifetime, ulong revision, ReceiverInputStamp captured,
            ReceiverInputStamp current, bool changed, bool valid)
        {
            ProducerLifetime = lifetime; Revision = revision; Captured = captured;
            Current = current; Changed = changed; Valid = valid;
        }
        public ulong ProducerLifetime { get; }
        public ulong Revision { get; }
        public ReceiverInputStamp Captured { get; }
        public ReceiverInputStamp Current { get; }
        public bool Changed { get; }
        public bool Valid { get; }
    }

    internal static class ReceiverInputValidation
    {
        internal const int MaximumAnchors = 64;
        internal const int MaximumNameBytes = 128;
        internal const long MaximumAgeNanoseconds = 1000000000L;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaximumNameBytes ||
                name.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(name) <= MaximumNameBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool TryNativePose(Matrix4x4 world, out double[] pose)
        {
            pose = null;
            // Anchor +Z-forward -> native +X-forward, distinct from geometry's
            // basis conjugation. No scale removal, normalization or yaw-only loss.
            Matrix4x4 native = CoordinateConverter.UnityToSpatialBlendAnchorMatrix(world);
            var values = new double[16];
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                {
                    double value = native[row, column];
                    if (double.IsNaN(value) || double.IsInfinity(value)) return false;
                    values[row * 4 + column] = value;
                }
            double affine = values[12] * values[12] + values[13] * values[13] +
                values[14] * values[14] + (values[15] - 1) * (values[15] - 1);
            if (affine > 1e-18) return false;
            double gramError = 0;
            for (int a = 0; a < 3; ++a)
                for (int b = 0; b < 3; ++b)
                {
                    double dot = 0;
                    for (int r = 0; r < 3; ++r) dot += values[r * 4 + a] * values[r * 4 + b];
                    double error = dot - (a == b ? 1 : 0);
                    gramError += error * error;
                }
            double det = values[0] * (values[5] * values[10] - values[6] * values[9]) -
                values[1] * (values[4] * values[10] - values[6] * values[8]) +
                values[2] * (values[4] * values[9] - values[5] * values[8]);
            if (gramError > 1e-12 || Math.Abs(det - 1) > 1e-6) return false;
            pose = values;
            return true;
        }

        internal static bool Fail(string message, out string error)
        { error = message; return false; }
    }
}
