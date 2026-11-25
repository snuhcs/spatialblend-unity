// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// A batchmode run (a test, a build) has no user to capture, so it must never open the microphone. The Freeform scene has
    /// microphone consumers that start recording on their own: the Meta Avatars sample's <c>LipSyncMicInput</c>
    /// (<c>Microphone.Start</c> from its Start and Update) and Photon Voice's <c>Recorder</c>. Both are SDK code that is not in
    /// this repository (git-ignored, installed by the bootstrap scripts), so they cannot be guarded where they are; this guard
    /// disables them from here, before their first Start and for any that arrive later, when <see cref="Application.isBatchMode"/>.
    /// Reason: the Mac's microphone indicator was on during the two-process spectator test, which loads the real Freeform
    /// scene headlessly. Outside batchmode it does nothing.
    /// </summary>
    public static class BatchModeMicrophoneGuard
    {
        private static readonly string[] ConsumerTypeNames = { "LipSyncMicInput", "Photon.Voice.Unity.Recorder" };
        private static bool _installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!Application.isBatchMode || _installed) return;
            _installed = true;
            // sceneLoaded fires after a scene's Awake and OnEnable and before its first Start.
            SceneManager.sceneLoaded += (scene, mode) => Apply();
            var runner = new GameObject("[BatchModeMicrophoneGuard]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Sweeper>();
        }

        /// <summary>Disables every microphone consumer in the loaded scenes. Returns how many it disabled. Test seam; no-op outside batchmode.</summary>
        public static int Apply() => Apply(Application.isBatchMode);

        /// <summary>The guard with the batchmode fact passed in, so a test can show a player build (false) leaves the voice stack alone.</summary>
        public static int Apply(bool batchMode)
        {
            if (!batchMode) return 0;
            int disabled = 0;
            foreach (var typeName in ConsumerTypeNames)
            {
                var type = FindType(typeName);
                if (type == null) continue;
                foreach (var found in UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (found is Behaviour behaviour && behaviour.enabled)
                    {
                        behaviour.enabled = false;
                        disabled++;
                    }
                    StopRecording(found);
                }
            }
            return disabled;
        }

        /// <summary>True when no microphone device is recording (a check for tests).</summary>
        public static bool NoMicrophoneIsRecording(out string device)
        {
            foreach (var d in Microphone.devices)
            {
                if (!Microphone.IsRecording(d)) continue;
                device = d;
                return false;
            }
            device = null;
            return true;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch (Exception) { /* a dynamic or broken assembly */ }
            }
            return null;
        }

        // A consumer that already started (a Recorder in a scene object that ran earlier) is told to stop as well.
        private static void StopRecording(UnityEngine.Object consumer)
        {
            foreach (var name in new[] { "RecordingEnabled", "TransmitEnabled" })
            {
                var property = consumer.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                {
                    try { property.SetValue(consumer, false); } catch (Exception) { /* nothing to stop */ }
                }
            }
            if (NoMicrophoneIsRecording(out _)) return;
            var stop = consumer.GetType().GetMethod("StopMicrophone", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (stop != null)
            {
                try { stop.Invoke(consumer, null); } catch (Exception) { /* nothing to stop */ }
            }
        }

        private sealed class Sweeper : MonoBehaviour
        {
            private float _next;

            private void Update()
            {
                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + 0.5f;
                Apply();
            }
        }
    }
}
