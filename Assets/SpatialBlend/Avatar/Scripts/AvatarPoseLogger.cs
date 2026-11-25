// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Fusion;
using Oculus.Avatar2;
using Oculus.Avatar2.Experimental;
using UnityEngine;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend
{
    /// <summary>
    /// Device-run diagnostic: logs the poses a multi-user avatar check needs, as `AVATARLOG {json}` lines in logcat.
    /// Compiled in only in the Editor and in a player built with the scripting define <c>SPATIALBLEND_SIM_HARNESS</c> (the Windows sim
    /// harness) or <c>SPATIALBLEND_AVATAR_POSE_LOG</c> (a device logging build); a shipped build does not contain it. Even then it is off
    /// unless the flag file <c>avatar_pose_log.flag</c> exists in <see cref="Application.persistentDataPath"/>
    /// (on a Quest: <c>adb shell touch /sdcard/Android/data/PACKAGE/files/avatar_pose_log.flag</c>). It writes nothing but log
    /// lines and changes no behaviour of the app. tools/avatar-multiuser/analyze_avatar_log.py reads the lines of two headsets.
    ///
    /// Lines:
    ///   {"v":1,"kind":"pose","who":"local","ts_ms":..,"root":[x,y,z],"head":..,"head_fwd":..,"lh":..,"rh":..}
    ///       the user's own tracked head and controllers (OVRCameraRig anchors, world space of this room);
    ///   {"v":1,"kind":"pose","who":"remote","id":N,"ts_ms":..,"head":..,"head_fwd":..,"lh":..,"rh":..}
    ///       a received avatar's head and hands as realised in this room (SbRetargetTransform), with "sb_joints" (the
    ///       walking Sb avatar's hips, ankles and balls, world) and "animation" (the SDK legs rig: mecanim_found, is_moving,
    ///       move_speed, move_fwd, turn_left/right, turn_strength), so foot travel can be compared with root travel;
    ///   {"v":1,"kind":"objects","objects":{"name":{"poly":[[x,z]..],"touch":[x,y,z]}}}
    ///       the shared objects of this room (footprint of the renderer bounds, touch point = top centre).
    /// Timestamps are the device's UTC clock in milliseconds; the protocol measures the offset between the two devices.
    /// </summary>
    public sealed class AvatarPoseLogger : MonoBehaviour
    {
        public const string FlagFileName = "avatar_pose_log.flag";
        private const float IntervalSeconds = 0.1f;
        private const float ObjectsIntervalSeconds = 5f;
        private float _next, _nextObjects;
        private string _lastObjects;
        private readonly StringBuilder _sb = new StringBuilder(2048);
        private readonly List<RemoteSource> _remotes = new List<RemoteSource>();
        private float _nextDiscovery;

        // Sb is instantiated separately from its Fusion object, not below it. Discover via the
        // network behaviour and cache the SDK rig; never reflect over Animator/controller fields.
        private sealed class RemoteSource
        {
            internal SbAvatarBehaviourFusion Behaviour;
            internal MetaAvatarEntity Meta;
            internal SbNetworkAvatarEntity Entity;
            internal SbAvatarEntity Sb;
            internal MecanimLegsAnimationController Controller;
            internal OvrAvatarAnimationBehavior Animation;
            internal Animator Animator;
            private RuntimeAnimatorController _animatorController;
            internal bool HasMovementParameters;

            internal void RefreshRig()
            {
                SbAvatarEntity current = Meta != null ? Meta._sbAvatar : null;
                if (current != Sb)
                {
                    Sb = current;
                    Controller = null;
                    Animation = null;
                    Animator = null;
                    _animatorController = null;
                    HasMovementParameters = false;
                }
                if (Sb == null) return;
                Animation = Sb.GetComponent<OvrAvatarAnimationBehavior>();
                Controller = Sb.GetComponentInChildren<MecanimLegsAnimationController>(true);
                Animator next = Controller != null ? Controller.GetComponent<Animator>() : null;
                RuntimeAnimatorController runtime = next != null ? next.runtimeAnimatorController : null;
                if (Animator == next && _animatorController == runtime) return;
                Animator = next;
                _animatorController = runtime;
                HasMovementParameters = false;
                if (Animator == null || runtime == null) return;
                bool moving = false, speed = false;
                foreach (AnimatorControllerParameter parameter in Animator.parameters)
                {
                    moving |= parameter.nameHash == IsMovingId && parameter.type == AnimatorControllerParameterType.Bool;
                    speed |= parameter.nameHash == MoveSpeedId && parameter.type == AnimatorControllerParameterType.Float;
                }
                HasMovementParameters = moving && speed;
            }
        }

        private static readonly int IsMovingId = Animator.StringToHash("IsMoving");
        private static readonly int MoveSpeedId = Animator.StringToHash("MoveSpeed");
        private static readonly int MoveForwardId = Animator.StringToHash("MoveForward");
        private static readonly int TurnLeftId = Animator.StringToHash("TurnLeft");
        private static readonly int TurnRightId = Animator.StringToHash("TurnRight");
        private static readonly int TurnStrengthId = Animator.StringToHash("TurnStrength");

#if UNITY_EDITOR || SPATIALBLEND_SIM_HARNESS || SPATIALBLEND_AVATAR_POSE_LOG
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            try
            {
                if (!File.Exists(Path.Combine(Application.persistentDataPath, FlagFileName))) return;
            }
            catch (Exception) { return; }
            var host = new GameObject("AvatarPoseLogger");
            DontDestroyOnLoad(host);
            host.AddComponent<AvatarPoseLogger>();
            Debug.Log("AVATARLOG {\"v\":1,\"kind\":\"start\"}");
        }
#endif

        private void LateUpdate()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + IntervalSeconds;
            if (Time.unscaledTime >= _nextDiscovery)
            {
                _nextDiscovery = Time.unscaledTime + 1f;
                DiscoverRemotes();
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LogLocal(ts);
            foreach (RemoteSource source in _remotes)
            {
                if (source.Behaviour == null) continue;
                NetworkObject owner = source.Behaviour.Object;
                SbRetargetTransform retarget = source.Sb != null ? source.Sb._retargetTransform : null;
                if (owner == null || !owner.IsValid || owner.HasInputAuthority) continue;
                // A missing rig is diagnostic state, not a reason to omit the remote record.
                LogRemote(ts, owner.StateAuthority.RawEncoded, owner.HasInputAuthority, owner.HasStateAuthority, source, retarget);
            }
            if (Time.unscaledTime >= _nextObjects)
            {
                _nextObjects = Time.unscaledTime + ObjectsIntervalSeconds;
                LogObjects();
            }
        }

        private void DiscoverRemotes()
        {
            _remotes.RemoveAll(source => source.Behaviour == null);
            foreach (SbAvatarBehaviourFusion behaviour in FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsSortMode.None))
            {
                RemoteSource source = null;
                foreach (RemoteSource existing in _remotes)
                {
                    if (existing.Behaviour == behaviour) { source = existing; break; }
                }
                if (source == null)
                {
                    source = new RemoteSource { Behaviour = behaviour };
                    _remotes.Add(source);
                }
                source.Meta = behaviour.GetComponent<MetaAvatarEntity>();
                source.Entity = behaviour.GetComponent<SbNetworkAvatarEntity>();
                source.RefreshRig();
            }
        }

        private static string V(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "[{0:0.####},{1:0.####},{2:0.####}]", v.x, v.y, v.z);

        private void LogLocal(long ts)
        {
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig == null || rig.centerEyeAnchor == null) return;
            Vector3 head = rig.centerEyeAnchor.position;
            Vector3 root = new Vector3(head.x, 0f, head.z);
            _sb.Clear();
            _sb.Append("AVATARLOG {\"v\":1,\"kind\":\"pose\",\"who\":\"local\",\"ts_ms\":").Append(ts)
               .Append(",\"root\":").Append(V(root))
               .Append(",\"head\":").Append(V(head))
               .Append(",\"head_fwd\":").Append(V(rig.centerEyeAnchor.forward))
               .Append(",\"lh\":").Append(V(rig.leftHandAnchor != null ? rig.leftHandAnchor.position : head))
               .Append(",\"rh\":").Append(V(rig.rightHandAnchor != null ? rig.rightHandAnchor.position : head))
               .Append('}');
            Debug.Log(_sb.ToString());
        }

        private void LogRemote(long ts, int id, bool inputAuthority, bool stateAuthority, RemoteSource source, SbRetargetTransform r)
        {
            Transform head = r != null ? r.headTransform : null;
            string headPosition = head != null ? V(head.position) : "null";
            _sb.Clear();
            _sb.Append("AVATARLOG {\"v\":1,\"kind\":\"pose\",\"who\":\"remote\",\"id\":").Append(id).Append(",\"ts_ms\":").Append(ts)
               .Append(",\"head\":").Append(headPosition)
               .Append(",\"head_fwd\":").Append(head != null ? V(head.forward) : "null")
               .Append(",\"lh\":").Append(r != null && r.leftHandTransform != null ? V(r.leftHandTransform.position) : headPosition)
               .Append(",\"rh\":").Append(r != null && r.rightHandTransform != null ? V(r.rightHandTransform.position) : headPosition)
               .Append(",\"display_mode\":").Append((int)source.Behaviour.DisplayModeForDiagnostics)
               .Append(",\"interacting\":").Append(B(source.Behaviour.IsInteractingForDiagnostics))
               .Append(",\"input_authority\":").Append(B(inputAuthority))
               .Append(",\"state_authority\":").Append(B(stateAuthority));
            if (source.Entity != null)
            {
                source.Entity.GetRealizedPose(out Vector3 position, out Quaternion rotation);
                _sb.Append(",\"root\":").Append(V(position)).Append(",\"root_fwd\":").Append(V(rotation * Vector3.forward));
            }
            AppendSdkState(_sb, "sb", source.Sb);
            AppendSdkState(_sb, "meta", source.Meta);
            AppendLegJoints(_sb, "sb_joints", source.Sb);
            AppendAnimationState(_sb, source.Controller, source.Animation, source.Animator, source.HasMovementParameters);
            _sb.Append('}');
            Debug.Log(_sb.ToString());
        }

        private static string B(bool value) => value ? "true" : "false";

        // Hidden is the SDK's requested visibility, not proof of a rendered/skinned frame.
        internal static void AppendSdkState(StringBuilder text, string key, OvrAvatarEntity avatar)
        {
            text.Append(",\"").Append(key).Append("\":");
            if (avatar == null) { text.Append("null"); return; }
            text.Append("{\"is_local\":").Append(B(avatar.IsLocal))
                .Append(",\"created\":").Append(B(avatar.IsCreated))
                .Append(",\"hidden\":").Append(B(avatar.Hidden))
                .Append(",\"active\":").Append(B(avatar.isActiveAndEnabled))
                .Append(",\"origin\":").Append(V(avatar.transform.position))
                .Append(",\"origin_fwd\":").Append(V(avatar.transform.forward));
            var provider = avatar.InputManager != null ? avatar.InputManager.InputTrackingProvider : null;
            OvrAvatarInputTrackingState input = default;
            bool available = provider != null && provider.GetInputTrackingState(out input);
            text.Append(",\"input_available\":").Append(B(available))
                .Append(",\"input_active\":").Append(B(available && input.headsetActive));
            if (available && input.headsetActive)
                text.Append(",\"input_head\":").Append(V(input.headset.position))
                    .Append(",\"input_head_fwd\":").Append(V(input.headset.orientation * Vector3.forward));
            text.Append('}');
        }

        // The joints a walk is judged by (foot travel against root travel, step cadence), on the Sb avatar: the one shown
        // while a remote person walks between objects. Only critical joints have a Transform (SbAvatarPrefab lists Hips,
        // both ankles and both balls); a joint that is not critical is written as null, never asked (the SDK logs an error).
        private static readonly (string Key, CAPI.ovrAvatar2JointType Joint)[] LegJoints =
        {
            ("hips", CAPI.ovrAvatar2JointType.Hips),
            ("lankle", CAPI.ovrAvatar2JointType.LeftFootAnkle), ("rankle", CAPI.ovrAvatar2JointType.RightFootAnkle),
            ("lball", CAPI.ovrAvatar2JointType.LeftFootBall), ("rball", CAPI.ovrAvatar2JointType.RightFootBall),
        };

        internal static void AppendLegJoints(StringBuilder text, string key, OvrAvatarEntity avatar)
        {
            text.Append(",\"").Append(key).Append("\":");
            if (avatar == null) { text.Append("null"); return; }
            text.Append('{');
            for (int i = 0; i < LegJoints.Length; ++i)
            {
                if (i > 0) text.Append(',');
                text.Append('"').Append(LegJoints[i].Key).Append("\":");
                Transform joint = null;
                try
                {
                    if (avatar.HasCriticalJoint(LegJoints[i].Joint)) joint = avatar.GetSkeletonTransform(LegJoints[i].Joint);
                }
                catch (Exception) { joint = null; }
                text.Append(joint != null ? V(joint.position) : "null");
            }
            text.Append('}');
        }

        internal static void AppendAnimationState(StringBuilder text, MecanimLegsAnimationController controller,
            OvrAvatarAnimationBehavior animation, Animator animator, bool hasMovementParameters)
        {
            text.Append(",\"animation\":{\"mecanim_found\":").Append(B(controller != null))
                .Append(",\"mecanim_active\":").Append(B(controller != null && controller.isActiveAndEnabled))
                .Append(",\"behaviour_active\":").Append(B(animation != null && animation.isActiveAndEnabled));
            if (controller != null)
                text.Append(",\"lower_body_fwd\":").Append(V(controller.LowerBodyRotation * Vector3.forward));
            if (animation != null)
                text.Append(",\"sdk_root_fwd\":").Append(V(animation.RootRotation * Vector3.forward));
            bool readable = animator != null && animator.isActiveAndEnabled && animator.isInitialized && hasMovementParameters;
            text.Append(",\"animator_readable\":").Append(B(readable));
            if (readable)
            {
                text.Append(",\"is_moving\":").Append(B(animator.GetBool(IsMovingId)))
                    .Append(",\"move_speed\":").Append(animator.GetFloat(MoveSpeedId).ToString("R", CultureInfo.InvariantCulture))
                    .Append(",\"move_fwd\":").Append(B(animator.GetBool(MoveForwardId)))
                    .Append(",\"turn_left\":").Append(B(animator.GetBool(TurnLeftId)))
                    .Append(",\"turn_right\":").Append(B(animator.GetBool(TurnRightId)))
                    .Append(",\"turn_strength\":").Append(animator.GetFloat(TurnStrengthId).ToString("R", CultureInfo.InvariantCulture));
                if (animator.layerCount > 0)
                    text.Append(",\"state_hash\":").Append(animator.GetCurrentAnimatorStateInfo(0).fullPathHash);
            }
            text.Append('}');
        }

        private void LogObjects()
        {
            SpatialBlendManager manager = FindFirstObjectByType<SpatialBlendManager>();
            if (manager == null) return;
            var seen = new HashSet<string>();
            _sb.Clear();
            _sb.Append("{\"v\":1,\"kind\":\"objects\",\"objects\":{");
            bool first = true;
            foreach (SbEntity entity in manager.Entities)
            {
                if (entity == null || entity.CompareTag("Avatar")) continue;
                var renderers = entity.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) continue;
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; ++i) b.Encapsulate(renderers[i].bounds);
                string name = entity is SbMRUKAnchorEntity anchor ? anchor.SceneLabel.ToString() : entity.gameObject.name.Replace("(Clone)", "").Trim();
                if (!seen.Add(name)) continue;
                if (!first) _sb.Append(',');
                first = false;
                Vector3 mn = b.min, mx = b.max;
                _sb.Append('"').Append(name).Append("\":{\"poly\":")
                   .AppendFormat(CultureInfo.InvariantCulture, "[[{0:0.###},{1:0.###}],[{2:0.###},{1:0.###}],[{2:0.###},{3:0.###}],[{0:0.###},{3:0.###}]]", mn.x, mn.z, mx.x, mx.z)
                   .Append(",\"touch\":").Append(V(new Vector3(b.center.x, mx.y, b.center.z))).Append('}');
            }
            _sb.Append("}}");
            string body = _sb.ToString();
            if (body == _lastObjects) return;
            _lastObjects = body;
            Debug.Log("AVATARLOG " + body);
        }
    }
}
