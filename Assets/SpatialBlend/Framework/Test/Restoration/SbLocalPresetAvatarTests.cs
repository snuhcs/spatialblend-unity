// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Oculus.Avatar2;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbLocalPresetAvatarTests
    {
        private SbTransformInputManager _input;
        private GameObject _firstObject;
        private SbAvatarEntity _first;
        private SbRetargetTransform _firstRetarget;

        [SetUp]
        public void SetUp()
        {
            (_firstObject, _first, _firstRetarget) = CreateInactiveAvatar("first");
            _input = _firstObject.AddComponent<SbTransformInputManager>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_firstObject);
        }

        [Test]
        public void ExplicitConfiguration_RejectsCrossOwnerInputWithoutMutation()
        {
            Assert.That(_first.TryConfigureLocalPreset(_input, _firstRetarget, 0, out string error), Is.True,
                error);
            var secondTuple = CreateInactiveAvatar("second");
            try
            {
                Assert.That(secondTuple.entity.TryConfigureLocalPreset(_input, secondTuple.retarget, 1,
                    out error), Is.False);
                Assert.That(error, Does.Contain("this avatar instance"));
                Assert.That(_input.SbTransform, Is.SameAs(_firstRetarget));
                Assert.That(secondTuple.entity.IsLocalPresetMode, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(secondTuple.gameObject);
            }
        }

        [Test]
        public void FailedConfiguration_DoesNotEnterLocalMode_AndReleasePermitsRetry()
        {
            var wrongObject = new GameObject("wrong-retarget");
            wrongObject.SetActive(false);
            var wrong = wrongObject.AddComponent<SbRetargetTransform>();
            try
            {
                Assert.That(_first.TryConfigureLocalPreset(_input, wrong, 0, out string error), Is.False);
                Assert.That(_first.IsLocalPresetMode, Is.False);
                Assert.That(_input.SbTransform, Is.Null);

                Assert.That(_first.TryConfigureLocalPreset(_input, _firstRetarget, 2, out error), Is.True,
                    error);
                Assert.That(_first.CurrentLocalPresetStatus,
                    Is.EqualTo(_first.IsLocalPresetRuntimeAvailable
                        ? SbAvatarEntity.LocalPresetStatus.Configured
                        : SbAvatarEntity.LocalPresetStatus.Unavailable));
                Assert.That(_first.TryReleaseLocalPresetConfiguration(out error), Is.True, error);
                Assert.That(_input.SbTransform, Is.Null);
                Assert.That(_first.TryConfigureLocalPreset(_input, _firstRetarget, 3, out error), Is.True,
                    error);
            }
            finally
            {
                Object.DestroyImmediate(wrongObject);
            }
        }

        [Test]
        public void LocalAvailability_IsExplicitWithoutPretendingMacCanInitializeNativeAvatar()
        {
            Assert.That(SbAvatarEntity.IsLocalPresetPlatformSupported(RuntimePlatform.OSXEditor), Is.False);
            Assert.That(SbAvatarEntity.IsLocalPresetPlatformSupported(RuntimePlatform.OSXPlayer), Is.False);
            Assert.That(SbAvatarEntity.IsLocalPresetPlatformSupported(RuntimePlatform.WindowsEditor), Is.True);
            Assert.That(SbAvatarEntity.IsLocalPresetPlatformSupported(RuntimePlatform.Android), Is.True);
            if (!_first.IsLocalPresetRuntimeAvailable)
            {
                Assert.That(_first.LocalPresetAvailabilityMessage, Does.Contain("transform retarget"));
            }
            Assert.That(_first.IsLocalPresetMode, Is.False,
                "the original prefab component remains legacy until explicitly configured");
        }

        [Test]
        public void Factory_StagesOriginalPrefabAndReturnsSameInstanceInputAndRetarget()
        {
            var originalPrefab = LoadOriginalPrefab();
            var owner = new GameObject("owned-local-avatar-parent");
            owner.SetActive(false);
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(originalPrefab, owner.transform,
                    out SbAvatarEntity clone, out SbRetargetTransform retarget,
                    out SbTransformInputManager input, out string error), Is.True, error);
                Assert.That(clone, Is.Not.SameAs(originalPrefab));
                Assert.That(clone.IsLocalPresetMode, Is.True);
                Assert.That(clone.LocalPresetRetarget, Is.SameAs(retarget));
                Assert.That(clone.LocalPresetInput, Is.SameAs(input));
                Assert.That(input.gameObject, Is.SameAs(clone.gameObject));
                Assert.That(input.SbTransform, Is.SameAs(retarget));
                foreach (OvrAvatarLipSyncContext lipSync in
                         clone.GetComponentsInChildren<OvrAvatarLipSyncContext>(true))
                {
                    Assert.That(lipSync.enabled, Is.False,
                        "local factory must not activate the legacy voice/microphone path");
                }
                Assert.That(clone.TryReleaseLocalPresetConfiguration(out error), Is.True, error);
                Assert.That(input.SbTransform, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void FactoryFailure_UsesImmediateEditModeRetirementWithoutStagingLeak()
        {
            var originalPrefab = LoadOriginalPrefab();
            var activeOwner = new GameObject("active-local-avatar-parent");
            int stagingBefore = CountStagingRoots();
            try
            {
                Assert.That(SbLocalPresetAvatarFactory.TryCreate(originalPrefab,
                    activeOwner.transform, -1, out SbAvatarEntity avatar,
                    out SbRetargetTransform retarget, out SbTransformInputManager input,
                    out string error), Is.False);
                Assert.That(error, Does.Contain("non-negative"));
                Assert.That(avatar, Is.Null);
                Assert.That(retarget, Is.Null);
                Assert.That(input, Is.Null);
                Assert.That(activeOwner.transform.childCount, Is.Zero);
                Assert.That(CountStagingRoots(), Is.EqualTo(stagingBefore));
            }
            finally
            {
                Object.DestroyImmediate(activeOwner);
            }
        }

        [Test]
        public void UnsupportedRuntime_AwakeAndStartRemainLocalAndDoNotCreateNativeEntity()
        {
            if (SbAvatarEntity.IsLocalPresetPlatformSupported(Application.platform))
            {
                Assert.Ignore("This assertion is specific to an unsupported Avatar SDK runtime.");
            }

            Assert.That(_first.TryConfigureLocalPreset(_input, _firstRetarget, 0, out string error), Is.True,
                error);
            _firstObject.SetActive(true);
            try
            {
                RunProductionAwake(_first);
                RunProductionStart(_first);
                Assert.That(_first.CurrentLocalPresetStatus,
                    Is.EqualTo(SbAvatarEntity.LocalPresetStatus.Unavailable));
                Assert.That(_first.LocalPresetAvailabilityMessage, Does.Contain("transform retarget"));
                Assert.That(_first.IsCreated, Is.False);
                Assert.That(_first.IsLocalPresetMode, Is.True);
                Assert.That(_first.TryReleaseLocalPresetConfiguration(out error), Is.False,
                    "an active unavailable instance must remain fail-closed rather than reverting to legacy startup");
                Assert.That(_first.IsLocalPresetMode, Is.True);
            }
            finally
            {
                _firstObject.SetActive(false);
            }
        }

        [Test]
        public void StartValidationFailure_RemainsFailClosedWhenSerializedBindingIsChanged()
        {
            if (SbAvatarEntity.IsLocalPresetPlatformSupported(Application.platform))
            {
                Assert.Ignore("This assertion is specific to an unsupported Avatar SDK runtime.");
            }

            Assert.That(_first.TryConfigureLocalPreset(_input, _firstRetarget, 0, out string error), Is.True,
                error);
            _input.SbTransform = null; // Simulates an Inspector/other-owner mutation after staging.
            _firstObject.SetActive(true);
            try
            {
                RunProductionAwake(_first);
                RunProductionStart(_first);
                Assert.That(_first.CurrentLocalPresetStatus,
                    Is.EqualTo(SbAvatarEntity.LocalPresetStatus.Failed));
                Assert.That(_first.IsLocalPresetMode, Is.True);
                Assert.That(_first.IsCreated, Is.False);
            }
            finally
            {
                _firstObject.SetActive(false);
            }
        }

        private static (GameObject gameObject, SbAvatarEntity entity,
                        SbRetargetTransform retarget) CreateInactiveAvatar(string name)
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            var retarget = gameObject.AddComponent<SbRetargetTransform>();
            retarget.headTransform = NewChild(gameObject.transform, "head");
            retarget.leftHandTransform = NewChild(gameObject.transform, "left-hand");
            retarget.rightHandTransform = NewChild(gameObject.transform, "right-hand");
            var entity = gameObject.AddComponent<SbAvatarEntity>();
            return (gameObject, entity, retarget);
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        private static SbAvatarEntity LoadOriginalPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab");
            Assert.That(source, Is.Not.Null);
            var originalPrefab = source.GetComponent<SbAvatarEntity>();
            Assert.That(originalPrefab, Is.Not.Null);
            return originalPrefab;
        }

        private static int CountStagingRoots()
        {
            int count = 0;
            foreach (GameObject gameObject in Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (gameObject.name == "SbLocalPresetAvatarStaging")
                {
                    ++count;
                }
            }
            return count;
        }

        private static void RunProductionStart(SbAvatarEntity entity)
        {
            MethodInfo start = typeof(SbAvatarEntity).GetMethod("Start",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(start, Is.Not.Null);
            RunToCompletion((IEnumerator)start.Invoke(entity, null));
        }

        private static void RunProductionAwake(SbAvatarEntity entity)
        {
            MethodInfo awake = typeof(SbAvatarEntity).GetMethod("Awake",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null);
            awake.Invoke(entity, null);
        }

        private static void RunToCompletion(IEnumerator routine)
        {
            while (routine.MoveNext())
            {
                if (routine.Current is IEnumerator child)
                {
                    RunToCompletion(child);
                }
            }
        }

    }

    [TestFixture]
    [Category("NativeManagerIntegration")]
    public sealed class SbLocalPresetAvatarPlayModeTests
    {
        private GameObject _ownedParent;

        [UnityTest]
        public IEnumerator FailedStagedCloneNeverActivatesLegacyAvatarInPlayMode()
        {
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying, Is.True);

            SbAvatarEntity originalPrefab = LoadOriginalPrefab();
            Assert.That(originalPrefab.gameObject.activeSelf, Is.True,
                "the original authored prefab must exercise the activeSelf staging escape path");
            _ownedParent = new GameObject("active-playmode-local-avatar-parent");
            int stagingBefore = CountStagingRoots();
            var existingAvatarIds = SnapshotAvatarIds();

            Assert.That(SbLocalPresetAvatarFactory.TryCreate(originalPrefab, _ownedParent.transform, -1,
                out SbAvatarEntity avatar, out SbRetargetTransform retarget,
                out SbTransformInputManager input, out string error), Is.False);
            Assert.That(error, Does.Contain("non-negative"));
            Assert.That(avatar, Is.Null);
            Assert.That(retarget, Is.Null);
            Assert.That(input, Is.Null);

            List<SbAvatarEntity> retiredClones = NewSceneAvatarsSince(existingAvatarIds);
            Assert.That(retiredClones, Has.Count.EqualTo(1),
                "the deferred-destroy clone must remain inspectable until the next Play Mode frame");
            SbAvatarEntity retiredClone = retiredClones[0];
            Assert.That(retiredClone.gameObject.activeInHierarchy, Is.False,
                "a failed staged clone must remain inactive before deferred destruction");
            Assert.That(retiredClone.IsCreated, Is.False,
                "an inactive failed clone must not enter the legacy AvatarSDK/native creation path");

            yield return null;
            Assert.That(NewSceneAvatarsSince(existingAvatarIds), Is.Empty);
            Assert.That(CountStagingRoots(), Is.EqualTo(stagingBefore));
            Assert.That(_ownedParent.transform.childCount, Is.Zero);
        }

        [UnityTearDown]
        public IEnumerator LeavePlayModeAfterEachFixture()
        {
            if (_ownedParent != null)
            {
                if (Application.isPlaying) Object.Destroy(_ownedParent);
                else Object.DestroyImmediate(_ownedParent);
                _ownedParent = null;
            }
            if (Application.isPlaying)
                yield return new ExitPlayMode();
        }

        private static SbAvatarEntity LoadOriginalPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab");
            Assert.That(source, Is.Not.Null);
            SbAvatarEntity originalPrefab = source.GetComponent<SbAvatarEntity>();
            Assert.That(originalPrefab, Is.Not.Null);
            return originalPrefab;
        }

        private static HashSet<int> SnapshotAvatarIds()
        {
            var ids = new HashSet<int>();
            foreach (SbAvatarEntity entity in Resources.FindObjectsOfTypeAll<SbAvatarEntity>())
                if (entity != null) ids.Add(entity.GetInstanceID());
            return ids;
        }

        private static List<SbAvatarEntity> NewSceneAvatarsSince(HashSet<int> existingIds)
        {
            var result = new List<SbAvatarEntity>();
            foreach (SbAvatarEntity entity in Resources.FindObjectsOfTypeAll<SbAvatarEntity>())
            {
                if (entity != null && !existingIds.Contains(entity.GetInstanceID()) &&
                    entity.gameObject.scene.IsValid())
                    result.Add(entity);
            }
            return result;
        }

        private static int CountStagingRoots()
        {
            int count = 0;
            foreach (GameObject gameObject in Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (gameObject.name == "SbLocalPresetAvatarStaging") ++count;
            }
            return count;
        }
    }
}
#endif
