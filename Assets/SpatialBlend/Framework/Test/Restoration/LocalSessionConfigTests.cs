// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The optional on-device spatialblend-local.json. Placeholder hosts only. Every warning is checked
    /// for the sentinel host so a value can never leak into a log.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class LocalSessionConfigTests
    {
        private const string Sentinel = "sentinel-host.example";
        private SpatialBlendSettings _settings;
        private string _dir;
        private List<string> _warnings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _dir = Path.Combine(Path.GetTempPath(), "spatialblend-local-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _warnings = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_settings);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private void Warn(string message) => _warnings.Add(message);

        private LocalSessionConfig LoadFile(string json)
        {
            File.WriteAllText(Path.Combine(_dir, LocalSessionConfig.FileName), json);
            return LocalSessionConfig.Load(_dir, Warn);
        }

        private ScanSessionConfig Build(LocalSessionConfig local) =>
            SessionManager.BuildConfig(_settings, "device", false, "cache", "persist", "data", local);

        private void AssertNoLeak()
        {
            foreach (string w in _warnings) Assert.That(w, Does.Not.Contain(Sentinel).And.Not.Contain("50000"), w);
        }

        [Test]
        public void AbsentFileChangesNothingAndIsSilent()
        {
            LocalSessionConfig local = LocalSessionConfig.Load(_dir, Warn);
            Assert.That(local.HasOverrides, Is.False);
            Assert.That(_warnings, Is.Empty);
            ScanSessionConfig config = Build(local);
            Assert.That(config.ServerAddress, Is.EqualTo(SpatialBlendSettings.DefaultSessionServerAddress));
            Assert.That(config.UseTls, Is.True);
            // The public repository names no scan server: with no device file, scanning stays off.
            Assert.That(config.IsEnabled, Is.False);
        }

        [Test]
        public void MissingDirectoryIsSilent()
        {
            Assert.That(LocalSessionConfig.Load(Path.Combine(_dir, "nope"), Warn).HasOverrides, Is.False);
            Assert.That(LocalSessionConfig.Load(null, Warn).HasOverrides, Is.False);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void ValidFileSuppliesBothAddresses()
        {
            LocalSessionConfig local = LoadFile(
                "{ \"sessionServerAddress\": \"scan-host.example:50000\", \"sessionFallbackServerAddress\": \"scan-two.example:50001\" }");
            ScanSessionConfig config = Build(local);

            Assert.That(config.ServerAddress, Is.EqualTo("scan-host.example:50000"));
            Assert.That(config.FallbackServerAddress, Is.EqualTo("scan-two.example:50001"));
            Assert.That(config.IsEnabled, Is.True);
            Assert.That(config.Validate(out ScanError error), Is.True, error.ToString());
            Assert.That(_warnings, Is.Empty);
            Assert.That(local.OverriddenFields, Is.EqualTo(new[]
                { LocalSessionConfig.ServerAddressKey, LocalSessionConfig.FallbackServerAddressKey }));
        }

        [Test]
        public void TlsTrueIsParsedAndAppliedToTheConfig()
        {
            LocalSessionConfig local = LoadFile(
                "{ \"sessionServerAddress\": \"scan-host.example:443\", \"tls\": true }");
            Assert.That(local.UseTls, Is.EqualTo(true));
            Assert.That(Build(local).UseTls, Is.True);
            Assert.That(local.OverriddenFields, Does.Contain(LocalSessionConfig.TlsKey));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void TlsFalseIsParsedAndStaysPlaintext()
        {
            LocalSessionConfig local = LoadFile("{ \"tls\": false }");
            Assert.That(local.UseTls, Is.EqualTo(false));
            Assert.That(Build(local).UseTls, Is.False);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void APrimaryAddressWithoutATlsKeyStaysPlaintext()
        {
            // Device files written before the public default named a server and omitted tls. That meant plaintext.
            LocalSessionConfig local = LoadFile("{ \"sessionServerAddress\": \"scan-host.example:50000\" }");
            Assert.That(local.UseTls, Is.Null);
            Assert.That(local.OverriddenFields, Does.Not.Contain(LocalSessionConfig.TlsKey));
            ScanSessionConfig named = Build(local);
            Assert.That(named.ServerAddress, Is.EqualTo("scan-host.example:50000"));
            Assert.That(named.UseTls, Is.False);

            Assert.That(Build(LocalSessionConfig.None).ServerAddress, Is.EqualTo(SpatialBlendSettings.DefaultSessionServerAddress));
            Assert.That(Build(LocalSessionConfig.None).UseTls, Is.True, "no address in the file keeps the settings default");
            LocalSessionConfig fallbackOnly = LoadFile("{ \"sessionFallbackServerAddress\": \"scan-two.example:50001\" }");
            ScanSessionConfig fallback = Build(fallbackOnly);
            Assert.That(fallback.ServerAddress, Is.EqualTo(SpatialBlendSettings.DefaultSessionServerAddress));
            Assert.That(fallback.UseTls, Is.True);
            Assert.That(fallback.FallbackServerAddress, Is.EqualTo("scan-two.example:50001"));
        }

        [TestCase("\"true\"")]
        [TestCase("1")]
        [TestCase("0")]
        [TestCase("\"sentinel-host.example:443\"")]
        public void NonBooleanTlsWarnsByNameAndIsIgnored(string value)
        {
            LocalSessionConfig local = LoadFile(
                "{ \"sessionServerAddress\": \"scan-host.example:443\", \"tls\": " + value + " }");
            Assert.That(local.UseTls, Is.Null);
            Assert.That(Build(local).UseTls, Is.False, "an ignored tls key plus a named server stays plaintext");
            Assert.That(local.ServerAddress, Is.EqualTo("scan-host.example:443")); // other fields survive
            Assert.That(_warnings.Count, Is.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain(LocalSessionConfig.TlsKey));
            Assert.That(_warnings[0], Does.Not.Contain("scan-host").And.Not.Contain(Sentinel).And.Not.Contain("443"));
        }

        [Test]
        public void ALocalTlsOverridePlumbsThroughBuildConfigWithoutTouchingOtherFields()
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            _settings.sessionUseTls = false;
            ScanSessionConfig config = Build(LocalSessionConfig.Parse("{\"tls\":true}", Warn));
            Assert.That(config.UseTls, Is.True);
            Assert.That(config.ServerAddress, Is.EqualTo("settings-one.example:7000"));
        }

        [Test]
        public void PartialFileOverridesOnlyWhatItSets()
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            _settings.sessionFallbackServerAddress = "settings-two.example:7001";
            LocalSessionConfig local = LoadFile("{\"sessionFallbackServerAddress\":\"scan-two.example:50001\"}");
            ScanSessionConfig config = Build(local);

            Assert.That(config.ServerAddress, Is.EqualTo("settings-one.example:7000"));
            Assert.That(config.FallbackServerAddress, Is.EqualTo("scan-two.example:50001"));
            Assert.That(config.SegmentTimeoutSeconds, Is.EqualTo((double)_settings.scanSegmentTimeoutSeconds));
        }

        [Test]
        public void FileOverridesNonEmptySettings()
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            ScanSessionConfig config = Build(LoadFile("{\"sessionServerAddress\":\"scan-host.example:50000\"}"));
            Assert.That(config.ServerAddress, Is.EqualTo("scan-host.example:50000"));
        }

        [Test]
        public void EmptyValuesKeepTheSettingsValue()
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            _settings.sessionFallbackServerAddress = "settings-two.example:7001";
            LocalSessionConfig local = LoadFile(
                "{\"sessionServerAddress\":\"\",\"sessionFallbackServerAddress\":\"  \",\"scanSegmentTimeoutSeconds\":null}");
            ScanSessionConfig config = Build(local);

            Assert.That(config.ServerAddress, Is.EqualTo("settings-one.example:7000"));
            Assert.That(config.FallbackServerAddress, Is.EqualTo("settings-two.example:7001"));
            Assert.That(local.HasOverrides, Is.False);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void TimeoutsOverrideSettings()
        {
            _settings.scanSegmentTimeoutSeconds = 12f;
            _settings.scanReconstructTimeoutSeconds = 99f;
            ScanSessionConfig config = Build(LoadFile(
                "{\"scanSegmentTimeoutSeconds\": 45, \"scanReconstructTimeoutSeconds\": 600.5}"));

            Assert.That(config.SegmentTimeoutSeconds, Is.EqualTo(45.0));
            Assert.That(config.ReconstructTimeoutSeconds, Is.EqualTo(600.5));
        }

        [Test]
        public void NonPositiveOrWrongTypeTimeoutsAreIgnoredWithAWarningNamingTheField()
        {
            _settings.scanSegmentTimeoutSeconds = 12f;
            _settings.scanReconstructTimeoutSeconds = 99f;
            ScanSessionConfig config = Build(LoadFile(
                "{\"scanSegmentTimeoutSeconds\": 0, \"scanReconstructTimeoutSeconds\": \"soon\"}"));

            Assert.That(config.SegmentTimeoutSeconds, Is.EqualTo(12.0));
            Assert.That(config.ReconstructTimeoutSeconds, Is.EqualTo(99.0));
            Assert.That(_warnings.Count, Is.EqualTo(2));
            Assert.That(_warnings[0], Does.Contain(LocalSessionConfig.SegmentTimeoutKey));
            Assert.That(_warnings[1], Does.Contain(LocalSessionConfig.ReconstructTimeoutKey));
            Assert.That(Build(LoadFile("{\"scanSegmentTimeoutSeconds\": -3}")).SegmentTimeoutSeconds, Is.EqualTo(12.0));
        }

        [TestCase("{ not json")]
        [TestCase("")]
        [TestCase("[]")]
        [TestCase("{\"sessionServerAddress\":\"sentinel-host.example:50000\"")]
        [TestCase("{\"sessionServerAddress\":\"sentinel-host.example:50000\",}")]
        [TestCase("{\"sessionServerAddress\":{\"a\":\"sentinel-host.example:50000\"}}")]
        [TestCase("{\"sessionServerAddress\":\"sentinel-host.example:50000\"} trailing")]
        public void MalformedJsonIsIgnoredWithoutLeakingContent(string json)
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            LocalSessionConfig local = LoadFile(json);

            Assert.That(local.HasOverrides, Is.False);
            Assert.That(Build(local).ServerAddress, Is.EqualTo("settings-one.example:7000"));
            Assert.That(_warnings.Count, Is.EqualTo(1));
            AssertNoLeak();
        }

        [TestCase("sentinel-host.example")]
        [TestCase("sentinel-host.example:")]
        [TestCase("sentinel-host.example:99999")]
        [TestCase("sentinel-host.example:0")]
        [TestCase("sentinel-host.example:abc")]
        [TestCase("sentinel host.example:50000")]
        public void InvalidAddressIsIgnoredAndNeverLogged(string address)
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            LocalSessionConfig local = LoadFile("{\"sessionServerAddress\":\"" + address + "\"}");

            Assert.That(Build(local).ServerAddress, Is.EqualTo("settings-one.example:7000"));
            Assert.That(_warnings.Count, Is.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain(LocalSessionConfig.ServerAddressKey));
            AssertNoLeak();
        }

        [TestCase("http://sentinel-host.example:50000")]
        [TestCase("grpc://sentinel-host.example:50000")]
        public void SchemeIsRejected(string address)
        {
            LocalSessionConfig local = LoadFile("{\"sessionFallbackServerAddress\":\"" + address + "\"}");
            Assert.That(local.FallbackServerAddress, Is.Null);
            Assert.That(_warnings.Count, Is.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain(LocalSessionConfig.FallbackServerAddressKey));
            AssertNoLeak();
        }

        [Test]
        public void OneBadFieldDoesNotDiscardTheGoodOnes()
        {
            LocalSessionConfig local = LoadFile(
                "{\"sessionServerAddress\":\"http://sentinel-host.example:50000\",\"sessionFallbackServerAddress\":\"scan-two.example:50001\",\"scanReconstructTimeoutSeconds\":120}");

            Assert.That(local.ServerAddress, Is.Null);
            Assert.That(local.FallbackServerAddress, Is.EqualTo("scan-two.example:50001"));
            Assert.That(local.ReconstructTimeoutSeconds, Is.EqualTo(120.0));
            AssertNoLeak();
        }

        [Test]
        public void UnknownKeysAndAByteOrderMarkAreTolerated()
        {
            LocalSessionConfig local = LoadFile(
                "\uFEFF{\"note\":\"hi\",\"flag\":true,\"sessionServerAddress\":\"scan-host.example:50000\"}");
            Assert.That(local.ServerAddress, Is.EqualTo("scan-host.example:50000"));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void ParseToleratesALeadingByteOrderMark()
        {
            LocalSessionConfig local = LocalSessionConfig.Parse(
                "\uFEFF{\"sessionServerAddress\":\"scan-host.example:50000\"}", Warn);
            Assert.That(local.ServerAddress, Is.EqualTo("scan-host.example:50000"));
            Assert.That(_warnings, Is.Empty);
        }

        [TestCase("{\"sessionServerAddress\":\"scan\\/host.example:50000\"}", false)] // \/ decodes to /, invalid host char
        [TestCase("{\"sessionServerAddress\":\"scan-host.example:50000\\\\\"}", false)] // trailing backslash: invalid port
        [TestCase("{\"sessionServerAddress\":\"\\u0073can-host.example:50000\"}", true)]  // \u0073 = 's'
        [TestCase("{\"sessionServerAddress\":\"\\u73\"}", false)]                       // short \u: malformed
        [TestCase("{\"sessionServerAddress\":\"\\u00zz\"}", false)]                     // non-hex \u: malformed
        [TestCase("{\"sessionServerAddress\":\"bad\\x\"}", false)]                      // bad escape: malformed
        [TestCase("{\"sessionServerAddress\":\"bad\u0001\"}", false)]                    // raw control char: malformed
        public void StringEscapesAreStrict(string json, bool expectsAddress)
        {
            LocalSessionConfig local = LocalSessionConfig.Parse(json, Warn);
            Assert.That(local.ServerAddress != null, Is.EqualTo(expectsAddress));
            if (!expectsAddress) Assert.That(_warnings.Count, Is.EqualTo(1));
            AssertNoLeak();
        }

        [TestCase("1e2", 100.0)]
        [TestCase("-1", null)]      // valid number, non-positive: field ignored
        [TestCase("0.5", 0.5)]
        [TestCase("1.", null)]      // not JSON: whole file ignored
        [TestCase("01", null)]
        [TestCase("-", null)]
        [TestCase("1e", null)]
        [TestCase("1e999", null)]   // overflow: field-level warning, other fields survive
        public void NumbersFollowJsonSyntax(string literal, double? expected)
        {
            LocalSessionConfig local = LocalSessionConfig.Parse(
                "{\"scanSegmentTimeoutSeconds\":" + literal + ",\"sessionServerAddress\":\"scan-host.example:50000\"}", Warn);
            Assert.That(local.SegmentTimeoutSeconds, Is.EqualTo(expected));
            if (expected == null) Assert.That(_warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void OverflowingNumberOnlyDropsThatField()
        {
            LocalSessionConfig local = LocalSessionConfig.Parse(
                "{\"scanSegmentTimeoutSeconds\":1e999,\"sessionServerAddress\":\"scan-host.example:50000\"}", Warn);
            Assert.That(local.ServerAddress, Is.EqualTo("scan-host.example:50000"));
            Assert.That(_warnings[0], Does.Contain(LocalSessionConfig.SegmentTimeoutKey));
        }

        [Test]
        public void OversizedFileIsIgnoredWithAWarning()
        {
            string padding = new string('x', LocalSessionConfig.MaxFileBytes);
            LocalSessionConfig local = LoadFile(
                "{\"note\":\"" + padding + "\",\"sessionServerAddress\":\"scan-host.example:50000\"}");
            Assert.That(local.HasOverrides, Is.False);
            Assert.That(_warnings.Count, Is.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain("too large"));
        }

        [Test]
        public void BuildConfigWithoutAnOverrideBehavesAsBefore()
        {
            _settings.sessionServerAddress = "settings-one.example:7000";
            Assert.That(SessionManager.BuildConfig(_settings, "device", false, "c", "p", "d").ServerAddress,
                Is.EqualTo("settings-one.example:7000"));
            Assert.That(Build(LocalSessionConfig.None).ServerAddress, Is.EqualTo("settings-one.example:7000"));
        }
    }
}
#endif
