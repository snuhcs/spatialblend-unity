// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Keeps a remote avatar's raw Meta body aligned with the realized root (T2.6, the v1
    /// <c>MetaAvatarEntity.StartContinuousCompensation</c>).
    ///
    /// A remote avatar has two bodies. The Sb avatar follows the realized root, which the core
    /// places (and, while the actor is contained in a shared object, places at the pose mapped
    /// from the source's object-relative pose). The Meta body plays the source's raw avatar
    /// stream, in whatever frame that stream was recorded. Every frame the Meta body is moved so
    /// that its head joint is over the reference head joint: its yaw is turned by the difference
    /// of the two heads' yaw, then its ground-plane position is shifted by the difference of the
    /// two heads' position. Height is left alone, the stream owns it. The raw stream then plays
    /// at the realized root, which is what keeps the avatar's pose relative to the object while
    /// it is contained.
    ///
    /// No Meta SDK type is used, so a test can drive it with plain transforms.
    /// </summary>
    public sealed class ContinuousCompensation
    {
        private Transform _body;
        private Transform _bodyHead;
        private Transform _referenceHead;

        public bool Enabled { get; private set; }

        /// <summary>
        /// Starts compensating. <paramref name="body"/> is the transform that is moved, normally
        /// the Meta avatar entity's. <paramref name="bodyHead"/> is its head joint and
        /// <paramref name="referenceHead"/> the realized avatar's head joint. False, and not
        /// started, when any of the three is missing.
        /// </summary>
        public bool Start(Transform body, Transform bodyHead, Transform referenceHead)
        {
            if (body == null || bodyHead == null || referenceHead == null)
            {
                Stop();
                return false;
            }
            _body = body;
            _bodyHead = bodyHead;
            _referenceHead = referenceHead;
            Enabled = true;
            return true;
        }

        public void Stop()
        {
            Enabled = false;
            _chart = null;
            _referenceInput = null;
            _hasLastExtra = false;
            _body = null;
            _bodyHead = null;
            _referenceHead = null;
        }

        /// <summary>
        /// One frame of compensation, as v1 did in LateUpdate. False when it is stopped or one of
        /// the transforms was destroyed, which also stops it.
        /// </summary>
        public bool Apply()
        {
            if (!Enabled) return false;
            if (_body == null || _bodyHead == null || _referenceHead == null)
            {
                Stop();
                return false;
            }

            // The turn that heads the body's head joint where the reference head joint heads: the signed angle between the ground
            // projections of the same joint axis of both (HeadingDifference), not a difference of Euler y (a joint axis near vertical
            // makes Unity's Euler y jump by half a turn with a few degrees of pitch).
            float yaw;
            if (_referenceInput != null)
            {
                // The face axis in the head joint's frame, read off the reference: the joint follows its input (the headset), so the
                // input's forward in the reference joint's frame is the face; the same axis of the body's head joint is its face (one
                // skeleton). Exact whatever the joint frame, the pitch and the roll.
                Vector3 face = Quaternion.Inverse(_referenceHead.rotation) * (_referenceInput.rotation * Vector3.forward);
                yaw = Mathf.DeltaAngle(FlatYaw(_bodyHead.rotation * face), FlatYaw(_referenceHead.rotation * face));
            }
            else
            {
                yaw = HeadingDifference(_bodyHead.rotation, _referenceHead.rotation);
            }
            Vector3 referencePosition = _referenceHead.position;
            if (_chart != null && _referenceInput != null && _chart(out Vector3 chartPosition, out float chartYaw, out float weight) &&
                weight > 0f)
            {
                // Inside an interaction the raw body replays the source exactly (as v1): its head goes where the head
                // anchor's CHART puts the source head (its own sample through the object), not where the reference head's Facing yaw
                // points. The reference head joint stands to its input (the headset the chart describes) as the raw body's head joint
                // to the source's headset, so the chart turns that relation: the yaw by the chart's yaw less the input's, the joint's
                // offset from the input with it, about the chart's ground point. Blended by the chart's weight.
                float extra = Mathf.DeltaAngle(FlatYaw(_referenceInput.rotation * Vector3.forward), chartYaw);
                // While the share is partial the turn is unwrapped from the previous frame: with the chart about half a turn off the
                // Facing yaw, a degree of jitter would flip the shortest turn from +179 to -179 and swing the body by the share of a
                // full turn. The direction is latched when the blend starts; at full share any equivalent turn is the same pose, so
                // it is wrapped again there (a later blend-out latches afresh).
                if (_hasLastExtra) extra = _lastExtra + Mathf.DeltaAngle(_lastExtra, extra);
                if (weight >= 1f) extra = Mathf.DeltaAngle(0f, extra);
                _lastExtra = extra;
                _hasLastExtra = true;
                Vector3 offset = Quaternion.AngleAxis(extra, Vector3.up) * (_referenceHead.position - _referenceInput.position);
                var chartHead = new Vector3(chartPosition.x + offset.x, referencePosition.y, chartPosition.z + offset.z);
                yaw += extra * weight;
                referencePosition = Vector3.Lerp(referencePosition, chartHead, weight);
            }
            else
            {
                _hasLastExtra = false;
            }
            _body.rotation = Quaternion.AngleAxis(yaw, Vector3.up) * _body.rotation;
            // Read after the rotation: turning the body moves its head joint.
            Vector3 bodyPosition = _bodyHead.position;
            CompensatePosition(_body, referencePosition - bodyPosition);
            return true;
        }

        /// <summary>The head anchor's chart, as <see cref="SbRetargetTransform.TryGetHeadChart"/> gives it: false (or weight 0) for none.</summary>
        public delegate bool ChartReference(out Vector3 headPosition, out float headYawDegrees, out float weight);

        private ChartReference _chart;
        private float _lastExtra;
        private bool _hasLastExtra;
        private Transform _referenceInput;

        /// <summary>
        /// While <paramref name="chart"/> gives a head chart, place the body by it instead of by the reference head joint's own heading
        /// and position (<see cref="Apply"/>). <paramref name="referenceInput"/> is the input the reference head joint follows (the
        /// retarget head: the headset pose, with the Facing yaw). Null for either: the reference head joint alone, as before.
        /// </summary>
        public void UseChartReference(Transform referenceInput, ChartReference chart)
        {
            _referenceInput = referenceInput;
            _chart = chart;
        }

        private static float FlatYaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private static readonly Vector3[] JointAxes = { Vector3.right, Vector3.up, Vector3.forward };

        /// <summary>
        /// The turn (degrees, about world up) that carries the heading of a joint at <paramref name="from"/> onto the heading of the same
        /// joint at <paramref name="to"/>: the signed angle between the ground projections of the joint axis that lies flattest in both.
        /// No axis is assumed to be the face's (the skeleton decides its joint frames) and no Euler decomposition is involved.
        /// </summary>
        public static float HeadingDifference(Quaternion from, Quaternion to)
        {
            Vector3 best = Vector3.forward;
            float flattest = -1f;
            foreach (Vector3 axis in JointAxes)
            {
                Vector3 a = to * axis, b = from * axis;
                float flat = Mathf.Min(a.x * a.x + a.z * a.z, b.x * b.x + b.z * b.z);
                if (flat > flattest) { flattest = flat; best = axis; }
            }
            Vector3 f = from * best, t = to * best;
            f.y = 0f; t.y = 0f;
            if (f.sqrMagnitude < 1e-12f || t.sqrMagnitude < 1e-12f) return 0f;
            return Vector3.SignedAngle(f, t, Vector3.up);
        }

        /// <summary>Adds the yaw component of the offset to the transform's yaw. Pitch and roll stay.</summary>
        public static void CompensateRotation(Transform body, Vector3 rotationOffset)
        {
            Vector3 currentEuler = body.rotation.eulerAngles;
            currentEuler.y += rotationOffset.y;
            body.rotation = Quaternion.Euler(currentEuler);
        }

        /// <summary>Adds the ground-plane (x, z) components of the offset. Height stays.</summary>
        public static void CompensatePosition(Transform body, Vector3 positionOffset)
        {
            Vector3 currentPosition = body.position;
            currentPosition.x += positionOffset.x;
            currentPosition.z += positionOffset.z;
            body.position = currentPosition;
        }
    }
}
