// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// One kind of world-space UI surface: how big one canvas unit is in the world, the distance it is designed to be read from, and how
    /// it grows past that distance. The design system's sizes are ANGULAR, in dmm (distance-independent millimetres: 1 dmm is 1 mm seen
    /// from 1 m, a milliradian, Meta's unit for Horizon OS UI), so the same token looks the same size on every surface. A surface built
    /// in code follows the one convention: ONE CANVAS UNIT IS ONE DMM at its reading distance (<see cref="Code"/>: metres per unit =
    /// 0.001 x reading distance), so a token in units is a token in dmm. An authored prefab keeps its own scale and converts with
    /// <see cref="Units"/>.
    /// </summary>
    public readonly struct UiSurface
    {
        public readonly string Name;
        /// <summary>World size of one canvas unit at scale 1 (at or inside the reading distance).</summary>
        public readonly float MetresPerUnit;
        /// <summary>The distance the surface is designed to be read from, metres.</summary>
        public readonly float ReadingMetres;
        /// <summary>
        /// The surface's scale factor over the type scale: 1 on the hand panel, the head-locked layer and the toasts; 4/3 on the scan card
        /// (the capture UI at twice its earlier 1 mm-per-unit size); 5/6 on the room widgets (their text at Meta's 20 dmm minimum,
        /// so a 24 token reads 20 dmm). The type scale itself is one; only these factors differ, each with its reason here.
        /// </summary>
        public readonly float Zoom;
        /// <summary>
        /// The farthest distance the surface still grows to (it scales with distance past <see cref="ReadingMetres"/>, so its angular
        /// size stays the same out to here); equal to the reading distance for a surface that never grows (head- or hand-locked).
        /// </summary>
        public readonly float MaxMetres;

        private UiSurface(string name, float metresPerUnit, float readingMetres, float zoom, float maxMetres)
        {
            Name = name;
            MetresPerUnit = metresPerUnit;
            ReadingMetres = readingMetres;
            Zoom = zoom;
            MaxMetres = Mathf.Max(readingMetres, maxMetres);
        }

        /// <summary>A surface built in code: one canvas unit is <paramref name="zoom"/> dmm at <paramref name="readingMetres"/>.</summary>
        public static UiSurface Code(string name, float readingMetres, float maxMetres, float zoom = 1f) =>
            new UiSurface(name, UiMetrics.MetresPerDmm * readingMetres * zoom, readingMetres, zoom, maxMetres);

        /// <summary>An authored prefab canvas with its own scale; its text sizes are converted with <see cref="Units"/>.</summary>
        public static UiSurface Authored(string name, float metresPerUnit, float readingMetres, float maxMetres, float zoom = 1f) =>
            new UiSurface(name, metresPerUnit, readingMetres, zoom, maxMetres);

        /// <summary>Canvas units per dmm token (1 for a <see cref="Code"/> surface).</summary>
        public float UnitsPerDmm => Zoom * UiMetrics.MetresPerDmm * ReadingMetres / MetresPerUnit;

        /// <summary>The canvas size of a dmm token on this surface.</summary>
        public float Units(float dmm) => dmm * UnitsPerDmm;

        /// <summary>The em in millimetres of a dmm token on this surface at its reading distance (for a canvas scaled elsewhere).</summary>
        public float Millimetres(float dmm) => dmm * Zoom * ReadingMetres;

        /// <summary>How much the surface has grown at <paramref name="distance"/>: 1 up to the reading distance, then distance / reading, capped.</summary>
        public float Growth(float distance) =>
            MaxMetres <= ReadingMetres ? 1f : Mathf.Clamp(distance / ReadingMetres, 1f, MaxMetres / ReadingMetres);

        /// <summary>World size of one canvas unit at <paramref name="distance"/> (with the growth).</summary>
        public float WorldScale(float distance) => MetresPerUnit * Growth(distance);

        /// <summary>The angular size in dmm of <paramref name="units"/> on this surface seen from <paramref name="distance"/> (with the growth).</summary>
        public float Dmm(float units, float distance) => units * WorldScale(distance) / Mathf.Max(1e-4f, distance) / UiMetrics.MetresPerDmm;

        public override string ToString() => Name;
    }

    /// <summary>
    /// The size rules of the design system and the catalogue of every world-space surface. Each world-space canvas the app builds
    /// declares its surface here (<see cref="Declare"/>, which sets its scale), and a surface that follows an object or grows with
    /// distance is placed by <see cref="Face"/>; nothing else sets a UI canvas scale. Pinned by UiConformanceGuardTests.
    /// </summary>
    public static class UiMetrics
    {
        /// <summary>1 dmm in metres at 1 m.</summary>
        public const float MetresPerDmm = 0.001f;

        /// <summary>
        /// Meta's floor for body text on Quest (dmm). Every token used for reading text meets it at its surface's reading distance;
        /// <see cref="UiTheme.FontFine"/> (the licence attribution only) is the one documented exception.
        /// </summary>
        public const float MinBodyDmm = 20f;

        /// <summary>The smallest hit target (dmm, both sides): 22 mm at 1 m, a floor set from Meta's guidance.</summary>
        public const float MinHitDmm = 22f;

        /// <summary>
        /// The control panel on the left controller or palm: read at 0.583 m (the controller is held 0.35 to 0.45 m from the eyes and the
        /// panel stands about 0.17 m out from it, 0.52 to 0.62 m), which is exactly <see cref="UiTheme.MetresPerUnit"/> (the chosen panel
        /// size) at one unit per dmm. Hand-locked: never grows. The tutorial card and the toasts on the panel share it.
        /// </summary>
        public static readonly UiSurface Hand = UiSurface.Code("Hand", UiTheme.MetresPerUnit / MetresPerDmm, UiTheme.MetresPerUnit / MetresPerDmm);

        /// <summary>
        /// The head-locked layer of the mode switch (the chip, its caption, the hands' Undo, the "Back to Interaction" countdown): its
        /// anchor is 0.85 m ahead and the widgets sit up to 0.36 m aside and 0.25 to 0.34 m above the eye, 0.90 to 0.98 m from it; 0.95 m.
        /// </summary>
        public static readonly UiSurface Hud = UiSurface.Code("Hud", 0.95f, 0.95f);

        /// <summary>The toast overlay: 0.9 m ahead and 0.2 m below the eye (<c>UiToastOverlay</c>), 0.92 m away. Head-locked.</summary>
        public static readonly UiSurface Toast = UiSurface.Code("Toast", 0.92f, 0.92f);   // 0.9 ahead, 0.2 below, 0.06 right

        /// <summary>The room widgets' scale factor over the type scale: body text at Meta's 20 dmm minimum (24 x 5/6).</summary>
        public const float RoomZoom = 5f / 6f;

        /// <summary>
        /// Everything that floats over an object in the room (the Edit and Menu prompts, the box undo hint, the edit tag, the scan save
        /// button, the place callout, the furniture share menu, world labels): 1 mm per unit (their size before the unification) up to
        /// 1.2 m, then growing with distance up to 4.5 m, so their 24 unit text is 20 dmm (<see cref="RoomZoom"/>,
        /// Meta's minimum: 1.25 times the old size at 1.5 m) at any distance from 1.2 to 4.5 m (larger inside 1.2 m).
        /// </summary>
        public static readonly UiSurface Room = UiSurface.Code("Room", 1.2f, 4.5f, RoomZoom);

        /// <summary>
        /// The capture card above the scan box (<c>ScanCard</c>): read at 1.5 m and drawn 4/3 of the type scale (the
        /// capture UI at twice its earlier 1 mm-per-unit size, 2 mm per unit there, kept exactly); grows with distance up to 3.75 m (2.5 times), as before.
        /// </summary>
        public static readonly UiSurface ScanCard = UiSurface.Code("ScanCard", 1.5f, 3.75f, 4f / 3f);

        /// <summary>
        /// The authored room panels (prefabs: the object menu <c>ManipulationPopUpUI</c>, the glTF object panel, the legacy mode panel):
        /// a root at scale 2 over a 0.5 mm canvas, so 1 mm per unit: the same rule as <see cref="Room"/> (20 dmm text from 1.2 m), growing
        /// with distance (<c>BillboardToCamera</c>) but only 1.4x, to 1.68 m: the object menu is 0.71 m wide unscaled, so it stays under
        /// 1 m wide and does not catch rays aimed at the objects around it; past 1.68 m its text shrinks with distance
        /// (11 dmm at 3 m; before the unification 8). A box's panel at its fixed height never grows. Measured on the prefab's lossy scale.
        /// </summary>
        public static readonly UiSurface RoomPrefab = UiSurface.Authored("RoomPrefab", 0.001f, 1.2f, 1.2f * 1.4f, RoomZoom);

        /// <summary>
        /// World-sized marks drawn on a canvas (the scan card's progress ring around the box's base): one unit is one millimetre in the
        /// world, sized to the object they mark. They carry no text.
        /// </summary>
        public static readonly UiSurface World = UiSurface.Code("World", 1f, 1f);

        /// <summary>
        /// The legacy authored hand-reach panels (the box annotation, mesh and VO panels; only the Test scene or nothing uses them):
        /// 0.5 mm per unit read at the hand distance. Never grows.
        /// </summary>
        public static readonly UiSurface HandPrefab = UiSurface.Authored("HandPrefab", 0.0005f, Hand.ReadingMetres, Hand.ReadingMetres);

        /// <summary>Every surface, for the tests.</summary>
        public static readonly UiSurface[] All = { Hand, Hud, Toast, Room, ScanCard, RoomPrefab, HandPrefab, World };

        /// <summary>The surface of an authored panel prefab, by path (null for a prefab that is not a world-space panel).</summary>
        public static UiSurface? PrefabSurface(string path)
        {
            if (path == null) return null;
            if (path.EndsWith("/ManipulationPopUpUI.prefab") || path.EndsWith("/GlTFObjectControlPanel.prefab") ||
                path.EndsWith("/InteractionModePanel.prefab"))
                return RoomPrefab;
            if (path.EndsWith("/BoxAnnotationInstance.prefab") || path.EndsWith("/MeshPopupUI.prefab") || path.EndsWith("/VOPanel.prefab"))
                return HandPrefab;
            if (path.EndsWith("/ControlPanel.prefab")) return Hand;   // scaled at runtime by ControlPanel.ApplyPanelScale
            return null;
        }

        /// <summary>
        /// Declares <paramref name="canvas"/>'s surface: sets its scale to the surface's metres per unit (or <paramref name="growth"/> times
        /// it, for a canvas that does not grow through a parent) and tags it, so the guard can find canvases that set a scale of their own.
        /// </summary>
        public static void Declare(RectTransform canvas, UiSurface surface, float growth = 1f)
        {
            canvas.localScale = Vector3.one * (surface.MetresPerUnit * growth);
            var tag = canvas.GetComponent<UiSurfaceTag>();
            if (tag == null) tag = canvas.gameObject.AddComponent<UiSurfaceTag>();
            tag.Surface = surface.Name;
        }

        /// <summary>
        /// Makes <paramref name="canvas"/> a world-space canvas on <paramref name="surface"/> (the one place a UI canvas becomes world-space:
        /// its render mode, its scale and its tag together).
        /// </summary>
        public static void MakeWorldSpace(Canvas canvas, UiSurface surface)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            Declare((RectTransform)canvas.transform, surface);
        }

        /// <summary>A new world-space canvas of <paramref name="size"/> units under <paramref name="parent"/>, declared on <paramref name="surface"/>.</summary>
        public static RectTransform NewCanvas(string name, Transform parent, UiSurface surface, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)go.transform;
            if (parent != null) rect.SetParent(parent, false);
            rect.sizeDelta = size;
            MakeWorldSpace(go.GetComponent<Canvas>(), surface);
            return rect;
        }

        /// <summary>
        /// Turns <paramref name="panel"/> to face <paramref name="eye"/> about the vertical (yaw only) and returns its distance. With a
        /// <paramref name="canvas"/> (one built by <see cref="NewCanvas"/> or <see cref="Declare"/>d), also sets that canvas's scale for the
        /// distance (the surface's growth); null leaves the scale alone. With no eye the surface keeps its reading-distance size.
        /// </summary>
        public static float Face(Transform panel, Transform eye, UiSurface surface, RectTransform canvas)
        {
            if (eye == null) return surface.ReadingMetres;
            Vector3 away = panel.position - eye.position;
            float distance = away.magnitude;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
            if (canvas != null) canvas.localScale = Vector3.one * surface.WorldScale(distance);   // declared when it was built
            return distance;
        }
    }

    /// <summary>Marks a world-space canvas with the <see cref="UiSurface"/> it was declared on (<see cref="UiMetrics.Declare"/>). Data only.</summary>
    [AddComponentMenu("")]
    public sealed class UiSurfaceTag : MonoBehaviour
    {
        public string Surface;
    }
}
