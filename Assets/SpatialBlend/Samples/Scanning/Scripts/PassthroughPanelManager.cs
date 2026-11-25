/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
// Modified by the SpatialBlend authors; this file stays under the license above (see NOTICE).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using TMPro;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Samples;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.FreeformCollaboration.UI;

namespace SpatialBlend.Samples.Scanning
{
    public class PassthroughPanelManager : MonoBehaviour
    {
        private WebCamTextureManager m_webCamTextureManager;
        private GameObject m_panel;
        private RawImage m_image;
        private Button m_leftEyeButton;
        private Button m_rightEyeButton;
        private Button m_captureButton;
        private Button m_sendToServerButton;
        private Button m_closeButton;
        private GameObject m_zoomButtonsGroup;
        private Button m_zoomInButton;
        private Button m_zoomOutButton;
        private TMP_Text m_zoomLevelText;
        private GameObject m_samButtonsGroup;
        private Button m_undoPointButton;
        private TMP_Text m_pointCountText;
        private RawImage m_maskOverlayImage;
        [SerializeField, Range(5, 40)] private float _pointDotSizePx = 20f;

        private bool isCamOn = false;
        private PassthroughCameraEye currentEye = PassthroughCameraEye.Left;
        private int sampledImgCount = 0;

        private float _zoomLevel = 1.0f;
        private const float ZoomStep = 0.1f;
        private const float ZoomMin  = 1.0f;
        private const float ZoomMax  = 2.0f;

        private Texture2D _capturedTex = null;
        private BoxAnnotationInstance _targetBoxAnnotation;

        private bool _debugDrawPixelsOnCapturedImage = false;
        private int _debugPixelDotRadius = 4;
        private Sam3dPendingIndicatorManager _sam3dPendingIndicatorManager;

        private byte[] _latestMaskBytes;
        private int _latestMaskWidth, _latestMaskHeight;
        private long _pendingSegmentEventHandle = -1;
        private Texture2D _maskOverlayTex;
        private byte[] _cachedImageBytes;
        private ScanPanelOverlays _overlays;

        private struct CaptureSnapshot
        {
            public bool IsValid;
            public PassthroughCameraEye Eye;
            public PassthroughCameraIntrinsics Intrinsics;
            public Pose CameraPoseInWorld;
            public int TextureWidth;
            public int TextureHeight;
            public float ScaleX;
            public float ScaleY;
            public string TimestampUtc;
        }

        private CaptureSnapshot _captureSnapshot;

        private string _androidDownloadPath = null;
        private SpatialBlendSettings _settings;
        private bool _isPanelOpen = false;
        private readonly List<Vector2> _pickedPixelPoints = new List<Vector2>();
        private readonly List<GameObject> _dotMarkers = new List<GameObject>();

        // The scan service is created by SessionManager.Initialize(). It is null while scanning is off.
        private static ScanService Scan => SessionManager.Instance != null ? SessionManager.Instance.Service : null;
        private ScanService _subscribedScan;

        /// <summary>
        /// Fired when the panel closes. ManipulationContext subscribes to clean up the instance.
        /// </summary>
        public event Action OnPanelClosed;

        /// <summary>Whether the scan camera panel is open (read by <c>ModeSwitchAssist</c>, which never leaves Scene Edit under it).</summary>
        public bool IsPanelOpen => _isPanelOpen;

        /// <summary>
        /// Raised at the start of <see cref="OpenPanel"/>, before it does anything, so a listener can configure the panel
        /// (the lobby tutorial gives it a sample photo). It is also how the tutorial notices that the panel opened.
        /// </summary>
        public static event Action<PassthroughPanelManager> PanelOpening;

        /// <summary>
        /// Raised when Reconstruct 3D could not even send its request (no points, no photo, scanning off). The panel only logs
        /// these; the argument is what a message to the user is built from (<c>ScanFailureText</c>).
        /// </summary>
        public event Action<ScanError> RequestFailed;

        /// <summary>Captures and taps since the panel opened.</summary>
        public ScanPanelProgress Progress { get; } = new ScanPanelProgress();

        /// <summary>
        /// A still image that stands in for the passthrough camera: the panel shows it as the live view and captures it. Null
        /// (the default) uses the camera. The lobby tutorial's canned scan sets it. It must be readable (decoded with LoadImage).
        /// </summary>
        public Texture2D StillOverride { get; set; }

        /// <summary>
        /// The prompt for a captured photo when there is no camera to project the box with (the tutorial's sample photo): the box
        /// in that photo's pixels. Null uses the drawn box projected through the capture snapshot.
        /// </summary>
        public ScanPixelBox? StillBoxPrompt { get; set; }

        /// <summary>
        /// Set the target BoxAnnotation whose 8 corners should be projected on SendToServer().
        /// Typically called by ManipulationContext before opening this panel.
        /// </summary>
        public void SetTargetBoxAnnotation(BoxAnnotationInstance instance)
        {
            _targetBoxAnnotation = instance;
        }

