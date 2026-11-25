// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    [CreateAssetMenu(fileName = "SpatialBlendSettings", menuName = "SpatialBlend/SpatialBlend Settings")]
    public class SpatialBlendSettings : ScriptableObject
    {
        /// <summary>Seconds a client waits for the core link round (<see cref="connectDeadlineSeconds"/>).</summary>
        public const float DefaultConnectDeadlineSeconds = 15f;
        /// <summary>Participants that must be ready before the apps start (<see cref="playersToStartApps"/>).</summary>
        public const int DefaultPlayersToStartApps = 2;
        /// <summary>Players per room (<see cref="maxPlayers"/>), the room limit the project has always used.</summary>
        public const int DefaultMaxPlayers = 9;
        /// <summary>
        /// Seconds the direct (WebRTC) carrier may take to come up before the Photon carrier takes over
        /// (<see cref="webRtcIceTimeoutSeconds"/>). STUN retransmits at an initial RTO of 500 ms (RFC 5389 section 7.2.1) and
        /// a reachable pair answers its first checks, so five seconds is ten RTOs; it also leaves two thirds of the 15 s
        /// connection round (<see cref="DefaultConnectDeadlineSeconds"/>) for the join if the fallback is needed.
        /// </summary>
        public const float DefaultWebRtcIceTimeoutSeconds = 5f;
        /// <summary>
        /// Seconds in Scene Edit with no editing before the return to Interaction is announced
        /// (<see cref="modeSwitchIdleSeconds"/>).
        /// </summary>
        public const float DefaultModeSwitchIdleSeconds = 10f;
        /// <summary>
        /// Seconds the announcement is shown before the switch to Interaction happens
        /// (<see cref="modeSwitchCountdownSeconds"/>).
        /// </summary>
        public const float DefaultModeSwitchCountdownSeconds = 5f;
        /// <summary>
        /// Seconds the ray must stay on the same editable object before the "Edit this object?" prompt appears
        /// (<see cref="modeSwitchHoverDwellSeconds"/>): the hover prompt shows only after the ray has stayed on the same object for 1 second.
        /// </summary>
        public const float DefaultModeSwitchHoverDwellSeconds = 1f;
        /// <summary>Google's public STUN server (developers.google.com/talk/libjingle/developer_guide, "stun.l.google.com:19302").</summary>
        public const string DefaultStunServer = "stun:stun.l.google.com:19302";
        /// <summary>
        /// No TURN credentials Worker by default: TURN is off (the Photon relay is the fallback) until you deploy your own Worker
        /// (tools/turn-credentials-worker) and set its URL with tools/setup_sdks.py --turn-worker-url (docs/webrtc-turn-relay.md).
        /// </summary>
        public const string DefaultTurnCredentialsUrl = "";

        [Header("Timeouts")]
        [Tooltip("Default wait time in milliseconds")]
        public int defaultWaitTimeMs = 2000;

        [Tooltip("Connection timeout in milliseconds")]
        public int connectionTimeoutMs = 5000;

        [Tooltip("Event wait timeout in milliseconds")]
        public int eventWaitTimeoutMs = 2000;

        // Hosting and joining a room needs no address: the core link rides the Photon session the players already
        // share (FusionConnectionManager), so the settings name no host or port.

        [Header("Geometry")]
        [Tooltip("Default walkable geometry resolution")]
        public double defaultWalkableGeometryResolution = 0.1;

        [Header("Debug & Logging")]
        [Tooltip("Enable debug logging globally")]
        public bool enableDebugLogging = true;

        [Tooltip("Log every counted runtime work event (NavMesh bake, anchor outline build, room registration, furniture staging) with its duration: RuntimeWorkCounters")]
        public bool logRuntimeWork = false;

        [Tooltip("Specific log categories to enable (flags can be combined)")]
        public SpatialBlendDebug.LogCategory enabledLogCategories = SpatialBlendDebug.LogCategory.All;

        [Tooltip("Only log errors and warnings (reduces verbose output)")]
        public bool errorsAndWarningsOnly = false;

        // Managed knobs only. v2 sb_create_session takes no runtime policy, and
        // nothing applied RuntimePolicy to the native library, so that v1 struct is gone.
        [Header("Runtime Policy")]
        [Tooltip("Retained managed knob. The core does not apply a native runtime policy.")]
        public bool useCustomRuntimePolicy = false;

        [Tooltip("Proximity hit threshold for interaction")]
        public double proximityHitThreshold = 1.0 - 1e-3;

        [Tooltip("Minimal state change threshold (prevents unnecessary updates for small changes)")]
        public double minimalStateChangeThreshold = 0.001;

        [Tooltip("State update interval in milliseconds")]
        public uint stateUpdateIntervalMs = 33;

        [Header("Build Configuration")]
        [Tooltip("Use the debug core library (spatialblend_d) instead of the release library")]
        public bool useDebugLibraries = false;

        [Header("Direct carrier (WebRTC)")]
        [Tooltip("Carry the core link over a WebRTC data channel, peer to peer, when it comes up. The Photon carrier (about 16 KB/s) is the fallback.")]
        public bool useWebRtc = true;

        [Tooltip("STUN servers for NAT traversal. They are always offered; TURN servers from the credentials Worker are added to them when a client secret is configured (see Turn credentials below).")]
        public string[] stunServers = { DefaultStunServer };

        [Tooltip("Allow only relayed ICE candidates. Without TURN servers none exist, so the direct path cannot come up and the link stays on the Photon carrier (a way to exercise the fallback); with TURN configured it forces the link through the TURN relay (a way to prove that path). Default off.")]
        public bool webRtcRelayOnly;

        [Min(1f)]
        [Tooltip("Seconds the direct carrier may take to come up before the Photon carrier takes over. Default 5.")]
        public float webRtcIceTimeoutSeconds = DefaultWebRtcIceTimeoutSeconds;

        [Header("Turn credentials (WebRTC through a relay)")]
        [Tooltip("URL of the TURN credentials Worker. TURN is used only when a client secret is also present (env SPATIALBLEND_TURN_CLIENT_SECRET, or the git-ignored Resources/SpatialBlendTurnClientSecret written by SpatialBlend > TURN > Write Client Secret). Empty turns TURN off.")]
        public string turnCredentialsUrl = DefaultTurnCredentialsUrl;

        [Tooltip("Which TURN transports the direct carrier may use. All by default; narrow it to prove one transport (for example Tls alone) at a venue.")]
        public TurnTransports turnTransports = TurnTransports.All;

        [Header("Mode switching")]
        [Min(1f)]
        [Tooltip("Seconds in Scene Edit with no editing activity before 'Back to Interaction' is announced. Default 10.")]
        public float modeSwitchIdleSeconds = DefaultModeSwitchIdleSeconds;

        [Min(1f)]
        [Tooltip("Seconds the 'Back to Interaction' announcement counts down before the switch. Default 5.")]
        public float modeSwitchCountdownSeconds = DefaultModeSwitchCountdownSeconds;

        [Min(0f)]
        [Tooltip("Seconds the ray must stay on the same editable object before the 'Edit this object?' prompt appears. Default 1.")]
        public float modeSwitchHoverDwellSeconds = DefaultModeSwitchHoverDwellSeconds;

        [Header("Avatar")]
        [Tooltip("Controls how remote avatars are shown in relation to interaction state.")]
        public AvatarDisplayMode avatarDisplayMode = AvatarDisplayMode.Default;

        [Tooltip("Fade duration (seconds) for avatar visibility transitions in ZoneBased mode. 0 = instant.")]
        [Range(0f, 2f)]
        public float fadeAvatarDuration = 1.5f;

        [Header("Addressables")]
        [Tooltip("Addressables key for runtime object preset (GLTF root container)")]
        public string objectPresetAddressKey = "ObjectPreset";

        [Tooltip("Addressables key for framerate defer agent prefab")]
        public string agentAddressKey = "StableFrameRateAgent";

        [Header("Platform Paths")]
        [Tooltip("Android download path to search GLB files")]
        public string androidDownloadPath = "/storage/emulated/0/Download";

        [Header("Scan Session (SAM3 / SAM3D)")]
        [Tooltip("Client identifier sent with scan requests")]
        public string clientId = "unity-client";

        [Tooltip("Append a deterministic per-device suffix to clientId (same build, different device => different ID)")]
        public bool useDeviceSpecificClientId = true;

        /// <summary>
        /// No scan server by default: scanning is off until you run a scan server (gRPC <c>spatialblend.scan.v1</c>) and name it,
        /// here or in a device <c>spatialblend-local.json</c> (docs/local-session-config.md). The address is host:port;
        /// <see cref="sessionUseTls"/> selects <c>spatialblend_inference_session_connect_tls</c>.
        /// </summary>
        public const string DefaultSessionServerAddress = "";

        [Tooltip("Primary scan server, gRPC host:port with no URL scheme. Empty by default (no scan server). " +
                 "spatialblend-local.json on the device overrides it. Empty disables scanning.")]
        public string sessionServerAddress = DefaultSessionServerAddress;

        [Tooltip("Connect with TLS (spatialblend_inference_session_connect_tls, gRPC SslCredentials). " +
                 "On by default: a server behind TLS (port 443) has no plaintext fallback. " +
                 "A device file can set tls to false for a plaintext server.")]
        public bool sessionUseTls = true;

        [Tooltip("Second scan server, gRPC host:port, tried when the primary does not connect. Alone it enables nothing.")]
        public string sessionFallbackServerAddress = "";

        [Tooltip("Seconds to wait for a segmentation answer before the request is reported as timed out")]
        public float scanSegmentTimeoutSeconds = 30f;

        [Tooltip("Seconds to wait for a reconstruction answer before the request is reported as timed out")]
        public float scanReconstructTimeoutSeconds = 300f;

        [Header("Connection round")]
        [Min(1f)]
        [Tooltip("Seconds a participant waits for the core link round before it gives up. Default 15.")]
        public float connectDeadlineSeconds = DefaultConnectDeadlineSeconds;

        [Min(1)]
        [Tooltip("Participants that must be connected and ready before the apps start. Default 2.")]
        public int playersToStartApps = DefaultPlayersToStartApps;

        [Min(2)]
        [Tooltip("Players per room. Passed to Fusion as StartGameArgs.PlayerCount; the untracked Photon config is not the source. Default 9.")]
        public int maxPlayers = DefaultMaxPlayers;

        [Tooltip("Editor folder under Assets that receives scan output")]
        public string editorTestDataPath = "SpatialBlend/Samples/Objects/TestData";

        /// <summary>
        /// Apply these settings to the debug system
        /// </summary>
        public void ApplyToDebugSystem()
        {
            SpatialBlendDebug.Initialize(this);
            SpatialBlendDebug.SetEnabledCategories(enabledLogCategories);
        }
    }
}
