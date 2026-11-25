// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    internal sealed class LocalObjectRegistration
    {
        internal LocalObjectRegistration(SpatialBlendLocalWorld world, SpatialBlendObject owner,
                                         string spaceId, string objectId, SpatialBlendObjectKind kind,
                                         LocalObjectRegistration parent, long nativeSpace,
                                         long nativeElement, int generation)
        {
            World = world;
            Owner = owner;
            SpaceId = spaceId;
            ObjectId = objectId;
            Kind = kind;
            Parent = parent;
            NativeSpace = nativeSpace;
            NativeElement = nativeElement;
            Generation = generation;
            IsLive = true;
        }
        internal SpatialBlendLocalWorld World { get; }
        internal SpatialBlendObject Owner { get; }
        internal string SpaceId { get; }
        internal string ObjectId { get; }
        internal SpatialBlendObjectKind Kind { get; }
        internal LocalObjectRegistration Parent { get; }
        internal long NativeSpace { get; }
        internal long NativeElement { get; }
        internal int Generation { get; }
        internal bool IsLive { get; private set; }
        internal void MarkStale()
        {
            IsLive = false;
        }
    }

    internal sealed class LocalActorRegistration
    {
        internal LocalActorRegistration(SpatialBlendLocalWorld world, SpatialBlendActor owner,
                                        string actorId, LocalObjectRegistration ownerObject,
                                        long nativeActor, long routingId, int generation)
        {
            World = world;
            Owner = owner;
            ActorId = actorId;
            OwnerObject = ownerObject;
            NativeSpace = ownerObject.NativeSpace;
            NativeActor = nativeActor;
            RoutingId = routingId;
            Generation = generation;
            IsLive = true;
        }
        internal SpatialBlendLocalWorld World { get; }
        internal SpatialBlendActor Owner { get; }
        internal string ActorId { get; }
        /// <summary>The actor's own element, the person leaf. It is fixed at registration.</summary>
        internal LocalObjectRegistration OwnerObject { get; }
        internal long NativeSpace { get; }
        internal long NativeActor { get; }
        internal long RoutingId { get; }
        internal int Generation { get; }
        internal bool ReceiverInputTargetOnly { get; set; }
        internal bool IsLive { get; private set; }
        internal void MarkStale() { IsLive = false; }
    }

    /// <summary>Owns one in-process native session and authored local
    /// spaces.</summary>
    public sealed partial class SpatialBlendLocalWorld : MonoBehaviour
    {
        [SerializeField]
        private string _defaultSpaceId = "local";
        private readonly Dictionary<string, long> _spaces =
            new Dictionary<string, long>(StringComparer.Ordinal);
        // Native local elements whose parent is zero are session roots and Core
        // deliberately does not permit their removal.  Foundation therefore owns
        // exactly one such identity root per native space; every authored top-level
        // object and observed-room scope is a removable child of it.
        private readonly Dictionary<string, NativeSpaceRoot> _nativeSpaceRoots =
            new Dictionary<string, NativeSpaceRoot>(StringComparer.Ordinal);
        private readonly Dictionary<ValueTuple<string, string>, LocalObjectRegistration>
            _registrations = new Dictionary<ValueTuple<string, string>, LocalObjectRegistration>();
        private readonly Dictionary<LocalObjectRegistration, Vector3[]> _worldSupports =
            new Dictionary<LocalObjectRegistration, Vector3[]>();
        private readonly List<SupportLease> _supportLeases = new List<SupportLease>();
        private readonly object _actorLock = new object();
        private readonly Dictionary<ValueTuple<long, long>, LocalActorRegistration> _actorsByNative =
            new Dictionary<ValueTuple<long, long>, LocalActorRegistration>();
        private readonly Dictionary<string, LocalActorRegistration> _sourceActorsById =
            new Dictionary<string, LocalActorRegistration>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _actorRoutingIds =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _actorOutputEpochs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<ValueTuple<string, long>, LocalActorRegistration>
            _actorsByKeyAndSpace =
                new Dictionary<ValueTuple<string, long>, LocalActorRegistration>();
        private readonly Dictionary<LocalObjectRegistration, LocalActorRegistration>
            _actorsByOwnerObject =
                new Dictionary<LocalObjectRegistration, LocalActorRegistration>();
        private readonly Dictionary<ValueTuple<long, long, int>, QueuedRealization> _queuedRealizations =
            new Dictionary<ValueTuple<long, long, int>, QueuedRealization>();
        private long _nextActorRoutingId;
        // Narrow deterministic setup-failure seam for the Foundation lifecycle
        // fixture. It is internal and consulted only while a new actor is built.
        internal static Func<string, bool> TestOnlyRejectActorAnchorBinding;
        private long _session;
        private bool _actorObservationFramesEnabled;
        private int _generation;
        private bool _shuttingDown;
        private int _mainThread;

        private sealed class NativeSpaceRoot
        {
            internal long Element;
            internal bool IdentityConfigured;
        }

        public bool IsInitialized => !_shuttingDown && _session > 0;
        public string DefaultSpaceId => _defaultSpaceId;
        public string LastError { get; private set; }

        private void Awake()
        {
            _mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        private sealed class QueuedRealization
        {
            internal long NativeSpace;
            internal long NativeActor;
            internal int Generation;
            internal string ActorId;
            internal long OutputEpoch;
            internal RealizationTarget[] Targets;
        }

        internal bool IsClaimAvailable(string spaceId, string objectId, out string error)
        {
            error = null;
            if (_shuttingDown || !IsValidId(spaceId) || !IsValidId(objectId))
                return Fail("Local world or authored IDs are invalid.", out error);
            if (_registrations.TryGetValue((spaceId, objectId),
                                           out LocalObjectRegistration current) &&
                current.IsLive)
                return Fail("Duplicate authored object ID in this space.", out error);
            return true;
        }

        internal bool TryRegister(SpatialBlendObject owner, string spaceId, string objectId,
                                  SpatialBlendObjectKind kind, LocalObjectRegistration parent,
                                  Matrix4x4 localTransform, Collider collider,
                                  Vector3[] worldSupport, out LocalObjectRegistration registration,
                                  out string error)
        {
            registration = null;
            error = null;
            if (_shuttingDown)
                return Fail("Local world is shutting down.", out error);
            if (owner == null || !IsValidId(spaceId) || !IsValidId(objectId))
                return Fail("Owner, space ID, and object ID are required and may not " +
                                "contain NUL.",
                            out error);
            if (!IsFinite(localTransform))
                return Fail("Local transform must be finite and non-singular.", out error);
            if (parent != null && (!parent.IsLive || parent.World != this ||
                                   parent.SpaceId != spaceId || parent.Generation != _generation))
                return Fail("Parent must be a live registration in this world and space.",
                            out error);
            var key = (spaceId, objectId);
            if (_registrations.TryGetValue(key, out LocalObjectRegistration existing) &&
                existing.IsLive)
                return Fail("Duplicate authored object ID in this space.", out error);
            if (!TryInitialize(out error) ||
                !TryGetOrCreateSpace(spaceId, out long nativeSpace, out error))
                return false;
            if (!TryGetNativeSpaceRoot(spaceId, out long nativeSpaceRoot, out error))
                return false;

            long nativeElement = 0;
            try
            {
                nativeElement = SpatialBlendAPI.AddElement(
                    nativeSpace, parent == null ? nativeSpaceRoot : parent.NativeElement);
                if (nativeElement <= 0)
                    return Fail("Native element creation failed.", out error);
                SpatialBlendAPI.SetElementTransform(nativeSpace, nativeElement, localTransform);
                SpatialBlendAPI.SetElementTag(nativeSpace, nativeElement, objectId);
                // Core: an element with no shared id has no counterpart on any receiver and is never a referent of any
                // family (v1 space.cc ShareEntity). Objects of the local world that carry the same authored
                // ID in different spaces are counterparts, so they share one process-local id.
                SpatialBlendAPI.SetElementSharedId(nativeSpace, nativeElement, SharedIdForAuthoredObject(objectId));
                if (collider != null)
                    SpatialBlendAPI.SetElementCollider(nativeSpace, nativeElement, collider);
                if (worldSupport != null && worldSupport.Length > 0)
                    SpatialBlendAPI.SetElementWorldSurface(nativeSpace, nativeElement, worldSupport,
                                                          GeometryRoleFor(kind));
                registration =
                    new LocalObjectRegistration(this, owner, spaceId, objectId, kind, parent,
                                                nativeSpace, nativeElement, _generation);
                _registrations[key] = registration;
                _worldSupports[registration] = CopySupport(worldSupport);
                return true;
            }
            catch (Exception exception)
            {
                return Fail(exception.Message, out error);
            }
            finally
            {
                if (registration == null && nativeElement > 0)
                {
                    try
                    {
                        RetireInteractionHierarchiesInSpace(nativeSpace);
                        if (HasPendingHierarchyInSpace(nativeSpace)) QueueHierarchyRemoval(nativeSpace, nativeElement, _generation);
                        else SpatialBlendAPI.RemoveLocalElement(nativeSpace, nativeElement);
                    }
                    catch (Exception exception)
                    {
                        LastError = exception.Message;
                    }
                }
            }
        }

        private const long AuthoredObjectSharedIdBase = 1L << 40;
        private readonly Dictionary<string, long> _authoredObjectSharedIds = new Dictionary<string, long>(StringComparer.Ordinal);

        private long SharedIdForAuthoredObject(string objectId)
        {
            if (!_authoredObjectSharedIds.TryGetValue(objectId, out long id))
                _authoredObjectSharedIds[objectId] = id = AuthoredObjectSharedIdBase + _authoredObjectSharedIds.Count + 1;
            return id;
        }

        internal void Unregister(LocalObjectRegistration registration)
        {
            if (registration == null || registration.World != this)
                return;
            var childFirst = new List<LocalObjectRegistration>();
            CollectChildFirst(registration, childFirst, new HashSet<LocalObjectRegistration>());
            foreach (LocalObjectRegistration current in childFirst)
                UnregisterOne(current);
        }

        public bool TryRegisterSupportCorrespondence(SpatialBlendObject source, SpatialBlendObject target,
                                                     out IDisposable lease, out string error)
        {
            lease = null;
            error = null;
            if (_shuttingDown || source == null || target == null)
                return Fail("Both support endpoints must be live in this local world.", out error);
            LocalObjectRegistration sourceRegistration = source.Registration;
            LocalObjectRegistration targetRegistration = target.Registration;
            if (!IsLiveEndpoint(sourceRegistration) || !IsLiveEndpoint(targetRegistration) ||
                sourceRegistration.NativeSpace == targetRegistration.NativeSpace ||
                sourceRegistration.Kind != SpatialBlendObjectKind.Affordance ||
                targetRegistration.Kind != SpatialBlendObjectKind.Affordance ||
                !HasExplicitWorldSupport(sourceRegistration) ||
                !HasExplicitWorldSupport(targetRegistration))
                return Fail("Support correspondence requires live, explicit Affordance " +
                                "supports in different local spaces.",
                            out error);
            try
            {
                long handle = SpatialBlendAPI.RegisterSupportCorrespondences(
                    _session,
                    new[] { new SbSupportCorrespondence(
                        sourceRegistration.NativeSpace, sourceRegistration.NativeElement,
                        targetRegistration.NativeSpace, targetRegistration.NativeElement) });
                if (handle <= 0)
                    return Fail("Native support correspondence registration failed.", out error);
                // Explicitly corresponding supports are counterparts whatever their authored IDs: one shared id.
                SpatialBlendAPI.SetElementSharedId(targetRegistration.NativeSpace, targetRegistration.NativeElement,
                                                  SharedIdForAuthoredObject(sourceRegistration.ObjectId));
                var created = new SupportLease(this, handle, _generation, sourceRegistration,
                                               targetRegistration);
                _supportLeases.Add(created);
                lease = created;
                return true;
            }
            catch (Exception exception)
            {
                return Fail(exception.Message, out error);
            }
        }

        public bool TryInitialize(out string error)
        {
            error = null;
            // EditMode's explicit initialization can precede Awake. Unity 6000.0
            // GetInstanceID performs its engine main-thread check in both player
            // and editor. Probe before assigning any ownership/native state;
            // the first arbitrary worker must not be able to claim this world.
            int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (_mainThread == 0)
            {
                try { GetInstanceID(); }
                catch (InvalidOperationException)
                { return Fail("Local world initialization requires the Unity main thread.", out error); }
                _mainThread = thread;
            }
            if (_mainThread != thread)
                return Fail("Local world initialization requires the owning Unity main thread.", out error);
            if (_shuttingDown)
                return Fail("Local world is shutting down.", out error);
            if (IsInitialized)
                return true;
            if (!IsValidId(_defaultSpaceId))
                return Fail("Default space ID is required and may not contain NUL.", out error);
            long createdSession = 0;
            try
            {
                SpatialBlendAPI.Initialize();
                createdSession = SpatialBlendAPI.CreateSession();
                if (createdSession <= 0)
                    return Fail("Native session creation failed.", out error);
                _session = createdSession;
                ++_generation;
                if (!TryGetOrCreateSpace(_defaultSpaceId, out _, out error))
                    throw new InvalidOperationException(error);
                return true;
            }
            catch (Exception exception)
            {
                if (createdSession > 0)
                {
                    try
                    {
                        SpatialBlendAPI.DestroySession(createdSession);
                    }
                    catch (Exception destroyException)
                    {
                        LastError = destroyException.Message;
                    }
                }
                _session = 0;
                _actorObservationFramesEnabled = false;
                _spaces.Clear();
                _nativeSpaceRoots.Clear();
                return Fail(exception.Message, out error);
            }
        }

        internal bool TryRegisterActor(SpatialBlendActor owner, string actorId,
                                       LocalObjectRegistration ownerObject,
                                       IReadOnlyList<SpatialBlendActorAnchor> anchors,
                                       bool isSource, out LocalActorRegistration registration,
                                       out string error)
        {
            registration = null;
            error = null;
            if (_shuttingDown || owner == null || !IsValidId(actorId) ||
                !IsLiveEndpoint(ownerObject) || anchors == null || anchors.Count == 0)
                return Fail("Actor requires a live owner object, valid ID, and explicit anchors.", out error);
            if (!SpatialBlendAPI.TryEnsureActorLifetimeAbi(out error))
                return Fail(error, out error);
            foreach (SpatialBlendActorAnchor anchor in anchors)
            {
                if (!anchor.TryResolvePositionDimension(out _))
                    return Fail("Actor anchor position dimension is invalid.", out error);
                if (anchor.Support != null &&
                    (!IsLiveEndpoint(anchor.Support.Registration) ||
                     anchor.Support.Registration.NativeSpace != ownerObject.NativeSpace))
                    return Fail("Actor anchor supports must be live in the owner's local space.",
                                out error);
            }
            lock (_actorLock)
            {
                if (isSource && _sourceActorsById.ContainsKey(actorId))
                    return Fail("Source actor IDs must be unique within a local world.", out error);
                if (_actorsByKeyAndSpace.ContainsKey((actorId, ownerObject.NativeSpace)))
                    return Fail("An actor key may have only one receiver in a local space.",
                                out error);
                if (_actorsByOwnerObject.ContainsKey(ownerObject))
                    return Fail("An authored object can own only one native actor.", out error);
            }
            try
            {
                long nativeActor = SpatialBlendAPI.CreateActor(ownerObject.NativeSpace);
                if (nativeActor <= 0) return Fail("Native actor creation failed.", out error);
                try
                {
                    SpatialBlendAPI.SetActorElement(ownerObject.NativeSpace, nativeActor,
                                                    ownerObject.NativeElement);
                    foreach (SpatialBlendActorAnchor anchor in anchors)
                    {
                        if (!anchor.TryResolvePositionDimension(out int positionDimension) ||
                            !SpatialBlendAPI.AddAnchorWithPositionDimension(
                                ownerObject.NativeSpace, nativeActor, anchor.Name,
                                anchor.LocalOffset, anchor.ParentName, positionDimension))
                        {
                            throw new InvalidOperationException(
                                "Native actor anchor position-dimension setup was rejected.");
                        }
                        if (TestOnlyRejectActorAnchorBinding != null &&
                            TestOnlyRejectActorAnchorBinding(anchor.Name))
                            throw new InvalidOperationException("Test-only actor anchor setup rejection.");
                        // Scope belongs to the relation family: a bind takes no scope or tag.
                        // The root is a Spacing anchor; every other anchor is an untagged AffordancePose anchor.
                        bool bound = anchor.Role == SpatialBlendActorAnchorRole.Root
                            ? SpatialBlendAPI.Bind(ownerObject.NativeSpace, nativeActor, anchor.Name, SbKernelType.Spacing)
                            : SpatialBlendAPI.BindAffordance(ownerObject.NativeSpace, nativeActor, anchor.Name);
                        if (!bound) throw new InvalidOperationException("Native actor binding was rejected.");
                    }
                    lock (_actorLock)
                    {
                        if (isSource || !_actorRoutingIds.TryGetValue(actorId, out long routingId))
                        {
                            routingId = ++_nextActorRoutingId;
                            _actorRoutingIds[actorId] = routingId;
                            foreach (LocalActorRegistration actor in _actorsByNative.Values)
                                if (actor.ActorId == actorId)
                                    SpatialBlendAPI.SetElementSharedId(actor.NativeSpace,
                                                                       actor.OwnerObject.NativeElement,
                                                                       routingId);
                        }
                        // This private, process-local routing ID is installed only as
                        // the native actor-owner matching key. It is not a wire or
                        // durable authored identity. Equal authored actor keys use
                        // the same key across local spaces so native fallback routes
                        // source updates to the intended receiver actor.
                        SpatialBlendAPI.SetElementSharedId(ownerObject.NativeSpace,
                                                           ownerObject.NativeElement, routingId);
                        registration = new LocalActorRegistration(this, owner, actorId,
                            ownerObject, nativeActor, routingId, _generation);
                        _actorsByNative[(ownerObject.NativeSpace, nativeActor)] = registration;
                        _actorsByKeyAndSpace.Add((actorId, ownerObject.NativeSpace), registration);
                        _actorsByOwnerObject.Add(ownerObject, registration);
                        if (isSource) _sourceActorsById.Add(actorId, registration);
                    }
                    return true;
                }
                catch
                {
                    SpatialBlendAPI.DestroyActor(ownerObject.NativeSpace, nativeActor);
                    throw;
                }
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
        }

        internal void UnregisterActor(LocalActorRegistration registration)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread)
                throw new InvalidOperationException("Actor retirement requires the owning Unity main thread.");
            if (registration == null || registration.World != this || !registration.IsLive) return;
            ObservedSourceSetupLease terminalObservedSetup =
                RetireObservedSourceSetupForActor(registration);
            RetireReceiverInput(registration);
            // Rich delivery is opt-in and token-scoped. Retire while this exact
            // registration is still live so its terminal envelope remains drainable.
            DisableRichRealization(registration);
            var releases = new List<LocalActorRegistration>();
            lock (_actorLock)
            {
                if (!registration.IsLive) return;
                registration.MarkStale();
                if (_actorsByNative.TryGetValue((registration.NativeSpace, registration.NativeActor),
                    out LocalActorRegistration current) && current == registration)
                    _actorsByNative.Remove((registration.NativeSpace, registration.NativeActor));
                if (_sourceActorsById.TryGetValue(registration.ActorId,
                    out LocalActorRegistration source) && source == registration)
                {
                    _sourceActorsById.Remove(registration.ActorId);
                    _actorOutputEpochs.TryGetValue(registration.ActorId, out long epoch);
                    _actorOutputEpochs[registration.ActorId] = epoch + 1;
                    foreach (LocalActorRegistration actor in _actorsByNative.Values)
                        if (actor.ActorId == registration.ActorId)
                            releases.Add(actor);
                }
                if (_actorsByKeyAndSpace.TryGetValue(
                        (registration.ActorId, registration.NativeSpace), out current) &&
                    current == registration)
                    _actorsByKeyAndSpace.Remove((registration.ActorId, registration.NativeSpace));
                if (_actorsByOwnerObject.TryGetValue(registration.OwnerObject, out current) &&
                    current == registration)
                    _actorsByOwnerObject.Remove(registration.OwnerObject);
                bool routeStillLive = false;
                foreach (LocalActorRegistration actor in _actorsByNative.Values)
                    if (actor.ActorId == registration.ActorId)
                    {
                        routeStillLive = true;
                        break;
                    }
                if (!routeStillLive)
                    _actorRoutingIds.Remove(registration.ActorId);
            }
            registration.Owner.ClearRegistration(registration);
            registration.Owner.ReleaseOwnedOutputs(registration, true);
            foreach (LocalActorRegistration receiver in releases)
                receiver.Owner.ReleaseOwnedOutputs(receiver, false);
            if (_session > 0 && registration.Generation == _generation)
            {
                try { SpatialBlendAPI.DestroyActor(registration.NativeSpace, registration.NativeActor); }
                catch (Exception exception) { LastError = exception.Message; }
            }
            ReleaseTerminalObservedSourceSetupAfterActorDestroy(terminalObservedSetup);
        }

        internal bool TryApplyActorFrame(LocalActorRegistration registration,
                                         AnchorPoseFrame frame, out string error)
        {
            error = null;
            if (registration == null || !registration.IsLive ||
                registration.World != this || registration.Generation != _generation)
                return Fail("Actor registration is stale.", out error);
            // The actor component performs complete-snapshot validation and
            // anchor observation updates. Root pose remains a separate call.
            if (registration.Owner.TryApplyFrameForRegistration(registration, frame))
                return true;
            return Fail("Actor observation frame was rejected.", out error);
        }

        internal bool TryBeginActorObservationEpoch(LocalActorRegistration registration,
                                                     string rootAnchor, out ulong epoch,
                                                     out string error)
        {
            epoch = 0; error = null;
            if (registration == null || !registration.IsLive || registration.World != this ||
                registration.Generation != _generation || !IsInitialized)
                return Fail("Actor registration is stale.", out error);
            if (!SpatialBlendAPI.TryEnsureActorObservationFrameAbi(out error))
                return Fail(error, out error);
            try
            {
                if (!_actorObservationFramesEnabled)
                {
                    if (!SpatialBlendAPI.EnableActorObservationFrames(_session))
                        return Fail("Native local observation-frame mode was rejected.", out error);
                    _actorObservationFramesEnabled = true;
                }
                epoch = SpatialBlendAPI.BeginActorObservationEpoch(registration.NativeSpace,
                    registration.NativeActor, rootAnchor);
                return epoch != 0 || Fail("Native observation epoch was rejected.", out error);
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
        }

        internal bool TryApplyActorObservationFrame(LocalActorRegistration registration, ulong epoch,
                                                     AnchorPoseFrame frame, string rootAnchor,
                                                     out string error)
        {
            error = null;
            if (registration == null || !registration.IsLive || registration.World != this ||
                registration.Generation != _generation || frame == null)
                return Fail("Actor registration is stale.", out error);
            var observations = new SpatialBlendAPI.SbActorObservation[frame.anchors.Length];
            for (int i = 0; i < observations.Length; ++i)
            {
                AnchorPoseSample sample = frame.anchors[i];
                Vector3 position = sample.anchorId == rootAnchor
                    ? new Vector3(sample.worldPosition.x, 0f, sample.worldPosition.z)
                    : sample.worldPosition;
                CoordinateConverter.UnityToSpatialBlend3D(position, out double x,
                    out double y, out double z);
                observations[i] = new SpatialBlendAPI.SbActorObservation
                {
                    anchor_name = sample.anchorId, position_x = x, position_y = y, position_z = z,
                    yaw = CoordinateConverter.UnityToSpatialBlendAngle(sample.worldRotation.eulerAngles.y) * Math.PI / 180d
                };
            }
            try
            {
                if (SpatialBlendAPI.ApplyActorObservationFrame(registration.NativeSpace,
                    registration.NativeActor, epoch, frame.sequence, frame.timeSeconds, observations)) return true;
                return Fail("Native atomic observation frame was rejected.", out error);
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
        }

        internal bool TryClearActorObservationAnchor(LocalActorRegistration registration, ulong epoch,
                                                      string anchor, out string error)
        {
            error = null;
            if (registration == null || !registration.IsLive || registration.World != this ||
                registration.Generation != _generation)
                return Fail("Actor registration is stale.", out error);
            try
            {
                return SpatialBlendAPI.ClearActorObservationAnchor(registration.NativeSpace,
                    registration.NativeActor, epoch, anchor) ||
                    Fail("Native observation-anchor clear was rejected.", out error);
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
        }

        internal bool TryGetActorObservationClock(LocalActorRegistration registration,
                                                   out SpatialBlendAPI.ActorObservationClock clock)
        {
            clock = default;
            return registration != null && registration.IsLive && registration.World == this &&
                registration.Generation == _generation && SpatialBlendAPI.TryGetActorObservationClock(
                    registration.NativeSpace, registration.NativeActor, out clock);
        }

        internal bool TryUpdateActorRoot(LocalActorRegistration registration,
                                         Vector3 worldPosition, float yawDegrees,
                                         out string error)
        {
            error = null;
            if (registration == null || !registration.IsLive ||
                registration.Generation != _generation)
                return Fail("Actor registration is stale.", out error);
            try
            {
                SpatialBlendAPI.UpdateActorState(registration.NativeSpace,
                                                 registration.NativeActor,
                                                 worldPosition, yawDegrees);
                return true;
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
        }

        internal bool IsCurrentActorOutput(LocalActorRegistration registration, long epoch)
        {
            lock (_actorLock)
            {
                return registration != null && registration.IsLive &&
                    _actorsByNative.TryGetValue((registration.NativeSpace, registration.NativeActor),
                        out LocalActorRegistration current) && current == registration &&
                    (!_actorOutputEpochs.TryGetValue(registration.ActorId, out long currentEpoch) ||
                     currentEpoch == epoch);
            }
        }

        private void OnNativeTargets(long nativeSpace, long nativeActor,
                                     IReadOnlyList<RealizationTarget> targets)
        {
            // This callback can be on a native worker. It reads only immutable
            // registration data under a managed lock and copies target values.
            lock (_actorLock)
            {
                if (!_actorsByNative.TryGetValue((nativeSpace, nativeActor),
                    out LocalActorRegistration registration) || !registration.IsLive ||
                    registration.NativeSpace != nativeSpace ||
                    registration.Generation != _generation)
                    return;
                var copy = new RealizationTarget[targets.Count];
                for (int i = 0; i < copy.Length; ++i) copy[i] = targets[i];
                if (_queuedRealizations.Count >= 256 &&
                    !_queuedRealizations.ContainsKey((nativeSpace, nativeActor, registration.Generation)))
                    return;
                _queuedRealizations[(nativeSpace, nativeActor, registration.Generation)] = new QueuedRealization
                {
                    NativeSpace = nativeSpace,
                    NativeActor = nativeActor,
                    Generation = registration.Generation,
                    ActorId = registration.ActorId,
                    OutputEpoch = _actorOutputEpochs.TryGetValue(registration.ActorId,
                        out long epoch) ? epoch : 0,
                    Targets = copy
                };
            }
        }

        public void DrainRealizations(double localSeconds)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread ||
                double.IsNaN(localSeconds) || double.IsInfinity(localSeconds)) return;
            DrainInteractionHierarchyCleanup();
            // Rich timing uses the pinned Stopwatch/native-clock bridge, never the
            // legacy Unity unscaled-time domain passed to this public method.
            DrainRichRealizations();
            QueuedRealization[] latest;
            lock (_actorLock)
            {
                latest = new List<QueuedRealization>(_queuedRealizations.Values).ToArray();
                _queuedRealizations.Clear();
            }
            foreach (QueuedRealization pending in latest)
            {
                LocalActorRegistration registration;
                lock (_actorLock)
                {
                    if (!_actorsByNative.TryGetValue((pending.NativeSpace, pending.NativeActor), out registration) ||
                        !registration.IsLive || registration.NativeSpace != pending.NativeSpace ||
                        registration.Generation != pending.Generation) continue;
                    if (_actorOutputEpochs.TryGetValue(pending.ActorId, out long epoch) &&
                        epoch != pending.OutputEpoch) continue;
                }
                registration.Owner.DeliverRealization(registration, pending.OutputEpoch, pending.Targets, localSeconds);
            }
        }

        public void Shutdown()
        {
            // An EditMode component that never awoke or initialized owns no
            // native lifetime to retire. Its teardown is a true no-op.
            if (_mainThread == 0 && _session == 0) return;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread)
                throw new InvalidOperationException("Local world shutdown requires the owning Unity main thread.");
            if (_shuttingDown)
                return;
            _shuttingDown = true;
            foreach (var hierarchy in _interactionHierarchies.ToArray()) hierarchy.Dispose();
            RetireSceneGeometryScopesForShutdown();
            foreach (SupportLease lease in new List<SupportLease>(_supportLeases))
                lease.Dispose();
            LocalActorRegistration[] actors;
            lock (_actorLock)
            {
                _queuedRealizations.Clear();
                actors = new List<LocalActorRegistration>(_actorsByNative.Values).ToArray();
            }
            foreach (LocalActorRegistration actor in actors)
                UnregisterActor(actor);
            // Shutdown has no future Update. Drain the token-scoped terminal
            // envelopes before clearing callback/session state so observers can
            // withdraw their own diagnostic view without touching a replacement.
            DrainRichRealizations();
            var childFirst = new List<LocalObjectRegistration>();
            var visited = new HashSet<LocalObjectRegistration>();
            foreach (LocalObjectRegistration registration in _registrations.Values)
                CollectChildFirst(registration, childFirst, visited);
            foreach (LocalObjectRegistration registration in childFirst)
                UnregisterOne(registration);
            _registrations.Clear();
            _worldSupports.Clear();
            foreach (long nativeSpace in _spaces.Values)
            {
                try
                {
                    SpatialBlendAPI.UnregisterCallbacks(nativeSpace);
                }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                }
            }
            _spaces.Clear();
            long session = _session;
            _session = 0;
            _actorObservationFramesEnabled = false;
            ++_generation;
            if (session > 0)
            {
                try
                {
                    SpatialBlendAPI.DestroySession(session);
                }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                }
            }
            // The roots are deliberately not removed individually: Core treats
            // parentless local elements as session-owned.  Dropping the managed
            // identities only after session teardown avoids any retry path
            // allocating a second root in the same native space.
            _nativeSpaceRoots.Clear();
            foreach (var hierarchy in _interactionHierarchies.ToArray()) ForgetInteractionHierarchy(hierarchy);
            _hierarchyRemovalRetries.Clear();
            _shuttingDown = false;
        }

        private void OnDisable()
        {
            Shutdown();
        }
        private void Update()
        {
            DrainRealizations(Time.unscaledTimeAsDouble);
        }
        private void OnDestroy()
        {
            Shutdown();
        }

        private bool TryGetOrCreateSpace(string spaceId, out long nativeSpace, out string error)
        {
            nativeSpace = 0;
            error = null;
            if (!IsInitialized || !IsValidId(spaceId))
                return Fail("World is not initialized or space ID is invalid.", out error);
            if (_spaces.TryGetValue(spaceId, out nativeSpace))
                return TryGetNativeSpaceRoot(spaceId, out _, out error);
            try
            {
                nativeSpace = SpatialBlendAPI.AddSpace(_session);
                if (nativeSpace <= 0)
                    return Fail("Native space creation failed.", out error);
                SpatialBlendAPI.RegisterOnTargets(nativeSpace, OnNativeTargets);
                _spaces.Add(spaceId, nativeSpace);
                return TryGetNativeSpaceRoot(spaceId, out _, out error);
            }
            catch (Exception exception)
            {
                return Fail(exception.Message, out error);
            }
        }

        // Raw root identities remain private implementation detail; partial
        // lifecycle code can use this helper without exposing native handles.
        private bool TryGetNativeSpaceRoot(string spaceId, out long root, out string error)
        {
            root = 0;
            error = null;
            if (!IsInitialized || !_spaces.TryGetValue(spaceId, out long nativeSpace))
                return Fail("Native local space is unavailable.", out error);
            try
            {
                if (!_nativeSpaceRoots.TryGetValue(spaceId, out NativeSpaceRoot state))
                {
                    long created = AddSceneGeometryNativeElement(nativeSpace, 0);
                    if (created <= 0)
                        return Fail("Native local-space root creation failed.", out error);
                    state = new NativeSpaceRoot { Element = created };
                    // Retain the root before its identity chart is configured. If
                    // that write throws, the exact same immutable root is retried
                    // rather than allocating another parentless native element.
                    _nativeSpaceRoots.Add(spaceId, state);
                }
                if (!state.IdentityConfigured)
                {
                    SetSceneGeometryNativeTransform(nativeSpace, state.Element, Matrix4x4.identity);
                    state.IdentityConfigured = true;
                }
                root = state.Element;
                return true;
            }
            catch (Exception exception)
            {
                return Fail(exception.Message, out error);
            }
        }

        private void CollectChildFirst(LocalObjectRegistration registration,
                                       List<LocalObjectRegistration> output,
                                       HashSet<LocalObjectRegistration> visited)
        {
            if (registration == null || !visited.Add(registration))
                return;
            foreach (LocalObjectRegistration candidate in _registrations.Values)
            {
                if (candidate.Parent == registration)
                    CollectChildFirst(candidate, output, visited);
            }
            output.Add(registration);
        }

        private void UnregisterOne(LocalObjectRegistration registration)
        {
            if (registration == null || registration.World != this || !registration.IsLive)
                return;
            RetireInteractionHierarchiesInSpace(registration.NativeSpace);
            DisposeLeasesFor(registration);
            // A C1 point capability must finish before removal changes the
            // native element.  If an admitted setup cannot release until the
            // element is terminal, retry exact capability cleanup afterward.
            ObservedSourceSetupLease terminalObservedSetup =
                RetireObservedSourceSetupForPoint(registration);
            LocalActorRegistration[] ownedActors;
            lock (_actorLock)
            {
                var matching = new List<LocalActorRegistration>();
                foreach (LocalActorRegistration actor in _actorsByNative.Values)
                    if (actor.OwnerObject == registration)
                        matching.Add(actor);
                ownedActors = matching.ToArray();
            }
            foreach (LocalActorRegistration actor in ownedActors)
                UnregisterActor(actor);
            registration.MarkStale();
            registration.Owner.ClearRegistration(registration);
            var key = (registration.SpaceId, registration.ObjectId);
            if (_registrations.TryGetValue(key, out LocalObjectRegistration current) &&
                current == registration)
                _registrations.Remove(key);
            _worldSupports.Remove(registration);
            if (_session > 0 && registration.Generation == _generation)
            {
                try
                {
                    // A closed Busy hierarchy still protects this Space root.
                    // Keep the exact stale identity until its native lease retires;
                    // never queue a future object with the same authored ID.
                    if (HasPendingHierarchyInSpace(registration.NativeSpace))
                        QueueHierarchyRemoval(registration.NativeSpace, registration.NativeElement, registration.Generation);
                    else
                        SpatialBlendAPI.RemoveLocalElement(registration.NativeSpace,
                                                          registration.NativeElement);
                }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                }
            }
            ReleaseTerminalObservedSourceSetupAfterPointDestroy(terminalObservedSetup);
        }

        private void DisposeLeasesFor(LocalObjectRegistration registration)
        {
            foreach (SupportLease lease in new List<SupportLease>(_supportLeases))
            {
                if (lease.Source == registration || lease.Target == registration)
                    lease.Dispose();
            }
        }

        private bool IsLiveEndpoint(LocalObjectRegistration registration) =>
            registration != null && registration.IsLive
            && registration.World == this && registration.Generation == _generation
            && IsInitialized;
        private bool HasExplicitWorldSupport(LocalObjectRegistration registration)
        {
            if (!_worldSupports.TryGetValue(registration, out Vector3[] support) ||
                support == null || support.Length < 3)
                return false;
            foreach (Vector3 point in support)
            {
                if (float.IsNaN(point.x) || float.IsInfinity(point.x) || float.IsNaN(point.y) ||
                    float.IsInfinity(point.y) || float.IsNaN(point.z) || float.IsInfinity(point.z))
                    return false;
            }
            return true;
        }
        private static SbGeometryRole GeometryRoleFor(SpatialBlendObjectKind kind) =>
            kind == SpatialBlendObjectKind.Region ? SbGeometryRole.Navigable : SbGeometryRole.Surface;
        private static Vector3[] CopySupport(Vector3[] support)
        {
            if (support == null || support.Length == 0)
                return Array.Empty<Vector3>();
            var copy = new Vector3[support.Length];
            Array.Copy(support, copy, support.Length);
            return copy;
        }
        internal static bool IsValidId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
        private static bool IsFinite(Matrix4x4 value)
        {
            for (int index = 0; index < 16; ++index)
            {
                float component = value[index];
                if (float.IsNaN(component) || float.IsInfinity(component))
                    return false;
            }
            return Math.Abs(value.determinant) > 1e-8f;
        }
        private bool Fail(string message, out string error)
        {
            LastError = message;
            error = message;
            return false;
        }

        private sealed class SupportLease : IDisposable
        {
            private SpatialBlendLocalWorld _world;
            private long _handle;
            private readonly int _generation;
            internal SupportLease(SpatialBlendLocalWorld world, long handle, int generation,
                                  LocalObjectRegistration source, LocalObjectRegistration target)
            {
                _world = world;
                _handle = handle;
                _generation = generation;
                Source = source;
                Target = target;
            }
            internal LocalObjectRegistration Source { get; }
            internal LocalObjectRegistration Target { get; }
            public void Dispose()
            {
                SpatialBlendLocalWorld world = _world;
                if (world == null)
                    return;
                _world = null;
                world._supportLeases.Remove(this);
                long handle = _handle;
                _handle = 0;
                // Shutdown sets its admission guard before disposing leases;
                // the session remains live until after this callback-free pass.
                if (handle > 0 && world._session > 0 && world._generation == _generation)
                {
                    try
                    {
                        SpatialBlendAPI.DisposeSupportCorrespondences(handle);
                    }
                    catch (Exception exception)
                    {
                        world.LastError = exception.Message;
                    }
                }
            }
        }
    }
}