        /// <summary>
        /// Discovers UI references from child hierarchy by name.
        /// Serialized fields are used as fallback — if already assigned, auto-wire skips them.
        /// </summary>
        private void AutoWireReferences()
        {
            if (m_webCamTextureManager == null)
                m_webCamTextureManager = FindAnyObjectByType<WebCamTextureManager>();

            Transform canvas = transform.Find("Canvas");
            if (canvas == null) canvas = transform;

            if (m_panel == null)
                m_panel = canvas.gameObject;

            if (m_image == null)
            {
                var t = canvas.Find("RawImage");
                if (t != null) m_image = t.GetComponent<RawImage>();
            }

            if (m_maskOverlayImage == null)
            {
                var t = canvas.Find("RawImage/MaskImage");
                if (t != null) m_maskOverlayImage = t.GetComponent<RawImage>();
            }

            if (m_leftEyeButton == null)
            {
                var t = canvas.Find("Buttons/Left Camera");
                if (t != null) m_leftEyeButton = t.GetComponent<Button>();
            }
            if (m_rightEyeButton == null)
            {
                var t = canvas.Find("Buttons/Right Camera");
                if (t != null) m_rightEyeButton = t.GetComponent<Button>();
            }
            if (m_captureButton == null)
            {
                var t = canvas.Find("Buttons/Capture");
                if (t != null) m_captureButton = t.GetComponent<Button>();
            }
            if (m_sendToServerButton == null)
            {
                var t = canvas.Find("Buttons/Send Server");
                if (t != null) m_sendToServerButton = t.GetComponent<Button>();
            }
            if (m_closeButton == null)
            {
                var t = canvas.Find("TitleGroup/CloseButton");
                if (t != null) m_closeButton = t.GetComponent<Button>();
            }

            if (m_zoomButtonsGroup == null)
            {
                var t = canvas.Find("ZoomButtons");
                if (t != null) m_zoomButtonsGroup = t.gameObject;
            }
            if (m_zoomInButton == null)
            {
                var t = canvas.Find("ZoomButtons/ZoomIn");
                if (t != null) m_zoomInButton = t.GetComponent<Button>();
            }
            if (m_zoomOutButton == null)
            {
                var t = canvas.Find("ZoomButtons/ZoomOut");
                if (t != null) m_zoomOutButton = t.GetComponent<Button>();
            }
            if (m_zoomLevelText == null)
            {
                var t = canvas.Find("ZoomButtons/ZoomValue");
                if (t != null) m_zoomLevelText = t.GetComponent<TMP_Text>();
            }

            if (m_samButtonsGroup == null)
            {
                var t = canvas.Find("SAMButtons");
                if (t != null) m_samButtonsGroup = t.gameObject;
            }
            if (m_undoPointButton == null)
            {
                var t = canvas.Find("SAMButtons/Undo");
                if (t != null) m_undoPointButton = t.GetComponent<Button>();
            }
            if (m_pointCountText == null)
            {
                var t = canvas.Find("SAMButtons/PointsValue");
                if (t != null) m_pointCountText = t.GetComponent<TMP_Text>();
            }
        }

        private void Awake()
        {
            AutoWireReferences();

            if (m_leftEyeButton != null)
                m_leftEyeButton.onClick.AddListener(() => SetCameraEye(true));
            if (m_rightEyeButton != null)
                m_rightEyeButton.onClick.AddListener(() => SetCameraEye(false));
            if (m_captureButton != null)
                m_captureButton.onClick.AddListener(() => CaptureImage());
            if (m_sendToServerButton != null)
                m_sendToServerButton.onClick.AddListener(() => SendToServer());
            if (m_closeButton != null)
                m_closeButton.onClick.AddListener(() => ClosePanel());
            if (m_zoomInButton != null) m_zoomInButton.onClick.AddListener(ZoomIn);
            if (m_zoomOutButton != null) m_zoomOutButton.onClick.AddListener(ZoomOut);
            SetCaptureUIMode(false);
            if (m_undoPointButton != null)
                m_undoPointButton.onClick.AddListener(UndoLastPoint);
            if (m_image != null)
            {
                var picker = m_image.gameObject.AddComponent<RawImageClickReceiver>();
                picker.OnClick = OnRawImageClicked;
            }
            _sam3dPendingIndicatorManager = FindAnyObjectByType<Sam3dPendingIndicatorManager>();
            SubscribeSegments();
            if (m_image != null) _overlays = ScanPanelOverlays.Build(m_image, OnAllowCameraPressed);
            StyleControls();
            RefreshHint();
        }

        /// <summary>
        /// Gives every button of the panel at least the theme's hit target (a row, 4.9 cm, both ways) and room for its longest label,
        /// whatever this canvas's unit is. The authored buttons were 3.6 cm tall (the title's close chip 2.1 cm) and "Start New Capture"
        /// overflowed its pill.
        /// </summary>
        private void StyleControls()
        {
            float scale = Mathf.Max(1e-5f, m_panel != null ? m_panel.transform.lossyScale.x : transform.lossyScale.x);
            float min = ScanPanelStyle.MinHitUnits(scale);
            foreach (var button in GetComponentsInChildren<Button>(true))
            {
                if (_overlays != null && button == _overlays.AllowButton) continue; // built to size
                var rect = (RectTransform)button.transform;
                var labels = button == m_captureButton ? new[] { ScanPanelStyle.CaptureLabel, ScanPanelStyle.NewCaptureLabel }
                    : button == m_sendToServerButton ? new[] { ScanPanelStyle.ReconstructLabel }
                    : null;
                float width = min, height = min;
                var text = button.GetComponentInChildren<TMP_Text>(true);
                if (button == m_captureButton || button == m_sendToServerButton)
                {
                    // Accent buttons carry OnAccent text (the theme's contrast rule), whatever the prefab authored.
                    var p = UiTheme.Palette;
                    button.colors = ControlPanelKit.Block(p.Accent, p.AccentHover, p.AccentPressed, p.SurfaceDisabled);
                    foreach (var t in button.GetComponentsInChildren<TMP_Text>(true)) t.color = p.OnAccent;
                }
                if (labels != null && text != null)
                    foreach (string label in labels) width = Mathf.Max(width, text.GetPreferredValues(label).x + min * 0.8f);
                var size = rect.sizeDelta;
                if (rect.anchorMin.x == rect.anchorMax.x) size.x = Mathf.Max(size.x, width);
                if (rect.anchorMin.y == rect.anchorMax.y) size.y = Mathf.Max(size.y, height);
                rect.sizeDelta = size;
                var element = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
                element.minHeight = Mathf.Max(element.minHeight, height);
                element.minWidth = Mathf.Max(element.minWidth, width);
                // The Interaction SDK chip draws its own Background child at its own (small) size; the chip is the button.
                foreach (var background in button.GetComponentsInChildren<LayoutElement>(true))
                {
                    if (background.name != "Background") continue;
                    background.minHeight = background.preferredHeight = Mathf.Max(background.minHeight, height);
                }
            }
            // The title's menu and minimize chips are not wired to anything: two tiny dead controls beside the close button.
            foreach (string chip in new[] { "MenuButton", "MinimizeButton" })
            {
                var t = transform.Find("Canvas/TitleGroup/" + chip);
                if (t != null) t.gameObject.SetActive(false);
            }
        }

