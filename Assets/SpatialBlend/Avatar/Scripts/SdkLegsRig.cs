// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;
using Oculus.Avatar2;
using Oculus.Avatar2.Experimental;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The Meta Avatars SDK legs rig the received avatar walks with. <see cref="OvrAvatarAnimationBehavior"/> (on
    /// <c>SbAvatarPrefab</c>, the avatar shown while a remote person walks between objects) loads its default rig with
    /// <c>Resources.Load("RTR00003")</c> in <c>OnUserAvatarLoaded</c>, puts its <see cref="MecanimLegsAnimationController"/>
    /// on the avatar, and that controller turns the tracked head's motion (the retarget head the embodied driver walks)
    /// into the walk, turn and crouch parameters of the rig's Animator. The rig ships only in the SDK's LegsNetworkLoopback
    /// scene sample (Samples~), so tools/setup_sdks.py copies it, unmodified and with the package's GUIDs, from the user's own
    /// package cache into the git-ignored Assets/SpatialBlend/Avatar/LegsRig (tools/vendor-meta-avatar-legs-rig.py; LICENSE.md and
    /// NOTICE.md there; Meta sample material is not redistributed in this repository). Without it the SDK calls <c>Instantiate(null)</c>, which throws ("The Object you want to
    /// instantiate is null", OvrAvatarAnimationBehavior.cs:856) before the behaviour registers its animation
    /// (RegisterAnimatedAvatar), so its Update never sends the root angle and translation and the legs never walk.
    /// <see cref="GuardBehaviour"/> turns that into one clear error and a disabled behaviour; <c>SdkLegsRigBuildGuard</c>
    /// refuses a player build without the rig.
    /// </summary>
    public static class SdkLegsRig
    {
        /// <summary>The resource name the SDK loads (<c>OvrAvatarAnimationBehavior._defaultRtRigPrefab</c>).</summary>
        public const string ResourceName = "RTR00003";

        public const string SetupHint =
            "It is installed (git-ignored) under Assets/SpatialBlend/Avatar/LegsRig from your Meta Avatars package cache: run python3 tools/setup_sdks.py (or tools/vendor-meta-avatar-legs-rig.py --apply).";

        private static readonly FieldInfo CustomAnimationOnlyField =
            typeof(OvrAvatarAnimationBehavior).GetField("_customAnimationOnly", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The rig as the SDK finds it: <c>Resources.Load&lt;GameObject&gt;("RTR00003")</c>.</summary>
        public static GameObject Load() => Resources.Load<GameObject>(ResourceName);

        /// <summary>Returns null when the rig is what the SDK needs to animate the legs, else the reason.</summary>
        public static string Verify(GameObject rig)
        {
            if (rig == null)
            {
                return "the SDK legs rig Resources/" + ResourceName + " is not in the project. " + SetupHint;
            }
            if (rig.GetComponent<MecanimLegsAnimationController>() == null)
            {
                return "Resources/" + ResourceName + " has no MecanimLegsAnimationController. " + SetupHint;
            }
            var animator = rig.GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return "Resources/" + ResourceName + " has no Animator with a controller. " + SetupHint;
            }
            var root = rig.GetComponent<OvrAvatarRigRootIdentifier>();
            if (root == null || root.RigRoot == null)
            {
                return "Resources/" + ResourceName + " has no OvrAvatarRigRootIdentifier with its rig root. " + SetupHint;
            }
            return null;
        }

        /// <summary>Whether the behaviour loads the default rig at all (it does not with <c>_customAnimationOnly</c>).</summary>
        public static bool UsesDefaultRig(OvrAvatarAnimationBehavior behaviour)
        {
            if (behaviour == null) return false;
            return CustomAnimationOnlyField == null || !(bool)CustomAnimationOnlyField.GetValue(behaviour);
        }

        /// <summary>
        /// Call before the behaviour's Start (it subscribes to the avatar's load events there): from the avatar's Awake.
        /// An enabled behaviour that would load a missing or broken rig is disabled with one error naming the fix, instead
        /// of throwing inside the SDK's load callback and leaving a half-initialised behaviour system. Returns whether the
        /// behaviour will animate the legs.
        /// </summary>
        public static bool GuardBehaviour(OvrAvatarAnimationBehavior behaviour, Func<GameObject> load = null)
        {
            if (behaviour == null || !behaviour.enabled) return false;
            if (!UsesDefaultRig(behaviour)) return true;
            string problem = Verify((load ?? Load)());
            if (problem == null) return true;
            behaviour.enabled = false;
            Debug.LogError("[SdkLegsRig] " + behaviour.gameObject.name + ": legs locomotion is off, " + problem);
            return false;
        }
    }
}
