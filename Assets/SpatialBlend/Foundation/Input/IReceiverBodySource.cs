// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public enum ReceiverBodySampleStatus { NoChange, Sample, Unavailable }

    /// <summary>
    /// Independent whole-body acquisition, unrelated to an app or transport.
    /// The native floor is sampled BEFORE acquisition, never a timestamp to
    /// stamp onto buffered recordings. No generated output rig is supplied.
    /// NoChange/Unavailable require a null frame; Sample requires an exact
    /// profile-bound frame. Implementations must not reenter controller phases.
    /// </summary>
    public interface IReceiverBodySource
    {
        bool TryAcquire(ReceiverBodyProfile profile, long nativeAcquisitionFloor,
            out ReceiverBodySampleStatus status, out ReceiverBodyFrame frame, out string error);
    }
}
