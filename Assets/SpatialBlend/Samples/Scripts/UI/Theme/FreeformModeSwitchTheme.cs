// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions.UI;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Themes the mode-switch widgets (the prompt, the countdown toast and the mode chip of <c>ModeSwitchAssist</c>) with the app's
    /// <see cref="UiTheme"/> palette and the <see cref="UiFonts"/> Pretendard SemiBold. Since the UI unification the widgets'
    /// default style is the theme itself (<see cref="ModeSwitchStyle.FromTheme"/>); installing it only makes that explicit.
    /// </summary>
    public static class FreeformModeSwitchTheme
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Install() => ModeSwitchStyle.Resolve = Resolve;

        public static ModeSwitchStyle Resolve() => ModeSwitchStyle.FromTheme();
    }
}
