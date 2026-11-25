// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A batchmode run of the real Freeform scene opens no microphone (the indicator once showed on during the two-process spectator
    /// test). PlayMode: the scene is loaded as build index 0, which is the Freeform scene in the project's build settings.
    /// Run with -testCategory BatchModeMicrophone (tools/run-spectator-two-process-test.sh checks the same in both of its processes).
    /// </summary>
    [TestFixture]
    [Category("BatchModeMicrophone")]
    public class FreeformSceneNoMicrophoneTests
    {
        [UnityTest, Timeout(120000)]
        public IEnumerator TheFreeformSceneStartsNoMicrophoneCaptureInBatchMode()
        {
            if (!Application.isBatchMode) NUnitAssert.Ignore("This test is about batchmode; an interactive run is not one.");
            // Batchmode has no headset, so the freeform app would otherwise join as an observer.
            string previous = Environment.GetEnvironmentVariable(DesktopObserverDecision.ForceParticipantVariable);
            Environment.SetEnvironmentVariable(DesktopObserverDecision.ForceParticipantVariable, "1");
            try
            {
                // The Meta Avatar SDK has no macOS binary: its manager logs an error when the scene touches it; not what is tested here.
                LogAssert.ignoreFailingMessages = true;
                yield return SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);
                NUnitAssert.That(SceneManager.GetActiveScene().name, Is.EqualTo("FreeformCollaboration"));
                // The consumers start recording from Start and Update: give them frames and seconds to do it.
                var until = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    NUnitAssert.That(BatchModeMicrophoneGuard.NoMicrophoneIsRecording(out var device), Is.True, "a microphone is recording: " + device);
                }
                var lipSync = System.Type.GetType("LipSyncMicInput, Assembly-CSharp");
                if (lipSync != null)
                    foreach (var found in UnityEngine.Object.FindObjectsByType(lipSync, FindObjectsInactive.Include, FindObjectsSortMode.None))
                        NUnitAssert.That(((Behaviour)found).enabled, Is.False, "LipSyncMicInput was left enabled in batchmode");
            }
            finally
            {
                Environment.SetEnvironmentVariable(DesktopObserverDecision.ForceParticipantVariable, previous);
            }
        }
    }
}
#endif
