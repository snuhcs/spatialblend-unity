// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    [Category("FoundationSharedToLocalBinding")]
    public sealed class FoundationSharedToLocalBindingTests
    {
        [Test]
        public void ExplicitChairToSofaBindingPreservesLocalPlacementAndIssuesDetachedReceipt()
        {
            using (var registry = new SharedToLocalBindingRegistry())
            {
                var declaration = Declaration("room-a", 4, "source-chair-seat", 7, 11,
                    Evidence("receiver-space/region-a/sofa/seat", 21, 31, 41));
                Assert.That(registry.TryRegister(declaration, out string error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room-a", 4, "source-chair-seat", 7, 9, 11),
                    out SharedToLocalBindingReceipt receipt, out error), Is.True, error);

                Assert.That(receipt.LocalEndpointId, Is.EqualTo("receiver-space/region-a/sofa/seat"));
                Assert.That(receipt.CounterpartMapRevision, Is.EqualTo(21));
                Assert.That(receipt.LocalBindingGeneration, Is.GreaterThan(0));
                Assert.That(registry.IsCurrent(receipt), Is.True);
                Assert.That(typeof(SharedToLocalBindingReceipt).GetProperties().Select(p => p.PropertyType)
                    .Any(type => type.FullName == "UnityEngine.Transform" ||
                                 type.FullName == "UnityEngine.GameObject"), Is.False,
                    "A receipt cannot carry a remotely placeable Unity object.");
            }
        }

        [Test]
        public void LocalMapSupportAndCalibrationChangesInvalidateBeforeNewGeneration()
        {
            using (var registry = new SharedToLocalBindingRegistry())
            {
                var initial = Declaration("room", 1, "board", 2, 3,
                    Evidence("local-board", 10, 20, 30));
                Assert.That(registry.TryRegister(initial, out string error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 2, 4, 3),
                    out SharedToLocalBindingReceipt old, out error), Is.True, error);

                var mapEdited = Declaration("room", 1, "board", 2, 3,
                    Evidence("local-board", 11, 20, 30));
                Assert.That(registry.TryRegister(mapEdited, out error), Is.True, error);
                Assert.That(registry.IsCurrent(old), Is.False);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 2, 4, 3),
                    out SharedToLocalBindingReceipt afterMap, out error), Is.True, error);
                Assert.That(afterMap.LocalBindingGeneration, Is.GreaterThan(old.LocalBindingGeneration));

                var supportEdited = Declaration("room", 1, "board", 2, 3,
                    Evidence("local-board", 11, 21, 30));
                Assert.That(registry.TryRegister(supportEdited, out error), Is.True, error);
                Assert.That(registry.IsCurrent(afterMap), Is.False);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 2, 4, 3),
                    out SharedToLocalBindingReceipt afterSupport, out error), Is.True, error);

                var calibrationEdited = Declaration("room", 1, "board", 2, 3,
                    Evidence("local-board", 11, 21, 31));
                Assert.That(registry.TryRegister(calibrationEdited, out error), Is.True, error);
                Assert.That(registry.IsCurrent(afterSupport), Is.False);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 2, 4, 3),
                    out SharedToLocalBindingReceipt afterCalibration, out error), Is.True, error);
                Assert.That(afterCalibration.LocalBindingGeneration, Is.GreaterThan(afterSupport.LocalBindingGeneration));
            }
        }

        [Test]
        public void StaleCrossRoomTombstoneUnavailableAndAmbiguousInputsFailClosed()
        {
            using (var registry = new SharedToLocalBindingRegistry())
            {
                var active = Declaration("room", 2, "logical-board", 3, 5,
                    Evidence("receiver-board", 2, 3, 4));
                Assert.That(registry.TryRegister(active, out string error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 2, "logical-board", 3, 8, 5),
                    out SharedToLocalBindingReceipt receipt, out error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 2, "logical-board", 3, 7, 5),
                    out _, out error), Is.False);
                Assert.That(error, Does.Contain("stale"));
                Assert.That(registry.IsCurrent(receipt), Is.True);
                Assert.That(registry.TryResolve(Reference("other-room", 2, "logical-board", 3, 9, 5),
                    out _, out _), Is.False);
                Assert.That(registry.TryResolve(Reference("room", 2, "logical-board", 3, 9, 5,
                    SharedLogicalObjectLifecycle.Tombstoned), out _, out _), Is.False);
                Assert.That(registry.IsCurrent(receipt), Is.False);
                Assert.That(registry.TryResolve(Reference("room", 2, "logical-board", 3, 10, 5),
                    out _, out _), Is.False, "A tombstoned durable generation cannot revive locally.");

                var unavailable = Declaration("room", 2, "other", 3, 5,
                    Evidence("other-endpoint", 2, 3, 4, false));
                Assert.That(registry.TryRegister(unavailable, out error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 2, "other", 3, 1, 5), out _, out _), Is.False);
                var ambiguous = Declaration("room", 2, "third", 3, 5,
                    Evidence("other-endpoint", 7, 8, 9));
                Assert.That(registry.TryRegister(ambiguous, out error), Is.False);
                Assert.That(error, Does.Contain("One local endpoint"));
            }
        }

        [Test]
        public void AuthorityEpochAndRemoteRevisionCannotBeInheritedFromReconnect()
        {
            using (var registry = new SharedToLocalBindingRegistry())
            {
                var declaration = Declaration("room", 1, "board", 1, 7,
                    Evidence("local-board", 1, 1, 1));
                Assert.That(registry.TryRegister(declaration, out string error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 1, 1, 7),
                    out SharedToLocalBindingReceipt prior, out error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 1, 2, 8), out _, out _),
                    Is.False, "A changed room authority must receive a new explicit local declaration.");
                Assert.That(registry.IsCurrent(prior), Is.True,
                    "A rejected reconnect claim cannot mutate the old local capability.");
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 1, 2, 7),
                    out SharedToLocalBindingReceipt newer, out error), Is.True, error);
                Assert.That(registry.IsCurrent(prior), Is.False,
                    "A later committed object revision fences older snapshot/tail receipts.");
                Assert.That(registry.IsCurrent(newer), Is.True);
            }
        }

        [Test]
        public void WorkerCallsAndCallbackFreeReentryCannotRetireOrMutateOwnerBinding()
        {
            using (var registry = new SharedToLocalBindingRegistry())
            {
                var declaration = Declaration("room", 1, "board", 1, 1,
                    Evidence("local-board", 1, 1, 1));
                Assert.That(registry.TryRegister(declaration, out string error), Is.True, error);
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 1, 1, 1),
                    out SharedToLocalBindingReceipt receipt, out error), Is.True, error);
                bool workerResolved = true;
                var worker = new Thread(() => workerResolved = registry.TryResolve(
                    Reference("room", 1, "board", 1, 2, 1), out _, out _));
                worker.Start(); Assert.That(worker.Join(2000), Is.True);
                Assert.That(workerResolved, Is.False);
                Assert.That(registry.IsCurrent(receipt), Is.True);
                Assert.That(typeof(SharedToLocalBindingRegistry).GetEvents(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance), Is.Empty, "The registry has no callback/reentry channel.");
                Assert.That(registry.TryResolve(Reference("room", 1, "board", 1, 2, 1),
                    out SharedToLocalBindingReceipt next, out error), Is.True, error);
                Assert.That(registry.IsCurrent(receipt), Is.False);
                Assert.That(registry.IsCurrent(next), Is.True);
            }
        }

        [Test]
        public void RegistryDoesNotDependOnFusionSocketOrSourceFreshnessTypes()
        {
            var assembly = typeof(SharedToLocalBindingRegistry).Assembly;
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
            Assert.That(references.Any(name => name.IndexOf("Fusion", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
            Assert.That(typeof(SharedToLocalBindingRegistry).GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public).Any(field => field.FieldType.Name.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           field.FieldType.Name.IndexOf("Clock", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
        }

        private static AuthenticatedLogicalObjectReference Reference(string room, ulong roomEpoch, string objectId,
            ulong objectGeneration, ulong objectRevision, ulong authority,
            SharedLogicalObjectLifecycle lifecycle = SharedLogicalObjectLifecycle.Active) =>
            new AuthenticatedLogicalObjectReference(room, roomEpoch, objectId, objectGeneration, objectRevision,
                authority, "board-strokes-v1", lifecycle);

        private static SharedToLocalBindingDeclaration Declaration(string room, ulong roomEpoch, string objectId,
            ulong objectGeneration, ulong authority, LocalBindingEvidence evidence) =>
            new SharedToLocalBindingDeclaration(room, roomEpoch, objectId, objectGeneration, authority,
                "board-strokes-v1", evidence);

        private static LocalBindingEvidence Evidence(string endpoint, ulong map, ulong support, ulong calibration,
            bool available = true) => new LocalBindingEvidence(endpoint, endpoint + "/chart", endpoint + "/support",
                endpoint + "/calibration", map, support, calibration, available);
    }
}
#endif
