// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Scene mutators return <c>sb_scene_status_t</c>: a change that is not OK was NOT registered (overlapping sibling regions, a
    /// child outside its parent). The binding never ignores that, and the collider the core derives an envelope
    /// from is handed over in the element's local frame.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationSceneStatusTests
    {
        private const long Space = 424242;

        [TearDown]
        public void TearDown() => SpatialBlendAPI.ClearSceneError(Space);

        [Test]
        public void StatusValuesMirrorTheHeader()
        {
            Assert.That((int)SbSceneStatus.Ok, Is.EqualTo(0));
            Assert.That((int)SbSceneStatus.InvalidArgument, Is.EqualTo(1));
            Assert.That((int)SbSceneStatus.ContainmentOverlap, Is.EqualTo(2));
            Assert.That((int)SbSceneStatus.ContainmentEscapesParent, Is.EqualTo(3));
        }

        [Test]
        public void AnOkStatusIsSilent()
        {
            Assert.That(SpatialBlendAPI.ReportSceneStatus(Space, 0, "sb_set_element_polygon", 7, () => "unused"), Is.EqualTo(SbSceneStatus.Ok));
            Assert.That(SpatialBlendAPI.TryGetSceneError(Space, out _), Is.False);
        }

        [TestCase(2, 2)]
        [TestCase(3, 3)]
        [TestCase(1, 1)]
        [TestCase(99, 1)]
        public void ARefusedChangeIsLoggedAsAnErrorWithTheCoresMessageAndKept(int raw, int expectedValue)
        {
            var expected = (SbSceneStatus)expectedValue;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("sb_set_element_polygon on element 7 was not registered"));

            SbSceneStatus status = SpatialBlendAPI.ReportSceneStatus(Space, raw, "sb_set_element_polygon", 7,
                () => "elements 7 and 9 overlap");

            Assert.That(status, Is.EqualTo(expected));
            Assert.That(SpatialBlendAPI.TryGetSceneError(Space, out string error), Is.True);
            Assert.That(error, Does.Contain("elements 7 and 9 overlap"));
            Assert.That(error, Does.Contain(expected.ToString()));
            SpatialBlendAPI.ClearSceneError(Space);
            Assert.That(SpatialBlendAPI.TryGetSceneError(Space, out _), Is.False);
        }

        [Test]
        public void ARefusalWithNoMessageStillSaysWhatWasRefused()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("sb_set_element_box on element 3 was not registered"));
            SpatialBlendAPI.ReportSceneStatus(Space, 2, "sb_set_element_box", 3, null);
            Assert.That(SpatialBlendAPI.TryGetSceneError(Space, out string error), Is.True);
            Assert.That(error, Does.Contain("ContainmentOverlap"));
        }

        // ------------------------------------------------------------------ collider frame

        private GameObject _parent;

        [TearDown]
        public void DestroyObjects()
        {
            if (_parent != null) Object.DestroyImmediate(_parent);
        }

        private BoxCollider ChildBox(Vector3 parentScale, Vector3 childLocalPosition, Vector3 childLocalScale)
        {
            _parent = new GameObject("element");
            _parent.transform.position = new Vector3(5f, 0f, 0f);
            _parent.transform.localScale = parentScale;
            var child = new GameObject("collider-child");
            child.transform.SetParent(_parent.transform, false);
            child.transform.localPosition = childLocalPosition;
            child.transform.localScale = childLocalScale;
            var box = child.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = Vector3.one;
            return box;
        }

        [Test]
        public void AColliderOnTheElementsOwnTransformIsHandedOverInItsOwnLocalFrame()
        {
            BoxCollider box = ChildBox(Vector3.one, Vector3.zero, Vector3.one);
            box.transform.SetParent(null);
            box.center = new Vector3(1f, 2f, 3f);
            box.size = new Vector3(4f, 5f, 6f);

            Assert.That(SpatialBlendAPI.TryBuildColliderParams(box, box.transform, out SbColliderType type, out double[] p), Is.True);

            Assert.That(type, Is.EqualTo(SbColliderType.Box));
            // Unity (x, y, z) is Z-up (x, z, y).
            Assert.That(p, Is.EqualTo(new double[] { 1, 3, 2, 4, 6, 5 }).Within(1e-6));
            Assert.That(SpatialBlendAPI.TryBuildColliderParams(box, null, out _, out double[] noFrame), Is.True);
            Assert.That(noFrame, Is.EqualTo(p).Within(1e-6), "No frame is the collider's own frame, as before.");
            Object.DestroyImmediate(box.gameObject);
        }

        [Test]
        public void AColliderOnAChildIsComposedIntoTheElementsLocalFrameNotTheChildsOwn()
        {
            // The child sits 1 m along the element's +X, and is twice as wide as the element's own scale.
            BoxCollider box = ChildBox(new Vector3(2f, 2f, 2f), new Vector3(1f, 0f, 0f), new Vector3(2f, 1f, 1f));

            Assert.That(SpatialBlendAPI.TryBuildColliderParams(box, _parent.transform, out _, out double[] composed), Is.True);
            Assert.That(SpatialBlendAPI.TryBuildColliderParams(box, null, out _, out double[] ownFrame), Is.True);

            Assert.That(composed[0], Is.EqualTo(1.0).Within(1e-6), "The child's offset in the element's frame.");
            Assert.That(composed[3], Is.EqualTo(2.0).Within(1e-6), "The child's extra width relative to the element.");
            Assert.That(ownFrame[0], Is.EqualTo(0.0).Within(1e-6), "In its own frame the child sits at its own origin: the old mismatch.");
            Assert.That(ownFrame[3], Is.EqualTo(1.0).Within(1e-6));
        }

        [Test]
        public void AHeadsPitchIsItsEulerXAsV1SentItAndApplyingItRestoresIt()
        {
            Quaternion head = Quaternion.Euler(25f, 130f, 0f);
            float pitch = HeadOrientation.PitchDegrees(head);
            Assert.That(HeadOrientation.YawDegrees(head), Is.EqualTo(130f).Within(1e-3f));
            Assert.That(Quaternion.Euler(pitch, 130f, 0f).eulerAngles.x, Is.EqualTo(25f).Within(1e-3f));

            // A head looking up has an eulerAngles.x near 360: it travels as a negative pitch and comes back as the same rotation.
            Quaternion lookingUp = Quaternion.Euler(-12f, 40f, 0f);
            double wire = HeadOrientation.PitchRadiansFromDegrees(HeadOrientation.PitchDegrees(lookingUp));
            Assert.That(wire, Is.EqualTo(-12.0 * System.Math.PI / 180.0).Within(1e-6));
            Quaternion restored = Quaternion.Euler(HeadOrientation.PitchDegreesFromRadians(wire), 40f, 0f);
            Assert.That(Quaternion.Angle(restored, lookingUp), Is.LessThan(1e-3f));
        }

        [Test]
        public void TheAbi4StructsHaveTheSizesTheCHeaderComputes()
        {
            // spatialblend_type.h (ABI 4): the sizes a C compiler computes, not the managed declarations.
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SbAnchorPose>(), Is.EqualTo(72));
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SbAnchorProposal>(), Is.EqualTo(144));
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SbProposalSet>(), Is.EqualTo(64));
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SbActorEvent>(), Is.EqualTo(360));
        }
    }
}
#endif
