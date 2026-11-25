// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Interactions
{
    /// <summary>What the owner of an <see cref="EditLease"/> must do after an input.</summary>
    public enum EditLeaseAction
    {
        /// <summary>Nothing to do.</summary>
        None,
        /// <summary>This user holds the lease: the edit goes ahead.</summary>
        Proceed,
        /// <summary>Nobody holds it: ask the network for it (Fusion: <c>RequestStateAuthority</c>). The edit waits for the answer.</summary>
        Request,
        /// <summary>Someone else holds it: the grab is refused (feedback names <see cref="EditLease.Holder"/>) and the object is let go.</summary>
        Refuse,
        /// <summary>This user holds it and no longer edits: give it back once the object has come to rest.</summary>
        Release,
    }

    /// <summary>
    /// The edit lease of one shared object, as one participant sees it. The lease is the network's single holder of the object's
    /// state (in the app, the Fusion state authority of a NetworkObject whose authority cannot be taken over): this class keeps no clock
    /// and decides nothing the network has not decided. It turns the user's grab and drop, and the holder the network reports, into what
    /// the owner must do.
    ///
    /// The rule: a grab while someone else holds the lease is refused on the spot, without asking. A grab while nobody holds it asks; the
    /// network grants the first request it receives while the lease is free and ignores any other, and a requester that sees someone else
    /// become the holder is refused. There is no tie. The holder keeps the lease from the first grab until the last part is dropped and the
    /// object has come to rest (so its last state is the one the others get), then releases it. A holder that leaves or disconnects loses it when the network
    /// removes the player (its own disconnect detection); nothing here times out.
    /// </summary>
    public sealed class EditLease
    {
        private readonly int _local;
        private bool _requested;

        /// <param name="localPlayer">This participant's player id.</param>
        public EditLease(int localPlayer)
        {
            _local = localPlayer;
        }

        /// <summary>The player the network says holds the lease, or null when nobody does.</summary>
        public int? Holder { get; private set; }

        /// <summary>How many parts of the object this user holds (a hand on each piece counts), granted or pending.</summary>
        public int GrabCount { get; private set; }

        /// <summary>Whether this user is grabbing the object (from a granted or pending grab until the last drop).</summary>
        public bool Grabbing => GrabCount > 0;

        /// <summary>Whether a request is out and not yet answered.</summary>
        public bool Requested => _requested;

        public bool HeldByMe => Holder == _local;
        public bool HeldByOther => Holder.HasValue && Holder.Value != _local;

        /// <summary>The user grabs a part. <paramref name="holder"/> is the holder the network reports now.</summary>
        public EditLeaseAction BeginGrab(int? holder)
        {
            Holder = holder;
            if (HeldByOther)
            {
                // Seeing the new holder here is the same news as HolderChanged: every part this user held (a request lost to it) is
                // let go with this one, and nothing is pending any more.
                GrabCount = 0;
                _requested = false;
                return EditLeaseAction.Refuse;
            }
            GrabCount++;
            if (HeldByMe) return EditLeaseAction.Proceed;
            // A second part grabbed while the first request is out waits for the same answer.
            if (_requested) return EditLeaseAction.None;
            _requested = true;
            return EditLeaseAction.Request;
        }

        /// <summary>The user drops a part. The lease goes back only when the last part is dropped.</summary>
        public EditLeaseAction EndGrab()
        {
            if (!Grabbing) return EditLeaseAction.None;
            GrabCount--;
            if (Grabbing) return EditLeaseAction.None;
            // Nothing held: the next grab asks again (asking twice is harmless, and an unanswered request must not block every
            // later one). A grant that still arrives for this request is given straight back (HolderChanged).
            _requested = false;
            return HeldByMe ? EditLeaseAction.Release : EditLeaseAction.None;
        }

        /// <summary>The network reports a new holder (null: nobody).</summary>
        public EditLeaseAction HolderChanged(int? holder)
        {
            Holder = holder;
            if (HeldByMe)
            {
                _requested = false;
                // Granted after the drop (a quick tap): nothing to edit, give it straight back.
                return Grabbing ? EditLeaseAction.Proceed : EditLeaseAction.Release;
            }
            if (HeldByOther)
            {
                _requested = false;
                // Refused only while this user is grabbing: a request lost to someone else, or (never with an authority that cannot
                // be taken over) a lease taken mid-move. Every part this user holds is let go. A holder that sees the next holder after
                // its own release has nothing to drop.
                if (Grabbing)
                {
                    GrabCount = 0;
                    return EditLeaseAction.Refuse;
                }
                return EditLeaseAction.None;
            }
            // Nobody holds it: a request still out may yet be granted.
            return EditLeaseAction.None;
        }

        /// <summary>Whether the owner should give the lease back now: it holds it, holds no part, and the object is at rest.</summary>
        public bool ShouldRelease(bool atRest) => HeldByMe && !Grabbing && atRest;
    }

    /// <summary>The words the lease shows. One place, so the toast, the tag and the tests agree.</summary>
    public static class EditLeaseText
    {
        /// <summary>The name a participant is shown by: the app has no user names, so it is the player's number in the room.</summary>
        public static string Participant(int player) => "Player " + player;

        /// <summary>The refusal toast: "Player 2 is moving this".</summary>
        public static string Refused(int? holder) =>
            (holder.HasValue ? Participant(holder.Value) : "Someone else") + " is moving this";

        /// <summary>The tag over an object someone else is moving: "Moving: Player 2".</summary>
        public static string Moving(int holder) => "Moving: " + Participant(holder);

        /// <summary>The badge over the avatar of a participant in Editing.</summary>
        public const string Editing = "Editing";
    }
}
