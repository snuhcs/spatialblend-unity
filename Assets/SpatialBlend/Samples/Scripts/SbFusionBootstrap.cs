// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// <see cref="FusionBootstrap"/> that passes the room limit itself. The player cap is declared in
    /// <see cref="SpatialBlendSettings.maxPlayers"/> and handed to Fusion as <c>StartGameArgs.PlayerCount</c>; the
    /// untracked Photon project config is not the source of the limit.
    /// Everything else is the SDK's own start path.
    /// </summary>
    public class SbFusionBootstrap : FusionBootstrap
    {
        /// <summary>
        /// The room limit: the declared setting, or its default when no settings exist. A declared value below 2 is a
        /// visible error (the default is then used so the room still starts); it used to fall back silently.
        /// </summary>
        public static int ResolvePlayerCount(SpatialBlendSettings settings)
        {
            if (settings == null) return SpatialBlendSettings.DefaultMaxPlayers;
            if (settings.maxPlayers >= 2) return settings.maxPlayers;
            Debug.LogError($"[SbFusionBootstrap] SpatialBlendSettings.maxPlayers = {settings.maxPlayers} is out of range " +
                           $"(at least 2); using the default {SpatialBlendSettings.DefaultMaxPlayers}.");
            return SpatialBlendSettings.DefaultMaxPlayers;
        }

        /// <summary>The settings the running app loaded, or the asset in Resources.</summary>
        protected virtual SpatialBlendSettings DeclaredSettings =>
            SpatialBlendManager.CurrentSettings != null
                ? SpatialBlendManager.CurrentSettings
                : Resources.Load<SpatialBlendSettings>("SpatialBlendSettings");

        /// <summary>The arguments of one runner start. Public so a test can read what Fusion would be given.</summary>
        public StartGameArgs BuildStartGameArgs(GameMode gameMode, NetAddress address, NetworkSceneInfo scene,
            Action<NetworkRunner> onGameStarted, INetworkSceneManager sceneManager, INetworkObjectProvider objectProvider,
            INetworkRunnerUpdater updater)
        {
            return new StartGameArgs
            {
                GameMode = gameMode,
                Address = address,
                Scene = scene,
                SessionName = DefaultRoomName,
                PlayerCount = ResolvePlayerCount(DeclaredSettings),
                OnGameStarted = onGameStarted,
                SceneManager = sceneManager,
                Updater = updater,
                ObjectProvider = objectProvider,
            };
        }

        /// <summary>
        /// Raised on the main thread when a runner start ended without joining its room: the room is full, closed or gone, the join timed out,
        /// or the network failed (the runner is already shut down). The stock bootstrap reports none of this: it logs a faulted task and moves to
        /// AllConnected whatever the result was.
        /// </summary>
        public event Action<ShutdownReason> StartFailed;

        /// <summary>Shared join and host both come through here. A desktop observer only joins.</summary>
        public override void StartSharedClient()
        {
            if (!ObserverMayStart())
            {
                Debug.LogWarning($"[SbFusionBootstrap] A desktop observer does not start '{DefaultRoomName}': it is not a listed room, " +
                                 "and starting it would create it. " + FreeformMatchmaking.ObserverHostError + ".");
                // The browser is already Starting: a refused start is a failed one, so it returns to a clean lobby.
                StartFailed?.Invoke(ShutdownReason.GameNotFound);
                return;
            }
            StartSharedClientCore();
        }

        /// <summary>
        /// A desktop observer joins and never creates. Its lobby gate is open (it has no room to set up), so this is the check
        /// at the start itself: the room must be one the lobby listed as joinable (<see cref="FusionSessionBrowser.ListsJoinable"/>).
        /// A participant always may.
        /// </summary>
        protected virtual bool ObserverMayStart()
        {
            if (!FusionSessionBrowser.LocalIsDesktopObserver()) return true;
            FusionSessionBrowser browser = GetComponent<FusionSessionBrowser>();
            return browser != null && browser.ListsJoinable(DefaultRoomName);
        }

        /// <summary>The Fusion start. A test replaces it so a closed gate is visible without opening a socket.</summary>
        protected virtual void StartSharedClientCore() => base.StartSharedClient();

        /// <summary>The runner start. A seam: tests return a failed start without a network.</summary>
        protected virtual async Task<(bool Ok, ShutdownReason Reason)> StartRunnerAsync(NetworkRunner runner, StartGameArgs args)
        {
            var result = await runner.StartGame(args);
            if (!result.Ok) Debug.LogWarning($"[SbFusionBootstrap] The runner did not join '{args.SessionName}': {result.ShutdownReason} {result.ErrorMessage}");
            return (result.Ok, result.ShutdownReason);
        }

        protected override async Task InitializeNetworkRunner(NetworkRunner runner, GameMode gameMode, NetAddress address,
            SceneRef scene, Action<NetworkRunner> onGameStarted, INetworkRunnerUpdater updater = null)
        {
            var sceneManager = runner.GetComponent<INetworkSceneManager>();
            if (sceneManager == null)
            {
                sceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>();
            }

            var objectProvider = runner.GetComponent<INetworkObjectProvider>();
            if (objectProvider == null)
            {
                objectProvider = runner.gameObject.AddComponent<NetworkObjectProviderDefault>();
            }

            var sceneInfo = new NetworkSceneInfo();
            if (scene.IsValid) sceneInfo.AddSceneRef(scene, LoadSceneMode.Additive);

            await StartAndReportAsync(runner, BuildStartGameArgs(gameMode, address, sceneInfo, onGameStarted, sceneManager,
                objectProvider, updater));
        }

        /// <summary>Starts the runner and raises <see cref="StartFailed"/> when it did not join (a start that throws counts as an error).</summary>
        protected async Task StartAndReportAsync(NetworkRunner runner, StartGameArgs args)
        {
            (bool Ok, ShutdownReason Reason) outcome;
            try
            {
                outcome = await StartRunnerAsync(runner, args);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SbFusionBootstrap] The runner start threw: " + e);
                outcome = (false, ShutdownReason.Error);
            }
            if (!outcome.Ok) StartFailed?.Invoke(outcome.Reason);
        }
    }
}
