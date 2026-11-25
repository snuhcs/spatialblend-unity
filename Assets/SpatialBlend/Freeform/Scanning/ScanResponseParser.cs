// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>Turns a completion callback and the files it names into a typed result.</summary>
    public static class ScanResponseParser
    {
        public const string ModelSuffix = "_objmodel.glb";
        public const string InfoSuffix = "_objinfo.json";
        /// <summary>The camera pose at capture, copied beside the output so the object pose can be read in the world.</summary>
        public const string CameraInfoSuffix = "_caminfo.json";

        private static readonly System.Text.RegularExpressions.Regex BoxMarker =
            new System.Text.RegularExpressions.Regex(@"(^|[^A-Za-z0-9_])box:1([^0-9]|$)");

        /// <summary>The scan server writes "box:1" in a Segment answer when it used the request's box.</summary>
        public static bool ServerUsedBox(string message) => !string.IsNullOrEmpty(message) && BoxMarker.IsMatch(message);

        /// <summary>
        /// Reads the mask the server wrote and removes the file. The width is the image width. The
        /// height comes from the byte count when it divides evenly, otherwise from the image height.
        /// </summary>
        public static ScanSegmentResult ParseSegment(ScanCompletion completion, string maskPath, int imageWidth,
                                                     int imageHeight, IScanFileSystem files)
        {
            var result = new ScanSegmentResult { EventHandle = completion.EventHandle };
            if (!completion.Success)
            {
                try { files.Delete(maskPath); } catch { /* best-effort cleanup of a partial file */ }
                result.Error = new ScanError(ScanErrorCode.ServerFailed, FailureMessage(completion));
                return result;
            }

            byte[] mask;
            try
            {
                mask = files.ReadAllBytes(maskPath);
            }
            catch (Exception e)
            {
                result.Error = new ScanError(ScanErrorCode.MaskUnreadable,
                    $"Mask file '{maskPath}' could not be read: {e.Message}");
                return result;
            }
            finally
            {
                try { files.Delete(maskPath); } catch { /* best-effort cleanup */ }
            }

            if (mask == null || mask.Length == 0)
            {
                result.Error = new ScanError(ScanErrorCode.MissingOutput, "The server wrote an empty mask.");
                return result;
            }

            int width = imageWidth;
            int height = (width > 0 && mask.Length % width == 0) ? mask.Length / width : imageHeight;
            if (width <= 0 || height <= 0)
            {
                result.Error = new ScanError(ScanErrorCode.InvalidMaskSize, "The mask size could not be derived.");
                return result;
            }

            result.Success = true;
            result.Mask = mask;
            result.Width = width;
            result.Height = height;
            return result;
        }

        /// <summary>Names the GLB and info files after the output prefix. It reads no file.</summary>
        public static bool TryParseReconstruction(ScanCompletion completion, ScanKind kind, string outputPrefix,
                                                  out ScanReconstructionResult result, out ScanError error)
        {
            result = null;
            if (!completion.Success)
            {
                error = new ScanError(ScanErrorCode.ServerFailed, FailureMessage(completion));
                return false;
            }
            string name = string.IsNullOrWhiteSpace(outputPrefix) ? null : Path.GetFileName(outputPrefix);
            if (string.IsNullOrWhiteSpace(name))
            {
                error = new ScanError(ScanErrorCode.MissingOutput,
                    $"No output name could be derived from '{outputPrefix}'.");
                return false;
            }

            result = new ScanReconstructionResult
            {
                EventHandle = completion.EventHandle,
                Kind = kind,
                ObjectName = name,
                ModelFileName = name + ModelSuffix,
                InfoFileName = name + InfoSuffix,
                OutputPrefix = outputPrefix,
            };
            error = ScanError.None;
            return true;
        }

        private static string FailureMessage(ScanCompletion completion) =>
            string.IsNullOrWhiteSpace(completion.Message) ? "The server reported a failure." : completion.Message;
    }
}
