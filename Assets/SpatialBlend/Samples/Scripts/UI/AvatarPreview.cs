// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The lobby preview avatar behind the preset mirror: ONE local, non-networked avatar of the selected preset, made from the
    /// original avatar prefab by <see cref="SbLocalPresetAvatarFactory"/> (which refuses a prefab with a NetworkObject or a
    /// Fusion behaviour, so nothing is spawned on the network). It exists only while the picker is open AND visible
    /// (<see cref="Allowed"/>: not while the tutorial covers the panel, nor while the panel is hidden): it is made then, replaced when
    /// the preset changes (after the slider rests), and destroyed when either stops being true or a room is joined.
    ///
    /// It follows the user through the same tracking path as the real local avatar (<see cref="MetaInputTrackingDelegate"/>, without
    /// the network part), so head, hands and fingers move. It asks the SDK for the third-person view (the first-person view hides
    /// the head) and lives on its own layer (<see cref="LayerName"/>) that the main camera does not draw, so it is only in the mirror.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class AvatarPreview : MonoBehaviour
    {
        public const string LayerName = "AvatarPreview";
        public const float PresetRestSeconds = 0.35f;
        /// <summary>While new objects keep appearing under the preview (the SDK makes them as it loads) it is re-layered every frame; this long after the last one, it settles.</summary>
        public const float SettleSeconds = 2f;
        private const float IdleRelayerPeriod = 0.5f;
        private const float RigSearchPeriod = 1f;

        /// <summary>The original, non-networked avatar prefab (the app's local actor prefab).</summary>
        public Func<SbAvatarEntity> PrefabResolver;
        /// <summary>The camera that must not draw the preview (the user's eyes). Camera.main by default; asked again every tick (rigs get rebuilt).</summary>
        public Func<Camera> MainCameraResolver;
        /// <summary>The rig whose tracking drives the preview. Found once and cached by default.</summary>
        public Func<OVRCameraRig> RigResolver;
        /// <summary>False while the picker cannot be seen (the tutorial covers the panel, or it is hidden): the preview is not kept then.</summary>
        public Func<bool> Allowed;

        private bool _open;
        private int _preset;
        private int _shownPreset = -1;
        private float _presetChangedAt = -1f;
        private GameObject _root;
        private SbAvatarEntity _avatar;
        private float _nextRelayer;
        private float _nextCreate;
        private float _lastGrowth;
        private int _lastCount;
        private bool _warned;
        private readonly List<Transform> _scratch = new List<Transform>();
        private OVRCameraRig _rig;
        private float _nextRigSearch;
        private Camera _hiddenFrom;
        private bool _clearedBit;

        /// <summary>Raised when the preview was made, replaced or destroyed.</summary>
        public event Action Changed;

        public bool IsOpen => _open;
        public bool Exists => _root != null;
        public int ShownPreset => _shownPreset;
        public SbAvatarEntity Avatar => _avatar;
        /// <summary>The preview's root (on the preview layer), or null.</summary>
        public Transform Root => _root != null ? _root.transform : null;
        public static int Layer => LayerMask.NameToLayer(LayerName);

        private bool IsAllowed => Allowed == null || Allowed();

        /// <summary>The picker opened or closed. Closing destroys the preview.</summary>
        public void SetOpen(bool open, int preset)
        {
            if (!open) { _open = false; Destroy(); return; }
            bool opening = !_open || !Exists;
            _open = true;
            if (opening) { _preset = preset; if (IsAllowed) Create(); }
            else SetPreset(preset);
        }

        /// <summary>The selected preset changed; the preview follows once the choice rests (the slider fires on every step).</summary>
        public void SetPreset(int preset)
        {
            if (preset == _preset) return;
            _preset = preset;
            _presetChangedAt = Time.unscaledTime;
        }

        /// <summary>Time-driven part of <see cref="LateUpdate"/>, separate so tests can drive it.</summary>
        public void Tick(float now)
        {
            if (!_open) return;
            if (!IsAllowed) { Destroy(); return; }   // the tutorial or a hidden panel: nothing is kept alive that nobody can see
            if (_presetChangedAt >= 0f && now - _presetChangedAt >= PresetRestSeconds) Flush();
            if (!Exists)
            {
                if (now >= _nextCreate) { _nextCreate = now + 1f; Create(); }   // the prefab may arrive late; retry slowly
                return;
            }
            KeepOffTheMainCamera();
            Relayer(now);
        }

        /// <summary>Applies a pending preset change now.</summary>
        public void Flush()
        {
            _presetChangedAt = -1f;
            if (_open && _preset != _shownPreset) { Destroy(); Create(); }
        }

        // LateUpdate: after every Update (the SDK makes its objects in its own), before rendering, so a new object is on the layer before it is drawn.
        private void LateUpdate() => Tick(Time.unscaledTime);
        private void OnDisable() => Destroy();
        private void OnDestroy() => Destroy();

        private void Create()
        {
            SbAvatarEntity prefab = PrefabResolver != null ? PrefabResolver() : null;
            if (prefab == null) return;
            _root = new GameObject("Lobby Avatar Preview");
            if (!SbLocalPresetAvatarFactory.TryCreate(prefab, _root.transform, _preset, out SbAvatarEntity avatar,
                    out _, out SbTransformInputManager input, out string error))
            {
                if (!_warned) Debug.LogWarning("[AvatarPreview] no preview avatar: " + error);
                _warned = true;
                DestroyObject(_root);
                _root = null;
                return;
            }
            _avatar = avatar;
            _shownPreset = _preset;
            // The factory has parented the clone, so its Awake has run, but its Start has not (it runs next frame and creates the SDK
            // entity). The real tracking path (head, hands, fingers from the rig, nothing sent anywhere) and the third-person view are
            // read at that creation, so setting them now, in this same call, is in time.
            input.TrackingDelegateOverride = new MetaInputTrackingDelegate(Rig());
            input.ControlDelegateOverride = new SampleInputControlDelegate();
            avatar.RequestLocalPresetThirdPersonView();
            _lastGrowth = Time.unscaledTime;
            _lastCount = 0;
            ApplyLayer();
            KeepOffTheMainCamera();
            Changed?.Invoke();
        }

        private void Destroy()
        {
            RestoreMainCamera();
            _presetChangedAt = -1f;
            if (_root == null) return;
            DestroyObject(_root);
            _root = null;
            _avatar = null;
            _shownPreset = -1;
            Changed?.Invoke();
        }

        private OVRCameraRig Rig()
        {
            if (RigResolver != null) return RigResolver();
            if (_rig == null && Time.unscaledTime >= _nextRigSearch)
            {
                _nextRigSearch = Time.unscaledTime + RigSearchPeriod;
                _rig = FindFirstObjectByType<OVRCameraRig>();
            }
            return _rig;
        }

        /// <summary>
        /// Puts everything the SDK made under the preview on the preview layer. The SDK makes its objects with new GameObject (layer 0)
        /// as the avatar loads, so while the number of objects grows it runs every frame; once settled, twice a second as a safety net.
        /// </summary>
        private void Relayer(float now)
        {
            if (_root == null) return;
            _root.GetComponentsInChildren(true, _scratch);
            if (_scratch.Count != _lastCount) { _lastCount = _scratch.Count; _lastGrowth = now; }
            bool settled = now - _lastGrowth > SettleSeconds;
            if (settled && now < _nextRelayer) return;
            _nextRelayer = now + IdleRelayerPeriod;
            ApplyLayer(_scratch);
        }

        private void ApplyLayer()
        {
            if (_root == null) return;
            _root.GetComponentsInChildren(true, _scratch);
            ApplyLayer(_scratch);
        }

        private void ApplyLayer(List<Transform> all)
        {
            int layer = Layer;
            if (layer < 0) return;
            for (int i = 0; i < all.Count; ++i)
                if (all[i].gameObject.layer != layer) all[i].gameObject.layer = layer;
        }

        /// <summary>
        /// The user's own camera must not draw the preview. Only the preview layer's bit is touched, and the camera is asked again every
        /// tick: a rebuilt rig or an observer camera gets the same treatment, and the old one gets its bit back.
        /// </summary>
        private void KeepOffTheMainCamera()
        {
            int layer = Layer;
            Camera cam = MainCameraResolver != null ? MainCameraResolver() : Camera.main;
            if (cam != _hiddenFrom) RestoreMainCamera();
            if (layer < 0 || cam == null) return;
            int bit = 1 << layer;
            if ((cam.cullingMask & bit) != 0)
            {
                cam.cullingMask &= ~bit;
                _clearedBit = true;   // it was ours to clear, so it is ours to give back
            }
            _hiddenFrom = cam;
        }

        private void RestoreMainCamera()
        {
            int layer = Layer;
            if (_hiddenFrom != null && _clearedBit && layer >= 0) _hiddenFrom.cullingMask |= 1 << layer;
            _hiddenFrom = null;
            _clearedBit = false;
        }

        private static void DestroyObject(GameObject go)
        {
            if (go == null) return;
            // The staged avatar was made by the factory; retire its input tracking before the object goes.
            foreach (var input in go.GetComponentsInChildren<SbTransformInputManager>(true))
                input.TryDetachArticulatedBodyTracking(out _);
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
