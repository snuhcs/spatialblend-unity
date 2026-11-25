// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Scene host for the SAM3 and SAM3D scan service. It replaces v1 SessionManager and the three
    /// session handlers. The server address comes from SpatialBlendSettings. All request logic,
    /// timeouts and the pending state machine live in ScanService, which is tested without Unity.
    /// </summary>
    public sealed class SessionManager : MonoBehaviour
    {
        public static SessionManager Instance { get; private set; }

        private ScanService _service;
        private IScanResultSink _resultSink;

        /// <summary>Null until Initialize connected at least one pipeline.</summary>
        public ScanService Service => _service;

        public bool IsInitialized => _service != null && _service.ActiveWorkflow != ScanWorkflow.None;

        /// <summary>The last initialization failure, for the UI. Empty when it worked.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// Receives finished reconstructions. The app sets it (FreeformCollaborationApp binds the GLB
        /// loader). A result that arrives while it is null is logged and dropped. The default sink,
        /// GltfScanResultSink, binds the scan's RegionHint to the loaded object's SbSpatialAuthoring
        /// (T5.2), so the annotated box becomes an explicit region.
        /// </summary>
        public IScanResultSink ResultSink
        {
            get => _resultSink;
            set => _resultSink = value;
        }

        /// <summary>
        /// Returns the scene's SessionManager, creating one when the scene has none. The v1 scene
        /// carried the object. v2 creates it on demand so the scene needs no edit.
        /// </summary>
        public static SessionManager EnsureInstance()
        {
            if (Instance != null) return Instance;
            return new GameObject("SessionManager").AddComponent<SessionManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        /// <summary>
        /// Connects the scan pipelines. The address and TLS flag come from SpatialBlendSettings
        /// (the default is the public lab endpoint over TLS). spatialblend-local.json overrides either.
        /// With no address it does nothing and sends nothing. Call it once, after SpatialBlendManager has its settings.
        /// </summary>
        public bool Initialize()
        {
            if (_service != null) return IsInitialized;

            SpatialBlendSettings settings = SpatialBlendManager.CurrentSettings;
            if (settings == null)
            {
                LastError = "SpatialBlendManager.CurrentSettings is not ready.";
                Debug.LogWarning($"[SessionManager] {LastError}");
                return false;
            }

            LocalSessionConfig local = LocalSessionConfig.Load(Application.persistentDataPath,
                message => Debug.LogWarning($"[SessionManager] {message}"));
            if (local.HasOverrides)
                Debug.Log($"[SessionManager] {LocalSessionConfig.FileName} overrides: {string.Join(", ", local.OverriddenFields)}");

            ScanSessionConfig config = BuildConfig(settings, SystemInfo.deviceUniqueIdentifier, Application.isEditor,
                Application.temporaryCachePath, Application.persistentDataPath, Application.dataPath, local);
            if (!config.IsEnabled)
            {
                LastError = string.IsNullOrWhiteSpace(config.FallbackServerAddress)
                    ? "No session server address in SpatialBlendSettings or the local config. Scanning is off."
                    : "Only a fallback scan server is set; scanning needs a primary server address. Scanning is off.";
                Debug.Log($"[SessionManager] {LastError}");
                return false;
            }
            Debug.Log(config.UseTls
                ? "[SessionManager] Connecting to the scan server with TLS."
                : "[SessionManager] Connecting to the scan server in plaintext.");

            return InitializeWith(new NativeScanSessionTransport(settings.connectionTimeoutMs), DiskFileSystem, config);
        }

        /// <summary>The real file system, for a caller that supplies its own transport to <see cref="InitializeWith"/>.</summary>
        public static IScanFileSystem DiskFileSystem => new SystemScanFileSystem();

        /// <summary>
        /// The injection seam: starts the scan service over the given transport instead of the native library. The lobby
        /// tutorial passes a canned transport so a scan can be shown without a server. Like <see cref="Initialize"/> it does
        /// nothing when a service already runs; <see cref="ResetService"/> drops the running one first.
        /// </summary>
        public bool InitializeWith(IScanSessionTransport transport, IScanFileSystem files, ScanSessionConfig config)
        {
            if (_service != null) return IsInitialized;
            if (transport == null || files == null || config == null)
            {
                LastError = "InitializeWith needs a transport, a file system and a config.";
                return false;
            }

            var service = new ScanService(transport, files, config, new ForwardingSink(this));
            service.Diagnostic += message => Debug.Log($"[SessionManager] {message}");
            service.Submitted += OnSubmitted;
            if (!service.Initialize(out ScanError error))
            {
                LastError = error.ToString();
                Debug.LogWarning($"[SessionManager] Scan session did not start: {LastError}");
                service.Dispose();
                return false;
            }

            _service = service;
            LastError = string.Empty;
            Debug.Log($"[SessionManager] Scan workflow: {service.ActiveWorkflow}");
            return true;
        }

        /// <summary>Disposes the running scan service, so a later <see cref="Initialize"/> or <see cref="InitializeWith"/> starts a fresh one.</summary>
        public void ResetService()
        {
            if (_service == null) return;
            _service.Submitted -= OnSubmitted;
            _service.Dispose();
            _service = null;
        }

        /// <summary>
        /// Maps settings and platform paths to the pure config. No I/O. A non-null localOverride
        /// (from spatialblend-local.json) replaces the settings fields it validly sets, including the
        /// address and <see cref="ScanSessionConfig.UseTls"/>.
        /// </summary>
        public static ScanSessionConfig BuildConfig(SpatialBlendSettings settings, string deviceId, bool isEditor,
                                                      string cacheDirectory, string persistentDirectory,
                                                      string dataDirectory,
                                                      LocalSessionConfig localOverride = null)
        {
            string outputDirectory = isEditor
                ? Path.Combine(dataDirectory, settings.editorTestDataPath ?? string.Empty)
                : persistentDirectory;

            var config = new ScanSessionConfig
            {
                ServerAddress = settings.sessionServerAddress ?? string.Empty,
                FallbackServerAddress = settings.sessionFallbackServerAddress ?? string.Empty,
                UseTls = settings.sessionUseTls,
                ClientId = ScanSessionConfig.ResolveClientId(settings.clientId, settings.useDeviceSpecificClientId,
                    deviceId, id => Hash128.Compute(id).ToString()),
                CacheDirectory = cacheDirectory,
                OutputDirectory = outputDirectory,
                SegmentTimeoutSeconds = settings.scanSegmentTimeoutSeconds,
                ReconstructTimeoutSeconds = settings.scanReconstructTimeoutSeconds,
            };
            localOverride?.ApplyTo(config);
            return config;
        }

        private void Update()
        {
            _service?.Update(Time.realtimeSinceStartupAsDouble);
        }

        private void OnDestroy()
        {
            if (_service != null)
            {
                _service.Submitted -= OnSubmitted;
                _service.Dispose();
                _service = null;
            }
            if (Instance == this) Instance = null;
        }

        // v1 copied the newest camera*.json next to the output so the server side tooling could pair
        // the reconstruction with the pose it was captured at. The Editor skips it, as in v1.
        private static void OnSubmitted(ScanSubmission submission)
        {
#if !UNITY_EDITOR
            if (submission.Kind == ScanKind.Segment) return;
            CopyCameraInfo(submission.OutputPath);
#endif
        }

        private static void CopyCameraInfo(string outputPrefix)
        {
            if (string.IsNullOrWhiteSpace(outputPrefix)) return;
            try
            {
                string source = LatestFile(Application.persistentDataPath, "camera*.json");
                string directory = Path.GetDirectoryName(outputPrefix);
                if (source == null || string.IsNullOrEmpty(directory)) return;
                File.Copy(source, Path.Combine(directory, Path.GetFileName(outputPrefix) + ScanResponseParser.CameraInfoSuffix), true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionManager] Camera info copy skipped: {e.Message}");
            }
        }

        private static string LatestFile(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) return null;
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string path in Directory.GetFiles(directory, pattern))
            {
                DateTime time = File.GetLastWriteTimeUtc(path);
                if (newest == null || time > newestTime)
                {
                    newest = path;
                    newestTime = time;
                }
            }
            return newest;
        }

        // The sink can be replaced at any time. Forwarding keeps the service's reference stable.
        private sealed class ForwardingSink : IScanResultSink
        {
            private readonly SessionManager _owner;
            public ForwardingSink(SessionManager owner) => _owner = owner;
            public void OnScanReconstructed(ScanReconstructionResult result)
            {
                if (_owner.ResultSink != null) _owner.ResultSink.OnScanReconstructed(result);
                else Debug.LogWarning($"[SessionManager] No result sink. Dropped {result.ModelFileName}.");
            }

            public void OnScanFailed(ScanFailure failure)
            {
                if (_owner.ResultSink != null) _owner.ResultSink.OnScanFailed(failure);
                else Debug.LogWarning($"[SessionManager] No result sink. {failure.Kind} request failed: {failure.Error}");
            }
        }

        private sealed class SystemScanFileSystem : IScanFileSystem
        {
            public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
            public void Delete(string path) => File.Delete(path);
        }
    }
}
