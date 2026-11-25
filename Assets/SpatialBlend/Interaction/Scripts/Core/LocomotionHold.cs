// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Interaction.Locomotion;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Holds the rig's thumbstick locomotion off while a placement grab uses the thumbstick. The FC rig (OVRInteractionComprehensive)
    /// carries the Interaction SDK's controller locomotion: turning on stick left/right, teleport and step on stick up/down. A grabbed object
    /// turns and moves with the same stick, and a panel has always pushed and pulled with it, so while any grab holds this, those
    /// components are disabled, and they are re-enabled (only the ones this disabled) when the last holder lets go.
    /// </summary>
    public static class LocomotionHold
    {
        private static readonly HashSet<object> s_Holders = new HashSet<object>();
        private static readonly List<Behaviour> s_Disabled = new List<Behaviour>();

        /// <summary>The locomotion components to hold. Replaced by a test; the default finds the Interaction SDK's stick locomotion in the scene.</summary>
        public static Func<IEnumerable<Behaviour>> Finder { get; set; } = FindStickLocomotion;

        public static bool IsHeld => s_Holders.Count > 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetStatics()
        {
            s_Holders.Clear();
            s_Disabled.Clear();
            Finder = FindStickLocomotion;
        }

        /// <summary>Starts a hold for <paramref name="owner"/>. The first holder disables the locomotion; a second hold by the same owner counts once.</summary>
        public static void Hold(object owner)
        {
            if (owner == null || !s_Holders.Add(owner) || s_Holders.Count > 1) return;
            foreach (Behaviour behaviour in Finder())
            {
                if (behaviour == null || !behaviour.enabled) continue;
                behaviour.enabled = false;
                s_Disabled.Add(behaviour);
            }
        }

        /// <summary>Ends <paramref name="owner"/>'s hold. When the last holder lets go, what the hold disabled is enabled again.</summary>
        public static void Release(object owner)
        {
            if (owner == null || !s_Holders.Remove(owner) || s_Holders.Count > 0) return;
            foreach (Behaviour behaviour in s_Disabled)
                if (behaviour != null) behaviour.enabled = true;
            s_Disabled.Clear();
        }

        private static IEnumerable<Behaviour> FindStickLocomotion()
        {
            foreach (var turner in UnityEngine.Object.FindObjectsByType<LocomotionAxisTurnerInteractor>(FindObjectsSortMode.None)) yield return turner;
            foreach (var teleport in UnityEngine.Object.FindObjectsByType<TeleportInteractor>(FindObjectsSortMode.None)) yield return teleport;
            foreach (var step in UnityEngine.Object.FindObjectsByType<StepLocomotionBroadcaster>(FindObjectsSortMode.None)) yield return step;
            foreach (var slide in UnityEngine.Object.FindObjectsByType<SlideLocomotionBroadcaster>(FindObjectsSortMode.None)) yield return slide;
        }
    }
}
