// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>What the scan panel tells the user about its camera. Pure text and tone, so it is testable without a scene.</summary>
    public struct ScanCameraMessage
    {
        public bool Show;
        public StatusTone Tone;
        public string Title;
        public string Detail;
        /// <summary>The message offers "Allow camera" (the permissions were refused).</summary>
        public bool OffersAllow;
    }

    /// <summary>
    /// The scan panel's look and words, from the B Graphite tokens only: no colour is written here. The mask and the tapped
    /// points use the accent (virtual things); <see cref="UiPalette.RealAnchor"/> (orange) stays the room's real outlines.
    /// </summary>
    public static class ScanPanelStyle
    {
        public const string CaptureLabel = "Capture";
        public const string NewCaptureLabel = "Start New Capture";
        public const string ReconstructLabel = "Reconstruct 3D";
        public const string AllowCameraLabel = "Allow camera";
        public const string NotReadyTitle = "Capture not ready";
        public const string NotReadyDetail = "Wait for the camera view to appear, then press Capture.";
        public const string HintLive = "Look at the object, then press Capture.";
        public const string HintTap = "Tap the object on the photo.";
        public const string HintTapMore = "Tap again to refine, then press Reconstruct 3D.";

        /// <summary>Opacity of the mask over the photo: strong enough to read on a bright photo, low enough to see the object under it.</summary>
        public const float MaskAlpha = 0.45f;
        public const float DotRadiusUnits = 14f;
        public const float FrameRadius = 20f;
        public const float FramePad = 10f;
        public const float ToastSeconds = 4f;

        public static Color MaskColor => new Color(UiTheme.Palette.Accent.r, UiTheme.Palette.Accent.g, UiTheme.Palette.Accent.b, MaskAlpha);
        public static Color32 MaskColor32 => MaskColor;
        public static Color DotFill => UiTheme.Palette.Accent;
        public static Color DotRing => UiTheme.Palette.OnAccent;

        /// <summary>The smallest hit target in a canvas whose unit is <paramref name="canvasScale"/> metres: the theme's row, 84 units of the control panel.</summary>
        public static float MinHitUnits(float canvasScale) => canvasScale <= 0f ? UiTheme.RowHeight : UiTheme.RowHeight * UiTheme.MetresPerUnit / canvasScale;

        /// <summary>Font size in this canvas's units for a room-reading em of <paramref name="mm"/> millimetres.</summary>
        public static float FontUnits(float mm, float canvasScale) => canvasScale <= 0f ? UiTheme.FontBody : Mathf.Ceil(UiTheme.UnitsForMm(mm, canvasScale));

        public static string PointsText(int count) => count == 1 ? "1 point" : count + " points";

        public static string ZoomText(float level) => $"Zoom {level:F1}x";

        public static string HintFor(bool captured, int taps) => !captured ? HintLive : taps == 0 ? HintTap : HintTapMore;

        /// <summary>The words for what the camera is doing. Live shows nothing.</summary>
        public static ScanCameraMessage Describe(ScanCameraStatus status)
        {
            switch (status)
            {
                case ScanCameraStatus.PermissionPending:
                    return new ScanCameraMessage { Show = true, Tone = StatusTone.Busy, Title = "Waiting for camera access",
                        Detail = "Choose Allow in the headset prompt. The camera view starts by itself." };
                case ScanCameraStatus.PermissionDenied:
                    return new ScanCameraMessage { Show = true, Tone = StatusTone.Bad, Title = "Camera access is off",
                        Detail = "Scanning needs the headset cameras. Allow it here, or in Settings > Apps > Permissions.", OffersAllow = true };
                case ScanCameraStatus.Starting:
                case ScanCameraStatus.Idle:
                    return new ScanCameraMessage { Show = true, Tone = StatusTone.Busy, Title = "Starting the camera", Detail = string.Empty };
                case ScanCameraStatus.Unsupported:
                    return new ScanCameraMessage { Show = true, Tone = StatusTone.Bad, Title = "Camera not available",
                        Detail = "This headset can't share its cameras with apps (Quest 3 and 3S, Horizon OS v74 or newer)." };
                case ScanCameraStatus.NoCamera:
                    return new ScanCameraMessage { Show = true, Tone = StatusTone.Bad, Title = "No camera image",
                        Detail = "Make sure passthrough is on, then reopen this panel." };
                default:
                    return default;
            }
        }
    }
}
