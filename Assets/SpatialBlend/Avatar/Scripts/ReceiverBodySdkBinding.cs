// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Opt-in observation ingress into Core AND AvatarSDK. This publishes the
    /// supplied receiver body, not a realization of Core goals or generated
    /// motion. It does not acquire animated output DOFs or certify contact.
    /// Native submit and SDK publish are ordered, NOT one atomic transaction.
    /// </summary>
    public sealed class ReceiverBodySdkBinding : IDisposable
    {
        private readonly ReceiverBodyInputBinding _input;
        private readonly SbTransformInputManager _manager;
        private readonly Func<long> _clock;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private SbArticulatedBodyTrackingContext _context;
        private int _state; // 0 constructing, 1 ready, 2 terminal cleanup pending, 3 closed
        private bool _nativeClosed, _sdkDetached;

        private ReceiverBodySdkBinding(ReceiverBodyInputBinding input, ReceiverBodySdkProfile profile,
            SbTransformInputManager manager, Func<long> clock)
        { _input = input; Profile = profile; _manager = manager; _clock = clock; }
        public ReceiverBodySdkProfile Profile { get; }
        public bool IsReady => Volatile.Read(ref _state) == 1;
        public bool IsClosed => Volatile.Read(ref _state) == 3;

        /// <summary>
        /// Requires an unused body binding and unconfigured input manager. After
        /// ownership is acquired, a failed bind also returns its cleanup handle;
        /// call TryClose again if IsClosed is false. No failure may abandon a
        /// potentially installed SDK callback registry entry.
        /// </summary>
        public static bool TryBind(ReceiverBodyInputBinding input, ReceiverBodySdkProfile profile,
            SbTransformInputManager manager, out ReceiverBodySdkBinding binding, out string error)
        {
            binding = null; error = null;
            if (input == null || profile == null || ReferenceEquals(manager, null) ||
                !ReferenceEquals(input.Profile, profile.Body))
                return ReceiverInputValidation.Fail("An exact body profile, fresh binding and explicit SDK input manager are required.", out error);
            // Body clock read establishes owner-thread/live registration before
            // touching any Unity manager or creating a native callback registry.
            if (!input.TryReadAcquisitionClock(out _, out error) ||
                !SpatialBlendAPI.TryCreateReceiverCallbackClock(profile.Body.Input, out var clock, out error)) return false;
            var candidate = new ReceiverBodySdkBinding(input, profile, manager, clock);
            if (!input.TryClaimPublisher(candidate, out error)) return false;
            binding = candidate;
            if (!manager.TryConfigureArticulatedBodyTracking(profile.CreateSkeletonDefinition(), candidate,
                out candidate._context, out error) || !manager.CheckReceiverBodyContext(candidate._context, out error))
                return candidate.FailClosed(error, out error);
            Volatile.Write(ref candidate._state, 1);
            return true;
        }

        public bool TrySubmit(ReceiverBodySdkSample sample, out string error)
        {
            if (!Check(out error)) return false;
            if (!_manager.CheckReceiverBodyContext(_context, out error)) return FailClosed(error, out error);
            var frame = sample?.Body;
            if (frame == null || !ReferenceEquals(frame.Profile, Profile.Body))
                return ReceiverInputValidation.Fail("SDK input must carry this exact body's immutable profile.", out error);
            bool rootAvailable = false;
            foreach (var anchor in frame.GeometricInput.Anchors)
                if (anchor.Name == Profile.Body.Input.RootAnchor) rootAvailable = anchor.Available;
            bool wholeInput = rootAvailable && frame.HasAllJointPoses;
            SbArticulatedBodyTrackingContext.Snapshot prepared = null;
            if (wholeInput)
            {
                Func<bool> validity = () =>
                {
                    if (!IsReady || !_input.IsCurrentForCallback(this, frame)) return false;
                    long now = _clock();
                    return now >= frame.AcquisitionUpperNanoseconds && now > 0 &&
                        now < frame.GeometricInput.ValidUntilNanoseconds &&
                        _input.IsCurrentForCallback(this, frame) && IsReady;
                };
                if (!Profile.TryPrepare(sample, validity, out var snapshot, out error) ||
                    !_context.TryPrepareSnapshot(this, snapshot, out prepared, out error)) return false;
            }
            // A valid explicit root/joint loss is authoritative, even though no
            // complete SDK payload can be made. Never reject it to keep old pose.
            if (!_input.TrySubmitOwned(this, frame, out error)) return false;
            bool published = wholeInput
                ? _manager.TryPublishArticulatedBodyTracking(this, _context, prepared, out error)
                : _context.ClearPoseOwned(this, out error);
            if (!published) return FailClosed(error, out error);
            return true;
        }

        public bool TryClear(out string error)
        {
            if (!Check(out error)) return false;
            if (!_manager.CheckReceiverBodyContext(_context, out error)) return FailClosed(error, out error);
            // SDK is withdrawn first. A native-clear failure terminally closes
            // both owners; no preserved body may be joined/admitted afterward.
            if (!_context.ClearPoseOwned(this, out error)) return FailClosed(error, out error);
            if (!_input.TryClearOwned(this, out error)) return FailClosed(error, out error);
            return true;
        }

        public bool TryClose(out string error)
        {
            error = null;
            if (!OwnerThread(out error)) return false;
            if (IsClosed) return true;
            Volatile.Write(ref _state, 2); // Immediately invalidates SDK callbacks, even on failure.
            string nativeError = null, sdkError = null;
            if (!_nativeClosed) _nativeClosed = _input.TryClose(out nativeError);
            if (!_sdkDetached && _manager.TryDetachReceiverBodyContext(_context, out sdkError)) _sdkDetached = true;
            if (!_nativeClosed || !_sdkDetached)
                return ReceiverInputValidation.Fail("Body SDK cleanup pending: " + nativeError + " " + sdkError, out error);
            Volatile.Write(ref _state, 3);
            return true;
        }
        internal bool TryWithdrawFromManager(SbArticulatedBodyTrackingContext context, out string error)
        {
            if (!OwnerThread(out error)) return false;
            if (context != null && !ReferenceEquals(context, _context))
                return ReceiverInputValidation.Fail("Manager withdrawal belongs to another SDK context.", out error);
            if (IsClosed) return true;
            Volatile.Write(ref _state, 2);
            if (!_nativeClosed) _nativeClosed = _input.TryClose(out error);
            if (_nativeClosed && _sdkDetached) Volatile.Write(ref _state, 3);
            return _nativeClosed;
        }
        internal void NotifyManagerDetached(SbArticulatedBodyTrackingContext context)
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread || !ReferenceEquals(context, _context)) return;
            _sdkDetached = true;
            if (_nativeClosed) Volatile.Write(ref _state, 3);
        }
        public void Dispose()
        {
            if (!TryClose(out string error)) throw new InvalidOperationException(error);
        }
        private bool FailClosed(string cause, out string error)
        {
            TryClose(out string cleanup);
            error = cause + (cleanup == null ? "" : " " + cleanup);
            return false;
        }
        private bool OwnerThread(out string error)
        {
            error = Thread.CurrentThread.ManagedThreadId == _ownerThread ? null :
                "Receiver body SDK operations require the Unity owner thread.";
            return error == null;
        }
        private bool Check(out string error)
        {
            if (!OwnerThread(out error)) return false;
            if (!IsReady) return ReceiverInputValidation.Fail("Receiver body SDK binding is not ready; only terminal cleanup may be retried.", out error);
            return true;
        }
    }
}
