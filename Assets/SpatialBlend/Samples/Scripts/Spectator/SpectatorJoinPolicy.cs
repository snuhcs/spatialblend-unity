// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The decisions of a desktop spectator that need no Fusion: which room to join, whether the room list allows it, and the
    /// one role a spectator can have. A spectator is a core CLIENT of the room's host and nothing else: no decision here ever
    /// yields <see cref="CoreParticipantRole.Host"/>, and a room that is not listed is waited for, never created.
    /// </summary>
    public static class SpectatorJoinPolicy
    {
        /// <summary>Launch flag: <c>-spectate-room NAME</c> joins that room at start (the name is the whole address).</summary>
        public const string RoomFlag = "-spectate-room";
        /// <summary>Environment fallback when no flag is given.</summary>
        public const string RoomEnvironmentVariable = "SPATIALBLEND_SPECTATE_ROOM";
        public const string EmptyNameError = "A room name is required.";
        public const string FullError = "That room is full or closed, so a spectator cannot join it.";
        public const string NotFoundError = "No room with that name is listed. A spectator only joins an existing room; it never creates one.";
        public const string RoomCreatedByUsError = "The room did not exist when this spectator connected, so it was left again. A spectator never hosts.";

        public const string WaitingForParticipantsError = "The room's participants have not all arrived, so the spectator did not join: a spectator counts as a player in the room and would start the participants' session before they are all there.";

        public enum WaitReason
        {
            None,
            /// <summary>The room list has not arrived yet.</summary>
            ListNotReady,
            /// <summary>The room is not in the list (yet).</summary>
            NotListed,
            /// <summary>The room is listed, but has fewer participants than the room needs to start its apps.</summary>
            TooFewParticipants,
        }

        public enum Verdict
        {
            /// <summary>The room is listed and joinable: start the runner as a core client.</summary>
            Join,
            /// <summary>The list has not arrived, or the room is not in it yet: keep listening, up to the caller's timeout.</summary>
            Wait,
            /// <summary>Joining is impossible as things stand (empty name, full or closed room).</summary>
            Refuse,
        }

        public readonly struct Decision
        {
            public Decision(Verdict verdict, string error, WaitReason reason = WaitReason.None, string notice = null)
            {
                Verdict = verdict;
                Error = error;
                Reason = reason;
                Notice = notice;
            }

            public WaitReason Reason { get; }
            /// <summary>What to tell the user while waiting; null when there is nothing to say.</summary>
            public string Notice { get; }

            public Verdict Verdict { get; }
            public string Error { get; }
            /// <summary>Always <see cref="CoreParticipantRole.Client"/>: a spectator has no other role.</summary>
            public CoreParticipantRole Role => CoreParticipantRole.Client;
        }

        /// <summary>The room named by the launch flag, else the environment, else null. Whitespace-only is null.</summary>
        public static string ResolveRoom(IReadOnlyList<string> args, Func<string, string> environment)
        {
            if (args != null)
            {
                for (var i = 0; i < args.Count - 1; i++)
                {
                    if (!string.Equals(args[i], RoomFlag, StringComparison.Ordinal)) continue;
                    var named = Normalize(args[i + 1]);
                    // A value that starts with '-' is the next flag, not a room name.
                    if (named != null && IsSafeAsArgument(named)) return named;
                }
            }

            return Normalize(environment?.Invoke(RoomEnvironmentVariable));
        }

        /// <summary>Launch flag: <c>-photon-region CODE</c> pins the Photon region (asia, eu, jp, kr, us, ...).</summary>
        public const string RegionFlag = "-photon-region";
        public const string RegionEnvironmentVariable = "SPATIALBLEND_PHOTON_REGION";
        /// <summary>
        /// The region when neither the flag, the environment nor the project's PhotonAppSettings.FixedRegion names one: empty, which
        /// Photon reads as "the best region by ping", like every participant whose project leaves FixedRegion empty. A room is listed
        /// only in its own region, so a spectator whose best region differs from the participants' (two regions with similar ping)
        /// does not see the room: set the same FixedRegion for every device, or pass <see cref="RegionFlag"/>
        /// (docs/two-headset-room.md, "Photon region").
        /// </summary>
        public const string BestRegion = "";
        /// <summary>What a status line shows for <see cref="BestRegion"/>.</summary>
        public const string BestRegionLabel = "best by ping";

        /// <summary>The Photon region and where it came from: the flag, else the environment, else the project's fixed region, else <see cref="BestRegion"/>.</summary>
        public static string ResolveRegion(IReadOnlyList<string> args, Func<string, string> environment, string configured, out string source)
        {
            if (args != null)
            {
                for (var i = 0; i < args.Count - 1; i++)
                {
                    if (!string.Equals(args[i], RegionFlag, StringComparison.Ordinal)) continue;
                    var named = Normalize(args[i + 1]);
                    if (named != null && IsSafeAsArgument(named)) { source = "flag"; return named.ToLowerInvariant(); }
                }
            }
            var env = Normalize(environment?.Invoke(RegionEnvironmentVariable));
            if (env != null) { source = "environment"; return env.ToLowerInvariant(); }
            var fixedRegion = Normalize(configured);
            if (fixedRegion != null) { source = "project"; return fixedRegion.ToLowerInvariant(); }
            source = "best";
            return BestRegion;
        }

        /// <summary>The region as a status line shows it.</summary>
        public static string RegionLabel(string region) => string.IsNullOrEmpty(region) ? BestRegionLabel : region;

        /// <summary>The not-found error's region hint: a room is listed only in its own Photon region.</summary>
        public static string RegionHint(string region) =>
            $" This spectator looks in Photon region '{RegionLabel(region)}'; if the participants are in another region, launch it with {RegionFlag} <code> (asia, eu, jp, kr, us, ...).";

        public static string Normalize(string name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        /// <summary>
        /// True for a room name that can be written after <see cref="RoomFlag"/> on a command line: Photon room names are chosen by any
        /// player, and Unity's player reads its own flags (-batchmode, -logFile, ...) anywhere in its arguments, so a name that starts
        /// with '-' would be read as a flag and is never passed on a command line.
        /// </summary>
        public static bool IsSafeAsArgument(string name) => name != null && name.Length > 0 && name[0] != '-';

        /// <summary>
        /// Whether to join <paramref name="room"/> given the room list. Pure; the role of any outcome is Client.
        /// <paramref name="participantsRequired"/> is the number of players the room needs to start its apps
        /// (<see cref="SpatialBlendSettings.playersToStartApps"/>, which <c>FusionConnectionManager.TryStartApps</c> compares the whole
        /// roster with). A spectator is in that roster, so joining a room that has fewer would start the participants' session before
        /// they are all there, and leaving it again before it has started retires their round. The spectator therefore waits until
        /// the room has that many players without it.
        /// </summary>
        public static Decision Evaluate(string room, bool listReady, IEnumerable<FusionSessionEntry> sessions, int participantsRequired = 0)
        {
            var name = Normalize(room);
            if (name == null) return new Decision(Verdict.Refuse, EmptyNameError);
            if (!listReady || sessions == null) return new Decision(Verdict.Wait, null, WaitReason.ListNotReady);
            foreach (var entry in sessions)
            {
                if (!string.Equals(entry.Name, name, StringComparison.Ordinal)) continue;
                if (!entry.CanJoin) return new Decision(Verdict.Refuse, FullError);
                if (entry.PlayerCount < participantsRequired)
                    return new Decision(Verdict.Wait, null, WaitReason.TooFewParticipants,
                        $"Waiting for the room's participants to start: it has {entry.PlayerCount} of the {participantsRequired} players it needs.");
                return new Decision(Verdict.Join, null);
            }

            return new Decision(Verdict.Wait, null, WaitReason.NotListed);
        }

        /// <summary>
        /// True when a runner that was meant to join has in fact created the room: it is the Shared-mode master and alone in it
        /// (the room vanished between the list and the start, and Fusion's Shared mode creates a missing room). The spectator
        /// then shuts down instead of staying as the room's only member.
        /// </summary>
        public static bool CreatedTheRoom(bool isSharedModeMasterClient, int activePlayerCount) =>
            isSharedModeMasterClient && activePlayerCount <= 1;

        /// <summary>What a spectator's share handler answers for received content of a kind.</summary>
        public static OnShareResponse ShareResponse(bool isAvatar, bool receiveAvatarsAsActors = false)
        {
            // A received avatar would get a receiver actor in this client's core space (SpatialBlendManager.EnsureV2ActorForAvatar).
            // A spectator registers no actor, so avatars are not taken into the core; everything else is shown.
            return isAvatar && !receiveAvatarsAsActors ? OnShareResponse.Reject() : OnShareResponse.AddAsNew();
        }
    }
}
