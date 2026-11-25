// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    public enum StatusTone
    {
        Neutral,
        Busy,
        Good,
        Bad,
    }

    /// <summary>Everything the session section needs, copied from the live components.</summary>
    public struct SessionStatusInput
    {
        public SpatialBlendConnectionState LinkState;
        public CoreParticipantRole Role;
        /// <summary>Which carrier the core link is on (FusionConnectionManager.CarrierText); empty when there is no link. Diagnostics only.</summary>
        public string CarrierText;
        public bool LibraryCompatible;
        public bool CanRetry;
        public bool LocalMode;
        public bool LocalRunning;
        /// <summary>The host left: the runner shut down under a joined client, the host's player left the room, or the core link to it ended.</summary>
        public bool HostLeft;
        /// <summary>The room the user is in, when known.</summary>
        public string RoomName;
        /// <summary>How many people are in the room, when known (0 = unknown).</summary>
        public int PeopleCount;
    }

    /// <summary>
    /// What the session block shows. Headline and Detail are the only user-facing text: plain words, no internal state
    /// names. The Link, Carrier and Library fields are developer diagnostics, shown only in the
    /// Diagnostics block of a development build and written to the log.
    /// </summary>
    public struct SessionStatusView
    {
        public string Headline;
        /// <summary>One short sentence that says what to do, for an error; empty otherwise.</summary>
        public string Detail;
        public string ConnectLabel;
        public bool ConnectEnabled;
        /// <summary>False while in a room: there is nothing to start, so the primary action is not shown.</summary>
        public bool ConnectVisible;
        /// <summary>True while in a room: "Leave room" is offered in place of the primary action.</summary>
        public bool LeaveVisible;
        /// <summary>True only in the lobby (not in a room, not connecting, no error or retry pending): the tutorial runs before connecting.</summary>
        public bool TutorialVisible;
        public bool Busy;
        public StatusTone Tone;
        /// <summary>The app cannot work (native library missing or incompatible): the user is told, and cannot connect.</summary>
        public bool Blocked;

        // Diagnostics
        public string CarrierText;
        public string LinkStateText;
        public string LibraryText;
        public bool LibraryLoaded;

        /// <summary>The developer lines of this status as one log entry.</summary>
        public string DiagnosticsLine => $"{LinkStateText}; carrier: {(string.IsNullOrEmpty(CarrierText) ? "none" : CarrierText)}; {LibraryText}";
    }

    /// <summary>Pure mapping from connection facts to the session block's text and tone.</summary>
    public static class SessionStatusPresenter
    {
        public const string HostLeftHeadline = "The room has ended";
        public const string NeedsUpdateHeadline = "This app needs an update";
        public const string NeedsUpdateDetail = "Install the latest version to join rooms.";
        public const string FailedHeadline = "Couldn't connect";
        public const string FailedDetail = "Check your Wi-Fi and try again.";

        public static SessionStatusView Build(SessionStatusInput input)
        {
            var view = new SessionStatusView
            {
                LibraryLoaded = input.LibraryCompatible,
                LibraryText = input.LibraryCompatible
                    ? "Core library: loaded"
                    : "Core library: missing or incompatible",
                CarrierText = input.CarrierText ?? string.Empty,
                LinkStateText = FormatLinkState(input),
                Detail = string.Empty,
                ConnectVisible = true,
            };

            if (input.LocalMode)
            {
                view.Headline = "Local content";
                view.LinkStateText = "Core link: not used";
                view.ConnectLabel = input.LocalRunning ? "Running" : "Start";
                view.ConnectEnabled = !input.LocalRunning;
                view.Tone = input.LocalRunning ? StatusTone.Good : StatusTone.Neutral;
                return view;
            }

            if (input.HostLeft)
            {
                // No re-host: the room is gone and the participant starts again from the room list.
                view.Headline = HostLeftHeadline;
                view.Detail = "Go back to the lobby to find another room.";
                view.Tone = StatusTone.Bad;
                view.ConnectLabel = "Back to the lobby";
                view.ConnectEnabled = true;
                return view;
            }

            if (!input.LibraryCompatible)
            {
                // A blocking message, not a hidden failure: the user is told and cannot start a session.
                view.Headline = NeedsUpdateHeadline;
                view.Detail = NeedsUpdateDetail;
                view.Tone = StatusTone.Bad;
                view.ConnectLabel = "Find a room";
                view.ConnectEnabled = false;
                view.Blocked = true;
                return view;
            }

            switch (input.LinkState)
            {
                case SpatialBlendConnectionState.Connecting:
                    view.Headline = "Connecting…";
                    view.Busy = true;
                    view.Tone = StatusTone.Busy;
                    view.ConnectLabel = "Connecting…";
                    view.ConnectEnabled = false;
                    break;
                case SpatialBlendConnectionState.Connected:
                    view.Headline = RoomHeadline(input);
                    view.Tone = StatusTone.Good;
                    view.ConnectLabel = "In a room";
                    view.ConnectEnabled = false;
                    view.ConnectVisible = false;
                    view.LeaveVisible = true;
                    break;
                case SpatialBlendConnectionState.Failed:
                    view.Headline = FailedHeadline;
                    view.Detail = FailedDetail;
                    view.Tone = StatusTone.Bad;
                    view.ConnectLabel = input.CanRetry ? "Try again" : "Find a room";
                    view.ConnectEnabled = true;
                    break;
                default:
                    view.Headline = "Not in a room";
                    view.TutorialVisible = !input.CanRetry;
                    view.Tone = StatusTone.Neutral;
                    view.ConnectLabel = input.CanRetry ? "Try again" : "Find a room";
                    view.ConnectEnabled = true;
                    break;
            }
            return view;
        }

        private static string RoomHeadline(SessionStatusInput input)
        {
            string room = string.IsNullOrWhiteSpace(input.RoomName) ? "a room" : input.RoomName.Trim();
            if (input.PeopleCount <= 0) return "In " + room;
            return input.PeopleCount == 1 ? $"In {room} · just you" : $"In {room} · {input.PeopleCount} people";
        }

        private static string FormatLinkState(SessionStatusInput input)
        {
            switch (input.LinkState)
            {
                case SpatialBlendConnectionState.Connecting: return "Core link: connecting";
                case SpatialBlendConnectionState.Connected: return "Core link: connected";
                case SpatialBlendConnectionState.Failed: return "Core link: failed";
                default: return "Core link: disconnected";
            }
        }
    }

    /// <summary>
    /// Avatar section state. Display mode and fade live in SpatialBlendSettings; every
    /// change refreshes the avatar behaviours through the injected callback.
    /// </summary>
    public sealed class AvatarSettingsPresenter
    {
        public const int PresetCount = 6;
        public const float MinFadeSeconds = 0f;
        public const float MaxFadeSeconds = 2f;

        private readonly Func<SpatialBlendSettings> _settings;
        private readonly Action _refreshAvatars;
        private int _presetIndex;

        public AvatarSettingsPresenter(Func<SpatialBlendSettings> settings, Action refreshAvatars)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _refreshAvatars = refreshAvatars ?? throw new ArgumentNullException(nameof(refreshAvatars));
        }

        public event Action Changed;

        public bool UsePreset { get; private set; } = true;
        public int PresetIndex => _presetIndex;
        public string PresetText => $"Preset {_presetIndex + 1} of {PresetCount}";
        public bool ZoneBased => _settings()?.avatarDisplayMode == AvatarDisplayMode.ZoneBased;
        public float FadeSeconds => _settings()?.fadeAvatarDuration ?? 0f;
        public bool HasSettings => _settings() != null;

        public void SetUsePreset(bool value)
        {
            UsePreset = value;
            Changed?.Invoke();
        }

        public void SetPresetIndex(float value)
        {
            _presetIndex = Math.Max(0, Math.Min(PresetCount - 1, (int)Math.Round(value)));
            Changed?.Invoke();
        }

        /// <returns>False when no settings asset is available.</returns>
        public bool SetZoneBased(bool value)
        {
            SpatialBlendSettings settings = _settings();
            if (settings == null) return false;
            settings.avatarDisplayMode = value ? AvatarDisplayMode.ZoneBased : AvatarDisplayMode.Default;
            _refreshAvatars();
            Changed?.Invoke();
            return true;
        }

        /// <returns>False when no settings asset is available.</returns>
        public bool SetFadeSeconds(float value)
        {
            SpatialBlendSettings settings = _settings();
            if (settings == null) return false;
            settings.fadeAvatarDuration = Math.Max(MinFadeSeconds, Math.Min(MaxFadeSeconds, value));
            _refreshAvatars();
            Changed?.Invoke();
            return true;
        }

        public static string FormatFade(float seconds)
        {
            return seconds <= 0f ? "Instant" : $"{seconds:0.0} s";
        }
    }

    /// <summary>Scene section state; each setter forwards to the matching scene action.</summary>
    public sealed class SceneControlsPresenter
    {
        public const float MinBrightness = -1f;
        public const float MaxBrightness = 1f;

        private readonly Action<bool> _applyAnchorsVisible;
        private readonly Action<bool> _applyManipulation;
        private readonly Action<float> _applyBrightness;
        private readonly Action _rebuildNavMesh;

        public SceneControlsPresenter(Action<bool> applyAnchorsVisible, Action<bool> applyManipulation,
                                      Action<float> applyBrightness, Action rebuildNavMesh)
        {
            _applyAnchorsVisible = applyAnchorsVisible;
            _applyManipulation = applyManipulation;
            _applyBrightness = applyBrightness;
            _rebuildNavMesh = rebuildNavMesh;
        }

        public event Action Changed;

        public bool AnchorsVisible { get; private set; }
        public bool ManipulationEnabled { get; private set; }
        public float Brightness { get; private set; }
        public int NavMeshRebuilds { get; private set; }

        public void SetAnchorsVisible(bool value)
        {
            AnchorsVisible = value;
            _applyAnchorsVisible?.Invoke(value);
            Changed?.Invoke();
        }

        /// <summary>True selects SceneEdit, false selects Interaction.</summary>
        public void SetManipulation(bool value)
        {
            ManipulationEnabled = value;
            _applyManipulation?.Invoke(value);
            Changed?.Invoke();
        }

        public void SetBrightness(float value)
        {
            Brightness = Math.Max(MinBrightness, Math.Min(MaxBrightness, value));
            _applyBrightness?.Invoke(Brightness);
            Changed?.Invoke();
        }

        public void RebuildNavMesh()
        {
            NavMeshRebuilds++;
            _rebuildNavMesh?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Mirror state set elsewhere (for example entering a session) without re-applying it.</summary>
        public void Mirror(bool anchorsVisible, bool manipulation)
        {
            AnchorsVisible = anchorsVisible;
            ManipulationEnabled = manipulation;
            Changed?.Invoke();
        }
    }
}
