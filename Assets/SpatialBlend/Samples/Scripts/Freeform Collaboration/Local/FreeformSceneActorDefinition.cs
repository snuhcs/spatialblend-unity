// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// One explicit anchor chart in the original Freeform scene.  A scope is optional
    /// and, when supplied, is an exact authored mapping: this definition never treats
    /// a chair, sofa, or arbitrary collider as an interchangeable support.
    /// </summary>
    [Serializable]
    public sealed class FreeformSceneActorAnchorReference
    {
        [SerializeField] private string _anchorId;
        [SerializeField] private Transform _reference;
        [UnityEngine.Serialization.FormerlySerializedAs("_scope")] [SerializeField] private SpatialBlendObject _support;

        public string AnchorId => _anchorId;
        public Transform Reference => _reference;
        public SpatialBlendObject Support => _support;
        internal bool IsBlank => string.IsNullOrEmpty(_anchorId) && _reference == null && _support == null;

        internal bool TryCreate(Transform root, SpatialBlendActorAnchorRole role,
                                string parent, out SpatialBlendActorAnchor anchor,
                                out string error,
                                SpatialBlendActorAnchorPositionDimension positionDimension =
                                    SpatialBlendActorAnchorPositionDimension.RoleDefault)
        {
            anchor = null;
            error = null;
            if (root == null || _reference == null ||
                !AnchorPoseFrameValidation.IsValidAnchorId(_anchorId))
            {
                error = "Actor anchors require an explicit valid ID and reference transform.";
                return false;
            }
            if (_reference != root && !_reference.IsChildOf(root))
            {
                error = "Actor anchor references must remain on their explicit authored root.";
                return false;
            }
            if (role == SpatialBlendActorAnchorRole.Root &&
                (!ReferenceEquals(_reference, root) || !string.IsNullOrEmpty(parent)))
            {
                error = "The motion root must be the explicit root reference with no parent.";
                return false;
            }
            // Actor offsets are rigid chart distances. Do not apply inverse scale:
            // an authored chart may have scale for Unity layout but native anchors
            // require actual world displacement expressed in the chart rotation.
            Vector3 offset = role == SpatialBlendActorAnchorRole.Root ? Vector3.zero :
                Quaternion.Inverse(root.rotation) * (_reference.position - root.position);
            anchor = new SpatialBlendActorAnchor(_anchorId, role, offset, parent, _support,
                                               positionDimension);
            return true;
        }
    }

    /// <summary>
    /// Serialized source/receiver chart for the original Freeform scene.  These
    /// transforms are authored placement references only; blank support scopes in
    /// the default scene intentionally do not claim an MRUK/support mapping.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneActorDefinition : MonoBehaviour
    {
        public const string RootAnchorId = "root";
        public const string HeadAnchorId = "head";
        public const string LeftHandAnchorId = "hand_l";
        public const string RightHandAnchorId = "hand_r";
        public const string SeatContactAnchorId = "seat_contact";

        [Header("Explicit source chart")]
        [SerializeField] private Transform _sourceRoot;
        [SerializeField] private SpatialBlendActor _sourceActor;
        [SerializeField] private RecordedAnchorPoseSource _input;
        [SerializeField] private AnchorPoseReplayPlayer _replay;
        [SerializeField] private FreeformSceneActorAnchorReference _sourceRootAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _sourceHeadAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _sourceLeftHandAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _sourceRightHandAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _sourceSeatContactAnchor;

        [Header("Explicit receiver chart")]
        [SerializeField] private Transform _receiverRoot;
        [SerializeField] private SpatialBlendActor _receiverActor;
        [SerializeField] private AnchorTargetController _targets;
        [SerializeField] private FreeformSceneActorAnchorReference _receiverRootAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _receiverHeadAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _receiverLeftHandAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _receiverRightHandAnchor;
        [SerializeField] private FreeformSceneActorAnchorReference _receiverSeatContactAnchor;

        [Header("Local actor identity")]
        [SerializeField] private string _actorId = "freeform-local-actor";
        [SerializeField] private string _sourceSpaceId = "freeform-scene-local-source";
        [SerializeField] private string _receiverSpaceId = "freeform-scene-local-receiver";

        [Header("Original local avatar prefab")]
        [SerializeField] private SbAvatarEntity _localAvatarPrefab;
        [SerializeField, Min(0)] private int _localAvatarPresetIndex;

        [Header("Explicit optional committed replay")]
        [SerializeField] private string _committedJsonlPath;
        [SerializeField] private string _committedManifestPath;
        private FreeformSceneActorRuntime _activeRuntime;
        private long _nextRuntimeLease;

        public Transform SourceRoot => _sourceRoot;
        public Transform ReceiverRoot => _receiverRoot;
        public SpatialBlendActor SourceActor => _sourceActor;
        public SpatialBlendActor ReceiverActor => _receiverActor;
        public RecordedAnchorPoseSource Input => _input;
        public AnchorPoseReplayPlayer Replay => _replay;
        public AnchorTargetController Targets => _targets;
        public string ActorId => _actorId;
        public string SourceSpaceId => _sourceSpaceId;
        public string ReceiverSpaceId => _receiverSpaceId;
        public SbAvatarEntity LocalAvatarPrefab => _localAvatarPrefab;
        public int LocalAvatarPresetIndex => _localAvatarPresetIndex;
        public string CommittedJsonlPath => _committedJsonlPath;
        public string CommittedManifestPath => _committedManifestPath;
        public Pose SourceReference => new Pose(_sourceRoot.position, _sourceRoot.rotation);
        public Pose ReceiverReference => new Pose(_receiverRoot.position, _receiverRoot.rotation);

        public IReadOnlyList<string> AnchorIds
        {
            get
            {
                if (TryCreateSourceAnchors(out SpatialBlendActorAnchor[] source, out _) &&
                    TryCreateReceiverAnchors(out SpatialBlendActorAnchor[] receiver, out _) &&
                    source.Length == 5 && receiver.Length == 5)
                {
                    return new[]
                    {
                        RootAnchorId, HeadAnchorId, LeftHandAnchorId, RightHandAnchorId,
                        SeatContactAnchorId
                    };
                }
                return new[] { RootAnchorId, HeadAnchorId, LeftHandAnchorId, RightHandAnchorId };
            }
        }

        internal bool ValidateForLoadedScene(UnityEngine.SceneManagement.Scene scene, out string error)
        {
            error = null;
            if (!InScene(this, scene) || !InScene(_sourceRoot, scene) ||
                !InScene(_receiverRoot, scene) || !InScene(_sourceActor, scene) ||
                !InScene(_receiverActor, scene) || !InScene(_input, scene) ||
                !InScene(_replay, scene) || !InScene(_targets, scene))
            {
                error = "Actor definitions require original loaded-scene components and transforms.";
                return false;
            }
            if (ReferenceEquals(_sourceRoot, _receiverRoot) ||
                ReferenceEquals(_sourceActor, _receiverActor) ||
                _sourceActor.gameObject != _sourceRoot.gameObject ||
                _receiverActor.gameObject != _receiverRoot.gameObject ||
                _input.gameObject != _sourceRoot.gameObject ||
                _replay.gameObject != _sourceRoot.gameObject ||
                _targets.gameObject != _receiverRoot.gameObject)
            {
                error = "Source and receiver actors, input, replay, and targets must be authored on their declared hosts.";
                return false;
            }
            if (_sourceActor.IsRegistered || _receiverActor.IsRegistered ||
                string.IsNullOrWhiteSpace(_actorId) || _actorId.IndexOf('\0') >= 0 ||
                string.IsNullOrWhiteSpace(_sourceSpaceId) ||
                string.IsNullOrWhiteSpace(_receiverSpaceId) ||
                string.Equals(_sourceSpaceId, _receiverSpaceId, StringComparison.Ordinal))
            {
                error = "Actors need one valid ID, must be idle, and must use distinct source and receiver spaces.";
                return false;
            }
            if (_localAvatarPrefab == null || _localAvatarPrefab.gameObject.scene.IsValid())
            {
                error = "Foundation Local requires an explicit original SbAvatarPrefab asset, not a scene avatar.";
                return false;
            }
            bool jsonlSpecified = !string.IsNullOrWhiteSpace(_committedJsonlPath);
            bool manifestSpecified = !string.IsNullOrWhiteSpace(_committedManifestPath);
            if (jsonlSpecified != manifestSpecified)
            {
                error = "Committed replay requires both explicit JSONL and manifest paths.";
                return false;
            }
            if (!TryGetSeatContactPair(out bool seatContactConfigured, out error))
                return false;
            return ValidateChart(_sourceRoot, _sourceRootAnchor, _sourceHeadAnchor,
                                 _sourceLeftHandAnchor, _sourceRightHandAnchor,
                                 _sourceSeatContactAnchor, seatContactConfigured, scene,
                                 out error) &&
                   ValidateChart(_receiverRoot, _receiverRootAnchor, _receiverHeadAnchor,
                                 _receiverLeftHandAnchor, _receiverRightHandAnchor,
                                 _receiverSeatContactAnchor, seatContactConfigured, scene,
                                 out error) && ValidateScopePairs(seatContactConfigured, out error);
        }

        internal bool TryAcquireRuntimeLease(FreeformSceneActorRuntime runtime, out long lease,
                                             out string error)
        {
            lease = 0;
            error = null;
            if (runtime == null)
            {
                error = "A local actor lifetime owner is required.";
                return false;
            }
            if (_activeRuntime != null && !_activeRuntime.TryFinishRetiredStop(out error))
                return false;
            if (_activeRuntime != null)
            {
                error = "The authored Freeform actor chart already has a local lifetime owner.";
                return false;
            }
            _activeRuntime = runtime;
            lease = ++_nextRuntimeLease;
            return true;
        }

        internal bool IsCurrentRuntime(FreeformSceneActorRuntime runtime, long lease)
        {
            return runtime != null && ReferenceEquals(_activeRuntime, runtime) && lease != 0 &&
                   lease == _nextRuntimeLease;
        }

        internal void ReleaseRuntimeLease(FreeformSceneActorRuntime runtime, long lease)
        {
            if (IsCurrentRuntime(runtime, lease))
                _activeRuntime = null;
        }

        internal bool TryCreateSourceAnchors(out SpatialBlendActorAnchor[] anchors, out string error)
        {
            if (!TryGetSeatContactPair(out bool seatContactConfigured, out error))
            {
                anchors = null;
                return false;
            }
            return TryCreateAnchors(_sourceRoot, _sourceRootAnchor, _sourceHeadAnchor,
                                    _sourceLeftHandAnchor, _sourceRightHandAnchor,
                                    _sourceSeatContactAnchor, seatContactConfigured,
                                    out anchors, out error);
        }

        internal bool TryCreateReceiverAnchors(out SpatialBlendActorAnchor[] anchors, out string error)
        {
            if (!TryGetSeatContactPair(out bool seatContactConfigured, out error))
            {
                anchors = null;
                return false;
            }
            return TryCreateAnchors(_receiverRoot, _receiverRootAnchor, _receiverHeadAnchor,
                                    _receiverLeftHandAnchor, _receiverRightHandAnchor,
                                    _receiverSeatContactAnchor, seatContactConfigured,
                                    out anchors, out error);
        }

        private static bool TryCreateAnchors(Transform root,
                                             FreeformSceneActorAnchorReference rootAnchor,
                                             FreeformSceneActorAnchorReference head,
                                             FreeformSceneActorAnchorReference left,
                                             FreeformSceneActorAnchorReference right,
                                             FreeformSceneActorAnchorReference seatContact,
                                             bool seatContactConfigured,
                                             out SpatialBlendActorAnchor[] anchors, out string error)
        {
            anchors = null;
            error = null;
            if (rootAnchor == null || head == null || left == null || right == null ||
                !rootAnchor.TryCreate(root, SpatialBlendActorAnchorRole.Root, null,
                                      out SpatialBlendActorAnchor rootValue, out error) ||
                !head.TryCreate(root, SpatialBlendActorAnchorRole.Body, RootAnchorId,
                                out SpatialBlendActorAnchor headValue, out error) ||
                !left.TryCreate(root, SpatialBlendActorAnchorRole.Body, RootAnchorId,
                                out SpatialBlendActorAnchor leftValue, out error) ||
                !right.TryCreate(root, SpatialBlendActorAnchorRole.Body, RootAnchorId,
                                 out SpatialBlendActorAnchor rightValue, out error))
            {
                error ??= "Actor chart is incomplete.";
                return false;
            }
            if (rootValue.Name != RootAnchorId || headValue.Name != HeadAnchorId ||
                leftValue.Name != LeftHandAnchorId || rightValue.Name != RightHandAnchorId)
            {
                error = "The original Freeform chart requires root, head, hand_l, and hand_r anchors.";
                return false;
            }
            if (!seatContactConfigured)
            {
                anchors = new[] { rootValue, headValue, leftValue, rightValue };
                return true;
            }
            if (seatContact == null ||
                !seatContact.TryCreate(root, SpatialBlendActorAnchorRole.Body, RootAnchorId,
                                       out SpatialBlendActorAnchor seatValue, out error,
                                       SpatialBlendActorAnchorPositionDimension.Spatial3D) ||
                seatValue.Name != SeatContactAnchorId || seatValue.Support == null ||
                seatValue.Support.Kind != SpatialBlendObjectKind.Affordance ||
                !IsSeatContactInRootScene(root, seatContact) ||
                !seatValue.TryResolvePositionDimension(out int dimension) || dimension != 3)
            {
                error ??= "Seat contact requires an explicit spatial Body anchor and Affordance scope.";
                return false;
            }
            anchors = new[] { rootValue, headValue, leftValue, rightValue, seatValue };
            return true;
        }

        private static bool IsSeatContactInRootScene(
            Transform root, FreeformSceneActorAnchorReference seatContact)
        {
            return root != null && seatContact != null && seatContact.Reference != null &&
                   seatContact.Support != null && root.gameObject.scene.IsValid() &&
                   root.gameObject.scene.isLoaded &&
                   seatContact.Reference.gameObject.scene == root.gameObject.scene &&
                   seatContact.Support.gameObject.scene == root.gameObject.scene;
        }

        private static bool ValidateChart(Transform root,
                                          FreeformSceneActorAnchorReference rootAnchor,
                                          FreeformSceneActorAnchorReference head,
                                          FreeformSceneActorAnchorReference left,
                                          FreeformSceneActorAnchorReference right,
                                          FreeformSceneActorAnchorReference seatContact,
                                          bool seatContactConfigured,
                                          UnityEngine.SceneManagement.Scene scene, out string error)
        {
            error = null;
            if (!TryCreateAnchors(root, rootAnchor, head, left, right, seatContact,
                                  seatContactConfigured, out SpatialBlendActorAnchor[] anchors,
                                  out error))
                return false;
            foreach (SpatialBlendActorAnchor anchor in anchors)
            {
                SpatialBlendObject scope = anchor.Support;
                if (scope != null && !InScene(scope, scene))
                {
                    error = "An explicit actor support scope must be an object in this loaded scene.";
                    return false;
                }
            }
            return true;
        }

        private bool TryGetSeatContactPair(out bool configured, out string error)
        {
            configured = false;
            error = null;
            bool sourceBlank = _sourceSeatContactAnchor == null || _sourceSeatContactAnchor.IsBlank;
            bool receiverBlank = _receiverSeatContactAnchor == null || _receiverSeatContactAnchor.IsBlank;
            if (sourceBlank && receiverBlank)
                return true;
            if (sourceBlank || receiverBlank)
            {
                error = "Seat contact requires matching source and receiver anchor references.";
                return false;
            }
            if (!IsSeatContactReferenceConfigured(_sourceRoot, _sourceSeatContactAnchor) ||
                !IsSeatContactReferenceConfigured(_receiverRoot, _receiverSeatContactAnchor))
            {
                error = "Seat contact requires matching authored spatial Body anchors and Affordance scopes.";
                return false;
            }
            configured = true;
            return true;
        }

        private static bool IsSeatContactReferenceConfigured(
            Transform root, FreeformSceneActorAnchorReference seatContact)
        {
            return root != null && seatContact != null &&
                   seatContact.AnchorId == SeatContactAnchorId &&
                   seatContact.Reference != null &&
                   (seatContact.Reference == root || seatContact.Reference.IsChildOf(root)) &&
                   seatContact.Support != null &&
                   seatContact.Support.Kind == SpatialBlendObjectKind.Affordance &&
                   IsSeatContactInRootScene(root, seatContact);
        }

        private bool ValidateScopePairs(bool seatContactConfigured, out string error)
        {
            error = null;
            FreeformSceneActorAnchorReference[] source =
            {
                _sourceRootAnchor, _sourceHeadAnchor, _sourceLeftHandAnchor, _sourceRightHandAnchor
            };
            FreeformSceneActorAnchorReference[] receiver =
            {
                _receiverRootAnchor, _receiverHeadAnchor, _receiverLeftHandAnchor, _receiverRightHandAnchor
            };
            for (int index = 0; index < source.Length; ++index)
            {
                if ((source[index].Support == null) != (receiver[index].Support == null))
                {
                    error = "An explicit support map requires source and receiver scopes for the same anchor.";
                    return false;
                }
            }
            if (seatContactConfigured &&
                (_sourceSeatContactAnchor.Support == null || _receiverSeatContactAnchor.Support == null ||
                 _sourceSeatContactAnchor.Support.Kind != SpatialBlendObjectKind.Affordance ||
                 _receiverSeatContactAnchor.Support.Kind != SpatialBlendObjectKind.Affordance))
            {
                error = "Seat contact requires explicit source and receiver Affordance scopes.";
                return false;
            }
            return true;
        }

        private static bool InScene(Component value, UnityEngine.SceneManagement.Scene scene)
        {
            return value != null && value.gameObject.scene.IsValid() &&
                   value.gameObject.scene.isLoaded && value.gameObject.scene == scene;
        }

        private static bool InScene(Transform value, UnityEngine.SceneManagement.Scene scene)
        {
            return value != null && value.gameObject.scene.IsValid() &&
                   value.gameObject.scene.isLoaded && value.gameObject.scene == scene;
        }
    }
}
