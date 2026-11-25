// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    public enum SimulatedRoomState
    {
        Browsing,
        Connecting,
        Connected,
    }

    /// <summary>
    /// Chapter 4's demo of hosting, joining and connecting. It is a model with nothing behind it: it holds no network type, so it
    /// cannot create or join a room. The real room list is only read (see <c>ControlPanel.ShowRoomListReadOnly</c>); a tap on a
    /// room or on New room in the tutorial lands here instead.
    /// </summary>
    public sealed class TutorialSimulatedRoom
    {
        public const double ConnectSeconds = 3.0;

        /// <summary>Shown when the real room list cannot be opened (offline, or no network stack in the Editor).</summary>
        public static readonly IReadOnlyList<string> SampleRooms = new[] { "Room 1", "Room 2" };

        private readonly ITutorialClock _clock;
        private double _startedAt;

        public TutorialSimulatedRoom(ITutorialClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        public SimulatedRoomState State { get; private set; } = SimulatedRoomState.Browsing;
        public string RoomName { get; private set; }
        /// <summary>True when the user pressed New room (hosting), false when they tapped a room (joining).</summary>
        public bool Hosting { get; private set; }
        /// <summary>How many times a room was joined or created: always the simulated ones, never a real one.</summary>
        public int SimulatedActions { get; private set; }
        public event Action Changed;

        public double Progress => State switch
        {
            SimulatedRoomState.Connecting => Math.Min(1.0, (_clock.Now - _startedAt) / ConnectSeconds),
            SimulatedRoomState.Connected => 1.0,
            _ => 0.0,
        };

        public string Headline => State switch
        {
            SimulatedRoomState.Connecting => (Hosting ? "Hosting " : "Joining ") + RoomName + "...",
            SimulatedRoomState.Connected => "In " + RoomName + " (demo)",
            _ => "Not in a room",
        };

        public string Detail => State switch
        {
            SimulatedRoomState.Connecting => "Demo only. No room is created.",
            SimulatedRoomState.Connected => "Demo only. Nobody else is here.",
            _ => string.Empty,
        };

        public void Join(string roomName) => Begin(roomName, false);

        /// <summary>The smallest free "Room N" would be hosted for real; the demo names it the same way.</summary>
        public void Host() => Begin("Room " + (SampleRooms.Count + 1), true);

        private void Begin(string roomName, bool hosting)
        {
            RoomName = string.IsNullOrWhiteSpace(roomName) ? "Room 1" : roomName;
            Hosting = hosting;
            State = SimulatedRoomState.Connecting;
            _startedAt = _clock.Now;
            SimulatedActions++;
            Changed?.Invoke();
        }

        public void Tick()
        {
            if (State != SimulatedRoomState.Connecting) return;
            if (_clock.Now - _startedAt < ConnectSeconds) return;
            State = SimulatedRoomState.Connected;
            Changed?.Invoke();
        }

        public void Reset()
        {
            if (State == SimulatedRoomState.Browsing && SimulatedActions == 0) return;
            State = SimulatedRoomState.Browsing;
            RoomName = null;
            Hosting = false;
            Changed?.Invoke();
        }
    }
}
