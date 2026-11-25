# Required IDs and credentials

A Freeform Collaboration build that lacks one of these runs, looks fine, and silently does not work (an early build had no voice because
`AppIdVoice` was empty). Every required one is checked at four points, from one list
(`Assets/SpatialBlend/Framework/Scripts/RequiredCredentials.cs`). No check ever prints a value: presence and length only.

## Inventory

| ID / credential | Required? | Read at | What broke silently without it | How it is supplied |
|---|---|---|---|---|
| Photon Fusion App ID (`PhotonAppSettings.AppIdFusion`, a GUID) | **Required** (Quest, Windows, macOS spectator) | Build: the settings asset. Run time: `FusionBootstrap` via Photon | No room could be created or joined. | `python3 tools/setup_sdks.py --photon-app-id <id>` or `SPATIALBLEND_PHOTON_APP_ID`; written to the git-ignored `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset` |
| Photon Voice App ID (`PhotonAppSettings.AppIdVoice`; a self-hosted `Server` also counts) | **Required** (Quest, Windows; not the spectator, which has no microphone) | Build: the settings asset. Run time: `FusionVoiceClient` | Microphone opened, avatar lip-synced, nobody heard anybody. A *separate* "Voice" application in the Photon dashboard; the Fusion ID does not work. | `python3 tools/setup_sdks.py --photon-voice-app-id <id>` or `SPATIALBLEND_PHOTON_VOICE_APP_ID`; same asset |
| TURN client secret (the credentials Worker's `X-SpatialBlend-Client` secret, `docs/webrtc-turn-relay.md`) | **Required only with a TURN Worker** (Quest, Windows, macOS spectator). There is no Worker by default (an empty `turnCredentialsUrl`), so TURN is off and the secret is not needed until you deploy your own Worker | Build: `~/.config/spatialblend/turn-client-secret`, copied by `TurnClientSecretSource` into git-ignored `Assets/Resources/SpatialBlendTurnClientSecret.txt` and embedded. Run time: `TurnClientSecret.Load` (`TurnIceService`) | No relay: on venue Wi-Fi that blocks a direct connection the WebRTC link failed to open and fell back to the slow Photon relay (about 16 KB/s), no error. | `python3 tools/setup_sdks.py --turn-secret-file <file>` or `SPATIALBLEND_TURN_CLIENT_SECRET_FILE`; the spectator may leave it out on purpose with `tools/build-spectator-mac.sh --no-turn-secret` |
| TURN Worker URL | Not a secret. Empty by default (`SpatialBlendSettings.DefaultTurnCredentialsUrl`): set it for your own Worker (`tools/turn-credentials-worker`) | `TurnWorkerUrl.Resolve` | (empty URL = TURN off, see above) | `--turn-worker-url` / `SPATIALBLEND_TURN_WORKER_URL` -> `Assets/Resources/SpatialBlendTurnWorkerUrl.txt` |
| Meta / Oculus app id (Platform `MobileAppID`, Avatars app id) | **Not required** by this app: it always loads the preset avatars (`AvatarLoadDecision`), and nothing else uses entitlement or Platform services. Only a player's own CDN avatar (never selected today) would need it, and then the code already falls back to a preset with an error log. | `OvrPlatformInit` (only on the CDN avatar path) | n/a | Meta XR project config (git-ignored `Assets/Oculus`) |
| Scan server (`SpatialBlendSettings.sessionServerAddress`, empty by default) | Not a build credential, and optional: with no server, scanning is off (the tutorial uses its practice server). Set your server in the settings asset or a device `spatialblend-local.json`. | `SessionManager.Initialize`, `LocalSessionConfig` | Scanning stays off. | `docs/local-session-config.md`. The panel says why scanning is off: no server, camera permission, or unreachable. The build and `setup_sdks.py` do not require it |
| Other `setup_sdks.py` items | Not credentials: the Unity version, UPM pins, Photon SDK, Meta Avatars assets, native libraries and TextMesh Pro are checked and installed by `setup_sdks.py` (SDKS.lock), each with its own guard | | | `python3 tools/setup_sdks.py` |

## The four checks

1. **Build guard** (`RequiredCredentialsBuildGuard`, runs before every Android, Windows or macOS build). One message lists every missing
   required ID, what breaks without it, how to supply it and its opt-out variable. The macOS target is the desktop spectator.
2. **Build stamp** (`assets/SpatialBlendBuildStamp.json`, written by `TmpEssentialsBuildGuard`): `voiceAppIdConfigured`, `photonAppIdConfigured`,
   `turnClientSecretConfigured` and `turnRequired`, each recording what the build really has. `tools/check_apk_contents.py` rejects an APK
   where one is not `true` (TURN: unless `turnRequired` is `false`), and a stamp from before the guard. An opted-out build therefore yields
   an APK that cannot pass the check.
3. **Run time** (`RequiredCredentialsRuntime`, in players, after the first scene loads): one `[credentials] missing id=<key> required=true
   impact="..." fix="..."` error per missing required ID, plus one info line with presence and length. The control panel
   (`ControlPanel.ShowCredentialNotices`) shows a line per missing ID (for example "Voice disabled: no Photon Voice App ID in this build") and
   a 10 second toast, and a neutral "Scanning is off" line that says why: no server is set, camera permission is denied, or the server could not be reached.
4. **`setup_sdks.py`** (a GUID Fusion ID, a non-blank trimmed secret file, as the guard counts them; it wants the TURN secret only when a Worker URL is configured (`--turn-worker-url`, `SPATIALBLEND_TURN_WORKER_URL` or `Assets/Resources/SpatialBlendTurnWorkerUrl.txt`), as the guard does, and it does not see a Voice `Server` or a secret already in Resources) lists every required ID as `ok` / `missing` with how to set it. `--check` exits 2 when one is missing; an applying
   run reports them and fails only with `--require-user-vars`.

## Opt-outs (compile-only checks)

Set to exactly `1`. Each waives only its own credential. `tools/lib/unity-env.sh` strips variables containing SECRET, KEY, TOKEN or
PASSWORD before Unity starts, so the names avoid those words.

| Variable | Waives |
|---|---|
| `SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID` | the Fusion App ID |
| `SPATIALBLEND_ALLOW_NO_VOICE_APP_ID` | the Voice App ID |
| `SPATIALBLEND_ALLOW_NO_TURN` | the TURN client secret |
| `build-spectator-mac.sh --no-turn-secret` (`-spectatorNoTurnSecret`) | the TURN secret of the spectator build, on purpose |

Example, an `Il2CppBuildCheck` copy that only proves the code compiles:
`SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID=1 SPATIALBLEND_ALLOW_NO_VOICE_APP_ID=1 SPATIALBLEND_ALLOW_NO_TURN=1 ...`. Its APK stamps the credentials as
missing and `check_apk_contents.py` rejects it, so it cannot be shipped by mistake.

## A release build needs

On the build machine, outside git: the Fusion App ID and the Voice App ID in `PhotonAppSettings.asset` (`setup_sdks.py --photon-app-id
... --photon-voice-app-id ...`), and, only if you deployed a TURN Worker and set its URL, the TURN client secret in
`~/.config/spatialblend/turn-client-secret`. Then `python3 tools/setup_sdks.py --check` exits 0 (it wants the secret only when a Worker URL is
configured) and, after the build, `python3 tools/check_apk_contents.py <apk>` passes.
