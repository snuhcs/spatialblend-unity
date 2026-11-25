// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// This fixture is selected only by the pre-C1 compatibility gate. It
    /// proves the optional C1 import fails closed while the pre-existing actor
    /// observation ABI continues to admit a complete legacy frame.
    /// </summary>
    [Category("FoundationObservedCollaboratorCompatibility")]
    public sealed class FoundationObservedCollaboratorCompatibilityTests
    {
        [Test]
        public void MissingOptionalC1SymbolsLeaveLegacyObservationFramesUsable()
        {
            // The subject is a pre-C1 core library. tools/run-foundation-tests.sh --observed-collaborator-compatibility
            // runs it against one, and its verifier fails a skipped case; a project whose core has C1 has nothing to prove.
            if (SpatialBlendAPI.TryEnsureObservedCollaboratorBatchAbi(out _))
                Assert.Ignore("Needs a pre-C1 core library (no observed-collaborator ABI); the loaded core has C1.");
            Assert.That(SpatialBlendAPI.TryEnsureObservedCollaboratorBatchAbi(out string batchError),
                Is.False);
            Assert.That(batchError, Does.Contain("feature unavailable"));
            Assert.That(SpatialBlendAPI.TryEnsureObservedSourceSetupAbi(out string setupError),
                Is.False);
            Assert.That(setupError, Does.Contain("feature unavailable"));

            long session = SpatialBlendAPI.CreateSession();
            Assert.That(session, Is.GreaterThan(0));
            try
            {
                long space = SpatialBlendAPI.AddSpace(session);
                long room = SpatialBlendAPI.AddElement(space);
                long owner = SpatialBlendAPI.AddElement(space, room);
                long actor = SpatialBlendAPI.CreateActor(space);
                Assert.That(space, Is.GreaterThan(0));
                Assert.That(room, Is.GreaterThan(0));
                Assert.That(owner, Is.GreaterThan(0));
                Assert.That(actor, Is.GreaterThan(0));
                SpatialBlendAPI.SetElementSharedId(space, owner, 7101);
                SpatialBlendAPI.AddAnchor(space, actor, "root", Vector3.zero);
                SpatialBlendAPI.SetActorElement(space, actor, owner);
                Assert.That(SpatialBlendAPI.EnableActorObservationFrames(session), Is.True);
                ulong epoch = SpatialBlendAPI.BeginActorObservationEpoch(space, actor, "root");
                Assert.That(epoch, Is.GreaterThan(0));
                var frame = new[] { new SpatialBlendAPI.SbActorObservation
                {
                    anchor_name = "root", position_x = 1d, position_y = 2d,
                    position_z = 0d, yaw = .25d
                } };
                Assert.That(SpatialBlendAPI.ApplyActorObservationFrame(space, actor, epoch,
                    0, .1d, frame), Is.True);
                Assert.That(SpatialBlendAPI.TryGetActorObservationClock(space, actor,
                    out var clock), Is.True);
                Assert.That(clock.Epoch, Is.EqualTo(epoch));
                Assert.That(clock.Sequence, Is.EqualTo(0));
                Assert.That(clock.HasSample, Is.True);
            }
            finally
            {
                SpatialBlendAPI.DestroySession(session);
            }
        }
    }
}
#endif
