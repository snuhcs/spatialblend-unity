// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

// Batchmode driver for the core's native-lifecycle check (Editor lifecycle half).
// Active only when SB_LIFECYCLE_MODE is set in the Unity process environment:
//   stale    : leak a native session + rich callback across a real domain
//              reload, then make native deliver to the old-domain thunk.
//   fixed    : same, but a beforeAssemblyReload hook destroys the session
//              first; after reload native must show the owner gone.
//   playmode : enter Play Mode, init an SpatialBlendLocalWorld and a raw
//              ownerless session, exit Play Mode, probe native leftovers.
#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SpatialBlend.Internal;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor.LifecycleProbe
{
    [InitializeOnLoad]
    internal static class NativeLifecycleProbe
    {
        private const string K = "OSLC.";
        private static readonly string Mode = Environment.GetEnvironmentVariable("SB_LIFECYCLE_MODE");
        // quitleak : (stale/fixed) after reload, quit without destroying the old session.
        // chain    : (playmode) after exit, enter Play Mode again, then destroy the ownerless session.
        private static readonly string Variant = Environment.GetEnvironmentVariable("SB_LIFECYCLE_VARIANT") ?? "";
        // Survives a domain reload only if the reload did not happen.
        private static int s_domainMarker;

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern void sb_destroy_session(long session);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sb_create_actor(long space);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sb_add_space(long session);

        private static void Log(string s)
        {
            // Console.WriteLine goes to the -logFile unbuffered by Debug.Log's stack spam.
            Console.WriteLine("[LIFECYCLE] " + s);
            Console.Out.Flush();
        }

        static NativeLifecycleProbe()
        {
            if (string.IsNullOrEmpty(Mode)) return;
            int load = SessionState.GetInt(K + "load", 0) + 1;
            SessionState.SetInt(K + "load", load);
            Log($"domain load #{load} mode={Mode} isPlaying={EditorApplication.isPlaying} appDomain={AppDomain.CurrentDomain.Id}");
            double deadline = EditorApplication.timeSinceStartup + 240;
            EditorApplication.update += () =>
            {
                if (EditorApplication.timeSinceStartup > deadline) { Log("TIMEOUT"); EditorApplication.Exit(3); }
            };
            EditorApplication.delayCall += () =>
            {
                try { Step(load); }
                catch (Exception e) { Log("EXCEPTION " + e); EditorApplication.Exit(2); }
            };
        }

        // ---- shared: build a session with one space, one actor, an active
        // rich lifetime, a rich callback and a targets callback ----
        private static void BuildSession(string tag, out long session, out long space, out long actor, out ulong lifetime)
        {
            SpatialBlendAPI.Initialize();
            session = SpatialBlendAPI.CreateSession();
            space = SpatialBlendAPI.AddSpace(session);
            actor = SpatialBlendAPI.CreateActor(space);
            int domain = AppDomain.CurrentDomain.Id;
            if (!SpatialBlendAPI.TryRegisterOnRichRealization(space, snap =>
                    Log($"rich callback fired tag={tag} registeredInDomain={domain} runningInDomain={AppDomain.CurrentDomain.Id}"),
                    out string err))
                throw new InvalidOperationException("register rich: " + err);
            SpatialBlendAPI.RegisterOnTargets(space, (s, a, t) => Log($"targets callback fired tag={tag}"));
            if (!SpatialBlendAPI.TryActivateRichRealization(space, actor, out lifetime, out err))
                throw new InvalidOperationException("activate rich: " + err);
            Log($"built tag={tag} session={session} space={space} actor={actor} richLifetime={lifetime}");
        }

        private static void Step(int load)
        {
            switch (Mode)
            {
                case "stale":
                case "fixed":
                    ReloadScenario(load);
                    break;
                case "playmode":
                    PlayModeScenario();
                    break;
                default:
                    Log("unknown mode"); EditorApplication.Exit(4); break;
            }
        }

        private static void ReloadScenario(int load)
        {
            if (load == 1)
            {
                // Live control: same delivery path inside the registering domain.
                BuildSession("control", out long cs, out _, out _, out _);
                Log("control: DestroySession in the registering domain (expect one live rich callback)");
                SpatialBlendAPI.DestroySession(cs);
                Log("control: DestroySession returned");

                BuildSession("leak", out long session, out long space, out long actor, out _);
                SessionState.SetString(K + "session", session.ToString());
                SessionState.SetString(K + "space", space.ToString());
                if (Mode == "fixed")
                {
                    // Fix shape under test: tear down native owners before the
                    // domain that owns the callback thunks is unloaded.
                    AssemblyReloadEvents.beforeAssemblyReload += () =>
                    {
                        Log("beforeAssemblyReload: destroying live native session before unload");
                        SpatialBlendAPI.DestroySession(session);
                        Log("beforeAssemblyReload: done");
                    };
                }
                Log("requesting script reload with native session still alive");
                EditorUtility.RequestScriptReload();
                return;
            }

            long oldSession = long.Parse(SessionState.GetString(K + "session", "0"));
            long oldSpace = long.Parse(SessionState.GetString(K + "space", "0"));
            Log($"after reload: old session={oldSession} space={oldSpace}");
            long probeActor = sb_create_actor(oldSpace);
            Log($"probe sb_create_actor(oldSpace) -> {probeActor} ({(probeActor != 0 ? "NATIVE SPACE STILL ALIVE (leaked across reload)" : "native space gone")})");
            if (Variant == "quitleak")
            {
                Log("quitleak: EditorApplication.Exit(0) with the old session still alive in native");
                EditorApplication.Exit(0);
                return;
            }
            Log("driving native teardown of the old session from the NEW domain (sb_destroy_session)");
            sb_destroy_session(oldSession);
            Log("sb_destroy_session(oldSession) returned without crash");
            EditorApplication.Exit(0);
        }

        private static void PlayModeScenario()
        {
            string phase = SessionState.GetString(K + "phase", "start");
            if (phase == "start")
            {
                SessionState.SetString(K + "phase", "entering");
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                Log("EnterPlaymode");
                EditorApplication.EnterPlaymode();
                return;
            }
            // Re-subscribe after each domain reload.
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (phase == "entering" && EditorApplication.isPlaying) InPlayMode();
            else if (phase == "reentering") EditorApplication.delayCall += SecondPlay;
            else if (phase == "exiting" && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode) AfterExit("reload-path");
        }

        private static void InPlayMode()
        {
            if (SessionState.GetString(K + "phase", "") != "entering") return;
            SessionState.SetString(K + "phase", "inplay");
            s_domainMarker = 42;
            var go = new GameObject("LifecycleProbeWorld");
            var world = go.AddComponent<SpatialBlend.SpatialBlendLocalWorld>();
            if (!world.TryInitialize(out string err)) throw new InvalidOperationException("world init: " + err);
            long worldSession = (long)typeof(SpatialBlend.SpatialBlendLocalWorld)
                .GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(world);
            SessionState.SetString(K + "worldSession", worldSession.ToString());
            Log($"in play mode: SpatialBlendLocalWorld session={worldSession}");

            // An ownerless session (no MonoBehaviour teardown), like a static
            // helper or a leaked test fixture would create.
            BuildSession("play-raw", out long raw, out long rawSpace, out _, out _);
            SessionState.SetString(K + "rawSession", raw.ToString());
            SessionState.SetString(K + "rawSpace", rawSpace.ToString());
            SessionState.SetString(K + "phase", "exiting");
            Log("ExitPlaymode");
            EditorApplication.ExitPlaymode();
        }

        private static void SecondPlay()
        {
            if (!EditorApplication.isPlaying) { EditorApplication.delayCall += SecondPlay; return; }
            if (SessionState.GetString(K + "phase", "") != "reentering") return;
            SessionState.SetString(K + "phase", "done");
            long raw = long.Parse(SessionState.GetString(K + "rawSession", "0"));
            long rawSpace = long.Parse(SessionState.GetString(K + "rawSpace", "0"));
            Log($"second play: domainMarker={s_domainMarker}; probe sb_create_actor(rawSpace) -> {sb_create_actor(rawSpace)}");
            Log("second play: driving sb_destroy_session(rawSession) after a domain reload");
            sb_destroy_session(raw);
            Log("second play: sb_destroy_session returned without crash");
            EditorApplication.Exit(0);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            Log("playModeStateChanged " + change);
            if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(K + "phase", "") == "entering")
                EditorApplication.delayCall += () => { try { InPlayMode(); } catch (Exception e) { Log("EXCEPTION " + e); EditorApplication.Exit(2); } };
            if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetString(K + "phase", "") == "exiting")
                EditorApplication.delayCall += () => { try { AfterExit("event-path"); } catch (Exception e) { Log("EXCEPTION " + e); EditorApplication.Exit(2); } };
        }

        private static void AfterExit(string via)
        {
            if (SessionState.GetString(K + "phase", "") != "exiting") return;
            SessionState.SetString(K + "phase", "done");
            Log($"after exit ({via}): domainMarker={s_domainMarker} ({(s_domainMarker == 42 ? "NO domain reload on exit" : "domain was reloaded on exit")})");
            long worldSession = long.Parse(SessionState.GetString(K + "worldSession", "0"));
            long raw = long.Parse(SessionState.GetString(K + "rawSession", "0"));
            long rawSpace = long.Parse(SessionState.GetString(K + "rawSpace", "0"));
            long probe = sb_add_space(worldSession);
            Log($"probe sb_add_space(worldSession={worldSession}) -> {probe} ({(probe != 0 ? "LocalWorld session LEAKED" : "LocalWorld session destroyed by OnDisable/OnDestroy")})");
            long rawProbe = sb_create_actor(rawSpace);
            Log($"probe sb_create_actor(rawSpace={rawSpace}) -> {rawProbe} ({(rawProbe != 0 ? "ownerless session still alive" : "gone")})");
            if (Variant == "chain")
            {
                SessionState.SetString(K + "phase", "reentering");
                Log("chain: EnterPlaymode again (reloads the domain) with the ownerless session still alive");
                EditorApplication.EnterPlaymode();
                return;
            }
            Log("driving sb_destroy_session(rawSession) after exit");
            sb_destroy_session(raw);
            Log("sb_destroy_session(rawSession) returned without crash");
            EditorApplication.Exit(0);
        }
    }
}
#endif
