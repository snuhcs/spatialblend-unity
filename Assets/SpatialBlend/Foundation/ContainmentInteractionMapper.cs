// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace SpatialBlend
{
    public enum InteractionSignal
    {
        None = 0,
        Start = 1,
        Terminate = 2
    }

    /// <summary>One interaction transition the mapper decided, with the object it concerns.</summary>
    public readonly struct ContainmentInteractionTransition
    {
        public readonly InteractionSignal Signal;
        /// <summary>The containing object's shared region id: the interaction referent.</summary>
        public readonly long Scope;

        public ContainmentInteractionTransition(InteractionSignal signal, long scope)
        {
            Signal = signal;
            Scope = scope;
        }
    }

    /// <summary>
    /// Maps the interaction value of a received avatar to the v1 interaction Start and Terminate events. The value is the core's TRANSFER flag AND the root anchor's ARRIVED, read from
    /// the current proposal set every frame. Start and Terminate are the frame-to-frame change of that value and nothing
    /// else: there is no threshold, hysteresis or timer in the app, and no event ordering to reconcile, because the value
    /// is recomputed on every read.
    ///
    /// <c>Start</c> is raised when the value turns true, <c>Terminate</c> when it turns false. The scope is only a label
    /// (the leaf's shared id the proposal named when the value turned true): a Terminate names the scope its Start did, and
    /// a change of the label while the value stays true raises nothing.
    /// </summary>
    public sealed class ContainmentInteractionMapper
    {
        private readonly Dictionary<long, long> _engagedScopeByActor = new Dictionary<long, long>();

        public bool IsEngaged(long actor) => _engagedScopeByActor.ContainsKey(actor);

        public bool TryGetScope(long actor, out long scope) => _engagedScopeByActor.TryGetValue(actor, out scope);

        /// <summary>
        /// Folds one reading of the value in and appends the transition it causes (zero or one).
        /// <paramref name="scope"/> labels a Start; a Terminate carries the scope its Start did.
        /// </summary>
        public void Observe(long actor, long scope, bool interacting, List<ContainmentInteractionTransition> output)
        {
            bool engaged = _engagedScopeByActor.TryGetValue(actor, out long current);
            if (interacting)
            {
                if (engaged) return;
                _engagedScopeByActor[actor] = scope;
                output?.Add(new ContainmentInteractionTransition(InteractionSignal.Start, scope));
                return;
            }

            if (!engaged) return;
            _engagedScopeByActor.Remove(actor);
            output?.Add(new ContainmentInteractionTransition(InteractionSignal.Terminate, current));
        }

        /// <summary>
        /// Ends the actor's engagement because the actor itself went away, and reports the scope it
        /// left. False when it was not engaged.
        /// </summary>
        public bool Forget(long actor, out long scope)
        {
            if (!_engagedScopeByActor.TryGetValue(actor, out scope)) return false;
            _engagedScopeByActor.Remove(actor);
            return true;
        }

        public void Clear() => _engagedScopeByActor.Clear();
    }
}
