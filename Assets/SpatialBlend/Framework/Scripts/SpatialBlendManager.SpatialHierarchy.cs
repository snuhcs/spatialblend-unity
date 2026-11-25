// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Explicit local hierarchy authoring; it neither shares nor infers identities.</summary>
    public partial class SpatialBlendManager
    {
        private readonly Dictionary<string, SpatialHierarchyRegistration> _spatialHierarchyRegistrations =
            new Dictionary<string, SpatialHierarchyRegistration>(StringComparer.Ordinal);

        public bool RegisterLocalSpatialHierarchy(SpatialHierarchyDescriptor descriptor,
                                                  out SpatialHierarchyRegistration registration)
        {
            registration = null;
            if (descriptor == null || !descriptor.TryCreateSnapshot(out SpatialHierarchySnapshot snapshot,
                out _) || !Initialized || ConnectionState != SpatialBlendConnectionState.Connected)
            {
                return false;
            }

            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation) ||
                (_spatialHierarchyRegistrations.TryGetValue(snapshot.SpaceId, out SpatialHierarchyRegistration existing) &&
                 existing.IsLive))
            {
                return false;
            }
            _spatialHierarchyRegistrations.Remove(snapshot.SpaceId);

            var byId = new Dictionary<string, SpatialHierarchyNode>(StringComparer.Ordinal);
            foreach (SpatialHierarchyNode node in snapshot.Nodes) byId.Add(node.id, node);
            var elements = new Dictionary<string, long>(StringComparer.Ordinal);
            var supports = new Dictionary<string, long>(StringComparer.Ordinal);
            var created = new List<long>();
            try
            {
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                long room = EnsureSpatialHierarchyRoom(nativeSpace);
                if (room <= 0 || !IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation))
                {
                    RollbackSpatialHierarchy(nativeSpace, generation, created);
                    return false;
                }
                foreach (SpatialHierarchyNode node in snapshot.Nodes)
                {
                    if (!TryRegisterNode(node, room, nativeSpace, generation, snapshot, byId, elements,
                        supports, created))
                    {
                        RollbackSpatialHierarchy(nativeSpace, generation, created);
                        return false;
                    }
                }
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation))
                {
                    RollbackSpatialHierarchy(nativeSpace, generation, created);
                    return false;
                }
                registration = new SpatialHierarchyRegistration(this, snapshot.SpaceId, nativeSpace,
                    generation, elements, supports);
                _spatialHierarchyRegistrations.Add(snapshot.SpaceId, registration);
                return true;
            }
            catch (Exception ex)
            {
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                    $"Failed to register explicit spatial hierarchy: {ex.Message}");
                RollbackSpatialHierarchy(nativeSpace, generation, created);
                return false;
            }
        }

        /// <summary>
        /// Resolves two already-live, separately authored spaces for an external
        /// transport bridge. It publishes no native link itself.
        /// </summary>
        public static bool TryPublishSpatialSupportCorrespondences(
            SpatialHierarchyRegistration source, SpatialHierarchyRegistration target,
            IReadOnlyList<SpatialSupportCorrespondence> correspondences,
            ISpatialSupportCorrespondenceSink sink)
        {
            if (source == null || target == null || sink == null || !source.IsLive || !target.IsLive ||
                source.NativeSpace == target.NativeSpace ||
                string.Equals(source.AuthoredSpaceId, target.AuthoredSpaceId, StringComparison.Ordinal) ||
                correspondences == null || correspondences.Count == 0)
            {
                return false;
            }
            var resolved = new List<ResolvedSpatialSupportCorrespondence>(correspondences.Count);
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialSupportCorrespondence correspondence in correspondences)
            {
                if (correspondence == null || string.IsNullOrWhiteSpace(correspondence.sourceSupportId) ||
                    string.IsNullOrWhiteSpace(correspondence.targetSupportId) ||
                    !sources.Add(correspondence.sourceSupportId) ||
                    !source.TryGetSupportElement(correspondence.sourceSupportId, out long sourceElement) ||
                    !target.TryGetSupportElement(correspondence.targetSupportId, out long targetElement))
                {
                    return false;
                }
                resolved.Add(new ResolvedSpatialSupportCorrespondence(correspondence.sourceSupportId,
                    source.AuthoredSpaceId, source.NativeSpace, source.Generation, sourceElement,
                    correspondence.targetSupportId, target.AuthoredSpaceId, target.NativeSpace,
                    target.Generation, targetElement));
            }
            // Check liveness again after endpoint resolution and immediately before
            // the one bridge call, so a reentrant disconnect cannot publish stale IDs.
            return source.IsLive && target.IsLive && sink.TryPublish(resolved);
        }

        internal bool IsSpatialHierarchyRegistrationCurrent(long nativeSpace, long generation) =>
            nativeSpace > 0 && _spaceID == nativeSpace && Initialized &&
            ConnectionState == SpatialBlendConnectionState.Connected &&
            IsCurrentLifecycleGeneration(generation);

        private bool TryRegisterNode(SpatialHierarchyNode node, long room, long nativeSpace,
                                     long generation, SpatialHierarchySnapshot snapshot,
                                     Dictionary<string, SpatialHierarchyNode> byId,
                                     Dictionary<string, long> elements,
                                     Dictionary<string, long> supports, List<long> created)
        {
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            if (elements.ContainsKey(node.id)) return true;
            long parent = room;
            if (!string.IsNullOrEmpty(node.parentId) &&
                (!byId.TryGetValue(node.parentId, out SpatialHierarchyNode parentNode) ||
                 !TryRegisterNode(parentNode, room, nativeSpace, generation, snapshot, byId, elements,
                    supports, created) || !elements.TryGetValue(node.parentId, out parent)))
            {
                return false;
            }
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            long element;
            if (node.existingElementId > 0)
            {
                if (node.kind != SpatialHierarchyNodeKind.Object) return false;
                element = node.existingElementId;
            }
            else
            {
                element = AddSpatialHierarchyElement(nativeSpace, parent);
                if (element <= 0 || !IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                created.Add(element);
                SetSpatialHierarchyTransform(nativeSpace, element, node.localTransform);
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            }
            if (!string.IsNullOrEmpty(node.tag))
            {
                SetSpatialHierarchyTag(nativeSpace, element, node.tag);
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            }
            if (node.kind == SpatialHierarchyNodeKind.Region &&
                node.localBoundaryPolygon != null && node.localBoundaryPolygon.Length >= 3)
            {
                SetSpatialHierarchyBoundary(nativeSpace, element, node.localBoundaryPolygon);
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            }
            if (node.kind == SpatialHierarchyNodeKind.Object &&
                node.localFootprintPolygon != null && node.localFootprintPolygon.Length >= 3)
            {
                SetSpatialHierarchyFootprint(nativeSpace, element, node.localFootprintPolygon, null);
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            }
            if (node.kind == SpatialHierarchyNodeKind.Affordance)
            {
                if (!snapshot.TryGetWorldSupportVertices(node.id, out Vector3[] worldVertices)) return false;
                SetSpatialHierarchySupport(nativeSpace, element, worldVertices);
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                if (node.subRegions != null)
                {
                    foreach (SpatialSubRegion subRegion in node.subRegions)
                    {
                        AddSpatialHierarchySubRegion(nativeSpace, element, subRegion.name,
                            subRegion.tag, subRegion.localPolygon, subRegion.rotationDegrees);
                        if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                    }
                }
                supports.Add(node.supportId, element);
            }
            elements.Add(node.id, element);
            return true;
        }

        private void RollbackSpatialHierarchy(long nativeSpace, long generation, List<long> created)
        {
            // A reentrant teardown/reconnect retires the captured native space.
            // Never issue cleanup calls to that retired handle: it may now be
            // invalid or, in a different backend, aliased with a new session.
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return;
            for (int i = created.Count - 1; i >= 0; --i)
            {
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return;
                try
                {
                    if (!RemoveSpatialHierarchyElement(nativeSpace, created[i]))
                    {
                        SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                            $"Failed to roll back local spatial element {created[i]} in captured space {nativeSpace}.");
                    }
                }
                catch (Exception ex)
                {
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                        $"Failed to roll back local spatial element {created[i]}: {ex.Message}");
                }
            }
        }

        protected virtual long EnsureSpatialHierarchyRoom(long nativeSpace) =>
            nativeSpace == _spaceID ? EnsureRoomElement() : -1;
        protected virtual long AddSpatialHierarchyElement(long nativeSpace, long parent) =>
            SpatialBlendAPI.AddElement(nativeSpace, parent);
        protected virtual void SetSpatialHierarchyTransform(long nativeSpace, long element,
                                                            Matrix4x4 localTransform) =>
            SpatialBlendAPI.SetElementTransform(nativeSpace, element, localTransform);
        // SurfaceSpatialDefinition is baked-world: this registration exports the
        // snapshot's composed vertices once for its captured generation. Editing
        // authored local node transforms later requires an explicit rebuild and
        // re-registration; it cannot mutate the already-baked native surface.
        protected virtual void SetSpatialHierarchySupport(long nativeSpace, long element,
                                                          Vector3[] worldVertices) =>
            SpatialBlendAPI.SetElementWorldSurface(nativeSpace, element, worldVertices, SbGeometryRole.Surface);
        protected virtual void SetSpatialHierarchyTag(long nativeSpace, long element, string tag) =>
            SpatialBlendAPI.SetElementTag(nativeSpace, element, tag);
        // False when the core refused the polygon.
        protected virtual bool SetSpatialHierarchyBoundary(long nativeSpace, long element, Vector2[] polygon) =>
            SpatialBlendAPI.SetElementPolygon(nativeSpace, element, SbGeometryRole.Containment, polygon);
        // The object footprint is the collider envelope: the Spacing and Facing support. A collider is
        // handed to the core to derive it. With no collider the compiled footprint polygon is written
        // directly, so a declared polygon is never dropped.
        private void SetSpatialHierarchyFootprint(long nativeSpace, long element,
                                                  Vector2[] footprint, Collider footprintCollider)
        {
            if (footprintCollider != null)
            {
                SetSpatialHierarchyFootprintCollider(nativeSpace, element, footprintCollider);
            }
            else if (footprint != null && footprint.Length >= 3)
            {
                SetSpatialHierarchyFootprintPolygon(nativeSpace, element, footprint);
            }
        }
        protected virtual void SetSpatialHierarchyFootprintCollider(long nativeSpace, long element,
                                                                    Collider footprintCollider) =>
            SpatialBlendAPI.SetElementCollider(nativeSpace, element, footprintCollider);
        // The compiled footprint is in the object frame, so it is written element-local when the
        // library can. It is never flagged: only the region is the actor's scope.
        protected virtual void SetSpatialHierarchyFootprintPolygon(long nativeSpace, long element,
                                                                   Vector2[] footprint)
        {
            if (_containmentFeatureAvailable &&
                SetSpatialHierarchyLocalBoundary(nativeSpace, element, footprint))
            {
                return;
            }
            SetSpatialHierarchyBoundary(nativeSpace, element, footprint);
        }
        protected virtual void SetSpatialHierarchySharedId(long nativeSpace, long element, long sharedId) =>
            SpatialBlendAPI.SetElementSharedId(nativeSpace, element, sharedId);
        protected virtual long FindSpatialHierarchyElement(long nativeSpace, long sharedId) =>
            SpatialBlendAPI.FindElementBySharedId(nativeSpace, sharedId);
        protected virtual void AddSpatialHierarchySubRegion(long nativeSpace, long element,
                                                            string name, string tag, Vector2[] polygon) =>
            SpatialBlendAPI.AddSubRegion(nativeSpace, element, name, tag, polygon);

        // A patch turned about the surface normal (a seat's confirmed front). Zero is the unrotated overload above, so a
        // fixture that overrides it still sees every ordinary patch.
        protected virtual void AddSpatialHierarchySubRegion(long nativeSpace, long element, string name, string tag,
                                                            Vector2[] polygon, float rotationDegrees)
        {
            if (rotationDegrees == 0f) AddSpatialHierarchySubRegion(nativeSpace, element, name, tag, polygon);
            else SpatialBlendAPI.AddSubRegion(nativeSpace, element, name, tag, polygon, rotationDegrees);
        }
        protected virtual bool RemoveSpatialHierarchyElement(long nativeSpace, long element) =>
            SpatialBlendAPI.RemoveLocalElement(nativeSpace, element);

        protected virtual void ShareSpatialIdentity(long nativeSpace, long element) =>
            SpatialBlendAPI.ShareElement(nativeSpace, element);
        protected virtual void ResolveSpatialHierarchyElement(long nativeSpace, long received, long resolved) =>
            SpatialBlendAPI.ResolveSharedElement(nativeSpace, received, resolved);

        private readonly Dictionary<long, long> _authoredRegionByObject = new Dictionary<long, long>();
        private readonly Dictionary<long, AuthoredObjectState> _authoredStateByObject =
            new Dictionary<long, AuthoredObjectState>();

        /// <summary>What one copy of an authored object wrote into this space.</summary>
        private sealed class AuthoredObjectState
        {
            public SpatialAuthoringPlan Plan;
            public long RegionElement;
            // The region polygon as registered, in the authoring frame: the declaration's, or a scene anchor's fitted one.
            public Vector2[] RegionPolygon;
            public long ObjectElement;
            public long ObjectSharedId;
            public long[] AffordanceElements = Array.Empty<long>();
            public SpatialElementMapping[] AffordanceMappings = Array.Empty<SpatialElementMapping>();
            public Matrix4x4 BakedWorld;
            // The observed anchor this object stands on: its footprint lies inside the
            // anchor's, so the region is a child of the anchor's top surface and its transform is relative to it.
            public Matrix4x4 RegionParentWorldInverse = Matrix4x4.identity;
            // The region's world transform now (the authoring frame's), kept current so the content can be moved with its anchor.
            public Matrix4x4 RegionFrameWorld = Matrix4x4.identity;
            // The app already owns the object's elements (the MRUK adapter). Their pose and
            // geometry are never rewritten from the declaration.
            public bool Owned;
            /// <summary>The region was trimmed to its free part (last resort): the next move re-declares the full one.</summary>
            public bool Trimmed;
            /// <summary>Set by a drop (SbApp.TryPullPlacement): the next pose update re-declares a trimmed region in full.</summary>
            public bool RedeclarePending;
            /// <summary>
            /// An own scan whose pose the core refused for its region: the region is withdrawn (no shared id, no tag, no transfer
            /// leaf, not this object's region on this side) and the object element is moved out of it, under the room at the pose
            /// the object really has (<see cref="ObjectUnderRoom"/>), so the core's scope walk no longer enters the stale region
            /// through its child. The region element itself stays: the core does not delete a shared element. Declared again,
            /// with the object back under it, when a later pose is taken.
            /// </summary>
            public bool Withdrawn;
            /// <summary>The object element was moved out of its withdrawn region, under the room.</summary>
            public bool ObjectUnderRoom;
        }

        /// <summary>Whether this object's region was withdrawn after a refused pose (an own scan).</summary>
        internal bool RegionWithdrawn(long objectElement) =>
            _authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState state) && state.Withdrawn;

        /// <summary>Whether a withdrawn region's object element stands under the room (moved out of it at its real pose).</summary>
        internal bool ObjectMovedOutOfRegion(long objectElement) =>
            _authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState state) && state.ObjectUnderRoom;

        public bool TryGetAuthoredRegion(long objectElement, out long regionElement) =>
            _authoredRegionByObject.TryGetValue(objectElement, out regionElement);

        internal void ForgetAuthoredRegion(long objectElement)
        {
            // A physical object that cut a wall is gone: the wall is made whole again once its region has left the core (the
            // caller retrims after the unshare; RetrimWalls drops a lingering cut whose region is gone).
            if (_wallCutsByObject.TryGetValue(objectElement, out Vector2[] wallCut))
            {
                _wallCutsByObject.Remove(objectElement);
                long cutRegion = _authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState cutState) ? cutState.RegionElement : 0;
                if (cutRegion > 0 && !_isTearingDown) _lingeringWallCuts[cutRegion] = wallCut;
                _wallsDirty = true;
            }
            if (_authoredRegionByObject.TryGetValue(objectElement, out long region))
            {
                _containmentRegions.Remove(region);
                _sceneLayout?.RemoveContent(region);
            }
            _sceneLayout?.RemoveContent(objectElement);
            _contentParentWorldInverse.Remove(objectElement);
            _authoredRegionByObject.Remove(objectElement);
            _authoredStateByObject.Remove(objectElement);
        }

        internal void ClearAuthoredRegions()
        {
            _wallCutsByObject.Clear();
            _lingeringWallCuts.Clear();
            _authoredRegionByObject.Clear();
            _authoredStateByObject.Clear();
            _containmentRegions.Clear();
            _anchorRegionByKey.Clear();
        }

        /// <summary>
        /// Instantiates the compiled declaration on this space. A zero
        /// <paramref name="existingObjectElement"/> creates the object under the
        /// region. A positive id is the received element: the region is the one the
        /// source shared first, found by its shared id, and the footprint is written
        /// locally rather than copied from the other space. When no region arrived,
        /// no sibling region is invented: the footprint and the affordance surfaces
        /// are still written under the received object, and the binding reports
        /// region 0. A linked local object goes through
        /// <see cref="TryLinkSpatialAuthoring"/> instead.
        /// A non-null <paramref name="owned"/> names elements the app already owns
        /// (the MRUK scene-geometry adapter's tree). Authoring binds them: it creates no
        /// second element, writes neither their pose nor their geometry, and the region is
        /// a sibling under the room because the core has no reparent call.
        /// </summary>
        public bool TryApplySpatialAuthoring(SpatialAuthoringPlan plan, long objectSharedId,
                                              long existingObjectElement, Matrix4x4 worldTransform,
                                              Collider footprintCollider,
                                              out SpatialAuthoringBinding binding,
                                              SpatialAuthoringExistingElements owned = null,
                                              Vector2[] regionOverride = null)
        {
            binding = null;
            if (plan == null || plan.Mappings == null || plan.Mappings.Length == 0 || objectSharedId == 0)
            {
                return false;
            }
            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation) || !IsFiniteMatrix(worldTransform))
            {
                return false;
            }

            long room = EnsureSpatialHierarchyRoom(nativeSpace);
            if (room <= 0 || !IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;

            // Polygons are written in the authoring frame, which sits inside the object frame.
            Matrix4x4 frameWorld = worldTransform * plan.FrameToObject;
            // A new object whose footprint lies entirely inside an observed anchor's (a chess board on the MRUK table) is
            // a child of that anchor's top surface; any other is a child of the room.
            long nestParent = 0;
            int nestKey = 0;
            Matrix4x4 nestWorld = Matrix4x4.identity;
            if (existingObjectElement <= 0 && owned == null)
            {
                Vector2[] footprintRing = WorldFootprintRing(plan, frameWorld);
                if (footprintRing != null) TryFindSceneGeometryContainer(footprintRing, out nestParent, out nestWorld, out nestKey);
            }
            Matrix4x4 regionLocal = nestParent > 0 ? nestWorld.inverse * frameWorld : frameWorld;
            var created = new List<long>();
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            var affordanceElements = new List<long>();
            var affordanceMappings = new List<SpatialElementMapping>();
            long regionElement = 0;
            Vector2[] regionPolygon = null;
            long objectElement = 0;
            int affordanceIndex = 0;
            bool createdRegion = false;
            // A scene anchor arrives with its adapter's element as the existing object, but it is this peer's own furniture:
            // its region is made here and holds that element (the anchor region below), as a local object's does.
            bool existingObject = existingObjectElement > 0;
            bool received = existingObject && owned == null;
            Vector2[] anchorRegion = null;
            Matrix4x4 anchorBoxWorld = Matrix4x4.identity;
            bool anchorReparented = false;
            // A rollback removes the elements made here, and the region takes its subtree with it: the anchor's box goes
            // back under the room first.
            void Rollback()
            {
                if (anchorReparented)
                {
                    ReparentSpatialHierarchyElement(nativeSpace, existingObjectElement, room, anchorBoxWorld);
                    anchorReparented = false;
                }
                RollbackSpatialHierarchy(nativeSpace, generation, created);
            }
            try
            {
                foreach (SpatialElementMapping mapping in plan.Mappings)
                {
                    if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) 
                    {
                        Rollback();
                        return false;
                    }
                    long parent = nestParent > 0 ? nestParent : room;
                    if (!string.Equals(mapping.parentId, SpatialAuthoringCompiler.RoomParentId, StringComparison.Ordinal))
                    {
                        if (!ids.TryGetValue(mapping.parentId, out parent) || parent <= 0)
                        {
                            if (existingObject && mapping.kind == SpatialHierarchyNodeKind.Object)
                            {
                                parent = room; // The object already exists; it is not re-parented.
                            }
                            else
                            {
                                Rollback();
                                return false;
                            }
                        }
                    }

                    long element = 0;
                    bool boundOwnedAffordance = false;
                    long sharedId = 0;
                    if (mapping.kind == SpatialHierarchyNodeKind.Region)
                    {
                        sharedId = SpatialAuthoringCompiler.RegionSharedId(objectSharedId);
                        if (owned != null)
                        {
                            // Always this peer's own element: a received region with the same shared id is resolved onto it
                            // when the source's share is linked (TryLinkSpatialAuthoring).
                            if (!TryFitAnchorRegion(plan, frameWorld, existingObjectElement, out anchorRegion,
                                    out anchorBoxWorld, out string why))
                            {
                                SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Scene,
                                    $"Spatial authoring '{plan.ObjectId}': the scene anchor registers no region ({why}).");
                                ids[mapping.id] = 0;
                                continue;
                            }
                        }
                        else
                        {
                            element = FindSpatialHierarchyElement(nativeSpace, sharedId);
                        }
                        if (element <= 0 && received)
                        {
                            // A received object cannot be moved under a region made now, so a
                            // region made now would be a childless sibling that no scoped
                            // binding could ever reach. Leave the region out.
                            SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Geometry,
                                $"Spatial authoring '{plan.ObjectId}': the source's region did not arrive, " +
                                "so the object is authored without a region.");
                            ids[mapping.id] = 0;
                            continue;
                        }
                    }
                    else if (mapping.kind == SpatialHierarchyNodeKind.Object && existingObject)
                    {
                        element = existingObjectElement;
                        sharedId = objectSharedId;
                    }
                    else if (mapping.kind == SpatialHierarchyNodeKind.Affordance)
                    {
                        sharedId = SpatialAuthoringCompiler.AffordanceSharedId(objectSharedId, affordanceIndex);
                        if (owned != null && owned.Affordances.TryGetValue(mapping.id, out long ownedElement) &&
                            ownedElement > 0)
                        {
                            element = ownedElement;
                            boundOwnedAffordance = true;
                        }
                        else
                        {
                            element = FindSpatialHierarchyElement(nativeSpace, sharedId);
                        }
                        affordanceIndex++;
                    }
                    else
                    {
                        sharedId = objectSharedId;
                    }

                    if (element <= 0)
                    {
                        element = AddSpatialHierarchyElement(nativeSpace, parent);
                        if (mapping.kind == SpatialHierarchyNodeKind.Region) createdRegion = true;
                        if (element <= 0)
                        {
                            Rollback();
                            return false;
                        }
                        created.Add(element);
                    }
                    if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation))
                    {
                        Rollback();
                        return false;
                    }
                    SetSpatialHierarchySharedId(nativeSpace, element, sharedId);
                    if (!string.IsNullOrEmpty(mapping.tag))
                        SetSpatialHierarchyTag(nativeSpace, element, mapping.tag);

                    if (mapping.kind == SpatialHierarchyNodeKind.Region)
                    {
                        SetSpatialHierarchyTransform(nativeSpace, element, regionLocal);
                        Vector2[] polygon = anchorRegion ?? regionOverride ?? mapping.polygon;
                        if (anchorRegion != null)
                        {
                            // The region holds the furniture: the box goes under it first (the region has no footprint
                            // yet, so nothing is checked against it), then the polygon is written. Written the other way
                            // round, the region and the box would be siblings under the room, which may only touch. The
                            // room's ring holds every anchor and its stand-off (RoomRegion), so the region lies inside it.
                            if (!ReparentSpatialHierarchyElement(nativeSpace, existingObjectElement, element,
                                    regionLocal.inverse * anchorBoxWorld))
                            {
                                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                                    $"Spatial authoring '{plan.ObjectId}': the scene anchor could not be moved under its region " +
                                    $"({SpatialHierarchySceneError(nativeSpace) ?? "no message"}); it registers no region.");
                                RemoveSpatialHierarchyElement(nativeSpace, element);
                                created.Remove(element);
                                ids[mapping.id] = 0;
                                continue;
                            }
                            anchorReparented = true;
                        }
                        // Written element-local and flagged for containment transfer, on both sides (a virtual object was
                        // moved into the walkable ring before it got here, see ShareGameObject). A polygon the core refused
                        // leaves the region with no geometry and no transfer declaration.
                        if (!WriteAuthoredRegion(nativeSpace, element, polygon))
                        {
                            string reason = SpatialHierarchySceneError(nativeSpace) ?? "the core refused its polygon";
                            if (anchorReparented)
                            {
                                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                                    $"Spatial authoring '{plan.ObjectId}': the scene anchor's region was not registered " +
                                    $"({reason}). The anchor stays under the room without a region.");
                                ReparentSpatialHierarchyElement(nativeSpace, existingObjectElement, room, anchorBoxWorld);
                                anchorReparented = false;
                                RemoveSpatialHierarchyElement(nativeSpace, element);
                                created.Remove(element);
                                ids[mapping.id] = 0;
                                continue;
                            }
                            if (created.Contains(element))
                            {
                                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                                    $"Spatial authoring '{plan.ObjectId}': region '{mapping.id}' was not registered ({reason}). " +
                                    "It is removed and the object is shared without it, so no avatar can transfer into it.");
                                Rollback();
                                return false;
                            }
                            // The source's region, received: this peer cannot remove it. It keeps its shared id and tag, has
                            // no polygon here, and is never a transfer leaf; the binding reports no region. The received object
                            // may be its child natively, so the region goes back to the identity and the object carries the
                            // frame itself, as a region-less object does (later pose updates write the object directly).
                            SetSpatialHierarchyTransform(nativeSpace, element, Matrix4x4.identity);
                            SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                                $"Spatial authoring '{plan.ObjectId}': received region '{mapping.id}' (element {element}) was " +
                                $"not registered ({reason}). The received region is left inert (no containment, no transfer) " +
                                "on this peer.");
                            if (!received)
                            {
                                Rollback();
                                return false;
                            }
                            ids[mapping.id] = 0;
                            continue;
                        }
                        regionElement = element;
                        regionPolygon = polygon;
                    }
                    else if (mapping.kind == SpatialHierarchyNodeKind.Object)
                    {
                        // The region carries the world pose and the object is the identity under
                        // it. A received object is already that child when the region was shared
                        // first. Without a region it keeps the world pose it was given. An
                        // adapter-owned object keeps the pose and geometry its adapter wrote.
                        if (owned == null)
                        {
                            bool parentedUnderRegion = regionElement > 0 && (!received || !createdRegion);
                            SetSpatialHierarchyTransform(nativeSpace, element,
                                parentedUnderRegion ? Matrix4x4.identity : frameWorld);
                            SetSpatialHierarchyFootprint(nativeSpace, element, mapping.polygon, footprintCollider);
                        }
                        objectElement = element;
                    }
                    else
                    {
                        if (!boundOwnedAffordance)
                            SetSpatialHierarchyTransform(nativeSpace, element, Matrix4x4.identity);
                        if (!boundOwnedAffordance && mapping.surface != null && mapping.surface.Length >= 3)
                        {
                            SetSpatialHierarchySupport(nativeSpace, element,
                                SpatialAuthoringCompiler.BakeSurface(mapping.surface, frameWorld));
                        }
                        if (mapping.subRegions != null)
                        {
                            foreach (SpatialSubRegion subRegion in mapping.subRegions)
                            {
                                AddSpatialHierarchySubRegion(nativeSpace, element, subRegion.name,
                                    subRegion.tag, subRegion.localPolygon, subRegion.rotationDegrees);
                            }
                        }
                        affordanceElements.Add(element);
                        affordanceMappings.Add(mapping);
                    }
                    ids[mapping.id] = element;
                }

                if (objectElement <= 0 || (regionElement <= 0 && !existingObject) ||
                    !IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation))
                {
                    Rollback();
                    return false;
                }
                // An adapter-owned object is not a child of the region, so the region is
                // not its transform parent and must not receive its pose updates.
                if (regionElement > 0 && owned == null) _authoredRegionByObject[objectElement] = regionElement;
                _authoredStateByObject[objectElement] = new AuthoredObjectState
                {
                    Plan = plan,
                    RegionElement = regionElement,
                    RegionPolygon = regionPolygon,
                    ObjectElement = objectElement,
                    ObjectSharedId = objectSharedId,
                    AffordanceElements = affordanceElements.ToArray(),
                    AffordanceMappings = affordanceMappings.ToArray(),
                    BakedWorld = worldTransform,
                    RegionParentWorldInverse = nestParent > 0 ? nestWorld.inverse : Matrix4x4.identity,
                    RegionFrameWorld = frameWorld,
                    Owned = owned != null,
                };
                if (regionElement > 0 && owned == null && existingObjectElement <= 0)
                {
                    // Registered with the layout so the region follows the anchor it stands on when that is retired or
                    // restaged, and finds the anchor that contains it if the anchor arrives later.
                    AuthoredObjectState registered = _authoredStateByObject[objectElement];
                    RegisterSceneContent(regionElement, nestKey,
                        () => registered.RegionFrameWorld,
                        () => WorldRing(plan, registered.RegionFrameWorld, SpatialHierarchyNodeKind.Object),
                        () => WorldRing(plan, registered.RegionFrameWorld, SpatialHierarchyNodeKind.Region),
                        (key, parentWorld) => registered.RegionParentWorldInverse = key > 0 ? parentWorld.inverse : Matrix4x4.identity);
                }
                if (anchorReparented && owned != null)
                {
                    // Its tree is staged again under the region, not the room (StageSceneGeometryRaw).
                    _anchorRegionByKey[owned.SourceKey] = new AnchorRegionParent
                    {
                        Element = regionElement, WorldInverse = regionLocal.inverse, Space = nativeSpace, Generation = generation,
                    };
                }
                binding = new SpatialAuthoringBinding
                {
                    RegionElement = regionElement,
                    ObjectElement = objectElement,
                    RegionId = plan.RegionId,
                    ObjectId = plan.ObjectId,
                };
                return true;
            }
            catch (Exception ex)
            {
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                    $"Failed to apply spatial authoring: {ex.Message}");
                Rollback();
                return false;
            }
        }

        /// <summary>
        /// Gives a linked local object its region. <paramref name="localObjectElement"/> is the
        /// receiver's own element, which already sits under the region it was authored with when it
        /// was registered. It is not moved, so no sibling region is made. The region the source shared
        /// arrives as a received element with the source's shared id. It is resolved onto the local
        /// region, which records the link the core needs to resolve the source's normalizing scope.
        /// The local region and affordances then take shared ids derived from the source's object id.
        /// That is the id the source's relation payload names, so a payload that refers to the source's
        /// affordance resolves to the receiver's own copy, with identical local geometry.
        /// </summary>
        public bool TryLinkSpatialAuthoring(long localObjectElement, long sourceObjectSharedId,
                                            Matrix4x4 worldTransform, out SpatialAuthoringBinding binding)
        {
            binding = null;
            if (localObjectElement <= 0 || sourceObjectSharedId == 0 ||
                !_authoredStateByObject.TryGetValue(localObjectElement, out AuthoredObjectState state) ||
                state.RegionElement <= 0 || !IsFiniteMatrix(worldTransform))
            {
                return false;
            }
            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            try
            {
                long receivedRegion = FindSpatialHierarchyElement(nativeSpace,
                    SpatialAuthoringCompiler.RegionSharedId(sourceObjectSharedId));
                if (receivedRegion > 0 && receivedRegion != state.RegionElement)
                {
                    ResolveSpatialHierarchyElement(nativeSpace, receivedRegion, state.RegionElement);
                    if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                }

                SpatialElementMapping regionMapping = Array.Find(state.Plan.Mappings,
                    mapping => mapping.kind == SpatialHierarchyNodeKind.Region);
                SetSpatialHierarchySharedId(nativeSpace, state.RegionElement,
                    SpatialAuthoringCompiler.RegionSharedId(sourceObjectSharedId));
                state.ObjectSharedId = sourceObjectSharedId;
                state.RegionFrameWorld = worldTransform * state.Plan.FrameToObject;
                SetSpatialHierarchyTransform(nativeSpace, state.RegionElement,
                    state.RegionParentWorldInverse * worldTransform * state.Plan.FrameToObject);
                if (regionMapping != null &&
                    !WriteAuthoredRegion(nativeSpace, state.RegionElement, state.RegionPolygon ?? regionMapping.polygon))
                {
                    // The core kept the region's previous polygon, but its shared id, frame and transform above are
                    // already the source's: the link is half made, and the region may sit where its polygon is refused.
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                        $"Spatial authoring '{state.Plan.ObjectId}': linking region {state.RegionElement} to the source's was " +
                        $"refused ({SpatialHierarchySceneError(nativeSpace) ?? "the core refused its polygon"}).");
                    return false;
                }
                // A scene anchor's box keeps the placement its adapter gave it under the region.
                if (!state.Owned) SetSpatialHierarchyTransform(nativeSpace, localObjectElement, Matrix4x4.identity);
                for (int index = 0; index < state.AffordanceElements.Length; ++index)
                {
                    SetSpatialHierarchySharedId(nativeSpace, state.AffordanceElements[index],
                        SpatialAuthoringCompiler.AffordanceSharedId(sourceObjectSharedId, index));
                }
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
                RefreshAuthoredSupports(localObjectElement, worldTransform);
                binding = new SpatialAuthoringBinding
                {
                    RegionElement = state.RegionElement,
                    ObjectElement = localObjectElement,
                    RegionId = state.Plan.RegionId,
                    ObjectId = state.Plan.ObjectId,
                };
                return true;
            }
            catch (Exception ex)
            {
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                    $"Failed to link spatial authoring: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-bakes the affordance surfaces after the object was moved. The native surface stores world
        /// vertices, so a copy that its space moved must write them again from the unchanged local
        /// declaration. The support then stays the identity in the object frame wherever the copy is.
        /// </summary>
        public bool RefreshAuthoredSupports(long objectElement, Matrix4x4 worldTransform, bool force = false)
        {
            if (!_authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState state) ||
                state.Owned || state.AffordanceElements.Length == 0 || !IsFiniteMatrix(worldTransform))
            {
                return false;
            }
            if (!force && MatricesNearlyEqual(state.BakedWorld, worldTransform)) return false;
            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            Matrix4x4 frameWorld = worldTransform * state.Plan.FrameToObject;
            for (int index = 0; index < state.AffordanceElements.Length; ++index)
            {
                SpatialElementMapping mapping = state.AffordanceMappings[index];
                if (mapping.surface == null || mapping.surface.Length < 3) continue;
                SetSpatialHierarchySupport(nativeSpace, state.AffordanceElements[index],
                    SpatialAuthoringCompiler.BakeSurface(mapping.surface, frameWorld));
                if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            }
            state.BakedWorld = worldTransform;
            return true;
        }

        /// <summary>
        /// Follows an authored object that its space moved: the region carries the authoring frame's
        /// world pose, the object stays the identity under it, and the surfaces are baked again.
        /// Returns false when the object has no authored region.
        /// </summary>
        public bool TryUpdateAuthoredPose(long objectElement, Matrix4x4 worldTransform) =>
            TryUpdateAuthoredPose(objectElement, worldTransform, revertUnity: true);

        // `revertUnity` false: a refused write leaves the Unity transform alone (a held object's core pose, RefitCorePoseOnly).
        private bool TryUpdateAuthoredPose(long objectElement, Matrix4x4 worldTransform, bool revertUnity)
        {
            if (!_authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState state) ||
                state.Owned || state.RegionElement <= 0 || !IsFiniteMatrix(worldTransform))
            {
                return false;
            }
            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            Matrix4x4 previousFrame = state.RegionFrameWorld;
            state.RegionFrameWorld = worldTransform * state.Plan.FrameToObject;
            // A move can put the region on another anchor (a Chess moved onto a table) or off one: the hierarchy decides, so the
            // region is nested under the anchor that now contains it before its transform is written.
            _sceneLayout?.TryRehome(state.RegionElement);
            if (!TrySetSpatialHierarchyTransform(nativeSpace, state.RegionElement,
                    state.RegionParentWorldInverse * worldTransform * state.Plan.FrameToObject))
            {
                // Refused: Unity never diverges from the core. The object goes back where the core holds it.
                state.RegionFrameWorld = previousFrame;
                _sceneLayout?.TryRehome(state.RegionElement);
                Transform moved = revertUnity ? FindAuthoredEntity(objectElement)?.EntityTransform : null;
                if (moved != null)
                {
                    Matrix4x4 back = previousFrame * state.Plan.FrameToObject.inverse;
                    moved.SetPositionAndRotation(back.GetColumn(3), back.rotation);
                    moved.hasChanged = false;
                }
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"'{state.Plan.ObjectId}' was moved where the core refuses it ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}): it goes back.");
                return false;
            }
            SetSpatialHierarchyTransform(nativeSpace, objectElement, Matrix4x4.identity);
            RefreshAuthoredSupports(objectElement, worldTransform);
            // A trimmed region is declared in full again once, on the move that follows a drop (never on every frame of a grab).
            if (state.Trimmed && state.RedeclarePending)
            {
                state.RedeclarePending = false;
                RedeclareFullRegion(state);
            }
            return true;
        }

        // A last-resort trim is not forever: after the object moved, its full authored region is declared again when it fits there,
        // and trimmed again (to the part left free) only when it does not.
        private void RedeclareFullRegion(AuthoredObjectState state)
        {
            SpatialElementMapping mapping = Array.Find(state.Plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Region);
            Vector2[] full = WorldRing(state.Plan, state.RegionFrameWorld, SpatialHierarchyNodeKind.Region);
            if (mapping?.polygon == null || full == null || _registeredWalkable == null) return;
            var shape = new RegionPull.Shape
            {
                Region = full,
                RegionObstacles = RegionObstacles(state.ObjectElement),
            };
            if (RegionPull.IsValid(shape, Vector2.zero, _registeredWalkable.Boundary, RegionFitMarginMeters) &&
                WriteAuthoredRegion(_spaceID, state.RegionElement, mapping.polygon))
            {
                state.RegionPolygon = mapping.polygon;
                state.Trimmed = false;
                return;
            }
            Vector2[] fitted = FitPhysicalRegion(state.Plan, state.RegionFrameWorld, state.ObjectElement);
            if (fitted != null && WriteAuthoredRegion(_spaceID, state.RegionElement, fitted)) state.RegionPolygon = fitted;
        }

        /// <summary>
        /// The walkable ring last registered with the core, which a region under the room must lie inside. A seam so the
        /// authoring contract tests can give one without a NavMesh.
        /// </summary>
        protected virtual WalkableRegion AuthoringWalkable => _registeredWalkable;

        // The region a scene anchor's tree hangs under (instead of the room), with the inverse of the region's world transform.
        private struct AnchorRegionParent
        {
            public long Element;
            public Matrix4x4 WorldInverse;
            public long Space;
            public long Generation;
        }

        private readonly Dictionary<int, AnchorRegionParent> _anchorRegionByKey = new Dictionary<int, AnchorRegionParent>();

        /// <summary>Metres the region of a scene anchor keeps from the ring's edge and from its siblings.</summary>
        internal const float AnchorRegionMarginMeters = 0.005f;
        // How far past the box's world bounding box its region can reach. The stand-off is laid out in the box's own frame, so
        // for a box turned by θ its corner reaches s·(|cos θ| + |sin θ|) past it in world x or z, at most s·√2 at 45°.
        internal const float AnchorRegionReachMeters = SceneAnchorSpatialAuthoring.StandOffMeters * 1.41421356f + AnchorRegionMarginMeters;

        /// <summary>
        /// The Containment region of a scene anchor, in its authoring frame (<see cref="AnchorRegionFit"/>): the box widened by
        /// <see cref="SceneAnchorSpatialAuthoring.StandOffMeters"/> where the room's registered ring is (the NavMesh's ring
        /// merged with the furniture), trimmed off its siblings. Only a top-level anchor (a child of the room) gets one: its box
        /// moves under the region.
        /// </summary>
        private bool TryFitAnchorRegion(SpatialAuthoringPlan plan, Matrix4x4 frameWorld, long boxElement,
                                        out Vector2[] polygon, out Matrix4x4 boxWorld, out string reason,
                                        Vector2[] extraSibling = null)
        {
            polygon = null;
            reason = null;
            if (!TryGetTopLevelSceneGeometryWorld(SceneGeometryKeyOf(boxElement), out boxWorld))
            {
                reason = "it is not staged directly under the room";
                return false;
            }
            WalkableRegion walkable = AuthoringWalkable;
            SpatialElementMapping footprint = Array.Find(plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Object);
            if (walkable == null || !walkable.HasBoundary || footprint?.polygon == null || footprint.polygon.Length < 3)
            {
                reason = walkable == null || !walkable.HasBoundary ? "no walkable region is registered" : "it declares no footprint";
                return false;
            }
            Vector2 boxMin = footprint.polygon[0], boxMax = footprint.polygon[0];
            foreach (Vector2 p in footprint.polygon)
            {
                boxMin = Vector2.Min(boxMin, p);
                boxMax = Vector2.Max(boxMax, p);
            }
            // The authoring frame has +Y up, so its x and z are the ground plane.
            Matrix4x4 toFrame = frameWorld.inverse;
            float height = frameWorld.GetColumn(3).y;
            Vector2[] ToFrame(IReadOnlyList<Vector2> world)
            {
                var local = new Vector2[world.Count];
                for (int i = 0; i < local.Length; ++i)
                {
                    Vector3 l = toFrame.MultiplyPoint3x4(new Vector3(world[i].x, height, world[i].y));
                    local[i] = new Vector2(l.x, l.z);
                }
                return local;
            }
            var siblings = new List<Vector2[]>();
            CollectSiblingFootprints(siblings, boxElement);
            if (extraSibling != null) siblings.Add(extraSibling);
            // The region reaches at most the stand-off past the box: nothing farther can touch it.
            KeepNear(siblings, WorldRing(plan, frameWorld, SpatialHierarchyNodeKind.Object) ?? new[] { Vector2.zero },
                AnchorRegionReachMeters);
            for (int i = 0; i < siblings.Count; ++i) siblings[i] = ToFrame(siblings[i]);
            if (!AnchorRegionFit.TryFit(boxMin, boxMax, SceneAnchorSpatialAuthoring.StandOffMeters, ToFrame(walkable.Boundary),
                    siblings, AnchorRegionMarginMeters, out polygon, out _))
            {
                reason = "no stand-off around it reaches the walkable floor clear of its neighbours";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The region of an object that stands for a physical one (this user's own scan, installed where it was scanned and never
        /// moved), in its authoring frame (never refused, by snapping). Its footprint stays; the region around it
        /// is the scene anchor's rule (<see cref="AnchorRegionFit"/>): each side's stand-off as far as the declared region reaches,
        /// trimmed where it would leave the room's ring or overlap a wall, a piece of furniture or a sibling region. When no
        /// stand-off fits on any side, the region is the footprint itself. Null when the object declares no footprint.
        /// </summary>
        internal Vector2[] FitPhysicalRegion(SpatialAuthoringPlan plan, Matrix4x4 frameWorld, long excludeObjectElement = 0,
                                             IReadOnlyList<Vector2[]> extraObstacles = null)
        {
            WalkableRegion walkable = AuthoringWalkable;
            SpatialElementMapping footprint = Array.Find(plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Object);
            SpatialElementMapping region = Array.Find(plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Region);
            if (footprint?.polygon == null || footprint.polygon.Length < 3) return null;
            Vector2 boxMin = footprint.polygon[0], boxMax = footprint.polygon[0];
            foreach (Vector2 p in footprint.polygon) { boxMin = Vector2.Min(boxMin, p); boxMax = Vector2.Max(boxMax, p); }
            // The fallback region is the footprint polygon itself (its bounding box could overlap what the polygon does not).
            Vector2[] box = FootprintPolygon(plan) ?? new[] { boxMin, new Vector2(boxMax.x, boxMin.y), boxMax, new Vector2(boxMin.x, boxMax.y) };
            if (walkable == null || !walkable.HasBoundary || region?.polygon == null) return box;
            float standOff = float.MaxValue;
            Vector2 rMin = region.polygon[0], rMax = region.polygon[0];
            foreach (Vector2 p in region.polygon) { rMin = Vector2.Min(rMin, p); rMax = Vector2.Max(rMax, p); }
            standOff = Mathf.Min(Mathf.Min(boxMin.x - rMin.x, rMax.x - boxMax.x), Mathf.Min(boxMin.y - rMin.y, rMax.y - boxMax.y));
            if (standOff <= 1e-3f) return box;
            Matrix4x4 toFrame = frameWorld.inverse;
            float height = frameWorld.GetColumn(3).y;
            Vector2[] ToFrame(IReadOnlyList<Vector2> world)
            {
                var local = new Vector2[world.Count];
                for (int i = 0; i < local.Length; ++i)
                {
                    Vector3 l = toFrame.MultiplyPoint3x4(new Vector3(world[i].x, height, world[i].y));
                    local[i] = new Vector2(l.x, l.z);
                }
                return local;
            }
            List<Vector2[]> obstacles = RegionObstacles(excludeObjectElement, extraObstacles);
            KeepNear(obstacles, WorldRing(plan, frameWorld, SpatialHierarchyNodeKind.Object) ?? new[] { Vector2.zero },
                standOff * 1.41421356f + AnchorRegionMarginMeters);
            for (int i = 0; i < obstacles.Count; ++i) obstacles[i] = ToFrame(obstacles[i]);
            return AnchorRegionFit.TryFit(boxMin, boxMax, standOff, ToFrame(walkable.Boundary), obstacles, AnchorRegionMarginMeters,
                out Vector2[] fitted, out _) ? fitted : box;
        }

        // The scene-geometry key whose tree has this element as its root, or 0.
        private int SceneGeometryKeyOf(long element)
        {
            if (element <= 0) return 0;
            foreach (KeyValuePair<int, OwnedSceneGeometry> pair in _ownedLocalSceneGeometry)
            {
                if (pair.Value.Parent == element) return pair.Key;
            }
            return 0;
        }

        /// <summary>The world transform of a scene anchor's element when it is staged directly under the room. A seam for the tests.</summary>
        protected virtual bool TryGetTopLevelSceneGeometryWorld(int key, out Matrix4x4 world)
        {
            world = Matrix4x4.identity;
            if (key == 0 || _sceneLayout == null || !_sceneLayout.TryGetParent(key, out int parent) ||
                parent != SceneGeometryLayoutEngine.Room || !_sceneLayout.TryGetRequested(key, out SceneGeometryDescriptor d))
                return false;
            world = SceneGeometryFootprint.Element(d);
            return true;
        }

        protected virtual bool ReparentSpatialHierarchyElement(long nativeSpace, long element, long parent, Matrix4x4 local) =>
            SpatialBlendAPI.TryReparentElement(nativeSpace, element, parent, local);

        /// <summary>
        /// A rebuilt walkable ring is checked by the core against the regions already under the room: one that sticks out of
        /// the new ring makes the core refuse the RING. Each room places its own copy, so such an object is moved first, into
        /// the new ring and clear of its siblings. Its move is checked against the outline still registered, so it is made
        /// only when the moved region also lies inside that one; otherwise the region is named and the user moves it.
        /// </summary>
        private void RefitRegionsToWalkable(WalkableRegion next)
        {
            foreach (AuthoredObjectState state in StatesInIdOrder())
            {
                if (state.Owned || state.RegionElement <= 0 || state.RegionParentWorldInverse != Matrix4x4.identity) continue;
                Vector2[] ring = CurrentRegionRing(state);
                if (ring == null) continue;
                if (IsPhysicalObject(FindAuthoredEntity(state.ObjectElement)?.gameObject)) continue;
                // THE placement (RoomFit.Place) in the new ring; the core keeps the old outline until it is registered, so a move
                // it refuses goes back (TryUpdateAuthoredPose).
                if (PlaceAuthoredByRule(state, null, next.Boundary, out string problem)) continue;
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"The rebuilt walkable region does not contain region {state.RegionElement} of '{state.Plan.ObjectId}' and " +
                    $"{problem}: the core keeps the previous room outline while that region is registered. Move the object " +
                    "into the new walkable area, or share the room again.");
            }
        }

        /// <summary>
        /// Furniture is about to be staged (the MRUK adapter registered or moved an anchor): a virtual object whose region its
        /// box would overlap is moved clear of it first, as each room places its own copy, so the core admits the box. The
        /// fit is against the ring registered now, its siblings and the arriving box.
        /// </summary>
        private void MakeRoomForSceneGeometry(SceneGeometryDescriptor descriptor)
        {
            if (SceneGeometryFootprint.TryRing(descriptor, out Vector2[] footprint)) MakeRoomFor(footprint, "furniture");
        }

        /// <summary>
        /// Something that does not move (furniture, a shared box, an own scan) takes <paramref name="footprint"/>: the scene
        /// anchors' stand-offs are trimmed off it and the virtual regions over it step aside.
        /// </summary>
        internal void MakeRoomFor(Vector2[] footprint, string what, long excludeObjectElement = 0)
        {
            if (_registeredWalkable == null || footprint == null) return;
            foreach (AuthoredObjectState state in StatesInIdOrder())
            {
                if (state.ObjectElement == excludeObjectElement || state.RegionElement <= 0) continue;
                if (state.Owned) RetrimAnchorRegion(state, footprint);
                else if (IsPhysicalObject(FindAuthoredEntity(state.ObjectElement)?.gameObject)) TrimPhysicalRegion(state, footprint, what);
            }
            MoveVirtualRegionsOff(footprint, what);
        }

        // The authored states in a fixed order (by object element), so a sequence of refits never depends on arrival order.
        private List<AuthoredObjectState> StatesInIdOrder()
        {
            var states = new List<AuthoredObjectState>(_authoredStateByObject.Values);
            states.Sort((a, b) => a.ObjectElement.CompareTo(b.ObjectElement));
            return states;
        }

        /// <summary>Whether an object stands for a physical one: an own scan (<see cref="PhysicalCounterpart"/>) or furniture (a scene anchor, or the box a joiner installs for a shared one). Such an object is never pulled; its region is fitted around it.</summary>
        internal static bool IsPhysicalObject(GameObject sceneObject) =>
            sceneObject != null && (sceneObject.GetComponentInParent<PhysicalCounterpart>() != null ||
                                    sceneObject.GetComponentInParent<SbMRUKAnchorEntity>() != null);

        // The region an authored object holds now (world ground ring): the polygon written to the core (trimmed, for a physical
        // object), else the declared one.
        private static Vector2[] CurrentRegionRing(AuthoredObjectState state) =>
            state.RegionPolygon != null && state.RegionPolygon.Length >= 3
                ? ToWorldRing(state.RegionPolygon, state.RegionFrameWorld)
                : WorldRing(state.Plan, state.RegionFrameWorld, SpatialHierarchyNodeKind.Region);

        // A physical object's region (not a scene anchor's) is trimmed off something that does not move.
        private void TrimPhysicalRegion(AuthoredObjectState state, Vector2[] footprint, string what)
        {
            Vector2[] ring = CurrentRegionRing(state);
            if (ring == null || !Overlaps(ring, footprint)) return;
            Vector2[] fitted = FitPhysicalRegion(state.Plan, state.RegionFrameWorld, state.ObjectElement, new[] { footprint });
            if (fitted != null && WriteAuthoredRegion(_spaceID, state.RegionElement, fitted))
            {
                state.RegionPolygon = fitted;
                return;
            }
            SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                $"A {what} arrives over the region of '{state.Plan.ObjectId}' (a physical object), which could not be trimmed off it " +
                $"({SpatialHierarchySceneError(_spaceID) ?? "no fit"}): the core refuses the {what}.");
        }

        /// <summary>
        /// The one rule for a physical object (an own scan, or the box a joiner installs over its real table) at a pose the user
        /// gave it: it is never pulled. What yields to it does first (virtual regions move off its footprint,
        /// other physical and scene-anchor regions are trimmed off it), its region shrinks to its footprint, it moves, and its
        /// region is fitted around it (<see cref="FitPhysicalRegion"/>). Every refusal is logged as an error. False when the
        /// object has no authored region here or the core refused its pose.
        /// </summary>
        public bool RefitPhysicalAtPose(long objectElement, Matrix4x4 worldTransform)
        {
            if (!_authoredStateByObject.TryGetValue(objectElement, out AuthoredObjectState state) ||
                state.Owned || state.RegionElement <= 0 || !IsFiniteMatrix(worldTransform))
            {
                return false;
            }
            long nativeSpace = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsSpatialHierarchyRegistrationCurrent(nativeSpace, generation)) return false;
            string name = state.Plan.ObjectId;
            Matrix4x4 frameWorld = worldTransform * state.Plan.FrameToObject;
            Vector2[] footprint = WorldFootprintRing(state.Plan, frameWorld);
            MakeRoomFor(footprint, $"placed '{name}'", objectElement);
            Vector2[] box = FootprintPolygon(state.Plan);
            // A wall the object now crosses gives way before anything is written there (RetrimWalls).
            RetrimWalls(WallCut(footprint, box != null ? ToWorldRing(box, frameWorld) : null), objectElement);
            Vector2[] previousPolygon = state.RegionPolygon;
            if (box != null)
            {
                if (WriteAuthoredRegion(nativeSpace, state.RegionElement, box)) state.RegionPolygon = box;
                else
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                        $"'{name}': the core refused to shrink its region to its footprint before the move ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}).");
            }
            Matrix4x4 previousFrame = state.RegionFrameWorld;
            state.RegionFrameWorld = frameWorld;
            _sceneLayout?.TryRehome(state.RegionElement);
            if (!TrySetSpatialHierarchyTransform(nativeSpace, state.RegionElement, state.RegionParentWorldInverse * frameWorld))
            {
                state.RegionFrameWorld = previousFrame;
                string refusal = SpatialHierarchySceneError(nativeSpace) ?? "no message";
                Transform own = FindAuthoredEntity(objectElement)?.EntityTransform;
                if (own != null && IsPhysicalCounterpart(own.gameObject) && WithdrawRegion(state, frameWorld))
                {
                    RetrimWalls();
                    // An own scan stays where the real object is. Its region at the old pose is withdrawn and its
                    // object element stands at the real pose under the room (when the core takes that), so no relation enters the
                    // stale region. A later pose the core takes declares it again.
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                        $"'{name}' stands where the core refuses its region ({refusal}): its region is withdrawn until a pose the core takes.");
                    return false;
                }
                // The core kept the previous pose: the region it had there goes back too (it was shrunk for the move).
                if (previousPolygon != null && WriteAuthoredRegion(nativeSpace, state.RegionElement, previousPolygon)) state.RegionPolygon = previousPolygon;
                // And the object goes back where the core holds it (a box shows as not placed again). Never an own scan: it stands
                // where the real object is, and nothing on this side moves it.
                Transform moved = FindAuthoredEntity(objectElement)?.EntityTransform;
                if (moved != null && !IsPhysicalCounterpart(moved.gameObject))
                {
                    Matrix4x4 back = previousFrame * state.Plan.FrameToObject.inverse;
                    moved.SetPositionAndRotation(back.GetColumn(3), back.rotation);
                }
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"'{name}' was placed where the core refuses it ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}): it keeps its previous pose in the core.");
                return false;
            }
            // A withdrawn region takes its identity and its object back first (the object comes back under it at the identity).
            if (state.Withdrawn) RedeclareRegion(state);
            SetSpatialHierarchyTransform(nativeSpace, objectElement, Matrix4x4.identity);
            RefreshAuthoredSupports(objectElement, worldTransform);
            WriteFittedPhysicalRegion(state, frameWorld, footprint);
            return true;
        }

        // The physical object's region fitted around it at its pose; the walls it crosses give way to it first. Kept as its cut.
        private void WriteFittedPhysicalRegion(AuthoredObjectState state, Matrix4x4 frameWorld, Vector2[] footprint)
        {
            long nativeSpace = _spaceID;
            Vector2[] fitted = FitPhysicalRegion(state.Plan, frameWorld, state.ObjectElement);
            if (fitted != null)
            {
                RetrimWalls(WallCut(footprint, ToWorldRing(fitted, frameWorld)), state.ObjectElement);
                if (WriteAuthoredRegion(nativeSpace, state.RegionElement, fitted)) state.RegionPolygon = fitted;
                else
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                        $"'{state.Plan.ObjectId}': the core refused its region fitted at its new pose ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}); it keeps its footprint as its region.");
            }
            Vector2[] held = state.RegionPolygon != null ? ToWorldRing(state.RegionPolygon, frameWorld) : null;
            _wallCutsByObject[state.ObjectElement] = WallCut(footprint, held);
            RetrimWalls();
        }

        // An own scan's region where the core refused its pose: the object element goes under the room at its real pose, and the
        // region is withdrawn in place (the pattern of a retired scene anchor's region the core keeps): no shared id, no tag, no
        // transfer leaf, and no longer this object's region on this side.
        private bool WithdrawRegion(AuthoredObjectState state, Matrix4x4 realFrame)
        {
            long nativeSpace = _spaceID;
            long region = state.RegionElement;
            // The object element out of the region, under the room at the pose the object has (its frame; the room is the world).
            long room = EnsureSpatialHierarchyRoom(nativeSpace);
            state.ObjectUnderRoom = room > 0 && ReparentSpatialHierarchyElement(nativeSpace, state.ObjectElement, room, realFrame);
            if (!state.ObjectUnderRoom)
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"'{state.Plan.ObjectId}': the core refused its object at its real pose too ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}); " +
                    "it stays under its withdrawn region at the pose the core holds.");
            SetSpatialHierarchyContainmentTransfer(nativeSpace, region, false);
            _containmentRegions.Remove(region);
            SetSpatialHierarchySharedId(nativeSpace, region, 0);
            SetSpatialHierarchyTag(nativeSpace, region, string.Empty);
            _authoredRegionByObject.Remove(state.ObjectElement);
            state.Withdrawn = true;
            return true;
        }

        // A withdrawn region taken back at a pose the core accepted: its identity, its tag and its transfer leaf again.
        private void RedeclareRegion(AuthoredObjectState state)
        {
            long nativeSpace = _spaceID;
            SetSpatialHierarchySharedId(nativeSpace, state.RegionElement, SpatialAuthoringCompiler.RegionSharedId(state.ObjectSharedId));
            SpatialElementMapping mapping = Array.Find(state.Plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Region);
            if (!string.IsNullOrEmpty(mapping?.tag)) SetSpatialHierarchyTag(nativeSpace, state.RegionElement, mapping.tag);
            if (state.RegionPolygon != null) WriteAuthoredRegion(nativeSpace, state.RegionElement, state.RegionPolygon);
            // The object back under its region, the identity there (the region carries the pose).
            if (state.ObjectUnderRoom)
            {
                if (ReparentSpatialHierarchyElement(nativeSpace, state.ObjectElement, state.RegionElement, Matrix4x4.identity))
                    state.ObjectUnderRoom = false;
                else
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                        $"'{state.Plan.ObjectId}': its object could not go back under its region ({SpatialHierarchySceneError(nativeSpace) ?? "no message"}).");
            }
            _authoredRegionByObject[state.ObjectElement] = state.RegionElement;
            state.Withdrawn = false;
        }

        /// <summary>The element's parent in the core. A seam for the tests.</summary>
        protected virtual bool TryGetSpatialHierarchyParent(long nativeSpace, long element, out long parent) =>
            SpatialBlendAPI.TryGetElementParent(nativeSpace, element, out parent);

        // The object's footprint polygon in the authoring frame, counter-clockwise (the smallest region a physical object has), or null.
        private static Vector2[] FootprintPolygon(SpatialAuthoringPlan plan)
        {
            SpatialElementMapping footprint = Array.Find(plan.Mappings, m => m.kind == SpatialHierarchyNodeKind.Object);
            if (footprint?.polygon == null || footprint.polygon.Length < 3) return null;
            var ring = (Vector2[])footprint.polygon.Clone();
            if (PolygonBoolean.SignedArea(ring) < 0f) Array.Reverse(ring);
            return ring;
        }

        /// <summary>
        /// Something that cannot move (furniture, an own scan) is about to take <paramref name="footprint"/>: every virtual
        /// object whose region it would overlap is pulled to the nearest valid spot clear of it first, so the core never has
        /// to refuse either of them.
        /// </summary>
        internal void MoveVirtualRegionsOff(Vector2[] footprint, string what)
        {
            if (_registeredWalkable == null || footprint == null) return;
            foreach (AuthoredObjectState state in StatesInIdOrder())
            {
                if (state.Owned || state.RegionElement <= 0 || state.RegionParentWorldInverse != Matrix4x4.identity) continue;
                if (IsPhysicalObject(FindAuthoredEntity(state.ObjectElement)?.gameObject)) continue;
                Vector2[] ring = CurrentRegionRing(state);
                if (ring == null || !Overlaps(ring, footprint)) continue;
                // THE placement (RoomFit.Place: a wall-mounted object slides along the walls, then stands on the floor; nothing
                // else moves it off a wall), with the arriving footprint as an obstacle; when nothing fits, the last-resort trim.
                if (PlaceAuthoredByRule(state, new[] { footprint }, null, out string problem)) continue;
                if (TrimVirtualRegionOff(state, footprint, what)) continue;
                problem = $"it fits nowhere in this room clear of that {what} ({problem}), and its own footprint is under it, so " +
                          "no part of its region is free: the core refuses the arrival";
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"A {what} arrives over region {state.RegionElement} of '{state.Plan.ObjectId}' and {problem}.");
            }
        }

        // A scene anchor cannot move out of the way: its region's stand-off is trimmed off the arriving furniture instead.
        private void RetrimAnchorRegion(AuthoredObjectState state, Vector2[] footprint)
        {
            Vector2[] ring = state.RegionPolygon == null ? null : ToWorldRing(state.RegionPolygon, state.RegionFrameWorld);
            if (ring == null || !Overlaps(ring, footprint)) return;
            if (TryFitAnchorRegion(state.Plan, state.RegionFrameWorld, state.ObjectElement, out Vector2[] polygon, out _,
                    out string why, footprint) &&
                WriteAuthoredRegion(_spaceID, state.RegionElement, polygon))
            {
                state.RegionPolygon = polygon;
                return;
            }
            SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                $"Furniture arrives over the region of scene anchor '{state.Plan.ObjectId}', which cannot be trimmed off it " +
                $"({why ?? SpatialHierarchySceneError(_spaceID) ?? "the core refused it"}): the core refuses the furniture's box.");
        }

        private static Vector2[] ToWorldRing(Vector2[] local, Matrix4x4 frameWorld)
        {
            var ring = new Vector2[local.Length];
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector3 w = frameWorld.MultiplyPoint3x4(new Vector3(local[i].x, 0f, local[i].y));
                ring[i] = new Vector2(w.x, w.z);
            }
            return ring;
        }

        private static bool Overlaps(Vector2[] a, Vector2[] b)
        {
            if (WalkableRegionFit.EdgesCross(a, b)) return true;
            foreach (Vector2 p in a) if (WalkablePlacementSnap.Contains(b, p)) return true;
            foreach (Vector2 p in b) if (WalkablePlacementSnap.Contains(a, p)) return true;
            return false;
        }

        // THE placement for an authored virtual object (RoomFit.Place, the one rule, on the plan the core holds), then written to the
        // core; a refused write puts it back (TryUpdateAuthoredPose). False, with the problem, when nothing fits or it was refused.
        private bool PlaceAuthoredByRule(AuthoredObjectState state, IReadOnlyList<Vector2[]> extra, Vector2[] ringOverride, out string problem)
        {
            problem = null;
            SbEntity entity = FindAuthoredEntity(state.ObjectElement);
            if (entity == null) { problem = "its object is not registered here"; return false; }
            Transform root = entity.transform;
            Transform frame = entity.EntityTransform != null ? entity.EntityTransform : root;
            // Held or settling: never moved under the user's hand, but the pose the core holds for it (its
            // pre-grab one) is refitted, so the arriving furniture or the new room is not refused against it. Its drop then places it
            // by the same rule and pushes the dropped pose.
            if (entity.GetComponentInChildren<Interactions.PlacementMotif>()?.InMotion == true) return RefitCorePoseOnly(state, root, frame, extra, ringOverride, out problem);
            Vector3 p0 = root.position;
            Quaternion r0 = root.rotation;
            if (!RoomFit.Place(root.gameObject, _roomRegion, this, null, RegionFitMarginMeters, extra, ringOverride, out string reason,
                    state.Plan, frame))
            {
                problem = "it fits nowhere in this room";
                return false;
            }
            if ((root.position - p0).sqrMagnitude < 1e-10f && Quaternion.Angle(root.rotation, r0) < 0.01f) return true;
            if (!TryUpdateAuthoredPose(state.ObjectElement, frame.localToWorldMatrix))
            {
                root.SetPositionAndRotation(p0, r0);
                problem = $"the core refused its new pose ({SpatialHierarchySceneError(_spaceID) ?? "no message"})";
                return false;
            }
            SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Scene,
                $"Moved '{state.Plan.ObjectId}': {reason}; the room's walkable area or furniture changed.");
            return true;
        }

        // The core's pose of a held object, placed by the rule and written to the core alone: the object is put at that pose for the
        // placement and put back where the user holds it, so Unity's transform is never moved under the hand.
        private bool RefitCorePoseOnly(AuthoredObjectState state, Transform root, Transform frame, IReadOnlyList<Vector2[]> extra,
                                       Vector2[] ringOverride, out string problem)
        {
            problem = null;
            Vector3 heldP = root.position;
            Quaternion heldR = root.rotation;
            bool heldChanged = root.hasChanged;
            Matrix4x4 coreFrame = state.RegionFrameWorld * state.Plan.FrameToObject.inverse;   // the frame's world pose the core holds
            // The root's pose that puts the frame there (the frame may be a child of the root): the core frame, then the root as
            // seen from the frame (the frame's offset is applied in the frame, not in the world).
            Matrix4x4 coreRoot = coreFrame * frame.localToWorldMatrix.inverse * root.localToWorldMatrix;
            root.SetPositionAndRotation(coreRoot.GetColumn(3), coreRoot.rotation);
            try
            {
                Vector3 p0 = root.position;
                Quaternion r0 = root.rotation;
                if (!RoomFit.Place(root.gameObject, _roomRegion, this, null, RegionFitMarginMeters, extra, ringOverride, out string reason,
                        state.Plan, frame))
                {
                    problem = "it fits nowhere in this room";
                    return false;
                }
                if ((root.position - p0).sqrMagnitude < 1e-10f && Quaternion.Angle(root.rotation, r0) < 0.01f) return true;
                if (!TryUpdateAuthoredPose(state.ObjectElement, frame.localToWorldMatrix, revertUnity: false))
                {
                    problem = $"the core refused its new pose ({SpatialHierarchySceneError(_spaceID) ?? "no message"})";
                    return false;
                }
                SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Scene,
                    $"Refitted the core's pose of held '{state.Plan.ObjectId}': {reason}; its drop places it.");
                return true;
            }
            finally
            {
                root.SetPositionAndRotation(heldP, heldR);
                // The user's hand moved it (or not) since the last push: the push at rest is still owed exactly as before.
                root.hasChanged = true;
                if (!heldChanged && frame != root) frame.hasChanged = true;
            }
        }

        /// <summary>A drop: the object's trimmed region (if any) is declared in full again on its next pose update.</summary>
        internal void RequestRegionRedeclare(GameObject root)
        {
            SbEntity entity = root != null ? root.GetComponentInParent<SbEntity>() : null;
            if (entity != null && _authoredStateByObject.TryGetValue(entity.ID, out AuthoredObjectState state) && state.Trimmed)
                state.RedeclarePending = true;
        }

        /// <summary>Last-resort counts (the room matrix reads them): virtual regions trimmed to their free part, and arrivals with none left.</summary>
        internal int VirtualRegionsTrimmed { get; private set; }
        internal int VirtualRegionsWithNoFreePart { get; private set; }

        // The last resort for a virtual object that fits nowhere else: it keeps its object where it is and its
        // REGION is trimmed to the part left free around its footprint (never refused). False when its footprint itself lies under
        // the arriving thing: no part is free.
        private bool TrimVirtualRegionOff(AuthoredObjectState state, Vector2[] footprint, string what)
        {
            Vector2[] own = WorldRing(state.Plan, state.RegionFrameWorld, SpatialHierarchyNodeKind.Object);
            if (own != null && Overlaps(own, footprint))
            {
                ++VirtualRegionsWithNoFreePart;
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"LAST RESORT: '{state.Plan.ObjectId}' fits nowhere in this room and the {what} stands on its own footprint: no part " +
                    "of its region is free.");
                return false;
            }
            Vector2[] fitted = FitPhysicalRegion(state.Plan, state.RegionFrameWorld, state.ObjectElement, new[] { footprint });
            if (fitted == null || !WriteAuthoredRegion(_spaceID, state.RegionElement, fitted))
            {
                ++VirtualRegionsWithNoFreePart;
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Scene,
                    $"LAST RESORT: '{state.Plan.ObjectId}' fits nowhere in this room and its region could not be trimmed off the {what} " +
                    $"({SpatialHierarchySceneError(_spaceID) ?? "no fit"}).");
                return false;
            }
            state.RegionPolygon = fitted;
            state.Trimmed = true;
            ++VirtualRegionsTrimmed;
            SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Scene,
                $"LAST RESORT: '{state.Plan.ObjectId}' fits nowhere in this room clear of the {what}: it stays, and its region is " +
                "trimmed to the part left free.");
            return true;
        }

        // The registered entity of an element. A seam for the tests.
        protected virtual SbEntity FindAuthoredEntity(long element)
        {
            lock (_entitiesLock)
            {
                return _entities.Find(e => e != null && e.ID == element);
            }
        }

        // False when the core refused the transform (a region moved where it sticks out of its parent or overlaps a sibling).
        protected virtual bool TrySetSpatialHierarchyTransform(long nativeSpace, long element, Matrix4x4 localTransform) =>
            SpatialBlendAPI.SetElementTransform(nativeSpace, element, localTransform);

        // The object's footprint on the ground in world (x, z), counter-clockwise, from its compiled declaration; null when it has none.
        private static Vector2[] WorldFootprintRing(SpatialAuthoringPlan plan, Matrix4x4 frameWorld) =>
            WorldRing(plan, frameWorld, SpatialHierarchyNodeKind.Object);

        // The ground ring of the declaration's object footprint or region extent, in world (x, z), counter-clockwise.
        /// <summary>The ground ring share time uses for a compiled declaration (the lobby asks for the same one).</summary>
        internal static Vector2[] WorldGroundRing(SpatialAuthoringPlan plan, Matrix4x4 frameWorld, SpatialHierarchyNodeKind kind) =>
            WorldRing(plan, frameWorld, kind);

        private static Vector2[] WorldRing(SpatialAuthoringPlan plan, Matrix4x4 frameWorld, SpatialHierarchyNodeKind kind)
        {
            SpatialElementMapping mapping = Array.Find(plan.Mappings, m => m.kind == kind);
            if (mapping == null || mapping.polygon == null || mapping.polygon.Length < 3) return null;
            var ring = new Vector2[mapping.polygon.Length];
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector3 w = frameWorld.MultiplyPoint3x4(new Vector3(mapping.polygon[i].x, 0f, mapping.polygon[i].y));
                ring[i] = new Vector2(w.x, w.z);
            }
            return CounterClockwise(ring);
        }

        internal static Vector2[] CounterClockwise(Vector2[] ring)
        {
            double sum = 0.0;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            if (sum < 0.0) Array.Reverse(ring);
            return ring;
        }

        private static bool MatricesNearlyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (int index = 0; index < 16; ++index)
            {
                if (Mathf.Abs(a[index] - b[index]) > 1e-5f) return false;
            }
            return true;
        }

        private static bool IsFiniteMatrix(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; ++index)
            {
                if (!SpatialHierarchyDescriptor.IsFinite(matrix[index])) return false;
            }
            return true;
        }

        // Cross-device support correspondence is NOT registered through the owned API here.
        // sb_resolve_shared_element (LinkToExisting) already writes the host->receiver link
        // (core space.cc, which also records the source handle) and the receiver decodes
        // through that link (space.cc, relation_evaluator.cc). The owned API
        // (spatialblend.h) needs both spaces in the one session's context
        // (owned_correspondence_c.inc) and a free source->target-space slot
        // (element_linker.cc). A peer device's space is not in this context, and after a
        // resolve the slot is taken, so it is refused. It stays for FoundationLocal
        // (SpatialBlendLocalWorld), where both spaces live in one process.
    }
}