        private void OnAllowCameraPressed()
        {
            if (m_webCamTextureManager != null) m_webCamTextureManager.RetryPermission();
        }

        private void RefreshHint()
        {
            if (_overlays == null) return;
            bool cameraMessageUp = isCamOn && CameraMessage.Show;   // the message already says what to do
            _overlays.SetHint(_isPanelOpen && !cameraMessageUp ? ScanPanelStyle.HintFor(!isCamOn, _pickedPixelPoints.Count) : string.Empty);
        }

        /// <summary>Shows a short card on the panel (a failure, or "Capture not ready").</summary>
        private void Toast(StatusTone tone, string title, string detail)
        {
            if (_overlays != null) _overlays.ShowToast(tone, title, detail, Time.unscaledTime);
        }

        /// <summary>The message the panel shows for the camera (null overlays in a hand-built test panel show nothing).</summary>
        public ScanCameraMessage CameraMessage { get; private set; }

        private void SubscribeSegments()
        {
            ScanService scan = Scan;
            if (scan == null || scan == _subscribedScan) return;
            UnsubscribeSegments();
            _subscribedScan = scan;
            _subscribedScan.SegmentCompleted += OnSegmentComplete;
        }

        private void UnsubscribeSegments()
        {
            if (_subscribedScan == null) return;
            _subscribedScan.SegmentCompleted -= OnSegmentComplete;
            _subscribedScan = null;
        }
        /// <summary>
        /// Changes the passthrough camera eye (left/right) and updates the RawImage when the new texture is ready.
        /// </summary>
        /// <param name="isLeft">If true, use the left eye camera; otherwise, use the right eye camera.</param>
        public void SetCameraEye(bool isLeft)
        {
            if (m_webCamTextureManager == null)
            {
                return;
            }
            if (!isCamOn)
            {
                return;
            }
            currentEye = isLeft ? PassthroughCameraEye.Left : PassthroughCameraEye.Right;
            m_webCamTextureManager.SetEye(isLeft);
            if (m_image != null) SetView(null);   // UpdateCameraView binds the new eye's texture once it is playing
        }

        public void OpenPanel()
        {
            PanelOpening?.Invoke(this);
            Progress.Reset();

            if (m_panel != null)
                m_panel.SetActive(true);

            ToggleBoxVisibility(false);
            isCamOn = true;
            SubscribeSegments();

            if (m_captureButton != null)
                m_captureButton.GetComponentInChildren<TMP_Text>().text = ScanPanelStyle.CaptureLabel;

            _captureSnapshot = default;
            if (_capturedTex != null)
            {
                Discard(_capturedTex);
                _capturedTex = null;
            }
            ClearPickedPoints();
            if (m_image != null)
                SetView(null);

            // The scene may have no camera provider (the v2 scene lost its prefab instance); the camera must not depend on it.
            if (m_webCamTextureManager == null && StillOverride == null && Application.isPlaying)
                m_webCamTextureManager = WebCamTextureManager.EnsureInstance();
            if (m_webCamTextureManager != null && StillOverride == null)
                m_webCamTextureManager.gameObject.SetActive(true);

            ResetZoom();
            SetCaptureUIMode(false);
            if (StillOverride != null)
            {
                // The still is the "camera view": nothing to start, and no camera permission to ask for.
                if (m_image != null) SetView(StillOverride);
                isCamOn = true;
            }
            else
            {
                SetCameraEye(currentEye == PassthroughCameraEye.Left);
            }
            _isPanelOpen = true;
            RefreshHint();
            UpdateCameraView();
        }

        public void ClosePanel()
        {
            ClearPickedPoints();
            isCamOn = false;
            if (m_image != null)
                SetView(null);
            if (m_webCamTextureManager != null)
                m_webCamTextureManager.StopCapture();

            _isPanelOpen = false;
            ToggleBoxVisibility(true);

            OnPanelClosed?.Invoke();
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);   // the Editor tests close the panel outside play mode
        }

        private void OnDestroy()
        {
            UnsubscribeSegments();
            ClearPickedPoints();
            // A panel destroyed without ClosePanel (the box deleted, the context torn down) must not leave the cameras on.
            if (_isPanelOpen && m_webCamTextureManager != null) m_webCamTextureManager.StopCapture();
            _isPanelOpen = false;

            if (_maskOverlayTex != null)
            {
                DestroyTexture(_maskOverlayTex);
                _maskOverlayTex = null;
            }
            if (_capturedTex != null)
            {
                DestroyTexture(_capturedTex);
                _capturedTex = null;
            }
        }

