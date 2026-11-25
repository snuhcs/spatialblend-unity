// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Plain-words text for a scan that did not work. The scan service reports a technical <see cref="ScanError"/> (it goes to the
    /// log, as <c>GltfScanResultSink.OnScanFailed</c> already does); the user gets one short sentence with what to do next.
    /// Every <see cref="ScanErrorCode"/> is mapped.
    /// </summary>
    public static class ScanFailureText
    {
        public const string Title = "The scan didn't work";
        public const string Fallback = "The scan didn't finish. Try again, or skip this step.";

        public static string ToUserMessage(ScanError error)
        {
            switch (error.Code)
            {
                case ScanErrorCode.None:
                    return string.Empty;
                case ScanErrorCode.InvalidConfig:
                case ScanErrorCode.NotInitialized:
                    return "Scanning isn't set up on this headset. You can skip this step.";
                case ScanErrorCode.ConnectFailed:
                case ScanErrorCode.TransportFault:
                    return "Couldn't reach the scan server. Check your Wi-Fi and try again.";
                case ScanErrorCode.Timeout:
                    return "The scan took too long. Try again, or skip this step.";
                case ScanErrorCode.Cancelled:
                    return "The scan was cancelled.";
                case ScanErrorCode.EmptyImage:
                    return "There was no photo to send. Capture one and try again.";
                case ScanErrorCode.EmptyMask:
                case ScanErrorCode.InvalidMaskSize:
                case ScanErrorCode.MaskUnreadable:
                case ScanErrorCode.NoPoints:
                case ScanErrorCode.InvalidPoint:
                    return "Tap the object on the photo first, then press Reconstruct 3D.";
                case ScanErrorCode.BoxPromptUnsupported:
                    return "The scan server is out of date for this capture. Retake, or tap the object on the photo.";
                case ScanErrorCode.RequestRejected:
                case ScanErrorCode.ServerFailed:
                case ScanErrorCode.MissingOutput:
                    return "The server couldn't build a 3D copy from this photo. Try another photo.";
                default:
                    return Fallback;
            }
        }
    }
}
