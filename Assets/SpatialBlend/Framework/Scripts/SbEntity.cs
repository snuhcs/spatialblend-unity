// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlendManager = SpatialBlend.SpatialBlendManager;
using Internal = SpatialBlend.Internal;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;
using SpatialBlend.Interactions;
using SpatialBlend.Motion;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;

namespace SpatialBlend
{
    using EntityID = System.Int64;

    /// <summary>
    /// Type of placement motif to auto-add to the entity.
    /// </summary>
    public enum PlacementMotifType
    {
        None,
        Panel,
        Object
    }

    // Always run after SpatialBlendManager
    [DefaultExecutionOrder(2)]
    public abstract class SbEntity : MonoBehaviour
    {
        /// <summary>
        /// Layer name for all SbEntity objects. Set via Project Settings or code.
        /// Used by InteractionCoordinator for entity detection.
        /// Default: "VirtualObject" (User Layer 7), the layer the object prefabs, <c>GltfLoader</c> and the
        /// whiteboard pointer already use (as v1).
        /// </summary>
        public static string EntityLayerName { get; set; } = "VirtualObject";

        /// <summary>
        /// Whether to automatically set the layer on Awake.
        /// </summary>
        public static bool AutoSetLayer { get; set; } = true;

        /// <summary>
        /// Gets the LayerMask for detecting SbEntity objects.
        /// Returns -1 (all layers) if the layer doesn't exist.
        /// </summary>
        public static LayerMask GetEntityLayerMask()
        {
            int layer = LayerMask.NameToLayer(EntityLayerName);
            return layer >= 0 ? (1 << layer) : -1;
        }

        [SerializeField]
        private SpatialBlendManager _spaceManager = null;
        [SerializeField]
        private EntityID _id = -1;

        // NOTE: v2 cross-space identity is no longer a Unity-assigned "logical shared
        // id". Each element gets an internal id from spatialblend; the cross-space
        // correspondence is established by the share operation (ElementLinker), and
        // referents/actors resolve through that link. The old _v2LogicalSharedId
        // convention was removed.
        private bool isSource = false;
        // Set before managed release callbacks. A reentrant Attach clears it again,
        // so repeated teardown is quiet without masking a new registration.
        private bool _detachedCleanupComplete;
        private long _attachmentVersion;
        protected Transform _entityTransform;
        public Transform EntityTransform
        {
            get
            {
                if (_entityTransform == null)
                    SetTransform(transform);
                return _entityTransform;
            }
        }

