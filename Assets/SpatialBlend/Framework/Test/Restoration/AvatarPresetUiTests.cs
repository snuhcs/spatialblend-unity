// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The preset picker: a mirror of the user's avatar while it is open, the picker gone once a room is joined, and the scene
    /// anchors starting off with the button saying what the mesh does.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarPresetUiTests
    {
        private GameObject _root;
        private GameObject _avatar;
        private GameObject _head;
        private ScriptableObject _settings;

        [TearDown]
        public void TearDown()
        {
            // Edit Mode runs no OnDisable: a mirror camera left enabled renders offscreen between tests (and crashes a no-graphics editor).
            foreach (var mirror in UnityEngine.Object.FindObjectsByType<AvatarMirror>(FindObjectsInactive.Include, FindObjectsSortMode.None)) mirror.SetPickerOpen(false);
            foreach (var preview in UnityEngine.Object.FindObjectsByType<AvatarPreview>(FindObjectsInactive.Include, FindObjectsSortMode.None)) preview.SetOpen(false, 0);
            foreach (var go in new[] { _root, _avatar, _head })
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            if (_settings != null) UnityEngine.Object.DestroyImmediate(_settings);
        }

        private ControlPanelView NewView(out SceneControlsPresenter scene)
        {
            _root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            scene = new SceneControlsPresenter(null, null, null, null);
            var settings = (SpatialBlendSettings)_settings;
            return ControlPanelView.Build((RectTransform)_root.transform, UiTheme.PanelWidth, new ControlPanelActions(),
                new AvatarSettingsPresenter(() => settings, () => { }), scene);
        }

        private static SessionStatusView Lobby() => SessionStatusPresenter.Build(new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true });

        private static SessionStatusView InRoom() => SessionStatusPresenter.Build(new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Connected, LibraryCompatible = true, RoomName = "r", PeopleCount = 1 });

        private void GiveAvatar(AvatarMirror mirror, int layer = 0)
        {
            _avatar = new GameObject("local-avatar") { layer = layer };
            _head = new GameObject("head");
            _head.transform.position = new Vector3(0f, 1.6f, 0f);
            mirror.TargetResolver = () => _avatar != null ? _avatar.transform : null;
            mirror.HeadResolver = () => _head.transform;
        }

        // ---- 1. the mirror ----

        [Test]
        public void TheMirrorRendersOnlyWhileThePickerIsOpenAndAnAvatarExists()
        {
            var view = NewView(out _);
            var mirror = view.Mirror;
            Assert.That(mirror, Is.Not.Null, "the picker carries a mirror");
            Assert.That(view.PresetPickerOpen, Is.True, "the lobby shows the picker");
            Assert.That(mirror.PickerOpen, Is.True);

            mirror.TargetResolver = () => null;
            mirror.Refresh();
            Assert.That(mirror.IsRendering, Is.False, "no local avatar: nothing to mirror, no camera, no texture");
            Assert.That(mirror.Texture, Is.Null);
            Assert.That(mirror.Placeholder.activeSelf, Is.True);

            GiveAvatar(mirror);
            mirror.Refresh();
            Assert.That(mirror.IsRendering, Is.True);
            Assert.That(mirror.Texture.width, Is.EqualTo(AvatarMirror.Resolution));
            Assert.That(mirror.Texture.height, Is.EqualTo(AvatarMirror.Resolution));
            Assert.That(mirror.MirrorCamera.targetTexture, Is.SameAs(mirror.Texture));
            Assert.That(mirror.Image.texture, Is.SameAs(mirror.Texture));
            Assert.That(mirror.Image.enabled, Is.True);
            Assert.That(mirror.Placeholder.activeSelf, Is.False);

            mirror.SetPickerOpen(false);
            Assert.That(mirror.IsRendering, Is.False, "a closed picker stops the camera");
            Assert.That(mirror.MirrorCamera, Is.Null, "and frees it");
            Assert.That(mirror.Texture, Is.Null, "and the texture");
            mirror.SetPickerOpen(true);
            Assert.That(mirror.IsRendering, Is.True, "reopened");
        }

        [Test]
        public void TheMirrorIsFlippedHorizontallyAndFacesTheUser()
        {
            var view = NewView(out _);
            GiveAvatar(view.Mirror);
            view.Mirror.Refresh();
            var uv = view.Mirror.Image.uvRect;
            Assert.That(uv.x, Is.EqualTo(1f));
            Assert.That(uv.width, Is.EqualTo(-1f), "like a real mirror, left and right swap");

            AvatarMirror.ComputePose(new Vector3(0f, 1.6f, 0f), new Vector3(0.2f, -0.3f, 1f), out var position, out var rotation);
            Assert.That(position.y, Is.EqualTo(1.6f).Within(1e-4f), "at the face's height");
            Assert.That(position.z, Is.GreaterThan(0f), "in front of where the head looks");
            Vector3 toUser = new Vector3(0f, 1.6f, 0f) - position;
            Assert.That(Vector3.Angle(rotation * Vector3.forward, toUser), Is.LessThan(20f), "looking back at the user");
            Assert.That(Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized,
                Is.EqualTo(Vector3.ProjectOnPlane(toUser, Vector3.up).normalized).Using(new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-3f)));
        }

        [Test]
        public void TheMirrorCullsToTheAvatarLayerWhenItHasOne()
        {
            var view = NewView(out _);
            GiveAvatar(view.Mirror, 9);
            view.Mirror.Refresh();
            Assert.That(view.Mirror.MirrorCamera.cullingMask, Is.EqualTo(1 << 9));
            Assert.That(AvatarMirror.CullingMaskFor(0) & (1 << 5), Is.EqualTo(0), "on the shared default layer: everything but the UI");
            Assert.That(AvatarMirror.CullingMaskFor(0) & 1, Is.EqualTo(1));
        }

        // ---- 2. the picker leaves with the session ----

        [Test]
        public void ThePresetPickerIsGoneInARoomAndBackInTheLobby()
        {
            var view = NewView(out _);
            GiveAvatar(view.Mirror);
            view.Mirror.Refresh();
            Assert.That(view.PresetSection.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.UsePreset.Toggle.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.Preset.Slider.gameObject.activeInHierarchy, Is.True);

            view.ApplySession(InRoom());
            Assert.That(view.PresetPickerOpen, Is.False);
            Assert.That(view.UsePreset.Toggle.gameObject.activeInHierarchy, Is.False, "no preset buttons in a room");
            Assert.That(view.Preset.Slider.gameObject.activeInHierarchy, Is.False);
            Assert.That(view.MirrorCard.gameObject.activeSelf, Is.False, "and no mirror card");
            Assert.That(view.Mirror.IsRendering, Is.False, "and no mirror either");
            Assert.That(view.Mirror.Texture, Is.Null);

            view.ApplySession(Lobby());
            Assert.That(view.PresetPickerOpen, Is.True, "restored when back in the lobby");
            Assert.That(view.UsePreset.Toggle.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.Preset.Slider.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.Mirror.PickerOpen, Is.True);
            Assert.That(view.MirrorCard.gameObject.activeSelf, Is.True);
        }

        // ---- 3. anchors start off ----

        private ControlPanel NewPanel(out EffectMesh effectMesh, out MeshRenderer renderer, out Material material)
        {
            _root = new GameObject("control-panel");
            _root.SetActive(false);
            var panel = _root.AddComponent<ControlPanel>();
            effectMesh = _root.AddComponent<EffectMesh>();
            SetField(panel, "_effectMesh", effectMesh);
            var meshObject = new GameObject("floor-mesh");
            meshObject.transform.SetParent(_root.transform);
            material = new Material(Shader.Find("Sprites/Default"));
            renderer = meshObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            var anchorObject = new GameObject("anchor");
            anchorObject.transform.SetParent(meshObject.transform);
            var anchor = anchorObject.AddComponent<MRUKAnchor>();
            ((IDictionary)GetField(effectMesh, "effectMeshObjects"))[anchor] =
                new EffectMesh.EffectMeshObject { effectMeshGO = meshObject };
            return panel;
        }

        [Test]
        public void TheAnchorsStartOffAndTheButtonMatchesAtStartupAndAfterEveryToggle()
        {
            var panel = NewPanel(out var effectMesh, out var renderer, out var material);
            try
            {
                Assert.That(renderer.enabled, Is.True, "a mesh the effect mesh just made is drawn");
                panel.ApplyDefaultAnchorsOff();   // what Start does
                var view = ControlPanelView.Build((RectTransform)new GameObject("canvas", typeof(RectTransform), typeof(Canvas)).transform,
                    UiTheme.PanelWidth, new ControlPanelActions(), panel.AvatarPresenter, panel.ScenePresenter);
                SetField(panel, "_view", view);
                var knob = view.Anchors.Knob;
                Assert.That(panel.ScenePresenter.AnchorsVisible, Is.False, "the default");
                Assert.That(renderer.enabled, Is.False, "the mesh is hidden at startup, not only the button");
                Assert.That(effectMesh.HideMesh, Is.True, "and meshes made later start hidden");
                Assert.That(view.Anchors.Toggle.isOn, Is.False);
                Assert.That(knob.anchorMin.x, Is.EqualTo(0f), "knob at the off end");

                view.Anchors.Toggle.isOn = true;
                Assert.That(renderer.enabled, Is.True);
                Assert.That(effectMesh.HideMesh, Is.False);
                Assert.That(view.Anchors.Toggle.isOn, Is.True);
                Assert.That(knob.anchorMin.x, Is.EqualTo(1f), "knob at the on end");

                view.Anchors.Toggle.isOn = false;
                Assert.That(renderer.enabled, Is.False);
                Assert.That(effectMesh.HideMesh, Is.True);
                Assert.That(knob.anchorMin.x, Is.EqualTo(0f));

                // The tutorial forces them on and off through the presenter and the button follows it.
                panel.ScenePresenter.SetAnchorsVisible(true);
                Assert.That(renderer.enabled, Is.True);
                Assert.That(view.Anchors.Toggle.isOn, Is.True, "the button follows the presenter");
                Assert.That(knob.anchorMin.x, Is.EqualTo(1f));
                panel.ScenePresenter.SetAnchorsVisible(false);
                Assert.That(view.Anchors.Toggle.isOn, Is.False);
                Assert.That(renderer.enabled, Is.False);
                UnityEngine.Object.DestroyImmediate(view.Root.GetComponentInParent<Canvas>().gameObject);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static object GetField(object target, string name)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            throw new MissingFieldException(name);
        }

        private static void SetField(object target, string name, object value)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(target, value); return; }
            }
            throw new MissingFieldException(name);
        }
    }
}
#endif
