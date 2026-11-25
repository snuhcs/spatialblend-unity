// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The foundation gate does not copy the networked avatar prefab, so this stays
    /// on the full-project gate. The spawned avatar is MetaAvatarPrefab, referenced
    /// by the scene object "[BuildingBlock] Networked Avatar".
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class NetworkedAvatarControllerPrefabTests
    {
        private const string LeftGuid = "7a748b9c297bc4f4da260ff672c421dd";
        private const string RightGuid = "52986955730fa7641a4e03bd2a740237";
        private const string AvatarPrefabGuid = "ab3a100058ae054478af6124db0bcf7e";
        private const string BehaviourGuid = "8041d3681b6d349e395c2617b2dcf69b";

        [Test]
        public void NetworkedAvatarPrefab_ReferencesV1ControllerPrefabs()
        {
            string scene = Read("SpatialBlend/Samples/Scenes/FreeformCollaboration.unity");
            string prefab = Read("SpatialBlend/Avatar/Prefabs/MetaAvatarPrefab.prefab");
            string leftMeta = Read("SpatialBlend/Avatar/Prefabs/LeftController.prefab.meta");
            string rightMeta = Read("SpatialBlend/Avatar/Prefabs/RightController.prefab.meta");
            string behaviour = Read("SpatialBlend/Avatar/Scripts/SbAvatarBehaviourFusion.cs");
            string entity = Read("SpatialBlend/Avatar/Scripts/SbAvatarEntity.cs");

            Assert.That(scene, Does.Contain("m_Name: '[BuildingBlock] Networked Avatar'"));
            Assert.That(scene, Does.Contain("guid: " + AvatarPrefabGuid));
            Assert.That(prefab, Does.Contain("guid: " + BehaviourGuid));
            Assert.That(prefab, Does.Contain(
                "leftControllerPrefab: {fileID: 3684217668757404445, guid: " + LeftGuid + ", type: 3}"));
            Assert.That(prefab, Does.Contain(
                "rightControllerPrefab: {fileID: 5974288584668434983, guid: " + RightGuid + ", type: 3}"));
            Assert.That(leftMeta, Does.Contain("guid: " + LeftGuid));
            Assert.That(rightMeta, Does.Contain("guid: " + RightGuid));
            Assert.That(Read("SpatialBlend/Avatar/Prefabs/LeftController.prefab"), Does.Contain("m_Name: LeftController"));
            Assert.That(Read("SpatialBlend/Avatar/Prefabs/RightController.prefab"), Does.Contain("m_Name: RightController"));

            Assert.That(entity, Does.Contain("public Action OnSkeletonLoadedCallback;"));
            Assert.That(entity, Does.Contain("OnSkeletonLoadedCallback?.Invoke();"));
            Assert.That(behaviour, Does.Contain("OnSkeletonLoadedCallback += callback"));
        }

        private static string Read(string assetRelativePath)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, assetRelativePath));
        }
    }
}
#endif
