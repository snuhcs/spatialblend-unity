// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using SpatialBlend;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformArrivalContractTests
    {
        [Test]
        public void ArrivalPose_FlattensTiltedViewerForwardAtHeadHeight()
        {
            bool ok = FreeformCollaborationApp.TryComputeRemoteArrivalPose(
                new Vector3(1f, 1.7f, 3f), new Vector3(0.3f, 0.9f, 0.4f),
                out Vector3 position, out Quaternion rotation);

            Assert.That(ok, Is.True);
            Assert.That(Vector3.Distance(position, new Vector3(2.2f, 1.7f, 4.6f)),
                Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(rotation * Vector3.forward,
                new Vector3(-0.6f, 0f, -0.8f)), Is.LessThan(1e-5f));
        }

        [Test]
        public void ArrivalPose_RejectsNonFiniteAndHandlesVerticalForwardDeterministically()
        {
            Assert.That(FreeformCollaborationApp.TryComputeRemoteArrivalPose(
                new Vector3(float.NaN, 1f, 1f), Vector3.forward, out _, out _), Is.False);

            bool ok = FreeformCollaborationApp.TryComputeRemoteArrivalPose(
                new Vector3(2f, 1.5f, -4f), Vector3.up, out Vector3 position,
                out Quaternion rotation);
            Assert.That(ok, Is.True);
            Assert.That(Vector3.Distance(position, new Vector3(2f, 1.5f, -2f)),
                Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(rotation * Vector3.forward, Vector3.back),
                Is.LessThan(1e-5f));
        }

        [Test]
        public void ArrivalPose_NormalizesHugeFiniteHorizontalForwardWithoutOverflow()
        {
            bool ok = FreeformCollaborationApp.TryComputeRemoteArrivalPose(
                new Vector3(10f, 1.5f, -2f), new Vector3(float.MaxValue, 0f,
                                                          float.MaxValue),
                out Vector3 position, out Quaternion rotation);

            Assert.That(ok, Is.True);
            Assert.That(float.IsInfinity(position.x) || float.IsNaN(position.x) ||
                        float.IsInfinity(position.y) || float.IsNaN(position.y) ||
                        float.IsInfinity(position.z) || float.IsNaN(position.z), Is.False);
            Assert.That(float.IsInfinity(rotation.x) || float.IsNaN(rotation.x) ||
                        float.IsInfinity(rotation.y) || float.IsNaN(rotation.y) ||
                        float.IsInfinity(rotation.z) || float.IsNaN(rotation.z) ||
                        float.IsInfinity(rotation.w) || float.IsNaN(rotation.w), Is.False);
        }

        [Test]
        public void ArrivalCallbacks_AreNotBlockedByMissingRetargetAndAreReleasedOnRemove()
        {
            var appObject = new GameObject("freeform-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<NetworkObject>();
            var remoteObject = new GameObject("remote-object");
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            int avatarEvents = 0;
            int objectEvents = 0;
            System.Action<GameObject, PlayerRef> onAvatar = (_, __) => avatarEvents++;
            System.Action<GameObject> onObject = _ => objectEvents++;
            FreeformCollaborationApp.OnRemoteAvatarAdded += onAvatar;
            FreeformCollaborationApp.OnRemoteObjectAdded += onObject;
            try
            {
                // There is deliberately no MetaAvatarEntity/retarget/viewer setup.
                // This proves only app callback routing; an unspawned NetworkObject
                // is not evidence of a real Fusion/voice identity.
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                Assert.That(avatarEvents, Is.EqualTo(1));

                app.OnObjectAdded(remoteObject, SbApp.OnObjectAddedType.Added);
                app.OnObjectAdded(remoteObject, SbApp.OnObjectAddedType.Added);
                Assert.That(objectEvents, Is.EqualTo(1));

                app.OnRemove(avatar);
                app.OnRemove(remoteObject);
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                app.OnObjectAdded(remoteObject, SbApp.OnObjectAddedType.Added);
                Assert.That(avatarEvents, Is.EqualTo(2));
                Assert.That(objectEvents, Is.EqualTo(2));
            }
            finally
            {
                FreeformCollaborationApp.OnRemoteAvatarAdded -= onAvatar;
                FreeformCollaborationApp.OnRemoteObjectAdded -= onObject;
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(remoteObject);
            }
        }

        [Test]
        public void Remove_DisablesOnlyTheAnchorControllerCreatedForRemoteArrival()
        {
            var appObject = new GameObject("freeform-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<NetworkObject>();
            avatar.AddComponent<SbNetworkEntity>();
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                var controller = avatar.GetComponent<SpatialBlend.Motion.AnchorTargetController>();
                Assert.That(controller, Is.Not.Null);
                controller.ApplyTarget("hand", Vector3.one, 0f, true, 1d, 1d);
                Assert.That(controller.TryGetTarget("hand", out _), Is.True);

                // An already-disabled component will not receive another automatic
                // OnDisable callback. OnRemove must still release its app-owned goal.
                controller.enabled = false;

                app.OnRemove(avatar);

                Assert.That(controller.enabled, Is.False);
                Assert.That(controller.TryGetTarget("hand", out _), Is.False);

                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                Assert.That(controller.enabled, Is.True,
                    "A re-added remote avatar must reclaim its app-owned controller.");
            }
            finally
            {
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void Remove_DoesNotDisableAnExternalAnchorController()
        {
            var appObject = new GameObject("freeform-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<NetworkObject>();
            avatar.AddComponent<SbNetworkEntity>();
            var externalController = avatar.AddComponent<SpatialBlend.Motion.AnchorTargetController>();
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                externalController.ApplyTarget("hand", Vector3.one, 0f, true, 1d, 1d);
                app.OnRemove(avatar);
                Assert.That(externalController.enabled, Is.True);
                Assert.That(externalController.TryGetTarget("hand", out _), Is.True);
                InvokePrivateLifecycleCallback(app, "OnDestroy");
                Assert.That(externalController.TryGetTarget("hand", out _), Is.True,
                    "App teardown must not release an externally owned controller.");
            }
            finally
            {
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void Remove_ReentrantAvatarArrivalRetainsNewControllerTargets()
        {
            var appObject = new GameObject("freeform-reentrant-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<SbLocalEntity>();
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                var controller = avatar.GetComponent<SpatialBlend.Motion.AnchorTargetController>();
                bool readded = false;
                controller.TargetReleased += _ =>
                {
                    if (readded) return;
                    readded = true;
                    app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                    controller.ApplyTarget("hand", Vector3.right, 0f, true, 1d, 2d);
                };
                controller.ApplyTarget("hand", Vector3.zero, 0f, true, 1d, 1d);
                app.OnRemove(avatar);
                Assert.That(readded, Is.True);
                Assert.That(controller.enabled, Is.True);
                Assert.That(controller.TryGetTarget("hand", out var target), Is.True);
                Assert.That(target.UpdatedAt, Is.EqualTo(2d));
            }
            finally
            {
                InvokePrivateLifecycleCallback(app, "OnDestroy");
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void Destroy_ExplicitlyReleasesAlreadyDisabledOwnedController()
        {
            var appObject = new GameObject("freeform-destroy-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<SbLocalEntity>();
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                var controller = avatar.GetComponent<SpatialBlend.Motion.AnchorTargetController>();
                controller.ApplyTarget("hand", Vector3.zero, 0f, true, 1d, 1d);
                controller.enabled = false;
                InvokePrivateLifecycleCallback(app, "OnDestroy");
                Assert.That(controller.TryGetTarget("hand", out _), Is.False);
                Assert.That(controller.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void DisableEnable_ExplicitLifecycleCallbacksRestartOnlyPendingRegistration()
        {
            var appObject = new GameObject("freeform-app");
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            var type = typeof(FreeformCollaborationApp);
            var started = type.GetField("_appStarted",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var pending = type.GetField("_registerAvatarCoroutine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var complete = type.GetField("_avatarRegistrationComplete",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                started.SetValue(app, true);
                // Plain MonoBehaviour instances in this EditMode fixture do not
                // receive Unity lifecycle dispatch merely from enabled toggles.
                // Exercise the production callbacks explicitly.
                InvokePrivateLifecycleCallback(app, "OnDisable");
                InvokePrivateLifecycleCallback(app, "OnEnable");
                Assert.That(pending.GetValue(app), Is.Not.Null);

                InvokePrivateLifecycleCallback(app, "OnDisable");
                Assert.That(pending.GetValue(app), Is.Null);

                complete.SetValue(app, true);
                InvokePrivateLifecycleCallback(app, "OnEnable");
                Assert.That(pending.GetValue(app), Is.Null,
                    "A completed registration must not restart after re-enable.");
            }
            finally
            {
                Object.DestroyImmediate(appObject);
            }
        }

        private static void InvokePrivateLifecycleCallback(MonoBehaviour component, string name)
        {
            var method = component.GetType().GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing production lifecycle callback {name}.");
            method.Invoke(component, null);
        }
    }
}
#endif
