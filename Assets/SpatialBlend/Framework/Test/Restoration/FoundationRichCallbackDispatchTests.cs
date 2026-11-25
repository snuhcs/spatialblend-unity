// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;

namespace SpatialBlend.Tests
{
    public sealed class FoundationRichCallbackDispatchTests
    {
        [Test]
        public void ThrowingMulticastObserverDoesNotSuppressLaterObserver()
        {
            bool deliveredAfterFailure = false;
            int reportedFailures = 0;
            Action<long, string> rejected = (_, __) => ++reportedFailures;
            SpatialBlendAPI.RichCallbackRejected += rejected;
            try
            {
                Action<RichRealizationSnapshot> observers = _ =>
                {
                    throw new InvalidOperationException("expected observer failure");
                };
                observers += _ => deliveredAfterFailure = true;
                MethodInfo dispatch = typeof(SpatialBlendAPI).GetMethod(
                    "InvokeRichCallbackObservers",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(dispatch);

                // The dispatcher is pure callback fan-out: a null snapshot is
                // sufficient here because neither observer reads it.
                Assert.DoesNotThrow(() => dispatch.Invoke(null,
                    new object[] { 71L, observers, null }));
                Assert.IsTrue(deliveredAfterFailure);
                Assert.AreEqual(1, reportedFailures);
            }
            finally
            {
                SpatialBlendAPI.RichCallbackRejected -= rejected;
            }
        }
    }
}
