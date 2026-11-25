// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Immutable, fully prevalidated committed AnchorPoseFrame
    /// recording.</summary>
    public sealed class AnchorPoseRecording
    {
        private const string OrientationMode =
            "s6-canonical-spatial-anchor-yaw-only: Unity yaw = pi/2 - S6 yaw; raw " +
            "tracker quaternions retained as evidence, not relabeled as Unity " + "anchor rotation";

        private readonly List<AnchorPoseFrame> _frames = new List<AnchorPoseFrame>();
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);

        // Internal seam permits a deterministic TOCTOU regression without making file
        // I/O public API.
        internal static Func<string, byte[]> ReadBytesForTesting;

        public int FrameCount => _frames.Count;
        public IEnumerable<string> KnownAnchorIds => new List<string>(_known);

        public bool TryLoadCommitted(string jsonlPath, string manifestPath,
                                     IEnumerable<string> knownAnchorIds, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(manifestPath))
            {
                error = "A committed manifest is required.";
                return false;
            }

            byte[] jsonlBytes;
            byte[] manifestBytes;
            try
            {
                // The JSONL is read once. All hash, UTF-8, and frame checks below
                // concern exactly these immutable bytes, never a later re-read.
                manifestBytes = ReadBytes(manifestPath);
            }
            catch (Exception exception)
                when (exception is IOException || exception is UnauthorizedAccessException)
            {
                error = "Could not read committed export: " + exception.Message;
                return false;
            }

            if (!TryDecodeUtf8(manifestBytes, "manifest", out string manifestText, out error) ||
                !TryResolveJsonl(manifestText, jsonlPath, manifestPath, out string resolvedJsonl,
                                 out bool hasLocator, out error))
                return false;
            try { jsonlBytes = ReadBytes(resolvedJsonl); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            { error = "Could not read committed JSONL: " + exception.Message; return false; }
            if (!TryValidateManifest(manifestText, resolvedJsonl, hasLocator, jsonlBytes, out error))
                return false;

            var known = new HashSet<string>(StringComparer.Ordinal);
            if (knownAnchorIds == null)
            {
                error = "Known anchor IDs are required.";
                return false;
            }
            foreach (string id in knownAnchorIds)
            {
                if (!AnchorPoseFrameValidation.IsValidAnchorId(id) || !known.Add(id))
                {
                    error = "Known anchor IDs must be unique and valid.";
                    return false;
                }
            }

            if (!TryDecodeUtf8(jsonlBytes, "JSONL", out string jsonlText, out error))
                return false;
            var candidate = new List<AnchorPoseFrame>();
            long lastSequence = -1;
            double lastTime = double.NegativeInfinity;
            string[] lines = jsonlText.Split('\n');
            int lineCount = lines.Length > 0 && lines[lines.Length - 1].Length == 0
                                ? lines.Length - 1
                                : lines.Length;
            for (int i = 0; i < lineCount; ++i)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) ||
                    !AnchorPoseJson.TryDecode(line, out AnchorPoseFrame frame, out error) ||
                    !AnchorPoseFrameValidation.TryValidateFrame(frame, lastSequence, lastTime,
                                                                known, out error))
                    return false;
                candidate.Add(AnchorPoseFrameValidation.DeepCopy(frame));
                lastSequence = frame.sequence;
                lastTime = frame.timeSeconds;
            }
            if (candidate.Count == 0)
            {
                error = "Committed JSONL contains no frames.";
                return false;
            }

            // No public or private state changes until every input check succeeded.
            _frames.Clear();
            _frames.AddRange(candidate);
            _known.Clear();
            _known.UnionWith(known);
            return true;
        }

        public bool TryGetFrame(int index, out AnchorPoseFrame frame)
        {
            if (index < 0 || index >= _frames.Count)
            {
                frame = null;
                return false;
            }
            frame = AnchorPoseFrameValidation.DeepCopy(_frames[index]);
            return true;
        }

        internal AnchorPoseRecording Freeze()
        {
            var frozen = new AnchorPoseRecording();
            foreach (AnchorPoseFrame frame in _frames)
                frozen._frames.Add(AnchorPoseFrameValidation.DeepCopy(frame));
            frozen._known.UnionWith(_known);
            return frozen;
        }

        private static byte[] ReadBytes(string path)
        {
            return ReadBytesForTesting != null ? ReadBytesForTesting(path)
                                               : File.ReadAllBytes(path);
        }

        private static bool TryDecodeUtf8(byte[] bytes, string label, out string text,
                                          out string error)
        {
            text = null;
            error = null;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                error = label + " is not strict UTF-8.";
                return false;
            }
        }

        private static bool TryValidateManifest(string manifestText, string jsonlPath,
                                                bool hasLocator, byte[] jsonlBytes, out string error)
        {
            error = null;
            if (!AnchorPoseJson.TryParse(manifestText, out AnchorPoseJson.JsonValue root,
                                         out error) ||
                root.Object == null ||
                !AnchorPoseJson.Required(root, "output", out AnchorPoseJson.JsonValue output) ||
                output.Object == null || !Text(output, "publication_state", out string state) ||
                state != "committed" ||
                (!hasLocator &&
                 (!Text(output, "jsonl_path", out string recordedPath) || recordedPath != Path.GetFullPath(jsonlPath))) ||
                !AnchorPoseJson.Required(output, "jsonl_byte_count",
                                         out AnchorPoseJson.JsonValue bytes) ||
                !AnchorPoseJson.Int64(bytes, out long size) || size != jsonlBytes.LongLength ||
                !Text(output, "jsonl_sha256", out string hash) ||
                !string.Equals(hash, Sha256(jsonlBytes), StringComparison.OrdinalIgnoreCase) ||
                !AnchorPoseJson.Required(output, "anchor_pose_schema",
                                         out AnchorPoseJson.JsonValue schema) ||
                !AnchorPoseJson.Int32(schema, out int version) ||
                version != AnchorPoseFrame.CurrentSchema ||
                !Text(output, "frame", out string frame) ||
                frame != AnchorPoseFrame.UnityWorldFrame ||
                !AnchorPoseJson.Required(root, "provenance",
                                         out AnchorPoseJson.JsonValue provenance) ||
                !Text(provenance, "orientation_mode", out string mode) || mode != OrientationMode)
            {
                error = "Manifest is not a matching committed S6 Unity-anchor export.";
                return false;
            }
            return true;
        }

        private static bool TryResolveJsonl(string manifestText, string legacyPath,
                                            string manifestPath, out string path,
                                            out bool hasLocator, out string error)
        {
            path = null;
            hasLocator = false;
            error = null;
            if (!AnchorPoseJson.TryParse(manifestText, out AnchorPoseJson.JsonValue root, out error) ||
                !AnchorPoseJson.Required(root, "output", out AnchorPoseJson.JsonValue output) ||
                output.Object == null)
            {
                error = "Manifest output is malformed.";
                return false;
            }
            if (!output.Object.ContainsKey("jsonl_locator"))
            {
                if (string.IsNullOrEmpty(legacyPath))
                {
                    error = "Legacy manifest requires JSONL path.";
                    return false;
                }
                try
                {
                    path = Path.GetFullPath(legacyPath);
                }
                catch (Exception exception) when (exception is ArgumentException ||
                                                  exception is NotSupportedException ||
                                                  exception is PathTooLongException)
                {
                    error = "Legacy JSONL path is invalid.";
                    return false;
                }
                return true;
            }
            hasLocator = true;
            if (!Text(output, "jsonl_locator", out string locator))
            {
                error = "Manifest JSONL locator is malformed.";
                return false;
            }
            if (string.IsNullOrEmpty(locator) || locator.IndexOf('\0') >= 0 || locator.Contains("\\") ||
                Path.IsPathRooted(locator))
            {
                error = "Manifest JSONL locator is unsafe.";
                return false;
            }
            string[] parts = locator.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    error = "Manifest JSONL locator is unsafe.";
                    return false;
                }
            }

            string baseDir;
            try
            {
                string manifestFullPath = Path.GetFullPath(manifestPath);
                baseDir = Path.GetDirectoryName(manifestFullPath);
                if (string.IsNullOrEmpty(baseDir))
                {
                    error = "Manifest path has no bundle directory.";
                    return false;
                }
            }
            catch (Exception exception) when (exception is ArgumentException ||
                                              exception is NotSupportedException ||
                                              exception is PathTooLongException)
            {
                error = "Manifest path is invalid.";
                return false;
            }

            string walk = baseDir;
            foreach (string part in parts)
            {
                walk = Path.Combine(walk, part);
                try
                {
                    if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Manifest JSONL locator traverses a link.";
                        return false;
                    }
                }
                catch (FileNotFoundException)
                {
                    // The later single read reports a missing committed JSONL.  A missing
                    // final component cannot itself be a link.
                }
                catch (DirectoryNotFoundException)
                {
                    // As above; do not probe outside the manifest's bundle directory.
                }
                catch (Exception exception) when (exception is IOException ||
                                                  exception is UnauthorizedAccessException)
                {
                    error = "Could not inspect manifest JSONL locator: " + exception.Message;
                    return false;
                }
            }
            path = Path.GetFullPath(walk);
            if (!path.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != baseDir)
            {
                error = "Manifest JSONL locator escapes bundle.";
                return false;
            }
            return true;
        }

        private static bool Text(AnchorPoseJson.JsonValue parent, string name, out string text)
        {
            text = null;
            return AnchorPoseJson.Required(parent, name, out AnchorPoseJson.JsonValue value) &&
                   value.String != null && (text = value.String) != null;
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter
                .ToString(hash.ComputeHash(bytes))
                .Replace("-", "")
                .ToLowerInvariant();
        }
    }
}
