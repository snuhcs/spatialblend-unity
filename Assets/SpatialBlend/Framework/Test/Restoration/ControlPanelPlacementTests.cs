// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEditor;
using UnityEngine.TestTools.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Pins where the left-controller control panel sits and how big it is (raised a little off the controller and a little
    /// bigger than a tight fit). The numbers live in <see cref="UiTheme"/>; the basis for each is written beside it there.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ControlPanelPlacementTests
    {
        private const string ControllerModel = "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";
        private const string PanelPrefab = "Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab";

        /// <summary>Em height (deg) of the old 0.00052 scale at the worst hand distance; the new scale must beat it.</summary>
        private const float OldBodyEmDegreesAtWorst = 0.90f;

        private sealed class Rig : IDisposable
        {
            public GameObject Anchor, Instance;
            public ControlPanel Panel;
            public RectTransform Canvas;

            public static Rig Build(SessionStatusInput state, bool diagnostics)
            {
                var rig = new Rig { Anchor = new GameObject("LeftControllerAnchor") };
                rig.Instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefab));
                rig.Instance.transform.SetParent(rig.Anchor.transform, false);
                rig.Panel = rig.Instance.GetComponent<ControlPanel>();
                typeof(ControlPanel).GetField("_showDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(rig.Panel, diagnostics);
                Assert.That(rig.Panel.BuildGeneratedLayout(), Is.True);
                rig.Panel.View.ApplySession(SessionStatusPresenter.Build(state));
                rig.Canvas = (RectTransform)rig.Panel.View.Root.parent;
                UnityEngine.Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rig.Canvas);
                UnityEngine.Canvas.ForceUpdateCanvases();
                return rig;
            }

            public Vector3[] Corners()
            {
                var corners = new Vector3[4];
                Canvas.GetWorldCorners(corners);
                return corners;
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Anchor);
            }
        }

        private static readonly SessionStatusInput Lobby = new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true };
        private static readonly SessionStatusInput InRoom = new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, RoomName = "Room 3", PeopleCount = 2 };

        private static readonly SessionStatusInput Diagnostics = new SessionStatusInput
            { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, CarrierText = "Photon fallback", RoomName = "Room 3", PeopleCount = 2 };

        /// <summary>The three panels there are: lobby, in a room, and the development build's Diagnostics block (the tallest).</summary>
        private static (string name, SessionStatusInput state, bool diagnostics)[] States() => new[]
        {
            ("lobby", Lobby, false), ("in-room", InRoom, false), ("diagnostics", Diagnostics, true),
        };

        private static Bounds ControllerBounds()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ControllerModel);
            Assert.That(model, Is.Not.Null, "the Quest 3 (Touch Plus) left controller model ships with the Meta XR core package");
            var instance = (GameObject)UnityEngine.Object.Instantiate(model);
            try
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                Assert.That(renderers, Is.Not.Empty);
                var bounds = renderers[0].bounds;
                foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                return bounds;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void TheControllerModelTopIsTheNumberTheThemeQuotes()
        {
            Bounds controller = ControllerBounds();
            Assert.That(controller.max.y, Is.EqualTo(0.0133f).Within(0.0015f), "UiTheme.HandPanelOffset quotes the model's top as +0.0133 m");
            Assert.That(controller.size.x * 0.5f, Is.EqualTo(0.034f).Within(0.003f), "UiTheme.HandPanelClearanceMetres quotes a head half-width of 0.034 m");
        }

        [Test]
        public void ThePanelsBottomEdgeClearsTheControllerModelByTheClearance()
        {
            Bounds controller = ControllerBounds();
            foreach (var (name, state, diagnostics) in States())
            {
                using (var rig = Rig.Build(state, diagnostics))
                {
                    var corners = rig.Corners();
                    float bottom = corners.Min(c => c.y);
                    Assert.That(bottom, Is.GreaterThanOrEqualTo(controller.max.y + UiTheme.HandPanelClearanceMetres - 1e-4f),
                        $"{name}: the lowest panel corner is at y {bottom:0.000}, the controller's highest point (thumbstick and buttons) at {controller.max.y:0.000}");
                    // Nothing of the panel is inside the controller's box either, whatever the tilt does to the corners.
                    foreach (var c in corners) Assert.That(controller.Contains(c), Is.False, name + ": a corner is inside the controller");
                    Assert.That(rig.Instance.transform.localPosition, Is.EqualTo(UiTheme.HandPanelOffset), "the root sits at the theme's offset");
                    Assert.That(rig.Canvas.pivot, Is.EqualTo(new Vector2(0.5f, 0f)), "the pivot is the bottom edge, so the content grows upward and the bottom never moves");
                    Assert.That(rig.Canvas.position, Is.EqualTo(UiTheme.HandPanelOffset).Using(Vector3EqualityComparer.Instance), "the bottom-centre is at the offset");
                }
            }
        }

        [Test]
        public void ThePanelKeepsASlightTiltTowardTheHead()
        {
            using (var rig = Rig.Build(Lobby, false))
            {
                Assert.That(UiTheme.HandPanelTiltDegrees, Is.InRange(5f, 30f), "a slight tilt");
                Vector3 toViewer = -rig.Canvas.forward;   // a uGUI canvas is read from its back side along -forward
                Assert.That(Vector3.Angle(toViewer, Vector3.back), Is.EqualTo(UiTheme.HandPanelTiltDegrees).Within(0.01f));
                Assert.That(toViewer.y, Is.GreaterThan(0f), "the face looks up, toward the eyes of a user glancing down at the hand");
            }
        }

        [Test]
        public void TheCanvasUnitIsTheThemesMetresPerUnitAndBodyTextMeetsTheAngularMinimum()
        {
            using (var rig = Rig.Build(Lobby, false))
            {
                Assert.That(rig.Canvas.lossyScale.x, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f), "the scale is applied, not left at the old prefab value");
                Assert.That(rig.Canvas.lossyScale.y, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f));

                // Every text the panel draws, by the size it is drawn at: the body text is the 24 unit tier, the smallest is the caption.
                var sizes = rig.Instance.GetComponentsInChildren<TMP_Text>(true).Where(t => t.transform.IsChildOf(rig.Panel.View.Root))
                    .Select(t => t.fontSize).Where(sz => sz > 0f).ToArray();
                Assert.That(sizes, Is.Not.Empty);
                Assert.That(sizes.Min(), Is.GreaterThanOrEqualTo(UiTheme.FontCaption - 0.01f));

                float body = UiTheme.FontBody * rig.Canvas.lossyScale.x;   // em height in metres, measured from the real canvas
                float atWorst = 2f * Mathf.Atan(body * 0.5f / UiTheme.HandWorstMetres) * Mathf.Rad2Deg;
                float atNominal = 2f * Mathf.Atan(body * 0.5f / UiTheme.HandNominalMetres) * Mathf.Rad2Deg;
                Debug.Log($"[ControlPanelPlacement] body em {body * 1000f:0.0} mm: {atNominal:0.00} deg at {UiTheme.HandNominalMetres} m, {atWorst:0.00} deg at {UiTheme.HandWorstMetres} m (before: 1.31 and {OldBodyEmDegreesAtWorst:0.00})");
                Assert.That(atWorst, Is.GreaterThanOrEqualTo(UiTheme.HandComfortEmDegrees), "body text at the arm-extended distance");
                Assert.That(atWorst, Is.GreaterThan(OldBodyEmDegreesAtWorst), "bigger than before");
                Assert.That(atNominal, Is.GreaterThanOrEqualTo(1.4f), "and comfortable at the nominal distance");
                float caption = 2f * Mathf.Atan(UiTheme.FontCaption * rig.Canvas.lossyScale.x * 0.5f / UiTheme.HandWorstMetres) * Mathf.Rad2Deg;
                Assert.That(caption, Is.GreaterThanOrEqualTo(UiTheme.MinEmDegrees), "captions keep the 0.8 deg floor");
            }
        }

        [Test]
        public void HitTargetsAreNoSmallerThanBeforeInPhysicalTerms()
        {
            const float oldRowMetres = 84f * 0.00052f;   // 4.37 cm
            using (var rig = Rig.Build(Lobby, false))
            {
                foreach (var button in new[] { rig.Panel.View.Connect, rig.Panel.View.RebuildNavMesh })
                {
                    float height = button.GetComponent<LayoutElement>().minHeight * rig.Canvas.lossyScale.y;
                    Assert.That(height, Is.GreaterThanOrEqualTo(oldRowMetres), button.name);
                }
                Assert.That(UiTheme.RowHeight * UiTheme.MetresPerUnit, Is.GreaterThanOrEqualTo(oldRowMetres));
            }
        }

        // ---- field of view and overlap, in the head's yaw-only frame (x right, y up, z ahead; the eye is the origin) ----

        private struct AngleBox
        {
            public float AzMin, AzMax, ElMin, ElMax;

            public static AngleBox Of(System.Collections.Generic.IEnumerable<Vector3> points)
            {
                var box = new AngleBox { AzMin = float.MaxValue, AzMax = float.MinValue, ElMin = float.MaxValue, ElMax = float.MinValue };
                foreach (var p in points)
                {
                    float az = Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg, el = Mathf.Atan2(p.y, Mathf.Sqrt(p.x * p.x + p.z * p.z)) * Mathf.Rad2Deg;
                    box.AzMin = Mathf.Min(box.AzMin, az); box.AzMax = Mathf.Max(box.AzMax, az);
                    box.ElMin = Mathf.Min(box.ElMin, el); box.ElMax = Mathf.Max(box.ElMax, el);
                }
                return box;
            }

            /// <summary>Degrees of free space between two boxes; negative when they overlap.</summary>
            public float GapTo(AngleBox other)
            {
                float az = Mathf.Max(other.AzMin - AzMax, AzMin - other.AzMax);
                float el = Mathf.Max(other.ElMin - ElMax, ElMin - other.ElMax);
                return Mathf.Max(az, el);
            }

            public override string ToString() => $"az {AzMin:0.0}..{AzMax:0.0}, el {ElMin:0.0}..{ElMax:0.0}";
        }

        private static AngleBox HudBox(Transform hud, Component element, Vector3 hudInEyeFrame)
        {
            var corners = new Vector3[4];
            ((RectTransform)element.transform).GetWorldCorners(corners);
            return AngleBox.Of(corners.Select(c => hud.InverseTransformPoint(c) + hudInEyeFrame));
        }

        [Test]
        public void InTheReferencePoseThePanelIsInViewAndOverlapsNeitherTheChipNorTheToasts()
        {
            var host = new GameObject("mode-switch");
            var overlay = UiToastOverlay.Create(0);
            try
            {
                var ui = host.AddComponent<ModeSwitchUi>();
                ui.Build(null);
                var hud = ui.ChipObject.transform.parent;
                Vector3 hudInEye = new Vector3(0f, -ModeSwitchUi.HudDropMetres, ModeSwitchUi.HudDistanceMetres);
                var chip = HudBox(hud, ui.ChipObject.transform, hudInEye);
                var modeToast = HudBox(hud, ui.ToastObject.transform, hudInEye);
                var overlayCorners = new Vector3[4];
                ((RectTransform)overlay.transform).GetWorldCorners(overlayCorners);
                Vector3 overlayInEye = UiToastOverlay.PositionFor(Vector3.zero, Vector3.forward);
                var toastOverlay = AngleBox.Of(overlayCorners.Select(c => c - overlay.transform.position + overlayInEye));

                foreach (var (name, state, diagnostics) in States())
                {
                    using (var rig = Rig.Build(state, diagnostics))
                    {
                        rig.Anchor.transform.position = UiTheme.HandReferencePose;
                        var panel = AngleBox.Of(rig.Corners());
                        Debug.Log($"[ControlPanelPlacement] {name}: panel {panel}; chip {chip}; mode toast {modeToast}; toast overlay {toastOverlay}");

                        // In view: gazing at the panel's centre, every corner is within 35 deg both ways (Quest 3 shows about +-48 deg vertically).
                        float azCentre = (panel.AzMin + panel.AzMax) * 0.5f, elCentre = (panel.ElMin + panel.ElMax) * 0.5f;
                        Assert.That(Mathf.Max(panel.AzMax - azCentre, azCentre - panel.AzMin), Is.LessThanOrEqualTo(35f), name + " horizontal extent");
                        Assert.That(Mathf.Max(panel.ElMax - elCentre, elCentre - panel.ElMin), Is.LessThanOrEqualTo(35f), name + " vertical extent");

                        Assert.That(panel.GapTo(chip), Is.GreaterThanOrEqualTo(0.5f), name + " vs the mode chip");
                        Assert.That(panel.GapTo(modeToast), Is.GreaterThanOrEqualTo(0.5f), name + " vs the mode-switch toast");
                        Assert.That(panel.GapTo(toastOverlay), Is.GreaterThanOrEqualTo(0.5f), name + " vs the toast overlay");
                    }
                }
                Assert.That(chip.ElMax, Is.LessThanOrEqualTo(30f), "the chip stays in the comfortable band above the eye line");
                Assert.That(modeToast.ElMax, Is.LessThanOrEqualTo(30f), "so does the mode-switch toast");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(overlay.gameObject);
            }
        }

        [Test]
        public void TheTutorialCardStaysOnThePanelCanvasAtTheNewScale()
        {
            // The card is an overlay on the panel canvas (PanelCanvas), so it takes the panel's size and placement with it; it must not
            // carry a world scale of its own that would leave it behind when the panel is raised and enlarged.
            using (var rig = Rig.Build(Lobby, false))
            {
                Assert.That(rig.Panel.PanelCanvas, Is.SameAs(rig.Canvas));
                var source = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Samples/Scripts/Tutorial/TutorialView.cs"));
                StringAssert.DoesNotContain("MetresPerUnit", source);
                StringAssert.DoesNotContain("localScale", source, "the card sizes itself in canvas units");
            }
        }

        [Test]
        public void ThePrefabCarriesTheThemesPlacementSoTheEditorShowsTheShippedPose()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefab);
            var canvas = prefab.GetComponent<ControlPanel>().GetComponentInChildren<Canvas>(true).GetComponent<RectTransform>();
            Assert.That(prefab.transform.localPosition, Is.EqualTo(UiTheme.HandPanelOffset).Using(Vector3EqualityComparer.Instance));
            Assert.That(prefab.transform.localScale.x * canvas.localScale.x, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f));
            Assert.That(canvas.pivot.y, Is.EqualTo(0f));
            Assert.That(canvas.localRotation.eulerAngles.x, Is.EqualTo(UiTheme.HandPanelTiltDegrees).Within(0.01f));
        }
    }
}
#endif
