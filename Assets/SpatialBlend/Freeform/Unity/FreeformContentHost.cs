// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Freeform
{
    /// <summary>Lifecycle bridge attached only to the SpatialBlendObject that owns content.</summary>
    [DisallowMultipleComponent]
    public sealed class FreeformContentHost : MonoBehaviour
    {
        private FreeformContentCatalog _catalog;
        private string _contentId;
        private SpatialBlendObject _owner;
        private bool _destroying;

        public string ContentId => _contentId;
        internal bool IsAttached => _catalog != null;
        internal bool IsDestroying => _destroying;

        internal void Attach(FreeformContentCatalog catalog, string contentId, SpatialBlendObject owner)
        {
            _catalog = catalog;
            _contentId = contentId;
            _owner = owner;
            if (_owner != null)
                _owner.Destroyed += OnOwnerDestroyed;
        }

        internal void Detach(FreeformContentCatalog catalog, string contentId)
        {
            if (_catalog == catalog && _contentId == contentId)
            {
                if (_owner != null)
                    _owner.Destroyed -= OnOwnerDestroyed;
                _catalog = null;
                _contentId = null;
                _owner = null;
            }
        }

        private void OnOwnerDestroyed(SpatialBlendObject owner)
        {
            if (owner == _owner)
                _catalog?.RemoveFromHost(this, _contentId);
        }

        private void OnDestroy()
        {
            _destroying = true;
            FreeformContentCatalog catalog = _catalog;
            string contentId = _contentId;
            catalog?.RemoveFromHost(this, contentId);
            Detach(catalog, contentId);
        }
    }
}
