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
    /// As in v1, the scene-anchor toggle hides or shows
    /// the effect-mesh renderers (<c>EffectMesh.ToggleEffectMeshVisibility</c>). abi4 swapped the mesh material
    /// between two assets, which left the mesh drawn while connected.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SceneAnchorVisibilityToggleTests
    {
        private GameObject _panelObject;
        private GameObject _meshObject;
        private Material _material;

        [TearDown]
        public void TearDown()
        {
            if (_panelObject != null) UnityEngine.Object.DestroyImmediate(_panelObject);
            if (_meshObject != null) UnityEngine.Object.DestroyImmediate(_meshObject);
            if (_material != null) UnityEngine.Object.DestroyImmediate(_material);
        }

        [Test]
        public void TheToggleHidesAndShowsTheEffectMeshRenderersAndLeavesTheirMaterial()
        {
            _panelObject = new GameObject("control-panel");
            _panelObject.SetActive(false); // no Awake/OnEnable work for the generated layout
            var panel = _panelObject.AddComponent<ControlPanel>();
            var effectMesh = _panelObject.AddComponent<EffectMesh>();
            SetField(panel, "_effectMesh", effectMesh);

            _meshObject = new GameObject("floor-mesh");
            _material = new Material(Shader.Find("Sprites/Default"));
            var renderer = _meshObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            var anchorObject = new GameObject("anchor");
            anchorObject.transform.SetParent(_meshObject.transform);
            var anchor = anchorObject.AddComponent<MRUKAnchor>();
            var entry = new EffectMesh.EffectMeshObject { effectMeshGO = _meshObject };
            ((IDictionary)GetField(effectMesh, "effectMeshObjects"))[anchor] = entry;
            Assert.That(renderer.enabled, Is.True);

            Invoke(panel, "OnSceneAnchorVisibleToggled", false);
            Assert.That(renderer.enabled, Is.False, "off hides the scene-anchor mesh");

            Invoke(panel, "OnSceneAnchorVisibleToggled", true);
            Assert.That(renderer.enabled, Is.True, "on shows it again");
            // The toggle still does not swap materials back and forth (the hide/show is the renderer's); turning the anchors on gives the
            // meshes ONE shared copy of their material in the real-anchor colour.
            Assert.That(renderer.sharedMaterial, Is.Not.SameAs(_material), "a themed copy, the original asset is never edited");
            Assert.That(renderer.sharedMaterial.color, Is.EqualTo(UiTheme.Palette.RealAnchor).Using(new UnityEngine.TestTools.Utils.ColorEqualityComparer(0.001f)));
            Assert.That(_material.color, Is.Not.EqualTo(UiTheme.Palette.RealAnchor).Using(new UnityEngine.TestTools.Utils.ColorEqualityComparer(0.001f)), "the source material is untouched");
            var themed = renderer.sharedMaterial;
            Invoke(panel, "OnSceneAnchorVisibleToggled", false);
            Invoke(panel, "OnSceneAnchorVisibleToggled", true);
            Assert.That(renderer.sharedMaterial, Is.SameAs(themed), "toggling again does not make more copies");
            Assert.That(effectMesh.MeshMaterial, Is.SameAs(themed), "meshes made later use it too");

            // Destroying the panel puts the package's own material back and drops the copy.
            typeof(ControlPanel).GetMethod("OnDestroy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(panel, null);
            Assert.That(renderer.sharedMaterial, Is.SameAs(_material), "the original is back on the mesh");
            Assert.That(effectMesh.MeshMaterial, Is.Not.SameAs(themed));
            Assert.That(themed == null, Is.True, "the copy is destroyed, nothing leaks");
        }

        [Test]
        public void TheControlPanelNoLongerCarriesTheTwoMaterialSlots()
        {
            Assert.That(typeof(ControlPanel).GetField("_effectMeshMaterialOn", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(ControlPanel).GetField("_effectMeshMaterialOff", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
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

        private static void Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(name);
            method.Invoke(target, args);
        }
    }
}
#endif
