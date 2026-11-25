// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using SpatialBlend.Interactions.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The capture card on the box (scan UI Concept A, stage 1) over a fake camera and a fake scan server: no readiness or "in view"
    /// checks, the permission and camera messages in the card, capture (nothing sent until the first point; never the box), the
    /// live view at a fixed crop, the frozen photo with the user's points and the refined mask, Confirm and 3D copy (no timer), the
    /// ring while building, the copy beside the real object, failures, and closing.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanCardTests
    {
        private sealed class FakeCamera : IScanFrameSource
        {
            public ScanCameraStatus Status { get; set; } = ScanCameraStatus.Live;
            public ScanCameraModel Camera;
            public int Starts, Stops, Retries;

            public bool TryGetLiveCamera(out ScanCameraModel camera)
            {
                camera = Camera;
                return Status == ScanCameraStatus.Live && Camera.IsValid;
            }

            public bool TryCapture(out byte[] photo, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
            {
                texture = new Texture2D(64, 48, TextureFormat.RGB24, false);
                photo = ScanPhotoEncoding.Encode(texture);
                width = 64;
                height = 48;
                camera = Camera;
                return Status == ScanCameraStatus.Live;
            }

            /// <summary>The live view's texture: one, kept for the camera's life (the card must never get a new one per call).</summary>
            public readonly Texture2D Preview = new Texture2D(64, 48, TextureFormat.RGB24, false);
            public int PreviewCalls;

            public bool TryGetPreview(out Texture texture, out ScanCameraModel camera)
            {
                PreviewCalls++;
                texture = Status == ScanCameraStatus.Live ? Preview : null;
                camera = Camera;
                return texture != null;
            }

            public void Start() => Starts++;
            public void Stop() => Stops++;
            public void RetryPermission() => Retries++;
            public void Tick(float now) { }
        }

        // The box 1.2 m ahead at table height; the camera at eye height looking at it (Unity axes, as the app's camera pose is).
        private static readonly Vector3 BoxCenter = new Vector3(0f, 0.95f, 1.2f);
        private static readonly Vector3 BoxSize = new Vector3(0.38f, 0.5f, 0.38f);
        private static readonly Vector3 EyePosition = new Vector3(0f, 1.5f, 0f);

        private FakeScanTransport _transport;
        private FakeScanFiles _files;
        private SessionManager _manager;
        private FakeCamera _camera;
        private Transform _eye;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private Action<ScanCard> _configure;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeScanTransport();
            _files = new FakeScanFiles();
            var managerGo = new GameObject("session-manager");
            _objects.Add(managerGo);
            _manager = managerGo.AddComponent<SessionManager>();
            Assert.That(_manager.InitializeWith(_transport, _files, new ScanSessionConfig
            {
                ServerAddress = "scan.test:443", ClientId = "card", CacheDirectory = "c", OutputDirectory = "o",
            }), Is.True);
            var eyeGo = new GameObject("eye");
            _objects.Add(eyeGo);
            _eye = eyeGo.transform;
            _eye.position = EyePosition;
            _eye.rotation = Quaternion.LookRotation(BoxCenter - EyePosition);
            _camera = new FakeCamera
            {
                Camera = new ScanCameraModel(EyePosition, _eye.rotation, new Vector2(866f, 866f), new Vector2(640f, 480f),
                    new Vector2Int(1280, 960), 64, 48),
            };
            _configure = card => { card.FrameSource = _camera; card.ServiceSource = () => _manager.Service; };
            ScanCard.Opening += _configure;
        }

        [TearDown]
        public void TearDown()
        {
            ScanCard.Opening -= _configure;
            if (ScanCard.Current != null) ScanCard.Current.Close();
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            if (_camera != null) UnityEngine.Object.DestroyImmediate(_camera.Preview);
        }

        /// <summary>The user's first point, on the photo's centre: segmentation starts here (nothing is asked at capture).</summary>
        private static void FirstPoint(ScanCard card) => Assert.That(card.AddPoint(new ScanPoint(32, 24)), Is.True);

        private ScanCard Open()
        {
            var card = ScanCard.Open(BoxCenter, BoxSize, Quaternion.identity, _eye);
            _objects.Add(card.gameObject);
            return card;
        }

        private void AnswerSegment(string message = "Segment OK | points:1", byte[] mask = null)
        {
            ScanTransportRequest request = _transport.Submitted.Last();
            _files.Files[request.OutputPath] = mask ?? Enumerable.Repeat((byte)1, 64 * 48).ToArray();
            _transport.Complete(ScanKind.Segment, _transport.Handles.Last(), true, message);
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble);
        }

        // The app whose room the card reads (SbApp.PlacementRoom); the Freeform app returns the room it read from MRUK.
        private sealed class RoomApp : SbApp
        {
            public RoomRegion Room;
            public override RoomRegion PlacementRoom => Room;
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, SbApp.OnObjectAddedType type) { }
            public override void OnRemove(GameObject gameObject) { }
        }

        // The scan box has no room rule. Capture is enabled with no room known, with the
        // box over a couch, and with the box outside the room's floor.
        [Test]
        public void CaptureIsAlwaysEnabled_OverFurnitureOutsideTheRoomOrWithNoRoom()
        {
            Vector2[] floor = { new Vector2(-2f, -1f), new Vector2(2f, -1f), new Vector2(2f, 3f), new Vector2(-2f, 3f) };
            Vector2[] couch = { new Vector2(-0.6f, 1.0f), new Vector2(0.6f, 1.0f), new Vector2(0.6f, 2.2f), new Vector2(-0.6f, 2.2f) };
            var card = Open();
            Assert.That(card.CaptureEnabled, Is.True, "no room known");
            card.Close();
            var appGo = new GameObject("room-app");
            _objects.Add(appGo);
            appGo.AddComponent<RoomApp>().Room = RoomRegion.Build(floor, null, new[] { couch });
            card = Open();   // its box is over the couch
            card.Refresh(0f, 0.016f);
            Assert.That(card.CaptureEnabled, Is.True, "over furniture");
            Assert.That(card.PrimaryEnabled, Is.True);
            Assert.That(card.Capture(), Is.True);
            card.Close();
            card = ScanCard.Open(new Vector3(5f, 0.2f, 1.2f), BoxSize, Quaternion.identity, _eye);
            _objects.Add(card.gameObject);
            Assert.That(card.CaptureEnabled, Is.True, "outside the room's floor");
        }

        [Test]
        public void ItOpensOnTheBoxWithTheLiveViewAndOneCaptureButton()
        {
            var card = Open();
            Assert.That(ScanCard.Current, Is.SameAs(card));
            Assert.That(_camera.Starts, Is.EqualTo(1), "the camera starts with the card");
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.AimTitle));
            Assert.That(card.PreviewVisible, Is.True, "the live view in place of the checks");
            Assert.That(card.ThumbTexture, Is.SameAs(_camera.Preview), "the camera's own texture, not a copy");
            Assert.That(card.PrimaryLabel, Is.EqualTo(ScanCardText.Capture));
            Assert.That(card.SecondaryLabel, Is.Null, "no footer Cancel: the header's Close is the way out");
            Assert.That(card.CloseLabel, Is.EqualTo(ScanCardText.Close));
            Assert.That(BoxAnnotationManager.CreationHeld, Is.True, "a trigger press captures; it must not start a box");
            Assert.That(card.transform.position.y, Is.GreaterThan(BoxCenter.y + BoxSize.y * 0.5f), "above the box");
        }

        // No readiness checks or hints ("step closer", "hold still", "too dark"), and no "box in view"
        // gate; only the camera's own state is said.
        [Test]
        public void NoReadinessHints_TheDetailStaysTheCaptureLine_EvenLookingAway()
        {
            var card = Open();
            card.Refresh(0f, 0.016f);
            Assert.That(card.DetailText, Is.EqualTo(ScanCardText.AimHint));
            _camera.Camera = new ScanCameraModel(EyePosition, Quaternion.LookRotation(Vector3.left), new Vector2(866f, 866f),
                new Vector2(640f, 480f), new Vector2Int(1280, 960), 64, 48);
            for (int i = 1; i < 10; i++) card.Refresh(i * 0.1f, 0.016f);
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.AimTitle));
            Assert.That(card.DetailText, Is.EqualTo(ScanCardText.AimHint), "looking away says nothing new: the live view shows it");
            Assert.That(card.PrimaryEnabled, Is.True);
        }

        [Test]
        public void ThePermissionMessageIsInTheCardWithAllowCamera()
        {
            _camera.Status = ScanCameraStatus.PermissionDenied;
            var card = Open();
            card.Refresh(0f, 0.016f);
            Assert.That(card.TitleText, Is.EqualTo(ScanPanelStyle.Describe(ScanCameraStatus.PermissionDenied).Title));
            Assert.That(card.PrimaryLabel, Is.EqualTo(ScanPanelStyle.AllowCameraLabel));
            card.PressPrimary();
            Assert.That(_camera.Retries, Is.EqualTo(1));
            Assert.That(card.Capture(), Is.False, "nothing to capture without a camera");

            _camera.Status = ScanCameraStatus.Live;
            card.Refresh(0.1f, 0.016f);
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.AimTitle), "the card goes back to aiming by itself");
        }

        [Test]
        public void CaptureFreezesTheFrame_TheFirstPointAsks_ThenConfirmLeadsTo3DCopy_NoTimer()
        {
            var card = Open();
            card.PressPrimary();   // Capture
            Assert.That(card.Progress.CaptureCount, Is.EqualTo(1));
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));
            Assert.That(card.PreviewVisible, Is.False, "the live view stops");
            Assert.That(card.ThumbTexture, Is.Not.SameAs(_camera.Preview), "the frozen photo, not the live texture");
            Assert.That(_transport.Submitted, Is.Empty, "no box-only request after the photo");
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.SelectTitle));
            Assert.That(card.DetailText, Is.EqualTo(ScanCardText.SelectDetail));
            FirstPoint(card);
            ScanPoint[] sent = _transport.Submitted.Last().Points;
            Assert.That(sent.Length, Is.EqualTo(1), "the user's point alone");
            Assert.That(sent[0].Label, Is.EqualTo(ScanPointLabel.Positive));
            Assert.That(sent[0].X, Is.EqualTo(32)); Assert.That(sent[0].Y, Is.EqualTo(24));
            Assert.That(card.DetailText, Is.EqualTo(ScanCardText.Refining));
            Assert.That(card.ToolsVisible, Is.True, "+/−, Undo, Clear");
            Assert.That(card.PrimaryLabel, Is.EqualTo(ScanCardText.Confirm));
            Assert.That(card.PrimaryEnabled, Is.False, "nothing to confirm yet");
            card.PressPrimary();
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));

            AnswerSegment();
            Assert.That(card.ThumbVisible && card.MaskVisible, Is.True, "the photo with the mask");
            Assert.That(card.PrimaryEnabled, Is.True);
            // No time limit. Frames long after the mask build nothing.
            for (int i = 0; i < 5; i++) card.Refresh(10f * i, 0.016f);
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 600);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));
            card.PressPrimary();   // Confirm
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Confirming));
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.SelectedTitle));
            Assert.That(card.PrimaryLabel, Is.EqualTo(ScanCardText.ThreeDCopy));
            Assert.That(card.SecondaryLabel, Is.EqualTo(ScanCardText.Back));
            Assert.That(_transport.Submitted.Any(r => r.Kind == ScanKind.Reconstruct), Is.False, "nothing is built by a timer");

            card.PressSecondary();   // Back
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));
            card.PressSecondary();   // Retake
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Aiming));
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.AimTitle));
            Assert.That(card.PreviewVisible, Is.True, "the live view is back");
        }

        [Test]
        public void BuildingShowsTheRingAndTheCopyIsInstalledInTheDrawnBox()
        {
            var card = Open();
            // A scan is a real object; its copy is installed exactly where the box was drawn, never beside it.
            Assert.That(card.Anchor.Center, Is.EqualTo(BoxCenter));
            Assert.That(card.Anchor.Size, Is.EqualTo(BoxSize));

            card.PressPrimary();
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();     // Confirm
            card.PressPrimary();     // 3D copy
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Building));
            ScanTransportRequest reconstruct = _transport.Submitted.Last();
            Assert.That(reconstruct.Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(reconstruct.ImageBytes, Is.SameAs(card.Flow.Photo), "the photo encoded once at capture");
            Assert.That(reconstruct.MaskBytes, Has.Length.EqualTo(64 * 48), "the point-based mask, at the photo's size");
            Assert.That(card.RingVisible, Is.True);
            Assert.That(card.TitleText, Is.EqualTo(ScanCardText.BuildingTitle));
            Assert.That(BoxAnnotationManager.CreationHeld, Is.False, "while the model is built the user may draw again");
            Assert.That(ScanCard.AnyCapturing, Is.False, "the mode assist may go back to Interaction while it builds");
            Assert.That(ScanCard.AnyOpen, Is.True);

            _transport.Complete(ScanKind.Reconstruct, _transport.Handles.Last(), true);
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Done));
            Assert.That(card.DetailText, Is.EqualTo(ScanCardText.DoneInPlace));
            Assert.That(card.RingFill, Is.EqualTo(1f));
        }

        [Test]
        public void TheMaskTextureIsUprightAndTheThumbnailHasTheFixedCrop()
        {
            var card = Open();
            card.PressPrimary();
            FirstPoint(card);
            var mask = new byte[64 * 48];
            for (int x = 0; x < 64; x++) mask[x] = 1;   // only the photo's top row (row 0 of the mask is the top)
            AnswerSegment(mask: mask);
            Texture2D tex = card.MaskTexture;
            Assert.That(tex, Is.Not.Null);
            Assert.That(tex.GetPixel(10, 47).a, Is.GreaterThan(0f), "the top row of the photo is the texture's last row");
            Assert.That(tex.GetPixel(10, 0).a, Is.EqualTo(0f));
            Rect uv = card.ThumbUv;
            Assert.That(uv.width * 64f / (uv.height * 48f), Is.EqualTo((ScanCard.Width - ScanCard.Pad * 2f) / ScanCard.ThumbHeightUnits).Within(0.02f), "the crop has the thumbnail's shape");
            Assert.That(uv.xMin, Is.GreaterThanOrEqualTo(0f)); Assert.That(uv.xMax, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(uv.yMin, Is.GreaterThanOrEqualTo(0f)); Assert.That(uv.yMax, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(uv, Is.EqualTo(ScanCard.Crop(64, 48)), "the frozen photo keeps the live view's fixed crop");
        }

        [Test]
        public void ACardStillBuildingKeepsGoingWhenAnotherOpens()
        {
            var first = Open();
            first.PressPrimary();
            FirstPoint(first);
            AnswerSegment();
            first.PressPrimary();   // Confirm
            first.PressPrimary();   // 3D copy
            var second = Open();
            Assert.That(first.IsOpen, Is.True, "its ring and its result stay");
            Assert.That(ScanCard.Current, Is.SameAs(second));
            first.Close();
        }

        [Test]
        public void AnOlderCardThatFailsWhileBuildingOffersOnlyClose()
        {
            var first = Open();
            first.PressPrimary();
            FirstPoint(first);
            AnswerSegment();
            first.PressPrimary();   // Confirm
            first.PressPrimary();   // 3D copy
            long build = _transport.Handles.Last();
            var second = Open();
            _transport.Complete(ScanKind.Reconstruct, build, false, "the model could not be built");
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(first.Flow.State, Is.EqualTo(ScanFlowState.Failed));
            Assert.That(first.PrimaryLabel, Is.Null, "no Retake: only the latest card captures");
            Assert.That(first.SecondaryLabel, Is.Null, "no second Close in the footer");
            Assert.That(first.CloseVisible, Is.True);
            first.PressClose();
            Assert.That(ScanCard.Current, Is.SameAs(second));
        }

        [Test]
        public void TheCopyIsInstalledInTheDrawnBoxTurnedAsDrawnAndTheResultCarriesTheTurn()
        {
            if (ScanCard.Current != null) ScanCard.Current.Close();
            Quaternion turn = Quaternion.Euler(0f, 35f, 0f);
            var card = ScanCard.Open(BoxCenter, BoxSize, turn, _eye);
            Assert.That(card.Anchor.Center, Is.EqualTo(BoxCenter));
            Assert.That(card.Anchor.Size, Is.EqualTo(BoxSize));
            Assert.That(Quaternion.Angle(card.Anchor.Rotation, turn), Is.LessThan(0.01f), "turned as drawn");
            Assert.That(Quaternion.Angle(card.ScannedBox.Rotation, turn), Is.LessThan(0.01f));
            var hint = new ScanRegionHint(card.Anchor.Center, card.Anchor.Size, rotation: card.Anchor.Rotation);
            Assert.That(Quaternion.Angle(hint.Rotation, turn), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(default(ScanRegionHint).Rotation, Quaternion.identity), Is.LessThan(0.01f), "an unturned box by default");
            card.Close();
        }

        // A failed refinement keeps the photo and the points; the card says why and the next point asks again.
        [Test]
        public void AFailedMask_KeepsThePhotoAndThePoints_SaysWhy_AndTheNextPointAsksAgain()
        {
            var card = Open();
            ScanError? reported = null;
            card.RequestFailed += e => reported = e;
            card.PressPrimary();   // Capture
            Assert.That(card.AddPoint(new ScanPoint(10, 10)), Is.True);
            _transport.Complete(ScanKind.Segment, _transport.Handles.Last(), false, "No object was found at that point.");
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting), "not Failed: the photo stays");
            Assert.That(card.ThumbVisible, Is.True);
            Assert.That(card.DetailText, Does.StartWith(ScanFailureText.ToUserMessage(card.Flow.RefineError)));
            Assert.That(card.PrimaryEnabled, Is.False, "nothing to confirm");
            Assert.That(reported.HasValue, Is.True, "the app hears it");
            Assert.That(card.AddPoint(new ScanPoint(32, 24)), Is.True);
            Assert.That(card.Flow.RefineError.IsError, Is.False, "asking again clears it");
            AnswerSegment();
            Assert.That(card.PrimaryEnabled, Is.True);
        }

        [Test]
        public void AFailedModelIsShownInTheCardInPlainWordsWithRetake()
        {
            var card = Open();
            card.PressPrimary();
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();   // Confirm
            card.PressPrimary();   // 3D copy
            _transport.Complete(ScanKind.Reconstruct, _transport.Handles.Last(), false, "the model could not be built");
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Failed));
            Assert.That(card.TitleText, Is.EqualTo(ScanFailureText.Title));
            Assert.That(card.DetailText, Is.EqualTo(ScanFailureText.ToUserMessage(card.Flow.Error)));
            Assert.That(card.PrimaryLabel, Is.EqualTo(ScanCardText.Retake));
            card.PressPrimary();
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Aiming));
        }

        [Test]
        public void ABoxOutOfTheCameraStillCaptures_AndTheFirstPointIsSentAlone()
        {
            // No "box in view" check. Looking away from the box (it is behind the camera), or with the box only
            // partly in frame, the photo is still held; the user picks the object with points.
            foreach (Quaternion look in new[] { Quaternion.LookRotation(Vector3.back), Quaternion.LookRotation(Vector3.left),
                         Quaternion.LookRotation(BoxCenter + new Vector3(0.3f, 0f, 0f) - EyePosition) })
            {
                _transport.Submitted.Clear();
                var card = Open();
                _camera.Camera = new ScanCameraModel(EyePosition, look, new Vector2(866f, 866f),
                    new Vector2(640f, 480f), new Vector2Int(1280, 960), 64, 48);
                card.Refresh(0f, 0.016f);
                Assert.That(card.CaptureEnabled && card.PrimaryEnabled, Is.True, "Capture is never disabled by the box");
                Assert.That(card.Capture(), Is.True, "captures with the box out of view: " + look.eulerAngles);
                Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));
                Assert.That(card.TitleText, Is.EqualTo(ScanCardText.SelectTitle));
                Assert.That(_transport.Submitted, Is.Empty, "nothing is asked until the first point");
                FirstPoint(card);
                Assert.That(_transport.Submitted.Last().Points.Length, Is.EqualTo(1));
                Assert.That(_transport.Submitted.Last().Points.Any(p => p.IsBoxCorner), Is.False);
                card.Close();
            }
        }

        [Test]
        public void TheHeaderCloseClosesTheCardAndReleasesTheCamera()
        {
            var card = Open();
            bool closed = false;
            card.Closed += () => closed = true;
            card.PressClose();
            Assert.That(closed, Is.True);
            Assert.That(ScanCard.AnyOpen, Is.False);
            Assert.That(_camera.Stops, Is.EqualTo(1));
            Assert.That(BoxAnnotationManager.CreationHeld, Is.False);
        }

        // The scan window needs a way out while choosing points or confirming.
        [Test]
        public void TheHeaderCloseIsShownInEveryCapturingState()
        {
            var card = Open();
            Assert.That(card.CloseVisible, Is.True, "aiming");
            card.PressPrimary();   // Capture
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Selecting));
            Assert.That(card.CloseVisible, Is.True, "choosing points");
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();   // Confirm
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Confirming));
            Assert.That(card.CloseVisible, Is.True, "confirming");
        }

        [Test]
        public void TheHeaderCloseLeavesWhileAPointIsBeingRefined_AndRestoresTheNormalState()
        {
            var card = Open();
            bool closed = false;
            card.Closed += () => closed = true;
            card.PressPrimary();   // Capture
            FirstPoint(card);      // the point's segment request is in flight
            Assert.That(_transport.Submitted.Count, Is.EqualTo(1));
            card.PressClose();
            Assert.That(closed, Is.True);
            Assert.That(ScanCard.AnyOpen, Is.False);
            Assert.That(ScanCard.AnyCapturing, Is.False, "the trigger and gaze are the user's again");
            Assert.That(BoxAnnotationManager.CreationHeld, Is.False, "box drawing is released");
            Assert.That(_camera.Stops, Is.EqualTo(1), "the camera is released");
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Idle), "the pending request is dropped");
            Assert.That(_transport.Submitted.Any(r => r.Kind == ScanKind.Reconstruct), Is.False, "nothing is built");
        }

        // In Building the header reads Hide; the card goes, the ring stays, and the copy still arrives.
        [Test]
        public void HideWhileBuilding_KeepsTheRingAndTheBuild_AndTheCopyStillArrives()
        {
            var card = Open();
            var terminal = new List<ScanPendingEvent>();
            _manager.Service.PendingChanged += e => { if (e.Kind == ScanKind.Reconstruct && e.IsTerminal) terminal.Add(e); };
            bool closed = false;
            card.Closed += () => closed = true;
            card.PressPrimary();   // Capture
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();   // Confirm
            card.PressPrimary();   // 3D copy
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Building));
            Assert.That(card.CloseLabel, Is.EqualTo(ScanCardText.Hide));
            Assert.That(card.SecondaryLabel, Is.EqualTo(ScanCardText.Cancel), "the footer Cancel still stops the build");

            card.PressClose();   // Hide
            Assert.That(card.IsHidden, Is.True);
            Assert.That(card.CardVisible, Is.False, "the card goes");
            Assert.That(card.RingVisible, Is.True, "the ring on the box's base is the pending indicator");
            Assert.That(card.IsOpen, Is.True);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Building), "the model keeps building");
            Assert.That(_transport.Submitted.Count(r => r.Kind == ScanKind.Reconstruct), Is.EqualTo(1));
            card.Refresh(Time.unscaledTime + 5f, 0.016f);
            Assert.That(card.RingFill, Is.GreaterThan(0f), "the ring keeps filling");

            _transport.Complete(ScanKind.Reconstruct, _transport.Handles.Last(), true);
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(terminal.Count, Is.EqualTo(1));
            Assert.That(terminal[0].Phase, Is.EqualTo(ScanPendingPhase.Succeeded), "the copy arrives (the app installs it on this)");
            Assert.That(closed, Is.True, "a hidden card goes quietly once its copy is in place (as Done does)");
            Assert.That(ScanCard.AnyOpen, Is.False);
            Assert.That(card == null || !card.RingVisible, Is.True);
        }

        // A build cancelled from outside (the service disposed, the tutorial backend) while the card is
        // hidden ends the hidden card too, instead of leaving it open and invisible.
        [Test]
        public void ABuildCancelledFromOutsideBehindAHiddenCardClosesIt()
        {
            var card = Open();
            bool closed = false;
            card.Closed += () => closed = true;
            card.PressPrimary();   // Capture
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();   // Confirm
            card.PressPrimary();   // 3D copy
            card.PressClose();     // Hide
            Assert.That(card.IsHidden, Is.True);
            Assert.That(_manager.Service.Cancel(_transport.Handles.Last()), Is.True);
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(closed, Is.True);
            Assert.That(ScanCard.AnyOpen, Is.False);
            Assert.That(card == null || !card.RingVisible, Is.True);
        }

        [Test]
        public void ABuildThatFailsBehindAHiddenCardShowsTheCardAgain()
        {
            var card = Open();
            card.PressPrimary();   // Capture
            FirstPoint(card);
            AnswerSegment();
            card.PressPrimary();   // Confirm
            card.PressPrimary();   // 3D copy
            card.PressClose();     // Hide
            _transport.Complete(ScanKind.Reconstruct, _transport.Handles.Last(), false, "the model could not be built");
            _manager.Service.Update(Time.realtimeSinceStartupAsDouble + 1);
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Failed));
            Assert.That(card.CardVisible, Is.True);
            Assert.That(card.IsHidden, Is.False);
            Assert.That(card.CloseLabel, Is.EqualTo(ScanCardText.Close));
        }

        // The ray collider of a button is centred on its pivot; a button whose pivot is not its centre is only
        // partly hittable and its collider sticks out of the card. Every ray button of the card, in every state, must have a
        // collider that is exactly its drawn rect.
        [Test]
        public void EveryRayButtonsColliderIsExactlyItsDrawnRect_InEveryState()
        {
            var card = Open();
            void Check(string state)
            {
                foreach (var target in card.GetComponentsInChildren<ModeSwitchClickTarget>(false))
                {
                    var rect = (RectTransform)target.transform;
                    var box = target.GetComponent<BoxCollider>();
                    Rect r = rect.rect;
                    Assert.That(box.center.x, Is.EqualTo(r.center.x).Within(1e-3f), state + ": " + rect.name + " centre x");
                    Assert.That(box.center.y, Is.EqualTo(r.center.y).Within(1e-3f), state + ": " + rect.name + " centre y");
                    Assert.That(box.size.x, Is.EqualTo(r.width).Within(1e-3f), state + ": " + rect.name + " width");
                    Assert.That(box.size.y, Is.EqualTo(r.height).Within(1e-3f), state + ": " + rect.name + " height");
                }
            }
            Check("aiming");
            card.PressPrimary();   // Capture
            FirstPoint(card);
            AnswerSegment();
            Check("selecting");
            card.PressPrimary();   // Confirm
            Check("confirming");
            card.PressPrimary();   // 3D copy
            Check("building");
        }

        [Test]
        public void TheHeaderCloseDoesNotOverlapTheTitle()
        {
            var card = Open();
            var close = (RectTransform)card.transform.Find("Card/Close");
            var title = (RectTransform)card.transform.Find("Card/Title");
            Assert.That(close, Is.Not.Null);
            Vector3[] c = new Vector3[4], t = new Vector3[4];
            close.GetWorldCorners(c);
            title.GetWorldCorners(t);
            Vector3 right = card.transform.Find("Card").right;
            Assert.That(Vector3.Dot(t[2], right), Is.LessThan(Vector3.Dot(c[0], right) + 1e-4f), "the title ends left of Close");
        }

        [Test]
        public void OpeningASecondCardClosesTheFirst()
        {
            var first = Open();
            var second = Open();
            Assert.That(first == null || !first.IsOpen, Is.True);
            Assert.That(ScanCard.Current, Is.SameAs(second));
        }

        [Test]
        public void TheCardUsesOnlyThemeTokensAndThePretendardFace()
        {
            var card = Open();
            foreach (var text in card.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (UiFonts.Regular != null) Assert.That(text.font == UiFonts.Regular || text.font == UiFonts.SemiBold, Is.True, text.name);
            foreach (var label in card.GetComponentsInChildren<TMPro.TMP_Text>(true))
                Assert.That(UiTheme.FontTokens, Does.Contain((int)label.fontSize), "a type-scale token: " + label.name);
        }

        // ---- the live view

        [Test]
        public void TheLiveViewRefreshesAtAModestRate_WithTheSameTextureAndNoAllocation()
        {
            var card = Open();
            int before = card.PreviewRefreshes;
            for (int i = 0; i < 60; i++) card.Refresh(i / 60f, 1f / 60f);   // one second at 60 frames
            int refreshes = card.PreviewRefreshes - before;
            Assert.That(refreshes, Is.InRange(7, 10), "about 8 Hz, not every frame");
            Assert.That(card.ThumbTexture, Is.SameAs(_camera.Preview), "the same texture every time");
            Assert.That(card.ThumbUv, Is.EqualTo(ScanCard.Crop(64, 48)), "the fixed crop the capture will have");
            // No managed allocation per refresh once warm.
            card.RefreshPreview();
            long start = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) card.RefreshPreview();
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(allocated, Is.EqualTo(0), "the live view allocates nothing per refresh");
        }

        // The crop is fixed: the preview does not zoom in and out as the head moves (as it would if the crop followed the
        // box's projection).
        [Test]
        public void TheLiveViewZoomIsLocked_MovingTheHeadDoesNotChangeTheCrop()
        {
            var card = Open();
            card.RefreshPreview();
            Rect first = card.ThumbUv;
            Vector3[] eyes = { EyePosition + Vector3.forward * 0.4f, EyePosition + Vector3.back * 0.8f, EyePosition + Vector3.right * 0.3f };
            foreach (Vector3 eye in eyes)
            {
                _camera.Camera = new ScanCameraModel(eye, Quaternion.LookRotation(BoxCenter - eye), new Vector2(866f, 866f),
                    new Vector2(640f, 480f), new Vector2Int(1280, 960), 64, 48);
                card.RefreshPreview();
                Assert.That(card.ThumbUv, Is.EqualTo(first), "nearer, further or to the side: the same crop");
            }
            Rect fixedCrop = ScanCard.Crop(64, 48);
            Assert.That(fixedCrop.center.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(fixedCrop.center.y, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(Mathf.Max(fixedCrop.width, fixedCrop.height), Is.EqualTo(1f).Within(1e-5f), "as wide (or tall) as the frame: no zoom");
        }

        [Test]
        public void CaptureFreezesTheFrame_TheLiveViewStops()
        {
            var card = Open();
            card.Refresh(0f, 0.016f);
            Assert.That(card.Capture(), Is.True);
            int calls = _camera.PreviewCalls;
            for (int i = 0; i < 30; i++) card.Refresh(1f + i / 30f, 1f / 30f);
            Assert.That(_camera.PreviewCalls, Is.EqualTo(calls), "no live view while points are chosen");
            Assert.That(card.ThumbTexture, Is.InstanceOf<Texture2D>().And.Not.SameAs(_camera.Preview));
        }

        // ---- points on the photo

        private ScanCard CaptureForPoints()
        {
            var card = Open();
            Assert.That(card.Capture(), Is.True);
            Canvas.ForceUpdateCanvases();
            return card;
        }

        private Ray RayAt(ScanCard card, Vector2 photoLocal)
        {
            var photo = card.transform.Find("Card/Thumb/Photo") as RectTransform;
            Vector3 target = photo.TransformPoint(new Vector3(photoLocal.x, photoLocal.y, 0f));
            return new Ray(_eye.position, (target - _eye.position).normalized);
        }

        [Test]
        public void TheReticleFollowsTheRayOnThePhoto_OpaqueAndDrawnAboveEverything()
        {
            var card = CaptureForPoints();
            FirstPoint(card);
            AnswerSegment();   // a mask is shown: the reticle must be above it
            Assert.That(card.MaskVisible, Is.True);
            Assert.That(card.UpdateReticle(RayAt(card, new Vector2(30f, -20f))), Is.True);
            Assert.That(card.ReticleVisible, Is.True);
            Assert.That(card.ReticleLocal.x, Is.EqualTo(30f).Within(0.5f));
            Assert.That(card.ReticleLocal.y, Is.EqualTo(-20f).Within(0.5f));
            // On top: the reticle is the last thing drawn in the photo, after the photo, the mask, the caption and the point dots.
            Transform thumb = card.transform.Find("Card/Thumb");
            Transform overlay = thumb.Find("Overlay");
            Assert.That(overlay.GetSiblingIndex(), Is.EqualTo(thumb.childCount - 1), "the overlay is drawn last in the photo");
            Assert.That(thumb.Find("Mask").GetSiblingIndex(), Is.LessThan(overlay.GetSiblingIndex()));
            Assert.That(card.ReticleRect.GetSiblingIndex(), Is.EqualTo(overlay.childCount - 1), "the reticle is drawn last");
            Assert.That(thumb.GetSiblingIndex(), Is.LessThan(card.transform.Find("Card/Buttons").GetSiblingIndex()), "the photo stays under the card's buttons");
            // Opaque, readable: a white ring and a dot of full alpha, about 7 cm across at the card's size.
            Assert.That(card.ReticleRect.GetComponent<UnityEngine.UI.Image>().color.a, Is.EqualTo(1f));
            Assert.That(card.ReticleDotTint, Is.EqualTo(ScanCard.PositivePoint));
            Assert.That(card.ReticleDotTint.a, Is.EqualTo(1f));
            Assert.That(card.ReticleRect.rect.width * card.CardRect.lossyScale.x, Is.GreaterThan(0.06f));
            Assert.That(card.ReticleRect.gameObject.layer, Is.EqualTo(ScanCard.UiLayer));
            card.PressMode();
            card.UpdateReticle(RayAt(card, new Vector2(30f, -20f)));
            Assert.That(card.ReticleDotTint, Is.EqualTo(ScanCard.NegativePoint), "Remove: the next point is red");
            Assert.That(card.UpdateReticle(RayAt(card, new Vector2(500f, 0f))), Is.False, "off the photo");
            Assert.That(card.ReticleVisible, Is.False);
            Assert.That(card.UpdateReticle(null), Is.False);
        }

        // The reticle needs no hit from the SDK: it is the ray's own crossing of the photo's plane (a dot that waited
        // for the editing pointer's reported hit never showed).
        [Test]
        public void TheReticleNeedsNoReportedHit_TheRaysCrossingOfThePhotoIsEnough()
        {
            var card = CaptureForPoints();
            var photo = card.transform.Find("Card/Thumb/Photo") as RectTransform;
            Vector3 onPhoto = photo.TransformPoint(Vector3.zero);
            var slanted = new Ray(onPhoto + photo.right * 0.4f - photo.forward * 0.8f, (onPhoto - (onPhoto + photo.right * 0.4f - photo.forward * 0.8f)).normalized);
            Assert.That(card.TryRayOnPhoto(slanted, out ScanCard.PhotoCanvasHit hit), Is.True);
            Assert.That(hit.MarkerLocal.magnitude, Is.LessThan(0.5f), "the centre of the photo, from a slanted ray");
        }

        [Test]
        public void EachPointAddsADot_PositiveGreen_NegativeRed_AndAsksForTheMaskOfAll()
        {
            var card = CaptureForPoints();
            Assert.That(_transport.Submitted, Is.Empty, "nothing until a point");
            Assert.That(card.AddPointAlong(RayAt(card, new Vector2(0f, 0f))), Is.True);
            Assert.That(_transport.Submitted.Count, Is.EqualTo(1));
            Assert.That(card.VisiblePointDots, Is.EqualTo(1));
            Assert.That(card.PointDots[0].color, Is.EqualTo(ScanCard.PositivePoint));
            AnswerSegment();
            card.PressMode();   // − Remove
            Assert.That(card.AddPointAlong(RayAt(card, new Vector2(60f, 40f))), Is.True);
            Assert.That(card.PointDots[1].color, Is.EqualTo(ScanCard.NegativePoint));
            ScanPoint[] sent = _transport.Submitted.Last().Points;
            Assert.That(sent.Select(p => p.Label), Is.EqualTo(new[] { ScanPointLabel.Positive, ScanPointLabel.Negative }));
            Assert.That(sent[1].X, Is.GreaterThan(sent[0].X), "the second point is to the right on the photo");
            Assert.That(sent[1].Y, Is.LessThan(sent[0].Y), "and higher (server pixels count down from the top)");
            AnswerSegment();
            Assert.That(card.PrimaryEnabled, Is.True);

            card.PressUndoPoint();
            Assert.That(card.Flow.Points.Count, Is.EqualTo(1));
            Assert.That(card.VisiblePointDots, Is.EqualTo(1));
            Assert.That(card.PrimaryEnabled, Is.False, "the mask on screen is for the old points until the answer");
            AnswerSegment();
            card.PressClear();
            Assert.That(card.Flow.Points, Is.Empty);
            Assert.That(card.VisiblePointDots, Is.EqualTo(0));
            Assert.That(card.MaskVisible, Is.False);
            Assert.That(card.PrimaryEnabled, Is.False, "Confirm needs a mask");
        }

        [Test]
        public void PointsWhileAnAnswerIsOut_AreSentTogetherOnce_AndTheStaleMaskIsNeverShown()
        {
            var card = CaptureForPoints();
            card.AddPointAlong(RayAt(card, new Vector2(0f, 0f)));
            card.AddPointAlong(RayAt(card, new Vector2(10f, 0f)));
            card.AddPointAlong(RayAt(card, new Vector2(20f, 0f)));
            Assert.That(_transport.Submitted.Count, Is.EqualTo(1), "one in flight");
            AnswerSegment();
            Assert.That(card.MaskVisible, Is.False, "the first point's answer is stale");
            Assert.That(_transport.Submitted.Count, Is.EqualTo(2));
            Assert.That(_transport.Submitted.Last().Points.Length, Is.EqualTo(3), "the latest points, together");
            AnswerSegment();
            Assert.That(card.MaskVisible, Is.True);
            card.PressPrimary();   // Confirm
            Assert.That(card.Flow.State, Is.EqualTo(ScanFlowState.Confirming));
        }

        // The reticle follows the editing input's ray when it is on the photo, not whichever ray the scene lists first.
        [Test]
        public void TheReticlePrefersTheEditingRay_ThenAnyOtherOnThePhoto()
        {
            var card = CaptureForPoints();
            Ray editing = RayAt(card, new Vector2(-50f, 10f));
            Ray idleHand = RayAt(card, new Vector2(80f, -40f));
            var away = new Ray(_eye.position, Vector3.back);
            Assert.That(card.PickPhotoRay(editing, new[] { idleHand }).Value.direction, Is.EqualTo(editing.direction), "the editing ray first");
            Assert.That(card.PickPhotoRay(away, new[] { away, idleHand }).Value.direction, Is.EqualTo(idleHand.direction), "else a ray on the photo");
            Assert.That(card.PickPhotoRay(away, new[] { away }), Is.Null);
            Assert.That(card.PickPhotoRay(null, null), Is.Null);
        }

        // The point is where the ray was at the PRESS; a pinch that drifts while it opens does not move it.
        [Test]
        public void ThePointIsTakenAtThePress_NotAtTheRelease()
        {
            var card = CaptureForPoints();
            Assert.That(card.BeginPhotoPress(RayAt(card, new Vector2(-60f, 30f))), Is.True);
            Assert.That(_transport.Submitted, Is.Empty, "nothing on the press");
            Assert.That(card.EndPhotoPress(), Is.True);
            var pressed = card.Flow.Points.Single();
            Assert.That(card.TryRayOnPhoto(RayAt(card, new Vector2(-60f, 30f)), out ScanCard.PhotoCanvasHit atPress), Is.True);
            Assert.That(pressed.X, Is.EqualTo(atPress.ServerPoint.X).Within(1e-3));
            Assert.That(pressed.Y, Is.EqualTo(atPress.ServerPoint.Y).Within(1e-3));
            Assert.That(card.EndPhotoPress(), Is.False, "one press, one point");
            Assert.That(card.BeginPhotoPress(new Ray(_eye.position, Vector3.back)), Is.False, "a press off the photo is no point");
            Assert.That(card.EndPhotoPress(), Is.False);
        }

        [Test]
        public void TheReticleIsOnlyThereWhilePointsAreChosen()
        {
            var card = Open();
            Assert.That(card.UpdateReticle(new Ray(_eye.position, (BoxCenter - _eye.position).normalized)), Is.False, "aiming: no reticle");
            Assert.That(card.ReticleVisible, Is.False);
        }

        // Demo fix (speed): the photo goes to the server as JPEG, not PNG (a 1728 x 1728 PNG is several MB and is re-sent with every
        // point). It keeps the frame's size, so the points and the mask stay in the same pixel space, and it is encoded once at
        // Capture: every Segment and the Reconstruct send the same bytes.
        [Test]
        public void ThePhotoIsSentAsJpegAtTheFrameSize_EncodedOnceForEveryRequest()
        {
            const int w = 96, h = 64;
            var still = new Texture2D(w, h, TextureFormat.RGB24, false);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    pixels[y * w + x] = new Color32((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x ^ y) & 0xFF), 255);
            still.SetPixels32(pixels);
            still.Apply();
            try
            {
                ScanCard.Opening -= _configure;
                _configure = c => { c.FrameSource = new StillFrameSource(still, null); c.ServiceSource = () => _manager.Service; };
                ScanCard.Opening += _configure;
                var card = Open();
                Assert.That(card.Capture(), Is.True);
                byte[] photo = card.Flow.Photo;
                Assert.That(photo.Length, Is.GreaterThan(4));
                Assert.That(photo[0], Is.EqualTo(0xFF)); Assert.That(photo[1], Is.EqualTo(0xD8), "a JPEG (SOI), not a PNG");
                Assert.That(photo[photo.Length - 2], Is.EqualTo(0xFF)); Assert.That(photo[photo.Length - 1], Is.EqualTo(0xD9), "ends with EOI");
                Assert.That(card.Flow.PhotoWidth, Is.EqualTo(w));
                Assert.That(card.Flow.PhotoHeight, Is.EqualTo(h));
                var decoded = new Texture2D(2, 2);
                try
                {
                    Assert.That(decoded.LoadImage(photo, false), Is.True, "decodes as the server's PIL would");
                    Assert.That(decoded.width, Is.EqualTo(w), "the same size: points and mask keep their pixel space");
                    Assert.That(decoded.height, Is.EqualTo(h));
                }
                finally { UnityEngine.Object.DestroyImmediate(decoded); }

                Assert.That(card.AddPoint(new ScanPoint(40, 30)), Is.True);
                Assert.That(_transport.Submitted.Last().ImageBytes, Is.SameAs(photo));
                _files.Files[_transport.Submitted.Last().OutputPath] = Enumerable.Repeat((byte)1, w * h).ToArray();
                _transport.Complete(ScanKind.Segment, _transport.Handles.Last(), true, "Segment OK | points:1");
                _manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(card.AddPoint(new ScanPoint(10, 10)), Is.True);
                Assert.That(_transport.Submitted.Last().ImageBytes, Is.SameAs(photo), "the second point re-sends the same bytes, not a new encode");
            }
            finally { UnityEngine.Object.DestroyImmediate(still); }
        }

        [Test]
        public void TheCardIsTwiceItsMillimetreSize()
        {
            ScanCard card = Open();
            Assert.That(ScanCard.MetresPerUnit, Is.EqualTo(0.002f).Within(1e-7f));
            Assert.That(card.CardRect.lossyScale.x, Is.EqualTo(0.002f).Within(1e-6f), "1.2 m away: within reading distance");
            Assert.That(card.CardRect.rect.width * card.CardRect.lossyScale.x, Is.EqualTo(ScanCard.Width * 0.002f).Within(1e-4f));
            var button = card.transform.Find("Card/Buttons/Primary").GetComponent<BoxCollider>();
            Assert.That(button.bounds.size.y, Is.EqualTo(ScanCard.ButtonHeight * 0.002f).Within(2e-3f), "the ray target doubles with it");
        }

    }

    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanAimingTests
    {
        [Test]
        public void TheRenderedCameraModelProjectsLikeUnitysOwnCamera()
        {
            // The projection's sign and origin, checked against Unity: a rendered stand-in camera must put a point where Unity draws it.
            var go = new GameObject("cam", typeof(Camera));
            try
            {
                var cam = go.GetComponent<Camera>();
                cam.fieldOfView = 50f;
                cam.aspect = 640f / 480f;
                go.transform.SetPositionAndRotation(new Vector3(0.1f, 1.5f, 0f), Quaternion.Euler(25f, 10f, 0f));
                var source = new RenderedFrameSource(cam, 640, 480);
                ScanCameraModel model = source.Model();
                foreach (var world in new[] { new Vector3(0.2f, 0.9f, 1.3f), new Vector3(-0.3f, 1.1f, 1.8f), new Vector3(0.5f, 0.7f, 1.1f) })
                {
                    Assert.That(model.TryProject(world, out Vector2 p), Is.True);
                    cam.pixelRect = new Rect(0, 0, 640, 480);
                    Vector3 screen = cam.WorldToScreenPoint(world);   // y from the bottom
                    Assert.That(p.x, Is.EqualTo(screen.x * 640f / cam.pixelWidth).Within(0.5f), "x of " + world);
                    Assert.That(p.y, Is.EqualTo(480f - screen.y * 480f / cam.pixelHeight).Within(0.5f), "y (from the top) of " + world);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AFootPivotSitsOnTheBoxBottomCenter()
        {
            var center = new Vector3(0f, 0.95f, 1.2f);
            var size = new Vector3(0.38f, 0.5f, 0.38f);
            var foot = new Bounds(new Vector3(0f, 0.15f, 0f), new Vector3(0.2f, 0.3f, 0.2f));
            Vector3 holder = ScanBoxRest.HolderPosition(center, size, Quaternion.identity, foot, Vector3.one);
            Assert.That(holder.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(holder.y, Is.EqualTo(0.7f).Within(1e-5f), "the foot origin is the box bottom");
            Assert.That(holder.z, Is.EqualTo(1.2f).Within(1e-5f));
        }

        [Test]
        public void ACenterPivotIsLiftedSoTheMeshRestsOnTheBottom()
        {
            var center = new Vector3(0f, 0.95f, 1.2f);
            var size = new Vector3(0.38f, 0.5f, 0.38f);
            var mesh = new Bounds(Vector3.zero, new Vector3(0.2f, 0.3f, 0.2f));
            Vector3 holder = ScanBoxRest.HolderPosition(center, size, Quaternion.identity, mesh, Vector3.one);
            Assert.That(holder.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(holder.y, Is.EqualTo(0.85f).Within(1e-5f), "half the mesh height above the box bottom");
            Assert.That(holder.z, Is.EqualTo(1.2f).Within(1e-5f));
        }

        [Test]
        public void AnOffsetScaledPivotStaysCenteredInATurnedBoxAndRestsOnItsBottom()
        {
            var boxCenter = new Vector3(1f, 0.8f, 2f);
            var boxSize = new Vector3(0.4f, 0.6f, 0.3f);
            var rotation = Quaternion.Euler(0f, 90f, 0f);
            var holder = new GameObject("offset-holder");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                holder.transform.localScale = new Vector3(2f, 1.5f, 2f);
                cube.transform.SetParent(holder.transform, false);
                cube.transform.localPosition = new Vector3(0.08f, 0.2f, -0.03f);
                cube.transform.localScale = new Vector3(0.1f, 0.3f, 0.12f);
                Assert.That(ScanBoxRest.TryContentBounds(holder.transform, out Bounds local), Is.True);
                Assert.That(local.center.x, Is.EqualTo(0.08f).Within(1e-4f));
                Assert.That(local.center.y, Is.EqualTo(0.2f).Within(1e-4f));
                Assert.That(local.center.z, Is.EqualTo(-0.03f).Within(1e-4f));
                Assert.That(local.min.y, Is.EqualTo(0.05f).Within(1e-4f));
                Vector3 position = ScanBoxRest.HolderPosition(boxCenter, boxSize, rotation, local, holder.transform.localScale);
                holder.transform.SetPositionAndRotation(position, rotation);
                Bounds world = cube.GetComponent<Renderer>().bounds;
                Assert.That(world.min.y, Is.EqualTo(0.5f).Within(1e-3f), "the mesh bottom is the box's bottom face");
                Assert.That(world.center.x, Is.EqualTo(boxCenter.x).Within(1e-3f), "centered on the footprint");
                Assert.That(world.center.z, Is.EqualTo(boxCenter.z).Within(1e-3f), "centered on the footprint");
            }
            finally
            {
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        [Test]
        public void TheFitFollowsTheBoxAndIgnoresAZeroAxis()
        {
            Assert.That(ScanBoxRest.UniformFit(new Vector3(0.2f, 0.4f, 0.2f), new Vector3(0.38f, 0.5f, 0.38f)),
                Is.EqualTo(1.25f).Within(1e-5f), "the height is the tight axis");
            Assert.That(ScanBoxRest.UniformFit(Vector3.one, new Vector3(0.38f, 0.5f, 0.38f)),
                Is.EqualTo(0.38f).Within(1e-5f), "a unit cube takes the box's smallest side");
            Assert.That(ScanBoxRest.UniformFit(new Vector3(0f, 1f, 1f), new Vector3(0.1f, 2f, 4f)),
                Is.EqualTo(2f).Within(1e-5f), "a missing axis is not a limit");
            Assert.That(ScanBoxRest.UniformFit(Vector3.zero, new Vector3(0.38f, 0.5f, 0.38f)), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void ASkinnedMeshCountsEvenWhenItsLocalBoundsAreEmpty()
        {
            var holder = new GameObject("skin-holder");
            var skinObject = new GameObject("skin");
            var mesh = new Mesh { name = "skin-mesh" };
            try
            {
                skinObject.transform.SetParent(holder.transform, false);
                skinObject.transform.localPosition = new Vector3(0.2f, 0f, 0f);
                mesh.vertices = new[]
                {
                    new Vector3(-0.1f, 0f, -0.1f),
                    new Vector3(0.1f, 0.8f, 0.1f),
                };
                mesh.RecalculateBounds();
                var skin = skinObject.AddComponent<SkinnedMeshRenderer>();
                skin.rootBone = skinObject.transform;
                skin.sharedMesh = mesh;
                skin.localBounds = new Bounds(Vector3.zero, new Vector3(0.01f, 0.01f, 0.01f));
                Assert.That(ScanBoxRest.TryContentBounds(holder.transform, out Bounds local), Is.True);
                Assert.That(local.size.y, Is.GreaterThan(0.5f), "the mesh height, not the stale local box");
                Assert.That(local.size.y, Is.EqualTo(0.8f).Within(0.02f));
                Assert.That(local.center.y, Is.EqualTo(0.4f).Within(0.02f));
                Assert.That(local.center.x, Is.EqualTo(0.2f).Within(0.02f), "the child's offset stays in the bounds");
            }
            finally
            {
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void APhotoHit_MapsToCanvasUv_AndToTheMarkerPosition()
        {
            var rect = new Rect(-200f, -115f, 400f, 230f);
            var crop = new Rect(0.1f, 0.2f, 0.5f, 0.6f);
            Assert.That(ScanCard.TryMapPhotoHit(Vector3.zero, rect, crop, 640, 480, out ScanCard.PhotoCanvasHit centre), Is.True);
            Assert.That(centre.CanvasUv.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(centre.CanvasUv.y, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(centre.MarkerLocal, Is.EqualTo(Vector2.zero));
            Assert.That(centre.ServerPoint.X, Is.EqualTo(224.0).Within(1e-4), "the cropped texture, not the full frame");
            Assert.That(centre.ServerPoint.Y, Is.EqualTo(240.0).Within(1e-4), "y down from the top");

            var corner = new Vector3(rect.xMin, rect.yMin, 0f);
            Assert.That(ScanCard.TryMapPhotoHit(corner, rect, crop, 640, 480, out ScanCard.PhotoCanvasHit bottomLeft), Is.True);
            Assert.That(bottomLeft.CanvasUv, Is.EqualTo(Vector2.zero));
            Assert.That(bottomLeft.MarkerLocal, Is.EqualTo(new Vector2(rect.xMin, rect.yMin)));
            Assert.That(bottomLeft.ServerPoint.X, Is.EqualTo(64.0).Within(1e-4));
            Assert.That(bottomLeft.ServerPoint.Y, Is.EqualTo(384.0).Within(1e-4));

            Assert.That(ScanCard.TryMapPhotoHit(new Vector3(0f, 0f, 3f), rect, crop, 640, 480, out _), Is.True,
                "a hit on the tap collider's face is still on the photo");
            Assert.That(ScanCard.TryMapPhotoHit(new Vector3(rect.xMax + 5f, 0f, 0f), rect, crop, 640, 480, out _), Is.False,
                "past the photo's edge there is no marker");
            Assert.That(ScanCard.TryMapPhotoHit(new Vector3(0f, 0f, 40f), rect, crop, 640, 480, out _), Is.False,
                "a hit off the photo's plane is not on the canvas");
        }
    }
}
#endif
