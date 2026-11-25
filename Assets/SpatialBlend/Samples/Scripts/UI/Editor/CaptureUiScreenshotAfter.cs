// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using SpatialBlend.Samples.Scanning;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>The renders that only exist with the camera messages and toasts (nothing is drawn for them before the restyle).</summary>
    internal static class CaptureUiScreenshotAfter
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        internal static void Run(Camera cam, string dir, string prefix)
        {
            foreach (var (status, name) in new[]
            {
                (ScanCameraStatus.PermissionPending, "4-camera-waiting-for-permission"),
                (ScanCameraStatus.PermissionDenied, "5-camera-permission-denied"),
                (ScanCameraStatus.Starting, "6-camera-starting"),
                (ScanCameraStatus.NoCamera, "7-camera-no-image"),
            })
            {
                var root = CaptureUiScreenshot.NewPanelPublic(cam, out var panel);
                panel.OpenPanel();
                typeof(PassthroughPanelManager).GetMethod("SetCameraMessage", Any).Invoke(panel, new object[] { ScanPanelStyle.Describe(status) });
                CaptureUiScreenshot.ShotPublic(cam, root, dir, prefix + "-" + name);
                Object.DestroyImmediate(root);
            }

            var photo = CaptureUiScreenshot.PhotoPublic();
            var withToast = CaptureUiScreenshot.NewPanelPublic(cam, out var p2);
            p2.StillOverride = photo;
            p2.OpenPanel();
            typeof(PassthroughPanelManager).GetMethod("Toast", Any).Invoke(p2, new object[] { StatusTone.Busy, ScanPanelStyle.NotReadyTitle, ScanPanelStyle.NotReadyDetail });
            CaptureUiScreenshot.ShotPublic(cam, withToast, dir, prefix + "-8-capture-not-ready-toast");
            typeof(PassthroughPanelManager).GetMethod("Toast", Any).Invoke(p2, new object[] { StatusTone.Bad, ScanFailureText.Title, ScanFailureText.ToUserMessage(new SpatialBlend.Freeform.Scanning.ScanError(SpatialBlend.Freeform.Scanning.ScanErrorCode.ConnectFailed, "x")) });
            CaptureUiScreenshot.ShotPublic(cam, withToast, dir, prefix + "-9-scan-failed-toast");
            Object.DestroyImmediate(withToast);
            Object.DestroyImmediate(photo);
        }
    }
}
#endif
