// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class RemovalCallbackTests
    {
        private const long Space = 901;
        private const long OtherSpace = 902;
        private static Type _apiType;
        private static int _firstCalls;
        private static int _secondCalls;
        private static int _targetCalls;
        private static int _addedCalls;
        private static int _reentrantCalls;
        private static bool _callbackLockWasHeld;

        [SetUp]
        public void SetUp()
        {
            _apiType = typeof(AnchorPoseFrame).Assembly.GetType(
                "SpatialBlend.Internal.SpatialBlendAPI", throwOnError: true);
            _firstCalls = _secondCalls = _targetCalls = _addedCalls = _reentrantCalls = 0;
            _callbackLockWasHeld = false;
            Clear(Space);
            Clear(OtherSpace);
        }

        [TearDown]
        public void TearDown()
        {
            Clear(Space);
            Clear(OtherSpace);
        }

        [Test]
        public void RemovalWrapper_ForwardsOnlyTheMatchingCurrentToken()
        {
            long oldToken = Install("elementRemovedCallbacks", "elementRemovedCallbackTokens",
                                    Space, (Action<long, long>)First);
            Invoke("OnElementRemovedWrapper", Space, 77L, new IntPtr(oldToken));
            Assert.That(_firstCalls, Is.EqualTo(1));

            long newToken = Install("elementRemovedCallbacks", "elementRemovedCallbackTokens",
                                    Space, (Action<long, long>)Second);
            Invoke("OnElementRemovedWrapper", Space, 78L, new IntPtr(oldToken));
            Invoke("OnElementRemovedWrapper", Space, 79L, new IntPtr(newToken));
            Assert.That(_firstCalls, Is.EqualTo(1));
            Assert.That(_secondCalls, Is.EqualTo(1));
        }

        [Test]
        public void TargetAndAddedWrappers_RejectStaleTokens()
        {
            long targetOld = Install("targetCallbacks", "targetCallbackTokens", Space,
                CreateTargetDelegate(nameof(TargetFirst)));
            long targetNew = Install("targetCallbacks", "targetCallbackTokens", Space,
                CreateTargetDelegate(nameof(TargetSecond)));
            Invoke("OnTargetsWrapper", Space, 1L, IntPtr.Zero, 0L, new IntPtr(targetOld));
            Invoke("OnTargetsWrapper", Space, 1L, IntPtr.Zero, 0L, new IntPtr(targetNew));

            long addedOld = Install("elementAddedCallbacks", "elementAddedCallbackTokens", Space,
                (Action<long, long, long, string, IntPtr, long>)AddedFirst);
            long addedNew = Install("elementAddedCallbacks", "elementAddedCallbackTokens", Space,
                (Action<long, long, long, string, IntPtr, long>)AddedSecond);
            Invoke("OnElementAddedWrapper", Space, 2L, 3L, IntPtr.Zero, IntPtr.Zero, 0L,
                   new IntPtr(addedOld));
            Invoke("OnElementAddedWrapper", Space, 2L, 3L, IntPtr.Zero, IntPtr.Zero, 0L,
                   new IntPtr(addedNew));

            Assert.That(_targetCalls, Is.EqualTo(1));
            Assert.That(_addedCalls, Is.EqualTo(1));
        }

        [Test]
        public void ManagedUnregister_IsPerSpaceAndDoesNotRetireOtherRegistration()
        {
            long first = Install("elementRemovedCallbacks", "elementRemovedCallbackTokens", Space,
                                 (Action<long, long>)First);
            long second = Install("elementRemovedCallbacks", "elementRemovedCallbackTokens", OtherSpace,
                                  (Action<long, long>)Second);

            Invalidate(Space);
            Invoke("OnElementRemovedWrapper", Space, 1L, new IntPtr(first));
            Invoke("OnElementRemovedWrapper", OtherSpace, 2L, new IntPtr(second));

            Assert.That(_firstCalls, Is.EqualTo(0));
            Assert.That(_secondCalls, Is.EqualTo(1));
        }

        [Test]
        public void RemovalWrapper_InvokesOutsideRegistryLockAndMayUnregister()
        {
            long token = Install("elementRemovedCallbacks", "elementRemovedCallbackTokens", Space,
                                 (Action<long, long>)Reentrant);
            Invoke("OnElementRemovedWrapper", Space, 3L, new IntPtr(token));
            Invoke("OnElementRemovedWrapper", Space, 4L, new IntPtr(token));

            Assert.That(_reentrantCalls, Is.EqualTo(1));
            Assert.That(_callbackLockWasHeld, Is.False);
            Assert.That(Dictionary("elementRemovedCallbacks").Contains(Space), Is.False);
            Assert.That(Dictionary("elementRemovedCallbackTokens").Contains(Space), Is.False);
        }

        private static long Install(string callbacks, string tokens, long space, Delegate callback)
        {
            Type callbackType = Dictionary(callbacks).GetType().GetGenericArguments()[1];
            if (callback.GetType() != callbackType)
            {
                callback = Delegate.CreateDelegate(callbackType, callback.Target, callback.Method);
            }
            MethodInfo method = _apiType.GetMethod("RegisterCallback",
                BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(callbackType);
            return (long)method.Invoke(null, new object[]
            {
                Dictionary(callbacks), Dictionary(tokens), space, callback
            });
        }

        private static Delegate CreateTargetDelegate(string methodName)
        {
            Type targetType = _apiType.Assembly.GetType(
                "SpatialBlend.Internal.RealizationTarget", throwOnError: true);
            Type delegateType = _apiType.Assembly.GetType(
                "SpatialBlend.Internal.OnTargetsDelegate", throwOnError: true);
            MethodInfo method = typeof(RemovalCallbackTests).GetMethod(methodName,
                BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(targetType);
            return Delegate.CreateDelegate(delegateType, method);
        }

        private static IDictionary Dictionary(string field) => (IDictionary)_apiType.GetField(field,
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static void Clear(long space) => Invalidate(space);
        private static void Invalidate(long space) => _apiType.GetMethod("InvalidateCallbacksForSpace",
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { space });
        private static void Invoke(string method, params object[] arguments) => _apiType.GetMethod(method,
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);

        private static void First(long space, long element) => _firstCalls++;
        private static void Second(long space, long element) => _secondCalls++;
        private static void TargetFirst<T>(long space, long actor, IReadOnlyList<T> targets) => _targetCalls += 100;
        private static void TargetSecond<T>(long space, long actor, IReadOnlyList<T> targets) => _targetCalls++;
        private static void AddedFirst(long space, long element, long shared, string tag, IntPtr data, long size) => _addedCalls += 100;
        private static void AddedSecond(long space, long element, long shared, string tag, IntPtr data, long size) => _addedCalls++;

        private static void Reentrant(long space, long element)
        {
            _reentrantCalls++;
            object callbackLock = _apiType.GetField("callbackLock",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _callbackLockWasHeld = Monitor.IsEntered(callbackLock);
            Invalidate(space);
        }
    }
}
#endif
