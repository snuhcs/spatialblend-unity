// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>"Join as observer" starts the desktop spectator app: where it is found and the command that starts it.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformSpectatorLauncherTests
    {
        private static string Find(string env, HashSet<string> existing, RuntimePlatform platform, string player = "/Apps") =>
            SpectatorAppLauncher.FindApp(n => n == SpectatorAppLauncher.AppEnvironmentVariable ? env : null, existing.Contains, platform, player);

        [Test]
        public void TheEnvironmentOverrideWinsThenTheAppNextToThePlayerAndNothingElse()
        {
            var all = new HashSet<string> { "/custom/Spec.app", "/Apps/SpatialBlendSpectator.app", "/Users/u/spatialblend-viz/spectator/SpatialBlendSpectator.app" };
            NUnitAssert.That(Find("/custom/Spec.app", all, RuntimePlatform.OSXPlayer), Is.EqualTo("/custom/Spec.app"));
            NUnitAssert.That(Find(null, all, RuntimePlatform.OSXPlayer), Is.EqualTo("/Apps/SpatialBlendSpectator.app"));
            all.Remove("/Apps/SpatialBlendSpectator.app");
            NUnitAssert.That(Find(null, all, RuntimePlatform.OSXPlayer), Is.Null, "no default folder in the user's home is searched");
            all.Clear();
            NUnitAssert.That(Find(null, all, RuntimePlatform.OSXPlayer), Is.Null, "nothing found: the caller says so");
            NUnitAssert.That(Find("/missing.app", all, RuntimePlatform.OSXEditor), Is.Null, "an override that does not exist is not used");
        }

        [Test]
        public void OtherDesktopsLookForTheirOwnExecutable()
        {
            var exe = Path.Combine("C:/Apps", "SpatialBlendSpectator.exe"); // the platform's own separator
            var win = new HashSet<string> { exe };
            NUnitAssert.That(Find(null, win, RuntimePlatform.WindowsPlayer, "C:/Apps"), Is.EqualTo(exe));
            NUnitAssert.That(Find(null, new HashSet<string> { "/Apps/SpatialBlendSpectator.app" }, RuntimePlatform.Android), Is.Null, "the Quest build has no spectator");
        }

        [Test]
        public void ThePlayerFolderIsWhereTheExecutableOrBundleIs()
        {
            NUnitAssert.That(SpectatorAppLauncher.PlayerFolder(RuntimePlatform.OSXPlayer, "/Apps/Freeform.app/Contents"), Is.EqualTo("/Apps"));
            NUnitAssert.That(SpectatorAppLauncher.PlayerFolder(RuntimePlatform.WindowsPlayer, "C:/Apps/Freeform_Data"), Is.EqualTo("C:/Apps"));
            NUnitAssert.That(SpectatorAppLauncher.PlayerFolder(RuntimePlatform.OSXPlayer, null), Is.Null);
        }

        [Test]
        public void MacOpensTheBundleAsANewInstanceWithTheRoomAndOthersRunTheExecutable()
        {
            var mac = SpectatorAppLauncher.BuildCommand("/Apps/SpatialBlendSpectator.app", " Room 1 ", RuntimePlatform.OSXPlayer);
            NUnitAssert.That(mac.File, Is.EqualTo("/usr/bin/open"));
            NUnitAssert.That(mac.Arguments, Is.EqualTo(new[] { "-n", "/Apps/SpatialBlendSpectator.app", "--args", "-spectate-room", "Room 1" }));
            var noRoom = SpectatorAppLauncher.BuildCommand("/Apps/SpatialBlendSpectator.app", null, RuntimePlatform.OSXPlayer);
            NUnitAssert.That(noRoom.Arguments, Is.EqualTo(new[] { "-n", "/Apps/SpatialBlendSpectator.app", "--args" }), "no room: the spectator's own window asks");
            var win = SpectatorAppLauncher.BuildCommand("C:/Apps/SpatialBlendSpectator.exe", "R", RuntimePlatform.WindowsPlayer);
            NUnitAssert.That(win.File, Is.EqualTo("C:/Apps/SpatialBlendSpectator.exe"));
            NUnitAssert.That(win.Arguments, Is.EqualTo(new[] { "-spectate-room", "R" }));
        }

        [Test]
        public void ARoomThatWouldReadAsAFlagIsNeverPassedOnTheCommandLine()
        {
            foreach (var room in new[] { "-batchmode", " -logFile", "--args" })
            {
                var mac = SpectatorAppLauncher.BuildCommand("/Apps/S.app", room, RuntimePlatform.OSXPlayer);
                NUnitAssert.That(mac.Arguments, Is.EqualTo(new[] { "-n", "/Apps/S.app", "--args" }), room);
                NUnitAssert.That(SpectatorAppLauncher.BuildCommand("/Apps/S.exe", room, RuntimePlatform.WindowsPlayer).Arguments, Is.Empty, room);
            }
            NUnitAssert.That(SpectatorJoinPolicy.ResolveRoom(new[] { "app", "-spectate-room", "-batchmode" }, n => null), Is.Null, "the next flag is not a room");
            NUnitAssert.That(SpectatorJoinPolicy.IsSafeAsArgument("Room -1"), Is.True, "a dash inside the name is fine");
        }

        [Test]
        public void TheRoomIsPassedOnlyWhenTheLobbyListsExactlyOne()
        {
            FusionSessionEntry Entry(string n) => new FusionSessionEntry(n, 1, 9);
            NUnitAssert.That(SpectatorAppLauncher.RoomToPass(null), Is.Null);
            NUnitAssert.That(SpectatorAppLauncher.RoomToPass(new FusionSessionEntry[0]), Is.Null);
            NUnitAssert.That(SpectatorAppLauncher.RoomToPass(new[] { Entry("Room 1") }), Is.EqualTo("Room 1"));
            NUnitAssert.That(SpectatorAppLauncher.RoomToPass(new[] { Entry("Room 1"), Entry("Room 2") }), Is.Null);
        }

        [Test]
        public void WhenTheAppIsNotFoundTheUserGetsTheMessageAndNothingStarts()
        {
            bool ok = SpectatorAppLauncher.TryLaunch("R", n => null, p => false, RuntimePlatform.OSXPlayer, "/Apps", out var message);
            NUnitAssert.That(ok, Is.False);
            NUnitAssert.That(message, Is.EqualTo(SpectatorAppLauncher.NotFoundMessage));
        }

        [Test]
        public void TryLaunchReallyStartsTheOpenerWithTheRoomAsSeparateArguments()
        {
            if (Application.platform != RuntimePlatform.OSXEditor) NUnitAssert.Ignore("Runs the macOS opener path; this is not a macOS Editor.");
            var dir = Path.Combine(Path.GetTempPath(), "spatialblend-launcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var app = Path.Combine(dir, "Fake.app");
            Directory.CreateDirectory(app);
            // A stand-in for /usr/bin/open: it writes each argument it receives on a line of its own. (The real opener would start a
            // GUI application; this test opens no window.)
            var opener = Path.Combine(dir, "fake-open");
            var output = opener + ".out";
            File.WriteAllText(opener, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$0.out.tmp\" && mv \"$0.out.tmp\" \"$0.out\"\n");
            using (var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/chmod", "+x \"" + opener + "\"") { UseShellExecute = false })) chmod.WaitForExit();
            var previousApp = Environment.GetEnvironmentVariable(SpectatorAppLauncher.AppEnvironmentVariable);
            var previousOpener = SpectatorAppLauncher.OpenExecutable;
            try
            {
                Environment.SetEnvironmentVariable(SpectatorAppLauncher.AppEnvironmentVariable, app);
                SpectatorAppLauncher.OpenExecutable = opener;
                const string room = "Room \"A\" b's  c";
                NUnitAssert.That(SpectatorAppLauncher.TryLaunch(room, out var message), Is.True, message);
                for (int i = 0; i < 100 && !File.Exists(output); i++) Thread.Sleep(50);
                NUnitAssert.That(File.Exists(output), Is.True, "the opener was started");
                NUnitAssert.That(File.ReadAllLines(output), Is.EqualTo(new[] { "-n", app, "--args", "-spectate-room", room }),
                    "the room is one argument, spaces and quotes included");
            }
            finally
            {
                SpectatorAppLauncher.OpenExecutable = previousOpener;
                Environment.SetEnvironmentVariable(SpectatorAppLauncher.AppEnvironmentVariable, previousApp);
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
#endif
