// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Assertions;
using PCD = SpatialBlend.Samples.Scanning.PassthroughCameraDebugger;

namespace SpatialBlend.Samples.Scanning
{
    public class WebCamTextureManager : MonoBehaviour
    {
        [SerializeField] public PassthroughCameraEye Eye = PassthroughCameraEye.Left;
        [SerializeField, Tooltip("The requested resolution of the camera may not be supported by the chosen camera. In such cases, the closest available values will be used.\n\n" +
                                 "When set to (0,0), the highest supported resolution will be used.")]
        public Vector2Int RequestedResolution;
        [SerializeField] public PassthroughCameraPermissions CameraPermissions;

        /// <summary>
        /// Returns <see cref="WebCamTexture"/> reference if required permissions were granted and this component is enabled. Else, returns null.
        /// </summary>
        public WebCamTexture WebCamTexture { get; private set; }

        /// <summary>Why there is, or is not yet, a camera image. The scan panel shows it.</summary>
        public ScanCameraStatus Status { get; private set; } = ScanCameraStatus.Idle;

        /// <summary>Seconds without the camera showing up in <c>WebCamTexture.devices</c> before the status says so.</summary>
        public const float NoCameraSeconds = 8f;

        private bool _wanted;
        private Coroutine _init;
        private bool _running;

        /// <summary>True between SetEye and StopCapture: someone wants the camera on.</summary>
        public bool CaptureWanted => _wanted;
        private float _retryAt;

        /// <summary>
        /// The one camera provider of the scene. A scene that has none gets one made here: the panel must not depend on a prefab
        /// instance that the scene can lose (the v2 scene did; the camera never started).
        /// </summary>
        public static WebCamTextureManager EnsureInstance()
        {
            var found = FindAnyObjectByType<WebCamTextureManager>(FindObjectsInactive.Include);
            if (found != null) return found;
            var go = new GameObject("WebCamTextureManager");
            go.SetActive(false); // Awake and OnEnable run once the permissions component is attached
            go.AddComponent<PassthroughCameraPermissions>();
            var created = go.AddComponent<WebCamTextureManager>();
            go.SetActive(true);
            return created;
        }

        private void Awake()
        {
            PCD.DebugMessage(LogType.Log, $"{nameof(WebCamTextureManager)}.{nameof(Awake)}() was called");
            Assert.AreEqual(1, FindObjectsByType<WebCamTextureManager>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                $"PCA: Passthrough Camera: more than one {nameof(WebCamTextureManager)} component. Only one instance is allowed at a time. Current instance: {name}");
            if (CameraPermissions == null && !TryGetComponent(out CameraPermissions)) CameraPermissions = gameObject.AddComponent<PassthroughCameraPermissions>();
            CameraPermissions.AskCameraPermissions();
        }

        private void OnDisable()
        {
            PCD.DebugMessage(LogType.Log, $"PCA: {nameof(OnDisable)}() was called");
            if (_init != null) StopCoroutine(_init);
            _init = null;
            _running = false;
            ReleaseTexture();
        }

        private void Update()
        {
            // A texture that stopped (headset sleep, system pause) is released so the next lines start a fresh one.
            if (_wanted && WebCamTexture != null && !WebCamTexture.isPlaying)
            {
                ReleaseTexture();
                Status = ScanCameraStatus.Starting;
            }
            if (!_wanted || WebCamTexture != null || _running || Time.realtimeSinceStartup < _retryAt) return;
            if (!PassthroughCameraUtils.IsSupported)
            {
                Status = ScanCameraStatus.Unsupported;
                return;
            }
            var gate = PassthroughCameraPermissions.Gate;
            Status = gate.State switch
            {
                CameraPermissionState.Granted => ScanCameraStatus.Starting,
                CameraPermissionState.Denied => ScanCameraStatus.PermissionDenied,
                _ => ScanCameraStatus.PermissionPending,
            };
            if (gate.IsGranted)
            {
                _running = true; // before the start: a routine that ends at once clears it itself
                _init = StartCoroutine(InitializeWebCamTexture());
            }
        }

        /// <summary>The user pressed "Allow camera" after a denial.</summary>
        public void RetryPermission()
        {
            PassthroughCameraPermissions.Retry();
            Status = ScanCameraStatus.PermissionPending;
        }

        private IEnumerator InitializeWebCamTexture()
        {
            try
            {
                var inner = InitializeCore();
                while (inner.MoveNext()) yield return inner.Current;
            }
            finally
            {
                _running = false;
                _init = null;
            }
        }

