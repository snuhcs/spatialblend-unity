# Local scan-server config (spatialblend-local.json)

Scanning a real object needs a scan server (gRPC `spatialblend.scan.v1`, SAM3 segmentation and SAM3D reconstruction). This
repository names none: `SpatialBlendSettings.sessionServerAddress` is empty by default (serialized in
`Assets/Resources/SpatialBlendSettings.asset`), so scanning is off and the control panel says "Scanning is off: no server". The
tutorial's scan task still runs on its built-in practice server. To scan, set the address of your server in the settings asset
(for every build) or in a device file (below). `sessionUseTls` is on by default: a server behind TLS on port 443, for example
nginx routing only the gRPC paths `/spatialblend.scan.v1.*`. The client dials `host:port` and selects TLS with
`spatialblend_inference_session_connect_tls` (gRPC `SslCredentials`); it does not put `https://` in the address.

To point one headset at a different server, put an optional JSON file in the app's data folder.
A valid `sessionServerAddress` there overrides the settings address. A file that names a server and
has no `tls` key connects in plaintext: that is what those files meant before this default. Set
`"tls": true` to use TLS for that server. A file that does not name a primary address leaves the
settings address (empty by default: no scanning).

## Location

The file is `<Application.persistentDataPath>/spatialblend-local.json`.

On Quest (Android) this is:

```
/sdcard/Android/data/com.hcs.spatialblendcoreunity/files/spatialblend-local.json
```

If the external files folder is unavailable, Unity falls back to internal storage
(`/data/data/<package>/files`), which `adb push` cannot write without root. To confirm where the
app looks, run `adb logcat -s Unity` and look for the startup line
`Looking for spatialblend-local.json in <path>`, which prints the exact `persistentDataPath`. (`com.hcs.spatialblendcoreunity` is the Android application identifier in ProjectSettings. A
different build flavor uses its own package id.) In the Unity Editor, `persistentDataPath` is the
per-project folder Unity reports for your OS.

## Format

A flat JSON object. Every key is optional.

```json
{
  "sessionServerAddress": "scan-host.example:50000",
  "sessionFallbackServerAddress": "scan-backup.example:50001",
  "tls": false,
  "scanSegmentTimeoutSeconds": 30,
  "scanReconstructTimeoutSeconds": 300
}
```

| Key | Meaning |
|-----|---------|
| `sessionServerAddress` | gRPC `host:port` of the primary scan server. |
| `sessionFallbackServerAddress` | Optional second server tried when the primary does not connect. |
| `tls` | Boolean. With a `sessionServerAddress` and no `tls` key, the connection is plaintext. With no primary address, an absent key keeps `sessionUseTls` (true for the public default). `true` connects both addresses over TLS. `false` is plaintext. Needs a session library that exports `spatialblend_inference_session_connect_tls`; with an older one the scan service reports "TLS needs a newer session library" and does not fall back to plaintext. |
| `scanSegmentTimeoutSeconds` | Positive number. Segmentation request timeout. |
| `scanReconstructTimeoutSeconds` | Positive number. Reconstruction request timeout. |

Rules:

- Addresses are plain `host:port` with a port in 1..65535. A URL scheme such as `http://` is refused.
- A valid, non-empty value overrides the matching `SpatialBlendSettings` value. An empty or absent key
  keeps the settings value, except a primary address with no `tls` key: that connection is plaintext.
- A missing file changes nothing and logs no warning (startup still logs the directory it looked in).
- Malformed JSON ignores the whole file. An invalid address or a non-positive timeout ignores only
  that field. A `tls` value that is not `true` or `false` (for example the string `"true"`) is ignored
  the same way. If the file also names a primary server, the connection stays plaintext; otherwise it
  keeps the settings value (TLS for the public default). Either case logs a warning that names the field. The log never contains the address or
  the file contents.
- The file is limited to 64 KiB. A larger file is ignored with a warning.
- If a key appears twice, the last one wins.
- Nested objects and arrays are not supported. A file containing one is treated as malformed.
- The native scan library itself may log the server address; this change cannot control that.
- Startup logs which fields were overridden, by name only.

## Installing on a Quest

```bash
adb push spatialblend-local.json /sdcard/Android/data/com.hcs.spatialblendcoreunity/files/spatialblend-local.json
```

Restart the app after changing the file; the scan service reads it once when it starts.
Deleting the file returns the headset to the settings address. To turn scanning off, the settings address has to be empty as well; the device file alone cannot clear a non-empty settings address (an empty `sessionServerAddress` in the file is ignored).

Keep your real file outside the repository. `spatialblend-local.json` and `*.local.json` are
git-ignored.
