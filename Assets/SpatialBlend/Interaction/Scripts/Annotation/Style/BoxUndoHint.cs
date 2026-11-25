// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// The small "B: undo" tag shown beside a box that is being drawn. A trigger that just misses an object in Editing lands on the
    /// floor and starts a box; B undoes it, and the tag says so. The tag sits above the first point, turned to the viewer, in the mode
    /// chip's style (a pill in the theme's panel style, 24 dmm text on the <see cref="UiMetrics.Room"/> surface, growing with distance),
    /// and goes away when the box is finished, cancelled or left.
    /// </summary>
    public sealed class BoxUndoHint : MonoBehaviour
    {
        /// <summary>B steps one point back while a box has points to take back.</summary>
        public const string UndoLabel = "B: undo";
        /// <summary>With no point placed yet another B drops the box itself.</summary>
        public const string CancelLabel = "B: cancel";
        /// <summary>Hands have no B: the Undo button by the mode chip undoes the last point.</summary>
        public const string HandUndoLabel = "Tap Undo";
        public const string HandCancelLabel = "Tap Undo to cancel";

        public const float RiseMetres = 0.14f;
        private static readonly Vector2 Size = new Vector2(300f, 64f);

        private Canvas _canvas;
        private TMP_Text _label;

        public bool Visible => _canvas != null && _canvas.gameObject.activeSelf;
        public string Text => _label != null ? _label.text : string.Empty;
        public Canvas Canvas => _canvas;

        /// <summary>What B does at a creation step: with a point placed it undoes that point, with none the next B cancels the box.</summary>
        public static string LabelFor(int placedPoints, EditInputSource source = EditInputSource.Controller)
        {
            bool hand = source == EditInputSource.Hand;
            if (placedPoints > 0) return hand ? HandUndoLabel : UndoLabel;
            return hand ? HandCancelLabel : CancelLabel;
        }

        public static BoxUndoHint Create(Transform parent)
        {
            var go = new GameObject("BoxUndoHint");
            go.transform.SetParent(parent, false);
            var hint = go.AddComponent<BoxUndoHint>();
            hint.Build();
            hint.Hide();
            return hint;
        }

        private void Build()
        {
            ModeSwitchStyle style = ModeSwitchStyle.Current;
            var rect = UiMetrics.NewCanvas("Canvas", transform, UiMetrics.Room, Size);
            _canvas = rect.GetComponent<Canvas>();
            ControlPanelKit.AddPanel(rect, UiTheme.Pill(Size.y));   // the chip's own pill and hairline
            var label = ModeSwitchUi.NewLabel("Label", rect, Size, Vector2.zero, UndoLabel, style.Text, TextAlignmentOptions.Midline);
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            _label = label;
        }

        /// <summary>
        /// Shows the tag above <paramref name="point"/>, facing <paramref name="eye"/> (null leaves it unturned), with the words for the
        /// number of points placed. It grows with the distance, as the mode prompt does, so it stays readable across a room.
        /// </summary>
        public void Show(Vector3 point, int placedPoints, Transform eye, EditInputSource source = EditInputSource.Controller)
        {
            if (_canvas == null) return;
            _label.text = LabelFor(placedPoints, source);
            transform.position = point + Vector3.up * RiseMetres;
            transform.localScale = Vector3.one;
            UiMetrics.Face(transform, eye, UiMetrics.Room, (RectTransform)_canvas.transform);
            _canvas.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }
    }
}
