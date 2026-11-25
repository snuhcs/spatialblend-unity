// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace SpatialBlend
{
    /// <summary>
    /// One-shot trace lines that tools read from the log, not the user: a simulation harness's analysis (<c>[FCShare]</c>,
    /// <c>[RoomPlacement]</c>, <c>[V2Share]</c>), the room matrix and the two-process cloud tests. Compiled only into the Editor and
    /// into a player built with the scripting define <c>SPATIALBLEND_SIM_HARNESS</c>: in any other player the call, and the building
    /// of its string, does not exist.
    /// </summary>
    public static class SbTrace
    {
        [Conditional("UNITY_EDITOR"), Conditional("SPATIALBLEND_SIM_HARNESS")]
        public static void Line(string line) => UnityEngine.Debug.Log(line);
    }
}
