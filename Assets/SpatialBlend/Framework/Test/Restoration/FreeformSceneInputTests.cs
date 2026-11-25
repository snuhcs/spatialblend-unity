// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Freeform;
using SpatialBlend.Samples.FreeformCollaboration;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformSceneInputTests
    {
        private readonly List<Object> _created = new List<Object>();
        private FreeformContentCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            _catalog = new FreeformContentCatalog();
        }

        [TearDown]
        public void TearDown()
        {
            _catalog.Dispose();
            for (int index = _created.Count - 1; index >= 0; --index)
                if (_created[index] != null)
                    Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void WhiteboardLateUpdateWritesBoundRasterAndCancelsOnModeSwitchOrRebind()
        {
            WhiteboardContentView first = CreateWhiteboard("first", Color.white,
                                                            out WhiteboardContentModel firstModel);
            WhiteboardContentView second = CreateWhiteboard("second", Color.white,
                                                             out WhiteboardContentModel secondModel);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("whiteboard-input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();

            Assert.That(input.Configure(first, pointer, modes, out string error), Is.True, error);
            input.SetPenColor(Color.red, 0);
            SetPointerObservation(pointer, first.transform.TransformPoint(Vector3.zero), true, true, true);
            InvokePrivate(input, "LateUpdate");
            Assert.That(firstModel.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.red));
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.DrawingPointer));

            modes.SetMode(InteractionMode.PanelManipulation);
            SetPointerObservation(pointer, first.transform.TransformPoint(new Vector3(.2f, 0f, 0f)),
                                  true, true, true);
            InvokePrivate(input, "LateUpdate");
            Assert.That(firstModel.CopyPixels()[5 + 4 * 8], Is.EqualTo((Color32)Color.white),
                        "A mode switch must release the old stroke before another point is committed.");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.PanelManipulation));

            modes.ResetMode(InteractionMode.PanelManipulation);
            Assert.That(input.Configure(second, pointer, modes, out error), Is.True, error);
            SetPointerObservation(pointer, second.transform.TransformPoint(Vector3.zero), true, true, true);
            InvokePrivate(input, "LateUpdate");
            Assert.That(secondModel.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.red));
            Assert.That(secondModel.CopyPixels()[5 + 4 * 8], Is.EqualTo((Color32)Color.white),
                        "Rebinding begins a fresh stroke rather than connecting the old board's last point.");
        }

        [Test]
        public void WhiteboardConfigurePreflightKeepsOldInputAndDisabledViewReleasesItsMode()
        {
            WhiteboardContentView first = CreateWhiteboard("first", Color.white,
                                                            out WhiteboardContentModel firstModel);
            WhiteboardContentView second = CreateWhiteboard("second", Color.white,
                                                             out _);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("whiteboard-input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(input.Configure(first, pointer, modes, out string error), Is.True, error);

            second.gameObject.SetActive(false);
            Assert.That(input.Configure(second, pointer, modes, out _), Is.False);
            input.SetPenColor(Color.green, 0);
            Assert.That(input.ProcessPointer(first.transform.TransformPoint(Vector3.zero), true, true,
                                             out error), Is.True, error);
            Assert.That(firstModel.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.green),
                        "A rejected replacement must not displace the prior live input binding.");

            first.gameObject.SetActive(false);
            Assert.That(input.ProcessPointer(Vector3.zero, false, false, out _), Is.False);
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.None));
        }

        [Test]
        public void WhiteboardReleaseOnlyResetsTheModeOfItsOwnStrokeAndDestroyedPointerReleasesIt()
        {
            WhiteboardContentView first = CreateWhiteboard("first", Color.white, out _);
            WhiteboardContentView second = CreateWhiteboard("second", Color.white, out _);
            var firstPointerObject = new GameObject("first-pointer");
            _created.Add(firstPointerObject);
            PointerHandlerMotif firstPointer = firstPointerObject.AddComponent<PointerHandlerMotif>();
            var secondPointerObject = new GameObject("second-pointer");
            _created.Add(secondPointerObject);
            PointerHandlerMotif secondPointer = secondPointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var firstInputObject = new GameObject("first-input");
            _created.Add(firstInputObject);
            FreeformSceneWhiteboardInput firstInput = firstInputObject.AddComponent<FreeformSceneWhiteboardInput>();
            var secondInputObject = new GameObject("second-input");
            _created.Add(secondInputObject);
            FreeformSceneWhiteboardInput secondInput = secondInputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(firstInput.Configure(first, firstPointer, modes, out string error), Is.True, error);
            Assert.That(secondInput.Configure(second, secondPointer, modes, out error), Is.True, error);

            Assert.That(firstInput.ProcessPointer(first.transform.TransformPoint(Vector3.zero), true, true,
                                                  out error), Is.True, error);
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.DrawingPointer));
            secondInput.ReleaseDrawing();
            secondInput.enabled = false;
            InvokePrivate(secondInput, "OnDisable");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.DrawingPointer),
                        "An idle second input must not cancel the first input's shared mode lease.");

            Object.DestroyImmediate(firstPointerObject);
            InvokePrivate(firstInput, "LateUpdate");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.None),
                        "A destroyed configured pointer must release its owned stroke in LateUpdate.");
        }

        [Test]
        public void WhiteboardModeLeaseCannotResetAnExternalSameEnumModeOrNewOwner()
        {
            WhiteboardContentView first = CreateWhiteboard("first", Color.white, out _);
            WhiteboardContentView second = CreateWhiteboard("second", Color.white, out _);
            var firstPointerObject = new GameObject("first-pointer");
            _created.Add(firstPointerObject);
            PointerHandlerMotif firstPointer = firstPointerObject.AddComponent<PointerHandlerMotif>();
            var secondPointerObject = new GameObject("second-pointer");
            _created.Add(secondPointerObject);
            PointerHandlerMotif secondPointer = secondPointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var firstInputObject = new GameObject("first-input");
            _created.Add(firstInputObject);
            FreeformSceneWhiteboardInput firstInput = firstInputObject.AddComponent<FreeformSceneWhiteboardInput>();
            var secondInputObject = new GameObject("second-input");
            _created.Add(secondInputObject);
            FreeformSceneWhiteboardInput secondInput = secondInputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(firstInput.Configure(first, firstPointer, modes, out string error), Is.True, error);
            Assert.That(secondInput.Configure(second, secondPointer, modes, out error), Is.True, error);
            Assert.That(firstInput.ProcessPointer(first.transform.TransformPoint(Vector3.zero), true, true,
                                                  out error), Is.True, error);

            modes.ResetMode(InteractionMode.PanelManipulation);
            firstInput.ReleaseDrawing();
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.None),
                        "A mismatched legacy reset must leave the active DrawingPointer lease disposable.");
            Assert.That(firstInput.ProcessPointer(first.transform.TransformPoint(Vector3.zero), true, true,
                                                  out error), Is.True, error);

            modes.SetMode(InteractionMode.DrawingPointer);
            firstInput.ReleaseDrawing();
            InvokePrivate(firstInput, "OnDisable");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.DrawingPointer),
                        "A legacy same-enum SetMode supersedes the old opaque lease.");

            modes.SetMode(InteractionMode.None);
            Assert.That(secondInput.ProcessPointer(second.transform.TransformPoint(Vector3.zero), true, true,
                                                   out error), Is.True, error);
            InvokePrivate(firstInput, "OnDisable");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.DrawingPointer),
                        "A stale first lease must not reset the second input's owned DrawingPointer mode.");
        }

        [Test]
        public void WhiteboardModelChangeCallbacksCannotResurrectAReleasedOrReconfiguredStroke()
        {
            WhiteboardContentView first = CreateWhiteboard("first", Color.white,
                                                            out WhiteboardContentModel firstModel);
            WhiteboardContentView second = CreateWhiteboard("second", Color.white,
                                                             out WhiteboardContentModel secondModel);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(input.Configure(first, pointer, modes, out string error), Is.True, error);

            var disableCallbackCount = 0;
            Action<FreeformContentModel> disableDuringChange = _ =>
            {
                ++disableCallbackCount;
                input.enabled = false;
                InvokePrivate(input, "OnDisable");
            };
            firstModel.Changed += disableDuringChange;
            input.SetPenColor(Color.red, 0);
            Assert.That(input.ProcessPointer(first.transform.TransformPoint(Vector3.zero), true, true, out _),
                        Is.False);
            firstModel.Changed -= disableDuringChange;
            Assert.That(disableCallbackCount, Is.EqualTo(1),
                        "The disable callback must be caused by an actual first raster change.");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.None));

            input.enabled = true;
            Assert.That(input.Configure(first, pointer, modes, out error), Is.True, error);
            var reconfigureCallbackCount = 0;
            Action<FreeformContentModel> reconfigureDuringChange = changedContent =>
            {
                ++reconfigureCallbackCount;
                input.Configure(second, pointer, modes, out string ignoredError);
            };
            firstModel.Changed += reconfigureDuringChange;
            input.SetPenColor(Color.green, 0);
            Assert.That(input.ProcessPointer(first.transform.TransformPoint(new Vector3(.2f, 0f, 0f)),
                                             true, true, out _), Is.False);
            firstModel.Changed -= reconfigureDuringChange;
            Assert.That(reconfigureCallbackCount, Is.EqualTo(1),
                        "The reconfigure callback must be caused by an independent raster change.");
            Assert.That(modes.CurrentMode, Is.EqualTo(InteractionMode.None));
            input.SetPenColor(Color.blue, 0);
            Assert.That(input.ProcessPointer(second.transform.TransformPoint(Vector3.zero), true, true,
                                             out error), Is.True, error);
            Assert.That(secondModel.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.blue),
                        "The replacement view starts a new dot rather than continuing a stale first-view stroke.");
        }

        [Test]
        public void WhiteboardMovementThresholdUsesNormalizedUvRatherThanScaledWorldDistance()
        {
            WhiteboardContentView view = CreateWhiteboard("scaled", Color.white,
                                                           out WhiteboardContentModel model);
            view.transform.localScale = new Vector3(.01f, 1f, 1f);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(input.Configure(view, pointer, modes, out string error), Is.True, error);
            input.SetPenColor(Color.red, 0);
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(Vector3.zero), true, true,
                                             out error), Is.True, error);
            input.SetPenColor(Color.blue, 0);
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(new Vector3(.002f, 0f, 0f)),
                                             true, true, out error), Is.True, error);
            Assert.That(model.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.blue),
                        "A sub-millimetre world delta with a >.001 local UV delta must remain a legacy draw movement.");
        }

        [Test]
        public void WhiteboardHeldDrawCallbackRestartCannotOverwriteTheNewStrokeEndpoint()
        {
            WhiteboardContentView view = CreateWhiteboard("board", Color.white,
                                                           out WhiteboardContentModel model);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(input.Configure(view, pointer, modes, out string error), Is.True, error);
            input.SetPenColor(Color.red, 0);
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(Vector3.zero), true, true,
                                             out error), Is.True, error);

            var restarted = false;
            Action<FreeformContentModel> restartDuringHeldDraw = _ =>
            {
                if (restarted)
                    return;
                restarted = true;
                input.ReleaseDrawing();
                input.SetPenColor(Color.blue, 0);
                Assert.That(input.ProcessPointer(view.transform.TransformPoint(new Vector3(-.4f, 0f, 0f)),
                                                 true, true, out string nestedError), Is.True, nestedError);
            };
            model.Changed += restartDuringHeldDraw;
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(new Vector3(.2f, 0f, 0f)), true,
                                             true, out _), Is.False);
            model.Changed -= restartDuringHeldDraw;
            Assert.That(restarted, Is.True);
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(new Vector3(-.3f, 0f, 0f)), true,
                                             true, out error), Is.True, error);
            Assert.That(model.CopyPixels()[2 + 4 * 8], Is.EqualTo((Color32)Color.white),
                        "The old held callback must not replace the restarted stroke endpoint with its old hit.");
            Assert.That(model.CopyPixels()[1 + 4 * 8], Is.EqualTo((Color32)Color.blue));
        }

        [Test]
        public void ColorSelectorLocalPenLeasesAllowTheAuthoredPaletteAndDisposeExactly()
        {
            WhiteboardContentView view = CreateWhiteboard("board", Color.white,
                                                           out WhiteboardContentModel model);
            var pointerObject = new GameObject("pointer");
            _created.Add(pointerObject);
            PointerHandlerMotif pointer = pointerObject.AddComponent<PointerHandlerMotif>();
            var modesObject = new GameObject("modes");
            _created.Add(modesObject);
            InteractionStateManagerMotif modes = modesObject.AddComponent<InteractionStateManagerMotif>();
            var inputObject = new GameObject("whiteboard-input");
            _created.Add(inputObject);
            FreeformSceneWhiteboardInput input = inputObject.AddComponent<FreeformSceneWhiteboardInput>();
            Assert.That(input.Configure(view, pointer, modes, out string error), Is.True, error);

            ColorSelectorMotif firstSelector = CreateSelector("first-selector", Color.magenta, 0);
            ColorSelectorMotif secondSelector = CreateSelector("second-selector", Color.cyan, 0);
            Assert.That(firstSelector.TryBindLocalPen(input, out IDisposable firstLease, out error), Is.True,
                        error);
            Assert.That(secondSelector.TryBindLocalPen(input, out IDisposable secondLease, out error), Is.True,
                        error);
            Assert.That(firstSelector.TryBindLocalPen(input, out _, out _), Is.False,
                        "One selector must not acquire a competing second lease.");
            firstSelector.ChangePenColor();
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(Vector3.zero), true, true, out error),
                        Is.True, error);
            Assert.That(model.CopyPixels()[4 + 4 * 8], Is.EqualTo((Color32)Color.magenta));
            input.ReleaseDrawing();

            firstLease.Dispose();
            secondSelector.ChangePenColor();
            Assert.That(input.ProcessPointer(view.transform.TransformPoint(new Vector3(.25f, 0f, 0f)),
                                             true, true, out error), Is.True, error);
            Assert.That(model.CopyPixels()[6 + 4 * 8], Is.EqualTo((Color32)Color.cyan),
                        "Disposing one selector lease must not disconnect another authored palette entry.");
            secondLease.Dispose();
        }

