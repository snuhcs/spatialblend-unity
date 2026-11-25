// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The embodied driver's declared bounds (<see cref="EmbodiedDriverConfig"/>, including far targets and
    /// lag): v1's prefab caps, no arrival budget, and the checked driver's goals pace at exactly the configured caps under the
    /// kinematic bound (4 m/s^2 acceleration, 4 m/s^2 braking relative to the target's own motion). The
    /// proposal-following kinematics these tests also pinned (EmbodiedDriver.Step / StepAnchor / PlaceRoot / the per-anchor pose
    /// clocks) are removed with the legacy driver frame; the checked pacing is EmbodiedTargetGoal, also covered by
    /// EmbodiedTargetConsumerTests and TargetEventDriverIntegrationTests.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EmbodiedDriverTests
    {
        private const float Dt = 1f / 60f;

        [Test]
        public void TheDefaultBoundsAreTheV1PrefabValues()
        {
            EmbodiedDriverConfig config = EmbodiedDriverConfig.Default;
            Assert.That(config.MaxLinearSpeedMetersPerSecond, Is.EqualTo(2f));
            Assert.That(config.MaxAngularSpeedDegreesPerSecond, Is.EqualTo(150f));
        }

        [Test]
        public void CapsMapAsTheyAreAndNonPositiveOrNonFiniteCapsAreRefused()
        {
            Assert.That(EmbodiedDriverConfig.TryFromCaps(1f, 30f, out EmbodiedDriverConfig config), Is.True);
            Assert.That(config.MaxLinearSpeedMetersPerSecond, Is.EqualTo(1f));
            Assert.That(config.MaxAngularSpeedDegreesPerSecond, Is.EqualTo(30f));
            foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                Assert.That(EmbodiedDriverConfig.TryFromCaps(bad, 30f, out _), Is.False, "linear " + bad);
                Assert.That(EmbodiedDriverConfig.TryFromCaps(1f, bad, out _), Is.False, "angular " + bad);
            }
        }

        [Test]
        public void ThereIsNoArrivalBudgetAnywhereInTheDriverContract()
        {
            Assert.That(typeof(AnchorProposal).GetField("ArrivalBudgetSeconds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public), Is.Null);
            foreach (string path in new[]
            {
                "Assets/SpatialBlend/Foundation/Realization/EmbodiedDriver.cs",
                "Assets/SpatialBlend/Foundation/Realization/EmbodiedTargetConsumer.cs",
            })
            {
                string driver = System.IO.File.ReadAllText(path);
                Assert.That(driver, Does.Not.Contain("FallbackArrivalBudget"), path);
                Assert.That(driver, Does.Not.Contain("arrival_budget"), path);
            }
        }

        // ---------------------------------------------------------------- the pacing of the checked goal (EmbodiedTargetGoal)
        // The checked driver's root and head goals pace by v1's rule (a goal's Set is one batch: the speed is latched once per Set)
        // under the kinematic bound: 4 m/s^2 acceleration, 4 m/s^2 braking relative to the target's own motion.

        private static EmbodiedTargetGoal Goal(Vector3 position, double? yawDegrees = null, EmbodiedDriverConfig? config = null)
        {
            var goal = new EmbodiedTargetGoal(config ?? EmbodiedDriverConfig.Default);
            Set(goal, position, yawDegrees);
            return goal;
        }

        private static void Set(EmbodiedTargetGoal goal, Vector3 position, double? yawDegrees = null)
        {
            TargetChannels channels = TargetChannels.Position | (yawDegrees.HasValue ? TargetChannels.Yaw : TargetChannels.None);
            goal.Set(new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target, default, channels, position,
                (yawDegrees ?? 0d) * System.Math.PI / 180d));
        }

        private static double Degrees(double radians) => radians * 180d / System.Math.PI;

        private static float A => EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared;
        private static float ADec => EmbodiedDriverConfig.MaxLinearDecelerationMetersPerSecondSquared;
        private static float Cap => EmbodiedDriverConfig.Default.MaxLinearSpeedMetersPerSecond;

        [Test]
        public void TheKinematicBoundIsTheOwnersDecision()
        {
            // 4 m/s^2 up and down, the brake relative to the target's own motion; the v1 pace, caps and snap kept.
            Assert.That(EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared, Is.EqualTo(4f));
            Assert.That(EmbodiedDriverConfig.MaxLinearDecelerationMetersPerSecondSquared, Is.EqualTo(4f));
            Assert.That(EmbodiedDriverConfig.PacingBatchDurationSeconds, Is.EqualTo(0.033f), "v1 _batchDuration");
            Assert.That(EmbodiedDriverConfig.SnapPositionMeters, Is.EqualTo(0.01f));
            Assert.That(EmbodiedDriverConfig.TargetVelocityWindowSeconds,
                Is.GreaterThan(EmbodiedDriverConfig.PacingBatchDurationSeconds), "a dense stream's velocity is measured");
        }

        // A stationary far target (v2 latching one distant target): from rest the speed grows by exactly a*dt a step to the 2 m/s cap,
        // reached after cap/a = 0.5 s, then brakes to a stop on the target: no step slows by more than a_dec*dt, nothing overshoots,
        // and after the landing the goal is at rest.
        [Test]
        public void AStationaryFarTargetRampsToTheCapInHalfASecondAndStopsWithoutOvershoot()
        {
            int rampSteps = Mathf.CeilToInt(Cap / (A * Dt) - 1e-4f);
            Assert.That(rampSteps * Dt, Is.EqualTo(0.5f).Within(1e-4f), "0 to 2 m/s at 4 m/s^2 takes 0.5 s");
            foreach (bool root in new[] { true, false })
            {
                EmbodiedTargetGoal goal = Goal(new Vector3(5f, 0.9f, 0f));
                var pose = new Vector3(0f, 0.9f, 0f);
                float previous = 0f;
                bool landed = false;
                for (int i = 1; i <= 600 && !landed; ++i)
                {
                    Vector3 next = goal.StepPosition(pose, Dt, root, null, out _);
                    float speed = (next.x - pose.x) / Dt;
                    Assert.That(next.x, Is.LessThanOrEqualTo(5f + 1e-6f), "never passes the target, step " + i);
                    Assert.That(speed, Is.GreaterThanOrEqualTo(0f), "never backs up, step " + i);
                    Assert.That(speed - previous, Is.LessThanOrEqualTo(A * Dt + 1e-3f), "acceleration bound, step " + i);
                    Assert.That(previous - speed, Is.LessThanOrEqualTo(ADec * Dt + 1e-3f), "deceleration bound, step " + i);
                    if (i <= rampSteps)
                        Assert.That(speed, Is.EqualTo(Mathf.Min(Cap, i * A * Dt)).Within(1e-3f), "the ramp, step " + i);
                    landed = next.x == 5f;
                    previous = speed;
                    pose = next;
                }
                Assert.That(landed, Is.True, "root " + root);
                Assert.That(goal.Speed, Is.EqualTo(0f), "at rest on a target at rest");
                Assert.That(goal.StepPosition(pose, Dt, root, null, out _), Is.EqualTo(pose), "and stays");
            }
        }

        // v1's dense ~30 Hz stream of a source walking at 1.2 m/s: the goal replays it at the source's speed. Each new sample is paced
        // by v1's rule (the distance at the sample over 0.033 s, latched for the batch), the relative brake is inactive (the target's own
        // velocity is measured from its successive positions), so the body is never more than one sample's travel behind: a
        // distance-only brake would trail by v^2 / 2a = 0.18 m.
        [Test]
        public void ADenseStreamIsFollowedAtTheSourceSpeedWithinOneSampleAndNotTrailedByTheBrake()
        {
            const float source = 1.2f, sampleSeconds = 1f / 30f;
            const int framesPerSample = 2;   // 60 Hz frames, 30 Hz samples
            float oneSample = source * sampleSeconds;
            Assert.That(oneSample, Is.LessThan(source * source / (2f * ADec)), "the test separates following from trailing");
            foreach (bool root in new[] { true, false })
            {
                var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
                var pose = new Vector3(0f, 0.9f, 0f);
                float target = 0f, startOfLastSecond = 0f;
                int frames = 4 * 60;
                for (int frame = 0; frame < frames; ++frame)
                {
                    bool sample = frame % framesPerSample == 0;
                    if (sample)
                    {
                        target = source * frame * Dt;
                        Set(goal, new Vector3(target, 0.9f, 0f));
                    }
                    float atSample = target - pose.x;
                    Vector3 next = goal.StepPosition(pose, Dt, root, null, out _);
                    Assert.That(next.x, Is.LessThanOrEqualTo(target + 1e-6f), "never passes the target");
                    if (frame >= 60)   // after the start from rest (1 s)
                    {
                        Assert.That(target - next.x, Is.LessThanOrEqualTo(oneSample + 1e-4f), "within one sample, frame " + frame);
                        Assert.That(goal.TargetVelocity.x, Is.EqualTo(source).Within(1e-3f), "the target's own speed");
                        Assert.That(goal.Speed, Is.GreaterThanOrEqualTo(source - 1e-3f),
                            "sitting on the moving target between samples is not rest, frame " + frame);
                        if (sample)
                            Assert.That(next.x - pose.x, Is.EqualTo(Mathf.Min(atSample,
                                atSample / EmbodiedDriverConfig.PacingBatchDurationSeconds * Dt)).Within(1e-5f),
                                "v1's pace: the distance at the sample over 0.033 s, frame " + frame);
                    }
                    if (frame == frames - 60) startOfLastSecond = next.x;
                    pose = next;
                }
                Assert.That((pose.x - startOfLastSecond) / (59 * Dt), Is.EqualTo(source).Within(0.03f), "at the source's speed");
            }
        }

        // A target that jumps ahead of a body following a stream: the body speeds up at the bounded acceleration to the cap, never
        // passes the target, and is back within one sample of the stream.
        [Test]
        public void ASuddenTargetJumpIsFollowedAtTheBoundedAcceleration()
        {
            const float source = 1.2f, jump = 1f, sampleSeconds = 1f / 30f;
            foreach (bool root in new[] { true, false })
            {
                var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
                var pose = new Vector3(0f, 0.9f, 0f);
                float target = 0f, peak = 0f;
                for (int frame = 0; frame < 5 * 60; ++frame)
                {
                    float t = frame * Dt;
                    if (frame % 2 == 0)
                    {
                        target = source * t + (t >= 2f ? jump : 0f);
                        Set(goal, new Vector3(target, 0.9f, 0f));
                    }
                    float before = goal.Speed;
                    Vector3 next = goal.StepPosition(pose, Dt, root, null, out _);
                    if (frame == 120)
                        Assert.That(goal.TargetVelocity.x, Is.EqualTo(source).Within(1e-3f),
                            "the jump is not a motion: the stream's estimate is held");
                    float speed = (next.x - pose.x) / Dt;
                    Assert.That(next.x, Is.LessThanOrEqualTo(target + 1e-6f), "never passes the target");
                    Assert.That(speed, Is.LessThanOrEqualTo(before + A * Dt + 1e-4f), "the bounded acceleration, frame " + frame);
                    Assert.That(speed, Is.LessThanOrEqualTo(Cap + 1e-4f));
                    if (t >= 2f) peak = Mathf.Max(peak, speed);
                    if (t >= 4f)
                        Assert.That(target - next.x, Is.LessThanOrEqualTo(source * sampleSeconds + 1e-4f), "caught up, frame " + frame);
                    pose = next;
                }
                Assert.That(peak, Is.EqualTo(Cap).Within(1e-4f), "the catch-up reaches the cap");
            }
        }

        // The case: 72 Hz frames, a 30 Hz source that starts walking at 2 m/s^2 to its speed, whose
        // positions arrive half a frame out of phase and up to 10 or 30 ms late (seeded). A 30 Hz stream over 72 Hz frames arrives
        // 2 and 3 frames apart, so one arrival interval reads 0.8 v or 1.2 v (and a 1-frame gap 2.4 v); the velocity is taken across
        // the window instead. The body follows like v1: its mean lag stays under v * (one sample + the jitter + one frame), the
        // time the newest position can be old, and it is never put at rest while it rides the moving target between samples.
        [TestCase(1.5f, 0.010f)]
        [TestCase(1.5f, 0.030f)]
        [TestCase(1.9f, 0.010f)]
        [TestCase(1.9f, 0.030f)]
        public void AJittered30HzStreamAt72HzIsFollowedLikeV1AndNeverResetToRest(float speed, float jitter)
        {
            const float hz = 72f, sampleSeconds = 1f / 30f, walkUp = 2f;
            float dt = 1f / hz;
            float Source(float t)
            {
                float t0 = speed / walkUp;
                return t < t0 ? 0.5f * walkUp * t * t : 0.5f * walkUp * t0 * t0 + speed * (t - t0);
            }
            var random = new System.Random(3);
            var arrivals = new System.Collections.Generic.List<(float at, float position)>();
            for (int k = 0; k < 240; ++k)
                arrivals.Add((k * sampleSeconds + 0.5f * dt + (float)random.NextDouble() * jitter, Source(k * sampleSeconds)));
            arrivals.Sort((a, b) => a.at.CompareTo(b.at));
            float bound = speed * (sampleSeconds + jitter + dt);
            foreach (bool root in new[] { true, false })
            {
                var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
                var pose = new Vector3(0f, 0.9f, 0f);
                int next = 0, counted = 0;
                bool started = false;
                float lagSum = 0f, lagMax = 0f;
                for (int n = 1; n < 7 * hz; ++n)
                {
                    float t = n * dt;
                    while (next < arrivals.Count && arrivals[next].at < t)
                    {
                        Set(goal, new Vector3(arrivals[next].position, 0.9f, 0f));
                        ++next; started = true;
                    }
                    if (!started) continue;
                    pose = goal.StepPosition(pose, dt, root, null, out _);
                    if (t <= 2f) continue;   // past the walk-up
                    float lag = Source(t) - pose.x;
                    lagSum += lag; lagMax = Mathf.Max(lagMax, lag); ++counted;
                    Assert.That(goal.Speed, Is.GreaterThanOrEqualTo(0.8f * speed), "never at rest on the moving target, t " + t);
                    Assert.That(goal.TargetVelocity.x, Is.InRange(0.8f * speed, Cap + 1e-4f), "measured, never zeroed, t " + t);
                }
                Assert.That(lagSum / counted, Is.LessThanOrEqualTo(bound), "mean lag, root " + root);
                Assert.That(lagMax, Is.LessThanOrEqualTo(bound + speed * dt), "max lag, root " + root);
            }
        }

        // A stream sparser than the window (2 Hz) gives no velocity: each position is a target at rest, so the body stops and goes
        // between them, as v1's own pace did.
        [Test]
        public void ATwoHertzStreamIsMeasuredAsAtRest()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            var pose = new Vector3(0f, 0.9f, 0f);
            for (int frame = 0; frame < 4 * 60; ++frame)
            {
                if (frame % 30 == 0) Set(goal, new Vector3(1.5f * frame * Dt, 0.9f, 0f));
                pose = goal.StepPosition(pose, Dt, true, null, out _);
                Assert.That(goal.TargetVelocity, Is.EqualTo(Vector3.zero), "frame " + frame);
            }
        }

        // Time that passed without a step (a refused target frame) still counts: the velocity is the displacement over the real time.
        [Test]
        public void TimeElapsedWithoutAStepCountsForTheTargetsVelocity()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            var pose = new Vector3(0f, 0.9f, 0f);
            Set(goal, new Vector3(0f, 0.9f, 0f));
            pose = goal.StepPosition(pose, Dt, true, null, out _);
            goal.Elapse(Dt);                                   // a refused frame
            Set(goal, new Vector3(0.04f, 0.9f, 0f));
            goal.StepPosition(pose, Dt, true, null, out _);
            Assert.That(goal.TargetVelocity.x, Is.EqualTo(0.04f / (2f * Dt)).Within(1e-4f));
        }

        // There is no turn acceleration: a yaw goal turns at the 150 deg/s cap from its first step, while the position ramps.
        [Test]
        public void TheTurnCapIsUnchangedAndHasNoAcceleration()
        {
            EmbodiedTargetGoal goal = Goal(new Vector3(3f, 0.9f, 0f), 90d);
            Vector3 pose = goal.StepPosition(new Vector3(0f, 0.9f, 0f), Dt, true, null, out _);
            Assert.That(pose.x, Is.EqualTo(A * Dt * Dt).Within(1e-6f), "the position starts from rest");
            Assert.That(Degrees(goal.StepYaw(0d, Dt)),
                Is.EqualTo(EmbodiedDriverConfig.Default.MaxAngularSpeedDegreesPerSecond * Dt).Within(1e-5d), "the yaw at its cap at once");
            // The course heading (no yaw goal) likewise.
            EmbodiedTargetGoal walk = Goal(new Vector3(0f, 0.9f, 3f));
            walk.StepPosition(new Vector3(0f, 0.9f, 0f), Dt, true, null, out double? heading);
            Assert.That(Degrees(walk.StepYaw(System.Math.PI / 2d, Dt, heading)),
                Is.EqualTo(-EmbodiedDriverConfig.Default.MaxAngularSpeedDegreesPerSecond * Dt).Within(1e-5d));
        }

        // A placement (a teleport, a new epoch) or a release puts the goal at rest: the next walk starts from rest again, and the
        // placement's jump is not taken for the target's motion.
        [Test]
        public void APlacementOrAReleaseResetsTheGoalToRest()
        {
            foreach (bool release in new[] { false, true })
            {
                EmbodiedTargetGoal goal = Goal(new Vector3(10f, 0.9f, 0f));
                var pose = new Vector3(0f, 0.9f, 0f);
                for (int i = 0; i < 60; ++i) pose = goal.StepPosition(pose, Dt, true, null, out _);
                Assert.That(goal.Speed, Is.EqualTo(Cap).Within(1e-4f), "walking at the cap");
                if (release) goal.Release(TargetChannels.Position);
                else goal.ResetPace();
                Assert.That(goal.Speed, Is.EqualTo(0f));
                var placed = new Vector3(20f, 0.9f, 0f);
                Set(goal, placed);                              // the placement's goal (the driver sets the placed pose)
                pose = goal.StepPosition(placed, Dt, true, null, out _);
                Assert.That(goal.TargetVelocity, Is.EqualTo(Vector3.zero), "a placement is not motion");
                Set(goal, new Vector3(25f, 0.9f, 0f));          // the next far target
                Vector3 next = goal.StepPosition(pose, Dt, true, null, out _);
                Assert.That(goal.TargetVelocity, Is.EqualTo(Vector3.zero), "nor is a newly latched far target");
                Assert.That(next.x - pose.x, Is.EqualTo(A * Dt * Dt).Within(1e-6f), "starts from rest, release " + release);
            }
        }

        [Test]
        public void YawSpeedIsTheRemainingYawOverTheSameDurationCappedAtTheBound()
        {
            // 1 degree to go over 0.033 s is about 30 deg/s, under the 150 deg/s cap.
            double near = Goal(Vector3.zero, 1d).StepYaw(0d, Dt);
            Assert.That(Degrees(near), Is.EqualTo(1d / EmbodiedDriverConfig.PacingBatchDurationSeconds * Dt).Within(1e-5d));
            // 30 degrees would be 900 deg/s: the cap holds.
            double far = Goal(Vector3.zero, 30d).StepYaw(0d, Dt);
            Assert.That(Degrees(far), Is.EqualTo(150d * Dt).Within(1e-5d));
        }

        [Test]
        public void TheSpeedIsLatchedOncePerRevisionAndNotRecomputedFromTheRemainingDistanceEveryFrame()
        {
            // A 1.2 m/s stream at 30 Hz over 60 Hz frames, past the start from rest: each sample is 0.04 m ahead, so v1's pace is
            // 0.04 / 0.033 = 1.21 m/s (under the bounds: the target moves, so the brake is relative). The frame after a sample
            // keeps that pace: it lands on the target (0.0198 m left, under one 0.0202 m step), where a pace recomputed from the
            // remaining distance would move 0.0198 / 0.033 / 60 = 0.010 m.
            const float source = 1.2f;
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            var pose = new Vector3(0f, 0.9f, 0f);
            float target = 0f, pace = 0f;
            int checkedFrames = 0;
            for (int frame = 0; frame < 180; ++frame)
            {
                bool sample = frame % 2 == 0;
                if (sample)
                {
                    target = source * frame * Dt;
                    Set(goal, new Vector3(target, 0.9f, 0f));
                    pace = (target - pose.x) / EmbodiedDriverConfig.PacingBatchDurationSeconds;
                }
                float remaining = target - pose.x;
                Vector3 next = goal.StepPosition(pose, Dt, true, null, out _);
                if (frame >= 120 && !sample)
                {
                    float recomputed = remaining / EmbodiedDriverConfig.PacingBatchDurationSeconds * Dt;
                    Assert.That(next.x - pose.x, Is.EqualTo(Mathf.Min(remaining, pace * Dt)).Within(1e-5f), "latched, frame " + frame);
                    Assert.That(next.x - pose.x - recomputed, Is.GreaterThan(1e-3f), "not recomputed, frame " + frame);
                    ++checkedFrames;
                }
                pose = next;
            }
            Assert.That(checkedFrames, Is.EqualTo(30));
        }

        [Test]
        public void TheYawSpeedIsLatchedOncePerRevisionToo()
        {
            EmbodiedTargetGoal goal = Goal(Vector3.zero, 2d);
            double first = goal.StepYaw(0d, Dt);
            double perFrame = System.Math.Min(2d / 0.033d, 150d) * Dt; // 1.01 degrees
            Assert.That(Degrees(first), Is.EqualTo(perFrame).Within(1e-4d));
            double second = goal.StepYaw(first, Dt);
            Assert.That(Degrees(first + second), Is.EqualTo(2d).Within(1e-4d), "the same 1.01 degrees a frame, so 2 degrees in two frames");
        }

        [Test]
        public void ATurnNeverOvershootsAndNeverSpeedsUp()
        {
            EmbodiedTargetGoal turn = Goal(Vector3.zero, 90d);
            double yaw = 0d, previousTurn = double.MaxValue;
            for (int i = 0; i < 600; ++i)
            {
                double step = turn.StepYaw(yaw, Dt);
                Assert.That(step, Is.LessThanOrEqualTo(previousTurn + 1e-9d));
                if (step > 0d) previousTurn = step;
                yaw += step;
                Assert.That(Degrees(yaw), Is.LessThanOrEqualTo(90d + 1e-6d));
            }
            Assert.That(Degrees(yaw), Is.EqualTo(90d).Within(1e-6d));
        }

        [Test]
        public void ATargetThatJittersByAMillimetreMovesTheBodyByNoMoreThanTheJitterAndNeverOvershoots()
        {
            foreach (bool root in new[] { true, false })
            {
                var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
                var pose = new Vector3(2f, 0.9f, 0f);
                for (int i = 0; i < 200; ++i)
                {
                    float jitter = (i % 2 == 0 ? 1f : -1f) * 0.001f;
                    Set(goal, new Vector3(2f + jitter, 0.9f, 0f));   // every update is a new request
                    Vector3 next = goal.StepPosition(pose, Dt, root, null, out _);
                    Assert.That(Mathf.Abs(next.x - pose.x), Is.LessThanOrEqualTo(0.002f + 1e-6f), "root " + root);
                    Assert.That(Mathf.Abs(next.x - 2f), Is.LessThanOrEqualTo(0.001f + 1e-6f), "Never leaves the jitter band.");
                    pose = next;
                }
                Assert.That(pose.x, Is.EqualTo(2f).Within(0.0011f));
            }
        }

        // Ported from the proposal-following kinematics to the checked driver's goal: the configured caps, not the defaults, pace a
        // root position goal and a yaw goal.
        [Test]
        public void ConfiguredBoundsAreTheOnesApplied()
        {
            Assert.That(EmbodiedDriverConfig.TryFromCaps(1f, 30f, out EmbodiedDriverConfig slow), Is.True);
            var goal = new EmbodiedTargetGoal(slow);
            goal.Set(new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target, default,
                TargetChannels.Position | TargetChannels.Yaw, new Vector3(10f, 0f, 0f), 90d * System.Math.PI / 180d));
            double yawStep = goal.StepYaw(0d, Dt);
            Assert.That(yawStep * 180d / System.Math.PI, Is.EqualTo(30d * Dt).Within(1e-6d));
            // The position ramps at the acceleration bound to the configured 1 m/s, not the default 2 m/s.
            var position = new Vector3(0f, 0.9f, 0f);
            float speed = 0f;
            for (int i = 0; i < 60; ++i)
            {
                Vector3 next = goal.StepPosition(position, Dt, true, null, out _);
                speed = (next.x - position.x) / Dt;
                position = next;
            }
            Assert.That(speed, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(goal.Speed, Is.EqualTo(1f).Within(1e-4f));
        }
    }
}
#endif
