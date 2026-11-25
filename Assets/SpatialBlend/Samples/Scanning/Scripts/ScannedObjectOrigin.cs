// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// Marks a scene object as one this user scanned. Its object menu is the scanned-object menu (Delete). A replica built from a
    /// peer's content never has it: a received copy has its own menu (<see cref="ReceivedCopy"/>).
    /// </summary>
    public sealed class ScannedObjectOrigin : MonoBehaviour
    {
        /// <summary>
        /// Marks a freshly scanned object as the user's own and gives it the object menu (the A button while hovering, in
        /// SceneEdit). Only the scanner's objects reach the scan result sink, so a received copy is never tagged.
        /// </summary>
        public static ScannedObjectOrigin Tag(GameObject root)
        {
            if (root == null) return null;
            if (!root.TryGetComponent(out ScannedObjectOrigin origin)) origin = root.AddComponent<ScannedObjectOrigin>();
            ManipulationPanelMotif.Setup(root);
            return origin;
        }
    }
}
