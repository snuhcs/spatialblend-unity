/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#nullable enable

using System.Threading;
using Oculus.Avatar2;
using UnityEngine;
using SpatialBlend;

public class SbTransformInputControlDelegate : OvrAvatarInputControlDelegate
{
    public CAPI.ovrAvatar2ControllerType controllerType = CAPI.ovrAvatar2ControllerType.Invalid;

    public override bool GetInputControlState(out OvrAvatarInputControlState inputControlState)
    {
        inputControlState = default;
        inputControlState.type = controllerType;

        return true;
    }
}

public class SbTransformTrackingDelegate : OvrAvatarInputTrackingDelegate
{
    private SbTransformInputManager _transforms;

    public SbTransformTrackingDelegate(SbTransformInputManager transforms)
    {
        _transforms = transforms;
    }

    public override bool GetRawInputTrackingState(out OvrAvatarInputTrackingState inputTrackingState)
    {
        inputTrackingState = default;

        if (_transforms.SbTransform?.headTransform != null &&
            _transforms.SbTransform.HasHeadObservation)
        {
            inputTrackingState.headsetActive = true;
            inputTrackingState.headset.position = _transforms.SbTransform.headTransform.position;
            inputTrackingState.headset.orientation = _transforms.SbTransform.headTransform.rotation;
        }


        if (_transforms.SbTransform?.leftHandTransform is not null &&
            _transforms.SbTransform.HasLeftHandObservation)
        {
            inputTrackingState.leftController.position = _transforms.SbTransform.leftHandTransform.position;
            inputTrackingState.leftController.orientation = _transforms.SbTransform.leftHandTransform.rotation;
            inputTrackingState.leftControllerActive = true;
            inputTrackingState.leftControllerVisible = _transforms.controllersVisible;
        }
        else
        {
            inputTrackingState.leftControllerActive = false;
        }

        if (_transforms.SbTransform?.rightHandTransform is not null &&
            _transforms.SbTransform.HasRightHandObservation)
        {
            inputTrackingState.rightController.position = _transforms.SbTransform.rightHandTransform.position;
            inputTrackingState.rightController.orientation = _transforms.SbTransform.rightHandTransform.rotation;
            inputTrackingState.rightControllerActive = true;
            inputTrackingState.rightControllerVisible = _transforms.controllersVisible;
        }
        else
        {
            inputTrackingState.rightControllerActive = false;
        }

        return true;
    }
}

// This class assigns Transform data to body tracking system
// so that avatar can be controlled without a headset
public class SbTransformInputManager : OvrAvatarInputManager
{
    public SbRetargetTransform? SbTransform;
    public SbAvatarBehaviourFusion? SbAvatarBehaviourFusion;
    public bool controllersVisible = false;

    /// <summary>
    /// Replaces the retarget-driven tracking and control with another delegate (the real headset/controller/hand path, for a local
    /// avatar that must move with the user: the lobby preview). Set before the avatar's entity is created; null keeps the retarget.
    /// </summary>
    public OvrAvatarInputTrackingDelegate TrackingDelegateOverride;
    public OvrAvatarInputControlDelegate ControlDelegateOverride;

    // This is deliberately separate from the SDK base field.  Its initialization
    // routine resets _bodyTrackingContext for the deprecated provider modes, while
    // this optional context is owned by this manager for its whole attached
    // lifetime.
    private SbArticulatedBodyTrackingContext? _articulatedBodyTrackingContext;
    private bool _articulatedBodyTrackingContextExposed;
    private bool _articulatedBodyTrackingContextNotificationInProgress;
    private bool _articulatedBodyTrackingDestroyed;
    private bool _receiverBodyMode;
    private ReceiverBodySdkBinding? _receiverBodyPublisher;
    // Failure-only test seam. It cannot admit input or fabricate SDK success.
    internal System.Func<bool> TestOnlyRejectReceiverBodyPublish;
    private static int s_articulatedBodyTrackingMainThreadId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void CaptureArticulatedBodyTrackingMainThread()
    {
        Volatile.Write(ref s_articulatedBodyTrackingMainThreadId,
                       Thread.CurrentThread.ManagedThreadId);
    }

#if UNITY_EDITOR
    // Edit Mode tests and inactive staged clones need the same main-thread
    // authority without waiting for a scene object's Awake/OnEnable callback.
    [UnityEditor.InitializeOnLoadMethod]
    private static void CaptureArticulatedBodyTrackingEditorMainThread()
    {
        CaptureArticulatedBodyTrackingMainThread();
    }
#endif

