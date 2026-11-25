// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    /// <summary>The one system that writes shared Chess and Whiteboard content in a run.</summary>
    public enum FreeformContentWriter
    {
        /// <summary>Fusion: ChessBoardHandlerMotif NetworkArray and WhiteboardManagerMotif RPC.</summary>
        FusionMotifs,
        /// <summary>Foundation Local backend binds the content; the Fusion motifs are suppressed.</summary>
        FoundationLocalBackend,
    }

    /// <summary>
    /// Pure mode to writer contract (T3.5). A run has exactly one content writer:
    /// Fusion mode uses the Fusion motifs and the local backend never binds;
    /// FoundationLocal mode suppresses the Fusion motifs and the backend binds.
    /// </summary>
    public static class FreeformContentWriterPolicy
    {
        public static FreeformContentWriter WriterFor(bool usesFoundationLocal) =>
            usesFoundationLocal ? FreeformContentWriter.FoundationLocalBackend
                                : FreeformContentWriter.FusionMotifs;

        public static bool AllowsLocalBackendToBind(bool usesFoundationLocal) =>
            WriterFor(usesFoundationLocal) == FreeformContentWriter.FoundationLocalBackend;

        public static bool AllowsFusionMotifs(bool usesFoundationLocal) =>
            WriterFor(usesFoundationLocal) == FreeformContentWriter.FusionMotifs;

        public static int ActiveWriterCount(bool fusionMotifsActive, bool localBackendBound) =>
            (fusionMotifsActive ? 1 : 0) + (localBackendBound ? 1 : 0);

        /// <summary>True when the observed state has exactly the writer the mode selects.</summary>
        public static bool HasExactlyTheModeWriter(bool usesFoundationLocal,
            bool fusionMotifsActive, bool localBackendBound)
        {
            if (ActiveWriterCount(fusionMotifsActive, localBackendBound) != 1)
                return false;
            return usesFoundationLocal ? localBackendBound : fusionMotifsActive;
        }
    }
}
