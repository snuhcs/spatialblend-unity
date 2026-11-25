// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpaceManagerType = SpatialBlend.SpaceManagerType;
using OnShareDelegate = SpatialBlend.Internal.OnShareDelegate;
using OnRemoveDelegate = SpatialBlend.Internal.OnRemoveDelegate;
using OnUpdateStateDelegate = SpatialBlend.Internal.OnUpdateStateDelegate;
using OnUpdateStateBatchDelegate = SpatialBlend.Internal.OnUpdateStateBatchDelegate;
using OnUpdateDataDelegate = SpatialBlend.Internal.OnUpdateDataDelegate;
using OnInteractionDelegate = SpatialBlend.Internal.OnInteractionDelegate;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using SpatialBlend.Internal;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;

using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.AI;
using Unity.AI.Navigation;
using System.Runtime.InteropServices;
using UnityEditor;
using Unity.VisualScripting;
using UnityEngine.UI;
using System.Threading.Tasks;
using System.Threading;
using Fusion;
using AOT;
using System.Text;
using Unity.Mathematics;

namespace SpatialBlend
{
    using SpaceID = System.Int64;
    using EntityID = System.Int64;
    using EventHandle = System.Int64;
    using SessionID = System.Int64;
    using ActorID = System.Int64;
    using ElementID = System.Int64;

    // We need to make sure that SpatialBlendManager is the first thing to be initialized, compared to other components.
    [DefaultExecutionOrder(0)]
    public partial class SpatialBlendManager : MonoBehaviour
    {
        // Space ID is set by the native API
        private SpaceID _spaceID = -1;
        private SessionID _sessionID = -1;
        private ElementID _roomElementID = -1;
        // The room's ring as registered (RegisterRoomRegion), null before.
        private WalkableRegion _registeredWalkable;
        private EventHandle _listenerHandle = -1;
        public SpaceID ID { get { return _spaceID; } }
        public static SpatialBlendSettings CurrentSettings { get; private set; }

        /// <summary>
        /// The head anchor's height above the root: a received avatar's head is 1.58 m, v1's <c>AVATAR_HEIGHT</c>
        /// (<c>SbRetargetTransform</c>, which <c>SetHeadTransform</c> forced), the same value the driver places
        /// a received avatar at (<see cref="Internal.EmbodiedDriverConfig.SpawnHeightMeters"/>). It was an uncited 1.6.
        /// It is the registered offset only: a SOURCE avatar's head anchor is given the tracked head's world pose on every
        /// source update (<see cref="SetNativeSourceHead"/>). The core's person body top (the Facing sightline's end at another
        /// person) still reads this offset.
        /// </summary>
        public const float HeadAnchorHeightMeters = Internal.EmbodiedDriverConfig.SpawnHeightMeters;
        private const long V2RoomSharedId = 1;
        private const long V2NetworkSharedIdBase = 1000000000000L;
        private const long V2LocalSharedIdBase = 2000000000000L;
        // A shared MRUK scene anchor has no per-device identity: the declared label link
        // pairs the biggest anchor of one label on every device, so its shared id is the label's.
        private const long V2SceneAnchorSharedIdBase = 3000000000000L;

        // Space creation completion tracking
        private bool _spaceCreated = false;
        public bool SpaceCreated => _spaceCreated;
        public static event System.Action<SpatialBlendManager> OnSpaceCreated;

        [SerializeField]
        [Tooltip("If true, the space manager will initialize the network connection on Awake. If false, the space manager will wait for explicit Host() or Connect() call.")]
        private bool _connectOnAwake = true;
        public bool ConnectOnAwake => _connectOnAwake;

        public SpaceManagerType _networkRole = SpaceManagerType.Host;

        // Hide these fields from the inspector (managed by SpaceManagerEditor.cs)
        [HideInInspector]
        public int _port = Constants.DEFAULT_PORT;
        [HideInInspector]
        public string _hostIP = Constants.DEFAULT_HOST_IP;

        public string HostIP => _hostIP;
        public int Port => _port;

        private bool _initialized = false;
        public bool Initialized => _initialized;

        private bool _appsStarted = false;

        public bool LOG_SPACE_CONTEXT = false;

        private readonly object _entitiesLock = new object();
        private List<SbEntity> _entities = new List<SbEntity>();
        public IReadOnlyList<SbEntity> Entities => _entities;
        private List<Interaction> _interactions = new List<Interaction>();
        public IReadOnlyList<Interaction> Interactions => _interactions;

        // v2 (SpatialBlend) relations declared by apps via RegisterRelation().
        private readonly List<SpatialRelation> _relations = new List<SpatialRelation>();
        public IReadOnlyList<SpatialRelation> Relations => _relations;

        private HashSet<StateType> _entityStatesToMonitor = new HashSet<StateType>();
        private readonly Dictionary<EntityID, ActorID> _v2ActorByEntity = new Dictionary<EntityID, ActorID>();
        private readonly Dictionary<ActorID, EntityID> _v2EntityByActor = new Dictionary<ActorID, EntityID>();
        // IDs are cleared by SbEntity.Detach before it calls RemoveEntity. Keep
        // the exact attached registration here so a re-entrant replacement cannot
        // retire another entity that happens to use the same native element id.
        private struct EntityRegistration
        {
            public EntityID Id;
            public long Token;
            public long IngressReceipt;
            public bool IsSource;
        }
        private readonly Dictionary<SbEntity, EntityRegistration> _entityRegistrations =
            new Dictionary<SbEntity, EntityRegistration>();
        // Only native GameObjects created by SbNativeEntity.SyncRemote and then
        // accepted through AddAsNew enter this registry. It is deliberately not a
        // general "remote" list: LinkToExisting, authored local objects, and
        // Fusion-owned avatars must never become destruction candidates.
        private struct ReconstructedNativeReplica
        {
            public SbNativeEntity Entity;
            public EntityID Id;
            public long RegistrationToken;
            public long AttachmentVersion;
            public long Generation;
        }
        private readonly Dictionary<SbNativeEntity, ReconstructedNativeReplica>
            _reconstructedNativeReplicas =
                new Dictionary<SbNativeEntity, ReconstructedNativeReplica>();
        private long _nextEntityRegistrationToken;
        // Subject anchors already added per native actor, to keep RegisterAnchor idempotent. Kept for the native actor's lifetime
        // (the space's), not the entity's: a released actor stays in the core with its anchors.
        private readonly HashSet<(ActorID actor, string name)> _v2ActorAnchors = new HashSet<(ActorID, string)>();
        // Source avatar actors whose "head" anchor carries the tracked head's world pose (SetNativeSourceHead), with the pose last
        // written; cleared on release.
        private readonly Dictionary<ActorID, (Vector3 position, float yaw)> _v2TrackedHeads =
            new Dictionary<ActorID, (Vector3 position, float yaw)>();
        // Last authoritative relation-pipeline snapshot for each actor. Absence
        // from the next snapshot is an explicit cancellation.
        private readonly Dictionary<ActorID, HashSet<RealizationPipelineKey>> _v2ActivePipelines =
            new Dictionary<ActorID, HashSet<RealizationPipelineKey>>();
        // Actors whose realization targets arrived with no entity: warned once each, not on every snapshot.
        private readonly HashSet<ActorID> _v2UnmappedTargetActors = new HashSet<ActorID>();
        private readonly HashSet<ActorID> _v2MissingEntityTargetActors = new HashSet<ActorID>();

        // Every native callback and every main-thread work item is tied to the
        // manager session that created it. Native callbacks may race session
        // teardown; a generation makes those stale deliveries harmless.
        private long _lifecycleGeneration = 1;
        private volatile bool _isTearingDown;
        private long _eventReceiptSequence;
        private long _currentTaskIngressReceipt;
        private readonly Dictionary<EntityID, long> _removedElementReceipts =
            new Dictionary<EntityID, long>();
        // Native removal is idempotent per attached registration, not merely per
        // element id: the same Unity object can be reattached with the same id.
        private readonly HashSet<long> _queuedRemovalRegistrationTokens = new HashSet<long>();

        // List of application instances that shares this space
        private List<SbApp> _apps = new List<SbApp>();

        // Add this near the top of the class with other static variables
#if UNITY_EDITOR
        private static int latestEditorPort = Constants.DEFAULT_PORT;
#endif

        internal void RegisterApp(SbApp app)
        {
            if (app == null || _apps.Contains(app)) return;
            _apps.Add(app);
            if (_appsStarted && Initialized)
            {
                StartRegisteredApp(app, ConnectionGeneration);
            }
            else if (_connectOnAwake && Initialized)
            {
                StartApps();
            }
        }

        internal void UnregisterApp(SbApp app)
        {
            if (_apps.Contains(app))
            {
                _apps.Remove(app);
            }
            StopRegisteredApp(app);
        }

        internal void StartApps()
        {
            if (!Initialized || _isTearingDown || _appsStarted)
            {
                return;
            }
            _appsStarted = true;

            long generation = ConnectionGeneration;
            foreach (var app in new List<SbApp>(_apps))
            {
                StartRegisteredApp(app, generation);
            }
        }

        /// <summary>
        /// The seat pairs this peer linked (set by the app): the receiver's driver reads whether a seat's front is confirmed on
        /// both sides from it. Null when the app links no seats.
        /// </summary>
        public SeatLinkBook SeatLinks { get; set; }

        public bool IsHost()
        {
            return _networkRole == SpaceManagerType.Host;
        }

        // Returns the app that is responsible to control the space (i.e., who gets the OnShare() callback)
        // Right now, we just return the first app that registered to this space.
        // We eventually need additional network logic to support multiple app connection to single SpatialBlendManager.
        protected SbApp GetRepresentativeApp()
        {
            if (_apps.Count == 0)
            {
                SpatialBlendDebug.LogWarning(LogCategory.Scene, "SpatialBlendManager::GetRepresentativeApp - No apps registered");
                return null;
            }

            return _apps[0];
        }

        private void OnValidate()
        {
            // This will trigger the property drawer to update
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        internal OnShareDelegate onShare;
        internal OnRemoveDelegate onRemove;
        internal OnUpdateStateDelegate onUpdateState;
        internal OnUpdateStateBatchDelegate onUpdateStateBatch;
        internal OnUpdateDataDelegate onUpdateData;
        internal OnInteractionDelegate onInteraction;


        private void Awake()
        {
            // Make sure UnityMainThreadDispatcher is initialized on the main thread in the earlist entry point of the framework
            UnityMainThreadDispatcher.Initialize();

            // Initialize debug system with SpatialBlendSettings if available
            var spatialBlendSettings = Resources.Load<SpatialBlendSettings>("SpatialBlendSettings");
            if (spatialBlendSettings != null)
            {
                spatialBlendSettings.ApplyToDebugSystem();
                CurrentSettings = spatialBlendSettings;
            }
            else
            {
                Debug.LogWarning("[SpatialBlend] No SpatialBlendSettings asset found in Resources/SpatialBlendSettings.asset, using default (release) library");
                // Fallback initialization if no SpatialBlendSettings asset is found
                SpatialBlendDebug.Initialize();
            }

            bool libraryReady = TryInitializeNativeLibrary();
            if (libraryReady && _connectOnAwake && _networkRole == SpaceManagerType.Host)
            {
                Host(_port);
            }
        }

        public virtual void Start()
        {
            if (_connectOnAwake && _networkRole == SpaceManagerType.Client)
            {
                Connect(_hostIP, _port);
            }
        }

        /* Context initialization methods */
        // TODO(JM): Once Fusion makes connection, call these methods to get sync complete
        // Log: Already done on FusionConnectionManager?
        public virtual void Host(int port)
        {
            EstablishConnection(SpaceManagerType.Host, null, port);
        }

        public virtual void Connect(string hostIP, int port)
        {
            EstablishConnection(SpaceManagerType.Client, hostIP, port);
        }


        protected virtual void ConfigureNativeSpace(long space, long callbackGeneration)
        {
            RegisterTransportCallbacks(space, callbackGeneration);
            NegotiateContainmentFeature();
            // The room exists before the space hosts or joins. A received element hangs under the element with its
            // parent's shared id; with no room yet it would become a root of its own, which the core's containment
            // checks never reach (a parentless element has no parent or siblings to be checked against).
            EnsureSpatialHierarchyRoom(space);
        }

        protected virtual void RegisterTransportCallbacks(long space, long callbackGeneration)
        {
            SpatialBlendAPI.SetUpdateInterval(space, 1.0 / 30.0);
            SpaceID callbackSpace = space;
            SpatialBlendAPI.RegisterOnTargets(space, (receivedSpace, actor, targets) =>
            {
                if (IsCurrentLifecycleCallback(callbackSpace, callbackGeneration, receivedSpace))
                {
                    OnRealizationTargets(receivedSpace, actor, targets, callbackGeneration);
                }
            });
            SpatialBlendAPI.RegisterOnElementAdded(space,
                (receivedSpace, element, sharedId, tag, userData, userDataSize) =>
                {
                    if (IsCurrentLifecycleCallback(callbackSpace, callbackGeneration, receivedSpace))
                    {
                        OnElementAdded(receivedSpace, element, sharedId, tag, userData, userDataSize,
                            callbackGeneration);
                    }
                });
            SpatialBlendAPI.TryRegisterOnElementRemoved(space, (receivedSpace, element) =>
            {
                if (IsCurrentLifecycleCallback(callbackSpace, callbackGeneration, receivedSpace))
                {
                    QueueElementRemoval(element, callbackGeneration);
                }
            });
            // ABI 4.9: the peer removed a link whose end here is a surviving element. Optional: an older core has no such callback.
            if (!SpatialBlendAPI.HasLinkRemoval())
            {
                Debug.LogWarning("[SpatialBlend] the loaded core is older than C ABI 4.9: unlinking and the link-removed signal are unavailable, so a " +
                                 "linked room object outlives the other side's unshare and unlink. Update the native library.");
            }
            SpatialBlendAPI.TryRegisterOnLinkRemoved(space, (receivedSpace, element, peerSpace, reason) =>
            {
                if (IsCurrentLifecycleCallback(callbackSpace, callbackGeneration, receivedSpace))
                {
                    long elementId = element;
                    LinkRemovedReason mapped = (LinkRemovedReason)(int)reason;
                    PushMainThreadTask(() => ProcessLinkRemoval(elementId, mapped), callbackGeneration);
                }
            });
        }

        // Main thread: tells the app which of its objects lost a link on the peer's side (the element survives).
        private void ProcessLinkRemoval(EntityID elementId, LinkRemovedReason reason)
        {
            SbEntity entity = null;
            lock (_entitiesLock)
            {
                foreach (SbEntity candidate in _entities)
                {
                    if (candidate != null && candidate.IsAttached() && candidate.ID == elementId)
                    {
                        entity = candidate;
                        break;
                    }
                }
            }
            if (entity == null) return;
            GetRepresentativeApp()?.OnLinkRemoved(entity.gameObject, reason);
        }

        /// <summary>True when the loaded core can remove a link (ABI 4.9, <c>sb_unlink</c>).</summary>
        public bool CanUnlink => SpatialBlendAPI.HasLinkRemoval();

        /// <summary>
        /// The receiver drops the link of its counterpart <paramref name="counterpart"/> (the local object a share was linked to, or the
        /// replica kept for it) on both sides (core ABI 4.9). The element stays, as a plain local one; the sharer is told
        /// (<see cref="SbApp.OnLinkRemoved"/>, <see cref="LinkRemovedReason.PeerUnlinked"/>). The sharer's own source is not
        /// unlinked by this: unshare it instead.
        /// </summary>
        public SbUnlinkResult TryUnlink(GameObject counterpart)
        {
            if (!SpatialBlendAPI.HasLinkRemoval()) return SbUnlinkResult.Unavailable;
            var entity = counterpart != null ? counterpart.GetComponent<SbEntity>() : null;
            if (entity == null || !entity.IsAttached() || entity.spaceManager != this) return SbUnlinkResult.NotLinked;
            return TryUnlinkElement(entity.ID);
        }

        /// <summary>
        /// <see cref="TryUnlink"/> by element id, for a counterpart whose GameObject is already gone (the element survives its entity:
        /// unsharing never deletes an adapter-owned or linked element). The id is only meaningful in the current session.
        /// </summary>
        public SbUnlinkResult TryUnlinkElement(long element)
        {
            if (!SpatialBlendAPI.HasLinkRemoval()) return SbUnlinkResult.Unavailable;
            if (element <= 0 || _spaceID <= 0 || _isTearingDown) return SbUnlinkResult.NotLinked;
            return SpatialBlendAPI.TryUnlink(_spaceID, element) > 0 ? SbUnlinkResult.Removed : SbUnlinkResult.NotLinked;
        }

        public virtual void OnDestroy()
        {
            _destroyed = true;
            UnhookLocalFrameEvents();
            Disconnect();
        }

        public SbEntity GetEntity(EntityID id)
        {
            return _entities.Find(e => e.ID == id);
        }

        public void AddEntity(SbEntity entity)
        {
            ValidateCanAttach();
            lock (_entitiesLock)
            {
                ValidateCanAttach();
                if (!_entities.Contains(entity))
                {
                    _entities.Add(entity);
                    _entityRegistrations[entity] = new EntityRegistration
                    {
                        Id = entity.ID,
                        Token = ++_nextEntityRegistrationToken,
                        IngressReceipt = _currentTaskIngressReceipt != 0
                            ? _currentTaskIngressReceipt
                            : Interlocked.Increment(ref _eventReceiptSequence),
                        IsSource = entity.IsSource
                    };
                    // An authored entity registers through its SbSpatialAuthoring declaration.
                    // Anything else keeps the per-surface SbAffordance path.
                    if (entity.IsSource && entity.GetComponent<SbSpatialAuthoring>() == null)
                    {
                        RegisterAffordances(entity);
                    }
                }
            }
        }

        public void RemoveEntity(SbEntity entity)
        {
            RemoveEntity(entity, entity != null ? entity.ID : -1, 0);
        }

        // Entity.Attach calls this before changing identity, so a teardown owner
        // cannot leave a half-attached entity when registration is rejected.
        internal void ValidateCanAttach()
        {
            if (_isTearingDown)
            {
                throw new InvalidOperationException(
                    "Cannot attach an SbEntity to a manager that is tearing down.");
            }
        }

        // SbEntity.Detach clears its public identity before releasing
        // controllers, so it supplies the captured id/token. The token prevents
        // a re-entrant reattach of the same Unity object from retiring its new
        // registration, even if the native id is reused.
        internal void RemoveEntity(SbEntity entity, long detachedEntityId,
                                   long registrationToken)
        {
            bool shouldUnshare = false;
            SpaceID unshareSpace = -1;
            EntityID unshareElement = -1;
            bool retireScene = false;
            int retireKey = 0;
            long retireRegion = 0;
            long[] retireAffordances = null;
            long removedRegion = 0;
            long[] removedAffordances = null;
            ActorID releasedHead = 0;
            lock (_entitiesLock)
            {
                if (entity == null ||
                    !_entityRegistrations.TryGetValue(entity, out EntityRegistration registration) ||
                    registration.Id != detachedEntityId ||
                    (registrationToken != 0 && registration.Token != registrationToken))
                {
                    return;
                }
                _entities.Remove(entity);
                _entityRegistrations.Remove(entity);
                EntityID registrationId = registration.Id;
                shouldUnshare = registration.IsSource && !_isTearingDown &&
                    _spaceID > 0 && _sessionID > 0;
                unshareSpace = _spaceID;
                unshareElement = registrationId;
                if (_v2ActorByEntity.TryGetValue(registrationId, out ActorID actorId) &&
                    _v2EntityByActor.TryGetValue(actorId, out EntityID mappedId) &&
                    mappedId == registrationId)
                {
                    _v2ActorByEntity.Remove(registrationId);
                    _v2EntityByActor.Remove(actorId);
                    // The native actor and its anchors stay in the core (nothing destroys an actor before the space goes), so
                    // _v2ActorAnchors keeps them: a re-share of this avatar finds the same actor (FindActorBySharedId) and
                    // must know its root and head again, and RegisterAnchor must not add an anchor the core already has.
                    if (_v2TrackedHeads.Remove(actorId)) releasedHead = actorId;
                    _v2ActivePipelines.Remove(actorId);
                    _v2UnmappedTargetActors.Remove(actorId);
                    _v2MissingEntityTargetActors.Remove(actorId);
                    ForgetEmbodiedDriverLocked(actorId);
                }
                // An adapter-owned scene anchor this app registered and now takes back: what registration wrote onto the adapter's
                // elements (shared id, tags, supports, the seat part) and the region it made are undone below.
                if (_sceneOwnedEntityElements.Remove(registrationId) && registration.IsSource &&
                    entity is SbMRUKAnchorEntity sceneAnchor)
                {
                    retireScene = true;
                    retireKey = sceneAnchor.SceneGeometryKey;
                    _authoredRegionByObject.TryGetValue(registrationId, out retireRegion);
                    if (_authoredStateByObject.TryGetValue(registrationId, out var authoredState)) retireAffordances = authoredState.AffordanceElements;
                }
                // An authored object this space shared (not an adapter's anchor): its region and its affordances leave the core with
                // it (otherwise the region element would stay behind for good).
                else if (registration.IsSource && _authoredStateByObject.TryGetValue(registrationId, out var removedState) &&
                         !removedState.Owned && removedState.RegionElement > 0)
                {
                    removedRegion = removedState.RegionElement;
                    removedAffordances = removedState.AffordanceElements;
                }
                ForgetAuthoredRegion(registrationId);
            }
            if (shouldUnshare && !_isTearingDown && _spaceID == unshareSpace &&
                _sessionID > 0)
            {
                SpatialBlendAPI.UnshareElement(unshareSpace, unshareElement);
            }
            // The released actor stays in the core: its head anchor gives up the tracked pose it was last given (the
            // registered offset again), so nothing reads a head the app no longer tracks.
            if (releasedHead > 0 && !_isTearingDown && _spaceID == unshareSpace && unshareSpace > 0)
                ClearNativeSourceHead(unshareSpace, releasedHead);
            // The region goes from this space whether or not a session shares it (offline it was never shared).
            if (removedRegion > 0 && !_isTearingDown && _spaceID == unshareSpace && unshareSpace > 0)
                RemoveAuthoredElements(unshareSpace, unshareElement, removedRegion, removedAffordances,
                    shared: shouldUnshare && _sessionID > 0);
            if (_wallsDirty && !_isTearingDown) RetrimWalls();
            if (retireScene && !_isTearingDown && _spaceID == unshareSpace) RetireSceneAnchor(unshareSpace, unshareElement, retireKey, retireRegion, retireAffordances);
        }

        // A removed source's authored elements: the region and the affordances are unshared too (the object was), then the region is
        // removed with its subtree (the object under it), and the object on its own when it stood outside its region (a withdrawn
        // own scan). A region the core still refuses to remove (a link it keeps) is withdrawn in place, as a retired anchor's is.
        private void RemoveAuthoredElements(SpaceID space, long objectElement, long region, long[] affordances, bool shared)
        {
            if (shared)
            {
                UnshareSpatialHierarchyElement(space, region);
                if (affordances != null)
                    foreach (long affordance in affordances)
                        if (affordance > 0) UnshareSpatialHierarchyElement(space, affordance);
            }
            if (RemoveSpatialHierarchyElement(space, region))
            {
                RemoveSpatialHierarchyElement(space, objectElement);   // already gone with the region, unless it stood outside it
                return;
            }
            SpatialBlendDebug.LogError(LogCategory.Scene,
                $"The region {region} of removed object {objectElement} could not be removed from the core: it stays, withdrawn (no identity, " +
                "no transfer leaf).");
            ++RemovedRegionsStripped;
            SetSpatialHierarchyContainmentTransfer(space, region, false);
            _containmentRegions.Remove(region);
            SetSpatialHierarchySharedId(space, region, 0);
            SetSpatialHierarchyTag(space, region, string.Empty);
        }

        /// <summary>Removed objects' regions the core would not delete and that were stripped in place instead (tests read it).</summary>
        internal int RemovedRegionsStripped { get; private set; }

        /// <summary>Unshares an element. A seam for the tests.</summary>
        protected virtual void UnshareSpatialHierarchyElement(SpaceID space, long element) => SpatialBlendAPI.UnshareElement(space, element);

        // Takes a scene anchor out of the shared hierarchy again, in place: the adapter's elements keep their geometry but lose the shared
        // identity and tags registration gave them (so the anchor is a referent of nothing, an obstacle only, as before it was shared), the
        // region registration made is removed, and the plane gets a fresh definition (no seat part, no supports). The core does not let
        // an element that was linked across spaces be deleted, so the elements themselves stay.
        private void RetireSceneAnchor(SpaceID space, long objectElement, int sourceKey, long region, long[] affordances)
        {
            // The anchor's box hangs under its region (TryApplySpatialAuthoring), and removing the region would take the box
            // with it: the box is parked first (the holding element has no footprint, so nothing is checked against it), and
            // set down under the room again once the region is gone.
            bool underRegion = region > 0 && _anchorRegionByKey.TryGetValue(sourceKey, out AnchorRegionParent anchorRegion) &&
                               anchorRegion.Element == region;
            Matrix4x4 boxWorld = Matrix4x4.identity;
            bool parked = underRegion && objectElement > 0 && TryGetTopLevelSceneGeometryWorld(sourceKey, out boxWorld) &&
                          ParkSceneGeometry(space, objectElement, boxWorld);
            if (underRegion && !parked)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene,
                    $"Retiring scene anchor {objectElement}: its box could not be taken out of its region {region}, so the region stays.");
            }
            else if (region > 0 && RemoveLocalSceneGeometryElement(space, region))
            {
                _anchorRegionByKey.Remove(sourceKey);
                if (parked)
                {
                    long room = EnsureLocalSceneGeometryRoom(space);
                    if (room <= 0 || !ReparentSpatialHierarchyElement(space, objectElement, room, boxWorld))
                    {
                        SpatialBlendDebug.LogWarning(LogCategory.Scene,
                            $"Retiring scene anchor {objectElement}: its box stays parked (the room refused it back).");
                    }
                }
            }
            else if (region > 0)
            {
                // Refused because it was linked across spaces: it stays, as a referent of nothing and no transfer leaf, and
                // keeps holding the box (the box overlaps it, so it cannot be its sibling).
                if (parked)
                {
                    _anchorRegionByKey.TryGetValue(sourceKey, out AnchorRegionParent stays);
                    ReparentSpatialHierarchyElement(space, objectElement, region, stays.WorldInverse * boxWorld);
                }
                SetSpatialHierarchyContainmentTransfer(space, region, false);
                _containmentRegions.Remove(region);
                SetSpatialHierarchySharedId(space, region, 0);
                SetSpatialHierarchyTag(space, region, string.Empty);
            }
            if (objectElement > 0)
            {
                SetSpatialHierarchySharedId(space, objectElement, 0);
                SetSpatialHierarchyTag(space, objectElement, string.Empty);
            }
            if (affordances != null)
            {
                foreach (long affordance in affordances)
                {
                    if (affordance <= 0) continue;
                    SetSpatialHierarchySharedId(space, affordance, 0);
                    SetSpatialHierarchyTag(space, affordance, string.Empty);
                }
            }
            if (sourceKey != 0 && _sceneLayout != null && _sceneLayout.TryGetRequested(sourceKey, out SceneGeometryDescriptor descriptor) &&
                _ownedLocalSceneGeometry.TryGetValue(sourceKey, out OwnedSceneGeometry owned) && owned.Plane > 0 && owned.Space == space &&
                owned.Generation == ConnectionGeneration && descriptor.HasPlane)
            {
                SetLocalSceneGeometryPolygon(space, owned.Plane, (int)SbGeometryRole.Surface, SceneGeometryChart.PlanePolygon(descriptor.PlaneBoundary));
            }
        }

