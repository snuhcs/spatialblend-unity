// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Native layout and delivery load libspatialblend the same way the other
    // Foundation native fixtures do: DllImport of the release library, which the
    // Foundation gate copies in before the editor starts. Kernel id SB_KERNEL_SPACING
    // = 0 and receiver rows require a core with relation events.
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationRelationEventTests
    {
        private const long RoomShared = 100;
        private const long ObjectShared = 200;
        private const long ActorShared = 300;

        [SetUp]
        public void SetUp()
        {
            SpatialBlendAPI.Initialize(false);
        }

        [TearDown]
        public void TearDown()
        {
            SpatialBlendAPI.CleanupAll();
        }

        [Test]
        public void LayoutMatchesNativeSizeofAndFieldOffsets()
        {
            Assert.That(SpatialBlendAPI.TryValidateRelationAbi(out string error), Is.True, error);
        }

        [Test]
        public void RegisterAndUnregisterArePerSpace()
        {
            long session = SpatialBlendAPI.CreateSession();
            long first = SpatialBlendAPI.AddSpace(session);
            long second = SpatialBlendAPI.AddSpace(session);
            long firstActor = BuildSpacingScene(first, RoomShared, ObjectShared, ActorShared);
            long secondActor = BuildSpacingScene(second, RoomShared + 1, ObjectShared + 1, ActorShared + 1);
            int firstCalls = 0;
            int secondCalls = 0;
            SpatialBlendAPI.RegisterOnRelationEvent(first, (_, __) => firstCalls++);
            SpatialBlendAPI.RegisterOnRelationEvent(second, (_, __) => secondCalls++);

            Approach(first, firstActor);
            Assert.That(firstCalls, Is.GreaterThan(0));
            int firstAfter = firstCalls;
            int secondAfter = secondCalls;

            SpatialBlendAPI.UnregisterOnRelationEvent(first);
            Approach(first, firstActor);
            Approach(second, secondActor);
            Assert.That(firstCalls, Is.EqualTo(firstAfter), "An unregistered space must not deliver.");
            Assert.That(secondCalls, Is.GreaterThan(secondAfter), "Another space keeps its callback.");

            SpatialBlendAPI.UnregisterOnRelationEvent(second);
            int secondStopped = secondCalls;
            Approach(second, secondActor);
            Assert.That(secondCalls, Is.EqualTo(secondStopped));
        }

        [Test]
        public void LocalSpacingApproachDeliversPhaseAndKernel()
        {
            long session = SpatialBlendAPI.CreateSession();
            long space = SpatialBlendAPI.AddSpace(session);
            var events = new List<RelationEvent>();
            SpatialBlendAPI.RegisterOnRelationEvent(space, (_, relationEvent) => events.Add(relationEvent));
            long actor = BuildSpacingScene(space, RoomShared, ObjectShared, ActorShared);
            Approach(space, actor);

            Assert.That(events, Is.Not.Empty);
            Assert.That(events, Has.Some.Matches<RelationEvent>(relationEvent =>
                relationEvent.Kernel == SbKernelType.Spacing &&
                relationEvent.FromPhase == SbRelationPhase.Dormant &&
                relationEvent.ToPhase >= SbRelationPhase.Active &&
                relationEvent.ReferentSharedId == ObjectShared));

            var rows = new List<RelationStateV2Row>();
            int count = SpatialBlendAPI.GetRelationStatesV2(space, actor, rows);
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(rows, Has.Some.Matches<RelationStateV2Row>(row =>
                row.Kernel == SbKernelType.Spacing && row.ReferentSharedId == ObjectShared));
        }

        [Test]
        public void DomainReloadGuardDropsRelationCallback()
        {
            long session = SpatialBlendAPI.CreateSession();
            long space = SpatialBlendAPI.AddSpace(session);
            int calls = 0;
            SpatialBlendAPI.RegisterOnRelationEvent(space, (_, __) => calls++);
            Assert.That(SpatialBlendAPI.HasLiveNativeState, Is.True);

            SpatialBlendDomainReloadGuard.TearDown();

            Assert.That(SpatialBlendAPI.HasLiveNativeState, Is.False);
            Assert.That(calls, Is.EqualTo(0), "Teardown itself must not deliver a stale callback.");
            var callbacks = typeof(SpatialBlendAPI).GetField("relationEventCallbacks",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var map = (IDictionary)callbacks.GetValue(null);
            Assert.That(map.Count, Is.EqualTo(0));
        }

        private static long BuildSpacingScene(long space, long roomShared, long objectShared, long actorShared)
        {
            long room = SpatialBlendAPI.AddElement(space);
            SpatialBlendAPI.SetElementSharedId(space, room, roomShared);
            Square(space, room, SbGeometryRole.Navigable, 0, 0, 100, 100);
            long obj = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, obj, objectShared);
            SpatialBlendAPI.SetElementTag(space, obj, "Object");
            Square(space, obj, SbGeometryRole.Containment, 97, 97, 99.5, 99.5);
            long actorElement = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, actorElement, actorShared);
            long actor = SpatialBlendAPI.CreateActor(space);
            SpatialBlendAPI.AddAnchor(space, actor, "root", Vector3.zero);
            SpatialBlendAPI.SetActorElement(space, actor, actorElement);
            Assert.That(SpatialBlendAPI.Bind(space, actor, "root", SbKernelType.Spacing), Is.True);
            return actor;
        }

        // Local updates deliver the phase callback before returning, the same
        // way relation_event_c_test.cc drives a source pose.
        private static void Approach(long space, long actor)
        {
            SpatialBlendAPI.UpdateActorState(space, actor, new Vector3(1f, 0f, 1f), 0f);
            for (int step = 1; step <= 8; ++step)
            {
                float t = step / 8f;
                SpatialBlendAPI.UpdateActorState(space, actor,
                    new Vector3(Mathf.Lerp(1f, 98.2f, t), 0f, Mathf.Lerp(1f, 98.2f, t)), 0f);
            }
        }

        private static void Square(long space, long element, SbGeometryRole role,
                                   double x0, double y0, double x1, double y1)
        {
            SpatialBlendAPI.SetElementPolygon(space, element, role, new[]
            {
                new Vector2((float)x0, (float)y0),
                new Vector2((float)x1, (float)y0),
                new Vector2((float)x1, (float)y1),
                new Vector2((float)x0, (float)y1)
            });
        }
    }
}
#endif
