// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpatialBlend.Freeform;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FreeformLocalAppTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int index = _created.Count - 1; index >= 0; --index)
                if (_created[index] != null)
                    Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void ExplicitCommandsShareContentAcrossIndependentViewsAndSupportRemovalRecreation()
        {
            FreeformLocalApp app = CreateApp("local-app");
            Assert.That(app.Initialize(out string error), Is.True, error);
            Assert.That(app.TryGetWhiteboard(out WhiteboardContentModel firstBoard), Is.True);
            Assert.That(app.TryGetChess(out ChessContentModel chess), Is.True);
            AssertStandardInitialChessLayout(chess);
            Assert.That(app.LocalWorld, Is.Not.Null);
            Assert.That(app.LocalWorld.IsInitialized, Is.True);
            Assert.That(app.SourceWhiteboardObject.IsRegistered, Is.True);
            Assert.That(app.ReceiverWhiteboardObject.IsRegistered, Is.True);
            Assert.That(app.SourceChessObject.IsRegistered, Is.True);
            Assert.That(app.ReceiverChessObject.IsRegistered, Is.True);
            Assert.That(app.SourceWhiteboardView.transform.position,
                        Is.Not.EqualTo(app.ReceiverWhiteboardView.transform.position));
            Assert.That(app.SourceChessView.transform.position,
                        Is.Not.EqualTo(app.ReceiverChessView.transform.position));

            Assert.That(app.DrawWhiteboardUv(new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                                              Color.red, 0, out error), Is.True, error);
            Assert.That(app.SourceWhiteboardView.Texture.GetPixel(64, 64), Is.EqualTo(Color.red));
            Assert.That(app.ReceiverWhiteboardView.Texture.GetPixel(64, 64), Is.EqualTo(Color.red));
            Assert.That(app.SourceChessView.Model, Is.SameAs(chess));
            Assert.That(app.ReceiverChessView.Model, Is.SameAs(chess));
            Assert.That(app.TrySelectChessPiece("white-knight-b1", out error), Is.True, error);
            Assert.That(app.SelectedChessPieceId, Is.EqualTo("white-knight-b1"));
            Assert.That(app.TryMoveSelectedChessPiece(new Vector3(.11f, 20f, -.08f), out error), Is.True, error);
            Assert.That(chess.TryGetPiecePose("white-knight-b1", out ChessPiecePose movedPose), Is.True);
            AssertBoardLocal(app.SourceChessView, "white-knight-b1", movedPose.BoardLocalPosition);
            AssertBoardLocal(app.ReceiverChessView, "white-knight-b1", movedPose.BoardLocalPosition);

            Assert.That(app.RemoveContent(FreeformLocalApp.DefaultWhiteboardContentId), Is.True);
            Assert.That(firstBoard.IsLive, Is.False);
            Assert.That(app.SourceWhiteboardView.Model, Is.Null);
            Assert.That(app.ReceiverWhiteboardView.Model, Is.Null);
            Assert.That(app.CreateDefaultContent(out error), Is.True, error);
            Assert.That(app.TryGetWhiteboard(out WhiteboardContentModel replacement), Is.True);
            Assert.That(replacement, Is.Not.SameAs(firstBoard));
            Assert.That(app.SourceWhiteboardView.Model, Is.SameAs(replacement));
            Assert.That(app.RemoveContent(FreeformLocalApp.DefaultChessContentId), Is.True);
            Assert.That(chess.IsLive, Is.False);
            Assert.That(app.SourceChessView.Model, Is.Null);
            Assert.That(app.ReceiverChessView.Model, Is.Null);
            Assert.That(app.CreateDefaultContent(out error), Is.True, error);
            Assert.That(app.TryGetChess(out ChessContentModel replacementChess), Is.True);
            Assert.That(replacementChess, Is.Not.SameAs(chess));
            AssertStandardInitialChessLayout(replacementChess);
            Assert.That(app.SourceChessView.Model, Is.SameAs(replacementChess));
            Assert.That(app.ReceiverChessView.Model, Is.SameAs(replacementChess));

            app.Teardown();
            Assert.That(app.IsInitialized, Is.False);
            Assert.That(app.WorkspaceRoot, Is.Null);
            Assert.That(replacement.IsLive, Is.False);
            Assert.That(replacementChess.IsLive, Is.False);
        }

        [Test]
        public void ExplicitExternalCameraIsReadOnlyAndSurvivesTeardown()
        {
            var cameraObject = new GameObject("external-camera");
            _created.Add(cameraObject);
            Camera externalCamera = cameraObject.AddComponent<Camera>();
            externalCamera.transform.SetPositionAndRotation(new Vector3(3f, 4f, 5f),
                                                            Quaternion.Euler(10f, 20f, 30f));
            Vector3 originalPosition = externalCamera.transform.position;
            Quaternion originalRotation = externalCamera.transform.rotation;
            FreeformLocalApp app = CreateApp("external-camera-app");
            Assert.That(app.ConfigureCamera(externalCamera, false, out string error), Is.True, error);
            Assert.That(app.Initialize(out error), Is.True, error);
            Assert.That(app.ActiveCamera, Is.SameAs(externalCamera));
            Assert.That(app.OwnsActiveCamera, Is.False);
            Assert.That(externalCamera.transform.position, Is.EqualTo(originalPosition));
            Assert.That(externalCamera.transform.rotation, Is.EqualTo(originalRotation));

            app.Teardown();
            Assert.That(externalCamera, Is.Not.Null);
            Assert.That(externalCamera.transform.position, Is.EqualTo(originalPosition));
            Assert.That(externalCamera.transform.rotation, Is.EqualTo(originalRotation));
        }

        [Test]
        public void InitializationFailsActionablyWhenNoCameraMayBeCreated()
        {
            FreeformLocalApp app = CreateApp("no-camera-app");
            Assert.That(app.ConfigureCamera(null, false, out string error), Is.True, error);
            Assert.That(app.Initialize(out error), Is.False);
            StringAssert.Contains("Assign a camera", error);
            Assert.That(app.WorkspaceRoot, Is.Null);
            Assert.That(app.IsInitialized, Is.False);
        }

        private FreeformLocalApp CreateApp(string name)
        {
            var value = new GameObject(name);
            _created.Add(value);
            return value.AddComponent<FreeformLocalApp>();
        }

        private static void AssertBoardLocal(ChessContentView view, string pieceId, Vector3 expected)
        {
            Assert.That(view.TryGetPieceTransform(pieceId, out Transform piece), Is.True);
            Vector3 actual = view.BoardTransform.InverseTransformPoint(piece.position);
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0001f));
        }

        private static void AssertStandardInitialChessLayout(ChessContentModel chess)
        {
            Assert.That(chess.PieceCount, Is.EqualTo(32));
            string[] types = { "rook", "knight", "bishop", "queen", "king", "bishop", "knight", "rook" };
            for (int file = 0; file < 8; ++file)
            {
                string name = ((char)('a' + file)).ToString();
                AssertInitialPose(chess, "white-" + types[file] + "-" + name + "1", file, 0);
                AssertInitialPose(chess, "white-pawn-" + name + "2", file, 1);
                AssertInitialPose(chess, "black-pawn-" + name + "7", file, 6);
                AssertInitialPose(chess, "black-" + types[file] + "-" + name + "8", file, 7);
            }
        }

        private static void AssertInitialPose(ChessContentModel chess, string pieceId, int file, int rank)
        {
            Assert.That(chess.TryGetPiecePose(pieceId, out ChessPiecePose pose), Is.True, pieceId);
            Vector3 expected = new Vector3((file + .5f) * (1.5f / 8f) - .75f, .16f,
                                           (rank + .5f) * (1.5f / 8f) - .75f);
            Assert.That(Vector3.Distance(pose.BoardLocalPosition, expected), Is.LessThan(.0001f), pieceId);
            if (pieceId.StartsWith("white-"))
                Assert.That(pose.BoardLocalRotation, Is.EqualTo(Quaternion.identity));
            else
                Assert.That(Quaternion.Angle(pose.BoardLocalRotation, Quaternion.Euler(0f, 180f, 0f)),
                            Is.LessThan(.001f));
        }
    }

    [TestFixture]
    [Category("FoundationPlayMode")]
    [Category("FoundationNative")]
    public sealed class FreeformLocalAppPlayModeTests
    {
        [UnityTearDown]
        public IEnumerator LeavePlayModeAfterEachFixture()
        {
            if (Application.isPlaying)
                yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator StartDisableAndReenableManageOnlyTheOwnedWorkspace()
        {
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying, Is.True);
            var appObject = new GameObject("playmode-local-app");
            FreeformLocalApp app = appObject.AddComponent<FreeformLocalApp>();
            yield return null;
            Assert.That(app.IsInitialized, Is.True, app.LastError);
            Assert.That(app.WorkspaceRoot, Is.Not.Null);
            appObject.SetActive(false);
            yield return null;
            Assert.That(app.IsInitialized, Is.False);
            Assert.That(app.WorkspaceRoot, Is.Null);
            appObject.SetActive(true);
            yield return null;
            Assert.That(app.IsInitialized, Is.True, app.LastError);
            Object.Destroy(appObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ScreenPointerEntryPointProjectsReceiverWhiteboardAndChess()
        {
            yield return new EnterPlayMode();
            var appObject = new GameObject("screen-pointer-local-app");
            FreeformLocalApp app = appObject.AddComponent<FreeformLocalApp>();
            yield return null;
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
            Assert.That(app.IsInitialized, Is.True, app.LastError);
            Camera camera = app.ActiveCamera;
            Assert.That(camera, Is.Not.Null);
            Assert.That(app.TryGetWhiteboard(out WhiteboardContentModel whiteboard), Is.True);
            Assert.That(app.TryGetChess(out ChessContentModel chess), Is.True);

            Vector2 whiteStart = ScreenPoint(camera, app.ReceiverWhiteboardView.transform.TransformPoint(
                new Vector3(-.2f, 0f, 0f)));
            Vector2 whiteEnd = ScreenPoint(camera, app.ReceiverWhiteboardView.transform.TransformPoint(
                new Vector3(.2f, 0f, 0f)));
            Color32[] before = whiteboard.CopyPixels();
            Assert.That(app.TryHandleDesktopPointer(whiteStart, FreeformDesktopPointerPhase.Press, true,
                                                    out string error), Is.False, error);
            Assert.That(whiteboard.CopyPixels(), Is.EqualTo(before), "UI interception must not start a stroke.");
            Assert.That(app.DesktopPointerTarget, Is.EqualTo(FreeformDesktopPointerTarget.None));
            Assert.That(app.TryHandleDesktopPointer(whiteStart, FreeformDesktopPointerPhase.Press, false,
                                                    out error), Is.True, error);
            Assert.That(app.DesktopPointerTarget, Is.EqualTo(FreeformDesktopPointerTarget.Whiteboard));
            Assert.That(app.TryHandleDesktopPointer(whiteEnd, FreeformDesktopPointerPhase.Drag, false,
                                                    out error), Is.True, error);
            Assert.That(app.TryHandleDesktopPointer(whiteEnd, FreeformDesktopPointerPhase.Release, false,
                                                    out error), Is.True, error);
            Assert.That(app.DesktopPointerTarget, Is.EqualTo(FreeformDesktopPointerTarget.None));
            AssertSynchronizedBluePixel(whiteboard, app.SourceWhiteboardView.Texture,
                                        app.ReceiverWhiteboardView.Texture);

            Assert.That(app.ReceiverChessView.TryGetPieceTransform("white-knight-b1", out Transform knight),
                        Is.True);
            Vector2 knightScreen = ScreenPoint(camera, knight.position);
            Assert.That(app.TryHandleDesktopPointer(knightScreen, FreeformDesktopPointerPhase.Press, false,
                                                    out error), Is.True, error);
            Assert.That(app.DesktopPointerTarget, Is.EqualTo(FreeformDesktopPointerTarget.Chess));
            Assert.That(app.TryHandleDesktopPointer(knightScreen, FreeformDesktopPointerPhase.Release, false,
                                                    out error), Is.True, error);
            Assert.That(app.DesktopPointerTarget, Is.EqualTo(FreeformDesktopPointerTarget.None));
            Assert.That(app.SelectedChessPieceId, Is.EqualTo("white-knight-b1"));

            Transform destinationTile = FindVisibleReceiverTile(camera, app.ReceiverChessView);
            Vector2 destinationScreen = ScreenPoint(camera, destinationTile.position);
            Vector3 expectedLocal = destinationTile.localPosition;
            Assert.That(app.TryHandleDesktopPointer(destinationScreen, FreeformDesktopPointerPhase.Press, false,
                                                    out error), Is.True, error);
            Assert.That(app.TryHandleDesktopPointer(destinationScreen, FreeformDesktopPointerPhase.Release, false,
                                                    out error), Is.True, error);
            Assert.That(chess.TryGetPiecePose("white-knight-b1", out ChessPiecePose moved), Is.True);
            Assert.That(moved.BoardLocalPosition.x, Is.EqualTo(expectedLocal.x).Within(.0001f));
            Assert.That(moved.BoardLocalPosition.z, Is.EqualTo(expectedLocal.z).Within(.0001f));
            AssertBoardLocal(app.SourceChessView, "white-knight-b1", moved.BoardLocalPosition);
            AssertBoardLocal(app.ReceiverChessView, "white-knight-b1", moved.BoardLocalPosition);

            Object.Destroy(appObject);
            yield return null;
        }

        private static void AssertBoardLocal(ChessContentView view, string pieceId, Vector3 expected)
        {
            Assert.That(view.TryGetPieceTransform(pieceId, out Transform piece), Is.True);
            Vector3 actual = view.BoardTransform.InverseTransformPoint(piece.position);
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0001f));
        }

        private static Vector2 ScreenPoint(Camera camera, Vector3 worldPoint)
        {
            Vector3 screen = camera.WorldToScreenPoint(worldPoint);
            Assert.That(screen.z, Is.GreaterThan(0f), "Target must be in front of the desktop camera.");
            Assert.That(screen.x, Is.InRange(0f, (float)Screen.width));
            Assert.That(screen.y, Is.InRange(0f, (float)Screen.height));
            return new Vector2(screen.x, screen.y);
        }

        private static Transform FindVisibleReceiverTile(Camera camera, ChessContentView view)
        {
            for (int rank = 2; rank < 6; ++rank)
                for (int file = 0; file < 8; ++file)
                {
                    Transform tile = view.transform.Find("Square " + (char)('A' + file) + (rank + 1));
                    Vector3 screen = camera.WorldToScreenPoint(tile.position);
                    if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width ||
                        screen.y < 0f || screen.y > Screen.height)
                        continue;
                    RaycastHit[] hits = Physics.RaycastAll(camera.ScreenPointToRay(screen), 100f);
                    RaycastHit nearest = default;
                    bool found = false;
                    foreach (RaycastHit hit in hits)
                        if (!found || hit.distance < nearest.distance)
                        {
                            nearest = hit;
                            found = true;
                        }
                    if (found && nearest.collider != null && nearest.collider.transform == tile)
                        return tile;
                }
            Assert.Fail("Expected at least one visible empty receiver chess tile.");
            return null;
        }

        private static void AssertSynchronizedBluePixel(WhiteboardContentModel model, Texture2D source,
                                                          Texture2D receiver)
        {
            Color32 blue = Color.blue;
            Color32[] pixels = model.CopyPixels();
            for (int index = 0; index < pixels.Length; ++index)
                if (pixels[index].Equals(blue))
                {
                    int x = index % model.Width;
                    int y = index / model.Width;
                    Assert.That((Color32)source.GetPixel(x, y), Is.EqualTo(blue));
                    Assert.That((Color32)receiver.GetPixel(x, y), Is.EqualTo(blue));
                    return;
                }
            Assert.Fail("Screen-coordinate whiteboard input did not change a model pixel.");
        }
    }

    [TestFixture]
    [Category("FoundationGraphics")]
    [Category("FoundationNative")]
    public sealed class FreeformLocalAppGraphicsTests
    {
        private const string OverviewPngName = "freeform-local-graphics-overview.png";
        // tools/run-foundation-tests.sh sets this to its temporary verification project; without it the overview goes
        // to the editor's temporary cache, never into the project (or repository) root.
        private const string ArtifactsDirVariable = "SPATIALBLEND_FOUNDATION_ARTIFACTS_DIR";

        internal static string OverviewPngPath()
        {
            string directory = System.Environment.GetEnvironmentVariable(ArtifactsDirVariable);
            if (string.IsNullOrWhiteSpace(directory))
                directory = Application.temporaryCachePath;
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, OverviewPngName);
        }

        [UnityTearDown]
        public IEnumerator LeavePlayModeAfterEachFixture()
        {
            if (Application.isPlaying)
                yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RenderedReceiverWhiteboardStrokeAndChessAreVisible()
        {
            // -nographics has no device to render with; tools/run-foundation-tests.sh excludes the category there and its
            // --graphics verifier fails a skipped case.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("FoundationGraphics requires a non-null graphics device (the editor ran with -nographics).");
            yield return new EnterPlayMode();
            var appObject = new GameObject("graphics-local-app");
            FreeformLocalApp app = appObject.AddComponent<FreeformLocalApp>();
            yield return null;
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
            Assert.That(app.IsInitialized, Is.True, app.LastError);
            Camera camera = app.ActiveCamera;
            Assert.That(camera, Is.Not.Null);

            Vector2 strokeStart = ScreenPoint(camera, app.ReceiverWhiteboardView.transform.TransformPoint(
                new Vector3(-.22f, 0f, 0f)));
            Vector2 strokeEnd = ScreenPoint(camera, app.ReceiverWhiteboardView.transform.TransformPoint(
                new Vector3(.22f, 0f, 0f)));
            Assert.That(app.TryHandleDesktopPointer(strokeStart, FreeformDesktopPointerPhase.Press, false,
                                                    out string error), Is.True, error);
            Assert.That(app.TryHandleDesktopPointer(strokeEnd, FreeformDesktopPointerPhase.Drag, false,
                                                    out error), Is.True, error);
            Assert.That(app.TryHandleDesktopPointer(strokeEnd, FreeformDesktopPointerPhase.Release, false,
                                                    out error), Is.True, error);

            const int width = 1024;
            const int height = 768;
            RenderTexture originalTarget = camera.targetTexture;
            RenderTexture originalActive = RenderTexture.active;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var capture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                target.Create();
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                capture.Apply(false, false);
                string overview = OverviewPngPath();
                File.WriteAllBytes(overview, capture.EncodeToPNG());
                Debug.Log("[FreeformLocalAppGraphicsTests] OVERVIEW_PNG=" + overview);

                AssertProjectedRegion(capture, camera.WorldToViewportPoint(
                    app.ReceiverWhiteboardView.transform.TransformPoint(Vector3.zero)),
                    pixel => pixel.b > 0.55f && pixel.b > pixel.r * 1.5f && pixel.b > pixel.g * 1.5f,
                    "the rendered blue whiteboard stroke");
                AssertProjectedRegion(capture, camera.WorldToViewportPoint(
                    app.ReceiverChessView.transform.Find("Square C4").position),
                    pixel => pixel.r > .18f && pixel.r > pixel.g + .03f && pixel.g > pixel.b + .02f,
                    "a rendered chess-board tile");
                Assert.That(app.ReceiverChessView.TryGetPieceTransform("white-knight-b1", out Transform knight),
                            Is.True);
                AssertProjectedRegion(capture, camera.WorldToViewportPoint(knight.position),
                    pixel => pixel.r > .65f && pixel.g > .65f && pixel.b > .5f &&
                             Mathf.Abs(pixel.r - pixel.g) < .16f && Mathf.Abs(pixel.g - pixel.b) < .2f,
                    "a rendered white chess piece");
            }
            finally
            {
                camera.targetTexture = originalTarget;
                RenderTexture.active = originalActive;
                if (target != null)
                {
                    target.Release();
                    Object.Destroy(target);
                }
                if (capture != null)
                    Object.Destroy(capture);
            }
            Object.Destroy(appObject);
            yield return null;
        }

        private static Vector2 ScreenPoint(Camera camera, Vector3 worldPoint)
        {
            Vector3 screen = camera.WorldToScreenPoint(worldPoint);
            Assert.That(screen.z, Is.GreaterThan(0f));
            Assert.That(screen.x, Is.InRange(0f, (float)Screen.width));
            Assert.That(screen.y, Is.InRange(0f, (float)Screen.height));
            return new Vector2(screen.x, screen.y);
        }

        private static void AssertProjectedRegion(Texture2D image, Vector3 viewportPoint,
                                                  System.Predicate<Color> matches, string label)
        {
            Assert.That(viewportPoint.z, Is.GreaterThan(0f), label + " must be in front of the camera.");
            int centerX = Mathf.RoundToInt(viewportPoint.x * (image.width - 1));
            int centerY = Mathf.RoundToInt(viewportPoint.y * (image.height - 1));
            for (int y = Mathf.Max(0, centerY - 28); y <= Mathf.Min(image.height - 1, centerY + 28); ++y)
                for (int x = Mathf.Max(0, centerX - 28); x <= Mathf.Min(image.width - 1, centerX + 28); ++x)
                    if (matches(image.GetPixel(x, y)))
                        return;
            Assert.Fail("Expected " + label + " near its projected camera position.");
        }
    }
}
#endif
