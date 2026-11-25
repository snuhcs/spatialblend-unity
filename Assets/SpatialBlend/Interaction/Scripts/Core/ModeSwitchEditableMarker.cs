// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Marks an object that is not an <see cref="SbEntity"/> but that the user can edit in Scene Edit, so the mode-switch assist offers
    /// "Edit this object?" over it. The lobby tutorial's practice cube carries one: it is a local prop that moves under the ray, not a
    /// shared entity. <see cref="ModeSwitchAssist.TryResolveEditable"/> reads it; nothing else does.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class ModeSwitchEditableMarker : MonoBehaviour
    {
    }
}