        internal long GetEntityRegistrationToken(SbEntity entity, long entityId)
        {
            lock (_entitiesLock)
            {
                return entity != null && _entityRegistrations.TryGetValue(entity,
                    out EntityRegistration registration) && registration.Id == entityId
                    ? registration.Token : 0;
            }
        }

        internal void UpdateV2EntityState(SbEntity entity)
        {
            if (entity == null || !entity.IsAttached())
            {
                return;
            }

            if (_sceneOwnedEntityElements.Contains(entity.ID))
            {
                return;
            }

            if (_v2ActorByEntity.TryGetValue(entity.ID, out ActorID actorID))
            {
                Transform t = entity.EntityTransform;
                bool avatar = IsAvatarObject(entity.gameObject);
                // A source avatar's EntityTransform is its tracked head, and the root it gives the core below is 2D (the floor
                // point under the head). The "head" anchor was registered at a fixed HeadAnchorHeightMeters above that root, so
                // without this the core saw every source's head at 1.58 m, seated or standing. Its own world pose is written
                // first, so the update below grounds the head's relations (the Facing sightline's eye) on the real head.
                if (avatar && entity.IsSource) WriteSourceHead(actorID, t);
                // An avatar's head also carries its pitch (ABI 3.1): the source publishes it with the yaw, the core
                // passes it through, and every receiver's driver applies it to its head at once, as v1 did.
                UpdateNativeActorState(actorID, t.position, t.eulerAngles.y,
                    avatar ? (float?)HeadOrientation.PitchDegrees(t.rotation) : null);
            }
            else if ((TryGetAuthoredRegion(entity.ID, out long regionElement) && regionElement > 0) || RegionWithdrawn(entity.ID))
            {
                // The region carries the pose, and the affordance surfaces are baked in world space.
                // Write them again when this copy moved, so the support stays the identity in the
                // object frame. A physical object (an own scan installed at its box) is never dragged rigidly: its
                // region is fitted again at its pose (RefitPhysicalAtPose); a user cannot grab one (PlacementMotif.IsOwnScan).
                if (IsPhysicalObject(entity.gameObject)) RefitPhysicalAtPose(entity.ID, entity.EntityTransform.localToWorldMatrix);
                else TryUpdateAuthoredPose(entity.ID, entity.EntityTransform.localToWorldMatrix);
            }
            else
            {
                Matrix4x4 world = entity.EntityTransform.localToWorldMatrix;
                // Content that stands on observed geometry is nested by the hierarchy where it is now (a collider moved onto a table).
                _sceneLayout?.TryRehome(entity.ID);
                SpatialBlendAPI.SetElementTransform(_spaceID, entity.ID, ContentLocalTransform(entity.ID, world));
                // A received object authored without a region still carries baked surfaces.
                RefreshAuthoredSupports(entity.ID, world);
            }
        }

        /// <summary>
        /// Writes where this room's copy of a received object now is into this room's own space. The copy is not a source, so
        /// nothing goes to the peers: each room places its own copy, and the core maps through it. Avatars are driven by the core
        /// (their actor is never written here), and a scene anchor keeps the geometry its adapter wrote. An authored copy moves its
        /// region (<see cref="TryUpdateAuthoredPose"/>, what a placed shared box does); any other copy is written as the arrival wrote
        /// it. Returns false when there is nothing this space places.
        /// </summary>
        internal bool UpdateReceivedCopyPlacement(SbEntity entity)
        {
            if (entity == null || !entity.IsAttached() || entity.IsSource) return false;
            if (_sceneOwnedEntityElements.Contains(entity.ID) || _v2ActorByEntity.ContainsKey(entity.ID) ||
                IsAvatarObject(entity.gameObject))
            {
                return false;
            }
            Matrix4x4 world = entity.EntityTransform.localToWorldMatrix;
            if (TryGetAuthoredRegion(entity.ID, out long regionElement) && regionElement > 0)
            {
                // A physical copy (the box a joiner installs) is carried freely and reaches the core only when it is placed
                // (RefitPhysicalAtPose): its region is fitted there, never dragged rigidly through the furniture.
                if (IsPhysicalObject(entity.gameObject)) return true;
                return TryUpdateAuthoredPose(entity.ID, world);
            }
            WriteReceivedCopyTransform(entity.ID, world);
            RefreshAuthoredSupports(entity.ID, world);
            return true;
        }

        /// <summary>Seam over the native write of an unauthored copy's pose (the same world matrix its arrival wrote).</summary>
        protected virtual void WriteReceivedCopyTransform(long element, Matrix4x4 world) =>
            SpatialBlendAPI.SetElementTransform(_spaceID, element, world);

        private void WriteSourceHead(ActorID actor, Transform head)
        {
            if (!_v2ActorAnchors.Contains((actor, HeadAnchorName))) return;
            Vector3 position = head.position;
            float yaw = head.eulerAngles.y;
            // The core marks every anchor write as an observation change, which sends the actor's relations past its movement
            // gate. Written on every frame, the head would send them every frame; written only when it moved by the core's own
            // gate (Space::kDefaultMinMoveM / kDefaultMinYawRad), it sends what the root's gate would plus a posture change.
            if (_v2TrackedHeads.TryGetValue(actor, out var last) &&
                (position - last.position).sqrMagnitude < SourceHeadGateMeters * SourceHeadGateMeters &&
                Mathf.Abs(Mathf.DeltaAngle(yaw, last.yaw)) < SourceHeadGateDegrees) return;
            SetNativeSourceHead(actor, position, yaw);
            _v2TrackedHeads[actor] = (position, yaw);
        }

        private const string HeadAnchorName = "head";
        // The core's default propagation gate (spatialblend space.h kDefaultMinMoveM = 5 cm, kDefaultMinYawRad = 0.05 degrees).
        internal const float SourceHeadGateMeters = 0.05f;
        internal const float SourceHeadGateDegrees = 0.05f;

        /// <summary>
        /// Seam over sb_set_anchor_transform for a source avatar's head anchor: its world pose (Unity world, yaw in Unity degrees;
        /// the native setter takes no pitch, which still rides the actor state). The root is untouched.
        /// </summary>
        protected virtual void SetNativeSourceHead(long actor, Vector3 worldPosition, float yawDegrees) =>
            SpatialBlendAPI.SetAnchorTransform(_spaceID, actor, HeadAnchorName, worldPosition, yawDegrees);

        /// <summary>Seam over sb_clear_anchor_transform for a released source avatar's head anchor.</summary>
        protected virtual void ClearNativeSourceHead(long space, long actor) =>
            SpatialBlendAPI.ClearAnchorTransform(space, actor, HeadAnchorName);

        /// <summary>Seam over the native source-state update, so a test can see the yaw and pitch it carries.</summary>
        protected virtual void UpdateNativeActorState(long actor, Vector3 position, float yawDegrees, float? headPitchDegrees)
        {
            if (headPitchDegrees.HasValue)
                SpatialBlendAPI.UpdateActorPose(_spaceID, actor, position, yawDegrees, headPitchDegrees.Value);
            else
                SpatialBlendAPI.UpdateActorState(_spaceID, actor, position, yawDegrees);
        }

        public bool IsObjectRegistered(GameObject gameObject)
        {
            return _entities.Find(e => e.gameObject == gameObject) != null;
        }

        private ElementID EnsureRoomElement()
        {
            if (_roomElementID > 0)
            {
                return _roomElementID;
            }

            _roomElementID = SpatialBlendAPI.FindElementBySharedId(_spaceID, V2RoomSharedId);
            if (_roomElementID <= 0)
            {
                _roomElementID = SpatialBlendAPI.AddElement(_spaceID, 0);
            }
            if (_roomElementID > 0)
            {
                SpatialBlendAPI.SetElementSharedId(_spaceID, _roomElementID, V2RoomSharedId);
                SpatialBlendAPI.SetElementTag(_spaceID, _roomElementID, "room");
            }
            return _roomElementID;
        }

        private static long ResolveSharedElementId(GameObject gameObject)
        {
            if (gameObject != null)
            {
                SbMRUKAnchorEntity sceneAnchor = gameObject.GetComponent<SbMRUKAnchorEntity>();
                if (sceneAnchor != null && !string.IsNullOrEmpty(sceneAnchor.SceneLabel))
                {
                    // Two seats of one room are two objects: a seat's identity is the label and its link key.
                    if (sceneAnchor.IsSeat) return SeatIdentity.SharedId(sceneAnchor.SeatKey);
                    // An anchor the user shared under a key is its own object; the key is the sharer's, so the id is the same on every device.
                    if (!string.IsNullOrEmpty(sceneAnchor.ShareKey)) return SharedAnchorName.SharedId(sceneAnchor.SceneLabel, sceneAnchor.ShareKey);
                    return SceneAnchorSharedId(sceneAnchor.SceneLabel);
                }

                // v2 (SpatialBlend): cross-space identity is NOT a Unity-assigned
                // "logical shared id" anymore — the same object registered in two
                // spaces gets DIFFERENT per-instance ids here, and the share operation
                // links them (ElementLinker) so referents/actors resolve by handle.
                // We still hand each element a locally-unique id (the core uses it
                // internally and as a legacy fallback); it just isn't expected to match
                // across spaces.
                NetworkObject networkObject = gameObject.GetComponent<NetworkObject>() ?? gameObject.GetComponentInParent<NetworkObject>();
                if (networkObject != null)
                {
                    uint rawId = networkObject.Id.Raw;
                    if (rawId != 0)
                    {
                        return V2NetworkSharedIdBase + rawId;
                    }
                }

                return V2LocalSharedIdBase + Math.Abs((long)gameObject.GetInstanceID());
            }

            return V2LocalSharedIdBase;
        }

        /// <summary>
        /// Stable across devices: FNV-1a of the label folded to 24 bits on a base above the
        /// network and local id ranges. The derived region and affordance ids (id * 64 + n) stay far inside a long.
        /// </summary>
        public static long SceneAnchorSharedId(string label)
        {
            uint hash = 2166136261u;
            foreach (char c in label)
            {
                hash = unchecked((hash ^ (uint)c) * 16777619u);
            }
            return V2SceneAnchorSharedIdBase + (hash & 0xFFFFFFu);
        }

        private static string ResolveElementTag(GameObject gameObject)
        {
            if (gameObject == null || string.IsNullOrEmpty(gameObject.tag) || gameObject.CompareTag("Untagged"))
            {
                return "object";
            }
            return gameObject.tag;
        }

        private static bool IsAvatarObject(GameObject gameObject)
        {
            return gameObject != null && (gameObject.CompareTag("Avatar") ||
                gameObject.GetComponent<MetaAvatarEntity>() != null ||
                gameObject.GetComponent<SbAvatarEntity>() != null);
        }

