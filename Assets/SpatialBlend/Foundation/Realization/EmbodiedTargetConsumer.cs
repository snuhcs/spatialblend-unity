// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Actor-local event history and native-accepted sample lifetime. No Transform,
    /// snapshot, requested pose or planned increment is an observation here.
    /// </summary>
    internal sealed class EmbodiedTargetConsumer
    {
        internal readonly struct Stamp
        {
            internal readonly ulong Epoch, Sequence;
            internal readonly bool Teleported;
            internal Stamp(ulong epoch, ulong sequence, bool teleported)
            {
                Epoch = epoch; Sequence = sequence; Teleported = teleported;
            }
        }

        internal sealed class AcceptedSample
        {
            internal readonly long Actor;
            internal readonly uint Anchor;
            internal readonly Stamp Stamp;
            internal readonly double UnityYawRadians, NativeYawRadians;
            internal AcceptedSample(long actor, uint anchor, Stamp stamp, double unityYaw, double nativeYaw)
            {
                Actor = actor; Anchor = anchor; Stamp = stamp;
                UnityYawRadians = unityYaw; NativeYawRadians = nativeYaw;
            }
        }

        private sealed class Port
        {
            internal ulong Epoch = 1, Sequence;
            internal bool Teleported, PublishedSinceFence;
            internal AcceptedSample Previous, Current;
        }

        internal enum OperationKind { Target, Placement, Release, Lifecycle, Unsupported }

        /// <summary>Detached before any accepted correspondence may be retired.</summary>
        internal sealed class Operation
        {
            internal readonly OperationKind Kind;
            internal readonly ActorEvent Event;
            internal readonly TargetChannels Channels;
            internal readonly Vector3 Position;
            internal readonly double YawRadians;
            internal readonly AcceptedSample Correspondence;

            internal Operation(OperationKind kind, ActorEvent actorEvent, TargetChannels channels,
                               Vector3 position, double yaw, AcceptedSample correspondence = null)
            {
                Kind = kind;
                // A lifecycle payload must not keep an app/native mutable array alias.
                actorEvent.Payload = actorEvent.Payload == null ? null : (byte[])actorEvent.Payload.Clone();
                Event = actorEvent; Channels = channels; Position = position;
                YawRadians = yaw; Correspondence = correspondence;
            }
        }

        private readonly long _actor;
        private readonly ulong _initialEpoch;
        private readonly Dictionary<uint, Port> _ports = new Dictionary<uint, Port>();
        private readonly List<Operation> _open = new List<Operation>();
        private readonly Queue<Operation> _ready = new Queue<Operation>();
        private readonly List<ActorEventPollFailure> _failures = new List<ActorEventPollFailure>();
        private bool _hasRevision, _closed;
        private ulong _revision;

        internal EmbodiedTargetConsumer(long actor, ulong initialEpoch = 1) { _actor = actor; _initialEpoch = initialEpoch; }
        internal bool DrainFailed { get; private set; }
        internal string Error { get; private set; }
        internal IReadOnlyList<ActorEventPollFailure> Failures => _failures.AsReadOnly();
        internal int PendingCount => _ready.Count;
        internal int RetainedOpenCount => _open.Count;

        private Port Of(uint anchor)
        {
            if (!_ports.TryGetValue(anchor, out Port port))
            {
                port = new Port { Epoch = _initialEpoch }; _ports.Add(anchor, port);
            }
            return port;
        }

        internal bool TryProposePublication(uint anchor, out Stamp stamp)
        {
            stamp = default;
            if (_closed) return false;
            Port port = Of(anchor);
            // A failed drain never resets this gate, even after a later native zero.
            if (port.PublishedSinceFence || port.Sequence == ulong.MaxValue) return false;
            stamp = new Stamp(port.Epoch, port.Sequence + 1, port.Teleported);
            return true;
        }

        internal void AcceptPublication(uint anchor, Stamp proposed, double actualUnityYaw, double submittedNativeYaw)
        {
            if (!TryProposePublication(anchor, out Stamp expected) ||
                proposed.Epoch != expected.Epoch || proposed.Sequence != expected.Sequence ||
                proposed.Teleported != expected.Teleported)
                throw new InvalidOperationException("Publication acknowledgement does not match the proposed stamp.");
            Port port = Of(anchor);
            port.Previous = port.Current;
            port.Current = new AcceptedSample(_actor, anchor, proposed, actualUnityYaw, submittedNativeYaw);
            port.Sequence = proposed.Sequence;
            port.Teleported = false;
            port.PublishedSinceFence = true;
            if (!Finite(actualUnityYaw) || !Finite(submittedNativeYaw))
                Fail("An accepted publication has no finite exact yaw correspondence.");
        }

        internal void Placed(uint anchor)
        {
            Port port = Of(anchor);
            if (port.Epoch == ulong.MaxValue)
            {
                Fail("Pose epoch exhausted."); return;
            }
            // Pending publication state only; the accepted pair is deliberately untouched.
            ++port.Epoch;
            port.Sequence = 0;
            port.Teleported = true;
        }

        internal ulong Epoch(uint anchor) => Of(anchor).Epoch;
        internal AcceptedSample Current(uint anchor) => Of(anchor).Current;
        internal AcceptedSample Previous(uint anchor) => Of(anchor).Previous;

        internal void Fail(string error, ActorEventPollFailure failure = null)
        {
            DrainFailed = true;
            if (Error == null) Error = error;
            if (failure != null) _failures.Add(failure);
        }

        internal void Receive(in ActorEvent actorEvent, bool supported)
        {
            if (_closed) return;
            if (_hasRevision && actorEvent.Revision != _revision)
            {
                if (actorEvent.Revision < _revision)
                    Fail("Actor event revisions regressed within one subscription.");
                if (!DrainFailed) CloseRevision();
            }
            _revision = actorEvent.Revision;
            _hasRevision = true;
            TargetRequest request = actorEvent.TargetRequest;
            if (!supported && actorEvent.Kind != ActorEventKind.SourceException)
            {
                _open.Add(new Operation(OperationKind.Unsupported, actorEvent, request.Channels,
                    default, 0d));
                return;
            }
            if (actorEvent.Kind == ActorEventKind.SourceException)
            {
                _open.Add(new Operation(OperationKind.Lifecycle, actorEvent, TargetChannels.None, default, 0d));
                return;
            }
            if (actorEvent.Kind == ActorEventKind.TeleportRequest)
            {
                _open.Add(new Operation(OperationKind.Placement, actorEvent,
                    TargetChannels.Position | TargetChannels.Yaw, actorEvent.Position,
                    actorEvent.YawDegrees * Math.PI / 180d));
                return;
            }
            if (request.Channels == TargetChannels.None)
            {
                Fail("Typed event has no named channel."); return;
            }
            if (actorEvent.Kind == ActorEventKind.TargetRelease)
            {
                _open.Add(new Operation(OperationKind.Release, actorEvent, request.Channels, default, 0d));
                return;
            }
            double yaw = 0d;
            AcceptedSample correspondence = null;
            if ((request.Channels & TargetChannels.Yaw) != 0)
            {
                if (request.Placement)
                    yaw = (90d - request.TargetYawRadians * 180d / Math.PI) * Math.PI / 180d;
                else
                {
                    Port port = Of(actorEvent.AnchorId);
                    correspondence = Matches(port.Current, request) ? port.Current
                        : Matches(port.Previous, request) ? port.Previous : null;
                    if (correspondence == null)
                    {
                        Fail("Ordinary yaw has no exact retained native-accepted correspondence.");
                        _open.Add(new Operation(OperationKind.Target, actorEvent, request.Channels, request.Position, double.NaN));
                        return;
                    }
                    yaw = correspondence.UnityYawRadians - request.YawTurnRadians;
                }
                if (!Finite(yaw)) { Fail("Continuous yaw goal is not finite."); return; }
            }
            _open.Add(new Operation(request.Placement ? OperationKind.Placement : OperationKind.Target,
                actorEvent, request.Channels, request.Position, yaw, correspondence));
        }

        private bool Matches(AcceptedSample sample, TargetRequest request) => sample != null &&
            sample.Actor == _actor && sample.Stamp.Epoch == request.SampleEpoch &&
            sample.Stamp.Sequence == request.SampleSequence && request.SampleSequence != 0 &&
            sample.NativeYawRadians == request.SampleYawRadians;

        internal void NativeEmpty()
        {
            if (_closed || DrainFailed) return;
            CloseRevision();
            _hasRevision = false;
            foreach (Port port in _ports.Values)
            {
                port.Previous = null;
                port.PublishedSinceFence = false;
            }
        }

        private void CloseRevision()
        {
            // Typed immutable placements supersede the matching lifecycle teleport,
            // not other revisions or anchors. Position and yaw remain named channels.
            var typedPlacements = new HashSet<uint>();
            foreach (Operation operation in _open)
                if (operation.Kind == OperationKind.Placement && operation.Event.Kind != ActorEventKind.TeleportRequest)
                    typedPlacements.Add(operation.Event.AnchorId);
            foreach (Operation operation in _open)
                if (!(operation.Kind == OperationKind.Placement &&
                      operation.Event.Kind == ActorEventKind.TeleportRequest &&
                      typedPlacements.Contains(operation.Event.AnchorId)))
                    _ready.Enqueue(operation);
            _open.Clear();
        }

        internal Operation[] Pending() => _ready.ToArray();
        internal bool IsNext(Operation operation) => _ready.Count > 0 && ReferenceEquals(_ready.Peek(), operation);
        internal void Acknowledge(Operation operation)
        {
            if (!IsNext(operation)) throw new InvalidOperationException("Stale or out-of-order event operation.");
            _ready.Dequeue();
        }

        internal void Close()
        {
            _closed = true;
            _ports.Clear(); _open.Clear(); _ready.Clear();
        }

        internal static double Nearest(double current, double absolute)
        {
            const double tau = 2d * Math.PI;
            double delta = absolute - current;
            delta -= tau * Math.Floor(delta / tau);
            return current + (delta > Math.PI ? delta - tau : delta);
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Independent v1-paced goals under the driver's kinematic bound. Only actual readback reduces their distance.
    /// The speed of a step is <c>min(v1 pace, last speed + a_acc * dt, max(0, v_target_along + sqrt(2 * a_dec * d)))</c>: the v1 pace
    /// (latched per request) replays a dense stream at the source's speed, the acceleration bound starts it from rest, and the brake,
    /// relative to the target's own motion, stops it at a stationary target without trailing a moving one.
    /// </summary>
    internal sealed class EmbodiedTargetGoal
    {
        private readonly EmbodiedDriverConfig _config;
        private readonly List<Vector2> _course = new List<Vector2>();
        internal bool HasPosition { get; private set; }
        internal bool HasYaw { get; private set; }
        internal Vector3 Position { get; private set; }
        internal double YawRadians { get; private set; }
        internal TargetRequest? PositionMetadata { get; private set; }
        internal TargetRequest? YawMetadata { get; private set; }
        private float? _linear;
        private double? _angular, _courseAngular;
        // The speed the last step was paced at (m/s), for the acceleration bound. Kept across a new target (the body does not stop
        // because the target moved); zero after a landing on a target at rest, a release, a placement or a new epoch (ResetPace).
        private float _speed;
        // The target's own velocity, measured from the goal's successive target positions on the goal's own clock (the time the
        // driver has run it, refused frames included): the positions of the last TargetVelocityWindowSeconds, each at the clock of
        // the first step after it arrived (several positions drained in one frame are one), and whether a new one has arrived.
        // The events carry no source time (sb_actor_event_t), so this is arrival time: the velocity is taken across the whole
        // window, not one arrival interval, so that arrival jitter and frame aliasing (a 30 Hz stream over 72 Hz frames arrives
        // 2 and 3 frames apart) average out.
        private const int TargetHistoryCapacity = 64;
        private readonly double[] _targetTimes = new double[TargetHistoryCapacity];
        private readonly Vector3[] _targetPositions = new Vector3[TargetHistoryCapacity];
        private int _targetFirst, _targetCount;
        private Vector3 _targetVelocity;
        private bool _targetMoved;
        private double _clock;
        internal EmbodiedTargetGoal(EmbodiedDriverConfig config) { _config = config; }

        /// <summary>The speed the last step was paced at, m/s (the acceleration bound's state).</summary>
        internal float Speed => _speed;

        /// <summary>The target's measured velocity (zero for a target at rest or a stale measurement).</summary>
        internal Vector3 TargetVelocity => _targetVelocity;

        /// <summary>The goal's own clock, s: the time it has been stepped or elapsed.</summary>
        internal double Clock => _clock;

        /// <summary>Time that passed without a step (a refused or skipped target frame): it still counts for the target's motion.</summary>
        internal void Elapse(float seconds)
        {
            if (seconds > 0f) _clock += seconds;
        }

        internal void Set(EmbodiedTargetConsumer.Operation operation)
        {
            if ((operation.Channels & TargetChannels.Position) != 0)
            {
                Position = operation.Position; HasPosition = true; _linear = null; _courseAngular = null;
                PositionMetadata = operation.Event.TargetRequest;
                _targetMoved = true;
            }
            if ((operation.Channels & TargetChannels.Yaw) != 0)
            {
                YawRadians = operation.YawRadians; HasYaw = true; _angular = null;
                YawMetadata = operation.Event.TargetRequest;
            }
        }

        /// <summary>
        /// The anchor was placed (a placement, a teleport, a new epoch): it starts from rest, and the target's motion is measured
        /// afresh from the next position (a placement is not motion). The latched linear pace is taken afresh too; the yaw pacing,
        /// which has no acceleration, is untouched.
        /// </summary>
        internal void ResetPace()
        {
            _linear = null; _speed = 0f; _trackedRevision = null; ResetTargetMotion();
        }

        // A goal whose target the driver composes every frame (the head chart's yaw inside a transfer leaf, option A). The same pacing
        // rule as a Set goal (v1 pace latched per request, the acceleration bound, the brake relative to the target's own motion): the
        // composed target's successive positions are its motion, and a new request revision re-latches the pace as a Set would.
        private ulong? _trackedRevision;

        internal void Track(Vector3 position, double? yawRadians, ulong revision)
        {
            if (_trackedRevision != revision)
            {
                _linear = null; _angular = null; _trackedRevision = revision;
            }
            if (!HasPosition || position != Position) _targetMoved = true;
            Position = position; HasPosition = true;
            if (yawRadians.HasValue) { YawRadians = yawRadians.Value; HasYaw = true; }
            else { HasYaw = false; _angular = null; }
        }

        private void ResetTargetMotion()
        {
            _targetFirst = 0; _targetCount = 0; _targetMoved = false; _targetVelocity = Vector3.zero;
        }

        /// <summary>The yaw goal re-based to the nearest turn of <paramref name="actual"/>, its pace taken afresh (after a period in which
        /// another rule turned the anchor, e.g. a head chart): the goal never unwinds turns it did not make.</summary>
        internal void RebaseYaw(double actual)
        {
            if (!HasYaw) return;
            YawRadians = EmbodiedTargetConsumer.Nearest(actual, YawRadians); _angular = null;
        }

        internal void Release(TargetChannels channels)
        {
            if ((channels & TargetChannels.Position) != 0)
            {
                HasPosition = false; _linear = null; _courseAngular = null; PositionMetadata = null;
                _speed = 0f; ResetTargetMotion();
            }
            if ((channels & TargetChannels.Yaw) != 0)
            {
                HasYaw = false; _angular = null; YawMetadata = null;
            }
        }

        internal Vector3 StepPosition(Vector3 actual, float dt, bool root, IEmbodiedCourse course,
                                      out double? courseYaw)
        {
            courseYaw = null;
            if (!HasPosition) return actual;
            MeasureTargetMotion();
            if (!root)
            {
                Vector3 toGoal = Position - actual;
                float distance = toGoal.magnitude;
                if (!_linear.HasValue) _linear = Linear(distance);
                // On the target the body moves with it: its own speed (there is no direction to it).
                float along = distance > 0f ? Vector3.Dot(_targetVelocity, toGoal / distance) : _targetVelocity.magnitude;
                Vector3 next = Vector3.MoveTowards(actual, Position, Bounded(_linear.Value, distance, along, dt) * dt);
                _clock += dt;
                return next;
            }
            var at = new Vector2(actual.x, actual.z);
            var goal = new Vector2(Position.x, Position.z);
            _course.Clear();
            if (course == null || !course.TryQueryCourse(at, goal, _course) || _course.Count == 0)
            {
                _course.Clear(); _course.Add(goal);
            }
            float remaining = 0f;
            Vector2 previous = at, heading = Vector2.zero;
            foreach (Vector2 point in _course)
            {
                Vector2 leg = point - previous;
                if (!courseYaw.HasValue && leg.sqrMagnitude > 1e-10f)
                {
                    courseYaw = Math.Atan2(leg.x, leg.y);
                    heading = leg.normalized;
                }
                remaining += leg.magnitude;
                previous = point;
            }
            // The motion heading is only a heading while the body still has somewhere to go: inside v1's snap distance
            // (POSITION_SNAP_THRESHOLD, 0.01 m) the residual is noise (a hold re-proposing the sampled position, a frame-old
            // sample) whose direction is arbitrary, and following it flipped the body round on arrival and on every hold.
            // There the yaw is kept.
            if (remaining <= EmbodiedDriverConfig.SnapPositionMeters) courseYaw = null;
            if (!_linear.HasValue) _linear = Linear(remaining);
            // The root brakes against the target's motion along its first leg (the course is straight on the checked path).
            // On the target (no first leg) the body moves with it: its own speed.
            var horizontal = new Vector2(_targetVelocity.x, _targetVelocity.z);
            float targetAlong = heading != Vector2.zero ? Vector2.Dot(horizontal, heading) : horizontal.magnitude;
            float distanceLeft = Bounded(_linear.Value, remaining, targetAlong, dt) * dt;
            foreach (Vector2 point in _course)
            {
                float leg = Vector2.Distance(at, point);
                at = Vector2.MoveTowards(at, point, distanceLeft);
                distanceLeft -= Math.Min(leg, distanceLeft);
            }
            _clock += dt;
            return new Vector3(at.x, actual.y, at.y);
        }

        // The target's velocity from its successive positions: the displacement across the window (from the oldest position within
        // TargetVelocityWindowSeconds to the newest) over the time between them, clamped to the speed cap. A displacement faster than
        // twice the cap is a jump (a newly latched far target, a correction), not a motion: the last estimate is held and the
        // measurement restarts from the jumped-to position. A target with no other position within the window, or none renewed
        // within it (a target held, a stream that stopped, a 2 Hz stream), is at rest.
        private void MeasureTargetMotion()
        {
            double window = EmbodiedDriverConfig.TargetVelocityWindowSeconds;
            float cap = _config.MaxLinearSpeedMetersPerSecond;
            if (_targetMoved)
            {
                _targetMoved = false;
                if (_targetCount == TargetHistoryCapacity) { _targetFirst = (_targetFirst + 1) % TargetHistoryCapacity; --_targetCount; }
                int newest = (_targetFirst + _targetCount) % TargetHistoryCapacity;
                _targetTimes[newest] = _clock; _targetPositions[newest] = Position; ++_targetCount;
                while (_targetCount > 1 && _clock - _targetTimes[_targetFirst] > window)
                {
                    _targetFirst = (_targetFirst + 1) % TargetHistoryCapacity; --_targetCount;
                }
                double span = _clock - _targetTimes[_targetFirst];
                if (_targetCount < 2 || !(span > 0d))
                {
                    _targetVelocity = Vector3.zero;
                    return;
                }
                Vector3 velocity = (Position - _targetPositions[_targetFirst]) / (float)span;
                if (velocity.magnitude > 2f * cap)
                {
                    _targetFirst = newest; _targetCount = 1;   // a jump: hold the estimate, measure afresh from here
                    return;
                }
                _targetVelocity = Vector3.ClampMagnitude(velocity, cap);
            }
            else if (_targetCount > 0 &&
                     _clock - _targetTimes[(_targetFirst + _targetCount - 1) % TargetHistoryCapacity] > window)
                _targetVelocity = Vector3.zero;
        }

        // One step's speed: the v1 pace, no faster than the last speed plus a_acc * dt, and no faster than lets it brake at a_dec to
        // the target's own speed along the way at the target. The brake is the stepped one: speeds v, v - a dt, v - 2 a dt, ... cover
        // v (v + a dt) / 2a, so v = sqrt(2 a d + (a dt / 2)^2) - a dt / 2 stops within d with no step slowing by more than a dt (the
        // continuous sqrt(2 a d) would brake harder than a in the last steps). A step never passes the target; a step that lands on
        // it leaves the body at the target's own speed along (zero for a target at rest), so the next step accelerates from there.
        private float Bounded(float pacing, float remaining, float targetAlong, float dt)
        {
            if (!(dt > 0f)) return 0f;
            float half = EmbodiedDriverConfig.MaxLinearDecelerationMetersPerSecondSquared * dt * 0.5f;
            float brake = Math.Max(0f, targetAlong + (float)Math.Sqrt(
                2f * EmbodiedDriverConfig.MaxLinearDecelerationMetersPerSecondSquared * remaining + half * half) - half);
            float speed = Math.Min(pacing, Math.Min(
                _speed + EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared * dt, brake));
            _speed = speed * dt >= remaining ? Math.Min(speed, Math.Max(0f, targetAlong)) : speed;
            return speed;
        }

        internal double StepYaw(double actual, float dt, double? courseYaw = null)
        {
            if (!HasYaw && !courseYaw.HasValue) return 0d;
            double target = HasYaw ? YawRadians : EmbodiedTargetConsumer.Nearest(actual, courseYaw.Value);
            double remaining = target - actual;
            double cap = _config.MaxAngularSpeedDegreesPerSecond * Math.PI / 180d;
            double fresh = Math.Min(Math.Abs(remaining) / EmbodiedDriverConfig.PacingBatchDurationSeconds, cap);
            if (HasYaw && !_angular.HasValue) _angular = fresh;
            if (!HasYaw && !_courseAngular.HasValue) _courseAngular = fresh;
            double distance = (HasYaw ? _angular.Value : _courseAngular.Value) * dt;
            return Math.Sign(remaining) * Math.Min(Math.Abs(remaining), distance);
        }

        private float Linear(float distance) => Math.Min(distance / EmbodiedDriverConfig.PacingBatchDurationSeconds,
            _config.MaxLinearSpeedMetersPerSecond);
    }
}
