// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Ensures the XR Management settings asset exists, that Android (Quest)
    /// initializes the OpenXR loader on start, and that Android enables the
    /// OpenXR features its build validation requires: Touch / Touch Plus
    /// controller and hand interaction profiles, and composition layers (the
    /// Composition Layers package is installed). Other targets are left as
    /// they are. Idempotent.
    ///
    /// Unity -batchmode -quit -projectPath P
    ///   -executeMethod SpatialBlend.Editor.XRLoaderSetup.EnsureAndroidOpenXR
    /// </summary>
    public static class XRLoaderSetup
    {
        public const string SettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        private const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";

        private static readonly string[] AndroidFeatures =
        {
            "OculusTouchControllerProfile",
            "MetaQuestTouchPlusControllerProfile",
            "HandInteractionProfile",
            "OpenXRCompositionLayersFeature",
        };

        public static void EnsureAndroidOpenXR()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey,
                    out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(SettingsPath);
                if (perTarget == null)
                {
                    perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    AssetDatabase.CreateAsset(perTarget, SettingsPath);
                }
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }

            var group = BuildTargetGroup.Android;
            if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            var general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;

            var assigned = XRPackageMetadataStore.AssignLoader(general.AssignedSettings, OpenXRLoader, group);
            EditorUtility.SetDirty(general.AssignedSettings);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(perTarget);
            var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            var enabled = new System.Collections.Generic.List<string>();
            foreach (var feature in openXR.GetFeatures())
            {
                if (System.Array.IndexOf(AndroidFeatures, feature.GetType().Name) < 0) continue;
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                enabled.Add(feature.GetType().Name);
            }
            EditorUtility.SetDirty(openXR);
            AssetDatabase.SaveAssets();
            Debug.Log($"[XRLoaderSetup] android OpenXR features enabled=[{string.Join(",", enabled)}]");

            var loaders = string.Join(",", general.AssignedSettings.activeLoaders);
            var ok = general.AssignedSettings.activeLoaders.Count > 0 && enabled.Count == AndroidFeatures.Length;
            Debug.Log($"[XRLoaderSetup] assigned={assigned} android loaders=[{loaders}] asset={AssetDatabase.GetAssetPath(perTarget)}");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
