// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpaceID = System.Int64;
using SessionID = System.Int64;
using ElementID = System.Int64;
using EventHandle = System.Int64;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Edit Mode tests for the v2 (SpatialBlend) C API exercised through the real
    /// SpatialBlendAPI binding — two SEPARATE sessions hosted/connected over
    /// localhost, exactly like the Unity local simulator. This level catches bugs
    /// the C++ Space-level network_test cannot, because that test hand-assigns
    /// distinct space ids; here the ids come from the C API itself.
    ///
    /// Regression: sb_add_space must give each space a globally-unique space_id.
    /// LocalContext assigns ids per-context starting at 1, so two sessions both
    /// got id=1 and the host skipped the client (source==self) in propagation —
    /// the client never received shared elements. This test fails without the fix.
    /// </summary>
    [TestFixture]
    public class SpatialBlendNetworkTests
    {
        // Use a high, uncommon port to reduce collision with a running app.
        private const int Port = 47821;

        [OneTimeSetUp]
        public void OneTimeSetUp() => SpatialBlendAPI.Initialize();

        [TearDown]
        public void TearDown() => SpatialBlendAPI.CleanupAll();

        [Test]
        public void ShareElement_AcrossTwoSessions_ClientReceivesViaCallback()
        {
            // --- Host: its own session + space, hosted on the port ---
            SessionID hostSession = SpatialBlendAPI.CreateSession();
            Assert.Greater(hostSession, 0, "host session");
            SpaceID hostSpace = SpatialBlendAPI.AddSpace(hostSession);
            Assert.Greater(hostSpace, 0, "host space");
            EventHandle hostHandle = SpatialBlendAPI.HostSpace(hostSpace, Port);
            Assert.IsTrue(SpatialBlendAPI.TryWait(hostHandle, 3000),
                "HostSpace failed: " + SpatialBlendAPI.GetError(hostHandle));

            // --- Client: a SEPARATE session + space, connected to the host ---
            SessionID clientSession = SpatialBlendAPI.CreateSession();
            Assert.Greater(clientSession, 0, "client session");
            SpaceID clientSpace = SpatialBlendAPI.AddSpace(clientSession);
            Assert.Greater(clientSpace, 0, "client space");
            EventHandle connHandle =
                SpatialBlendAPI.ConnectSpace(clientSpace, "127.0.0.1", Port);
            Assert.IsTrue(SpatialBlendAPI.TryWait(connHandle, 3000),
                "ConnectSpace failed: " + SpatialBlendAPI.GetError(connHandle));

            // --- Register the element-added callback on the client ---
            int received = 0;
            long receivedSharedId = 0;
            string receivedTag = null;
            long receivedBytes = 0;
            SpatialBlendAPI.RegisterOnElementAdded(clientSpace,
                (space, elem, sharedId, tag, userData, size) =>
                {
                    Interlocked.Increment(ref received);
                    receivedSharedId = sharedId;
                    receivedTag = tag;
                    receivedBytes = size;
                });

            // Let the Subscribe stream settle.
            Thread.Sleep(400);

            // --- Host shares a NEW root element with a payload ---
            ElementID elem = SpatialBlendAPI.AddElement(hostSpace, 0);
            Assert.Greater(elem, 0, "host element");
            SpatialBlendAPI.SetElementSharedId(hostSpace, elem, 200);
            SpatialBlendAPI.SetElementTag(hostSpace, elem, "Sofa");

            byte[] payload = Encoding.UTF8.GetBytes("hello");
            GCHandle pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                SpatialBlendAPI.ShareElement(hostSpace, elem,
                    pin.AddrOfPinnedObject(), payload.Length);
            }
            finally
            {
                pin.Free();
            }

            // Wait for network propagation (callback fires on a gRPC thread).
            Thread.Sleep(800);

            Assert.Greater(received, 0,
                "Client never received the shared element — cross-space propagation " +
                "is broken (likely space_id collision: both spaces got id=1).");
            Assert.AreEqual(200, receivedSharedId, "shared id");
            Assert.AreEqual("Sofa", receivedTag, "tag");
            Assert.AreEqual(payload.Length, receivedBytes, "payload size carried");
        }
    }
}
#endif
