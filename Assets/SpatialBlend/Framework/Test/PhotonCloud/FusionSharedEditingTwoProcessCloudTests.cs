// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using MRMotifs.SharedActivities.ChessSample;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Shared editing between TWO UNITY PROCESSES over the real Photon Cloud. The host (A) and the joiner (B) each have the
    /// product's Chess (the real prefab: its NetworkObject, ChessBoardHandlerMotif, SbNetworkEntity and authoring), spawned by A and
    /// placed by each in its own room, and the product's core link (FusionConnectionManager, SpatialBlendManager, the native core).
    ///
    /// Measured, in order:
    /// 1. B moves its copy of the Chess: the move reaches B's own space (its region follows), and A's board and region do not move
    ///    (each room places its own copy; the core carries no object pose between spaces).
    /// 2. Refuse. A grabs a piece; B's grab of another piece is refused on the spot and names A. A drops: the lease is released once
    ///    the piece rests, and B sees the dropped pose. B then gets the lease, and A is refused.
    /// 3. Race. Both grab the same free piece at the same wall-clock instant: exactly one is granted, the other is refused.
    /// 4. Leave. B holds the lease and leaves the room: A sees the lease free and gets it.
    /// Grabs are the motif's own select/unselect entry points (BeginPieceGrab/EndPieceGrab, what the pieces' ISDK events call): there is
    /// no hand or controller in batchmode. Numbers are printed as TWOPROC lines.
    /// Environment: SPATIALBLEND_TP_ROLE=host|joiner, _ROOM, _MODE, _DIR, _SCENARIO=edit. Run by tools/run-two-process-cloud-test.sh.
    /// </summary>
    [TestFixture]
    [Category("TwoProcessCloud")]
    public class FusionSharedEditingTwoProcessCloudTests
    {
        private const string ChessPrefabPath = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private static readonly Matrix4x4 ChessA = Matrix4x4.TRS(new Vector3(2.0f, 0f, 1.5f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
        private static readonly Matrix4x4 ChessB = Matrix4x4.TRS(new Vector3(-1.0f, 0f, 3.0f), Quaternion.Euler(0f, 110f, 0f), Vector3.one);
        private static readonly Matrix4x4 ChessBMoved = Matrix4x4.TRS(new Vector3(0.2f, 0f, 3.8f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);

        private NetworkRunner _runner;
        private GameObject _managerObject, _runnerObject, _appObject;
        private SpatialBlendSettings _settings, _previousSettings;
        private readonly List<GameObject> _scratch = new List<GameObject>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            if (_managerObject != null) { _managerObject.GetComponent<SpatialBlendManager>()?.Disconnect(); UnityEngine.Object.Destroy(_managerObject); }
            if (_runner != null && _runner.IsRunning) { var t = _runner.Shutdown(); while (!t.IsCompleted) yield return null; }
            foreach (var o in new[] { _runnerObject, _appObject }) if (o != null) UnityEngine.Object.Destroy(o);
            foreach (var o in _scratch) if (o != null) UnityEngine.Object.Destroy(o);
            if (_settings != null)
            {
                typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _previousSettings);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        private static string Env(string name) => Environment.GetEnvironmentVariable("SPATIALBLEND_TP_" + name);
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static IEnumerator Until(Func<bool> done, string what, float seconds, Func<string> describe = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > end) NUnitAssert.Fail($"Timed out after {seconds}s waiting for {what}. {describe?.Invoke()}");
                yield return null;
            }
        }

        private string _dir;
        private void Mark(string name, string value = "1") => File.WriteAllText(Path.Combine(_dir, name), value);
        private bool Has(string name) => File.Exists(Path.Combine(_dir, name));
        private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name));

        private sealed class EditApp : SbApp
        {
            public readonly List<GameObject> Added = new List<GameObject>();
            private bool _started;
            public override void OnAppStart() { _started = true; }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType type) { if (type == OnObjectAddedType.Added) Added.Add(gameObject); }
            public override void OnRemove(GameObject gameObject) { }
            public override bool IsAppStarted => _started;
        }

        // Every replica of an inactive template is inactive; the core matches a replica by NetworkId among ACTIVE objects.
        private sealed class ReplicaActivator : MonoBehaviour
        {
            private void Update() => ActivateReplicas();
        }

        private static void ActivateReplicas()
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (o.gameObject.activeSelf || !o.IsValid || o.GetComponentInChildren<ChessBoardHandlerMotif>(true) == null) continue;
                o.gameObject.SetActive(true);
                TestContext.Out.WriteLine($"TWOPROC activated replica {o.name} id={o.Id} active={o.gameObject.activeInHierarchy}");
            }
        }

        private static string NetworkObjects() => "objects=" + string.Join(";", UnityEngine.Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Select(o => $"{o.name}:active={o.gameObject.activeInHierarchy}:valid={o.IsValid}:id={(o.IsValid ? o.Id.ToString() : "-")}"));

        private static Matrix4x4 RegionFrame(SpatialBlendManager manager, long objectElement)
        {
            var states = (IDictionary)typeof(SpatialBlendManager).GetField("_authoredStateByObject", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            object state = states[objectElement];
            return (Matrix4x4)state.GetType().GetField("RegionFrameWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(state);
        }

        private static double MaxAbs(Matrix4x4 a, Matrix4x4 b)
        {
            double m = 0;
            for (int i = 0; i < 16; ++i) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        [UnityTest, Timeout(1200000)]
        public IEnumerator OneSideOfTheSharedEditingRun()
        {
            var role = Env("ROLE");
            if (string.IsNullOrEmpty(role) || Env("SCENARIO") != "edit") NUnitAssert.Ignore("Not a two-process shared-editing run.");
            var room = Env("ROOM"); var mode = Env("MODE"); _dir = Env("DIR");
            bool host = role == "host";
            string other = host ? "joiner" : "host";
            if (!PhotonAppSettings.TryGetGlobal(out var photon) || !Guid.TryParse(photon.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            LogAssert.ignoreFailingMessages = true;   // the Interaction SDK and Meta blocks log their missing rig in batchmode
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;
            Application.targetFrameRate = 60;

            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings.useWebRtc = mode != "Photon";
            _previousSettings = SpatialBlendManager.CurrentSettings;
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);

            _managerObject = new GameObject("tp-core-" + role); _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            _managerObject.SetActive(true);

            _runnerObject = new GameObject("tp-runner-" + role);
            _runner = _runnerObject.AddComponent<NetworkRunner>();
            FusionConnectionManager connection = null;
            var left = new List<PlayerRef>();
            _runner.AddCallbacks(new Events(
                (r, p) => connection?.OnPlayerJoined(r, p), (r, p) => { left.Add(p); connection?.OnPlayerLeft(r, p); }, () => connection?.ShutdownConnection()));
            var connTemplate = new GameObject("FusionConnectionManagerTemplate"); connTemplate.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(connTemplate);
            connTemplate.AddComponent<NetworkObject>();
            var connTemplate2 = connTemplate.AddComponent<FusionConnectionManager>();
            typeof(FusionConnectionManager).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connTemplate2, manager);
            new NetworkObjectBaker().Bake(connTemplate);
            // The product's Chess, as a spawnable template (inactive, so no replica wakes before it is placed).
            var chessPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ChessPrefabPath);
            NUnitAssert.That(chessPrefab, Is.Not.Null, ChessPrefabPath);
            // Instantiated under an inactive holder and deactivated before it is taken out, so none of its components wakes.
            var holder = new GameObject("tp-chess-holder"); holder.SetActive(false); _scratch.Add(holder);
            var chessTemplate = UnityEngine.Object.Instantiate(chessPrefab, holder.transform);
            chessTemplate.SetActive(false);
            chessTemplate.transform.SetParent(null, false);
            chessTemplate.name = "Chess";
            UnityEngine.Object.DontDestroyOnLoad(chessTemplate);
            new NetworkObjectBaker().Bake(chessTemplate);
            NUnitAssert.That(chessTemplate.GetComponent<NetworkObject>().Flags & NetworkObjectFlags.AllowStateAuthorityOverride, Is.EqualTo(NetworkObjectFlags.None));
            var activator = new GameObject("tp-replica-activator"); _scratch.Add(activator);
            activator.AddComponent<ReplicaActivator>();
            _scratch.Add(connTemplate); _scratch.Add(chessTemplate);

            if (!host) yield return Until(() => Has("host-ready"), "the host to be in the room", 240);
            var start = _runner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room, PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, AuthValues = TwoProcessIdentity.AuthValues(),
                SceneManager = _runnerObject.AddComponent<NetworkSceneManagerDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 60);
            TestContext.Out.WriteLine($"TWOPROC role={role} StartGame ok={start.Result.Ok} reason={start.Result.ShutdownReason}");
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            NUnitAssert.That(_runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic { Object = connTemplate.GetComponent<NetworkObject>(),
                AssetGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000044") }, out var connPrefab), Is.True);
            NUnitAssert.That(_runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic { Object = chessTemplate.GetComponent<NetworkObject>(),
                AssetGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000066") }, out var chessNetPrefab), Is.True);
            int me = _runner.LocalPlayer.PlayerId;
            TestContext.Out.WriteLine($"TWOPROC role={role} player={me} pid={System.Diagnostics.Process.GetCurrentProcess().Id} mode={mode}");
            if (host)
            {
                connection = _runner.Spawn(connPrefab).GetComponent<FusionConnectionManager>();
                connection.gameObject.SetActive(true);
                connection.OnPlayerJoined(_runner, _runner.LocalPlayer);
                Mark("host-ready");
            }
            yield return Until(() => _runner.ActivePlayers.Count() == 2, "both players", 240);
            int peer = _runner.ActivePlayers.First(p => p != _runner.LocalPlayer).PlayerId;
            Func<FusionConnectionManager> find = () => UnityEngine.Object.FindObjectsByType<FusionConnectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(m => m != connTemplate2 && m.Runner == _runner);
            if (!host) { yield return Until(() => find() != null, "the connection manager", 120); connection = find(); connection.gameObject.SetActive(true); }
            Func<string> describe = () => $"role={role} state={manager.ConnectionState} linked={connection.LinkedPeerCount}";
            yield return Until(() => manager.ConnectionState == SpatialBlendConnectionState.Connected && connection.LinkedPeerCount == 1, "the product's core link", 120, describe);

            _appObject = new GameObject("tp-app"); _appObject.SetActive(false);
            var app = _appObject.AddComponent<EditApp>();
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
            _appObject.SetActive(true);
            yield return Until(() => app.IsAppStarted, "the app start", 120, describe);
            long roomElement = (long)typeof(SpatialBlendManager).GetMethod("EnsureRoomElement", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
            NUnitAssert.That(roomElement, Is.GreaterThan(0));

            // The Chess: A spawns it (the state authority to begin with) at its pose; B places the replica at its own pose, as each
            // room's SpawnOnMRUKAnchor does. A shares it once B has placed it; B's app adds it as its copy (the product's path).
            ChessBoardHandlerMotif board = null;
            if (host)
            {
                var spawned = _runner.Spawn(chessNetPrefab, ChessA.GetColumn(3), ChessA.rotation);
                spawned.gameObject.SetActive(true);
                board = spawned.GetComponentInChildren<ChessBoardHandlerMotif>(true);
                TestContext.Out.WriteLine($"TWOPROC role=host spawned Chess id={spawned.Id} valid={spawned.IsValid} flags={spawned.Flags} " + NetworkObjects());
                yield return Until(() => Has("joiner-placed"), "the joiner to place its copy", 120);
                app.RegisterGameObject(spawned.gameObject, false);
                yield return Until(() => spawned.GetComponent<SbEntity>().IsAttached(), "the host's Chess to be registered", 30);
            }
            else
            {
                yield return Until(() => { ActivateReplicas(); return (board = UnityEngine.Object.FindObjectsByType<ChessBoardHandlerMotif>(FindObjectsSortMode.None)
                    .FirstOrDefault(b => b.Object != null && b.Object.IsValid && b.Runner == _runner)) != null; }, "the Chess replica", 120,
                    () => NetworkObjects() + " boards=" + string.Join(";", UnityEngine.Object.FindObjectsByType<ChessBoardHandlerMotif>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .Select(b => $"{b.name}:active={b.isActiveAndEnabled}:obj={(b.Object != null)}:runner={(b.Runner == _runner)}")));
                board.Object.transform.SetPositionAndRotation(ChessB.GetColumn(3), ChessB.rotation);
                Mark("joiner-placed");
                yield return Until(() => board.Object.GetComponent<SbEntity>().IsAttached(), "the Chess copy to arrive through the core", 60, describe);
            }
            yield return Until(() => board.Lease != null, "the board to be spawned", 30);
            SbEntity chessEntity = board.Object.GetComponent<SbEntity>();
            Transform chessRoot = chessEntity.transform;
            TestContext.Out.WriteLine($"TWOPROC role={role} chess attached source={chessEntity.IsSource} element={chessEntity.ID}");
            NUnitAssert.That(chessEntity.IsSource, Is.EqualTo(host));
            yield return Until(() => manager.TryGetAuthoredRegion(chessEntity.ID, out long r) && r > 0, "the Chess region", 30);
            Mark(role + "-chess");
            yield return Until(() => Has(other + "-chess"), "the peer's Chess", 120);

            // ---- 1. B moves its copy -------------------------------------------------------------------------------------
            Matrix4x4 frameToObject = Matrix4x4.Inverse(host ? ChessA : ChessB) * RegionFrame(manager, chessEntity.ID);
            if (!host)
            {
                Matrix4x4 before = RegionFrame(manager, chessEntity.ID);
                double arrivalError = MaxAbs(before, ChessB * frameToObject);
                chessRoot.SetPositionAndRotation(ChessBMoved.GetColumn(3), ChessBMoved.rotation);
                long movedAt = Now();
                Matrix4x4 expected = ChessBMoved * frameToObject;
                yield return Until(() => MaxAbs(RegionFrame(manager, chessEntity.ID), expected) < 1e-4, "B's region to follow its moved copy", 10,
                    () => $"region={RegionFrame(manager, chessEntity.ID)} expected={expected}");
                double moveError = MaxAbs(RegionFrame(manager, chessEntity.ID), expected);
                TestContext.Out.WriteLine($"TWOPROC 190 B moved its copy by {F(Vector3.Distance(ChessB.GetColumn(3), ChessBMoved.GetColumn(3)))} m / 90 deg: " +
                    $"B region followed in {Now() - movedAt} ms, max matrix error {F(moveError)} (arrival error {F(arrivalError)})");
                Mark("joiner-moved");
            }
            else
            {
                Matrix4x4 boardBefore = chessRoot.localToWorldMatrix, regionBefore = RegionFrame(manager, chessEntity.ID);
                yield return Until(() => Has("joiner-moved"), "B's move", 60);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 2f) yield return null;   // a window for any pose to arrive; none should
                double boardDelta = MaxAbs(chessRoot.localToWorldMatrix, boardBefore), regionDelta = MaxAbs(RegionFrame(manager, chessEntity.ID), regionBefore);
                TestContext.Out.WriteLine($"TWOPROC 190 A after B's move: A board moved {F(boardDelta)}, A region moved {F(regionDelta)} (per-room placement: both 0)");
                NUnitAssert.That(boardDelta, Is.LessThan(1e-6), "A's board is A's room's own");
                NUnitAssert.That(regionDelta, Is.LessThan(1e-6));
            }

            // ---- 2. refuse, drop, hand over -------------------------------------------------------------------------------
            EditLease lease = board.Lease;
            var refusals = new List<(int? holder, int piece)>();
            board.GrabRefused += (h, p) => refusals.Add((h, p));
            // The board starts held by its spawner, which gives it back once the pieces are at rest: wait until nobody holds it.
            yield return Until(() => lease.Holder == null, "the board's initial authority to be released", 60, () => $"holder={lease.Holder}");
            Mark(role + "-free0");
            yield return Until(() => Has(other + "-free0"), "the peer to see the board free", 60);

            if (host)
            {
                long asked = Now();
                board.BeginPieceGrab(0);
                yield return Until(() => lease.HeldByMe, "A's grab to be granted", 20, () => $"holder={lease.Holder}");
                TestContext.Out.WriteLine($"TWOPROC 191 A grab granted in {Now() - asked} ms");
                Mark("host-holds", Now().ToString(CultureInfo.InvariantCulture));
                yield return Until(() => Has("joiner-refused"), "B's refused grab", 60);
                // A moves the piece 10 cm along the board and drops it (kinematic, so it rests where it is put).
                Transform p0 = board.Piece(0);
                p0.GetComponent<Rigidbody>().isKinematic = true;
                Vector3 dropped = p0.localPosition + new Vector3(0.1f, 0f, 0f);
                p0.localPosition = dropped;
                long drop = Now();
                board.EndPieceGrab(0);
                yield return Until(() => lease.Holder == null, "A's lease to be released after the drop", 20, () => $"holder={lease.Holder}");
                TestContext.Out.WriteLine($"TWOPROC 191 A released {Now() - drop} ms after the drop");
                Mark("host-dropped", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", dropped.x, dropped.y, dropped.z));
                yield return Until(() => Has("joiner-holds"), "B to take the lease", 60);
                yield return Until(() => lease.Holder == peer, "A to see B hold the lease", 20);
                board.BeginPieceGrab(2);
                NUnitAssert.That(refusals.Count, Is.EqualTo(1), "A's grab while B holds is refused on the spot");
                NUnitAssert.That(refusals[0].holder, Is.EqualTo(peer), "the refusal names B");
                NUnitAssert.That(lease.Requested, Is.False, "no request was sent");
                TestContext.Out.WriteLine($"TWOPROC 191 A refused while B holds: holder named Player {refusals[0].holder}; tag shows '{board.PresenceTag?.Text}'");
                NUnitAssert.That(board.PresenceTag != null && board.PresenceTag.Visible && board.PresenceTag.Text == EditLeaseText.Moving(peer), Is.True, "A sees 'Moving: B'");
                long outlineFrom = Now();
                yield return Until(() => board.PresenceOutline != null && board.PresenceOutline.Visible && board.PresenceOutline.Target == board.Piece(0),
                    "A to see B's piece outlined", 10, () => $"moving={board.MovingPiece} outline={(board.PresenceOutline != null ? board.PresenceOutline.Visible.ToString() : "none")}");
                TestContext.Out.WriteLine($"TWOPROC 191 A sees B's moved piece outlined ({Now() - outlineFrom} ms after the refusal)");
                Mark("host-refused");
            }
            else
            {
                yield return Until(() => Has("host-holds"), "A to hold the lease", 60);
                long heldAt = long.Parse(Read("host-holds"), CultureInfo.InvariantCulture);
                yield return Until(() => lease.Holder == peer, "B to see A hold the lease", 20, () => $"holder={lease.Holder}");
                TestContext.Out.WriteLine($"TWOPROC 191 B saw A's lease {Now() - heldAt} ms after A held it (wall clock)");
                NUnitAssert.That(board.PresenceTag != null && board.PresenceTag.Visible && board.PresenceTag.Text == EditLeaseText.Moving(peer), Is.True, "B sees 'Moving: A'");
                board.BeginPieceGrab(1);
                NUnitAssert.That(refusals.Count, Is.EqualTo(1), "B's grab while A holds is refused on the spot");
                NUnitAssert.That(refusals[0].holder, Is.EqualTo(peer), "the refusal names A");
                NUnitAssert.That(lease.Requested, Is.False, "no request was sent");
                NUnitAssert.That(EditLeaseText.Refused(refusals[0].holder), Is.EqualTo($"Player {peer} is moving this"));
                TestContext.Out.WriteLine($"TWOPROC 191 B refused while A holds: \"{EditLeaseText.Refused(refusals[0].holder)}\"");
                Mark("joiner-refused");
                yield return Until(() => Has("host-dropped"), "A's drop", 60);
                var parts = Read("host-dropped").Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                var dropped = new Vector3(parts[0], parts[1], parts[2]);
                long seen = Now();
                yield return Until(() => lease.Holder == null && Vector3.Distance(board.NetworkedPiece(0).position, dropped) < 1e-3f,
                    "B to see the lease free and the dropped pose", 20, () => $"holder={lease.Holder} networked={board.NetworkedPiece(0).position} dropped={dropped}");
                yield return Until(() => Vector3.Distance(board.Piece(0).localPosition, dropped) < 2e-3f, "B's piece to reach the dropped pose", 10);
                TestContext.Out.WriteLine($"TWOPROC 191 B: dropped pose error networked {F(Vector3.Distance(board.NetworkedPiece(0).position, dropped) * 1000)} mm, " +
                    $"shown {F(Vector3.Distance(board.Piece(0).localPosition, dropped) * 1000)} mm, lease free and pose in {Now() - seen} ms after A's mark");
                long asked = Now();
                board.BeginPieceGrab(0);
                yield return Until(() => lease.HeldByMe, "B's grab to be granted", 20, () => $"holder={lease.Holder}");
                TestContext.Out.WriteLine($"TWOPROC 191 B grab granted in {Now() - asked} ms");
                Mark("joiner-holds");
                yield return Until(() => Has("host-refused"), "A's refused grab", 60);
                board.Piece(0).GetComponent<Rigidbody>().isKinematic = true;
                board.EndPieceGrab(0);
                yield return Until(() => lease.Holder == null, "B's lease to be released", 20);
                Mark("free1");
            }

            // ---- 3. both grab the same free piece at the same instant ---------------------------------------------------------
            yield return Until(() => Has("free1") && lease.Holder == null, "the board to be free before the race", 30);
            if (host) Mark("race-at", (Now() + 3000).ToString(CultureInfo.InvariantCulture));
            yield return Until(() => Has("race-at"), "the race instant", 30);
            long raceAt = long.Parse(Read("race-at"), CultureInfo.InvariantCulture);
            yield return Until(() => Now() >= raceAt, "the race instant", 30);
            int refusalsBefore = refusals.Count;
            long raced = Now();
            board.BeginPieceGrab(3);
            yield return Until(() => lease.Holder != null, "a holder after the race", 20);
            yield return Until(() => lease.HeldByMe || refusals.Count > refusalsBefore, "this side's outcome", 20);
            string outcome = lease.HeldByMe ? "won" : "refused";
            TestContext.Out.WriteLine($"TWOPROC 191 race: {role} {outcome} in {Now() - raced} ms (late by {raced - raceAt} ms), holder Player {lease.Holder}");
            Mark(role + "-race", outcome);
            yield return Until(() => Has(other + "-race"), "the peer's race outcome", 30);
            NUnitAssert.That(new[] { outcome, Read(other + "-race") }.Count(o => o == "won"), Is.EqualTo(1), "exactly one grabber wins");
            if (outcome == "won")
            {
                board.Piece(3).GetComponent<Rigidbody>().isKinematic = true;
                board.EndPieceGrab(3);
                yield return Until(() => lease.Holder == null, "the winner's release", 20);
                Mark("free2");
            }
            yield return Until(() => Has("free2") && lease.Holder == null, "the board to be free after the race", 30);

            // ---- 4. the holder leaves ------------------------------------------------------------------------------------
            if (!host)
            {
                board.BeginPieceGrab(4);
                yield return Until(() => lease.HeldByMe, "B's grab before leaving", 20);
                Mark("joiner-holds-leaving");
                yield return Until(() => Has("host-saw-joiner-hold"), "A to see B hold", 30);
                Mark("joiner-left", Now().ToString(CultureInfo.InvariantCulture));
                var shutdown = _runner.Shutdown();
                yield return Until(() => shutdown.IsCompleted, "B's leave", 30);
                TestContext.Out.WriteLine("TWOPROC 191 B left the room holding the lease");
                yield break;
            }
            yield return Until(() => Has("joiner-holds-leaving") && lease.Holder == peer, "B to hold before leaving", 30);
            Mark("host-saw-joiner-hold");
            yield return Until(() => Has("joiner-left"), "B's leave", 60);
            long leftAt = long.Parse(Read("joiner-left"), CultureInfo.InvariantCulture);
            yield return Until(() => lease.Holder == null, "the leaver's lease to be freed", 60, () => $"holder={lease.Holder} left={string.Join(",", left)}");
            long freed = Now();
            board.BeginPieceGrab(4);
            yield return Until(() => lease.HeldByMe, "A's grab after B left", 20);
            TestContext.Out.WriteLine($"TWOPROC 191 leave: lease free {freed - leftAt} ms after B left (wall clock), A granted {Now() - freed} ms later; PlayerLeft seen={left.Count}");
            board.EndPieceGrab(4);
        }

        private sealed class Events : INetworkRunnerCallbacks
        {
            private readonly Action<NetworkRunner, PlayerRef> _joined, _left; private readonly Action _shutdown;
            public Events(Action<NetworkRunner, PlayerRef> j, Action<NetworkRunner, PlayerRef> l, Action s) { _joined = j; _left = l; _shutdown = s; }
            public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) => _joined(runner, player);
            public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) => _left(runner, player);
            public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) => _shutdown();
            public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
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
            public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
            public void OnSceneLoadDone(NetworkRunner runner) { }
            public void OnSceneLoadStart(NetworkRunner runner) { }
            public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
            public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        }
    }
}
#endif
