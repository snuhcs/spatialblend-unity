// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformSceneActorRuntimeTests
    {
        private readonly List<string> _files = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string path in _files)
                if (File.Exists(path))
                    File.Delete(path);
            _files.Clear();
        }

        [Test]
        public void FrozenKnownAnchorChart_AdmitsOriginalFourAndOptionalOrderedSeatContact()
        {
            string[] defaultIds = Freeze(Chart(
                FreeformSceneActorDefinition.RootAnchorId,
                FreeformSceneActorDefinition.HeadAnchorId,
                FreeformSceneActorDefinition.LeftHandAnchorId,
                FreeformSceneActorDefinition.RightHandAnchorId), Chart(
                FreeformSceneActorDefinition.RootAnchorId,
                FreeformSceneActorDefinition.HeadAnchorId,
                FreeformSceneActorDefinition.LeftHandAnchorId,
                FreeformSceneActorDefinition.RightHandAnchorId));
            CollectionAssert.AreEqual(new[] { "root", "head", "hand_l", "hand_r" }, defaultIds);

            string[] withSeat = Freeze(Chart("root", "head", "hand_l", "hand_r", "seat_contact"),
                                      Chart("root", "head", "hand_l", "hand_r", "seat_contact"));
            CollectionAssert.AreEqual(new[] { "root", "head", "hand_l", "hand_r", "seat_contact" },
                                      withSeat);
            SpatialBlendActorAnchor[] mutableSource = Chart("root", "head", "hand_l", "hand_r", "seat_contact");
            string[] frozen = Freeze(mutableSource, Chart("root", "head", "hand_l", "hand_r", "seat_contact"));
            mutableSource[4] = new SpatialBlendActorAnchor("seat_typo", SpatialBlendActorAnchorRole.Body,
                                                         Vector3.zero);
            CollectionAssert.AreEqual(new[] { "root", "head", "hand_l", "hand_r", "seat_contact" },
                                      frozen, "The lifetime owns a copy, not the mutable source array.");
        }

        [Test]
        public void FrozenKnownAnchorChart_RejectsMismatchedOrderBeforeAnyRuntimeOwnerIsCreated()
        {
            Assert.That(TryFreeze(Chart("root", "head", "hand_l", "hand_r", "seat_contact"),
                                  Chart("root", "head", "hand_r", "hand_l", "seat_contact"),
                                  out string[] ids, out string error), Is.False);
            Assert.That(ids, Is.Null);
            Assert.That(error, Does.Contain("ordered"));
        }

        [Test]
        public void FrozenKnownAnchorChart_AllowsOmissionAndRejectsUnknownProspectiveRecording()
        {
            string[] required = Freeze(Chart("root", "head", "hand_l", "hand_r", "seat_contact"),
                                       Chart("root", "head", "hand_l", "hand_r", "seat_contact"));
            WriteCommitted(Frame(0, new[] { "root", "head", "hand_l", "hand_r" }),
                           out string omittedSeatJsonl, out string omittedSeatManifest);
            WriteCommitted(Frame(1, Array.Empty<string>()), out string emptyJsonl, out string emptyManifest);
            WriteCommitted(Frame(2, new[] { "root", "head", "hand_l", "hand_r", "seat_typo" }),
                           out string unknownJsonl, out string unknownManifest);
            var recording = new AnchorPoseRecording();
            Assert.That(recording.TryLoadCommitted(omittedSeatJsonl, omittedSeatManifest, required,
                                                    out string error), Is.True, error);
            Assert.That(recording.TryLoadCommitted(emptyJsonl, emptyManifest, required, out error), Is.True,
                        error);
            Assert.That(recording.TryLoadCommitted(unknownJsonl, unknownManifest, required, out error), Is.False);
            Assert.That(error, Does.Contain("known IDs"));

            WriteCommitted(Frame(3, new[] { "root", "head", "hand_l", "hand_r", "seat_contact" }),
                           out string seatJsonl, out string seatManifest);
            Assert.That(recording.TryLoadCommitted(seatJsonl, seatManifest,
                Freeze(Chart("root", "head", "hand_l", "hand_r"),
                       Chart("root", "head", "hand_l", "hand_r")), out error), Is.False);
            Assert.That(error, Does.Contain("known IDs"),
                        "The default four-anchor chart does not silently admit the optional seat channel.");
        }

        private void WriteCommitted(AnchorPoseFrame frame, out string jsonlPath, out string manifestPath)
        {
            jsonlPath = Path.GetTempFileName();
            manifestPath = Path.GetTempFileName();
            _files.Add(jsonlPath);
            _files.Add(manifestPath);
            string jsonl = FrameJson(frame) + "\n";
            File.WriteAllText(jsonlPath, jsonl);
            byte[] bytes = File.ReadAllBytes(jsonlPath);
            File.WriteAllText(manifestPath,
                "{\"provenance\":{\"orientation_mode\":\"s6-canonical-spatial-anchor-yaw-only: Unity yaw = pi/2 - S6 yaw; raw tracker quaternions retained as evidence, not relabeled as Unity anchor rotation\"},\"output\":{\"publication_state\":\"committed\",\"jsonl_path\":\"" +
                Path.GetFullPath(jsonlPath).Replace("\\", "\\\\") + "\",\"jsonl_byte_count\":" +
                bytes.Length + ",\"jsonl_sha256\":\"" + Hash(bytes) +
                "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
        }

        private static SpatialBlendActorAnchor[] Chart(params string[] ids)
        {
            var anchors = new SpatialBlendActorAnchor[ids.Length];
            for (int index = 0; index < ids.Length; ++index)
                anchors[index] = new SpatialBlendActorAnchor(ids[index],
                    index == 0 ? SpatialBlendActorAnchorRole.Root : SpatialBlendActorAnchorRole.Body,
                    Vector3.zero);
            return anchors;
        }

        // A navigable root needs geometry. A Region without footprint geometry registers fine, but it contains
        // nothing, so it can not be the root actors hang under; and no Region at all must not leave a detached actor.
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (GameObject go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private SpatialBlendObject Region(SpatialBlendLocalWorld world, string id, bool withCollider)
        {
            var go = new GameObject(id);
            _objects.Add(go);
            if (withCollider) go.AddComponent<BoxCollider>();
            var region = go.AddComponent<SpatialBlendObject>();
            region.Configure(world, "local", id, null, SpatialBlendObjectKind.Region);
            return region;
        }

        private static bool TryValidateRoot(SpatialBlendObject root, SpatialBlendLocalWorld world, out string error)
        {
            MethodInfo method = typeof(FreeformSceneActorRuntime).GetMethod("ValidateNavigableRoot",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { root, "local", world, "source", null };
            bool result = (bool)method.Invoke(null, arguments);
            error = arguments[4] as string;
            return result;
        }

        [Test]
        public void ANavigableRootIsAnExplicitTopRegionWithFootprintGeometry()
        {
            var worldObject = new GameObject("world");
            _objects.Add(worldObject);
            var world = worldObject.AddComponent<SpatialBlendLocalWorld>();
            SpatialBlendObject withCollider = Region(world, "room", true);
            SpatialBlendObject without = Region(world, "bare", false);
            Assert.That(withCollider.HasFootprintGeometry, Is.True);
            Assert.That(without.HasFootprintGeometry, Is.False);

            // A Region carrying a measured navigable polygon (the room's floor) has footprint geometry, collider or not.
            SpatialBlendObject floor = Region(world, "floor", false);
            Assert.That(floor.TrySetSupportPolygon(new[]
            {
                new Vector3(-2f, 0f, -2f), new Vector3(2f, 0f, -2f), new Vector3(2f, 0f, 2f), new Vector3(-2f, 0f, 2f)
            }, out string error), Is.True, error);
            Assert.That(floor.HasFootprintGeometry, Is.True);

            Assert.That(TryValidateRoot(null, world, out error), Is.False, "an unrooted actor is an error");
            Assert.That(error, Does.Contain("no navigable root"));
            Assert.That(TryValidateRoot(without, world, out error), Is.False);
            Assert.That(error, Does.Contain("no footprint geometry"));

            var objectOnly = new GameObject("object-only");
            _objects.Add(objectOnly);
            var loose = objectOnly.AddComponent<SpatialBlendObject>();
            loose.Configure(world, "local", "loose", null, SpatialBlendObjectKind.Object);
            Assert.That(TryValidateRoot(loose, world, out error), Is.False);
            Assert.That(error, Does.Contain("top-level Region"));

            // Footprint geometry alone is not enough: the root must be live in the actor's space of this world.
            Assert.That(TryValidateRoot(floor, world, out error), Is.False);
            Assert.That(error, Does.Contain("must be registered"));
        }

        private static string[] Freeze(SpatialBlendActorAnchor[] source, SpatialBlendActorAnchor[] receiver)
        {
            Assert.That(TryFreeze(source, receiver, out string[] ids, out string error), Is.True, error);
            return ids;
        }

        private static bool TryFreeze(SpatialBlendActorAnchor[] source, SpatialBlendActorAnchor[] receiver,
                                      out string[] ids, out string error)
        {
            MethodInfo method = typeof(FreeformSceneActorRuntime).GetMethod("TryFreezeKnownAnchorIds",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { source, receiver, null, null };
            bool result = (bool)method.Invoke(null, arguments);
            ids = arguments[2] as string[];
            error = arguments[3] as string;
            return result;
        }

        private static AnchorPoseFrame Frame(long sequence, string[] ids)
        {
            var anchors = new AnchorPoseSample[ids.Length];
            for (int index = 0; index < ids.Length; ++index)
            {
                anchors[index] = new AnchorPoseSample
                {
                    anchorId = ids[index],
                    worldPosition = new Vector3(index, 1f, -index),
                    worldRotation = Quaternion.identity
                };
            }
            return new AnchorPoseFrame
            {
                sequence = sequence,
                timeSeconds = sequence,
                anchors = anchors
            };
        }

        private static string FrameJson(AnchorPoseFrame frame)
        {
            var samples = new List<string>();
            foreach (AnchorPoseSample sample in frame.anchors)
                samples.Add("{\"anchorId\":\"" + sample.anchorId + "\",\"worldPosition\":{\"x\":" +
                            sample.worldPosition.x.ToString(CultureInfo.InvariantCulture) + ",\"y\":" +
                            sample.worldPosition.y.ToString(CultureInfo.InvariantCulture) +
                            ",\"z\":" + sample.worldPosition.z.ToString(CultureInfo.InvariantCulture) +
                            "},\"worldRotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}");
            return "{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":" + frame.sequence +
                   ",\"timeSeconds\":" + frame.timeSeconds.ToString(CultureInfo.InvariantCulture) + ",\"anchors\":[" +
                   string.Join(",", samples) + "]}";
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
#endif
