// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace SpatialBlend.Freeform.Scanning
{
    public readonly struct ScanBuildResult
    {
        public readonly ScanTransportRequest Request;
        public readonly ScanError Error;

        public ScanBuildResult(ScanTransportRequest request, ScanError error)
        {
            Request = request;
            Error = error;
        }

        public bool Ok => !Error.IsError && Request != null;
    }

    /// <summary>
    /// Builds the argument bundle for each request. It validates input and picks output paths.
    /// It never touches the network, the clock, or the disk.
    /// </summary>
    public static class ScanRequestBuilder
    {
        public const string TimestampFormat = "yyyyMMdd_HHmmssffff";

        public static ScanBuildResult BuildSegment(ScanSessionConfig config, byte[] image, ScanPoint[] points,
                                                   DateTime utcNow)
        {
            ScanError error = CheckCommon(config, image);
            if (error.IsError) return Fail(error);
            error = CheckPrompt(points);
            if (error.IsError) return Fail(error);

            string maskPath = Path.Combine(config.CacheDirectory ?? string.Empty,
                $"seg_mask_{config.ClientId}_{utcNow.ToString(TimestampFormat)}.bin");
            return Ok(new ScanTransportRequest
            {
                Kind = ScanKind.Segment,
                ClientId = config.ClientId,
                ImageBytes = image,
                Points = points,
                OutputPath = maskPath,
            });
        }

        public static ScanBuildResult BuildReconstruct(ScanSessionConfig config, byte[] image, byte[] mask,
                                                       int maskWidth, int maskHeight, DateTime utcNow)
        {
            ScanError error = CheckCommon(config, image);
            if (error.IsError) return Fail(error);
            if (mask == null || mask.Length == 0)
                return Fail(new ScanError(ScanErrorCode.EmptyMask, "The mask is empty."));
            if (maskWidth <= 0 || maskHeight <= 0 || (long)maskWidth * maskHeight != mask.Length)
                return Fail(new ScanError(ScanErrorCode.InvalidMaskSize,
                    $"Mask {maskWidth}x{maskHeight} does not match {mask.Length} bytes."));

            return Ok(new ScanTransportRequest
            {
                Kind = ScanKind.Reconstruct,
                ClientId = config.ClientId,
                ImageBytes = image,
                MaskBytes = mask,
                MaskWidth = maskWidth,
                MaskHeight = maskHeight,
                OutputPath = OutputPrefix(config, utcNow),
            });
        }

        /// <summary>The native side writes "{prefix}_objmodel.glb" and "{prefix}_objinfo.json".</summary>
        public static string OutputPrefix(ScanSessionConfig config, DateTime utcNow) =>
            Path.Combine(config.OutputDirectory ?? string.Empty,
                $"sam3d_{config.ClientId}_{utcNow.ToString(TimestampFormat)}");

        private static ScanError CheckCommon(ScanSessionConfig config, byte[] image)
        {
            if (config == null) return new ScanError(ScanErrorCode.InvalidConfig, "No scan configuration.");
            if (image == null || image.Length == 0)
                return new ScanError(ScanErrorCode.EmptyImage, "The image is empty.");
            if (string.IsNullOrWhiteSpace(config.ClientId))
                return new ScanError(ScanErrorCode.InvalidConfig, "The client id is empty.");
            return ScanError.None;
        }

        /// <summary>
        /// A prompt is a box (exactly one top-left and one bottom-right corner) and/or clicks, with at least one positive
        /// click or the box: negative clicks alone select nothing.
        /// </summary>
        private static ScanError CheckPrompt(ScanPoint[] points)
        {
            if (points == null || points.Length == 0)
                return new ScanError(ScanErrorCode.NoPoints, "At least one point or a box is required.");
            int topLeft = 0, bottomRight = 0, positive = 0;
            ScanPoint corner1 = default, corner2 = default;
            for (int i = 0; i < points.Length; i++)
            {
                ScanPoint p = points[i];
                if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y) ||
                    p.X < 0.0 || p.Y < 0.0)
                    return new ScanError(ScanErrorCode.InvalidPoint, $"Point {i} is not a valid pixel position.");
                switch (p.Label)
                {
                    case ScanPointLabel.Positive: positive++; break;
                    case ScanPointLabel.Negative: break;
                    case ScanPointLabel.BoxTopLeft: topLeft++; corner1 = p; break;
                    case ScanPointLabel.BoxBottomRight: bottomRight++; corner2 = p; break;
                    default: return new ScanError(ScanErrorCode.InvalidPoint, $"Point {i} has an unknown label.");
                }
            }
            if (topLeft + bottomRight > 0 && (topLeft != 1 || bottomRight != 1))
                return new ScanError(ScanErrorCode.InvalidPoint, "A box needs exactly one top-left and one bottom-right corner.");
            if (topLeft == 1 && (corner2.X <= corner1.X || corner2.Y <= corner1.Y))
                return new ScanError(ScanErrorCode.InvalidPoint, "The box's bottom-right corner must be right of and below its top-left.");
            if (positive == 0 && topLeft == 0)
                return new ScanError(ScanErrorCode.NoPoints, "At least one positive point or a box is required.");
            return ScanError.None;
        }

        private static ScanBuildResult Ok(ScanTransportRequest request) => new ScanBuildResult(request, ScanError.None);

        private static ScanBuildResult Fail(ScanError error) => new ScanBuildResult(null, error);
    }
}
