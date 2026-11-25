// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Optional scene lifetime wrapper for the transport-neutral room client.
    /// A bootstrapper supplies the selected native room-client adapter; this
    /// component never selects a transport implementation and does not share
    /// engine-object state.  Inert is the safe default when no
    /// bootstrapper/configuration is supplied.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialBlendSession : MonoBehaviour
    {
        private SpatialBlendRoomClient _client;
        private bool _destroyed;

        public SpatialBlendRoomClient Client => _client;
        public RoomSessionPhase Phase => _client == null ? RoomSessionPhase.Inert : _client.Phase;
        public string LastError => _client == null ? null : _client.LastError;

        /// <summary>Called by an explicit app/bootstrap composition root.  A
        /// replacement is rejected rather than closing a live session owned by
        /// another binding or a domain-reload replacement.</summary>
        public bool TryInitialize(RoomClientConfiguration configuration, IRoomClientTransport transport,
                                  out string error)
        {
            if (_destroyed)
            {
                error = "Session component is destroyed.";
                return false;
            }
            if (_client != null)
            {
                error = "Session component is already initialized.";
                return false;
            }
            if (configuration == null || transport == null)
            {
                error = "Session configuration and transport are required.";
                return false;
            }
            _client = new SpatialBlendRoomClient(configuration, transport);
            error = null;
            return true;
        }

        public bool TryHost(RoomEnvelopeBytes envelope, out string error)
        {
            return TryClient(out error) && _client.TryHost(envelope, out error);
        }

        public bool TryJoin(string roomId, RoomEnvelopeBytes envelope, out string error)
        {
            return TryClient(out error) && _client.TryJoin(roomId, envelope, out error);
        }

        public bool TryLeave(RoomEnvelopeBytes envelope, out string error)
        {
            return TryClient(out error) && _client.TryLeave(envelope, out error);
        }

        public bool TryReconnect(RoomEnvelopeBytes envelope, out string error)
        {
            return TryClient(out error) && _client.TryReconnect(envelope, out error);
        }

        private bool TryClient(out string error)
        {
            if (_destroyed || _client == null)
            {
                error = "Session is inert because no room-client transport is bound.";
                return false;
            }
            error = null;
            return true;
        }

        private void Update()
        {
            _client?.Pump();
        }

        private void OnDestroy()
        {
            _destroyed = true;
            _client?.Dispose();
            _client = null;
        }
    }
}
