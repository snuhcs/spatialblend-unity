// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using Fusion;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Assert = NUnit.Framework.Assert;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The edit lease of a shared object (the chess board's pieces). The network is the only judge (the first request it receives
    /// while the lease is free is granted, any other is not), and <see cref="EditLease"/> turns the user's grab and drop and the holder
    /// the network reports into what to do. No clock: a release waits for the dropped piece to rest, a leaver's lease goes with the
    /// player.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EditLeaseTests
    {
        private const int Me = 1, Ana = 2;

        [Test]
        public void AGrabOfAFreeLeaseAsksAndProceedsWhenGrantedThenReleasesOnceTheDropIsAtRest()
        {
            var lease = new EditLease(Me);
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
            Assert.That(lease.Requested, Is.True);
            Assert.That(lease.HolderChanged(Me), Is.EqualTo(EditLeaseAction.Proceed));
            Assert.That(lease.HeldByMe && lease.Grabbing, Is.True);
            Assert.That(lease.ShouldRelease(atRest: true), Is.False, "never while grabbing");

            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.Release));
            Assert.That(lease.ShouldRelease(atRest: false), Is.False, "the dropped piece is still falling: its last state is not sent yet");
            Assert.That(lease.ShouldRelease(atRest: true), Is.True);
            Assert.That(lease.HolderChanged(null), Is.EqualTo(EditLeaseAction.None));
            Assert.That(lease.HeldByMe || lease.HeldByOther, Is.False);
        }

        [Test]
        public void AGrabWhileSomeoneElseHoldsTheLeaseIsRefusedOnTheSpotWithoutAsking()
        {
            var lease = new EditLease(Me);
            lease.HolderChanged(Ana);
            Assert.That(lease.BeginGrab(Ana), Is.EqualTo(EditLeaseAction.Refuse));
            Assert.That(lease.Grabbing, Is.False);
            Assert.That(lease.Requested, Is.False, "no request is sent: it could not be granted");
            Assert.That(lease.Holder, Is.EqualTo(Ana), "the refusal names the holder");
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.None));
        }

        [Test]
        public void TwoGrabbersOfAFreeLeaseBothAskAndExactlyTheOneTheNetworkGrantsProceeds()
        {
            var mine = new EditLease(Me);
            var hers = new EditLease(Ana);
            Assert.That(mine.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
            Assert.That(hers.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));

            // The network received Ana's request first. Both are told the same holder.
            Assert.That(hers.HolderChanged(Ana), Is.EqualTo(EditLeaseAction.Proceed));
            Assert.That(mine.HolderChanged(Ana), Is.EqualTo(EditLeaseAction.Refuse));
            Assert.That(mine.Grabbing, Is.False);
            Assert.That(mine.Requested, Is.False);
            Assert.That(hers.Grabbing, Is.True);
        }

        [Test]
        public void AQuickTapThatIsGrantedAfterTheDropGivesTheLeaseStraightBack()
        {
            var lease = new EditLease(Me);
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.None));
            Assert.That(lease.HolderChanged(Me), Is.EqualTo(EditLeaseAction.Release));
            Assert.That(lease.ShouldRelease(atRest: true), Is.True);
        }

        [Test]
        public void TheHolderLeavingFreesTheLeaseForTheNextGrab()
        {
            var lease = new EditLease(Me);
            lease.HolderChanged(Ana);
            Assert.That(lease.BeginGrab(Ana), Is.EqualTo(EditLeaseAction.Refuse));
            // Photon removed Ana (left, or its disconnect detection dropped her): the network reports nobody.
            Assert.That(lease.HolderChanged(null), Is.EqualTo(EditLeaseAction.None));
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
        }

        [Test]
        public void AHolderThatSeesTheNextHolderAfterItsOwnReleaseIsNotRefused()
        {
            var lease = new EditLease(Me);
            lease.BeginGrab(null);
            lease.HolderChanged(Me);
            lease.EndGrab();
            // The release and Ana's grant arrive between two reads: the holder goes from me to Ana directly.
            Assert.That(lease.HolderChanged(Ana), Is.EqualTo(EditLeaseAction.None));
            Assert.That(lease.HeldByOther, Is.True);
        }

        [Test]
        public void AGrabWhileStillHoldingFromTheLastDropProceedsAndASecondPieceWaitsForTheSameRequest()
        {
            var lease = new EditLease(Me);
            lease.BeginGrab(null);
            lease.HolderChanged(Me);
            lease.EndGrab();
            Assert.That(lease.BeginGrab(Me), Is.EqualTo(EditLeaseAction.Proceed), "the piece is still settling: the lease is still mine");

            var other = new EditLease(Me);
            Assert.That(other.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
            Assert.That(other.BeginGrab(null), Is.EqualTo(EditLeaseAction.None), "a second piece waits for the request already out");
            Assert.That(other.GrabCount, Is.EqualTo(2));
        }

        [Test]
        public void AnUnansweredRequestDoesNotBlockTheNextGrab()
        {
            var lease = new EditLease(Me);
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
            lease.EndGrab();
            Assert.That(lease.Requested, Is.False, "nothing held: the next grab asks again");
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request));
        }

        [Test]
        public void WithAPieceInEachHandTheLeaseIsKeptUntilTheLastDrop()
        {
            var lease = new EditLease(Me);
            lease.BeginGrab(null);
            lease.HolderChanged(Me);
            Assert.That(lease.BeginGrab(Me), Is.EqualTo(EditLeaseAction.Proceed), "the other hand");
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.None), "one hand still holds a piece");
            Assert.That(lease.ShouldRelease(atRest: true), Is.False, "the held piece is kinematic, so 'at rest' is not enough");
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.Release));
            Assert.That(lease.ShouldRelease(atRest: true), Is.True);

            // A quick tap on one piece while the other hand waits for the same answer: the grant is kept for the hand still holding.
            var tap = new EditLease(Me);
            tap.BeginGrab(null);
            tap.BeginGrab(null);
            tap.EndGrab();
            Assert.That(tap.HolderChanged(Me), Is.EqualTo(EditLeaseAction.Proceed));
        }

        [Test]
        public void ARaceLostSeenFirstByTheOtherHandsGrabLeavesNothingPendingAndTheNextLeaseIsReleased()
        {
            var lease = new EditLease(Me);
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request), "piece 1, request out");
            // Ana won; the other hand's grab is the first to see it (before the holder poll).
            Assert.That(lease.BeginGrab(Ana), Is.EqualTo(EditLeaseAction.Refuse));
            Assert.That(lease.GrabCount, Is.Zero, "piece 1 is let go too");
            Assert.That(lease.Requested, Is.False);
            Assert.That(lease.HolderChanged(Ana), Is.EqualTo(EditLeaseAction.None), "the poll then has nothing to add");
            Assert.That(lease.HolderChanged(null), Is.EqualTo(EditLeaseAction.None));
            Assert.That(lease.BeginGrab(null), Is.EqualTo(EditLeaseAction.Request), "a free lease is asked for again");
            Assert.That(lease.HolderChanged(Me), Is.EqualTo(EditLeaseAction.Proceed));
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.Release));
            Assert.That(lease.ShouldRelease(atRest: true), Is.True);
        }

        [Test]
        public void ALostRaceLetsGoOfEveryPieceHeld()
        {
            var lease = new EditLease(Me);
            lease.BeginGrab(null);
            lease.BeginGrab(null);
            Assert.That(lease.HolderChanged(Ana), Is.EqualTo(EditLeaseAction.Refuse));
            Assert.That(lease.GrabCount, Is.Zero);
            Assert.That(lease.EndGrab(), Is.EqualTo(EditLeaseAction.None), "the let-go pieces' drops count for nothing");
        }

        [Test]
        public void TheWordsNameTheHolder()
        {
            Assert.That(EditLeaseText.Refused(Ana), Is.EqualTo("Player 2 is moving this"));
            Assert.That(EditLeaseText.Refused(null), Is.EqualTo("Someone else is moving this"));
            Assert.That(EditLeaseText.Moving(3), Is.EqualTo("Moving: Player 3"));
            Assert.That(EditLeaseText.Editing, Is.EqualTo("Editing"));
        }

        [Test]
        public void TheEditingBadgeShowsOnlyOverARemoteParticipantInEditingWhoseAvatarIsShown()
        {
            Assert.That(SbAvatarBehaviourFusion.ShowsEditingBadge(isLocal: false, isEditing: true, hasHead: true, avatarHidden: false), Is.True);
            Assert.That(SbAvatarBehaviourFusion.ShowsEditingBadge(isLocal: true, isEditing: true, hasHead: true, avatarHidden: false), Is.False, "never over yourself");
            Assert.That(SbAvatarBehaviourFusion.ShowsEditingBadge(false, false, true, false), Is.False, "Interaction shows nothing");
            Assert.That(SbAvatarBehaviourFusion.ShowsEditingBadge(false, true, false, false), Is.False, "no head yet");
            Assert.That(SbAvatarBehaviourFusion.ShowsEditingBadge(false, true, true, true), Is.False, "not over a hidden avatar");
        }

        [Test]
        public void ARefusalNoticeNeedsALiveAssistAndIsRemembered()
        {
            var previous = ModeSwitchAssist.Instance;
            if (previous == null)
            {
                Assert.That(ModeSwitchAssist.ShowNotice(EditLeaseText.Refused(Ana)), Is.False, "no assist, nothing to show it on");
            }
            var go = new GameObject("notice-assist");
            try
            {
                var assist = go.AddComponent<ModeSwitchAssist>();
                typeof(ModeSwitchAssist).GetProperty("Instance").SetValue(null, assist);
                Assert.That(ModeSwitchAssist.ShowNotice(EditLeaseText.Refused(Ana)), Is.False, "an inactive assist (the lobby) shows nothing");
                Assert.That(assist.LastNotice, Is.EqualTo("Player 2 is moving this"));
                Assert.That(ModeSwitchAssist.ShowNotice(null), Is.False);
            }
            finally
            {
                typeof(ModeSwitchAssist).GetProperty("Instance").SetValue(null, previous);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TheChessBoardsAuthorityCannotBeTakenOverSoTheLeaseCannotPingPong()
        {
            var root = PrefabUtility.LoadPrefabContents("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            try
            {
                NetworkObjectFlags flags = root.GetComponent<NetworkObject>().Flags;
                Assert.That(flags & NetworkObjectFlags.AllowStateAuthorityOverride, Is.EqualTo(NetworkObjectFlags.None),
                    "with an override a second grabber's request takes the pieces mid-move (the old ping-pong)");
                Assert.That(flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves, Is.EqualTo(NetworkObjectFlags.None),
                    "the board outlives a holder that leaves; its lease goes with the player");
                Assert.That(flags & NetworkObjectFlags.MasterClientObject, Is.EqualTo(NetworkObjectFlags.None),
                    "a master-client object would be held by the master and never free");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void ThePresenceTagIsAGraphitePillThatShowsTheHolderAndNotTheRealAnchorColour()
        {
            var follow = new GameObject("followed");
            EditPresenceTag tag = EditPresenceTag.Create("tag", follow.transform, Vector3.up * 0.35f);
            try
            {
                Assert.That(tag.Visible, Is.False, "hidden until someone holds the object");
                follow.transform.position = new Vector3(1f, 0.5f, 2f);
                tag.Show(EditLeaseText.Moving(Ana));
                Assert.That(tag.Visible, Is.True);
                Assert.That(tag.Text, Is.EqualTo("Moving: Player 2"));
                Assert.That(Vector3.Distance(tag.transform.position, new Vector3(1f, 0.85f, 2f)), Is.LessThan(1e-4f));
                ModeSwitchStyle style = ModeSwitchStyle.Current;
                Color real = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor;
                foreach (Image image in tag.GetComponentsInChildren<Image>(true))
                {
                    Assert.That(image.color, Is.Not.EqualTo(real), image.name + " is not the real-anchor orange");
                    // The theme's one panel style: a white fill tinted by the PanelTop-to-PanelBottom gradient, the hairline, the accent dot.
                    var gradient = image.GetComponent<SpatialBlend.Samples.FreeformCollaboration.UI.UiVerticalGradient>();
                    bool panelFill = gradient != null && gradient.Top == style.Surface;
                    Assert.That(panelFill || image.color == style.Surface || image.color == style.Outline || image.color == style.Accent, Is.True,
                        image.name + " uses a Graphite token");
                }
                tag.Hide();
                Assert.That(tag.Visible, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(tag.gameObject);
                Object.DestroyImmediate(follow);
            }
        }

        [Test]
        public void TheMovedPieceOutlineIsTheAccentAroundThePiece()
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.transform.localScale = Vector3.one * 0.05f;
            EditPresenceOutline outline = EditPresenceOutline.Create("outline");
            try
            {
                outline.Show(piece.transform);
                Assert.That(outline.Visible, Is.True);
                var line = outline.GetComponent<LineRenderer>();
                Assert.That(line.startColor, Is.EqualTo(ModeSwitchStyle.Current.Accent));
                Assert.That(line.positionCount, Is.EqualTo(16));
                Bounds b = EditPresenceOutline.BoundsOf(piece.transform);
                for (int i = 0; i < line.positionCount; i++)
                    Assert.That(b.Contains(line.GetPosition(i)) || Vector3.Distance(b.ClosestPoint(line.GetPosition(i)), line.GetPosition(i)) < 1e-4f, Is.True);
                outline.Hide();
                Assert.That(outline.Visible, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(outline.gameObject);
                Object.DestroyImmediate(piece);
            }
        }
    }
}
#endif
