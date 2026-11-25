// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Fusion;
using Fusion.Sockets;
using NUnit.Framework;
using SpatialBlend.Objects;
using SpatialBlend.Samples;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A desktop spectator against a participant, in TWO UNITY PROCESSES over the real Photon Cloud.
    /// Process A, the participant: loads the REAL Freeform scene (so the room's FusionConnectionManager is bound by the same scene
    /// index and NetworkObject order as on a Quest) with the real FreeformCollaborationApp, hosts a room by name as the Freeform
    /// room list does (core Host), and shares one native object, optionally with a source actor.
    /// Process B, the spectator: loads the spectator scene (whose NetworkObjects mirror the Freeform scene's) and joins the room by
    /// name with FreeformSpectatorSession, no avatar. B must receive A's object, leave, join again and receive it again, and refuse
    /// a room that does not exist. A's session must be unaffected throughout: connected, no element, entity or actor from B, its own
    /// object intact, and its roster and link back to what they were once B leaves.
    /// Environment: SPATIALBLEND_TP_ROLE=host|spectator, _ROOM, _MODE=WebRtc|Photon|WebRtcTurn, _DIR, _SOURCE_ACTOR=1.
    /// Run by tools/run-spectator-two-process-test.sh (which makes build index 0 the right scene in each clone); ignored otherwise.
    /// </summary>
    [TestFixture]
    [Category("TwoProcessSpectator")]
    public class FreeformSpectatorTwoProcessCloudTests
    {
        private const string ProbeName = "spectator-probe";
        private NetworkRunner _hostRunner;
        private GameObject _hostRunnerObject;
        private SpatialBlendSettings _settings, _previousSettings;
        private FreeformSpectatorSession _session;
        private SpatialBlendManager _manager;
        private Action _unsubscribe;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            _unsubscribe?.Invoke();
            if (_session != null && _session.Runner != null) { var t = _session.Runner.Shutdown(); while (!t.IsCompleted) yield return null; }
            if (_manager != null) _manager.Disconnect();
            if (_hostRunner != null) { var t = _hostRunner.Shutdown(); while (!t.IsCompleted) yield return null; }
            if (_hostRunnerObject != null) UnityEngine.Object.Destroy(_hostRunnerObject);
            if (_settings != null)
            {
                typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _previousSettings);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        private static string Env(string name) => Environment.GetEnvironmentVariable("SPATIALBLEND_TP_" + name);

        // SPATIALBLEND_TP_PARTICIPANT_VISITS=2: the participant joiner leaves (the runner shut down, the scene reloaded, as the
        // Freeform Leave does) and joins again, and must receive the host's object on both visits.
        private static int ParticipantVisits() => Env("PARTICIPANT_VISITS") == "2" ? 2 : 1;

        // A received scan is made from the app's scan template (ObjectPreset) so the receiver can grab and place it: its collider,
        // the ray interactable on it and the placement motif. A bare copy (the loader's fallback when the template cannot be made)
        // has none of them. By type name: this assembly does not reference the interaction SDK or the Samples.
        private static string MissingGrabParts(GameObject received)
        {
            var missing = new List<string>();
            if (received.GetComponentInChildren<Collider>(true) == null) missing.Add("Collider");
            var types = received.GetComponentsInChildren<Component>(true).Where(c => c != null).Select(c => c.GetType().Name).ToList();
            foreach (string part in new[] { "RayInteractable", "ObjectPlacementMotif" })
                if (!types.Contains(part)) missing.Add(part);
            return string.Join(", ", missing);
        }

        // Every "a bare object is used" error of the loader's template hook, from the joiner's start.
        private static readonly List<string> s_bareCopies = new List<string>();
        private static void NoteBareCopy(string message, string stack, UnityEngine.LogType type)
        {
            if (message != null && message.Contains("a bare object is used")) lock (s_bareCopies) s_bareCopies.Add(message);
        }

        private static void AssertGrabbableCopy(GameObject received, string what)
        {
            string missing = MissingGrabParts(received);
            TestContext.Out.WriteLine($"TWOPROC {what} grabbable={(missing.Length == 0)} missing=[{missing}] bareCopyErrors={s_bareCopies.Count}");
            NUnitAssert.That(missing, Is.Empty, what + ": the received copy is the scan template, grabbable");
            lock (s_bareCopies) NUnitAssert.That(s_bareCopies, Is.Empty, what + ": no copy fell back to a bare object");
        }

        // Phase timing: whenever the snapshot changes, one line with the wall clock (the two processes share it) and the seconds since Zero.
        private static Func<string> s_trace;
        private static string s_traceLast;
        private static string s_traceRole = "";
        private static float s_traceZero;

        private static void Trace()
        {
            if (s_trace == null) return;
            var now = s_trace();
            if (now == s_traceLast) return;
            s_traceLast = now;
            TestContext.Out.WriteLine($"TWOPROC t={Time.realtimeSinceStartup - s_traceZero:0.00} wall={DateTime.Now:HH:mm:ss.fff} role={s_traceRole} {now}");
        }

        private static IEnumerator Until(Func<bool> done, string what, float seconds, Func<string> describe = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done())
            {
                Trace();
                if (Time.realtimeSinceStartup > end) NUnitAssert.Fail($"Timed out after {seconds}s waiting for {what}. {describe?.Invoke()}");
                yield return null;
            }
            Trace();
        }

        private static byte[] ProbeGlb()
        {
            // One triangle: the smallest GLB glTFast accepts.
            var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                       "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]," +
                       "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}]," +
                       "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],\"buffers\":[{\"byteLength\":36}]}";
            var jsonBytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4, ' '));
            var bin = new byte[36];
            Buffer.BlockCopy(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, 0, bin, 0, 36);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + bin.Length));
            w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
            w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin);
            return ms.ToArray();
        }

        private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)); }

        /// <summary>A headless run never opens the user's microphone (BatchModeMicrophoneGuard); checked here in both processes.</summary>
        private static void AssertNoMicrophone(string when) =>
            NUnitAssert.That(BatchModeMicrophoneGuard.NoMicrophoneIsRecording(out var device), Is.True, "a microphone is recording " + when + ": " + device);

        private static int ActorCount(SpatialBlendManager manager) =>
            ((System.Collections.IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager)).Count;

        private IEnumerator LoadSceneAndSettings(string mode)
        {
            // The runner starts from the scene index; tools/run-spectator-two-process-test.sh makes the spectator scene index 0 of each clone.
            var load = EditorSceneManagerLoad();
            yield return load;
            _manager = UnityEngine.Object.FindFirstObjectByType<SpatialBlendManager>();
            NUnitAssert.That(_manager, Is.Not.Null, "the scene has its core manager");
            AssertNoMicrophone("after the scene loaded");
            NUnitAssert.That(SceneManager.GetActiveScene().buildIndex, Is.EqualTo(0), "the spectator scene is build index 0 (Fusion names its scene object by it)");

            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings.useWebRtc = mode != "Photon";
            // The spectator waits for a room to have the players it needs to start its apps before it joins (FreeformSpectatorSession), and
            // this harness has one participant, so the spectator process declares 1. The participant keeps the declared default (2): its
            // apps start when the spectator has joined, as in the earlier runs. (With the participant declaring 1 as well, its apps
            // start alone, and in the one run made so a later joiner linked and started its app but received no content; that is the
            // late-joiner behaviour the rejoin fix branch is about, not something this test asserts.)
            // Since the minimap the spectator is a declared desktop observer, so the host no longer counts it toward its start:
            // with one participant, the participant declares 1 too, or its apps would never start. A participant joiner (the comparison
            // mode) is a participant and keeps the default with the host.
            if (Env("ROLE") != "host" || Env("JOINER") != "participant") _settings.playersToStartApps = 1;
            if (mode == "WebRtcTurn")
            {
                NUnitAssert.That(TurnClientSecret.Load(), Is.Not.Null.And.Not.Empty, "WebRtcTurn needs the TURN client secret (" + TurnClientSecret.FileEnvironmentVariable + ")");
                if (TurnIceService.Current == null) TurnIceService.Prefetch(_settings);
                yield return Until(() => TurnIceService.Current != null && TurnIceService.Current.State == TurnIceProvider.FetchState.Ready, "the TURN prefetch", 60);
            }
            _previousSettings = SpatialBlendManager.CurrentSettings;
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);
        }

        private static AsyncOperation EditorSceneManagerLoad() =>
            SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);

        [UnityTest, Timeout(1800000)]
        public IEnumerator OneSideOfTheSpectatorTest()
        {
            var role = Env("ROLE");
            if (string.IsNullOrEmpty(role)) NUnitAssert.Ignore("Not a two-process spectator run.");
            var room = Env("ROOM"); var mode = Env("MODE"); var dir = Env("DIR");
            if (!Fusion.Photon.Realtime.PhotonAppSettings.TryGetGlobal(out var photon) || !Guid.TryParse(photon.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            // The Meta Avatar SDK has no macOS binary: its manager logs an error when something touches it; that is not a result here.
            LogAssert.ignoreFailingMessages = true;
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;
            yield return LoadSceneAndSettings(mode);
            s_traceRole = role; s_traceZero = Time.realtimeSinceStartup; s_traceLast = null; s_trace = null;
            s_bareCopies.Clear();
            Application.logMessageReceived += NoteBareCopy;
            try
            {
                if (role == "host") yield return RunHost(room, dir);
                else if (Env("JOINER") == "participant") yield return RunParticipantJoiner(room, dir);
                else yield return RunSpectator(room, dir);
            }
            finally { Application.logMessageReceived -= NoteBareCopy; }
            LogAssert.ignoreFailingMessages = true;
        }

        // ---------------------------------------------------------------------------------------------------------------
        private IEnumerator RunHost(string room, string dir)
        {
            // The participant is the real Freeform scene with the real Freeform app (the harness switches the scene's serialized
            // Foundation Local profile, which only the Editor honours, to the Fusion profile a player always runs).
            NUnitAssert.That(SceneManager.GetActiveScene().name, Is.EqualTo("FreeformCollaboration"));
            var app = UnityEngine.Object.FindFirstObjectByType<FreeformCollaborationApp>();
            NUnitAssert.That(app, Is.Not.Null, "the Freeform scene's app");
            NUnitAssert.That(app.UsesFoundationLocal, Is.False, "the participant runs the Fusion profile");
            var connection = UnityEngine.Object.FindFirstObjectByType<FusionConnectionManager>();
            NUnitAssert.That(connection, Is.Not.Null, "the Freeform scene's connection manager");
            int remoteObjects = 0, remoteAvatars = 0;
            Action<GameObject> onObject = g => remoteObjects++;
            Action<GameObject, PlayerRef> onAvatar = (g, p) => remoteAvatars++;
            FreeformCollaborationApp.OnRemoteObjectAdded += onObject;
            FreeformCollaborationApp.OnRemoteAvatarAdded += onAvatar;
            _unsubscribe = () => { FreeformCollaborationApp.OnRemoteObjectAdded -= onObject; FreeformCollaborationApp.OnRemoteAvatarAdded -= onAvatar; };

            // As the Freeform room list does: hosting a free name makes this player the core host.
            var matchmaking = new FreeformMatchmaking();
            matchmaking.Refresh(new MatchmakingRoom[0]);
            var decision = matchmaking.TryHost(room);
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.Host));
            connection.AdoptMatchmakingDecision(decision);

            _hostRunnerObject = new GameObject("participant-runner");
            _hostRunner = _hostRunnerObject.AddComponent<NetworkRunner>();
            _hostRunner.AddCallbacks(new Events(connection));
            var sceneInfo = new NetworkSceneInfo();
            sceneInfo.AddSceneRef(SceneRef.FromIndex(0), LoadSceneMode.Additive);
            var start = _hostRunner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, Scene = sceneInfo,
                SceneManager = _hostRunnerObject.AddComponent<NetworkSceneManagerDefault>(),
                ObjectProvider = _hostRunnerObject.AddComponent<NetworkObjectProviderDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 240);
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            NUnitAssert.That(_hostRunner.IsSharedModeMasterClient, Is.True);
            TestContext.Out.WriteLine($"TWOPROC role=host pid={System.Diagnostics.Process.GetCurrentProcess().Id} mode={Env("MODE")} scene={SceneManager.GetActiveScene().name}");
            Func<string> describe = () => $"state={_manager.ConnectionState} role={connection.ParticipantRole} linked={connection.LinkedPeerCount} players={_hostRunner.ActivePlayers.Count()} " +
                $"carrier={connection.ActiveCarrier} appStarted={app.IsAppStarted} connObj={(connection.Object != null && connection.Object.IsValid)} connRunner={(connection.Runner == _hostRunner)} entities={_manager.Entities.Count}";
            s_traceZero = Time.realtimeSinceStartup;
            s_trace = () => $"{describe()} epoch={connection.CurrentEpoch}";
            // The scene object is spawned when the runner has loaded the scene. The host's own player-joined event came before it
            // existed, so it is handed over now (the same call as the existing link test; the round logic is idempotent).
            yield return Until(() => connection.Object != null && connection.Object.IsValid && connection.Runner == _hostRunner, "the scene's connection manager to be spawned", 60, describe);
            connection.OnPlayerJoined(_hostRunner, _hostRunner.LocalPlayer);
            yield return Until(() => _manager.ConnectionState == SpatialBlendConnectionState.Connected, "the host's own core", 60, describe);
            File.WriteAllText(Path.Combine(dir, "host-ready"), "1");

            // The spectator joins by name; the host does nothing for it.
            yield return Until(() => _hostRunner.ActivePlayers.Count() == 2, "the spectator to join the room", 600, describe);
            yield return Until(() => connection.LinkedPeerCount == 1 && app.IsAppStarted, "the core link to the spectator and the app start", 120, describe);
            TestContext.Out.WriteLine("TWOPROC role=host spectator linked " + describe());

            // The participant's content: one native object, optionally with a source actor on it.
            var loader = NativeLoader.GetLoader(NativeContentType.GLTF);
            var probe = loader.CreateTemplate(ProbeName);
            var entity = probe.GetComponent<SbNativeEntity>();
            var glb = ProbeGlb();
            entity.SetContentData(glb);
            app.RegisterGameObject(probe);
            yield return Until(() => entity.IsAttached(), "the probe to be registered", 30, describe);
            if (Env("SOURCE_ACTOR") == "1") app.RegisterAnchor(probe, "probe", Vector3.zero);
            yield return new WaitForSeconds(2f);
            long probeId = entity.ID;
            TestContext.Out.WriteLine($"TWOPROC role=host shared probe id={probeId} entities={_manager.Entities.Count} actors={ActorCount(_manager)} bytes={glb.Length} hash={Hash(glb).Substring(0, 8)}");
            File.WriteAllText(Path.Combine(dir, "host-shared"), Hash(glb));

            // Two visits by the spectator, with a leave between them. Each time A must be exactly what it was.
            int visits = Env("JOINER") == "participant" ? ParticipantVisits() : 2;
            for (int visit = 1; visit <= visits; visit++)
            {
                yield return Until(() => File.Exists(Path.Combine(dir, $"spectator-received-{visit}")), $"the spectator to receive the probe (visit {visit})", 240, describe);
                int entitiesBefore = _manager.Entities.Count, actorsBefore = ActorCount(_manager);
                yield return new WaitForSeconds(3f);
                NUnitAssert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), describe());
                NUnitAssert.That(_manager.IsHost(), Is.True);
                NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Host));
                NUnitAssert.That(connection.HostLeft, Is.False);
                NUnitAssert.That(connection.LinkedPeerCount, Is.EqualTo(1), "one link: the spectator's");
                NUnitAssert.That(_hostRunner.ActivePlayers.Count(), Is.EqualTo(2));
                if (Env("JOINER") != "participant")
                {
                    NUnitAssert.That(remoteObjects, Is.EqualTo(0), "nothing was received from the spectator");
                    NUnitAssert.That(remoteAvatars, Is.EqualTo(0), "no avatar came from the spectator");
                }
                NUnitAssert.That(_manager.Entities.Count, Is.EqualTo(entitiesBefore), "no entity added by the spectator");
                NUnitAssert.That(ActorCount(_manager), Is.EqualTo(actorsBefore), "no actor added by the spectator");
                NUnitAssert.That(entity.IsAttached() && entity.ID == probeId && entity.IsSource, Is.True, "the participant's own object is intact");
                File.WriteAllText(Path.Combine(dir, $"host-verified-{visit}"), "1");
                if (visit == 1 && visits > 1)
                {
                    // The spectator leaves; the participant is back to a room of one with no link.
                    yield return Until(() => _hostRunner.ActivePlayers.Count() == 1, "the spectator to leave", 120, describe);
                    yield return Until(() => connection.LinkedPeerCount == 0, "the spectator's link to be closed", 60, describe);
                    yield return new WaitForSeconds(2f);
                    NUnitAssert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), describe());
                    NUnitAssert.That(connection.HostLeft, Is.False);
                    NUnitAssert.That(entity.IsAttached(), Is.True);
                    File.WriteAllText(Path.Combine(dir, "host-saw-leave"), "1");
                    yield return Until(() => _hostRunner.ActivePlayers.Count() == 2 && connection.LinkedPeerCount == 1, "the spectator to join again", 240, describe);
                }
            }

            yield return Until(() => File.Exists(Path.Combine(dir, "spectator-done")), "the spectator to finish", 240, describe);
            yield return Until(() => _hostRunner.ActivePlayers.Count() == 1 && connection.LinkedPeerCount == 0, "the spectator to be gone", 120, describe);
            yield return new WaitForSeconds(2f);
            NUnitAssert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), describe());
            NUnitAssert.That(connection.HostLeft, Is.False);
            NUnitAssert.That(entity.IsAttached(), Is.True);
            AssertNoMicrophone("at the end of the participant's run");
            TestContext.Out.WriteLine("TWOPROC role=host DONE " + describe());
        }

        // ---------------------------------------------------------------------------------------------------------------
        private IEnumerator RunSpectator(string room, string dir)
        {
            NUnitAssert.That(SceneManager.GetActiveScene().name, Is.EqualTo("FreeformSpectator"));
            _session = UnityEngine.Object.FindFirstObjectByType<FreeformSpectatorSession>();
            NUnitAssert.That(_session, Is.Not.Null);
            var connection = _session.Connection;
            var app = _session.App;
            yield return Until(() => File.Exists(Path.Combine(dir, "host-ready")), "the host to be in the room", 600);
            TestContext.Out.WriteLine($"TWOPROC role=spectator pid={System.Diagnostics.Process.GetCurrentProcess().Id} mode={Env("MODE")}");
            Func<string> describe = () => $"phase={_session.Phase} err={_session.Error} {_session.StatusLine()} linked={connection.LinkedPeerCount} text=({connection.CarrierText})";
            // After a runner has been shut down the session reloads the scene (Fusion consumes the scene's NetworkObjects); the
            // new scene brings a new session, manager, connection manager and app, and the declared settings are applied again.
            for (int visit = 1; visit <= 2; visit++)
            {
                s_traceZero = Time.realtimeSinceStartup;
                s_trace = () => $"{describe()} connObj={(connection.Object != null && connection.Object.IsValid)} epoch={connection.CurrentEpoch} appStarted={app.IsAppStarted} received={app.ReceivedCount}";
                _session.Join(room);
                yield return Until(() => _session.Phase == SpectatorPhase.Spectating || _session.Phase == SpectatorPhase.Failed, $"the spectator to join (visit {visit})", 120, describe);
                NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating), describe());
                NUnitAssert.That(_session.Runner.IsSharedModeMasterClient, Is.False, "a spectator is not the room's master");
                yield return Until(() => _manager.ConnectionState == SpatialBlendConnectionState.Connected && connection.LinkedPeerCount == 1, "the spectator's core link", 120, describe);
                TestContext.Out.WriteLine($"TWOPROC role=spectator visit={visit} linked " + describe());
                NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
                NUnitAssert.That(_manager.IsHost(), Is.False);
                if (visit == 1) yield return Until(() => app.IsAppStarted, "the spectator app to start", 120, describe);
                else
                {
                    // A second visit in the same room: it joins again, its core link comes up as a Client, it registers nothing,
                    // the host is unaffected (the host asserts that), and the room's content reaches it again, the participant's
                    // native object included. This guards a past failure: the received copy is made from the
                    // app's scan template, and the scan loader made after the Leave's scene reload had no preset key, so making
                    // the template threw inside Addressables and the share was dropped (GltfLoader.LoadSpatialBlendSettings).
                    float wait = Time.realtimeSinceStartup + 90f;
                    while (Time.realtimeSinceStartup < wait && !(app.IsAppStarted && app.ReceivedObjects.Any(g => g != null && g.name == ProbeName))) { Trace(); yield return null; }
                    TestContext.Out.WriteLine($"TWOPROC role=spectator visit={visit} rejoin: app_started={app.IsAppStarted} content_received={app.ReceivedObjects.Any(g => g != null && g.name == ProbeName)} entities={_manager.Entities.Count} linked={connection.LinkedPeerCount} state={_manager.ConnectionState}");
                    NUnitAssert.That(app.ReceivedObjects.Any(g => g != null && g.name == ProbeName), Is.True,
                        "the participant's object reached the re-joined spectator. " + describe());
                    AssertGrabbableCopy(app.ReceivedObjects.First(g => g != null && g.name == ProbeName), $"role=spectator visit={visit} rejoin");
                    NUnitAssert.That(_manager.Entities.Any(e => e.IsSource), Is.False, "no source entity on the re-joined spectator");
                    NUnitAssert.That(ActorCount(_manager), Is.EqualTo(0), "no actor on the re-joined spectator");
                    File.WriteAllText(Path.Combine(dir, $"spectator-received-{visit}"), "rejoined");
                    yield return Until(() => File.Exists(Path.Combine(dir, $"host-verified-{visit}")), "the host to verify its session", 240, describe);
                    break;
                }
                yield return Until(() => File.Exists(Path.Combine(dir, "host-shared")), "the host to share", 240, describe);
                yield return Until(() => app.ReceivedObjects.Any(g => g != null && g.name == ProbeName), "the participant's object", 240, describe);
                var received = app.ReceivedObjects.First(g => g != null && g.name == ProbeName);
                var native = received.GetComponent<SbNativeEntity>();
                NUnitAssert.That(native, Is.Not.Null);
                NUnitAssert.That(Hash(native.GetContentData()), Is.EqualTo(File.ReadAllText(Path.Combine(dir, "host-shared"))), "the content arrived byte for byte");
                NUnitAssert.That(native.IsSource, Is.False);
                // It registered nothing of its own.
                NUnitAssert.That(_manager.Entities.Any(e => e.IsSource), Is.False, "no source entity on the spectator");
                NUnitAssert.That(ActorCount(_manager), Is.EqualTo(0), "no actor on the spectator");
                NUnitAssert.That(UnityEngine.Object.FindObjectsByType<SbNetworkAvatarEntity>(FindObjectsSortMode.None).Length, Is.EqualTo(0));
                // The glTF is loaded asynchronously: note whether it rendered; the received bytes are what is asserted.
                float until = Time.realtimeSinceStartup + 20f;
                while (received != null && received.GetComponentsInChildren<MeshRenderer>().Length == 0 && Time.realtimeSinceStartup < until) yield return null;
                TestContext.Out.WriteLine($"TWOPROC role=spectator visit={visit} received probe '{received.name}' bytes={native.GetContentData().Length} renderers={received.GetComponentsInChildren<Renderer>().Length} " +
                    $"entities={_manager.Entities.Count} actors={ActorCount(_manager)} carrier={connection.ActiveCarrier} path={connection.ActivePath}");
                File.WriteAllText(Path.Combine(dir, $"spectator-received-{visit}"), "1");
                yield return Until(() => File.Exists(Path.Combine(dir, $"host-verified-{visit}")), "the host to verify its session", 240, describe);
                if (visit == 1)
                {
                    var old = _session;
                    _session.Leave();
                    yield return Until(() => _session.Phase == SpectatorPhase.Idle && _session.Runner == null, "the spectator to leave", 60, describe);
                    NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None), "the core connection was told the spectator left");
                    yield return Until(() => { var s2 = UnityEngine.Object.FindFirstObjectByType<FreeformSpectatorSession>(); return s2 != null && s2 != old; }, "the scene to reload after the leave", 60, describe);
                    _session = UnityEngine.Object.FindFirstObjectByType<FreeformSpectatorSession>();
                    _manager = UnityEngine.Object.FindFirstObjectByType<SpatialBlendManager>();
                    typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);
                    connection = _session.Connection;
                    app = _session.App;
                    NUnitAssert.That(connection != null, Is.True, "the reloaded scene has its connection manager");
                    yield return Until(() => File.Exists(Path.Combine(dir, "host-saw-leave")), "the host to see the spectator leave", 240, describe);
                }
            }

            // Leave again, then a room that does not exist: refused with the message, and nothing is hosted.
            var old2 = _session;
            _session.Leave();
            yield return Until(() => _session.Phase == SpectatorPhase.Idle && _session.Runner == null, "the spectator to leave", 60, describe);
            yield return Until(() => { var s2 = UnityEngine.Object.FindFirstObjectByType<FreeformSpectatorSession>(); return s2 != null && s2 != old2; }, "the scene to reload after the leave", 60, describe);
            _session = UnityEngine.Object.FindFirstObjectByType<FreeformSpectatorSession>();
            _manager = UnityEngine.Object.FindFirstObjectByType<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);
            typeof(FreeformSpectatorSession).GetField("_roomWaitSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_session, 8f);
            var missing = "spatialblend-no-such-room-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _session.Join(missing);
            yield return Until(() => _session.Phase == SpectatorPhase.Failed, "the missing room to be refused", 60, describe);
            NUnitAssert.That(_session.Error, Does.Contain(SpectatorJoinPolicy.NotFoundError.Substring(0, 30)));
            NUnitAssert.That(_session.Runner, Is.Null, "no runner was started for a room that does not exist");
            TestContext.Out.WriteLine("TWOPROC role=spectator refused a missing room: " + _session.Error);

            // What Fusion's Shared mode does with a start on a room nobody made: the list check above is what keeps a spectator
            // from this, and the session's CreatedRoom check is the net under it. Either Fusion refuses (EnableClientSessionCreation
            // false) or it creates the room, and then this runner is alone in it as the master.
            var startCore = typeof(FreeformSpectatorSession).GetMethod("StartRunnerCore", BindingFlags.Instance | BindingFlags.NonPublic);
            var shutdownCore = typeof(FreeformSpectatorSession).GetMethod("ShutdownRunnerCore", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (System.Threading.Tasks.Task<FreeformSpectatorSession.RunnerStart>)startCore.Invoke(_session, new object[] { missing + "-direct" });
            yield return Until(() => task.IsCompleted, "the direct start of a missing room", 60, describe);
            var outcome = task.Result;
            TestContext.Out.WriteLine($"TWOPROC role=spectator direct start of a missing room: ok={outcome.Ok} createdRoom={outcome.CreatedRoom} error={outcome.Error}");
            NUnitAssert.That(outcome.Ok, Is.False, "Fusion refuses a start on a room nobody made (EnableClientSessionCreation=false): GameNotFound, not a created room");
            var down = (System.Threading.Tasks.Task)shutdownCore.Invoke(_session, null);
            yield return Until(() => down.IsCompleted, "the runner shutdown", 60, describe);
            File.WriteAllText(Path.Combine(dir, "spectator-done"), "1");
            AssertNoMicrophone("at the end of the spectator's run");
            TestContext.Out.WriteLine("TWOPROC role=spectator DONE");
        }

        // ---------------------------------------------------------------------------------------------------------------
        /// <summary>
        /// A normal FC participant joining the same room in the same harness, for comparison with the spectator: the real Freeform
        /// scene and app, the Client role, the same runner start, no session component. Timed with the same trace.
        /// </summary>
        private IEnumerator RunParticipantJoiner(string room, string dir)
        {
            yield return Until(() => File.Exists(Path.Combine(dir, "host-ready")), "the host to be in the room", 600);
            TestContext.Out.WriteLine($"TWOPROC role=participant-joiner pid={System.Diagnostics.Process.GetCurrentProcess().Id} mode={Env("MODE")}");
            int visits = ParticipantVisits();
            for (int visit = 1; visit <= visits; visit++)
            {
                if (visit > 1)
                {
                    // Leave as the Freeform Leave does: the runner is shut down and the scene reloads (Fusion consumed the scene's
                    // NetworkObjects); the new scene brings a new manager, connection manager and app.
                    var leaving = _hostRunner;
                    var down = leaving.Shutdown();
                    yield return Until(() => down.IsCompleted, "the participant's runner shutdown", 60);
                    if (_hostRunnerObject != null) UnityEngine.Object.Destroy(_hostRunnerObject);
                    _hostRunner = null; _hostRunnerObject = null;
                    var reload = SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);
                    yield return Until(() => reload.isDone, "the scene to reload after the leave", 60);
                    _manager = UnityEngine.Object.FindFirstObjectByType<SpatialBlendManager>();
                    typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);
                    yield return Until(() => File.Exists(Path.Combine(dir, "host-saw-leave")), "the host to see the participant leave", 240);
                }
                yield return JoinAsParticipant(room, dir, visit);
            }
            AssertNoMicrophone("at the end of the participant-joiner's run");
            File.WriteAllText(Path.Combine(dir, "spectator-done"), "1");
        }

        private IEnumerator JoinAsParticipant(string room, string dir, int visit)
        {
            NUnitAssert.That(SceneManager.GetActiveScene().name, Is.EqualTo("FreeformCollaboration"));
            var app = UnityEngine.Object.FindFirstObjectByType<FreeformCollaborationApp>();
            NUnitAssert.That(app.UsesFoundationLocal, Is.False);
            var connection = UnityEngine.Object.FindFirstObjectByType<FusionConnectionManager>();
            Func<string> describe = () => $"visit={visit} state={_manager.ConnectionState} role={connection.ParticipantRole} linked={connection.LinkedPeerCount} players={(_hostRunner != null && _hostRunner.IsRunning ? _hostRunner.ActivePlayers.Count() : 0)} " +
                $"carrier={connection.ActiveCarrier} appStarted={app.IsAppStarted} connObj={(connection.Object != null && connection.Object.IsValid)} epoch={connection.CurrentEpoch} entities={_manager.Entities.Count}";
            s_traceZero = Time.realtimeSinceStartup;
            s_trace = describe;
            var matchmaking = new FreeformMatchmaking();
            matchmaking.Refresh(new[] { new MatchmakingRoom(room, 1, SpatialBlendSettings.DefaultMaxPlayers) });
            var decision = matchmaking.TryJoin(room);
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.Client));
            connection.AdoptMatchmakingDecision(decision);
            _hostRunnerObject = new GameObject("participant-joiner-runner");
            _hostRunner = _hostRunnerObject.AddComponent<NetworkRunner>();
            _hostRunner.AddCallbacks(new Events(connection));
            var sceneInfo = new NetworkSceneInfo();
            sceneInfo.AddSceneRef(SceneRef.FromIndex(0), LoadSceneMode.Additive);
            var start = _hostRunner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, Scene = sceneInfo,
                SceneManager = _hostRunnerObject.AddComponent<NetworkSceneManagerDefault>(),
                ObjectProvider = _hostRunnerObject.AddComponent<NetworkObjectProviderDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 240, describe);
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            float linkStart = Time.realtimeSinceStartup;
            yield return Until(() => _manager.ConnectionState == SpatialBlendConnectionState.Connected && connection.LinkedPeerCount == 1 && app.IsAppStarted, "the participant's core link and app start", 120, describe);
            TestContext.Out.WriteLine($"TWOPROC role=participant-joiner visit={visit} linked {Time.realtimeSinceStartup - s_traceZero:0.00}s after the join call, {Time.realtimeSinceStartup - linkStart:0.00}s after StartGame returned");
            if (ParticipantVisits() > 1)
            {
                // The host's object, on every visit: shared after the link on the first, already in the room on a rejoin.
                if (visit == 1) yield return Until(() => File.Exists(Path.Combine(dir, "host-shared")), "the host to share", 240, describe);
                Func<SbNativeEntity> probe = () => _manager.Entities.OfType<SbNativeEntity>()
                    .FirstOrDefault(e => e != null && e.gameObject.name == ProbeName && !e.IsSource);
                float wait = Time.realtimeSinceStartup + 60f;
                while (probe() == null && Time.realtimeSinceStartup < wait) { Trace(); yield return null; }
                var received = probe();
                TestContext.Out.WriteLine($"TWOPROC role=participant-joiner visit={visit} {(visit > 1 ? "rejoin: " : "")}app_started={app.IsAppStarted} " +
                    $"content_received={received != null} entities={_manager.Entities.Count} linked={connection.LinkedPeerCount} state={_manager.ConnectionState}");
                NUnitAssert.That(received, Is.Not.Null, $"the host's object reached the participant (visit {visit}). {describe()}");
                NUnitAssert.That(Hash(received.GetContentData()), Is.EqualTo(File.ReadAllText(Path.Combine(dir, "host-shared"))), "the content arrived byte for byte");
                AssertGrabbableCopy(received.gameObject, $"role=participant-joiner visit={visit}");
            }
            File.WriteAllText(Path.Combine(dir, $"spectator-received-{visit}"), "1");
            yield return Until(() => File.Exists(Path.Combine(dir, $"host-verified-{visit}")), "the host", 240, describe);
        }

        private sealed class Events : INetworkRunnerCallbacks
        {
            private readonly FusionConnectionManager _connection;
            public Events(FusionConnectionManager connection) { _connection = connection; }
            public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) => _connection?.OnPlayerJoined(runner, player);
            public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) => _connection?.OnPlayerLeft(runner, player);
            public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) => _connection?.ShutdownConnection();
            public void OnInput(NetworkRunner runner, NetworkInput input) { }
            public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
            public void OnConnectedToServer(NetworkRunner runner) { }
            public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
            public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
            public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
            public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
            public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
            public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
            public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
            public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
            public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
            public void OnSceneLoadDone(NetworkRunner runner) { }
            public void OnSceneLoadStart(NetworkRunner runner) { }
            public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
            public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        }
    }
}
#endif
