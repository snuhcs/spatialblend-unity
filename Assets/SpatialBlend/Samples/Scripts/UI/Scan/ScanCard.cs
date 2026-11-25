// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>The capture card's words (scan UI Concept A, stage 1).</summary>
    public static class ScanCardText
    {
        public const string AimTitle = "Capture this object";
        public const string AimHint = "Press Capture, or pull the trigger.";
        public const string LiveCaption = "Live view";
        public const string Capture = "Capture", Cancel = "Cancel", Close = "Close", Hide = "Hide";
        public const string SelectTitle = "Point at your object";
        public const string SelectDetail = "Pull the trigger, or pinch, on the photo to add a point. Add more until the highlight covers the object.";
        public const string RemoveDetail = "Remove is on: a point now marks what is not your object.";
        public const string Refining = "Finding your object…";
        public const string TryAnotherPoint = "Try another point, or Undo.";
        public const string Confirm = "Confirm", Retake = "Retake";
        public const string AddMode = "+ Add", RemoveMode = "− Remove", UndoPoint = "Undo", Clear = "Clear";
        public const string SelectedTitle = "Object selected";
        public const string ThumbCaption = "What the camera saw";
        public const string Back = "Back", ThreeDCopy = "3D copy";
        public const string BuildingTitle = "Building 3D model";
        public const string BuildingDetail = "About 35 s. You can keep working.";
        public const string DoneTitle = "Your 3D copy is ready";
        public const string DoneInPlace = "It's where your box was.";
        public const string Done = "Done";
    }

    /// <summary>Whether the next point on the photo marks the object (positive) or what is not it (negative).</summary>
    public enum ScanPointMode { Add, Remove }

    /// <summary>
    /// Scan UI Concept A, stage 1: a card above the box the user drew. While aiming it shows the live view of the camera at a
    /// fixed crop, as the photo will be (a preview in place of readiness checks) and one Capture button (the
    /// trigger, or a pinch, also captures). Capture freezes that frame large on the card; the user points at the object on it, each
    /// trigger or pinch adding a point (positive, or negative with Remove), and each point asks the segmenter for the mask of all of
    /// them (the points alone, never the box), shown over the photo as it refines; Undo and Clear take points back, and Confirm
    /// commits the mask. The model is built only when the user then presses 3D copy (no timer); a ring around the
    /// box's base fills while the server works; the copy is installed exactly in the drawn box. A reticle follows the ray on the photo. Errors
    /// and the camera permission are shown in the same card. The flow itself is <see cref="ScanFlow"/>; this is its view.
    /// Built in code on the <see cref="UiMetrics.ScanCard"/> surface (one unit is 4/3 dmm at its 1.5 m reading distance, 2 mm per unit:
    /// twice its earlier 1 mm-per-unit size) from the UI theme's tokens: the one panel style, the one button style, the type scale, with the
    /// Interaction SDK ray buttons the mode-switch widgets use.
    /// </summary>
    public sealed class ScanCard : MonoBehaviour
    {
        public const float Width = 460f;
        public const float Pad = UiTheme.PanelPadding;
        public const float ButtonHeight = UiTheme.ButtonHeight;
        public const float ToolHeight = UiTheme.ToolHeight;
        public const float LiftMetres = 0.06f;
        /// <summary>The header's Close button (units): always there, so every state of the card has a way out.</summary>
        public const float CloseButtonWidth = 120f;
        /// <summary>
        /// Metres per canvas unit at the reading distance (<see cref="UiMetrics.ScanCard"/>, 2 mm: the capture UI at twice
        /// its earlier 1 mm-per-unit size). Text, buttons, the photo and their ray targets all scale with it.
        /// </summary>
        public static float MetresPerUnit => UiMetrics.ScanCard.MetresPerUnit;
        public static float ReadingDistanceMetres => UiMetrics.ScanCard.ReadingMetres;
        public const float DoneCloseSeconds = 6f;
        /// <summary>The server's typical reconstruction time (plan doc: about 35 s); the ring fills towards it.</summary>
        public const float ExpectedBuildSeconds = 35f;
        /// <summary>The photo's height on the card (its width is the card's inner width): large, for pointing at the object.</summary>
        public const float ThumbHeightUnits = 300f;
        /// <summary>How often the live view refreshes (8 Hz). The texture itself is the camera's own, never copied.</summary>
        public const float PreviewIntervalSeconds = 0.125f;
        /// <summary>The reticle's diameter on the photo (units): about 7 cm at the card's size, readable at arm's length.</summary>
        public const float ReticleUnits = 36f;
        /// <summary>A placed point's diameter on the photo (units).</summary>
        public const float PointDotUnits = 20f;

        /// <summary>Raised when a card opens, before it starts the camera: a listener may set <see cref="FrameSource"/> (the tutorial does).</summary>
        public static event Action<ScanCard> Opening;

        /// <summary>
        /// The latest card. Opening another replaces it; an older card already building keeps its ring and closes itself when done.
        /// </summary>
        public static ScanCard Current { get; private set; }
        public static bool AnyOpen => Current != null;

        /// <summary>
        /// Whether a card is capturing (aiming, choosing points, confirming, or showing a failure): the user's trigger and gaze belong
        /// to it. While the model is built the user is free ("You can keep working"), so this is false then.
        /// </summary>
        public static bool AnyCapturing => Current != null && Current.IsCapturing;

        public bool IsCapturing => IsOpen && _flow != null && (_flow.State == ScanFlowState.Aiming ||
            _flow.State == ScanFlowState.Selecting || _flow.State == ScanFlowState.Confirming || _flow.State == ScanFlowState.Failed);

        /// <summary>Raised once when the card closes.</summary>
        public event Action Closed;
        /// <summary>Raised when the flow fails (no service, a failed model) or a refinement fails (the points stay); the argument is the reason.</summary>
        public event Action<ScanError> RequestFailed;

        /// <summary>Captures since the card opened (the tutorial reads it).</summary>
        public ScanPanelProgress Progress { get; } = new ScanPanelProgress();
        /// <summary>The camera; null (the default) is the passthrough camera. Set it from <see cref="Opening"/>.</summary>
        public IScanFrameSource FrameSource { get; set; }
        /// <summary>The scan service the card uses; null is the app's (<see cref="SessionManager.Instance"/>). The tutorial gives its own.</summary>
        public Func<ScanService> ServiceSource { get; set; }
        /// <summary>
        /// No longer used: every Segment request carries the user's points alone, and nothing is asked before the first point.
        /// Kept only so the tutorial, which still sets it, compiles unchanged.
        /// </summary>
        public bool PromptWithBox { get; set; } = true;
        public ScanFlow Flow => _flow;
        public bool IsOpen { get; private set; }
        public ScanAnchor Anchor => _anchor;
        public ScanAnchor ScannedBox => new ScanAnchor(_center, _size, _rotation);
        public Quaternion BoxRotation => _rotation;
        /// <summary>The user's eye the card turns to (null when none was found).</summary>
        public Transform Eye => _eye;
        /// <summary>What the next point marks.</summary>
        public ScanPointMode PointMode { get; private set; } = ScanPointMode.Add;

        // what the user sees, for tests and renders
        public string TitleText => _title != null ? _title.text : string.Empty;
        public string DetailText => _detail != null && _detail.gameObject.activeSelf ? _detail.text : string.Empty;
        public string PrimaryLabel => _primary != null && _primary.gameObject.activeSelf ? _primaryLabel.text : null;
        public string SecondaryLabel => _secondary != null && _secondary.gameObject.activeSelf ? _secondaryLabel.text : null;
        /// <summary>Whether the primary button does something now (Capture or Confirm are shown dimmed when they do not).</summary>
        public bool PrimaryEnabled => _primary != null && _primary.gameObject.activeSelf && _primaryEnabled;
        public bool ThumbVisible => _thumb != null && _thumb.gameObject.activeSelf;
        public bool MaskVisible => _maskImage != null && _maskImage.gameObject.activeSelf && _maskImage.texture != null;
        public bool ToolsVisible => _tools != null && _tools.gameObject.activeSelf;
        public bool RingVisible => _ring != null && _ring.activeSelf;
        public float RingFill => _ringFill != null ? _ringFill.fillAmount : 0f;
        public RectTransform CardRect => _card;
        public Texture2D MaskTexture => _maskTex;
        public Rect ThumbUv => _photoImage != null ? _photoImage.uvRect : default;
        public GameObject RingObject => _ring;
        /// <summary>The live view is up (aiming): the thumbnail shows the camera's own texture, not a captured photo.</summary>
        public bool PreviewVisible => ThumbVisible && _previewing;
        /// <summary>The texture the thumbnail shows now (the camera's while aiming, the frozen photo after Capture).</summary>
        public Texture ThumbTexture => _photoImage != null ? _photoImage.texture : null;
        /// <summary>How many times the live view's crop was refreshed (the tests read it).</summary>
        public int PreviewRefreshes { get; private set; }

        /// <summary>
        /// The reticle that follows the ray on the photo while points are chosen: a white ring with a dot in the colour of the next point.
        /// Opaque (alpha 1), the last thing drawn on the card. Saturated colours that read on any photo (allow-listed in the UI colour guard).
        /// </summary>
        public static readonly Color ReticleRing = new Color(1f, 1f, 1f, 1f);
        public static readonly Color PositivePoint = new Color(0.16f, 0.82f, 0.33f, 1f);
        public static readonly Color NegativePoint = new Color(0.92f, 0.12f, 0.12f, 1f);
        /// <summary>How far off the photo's plane a ray hit may be and still count (the tap collider is 6 units thick).</summary>
        public const float PhotoHitDepth = 8f;
        public bool ReticleVisible => _reticle != null && _reticle.gameObject.activeInHierarchy;
        public Vector2 ReticleLocal => _reticle != null ? _reticle.anchoredPosition : Vector2.zero;
        public Color ReticleDotTint => _reticleDot != null ? _reticleDot.color : default;
        /// <summary>The reticle's root, for the tests: it must be drawn last and opaque.</summary>
        public RectTransform ReticleRect => _reticle;
        /// <summary>The point dots drawn on the photo, in order (green positive, red negative); each sits in a white ring.</summary>
        public IReadOnlyList<Image> PointDots => _dots;
        /// <summary>How many point dots are shown.</summary>
        public int VisiblePointDots { get { int n = 0; foreach (Image d in _dots) if (d.transform.parent.gameObject.activeSelf) n++; return n; } }

        private Vector3 _center, _size;
        private Quaternion _rotation;
        private Transform _eye;
        private BoxAnnotationInstance _annotation;
        private ScanAnchor _anchor;
        private ScanFlow _flow;
        private Texture2D _photo, _maskTex;
        private Color32[] _maskPixels;
        private ModeSwitchClickTarget _photoTap;
        private float _buildStart, _doneAt, _nextPreview;
        private bool _boxHidden, _previewing, _primaryEnabled = true, _hidden;
        private TextMeshProUGUI _closeLabel;
        private bool _triggerWasDown, _pinchWasDown;
        private ScanCameraMessage _cameraMessage;
        private Action _onPrimary, _onSecondary;
        private readonly List<Image> _dots = new List<Image>();

        // view
        private RectTransform _card;
        private TextMeshProUGUI _title, _detail, _caption;
        private RectTransform _thumb, _overlay, _reticle, _buttons, _tools;
        private Image _reticleDot;
        private RawImage _photoImage, _maskImage;
        private ModeSwitchClickTarget _primary, _secondary, _modeButton, _undoButton, _clearButton, _closeButton;
        private TextMeshProUGUI _primaryLabel, _secondaryLabel, _modeLabel;
        private GameObject _ring;
        private Image _ringFill;

        // ------------------------------------------------------------------ open and close

        /// <summary>Opens the card on a drawn box (its collider's centre, size and rotation in the world).</summary>
        public static ScanCard Open(BoxAnnotationInstance box, Transform eye)
        {
            var collider = box != null ? box.GetComponent<BoxCollider>() : null;
            if (collider == null) return null;
            Transform t = box.transform;
            return Open(t.TransformPoint(collider.center), Vector3.Scale(collider.size, t.lossyScale), t.rotation, eye, box);
        }

        public static ScanCard Open(Vector3 center, Vector3 size, Quaternion rotation, Transform eye, BoxAnnotationInstance annotation = null)
        {
            // A card still capturing gives way; one already building keeps its ring and closes itself when its model is done.
            if (Current != null && Current.IsCapturing) Current.Close();
            var card = new GameObject("ScanCard").AddComponent<ScanCard>();
            card.Init(center, size, rotation, eye, annotation);
            return card;
        }

        private void Init(Vector3 center, Vector3 size, Quaternion rotation, Transform eye, BoxAnnotationInstance annotation)
        {
            _center = center;
            _size = size;
            _rotation = rotation;
            _eye = eye;
            _annotation = annotation;
            Current = this;
            IsOpen = true;
            Opening?.Invoke(this);
            if (FrameSource == null) FrameSource = new PassthroughFrameSource();
            BuildView();
            BuildRing();
            SetLayer(transform, UiLayer);
            SetLayer(_ring.transform, UiLayer);
            // The scan stands for the real object: its copy is installed exactly in the box the user drew, turned as drawn (a scan
            // is a REAL object, so it is not snapped; the copy does not go beside it).
            _anchor = new ScanAnchor(center, size, rotation);
            _flow = new ScanFlow(() => ServiceSource != null ? ServiceSource()
                : SessionManager.Instance != null ? SessionManager.Instance.Service : null);
            _flow.Changed += OnFlowChanged;
            _flow.SelectionChanged += OnSelectionChanged;
            _flow.RefineFailed += OnRefineFailed;
            BoxAnnotationManager.HoldCreation(this);
            FrameSource.Start();
            _flow.Begin(_anchor);
            Place();
        }

        /// <summary>Closes the card. A reconstruction already sent keeps going and its copy still appears.</summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (Current == this) Current = null;
            BoxAnnotationManager.ReleaseCreation(this);
            if (_flow != null)
            {
                _flow.Changed -= OnFlowChanged;
                _flow.SelectionChanged -= OnSelectionChanged;
                _flow.RefineFailed -= OnRefineFailed;
                if (_flow.State != ScanFlowState.Building) _flow.Close();
                _flow.Dispose();
            }
            FrameSource?.Stop();
            ShowBox(true);
            Discard(_ring);
            // Edit mode never runs OnDestroy for this behaviour: free the textures here too.
            Discard(_photo);
            Discard(_maskTex);
            _photo = _maskTex = null;
            Closed?.Invoke();
            Discard(gameObject);
        }

        private void OnDestroy()
        {
            if (IsOpen)
            {
                IsOpen = false;
                if (Current == this) Current = null;
                BoxAnnotationManager.ReleaseCreation(this);
                _flow?.Dispose();
                FrameSource?.Stop();
            }
            Discard(_photo);
            Discard(_maskTex);
            Discard(_ring);
        }

        private static void Discard(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        // ------------------------------------------------------------------ input

        /// <summary>The card's main button, as a ray click on it does (tests and renders press it this way).</summary>
        public void PressPrimary() { if (_primary.gameObject.activeSelf) _onPrimary?.Invoke(); }

        /// <summary>
        /// The header's button, shown in every state (choosing points and confirming need a way out too). It
        /// reads Close and is <see cref="Close"/>: a pending segment request is dropped, the camera stops and box drawing is
        /// released. While the model is built it reads Hide: the card goes, the ring on the box's base stays as the pending
        /// indicator, and the copy is still installed when done (the footer Cancel is the way to stop the build).
        /// </summary>
        public void PressClose() { if (CloseVisible) OnHeaderButton(); }

        /// <summary>Whether the header's Close (or Hide) button is shown.</summary>
        public bool CloseVisible => _closeButton != null && _closeButton.gameObject.activeInHierarchy;

        /// <summary>The header button's label: Close, or Hide while the model is built.</summary>
        public string CloseLabel => _closeLabel != null ? _closeLabel.text : null;

        /// <summary>Whether the card was hidden while its model is built (its ring still shows the build).</summary>
        public bool IsHidden => _hidden;

        /// <summary>Whether the card's panel is shown.</summary>
        public bool CardVisible => _card != null && _card.gameObject.activeSelf;

        private void OnHeaderButton()
        {
            if (_flow != null && _flow.State == ScanFlowState.Building) Hide();
            else Close();
        }

        private void Hide()
        {
            _hidden = true;
            _card.gameObject.SetActive(false);
        }

        private void Unhide()
        {
            if (!_hidden) return;
            _hidden = false;
            _card.gameObject.SetActive(true);
        }

        /// <summary>The card's second button.</summary>
        public void PressSecondary() { if (_secondary.gameObject.activeSelf) _onSecondary?.Invoke(); }

        /// <summary>Whether Capture does anything now (the button, the trigger or a pinch). There is no room rule.</summary>
        public bool CaptureEnabled => IsOpen && _flow != null && (_flow.State == ScanFlowState.Aiming || _flow.State == ScanFlowState.Failed);

        /// <summary>Freezes the camera's frame and holds it for the user's points (the Capture button, the trigger or a pinch).</summary>
        public bool Capture()
        {
            if (!CaptureEnabled) return false;
            if (FrameSource.Status != ScanCameraStatus.Live) return false;
            ReadBox();
            if (!FrameSource.TryCapture(out byte[] photo, out int width, out int height, out _, out Texture2D texture))
                return false;
            // No "box in view" gate, and no box prompt. The photo is held whatever the box projects to; the user
            // picks the object with points, and segmentation starts at the first point.
            Discard(_photo);
            _photo = texture;
            Progress.NoteCapture();
            PointMode = ScanPointMode.Add;
            return _flow.Capture(photo, width, height);
        }

        /// <summary>Adds a point at a server pixel of the photo, in the current <see cref="PointMode"/>. The tests and the tutorial call it.</summary>
        public bool AddPoint(ScanPoint point)
        {
            if (_flow == null || _flow.State != ScanFlowState.Selecting) return false;
            var labelled = new ScanPoint(point.X, point.Y, PointMode == ScanPointMode.Remove ? ScanPointLabel.Negative : ScanPointLabel.Positive);
            return _flow.AddPoint(labelled, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>Adds a point where <paramref name="ray"/> meets the photo. False when it does not meet it.</summary>
        public bool AddPointAlong(Ray ray)
        {
            if (!TryRayOnPhoto(ray, out PhotoCanvasHit hit)) return false;
            return AddPoint(hit.ServerPoint);
        }

        /// <summary>Switches what the next point marks (the +/− button).</summary>
        public void ToggleMode()
        {
            PointMode = PointMode == ScanPointMode.Add ? ScanPointMode.Remove : ScanPointMode.Add;
            ShowSelecting();
        }

        public bool UndoPoint() => _flow != null && _flow.UndoPoint(Time.realtimeSinceStartupAsDouble);
        public bool ClearPoints() => _flow != null && _flow.ClearPoints(Time.realtimeSinceStartupAsDouble);
        /// <summary>Commits the mask shown (the Confirm button). False while it is not ready.</summary>
        public bool Confirm() => _flow != null && _flow.Confirm();

        private void PollCaptureInput()
        {
            // Only the latest card listens: an older one still building must never capture on the same press.
            if (!Application.isPlaying || Current != this || _flow.State != ScanFlowState.Aiming || FrameSource.Status != ScanCameraStatus.Live) return;
            bool trigger = false, pinch = false;
            try
            {
                if (OVRManager.instance != null)
                {
                    trigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                              || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
                    pinch = AnyIndexPinch();
                }
            }
            catch (Exception) { /* no OVR runtime (the Editor) */ }
            bool pressed = (trigger && !_triggerWasDown) || (pinch && !_pinchWasDown);
            _triggerWasDown = trigger;
            _pinchWasDown = pinch;
            // The ray on a button or an object keeps its own meaning; only a press into empty space captures. Every ray counts:
            // the controller's and each hand's (a pinch on Cancel must not capture first).
            if (pressed && !AnyRayOnSomething()) Capture();
        }

        private OVRHand[] _hands;
        private Oculus.Interaction.RayInteractor[] _rays;

        private float _nextLookup;

        /// <summary>Hands and rays are looked up again every second (a controller-to-hands switch may create them late).</summary>
        private void RefreshInputLookups()
        {
            if (Time.unscaledTime < _nextLookup && _hands != null && _rays != null) return;
            _nextLookup = Time.unscaledTime + 1f;
            _hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            _rays = FindObjectsByType<Oculus.Interaction.RayInteractor>(FindObjectsSortMode.None);
        }

        private bool AnyIndexPinch()
        {
            RefreshInputLookups();
            foreach (var hand in _hands)
                if (hand != null && hand.IsTracked && hand.GetFingerIsPinching(OVRHand.HandFinger.Index)) return true;
            return false;
        }

        private bool AnyRayOnSomething()
        {
            RefreshInputLookups();
            foreach (var ray in _rays)
                if (ray != null && ray.isActiveAndEnabled && ray.HasCandidate) return true;
            var main = InteractionModeManager.Interactor;
            return main != null && main.HasCandidate;
        }

        /// <summary>
        /// The ray the reticle follows: the editing input's own ray (<see cref="EditInput.ActiveRay"/>) when it
        /// is on the photo, else any other ray of the rig on the photo (the left controller, the other hand), else none. The rays
        /// are line-plane crossings of the photo, so no hit reported by the SDK is needed.
        /// </summary>
        private Ray? PhotoRay()
        {
            Ray? active = EditInput.ActiveRay;
            if (active.HasValue && TryRayOnPhoto(active.Value, out _)) return active;
            if (!Application.isPlaying) return null;
            RefreshInputLookups();
            _otherRays.Clear();
            foreach (var ray in _rays)
                if (ray != null && ray.isActiveAndEnabled) _otherRays.Add(ray.Ray);
            return PickPhotoRay(null, _otherRays);
        }

        private readonly List<Ray> _otherRays = new List<Ray>();

        /// <summary>The first of <paramref name="active"/>, then <paramref name="others"/> in order, that crosses the photo; null when none does.</summary>
        public Ray? PickPhotoRay(Ray? active, IReadOnlyList<Ray> others)
        {
            if (active.HasValue && TryRayOnPhoto(active.Value, out _)) return active;
            if (others != null)
                foreach (Ray ray in others)
                    if (TryRayOnPhoto(ray, out _)) return ray;
            return null;
        }

        /// <summary>The ray that pressed the photo: the interactor the SDK says is selecting it, else the reticle's ray.</summary>
        private Ray? PressingRay()
        {
            var photo = _photoTap != null ? _photoTap.Interactable : null;
            if (photo != null)
                foreach (var interactor in photo.SelectingInteractors)
                    if (interactor != null) return interactor.Ray;
            return PhotoRay();
        }

        private ScanPoint? _pressPoint;

        /// <summary>
        /// The press on the photo: the point is taken where the pressing ray is NOW, not where it is at the release,
        /// so a pinch that drifts as it opens does not move the point. False when the ray is off the photo.
        /// </summary>
        public bool BeginPhotoPress(Ray? ray)
        {
            _pressPoint = null;
            if (!ray.HasValue || !TryRayOnPhoto(ray.Value, out PhotoCanvasHit hit)) return false;
            _pressPoint = hit.ServerPoint;
            return true;
        }

        /// <summary>The release of that press: the point taken at the press is added.</summary>
        public bool EndPhotoPress()
        {
            if (!_pressPoint.HasValue) return false;
            ScanPoint point = _pressPoint.Value;
            _pressPoint = null;
            return AddPoint(point);
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            if (!IsOpen) return;
            float now = Time.unscaledTime;
            FrameSource.Tick(now);
            Refresh(now, Time.unscaledDeltaTime);
            if (_flow.State == ScanFlowState.Selecting) UpdateReticle(PhotoRay());
            PollCaptureInput();
            Place();
        }

        /// <summary>One frame of the view (public so the Editor tests and renders can drive it).</summary>
        public void Refresh(float now, float deltaSeconds)
        {
            if (!IsOpen) return;
            switch (_flow.State)
            {
                case ScanFlowState.Aiming:
                    RefreshAiming(now, deltaSeconds);
                    break;
                case ScanFlowState.Building:
                    float t = now - _buildStart;
                    if (_ringFill != null) _ringFill.fillAmount = Mathf.Min(0.95f, 1f - Mathf.Exp(-t / ExpectedBuildSeconds * 1.5f));
                    break;
                case ScanFlowState.Done:
                    if (now - _doneAt > DoneCloseSeconds) Close();
                    break;
            }
        }

        /// <summary>Re-reads the drawn box: the user may move it while aiming.</summary>
        private void ReadBox()
        {
            if (_annotation == null) return;
            var collider = _annotation.GetComponent<BoxCollider>();
            if (collider == null) return;
            Transform t = _annotation.transform;
            _center = t.TransformPoint(collider.center);
            _size = Vector3.Scale(collider.size, t.lossyScale);
            _rotation = t.rotation;
        }

        private void RefreshAiming(float now, float deltaSeconds)
        {
            ScanCameraStatus status = FrameSource.Status;
            if (status != ScanCameraStatus.Live)
            {
                ScanCameraMessage message = ScanPanelStyle.Describe(status);
                if (!message.Show) message = ScanPanelStyle.Describe(ScanCameraStatus.Starting);
                if (message.Title != _cameraMessage.Title || message.OffersAllow != _cameraMessage.OffersAllow)
                {
                    _cameraMessage = message;
                    ShowCameraMessage(message);
                }
                return;
            }
            if (_cameraMessage.Show)
            {
                _cameraMessage = default;
                ShowAiming();
            }
            ReadBox();
            // No readiness or "in view" checks: the live view shows what the photo will hold.
            if (now >= _nextPreview || !_previewing)
            {
                _nextPreview = now + PreviewIntervalSeconds;
                RefreshPreview();
            }
        }

        /// <summary>
        /// The live view: the camera's own texture (never copied) on the thumbnail, at the fixed crop the photo will have.
        /// Called a few times a second; it allocates nothing.
        /// </summary>
        public void RefreshPreview()
        {
            if (!FrameSource.TryGetPreview(out Texture texture, out _) || texture == null)
            {
                if (_previewing) { _previewing = false; _thumb.gameObject.SetActive(false); Layout(); }
                return;
            }
            _photoImage.texture = texture;
            Rect uv = Crop(texture.width, texture.height);
            _photoImage.uvRect = uv;
            PreviewRefreshes++;
            if (!_previewing)
            {
                _previewing = true;
                _caption.text = ScanCardText.LiveCaption;
                _thumb.gameObject.SetActive(true);
                _maskImage.gameObject.SetActive(false);
                Layout();
            }
        }

        // ------------------------------------------------------------------ states

        private void OnFlowChanged(ScanFlowState state)
        {
            switch (state)
            {
                case ScanFlowState.Aiming:
                    ShowBox(true);
                    _cameraMessage = default;
                    BoxAnnotationManager.HoldCreation(this);
                    ShowAiming();
                    break;
                case ScanFlowState.Selecting:
                    _previewing = false;
                    ShowSelecting();
                    break;
                case ScanFlowState.Confirming:
                    Show(ScanCardText.SelectedTitle, null, UiTheme.Palette.Text, thumb: true, mask: true, tools: false);
                    SetButtons(ScanCardText.ThreeDCopy, () => _flow.Build(Time.realtimeSinceStartupAsDouble), ScanCardText.Back, () => _flow.Back());
                    break;
                case ScanFlowState.Building:
                    // The user is free while the model is built: new boxes may be drawn again.
                    BoxAnnotationManager.ReleaseCreation(this);
                    _buildStart = Time.unscaledTime;
                    DeleteAnnotation();
                    _ring.SetActive(true);
                    _ringFill.fillAmount = 0f;
                    Show(ScanCardText.BuildingTitle, ScanCardText.BuildingDetail, UiTheme.Palette.Text, thumb: false, mask: false, tools: false);
                    SetButtons(null, null, ScanCardText.Cancel, () => { _flow.CancelBuild(); Close(); });
                    _closeLabel.text = ScanCardText.Hide;
                    break;
                case ScanFlowState.Done:
                    _closeLabel.text = ScanCardText.Close;
                    // A hidden card's copy is already in place: nothing to say, the card just goes.
                    if (_hidden) { Close(); break; }
                    _doneAt = Time.unscaledTime;
                    _ringFill.fillAmount = 1f;
                    Show(ScanCardText.DoneTitle, ScanCardText.DoneInPlace,
                        UiTheme.Palette.Success, thumb: false, mask: false, tools: false);
                    SetButtons(ScanCardText.Done, Close, null, null);
                    break;
                case ScanFlowState.Failed:
                    _closeLabel.text = ScanCardText.Close;
                    Unhide();   // a build that failed behind a hidden card says so
                    _ring.SetActive(false);
                    Show(ScanFailureText.Title, ScanFailureText.ToUserMessage(_flow.Error), UiTheme.Palette.Warning,
                        thumb: false, mask: false, tools: false);
                    if (Current == this)
                    {
                        BoxAnnotationManager.HoldCreation(this);
                        SetButtons(ScanCardText.Retake, () => _flow.Retake(), null, null);
                    }
                    else SetButtons(null, null, null, null);   // an older card that failed while building: no second capture; the header closes
                    RequestFailed?.Invoke(_flow.Error);
                    break;
                case ScanFlowState.Idle:
                    _ring.SetActive(false);
                    // A hidden card's build ended from outside (the service disposed, the tutorial's backend cancelled it): with
                    // nothing left to show, the hidden card closes instead of lingering invisible.
                    if (_hidden) Close();
                    break;
            }
        }

        // A failed refinement keeps the points: the card says why, and the app hears it (the tutorial falls back
        // from a failing real server to its practice one on it).
        private void OnRefineFailed(ScanError error) => RequestFailed?.Invoke(error);

        private void OnSelectionChanged()
        {
            if (_flow.State != ScanFlowState.Selecting && _flow.State != ScanFlowState.Confirming) return;
            if (_flow.Mask != null) BuildMaskTexture();
            else if (_maskImage != null) _maskImage.texture = null;
            if (_flow.State == ScanFlowState.Selecting) ShowSelecting();
        }

        private void ShowSelecting()
        {
            if (_flow == null || _flow.State != ScanFlowState.Selecting) return;
            string detail = _flow.Refining ? ScanCardText.Refining
                : _flow.RefineError.IsError ? ScanFailureText.ToUserMessage(_flow.RefineError) + " " + ScanCardText.TryAnotherPoint
                : PointMode == ScanPointMode.Remove ? ScanCardText.RemoveDetail : ScanCardText.SelectDetail;
            Show(ScanCardText.SelectTitle, detail, UiTheme.Palette.Text, thumb: true, mask: _flow.Mask != null, tools: true);
            SetButtons(ScanCardText.Confirm, () => Confirm(), ScanCardText.Retake, () => _flow.Retake());
            SetPrimaryEnabled(_flow.CanConfirm);
            _modeLabel.text = PointMode == ScanPointMode.Add ? ScanCardText.AddMode : ScanCardText.RemoveMode;
            SyncDots();
        }

        private void ShowAiming()
        {
            Show(ScanCardText.AimTitle, ScanCardText.AimHint, UiTheme.Palette.Text, thumb: false, mask: false, tools: false);
            SetButtons(ScanCardText.Capture, () => Capture(), null, null);   // the header's Close is the way out
            SetPrimaryEnabled(true);
            _previewing = false;
            RefreshPreview();
        }

        private void ShowCameraMessage(ScanCameraMessage message)
        {
            _previewing = false;
            Show(message.Title, message.Detail, UiTheme.ToneColor(message.Tone), thumb: false, mask: false, tools: false);
            if (message.OffersAllow) SetButtons(ScanPanelStyle.AllowCameraLabel, () => FrameSource.RetryPermission(), null, null);
            else SetButtons(null, null, null, null);   // the header's Close is the way out
            SetPrimaryEnabled(true);
        }

        private void SetPrimaryEnabled(bool enabled)
        {
            _primaryEnabled = enabled;
            var c = UiTheme.Button(true);
            if (enabled) _primary.SetColors(c.Normal, c.Hover, c.Pressed);
            else _primary.SetColors(c.Disabled, c.Disabled, c.Disabled);
            _primaryLabel.color = enabled ? c.Label : c.LabelDisabled;
        }

        // ------------------------------------------------------------------ the photo: reticle and points

        /// <summary>Where a ray hit sits on the photo: the marker's local position, the canvas UV, and the server pixel.</summary>
        public readonly struct PhotoCanvasHit
        {
            public readonly Vector2 MarkerLocal;
            public readonly Vector2 CanvasUv;
            public readonly ScanPoint ServerPoint;

            public PhotoCanvasHit(Vector2 markerLocal, Vector2 canvasUv, ScanPoint serverPoint)
            {
                MarkerLocal = markerLocal;
                CanvasUv = canvasUv;
                ServerPoint = serverPoint;
            }
        }

        /// <summary>
        /// A ray hit in the photo's local space (<c>RectTransform.InverseTransformPoint</c>) maps to the marker's
        /// position and to server pixels. Canvas UV is 0–1 across the photo rect, origin at the bottom left. Server
        /// pixels follow the displayed crop (<paramref name="uvRect"/>), y down from the top of the texture. A hit
        /// outside the rect, or further than <see cref="PhotoHitDepth"/> off its plane, is not on the photo.
        /// </summary>
        public static bool TryMapPhotoHit(Vector3 local, Rect rect, Rect uvRect, int width, int height, out PhotoCanvasHit hit)
        {
            hit = default;
            if (width <= 0 || height <= 0 || rect.width <= 0f || rect.height <= 0f) return false;
            if (Mathf.Abs(local.z) > PhotoHitDepth) return false;
            if (local.x < rect.xMin || local.x > rect.xMax || local.y < rect.yMin || local.y > rect.yMax) return false;
            float u = (local.x - rect.xMin) / rect.width;
            float v = (local.y - rect.yMin) / rect.height;
            float texU = uvRect.x + u * uvRect.width;
            float texV = uvRect.y + v * uvRect.height;
            hit = new PhotoCanvasHit(new Vector2(local.x, local.y), new Vector2(u, v), new ScanPoint(texU * width, (1f - texV) * height));
            return true;
        }

        /// <summary>Where <paramref name="ray"/> crosses the photo's plane, mapped onto the photo. False off the photo or with no photo.</summary>
        public bool TryRayOnPhoto(Ray ray, out PhotoCanvasHit hit)
        {
            hit = default;
            if (_flow == null || _flow.State != ScanFlowState.Selecting || _photo == null || _photoImage == null) return false;
            RectTransform rt = _photoImage.rectTransform;
            var plane = new Plane(rt.forward, rt.position);
            if (!plane.Raycast(ray, out float distance) || distance <= 0f) return false;
            Vector3 local = rt.InverseTransformPoint(ray.GetPoint(distance));
            local.z = 0f;
            return TryMapPhotoHit(local, rt.rect, _photoImage.uvRect, _photo.width, _photo.height, out hit);
        }

        /// <summary>Puts the reticle where <paramref name="ray"/> meets the photo while points are chosen, and hides it otherwise.</summary>
        public bool UpdateReticle(Ray? ray)
        {
            if (_reticle == null) return false;
            bool show = ray.HasValue && TryRayOnPhoto(ray.Value, out PhotoCanvasHit hit) && SetReticle(hit);
            if (!show && _reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(false);
            return show;
        }

        private bool SetReticle(PhotoCanvasHit hit)
        {
            if (!_reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(true);
            _reticle.anchoredPosition = hit.MarkerLocal;
            _reticleDot.color = PointMode == ScanPointMode.Remove ? NegativePoint : PositivePoint;
            return true;
        }

        /// <summary>The point dots follow the flow's points (a dot per point, in its colour), placed on the photo's crop.</summary>
        private void SyncDots()
        {
            IReadOnlyList<ScanPoint> points = _flow.Points;
            while (_dots.Count < points.Count) _dots.Add(NewDot());
            Rect rect = _photoImage.rectTransform.rect, uv = _photoImage.uvRect;
            for (int i = 0; i < _dots.Count; i++)
            {
                bool on = i < points.Count && _photo != null;
                var ring = (RectTransform)_dots[i].transform.parent;
                ring.gameObject.SetActive(on);
                if (!on) continue;
                ScanPoint p = points[i];
                float u = ((float)(p.X / _photo.width) - uv.x) / uv.width;
                float v = ((1f - (float)(p.Y / _photo.height)) - uv.y) / uv.height;
                ring.anchoredPosition = new Vector2(rect.xMin + u * rect.width, rect.yMin + v * rect.height);
                _dots[i].color = p.Label == ScanPointLabel.Negative ? NegativePoint : PositivePoint;
            }
            _reticle.SetAsLastSibling();
        }

        /// <summary>A white-ringed dot; the ring (the returned rect) carries the position, its child the point's colour.</summary>
        private Image NewDot()
        {
            var ring = Rect("Point", _overlay, new Vector2(PointDotUnits + 6f, PointDotUnits + 6f));
            ring.anchorMin = ring.anchorMax = new Vector2(0.5f, 0.5f);
            var ringImage = ring.gameObject.AddComponent<Image>();
            UiSprites.Apply(ringImage, UiSprites.RoundedRect((PointDotUnits + 6f) * 0.5f));
            ringImage.color = ReticleRing;
            ringImage.raycastTarget = false;
            var dot = Rect("Dot", ring, new Vector2(PointDotUnits, PointDotUnits));
            dot.anchorMin = dot.anchorMax = new Vector2(0.5f, 0.5f);
            var dotImage = dot.gameObject.AddComponent<Image>();
            UiSprites.Apply(dotImage, UiSprites.RoundedRect(PointDotUnits * 0.5f));
            dotImage.raycastTarget = false;
            SetLayer(ring, UiLayer);
            return dotImage;
        }

        // ------------------------------------------------------------------ helpers

        private void ShowBox(bool visible)
        {
            if (_annotation == null || _boxHidden == !visible) return;
            _boxHidden = !visible;
            _annotation.OnVisibleToggleChanged(visible);
        }

        /// <summary>The box goes once the request is out (as the panel did): the card and the ring keep its place.</summary>
        private void DeleteAnnotation()
        {
            if (_annotation == null) return;
            var go = _annotation.gameObject;
            _annotation = null;
            Discard(go);
        }

        // ------------------------------------------------------------------ placement

        /// <summary>The card sits above the box's top, turned to the user (yaw only), and grows past the reading distance.</summary>
        private void Place()
        {
            Vector3 top = _center + Vector3.up * (_size.y * 0.5f + LiftMetres);
            transform.position = top;
            if (_eye == null) return;
            UiMetrics.Face(transform, _eye, UiMetrics.ScanCard, _card);
        }

        // ------------------------------------------------------------------ view building

        private void BuildView()
        {
            var p = UiTheme.Palette;
            _card = UiMetrics.NewCanvas("Card", transform, UiMetrics.ScanCard, new Vector2(Width, 200f));
            _card.gameObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
            _card.pivot = new Vector2(0.5f, 0f);

            ControlPanelKit.AddPanel(_card, UiTheme.RadiusCard);

            _title = Label("Title", _card, UiTheme.FontTitle, p.Text, true);
            _closeButton = NewButton("Close", _card, false, ToolHeight, out _closeLabel);
            _closeLabel.text = ScanCardText.Close;
            _closeButton.Clicked += OnHeaderButton;
            // Anchored to the card's top-right corner with a centred pivot: the ray collider is centred on the pivot, so it covers
            // exactly the drawn button.
            var closeRect = (RectTransform)_closeButton.transform;
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(0.5f, 0.5f);
            closeRect.sizeDelta = new Vector2(CloseButtonWidth, ToolHeight);
            closeRect.anchoredPosition = new Vector2(-Pad - CloseButtonWidth * 0.5f, -Pad - ToolHeight * 0.5f);
            closeRect.GetComponent<BoxCollider>().size = new Vector3(CloseButtonWidth, ToolHeight, 6f);

            // The photo: the live view while aiming, the frozen capture after. Drawn in this order: the frame, the photo, the mask, the
            // caption, then the overlay with the point dots and the reticle on top of everything.
            _thumb = Rect("Thumb", _card, new Vector2(Width - Pad * 2f, ThumbHeightUnits));
            Fill("Frame", _thumb, p.SurfaceDisabled, 12f, -3f);
            _photoImage = new GameObject("Photo", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            Stretch(_photoImage.rectTransform, _thumb);
            _photoImage.raycastTarget = false;
            // The photo takes the ray (a collider the size of the photo), so a press on it is a point, not a press on what is behind.
            var tapGo = new GameObject("PhotoTap", typeof(RectTransform), typeof(Image));
            Stretch(tapGo.GetComponent<RectTransform>(), _thumb);
            var tapImage = tapGo.GetComponent<Image>();
            tapImage.color = Color.clear;
            tapImage.raycastTarget = false;
            _photoTap = ModeSwitchClickTarget.Create(tapGo, new Vector2(Width - Pad * 2f, ThumbHeightUnits), tapImage, Color.clear, Color.clear, Color.clear);
            _photoTap.Pressed += () => BeginPhotoPress(PressingRay());
            _photoTap.Clicked += () => EndPhotoPress();
            _photoTap.gameObject.SetActive(false);
            _maskImage = new GameObject("Mask", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            Stretch(_maskImage.rectTransform, _thumb);
            _maskImage.raycastTarget = false;
            var capBg = Fill("CaptionBg", _thumb, p.PanelBottom, 8f, 0f);
            var cbr = capBg.rectTransform;
            cbr.anchorMin = new Vector2(0f, 0f); cbr.anchorMax = new Vector2(1f, 0f); cbr.pivot = new Vector2(0.5f, 0f);
            cbr.offsetMin = Vector2.zero; cbr.offsetMax = new Vector2(0f, 40f);
            capBg.color = new Color(p.PanelBottom.r, p.PanelBottom.g, p.PanelBottom.b, 0.7f);
            _caption = Label("Caption", _thumb, UiTheme.FontCaption, p.Text, false);
            _caption.text = ScanCardText.ThumbCaption;
            var cr = _caption.rectTransform;
            cr.anchorMin = new Vector2(0f, 0f); cr.anchorMax = new Vector2(1f, 0f); cr.pivot = new Vector2(0.5f, 0f);
            cr.sizeDelta = new Vector2(-16f, 32f); cr.anchoredPosition = new Vector2(0f, 6f);
            _overlay = Rect("Overlay", _thumb, Vector2.zero);
            Stretch(_overlay, _thumb);
            _reticle = Rect("Reticle", _overlay, new Vector2(ReticleUnits, ReticleUnits));
            _reticle.anchorMin = _reticle.anchorMax = new Vector2(0.5f, 0.5f);
            var reticleRing = _reticle.gameObject.AddComponent<Image>();
            UiSprites.Apply(reticleRing, UiSprites.Ring(ReticleUnits * 0.5f, 5f));
            reticleRing.color = ReticleRing;
            reticleRing.raycastTarget = false;
            var reticleDot = Rect("Dot", _reticle, new Vector2(12f, 12f));
            reticleDot.anchorMin = reticleDot.anchorMax = new Vector2(0.5f, 0.5f);
            _reticleDot = reticleDot.gameObject.AddComponent<Image>();
            UiSprites.Apply(_reticleDot, UiSprites.RoundedRect(6f));
            _reticleDot.color = PositivePoint;
            _reticleDot.raycastTarget = false;
            _reticle.gameObject.SetActive(false);
            _thumb.gameObject.SetActive(false);

            _detail = Label("Detail", _card, UiTheme.FontCaption, p.TextMuted, false);

            // The point tools: +/− (what the next point marks), Undo (the last point), Clear (all of them).
            _tools = Rect("Tools", _card, new Vector2(Width - Pad * 2f, ToolHeight));
            _modeButton = NewButton("Mode", _tools, false, ToolHeight, out _modeLabel);
            _modeButton.Clicked += ToggleMode;
            _undoButton = NewButton("UndoPoint", _tools, false, ToolHeight, out var undoLabel);
            undoLabel.text = ScanCardText.UndoPoint;
            _undoButton.Clicked += () => UndoPoint();
            _clearButton = NewButton("Clear", _tools, false, ToolHeight, out var clearLabel);
            clearLabel.text = ScanCardText.Clear;
            _clearButton.Clicked += () => ClearPoints();
            float inner = Width - Pad * 2f, w = (inner - 2f * 12f) / 3f;
            SizeButton((RectTransform)_modeButton.transform, w, -inner * 0.5f + w * 0.5f, ToolHeight);
            SizeButton((RectTransform)_undoButton.transform, w, 0f, ToolHeight);
            SizeButton((RectTransform)_clearButton.transform, w, inner * 0.5f - w * 0.5f, ToolHeight);
            _tools.gameObject.SetActive(false);

            _buttons = Rect("Buttons", _card, new Vector2(Width - Pad * 2f, ButtonHeight));
            _primary = NewButton("Primary", _buttons, true, ButtonHeight, out _primaryLabel);
            _secondary = NewButton("Secondary", _buttons, false, ButtonHeight, out _secondaryLabel);
            _primary.Clicked += () => { if (_primaryEnabled) _onPrimary?.Invoke(); };
            _secondary.Clicked += () => _onSecondary?.Invoke();
        }

        private ModeSwitchClickTarget NewButton(string name, RectTransform row, bool primary, float height, out TextMeshProUGUI label)
        {
            var c = UiTheme.Button(primary);
            var rect = Rect(name, row, new Vector2(10f, height));
            var image = ControlPanelKit.AddButtonFill(rect, height, primary);
            var target = ModeSwitchClickTarget.Create(rect.gameObject, new Vector2(10f, height), image, c.Normal, c.Hover, c.Pressed);
            label = Label("Label", rect, UiTheme.FontLabel, c.Label, true);
            label.alignment = TextAlignmentOptions.Center;
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.offsetMin = lr.offsetMax = Vector2.zero;
            return target;
        }

        /// <summary>The tools' buttons, for the tests (a ray click calls the same).</summary>
        public void PressMode() { if (ToolsVisible) ToggleMode(); }
        public void PressUndoPoint() { if (ToolsVisible) UndoPoint(); }
        public void PressClear() { if (ToolsVisible) ClearPoints(); }

        private void BuildRing()
        {
            var p = UiTheme.Palette;
            _ring = new GameObject("ScanRing", typeof(RectTransform), typeof(Canvas));
            UiMetrics.MakeWorldSpace(_ring.GetComponent<Canvas>(), UiMetrics.World);
            var rect = (RectTransform)_ring.transform;
            float footprint = new Vector2(_size.x, _size.z).magnitude;
            float diameterMm = (footprint + 0.12f) * 1000f;
            rect.sizeDelta = new Vector2(diameterMm, diameterMm);
            rect.position = new Vector3(_center.x, _center.y - _size.y * 0.5f + 0.004f, _center.z);
            rect.rotation = Quaternion.Euler(90f, 0f, 0f);
            Sprite ring = UiSprites.Ring(100f, 7f);
            var track = Rect("Track", rect, Vector2.zero);
            Stretch(track, rect);
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = ring;
            trackImage.type = Image.Type.Simple;
            trackImage.color = new Color(p.Track.r, p.Track.g, p.Track.b, 0.45f);
            trackImage.raycastTarget = false;
            var fill = Rect("Fill", rect, Vector2.zero);
            Stretch(fill, rect);
            _ringFill = fill.gameObject.AddComponent<Image>();
            _ringFill.sprite = ring;
            _ringFill.type = Image.Type.Filled;
            _ringFill.fillMethod = Image.FillMethod.Radial360;
            _ringFill.fillOrigin = (int)Image.Origin360.Top;
            _ringFill.fillClockwise = true;
            _ringFill.fillAmount = 0f;
            _ringFill.color = p.Accent;
            _ringFill.raycastTarget = false;
            // Its own root: the card moves with the box top, the ring stays on the base.
            _ring.SetActive(false);
        }

        // ------------------------------------------------------------------ view state

        private void Show(string title, string detail, Color titleColor, bool thumb, bool mask, bool tools)
        {
            _title.text = title;
            _title.color = titleColor;
            _detail.gameObject.SetActive(!string.IsNullOrEmpty(detail));
            _detail.text = detail ?? string.Empty;
            bool photo = thumb && _photo != null;
            if (photo)
            {
                _previewing = false;
                _caption.text = ScanCardText.ThumbCaption;
                ShowPhoto();
            }
            if (photo || !_previewing) _thumb.gameObject.SetActive(photo);
            _maskImage.gameObject.SetActive(photo && mask && _maskTex != null && _maskImage.texture != null);
            bool selecting = photo && tools;
            _photoTap.gameObject.SetActive(selecting);
            _overlay.gameObject.SetActive(photo);
            if (!selecting && _reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(false);
            if (!photo) foreach (Image dot in _dots) dot.transform.parent.gameObject.SetActive(false);
            _tools.gameObject.SetActive(selecting);
            Layout();
        }

        private void SetButtons(string primary, Action onPrimary, string secondary, Action onSecondary)
        {
            _onPrimary = onPrimary;
            _onSecondary = onSecondary;
            _primary.gameObject.SetActive(primary != null);
            _secondary.gameObject.SetActive(secondary != null);
            if (primary != null) _primaryLabel.text = primary;
            if (secondary != null) _secondaryLabel.text = secondary;
            float inner = Width - Pad * 2f;
            const float gap = 12f;
            var a = (RectTransform)_primary.transform;
            var b = (RectTransform)_secondary.transform;
            if (primary != null && secondary != null)
            {
                float wa = (inner - gap) * 0.6f, wb = inner - gap - wa;
                SizeButton(a, wa, -inner * 0.5f + wa * 0.5f, ButtonHeight);
                SizeButton(b, wb, inner * 0.5f - wb * 0.5f, ButtonHeight);
            }
            else if (primary != null) SizeButton(a, inner, 0f, ButtonHeight);
            else if (secondary != null) SizeButton(b, inner, 0f, ButtonHeight);
            Layout();
        }

        private static void SizeButton(RectTransform button, float width, float x, float height)
        {
            button.sizeDelta = new Vector2(width, height);
            button.anchoredPosition = new Vector2(x, 0f);
            var box = button.GetComponent<BoxCollider>();
            if (box != null) box.size = new Vector3(width, height, 6f);
        }

        /// <summary>Stacks the visible parts top-down and fits the card around them.</summary>
        private void Layout()
        {
            float inner = Width - Pad * 2f;
            float y = -Pad;
            void Place(RectTransform rect, float height)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(inner, height);
                rect.anchoredPosition = new Vector2(0f, y);
                y -= height + UiTheme.Space4;
            }
            // The title shares its row with the Close button on the right.
            float titleWidth = inner - CloseButtonWidth - UiTheme.Space4;
            Place(_title.rectTransform, Mathf.Max(ToolHeight, Mathf.Ceil(_title.GetPreferredValues(_title.text, titleWidth, 0f).y) + 4f));
            _title.rectTransform.sizeDelta = new Vector2(titleWidth, _title.rectTransform.sizeDelta.y);
            _title.rectTransform.anchoredPosition = new Vector2(-(inner - titleWidth) * 0.5f, _title.rectTransform.anchoredPosition.y);
            if (_thumb.gameObject.activeSelf) Place(_thumb, ThumbHeightUnits);
            if (_detail.gameObject.activeSelf) Place(_detail.rectTransform, Mathf.Ceil(_detail.GetPreferredValues(_detail.text, inner, 0f).y) + 4f);
            if (_tools.gameObject.activeSelf) Place(_tools, ToolHeight);
            if (_primary.gameObject.activeSelf || _secondary.gameObject.activeSelf) Place(_buttons, ButtonHeight);
            float height = -y - UiTheme.Space4 + Pad;
            _card.sizeDelta = new Vector2(Width, height);
        }

        /// <summary>
        /// The crop of a <paramref name="width"/> x <paramref name="height"/> frame that the thumbnail shows: the largest centred
        /// rect of the thumbnail's own shape, so the picture is never stretched. It does not depend on the box (the
        /// zoom is locked; a crop that followed the box's projection zoomed in and out as the head moved). The live view and the
        /// frozen photo use the same crop, so the preview shows what the capture will hold.
        /// </summary>
        public static Rect Crop(int width, int height)
        {
            if (width <= 0 || height <= 0) return new Rect(0f, 0f, 1f, 1f);
            float pw = width, ph = height;
            float aspect = (Width - Pad * 2f) / ThumbHeightUnits;
            float h = Mathf.Min(ph, pw / aspect);
            float w = h * aspect;
            float x1 = (pw - w) * 0.5f, y1 = (ph - h) * 0.5f;
            // pixels are from the top; uv from the bottom
            return new Rect(x1 / pw, 1f - (y1 + h) / ph, w / pw, h / ph);
        }

        /// <summary>The frozen photo, at the same fixed crop as the live view.</summary>
        private void ShowPhoto()
        {
            _photoImage.texture = _photo;
            Rect uv = _photo != null ? Crop(_photo.width, _photo.height) : new Rect(0f, 0f, 1f, 1f);
            _photoImage.uvRect = uv;
            _maskImage.uvRect = uv;
        }

        private void BuildMaskTexture()
        {
            byte[] mask = _flow.Mask;
            int w = _flow.MaskWidth, h = _flow.MaskHeight;
            if (mask == null || w <= 0 || h <= 0 || mask.Length < w * h) return;
            if (_maskTex == null || _maskTex.width != w || _maskTex.height != h)
            {
                Discard(_maskTex);
                _maskTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            if (_maskPixels == null || _maskPixels.Length != w * h) _maskPixels = new Color32[w * h];
            Color32 on = ScanPanelStyle.MaskColor32, off = default;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    _maskPixels[(h - 1 - y) * w + x] = mask[y * w + x] != 0 ? on : off;   // mask rows from the top
            _maskTex.SetPixels32(_maskPixels);
            _maskTex.Apply();
            _maskImage.texture = _maskTex;
        }

        // ------------------------------------------------------------------ small builders

        /// <summary>Unity's UI layer: a rendered stand-in camera (<see cref="RenderedFrameSource"/>) leaves it out, as the real camera never sees it.</summary>
        public const int UiLayer = 5;

        private static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer);
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            return rect;
        }

        private static void Stretch(RectTransform rect, Transform parent)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        /// <summary>A rounded fill that covers its parent (inset by <paramref name="inset"/> units; negative grows it).</summary>
        private static Image Fill(string name, RectTransform parent, Color color, float radius, float inset)
        {
            var rect = Rect(name, parent, Vector2.zero);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            var image = rect.gameObject.AddComponent<Image>();
            UiSprites.Apply(image, UiSprites.RoundedRect(radius));
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI Label(string name, Transform parent, float mm, Color color, bool semiBold)
        {
            var rect = Rect(name, parent, new Vector2(Width - Pad * 2f, mm * 1.6f));
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(label, semiBold);
            label.fontSize = mm;
            label.color = color;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            return label;
        }
    }
}
