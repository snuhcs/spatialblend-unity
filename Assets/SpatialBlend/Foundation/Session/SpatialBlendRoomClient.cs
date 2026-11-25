// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>
    /// The room protocol boundary consumed by Foundation.  It owns a frozen
    /// byte-for-byte N0 envelope, rather than an engine-object serialization
    /// model.  Native local handles and receiver-local support/binding
    /// generations cannot be represented here.
    /// </summary>
    public sealed class RoomEnvelopeBytes
    {
        private readonly byte[] _bytes;

        public RoomEnvelopeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("A room envelope must be nonempty.", nameof(bytes));
            _bytes = Copy(bytes);
        }

        public int Length => _bytes.Length;
        public byte[] CopyBytes() => Copy(_bytes);

        internal static byte[] Copy(byte[] source)
        {
            if (source == null)
                return null;
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }

    public enum RoomSessionPhase
    {
        Inert,
        Hosting,
        Joining,
        Joined,
        Leaving,
        Recovering,
        Expired,
        Failed,
        Disposed
    }

    public enum RoomTransportCommand
    {
        Host,
        Join,
        Leave,
        Reconnect
    }

    public enum RoomTransportEventKind
    {
        MembershipAccepted,
        MembershipLeft,
        AuthorityChanged,
        TransportLost,
        CredentialExpired,
        Envelope,
        Failed
    }

    public enum RoomSessionErrorCode
    {
        None,
        InvalidConfiguration,
        InvalidState,
        BeginRejected,
        TransportFailure,
        CrossRoomEvent,
        StaleEvent,
        InvalidMembership,
        Disposed
    }

    /// <summary>Durable room identity returned by the native/server boundary.
    /// These strings are never local Unity/native instance identities.</summary>
    public sealed class RoomMembershipFence
    {
        public RoomMembershipFence(string roomId, ulong roomEpoch, string participantId,
                                   ulong connectionEpoch, string grantId, ulong authorityEpoch)
        {
            RoomId = roomId;
            RoomEpoch = roomEpoch;
            ParticipantId = participantId;
            ConnectionEpoch = connectionEpoch;
            GrantId = grantId;
            AuthorityEpoch = authorityEpoch;
        }

        public string RoomId { get; }
        public ulong RoomEpoch { get; }
        public string ParticipantId { get; }
        public ulong ConnectionEpoch { get; }
        public string GrantId { get; }
        public ulong AuthorityEpoch { get; }

        public bool IsStructurallyValid => IsId(RoomId) && IsId(ParticipantId) && IsId(GrantId) &&
                                           RoomEpoch != 0 && ConnectionEpoch != 0 && AuthorityEpoch != 0;

        internal bool SameRoom(RoomMembershipFence other) => other != null &&
            RoomId == other.RoomId && RoomEpoch == other.RoomEpoch;

        internal bool ExactlyEquals(RoomMembershipFence other) => SameRoom(other) &&
            ParticipantId == other.ParticipantId && ConnectionEpoch == other.ConnectionEpoch &&
            GrantId == other.GrantId && AuthorityEpoch == other.AuthorityEpoch;

        internal static bool IsId(string value) => !string.IsNullOrEmpty(value) &&
                                                   value.Length <= 96 && value.IndexOf('\0') < 0;
    }

    /// <summary>Configuration intentionally does not select a protocol/client
    /// implementation.  Auth material remains an opaque,
    /// per-attempt lease owned by the provider/transport.</summary>
    public sealed class RoomClientConfiguration
    {
        public RoomClientConfiguration(string endpoint, ushort protocolMajor, ushort protocolMinor,
                                       IRoomAuthenticationProvider authentication)
        {
            Endpoint = endpoint;
            ProtocolMajor = protocolMajor;
            ProtocolMinor = protocolMinor;
            Authentication = authentication;
        }

        public string Endpoint { get; }
        public ushort ProtocolMajor { get; }
        public ushort ProtocolMinor { get; }
        public IRoomAuthenticationProvider Authentication { get; }

        internal bool IsValid => Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri endpoint) &&
                                 endpoint.Scheme == Uri.UriSchemeHttps && ProtocolMajor == 1 &&
                                 Authentication != null;
    }

    public interface IRoomAuthenticationLease : IDisposable { }

    public interface IRoomAuthenticationProvider
    {
        bool TryAcquire(RoomTransportCommand command, string expectedRoomId,
                        out IRoomAuthenticationLease lease, out string error);
    }

    public sealed class RoomTransportRequest
    {
        internal RoomTransportRequest(RoomTransportCommand command, ulong operationId,
                                      RoomClientConfiguration configuration, string expectedRoomId,
                                      RoomEnvelopeBytes envelope, IRoomAuthenticationLease authentication)
        {
            Command = command;
            OperationId = operationId;
            Configuration = configuration;
            ExpectedRoomId = expectedRoomId;
            Envelope = envelope;
            Authentication = authentication;
        }

        public RoomTransportCommand Command { get; }
        public ulong OperationId { get; }
        public RoomClientConfiguration Configuration { get; }
        public string ExpectedRoomId { get; }
        public RoomEnvelopeBytes Envelope { get; }
        public IRoomAuthenticationLease Authentication { get; }
    }

    public sealed class RoomTransportEvent
    {
        public RoomTransportEvent(RoomTransportEventKind kind, ulong operationId,
                                  RoomMembershipFence membership, RoomEnvelopeBytes envelope,
                                  string error)
        {
            Kind = kind;
            OperationId = operationId;
            Membership = membership;
            Envelope = envelope;
            Error = error;
        }

        public RoomTransportEventKind Kind { get; }
        public ulong OperationId { get; }
        public RoomMembershipFence Membership { get; }
        public RoomEnvelopeBytes Envelope { get; }
        public string Error { get; }
    }

    public interface IRoomTransportOperation : IDisposable
    {
        void Cancel();
    }

    public interface IRoomClientTransport
    {
        /// <summary>May invoke <paramref name="onEvent"/> from an arbitrary
        /// transport thread.  It must echo request.OperationId exactly.</summary>
        bool TryBegin(RoomTransportRequest request, Action<RoomTransportEvent> onEvent,
                      out IRoomTransportOperation operation, out string error);
    }

    /// <summary>
    /// Main-thread room client state.  Transport callbacks only copy into a
    /// bounded queue; callers explicitly call Pump on the owner thread.  This
    /// type deliberately has no SharedToLocalBinding member: receiver-local
    /// object/counterpart/support/calibration activation belongs to U1 and
    /// must not be created by room reconnect or a replicated object revision.
    /// </summary>
    public sealed class SpatialBlendRoomClient : IDisposable
    {
        private const int MaxPendingEvents = 128;
        private readonly IRoomClientTransport _transport;
        private readonly RoomClientConfiguration _configuration;
        private readonly int _ownerThread;
        private readonly object _queueLock = new object();
        private readonly Queue<RoomTransportEvent> _pending = new Queue<RoomTransportEvent>();
        private bool _queueOverflowed;
        private IRoomTransportOperation _operation;
        private RoomTransportCommand _activeCommand;
        private RoomMembershipFence _membership;
        private string _expectedRoomId;
        private ulong _nextOperationId;
        private ulong _activeOperationId;
        private bool _disposed;

        public SpatialBlendRoomClient(RoomClientConfiguration configuration, IRoomClientTransport transport)
        {
            _configuration = configuration;
            _transport = transport;
            _ownerThread = Thread.CurrentThread.ManagedThreadId;
            Phase = RoomSessionPhase.Inert;
        }

        public RoomSessionPhase Phase { get; private set; }
        public RoomMembershipFence Membership => _membership;
        public RoomSessionErrorCode LastErrorCode { get; private set; }
        public string LastError { get; private set; }
        public ulong ClientGeneration { get; private set; }
        public event Action<RoomEnvelopeBytes, RoomMembershipFence> EnvelopeReceived;
        public event Action<RoomSessionPhase> PhaseChanged;

        public bool TryHost(RoomEnvelopeBytes envelope, out string error) =>
            TryBegin(RoomTransportCommand.Host, null, envelope, out error);

        public bool TryJoin(string expectedRoomId, RoomEnvelopeBytes envelope, out string error) =>
            TryBegin(RoomTransportCommand.Join, expectedRoomId, envelope, out error);

        public bool TryLeave(RoomEnvelopeBytes envelope, out string error)
        {
            RequireOwnerThread();
            if (Phase != RoomSessionPhase.Joined || _membership == null)
                return Fail(RoomSessionErrorCode.InvalidState, "Leave requires an active server membership.", out error);
            return TryBegin(RoomTransportCommand.Leave, _membership.RoomId, envelope, out error);
        }

        public bool TryReconnect(RoomEnvelopeBytes envelope, out string error)
        {
            RequireOwnerThread();
            if (Phase != RoomSessionPhase.Recovering || !RoomMembershipFence.IsId(_expectedRoomId))
                return Fail(RoomSessionErrorCode.InvalidState, "Reconnect requires a prior lost room membership.", out error);
            return TryBegin(RoomTransportCommand.Reconnect, _expectedRoomId, envelope, out error);
        }

        private bool TryBegin(RoomTransportCommand command, string expectedRoomId,
                              RoomEnvelopeBytes envelope, out string error)
        {
            RequireOwnerThread();
            if (_disposed)
                return Fail(RoomSessionErrorCode.Disposed, "Room client is disposed.", out error);
            if (_transport == null || _configuration == null || !_configuration.IsValid || envelope == null ||
                ((command == RoomTransportCommand.Join || command == RoomTransportCommand.Reconnect) &&
                 !RoomMembershipFence.IsId(expectedRoomId)))
                return Fail(RoomSessionErrorCode.InvalidConfiguration, "Room transport/configuration/request is invalid.", out error);
            if (!CanBegin(command))
                return Fail(RoomSessionErrorCode.InvalidState, "Room command is not legal in the current phase.", out error);

            if (!_configuration.Authentication.TryAcquire(command, expectedRoomId, out IRoomAuthenticationLease lease,
                                                          out string authError) || lease == null)
                return Fail(RoomSessionErrorCode.BeginRejected, string.IsNullOrEmpty(authError) ? "Room authentication was rejected." : authError, out error);
            ulong id = NextOperationId();
            IRoomTransportOperation next = null;
            try
            {
                var request = new RoomTransportRequest(command, id, _configuration, expectedRoomId, envelope, lease);
                if (!_transport.TryBegin(request, QueueFromTransport, out next, out string transportError) || next == null)
                    return Fail(RoomSessionErrorCode.BeginRejected, string.IsNullOrEmpty(transportError) ? "Room transport rejected command." : transportError, out error);
            }
            finally
            {
                // Credential values never enter state, events, errors, or logs.
                lease.Dispose();
            }

            RetireActiveOperation();
            _operation = next;
            _activeCommand = command;
            _activeOperationId = id;
            _expectedRoomId = expectedRoomId;
            if (command == RoomTransportCommand.Host) SetPhase(RoomSessionPhase.Hosting);
            else if (command == RoomTransportCommand.Leave) SetPhase(RoomSessionPhase.Leaving);
            else SetPhase(RoomSessionPhase.Joining);
            LastErrorCode = RoomSessionErrorCode.None;
            LastError = null;
            error = null;
            return true;
        }

        public void Pump()
        {
            RequireOwnerThread();
            if (_disposed)
                return;
            while (true)
            {
                RoomTransportEvent value;
                lock (_queueLock)
                {
                    if (_queueOverflowed)
                    {
                        _queueOverflowed = false;
                        _pending.Clear();
                        value = null;
                    }
                    else
                    {
                        if (_pending.Count == 0)
                            return;
                        value = _pending.Dequeue();
                    }
                }
                if (value == null)
                {
                    // Dropping a callback can hide a leave/revocation or a
                    // source reset.  A bounded queue therefore fails closed
                    // rather than coalescing room-control or spatial bytes.
                    Record(RoomSessionErrorCode.TransportFailure, "Room transport callback queue overflowed.");
                    RetireActiveOperation();
                    _membership = null;
                    SetPhase(RoomSessionPhase.Failed);
                    return;
                }
                ApplyEvent(value);
            }
        }

        private void QueueFromTransport(RoomTransportEvent value)
        {
            if (value == null)
                return;
            // A foreign thread owns none of Unity/Foundation state.  Copy the
            // raw bytes immediately so a native/transport buffer can be freed.
            RoomEnvelopeBytes copy = value.Envelope == null ? null : new RoomEnvelopeBytes(value.Envelope.CopyBytes());
            var copied = new RoomTransportEvent(value.Kind, value.OperationId, value.Membership, copy, value.Error);
            lock (_queueLock)
            {
                if (_disposed)
                    return;
                if (_pending.Count >= MaxPendingEvents)
                {
                    _queueOverflowed = true;
                    return;
                }
                _pending.Enqueue(copied);
            }
        }

        private void ApplyEvent(RoomTransportEvent value)
        {
            if (value == null || value.OperationId == 0 || value.OperationId != _activeOperationId)
            {
                Record(RoomSessionErrorCode.StaleEvent, "Stale room transport callback was ignored.");
                return;
            }
            switch (value.Kind)
            {
                case RoomTransportEventKind.MembershipAccepted:
                    ApplyAccepted(value);
                    break;
                case RoomTransportEventKind.MembershipLeft:
                    if (!MatchesCurrentMembership(value.Membership)) { RejectStale(); break; }
                    _membership = null; _expectedRoomId = null; RetireActiveOperation(); SetPhase(RoomSessionPhase.Inert);
                    break;
                case RoomTransportEventKind.AuthorityChanged:
                    ApplyAuthority(value);
                    break;
                case RoomTransportEventKind.TransportLost:
                    if (!MatchesCurrentMembership(value.Membership)) { RejectStale(); break; }
                    // Retain only the durable room target for an explicit resume;
                    // membership/authority are not usable while disconnected.
                    _expectedRoomId = _membership.RoomId; _membership = null; RetireActiveOperation(); SetPhase(RoomSessionPhase.Recovering);
                    break;
                case RoomTransportEventKind.CredentialExpired:
                    if (!MatchesCurrentMembership(value.Membership)) { RejectStale(); break; }
                    _membership = null; RetireActiveOperation(); SetPhase(RoomSessionPhase.Expired);
                    break;
                case RoomTransportEventKind.Envelope:
                    if (!MatchesCurrentMembership(value.Membership) || value.Envelope == null) { RejectStale(); break; }
                    Action<RoomEnvelopeBytes, RoomMembershipFence> handlers = EnvelopeReceived;
                    if (handlers != null)
                        handlers(new RoomEnvelopeBytes(value.Envelope.CopyBytes()), _membership);
                    break;
                case RoomTransportEventKind.Failed:
                    Record(RoomSessionErrorCode.TransportFailure, value.Error); RetireActiveOperation(); _membership = null; SetPhase(RoomSessionPhase.Failed);
                    break;
                default:
                    Record(RoomSessionErrorCode.StaleEvent, "Unknown room transport event was ignored.");
                    break;
            }
        }

        private void ApplyAccepted(RoomTransportEvent value)
        {
            if (Phase != RoomSessionPhase.Hosting && Phase != RoomSessionPhase.Joining)
            {
                RejectStale();
                return;
            }
            if (value.Membership == null || !value.Membership.IsStructurallyValid)
            {
                Record(RoomSessionErrorCode.InvalidMembership, "Room membership event is structurally invalid.");
                return;
            }
            if (RoomMembershipFence.IsId(_expectedRoomId) && value.Membership.RoomId != _expectedRoomId)
            {
                Record(RoomSessionErrorCode.CrossRoomEvent, "Room transport returned a different room.");
                return;
            }
            _membership = value.Membership;
            _expectedRoomId = value.Membership.RoomId;
            SetPhase(RoomSessionPhase.Joined);
        }

        private void ApplyAuthority(RoomTransportEvent value)
        {
            if (Phase != RoomSessionPhase.Joined || _membership == null || value.Membership == null || !_membership.SameRoom(value.Membership) ||
                _membership.ParticipantId != value.Membership.ParticipantId ||
                _membership.ConnectionEpoch != value.Membership.ConnectionEpoch ||
                value.Membership.AuthorityEpoch <= _membership.AuthorityEpoch || !value.Membership.IsStructurallyValid)
            {
                RejectStale();
                return;
            }
            _membership = value.Membership;
        }

        private bool MatchesCurrentMembership(RoomMembershipFence candidate) =>
            _membership != null && _membership.ExactlyEquals(candidate);

        private void RejectStale() => Record(RoomSessionErrorCode.StaleEvent, "Room transport event did not match the active membership fence.");

        private bool CanBegin(RoomTransportCommand command)
        {
            if (command == RoomTransportCommand.Host || command == RoomTransportCommand.Join)
                return Phase == RoomSessionPhase.Inert || Phase == RoomSessionPhase.Failed || Phase == RoomSessionPhase.Expired;
            if (command == RoomTransportCommand.Leave)
                return Phase == RoomSessionPhase.Joined;
            return command == RoomTransportCommand.Reconnect && Phase == RoomSessionPhase.Recovering;
        }

        private ulong NextOperationId()
        {
            ++_nextOperationId;
            if (_nextOperationId == 0) ++_nextOperationId;
            return _nextOperationId;
        }

        private void RetireActiveOperation()
        {
            IRoomTransportOperation prior = _operation;
            _operation = null;
            _activeOperationId = 0;
            ++ClientGeneration;
            if (prior == null) return;
            try { prior.Cancel(); }
            finally { prior.Dispose(); }
        }

        private void SetPhase(RoomSessionPhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }

        private bool Fail(RoomSessionErrorCode code, string message, out string error)
        {
            Record(code, message); error = message; return false;
        }

        private void Record(RoomSessionErrorCode code, string message)
        {
            LastErrorCode = code;
            LastError = string.IsNullOrEmpty(message) ? "Room transport failed." : message;
        }

        private void RequireOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                throw new InvalidOperationException("Room client must be used from its construction/main thread.");
        }

        public void Dispose()
        {
            RequireOwnerThread();
            if (_disposed) return;
            _disposed = true;
            RetireActiveOperation();
            _membership = null; _expectedRoomId = null;
            lock (_queueLock) _pending.Clear();
            SetPhase(RoomSessionPhase.Disposed);
            EnvelopeReceived = null; PhaseChanged = null;
        }
    }
}
