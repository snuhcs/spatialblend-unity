// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fusion;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Two headsets signed into one Meta account must be able to share a room (verified on the real Photon Cloud:
    /// two clients with one Photon UserId are refused with ErrorCode 32746 "UserId already joined"). That holds because the
    /// product sets no Photon UserId, so Photon gives each runner its own random one. These tests pin it: a UserId may only be
    /// added later if it is unique per install (never the Meta user id or any other per-account value), and the change must
    /// then update this guard on purpose.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class PhotonIdentityGuardTests
    {
        [Test]
        public void TheBootstrapStartsTheRunnerWithoutAuthValues()
        {
            var go = new GameObject("identity-guard-bootstrap");
            try
            {
                var bootstrap = go.AddComponent<SbFusionBootstrap>();
                var args = bootstrap.BuildStartGameArgs(GameMode.Shared, default, default, null, null, null, null);
                NUnitAssert.That(args.AuthValues, Is.Null,
                    "AuthValues.UserId must stay unset (a random per-runner id) or be per install, never the Meta user id: " +
                    "two headsets on one account would be refused by Photon (32746).");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // The spectator builds its StartGameArgs inline and the rest of the product has no other start path, so the whole
        // product source is checked: no Photon auth values or UserId, and no Meta user id feeding the network.
        private static readonly string[] ForbiddenTokens =
        {
            "AuthValues", "AuthenticationValues", "AuthType",
        };

        private static IEnumerable<string> ProductSources()
        {
            string root = Path.Combine(Application.dataPath, "SpatialBlend");
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string normal = path.Replace('\\', '/');
                if (normal.Contains("/Test/") || normal.Contains("/Tests/") || normal.Contains("/Editor/")) continue;
                yield return path;
            }
        }

        [Test]
        public void NoProductCodeSetsPhotonAuthValues()
        {
            var offenders = new List<string>();
            foreach (string path in ProductSources())
            {
                string text = File.ReadAllText(path);
                foreach (string token in ForbiddenTokens)
                    if (text.Contains(token)) offenders.Add(Path.GetFileName(path) + " uses " + token);
            }
            NUnitAssert.That(offenders, Is.Empty,
                "Product code must not set Photon auth values. If a UserId is needed it must be unique per install (a stored " +
                "GUID), never the Meta user id or another per-account value, or same-account headsets cannot join one room.");
        }

        [Test]
        public void TheMetaUserIdIsNeverWrittenToNetworkState()
        {
            // OculusId is the one networked Meta-id field; the spawner must not assign it (avatars are keyed by PlayerRef).
            var offenders = new List<string>();
            foreach (string path in ProductSources())
            {
                string text = File.ReadAllText(path);
                if (text.Contains(".OculusId =") || text.Contains("OculusId=")) offenders.Add(Path.GetFileName(path));
            }
            NUnitAssert.That(offenders, Is.Empty, "Assigning OculusId ties a remote avatar to the Meta account; key by PlayerRef.");
        }
    }
}
#endif