        private void OnRealizationTargets(SpaceID space, ActorID actor,
                                          IReadOnlyList<RealizationTarget> targets,
                                          long callbackGeneration)
        {
            IReadOnlyList<RealizationTarget> snapshot =
                targets ?? Array.Empty<RealizationTarget>();

            PushMainThreadTask(() =>
            {
                if (!_v2EntityByActor.TryGetValue(actor, out EntityID entityID))
                {
                    if (_v2UnmappedTargetActors.Add(actor))
                        Debug.LogWarning($"[V2Share] OnRealizationTargets actor={actor} has {snapshot.Count} targets but NO entity mapping (received avatar not registered as actor?)");
                    return;
                }

                SbEntity entity = GetEntity(entityID);
                if (entity == null)
                {
                    if (_v2MissingEntityTargetActors.Add(actor))
                        Debug.LogWarning($"[V2Share] OnRealizationTargets actor={actor} -> entity {entityID} but GetEntity returned null");
                    return;
                }

                var currentPipelines = new HashSet<RealizationPipelineKey>();
                var anchorTargets =
                    new SortedDictionary<string, RealizationTarget>(StringComparer.Ordinal);
                RealizationTarget? rootTarget = null;
                // Every entry remains an independently identified pipeline. The
                // framework's actuation seams are root-level and anchor-level, so
                // choose one deterministic command per seam without coupling the
                // native solves.
                for (int i = 0; i < snapshot.Count; ++i)
                {
                    RealizationTarget target = snapshot[i];
                    // An anchor the received avatar's driver samples is the driver's alone to write (single owner).
                    if (!string.Equals(target.AnchorName, "root", StringComparison.Ordinal))
                    {
                        bool driverOwned;
                        lock (_entitiesLock)
                        {
                            driverOwned = IsDriverOwnedAnchorLocked(actor, target.AnchorName);
                        }
                        if (driverOwned)
                        {
                            continue;
                        }
                    }
                    currentPipelines.Add(target.PipelineKey);
                    if (string.Equals(target.AnchorName, "root",
                                      StringComparison.Ordinal))
                    {
                        if (!rootTarget.HasValue ||
                            IsPreferredTarget(target, rootTarget.Value))
                        {
                            rootTarget = target;
                        }
                    }
                    else if (!anchorTargets.TryGetValue(
                                 target.AnchorName ?? string.Empty,
                                 out RealizationTarget current) ||
                             IsPreferredTarget(target, current))
                    {
                        anchorTargets[target.AnchorName ?? string.Empty] = target;
                    }
                }

                _v2ActivePipelines.TryGetValue(
                    actor, out HashSet<RealizationPipelineKey> previousPipelines);
                if (previousPipelines != null)
                {
                    bool previouslyHadRoot = false;
                    var previousAnchors = new HashSet<string>(StringComparer.Ordinal);
                    foreach (RealizationPipelineKey key in previousPipelines)
                    {
                        if (string.Equals(key.AnchorName, "root",
                                          StringComparison.Ordinal))
                        {
                            previouslyHadRoot = true;
                        }
                        else
                        {
                            previousAnchors.Add(key.AnchorName ?? string.Empty);
                        }
                    }

                    if (previouslyHadRoot && !rootTarget.HasValue)
                    {
                        entity.CancelRealizationTarget();
                    }

                    foreach (string previousAnchor in previousAnchors)
                    {
                        if (!anchorTargets.ContainsKey(previousAnchor))
                        {
                            entity.CancelAnchorTarget(previousAnchor);
                        }
                    }
                }

                // The root is never applied from here. A received avatar's root belongs to its embodied driver,
                // which reads the core's proposal itself every frame; an avatar with no driver does not move.
                // There is no raw-apply path.

                foreach (KeyValuePair<string, RealizationTarget> pair in anchorTargets)
                {
                    RealizationTarget target = pair.Value;
                    entity.ApplyAnchorTarget(
                        pair.Key, target.Position, target.Rotation.eulerAngles.y,
                        target.HasHeight, target.Weight);
                }

                if (currentPipelines.Count == 0)
                {
                    _v2ActivePipelines.Remove(actor);
                }
                else
                {
                    _v2ActivePipelines[actor] = currentPipelines;
                }
            }, callbackGeneration);
        }

        private static bool IsPreferredTarget(
            RealizationTarget candidate, RealizationTarget current)
        {
            return candidate.Weight > current.Weight ||
                (candidate.Weight == current.Weight &&
                 candidate.PipelineKey.CompareTo(current.PipelineKey) < 0);
        }

        // v2 (SpatialBlend): a peer shared an element (object or avatar). Reconstruct
        // it from the serialized payload and run the app's OnShare (AddAsNew /
        // LinkToExisting) via the shared v1 OnShareInternal path. For avatars we also
        // create the local v2 actor so realization targets can drive the reconstruction.
        private void OnElementAdded(SpaceID space, EntityID localElement, long sharedId, string tag,
                                    IntPtr userData, long userDataSize, long callbackGeneration)
        {
            if (!IsCurrentLifecycleGeneration(callbackGeneration))
            {
                return;
            }
            long addReceipt = Interlocked.Increment(ref _eventReceiptSequence);
            SbTrace.Line($"[V2Share] OnElementAdded space={space} localElem={localElement} sharedId={sharedId} tag='{tag}' payloadBytes={userDataSize}");
            if (userData == IntPtr.Zero || userDataSize <= 0)
            {
                // No serialized payload — nothing to instantiate (e.g. geometry-only
                // element). The local element already exists in the native tree.
                return;
            }

            // The native payload is valid only for the duration of this callback, so
            // copy it out before any deferred/main-thread work touches it.
            if (userDataSize > int.MaxValue)
            {
                SpatialBlendDebug.LogError(LogCategory.Data,
                    $"[V2Share] OnElementAdded payload too large for managed copy: {userDataSize}");
                return;
            }
            int size = checked((int)userDataSize);
            byte[] payload = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(userData, payload, 0, size);

            // The source pointer is only valid during the callback. If teardown
            // started while copying, discard the private copy instead of queuing it
            // into a replacement session.
            if (!IsCurrentLifecycleGeneration(callbackGeneration))
            {
                return;
            }

            // FUNDAMENTAL FIX (native→Unity upcall handling): never reconstruct on a
            // background thread. SbEntity.Sync instantiates GameObjects/meshes (Unity
            // API), which is main-thread-only — running it on a worker thread crashed
            // Unity's Metal renderer (BufferMetal::Initialize SEGV) plus a
            // mono_thread_info_current assert when that thread was finalized. Native
            // upcalls must be (a) main-thread-confined and (b) non-blocking, so we
            // enqueue the reconstruction onto the existing main-thread pump (drained in
            // Update). The cross-entity rendezvous that used to require a blocking
            // Monitor.Wait on this thread is now an event-driven pending list, retried
            // on the main thread when a local counterpart registers (TryLazySync).
            if (!AcceptAddedReceipt(localElement, addReceipt, callbackGeneration)) return;
            PushMainThreadTask(() => ReconstructShare(space, localElement, payload,
                addReceipt, callbackGeneration, sharedId),
                callbackGeneration, addReceipt);
        }

        private bool AcceptAddedReceipt(EntityID elementId, long receipt, long generation)
        {
            lock (_entitiesLock)
            {
                // Copying native data may overlap a later removal. Only a genuinely
                // newer add may reopen this id; finishing an old copy must not erase
                // a newer tombstone and resurrect the removed replica.
                if (!IsCurrentLifecycleGeneration(generation) ||
                    (_removedElementReceipts.TryGetValue(elementId, out long removedAt) &&
                     removedAt >= receipt)) return false;
                _removedElementReceipts.Remove(elementId);
                return true;
            }
        }