#if FUSION2
        [Test]
        public void UnleasedColorSelectorStillUsesItsSerializedLegacyPointerDrawingMotif()
        {
            var pointerObject = new GameObject("legacy-pointer");
            _created.Add(pointerObject);
            pointerObject.AddComponent<PointerHandlerMotif>();
            PointerDrawingMotif legacyDrawing = pointerObject.AddComponent<PointerDrawingMotif>();
            ColorSelectorMotif selector = CreateSelector("legacy-selector", Color.yellow, 3);
            SetField(selector, "m_pointerDrawingMotif", legacyDrawing);

            selector.ChangePenColor();
            Assert.That(GetField<Color>(legacyDrawing, "penColor"), Is.EqualTo(Color.yellow));
            Assert.That(GetField<int>(legacyDrawing, "brushRadius"), Is.EqualTo(3));
        }
#endif

        [Test]
        public void ChessWrapperEventsCaptureBoardLocalPoseAndPreserveExternalCallbacksOnTeardown()
        {
            ChessContentView view = CreateChess("first", "piece-a", out Transform piece,
                                                out InteractableUnityEventWrapper wrapper,
                                                out ChessContentModel model);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            var binding = new FreeformSceneChessPieceBinding("piece-a", piece, wrapper);
            var externalSelects = 0;
            var externalUnselects = 0;
            wrapper.WhenSelect.AddListener(() => ++externalSelects);
            wrapper.WhenUnselect.AddListener(() => ++externalUnselects);
            Assert.That(input.Configure(view, new[] { binding }, out string error), Is.True, error);

            SetBoardLocalPose(view, piece, new Vector3(.25f, 0f, -.15f), Quaternion.Euler(0f, 37f, 0f));
            wrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(model, "piece-a", new Vector3(.25f, 0f, -.15f), Quaternion.Euler(0f, 37f, 0f));

            SetBoardLocalPose(view, piece, new Vector3(-.1f, 0f, .3f), Quaternion.Euler(0f, 91f, 0f));
            wrapper.WhenUnselect.Invoke();
            AssertPose(model, "piece-a", new Vector3(-.1f, 0f, .3f), Quaternion.Euler(0f, 91f, 0f));
            Assert.That(externalSelects, Is.EqualTo(1));
            Assert.That(externalUnselects, Is.EqualTo(1));

            input.ReleaseInput();
            SetBoardLocalPose(view, piece, new Vector3(.45f, 0f, .45f), Quaternion.identity);
            wrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(model, "piece-a", new Vector3(-.1f, 0f, .3f), Quaternion.Euler(0f, 91f, 0f));
            Assert.That(externalSelects, Is.EqualTo(2),
                        "Input teardown must remove only its exact listener, never authored callbacks.");
        }

        [Test]
        public void ChessLateUpdateSnapshotsEverySelectedPoseBeforeModelChangedRefreshesTheView()
        {
            ChessContentView view = CreateChessPair("late-update", out Transform firstPiece,
                                                     out InteractableUnityEventWrapper firstWrapper,
                                                     out Transform secondPiece,
                                                     out InteractableUnityEventWrapper secondWrapper,
                                                     out ChessContentModel model);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            Assert.That(input.Configure(view, new[]
            {
                new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper),
                new FreeformSceneChessPieceBinding("piece-b", secondPiece, secondWrapper)
            }, out string error), Is.True, error);

            Vector3 firstPosition = new Vector3(.24f, 0f, -.16f);
            Quaternion firstRotation = Quaternion.Euler(0f, 41f, 0f);
            Vector3 secondPosition = new Vector3(-.31f, 0f, .27f);
            Quaternion secondRotation = Quaternion.Euler(0f, 123f, 0f);
            SetBoardLocalPose(view, firstPiece, firstPosition, firstRotation);
            SetBoardLocalPose(view, secondPiece, secondPosition, secondRotation);
            firstWrapper.WhenSelect.Invoke();
            secondWrapper.WhenSelect.Invoke();

            InvokePrivate(input, "LateUpdate");

            AssertPose(model, "piece-a", firstPosition, firstRotation);
            AssertPose(model, "piece-b", secondPosition, secondRotation);
            AssertViewPose(view, firstPiece, firstPosition, firstRotation);
            AssertViewPose(view, secondPiece, secondPosition, secondRotation);
        }

        [Test]
        public void ChessReleaseInputSnapshotsEverySelectedPoseBeforeModelChangedRefreshesTheView()
        {
            ChessContentView view = CreateChessPair("release", out Transform firstPiece,
                                                     out InteractableUnityEventWrapper firstWrapper,
                                                     out Transform secondPiece,
                                                     out InteractableUnityEventWrapper secondWrapper,
                                                     out ChessContentModel model);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            Assert.That(input.Configure(view, new[]
            {
                new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper),
                new FreeformSceneChessPieceBinding("piece-b", secondPiece, secondWrapper)
            }, out string error), Is.True, error);

            Vector3 firstPosition = new Vector3(.38f, 0f, .11f);
            Quaternion firstRotation = Quaternion.Euler(0f, 64f, 0f);
            Vector3 secondPosition = new Vector3(-.19f, 0f, -.34f);
            Quaternion secondRotation = Quaternion.Euler(0f, 151f, 0f);
            SetBoardLocalPose(view, firstPiece, firstPosition, firstRotation);
            SetBoardLocalPose(view, secondPiece, secondPosition, secondRotation);
            firstWrapper.WhenSelect.Invoke();
            secondWrapper.WhenSelect.Invoke();

            input.ReleaseInput();

            AssertPose(model, "piece-a", firstPosition, firstRotation);
            AssertPose(model, "piece-b", secondPosition, secondRotation);
            AssertViewPose(view, firstPiece, firstPosition, firstRotation);
            AssertViewPose(view, secondPiece, secondPosition, secondRotation);
        }

        [Test]
        public void ChessUnselectSnapshotsOtherSelectedPendingPoseBeforeModelChangedRefreshesTheView()
        {
            ChessContentView view = CreateChessPair("unselect", out Transform firstPiece,
                                                     out InteractableUnityEventWrapper firstWrapper,
                                                     out Transform secondPiece,
                                                     out InteractableUnityEventWrapper secondWrapper,
                                                     out ChessContentModel model);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            Assert.That(input.Configure(view, new[]
            {
                new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper),
                new FreeformSceneChessPieceBinding("piece-b", secondPiece, secondWrapper)
            }, out string error), Is.True, error);

            Vector3 firstPosition = new Vector3(.17f, 0f, -.26f);
            Quaternion firstRotation = Quaternion.Euler(0f, 32f, 0f);
            Vector3 secondPosition = new Vector3(-.36f, 0f, .22f);
            Quaternion secondRotation = Quaternion.Euler(0f, 117f, 0f);
            SetBoardLocalPose(view, firstPiece, firstPosition, firstRotation);
            SetBoardLocalPose(view, secondPiece, secondPosition, secondRotation);
            firstWrapper.WhenSelect.Invoke();
            secondWrapper.WhenSelect.Invoke();

            firstWrapper.WhenUnselect.Invoke();

            AssertPose(model, "piece-a", firstPosition, firstRotation);
            AssertPose(model, "piece-b", secondPosition, secondRotation);
            AssertViewPose(view, firstPiece, firstPosition, firstRotation);
            AssertViewPose(view, secondPiece, secondPosition, secondRotation);
            InvokePrivate(input, "LateUpdate");
            AssertPose(model, "piece-b", secondPosition, secondRotation);
            AssertViewPose(view, secondPiece, secondPosition, secondRotation);
        }

        [Test]
        public void ChessReconfigureRejectsInvalidBindingsWithoutDisplacingOldSubscriptions()
        {
            ChessContentView first = CreateChess("first", "piece-a", out Transform firstPiece,
                                                 out InteractableUnityEventWrapper firstWrapper,
                                                 out ChessContentModel firstModel);
            ChessContentView second = CreateChess("second", "piece-b", out Transform secondPiece,
                                                  out InteractableUnityEventWrapper secondWrapper,
                                                  out ChessContentModel secondModel);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            var firstBinding = new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper);
            Assert.That(input.Configure(first, new[] { firstBinding }, out string error), Is.True, error);

            Assert.That(input.Configure(first, new[] { firstBinding, firstBinding }, out _), Is.False);
            SetBoardLocalPose(first, firstPiece, new Vector3(.2f, 0f, 0f), Quaternion.identity);
            firstWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(firstModel, "piece-a", new Vector3(.2f, 0f, 0f), Quaternion.identity);

            var secondBinding = new FreeformSceneChessPieceBinding("piece-b", secondPiece, secondWrapper);
            Assert.That(input.Configure(second, new[] { secondBinding }, out error), Is.True, error);
            SetBoardLocalPose(first, firstPiece, new Vector3(.4f, 0f, 0f), Quaternion.identity);
            firstWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(firstModel, "piece-a", new Vector3(.2f, 0f, 0f), Quaternion.identity);

            SetBoardLocalPose(second, secondPiece, new Vector3(0f, 0f, -.25f), Quaternion.Euler(0f, 15f, 0f));
            secondWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(secondModel, "piece-b", new Vector3(0f, 0f, -.25f), Quaternion.Euler(0f, 15f, 0f));
        }

        [Test]
        public void ChessModelChangedCallbackCanReleaseOrReconfigureWithoutMutatingLiveSelectionEnumeration()
        {
            ChessContentView first = CreateChess("first", "piece-a", out Transform firstPiece,
                                                 out InteractableUnityEventWrapper firstWrapper,
                                                 out ChessContentModel firstModel);
            ChessContentView second = CreateChess("second", "piece-b", out Transform secondPiece,
                                                  out InteractableUnityEventWrapper secondWrapper,
                                                  out ChessContentModel secondModel);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            var firstBinding = new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper);
            var secondBinding = new FreeformSceneChessPieceBinding("piece-b", secondPiece, secondWrapper);
            Assert.That(input.Configure(first, new[] { firstBinding }, out string error), Is.True, error);
            var reconfigured = false;
            Action<FreeformContentModel> reconfigureOnChanged = _ =>
            {
                if (reconfigured)
                    return;
                reconfigured = true;
                Assert.That(input.Configure(second, new[] { secondBinding }, out string nestedError),
                            Is.True, nestedError);
            };
            firstModel.Changed += reconfigureOnChanged;
            SetBoardLocalPose(first, firstPiece, new Vector3(.18f, 0f, 0f), Quaternion.identity);
            firstWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            firstModel.Changed -= reconfigureOnChanged;
            Assert.That(reconfigured, Is.True);

            SetBoardLocalPose(second, secondPiece, new Vector3(0f, 0f, -.2f), Quaternion.identity);
            secondWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            AssertPose(secondModel, "piece-b", new Vector3(0f, 0f, -.2f), Quaternion.identity);
        }

        [Test]
        public void ChessModelReplacementAndStaleSameIdWrapperCallbacksCannotCaptureReplacementBinding()
        {
            ChessContentView first = CreateChess("first", "piece-a", out Transform firstPiece,
                                                 out InteractableUnityEventWrapper firstWrapper,
                                                 out ChessContentModel firstModel);
            ChessContentView second = CreateChess("second", "piece-a", out Transform secondPiece,
                                                  out InteractableUnityEventWrapper secondWrapper,
                                                  out ChessContentModel secondModel);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            var firstBinding = new FreeformSceneChessPieceBinding("piece-a", firstPiece, firstWrapper);
            var secondBinding = new FreeformSceneChessPieceBinding("piece-a", secondPiece, secondWrapper);
            var secondChanges = 0;
            secondModel.Changed += _ => ++secondChanges;
            var reconfigured = false;
            firstWrapper.WhenSelect.AddListener(() =>
            {
                if (reconfigured)
                    return;
                reconfigured = true;
                Assert.That(input.Configure(second, new[] { secondBinding }, out string nestedError),
                            Is.True, nestedError);
            });
            Assert.That(input.Configure(first, new[] { firstBinding }, out string error), Is.True, error);

            firstWrapper.WhenSelect.Invoke();
            InvokePrivate(input, "LateUpdate");
            Assert.That(reconfigured, Is.True);
            Assert.That(secondChanges, Is.Zero,
                        "The old listener in UnityEvent's invocation snapshot must not select same-ID replacement input.");

            Assert.That(second.TryBind(firstModel, out error), Is.True, error);
            Assert.That(input.CapturePiece("piece-a", out _), Is.False,
                        "A configured input must reject a view whose model was replaced behind its back.");
        }

        [Test]
        public void ChessReleaseSnapshotsStopAfterSameViewModelReplacementAndRebuildConfigureCandidate()
        {
            ChessContentView source = CreateChessPair("source", out Transform sourceA,
                                                       out InteractableUnityEventWrapper sourceWrapperA,
                                                       out Transform sourceB,
                                                       out InteractableUnityEventWrapper sourceWrapperB,
                                                       out ChessContentModel sourceModel);
            ChessContentView candidate = CreateChess("candidate", "piece-a", out Transform candidatePiece,
                                                      out InteractableUnityEventWrapper candidateWrapper,
                                                      out ChessContentModel candidateModel);
            SpatialBlendObject replacementOwner = CreateOwner("replacement-owner");
            Assert.That(_catalog.TryCreateChess(replacementOwner, "replacement-model",
                                                 new[] { new ChessPiecePose("piece-a", Vector3.zero,
                                                                            Quaternion.identity),
                                                         new ChessPiecePose("piece-b", Vector3.zero,
                                                                            Quaternion.identity) },
                                                 out ChessContentModel sourceReplacement, out string error),
                        Is.True, error);
            SpatialBlendObject candidateReplacementOwner = CreateOwner("candidate-replacement-owner");
            Assert.That(_catalog.TryCreateChess(candidateReplacementOwner, "candidate-replacement-model",
                                                 new[] { new ChessPiecePose("piece-a", Vector3.zero,
                                                                            Quaternion.identity) },
                                                 out ChessContentModel candidateReplacement, out error),
                        Is.True, error);
            var inputObject = new GameObject("chess-input");
            _created.Add(inputObject);
            FreeformSceneChessInput input = inputObject.AddComponent<FreeformSceneChessInput>();
            var sourceBindings = new[]
            {
                new FreeformSceneChessPieceBinding("piece-a", sourceA, sourceWrapperA),
                new FreeformSceneChessPieceBinding("piece-b", sourceB, sourceWrapperB)
            };
            var candidateBinding = new FreeformSceneChessPieceBinding("piece-a", candidatePiece,
                                                                        candidateWrapper);
            Assert.That(input.Configure(source, sourceBindings, out error), Is.True, error);
            SetBoardLocalPose(source, sourceA, new Vector3(.2f, 0f, 0f), Quaternion.identity);
            SetBoardLocalPose(source, sourceB, new Vector3(-.25f, 0f, 0f), Quaternion.identity);
            sourceWrapperA.WhenSelect.Invoke();
            sourceWrapperB.WhenSelect.Invoke();
            var sourceRebound = false;
            Action<FreeformContentModel> rebindSourceDuringRelease = _ =>
            {
                if (sourceRebound)
                    return;
                sourceRebound = true;
                Assert.That(source.TryBind(sourceReplacement, out string nestedError), Is.True, nestedError);
            };
            sourceModel.Changed += rebindSourceDuringRelease;
            input.ReleaseInput();
            sourceModel.Changed -= rebindSourceDuringRelease;
            Assert.That(sourceRebound, Is.True);
            Assert.That(CountChangedPiecePositions(sourceModel), Is.EqualTo(1),
                        "After first final capture rebinds the same view, later selected bindings must not capture by ID.");

            Assert.That(input.Configure(source, sourceBindings, out error), Is.True, error);
            // Recreate a selected source state so Configure(candidate) must final-capture it.
            SetBoardLocalPose(source, sourceA, new Vector3(.1f, 0f, 0f), Quaternion.identity);
            sourceWrapperA.WhenSelect.Invoke();
            var candidateRebound = false;
            Action<FreeformContentModel> rebindCandidateDuringRelease = _ =>
            {
                if (candidateRebound)
                    return;
                candidateRebound = true;
                Assert.That(candidate.TryBind(candidateReplacement, out string nestedError), Is.True, nestedError);
            };
            sourceReplacement.Changed += rebindCandidateDuringRelease;
            Assert.That(input.Configure(candidate, new[] { candidateBinding }, out error), Is.True, error);
            sourceReplacement.Changed -= rebindCandidateDuringRelease;
            Assert.That(candidateRebound, Is.True);
            SetBoardLocalPose(candidate, candidatePiece, new Vector3(.15f, 0f, 0f), Quaternion.identity);
            Assert.That(input.CapturePiece("piece-a", out error), Is.True, error);
            Assert.That(CountChangedPiecePositions(candidateModel), Is.Zero,
                        "Configure must not retain the pre-release candidate model after it was rebound.");
            Assert.That(CountChangedPiecePositions(candidateReplacement), Is.EqualTo(1));
        }

        private WhiteboardContentView CreateWhiteboard(string name, Color background,
                                                        out WhiteboardContentModel model)
        {
            SpatialBlendObject owner = CreateOwner(name + "-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, name + "-content",
                                                      new WhiteboardContentConfiguration(8, 8, background),
                                                      out model, out string error), Is.True, error);
            var value = new GameObject(name + "-view");
            _created.Add(value);
            Renderer renderer = value.AddComponent<MeshRenderer>();
            Shader shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            Material material = new Material(shader);
            renderer.sharedMaterial = material;
            _created.Add(material);
            WhiteboardContentView view = value.AddComponent<WhiteboardContentView>();
            Assert.That(view.Configure(renderer, out error), Is.True, error);
            Assert.That(view.TryBind(model, out error), Is.True, error);
            return view;
        }

        private ChessContentView CreateChess(string name, string pieceId, out Transform piece,
                                             out InteractableUnityEventWrapper wrapper,
                                             out ChessContentModel model)
        {
            var board = new GameObject(name + "-board");
            _created.Add(board);
            board.transform.SetPositionAndRotation(new Vector3(3f, 2f, -1f), Quaternion.Euler(0f, 29f, 0f));
            board.transform.localScale = new Vector3(1.5f, 1f, .75f);
            var pieceObject = new GameObject(name + "-piece");
            _created.Add(pieceObject);
            piece = pieceObject.transform;
            wrapper = pieceObject.AddComponent<InteractableUnityEventWrapper>();
            EnsureWrapperEvents(wrapper);
            ChessContentView view = board.AddComponent<ChessContentView>();
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding(pieceId, piece) }, out string error),
                        Is.True, error);
            SpatialBlendObject owner = CreateOwner(name + "-owner");
            Assert.That(_catalog.TryCreateChess(owner, name + "-content",
                                                 new[] { new ChessPiecePose(pieceId, Vector3.zero,
                                                                            Quaternion.identity) },
                                                 out model, out error), Is.True, error);
            Assert.That(view.TryBind(model, out error), Is.True, error);
            return view;
        }

        private ChessContentView CreateChessPair(string name, out Transform firstPiece,
                                                  out InteractableUnityEventWrapper firstWrapper,
                                                  out Transform secondPiece,
                                                  out InteractableUnityEventWrapper secondWrapper,
                                                  out ChessContentModel model)
        {
            var board = new GameObject(name + "-board");
            _created.Add(board);
            var first = new GameObject(name + "-piece-a");
            _created.Add(first);
            firstPiece = first.transform;
            firstWrapper = first.AddComponent<InteractableUnityEventWrapper>();
            EnsureWrapperEvents(firstWrapper);
            var second = new GameObject(name + "-piece-b");
            _created.Add(second);
            secondPiece = second.transform;
            secondWrapper = second.AddComponent<InteractableUnityEventWrapper>();
            EnsureWrapperEvents(secondWrapper);
            ChessContentView view = board.AddComponent<ChessContentView>();
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("piece-a", firstPiece),
                                               new ChessPieceViewBinding("piece-b", secondPiece) },
                                       out string error), Is.True, error);
            SpatialBlendObject owner = CreateOwner(name + "-owner");
            Assert.That(_catalog.TryCreateChess(owner, name + "-content",
                                                 new[] { new ChessPiecePose("piece-a", Vector3.zero,
                                                                            Quaternion.identity),
                                                         new ChessPiecePose("piece-b", Vector3.zero,
                                                                            Quaternion.identity) },
                                                 out model, out error), Is.True, error);
            Assert.That(view.TryBind(model, out error), Is.True, error);
            return view;
        }

        private ColorSelectorMotif CreateSelector(string name, Color color, int radius)
        {
            var value = new GameObject(name);
            _created.Add(value);
            value.AddComponent<BoxCollider>();
            ColorSelectorMotif selector = value.AddComponent<ColorSelectorMotif>();
            SetField(selector, "selectionColor", color);
            SetField(selector, "selectionBrushRadius", radius);
            return selector;
        }

        private SpatialBlendObject CreateOwner(string name)
        {
            var value = new GameObject(name);
            _created.Add(value);
            return value.AddComponent<SpatialBlendObject>();
        }

        private static void SetPointerObservation(PointerHandlerMotif pointer, Vector3 hitPoint,
                                                  bool hovered, bool selected, bool validHit)
        {
            SetProperty(pointer, "HitPoint", hitPoint);
            SetProperty(pointer, "IsHovered", hovered);
            SetProperty(pointer, "IsSelected", selected);
            SetProperty(pointer, "HasValidHit", validHit);
        }

        private static void SetBoardLocalPose(ChessContentView view, Transform piece,
                                              Vector3 localPosition, Quaternion localRotation)
        {
            Transform board = view.BoardTransform;
            piece.SetPositionAndRotation(board.TransformPoint(localPosition), board.rotation * localRotation);
        }

        private static void AssertPose(ChessContentModel model, string pieceId, Vector3 position,
                                       Quaternion rotation)
        {
            foreach (ChessPiecePose pose in model.CopyPieces())
            {
                if (pose.PieceId != pieceId)
                    continue;
                Assert.That(Vector3.Distance(pose.BoardLocalPosition, position), Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(pose.BoardLocalRotation, rotation), Is.LessThan(.01f));
                return;
            }
            Assert.Fail("Expected chess piece was not present in the actual model.");
        }

        private static void AssertViewPose(ChessContentView view, Transform piece, Vector3 localPosition,
                                           Quaternion localRotation)
        {
            Transform board = view.BoardTransform;
            Assert.That(Vector3.Distance(piece.position, board.TransformPoint(localPosition)), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(piece.rotation, board.rotation * localRotation), Is.LessThan(.01f));
        }

        private static int CountChangedPiecePositions(ChessContentModel model)
        {
            var changed = 0;
            foreach (ChessPiecePose pose in model.CopyPieces())
                if (pose.BoardLocalPosition.sqrMagnitude > .000001f)
                    ++changed;
            return changed;
        }

        private static void EnsureWrapperEvents(InteractableUnityEventWrapper wrapper)
        {
            EnsureUnityEvent(wrapper, "_whenSelect");
            EnsureUnityEvent(wrapper, "_whenUnselect");
        }

        private static void EnsureUnityEvent(Component component, string fieldName)
        {
            FieldInfo field = component.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            if (field.GetValue(component) == null)
                field.SetValue(component, new UnityEvent());
        }

        private static void SetField(Component component, string fieldName, object value)
        {
            FieldInfo field = component.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(component, value);
        }

        private static T GetField<T>(Component component, string fieldName)
        {
            FieldInfo field = component.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(component);
        }

        private static void SetProperty(Component component, string propertyName, object value)
        {
            PropertyInfo property = component.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            MethodInfo setter = property.GetSetMethod(true);
            Assert.That(setter, Is.Not.Null);
            setter.Invoke(component, new[] { value });
        }

        private static void InvokePrivate(Component component, string methodName)
        {
            MethodInfo method = component.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(component, null);
        }
    }
}
#endif
