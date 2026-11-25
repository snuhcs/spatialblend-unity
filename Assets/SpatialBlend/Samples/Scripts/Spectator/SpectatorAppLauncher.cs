// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// "Join as observer" in the desktop lobby (ControlPanelActions.JoinAsObserver): starts the desktop spectator app as a separate
    /// process, with the room when the lobby shows exactly one (<c>-spectate-room NAME</c>, see <see cref="SpectatorJoinPolicy"/>);
    /// otherwise the spectator's own window asks for the room. The spectator scene must be the first scene of its build (Fusion names a
    /// scene object by it), so it cannot be a scene of the Freeform app itself, and a separate process is how it stays separate.
    /// </summary>
    public static class SpectatorAppLauncher
    {
        public const string AppEnvironmentVariable = "SPATIALBLEND_SPECTATOR_APP";
        public const string MacAppName = "SpatialBlendSpectator.app";
        public const string WindowsAppName = "SpatialBlendSpectator.exe";
        public const string LinuxAppName = "SpatialBlendSpectator.x86_64";
        /// <summary>The macOS opener. A test points it at a stand-in that records its arguments; null is /usr/bin/open.</summary>
        public static string OpenExecutable { get; set; }

        /// <summary>The room to pass from the lobby's list: the only one listed, or none (the spectator's window asks).</summary>
        public static string RoomToPass(IReadOnlyList<FusionSessionEntry> sessions) =>
            sessions != null && sessions.Count == 1 ? sessions[0].Name : null;

        public const string NotFoundMessage = "The spectator app was not found. Build it with tools/build-spectator-mac.sh, then put it next to this app or set SPATIALBLEND_SPECTATOR_APP to its path.";

        /// <summary>
        /// Where the spectator app is, first match wins: <see cref="AppEnvironmentVariable"/> (the app's path), then in
        /// <paramref name="playerFolder"/> (the folder this player is in: the bundle's parent on macOS, the executable's folder elsewhere).
        /// There is no other default location: put the app next to the player, or set the variable (docs/desktop-spectator.md).
        /// </summary>
        public static string FindApp(Func<string, string> environment, Func<string, bool> exists, RuntimePlatform platform, string playerFolder)
        {
            foreach (var candidate in Candidates(environment, platform, playerFolder))
                if (!string.IsNullOrEmpty(candidate) && exists(candidate)) return candidate;
            return null;
        }

        private static IEnumerable<string> Candidates(Func<string, string> environment, RuntimePlatform platform, string playerFolder)
        {
            yield return environment?.Invoke(AppEnvironmentVariable);
            var name = AppName(platform);
            if (name == null) yield break;
            if (!string.IsNullOrEmpty(playerFolder)) yield return Path.Combine(playerFolder, name);
        }

        private static string AppName(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor: return MacAppName;
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor: return WindowsAppName;
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor: return LinuxAppName;
                default: return null;
            }
        }

        /// <summary>The folder the player is in. macOS: dataPath is Game.app/Contents, so the bundle's parent; elsewhere dataPath is Game_Data next to the executable.</summary>
        public static string PlayerFolder(RuntimePlatform platform, string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return null;
            var parent = Path.GetDirectoryName(dataPath.TrimEnd('/', '\\'));
            if (platform == RuntimePlatform.OSXPlayer && !string.IsNullOrEmpty(parent)) return Path.GetDirectoryName(parent);
            return parent;
        }

        /// <summary>The process to start: macOS opens the bundle as a new instance (<c>open -n</c>), the others run the executable.</summary>
        public static (string File, List<string> Arguments) BuildCommand(string app, string room, RuntimePlatform platform)
        {
            var arguments = new List<string>();
            bool mac = platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor;
            if (mac) { arguments.Add("-n"); arguments.Add(app); arguments.Add("--args"); }
            var name = SpectatorJoinPolicy.Normalize(room);
            // A room that would read as a flag is not passed: the spectator's own window asks for it.
            if (SpectatorJoinPolicy.IsSafeAsArgument(name)) { arguments.Add(SpectatorJoinPolicy.RoomFlag); arguments.Add(name); }
            return (mac ? (OpenExecutable ?? "/usr/bin/open") : app, arguments);
        }

        /// <summary>Starts the spectator app. False with a message for the user when it cannot be found or started.</summary>
        public static bool TryLaunch(string room, out string message)
        {
            var platform = Application.platform;
            return TryLaunch(room, Environment.GetEnvironmentVariable, p => File.Exists(p) || Directory.Exists(p), platform,
                PlayerFolder(platform, Application.dataPath), out message);
        }

        /// <summary>The same with the machine's facts passed in (a test's seam).</summary>
        public static bool TryLaunch(string room, Func<string, string> environment, Func<string, bool> exists, RuntimePlatform platform,
            string playerFolder, out string message)
        {
            message = null;
            var app = FindApp(environment, exists, platform, playerFolder);
            if (app == null) { message = NotFoundMessage; return false; }
            var command = BuildCommand(app, room, platform);
            try
            {
                var start = new ProcessStartInfo(command.File) { UseShellExecute = false };
                foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
                // Disposing the handle does not end the spectator: it is a separate process.
                using (Process.Start(start)) { }
                Debug.Log("[Spectator] started " + app + (SpectatorJoinPolicy.IsSafeAsArgument(SpectatorJoinPolicy.Normalize(room)) ? " for room '" + room.Trim() + "'" : ""));
                return true;
            }
            catch (Exception e)
            {
                message = "The spectator app could not be started: " + e.Message;
                return false;
            }
        }
    }
}
