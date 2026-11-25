// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The v1 sender filter.
    /// Interaction bypasses velocity hysteresis and nothing else.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarStateUpdateFilterTests
    {
        private const float PositionStart = 0.3f;
        private const float PositionStop = 0.1f;
        private const float YawStart = 10f;
        private const float YawStop = 3f;

        [Test]
        public void UnchangedTransform_StaysGated_EvenWhileInteracting()
        {
            Assert.That(AvatarStateUpdateFilter.PassesEarlyGate(
                attached: true, transformChanged: false, forceUpdate: false), Is.False);
            Assert.That(AvatarStateUpdateFilter.PassesEarlyGate(
                attached: false, transformChanged: true, forceUpdate: false), Is.False);
            Assert.That(AvatarStateUpdateFilter.PassesEarlyGate(
                attached: true, transformChanged: true, forceUpdate: false), Is.True);
            Assert.That(AvatarStateUpdateFilter.PassesEarlyGate(
                attached: false, transformChanged: false, forceUpdate: true), Is.True);
        }

        [Test]
        public void HysteresisIsBypassedExactlyWhileInteracting()
        {
            float[] positionVelocities = { 0f, 0.1f, 0.2f, 0.3f, 0.31f, 1f };
            float[] yawVelocities = { 0f, 3f, 9f, 10f, 11f };
            bool[] flags = { false, true };

            foreach (float positionVelocity in positionVelocities)
            {
                foreach (float yawVelocity in yawVelocities)
                {
                    foreach (bool wasPositionMoving in flags)
                    {
                        foreach (bool wasYawRotating in flags)
                        {
                            foreach (bool forceUpdate in flags)
                            {
                                AvatarStateUpdateFilter.StepResult idle = Step(
                                    wasPositionMoving, wasYawRotating, positionVelocity, yawVelocity,
                                    interacting: false, forceUpdate: forceUpdate);
                                AvatarStateUpdateFilter.StepResult live = Step(
                                    wasPositionMoving, wasYawRotating, positionVelocity, yawVelocity,
                                    interacting: true, forceUpdate: forceUpdate);

                                Assert.That(idle.HysteresisBypassed, Is.False, Describe(positionVelocity, yawVelocity, forceUpdate));
                                if (!idle.PositionMoving && !idle.YawRotating && !forceUpdate)
                                {
                                    Assert.That(idle.Send, Is.False, Describe(positionVelocity, yawVelocity, forceUpdate));
                                    Assert.That(live.Send, Is.True, Describe(positionVelocity, yawVelocity, forceUpdate));
                                    Assert.That(live.HysteresisBypassed, Is.True, Describe(positionVelocity, yawVelocity, forceUpdate));
                                }
                                else
                                {
                                    Assert.That(idle.Send, Is.True, Describe(positionVelocity, yawVelocity, forceUpdate));
                                    Assert.That(live.Send, Is.True, Describe(positionVelocity, yawVelocity, forceUpdate));
                                    Assert.That(live.HysteresisBypassed, Is.False, Describe(positionVelocity, yawVelocity, forceUpdate));
                                }
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void HysteresisBand_HoldsBetweenStopAndStart_UntilInteraction()
        {
            AvatarStateUpdateFilter.StepResult held = Step(
                wasPositionMoving: true, wasYawRotating: false,
                positionVelocity: 0.2f, yawVelocity: 0f,
                interacting: false, forceUpdate: false);
            Assert.That(held.PositionMoving, Is.True);
            Assert.That(held.Send, Is.True);
            Assert.That(held.HysteresisBypassed, Is.False);

            AvatarStateUpdateFilter.StepResult stopped = Step(
                wasPositionMoving: true, wasYawRotating: false,
                positionVelocity: 0.09f, yawVelocity: 0f,
                interacting: false, forceUpdate: false);
            Assert.That(stopped.PositionMoving, Is.False);
            Assert.That(stopped.Send, Is.False);

            AvatarStateUpdateFilter.StepResult forced = Step(
                wasPositionMoving: true, wasYawRotating: false,
                positionVelocity: 0.09f, yawVelocity: 0f,
                interacting: true, forceUpdate: false);
            Assert.That(forced.PositionMoving, Is.False);
            Assert.That(forced.Send, Is.True);
            Assert.That(forced.HysteresisBypassed, Is.True);
        }

        private static AvatarStateUpdateFilter.StepResult Step(
            bool wasPositionMoving,
            bool wasYawRotating,
            float positionVelocity,
            float yawVelocity,
            bool interacting,
            bool forceUpdate)
        {
            return AvatarStateUpdateFilter.Step(
                wasPositionMoving,
                wasYawRotating,
                positionVelocity,
                yawVelocity,
                PositionStart,
                PositionStop,
                YawStart,
                YawStop,
                interacting,
                forceUpdate);
        }

        private static string Describe(float positionVelocity, float yawVelocity, bool forceUpdate)
        {
            return "pos=" + positionVelocity + " yaw=" + yawVelocity + " force=" + forceUpdate;
        }
    }
}
#endif
