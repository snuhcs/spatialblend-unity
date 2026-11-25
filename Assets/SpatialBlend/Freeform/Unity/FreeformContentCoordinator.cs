// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    /// <summary>
    /// Scene-facing create/remove and bind/unbind commands for one local catalog.
    /// This is intentionally an in-process content coordinator, not replication.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformContentCoordinator : MonoBehaviour
    {
        private FreeformContentCatalog _catalog;

        public FreeformContentCatalog Catalog => _catalog ??= new FreeformContentCatalog();

        public bool TryCreateWhiteboard(SpatialBlendObject owner, string contentId,
                                        WhiteboardContentConfiguration configuration,
                                        out WhiteboardContentModel model, out string error) =>
            Catalog.TryCreateWhiteboard(owner, contentId, configuration, out model, out error);

        public bool TryCreateChess(SpatialBlendObject owner, string contentId,
                                   IReadOnlyList<ChessPiecePose> initialPieces,
                                   out ChessContentModel model, out string error) =>
            Catalog.TryCreateChess(owner, contentId, initialPieces, out model, out error);

        public bool Remove(string contentId) => Catalog.Remove(contentId);

        public bool TryBindWhiteboard(string contentId, WhiteboardContentView view, out string error)
        {
            if (view == null || !Catalog.TryGetWhiteboard(contentId, out WhiteboardContentModel model))
            {
                error = "A live whiteboard view and matching whiteboard content are required.";
                return false;
            }
            return view.TryBind(model, out error);
        }

        public bool TryBindChess(string contentId, ChessContentView view, out string error)
        {
            if (view == null || !Catalog.TryGetChess(contentId, out ChessContentModel model))
            {
                error = "A live chess view and matching chess content are required.";
                return false;
            }
            return view.TryBind(model, out error);
        }

        private void OnDestroy()
        {
            _catalog?.Dispose();
            _catalog = null;
        }
    }
}
