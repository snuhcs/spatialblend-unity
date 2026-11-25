// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    [Serializable]
    public struct ChessPieceViewBinding
    {
        [SerializeField] private string _pieceId;
        [SerializeField] private Transform _piece;

        public ChessPieceViewBinding(string pieceId, Transform piece)
        {
            _pieceId = pieceId;
            _piece = piece;
        }

        public string PieceId => _pieceId;
        public Transform Piece => _piece;
    }

    /// <summary>
    /// Projects model board-local chess poses into concrete piece transforms below
    /// this view's board transform. It permits arbitrary moves; chess rules are not
    /// part of Freeform content state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChessContentView : MonoBehaviour
    {
        [SerializeField] private Transform _boardTransform;
        [SerializeField] private ChessPieceViewBinding[] _pieceBindings = Array.Empty<ChessPieceViewBinding>();

        private readonly Dictionary<string, Transform> _pieces =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        private ChessContentModel _model;

        public ChessContentModel Model => _model;
        public Transform BoardTransform => _boardTransform == null ? transform : _boardTransform;

        public bool TryGetPieceTransform(string pieceId, out Transform piece)
        {
            piece = null;
            return FreeformValidation.ValidPiece(pieceId) && _pieces.TryGetValue(pieceId, out piece) &&
                   piece != null;
        }

        public bool Configure(Transform boardTransform, IReadOnlyList<ChessPieceViewBinding> bindings,
                              out string error)
        {
            if (_model != null)
            {
                error = "Unbind before changing a live chess view configuration.";
                return false;
            }
            if (!TryBuildBindings(boardTransform == null ? transform : boardTransform, bindings,
                                  out Dictionary<string, Transform> candidate, out error))
                return false;
            _boardTransform = boardTransform;
            _pieceBindings = new ChessPieceViewBinding[bindings.Count];
            for (int index = 0; index < bindings.Count; ++index)
                _pieceBindings[index] = bindings[index];
            _pieces.Clear();
            foreach (var pair in candidate)
                _pieces.Add(pair.Key, pair.Value);
            return true;
        }

        public bool TryBind(ChessContentModel model, out string error)
        {
            if (model == null || !model.IsLive)
            {
                error = "A live chess model is required.";
                return false;
            }
            if (_model == model)
            {
                error = null;
                return true;
            }
            if (!TryBuildBindings(BoardTransform, _pieceBindings,
                                  out Dictionary<string, Transform> candidate, out error))
                return false;
            foreach (ChessPiecePose pose in model.CopyPieces())
                if (!candidate.ContainsKey(pose.PieceId))
                {
                    error = "The chess view does not contain every model piece ID.";
                    return false;
                }

            // All validation happens before touching model subscriptions or transforms.
            ChessContentModel previous = _model;
            if (previous != null)
            {
                previous.Changed -= OnModelChanged;
                previous.Retired -= OnModelRetired;
            }
            _pieces.Clear();
            foreach (var pair in candidate)
                _pieces.Add(pair.Key, pair.Value);
            _model = model;
            _model.Changed += OnModelChanged;
            _model.Retired += OnModelRetired;
            ApplyAll();
            error = null;
            return true;
        }

        public void Unbind()
        {
            if (_model != null)
            {
                _model.Changed -= OnModelChanged;
                _model.Retired -= OnModelRetired;
            }
            _model = null;
        }

        public bool TryCapturePiece(string pieceId, out string error)
        {
            if (_model == null || !FreeformValidation.ValidPiece(pieceId) ||
                !_pieces.TryGetValue(pieceId, out Transform piece) || piece == null)
            {
                error = "Bind a model and configure the requested chess piece before capture.";
                return false;
            }
            Transform board = BoardTransform;
            if (!FreeformValidation.ValidTransform(board))
            {
                error = "The chess board transform is no longer finite and nonsingular.";
                return false;
            }
            Vector3 localPosition = board.InverseTransformPoint(piece.position);
            Quaternion localRotation = Quaternion.Inverse(board.rotation) * piece.rotation;
            return _model.TrySetPiecePose(pieceId, localPosition, localRotation, out error);
        }

        private void OnDisable() => Unbind();
        private void OnDestroy() => Unbind();

        private void OnModelChanged(FreeformContentModel changed)
        {
            if (changed == _model)
                ApplyAll();
        }

        private void OnModelRetired(FreeformContentModel retired)
        {
            if (retired == _model)
                Unbind();
        }

        private void ApplyAll()
        {
            if (_model == null)
                return;
            Transform board = BoardTransform;
            if (!FreeformValidation.ValidTransform(board))
                return;
            foreach (ChessPiecePose pose in _model.CopyPieces())
            {
                if (!_pieces.TryGetValue(pose.PieceId, out Transform piece) || piece == null)
                    continue;
                piece.SetPositionAndRotation(board.TransformPoint(pose.BoardLocalPosition),
                                             board.rotation * pose.BoardLocalRotation);
            }
        }

        private static bool TryBuildBindings(Transform board, IReadOnlyList<ChessPieceViewBinding> bindings,
                                             out Dictionary<string, Transform> result, out string error)
        {
            result = null;
            if (!FreeformValidation.ValidTransform(board) || bindings == null)
            {
                error = "A finite, nonsingular board transform and piece bindings are required.";
                return false;
            }
            var candidate = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var targets = new List<Transform>();
            foreach (ChessPieceViewBinding binding in bindings)
            {
                if (!FreeformValidation.ValidPiece(binding.PieceId) || binding.Piece == null ||
                    binding.Piece == board || board.IsChildOf(binding.Piece) ||
                    !candidate.TryAdd(binding.PieceId, binding.Piece))
                {
                    error = "Chess bindings require unique IDs and non-aliasing live piece transforms.";
                    return false;
                }
                foreach (Transform target in targets)
                    if (binding.Piece == target || binding.Piece.IsChildOf(target) ||
                        target.IsChildOf(binding.Piece))
                    {
                        error = "Chess bindings may not use nested or aliased piece transforms.";
                        return false;
                    }
                targets.Add(binding.Piece);
            }
            result = candidate;
            error = null;
            return true;
        }
    }
}
