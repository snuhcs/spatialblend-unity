// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;
using System.Runtime.InteropServices;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;
using Fusion;
using System.Collections.Generic;
using System.Linq;

namespace SpatialBlend
{
    using EntityID = System.Int64;
    public class SbNetworkAvatarEntity : SbEntity
    {
        public override EntityType EntityType { get => EntityType.NetworkAvatar; }

        /// <summary>
        /// Network-avatar root realization normally addresses the retarget head via
        /// <see cref="SbEntity.EntityTransform"/>.  Route it through the retarget
        /// setter when available so an optional anchor bridge retains the latest
        /// tracked baseline instead of treating this direct transform write as an
        /// opaque override.
        /// </summary>
        public override void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation)
        {
            // The root is written, never steered. A received avatar's root belongs to the embodied driver
            // (design A*, single owner), so a NavMeshAgent here would be a second motion layer: it would
            // approach the driver's pose at its own speed and acceleration. The walkable geometry reaches the
            // driver as its course (sb_query_course), not as a locomotion component on the avatar.
            SbRetargetTransform retarget = GetRetargetTransform();
            if (retarget != null)
            {
                retarget.ApplyRootRealizationTransform(worldPosition, worldRotation);
                return;
            }
            EntityTransform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        /// <summary>
        /// The root the driver samples: where the avatar stands (the retarget head's position) and which way its BODY heads (the heading
        /// the driver last wrote), not the yaw of the visible head. The retarget head is also the avatar's upper body: while Facing is
        /// active its yaw is the Facing yaw, so reporting it as the root's yaw would make the body heading follow the upper body whenever
        /// the avatar stands (the driver keeps a standing root's yaw from this sample) and would feed the core a root yaw that no
        /// relation asked for. Facing turns the upper body, independently of the walking heading.
        /// </summary>
        public override void GetRealizedPose(out Vector3 position, out Quaternion rotation)
        {
            base.GetRealizedPose(out position, out rotation);
            SbRetargetTransform retarget = GetRetargetTransform();
            if (retarget != null && retarget.TryGetBodyPlacement(out Quaternion body)) rotation = body;
        }

        /// <summary>
        /// Dormant, explicit capability for the synchronous application root/direct head.
        /// Quaternion-only SDK bones and generic entity overrides are not admitted by it.
        /// The caller must own every root/head and parent mutation for the lease lifetime.
        /// Existing driver calls do not acquire this capability or activate target events.
        /// </summary>
        internal bool TryAcquireCheckedRealization(object owner,
            SbRetargetTransform.RealizationContract contract,
            out SbRetargetTransform.RealizationLease lease)
        {
            lease = null;
            if (GetType() != typeof(SbNetworkAvatarEntity)) return false;
            SbRetargetTransform retarget = GetRetargetTransform();
            // Keep the existing root sample origin: final head position plus BODY
            // yaw. RootPlacement.position may have a tracked local offset and is not
            // a drop-in replacement. The driver explicitly inverts that offset.
            return retarget != null && ReferenceEquals(EntityTransform, retarget.headTransform) &&
                retarget.TryAcquireCheckedRealization(owner, contract, out lease);
        }

        /// <summary>
        /// The head anchor's chart (the source head's own sample replayed through the shared object that holds it) is kept on the
        /// retarget as state: the raw Meta body, visible during an interaction, is placed by it (ContinuousCompensation via
        /// MetaAvatarEntity.WireChartReference) so it replays the source exactly. Nothing of the Sb avatar is written from it: its head
        /// and hands follow the Facing/Spacing result. State only, so it may run under the manager's entity lock.
        /// </summary>
        internal override void ApplyAnchorCharts(IReadOnlyList<Internal.EmbodiedAnchorChart> charts)
        {
            SbRetargetTransform retarget = GetRetargetTransform();
            if (retarget == null) return;
            for (int i = 0; charts != null && i < charts.Count; ++i)
            {
                if (string.Equals(charts[i].AnchorName, SbRetargetAnchorTargetBridge.HeadAnchor, StringComparison.Ordinal))
                {
                    retarget.SetHeadChart(true, charts[i].Position, charts[i].YawDegrees);
                    return;
                }
            }
            retarget.SetHeadChart(false, default, 0f);
        }

        internal override void ClearAnchorCharts() => GetRetargetTransform()?.ClearHeadChart();

        /// <summary>Nothing to cancel: there is no locomotion pipeline on a received avatar, only the driver's writes.</summary>
        public override void CancelRealizationTarget() { }

        private static readonly string[] HeadOnly = { SbRetargetAnchorTargetBridge.HeadAnchor };

        /// <summary>
        /// ABI 4: the received Meta/OVR avatar samples the root and the head. The
        /// root is <see cref="SbEntity.GetRealizedPose"/>; the head is the retarget head, published through its own
        /// anchor port so the Facing proposal (SB: facing binds to the head or chest) is followed on the upper body.
        /// The pelvis and the hands are not anchors this entity reports: they exist only through the embodiment the driver
        /// registers (ABI 4.7, Avatar/Embodiment), which names a bone, an offset and the families each anchor serves. The checked
        /// driver publishes the root and the direct head only (the bone ports went with the removed proposal-following frame).
        /// An entity without a retarget head samples only the root.
        /// </summary>
        public override IReadOnlyList<string> RealizedAnchorNames =>
            GetRetargetTransform()?.headTransform != null ? HeadOnly : Array.Empty<string>();

        public override bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
        {
            Transform head = string.Equals(anchorName, SbRetargetAnchorTargetBridge.HeadAnchor, StringComparison.Ordinal)
                ? GetRetargetTransform()?.headTransform
                : null;
            if (head == null)
            {
                position = default;
                rotation = Quaternion.identity;
                return false;
            }
            head.GetPositionAndRotation(out position, out rotation);
            return true;
        }

        /// <summary>The preset the avatar behaviour states, when the avatar is a preset avatar.</summary>
        public override int? AvatarPresetIndex
        {
            get
            {
                var behaviour = _avatarBehaviour != null ? _avatarBehaviour : GetComponent<SbAvatarBehaviourFusion>();
                return behaviour != null && behaviour.Object != null && behaviour.Object.IsValid && behaviour.UsePresetAvatar
                    ? behaviour.LocalAvatarIndex
                    : (int?)null;
            }
        }

        internal SbRetargetTransform CheckedRetarget => GetRetargetTransform();

        private SbRetargetTransform GetRetargetTransform()
        {
            if (_sbAvatar != null && _sbAvatar._retargetTransform != null)
            {
                return _sbAvatar._retargetTransform;
            }
            var metaAvatar = GetComponent<MetaAvatarEntity>();
            return metaAvatar != null && metaAvatar._sbAvatar != null
                ? metaAvatar._sbAvatar._retargetTransform
                : null;
        }
        
        // Component references
        private SbAvatarBehaviourFusion _avatarBehaviour;
        private SbAvatarEntity _sbAvatar;
        private Transform _localTransform;

        /// <summary>
        /// Called when the component is added or reset in the Editor.
        /// Sets avatar-specific defaults.
        /// </summary>
        private void Reset()
        {
            // Avatars should not be manipulated or placed by default
            _allowManipulation = false;
            _placementMotifType = PlacementMotifType.None;
        }
        // The kinetic bounds of a received avatar are not set here: they are EmbodiedDriverConfig, declared once.
        // The avatar is placed only by the core's typed target events (a placement request, a source exception's lifecycle
        // operation), realized by the checked driver; no field here teleports it.

        // Hysteresis thresholds for filtering unintentional movements (sending side)
        // Start threshold is higher than stop threshold to prevent on/off flickering
        [Header("Movement Detection (Hysteresis)")]
        [SerializeField] private float _positionStartThreshold = 0.3f;  // m/s - start updating when above this
        [SerializeField] private float _positionStopThreshold = 0.1f;   // m/s - stop updating when below this
        [SerializeField] private float _yawStartThreshold = 10.0f;      // deg/s - start updating when above this
        [SerializeField] private float _yawStopThreshold = 3.0f;        // deg/s - stop updating when below this
        
        // Previous frame values for velocity calculation
        private Vector3 _prevPosition;
        private float _prevYaw;
        private bool _prevValuesInitialized = false;
        
        // Movement state (hysteresis)
        private bool _isPositionMoving = false;
        private bool _isYawRotating = false;

        // Euclidean2D interaction. While set, UpdateStates bypasses hysteresis.
        private bool _isInteracting = false;

        public override void Start()
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
            }
            _avatarBehaviour = GetComponent<SbAvatarBehaviourFusion>();
            if (_avatarBehaviour == null)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, "SbNetworkAvatarEntity::Start - SbAvatarBehaviourFusion component not found!");
                return;
            }
            _sbAvatar = GetComponent<MetaAvatarEntity>()._sbAvatar;

            this.SetOnInteractionCallback(new OnInteractionDelegate(OnAvatarInteraction));

            OVRCameraRig cameraRig = FindObjectsByType<OVRCameraRig>(FindObjectsSortMode.None).FirstOrDefault();
            if (cameraRig == null)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::Start - OVRCameraRig not found in {gameObject.name}");
                return;
            }
            _localTransform = cameraRig.centerEyeAnchor.transform;
            SetTransform(_localTransform);
        }

        private void Update()
        {
            // ZoneBased uses the declared region, not relation events.
            if (IsZoneBasedMode)
            {
                OnTestAvatarInteraction();
            }
        }



        /// <summary>
        /// Override UpdateStates to apply One-Euro Filter for network avatar.
        /// This smooths out jitter from head tracking while maintaining responsiveness.
        /// </summary>
        public override void UpdateStates(HashSet<StateType> stateTypes, bool forceUpdate = false)
        {
            if (_entityTransform == null)
            {
                SetTransform(transform);
            }

            bool transformChanged = _entityTransform != null && _entityTransform.hasChanged;
            if (!AvatarStateUpdateFilter.PassesEarlyGate(IsAttached(), transformChanged, forceUpdate))
            {
                return;
            }

            Vector3 currentPosition = _entityTransform.position;
            float currentYaw = _entityTransform.eulerAngles.y;
            float dt = Time.deltaTime > 0 ? Time.deltaTime : (1f / 60f);

            if (!_prevValuesInitialized)
            {
                _prevPosition = currentPosition;
                _prevYaw = currentYaw;
                _prevValuesInitialized = true;
            }

            float positionVelocity = Vector3.Distance(currentPosition, _prevPosition) / dt;
            float yawVelocity = Mathf.Abs(Mathf.DeltaAngle(currentYaw, _prevYaw)) / dt;
            _prevPosition = currentPosition;
            _prevYaw = currentYaw;

            // Interaction bypasses hysteresis only after the change gate.
            AvatarStateUpdateFilter.StepResult step = AvatarStateUpdateFilter.Step(
                _isPositionMoving,
                _isYawRotating,
                positionVelocity,
                yawVelocity,
                _positionStartThreshold,
                _positionStopThreshold,
                _yawStartThreshold,
                _yawStopThreshold,
                _isInteracting,
                forceUpdate);
            _isPositionMoving = step.PositionMoving;
            _isYawRotating = step.YawRotating;

            if (step.Send)
            {
                spaceManager.UpdateV2EntityState(this);
            }

            _entityTransform.hasChanged = false;
        }

        internal override ulong GetSyncSerializedLength()
        {
            // We need space for the NetworkObject's NetworkId (4 bytes) + position data (12 bytes: x, y, z as floats)
            return sizeof(uint) + (sizeof(float) * 3);
        }

        internal override void PrepareSync(IntPtr objectData) // local에서 1. NetworkId로 metaavatar 찾기 2. 여기 cameraRig로 transform 찾기
        {
            NetworkObject networkObject = GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::PrepareSync - NetworkObject not found in {gameObject.name}");
                return;
            }
            uint networkId = networkObject.Id.Raw;

            Vector3 sourceWorldPosition = _localTransform.position;

            // Store the source entity's position in the object data
            Marshal.WriteInt32(objectData, 0, (int)networkId);
            Marshal.WriteInt32(objectData, 4, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.x), 0));
            Marshal.WriteInt32(objectData, 8, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.y), 0));
            Marshal.WriteInt32(objectData, 12, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.z), 0));

            SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::PrepareSync - Preparing to sync {gameObject.name} with NetworkId {networkId} at world position {sourceWorldPosition}");
        }

        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData) // remote에서 1. NetworkId로 newObject에 해당하는 metaAvatar 찾기 2. 여기 transform -> SbRetargetTransform으로 3. newEntity는 metaavatar에 있음..
        {
            uint receivedNetworkId = (uint)Marshal.ReadInt32(objectData, 0);

            GameObject newObject = null;
            SbEntity newEntity = null;

            UnityMainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                newObject = FindObjectByNetworkId(receivedNetworkId);
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote {receivedNetworkId} matched with {newObject?.name}");

                if (newObject != null)
                {
                    // Set the object to the exact position of the source entity
                    var metaAvatar = newObject.GetComponent<MetaAvatarEntity>();
                    if (metaAvatar == null)
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote - Object {newObject.name} is not a MetaAvatarEntity");
                        return;
                    }
                    var sbAvatar = metaAvatar._sbAvatar;
                    if (sbAvatar == null)
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote - sbAvatar not initialized");
                        return;
                    }
                    var headTransform = sbAvatar._retargetTransform.headTransform;
                    if (headTransform == null)
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote - headTransform not initialized");
                        return;
                    }
                    newEntity = newObject.GetComponent<SbEntity>();
                    if (newEntity == null)
                    {
                        SpatialBlendDebug.LogError(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote - Object {newObject.name} is not a sbentity");
                        return;
                    }
                    newEntity.SetTransform(headTransform);

                    // Change newObject to the SbAvatarEntity
                    newObject = sbAvatar.gameObject;

                    // Disable collision detection on the found object to prevent physics issues
                    DisableCollisionDetectionOnFoundObject(newObject);

                    SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::SyncRemote - Synced object {newObject.name}");
                }
            }).Wait();
            return (newObject, newEntity);
        }

        /// <summary>
        /// Disables collision detection on a found object to prevent physics issues
        /// </summary>
        private static void DisableCollisionDetectionOnFoundObject(GameObject foundObject)
        {
            // Disable Rigidbody if present
            var rigidbody = foundObject.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.isKinematic = true;
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::DisableCollisionDetectionOnFoundObject - Disabled Rigidbody on {foundObject.name}");
            }

            // Disable Collider if present
            var collider = foundObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::DisableCollisionDetectionOnFoundObject - Disabled Collider on {foundObject.name}");
            }

            // Disable CharacterController if present
            var characterController = foundObject.GetComponent<CharacterController>();
            if (characterController != null)
            {
                characterController.enabled = false;
                SpatialBlendDebug.Log(LogCategory.Scene, $"SbNetworkAvatarEntity::DisableCollisionDetectionOnFoundObject - Disabled CharacterController on {foundObject.name}");
            }

            // Set to Avatar layer to prevent any collision(for further usage)
            foundObject.layer = LayerMask.NameToLayer("Avatar");
        }

        private static GameObject FindObjectByNetworkId(uint networkId)
        {
            // No method that finds NetworkObject by NetworkId directly, so we have to iterate through all NetworkObjects
            NetworkObject[] allObjects = FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);
            foreach (NetworkObject netobj in allObjects)
            {
                if (netobj.Id.Raw == networkId)
                {
                    return netobj.gameObject;
                }
            }
            return null;
        }

        // As v1 SbNetworkAvatarEntity.
        private bool IsZoneBasedMode =>
            SpatialBlendManager.CurrentSettings != null &&
            SpatialBlendManager.CurrentSettings.avatarDisplayMode == AvatarDisplayMode.ZoneBased;

        // ZoneBased is the paper's fixed-zone baseline: instant switching on the
        // declared Containment Region, with no hysteresis. The region is the
        // SbSpatialAuthoring declaration, never a scene volume component.
        private readonly DeclaredRegionZone.Switch _zoneSwitch = new DeclaredRegionZone.Switch();

        /// <summary>
        /// ZoneBased display flag: the core's interaction value, with no point test of the app's own. For a received avatar
        /// it is the TRANSFER flag of its root proposal AND that anchor's ARRIVED,
        /// so it switches on the realised body; it is false where this peer has no congruent counterpart
        /// of the leaf (the flag stays clear), as v1 had nothing to be inside when the volume was not in the scene. For the
        /// local avatar it is the source-side transfer fact the core computed on its latest update.
        /// </summary>
        public void OnTestAvatarInteraction()
        {
            if (_entityTransform == null || _avatarBehaviour == null)
            {
                return;
            }

            bool changed = _zoneSwitch.Set(spaceManager != null && spaceManager.IsTransferInteracting(this));
            if (changed)
            {
                _avatarBehaviour.SetInteractionState(IsSource, _zoneSwitch.Inside);
            }
        }

        private void OnAvatarInteraction(InteractionEventType eventType, EntityID toEntityId, ProximityType proximityType, ulong interactionTime)
        {
            // As v1: ZoneBased ignores the core interaction event. Default
            // mode reads it, and it is the frame-to-frame change of the interaction value (the transfer
            // flag AND the root's ARRIVED), switching with the realised body.
            if (IsZoneBasedMode)
            {
                return;
            }

            if (proximityType == ProximityType.Euclidean2D)
            {
                switch (eventType)
                {
                    case InteractionEventType.Start:
                        _isInteracting = true;
                        _avatarBehaviour.SetInteractionState(IsSource, true);
                        break;
                    case InteractionEventType.Terminate:
                        _isInteracting = false;
                        _avatarBehaviour.SetInteractionState(IsSource, false);
                        break;
                }
            }
        }
    }
}
