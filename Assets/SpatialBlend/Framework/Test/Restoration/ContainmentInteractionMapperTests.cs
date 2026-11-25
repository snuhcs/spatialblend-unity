// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// "Interacting" is the core's interaction value of a received avatar: the TRANSFER flag of its root proposal AND that
    /// anchor's ARRIVED. The mapper has no threshold, timer or hysteresis: Start and
    /// Terminate are the frame-to-frame change of the value, on the very read it changes.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ContainmentInteractionMapperTests
    {
        private const long Actor = 7;
        private const long Chess = 9132;
        private const long Board = 9196;

        private ContainmentInteractionMapper _mapper;
        private List<ContainmentInteractionTransition> _out;

        [SetUp]
        public void SetUp()
        {
            _mapper = new ContainmentInteractionMapper();
            _out = new List<ContainmentInteractionTransition>();
        }

        private List<ContainmentInteractionTransition> Observe(long actor, long scope, bool interacting)
        {
            _out = new List<ContainmentInteractionTransition>();
            _mapper.Observe(actor, scope, interacting, _out);
            return _out;
        }

        private static void AssertOnly(List<ContainmentInteractionTransition> transitions,
                                       InteractionSignal signal, long scope)
        {
            Assert.That(transitions.Count, Is.EqualTo(1));
            Assert.That(transitions[0].Signal, Is.EqualTo(signal));
            Assert.That(transitions[0].Scope, Is.EqualTo(scope));
        }

        [Test]
        public void TheValueTurningTrueRaisesStartAndTurningFalseRaisesTerminateOnTheSameRead()
        {
            AssertOnly(Observe(Actor, Chess, true), InteractionSignal.Start, Chess);
            Assert.That(_mapper.IsEngaged(Actor), Is.True);
            AssertOnly(Observe(Actor, 0, false), InteractionSignal.Terminate, Chess);
            Assert.That(_mapper.IsEngaged(Actor), Is.False);
        }

        [Test]
        public void HoldingTheValueRaisesNothing()
        {
            Assert.That(Observe(Actor, 0, false), Is.Empty, "Never engaged, so nothing to end.");
            Observe(Actor, Chess, true);
            for (int i = 0; i < 5; ++i) Assert.That(Observe(Actor, Chess, true), Is.Empty);
            Observe(Actor, 0, false);
            for (int i = 0; i < 5; ++i) Assert.That(Observe(Actor, 0, false), Is.Empty);
        }

        [Test]
        public void ThereIsNoDwellOrHysteresisEnteringAgainStartsAgainAtOnce()
        {
            for (int round = 0; round < 3; ++round)
            {
                AssertOnly(Observe(Actor, Chess, true), InteractionSignal.Start, Chess);
                AssertOnly(Observe(Actor, 0, false), InteractionSignal.Terminate, Chess);
            }
        }

        [Test]
        public void AScopeLabelThatChangesWhileTheValueStaysTrueRaisesNothing()
        {
            // A hop between two leaves keeps TRANSFER and ARRIVED set, so the value did not change: no signal. (The former
            // event path raised a Terminate and a Start; the label is not part of the value.)
            AssertOnly(Observe(Actor, Chess, true), InteractionSignal.Start, Chess);
            Assert.That(Observe(Actor, Board, true), Is.Empty);
            AssertOnly(Observe(Actor, 0, false), InteractionSignal.Terminate, Chess);
        }

        [Test]
        public void ActorsAreIndependent()
        {
            Observe(1, Chess, true);
            AssertOnly(Observe(2, Chess, true), InteractionSignal.Start, Chess);
            AssertOnly(Observe(1, 0, false), InteractionSignal.Terminate, Chess);
            Assert.That(_mapper.IsEngaged(2), Is.True);
        }

        [Test]
        public void ForgetEndsAnEngagementWhenTheActorGoesAway()
        {
            Observe(Actor, Chess, true);
            Assert.That(_mapper.Forget(Actor, out long scope), Is.True);
            Assert.That(scope, Is.EqualTo(Chess));
            Assert.That(_mapper.Forget(Actor, out _), Is.False);
            AssertOnly(Observe(Actor, Chess, true), InteractionSignal.Start, Chess);
        }

        [Test]
        public void ClearForgetsEveryEngagementWithoutRaising()
        {
            Observe(1, Chess, true);
            Observe(2, Board, true);
            _mapper.Clear();
            Assert.That(_mapper.IsEngaged(1) || _mapper.IsEngaged(2), Is.False);
            Assert.That(Observe(1, 0, false), Is.Empty);
        }

        [Test]
        public void NullOutputIsAccepted()
        {
            _mapper.Observe(Actor, Chess, true, null);
            Assert.That(_mapper.IsEngaged(Actor), Is.True);
            _mapper.Observe(Actor, 0, false, null);
            Assert.That(_mapper.IsEngaged(Actor), Is.False);
        }

        [Test]
        public void MapperHoldsNoThresholdTimerOrPhaseConstant()
        {
            // The mapper is a pure edge detector over the interaction value. Its public surface has no numeric tuning
            // constant and no sentinel.
            var constants = typeof(ContainmentInteractionMapper).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.FlattenHierarchy);
            Assert.That(constants, Is.Empty);
            string text = System.IO.File.ReadAllText("Assets/SpatialBlend/Foundation/ContainmentInteractionMapper.cs");
            foreach (string word in new[] { "Time.", "dwell", "Dwell", "hysteresis(", "Threshold", "AnyScope" })
            {
                Assert.That(text, Does.Not.Contain(word), word);
            }
            Assert.That(System.IO.File.Exists("Assets/SpatialBlend/Foundation/InteractionPhaseMapper.cs"), Is.False);
        }
    }
}
#endif
