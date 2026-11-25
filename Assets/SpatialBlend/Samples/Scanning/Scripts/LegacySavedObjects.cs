// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// The Save (Keep in room) feature was removed. Its library, &lt;persistentDataPath&gt;/SavedObjects/
    /// (a folder per room holding GLBs and records), is never read any more; this deletes it once, at startup, so a device that
    /// used Save does not keep the files. Best effort: it logs once and never throws. Once the folder is gone it does nothing.
    /// </summary>
    public static class LegacySavedObjects
    {
        /// <summary>The folder the removed library wrote (SavedObjectLibrary.RootFolderName).</summary>
        public const string FolderName = "SavedObjects";

        /// <summary>Deletes <paramref name="persistentDataPath"/>/SavedObjects when it exists. True when it was there and is gone.</summary>
        public static bool DeleteIfPresent(string persistentDataPath)
        {
            try
            {
                if (string.IsNullOrEmpty(persistentDataPath)) return false;
                string folder = Path.Combine(persistentDataPath, FolderName);
                if (!Directory.Exists(folder)) return false;
                Directory.Delete(folder, true);
                Debug.Log("[Scan] Removed the saved-object library left from the removed Save feature.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Scan] Could not remove the old saved-object library: {e.GetType().Name}.");
                return false;
            }
        }
    }
}
