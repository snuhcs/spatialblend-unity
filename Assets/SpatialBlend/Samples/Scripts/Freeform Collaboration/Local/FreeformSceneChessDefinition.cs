// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Oculus.Interaction;
using Meta.XR.Samples;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>One stable, authored chess identity. Do not derive IDs from child ordering.</summary>
    [Serializable]
    public sealed class FreeformSceneChessPieceReference
    {
        [SerializeField] private string _pieceId;
        [SerializeField] private Transform _piece;
        [SerializeField] private InteractableUnityEventWrapper _interaction;

        public string PieceId => _pieceId;
        public Transform Piece => _piece;
        public InteractableUnityEventWrapper Interaction => _interaction;
    }

    /// <summary>Serialized, instance-resolving bindings for the authored chess prefab.</summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneChessDefinition : MonoBehaviour
    {
        [SerializeField] private Transform _movableRoot;
        [SerializeField] private Transform _board;
        [SerializeField] private FreeformSceneChessPieceReference[] _pieces =
            Array.Empty<FreeformSceneChessPieceReference>();
        [SerializeField] private Behaviour[] _legacyContentBehaviours = Array.Empty<Behaviour>();

        public Transform MovableRoot => _movableRoot;
        public Transform Board => _board;
        public FreeformSceneChessPieceReference[] Pieces => _pieces;
        public Behaviour[] LegacyContentBehaviours => _legacyContentBehaviours;
    }
}
