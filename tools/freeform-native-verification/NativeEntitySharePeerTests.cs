// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Two real managers over loopback with the real native core and the real FreeformCollaborationApp
    // on both sides (no Fusion). v1 registered scanned objects and objects created before connect with
    // propagate=false and they still reached the peers: the v1 core's ShareEntity always sent the entity
    // and only withheld the per-entity state stream (spatialblend-core space/space.cc Space::ShareEntity,
    // which calls hub->AsyncShare whatever the flag). The receiver's OnShare answered AddAsNew for
    // anything that was not an avatar or a scene anchor (v1
    // FreeformCollaborationApp.OnShare).
    public partial class NativeManagerIntegrationTests
    {
        private sealed class PeerRig
        {
            public SpatialBlendManager Host, Client;
            public FreeformCollaborationApp HostApp, ClientApp;
            // Set for a tunneled rig: the two ends of the core link and the carrier between them.
            public CoreLinkPump HostPump, ClientPump;
            public readonly Queue<(int Epoch, int Sequence, byte[] Bytes)> ToHost = new Queue<(int, int, byte[])>();
            public readonly Queue<(int Epoch, int Sequence, byte[] Bytes)> ToClient = new Queue<(int, int, byte[])>();

            // One frame of the carrier: what each core has for the other is sent, then delivered, in order.
            public void PumpLinks()
            {
                if (HostPump == null) return;
                HostPump.Pump();
                ClientPump.Pump();
                while (ToHost.Count > 0) { var c = ToHost.Dequeue(); HostPump.Receive(c.Epoch, c.Sequence, c.Bytes); }
                while (ToClient.Count > 0) { var c = ToClient.Dequeue(); ClientPump.Receive(c.Epoch, c.Sequence, c.Bytes); }
            }
        }

        private sealed class QueueWire : ICoreLinkWire
        {
            private readonly Queue<(int Epoch, int Sequence, byte[] Bytes)> _queue;
            public QueueWire(Queue<(int Epoch, int Sequence, byte[] Bytes)> queue) => _queue = queue;
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => _queue.Enqueue((epoch, sequence, bytes));
        }

        // The same two real managers, joined with no TCP listener and no address (ABI 4.5): the core link is a byte
        // stream and this test's frame loop is the carrier, as Fusion reliable data is in the app.
        private PeerRig BuildTunneledPeerRig()
        {
            var rig = new PeerRig();
            rig.Host = CreateManager("tunnel-host");
            rig.Client = CreateManager("tunnel-client");
            rig.Host.HostTunneled();
            AssertConnected(rig.Host);
            Assert.That(rig.Host.Port, Is.Zero, "a tunneled host binds and advertises no port");
            CoreLink hostEnd = rig.Host.AcceptCoreLink();
            Assert.That(hostEnd, Is.Not.Null);
            CoreLink joinEnd = rig.Client.JoinTunneled();
            Assert.That(joinEnd, Is.Not.Null);
            Assert.That(rig.Client.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connecting),
                "the join completes only while its link is pumped");
            rig.HostPump = new CoreLinkPump(hostEnd, 2, 1, new QueueWire(rig.ToClient), holdUntilPeerSpeaks: true);
            rig.ClientPump = new CoreLinkPump(joinEnd, 1, 1, new QueueWire(rig.ToHost), holdUntilPeerSpeaks: false);
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!rig.Client.Initialized && rig.Client.ConnectionState != SpatialBlendConnectionState.Failed &&
                   DateTime.UtcNow < deadline)
            {
                rig.PumpLinks();
                rig.Host.Update();
                rig.Client.Update();
                System.Threading.Thread.Sleep(2);
            }
            AssertConnected(rig.Client);
            rig.HostApp = ActiveApp("tunnel-host-app", Catalog());
            rig.ClientApp = ActiveApp("tunnel-client-app", Catalog());
            SetAppManager(rig.HostApp, rig.Host);
            SetAppManager(rig.ClientApp, rig.Client);
            Invoke(rig.Host, "RegisterApp", rig.HostApp);
            Invoke(rig.Client, "RegisterApp", rig.ClientApp);
            return rig;
        }

        private PeerRig BuildPeerRig()
        {
            var rig = new PeerRig();
            rig.Host = CreateManager("share-host");
            rig.Client = CreateManager("share-client");
            int port = FreePort();
            rig.Host.Host(port);
            AssertConnected(rig.Host);
            rig.Client.Connect(LoopbackHost, port);
            AssertConnected(rig.Client);
            rig.HostApp = ActiveApp("share-host-app", Catalog());
            rig.ClientApp = ActiveApp("share-client-app", Catalog());
            SetAppManager(rig.HostApp, rig.Host);
            SetAppManager(rig.ClientApp, rig.Client);
            Invoke(rig.Host, "RegisterApp", rig.HostApp);
            Invoke(rig.Client, "RegisterApp", rig.ClientApp);
            return rig;
        }

        // EditMode has no player loop, so the main-thread dispatcher that the receiver's reconstruction
        // waits on is initialised and pumped here, alongside both managers.
        private void PumpPeers(Func<bool> done, PeerRig rig, Func<string> describe = null)
        {
            describe = describe ?? (() => string.Empty);
            PumpPeersCore(done, rig, describe);
        }

        private void PumpPeersCore(Func<bool> done, PeerRig rig, Func<string> describe)
        {
            UnityMainThreadDispatcher.Initialize();
            UnityMainThreadDispatcher dispatcher = UnityMainThreadDispatcher.Instance;
            _objects.Add(dispatcher.gameObject);
            // EditMode never runs the dispatcher's Awake, which is what records the main thread. Without it
            // the reconstruction's main-thread hop would queue behind itself and wait forever.
            typeof(UnityMainThreadDispatcher).GetField("_mainThreadId",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            System.Reflection.MethodInfo update = typeof(UnityMainThreadDispatcher).GetMethod(
                "Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < deadline)
            {
                rig.PumpLinks();
                rig.Host.Update();
                rig.Client.Update();
                update.Invoke(dispatcher, null);
                System.Threading.Thread.Sleep(10);
            }
            Assert.That(done(), Is.True, "The shared object never arrived at the peer. " + describe());
        }

        // The replica exists when OnObjectAdded fires; its content and box blocks are applied as they are read.
        private static bool ContentArrived(GameObject go)
        {
            SbNativeEntity replica = go.GetComponent<SbNativeEntity>();
            return replica != null && replica.GetContentData().Length > 0 && replica.AuthoredBox != null;
        }

        private static string Describe(List<GameObject> arrived) =>
            $"arrived={arrived.Count} " + string.Join(",", arrived.Select(g => g.GetComponent<SbNativeEntity>()).Select(
                r => r == null ? "noEntity" : $"bytes={r.GetContentData().Length},box={(r.AuthoredBox != null)}"));

        // The managers destroy a reconstructed replica with Destroy, which EditMode refuses; the test retires
        // its own replicas and managers first.
        private void RetirePeers(PeerRig rig, IEnumerable<GameObject> replicas)
        {
            foreach (GameObject replica in replicas) if (replica != null) UnityEngine.Object.DestroyImmediate(replica);
            rig.Client.Disconnect();
            rig.Host.Disconnect();
        }

        private GameObject ScannedObject(string name, Vector3 position, Vector3 boxSize, Vector3 scale, byte[] glb)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.position = position;
            go.AddComponent<BoxCollider>();
            var entity = go.AddComponent<SbNativeEntity>();
            // EditMode does not run Awake, which is where the entity adds its content and box serialization blocks.
            entity.EnsureSerializationBlocks();
            entity.SetContentData(glb);
            Assert.That(ScanBoxRegion.TryFromLocalBox(Vector3.zero, boxSize, out ScanBoxRegion box), Is.True);
            entity.SetAuthoredBox(box);
            entity.SetSharedScale(scale);
            return go;
        }

        // A valid GLB of about `total` bytes: the JSON chunk padded with the whitespace JSON allows.
        private static byte[] PaddedGlb(string tag, int total)
        {
            byte[] baseJson = Encoding.UTF8.GetBytes(
                "{\"asset\":{\"version\":\"2.0\",\"generator\":\"" + tag + "\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"n\"}]}");
            int padded = (Math.Max(total, 32 + baseJson.Length) - 20 + 3) & ~3;
            byte[] glb = new byte[20 + padded];
            void W(int o, uint v) { glb[o] = (byte)v; glb[o + 1] = (byte)(v >> 8); glb[o + 2] = (byte)(v >> 16); glb[o + 3] = (byte)(v >> 24); }
            W(0, 0x46546C67); W(4, 2); W(8, (uint)glb.Length); W(12, (uint)padded); W(16, 0x4E4F534A);
            Buffer.BlockCopy(baseJson, 0, glb, 20, baseJson.Length);
            for (int i = 20 + baseJson.Length; i < glb.Length; ++i) glb[i] = 0x20;
            return glb;
        }

        private static byte[] TinyGlb(string tag)
        {
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"asset\":{\"version\":\"2.0\",\"generator\":\"" + tag + "\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"n\"}]}");
            int padded = (json.Length + 3) & ~3;
            byte[] glb = new byte[12 + 8 + padded];
            void W(int o, uint v) { glb[o] = (byte)v; glb[o + 1] = (byte)(v >> 8); glb[o + 2] = (byte)(v >> 16); glb[o + 3] = (byte)(v >> 24); }
            W(0, 0x46546C67); W(4, 2); W(8, (uint)glb.Length); W(12, (uint)padded); W(16, 0x4E4F534A);
            Buffer.BlockCopy(json, 0, glb, 20, json.Length);
            for (int i = 20 + json.Length; i < 20 + padded; ++i) glb[i] = 0x20;
            return glb;
        }

        private static SbNativeEntity Replica(GameObject go) => go.GetComponent<SbNativeEntity>();

        [TestCase(true)]
        [TestCase(false)]
        public void AScannedObjectRegisteredWithOrWithoutPropagateReachesThePeerWithItsContent(bool propagate)
        {
            PeerRig rig = BuildPeerRig();
            var arrived = new List<GameObject>();
            Action<GameObject> onAdded = go => arrived.Add(go);
            FreeformCollaborationApp.OnRemoteObjectAdded += onAdded;
            try
            {
                byte[] glb = TinyGlb("scan-" + propagate);
                GameObject scanned = ScannedObject("scan", new Vector3(1f, 0f, 2f), new Vector3(0.4f, 0.6f, 1f),
                    Vector3.one * 0.6f, glb);

                rig.HostApp.RegisterGameObject(scanned, propagate);

                PumpPeers(() => arrived.Count > 0 && ContentArrived(arrived[0]), rig, () => Describe(arrived));
                SbNativeEntity replica = Replica(arrived[0]);
                Assert.That(replica, Is.Not.Null);
                Assert.That(replica.IsRemoteReplica, Is.True);
                Assert.That(replica.GetContentData(), Is.EqualTo(glb), "The GLB bytes ride the content block.");
                Assert.That(replica.TryGetSharedScale(out Vector3 scale), Is.True);
                Assert.That(Vector3.Distance(scale, Vector3.one * 0.6f), Is.LessThan(1e-5f), "The source's fitted scale.");
                Assert.That(replica.AuthoredBox, Is.Not.Null, "The annotated box rides the second block.");
                Assert.That(replica.AuthoredBox.Encode(), Is.EqualTo(scanned.GetComponent<SbNativeEntity>().AuthoredBox.Encode()));
            }
            finally { FreeformCollaborationApp.OnRemoteObjectAdded -= onAdded; RetirePeers(rig, arrived); }
        }

        // ABI 4.5: the same share over the tunneled link, with a multi-megabyte GLB (a scan is that big). Nothing in this
        // test opens a TCP listener or names an address.
        [Test]
        public void AScannedObjectReachesThePeerOverTheTunneledLinkWithItsLargeContent()
        {
            PeerRig rig = BuildTunneledPeerRig();
            var arrived = new List<GameObject>();
            Action<GameObject> onAdded = go => arrived.Add(go);
            FreeformCollaborationApp.OnRemoteObjectAdded += onAdded;
            try
            {
                byte[] glb = PaddedGlb("tunnel", 3 * 1024 * 1024);
                GameObject scanned = ScannedObject("tunnel-scan", new Vector3(1f, 0f, 2f), new Vector3(0.4f, 0.6f, 1f),
                    Vector3.one * 0.6f, glb);

                rig.HostApp.RegisterGameObject(scanned, true);

                PumpPeers(() => arrived.Count > 0 && ContentArrived(arrived[0]), rig, () => Describe(arrived));
                SbNativeEntity replica = Replica(arrived[0]);
                Assert.That(replica.GetContentData().Length, Is.EqualTo(glb.Length));
                Assert.That(replica.GetContentData(), Is.EqualTo(glb), "The GLB bytes ride the content block, whole, over the tunnel.");
                Assert.That(rig.HostPump.BytesSent + rig.ClientPump.BytesSent, Is.GreaterThan(glb.Length),
                    "the bytes crossed the carrier, not a socket");
                Assert.That(replica.AuthoredBox, Is.Not.Null);
            }
            finally { FreeformCollaborationApp.OnRemoteObjectAdded -= onAdded; RetirePeers(rig, arrived); }
        }

        // The AFV mismatch race: two participants scan at once. Each peer's copy of
        // the other's object has the source's box and scale, and neither source's own object is touched
        // by the arrival of the other's.
        [Test]
        public void TwoPeersThatScanAtTheSameTimeEachKeepTheirOwnBoxAndScaleAndTheCopiesAgreeWithTheSources()
        {
            PeerRig rig = BuildPeerRig();
            var arrived = new List<GameObject>();
            Action<GameObject> onAdded = go => arrived.Add(go);
            FreeformCollaborationApp.OnRemoteObjectAdded += onAdded;
            try
            {
                Vector3 hostBox = new Vector3(0.4f, 0.6f, 1.0f), clientBox = new Vector3(0.9f, 0.3f, 0.5f);
                Vector3 hostScale = Vector3.one * 0.6f, clientScale = Vector3.one * 0.3f;
                GameObject hostObject = ScannedObject("host-scan", new Vector3(1f, 0f, 2f), hostBox, hostScale, TinyGlb("host"));
                GameObject clientObject = ScannedObject("client-scan", new Vector3(-1f, 0f, 3f), clientBox, clientScale, TinyGlb("client"));

                // Registered back to back, before either manager has pumped: both shares are in flight together.
                rig.HostApp.RegisterGameObject(hostObject, false);
                rig.ClientApp.RegisterGameObject(clientObject, false);
                PumpPeers(() => arrived.Count >= 2 && arrived.All(ContentArrived), rig, () => Describe(arrived));

                Assert.That(arrived.Count, Is.EqualTo(2));
                SbNativeEntity hostSource = hostObject.GetComponent<SbNativeEntity>();
                SbNativeEntity clientSource = clientObject.GetComponent<SbNativeEntity>();
                ScanBoxRegion.TryFromLocalBox(Vector3.zero, hostBox, out ScanBoxRegion expectedHostBox);
                ScanBoxRegion.TryFromLocalBox(Vector3.zero, clientBox, out ScanBoxRegion expectedClientBox);
                Assert.That(hostSource.AuthoredBox.Encode(), Is.EqualTo(expectedHostBox.Encode()));
                Assert.That(clientSource.AuthoredBox.Encode(), Is.EqualTo(expectedClientBox.Encode()));
                Assert.That(Vector3.Distance(hostSource.transform.position, new Vector3(1f, 0f, 2f)), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(clientSource.transform.position, new Vector3(-1f, 0f, 3f)), Is.LessThan(1e-5f));

                SbNativeEntity copyOfHost = arrived.Select(Replica).Single(r => Encoding.UTF8.GetString(r.GetContentData()).Contains("\"host\""));
                SbNativeEntity copyOfClient = arrived.Select(Replica).Single(r => Encoding.UTF8.GetString(r.GetContentData()).Contains("\"client\""));
                Assert.That(copyOfHost.AuthoredBox.Encode(), Is.EqualTo(expectedHostBox.Encode()), "The client's copy has the host's box.");
                Assert.That(copyOfClient.AuthoredBox.Encode(), Is.EqualTo(expectedClientBox.Encode()), "The host's copy has the client's box.");
                Assert.That(copyOfHost.TryGetSharedScale(out Vector3 s1) && Vector3.Distance(s1, hostScale) < 1e-5f, Is.True);
                Assert.That(copyOfClient.TryGetSharedScale(out Vector3 s2) && Vector3.Distance(s2, clientScale) < 1e-5f, Is.True);
                Assert.That(hostSource.TryGetSharedScale(out Vector3 own1) && Vector3.Distance(own1, hostScale) < 1e-5f, Is.True);
                Assert.That(clientSource.TryGetSharedScale(out Vector3 own2) && Vector3.Distance(own2, clientScale) < 1e-5f, Is.True);
            }
            finally { FreeformCollaborationApp.OnRemoteObjectAdded -= onAdded; RetirePeers(rig, arrived); }
        }
    }
}
#endif
