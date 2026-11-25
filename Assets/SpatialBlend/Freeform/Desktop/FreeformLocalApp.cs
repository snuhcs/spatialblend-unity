// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    public enum FreeformDesktopPointerPhase { Press, Drag, Release, Cancel }
    public enum FreeformDesktopPointerTarget { None, Whiteboard, Chess }

    /// <summary>
    /// A self-contained desktop demonstration of the in-process Freeform models.
    /// It owns a local Foundation world and ordinary Unity objects, with no network
    /// or external SDK integration.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformLocalApp : MonoBehaviour
    {
        public const string DefaultWhiteboardContentId = "local-whiteboard";
        public const string DefaultChessContentId = "local-chess";
        public const string DefaultSelectedChessPieceId = "white-pawn-a2";
        private const float ChessBoardSize = 1.5f;
        private const float ChessTileSize = ChessBoardSize / 8f;
        private const float ChessPieceHeight = .16f;
        private static readonly Rect DesktopUiRect = new Rect(12f, 12f, 220f, 168f);
        private static readonly string[] BackRankPieceTypes =
        {
            "rook", "knight", "bishop", "queen", "king", "bishop", "knight", "rook"
        };

        [SerializeField] private Camera _camera;
        [SerializeField] private bool _createCameraWhenUnassigned = true;
        [SerializeField] private bool _showDesktopUi = true;

        private readonly List<Material> _ownedMaterials = new List<Material>();
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();
        private readonly Dictionary<Transform, string> _receiverChessPieceIds =
            new Dictionary<Transform, string>();
        private GameObject _workspaceRoot;
        private GameObject _ownedCameraObject;
        private SpatialBlendLocalWorld _localWorld;
        private FreeformContentCoordinator _coordinator;
        private WhiteboardContentView _sourceWhiteboardView;
        private WhiteboardContentView _receiverWhiteboardView;
        private ChessContentView _sourceChessView;
        private ChessContentView _receiverChessView;
        private SpatialBlendObject _sourceWhiteboardObject;
        private SpatialBlendObject _receiverWhiteboardObject;
        private SpatialBlendObject _sourceChessObject;
        private SpatialBlendObject _receiverChessObject;
        private bool _initialized;
        private bool _startAttempted;
        private FreeformDesktopPointerTarget _desktopPointerTarget;
        private Vector3 _lastPointerPoint;
        private string _selectedChessPieceId;

        public bool IsInitialized => _initialized;
        public string LastError { get; private set; }
        public Camera ActiveCamera => _camera;
        public bool OwnsActiveCamera => _ownedCameraObject != null;
        public GameObject WorkspaceRoot => _workspaceRoot;
        public SpatialBlendLocalWorld LocalWorld => _localWorld;
        public WhiteboardContentView SourceWhiteboardView => _sourceWhiteboardView;
        public WhiteboardContentView ReceiverWhiteboardView => _receiverWhiteboardView;
        public ChessContentView SourceChessView => _sourceChessView;
        public ChessContentView ReceiverChessView => _receiverChessView;
        public SpatialBlendObject SourceWhiteboardObject => _sourceWhiteboardObject;
        public SpatialBlendObject ReceiverWhiteboardObject => _receiverWhiteboardObject;
        public SpatialBlendObject SourceChessObject => _sourceChessObject;
        public SpatialBlendObject ReceiverChessObject => _receiverChessObject;
        public string SelectedChessPieceId => _selectedChessPieceId;
        public FreeformDesktopPointerTarget DesktopPointerTarget => _desktopPointerTarget;

        /// <summary>Sets camera policy before initialization. Assigned cameras are never moved or destroyed.</summary>
        public bool ConfigureCamera(Camera camera, bool createWhenUnassigned, out string error)
        {
            if (_initialized)
            {
                error = "Teardown the local app before changing its camera policy.";
                return false;
            }
            _camera = camera;
            _createCameraWhenUnassigned = createWhenUnassigned;
            error = null;
            return true;
        }

        /// <summary>Builds the owned workspace and its default local whiteboard and chess content.</summary>
        public bool Initialize(out string error)
        {
            if (_initialized)
            {
                error = null;
                return true;
            }
            if (_workspaceRoot != null)
                Teardown();
            try
            {
                EnsureCamera();
                _workspaceRoot = new GameObject("Freeform Local Workspace");
                _workspaceRoot.transform.SetParent(transform, false);
                _localWorld = _workspaceRoot.AddComponent<SpatialBlendLocalWorld>();
                if (!_localWorld.TryInitialize(out error))
                    throw new InvalidOperationException("Owned local world failed to initialize: " + error);
                _coordinator = _workspaceRoot.AddComponent<FreeformContentCoordinator>();
                _sourceWhiteboardView = CreateWhiteboardView("Source Whiteboard", new Vector3(-2f, 1.2f, 0f),
                    "freeform-source", "source-whiteboard", out _sourceWhiteboardObject);
                _receiverWhiteboardView = CreateWhiteboardView("Receiver Whiteboard", new Vector3(2f, 1.2f, 0f),
                    "freeform-receiver", "receiver-whiteboard", out _receiverWhiteboardObject);
                _sourceChessView = CreateChessView("Source Chess", new Vector3(-2f, -0.35f, 0.5f),
                    "freeform-source", "source-chess", out _sourceChessObject);
                _receiverChessView = CreateChessView("Receiver Chess", new Vector3(2f, -0.35f, 0.5f),
                    "freeform-receiver", "receiver-chess", out _receiverChessObject);
                RegisterViewObjects();
                _initialized = true;
                if (!CreateDefaultContent(out error))
                {
                    Teardown();
                    return false;
                }
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Teardown();
                error = "Freeform local workspace initialization failed: " + exception.Message;
                LastError = error;
                return false;
            }
        }

        /// <summary>Creates or rebinds the two default logical content models.</summary>
        public bool CreateDefaultContent(out string error)
        {
            if (!_initialized || _coordinator == null)
            {
                error = "Initialize the local workspace before creating content.";
                return false;
            }
            bool createdWhiteboard = false;
            if (!_coordinator.Catalog.TryGetWhiteboard(DefaultWhiteboardContentId,
                                                        out WhiteboardContentModel whiteboard))
            {
                if (!_coordinator.TryCreateWhiteboard(_sourceWhiteboardObject, DefaultWhiteboardContentId,
                    new WhiteboardContentConfiguration(128, 128, Color.white), out whiteboard, out error))
                    return SetFailure(error, out error);
                createdWhiteboard = true;
            }
            if (!_sourceWhiteboardView.TryBind(whiteboard, out error) ||
                !_receiverWhiteboardView.TryBind(whiteboard, out error))
            {
                if (createdWhiteboard)
                    _coordinator.Remove(DefaultWhiteboardContentId);
                return SetFailure(error, out error);
            }

            bool createdChess = false;
            if (!_coordinator.Catalog.TryGetChess(DefaultChessContentId, out ChessContentModel chess))
            {
                ChessPiecePose[] pieces = CreateInitialChessPieces();
                if (!_coordinator.TryCreateChess(_sourceChessObject, DefaultChessContentId, pieces, out chess, out error))
                {
                    if (createdWhiteboard)
                        _coordinator.Remove(DefaultWhiteboardContentId);
                    return SetFailure(error, out error);
                }
                createdChess = true;
            }
            if (!_sourceChessView.TryBind(chess, out error) || !_receiverChessView.TryBind(chess, out error))
            {
                if (createdChess)
                    _coordinator.Remove(DefaultChessContentId);
                return SetFailure(error, out error);
            }
            LastError = null;
            error = null;
            return true;
        }

        public bool RemoveContent(string contentId)
        {
            if (!_initialized || _coordinator == null)
                return false;
            bool removed = _coordinator.Remove(contentId);
            if (removed && contentId == DefaultChessContentId)
                _selectedChessPieceId = null;
            if (!removed)
                LastError = "No live local content exists with that ID.";
            else
                LastError = null;
            return removed;
        }

        public bool TryGetWhiteboard(out WhiteboardContentModel model)
        {
            model = null;
            return _coordinator != null && _coordinator.Catalog.TryGetWhiteboard(DefaultWhiteboardContentId,
                                                                                   out model);
        }

        public bool TryGetChess(out ChessContentModel model)
        {
            model = null;
            return _coordinator != null && _coordinator.Catalog.TryGetChess(DefaultChessContentId, out model);
        }

        public bool ClearWhiteboard(out string error)
        {
            if (!TryGetWhiteboard(out WhiteboardContentModel model))
            {
                error = "Create the default whiteboard before clearing it.";
                return SetFailure(error, out error);
            }
            bool cleared = model.TryClear(out error);
            if (!cleared)
                return SetFailure(error, out error);
            LastError = null;
            return true;
        }

        public bool DrawWhiteboardUv(Vector2 startUv, Vector2 endUv, Color32 color, int brushRadius,
                                     out string error)
        {
            if (!TryGetWhiteboard(out WhiteboardContentModel model))
            {
                error = "Create the default whiteboard before drawing.";
                return SetFailure(error, out error);
            }
            bool drawn = model.DrawLine(startUv, endUv, color, brushRadius, out error);
            if (!drawn)
                return SetFailure(error, out error);
            LastError = null;
            return true;
        }

        /// <summary>Uses the receiver view's local projection, matching desktop ray input.</summary>
        public bool DrawWhiteboardWorld(Vector3 worldStart, Vector3 worldEnd, Color32 color,
                                        int brushRadius, out string error)
        {
            if (_receiverWhiteboardView == null)
            {
                error = "Initialize the receiver whiteboard before drawing.";
                return SetFailure(error, out error);
            }
            bool drawn = _receiverWhiteboardView.TryDrawWorldLine(worldStart, worldEnd, color,
                                                                    brushRadius, out error);
            if (!drawn)
                return SetFailure(error, out error);
            LastError = null;
            return true;
        }

        public bool TryMoveChessPiece(string pieceId, Vector3 boardLocalPosition,
                                      Quaternion boardLocalRotation, out string error)
        {
            if (!TryGetChess(out ChessContentModel model))
            {
                error = "Create the default chess content before moving a piece.";
                return SetFailure(error, out error);
            }
            bool moved = model.TrySetPiecePose(pieceId, boardLocalPosition, boardLocalRotation, out error);
            if (!moved)
                return SetFailure(error, out error);
            LastError = null;
            return true;
        }

        /// <summary>Selects a configured local chess piece for the next board-plane move.</summary>
        public bool TrySelectChessPiece(string pieceId, out string error)
        {
            if (!TryGetChess(out ChessContentModel model) || !model.TryGetPiecePose(pieceId, out _))
            {
                error = "Create the default chess content and select one of its piece IDs first.";
                return SetFailure(error, out error);
            }
            _selectedChessPieceId = pieceId;
            LastError = null;
            error = null;
            return true;
        }

        /// <summary>Moves the selected piece on the local board, preserving its pose height and rotation.</summary>
        public bool TryMoveSelectedChessPiece(Vector3 boardPlanePosition, out string error)
        {
            if (string.IsNullOrEmpty(_selectedChessPieceId) || !TryGetChess(out ChessContentModel model) ||
                !model.TryGetPiecePose(_selectedChessPieceId, out ChessPiecePose pose))
            {
                error = "Select a live chess piece before choosing a board destination.";
                return SetFailure(error, out error);
            }
            Vector3 destination = new Vector3(SnapToSquare(boardPlanePosition.x), pose.BoardLocalPosition.y,
                                              SnapToSquare(boardPlanePosition.z));
            return TryMoveChessPiece(_selectedChessPieceId, destination, pose.BoardLocalRotation, out error);
        }

        /// <summary>
        /// Routes a real Unity bottom-left screen coordinate through the desktop
        /// camera and receiver colliders. A true return means content consumed the
        /// pointer; UI-intercepted input is intentionally ignored.
        /// </summary>
        public bool TryHandleDesktopPointer(Vector2 screenPoint, FreeformDesktopPointerPhase phase,
                                            bool uiIntercepted, out string error)
        {
            error = null;
            if (phase == FreeformDesktopPointerPhase.Cancel || uiIntercepted)
            {
                ResetDesktopPointer();
                return false;
            }
            if (!_initialized || _camera == null || !IsFiniteScreenPoint(screenPoint))
            {
                error = "Desktop pointer input requires an initialized workspace, camera, and finite screen point.";
                return SetFailure(error, out error);
            }
            if (phase != FreeformDesktopPointerPhase.Press && phase != FreeformDesktopPointerPhase.Drag &&
                phase != FreeformDesktopPointerPhase.Release)
            {
                error = "Desktop pointer phase is invalid.";
                return SetFailure(error, out error);
            }
            if (phase == FreeformDesktopPointerPhase.Release)
            {
                bool consumed = _desktopPointerTarget != FreeformDesktopPointerTarget.None;
                if (_desktopPointerTarget == FreeformDesktopPointerTarget.Whiteboard &&
                    TryRaycastReceiverWhiteboard(screenPoint, out Vector3 finalPoint) &&
                    !DrawWhiteboardWorld(_lastPointerPoint, finalPoint, Color.blue, 2, out error))
                {
                    ResetDesktopPointer();
                    return SetFailure(error, out error);
                }
                ResetDesktopPointer();
                return consumed;
            }
            if (phase == FreeformDesktopPointerPhase.Drag)
            {
                if (_desktopPointerTarget != FreeformDesktopPointerTarget.Whiteboard)
                    return _desktopPointerTarget != FreeformDesktopPointerTarget.None;
                if (TryRaycastReceiverWhiteboard(screenPoint, out Vector3 point) &&
                    DrawWhiteboardWorld(_lastPointerPoint, point, Color.blue, 2, out error))
                    _lastPointerPoint = point;
                return true;
            }

            // A new press cannot inherit an incomplete interaction from an earlier input source.
            ResetDesktopPointer();
            if (TryRaycastReceiverWhiteboard(screenPoint, out Vector3 whiteboardPoint))
            {
                if (!DrawWhiteboardWorld(whiteboardPoint, whiteboardPoint, Color.blue, 2, out error))
                    return SetFailure(error, out error);
                _lastPointerPoint = whiteboardPoint;
                _desktopPointerTarget = FreeformDesktopPointerTarget.Whiteboard;
                return true;
            }
            if (TryRaycastReceiverChess(screenPoint, out RaycastHit chessHit))
            {
                _desktopPointerTarget = FreeformDesktopPointerTarget.Chess;
                return TryProcessChessPress(chessHit, out error);
            }
            return false;
        }

        /// <summary>Unbinds/removes local content and destroys only objects and materials created by this app.</summary>
        public void Teardown()
        {
            ResetDesktopPointer();
            bool ownedCamera = _ownedCameraObject != null;
            if (_coordinator != null)
            {
                _coordinator.Remove(DefaultWhiteboardContentId);
                _coordinator.Remove(DefaultChessContentId);
            }
            UnregisterViewObjects();
            if (_localWorld != null) _localWorld.Shutdown();
            foreach (Material material in _ownedMaterials)
                DestroyOwned(material);
            _ownedMaterials.Clear();
            foreach (Mesh mesh in _ownedMeshes)
                DestroyOwned(mesh);
            _ownedMeshes.Clear();
            DestroyOwned(_workspaceRoot);
            DestroyOwned(_ownedCameraObject);
            _workspaceRoot = null;
            _ownedCameraObject = null;
            _localWorld = null;
            _coordinator = null;
            _sourceWhiteboardView = null;
            _receiverWhiteboardView = null;
            _sourceChessView = null;
            _receiverChessView = null;
            _sourceWhiteboardObject = null;
            _receiverWhiteboardObject = null;
            _sourceChessObject = null;
            _receiverChessObject = null;
            _receiverChessPieceIds.Clear();
            _selectedChessPieceId = null;
            _initialized = false;
            if (ownedCamera)
                _camera = null;
        }

        private void Start()
        {
            _startAttempted = true;
            if (!Initialize(out string error))
                Debug.LogError("[FreeformLocalApp] " + error, this);
        }

        private void OnEnable()
        {
            if (_startAttempted && !_initialized && !Initialize(out string error))
                Debug.LogError("[FreeformLocalApp] " + error, this);
        }

        private void OnDisable() => Teardown();
        private void OnDestroy() => Teardown();

        private void OnGUI()
        {
            if (!_initialized || _camera == null)
                return;
            Event current = Event.current;
            if (!_showDesktopUi)
                HandleDesktopGuiPointer(current, false);
            else
            {
                bool uiIntercepted = current != null && DesktopUiRect.Contains(current.mousePosition);
                DrawDesktopUi();
                HandleDesktopGuiPointer(current, uiIntercepted);
            }
        }

        private void DrawDesktopUi()
        {
            GUILayout.BeginArea(DesktopUiRect, "Freeform Local", GUI.skin.window);
            if (GUILayout.Button("Clear whiteboard")) ClearWhiteboard(out _);
            if (GUILayout.Button("Move white pawn"))
            {
                TrySelectChessPiece(DefaultSelectedChessPieceId, out _);
                TryMoveSelectedChessPiece(new Vector3(.05f, 0f, -.05f), out _);
            }
            if (GUILayout.Button("Remove chess")) RemoveContent(DefaultChessContentId);
            if (GUILayout.Button("Recreate content")) CreateDefaultContent(out _);
            GUILayout.Label("Chess: click a receiver piece, then a square.");
            GUILayout.Label("Drag on receiver whiteboard to draw.");
            GUILayout.EndArea();
        }

        private void HandleDesktopGuiPointer(Event current, bool uiIntercepted)
        {
            if (current == null || current.button != 0)
                return;
            FreeformDesktopPointerPhase? phase = current.type == EventType.MouseDown
                ? FreeformDesktopPointerPhase.Press
                : current.type == EventType.MouseDrag ? FreeformDesktopPointerPhase.Drag
                : current.type == EventType.MouseUp ? FreeformDesktopPointerPhase.Release
                : (FreeformDesktopPointerPhase?)null;
            if (!phase.HasValue)
                return;
            bool consumed = TryHandleDesktopPointer(GuiToScreenPoint(current.mousePosition), phase.Value,
                                                    uiIntercepted, out _);
            if (consumed || uiIntercepted)
                current.Use();
        }

        private void EnsureCamera()
        {
            if (_camera != null)
                return;
            if (!_createCameraWhenUnassigned)
                throw new InvalidOperationException("Assign a camera or enable Create Camera When Unassigned.");
            _ownedCameraObject = new GameObject("Freeform Local Camera");
            _camera = _ownedCameraObject.AddComponent<Camera>();
            // The receiver chess board needs an elevated, downward view: a shallow
            // camera ray crosses the front rank before it reaches an empty square.
            // This framing still keeps the vertical whiteboards and both placements
            // in the generated camera's frustum without touching assigned cameras.
            Vector3 cameraPosition = new Vector3(0f, 4.5f, -6f);
            Vector3 workspaceFocus = new Vector3(0f, .35f, .3f);
            _ownedCameraObject.transform.SetPositionAndRotation(cameraPosition,
                Quaternion.LookRotation(workspaceFocus - cameraPosition, Vector3.up));
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(.08f, .08f, .1f, 1f);
        }

        private WhiteboardContentView CreateWhiteboardView(string name, Vector3 position, string spaceId,
                                                            string objectId, out SpatialBlendObject authoredObject)
        {
            var board = new GameObject(name);
            board.SetActive(false);
            board.transform.SetParent(_workspaceRoot.transform, false);
            board.transform.SetPositionAndRotation(position, Quaternion.identity);
            MeshFilter filter = board.AddComponent<MeshFilter>();
            filter.sharedMesh = CreateQuadMesh();
            Renderer renderer = board.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateMaterial("Unlit/Texture", Color.white);
            var collider = board.AddComponent<BoxCollider>();
            collider.size = new Vector3(1f, 1f, .02f);
            WhiteboardContentView view = board.AddComponent<WhiteboardContentView>();
            if (!view.Configure(renderer, out string error))
                throw new InvalidOperationException(error);
            authoredObject = board.AddComponent<SpatialBlendObject>();
            ConfigureViewObject(authoredObject, spaceId, objectId);
            board.SetActive(true);
            return view;
        }

        private ChessContentView CreateChessView(string name, Vector3 position, string spaceId,
                                                  string objectId, out SpatialBlendObject authoredObject)
        {
            var board = new GameObject(name);
            board.SetActive(false);
            board.transform.SetParent(_workspaceRoot.transform, false);
            board.transform.SetPositionAndRotation(position, Quaternion.identity);
            board.transform.localScale = Vector3.one;
            CreateChessTiles(board.transform);
            ChessContentView view = board.AddComponent<ChessContentView>();
            ChessPiecePose[] pieces = CreateInitialChessPieces();
            var bindings = new ChessPieceViewBinding[pieces.Length];
            Material whiteMaterial = CreateMaterial("Unlit/Color", new Color(.9f, .9f, .82f));
            Material blackMaterial = CreateMaterial("Unlit/Color", new Color(.08f, .08f, .1f));
            for (int index = 0; index < pieces.Length; ++index)
            {
                string pieceId = pieces[index].PieceId;
                Transform piece = CreateChessPiece(board.transform, pieceId,
                    pieceId.StartsWith("white-", StringComparison.Ordinal) ? whiteMaterial : blackMaterial);
                bindings[index] = new ChessPieceViewBinding(pieceId, piece);
                if (spaceId == "freeform-receiver")
                    _receiverChessPieceIds.Add(piece, pieceId);
            }
            if (!view.Configure(board.transform, bindings, out string error))
                throw new InvalidOperationException(error);
            authoredObject = board.AddComponent<SpatialBlendObject>();
            ConfigureViewObject(authoredObject, spaceId, objectId);
            board.SetActive(true);
            return view;
        }

        private void CreateChessTiles(Transform board)
        {
            Material light = CreateMaterial("Unlit/Color", new Color(.8f, .68f, .48f));
            Material dark = CreateMaterial("Unlit/Color", new Color(.28f, .18f, .1f));
            for (int rank = 0; rank < 8; ++rank)
                for (int file = 0; file < 8; ++file)
                {
                    GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tile.name = "Square " + (char)('A' + file) + (rank + 1);
                    tile.transform.SetParent(board, false);
                    tile.transform.localPosition = BoardSquare(file, rank, 0f);
                    tile.transform.localScale = new Vector3(ChessTileSize, .06f, ChessTileSize);
                    tile.GetComponent<Renderer>().sharedMaterial = ((file + rank) & 1) == 0 ? light : dark;
                }
        }

        private Transform CreateChessPiece(Transform board, string pieceId, Material material)
        {
            string type = PieceType(pieceId);
            PrimitiveType primitive = type == "rook" ? PrimitiveType.Cube :
                                      type == "knight" ? PrimitiveType.Capsule :
                                      type == "bishop" || type == "king" ? PrimitiveType.Cylinder :
                                      PrimitiveType.Sphere;
            GameObject piece = GameObject.CreatePrimitive(primitive);
            piece.name = pieceId;
            piece.transform.SetParent(board, false);
            piece.transform.localScale = PieceScale(type);
            piece.GetComponent<Renderer>().sharedMaterial = material;
            return piece.transform;
        }

        private Material CreateMaterial(string shaderName, Color color)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
                throw new InvalidOperationException("Required desktop shader is unavailable: " + shaderName + ".");
            var material = new Material(shader);
            if (material.HasProperty("_Color"))
                material.color = color;
            _ownedMaterials.Add(material);
            return material;
        }

        private Mesh CreateQuadMesh()
        {
            var mesh = new Mesh { name = "Freeform Local Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f, -.5f, 0f), new Vector3(.5f, -.5f, 0f),
                new Vector3(.5f, .5f, 0f), new Vector3(-.5f, .5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            _ownedMeshes.Add(mesh);
            return mesh;
        }

        private void ConfigureViewObject(SpatialBlendObject authoredObject, string spaceId, string objectId)
        {
            if (!authoredObject.TryConfigure(_localWorld, spaceId, objectId, null,
                                             SpatialBlendObjectKind.Object, out string error))
                throw new InvalidOperationException(error);
        }

        private void RegisterViewObjects()
        {
            foreach (SpatialBlendObject authoredObject in new[]
                     {
                         _sourceWhiteboardObject, _receiverWhiteboardObject,
                         _sourceChessObject, _receiverChessObject
                     })
                if (!authoredObject.TryRegister(out string error))
                    throw new InvalidOperationException("Owned view registration failed: " + error);
        }

        private void UnregisterViewObjects()
        {
            if (_sourceWhiteboardObject != null) _sourceWhiteboardObject.Unregister();
            if (_receiverWhiteboardObject != null) _receiverWhiteboardObject.Unregister();
            if (_sourceChessObject != null) _sourceChessObject.Unregister();
            if (_receiverChessObject != null) _receiverChessObject.Unregister();
        }

        private bool TryProcessChessPress(RaycastHit hit, out string error)
        {
            error = null;
            for (Transform current = hit.collider.transform; current != null &&
                 current != _receiverChessView.transform; current = current.parent)
                if (_receiverChessPieceIds.TryGetValue(current, out string pieceId))
                    return TrySelectChessPiece(pieceId, out error);
            if (_selectedChessPieceId == null)
                return true;
            Vector3 localPoint = _receiverChessView.BoardTransform.InverseTransformPoint(hit.point);
            return TryMoveSelectedChessPiece(localPoint, out error);
        }

        private bool TryRaycastReceiverWhiteboard(Vector2 screenPoint, out Vector3 point)
        {
            point = default;
            if (_receiverWhiteboardView == null || !TryRaycastClosest(screenPoint, out RaycastHit hit) ||
                hit.collider == null || hit.collider.gameObject != _receiverWhiteboardView.gameObject)
                return false;
            point = hit.point;
            return true;
        }

        private bool TryRaycastReceiverChess(Vector2 screenPoint, out RaycastHit result)
        {
            result = default;
            if (_receiverChessView == null || !TryRaycastClosest(screenPoint, out result) ||
                result.collider == null || !result.collider.transform.IsChildOf(_receiverChessView.transform))
                return false;
            return true;
        }

        private bool TryRaycastClosest(Vector2 screenPoint, out RaycastHit result)
        {
            result = default;
            RaycastHit[] hits = Physics.RaycastAll(_camera.ScreenPointToRay(screenPoint), 100f);
            bool found = false;
            float nearest = float.PositiveInfinity;
            foreach (RaycastHit hit in hits)
                if (hit.collider != null && hit.distance < nearest)
                {
                    result = hit;
                    nearest = hit.distance;
                    found = true;
                }
            return found;
        }

        private void ResetDesktopPointer()
        {
            _desktopPointerTarget = FreeformDesktopPointerTarget.None;
            _lastPointerPoint = default;
        }

        private static Vector2 GuiToScreenPoint(Vector2 guiPoint) =>
            new Vector2(guiPoint.x, Screen.height - guiPoint.y);

        private static bool IsFiniteScreenPoint(Vector2 point) =>
            !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) && !float.IsInfinity(point.y);

        private static ChessPiecePose[] CreateInitialChessPieces()
        {
            var pieces = new List<ChessPiecePose>(32);
            AddInitialSide(pieces, "white", 0, 1, Quaternion.identity);
            AddInitialSide(pieces, "black", 7, 6, Quaternion.Euler(0f, 180f, 0f));
            return pieces.ToArray();
        }

        private static void AddInitialSide(List<ChessPiecePose> pieces, string side, int backRank,
                                           int pawnRank, Quaternion rotation)
        {
            for (int file = 0; file < 8; ++file)
            {
                string square = ((char)('a' + file)).ToString();
                pieces.Add(new ChessPiecePose(side + "-" + BackRankPieceTypes[file] + "-" + square +
                                               (backRank + 1),
                                               BoardSquare(file, backRank, ChessPieceHeight), rotation));
                pieces.Add(new ChessPiecePose(side + "-pawn-" + square + (pawnRank + 1),
                                               BoardSquare(file, pawnRank, ChessPieceHeight), rotation));
            }
        }

        private static Vector3 BoardSquare(int file, int rank, float height) =>
            new Vector3((file + .5f) * ChessTileSize - ChessBoardSize * .5f, height,
                        (rank + .5f) * ChessTileSize - ChessBoardSize * .5f);

        private static float SnapToSquare(float coordinate)
        {
            int index = Mathf.Clamp(Mathf.FloorToInt((coordinate + ChessBoardSize * .5f) / ChessTileSize),
                                    0, 7);
            return BoardSquare(index, 0, 0f).x;
        }

        private static string PieceType(string pieceId)
        {
            string[] tokens = pieceId.Split('-');
            return tokens.Length == 3 ? tokens[1] : "pawn";
        }

        private static Vector3 PieceScale(string pieceType)
        {
            if (pieceType == "rook") return new Vector3(.13f, .18f, .13f);
            if (pieceType == "knight") return new Vector3(.14f, .2f, .14f);
            if (pieceType == "bishop") return new Vector3(.13f, .2f, .13f);
            if (pieceType == "king") return new Vector3(.14f, .24f, .14f);
            if (pieceType == "queen") return new Vector3(.16f, .2f, .16f);
            return Vector3.one * .15f;
        }

        private bool SetFailure(string message, out string error)
        {
            LastError = message;
            error = message;
            return false;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }
    }
}
