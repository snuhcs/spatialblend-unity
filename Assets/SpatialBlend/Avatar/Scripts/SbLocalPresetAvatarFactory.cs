// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using Oculus.Avatar2;

namespace SpatialBlend
{
    /// <summary>
    /// Explicit factory for an offline/local-preset use of the original SbAvatarPrefab.
    /// It stages the clone under an inactive root so configuration precedes SDK Awake.
    /// No scene, Fusion runner, entitlement, CDN, or microphone source is discovered.
    /// </summary>
    public static class SbLocalPresetAvatarFactory
    {
        public static bool TryCreate(SbAvatarEntity originalPrefab,
                                     Transform ownedParent,
                                     out SbAvatarEntity avatar,
                                     out SbRetargetTransform retarget,
                                     out SbTransformInputManager input,
                                     out string error)
        {
            return TryCreate(originalPrefab, ownedParent, 0, out avatar, out retarget,
                             out input, out error);
        }

        public static bool TryCreate(SbAvatarEntity originalPrefab,
                                     Transform ownedParent,
                                     int presetIndex,
                                     out SbAvatarEntity avatar,
                                     out SbRetargetTransform retarget,
                                     out SbTransformInputManager input,
                                     out string error)
        {
            return TryCreateInternal(originalPrefab, ownedParent, presetIndex, null,
                                     out avatar, out retarget, out input, out error);
        }

        /// <summary>
        /// Creates a staged local-preset clone with one optional explicit articulated
        /// body skeleton.  The factory owns no context: the clone's input manager
        /// creates, freezes, and tears down its own context before activation.
        /// </summary>
        public static bool TryCreate(SbAvatarEntity originalPrefab,
                                     Transform ownedParent,
                                     SbArticulatedBodyTrackingContext.SkeletonDefinition skeleton,
                                     out SbAvatarEntity avatar,
                                     out SbRetargetTransform retarget,
                                     out SbTransformInputManager input,
                                     out string error)
        {
            return TryCreate(originalPrefab, ownedParent, 0, skeleton, out avatar,
                             out retarget, out input, out error);
        }

        public static bool TryCreate(SbAvatarEntity originalPrefab,
                                     Transform ownedParent,
                                     int presetIndex,
                                     SbArticulatedBodyTrackingContext.SkeletonDefinition skeleton,
                                     out SbAvatarEntity avatar,
                                     out SbRetargetTransform retarget,
                                     out SbTransformInputManager input,
                                     out string error)
        {
            if (skeleton == null)
            {
                avatar = null;
                retarget = null;
                input = null;
                error = "The articulated local-preset overload requires an explicit skeleton definition.";
                return false;
            }
            return TryCreateInternal(originalPrefab, ownedParent, presetIndex, skeleton,
                                     out avatar, out retarget, out input, out error);
        }

        private static bool TryCreateInternal(SbAvatarEntity originalPrefab,
                                              Transform ownedParent,
                                              int presetIndex,
                                              SbArticulatedBodyTrackingContext.SkeletonDefinition skeleton,
                                              out SbAvatarEntity avatar,
                                              out SbRetargetTransform retarget,
                                              out SbTransformInputManager input,
                                              out string error)
        {
            avatar = null;
            retarget = null;
            input = null;
            error = null;
            if (originalPrefab == null)
            {
                error = "An original SbAvatarPrefab reference is required.";
                return false;
            }
            if (ownedParent == null)
            {
                error = "An owned parent is required for a local preset avatar.";
                return false;
            }
            if (!ownedParent.gameObject.scene.IsValid() || !ownedParent.gameObject.scene.isLoaded)
            {
                error = "The owned parent must be a loaded-scene instance, not a prefab asset.";
                return false;
            }
            if (originalPrefab.GetComponent<SbAvatarBehaviourFusion>() != null ||
                originalPrefab.GetComponent<Fusion.NetworkObject>() != null)
            {
                error = "The supplied prefab has Fusion network ownership and cannot be used for local presets.";
                return false;
            }

            GameObject staging = null;
            GameObject instance = null;
            bool created = false;
            try
            {
                staging = new GameObject("SbLocalPresetAvatarStaging");
                staging.SetActive(false);
                instance = UnityEngine.Object.Instantiate(originalPrefab.gameObject, staging.transform);
                avatar = instance.GetComponent<SbAvatarEntity>();
                retarget = instance.GetComponent<SbRetargetTransform>();
                input = instance.GetComponent<SbTransformInputManager>();
                if (input == null)
                {
                    input = instance.AddComponent<SbTransformInputManager>();
                }
                if (skeleton != null &&
                    !input.TryConfigureArticulatedBodyTracking(skeleton, out error))
                {
                    return false;
                }
                if (avatar == null || retarget == null ||
                    !avatar.TryConfigureLocalPreset(input, retarget, presetIndex, out error))
                {
                    return false;
                }

                // A cloned prefab may carry a lip-sync context intended for the legacy
                // network voice path. Disable it before this local clone becomes active.
                foreach (OvrAvatarLipSyncContext lipSync in
                         instance.GetComponentsInChildren<OvrAvatarLipSyncContext>(true))
                {
                    lipSync.SetAudioSourceType(LipSyncAudioSourceType.None);
                }

                instance.transform.SetParent(ownedParent, false);
                created = true;
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not create the staged local preset avatar: " + exception.Message;
                return false;
            }
            finally
            {
                if (!created)
                {
                    RetireOwned(instance);
                    avatar = null;
                    retarget = null;
                    input = null;
                }
                RetireOwned(staging);
            }
        }

        private static void RetireOwned(GameObject ownedObject)
        {
            if (ownedObject == null)
            {
                return;
            }
            // Inactive staged clones do not necessarily receive a useful Unity
            // teardown callback before destruction.  Explicitly detach first so a
            // manager-owned callback context cannot outlive a failed factory path.
            foreach (SbTransformInputManager input in
                     ownedObject.GetComponentsInChildren<SbTransformInputManager>(true))
            {
                input.TryDetachArticulatedBodyTracking(out _);
            }
            if (UnityEngine.Application.isPlaying)
            {
                // A staged clone inherits activeSelf from the original prefab.  It
                // must be made inactive before detaching from its inactive staging
                // parent, otherwise deferred destruction would briefly activate the
                // legacy AvatarSDK Awake/native creation path.
                ownedObject.SetActive(false);
                ownedObject.transform.SetParent(null, true);
                UnityEngine.Object.Destroy(ownedObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(ownedObject);
            }
        }
    }
}
