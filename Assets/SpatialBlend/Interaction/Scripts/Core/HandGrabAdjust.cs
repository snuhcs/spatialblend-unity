// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// The hand's stand-in for the right thumbstick while an object or panel is grabbed with a pinch. The stick turns the object (X) and
    /// pushes or pulls it (Y), as a rate; the hand does the same with two things it can do while pinching without letting go:
    /// <list type="bullet">
    /// <item><b>Wrist twist</b> turns the object: the roll of the wrist about its own wrist-to-knuckles axis since the pinch began, measured against the
    /// horizon: the angle the wrist's up has to the world's up about that axis, so aiming the hand (which swings the axis around the room, even pitched down) is not a twist. Clockwise seen from behind the hand turns the
    /// object clockwise seen from above, like stick right. Meta's hands guidance prefers to "grab and turn objects" over gestures, and a twist of
    /// the held object needs no new gesture and no timing.</item>
    /// <item><b>Reach</b> pushes or pulls: how much farther from the shoulder the hand is than at the pinch. The arm pivots about the shoulder, so sweeping it to
    /// aim, or walking, is not a reach. Past a dead zone the object moves away (hand out) or toward you (hand in) at a rate that grows with the reach.</item>
    /// </list>
    /// Both are measured against the pose at the pinch, so a constant bias of the tracking never matters. The output has the stick's shape (a rate in
    /// -1..1 per axis), so <c>ObjectPlacementMotif.ApplyGrabStick</c> and the panel's push/pull take it unchanged, with the same stronger-axis rule and
    /// speeds. The ramp starts just above the stick's own dead zone (<see cref="StickDeadZone"/>), so the dead zones below are the ones the user feels.
    /// The numbers are first values, not measured on a device.
    /// </summary>
    public static class HandGrabAdjust
    {
        /// <summary>Twist under this many degrees is ignored: a pinch is not perfectly still.</summary>
        public const float TwistDeadZoneDegrees = 12f;
        /// <summary>Twist of this many degrees or more turns at the full rate (90 degrees per second).</summary>
        public const float TwistFullDegrees = 50f;
        /// <summary>Reach under this many metres is ignored: the hand moves to aim, and aiming moves the object by itself.</summary>
        public const float ReachDeadZoneMetres = 0.10f;
        /// <summary>Reach of this many metres or more pushes or pulls at the full rate (2.5 m/s).</summary>
        public const float ReachFullMetres = 0.30f;
        /// <summary>The placement motifs ignore a stick deflection under this (<c>ObjectPlacementMotif.StickDeadZone</c>); a hand's output starts above it.</summary>
        public const float StickDeadZone = 0.25f;
        /// <summary>
        /// Time constant (s) with which the body's yaw, which places the estimated shoulder, follows the head's yaw during a pinch. A glance is shorter
        /// and barely moves it; a turn of the body is longer and carries it along, so after a turn the reach reads right again. While it catches up the
        /// reading is off by up to about 0.13 m for a 60 degree turn (a brief, bounded push or pull): an approximation, first value, not tuned on a device.
        /// </summary>
        public const float HeadYawFollowSeconds = 1f;

        /// <summary>
        /// The signed angle, in degrees (-180..180), of the rotation from <paramref name="start"/> to <paramref name="now"/> about
        /// <paramref name="axis"/> (swing-twist decomposition: the part of the turn that is a roll about the axis). Positive is clockwise seen
        /// along the axis, the direction of a positive rotation about it in Unity.
        /// </summary>
        public static float TwistDegrees(Quaternion start, Quaternion now, Vector3 axis)
        {
            if (axis.sqrMagnitude < 1e-8f) return 0f;
            axis.Normalize();
            Quaternion delta = now * Quaternion.Inverse(start);
            var v = new Vector3(delta.x, delta.y, delta.z);
            Vector3 along = Vector3.Project(v, axis);
            var twist = new Quaternion(along.x, along.y, along.z, delta.w);
            float magnitude = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (magnitude < 1e-6f) return 0f;
            twist = new Quaternion(twist.x / magnitude, twist.y / magnitude, twist.z / magnitude, twist.w / magnitude);
            if (twist.w < 0f) twist = new Quaternion(-twist.x, -twist.y, -twist.z, -twist.w);
            float half = Mathf.Atan2(Vector3.Dot(new Vector3(twist.x, twist.y, twist.z), axis), twist.w);
            return Mathf.DeltaAngle(0f, half * 2f * Mathf.Rad2Deg);
        }

        /// <summary>
        /// Maps a measured value to a stick-like deflection: 0 inside <paramref name="dead"/>, then a linear ramp from just above the stick's dead zone
        /// to +-1 at <paramref name="full"/>.
        /// </summary>
        public static float Ramp(float value, float dead, float full)
        {
            float magnitude = Mathf.Abs(value);
            if (magnitude <= dead) return 0f;
            float t = Mathf.Clamp01((magnitude - dead) / Mathf.Max(1e-6f, full - dead));
            return Mathf.Sign(value) * Mathf.Lerp(StickDeadZone + 0.01f, 1f, t);
        }

        /// <summary>The stick-shaped output for a twist (degrees) and a reach (metres): x turns, y pushes (+) or pulls (-).</summary>
        public static Vector2 Rates(float twistDegrees, float reachMetres) =>
            new Vector2(Ramp(twistDegrees, TwistDeadZoneDegrees, TwistFullDegrees), Ramp(reachMetres, ReachDeadZoneMetres, ReachFullMetres));
    }

    /// <summary>One pinch's reference pose, from which <see cref="HandGrabAdjust"/> measures twist and reach.</summary>
    public sealed class HandGrabTracker
    {
        /// <summary>The wrist-to-knuckles axis must be at least this far from vertical (the horizon-referenced roll is undefined pointing straight up or down): sin of about 11 degrees.</summary>
        public const float MinHorizonProjection = 0.2f;

        private Quaternion _wrist0;
        private Vector3 _axisLocal;
        private Vector3 _referenceLocal;
        private bool _horizon;
        private float _shoulderDistance0;
        private bool _active;

        public bool IsActive => _active;

        /// <summary>The body's yaw as estimated for the shoulder: the head's at the pinch, then following it slowly (see <see cref="HandGrabAdjust.HeadYawFollowSeconds"/>).</summary>
        public Quaternion BodyYaw { get; set; } = Quaternion.identity;
        public float LastSampleTime { get; set; }

        /// <summary>
        /// Starts a pinch. <paramref name="fingerAxisWorld"/> is the wrist-to-knuckles direction (the twist axis; null falls back to the ray),
        /// <paramref name="shoulderDistance"/> the hand's distance from the shoulder now.
        /// </summary>
        public void Begin(Quaternion wristRotation, Vector3? fingerAxisWorld, Vector3 rayForward, float shoulderDistance)
        {
            _wrist0 = wristRotation;
            Vector3 axis = (fingerAxisWorld.HasValue && fingerAxisWorld.Value.sqrMagnitude > 1e-8f ? fingerAxisWorld.Value : rayForward).normalized;
            _axisLocal = Quaternion.Inverse(wristRotation) * axis;
            // The roll is measured against the horizon: the wrist's "up" is the one the world's up has about the axis at the pinch. Aiming the hand
            // (yawing it, even with the fingers pitched down) keeps the two together; only a roll about the axis parts them.
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axis);
            _horizon = up.magnitude >= MinHorizonProjection;
            _referenceLocal = _horizon ? Quaternion.Inverse(wristRotation) * up.normalized : Vector3.zero;
            _shoulderDistance0 = shoulderDistance;
            _active = true;
        }

        public void End() => _active = false;

        /// <summary>The stick-shaped output now; zero when no pinch is being tracked.</summary>
        public Vector2 Sample(Quaternion wristRotation, float shoulderDistance)
        {
            if (!_active) return Vector2.zero;
            return HandGrabAdjust.Rates(RollDegrees(wristRotation), shoulderDistance - _shoulderDistance0);
        }

        /// <summary>The wrist's roll about its own axis since the pinch, in degrees, against the horizon (positive clockwise seen along the axis).</summary>
        public float RollDegrees(Quaternion wristRotation)
        {
            if (!_horizon)
            {
                // Pinched pointing straight up or down: no horizon to measure against, so the plain turn of the wrist about its axis.
                return HandGrabAdjust.TwistDegrees(Quaternion.identity, Quaternion.Inverse(_wrist0) * wristRotation, _axisLocal);
            }
            Vector3 axis = wristRotation * _axisLocal;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (up.magnitude < MinHorizonProjection) return 0f; // pointing straight up or down now: hold
            Vector3 wristUp = Vector3.ProjectOnPlane(wristRotation * _referenceLocal, axis);
            if (wristUp.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(up, wristUp, axis);
        }
    }
}
