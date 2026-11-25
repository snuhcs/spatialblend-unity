// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class AnchorPoseInputContractTests
    {
        [Test]
        public void RecordedJson_IsExplicitCopiedAndRejectsInvalidPoseInputs()
        {
            var go = new GameObject("recorded-pose-source");
            var source = go.AddComponent<RecordedAnchorPoseSource>();
            var sink = new RecordingSink();
            try
            {
                source.SetKnownAnchorIds(new[] { "head", "hand_l" });
                source.SetSink(sink);
                string fixture = File.ReadAllText(Path.Combine(Application.dataPath,
                    "SpatialBlend/Framework/Test/Restoration/Fixtures/anchor_pose_v1.json"));
                Assert.That(source.TrySubmitJson(fixture, out string error), Is.True, error);
                Assert.That(sink.Frames, Has.Count.EqualTo(1));
                Assert.That(source.TrySubmitJson("{\"anchors\":[]}", out _), Is.False,
                    "JSON must not inherit field initializers for missing schema/frame.");
                Assert.That(source.TrySubmitJson("{\"recording\":{\"schema\":1,\"frame\":\"unity-world\"}}", out _),
                    Is.False, "Nested metadata is not a root frame schema.");

                AnchorPoseFrame rotated = Frame(2, 1d, "head", Quaternion.Euler(0f, 73f, 0f));
                sink.MutateReceived = true;
                Assert.That(source.TrySubmit(rotated, out error), Is.True, error);
                Assert.That(rotated.anchors[0].worldPosition.y, Is.EqualTo(1.75f),
                    "The source must not hand caller-owned DTOs to sinks.");

                AnchorPoseFrame nonUnit = Frame(3, 2d, "head", new Quaternion(0f, 0f, 0f, 2f));
                Assert.That(source.TrySubmit(nonUnit, out _), Is.False);
                AnchorPoseFrame nul = Frame(3, 2d, "head\0bad", Quaternion.identity);
                Assert.That(source.TrySubmit(nul, out _), Is.False);
                AnchorPoseFrame backwards = Frame(1, 2d, "head", Quaternion.identity);
                Assert.That(source.TrySubmit(backwards, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RecordedSource_RejectsReentryAndClearsTrackedOverridesOnDisableAndReplacement()
        {
            var go = new GameObject("recorded-pose-reset");
            var source = go.AddComponent<RecordedAnchorPoseSource>();
            var first = new RecordingSink();
            var second = new RecordingSink();
            try
            {
                source.SetKnownAnchorIds(new[] { "head" });
                source.SetSink(first);
                first.Reenter = () =>
                {
                    first.ReentryAccepted = source.TrySubmit(
                        Frame(2, 2d, "head", Quaternion.identity), out _);
                    InvokeInstance(source, "OnDisable");
                };
                Assert.That(source.TrySubmit(Frame(1, 1d, "head", Quaternion.identity), out _), Is.True);
                Assert.That(first.ReentryAccepted, Is.False);
                CollectionAssert.AreEqual(new[] { "head" }, first.Clears,
                    "Disable requested during a sink callback must clear after submission completes.");
                source.SetSink(second);
                CollectionAssert.AreEqual(new[] { "head" }, first.Clears);

                Assert.That(source.TrySubmit(Frame(1, 0d, "head", Quaternion.identity), out _), Is.True,
                    "A replacement receiver starts a new replay epoch after reset.");
                InvokeInstance(source, "OnDisable");
                CollectionAssert.AreEqual(new[] { "head" }, second.Clears);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ManagerSink_UsesCapturedSourceActorAndFullSnapshotNativeObservations()
        {
            var managerGo = new GameObject("anchor-input-manager");
            var sourceGo = new GameObject("anchor-input-source");
            var replayGo = new GameObject("anchor-input-replay-source");
            var manager = managerGo.AddComponent<RecordingAnchorManager>();
            var source = sourceGo.AddComponent<SbLocalEntity>();
            var replay = replayGo.AddComponent<RecordedAnchorPoseSource>();
            try
            {
                ConfigureLiveSource(manager, source, 23, 5, 71, 99, new[] { "head", "hand_l" });
                SetPrivate(source, "isSource", false);
                Assert.That(manager.TryCreateAnchorPoseInput(source, out _), Is.False,
                    "A receiver entity cannot inject source-anchor observations.");
                SetPrivate(source, "isSource", true);
                Assert.That(manager.TryCreateAnchorPoseInput(source, out IAnchorPoseFrameSink sink), Is.True);
                Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 0, timeSeconds = 0d,
                    anchors = new[]
                    {
                        Sample("head", new Vector3(1f, 2.5f, 3f), Quaternion.Euler(0f, 90f, 0f)),
                        Sample("hand_l", new Vector3(-1f, 1.25f, 2f), Quaternion.identity)
                    }
                }), Is.True);
                Assert.That(manager.Sets, Has.Count.EqualTo(2));
                Assert.That(manager.Sets[0].Space, Is.EqualTo(23));
                Assert.That(manager.Sets[0].Actor, Is.EqualTo(99));
                Assert.That(manager.Sets[0].Anchor, Is.EqualTo("head"));
                Assert.That(manager.Sets[0].Position, Is.EqualTo(new Vector3(1f, 2.5f, 3f)));
                Assert.That(manager.Sets[0].Yaw, Is.EqualTo(90f).Within(0.01f));

                Assert.That(sink.TryApplyWorldFrame(Frame(1, 1d, "head", Quaternion.identity)), Is.True);
                CollectionAssert.AreEqual(new[] { "hand_l" }, manager.Clears,
                    "A frame is a complete snapshot, so omitted anchors clear.");
                int callsBeforeUnknown = manager.Sets.Count + manager.Clears.Count;
                Assert.That(sink.TryApplyWorldFrame(Frame(2, 2d, "unknown", Quaternion.identity)), Is.False);
                Assert.That(manager.Sets.Count + manager.Clears.Count, Is.EqualTo(callsBeforeUnknown));

                Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 2, timeSeconds = 2d, anchors = Array.Empty<AnchorPoseSample>()
                }), Is.True, "An explicit empty full snapshot is complete tracking loss, not malformed input.");
                CollectionAssert.AreEqual(new[] { "hand_l", "head" }, manager.Clears);

                Assert.That(manager.TryCreateAnchorPoseInput(source, out IAnchorPoseFrameSink replaySink), Is.True);
                replay.SetKnownAnchorIds(new[] { "head", "hand_l" });
                replay.SetSink(replaySink);
                Assert.That(replay.TrySubmit(Frame(0, 0d, "head", Quaternion.identity), out _), Is.True);
                replay.ReplayReset();
                Assert.That(replay.TrySubmit(Frame(0, 0d, "head", Quaternion.identity), out _), Is.True,
                    "Resetting a source also resets the bound production sink replay watermark.");

                int callsBeforeStale = manager.Sets.Count + manager.Clears.Count;
                SetPrivate(manager, "_lifecycleGeneration", 6L);
                Assert.That(sink.TryApplyWorldFrame(Frame(2, 2d, "head", Quaternion.identity)), Is.False,
                    "A stale sink cannot mutate a replacement connection generation.");
                Assert.That(manager.Sets.Count + manager.Clears.Count, Is.EqualTo(callsBeforeStale));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replayGo);
                UnityEngine.Object.DestroyImmediate(sourceGo);
                UnityEngine.Object.DestroyImmediate(managerGo);
            }
        }

        private static AnchorPoseFrame Frame(long sequence, double time, string id, Quaternion rotation) =>
            new AnchorPoseFrame
            {
                sequence = sequence, timeSeconds = time,
                anchors = new[] { Sample(id, new Vector3(0f, 1.75f, 0f), rotation) }
            };

        private static AnchorPoseSample Sample(string id, Vector3 position, Quaternion rotation) =>
            new AnchorPoseSample { anchorId = id, worldPosition = position, worldRotation = rotation };

        private static void ConfigureLiveSource(SpatialBlendManager manager, SbEntity source, long space,
                                                long generation, long entity, long actor,
                                                IEnumerable<string> anchors)
        {
            SetPrivate(manager, "_initialized", true);
            SetPrivate(manager, "_connectionState", SpatialBlendConnectionState.Connected);
            SetPrivate(manager, "_spaceID", space);
            SetPrivate(manager, "_lifecycleGeneration", generation);
            SetPrivate(source, "_spaceManager", manager);
            SetPrivate(source, "_id", entity);
            SetPrivate(source, "isSource", true);

            FieldInfo registrationsField = typeof(SpatialBlendManager).GetField("_entityRegistrations",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Type registrationType = registrationsField.FieldType.GetGenericArguments()[1];
            object registration = Activator.CreateInstance(registrationType);
            registrationType.GetField("Id").SetValue(registration, entity);
            registrationType.GetField("Token").SetValue(registration, 17L);
            registrationType.GetField("IsSource").SetValue(registration, true);
            ((IDictionary)registrationsField.GetValue(manager)).Add(source, registration);
            ((IDictionary)typeof(SpatialBlendManager).GetField("_v2ActorByEntity",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager)).Add(entity, actor);
            object anchorSet = typeof(SpatialBlendManager).GetField("_v2ActorAnchors",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            MethodInfo add = anchorSet.GetType().GetMethod("Add");
            foreach (string anchor in anchors)
                add.Invoke(anchorSet, new object[] { ValueTuple.Create(actor, anchor) });
        }

        private static void SetPrivate(object target, string name, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                field.SetValue(target, value);
                return;
            }
            throw new MissingFieldException(target.GetType().FullName, name);
        }

        private static void InvokeInstance(object target, string method) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);

        private sealed class RecordingSink : IAnchorPoseFrameSink
        {
            public readonly List<AnchorPoseFrame> Frames = new List<AnchorPoseFrame>();
            public readonly List<string> Clears = new List<string>();
            public Action Reenter;
            public bool ReentryAccepted;
            public bool MutateReceived;
            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                Frames.Add(frame);
                if (MutateReceived) frame.anchors[0].worldPosition = Vector3.zero;
                Reenter?.Invoke();
                return true;
            }
            public void ClearWorldAnchor(string anchorId) => Clears.Add(anchorId);
        }

        private sealed class RecordingAnchorManager : SpatialBlendManager
        {
            public readonly List<(long Space, long Actor, string Anchor, Vector3 Position, float Yaw)> Sets =
                new List<(long, long, string, Vector3, float)>();
            public readonly List<string> Clears = new List<string>();
            protected override void InitializeNativeLibrary() { }
            protected override void SetAnchorPoseInputTransform(long space, long actor, string anchorId,
                Vector3 position, float yaw) => Sets.Add((space, actor, anchorId, position, yaw));
            protected override void ClearAnchorPoseInputTransform(long space, long actor, string anchorId) =>
                Clears.Add(anchorId);
        }
    }
}
#endif
