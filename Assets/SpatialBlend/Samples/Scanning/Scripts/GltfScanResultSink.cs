// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.Objects;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// The default IScanResultSink: load the reconstructed GLB into the scene and bind it to the box the user
    /// annotated (T5.2, parity rows S2 and AV12).
    ///
    /// v1 built a volume component from the box inside GltfLoader.ConfigureAffordance. v2 has none on the
    /// runtime path. The box arrives as ScanReconstructionResult.RegionHint, and the loaded object takes it as
    /// an SbSpatialAuthoring declaration: the Containment Region is the box footprint extent, the footprint is
    /// the box base, and the box top is a tagged surface. The declaration is written before the object is
    /// registered, and it is shared with the content, so the receiver writes identical authoring.
    /// </summary>
    public sealed class GltfScanResultSink : IScanResultSink
    {
        /// <summary>Loads the model and calls the callback on the loaded object, before it is registered.</summary>
        public delegate void ModelLoader(string modelPath, string objectName, Action<GameObject> configure);

        /// <summary>
        /// Loads the model, fits it to the box when there is one, and calls the callback on the loaded object
        /// before it is registered. The box fixes the object's metric scale (SpatialBlend section 6, footnote).
        /// </summary>
        public delegate void BoxedModelLoader(string modelPath, string objectName, ScanRegionHint? box,
            Action<GameObject> configure);

        private readonly BoxedModelLoader _loadModel;

        public GltfScanResultSink() : this((BoxedModelLoader)null)
        {
        }

        /// <param name="loadModel">The GLB loader. Null loads through <see cref="GltfLoader"/>.</param>
        public GltfScanResultSink(ModelLoader loadModel)
            : this(loadModel == null
                ? null
                : (BoxedModelLoader)((path, name, box, configure) => loadModel(path, name, configure)))
        {
        }

        public GltfScanResultSink(BoxedModelLoader loadModel)
        {
            _loadModel = loadModel ?? LoadWithGltfLoader;
        }

        public void OnScanReconstructed(ScanReconstructionResult result)
        {
            if (result == null) return;
            ScanRegionHint? box = result.HasRegionHint ? result.RegionHint : (ScanRegionHint?)null;
            _loadModel(result.ModelPath, result.ObjectName, box, loaded =>
            {
                Bind(result, loaded);
                // Only the scanner's own objects reach this sink: the user's scan, with its object menu.
                ScannedObjectOrigin.Tag(loaded);
            });
        }

        /// <summary>
        /// Binds the loaded object to the annotated box of its scan. A scan without a box declares nothing,
        /// and the object keeps the authoring of its content prefab.
        /// </summary>
        public static SbSpatialAuthoring Bind(ScanReconstructionResult result, GameObject loaded)
        {
            if (result == null || loaded == null || !result.HasRegionHint) return null;
            SbSpatialAuthoring authoring = BoxAnnotationAuthoring.Declare(loaded, result.RegionHint);
            if (authoring == null)
            {
                Debug.LogWarning($"[Scan] {result.ObjectName}: the annotated box is degenerate; the object keeps its default authoring.");
            }
            return authoring;
        }

        public void OnScanFailed(ScanFailure failure)
        {
            Debug.LogError($"[Scan] {failure.Kind} request {failure.EventHandle} failed: {failure.Error}");
        }

        private static void LoadWithGltfLoader(string modelPath, string objectName, ScanRegionHint? box,
            Action<GameObject> configure)
        {
            GltfLoader loader = GltfLoader.EnsureInstance();
            if (loader == null)
            {
                Debug.LogError("[Scan] No GltfLoader to load the reconstruction.");
                return;
            }
            loader.LoadModelFromPath(modelPath, objectName, configure, box);
        }
    }
}
