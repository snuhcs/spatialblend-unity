// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialBlend.Samples
{
    /// <summary>One joinable Fusion session as shown in the room list.</summary>
    public readonly struct FusionSessionEntry
    {
        public FusionSessionEntry(string name, int playerCount, int maxPlayers, bool isOpen = true, bool isVisible = true)
        {
            Name = name;
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
        public bool IsFull => MaxPlayers > 0 && PlayerCount >= MaxPlayers;
        public bool CanJoin => IsOpen && !IsFull;
        public string Occupancy => MaxPlayers > 0 ? $"{PlayerCount}/{MaxPlayers}" : PlayerCount.ToString();
    }

    /// <summary>
    /// Pure room-list rules, kept apart from the runner so they can be tested
    /// without Photon. Discovery only: nothing here grants authority.
    /// </summary>
    public static class FusionSessionList
    {
        public const string RoomNamePrefix = "Room ";

        public static List<FusionSessionEntry> Build(IEnumerable<FusionSessionEntry> sessions)
        {
            if (sessions == null) return new List<FusionSessionEntry>();
            return sessions
                .Where(s => s.IsVisible && !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Smallest "Room N" not already listed.</summary>
        public static string NewRoomName(IEnumerable<FusionSessionEntry> existing)
        {
            var taken = new HashSet<string>(
                (existing ?? Enumerable.Empty<FusionSessionEntry>()).Select(s => s.Name),
                StringComparer.Ordinal);
            for (var n = 1; ; n++)
            {
                var name = RoomNamePrefix + n;
                if (!taken.Contains(name)) return name;
            }
        }
    }
}
