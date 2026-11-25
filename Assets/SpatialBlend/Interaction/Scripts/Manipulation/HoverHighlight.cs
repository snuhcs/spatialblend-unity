// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Generic hover / selection highlight that any system can toggle.
    /// 
    /// Recommended usage:
    /// - Add this component to any object you want to be "hover-highlightable".
    /// - Assign a child GameObject (e.g. outline mesh, glow ring, etc.) to <see cref="_visualRoot"/>.
    /// - That child can use any material / shader you like.
    /// 
    /// You can package this as a prefab with preconfigured visuals and reuse it across systems.
    /// </summary>
    [MovedFrom("SpatialBlend.Samples.Common")]
    public class HoverHighlight : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Optional visual root to enable/disable when highlighted. If null, this GameObject is used.")]
        private GameObject _visualRoot;

        private bool _isHighlighted;

        private void Awake()
        {
            if (_visualRoot == null)
            {
                _visualRoot = gameObject;
            }

            // Ensure we start disabled; systems will turn it on when appropriate
            SetHighlighted(false);
        }

        /// <summary>
        /// Enable or disable the highlight visual.
        /// </summary>
        public void SetHighlighted(bool highlighted)
        {
            if (_isHighlighted == highlighted) return;
            _isHighlighted = highlighted;

            if (_visualRoot != null)
            {
                _visualRoot.SetActive(highlighted);
            }
        }
    }
}


