// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Shared MRUK scene anchor. The payload is the anchor name and the semantic label.
    /// The entity is attached to the element the scene-geometry adapter already created
    /// for this anchor (see <see cref="SceneGeometryKey"/>); it never creates a second
    /// one. Its <see cref="SbSpatialAuthoring"/> declares the table top as the Tabletop
    /// support surface and the region as that top plus the 0.5 m fallback margin, the same way on every
    /// device.
    /// </summary>
    public class SbMRUKAnchorEntity : SbEntity
    {
        public const string TableLabel = "TABLE";
        public const string TabletopAffordanceId = SceneAnchorSpatialAuthoring.TabletopAffordanceId;

        public override EntityType EntityType => EntityType.SceneAnchor;

        [SerializeField] private string _sceneLabel = string.Empty;
        private GameObject _affordanceHost;
        private Vector3 _localCenter;
        private Vector3 _localSize;
        private bool _hasSupport;

        public string SceneLabel => _sceneLabel;

        // A seat (a COUCH anchor with a measured plane): the user's link key, the measured outline, and the confirmed front.
        private string _seatKey;
        private Vector2[] _seatOutline;
        private float? _seatFrontDegrees;
        // A replica of a peer's seat carries only what its name says.
        private bool _replicaFrontConfirmed;

        public bool IsSeat => !string.IsNullOrEmpty(_seatKey);
        public string SeatKey => _seatKey;
        public bool SeatFrontConfirmed => _seatFrontDegrees.HasValue || _replicaFrontConfirmed;
        public float? SeatFrontDegrees => _seatFrontDegrees;
        public Vector2[] SeatOutline => _seatOutline;

        // The key the sharer gave an anchor that is not a seat (a seat's key is its seat key), and the front a replica's sharer confirmed.
        private string _shareKey;
        private float? _replicaFrontDegrees;
        private bool _measuredKnown;
        private bool _addedCollider;
        // This is set only for a receiver's unmatched shared anchor. It changes
        // presentation, never the support colliders or spatial declaration.
        private bool _receiverBoxPresentationHidden;
        private string _originalName;   // the MRUK anchor's own name, put back when the registration is taken off

        /// <summary>The key the sharer chose for this anchor, or empty for an anchor shared without one.</summary>
        public string ShareKey => IsSeat ? _seatKey : (_shareKey ?? string.Empty);
        /// <summary>The front the sharer of a seat confirmed (anchor-local plane angle) when this is a replica of it, or null.</summary>
        public float? ReplicaFrontDegrees => _replicaFrontDegrees;
        /// <summary>The measured volume box in the anchor's own frame, as registered here or as the sharer measured it (replica).</summary>
        public bool TryGetMeasuredBox(out Vector3 center, out Vector3 size)
        {
            center = _localCenter;
            size = _localSize;
            return _measuredKnown;
        }

        /// <summary>The element affordance id this anchor's measured plane is: the seat surface, or the tabletop.</summary>
        public string PlaneAffordanceId => IsSeat ? SceneAnchorSpatialAuthoring.SeatAffordanceId : TabletopAffordanceId;
        public GameObject AffordanceHost => _affordanceHost;

        /// <summary>
        /// The key the scene-geometry adapter registered this anchor under. The manager looks
        /// up the adapter's elements by it. Zero means the anchor has no adapter geometry.
        /// </summary>
        public int SceneGeometryKey { get; private set; }

        public void SetSceneLabel(string label) => _sceneLabel = label ?? string.Empty;
        public void SetSceneGeometryKey(int key) => SceneGeometryKey = key;

        public override void Awake()
        {
            _allowManipulation = false;
            _placementMotifType = PlacementMotifType.None;
            base.Awake();
        }

        private void OnDestroy()
        {
            if (_affordanceHost != null)
            {
                if (Application.isPlaying) Destroy(_affordanceHost);
                else DestroyImmediate(_affordanceHost);
                _affordanceHost = null;
            }
        }

        /// <summary>
        /// Box, world-aligned SURROUND affordance, and wire box. Idempotent.
        /// </summary>
        public void ApplySupport(Vector3 localCenter, Vector3 localSize)
        {
            _localCenter = localCenter;
            _localSize = localSize;
            _measuredKnown = true;
            _hasSupport = true;
            EnsureSupportVisuals();
            EnsureSpatialAuthoring();
        }

        /// <summary>
        /// Registers an anchor the user chose to share: its measured box, and the key the sharer gave it. The wire name is
        /// <c>LABEL#key@box</c> (<see cref="SharedAnchorName"/>), so the receiver sees the label and the measured size without a new payload.
        /// </summary>
        public bool ApplySharedSupport(Vector3 localCenter, Vector3 localSize, string key)
        {
            if (string.IsNullOrEmpty(_sceneLabel) || !SharedAnchorName.IsValidKey(key)) return false;
            _shareKey = key;
            if (_originalName == null) _originalName = gameObject.name;
            gameObject.name = SharedAnchorName.Compose(SharedAnchorName.Head(_sceneLabel, key), localCenter, localSize, null);
            ApplySupport(localCenter, localSize);
            return true;
        }

        /// <summary>
        /// Registers this anchor as a seat: the box and the plane outline it was measured with, the user's link key, and the
        /// confirmed front (null until confirmed). Idempotent; the name is the seat's identity on the wire.
        /// </summary>
        public bool ApplySeatSupport(Vector3 localCenter, Vector3 localSize, Vector2[] planeOutline, string linkKey,
                                     float? frontDegrees)
        {
            if (!SeatIdentity.IsValidKey(linkKey) || planeOutline == null || planeOutline.Length < 3) return false;
            _localCenter = localCenter;
            _localSize = localSize;
            _seatKey = linkKey;
            _seatOutline = (Vector2[])planeOutline.Clone();
            _seatFrontDegrees = frontDegrees;
            _hasSupport = true;
            _measuredKnown = true;
            if (_originalName == null) _originalName = gameObject.name;
            gameObject.name = SharedAnchorName.Compose(SeatIdentity.Compose(linkKey, frontDegrees.HasValue), localCenter, localSize, frontDegrees);
            EnsureSupportVisuals();
            return EnsureSpatialAuthoring();
        }

        /// <summary>
        /// Declares the anchor's spatial authoring through <see cref="SceneAnchorSpatialAuthoring"/>,
        /// the one TABLE helper. The table top is the Tabletop support surface and the footprint. The
        /// region is the footprint plus the 0.5 m fallback margin. Receiver and
        /// source build the identical declaration from their own box. Idempotent.
        /// </summary>
        public bool EnsureSpatialAuthoring()
        {
            if (!_hasSupport || string.IsNullOrEmpty(_sceneLabel))
            {
                return false;
            }
            if (IsSeat)
            {
                return SceneAnchorSpatialAuthoring.ApplySeat(gameObject, _localCenter, _localSize, _seatOutline, _seatKey,
                    _seatFrontDegrees) != null;
            }
            return SceneAnchorSpatialAuthoring.Apply(gameObject, _localCenter, _localSize) != null;
        }

        /// <summary>
        /// Hides only the receiver's unmatched-anchor box renderers. Its support
        /// colliders, affordance host, spatial declaration, and placement remain
        /// live so the shared referent can still be installed and paired.
        /// </summary>
        public void HideReceiverBoxPresentation()
        {
            _receiverBoxPresentationHidden = true;
            ApplyReceiverBoxPresentationPolicy();
        }

        public void EnsureSupportVisuals()
        {
            if (!_hasSupport)
            {
                return;
            }
            if (_affordanceHost != null)
            {
                ApplyReceiverBoxPresentationPolicy();
                return;
            }

            var box = GetComponent<BoxCollider>();
            if (box == null)
            {
                box = gameObject.AddComponent<BoxCollider>();
                _addedCollider = true;
            }
            box.center = _localCenter;
            box.size = _localSize;

            Vector3 worldCenter = transform.TransformPoint(_localCenter);
            Vector3 worldSize = WorldAlignedSize(transform.rotation, _localSize);
            _affordanceHost = new GameObject(gameObject.name + "_AffordanceHost");
            _affordanceHost.transform.SetParent(transform, true);
            _affordanceHost.transform.position = worldCenter;
            _affordanceHost.transform.rotation = Quaternion.identity;

            var hostCollider = _affordanceHost.AddComponent<BoxCollider>();
            hostCollider.center = Vector3.zero;
            hostCollider.size = worldSize;

            ++RuntimeWorkCounters.AnchorOutlineBuilds;
            RuntimeWorkCounters.Log($"anchor wire box built for '{gameObject.name}'");
            // A shared real object: the scan box's look in its own violet, built once here.
            SpatialBlend.Samples.FreeformCollaboration.UI.SharedAnchorBox.Build(transform, _localCenter, _localSize, SharedBoxName);
            ApplyReceiverBoxPresentationPolicy();
        }

        /// <summary>True when this receiver's unmatched box draws no wires or pending outline (<see cref="HideReceiverBoxPresentation"/>).</summary>
        public bool ReceiverBoxPresentationHidden => _receiverBoxPresentationHidden;

        /// <summary>
        /// Applies the receiver presentation policy to the outlines present now. Called when an outline is (re)built
        /// (<c>SharedRoomObjectBox</c> rebuilds its pending outline after a placed box moves), never per frame.
        /// </summary>
        public void RefreshReceiverBoxPresentation() => ApplyReceiverBoxPresentationPolicy();

        private void ApplyReceiverBoxPresentationPolicy()
        {
            if (!_receiverBoxPresentationHidden) return;
            for (int i = 0; i < transform.childCount; ++i)
            {
                Transform child = transform.GetChild(i);
                if (!child.name.StartsWith("SceneAnchorWire_", StringComparison.Ordinal) &&
                    !string.Equals(child.name, "SharedBoxOutline", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Renderer part in child.GetComponentsInChildren<Renderer>(true)) part.enabled = false;
            }
        }

        public static Vector3 WorldAlignedSize(Quaternion rotation, Vector3 localSize)
        {
            Matrix4x4 rot = Matrix4x4.Rotate(rotation);
            Vector3 half = localSize * 0.5f;
            return new Vector3(
                Mathf.Abs(rot[0, 0]) * half.x + Mathf.Abs(rot[0, 1]) * half.y + Mathf.Abs(rot[0, 2]) * half.z,
                Mathf.Abs(rot[1, 0]) * half.x + Mathf.Abs(rot[1, 1]) * half.y + Mathf.Abs(rot[1, 2]) * half.z,
                Mathf.Abs(rot[2, 0]) * half.x + Mathf.Abs(rot[2, 1]) * half.y + Mathf.Abs(rot[2, 2]) * half.z
            ) * 2f;
        }

        /// <summary>The shared box's object under the anchor (the "SceneAnchorWire_" prefix: what the presentation policy and a re-adoption find).</summary>
        public const string SharedBoxName = "SceneAnchorWire_SharedBox";

        // [nameLen:4][name][labelLen:4][label]. The cross-space identity is the element link
        // the core records on resolve; the payload carries no element handle.
        internal override ulong GetSyncSerializedLength()
        {
            return sizeof(int) + (ulong)Encoding.UTF8.GetByteCount(gameObject.name)
                + sizeof(int) + (ulong)Encoding.UTF8.GetByteCount(_sceneLabel ?? string.Empty);
        }

        internal override void PrepareSync(IntPtr objectData)
        {
            WritePayload(objectData, gameObject.name, _sceneLabel);
        }

        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData)
        {
            if (!TryReadPayload(objectData, out string objectName, out string label))
            {
                return (null, null);
            }

            var newObject = new GameObject(string.IsNullOrEmpty(objectName) ? "SceneAnchor" : objectName);
            var entity = newObject.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(label);
            entity.AdoptWireName(objectName, label);
            return (newObject, entity);
        }

        /// <summary>
        /// Builds this replica as a box of the size its sharer measured, in the same local frame (so a seat's plane and front mean the
        /// same here): the box, its wire, and the declaration (the table's, or a seat's with the sharer's confirmed front). A seat's plane
        /// is the box's rectangle at the anchor's own plane height; the sharer's outline is not on the wire. False when the sharer
        /// sent no measure.
        /// </summary>
        public bool ApplyReplicaBox()
        {
            if (!_measuredKnown || string.IsNullOrEmpty(_sceneLabel)) return false;
            if (IsSeat)
            {
                Vector2 h = new Vector2(_localSize.x, _localSize.y) * 0.5f;
                Vector2 c = new Vector2(_localCenter.x, _localCenter.y);
                var outline = new[] { c + new Vector2(-h.x, -h.y), c + new Vector2(h.x, -h.y), c + new Vector2(h.x, h.y), c + new Vector2(-h.x, h.y) };
                return ApplySeatSupport(_localCenter, _localSize, outline, _seatKey, _replicaFrontDegrees);
            }
            ApplySupport(_localCenter, _localSize);
            return true;
        }

        /// <summary>
        /// Lets the user move this replica with the ordinary placement and manipulation motifs (the ones every other placeable shared
        /// object has). Recentring is off: it would stand the box up in the object frame, and the app places the box itself.
        /// </summary>
        public void MakePlaceable()
        {
            _allowManipulation = true;
            _placementMotifType = PlacementMotifType.Object;
            EnsureSupportVisuals();
            if (!Application.isPlaying) return;
            SetupManipulationPanelMotifs();
            var motif = GetComponent<SpatialBlend.Interactions.PlacementMotif>();
            if (motif != null) motif.AllowRecenter = false;
        }

        /// <summary>
        /// Reads what a sharer's name says (key, measured box, confirmed front) into this replica. A name without a measure part is the
        /// older plain one: the key, if any, is still read.
        /// </summary>
        internal void AdoptWireName(string objectName, string label)
        {
            if (!SharedAnchorName.TryParse(objectName, out string head, out Vector3 center, out Vector3 size, out float? front))
            {
                head = objectName;
            }
            else
            {
                _localCenter = center;
                _localSize = size;
                _measuredKnown = true;
                _replicaFrontDegrees = front;
            }
            if (string.Equals(label, SceneAnchorSpatialAuthoring.CouchLabel, StringComparison.Ordinal) &&
                SeatIdentity.TryParse(head, out string key, out bool frontConfirmed))
            {
                _seatKey = key;
                _replicaFrontConfirmed = frontConfirmed;
            }
            else if (SharedAnchorName.TryParseHead(head, out string headLabel, out string shareKey) &&
                     string.Equals(headLabel, label, StringComparison.Ordinal))
            {
                _shareKey = shareKey;
            }
        }

        /// <summary>
        /// Takes this anchor out of the shared hierarchy: the support visuals, the declaration and the collider this entity added go,
        /// then the entity (so the core unshares the element). The MRUK anchor itself stays: it is the user's room.
        /// </summary>
        public void Unregister()
        {
            if (_affordanceHost != null) DestroyObject(_affordanceHost);
            _affordanceHost = null;
            for (int i = transform.childCount - 1; i >= 0; --i)
            {
                Transform child = transform.GetChild(i);
                if (child.name.StartsWith("SceneAnchorWire_", StringComparison.Ordinal)) DestroyObject(child.gameObject);
            }
            var authoring = GetComponent<SbSpatialAuthoring>();
            if (authoring != null) DestroyObject(authoring);
            if (_addedCollider)
            {
                var box = GetComponent<BoxCollider>();
                if (box != null) DestroyObject(box);
            }
            if (_originalName != null) gameObject.name = _originalName;
            Detach();
            DestroyObject(this);
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private static void WritePayload(IntPtr objectData, string name, string label)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            byte[] labelBytes = Encoding.UTF8.GetBytes(label ?? string.Empty);
            int offset = 0;
            Marshal.WriteInt32(objectData, offset, nameBytes.Length);
            offset += sizeof(int);
            if (nameBytes.Length > 0)
            {
                Marshal.Copy(nameBytes, 0, IntPtr.Add(objectData, offset), nameBytes.Length);
                offset += nameBytes.Length;
            }
            Marshal.WriteInt32(objectData, offset, labelBytes.Length);
            offset += sizeof(int);
            if (labelBytes.Length > 0)
            {
                Marshal.Copy(labelBytes, 0, IntPtr.Add(objectData, offset), labelBytes.Length);
                offset += labelBytes.Length;
            }
        }

        private static bool TryReadPayload(IntPtr objectData, out string name, out string label)
        {
            name = "SceneAnchor";
            label = string.Empty;
            if (objectData == IntPtr.Zero)
            {
                return false;
            }
            int offset = 0;
            int nameLen = Marshal.ReadInt32(objectData, offset);
            offset += sizeof(int);
            if (nameLen < 0)
            {
                return false;
            }
            if (nameLen > 0)
            {
                byte[] nameBytes = new byte[nameLen];
                Marshal.Copy(IntPtr.Add(objectData, offset), nameBytes, 0, nameLen);
                name = Encoding.UTF8.GetString(nameBytes);
                offset += nameLen;
            }
            int labelLen = Marshal.ReadInt32(objectData, offset);
            offset += sizeof(int);
            if (labelLen < 0)
            {
                return false;
            }
            if (labelLen > 0)
            {
                byte[] labelBytes = new byte[labelLen];
                Marshal.Copy(IntPtr.Add(objectData, offset), labelBytes, 0, labelLen);
                label = Encoding.UTF8.GetString(labelBytes);
                offset += labelLen;
            }
            return true;
        }
    }
}
