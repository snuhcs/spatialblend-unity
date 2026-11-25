// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    public partial class SpatialBlendManager
    {
        /// <summary>
        /// Creates a generation- and registration-bound ingress sink for a local
        /// source actor. The sink only records native anchor observations; it never
        /// drives realization, root motion, or anchor registration.
        /// </summary>
        public bool TryCreateAnchorPoseInput(SbEntity source, out IAnchorPoseFrameSink sink)
        {
            sink = null;
            if (source == null || !source.IsSource || !source.IsAttached() || !Initialized ||
                ConnectionState != SpatialBlendConnectionState.Connected) return false;

            lock (_entitiesLock)
            {
                if (!_entityRegistrations.TryGetValue(source, out EntityRegistration registration) ||
                    !registration.IsSource || registration.Id != source.ID ||
                    !_v2ActorByEntity.TryGetValue(registration.Id, out long actor) || actor <= 0)
                {
                    return false;
                }
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in _v2ActorAnchors)
                {
                    if (pair.actor == actor) known.Add(pair.name);
                }
                if (known.Count == 0) return false;
                sink = new ManagerAnchorPoseInput(this, source, _spaceID, ConnectionGeneration,
                    registration.Id, registration.Token, actor, known);
                return true;
            }
        }

        private bool IsAnchorPoseInputLive(SbEntity source, long capturedSpace,
                                           long capturedGeneration, long capturedEntity,
                                           long capturedToken, long capturedActor,
                                           string anchorId)
        {
            lock (_entitiesLock)
            {
                return source != null && source.IsAttached() && source.IsSource && Initialized &&
                    ConnectionState == SpatialBlendConnectionState.Connected &&
                    _spaceID == capturedSpace && IsCurrentLifecycleGeneration(capturedGeneration) &&
                    _entityRegistrations.TryGetValue(source, out EntityRegistration registration) &&
                    registration.IsSource && registration.Id == capturedEntity &&
                    registration.Token == capturedToken && source.ID == capturedEntity &&
                    _v2ActorByEntity.TryGetValue(capturedEntity, out long currentActor) &&
                    currentActor == capturedActor &&
                    _v2ActorAnchors.Contains((capturedActor, anchorId));
            }
        }

        protected virtual void SetAnchorPoseInputTransform(long space, long actor, string anchorId,
                                                            Vector3 worldPosition, float unityYawDegrees) =>
            SpatialBlendAPI.SetAnchorTransform(space, actor, anchorId, worldPosition, unityYawDegrees);

        protected virtual void ClearAnchorPoseInputTransform(long space, long actor, string anchorId) =>
            SpatialBlendAPI.ClearAnchorTransform(space, actor, anchorId);

        private sealed class ManagerAnchorPoseInput : IAnchorPoseFrameReplaySink
        {
            private readonly SpatialBlendManager _manager;
            private readonly SbEntity _source;
            private readonly long _space;
            private readonly long _generation;
            private readonly long _entity;
            private readonly long _token;
            private readonly long _actor;
            private readonly HashSet<string> _known;
            private readonly HashSet<string> _active = new HashSet<string>(StringComparer.Ordinal);
            private long _lastSequence = -1;
            private double _lastTime = double.NegativeInfinity;
            private bool _applying;

            internal ManagerAnchorPoseInput(SpatialBlendManager manager, SbEntity source, long space,
                                            long generation, long entity, long token, long actor,
                                            HashSet<string> known)
            {
                _manager = manager;
                _source = source;
                _space = space;
                _generation = generation;
                _entity = entity;
                _token = token;
                _actor = actor;
                _known = known;
            }

            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                if (_applying || !AnchorPoseFrameValidation.TryValidateFrame(frame, _lastSequence,
                    _lastTime, _known, out _) || !IsLiveKnownSession()) return false;

                // Defend this production boundary even when callers bypass
                // RecordedAnchorPoseSource and mutate their DTO during a hook.
                AnchorPoseFrame snapshot = AnchorPoseFrameValidation.DeepCopy(frame);
                var next = new HashSet<string>(StringComparer.Ordinal);
                foreach (AnchorPoseSample sample in snapshot.anchors) next.Add(sample.anchorId);
                var removed = new List<string>();
                foreach (string previous in _active)
                {
                    if (!next.Contains(previous)) removed.Add(previous);
                }
                removed.Sort(StringComparer.Ordinal);

                _applying = true;
                try
                {
                    // Validation above completes before any native mutation.
                    foreach (AnchorPoseSample sample in snapshot.anchors)
                    {
                        if (!IsLive(sample.anchorId)) return false;
                        float yaw = sample.worldRotation.eulerAngles.y;
                        if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return false;
                        _manager.SetAnchorPoseInputTransform(_space, _actor, sample.anchorId,
                            sample.worldPosition, yaw);
                    }
                    foreach (string anchorId in removed)
                    {
                        if (!IsLive(anchorId)) return false;
                        _manager.ClearAnchorPoseInputTransform(_space, _actor, anchorId);
                    }
                    _active.Clear();
                    _active.UnionWith(next);
                    _lastSequence = snapshot.sequence;
                    _lastTime = snapshot.timeSeconds;
                    return true;
                }
                catch (Exception exception)
                {
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.State,
                        $"Anchor-pose input rejected native observation: {exception.Message}");
                    return false;
                }
                finally
                {
                    _applying = false;
                }
            }

            public void ClearWorldAnchor(string anchorId)
            {
                if (!AnchorPoseFrameValidation.IsValidAnchorId(anchorId) || !_known.Contains(anchorId) ||
                    !IsLive(anchorId)) return;
                try
                {
                    _manager.ClearAnchorPoseInputTransform(_space, _actor, anchorId);
                    _active.Remove(anchorId);
                }
                catch (Exception exception)
                {
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.State,
                        $"Anchor-pose input failed to clear observation: {exception.Message}");
                }
            }

            public void ResetWorldAnchorReplay()
            {
                if (_applying) return;
                _active.Clear();
                _lastSequence = -1;
                _lastTime = double.NegativeInfinity;
            }

            private bool IsLive(string anchorId) => _manager != null &&
                _manager.IsAnchorPoseInputLive(_source, _space, _generation, _entity, _token, _actor,
                    anchorId);

            private bool IsLiveKnownSession()
            {
                // There is no synthetic empty anchor. Use one captured known anchor
                // to validate the session before frame work; each hook rechecks its
                // own name immediately before writing.
                foreach (string anchorId in _known) return IsLive(anchorId);
                return false;
            }
        }
    }
}
