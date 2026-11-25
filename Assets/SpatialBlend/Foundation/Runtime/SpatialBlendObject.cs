// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    public enum SpatialBlendObjectKind
    {
        Region,
        Object,
        Affordance
    }

    /// <summary>
    /// Adds authored local spatial meaning to an ordinary Unity object. Identity
    /// is scoped to an authored space; native registrations are local lifetimes,
    /// never room membership, ownership, or a network identity.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialBlendObject : MonoBehaviour
    {
        [SerializeField]
        private SpatialBlendLocalWorld _world;
        [SerializeField]
        private string _spaceId = "local";
        [SerializeField]
        private string _objectId;
        [SerializeField]
        private SpatialBlendObject _parent;
        [SerializeField]
        private SpatialBlendObjectKind _kind = SpatialBlendObjectKind.Object;
        [SerializeField]
        private Collider _collider;
        [SerializeField]
        private bool _colliderGeometryEnabled = true;
        [SerializeField]
        private Vector3[] _localSupportPolygon = Array.Empty<Vector3>();

        private LocalObjectRegistration _registration;
        private bool _registeredColliderGeometryEnabled;
        private bool _started;
        private bool _destroyed;

        public SpatialBlendLocalWorld World => _registration != null ? _registration.World : _world;
        public string SpaceId => _registration != null ? _registration.SpaceId : _spaceId;
        public string ObjectId => _registration != null ? _registration.ObjectId : _objectId;
        public SpatialBlendObjectKind Kind => _registration != null ? _registration.Kind : _kind;
        /// <summary>
        /// The authored parent this object was configured under, or null. Relation scope belongs to the relation
        /// family and starts at an actor's element (ABI 4), so a host that places an actor needs to know which authored
        /// region holds the objects the actor relates to.
        /// </summary>
        public SpatialBlendObject ParentObject => _parent;
        public bool IsRegistered => _registration != null && _registration.IsLive;
        /// <summary>
        /// Whether this object's collider is admitted as native geometry. Turning this
        /// off preserves authored Unity physics while omitting collider geometry from
        /// native registration and refresh.
        /// </summary>
        public bool ColliderGeometryEnabled => _registration != null
            ? _registeredColliderGeometryEnabled
            : _colliderGeometryEnabled;
        /// <summary>True while this component is being destroyed, before listeners are notified.</summary>
        public bool IsDestroyed => _destroyed;
        public string LastError { get; private set; }
        public event Action<SpatialBlendObject> Destroyed;
        internal LocalObjectRegistration Registration => _registration;

        private void Reset() => EnsureAuthoredId();
        private void Awake() => EnsureAuthoredId();

        private void EnsureAuthoredId()
        {
            // Existing serialized IDs are never silently changed on instantiation.
            // Intentional duplicate prefab instances must be explicitly configured.
            if (string.IsNullOrEmpty(_objectId))
                _objectId = Guid.NewGuid().ToString("N");
        }

        public void Configure(SpatialBlendLocalWorld world, string spaceId, string objectId,
                              SpatialBlendObject parent = null,
                              SpatialBlendObjectKind kind = SpatialBlendObjectKind.Object)
        {
            if (!TryConfigure(world, spaceId, objectId, parent, kind, out string error))
                throw new ArgumentException(error);
        }

        public bool TryConfigure(SpatialBlendLocalWorld world, string spaceId, string objectId,
                                 SpatialBlendObject parent, SpatialBlendObjectKind kind, out string error)
        {
            if (!ValidateIdentityAndParent(world, spaceId, objectId, parent, kind, out error))
                return Fail(error, out error);
            if (!IsFiniteAffine(transform.localToWorldMatrix))
                return Fail("Object transform must be a finite, invertible affine matrix.",
                            out error);
            if (IsRegistered)
            {
                if (world == _registration.World && spaceId == _registration.SpaceId &&
                    objectId == _registration.ObjectId && kind == _registration.Kind &&
                    (parent == null ? null : parent.Registration) == _registration.Parent)
                {
                    error = null;
                    return true;
                }
                return Fail(
                    "Unregister explicitly before changing a live object's identity or hierarchy.",
                    out error);
            }
            Unregister();
            _world = world;
            _spaceId = spaceId;
            _objectId = objectId;
            _parent = parent;
            _kind = kind;
            LastError = null;
            return true;
        }

        /// <summary>Ordered local support boundary, not arbitrary mesh vertices.</summary>
        public bool TrySetSupportPolygon(IReadOnlyList<Vector3> vertices, out string error)
        {
            if (!TryCopyValidPolygon(vertices, out Vector3[] candidate, out error))
                return Fail(error, out error);
            if (IsRegistered)
                return Fail("Unregister the object before changing authored support geometry.",
                            out error);
            _localSupportPolygon = candidate;
            LastError = null;
            return true;
        }

        /// <summary>
        /// Explicitly controls whether collider geometry is supplied to the local
        /// native element. This does not alter the Unity collider or affordance support.
        /// </summary>
        public bool TrySetColliderGeometryEnabled(bool enabled, out string error)
        {
            if (IsRegistered)
            {
                if (_registeredColliderGeometryEnabled == enabled)
                {
                    // Restore an inspector-mutated candidate to the policy that was
                    // actually admitted with this live native registration.
                    _colliderGeometryEnabled = enabled;
                    LastError = null;
                    error = null;
                    return true;
                }
                return Fail("Unregister the object before changing collider geometry policy.",
                            out error);
            }
            _colliderGeometryEnabled = enabled;
            LastError = null;
            error = null;
            return true;
        }

        private void Start()
        {
            _started = true;
            TryRegister(out _);
        }

        private void OnEnable()
        {
            if (_started)
                TryRegister(out _);
        }

        private void OnDisable() => Unregister();
        private void OnDestroy()
        {
            _destroyed = true;
            Unregister();
            try
            {
                Action<SpatialBlendObject> handlers = Destroyed;
                if (handlers != null)
                    foreach (Delegate callback in handlers.GetInvocationList())
                        try
                        {
                            ((Action<SpatialBlendObject>)callback)(this);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogException(exception);
                        }
            }
            finally
            {
                Destroyed = null;
            }
        }

        private void LateUpdate()
        {
            if (IsRegistered)
                TryRefresh(out _);
        }

        /// <summary>
        /// True when this object has footprint geometry: collider geometry is admitted and its own primitive collider is
        /// one the core registers as a footprint (box, sphere, or a vertical capsule). A Region without it registers
        /// fine but contains nothing, so it cannot be the navigable root actors hang under.
        /// </summary>
        public bool HasFootprintGeometry
        {
            get
            {
                if (_kind == SpatialBlendObjectKind.Region && _localSupportPolygon.Length >= 3) return true;
                if (!ColliderGeometryEnabled) return false;
                Collider collider = _collider != null ? _collider : GetComponent<Collider>();
                return collider != null && collider.transform == transform &&
                       (collider is BoxCollider || collider is SphereCollider ||
                        (collider is CapsuleCollider capsule && capsule.direction == 1));
            }
        }

        /// <summary>
        /// True when the world position is inside this object's footprint: the primitive collider's extent in the object's
        /// local XZ plane (the core's XY). This is v1's per-update test, <c>SbNetworkAvatarEntity.IsInsideAffordanceVolume</c>
        /// (world position into the volume's local XZ plane, then point-in-polygon), on the footprint the core registers.
        /// An object without collider geometry has no footprint and contains nothing.
        /// </summary>
        public bool ContainsWorldPosition(Vector3 worldPosition)
        {
            if (!IsRegistered || !_registeredColliderGeometryEnabled) return false;
            Collider collider = _collider != null ? _collider : GetComponent<Collider>();
            if (collider == null || collider.transform != transform) return false;
            Vector3 local = transform.InverseTransformPoint(worldPosition);
            if (collider is BoxCollider box)
                return Mathf.Abs(local.x - box.center.x) <= box.size.x * .5f &&
                       Mathf.Abs(local.z - box.center.z) <= box.size.z * .5f;
            if (collider is SphereCollider sphere)
            {
                float dx = local.x - sphere.center.x, dz = local.z - sphere.center.z;
                return dx * dx + dz * dz <= sphere.radius * sphere.radius;
            }
            if (collider is CapsuleCollider capsule && capsule.direction == 1)
            {
                float dx = local.x - capsule.center.x, dz = local.z - capsule.center.z;
                return dx * dx + dz * dz <= capsule.radius * capsule.radius;
            }
            return false;
        }

        public bool TryRegister(out string error)
        {
            if (IsRegistered)
            {
                if (!HasAdmittedColliderGeometryPolicy())
                    return Fail("Authored collider geometry policy changed; unregister before registering again.",
                                out error);
                error = null;
                return true;
            }
            if (_registration != null)
                Unregister();
            if (_world == null && !TryResolveRuntimeWorld(out error))
                return false;

            // Validate the entire authored ancestry before any ancestor allocates.
            var ancestry = new List<SpatialBlendObject>();
            var visited = new HashSet<SpatialBlendObject>();
            var claims = new HashSet<(SpatialBlendLocalWorld, string, string)>();
            for (SpatialBlendObject current = this; current != null; current = current._parent)
            {
                if (!visited.Add(current))
                    return Fail("Authored hierarchy contains a parent cycle.", out error);
                if (current.IsRegistered && !current.HasAdmittedColliderGeometryPolicy())
                    return Fail("An authored ancestor changed collider geometry policy; unregister it first.",
                                out error);
                if (!current.TryPrepare(out _, out _, out _, out error))
                    return Fail(error, out error);
                if (!claims.Add((current._world, current._spaceId, current._objectId)))
                    return Fail("Authored ancestry contains duplicate object IDs in one space.",
                                out error);
                if (!current.IsRegistered && !current._world.IsClaimAvailable(
                                                 current._spaceId, current._objectId, out error))
                    return Fail(error, out error);
                ancestry.Add(current);
            }
            var created = new List<LocalObjectRegistration>();
            bool committed = false;
            try
            {
                for (int index = ancestry.Count - 1; index >= 0; --index)
                {
                    SpatialBlendObject current = ancestry[index];
                    if (current.IsRegistered)
                        continue;
                    if (!current.TryPrepare(out Matrix4x4 local, out Collider collider,
                                            out Vector3[] support, out error))
                        return Fail(error, out error);
                    LocalObjectRegistration parent =
                        current._parent == null ? null : current._parent._registration;
                    if (!current._world.TryRegister(current, current._spaceId, current._objectId,
                                                    current._kind, parent, local, collider, support,
                                                    out LocalObjectRegistration registration,
                                                    out error))
                        return Fail(error, out error);
                    current._registration = registration;
                    current._registeredColliderGeometryEnabled = current._colliderGeometryEnabled;
                    current.LastError = null;
                    created.Add(registration);
                }
                committed = true;
            }
            finally
            {
                if (!committed)
                    for (int index = created.Count - 1; index >= 0; --index)
                        created[index].World.Unregister(created[index]);
            }
            error = null;
            LastError = null;
            return true;
        }

        public bool TryRefresh(out string error)
        {
            if (!IsRegistered)
                return Fail("Object registration is no longer live.", out error);
            // Inspector edits cannot retarget or remove somebody else's registration.
            if (_world != _registration.World || _spaceId != _registration.SpaceId ||
                _objectId != _registration.ObjectId || _kind != _registration.Kind ||
                (_parent == null ? null : _parent._registration) != _registration.Parent)
                return Fail(
                    "Authored identity or hierarchy changed; reconfigure before refreshing.",
                    out error);
            if (!HasAdmittedColliderGeometryPolicy())
                return Fail("Authored collider geometry policy changed; unregister before refreshing.",
                            out error);
            if (!TryPrepare(out Matrix4x4 local, out Collider collider, out Vector3[] support,
                            out error))
                return Fail(error, out error);
            try
            {
                SpatialBlendAPI.SetElementTransform(_registration.NativeSpace,
                                                   _registration.NativeElement, local);
                if (collider != null)
                    SpatialBlendAPI.SetElementCollider(_registration.NativeSpace,
                                                      _registration.NativeElement, collider);
                if (support != null)
                    SpatialBlendAPI.SetElementWorldSurface(_registration.NativeSpace,
                                                          _registration.NativeElement, support,
                                                          SbGeometryRole.Surface);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail(exception.Message, out error);
            }
        }

        public void Unregister()
        {
            LocalObjectRegistration registration = _registration;
            if (registration == null)
                return;
            registration.World.Unregister(registration);
            ClearRegistration(registration);
        }

        internal void ClearRegistration(LocalObjectRegistration registration)
        {
            if (ReferenceEquals(_registration, registration))
                _registration = null;
        }

        private bool HasAdmittedColliderGeometryPolicy() =>
            !IsRegistered || _colliderGeometryEnabled == _registeredColliderGeometryEnabled;

        private bool TryResolveRuntimeWorld(out string error)
        {
            if (!Application.isPlaying)
                return Fail("Configure an explicit local world outside Play Mode.", out error);
            SpatialBlendLocalWorld[] worlds = FindObjectsByType<SpatialBlendLocalWorld>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (worlds.Length > 1)
                return Fail("Multiple local worlds exist; configure the intended world explicitly.",
                            out error);
            _world =
                worlds.Length == 1
                    ? worlds[0]
                    : new GameObject("SpatialBlend Local World").AddComponent<SpatialBlendLocalWorld>();
            error = null;
            return true;
        }

        private bool TryPrepare(out Matrix4x4 local, out Collider collider,
                                out Vector3[] worldSupport, out string error)
        {
            local = default;
            collider = null;
            worldSupport = null;
            if (!ValidateIdentityAndParent(_world, _spaceId, _objectId, _parent, _kind, out error))
                return false;
            Matrix4x4 world = transform.localToWorldMatrix;
            if (!IsFiniteAffine(world) ||
                (_parent != null && !IsFiniteAffine(_parent.transform.localToWorldMatrix)))
            {
                error = "Object and parent transforms must be finite, invertible affine matrices.";
                return false;
            }
            local = _parent == null ? world : _parent.transform.worldToLocalMatrix * world;
            if (!IsFiniteAffine(local))
            {
                error = "Composed local transform is invalid.";
                return false;
            }
            if (_colliderGeometryEnabled)
            {
                collider = _collider;
                if (collider == null)
                {
                    Collider[] attached = GetComponents<Collider>();
                    if (attached.Length > 1)
                    {
                        error = "Select one explicit primitive collider.";
                        return false;
                    }
                    if (attached.Length == 1)
                        collider = attached[0];
                }
                if (collider != null && (collider.transform != transform ||
                                         !(collider is BoxCollider || collider is SphereCollider ||
                                           collider is CapsuleCollider)))
                {
                    error = "Only a primitive collider on this object is supported.";
                    return false;
                }
            }
            // An Affordance's support, and a Region's measured navigable surface (the room's floor),
            // are authored polygons registered as world surfaces; a Region without one has only its collider footprint.
            if (_kind == SpatialBlendObjectKind.Affordance ||
                (_kind == SpatialBlendObjectKind.Region && _localSupportPolygon.Length > 0))
            {
                if (!TryCopyValidPolygon(_localSupportPolygon, out Vector3[] polygon, out error))
                    return false;
                worldSupport = new Vector3[polygon.Length];
                for (int index = 0; index < polygon.Length; ++index)
                    worldSupport[index] = world.MultiplyPoint3x4(polygon[index]);
                if (!TryCopyValidPolygon(worldSupport, out worldSupport, out error))
                    return false;
            }
            error = null;
            return true;
        }

        private bool ValidateIdentityAndParent(SpatialBlendLocalWorld world, string spaceId,
                                               string objectId, SpatialBlendObject parent,
                                               SpatialBlendObjectKind kind, out string error)
        {
            error = null;
            if (world == null || !ValidId(spaceId) || !ValidId(objectId) ||
                !Enum.IsDefined(typeof(SpatialBlendObjectKind), kind))
            {
                error =
                    "A local world, valid authored IDs, and a known hierarchy kind are required.";
                return false;
            }
            if (parent != null &&
                (parent == this || parent._world != world ||
                 !string.Equals(parent._spaceId, spaceId, StringComparison.Ordinal)))
            {
                error = "Parent must belong to the same authored world and space.";
                return false;
            }
            if (kind == SpatialBlendObjectKind.Affordance &&
                (parent == null || parent._kind != SpatialBlendObjectKind.Object))
            {
                error = "An affordance requires an Object parent.";
                return false;
            }
            if (parent != null && kind != SpatialBlendObjectKind.Affordance &&
                parent._kind != SpatialBlendObjectKind.Region)
            {
                error = "Regions and objects may be nested only under a Region.";
                return false;
            }
            var visited = new HashSet<SpatialBlendObject>();
            for (SpatialBlendObject ancestor = parent; ancestor != null; ancestor = ancestor._parent)
                if (ancestor == this || !visited.Add(ancestor))
                {
                    error = "Authored hierarchy contains a parent cycle.";
                    return false;
                }
            return true;
        }

        internal static bool ValidId(string value) =>
            Internal.SpatialAuthoringValidation.ValidId(value);

        internal static bool IsFiniteAffine(Matrix4x4 matrix) =>
            Internal.SpatialAuthoringValidation.IsFiniteAffine(matrix);

        internal static bool TryCopyValidPolygon(IReadOnlyList<Vector3> vertices,
                                                 out Vector3[] copy, out string error) =>
            Internal.SpatialAuthoringValidation.TryCopyValidPolygon(vertices, out copy, out error);

        private bool Fail(string message, out string error)
        {
            error = LastError = message;
            return false;
        }
    }
}