        /// <summary>
        /// v2 (SpatialBlend): drive this entity toward a realization target. The
        /// default routes to a NavMeshAgent if one is present (locomotion +
        /// animation), otherwise sets the transform directly. Entities with their
        /// own movement controller (e.g. LocalCharacter) override this to feed it,
        /// which also keeps any predicted-target visualization in sync.
        /// </summary>
        public virtual void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation)
        {
            var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.SetDestination(worldPosition);
            }
            else
            {
                EntityTransform.SetPositionAndRotation(worldPosition, worldRotation);
            }
        }

        /// <summary>
        /// v2 (SpatialBlend): cancel the active root locomotion pipeline because
        /// it is absent from the latest authoritative snapshot.
        /// </summary>
        public virtual void CancelRealizationTarget()
        {
            var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
            }
        }

        /// <summary>
        /// v2 (SpatialBlend): receive a per-anchor body-part target (hand, pelvis, …)
        /// reconstructed for this entity — distinct from the root locomotion target
        /// handled by <see cref="ApplyRealizationTarget"/>. The CORE's responsibility
        /// ends at delivering this world-space target; what an app does with it (IK,
        /// animation retarget, baked-clip offset, a debug marker) is entirely the
        /// application's concern. The framework default only logs, so per-anchor
        /// targets are observable without committing the framework to any pose system.
        /// <paramref name="hasHeight"/> is true when <paramref name="worldPosition"/>.y
        /// carries a real height (affordance targets) vs a ground projection.
        /// Release is delivered explicitly through
        /// <see cref="CancelAnchorTarget"/> when an anchor is absent from a newer
        /// authoritative snapshot.
        /// </summary>
        public virtual void ApplyAnchorTarget(string anchorName, Vector3 worldPosition,
                                              float yaw, bool hasHeight, double weight)
        {
            // A controller is opt-in: existing tracked-avatar ownership remains
            // untouched unless the application attaches this explicit C boundary.
            if (TryGetComponent(out AnchorTargetController controller))
            {
                controller.ApplyTarget(anchorName, worldPosition, yaw, hasHeight, weight);
                return;
            }
            // Without a writer the target would go nowhere. A write that silently does nothing is a defect:
            // report it, once per anchor, instead.
            ReportUnsupportedAnchorTarget(anchorName);
        }

        private HashSet<string> _unsupportedAnchorTargets;

        /// <summary>Anchors a core proposal arrived for that this entity has no writer for (reported once each).</summary>
        public IReadOnlyCollection<string> UnsupportedAnchorTargets =>
            (IReadOnlyCollection<string>)_unsupportedAnchorTargets ?? Array.Empty<string>();

        /// <summary>
        /// A core proposal for <paramref name="anchorName"/> reached an entity with no writer for it: reported as an error,
        /// once per anchor, and listed in <see cref="UnsupportedAnchorTargets"/>.
        /// </summary>
        protected void ReportUnsupportedAnchorTarget(string anchorName)
        {
            _unsupportedAnchorTargets ??= new HashSet<string>(StringComparer.Ordinal);
            if (!_unsupportedAnchorTargets.Add(anchorName)) return;
            Debug.LogError($"[SbEntity] '{name}' has no anchor-target writer (no AnchorTargetController): the core's proposal " +
                           $"for its '{anchorName}' anchor is unsupported and was not applied.");
        }

        /// <summary>
        /// ABI 4.14: the anchors whose source sample is replayed through the chart of a shared object this frame (empty: none is).
        /// The chart is what the source anchor does relative to the object, as is; the embodied driver hands it over beside its
        /// writes and the entity composes it with whatever depends on that anchor. The default has nothing that does.
        /// </summary>
        internal virtual void ApplyAnchorCharts(IReadOnlyList<Internal.EmbodiedAnchorChart> charts) { }

        /// <summary>Drops every chart the entity composes with, at once (its driver was reset or closed).</summary>
        internal virtual void ClearAnchorCharts() { }

        /// <summary>
        /// v2 (SpatialBlend): cancel an app-owned body-anchor command because no
        /// pipeline for that anchor remains in the latest snapshot.
        /// </summary>
        public virtual void CancelAnchorTarget(string anchorName)
        {
            if (TryGetComponent(out AnchorTargetController controller))
            {
                controller.CancelTarget(anchorName);
            }
        }

        /// <summary>
        /// v2 (SpatialBlend): report this entity's CURRENT realized pose — the
        /// counterpart to <see cref="ApplyRealizationTarget"/>. The solver is fed this
        /// as the avatar/rendered state (vs the authoritative target) so its progressive
        /// step starts from where the entity ACTUALLY is — necessary whenever a backend
        /// (controller-driven locomotion, animation, motion generation) makes the
        /// realized pose lag the commanded target. Default = the entity transform;
        /// backends whose realized pose lives elsewhere override this to report it.
        /// Together, ApplyRealizationTarget (consume control signal) + GetRealizedPose
        /// (report realized state) form the backend-agnostic state interface, so the
        /// manager never needs to know which backend drives a given entity.
        /// </summary>
        public virtual void GetRealizedPose(out Vector3 position, out Quaternion rotation)
        {
            var t = EntityTransform;
            position = t.position;
            rotation = t.rotation;
        }

        /// <summary>
        /// ABI 4: the body anchors (pelvis, head, ...) of this entity whose realized pose the entity can report. The
        /// embodied driver publishes each of them to the core through the anchor's own port every frame, and follows the
        /// core's proposal for it. The default is none: only the root (<see cref="GetRealizedPose"/>) is sampled. A
        /// backend with a registered kinematic chain overrides this together with <see cref="TryGetRealizedAnchorPose"/>.
        /// </summary>
        public virtual IReadOnlyList<string> RealizedAnchorNames => Array.Empty<string>();

        /// <summary>
        /// ABI 4: the CURRENT realized world pose of one of <see cref="RealizedAnchorNames"/>, as the backend actually has
        /// it (not the commanded target).
        /// </summary>
        public virtual bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// The preset avatar this entity shows, when it shows one, else null. The embodiment registration uses it to register the
        /// sit-bone offset measured on that preset instead of one preset's for every avatar.
        /// </summary>
        public virtual int? AvatarPresetIndex => null;



        // These properties will reset on play
        public SpatialBlendManager spaceManager { get => _spaceManager; private set => _spaceManager = value; }
        public EntityID ID { get => _id; private set => _id = value; }

        [Header("Manipulation Settings")]
        [SerializeField, Tooltip("Whether this entity can be manipulated (hover, select, move)")]
        protected bool _allowManipulation = true;

        [SerializeField, Tooltip("Type of placement motif to auto-add (object)")]
        protected PlacementMotifType _placementMotifType = PlacementMotifType.Object;

        /// <summary>
        /// Whether this entity can be manipulated (hover, select, move).
        /// </summary>
        public bool AllowManipulation { get => _allowManipulation; set => _allowManipulation = value; }

        /// <summary>
        /// Type of placement motif to auto-add to this entity.
        /// </summary>
        public PlacementMotifType PlacementMotifTypeValue { get => _placementMotifType; set => _placementMotifType = value; }

        public abstract EntityType EntityType { get; }
        public bool IsSource { get { return isSource; } }

        // Cached last reported position (for debugging/visualization)
        private Vector3 lastReportedPosition = Vector3.zero;
        private bool hasReportedPosition = false;
        public Vector3 LastReportedPosition => lastReportedPosition;
        public bool HasReportedPosition => hasReportedPosition;

        public delegate void OnUpdateDataDelegate(IntPtr userData, ulong userDataSize);
        private OnUpdateDataDelegate onUpdateDataCallback = null;

        public delegate void OnUpdateStateDelegate(ProximityType proximityType, StateType stateType, [In] double[] stateArr, ulong stateArrSize);
        private OnUpdateStateDelegate onUpdateStateCallback = null;

        public delegate void OnUpdateStateBatchDelegate(ProximityType proximityType, IReadOnlyList<Internal.StateUpdateBatchItem> updates);
        private OnUpdateStateBatchDelegate onUpdateStateBatchCallback = null;

        public delegate void OnRemoveDelegate();
        private OnRemoveDelegate onRemoveCallback = null;

        public delegate void OnInteractionDelegate(InteractionEventType eventType, EntityID toEntityId, ProximityType proximityType, ulong interactionTime);
        private OnInteractionDelegate onInteractionCallback = null;

        // Source-side behavior
        // - UpdateData() : Propagate any data to linked targets in remote spaces
        // - UpdateStates() : Update the entity's state (e.g., transform) - automatically called by SpatialBlendManager::Update()

        // Target-side behavior
        // - OnUpdateDataCallback() : Defines entity-specific data update logic (e.g., update the animation)
        // - OnUpdateStateCallback() : Defines entity-specific state update logic (e.g., update the transform)
        // - AddSerializationBlock() : Defines entity-specific serialization / deserialization logic

        public void SetOnUpdateDataCallback(OnUpdateDataDelegate callback)
        {
            onUpdateDataCallback = callback;
        }

        public void SetOnUpdateStateCallback(OnUpdateStateDelegate callback)
        {
            onUpdateStateCallback = callback;
        }

        public void SetOnUpdateStateBatchCallback(OnUpdateStateBatchDelegate callback)
        {
            onUpdateStateBatchCallback = callback;
        }

        public void SetOnRemoveCallback(OnRemoveDelegate callback)
        {
            onRemoveCallback = callback;
        }

        public void SetOnInteractionCallback(OnInteractionDelegate callback)
        {
            onInteractionCallback = callback;
        }

        internal virtual void SetTransform(Transform transform)
        {
            _entityTransform = transform;
        }


        public void AddSerializationBlock(SerializationBlock block)
        {
            if (serializationBlocks.Count >= MAX_SERIALIZATION_BLOCKS)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, $"SbEntity::AddSerializationBlock - Cannot add more serialization blocks. Maximum limit of {MAX_SERIALIZATION_BLOCKS} reached.");
                return;
            }
            serializationBlocks.Add(block);
        }

        protected void UpdateData(IntPtr userData, ulong userDataSize)
        {
            if (isSource && IsAttached())
            {
                spaceManager.UpdateData(ID, userData, userDataSize);
            }
        }

        public virtual void Awake()
        {
            // Auto-set layer for entity detection
            if (AutoSetLayer && !string.IsNullOrEmpty(EntityLayerName))
            {
                int layer = LayerMask.NameToLayer(EntityLayerName);
                if (layer >= 0)
                {
                    gameObject.layer = layer;
                }
            }
        }

        private void SetupSourceEntity()
        {
            // As in v1, the auto-setup of the manipulation and placement motifs is
            // disabled. The objects' prefabs carry their own motifs, so the call made duplicates.
            // TODO(Dongho): Follow the hierarchy of current objects(not to make duplicates)
            // // Auto-setup manipulation and placement motifs
            // SetupManipulationPanelMotifs();
        }

        /// <summary>
        /// Auto-setup PlacementMotif and ManipulationPanelMotif based on entity settings.
        /// Adds ISDK components directly on the collider's GameObject for proper detection.
        /// </summary>
        protected virtual void SetupManipulationPanelMotifs()
        {
            // Skip entirely if manipulation is not allowed
            if (!_allowManipulation) return;

            // Find the entity's collider
            var collider = GetComponentInChildren<Collider>();
            if (collider == null)
            {
                Debug.LogError($"[SbEntity:{name}] No Collider found. Add a Collider for ISDK interaction.");
                return;
            }

            // Get the GameObject that has the collider - ISDK components must be on this object
            var colliderGO = collider.gameObject;

            // Add ISDK components directly on the collider's GameObject (always, even if motif exists)
            var surface = colliderGO.GetComponent<ColliderSurface>();
            if (surface == null)
            {
                surface = colliderGO.AddComponent<ColliderSurface>();
                surface.InjectCollider(collider);
            }

            var rayInteractable = colliderGO.GetComponent<RayInteractable>();
            if (rayInteractable == null)
            {
                rayInteractable = colliderGO.AddComponent<RayInteractable>();
                rayInteractable.InjectSurface(surface);
            }

            // Setup PlacementMotif if type is specified
            if (_placementMotifType != PlacementMotifType.None)
            {
                // Check if motif already exists
                var existingMotif = GetComponentInChildren<PlacementMotif>();
                if (existingMotif != null)
                {
                    // Wire existing motif's RayInteractable reference
                    var rayField = existingMotif.GetType().GetField("_rayInteractable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    rayField?.SetValue(existingMotif, rayInteractable);
                }
                else
                {
                    // Add motif component directly on this entity
                    PlacementMotif motif = _placementMotifType switch
                    {
                        PlacementMotifType.Panel => gameObject.AddComponent<Interactions.PanelPlacementMotif>(),
                        PlacementMotifType.Object => gameObject.AddComponent<Interactions.ObjectPlacementMotif>(),
                        _ => null
                    };

                    if (motif != null)
                    {
                        // Wire the motif's RayInteractable reference
                        var rayField = motif.GetType().GetField("_rayInteractable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        rayField?.SetValue(motif, rayInteractable);
                    }
                }
            }

            // Add ManipulationPanelMotif for A button panel toggle (independent of PlacementMotif)
            ManipulationPanelMotif.Setup(gameObject);
        }

        public virtual void Start()
        {
            if (!IsAttached())
            {
                // If the entity is not attached to a space manager, it could be a remote entity duplicated by PrepareSync()
                // In such case, we just trigger lazy sync to all space managers
                var managers = FindObjectsByType<SpatialBlendManager>(FindObjectsSortMode.None);
                foreach (var manager in managers)
                {
                    manager.TryLazySync();
                }

                this.SetOnUpdateStateBatchCallback(new OnUpdateStateBatchDelegate(DefaultOnUpdateStateBatch));
            }
        }

        public virtual void OnDestroy()
        {
            Detach();
        }

        public bool IsAttached()
        {
            return spaceManager != null && ID != -1;
        }

        // Monotonic per-component attachment identity. Manager-owned replica
        // provenance uses this in addition to its registration token: a callback
        // can attach and detach this same component before stale cleanup resumes.
        internal long AttachmentVersion => _attachmentVersion;

        // Helper functions called by SpatialBlendManager
        internal void Attach(SpatialBlendManager spaceManager, EntityID ID, bool isSource)
        {
            if (spaceManager == null)
            {
                throw new ArgumentNullException(nameof(spaceManager));
            }
            // Reject a retired owner before changing identity. A failed attempt
            // must not look attached or overwrite the previous registration.
            spaceManager.ValidateCanAttach();
            ++_attachmentVersion;
            _detachedCleanupComplete = false;
            this.ID = ID;
            this.spaceManager = spaceManager;
            this.isSource = isSource;

            if (isSource)
            {
                SetupSourceEntity();
            }
            spaceManager.AddEntity(this);
        }

        internal void Detach()
        {
            if (_detachedCleanupComplete && !IsAttached())
            {
                return;
            }

            // Snapshot and clear the old registration before invoking anything that
            // can synchronously observe a target release. An observer may attach this
            // same component again; no cleanup after that point may erase the new
            // registration.
            SpatialBlendManager previousManager = spaceManager;
            EntityID previousId = ID;
            long previousAttachmentVersion = _attachmentVersion;
            bool wasAttached = IsAttached();
            TryGetComponent(out AnchorTargetController previousAnchorController);
            long previousRegistrationToken = wasAttached && previousManager != null
                ? previousManager.GetEntityRegistrationToken(this, previousId)
                : -1;
            _detachedCleanupComplete = true;
            this.ID = -1;
            this.spaceManager = null;
            isSource = false;

            // Clear every managed callback, including the v2 batch callback.
            onUpdateDataCallback = null;
            onUpdateStateCallback = null;
            onUpdateStateBatchCallback = null;
            onRemoveCallback = null;
            onInteractionCallback = null;

            // Clear reported position
            lastReportedPosition = Vector3.zero;
            hasReportedPosition = false;

            // Manager owns map/native propagation and must not call back into this
            // cleanup. It receives the immutable old registration exactly once.
            if (wasAttached && previousManager != null)
            {
                previousManager.RemoveEntity(this, previousId, previousRegistrationToken);
            }

            if (_attachmentVersion != previousAttachmentVersion)
            {
                // Source unshare can synchronously notify a local peer. If that
                // observer reattached us, its goals belong to the new registration.
                return;
            }

            // These are local, opt-in realization consumers. Do not invoke native
            // APIs here; manager/session teardown owns external propagation. Release
            // the captured anchor set before root cancellation so a reentrant root
            // callback cannot have its newly attached goals released by old cleanup.
            if (previousAnchorController != null)
            {
                previousAnchorController.ReleaseAllTargets();
            }
            if (_attachmentVersion == previousAttachmentVersion)
            {
                CancelRealizationTarget();
            }
        }

        // SpatialBlendManager::Update() will call this method to update the entity's state.
        public virtual void UpdateStates(HashSet<StateType> stateTypes, bool forceUpdate = false)
        {
            if (!IsAttached())
            {
                return;
            }

            if (_entityTransform == null)
            {
                SetTransform(transform);
            }

            if (_entityTransform.hasChanged || forceUpdate)
            {
                spaceManager.UpdateV2EntityState(this);
                lastReportedPosition = _entityTransform.position;
                hasReportedPosition = true;
                _entityTransform.hasChanged = false;
            }
        }

        internal void OnUpdateData(IntPtr userData, ulong userDataSize)
        {
            if (onUpdateDataCallback != null)
            {
                onUpdateDataCallback(userData, userDataSize);
            }
        }

        internal void OnUpdateState(ProximityType originProximityType, StateType stateType, [In] double[] stateArr, ulong stateArrSize)
        {
            // stateArr is already in Unity coordinates.
            if (onUpdateStateCallback != null)
            {
                onUpdateStateCallback(originProximityType, stateType, stateArr, stateArrSize);
            }
        }

        internal void OnUpdateStateBatch(ProximityType originProximityType, IReadOnlyList<Internal.StateUpdateBatchItem> updates)
        {
            if (onUpdateStateBatchCallback != null)
            {
                onUpdateStateBatchCallback(originProximityType, updates);
            }
        }

        internal void OnInteraction(InteractionEventType eventType, EntityID toEntityId, ProximityType proximityType, ulong interactionTime)
        {
            if (onInteractionCallback != null)
            {
                onInteractionCallback(eventType, toEntityId, proximityType, interactionTime);
            }
        }

        internal void OnRemove()
        {
            if (onRemoveCallback != null)
            {
                onRemoveCallback();
            }
        }

        protected virtual float GetGroundLevel()
        {
            // Default ground level - override in derived classes for custom ground detection
            return 0.0f;
        }

        protected virtual float GetYPositionForPosition2D(float currentY, float groundLevel)
        {
            // Default behavior: snap to ground if floating, otherwise maintain current Y
            if (Mathf.Abs(currentY - groundLevel) < 0.1f)
            {
                // Already near ground, keep current Y
                return currentY;
            }
            else
            {
                // Snap to ground level to prevent floating
                return groundLevel;
            }
        }

        protected void DefaultOnUpdateState(ProximityType proximityType, StateType stateType, double[] stateArr, ulong stateArrSize)
        {
            if (_entityTransform == null)
            {
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbEntity::DefaultOnUpdateState - {gameObject.name} transform is null");
                SetTransform(transform);
            }
            // by default, just update the transform
            if (stateType == StateType.Position2D)
            {
                Vector3 newPosition = _entityTransform.position;
                newPosition.x = (float)stateArr[0];
                newPosition.z = (float)stateArr[1];
                // Keep the current Y position since entities are now created at the correct position
                _entityTransform.position = newPosition;
            }
            else if (stateType == StateType.Position3D)
            {
                _entityTransform.position = new Vector3(
                    (float)stateArr[0],
                    (float)stateArr[1],
                    (float)stateArr[2]
                );
            }
            else if (stateType == StateType.RotationYaw)
            {
                Vector3 newRotation = _entityTransform.eulerAngles;
                newRotation.y = (float)stateArr[0];
                _entityTransform.eulerAngles = newRotation;
            }
            else if (stateType == StateType.RotationPitch)
            {
                Vector3 newRotation = _entityTransform.eulerAngles;
                newRotation.x = (float)stateArr[0];
                _entityTransform.eulerAngles = newRotation;
            }
        }

        protected void DefaultOnUpdateStateBatch(ProximityType proximityType, IReadOnlyList<Internal.StateUpdateBatchItem> updates)
        {
            if (_entityTransform == null)
            {
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbEntity::DefaultOnUpdateStateBatch - {gameObject.name} transform is null");
                SetTransform(transform);
            }
            foreach (var update in updates)
            {
                if (update.Type == StateType.Position2D)
                {
                    Vector3 newPosition = _entityTransform.position;
                    newPosition.x = (float)update.State[0];
                    newPosition.z = (float)update.State[1];
                    // Keep the current Y position since entities are now created at the correct position
                    _entityTransform.position = newPosition;
                }
                else if (update.Type == StateType.Position3D)
                {
                    _entityTransform.position = new Vector3(
                        (float)update.State[0],
                        (float)update.State[1],
                        (float)update.State[2]
                    );
                }
                else if (update.Type == StateType.RotationYaw)
                {
                    Vector3 newRotation = _entityTransform.eulerAngles;
                    newRotation.y = (float)update.State[0];
                    _entityTransform.eulerAngles = newRotation;
                }
                else if (update.Type == StateType.RotationPitch)
                {
                    Vector3 newRotation = _entityTransform.eulerAngles;
                    newRotation.x = (float)update.State[0];
                    _entityTransform.eulerAngles = newRotation;
                }
            }
        }

        /// <summary>
        /// Serialize and Deserialize logic for the entity.
        /// Assuming that external code will call AddSerializationBlock to add serialization / deserialization logic when the entity is shared.
        /// For example, SbMeshPrefab defines GameObject with SbNetworkEntity component, and adds serialization / deserialization logic for each MeshFilter and MeshRenderer
        /// components to deliver run-time generated triangles and materials.
        /// </summary>

        protected static readonly int MAX_SERIALIZATION_BLOCKS = 5;

        internal List<SerializationBlock> serializationBlocks = new List<SerializationBlock>();

        // Serialize and Deserialization logic depending on the entity type.
        // This is called by the SpatialBlendManager when the entity is shared or synced.
        // SpaceManager will call Serialize() and Deserialize() of each SerializationBlock to fill / read the memory.
        // Below methods are responsible to define entity-specific serialization / deserialization logic.
        //
        // Expected memory layout:
        // [EntityType (1 byte) | # of serialization blocks (1 byte) | { Num bytes per block (4 byte) | {Actual serialized data} | ... } | {Entity type-specific data for serialization e.g., Fusion ID}]
        // Expected work flow:
        // - Source-side:
        // 1. Allocate entire memory (refer to above expected memory layout) 
        // 2. Call Serialize() of each SerializationBlock to fill the memory
        // 3. Propagate the memory to remote spaces
        // 4. Call PrepareSync() to fill the entity-specific data for serialization
        // - Remote-side:
        // 1. Reconstruct game object depending on the entity type
        // 2. Call Deserialize() of each SerializationBlock to synchronize the entity
        // 

        internal ulong GetSerializedLength()
        {
            ulong totalSize = GetSyncSerializedLength();
            foreach (var block in serializationBlocks)
            {
                totalSize += block.getLength();
            }

            // Add space for the entity type and number of serialization blocks
            totalSize += sizeof(byte) + sizeof(byte) + (ulong)(sizeof(int) * serializationBlocks.Count);

            return totalSize;
        }

        internal abstract ulong GetSyncSerializedLength();
        internal abstract void PrepareSync(IntPtr objectData);

        internal static (GameObject, SbEntity) Sync(EntityType entityType, IntPtr objectData) => Sync(entityType, objectData, 0);

        /// <param name="length">The sync data's length (0: unknown); a native entity reads its versioned extension only within it.</param>
        internal static (GameObject, SbEntity) Sync(EntityType entityType, IntPtr objectData, ulong length)
        {
            switch (entityType)
            {
                case EntityType.Network:
                    return SbNetworkEntity.SyncRemote(objectData);
                case EntityType.Local:
                    return SbLocalEntity.SyncRemote(objectData);
                case EntityType.NetworkAvatar:
                    return SbNetworkAvatarEntity.SyncRemote(objectData);
                case EntityType.Native:
                    return SbNativeEntity.SyncRemote(objectData, length);
                case EntityType.SceneAnchor:
                    return SbMRUKAnchorEntity.SyncRemote(objectData);
                default:
                    return (null, null);
            }
        }

        private void Reset()
        {
            _spaceManager = null;
            _id = -1;
            _entityTransform = null;
            isSource = false;
            onUpdateDataCallback = null;
            onUpdateStateCallback = null;
            onRemoveCallback = null;
            serializationBlocks.Clear();
        }
    }
}
