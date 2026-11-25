// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Oculus.Avatar2;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using CAPI = Oculus.Avatar2.CAPI;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbArticulatedInputBindingTests
    {
        private GameObject _ownedObject;

        [TearDown]
        public void TearDown()
        {
            if (_ownedObject != null)
            {
                DetachOwnedInputs(_ownedObject);
                Object.DestroyImmediate(_ownedObject);
                _ownedObject = null;
            }
        }

        [Test]
        public void Configure_AttachEventSeesExactOwnedEmptyContextBeforeActivation()
        {
            SbTransformInputManager input = CreateInactiveInput();
            SbArticulatedBodyTrackingContext observed = null;
            bool activeAtNotification = true;
            input.OnBodyTrackingContextContextChanged.AddListener(manager =>
            {
                observed = input.BodyTrackingContext as SbArticulatedBodyTrackingContext;
                activeAtNotification = input.gameObject.activeInHierarchy;
            });

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            Assert.That(observed, Is.SameAs(input.ArticulatedBodyTrackingContext));
            Assert.That(observed.HasPublishedPose, Is.False);
            Assert.That(activeAtNotification, Is.False);
        }

        [Test]
        public void Factory_OptInStagesOriginalPrefabWithFrozenEmptyContext_AndLegacyOverloadStaysTransformOnly()
        {
            SbAvatarEntity original = LoadOriginalPrefab();
            var articulatedParent = new GameObject("articulated-local-parent");
            articulatedParent.SetActive(false);
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(original, articulatedParent.transform,
                    Skeleton(), out SbAvatarEntity clone, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.True, error);
                Assert.That(clone.IsLocalPresetMode, Is.True);
                Assert.That(input.ArticulatedBodyTrackingContext, Is.Not.Null);
                Assert.That(input.BodyTrackingContext, Is.SameAs(input.ArticulatedBodyTrackingContext));
                Assert.That(input.ArticulatedBodyTrackingContext.HasPublishedPose, Is.False);
                Assert.That(input.SbTransform, Is.SameAs(retarget));
                Assert.That(retarget.headTransform, Is.Not.Null);
                Assert.That(retarget.leftHandTransform, Is.Not.Null);
                Assert.That(retarget.rightHandTransform, Is.Not.Null);
            }
            finally
            {
                DetachOwnedInputs(articulatedParent);
                Object.DestroyImmediate(articulatedParent);
            }

            var legacyParent = new GameObject("legacy-local-parent");
            legacyParent.SetActive(false);
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(original, legacyParent.transform,
                    out SbAvatarEntity clone, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.True, error);
                Assert.That(clone.IsLocalPresetMode, Is.True);
                Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);
                Assert.That(input.SbTransform, Is.SameAs(retarget));
                Assert.That(retarget.headTransform, Is.Not.Null);
                Assert.That(retarget.leftHandTransform, Is.Not.Null);
                Assert.That(retarget.rightHandTransform, Is.Not.Null);
            }
            finally
            {
                DetachOwnedInputs(legacyParent);
                Object.DestroyImmediate(legacyParent);
            }
        }

        [Test]
        public void InvalidOrDuplicateConfigure_PreservesExistingOwnerAndRegistryEntry()
        {
            SbTransformInputManager input = CreateInactiveInput();
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            SbArticulatedBodyTrackingContext owned = input.ArticulatedBodyTrackingContext;
            int id = ContextId(owned);
            int registryCount = Registry.Count;

            CAPI.ovrAvatar2Bone[] invalidBones = Bones();
            invalidBones[1].parentBoneIndex = -1;
            Assert.That(input.TryConfigureArticulatedBodyTracking(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                1, new CAPI.ovrAvatar2Vector3f { z = 1f }, invalidBones, Transforms()), out error), Is.False);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.SameAs(owned));
            Assert.That(Registry.Contains(id), Is.True);
            Assert.That(Registry.Count, Is.EqualTo(registryCount));

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out error), Is.False);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.SameAs(owned));
        }

        [Test]
        public void ThrowingAttachListener_HidesRetainedContextAndBlocksPublicationUntilDetachRetry()
        {
            SbTransformInputManager input = CreateInactiveInput();
            SbArticulatedBodyTrackingContext observed = null;
            UnityAction<OvrAvatarInputManagerBehavior> throwingListener = manager =>
            {
                observed = input.BodyTrackingContext as SbArticulatedBodyTrackingContext;
                throw new InvalidOperationException("test listener");
            };
            input.OnBodyTrackingContextContextChanged.AddListener(throwingListener);

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.False);
            Assert.That(observed, Is.Not.Null);
            int id = ContextId(observed);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);
            Assert.That(input.BodyTrackingContext, Is.Null);
            Assert.That(Registry.Contains(id), Is.True,
                "a context with a possibly installed callback must be retained fail-closed");
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(1), out error), Is.False);
            input.OnBodyTrackingContextContextChanged.RemoveListener(throwingListener);
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
            Assert.That(Registry.Contains(id), Is.False);
        }

        [Test]
        public void ContextNotification_FencesNestedMutationsButLeavesGetterVisibleToListener()
        {
            SbTransformInputManager input = CreateInactiveInput();
            SbArticulatedBodyTrackingContext observed = null;
            bool nestedDetach = true;
            bool nestedPublish = true;
            bool nestedConfigure = true;
            input.OnBodyTrackingContextContextChanged.AddListener(manager =>
            {
                observed = input.BodyTrackingContext as SbArticulatedBodyTrackingContext;
                nestedDetach = input.TryDetachArticulatedBodyTracking(out _);
                nestedPublish = input.TryPublishArticulatedBodyTracking(Snapshot(1), out _);
                nestedConfigure = input.TryConfigureArticulatedBodyTracking(Skeleton(), out _);
            });

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            Assert.That(observed, Is.SameAs(input.ArticulatedBodyTrackingContext));
            Assert.That(nestedDetach || nestedPublish || nestedConfigure, Is.False);
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
        }

        [Test]
        public void Detach_ClearsThenPublishesNullWhileRegistryStillExists_BeforeDisposal()
        {
            SbTransformInputManager input = CreateInactiveInput();
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            SbArticulatedBodyTrackingContext context = input.ArticulatedBodyTrackingContext;
            int id = ContextId(context);
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(3), out error), Is.True, error);

            bool observedNull = false;
            bool registryPresent = false;
            bool poseCleared = false;
            input.OnBodyTrackingContextContextChanged.AddListener(manager =>
            {
                if (input.BodyTrackingContext == null)
                {
                    observedNull = true;
                    registryPresent = Registry.Contains(id);
                    poseCleared = !context.HasPublishedPose;
                }
            });
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
            Assert.That(observedNull && registryPresent && poseCleared, Is.True);
            Assert.That(Registry.Contains(id), Is.False);
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(4), out error), Is.False);
        }

        [Test]
        public void PendingDetachRetry_FromWrongThreadPreservesOwnerAndRegistry()
        {
            SbTransformInputManager input = CreateInactiveInput();
            SbArticulatedBodyTrackingContext observed = null;
            UnityAction<OvrAvatarInputManagerBehavior> throwingListener = manager =>
            {
                observed = input.BodyTrackingContext as SbArticulatedBodyTrackingContext;
                throw new InvalidOperationException("test listener");
            };
            input.OnBodyTrackingContextContextChanged.AddListener(throwingListener);
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out _), Is.False);
            input.OnBodyTrackingContextContextChanged.RemoveListener(throwingListener);
            int id = ContextId(observed);

            bool detached = true;
            var worker = new Thread(() => detached = input.TryDetachArticulatedBodyTracking(out _));
            worker.Start();
            Assert.That(worker.Join(5000), Is.True);
            Assert.That(detached, Is.False);
            Assert.That(Registry.Contains(id), Is.True);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);
            Assert.That(input.TryDetachArticulatedBodyTracking(out string error), Is.True, error);
            Assert.That(Registry.Contains(id), Is.False);
        }

        [Test]
        public void WorkerBeforeFirstConfiguration_CannotAllocateContextOrNotify_ThenMainThreadCanConfigure()
        {
            SbTransformInputManager input = CreateInactiveInput();
            int registryBefore = Registry.Count;
            int notifications = 0;
            input.OnBodyTrackingContextContextChanged.AddListener(manager => ++notifications);
            bool configured = true;
            var worker = new Thread(() =>
                configured = input.TryConfigureArticulatedBodyTracking(Skeleton(), out _));
            worker.Start();
            Assert.That(worker.Join(5000), Is.True);
            Assert.That(configured, Is.False);
            Assert.That(Registry.Count, Is.EqualTo(registryBefore));
            Assert.That(notifications, Is.Zero);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Not.Null);
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void ManagedOnDisableCallback_ClearsWithoutResettingSequence_AndReleaseFailurePreservesLocalBinding()
        {
            _ownedObject = new GameObject("local-release-input");
            _ownedObject.SetActive(false);
            var retarget = _ownedObject.AddComponent<SbRetargetTransform>();
            retarget.headTransform = NewChild(_ownedObject.transform, "head");
            retarget.leftHandTransform = NewChild(_ownedObject.transform, "left");
            retarget.rightHandTransform = NewChild(_ownedObject.transform, "right");
            var avatar = _ownedObject.AddComponent<SbAvatarEntity>();
            var input = _ownedObject.AddComponent<SbTransformInputManager>();
            Assert.That(avatar.TryConfigureLocalPreset(input, retarget, 0, out string error), Is.True, error);
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out error), Is.True, error);
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(5), out error), Is.True, error);

            // Edit Mode activation does not prove Unity's Play Mode callback
            // dispatch. Invoke the production callback directly, as the existing
            // avatar lifecycle tests do, to cover its managed clear contract.
            InvokeProductionOnDisable(input);
            Assert.That(input.ArticulatedBodyTrackingContext.HasPublishedPose, Is.False);
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(5), out error), Is.False,
                "disable clearing must retain the publication epoch");

            UnityAction<OvrAvatarInputManagerBehavior> throwingListener = manager =>
                throw new InvalidOperationException("test listener");
            input.OnBodyTrackingContextContextChanged.AddListener(throwingListener);
            Assert.That(avatar.TryReleaseLocalPresetConfiguration(out error), Is.False);
            Assert.That(avatar.IsLocalPresetMode, Is.True);
            Assert.That(input.SbTransform, Is.SameAs(retarget));
            input.OnBodyTrackingContextContextChanged.RemoveListener(throwingListener);
            Assert.That(avatar.TryReleaseLocalPresetConfiguration(out error), Is.True, error);
            Assert.That(avatar.IsLocalPresetMode, Is.False);
            Assert.That(input.SbTransform, Is.Null);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);
        }

        [Test]
        public void FailedFactorySkeletonConfiguration_RetiresTheCandidateRegistryEntry()
        {
            SbAvatarEntity original = LoadOriginalPrefab();
            var parent = new GameObject("failed-articulated-local-parent");
            int before = Registry.Count;
            CAPI.ovrAvatar2Bone[] invalidBones = Bones();
            invalidBones[1].parentBoneIndex = -1;
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(original, parent.transform,
                    new SbArticulatedBodyTrackingContext.SkeletonDefinition(1,
                        new CAPI.ovrAvatar2Vector3f { z = 1f }, invalidBones, Transforms()),
                    out SbAvatarEntity avatar, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.False);
                Assert.That(avatar, Is.Null);
                Assert.That(retarget, Is.Null);
                Assert.That(input, Is.Null);
                Assert.That(Registry.Count, Is.EqualTo(before));
            }
            finally
            {
                DetachOwnedInputs(parent);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Factory_InvalidPresetAfterValidSkeletonAttachment_RetiresItsRegistryContext()
        {
            SbAvatarEntity original = LoadOriginalPrefab();
            var parent = new GameObject("invalid-preset-articulated-local-parent");
            int before = Registry.Count;
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(original, parent.transform, -1,
                    Skeleton(), out SbAvatarEntity avatar, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.False);
                Assert.That(error, Does.Contain("non-negative"));
                Assert.That(avatar, Is.Null);
                Assert.That(retarget, Is.Null);
                Assert.That(input, Is.Null);
                Assert.That(Registry.Count, Is.EqualTo(before),
                    "the valid attached candidate must be detached during failed staged retirement");
            }
            finally
            {
                DetachOwnedInputs(parent);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void ExplicitArticulatedFactoryOverload_RejectsNullSkeletonWithoutCreatingAClone()
        {
            SbAvatarEntity original = LoadOriginalPrefab();
            var parent = new GameObject("null-articulated-local-parent");
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(original, parent.transform, 0, null,
                    out SbAvatarEntity avatar, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.False);
                Assert.That(error, Does.Contain("explicit skeleton"));
                Assert.That(avatar, Is.Null);
                Assert.That(retarget, Is.Null);
                Assert.That(input, Is.Null);
                Assert.That(parent.transform.childCount, Is.Zero);
            }
            finally
            {
                DetachOwnedInputs(parent);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void DestroyedManager_RejectsNewConfigurationAndPublicationButMayStillDetach()
        {
            SbTransformInputManager input = CreateInactiveInput();
            MethodInfo onDestroyCalled = typeof(SbTransformInputManager).GetMethod("OnDestroyCalled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDestroyCalled, Is.Not.Null);
            onDestroyCalled.Invoke(input, null);

            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.False);
            Assert.That(error, Does.Contain("destroyed"));
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(1), out error), Is.False);
            Assert.That(error, Does.Contain("destroyed"));
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
        }

        [Test]
        public void ManagedDestroyCallback_WithThrowingDetachListenerRetainsOnlyPendingCleanup()
        {
            SbTransformInputManager input = CreateInactiveInput();
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            SbArticulatedBodyTrackingContext context = input.ArticulatedBodyTrackingContext;
            int id = ContextId(context);
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(4), out error), Is.True, error);
            UnityAction<OvrAvatarInputManagerBehavior> throwingListener = manager =>
                throw new InvalidOperationException("test listener");
            input.OnBodyTrackingContextContextChanged.AddListener(throwingListener);
            MethodInfo onDestroyCalled = typeof(SbTransformInputManager).GetMethod("OnDestroyCalled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDestroyCalled, Is.Not.Null);
            LogAssert.Expect(LogType.Warning,
                "Retained articulated body-tracking context during input-manager destruction: " +
                "A body-tracking context listener failed; the context remains retained for an explicit detach retry. " +
                "test listener");

            // This invokes the production managed destruction callback directly;
            // it does not claim Unity's inactive-object destruction dispatch.
            onDestroyCalled.Invoke(input, null);

            Assert.That(context.HasPublishedPose, Is.False);
            Assert.That(input.ArticulatedBodyTrackingContext, Is.Null);
            Assert.That(Registry.Contains(id), Is.True);
            Assert.That(input.TryConfigureArticulatedBodyTracking(Skeleton(), out error), Is.False);
            Assert.That(error, Does.Contain("destroyed"));
            Assert.That(input.TryPublishArticulatedBodyTracking(Snapshot(5), out error), Is.False);
            Assert.That(error, Does.Contain("destroyed"));
            input.OnBodyTrackingContextContextChanged.RemoveListener(throwingListener);
            Assert.That(input.TryDetachArticulatedBodyTracking(out error), Is.True, error);
            Assert.That(Registry.Contains(id), Is.False);
        }

        [Test]
        public void DestroyCallback_RetiresOnlyItsOwnContextAndPreservesOtherManagerSnapshot()
        {
            _ownedObject = new GameObject("independent-articulated-inputs");
            _ownedObject.SetActive(false);
            SbTransformInputManager first = NewChild(_ownedObject.transform, "first")
                .gameObject.AddComponent<SbTransformInputManager>();
            SbTransformInputManager second = NewChild(_ownedObject.transform, "second")
                .gameObject.AddComponent<SbTransformInputManager>();
            Assert.That(first.TryConfigureArticulatedBodyTracking(Skeleton(), out string error), Is.True,
                error);
            Assert.That(second.TryConfigureArticulatedBodyTracking(Skeleton(), out error), Is.True, error);
            SbArticulatedBodyTrackingContext firstContext = first.ArticulatedBodyTrackingContext;
            SbArticulatedBodyTrackingContext secondContext = second.ArticulatedBodyTrackingContext;
            int firstId = ContextId(firstContext);
            int secondId = ContextId(secondContext);
            Assert.That(first.TryPublishArticulatedBodyTracking(Snapshot(2), out error), Is.True, error);
            Assert.That(second.TryPublishArticulatedBodyTracking(Snapshot(7), out error), Is.True, error);

            MethodInfo onDestroyCalled = typeof(SbTransformInputManager).GetMethod("OnDestroyCalled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            onDestroyCalled.Invoke(first, null);

            Assert.That(Registry.Contains(firstId), Is.False);
            Assert.That(Registry.Contains(secondId), Is.True);
            Assert.That(second.ArticulatedBodyTrackingContext, Is.SameAs(secondContext));
            Assert.That(secondContext.HasPublishedPose, Is.True);
            Assert.That(PublishedSequence(secondContext), Is.EqualTo(7));
            Assert.That(second.TryPublishArticulatedBodyTracking(Snapshot(8), out error), Is.True, error);
            Assert.That(second.TryDetachArticulatedBodyTracking(out error), Is.True, error);
        }

        private SbTransformInputManager CreateInactiveInput()
        {
            _ownedObject = new GameObject("articulated-input");
            _ownedObject.SetActive(false);
            return _ownedObject.AddComponent<SbTransformInputManager>();
        }

        private static void DetachOwnedInputs(GameObject root)
        {
            if (root == null) return;
            foreach (SbTransformInputManager input in
                     root.GetComponentsInChildren<SbTransformInputManager>(true))
            {
                input.TryDetachArticulatedBodyTracking(out _);
            }
        }

        private static void InvokeProductionOnDisable(SbTransformInputManager input)
        {
            MethodInfo onDisable = typeof(SbTransformInputManager).GetMethod("OnDisable",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDisable, Is.Not.Null);
            onDisable.Invoke(input, null);
        }

        private static SbAvatarEntity LoadOriginalPrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab");
            Assert.That(prefab, Is.Not.Null);
            return prefab.GetComponent<SbAvatarEntity>();
        }

        private static IDictionary Registry => (IDictionary)typeof(OvrAvatarCallbackContextBase)
            .GetField("instanceMap_", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static int ContextId(SbArticulatedBodyTrackingContext context) =>
            (int)typeof(OvrAvatarCallbackContextBase).GetField("id",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);

        private static long PublishedSequence(SbArticulatedBodyTrackingContext context)
        {
            var snapshot = (SbArticulatedBodyTrackingContext.Snapshot)typeof(
                SbArticulatedBodyTrackingContext).GetField("_snapshot",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
            return snapshot.Sequence;
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        private static SbArticulatedBodyTrackingContext.SkeletonDefinition Skeleton() =>
            new SbArticulatedBodyTrackingContext.SkeletonDefinition(1,
                new CAPI.ovrAvatar2Vector3f { z = 1f }, Bones(), Transforms());

        private static CAPI.ovrAvatar2Bone[] Bones() => new[]
        {
            new CAPI.ovrAvatar2Bone { boneId = CAPI.ovrAvatar2JointType.Hips, parentBoneIndex = -1 },
            new CAPI.ovrAvatar2Bone { boneId = CAPI.ovrAvatar2JointType.LeftLegUpper, parentBoneIndex = 0 }
        };

        private static CAPI.ovrAvatar2Transform[] Transforms() => new[] { Transform(), Transform() };

        private static CAPI.ovrAvatar2Transform Transform() =>
            new CAPI.ovrAvatar2Transform(Vector3.zero, Quaternion.identity, Vector3.one);

        private static SbArticulatedBodyTrackingContext.Snapshot Snapshot(long sequence) =>
            new SbArticulatedBodyTrackingContext.Snapshot(sequence,
                CAPI.ovrAvatar2TrackingBodyModality.Sitting,
                new CAPI.ovrAvatar2InputTrackingState(),
                new CAPI.ovrAvatar2InputControlState(),
                CAPI.ovrAvatar2HandInputType.Controller,
                CAPI.ovrAvatar2HandInputType.Controller, 1f, 1f, Transforms());
    }
}
#endif
