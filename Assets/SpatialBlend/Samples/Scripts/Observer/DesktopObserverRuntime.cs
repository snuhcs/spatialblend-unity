// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using Oculus.Interaction;
using SpatialBlend;
using SpatialBlend.Interactions;
#if FUSION2
using MRMotifs.SharedActivities.ChessSample;
using SpatialBlend.Samples.Objects.Whiteboard;
#endif
using UnityEngine;
using UnityEngine.EventSystems;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Desktop observer host on the freeform app. Participants publish their room (space id, HMD, furniture, placed
    /// objects, avatars as realized there). An observer lists those rooms, poses the scene from the one it picked,
    /// and flies or locks to a head in that room. It does not publish.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class DesktopObserverRuntime : MonoBehaviour
    {
        public const float PublishIntervalSeconds = ObserverTraffic.MotionIntervalSeconds;
        public const double RoomTimeoutSeconds = 3d;

        private FreeformCollaborationApp _app;
        private ObserverSessionState _state;
        private ObserverPanel _panel;
        private Camera _camera;
        private bool _interactionLocked;
        private float _nextMotion;
        private float _nextFurniture = -1f;
        private float _nextPresence;
        private float _nextLock;
        private float _nextAuthority;
        private float _allowance = ObserverTraffic.BudgetCapBytes;
        private int _furnitureHash = 1;
        private int _sampledFurnitureHash;
        private NetworkRunner _runner;
        private ObserverRoomReceiver _receiver;
        private FusionConnectionManager _connection;
        private bool _presenceHooked;
        private bool _hostingClosed;
        private SpatialBlendManager _manager;
        private MrukSceneAnchorCatalog _anchorCatalog;
        private readonly ObserverAudience _audience = new ObserverAudience();
        private readonly List<PlayerRef> _recipients = new List<PlayerRef>();
        private int _recipientCursor;
        private readonly List<ObserverFurniture> _furnitureScratch = new List<ObserverFurniture>();
        private readonly List<ObserverPlacedObject> _objectScratch = new List<ObserverPlacedObject>();
        private readonly List<ObserverAvatarPose> _avatarScratch = new List<ObserverAvatarPose>();
        private readonly HashSet<int> _anchorSeen = new HashSet<int>();
        private readonly Dictionary<string, Transform> _furniture = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private Transform _furnitureRoot;

        public ObserverSessionState State => _state;

        public static DesktopObserverRuntime Ensure(FreeformCollaborationApp app)
        {
            if (app == null) return null;
            var runtime = app.GetComponent<DesktopObserverRuntime>();
            if (runtime == null) runtime = app.gameObject.AddComponent<DesktopObserverRuntime>();
            runtime.Bind(app);
            return runtime;
        }

        public void Bind(FreeformCollaborationApp app)
        {
            _app = app;
            if (_state == null) _state = new ObserverSessionState();
        }

        private void OnDestroy()
        {
            if (_presenceHooked && _connection != null) _connection.ObserverAnnounced -= OnObserverAnnounced;
            DetachRunner();
        }

        private void Update()
        {
            if (!Application.isPlaying || _app == null || _state == null) return;
            if (_app.IsDesktopObserver)
            {
                // Announce can fail before the scene object is valid. That must not skip this frame's camera, panel, or expiry.
                try
                {
                    Announce();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
                Observe();
            }
            else Publish();
            MaintainSceneAuthority();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || _app == null || !_app.IsDesktopObserver || _state == null) return;
            ApplyRoom(_state.Rooms.Selected);
            ApplyCamera();
        }

        private void Observe()
        {
            CloseHosting();
            EnsurePresentation();
            MaintainLocks();
            if (!EnsureRunner()) return;
            _state.Expire(Time.unscaledTimeAsDouble, RoomTimeoutSeconds);
            if (_panel != null) _panel.Refresh();
            ReadFlyInput();
        }

        private void Announce()
        {
            if (!EnsureRunner()) return;
            if (Time.unscaledTime < _nextPresence) return;
            _nextPresence = Time.unscaledTime + ObserverTraffic.PresenceIntervalSeconds;
            if (_connection != null) _connection.AnnounceLocalObserver();
        }

        private void Publish()
        {
            if (!EnsureRunner()) return;
            float now = Time.unscaledTime;
            _allowance = ObserverTraffic.Refill(_allowance, Time.unscaledDeltaTime);
            DropExpiredObservers();
            if (_audience.Count == 0 || now < _nextMotion) return;
            bool includeFurniture = _nextFurniture < 0f || now >= _nextFurniture || FurnitureChanged();
            ParticipantRoomSnapshot snapshot = Capture(_runner, includeFurniture);
            _nextMotion = now + ObserverTraffic.MotionIntervalSeconds;
            if (snapshot == null) return;
            // Motion always. Furniture is its own message, and only when it fits; otherwise the motion snapshot is the fallback.
            byte[] motion = ObserverRoomCodec.WriteWithin(WithoutFurniture(snapshot), ObserverTraffic.MaxSnapshotBytes);
            byte[] furniture = null;
            if (includeFurniture)
            {
                byte[] combined = ObserverRoomCodec.Write(snapshot);
                if (combined != null && combined.Length <= ObserverTraffic.MaxSnapshotBytes) furniture = combined;
            }
            SendFresh(motion);
            bool sentFurniture = furniture != null && SendFresh(furniture) > 0;
            if (sentFurniture)
            {
                _nextFurniture = now + ObserverTraffic.FurnitureIntervalSeconds;
                _furnitureHash = _sampledFurnitureHash;
            }
        }

        private static ParticipantRoomSnapshot WithoutFurniture(ParticipantRoomSnapshot snapshot) =>
            snapshot == null || !snapshot.IncludesFurniture ? snapshot
            : ParticipantRoomSnapshot.Create(snapshot.ParticipantId, snapshot.DisplayName, snapshot.SpaceId,
                snapshot.HasHead, snapshot.HeadPosition, snapshot.HeadRotation,
                null, snapshot.Objects, snapshot.Avatars, false);

        /// <summary>
        /// Charges each recipient for this payload alone, starting one observer later than the previous send.
        /// Recipients the bucket cannot pay this time are first in line later; the next capture replaces this one.
        /// </summary>
        private int SendFresh(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > ObserverTraffic.BudgetCapBytes) return 0;
            _recipients.Clear();
            foreach (PlayerRef player in _runner.ActivePlayers)
            {
                if (player == _runner.LocalPlayer || !_audience.Contains(player.PlayerId)) continue;
                _recipients.Add(player);
            }
            int count = _recipients.Count;
            if (count == 0) return 0;
            int sent = ObserverTraffic.SendPerRecipient(ref _allowance, payload.Length, count, ref _recipientCursor, out int start);
            for (int n = 0; n < sent; ++n)
                _runner.SendReliableDataToPlayer(_recipients[(start + n) % count], ObserverRoomChannel.RoomKey(), payload);
            return sent;
        }

        private void OnObserverAnnounced(PlayerRef player)
        {
            if (_app == null || _app.IsDesktopObserver || player == PlayerRef.None) return;
            _audience.Note(player.PlayerId, Time.unscaledTimeAsDouble);
        }

        private void OnRoomBytes(byte[] payload)
        {
            if (_app == null || !_app.IsDesktopObserver || _state == null) return;
            if (!ObserverRoomCodec.TryRead(payload, out ParticipantRoomSnapshot snapshot)) return;
            _state.UpsertRoom(snapshot, Time.unscaledTimeAsDouble);
        }

        private void OnRemoteLeft(PlayerRef player)
        {
            // Snapshot routing only. The connection manager keeps the observer on its own record until this same leave.
            _audience.Forget(player.PlayerId);
        }

        private void DropExpiredObservers()
        {
            // Three seconds drops a quiet observer from the snapshot recipients. It does not forget them for the round.
            _audience.Expire(Time.unscaledTimeAsDouble, ObserverAudience.TimeoutSeconds);
        }

        private void CloseHosting()
        {
            if (_hostingClosed || _app == null || !_app.IsDesktopObserver) return;
            FusionSessionBrowser browser = FindFirstObjectByType<FusionSessionBrowser>();
            if (browser == null) return;
            browser.SetHostingAllowed(false);
            _hostingClosed = true;
        }

        private void EnsurePresentation()
        {
            if (_panel == null) _panel = ObserverPanel.Create(_state);
            if (_camera == null) _camera = EnsureCamera();
        }

        private void MaintainLocks()
        {
            if (_interactionLocked && Time.unscaledTime < _nextLock) return;
            _nextLock = Time.unscaledTime + 1f;
            LockInteraction();
        }

        private void LockInteraction()
        {
            _interactionLocked = true;
            InteractionModeManager modes = FindFirstObjectByType<InteractionModeManager>();
            if (modes != null) modes.enabled = false;
            BoxAnnotationManager boxes = FindFirstObjectByType<BoxAnnotationManager>();
            if (boxes != null) boxes.enabled = false;
            RayInteractable[] rays = FindObjectsByType<RayInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < rays.Length; ++i)
            {
                if (rays[i] != null) rays[i].enabled = false;
            }
        }

        private static Camera EnsureCamera()
        {
            var go = new GameObject("ObserverCamera");
            var camera = go.AddComponent<Camera>();
            camera.depth = 100f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;
            camera.fieldOfView = 72f;
            if (FindFirstObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera other = cameras[i];
                if (other == null || other == camera) continue;
                if (other.CompareTag("MainCamera")) other.tag = "Untagged";
                other.enabled = false;
            }
            go.tag = "MainCamera";
            return camera;
        }

        private void ReadFlyInput()
        {
            if (_state.Mode != ObserverCameraMode.FreeFly) return;
            bool typing = SpectatorCamera.TypingInTextField();
            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            bool blocked = typing || overUi;
            Func<KeyCode, bool> held = SpectatorCamera.HeldKeys(blocked, Input.GetKey);
            bool look = !blocked && Input.GetMouseButton(1);
            _state.StepFly(new ObserverFly.Input
            {
                Forward = held(KeyCode.W),
                Back = held(KeyCode.S),
                Left = held(KeyCode.A),
                Right = held(KeyCode.D),
                Up = held(KeyCode.E) || held(KeyCode.Space),
                Down = held(KeyCode.Q) || held(KeyCode.LeftControl) || held(KeyCode.RightControl),
                Fast = held(KeyCode.LeftShift) || held(KeyCode.RightShift),
                MouseX = look ? Input.GetAxis("Mouse X") : 0f,
                MouseY = look ? Input.GetAxis("Mouse Y") : 0f,
                DeltaTime = Time.unscaledDeltaTime,
            });
        }

        private void ApplyCamera()
        {
            if (_camera == null) return;
            if (!_state.TryCameraPose(out Vector3 position, out Quaternion rotation)) return;
            _camera.transform.SetPositionAndRotation(position, rotation);
        }

        private void ApplyRoom(ParticipantRoomSnapshot room)
        {
            if (room == null)
            {
                SetFurnitureActive(false);
                return;
            }
            ApplyFurniture(room);
            ApplyObjects(room);
            ApplyAvatars(room);
        }

        private void ApplyObjects(ParticipantRoomSnapshot room)
        {
            var byNet = new Dictionary<uint, Transform>();
            var byNative = new Dictionary<string, Transform>(StringComparer.Ordinal);
            NetworkObject[] networked = FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < networked.Length; ++i)
            {
                NetworkObject networkObject = networked[i];
                if (networkObject == null || !networkObject.IsValid) continue;
                byNet[networkObject.Id.Raw] = networkObject.transform;
            }
            SbNativeEntity[] natives = FindObjectsByType<SbNativeEntity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < natives.Length; ++i)
            {
                if (natives[i] == null) continue;
                byNative[ObserverSceneMap.NativeKey(natives[i].gameObject.name)] = natives[i].transform;
            }
            for (int i = 0; i < room.Objects.Count; ++i)
            {
                ObserverPlacedObject placed = room.Objects[i];
                Transform target = null;
                if (ObserverSceneMap.TryParseNetworkKey(placed.Key, out uint raw)) byNet.TryGetValue(raw, out target);
                else byNative.TryGetValue(placed.Key, out target);
                if (target == null || target == _camera.transform) continue;
                target.SetPositionAndRotation(placed.Position, placed.Rotation);
            }
        }

        private void ApplyAvatars(ParticipantRoomSnapshot room)
        {
            SbAvatarBehaviourFusion[] avatars = FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < room.Avatars.Count; ++i)
            {
                ObserverAvatarPose pose = room.Avatars[i];
                Transform root = null;
                for (int a = 0; a < avatars.Length; ++a)
                {
                    SbAvatarBehaviourFusion behaviour = avatars[a];
                    if (behaviour == null || behaviour.Object == null || !behaviour.Object.IsValid) continue;
                    if (ObserverSceneMap.ParticipantId(behaviour.Object.StateAuthority.PlayerId) != pose.ParticipantId) continue;
                    root = behaviour.transform;
                    break;
                }
                if (root == null) continue;
                root.SetPositionAndRotation(pose.RootPosition, pose.RootRotation);
                if (!pose.HasHead) continue;
                SbRetargetTransform retarget = root.GetComponentInChildren<SbRetargetTransform>(true);
                if (retarget != null) retarget.SetHeadTransform(pose.HeadPosition, pose.HeadRotation);
            }
        }

        private void ApplyFurniture(ParticipantRoomSnapshot room)
        {
            if (_furnitureRoot == null)
            {
                var root = new GameObject("ObserverFurniture");
                _furnitureRoot = root.transform;
            }
            _furnitureRoot.gameObject.SetActive(true);
            var live = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < room.Furniture.Count; ++i)
            {
                ObserverFurniture item = room.Furniture[i];
                live.Add(item.Id);
                if (!_furniture.TryGetValue(item.Id, out Transform proxy) || proxy == null)
                {
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "ObserverFurnitureProxy";
                    Collider collider = cube.GetComponent<Collider>();
                    if (collider != null) Destroy(collider);
                    cube.transform.SetParent(_furnitureRoot, false);
                    proxy = cube.transform;
                    _furniture[item.Id] = proxy;
                }
                proxy.gameObject.SetActive(true);
                proxy.SetPositionAndRotation(item.Position, item.Rotation);
                Vector3 size = item.Size;
                proxy.localScale = new Vector3(Mathf.Max(Mathf.Abs(size.x), 0.02f), Mathf.Max(Mathf.Abs(size.y), 0.02f), Mathf.Max(Mathf.Abs(size.z), 0.02f));
            }
            var stale = new List<string>();
            foreach (KeyValuePair<string, Transform> pair in _furniture)
            {
                if (!live.Contains(pair.Key) || pair.Value == null) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; ++i)
            {
                if (_furniture.TryGetValue(stale[i], out Transform proxy) && proxy != null) Destroy(proxy.gameObject);
                _furniture.Remove(stale[i]);
            }
        }

        private void SetFurnitureActive(bool active)
        {
            if (_furnitureRoot != null) _furnitureRoot.gameObject.SetActive(active);
        }

        private bool FurnitureChanged()
        {
            SampleFurniture();
            return _sampledFurnitureHash != _furnitureHash;
        }

        private void SampleFurniture()
        {
            if (_anchorCatalog == null) _anchorCatalog = new MrukSceneAnchorCatalog();
            _furnitureScratch.Clear();
            _anchorSeen.Clear();
            foreach (SceneAnchorRecord record in _anchorCatalog.Anchors)
            {
                if (record == null || record.Anchor == null) continue;
                int instance = record.Anchor.GetInstanceID();
                if (!_anchorSeen.Add(instance)) continue;
                string id = string.IsNullOrEmpty(record.AnchorId) ? "anchor:" + instance.ToString(System.Globalization.CultureInfo.InvariantCulture) : record.AnchorId;
                Transform anchor = record.Anchor.transform;
                Vector3 center = ObserverSceneMap.FurnitureCenter(anchor.position, anchor.rotation, record.LocalCenter);
                _furnitureScratch.Add(new ObserverFurniture(id, record.Label, center, anchor.rotation, record.LocalSize));
            }
            _sampledFurnitureHash = Fingerprint(_furnitureScratch);
        }

        internal static int Fingerprint(List<ObserverFurniture> items)
        {
            int hash = items.Count;
            for (int i = 0; i < items.Count; ++i)
            {
                ObserverFurniture item = items[i];
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(item.Id);
                hash = hash * 31 + Quant(item.Position.x);
                hash = hash * 31 + Quant(item.Position.y);
                hash = hash * 31 + Quant(item.Position.z);
                hash = hash * 31 + Quant(item.Size.x);
                hash = hash * 31 + Quant(item.Size.y);
                hash = hash * 31 + Quant(item.Size.z);
                // A stand-in turned in place keeps its centre: the rotation (its yaw) is part of what changed. The quaternion
                // is hashed, not Euler angles (an anchor frame pitched -90 is at Euler's gimbal lock), with q and -q as one.
                Quaternion r = item.Rotation;
                float sign = r.w < 0f ? -1f : 1f;
                hash = hash * 31 + Quant(r.x * sign);
                hash = hash * 31 + Quant(r.y * sign);
                hash = hash * 31 + Quant(r.z * sign);
                hash = hash * 31 + Quant(r.w * sign);
            }
            return hash;
        }

        private static int Quant(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : (int)Math.Round(value * 1000d);

        private ParticipantRoomSnapshot Capture(NetworkRunner runner, bool includeFurniture)
        {
            if (_manager == null) _manager = FindFirstObjectByType<SpatialBlendManager>();
            long spaceId = _manager != null ? _manager.ID : 0;
            if (spaceId <= 0 || runner.LocalPlayer == PlayerRef.None) return null;
            string self = ObserverSceneMap.ParticipantId(runner.LocalPlayer.PlayerId);
            bool hasHead = TryReadHmd(out Vector3 headPosition, out Quaternion headRotation);

            if (includeFurniture) SampleFurniture();

            _objectScratch.Clear();
            SbNetworkEntity[] networked = FindObjectsByType<SbNetworkEntity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < networked.Length; ++i)
            {
                SbNetworkEntity entity = networked[i];
                if (entity == null) continue;
                NetworkObject networkObject = entity.GetComponent<NetworkObject>();
                if (networkObject == null || !networkObject.IsValid || networkObject.Id.Raw == 0) continue;
                _objectScratch.Add(new ObserverPlacedObject(ObserverSceneMap.NetworkKey(networkObject.Id.Raw), entity.transform.position, entity.transform.rotation));
            }
            SbNativeEntity[] natives = FindObjectsByType<SbNativeEntity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < natives.Length; ++i)
            {
                SbNativeEntity native = natives[i];
                if (native == null || native.GetComponent<NetworkObject>() != null) continue;
                _objectScratch.Add(new ObserverPlacedObject(ObserverSceneMap.NativeKey(native.gameObject.name), native.transform.position, native.transform.rotation));
            }

            _avatarScratch.Clear();
            SbAvatarBehaviourFusion[] behaviours = FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; ++i)
            {
                SbAvatarBehaviourFusion behaviour = behaviours[i];
                if (behaviour == null || behaviour.Object == null || !behaviour.Object.IsValid) continue;
                string id = ObserverSceneMap.ParticipantId(behaviour.Object.StateAuthority.PlayerId);
                bool avatarHead = false;
                Vector3 avatarHeadPosition = Vector3.zero;
                Quaternion avatarHeadRotation = Quaternion.identity;
                SbNetworkAvatarEntity avatarEntity = behaviour.GetComponent<SbNetworkAvatarEntity>();
                if (avatarEntity == null) avatarEntity = behaviour.GetComponentInChildren<SbNetworkAvatarEntity>(true);
                if (avatarEntity != null && avatarEntity.TryGetRealizedAnchorPose(SbRetargetAnchorTargetBridge.HeadAnchor, out avatarHeadPosition, out avatarHeadRotation))
                    avatarHead = true;
                if (id == self && hasHead)
                {
                    avatarHead = true;
                    avatarHeadPosition = headPosition;
                    avatarHeadRotation = headRotation;
                }
                else if (id == self && avatarHead)
                {
                    hasHead = true;
                    headPosition = avatarHeadPosition;
                    headRotation = avatarHeadRotation;
                }
                _avatarScratch.Add(new ObserverAvatarPose(id, "Participant " + id, behaviour.transform.position, behaviour.transform.rotation,
                    avatarHead, avatarHeadPosition, avatarHeadRotation));
            }

            return ParticipantRoomSnapshot.Create(self, "Participant " + self, spaceId, hasHead, headPosition, headRotation,
                includeFurniture ? _furnitureScratch : null, _objectScratch, _avatarScratch, includeFurniture);
        }

        private static bool TryReadHmd(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig == null || rig.centerEyeAnchor == null) return false;
            rig.centerEyeAnchor.GetPositionAndRotation(out position, out rotation);
            return true;
        }

        private bool EnsureRunner()
        {
            if (_connection == null)
            {
                _presenceHooked = false;
                _connection = FindFirstObjectByType<FusionConnectionManager>();
            }
            if (_connection != null)
            {
                _connection.DeclareLocalObserver(_app != null && _app.IsDesktopObserver);
                if (!_presenceHooked)
                {
                    _connection.ObserverAnnounced += OnObserverAnnounced;
                    _presenceHooked = true;
                }
            }
            NetworkRunner runner = _connection != null ? _connection.Runner : null;
            if (runner == _runner) return _runner != null && _runner.IsRunning;
            DetachRunner();
            if (runner == null || !runner.IsRunning) return false;
            _runner = runner;
            _receiver = new ObserverRoomReceiver(OnRoomBytes, OnRemoteLeft, () => _app != null && _app.IsDesktopObserver);
            runner.AddCallbacks(_receiver);
            return true;
        }

        private void MaintainSceneAuthority()
        {
            bool release = _app != null && _app.IsDesktopObserver;
            // No observer in the room: do not scan, and do not request authority. An unheld board stays with the edit lease.
            if (!release && (_connection == null || !_connection.HasKnownObserver)) return;
            if (Time.unscaledTime < _nextAuthority) return;
            _nextAuthority = Time.unscaledTime + 1f;
            bool take = !release && _connection != null && _connection.ParticipantRole == CoreParticipantRole.Host;
            if (!release && !take) return;
#if FUSION2
            ChessBoardHandlerMotif[] chess = FindObjectsByType<ChessBoardHandlerMotif>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < chess.Length; ++i) AdjustAuthority(chess[i], release);
            WhiteboardManagerMotif[] boards = FindObjectsByType<WhiteboardManagerMotif>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < boards.Length; ++i) AdjustAuthority(boards[i], release);
