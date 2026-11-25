// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Clock-driven local replay only; completion retains the final
    /// snapshot until Stop.</summary>
    public sealed class AnchorPoseReplayPlayer : MonoBehaviour
    {
        [SerializeField] private bool _playOnUpdate;
        private RecordedAnchorPoseSource _source;
        private AnchorPoseRecording _recording;
        private int _cursor;
        private bool _running;
        private bool _advancing;
        private bool _stopRequested;
        private long _configurationVersion;
        private long _runningConfigurationVersion;
        private double _lastElapsed;
        private double _beganAt;
        private double _sourceTimeOrigin;
        // Test seam only; production uses Unity's unscaled monotonic clock.
        internal Func<double> ClockForTesting;
        internal bool PlayOnUpdateForTesting { get => _playOnUpdate; set => _playOnUpdate = value; }

        public bool IsRunning => _running;
        public int NextFrameIndex => _cursor;
        /// <summary>Monotonic identity for the currently configured recording/source pair.</summary>
        public long ConfigurationVersion => _configurationVersion;
        public bool HasConfiguration => _source != null && _recording != null;
        public bool AutoAdvanceOnUpdate => _playOnUpdate;

        public bool Configure(RecordedAnchorPoseSource source, AnchorPoseRecording recording,
                              out string error)
        {
            error = null;
            if (_advancing)
            {
                error = "Cannot configure while a replay frame is being submitted.";
                return false;
            }
            // Validate the prospective configuration before disturbing a live replay.
            if (source == null || recording == null || recording.FrameCount == 0)
            {
                error = "A source and nonempty committed recording are required.";
                return false;
            }
            if (!CompleteStop())
            {
                error = "Existing replay sink rejected reset acknowledgement.";
                return false;
            }
            _source = source;
            _recording = recording.Freeze();
            _cursor = 0;
            _runningConfigurationVersion = 0;
            ++_configurationVersion;
            return true;
        }

        /// <summary>
        /// Clears exactly the configuration identity supplied by its owner. A stale
        /// owner cannot clear a later recording/source pair.
        /// </summary>
        public bool TryReleaseConfiguration(long expectedVersion, out string error)
        {
            error = null;
            if (expectedVersion != _configurationVersion || !HasConfiguration)
            {
                error = "Replay configuration is no longer current.";
                return false;
            }
            if (_advancing)
            {
                error = "Cannot release replay configuration while a frame is being submitted.";
                return false;
            }

            RecordedAnchorPoseSource expectedSource = _source;
            AnchorPoseRecording expectedRecording = _recording;
            if (!CompleteStop())
            {
                error = "Existing replay sink rejected reset acknowledgement.";
                return false;
            }
            if (expectedVersion != _configurationVersion ||
                !ReferenceEquals(expectedSource, _source) ||
                !ReferenceEquals(expectedRecording, _recording))
            {
                error = "Replay configuration changed while release was being acknowledged.";
                return false;
            }

            _source = null;
            _recording = null;
            _runningConfigurationVersion = 0;
            ++_configurationVersion;
            return true;
        }

        public bool Begin(out string error)
        {
            error = null;
            if (_advancing)
            {
                error = "Cannot begin while a replay frame is being submitted.";
                return false;
            }
            if (_running)
            {
                error = "Replay is already running.";
                return false;
            }
            if (_source == null || _recording == null)
            {
                error = "Player is not configured.";
                return false;
            }
            long configurationVersion = _configurationVersion;
            RecordedAnchorPoseSource source = _source;
            AnchorPoseRecording recording = _recording;
            if (!source.TrySetKnownAnchorIds(recording.KnownAnchorIds, out error))
                return false;
            if (!ConfigurationMatches(configurationVersion, source, recording))
            {
                error = "Replay configuration changed while beginning.";
                return false;
            }
            _cursor = 0;
            _lastElapsed = 0d;
            _beganAt = Now();
            if (!recording.TryGetFrame(0, out AnchorPoseFrame first))
            {
                error = "Recording has no first frame.";
                return false;
            }
            _sourceTimeOrigin = first.timeSeconds;
            _stopRequested = false;
            _running = true;
            _runningConfigurationVersion = configurationVersion;
            return true;
        }

        public bool TryAdvanceTo(double elapsedSeconds, out string error)
        {
            error = null;
            if (!_running || _advancing)
            {
                error = "Replay is not running or is already advancing.";
                return false;
            }
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds < 0d)
            {
                error = "Elapsed time must be finite and nonnegative.";
                return false;
            }
            if (elapsedSeconds < _lastElapsed)
            {
                error = "Elapsed time cannot move backwards.";
                return false;
            }

            long configurationVersion = _runningConfigurationVersion;
            RecordedAnchorPoseSource source = _source;
            AnchorPoseRecording recording = _recording;
            if (!ConfigurationMatches(configurationVersion, source, recording))
            {
                _running = false;
                error = "Replay configuration changed while advancing.";
                return false;
            }

            _advancing = true;
            try
            {
                while (!_stopRequested && _cursor < recording.FrameCount &&
                       recording.TryGetFrame(_cursor, out AnchorPoseFrame frame) &&
                       frame.timeSeconds - _sourceTimeOrigin <= elapsedSeconds)
                {
                    if (!source.TrySubmit(frame, out error))
                        return false; // retry exact frame; cursor remains unchanged.
                    if (!ConfigurationMatches(configurationVersion, source, recording))
                    {
                        _running = false;
                        error = "Replay configuration changed while a frame was being submitted.";
                        return false;
                    }
                    if (_stopRequested)
                        break;
                    ++_cursor;
                }
                if (!_stopRequested && _cursor == recording.FrameCount)
                    _running = false;
                if (!_stopRequested)
                    _lastElapsed = elapsedSeconds;
                return !_stopRequested;
            }
            finally
            {
                _advancing = false;
                if (_stopRequested)
                    CompleteStop();
            }
        }

        public void Stop()
        {
            if (_advancing)
            {
                _stopRequested = true;
                _running = false;
                return;
            }
            CompleteStop();
        }

        private bool CompleteStop()
        {
            // Stop is void for compatibility. Do not clear replay cursor/fences if
            // a checked native sink refuses reset; callers can still observe the
            // stopped state and retry Begin after the native failure is resolved.
            if (_source != null && !_source.TryReplayReset())
            {
                _stopRequested = false;
                _running = false;
                return false;
            }
            _stopRequested = false;
            _running = false;
            _runningConfigurationVersion = 0;
            _cursor = 0;
            _lastElapsed = 0d;
            return true;
        }

        private bool ConfigurationMatches(long version, RecordedAnchorPoseSource source,
                                          AnchorPoseRecording recording)
        {
            return version != 0 && version == _configurationVersion &&
                ReferenceEquals(source, _source) && ReferenceEquals(recording, _recording);
        }

        private void OnDisable() => Stop();
        private void OnDestroy() => Stop();
        private void Update()
        {
            if (_playOnUpdate && _running && !_advancing)
                TryAdvanceTo(Now() - _beganAt, out _);
        }
        internal void TickUpdateForTesting() => Update();
        private double Now() => ClockForTesting != null ? ClockForTesting() : Time.unscaledTimeAsDouble;
    }
}
