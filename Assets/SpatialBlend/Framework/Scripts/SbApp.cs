// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend;

using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SpatialBlend
{
    /// <summary>Why a link was removed by the peer (core ABI 4.9, <c>sb_link_removed_reason_t</c>).</summary>
    public enum LinkRemovedReason
    {
        PeerUnshared = 1,
        PeerUnlinked = 2,
    }

    /// <summary>The outcome of <see cref="SpatialBlendManager.TryUnlink"/>.</summary>
    public enum SbUnlinkResult
    {
        /// <summary>The loaded core is older than ABI 4.9 and has no unlink; nothing was done.</summary>
        Unavailable,
        /// <summary>The object is not attached, or is not the counterpart of a remote source.</summary>
        NotLinked,
        /// <summary>The link was removed on both sides; the element stays.</summary>
        Removed,
    }

    [DefaultExecutionOrder(1)]
    public abstract class SbApp : MonoBehaviour
    {
        // List of functionalities exposed here
        // - Registration of Object & Interaction
        //   - Serialized field of Object & Interaction to share
        //   - Policy to take remote Object & build interaction with local Objects

        [Header("Space Settings")]
        [SerializeField]
        protected SpatialBlendManager _spaceManager;

        [SerializeField]
        [Tooltip("The class to be used for the SbEntity component. If not set, SbNetworkEntity will be used by default.")]
        private UnityEngine.Object _sbEntityScriptAsset;

        [SerializeField, HideInInspector]
        private string _sbEntityTypeName = string.Empty;

        [SerializeField]
        private NavMeshSurface _walkableGeometry;

        public NavMeshSurface WalkableGeometry => _walkableGeometry;

        /// <summary>The room's ring registered with the core. Null until the room has been registered.</summary>
        public WalkableRegion RegisteredWalkableRegion => _spaceManager != null ? _spaceManager.RegisteredWalkable : null;

        /// <summary>
        /// The room a placement is checked against (<see cref="RoomFit"/>): the registered one; an app that knows its room before
        /// a session (the Freeform app, from MRUK) returns that one until then.
        /// </summary>
        public virtual RoomRegion PlacementRoom => _spaceManager != null ? _spaceManager.RegisteredRoom : null;

        /// <summary>The height of the room's floor (world y), where a drop preview draws a region; 0 unless an app knows its floor.</summary>
        public virtual float PlacementFloorY => 0f;

        /// <summary>The other virtual objects whose regions a placement keeps clear of before a session.</summary>
        public virtual IEnumerable<GameObject> PlacementOthers() => Array.Empty<GameObject>();

        /// <summary>
        /// The nearest translation that makes <paramref name="self"/>'s placement valid in this app's room (<see cref="RoomFit"/>),
        /// with its shapes first moved by <paramref name="offset"/> (a candidate pose). False when no room is known yet, or no spot
        /// in it fits.
        /// </summary>
        /// <summary>The furniture footprints of the room now (the scan-box rule): the placement room's unless an app reads them live.</summary>
        public virtual IReadOnlyList<Vector2[]> PlacementFurniture() => PlacementRoom?.Furniture;

        public bool TryPullPlacement(GameObject self, Vector2 offset, float margin, out Vector2 shift)
        {
            // A physical object (an own scan, furniture, the box over a real table) stays where the user puts it.
            if (SpatialBlendManager.IsPhysicalObject(self))
            {
                shift = Vector2.zero;
                return true;
            }
            if (_spaceManager != null) _spaceManager.RequestRegionRedeclare(self);
            return RoomFit.TryPull(self, offset, PlacementRoom, _spaceManager, PlacementOthers(), margin, out shift);
        }

        /// <summary>Whether <paramref name="self"/> is valid where it stands now (the one rule, fresh inputs: the room and the other objects as they are).</summary>
        public bool IsPlacementValid(GameObject self, float margin) =>
            self != null && RoomFit.IsValid(self, PlacementRoom, _spaceManager, PlacementOthers(), Mathf.Max(margin, SpatialBlendManager.RegionFitMarginMeters));

        /// <summary>
        /// The inputs of <paramref name="self"/>'s placements in this app's room now (the ring, the obstacles, the walls), for a drop
        /// preview to reuse during a grab. Null for a physical object, or when it has no region or no room is known.
        /// </summary>
        public RoomFit.Prepared PreparePlacement(GameObject self, float margin) =>
            self == null || SpatialBlendManager.IsPhysicalObject(self) ? null
                : RoomFit.Prepare(self, PlacementRoom, _spaceManager, PlacementOthers(), margin);

        /// <summary>Whether a drop of wall-mounted <paramref name="self"/> on a wall could land on one (the drop's own wall slide, <see cref="RoomFit.DropWallFits"/>).</summary>
        public bool AnyWallFits(GameObject self, Interactions.PlacementMotif motif, float margin) =>
            self != null && motif != null && RoomFit.DropWallFits(self, motif, PlacementRoom, _spaceManager, PlacementOthers(), margin);

        /// <summary>
        /// A drop: THE placement (<see cref="RoomFit.Place"/>) of <paramref name="self"/> at <paramref name="candidate"/>,
        /// turning allowed, the way back to where it stands bounding the search. <paramref name="placed"/> is the pose it takes; the
        /// object itself is left where it is (the motif settles it there). A physical object stays where the user puts it.
        /// </summary>
        public bool TryPlaceDrop(GameObject self, Pose candidate, float margin, out Pose placed)
        {
            placed = candidate;
            if (self == null || SpatialBlendManager.IsPhysicalObject(self)) return true;
            if (_spaceManager != null) _spaceManager.RequestRegionRedeclare(self);
            Transform t = self.transform;
            var was = new Pose(t.position, t.rotation);
            var back = new Vector2(was.position.x - candidate.position.x, was.position.z - candidate.position.z);
            t.SetPositionAndRotation(candidate.position, candidate.rotation);
            Vector2? knownValid = back.sqrMagnitude > 1e-8f ? back : (Vector2?)null;
            bool ok = RoomFit.Place(self, PlacementRoom, _spaceManager, PlacementOthers(), margin, null, null, out _, knownValid: knownValid);
            if (ok) placed = new Pose(t.position, t.rotation);
            t.SetPositionAndRotation(was.position, was.rotation);
            return ok;
        }

        /// <summary>
        /// A wall-mounted object dropped on a wall, where it stands if valid, else at the valid wall spot nearest it
        /// (<see cref="RoomFit.TryPlaceOnWall"/>). False when it is valid nowhere on a wall (the caller pulls it instead).
        /// </summary>
        public bool TryPlaceOnWall(GameObject self, Interactions.PlacementMotif motif, float margin)
        {
            if (self == null || SpatialBlendManager.IsPhysicalObject(self)) return false;
            List<GameObject> others = new List<GameObject>(PlacementOthers());
            // The same predicate as the preview's and the release's re-check (never under the manager's fit margin).
            if (RoomFit.IsValid(self, PlacementRoom, _spaceManager, others, Mathf.Max(margin, SpatialBlendManager.RegionFitMarginMeters))) return true;
            Vector3 p = self.transform.position;
            return RoomFit.TryPlaceOnWall(self, motif, new Vector2(p.x, p.z), PlacementRoom, _spaceManager, others, margin);
        }

        [SerializeField]
        private double _walkableGeometryResolution = 0.5;

        [SerializeField]
        private NavMeshSurface _wallGeometry;

        public virtual void Start()
        {
            if (_spaceManager == null)
            {
                throw new Exception("SpatialBlendManager is not assigned!");
            }

#if UNITY_EDITOR
            if (_sbEntityScriptAsset != null)
            {
                var monoScript = _sbEntityScriptAsset as UnityEditor.MonoScript;
                if (monoScript != null)
                {
                    var selectedType = monoScript.GetClass();
                    if (selectedType == null || !typeof(SbEntity).IsAssignableFrom(selectedType))
                    {
                        throw new Exception("Assigned script must inherit SbEntity");
                    }
                }
            }
#endif

            _spaceManager.RegisterApp(this);
        }

        /// <summary>
        /// True once this app has passed its start gate. Content created before
        /// that point (for example a loaded GLB) is left for the app's own
        /// pre-existing content registration instead of being shared early.
        /// </summary>
        public virtual bool IsAppStarted => false;

        public bool IsHost()
        {
            return _spaceManager.IsHost();
        }

        public void OnDestroy()
        {
            if (_spaceManager != null) _spaceManager.UnregisterApp(this);
        }

        // Owned by the manager's successful connection/start gate, including a
        // late app registration and an explicit fresh-session retry.
        internal void StartInSpace()
        {
            // Set here, on every path that starts the app (an already-connected manager starts it from Start), before the
            // walkable region is registered: otherwise a joiner fits received objects before its furniture is staged.
            if (WaitsForLocalSceneGeometry) _spaceManager.WaitsForLocalSceneGeometry = true;
            if (_walkableGeometry != null)
            {
                _spaceManager.RegisterWalkable(_walkableGeometry, _walkableGeometryResolution);
            }
            OnAppStart();
        }

        public virtual void OnAppStop() { }

        /// <summary>
        /// True when the app registers its own scene geometry (furniture) and calls
        /// <see cref="SpatialBlendManager.MarkLocalSceneGeometryRegistered"/>: authored objects are fitted only after that.
        /// </summary>
        protected virtual bool WaitsForLocalSceneGeometry => false;

        /// <summary>
        /// A link between a shared element and this space's own element ended on the peer's side (core ABI 4.9), and the element here
        /// survives. <see cref="LinkRemovedReason.PeerUnshared"/>: the sharer unshared the object this app linked
        /// <paramref name="gameObject"/> to (LinkToExisting); the app releases its counterpart. <see cref="LinkRemovedReason.PeerUnlinked"/>:
        /// a receiver dropped its counterpart of <paramref name="gameObject"/>, which this app shares and which stays shared.
        /// Called on the main thread.
        /// </summary>
        public virtual void OnLinkRemoved(GameObject gameObject, LinkRemovedReason reason) { }

        public void RegisterGameObject(GameObject gameObject, bool propagate = true)
        {
            var sbEntity = gameObject.GetComponent<SbEntity>();
            if (sbEntity == null)
            {
                var entityType = ResolveSbEntityType();
                gameObject.AddComponent(entityType);
            }
            _spaceManager.ShareGameObject(gameObject, propagate);
        }

        // Legacy v1 proximity API. Prefer RegisterRelation() for v2 (SpatialBlend).
        public void RegisterInteraction(Interaction interaction)
        {
            _spaceManager.RegisterInteraction(interaction);
        }

        /// <summary>
        /// v2 (SpatialBlend) API: declare a named subject anchor on an avatar actor
        /// (e.g. "hand"). "root" and "head" are added automatically when
        /// the avatar is registered; call this for any additional anchors before
        /// referencing them in <see cref="RegisterRelation"/>. Offset is in Unity space.
        /// </summary>
        public void RegisterAnchor(GameObject actor, string name, Vector3 localOffset)
        {
            _spaceManager.RegisterAnchor(actor, name, localOffset);
        }

        /// <summary>
        /// v2 (SpatialBlend) API: declare a spatial relation that binds one of the
        /// actor's subject anchors to referents through a relation kernel. This is
        /// the v2 replacement for proximity-based <see cref="RegisterInteraction"/>.
        /// </summary>
        public void RegisterRelation(SpatialRelation relation)
        {
            _spaceManager.RegisterRelation(relation);
        }

        /// <summary>
        /// Resolves the System.Type to use for the SbEntity component.
        /// Defaults to SbNetworkEntity when no valid script is assigned.
        /// </summary>
        protected Type ResolveSbEntityType()
        {
#if UNITY_EDITOR
            if (_sbEntityScriptAsset != null)
            {
                var monoScript = _sbEntityScriptAsset as UnityEditor.MonoScript;
                if (monoScript != null)
                {
                    return monoScript.GetClass();
                }
            }
#endif

            if (!string.IsNullOrEmpty(_sbEntityTypeName))
            {
                var resolved = ResolveTypeByName(_sbEntityTypeName);
                if (resolved == null || !typeof(SbEntity).IsAssignableFrom(resolved))
                {
                    throw new Exception($"Configured SbEntity type '{_sbEntityTypeName}' is invalid or does not inherit SbEntity");
                }
                return resolved;
            }

            return typeof(SbNetworkEntity);
        }

        private static Type ResolveTypeByName(string typeName)
        {
            // Try fast path first
            var t = Type.GetType(typeName, false);
            if (t != null) return t;

            // Fallback: search all loaded assemblies
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    var found = assemblies[i].GetType(typeName, false);
                    if (found != null) return found;
                }
                catch { }
            }
            return null;
        }

        public void DeregisterGameObject(GameObject gameObject)
        {
            var sbEntity = gameObject.GetComponent<SbEntity>();
            if (sbEntity != null)
            {
                sbEntity.Detach();
            }
        }

        public void RegisterWalkable(NavMeshSurface walkableNavMesh)
        {
            _walkableGeometry = walkableNavMesh;
            if (_spaceManager.Initialized)
            {
                _spaceManager.RegisterWalkable(walkableNavMesh, _walkableGeometryResolution);
            }
        }

        // Abstract methods to be implemented by the subclass. Below methods are guaranteed to be called on the main thread.
        // - OnAppStart: Called when the app is started.
        public abstract void OnAppStart();
        // - OnShare: Called when the `remote` object is shared in the scene. 
        //   Application should decide how to handle the incoming object.
        public abstract OnShareResponse OnShare(GameObject gameObject);

        public enum OnObjectAddedType
        {
            SpawnedSource,
            Linked,
            Added,
        }

        // - OnObjectAdded: Called when new object is spawned in the scene. 
        //   This object is fully initialized based on decision made in OnShare. 
        //   - OnShareResponse.Result.AddAsNew: This object the one cloned from remote.
        //   - OnShareResponse.Result.LinkToExisting: This object was already in the scene, but now linked as a counterpart of remote object.
        //   Application should define interactions with this object.   
        public abstract void OnObjectAdded(GameObject gameObject, OnObjectAddedType onObjectAddedType);

        // - OnRemove: Called when the 'remote' object is removed from remote spaces
        public abstract void OnRemove(GameObject gameObject);
    }
}
