// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    /// <summary>
    /// Sender hysteresis for avatar state updates.
    /// As v1 <c>SbNetworkAvatarEntity.UpdateStates</c>:
    /// the unchanged-transform gate runs first, then interaction forces the send
    /// and bypasses the velocity hysteresis.
    /// </summary>
    public static class AvatarStateUpdateFilter
    {
        public readonly struct StepResult
        {
            public StepResult(bool positionMoving, bool yawRotating, bool send, bool hysteresisBypassed)
            {
                PositionMoving = positionMoving;
                YawRotating = yawRotating;
                Send = send;
                HysteresisBypassed = hysteresisBypassed;
            }

            public bool PositionMoving { get; }
            public bool YawRotating { get; }
            public bool Send { get; }
            public bool HysteresisBypassed { get; }
        }

        /// <summary>
        /// v1 places the interaction bypass after the attached and hasChanged gates.
        /// Interaction does not open either gate.
        /// </summary>
        public static bool PassesEarlyGate(bool attached, bool transformChanged, bool forceUpdate)
        {
            if (!attached && !forceUpdate)
            {
                return false;
            }

            if (!transformChanged && !forceUpdate)
            {
                return false;
            }

            return true;
        }

        public static StepResult Step(
            bool wasPositionMoving,
            bool wasYawRotating,
            float positionVelocity,
            float yawVelocity,
            float positionStartThreshold,
            float positionStopThreshold,
            float yawStartThreshold,
            float yawStopThreshold,
            bool interacting,
            bool forceUpdate)
        {
            bool positionMoving = NextFlag(wasPositionMoving, positionVelocity, positionStartThreshold, positionStopThreshold);
            bool yawRotating = NextFlag(wasYawRotating, yawVelocity, yawStartThreshold, yawStopThreshold);
            bool hysteresisWouldBlock = !positionMoving && !yawRotating && !forceUpdate;
            bool send = !hysteresisWouldBlock || interacting;
            return new StepResult(positionMoving, yawRotating, send, hysteresisWouldBlock && interacting);
        }

        public static bool NextFlag(bool wasActive, float velocity, float startThreshold, float stopThreshold)
        {
            if (!wasActive && velocity > startThreshold)
            {
                return true;
            }

            if (wasActive && velocity < stopThreshold)
            {
                return false;
            }

            return wasActive;
        }
    }
}
