// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Interaction;
using SpatialBlend.Freeform;
using UnityEngine;
using UnityEngine.Events;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    [Serializable]
    public struct FreeformSceneChessPieceBinding
    {
        [SerializeField] private string _pieceId;
        [SerializeField] private Transform _piece;
        [SerializeField] private InteractableUnityEventWrapper _interaction;

        public FreeformSceneChessPieceBinding(string pieceId, Transform piece,
                                              InteractableUnityEventWrapper interaction)
        {
            _pieceId = pieceId;
            _piece = piece;
            _interaction = interaction;
        }

        public string PieceId => _pieceId;
        public Transform Piece => _piece;
        public InteractableUnityEventWrapper Interaction => _interaction;
    }

    /// <summary>
    /// Captures poses from the original authored chess-piece interactables into a
    /// bound local Freeform model. It deliberately adds no chess rules or networking.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneChessInput : MonoBehaviour
    {
        private sealed class RuntimeBinding
        {
            public RuntimeBinding(FreeformSceneChessPieceBinding binding)
            {
                Binding = binding;
            }

            public FreeformSceneChessPieceBinding Binding { get; }
            public UnityAction Select { get; set; }
            public UnityAction Unselect { get; set; }
        }

        private readonly struct CapturedPose
        {
            public CapturedPose(RuntimeBinding binding, Vector3 boardLocalPosition,
                                Quaternion boardLocalRotation)
            {
                Binding = binding;
                BoardLocalPosition = boardLocalPosition;
                BoardLocalRotation = boardLocalRotation;
            }

            public RuntimeBinding Binding { get; }
            public Vector3 BoardLocalPosition { get; }
            public Quaternion BoardLocalRotation { get; }
        }

        private readonly Dictionary<string, RuntimeBinding> _bindings =
            new Dictionary<string, RuntimeBinding>(StringComparer.Ordinal);
        private readonly HashSet<RuntimeBinding> _selectedBindings =
            new HashSet<RuntimeBinding>();
        private ChessContentView _view;
        private ChessContentModel _model;
        private int _configurationVersion;

        public bool Configure(ChessContentView view,
                              IReadOnlyList<FreeformSceneChessPieceBinding> bindings,
                              out string error)
        {
            if (!TryBuildBindings(view, bindings, out Dictionary<string, RuntimeBinding> candidate,
                                  out error))
                return false;

            // Nothing is unsubscribed until the complete replacement configuration is valid.
            int beforeReleaseVersion = _configurationVersion;
            bool hadOwnedState = _view != null || _bindings.Count > 0 || _selectedBindings.Count > 0;
            ReleaseInput();
            int expectedDetachedVersion = hadOwnedState ? beforeReleaseVersion + 1 : beforeReleaseVersion;
            if (_view != null || _configurationVersion != expectedDetachedVersion)
            {
                error = "Chess input ownership changed while the prior selection was released.";
                return false;
            }
            // A Changed callback from final capture may have rebound the candidate
            // view/model while all old subscriptions were detached.
            if (!TryBuildBindings(view, bindings, out candidate, out error))
                return false;
            _view = view;
            _model = view.Model;
            ++_configurationVersion;
            foreach (KeyValuePair<string, RuntimeBinding> pair in candidate)
            {
                RuntimeBinding captured = pair.Value;
                captured.Select = () => OnPieceSelected(captured);
                captured.Unselect = () => OnPieceUnselected(captured);
                captured.Binding.Interaction.WhenSelect.AddListener(captured.Select);
                captured.Binding.Interaction.WhenUnselect.AddListener(captured.Unselect);
                _bindings.Add(pair.Key, captured);
            }
            error = null;
            return true;
        }

        public void ReleaseInput()
        {
            if (_view == null && _bindings.Count == 0 && _selectedBindings.Count == 0)
                return;
            ChessContentView view = _view;
            ChessContentModel model = _model;
            var selected = new List<RuntimeBinding>(_selectedBindings);
            var captured = new List<CapturedPose>(selected.Count);
            bool hasCompleteSnapshot = TrySnapshotAll(view, model, selected, captured, out _);
            // Detach all owned state before invoking the model/view. Changed handlers
            // may reconfigure or release this component synchronously.
            int detachedVersion = ++_configurationVersion;
            _selectedBindings.Clear();
            foreach (RuntimeBinding binding in _bindings.Values)
                Unsubscribe(binding);
            _bindings.Clear();
            _view = null;
            _model = null;
            if (!hasCompleteSnapshot)
                return;
            foreach (CapturedPose pose in captured)
            {
                if (!IsDetachedCaptureCurrent(detachedVersion, view, model, pose))
                    break;
                CommitCapturedPose(view, model, pose, out _);
                if (!IsDetachedCaptureCurrent(detachedVersion, view, model, pose))
                    break;
            }
        }

        public bool CapturePiece(string pieceId, out string error)
        {
            if (!HasLiveView() || string.IsNullOrWhiteSpace(pieceId) ||
                !_bindings.TryGetValue(pieceId, out RuntimeBinding binding) || !IsCurrentBinding(binding))
            {
                error = "A configured live chess view and requested original piece binding are required.";
                return false;
            }
            return CaptureCurrent(binding, out error);
        }

        private void LateUpdate()
        {
            if (!HasLiveView())
            {
                ReleaseInput();
                return;
            }
            int version = _configurationVersion;
            ChessContentView view = _view;
            ChessContentModel model = _model;
            var selected = new List<RuntimeBinding>(_selectedBindings);
            CaptureCurrentBatch(selected, version, view, model, out _);
        }

        private void OnDisable() => ReleaseInput();
        private void OnDestroy() => ReleaseInput();

        private void OnPieceSelected(RuntimeBinding binding)
        {
            if (IsCurrentBinding(binding))
                _selectedBindings.Add(binding);
        }

        private void OnPieceUnselected(RuntimeBinding binding)
        {
            if (!IsCurrentBinding(binding) || !_selectedBindings.Contains(binding))
                return;
            int version = _configurationVersion;
            ChessContentView view = _view;
            ChessContentModel model = _model;
            var selected = new List<RuntimeBinding>(_selectedBindings);
            if (!TrySnapshotCurrentBatch(selected, version, view, model,
                                         out List<CapturedPose> captured, out _))
                return;
            if (!IsCurrentCaptureCurrent(version, view, model, binding))
                return;
            _selectedBindings.Remove(binding);
            CommitCurrentSnapshots(captured, version, view, model, out _);
        }

        private bool HasLiveView()
        {
            return isActiveAndEnabled && _view != null && _view.isActiveAndEnabled &&
                   _view.Model == _model && _model != null && _model.IsLive;
        }

        private bool IsCurrentBinding(RuntimeBinding binding)
        {
            return HasLiveView() && binding != null &&
                   _bindings.TryGetValue(binding.Binding.PieceId, out RuntimeBinding current) &&
                   ReferenceEquals(current, binding);
        }

        private bool CaptureCurrent(RuntimeBinding binding, out string error)
        {
            if (!IsCurrentBinding(binding))
            {
                error = "The selected chess binding is no longer owned by this input.";
                return false;
            }
            int version = _configurationVersion;
            ChessContentView view = _view;
            ChessContentModel model = _model;
            var selected = new List<RuntimeBinding>(_selectedBindings);
            if (!selected.Contains(binding))
                selected.Add(binding);
            return CaptureCurrentBatch(selected, version, view, model, out error);
        }

        private bool CaptureCurrentBatch(IReadOnlyList<RuntimeBinding> selected, int version,
                                         ChessContentView view, ChessContentModel model,
                                         out string error)
        {
            if (!TrySnapshotCurrentBatch(selected, version, view, model,
                                         out List<CapturedPose> captured, out error))
                return false;
            return CommitCurrentSnapshots(captured, version, view, model, out error);
        }

        private bool TrySnapshotCurrentBatch(IReadOnlyList<RuntimeBinding> selected, int version,
                                             ChessContentView view, ChessContentModel model,
                                             out List<CapturedPose> captured, out string error)
        {
            captured = new List<CapturedPose>(selected.Count);
            foreach (RuntimeBinding binding in selected)
            {
                if (!IsCurrentCaptureCurrent(version, view, model, binding))
                {
                    error = "The chess input changed before selected poses could be captured.";
                    return false;
                }
                if (!TrySnapshotPose(view, model, binding, out CapturedPose pose, out error))
                    return false;
                captured.Add(pose);
            }
            error = null;
            return true;
        }

        private bool CommitCurrentSnapshots(IReadOnlyList<CapturedPose> captured, int version,
                                            ChessContentView view, ChessContentModel model,
                                            out string error)
        {
            foreach (CapturedPose pose in captured)
            {
                if (!IsCurrentCaptureCurrent(version, view, model, pose.Binding))
                {
                    error = "The chess input changed before selected poses could be committed.";
                    return false;
                }
                if (!CommitCapturedPose(view, model, pose, out error))
                    return false;
            }
            error = null;
            return true;
        }

        private bool IsCurrentCaptureCurrent(int version, ChessContentView view,
                                             ChessContentModel model, RuntimeBinding binding)
        {
            return _configurationVersion == version && _view == view && _model == model &&
                   IsCurrentBinding(binding);
        }

        private bool IsDetachedCaptureCurrent(int detachedVersion, ChessContentView view,
                                              ChessContentModel model, CapturedPose pose)
        {
            return _configurationVersion == detachedVersion && _view == null && _model == null &&
                   _bindings.Count == 0 && _selectedBindings.Count == 0 && view != null &&
                   model != null && model.IsLive && view.isActiveAndEnabled && view.Model == model &&
                   pose.Binding != null;
        }

        private static bool TrySnapshotAll(ChessContentView view, ChessContentModel model,
                                           IReadOnlyList<RuntimeBinding> selected,
                                           List<CapturedPose> captured, out string error)
        {
            foreach (RuntimeBinding binding in selected)
            {
                if (!TrySnapshotPose(view, model, binding, out CapturedPose pose, out error))
                    return false;
                captured.Add(pose);
            }
            error = null;
            return true;
        }

        private static bool TrySnapshotPose(ChessContentView view, ChessContentModel model,
                                            RuntimeBinding binding, out CapturedPose pose,
                                            out string error)
        {
            pose = default;
            if (view == null || !view.isActiveAndEnabled || model == null || !model.IsLive ||
                view.Model != model || binding == null || binding.Binding.Piece == null ||
                !view.TryGetPieceTransform(binding.Binding.PieceId, out Transform piece) ||
                piece != binding.Binding.Piece || view.BoardTransform == null)
            {
                error = "The chess view/model changed before the selected piece pose could be captured.";
                return false;
            }
            Transform board = view.BoardTransform;
            pose = new CapturedPose(binding, board.InverseTransformPoint(piece.position),
                                    Quaternion.Inverse(board.rotation) * piece.rotation);
            error = null;
            return true;
        }

        private static bool CommitCapturedPose(ChessContentView view, ChessContentModel model,
                                               CapturedPose pose, out string error)
        {
            if (view == null || !view.isActiveAndEnabled || model == null || !model.IsLive ||
                view.Model != model || pose.Binding == null)
            {
                error = "The chess view/model changed before the selected pose could be committed.";
                return false;
            }
            return model.TrySetPiecePose(pose.Binding.Binding.PieceId, pose.BoardLocalPosition,
                                         pose.BoardLocalRotation, out error);
        }

        private static void Unsubscribe(RuntimeBinding binding)
        {
            if (binding.Binding.Interaction == null)
                return;
            binding.Binding.Interaction.WhenSelect.RemoveListener(binding.Select);
            binding.Binding.Interaction.WhenUnselect.RemoveListener(binding.Unselect);
        }

        private static bool TryBuildBindings(ChessContentView view,
                                             IReadOnlyList<FreeformSceneChessPieceBinding> bindings,
                                             out Dictionary<string, RuntimeBinding> result,
                                             out string error)
        {
            result = null;
            if (view == null || view.Model == null || !view.Model.IsLive || bindings == null)
            {
                error = "A bound live chess view and original piece bindings are required.";
                return false;
            }

            var candidate = new Dictionary<string, RuntimeBinding>(StringComparer.Ordinal);
            var pieces = new List<Transform>();
            var interactions = new HashSet<InteractableUnityEventWrapper>();
            foreach (FreeformSceneChessPieceBinding binding in bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.PieceId) || binding.Piece == null ||
                    binding.Interaction == null || binding.Interaction.transform != binding.Piece ||
                    !view.TryGetPieceTransform(binding.PieceId, out Transform configuredPiece) ||
                    configuredPiece != binding.Piece || !candidate.TryAdd(binding.PieceId,
                        new RuntimeBinding(binding)) || !interactions.Add(binding.Interaction))
                {
                    error = "Chess input bindings require unique live IDs, original wrappers, and configured piece transforms.";
                    return false;
                }
                foreach (Transform existing in pieces)
                {
                    if (binding.Piece == existing || binding.Piece.IsChildOf(existing) ||
                        existing.IsChildOf(binding.Piece))
                    {
                        error = "Chess input bindings may not use aliased or nested piece transforms.";
                        return false;
                    }
                }
                pieces.Add(binding.Piece);
            }

            foreach (ChessPiecePose pose in view.Model.CopyPieces())
            {
                if (!candidate.ContainsKey(pose.PieceId))
                {
                    error = "Chess input bindings must include every piece in the bound model.";
                    return false;
                }
            }
            result = candidate;
            error = null;
            return true;
        }
    }
}
