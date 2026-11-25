// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public static class Constants
    {
        // Timeouts
        public const int DEFAULT_WAIT_TIME_MS = 2000;
        public const int CONNECTION_TIMEOUT_MS = 5000;
        public const int EVENT_WAIT_TIMEOUT_MS = 2000;

        // Network
        public const int DEFAULT_PORT = 49152;
        public const string DEFAULT_HOST_IP = "127.0.0.1";

        // Geometry
        public const double DEFAULT_WALKABLE_GEOMETRY_RESOLUTION = 0.1;

        // Internal log verbosity
        public const LogSeverity INTERNAL_LOG_VERBOSITY = LogSeverity.Info;
    }
}