#endif
        }

#if FUSION2
        private void AdjustAuthority(NetworkBehaviour behaviour, bool release)
        {
            if (behaviour == null || behaviour.Object == null || !behaviour.Object.IsValid) return;
            if (release)
            {
                if (behaviour.Object.HasStateAuthority) behaviour.Object.ReleaseStateAuthority();
                return;
            }
            if (behaviour.Object.HasStateAuthority) return;
            PlayerRef holder = behaviour.Object.StateAuthority;
            if (_connection != null && _connection.IsKnownObserver(holder))
                behaviour.Object.RequestStateAuthority();
        }
#endif

        private void DetachRunner()
        {
            if (_runner != null && _receiver != null) _runner.RemoveCallbacks(_receiver);
            _runner = null;
            _receiver = null;
        }
    }

    internal static class ObserverRoomChannel
    {
        public const int Magic = 0x4F425331;
        public const int RoomKind = 1;

        public static ReliableKey RoomKey() => ReliableKey.FromInts(Magic, RoomKind, 0, 0);

        public static bool TryKind(ReliableKey key, out int kind)
        {
            key.GetInts(out int magic, out kind, out _, out _);
            if (magic != Magic) { kind = 0; return false; }
            return kind == RoomKind;
        }
    }

    internal sealed class ObserverRoomReceiver : INetworkRunnerCallbacks
    {
        private readonly Action<byte[]> _onRoom;
        private readonly Action<PlayerRef> _onLeft;
        private readonly Func<bool> _copyRooms;

        public ObserverRoomReceiver(Action<byte[]> onRoom, Action<PlayerRef> onLeft, Func<bool> copyRooms)
        {
            _onRoom = onRoom;
            _onLeft = onLeft;
            _copyRooms = copyRooms;
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
            if (!ObserverRoomChannel.TryKind(key, out _) || data.Array == null || data.Count <= 0) return;
            // A participant drops a room snapshot without copying it. Only an observer keeps the bytes.
            if (_copyRooms == null || !_copyRooms() || _onRoom == null) return;
            var bytes = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, bytes, 0, data.Count);
            _onRoom(bytes);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) => _onLeft?.Invoke(player);

        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    }
}
