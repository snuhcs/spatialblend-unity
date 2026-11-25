// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The object stands for a physical one: this user's own scan of a real object. It is installed where that object is (its
    /// scan box) and never moved to fit; every placement path moves only virtual objects.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PhysicalCounterpart : MonoBehaviour
    {
    }
}
