// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Seats of the Freeform app (design: sit transfer, section 2.0 and 3.2). A MRUK COUCH anchor with a measured plane is
    /// registered as a shared <c>Sittable</c> interaction entity when the user shares it (room objects, <c>FreeformCollaborationApp.RoomShare</c>),
    /// is linked on the other side by the app's matching rule or arrives there as a box to install, and carries a confirmed front. A
    /// chair that nobody shared and linked is not a referent of any relation (MA-1), so sitting on it transfers nothing: the app has no
    /// special path for it, and no run-time search for a nearest chair.
    /// </summary>
    public partial class FreeformCollaborationApp
    {
        private readonly SeatLinkBook _seatLinks = new SeatLinkBook();
        private ISeatPreferences _seatPreferences;

        private bool _sourceBodyRegistered;

        /// <summary>Where relations are registered (the app's own RegisterRelation by default; a test observes it).</summary>
        internal System.Action<SpatialRelation> RelationRegistrar { get; set; }
        private bool _sittingRelationRegistered;

        /// <summary>The sitting relation of an avatar: its pelvis bound to AffordancePose, scoped to the Sittable affordance.</summary>
        public static SpatialRelation SittingRelation(GameObject avatar) =>
            new SpatialRelation(avatar, SittingRelationBinding.Anchor, SittingRelationBinding.Kind, SittingRelationBinding.Tag);

        /// <summary>
        /// The user's own avatar has registered its embodiment with the core, so its pelvis exists as an anchor (derived from the Hips
        /// bone and the measured sit-bone point): the sitting relation can be bound now. Binding an anchor that does not exist yet
        /// would be refused by the core and lost. The caller is the source-body publisher (the sender side of the embodiment, which lands with the
        /// seated layer, phase U2): until a body publishes, no pelvis exists and the relation is not registered.
        /// </summary>
        public void NotifySourceBodyRegistered()
        {
            _sourceBodyRegistered = true;
            TryRegisterSittingRelation();
        }

        /// <summary>Binds the user's pelvis to Sittable once both the avatar and its embodiment are registered. Once.</summary>
        public bool TryRegisterSittingRelation()
        {
            if (_sittingRelationRegistered || !_sourceBodyRegistered || !_avatarRegistrationComplete || _userAvatar == null)
            {
                return false;
            }
            (RelationRegistrar ?? RegisterRelation)(SittingRelation(_userAvatar));
            _sittingRelationRegistered = true;
            return true;
        }

        /// <summary>True on the host: the host shares the seats the user links, a peer keeps them local so a share can link to them.</summary>
        public bool IsHostRole => _spaceManager != null && _spaceManager.IsHost();

        /// <summary>The seats this peer registered by link key, and the pairs the core linked.</summary>
        public SeatLinkBook SeatLinks => _seatLinks;

        public ISeatPreferences SeatPreferences => _seatPreferences ?? (_seatPreferences = new PlayerPrefsSeatPreferences());

        /// <summary>Replaces the store of the user's seat links and fronts (tests supply a memory one).</summary>
        public void UseSeatPreferences(ISeatPreferences preferences) => _seatPreferences = preferences;

        /// <summary>A COUCH anchor with a measured plane. A seat is never registered at a guessed height.</summary>
        public static bool IsSeatRecord(SceneAnchorRecord record) =>
            record != null && record.Anchor != null && record.PlaneBoundary != null && record.PlaneBoundary.Length >= 3 &&
            string.Equals(record.Label, SceneAnchorSpatialAuthoring.CouchLabel, System.StringComparison.Ordinal);

        /// <summary>The seats of the current room the user can share or link.</summary>
        public List<SceneAnchorRecord> SeatCandidates()
        {
            ScopeSeatsToRoom(true);
            var seats = new List<SceneAnchorRecord>();
            ISceneAnchorCatalog catalog = _sceneAnchorCatalog ?? _mrukSceneAnchors;
            IReadOnlyList<SceneAnchorRecord> anchors = catalog.Anchors;
            for (int i = 0; i < anchors.Count; ++i)
            {
                if (IsSeatRecord(anchors[i])) seats.Add(anchors[i]);
            }
            return seats;
        }

        /// <summary>The confirmed front of a seat (anchor-local plane angle), or null while the user has not confirmed one.</summary>
        public float? SeatFront(SceneAnchorRecord record)
        {
            var entity = record != null && record.Anchor != null ? record.Anchor.GetComponent<SbMRUKAnchorEntity>() : null;
            if (entity != null && entity.IsSeat && entity.SeatFrontDegrees.HasValue) return entity.SeatFrontDegrees;
            return record != null && SeatPreferences.TryGetFront(record.AnchorId, out float stored) ? stored : (float?)null;
        }

        /// <summary>
        /// Registers a seat under its key and, when <paramref name="shareWithPeers"/>, shares it. The key is the sharer's (the anchor's own
        /// id): two seats with the same key are the pair. A receiver registers the seat it linked to under the sharer's key the same way, so
        /// the sharer's echo finds it.
        /// </summary>
        public bool TryRegisterSeat(SceneAnchorRecord record, string linkKey, bool shareWithPeers)
        {
            ScopeSeatsToRoom(true);
            if (!IsSeatRecord(record) || !SeatIdentity.IsValidKey(linkKey) || RefusesKeyOfABox(linkKey)) return false;
            var entity = record.Anchor.GetComponent<SbMRUKAnchorEntity>();
            if (entity != null && entity.IsAttached())
            {
                // A registered seat keeps its key and its part until it is unshared; re-keying one needs a fresh registration.
                return entity.IsSeat && entity.SeatKey == linkKey;
            }
            if (_seatLinks.TryGetLocal(linkKey, out GameObject holder) && holder != record.Anchor)
            {
                var other = holder.GetComponent<SbMRUKAnchorEntity>();
                if (other != null && other.IsAttached())
                {
                    Debug.LogWarning($"[Seat] link key '{linkKey}' already belongs to a registered seat.");
                    return false;
                }
            }
            if (entity == null) entity = record.Anchor.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(SceneAnchorSpatialAuthoring.CouchLabel);
            entity.SetSceneGeometryKey(record.SourceKey);
            if (!entity.ApplySeatSupport(record.LocalCenter, record.LocalSize, record.PlaneBoundary, linkKey, SeatFront(record)))
            {
                return false;
            }
            _seatLinks.SetLocal(linkKey, record.Anchor);
            if (_spaceManager != null) _spaceManager.SeatLinks = _seatLinks;
            if (!PublishAnchor(record.Anchor, shareWithPeers))
            {
                _seatLinks.RemoveLocal(record.Anchor);
                entity.Unregister();   // a registration that did not go out leaves nothing behind
                return false;
            }
            NoteLocalAnchor(record, linkKey);
            return true;
        }

        /// <summary>
        /// The user confirms a seat's front once, by aiming at the side the seat faces. The front is stored with
        /// the seat. It takes part in the seat's registration, so it must be confirmed before the seat is shared or linked; a
        /// seat registered earlier keeps the part it was registered with until it is shared again. Returns false when the aim is
        /// over the seat's centre or the seat is not one.
        /// </summary>
        public bool TryConfirmSeatFront(SceneAnchorRecord record, Vector3 aimedWorldPoint)
        {
            if (!IsSeatRecord(record) ||
                !SceneAnchorSpatialAuthoring.TryFrontFromWorldPoint(record.Anchor.transform.localToWorldMatrix,
                    record.PlaneBoundary, aimedWorldPoint, out float frontDegrees))
            {
                return false;
            }
            SeatPreferences.SetFront(record.AnchorId, frontDegrees);
            var entity = record.Anchor.GetComponent<SbMRUKAnchorEntity>();
            if (entity != null && entity.IsSeat && !entity.IsAttached())
            {
                entity.ApplySeatSupport(record.LocalCenter, record.LocalSize, record.PlaneBoundary, entity.SeatKey, frontDegrees);
            }
            return true;
        }

        // A shared room object that waits for this room's anchor of its key to attach (the link is made by ResolveAnchorShare when it is
        // there). It is not lost: PollPendingSeatShares completes it the moment the local anchor is attached.
        private readonly Dictionary<string, GameObject> _pendingSeatShares = new Dictionary<string, GameObject>();

        /// <summary>The keys of shared room objects that wait for this room's linked anchor to attach.</summary>
        public IReadOnlyCollection<string> PendingSeatKeys => _pendingSeatShares.Keys;

        /// <summary>
        /// Completes the deferred shares whose key an attached local anchor now carries. Called every frame by the app; a test calls it directly.
        /// Returns how many were linked.
        /// </summary>
        public int PollPendingSeatShares()
        {
            if (_pendingSeatShares.Count == 0 || _spaceManager == null) return 0;
            int linked = 0;
            foreach (string key in new List<string>(_pendingSeatShares.Keys))
            {
                GameObject replica = _pendingSeatShares[key];
                if (replica == null || !_spaceManager.IsShareDeferred(replica))
                {
                    // Gone: the source unshared the seat while it waited, or the session changed. Nothing is waiting any more, and the
                    // local anchor adopted for it has no link to be signalled about: it is released here.
                    _pendingSeatShares.Remove(key);
                    if (TryGetLocalAnchorByKey(key, out GameObject adopted) && _anchorShares.TryGetValue(adopted, out AnchorShareEntry adoptedEntry) &&
                        adoptedEntry.State == AnchorShareState.Linked)
                    {
                        ReleaseLocalAnchor(adopted, adoptedEntry, true);
                    }
                    continue;
                }
                if (!TryGetLocalAnchorByKey(key, out GameObject local)) continue;
                var replicaSeat = replica.GetComponent<SbMRUKAnchorEntity>();
                var localSeat = local.GetComponent<SbMRUKAnchorEntity>();
                if (localSeat == null || !localSeat.IsAttached()) continue;
                bool sourceFront = replicaSeat != null && replicaSeat.SeatFrontConfirmed;
                bool isSeat = replicaSeat != null && replicaSeat.IsSeat;   // read before the completion discards the replica
                if (_spaceManager.CompleteDeferredShare(replica, local))
                {
                    if (isSeat)
                    {
                        _seatLinks.RecordLink(SeatIdentity.SharedId(key), key, localSeat.SeatFrontConfirmed, sourceFront);
                        _spaceManager.SeatLinks = _seatLinks;
                    }
                    _pendingSeatShares.Remove(key);
                    ++linked;
                }
                else if (!_spaceManager.IsShareDeferred(replica))
                {
                    _pendingSeatShares.Remove(key);
                }
            }
            return linked;
        }

        // Frames until the next scope check (frame-driven, not wall-clock: the same in every run).
        private int _roomScopeFramesLeft;
        private const int RoomScopeEveryFrames = 30;
        private bool _inScopeRegistration;

        private void Update()
        {
            ScopeSeatsToRoom();
            RememberLinkedElementIds();
            PollPendingSeatShares();
            NarrateArrivals();
        }

        /// <summary>
        /// Seat links and confirmed fronts belong to the room they were made in. When the confirmed MRUK room changes (a
        /// re-scan, another space) the links are dropped and fronts are read from the new room's scope.
        /// </summary>
        // The scope is brought up to date before any seat is read or registered, never on the 30-frame tick alone: a seat
        // registered in the frame the room is confirmed must land in the new room's book, not be cleared by the next tick.
        private void ScopeSeatsToRoom(bool now = false)
        {
            if (!now && --_roomScopeFramesLeft > 0) return;
            _roomScopeFramesLeft = RoomScopeEveryFrames;
            if (!((_sceneAnchorCatalog ?? _mrukSceneAnchors) is IRoomScopedCatalog scoped)) return;
            string key = scoped.RoomKey ?? string.Empty;
            if (SeatPreferences is PlayerPrefsSeatPreferences prefs) prefs.RoomScope = key;
            if (!_seatLinks.EnterRoom(key)) return;
            ReleaseLocalShares();
            // A room confirmed after connect (the headset was outside its outline, or MRUK was still loading) gets its obstacle
            // geometry registered now: the catalog was empty when it was first tried. What the user shares is chosen again in the list.
            if (key.Length > 0 && !_inScopeRegistration)
            {
                _inScopeRegistration = true;
                try { TryRegisterCurrentRoomGeometry(); }
                finally { _inScopeRegistration = false; }
            }
        }
    }
}
