// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Base class for interaction motifs.
    /// Provides grab state management and mode gating.
    /// </summary>
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public abstract class InteractionMotif : MonoBehaviour
    {
        [Header("Mode Gate")]
        [SerializeField] private bool _respectModeGate = true;
        [SerializeField] private InteractionMode _requiredMode = InteractionMode.SceneEdit;

        [Header("Debug")]
        [SerializeField] protected bool _debugLog = false;

        // Internal state
        protected bool _isGrabbed = false;

        /// <summary>
        /// Whether this motif is currently active (has claimed exclusive).
        /// </summary>
        protected bool IsActiveByMe => _isGrabbed;

        /// <summary>Whether this motif holds its exclusive claim (a grab or drag in progress). Read by <see cref="ObjectMoveHistory"/>.</summary>
        public bool IsClaimed => _isGrabbed;

        /// <summary>
        /// Whether this motif only acts in <paramref name="mode"/> (mode gate on, required mode equal).
        /// <see cref="ModeSwitchAssist"/> reads it to tell an object that can be edited from one that cannot.
        /// </summary>
        public bool IsGatedTo(InteractionMode mode) => _respectModeGate && _requiredMode == mode;

        /// <summary>
        /// Claim exclusive state for this motif.
        /// </summary>
        protected void ClaimExclusive()
        {
            _isGrabbed = true;
            OnExclusiveClaimed();
        }

        /// <summary>
        /// Release exclusive state.
        /// </summary>
        protected void ReleaseExclusive()
        {
            _isGrabbed = false;
            OnExclusiveReleased();
        }

        /// <summary>
        /// Called when exclusive priority is claimed.
        /// Override to add custom behavior.
        /// </summary>
        protected virtual void OnExclusiveClaimed() { }

        /// <summary>
        /// Called when exclusive priority is released.
        /// Override to add custom behavior.
        /// </summary>
        protected virtual void OnExclusiveReleased() { }

        /// <summary>
        /// Ensure exclusive is released when disabled.
        /// </summary>
        protected virtual void OnDisable()
        {
            if (_isGrabbed)
            {
                ReleaseExclusive();
            }
        }

        /// <summary>
        /// Check if application is focused (for VR input handling).
        /// In VR, we check OVRManager.hasVrFocus instead of Application.isFocused
        /// because the Unity editor may not have focus while the headset does.
        /// </summary>
        protected bool IsFocused => FocusOverride != null ? FocusOverride() : OVRManager.hasVrFocus || Application.isFocused;

        /// <summary>Replaces the focus test (an EditMode run has no focused player). Null reads the headset's and the player's focus.</summary>
        internal static System.Func<bool> FocusOverride { get; set; }

        /// <summary>
        /// Check if current interaction mode allows this motif.
        /// </summary>
        protected bool IsModeAllowed => !_respectModeGate || InteractionModeManager.IsMode(_requiredMode);

        /// <summary>
        /// Whether debug logging is enabled.
        /// </summary>
        public bool DebugLog => _debugLog;
    }
}
