// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// What discontinuity of the local frame or the local avatar happened. Only <see cref="AppTeleport"/> is a source
    /// exception: the app declares that it moved the avatar. A change of the frame (recenter, tracking origin, re-anchor)
    /// is not body movement, so it is never raised as an exception.
    /// </summary>
    public enum LocalDiscontinuity
    {
        /// <summary>The headset recentered or the tracking origin changed: the local frame jumped.</summary>
        TrackingOriginReset = 0,
        /// <summary>The MRUK scene was reloaded or the space re-anchored: the room moved under the avatars.</summary>
        SceneReanchored = 1,
        /// <summary>The app placed the local avatar somewhere else instead of the user walking there.</summary>
        AppTeleport = 2,
    }

    /// <summary>
    /// Discontinuities of the local avatar (the source) and of the frame the received avatars stand in. The
    /// relation core cannot tell a jump from a walk, so the source app raises a teleport it performs as a source
    /// exception (<c>sb_raise_source_exception</c>, a source-side event and not a distance threshold; the receiving app
    /// realizes it, by default as a teleport). A change of the frame is different: in
    /// room coordinates no body moved, only its representation did, so it raises no exception and instead resets every
    /// received avatar's embodied driver, so its next published pose carries the new epoch, sequence 1 and
    /// SB_EMBODIED_TELEPORTED.
    ///
    /// Hook sites, each reaching <see cref="ReportLocalDiscontinuity"/>:
    /// - OVR recenter: <c>OVRManager.display.RecenteredPose</c>, hooked from LateUpdate once the display exists.
    /// - Tracking-origin change: <c>OVRManager.TrackingOriginChangePending</c>.
    /// - MRUK or space re-anchoring: <c>MRUK.Instance.RegisterSceneLoadedCallback</c>. The first call is the
    ///   catch-up of an already loaded scene and is not a discontinuity; every later one is.
    /// - Explicit teleport in the app: <see cref="TeleportLocalAvatar"/>.
    /// - There is no rejoin discontinuity: a rejoined peer's local avatar is a new source actor, whose first update
    ///   carries the implicit <c>SB_EXCEPTION_JOIN</c> (ABI 4 section 5).
    /// </summary>
    public partial class SpatialBlendManager
    {
        /// <summary>
        /// The exception id of a <see cref="LocalDiscontinuity"/>: <see cref="LocalDiscontinuityExceptionIdBase"/> plus its
        /// value. Exception ids are the app's own and opaque to the core, which reserves only
        /// <c>SB_EXCEPTION_JOIN</c> (1) for a source actor's first update.
        /// </summary>
        public const uint LocalDiscontinuityExceptionIdBase = 0x1000u;

        public static uint ExceptionIdOf(LocalDiscontinuity kind) => LocalDiscontinuityExceptionIdBase + (uint)kind;

        private OVRDisplay _hookedOvrDisplay;
        private bool _ovrOriginEventHooked;
        private MRUK _hookedMruk;
        private int _mrukSceneLoads;

        /// <summary>
        /// Reports a discontinuity. <paramref name="localAvatar"/> is the avatar it concerns for
        /// <see cref="LocalDiscontinuity.AppTeleport"/>, which raises its source exception. The other kinds are changes of
        /// the frame: they reset every received avatar's driver (new epoch, TELEPORTED) and raise nothing. Returns how
         /// many local avatar actors raised a source exception to the core.
        /// </summary>
        public int ReportLocalDiscontinuity(LocalDiscontinuity kind, SbEntity localAvatar = null)
        {
            int declared = 0;
            lock (_entitiesLock)
            {
                if (kind == LocalDiscontinuity.AppTeleport)
                {
                    if (localAvatar != null && TryGetLocalAvatarActorLocked(localAvatar, out long actor) &&
                        RaiseNativeSourceException(actor, ExceptionIdOf(kind), null))
                    {
                        declared = 1;
                    }
                }
                else
                {
                    // The frame the received avatars stand in jumped: their bodies were placed from outside, so every
                    // driver starts a new epoch. This is never a body source exception (2A).
                    foreach (IEmbodiedDriver driver in _v2EmbodiedDrivers.Values)
                    {
                        driver.Reset();
                    }
                }
            }
            return declared;
        }

        /// <summary>
        /// Explicit teleport of the local avatar in the app: places it and raises the exception, so every receiver
        /// gets a SOURCE_EXCEPTION event and its app realizes it (by default its driver teleports to the proposal).
        /// </summary>
        public bool TeleportLocalAvatar(SbEntity localAvatar, Vector3 worldPosition, float yawDegrees)
        {
            if (localAvatar == null || !localAvatar.IsSource)
            {
                return false;
            }
            Transform t = localAvatar.EntityTransform;
            t.SetPositionAndRotation(worldPosition,
                Quaternion.Euler(t.eulerAngles.x, yawDegrees, t.eulerAngles.z));
            // The relation update that follows must be the one that carries the exception.
            bool declared = ReportLocalDiscontinuity(LocalDiscontinuity.AppTeleport, localAvatar) == 1;
            PushSourceState(localAvatar);
            return declared;
        }

        /// <summary>Seam over the relation update that carries the exception.</summary>
        protected virtual void PushSourceState(SbEntity entity) => UpdateV2EntityState(entity);

        /// <summary>Seam over the native exception, so a test can observe it.</summary>
        protected virtual bool RaiseNativeSourceException(long actor, uint exceptionId, byte[] payload) =>
            SpatialBlendAPI.TryRaiseSourceException(_spaceID, actor, exceptionId, payload);

        // Callers hold _entitiesLock. A local avatar is a source entity that is an avatar and has an actor.
        private bool TryGetLocalAvatarActorLocked(SbEntity entity, out long actor)
        {
            actor = 0;
            return entity != null && entity.IsSource && entity.IsAttached() && IsAvatarObject(entity.gameObject) &&
                   _v2ActorByEntity.TryGetValue(entity.ID, out actor);
        }

        // Called once per LateUpdate. Hooks the headset and MRUK events when they exist.
        private void TickLocalDiscontinuities()
        {
            HookLocalFrameEvents();
        }

        private void HookLocalFrameEvents()
        {
            if (!_ovrOriginEventHooked)
            {
                OVRManager.TrackingOriginChangePending += OnTrackingOriginChangePending;
                _ovrOriginEventHooked = true;
            }
            OVRDisplay display = OVRManager.display;
            if (display != null && _hookedOvrDisplay != display)
            {
                if (_hookedOvrDisplay != null) _hookedOvrDisplay.RecenteredPose -= OnOvrRecentered;
                display.RecenteredPose += OnOvrRecentered;
                _hookedOvrDisplay = display;
            }
            MRUK mruk = MRUK.Instance;
            if (mruk != null && _hookedMruk != mruk)
            {
                _mrukSceneLoads = 0;
                _hookedMruk = mruk;
                mruk.RegisterSceneLoadedCallback(OnMrukSceneLoaded);
            }
        }

        private void UnhookLocalFrameEvents()
        {
            if (_ovrOriginEventHooked)
            {
                OVRManager.TrackingOriginChangePending -= OnTrackingOriginChangePending;
                _ovrOriginEventHooked = false;
            }
            if (_hookedOvrDisplay != null)
            {
                _hookedOvrDisplay.RecenteredPose -= OnOvrRecentered;
                _hookedOvrDisplay = null;
            }
            if (_hookedMruk != null)
            {
                _hookedMruk.SceneLoadedEvent.RemoveListener(OnMrukSceneLoaded);
                _hookedMruk = null;
            }
        }

        internal void OnOvrRecentered() => ReportLocalDiscontinuity(LocalDiscontinuity.TrackingOriginReset);

        private void OnTrackingOriginChangePending(OVRManager.TrackingOrigin origin, OVRPose? pose) =>
            ReportLocalDiscontinuity(LocalDiscontinuity.TrackingOriginReset);

        // MRUK invokes the callback immediately when the scene is already loaded. That catch-up is not a
        // discontinuity; a later load is.
        internal void OnMrukSceneLoaded()
        {
            if (++_mrukSceneLoads > 1)
            {
                ReportLocalDiscontinuity(LocalDiscontinuity.SceneReanchored);
            }
        }
    }
}
