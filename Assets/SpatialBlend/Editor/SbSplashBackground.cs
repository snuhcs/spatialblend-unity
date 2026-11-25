// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.IO;
using System.Xml;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Keeps the Quest OS splash background on contextual passthrough.
    /// OVRManifestPreprocessor rewrites <c>com.oculus.ossplash.background</c> in the generated manifest from
    /// OculusProjectConfig (<c>systemLoadingScreenBackground</c>), and that asset is gitignored, so a clean checkout
    /// builds "black". This runs after OVRGradleGeneration (order 99999) and sets the value back (parity row E4).
    /// </summary>
    public static class SbSplashBackground
    {
        public const string MetaDataName = "com.oculus.ossplash.background";
        public const string Value = "passthrough-contextual";
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        /// <summary>Sets the background on a manifest. Returns true when the document changed.</summary>
        public static bool Apply(XmlDocument manifest)
        {
            XmlNode application = manifest.SelectSingleNode("/manifest/application");
            if (application == null)
            {
                UnityEngine.Debug.LogWarning("[SbSplashBackground] The manifest has no <application> element; the OS splash background was not set.");
                return false;
            }
            foreach (XmlNode node in application.SelectNodes("meta-data"))
            {
                var element = (XmlElement)node;
                if (element.GetAttribute("name", AndroidNamespace) != MetaDataName) continue;
                if (element.GetAttribute("value", AndroidNamespace) == Value) return false;
                element.SetAttribute("value", AndroidNamespace, Value);
                return true;
            }
            XmlElement added = manifest.CreateElement("meta-data");
            added.SetAttribute("name", AndroidNamespace, MetaDataName);
            added.SetAttribute("value", AndroidNamespace, Value);
            application.AppendChild(added);
            return true;
        }

        public static bool ApplyToFile(string manifestPath)
        {
            var document = new XmlDocument();
            document.Load(manifestPath);
            if (!Apply(document)) return false;
            document.Save(manifestPath);
            return true;
        }
    }

#if UNITY_ANDROID
    /// <summary>
    /// Runs after OVRGradleGeneration, whose callbackOrder is 99999 (or 3 with PRIORITIZE_OCULUS_XR_SETTINGS). It also
    /// packages the splash as assets/vr_splash.png, the file the Quest OS splash reads. The OpenXR MetaQuestFeature does
    /// that from its systemSplashScreen, but that feature is disabled in this project (the Oculus loader is used), and
    /// OVR does it from the gitignored OculusProjectConfig. Neither fires on a clean checkout.
    /// </summary>
    public sealed class SbSplashBackgroundPostprocessor : IPostGenerateGradleAndroidProject
    {
        /// <summary>Project-relative splash path. It is resolved from Application.dataPath, never the working directory.</summary>
        public const string SplashSource = "Assets/Resources/FreeformCollaboration_Splash.png";

        public int callbackOrder => 200000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (File.Exists(manifest)) SbSplashBackground.ApplyToFile(manifest);

            string source = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", SplashSource));
            if (!File.Exists(source))
            {
                UnityEngine.Debug.LogWarning($"[SbSplashBackground] Splash image not found at {source}; assets/vr_splash.png was not written.");
                return;
            }
            string assets = Path.Combine(path, "src", "main", "assets");
            Directory.CreateDirectory(assets);
            string target = Path.Combine(assets, "vr_splash.png");
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(source, target, true);
        }
    }
#endif
}