        // Core: ensure an entity has a v2 actor (default root/head anchors, element
        // binding, initial pose). Idempotent; returns the actor id, or 0 on failure.
        // Used both when the app declares anchors/relations on an entity
        // (relation-driven) and when a received avatar must be driven by targets.
        private ActorID EnsureV2Actor(SbEntity entity)
        {
            if (entity == null)
            {
                return 0;
            }
            if (_v2ActorByEntity.TryGetValue(entity.ID, out ActorID existing))
            {
                return existing;
            }
            ActorID actorID = SpatialBlendAPI.CreateActor(_spaceID);
            if (actorID <= 0)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, $"EnsureV2Actor: failed to create actor for '{entity.gameObject.name}' elem={entity.ID}");
                return 0;
            }
            SpatialBlendAPI.AddAnchor(_spaceID, actorID, "root", Vector3.zero);
            SpatialBlendAPI.AddAnchor(_spaceID, actorID, "head", new Vector3(0f, HeadAnchorHeightMeters, 0f));
            _v2ActorAnchors.Add((actorID, "root"));
            _v2ActorAnchors.Add((actorID, "head"));
            SpatialBlendAPI.SetActorElement(_spaceID, actorID, entity.ID);
            Transform t = entity.EntityTransform != null ? entity.EntityTransform : entity.transform;
            SpatialBlendAPI.UpdateActorState(_spaceID, actorID, t.position, t.eulerAngles.y);
            _v2ActorByEntity[entity.ID] = actorID;
            _v2EntityByActor[actorID] = entity.ID;
            return actorID;
        }

        // Receive-side: only explicit avatar identity creates an actor. Ordinary
        // shared objects remain referents; RegisterAnchor/RegisterRelation retain
        // their explicit actor opt-in path through EnsureV2Actor.
        private void EnsureV2ActorForAvatar(GameObject gameObject, EntityID elementId)
        {
            if (!ShouldCreateActorForReceivedObject(gameObject))
            {
                return;
            }
            SbEntity avatar = gameObject.GetComponent<SbEntity>();
            ActorID actorId = EnsureV2Actor(avatar);
            // A received avatar's root is driven by the app's embodied driver, which moves it toward
            // the core's proposal within the avatar's kinetic bounds. The actor is placed at its
            // arrival pose first, and the driver reads the realized pose from then on.
            ConfigureEmbodiedDriver(avatar, actorId);
        }

        private static bool ShouldCreateActorForReceivedObject(GameObject gameObject)
        {
            return IsAvatarObject(gameObject);
        }

        // We don't necessarily need to run all logics of below methods on the main thread, but want to keep the order of operations
        // consistent from the perspective of the app (e.g. ShareGameObject() -> RegisterInteraction())
        public void ShareGameObject(GameObject gameObject, bool propagate, bool publish = true)
        {
            PushMainThreadTask(() =>
            {
                if (AuthoringInputsReady)
                    _sharesAwaitingWalkable.RemoveAll(parked => parked.GameObject == gameObject);
                var sbEntity = gameObject.GetComponent<SbEntity>();
                if (sbEntity == null)
                {
                    throw new Exception($"GameObject {gameObject.name} must have an SbEntity component");
                }

                if (sbEntity.IsAttached())
                {
                    SpatialBlendDebug.Log(LogCategory.Scene, $"GameObject {gameObject.name} is already attached to a space manager");
                    return;
                }

                lock (_entitiesLock)
                {
                    if (_entities.Contains(sbEntity))
                    {
                        return;
                    }

                    if (!TryAdmitActorShare(sbEntity, out string actorError))
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"ShareGameObject: {actorError}");
                        return;
                    }
                    SbSpatialAuthoring authoring = sbEntity.GetComponent<SbSpatialAuthoring>();
                    // An authored region is a child of the room and must lie inside its walkable ring, which the core checks
                    // only once the ring exists: a region written before it is never checked. The share waits for the
                    // ring and runs again when it is registered (ApplyWalkableRegion).
                    if (authoring != null && !AuthoringInputsReady)
                    {
                        ParkShareUntilWalkable(gameObject, propagate, publish);
                        return;
                    }
                    ElementID room = EnsureRoomElement();
                    long sharedID = ResolveSharedElementId(gameObject);
                    SpatialAuthoringBinding authored = null;
                    ElementID entityID = 0;
                    // A scene anchor is attached to the element the scene-geometry adapter
                    // already created for it. It never gets a second element.
                    bool sceneOwned = TryGetSceneOwnedElements(sbEntity, out long sceneParent,
                        out SpatialAuthoringExistingElements sceneElements);
                    if (!AdmitsLocalShare(sbEntity, sceneOwned, out string refusal))
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"ShareGameObject: {refusal}");
                        return;
                    }
                    // Each room places its own copy: every peer moves its copy of a virtual object
                    // so that the region lies inside its own walkable ring, clear of its own siblings. No peer syncs the
                    // object's world pose; only object-relative state is shared.
                    SpatialAuthoringPlan plan = null;
                    bool planned = authoring != null && TryPlanAuthoring(authoring, out plan);
                    Vector2[] physicalRegion = null;
                    bool fits = planned && (sceneOwned || TryFitAuthoredObjectIntoWalkable(sbEntity.transform, sbEntity.transform,
                        gameObject.name, plan, out physicalRegion));
                    bool applied = fits && TryApplySpatialAuthoring(plan, sharedID, sceneOwned ? sceneParent : 0,
                        sbEntity.transform.localToWorldMatrix,
                        sceneOwned ? null : authoring.footprint, out authored, sceneElements, physicalRegion);
                    if (_pendingWallCut != null)
                    {
                        // An own scan's cut in the walls was made before its region was written; it is kept with the object, or
                        // undone when the region was not registered.
                        Vector2[] cut = _pendingWallCut;
                        _pendingWallCut = null;
                        if (applied && authored != null && authored.ObjectElement > 0) _wallCutsByObject[authored.ObjectElement] = cut;
                        else RetrimWalls();
                    }
                    if (applied)
                    {
                        entityID = authored.ObjectElement;
                    }
                    else if (sceneOwned)
                    {
                        entityID = sceneParent;
                    }
                    else
                    {
                        entityID = SpatialBlendAPI.FindElementBySharedId(_spaceID, sharedID);
                        if (entityID <= 0)
                        {
                            // An object standing on an observed anchor (its footprint lies entirely inside the anchor's) is a
                            // child of the anchor's top surface; any other is a child of the room.
                            long contentParent = room;
                            int contentKey = 0;
                            Matrix4x4 contentParentWorld = Matrix4x4.identity;
                            Collider footprintProbe = authored == null && !IsAvatarObject(gameObject)
                                ? sbEntity.GetComponentInChildren<Collider>() : null;
                            Vector2[] ring = footprintProbe != null ? ColliderGroundRing(footprintProbe) : null;
                            bool nested = ring != null &&
                                TryFindSceneGeometryContainer(ring, out contentParent, out contentParentWorld, out contentKey);
                            if (!nested) contentParent = room;
                            entityID = SpatialBlendAPI.AddElement(_spaceID, contentParent);
                            if (nested && entityID > 0) _contentParentWorldInverse[entityID] = contentParentWorld.inverse;
                            if (entityID > 0 && footprintProbe != null)
                            {
                                // Standing on observed geometry, or able to: it follows its anchor when that is retired or
                                // restaged, and finds the anchor that contains it if the anchor arrives later.
                                long contentElement = entityID;
                                Collider probe = footprintProbe;
                                SbEntity contentEntity = sbEntity;
                                RegisterSceneContent(contentElement, nested ? contentKey : 0,
                                    () => (contentEntity.EntityTransform != null ? contentEntity.EntityTransform : contentEntity.transform).localToWorldMatrix,
                                    () => ColliderGroundRing(probe), () => ColliderGroundRing(probe),
                                    (key, parentWorld) =>
                                    {
                                        if (key > 0) _contentParentWorldInverse[contentElement] = parentWorld.inverse;
                                        else _contentParentWorldInverse.Remove(contentElement);
                                    });
                            }
                        }
                    }
                    if (entityID <= 0)
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"ShareGameObject: failed to create the core element for {gameObject.name} " +
                            $"(planned {planned}, fits {fits}, authored {authored != null}, scene {SpatialHierarchySceneError(_spaceID) ?? "none"})");
                        return;
                    }

                    if (authored == null)
                    {
                        SpatialBlendAPI.SetElementSharedId(_spaceID, entityID, sharedID);
                        SpatialBlendAPI.SetElementTag(_spaceID, entityID, ResolveElementTag(gameObject));
                        if (!sceneOwned)
                        {
                            SpatialBlendAPI.SetElementTransform(_spaceID, entityID, ContentLocalTransform(entityID, sbEntity.transform.localToWorldMatrix));
                        }
                    }

                    if (sceneOwned)
                    {
                        _sceneOwnedEntityElements.Add(entityID);
                    }
                    sbEntity.Attach(this, entityID, true);

                    // Objects with SbSpatialAuthoring already received their physical
                    // footprint as the collider envelope. The Containment Region is a
                    // separate parent and is not this support. Objects without that
                    // declaration still get a collider envelope so spacing is
                    // boundary-relative. Avatars are excluded.
                    if (authored == null && !sceneOwned && !IsAvatarObject(gameObject))
                    {
                        Collider footprintCollider = sbEntity.GetComponentInChildren<Collider>();
                        if (footprintCollider != null)
                        {
                            // The collider may sit on a child: its parameters are composed into the element's frame.
                            SpatialBlendAPI.SetElementCollider(_spaceID, entityID, footprintCollider, sbEntity.EntityTransform);
                        }
                    }

                    // Get current position - use transform.position directly to ensure we get the actual position
                    // EntityTransform might not be initialized yet if ShareGameObject is called before Start()
                    Vector3 currentPosition = sbEntity.transform.position;

                    // Check if position is valid (not (0,0,0) or if it's actually at origin, log a warning)
                    if (currentPosition.magnitude < 0.001f)
                    {
                        SpatialBlendDebug.LogWarning(LogCategory.Scene,
                            $"ShareGameObject: Entity {sbEntity.gameObject.name} has position (0,0,0) when sharing. " +
                            $"This might indicate the entity hasn't been properly initialized yet. " +
                            $"Transform position: {sbEntity.transform.position}, " +
                            $"EntityTransform position: {sbEntity.EntityTransform.position}");
                    }

                    if (IsAvatarObject(gameObject))
                    {
                        ActorID actorID = SpatialBlendAPI.FindActorBySharedId(_spaceID, sharedID);
                        bool createdActor = false;
                        if (actorID <= 0)
                        {
                            actorID = SpatialBlendAPI.CreateActor(_spaceID);
                            createdActor = true;
                        }
                        if (actorID <= 0)
                        {
                            SpatialBlendDebug.LogError(LogCategory.Scene, $"ShareGameObject: failed to create the core actor for avatar {gameObject.name}");
                            return;
                        }

                        if (createdActor)
                        {
                            SpatialBlendAPI.AddAnchor(_spaceID, actorID, "root", Vector3.zero);
                            SpatialBlendAPI.AddAnchor(_spaceID, actorID, "head", new Vector3(0f, HeadAnchorHeightMeters, 0f));
                            _v2ActorAnchors.Add((actorID, "root"));
                            _v2ActorAnchors.Add((actorID, "head"));
                        }
                        SpatialBlendAPI.SetActorElement(_spaceID, actorID, entityID);
                        SpatialBlendAPI.UpdateActorState(_spaceID, actorID, currentPosition, sbEntity.transform.eulerAngles.y);
                        _v2ActorByEntity[entityID] = actorID;
                        _v2EntityByActor[actorID] = entityID;
                    }

                    // v1 shared the entity whatever the flag said. `publish` is the session
                    // set: the host publishes a scene object once; a joiner only attaches
                    // the copy it already placed, so it does not add a second chess.
                    if (publish)
                    {
                        // The region is the object's parent. Share it first, with no
                        // payload, so the receiver can parent the object under the
                        // region it instantiates locally. Geometry stays local.
                        // A shared scene anchor's region goes too: a receiver that links the anchor resolves it
                        // onto its own anchor's region, and one that has no such anchor (the box it installs)
                        // authors the box under it, in its own room (without it the box had no region and the core
                        // refused its pose outside the room).
                        if (authored != null && authored.RegionElement > 0)
                        {
                            ShareSpatialIdentity(_spaceID, authored.RegionElement);
                        }
                        // Carry the serialized entity (prefab type + sync data + blocks)
                        // so the receiving space can reconstruct it via OnShare, exactly
                        // like v1. Freed after the synchronous native call copies it.
                        SerializationData sd = SerializationData.Serialize(sbEntity);
                        try
                        {
                            SpatialBlendAPI.ShareElement(_spaceID, entityID, sd.DataPtr, (long)sd.Length);
                            SbTrace.Line($"[FCShare] shared go='{gameObject.name}' role={(_networkRole == SpaceManagerType.Host ? "host" : "client")} " +
                                      $"elem={entityID} propagate={propagate} pos={sbEntity.transform.position} yaw={sbEntity.transform.eulerAngles.y:0.0} bytes={sd.Length}");
                        }
                        finally
                        {
                            sd.Free();
                        }
                    }
                }
            });
        }

        /// <summary>
        /// v2 (SpatialBlend) app API: declare a named subject anchor on the actor
        /// backing <paramref name="actor"/>. Subject anchors are body-relative
        /// handles (root, chest, hips, hand, ...) that relations bind to.
        /// The actor's "root" and "head" anchors are created automatically when the
        /// avatar is shared; this adds additional anchors (e.g. "hand").
        /// Idempotent: re-adding the same anchor is a no-op.
        /// </summary>
        public void RegisterAnchor(GameObject actor, string name, Vector3 localOffset)
        {
            PushMainThreadTask(() =>
            {
                if (string.IsNullOrEmpty(name))
                {
                    SpatialBlendDebug.LogError(LogCategory.State, "SpatialBlendManager::RegisterAnchor - anchor name is null or empty");
                    return;
                }

                if (!IsObjectRegistered(actor))
                {
                    SpatialBlendDebug.LogError(LogCategory.State, $"SpatialBlendManager::RegisterAnchor - actor {actor?.name} not registered");
                    return;
                }

                var entity = actor.GetComponent<SbEntity>();
                // Declaring an anchor implies this entity is an actor: create the
                // v2 actor on demand (relation-driven), even if it isn't tagged as
                // an avatar.
                ActorID actorID = EnsureV2Actor(entity);
                if (actorID <= 0)
                {
                    return;
                }

                if (!_v2ActorAnchors.Add((actorID, name)))
                {
                    return;
                }

                SpatialBlendAPI.AddAnchor(_spaceID, actorID, name, localOffset);
            });
        }

        /// <summary>
        /// v2 (SpatialBlend) app API: declare a spatial relation that binds one of
        /// the actor's subject anchors to referents in the shared interaction
        /// hierarchy through a relation kernel (Spacing / Facing / AffordancePose).
        /// This is the v2 replacement for <see cref="RegisterInteraction"/>.
        /// </summary>
        public void RegisterRelation(SpatialRelation relation)
        {
            PushMainThreadTask(() =>
            {
                if (relation == null || !relation.IsValid())
                {
                    SpatialBlendDebug.LogError(LogCategory.State, "SpatialBlendManager::RegisterRelation - relation is null or invalid");
                    return;
                }

                if (_relations.Contains(relation))
                {
                    return;
                }

                if (!IsObjectRegistered(relation.Source))
                {
                    SpatialBlendDebug.LogError(LogCategory.State, $"SpatialBlendManager::RegisterRelation - source {relation.Source.name} not registered");
                    return;
                }

                var sourceEntity = relation.Source.GetComponent<SbEntity>();
                // Declaring a relation implies the source is an actor: create the
                // v2 actor on demand (relation-driven) if it doesn't have one yet.
                ActorID actorID = EnsureV2Actor(sourceEntity);
                if (actorID <= 0)
                {
                    SpatialBlendDebug.LogWarning(LogCategory.State, $"SpatialBlendManager::RegisterRelation - could not create actor for {relation.Source.name}; skipping {relation}");
                    return;
                }

                if (!_v2ActorAnchors.Contains((actorID, relation.AnchorName)))
                {
                    SpatialBlendDebug.LogWarning(LogCategory.State, $"SpatialBlendManager::RegisterRelation - anchor '{relation.AnchorName}' not declared on {relation.Source.name}; call RegisterAnchor first. Binding anyway.");
                }

                DispatchRelationBinding(actorID, SpatialAuthoringCompiler.PlanRelation(
                    relation.Kind, relation.AnchorName, relation.Tag));

                _relations.Add(relation);
            });
        }

        private void DispatchRelationBinding(ActorID actorID, RelationBinding binding)
        {
            SbKernelType kernelType = MapRelationKernel(binding.Kind);
            // Scope belongs to the relation family: only AffordancePose carries a tag.
            if (binding.Kind == RelationKind.AffordancePose)
                SpatialBlendAPI.BindAffordance(_spaceID, actorID, binding.Anchor, binding.Tag);
            else
                SpatialBlendAPI.Bind(_spaceID, actorID, binding.Anchor, kernelType);
        }

        private static SbKernelType MapRelationKernel(RelationKind kind)
        {
            switch (kind)
            {
                case RelationKind.Facing:
                    return SbKernelType.Facing;
                case RelationKind.AffordancePose:
                    return SbKernelType.AffordancePose;
                case RelationKind.Spacing:
                default:
                    return SbKernelType.Spacing;
            }
        }

        // Legacy v1 proximity API. Prefer RegisterRelation() for v2 (SpatialBlend).
        // Euclidean kernels bind Spacing on root. Angular kernels bind Facing on chest.
        public void RegisterInteraction(Interaction interaction)
        {
            PushMainThreadTask(() =>
            {
                if (_interactions.Contains(interaction))
                {
                    return;
                }

                if (!IsObjectRegistered(interaction.Source))
                {
                    List<string> notRegistered = new List<string>();
                    if (!IsObjectRegistered(interaction.Source))
                    {
                        notRegistered.Add(interaction.Source.name);
                    }
                    SpatialBlendDebug.LogError(LogCategory.State, $"SpatialBlendManager::RegisterInteraction - Objects not registered: {string.Join(", ", notRegistered)}");
                    return;
                }

                var sourceEntity = interaction.Source.GetComponent<SbEntity>();
                if (!_v2ActorByEntity.TryGetValue(sourceEntity.ID, out ActorID actorID))
                {
                    SpatialBlendDebug.LogWarning(LogCategory.State, $"SpatialBlendManager::RegisterInteraction - source {interaction.Source.name} has no core actor; skipping {interaction.Type}");
                    return;
                }

                if (!SpatialAuthoringCompiler.TryPlanLegacyInteraction((ProximityType)interaction.Type,
                        out RelationBinding binding))
                {
                    SpatialBlendDebug.LogWarning(LogCategory.State, $"SpatialBlendManager::RegisterInteraction - no core kernel mapping for {interaction.Type}");
                    return;
                }
                // The Facing anchor is the head, added with the actor: no second anchor is made for it here.
                DispatchRelationBinding(actorID, binding);

                _interactions.Add(interaction);
            });
        }

        public virtual void Update()
        {
            PollTunneledJoin();

            // Keep this empty or reserved for non-transform related updates if needed in future.
            // All transform/state updates are now handled in LateUpdate to ensure NavMeshAgent
            // modifications to the Transform have already been applied.

            while (true)
            {
                Action task;
                lock (taskLock)
                {
                    if (taskQueue.Count == 0)
                    {
                        break;
                    }
                    task = taskQueue.Dequeue();
                }
                task();
            }
        }

        // Move transform/state propagation after NavMesh internal update so that
        // movement driven by NavMeshAgent is properly detected (Transform.hasChanged).
        /// <summary>The longest frame a source tick accounts for (a hitch or a pause is not 0.25 s of motion and time each).</summary>
        internal const double MaxSourceStepSeconds = 0.25;

        /// <summary>Test seam: the frame's duration in place of Time.unscaledDeltaTime.</summary>
        internal float? TestOnlySourceStepSeconds;

        /// <summary>
        /// The real duration of this frame, as the source time each actor tick of this frame adds (sb_set_update_interval). The native
        /// default of 1/30 s per tick counted a 60-90 Hz app's ticks at two to three times real time, so the receivers' interaction
        /// latency limit (5 s of source time) ran out after 2 s or less and their avatars were teleported before they could walk there.
        /// </summary>
        private void StepSourceClock()
        {
            if (_spaceID <= 0) return;
            double seconds = TestOnlySourceStepSeconds ?? Time.unscaledDeltaTime;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0) return;
            SetNativeSourceStep(Math.Min(seconds, MaxSourceStepSeconds));
        }

        /// <summary>Seam over the native per-tick source step (sb_set_update_interval).</summary>
        protected virtual void SetNativeSourceStep(double seconds) => SpatialBlendAPI.SetUpdateInterval(_spaceID, seconds);

        public virtual void LateUpdate()
        {
            StepSourceClock();
            List<DrivenAvatarWrite> drivenWrites = null;
            List<PendingInteraction> interactions;
            lock (_entitiesLock)
            {
                foreach (SbEntity entity in _entities)
                {
                    if (entity == null || !entity.IsAttached() || !entity.IsSource)
                    {
                        continue;
                    }

                    // Only push when the Unity transform actually changed. Static
                    // furniture then pushes once after it is positioned, then never
                    // again (perf). Unity sets hasChanged=true on ANY modification,
                    // including scene-load/instantiation positioning, so the first
                    // LateUpdate after the piece is placed still captures its real
                    // transform — it does NOT freeze the core at an origin/registration
                    // value. Reset the flag after pushing so the next change re-triggers.
                    //
                    // An entity with a v2 actor (an avatar: it has relations bound) is the exception. The core counts
                    // the calls of its source tick: its movement gate holds a still source back by construction and
                    // its heartbeat (kHeartbeatInterval calls) is what sends a still source's relations again, so
                    // that a receiver whose own body is still on its way re-solves against where that body is now. An
                    // actor that is only pushed when it moved starves the heartbeat: the observed avatar arrived at
                    // the table and its head answered a position it had left seconds before, until the source moved
                    // again.
                    Transform t = entity.EntityTransform;
                    // A carried or settling object's region is not pushed (it would be refused at every edge it crosses): its
                    // change is kept and pushed once at rest, after the drop's pull. Peers keep its last committed pose meanwhile.
                    if (t != null && t.hasChanged && IsInMotion(entity)) continue;
                    if (t != null && !t.hasChanged && !_v2ActorByEntity.ContainsKey(entity.ID))
                    {
                        continue;
                    }

                    UpdateV2EntityState(entity);
                    if (t != null)
                    {
                        t.hasChanged = false;
                    }
                }

                // A copy this room received (not its source) is placed by this room: each participant arranges its own copy, and the
                // core carries no object pose between spaces (the wire has actor states, shares and relations, never an element
                // transform). Its moves still have to reach this room's own space, which maps a contained remote avatar through the
                // copy and refers a relation to it. A move a user makes to a copy is pushed like a source's.
                foreach (SbEntity entity in _entities)
                {
                    if (entity == null || !entity.IsAttached() || entity.IsSource) continue;
                    Transform t = entity.EntityTransform;
                    if (t == null || !t.hasChanged) continue;
                    if (IsInMotion(entity)) continue;
                    // A copy that was not written keeps its change and is tried again next frame: a registration not current yet
                    // (a reconnect) catches up. A received avatar never is (its actor is the core's), which costs a lookup a frame.
                    if (UpdateReceivedCopyPlacement(entity)) t.hasChanged = false;
                }

                // The local avatars' interaction value is the core's source-side transfer fact for the update just pushed.
                foreach (SbEntity entity in _entities)
                {
                    if (entity == null || !entity.IsAttached() || !entity.IsSource) continue;
                    if (_v2ActorByEntity.TryGetValue(entity.ID, out ActorID sourceActor))
                    {
                        ObserveSourceTransfer(entity, sourceActor);
                    }
                }

                // Each RECEIVED clone's embodied driver publishes the avatar's realized pose to the
                // core, reads the proposal and queues the pose it moved the body to. The write happens
                // after the lock is released. Source avatars are pushed above.
                foreach (SbEntity entity in _entities)
                {
                    if (entity == null || !entity.IsAttached() || entity.IsSource)
                    {
                        continue;
                    }
                    if (_v2ActorByEntity.TryGetValue(entity.ID, out ActorID cloneActor))
                    {
                        CollectDrivenAvatarWrite(entity, cloneActor, ref drivenWrites);
                    }
                }

                foreach (ActorID actorID in _v2ActorByEntity.Values)
                {
                    PollNativeRealizationTargets(actorID);
                }
                interactions = DrainPendingInteractionsLocked();
            }
            RaiseInteractions(interactions);
            ApplyDrivenAvatarWrites(drivenWrites);
            TickLocalDiscontinuities();
        }

        // Seam over the native poll, so a fake native layer can drive LateUpdate.
        protected virtual void PollNativeRealizationTargets(long actor) =>
            SpatialBlendAPI.PollRealizationTargets(_spaceID, actor);

        internal void UpdateData(EntityID entity_id, IntPtr user_data, ulong user_data_size)
        {
            SpatialBlendDebug.Log(LogCategory.Data, $"SpatialBlendManager::UpdateData ignored for entity {entity_id}; the core propagates relation payloads, not opaque entity data.");
        }

        // Internal callbacks from native API. This is called on a gRPC server thread.
        // SpaceManager is responsible to process them internally, and call the corresponding callbacks (for both SbApp and SbEntity) on the main thread.
        // Internal async task queue

        private readonly object taskLock = new object();
        private Queue<Action> taskQueue = new Queue<Action>();

        private void PushMainThreadTask(Action task)
        {
            PushMainThreadTask(task, Interlocked.Read(ref _lifecycleGeneration),
                Interlocked.Increment(ref _eventReceiptSequence));
        }

        private void PushMainThreadTask(Action task, long generation)
        {
            PushMainThreadTask(task, generation,
                Interlocked.Increment(ref _eventReceiptSequence));
        }

        private void PushMainThreadTask(Action task, long generation, long ingressReceipt)
        {
            if (task == null || !IsCurrentLifecycleGeneration(generation))
            {
                return;
            }
            lock (taskLock)
            {
                if (!IsCurrentLifecycleGeneration(generation))
                {
                    return;
                }
                taskQueue.Enqueue(() =>
                {
                    if (IsCurrentLifecycleGeneration(generation))
                    {
                        long previousIngressReceipt = _currentTaskIngressReceipt;
                        _currentTaskIngressReceipt = ingressReceipt;
                        try
                        {
                            task();
                        }
                        finally
                        {
                            _currentTaskIngressReceipt = previousIngressReceipt;
                        }
                    }
                });
            }
        }

        internal bool IsCurrentLifecycleGeneration(long generation)
        {
            return !_isTearingDown && generation == Interlocked.Read(ref _lifecycleGeneration);
        }

        private bool IsCurrentLifecycleCallback(SpaceID expectedSpace, long generation,
                                                SpaceID callbackSpace)
        {
            return callbackSpace == expectedSpace && callbackSpace == _spaceID &&
                   IsCurrentLifecycleGeneration(generation);
        }

        // A manager can be reused only after teardown. Incrementing here makes all
        // callbacks/work captured by the prior session stale before new native
        // callback registration begins.
        private void BeginManagedLifecycle()
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            _isTearingDown = false;
            lock (taskLock)
            {
                taskQueue.Clear();
            }
            lock (_entitiesLock)
            {
                _removedElementReceipts.Clear();
                _queuedRemovalRegistrationTokens.Clear();
            }
        }

        private void BeginManagedTeardown()
        {
            if (_isTearingDown)
            {
                return;
            }
            _isTearingDown = true;
            Interlocked.Increment(ref _lifecycleGeneration);
            // Release observers can re-enter app registration and scene ingestion.
            // Readiness must already be false before the first such callback.
            _initialized = false;
            _spaceCreated = false;
            _appsStarted = false;

            // No task from the retired generation may be run after teardown. Tasks
            // enqueued re-entrantly by cleanup are refused by _isTearingDown.
            lock (taskLock)
            {
                taskQueue.Clear();
            }

            List<KeyValuePair<SbEntity, EntityRegistration>> attached;
            lock (_entitiesLock)
            {
                attached = new List<KeyValuePair<SbEntity, EntityRegistration>>(
                    _entityRegistrations);
            }
            foreach (KeyValuePair<SbEntity, EntityRegistration> attachedRegistration in attached)
            {
                SbEntity entity = attachedRegistration.Key;
                if (entity == null)
                {
                    continue;
                }
                // Detach owns target/controller release. Only invoke it while the
                // exact old registration remains current; a reentrant attachment to
                // another manager must survive this manager's teardown.
                if (IsEntityRegistrationCurrent(entity, attachedRegistration.Value.Id,
                                                attachedRegistration.Value.Token))
                {
                    try { entity.Detach(); }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"[SpatialBlend] Entity teardown failed: {exception.Message}");
                    }
                }
                RetireOwnedNativeReplica(entity, attachedRegistration.Value.Id,
                    attachedRegistration.Value.Token, 0);
            }

            lock (_entitiesLock)
            {
                foreach (IEmbodiedDriver driver in new List<IEmbodiedDriver>(_v2EmbodiedDrivers.Values))
                {
                    if (!(driver is OvrAvatarEmbodiedDriver ovr)) continue;
                    try { ovr.Close(); }
                    catch (Exception exception) { Debug.LogWarning($"[SpatialBlend] Motion source teardown failed: {exception.Message}"); }
                }
                _entities.Clear();
                _entityRegistrations.Clear();
                _reconstructedNativeReplicas.Clear();
                _v2ActorByEntity.Clear();
                _v2EntityByActor.Clear();
                _v2ActorAnchors.Clear();
                _v2TrackedHeads.Clear();
                _v2ActivePipelines.Clear();
                _v2UnmappedTargetActors.Clear();
                _v2MissingEntityTargetActors.Clear();
                _v2EmbodiedDrivers.Clear();
                _v2UndrivableAvatars.Clear();
                _embodiedCore = null;
                _removedElementReceipts.Clear();
                _queuedRemovalRegistrationTokens.Clear();
            }
            _relations.Clear();
            ClearContainmentState();
            _interactions.Clear();
            _entityStatesToMonitor.Clear();
            _pendingShares.Clear();
            foreach (DeferredShare deferred in _deferredShares.Values)
            {
                DiscardReconstructedShareClone(deferred.Replica, deferred.Entity, deferred.CloneVersion);
            }
            _deferredShares.Clear();
            _sharesAwaitingWalkable.Clear();
            _reportedSharesAwaitingWalkable = false;
            _receivedAuthoringAwaitingWalkable.Clear();
            _roomElementID = -1;
            ClearAuthoredRegions();
            _sceneOwnedEntityElements.Clear();
            _wallElementIDs.Clear();
            _wallShapes.Clear();
            _wallPieces.Clear();
            _wallPieceIDs.Clear();
            _wallCutsByObject.Clear();
            _lingeringWallCuts.Clear();
            _wallsDirty = false;
            _pendingWallCut = null;
            _contentParentWorldInverse.Clear();
            _registeredWalkable = null;
            _roomRegion = null;
            _localSceneGeometryRegistered = false;
            _initialized = false;
            _spaceCreated = false;
            _appsStarted = false;
        }

        // A local SbEntity that an inbound share was waiting for has just become
        // available (PrepareSync calls this). Re-attempt any parked shares on the
        // main-thread pump — reconstruction touches Unity API, so it must not run on
        // the caller's thread.
        internal void TryLazySync()
        {
            PushMainThreadTask(RetryPendingShares);
        }

        // Inbound shares whose local counterpart was not yet registered when they
        // arrived. Touched only on the main thread (every entry point hops through
        // PushMainThreadTask), so no lock is needed. Each entry keeps a private copy of
        // the serialized payload — the native buffer is valid only during the upcall.
        private struct PendingShare
        {
            public SpaceID space;
            public EntityID entityId;
            public byte[] payload;
            public long receipt;
            public long generation;
            public long sharedId;
        }
        private readonly List<PendingShare> _pendingShares = new List<PendingShare>();

        // Elements that an entity is attached to but the scene-geometry adapter owns. The
        // adapter positions and retires them; the entity must not write their pose.
        private readonly HashSet<long> _sceneOwnedEntityElements = new HashSet<long>();

        // Main-thread entry for an inbound share (enqueued from OnElementAdded). Attempts
        // reconstruction once; if the local counterpart is not ready, the share is parked
        // and retried when a local entity registers (TryLazySync).
        private void ReconstructShare(EntityID entity_id, byte[] payload)
        {
            ReconstructShare(_spaceID, entity_id, payload,
                Interlocked.Read(ref _eventReceiptSequence),
                Interlocked.Read(ref _lifecycleGeneration), 0);
        }

        private void ReconstructShare(SpaceID space, EntityID entity_id, byte[] payload,
                                      long receipt, long generation, long sharedId)
        {
            if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
            {
                return;
            }
            try
            {
                if (!TryReconstructShareOnce(space, entity_id, payload, receipt, generation, sharedId) &&
                    IsCurrentInboundShare(space, entity_id, receipt, generation))
                {
                    _pendingShares.Add(new PendingShare
                    {
                        space = space,
                        entityId = entity_id,
                        payload = payload,
                        receipt = receipt,
                        generation = generation,
                        sharedId = sharedId,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[V2Share] ReconstructShare: reconstruction FAILED for element {entity_id}: {ex}");
            }
        }

        // Re-attempt every parked share on the main thread (TryLazySync). Resolved shares
        // are completed and dropped; the rest stay parked for the next registration.
        private void RetryPendingShares()
        {
            if (_pendingShares.Count == 0) return;
            long retryGeneration = Interlocked.Read(ref _lifecycleGeneration);
            var still = new List<PendingShare>();
            foreach (var ps in _pendingShares)
            {
                if (!IsCurrentInboundShare(ps.space, ps.entityId, ps.receipt, ps.generation))
                {
                    continue;
                }
                try
                {
                    if (!TryReconstructShareOnce(ps.space, ps.entityId, ps.payload,
                                                  ps.receipt, ps.generation, ps.sharedId) &&
                        IsCurrentInboundShare(ps.space, ps.entityId, ps.receipt,
                                              ps.generation))
                    {
                        still.Add(ps);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[V2Share] RetryPendingShares: reconstruction FAILED for element {ps.entityId}: {ex}");
                }
            }
            // An app callback may have retired this lifecycle and populated a new
            // session's pending list. The old retry pass must not clear it.
            if (!IsCurrentLifecycleGeneration(retryGeneration)) return;
            _pendingShares.Clear();
            _pendingShares.AddRange(still);
        }

        // Reconstruct one inbound share. MUST run on the main thread (SbEntity.Sync
        // instantiates GameObjects/meshes). Returns true if the entity resolved (and was
        // fully handled via CompleteShare), false if the local counterpart is not yet
        // available (caller parks it for a later retry).
        private bool TryReconstructShareOnce(SpaceID space, EntityID entity_id, byte[] payload,
                                             long receipt, long generation, long sharedId)
        {
            if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
            {
                return true;
            }
            // Its local counterpart may be waiting for the walkable ring (ParkShareUntilWalkable). It is not attached yet, so
            // linking to it now would be refused: that share waits with it and is retried once it is registered.
            if (InboundShareWaitsForParkedCounterpart(sharedId))
            {
                return false;
            }
            var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                SerializationData serializationData =
                    SerializationData.Read(handle.AddrOfPinnedObject(), (ulong)payload.Length);
                var (gameObject, entity) =
                    SbEntity.Sync(serializationData.GetEntityType(), serializationData.GetSyncData(),
                        serializationData.GetSyncDataLength());
                if (entity == null)
                {
                    return false;  // lazy-sync: local counterpart not ready yet
                }
                long cloneAttachmentVersion = entity.AttachmentVersion;
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
                {
                    DiscardReconstructedShareClone(gameObject, entity, cloneAttachmentVersion);
                    return true;
                }
                try
                {
                    CompleteShare(space, entity_id, gameObject, entity, serializationData,
                                  receipt, generation, cloneAttachmentVersion, sharedId);
                }
                catch
                {
                    DiscardReconstructedShareClone(gameObject, entity, cloneAttachmentVersion);
                    throw;
                }
                return true;
            }
            finally
            {
                handle.Free();
            }
        }

        // Finish a resolved share — always on the main thread, so the formerly-nested
        // RunOnMainThreadAsync().Wait() / PushMainThreadTask hops are gone: deserialize
        // blocks, run the app's OnShare decision, and apply AddAsNew / LinkToExisting.
        private bool TryApplyReceivedAuthoring(SbEntity entity, long elementId, long sharedId)
        {
            if (entity == null || elementId <= 0 || sharedId == 0) return false;
            SbSpatialAuthoring authoring = entity.GetComponent<SbSpatialAuthoring>();
            if (authoring != null && AwaitsReceivedModel(entity, authoring))
            {
                // A received scan with no box declares its footprint from its collider, which is the template's default until the
                // model is loaded and fitted: its region is written once the model is there.
                ParkReceivedAuthoringUntilModel((SbNativeEntity)entity, elementId, sharedId);
                return false;
            }
            if (authoring == null || !TryPlanAuthoring(authoring, out SpatialAuthoringPlan plan))
            {
                return false;
            }
            // As for a local share: the received region is written once this room has its walkable ring and furniture.
            if (!AuthoringInputsReady)
            {
                _receivedAuthoringAwaitingWalkable.Add(new ReceivedAuthoringAwaitingWalkable
                {
                    Entity = entity, ElementId = elementId, SharedId = sharedId, Generation = ConnectionGeneration,
                });
                SpatialBlendDebug.LogWarning(LogCategory.Scene,
                    $"Received '{entity.gameObject.name}': its region is written once the room's walkable region is registered.");
                return false;
            }
            Transform pose = entity.EntityTransform != null ? entity.EntityTransform : entity.transform;
            bool sceneOwned = TryGetSceneOwnedElements(entity, out long sceneParent,
                out SpatialAuthoringExistingElements sceneElements) && sceneParent == elementId;
            // This room's copy is placed in this room: fitted like a local share before its region is written.
            // A received copy is never nested under an anchor (the core does not reparent a received replica), so an
            // anchor under it is a sibling like any other and the copy is moved clear of it.
            Vector2[] regionOverride = null;
            bool fitted = sceneOwned || TryFitAuthoredObjectIntoWalkable(pose, entity.transform, entity.gameObject.name, plan,
                out regionOverride);
            // A received copy is never a physical counterpart: a cut a fit left pending is not this object's.
            if (_pendingWallCut != null)
            {
                _pendingWallCut = null;
                RetrimWalls();
            }
            if (!fitted)
            {
                return false;
            }
            // The footprint is the collider the declaration names, never the entity's own, and an
            // adapter-owned scene anchor keeps the geometry its adapter wrote.
            Collider footprint = sceneOwned ? null : authoring.footprint;
            return TryApplySpatialAuthoring(plan, sharedId, elementId, pose.localToWorldMatrix, footprint,
                out _, sceneOwned ? sceneElements : null, regionOverride);
        }

        // Whether a received copy's declaration still reads a collider its model has not been fitted to yet.
        internal static bool AwaitsReceivedModel(SbEntity entity, SbSpatialAuthoring authoring) =>
            entity is SbNativeEntity native && native.IsRemoteReplica && !native.ModelLoaded && authoring != null &&
            authoring.footprint != null && (authoring.footprintPolygon == null || authoring.footprintPolygon.Length < 3);

        private void ParkReceivedAuthoringUntilModel(SbNativeEntity entity, long elementId, long sharedId)
        {
            long generation = ConnectionGeneration;
            void OnLoaded(SbNativeEntity loaded)
            {
                if (!loaded.ModelLoaded) return;   // the bytes arrived; the model is not instantiated yet
                loaded.ContentReady -= OnLoaded;
                if (this == null || loaded == null || generation != ConnectionGeneration || !loaded.IsAttached() ||
                    loaded.ID != elementId) return;
                TryApplyReceivedAuthoring(loaded, elementId, sharedId);
            }
            entity.ContentReady += OnLoaded;
            SpatialBlendDebug.Log(LogCategory.Scene,
                $"Received '{entity.gameObject.name}': its region is written once its model is loaded.");
        }

        /// <summary>Metres a fitted region keeps from the walkable ring, so a region at the ring is not refused for rounding.</summary>
        internal const float RegionFitMarginMeters = 0.01f;

        private sealed class ShareAwaitingWalkable
        {
            internal GameObject GameObject;
            internal bool Propagate;
            internal bool Publish;
            internal long Generation;
            internal long SharedId;
        }

        private sealed class ReceivedAuthoringAwaitingWalkable
        {
            internal SbEntity Entity;
            internal long ElementId;
            internal long SharedId;
            internal long Generation;
        }

        private readonly List<ShareAwaitingWalkable> _sharesAwaitingWalkable = new List<ShareAwaitingWalkable>();
        private readonly List<ReceivedAuthoringAwaitingWalkable> _receivedAuthoringAwaitingWalkable =
            new List<ReceivedAuthoringAwaitingWalkable>();

        private bool _reportedSharesAwaitingWalkable;

        private void ParkShareUntilWalkable(GameObject gameObject, bool propagate, bool publish)
        {
            PruneSharesAwaitingWalkable();
            if (_sharesAwaitingWalkable.Exists(parked => parked.GameObject == gameObject)) return;
            _sharesAwaitingWalkable.Add(new ShareAwaitingWalkable
            {
                GameObject = gameObject, Propagate = propagate, Publish = publish, Generation = ConnectionGeneration,
                SharedId = ResolveSharedElementId(gameObject),
            });
            // Once per wait: an app that never registers a walkable region would otherwise never share these objects.
            if (!_reportedSharesAwaitingWalkable)
            {
                _reportedSharesAwaitingWalkable = true;
                SpatialBlendDebug.LogError(LogCategory.Scene,
                    $"ShareGameObject: '{gameObject.name}' (and any later authored object) waits for the room's walkable " +
                    "region and scene geometry: its authored region must lie inside it, clear of the furniture. Register the " +
                    "walkable region (RegisterWalkable) and, when the app waits for it, the scene geometry " +
                    "(MarkLocalSceneGeometryRegistered)."); 
            }
        }

        private void PruneSharesAwaitingWalkable()
        {
            long generation = ConnectionGeneration;
            _sharesAwaitingWalkable.RemoveAll(parked => parked.GameObject == null || parked.Generation != generation);
        }

        /// <summary>
        /// True when the inbound share's local counterpart may be parked until the walkable ring. A network object or a
        /// scene anchor has the same shared id on every peer, so only the share with a parked object's id waits. An object
        /// with neither has a per-instance id that differs across peers and cannot be matched, so its share waits while
        /// any such object is parked (linking it now would find no attached counterpart and drop the clone). Every other
        /// share, an avatar's included, goes on.
        /// </summary>
        internal bool InboundShareWaitsForParkedCounterpart(long sharedId)
        {
            PruneSharesAwaitingWalkable();
            if (sharedId == 0) return false;
            bool perInstance = IsPerInstanceSharedId(sharedId);
            return _sharesAwaitingWalkable.Exists(parked =>
                parked.SharedId == sharedId || (perInstance && IsPerInstanceSharedId(parked.SharedId)));
        }

        // The ids ResolveSharedElementId derives from the instance: they are not the same on two peers.
        private static bool IsPerInstanceSharedId(long sharedId) =>
            sharedId >= V2LocalSharedIdBase && sharedId < V2SceneAnchorSharedIdBase;

        // Runs the shares and received regions that waited for the walkable ring, in arrival order. A share stays listed
        // until its own task runs (ShareGameObject drops it), so an inbound share queued before that task keeps waiting.
        private void ResumeAuthoringAwaitingWalkable()
        {
            long generation = ConnectionGeneration;
            PruneSharesAwaitingWalkable();
            _reportedSharesAwaitingWalkable = false;
            var shares = new List<ShareAwaitingWalkable>(_sharesAwaitingWalkable);
            foreach (ShareAwaitingWalkable parked in shares)
            {
                ShareGameObject(parked.GameObject, parked.Propagate, parked.Publish);
            }
            var received = new List<ReceivedAuthoringAwaitingWalkable>(_receivedAuthoringAwaitingWalkable);
            _receivedAuthoringAwaitingWalkable.Clear();
            foreach (ReceivedAuthoringAwaitingWalkable parked in received)
            {
                if (parked.Entity != null && parked.Generation == generation && parked.Entity.IsAttached() &&
                    parked.Entity.ID == parked.ElementId)
                    TryApplyReceivedAuthoring(parked.Entity, parked.ElementId, parked.SharedId);
            }
            // Inbound shares that waited for those local counterparts run after them (the queue is in order).
            if (shares.Count > 0) TryLazySync();
        }

        /// <summary>
        /// Moves this room's copy of a virtual object so that its authored region is valid: inside the room's
        /// ring, clear of the walls, the furniture and the region's siblings, the checks the core makes against it. Each room
        /// places its own copy. Nothing is refused: a region that does not fit is pulled to the nearest spot that does, however far
        /// (<see cref="RegionPull"/>). <paramref name="frame"/> carries the authoring frame; <paramref name="placementRoot"/> is what
        /// moves (the SbEntity's own transform). The placement is RoomFit.Place, the one rule every path uses. An object that stands for a physical one (this user's own
        /// scan) is never moved: it is installed where it was scanned. False, with an error, only when no spot in this room fits
        /// the region (a room too small or too cluttered for it): it is then shared without its region.
        /// </summary>
        internal bool TryFitAuthoredObjectIntoWalkable(Transform frame, Transform placementRoot, string objectName,
                                                       SpatialAuthoringPlan plan) =>
            TryFitAuthoredObjectIntoWalkable(frame, placementRoot, objectName, plan, out _);

        /// <summary>
        /// The fit, and the region to write instead of the declared one (null: the declared one). An object that cannot move or
        /// a piece of furniture is never refused: an own scan keeps its pose and its region is fitted around it;
        /// a shared piece of furniture (the box a joiner installs) whose region with its stand-off fits nowhere is pulled by its
        /// footprint alone and its region is fitted around it, as a scene anchor's is (<see cref="FitPhysicalRegion"/>).
        /// </summary>
        internal bool TryFitAuthoredObjectIntoWalkable(Transform frame, Transform placementRoot, string objectName,
                                                       SpatialAuthoringPlan plan, out Vector2[] regionOverride)
        {
            regionOverride = null;
            WalkableRegion walkable = AuthoringWalkable;
            if (walkable == null || !walkable.HasBoundary) return true;
            Matrix4x4 frameWorld = frame.localToWorldMatrix * plan.FrameToObject;
            Vector2[] region = WorldRing(plan, frameWorld, SpatialHierarchyNodeKind.Region);
            if (region == null) return true;
            Vector2[] footprint = WorldFootprintRing(plan, frameWorld);
            GameObject placed = placementRoot != null ? placementRoot.gameObject : frame.gameObject;
            if (IsPhysicalCounterpart(placed))
            {
                // An own scan is installed where it was scanned: what is virtual steps aside from its footprint, and its region
                // is fitted around it. A wall it crosses gives way (RetrimWalls), so its region may extend the room through it.
                Vector2[] scanFoot = WorldFootprintRing(plan, frameWorld);
                MakeRoomFor(scanFoot, "scanned object");
                regionOverride = FitPhysicalRegion(plan, frameWorld);
                Vector2[] regionWorld = regionOverride != null ? ToWorldRing(regionOverride, frameWorld)
                    : WorldRing(plan, frameWorld, SpatialHierarchyNodeKind.Region);
                _pendingWallCut = WallCut(scanFoot, regionWorld);
                RetrimWalls(_pendingWallCut);
                return true;
            }
            // A shared box (SbMRUKAnchorEntity, furniture a joiner installs): its region pulled, else placed by its footprint with
            // its region fitted around it, as a scene anchor's is.
            if (footprint != null && placed.GetComponent<SbMRUKAnchorEntity>() != null)
            {
                if (TryComputeRegionFit(region, walkable, 0, out Vector2 boxShift, null, footprint))
                {
                    placementRoot.position += new Vector3(boxShift.x, 0f, boxShift.y);
                    return true;
                }
                if (TryComputeRegionFit(footprint, walkable, 0, out Vector2 footShift, null, footprint))
                {
                    placementRoot.position += new Vector3(footShift.x, 0f, footShift.y);
                    MakeRoomFor(WorldFootprintRing(plan, frame.localToWorldMatrix * plan.FrameToObject), "shared box");
                    regionOverride = FitPhysicalRegion(plan, frame.localToWorldMatrix * plan.FrameToObject);
                    SpatialBlendDebug.LogWarning(LogCategory.Scene,
                        $"ShareGameObject: '{objectName}' with its full stand-off fits nowhere in this room: it is placed by its " +
                        "footprint and its region is fitted around it.");
                    return true;
                }
                SpatialBlendDebug.LogError(LogCategory.Scene, $"ShareGameObject: the box '{objectName}' fits nowhere in this room. It is shared without its region.");
                return false;
            }
            // Every virtual object: THE placement (RoomFit.Place, the one rule spawn, drop, arrival and room changes use): valid
            // where it is, it stays; a wall-mounted one slides along the walls, then stands on the open floor; any other one is
            // pulled, turned when no translation fits.
            Vector3 was = placementRoot.position;
            Quaternion wasTurned = placementRoot.rotation;
            if (!RoomFit.Place(placementRoot.gameObject, _roomRegion, this, null, RegionFitMarginMeters, null, walkable.Boundary,
                    out string reason, plan, frame))
            {
                SpatialBlendDebug.LogError(LogCategory.Scene,
                    $"ShareGameObject: the region of '{objectName}' fits nowhere in this room, clear of the walls, the furniture " +
                    "and the other regions. It is shared without its region.");
                return false;
            }
            if ((placementRoot.position - was).sqrMagnitude > 1e-10f || Quaternion.Angle(placementRoot.rotation, wasTurned) > 0.01f)
                SpatialBlendDebug.LogWarning(LogCategory.Scene,
                    $"ShareGameObject: moved '{objectName}' by ({placementRoot.position.x - was.x:0.###}, {placementRoot.position.z - was.z:0.###}) m: {reason}.");
            return true;
        }

        // Whether a placement motif is moving this entity (a grab or its release animation) and it has an authored region here.
        private bool IsInMotion(SbEntity entity)
        {
            if (entity == null || !TryGetAuthoredRegion(entity.ID, out long region) || region <= 0) return false;
            Interactions.PlacementMotif motif = entity.GetComponentInChildren<Interactions.PlacementMotif>();
            return motif != null && motif.InMotion;
        }

        internal static bool IsPhysicalCounterpart(GameObject sceneObject) =>
            sceneObject != null && sceneObject.GetComponentInParent<PhysicalCounterpart>() != null;

        /// <summary>
        /// Whether ShareGameObject attaches <paramref name="entity"/> as a local object: a scene anchor only on the element the
        /// MRUK adapter registered for it. Anything else (a virtual object) is attached with its own region.
        /// </summary>
        internal bool AdmitsLocalShare(SbEntity entity, bool sceneOwned, out string reason)
        {
            reason = null;
            if (entity is SbMRUKAnchorEntity && !sceneOwned)
            {
                reason = $"scene anchor {entity.gameObject.name} has no registered MRUK scene geometry; it is not shared until the adapter has registered it.";
                return false;
            }
            return true;
        }

        /// <summary>The walkable ring placements are made in: the one authoring is checked against (RoomFit.Ring).</summary>
        internal WalkableRegion PlacementWalkable => AuthoringWalkable;

        /// <summary>
        /// The obstacles a region under the room must stay clear of: every staged piece of furniture (at any depth: a box nested in
        /// another is inside it) and every sibling footprint under the room (<see cref="CollectSiblingFootprints"/>: the anchor boxes
        /// and the other regions, which the core refuses to overlap). The walls bound the object's footprint (<see cref="RoomWalls"/>).
        /// </summary>
        internal List<Vector2[]> RegionObstacles(long excludeObjectElement, IReadOnlyList<Vector2[]> extraObstacles = null)
        {
            var obstacles = new List<Vector2[]>();
            CollectFurnitureFootprints(obstacles, excludeObjectElement);
            CollectSiblingFootprints(obstacles, excludeObjectElement);
            if (extraObstacles != null)
            {
                for (int i = 0; i < extraObstacles.Count; ++i)
                    if (extraObstacles[i] != null && extraObstacles[i].Length >= 3) obstacles.Add(extraObstacles[i]);
            }
            return obstacles;
        }

        // Every staged piece of furniture's ground footprint (world), but the one whose region is fitted. A seam for the tests.
        protected virtual void CollectFurnitureFootprints(List<Vector2[]> into, long excludeObjectElement)
        {
            if (_sceneLayout == null) return;
            var all = new List<Vector2[]>();
            _sceneLayout.CollectFurnitureFootprints(all, SceneGeometryKeyOf(excludeObjectElement));
            into.AddRange(all);
        }

        /// <summary>
        /// The nearest translation that makes a placement valid (<see cref="RegionPull"/>): the region (world ground ring) inside the
        /// walkable ring and clear of <see cref="RegionObstacles"/>, and the object's <paramref name="footprint"/> clear of the
        /// room's walls, with <see cref="RegionFitMarginMeters"/> of clearance. <paramref name="excludeObjectElement"/> is the object
        /// whose own region is being fitted again. False when none does.
        /// </summary>
        private bool TryComputeRegionFit(Vector2[] region, WalkableRegion walkable, long excludeObjectElement, out Vector2 shift,
                                         IReadOnlyList<Vector2[]> extraObstacles = null, Vector2[] footprint = null)
        {
            var shape = new RegionPull.Shape
            {
                Region = region, RegionObstacles = RegionObstacles(excludeObjectElement, extraObstacles),
                Footprint = footprint, FootprintObstacles = RoomWalls,
            };
            return RegionPull.TryPull(shape, walkable.Boundary, RegionFitMarginMeters, out shift);
        }

        // Drops the rings whose bounding box is farther than `reach` from the polygon's.
        internal static void KeepNear(List<Vector2[]> rings, IReadOnlyList<Vector2> polygon, float reach)
        {
            Vector2 min = polygon[0], max = polygon[0];
            foreach (Vector2 p in polygon) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            min -= new Vector2(reach, reach);
            max += new Vector2(reach, reach);
            rings.RemoveAll(ring =>
            {
                if (ring == null || ring.Length == 0) return true;
                Vector2 rMin = ring[0], rMax = ring[0];
                foreach (Vector2 p in ring) { rMin = Vector2.Min(rMin, p); rMax = Vector2.Max(rMax, p); }
                return rMax.x < min.x || rMin.x > max.x || rMax.y < min.y || rMin.y > max.y;
            });
        }

        /// <summary>
        /// The world ground footprints that are siblings of a region under the room: the observed anchors and content this
        /// space's layout hangs there, and every authored region registered under the room. A seam for the tests.
        /// </summary>
        protected virtual void CollectSiblingFootprints(List<Vector2[]> into, long excludeObjectElement)
        {
            long excludeRegion = excludeObjectElement > 0 &&
                                 _authoredStateByObject.TryGetValue(excludeObjectElement, out AuthoredObjectState own)
                ? own.RegionElement
                : 0;
            // A scene anchor's own box is not its region's sibling: the region holds it.
            _sceneLayout?.CollectRoomFootprints(into, excludeRegion, SceneGeometryKeyOf(excludeObjectElement));
            foreach (AuthoredObjectState state in StatesInIdOrder())
            {
                if (state.ObjectElement == excludeObjectElement || state.RegionElement <= 0 ||
                    state.RegionParentWorldInverse != Matrix4x4.identity) continue;
                Vector2[] ring = CurrentRegionRing(state);
                if (ring != null) into.Add(ring);
            }
        }

        // A declaration that does not compile must not silently turn the object into an unauthored one.
        private static bool TryPlanAuthoring(SbSpatialAuthoring authoring, out SpatialAuthoringPlan plan)
        {
            plan = null;
            if (authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error) &&
                SpatialAuthoringCompiler.TryCompile(declaration, out plan, out error))
            {
                return true;
            }
            SpatialBlendDebug.LogError(LogCategory.Scene,
                $"SbSpatialAuthoring on '{authoring.gameObject.name}' does not compile: {error}");
            plan = null;
            return false;
        }

        // A linked local object already carries the region and the affordances it was authored with.
        // Link them to the source's, using the source's object shared id.
        private bool TryLinkReceivedAuthoring(SbEntity existing, long sourceSharedId)
        {
            if (existing == null || !existing.IsAttached() || sourceSharedId == 0) return false;
            Transform pose = existing.EntityTransform != null ? existing.EntityTransform : existing.transform;
            return TryLinkSpatialAuthoring(existing.ID, sourceSharedId, pose.localToWorldMatrix, out _);
        }

        // The scene-geometry adapter's elements for a scene anchor entity: the Obstacle parent
        // is the object, and its Surface plane child is the Tabletop affordance when the anchor
        // has a measured plane.
        private bool TryGetSceneOwnedElements(SbEntity entity, out long parent,
                                              out SpatialAuthoringExistingElements elements)
        {
            parent = 0;
            elements = null;
            if (!(entity is SbMRUKAnchorEntity anchor) ||
                !TryGetLocalSceneGeometryElements(anchor.SceneGeometryKey, out parent, out long plane))
            {
                return false;
            }
            // A seat's part lives in the frame of the adapter's plane element: with no plane there is no seat (never a baked surface in another frame).
            if (anchor.IsSeat && plane <= 0)
            {
                elements = null;
                return false;
            }
            elements = new SpatialAuthoringExistingElements { SourceKey = anchor.SceneGeometryKey };
            if (plane > 0)
            {
                elements.Affordances[anchor.PlaneAffordanceId] = plane;
            }
            return true;
        }

        private void CompleteShare(SpaceID space, EntityID entity_id, GameObject gameObject,
                                   SbEntity entity, SerializationData serializationData,
                                   long receipt, long generation,
                                   long cloneAttachmentVersion, long sharedId)
        {
            if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
            {
                DiscardUnattachedReconstructedClone(gameObject, entity, cloneAttachmentVersion);
                return;
            }
            var representativeApp = GetRepresentativeApp();

            if (representativeApp == null)
            {
                DiscardUnattachedReconstructedClone(gameObject, entity, cloneAttachmentVersion);
                return;
            }

            for (int i = 0; i < serializationData.GetNumBlocks(); i++)
            {
                entity.serializationBlocks[i].deserialize(serializationData.GetBlockData(i), serializationData.GetBlockLength(i));
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
                {
                    DiscardUnattachedReconstructedClone(gameObject, entity,
                                                        cloneAttachmentVersion);
                    return;
                }
            }

            var response = representativeApp.OnShare(gameObject);
            if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
            {
                DiscardUnattachedReconstructedClone(gameObject, entity, cloneAttachmentVersion);
                return;
            }
            SbTrace.Line($"[V2Share] CompleteShare manager='{name}' elem={entity_id} go='{(gameObject == null ? "<null>" : gameObject.name)}' -> OnShare decision={response.result}");

            if (response.result == OnShareResponse.Result.AddAsNew)
            {
                entity.Attach(this, entity_id, false);
                TrackReconstructedNativeReplica(entity, entity_id);
                long registrationToken = GetEntityRegistrationToken(entity, entity_id);
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation) ||
                    !IsEntityRegistrationCurrent(entity, entity_id, registrationToken))
                {
                    return;
                }
                representativeApp.OnObjectAdded(entity.gameObject, SbApp.OnObjectAddedType.Added);
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation) ||
                    !IsEntityRegistrationCurrent(entity, entity_id, registrationToken))
                {
                    return;
                }
                if (!TryApplyReceivedAuthoring(entity, entity_id, sharedId))
                {
                    SpatialBlendAPI.SetElementTransform(_spaceID, entity_id, entity.EntityTransform.localToWorldMatrix);
                }
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation) ||
                    !IsEntityRegistrationCurrent(entity, entity_id, registrationToken))
                {
                    return;
                }
                // AddAsNew creates an ordinary referent unless it is explicitly an
                // avatar. Apps may still opt into actor semantics via RegisterAnchor
                // or RegisterRelation after the object is registered.
                EnsureV2ActorForAvatar(entity.gameObject, entity_id);
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation) ||
                    !IsEntityRegistrationCurrent(entity, entity_id, registrationToken))
                {
                    return;
                }
                // Resolve: the received temp element IS the canonical element here.
                // Records the source->this link in the ElementLinker (keeps the element).
                SpatialBlendAPI.ResolveSharedElement(_spaceID, entity_id, entity_id);
            }
            else if (response.result == OnShareResponse.Result.LinkToExisting)
            {
                // The local object already has its own native element (e.g. furniture
                // registered in OnAppStart). KEEP it; the received element is a duplicate
                // temp. Resolve to the existing local element id, which makes the core
                // REMOVE the temp (no duplicate shared id) and record the cross-space
                // link. The reconstructed clone GameObject is discarded.
                SbEntity existing = FindAttachedLinkTarget(response.gameObject, gameObject);
                if (existing == null)
                {
                    string targetName = response.gameObject == null ? "<null>" : response.gameObject.name;
                    SpatialBlendDebug.LogError(LogCategory.Scene,
                        $"[V2Share] manager='{name}' LinkToExisting('{targetName}') requires an attached local " +
                        "SbEntity owned by this manager; refusing an implicit AddAsNew fallback.");
                    DiscardReconstructedShareClone(gameObject, entity, cloneAttachmentVersion);
                    return;
                }

                // `gameObject` is the just-reconstructed clone. Never destroy the
                // application-selected local/Fusion target in response.gameObject.
                DiscardReconstructedShareClone(gameObject, entity, cloneAttachmentVersion);
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
                {
                    return;
                }
                SpatialBlendAPI.ResolveSharedElement(_spaceID, entity_id, existing.ID);
                // The object link comes first. Resolving the shared region afterwards removes only
                // that region element, never the received object that was just resolved.
                if (!TryLinkReceivedAuthoring(existing, sharedId))
                {
                    SpatialBlendDebug.LogWarning(LogCategory.Scene,
                        $"[V2Share] LinkToExisting('{existing.gameObject.name}') has no authored region. " +
                        "Its SbSpatialAuthoring must be applied when the local object is registered.");
                }
                representativeApp.OnObjectAdded(response.gameObject, SbApp.OnObjectAddedType.Linked);
                if (!IsCurrentInboundShare(space, entity_id, receipt, generation))
                {
                    return;
                }
                // LinkToExisting targets a pre-existing local object (e.g. furniture) →
                // only make it an actor if it is actually a tagged avatar.
                EnsureV2ActorForAvatar(response.gameObject, existing.ID);
            }
            else if (response.result == OnShareResponse.Result.Defer)
            {
                // The app answers later. The temp element stays unresolved and the replica is kept aside, unattached, until the
                // app completes the link (CompleteDeferredShare) or drops it. Duplicate shared ids are allowed in the core (smallest element
                // id holds the lookup), so a local seat registered in the meantime does not collide with the temp.
                DeferShare(space, entity_id, gameObject, entity, cloneAttachmentVersion, sharedId, generation);
            }
            else
            {
                // Reject keeps the receiver's own objects. The reconstructed clone
                // is dropped and the temp element stays unresolved until unshare.
                DiscardReconstructedShareClone(gameObject, entity, cloneAttachmentVersion);
            }
        }

        private sealed class DeferredShare
        {
            internal SpaceID Space;
            internal EntityID EntityId;
            internal GameObject Replica;
            internal SbEntity Entity;
            internal long CloneVersion;
            internal long SharedId;
            internal long Generation;
        }

        private readonly Dictionary<GameObject, DeferredShare> _deferredShares = new Dictionary<GameObject, DeferredShare>();

        private void DeferShare(SpaceID space, EntityID entityId, GameObject replica, SbEntity entity, long cloneVersion, long sharedId, long generation)
        {
            if (replica == null) return;
            _deferredShares[replica] = new DeferredShare
            {
                Space = space, EntityId = entityId, Replica = replica, Entity = entity, CloneVersion = cloneVersion, SharedId = sharedId,
                Generation = generation,
            };
        }

        /// <summary>The shares the app deferred and has neither completed nor dropped.</summary>
        public int DeferredShareCount => _deferredShares.Count;

        /// <summary>
        /// Completes a share the app answered with <see cref="OnShareResponse.Defer"/>: the received element is resolved onto <paramref name="local"/>
        /// (an attached local entity of this manager), exactly as <see cref="OnShareResponse.LinkToExisting"/> would have, and the app is told
        /// (<see cref="SbApp.OnObjectAdded"/>, Linked). False when the share is gone (the connection changed, the source unshared it) or the
        /// local object is not attached yet; in the second case the deferral stays.
        /// </summary>
        public bool CompleteDeferredShare(GameObject replica, GameObject local)
        {
            if (replica == null || !_deferredShares.TryGetValue(replica, out DeferredShare deferred)) return false;
            if (deferred.Space != _spaceID || !IsCurrentLifecycleGeneration(deferred.Generation) || WasRemovedAtOrAfter(deferred.EntityId, 0))
            {
                _deferredShares.Remove(replica);
                DiscardReconstructedShareClone(replica, deferred.Entity, deferred.CloneVersion);
                return false;
            }
            SbEntity existing = FindAttachedLinkTarget(local, replica);
            var representativeApp = GetRepresentativeApp();
            if (existing == null || representativeApp == null) return false;
            _deferredShares.Remove(replica);
            DiscardReconstructedShareClone(replica, deferred.Entity, deferred.CloneVersion);
            SpatialBlendAPI.ResolveSharedElement(_spaceID, deferred.EntityId, existing.ID);
            if (!TryLinkReceivedAuthoring(existing, deferred.SharedId))
            {
                SpatialBlendDebug.LogWarning(LogCategory.Scene,
                    $"[V2Share] deferred link of '{existing.gameObject.name}' has no authored region. Its SbSpatialAuthoring must be applied when the local object is registered.");
            }
            representativeApp.OnObjectAdded(local, SbApp.OnObjectAddedType.Linked);
            EnsureV2ActorForAvatar(local, existing.ID);
            return true;
        }

        /// <summary>
        /// Completes a share the app answered with <see cref="OnShareResponse.Defer"/> as a new object, exactly as
        /// <see cref="OnShareResponse.AddAsNew"/> would have: the replica is attached to the received element, the app is told
        /// (<see cref="SbApp.OnObjectAdded"/>, Added), and its authoring is applied. False when the share is gone.
        /// </summary>
        public bool CompleteDeferredShareAsNew(GameObject replica)
        {
            if (replica == null || !_deferredShares.TryGetValue(replica, out DeferredShare deferred)) return false;
            _deferredShares.Remove(replica);
            var representativeApp = GetRepresentativeApp();
            if (deferred.Space != _spaceID || !IsCurrentLifecycleGeneration(deferred.Generation) ||
                WasRemovedAtOrAfter(deferred.EntityId, 0) || deferred.Entity == null || representativeApp == null)
            {
                DiscardReconstructedShareClone(replica, deferred.Entity, deferred.CloneVersion);
                return false;
            }
            SbEntity entity = deferred.Entity;
            long entityId = deferred.EntityId;
            entity.Attach(this, entityId, false);
            TrackReconstructedNativeReplica(entity, entityId);
            long registrationToken = GetEntityRegistrationToken(entity, entityId);
            representativeApp.OnObjectAdded(entity.gameObject, SbApp.OnObjectAddedType.Added);
            if (!IsEntityRegistrationCurrent(entity, entityId, registrationToken)) return true;
            if (!TryApplyReceivedAuthoring(entity, entityId, deferred.SharedId))
            {
                SpatialBlendAPI.SetElementTransform(_spaceID, entityId, entity.EntityTransform.localToWorldMatrix);
            }
            if (!IsEntityRegistrationCurrent(entity, entityId, registrationToken)) return true;
            EnsureV2ActorForAvatar(entity.gameObject, entityId);
            SpatialBlendAPI.ResolveSharedElement(_spaceID, entityId, entityId);
            return true;
        }

        /// <summary>True while the share of this replica waits for the app's answer.</summary>
        public bool IsShareDeferred(GameObject replica) => replica != null && _deferredShares.ContainsKey(replica);

        // The source unshared an element whose share is still deferred: nothing is attached for it, so the removal would pass unnoticed.
        private void DropDeferredSharesOf(EntityID entityId)
        {
            if (_deferredShares.Count == 0) return;
            var gone = new List<GameObject>();
            foreach (KeyValuePair<GameObject, DeferredShare> pair in _deferredShares)
            {
                if (pair.Value.EntityId == entityId) gone.Add(pair.Key);
            }
            foreach (GameObject replica in gone) DropDeferredShare(replica);
        }

        /// <summary>Gives up a deferred share: the replica is discarded (the received element stays unresolved until the source unshares it).</summary>
        public void DropDeferredShare(GameObject replica)
        {
            if (replica == null || !_deferredShares.TryGetValue(replica, out DeferredShare deferred)) return;
            _deferredShares.Remove(replica);
            DiscardReconstructedShareClone(replica, deferred.Entity, deferred.CloneVersion);
        }

        [MonoPInvokeCallback(typeof(OnRemoveDelegate))]
        private static void OnRemoveInternalStatic(SpaceID space_id, EntityID entity_id)
        {
            if (SpatialBlendRegistry.TryGetManager(space_id, out var manager))
            {
                manager.OnRemoveInternal(entity_id);
                return;
            }
            SpatialBlendDebug.LogWarning(LogCategory.Connection, $"OnRemoveInternalStatic: No manager found for space {space_id}");
        }

        private void OnRemoveInternal(EntityID entity_id)
        {
            QueueElementRemoval(entity_id, Interlocked.Read(ref _lifecycleGeneration));
        }

        private void QueueElementRemoval(EntityID entityId, long callbackGeneration)
        {
            if (!IsCurrentLifecycleGeneration(callbackGeneration))
            {
                return;
            }

            SbEntity entity = null;
            long registrationToken = 0;
            lock (_entitiesLock)
            {
                if (_removedElementReceipts.ContainsKey(entityId))
                {
                    return;
                }
                foreach (SbEntity candidate in _entities)
                {
                    if (_entityRegistrations.TryGetValue(candidate,
                        out EntityRegistration registration) && registration.Id == entityId)
                    {
                        entity = candidate;
                        registrationToken = registration.Token;
                        break;
                    }
                }

                // Retain the receipt tombstone for this registration. A duplicate
                // old callback cannot later select a same-id reattachment, while a
                // genuinely new attachment receives a new token and remains eligible.
                if (registrationToken != 0 &&
                    !_queuedRemovalRegistrationTokens.Add(registrationToken))
                {
                    return;
                }
            }
            long removalReceipt = Interlocked.Increment(ref _eventReceiptSequence);
            PushMainThreadTask(() => ProcessElementRemoval(entityId, entity,
                registrationToken, removalReceipt, callbackGeneration), callbackGeneration,
                removalReceipt);
        }

        // Main-thread removal. App notification intentionally precedes the entity
        // callback/detach; neither runs under _entitiesLock because either may
        // destroy/re-register objects re-entrantly.
        private void ProcessElementRemoval(EntityID entityId, SbEntity entity,
                                           long removalToken, long removalReceipt,
                                           long callbackGeneration)
        {
            if (!IsCurrentLifecycleGeneration(callbackGeneration))
            {
                return;
            }

            lock (_entitiesLock)
            {
                if (_removedElementReceipts.ContainsKey(entityId))
                {
                    return;
                }
                _removedElementReceipts[entityId] = removalReceipt;
                _pendingShares.RemoveAll(pending => pending.entityId == entityId &&
                    pending.receipt <= removalReceipt);

                // Added and removed can both arrive before the main-thread pump.
                // Resolve only an attachment whose queued ingress predates this
                // removal receipt; a later manual same-id reattach is protected.
                if (entity == null)
                {
                    foreach (SbEntity candidate in _entities)
                    {
                        if (_entityRegistrations.TryGetValue(candidate,
                            out EntityRegistration registration) && registration.Id == entityId &&
                            registration.IngressReceipt < removalReceipt)
                        {
                            entity = candidate;
                            removalToken = registration.Token;
                            break;
                        }
                    }
                }
            }

            // A no-entity removal still retires pending reconstruction. For an
            // attached entity, only the registration captured at callback receipt
            // may receive app/entity notifications.
            if (entity == null || !IsEntityRegistrationCurrent(entity, entityId, removalToken))
            {
                DropDeferredSharesOf(entityId);
                ForgetOwnedNativeReplica(entity, removalToken);
                return;
            }

            SbApp app = GetRepresentativeApp();
            if (app != null && IsEntityRegistrationCurrent(entity, entityId, removalToken))
            {
                app.OnRemove(entity.gameObject);
            }

            // Each external callback can detach/reattach this same object. Never
            // invoke an old registration's callback or Detach on its replacement.
            if (!IsEntityRegistrationCurrent(entity, entityId, removalToken))
            {
                ForgetOwnedNativeReplica(entity, removalToken);
                return;
            }
            entity.OnRemove();
            if (IsEntityRegistrationCurrent(entity, entityId, removalToken))
            {
                entity.Detach();
                RetireOwnedNativeReplica(entity, entityId, removalToken, callbackGeneration);
            }
        }

        private SbEntity FindAttachedLinkTarget(GameObject target, GameObject reconstructedClone)
        {
            if (target == null)
            {
                return null;
            }
            // SyncRemote returns the scene object itself. When this room already
            // attached it, the link target is that object, not a separate clone.
            if (target == reconstructedClone)
            {
                foreach (SbEntity same in target.GetComponents<SbEntity>())
                {
                    if (same is SbNetworkEntity && same.IsAttached() && same.spaceManager == this)
                    {
                        return same;
                    }
                }
                return null;
            }
            foreach (SbEntity candidate in target.GetComponents<SbEntity>())
            {
                if (candidate != null && candidate.IsAttached() && candidate.spaceManager == this)
                {
                    return candidate;
                }
            }
            return null;
        }

        // Called only after a successful AddAsNew Attach. This boundary is the
        // provenance proof that allows later destruction; IsRemoteReplica alone
        // is intentionally insufficient.
        private void TrackReconstructedNativeReplica(SbEntity entity, EntityID entityId)
        {
            if (!(entity is SbNativeEntity nativeReplica) || !nativeReplica.IsRemoteReplica)
            {
                return;
            }
            long generation = Interlocked.Read(ref _lifecycleGeneration);
            long token = GetEntityRegistrationToken(nativeReplica, entityId);
            if (token == 0 || !IsCurrentLifecycleGeneration(generation))
            {
                return;
            }

            RetireSupersededNativeReplicas(entityId, generation, nativeReplica);
            lock (_entitiesLock)
            {
                if (IsEntityRegistrationCurrent(nativeReplica, entityId, token))
                {
                    _reconstructedNativeReplicas[nativeReplica] = new ReconstructedNativeReplica
                    {
                        Entity = nativeReplica,
                        Id = entityId,
                        RegistrationToken = token,
                        AttachmentVersion = nativeReplica.AttachmentVersion,
                        Generation = generation,
                    };
                }
            }
        }

        private void RetireSupersededNativeReplicas(EntityID entityId, long generation,
                                                    SbNativeEntity replacement)
        {
            var superseded = new List<ReconstructedNativeReplica>();
            lock (_entitiesLock)
            {
                var candidates = new List<ReconstructedNativeReplica>(
                    _reconstructedNativeReplicas.Values);
                foreach (ReconstructedNativeReplica candidate in candidates)
                {
                    if (candidate.Entity != replacement && candidate.Id == entityId &&
                        candidate.Generation == generation)
                    {
                        superseded.Add(candidate);
                        _reconstructedNativeReplicas.Remove(candidate.Entity);
                    }
                }
            }
            foreach (ReconstructedNativeReplica candidate in superseded)
            {
                if (candidate.Entity != null &&
                    IsEntityRegistrationCurrent(candidate.Entity, candidate.Id,
                                                candidate.RegistrationToken) &&
                    candidate.Entity.AttachmentVersion == candidate.AttachmentVersion)
                {
                    candidate.Entity.Detach();
                }
                if (candidate.Entity != null && !candidate.Entity.IsAttached() &&
                    candidate.Entity.AttachmentVersion == candidate.AttachmentVersion)
                {
                    candidate.Entity.RetireReconstructedReplica();
                    DestroyOwnedReconstructedNativeReplica(candidate.Entity.gameObject);
                }
            }
        }

        private void ForgetOwnedNativeReplica(SbEntity entity, long registrationToken)
        {
            if (!(entity is SbNativeEntity nativeReplica)) return;
            lock (_entitiesLock)
            {
                if (_reconstructedNativeReplicas.TryGetValue(nativeReplica,
                    out ReconstructedNativeReplica record) &&
                    record.RegistrationToken == registrationToken)
                {
                    _reconstructedNativeReplicas.Remove(nativeReplica);
                }
            }
        }

        private void RetireOwnedNativeReplica(SbEntity entity, EntityID entityId,
                                              long registrationToken, long generation)
        {
            if (!(entity is SbNativeEntity nativeReplica)) return;
            bool owned = false;
            long attachmentVersion = -1;
            lock (_entitiesLock)
            {
                if (_reconstructedNativeReplicas.TryGetValue(nativeReplica,
                    out ReconstructedNativeReplica record) && record.Id == entityId &&
                    record.RegistrationToken == registrationToken &&
                    (generation == 0 || record.Generation == generation))
                {
                    _reconstructedNativeReplicas.Remove(nativeReplica);
                    owned = true;
                    attachmentVersion = record.AttachmentVersion;
                }
            }
            if (!owned || nativeReplica == null || nativeReplica.IsAttached() ||
                nativeReplica.AttachmentVersion != attachmentVersion) return;
            nativeReplica.RetireReconstructedReplica();
            DestroyOwnedReconstructedNativeReplica(nativeReplica.gameObject);
        }

        private void DiscardReconstructedShareClone(GameObject reconstructedClone, SbEntity entity,
                                                     long attachmentVersion)
        {
            DiscardUnattachedReconstructedClone(reconstructedClone, entity, attachmentVersion);
            if (entity is SbMRUKAnchorEntity anchor &&
                anchor.gameObject == reconstructedClone && !anchor.IsAttached() &&
                anchor.AttachmentVersion == attachmentVersion)
            {
                if (Application.isPlaying) Destroy(reconstructedClone);
                else DestroyImmediate(reconstructedClone);
            }
        }

        private void DiscardUnattachedReconstructedClone(GameObject reconstructedClone,
                                                          SbEntity entity,
                                                          long attachmentVersion = 0)
        {
            if (!(entity is SbNativeEntity nativeReplica) || !nativeReplica.IsRemoteReplica ||
                nativeReplica.gameObject != reconstructedClone || nativeReplica.IsAttached() ||
                nativeReplica.AttachmentVersion != attachmentVersion)
            {
                return;
            }
            nativeReplica.RetireReconstructedReplica();
            DestroyOwnedReconstructedNativeReplica(reconstructedClone);
        }

        protected virtual void DestroyOwnedReconstructedNativeReplica(GameObject replica)
        {
            if (replica != null) Destroy(replica);
        }

        private bool IsEntityRegistrationCurrent(SbEntity entity, EntityID entityId,
                                                 long registrationToken)
        {
            lock (_entitiesLock)
            {
                return entity != null && _entityRegistrations.TryGetValue(entity,
                    out EntityRegistration registration) && registration.Id == entityId &&
                    registration.Token == registrationToken;
            }
        }

        private bool WasRemovedAtOrAfter(EntityID entityId, long receipt)
        {
            lock (_entitiesLock)
            {
                return _removedElementReceipts.TryGetValue(entityId,
                    out long removalReceipt) && removalReceipt >= receipt;
            }
        }

        private bool IsCurrentInboundShare(SpaceID space, EntityID entityId, long receipt,
                                           long generation)
        {
            return space == _spaceID && IsCurrentLifecycleGeneration(generation) &&
                   !WasRemovedAtOrAfter(entityId, receipt);
        }

        [MonoPInvokeCallback(typeof(OnUpdateStateDelegate))]
        private static void OnUpdateStateInternalStatic(SpaceID space_id, EntityID entity_id, ProximityType origin_proximity_type, StateType state_type, [In] double[] state_arr, ulong state_arr_size)
        {
            if (SpatialBlendRegistry.TryGetManager(space_id, out var manager))
            {
                manager.OnUpdateStateInternal(entity_id, origin_proximity_type, state_type, state_arr, state_arr_size);
                return;
            }
            SpatialBlendDebug.LogWarning(LogCategory.Connection, $"OnUpdateStateInternalStatic: No manager found for space {space_id}");
        }

        [MonoPInvokeCallback(typeof(OnUpdateStateBatchDelegate))]
        private static void OnUpdateStateBatchInternalStatic(SpaceID space_id, EntityID entity_id, ProximityType origin_proximity_type, IReadOnlyList<StateUpdateBatchItem> updates)
        {
            if (SpatialBlendRegistry.TryGetManager(space_id, out var manager))
            {
                manager.OnUpdateStateBatchInternal(entity_id, origin_proximity_type, updates);
                return;
            }
            SpatialBlendDebug.LogWarning(LogCategory.Connection, $"OnUpdateStateBatchInternalStatic: No manager found for space {space_id}");
        }

        private void OnUpdateStateInternal(EntityID entity_id, ProximityType origin_proximity_type, StateType state_type, [In] double[] state_arr, ulong state_arr_size)
        {
            // state_arr is already in Unity coordinates.
            PushMainThreadTask(() =>
            {
                lock (_entitiesLock)
                {
                    SbEntity entity = GetEntity(entity_id);
                    if (entity != null)
                    {
                        entity.OnUpdateState(origin_proximity_type, state_type, state_arr, state_arr_size);
                    }
                }
            });
        }

        private void OnUpdateStateBatchInternal(EntityID entity_id, ProximityType origin_proximity_type, IReadOnlyList<StateUpdateBatchItem> updates)
        {
            if (updates == null)
            {
                return;
            }

            PushMainThreadTask(() =>
            {
                lock (_entitiesLock)
                {
                    SbEntity entity = GetEntity(entity_id);
                    if (entity != null)
                    {
                        entity.OnUpdateStateBatch(origin_proximity_type, updates);
                    }
                }
            });
        }

        [MonoPInvokeCallback(typeof(OnUpdateDataDelegate))]
        private static void OnUpdateDataInternalStatic(SpaceID space_id, EntityID entity_id, IntPtr user_data, ulong user_data_size)
        {
            if (SpatialBlendRegistry.TryGetManager(space_id, out var manager))
            {
                manager.OnUpdateDataInternal(entity_id, user_data, user_data_size);
                return;
            }
            SpatialBlendDebug.LogWarning(LogCategory.Connection, $"OnUpdateDataInternalStatic: No manager found for space {space_id}");
        }

        private void OnUpdateDataInternal(EntityID entity_id, IntPtr user_data, ulong user_data_size)
        {
            PushMainThreadTask(() =>
            {
                lock (_entitiesLock)
                {
                    SbEntity entity = GetEntity(entity_id);
                    if (entity != null)
                    {
                        entity.OnUpdateData(user_data, user_data_size);
                    }
                }
            });
        }

        [MonoPInvokeCallback(typeof(OnInteractionDelegate))]
        private static void OnInteractionInternalStatic(SpaceID space_id, InteractionEventType event_type, EntityID from_entity_id, EntityID to_entity_id, ProximityType proximity_type, ulong interaction_time)
        {
            if (SpatialBlendRegistry.TryGetManager(space_id, out var manager))
            {
                manager.OnInteractionInternal(event_type, from_entity_id, to_entity_id, proximity_type, interaction_time);
                return;
            }
            SpatialBlendDebug.LogWarning(LogCategory.Connection, $"OnInteractionInternalStatic: No manager found for space {space_id}");
        }

        private void OnInteractionInternal(InteractionEventType event_type, EntityID from_entity_id, EntityID to_entity_id, ProximityType proximity_type, ulong interaction_time)
        {
            PushMainThreadTask(() =>
            {
                lock (_entitiesLock)
                {
                    SbEntity entity = GetEntity(from_entity_id);
                    if (entity != null)
                    {
                        entity.OnInteraction(event_type, to_entity_id, proximity_type, interaction_time);
                    }
                }
            });
        }

        /// <summary>
        /// A generic app's NavMesh as its room: the NavMesh's outer ring is the room's Navigable polygon, with no holes and no
        /// walls (the Freeform app registers its MRUK room instead, <see cref="RegisterRoomRegion"/>).
        /// </summary>
        internal void RegisterWalkable(NavMeshSurface walkableNavMesh, double walkableGeometryResolution)
        {
            if (walkableNavMesh == null || !walkableNavMesh.enabled)
            {
                SpatialBlendDebug.Log(LogCategory.Geometry, "NavMeshSurface is null or disabled");
                return;
            }
            if (!NavMeshSurfaceWalkable.TryRead(walkableNavMesh, out WalkableRegion region, out _))
            {
                SpatialBlendDebug.LogError(LogCategory.Geometry, "Failed to get NavMesh triangulation data");
                return;
            }
            RegisterRoomRegion(new RoomRegion(region.Boundary, region.Boundary, null, null, 0f));
        }

        /// <summary>A triangulation's outer ring as the room's Navigable polygon (see <see cref="RegisterWalkable"/>).</summary>
        public bool RegisterWalkableMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices)
        {
            if (!NavMeshWalkableExtractor.TryExtract(vertices, indices, out WalkableRegion region))
                return false;
            return RegisterRoomRegion(new RoomRegion(region.Boundary, region.Boundary, null, null, 0f));
        }

        private RoomRegion _roomRegion;
        private readonly List<ElementID> _wallElementIDs = new List<ElementID>();
        private bool _localSceneGeometryRegistered;

        /// <summary>The room last registered with the core (<see cref="RegisterRoomRegion"/>); null before.</summary>
        public RoomRegion RegisteredRoom => _roomRegion;

        /// <summary>
        /// True when the app also registers the room's scene geometry (the MRUK adapter): authored objects are then fitted only
        /// once it is in, so their regions are pulled clear of the furniture. The app calls <see cref="MarkLocalSceneGeometryRegistered"/>.
        /// </summary>
        public bool WaitsForLocalSceneGeometry { get; set; }

        /// <summary>The scene geometry the app had is registered (or there is none): waiting authored objects are fitted now.</summary>
        public void MarkLocalSceneGeometryRegistered()
        {
            if (_localSceneGeometryRegistered) return;
            _localSceneGeometryRegistered = true;
            if (AuthoringInputsReady) ResumeAuthoringAwaitingWalkable();
        }

        // Authored regions are fitted against the room's registered ring and its furniture: both must be in.
        private bool AuthoringInputsReady =>
            _registeredWalkable != null && (!WaitsForLocalSceneGeometry || _localSceneGeometryRegistered);

        /// <summary>The room's Navigable ring last registered with the core (no holes). Null until a room is registered.</summary>
        public WalkableRegion RegisteredWalkable => _registeredWalkable;

        /// <summary>
        /// An actor is placed under the room element, the navigable root of the networked tree. That root has footprint
        /// geometry only once the room is registered, so an actor shared before it would hang under a root with none.
        /// It is refused, visibly, until the room is registered; the app shares it again after.
        /// </summary>
        internal bool TryAdmitActorShare(SbEntity entity, out string error)
        {
            error = null;
            if (!(entity is SbNetworkAvatarEntity) || _registeredWalkable != null) return true;
            error = $"actor '{entity.name}' was not shared: the room (its navigable root) has no footprint yet. " +
                    "Register the room first.";
            return false;
        }

        /// <summary>
        /// Registers the room (the "simple room"): <see cref="RoomRegion.Ring"/>, the scanned room enlarged, is the
        /// room element's Navigable polygon, and each wall is an Obstacle element (<see cref="RoomRegion.Walls"/>), so the core's free
        /// space excludes the walls as it excludes the furniture boxes the MRUK adapter stages. The element tree:
        /// <code>
        /// room (Navigable: the ring)
        ///   wall group i (no footprint)       one per wall: walls overlap at the corners, and a footprint-less parent
        ///     wall i (Obstacle)                 is never checked against its siblings, so no wall is a sibling of another
        ///   furniture box (Obstacle)          the adapter's, clipped or nested so that no two overlap (SceneFootprintLayout)
        ///   anchor region (Containment)       a shared anchor's region holds its box; inside the ring by construction
        ///     furniture box
        ///   object region (Containment)       a virtual object's, pulled clear of every obstacle and sibling region
        ///     object
        ///   actors
        /// </code>
        /// Called once when the room loads (a new room replaces it). False when the core refused the ring.
        /// </summary>
        public bool RegisterRoomRegion(RoomRegion room)
        {
            if (room == null || !room.IsValid) return false;
            ElementID roomElement = EnsureWalkableRoom();
            if (roomElement <= 0) return false;
            var walkable = room.Walkable;
            RoomRegion previous = _roomRegion;
            _roomRegion = room;
            if (_registeredWalkable != null)
                RefitRegionsToWalkable(walkable);
            if (!SetWalkablePolygon(roomElement, (int)SbGeometryRole.Navigable, room.Ring))
            {
                _roomRegion = previous;
                SpatialBlendDebug.LogError(LogCategory.Scene,
                    $"The room's region was not registered ({SpatialHierarchySceneError(_spaceID) ?? "the core refused it"}). " +
                    (_registeredWalkable != null ? "The previous outline stays registered." : "Authored objects wait for one."));
                return false;
            }
            ClearWallElements();
            foreach (Vector2[] wall in room.Walls)
            {
                if (wall == null || wall.Length < 3) continue;
                ElementID group = AddWallElement(roomElement);
                if (group <= 0) continue;
                _wallElementIDs.Add(group);
                _wallShapes.Add(wall);
                _wallPieceIDs.Add(new List<ElementID>());
                _wallPieces.Add(null);
            }
            // Each wall as it stands, cut where a physical object crosses its line (an own scan drawn over the wall).
            RetrimWalls();
            _registeredWalkable = walkable;
            SpatialBlendDebug.Log(LogCategory.Geometry,
                $"Registered the room: a ring of {room.Ring.Length} points and {room.Walls.Length} walls for space {_spaceID}");
            if (AuthoringInputsReady) ResumeAuthoringAwaitingWalkable();
            return true;
        }

        private void ClearWallElements()
        {
            // A group's removal takes its subtree (sb_remove_local_element): the pieces go with it.
            _wallShapes.Clear();
            _wallPieceIDs.Clear();
            _wallPieces.Clear();
            for (int i = 0; i < _wallElementIDs.Count; ++i)
            {
                if (!RemoveWallElement(_wallElementIDs[i]))
                    SpatialBlendDebug.LogError(LogCategory.Geometry, $"Failed to remove wall {_wallElementIDs[i]} in space {_spaceID}.");
            }
            _wallElementIDs.Clear();
        }

        // ---------------------------------------------------------------- walls cut by physical objects

        // Per registered wall (its footprint-less group in _wallElementIDs): its full obstacle, the Obstacle pieces it is
        // registered as now, and their elements.
        private readonly List<Vector2[]> _wallShapes = new List<Vector2[]>();
        private readonly List<Vector2[][]> _wallPieces = new List<Vector2[][]>();
        private readonly List<List<ElementID>> _wallPieceIDs = new List<List<ElementID>>();
        // What each physical object takes out of the walls: the convex hull of its footprint and its region (world ground).
        private readonly Dictionary<long, Vector2[]> _wallCutsByObject = new Dictionary<long, Vector2[]>();
        // The cut of an own scan being shared now, before its element exists (TryFitAuthoredObjectIntoWalkable -> ShareGameObject).
        private Vector2[] _pendingWallCut;
        // The cuts of objects removed here whose region element the core still holds (keyed by that region): the wall stays open
        // over it until it is gone, or the wall would overlap it.
        private readonly Dictionary<long, Vector2[]> _lingeringWallCuts = new Dictionary<long, Vector2[]>();
        private bool _wallsDirty;

        /// <summary>The Obstacle pieces each wall is registered as now (a wall no physical object crosses is one piece).</summary>
        internal IReadOnlyList<Vector2[][]> WallPieces => _wallPieces;

        /// <summary>
        /// A wall is an Obstacle reaching <see cref="RoomRegion.WallDepthMeters"/> behind its line, so a physical object drawn over
        /// the wall (an own scan: if it pokes outside, the core enlarges the room) would extend the room through it,
        /// which the core refuses (an extended region may not overlap a footprint outside its line, containment_layout.cc). The
        /// object is never moved: the wall gives way. Each wall is registered minus the cuts of the physical objects that cross it
        /// (the convex hull of the object's footprint and its region, so the gap is the object's own region) and stays whole
        /// everywhere else. <paramref name="pending"/> is a cut to apply before its object's region is written;
        /// <paramref name="replacing"/> an object whose recorded cut it replaces. Only the walls that change are written again.
        /// </summary>
        internal void RetrimWalls(Vector2[] pending = null, long replacing = 0)
        {
            _wallsDirty = false;
            var cuts = new List<Vector2[]>();
            foreach (KeyValuePair<long, Vector2[]> cut in _wallCutsByObject)
                if (cut.Key != replacing && cut.Value != null) cuts.Add(cut.Value);
            foreach (long region in new List<long>(_lingeringWallCuts.Keys))
            {
                if (TryGetSpatialHierarchyParent(_spaceID, region, out _)) cuts.Add(_lingeringWallCuts[region]);
                else _lingeringWallCuts.Remove(region);
            }
            if (pending != null) cuts.Add(pending);
            for (int i = 0; i < _wallShapes.Count; ++i)
            {
                Vector2[][] pieces = CutWall(_wallShapes[i], cuts);
                if (SamePieces(pieces, _wallPieces[i])) continue;
                // The old pieces go first: a new piece would overlap the old one it replaces (siblings may only touch).
                foreach (ElementID old in _wallPieceIDs[i])
                    if (!RemoveWallElement(old))
                        SpatialBlendDebug.LogError(LogCategory.Geometry, $"A wall piece {old} could not be removed in space {_spaceID}.");
                _wallPieceIDs[i].Clear();
                bool allTaken = true;
                foreach (Vector2[] piece in pieces)
                {
                    ElementID element = AddWallElement(_wallElementIDs[i]);
                    ++WallPieceWrites;
                    if (element > 0 && SetWalkablePolygon(element, (int)SbGeometryRole.Obstacle, piece))
                    {
                        _wallPieceIDs[i].Add(element);
                        continue;
                    }
                    allTaken = false;
                    SpatialBlendDebug.LogError(LogCategory.Geometry,
                        $"A wall obstacle was not registered ({SpatialHierarchySceneError(_spaceID) ?? "no message"}).");
                    if (element > 0) RemoveWallElement(element);
                }
                // A refused piece is written again by the next retrim (the wall is not marked as standing as computed).
                _wallPieces[i] = allTaken ? pieces : null;
            }
        }

        /// <summary>
        /// The cut a physical object makes in the walls: the convex hull of its footprint and its region (world ground), only when
        /// it reaches past the room's ring (its region extends the room's scope). Inside the ring a wall
        /// footprint is under a footprint-less group the core never checks against the object, so nothing is cut there and the band
        /// behind the wall stays closed to avatars. Null: no cut.
        /// </summary>
        internal Vector2[] WallCut(Vector2[] footprint, Vector2[] region)
        {
            var points = new List<Vector2>();
            if (footprint != null) points.AddRange(footprint);
            if (region != null) points.AddRange(region);
            Vector2[] hull = points.Count >= 3 ? ConvexHull(points) : null;
            if (hull == null) return null;
            Vector2[] ring = _roomRegion != null ? _roomRegion.Ring : null;
            if (ring == null) return hull;
            if (ConvexInsideRing(hull, ring)) return null;
            // Clear of the ring altogether: the core joins it to the room by a bridge across the gap (ScopeExtensionBridge), which
            // may not overlap a wall either. The cut reaches the ring along the shortest way (the region moved onto the ring, hulled).
            if (DisjointFromRing(hull, ring, out Vector2 toRing))
            {
                // The core's bridge for a near child is near_parent.Inflate(r) ∩ child.Inflate(r), r = gap + 1 mm, inflated with
                // mitred corners (containment_layout.cc ScopeExtensionBridge): at a corner turned 45° to the axes the mitre tip
                // reaches √2·r. The cut covers the region swept onto the ring and grown by an axis-aligned square of half side
                // √2·r plus a centimetre, which holds every mitre tip. For a far child (gap > 0.5 m) the bridge is a corridor as
                // wide as the child (at most 2 m), inside the same sweep; the grown sweep opens the wall a little wider than the
                // corridor, by the reach on each side.
                float reach = 1.41421356f * (toRing.magnitude + 0.001f) + 0.01f;
                var bridged = new List<Vector2>();
                foreach (Vector2 v in hull)
                    foreach (Vector2 w in new[] { v, v + toRing + toRing.normalized * 0.01f })
                    {
                        bridged.Add(w + new Vector2(reach, reach));
                        bridged.Add(w + new Vector2(reach, -reach));
                        bridged.Add(w + new Vector2(-reach, reach));
                        bridged.Add(w + new Vector2(-reach, -reach));
                    }
                return ConvexHull(bridged);
            }
            return hull;
        }

        // Whether convex `cut` and `ring` do not meet; `toRing` is then the shortest step from the cut to the ring.
        private static bool DisjointFromRing(Vector2[] cut, Vector2[] ring, out Vector2 toRing)
        {
            toRing = Vector2.zero;
            foreach (Vector2 p in cut) if (WalkablePlacementSnap.Contains(ring, p)) return false;
            foreach (Vector2 q in ring) if (StrictlyInsideConvex(q, cut, 0f)) return false;
            for (int i = 0; i < cut.Length; ++i)
                for (int k = 0; k < ring.Length; ++k)
                    if (ProperlyCross(cut[i], cut[(i + 1) % cut.Length], ring[k], ring[(k + 1) % ring.Length], 0f)) return false;
            float best = float.MaxValue;
            Vector2 step = Vector2.zero;
            foreach (Vector2 p in cut)
            {
                Vector2 d = ClosestOnRing(p, ring) - p;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; step = d; }
            }
            foreach (Vector2 q in ring)
            {
                Vector2 d = q - ClosestOnRing(q, cut);
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; step = d; }
            }
            toRing = step;
            return best < float.MaxValue && best > 0f;
        }

        private static Vector2 ClosestOnRing(Vector2 p, Vector2[] ring)
        {
            float best = float.MaxValue;
            Vector2 at = p;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], ab = ring[(i + 1) % ring.Length] - a;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                Vector2 c = a + ab * t;
                float d = (c - p).sqrMagnitude;
                if (d < best) { best = d; at = c; }
            }
            return at;
        }

        // Whether convex `cut` lies inside simple `ring` (touching its boundary counts as inside): every vertex of the cut in the
        // ring, no vertex of the ring strictly inside the cut, and no edge of one properly crossing an edge of the other.
        internal static bool ConvexInsideRing(Vector2[] cut, Vector2[] ring)
        {
            const float onEdge = 1e-4f;
            foreach (Vector2 p in cut)
                if (!WalkablePlacementSnap.Contains(ring, p) && DistanceToRing(p, ring) > onEdge) return false;
            foreach (Vector2 q in ring)
                if (StrictlyInsideConvex(q, cut, onEdge)) return false;
            for (int i = 0; i < cut.Length; ++i)
                for (int k = 0; k < ring.Length; ++k)
                    if (ProperlyCross(cut[i], cut[(i + 1) % cut.Length], ring[k], ring[(k + 1) % ring.Length], onEdge)) return false;
            return true;
        }

        private static float DistanceToRing(Vector2 p, Vector2[] ring)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], ab = ring[(i + 1) % ring.Length] - a;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        // Inside a counter-clockwise convex polygon by more than `margin` from every edge.
        private static bool StrictlyInsideConvex(Vector2 p, Vector2[] convex, float margin)
        {
            for (int i = 0; i < convex.Length; ++i)
            {
                Vector2 a = convex[i], b = convex[(i + 1) % convex.Length], e = b - a;
                float len = e.magnitude;
                if (len < 1e-9f) continue;
                if ((e.x * (p.y - a.y) - e.y * (p.x - a.x)) / len <= margin) return false;
            }
            return true;
        }

        // Segments ab and cd cross at a point interior to both (more than `margin` from every end).
        private static bool ProperlyCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float margin)
        {
            float d1 = Cross2(c, d, a), d2 = Cross2(c, d, b), d3 = Cross2(a, b, c), d4 = Cross2(a, b, d);
            float lcd = (d - c).magnitude, lab = (b - a).magnitude;
            if (lcd < 1e-9f || lab < 1e-9f) return false;
            return d1 / lcd * (d2 / lcd) < -margin * margin && d3 / lab * (d4 / lab) < -margin * margin;
        }

        private static float Cross2(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        // The wall minus every cut that overlaps it: disjoint convex pieces, kept off each cut by the data precision's clearance.
        private static Vector2[][] CutWall(Vector2[] wall, List<Vector2[]> cuts)
        {
            var pieces = new List<Vector2[]> { wall };
            double wallArea = SceneFootprintLayout.Area(wall);
            float largest = 1f;
            foreach (Vector2 v in wall) largest = Mathf.Max(largest, Mathf.Abs(v.x), Mathf.Abs(v.y));
            double clearance = SceneFootprintLayout.Clearance(largest);
            foreach (Vector2[] cut in cuts)
            {
                if (cut == null || cut.Length < 3) continue;
                var next = new List<Vector2[]>();
                foreach (Vector2[] piece in pieces)
                {
                    if (SceneFootprintLayout.IntersectionArea(piece, cut) <= SceneFootprintLayout.DataPrecision * wallArea) next.Add(piece);
                    else SceneFootprintLayout.SubtractConvex(piece, cut, next, SceneFootprintLayout.DataPrecision * wallArea, clearance);
                }
                pieces = next;
            }
            return pieces.ToArray();
        }

        /// <summary>Wall piece elements written so far (a retrim that changes nothing writes none).</summary>
        internal int WallPieceWrites { get; private set; }

        // The same pieces in any order (each piece's vertices in the order the cut produces them).
        private static bool SamePieces(Vector2[][] a, Vector2[][] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var used = new bool[b.Length];
            foreach (Vector2[] piece in a)
            {
                int match = -1;
                for (int j = 0; j < b.Length && match < 0; ++j)
                    if (!used[j] && SamePiece(piece, b[j])) match = j;
                if (match < 0) return false;
                used[match] = true;
            }
            return true;
        }

        private static bool SamePiece(Vector2[] a, Vector2[] b)
        {
            if (a.Length != b.Length) return false;
            for (int k = 0; k < a.Length; ++k)
                if ((a[k] - b[k]).sqrMagnitude > 1e-12f) return false;
            return true;
        }

        // Andrew's monotone chain, counter-clockwise, no collinear points.
        private static Vector2[] ConvexHull(List<Vector2> points)
        {
            points.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            var hull = new List<Vector2>();
            double Cross(Vector2 o, Vector2 a, Vector2 b) => ((double)a.x - o.x) * ((double)b.y - o.y) - ((double)a.y - o.y) * ((double)b.x - o.x);
            for (int pass = 0; pass < 2; ++pass)
            {
                int start = hull.Count;
                for (int k = 0; k < points.Count; ++k)
                {
                    Vector2 p = points[pass == 0 ? k : points.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull.Count >= 3 ? hull.ToArray() : null;
        }

        /// <summary>The walls of the registered room (empty before one is registered).</summary>
        internal IReadOnlyList<Vector2[]> RoomWalls => _roomRegion != null ? _roomRegion.Walls : (IReadOnlyList<Vector2[]>)Array.Empty<Vector2[]>();

        protected virtual ElementID EnsureWalkableRoom() => EnsureRoomElement();

        protected virtual ElementID AddWallElement(ElementID parent) => SpatialBlendAPI.AddElement(_spaceID, parent);

        // False when the core refused the polygon.
        protected virtual bool SetWalkablePolygon(ElementID element, int role, IReadOnlyList<Vector2> points) =>
            SpatialBlendAPI.SetElementPolygon(_spaceID, element, (SbGeometryRole)role, points);

        protected virtual bool RemoveWallElement(ElementID element) =>
            SpatialBlendAPI.RemoveLocalElement(_spaceID, element);

        // --- Native Utility Methods ---
        internal void LogSpaceStatus() => SpatialBlendDebug.Log(LogCategory.Connection, $"SpatialBlend space {_spaceID}, session {_sessionID}");

        public SpaceInfo GetSpaceInfo()
        {
            // v2 native exposes no sb_get_space_info, so report from the state the
            // manager already tracks. IsHost reflects the network role, not a stub.
            int numEntities;
            lock (_entitiesLock)
            {
                numEntities = _entities.Count;
            }
            return new SpaceInfo(new Internal.SbSpaceInfo
            {
                num_entities = (ulong)numEntities,
                num_connected_spaces = 0,
                is_host = _networkRole == SpaceManagerType.Host,
                is_connected = _initialized,
                has_walkable_geometry = false,
                has_wall_geometry = false,
            });
        }

        public EntityInfo GetEntityInfo(long entityId)
        {
            SbEntity entity;
            lock (_entitiesLock)
            {
                entity = _entities.Find(e => e != null && e.ID == entityId);
            }

            // Always return non-null position arrays — the inspector indexes
            // Position3D[0..2] directly, so default(EntityInfo) would NRE.
            if (entity == null)
            {
                return new EntityInfo(new Internal.SbEntityInfo
                {
                    entity_id = entityId,
                    is_local = false,
                    is_valid = false,
                    user_data_size = 0,
                    position_2d = new double[2],
                    position_3d = new double[3],
                    rotation_yaw = 0,
                    rotation_pitch = 0,
                });
            }

            // v2 has no native sb_get_entity_info; report Unity world-space state.
            var t = entity.EntityTransform;
            Vector3 p = t != null ? t.position : Vector3.zero;
            float yaw = t != null ? t.eulerAngles.y : 0f;
            return new EntityInfo(new Internal.SbEntityInfo
            {
                entity_id = entityId,
                is_local = entity.IsSource,
                is_valid = entity.IsAttached(),
                user_data_size = 0,
                position_2d = new double[] { p.x, p.z },
                position_3d = new double[] { p.x, p.y, p.z },
                rotation_yaw = yaw,
                rotation_pitch = 0,
            });
        }

        public EntityCounts GetEntityCounts()
        {
            ulong total = 0, local = 0, remote = 0;
            lock (_entitiesLock)
            {
                foreach (var e in _entities)
                {
                    if (e == null || !e.IsAttached()) continue;
                    total++;
                    if (e.IsSource) local++;
                    else remote++;
                }
            }
            return new EntityCounts(new Internal.SbEntityCounts
            {
                total_entities = total,
                local_entities = local,
                remote_entities = remote,
                dirty_entities = 0, // v2 manager has no dirty tracking
            });
        }

        public EntityState GetEntityState(long entityId)
            => default;

        public SpaceStateSummary GetSpaceStateSummary()
            => default;

        public InteractionInfo GetInteractionInfo(int proximityType, long sourceEntityId)
            => default;

        public double GetProximity(int proximityType, long sourceEntityId, long targetEntityId)
            => 0.0;

        public ProximityInfo GetProximityInfo(int proximityType)
            => default;

        public GeometryBounds GetGeometryBounds(int geometryType)
            => default;

        public Polygon GetWalkablePolygon()
            => default;

        public Polygon GetWallPolygon()
            => default;

        public Polygon GetEntityAffordancePolygon(long entityId)
            => default;

        public Point2D GetNearestPointInWalkable(Point2D point)
            => point;

        public bool IsPointInWalkable(Point2D point)
            => true;

        public string GetSpaceStateString()
        {
            return $"SpatialBlend space {_spaceID}, session {_sessionID}, entities {_entities.Count}, interactions {_interactions.Count}";
        }

        public PolygonEx GetEffectiveWalkablePolygonEx()
            => default;

        public EntityTargetInfo GetEntityTargetInfo(long entityId)
            => default;

        public InteractionTimeline GetEntityInteractionTimeline(long sourceEntityId, int proximityType)
            => default;

        public ProximityDistanceInfo GetProximityDistanceInfo(int proximityType, long sourceEntityId, long targetEntityId)
            => default;

        public bool IsInteractable(int proximityType, long sourceEntityId, long targetEntityId)
            => false;

        // v2 (SpatialBlend): register every SbAffordance found on the entity or its
        // children as an InteractionRegion child element of the entity's furniture
        // element — the AffordancePose relation's referent. This is the v2-native,
        // lightweight path for entities without an SbSpatialAuthoring declaration:
        // attach SbAffordance to the SPECIFIC surface (a board's writable face, a
        // seat) and its world-AABB collider supplies footprint + height. No geometry
        // is added to the parent furniture — kProximal returns the whole container
        // subtree, so the child alone is a valid referent. Called for SOURCE entities
        // only (the source forms relations; receivers reconstruct from the payload).
        public void RegisterAffordances(SbEntity entity)
        {
            if (entity == null || !entity.IsSource)
            {
                return;
            }
            SbAffordance[] affordances = entity.GetComponentsInChildren<SbAffordance>(false);
            if (affordances == null || affordances.Length == 0)
            {
                return;
            }

            long parentShared = ResolveSharedElementId(entity.gameObject);
            int idx = 0;
            foreach (SbAffordance aff in affordances)
            {
                Collider col = aff.ResolveBoundary();
                if (col == null)
                {
                    SpatialBlendDebug.LogWarning(LogCategory.Geometry,
                        $"SbAffordance on '{aff.gameObject.name}' has no Collider; skipped");
                    continue;
                }

                try
                {
                    ElementID affElem = SpatialBlendAPI.AddElement(_spaceID, entity.ID);
                    if (affElem <= 0)
                    {
                        SpatialBlendDebug.LogWarning(LogCategory.Geometry,
                            $"Failed to create the core affordance element for '{aff.gameObject.name}'");
                        continue;
                    }

                    // Child LOCAL transform = the collider's world center expressed in
                    // the parent furniture's frame. Its world Z (height) flows into the
                    // reconstructed per-anchor target Z (SolveActorTargets §8.1).
                    Bounds b = col.bounds;  // world-axis-aligned
                    Matrix4x4 worldCenter = Matrix4x4.TRS(b.center, Quaternion.identity, Vector3.one);
                    Matrix4x4 localTransform = entity.transform.worldToLocalMatrix * worldCenter;
                    SpatialBlendAPI.SetElementTransform(_spaceID, affElem, localTransform);

                    // Footprint rectangle (centered, Unity local XZ) from collider extents.
                    float hx = b.extents.x;
                    float hz = b.extents.z;
                    if (!(hx > 0f) || !(hz > 0f))
                    {
                        // A zero-area footprint is a segment: it contains nothing (core: GroundImageIsSegment). It was padded
                        // to 0.01 m (uncited; v1 had no such path); a collider with no area is reported, not invented around.
                        SpatialBlendDebug.LogError(LogCategory.Geometry,
                            $"SbAffordance on '{aff.gameObject.name}' has a collider with no horizontal extent; skipped");
                        SpatialBlendAPI.RemoveLocalElement(_spaceID, affElem);
                        continue;
                    }
                    List<Vector2> polygon = new List<Vector2>
                    {
                        new Vector2(-hx, -hz),
                        new Vector2(hx, -hz),
                        new Vector2(hx, hz),
                        new Vector2(-hx, hz),
                    };
                    SpatialBlendAPI.SetElementPolygon(_spaceID, affElem,
                        SbGeometryRole.InteractionRegion, polygon);
                    SpatialBlendAPI.SetElementTag(_spaceID, affElem, aff.affordanceTag);

                    // Deterministic shared id so the SAME surface resolves to the SAME
                    // referent in both apartments. The *16 band starts at 8,
                    // which keeps the ids the retired v1 volume path used (ordinals 1..4) free.
                    long affShared = parentShared * 16 + 8 + idx;
                    SpatialBlendAPI.SetElementSharedId(_spaceID, affElem, affShared);
                    idx++;
                }
                catch (Exception ex)
                {
                    SpatialBlendDebug.LogError(LogCategory.Geometry,
                        $"Failed to register affordance '{aff.gameObject.name}': {ex.Message}");
                }
            }
        }
    }
}
