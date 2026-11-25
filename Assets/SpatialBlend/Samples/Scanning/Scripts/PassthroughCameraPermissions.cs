// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// Keeps the scan camera's two permissions asked for and tracked (see <see cref="CameraPermissionGate"/>). It asks only for the
    /// camera pair: the scene permission and the rest belong to <c>SbPermissionManager</c>, and Unity keeps one set of callbacks
    /// per request, so the two never request at the same time. The answer is read from the system every half second, not from callbacks.
    /// </summary>
    public class PassthroughCameraPermissions : MonoBehaviour
    {
        /// <summary>Kept so the saved prefab still deserialises; the scene permission is requested by <c>SbPermissionManager</c>.</summary>
        [SerializeField] public List<string> PermissionRequestsOnStartup = new();

        public static readonly string[] CameraPermissions =
        {
            "android.permission.CAMERA",          // Required to use WebCamTexture object.
            "horizonos.permission.HEADSET_CAMERA" // Required to access the Passthrough Camera API in Horizon OS v74 and above.
        };

        private const float PollSeconds = 0.5f;
        private static CameraPermissionGate s_gate;
        private float _nextPoll;

        /// <summary>The gate shared by every user of the camera permissions (created on first use).</summary>
        public static CameraPermissionGate Gate => s_gate ??= new CameraPermissionGate(new PlatformBackend(), CameraPermissions);

        /// <summary>Replaces the gate (tests). Null restores the platform one.</summary>
        public static void SetGateForTests(CameraPermissionGate gate) => s_gate = gate;

        /// <summary>True once both permissions are granted, false while they are not, null before the first check.</summary>
        public static bool? HasCameraPermission =>
            Gate.State == CameraPermissionState.Unknown ? (bool?)null : Gate.IsGranted;

        /// <summary>Checks the permissions and, when something is missing and nothing is in flight, asks for it.</summary>
        public void AskCameraPermissions() => Gate.Tick(Time.realtimeSinceStartupAsDouble);

        /// <summary>The user pressed "Allow camera" after a denial.</summary>
        public static void Retry() => Gate.Retry(Time.realtimeSinceStartupAsDouble);

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;
            AskCameraPermissions();
        }

        private void OnApplicationFocus(bool focused)
        {
            // The prompt and the system settings are other windows; coming back is when a new answer is there to read.
            if (focused) _nextPoll = 0f;
        }

        private sealed class PlatformBackend : ICameraPermissionBackend
        {
            public bool OtherRequestPending => SbPermissionManager.RequestPending;

            public bool IsGranted(string permission)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(permission);
#else
                return true; // the Editor has no Horizon permissions
#endif
            }

            public void Request(string[] permissions, Action finished)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Count distinct permissions: some engine versions fire both Denied and DeniedAndDontAskAgain for one.
                var left = new System.Collections.Generic.HashSet<string>(permissions);
                void One(string name) { if (left.Remove(name) && left.Count == 0) finished(); }
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += One;
                callbacks.PermissionDenied += One;
                callbacks.PermissionDeniedAndDontAskAgain += One;
                Permission.RequestUserPermissions(permissions, callbacks);
#else
                finished();
#endif
            }
        }
    }
}
