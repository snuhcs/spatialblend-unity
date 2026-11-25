// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Threading.Tasks;
using GLTFast;
using System;
namespace SpatialBlend.Samples.Objects
{
    public class GltfNativeLoader : NativeLoader
    {
        private GltfLoader _loader;

        /// <summary>
        /// A received copy of a peer's scan is made from the scan template (ObjectPreset: collider, ray interactable, placement motif), as
        /// in v1, so the receiver can grab it and place it. The Framework's loader, the one <see cref="SbNativeEntity"/> uses,
        /// takes it through its template hook.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void RegisterReceivedCopyTemplate() =>
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = ReceivedCopyTemplate;

        internal static GameObject ReceivedCopyTemplate(string objectName)
        {
            GltfLoader loader = GltfLoader.EnsureInstance();
            return loader != null ? loader.CreateObjectTemplate(objectName) : null;
        }
        public override GameObject CreateTemplate(string objectName)
        {
            _loader = GltfLoader.EnsureInstance();
            return _loader.CreateObjectTemplate(objectName);
        }

        public override async Task LoadContentAsync(GameObject target, byte[] data)
        {
            if (target == null || data == null || data.Length == 0) {
                return;
            }
            if (_loader == null) _loader = GltfLoader.EnsureInstance();
            bool success = await _loader.LoadModelFromBytes(target, data);
            if (!success) Debug.LogError($"[GltfNativeLoader] Failed to load content into {target.name}");
        }
    }
}