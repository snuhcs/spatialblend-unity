// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The DllImport names resolve to the shipped files (libspatialblend.*, libspatialblend_session.*) on this Editor's platform: Windows
    /// looks for &lt;name&gt;.dll with no "lib" prefix, the other platforms add it. tools/test_native_library_names.py checks every platform.
    /// </summary>
    public sealed class NativeLibraryNameTests
    {
        private static string Resolved(string name)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return name + ".dll";
                case RuntimePlatform.OSXEditor: return "lib" + name + ".dylib";
                default: return "lib" + name + ".so";
            }
        }

        private static string Expected(string osx, string windows, string other)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return windows;
                case RuntimePlatform.OSXEditor: return osx;
                default: return other;
            }
        }

        [Test]
        public void TheCoreNameResolvesToTheShippedFile()
        {
            Assert.That(Resolved(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY), Is.EqualTo(Expected(
                SpatialBlendLibraryConfig.CorePlugins.OSX_LIBRARY, SpatialBlendLibraryConfig.CorePlugins.WINDOWS_LIBRARY,
                SpatialBlendLibraryConfig.CorePlugins.ANDROID_LIBRARY)));
        }

        [Test]
        public void TheSessionNameResolvesToTheShippedFile()
        {
            Assert.That(Resolved(SessionLibrary.LIB_NAME), Is.EqualTo(Expected(
                SpatialBlendLibraryConfig.SessionPlugins.OSX_LIBRARY, SpatialBlendLibraryConfig.SessionPlugins.WINDOWS_LIBRARY,
                SpatialBlendLibraryConfig.SessionPlugins.ANDROID_LIBRARY)));
        }
    }
}
#endif
