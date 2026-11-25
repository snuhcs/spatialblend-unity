// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    /// <summary>
    /// An in-process catalog for typed local content. It deliberately has no room,
    /// peer, authority, or transport concepts; callers bind one model to any number
    /// of ordinary Unity views in this process.
    /// </summary>
    public sealed class FreeformContentCatalog : IDisposable
    {
        private sealed class Entry
        {
            internal Entry(FreeformContentModel model, FreeformContentHost host)
            {
                Model = model;
                Host = host;
            }

            internal FreeformContentModel Model { get; }
            internal FreeformContentHost Host { get; }
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private bool _disposed;

        public int Count => _entries.Count;

        public bool TryCreateWhiteboard(SpatialBlendObject owner, string contentId,
                                        WhiteboardContentConfiguration configuration,
                                        out WhiteboardContentModel model, out string error)
        {
            model = null;
            if (!TryReserve(owner, contentId, out FreeformContentHost host, out error))
                return false;
            if (configuration.Width <= 0 || configuration.Height <= 0 ||
                configuration.Width > WhiteboardContentModel.MaximumDimension ||
                configuration.Height > WhiteboardContentModel.MaximumDimension ||
                (long)configuration.Width * configuration.Height > 16777216)
            {
                error = "Whiteboard dimensions must be positive and within the supported raster size.";
                return false;
            }
            model = new WhiteboardContentModel(contentId, owner, configuration);
            Commit(contentId, model, host);
            error = null;
            return true;
        }

        public bool TryCreateChess(SpatialBlendObject owner, string contentId,
                                   IReadOnlyList<ChessPiecePose> initialPieces,
                                   out ChessContentModel model, out string error)
        {
            model = null;
            if (!TryReserve(owner, contentId, out FreeformContentHost host, out error))
                return false;
            if (!TryValidateInitialPieces(initialPieces, out error))
                return false;
            model = new ChessContentModel(contentId, owner, initialPieces);
            Commit(contentId, model, host);
            error = null;
            return true;
        }

        public bool TryGetWhiteboard(string contentId, out WhiteboardContentModel model)
        {
            model = null;
            return TryGet(contentId, out FreeformContentModel value) &&
                   (model = value as WhiteboardContentModel) != null;
        }

        public bool TryGetChess(string contentId, out ChessContentModel model)
        {
            model = null;
            return TryGet(contentId, out FreeformContentModel value) &&
                   (model = value as ChessContentModel) != null;
        }

        public bool TryGet(string contentId, out FreeformContentModel model)
        {
            model = null;
            if (_disposed || !FreeformValidation.ValidContentId(contentId) ||
                !_entries.TryGetValue(contentId, out Entry entry))
                return false;
            model = entry.Model;
            return true;
        }

        public bool Remove(string contentId)
        {
            if (_disposed || !FreeformValidation.ValidContentId(contentId) ||
                !_entries.TryGetValue(contentId, out Entry entry))
                return false;
            _entries.Remove(contentId);
            DetachAndRetire(entry, contentId);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            var entries = new List<Entry>(_entries.Values);
            _entries.Clear();
            foreach (Entry entry in entries)
                DetachAndRetire(entry, entry.Model.ContentId);
        }

        internal void RemoveFromHost(FreeformContentHost host, string contentId)
        {
            if (_disposed || host == null || !FreeformValidation.ValidContentId(contentId) ||
                !_entries.TryGetValue(contentId, out Entry entry) || entry.Host != host)
                return;
            _entries.Remove(contentId);
            DetachAndRetire(entry, contentId);
        }

        private bool TryReserve(SpatialBlendObject owner, string contentId,
                                out FreeformContentHost host, out string error)
        {
            host = null;
            if (_disposed)
            {
                error = "This content catalog has been disposed.";
                return false;
            }
            if (owner == null || owner.IsDestroyed || !FreeformValidation.ValidContentId(contentId))
            {
                error = "A live SpatialBlendObject and a nonempty content ID are required.";
                return false;
            }
            if (_entries.ContainsKey(contentId))
            {
                error = "A content item with this ID already exists in this catalog.";
                return false;
            }
            host = owner.GetComponent<FreeformContentHost>();
            if (host != null && (host.IsAttached || host.IsDestroying))
            {
                error = "An SpatialBlendObject may host only one local content item.";
                return false;
            }
            error = null;
            return true;
        }

        private void Commit(string contentId, FreeformContentModel model, FreeformContentHost host)
        {
            if (host == null)
                host = model.Owner.gameObject.AddComponent<FreeformContentHost>();
            host.Attach(this, contentId, model.Owner);
            _entries.Add(contentId, new Entry(model, host));
        }

        private void DetachAndRetire(Entry entry, string contentId)
        {
            try
            {
                entry.Host.Detach(this, contentId);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                entry.Model.Retire();
            }
        }

        private static bool TryValidateInitialPieces(IReadOnlyList<ChessPiecePose> pieces,
                                                     out string error)
        {
            if (pieces == null || pieces.Count > 128)
            {
                error = "Chess requires from zero to 128 configured pieces.";
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChessPiecePose piece in pieces)
            {
                if (!FreeformValidation.ValidPiece(piece.PieceId) ||
                    !FreeformValidation.ValidPose(piece.BoardLocalPosition,
                                                  piece.BoardLocalRotation) || !seen.Add(piece.PieceId))
                {
                    error = "Chess piece IDs must be unique and poses must be finite and nonzero.";
                    return false;
                }
            }
            error = null;
            return true;
        }
    }

}
