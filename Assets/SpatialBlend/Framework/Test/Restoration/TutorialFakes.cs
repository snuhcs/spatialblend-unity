// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;

namespace SpatialBlend.Tests
{
    internal sealed class FakeTutorialClock : ITutorialClock
    {
        public double Now { get; set; }
        public void Advance(double seconds) => Now += seconds;
    }

    internal sealed class FakeTutorialHost : ITutorialHost
    {
        public readonly List<string> Log = new List<string>();
        public bool SceneEdit { get; set; }
        public bool AnchorsVisible { get => _anchors; set { _anchors = value; AnchorWrites++; } }
        public float Brightness { get; set; }
        public bool HandTrackingActive { get; set; }
        public bool RoomAnchorsAvailable { get; set; } = true;
        public bool LobbyHidden;
        public double VoiceSeconds = 10.0;
        public TutorialEnd EndedWith = TutorialEnd.None;
        public readonly List<string> Played = new List<string>();
        public int SceneEditSets;
        public int AnchorWrites;
        private bool _anchors;

        void ITutorialHost.SetLobbyHidden(bool hidden) { LobbyHidden = hidden; Log.Add("lobby:" + (hidden ? "hidden" : "shown")); }
        public void FrameEntered(TutorialFrame frame) => Log.Add("enter:" + frame.Id);
        public void FrameLeft(TutorialFrame frame) => Log.Add("leave:" + frame.Id);
        public readonly List<string> Completed = new List<string>();
        public void StepStarted(TutorialFrame frame, int stepIndex) { Log.Add("start:" + frame.Id + "/" + stepIndex); }
        public void StepCompleted(TutorialFrame frame, int stepIndex) { Completed.Add(frame.Id + "/" + stepIndex); Log.Add("step:" + frame.Id + "/" + stepIndex); }
        public double PlayVoice(string clipName) { Played.Add(clipName); return VoiceSeconds; }
        public void StopVoice() => Log.Add("stop-voice");
        public void PauseVoice(bool paused) => Log.Add(paused ? "pause-voice" : "resume-voice");
        public void Ended(TutorialEnd end) { EndedWith = end; Log.Add("ended:" + end); }
    }

    internal sealed class FakeKeyValueStore : ITutorialKeyValueStore
    {
        public readonly Dictionary<string, int> Values = new Dictionary<string, int>();
        public bool ThrowOnRead, ThrowOnWrite, ThrowOnSave;
        public int Saves;
        public int GetInt(string key, int fallback)
        {
            if (ThrowOnRead) throw new InvalidOperationException("read blocked");
            return Values.TryGetValue(key, out int v) ? v : fallback;
        }
        public void SetInt(string key, int value)
        {
            if (ThrowOnWrite) throw new InvalidOperationException("write blocked");
            Values[key] = value;
        }
        public void Save()
        {
            if (ThrowOnSave) throw new InvalidOperationException("disk full");
            Saves++;
        }
    }

    internal sealed class FakeModeAssist : ITutorialModeAssist
    {
        public int Suspends, Resumes, Releases;
        /// <summary>Held suspended: more suspensions than releases (the real assist counts owners, the tutorial being one).</summary>
        public bool IsSuspended => Suspends > Resumes + Releases;
        public void Suspend() => Suspends++;
        public void Resume() => Resumes++;
        public void Release() => Releases++;
    }
}
#endif
