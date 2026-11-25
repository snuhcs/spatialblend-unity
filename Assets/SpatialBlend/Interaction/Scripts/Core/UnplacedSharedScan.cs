// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// A SAM3D scan that arrived <c>AddAsNew</c> and is still on the floor where that share dropped it.
    /// The hover Edit prompt is offered only while this mark is present and the position has not been specified.
    /// Specifying the position (the user places the object) retires the prompt; the object stays.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class UnplacedSharedScan : MonoBehaviour
    {
        public bool PositionSpecified { get; private set; }

        /// <summary>The user has put this copy where it belongs in this room.</summary>
        public void SpecifyPosition() => PositionSpecified = true;
    }
}
