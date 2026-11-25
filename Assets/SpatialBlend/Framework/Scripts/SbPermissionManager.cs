// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace SpatialBlend
{
    /// <summary>
    /// Requests all runtime Android permissions in a single batch at app startup.
    /// Auto-initializes before any scene loads via [RuntimeInitializeOnLoadMethod].
    /// </summary>
    public class SbPermissionManager : MonoBehaviour
    {
        private static SbPermissionManager _instance;
        private static bool _requested;

        private static readonly HashSet<string> _grantedPermissions = new();
        private static readonly HashSet<string> _deniedPermissions = new();

        private static readonly HashSet<string> _pending = new();

        /// <summary>True while the startup batch still waits for an answer. A second <c>RequestUserPermissions</c> in that time replaces its callbacks, so other requesters wait for this.</summary>
        public static bool RequestPending => _pending.Count > 0 && Time.realtimeSinceStartup - _requestedAt < PendingTimeoutSeconds;

        /// <summary>A permission whose callback never comes (a prompt dismissed by the system) must not block the others for ever.</summary>
        public const float PendingTimeoutSeconds = 45f;
        private static float _requestedAt;

        public static bool AllGranted => _deniedPermissions.Count == 0 && _grantedPermissions.Count > 0;

        public static bool IsGranted(string permission)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(permission);
#else
            return true;
#endif
        }

        private static readonly string[] RuntimePermissions =
        {
            "android.permission.CAMERA",
            "horizonos.permission.HEADSET_CAMERA",
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.RECORD_AUDIO",
            "com.oculus.permission.USE_SCENE",
        };

        public static IReadOnlyList<string> RequestedPermissions => RuntimePermissions;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_instance != null) return;

            var go = new GameObject("SbPermissionManager");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SbPermissionManager>();
#endif
        }

        private void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_requested) return;
            _requested = true;

            // Filter to only permissions not yet granted
            var needed = new List<string>();
            foreach (var perm in RuntimePermissions)
            {
                if (!Permission.HasUserAuthorizedPermission(perm))
                {
                    needed.Add(perm);
                }
                else
                {
                    _grantedPermissions.Add(perm);
                }
            }

            if (needed.Count == 0)
            {
                return;
            }

            foreach (var perm in needed) _pending.Add(perm);
            _requestedAt = Time.realtimeSinceStartup;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += OnPermissionGranted;
            callbacks.PermissionDenied += OnPermissionDenied;
            callbacks.PermissionDeniedAndDontAskAgain += OnPermissionDenied;

            Permission.RequestUserPermissions(needed.ToArray(), callbacks);
#endif
        }

#if UNITY_ANDROID
        private static void OnPermissionGranted(string permission)
        {
            _pending.Remove(permission);
            _grantedPermissions.Add(permission);
            _deniedPermissions.Remove(permission);
        }

        private static void OnPermissionDenied(string permission)
        {
            Debug.LogWarning($"[SbPermissionManager] Denied: {permission}");
            _pending.Remove(permission);
            _deniedPermissions.Add(permission);
        }
#endif
    }
}