    /// <summary>
    /// The explicitly configured immutable-snapshot context, if it is currently
    /// exposed to AvatarSDK.  It is never synthesized from transform tracking.
    /// </summary>
    public SbArticulatedBodyTrackingContext? ArticulatedBodyTrackingContext =>
        _articulatedBodyTrackingContextExposed ? _articulatedBodyTrackingContext : null;

    // Whole-body receiver ingress has its own explicit input/control state.
    // Never install transform/Fusion/OVR providers alongside it, including
    // while detached or expired. The mode is sticky for this manager lifetime.
    public override OvrAvatarInputControlProviderBase? InputControlProvider =>
        _receiverBodyMode ? null : base.InputControlProvider;
    public override OvrAvatarInputTrackingProviderBase? InputTrackingProvider =>
        _receiverBodyMode ? null : base.InputTrackingProvider;
    public override OvrAvatarHandTrackingPoseProviderBase? HandTrackingProvider =>
        _receiverBodyMode ? null : base.HandTrackingProvider;

    /// <summary>
    /// Returns the optional manager-owned context through the actual AvatarSDK
    /// binding API.  The base initialization can clear its protected context, so
    /// initialize first and then return our independently owned context.
    /// </summary>
    public override OvrAvatarBodyTrackingContextBase? BodyTrackingContext
    {
        get
        {
            if (_receiverBodyMode) return ArticulatedBodyTrackingContext;
            InitializeBodyTracking();
            // A pending explicit detach must report null even if a base SDK mode
            // happens to retain a separate deprecated context.
            return _articulatedBodyTrackingContext != null
                ? ArticulatedBodyTrackingContext
                : _bodyTrackingContext;
        }
    }

    /// <summary>
    /// Creates and freezes one explicit articulated skeleton for this input
    /// manager.  Replacement is intentionally refused until explicit detach.
    /// </summary>
    public bool TryConfigureArticulatedBodyTracking(
        SbArticulatedBodyTrackingContext.SkeletonDefinition skeleton, out string error)
        => TryConfigureArticulatedBodyTracking(skeleton, null, out _, out error);

    internal bool TryConfigureArticulatedBodyTracking(
        SbArticulatedBodyTrackingContext.SkeletonDefinition skeleton, object publisher,
        out SbArticulatedBodyTrackingContext context, out string error)
    {
        context = null;
        error = null;
        if (!IsArticulatedOwnerThread(out error)) return false;
        if (IsArticulatedDestroyed(out error)) return false;
        if (IsArticulatedTransitionInProgress(out error)) return false;
        if (_articulatedBodyTrackingContext != null)
        {
            error = "Detach the existing articulated body-tracking context before configuring another.";
            return false;
        }
        if (_receiverBodyMode && publisher == null)
        {
            error = "A receiver-body manager cannot fall back to unowned articulated input.";
            return false;
        }

        var candidate = new SbArticulatedBodyTrackingContext();
        if (!candidate.TryConfigure(skeleton, out error))
        {
            candidate.Dispose();
            return false;
        }
        if (publisher != null && !candidate.TryClaimPublisher(publisher, out error))
        { candidate.Dispose(); return false; }

        context = candidate; // Also returns exact cleanup ownership on notification failure.
        _receiverBodyPublisher = publisher as ReceiverBodySdkBinding;
        if (publisher != null) _receiverBodyMode = true;
        _articulatedBodyTrackingContext = candidate;
        _articulatedBodyTrackingContextExposed = true;
        if (!TryNotifyBodyTrackingContextChanged(out error))
        {
            // A listener may already have installed this context.  Retain it and
            // fail closed rather than disposing a potentially live callback.  It
            // is hidden immediately, so a false configure result cannot publish.
            _articulatedBodyTrackingContextExposed = false;
            return false;
        }
        return true;
    }

