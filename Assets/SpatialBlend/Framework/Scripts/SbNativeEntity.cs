// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;
using System.Text;
using System.Runtime.InteropServices;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;
using SpatialBlend.Objects;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.Scanning;

namespace SpatialBlend
{
    using EntityID = System.Int64;
    public class SbNativeEntity : SbEntity
    {
        public override EntityType EntityType { get => EntityType.Native; }
        private byte[] _contentBytes = new byte[0];
        [NonSerialized] private bool _isRemoteReplica;
        private long _contentEpoch;
        [NonSerialized] private bool _retiredReconstructedReplica;
        /// <summary>
        /// Raised when serialized content bytes become available. A loader may
        /// raise it again after its own work completes; neither notification
        /// promises that renderable geometry is fully instantiated.
        /// </summary>
        public event Action<SbNativeEntity> ContentReady;
        /// <summary>True only for a GameObject reconstructed from a peer payload.</summary>
        public bool IsRemoteReplica => _isRemoteReplica;
        [SerializeField] private NativeContentType _contentType = NativeContentType.GLTF;
        [NonSerialized] private ScanBoxRegion _authoredBox;
        /// <summary>
        /// The annotated box this content was scanned from, in this object's local frame. It rides with the
        /// shared content (second serialization block), so the receiver writes the same spatial authoring
        /// the source wrote. Null for content that was not scanned from a box.
        /// </summary>
        public ScanBoxRegion AuthoredBox => _authoredBox;
        [NonSerialized] private Vector3 _sharedScale = Vector3.one;
        [NonSerialized] private bool _hasSharedScale;
        public void SetContentType(NativeContentType contentType) => _contentType = contentType;

        /// <summary>
        /// The scale the source fitted the object to its annotated box with, which the peers apply
        /// (v1 SbNativeEntity.SetSharedScale). The paper recovers metric scale from the user's
        /// box, so a peer's copy is the size of the source's only when it takes this scale.
        /// </summary>
        public void SetSharedScale(Vector3 scale)
        {
            _sharedScale = scale;
            _hasSharedScale = true;
        }

        public bool TryGetSharedScale(out Vector3 scale)
        {
            scale = _hasSharedScale ? _sharedScale : Vector3.one;
            return _hasSharedScale;
        }
        public NativeContentType GetContentType() => _contentType;

        [NonSerialized] private Quaternion _uprightRotation = Quaternion.identity;
        [NonSerialized] private bool _hasUprightRotation;

        /// <summary>
        /// The model's upright correction: its geometry's rotation in the (level) root, the part of the server's pose that stands a
        /// reconstruction up (<see cref="ScanObjectFit.LevelRoot"/>). It rides with the sync data so a peer's copy stands as the
        /// source's does (a Z-up model would otherwise arrive lying on its side).
        /// </summary>
        public void SetUprightRotation(Quaternion rotation)
        {
            _uprightRotation = rotation;
            _hasUprightRotation = true;
        }

        public bool TryGetUprightRotation(out Quaternion rotation)
        {
            rotation = _hasUprightRotation ? _uprightRotation : Quaternion.identity;
            return _hasUprightRotation;
        }

        /// <summary>A receiver's model is instantiated and fitted (its loader finished); false until then, and for a source.</summary>
        public bool ModelLoaded { get; private set; }

        public override void Awake()
        {
            base.Awake();
            EnsureSerializationBlocks();
        }

        [NonSerialized] private bool _hasSerializationBlocks;

        /// <summary>
        /// Adds the content block and then the box block, once. Awake does it, and so does the reconstruction of a
        /// peer's object right after the template is made, so the block layout never depends on when Awake runs
        /// (EditMode never runs it). The box block is always the second, empty when there is no box, so both
        /// peers agree on the layout.
        /// </summary>
        internal void EnsureSerializationBlocks()
        {
            if (_hasSerializationBlocks) return;
            _hasSerializationBlocks = true;
            AddSerializationBlock(ObjectSerializer.CreateContentBlock(this));
            AddSerializationBlock(ObjectSerializer.CreateBoxRegionBlock(this));
        }

        /// <summary>
        /// Records the annotated box and writes its declaration into this object's SbSpatialAuthoring. The
        /// source calls it before the object is shared. A receiver calls it from the shared block, before its
        /// own authoring is compiled.
        /// </summary>
        public void SetAuthoredBox(ScanBoxRegion region)
        {
            if (_retiredReconstructedReplica) return;
            _authoredBox = region;
            if (region != null) BoxAnnotationAuthoring.Apply(gameObject, region);
        }

        public void SetContentData(byte[] data)
        {
            if (_retiredReconstructedReplica) return;
            ++_contentEpoch;
            _contentBytes = data ?? new byte[0];
            if (_contentBytes.Length > 0)
            {
                ContentReady?.Invoke(this);
            }
        }
        public byte[] GetContentData() => _contentBytes;
        internal void MarkRemoteReplica() => _isRemoteReplica = true;
        internal void RetireReconstructedReplica()
        {
            if (!_isRemoteReplica) return;
            _retiredReconstructedReplica = true;
            ++_contentEpoch;
            _contentBytes = Array.Empty<byte>();
            ContentReady = null;
        }

