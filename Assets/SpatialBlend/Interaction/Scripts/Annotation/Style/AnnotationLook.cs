// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>How an annotation as a whole is emphasised.</summary>
    public enum AnnotationEmphasis { Normal, Hovered, Selected }

    /// <summary>How one corner bracket is emphasised (a hovered corner, or the corner being dragged or placed).</summary>
    public enum CornerFocus { None, Hover, Active }

    /// <summary>
    /// The look of the annotation graphics (box annotation, its preview, the region volume): graphite UI accent lines with a dark
    /// casing and a soft glow, faint faces, and L-shaped corner brackets as the corner and handle marker. The colours come from
    /// the UI theme palette, so there is one source for the accent. No amber or orange: warm colours belong to real-object anchors.
    /// </summary>
    public sealed class AnnotationLook
    {
        // Colours
        public Color Edge;                 // theme accent
        public Color EdgeHover;            // theme accent, hovered
        public Color Core = Color.white;   // lines blend this into their core
        public float CoreMix = 0.3f;
        public Color Casing = new Color(0.02f, 0.12f, 0.10f, 0.5f);   // dark hairline around a line (a = strength)
        public Color FaceFill;

        // Lines (pixels at any distance)
        public float LinePx = 2.6f;
        public float GlowPx = 6f;
        public float GlowAlpha = 0.42f;
        public float CasingPx = 1.4f;
        public float EdgeWidthScale = 0.8f;
        public float BracketWidthScale = 1.4f;
        public float HoverBracketWidthScale = 1.8f;
        public float ActiveBracketWidthScale = 2.1f;
        public float SelectedEdgeWidthScale = 1.0f;
        public float HoveredEdgeWidthScale = 0.9f;
        public float HiddenAlpha = 0.34f;  // edges behind the box itself

        // Corner brackets: 25% of the edge, at most 9 cm
        public float BracketFraction = 0.25f;
        public float BracketMax = 0.09f;
        public float BracketMin = 0.012f;
        public float PointMarkerSide = 0.045f;   // the small square of brackets that marks a lone point
        public float ActivePointMarkerSide = 0.065f;
        public float AimMarkerSide = 0.09f;
        public float AimRayAlpha = 0.4f;

        // Faces
        public float FaceAlpha = 0.075f;
        public float FaceEdgeAlpha = 0.26f;
        public float FaceEdgeWidth = 0.04f;
        public float FaceBackFactor = 0.5f;
        public float FaceTopFade = 0.5f;

        public static AnnotationLook Graphite()
        {
            var palette = UiTheme.Palette;
            return new AnnotationLook
            {
                Edge = palette.Accent,
                EdgeHover = palette.AccentHover,
                FaceFill = Color.Lerp(palette.AccentPressed, palette.Accent, 0.35f),
            };
        }

        public Color EdgeColor(AnnotationEmphasis emphasis)
        {
            switch (emphasis)
            {
                case AnnotationEmphasis.Selected: return Color.Lerp(EdgeHover, Color.white, 0.4f);
                case AnnotationEmphasis.Hovered: return EdgeHover;
                default: return Edge;
            }
        }

        public Color BracketColor(CornerFocus focus, AnnotationEmphasis emphasis)
        {
            switch (focus)
            {
                case CornerFocus.Active: return Color.Lerp(EdgeHover, Color.white, 0.6f);
                case CornerFocus.Hover: return Color.Lerp(EdgeHover, Color.white, 0.3f);
                default: return EdgeColor(emphasis);
            }
        }

        public float EdgeScale(AnnotationEmphasis emphasis)
        {
            return emphasis == AnnotationEmphasis.Selected ? SelectedEdgeWidthScale : emphasis == AnnotationEmphasis.Hovered ? HoveredEdgeWidthScale : EdgeWidthScale;
        }

        public float BracketScale(CornerFocus focus)
        {
            return focus == CornerFocus.Active ? ActiveBracketWidthScale : focus == CornerFocus.Hover ? HoverBracketWidthScale : BracketWidthScale;
        }

        public float FaceScale(AnnotationEmphasis emphasis)
        {
            return emphasis == AnnotationEmphasis.Selected ? 1.8f : emphasis == AnnotationEmphasis.Hovered ? 1.45f : 1f;
        }
    }
}
