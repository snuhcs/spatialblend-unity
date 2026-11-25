// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The shipped MetaAvatarPrefab sends avatar state once the position speed passes 0.5 m/s
    /// (_positionStartThreshold: 0.5, MetaAvatarPrefab.prefab:98). The code default stays 0.3 as on v1; the
    /// prefab value is the shipped one (the rule parity A2 already follows). It gates how often the source
    /// sends networked state.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AvatarSendThresholdPrefabTests
    {
        private const string Prefab = "Assets/SpatialBlend/Avatar/Prefabs/MetaAvatarPrefab.prefab";

        private static SerializedObject Entity()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            Assert.That(prefab, Is.Not.Null);
            var entity = prefab.GetComponentInChildren<SbNetworkAvatarEntity>(true);
            Assert.That(entity, Is.Not.Null);
            return new SerializedObject(entity);
        }

        [Test]
        public void ThePrefabStartsSendingAtHalfAMetrePerSecond()
        {
            Assert.That(Entity().FindProperty("_positionStartThreshold").floatValue, Is.EqualTo(0.5f));
            Assert.That(Entity().FindProperty("_positionStopThreshold").floatValue, Is.EqualTo(0.1f));
        }

        [Test]
        public void TheShippedThresholdKeepsA04MetrePerSecondDriftFromSending()
        {
            float start = Entity().FindProperty("_positionStartThreshold").floatValue;
            float stop = Entity().FindProperty("_positionStopThreshold").floatValue;
            AvatarStateUpdateFilter.StepResult drift = AvatarStateUpdateFilter.Step(false, false, 0.4f, 0f, start, stop, 10f, 3f, false, false);
            AvatarStateUpdateFilter.StepResult walk = AvatarStateUpdateFilter.Step(false, false, 0.6f, 0f, start, stop, 10f, 3f, false, false);
            Assert.That(drift.Send, Is.False, "0.4 m/s is under the shipped start threshold (it sent at the old 0.3)");
            Assert.That(walk.Send, Is.True);
        }
    }
}
#endif