        /// <summary>
        /// Marks the upright extension after the v1 layout ("UPR1"). A v1 receiver reads the v1 fields and ignores what follows; a
        /// receiver of a v1 payload (too short for the extension, or no tag) keeps the identity.
        /// </summary>
        internal const int UprightExtensionTag = 0x31525055;
        internal const int UprightExtensionLength = sizeof(int) + sizeof(float) * 4;

        internal override ulong GetSyncSerializedLength()
        {
            // namelength(int) + name(in bytes) + contentType(int) + shared scale(float x3), the v1 layout, then the upright extension
            return sizeof(int) + (ulong)Encoding.UTF8.GetByteCount(gameObject.name) + sizeof(int)
                + sizeof(float) * 3 + UprightExtensionLength;
        }

        internal override void PrepareSync(IntPtr objectData)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(gameObject.name);
            int offset = 0;
            Marshal.WriteInt32(objectData, offset, nameBytes.Length);
            offset += sizeof(int);
            if (nameBytes.Length > 0)
            {
                Marshal.Copy(nameBytes, 0, IntPtr.Add(objectData, offset), nameBytes.Length);
                offset += nameBytes.Length;
            }
            Marshal.WriteInt32(objectData, offset, (int)GetContentType());
            offset += sizeof(int);

            // v1 sent the object's own scale when no box fitted one.
            Vector3 sharedScale = _hasSharedScale ? _sharedScale : transform.localScale;
            Marshal.WriteInt32(objectData, offset, BitConverter.SingleToInt32Bits(sharedScale.x));
            offset += sizeof(float);
            Marshal.WriteInt32(objectData, offset, BitConverter.SingleToInt32Bits(sharedScale.y));
            offset += sizeof(float);
            Marshal.WriteInt32(objectData, offset, BitConverter.SingleToInt32Bits(sharedScale.z));
            offset += sizeof(float);
            Quaternion upright = _hasUprightRotation ? _uprightRotation : Quaternion.identity;
            Marshal.WriteInt32(objectData, offset, UprightExtensionTag);
            offset += sizeof(int);
            foreach (float f in new[] { upright.x, upright.y, upright.z, upright.w })
            {
                Marshal.WriteInt32(objectData, offset, BitConverter.SingleToInt32Bits(f));
                offset += sizeof(float);
            }
            SpatialBlendDebug.Log(LogCategory.Scene, $"SpawnEntity::PrepareSync - Preparing to sync {gameObject.name}, scale={sharedScale}");
        }

        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData) => SyncRemote(objectData, 0);

        /// <param name="length">The sync data's length; 0 when unknown (then only the v1 layout is read).</param>
        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData, ulong length)
        {
            int offset = 0;
            int objectNameLength = Marshal.ReadInt32(objectData, offset);
            offset += sizeof(int);
            string objectName = "SyncedObject";
            if (objectNameLength > 0)
            {
                byte[] objectNameBytes = new byte[objectNameLength];
                Marshal.Copy(IntPtr.Add(objectData, offset), objectNameBytes, 0, objectNameLength);
                objectName = Encoding.UTF8.GetString(objectNameBytes);
                offset += objectNameLength;
            }
            int typeInt = Marshal.ReadInt32(objectData, offset);
            offset += sizeof(int);
            NativeContentType contentType = (NativeContentType)typeInt;
            float scaleX = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(objectData, offset));
            offset += sizeof(float);
            float scaleY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(objectData, offset));
            offset += sizeof(float);
            float scaleZ = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(objectData, offset));
            offset += sizeof(float);
            var sharedScale = new Vector3(scaleX, scaleY, scaleZ);
            Quaternion? upright = ReadUprightExtension(objectData, offset, length);

            GameObject newObject = null;
            SbEntity newEntity = null;

            UnityMainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                NativeLoader loader = NativeLoader.GetLoader(contentType);
                if (loader == null)
                {
                    SpatialBlendDebug.LogError(LogCategory.Scene, $"SpawnEntity::SyncRemote - Loader not found");
                    return;
                }
                // Creating a template is needed as sbentity is monobehaviour
                newObject = loader.CreateTemplate(objectName);
                // As v1, the replica waits at a random
                // floor point of the room until its state arrives.
                newObject.transform.SetPositionAndRotation(ComputeRandomStagingPosition(), Quaternion.identity);
                var nativeEntity = newObject.GetComponent<SbNativeEntity>();
                nativeEntity.EnsureSerializationBlocks();
                nativeEntity.SetContentType(contentType);
                nativeEntity.SetSharedScale(sharedScale);
                if (upright.HasValue) nativeEntity.SetUprightRotation(upright.Value);
                nativeEntity.MarkRemoteReplica();
                newEntity = nativeEntity;
            }).Wait();
            SpatialBlendDebug.Log(LogCategory.Scene, $"SpawnEntity::SyncRemote - Synced object {objectName}");
            return (newObject, newEntity);
        }

        // The upright extension at `offset`, when the payload is long enough and tagged; null for a v1 payload or a bad quaternion.
        internal static Quaternion? ReadUprightExtension(IntPtr objectData, int offset, ulong length)
        {
            if (length < (ulong)(offset + UprightExtensionLength)) return null;
            if (Marshal.ReadInt32(objectData, offset) != UprightExtensionTag) return null;
            offset += sizeof(int);
            var v = new float[4];
            for (int i = 0; i < 4; i++)
            {
                v[i] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(objectData, offset));
                offset += sizeof(float);
            }
            var q = new Quaternion(v[0], v[1], v[2], v[3]);
            float norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(norm) || norm < 0.5f || norm > 1.5f) return null;
            return new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm);
        }

        public void LoadContentAsync()
        {
            if (_retiredReconstructedReplica || _contentBytes == null || _contentBytes.Length == 0) return;
            long loadEpoch = _contentEpoch;
            byte[] loadBytes = _contentBytes;
            UnityMainThreadDispatcher.RunOnMainThreadAsync(async () =>
            {
                NativeLoader loader = NativeLoader.GetLoader(_contentType);
                if (loader != null && IsContentLoadCurrent(loadEpoch, loadBytes))
                {
                    SpatialBlendDebug.Log(LogCategory.Scene, $"SpawnEntity::LoadContent - Loading content type: {_contentType}");
                    // The portal effect marks the object that is arriving from another participant while it loads.
                    // The scene may carry no manager (a test or a bare scene), and then nothing is shown.
                    Sam3dPendingIndicatorManager indicators = UnityEngine.Object.FindAnyObjectByType<Sam3dPendingIndicatorManager>();
                    Func<System.Threading.Tasks.Task<bool>> load = () => loader.TryLoadContentAsync(this.gameObject, loadBytes,
                        () => IsContentLoadCurrent(loadEpoch, loadBytes));
                    bool loaded = indicators != null
                        ? await indicators.RunWithSharedObjectPortal(
                            gameObject.GetInstanceID(), gameObject.name, transform.position, transform.rotation, load)
                        : await load();
                    if (loaded && IsContentLoadCurrent(loadEpoch, loadBytes))
                    {
                        ModelLoaded = true;
                        // The collider was just fitted to the model: a declaration that reads it is resolved again.
                        var authoring = GetComponent<SbSpatialAuthoring>();
                        if (authoring != null) authoring.InvalidateRegion();
                        ContentReady?.Invoke(this);
                    }
                }
                else if (loader == null)
                {
                    SpatialBlendDebug.LogError(LogCategory.Scene, $"SpawnEntity::LoadContent - No loader found for type: {_contentType}");
                }

            });

        }

        /// <summary>v1 RemoteStagingMinDistanceToSurface: MRUK keeps the point this far from any surface.</summary>
        internal const float RemoteStagingMinDistanceToSurface = 0.3f;

        /// <summary>v1 :221: the staging point sits this far above the floor so the object is not stuck in it.</summary>
        internal const float RemoteStagingFloorLift = 0.05f;

        /// <summary>
        /// Where a replica waits (v1 :203-222): MRUK's random point in the room, dropped to the floor plus a small
        /// lift (MRUK returns a 3D point that can be mid-air). Without a room the origin, as in v1.
        /// </summary>
        internal static Vector3 StagingPosition(Vector3? randomRoomPoint, float floorY)
        {
            if (!randomRoomPoint.HasValue) return Vector3.zero;
            Vector3 point = randomRoomPoint.Value;
            point.y = floorY + RemoteStagingFloorLift;
            return point;
        }

        private static Vector3 ComputeRandomStagingPosition()
        {
            Meta.XR.MRUtilityKit.MRUK mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
            Meta.XR.MRUtilityKit.MRUKRoom room = mruk != null ? mruk.GetCurrentRoom() : null;
            Vector3? point = room != null
                ? room.GenerateRandomPositionInRoom(RemoteStagingMinDistanceToSurface, true)
                : null;
            if (!point.HasValue)
            {
                SpatialBlendDebug.LogWarning(LogCategory.Scene,
                    "[SbNativeEntity] No random room position from MRUK; using the origin for remote staging.");
            }
            float floorY = room != null && room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
            return StagingPosition(point, floorY);
        }

        private bool IsContentLoadCurrent(long epoch, byte[] bytes)
        {
            return !_retiredReconstructedReplica && this != null && gameObject != null &&
                   _contentEpoch == epoch && ReferenceEquals(_contentBytes, bytes);
        }
    }
}
