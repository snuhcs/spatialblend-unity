// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationObservedCollaborator")]
    public sealed partial class FoundationObservedCollaboratorNativeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        [SetUp] public void SetUp() => _world = Create<SpatialBlendLocalWorld>("observed-source-point-world");
        [TearDown]
        public void TearDown()
        {
            _world.Shutdown();
            for (int index = _created.Count - 1; index >= 0; --index)
                UnityEngine.Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void PassivePointDeclarationLeavesExistingLegacySourceWriterUsable()
        {
            Fixture fixture = CreateFixture();
            Assert.That(fixture.Source.TryCreateMotionInput(out IAnchorPoseFrameSink legacy), Is.True);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out var declaration, out string error), Is.True, error);
            Assert.That(declaration.Point, Is.EqualTo(fixture.Point.Registration));
            Assert.That(declaration.Actor, Is.EqualTo(fixture.Source.Registration));
            Assert.That(declaration.AnchorId, Is.EqualTo("head"));
            Assert.That(declaration.MaximumAgeNanoseconds, Is.EqualTo(1000000000L));
            Assert.That(legacy.TryApplyWorldFrame(Frame(1, .1)), Is.True,
                "Reading a passive C1 declaration must not replace the legacy epoch.");
        }

        [Test]
        public void RejectsActorOwnerGeometryWrongAnchorCoreAgeChildAndActorAncestor()
        {
            Fixture fixture = CreateFixture();
            fixture.Component.Configure(fixture.Owner, fixture.Source, "head", 1000000000L);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            SpatialBlendObject geometry = Object("source", "geometry-point", true);
            Assert.That(geometry.TryRegister(out string error), Is.True, error);
            fixture.Component.Configure(geometry, fixture.Source, "head", 1000000000L);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            fixture.Component.Configure(fixture.Point, fixture.Source, "unknown-anchor", 1000000000L);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            fixture.Component.Configure(fixture.Point, fixture.Source, "head",
                SpatialBlendLocalWorld.ObservedSourceAnchorPointMaximumAgeNanoseconds + 1);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            SpatialBlendObject child = Create<SpatialBlendObject>("point-child");
            child.Configure(_world, "source", "point-child", fixture.Point, SpatialBlendObjectKind.Affordance);
            Assert.That(child.TrySetColliderGeometryEnabled(false, out error), Is.True, error);
            Assert.That(child.TrySetSupportPolygon(new[]
            {
                Vector3.zero, Vector3.right, Vector3.forward
            }, out error), Is.True, error);
            Assert.That(child.TryRegister(out error), Is.True, error);
            fixture.Component.Configure(fixture.Point, fixture.Source, "head", 1000000000L);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            child.Unregister();
            SpatialBlendObject descendant = Create<SpatialBlendObject>("actor-descendant");
            descendant.Configure(_world, "source", "actor-descendant", fixture.Owner, SpatialBlendObjectKind.Affordance);
            Assert.That(descendant.TrySetColliderGeometryEnabled(false, out error), Is.True, error);
            Assert.That(descendant.TrySetSupportPolygon(new[]
            {
                Vector3.zero, Vector3.right, Vector3.forward
            }, out error), Is.True, error);
            Assert.That(descendant.TryRegister(out error), Is.True, error);
            fixture.Component.Configure(descendant, fixture.Source, "head", 1000000000L);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
        }

        [Test]
        public void ActorPointAndWorldReplacementMakeCapturedDeclarationStale()
        {
            Fixture fixture = CreateFixture();
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out var initial, out string error), Is.True, error);
            fixture.Source.Unregister();
            Assert.That(fixture.Component.TryRevalidateDeclaration(initial, out _), Is.False);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            Assert.That(fixture.Source.TryRegister(out error), Is.True, error);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out var afterActor, out error), Is.True, error);
            Assert.That(afterActor.Actor, Is.Not.EqualTo(initial.Actor));
            fixture.Point.Unregister();
            Assert.That(fixture.Component.TryRevalidateDeclaration(afterActor, out _), Is.False);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            Assert.That(fixture.Point.TryRegister(out error), Is.True, error);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out var beforeShutdown, out error), Is.True, error);
            _world.Shutdown();
            Assert.That(fixture.Component.TryRevalidateDeclaration(beforeShutdown, out _), Is.False);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
            Assert.That(fixture.Owner.TryRegister(out error), Is.True, error);
            Assert.That(fixture.Point.TryRegister(out error), Is.True, error);
            Assert.That(fixture.Source.TryRegister(out error), Is.True, error);
            Assert.That(fixture.Component.TryGetCurrentDeclaration(out var afterShutdown, out error), Is.True, error);
            Assert.That(afterShutdown.Generation, Is.Not.EqualTo(beforeShutdown.Generation));
        }

        [Test]
        public void AtomicC1SetupFencesLegacyWritersAndTruthfullyRetiresEveryActor()
        {
            Fixture first = CreateFixture("c1-first");
            Fixture second = CreateFixture("c1-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            Assert.That(setup.NativeReceipt.Actors.Count, Is.EqualTo(2));
            Assert.That(setup.NativeReceipt.Points.Count, Is.EqualTo(2));
            Assert.That(first.Source.TryCreateAnchorPoseInput(out _), Is.False);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.False);
            Assert.That(second.Source.TryCreateAnchorPoseInput(out _), Is.False);
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.False);

            Assert.That(_world.TryRetireObservedSourceSetup(setup, out error), Is.True, error);
            Assert.That(_world.TryRetireObservedSourceSetup(setup, out _), Is.False);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True);
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.True);
        }

        [Test]
        public void AtomicC1SetupExplicitlyTransitionsLiveLegacyWritersWithoutStaleSinkAdmission()
        {
            Fixture first = CreateFixture("c1-legacy-first");
            Fixture second = CreateFixture("c1-legacy-second");
            Assert.That(first.Source.TryCreateMotionInput(out IAnchorPoseFrameSink firstLegacy), Is.True);
            Assert.That(second.Source.TryCreateMotionInput(out IAnchorPoseFrameSink secondLegacy), Is.True);
            var retired = new List<CommittedMotionObservation>();
            Assert.That(first.Source.TrySubscribeCommittedMotion(value => retired.Add(value), out var observer), Is.True);
            Assert.That(firstLegacy.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(secondLegacy.TryApplyWorldFrame(Frame(0, 0d)), Is.True);

            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            // The first committed source frame remains observable; C1 adds
            // exactly one terminal event for the writer it replaced.
            CommittedMotionObservation[] writerRetirements = retired
                .Where(value => value.Kind == CommittedMotionObservationKind.Retired).ToArray();
            Assert.That(writerRetirements, Has.Length.EqualTo(1));
            Assert.That(writerRetirements[0].RetirementReason,
                Is.EqualTo(CommittedMotionRetirementReason.WriterReplaced));
            Assert.That(firstLegacy.TryApplyWorldFrame(Frame(1, .1d)), Is.False,
                "The old source sink must not admit a frame after its exact epoch moved into C1.");
            Assert.That(secondLegacy.TryApplyWorldFrame(Frame(1, .1d)), Is.False);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.False,
                "C1 guards remain active until its explicit setup lifetime ends.");

            Assert.That(_world.TryRetireObservedSourceSetup(setup, out error), Is.True, error);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True,
                "A later legacy writer owns a fresh epoch after truthful C1 retirement.");
            observer.Dispose();
        }

        [Test]
        public void InvalidLateC1PointDoesNotChangeEarlierActorLegacyAvailability()
        {
            Fixture first = CreateFixture("c1-valid");
            Fixture second = CreateFixture("c1-invalid");
            // An actor owner cannot be a source point; it is a late member so
            // this proves framework preflight has not opened first's epoch.
            second.Component.Configure(second.Owner, second.Source, "head", 1000000000L);
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out _), Is.False);
            Assert.That(setup, Is.Null);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True);
        }

        [Test]
        public void AdmittedC1SetupCanReleaseButCannotClaimCancellation()
        {
            Fixture first = CreateFixture("c1-admitted-first");
            Fixture second = CreateFixture("c1-admitted-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            Assert.That(SpatialBlendAPI.TryReadRichMonotonicNanoseconds(out ulong now, out _, out _, out error), Is.True, error);
            var frames = new List<SpatialBlendAPI.ObservedCollaboratorFrameInput>();
            foreach (var actor in setup.NativeReceipt.Actors)
                frames.Add(new SpatialBlendAPI.ObservedCollaboratorFrameInput(actor.Actor, actor.Epoch,
                    0, 0d, new[]
                    {
                        new SpatialBlendAPI.SbActorObservation { anchor_name = "root", position_x = 0d, position_y = 0d, position_z = 0d, yaw = 0d },
                        new SpatialBlendAPI.SbActorObservation { anchor_name = "head", position_x = 0d, position_y = 0d, position_z = 1.7d, yaw = 0d }
                    }));
            var publications = new List<SpatialBlendAPI.ObservedCollaboratorPublicationInput>();
            foreach (var point in setup.NativeReceipt.Points)
                publications.Add(new SpatialBlendAPI.ObservedCollaboratorPublicationInput(point.Element,
                    point.Lease, (long)now, (long)now + 1000000000L));
            Assert.That(SpatialBlendAPI.TryApplyObservedCollaboratorBatch(
                first.Source.Registration.NativeSpace, frames, publications, out error), Is.True, error);
            Assert.That(_world.TryRetireObservedSourceSetup(setup, out _), Is.False,
                "Admitted evidence may not be reported as a cancellation.");
            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out error), Is.True, error);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True,
                "Release is an explicit transition; the later writer owns a new legacy epoch.");
        }

        [Test]
        public void UnregisteringOneUntouchedC1ActorRetiresTheWholeSetupBeforeNativeActorDestroy()
        {
            Fixture first = CreateFixture("c1-retire-first");
            Fixture second = CreateFixture("c1-retire-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            first.Source.Unregister();
            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out _), Is.False,
                "Actor teardown already retired and released the untouched setup capability.");
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.True,
                "The surviving actor must not retain a stale C1 local ownership guard.");
        }

        [Test]
        public void ReconfiguringOrUnregisteringAC1PointEndsTheWholeSetupBeforeItsDeclarationChanges()
        {
            Fixture first = CreateFixture("c1-point-first");
            Fixture second = CreateFixture("c1-point-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var reconfigured, out string error), Is.True, error);
            first.Component.Configure(first.Point, first.Source, "root", 500000000L);
            Assert.That(_world.TryReleaseObservedSourceSetup(reconfigured, out _), Is.False,
                "Reconfiguration must already have retired/released the old setup capability.");
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True);
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.True);

            Fixture third = CreateFixture("c1-point-third");
            Fixture fourth = CreateFixture("c1-point-fourth");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { third.Component, fourth.Component },
                out var unregistered, out error), Is.True, error);
            third.Point.Unregister();
            Assert.That(_world.TryReleaseObservedSourceSetup(unregistered, out _), Is.False,
                "Point removal must already have retired/released the old setup capability.");
            Assert.That(fourth.Source.TryCreateMotionInput(out _), Is.True,
                "A surviving participant must not retain a stale C1 local ownership guard.");
        }

        [Test]
        public void DuplicateActorOrPointDeclarationsRejectBeforeAnyC1EpochOrLeaseMutation()
        {
            Fixture fixture = CreateFixture("c1-duplicate");
            SpatialBlendObject secondPoint = Object("source", "c1-duplicate-second-point", false);
            Assert.That(secondPoint.TryRegister(out string error), Is.True, error);
            ObservedSourceAnchorPoint duplicateActor = Create<ObservedSourceAnchorPoint>("c1-duplicate-component");
            duplicateActor.Configure(secondPoint, fixture.Source, "root", 1000000000L);
            Assert.That(_world.TryBeginObservedSourceSetup(
                new[] { fixture.Component, duplicateActor }, out var setup, out _), Is.False);
            Assert.That(setup, Is.Null);
            Assert.That(fixture.Source.TryCreateMotionInput(out _), Is.True,
                "Duplicate declarations must be rejected before the first actor changes mode.");

            Fixture distinct = CreateFixture("c1-duplicate-point-other");
            ObservedSourceAnchorPoint duplicatePoint = Create<ObservedSourceAnchorPoint>("c1-duplicate-point-component");
            duplicatePoint.Configure(fixture.Point, distinct.Source, "head", 1000000000L);
            Assert.That(_world.TryBeginObservedSourceSetup(
                new[] { fixture.Component, duplicatePoint }, out setup, out _), Is.False);
            Assert.That(setup, Is.Null);
            Assert.That(distinct.Source.TryCreateMotionInput(out _), Is.True);
        }

        [Test]
        public void CrossWorldDeclarationRejectsBeforeTheLocalActorChangesC1Mode()
        {
            Fixture local = CreateFixture("c1-local-world");
            SpatialBlendLocalWorld foreignWorld = Create<SpatialBlendLocalWorld>("c1-foreign-world");
            SpatialBlendObject foreignOwner = Create<SpatialBlendObject>("c1-foreign-owner");
            foreignOwner.Configure(foreignWorld, "foreign", "c1-foreign-owner", null,
                SpatialBlendObjectKind.Object);
            Assert.That(foreignOwner.TrySetColliderGeometryEnabled(false, out string error), Is.True, error);
            Assert.That(foreignOwner.TryRegister(out error), Is.True, error);
            SpatialBlendObject foreignPoint = Create<SpatialBlendObject>("c1-foreign-point");
            foreignPoint.Configure(foreignWorld, "foreign", "c1-foreign-point", null,
                SpatialBlendObjectKind.Object);
            Assert.That(foreignPoint.TrySetColliderGeometryEnabled(false, out error), Is.True, error);
            Assert.That(foreignPoint.TryRegister(out error), Is.True, error);
            SpatialBlendActor foreignSource = Create<SpatialBlendActor>("c1-foreign-source");
            foreignSource.Configure(foreignOwner, "c1-foreign-source", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("head", SpatialBlendActorAnchorRole.Body, Vector3.up)
            });
            Assert.That(foreignSource.TryRegister(out error), Is.True, error);
            ObservedSourceAnchorPoint foreign = Create<ObservedSourceAnchorPoint>("c1-foreign-component");
            foreign.Configure(foreignPoint, foreignSource, "head", 1000000000L);

            Assert.That(_world.TryBeginObservedSourceSetup(new[] { local.Component, foreign },
                out var setup, out _), Is.False);
            Assert.That(setup, Is.Null);
            Assert.That(local.Source.TryCreateMotionInput(out _), Is.True,
                "A foreign declaration must fail before the local actor receives a C1 epoch.");
            foreignWorld.Shutdown();
        }

        [Test]
        public void DestroyingPassivePointComponentEndsItsSelectedSetupWithoutCreatingAnother()
        {
            Fixture first = CreateFixture("c1-component-destroy-first");
            Fixture second = CreateFixture("c1-component-destroy-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);

            Destroy(first.Component);

            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out _), Is.False,
                "Passive component destruction must already retire/release the selected setup capability.");
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.True,
                "The surviving actor must not retain a C1 ownership guard after declaration destruction.");
        }

        [Test]
        public void WorldShutdownForgetsAnActiveC1SetupBeforeDroppingItsNativeSession()
        {
            Fixture first = CreateFixture("c1-shutdown-first");
            Fixture second = CreateFixture("c1-shutdown-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);

            _world.Shutdown();

            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out _), Is.False,
                "Shutdown owns the terminal native session cleanup and must forget its old setup lease.");
            Assert.That(first.Source.IsRegistered, Is.False);
            Assert.That(second.Source.IsRegistered, Is.False);
            Assert.That(first.Component.TryGetCurrentDeclaration(out _, out _), Is.False);
        }

        [Test]
        public void PreSolveCoordinatorCommitsOneDetachedTwoActorBatchWithoutLegacyFallback()
        {
            Fixture first = CreateFixture("c1-coordinator-first");
            Fixture second = CreateFixture("c1-coordinator-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            var participants = new Dictionary<string, IReadOnlyCollection<string>>
            {
                { first.Source.ActorId, new[] { "root", "head" } },
                { second.Source.ActorId, new[] { "root", "head" } }
            };
            using (var coordinator = new ObservedCollaboratorPreSolveCoordinator(setup, participants))
            {
                AnchorPoseFrame firstFrame = Frame(0, 0d);
                AnchorPoseFrame secondFrame = Frame(0, .01d);
                Assert.That(coordinator.TryStage(42, first.Source.ActorId, firstFrame, out error),
                    Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(coordinator.TryStage(42, second.Source.ActorId, secondFrame, out error),
                    Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                // The barrier owns detached input. A post-stage mutation cannot
                // change the one native call or its managed notification.
                firstFrame.anchors[1].worldPosition = new Vector3(99f, 99f, 99f);
                Assert.That(coordinator.TryCommitReady(out var accepted, out error), Is.True, error);
                Assert.That(accepted.BatchId, Is.EqualTo(42));
                Assert.That(accepted.Frames[first.Source.ActorId].Anchors[1].WorldPosition,
                    Is.EqualTo(Vector3.up));
                // Accepted values are a separate immutable projection: neither
                // callers nor mutable AnchorPoseFrame/AnchorPoseSample DTOs can
                // alter the post-success notification.
                Vector3 consumerCopy = accepted.Frames[first.Source.ActorId].Anchors[1].WorldPosition;
                consumerCopy.x = 77f;
                Assert.That(accepted.Frames[first.Source.ActorId].Anchors[1].WorldPosition,
                    Is.EqualTo(Vector3.up));
                foreach (var actor in setup.NativeReceipt.Actors)
                {
                    Assert.That(SpatialBlendAPI.TryGetActorObservationClock(
                        first.Source.Registration.NativeSpace, actor.Actor, out var clock), Is.True);
                    Assert.That(clock.Epoch, Is.EqualTo(actor.Epoch));
                    Assert.That(clock.Sequence, Is.EqualTo(0));
                    Assert.That(clock.HasSample, Is.True);
                }
                Assert.That(first.Source.TryCreateMotionInput(out _), Is.False,
                    "C1 owns the actor until the setup lifecycle explicitly releases it.");
            }
            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out error), Is.True, error);
        }

        [Test]
        public void PreSolveCoordinatorFailureAbortsBeforeAnyNativeActorFrameAdmission()
        {
            Fixture first = CreateFixture("c1-coordinator-failure-first");
            Fixture second = CreateFixture("c1-coordinator-failure-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            var participants = new Dictionary<string, IReadOnlyCollection<string>>
            {
                { first.Source.ActorId, new[] { "root", "head" } },
                { second.Source.ActorId, new[] { "root", "head" } }
            };
            using (var coordinator = new ObservedCollaboratorPreSolveCoordinator(setup, participants))
            {
                Assert.That(coordinator.TryStage(7, first.Source.ActorId, Frame(0, 0d), out error),
                    Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(coordinator.TryStage(7, first.Source.ActorId, Frame(0, .01d), out _),
                    Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
                Assert.That(coordinator.TryCommitReady(out _, out _), Is.False);
            }
            foreach (var actor in setup.NativeReceipt.Actors)
            {
                Assert.That(SpatialBlendAPI.TryGetActorObservationClock(
                    first.Source.Registration.NativeSpace, actor.Actor, out var clock), Is.True);
                Assert.That(clock.Epoch, Is.EqualTo(actor.Epoch));
                Assert.That(clock.Sequence, Is.EqualTo(-1));
                Assert.That(clock.HasSample, Is.False);
            }
            Assert.That(_world.TryRetireObservedSourceSetup(setup, out error), Is.True, error);
        }

        [Test]
        public void CoordinatorWorkerRejectionCannotPublishAnAcceptedBatchOrAdvanceNativeClocks()
        {
            Fixture first = CreateFixture("c1-worker-first");
            Fixture second = CreateFixture("c1-worker-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            var participants = new Dictionary<string, IReadOnlyCollection<string>>
            {
                { first.Source.ActorId, new[] { "root", "head" } },
                { second.Source.ActorId, new[] { "root", "head" } }
            };
            using (var coordinator = new ObservedCollaboratorPreSolveCoordinator(setup, participants))
            {
                ObservedCollaboratorStageResult worker = Task.Run(() => coordinator.TryStage(
                    11, first.Source.ActorId, Frame(0, 0d), out _)).Result;
                Assert.That(worker, Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
                Assert.That(coordinator.TryCommitReady(out var accepted, out _), Is.False);
                Assert.That(accepted, Is.Null);
            }
            foreach (var actor in setup.NativeReceipt.Actors)
            {
                Assert.That(SpatialBlendAPI.TryGetActorObservationClock(
                    first.Source.Registration.NativeSpace, actor.Actor, out var clock), Is.True);
                Assert.That(clock.Sequence, Is.EqualTo(-1));
                Assert.That(clock.HasSample, Is.False);
            }
            Assert.That(_world.TryRetireObservedSourceSetup(setup, out error), Is.True, error);
        }

        [Test]
        public void StaleSetupCapabilityReleasesWithoutClaimingRetirementOrLeavingActorGuards()
        {
            Fixture first = CreateFixture("c1-stale-first");
            Fixture second = CreateFixture("c1-stale-second");
            Assert.That(_world.TryBeginObservedSourceSetup(new[] { first.Component, second.Component },
                out var setup, out string error), Is.True, error);
            // A direct legacy epoch replaces Core's setup-owned epoch. This is
            // terminal/stale, not a truthful retirement and it has admitted no
            // C1 frame/publication.
            ulong replacement = SpatialBlendAPI.BeginActorObservationEpoch(
                first.Source.Registration.NativeSpace, first.Source.Registration.NativeActor, "root");
            Assert.That(replacement, Is.GreaterThan(0));
            Assert.That(_world.TryRetireObservedSourceSetup(setup, out _), Is.False);
            Assert.That(_world.TryReleaseObservedSourceSetup(setup, out error), Is.True, error);
            Assert.That(first.Source.TryCreateMotionInput(out _), Is.True);
            Assert.That(second.Source.TryCreateMotionInput(out _), Is.True);
        }

        private Fixture CreateFixture(string prefix = "source")
        {
            SpatialBlendObject owner = Object("source", prefix + "-owner", false);
            SpatialBlendObject point = Object("source", prefix + "-head-point", false);
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            Assert.That(point.TryRegister(out error), Is.True, error);
            SpatialBlendActor source = Create<SpatialBlendActor>(prefix + "-actor");
            source.Configure(owner, prefix + "-actor", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("head", SpatialBlendActorAnchorRole.Body, new Vector3(0f, 1.7f, 0f))
            });
            Assert.That(source.TryRegister(out error), Is.True, error);
            ObservedSourceAnchorPoint component = Create<ObservedSourceAnchorPoint>(prefix + "-head-component");
            component.Configure(point, source, "head", 1000000000L);
            return new Fixture { Owner = owner, Point = point, Source = source, Component = component };
        }

        private SpatialBlendObject Object(string space, string id, bool geometryEnabled, SpatialBlendObject parent = null)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, parent, SpatialBlendObjectKind.Object);
            Assert.That(value.TrySetColliderGeometryEnabled(geometryEnabled, out string error), Is.True, error);
            return value;
        }

        private static AnchorPoseFrame Frame(long sequence, double time) => new AnchorPoseFrame
        {
            sequence = sequence, timeSeconds = time,
            anchors = new[]
            {
                new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero, worldRotation = Quaternion.identity },
                new AnchorPoseSample { anchorId = "head", worldPosition = Vector3.up, worldRotation = Quaternion.identity }
            }
        };

        private T Create<T>(string name) where T : Component
        {
            var gameObject = new GameObject(name); _created.Add(gameObject);
            T value = gameObject.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null);
            return value;
        }

        // This fixture creates components without ever routing them through
        // Unity's own scene-activation pipeline (see the explicit Awake
        // invocation above), so Unity never dispatches OnDisable/OnDestroy for
        // them either. DestroyImmediate alone cannot exercise a component's
        // destruction-triggered cleanup here; invoke the same messages Unity
        // would send a live, previously-active component being destroyed.
        private static void Destroy(Component component)
        {
            Type type = component.GetType();
            type.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            type.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            UnityEngine.Object.DestroyImmediate(component.gameObject);
        }

        private sealed class Fixture
        {
            internal SpatialBlendObject Owner, Point;
            internal SpatialBlendActor Source;
            internal ObservedSourceAnchorPoint Component;
        }
    }
}
#endif
