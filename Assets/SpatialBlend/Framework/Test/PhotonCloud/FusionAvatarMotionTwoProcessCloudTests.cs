// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Avatar motion between TWO UNITY PROCESSES over the real Photon Cloud, each a participant in a DIFFERENT room layout
    /// (the host in layout A, the joiner in layout B). Each process drives its OWN avatar through a scripted body motion (a walk to
    /// each shared object, a reach, a gaze switch), and observes the OTHER's avatar, so both directions are measured in one run.
    ///
    /// Real: Photon Cloud, the product's core link (FusionConnectionManager over WebRTC or Photon), SpatialBlendManager and the native
    /// core solving Spacing on the root and Facing on the head (FreeformCollaborationApp.AvatarRelationBindings), the product's
    /// SbNetworkAvatarEntity.UpdateStates send path with its hysteresis, the embodied driver and SbRetargetTransform on the receiver.
    /// Stand-ins, and why: (1) the avatar is a NetworkObject with no Meta avatar (the Meta Avatar SDK has no macOS runtime, so
    /// SbAvatarBehaviourFusion.OnSkeletonLoaded never fires and a real remote avatar has no retarget here); (2) the user's tracked
    /// head and hands are transforms the script moves every frame in place of the OVRCameraRig anchors a headset updates (the entity
    /// reads them exactly as it reads centerEyeAnchor); (3) the hand stream is two child transforms replicated by Fusion's NetworkTransform (state replication at the
    /// tick rate), their local position being the head-relative offset MetaInputTrackingDelegate sends, written at its rate (20 Hz) and applied when it
    /// changes as SbAvatarBehaviourFusion.OnHandPositionsChanged does: the same data and the same application, not the same carrier.
    /// The room: each participant authors the Freeform Chess at its own pose (a region that carries the object) and registers a table and
    /// a whiteboard as plain elements of different footprints; there is no MRUK room (no MRUK scene geometry is produced on a Mac).
    /// Output: AVATARLOG lines (the format of AvatarPoseLogger, so one analysis serves this run and a device run) in
    /// SPATIALBLEND_TP_DIR/avatar-ROLE.log, and with SPATIALBLEND_TP_GRAPHICS=1 a PNG sequence of the observer's room with the other's avatar.
    /// Environment: SPATIALBLEND_TP_ROLE=host|joiner, _ROOM, _MODE, _DIR, _SCENARIO=avatar. Run by tools/run-two-process-cloud-test.sh.
    /// </summary>
    [TestFixture]
    [Category("TwoProcessCloud")]
    public class FusionAvatarMotionTwoProcessCloudTests
    {
        private const float HeadHeight = 1.58f;                 // EmbodiedDriverConfig.SpawnHeightMeters
        private const float ScriptStepSeconds = 0.1f;
        private const float HandSendIntervalSeconds = 0.05f;    // MetaInputTrackingDelegate.handUpdateInterval
        private const float HandLerpTimeSeconds = 0.5f;         // SbAvatarBehaviourFusion.LERP_TIME
        private const float WalkMetersPerSecond = 1.2f;

        private NetworkRunner _runner;
        private GameObject _managerObject, _runnerObject, _rigObject, _appObject;
        private SpatialBlendSettings _settings, _previousSettings;
        private readonly List<GameObject> _scratch = new List<GameObject>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            if (_managerObject != null) { _managerObject.GetComponent<SpatialBlendManager>()?.Disconnect(); UnityEngine.Object.Destroy(_managerObject); }
            if (_runner != null) { var t = _runner.Shutdown(); while (!t.IsCompleted) yield return null; }
            foreach (var o in new[] { _runnerObject, _rigObject, _appObject }) if (o != null) UnityEngine.Object.Destroy(o);
            foreach (var o in _scratch) if (o != null) UnityEngine.Object.Destroy(o);
            if (_settings != null)
            {
                typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _previousSettings);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        private static string Env(string name) => Environment.GetEnvironmentVariable("SPATIALBLEND_TP_" + name);

        private static IEnumerator Until(Func<bool> done, string what, float seconds, Func<string> describe = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > end) NUnitAssert.Fail($"Timed out after {seconds}s waiting for {what}. {describe?.Invoke()}");
                yield return null;
            }
        }

        // ---- layouts and the scripted body motion (the same as NativeAvatarSemanticTransferTests) -------------------------

        private sealed class Obj { public string Name; public long Id; public Vector2[] Poly; public float Top; public Vector3? TouchOverride; public bool Authored; }

        private static Vector2[] Box(float x0, float z0, float x1, float z1) =>
            new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };

        // The Chess is the Freeform scene's authored object (a region that carries it), placed at a different pose in each room; the table
        // and the whiteboard are plain elements by shared id (the core links them), with different footprints and positions.
        private const long ChessSharedId = 202;
        private const string ChessPrefabPath = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private static readonly Vector3 ChessTouchLocal = new Vector3(0.1f, 0.508f, 0f);

        private static (Vector2 floor, Obj[] objects, Matrix4x4 chessWorld) Layout(string name)
        {
            Matrix4x4 world = name == "A" ? Matrix4x4.TRS(new Vector3(4.5f, 0f, 3.2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one)
                                          : Matrix4x4.TRS(new Vector3(6.0f, 0f, 3.3f), Quaternion.Euler(0f, 110f, 0f), Vector3.one);
            var chess = new Obj
            {
                Name = "chess", Id = ChessSharedId, Authored = true, TouchOverride = world.MultiplyPoint3x4(ChessTouchLocal), Top = 0.508f,
                Poly = new[] { new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.25f, 0f, -0.25f), new Vector3(0.25f, 0f, 0.25f), new Vector3(-0.25f, 0f, 0.25f) }
                    .Select(q => { var w = world.MultiplyPoint3x4(q); return new Vector2(w.x, w.z); }).ToArray()
            };
            return name == "A"
                ? (new Vector2(6f, 5f), new[]
                {
                    new Obj { Name = "table", Id = 201, Poly = Box(1.8f, 3.2f, 3.0f, 4.0f), Top = 0.75f },
                    chess,
                    new Obj { Name = "whiteboard", Id = 203, Poly = Box(0.1f, 1.5f, 0.3f, 3.0f), Top = 1.30f },
                }, world)
                : (new Vector2(8f, 4.5f), new[]
                {
                    new Obj { Name = "table", Id = 201, Poly = Box(5.4f, 0.8f, 6.8f, 1.6f), Top = 0.75f },
                    chess,
                    new Obj { Name = "whiteboard", Id = 203, Poly = Box(7.7f, 1.0f, 7.9f, 3.0f), Top = 1.30f },
                }, world);
        }

        private static Vector3 Centroid(Obj o) { var c = Vector2.zero; foreach (var p in o.Poly) c += p; c /= o.Poly.Length; return new Vector3(c.x, 0f, c.y); }
        private static Vector3 Touch(Obj o) { if (o.TouchOverride.HasValue) return o.TouchOverride.Value; var c = Centroid(o); return new Vector3(c.x, o.Top, c.z); }

        private struct Sample { public float T; public Vector3 Root; public float Yaw, Pitch; public Vector3 Left, Right; public string Phase; }

        private static float YawToward(Vector3 a, Vector3 b) => Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;
        private static float PitchToward(Vector3 a, Vector3 b) { var d = b - a; return -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg; }

        private static Vector3 StandPoint(Obj o, Vector3 from, float distance = 0.6f)
        {
            Vector3 c = Centroid(o);
            Vector2 toward = new Vector2(from.x - c.x, from.z - c.z);
            Vector2 min = o.Poly[0], max = o.Poly[0];
            foreach (var p in o.Poly) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Vector2 half = (max - min) * 0.5f;
            if (Mathf.Abs(toward.x) / Mathf.Max(half.x, 0.01f) > Mathf.Abs(toward.y) / Mathf.Max(half.y, 0.01f))
                return new Vector3(c.x + Mathf.Sign(toward.x) * (half.x + distance), 0f, c.z);
            return new Vector3(c.x, 0f, c.z + Mathf.Sign(toward.y) * (half.y + distance));
        }

        private static List<Sample> Script(Vector2 floor, Obj[] objects, Matrix4x4 chessWorld)
        {
            var list = new List<Sample>();
            Vector3 pos = new Vector3(floor.x * 0.5f, 0f, floor.y * 0.5f);
            float yaw = 0f, pitch = 0f;
            Func<Vector3, float, Vector3> rest = (root, y) => root + Quaternion.Euler(0f, y, 0f) * new Vector3(0.25f, HeadHeight - 0.55f, 0.25f);
            void Emit(string phase, Vector3? right = null)
            {
                var s = new Sample { T = list.Count * ScriptStepSeconds, Root = pos, Yaw = yaw, Pitch = pitch, Phase = phase };
                s.Left = pos + Quaternion.Euler(0f, yaw, 0f) * new Vector3(-0.25f, HeadHeight - 0.55f, 0.25f);
                s.Right = right ?? rest(pos, yaw);
                list.Add(s);
            }
            void Hold(string phase, float seconds) { for (float t = 0f; t < seconds; t += ScriptStepSeconds) Emit(phase); }
            void Turn(string phase, float toYaw, float toPitch)
            {
                float d = Mathf.DeltaAngle(yaw, toYaw);
                int n = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(d) / (90f * ScriptStepSeconds)));
                float y0 = yaw, p0 = pitch;
                for (int i = 1; i <= n; ++i) { yaw = y0 + d * i / n; pitch = Mathf.Lerp(p0, toPitch, (float)i / n); Emit(phase); }
            }
            void Walk(string phase, Vector3 to, Obj facing)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pos, to) / (WalkMetersPerSecond * ScriptStepSeconds)));
                Vector3 from = pos; float y0 = yaw, walkYaw = YawToward(from, to);
                for (int i = 1; i <= n; ++i)
                {
                    pos = Vector3.Lerp(from, to, (float)i / n);
                    yaw = y0 + Mathf.DeltaAngle(y0, walkYaw) * Mathf.Min(1f, i / (float)Mathf.Max(1, n / 3));
                    Emit(phase);
                }
                if (facing != null) Turn(phase, YawToward(pos + Vector3.up * HeadHeight, Touch(facing)), 0f);
            }
            void Reach(Obj o)
            {
                Vector3 head = pos + Vector3.up * HeadHeight, touch = Touch(o), r0 = rest(pos, yaw);
                yaw = YawToward(head, touch); pitch = PitchToward(head, touch);
                for (int i = 1; i <= 8; ++i) Emit("reach_" + o.Name, Vector3.Lerp(r0, touch, i / 8f));
                for (float t = 0f; t < 3f; t += ScriptStepSeconds) Emit("touch_" + o.Name, touch);
                for (int i = 7; i >= 0; --i) Emit("retract_" + o.Name, Vector3.Lerp(r0, touch, i / 8f));
                pitch = 0f;
            }
            Func<string, Obj> find = n => objects.First(o => o.Name == n);
            Hold("stand", 2f);
            Walk("walk_table", StandPoint(find("table"), pos), find("table"));
            Reach(find("table"));
            foreach (string n in new[] { "chess", "whiteboard", "chess" })
            {
                Vector3 head = pos + Vector3.up * HeadHeight;
                Turn("look_" + n, YawToward(head, Touch(find(n))), PitchToward(head, Touch(find(n))));
                Hold("look_" + n, 3f);
            }
            Walk("walk_whiteboard", StandPoint(find("whiteboard"), pos), find("whiteboard"));
            Reach(find("whiteboard"));
            // The Chess: in the object's own frame, from outside its region to inside, reach, look away and back, and back out.
            Func<Vector3, Vector3> W = l => chessWorld.MultiplyPoint3x4(l);
            float cy = chessWorld.rotation.eulerAngles.y;
            Walk("walk_chess", W(new Vector3(0f, 0f, -2.5f)), null);
            Turn("walk_chess", cy, 0f);
            int steps = Mathf.CeilToInt(1.8f / (WalkMetersPerSecond * ScriptStepSeconds));
            Vector3 outside = pos, stand = W(new Vector3(0f, 0f, -0.7f));
            for (int i = 1; i <= steps; ++i) { pos = Vector3.Lerp(outside, stand, (float)i / steps); yaw = cy; Emit("walk_in_chess"); }
            {
                Vector3 head = pos + Vector3.up * HeadHeight, touch = W(ChessTouchLocal), r0 = rest(pos, yaw);
                pitch = PitchToward(head, touch);
                for (int i = 1; i <= 8; ++i) Emit("reach_chess", Vector3.Lerp(r0, touch, i / 8f));
                for (float t = 0f; t < 3f; t += ScriptStepSeconds) Emit("touch_chess", touch);
                for (int i = 7; i >= 0; --i) Emit("retract_chess", Vector3.Lerp(r0, touch, i / 8f));
                float reachPitch = pitch;
                Turn("look_away", cy + 70f, 0f); Hold("look_away", 3f);
                Turn("look_back", cy, reachPitch); Hold("look_back", 3f);
                pitch = 0f;
            }
            for (int i = 1; i <= steps; ++i) { pos = Vector3.Lerp(stand, outside, (float)i / steps); yaw = cy; Emit("walk_out_chess"); }
            Hold("end", 4f);
            return list;
        }

        // ---- the avatar -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The product's SbNetworkAvatarEntity (its UpdateStates send path with the hysteresis, its PrepareSync) on a NetworkObject
        /// without a Meta avatar: the head and hands are the retarget's transforms, the entity transform is the tracked head.
        /// </summary>
        public sealed class StandInAvatarEntity : SbNetworkAvatarEntity
        {
            public Transform Head, Left, Right;
            public SbRetargetTransform Retarget;
            private static readonly string[] HeadOnly = { SbRetargetAnchorTargetBridge.HeadAnchor };
            public override EntityType EntityType => EntityType.Network;   // the Fusion replica is matched by NetworkId (no MetaAvatarEntity here)
            public override void Start()
            {
                if (!IsAttached()) foreach (var m in FindObjectsByType<SpatialBlendManager>(FindObjectsSortMode.None)) TryLazySync.Invoke(m, null);
            }
            public override void ApplyRealizationTarget(Vector3 position, Quaternion rotation) => Retarget.ApplyRootRealizationTransform(position, rotation);
            public override IReadOnlyList<string> RealizedAnchorNames => HeadOnly;
            public override bool TryGetRealizedAnchorPose(string anchorName, out Vector3 position, out Quaternion rotation)
            {
                position = default; rotation = Quaternion.identity;
                if (anchorName != SbRetargetAnchorTargetBridge.HeadAnchor || Head == null) return false;
                Head.GetPositionAndRotation(out position, out rotation);
                return true;
            }
            private static readonly MethodInfo TryLazySync = typeof(SpatialBlendManager).GetMethod("TryLazySync", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly MethodInfo SetT = typeof(SbEntity).GetMethod("SetTransform", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly FieldInfo LocalT = typeof(SbNetworkAvatarEntity).GetField("_localTransform", BindingFlags.Instance | BindingFlags.NonPublic);
            public void UseAsLocal(Transform trackedHead) { SetT.Invoke(this, new object[] { trackedHead }); LocalT.SetValue(this, trackedHead); }
            public void UseAsRemote() => SetT.Invoke(this, new object[] { Head });
        }

        // The family errors of the native proposal (-1 = the anchor has no relation of that family): which relations an anchor carries.
        private static string ErrFields(SbAnchorProposal[] native, uint anchor)
        {
            if (native == null) return "";
            foreach (var n in native)
                if (n.struct_size != 0 && n.anchor_id == anchor)
                    return string.Format(CultureInfo.InvariantCulture, ",\"err_spacing\":{0:0.###},\"err_facing\":{1:0.###},\"err_pose\":{2:0.###}", n.err_spacing_target, n.err_facing_target, n.err_pose_target);
            return "";
        }

        // Scope diagnostics of the local avatar's actor (the core's own record of where it is contained), for the run's log.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ScopeRecord
        {
            public uint struct_size, version;
            public IntPtr anchor_name, tag_filter;
            public double anchor_x, anchor_y, anchor_z, anchor_yaw, time_seconds;
            public long binding_index, scope_root, kernel_scope_origin, deepest_containing, navigable_element;
            public long candidate_count, included_count;
            public int kernel_type, valid, anchor_active, explicit_scope, scope_live, reserved;
        }
        [System.Runtime.InteropServices.DllImport(SpatialBlend.Internal.SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int sb_get_scope_records(long space, long actor, [System.Runtime.InteropServices.Out] ScopeRecord[] records, int capacity, uint structSize);
        [System.Runtime.InteropServices.DllImport(SpatialBlend.Internal.SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int sb_set_observability_capture(long space, int enabled);

        [System.Runtime.InteropServices.DllImport(SpatialBlend.Internal.SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int sb_get_element_parent(long space, long element, out long parent);

        private static string ScopeLine(long space, long actor, long region, long obj, long room)
        {
            var records = new ScopeRecord[8];
            for (int i = 0; i < records.Length; ++i) records[i].struct_size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ScopeRecord>();
            int n = sb_get_scope_records(space, actor, records, records.Length, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ScopeRecord>());
            var parts = new List<string>();
            for (int i = 0; i < Mathf.Min(n, records.Length); ++i)
                parts.Add($"{{\"kernel\":{records[i].kernel_type},\"valid\":{records[i].valid},\"deepest\":{records[i].deepest_containing},\"scope_root\":{records[i].scope_root},\"candidates\":{records[i].candidate_count},\"included\":{records[i].included_count}}}");
            sb_get_element_parent(space, region, out long regionParent); sb_get_element_parent(space, obj, out long objectParent);
            return $"AVATARLOG {{\"v\":1,\"kind\":\"scope\",\"region\":{region},\"region_parent\":{regionParent},\"object\":{obj},\"object_parent\":{objectParent},\"room\":{room},\"records\":[{string.Join(",", parts)}]}}";
        }

        private sealed class ReplicaActivator : MonoBehaviour
        {
            private void Update()
            {
                foreach (var e in FindObjectsByType<StandInAvatarEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!e.gameObject.activeSelf && e.GetComponent<NetworkObject>() != null && e.GetComponent<NetworkObject>().IsValid) e.gameObject.SetActive(true);
            }
        }

        private sealed class AvatarApp : SbApp
        {
            public readonly List<GameObject> Added = new List<GameObject>();
            public Action<GameObject> OnAdded;
            private bool _started;
            public override void OnAppStart() { _started = true; }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType type)
            {
                if (type != OnObjectAddedType.Added) return;
                Added.Add(gameObject);
                OnAdded?.Invoke(gameObject);
            }
            public override void OnRemove(GameObject gameObject) { }
            public override bool IsAppStarted => _started;
        }

        private GameObject BuildAvatarTemplate()
        {
            var go = new GameObject("tp-avatar-template") { tag = "Avatar" };
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<NetworkObject>();
            Transform Part(string n, PrimitiveType shape, float size, Color color)
            {
                var p = GameObject.CreatePrimitive(shape);
                p.name = n; p.transform.SetParent(go.transform, false); p.transform.localScale = Vector3.one * size;
                UnityEngine.Object.Destroy(p.GetComponent<Collider>());
                p.GetComponent<Renderer>().material.color = color;
                return p.transform;
            }
            var head = Part("head", PrimitiveType.Sphere, 0.22f, new Color(0.95f, 0.75f, 0.35f));
            var left = Part("hand_l", PrimitiveType.Sphere, 0.09f, new Color(0.3f, 0.7f, 1f));
            var right = Part("hand_r", PrimitiveType.Sphere, 0.09f, new Color(1f, 0.4f, 0.3f));
            // The hand stream: two children whose LOCAL position is the hand's head-relative offset, replicated by Fusion's own NetworkTransform
            // (state replication at the tick rate, as the product's [Networked] HandTransformData is). A test assembly may not define a
            // NetworkBehaviour of its own (tools/test_fusion_weave_list.py), so the product's component is not mimicked, a built-in one carries it.
            foreach (string n in new[] { "hand_stream_l", "hand_stream_r" })
            {
                var h = new GameObject(n); h.transform.SetParent(go.transform, false);
                h.AddComponent<NetworkTransform>();
            }
            var entity = go.AddComponent<StandInAvatarEntity>();
            entity.AllowManipulation = false;
            var retarget = go.AddComponent<SbRetargetTransform>();
            retarget.headTransform = head; retarget.leftHandTransform = left; retarget.rightHandTransform = right;
            entity.Head = head; entity.Left = left; entity.Right = right; entity.Retarget = retarget;
            new NetworkObjectBaker().Bake(go);
            return go;
        }

        // ---- the run ----------------------------------------------------------------------------------------------------

        [UnityTest, Timeout(1500000)]
        public IEnumerator OneSideOfTheAvatarRun()
        {
            var role = Env("ROLE");
            if (string.IsNullOrEmpty(role) || Env("SCENARIO") != "avatar") NUnitAssert.Ignore("Not a two-process avatar run.");
            var room = Env("ROOM"); var mode = Env("MODE"); var dir = Env("DIR");
            bool host = role == "host";
            string layoutName = host ? "A" : "B";
            if (!PhotonAppSettings.TryGetGlobal(out var photon) || !Guid.TryParse(photon.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            LogAssert.ignoreFailingMessages = true;   // the Meta Avatar SDK has no macOS binary; the Photon client logs the peer's disconnect as an error
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;
            Application.targetFrameRate = 60;

            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings.useWebRtc = mode != "Photon";
            _previousSettings = SpatialBlendManager.CurrentSettings;
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);

            _managerObject = new GameObject("tp-core-" + role); _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            _managerObject.SetActive(true);

            _runnerObject = new GameObject("tp-runner-" + role);
            _runner = _runnerObject.AddComponent<NetworkRunner>();
            FusionConnectionManager connection = null;
            _runner.AddCallbacks(new Events(
                (r, p) => connection?.OnPlayerJoined(r, p), (r, p) => connection?.OnPlayerLeft(r, p), () => connection?.ShutdownConnection()));
            var connTemplate = new GameObject("FusionConnectionManagerTemplate"); connTemplate.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(connTemplate);
            connTemplate.AddComponent<NetworkObject>();
            var connTemplate2 = connTemplate.AddComponent<FusionConnectionManager>();
            typeof(FusionConnectionManager).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connTemplate2, manager);
            new NetworkObjectBaker().Bake(connTemplate);
            var avatarTemplate = BuildAvatarTemplate();
            // Every replica of the (inactive) template is inactive too, and the core matches a replica by NetworkId among ACTIVE objects.
            var activator = new GameObject("tp-replica-activator"); _scratch.Add(activator);
            activator.AddComponent<ReplicaActivator>();
            _scratch.Add(connTemplate); _scratch.Add(avatarTemplate);

            if (!host) yield return Until(() => File.Exists(Path.Combine(dir, "host-ready")), "the host to be in the room", 240);
            var start = _runner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room, PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, AuthValues = TwoProcessIdentity.AuthValues(),
                SceneManager = _runnerObject.AddComponent<NetworkSceneManagerDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 60);
            TestContext.Out.WriteLine($"TWOPROC role={role} StartGame ok={start.Result.Ok} reason={start.Result.ShutdownReason} error={start.Result.ErrorMessage}");
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            NUnitAssert.That(_runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic { Object = connTemplate.GetComponent<NetworkObject>(),
                AssetGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000044") }, out var connPrefab), Is.True);
            NUnitAssert.That(_runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic { Object = avatarTemplate.GetComponent<NetworkObject>(),
                AssetGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000055") }, out var avatarPrefab), Is.True);
            NUnitAssert.That(_runner.IsSharedModeMasterClient, Is.EqualTo(host));
            TestContext.Out.WriteLine($"TWOPROC role={role} userid={TwoProcessIdentity.Describe(_runner)} pid={System.Diagnostics.Process.GetCurrentProcess().Id} layout={layoutName} mode={mode} graphics={Environment.GetEnvironmentVariable("SPATIALBLEND_TP_GRAPHICS")}");
            if (host)
            {
                connection = _runner.Spawn(connPrefab).GetComponent<FusionConnectionManager>();
                connection.gameObject.SetActive(true);
                connection.OnPlayerJoined(_runner, _runner.LocalPlayer);
                File.WriteAllText(Path.Combine(dir, "host-ready"), "1");
            }
            yield return Until(() => _runner.ActivePlayers.Count() == 2, "both players", 240);
            var remotePlayer = _runner.ActivePlayers.First(p => p != _runner.LocalPlayer);
            Func<FusionConnectionManager> find = () => UnityEngine.Object.FindObjectsByType<FusionConnectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(m => m != connTemplate2 && m.Runner == _runner);
            if (!host) { yield return Until(() => find() != null, "the connection manager", 120); connection = find(); connection.gameObject.SetActive(true); }
            Func<string> describe = () => $"role={role} state={manager.ConnectionState} linked={connection.LinkedPeerCount} carrier={connection.ActiveCarrier} path={connection.ActivePath}";
            yield return Until(() => manager.ConnectionState == SpatialBlendConnectionState.Connected && connection.LinkedPeerCount == 1, "the product's core link", 120, describe);
            TestContext.Out.WriteLine("TWOPROC linked " + describe());

            // The app: it accepts the peer's avatar and gives it what FreeformCollaborationApp gives a received avatar.
            _appObject = new GameObject("tp-app"); _appObject.SetActive(false);
            var app = _appObject.AddComponent<AvatarApp>();
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
            _appObject.SetActive(true);
            // The product registers content only after its apps have started (the connection manager's round).
            yield return Until(() => app.IsAppStarted, "the app start", 120, describe);
            // The room of this participant: the walkable floor and the shared objects, by shared id (the peers link them by that id).
            var (floor, objects, chessWorld) = Layout(layoutName);
            // The room element is made on first use; an object element added under room 0 would be refused.
            long roomElement = (long)typeof(SpatialBlendManager).GetMethod("EnsureRoomElement", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
            NUnitAssert.That(roomElement, Is.GreaterThan(0), "the room element");
            NUnitAssert.That(manager.RegisterWalkableMesh(new[] { new Vector3(0, 0, 0), new Vector3(floor.x, 0, 0), new Vector3(floor.x, 0, floor.y), new Vector3(0, 0, floor.y) },
                new[] { 0, 1, 2, 0, 2, 3 }), Is.True);
            // SPATIALBLEND_TP_SHARE=table (test-only): only the table is shared; the whiteboard and the Chess stay this room's own, unknown to the peer.
            bool tableOnly = Env("SHARE") == "table";
            foreach (var o in objects.Where(x => !x.Authored && (!tableOnly || x.Name == "table")))
            {
                long e = SpatialBlendAPI.AddElement(manager.ID, roomElement);
                SpatialBlendAPI.SetElementSharedId(manager.ID, e, o.Id);
                SpatialBlendAPI.SetElementTag(manager.ID, e, o.Name);
                SpatialBlendAPI.SetElementPolygon(manager.ID, e, SbGeometryRole.Containment, o.Poly);
            }

            // The Chess, authored as the Freeform scene authors it: each participant authors its own copy at its own pose from the same
            // declaration (the same shared id), under its own room, as it does at app start; the core links the copies by that id. (A
            // joiner that instead built its copy on the host's region element received before its own room existed got an orphan region,
            // never contained: see the verification doc.)
            if (!tableOnly)
            {
                var chessPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ChessPrefabPath);
                NUnitAssert.That(chessPrefab, Is.Not.Null, ChessPrefabPath);
                NUnitAssert.That(chessPrefab.GetComponent<SbSpatialAuthoring>().TryBuildDeclaration(out var declaration, out string authoringError), Is.True, authoringError);
                NUnitAssert.That(SpatialAuthoringCompiler.TryCompile(declaration, out var plan, out authoringError), Is.True, authoringError);
                NUnitAssert.That(manager.TryApplySpatialAuthoring(plan, ChessSharedId, 0, chessWorld, null, out var binding), Is.True, manager.SceneRegistrationError);
                NUnitAssert.That(manager.ContainmentRegionCount, Is.EqualTo(1), "the Chess is a transfer leaf");
            }
            _rigObject = new GameObject("tp-tracking-rig");
            var eye = new GameObject("centerEyeAnchor").transform; var lh = new GameObject("leftHandAnchor").transform; var rh = new GameObject("rightHandAnchor").transform;
            foreach (var t in new[] { eye, lh, rh }) t.SetParent(_rigObject.transform, false);
            eye.position = new Vector3(floor.x * 0.5f, HeadHeight, floor.y * 0.5f);
            StandInAvatarEntity remote = null; Transform remoteHandL = null, remoteHandR = null;
            app.OnAdded = g =>
            {
                var e = g.GetComponent<StandInAvatarEntity>();
                if (e == null || e.Head == null) return;
                remote = e;
                remoteHandL = g.transform.Find("hand_stream_l"); remoteHandR = g.transform.Find("hand_stream_r");
                e.UseAsRemote();
                var controller = g.AddComponent<AnchorTargetController>();
                var bridge = g.AddComponent<SbRetargetAnchorTargetBridge>();
                bridge.Configure(controller, e.Retarget);
                if (FreeformCollaborationApp.TryComputeRemoteArrivalPose(eye.position, eye.forward, out var p, out var q)) manager.PlaceReceivedAvatar(e, p, q);
            };
            // The peer's avatar may have arrived while this process was still setting up (the first one to register is
            // announced to the other at once): hand over what the app already saw.
            foreach (var arrived in app.Added.ToArray()) app.OnAdded(arrived);
            yield return null;
            yield return null;

            // Own avatar: spawned by this process (state authority), shared by the app as FreeformCollaborationApp does.
            var spawned = _runner.Spawn(avatarPrefab, eye.position, Quaternion.identity, _runner.LocalPlayer);
            spawned.gameObject.SetActive(true);   // the template is inactive (as the connection manager's is), and so is every replica of it
            var mine = spawned.GetComponent<StandInAvatarEntity>();
            Transform mineHandL = spawned.transform.Find("hand_stream_l"), mineHandR = spawned.transform.Find("hand_stream_r");
            mine.UseAsLocal(eye);
            foreach (var r in mine.GetComponentsInChildren<Renderer>()) r.enabled = false;   // the observer's view shows the other avatar only
            app.RegisterGameObject(spawned.gameObject, true);
            yield return Until(() => mine.IsAttached(), "my avatar to be registered", 30, describe);
            foreach ((string anchor, RelationKind kind) in FreeformCollaborationApp.AvatarRelationBindings) app.RegisterRelation(new SpatialRelation(spawned.gameObject, anchor, kind));
            File.WriteAllText(Path.Combine(dir, role + "-registered"), "1");
            yield return Until(() => remote != null, "the peer's avatar", 120, describe);
            TestContext.Out.WriteLine("TWOPROC peer avatar arrived " + describe());

            // Both start the script at the same wall-clock instant.
            if (host)
            {
                yield return Until(() => File.Exists(Path.Combine(dir, "joiner-registered")), "the joiner's avatar", 120, describe);
                File.WriteAllText(Path.Combine(dir, "go"), (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4000).ToString(CultureInfo.InvariantCulture));
            }
            yield return Until(() => File.Exists(Path.Combine(dir, "go")), "the start signal", 120, describe);
            long goAt = long.Parse(File.ReadAllText(Path.Combine(dir, "go")), CultureInfo.InvariantCulture);

            var script = Script(floor, objects, chessWorld);
            long myActor = (long)((System.Collections.IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager))[mine.ID];
            sb_set_observability_capture(manager.ID, 1);
            float nextScope = 0f;
            float total = script[script.Count - 1].T + 6f;
            var log = new StreamWriter(Path.Combine(dir, $"avatar-{role}.log"), false, new UTF8Encoding(false));
            var sb = new StringBuilder();
            Func<Vector3, string> V = v => string.Format(CultureInfo.InvariantCulture, "[{0:0.####},{1:0.####},{2:0.####}]", v.x, v.y, v.z);
            sb.Clear().Append("AVATARLOG {\"v\":1,\"kind\":\"objects\",\"objects\":{");
            sb.Append(string.Join(",", objects.Select(o => "\"" + o.Name + "\":{\"poly\":[" + string.Join(",", o.Poly.Select(p => string.Format(CultureInfo.InvariantCulture, "[{0},{1}]", p.x, p.y))) + "],\"touch\":" + V(Touch(o)) + "}")));
            sb.Append("}}"); log.WriteLine(sb.ToString());

            bool graphics = Environment.GetEnvironmentVariable("SPATIALBLEND_TP_GRAPHICS") == "1" && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            SceneView3D view = graphics ? SceneView3D.Build(floor, objects, role, Path.Combine(dir, "frames-" + role), _scratch) : null;
            yield return Until(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= goAt, "the start instant", 30);

            float t0 = Time.realtimeSinceStartup, nextHand = 0f, nextLocal = 0f, nextShot = 0f;
            int remotePoseLines = 0; bool haveHands = false; Vector3 lastLeft = default, lastRight = default; long remoteActor = 0; SbAnchorProposal[] proposalScratch = null; var proposalSet = new ProposalSet();
            while (Time.realtimeSinceStartup - t0 < total)
            {
                float t = Time.realtimeSinceStartup - t0;
                // The user's tracked head and hands, from the script (linear between the 10 Hz script samples).
                int i = Mathf.Min(script.Count - 1, Mathf.FloorToInt(t / ScriptStepSeconds));
                int j = Mathf.Min(script.Count - 1, i + 1);
                float k = Mathf.Clamp01((t - i * ScriptStepSeconds) / ScriptStepSeconds);
                Vector3 root = Vector3.Lerp(script[i].Root, script[j].Root, k);
                float yaw = script[i].Yaw + Mathf.DeltaAngle(script[i].Yaw, script[j].Yaw) * k, pitch = Mathf.Lerp(script[i].Pitch, script[j].Pitch, k);
                eye.SetPositionAndRotation(root + Vector3.up * HeadHeight, Quaternion.Euler(pitch, yaw, 0f));
                lh.position = Vector3.Lerp(script[i].Left, script[j].Left, k); rh.position = Vector3.Lerp(script[i].Right, script[j].Right, k);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (t >= nextHand)
                {
                    nextHand = t + HandSendIntervalSeconds;
                    // MetaInputTrackingDelegate.UpdateHandPositionsToFusion: the hands in the head's own axes.
                    var inv = Quaternion.Inverse(eye.rotation);
                    Vector3 l = inv * (lh.position - eye.position), r = inv * (rh.position - eye.position);
                    mineHandL.localPosition = l; mineHandR.localPosition = r;
                }
                // OnHandPositionsChanged: applied by the peers when the replicated value changes.
                if (remote != null && remoteHandL != null)
                {
                    Vector3 hl = remoteHandL.localPosition, hr = remoteHandR.localPosition;
                    if (!haveHands || hl != lastLeft || hr != lastRight)
                    {
                        haveHands = true; lastLeft = hl; lastRight = hr;
                        remote.Retarget.SetHandTransform(hl, hr, Quaternion.identity, Quaternion.identity, Time.deltaTime / HandLerpTimeSeconds);
                    }
                }
                if (t >= nextScope)
                {
                    nextScope = t + 1f;
                    log.WriteLine(ScopeLine(manager.ID, myActor, SpatialBlendAPI.FindElementBySharedId(manager.ID, SpatialAuthoringCompiler.RegionSharedId(ChessSharedId)),
                        SpatialBlendAPI.FindElementBySharedId(manager.ID, ChessSharedId), roomElement));
                }
                if (t >= nextLocal)
                {
                    nextLocal = t + 0.05f;
                    log.WriteLine("AVATARLOG {\"v\":1,\"kind\":\"pose\",\"who\":\"local\",\"ts_ms\":" + ts + ",\"root\":" + V(new Vector3(eye.position.x, 0, eye.position.z)) +
                        ",\"head\":" + V(eye.position) + ",\"head_fwd\":" + V(eye.forward) + ",\"lh\":" + V(lh.position) + ",\"rh\":" + V(rh.position) + ",\"phase\":\"" + script[i].Phase + "\",\"transfer\":" +
                        (((bool)typeof(SpatialBlendManager).GetMethod("IsTransferInteracting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(manager, new object[] { mine })) ? "true" : "false") + "}");
                }
                if (remote != null)
                {
                    var rt = remote.Retarget;
                    log.WriteLine("AVATARLOG {\"v\":1,\"kind\":\"pose\",\"who\":\"remote\",\"ts_ms\":" + ts + ",\"head\":" + V(rt.headTransform.position) + ",\"head_fwd\":" + V(rt.headTransform.forward) +
                        ",\"lh\":" + V(rt.leftHandTransform.position) + ",\"rh\":" + V(rt.rightHandTransform.position) + "}");
                    remotePoseLines++;
                    // The core's proposals for the peer's avatar (root and head), what the driver follows: attributes a frozen or jumping
                    // body to the solver or to the realization.
                    if (remoteActor == 0 && ((System.Collections.IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager)).Contains(remote.ID))
                        remoteActor = (long)((System.Collections.IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager))[remote.ID];
                    if (remoteActor != 0 && SpatialBlendAPI.TryReadProposals(manager.ID, remoteActor, ref proposalScratch, proposalSet))
                        foreach (var a in proposalSet.Anchors)
                            log.WriteLine("AVATARLOG {\"v\":1,\"kind\":\"proposal\",\"ts_ms\":" + ts + ",\"anchor\":" + a.AnchorId + ",\"status\":\"" + a.Status + "\",\"pos_unspecified\":" +
                                (a.PositionUnspecified ? "true" : "false") + ",\"pos\":" + V(a.Position) + ",\"yaw\":" + a.YawDegrees.ToString("0.##", CultureInfo.InvariantCulture) + ",\"cause\":\"" + a.Cause + "\"" + ErrFields(proposalScratch, a.AnchorId) + "}");
                    if (view != null && t >= nextShot) { nextShot = t + 0.25f; view.Shot(remote, t); }
                }
                yield return null;
            }
            log.Flush(); log.Dispose();
            TestContext.Out.WriteLine($"TWOPROC role={role} DONE remote_pose_lines={remotePoseLines} frames={(view != null ? view.Count : 0)} {describe()}");
            File.WriteAllText(Path.Combine(dir, role + "-done"), "1");
            yield return Until(() => File.Exists(Path.Combine(dir, (host ? "joiner" : "host") + "-done")), "the peer to finish", 240);
            NUnitAssert.That(remotePoseLines, Is.GreaterThan(100), "the peer's avatar was observed");
            LogAssert.ignoreFailingMessages = true;
        }

        // The observer's room with the other's avatar, rendered offscreen (batchmode with graphics; never a window).
        private sealed class SceneView3D
        {
            private readonly Camera _camera; private readonly RenderTexture _rt; private readonly string _dir; public int Count;
            private Transform _body;
            private SceneView3D(Camera c, RenderTexture rt, string dir) { _camera = c; _rt = rt; _dir = dir; }
            public static SceneView3D Build(Vector2 floor, Obj[] objects, string role, string dir, List<GameObject> keep)
            {
                Directory.CreateDirectory(dir);
                var root = new GameObject("tp-view-" + role); keep.Add(root);
                var f = GameObject.CreatePrimitive(PrimitiveType.Cube); f.transform.SetParent(root.transform);
                f.transform.position = new Vector3(floor.x / 2f, -0.02f, floor.y / 2f); f.transform.localScale = new Vector3(floor.x, 0.04f, floor.y);
                f.GetComponent<Renderer>().material.color = new Color(0.55f, 0.55f, 0.58f);
                foreach (var o in objects)
                {
                    var b = GameObject.CreatePrimitive(PrimitiveType.Cube); b.transform.SetParent(root.transform);
                    Vector2 min = o.Poly[0], max = o.Poly[0]; foreach (var p in o.Poly) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
                    b.transform.position = new Vector3((min.x + max.x) / 2f, o.Top / 2f, (min.y + max.y) / 2f);
                    b.transform.localScale = new Vector3(max.x - min.x, o.Top, max.y - min.y);
                    b.GetComponent<Renderer>().material.color = o.Name == "table" ? new Color(0.6f, 0.42f, 0.25f) : o.Name == "chess" ? new Color(0.2f, 0.2f, 0.2f) : Color.white;
                }
                var cam = new GameObject("view-camera").AddComponent<Camera>(); cam.transform.SetParent(root.transform);
                cam.transform.position = new Vector3(floor.x * 0.5f, 6.5f, -floor.y * 0.55f); cam.transform.LookAt(new Vector3(floor.x * 0.5f, 0.5f, floor.y * 0.5f));
                cam.fieldOfView = 55f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f); cam.enabled = false;
                var light = new GameObject("view-light").AddComponent<Light>(); light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(50f, -30f, 0f); light.transform.SetParent(root.transform);
                var rt = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32); cam.targetTexture = rt;
                var view = new SceneView3D(cam, rt, dir);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule); body.transform.SetParent(root.transform); UnityEngine.Object.Destroy(body.GetComponent<Collider>());
                body.GetComponent<Renderer>().material.color = new Color(0.9f, 0.6f, 0.2f, 1f); view._body = body.transform;
                return view;
            }
            public void Shot(StandInAvatarEntity remote, float t)
            {
                // The avatar's trunk, floor to head: the retarget moves only the head and the hands.
                Vector3 h = remote.Head.position;
                _body.position = new Vector3(h.x, h.y / 2f - 0.1f, h.z); _body.localScale = new Vector3(0.35f, h.y / 2f - 0.1f, 0.35f);
                _camera.Render();
                var prev = RenderTexture.active; RenderTexture.active = _rt;
                var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(_dir, $"f{Count++:D5}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
        }

        private sealed class Events : INetworkRunnerCallbacks
        {
            private readonly Action<NetworkRunner, PlayerRef> _joined, _left; private readonly Action _shutdown;
            public Events(Action<NetworkRunner, PlayerRef> j, Action<NetworkRunner, PlayerRef> l, Action s) { _joined = j; _left = l; _shutdown = s; }
            public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) => _joined(runner, player);
            public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) => _left(runner, player);
            public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) => _shutdown();
            public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
            public void OnInput(NetworkRunner runner, NetworkInput input) { }
            public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
            public void OnConnectedToServer(NetworkRunner runner) { }
            public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
            public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
            public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
            public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
            public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
            public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
            public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
            public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
            public void OnSceneLoadDone(NetworkRunner runner) { }
            public void OnSceneLoadStart(NetworkRunner runner) { }
            public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
            public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        }
    }
}
#endif
