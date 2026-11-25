// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Decides which object, if any, carries the "Edit this object?" prompt. No Unity dependency and no clock of its own
    /// (every call is given <c>now</c>), so it is stepped by passing numbers.
    /// <para>
    /// The prompt exists only in its own mode, only for an editable object, and only while the ray is on that object or on the prompt itself.
    /// There is ONE prompt. The ray must stay on an object for <c>dwellSeconds</c> before the prompt moves there; moving to another object
    /// restarts the dwell, and the prompt on the old object lingers out meanwhile (with a dwell longer than the linger it hides, then shows
    /// on the new object). When the ray leaves, the prompt stays for <c>lingerSeconds</c>: the button floats above the object, so the
    /// ray has to cross empty space to reach it, and the prompt must still be there when it arrives. The Edit prompt passes 0.4 s; the
    /// hands' Menu button passes a longer linger. Hovering the object again, or the button, cancels the hide, so a one-frame drop of a
    /// jittery hand ray does not remove a button that is already showing.
    /// </para>
    /// </summary>
    public sealed class ModeSwitchHoverModel
    {
        public const int NoTarget = 0;

        private readonly float _lingerSeconds;
        private readonly float _dwellSeconds;
        private readonly InteractionMode _activeIn;
        private int _target = NoTarget;
        private int _candidate = NoTarget;
        private float _candidateSince;
        private float _lostAt = -1f;

        /// <param name="lingerSeconds">How long a shown prompt stays after the ray leaves.</param>
        /// <param name="dwellSeconds">How long the ray must stay on the same editable object before the prompt shows (1 s by default).</param>
        /// <param name="activeIn">The mode the prompt belongs to: Interaction for "Edit this object?", Scene Edit for the hands' Menu button.</param>
        public ModeSwitchHoverModel(float lingerSeconds, float dwellSeconds = 0f, InteractionMode activeIn = InteractionMode.Interaction)
        {
            _activeIn = activeIn;
            _lingerSeconds = lingerSeconds < 0f ? 0f : lingerSeconds;
            _dwellSeconds = dwellSeconds < 0f ? 0f : dwellSeconds;
        }

        /// <summary>The object the prompt is shown for, or <see cref="NoTarget"/>.</summary>
        public int Target => _target;

        /// <summary>
        /// Advances to <paramref name="now"/>. <paramref name="hoveredEditable"/> is the id of the editable object under the ray
        /// (<see cref="NoTarget"/> when the ray is on nothing or on something that cannot be edited), <paramref name="promptHovered"/>
        /// whether the ray is on the prompt. Returns the object to show the prompt for.
        /// </summary>
        public int Tick(float now, InteractionMode mode, int hoveredEditable, bool promptHovered, bool suspended)
        {
            if (suspended || mode != _activeIn)
            {
                _target = NoTarget;
                _candidate = NoTarget;
                _lostAt = -1f;
                return NoTarget;
            }

            if (hoveredEditable != _candidate)
            {
                // A different object (or none): the dwell starts over. Leaving before it elapses shows nothing.
                _candidate = hoveredEditable;
                _candidateSince = now;
            }

            if (hoveredEditable != NoTarget)
            {
                if (hoveredEditable == _target)
                {
                    _lostAt = -1f;
                }
                else if (now - _candidateSince >= _dwellSeconds)
                {
                    _target = hoveredEditable;
                    _lostAt = -1f;
                }
                else if (_target != NoTarget)
                {
                    // Still dwelling on the new object: the old prompt lingers out as if the ray had left it.
                    if (_lostAt < 0f) _lostAt = now;
                    if (now - _lostAt >= _lingerSeconds) { _target = NoTarget; _lostAt = -1f; }
                }
            }
            else if (_target != NoTarget && promptHovered)
            {
                _lostAt = -1f;
            }
            else if (_target != NoTarget)
            {
                if (_lostAt < 0f) _lostAt = now;
                if (now - _lostAt >= _lingerSeconds)
                {
                    _target = NoTarget;
                    _lostAt = -1f;
                }
            }

            return _target;
        }

        /// <summary>Hides the prompt at once (the [Edit] button was pressed, or the object went away).</summary>
        public void Clear()
        {
            _target = NoTarget;
            _candidate = NoTarget;
            _lostAt = -1f;
        }
    }
}
