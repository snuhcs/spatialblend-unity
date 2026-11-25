// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    /// <summary>The lobby's choice for one furniture anchor. It lives on the anchor, so a refresh does not forget it.</summary>
    public sealed class FurnitureShareMark : MonoBehaviour
    {
        public bool ShareWhenConnected;
        /// <summary>The user set this from the object's own menu. Defaults do not overwrite it.</summary>
        public bool UserChose;
    }
}
