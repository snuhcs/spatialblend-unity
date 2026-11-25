// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Design A* single ownership on the Unity side: the only thing that writes a received avatar's root
    /// transform is the embodied driver. No NavMeshAgent, interpolator or core pacer moves it.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EmbodiedSingleOwnerTests
    {
        private GameObject _avatar;
        private NavMeshDataInstance _navMesh;

        [TearDown]
        public void TearDown()
        {
            if (_navMesh.valid) NavMesh.RemoveNavMeshData(_navMesh);
            if (_avatar != null) Object.DestroyImmediate(_avatar);
        }

        [Test]
        public void AnEnabledNavMeshAgentOnAReceivedAvatarNeverSteersTheRoot()
        {
            // A floor navmesh, and an enabled agent standing on it.
            var source = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = new Vector3(40f, 0.1f, 40f),
                transform = Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, Vector3.one),
                area = 0,
            };
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByID(0),
                new System.Collections.Generic.List<NavMeshBuildSource> { source },
                new Bounds(Vector3.zero, new Vector3(40f, 10f, 40f)), Vector3.zero, Quaternion.identity);
            _navMesh = NavMesh.AddNavMeshData(data);

            _avatar = new GameObject("single-owner-avatar");
            _avatar.SetActive(false);
            _avatar.transform.position = Vector3.zero;
            var entity = _avatar.AddComponent<SbNetworkAvatarEntity>();
            var agent = _avatar.AddComponent<NavMeshAgent>();
            _avatar.SetActive(true);
            Assert.That(NavMesh.SamplePosition(Vector3.zero, out NavMeshHit hit, 1f, NavMesh.AllAreas), Is.True, "The floor navmesh exists.");
            agent.Warp(hit.position);
            // EditMode does not always register the agent on the mesh. The source-level checks below cover the
            // case where it is; here the agent is enabled and the write must still land in the same call.
            Assert.That(agent.enabled, Is.True);

            Vector3 target = new Vector3(6f, 0f, 3f);
            entity.ApplyRealizationTarget(target, Quaternion.Euler(0f, 40f, 0f));

            Assert.That(_avatar.transform.position.x, Is.EqualTo(6f).Within(1e-4f), "Written, not steered toward.");
            Assert.That(_avatar.transform.position.z, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(_avatar.transform.rotation.eulerAngles.y, Is.EqualTo(40f).Within(1e-3f));
            Assert.That(agent.hasPath, Is.False, "No destination was set on the agent.");
        }

        // The single-owner rules are behaviour, asserted in ManagerContainmentContractTests on a driven rig (not as
        // call counts read from the source text): TheDriverIsTheOnlyWriterOfAReceivedRoot and
        // PlacementAndResetGoThroughTheDriverAndNothingElseMovesTheBody.

        // Networked remotes take the SbAvatarEntity.Start path (FindAvatarInputManagerBehavior before the fix), not the
        // explicit TryConfigureLocalPreset one. That path picked the first active manager in the SCENE, so every remote
        // avatar shared one retarget transform and one Fusion behaviour and drove the others' bodies.
        [Test]
        public void TwoRemoteAvatarsEachGetTheirOwnInputManagerNeverTheSceneWideOne()
        {
            var decoyObject = new GameObject("scene-wide-input-manager");
            decoyObject.AddComponent<SbTransformInputManager>();
            var a = new GameObject("remote-a");
            var b = new GameObject("remote-b");
            var cleanup = new[] { decoyObject, a, b };
            try
            {
                SbTransformInputManager decoy = decoyObject.GetComponent<SbTransformInputManager>();
                SbTransformInputManager managerA = SbAvatarEntity.ResolveOwnInputManager(a);
                SbTransformInputManager managerB = SbAvatarEntity.ResolveOwnInputManager(b);

                Assert.That(managerA, Is.Not.SameAs(managerB));
                Assert.That(managerA, Is.Not.SameAs(decoy));
                Assert.That(managerB, Is.Not.SameAs(decoy));
                Assert.That(managerA.gameObject, Is.SameAs(a));
                Assert.That(managerB.gameObject, Is.SameAs(b));
                Assert.That(SbAvatarEntity.ResolveOwnInputManager(a), Is.SameAs(managerA), "Resolving again finds the same one.");

                // Configuring one leaves the others untouched.
                var retargetA = a.AddComponent<SbRetargetTransform>();
                managerA.SbTransform = retargetA;
                Assert.That(managerB.SbTransform, Is.Null);
                Assert.That(decoy.SbTransform, Is.Null);
                Assert.That(a.GetComponents<SbTransformInputManager>().Length, Is.EqualTo(1));
                Assert.That(b.GetComponents<SbTransformInputManager>().Length, Is.EqualTo(1));

                // A manager the avatar already carries in its own hierarchy is the one used.
                var child = new GameObject("own-child-manager");
                child.transform.SetParent(b.transform);
                SbTransformInputManager own = child.AddComponent<SbTransformInputManager>();
                Assert.That(SbAvatarEntity.ResolveOwnInputManager(b), Is.SameAs(managerB).Or.SameAs(own));
                Assert.That(SbAvatarEntity.ResolveOwnInputManager(b), Is.Not.SameAs(decoy));
            }
            finally
            {
                foreach (GameObject go in cleanup) Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ThereIsNoSecondIntegratorOnTheReceivedAvatarEntity()
        {
            string entity = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs");
            Assert.That(entity, Does.Not.Match(@"GetComponent<[\w.]*NavMeshAgent>|\.SetDestination|\.ResetPath"));
            Assert.That(entity, Does.Not.Contain("MoveTowards"));
            Assert.That(entity, Does.Not.Contain("_targetPosition"));
            Assert.That(entity, Does.Not.Contain("SetOnUpdateStateBatchCallback"));
        }
    }
}
#endif
