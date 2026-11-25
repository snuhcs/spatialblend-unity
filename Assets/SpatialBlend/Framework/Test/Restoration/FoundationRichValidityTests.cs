// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Isolated native policy/scene validity fixture. It does not instantiate
    // LocalWorld/Actor adapters or make any actuator/body-current claim.
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationRichValidityTests
    {
        [Test]
        public void ValidityRejectsZeroAndUnknownRequestedMasksBeforeNativeCall()
        {
            Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(1, 1,
                RichRealizationValidityDomain.None, out _, out int result, out string error), Is.False);
            Assert.That(result, Is.EqualTo(-1));
            StringAssert.Contains("nonzero", error);
            Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(1, 1,
                (RichRealizationValidityDomain)(1u << 30), out _, out result, out error), Is.False);
            Assert.That(result, Is.EqualTo(-1));
            StringAssert.Contains("declared", error);
        }

        [Test]
        public void ValidityFeatureNegotiatesAllFieldsAndUnknownTokenIsDistinct()
        {
            Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(ulong.MaxValue, 1,
                RichRealizationValidityDomain.Policy, out RichRealizationValidity validity,
                out int result, out string error), Is.True, error);
            Assert.That(result, Is.EqualTo(-2));
            Assert.That(validity, Is.Null);
        }

        [Test]
        public void FrozenRevisionReportsPolicyAndCounterpartChangesWithoutNewReceipt()
        {
            long session = SpatialBlendAPI.CreateSession();
            ulong lifetime = 0;
            long target = 0;
            long linkRegistration = 0;
            try
            {
                Assert.That(session, Is.Not.EqualTo(0));
                long source = SpatialBlendAPI.AddSpace(session);
                target = SpatialBlendAPI.AddSpace(session);
                ConfigureSpace(source, 7001, 7002, out long sourceRoom);
                ConfigureSpace(target, 7001, 7002, out long targetRoom);
                long sourceActor = SpatialBlendAPI.CreateActor(source);
                long receiverActor = SpatialBlendAPI.CreateActor(target);
                SpatialBlendAPI.AddAnchor(source, sourceActor, "root", Vector3.zero);
                SpatialBlendAPI.AddAnchor(target, receiverActor, "root", Vector3.zero);
                SpatialBlendAPI.SetActorElement(source, sourceActor, sourceRoom);
                SpatialBlendAPI.SetActorElement(target, receiverActor, targetRoom);
                SpatialBlendAPI.Bind(source, sourceActor, "root", SbKernelType.Spacing);
                var callbacks = new List<RichRealizationSnapshot>();
                Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(target, callbacks.Add,
                    out string error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryActivateRichRealization(target, receiverActor,
                    out lifetime, out error), Is.True, error);
                SpatialBlendAPI.UpdateActorState(source, sourceActor, new Vector3(2f, 0f, 3f), 0f);
                Assert.That(callbacks.Count, Is.GreaterThan(0));
                RichRealizationSnapshot frozen = callbacks[callbacks.Count - 1];
                Assert.That(frozen.Header.Revision, Is.GreaterThan(0));

                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Policy,
                    out RichRealizationValidity policy, out int result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(policy.Valid, Is.True);
                Assert.That(policy.ProducerLifetime, Is.EqualTo(lifetime));
                Assert.That(policy.Revision, Is.EqualTo(frozen.Header.Revision));
                Assert.That(policy.CapturedKnownDomains & RichRealizationValidityDomain.Policy,
                    Is.EqualTo(RichRealizationValidityDomain.Policy));
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision,
                    RichRealizationValidityDomain.Policy | RichRealizationValidityDomain.Counterpart,
                    out RichRealizationValidity baseline, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(baseline.Valid, Is.True);

                // This changes native policy configuration only. It neither
                // sends an observation nor changes the frozen receipt/revision.
                Assert.That(SpatialBlendAPI.TrySetRichValidityPolicyForTest(target, 1,
                    out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Policy,
                    out policy, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(policy.Valid, Is.False);
                Assert.That(policy.ChangedDomains & RichRealizationValidityDomain.Policy,
                    Is.EqualTo(RichRealizationValidityDomain.Policy));

                // Installing an explicit correspondence mutates the live
                // linker graph only. No source packet is sent after this line.
                linkRegistration = SpatialBlendAPI.RegisterSupportCorrespondences(session,
                    new[] { new SbSupportCorrespondence(source, sourceRoom, target, targetRoom) });
                Assert.That(linkRegistration, Is.Not.EqualTo(0));
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Counterpart,
                    out RichRealizationValidity counterpart, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(counterpart.Valid, Is.False);
                Assert.That(counterpart.ChangedDomains & RichRealizationValidityDomain.Counterpart,
                    Is.EqualTo(RichRealizationValidityDomain.Counterpart));
                SpatialBlendAPI.DisposeSupportCorrespondences(linkRegistration);
                linkRegistration = 0;
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Counterpart,
                    out counterpart, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(counterpart.ChangedDomains & RichRealizationValidityDomain.Counterpart,
                    Is.EqualTo(RichRealizationValidityDomain.Counterpart));

                foreach (RichRealizationValidityDomain sceneDomain in new[]
                {
                    RichRealizationValidityDomain.Tree,
                    RichRealizationValidityDomain.Support
                })
                {
                    Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                        frozen.Header.Revision, sceneDomain, out RichRealizationValidity scene,
                        out result, out error), Is.True, error);
                    Assert.That(result, Is.EqualTo(1));
                    Assert.That(scene.Valid, Is.True);
                    Assert.That(scene.CapturedKnownDomains & sceneDomain, Is.EqualTo(sceneDomain));
                    Assert.That(scene.CurrentKnownDomains & sceneDomain, Is.EqualTo(sceneDomain));
                    Assert.That(scene.UncheckedDomains & sceneDomain, Is.EqualTo(RichRealizationValidityDomain.None));
                    Assert.That(scene.CapturedTreeRevision, Is.GreaterThan(0));
                    Assert.That(scene.CurrentTreeRevision, Is.EqualTo(scene.CapturedTreeRevision));
                    Assert.That(scene.CurrentSupportRevision, Is.EqualTo(scene.CapturedSupportRevision));
                }
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.ReceiverInput,
                    out RichRealizationValidity unavailable, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(-5));
                Assert.That(unavailable.Valid, Is.False);
                Assert.That(unavailable.UncheckedDomains & RichRealizationValidityDomain.ReceiverInput,
                    Is.EqualTo(RichRealizationValidityDomain.ReceiverInput));

                // Changing the receiver's scene is not a new source sample,
                // but it must invalidate both captured scene dependencies.
                var sceneDomains = RichRealizationValidityDomain.Tree | RichRealizationValidityDomain.Support;
                SpatialBlendAPI.SetElementTransform(target, targetRoom,
                    Matrix4x4.Translate(new Vector3(.2f, .1f, .3f)));
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, sceneDomains, out RichRealizationValidity changedScene,
                    out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1));
                Assert.That(changedScene.Valid, Is.False);
                Assert.That(changedScene.ChangedDomains & sceneDomains, Is.EqualTo(sceneDomains));
                Assert.That(changedScene.CurrentTreeRevision, Is.GreaterThan(changedScene.CapturedTreeRevision));
                Assert.That(changedScene.CurrentSupportRevision, Is.GreaterThan(changedScene.CapturedSupportRevision));

                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision + 1, RichRealizationValidityDomain.Policy,
                    out _, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(-6));

                // The validity reads above cannot consume the token's first
                // poll transaction or manufacture a newer native receipt.
                Assert.That(SpatialBlendAPI.TryPollRichRealization(lifetime,
                    out RichRealizationSnapshot polled, out int pollResult, out error), Is.True, error);
                Assert.That(pollResult, Is.EqualTo(1));
                Assert.That(polled.Header.Revision, Is.EqualTo(frozen.Header.Revision));
                Assert.That(polled.Header.NativeReceiptNanoseconds.Value,
                    Is.EqualTo(frozen.Header.NativeReceiptNanoseconds.Value));

                Assert.That(SpatialBlendAPI.TryRetireRichRealization(lifetime, out error), Is.True, error);
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(lifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Policy,
                    out RichRealizationValidity retired, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(-5));
                Assert.That(retired, Is.Null, "Retirement is unavailable without a current comparison payload.");
                ulong releasedLifetime = lifetime;
                Assert.That(SpatialBlendAPI.TryReleaseRichRealization(lifetime,
                    out int released, out error), Is.True, error);
                Assert.That(released, Is.EqualTo(1));
                lifetime = 0;
                Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(releasedLifetime,
                    frozen.Header.Revision, RichRealizationValidityDomain.Policy,
                    out _, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(-2));
            }
            finally
            {
                if (linkRegistration != 0) SpatialBlendAPI.DisposeSupportCorrespondences(linkRegistration);
                if (target != 0) SpatialBlendAPI.TryUnregisterOnRichRealization(target, out _);
                if (lifetime != 0)
                {
                    SpatialBlendAPI.TryRetireRichRealization(lifetime, out _);
                    SpatialBlendAPI.TryReleaseRichRealization(lifetime, out _, out _);
                }
                if (session != 0) SpatialBlendAPI.DestroySession(session);
            }
        }

        private static void ConfigureSpace(long space, long roomSharedId, long objectSharedId,
            out long room)
        {
            room = SpatialBlendAPI.AddElement(space);
            SpatialBlendAPI.SetElementSharedId(space, room, roomSharedId);
            SpatialBlendAPI.SetElementPolygon(space, room, SbGeometryRole.Navigable,
                new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) });
            long obstacle = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, obstacle, objectSharedId);
            SpatialBlendAPI.SetElementBox(space, obstacle, SbGeometryRole.Obstacle,
                new Vector3(4, 0, 4), new Vector3(6, 1, 6));
        }
    }
}
#endif
