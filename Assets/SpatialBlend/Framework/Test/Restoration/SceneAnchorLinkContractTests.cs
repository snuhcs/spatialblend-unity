// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SceneAnchorLinkContractTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyFixtures()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
            {
                if (_objects[i] != null)
                {
                    Object.DestroyImmediate(_objects[i]);
                }
            }
            _objects.Clear();
        }

        [Test]
        public void Host_SharesOnlyTheChosenTable_WithWorldAlignedSurround()
        {
            GameObject small = Anchor("small-table", 1f);
            GameObject big = Anchor("big-table", 4f);
            big.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var catalog = new ListSceneAnchorCatalog();
            catalog.Add(Record(small, 1f, new Vector3(0.2f, 0.1f, 0f), new Vector3(1f, 1f, 0.05f)));
            catalog.Add(Record(big, 4f, new Vector3(0.1f, 0.2f, 0.3f), new Vector3(2f, 4f, 0.2f)));
            var published = new RecordingPublisher();
            var app = App();
            app.UseSceneAnchorCatalog(catalog);
            app.UseSceneAnchorPublisher(published);

            Assert.That(published.Anchor, Is.Null, "nothing is shared before the user chooses");
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.True);

            Assert.That(published.Anchor, Is.SameAs(big));
            Assert.That(published.ShareWithPeers, Is.True);
            Assert.That(small.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            SbMRUKAnchorEntity entity = big.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(entity, Is.Not.Null);
            Assert.That(entity.SceneLabel, Is.EqualTo(SbMRUKAnchorEntity.TableLabel));
            Assert.That(entity.ShareKey, Is.Not.Empty, "the sharer's key");
            Assert.That(big.name, Does.StartWith(SbMRUKAnchorEntity.TableLabel + "#" + entity.ShareKey + "#obj@"),
                "the wire name carries the label, the key and the measured box");
            BoxCollider localBox = big.GetComponent<BoxCollider>();
            Assert.That(localBox.center, Is.EqualTo(new Vector3(0.1f, 0.2f, 0.3f)));
            Assert.That(localBox.size, Is.EqualTo(new Vector3(2f, 4f, 0.2f)));

            Transform host = entity.AffordanceHost.transform;
            Assert.That(host.parent, Is.SameAs(big.transform));
            Assert.That(Quaternion.Angle(host.rotation, Quaternion.identity), Is.LessThan(0.01f));
            BoxCollider worldBox = host.GetComponent<BoxCollider>();
            Vector3 expected = SbMRUKAnchorEntity.WorldAlignedSize(
                Quaternion.Euler(-90f, 0f, 0f), new Vector3(2f, 4f, 0.2f));
            Assert.That(Vector3.Distance(expected, new Vector3(2f, 0.2f, 4f)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(worldBox.size, expected), Is.LessThan(1e-4f));
            Assert.That(host.GetComponent<AffordanceVolume>(), Is.Null, "T3.6: no runtime volume on the anchor host.");
            Assert.That(big.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(9), "the shared box: its edges and eight corner brackets");
        }

        [Test]
        public void Table_DeclaresTabletopSupportAndTheTopPlusTheFallbackMarginAsRegion_TheSameOnEverySide()
        {
            SpatialAuthoringPlan host = CompileTable(out SbSpatialAuthoring hostAuthoring);
            SpatialAuthoringPlan receiver = CompileTable(out _);

            // Region is the tabletop plus the 0.5 m fallback margin.
            Vector2[] footprint = host.Mappings[1].polygon;
            Vector2[] region = host.Mappings[0].polygon;
            Assert.That(region.Max(p => p.x) - region.Min(p => p.x),
                Is.EqualTo(footprint.Max(p => p.x) - footprint.Min(p => p.x) + 2f * SceneAnchorSpatialAuthoring.StandOffMeters).Within(1e-3f));
            Assert.That(region.Max(p => p.y) - region.Min(p => p.y),
                Is.EqualTo(footprint.Max(p => p.y) - footprint.Min(p => p.y) + 2f * SceneAnchorSpatialAuthoring.StandOffMeters).Within(1e-3f));

            // Local centre (0.1,0.2,0.3), size (2,4,0.2) in the MRUK frame (+Z up) is a 2 x 4 footprint.
            Assert.That(footprint.Max(p => p.x) - footprint.Min(p => p.x), Is.EqualTo(2f).Within(1e-3f));
            Assert.That(footprint.Max(p => p.y) - footprint.Min(p => p.y), Is.EqualTo(4f).Within(1e-3f));
            Assert.That(hostAuthoring.frameEulerAngles, Is.EqualTo(SceneAnchorSpatialAuthoring.MrukFrameEulerAngles));

            // The table top is the Tabletop support surface, at the top face height of the box.
            SpatialElementMapping top = host.Mappings[2];
            Assert.That(top.role, Is.EqualTo(SpatialElementRole.Surface));
            Assert.That(top.tag, Is.EqualTo(SceneAnchorSpatialAuthoring.TabletopTag));
            Assert.That(top.surface.All(v => Mathf.Abs(v.y - 0.4f) < 1e-4f), Is.True);
            Assert.That(host.Mappings.Length, Is.EqualTo(3), "region, object and exactly one Tabletop");

            // Both sides compile the identical declaration from their own box.
            Assert.That(receiver.Mappings.Length, Is.EqualTo(host.Mappings.Length));
            for (int i = 0; i < host.Mappings.Length; ++i)
            {
                Assert.That(receiver.Mappings[i].id, Is.EqualTo(host.Mappings[i].id));
                Assert.That(receiver.Mappings[i].tag, Is.EqualTo(host.Mappings[i].tag));
            }
        }

        [Test]
        public void Table_SharedIdFollowsTheLabelNotTheInstance()
        {
            long first = SpatialBlendManager.SceneAnchorSharedId(SbMRUKAnchorEntity.TableLabel);
            Assert.That(SpatialBlendManager.SceneAnchorSharedId(SbMRUKAnchorEntity.TableLabel), Is.EqualTo(first));
            Assert.That(SpatialBlendManager.SceneAnchorSharedId("FLOOR"), Is.Not.EqualTo(first));
            Assert.That(SpatialAuthoringCompiler.RegionSharedId(first), Is.GreaterThan(first));
        }

        [Test]
        public void Receiver_LinksSharedTableToItsOwnBiggestFreeOneOfTheLabel()
        {
            // The incoming table is 2 x 4 x 0.2; the local room has a huge one and a similar one. The host shares its
            // tables largest first and each links to the receiver's largest free one of the label, so the huge one is taken.
            GameObject huge = Anchor("local-huge", 9f);
            GameObject similar = Anchor("local-similar", 4f);
            GameObject incoming = Replica("TABLE", "t1", new Vector3(0.1f, 0.2f, 0.3f), new Vector3(2f, 4f, 0.2f));
            var catalog = Catalog(Record(huge, 9f, Vector3.zero, new Vector3(6f, 9f, 0.5f)),
                                  Record(similar, 4f, Vector3.zero, new Vector3(1.9f, 3.9f, 0.21f)));
            var app = App(catalog);
            app.UseSceneAnchorPublisher(new RecordingPublisher());

            Assert.That(app.OnShare(incoming).result, Is.EqualTo(OnShareResponse.Result.Defer), "the local table registers first");
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(app.ShareStateOf(catalog.Anchors[1]), Is.EqualTo(AnchorShareState.None));
            Assert.That(huge.GetComponent<SbMRUKAnchorEntity>().ShareKey, Is.EqualTo("t1"), "registered under the sharer's key");
        }

        [Test]
        public void Receiver_AddsAsNewWhenNoLocalAnchorHasTheSameLabel()
        {
            GameObject incoming = Replica("BED", "b1", Vector3.zero, new Vector3(2f, 1.4f, 0.5f));
            GameObject table = Anchor("local-table", 4f);
            var app = App(Catalog(Record(table, 4f)));

            OnShareResponse response = app.OnShare(incoming);

            Assert.That(response.result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            Assert.That(response.gameObject, Is.Null);
            Assert.That(table.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the room's own table is untouched");
        }

        [Test]
        public void Receiver_RejectsAShareWithNoKeyNoMeasureOrOfTheRoomsStructure()
        {
            var app = App(Catalog(Record(Anchor("local-table", 4f), 4f)));
            var keyless = Anchor("legacy-table", 1f);
            keyless.AddComponent<SbMRUKAnchorEntity>().SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            Assert.That(app.OnShare(keyless).result, Is.EqualTo(OnShareResponse.Result.Reject), "an older peer's table has no key and no measure");
            Assert.That(app.OnShare(Replica("FLOOR", "f1", Vector3.zero, new Vector3(4f, 5f, 0.05f))).result, Is.EqualTo(OnShareResponse.Result.Reject),
                "the structure of the room is not a thing to install");
            Assert.That(app.SharedBoxes, Is.Empty);
        }

        [Test]
        public void Receiver_AddsAvatarAndEveryOtherObjectAsNewAsInV1()
        {
            var app = App(new ListSceneAnchorCatalog());
            var avatar = new GameObject("avatar") { tag = "Avatar" };
            _objects.Add(avatar);
            var plain = Anchor("plain", 1f);

            Assert.That(app.OnShare(avatar).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            // As v1: every object that is not an avatar or a scene anchor is AddAsNew.
            Assert.That(app.OnShare(plain).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
        }

        [Test]
        public void Receiver_LinksAnAttachedSceneNetworkEntityAndAddsOneThatIsNot()
        {
            var app = App(new ListSceneAnchorCatalog());
            var managerObject = new GameObject("manager");
            _objects.Add(managerObject);
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            // OnShare is called by the app's space manager, and its receive log reads the manager's role.
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);

            var loose = new GameObject("Chess");
            _objects.Add(loose);
            loose.AddComponent<SbNetworkEntity>();
            Assert.That(app.OnShare(loose).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));

            var placed = new GameObject("Whiteboard");
            _objects.Add(placed);
            var entity = placed.AddComponent<SbNetworkEntity>();
            entity.Attach(manager, 7, false);

            OnShareResponse linked = app.OnShare(placed);
            Assert.That(linked.result, Is.EqualTo(OnShareResponse.Result.LinkToExisting));
            Assert.That(linked.gameObject, Is.SameAs(placed));
        }

        [Test]
        public void LinkTarget_IsThePlacedSceneObjectWhenTheReplicaIsThatObject()
        {
            var managerObject = new GameObject("manager");
            _objects.Add(managerObject);
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            var placed = new GameObject("Chess");
            _objects.Add(placed);
            var entity = placed.AddComponent<SbNetworkEntity>();
            entity.Attach(manager, 7, false);

            var find = typeof(SpatialBlendManager).GetMethod(
                "FindAttachedLinkTarget",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(find.Invoke(manager, new object[] { placed, placed }), Is.SameAs(entity));

            var loose = new GameObject("loose");
            _objects.Add(loose);
            loose.AddComponent<SbNetworkEntity>();
            Assert.That(find.Invoke(manager, new object[] { loose, loose }), Is.Null,
                "an unattached scene object is not a link target");
        }

        [Test]
        public void Unshare_DoesNotRemoveTheLinkedLocalAnchor()
        {
            GameObject local = Anchor("linked-table", 4f);
            var entity = local.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            var app = App(Catalog(Record(local, 4f)));

            app.OnRemove(local);

            Assert.That(local, Is.Not.Null);
            Assert.That(entity, Is.Not.Null);
            Assert.That(entity.SceneLabel, Is.EqualTo(SbMRUKAnchorEntity.TableLabel));
        }

        private SpatialAuthoringPlan CompileTable(out SbSpatialAuthoring authoring)
        {
            GameObject anchor = Anchor("table", 4f);
            anchor.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var entity = anchor.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            entity.ApplySupport(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(2f, 4f, 0.2f));
            authoring = anchor.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring, Is.Not.Null);
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            return plan;
        }

        private GameObject Replica(string label, string key, Vector3 center, Vector3 size)
        {
            string name = SharedAnchorName.Compose(SharedAnchorName.Head(label, key), center, size, null);
            var go = new GameObject(name);
            _objects.Add(go);
            var entity = go.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel(label);
            entity.AdoptWireName(name, label);
            return go;
        }

        private FreeformCollaborationApp App(ISceneAnchorCatalog catalog = null)
        {
            var go = new GameObject("freeform-app");
            _objects.Add(go);
            var app = go.AddComponent<FreeformCollaborationApp>();
            if (catalog != null)
            {
                app.UseSceneAnchorCatalog(catalog);
            }
            return app;
        }

        private GameObject Anchor(string name, float size)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.localScale = Vector3.one * Mathf.Max(size, 0.01f);
            return go;
        }

        private static ListSceneAnchorCatalog Catalog(params SceneAnchorRecord[] records)
        {
            var catalog = new ListSceneAnchorCatalog();
            for (int i = 0; i < records.Length; ++i)
            {
                catalog.Add(records[i]);
            }
            return catalog;
        }

        private static SceneAnchorRecord Record(GameObject anchor, float size)
        {
            return Record(anchor, size, Vector3.zero, Vector3.one * 0.1f);
        }

        private static SceneAnchorRecord Record(GameObject anchor, float size, Vector3 center, Vector3 localSize)
        {
            return new SceneAnchorRecord
            {
                Label = SbMRUKAnchorEntity.TableLabel,
                Anchor = anchor,
                Size = size,
                LocalCenter = center,
                LocalSize = localSize
            };
        }

        private sealed class RecordingPublisher : ISceneAnchorPublisher
        {
            public GameObject Anchor;
            public bool ShareWithPeers;
            public void Publish(GameObject anchor, bool shareWithPeers)
            {
                Anchor = anchor;
                ShareWithPeers = shareWithPeers;
            }
        }
    }
}
#endif
