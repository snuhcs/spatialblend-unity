// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of the capture card (scan UI Concept A, stage 1) in a stand-in room: a vase on a side table seen from
    /// eye height, the real <see cref="ScanCard"/> on the box, a canned server whose mask is the vase's rendered silhouette.
    /// Run with <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.ScanCardShots.Capture -shotDir DIR</c>
    /// (needs a graphics device: no -nographics).
    /// </summary>
    public static class ScanCardShots
    {
        private const int W = 1600, H = 1000;
        private const int VaseLayer = 30;
        private static readonly Vector3 Eye = new Vector3(0f, 1.5f, 0f);
        private static readonly Vector3 VaseBase = new Vector3(0f, 0.70f, 1.20f);
        private static readonly Vector3 BoxCenter = VaseBase + new Vector3(0f, 0.25f, 0f);
        private static readonly Vector3 BoxSize = new Vector3(0.38f, 0.5f, 0.38f);
        private const float TableTop = 0.70f;

        private static readonly Vector2[] Profile =
        {
            new Vector2(0.000f, 0.000f), new Vector2(0.090f, 0.000f), new Vector2(0.105f, 0.015f), new Vector2(0.135f, 0.080f),
            new Vector2(0.150f, 0.160f), new Vector2(0.138f, 0.240f), new Vector2(0.095f, 0.300f), new Vector2(0.062f, 0.340f),
            new Vector2(0.058f, 0.390f), new Vector2(0.075f, 0.425f), new Vector2(0.070f, 0.440f), new Vector2(0.000f, 0.440f),
        };

        private sealed class Clock : ITutorialClock { public double Now { get; set; } }

        private sealed class StubCamera : IScanFrameSource
        {
            public ScanCameraStatus Status { get; set; }
            public bool TryGetLiveCamera(out ScanCameraModel camera) { camera = default; return false; }
            public bool TryCapture(out byte[] png, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
            { png = null; width = height = 0; camera = default; texture = null; return false; }
            public bool TryGetPreview(out Texture texture, out ScanCameraModel camera) { texture = null; camera = default; return false; }
            public void Start() { }
            public void Stop() { }
            public void RetryPermission() { }
            public void Tick(float now) { }
        }

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "scan-card-shots");
            Directory.CreateDirectory(dir);
            try
            {
                if (UiFonts.Regular == null || UiFonts.SemiBold == null) UiFontAssetBuilder.Generate();
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                Run(dir);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[ScanCardShots] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void Run(string dir)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject vase = BuildRoom();
            var eye = new GameObject("Eye", typeof(Camera));
            var cam = eye.GetComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 58f;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = UiTheme.Palette.PanelBottom;
            eye.transform.position = Eye;
            eye.transform.rotation = Quaternion.LookRotation(BoxCenter + Vector3.up * 0.12f - Eye);

            // The "passthrough camera": the same eye at the photo's aspect; its silhouette of the vase is the canned mask.
            var photoCamGo = new GameObject("PhotoCam", typeof(Camera));
            var photoCam = photoCamGo.GetComponent<Camera>();
            photoCam.enabled = false;
            photoCam.fieldOfView = 50f;
            photoCam.nearClipPlane = 0.05f;
            photoCam.clearFlags = CameraClearFlags.SolidColor;
            photoCam.backgroundColor = Color.black;
            photoCamGo.transform.SetPositionAndRotation(Eye + Vector3.left * 0.03f, Quaternion.LookRotation(BoxCenter - Eye));
            var frames = new RenderedFrameSource(photoCam, 640, 480);
            byte[] silhouette = Silhouette(frames, vase);

            string outDir = Path.Combine(Path.GetTempPath(), "scan-card-shots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            var clock = new Clock();
            var transport = new CannedScanTransport(clock, TutorialAssets.LoadSampleModel, segmenter: new CannedSegmenter(silhouette, 640, 480))
            {
                SegmentDelaySeconds = 0.0, DelaySeconds = 100.0,
            };
            var service = new ScanService(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(outDir), null);
            if (!service.Initialize(out ScanError error)) throw new Exception("canned service: " + error);

            IScanFrameSource source = frames;
            Action<ScanCard> configure = c =>
            {
                c.FrameSource = source;
                c.ServiceSource = () => service;
            };
            ScanCard.Opening += configure;
            try
            {
                // 1. aim: the live view of the box, one Capture button
                var card = ScanCard.Open(BoxCenter, BoxSize, Quaternion.identity, eye.transform);
                var box = Box(UiTheme.Palette.Accent, 1f);
                card.Refresh(0f, 0.016f);
                Shoot(cam, dir, "1-aim");

                // 2. the frozen photo with the box's mask, the point tools and Confirm
                card.Capture();
                transport.Tick();
                service.Update(0.1);
                card.Refresh(0.5f, 0.016f);
                Shoot(cam, dir, "2-points-confirm");

                // 3. building: the ring around the box's base
                card.PressPrimary();   // Confirm
                card.PressPrimary();   // 3D copy (nothing is built by itself)
                UnityEngine.Object.DestroyImmediate(box);
                card.Refresh(12f, 0.016f);
                Shoot(cam, dir, "3-building-ring");

                // 4. done: the copy where the box was
                clock.Now = 200.0;
                transport.Tick();
                service.Update(200.0);
                var copy = Vase("Copy", VaseBase + new Vector3(card.Anchor.Center.x - BoxCenter.x, 0f, card.Anchor.Center.z - BoxCenter.z), 0f);
                Shoot(cam, dir, "4-done-copy-beside");
                UnityEngine.Object.DestroyImmediate(copy);
                card.Close();

                // 5. camera permission in the card
                source = new StubCamera { Status = ScanCameraStatus.PermissionDenied };
                card = ScanCard.Open(BoxCenter, BoxSize, Quaternion.identity, eye.transform);
                box = Box(UiTheme.Palette.Accent, 0.6f);
                card.Refresh(0f, 0.016f);
                Shoot(cam, dir, "5-permission");
                card.Close();

                // 6. a failure in plain words with Retake: a box beside the vase holds nothing the server can find
                source = frames;
                UnityEngine.Object.DestroyImmediate(box);
                card = ScanCard.Open(BoxCenter + Vector3.right * 0.45f, BoxSize, Quaternion.identity, eye.transform);
                card.Capture();
                transport.Tick();
                service.Update(300.0);
                Shoot(cam, dir, "6-failed-retake");
                card.Close();

                // 7. the lobby tutorial: a practice vase stands in the box; photo and mask are rendered from the eye
                vase.SetActive(false);
                var hologram = Vase("TutorialHologramVase", VaseBase, 0f);
                source = new TutorialHologramFrameSource(() => eye.transform, hologram, m => transport.Segmenter = m);
                card = ScanCard.Open(BoxCenter, BoxSize, Quaternion.identity, eye.transform);
                box = Box(UiTheme.Palette.Accent, 0.6f);
                card.Capture();
                transport.Tick();
                service.Update(400.0);
                card.Refresh(400.5f, 0.016f);
                Shoot(cam, dir, "7-tutorial-practice-vase-selected");
                card.Close();
                UnityEngine.Object.DestroyImmediate(box);
            }
            finally
            {
                ScanCard.Opening -= configure;
                service.Dispose();
                try { Directory.Delete(outDir, true); } catch { /* temp */ }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static byte[] Silhouette(RenderedFrameSource frames, GameObject vase)
        {
            var renderer = vase.GetComponent<Renderer>();
            Material keep = renderer.sharedMaterial;
            int layer = vase.layer;
            vase.layer = VaseLayer;
            renderer.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
            Texture2D tex = frames.Render(1 << VaseLayer);
            vase.layer = layer;
            renderer.sharedMaterial = keep;
            var pixels = tex.GetPixels32();
            var mask = new byte[pixels.Length];
            int w = tex.width, h = tex.height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[(h - 1 - y) * w + x] = pixels[y * w + x].r > 127 ? (byte)1 : (byte)0;   // rows from the top
            UnityEngine.Object.DestroyImmediate(tex);
            return mask;
        }

        private static void Shoot(Camera cam, string dir, string name)
        {
            Canvas.ForceUpdateCanvases();
            cam.cullingMask = ~0;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            cam.aspect = (float)W / H;
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            Debug.Log("[ScanCardShots] wrote " + name);
        }

        private static Material Lit(Color c, float gloss = 0.25f)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", gloss);
            return m;
        }

        private static GameObject Cube(string name, Vector3 center, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = center;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        private static GameObject BuildRoom()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.62f, 0.64f);
            RenderSettings.ambientEquatorColor = new Color(0.5f, 0.47f, 0.43f);
            RenderSettings.ambientGroundColor = new Color(0.3f, 0.25f, 0.2f);
            var sun = new GameObject("Light", typeof(Light)).GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.85f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.5f;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            Cube("Floor", new Vector3(0f, -0.01f, 1.5f), new Vector3(10f, 0.02f, 8f), Lit(new Color(0.55f, 0.39f, 0.26f), 0.15f));
            Cube("Wall", new Vector3(0f, 1.4f, 2.25f), new Vector3(10f, 2.8f, 0.05f), Lit(new Color(0.86f, 0.83f, 0.78f), 0.05f));
            Cube("WallLeft", new Vector3(-1.5f, 1.4f, 1f), new Vector3(0.05f, 2.8f, 4f), Lit(new Color(0.8f, 0.78f, 0.74f), 0.05f));
            var wood = Lit(new Color(0.3f, 0.22f, 0.16f), 0.35f);
            Cube("TableTop", new Vector3(0.1f, TableTop - 0.02f, 1.25f), new Vector3(1.0f, 0.04f, 0.6f), wood);
            foreach (float dx in new[] { -0.36f, 0.56f })
                foreach (float dz in new[] { 1.0f, 1.5f })
                    Cube("Leg", new Vector3(dx, (TableTop - 0.04f) * 0.5f, dz), new Vector3(0.04f, TableTop - 0.04f, 0.04f), wood);
            Cube("Book", new Vector3(0.42f, TableTop + 0.02f, 1.45f), new Vector3(0.22f, 0.04f, 0.16f), Lit(new Color(0.18f, 0.32f, 0.52f), 0.3f));
            return Vase("Vase", VaseBase, 0f);
        }

        private static GameObject Vase(string name, Vector3 at, float yaw)
        {
            const int seg = 40;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < Profile.Length - 1; i++)
            {
                Vector2 a = Profile[i], b = Profile[i + 1];
                Vector2 d = (b - a).normalized;
                var n2 = new Vector2(d.y, -d.x);
                int start = verts.Count;
                for (int s = 0; s <= seg; s++)
                {
                    float ang = s * Mathf.PI * 2f / seg, c = Mathf.Cos(ang), sn = Mathf.Sin(ang);
                    verts.Add(new Vector3(a.x * c, a.y, a.x * sn));
                    verts.Add(new Vector3(b.x * c, b.y, b.x * sn));
                    var n = new Vector3(n2.x * c, n2.y, n2.x * sn).normalized;
                    norms.Add(n); norms.Add(n);
                }
                for (int s = 0; s < seg; s++)
                {
                    int k = start + s * 2;
                    tris.AddRange(new[] { k, k + 1, k + 2, k + 1, k + 3, k + 2 });
                }
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = Lit(new Color(0.82f, 0.38f, 0.24f), 0.55f);
            go.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            return go;
        }

        /// <summary>The drawn box as the app shows it: accent edges.</summary>
        private static GameObject Box(Color color, float alpha)
        {
            var root = new GameObject("Box") { layer = ScanCard.UiLayer };
            var m = new Material(Shader.Find("Sprites/Default")) { color = new Color(color.r, color.g, color.b, alpha) };
            Vector3 e = BoxSize * 0.5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = BoxCenter + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    int j = i | bit;
                    if (j == i) continue;
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                    go.transform.SetParent(root.transform, false);
                    Vector3 a = corners[i], b = corners[j];
                    go.transform.position = (a + b) * 0.5f;
                    go.transform.rotation = Quaternion.LookRotation(b - a, Mathf.Abs(Vector3.Dot((b - a).normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up);
                    go.transform.localScale = new Vector3(0.005f, 0.005f, (b - a).magnitude + 0.005f);
                    go.GetComponent<Renderer>().sharedMaterial = m;
                    go.layer = ScanCard.UiLayer;
                }
            return root;
        }
    }
}
#endif
