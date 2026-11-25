// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Which Fusion modes can run with no Photon Cloud. The Freeform app needs
    /// Shared mode (FusionConnectionManager elects its authority through
    /// NetworkRunner.IsSharedModeMasterClient). These tests point every runner
    /// at a loopback port that nothing listens on, with the name server off, so
    /// a run can only succeed if the mode needs no Photon server. They never
    /// contact the cloud and need no App ID of their own.
    /// </summary>
    [TestFixture]
    [Category("FusionHeadlessModes")]
    public class FusionHeadlessModeTests
    {
        private const float TimeoutSeconds = 40f;
        private readonly List<NetworkRunner> _runners = new();
        private readonly List<GameObject> _objects = new();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var runner in _runners.Where(r => r != null))
            {
                var shutdown = runner.Shutdown();
                yield return Wait(shutdown, "runner shutdown");
            }
            _runners.Clear();
            foreach (var go in _objects.Where(o => o != null)) UnityEngine.Object.Destroy(go);
            _objects.Clear();
        }

        [UnityTest]
        public IEnumerator SinglePlayerStartsWithNoServerButIsNotSharedMode()
        {
            var runner = NewRunner("single");
            var start = runner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Single,
                SessionName = "headless-single",
                SceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>(),
                CustomPhotonAppSettings = LoopbackNoServerSettings(),
            });
            yield return Wait(start, "single StartGame");
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            NUnitAssert.That(runner.IsSinglePlayer, Is.True);
            NUnitAssert.That(runner.ActivePlayers.Count(), Is.EqualTo(1));
            NUnitAssert.That(runner.IsSharedModeMasterClient, Is.False,
                "Single is not Shared mode, so FusionConnectionManager would never see itself as authority.");
        }

        [UnityTest]
        public IEnumerator SharedModeFailsWithoutAPhotonServer()
        {
            var runner = NewRunner("shared");
            var start = runner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = "headless-shared",
                SceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>(),
                CustomPhotonAppSettings = LoopbackNoServerSettings(),
            });
            yield return Wait(start, "shared StartGame");
            NUnitAssert.That(start.Result.Ok, Is.False, "Shared mode started with no Photon server reachable.");
            Debug.Log($"FUSION_HEADLESS shared ShutdownReason={start.Result.ShutdownReason}");
        }

        [UnityTest]
        public IEnumerator HostModeFailsWithoutAPhotonServer()
        {
            var host = NewRunner("host");
            var hosted = host.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Host,
                SessionName = "headless-host",
                SceneManager = host.gameObject.AddComponent<NetworkSceneManagerDefault>(),
                CustomPhotonAppSettings = LoopbackNoServerSettings(),
            });
            yield return Wait(hosted, "host StartGame");
            Debug.Log($"FUSION_HEADLESS host Ok={hosted.Result.Ok} ShutdownReason={hosted.Result.ShutdownReason}");
            NUnitAssert.That(hosted.Result.Ok, Is.False, "Host mode started with no Photon server reachable.");
        }

        private NetworkRunner NewRunner(string name)
        {
            var go = new GameObject("fusion-headless-" + name);
            _objects.Add(go);
            var runner = go.AddComponent<NetworkRunner>();
            _runners.Add(runner);
            return runner;
        }

        private static FusionAppSettings LoopbackNoServerSettings()
        {
            var settings = new FusionAppSettings();
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            var port = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            settings.AppIdFusion = Guid.NewGuid().ToString();
            settings.UseNameServer = false;
            settings.Server = "127.0.0.1";
            settings.Port = (ushort)port;
            return settings;
        }

        private static IEnumerator Wait(Task task, string what)
        {
            var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline) NUnitAssert.Fail($"Timed out after {TimeoutSeconds}s: {what}.");
                yield return null;
            }
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }
    }
}
