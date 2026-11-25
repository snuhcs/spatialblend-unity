// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationObservedCollaborator")]
    public sealed class FoundationObservedCollaboratorTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        private SpatialBlendObject _sourceBoard;
        private SpatialBlendObject _targetBoard;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("observed-collaborator-world");
            _sourceBoard = Object("source", "source-board");
            _targetBoard = Object("target", "target-board");
        }

        [TearDown]
        public void TearDown()
        {
            _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i)
                UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        [Test]
        public void NativeBatchOnlyIssuesBoundedReadOnlyLeaseForIndependentActors()
        {
            var batch = Batch("source", _sourceBoard.Registration.NativeSpace, 7, 17, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("bob", 22, 222, 2222, "head", 2));

            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(batch, "alice", "head",
                "bob", "head", 150, null, out var lease, out string error), Is.True, error);
            Assert.That(lease.Batch, Is.SameAs(batch));
            Assert.That(lease.First.CollaboratorId, Is.EqualTo("alice"));
            Assert.That(lease.Second.CollaboratorId, Is.EqualTo("bob"));
            Assert.That(lease.TryRevalidate(200, out error), Is.True, error);
            Assert.That(lease.TryRevalidate(201, out _), Is.False,
                "A retained batch must not receive an indefinite last-pose lifetime.");
            Assert.Throws<NotSupportedException>(() =>
                ((IList<ObservedCollaboratorPoint>)batch.Points)[0] = lease.Second);
            lease.Dispose();
            Assert.That(lease.TryRevalidate(150, out _), Is.False);
        }

        [Test]
        public void RejectsSelfAndAmbiguousEvidenceBeforeAnyLease()
        {
            var self = Batch("source", _sourceBoard.Registration.NativeSpace, 7, 17, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("alice", 22, 222, 2222, "head", 2));
            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(self, "alice", "head",
                "alice", "head", 150, null, out _, out _), Is.False,
                "Logical self-reference must not be accepted merely because native actor handles differ.");

            var duplicate = Batch("source", _sourceBoard.Registration.NativeSpace, 7, 18, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("alice", 11, 111, 1111, "head", 2));
            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(duplicate, "alice", "head",
                "bob", "head", 150, null, out _, out _), Is.False,
                "A duplicate native point identity is ambiguous even when its numeric pose differs.");

            var crossSpace = Batch("target", _targetBoard.Registration.NativeSpace, 7, 19, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("bob", 22, 222, 2222, "head", 2));
            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(crossSpace, "alice", "head",
                "bob", "head", 150, null, out _, out _), Is.True,
                "A batch is admitted only in its exact declared local native space.");
            var liedSpace = Batch("source", _targetBoard.Registration.NativeSpace, 7, 20, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("bob", 22, 222, 2222, "head", 2));
            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(liedSpace, "alice", "head",
                "bob", "head", 150, null, out _, out _), Is.False);
        }

        [Test]
        public void BoardRequiresExactAuthoredHierarchyPairAndWithdrawsOnReplacement()
        {
            Assert.That(SharedBoardSupportEndpoint.TryCreate("board-7", _sourceBoard,
                out var source, out string error), Is.True, error);
            Assert.That(SharedBoardSupportEndpoint.TryCreate("board-7", _targetBoard,
                out var target, out error), Is.True, error);
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", new[] {
                new InteractionHierarchyPair(_sourceBoard, _targetBoard) }, out var hierarchy, out error), Is.True, error);
            Assert.That(SharedBoardSupportBinding.TryCreate(source, target, hierarchy,
                out var board, out error), Is.True, error);

            var batch = Batch("source", _sourceBoard.Registration.NativeSpace, 7, 17, 100, 200,
                Point("alice", 11, 111, 1111, "head", 1),
                Point("bob", 22, 222, 2222, "head", 2));
            Assert.That(_world.TryIssueObservedCollaboratorSolveInput(batch, "alice", "head",
                "bob", "head", 150, board, out var lease, out error), Is.True, error);
            _targetBoard.Unregister();
            Assert.That(board.TryRevalidate(out _), Is.False);
            Assert.That(lease.TryRevalidate(150, out _), Is.False,
                "A replaced/unregistered board surface must withdraw just this solve input.");
        }

        private ObservedCollaboratorBatchEvidence Batch(string space, long nativeSpace, ulong epoch,
            long frame, ulong receipt, ulong until, params NativeObservedCollaboratorPoint[] points)
        {
            Assert.That(ObservedCollaboratorBinding.TryDecodeNativeBatch(space, nativeSpace, epoch, frame,
                1, 9, receipt, until, points, out var batch, out string error), Is.True, error);
            return batch;
        }

        private static NativeObservedCollaboratorPoint Point(string collaborator, long actor,
            long actorElement, long point, string anchor, double x) =>
            new NativeObservedCollaboratorPoint(collaborator, actor, actorElement, point, anchor,
                x, .5, .25, 0);

        private SpatialBlendObject Object(string space, string id)
        {
            // A support affordance is never a root hierarchy node.  Register
            // the minimal authored Object → Affordance chain so this fixture
            // exercises the same explicit hierarchy contract as production.
            var container = Create<SpatialBlendObject>(id + "-object");
            container.Configure(_world, space, id + "-object", null, SpatialBlendObjectKind.Object);
            var value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, container, SpatialBlendObjectKind.Affordance);
            Assert.That(value.TrySetSupportPolygon(new[] {
                new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out string error), Is.True, error);
            Assert.That(value.TryRegister(out error), Is.True, error);
            return value;
        }

        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _created.Add(go);
            var value = go.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null);
            return value;
        }
    }
}
#endif
