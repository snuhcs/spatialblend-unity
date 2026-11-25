// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>The settings-to-config mapping. It reads no file and opens no connection.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SessionManagerConfigTests
    {
        private SpatialBlendSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        [Test]
        public void ThereIsNoDefaultServer_ScanningIsOffUntilOneIsNamed()
        {
            ScanSessionConfig config = SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data");
            Assert.That(SpatialBlendSettings.DefaultSessionServerAddress, Is.Empty, "the public repository names no scan server");
            Assert.That(_settings.sessionServerAddress, Is.Empty);
            Assert.That(_settings.sessionUseTls, Is.True);
            Assert.That(_settings.sessionFallbackServerAddress, Is.Empty);
            Assert.That(config.IsEnabled, Is.False);

            _settings.sessionServerAddress = "scan.example.org:443";
            config = SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data");
            Assert.That(config.ServerAddress, Is.EqualTo("scan.example.org:443"));
            Assert.That(config.UseTls, Is.True);
            Assert.That(config.IsEnabled, Is.True);
            Assert.That(config.Validate(out ScanError error), Is.True, error.ToString());
        }

        [Test]
        public void AnEmptyAddressLeavesScanningOff()
        {
            _settings.sessionServerAddress = "";
            ScanSessionConfig config = SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data");
            Assert.That(config.IsEnabled, Is.False);
        }

        [Test]
        public void AddressesTimeoutsAndPathsComeFromSettingsAndThePlatform()
        {
            _settings.sessionServerAddress = "scan.example:7000";
            _settings.sessionUseTls = false;
            _settings.sessionFallbackServerAddress = "scan2.example:7001";
            _settings.scanSegmentTimeoutSeconds = 12f;
            _settings.scanReconstructTimeoutSeconds = 99f;
            _settings.useDeviceSpecificClientId = false;
            _settings.clientId = "freeform";

            ScanSessionConfig config = SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data");

            Assert.That(config.ServerAddress, Is.EqualTo("scan.example:7000"));
            Assert.That(config.UseTls, Is.False);
            Assert.That(config.FallbackServerAddress, Is.EqualTo("scan2.example:7001"));
            Assert.That(config.ClientId, Is.EqualTo("freeform"));
            Assert.That(config.SegmentTimeoutSeconds, Is.EqualTo(12.0));
            Assert.That(config.ReconstructTimeoutSeconds, Is.EqualTo(99.0));
            Assert.That(config.CacheDirectory, Is.EqualTo("cache"));
            Assert.That(config.OutputDirectory, Is.EqualTo("persist"));
            Assert.That(config.Validate(out ScanError error), Is.True, error.ToString());
        }

        [Test]
        public void ADeviceFileReplacesTheDefaultAddressAndCanSelectPlaintext()
        {
            var local = LocalSessionConfig.Parse(
                "{\"sessionServerAddress\":\"scan-other.example:50001\",\"tls\":false}", null);
            ScanSessionConfig overridden = SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data", local);
            Assert.That(overridden.ServerAddress, Is.EqualTo("scan-other.example:50001"));
            Assert.That(overridden.UseTls, Is.False);
            Assert.That(overridden.ServerAddress, Is.Not.EqualTo(_settings.sessionServerAddress));
        }

        [Test]
        public void TheEditorWritesUnderTheTestDataFolder()
        {
            ScanSessionConfig config = SessionManager.BuildConfig(_settings, "device", true, "cache", "persist", "data");
            Assert.That(config.OutputDirectory, Is.EqualTo(Path.Combine("data", _settings.editorTestDataPath)));
        }

        [Test]
        public void ADeviceClientIdGetsAStableSuffix()
        {
            _settings.useDeviceSpecificClientId = true;
            _settings.clientId = "freeform";
            string first = SessionManager.BuildConfig(_settings, "device-A", false, "c", "p", "d").ClientId;
            string again = SessionManager.BuildConfig(_settings, "device-A", false, "c", "p", "d").ClientId;
            string other = SessionManager.BuildConfig(_settings, "device-B", false, "c", "p", "d").ClientId;

            Assert.That(first, Does.StartWith("freeform-"));
            Assert.That(first.Length, Is.EqualTo("freeform-".Length + 8));
            Assert.That(again, Is.EqualTo(first));
            Assert.That(other, Is.Not.EqualTo(first));
        }
    }
}
#endif
