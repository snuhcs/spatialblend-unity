// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using SpatialBlend.Internal;

namespace SpatialBlend.Tests
{
    // Copied only into an explicitly provisioned temporary native verification
    // project. Never included in the original Freeform project/device-free gate.
    public class NativeRemovalIntegrationTests
    {
        [Test]
        [Category("NativeIntegration")]
        public void RemovalTraversesRealNativeAndManagedBoundaryWithSpaceIsolation()
        {
            SpatialBlendAPI.Initialize();
            long session = SpatialBlendAPI.CreateSession();
            Assert.That(session, Is.GreaterThan(0));
            long source = SpatialBlendAPI.AddSpace(session);
            long receiver = SpatialBlendAPI.AddSpace(session);
            long other = SpatialBlendAPI.AddSpace(session);
            long received = 0, otherReceived = 0;
            var removed = new List<long>();
            var otherRemoved = new List<long>();
            try
            {
                SpatialBlendAPI.RegisterOnElementAdded(receiver, (s, e, shared, tag, data, size) =>
                {
                    Assert.That(s, Is.EqualTo(receiver));
                    received = e;
                    SpatialBlendAPI.ResolveSharedElement(s, e, e);
                });
                SpatialBlendAPI.RegisterOnElementAdded(other, (s, e, shared, tag, data, size) =>
                {
                    otherReceived = e;
                    SpatialBlendAPI.ResolveSharedElement(s, e, e);
                });
                Assert.That(SpatialBlendAPI.TryRegisterOnElementRemoved(receiver, (s, e) =>
                {
                    Assert.That(s, Is.EqualTo(receiver));
                    removed.Add(e);
                    // Re-enter the source through C while the synchronous unshare
                    // is in flight. No source or receiver lock may surround upcalls.
                    SpatialBlendAPI.SetUpdateInterval(source, 0.05);
                }), Is.True, "New native artifact must expose removal; unsupported is not a pass.");
                Assert.That(SpatialBlendAPI.TryRegisterOnElementRemoved(other,
                    (s, e) => otherRemoved.Add(e)), Is.True);

                long element = SpatialBlendAPI.AddElement(source);
                SpatialBlendAPI.ShareElement(source, element);
                Assert.That(received, Is.GreaterThan(0));
                Assert.That(otherReceived, Is.GreaterThan(0));
                SpatialBlendAPI.UnshareElement(source, element);
                SpatialBlendAPI.UnshareElement(source, element);
                CollectionAssert.AreEqual(new[] { received }, removed);
                CollectionAssert.AreEqual(new[] { otherReceived }, otherRemoved);

                SpatialBlendAPI.UnregisterCallbacks(receiver);
                SpatialBlendAPI.ShareElement(source, element);
                SpatialBlendAPI.UnshareElement(source, element);
                Assert.That(removed.Count, Is.EqualTo(1));
                Assert.That(otherRemoved.Count, Is.EqualTo(2));
                Assert.That(otherRemoved[1], Is.Not.EqualTo(otherRemoved[0]));
            }
            finally
            {
                SpatialBlendAPI.UnregisterCallbacks(receiver);
                SpatialBlendAPI.UnregisterCallbacks(other);
                SpatialBlendAPI.DestroySession(session);
            }
        }
    }
}
#endif