    /// <summary>Publishes a complete caller-supplied articulated body snapshot.</summary>
    public bool TryPublishArticulatedBodyTracking(SbArticulatedBodyTrackingContext.Snapshot snapshot,
                                                  out string error)
        => TryPublishArticulatedBodyTracking(null, null, snapshot, out error);

    internal bool TryPublishArticulatedBodyTracking(object publisher,
        SbArticulatedBodyTrackingContext expected, SbArticulatedBodyTrackingContext.Snapshot snapshot,
        out string error)
    {
        if (!IsArticulatedOwnerThread(out error))
        {
            return false;
        }
        if (IsArticulatedDestroyed(out error))
        {
            return false;
        }
        if (IsArticulatedTransitionInProgress(out error))
        {
            return false;
        }
        if (!TryGetExposedArticulatedContext(out SbArticulatedBodyTrackingContext context,
                                             out error))
        {
            return false;
        }
        if (publisher != null && !ReferenceEquals(context, expected))
        { error = "The receiver body's exact SDK context is no longer installed."; return false; }
        if (publisher != null && TestOnlyRejectReceiverBodyPublish != null && TestOnlyRejectReceiverBodyPublish())
        { error = "Test-only receiver body SDK publication rejection."; return false; }
        return context.TryPublishOwned(publisher, snapshot, out error);
    }

    internal bool CheckReceiverBodyContext(SbArticulatedBodyTrackingContext expected, out string error)
    {
        if (!IsArticulatedOwnerThread(out error) || IsArticulatedDestroyed(out error) ||
            IsArticulatedTransitionInProgress(out error)) return false;
        if (expected == null || !ReferenceEquals(ArticulatedBodyTrackingContext, expected))
        { error = "The receiver body's SDK context is not exposed by this manager."; return false; }
        return true;
    }

    internal bool TryDetachReceiverBodyContext(SbArticulatedBodyTrackingContext expected, out string error)
    {
        if (!IsArticulatedOwnerThread(out error)) return false;
        // Old cleanup must never detach a replacement context.
        return expected == null || !ReferenceEquals(_articulatedBodyTrackingContext, expected) ||
            TryDetachArticulatedBodyTracking(out error);
    }

    /// <summary>Clears articulated body input without resetting its sequence epoch.</summary>
    public bool TryClearArticulatedBodyTracking(out string error)
    {
        if (!IsArticulatedOwnerThread(out error))
        {
            return false;
        }
        if (IsArticulatedDestroyed(out error))
        {
            return false;
        }
        if (IsArticulatedTransitionInProgress(out error))
        {
            return false;
        }
        if (!TryGetExposedArticulatedContext(out SbArticulatedBodyTrackingContext context,
                                             out error))
        {
            return false;
        }
        if (_receiverBodyPublisher != null) return _receiverBodyPublisher.TryClear(out error);
        return context.ClearPose(out error);
    }

    /// <summary>
    /// Detaches the context while this manager is still installed on the entity.
    /// Listeners therefore observe null and can clear their native callback before
    /// the registry entry is disposed.  If a listener throws, the detached context
    /// is retained for an explicit retry rather than being disposed unsafely.
    /// </summary>
    public bool TryDetachArticulatedBodyTracking(out string error)
    {
        error = null;
        if (!IsArticulatedOwnerThread(out error)) return false;
        if (IsArticulatedTransitionInProgress(out error)) return false;
        SbArticulatedBodyTrackingContext context = _articulatedBodyTrackingContext;
        if (context == null)
        {
            return _receiverBodyPublisher == null || _receiverBodyPublisher.TryWithdrawFromManager(null, out error);
        }

        bool nativeWithdrawn = _receiverBodyPublisher == null ||
            _receiverBodyPublisher.TryWithdrawFromManager(context, out error);
        string nativeError = error;

        // Clear on every retry.  Besides removing any pose before the first
        // notification, this enforces the context's owner-thread contract before
        // changing visibility or invoking listeners on a retained pending detach.
        if (!context.ClearPoseOwned(_receiverBodyPublisher, out error))
        {
            return false;
        }

        _articulatedBodyTrackingContextExposed = false;
        if (!TryNotifyBodyTrackingContextChanged(out error))
        {
            return false;
        }

        _articulatedBodyTrackingContext = null;
        context.DisposeAfterDetach();
        _receiverBodyPublisher?.NotifyManagerDetached(context);
        error = nativeError;
        return nativeWithdrawn;
    }

