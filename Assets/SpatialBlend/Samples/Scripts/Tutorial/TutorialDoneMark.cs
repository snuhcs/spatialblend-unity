// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>Where the "tutorial done" mark lives. PlayerPrefs on the device; a fake in tests.</summary>
    public interface ITutorialKeyValueStore
    {
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        void Save();
    }

    public sealed class PlayerPrefsKeyValueStore : ITutorialKeyValueStore
    {
        public int GetInt(string key, int fallback) => PlayerPrefs.GetInt(key, fallback);
        public void SetInt(string key, int value) => PlayerPrefs.SetInt(key, value);
        public void Save() => PlayerPrefs.Save();
    }

    /// <summary>
    /// The local mark that the tutorial was gone through once. The lobby's hint dot shows until it is set. Storage can fail
    /// (a full disk, a blocked store); then the tutorial still works and the dot simply stays. Every read and write is wrapped.
    /// </summary>
    public sealed class TutorialDoneMark
    {
        public const string Key = "spatialblend.freeform.tutorial.done";

        private readonly ITutorialKeyValueStore _store;
        private bool _doneInSession;

        public TutorialDoneMark(ITutorialKeyValueStore store = null) => _store = store ?? new PlayerPrefsKeyValueStore();

        private static TutorialDoneMark _shared = new TutorialDoneMark();

        /// <summary>The device-wide mark, shared by every lobby panel.</summary>
        public static TutorialDoneMark Shared => _shared;

        /// <summary>Tests swap the shared mark for one over a fake store, so they never write the real PlayerPrefs. Null restores the real one.</summary>
        internal static void ReplaceShared(TutorialDoneMark mark) => _shared = mark ?? new TutorialDoneMark();

        public bool IsDone
        {
            get
            {
                if (_doneInSession) return true;
                try { return _store.GetInt(Key, 0) == 1; }
                catch (Exception e)
                {
                    Debug.LogWarning("[Tutorial] Could not read the done mark: " + e.Message);
                    return false;
                }
            }
        }

        /// <summary>Sets the mark. Returns false when it could not be saved (it still counts for this session).</summary>
        public bool MarkDone()
        {
            _doneInSession = true;
            try
            {
                _store.SetInt(Key, 1);
                _store.Save();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tutorial] Could not save the done mark: " + e.Message);
                return false;
            }
        }
    }
}
