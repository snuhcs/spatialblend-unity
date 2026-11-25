// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// Per-FoundationLocal lifetime for the original scene's authored actor chart.
    /// It owns only transient native owners and the local avatar clone; the actors,
    /// source, replay player, targets, and reference transforms remain authored
    /// scene components and are never destroyed here.
    /// </summary>
    public sealed class FreeformSceneActorRuntime
    {
        private sealed class MotionForwardingSink : IAnchorPoseFrameCheckedReplaySink,
                                                    IAnchorPoseFrameRetiredReplaySink
        {
            private readonly IAnchorPoseFrameSink _native;
            private readonly FreeformSceneActorRuntime _runtime;

            public MotionForwardingSink(IAnchorPoseFrameSink native,
                                        FreeformSceneActorRuntime runtime)
            {
                _native = native;
                _runtime = runtime;
            }

            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                // Native admission is authoritative. A retarget/SDK consumer failure
                // is separately observable and must never undo an accepted frame.
                return _runtime.TryApplySourceFrame(this, _native, frame);
            }

            public void ClearWorldAnchor(string anchor)
            {
                if (_runtime.CanUseForwardingSink(this))
                    _native?.ClearWorldAnchor(anchor);
            }

            public void ResetWorldAnchorReplay()
            {
                if (!_runtime.CanUseForwardingSink(this))
                    return;
                // Do not clear Unity-side consumer state after an unacknowledged
                // void reset. Our actor sink is checked; a foreign void sink gets no
                // claim that native retirement/reset actually succeeded.
                if (_native is IAnchorPoseFrameCheckedReplaySink checkedSink &&
                    checkedSink.TryResetWorldAnchorReplay() &&
                    _runtime.CanUseForwardingSink(this))
                {
                    _runtime.ClearConsumerObservations();
                }
            }

            public bool TryResetWorldAnchorReplay()
            {
                if (!_runtime.CanUseForwardingSink(this))
                    return false;
                if (_native is IAnchorPoseFrameCheckedReplaySink checkedSink)
                {
                    if (!checkedSink.TryResetWorldAnchorReplay())
                        return false;
                }
                else
                {
                    if (_native is IAnchorPoseFrameReplaySink replaySink)
                        replaySink.ResetWorldAnchorReplay();
                }
                if (_runtime.CanUseForwardingSink(this))
                    _runtime.ClearConsumerObservations();
                return true;
            }

            public bool IsReplaySinkRetired => !_runtime.CanUseForwardingSink(this) ||
                (_native is IAnchorPoseFrameRetiredReplaySink retired && retired.IsReplaySinkRetired);
        }

        private readonly FreeformSceneActorDefinition _definition;
        private readonly SpatialBlendLocalWorld _world;
        private readonly Transform _ownedParent;
        private readonly SpatialBlendActor _sourceActor;
        private readonly SpatialBlendActor _receiverActor;
        private readonly RecordedAnchorPoseSource _input;
        private readonly AnchorPoseReplayPlayer _replay;
        private readonly AnchorTargetController _targets;
        private readonly SbAvatarEntity _avatarPrefab;
        private readonly int _avatarPresetIndex;
        private readonly string _actorId;
        private readonly string _sourceSpaceId;
        private readonly string _receiverSpaceId;
        private readonly string _committedJsonlPath;
        private readonly string _committedManifestPath;
        private readonly Pose _sourceReference;
        private readonly Pose _receiverReference;
        // This is frozen from the exact matching source/receiver arrays admitted at
        // Start. Do not consult the mutable serialized definition after that point:
        // both live source ingress and committed replay allow the same chart IDs.
        private string[] _knownAnchorIds;
        private long _lifetimeLease;
        private GameObject _sourceOwnerRoot;
        private GameObject _receiverOwnerRoot;
        private GameObject _avatarRoot;
        private SpatialBlendObject _sourceOwner;
        private SpatialBlendObject _receiverOwner;
        private MotionForwardingSink _forwardingSink;
        private RecordedAnchorPoseSource.ReplaySinkLease _inputLease;
        private long _replayConfigurationVersion;
        private readonly List<IDisposable> _supportLeases = new List<IDisposable>();
        private AnchorPoseRecording _recording;
        private double _replayOriginNow;
        private double _lastElapsed;
        private double _lastDeliveryNow = double.NegativeInfinity;
        private bool _replayClockActive;
        private bool _running;
        private bool _stopping;
        private bool _teardownPending;
        private bool _pendingInputRelease;
        private bool _pendingReplayRelease;
        private SpatialBlendObject _explicitSourceRoot, _explicitReceiverRoot;

        private FreeformSceneActorRuntime(FreeformSceneActorDefinition definition,
                                          SpatialBlendLocalWorld world, Transform ownedParent)
        {
            _definition = definition;
            _world = world;
            _ownedParent = ownedParent;
            _sourceActor = definition.SourceActor;
            _receiverActor = definition.ReceiverActor;
            _input = definition.Input;
            _replay = definition.Replay;
            _targets = definition.Targets;
            _avatarPrefab = definition.LocalAvatarPrefab;
            _avatarPresetIndex = definition.LocalAvatarPresetIndex;
            _actorId = definition.ActorId;
            _sourceSpaceId = definition.SourceSpaceId;
            _receiverSpaceId = definition.ReceiverSpaceId;
            _committedJsonlPath = definition.CommittedJsonlPath;
            _committedManifestPath = definition.CommittedManifestPath;
            _sourceReference = definition.SourceReference;
            _receiverReference = definition.ReceiverReference;
        }

        public bool IsRunning => _running;
        public string LastError { get; private set; }
        public string LastConsumerError { get; private set; }
        public RecordedAnchorPoseSource Input => _input;
        public AnchorPoseReplayPlayer Replay => _replay;
        public SpatialBlendActor SourceActor => _sourceActor;
        public SpatialBlendActor ReceiverActor => _receiverActor;
        public AnchorTargetController Targets => _targets;
        public FoundationAvatarRetargetEntity AvatarConsumer { get; private set; }
        public SbAvatarEntity LocalAvatar { get; private set; }
        /// <summary>The actual original SbAvatarPrefab clone, when its optional local consumer is available.</summary>
        public SbAvatarEntity Avatar => LocalAvatar;

        /// <summary>
        /// Starts one actor lifetime under the room's navigable roots, which the caller hands in: the Region of the space's
        /// registered walkable footprint, one per actor space. Anchor supports are never searched
        /// for a root. A root must be a registered Region with footprint geometry and no parent.
        /// </summary>
        public static bool TryStart(FreeformSceneActorDefinition definition,
                                    SpatialBlendLocalWorld world, Transform ownedParent,
                                    SpatialBlendObject sourceRoot, SpatialBlendObject receiverRoot,
                                    out FreeformSceneActorRuntime runtime, out string error)
        {
            runtime = null;
            error = null;
            if (definition == null || world == null || ownedParent == null || !world.IsInitialized ||
                !InSameLoadedScene(definition.gameObject, world.gameObject) ||
                !InSameLoadedScene(definition.gameObject, ownedParent.gameObject) ||
                !definition.ValidateForLoadedScene(definition.gameObject.scene, out error))
            {
                error ??= "A valid initialized local world and owned parent in the actor definition's loaded scene are required.";
                return false;
            }
            if (!ValidateNavigableRoot(sourceRoot, definition.SourceSpaceId, world, "source", out error) ||
                !ValidateNavigableRoot(receiverRoot, definition.ReceiverSpaceId, world, "receiver", out error))
                return false;
            var candidate = new FreeformSceneActorRuntime(definition, world, ownedParent);
            candidate._explicitSourceRoot = sourceRoot;
            candidate._explicitReceiverRoot = receiverRoot;
            try
            {
                if (!candidate.Start(out error))
                {
                    candidate.Stop();
                    return false;
                }
                runtime = candidate;
                return true;
            }
            catch (Exception exception)
            {
                candidate.Stop();
                error = "Freeform actor lifetime startup failed: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Explicitly opens one committed local recording. This performs filesystem
        /// loading only for the caller-supplied JSONL and manifest; it has no network
        /// or download fallback.
        /// </summary>
        public bool TryLoadCommittedReplay(string jsonlPath, string manifestPath, out string error)
        {
            error = null;
            if (!OwnsAuthoredLifetime() || !_running || _stopping || string.IsNullOrWhiteSpace(jsonlPath) ||
                string.IsNullOrWhiteSpace(manifestPath))
                return Fail("A running actor lifetime and explicit JSONL and manifest paths are required.", out error);
            if (!HasCurrentInputLease())
                return Fail("The authored recorded-input source was replaced by another owner.", out error);
            if (Replay.HasConfiguration && !HasCurrentReplayConfiguration())
                return Fail("The authored replay player is configured by another owner.", out error);
            if (Replay.IsRunning)
                return Fail("Stop the current replay before loading another committed recording.", out error);

            // Validate the prospective committed bytes before touching a valid
            // configured replay. A malformed replacement leaves it runnable.
            var recording = new AnchorPoseRecording();
            if (_knownAnchorIds == null || _knownAnchorIds.Length == 0 ||
                !recording.TryLoadCommitted(jsonlPath, manifestPath, _knownAnchorIds, out error))
                return Fail(error, out error);
            if (HasCurrentReplayConfiguration() &&
                !Replay.TryReleaseConfiguration(_replayConfigurationVersion, out error))
                return Fail(error, out error);
            _replayConfigurationVersion = 0;
            if (!Replay.Configure(Input, recording, out error))
                return Fail(error, out error);
            _recording = recording;
            _replayConfigurationVersion = Replay.ConfigurationVersion;
            _replayClockActive = false;
            _lastElapsed = 0d;
            LastError = null;
            return true;
        }

        /// <summary>
        /// Begins the configured recording and captures a local unscaled-time origin.
        /// A caller that wants deterministic control should instead call the overload
        /// with its own finite delivery clock and then <see cref="Tick(double,double,out string)"/>.
        /// </summary>
        public bool TryBeginReplay(out string error)
        {
            return TryBeginReplay(Time.unscaledTimeAsDouble, out error);
        }

        public bool TryBeginReplay(double deliveryNow, out string error)
        {
            error = null;
            if (!OwnsAuthoredLifetime() || !_running || _stopping || _recording == null ||
                !HasCurrentInputLease() || !HasCurrentReplayConfiguration() || !IsFinite(deliveryNow) ||
                deliveryNow < _lastDeliveryNow)
                return Fail("A configured replay and finite delivery clock are required.", out error);
            if (!Replay.Begin(out error))
                return Fail(error, out error);
            _replayOriginNow = deliveryNow;
            _lastElapsed = 0d;
            _lastDeliveryNow = deliveryNow;
            _replayClockActive = true;
            LastError = null;
            return true;
        }

        /// <summary>
        /// Production driver: replay elapsed time and delivery/expiry time derive from
        /// the same unscaled monotonic clock.  The replay component's Update driver is
        /// left disabled, so this is the only replay advancement path.
        /// </summary>
        public bool Tick(out string error)
        {
            double now = Time.unscaledTimeAsDouble;
            if (!OwnsAuthoredLifetime() || !_running || _stopping || !HasCurrentInputLease() ||
                (Replay.HasConfiguration && !HasCurrentReplayConfiguration()))
                return Fail("This actor lifetime is no longer current.", out error);
            if (!_replayClockActive)
            {
                if (!IsFinite(now) || now < _lastDeliveryNow)
                    return Fail("The shared unscaled clock must be finite and monotonic.", out error);
                if (!TryRunFrame(null, now, out error)) return Fail(error, out error);
                _lastDeliveryNow = now;
                error = null;
                return true;
            }
            return Tick(now - _replayOriginNow, now, out error);
        }

        /// <summary>
        /// Deterministic external driver. Both arguments are guarded before replay
        /// advances, preventing a malformed/backward caller clock from partly applying
        /// an otherwise valid committed recording.
        /// </summary>
        public bool Tick(double elapsedSeconds, double deliveryNow, out string error)
        {
            error = null;
            if (!OwnsAuthoredLifetime() || !_running || _stopping || !HasCurrentInputLease() ||
                (Replay.HasConfiguration && !HasCurrentReplayConfiguration()) ||
                !IsFinite(elapsedSeconds) || !IsFinite(deliveryNow) ||
                elapsedSeconds < 0d || elapsedSeconds < _lastElapsed ||
                deliveryNow < _lastDeliveryNow)
                return Fail("Replay elapsed and delivery clocks must be finite and monotonic.", out error);

            if (!TryRunFrame(elapsedSeconds, deliveryNow, out error))
                return Fail(error, out error);
            _lastElapsed = elapsedSeconds;
            _lastDeliveryNow = deliveryNow;
            LastError = null;
            return true;
        }

        public void Stop()
        {
            if (_stopping)
                return;
            // A source callback can call Stop while RecordedAnchorPoseSource is
            // submitting. Its exact lease deliberately refuses release until the
            // callback unwinds. Do not run teardown a second time and overwrite
            // that pending ownership state; this is only an acknowledgement retry.
            if (!_running && _teardownPending)
            {
                TryFinishRetiredStop(out _);
                return;
            }
            _stopping = true;
            try
            {
                bool owns = OwnsAuthoredLifetime();
                // Output and exact input ownership retire before actors.
                if (owns)
                {
                    bool inputCurrent = HasCurrentInputLease();
                    bool replayCurrent = HasCurrentReplayConfiguration();
                    bool exactOwnedPair = inputCurrent && replayCurrent;
                    bool replayReleased = !replayCurrent;
                    // A replay configuration is permitted to reset its source only
                    // while this runtime still owns that exact source lease. An
                    // explicit source replacement must remain completely untouched.
                    if (exactOwnedPair)
                    {
                        TryCleanup(() => Replay.Stop());
                        replayReleased = Replay.TryReleaseConfiguration(_replayConfigurationVersion,
                                                                          out string replayError);
                        if (!replayReleased)
                            LastError = replayError;
                    }
                    TryCleanup(() => AvatarConsumer?.ClearObservations());
                    TryCleanup(() => AvatarConsumer?.Release());
                    TryCleanup(() => { if (SourceActor != null) SourceActor.Unregister(); });
                    TryCleanup(() => { if (ReceiverActor != null) ReceiverActor.Unregister(); });
                    // A native reset can reject during teardown. Retire the actors,
                    // detach only our exact now-retired input lease, then retry the
                    // still-owned replay configuration against the empty source.
                    // This never resets a replacement sink/configuration.
                    bool inputReleased = !inputCurrent;
                    if (inputCurrent && HasCurrentInputLease())
                    {
                        inputReleased = _inputLease.TryRelease(out string inputError);
                        if (!inputReleased)
                            LastError = inputError;
                    }
                    if (!replayReleased && inputReleased && exactOwnedPair &&
                        HasCurrentReplayConfiguration())
                    {
                        replayReleased = Replay.TryReleaseConfiguration(_replayConfigurationVersion,
                                                                          out string retryError);
                        if (!replayReleased)
                            LastError = retryError;
                    }
                    // Direct recorded input does not require a replay
                    // configuration. Its submitted-frame callback can still reject
                    // release, so preserve this exact source lease independently
                    // of whether this lifetime happened to configure a player.
                    _pendingInputRelease = inputCurrent && !inputReleased &&
                        HasCurrentInputLease();
                    _pendingReplayRelease = exactOwnedPair && !replayReleased &&
                        HasCurrentReplayConfiguration() &&
                        (inputReleased || HasCurrentInputLease());
                    _teardownPending = _pendingInputRelease || _pendingReplayRelease;
                    if (replayReleased)
                        _replayConfigurationVersion = 0;
                }
                foreach (IDisposable lease in _supportLeases)
                    TryCleanup(() => lease?.Dispose());
                _supportLeases.Clear();
                AvatarConsumer = null;
                TryCleanup(() => { if (_sourceOwner != null) _sourceOwner.Unregister(); });
                TryCleanup(() => { if (_receiverOwner != null) _receiverOwner.Unregister(); });
                DestroyOwned(_avatarRoot);
                DestroyOwned(_sourceOwnerRoot);
                DestroyOwned(_receiverOwnerRoot);
                _avatarRoot = null;
                _sourceOwnerRoot = null;
                _receiverOwnerRoot = null;
                _sourceOwner = null;
                _receiverOwner = null;
                _forwardingSink = null;
                LocalAvatar = null;
                _recording = null;
                _replayClockActive = false;
                _running = false;
                if (owns && !_teardownPending)
                    _definition.ReleaseRuntimeLease(this, _lifetimeLease);
            }
            finally
            {
                _stopping = false;
            }
        }

        /// <summary>
        /// Admission-only completion for a Stop that was invoked from an input
        /// callback. No scheduler or reflection is used: a subsequent ordinary
        /// local start performs this exact-lease cleanup once the callback returns.
        /// It never resets a source/configuration that another owner replaced.
        /// </summary>
        internal bool TryFinishRetiredStop(out string error)
        {
            error = null;
            if (_stopping)
            {
                error = "The retired actor lifetime is still inside its stop callback.";
                return false;
            }
            if (_running)
            {
                error = "The authored actor chart is still running.";
                return false;
            }
            if (!OwnsAuthoredLifetime())
                return true;
            if (!_teardownPending)
            {
                _definition.ReleaseRuntimeLease(this, _lifetimeLease);
                return true;
            }

            if (_pendingInputRelease)
            {
                if (!HasCurrentInputLease())
                {
                    // A foreign replacement won the source after retirement. It is
                    // not this runtime's cleanup target, so do not reset its replay.
                    _pendingInputRelease = false;
                    _pendingReplayRelease = false;
                }
                else if (!_inputLease.TryRelease(out error))
                {
                    return false;
                }
                else
                {
                    _pendingInputRelease = false;
                }
            }

            if (_pendingReplayRelease)
            {
                if (!HasCurrentReplayConfiguration())
                {
                    _pendingReplayRelease = false;
                }
                else if (!_pendingInputRelease &&
                         !Replay.TryReleaseConfiguration(_replayConfigurationVersion, out error))
                {
                    return false;
                }
                else
                {
                    _pendingReplayRelease = false;
                    _replayConfigurationVersion = 0;
                }
            }

            _teardownPending = _pendingInputRelease || _pendingReplayRelease;
            if (_teardownPending)
            {
                error = "Retired actor teardown is awaiting its exact input or replay acknowledgement.";
                return false;
            }
            _definition.ReleaseRuntimeLease(this, _lifetimeLease);
            return true;
        }

        private bool Start(out string error)
        {
            error = null;
            if (!_definition.TryAcquireRuntimeLease(this, out _lifetimeLease, out error))
                return false;
            if (Replay.HasConfiguration || Replay.IsRunning || Replay.AutoAdvanceOnUpdate)
            {
                error = "The authored replay player must be unconfigured and have automatic Update driving disabled.";
                return false;
            }
            if (!_definition.TryCreateSourceAnchors(out SpatialBlendActorAnchor[] sourceAnchors, out error) ||
                !_definition.TryCreateReceiverAnchors(out SpatialBlendActorAnchor[] receiverAnchors, out error))
                return false;
            // ABI 4: relation scope belongs to the relation family and starts at the actor's element.
            // The actor's element is its own person leaf, never a region,
            // and it does not move; scope descent starts at the tree's navigable root.
            // The roots were handed in (the room's registered walkable footprint); no anchor support is searched.
            SpatialBlendObject sourceRoot = _explicitSourceRoot, receiverRoot = _explicitReceiverRoot;
            _sourceOwner = CreateOwner("Freeform Scene Local Source Owner", _sourceSpaceId,
                                       _actorId + "-source-owner", _sourceReference, sourceRoot, out _sourceOwnerRoot, out error);
            if (_sourceOwner == null)
                return false;
            _receiverOwner = CreateOwner("Freeform Scene Local Receiver Owner", _receiverSpaceId,
                                         _actorId + "-receiver-owner", _receiverReference, receiverRoot, out _receiverOwnerRoot, out error);
            if (_receiverOwner == null)
                return false;
            if (!TryFreezeKnownAnchorIds(sourceAnchors, receiverAnchors, out _knownAnchorIds, out error)) return false;
            if (!TryRegisterExplicitSupportPairs(sourceAnchors, receiverAnchors, out error))
                return false;

            SourceActor.Configure(_sourceOwner, _actorId, true, sourceAnchors);
            ReceiverActor.Configure(_receiverOwner, _actorId, false, receiverAnchors, Targets);
            if (!SourceActor.TryRegister(out error) || !ReceiverActor.TryRegister(out error) ||
                !SourceActor.TryCreateMotionInput(out IAnchorPoseFrameSink nativeSink))
                return false;
            _forwardingSink = new MotionForwardingSink(nativeSink, this);
            if (!Input.TryAcquireReplaySink(_forwardingSink, _knownAnchorIds, out _inputLease,
                                            out error))
                return false;

            _running = true;
            // Avatar rendering is downstream of native source admission. A platform
            // SDK/retarget failure is exposed separately and cannot roll native input back.
            TryCreateLocalAvatarConsumer();
            bool specifiedReplay = !string.IsNullOrWhiteSpace(_committedJsonlPath);
            if (specifiedReplay &&
                (!TryLoadCommittedReplay(_committedJsonlPath, _committedManifestPath, out error) ||
                 !TryBeginReplay(out error)))
                return false;
            LastError = null;
            return true;
        }

        // The person leaf hangs under the navigable root of the space its anchors are scoped to, so that every scope descent
        // (which starts at that root) reaches the authored objects.
        private static bool ValidateNavigableRoot(SpatialBlendObject root, string spaceId, SpatialBlendLocalWorld world,
                                                  string side, out string error)
        {
            error = null;
            if (root == null)
                error = $"The {side} actor has no navigable root: the room's walkable footprint is not registered.";
            else if (root.Kind != SpatialBlendObjectKind.Region || root.ParentObject != null)
                error = $"The {side} navigable root '{root.ObjectId}' must be a top-level Region.";
            else if (!root.HasFootprintGeometry)
                error = $"The {side} navigable root '{root.ObjectId}' is a Region with no footprint geometry: actors cannot be placed under it.";
            else if (!root.IsRegistered || root.World != world || root.SpaceId != spaceId)
                error = $"The {side} navigable root '{root.ObjectId}' must be registered in space '{spaceId}' of this world.";
            return error == null;
        }

        private SpatialBlendObject CreateOwner(string name, string spaceId, string objectId, Pose reference,
                                            SpatialBlendObject navigableRoot, out GameObject root, out string error)
        {
            error = null;
            root = new GameObject(name);
            root.transform.SetParent(_ownedParent, false);
            root.transform.SetPositionAndRotation(reference.position, reference.rotation);
            var owner = root.AddComponent<SpatialBlendObject>();
            if (!owner.TryConfigure(_world, spaceId, objectId, navigableRoot, SpatialBlendObjectKind.Object, out error) ||
                !owner.TrySetColliderGeometryEnabled(false, out error) || !owner.TryRegister(out error))
            {
                DestroyOwned(root);
                root = null;
                return null;
            }
            return owner;
        }

        private bool TryRegisterExplicitSupportPairs(SpatialBlendActorAnchor[] sourceAnchors,
                                                     SpatialBlendActorAnchor[] receiverAnchors,
                                                     out string error)
        {
            error = null;
            if (sourceAnchors == null || receiverAnchors == null ||
                sourceAnchors.Length != receiverAnchors.Length)
            {
                error = "Source and receiver charts must have matching explicit support pairs.";
                return false;
            }
            var registeredPairs = new HashSet<(SpatialBlendObject, SpatialBlendObject)>();
            for (int index = 0; index < sourceAnchors.Length; ++index)
            {
                SpatialBlendObject source = sourceAnchors[index].Support;
                SpatialBlendObject receiver = receiverAnchors[index].Support;
                if (source == null && receiver == null)
                    continue;
                if (source == null || receiver == null || source.World != _world ||
                    receiver.World != _world || !source.IsRegistered || !receiver.IsRegistered ||
                    source.SpaceId != _sourceSpaceId || receiver.SpaceId != _receiverSpaceId ||
                    source.Kind != SpatialBlendObjectKind.Affordance ||
                    receiver.Kind != SpatialBlendObjectKind.Affordance)
                {
                    error = "Explicit actor support pairs require live authored Affordances in this world's declared source and receiver spaces.";
                    return false;
                }
                if (!registeredPairs.Add((source, receiver)))
                    continue;
                if (!_world.TryRegisterSupportCorrespondence(source, receiver,
                                                             out IDisposable lease, out error))
                    return false;
                _supportLeases.Add(lease);
            }
            return true;
        }

        private static bool TryFreezeKnownAnchorIds(SpatialBlendActorAnchor[] sourceAnchors,
                                                    SpatialBlendActorAnchor[] receiverAnchors,
                                                    out string[] anchorIds, out string error)
        {
            anchorIds = null;
            error = null;
            if (sourceAnchors == null || receiverAnchors == null ||
                sourceAnchors.Length != receiverAnchors.Length ||
                (sourceAnchors.Length != 4 && sourceAnchors.Length != 5))
            {
                error = "Source and receiver charts must have matching four-anchor charts or matching optional seat-contact charts.";
                return false;
            }

            string[] expected = sourceAnchors.Length == 5
                ? new[]
                {
                    FreeformSceneActorDefinition.RootAnchorId,
                    FreeformSceneActorDefinition.HeadAnchorId,
                    FreeformSceneActorDefinition.LeftHandAnchorId,
                    FreeformSceneActorDefinition.RightHandAnchorId,
                    FreeformSceneActorDefinition.SeatContactAnchorId
                }
                : new[]
                {
                    FreeformSceneActorDefinition.RootAnchorId,
                    FreeformSceneActorDefinition.HeadAnchorId,
                    FreeformSceneActorDefinition.LeftHandAnchorId,
                    FreeformSceneActorDefinition.RightHandAnchorId
                };
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var frozen = new string[expected.Length];
            for (int index = 0; index < expected.Length; ++index)
            {
                SpatialBlendActorAnchor source = sourceAnchors[index];
                SpatialBlendActorAnchor receiver = receiverAnchors[index];
                if (source == null || receiver == null ||
                    !AnchorPoseFrameValidation.IsValidAnchorId(source.Name) ||
                    !string.Equals(source.Name, receiver.Name, StringComparison.Ordinal) ||
                    !string.Equals(source.Name, expected[index], StringComparison.Ordinal) ||
                    !unique.Add(source.Name))
                {
                    error = "Source and receiver charts must retain matching ordered original Freeform anchor IDs.";
                    return false;
                }
                frozen[index] = source.Name;
            }
            anchorIds = frozen;
            return true;
        }

        private void TryCreateLocalAvatarConsumer()
        {
            try
            {
                _avatarRoot = new GameObject("Freeform Scene Local Avatar");
                _avatarRoot.transform.SetParent(_ownedParent, false);
                if (!SbLocalPresetAvatarFactory.TryCreate(_avatarPrefab,
                                                            _avatarRoot.transform,
                                                            _avatarPresetIndex,
                                                            out SbAvatarEntity avatar,
                                                            out SbRetargetTransform retarget,
                                                            out _, out string error))
                {
                    LastConsumerError = error;
                    DestroyOwned(_avatarRoot);
                    _avatarRoot = null;
                    return;
                }
                LocalAvatar = avatar;
                AvatarConsumer = avatar.gameObject.AddComponent<FoundationAvatarRetargetEntity>();
                if (!AvatarConsumer.TryConfigure(ReceiverActor, Targets, retarget,
                                                 _sourceReference,
                                                 _receiverReference,
                                                 FreeformSceneActorDefinition.RootAnchorId,
                                                 _knownAnchorIds,
                                                 out error))
                {
                    LastConsumerError = error;
                    DestroyOwned(_avatarRoot);
                    _avatarRoot = null;
                    LocalAvatar = null;
                    AvatarConsumer = null;
                    return;
                }
                LastConsumerError = null;
            }
            catch (Exception exception)
            {
                LastConsumerError = "Local avatar consumer setup failed: " + exception.Message;
                DestroyOwned(_avatarRoot);
                _avatarRoot = null;
                LocalAvatar = null;
                AvatarConsumer = null;
            }
        }

        private bool TryApplySourceFrame(MotionForwardingSink sink, IAnchorPoseFrameSink native, AnchorPoseFrame frame)
        {
            if (!CanUseForwardingSink(sink) || native == null) return false;
            if (!native.TryApplyWorldFrame(frame)) return false;
            TryForwardCommittedFrame(sink, frame);
            return true;
        }

        private bool TryRunFrame(double? elapsed, double deliveryNow, out string error)
        {
            error = null;
            if (elapsed.HasValue && Replay.IsRunning && !Replay.TryAdvanceTo(elapsed.Value, out error)) return false;
            _world.DrainRealizations(deliveryNow);
            Targets.Tick(deliveryNow);
            return true;
        }

        private void TryForwardCommittedFrame(MotionForwardingSink sink, AnchorPoseFrame frame)
        {
            if (!CanUseForwardingSink(sink) || AvatarConsumer == null)
                return;
            try
            {
                if (!AvatarConsumer.TryApplyObservationFrame(frame, out string error))
                    LastConsumerError = error;
                else
                    LastConsumerError = null;
            }
            catch (Exception exception)
            {
                LastConsumerError = "Local avatar consumer rejected a committed frame: " + exception.Message;
            }
        }

        private void ClearConsumerObservations()
        {
            if (!OwnsAuthoredLifetime())
                return;
            try { AvatarConsumer?.ClearObservations(); }
            catch (Exception exception) { LastConsumerError = exception.Message; }
        }

        private bool Fail(string message, out string error)
        {
            LastError = string.IsNullOrWhiteSpace(message) ? "Freeform actor lifetime failed." : message;
            error = LastError;
            return false;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private bool OwnsAuthoredLifetime()
        {
            return _definition != null && _definition.IsCurrentRuntime(this, _lifetimeLease);
        }

        private bool CanUseForwardingSink(MotionForwardingSink sink)
        {
            return _running && !_stopping && OwnsAuthoredLifetime() &&
                   ReferenceEquals(_forwardingSink, sink) && HasCurrentInputLease();
        }

        private bool HasCurrentInputLease() => _inputLease != null && _inputLease.IsCurrent;

        private bool HasCurrentReplayConfiguration()
        {
            return _replayConfigurationVersion != 0 && Replay != null && Replay.HasConfiguration &&
                   Replay.ConfigurationVersion == _replayConfigurationVersion;
        }

        private static void TryCleanup(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private static void DestroyOwned(GameObject value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
            {
                value.SetActive(false);
                value.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(value);
            }
            else
                UnityEngine.Object.DestroyImmediate(value);
        }

        private static bool InSameLoadedScene(GameObject first, GameObject second)
        {
            return first != null && second != null && first.scene.IsValid() &&
                   first.scene.isLoaded && first.scene == second.scene;
        }
    }
}
