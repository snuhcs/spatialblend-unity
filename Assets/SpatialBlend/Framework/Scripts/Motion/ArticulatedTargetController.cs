// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Motion
{
    public sealed class ArticulatedTargetController : MonoBehaviour
    {
        [Serializable]
        public sealed class Limb
        {
            public Transform Upper, Lower, End;
            public Vector3 BendNormal = Vector3.forward;
        }
        [Serializable]
        public sealed class Rig
        {
            public Transform Hip, SeatContact, StandingHipReference, Head;
            public Limb LeftArm = new Limb(), RightArm = new Limb(), LeftLeg = new Limb(),
                        RightLeg = new Limb();
        }
        private struct Desired
        {
            public bool Active;
            public Pose Pose;
            public float Weight;
        }
        private struct WorldPose
        {
            public Transform T;
            public Vector3 P;
            public Quaternion R;
            public WorldPose(Transform t)
            {
                T = t;
                P = t.position;
                R = t.rotation;
            }
        }
        [SerializeField]
        private AnchorTargetController _targets;
        [SerializeField]
        private Rig _rig = new Rig();
        [SerializeField]
        private Vector3 _hipToSeatContact;
        [SerializeField]
        private bool _hasSeatCalibration;
        private Desired _left, _right, _head, _hip;
        private string _hipAnchor;
        private bool _hipIsContact, _standing;
        private Vector3 _leftFoot, _rightFoot;
        private float _leftUpperLength, _leftLowerLength, _rightUpperLength, _rightLowerLength;
        private double _lastTick;
        public event Action<string, string> TargetRejected;
        public Rig BodyRig => _rig;
        public Vector3 HipToSeatContact => _hipToSeatContact;
        public bool HasSeatCalibration => _hasSeatCalibration;

        public void Configure(AnchorTargetController targets, Rig rig)
        {
            Unsubscribe();
            ReleaseAll();
            _targets = targets;
            _rig = rig ?? new Rig();
            // A hip-local offset is meaningful only for the rig that produced it.
            _hipToSeatContact = Vector3.zero;
            _hasSeatCalibration = false;
            Subscribe();
        }
        public bool CalibrateSeatContact(Vector3 contact)
        {
            if (!Validate("seat_contact") || _rig.SeatContact == null || !CarriedContact() ||
                !Finite(contact))
            {
                Reject("seat_contact", "Seat contact must be finite and carried by Hip.");
                return false;
            }
            _hipToSeatContact = Quaternion.Inverse(_rig.Hip.rotation) * (contact - _rig.Hip.position);
            _hasSeatCalibration = true;
            return true;
        }
        private void OnEnable() => Subscribe();
        private void OnDisable()
        {
            Unsubscribe();
            ReleaseAll();
        }
        private void OnDestroy()
        {
            Unsubscribe();
            ReleaseAll();
        }
        private void Update() => Tick(Time.unscaledTimeAsDouble);
        public void Tick(double now)
        {
            if (!Finite(now))
                return;
            _lastTick = now;
            if (!_standing)
                return;
            Transform reference = _rig == null ? null : _rig.StandingHipReference;
            if (reference == null || !Validate("pelvis"))
            {
                StopStanding("pelvis", "Standing reference or rig is invalid.");
                return;
            }
            // Released, the hip returns to its live standing reference at once, with no
            // fade: a fade has no v1 or paper basis.
            Vector3 hip = reference.position;
            Quaternion rotation = reference.rotation;
            if (!LegsReach(hip, rotation))
            {
                StopStanding("pelvis", "Live standing reference is outside leg reach.");
                return;
            }
            ApplyHip(hip, rotation);
            ReapplyUpper();
            _standing = false;
            _hip.Active = false;
        }
        private void Subscribe()
        {
            if (_targets == null)
                return;
            _targets.TargetApplied -= Applied;
            _targets.TargetReleased -= Released;
            _targets.TargetApplied += Applied;
            _targets.TargetReleased += Released;
        }
        private void Unsubscribe()
        {
            if (_targets == null)
                return;
            _targets.TargetApplied -= Applied;
            _targets.TargetReleased -= Released;
        }
        private void Applied(string anchor, Pose pose, bool height, double weight)
        {
            if (weight <= 0 || !Finite(pose.position) || !Validate(anchor))
                return;
            Desired d =
                new Desired { Active = true, Pose = pose, Weight = Mathf.Clamp01((float)weight) };
            switch (anchor)
            {
            case "hand_l":
                d = BlendOnce(_rig.LeftArm.End, d);
                if (!CanReach(_rig.LeftArm, d))
                {
                    Reject(anchor, "Target is outside limb reach.");
                    return;
                }
                _left = d;
                break;
            case "hand_r":
            case "hand":
                d = BlendOnce(_rig.RightArm.End, d);
                if (!CanReach(_rig.RightArm, d))
                {
                    Reject(anchor, "Target is outside limb reach.");
                    return;
                }
                _right = d;
                break;
            case "head":
                _head = BlendOnce(_rig.Head, d);
                break;
            case "pelvis":
                if (!SetHip(d, false, anchor))
                    return;
                break;
            case "seat_contact":
                if (!SetHip(d, true, anchor))
                    return;
                break;
            default:
                return;
            }
            ApplyAll();
        }
        private void Released(string anchor)
        {
            switch (anchor)
            {
            case "hand_l":
                _left.Active = false;
                break;
            case "hand_r":
            case "hand":
                _right.Active = false;
                break;
            case "head":
                _head.Active = false;
                break;
            // Do not ApplyAll here: it would re-lerp the released fractional hip target.
            case "pelvis":
            case "seat_contact":
                if (_hipAnchor == anchor) StartStanding();
                return;
            default:
                return;
            }
            ApplyAll();
        }
        private bool SetHip(Desired d, bool contact, string anchor)
        {
            if (contact && (_rig.SeatContact == null || !CarriedContact()))
            {
                Reject(anchor, "seat_contact must be Hip or a Hip descendant.");
                return false;
            }
            if (contact && !_hasSeatCalibration && !CalibrateSeatContact(_rig.SeatContact.position))
                return false;
            Vector3 target = HipTarget(d, contact);
            if (!_hip.Active)
            {
                _leftFoot = _rig.LeftLeg.End.position;
                _rightFoot = _rig.RightLeg.End.position;
                CaptureLegLengths();
            }
            Vector3 appliedPosition = Vector3.Lerp(_rig.Hip.position, target, d.Weight);
            Quaternion appliedRotation =
                Quaternion.Slerp(_rig.Hip.rotation, d.Pose.rotation, d.Weight);
            if (!LegsReach(appliedPosition, appliedRotation))
            {
                Reject(anchor, "Body target is outside fixed foot reach.");
                return false;
            }
            // Resolve the command's weight once. Recomposition after another
            // body-part update must not repeatedly lerp this same command.
            d.Pose = new Pose(appliedPosition, appliedRotation);
            d.Weight = 1f;
            _hip = d;
            _hipIsContact = false;
            _hipAnchor = anchor;
            _standing = false;
            return true;
        }
        // Fixed order makes hand->pelvis and pelvis->hand converge: body, left arm, right arm,
        // head.
        private void ApplyAll()
        {
            if (!Validate("rig"))
                return;
            if (_hip.Active)
            {
                Vector3 target = HipTarget(_hip, _hipIsContact);
                Quaternion rotation =
                    Quaternion.Slerp(_rig.Hip.rotation, _hip.Pose.rotation, _hip.Weight);
                Vector3 appliedPosition = Vector3.Lerp(_rig.Hip.position, target, _hip.Weight);
                if (!LegsReach(appliedPosition, rotation))
                {
                    Reject("pelvis", "Active body target is outside leg reach.");
                    return;
                }
                ApplyHip(appliedPosition, rotation);
            }
            ReapplyUpper();
        }
        private void ReapplyUpper()
        {
            if (_left.Active)
                ApplyLimb(_rig.LeftArm, _left, "hand_l");
            if (_right.Active)
                ApplyLimb(_rig.RightArm, _right, "hand_r");
            if (_head.Active)
            {
                _rig.Head.position =
                    Vector3.Lerp(_rig.Head.position, _head.Pose.position, _head.Weight);
                _rig.Head.rotation =
                    Quaternion.Slerp(_rig.Head.rotation, _head.Pose.rotation, _head.Weight);
            }
        }
        private void ApplyHip(Vector3 position, Quaternion rotation)
        {
            var tracked = CaptureInactive();
            _rig.Hip.SetPositionAndRotation(position, rotation);
            Solve(_rig.LeftLeg, _leftFoot);
            Solve(_rig.RightLeg, _rightFoot);
            Restore(tracked);
        }
        private void ApplyLimb(Limb limb, Desired d, string anchor)
        {
            Vector3 target = Vector3.Lerp(limb.End.position, d.Pose.position, d.Weight);
            if (!Reach(limb, target))
            {
                Reject(anchor, "Active target became unreachable.");
                return;
            }
            Solve(limb, target);
            limb.End.rotation = Quaternion.Slerp(limb.End.rotation, d.Pose.rotation, d.Weight);
        }

        private static Desired BlendOnce(Transform target, Desired command)
        {
            command.Pose = new Pose(Vector3.Lerp(target.position, command.Pose.position, command.Weight),
                Quaternion.Slerp(target.rotation, command.Pose.rotation, command.Weight));
            command.Weight = 1f;
            return command;
        }
        private void StartStanding()
        {
            if (!_hip.Active)
                return;
            _hip.Active = false;
            if (_rig.StandingHipReference == null)
            {
                _hip.Active = false;
                return;
            }
            _standing = true;
        }
        private void StopStanding(string a, string why)
        {
            _standing = false;
            _hip.Active = false;
            Reject(a, why);
        }
        private void ReleaseAll()
        {
            _standing = false;
            _left.Active = _right.Active = _head.Active = _hip.Active = false;
        }
        private Vector3 HipTarget(Desired d, bool contact) =>
            contact ? d.Pose.position - d.Pose.rotation * _hipToSeatContact : d.Pose.position;
        private void CaptureLegLengths()
        {
            _leftUpperLength =
                Vector3.Distance(_rig.LeftLeg.Upper.position, _rig.LeftLeg.Lower.position);
            _leftLowerLength =
                Vector3.Distance(_rig.LeftLeg.Lower.position, _rig.LeftLeg.End.position);
            _rightUpperLength =
                Vector3.Distance(_rig.RightLeg.Upper.position, _rig.RightLeg.Lower.position);
            _rightLowerLength =
                Vector3.Distance(_rig.RightLeg.Lower.position, _rig.RightLeg.End.position);
        }
        private bool LegsReach(Vector3 hip, Quaternion rotation) =>
            ReachFixed(_rig.LeftLeg, Socket(_rig.LeftLeg, hip, rotation), _leftFoot,
                       _leftUpperLength, _leftLowerLength) &&
            ReachFixed(_rig.RightLeg, Socket(_rig.RightLeg, hip, rotation), _rightFoot,
                       _rightUpperLength, _rightLowerLength);
        private Vector3 Socket(Limb limb, Vector3 hip, Quaternion rotation)
        {
            Vector3 worldOffset = limb.Upper.position - _rig.Hip.position;
            Vector3 unrotatedOffset = Quaternion.Inverse(_rig.Hip.rotation) * worldOffset;
            return hip + rotation * unrotatedOffset;
        }
        private static bool ReachFixed(Limb limb, Vector3 root, Vector3 target, float a, float b)
        {
            float d = Vector3.Distance(root, target);
            return a > .0001f && b > .0001f && d > .0001f && d >= Mathf.Abs(a - b) - .0001f &&
                   d <= a + b + .0001f;
        }
        private static bool CanReach(Limb l, Desired d) => Reach(l, Vector3.Lerp(l.End.position,
                                                                                 d.Pose.position,
                                                                                 d.Weight));
        private bool Validate(string anchor)
        {
            if (_rig == null || _rig.Hip == null || _rig.Head == null || !Valid(_rig.LeftArm) ||
                !Valid(_rig.RightArm) || !Valid(_rig.LeftLeg) || !Valid(_rig.RightLeg))
            {
                Reject(anchor, "Rig needs hip, head, and complete chains.");
                return false;
            }
            var all = new HashSet<Transform> { _rig.Hip };
            if (!all.Add(_rig.Head))
            {
                Reject(anchor, "Hip/head aliases and cross-chain shared transforms are invalid.");
                return false;
            }
            foreach (Limb l in new[] { _rig.LeftArm, _rig.RightArm, _rig.LeftLeg, _rig.RightLeg })
            {
                if (!l.Lower.IsChildOf(l.Upper) || !l.End.IsChildOf(l.Lower))
                {
                    Reject(anchor, "Chains require lower under upper and end under lower.");
                    return false;
                }
                foreach (Transform t in new[] { l.Upper, l.Lower, l.End })
                    if (!all.Add(t))
                    {
                        Reject(anchor,
                               "Hip/head aliases and cross-chain shared transforms are invalid.");
                        return false;
                    }
            }
            if (!_rig.LeftLeg.Upper.IsChildOf(_rig.Hip) || !_rig.RightLeg.Upper.IsChildOf(_rig.Hip))
            {
                Reject(anchor, "Hip must carry leg roots.");
                return false;
            }
            return true;
        }
        private bool CarriedContact() => _rig.SeatContact == _rig.Hip
                                         || _rig.SeatContact.IsChildOf(_rig.Hip);
        private List<WorldPose> CaptureInactive()
        {
            var r = new List<WorldPose>();
            if (!_head.Active)
                Add(r, _rig.Head);
            if (!_left.Active)
                Add(r, _rig.LeftArm);
            if (!_right.Active)
                Add(r, _rig.RightArm);
            return r;
        }
        private static void Add(List<WorldPose> r, Limb l)
        {
            Add(r, l.Upper);
            Add(r, l.Lower);
            Add(r, l.End);
        }
        private static void Add(List<WorldPose> r, Transform t)
        {
            if (t != null)
                r.Add(new WorldPose(t));
        }
        private static void Restore(List<WorldPose> r)
        {
            foreach (WorldPose p in r)
                if (p.T != null)
                    p.T.SetPositionAndRotation(p.P, p.R);
        }
        private static bool Valid(Limb l) => l != null && l.Upper != null && l.Lower != null
                                             && l.End != null && l.Upper != l.Lower
                                             && l.Lower != l.End && l.Upper != l.End;
        private static bool Reach(Limb l, Vector3 t) => ReachFrom(l, l.Upper.position, t);
        private static bool ReachFrom(Limb l, Vector3 root, Vector3 t)
        {
            float a = Vector3.Distance(root, l.Lower.position),
                  b = Vector3.Distance(l.Lower.position, l.End.position),
                  d = Vector3.Distance(root, t);
            return a > .0001f && b > .0001f && d > .0001f && d >= Mathf.Abs(a - b) - .0001f &&
                   d <= a + b + .0001f;
        }
        private static void Solve(Limb l, Vector3 target)
        {
            Vector3 root = l.Upper.position, to = target - root;
            float a = Vector3.Distance(root, l.Lower.position),
                  b = Vector3.Distance(l.Lower.position, l.End.position), d = to.magnitude;
            Vector3 dir = to / d,
                    side = Vector3.Cross(l.Upper.TransformDirection(l.BendNormal), dir);
            if (side.sqrMagnitude < .000001f)
                side = Vector3.Cross(Vector3.right, dir);
            if (side.sqrMagnitude < .000001f)
                side = Vector3.Cross(Vector3.forward, dir);
            side.Normalize();
            float x = (a * a - b * b + d * d) / (2 * d),
                  y = Mathf.Sqrt(Mathf.Max(0, a * a - x * x));
            Vector3 knee = root + dir * x + side * y;
            l.Upper.rotation =
                Quaternion.FromToRotation(l.Lower.position - root, knee - root) * l.Upper.rotation;
            l.Lower.rotation =
                Quaternion.FromToRotation(l.End.position - l.Lower.position, target - knee) *
                l.Lower.rotation;
        }
        private void Reject(string a, string r) => TargetRejected?.Invoke(a, r);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
