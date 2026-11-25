// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarAlphaFadeShaderTests
    {
        private const string ShaderGuid = "358f3e522e0794c4584d943f1ce81ac6";
        private const string ConfigurationGuid = "e1a504b1ee88e0d40a1dd9d0a93130f0";

        [Test]
        public void OcclusionShaderAndConfigurationExposeAlphaFade()
        {
            string shader = Read("SpatialBlend/Avatar/Scripts/Style2Meta_Occlusion.shader");
            string core = Read("SpatialBlend/Avatar/Scripts/Style2MetaAvatarCore_Occlusion.hlsl");
            string app = Read("SpatialBlend/Avatar/Scripts/LocalAvatarAppFunctions.hlsl");
            string configuration = Read("SpatialBlend/Avatar/Scripts/MetaStyle2ShaderOcclusionConfiguration.asset");
            string scene = Read("SpatialBlend/Samples/Scenes/FreeformCollaboration.unity");

            Assert.That(shader, Does.Contain("_AlphaFade (\"Alpha Fade\", Range(0, 1))"));
            Assert.That(shader, Does.Contain("#include \"LocalAvatarAppFunctions.hlsl\""));
            Assert.That(core, Does.Contain("float _AlphaFade;"));
            Assert.That(app, Does.Contain("o.color.a = _AlphaFade;"));
            Assert.That(configuration, Does.Contain("guid: " + ShaderGuid));
            Assert.That(scene, Does.Contain("guid: " + ConfigurationGuid));
        }

        private static string Read(string assetRelativePath)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, assetRelativePath));
        }
    }
}
#endif
