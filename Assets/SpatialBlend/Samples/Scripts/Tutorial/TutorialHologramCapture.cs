// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.Scanning;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// The lobby tutorial's camera for task 3.1: a practice vase (a hologram) stands in the user's box,
    /// and the "photo" is a render of it aimed at the vase, so the vase sits in the centre. The vase's materials are lit
    /// (the occlusion standard shader, and the glTF PBR shader), and the lobby often has no light of its own. A camera that
    /// only copied the eye direction and cleared to the dark panel colour therefore rendered the vase black. This camera
    /// clears to a mid gray and carries a directional light. When that render is still dark or empty (no graphics device, or a
    /// shader that stays black), a synthetic gray photo of the same mask stands in. The canned server's mask is the vase's
    /// silhouette from the same pose, so the box prompt, the mask and the thumbnail all line up. Without a graphics device
    /// (the Editor tests run -nographics) the silhouette is the projected outline of the vase's bounds instead.
    /// </summary>
    public sealed class TutorialHologramFrameSource : IScanFrameSource
    {
        /// <summary>A layer nothing in the app uses: the hologram is moved there for the two renders and moved back.</summary>
        public const int CaptureLayer = 29;
        public const int PhotoWidth = 640, PhotoHeight = 480;
        /// <summary>The passthrough camera's vertical field of view (Quest 3, about 58 deg on a 4:3 frame).</summary>
        public const float FieldOfView = 58f;
        /// <summary>Fewer silhouette pixels than this (or more than 60% of the photo) means the render did not happen (no graphics device): the outline stands in.</summary>
        public const int MinSilhouettePixels = 50;
        /// <summary>The photo's clear colour. The panel colour is nearly black, and a dark clear reads as an empty photo.</summary>
        public static readonly Color PhotoBackground = new Color(0.45f, 0.45f, 0.47f, 1f);
        /// <summary>The vase ink of the synthetic photo, the practice placeholder's tint.</summary>
        public static readonly Color SyntheticVase = new Color(0.85f, 0.80f, 0.70f, 1f);
        /// <summary>A pixel this far from <see cref="PhotoBackground"/> (sum of the channel deltas) belongs to the subject.</summary>
        public const int SubjectChannelDelta = 36;
        /// <summary>A subject darker than this (0–1) is the unlit vase, and the synthetic photo stands in.</summary>
        public const float MinSubjectLuminance = 0.18f;

        private readonly Func<Transform> _eye;
        private readonly GameObject _hologram;
        private readonly Vector3 _fallbackTarget;
        private readonly Action<CannedSegmenter> _setMask;
        private Camera _camera;
        private RenderedFrameSource _frames;

        /// <param name="eye">The user's eye (null in the Editor tests: a pose looking at the hologram from 1.2 m is used).</param>
        /// <param name="setMask">Takes the mask rendered with each photo (the canned server's segmenter).</param>
        public TutorialHologramFrameSource(Func<Transform> eye, GameObject hologram, Action<CannedSegmenter> setMask)
        {
            _eye = eye ?? (() => null);
            _hologram = hologram != null ? hologram : throw new ArgumentNullException(nameof(hologram));
            _fallbackTarget = hologram.transform.position;
            _setMask = setMask;
        }

        public ScanCameraStatus Status => ScanCameraStatus.Live;
        public int LastSilhouettePixels { get; private set; }
        public bool LastMaskWasRendered { get; private set; }
        /// <summary>True when the last photo was the synthetic vase, because the render was dark or empty.</summary>
        public bool LastPhotoWasSynthetic { get; private set; }
        internal Camera CaptureCamera => _camera;

        public void Start() => EnsureCamera();

        public void Stop()
        {
            if (_camera == null) return;
            var go = _camera.gameObject;
            _frames?.ReleasePreview();
            _camera = null;
            _frames = null;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        public void RetryPermission() { }
        public void Tick(float now) { }

        public bool TryGetLiveCamera(out ScanCameraModel camera)
        {
            EnsureCamera();
            Aim();
            camera = _frames.Model();
            return true;
        }

        private readonly List<(GameObject go, int layer)> _previewLayers = new List<(GameObject, int)>();
        private Transform[] _hologramParts;

        /// <summary>
        /// The viewfinder: the practice vase rendered from the user's eye into one kept render texture, as the photo will be (the
        /// card calls it a few times a second; the parts and the layer list are reused, so nothing is allocated per call).
        /// </summary>
        public bool TryGetPreview(out Texture texture, out ScanCameraModel camera)
        {
            EnsureCamera();
            Aim();
            camera = _frames.Model();
            if (_hologramParts == null) _hologramParts = _hologram.GetComponentsInChildren<Transform>(true);
            _previewLayers.Clear();
            foreach (Transform t in _hologramParts)
            {
                if (t == null) continue;
                _previewLayers.Add((t.gameObject, t.gameObject.layer));
                t.gameObject.layer = CaptureLayer;
            }
            try { _frames.RenderInto(_frames.PreviewTarget(), 1 << CaptureLayer); }
            finally { foreach (var (go, layer) in _previewLayers) if (go != null) go.layer = layer; }
            texture = _frames.PreviewTarget();
            return true;
        }

        public bool TryCapture(out byte[] png, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
        {
            LastPhotoWasSynthetic = false;
            EnsureCamera();
            Aim();
            camera = _frames.Model();
            var layers = new List<(GameObject, int)>();
            foreach (var t in _hologram.GetComponentsInChildren<Transform>(true))
            {
                layers.Add((t.gameObject, t.gameObject.layer));
                t.gameObject.layer = CaptureLayer;
            }
            texture = null;
            try
            {
                texture = _frames.Render(1 << CaptureLayer);
                byte[] mask = null;
                int pixels = 0;
                try { mask = RenderSilhouette(out pixels); }
                catch (Exception e) { Debug.LogWarning("[Tutorial] The vase's silhouette could not be rendered; its outline stands in: " + e.Message); }
                LastSilhouettePixels = pixels;
                // Nothing drawn, or everything "drawn" (a read-back with no graphics device): the outline stands in.
                LastMaskWasRendered = mask != null && pixels >= MinSilhouettePixels && pixels <= PhotoWidth * PhotoHeight * 0.6f;
                if (!LastMaskWasRendered) mask = OutlineMask(camera, Bounds());
                _setMask?.Invoke(new CannedSegmenter(mask, PhotoWidth, PhotoHeight));
                LastPhotoWasSynthetic = !RenderedPhotoIsUsable(texture);
                if (LastPhotoWasSynthetic) PaintSynthetic(texture, mask);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tutorial] The practice vase could not be captured: " + e.Message);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                texture = null;
                png = null;
                width = height = 0;
                return false;
            }
            finally
            {
                foreach (var (go, layer) in layers) if (go != null) go.layer = layer;
            }
            png = texture.EncodeToPNG();
            width = PhotoWidth;
            height = PhotoHeight;
            return png != null && png.Length > 0;
        }

        private void EnsureCamera()
        {
            if (_camera != null) return;
            var go = new GameObject("TutorialHologramCamera", typeof(Camera), typeof(Light)) { hideFlags = HideFlags.DontSave };
            _camera = go.GetComponent<Camera>();
            _camera.enabled = false;
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 20f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = PhotoBackground;
            var light = go.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            light.color = Color.white;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.cullingMask = 1 << CaptureLayer;
            _frames = new RenderedFrameSource(_camera, PhotoWidth, PhotoHeight);
        }

        private void Aim()
        {
            // The synthetic photo frames the vase in the centre. The eye supplies the place the picture is taken from;
            // the rotation looks at the vase, so a glance past it still shows the vase in the middle of the frame.
            Vector3 target = _hologram != null ? Bounds().center : _fallbackTarget;
            Transform eye = _eye();
            if (eye == null && Application.isPlaying && Camera.main != null) eye = Camera.main.transform;
            if (eye != null)
            {
                Vector3 dir = target - eye.position;
                if (dir.sqrMagnitude < 1e-6f) dir = eye.forward.sqrMagnitude > 1e-6f ? eye.forward : Vector3.forward;
                Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
                _camera.transform.SetPositionAndRotation(eye.position, Quaternion.LookRotation(dir, up));
                return;
            }
            Vector3 from = target + new Vector3(0f, 0.5f, -1.1f);
            _camera.transform.SetPositionAndRotation(from, Quaternion.LookRotation(target - from, Vector3.up));
        }

        /// <summary>
        /// A rendered frame is kept when a lit subject stands on the gray clear: enough pixels differ from
        /// <see cref="PhotoBackground"/>, they are not most of the frame (a failed read-back paints everything), and their
        /// average luminance is at least <see cref="MinSubjectLuminance"/> (a black vase is the unlit lobby).
        /// </summary>
        public static bool KeepRenderedPhoto(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return false;
            var clear = (Color32)PhotoBackground;
            int count = 0;
            long luma = 0;
            int limit = (int)(pixels.Length * 0.6f);
            float minLuma = MinSubjectLuminance * 255f;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                int delta = Math.Abs(p.r - clear.r) + Math.Abs(p.g - clear.g) + Math.Abs(p.b - clear.b);
                if (delta <= SubjectChannelDelta) continue;
                count++;
                if (count > limit) return false;
                luma += (p.r * 54 + p.g * 183 + p.b * 19) >> 8;
            }
            if (count < MinSilhouettePixels) return false;
            return luma / (float)count >= minLuma;
        }

        /// <summary>
        /// A gray frame with the vase painted where <paramref name="mask"/> is set (mask rows from the top, texture rows
        /// from the bottom, the same flip as the silhouette). An empty mask gets an ellipse in the centre.
        /// </summary>
        public static Color32[] SyntheticPhoto(int width, int height, byte[] mask)
        {
            var background = (Color32)PhotoBackground;
            var ink = (Color32)SyntheticVase;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;
            int painted = 0;
            if (mask != null && width > 0 && height > 0 && mask.Length >= width * height)
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (mask[y * width + x] == 0) continue;
                        pixels[(height - 1 - y) * width + x] = ink;
                        painted++;
                    }
            }
            if (painted < MinSilhouettePixels) PaintCentreEllipse(pixels, width, height, ink);
            return pixels;
        }

        private static bool RenderedPhotoIsUsable(Texture2D texture)
        {
            if (texture == null) return false;
            try { return KeepRenderedPhoto(texture.GetPixels32()); }
            catch (Exception) { return false; }
        }

        private static void PaintSynthetic(Texture2D texture, byte[] mask)
        {
            if (texture == null) return;
            try
            {
                texture.SetPixels32(SyntheticPhoto(texture.width, texture.height, mask));
                texture.Apply();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tutorial] The synthetic vase photo could not be written: " + e.Message);
            }
        }

        private static void PaintCentreEllipse(Color32[] pixels, int width, int height, Color32 ink)
        {
            if (width <= 0 || height <= 0) return;
            double cx = (width - 1) * 0.5, cy = (height - 1) * 0.5;
            double rx = Math.Max(1.0, width * 0.12), ry = Math.Max(1.0, height * 0.22);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                    if (dx * dx + dy * dy <= 1.0) pixels[y * width + x] = ink;
                }
        }

        /// <summary>The vase in flat white on black, as one byte per pixel with row 0 at the top.</summary>
        private byte[] RenderSilhouette(out int pixels)
        {
            var renderers = _hologram.GetComponentsInChildren<Renderer>(true);
            var kept = new Material[renderers.Length][];
            // Unlit/Color may be stripped from a player build; Sprites/Default is always included.
            Shader flatShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (flatShader == null) throw new InvalidOperationException("no unlit shader in this build");
            var white = new Material(flatShader) { color = Color.white };
            Color background = _camera.backgroundColor;
            for (int i = 0; i < renderers.Length; i++)
            {
                kept[i] = renderers[i].sharedMaterials;
                var flat = new Material[kept[i].Length];
                for (int k = 0; k < flat.Length; k++) flat[k] = white;
                renderers[i].sharedMaterials = flat;
            }
            Texture2D silhouette = null;
            try
            {
                _camera.backgroundColor = Color.clear;
                silhouette = _frames.Render(1 << CaptureLayer);
                var colors = silhouette.GetPixels32();
                var mask = new byte[PhotoWidth * PhotoHeight];
                pixels = 0;
                for (int y = 0; y < PhotoHeight; y++)
                    for (int x = 0; x < PhotoWidth; x++)
                    {
                        if (colors[y * PhotoWidth + x].r <= 127) continue;
                        mask[(PhotoHeight - 1 - y) * PhotoWidth + x] = 1;   // texture rows from the bottom
                        pixels++;
                    }
                return mask;
            }
            finally
            {
                _camera.backgroundColor = background;
                for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) renderers[i].sharedMaterials = kept[i];
                if (silhouette != null) UnityEngine.Object.DestroyImmediate(silhouette);
                UnityEngine.Object.DestroyImmediate(white);
            }
        }

        private Bounds Bounds()
        {
            Bounds? measured = null;
            foreach (var r in _hologram.GetComponentsInChildren<Renderer>(true))
            {
                if (measured == null) measured = r.bounds;
                else { var b = measured.Value; b.Encapsulate(r.bounds); measured = b; }
            }
            if (measured != null) return measured.Value;
            var box = _hologram.GetComponent<BoxCollider>();
            return box != null ? new Bounds(_hologram.transform.TransformPoint(box.center), box.size) : new Bounds(_hologram.transform.position, Vector3.one * 0.2f);
        }

        /// <summary>The ellipse inside the projected bounds: the vase's outline when nothing can be rendered.</summary>
        public static byte[] OutlineMask(ScanCameraModel camera, Bounds bounds)
        {
            var mask = new byte[camera.ImageWidth * camera.ImageHeight];
            if (!ScanBoxProjection.TryBuildPrompt(camera, bounds.center, bounds.size, Quaternion.identity, out ScanPrompt prompt, out _))
                return mask;
            ScanPixelBox b = prompt.Box.Value;
            double rx = b.Width * 0.5, ry = b.Height * 0.5;
            for (int y = (int)b.Y1; y <= (int)b.Y2 && y < camera.ImageHeight; y++)
                for (int x = (int)b.X1; x <= (int)b.X2 && x < camera.ImageWidth; x++)
                {
                    double dx = (x + 0.5 - b.CenterX) / rx, dy = (y + 0.5 - b.CenterY) / ry;
                    if (dx * dx + dy * dy <= 1.0) mask[y * camera.ImageWidth + x] = 1;
                }
            return mask;
        }
    }
}
