// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Fusion.Editor;
using Fusion.Photon.Realtime;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Writes the Photon Fusion App ID into PhotonAppSettings.asset, which lives
    /// under the gitignored SDK, so the ID never enters the repository.
    ///
    /// SPATIALBLEND_PHOTON_APP_ID=... Unity -batchmode -quit -projectPath P
    ///   -executeMethod SpatialBlend.Editor.PhotonAppIdSetup.ApplyFromEnvironment
    /// </summary>
    public static class PhotonAppIdSetup
    {
        public const string EnvironmentVariable = "SPATIALBLEND_PHOTON_APP_ID";

        public static void ApplyFromEnvironment()
        {
            var appId = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
            if (!Guid.TryParse(appId, out _))
            {
                Debug.LogError($"[PhotonAppIdSetup] {EnvironmentVariable} is missing or not a GUID.");
                if (Application.isBatchMode) EditorApplication.Exit(2);
                return;
            }

            FusionGlobalScriptableObjectUtils.EnsureAssetExists<PhotonAppSettings>();
            var settings = PhotonAppSettings.Global;
            settings.AppSettings.AppIdFusion = appId;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PhotonAppIdSetup] AppIdFusion set in {AssetDatabase.GetAssetPath(settings)}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
