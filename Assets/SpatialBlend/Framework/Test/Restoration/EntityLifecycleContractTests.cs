// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class EntityLifecycleContractTests
    {
        private sealed class TrackingEntity : SbLocalEntity
        {
            public int RootCancellationCount { get; private set; }

            public override void CancelRealizationTarget()
            {
                RootCancellationCount++;
                base.CancelRealizationTarget();
            }
        }

        [Test]
        public void Detach_ReleasesAnchorAndRootAndClearsBatchCallbackIdempotently()
        {
            var gameObject = new GameObject("entity-lifecycle");
            var entity = gameObject.AddComponent<TrackingEntity>();
            var controller = gameObject.AddComponent<AnchorTargetController>();
            int batchCallbacks = 0;
            entity.SetOnUpdateStateBatchCallback((_, __) => batchCallbacks++);
            controller.ApplyTarget("left_hand", Vector3.one, 0f, true, 1d, 1d);
            Assert.That(controller.TryGetTarget("left_hand", out _), Is.True);
            try
            {
                InvokeInternal(entity, "Detach");
                Assert.That(controller.TryGetTarget("left_hand", out _), Is.False);
                Assert.That(entity.RootCancellationCount, Is.EqualTo(1));

                InvokeInternal(entity, "OnUpdateStateBatch", ProximityType.Euclidean3D,
                               new List<SpatialBlend.Internal.StateUpdateBatchItem>());
                Assert.That(batchCallbacks, Is.EqualTo(0),
                    "Detach must clear the batch callback with the other managed callbacks.");

                InvokeInternal(entity, "Detach");
                Assert.That(entity.RootCancellationCount, Is.EqualTo(1),
                    "A second detached cleanup must not re-release the old root target.");
                Assert.That(controller.TryGetTarget("left_hand", out _), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void OnDestroy_UsesTheSameAnchorAndRootCleanupPath()
        {
            var gameObject = new GameObject("entity-destroy-lifecycle");
            var entity = gameObject.AddComponent<TrackingEntity>();
            var controller = gameObject.AddComponent<AnchorTargetController>();
            controller.ApplyTarget("pelvis", Vector3.zero, 0f, true, 1d, 1d);
            try
            {
                entity.OnDestroy();
                Assert.That(controller.TryGetTarget("pelvis", out _), Is.False);
                Assert.That(entity.RootCancellationCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ReleaseAllTargets_LeavesAReentrantNewTargetActive()
        {
            var gameObject = new GameObject("anchor-controller-release");
            var controller = gameObject.AddComponent<AnchorTargetController>();
            int releases = 0;
            controller.TargetReleased += anchor =>
            {
                releases++;
                if (anchor == "old")
                {
                    controller.ApplyTarget("new", Vector3.right, 0f, true, 1d, 2d);
                }
            };
            try
            {
                controller.ApplyTarget("old", Vector3.zero, 0f, true, 1d, 1d);
                controller.ReleaseAllTargets();

                Assert.That(releases, Is.EqualTo(1));
                Assert.That(controller.TryGetTarget("old", out _), Is.False);
                Assert.That(controller.TryGetTarget("new", out _), Is.True,
                    "ReleaseAllTargets releases only the targets active at its entry.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ReleaseAllTargets_PreservesReplacementOfAnotherEntryKey()
        {
            var gameObject = new GameObject("anchor-release-same-key");
            var controller = gameObject.AddComponent<AnchorTargetController>();
            int releases = 0;
            controller.TargetReleased += _ =>
            {
                if (++releases == 1)
                {
                    // Whichever entry is notified first, both entry keys get new
                    // commands. Old release work must not retire either replacement.
                    controller.ApplyTarget("left", Vector3.left, 0f, true, 1d, 2d);
                    controller.ApplyTarget("right", Vector3.right, 0f, true, 1d, 2d);
                }
            };
            try
            {
                controller.ApplyTarget("left", Vector3.zero, 0f, true, 1d, 1d);
                controller.ApplyTarget("right", Vector3.zero, 0f, true, 1d, 1d);
                controller.ReleaseAllTargets();
                Assert.That(releases, Is.EqualTo(2));
                foreach (var anchor in new[] { "left", "right" })
                {
                    Assert.That(controller.TryGetTarget(anchor, out var target), Is.True);
                    Assert.That(target.UpdatedAt, Is.EqualTo(2d));
                    Assert.That(controller.GetGoal(anchor).gameObject.activeSelf, Is.True);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Detach_ReleaseObserverCanAttachNewOwnerWithoutOldRootCancellation()
        {
            // Plain EditMode MonoBehaviours do not dispatch Awake. Exercise the
            // production Attach/Detach map seams without creating a native session.
            var oldOwnerObject = new GameObject("old-owner");
            var newOwnerObject = new GameObject("new-owner");
            var gameObject = new GameObject("reattached-entity");
            var oldOwner = oldOwnerObject.AddComponent<SpatialBlendManager>();
            var newOwner = newOwnerObject.AddComponent<SpatialBlendManager>();
            var entity = gameObject.AddComponent<TrackingEntity>();
            var controller = gameObject.AddComponent<AnchorTargetController>();
            bool reattached = false;
            controller.TargetReleased += _ =>
            {
                if (reattached) return;
                reattached = true;
                InvokeInternal(entity, "Attach", newOwner, 202L, false);
                controller.ApplyTarget("hand", Vector3.right, 0f, true, 1d, 2d);
            };
            try
            {
                InvokeInternal(entity, "Attach", oldOwner, 101L, false);
                controller.ApplyTarget("hand", Vector3.zero, 0f, true, 1d, 1d);
                InvokeInternal(entity, "Detach");
                Assert.That(reattached, Is.True);
                Assert.That(entity.ID, Is.EqualTo(202L));
                Assert.That(entity.spaceManager, Is.SameAs(newOwner));
                Assert.That(oldOwner.GetEntity(101L), Is.Null);
                Assert.That(newOwner.GetEntity(202L), Is.SameAs(entity));
                Assert.That(entity.RootCancellationCount, Is.Zero,
                    "Old teardown must not cancel a reattached entity's new root control.");
                Assert.That(controller.TryGetTarget("hand", out var target), Is.True);
                Assert.That(target.UpdatedAt, Is.EqualTo(2d));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(oldOwnerObject);
                UnityEngine.Object.DestroyImmediate(newOwnerObject);
            }
        }

        [Test]
        public void Attach_ToRetiredManagerRejectsBeforeChangingIdentity()
        {
            var ownerObject = new GameObject("retired-owner");
            var gameObject = new GameObject("unattached-entity");
            var owner = ownerObject.AddComponent<SpatialBlendManager>();
            var entity = gameObject.AddComponent<TrackingEntity>();
            try
            {
                typeof(SpatialBlendManager).GetMethod("BeginManagedTeardown",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, null);
                var error = Assert.Throws<TargetInvocationException>(() =>
                    InvokeInternal(entity, "Attach", owner, 303L, false));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(entity.IsAttached(), Is.False);
                Assert.That(entity.spaceManager, Is.Null);
                Assert.That(entity.ID, Is.EqualTo(-1L));
                Assert.That(owner.GetEntity(303L), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(ownerObject);
            }
        }

        private static void InvokeInternal(object instance, string name, params object[] arguments)
        {
            var method = typeof(SbEntity).GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing production method {name}.");
            method.Invoke(instance, arguments);
        }
    }
}
#endif
