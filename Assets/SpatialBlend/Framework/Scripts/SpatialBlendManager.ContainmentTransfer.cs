// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Containment transfer on the app side.
    ///
    /// While an actor's root is inside a transfer leaf (a region both spaces declared whose shapes are congruent), that leaf
    /// is the actor's whole scope: the empty-scope mapping maps the root through the leaf's chart onto the receiver's
    /// own copy. The core says so on every update with the TRANSFER flag of the root proposal (ABI 4.3). There is no
    /// contained event and no per-actor state to read. The app does three things with it:
    /// 1. The interaction value of a received avatar is the TRANSFER flag AND the root anchor's ARRIVED, read from the
    ///    proposal set the driver reads every frame: the realised body is in the leaf, not just the proposal. The display
    ///    switch (Sb and Meta replay, Default and ZoneBased) reads that value, and Start and Terminate are its
    ///    frame-to-frame change. Nothing is stored for the flag, and the app holds no hysteresis. The local avatar has
    ///    no proposal set; its value is the source-side transfer fact the core computed on its latest update
    ///    (<c>sb_get_source_transfer</c>, ABI 4.4), which drives its animation enable and state-filter bypass. The
    ///    app holds no point test of its own: the core is the single judgment.
    /// 2. The remote root is the embodied driver's: the root proposal IS the mapped pose, and the driver
    ///    follows it like any other target (<see cref="SpatialBlend.Internal.EmbodiedDriver"/>).
    /// 3. Facing stays global: the app does not filter Facing targets by where their referent is
    ///    (an app-side filter would be a scope override).
    ///
    /// There is no AffordancePose binding on the root any more.
    /// </summary>
    public partial class SpatialBlendManager
    {
        private readonly ContainmentInteractionMapper _containmentInteractions = new ContainmentInteractionMapper();
        private readonly List<ContainmentInteractionTransition> _containmentTransitions =
            new List<ContainmentInteractionTransition>();
        // Region elements declared as transfer leaves on this space.
        private readonly HashSet<long> _containmentRegions = new HashSet<long>();
        // The actors whose current root proposal reads TRANSFER and ARRIVED. Recomputed from every proposal set read, so a
        // counterpart that appears late, or a body that is still walking, is simply the value of the next read.
        // Guarded by _entitiesLock.
        private readonly HashSet<long> _transferInteracting = new HashSet<long>();
        // Start and Terminate decided under _entitiesLock and raised after it is released. Guarded by _entitiesLock.
        private readonly List<PendingInteraction> _pendingInteractions = new List<PendingInteraction>();
        private bool _containmentFeatureAvailable;

        private readonly struct PendingInteraction
        {
            public readonly SbEntity Entity;
            public readonly long Referent;
            public readonly InteractionSignal Signal;

            public PendingInteraction(SbEntity entity, long referent, InteractionSignal signal)
            {
                Entity = entity;
                Referent = referent;
                Signal = signal;
            }
        }

        /// <summary>
        /// True when the loaded library negotiated the containment transfer feature for this session (version 3, ABI 4.3).
        /// False for an older library, which this binding does not accept: the declaration of regions then stays out of the
        /// core and no avatar is ever interacting.
        /// </summary>
        public bool ContainmentTransferAvailable => _containmentFeatureAvailable;

        /// <summary>The number of authored regions declared as transfer leaves on this space.</summary>
        public int ContainmentRegionCount => _containmentRegions.Count;

        // Negotiated from ConfigureNativeSpace. There is no callback to register: the contained state is the flag of the
        // proposals the driver reads.
        protected virtual void NegotiateContainmentFeature()
        {
            _containmentFeatureAvailable = ProbeContainmentFeature(out string error);
            if (!_containmentFeatureAvailable)
            {
                SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.State,
                    "SpatialBlendManager::NegotiateContainmentFeature - containment transfer is unavailable (" + error +
                    "). Regions are not declared as transfer leaves and no avatar is interacting.");
            }
        }

        protected virtual bool ProbeContainmentFeature(out string error) =>
            SpatialBlendAPI.TryValidateContainmentAbi(out error);

        // Clears engagement before the session destroy that follows UnregisterNativeSpace.
        protected virtual void RetireContainmentState()
        {
            lock (_entitiesLock)
            {
                _containmentInteractions.Clear();
                _transferInteracting.Clear();
                _pendingInteractions.Clear();
            }
            _containmentFeatureAvailable = false;
        }

        private void ClearContainmentState()
        {
            lock (_entitiesLock)
            {
                _containmentInteractions.Clear();
                _transferInteracting.Clear();
                _pendingInteractions.Clear();
            }
            _containmentRegions.Clear();
            _containmentFeatureAvailable = false;
        }

        // Callers hold _entitiesLock. Folds the actor's current root proposal into its interaction value (TRANSFER and the
        // root's ARRIVED) and queues the Start or Terminate its change causes. Start and Terminate are the
        // frame-to-frame change of that value and nothing else: a hop between two leaves keeps the value, and a root that
        // is mapped but still walking has not changed it.
        private void ObserveTransferState(SbEntity entity, long actor, ProposalSet proposals)
        {
            bool interacting = proposals.HasRoot && proposals.Root.TransferArrived;
            if (interacting) _transferInteracting.Add(actor);
            else _transferInteracting.Remove(actor);
            _containmentTransitions.Clear();
            _containmentInteractions.Observe(actor, interacting ? proposals.Root.ReferentSharedId : 0L, interacting,
                _containmentTransitions);
            foreach (ContainmentInteractionTransition transition in _containmentTransitions)
            {
                _pendingInteractions.Add(new PendingInteraction(entity, transition.Scope, transition.Signal));
            }
        }

        protected virtual bool TryReadSourceTransfer(long actor, out long leafSharedId) =>
            SpatialBlendAPI.TryGetSourceTransfer(_spaceID, actor, out leafSharedId);

        // Callers hold _entitiesLock. The local avatar's interaction value is the source-side transfer fact: the core's
        // answer for its latest update, read every frame and never stored beyond the edge detector. A library that
        // cannot answer leaves the value as it was.
        private void ObserveSourceTransfer(SbEntity entity, long actor)
        {
            if (!_containmentFeatureAvailable || !TryReadSourceTransfer(actor, out long leaf)) return;
            bool inside = leaf != 0;
            if (inside) _transferInteracting.Add(actor);
            else _transferInteracting.Remove(actor);
            _containmentTransitions.Clear();
            _containmentInteractions.Observe(actor, leaf, inside, _containmentTransitions);
            foreach (ContainmentInteractionTransition transition in _containmentTransitions)
            {
                _pendingInteractions.Add(new PendingInteraction(entity, transition.Scope, transition.Signal));
            }
        }

        // Callers hold _entitiesLock. The interactions decided since the last drain.
        private List<PendingInteraction> DrainPendingInteractionsLocked()
        {
            if (_pendingInteractions.Count == 0) return null;
            var drained = new List<PendingInteraction>(_pendingInteractions);
            _pendingInteractions.Clear();
            return drained;
        }

        // Runs outside _entitiesLock because an interaction runs app code.
        private static void RaiseInteractions(List<PendingInteraction> interactions)
        {
            if (interactions == null) return;
            foreach (PendingInteraction interaction in interactions)
            {
                if (interaction.Entity == null || !interaction.Entity.IsAttached()) continue;
                RaiseInteraction(interaction.Entity, interaction.Referent, interaction.Signal);
            }
        }

        // The avatar handler keys on Euclidean2D, the v1 proximity type that drove the display
        // switch. The type is a label here: the source is the interaction value.
        private static void RaiseInteraction(SbEntity entity, long referent, InteractionSignal signal)
        {
            if (signal == InteractionSignal.None) return;
            entity.OnInteraction(
                signal == InteractionSignal.Start ? InteractionEventType.Start : InteractionEventType.Terminate,
                referent, ProximityType.Euclidean2D, 0);
        }

        /// <summary>
        /// The interaction value of an avatar. Received: its root proposal reads TRANSFER (the source root is in a transfer
        /// leaf and this space has a congruent counterpart) AND ARRIVED (its realised body satisfies the mapping), as of the
        /// proposal set read this frame. Local: the core's source-side transfer fact for its latest update. ZoneBased, the
        /// representation switch, the local animation enable and the state-filter bypass read it. False for an avatar with
        /// no driver and before the first read: the core says nothing there.
        /// </summary>
        public bool IsTransferInteracting(SbEntity entity)
        {
            if (entity == null || _spaceID <= 0) return false;
            lock (_entitiesLock)
            {
                return _v2ActorByEntity.TryGetValue(entity.ID, out long actor) && _transferInteracting.Contains(actor);
            }
        }

        // Called once an authored region has its transform: the region is written element-local, so the polygon follows
        // the element, and declared a transfer leaf on this side. The declaration must be made on both sides, so the
        // receiver does the same for the shared region. Since ABI 3.1 sb_set_element_polygon reads the polygon
        // element-local too (for every role but NAVIGABLE), so the fallback polygon below is the same local one.
        // Returns false only when the core REFUSED the polygon (a region that sticks out of its parent,
        // ContainmentEscapesParent): the core then keeps the element's previous geometry, none for a new element, so the
        // caller must not keep the region. Without the feature, or with the transfer declaration refused, the region
        // still has its polygon and is not a transfer leaf.
        private bool WriteAuthoredRegion(long nativeSpace, long element, Vector2[] polygon)
        {
            if (!_containmentFeatureAvailable)
            {
                return SetSpatialHierarchyBoundary(nativeSpace, element, polygon);
            }
            if (!SetSpatialHierarchyLocalBoundary(nativeSpace, element, polygon))
            {
                // A library without the local entry point, or a refusal: the fallback reports which.
                return SetSpatialHierarchyBoundary(nativeSpace, element, polygon);
            }
            if (!SetSpatialHierarchyContainmentTransfer(nativeSpace, element, true))
            {
                SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Geometry,
                    $"Spatial authoring: the core refused to declare region {element} a transfer leaf.");
                return true;
            }
            _containmentRegions.Add(element);
            return true;
        }

        // The core's message for the last scene change it refused on this space, for the error that names the object.
        protected virtual string SpatialHierarchySceneError(long nativeSpace) =>
            SpatialBlendAPI.TryGetSceneError(nativeSpace, out string error) ? error : null;

        protected virtual bool SetSpatialHierarchyLocalBoundary(long nativeSpace, long element, Vector2[] polygon) =>
            SpatialBlendAPI.TrySetElementLocalPolygon(nativeSpace, element, SbGeometryRole.Containment, polygon);

        protected virtual bool SetSpatialHierarchyContainmentTransfer(long nativeSpace, long element, bool enabled) =>
            SpatialBlendAPI.TrySetElementContainmentTransfer(nativeSpace, element, enabled);
    }
}
