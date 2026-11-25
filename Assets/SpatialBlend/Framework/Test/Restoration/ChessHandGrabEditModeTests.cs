// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using MRMotifs.SharedActivities.ChessSample;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using SpatialBlend.Interactions;
using UnityEngine;
using UnityEngine.Events;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A hand select is the piece's WhenStateChanged, and it takes the same lease as the controller wrapper.
    /// While that grab is waiting for authority, the live pose is not pulled back to the networked value.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ChessHandGrabEditModeTests
    {
        private GameObject _boardObject;
        private ChessBoardHandlerMotif _board;
        private HandGrabInteractable _hand;

        [SetUp]
        public void SetUp()
        {
            _boardObject = new GameObject("chess");
            _board = _boardObject.AddComponent<ChessBoardHandlerMotif>();
            _hand = Piece("pawn", withHand: true);
            Piece("rook", withHand: false);
            Invoke("InitializeChessPieces");
            Set("m_hasSpawned", true);
            Set("m_lease", new EditLease(1));
        }

        [TearDown]
        public void TearDown()
        {
            if (_boardObject != null) UnityEngine.Object.DestroyImmediate(_boardObject);
        }

        [Test]
        public void AHandSelectHoldsThePieceAndALaterChangeLetsItGo()
        {
            Raise(_hand, InteractableState.Hover, InteractableState.Select);

            Assert.That(_board.Lease.Grabbing, Is.True);
            Assert.That(_board.Lease.GrabCount, Is.EqualTo(1));
            Assert.That(_board.LeavesHeldPoseAlone(0, hasStateAuthority: false), Is.True,
                "waiting for the lease keeps the hand's pose");
            Assert.That(_board.LeavesHeldPoseAlone(0, hasStateAuthority: true), Is.False,
                "the holder writes the pose once the lease is theirs");
            Assert.That(_board.LeavesHeldPoseAlone(1, hasStateAuthority: false), Is.False,
                "the piece with no hand was not grabbed");

            Raise(_hand, InteractableState.Select, InteractableState.Hover);

            Assert.That(_board.Lease.GrabCount, Is.EqualTo(0));
            Assert.That(_board.LeavesHeldPoseAlone(0, hasStateAuthority: false), Is.False);
        }

        [Test]
        public void AfterUnsubscribeAHandSelectNoLongerHoldsThePiece()
        {
            Invoke("UnsubscribeChessPieceEvents");

            Raise(_hand, InteractableState.Hover, InteractableState.Select);

            Assert.That(_board.Lease.GrabCount, Is.EqualTo(0));
            Assert.That(_board.LeavesHeldPoseAlone(0, hasStateAuthority: false), Is.False);
        }

        private HandGrabInteractable Piece(string name, bool withHand)
        {
            var piece = new GameObject(name);
            piece.transform.SetParent(_boardObject.transform, false);
            piece.AddComponent<Rigidbody>();
            var wrapper = piece.AddComponent<InteractableUnityEventWrapper>();
            // EditMode does not construct the serialized UnityEvents the way a player does.
            foreach (var fieldName in new[] { "_whenSelect", "_whenUnselect" })
            {
                var field = typeof(InteractableUnityEventWrapper).GetField(
                    fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field.GetValue(wrapper) == null) field.SetValue(wrapper, new UnityEvent());
            }
            return withHand ? piece.AddComponent<HandGrabInteractable>() : null;
        }

        private void Raise(HandGrabInteractable hand, InteractableState previous, InteractableState next)
        {
            var args = new InteractableStateChangeArgs(previous, next);
            for (var type = hand.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("WhenStateChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                var handler = (Action<InteractableStateChangeArgs>)field.GetValue(hand);
                Assert.That(handler, Is.Not.Null);
                handler(args);
                return;
            }
            Assert.Fail("WhenStateChanged is not on the hand");
        }

        private void Invoke(string method) =>
            typeof(ChessBoardHandlerMotif).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_board, null);

        private void Set(string field, object value) =>
            typeof(ChessBoardHandlerMotif).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_board, value);
    }
}
#endif
