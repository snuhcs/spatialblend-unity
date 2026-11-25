// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>How an anchor of this room stands in the shared hierarchy.</summary>
    public enum AnchorShareState
    {
        /// <summary>A local obstacle of this room only: no shared id, no relation.</summary>
        None,
        /// <summary>The user chose to share it.</summary>
        Shared,
        /// <summary>The other user shared one of the same label and this room's was linked to it (<see cref="TryMatchLocalAnchor"/>); the user did nothing.</summary>
        Linked,
    }

    /// <summary>
    /// Room objects of the Freeform app: only what a user explicitly
    /// shares exists for SpatialBlend. The scanned anchors of the room stay local obstacles; the user picks which ones to send
    /// (<see cref="TryShareAnchor"/>), and on the receiving side the app decides, in <see cref="ResolveAnchorShare"/>, between linking to
    /// the room's own anchor of the same label and adding the shared one as a box to install. The app owns both decisions; the framework only
    /// executes the LinkToExisting / AddAsNew the app answers with. Seats are anchors like the rest, with a measured plane and a front.
    /// </summary>
    public partial class FreeformCollaborationApp
    {
        private sealed class AnchorShareEntry
        {
            public SceneAnchorRecord Record;
            public string Key;
            public AnchorShareState State;
            /// <summary>
            /// The receiver on the other side dropped its counterpart of this shared anchor (core ABI 4.9); it stays shared here. One flag:
            /// exact for the two-user app (with more peers it would mean "some peer").
            /// </summary>
            public bool PeerUnlinked;
            /// <summary>The element id of a Linked anchor, remembered while its entity is attached: the link can still be dropped by id after MRUK destroyed the anchor.</summary>
            public long ElementId;
        }

        private readonly Dictionary<GameObject, AnchorShareEntry> _anchorShares = new Dictionary<GameObject, AnchorShareEntry>();
        private readonly Dictionary<GameObject, SharedRoomObjectBox> _boxes = new Dictionary<GameObject, SharedRoomObjectBox>();
        private readonly Dictionary<GameObject, string> _sessionKeys = new Dictionary<GameObject, string>();
        // Keys this user unshared: an echo of one that reaches this user afterwards is not adopted again (it would re-share the anchor without consent).
        private readonly HashSet<string> _unsharedKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Raised when a box the other user shared has been placed here (the box is then this room's counterpart).</summary>
        public event Action<SharedRoomObjectBox> BoxPlaced;

        /// <summary>The structure of a room is not shared furniture: nothing here is offered to share, and a receiver never links to it.</summary>
        private static readonly HashSet<string> StructuralLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "FLOOR", "CEILING", "WALL_FACE", "INVISIBLE_WALL_FACE", "DOOR_FRAME", "WINDOW_FRAME", "GLOBAL_MESH",
        };

        /// <summary>A measured, non-structural anchor: the room objects the user can share.</summary>
        public static bool IsShareable(SceneAnchorRecord record) =>
            record != null && record.Anchor != null && !StructuralLabels.Contains(record.Label ?? string.Empty) &&
            record.LocalSize.x > 0f && record.LocalSize.y > 0f && record.LocalSize.z > 0f;

        /// <summary>The scanned anchors of this room the user can share, in a stable order (label, then size, then id).</summary>
        public List<SceneAnchorRecord> ShareableAnchors()
        {
            ScopeSeatsToRoom(true);   // the confirmed room's anchors, with this user's shares scoped to that room
            var list = new List<SceneAnchorRecord>();
            ISceneAnchorCatalog catalog = _sceneAnchorCatalog ?? _mrukSceneAnchors;
            IReadOnlyList<SceneAnchorRecord> anchors = catalog.Anchors;
            for (int i = 0; i < anchors.Count; ++i)
            {
                if (IsShareable(anchors[i]) && !_boxes.ContainsKey(anchors[i].Anchor)) list.Add(anchors[i]);
            }
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Label, b.Label);
                if (c != 0) return c;
                c = b.LocalSize.sqrMagnitude.CompareTo(a.LocalSize.sqrMagnitude);
                return c != 0 ? c : string.CompareOrdinal(ShareKeyOf(a), ShareKeyOf(b));
            });
            return list;
        }

        /// <summary>
        /// The key a shared anchor of this room carries: the platform's own anchor id (unique across rooms and devices), or a
        /// session-unique one for an anchor that has none (a prefab room in the Editor). A key names one anchor of one room.
        /// </summary>
        /// <summary>A test seam: the session key of an anchor with no platform id (default: a new GUID). The room matrix makes it deterministic.</summary>
        internal Func<SceneAnchorRecord, string> SessionKeyProvider { get; set; }

        public string ShareKeyOf(SceneAnchorRecord record)
        {
            if (record == null) return string.Empty;
            // A platform id a received box already carries here would put one shared id on two elements (peer B would share its TABLE under
            // the key of A's TABLE box): the anchor takes a session key instead.
            if (!string.IsNullOrEmpty(record.AnchorId) && SharedAnchorName.IsValidKey(record.AnchorId) && !IsKeyOfABox(record.AnchorId))
                return record.AnchorId;
            if (record.Anchor == null) return string.Empty;
            if (!_sessionKeys.TryGetValue(record.Anchor, out string key))
            {
                key = SessionKeyProvider != null ? SessionKeyProvider(record) : Guid.NewGuid().ToString("N");
                _sessionKeys[record.Anchor] = key;
            }
            return key;
        }

        /// <summary>True when the other room unlinked its counterpart of this anchor: it is still shared, but nothing there holds it.</summary>
        public bool PeerUnlinkedOf(SceneAnchorRecord record) =>
            record != null && record.Anchor != null && _anchorShares.TryGetValue(record.Anchor, out AnchorShareEntry entry) && entry.PeerUnlinked;

        public AnchorShareState ShareStateOf(SceneAnchorRecord record) =>
            record != null && record.Anchor != null && _anchorShares.TryGetValue(record.Anchor, out AnchorShareEntry entry)
                ? entry.State : AnchorShareState.None;

        /// <summary>
        /// The user shares one anchor of this room: a seat (a COUCH with a measured plane) through <see cref="TryRegisterSeat"/>, any other
        /// through <see cref="TryRegisterSharedAnchor"/>, both ending in the app's existing publish path. Idempotent.
        /// </summary>
        public bool TryShareAnchor(SceneAnchorRecord record)
        {
            if (IsDesktopObserver) return false;
            if (!IsShareable(record)) return false;
            if (_anchorShares.ContainsKey(record.Anchor)) return true;
            // The other user's object of this label already stands here as a box (this room had none when it arrived): it is this room's
            // counterpart, and sharing a second one of the label would leave both. The box goes first.
            if (TryGetBoxOfLabel(record.Label, out SharedRoomObjectBox box))
            {
                Debug.LogWarning($"[RoomShare] '{record.Label}' is not shared: '{box.name}' already stands here for the other user's {record.Label}.");
                return false;
            }
            _unsharedKeys.Remove(ShareKeyOf(record));
            return RegisterAnchorLocal(record, ShareKeyOf(record), true);
        }

        /// <summary>The user stops sharing an anchor (or drops a link): the element is unshared and the anchor is a local obstacle again.</summary>
        public bool TryUnshareAnchor(SceneAnchorRecord record)
        {
            if (record == null || record.Anchor == null || !_anchorShares.TryGetValue(record.Anchor, out AnchorShareEntry entry)) return false;
            if (entry.State == AnchorShareState.Shared) _unsharedKeys.Add(entry.Key);
            ReleaseLocalAnchor(record.Anchor, entry);
            return true;
        }

        private void ReleaseLocalAnchor(GameObject anchor, AnchorShareEntry entry, bool linkAlreadyRemoved = false)
        {
            // A linked anchor is a counterpart of the sharer's object: drop that link on both sides first (core ABI 4.9), so the
            // sharer's relations no longer resolve onto it and the sharer is told. The element stays and becomes a local obstacle.
            if (!linkAlreadyRemoved) UnlinkIfLinked(anchor, entry);
            _anchorShares.Remove(anchor);
            _seatLinks.RemoveLocal(anchor);
            var released = anchor != null ? anchor.GetComponent<SbMRUKAnchorEntity>() : null;
            if (released != null && released.IsSeat) _seatLinks.RemovePair(SeatIdentity.SharedId(released.SeatKey));
            var stale = new List<string>();
            foreach (KeyValuePair<string, GameObject> pending in _pendingSeatShares)
            {
                if (pending.Key == entry.Key) stale.Add(pending.Key);
            }
            foreach (string key in stale)
            {
                if (_spaceManager != null) _spaceManager.DropDeferredShare(_pendingSeatShares[key]);
                _pendingSeatShares.Remove(key);
            }
            var entity = anchor != null ? anchor.GetComponent<SbMRUKAnchorEntity>() : null;
            if (entity != null) entity.Unregister();
        }

        // Drops the link of a Linked anchor on both sides: by its GameObject while it is there, else by the element id remembered while it was attached.
        private void UnlinkIfLinked(GameObject anchor, AnchorShareEntry entry)
        {
            if (entry.State != AnchorShareState.Linked || _spaceManager == null) return;
            if (anchor != null)
            {
                var entity = anchor.GetComponent<SbMRUKAnchorEntity>();
                if (entity != null && entity.IsAttached()) entry.ElementId = entity.ID;
                if (_spaceManager.TryUnlink(anchor) != SbUnlinkResult.NotLinked || entry.ElementId <= 0) return;
            }
            if (entry.ElementId > 0) _spaceManager.TryUnlinkElement(entry.ElementId);
        }

        /// <summary>Remembers the element ids of the Linked anchors while their entities are attached (called every frame).</summary>
        internal void RememberLinkedElementIds()
        {
            foreach (KeyValuePair<GameObject, AnchorShareEntry> pair in _anchorShares)
            {
                if (pair.Value.State != AnchorShareState.Linked || pair.Key == null) continue;
                var entity = pair.Key.GetComponent<SbMRUKAnchorEntity>();
                if (entity != null && entity.IsAttached()) pair.Value.ElementId = entity.ID;
            }
        }

        private bool RegisterAnchorLocal(SceneAnchorRecord record, string key, bool shareWithPeers) =>
            IsSeatRecord(record) ? TryRegisterSeat(record, key, shareWithPeers) : TryRegisterSharedAnchor(record, key, shareWithPeers);

        /// <summary>Registers a plain anchor (not a seat) under the sharer's key and publishes it through the app's one publish path.</summary>
        public bool TryRegisterSharedAnchor(SceneAnchorRecord record, string key, bool shareWithPeers)
        {
            if (!IsShareable(record) || !SharedAnchorName.IsValidKey(key) || RefusesKeyOfABox(key)) return false;
            var entity = record.Anchor.GetComponent<SbMRUKAnchorEntity>();
            if (entity != null && entity.IsAttached()) return entity.ShareKey == key;
            if (entity == null) entity = record.Anchor.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(record.Label);
            entity.SetSceneGeometryKey(record.SourceKey);
            if (!entity.ApplySharedSupport(record.LocalCenter, record.LocalSize, key)) return false;
            if (!PublishAnchor(record.Anchor, shareWithPeers))
            {
                entity.Unregister();   // a registration that did not go out leaves nothing behind
                return false;
            }
            NoteLocalAnchor(record, key);
            return true;
        }

        // An anchor of this room that is registered under a key is in the shared set. Registered by the user it is Shared; the receiver
        // that linked it to the sharer's changes that to Linked afterwards.
        private void NoteLocalAnchor(SceneAnchorRecord record, string key)
        {
            if (!_anchorShares.ContainsKey(record.Anchor))
            {
                _anchorShares[record.Anchor] = new AnchorShareEntry { Record = record, Key = key, State = AnchorShareState.Shared };
            }
        }

        // The one publish path of a room object: the contract tests' publisher when one is injected, else RegisterGameObject.
        private bool PublishAnchor(GameObject anchor, bool shareWithPeers)
        {
            if (_sceneAnchorPublisher != null)
            {
                _sceneAnchorPublisher.Publish(anchor, shareWithPeers);
                return true;
            }
            if (_spaceManager == null || !_spaceManager.Initialized) return false;
            RegisterGameObject(anchor, shareWithPeers);
            return true;
        }

        private bool TryGetLocalAnchorByKey(string key, out GameObject anchor)
        {
            foreach (KeyValuePair<GameObject, AnchorShareEntry> pair in _anchorShares)
            {
                if (pair.Key != null && pair.Value.Key == key)
                {
                    anchor = pair.Key;
                    return true;
                }
            }
            anchor = null;
            return false;
        }

        // One shared id names one element in this space. A received box holds its sharer's key: no anchor of this
        // room registers under it. (The other side's echo of a key this room registered is linked to it instead, never boxed.)
        private bool IsKeyOfABox(string key)
        {
            foreach (KeyValuePair<GameObject, SharedRoomObjectBox> pair in _boxes)
            {
                var entity = pair.Key != null ? pair.Key.GetComponent<SbMRUKAnchorEntity>() : null;
                if (entity != null && entity.ShareKey == key) return true;
            }
            return false;
        }

        private bool RefusesKeyOfABox(string key)
        {
            if (!IsKeyOfABox(key)) return false;
            Debug.LogWarning($"[RoomShare] key '{key}' is refused: a box the other user shared already holds it here.");
            return true;
        }

        private bool TryGetBoxOfLabel(string label, out SharedRoomObjectBox box)
        {
            foreach (KeyValuePair<GameObject, SharedRoomObjectBox> pair in _boxes)
            {
                var entity = pair.Key != null ? pair.Key.GetComponent<SbMRUKAnchorEntity>() : null;
                if (pair.Value != null && entity != null && string.Equals(entity.SceneLabel, label, StringComparison.Ordinal))
                {
                    box = pair.Value;
                    return true;
                }
            }
            box = null;
            return false;
        }

        private static bool IsAttachedAnchor(GameObject anchor)
        {
            var entity = anchor != null ? anchor.GetComponent<SbMRUKAnchorEntity>() : null;
            return entity != null && entity.IsAttached();
        }

        // ------------------------------------------------------------------ receiver: link to existing, or add as new

        /// <summary>
        /// The receiver's decision for a shared room object (app logic):
        /// 1. this user already has an anchor under that key (the other side's echo of an earlier link): link to it;
        /// 2. this room has a not-yet-used anchor with the same label, whatever its size: the largest free one is registered under the
        ///    sender's key and linked, with no prompt (<see cref="TryMatchLocalAnchor"/>);
        /// 3. otherwise (no anchor of that label, or all are taken): the shared one is added as a box the user installs
        ///    (<see cref="SharedRoomObjectBox"/>). Nothing is refused for being in the way.
        /// A linked anchor is released when the sharer
        /// unshares (<see cref="OnLinkRemoved"/>, core ABI 4.9), and unchecking it drops the link on both sides.
        /// </summary>
        private OnShareResponse ResolveAnchorShare(SbMRUKAnchorEntity incoming)
        {
            // This room's anchors are not registered yet (the share arrived before the app started, or before MRUK loaded):
            // nothing here can be matched, and an AddAsNew box now would stand where this room's own anchor of the same key
            // later is (peer B would make boxes of A's TABLE, COUCH and STORAGE, then share its own under the same ids). It waits.
            if (!RoomAnchorsReady)
            {
                if (!_anchorSharesAwaitingRoom.Contains(incoming.gameObject)) _anchorSharesAwaitingRoom.Add(incoming.gameObject);
                return OnShareResponse.Defer();
            }
            ScopeSeatsToRoom(true);
            GameObject replica = incoming.gameObject;
            string key = incoming.ShareKey;
            // A share with no key or no measured box (an older peer) or of the room's structure cannot be linked and is not a thing to
            // install: it is refused, as an unmatched anchor was before.
            if (_unsharedKeys.Contains(key ?? string.Empty))
            {
                // The other room's echo of an anchor this user unshared: not adopted again.
                return OnShareResponse.Reject();
            }
            if (string.IsNullOrEmpty(key) || !incoming.TryGetMeasuredBox(out _, out _) || StructuralLabels.Contains(incoming.SceneLabel ?? string.Empty))
            {
                Debug.LogWarning($"[RoomShare] OnShare: rejecting '{replica.name}' (label={incoming.SceneLabel}): no key, no measured size, or structure.");
                return OnShareResponse.Reject();
            }

            if (TryGetLocalAnchorByKey(key, out GameObject mine) && mine != replica)
            {
                return IsAttachedAnchor(mine) ? LinkAnchor(incoming, mine) : DeferAnchorShare(incoming);
            }
            if (TryMatchLocalAnchor(incoming, out SceneAnchorRecord match) && AdoptLocalAnchor(match, incoming))
            {
                return IsAttachedAnchor(match.Anchor) ? LinkAnchor(incoming, match.Anchor) : DeferAnchorShare(incoming);
            }
            // No free anchor of that label here (none at all, or all taken): the shared one arrives as a box to install.
            return OnShareResponse.AddAsNew();
        }

        private bool _roomAnchorsReady;
        // This room's anchors can be matched: its MRUK room is registered (TryRegisterCurrentRoomGeometry) or confirmed (the catalog
        // reads it), or a catalog was given.
        private bool RoomAnchorsReady =>
            _roomAnchorsReady || _sceneAnchorCatalog != null || MrukRoomTracker.Current(MRUK.Instance) != null;
        private readonly List<GameObject> _anchorSharesAwaitingRoom = new List<GameObject>();

        /// <summary>
        /// This room's anchors are registered (TryRegisterCurrentRoomGeometry): the shares that waited for them are decided now, in
        /// arrival order, exactly as they would have been on arrival.
        /// </summary>
        private void ResolveAnchorSharesAwaitingRoom()
        {
            _roomAnchorsReady = true;
            if (_spaceManager == null) return;
            var waiting = new List<GameObject>(_anchorSharesAwaitingRoom);
            _anchorSharesAwaitingRoom.Clear();
            foreach (GameObject replica in waiting)
            {
                if (replica == null || !_spaceManager.IsShareDeferred(replica)) continue;
                var incoming = replica.GetComponent<SbMRUKAnchorEntity>();
                if (incoming == null) { _spaceManager.DropDeferredShare(replica); continue; }
                OnShareResponse response = ResolveAnchorShare(incoming);
                switch (response.result)
                {
                    case OnShareResponse.Result.LinkToExisting:
                        // An attached local anchor: linked now; one still registering is completed by PollPendingSeatShares.
                        if (!_spaceManager.CompleteDeferredShare(replica, response.gameObject) && _spaceManager.IsShareDeferred(replica))
                            _pendingSeatShares[incoming.ShareKey] = replica;
                        break;
                    case OnShareResponse.Result.Defer:
                        break;   // DeferAnchorShare recorded it for PollPendingSeatShares
                    case OnShareResponse.Result.AddAsNew:
                        _spaceManager.CompleteDeferredShareAsNew(replica);
                        break;
                    default:
                        _spaceManager.DropDeferredShare(replica);
                        break;
                }
            }
        }

        /// <summary>
        /// The local anchor <paramref name="incoming"/> links to: this room's largest still-free anchor of the same
        /// label (by top area, <see cref="FurnitureSharePolicy.CompareDesks"/>). The host shares its items largest first, so its
        /// largest table links to this room's largest, its second to the second, and so on.
        /// </summary>
        private bool TryMatchLocalAnchor(SbMRUKAnchorEntity incoming, out SceneAnchorRecord match)
        {
            match = null;
            if (!incoming.TryGetMeasuredBox(out _, out _)) return false;
            foreach (SceneAnchorRecord record in ShareableAnchors())
            {
                // Already shared or linked by this user: not a candidate (one-to-one).
                if (_anchorShares.ContainsKey(record.Anchor)) continue;
                if (!string.Equals(record.Label, incoming.SceneLabel, StringComparison.Ordinal)) continue;
                if (incoming.IsSeat && !IsSeatRecord(record)) continue;
                if (match == null || FurnitureSharePolicy.CompareDesks(record, match) < 0) match = record;
            }
            return match != null;
        }

        private bool AdoptLocalAnchor(SceneAnchorRecord record, SbMRUKAnchorEntity incoming)
        {
            string key = incoming.ShareKey;
            bool registered = incoming.IsSeat ? TryRegisterSeat(record, key, true) : TryRegisterSharedAnchor(record, key, true);
            if (!registered) return false;
            _anchorShares[record.Anchor].State = AnchorShareState.Linked;
            return true;
        }

        private OnShareResponse LinkAnchor(SbMRUKAnchorEntity incoming, GameObject local)
        {
            string key = incoming.ShareKey;
            var localEntity = local.GetComponent<SbMRUKAnchorEntity>();
            if (incoming.IsSeat)
            {
                _seatLinks.RecordLink(SeatIdentity.SharedId(key), key, localEntity != null && localEntity.SeatFrontConfirmed, incoming.SeatFrontConfirmed);
                if (_spaceManager != null) _spaceManager.SeatLinks = _seatLinks;
            }
            if (_pendingSeatShares.TryGetValue(key, out GameObject stale))
            {
                _pendingSeatShares.Remove(key);
                if (stale != incoming.gameObject && _spaceManager != null) _spaceManager.DropDeferredShare(stale);
            }
            return OnShareResponse.LinkToExisting(local);
        }

        // The local anchor of that key is still registering (sharing is symmetric: it reaches the other side and is attached when the
        // manager has handled it). The share waits for it and PollPendingSeatShares completes it.
        private OnShareResponse DeferAnchorShare(SbMRUKAnchorEntity incoming)
        {
            string key = incoming.ShareKey;
            if (_pendingSeatShares.TryGetValue(key, out GameObject older) && older != incoming.gameObject && _spaceManager != null)
            {
                _spaceManager.DropDeferredShare(older);
            }
            _pendingSeatShares[key] = incoming.gameObject;
            return OnShareResponse.Defer();
        }

        // ------------------------------------------------------------------ receiver: the box to install

        /// <summary>Where the user is looking from (tests inject one; the headset's centre eye, else the main camera).</summary>
        internal Func<(Vector3 eye, Vector3 forward)?> UserViewProvider { get; set; }
        /// <summary>The floor of the room the box stands on (tests inject one; the MRUK floor anchor's height, else 0).</summary>
        internal Func<float> FloorHeightProvider { get; set; }

        private (Vector3 eye, Vector3 forward)? CurrentUserView()
        {
            if (UserViewProvider != null) return UserViewProvider();
            var rig = FindFirstObjectByType<OVRCameraRig>();
            Transform eye = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
            return eye != null ? (eye.position, eye.forward) : ((Vector3, Vector3)?)null;
        }

        private float CurrentFloorHeight()
        {
            if (FloorHeightProvider != null) return FloorHeightProvider();
            MRUKRoom room = CurrentRoom();
            return room != null && room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
        }

        /// <summary>The shared anchor had nothing to link to: it becomes a box in front of the user, to move and place.</summary>
        private void CreateSharedBox(SbMRUKAnchorEntity entity)
        {
            if (entity == null || _boxes.ContainsKey(entity.gameObject)) return;
            if (!entity.TryGetMeasuredBox(out Vector3 center, out Vector3 size)) return;
            var view = CurrentUserView();
            Vector3 eye = view.HasValue ? view.Value.eye : Vector3.zero;
            Vector3 forward = view.HasValue ? view.Value.forward : Vector3.forward;
            Pose pose = SharedRoomObjectBox.ArrivalPose(eye, forward, CurrentFloorHeight(), center, size);
            SharedRoomObjectBox box = SharedRoomObjectBox.Create(entity, pose);
            if (box == null) return;
            // A received copy is placed in this room like any virtual object: pulled from in front of the user to the nearest
            // valid spot before its pose reaches the core (inside the room, clear of the furniture and the other regions).
            if (!RoomFit.PullInPlace(entity.gameObject, PlacementRoom, _spaceManager, PlacementOthers(),
                    SpatialBlendManager.RegionFitMarginMeters, out _))
                Debug.LogWarning($"[RoomShare] The box for '{entity.SceneLabel}' has no valid spot in this room; it stays in front of the user.");
            // Only an unmatched remote room anchor takes this path. Keep its
            // referent, colliders, placement, and correspondence live while
            // suppressing the receiver-only wire and pending-outline rendering.
            entity.HideReceiverBoxPresentation();
            box.Placed += OnBoxPlaced;
            box.Destroyed += OnBoxDestroyed;
            _boxes[entity.gameObject] = box;
            EnsureBoxBusyProbe();
        }

        /// <summary>The boxes the other user's shared objects became here (placed or not), in arrival order of the list.</summary>
        public IReadOnlyCollection<SharedRoomObjectBox> SharedBoxes => LiveSharedBoxes();

        private void OnBoxPlaced(SharedRoomObjectBox box)
        {
            SbMRUKAnchorEntity entity = box.Entity;
            if (entity == null) return;
            // The replica is not a source, so its moves do not reach the core by themselves: the placement does.
            if (_spaceManager != null && entity.IsAttached())
            {
                // The box stands where the user put it over the real object: never pulled; its region is fitted around it and
                // the virtual objects step off it.
                _spaceManager.RefitPhysicalAtPose(entity.ID, entity.transform.localToWorldMatrix);
            }
            RecordBoxPair(entity);
            BoxPlaced?.Invoke(box);
        }

        // The placed box is this room's seat for the pair; the sharer's confirmed front travelled with it.
        private void RecordBoxPair(SbMRUKAnchorEntity entity)
        {
            if (entity == null || !entity.IsSeat) return;
            _seatLinks.RecordLink(SeatIdentity.SharedId(entity.SeatKey), entity.SeatKey, entity.SeatFrontConfirmed, entity.SeatFrontConfirmed);
            if (_spaceManager != null) _spaceManager.SeatLinks = _seatLinks;
        }

        // The core removed a shared object that arrived as a box: the box goes with its pair. (A local anchor linked to the sharer's object
        // survives the sharer's unshare in the core; the link-removed signal, OnLinkRemoved, releases it.)
        private void ReleaseSharedAnchor(GameObject gameObject)
        {
            if (_boxes.TryGetValue(gameObject, out SharedRoomObjectBox box))
            {
                _boxes.Remove(gameObject);
                var entity = gameObject.GetComponent<SbMRUKAnchorEntity>();
                if (entity != null && entity.IsSeat) _seatLinks.RemovePair(SeatIdentity.SharedId(entity.SeatKey));
                DestroyObject(gameObject);
                return;
            }
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        /// <summary>
        /// The confirmed room changed (a re-scan, another space): what this user shared from the old room's anchors goes with it, like the
        /// seat links the room scope resets. Boxes the other user's objects became are not the old room's and stay.
        /// </summary>
        private void ReleaseLocalShares()
        {
            foreach (KeyValuePair<GameObject, AnchorShareEntry> pair in new List<KeyValuePair<GameObject, AnchorShareEntry>>(_anchorShares))
            {
                // A linked anchor is unlinked on both sides first (the sharer is told), also when MRUK already destroyed its GameObject.
                UnlinkIfLinked(pair.Key, pair.Value);
                if (pair.Key == null) continue;
                var entity = pair.Key.GetComponent<SbMRUKAnchorEntity>();
                if (entity != null) entity.Unregister();
            }
            _anchorShares.Clear();
            _sessionKeys.Clear();
            _unsharedKeys.Clear();
            if (_spaceManager != null)
            {
                foreach (GameObject replica in new List<GameObject>(_pendingSeatShares.Values)) _spaceManager.DropDeferredShare(replica);
            }
            _pendingSeatShares.Clear();
            // The room scope cleared the seat pairs; a box that is placed is still the counterpart of the sharer's seat.
            foreach (SharedRoomObjectBox box in _boxes.Values) if (box != null && box.IsPlaced) RecordBoxPair(box.Entity);
        }

        /// <summary>
        /// The peer removed a link whose end here is one of this user's anchors (core ABI 4.9, called on the main thread).
        /// PeerUnshared: the sharer unshared the object this room's anchor was linked to: the anchor is released (its share and its
        /// pair go, the element stays a local obstacle). PeerUnlinked: the receiver dropped its counterpart of an anchor this user
        /// shares: it stays shared here, and the list says nothing in the other room holds it.
        /// </summary>
        public override void OnLinkRemoved(GameObject gameObject, LinkRemovedReason reason)
        {
            if (gameObject == null || !_anchorShares.TryGetValue(gameObject, out AnchorShareEntry entry)) return;
            if (reason == LinkRemovedReason.PeerUnshared && entry.State == AnchorShareState.Linked)
            {
                ReleaseLocalAnchor(gameObject, entry, true);
            }
            else if (reason == LinkRemovedReason.PeerUnlinked && entry.State == AnchorShareState.Shared)
            {
                entry.PeerUnlinked = true;
            }
        }

        private void ClearRoomShares()
        {
            foreach (SharedRoomObjectBox box in new List<SharedRoomObjectBox>(_boxes.Values))
            {
                if (box != null) box.Destroyed -= OnBoxDestroyed;   // clearing the room is not a box deleted by the user
                if (box != null) DestroyObject(box.gameObject);
            }
            _boxes.Clear();
            // Leaving takes this user's registrations off the room's anchors too (wires, declaration), so the next session starts clean.
            foreach (KeyValuePair<GameObject, AnchorShareEntry> pair in new List<KeyValuePair<GameObject, AnchorShareEntry>>(_anchorShares))
            {
                if (pair.Key == null) continue;
                var entity = pair.Key.GetComponent<SbMRUKAnchorEntity>();
                if (entity != null) entity.Unregister();
            }
            _anchorShares.Clear();
            _sessionKeys.Clear();
            _unsharedKeys.Clear();
        }
    }
}
