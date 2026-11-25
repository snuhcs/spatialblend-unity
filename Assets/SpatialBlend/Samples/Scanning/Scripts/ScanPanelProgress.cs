// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// What the scan panel has done since it opened: how many photos were captured and how many points are tapped on the
    /// current one. The lobby tutorial reads it ("capture, tap at least once, then Reconstruct 3D"); nothing in the scan flow depends on it.
    /// </summary>
    public sealed class ScanPanelProgress
    {
        public int CaptureCount { get; private set; }
        /// <summary>Points tapped on the current photo (a new capture or an undo changes it).</summary>
        public int TapCount { get; private set; }
        /// <summary>Every tap since the panel opened, undone ones included.</summary>
        public int TotalTaps { get; private set; }

        /// <summary>Raised after every change.</summary>
        public event Action Changed;
        /// <summary>Raised when a photo was captured; the argument is the new capture count.</summary>
        public event Action<int> Captured;
        /// <summary>Raised when a point was tapped; the argument is the points now on the photo.</summary>
        public event Action<int> Tapped;

        public void NoteCapture()
        {
            CaptureCount++;
            TapCount = 0;
            Captured?.Invoke(CaptureCount);
            Changed?.Invoke();
        }

        public void NoteTap(int pointsNow)
        {
            TotalTaps++;
            TapCount = Math.Max(0, pointsNow);
            Tapped?.Invoke(TapCount);
            Changed?.Invoke();
        }

        /// <summary>Points changed without a new tap (undo, or a new capture cleared them).</summary>
        public void SetTapCount(int pointsNow)
        {
            if (TapCount == pointsNow) return;
            TapCount = Math.Max(0, pointsNow);
            Changed?.Invoke();
        }

        public void Reset()
        {
            CaptureCount = 0;
            TapCount = 0;
            TotalTaps = 0;
            Changed?.Invoke();
        }
    }
}
