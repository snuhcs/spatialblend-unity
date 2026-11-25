// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialBlend.Samples
{
    public enum CoreParticipantRole
    {
        None,
        Host,
        Client,
    }

    public enum MatchmakingPhase
    {
        Browsing,
        InSession,
        Error,
    }

    /// <summary>One room as the matchmaker sees it. No Fusion types.</summary>
    public readonly struct MatchmakingRoom
    {
        public MatchmakingRoom(string name, int playerCount, int maxPlayers, bool isOpen = true, bool isVisible = true)
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsOpen = isOpen;
            IsVisible = isVisible;
        }

        public string Name { get; }
        public int PlayerCount { get; }
        public int MaxPlayers { get; }
        public bool IsOpen { get; }
        public bool IsVisible { get; }
        public bool CanJoin => new FusionSessionEntry(Name, PlayerCount, MaxPlayers, IsOpen, IsVisible).CanJoin;

        public FusionSessionEntry ToEntry() =>
            new FusionSessionEntry(Name, PlayerCount, MaxPlayers, IsOpen, IsVisible);

        public static MatchmakingRoom FromEntry(FusionSessionEntry entry) =>
            new MatchmakingRoom(entry.Name, entry.PlayerCount, entry.MaxPlayers, entry.IsOpen, entry.IsVisible);
    }

    /// <summary>Outcome of one list, host, join, or leave decision.</summary>
    public readonly struct MatchmakingDecision
    {
        public MatchmakingDecision(bool accepted, MatchmakingPhase phase, CoreParticipantRole role, string sessionName,
            string error, IReadOnlyList<MatchmakingRoom> rooms)
        {
            Accepted = accepted;
            Phase = phase;
            Role = role;
            SessionName = sessionName;
            Error = error;
            Rooms = rooms ?? Array.Empty<MatchmakingRoom>();
        }

        public bool Accepted { get; }
        public MatchmakingPhase Phase { get; }
        public CoreParticipantRole Role { get; }
        public string SessionName { get; }
        public string Error { get; }
        public IReadOnlyList<MatchmakingRoom> Rooms { get; }
    }

    /// <summary>
    /// List, then host or join intent, then the SpatialBlend core role.
    /// Hosting a free name makes this player the core host. Joining makes
    /// this player a core client of that room's host. The room's name is the only thing shared: no address is
    /// advertised or looked up, and the core link rides the Photon session (FusionConnectionManager).
    /// </summary>
    public sealed class FreeformMatchmaking
    {
        public const string NameCollisionError = "A room with that name already exists.";
        public const string VanishedRoomError = "That room is no longer listed.";
        public const string EmptyNameError = "A room name is required.";
        public const string NotJoinableError = "That room cannot be joined.";
        public const string NotListedYetError = "The room list is not ready.";
        public const string BusyError = "Leave the current room before starting another.";
        /// <summary>Shown when a desktop observer tries to create a room. They can only join one a participant created.</summary>
        public const string ObserverHostError = "Observers can only watch existing rooms";

        private readonly List<MatchmakingRoom> _rooms = new();
        private readonly HashSet<string> _takenNames = new(StringComparer.Ordinal);

        public IReadOnlyList<MatchmakingRoom> Rooms => _rooms;
        public bool HasList { get; private set; }
        public bool IsEmpty => HasList && _rooms.Count == 0;
        public MatchmakingPhase Phase { get; private set; } = MatchmakingPhase.Browsing;
        public CoreParticipantRole Role { get; private set; } = CoreParticipantRole.None;
        public string SessionName { get; private set; }
        public string Error { get; private set; }
        /// <summary>False for a desktop observer: <see cref="TryHost"/> refuses, and the room list disables New room.</summary>
        public bool HostingAllowed { get; private set; } = true;

        public void SetHostingAllowed(bool allowed) => HostingAllowed = allowed;

        public MatchmakingDecision Refresh(IEnumerable<MatchmakingRoom> rooms)
        {
            _rooms.Clear();
            _takenNames.Clear();
            var entries = new List<FusionSessionEntry>();
            if (rooms != null)
            {
                foreach (var room in rooms)
                {
                    if (!string.IsNullOrEmpty(room.Name)) _takenNames.Add(room.Name);
                    entries.Add(room.ToEntry());
                }
            }

            foreach (var entry in FusionSessionList.Build(entries))
                _rooms.Add(MatchmakingRoom.FromEntry(entry));
            HasList = true;
            if (Phase != MatchmakingPhase.InSession)
            {
                Phase = MatchmakingPhase.Browsing;
                Error = null;
            }

            return Snapshot(true);
        }

        public void ClearList()
        {
            _rooms.Clear();
            _takenNames.Clear();
            HasList = false;
            if (Phase != MatchmakingPhase.InSession)
            {
                Phase = MatchmakingPhase.Browsing;
                Error = null;
            }
        }

        /// <summary>Smallest "Room N" that is not already taken, including hidden rooms.</summary>
        public string NextFreeName() =>
            FusionSessionList.NewRoomName(_takenNames.Select(name => new FusionSessionEntry(name, 1, 9)));

        public MatchmakingDecision TryHost(string sessionName)
        {
            if (!HostingAllowed) return Refuse(ObserverHostError, MatchmakingPhase.Browsing);
            if (Phase == MatchmakingPhase.InSession) return Refuse(BusyError, MatchmakingPhase.InSession);
            if (!HasList) return Refuse(NotListedYetError, MatchmakingPhase.Error);
            var name = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim();
            if (name == null) return Refuse(EmptyNameError, MatchmakingPhase.Error);
            if (_takenNames.Contains(name)) return Refuse(NameCollisionError, MatchmakingPhase.Error);

            Role = CoreParticipantRole.Host;
            SessionName = name;
            Phase = MatchmakingPhase.InSession;
            Error = null;
            return Snapshot(true);
        }

        public MatchmakingDecision TryJoin(string sessionName)
        {
            if (Phase == MatchmakingPhase.InSession) return Refuse(BusyError, MatchmakingPhase.InSession);
            if (!HasList) return Refuse(NotListedYetError, MatchmakingPhase.Error);
            var name = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim();
            if (name == null) return Refuse(EmptyNameError, MatchmakingPhase.Error);

            MatchmakingRoom match = default;
            var found = false;
            foreach (var room in _rooms)
            {
                if (!string.Equals(room.Name, name, StringComparison.Ordinal)) continue;
                match = room;
                found = true;
                break;
            }

            if (!found) return Refuse(VanishedRoomError, MatchmakingPhase.Error);
            if (!match.CanJoin) return Refuse(NotJoinableError, MatchmakingPhase.Browsing);

            Role = CoreParticipantRole.Client;
            SessionName = name;
            Phase = MatchmakingPhase.InSession;
            Error = null;
            return Snapshot(true);
        }

        public MatchmakingDecision Leave()
        {
            Role = CoreParticipantRole.None;
            SessionName = null;
            Phase = MatchmakingPhase.Browsing;
            Error = null;
            return Snapshot(true);
        }

        private MatchmakingDecision Refuse(string error, MatchmakingPhase phase)
        {
            Phase = phase;
            Error = error;
            return Snapshot(false);
        }

        private MatchmakingDecision Snapshot(bool accepted) =>
            new MatchmakingDecision(accepted, Phase, Role, SessionName, Error, _rooms.ToArray());
    }
}
