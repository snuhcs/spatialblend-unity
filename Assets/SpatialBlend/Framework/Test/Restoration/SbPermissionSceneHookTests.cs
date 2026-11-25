// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    // The foundation gate does not copy this scene, so this stays on the full-project gate.
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbPermissionSceneHookTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";

        [Test]
        public void FreeformSceneContainsOnePermissionManager()
        {
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.isLoaded;
            if (opened)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            }

            try
            {
                SbPermissionManager[] managers = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<SbPermissionManager>(true))
                    .ToArray();
                Assert.That(managers, Has.Length.EqualTo(1));
                Assert.That(managers[0].gameObject.name, Is.EqualTo("SbPermissionManager"));
            }
            finally
            {
                if (opened && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
#endif
