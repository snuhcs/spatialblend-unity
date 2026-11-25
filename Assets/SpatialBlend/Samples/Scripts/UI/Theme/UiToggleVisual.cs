// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The on and off look of a Toggle in a skinned prefab. A uGUI colour block only knows hover and pressed, so this
    /// swaps the block, the content colours (labels and icons) and, for a switch, the knob position when the value changes.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class UiToggleVisual : MonoBehaviour
    {
        public Toggle Toggle;
        public Image Background;
        public Graphic[] Content = new Graphic[0];
        /// <summary>When set, the toggle is a switch: <see cref="Background"/> is its track and this is its knob.</summary>
        public RectTransform Knob;

        private void OnEnable()
        {
            if (Toggle == null) return;
            Toggle.onValueChanged.AddListener(Apply);
            Apply(Toggle.isOn);
        }

        private void OnDisable()
        {
            if (Toggle != null) Toggle.onValueChanged.RemoveListener(Apply);
        }

        public void Apply(bool on)
        {
            var p = UiTheme.Palette;
            if (Toggle == null) return;
            if (Knob != null)
            {
                // A switch: the track is the accent when on, the neutral track colour when off.
                Toggle.colors = ControlPanelKit.Block(on ? p.Accent : p.Track, on ? p.AccentHover : p.Track,
                    on ? p.AccentPressed : p.Track, p.SurfaceDisabled);
                Knob.anchorMin = Knob.anchorMax = new Vector2(on ? 1f : 0f, 0.5f);
                Knob.pivot = new Vector2(on ? 1f : 0f, 0.5f);
                Knob.anchoredPosition = new Vector2(on ? -3f : 3f, 0f);
                return;
            }
            Toggle.colors = on
                ? ControlPanelKit.Block(p.Accent, p.AccentHover, p.AccentPressed, p.SurfaceDisabled)
                : ControlPanelKit.Block(p.Surface, p.SurfaceHover, p.SurfacePressed, p.SurfaceDisabled);
            var content = on ? p.OnAccent : p.Text;
            foreach (var graphic in Content)
                if (graphic != null) graphic.color = content;
        }
    }
}
