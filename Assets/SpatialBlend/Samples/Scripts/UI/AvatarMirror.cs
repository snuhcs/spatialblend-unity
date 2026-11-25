// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// A mirror on the control panel for choosing an avatar preset: a camera in front of the user renders the local avatar
    /// into a small render texture shown on a RawImage, flipped horizontally like a real mirror. It costs nothing unless the
    /// preset picker is open AND a local avatar exists: only then the camera and the texture exist (512x512, no MSAA, no HDR,
    /// culling only the avatar's layer when it has its own).
    /// </summary>
    [AddComponentMenu("")]
    public sealed class AvatarMirror : MonoBehaviour
    {
        public const int Resolution = 512;
        /// <summary>How far in front of the user's head the mirror camera stands, and how far below the head it looks.</summary>
        public const float Distance = 1.1f;
        public const float LookDown = 0.22f;
        private const float ResolvePeriod = 1f;
        private const int UiLayer = 5;

        public RawImage Image;
        /// <summary>The "nothing to show yet" line, shown instead of the image while there is no local avatar.</summary>
        public GameObject Placeholder;

        private bool _pickerOpen;
        private Transform _target;
        private Camera _camera;
        private RenderTexture _texture;
        private float _nextResolve;

        /// <summary>Overrides how the local avatar is found (tests, and apps whose avatar is not a Fusion one).</summary>
        public System.Func<Transform> TargetResolver;
        /// <summary>Overrides the user's head (the main camera by default).</summary>
        public System.Func<Transform> HeadResolver;

        public bool PickerOpen => _pickerOpen;
        /// <summary>True while the mirror camera exists and renders.</summary>
        public bool IsRendering => _camera != null && _camera.enabled;
        public RenderTexture Texture => _texture;
        public Camera MirrorCamera => _camera;

        /// <summary>The preset picker was opened or closed; the mirror renders only while it is open.</summary>
        public void SetPickerOpen(bool open)
        {
            _pickerOpen = open;
            _nextResolve = 0f;
            Refresh();
        }

        /// <summary>Where the mirror camera stands and looks: in front of the head on the floor plane, at the face, looking back at the user.</summary>
        public static void ComputePose(Vector3 headPosition, Vector3 headForward, out Vector3 position, out Quaternion rotation)
        {
            Vector3 flat = Vector3.ProjectOnPlane(headForward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            position = headPosition + flat * Distance;
            Vector3 look = headPosition + Vector3.down * LookDown;
            rotation = Quaternion.LookRotation(look - position, Vector3.up);
        }

        /// <summary>Only the avatar's own layer when it has one; the default layer is shared with the room, so everything but the UI then.</summary>
        public static int CullingMaskFor(int avatarLayer) => avatarLayer != 0 ? 1 << avatarLayer : ~(1 << UiLayer);

        /// <summary>The local (state authority) avatar of the session, or null.</summary>
        public static Transform FindLocalAvatar()
        {
            foreach (var behaviour in FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsSortMode.None))
                if (behaviour != null && behaviour.Object != null && behaviour.Object.HasStateAuthority) return behaviour.transform;
            return null;
        }

        private void OnEnable() => Refresh();
        private void OnDisable() => Teardown();
        private void OnDestroy() => Teardown();

        private void Update()
        {
            if (!_pickerOpen) return;
            if (Time.unscaledTime >= _nextResolve)
            {
                _nextResolve = Time.unscaledTime + ResolvePeriod;
                Refresh();
            }
            if (_camera != null) Aim();
        }

        /// <summary>Re-evaluates what is shown: renders only when the picker is open and the avatar exists.</summary>
        public void Refresh()
        {
            _target = _pickerOpen && isActiveAndEnabled ? (TargetResolver != null ? TargetResolver() : FindLocalAvatar()) : null;
            bool render = _pickerOpen && isActiveAndEnabled && _target != null;
            if (Placeholder != null) Placeholder.SetActive(!render);
            if (Image != null) Image.enabled = render;
            if (!render) { Teardown(); return; }
            EnsureCamera();
            _camera.cullingMask = CullingMaskFor(_target.gameObject.layer);
            _camera.enabled = true;
            Aim();
        }

        private void Aim()
        {
            Transform head = HeadResolver != null ? HeadResolver() : (Camera.main != null ? Camera.main.transform : null);
            if (head == null || _camera == null) return;
            ComputePose(head.position, head.forward, out Vector3 position, out Quaternion rotation);
            _camera.transform.SetPositionAndRotation(position, rotation);
        }

        private void EnsureCamera()
        {
            if (_texture == null)
            {
                _texture = new RenderTexture(Resolution, Resolution, 16, RenderTextureFormat.Default) { name = "AvatarMirror", antiAliasing = 1 };
                _texture.Create();
            }
            if (_camera == null)
            {
                var go = new GameObject("AvatarMirrorCamera");
                _camera = go.AddComponent<Camera>();
                _camera.fieldOfView = 45f;
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = 6f;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = UiTheme.Palette.Surface;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.useOcclusionCulling = false;
                _camera.targetTexture = _texture;
            }
            if (Image != null)
            {
                Image.texture = _texture;
                Image.uvRect = new Rect(1f, 0f, -1f, 1f);   // a mirror: left and right swap
            }
        }

        private void Teardown()
        {
            if (_camera != null)
            {
                _camera.enabled = false;
                _camera.targetTexture = null;
                if (Application.isPlaying) Destroy(_camera.gameObject); else DestroyImmediate(_camera.gameObject);
                _camera = null;
            }
            if (Image != null) Image.texture = null;
            if (_texture != null)
            {
                _texture.Release();
                if (Application.isPlaying) Destroy(_texture); else DestroyImmediate(_texture);
                _texture = null;
            }
        }
    }
}
