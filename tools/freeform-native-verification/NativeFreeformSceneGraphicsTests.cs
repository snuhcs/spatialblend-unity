// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpatialBlend.Freeform;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Full-project visual evidence for the original authored Freeform scene.
    /// This is an EditMode offscreen render only: it is not headset-camera,
    /// PlayMode, hardware, or two-user-avatar acceptance evidence.
    /// </summary>
    [TestFixture]
    [Category("NativeFreeformGraphics")]
    public sealed class NativeFreeformSceneGraphicsTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private const string OverviewPngName = "freeform-scene-local-graphics-overview.png";
        private static readonly Color ClearColor = new Color(.015f, .02f, .03f, 1f);
        private IDisposable _worldAwakeLease;

        [SetUp]
        public void SupplyRealLocalWorldLifecycleInEditMode() =>
            _worldAwakeLease = NativeManagerIntegrationTests.InstallLocalWorldAwakeForEditMode();

        [TearDown]
        public void ReleaseLocalWorldLifecycleHook()
        {
            _worldAwakeLease?.Dispose();
            _worldAwakeLease = null;
        }

        [Test]
        public void OriginalFreeformScene_LocalBackend_RendersAuthoredWhiteboardAndChess()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null),
                        "NativeFreeformGraphics requires a non-null graphics device; it cannot run with -nographics.");

            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool openedHere = !scene.isLoaded;
            if (openedHere)
                scene = NativeManagerIntegrationTests.OpenOriginalFreeformSceneForNativeTest();

            FreeformSceneLocalBackend backend = null;
            FreeformCollaborationApp app = null;
            RenderTexture target = null;
            Texture2D capture = null;
            Texture2D baseline = null;
            GameObject whiteboardCameraObject = null;
            GameObject chessCameraObject = null;
            RenderTexture originalActive = RenderTexture.active;
            try
            {
                app = FindSingle<FreeformCollaborationApp>(scene);
                backend = FindSingle<FreeformSceneLocalBackend>(scene);
                Assert.That(app.UsesFoundationLocal, Is.True,
                            "The original scene must select the serialized Foundation Local profile.");
                Assert.That(app.FoundationLocalBackend, Is.SameAs(backend),
                            "The original profile entrypoint must own the exact declared local backend.");
                FreeformSceneWhiteboardDefinition whiteboardDefinition =
                    FindSingle<FreeformSceneWhiteboardDefinition>(scene);
                FreeformSceneChessDefinition chessDefinition =
                    FindSingle<FreeformSceneChessDefinition>(scene);
                Assert.That(whiteboardDefinition.SurfaceRenderer, Is.Not.Null,
                            "The original scene must provide its serialized whiteboard surface renderer.");
                Assert.That(whiteboardDefinition.SurfaceRenderer.sharedMaterial, Is.Not.Null,
                            "The original whiteboard renderer must retain an authored material.");
                Assert.That(chessDefinition.Pieces, Is.Not.Null,
                            "The original scene must provide authored chess piece bindings.");
                Assert.That(chessDefinition.Pieces.Length, Is.EqualTo(32),
                            "The original scene must provide all 32 authored chess piece bindings.");
                Assert.That(CountRenderers(chessDefinition.MovableRoot), Is.GreaterThan(1),
                            "The original chess root must retain its board/piece renderers.");

                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                Assert.That(backend.IsRunning, Is.True, backend.LastError);
                Assert.That(backend.LocalWorld, Is.Not.Null);
                Assert.That(backend.LocalWorld.IsInitialized, Is.True,
                            "The local backend must have initialized its actual native local world.");

                Assert.That(backend.ContentCoordinator.Catalog.TryGetWhiteboard(
                    FreeformSceneLocalBackend.WhiteboardContentId, out WhiteboardContentModel whiteboardModel),
                    Is.True);
                Assert.That(backend.ContentCoordinator.Catalog.TryGetChess(
                    FreeformSceneLocalBackend.ChessContentId, out ChessContentModel chessModel), Is.True);
                WhiteboardContentView whiteboardView = whiteboardDefinition.SurfaceRenderer.GetComponent<WhiteboardContentView>();
                ChessContentView chessView = chessDefinition.MovableRoot.GetComponent<ChessContentView>();
                Assert.That(whiteboardView, Is.Not.Null);
                Assert.That(chessView, Is.Not.Null);
                Assert.That(whiteboardView.Model, Is.SameAs(whiteboardModel));
                Assert.That(chessView.Model, Is.SameAs(chessModel));

                const int width = 1280;
                const int height = 720;
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.Create();
                capture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                baseline = new Texture2D(width, height, TextureFormat.RGBA32, false);
                whiteboardCameraObject = CreateTemporaryCamera(scene, "Native Freeform Whiteboard Overview", new Rect(0f, 0f, .5f, 1f),
                                                                CameraClearFlags.SolidColor);
                chessCameraObject = CreateTemporaryCamera(scene, "Native Freeform Chess Overview", new Rect(.5f, 0f, .5f, 1f),
                                                           CameraClearFlags.Depth);
                Camera whiteboardCamera = whiteboardCameraObject.GetComponent<Camera>();
                Camera chessCamera = chessCameraObject.GetComponent<Camera>();
                whiteboardCamera.targetTexture = target;
                chessCamera.targetTexture = target;
                // The authored surface is Unity's built-in Quad, whose visible front
                // faces local -Z. Its Sprite-Unlit material therefore must be viewed
                // from -forward, not from the transform's positive forward side.
                FrameOrthographic(whiteboardCamera, whiteboardDefinition.SurfaceRenderer.bounds,
                                  -whiteboardDefinition.SurfaceRenderer.transform.forward,
                                  whiteboardDefinition.SurfaceRenderer.transform.up, width / (float)height * .5f);
                Vector3 boardDirection = (chessView.BoardTransform.up * .9f - chessView.BoardTransform.forward * .45f).normalized;
                Vector3 boardUp = Vector3.ProjectOnPlane(chessView.BoardTransform.forward, boardDirection).normalized;
                FrameOrthographic(chessCamera, RenderedBounds(chessDefinition.MovableRoot), boardDirection,
                                  boardUp, width / (float)height * .5f);

                whiteboardCamera.Render();
                chessCamera.Render();
                ReadTarget(target, baseline, width, height);

                Color32 strokeColor = new Color(1f, 0f, 1f, 1f);
                Assert.That(whiteboardModel.DrawLine(new Vector2(.2f, .5f), new Vector2(.8f, .5f),
                                                     strokeColor, 24, out error), Is.True, error);
                AssertRasterAndRendererChanged(whiteboardModel, whiteboardView,
                                               whiteboardDefinition.SurfaceRenderer, strokeColor);
                ChessPiecePose sourcePose = chessModel.CopyPieces()[0];
                Assert.That(chessView.TryGetPieceTransform(sourcePose.PieceId, out Transform movedPiece), Is.True);
                Vector3 targetLocalPosition = sourcePose.BoardLocalPosition + new Vector3(.08f, 0f, 0f);
                Assert.That(chessModel.TrySetPiecePose(sourcePose.PieceId, targetLocalPosition,
                                                       sourcePose.BoardLocalRotation, out error), Is.True, error);
                Vector3 expectedWorldPosition = chessView.BoardTransform.TransformPoint(targetLocalPosition);
                Assert.That(Vector3.Distance(movedPiece.position, expectedWorldPosition), Is.LessThan(.0001f),
                            "The authored chess-piece transform must update from the bound model pose.");

                whiteboardCamera.Render();
                chessCamera.Render();
                RenderTexture.active = target;
                ReadTarget(target, capture, width, height);
                Assert.That(HasNonClearPixel(capture), Is.True,
                            "The real authored scene render must contain visible pixels beyond its offscreen clear color.");
                Assert.That(HasNonClearPixel(capture, width / 2, width), Is.True,
                            "The right-hand chess camera viewport must contain rendered authored board/piece pixels.");
                Vector3 strokePoint = whiteboardDefinition.SurfaceRenderer.transform.TransformPoint(Vector3.zero);
                AssertRenderedStrokeDelta(baseline, capture, whiteboardCamera, strokePoint);
                AssertOffStrokeControlStable(baseline, capture, whiteboardCamera,
                                             whiteboardDefinition.SurfaceRenderer.transform.TransformPoint(
                                                 new Vector3(0f, .35f, 0f)));

                string overview = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OverviewPngName);
                File.WriteAllBytes(overview, capture.EncodeToPNG());
                Debug.Log("[NativeFreeformSceneGraphicsTests] OVERVIEW_PNG=" + overview);
            }
            catch
            {
                WriteDiagnosticPng(baseline, "freeform-scene-local-graphics-baseline-diagnostic.png");
                WriteDiagnosticPng(capture, "freeform-scene-local-graphics-after-diagnostic.png");
                throw;
            }
            finally
            {
                RenderTexture.active = originalActive;
                if (whiteboardCameraObject != null)
                    Object.DestroyImmediate(whiteboardCameraObject);
                if (chessCameraObject != null)
                    Object.DestroyImmediate(chessCameraObject);
                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                if (capture != null)
                    Object.DestroyImmediate(capture);
                if (baseline != null)
                    Object.DestroyImmediate(baseline);
                if (app != null)
                    app.StopFoundationLocal();
                else if (backend != null)
                    backend.Stop();
                if (openedHere && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static T FindSingle<T>(Scene scene) where T : Component
        {
            var matches = new List<T>();
            foreach (GameObject root in scene.GetRootGameObjects())
                matches.AddRange(root.GetComponentsInChildren<T>(true));
            Assert.That(matches, Has.Count.EqualTo(1), "Expected exactly one " + typeof(T).Name + " in the original scene.");
            return matches[0];
        }

        private static int CountRenderers(Transform root)
        {
            return root == null ? 0 : root.GetComponentsInChildren<Renderer>(true).Length;
        }

        private static void AssertRasterAndRendererChanged(WhiteboardContentModel model,
                                                           WhiteboardContentView view,
                                                           Renderer renderer, Color32 strokeColor)
        {
            int strokeIndex = -1;
            Color32[] pixels = model.CopyPixels();
            for (int index = 0; index < pixels.Length; ++index)
            {
                if (!pixels[index].Equals(strokeColor))
                    continue;
                strokeIndex = index;
                break;
            }
            Assert.That(strokeIndex, Is.GreaterThanOrEqualTo(0),
                        "The actual whiteboard model must contain the known stroke color.");
            Assert.That(view.Texture, Is.Not.Null);
            Assert.That((Color32)view.Texture.GetPixel(strokeIndex % model.Width,
                                                       strokeIndex / model.Width), Is.EqualTo(strokeColor),
                        "The actual bound whiteboard view texture must receive the model raster change.");
            var propertyBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(propertyBlock);
            Assert.That(propertyBlock.GetTexture(Shader.PropertyToID("_MainTex")), Is.SameAs(view.Texture),
                        "The authored renderer must be displaying the bound view texture through its property block.");
        }

        private static GameObject CreateTemporaryCamera(Scene scene, string name, Rect rect,
                                                        CameraClearFlags clearFlags)
        {
            var value = new GameObject(name);
            SceneManager.MoveGameObjectToScene(value, scene);
            Camera camera = value.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.rect = rect;
            camera.clearFlags = clearFlags;
            camera.backgroundColor = ClearColor;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            return value;
        }

        private static void FrameOrthographic(Camera camera, Bounds bounds, Vector3 direction,
                                              Vector3 up, float aspect)
        {
            Assert.That(direction.sqrMagnitude, Is.GreaterThan(.0001f));
            direction.Normalize();
            if (up.sqrMagnitude < .0001f || Mathf.Abs(Vector3.Dot(direction, up.normalized)) > .98f)
                up = Vector3.up;
            up = Vector3.ProjectOnPlane(up, direction).normalized;
            camera.transform.SetPositionAndRotation(bounds.center + direction * 50f,
                                                    Quaternion.LookRotation(-direction, up));
            Vector3 extents = bounds.extents;
            float maxX = 0f;
            float maxY = 0f;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = bounds.center + Vector3.Scale(extents, new Vector3(x, y, z));
                        Vector3 local = camera.transform.InverseTransformPoint(corner);
                        maxX = Mathf.Max(maxX, Mathf.Abs(local.x));
                        maxY = Mathf.Max(maxY, Mathf.Abs(local.y));
                    }
            camera.orthographicSize = Mathf.Max(maxY, maxX / aspect) * 1.15f + .01f;
        }

        private static Bounds RenderedBounds(Transform root)
        {
            Assert.That(root, Is.Not.Null);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool found = false;
            Bounds result = default;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found)
                {
                    result = renderer.bounds;
                    found = true;
                }
                else
                {
                    result.Encapsulate(renderer.bounds);
                }
            }
            Assert.That(found, Is.True, "The original authored root has no enabled renderer bounds to frame.");
            return result;
        }

        private static bool HasNonClearPixel(Texture2D image)
        {
            return HasNonClearPixel(image, 0, image.width);
        }

        private static bool HasNonClearPixel(Texture2D image, int minXInclusive, int maxXExclusive)
        {
            for (int y = 0; y < image.height; ++y)
                for (int x = Mathf.Clamp(minXInclusive, 0, image.width);
                     x < Mathf.Clamp(maxXExclusive, 0, image.width); ++x)
                {
                    Color pixel = image.GetPixel(x, y);
                    if (Mathf.Abs(pixel.r - ClearColor.r) > .03f || Mathf.Abs(pixel.g - ClearColor.g) > .03f ||
                        Mathf.Abs(pixel.b - ClearColor.b) > .03f)
                        return true;
                }
            return false;
        }

        private static void ReadTarget(RenderTexture target, Texture2D destination, int width, int height)
        {
            RenderTexture.active = target;
            destination.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            destination.Apply(false, false);
        }

        private static void WriteDiagnosticPng(Texture2D image, string fileName)
        {
            if (image == null)
                return;
            try
            {
                string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, fileName);
                File.WriteAllBytes(path, image.EncodeToPNG());
                Debug.Log("[NativeFreeformSceneGraphicsTests] DIAGNOSTIC_PNG=" + path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[NativeFreeformSceneGraphicsTests] Unable to write diagnostic PNG: " +
                                 exception.Message);
            }
        }

        private static void AssertRenderedStrokeDelta(Texture2D before, Texture2D after, Camera camera,
                                                      Vector3 worldPoint)
        {
            Vector3 viewport = camera.WorldToViewportPoint(worldPoint);
            Assert.That(viewport.z, Is.GreaterThan(0f));
            int centerX = Mathf.RoundToInt((camera.rect.x + viewport.x * camera.rect.width) *
                                           (after.width - 1));
            int centerY = Mathf.RoundToInt((camera.rect.y + viewport.y * camera.rect.height) *
                                           (after.height - 1));
            for (int y = Mathf.Max(0, centerY - 80); y <= Mathf.Min(after.height - 1, centerY + 80); ++y)
                for (int x = Mathf.Max(0, centerX - 80); x <= Mathf.Min(after.width - 1, centerX + 80); ++x)
                {
                    Color pixel = after.GetPixel(x, y);
                    Color previous = before.GetPixel(x, y);
                    if (pixel.r > .55f && pixel.b > .55f && pixel.g < .4f &&
                        ColorDistance(pixel, previous) > .15f)
                        return;
                }
            Assert.Fail("The authored whiteboard surface did not visibly change from baseline for the known magenta model stroke.");
        }

        private static void AssertOffStrokeControlStable(Texture2D before, Texture2D after, Camera camera,
                                                         Vector3 worldPoint)
        {
            Vector3 viewport = camera.WorldToViewportPoint(worldPoint);
            Assert.That(viewport.z, Is.GreaterThan(0f));
            int centerX = Mathf.RoundToInt((camera.rect.x + viewport.x * camera.rect.width) *
                                           (after.width - 1));
            int centerY = Mathf.RoundToInt((camera.rect.y + viewport.y * camera.rect.height) *
                                           (after.height - 1));
            var changed = 0;
            var sampled = 0;
            for (int y = Mathf.Max(0, centerY - 24); y <= Mathf.Min(after.height - 1, centerY + 24); ++y)
                for (int x = Mathf.Max(0, centerX - 24); x <= Mathf.Min(after.width - 1, centerX + 24); ++x)
                {
                    ++sampled;
                    if (ColorDistance(before.GetPixel(x, y), after.GetPixel(x, y)) > .08f)
                        ++changed;
                }
            Assert.That(changed, Is.LessThanOrEqualTo(sampled / 20),
                        "A nearby off-stroke whiteboard control region changed too much between captures.");
        }

        private static float ColorDistance(Color first, Color second)
        {
            return Mathf.Max(Mathf.Abs(first.r - second.r), Mathf.Abs(first.g - second.g),
                             Mathf.Abs(first.b - second.b));
        }
    }
}
#endif
