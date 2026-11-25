// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>The lobby preview avatar behind the preset mirror: local, non-networked, only while the picker is open.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarPreviewTests
    {
        private GameObject _root;
        private GameObject _camera;
        private GameObject _head;
        private ScriptableObject _settings;
        private AvatarSettingsPresenter _avatars;
        private SbAvatarEntity _prefab;

        [SetUp]
        public void SetUp()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab");
            _prefab = asset.GetComponent<SbAvatarEntity>();
            _camera = new GameObject("test-main-camera", typeof(Camera));
            _camera.GetComponent<Camera>().cullingMask = ~0;
            _head = new GameObject("head");
            _head.transform.position = new Vector3(0f, 1.6f, 0f);
        }

        [TearDown]
        public void TearDown()
        {
            // Edit Mode runs no OnDisable: a mirror camera left enabled renders offscreen between tests (and crashes a no-graphics editor).
            foreach (var mirror in UnityEngine.Object.FindObjectsByType<AvatarMirror>(FindObjectsInactive.Include, FindObjectsSortMode.None)) mirror.SetPickerOpen(false);
            foreach (var preview in UnityEngine.Object.FindObjectsByType<AvatarPreview>(FindObjectsInactive.Include, FindObjectsSortMode.None)) preview.SetOpen(false, 0);
            foreach (var go in new[] { _root, _camera, _head })
                if (go != null) Object.DestroyImmediate(go);
            if (_settings != null) Object.DestroyImmediate(_settings);
        }

        private ControlPanelView NewView()
        {
            _root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings = settings;
            _avatars = new AvatarSettingsPresenter(() => settings, () => { });
            var view = ControlPanelView.Build((RectTransform)_root.transform, UiTheme.PanelWidth, new ControlPanelActions(),
                _avatars, new SceneControlsPresenter(null, null, null, null));
            view.Preview.PrefabResolver = () => _prefab;
            view.Preview.MainCameraResolver = () => _camera.GetComponent<Camera>();
            view.Mirror.HeadResolver = () => _head.transform;
            return view;
        }

        private static SessionStatusView Lobby() => SessionStatusPresenter.Build(new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true });

        private static SessionStatusView InRoom() => SessionStatusPresenter.Build(new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Connected, LibraryCompatible = true, RoomName = "r", PeopleCount = 1 });

        [Test]
        public void ThePreviewExistsOnlyWhilethePickerIsOpenInTheLobbyAndIsNeverNetworked()
        {
            var view = NewView();
            int layer = AvatarPreview.Layer;
            Assert.That(layer, Is.GreaterThanOrEqualTo(0), "the AvatarPreview layer is defined in the project");
            Assert.That(view.Preview.Exists, Is.False, "no prefab yet, nothing is made");

            view.ApplySession(Lobby());
            var preview = view.Preview;
            Assert.That(preview.Exists, Is.True, "the picker is open in the lobby");
            Assert.That(preview.Avatar.IsLocalPresetMode, Is.True, "a local preset, made by the factory");
            Assert.That(preview.Avatar.LocalPresetThirdPersonRequested, Is.True, "the SDK's third-person view, so the head is drawn");
            Assert.That(preview.Root.GetComponentsInChildren<Fusion.NetworkObject>(true), Is.Empty, "no network object");
            Assert.That(preview.Root.GetComponentsInChildren<SbAvatarBehaviourFusion>(true), Is.Empty);
            foreach (var t in preview.Root.GetComponentsInChildren<Transform>(true))
                Assert.That(t.gameObject.layer, Is.EqualTo(layer), "everything on the preview layer");
            Assert.That(_camera.GetComponent<Camera>().cullingMask & (1 << layer), Is.EqualTo(0), "the user's own eyes do not draw it");
            Assert.That(view.Mirror.IsRendering, Is.True, "the mirror shows it");
            Assert.That(view.Mirror.MirrorCamera.cullingMask, Is.EqualTo(1 << layer), "and sees only the preview layer");

            view.ApplySession(InRoom());
            Assert.That(preview.Exists, Is.False, "joining a room destroys it");
            Assert.That(view.Mirror.IsRendering, Is.False);
            Assert.That(_camera.GetComponent<Camera>().cullingMask, Is.EqualTo(~0), "the user's camera is as it was");
            Assert.That(Object.FindObjectsByType<SbAvatarEntity>(FindObjectsSortMode.None), Is.Empty, "nothing left in the scene");

            view.ApplySession(Lobby());
            Assert.That(preview.Exists, Is.True, "back in the lobby, back again");
            view.Preview.SetOpen(false, 0);
            Assert.That(preview.Exists, Is.False, "closing the picker destroys it");
            Assert.That(Object.FindObjectsByType<SbAvatarEntity>(FindObjectsSortMode.None), Is.Empty);
        }

        [Test]
        public void ThePreviewFollowsThePresetOnceTheChoiceRests()
        {
            var view = NewView();
            view.ApplySession(Lobby());
            var preview = view.Preview;
            var first = preview.Avatar.gameObject;
            Assert.That(preview.ShownPreset, Is.EqualTo(0));

            _avatars.SetPresetIndex(3);
            view.SyncAvatars(_avatars);
            preview.Tick(Time.unscaledTime);
            Assert.That(preview.ShownPreset, Is.EqualTo(0), "the slider is still moving: no respawn per step");
            _avatars.SetPresetIndex(4);
            view.SyncAvatars(_avatars);
            preview.Tick(Time.unscaledTime + AvatarPreview.PresetRestSeconds + 0.1f);
            Assert.That(preview.ShownPreset, Is.EqualTo(4));
            Assert.That(preview.Avatar.gameObject, Is.Not.SameAs(first));
            Assert.That(first == null, Is.True, "the old preview is destroyed, one preview at a time");
            Assert.That(Object.FindObjectsByType<SbAvatarEntity>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            Assert.That(view.Mirror.IsRendering, Is.True, "the mirror follows the new one");
        }

        [Test]
        public void ThePreviewUsesTheRealTrackingPathWithoutTheNetwork()
        {
            var view = NewView();
            view.ApplySession(Lobby());
            var input = view.Preview.Avatar.LocalPresetInput;
            Assert.That(input.TrackingDelegateOverride, Is.InstanceOf<MetaInputTrackingDelegate>(), "head, hands and fingers as the real local avatar");
            Assert.That(input.ControlDelegateOverride, Is.Not.Null);
            Assert.That(input.SbAvatarBehaviourFusion, Is.Null, "nothing is sent to a network");
        }

        [Test]
        public void ObjectsTheSdkMakesLateLandOnThePreviewLayerAtOnce()
        {
            var view = NewView();
            view.ApplySession(Lobby());
            int layer = AvatarPreview.Layer;
            var late = new GameObject("late-mesh");   // layer 0, as the SDK's new GameObject makes it
            late.transform.SetParent(view.Preview.Root, false);
            Assert.That(late.layer, Is.EqualTo(0));
            view.Preview.Tick(Time.unscaledTime + 0.016f);   // the very next frame, not half a second later
            Assert.That(late.layer, Is.EqualTo(layer));
            var later = new GameObject("later-mesh");
            later.transform.SetParent(late.transform, false);
            view.Preview.Tick(Time.unscaledTime + 0.032f);
            Assert.That(later.layer, Is.EqualTo(layer));
        }

        [Test]
        public void ThePreviewIsOffWhileThePickerCannotBeSeen()
        {
            var view = NewView();
            bool visible = true;
            view.Preview.Allowed = () => visible;
            view.ApplySession(Lobby());
            Assert.That(view.Preview.Exists, Is.True);
            visible = false;   // the tutorial card covers the panel, or it is hidden
            view.Preview.Tick(Time.unscaledTime + 0.1f);
            Assert.That(view.Preview.Exists, Is.False);
            Assert.That(view.Mirror.IsRendering, Is.False, "and so is the mirror");
            Assert.That(_camera.GetComponent<Camera>().cullingMask, Is.EqualTo(~0));
            view.Preview.Tick(Time.unscaledTime + 5f);
            Assert.That(view.Preview.Exists, Is.False, "not remade while hidden");
            visible = true;
            view.Preview.Tick(Time.unscaledTime + 6f);
            Assert.That(view.Preview.Exists, Is.True, "back when the panel is");
        }

        [Test]
        public void OnlyThePreviewLayersBitOfTheMainCameraIsTouched()
        {
            var cam = _camera.GetComponent<Camera>();
            int layer = AvatarPreview.Layer;
            int other = LayerMask.NameToLayer("UI");
            cam.cullingMask = (1 << 0) | (1 << other);   // the preview layer's bit is already off
            var view = NewView();
            view.ApplySession(Lobby());
            Assert.That(cam.cullingMask, Is.EqualTo((1 << 0) | (1 << other)));
            view.ApplySession(InRoom());
            Assert.That(cam.cullingMask & (1 << layer), Is.EqualTo(0), "a bit that was not ours to clear is not given back");

            cam.cullingMask = ~0;
            view.ApplySession(Lobby());
            cam.cullingMask &= ~(1 << other);   // someone else changes the mask meanwhile
            view.Preview.Tick(Time.unscaledTime + 0.1f);
            view.ApplySession(InRoom());
            Assert.That(cam.cullingMask, Is.EqualTo(~(1 << other)), "only our bit comes back; the other change stays");

            // the main camera is replaced (a rig rebuild): the old one gets its bit back, the new one is hidden from
            cam.cullingMask = ~0;
            view.ApplySession(Lobby());
            var second = new GameObject("second-camera", typeof(Camera));
            try
            {
                second.GetComponent<Camera>().cullingMask = ~0;
                view.Preview.MainCameraResolver = () => second.GetComponent<Camera>();
                view.Preview.Tick(Time.unscaledTime + 0.2f);
                Assert.That(cam.cullingMask, Is.EqualTo(~0), "the old camera is whole again");
                Assert.That(second.GetComponent<Camera>().cullingMask & (1 << layer), Is.EqualTo(0));
            }
            finally { Object.DestroyImmediate(second); }
        }
    }
}
#endif
