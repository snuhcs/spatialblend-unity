// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Fusion;
using Meta.XR.MRUtilityKit;
using SpatialBlend.Freeform;
using UnityEngine;
using UnityBehaviour = UnityEngine.Behaviour;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// Local-only content lifetime for the original Freeform scene. It binds the
    /// authored Whiteboard and Chess instances to Foundation content; it never
    /// creates substitute primitives or initiates a transport.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneLocalBackend : MonoBehaviour
    {
        public const string WhiteboardContentId = "freeform-scene-whiteboard";
        public const string ChessContentId = "freeform-scene-chess";

        [SerializeField] private FreeformSceneWhiteboardDefinition _whiteboard;
        [SerializeField] private FreeformSceneChessDefinition _chess;
        [SerializeField] private FreeformSceneActorDefinition _actors;
        [SerializeField] private FreeformSceneRoomGeometryDefinition _roomGeometry;
        [SerializeField] private FreeformSceneFoundationLocalGate _startupGate;
        [SerializeField] private string _spaceId = "freeform-scene-local";

        /// <summary>The original, non-networked avatar prefab the local actors use (also the lobby preview's), or null.</summary>
        public SbAvatarEntity LocalAvatarPrefab => _actors != null ? _actors.LocalAvatarPrefab : null;

        private readonly Dictionary<UnityBehaviour, bool> _suppressedBehaviours =
            new Dictionary<UnityBehaviour, bool>();
        private readonly List<IDisposable> _paletteLeases = new List<IDisposable>();
        private SpatialBlendLocalWorld _localWorld;
        private GameObject _worldRoot;
        private FreeformContentCoordinator _contentCoordinator;
        private SpatialBlendObject _whiteboardObject;
        private GameObject _whiteboardSupportRoot;
        private SpatialBlendObject _whiteboardSupport;
        private SpatialBlendObject _chessObject;
        private WhiteboardContentView _whiteboardView;
        private ChessContentView _chessView;
        private FreeformSceneWhiteboardInput _whiteboardInput;
        private FreeformSceneChessInput _chessInput;
        private readonly List<FreeformSceneActorRuntime> _actorRuntimes =
            new List<FreeformSceneActorRuntime>();
        private FreeformSceneRoomGeometryRuntime _roomGeometryRuntime;
        private bool _running;
        private bool _stopping;
        private bool _attachmentsCreated;
        private bool _actorsPending;
        private string _reportedActorWait;
        private WalkableFloorSource _floorSource;

#if UNITY_EDITOR
        // Fixture-only lifecycle bridge: EditMode AddComponent does not invoke
        // Awake, while the production path relies on Unity's normal lifecycle.
        private static Action<SpatialBlendLocalWorld> TestOnlyAfterLocalWorldCreated;
        // Fixture-only measured floor: EditMode has no MRUK room and no runtime-built NavMesh, so a test supplies the
        // measurement the device would provide. Consulted before the app's source.
        private static WalkableFloorSource TestOnlyFloorSource;
#endif

        public bool IsRunning => _running;
        public string LastError { get; private set; }
        public SpatialBlendLocalWorld LocalWorld => _localWorld;
        public FreeformContentCoordinator ContentCoordinator => _contentCoordinator;
        /// <summary>Receiver-only native surface support for the authored whiteboard.</summary>
        public SpatialBlendObject WhiteboardSupport => _whiteboardSupport;
        public FreeformSceneActorRuntime ActorRuntime => _actorRuntimes.Count == 0
            ? null : _actorRuntimes[0];
        /// <summary>Read-only diagnostics for the explicitly declared scene actor lifetimes.</summary>
        public IReadOnlyList<FreeformSceneActorRuntime> ActorRuntimes => _actorRuntimes;
        /// <summary>Generic observed-room lifetime for the physical receiver space.</summary>
        public FreeformSceneRoomGeometryRuntime RoomGeometryRuntime => _roomGeometryRuntime;
        public event Action StateChanged;

        /// <summary>The app's registered walkable (the NavMesh it registers); false while there is none.</summary>
        public delegate bool WalkableFloorSource(out WalkableRegion region, out float height);

        /// <summary>True while the actors wait for the room's walkable footprint, their navigable root.</summary>
        public bool ActorsWaitingForRoom => _running && _actorsPending;
        /// <summary>Why the actors are waiting, or null.</summary>
        public string ActorWaitState { get; private set; }

        /// <summary>The app's walkable source (the NavMesh), consulted before the MRUK room's floor.</summary>
        public void SetFloorSource(WalkableFloorSource source) => _floorSource = source;

        public bool TryStart(out string error)
        {
            if (_stopping)
                return Fail("Foundation Local is retiring its previous lifetime.", out error);
            if (_running)
            {
                error = null;
                return true;
            }
            if (!ValidateConfiguration(out error))
                return Fail(error, out error);

            try
            {
                SuppressLegacyContent();
                if (!AllLegacyContentSuppressed())
                    throw new InvalidOperationException(
                        "A Fusion content motif is still enabled; the local backend would be a second writer.");
                EnsureOwnedAttachments();
                CreateNativeWorldRoot();
                _localWorld = _worldRoot.AddComponent<SpatialBlendLocalWorld>();
#if UNITY_EDITOR
                TestOnlyAfterLocalWorldCreated?.Invoke(_localWorld);
#endif
                if (!_localWorld.TryInitialize(out error))
                    throw new InvalidOperationException(error);

                if (!_whiteboardObject.TryConfigure(_localWorld, _spaceId,
                    "freeform-scene-whiteboard-root", null, SpatialBlendObjectKind.Object, out error) ||
                    !_chessObject.TryConfigure(_localWorld, _spaceId,
                    "freeform-scene-chess-root", null, SpatialBlendObjectKind.Object, out error))
                    throw new InvalidOperationException(error);
                if (!ConfigureWhiteboardSupport(out error))
                    throw new InvalidOperationException(error);
                // Retain the authored MeshColliders for ISDK/MRUK interactions,
                // but do not claim their complete meshes as Foundation geometry.
                if (!_whiteboardObject.TrySetColliderGeometryEnabled(false, out error) ||
                    !_whiteboardSupport.TrySetColliderGeometryEnabled(false, out error) ||
                    !_chessObject.TrySetColliderGeometryEnabled(false, out error) ||
                    !_whiteboardSupport.TryRegister(out error) || !_chessObject.TryRegister(out error))
                    throw new InvalidOperationException(error);

                if (!_whiteboard.TryCreateSurfaceFrame(out WhiteboardSurfaceFrame whiteboardFrame,
                    out error) || !_whiteboardView.Configure(_whiteboard.SurfaceRenderer,
                    whiteboardFrame, out error))
                    throw new InvalidOperationException(error);
                ChessPieceViewBinding[] viewBindings = BuildViewBindings();
                if (!_chessView.Configure(_chess.Board, viewBindings, out error))
                    throw new InvalidOperationException(error);

                if (!_contentCoordinator.TryCreateWhiteboard(_whiteboardObject, WhiteboardContentId,
                    new WhiteboardContentConfiguration(_whiteboard.TextureWidth,
                        _whiteboard.TextureHeight, _whiteboard.InitialColor),
                    out WhiteboardContentModel whiteboardModel, out error) ||
                    !_contentCoordinator.TryCreateChess(_chessObject, ChessContentId,
                    CaptureInitialChessPoses(), out ChessContentModel chessModel, out error) ||
                    !_whiteboardView.TryBind(whiteboardModel, out error) ||
                    !_chessView.TryBind(chessModel, out error))
                    throw new InvalidOperationException(error);

                // Content views must be live before either input adapter observes authored input.
                if (!_whiteboardInput.Configure(_whiteboardView, _whiteboard.Pointer,
                    _whiteboard.InteractionState, out error))
                    throw new InvalidOperationException(error);
                if (!_chessInput.Configure(_chessView, BuildInputBindings(), out error))
                    throw new InvalidOperationException(error);
                foreach (ColorSelectorMotif selector in _whiteboard.Palette)
                {
                    if (selector == null || !selector.TryBindLocalPen(_whiteboardInput,
                        out IDisposable lease, out error))
                        throw new InvalidOperationException(error ?? "A serialized palette selector is missing.");
                    _paletteLeases.Add(lease);
                }

                if (!FreeformSceneRoomGeometryRuntime.TryStart(_roomGeometry, _localWorld, _spaceId,
                                                                out _roomGeometryRuntime, out error))
                    throw new InvalidOperationException(error);
                // Local authored content is live now. The actors take part in relations only under the room's registered
                // walkable footprint (their navigable root); they start as soon as it exists and report the wait until then.
                _actorsPending = true;
                _reportedActorWait = null;
                if (!TryStartActorsWhenFloorExists(out error))
                    throw new InvalidOperationException(error);

                _running = true;
                LastError = null;
                NotifyStateChanged();
                if (!_running)
                    return Fail("Foundation Local was stopped by a state observer during startup.", out error);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                StopInternal();
                return Fail("Freeform local scene startup failed: " + exception.Message, out error);
            }
        }

        public void Stop()
        {
            if (!_running && _localWorld == null)
                return;
            StopInternal();
            LastError = null;
            NotifyStateChanged();
        }

        private void Update()
        {
            if (!_running || _stopping)
                return;
            if (_roomGeometryRuntime != null && !_roomGeometryRuntime.Tick(out string roomError))
                LastError = roomError;
            if (_actorsPending && !TryStartActorsWhenFloorExists(out string actorError))
            {
                LastError = actorError;
                Debug.LogError("[FreeformSceneLocalBackend] " + actorError);
                _actorsPending = false;
            }
            // Replay and target expiry share Time.unscaledTimeAsDouble inside this
            // runtime. A replay ingress error stays retryable and does not re-enable
            // legacy transport or discard already committed native observations.
            foreach (FreeformSceneActorRuntime runtime in _actorRuntimes)
                if (!runtime.Tick(out string error))
                    LastError = error;
        }

        private void OnDisable() => Stop();
        private void OnDestroy()
        {
            Stop();
            // Root attachments are retained across Stop/TryStart, but never outlive
            // this backend. These are the only components this backend created.
            DestroyOwnedAttachment(_whiteboardInput);
            DestroyOwnedAttachment(_chessInput);
            DestroyOwnedAttachment(_whiteboardView);
            DestroyOwnedAttachment(_chessView);
            if (_whiteboardSupportRoot != null)
                DestroyOwnedGameObject(_whiteboardSupportRoot);
            DestroyOwnedAttachment(_whiteboardObject);
            DestroyOwnedAttachment(_chessObject);
            if (_worldRoot != null)
                DestroyOwnedGameObject(_worldRoot);
        }

        private bool ValidateConfiguration(out string error)
        {
            error = null;
            if (_whiteboard == null || _chess == null || _actors == null || _roomGeometry == null || _whiteboard.MovableRoot == null ||
                _chess.MovableRoot == null || _whiteboard.SurfaceRenderer == null ||
                _whiteboard.Pointer == null || _whiteboard.InteractionState == null ||
                _chess.Board == null || string.IsNullOrWhiteSpace(_spaceId))
                return SetValidationError("The original Whiteboard and Chess scene definitions must be explicitly wired.", out error);
            if (!IsLoadedSceneObject(_whiteboard) || !IsLoadedSceneObject(_chess) ||
                !IsLoadedSceneObject(_roomGeometry) ||
                !IsLoadedSceneObject(_whiteboard.MovableRoot) ||
                !IsLoadedSceneObject(_whiteboard.SurfaceRenderer) ||
                !IsLoadedSceneObject(_whiteboard.Pointer) ||
                !IsLoadedSceneObject(_whiteboard.InteractionState) ||
                !IsLoadedSceneObject(_chess.MovableRoot) || !IsLoadedSceneObject(_chess.Board))
                return SetValidationError("Foundation Local requires instantiated definitions and authored objects from this loaded scene.", out error);
            if (!_actors.ValidateForLoadedScene(gameObject.scene, out error))
                return SetValidationError(error, out error);
            if (!_roomGeometry.ValidateForLoadedScene(gameObject.scene, out error))
                return SetValidationError(error, out error);
            if (!string.Equals(_spaceId, _actors.ReceiverSpaceId, StringComparison.Ordinal))
                return SetValidationError("Room geometry and local content must use the actor receiver space.", out error);
            if (_startupGate == null || !_startupGate.PermitsLocalContentWriter)
                return SetValidationError("The local content backend binds only in FoundationLocal mode; in Fusion mode the Fusion motifs are the only writer.", out error);
            if (!_startupGate.FoundationTransportSuppressed)
                return SetValidationError("Foundation Local requires the authored Fusion bootstrap to be suppressed before startup.", out error);
            if ((!_whiteboard.SurfaceRenderer.transform.IsChildOf(_whiteboard.MovableRoot) &&
                 _whiteboard.SurfaceRenderer.transform != _whiteboard.MovableRoot) ||
                (!_chess.Board.IsChildOf(_chess.MovableRoot) &&
                 _chess.Board != _chess.MovableRoot) ||
                _whiteboard.TextureWidth < 1 || _whiteboard.TextureHeight < 1)
                return SetValidationError("Definitions must reference objects on their instantiated authored roots.", out error);
            if (!_whiteboard.TryCreateSurfaceFrame(out _, out error))
                return SetValidationError(error, out error);
            if (!ValidateChessBindings(out error) || !ValidateNoConflictingOwner(out error) ||
                !ValidateNoLiveTransport(out error))
                return false;
            return true;
        }

        private bool ValidateChessBindings(out string error)
        {
            FreeformSceneChessPieceReference[] pieces = _chess.Pieces;
            if (pieces == null || pieces.Length != 32)
                return SetValidationError("Chess requires exactly 32 explicitly serialized piece bindings.", out error);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var transforms = new HashSet<Transform>();
            foreach (FreeformSceneChessPieceReference piece in pieces)
            {
                if (piece == null || string.IsNullOrWhiteSpace(piece.PieceId) || piece.Piece == null ||
                    piece.Interaction == null || !IsLoadedSceneObject(piece.Piece) ||
                    !IsLoadedSceneObject(piece.Interaction) || !piece.Piece.IsChildOf(_chess.MovableRoot) ||
                    !ids.Add(piece.PieceId) || !transforms.Add(piece.Piece))
                    return SetValidationError("Chess bindings need 32 unique IDs, transforms, and ISDK wrappers.", out error);
            }
            error = null;
            return true;
        }

        private bool IsLoadedSceneObject(Component component)
        {
            return component != null && component.gameObject.scene.IsValid() &&
                   component.gameObject.scene.isLoaded && component.gameObject.scene == gameObject.scene;
        }

        private bool IsLoadedSceneObject(Transform transform)
        {
            return transform != null && transform.gameObject.scene.IsValid() &&
                   transform.gameObject.scene.isLoaded && transform.gameObject.scene == gameObject.scene;
        }

        private bool ValidateNoConflictingOwner(out string error)
        {
            if (_attachmentsCreated && (_whiteboardObject == null || _chessObject == null ||
                _whiteboardSupportRoot == null || _whiteboardSupport == null || _whiteboardView == null ||
                _chessView == null || _whiteboardInput == null ||
                _chessInput == null))
                return SetValidationError("A retained local attachment was destroyed; recreate the scene backend.",
                    out error);
            foreach (Transform root in new[] { _whiteboard.MovableRoot, _chess.MovableRoot })
            {
                SpatialBlendObject owner = root.GetComponent<SpatialBlendObject>();
                if (owner != null && !ReferenceEquals(owner, _whiteboardObject) &&
                    !ReferenceEquals(owner, _chessObject))
                    return SetValidationError("An authored root already has an SpatialBlendObject owner.", out error);
                if (owner != null && owner.IsRegistered)
                    return SetValidationError("An authored root already has a live local owner.", out error);
            }
            if (!_attachmentsCreated &&
                (_whiteboard.SurfaceRenderer.GetComponent<WhiteboardContentView>() != null ||
                 _chess.MovableRoot.GetComponent<ChessContentView>() != null ||
                 _whiteboard.MovableRoot.GetComponent<FreeformSceneWhiteboardInput>() != null ||
                 _chess.MovableRoot.GetComponent<FreeformSceneChessInput>() != null))
                return SetValidationError("An authored content view or input adapter already owns this scene.",
                    out error);
            error = null;
            return true;
        }

        private bool ConfigureWhiteboardSupport(out string error)
        {
            error = null;
            if (_whiteboardSupport == null || _whiteboardObject == null ||
                !_whiteboard.TryCreateSurfaceFrame(out WhiteboardSurfaceFrame frame, out error))
                return false;
            // The support object's identity is receiver-only. This local scene has
            // no explicit source counterpart, so it deliberately acquires no
            // support-correspondence lease or self-mapping.
            if (!_whiteboardSupport.TryConfigure(_localWorld, _spaceId,
                    "freeform-scene-whiteboard-surface", _whiteboardObject,
                    SpatialBlendObjectKind.Affordance, out error))
                return false;
            // The support is parented to the renderer plane, therefore these chart
            // corners are transformed by that exact complete affine on registration
            // and refresh; renderer bounds and root yaw never enter the calculation.
            return _whiteboardSupport.TrySetSupportPolygon(frame.Chart.GetLocalCorners(), out error);
        }

        // One source of truth for the room: the registered walkable footprint, read from the app's NavMesh, else from the MRUK
        // room's measured floor. No polygon is authored here.
        private bool TryReadFloor(out WalkableRegion region, out float height)
        {
            region = null;
            height = 0f;
#if UNITY_EDITOR
            if (TestOnlyFloorSource != null && TestOnlyFloorSource(out region, out height) && region != null &&
                region.HasBoundary)
                return true;
#endif
            if (_floorSource != null && _floorSource(out region, out height) && region != null && region.HasBoundary)
                return true;
            MRUKRoom room = _roomGeometryRuntime != null ? _roomGeometryRuntime.CurrentRoom : null;
            MRUKAnchor floor = room != null ? room.FloorAnchor : null;
            if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3) return false;
            var ring = new Vector2[floor.PlaneBoundary2D.Count];
            float sum = 0f;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector3 world = floor.transform.TransformPoint(new Vector3(floor.PlaneBoundary2D[i].x,
                                                                           floor.PlaneBoundary2D[i].y, 0f));
                ring[i] = new Vector2(world.x, world.z);
                sum += world.y;
            }
            region = new WalkableRegion(ring, null);
            height = sum / ring.Length;
            return true;
        }

        private bool TryStartActorsWhenFloorExists(out string error)
        {
            error = null;
            if (!TryReadFloor(out WalkableRegion region, out float height))
            {
                string roomFailure = _roomGeometryRuntime != null ? _roomGeometryRuntime.LastError : null;
                ActorWaitState = roomFailure != null
                    ? "The actors are waiting for the room's walkable footprint and the room geometry failed: " + roomFailure
                    : "The actors are waiting for the room's walkable footprint (a registered NavMesh or a measured MRUK floor).";
                if (_reportedActorWait != ActorWaitState)
                {
                    _reportedActorWait = ActorWaitState;
                    Debug.LogError("[FreeformSceneLocalBackend] " + ActorWaitState);
                    NotifyStateChanged();
                }
                return true;
            }
            if (!TryCreateFloorRoot(_actors.SourceSpaceId, region, height, out SpatialBlendObject sourceRoot, out error) ||
                !TryCreateFloorRoot(_actors.ReceiverSpaceId, region, height, out SpatialBlendObject receiverRoot, out error) ||
                !FreeformSceneActorRuntime.TryStart(_actors, _localWorld, _worldRoot.transform, sourceRoot, receiverRoot,
                                                    out FreeformSceneActorRuntime actors, out error))
                return false;
            _actorRuntimes.Add(actors);
            _actorsPending = false;
            ActorWaitState = null;
            _reportedActorWait = null;
            NotifyStateChanged();
            return true;
        }

        // The room's footprint as the actors' navigable root: a Region whose navigable surface is the registered walkable
        // ring. The same authored id in both spaces makes the two roots counterparts.
        private bool TryCreateFloorRoot(string spaceId, WalkableRegion region, float height, out SpatialBlendObject root,
                                        out string error)
        {
            root = null;
            var go = new GameObject("Freeform Room Floor " + spaceId);
            go.transform.SetParent(_worldRoot.transform, false);
            var vertices = new Vector3[region.Boundary.Length];
            for (int i = 0; i < vertices.Length; ++i)
                vertices[i] = new Vector3(region.Boundary[i].x, height, region.Boundary[i].y);
            var candidate = go.AddComponent<SpatialBlendObject>();
            if (!candidate.TryConfigure(_localWorld, spaceId, "freeform-room-floor", null, SpatialBlendObjectKind.Region, out error) ||
                !candidate.TrySetColliderGeometryEnabled(false, out error) ||
                !candidate.TrySetSupportPolygon(vertices, out error) || !candidate.TryRegister(out error))
            {
                DestroyOwnedGameObject(go);
                return false;
            }
            root = candidate;
            return true;
        }

        private static bool ValidateNoLiveTransport(out string error)
        {
            // The original scene intentionally keeps an idle Fusion runner prefab
            // available for its explicit legacy profile.  Only a runner that has
            // actually entered Fusion may conflict with this local lifetime.
            foreach (NetworkRunner runner in FindObjectsByType<NetworkRunner>(FindObjectsSortMode.None))
            {
                if (runner != null && runner.IsRunning)
                {
                    error = "Foundation local mode cannot take over a running Fusion session.";
                    return false;
                }
            }
            SpatialBlendManager manager = FindFirstObjectByType<SpatialBlendManager>();
            // SpatialBlendManager loads the compatible native library in Awake even
            // when its ConnectOnAwake policy is false. That idle library state is
            // not a session and must not block the Foundation local world.
            if (manager != null &&
                manager.ConnectionState != SpatialBlendConnectionState.Disconnected)
            {
                error = "Foundation local mode cannot take over a running SpatialBlend session.";
                return false;
            }
            error = null;
            return true;
        }

        private void EnsureOwnedAttachments()
        {
            if (_attachmentsCreated)
                return;
            try
            {
                _whiteboardObject = _whiteboard.MovableRoot.gameObject.AddComponent<SpatialBlendObject>();
                _chessObject = _chess.MovableRoot.gameObject.AddComponent<SpatialBlendObject>();
                // Keep spatial support separate from authored rendering, physics and
                // pointer colliders. Parenting under the renderer preserves its full
                // affine plane transform for moved, tilted and scaled boards.
                _whiteboardSupportRoot = new GameObject("Freeform Scene Whiteboard Surface Support");
                _whiteboardSupportRoot.transform.SetParent(_whiteboard.SurfaceRenderer.transform, false);
                _whiteboardSupport = _whiteboardSupportRoot.AddComponent<SpatialBlendObject>();
                // The view is intentionally on the authored rendering surface, not the
                // movable root: the latter has a non-identity local chart in this prefab.
                _whiteboardView = _whiteboard.SurfaceRenderer.gameObject.AddComponent<WhiteboardContentView>();
                _chessView = _chess.MovableRoot.gameObject.AddComponent<ChessContentView>();
                _whiteboardInput = _whiteboard.MovableRoot.gameObject.AddComponent<FreeformSceneWhiteboardInput>();
                _chessInput = _chess.MovableRoot.gameObject.AddComponent<FreeformSceneChessInput>();
                _attachmentsCreated = true;
            }
            catch
            {
                DestroyOwnedAttachment(_whiteboardInput);
                DestroyOwnedAttachment(_chessInput);
                DestroyOwnedAttachment(_whiteboardView);
                DestroyOwnedAttachment(_chessView);
                if (_whiteboardSupportRoot != null)
                    DestroyOwnedGameObject(_whiteboardSupportRoot);
                DestroyOwnedAttachment(_whiteboardObject);
                DestroyOwnedAttachment(_chessObject);
                _whiteboardInput = null;
                _chessInput = null;
                _whiteboardView = null;
                _chessView = null;
                _whiteboardSupportRoot = null;
                _whiteboardSupport = null;
                _whiteboardObject = null;
                _chessObject = null;
                throw;
            }
        }

        private ChessPieceViewBinding[] BuildViewBindings()
        {
            var bindings = new ChessPieceViewBinding[_chess.Pieces.Length];
            for (int i = 0; i < bindings.Length; ++i)
                bindings[i] = new ChessPieceViewBinding(_chess.Pieces[i].PieceId, _chess.Pieces[i].Piece);
            return bindings;
        }

        private FreeformSceneChessPieceBinding[] BuildInputBindings()
        {
            var bindings = new FreeformSceneChessPieceBinding[_chess.Pieces.Length];
            for (int i = 0; i < bindings.Length; ++i)
                bindings[i] = new FreeformSceneChessPieceBinding(_chess.Pieces[i].PieceId,
                    _chess.Pieces[i].Piece, _chess.Pieces[i].Interaction);
            return bindings;
        }

        private ChessPiecePose[] CaptureInitialChessPoses()
        {
            var poses = new ChessPiecePose[_chess.Pieces.Length];
            Transform board = _chess.Board;
            for (int i = 0; i < poses.Length; ++i)
            {
                Transform piece = _chess.Pieces[i].Piece;
                poses[i] = new ChessPiecePose(_chess.Pieces[i].PieceId,
                    board.InverseTransformPoint(piece.position),
                    Quaternion.Inverse(board.rotation) * piece.rotation);
            }
            return poses;
        }

        private void SuppressLegacyContent()
        {
            Suppress(_whiteboard.LegacyContentBehaviours);
            Suppress(_chess.LegacyContentBehaviours);
        }

        private bool AllLegacyContentSuppressed()
        {
            return AllDisabled(_whiteboard.LegacyContentBehaviours) &&
                   AllDisabled(_chess.LegacyContentBehaviours);
        }

        private static bool AllDisabled(IEnumerable<UnityBehaviour> behaviours)
        {
            if (behaviours == null)
                return true;
            foreach (UnityBehaviour behaviour in behaviours)
                if (behaviour != null && behaviour.enabled)
                    return false;
            return true;
        }

        private void Suppress(IEnumerable<UnityBehaviour> behaviours)
        {
            if (behaviours == null)
                return;
            foreach (UnityBehaviour behaviour in behaviours)
            {
                if (behaviour == null || _suppressedBehaviours.ContainsKey(behaviour))
                    continue;
                _suppressedBehaviours.Add(behaviour, behaviour.enabled);
                behaviour.enabled = false;
            }
        }

        private void StopInternal()
        {
            if (_stopping)
                return;
            _stopping = true;
            try
            {
                foreach (IDisposable lease in _paletteLeases)
                    TryCleanup(() => lease?.Dispose());
                _paletteLeases.Clear();
                TryCleanup(() => { if (_whiteboardInput != null) _whiteboardInput.ReleaseDrawing(); });
                TryCleanup(() => { if (_chessInput != null) _chessInput.ReleaseInput(); });
                TryCleanup(() => _roomGeometryRuntime?.Stop());
                _roomGeometryRuntime = null;
                _actorsPending = false;
                ActorWaitState = null;
                foreach (FreeformSceneActorRuntime actors in _actorRuntimes)
                    TryCleanup(() => actors?.Stop());
                _actorRuntimes.Clear();
                // This owns no cross-space lease, but its child registration must
                // retire before the parent Object on both failed startup and stop.
                TryCleanup(() => { if (_whiteboardSupport != null) _whiteboardSupport.Unregister(); });
                TryCleanup(() => { if (_whiteboardView != null) _whiteboardView.Unbind(); });
                TryCleanup(() => { if (_chessView != null) _chessView.Unbind(); });
                TryCleanup(() => { if (_contentCoordinator != null) _contentCoordinator.Remove(WhiteboardContentId); });
                TryCleanup(() => { if (_contentCoordinator != null) _contentCoordinator.Remove(ChessContentId); });
                TryCleanup(() => { if (_whiteboardObject != null) _whiteboardObject.Unregister(); });
                TryCleanup(() => { if (_chessObject != null) _chessObject.Unregister(); });
                TryCleanup(() => { if (_localWorld != null) _localWorld.Shutdown(); });
                // The native world cannot be reinitialized after Shutdown. Retire
                // its complete root, leaving reusable authored-root attachments
                // intact; a retry gets one fresh active world root.
                if (_worldRoot != null)
                {
                    _worldRoot.SetActive(false);
                    _worldRoot.transform.SetParent(null, false);
                    DestroyOwnedGameObject(_worldRoot);
                }
                foreach (KeyValuePair<UnityBehaviour, bool> entry in _suppressedBehaviours)
                    if (entry.Key != null)
                        entry.Key.enabled = entry.Value;
                _suppressedBehaviours.Clear();
                _localWorld = null;
                _contentCoordinator = null;
                _worldRoot = null;
                _running = false;
            }
            finally
            {
                _stopping = false;
            }
        }

        private bool SetValidationError(string message, out string error)
        {
            error = message;
            return Fail(error, out error);
        }

        private bool Fail(string message, out string error)
        {
            LastError = message;
            error = message;
            return false;
        }

        private void NotifyStateChanged()
        {
            Action callbacks = StateChanged;
            if (callbacks == null)
                return;
            foreach (Delegate callback in callbacks.GetInvocationList())
            {
                try { ((Action)callback).Invoke(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        private static void DestroyOwnedAttachment(Component component)
        {
            if (component != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(component);
                else
                    UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static void TryCleanup(Action cleanup)
        {
            try { cleanup?.Invoke(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private static void DestroyOwnedGameObject(GameObject gameObject)
        {
            if (gameObject == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(gameObject);
            else
                UnityEngine.Object.DestroyImmediate(gameObject);
        }

        private void CreateNativeWorldRoot()
        {
            _worldRoot = new GameObject("Freeform Scene Local Foundation World");
            _worldRoot.transform.SetParent(transform, false);
            _contentCoordinator = _worldRoot.AddComponent<FreeformContentCoordinator>();
        }
    }
}
