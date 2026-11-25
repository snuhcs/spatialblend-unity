// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using ContainmentManager = SpatialBlend.Tests.ManagerContainmentContractTests.ContainmentManager;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Each room places its own copy of a received object (the core carries no object pose between spaces), so a move a user
    /// makes to a copy must reach this room's own space. LateUpdate pushes a moved copy like a source, through its authored region,
    /// and never touches an avatar's (driven by the core) or a source's (its own path) element. The native round trip, a remote
    /// avatar mapped onto the moved copy, is <c>AReceiverThatMovesItsCopyAfterArrivalSeesTheRemoteAvatarOnTheMovedCopy</c>.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ReceivedCopyPlacementTests
    {
        private const long ChessObject = 9150;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void AMovedAuthoredCopyMovesItsRegionInThisSpaceToWhereAFreshCopyThereWouldBe()
        {
            SpatialAuthoringPlan plan = CompileChess();
            Matrix4x4 arrived = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 moved = Matrix4x4.TRS(new Vector3(-2f, 0f, 4.5f), Quaternion.Euler(0f, 145f, 0f), Vector3.one);

            ContainmentManager manager = HostedManager("copy");
            Assert.That(manager.TryApplySpatialAuthoring(plan, ChessObject, 0, arrived, null, out SpatialAuthoringBinding binding), Is.True);
            SbEntity copy = AttachCopy(manager, binding.ObjectElement, arrived);
            manager.LateUpdate();
            Matrix4x4 atArrival = manager.Transforms[binding.RegionElement];

            copy.transform.SetPositionAndRotation(moved.GetColumn(3), moved.rotation);
            manager.LateUpdate();

            ContainmentManager reference = HostedManager("reference");
            Assert.That(reference.TryApplySpatialAuthoring(plan, ChessObject, 0, moved, null, out SpatialAuthoringBinding fresh), Is.True);
            AssertNear(manager.Transforms[binding.RegionElement], reference.Transforms[fresh.RegionElement], "the moved copy's region");
            Assert.That(manager.Transforms[binding.RegionElement].GetColumn(3), Is.Not.EqualTo(atArrival.GetColumn(3)), "the region moved");
            Assert.That(manager.Transforms[binding.ObjectElement], Is.EqualTo(Matrix4x4.identity), "the object stays the identity under its region");
            Assert.That(copy.transform.hasChanged, Is.False, "the push consumed the change");
        }

        [Test]
        public void AnUnchangedCopyIsNotWrittenAgain()
        {
            ContainmentManager manager = HostedManager("unchanged");
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null,
                out SpatialAuthoringBinding binding), Is.True);
            AttachCopy(manager, binding.ObjectElement, Matrix4x4.identity);
            manager.LateUpdate();
            manager.Transforms.Clear();
            manager.LateUpdate();
            Assert.That(manager.Transforms, Is.Empty);
        }

        [Test]
        public void AReceivedAvatarAndASourceAreNotWrittenThroughTheCopyPath()
        {
            ContainmentManager manager = HostedManager("filters");
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null,
                out SpatialAuthoringBinding binding), Is.True);
            SbEntity avatar = AttachCopy(manager, binding.ObjectElement, Matrix4x4.identity, "Avatar");
            Assert.That(manager.UpdateReceivedCopyPlacement(avatar), Is.False, "an avatar is the core's to drive");

            var sourceObject = Inactive("source");
            var source = sourceObject.AddComponent<SbNativeEntity>();
            source.AllowManipulation = false;
            Attach(source, manager, 77, true);
            Assert.That(manager.UpdateReceivedCopyPlacement(source), Is.False, "a source goes through its own path");
            Assert.That(manager.UpdateReceivedCopyPlacement(null), Is.False);
        }

        [Test]
        public void ACopyWithAnActorOrASceneOwnedElementIsNotWrittenThroughTheCopyPath()
        {
            ContainmentManager manager = HostedManager("actor-scene");
            Assert.That(manager.TryApplySpatialAuthoring(CompileChess(), ChessObject, 0, Matrix4x4.identity, null,
                out SpatialAuthoringBinding binding), Is.True);
            SbEntity withActor = AttachCopy(manager, binding.ObjectElement, Matrix4x4.identity);
            var actors = (System.Collections.IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            actors[withActor.ID] = 99L;
            Assert.That(manager.UpdateReceivedCopyPlacement(withActor), Is.False, "an entity the core drives through an actor");
            actors.Remove(withActor.ID);

            SbEntity anchor = AttachCopy(manager, 5151, Matrix4x4.identity);
            var sceneOwned = (HashSet<long>)typeof(SpatialBlendManager).GetField("_sceneOwnedEntityElements",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            sceneOwned.Add(anchor.ID);
            Assert.That(manager.UpdateReceivedCopyPlacement(anchor), Is.False, "a scene anchor keeps what its adapter wrote");
        }

        [Test]
        public void AMovedUnauthoredCopyIsWrittenByLateUpdateOnceAndAnUnchangedOneIsNot()
        {
            var managerObject = Inactive("unauthored-late");
            var manager = managerObject.AddComponent<CopyWriteManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            SbEntity copy = AttachCopy(manager, 4343, Matrix4x4.identity);
            manager.LateUpdate();
            Assert.That(manager.Writes.Count, Is.EqualTo(1), "the arrival pose once");
            manager.LateUpdate();
            Assert.That(manager.Writes.Count, Is.EqualTo(1), "nothing changed");
            Matrix4x4 moved = Matrix4x4.TRS(new Vector3(1.5f, 0f, 0.5f), Quaternion.Euler(0f, -40f, 0f), Vector3.one);
            copy.transform.SetPositionAndRotation(moved.GetColumn(3), moved.rotation);
            manager.LateUpdate();
            Assert.That(manager.Writes.Count, Is.EqualTo(2));
            AssertNear(manager.Writes[1].world, moved, "the moved copy");
        }

        [Test]
        public void AnUnauthoredCopyIsWrittenAsItsArrivalWroteIt()
        {
            var managerObject = Inactive("unauthored");
            var manager = managerObject.AddComponent<CopyWriteManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            Matrix4x4 moved = Matrix4x4.TRS(new Vector3(0.5f, 0f, -1f), Quaternion.Euler(0f, 60f, 0f), Vector3.one);
            SbEntity copy = AttachCopy(manager, 4242, moved);
            Assert.That(manager.UpdateReceivedCopyPlacement(copy), Is.True);
            Assert.That(manager.Writes.Count, Is.EqualTo(1));
            Assert.That(manager.Writes[0].element, Is.EqualTo(4242));
            AssertNear(manager.Writes[0].world, moved, "the world pose, as CompleteShare writes an unauthored copy");
        }

        private sealed class CopyWriteManager : SpatialBlendManager
        {
            public readonly List<(long element, Matrix4x4 world)> Writes = new List<(long, Matrix4x4)>();
            protected override void WriteReceivedCopyTransform(long element, Matrix4x4 world) => Writes.Add((element, world));
        }

        private ContainmentManager HostedManager(string name)
        {
            var go = Inactive("received-copy-" + name);
            var manager = go.AddComponent<ContainmentManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            manager.Host(1);
            return manager;
        }

        private SbEntity AttachCopy(SpatialBlendManager manager, long element, Matrix4x4 world, string tag = null)
        {
            var go = Inactive("copy-" + element);
            if (tag != null) go.tag = tag;
            go.transform.SetPositionAndRotation(world.GetColumn(3), world.rotation);
            var entity = go.AddComponent<SbNativeEntity>();
            entity.AllowManipulation = false;
            Attach(entity, manager, element, false);
            return entity;
        }

        private static void Attach(SbEntity entity, SpatialBlendManager manager, long element, bool isSource) =>
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(entity, new object[] { manager, element, isSource });

        private GameObject Inactive(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            _objects.Add(go);
            return go;
        }

        private static SpatialAuthoringPlan CompileChess()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            Assert.That(prefab.GetComponent<SbSpatialAuthoring>().TryBuildDeclaration(out var declaration, out string error), Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error), Is.True, error);
            return plan;
        }

        private static void AssertNear(Matrix4x4 actual, Matrix4x4 expected, string what)
        {
            for (int i = 0; i < 16; ++i)
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-4f), $"{what}: element {i}\n{actual}\n{expected}");
        }
    }
}
#endif
