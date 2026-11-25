// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// What a join (or host) that did not get into its room tells the user, and the one notice that survives the reset that follows it.
    /// <see cref="FusionSessionBrowser.ReportStartFailed"/> returns the lobby to a clean state by <c>FusionBootstrap.ShutdownAll</c>
    /// (Photon's stock reset: the scene reloads, as for Leave), so the sentence is carried across the reload here and the new control panel
    /// shows it. Every <see cref="ShutdownReason"/> a start can end with is mapped to one plain sentence; the technical reason goes to the log.
    /// </summary>
    public static class JoinFailure
    {
        public const string Title = "Couldn't join the room";
        public const string Fallback = "Couldn't join the room. Try again.";

        private static string s_notice;

        public static string ToUserMessage(ShutdownReason reason)
        {
            switch (reason)
            {
                case ShutdownReason.GameIsFull:
                    return "That room is full. Pick another room or make a new one.";
                case ShutdownReason.GameClosed:
                    return "That room is closed. Pick another room or make a new one.";
                case ShutdownReason.GameNotFound:
                    return "That room is gone. Find a room to see what is open.";
                case ShutdownReason.GameIdAlreadyExists:
                    return "A room with that name already exists. Find a room and pick it, or use another name.";
                case ShutdownReason.PhotonCloudTimeout:
                case ShutdownReason.ConnectionTimeout:
                case ShutdownReason.OperationTimeout:
                    return "Joining took too long. Check your Wi-Fi and try again.";
                case ShutdownReason.Error:
                case ShutdownReason.ConnectionRefused:
                case ShutdownReason.InvalidRegion:
                case ShutdownReason.DisconnectedByPluginLogic:
                case ShutdownReason.ServerInRoom:
                    return "Couldn't reach the room. Check your Wi-Fi and try again.";
                case ShutdownReason.MaxCcuReached:
                    return "The service is busy right now. Try again in a moment.";
                case ShutdownReason.InvalidAuthentication:
                case ShutdownReason.CustomAuthenticationFailed:
                case ShutdownReason.AuthenticationTicketExpired:
                    return "Couldn't sign in to the room service. Try again in a moment.";
                case ShutdownReason.OperationCanceled:
                    return "Joining was cancelled.";
                default:
                    return Fallback;
            }
        }

        /// <summary>Keeps the sentence for the control panel that the reset brings up.</summary>
        public static void Carry(string message) => s_notice = string.IsNullOrWhiteSpace(message) ? null : message;

        /// <summary>The carried sentence, once: null when there is none.</summary>
        public static string Take()
        {
            var notice = s_notice;
            s_notice = null;
            return notice;
        }

        public static bool HasNotice => s_notice != null;
    }
}
