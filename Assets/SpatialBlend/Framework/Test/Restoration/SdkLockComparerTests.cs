// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Editor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>SDKS.lock check: the comparison names each difference, says nothing when all match, and the lock file parses.</summary>
    public sealed class SdkLockComparerTests
    {
        private const string Lock = @"{
  ""schema"": 1,
  ""unity_editor"": { ""version"": ""6000.0.56f1"" },
  ""entries"": [
    { ""name"": ""com.unity.inputsystem"", ""kind"": ""upm"", ""version"": ""1.14.2"", ""required"": true },
    { ""name"": ""com.meta.xr.depthapi"", ""kind"": ""upm-git"", ""version"": ""77.0.0"", ""source"": { ""revision"": ""86633ff2996c98b366395e705d089bfda60ee1a8"" } },
    { ""name"": ""photon-fusion"", ""kind"": ""photon"", ""version"": ""2.0.9 Stable 1566"", ""required"": true }
  ]
}";

        private static SdkInstalled Matching() => new SdkInstalled
        {
            EditorVersion = "6000.0.56f1",
            Packages = new Dictionary<string, string> { ["com.unity.inputsystem"] = "1.14.2" },
            GitRevisions = new Dictionary<string, string> { ["com.meta.xr.depthapi"] = "86633ff2996c98b366395e705d089bfda60ee1a8" },
            FusionBuild = "2.0.9 Stable 1566",
        };

        [Test]
        public void MatchingInstallReportsNothing()
        {
            Assert.That(SdkLockComparer.Compare(JsonUtility.FromJson<SdkLockFile>(Lock), Matching()), Is.Empty);
        }

        [Test]
        public void EachDifferenceIsNamedAndTheWarningNamesTheSetupCommand()
        {
            var installed = Matching();
            installed.EditorVersion = "6000.0.1f1";
            installed.Packages["com.unity.inputsystem"] = "1.13.0";
            installed.GitRevisions["com.meta.xr.depthapi"] = "0000000000000000000000000000000000000000";
            installed.FusionBuild = "2.0.8 Stable 1500";
            var differences = SdkLockComparer.Compare(JsonUtility.FromJson<SdkLockFile>(Lock), installed);
            Assert.That(differences, Has.Count.EqualTo(4));
            Assert.That(string.Join("|", differences), Does.Contain("com.unity.inputsystem").And.Contain("depthapi").And.Contain("Fusion").And.Contain("Unity Editor"));
            Assert.That(SdkLockComparer.Warning(differences), Does.Contain("python3 tools/setup_sdks.py"));
        }

        [Test]
        public void MissingFusionIsReported()
        {
            var installed = Matching();
            installed.FusionBuild = null;
            Assert.That(SdkLockComparer.Compare(JsonUtility.FromJson<SdkLockFile>(Lock), installed), Has.Count.EqualTo(1));
        }
    }
}
