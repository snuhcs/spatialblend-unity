// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>Which input drives an editing pointer: the right controller's ray, or the right hand's ray (pinch to select).</summary>
    public enum EditInputSource
    {
        Controller,
        Hand,
    }

    /// <summary>
    /// One ray the motifs can be driven by: the right controller's or the right hand's. It wraps the Interaction SDK
    /// <see cref="RayInteractor"/> and gives the motifs one shape for both, so they never ask which device it is. Every read can be replaced
    /// (the <c>...Source</c> members) so a test drives a pointer with numbers and no interactor.
    /// </summary>
    public sealed class EditPointer
    {
        private readonly IHand _hand;

        public EditPointer(EditInputSource source, RayInteractor interactor)
        {
            Source = source;
            Interactor = interactor;
            if (source == EditInputSource.Hand && interactor != null) _hand = interactor.GetComponentInParent<IHand>();
        }

        /// <summary>A pointer with no interactor, for tests: set the sources it should answer.</summary>
        public EditPointer(EditInputSource source) { Source = source; }

        public EditInputSource Source { get; }
        public RayInteractor Interactor { get; }

        public Func<Vector3> OriginSource { get; set; }
        public Func<Vector3> ForwardSource { get; set; }
        public Func<Vector3?> HitSource { get; set; }
        public Func<RayInteractable> HoveredSource { get; set; }
        public Func<RayInteractable> SelectedSource { get; set; }
        /// <summary>Whether the index finger and thumb are pinched (a hand pointer). The controller's trigger is read by <see cref="EditInput"/>.</summary>
        public Func<bool> PinchSource { get; set; }
        /// <summary>The wrist's pose in world space (a hand pointer), or null when the hand is not tracked.</summary>
        public Func<Pose?> WristSource { get; set; }
        /// <summary>The wrist-to-knuckles direction in world space (a hand pointer): the axis a wrist twist is measured about. Null when unknown.</summary>
        public Func<Vector3?> FingerAxisSource { get; set; }
        /// <summary>The head's pose in world space, from which the shoulder, and so the reach of a pinching hand, is estimated. Null reads the camera rig's centre eye.</summary>
        public Func<Pose?> HeadSource { get; set; }

        public Vector3 Origin => OriginSource != null ? OriginSource() : Interactor != null ? Interactor.Origin : Vector3.zero;
        public Vector3 Forward => ForwardSource != null ? ForwardSource() : Interactor != null ? Interactor.Forward : Vector3.forward;
        public Ray Ray => new Ray(Origin, Forward);

        /// <summary>Where the ray meets the surface it is on, when it is on one.</summary>
        public Vector3? HitPoint
        {
            get
            {
                if (HitSource != null) return HitSource();
                if (Interactor != null && Interactor.CollisionInfo.HasValue) return Interactor.CollisionInfo.Value.Point;
                return null;
            }
        }

        public RayInteractable Hovered => HoveredSource != null ? HoveredSource() : Interactor != null ? Interactor.Interactable : null;
        public RayInteractable Selected => SelectedSource != null ? SelectedSource() : Interactor != null ? Interactor.SelectedInteractable : null;
        public bool HasCandidate => HoveredSource != null ? HoveredSource() != null : Interactor != null && Interactor.HasCandidate;

        public bool IsPinching
        {
            get
            {
                if (PinchSource != null) return PinchSource();
                return _hand != null && _hand.IsConnected && _hand.GetIndexFingerIsPinching();
            }
        }

        public Pose? WristPose
        {
            get
            {
                if (WristSource != null) return WristSource();
                return _hand != null && _hand.GetRootPose(out Pose pose) ? pose : (Pose?)null;
            }
        }

        public Vector3? FingerAxis
        {
            get
            {
                if (FingerAxisSource != null) return FingerAxisSource();
                if (_hand != null && _hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist) && _hand.GetJointPose(HandJointId.HandMiddle1, out Pose middle))
                    return middle.position - wrist.position;
                return null;
            }
        }

        private static Transform s_Eye;

        public Pose? Head
        {
            get
            {
                if (HeadSource != null) return HeadSource();
                if (s_Eye == null)
                {
                    var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
                    s_Eye = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
                }
                return s_Eye != null ? new Pose(s_Eye.position, s_Eye.rotation) : (Pose?)null;
            }
        }

        /// <summary>Where the right shoulder is relative to the eye, in the head's yaw frame (metres: 0.17 right, 0.20 down). A first value, not measured.</summary>
        public static readonly Vector3 ShoulderFromHead = new Vector3(0.17f, -0.20f, 0f);

        /// <summary>The head's yaw (a rotation about vertical) now; identity when the head is unknown.</summary>
        internal Quaternion HeadYaw()
        {
            var head = Head;
            if (!head.HasValue) return Quaternion.identity;
            Vector3 flat = Vector3.ProjectOnPlane(head.Value.rotation * Vector3.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;
        }

        /// <summary>
        /// How far the hand (the ray origin) is from the shoulder, which is estimated as the head's position plus <see cref="ShoulderFromHead"/> turned
        /// by <paramref name="bodyYaw"/>. The arm pivots about the shoulder, so aiming sweeps the hand around it at a constant distance. The caller
        /// passes a slowly following yaw (<see cref="HandGrabAdjust.HeadYawFollowSeconds"/>), not the head's own: a glance (the head turning on the
        /// body) must not swing the estimate, while a turn of the body, which the head's yaw follows for good, must in the end. Zero when the head is unknown.
        /// </summary>
        internal float ShoulderDistance(Quaternion bodyYaw)
        {
            var head = Head;
            return head.HasValue ? Vector3.Distance(head.Value.position + bodyYaw * ShoulderFromHead, Origin) : 0f;
        }

        // The pinch's reference pose, from which the wrist twist and the reach are measured (see HandGrabAdjust).
        internal HandGrabTracker Tracker { get; } = new HandGrabTracker();
    }
}
