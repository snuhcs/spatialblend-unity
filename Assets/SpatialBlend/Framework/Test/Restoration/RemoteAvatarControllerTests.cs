// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Fake wrist transforms stand in for Meta Avatar skeleton joints.
    /// The Avatar SDK has no macOS native library, so these tests never load one.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RemoteAvatarControllerTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private GameObject _leftPrefab;
        private GameObject _rightPrefab;

        [SetUp]
        public void SetUp()
        {
            _leftPrefab = NewRoot("LeftController");
            _rightPrefab = NewRoot("RightController");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (_roots[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_roots[i]);
                }
            }

            _roots.Clear();
        }

        [Test]
        public void MetaControllers_ParentImmediately_WithV1Offsets()
        {
            Transform metaLeft = Wrist("MetaLeftWrist");
            Transform metaRight = Wrist("MetaRightWrist");
            Action sbLoaded = null;
            var attachment = new RemoteAvatarControllerAttachment();

            attachment.Bind(
                _leftPrefab,
                _rightPrefab,
                metaLeft,
                metaRight,
                callback => sbLoaded = callback,
                () => null,
                () => null,
                () => { });

            AssertPose(attachment.MetaLeft, metaLeft, RemoteAvatarControllerAttachment.MetaLeftPosition, RemoteAvatarControllerAttachment.MetaLeftRotation);
            AssertPose(attachment.MetaRight, metaRight, RemoteAvatarControllerAttachment.MetaRightPosition, RemoteAvatarControllerAttachment.MetaRightRotation);
            Assert.That(attachment.SbAttached, Is.False);
            Assert.That(attachment.SbLeft, Is.Null);
            Assert.That(attachment.SbRight, Is.Null);
            Assert.That(sbLoaded, Is.Not.Null);
        }

        [Test]
        public void SbControllers_AttachOnlyAfterSkeletonLoadedCallback_WithV1Offsets()
        {
            Transform metaLeft = Wrist("MetaLeftWrist");
            Transform metaRight = Wrist("MetaRightWrist");
            Transform sbLeft = Wrist("SbLeftWrist");
            Transform sbRight = Wrist("SbRightWrist");
            Action sbLoaded = null;
            int wristLookups = 0;
            bool attachedNotified = false;
            var attachment = new RemoteAvatarControllerAttachment();

            attachment.Bind(
                _leftPrefab,
                _rightPrefab,
                metaLeft,
                metaRight,
                callback => sbLoaded = callback,
                () =>
                {
                    wristLookups++;
                    return sbLeft;
                },
                () =>
                {
                    wristLookups++;
                    return sbRight;
                },
                () => attachedNotified = true);

            Assert.That(wristLookups, Is.EqualTo(0));
            Assert.That(attachment.SbAttached, Is.False);
            Assert.That(sbLeft.childCount, Is.EqualTo(0));
            Assert.That(sbRight.childCount, Is.EqualTo(0));

            sbLoaded();

            Assert.That(wristLookups, Is.EqualTo(2));
            Assert.That(attachment.SbAttached, Is.True);
            Assert.That(attachedNotified, Is.True);
            AssertPose(attachment.SbLeft, sbLeft, RemoteAvatarControllerAttachment.SbLeftPosition, RemoteAvatarControllerAttachment.SbLeftRotation);
            AssertPose(attachment.SbRight, sbRight, RemoteAvatarControllerAttachment.SbRightPosition, RemoteAvatarControllerAttachment.SbRightRotation);

            int childrenAfter = sbLeft.childCount + sbRight.childCount;
            sbLoaded();
            Assert.That(sbLeft.childCount + sbRight.childCount, Is.EqualTo(childrenAfter));
        }

        [Test]
        public void ControllerVisibility_FollowsRemoteAvatarVisibilityPolicy()
        {
            RemoteAvatarControllerAttachment attachment = AttachBoth();

            RemoteAvatarVisibilityDecision spawnHidden = Evaluate(
                AvatarDisplayMode.Default, interacting: true, isLocal: false,
                timeSinceSpawn: 0f, spawnHideSeconds: RemoteAvatarVisibilityPolicy.SpawnedHideSeconds,
                fadeDuration: 1.5f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(spawnHidden);
            AssertActive(attachment, spawnHidden);

            RemoteAvatarVisibilityDecision idle = Evaluate(
                AvatarDisplayMode.Default, interacting: false, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 1.5f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(idle);
            AssertActive(attachment, idle);
            Assert.That(attachment.SbLeft.activeSelf, Is.True);
            Assert.That(attachment.MetaLeft.activeSelf, Is.False);

            RemoteAvatarVisibilityDecision interacting = Evaluate(
                AvatarDisplayMode.Default, interacting: true, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 1.5f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(interacting);
            AssertActive(attachment, interacting);
            Assert.That(attachment.SbLeft.activeSelf, Is.False);
            Assert.That(attachment.MetaLeft.activeSelf, Is.True);

            RemoteAvatarVisibilityDecision zoneIdle = Evaluate(
                AvatarDisplayMode.ZoneBased, interacting: false, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 0f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(zoneIdle);
            AssertActive(attachment, zoneIdle);
            Assert.That(attachment.SbLeft.activeSelf, Is.False);
            Assert.That(attachment.MetaLeft.activeSelf, Is.False);

            RemoteAvatarVisibilityDecision zoneOn = Evaluate(
                AvatarDisplayMode.ZoneBased, interacting: true, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 0f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(zoneOn);
            AssertActive(attachment, zoneOn);
            Assert.That(attachment.SbRight.activeSelf, Is.False);
            Assert.That(attachment.MetaRight.activeSelf, Is.True);

            attachment.MetaLeft.SetActive(true);
            attachment.MetaRight.SetActive(true);
            RemoteAvatarVisibilityDecision fadeBegins = Evaluate(
                AvatarDisplayMode.ZoneBased, interacting: false, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 1.5f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(fadeBegins);
            Assert.That(fadeBegins.ApplySbVisibility, Is.True);
            Assert.That(fadeBegins.ApplyMetaVisibility, Is.False);
            Assert.That(attachment.SbLeft.activeSelf, Is.EqualTo(fadeBegins.SbVisible));
            Assert.That(attachment.MetaLeft.activeSelf, Is.True, "Meta controllers stay until the fade sample applies alpha.");

            RemoteAvatarVisibilityDecision fadeMid = Evaluate(
                AvatarDisplayMode.ZoneBased, interacting: true, isLocal: false,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 1.5f, fadeProgress: 0.4f);
            attachment.Apply(fadeMid);
            Assert.That(fadeMid.ApplyMetaAlpha, Is.True);
            AssertActive(attachment, fadeMid);

            bool metaBeforeLocal = attachment.MetaLeft.activeSelf;
            bool sbBeforeLocal = attachment.SbLeft.activeSelf;
            RemoteAvatarVisibilityDecision local = Evaluate(
                AvatarDisplayMode.Default, interacting: true, isLocal: true,
                timeSinceSpawn: 10f, spawnHideSeconds: 0f,
                fadeDuration: 1.5f, fadeProgress: RemoteAvatarVisibilityPolicy.NoFadeSample);
            attachment.Apply(local);
            Assert.That(local.ApplyRemoteRules, Is.False);
            Assert.That(attachment.MetaLeft.activeSelf, Is.EqualTo(metaBeforeLocal));
            Assert.That(attachment.SbLeft.activeSelf, Is.EqualTo(sbBeforeLocal));
        }

        private RemoteAvatarControllerAttachment AttachBoth()
        {
            Action sbLoaded = null;
            var attachment = new RemoteAvatarControllerAttachment();
            attachment.Bind(
                _leftPrefab,
                _rightPrefab,
                Wrist("MetaLeftWrist"),
                Wrist("MetaRightWrist"),
                callback => sbLoaded = callback,
                () => Wrist("SbLeftWrist"),
                () => Wrist("SbRightWrist"),
                () => { });
            sbLoaded();
            return attachment;
        }

        private Transform Wrist(string name)
        {
            GameObject wrist = NewRoot(name);
            wrist.transform.position = new Vector3(3f, 4f, 5f);
            wrist.transform.rotation = Quaternion.Euler(20f, 40f, 60f);
            wrist.transform.localScale = new Vector3(2f, 2f, 2f);
            return wrist.transform;
        }

        private GameObject NewRoot(string name)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            _roots.Add(go);
            return go;
        }

        private static RemoteAvatarVisibilityDecision Evaluate(
            AvatarDisplayMode mode,
            bool interacting,
            bool isLocal,
            float timeSinceSpawn,
            float spawnHideSeconds,
            float fadeDuration,
            float fadeProgress)
        {
            return RemoteAvatarVisibilityPolicy.Evaluate(new RemoteAvatarVisibilityInput(
                mode,
                interacting,
                isLocal,
                timeSinceSpawn,
                spawnHideSeconds,
                fadeDuration,
                fadeProgress));
        }

        private static void AssertActive(RemoteAvatarControllerAttachment attachment, RemoteAvatarVisibilityDecision decision)
        {
            if (decision.ApplySbVisibility)
            {
                Assert.That(attachment.SbLeft.activeSelf, Is.EqualTo(decision.SbVisible), decision.ToString());
                Assert.That(attachment.SbRight.activeSelf, Is.EqualTo(decision.SbVisible), decision.ToString());
            }

            if (decision.ApplyMetaVisibility || decision.ApplyMetaAlpha)
            {
                Assert.That(attachment.MetaLeft.activeSelf, Is.EqualTo(decision.MetaVisible), decision.ToString());
                Assert.That(attachment.MetaRight.activeSelf, Is.EqualTo(decision.MetaVisible), decision.ToString());
            }
        }

        private static void AssertPose(GameObject instance, Transform wrist, Vector3 localPosition, Quaternion localRotation)
        {
            Assert.That(instance, Is.Not.Null);
            Assert.That(instance.transform.parent, Is.EqualTo(wrist));
            Assert.That(instance.transform.localPosition, Is.EqualTo(localPosition));
            Assert.That(Quaternion.Dot(instance.transform.localRotation, localRotation), Is.EqualTo(1f).Within(0.0001f));
        }
    }
}
#endif