        /// <summary>Destroy in play, DestroyImmediate in the Editor tests (Destroy is an error there).</summary>
        private static void Discard(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        // Destroy is not allowed outside play mode (the Editor tests); the textures are freed immediately there.
        private static void DestroyTexture(Texture2D texture)
        {
            if (Application.isPlaying) Destroy(texture);
            else DestroyImmediate(texture);
        }

        public void CaptureImage()
        {
            // Start the capture process
            if (isCamOn)
            {
                if (StillOverride == null && !CameraViewIsLive())
                {
                    // Pressing Capture before the camera view is up used to flip the button into "Start New Capture" and capture nothing.
                    Toast(StatusTone.Busy, ScanPanelStyle.NotReadyTitle, ScanPanelStyle.NotReadyDetail);
                    return;
                }
                if (m_captureButton != null)
                    m_captureButton.GetComponentInChildren<TMP_Text>().text = ScanPanelStyle.NewCaptureLabel;

                bool captured = false;

                if (StillOverride != null)
                {
                    CaptureStill();
                    return;
                }

                if (m_webCamTextureManager != null)
                {
                    var orgTexture = m_webCamTextureManager.WebCamTexture;
                    if (orgTexture != null && orgTexture.isPlaying)
                    {
                        var tex = new Texture2D(orgTexture.width, orgTexture.height, TextureFormat.RGB24, false);
                        tex.SetPixels(orgTexture.GetPixels());
                        tex.Apply();

                        GetCameraInfo();

                        if (_capturedTex != null) Discard(_capturedTex);
                        _capturedTex = tex;
                        _cachedImageBytes = tex.EncodeToPNG();
                        CaptureSnapshotCameraParameters(tex.width, tex.height);
                        captured = true;
                        Progress.NoteCapture();
                    }

                    // Assign captured texture BEFORE stopping webcam so RawImage never shows destroyed texture
                    if (captured && m_image != null)
                    {
                        float uvSize   = 1.0f / _zoomLevel;
                        float uvOffset = (1.0f - uvSize) * 0.5f;
                        m_image.uvRect  = new Rect(uvOffset, uvOffset, uvSize, uvSize);
                        SetView(_capturedTex);
                    }

                    if (captured) m_webCamTextureManager.StopCapture();
                }
                else
                {
                    Debug.LogWarning("[PassthroughPanelManager] No camera available for capture.");
                    return;
                }

                if (!captured)
                {
                    // The frame could not be read: stay on the live view and say so.
                    if (m_captureButton != null) m_captureButton.GetComponentInChildren<TMP_Text>().text = ScanPanelStyle.CaptureLabel;
                    Toast(StatusTone.Busy, ScanPanelStyle.NotReadyTitle, ScanPanelStyle.NotReadyDetail);
                    return;
                }
                isCamOn = false;
                SetCaptureUIMode(true);
                RefreshHint();
                TriggerSegmentRequest();   // the drawn box is the prompt: the mask comes without a tap
            }
            else
            {
                ClearPickedPoints();
                Progress.SetTapCount(0);
                // Clear RawImage before destroying the texture it references
                if (m_image != null)
                    SetView(null);
                if (_capturedTex != null)
                {
                    Discard(_capturedTex);
                    _capturedTex = null;
                }
                _cachedImageBytes = null;
                if (m_captureButton != null)
                    m_captureButton.GetComponentInChildren<TMP_Text>().text = ScanPanelStyle.CaptureLabel;
                ResetZoom();
                SetCaptureUIMode(false);
                isCamOn = true;

                if (StillOverride != null && m_image != null)
                    SetView(StillOverride);
                else if (m_webCamTextureManager != null)
                    SetCameraEye(currentEye == PassthroughCameraEye.Left);
                RefreshHint();
            }
        }

        /// <summary>Captures <see cref="StillOverride"/> the way a camera frame is captured, without camera calls.</summary>
        private void CaptureStill()
        {
            var tex = new Texture2D(StillOverride.width, StillOverride.height, TextureFormat.RGB24, false);
            tex.SetPixels32(StillOverride.GetPixels32());
            tex.Apply();
            if (_capturedTex != null) DestroyTexture(_capturedTex);
            _capturedTex = tex;
            _cachedImageBytes = tex.EncodeToPNG();
            _captureSnapshot = new CaptureSnapshot
            {
                IsValid = true,
                Eye = currentEye,
                CameraPoseInWorld = new Pose(Vector3.zero, Quaternion.identity),
                TextureWidth = tex.width,
                TextureHeight = tex.height,
                ScaleX = 1f,
                ScaleY = 1f,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };
            if (m_image != null)
            {
                m_image.uvRect = new Rect(0f, 0f, 1f, 1f);
                SetView(_capturedTex);
            }
            isCamOn = false;
            SetCaptureUIMode(true);
            Progress.NoteCapture();
            RefreshHint();
            TriggerSegmentRequest();   // the still's box prompt, when the caller gave one
        }

        public void SendToServer()
        {
            if (_capturedTex == null || !_captureSnapshot.IsValid)
            {
                Debug.LogWarning("[PassthroughPanelManager] Capture not ready — aborting.");
                Toast(StatusTone.Busy, ScanPanelStyle.NotReadyTitle, ScanPanelStyle.NotReadyDetail);
                return;
            }

            // Spawn position from box annotation
            Vector3 spawnPosition = Vector3.zero;
            Quaternion spawnRotation = Quaternion.identity;
            Vector3 spawnScale = Vector3.one;
            if (_targetBoxAnnotation != null)
            {
                var corners = GetBoxWorldCorners(_targetBoxAnnotation);
                if (corners.Length == 8)
                {
                    spawnPosition = GetCenterFromCorners(corners);
                    spawnRotation = _targetBoxAnnotation.transform.rotation;
                    spawnScale = GetBoxWorldSizeFromCorners(corners);
                }
                else
                {
                    Debug.LogWarning("[PassthroughPanelManager] Box annotation has no 8 corners, using fallback position=zero.");
                }
            }
            else
            {
                Debug.LogWarning("[PassthroughPanelManager] No target box annotation — spawn at origin.");
            }

            if (_debugDrawPixelsOnCapturedImage)
            {
                SaveDebugProjectedImage();
            }

            ScanService scan = Scan;
            if (scan == null || scan.ActiveWorkflow == ScanWorkflow.None)
            {
                Debug.LogError("[PassthroughPanelManager] The scan service is not connected. Check the session server address in SpatialBlendSettings.");
                FailRequest(new ScanError(ScanErrorCode.NotInitialized, "The scan service is not connected."));
                return;
            }

            // Only a drawn box is an anchor. v1 sent origin and unit size when there was none.
            ScanAnchor? anchor = _targetBoxAnnotation != null && GetBoxWorldCorners(_targetBoxAnnotation).Length == 8
                ? new ScanAnchor(spawnPosition, spawnScale)
                : (ScanAnchor?)null;

            long handle;
            ScanError error;
            double now = Time.realtimeSinceStartupAsDouble;
            {
                if (_latestMaskBytes == null)
                {
                    Debug.LogError("[PassthroughPanelManager] No mask available. Add points and wait for segment.");
                    FailRequest(new ScanError(ScanErrorCode.EmptyMask, "No mask available."));
                    return;
                }
                if (!scan.TryRequestReconstruct(_cachedImageBytes, _latestMaskBytes, _latestMaskWidth, _latestMaskHeight,
                        anchor, now, out handle, out error))
                {
                    Debug.LogError($"[PassthroughPanelManager] Reconstruct request failed: {error}");
                    FailRequest(error);
                    return;
                }
            }

            DeleteTargetAnnotation();
            if (_sam3dPendingIndicatorManager != null)
            {
                Vector3 indicatorPosition = spawnPosition;
                indicatorPosition.y = 0;
                _sam3dPendingIndicatorManager.TrySpawnPendingIndicator(
                    handle, indicatorPosition, spawnRotation, spawnScale);
            }

            ClosePanel();
        }

        /// <summary>Tells the listener (the tutorial builds its own message from it); with none, the panel shows the plain-words toast itself.</summary>
        private void FailRequest(ScanError error)
        {
            if (RequestFailed != null) RequestFailed.Invoke(error);
            else Toast(StatusTone.Bad, ScanFailureText.Title, ScanFailureText.ToUserMessage(error));
        }

        private void SaveDebugProjectedImage()
        {
            var debugTex = new Texture2D(_capturedTex.width, _capturedTex.height, TextureFormat.RGB24, false);
            debugTex.SetPixels(_capturedTex.GetPixels());

            foreach (var p in _pickedPixelPoints)
                DrawDotOnTexture(debugTex, (int)p.x, (int)p.y, new Color32(0, 100, 255, 255), _debugPixelDotRadius);

            if (_targetBoxAnnotation != null)
            {
                var debugCorners = GetBoxWorldCorners(_targetBoxAnnotation);
                if (debugCorners.Length == 8)
                {
                    Vector2 cornerSum = Vector2.zero;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector2 cp = WorldToTexturePixel(debugCorners[i], _captureSnapshot);
                        DrawDotOnTexture(debugTex, (int)cp.x, (int)cp.y, new Color32(255, 0, 0, 255), _debugPixelDotRadius);
                        cornerSum += cp;
                    }
                    Vector2 centerPixel = cornerSum / 8f;
                    DrawDotOnTexture(debugTex, (int)centerPixel.x, (int)centerPixel.y, new Color32(0, 255, 0, 255), _debugPixelDotRadius * 2);
                }
            }

            debugTex.Apply(false, false);

            sampledImgCount++;
            string basePath = !string.IsNullOrEmpty(_androidDownloadPath) ? _androidDownloadPath : Application.persistentDataPath;
            string imgPath = Path.Combine(basePath, $"projected_{sampledImgCount}.png");
            try
            {
                Directory.CreateDirectory(basePath);
                File.WriteAllBytes(imgPath, debugTex.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogError($"[PassthroughPanelManager] Failed to save projected debug image: {e.Message}");
            }

            Destroy(debugTex);

            if (m_image != null)
                SetView(_capturedTex);
        }

        private void ToggleBoxVisibility(bool isOn)
        {
            if (_targetBoxAnnotation != null)
            {
                _targetBoxAnnotation.OnVisibleToggleChanged(isOn);
            }
        }

        private void DeleteTargetAnnotation()
        {
            if (_targetBoxAnnotation != null)
            {
                Destroy(_targetBoxAnnotation.gameObject);
                _targetBoxAnnotation = null;
            }
        }

        [Serializable]
        private class CameraIntrinsicsDto
        {
            public float fx;
            public float fy;
            public float cx;
            public float cy;
            public float skew;
            public int width;
            public int height;
        }

        [Serializable]
        private class CameraExtrinsicsDto
        {
            public float[] position;
            public float[] rotationQuaternion;
            public float[] matrix;
        }

        [Serializable]
        private class CameraMetaDto
        {
            public string eye;
            public string timestampUtc;
            public string imageFileName;
            public float zoom;
            public CameraIntrinsicsDto intrinsics;
            public CameraExtrinsicsDto c2w;
        }

        private void GetCameraInfo()
        {
            var cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(currentEye);
            var c2w = PassthroughCameraUtils.GetCameraPoseInWorld(currentEye);

            // Zoom is visual-only: always send original intrinsics matching the full-resolution image
            var meta = new CameraMetaDto
            {
                eye = currentEye == PassthroughCameraEye.Left ? "left" : "right",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                intrinsics = new CameraIntrinsicsDto
                {
                    fx = cameraIntrinsics.FocalLength.x,
                    fy = cameraIntrinsics.FocalLength.y,
                    cx = cameraIntrinsics.PrincipalPoint.x,
                    cy = cameraIntrinsics.PrincipalPoint.y,
                    skew = cameraIntrinsics.Skew,
                    width = cameraIntrinsics.Resolution.x,
                    height = cameraIntrinsics.Resolution.y
                },
                c2w = new CameraExtrinsicsDto
                {
                    position = new[] { c2w.position.x, c2w.position.y, c2w.position.z },
                    rotationQuaternion = new[] { c2w.rotation.x, c2w.rotation.y, c2w.rotation.z, c2w.rotation.w },
                    matrix = MatrixToArray(Matrix4x4.TRS(c2w.position, c2w.rotation, Vector3.one))
                }
            };
            var json = JsonUtility.ToJson(meta, true);
            var path = Path.Combine(Application.persistentDataPath, $"camera_{sampledImgCount}.json");
            File.WriteAllText(path, json);
        }

        private static float[] MatrixToArray(Matrix4x4 m)
        {
            return new[]
            {
                m.m00, m.m01, m.m02, m.m03,
                m.m10, m.m11, m.m12, m.m13,
                m.m20, m.m21, m.m22, m.m23,
                m.m30, m.m31, m.m32, m.m33
            };
        }

        private static void DrawDotOnTexture(Texture2D tex, int x, int y, Color32 color, int radiusPixels)
        {
            if (tex == null) return;
            if (tex.width <= 0 || tex.height <= 0) return;

            int r = Mathf.Max(1, radiusPixels);
            int x0 = Mathf.Clamp(x, 0, tex.width - 1);
            int y0 = Mathf.Clamp(y, 0, tex.height - 1);

            int minX = Mathf.Max(0, x0 - r);
            int maxX = Mathf.Min(tex.width - 1, x0 + r);
            int minY = Mathf.Max(0, y0 - r);
            int maxY = Mathf.Min(tex.height - 1, y0 + r);

            int rr = r * r;
            for (int yy = minY; yy <= maxY; yy++)
            {
                int dy = yy - y0;
                for (int xx = minX; xx <= maxX; xx++)
                {
                    int dx = xx - x0;
                    if (dx * dx + dy * dy <= rr)
                    {
                        tex.SetPixel(xx, yy, color);
                    }
                }
            }
        }

        private void CaptureSnapshotCameraParameters(int capturedTexWidth, int capturedTexHeight)
        {
            var eye = m_webCamTextureManager != null ? m_webCamTextureManager.Eye : currentEye;
            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(eye);
            var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(eye);

            _captureSnapshot = new CaptureSnapshot
            {
                IsValid = true,
                Eye = eye,
                Intrinsics = intrinsics,
                CameraPoseInWorld = cameraPose,
                TextureWidth = capturedTexWidth,
                TextureHeight = capturedTexHeight,
                ScaleX = capturedTexWidth / (float)intrinsics.Resolution.x,
                ScaleY = capturedTexHeight / (float)intrinsics.Resolution.y,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private Vector2 WorldToTexturePixel(Vector3 worldPoint, CaptureSnapshot snapshot)
        {
            var localPoint = Quaternion.Inverse(snapshot.CameraPoseInWorld.rotation) * (worldPoint - snapshot.CameraPoseInWorld.position);
            var intrinsics = snapshot.Intrinsics;

            if (localPoint.z <= 0.0001f)
            {
                Debug.LogWarning("[PassthroughPanelManager] Point too close or behind camera.");
                return Vector2.zero;
            }

            var uPixel = intrinsics.FocalLength.x * (localPoint.x / localPoint.z) + intrinsics.PrincipalPoint.x;
            var vPixel = intrinsics.FocalLength.y * (localPoint.y / localPoint.z) + intrinsics.PrincipalPoint.y;

            uPixel *= snapshot.ScaleX;
            vPixel *= snapshot.ScaleY;

            var x = Mathf.Clamp(Mathf.RoundToInt(uPixel), 0, snapshot.TextureWidth - 1);
            var y = Mathf.Clamp(Mathf.RoundToInt(vPixel), 0, snapshot.TextureHeight - 1);

            return new Vector2(x, y);
        }

        /// <summary>
        /// Returns the 8 corners of the target BoxCollider in world space.
        /// Ordering: each corner is (center +/- extents) for x,y,z (all sign combos).
        /// </summary>
        private static Vector3[] GetBoxWorldCorners(BoxAnnotationInstance instance)
        {
            var col = instance != null ? instance.GetComponent<BoxCollider>() : null;
            if (col == null)
            {
                return Array.Empty<Vector3>();
            }

            var c = col.center;
            var e = col.size * 0.5f;

            // 8 sign combinations
            Vector3[] localCorners =
            {
                c + new Vector3(-e.x, -e.y, -e.z),
                c + new Vector3( e.x, -e.y, -e.z),
                c + new Vector3( e.x, -e.y,  e.z),
                c + new Vector3(-e.x, -e.y,  e.z),
                c + new Vector3(-e.x,  e.y, -e.z),
                c + new Vector3( e.x,  e.y, -e.z),
                c + new Vector3( e.x,  e.y,  e.z),
                c + new Vector3(-e.x,  e.y,  e.z),
            };

            var t = instance.transform;
            var worldCorners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                worldCorners[i] = t.TransformPoint(localCorners[i]);
            }
            return worldCorners;
        }

        /// <summary>
        /// Returns bottom face center from 8 corners.
        /// Assumes corner ordering from GetBoxWorldCorners(), where 0..3 are bottom-face corners.
        /// </summary>
        private static Vector3 GetCenterFromCorners(Vector3[] corners)
        {
            if (corners == null || corners.Length < 8)
            {
                return Vector3.zero;
            }

            return (corners[0] + corners[1] + corners[2] + corners[3] + corners[4] + corners[5] + corners[6] + corners[7]) * 0.125f;
        }

        /// <summary>
        /// Returns world-space box size derived from corner distances.
        /// Assumes corner ordering from GetBoxWorldCorners().
        /// </summary>
        private static Vector3 GetBoxWorldSizeFromCorners(Vector3[] corners)
        {
            if (corners == null || corners.Length < 8)
            {
                return Vector3.one;
            }

            float sizeX = Vector3.Distance(corners[1], corners[0]);
            float sizeY = Vector3.Distance(corners[4], corners[0]);
            float sizeZ = Vector3.Distance(corners[3], corners[0]);
            return new Vector3(sizeX, sizeY, sizeZ);
        }

        private void OnRawImageClicked(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (isCamOn || _capturedTex == null) return;

            Vector3 worldHit = eventData.pointerCurrentRaycast.worldPosition;
            if (worldHit == Vector3.zero) return;

            Vector3 local3 = m_image.rectTransform.InverseTransformPoint(worldHit);
            Rect rect = m_image.rectTransform.rect;
            float uvX = (local3.x - rect.x) / rect.width;
            float uvY = (local3.y - rect.y) / rect.height;
            if (uvX < 0f || uvX > 1f || uvY < 0f || uvY > 1f) return;

            Rect uvRect = m_image.uvRect;
            float texU = uvRect.x + uvX * uvRect.width;
            float texV = uvRect.y + uvY * uvRect.height;
            Vector2 pixelPos = new Vector2(
                texU * _capturedTex.width,
                texV * _capturedTex.height
            );

            AddPickedPoint(pixelPos, new Vector2(uvX, uvY));
        }

        private void AddPickedPoint(Vector2 pixelPos, Vector2 normalizedInImage)
        {
            _pickedPixelPoints.Add(pixelPos);
            SpawnDotMarker(normalizedInImage);
            UpdatePointCountText();
            Progress.NoteTap(_pickedPixelPoints.Count);
            RefreshHint();
            TriggerSegmentRequest();
        }

        private void SpawnDotMarker(Vector2 normalizedInImage)
        {
            var go = new GameObject("PickedDot");
            go.transform.SetParent(m_image.transform, false);
            var img = go.AddComponent<UnityEngine.UI.Image>();
            img.raycastTarget = false;
            // An accent dot with a dark ring: readable on a bright or a dark photo, and not the real-anchor orange.
            float size = Mathf.Max(_pointDotSizePx, ScanPanelStyle.DotRadiusUnits * 2f * ScanPanelStyleScale());
            UiSprites.Apply(img, UiSprites.RoundedRect(size * 0.5f));
            img.color = ScanPanelStyle.DotRing;
            var inner = new GameObject("Core", typeof(RectTransform)).GetComponent<RectTransform>();
            inner.SetParent(go.transform, false);
            inner.anchorMin = Vector2.zero; inner.anchorMax = Vector2.one;
            inner.offsetMin = new Vector2(size * 0.16f, size * 0.16f); inner.offsetMax = new Vector2(-size * 0.16f, -size * 0.16f);
            var core = inner.gameObject.AddComponent<UnityEngine.UI.Image>();
            core.raycastTarget = false;
            UiSprites.Apply(core, UiSprites.RoundedRect(size * 0.34f));
            core.color = ScanPanelStyle.DotFill;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            Rect rect = m_image.rectTransform.rect;
            rt.anchoredPosition = new Vector2(
                rect.x + normalizedInImage.x * rect.width,
                rect.y + normalizedInImage.y * rect.height
            );
            _dotMarkers.Add(go);
        }

        /// <summary>Panel units per theme unit, so a dot is the same physical size as the control panel's.</summary>
        private float ScanPanelStyleScale() => UiTheme.MetresPerUnit / Mathf.Max(1e-5f, m_image != null ? m_image.transform.lossyScale.x : UiTheme.MetresPerUnit);

        private void UndoLastPoint()
        {
            if (_pickedPixelPoints.Count == 0) return;
            _pickedPixelPoints.RemoveAt(_pickedPixelPoints.Count - 1);
            var last = _dotMarkers[_dotMarkers.Count - 1];
            _dotMarkers.RemoveAt(_dotMarkers.Count - 1);
            Discard(last);
            UpdatePointCountText();
            Progress.SetTapCount(_pickedPixelPoints.Count);
            RefreshHint();
            TriggerSegmentRequest();   // the box alone still selects; with no box and no taps the overlay clears
        }

        private void SetCaptureUIMode(bool isCapturing)
        {
            if (m_zoomButtonsGroup != null) m_zoomButtonsGroup.SetActive(!isCapturing);
            if (m_samButtonsGroup  != null) m_samButtonsGroup.SetActive(isCapturing);

            if (m_sendToServerButton != null)
            {
                var label = m_sendToServerButton.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = ScanPanelStyle.ReconstructLabel;
            }
        }

        private void ClearPickedPoints()
        {
            _pickedPixelPoints.Clear();
            foreach (var go in _dotMarkers) if (go != null) Discard(go);
            _dotMarkers.Clear();
            UpdatePointCountText();
            ClearMaskOverlay();
        }

        private void UpdatePointCountText()
        {
            if (m_pointCountText != null)
                m_pointCountText.text = ScanPanelStyle.PointsText(_pickedPixelPoints.Count);
        }

        public void ZoomIn()
        {
            if (!isCamOn) return;
            _zoomLevel = Mathf.Min(_zoomLevel + ZoomStep, ZoomMax);
            ApplyZoom();
        }

        public void ZoomOut()
        {
            if (!isCamOn) return;
            _zoomLevel = Mathf.Max(_zoomLevel - ZoomStep, ZoomMin);
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            float uvSize   = 1.0f / _zoomLevel;
            float uvOffset = (1.0f - uvSize) * 0.5f;
            if (m_image != null)
                m_image.uvRect = new Rect(uvOffset, uvOffset, uvSize, uvSize);
            if (m_maskOverlayImage != null)
                m_maskOverlayImage.uvRect = m_image.uvRect;
            if (m_zoomLevelText != null) 
                m_zoomLevelText.text = ScanPanelStyle.ZoomText(_zoomLevel);
        }

        private void ResetZoom()
        {
            _zoomLevel = ZoomMin;
            if (m_image != null)
                m_image.uvRect = new Rect(0f, 0f, 1f, 1f);
            if (m_maskOverlayImage != null)
                m_maskOverlayImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            if (m_zoomLevelText != null)
                m_zoomLevelText.text = ScanPanelStyle.ZoomText(ZoomMin);
        }

        /// <summary>True when the camera has a playing image to show or capture (it has its first frame).</summary>
        private bool CameraViewIsLive()
        {
            var tex = m_webCamTextureManager != null ? m_webCamTextureManager.WebCamTexture : null;
            return tex != null && tex.isPlaying && tex.width > 16;
        }

        /// <summary>
        /// Binds the live camera texture to the view as soon as it is playing, and otherwise says why there is no image. This runs
        /// every frame while the live view is up, so a permission granted late (or a camera that starts slowly) shows up without
        /// reopening the panel; the old code waited once, at open, and showed nothing at all.
        /// </summary>
        private void UpdateCameraView()
        {
            if (!_isPanelOpen || StillOverride != null || !isCamOn || m_image == null) return;
            if (m_webCamTextureManager == null)
            {
                SetCameraMessage(ScanPanelStyle.Describe(ScanCameraStatus.Unsupported));
                return;
            }
            if (CameraViewIsLive())
            {
                var tex = m_webCamTextureManager.WebCamTexture;
                if (m_image.texture != tex) SetView(tex);
                SetCameraMessage(default);
                return;
            }
            SetView(null);
            SetCameraMessage(ScanPanelStyle.Describe(m_webCamTextureManager.Status));
        }

        /// <summary>Sets the view's picture. With none, the view is clear (a RawImage with no texture draws solid white, which read as a broken camera).</summary>
        private void SetView(Texture texture)
        {
            if (m_image == null) return;
            m_image.texture = texture;
            m_image.color = texture == null ? UiTheme.Palette.SurfaceDisabled : Color.white;
        }

        private void SetCameraMessage(ScanCameraMessage message)
        {
            bool changed = CameraMessage.Show != message.Show;
            CameraMessage = message;
            _overlays?.ShowMessage(message);
            if (changed) RefreshHint();
        }

        private void Update()
        {
            UpdateCameraView();
            _overlays?.Tick(Time.unscaledTime);
        }

        private void Start()
        {
            LoadSpatialBlendSettings();
        }

        private void LoadSpatialBlendSettings()
        {
            if (_settings == null)
            {
                _settings = SpatialBlendManager.CurrentSettings;
                if (_settings == null) return;
                if (!string.IsNullOrEmpty(_settings.androidDownloadPath))
                {
                    _androidDownloadPath = _settings.androidDownloadPath;
                }
            }
        }

        private void TriggerSegmentRequest()
        {
            if (_cachedImageBytes == null || _capturedTex == null) return;
            ScanService scan = Scan;
            if (scan == null || !scan.IsReady(ScanKind.Segment)) return;

            ScanPrompt prompt = BuildPrompt();
            if (!prompt.HasBox && prompt.Clicks.Count == 0) { ClearMaskOverlay(); return; }

            if (scan.TryRequestSegment(_cachedImageBytes, _capturedTex.width, _capturedTex.height, prompt,
                    Time.realtimeSinceStartupAsDouble, out long handle, out ScanError error))
            {
                _pendingSegmentEventHandle = handle;
            }
            else
            {
                Debug.LogWarning($"[PassthroughPanelManager] Segment request failed: {error}");
                ClearMaskOverlay();
            }
        }

        /// <summary>
        /// The drawn box projected into the captured photo (through the snapshot taken with it) plus the taps. Without a box in
        /// view, the taps alone.
        /// </summary>
        private ScanPrompt BuildPrompt()
        {
            ScanPrompt prompt = null;
            if (StillOverride != null)
            {
                if (StillBoxPrompt.HasValue) prompt = ScanPrompt.ForBox(StillBoxPrompt.Value, null);
            }
            else if (_targetBoxAnnotation != null && _captureSnapshot.IsValid)
            {
                var col = _targetBoxAnnotation.GetComponent<BoxCollider>();
                if (col != null)
                {
                    Transform t = _targetBoxAnnotation.transform;
                    var camera = new ScanCameraModel(_captureSnapshot.CameraPoseInWorld.position, _captureSnapshot.CameraPoseInWorld.rotation,
                        _captureSnapshot.Intrinsics.FocalLength, _captureSnapshot.Intrinsics.PrincipalPoint,
                        _captureSnapshot.Intrinsics.Resolution, _captureSnapshot.TextureWidth, _captureSnapshot.TextureHeight);
                    if (!ScanBoxProjection.TryBuildPrompt(camera, t.TransformPoint(col.center), Vector3.Scale(col.size, t.lossyScale),
                            t.rotation, out prompt, out _))
                        Debug.Log("[PassthroughPanelManager] The drawn box is not in the photo (behind the camera or out of view); tap the object instead.");
                }
            }
            if (prompt == null) prompt = new ScanPrompt();
            prompt.Clicks.AddRange(GetServerSpacePoints());
            return prompt;
        }

        private ScanPoint[] GetServerSpacePoints()
        {
            if (_pickedPixelPoints.Count == 0) return Array.Empty<ScanPoint>();

            var result = new ScanPoint[_pickedPixelPoints.Count];
            for (int i = 0; i < _pickedPixelPoints.Count; i++)
            {
                var p = _pickedPixelPoints[i];
                // Y-flip: Unity Y=0 at bottom → server Y=0 at top
                result[i] = new ScanPoint(
                    Mathf.Clamp(p.x, 0f, _capturedTex.width  - 1f),
                    Mathf.Clamp(_capturedTex.height - 1f - p.y, 0f, _capturedTex.height - 1f));
            }
            return result;
        }

        private void OnSegmentComplete(ScanSegmentResult segment)
        {
            if (segment.EventHandle != _pendingSegmentEventHandle) return; // stale request - ignore
            if (!segment.Success)
            {
                Debug.LogWarning($"[PassthroughPanelManager] Segmentation failed: {segment.Error}");
                ClearMaskOverlay();
                // An older server took the box as clicks; the service now sends the box's centre instead. Ask once more.
                if (segment.Error.Code == ScanErrorCode.BoxPromptUnsupported) TriggerSegmentRequest();
                return;
            }

            _latestMaskBytes  = segment.Mask;
            _latestMaskWidth  = segment.Width;
            _latestMaskHeight = segment.Height;
            UpdateMaskOverlay(segment.Mask, segment.Width, segment.Height);
        }

        private void UpdateMaskOverlay(byte[] maskData, int width, int height)
        {
            if (m_maskOverlayImage == null)
            {
                Debug.LogError("[PassthroughPanelManager] UpdateMaskOverlay: m_maskOverlayImage is null — assign it in the Inspector.");
                return;
            }

            if (_maskOverlayTex == null || _maskOverlayTex.width != width || _maskOverlayTex.height != height)
            {
                if (_maskOverlayTex != null) Discard(_maskOverlayTex);
                _maskOverlayTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }

            var pixels = new Color32[width * height];
            int fgCount = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // mask: Y=0 at top → Unity Texture2D: Y=0 at bottom → flip
                    int maskIdx = y * width + x;
                    int texIdx  = (height - 1 - y) * width + x;
                    bool isFg = maskData[maskIdx] > 0;
                    if (isFg) fgCount++;
                    pixels[texIdx] = isFg
                        ? ScanPanelStyle.MaskColor32      // the theme accent, translucent
                        : new Color32(0, 0, 0, 0);        // transparent
                }
            }
            _maskOverlayTex.SetPixels32(pixels);
            _maskOverlayTex.Apply();

            if (m_maskOverlayImage != null)
            {
                m_maskOverlayImage.texture = _maskOverlayTex;
                m_maskOverlayImage.uvRect  = m_image.uvRect; // sync zoom
                m_maskOverlayImage.color   = Color.white;    // ensure full alpha
                m_maskOverlayImage.gameObject.SetActive(true);
            }
        }

        private void ClearMaskOverlay()
        {
            _latestMaskBytes = null;
            if (m_maskOverlayImage != null)
            {
                m_maskOverlayImage.texture = null;
                m_maskOverlayImage.gameObject.SetActive(false);
            }
        }

        private class RawImageClickReceiver : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler
        {
            public System.Action<UnityEngine.EventSystems.PointerEventData> OnClick;
            public void OnPointerDown(UnityEngine.EventSystems.PointerEventData eventData)
                => OnClick?.Invoke(eventData);
        }
    }
}