        private IEnumerator InitializeCore()
        {
            // Check if Passhtrough is present in the scene and is enabled
            var ptLayer = FindAnyObjectByType<OVRPassthroughLayer>();
            if (ptLayer == null || !PassthroughCameraUtils.IsPassthroughEnabled())
            {
                PCD.DebugMessage(LogType.Error, "Passthrough must be enabled to use the Passthrough Camera API.");
                Status = ScanCameraStatus.NoCamera;
                _init = null;
                _retryAt = Time.realtimeSinceStartup + 2f;
                yield break;
            }

#if !UNITY_6000_OR_NEWER
            // There is a bug on Unity 2022 that causes a crash if you don't wait a frame before initializing the WebCamTexture.
            // Waiting for one frame is important and prevents the bug.
            yield return new WaitForEndOfFrame();
#endif

            float started = Time.realtimeSinceStartup;
            int tries = 0;
            while (_wanted)
            {
                var devices = WebCamTexture.devices;
                if (PassthroughCameraUtils.EnsureInitialized() && PassthroughCameraUtils.CameraEyeToCameraIdMap.TryGetValue(Eye, out var cameraData))
                {
                    if (cameraData.index < devices.Length)
                    {
                        var deviceName = devices[cameraData.index].name;
                        WebCamTexture webCamTexture;
                        if (RequestedResolution == Vector2Int.zero)
                        {
                            var largestResolution = PassthroughCameraUtils.GetOutputSizes(Eye).OrderBy(static size => size.x * size.y).Last();
                            webCamTexture = new WebCamTexture(deviceName, largestResolution.x, largestResolution.y);
                        }
                        else
                        {
                            webCamTexture = new WebCamTexture(deviceName, RequestedResolution.x, RequestedResolution.y);
                        }
                        webCamTexture.Play();
                        var currentResolution = new Vector2Int(webCamTexture.width, webCamTexture.height);
                        if (RequestedResolution != Vector2Int.zero && RequestedResolution != currentResolution)
                        {
                            PCD.DebugMessage(LogType.Warning, $"WebCamTexture created, but '{nameof(RequestedResolution)}' {RequestedResolution} is not supported. Current resolution: {currentResolution}.");
                        }
                        WebCamTexture = webCamTexture;
                        Status = ScanCameraStatus.Live;
                        _init = null;
                        PCD.DebugMessage(LogType.Log, $"WebCamTexture created, texturePtr: {WebCamTexture.GetNativeTexturePtr()}, size: {WebCamTexture.width}/{WebCamTexture.height}");
                        yield break;
                    }
                }

                if (tries++ % 120 == 0)
                    PCD.DebugMessage(LogType.Error, $"Requested camera is not present in WebCamTexture.devices: {string.Join(", ", devices)}.");
                if (Time.realtimeSinceStartup - started > NoCameraSeconds) Status = ScanCameraStatus.NoCamera;
                yield return null;
            }
            _init = null;
        }

        /// <summary>
        /// Starts the camera (when its permissions allow) with the given eye, or restarts it for another eye. Safe to call before the
        /// permissions are granted: the camera starts by itself the moment they are.
        /// </summary>
        /// <param name="isLeft">If true, use the left eye camera; otherwise, use the right eye camera.</param>
        public void SetEye(bool isLeft)
        {
            var newEye = isLeft ? PassthroughCameraEye.Left : PassthroughCameraEye.Right;
            var needsRestart = WebCamTexture == null || !WebCamTexture.isPlaying;
            if (Eye == newEye && !needsRestart && _wanted)
            {
                return;
            }

            Eye = newEye;
            _wanted = true;

            ReleaseTexture();
            if (_init != null) { StopCoroutine(_init); _init = null; }
            _running = false;
            Status = ScanCameraStatus.Idle;
            // Update starts it as soon as the permissions are there.
        }

        public void StopCapture()
        {
            _wanted = false;
            if (_init != null) { StopCoroutine(_init); _init = null; }
            _running = false;
            ReleaseTexture();
            Status = ScanCameraStatus.Idle;
        }

        private void ReleaseTexture()
        {
            if (WebCamTexture == null) return;
            WebCamTexture.Stop();
            Destroy(WebCamTexture);
            WebCamTexture = null;
        }
    }

    /// <summary>What the scan camera is doing; the scan panel turns it into a message.</summary>
    public enum ScanCameraStatus
    {
        Idle,
        PermissionPending,
        PermissionDenied,
        Starting,
        Live,
        /// <summary>The headset has no passthrough camera access (Quest 2, an old Horizon OS, the Editor).</summary>
        Unsupported,
        /// <summary>The permissions are granted but the camera did not appear (passthrough off, or the system is slow).</summary>
        NoCamera,
    }

    /// <summary>
    /// Defines the position of a passthrough camera relative to the headset
    /// </summary>
    public enum PassthroughCameraEye
    {
        Left,
        Right
    }
}
