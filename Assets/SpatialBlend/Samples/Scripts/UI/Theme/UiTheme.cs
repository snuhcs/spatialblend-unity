// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>Every colour the UI may use. Surfaces carry text only in the Text* tokens (contrast is pinned by tests).</summary>
    public struct UiPalette
    {
        public Color PanelTop, PanelBottom;
        public Color Surface, SurfaceHover, SurfacePressed, SurfaceDisabled;
        public Color Outline;
        public Color Accent, AccentHover, AccentPressed, OnAccent;
        public Color Text, TextMuted, TextDisabled;
        public Color Track, Knob;
        public Color Success, Warning, Error;
        /// <summary>
        /// The outline of REAL objects (the room's scene anchors: walls, floor, furniture). Its own hue on purpose: not the accent
        /// (virtual things, practice targets), not the box annotation's green, not the warning yellow. Never reuse it for annotations.
        /// </summary>
        public Color RealAnchor;
        /// <summary>
        /// The box of a REAL object SHARED with the other room (a shared furniture anchor, the box a joiner installs):
        /// violet, drawn like the scan box (edges, corner brackets, a faint fill) but in its own hue, apart from the scan box's mint
        /// accent, the placement hologram's cyan and amber, the unshared furniture's orange and the error red.
        /// </summary>
        public Color SharedReal;
        public Color Shadow;
        /// <summary>Alpha of the hairline around the panel and cards.</summary>
        public float OutlineAlpha;
    }

    /// <summary>The states of the one button style (<see cref="UiTheme.Button"/>).</summary>
    public struct UiButtonColours
    {
        public Color Normal, Hover, Pressed, Disabled, Label, LabelDisabled;
    }

    /// <summary>How far the reader is from a surface; decides the minimum text size.</summary>
    public enum UiReading
    {
        /// <summary>The controller-attached control panel: held within arm's reach.</summary>
        Hand,
        /// <summary>Panels that float in the room (pop-ups over an object, the scan panel): read from further away.</summary>
        Room,
    }

    /// <summary>
    /// The single token source for the in-headset UI: palette, type scale, spacing, radius, elevation, sizes, and the one panel and
    /// button style. Sizes are angular, in dmm; <see cref="UiMetrics"/> turns them into canvas units per surface (one unit is one dmm on
    /// every surface built in code). The control panel's canvas is 0.583 mm per unit (<see cref="MetresPerUnit"/>), one dmm at its
    /// 0.583 m reading distance, so an 84 unit row is a 4.9 cm hit target. Direction: Graphite, a dark graphite panel with a soft
    /// vertical gradient and one mint accent. The panel style (every floating panel, card, prompt, tag and toast): the PanelTop to
    /// PanelBottom gradient (97% opaque), a <see cref="HairlineWidth"/> hairline, <see cref="RadiusPanel"/> or <see cref="RadiusCard"/>
    /// corners (pills for chips and tags), built by <c>ControlPanelKit.AddPanel</c>.
    /// </summary>
    public static class UiTheme
    {
        /// <summary>
        /// World size of one control-panel canvas unit. Placement of the left-controller panel (this scale, <see cref="HandPanelOffset"/>,
        /// <see cref="HandPanelTiltDegrees"/>) is set only here; <c>ControlPanel</c> applies it on Awake.
        /// Basis: on a device the panel was too tight on the controller, so it was raised and made a bit bigger. The old 0.00052 gave a
        /// 24 unit body em of 0.90 deg at <see cref="HandWorstMetres"/>; this value is the smallest round scale that gives
        /// <see cref="HandComfortEmDegrees"/> (1.0 deg, 25% above the 0.8 deg floor) there: 24 x 0.000583 = 14.0 mm em, 1.46 deg at the
        /// 0.55 m nominal distance (was 1.31). A row is 84 x 0.000583 = 4.9 cm (was 4.4 cm).
        /// </summary>
        public const float MetresPerUnit = 0.000583f;

        /// <summary>Em height (deg) the hand panel's body text must reach at <see cref="HandWorstMetres"/>: the 0.8 deg floor plus 25% margin.</summary>
        public const float HandComfortEmDegrees = 1.0f;

        /// <summary>
        /// Where the panel's BOTTOM-CENTRE sits in the left controller anchor's space (metres): x across the controller, y up (the model's
        /// top, thumbstick included, is at +0.0133, measured from MetaQuestTouchPlus_Left.fbx), z along the aim. The canvas pivot is
        /// its bottom edge, so this point stays put whatever height the content has (lobby 872 units, with Diagnostics 918) and the
        /// panel grows upward. y = 0.07 is the model's top + <see cref="HandPanelClearanceMetres"/> (0.05) + 7 mm; the old placement
        /// centred the panel at y 0.072, so its bottom hung 0.14 m BELOW the controller top, hugging the controller. z = 0.07 keeps the
        /// bottom edge where it was along the aim (old bottom edge z 0.072).
        /// </summary>
        public static readonly Vector3 HandPanelOffset = new Vector3(0f, 0.07f, 0.07f);

        /// <summary>
        /// Clearance of the panel's bottom edge above the controller's highest point. 0.05 m = half the controller head's width (0.034 m,
        /// the FBX x extent is 0.067 m: the right controller can sit that far to the side of a poke on the lowest button without touching
        /// the left model) + 0.016 m for pose jitter and parallax. An engineering choice; Meta publishes no figure for it.
        /// </summary>
        public const float HandPanelClearanceMetres = 0.05f;

        /// <summary>Tilt of the panel about the controller's x axis, degrees: the top leans away so the face looks up at the eyes of a user glancing down at the hand. 20 deg.</summary>
        public const float HandPanelTiltDegrees = 20f;

        /// <summary>
        /// Reference pose for the field-of-view and overlap tests, in the head's yaw-only frame (x right, y up, z ahead; metres from the eye):
        /// the left controller held out left of the left shoulder, forearm lowered, 0.66 m from the eye (between <see cref="HandNominalMetres"/>
        /// and <see cref="HandWorstMetres"/>). An assumption, not a measurement.
        /// </summary>
        public static readonly Vector3 HandReferencePose = new Vector3(-0.32f, -0.45f, 0.35f);

        /// <summary>
        /// Reading distances. Hand: the panel is a child of the left controller anchor, 0.167 m out from it (scene
        /// FreeformCollaboration, ControlPanel instance: local position 0.0002, 0.0724, 0.1499), and the controller is
        /// held about 0.35 to 0.45 m from the eyes, so 0.55 m nominal and 0.8 m with the arm extended. Room: pop-ups
        /// follow an object, so 1.5 m is the assumed far end (an assumption, not read from the scene).
        /// </summary>
        public const float HandNominalMetres = 0.55f, HandWorstMetres = 0.8f, RoomWorstMetres = 1.5f;

        /// <summary>Minimum em height at the worst reading distance, in degrees. Twice Microsoft's 0.4 deg minimum for HoloLens (Quest has about half the pixel density) and the top of its 0.65 to 0.8 deg comfortable range.</summary>
        public const float MinEmDegrees = 0.8f;

        /// <summary>The control panel's width in canvas units (36.6 cm): two columns on one page, no tabs.</summary>
        public const float PanelWidth = 704f;

        /// <summary>The app's title. The one place this string lives; the launcher's productName is separate.</summary>
        public const string ProductTitle = "SpatialBlend - Freeform Collaboration";

        // ---- The design system's sizes (UI unification). Every size below is ANGULAR, in dmm (1 mm seen from 1 m): on a
        // surface built in code one canvas unit is one dmm at its reading distance (UiMetrics), so the same token is the same size on
        // the eye on the control panel, the mode chip, a prompt over an object or the scan card (drawn 4/3 larger).

        // Spacing scale (dmm)
        public const float Space1 = 4f, Space2 = 8f, Space3 = 12f, Space4 = 16f, Space5 = 24f, Space6 = 32f;

        /// <summary>Inner padding of a floating panel, card, prompt or toast (dmm).</summary>
        public const float PanelPadding = Space5;

        // Corner radius scale (dmm): large panels (the control panel, the tutorial card), cards (prompts, toasts, the scan card,
        // rows); buttons, chips and tags are pills (<see cref="Pill"/>).
        public const float RadiusPanel = 40f, RadiusCard = 28f;

        /// <summary>The radius of a pill of <paramref name="height"/>: buttons, chips and tags.</summary>
        public static float Pill(float height) => height * 0.5f;

        /// <summary>The hairline around every panel and card (dmm), drawn in <see cref="UiPalette.Outline"/> at <see cref="UiPalette.OutlineAlpha"/>.</summary>
        public const float HairlineWidth = 1.5f;

        // Type scale (dmm of em). Body and labels are 24 (Meta's floor for body text is 20, UiMetrics.MinBodyDmm); captions 22;
        // titles 30; the control panel's heading 38. FontFine (18) is for the licence attribution line only, never for an instruction.
        public const int FontFine = 18, FontCaption = 22, FontBody = 24, FontLabel = 24, FontTitle = 30, FontHeading = 38;

        /// <summary>Every font size token, for the guard: a runtime text's size is one of these (in its surface's units).</summary>
        public static readonly int[] FontTokens = { FontFine, FontCaption, FontBody, FontLabel, FontTitle, FontHeading };

        /// <summary>A button on a floating widget (prompts, the chip, toasts, the scan card): 64 dmm, three times Meta's 22 dmm floor.</summary>
        public const float ButtonHeight = 64f;
        /// <summary>A secondary tool button (the scan card's +/-, Undo, Clear): 56 dmm.</summary>
        public const float ToolHeight = 56f;

        // Hit targets on the control panel (dmm): a row is 84, its status line 44
        public const float RowHeight = 84f, SliderTrackHeight = 56f, StatusRowHeight = 44f;

        // Elevation: shadow blur radius and downward offset in canvas units
        public static readonly (float blur, float dy)[] Elevation =
        {
            (0f, 0f), (12f, 4f), (28f, 10f), (36f, 14f),
        };

        public static UiPalette Palette => Graphite;

        /// <summary>Em height in degrees of a font size (canvas units) at a distance.</summary>
        public static float AngularSizeDegrees(float fontUnits, float distance) =>
            2f * Mathf.Atan(fontUnits * MetresPerUnit * 0.5f / distance) * Mathf.Rad2Deg;

        /// <summary>Em height in degrees of a font of <paramref name="emMillimetres"/> at a distance.</summary>
        public static float AngularSizeDegreesMm(float emMillimetres, float distance) =>
            2f * Mathf.Atan(emMillimetres * 0.001f * 0.5f / distance) * Mathf.Rad2Deg;

        /// <summary>The smallest em (mm) that meets <see cref="MinEmDegrees"/> at the worst distance of a reading class.</summary>
        public static float MinEmMillimetres(UiReading reading)
        {
            float distance = reading == UiReading.Hand ? HandWorstMetres : RoomWorstMetres;
            return 2f * distance * Mathf.Tan(MinEmDegrees * 0.5f * Mathf.Deg2Rad) * 1000f;
        }

        /// <summary>Font size in canvas units for a millimetre em on a canvas whose world scale is <paramref name="canvasScale"/>.</summary>
        public static float UnitsForMm(float emMillimetres, float canvasScale) => emMillimetres * 0.001f / canvasScale;

        public static Color Hex(string hex, float alpha = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = alpha;
            return c;
        }

        /// <summary>
        /// The one button style: a primary button is the accent with dark text, a secondary one the raised surface with light text; both
        /// lighten on hover, darken on press, and go to the disabled surface with disabled text. A pill (<see cref="Pill"/>).
        /// </summary>
        public static UiButtonColours Button(bool primary)
        {
            var p = Palette;
            return primary
                ? new UiButtonColours { Normal = p.Accent, Hover = p.AccentHover, Pressed = p.AccentPressed, Disabled = p.SurfaceDisabled, Label = p.OnAccent, LabelDisabled = p.TextDisabled }
                : new UiButtonColours { Normal = p.Surface, Hover = p.SurfaceHover, Pressed = p.SurfacePressed, Disabled = p.SurfaceDisabled, Label = p.Text, LabelDisabled = p.TextDisabled };
        }

        public static Color ToneColor(StatusTone tone)
        {
            var p = Palette;
            switch (tone)
            {
                case StatusTone.Busy: return p.Warning;
                case StatusTone.Good: return p.Success;
                case StatusTone.Bad: return p.Error;
                default: return p.TextMuted;
            }
        }

        public static readonly UiPalette Graphite = new UiPalette
        {
            PanelTop = Hex("#2C3039", 0.97f), PanelBottom = Hex("#16181D", 0.97f),
            Surface = Hex("#333844"), SurfaceHover = Hex("#3E4452"), SurfacePressed = Hex("#4A5160"),
            SurfaceDisabled = Hex("#23262D"),
            Outline = Hex("#4A5060"),
            Accent = Hex("#35D9B0"), AccentHover = Hex("#5CE6C3"), AccentPressed = Hex("#20B38E"), OnAccent = Hex("#06110E"),
            Text = Hex("#F2F4F8"), TextMuted = Hex("#B2B9C8"), TextDisabled = Hex("#727A8C"),
            Track = Hex("#7C859B"), Knob = Hex("#FFFFFF"),
            Success = Hex("#4ADE80"), Warning = Hex("#FBBF24"), Error = Hex("#FF8080"),
            RealAnchor = Hex("#FF6B1F"),
            SharedReal = Hex("#B48CFF"),
            Shadow = Hex("#000000"), OutlineAlpha = 0.55f,
        };

        /// <summary><paramref name="colour"/> at <paramref name="alpha"/>: a token's translucent variant (a fill, a wedge, a selection).</summary>
        public static Color WithAlpha(Color colour, float alpha) => new Color(colour.r, colour.g, colour.b, alpha);

        /// <summary>
        /// The desktop spectator's tokens: a window on a Mac, not a headset surface, so its text sizes are
        /// screen units of its 1920 x 1080 reference canvas, not dmm, and are outside the headset type scale's floors.
        /// </summary>
        public static class Spectator
        {
            /// <summary>
            /// The participants' colours on the minimap and in the 3-D view (data, not chrome): eight fixed colours by player id modulo 8,
            /// red, orange, yellow, green (110), sky (205), pink (330), white, light brown, each at least 30 degrees of hue from
            /// <see cref="UiPalette.SharedReal"/> and <see cref="UiPalette.Accent"/>, which the map uses for shared furniture and placed objects.
            /// </summary>
            public static readonly Color[] ParticipantPalette =
            {
                Hex("#FF4D4D"), Hex("#FF9926"), Hex("#FFE640"), Hex("#73E640"),
                Hex("#4DB3FF"), Hex("#FF73BF"), Hex("#F7F7F7"), Hex("#C78C54"),
            };

            /// <summary>The thin dark outline under every person marker on the minimap.</summary>
            public static readonly Color MarkerOutline = Hex("#0A0D0F", 0.9f);

            /// <summary>The viewed room drawn in 3-D: its floor, its low wall strips and its (unshared) furniture boxes.</summary>
            public static readonly Color RoomFloor = Hex("#333842"), RoomWall = Hex("#D9DEE6"), RoomFurniture = Hex("#8C94A3");

            /// <summary>Text sizes in the spectator window's canvas units: the minimap's marker and object labels, its waiting status.</summary>
            public const int MapLabelFont = FontCaption - 6, MapStatusFont = FontCaption - 4;

            /// <summary>The em (metres) of a person's label above their proxy in the 3-D view.</summary>
            public const float WorldLabelEmMetres = 1.6f;
        }
    }
}
