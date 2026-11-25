// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationRoomSession")]
    public sealed class FoundationRoomSessionTests
    {
        private sealed class FakeLease : IRoomAuthenticationLease
        {
            internal int DisposeCount;
            public void Dispose() { ++DisposeCount; }
        }

        private sealed class FakeAuthentication : IRoomAuthenticationProvider
        {
            internal bool Accept = true;
            internal readonly List<FakeLease> Leases = new List<FakeLease>();
            public bool TryAcquire(RoomTransportCommand command, string room, out IRoomAuthenticationLease lease, out string error)
            {
                if (!Accept) { lease = null; error = "denied"; return false; }
                var value = new FakeLease(); Leases.Add(value); lease = value; error = null; return true;
            }
        }

        private sealed class FakeOperation : IRoomTransportOperation
        {
            internal int CancelCount;
            internal int DisposeCount;
            public void Cancel() { ++CancelCount; }
            public void Dispose() { ++DisposeCount; }
        }

        private sealed class FakeTransport : IRoomClientTransport
        {
            internal readonly List<RoomTransportRequest> Requests = new List<RoomTransportRequest>();
            internal readonly List<FakeOperation> Operations = new List<FakeOperation>();
            internal readonly List<Action<RoomTransportEvent>> Sinks = new List<Action<RoomTransportEvent>>();
            internal bool Accept = true;
            public bool TryBegin(RoomTransportRequest request, Action<RoomTransportEvent> onEvent,
                                 out IRoomTransportOperation operation, out string error)
            {
                if (!Accept) { operation = null; error = "transport denied"; return false; }
                var value = new FakeOperation(); Requests.Add(request); Operations.Add(value); Sinks.Add(onEvent);
                operation = value; error = null; return true;
            }
            internal void Emit(int index, RoomTransportEvent value) => Sinks[index](value);
        }

        [Test]
        public void HostPreservesOpaqueN0BytesAndNeverCreatesLocalBindingOrFreshness()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            using (var client = Client(auth, transport))
            {
                byte[] source = { 1, 2, 3, 4 };
                var envelope = new RoomEnvelopeBytes(source); source[0] = 99;
                Assert.That(client.TryHost(envelope, out string error), Is.True, error);
                Assert.That(transport.Requests, Has.Count.EqualTo(1));
                Assert.That(transport.Requests[0].Command, Is.EqualTo(RoomTransportCommand.Host));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, transport.Requests[0].Envelope.CopyBytes());
                Assert.That(auth.Leases[0].DisposeCount, Is.EqualTo(1), "Auth lease is not retained in client state.");
                var fence = Fence("host-room", 4, 8, 10);
                transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, fence)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joined));
                Assert.That(client.Membership.AuthorityEpoch, Is.EqualTo(10));
                Assert.That(client.ClientGeneration, Is.EqualTo(1), "The client lifecycle generation is local and distinct from server authority.");

                byte[] delivered = null;
                client.EnvelopeReceived += (raw, membership) => delivered = raw.CopyBytes();
                var inbound = new RoomEnvelopeBytes(new byte[] { 7, 8, 9 });
                transport.Emit(0, Event(RoomTransportEventKind.Envelope, 1, fence, inbound));
                client.Pump();
                CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, delivered);
                Assert.That(typeof(SpatialBlendRoomClient).GetField("_membership", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Not.Null);
                Assert.That(typeof(SpatialBlendRoomClient).GetField("SharedToLocalBinding", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Null,
                    "U0 cannot mint a U1 receiver-local binding from room traffic.");
            }
        }

        [Test]
        public void CrossRoomLateCallbackAndReconnectCannotReplaceMembershipOrAuthority()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            using (var client = Client(auth, transport))
            {
                Assert.That(client.TryJoin("room-a", Bytes(1), out string error), Is.True, error);
                transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, Fence("room-b", 1, 1, 1)));
                client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joining));
                Assert.That(client.LastErrorCode, Is.EqualTo(RoomSessionErrorCode.CrossRoomEvent));
                var first = Fence("room-a", 1, 1, 4);
                transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, first)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joined));
                transport.Emit(0, Event(RoomTransportEventKind.TransportLost, 1, first)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Recovering));
                Assert.That(client.Membership, Is.Null);
                Assert.That(transport.Operations[0].CancelCount, Is.EqualTo(1));

                Assert.That(client.TryReconnect(Bytes(2), out error), Is.True, error);
                // Old transport callback retains no power over the new operation.
                transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, first));
                client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joining));
                var resumed = Fence("room-a", 1, 2, 9);
                transport.Emit(1, Event(RoomTransportEventKind.MembershipAccepted, 2, resumed)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joined));
                Assert.That(client.Membership.ConnectionEpoch, Is.EqualTo(2));
                Assert.That(client.Membership.AuthorityEpoch, Is.EqualTo(9));
                Assert.That(client.ClientGeneration, Is.EqualTo(3), "Reconnect creates a new local lifecycle but authority remains the exact server event.");
            }
        }

        [Test]
        public void AuthorityAndExpiryRequireExactMembershipFence()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            using (var client = Client(auth, transport))
            {
                Assert.That(client.TryHost(Bytes(3), out string error), Is.True, error);
                var old = Fence("room", 2, 3, 4);
                transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, old)); client.Pump();
                var granted = Fence("room", 2, 3, 5);
                transport.Emit(0, Event(RoomTransportEventKind.AuthorityChanged, 1, granted)); client.Pump();
                Assert.That(client.Membership.AuthorityEpoch, Is.EqualTo(5));
                transport.Emit(0, Event(RoomTransportEventKind.AuthorityChanged, 1, old)); client.Pump();
                Assert.That(client.Membership.AuthorityEpoch, Is.EqualTo(5));
                Assert.That(client.LastErrorCode, Is.EqualTo(RoomSessionErrorCode.StaleEvent));
                transport.Emit(0, Event(RoomTransportEventKind.CredentialExpired, 1, old)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joined));
                transport.Emit(0, Event(RoomTransportEventKind.CredentialExpired, 1, granted)); client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Expired));
                Assert.That(client.Membership, Is.Null);
            }
        }

        [Test]
        public void LeaveAndDestroyCancelOnlyTheirExactOperationAndQueuedCallbacksAreInert()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            var client = Client(auth, transport);
            Assert.That(client.TryHost(Bytes(5), out string error), Is.True, error);
            var membership = Fence("room", 1, 1, 1);
            transport.Emit(0, Event(RoomTransportEventKind.MembershipAccepted, 1, membership)); client.Pump();
            Assert.That(client.TryLeave(Bytes(6), out error), Is.True, error);
            Assert.That(transport.Operations[0].CancelCount, Is.EqualTo(1));
            transport.Emit(0, Event(RoomTransportEventKind.MembershipLeft, 1, membership)); client.Pump();
            Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Leaving));
            transport.Emit(1, Event(RoomTransportEventKind.MembershipLeft, 2, membership)); client.Pump();
            Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Inert));
            Assert.That(transport.Operations[1].CancelCount, Is.EqualTo(1));

            Assert.That(client.TryHost(Bytes(7), out error), Is.True, error);
            client.Dispose();
            transport.Emit(2, Event(RoomTransportEventKind.MembershipAccepted, 3, membership));
            client.Pump();
            Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Disposed));
            Assert.That(transport.Operations[2].CancelCount, Is.EqualTo(1));
            Assert.That(transport.Operations[2].DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void InvalidOrDeniedRequestsRemainInertAndTransportNeverReceivesThem()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            using (var client = Client(auth, transport))
            {
                Assert.That(client.TryJoin("", Bytes(8), out _), Is.False);
                Assert.That(transport.Requests, Is.Empty);
                auth.Accept = false;
                Assert.That(client.TryHost(Bytes(9), out _), Is.False);
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Inert));
                Assert.That(transport.Requests, Is.Empty);
            }
        }

        [Test]
        public void TransportThreadDispatchAndBoundedOverflowFailClosedWithoutDeliveringPartialTail()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            using (var client = Client(auth, transport))
            {
                Assert.That(client.TryHost(Bytes(10), out string error), Is.True, error);
                var membership = Fence("room", 1, 1, 1);
                var worker = new Thread(() => transport.Emit(0,
                    Event(RoomTransportEventKind.MembershipAccepted, 1, membership)));
                worker.Start(); Assert.That(worker.Join(2000), Is.True);
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Hosting), "Transport thread cannot touch session state.");
                client.Pump(); Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Joined));
                for (int i = 0; i < 129; ++i)
                    transport.Emit(0, Event(RoomTransportEventKind.Envelope, 1, membership, Bytes((byte)(20 + i))));
                client.Pump();
                Assert.That(client.Phase, Is.EqualTo(RoomSessionPhase.Failed));
                Assert.That(client.Membership, Is.Null);
                Assert.That(client.LastErrorCode, Is.EqualTo(RoomSessionErrorCode.TransportFailure));
            }
        }

        [Test]
        public void SceneWrapperIsInertUntilExplicitlyBoundAndDestroyRetiresOnlyItsClient()
        {
            var auth = new FakeAuthentication(); var transport = new FakeTransport();
            var gameObject = new GameObject("room-session");
            try
            {
                var session = gameObject.AddComponent<SpatialBlendSession>();
                Assert.That(session.TryHost(Bytes(40), out _), Is.False);
                Assert.That(session.TryInitialize(new RoomClientConfiguration("https://room.test:443", 1, 0, auth), transport,
                    out string error), Is.True, error);
                Assert.That(session.TryHost(Bytes(41), out error), Is.True, error);
                Assert.That(session.TryInitialize(new RoomClientConfiguration("https://other.test:443", 1, 0, auth), transport,
                    out _), Is.False, "A scene wrapper must not replace/close a live bound client.");
                typeof(SpatialBlendSession).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(session, null);
                UnityEngine.Object.DestroyImmediate(gameObject);
                Assert.That(transport.Operations[0].CancelCount, Is.EqualTo(1));
                Assert.That(transport.Operations[0].DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                if (gameObject != null)
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static SpatialBlendRoomClient Client(FakeAuthentication auth, FakeTransport transport) =>
            new SpatialBlendRoomClient(new RoomClientConfiguration("https://room.test:443", 1, 0, auth), transport);
        private static RoomEnvelopeBytes Bytes(byte value) => new RoomEnvelopeBytes(new[] { value, (byte)(value + 1) });
        private static RoomMembershipFence Fence(string room, ulong roomEpoch, ulong connection, ulong authority) =>
            new RoomMembershipFence(room, roomEpoch, "participant", connection, "grant-" + authority, authority);
        private static RoomTransportEvent Event(RoomTransportEventKind kind, ulong operation, RoomMembershipFence membership, RoomEnvelopeBytes bytes = null) =>
            new RoomTransportEvent(kind, operation, membership, bytes, "test failure");
    }
}
#endif
