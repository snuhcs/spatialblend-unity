// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;
using SpatialBlend;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Whether this process is a desktop observer. A desktop player (and the Editor) with no active HMD is an
    /// observer: it joins the freeform session and does not publish an avatar, an actor, or content. A headset,
    /// or a non-desktop player (Quest), stays a participant. An HMD wins over a force-observer flag, and
    /// force-observer never applies off a desktop platform.
    /// </summary>
    public static class DesktopObserverDecision
    {
        public const string ForceParticipantVariable = "SPATIALBLEND_FORCE_PARTICIPANT";
        public const string ForceObserverVariable = "SPATIALBLEND_FORCE_OBSERVER";
        public const string ForceParticipantArgument = "-participant";
        public const string ForceObserverArgument = "-observer";

        public static bool IsDesktopPlatform(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// <paramref name="xrActive"/> is an HMD or a running XR session. Force-participant wins over force-observer.
        /// Force-observer does not override an active headset, and it never makes a non-desktop player (Quest) an observer.
        /// </summary>
        public static bool IsObserver(RuntimePlatform platform, bool xrActive, bool forceParticipant, bool forceObserver)
        {
            if (xrActive || forceParticipant || !IsDesktopPlatform(platform)) return false;
            if (forceObserver) return true;
            return true;
        }

        public static bool FromRuntime(RuntimePlatform platform, bool xrActive) =>
            FromRuntime(platform, xrActive, Environment.GetEnvironmentVariable, Environment.GetCommandLineArgs());

        public static bool FromRuntime(RuntimePlatform platform, bool xrActive, Func<string, string> environment, string[] args) =>
            IsObserver(platform, xrActive, Flag(environment, ForceParticipantVariable) || HasArgument(args, ForceParticipantArgument),
                Flag(environment, ForceObserverVariable) || HasArgument(args, ForceObserverArgument));

        public static bool Flag(Func<string, string> environment, string name)
        {
            if (environment == null || string.IsNullOrEmpty(name)) return false;
            string value = environment(name);
            return string.Equals(value, "1", StringComparison.Ordinal) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasArgument(string[] args, string argument)
        {
            if (args == null || string.IsNullOrEmpty(argument)) return false;
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], argument, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reads the headset the rest of the app already trusts (<see cref="OVRManager.isHmdPresent"/>), and a running
    /// XR device when the XR module is loaded. No headset and no module means not XR.
    /// </summary>
    public static class DesktopObserverXr
    {
        public static bool IsActive() => IsActive(OvrHmdPresent, XrDeviceActive);

        public static bool IsActive(Func<bool> hmdPresent, Func<bool> xrDeviceActive)
        {
            bool hmd = false;
            bool device = false;
            if (hmdPresent != null)
            {
                try { hmd = hmdPresent(); }
                catch (Exception) { hmd = false; }
            }
            if (xrDeviceActive != null)
            {
                try { device = xrDeviceActive(); }
                catch (Exception) { device = false; }
            }
            return hmd || device;
        }

        private static bool OvrHmdPresent() => OVRManager.isHmdPresent;

        private static bool XrDeviceActive()
        {
            Type settings = Type.GetType("UnityEngine.XR.XRSettings, UnityEngine.XRModule");
            if (settings == null) return false;
            var property = settings.GetProperty("isDeviceActive", BindingFlags.Public | BindingFlags.Static);
            return property != null && property.GetValue(null) is bool active && active;
        }
    }

    public enum ObserverIncomingKind
    {
        Content,
        Avatar,
        Anchor,
    }

    /// <summary>
    /// What an observer answers on <c>OnShare</c>. An avatar would become a receiver actor. An anchor would link
    /// furniture into a room the observer does not have. Other content is accepted so the mesh exists; the view
    /// then poses it from the selected participant's room.
    /// </summary>
    public static class ObserverSharePolicy
    {
        public static ObserverIncomingKind Classify(bool isAvatar, bool isAnchor)
        {
            if (isAvatar) return ObserverIncomingKind.Avatar;
            if (isAnchor) return ObserverIncomingKind.Anchor;
            return ObserverIncomingKind.Content;
        }

        public static OnShareResponse Response(ObserverIncomingKind kind) =>
            kind == ObserverIncomingKind.Content ? OnShareResponse.AddAsNew() : OnShareResponse.Reject();
    }
}
