// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationObservedCollaboratorNative")]
    public sealed partial class FoundationObservedCollaboratorNativeTests
    {
        [Test]
        public void NativeC1BatchPinsNestedRowsAndRejectsWholeMalformedOrRetiredBatch()
        {
            Assert.That(SpatialBlendAPI.TryEnsureObservedCollaboratorBatchAbi(out string error), Is.True, error);
            long session = SpatialBlendAPI.CreateSession();
            Assert.That(session, Is.GreaterThan(0));
            try
            {
                long space = SpatialBlendAPI.AddSpace(session);
                Assert.That(space, Is.GreaterThan(0));
                Assert.That(SpatialBlendAPI.EnableActorObservationFrames(session), Is.True);
                var actors = new long[2]; var epochs = new ulong[2]; var points = new long[2]; var leases = new ulong[2];
                long room = SpatialBlendAPI.AddElement(space);
                for (int index = 0; index < 2; ++index)
                {
                    long owner = SpatialBlendAPI.AddElement(space, room);
                    points[index] = SpatialBlendAPI.AddElement(space, room);
                    actors[index] = SpatialBlendAPI.CreateActor(space);
                    Assert.That(owner, Is.GreaterThan(0)); Assert.That(points[index], Is.GreaterThan(0)); Assert.That(actors[index], Is.GreaterThan(0));
                    SpatialBlendAPI.SetElementSharedId(space, owner, 100 + index);
                    SpatialBlendAPI.AddAnchor(space, actors[index], "root", Vector3.zero);
                    SpatialBlendAPI.AddAnchor(space, actors[index], "head", new Vector3(0, 1.7f, 0));
                    SpatialBlendAPI.SetActorElement(space, actors[index], owner);
                    epochs[index] = SpatialBlendAPI.BeginActorObservationEpoch(space, actors[index], "root");
                    Assert.That(epochs[index], Is.Not.EqualTo(0));
                    Assert.That(SpatialBlendAPI.TryBeginObservedSourceAnchorPoint(space, points[index], actors[index], "head",
                        1000000000, out leases[index], out error), Is.True, error);
                }

                var frames = Frames(actors, epochs, 0);
                var publications = Publications(points, leases);
                Assert.That(SpatialBlendAPI.TryApplyObservedCollaboratorBatch(space, frames, publications, out error), Is.True, error);
                AssertClock(space, actors[0], epochs[0], 0); AssertClock(space, actors[1], epochs[1], 0);

                var duplicate = new List<SpatialBlendAPI.ObservedCollaboratorFrameInput> { Frame(actors[0], epochs[0], 1, 0), Frame(actors[0], epochs[0], 1, 1) };
                Assert.That(SpatialBlendAPI.TryApplyObservedCollaboratorBatch(space, duplicate, publications, out error), Is.False);
                Assert.That(error, Does.Contain("without sequential fallback"));
                AssertClock(space, actors[0], epochs[0], 0); AssertClock(space, actors[1], epochs[1], 0);

                Assert.That(SpatialBlendAPI.TryRetireObservedSourceAnchorPoint(space, points[0], leases[0], out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryApplyObservedCollaboratorBatch(space, Frames(actors, epochs, 1), Publications(points, leases), out error), Is.False);
                AssertClock(space, actors[0], epochs[0], 0); AssertClock(space, actors[1], epochs[1], 0);
            }
            finally { SpatialBlendAPI.DestroySession(session); }
        }

        [Test]
        public void NativeC1SourceSetupNegotiatesLayoutAndKeepsSetupLifecycleAtomic()
        {
            Assert.That(SpatialBlendAPI.TryEnsureObservedSourceSetupAbi(out string error), Is.True, error);
            long session = SpatialBlendAPI.CreateSession();
            Assert.That(session, Is.GreaterThan(0));
            try
            {
                long space = SpatialBlendAPI.AddSpace(session);
                Assert.That(space, Is.GreaterThan(0));
                Assert.That(SpatialBlendAPI.EnableActorObservationFrames(session), Is.True);
                long room = SpatialBlendAPI.AddElement(space);
                var actors = new long[2]; var owners = new long[2]; var points = new long[2];
                for (int index = 0; index < 2; ++index)
                {
                    long owner = owners[index] = SpatialBlendAPI.AddElement(space, room);
                    points[index] = SpatialBlendAPI.AddElement(space, room);
                    actors[index] = SpatialBlendAPI.CreateActor(space);
                    Assert.That(owner, Is.GreaterThan(0)); Assert.That(points[index], Is.GreaterThan(0));
                    Assert.That(actors[index], Is.GreaterThan(0));
                    SpatialBlendAPI.SetElementSharedId(space, owner, 200 + index);
                    SpatialBlendAPI.AddAnchor(space, actors[index], "root", Vector3.zero);
                    SpatialBlendAPI.AddAnchor(space, actors[index], "head", new Vector3(0, 1.7f, 0));
                    SpatialBlendAPI.SetActorElement(space, actors[index], owner);
                }
                var participants = new List<SpatialBlendAPI.ObservedSourceSetupParticipantInput> {
                    new SpatialBlendAPI.ObservedSourceSetupParticipantInput(actors[0], "root"),
                    new SpatialBlendAPI.ObservedSourceSetupParticipantInput(actors[1], "root") };
                var invalidPoints = new List<SpatialBlendAPI.ObservedSourceSetupPointInput> {
                    new SpatialBlendAPI.ObservedSourceSetupPointInput(points[0], actors[0], "head", 1000000000),
                    // An actor-owned point is malformed in Core.  It must not open actor 0's epoch.
                    new SpatialBlendAPI.ObservedSourceSetupPointInput(
                        owners[1], actors[1], "head", 1000000000) };
                Assert.That(SpatialBlendAPI.TryBeginObservedSourceSetup(space, participants, invalidPoints,
                    out var rejected, out error), Is.False);
                Assert.That(rejected, Is.Null);
                Assert.That(SpatialBlendAPI.TryGetActorObservationClock(space, actors[0], out _), Is.False);
                Assert.That(SpatialBlendAPI.TryGetActorObservationClock(space, actors[1], out _), Is.False);

                var validPoints = new List<SpatialBlendAPI.ObservedSourceSetupPointInput> {
                    new SpatialBlendAPI.ObservedSourceSetupPointInput(points[0], actors[0], "head", 1000000000),
                    new SpatialBlendAPI.ObservedSourceSetupPointInput(points[1], actors[1], "head", 1000000000) };
                Assert.That(SpatialBlendAPI.TryBeginObservedSourceSetup(space, participants, validPoints,
                    out var receipt, out error), Is.True, error);
                Assert.That(receipt.Setup, Is.GreaterThan(0));
                Assert.That(receipt.Actors.Count, Is.EqualTo(2));
                Assert.That(receipt.Points.Count, Is.EqualTo(2));
                for (int index = 0; index < 2; ++index)
                {
                    Assert.That(receipt.Actors[index].Actor, Is.EqualTo(actors[index]));
                    Assert.That(receipt.Actors[index].Epoch, Is.GreaterThan(0));
                    Assert.That(receipt.Points[index].Element, Is.EqualTo(points[index]));
                    Assert.That(receipt.Points[index].Lease, Is.GreaterThan(0));
                    AssertInitialClock(space, actors[index], receipt.Actors[index].Epoch);
                }
                Assert.That(SpatialBlendAPI.TryRetireObservedSourceSetup(space, receipt.Setup, out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryRetireObservedSourceSetup(space, receipt.Setup, out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryReleaseObservedSourceSetup(receipt.Setup, out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryRetireObservedSourceSetup(space, receipt.Setup, out error), Is.False);
            }
            finally { SpatialBlendAPI.DestroySession(session); }
        }

        private static List<SpatialBlendAPI.ObservedCollaboratorFrameInput> Frames(long[] actors, ulong[] epochs, long sequence) =>
            new List<SpatialBlendAPI.ObservedCollaboratorFrameInput> { Frame(actors[0], epochs[0], sequence, 0), Frame(actors[1], epochs[1], sequence, 1) };
        private static SpatialBlendAPI.ObservedCollaboratorFrameInput Frame(long actor, ulong epoch, long sequence, int index) =>
            new SpatialBlendAPI.ObservedCollaboratorFrameInput(actor, epoch, sequence, sequence * .01,
                new[] { new SpatialBlendAPI.SbActorObservation { anchor_name = "root", position_x = index * 2 + sequence * .01, position_y = 0, position_z = 0, yaw = 0 },
                        new SpatialBlendAPI.SbActorObservation { anchor_name = "head", position_x = index * 2 + sequence * .01, position_y = .1, position_z = 1.7, yaw = .2 } });
        private static List<SpatialBlendAPI.ObservedCollaboratorPublicationInput> Publications(long[] points, ulong[] leases)
        {
            Assert.That(SpatialBlendAPI.TryReadRichMonotonicNanoseconds(out ulong now, out _, out _, out string error), Is.True, error);
            return new List<SpatialBlendAPI.ObservedCollaboratorPublicationInput> {
                new SpatialBlendAPI.ObservedCollaboratorPublicationInput(points[0], leases[0], (long)now, (long)now + 1000000000),
                new SpatialBlendAPI.ObservedCollaboratorPublicationInput(points[1], leases[1], (long)now, (long)now + 1000000000) };
        }
        private static void AssertClock(long space, long actor, ulong epoch, long sequence)
        {
            Assert.That(SpatialBlendAPI.TryGetActorObservationClock(space, actor, out var clock), Is.True);
            Assert.That(clock.Epoch, Is.EqualTo(epoch)); Assert.That(clock.Sequence, Is.EqualTo(sequence)); Assert.That(clock.HasSample, Is.True);
        }
        private static void AssertInitialClock(long space, long actor, ulong epoch)
        {
            Assert.That(SpatialBlendAPI.TryGetActorObservationClock(space, actor, out var clock), Is.True);
            Assert.That(clock.Epoch, Is.EqualTo(epoch)); Assert.That(clock.Sequence, Is.EqualTo(-1)); Assert.That(clock.HasSample, Is.False);
        }
    }
}
#endif
