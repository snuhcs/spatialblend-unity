// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// How the photo is encoded for the scan server: JPEG at <see cref="JpegQuality"/>, at the texture's own size (the user's points
    /// and the returned mask stay in the same pixel space). Demo fix: a 1728 x 1728 passthrough photo as PNG is several MB and is
    /// re-sent with every Segment request, which took tens of seconds per point over the US-Korea link; as JPEG it is a few hundred
    /// KB. The server opens the bytes with PIL, which takes PNG or JPEG (proto: "An encoded image (PNG or JPEG)").
    /// </summary>
    public static class ScanPhotoEncoding
    {
        public const int JpegQuality = 90;

        /// <summary>The texture as JPEG bytes (an RGB24 or RGBA32 CPU texture; alpha is dropped). Null for no texture.</summary>
        public static byte[] Encode(Texture2D texture) => texture != null ? texture.EncodeToJPG(JpegQuality) : null;
    }

    /// <summary>
    /// Where the capture card's photo comes from: the passthrough camera on the headset, or a stand-in (the lobby tutorial's
    /// still, or its hologram render). It reports the camera's state and the camera model, and takes the photo with the camera
    /// model it was taken with.
    /// </summary>
    public interface IScanFrameSource
    {
        /// <summary>The camera's state (the card shows the permission and starting messages from it).</summary>
        ScanCameraStatus Status { get; }

        /// <summary>The camera model now (the pose of this frame). False when there is no camera.</summary>
        bool TryGetLiveCamera(out ScanCameraModel camera);

        /// <summary>
        /// The photo as encoded bytes for the server (<see cref="ScanPhotoEncoding"/>: JPEG), its size, and the camera model it was
        /// taken with. False when there is no frame.
        /// </summary>
        bool TryCapture(out byte[] photo, out int width, out int height, out ScanCameraModel camera, out Texture2D texture);

        /// <summary>
        /// The live view (the card's viewfinder before Capture): a texture the source keeps up to date, the same one every call (no
        /// allocation per call), and the camera model of its frame (invalid when there is no camera). False when
        /// there is no frame. The card calls it a few times a second.
        /// </summary>
        bool TryGetPreview(out Texture texture, out ScanCameraModel camera);

        void Start();
        void Stop();
        void RetryPermission();

        /// <summary>Call every frame while the card is open.</summary>
        void Tick(float now);
    }

    /// <summary>
    /// The passthrough camera (Passthrough Camera API through <see cref="WebCamTextureManager"/>, with its provider and
    /// permission handling). The camera model is the left camera's intrinsics and its pose in the world at the moment of the
    /// frame grab.
    /// </summary>
    public sealed class PassthroughFrameSource : IScanFrameSource
    {
        private WebCamTextureManager _manager;

        public ScanCameraStatus Status
        {
            get
            {
                if (_manager == null) return ScanCameraStatus.Unsupported;
                if (Live) return ScanCameraStatus.Live;
                return _manager.Status == ScanCameraStatus.Live ? ScanCameraStatus.Starting : _manager.Status;
            }
        }

        private bool Live
        {
            get
            {
                var tex = _manager != null ? _manager.WebCamTexture : null;
                return tex != null && tex.isPlaying && tex.width > 16;
            }
        }

        public void Start()
        {
            if (_manager == null && Application.isPlaying) _manager = WebCamTextureManager.EnsureInstance();
            if (_manager == null) return;
            _manager.gameObject.SetActive(true);
            _manager.SetEye(_manager.Eye == PassthroughCameraEye.Left);
        }

        public void Stop()
        {
            if (_manager != null) _manager.StopCapture();
        }

        public void RetryPermission()
        {
            if (_manager != null) _manager.RetryPermission();
        }

        public bool TryGetLiveCamera(out ScanCameraModel camera)
        {
            camera = default;
            if (!Live) return false;
            var tex = _manager.WebCamTexture;
            camera = Model(_manager.Eye, tex.width, tex.height);
            return camera.IsValid;
        }

        public bool TryCapture(out byte[] photo, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
        {
            photo = null; width = height = 0; camera = default; texture = null;
            if (!Live) return false;
            var source = _manager.WebCamTexture;
            // The pose is read right after the pixels: the closer to the frame, the better the box lines up (see the PR's device check).
            texture = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            texture.SetPixels32(source.GetPixels32());
            texture.Apply();
            camera = Model(_manager.Eye, texture.width, texture.height);
            photo = ScanPhotoEncoding.Encode(texture);
            width = texture.width;
            height = texture.height;
            return photo != null && photo.Length > 0;
        }

        /// <summary>The camera's own texture (never copied: the viewfinder shows it as it updates) and the camera model now.</summary>
        public bool TryGetPreview(out Texture texture, out ScanCameraModel camera)
        {
            texture = null;
            camera = default;
            if (!Live) return false;
            var source = _manager.WebCamTexture;
            texture = source;
            camera = Model(_manager.Eye, source.width, source.height);
            return true;
        }

        public void Tick(float now) { }

        private static ScanCameraModel Model(PassthroughCameraEye eye, int width, int height)
        {
            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(eye);
            Pose pose = PassthroughCameraUtils.GetCameraPoseInWorld(eye);
            return new ScanCameraModel(pose.position, pose.rotation, intrinsics.FocalLength, intrinsics.PrincipalPoint,
                intrinsics.Resolution, width, height);
        }

    }

    /// <summary>
    /// A fixed picture that stands in for the camera (the lobby tutorial's sample photo): always live, always bright, with no
    /// camera. <see cref="BoxPrompt"/> is the box in the photo's pixels; the card no longer sends it (Segment carries points only).
    /// </summary>
    public sealed class StillFrameSource : IScanFrameSource
    {
        private readonly Texture2D _still;

        public StillFrameSource(Texture2D still, ScanPixelBox? boxPrompt)
        {
            _still = still ?? throw new ArgumentNullException(nameof(still));
            BoxPrompt = boxPrompt;
        }

        public ScanPixelBox? BoxPrompt { get; }
        public ScanCameraStatus Status => ScanCameraStatus.Live;
        public void Start() { }
        public void Stop() { }
        public void RetryPermission() { }
        public void Tick(float now) { }

        public bool TryGetLiveCamera(out ScanCameraModel camera)
        {
            camera = default;
            return false;
        }

        /// <summary>The still itself.</summary>
        public bool TryGetPreview(out Texture texture, out ScanCameraModel camera)
        {
            texture = _still;
            camera = default;
            return true;
        }

        public bool TryCapture(out byte[] photo, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
        {
            texture = new Texture2D(_still.width, _still.height, TextureFormat.RGB24, false);
            texture.SetPixels32(_still.GetPixels32());
            texture.Apply();
            photo = ScanPhotoEncoding.Encode(texture);
            width = texture.width;
            height = texture.height;
            camera = default;
            return true;
        }
    }
}

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// A Unity camera rendered as the photo: what a passthrough camera at that pose would see of the virtual scene. The camera
    /// model is the pinhole of the Unity camera (fy = H / 2 / tan(fov / 2), principal point at the centre). The lobby tutorial's
    /// hologram capture and the Editor renders use it.
    /// </summary>
    public sealed class RenderedFrameSource : IScanFrameSource
    {
        private readonly Camera _camera;
        private readonly int _width, _height;

        public RenderedFrameSource(Camera camera, int width = 640, int height = 480)
        {
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _width = width;
            _height = height;
        }

        public ScanCameraStatus Status => ScanCameraStatus.Live;
        public Camera Camera => _camera;
        public void Start() { }
        public void Stop() => ReleasePreview();
        public void RetryPermission() { }
        public void Tick(float now) { }

        public ScanCameraModel Model()
        {
            float fy = _height * 0.5f / Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return new ScanCameraModel(_camera.transform.position, _camera.transform.rotation, new Vector2(fy, fy),
                new Vector2(_width * 0.5f, _height * 0.5f), new Vector2Int(_width, _height), _width, _height);
        }

        public bool TryGetLiveCamera(out ScanCameraModel camera)
        {
            camera = Model();
            return true;
        }

        private RenderTexture _preview;

        /// <summary>The viewfinder: the camera's view (its culling mask without the UI layer) rendered into one kept render texture.</summary>
        public bool TryGetPreview(out Texture texture, out ScanCameraModel camera)
        {
            RenderInto(PreviewTarget(), _camera.cullingMask & ~(1 << 5));
            texture = _preview;
            camera = Model();
            return true;
        }

        /// <summary>The kept render texture of the viewfinder (made once, at the photo's size).</summary>
        public RenderTexture PreviewTarget()
        {
            if (_preview == null) _preview = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32) { name = "ScanPreview" };
            return _preview;
        }

        /// <summary>Frees the viewfinder's render texture.</summary>
        public void ReleasePreview()
        {
            if (_preview == null) return;
            _preview.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(_preview);
            else UnityEngine.Object.DestroyImmediate(_preview);
            _preview = null;
        }

        /// <summary>Renders the camera's view (its culling mask) into <paramref name="target"/>, allocating nothing.</summary>
        public void RenderInto(RenderTexture target, int cullingMask)
        {
            RenderTexture previousTarget = _camera.targetTexture;
            int previousMask = _camera.cullingMask;
            float previousAspect = _camera.aspect;
            try
            {
                _camera.cullingMask = cullingMask;
                _camera.aspect = (float)_width / _height;
                _camera.targetTexture = target;
                _camera.Render();
            }
            finally
            {
                _camera.targetTexture = previousTarget;
                _camera.cullingMask = previousMask;
                _camera.aspect = previousAspect;
            }
        }

        /// <summary>Renders the camera's view (its culling mask) into a texture of the photo's size.</summary>
        public Texture2D Render(int cullingMask)
        {
            var rt = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            RenderTexture previousTarget = _camera.targetTexture, previousActive = RenderTexture.active;
            int previousMask = _camera.cullingMask;
            float previousAspect = _camera.aspect;
            try
            {
                _camera.cullingMask = cullingMask;
                _camera.aspect = (float)_width / _height;
                _camera.targetTexture = rt;
                _camera.Render();
                RenderTexture.active = rt;
                var texture = new Texture2D(_width, _height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                _camera.targetTexture = previousTarget;
                _camera.cullingMask = previousMask;
                _camera.aspect = previousAspect;
                RenderTexture.active = previousActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        public bool TryCapture(out byte[] photo, out int width, out int height, out ScanCameraModel camera, out Texture2D texture)
        {
            texture = Render(_camera.cullingMask & ~(1 << 5));   // the UI layer: the camera never sees the app's own UI
            photo = ScanPhotoEncoding.Encode(texture);
            width = _width;
            height = _height;
            camera = Model();
            return true;
        }
    }
}
