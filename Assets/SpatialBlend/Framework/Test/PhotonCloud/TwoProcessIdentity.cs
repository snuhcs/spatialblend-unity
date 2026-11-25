// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Fusion;
using Fusion.Photon.Realtime;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Test-only seam for the two-process cloud harness: SPATIALBLEND_TP_USERID forces the Photon UserId of BOTH processes to the same
    /// value, the case of two headsets signed into one account (the product sets no AuthValues, so it never does this itself).
    /// Unset, the runner is started exactly as the product starts it (no AuthValues).
    /// </summary>
    internal static class TwoProcessIdentity
    {
        public const string Variable = "SPATIALBLEND_TP_USERID";

        public static AuthenticationValues AuthValues()
        {
            var id = Environment.GetEnvironmentVariable(Variable);
            return string.IsNullOrEmpty(id) ? null : new AuthenticationValues(id);
        }

        public static string Describe(NetworkRunner runner)
        {
            var forced = Environment.GetEnvironmentVariable(Variable);
            var actual = runner == null || runner.AuthenticationValues == null ? null : runner.AuthenticationValues.UserId;
            return $"forced={(string.IsNullOrEmpty(forced) ? "none" : forced)} actual={(string.IsNullOrEmpty(actual) ? "(none)" : actual)}";
        }
    }
}
