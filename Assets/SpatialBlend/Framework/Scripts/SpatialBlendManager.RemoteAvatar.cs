// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The wiring of every received avatar's embodied driver (design A*, ABI 4). The manager holds one
    /// <see cref="IEmbodiedDriver"/> (an <see cref="OvrAvatarEmbodiedDriver"/>) per remote avatar actor and nothing
    /// else of the avatar's pose. Every frame it asks the driver to publish the poses, read the core's proposals, poll
    /// the core's events, plan and apply. The motion itself, the whole v1-equivalent chain, is the driver's.
    /// The core keeps no copy of the poses, and the manager keeps no copy of the proposals.
    ///
    /// The root, and every other anchor the avatar's entity samples (<see cref="SbEntity.RealizedAnchorNames"/>), are
    /// the driver's: realization targets for those anchors are not applied. Anchor targets (head, hands) of an anchor
    /// the entity does not sample still flow through <see cref="AnchorTargetController"/> and
    /// <see cref="SbRetargetAnchorTargetBridge"/>.
    /// </summary>
    public partial class SpatialBlendManager
    {
        /// <summary>The exception id the first update of every source actor carries (<c>SB_EXCEPTION_JOIN</c>).</summary>
        public const uint JoinExceptionId = SpatialBlendAPI.ExceptionJoin;

        private readonly struct DrivenAvatarWrite
        {
            public readonly IEmbodiedDriver Driver;
            public readonly SbEntity Entity;
            public readonly long Actor;
            public readonly EmbodiedFramePlan Write;

            public DrivenAvatarWrite(IEmbodiedDriver driver, SbEntity entity, long actor, EmbodiedFramePlan write)
            {
                Driver = driver;
                Entity = entity;
                Actor = actor;
                Write = write;
            }
        }

        // Remote avatar actors with an embodied driver. Guarded by _entitiesLock.
        private readonly Dictionary<long, IEmbodiedDriver> _v2EmbodiedDrivers = new Dictionary<long, IEmbodiedDriver>();

        // The adapter to the core. Created with the first driver of a session. Guarded by _entitiesLock.
        private IEmbodiedCore _embodiedCore;

        /// <summary>
        /// Test seam: replaces Time.deltaTime for the driver step, which is zero in EditMode.
        /// Null in production.
        /// </summary>
        internal float? TestOnlyDriverSeconds;

        /// <summary>
        /// The adapter to the core's embodied ABI for <paramref name="space"/>, or null when the loaded
        /// library does not have it.
        /// </summary>
        internal virtual IEmbodiedCore CreateEmbodiedCore(long space)
        {
            if (SpatialBlendAPI.TryCheckEmbodiedPort(out string error))
            {
                return new NativeEmbodiedCore(space);
            }
            _embodiedCoreError = error;
            return null;
        }

        // Why no core could be created, for the degraded state.
        private string _embodiedCoreError;

        // Received avatar actors that have no driver because the core's embodied port is unusable. Guarded by
        // _entitiesLock.
        private readonly HashSet<long> _v2UndrivableAvatars = new HashSet<long>();

        /// <summary>
        /// True when a received avatar could not be given a driver because the loaded core has no usable
        /// embodied port. Those avatars do not move: there is no raw fallback, because the core proposes only
        /// through the port. <see cref="EmbodiedDriverError"/> says why.
        /// </summary>
        public bool EmbodiedDriverDegraded
        {
            get { lock (_entitiesLock) { return _v2UndrivableAvatars.Count > 0; } }
        }

        /// <summary>The reason the embodied driver is degraded, or null.</summary>
        public string EmbodiedDriverError => EmbodiedDriverDegraded
            ? (_embodiedCoreError ?? "The embodied core is unavailable.")
            : null;

        /// <summary>
        /// Gives a received avatar its driver. The avatar's kinetic bounds are the single declared
        /// <see cref="EmbodiedDriverConfig"/>, read from the avatar entity's caps.
        /// </summary>
        internal void ConfigureEmbodiedDriver(SbEntity entity, long actor)
        {
            if (entity == null || actor <= 0 || entity.IsSource || _spaceID <= 0)
            {
                return;
            }

            lock (_entitiesLock)
            {
                if (_v2EmbodiedDrivers.TryGetValue(actor, out IEmbodiedDriver existing) &&
                    existing is OvrAvatarEmbodiedDriver retained && retained.TargetEventsEnabled &&
                    ReferenceEquals(retained.DrivenEntity, entity))
                {
                    // EnsureV2Actor may return this exact native actor on a repeated
                    // link. Keep its lease, goals, drain fences and accepted samples:
                    // native subscription changes do not reset the actor's clocks.
                    if (!retained.TargetLifetimeOpen || retained.TargetEventError != null)
                    {
                        _v2UndrivableAvatars.Add(actor);
                        _embodiedCoreError = retained.TargetEventError ?? "The checked actor lifetime ended; replace the native actor.";
                        Debug.LogError("[SpatialBlend] checked actor cannot be reconfigured: " + _embodiedCoreError);
                    }
                    return;
                }
                if (_v2EmbodiedDrivers.ContainsKey(actor) || _v2UndrivableAvatars.Contains(actor))
                {
                    ForgetEmbodiedDriverLocked(actor);
                }
                if (_embodiedCore == null)
                {
                    _embodiedCore = CreateEmbodiedCore(_spaceID);
                }
                if (_embodiedCore == null)
                {
                    _v2UndrivableAvatars.Add(actor);
                    Debug.LogError($"[SpatialBlend] '{entity.gameObject.name}' cannot be driven and will not move: " +
                        (_embodiedCoreError ?? "the embodied core is unavailable."));
                    return;
                }
                var driver = new OvrAvatarEmbodiedDriver(entity, actor, _embodiedCore,
                    ResolveEmbodiedDriverConfig(entity));
                // The checked target-event path is the only one: an avatar it cannot admit is not driven.
                if (!driver.TryEnableTargetEvents(
                        SbRetargetTransform.RealizationContract.SynchronousExclusiveRootAndHead, out string error))
                {
                    driver.Close();
                    _v2UndrivableAvatars.Add(actor);
                    _embodiedCoreError = error;
                    Debug.LogError("[SpatialBlend] checked target-event admission refused: " + error);
                    return;
                }
                _v2EmbodiedDrivers[actor] = driver;

                // The core solves when a source update arrives, using the latest published pose, and
                // proposes nothing without one. The avatar already stands at its arrival pose, so publish it
                // now: the first source update after arrival then has a sample to solve from.
                driver.PublishPose();
            }
        }

        // The bounds are EmbodiedDriverConfig.Default, declared once. Nothing on the avatar or its prefab carries a
        // second copy, and no app opt-in teleports it: only core's request and a source exception do.
        internal static EmbodiedDriverConfig ResolveEmbodiedDriverConfig(SbEntity entity) =>
            EmbodiedDriverConfig.Default;

        /// <summary>
        /// The source app raises an app-specific exception for its local avatar (a teleport, pressing the button next to
        /// a whiteboard, ...) with an opaque payload of at most 256 bytes. It rides the avatar's next update to every
        /// other space at once; each receiving avatar's driver realizes it as the core's typed target events (a lifecycle
        /// operation, the placements it carries). False when the entity is not a local avatar with an actor, or the payload
        /// is too large.
        /// </summary>
        public bool RaiseSourceException(SbEntity localAvatar, uint exceptionId, byte[] payload = null)
        {
            lock (_entitiesLock)
            {
                return TryGetLocalAvatarActorLocked(localAvatar, out long actor) &&
                       RaiseNativeSourceException(actor, exceptionId, payload);
            }
        }

        /// <summary>
        /// The app moved a received avatar's body from outside the driver. The driver bumps its epoch, which
        /// tells the core to drop what it derived from the old pose. False when the entity has no driver.
        /// Only the avatar's creation and a local frame jump (recenter, tracking origin,
        /// MRUK re-anchor) may call this; it is never an independent path. Its effect (an epoch bump and no write) is
        /// asserted by ManagerContainmentContractTests.
        /// </summary>
        public bool ResetEmbodiedDriver(SbEntity entity)
        {
            if (entity == null)
            {
                return false;
            }
            lock (_entitiesLock)
            {
                if (!_v2ActorByEntity.TryGetValue(entity.ID, out long actor) ||
                    !_v2EmbodiedDrivers.TryGetValue(actor, out IEmbodiedDriver driver))
                {
                    return false;
                }
                driver.Reset();
                return true;
            }
        }

        // Callers hold _entitiesLock.
        private void ForgetEmbodiedDriverLocked(long actor)
        {
            _v2EmbodiedDrivers.TryGetValue(actor, out IEmbodiedDriver driver);
            try
            {
                if (driver is OvrAvatarEmbodiedDriver ovr) ovr.Close();
            }
            finally
            {
                _v2EmbodiedDrivers.Remove(actor);
                _v2UndrivableAvatars.Remove(actor);
                _transferInteracting.Remove(actor);
                _containmentInteractions.Forget(actor, out _);
            }
        }

        /// <summary>
        /// Places a received avatar at its arrival pose through the driver, so the
        /// core learns the placement (a new epoch and SB_EMBODIED_TELEPORTED), with its head at the v1 spawn height whatever
        /// height it was observed at. An avatar the core cannot drive is still placed at the spawn height,
        /// so it is visible, but it does not move afterwards.
        /// Only the avatar's creation may call this; it is never an independent path.
        /// Its effect (a placement and a new epoch) is asserted by ManagerContainmentContractTests.
        /// </summary>
        public bool PlaceReceivedAvatar(SbEntity entity, Vector3 position, Quaternion rotation)
        {
            if (entity == null)
            {
                return false;
            }
            IEmbodiedDriver driver = null;
            lock (_entitiesLock)
            {
                if (_v2ActorByEntity.TryGetValue(entity.ID, out long actor))
                {
                    _v2EmbodiedDrivers.TryGetValue(actor, out driver);
                }
            }
            if (driver is OvrAvatarEmbodiedDriver ovr)
            {
                ovr.PlaceAtArrival(position, rotation);
                return true;
            }
            OvrAvatarEmbodiedDriver.PlaceWithoutCore(entity, position, rotation);
            return false;
        }

        // Reused every frame: the manager keeps no copy of the proposals or the events beyond the frame.
        private readonly ProposalSet _frameProposals = new ProposalSet();
        private readonly List<ActorEvent> _frameEvents = new List<ActorEvent>();

        // Callers hold _entitiesLock. Publishes the avatar's poses, reads the proposals, polls the events and plans the
        // frame. The write happens after the lock is released.
        private void CollectDrivenAvatarWrite(SbEntity entity, long actor, ref List<DrivenAvatarWrite> writes)
        {
            if (_embodiedCore == null || !_v2EmbodiedDrivers.TryGetValue(actor, out IEmbodiedDriver driver))
            {
                return;
            }

            EmbodiedPose embodied = driver.PublishPose();
            bool hasSnapshot = driver.TryReadProposals(_frameProposals);
            if (hasSnapshot)
            {
                // Status/display metadata only for the typed path, never target authority.
                ObserveTransferState(entity, actor, _frameProposals);
            }
            else
            {
                // No set: the plan is made from an empty one, which also releases the charts it would carry.
                _frameProposals.Clear();
            }
            // Missing snapshots cannot suppress the sole typed stream drain or
            // completion of goals retained from a stopped source.
            driver.PollEvents(_frameEvents);

            EmbodiedFramePlan plan = driver.Plan(embodied, _frameProposals, _frameEvents,
                TestOnlyDriverSeconds ?? Time.deltaTime);
            if (plan.IsEmpty)
            {
                return;
            }
            if (writes == null)
            {
                writes = new List<DrivenAvatarWrite>();
            }
            writes.Add(new DrivenAvatarWrite(driver, entity, actor, plan));
        }

        // Callers hold _entitiesLock. True when the avatar's driver samples (and so writes) the anchor.
        private bool IsDriverOwnedAnchorLocked(long actor, string anchorName) =>
            _v2EmbodiedDrivers.TryGetValue(actor, out IEmbodiedDriver driver) && driver.OwnsAnchor(anchorName);

        // Runs outside _entitiesLock because an entity write can run app code. Each write is
        // rechecked, so a removed entity or a forgotten driver is never written.
        private void ApplyDrivenAvatarWrites(List<DrivenAvatarWrite> writes)
        {
            if (writes == null)
            {
                return;
            }

            foreach (DrivenAvatarWrite write in writes)
            {
                lock (_entitiesLock)
                {
                    if (write.Entity == null || !write.Entity.IsAttached() ||
                        !_v2EmbodiedDrivers.TryGetValue(write.Actor, out IEmbodiedDriver current) ||
                        !ReferenceEquals(current, write.Driver) ||
                        !_v2ActorByEntity.TryGetValue(write.Entity.ID, out long actor) || actor != write.Actor)
                    {
                        continue;
                    }
                }
                write.Driver.Apply(write.Write);
            }
        }
    }
}
