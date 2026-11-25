// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>A box in image pixels (x right, y down from the top-left), x1 &lt; x2 and y1 &lt; y2.</summary>
    public readonly struct ScanPixelBox
    {
        public readonly double X1, Y1, X2, Y2;

        public ScanPixelBox(double x1, double y1, double x2, double y2)
        {
            X1 = Math.Min(x1, x2);
            Y1 = Math.Min(y1, y2);
            X2 = Math.Max(x1, x2);
            Y2 = Math.Max(y1, y2);
        }

        public double Width => X2 - X1;
        public double Height => Y2 - Y1;
        public double CenterX => (X1 + X2) * 0.5;
        public double CenterY => (Y1 + Y2) * 0.5;

        public bool Contains(double x, double y) => x >= X1 && x <= X2 && y >= Y1 && y <= Y2;

        public override string ToString() => $"({X1:0.#}, {Y1:0.#}) - ({X2:0.#}, {Y2:0.#})";
    }

    /// <summary>
    /// What one Segment request asks for: the drawn box projected into the photo, the user's clicks (positive or negative),
    /// and the points to send instead of the box when the server cannot take a box (an older server takes the box corners
    /// as clicks, so it must never see them).
    /// </summary>
    public sealed class ScanPrompt
    {
        public ScanPixelBox? Box;
        public readonly List<ScanPoint> Clicks = new List<ScanPoint>();
        /// <summary>Positive clicks used in place of the box on a server without box prompts (the projected box centre).</summary>
        public readonly List<ScanPoint> BoxFallback = new List<ScanPoint>();

        public static ScanPrompt ForBox(ScanPixelBox box, IEnumerable<ScanPoint> fallback)
        {
            var prompt = new ScanPrompt { Box = box };
            if (fallback != null) prompt.BoxFallback.AddRange(fallback);
            if (prompt.BoxFallback.Count == 0) prompt.BoxFallback.Add(new ScanPoint(box.CenterX, box.CenterY));
            return prompt;
        }

        public static ScanPrompt ForClicks(IEnumerable<ScanPoint> clicks)
        {
            var prompt = new ScanPrompt();
            if (clicks != null) prompt.Clicks.AddRange(clicks);
            return prompt;
        }

        public bool HasBox => Box.HasValue;

        /// <summary>
        /// The points on the wire. With box prompts: the clicks plus the two corners (z = 2, 3). Without: the clicks plus the
        /// fallback points, all as clicks.
        /// </summary>
        public ScanPoint[] ToWire(bool boxPrompts)
        {
            var wire = new List<ScanPoint>(Clicks.Count + 2);
            wire.AddRange(Clicks);
            if (Box.HasValue)
            {
                if (boxPrompts)
                {
                    ScanPixelBox b = Box.Value;
                    wire.Add(new ScanPoint(b.X1, b.Y1, ScanPointLabel.BoxTopLeft));
                    wire.Add(new ScanPoint(b.X2, b.Y2, ScanPointLabel.BoxBottomRight));
                }
                else
                {
                    foreach (ScanPoint p in BoxFallback) wire.Add(new ScanPoint(p.X, p.Y, ScanPointLabel.Positive));
                }
            }
            return wire.ToArray();
        }
    }

    /// <summary>
    /// The passthrough camera's pinhole model, as captured with a photo: the pose of the camera in the world as
    /// PassthroughCameraUtils.GetCameraPoseInWorld returns it (Unity axes: x right, y up, z forward), the intrinsics in
    /// sensor pixels (Android LENS_INTRINSIC_CALIBRATION: the principal point from the top-left), and the size of the texture
    /// the photo was taken from (an upright image: the PNG's first row is the top). A point above the optical axis is above
    /// the principal point, so v (from the top) = cy - fy y / z. Meta's helper ScreenPointToRayInCamera reads y from the
    /// bottom with the same cy, which differs from this by (sensorH - 2 cy) pixels: small when cy is near the middle.
    /// </summary>
    public readonly struct ScanCameraModel
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector2 Focal;
        public readonly Vector2 Principal;
        public readonly Vector2Int Sensor;
        public readonly int ImageWidth;
        public readonly int ImageHeight;

        public ScanCameraModel(Vector3 position, Quaternion rotation, Vector2 focal, Vector2 principal, Vector2Int sensor,
                               int imageWidth, int imageHeight)
        {
            Position = position;
            Rotation = rotation;
            Focal = focal;
            Principal = principal;
            Sensor = sensor;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
        }

        public bool IsValid => Focal.x > 0f && Focal.y > 0f && Sensor.x > 0 && Sensor.y > 0 && ImageWidth > 0 && ImageHeight > 0;

        /// <summary>
        /// A world point in photo pixels (top-left origin, the server's convention), or false when it is behind the camera.
        /// p_cam = R^-1 (p - t); u = cx + fx x/z; v = cy - fy y/z (y up in the camera, v down in the image); both scaled to the
        /// photo (W / sensorW, H / sensorH: a resize of the sensor frame, not a crop). Not clamped.
        /// </summary>
        public bool TryProject(Vector3 world, out Vector2 pixel)
        {
            pixel = default;
            if (!IsValid) return false;
            Vector3 local = Quaternion.Inverse(Rotation) * (world - Position);
            if (local.z <= 1e-4f) return false;
            float u = Focal.x * (local.x / local.z) + Principal.x;
            float v = Principal.y - Focal.y * (local.y / local.z);
            pixel = new Vector2(u * ImageWidth / Sensor.x, v * ImageHeight / Sensor.y);
            return true;
        }
    }

    /// <summary>Turns the drawn 3D box into the 2D prompt for the photo it was captured with.</summary>
    public static class ScanBoxProjection
    {
        /// <summary>A box narrower than this (pixels, after clamping) is not a usable prompt.</summary>
        public const double MinBoxPixels = 8.0;

        /// <summary>
        /// The image box around the eight corners of a box (center, size, rotation in the world), clamped to the photo, and
        /// the projected box centre as the fallback click. False when any corner is behind the camera, or the clamped box is
        /// smaller than <see cref="MinBoxPixels"/>. The capture card sends no box (Segment carries
        /// the user's points only, and nothing refuses a capture because the box is not in view).
        /// </summary>
        public static bool TryBuildPrompt(ScanCameraModel camera, Vector3 center, Vector3 size, Quaternion rotation,
                                          out ScanPrompt prompt, out float coverage)
        {
            prompt = null;
            if (!TryProjectBox(camera, center, size, rotation, out ScanPixelBox box, out coverage)) return false;

            var fallback = new List<ScanPoint>();
            if (camera.TryProject(center, out Vector2 c) && box.Contains(c.x, c.y))
                fallback.Add(new ScanPoint(c.x, c.y));
            prompt = ScanPrompt.ForBox(box, fallback);
            return true;
        }

        /// <summary>The image box of <see cref="TryBuildPrompt"/> alone, with no allocation.</summary>
        public static bool TryProjectBox(ScanCameraModel camera, Vector3 center, Vector3 size, Quaternion rotation,
                                         out ScanPixelBox box, out float coverage)
        {
            box = default;
            coverage = 0f;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                if (!camera.TryProject(Corner(center, size, rotation, i), out Vector2 p)) return false;
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }
            double x1 = Clamp(minX, camera.ImageWidth - 1), x2 = Clamp(maxX, camera.ImageWidth - 1);
            double y1 = Clamp(minY, camera.ImageHeight - 1), y2 = Clamp(maxY, camera.ImageHeight - 1);
            if (x2 - x1 < MinBoxPixels || y2 - y1 < MinBoxPixels) return false;
            box = new ScanPixelBox(x1, y1, x2, y2);
            coverage = (float)(box.Width * box.Height / ((double)camera.ImageWidth * camera.ImageHeight));
            return true;
        }

        public static Vector3[] Corners(Vector3 center, Vector3 size, Quaternion rotation)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++) corners[i] = Corner(center, size, rotation, i);
            return corners;
        }

        /// <summary>The box's corner <paramref name="i"/> (0..7) in the world.</summary>
        public static Vector3 Corner(Vector3 center, Vector3 size, Quaternion rotation, int i)
        {
            Vector3 e = size * 0.5f;
            var local = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            return center + rotation * local;
        }

        private static double Clamp(double v, int limit) => v < 0 ? 0 : v > limit ? limit : v;
    }
}
