// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Collections.Generic;
using System.Collections;

using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;

namespace SpatialBlend
{
    public struct OnShareResponse
    {
        public enum Result
        {
            AddAsNew,
            LinkToExisting,
            Reject,
            /// <summary>
            /// The app cannot answer yet (a seat shared before the user linked a local seat to it). The received element is kept unresolved and
            /// its replica kept aside; the app completes it with <see cref="SpatialBlendManager.CompleteDeferredShare"/> once it can, or drops it.
            /// </summary>
            Defer,
        };

        public Result result;
        public GameObject gameObject;

        public static OnShareResponse AddAsNew()
        {
            return new OnShareResponse
            {
                result = Result.AddAsNew,
                gameObject = null,
            };
        }

        public static OnShareResponse Reject()
        {
            return new OnShareResponse
            {
                result = Result.Reject,
                gameObject = null,
            };
        }

        public static OnShareResponse Defer()
        {
            return new OnShareResponse
            {
                result = Result.Defer,
                gameObject = null,
            };
        }

        public static OnShareResponse LinkToExisting(GameObject gameObject)
        {
            return new OnShareResponse
            {
                result = Result.LinkToExisting,
                gameObject = gameObject,
            };
        }

        public bool IsValid()
        {
            if (result == Result.LinkToExisting)
            {
                if (gameObject == null)
                {
                    SpatialBlendDebug.LogError(LogCategory.Scene, "OnShareResponse.LinkToExisting() returned null gameObject");
                    return false;
                }

                // explicit copy for async Action (cannot hold this reference)
                GameObject localGameObject = gameObject;
                SbEntity sbEntity = null;
                UnityMainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    sbEntity = localGameObject.GetComponent<SbEntity>();
                }).Wait();

                if (sbEntity != null && sbEntity.IsAttached())
                {
                    SpatialBlendDebug.LogError(LogCategory.Scene, $"OnShareResponse.LinkToExisting() returned gameObject '{localGameObject.name}' that is already attached to SpaceManager with ID {sbEntity.spaceManager?.ID}. Entity ID: {sbEntity.ID}");
                    return false;
                }
                
                // Additional validation: check if the target object is in a valid state
                if (localGameObject != null && !localGameObject.activeInHierarchy)
                {
                    SpatialBlendDebug.LogWarning(LogCategory.Scene, $"OnShareResponse.LinkToExisting() target object '{localGameObject.name}' is not active in hierarchy. This may cause issues with space connection.");
                }
            }

            return true;
        }
    }
}