    private bool TryGetExposedArticulatedContext(out SbArticulatedBodyTrackingContext context,
                                                 out string error)
    {
        context = ArticulatedBodyTrackingContext;
        if (context != null)
        {
            error = null;
            return true;
        }
        error = _articulatedBodyTrackingContext == null
            ? "Configure articulated body tracking before publishing or clearing it."
            : "The articulated body-tracking context has been detached and cannot be republished.";
        return false;
    }

    private bool IsArticulatedTransitionInProgress(out string error)
    {
        error = _articulatedBodyTrackingContextNotificationInProgress
            ? "Articulated body-tracking changes cannot reenter a context-change notification."
            : null;
        return error != null;
    }

    private bool IsArticulatedDestroyed(out string error)
    {
        error = _articulatedBodyTrackingDestroyed
            ? "This input manager has been destroyed and cannot configure or publish articulated body tracking."
            : null;
        return error != null;
    }

    private bool IsArticulatedOwnerThread(out string error)
    {
        int mainThreadId = Volatile.Read(ref s_articulatedBodyTrackingMainThreadId);
        if (mainThreadId == 0)
        {
            error = "Articulated body tracking is unavailable until Unity has initialized its main-thread authority.";
            return false;
        }
        error = Thread.CurrentThread.ManagedThreadId == mainThreadId
            ? null
            : "Articulated body-tracking configuration, publication, and teardown must run on Unity's main thread.";
        return error == null;
    }

    private bool TryNotifyBodyTrackingContextChanged(out string error)
    {
        _articulatedBodyTrackingContextNotificationInProgress = true;
        try
        {
            OnBodyTrackingContextContextChanged.Invoke(this);
            error = null;
            return true;
        }
        catch (System.Exception exception)
        {
            error = "A body-tracking context listener failed; the context remains retained for an explicit detach retry. " +
                    exception.Message;
            return false;
        }
        finally
        {
            _articulatedBodyTrackingContextNotificationInProgress = false;
        }
    }

    private void OnDisable()
    {
        // Do not invent a standing/reference pose when the manager stops.  A later
        // publish must use a strictly newer sequence from the frozen context.
        if (_articulatedBodyTrackingContextExposed &&
            !_articulatedBodyTrackingContextNotificationInProgress)
        {
            if (_receiverBodyPublisher != null)
            {
                if (!_receiverBodyPublisher.TryClear(out string error))
                    Debug.LogWarning("Receiver body withdrawal during disable requires cleanup retry: " + error, this);
            }
            else _articulatedBodyTrackingContext?.ClearPose(out _);
        }
    }

    protected override void OnDestroyCalled()
    {
        // The AvatarSDK base only disposes _bodyTrackingContext.  Detach our
        // separate ownership before that happens, preserving the listener/native
        // callback ordering above.
        _articulatedBodyTrackingDestroyed = true;
        if (!TryDetachArticulatedBodyTracking(out string error))
        {
            Debug.LogWarning("Retained articulated body-tracking context during input-manager destruction: " +
                             error, this);
        }
        base.OnDestroyCalled();
    }

    protected override void OnTrackingInitialized()
    {
        OvrAvatarInputTrackingDelegate inputTrackingDelegate = TrackingDelegateOverride ?? new SbTransformTrackingDelegate(this);
        OvrAvatarInputControlDelegate inputControlDelegate = ControlDelegateOverride ?? new SbTransformInputControlDelegate();
        _inputTrackingProvider = new OvrAvatarInputTrackingDelegatedProvider(inputTrackingDelegate);
        _inputControlProvider = new OvrAvatarInputControlDelegatedProvider(inputControlDelegate);
    }
}
