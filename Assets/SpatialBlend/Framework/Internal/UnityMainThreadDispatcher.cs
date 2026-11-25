// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using System;
using System.Threading.Tasks;

namespace SpatialBlend.Internal
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly object _lock = new object();
        private static int _mainThreadId = -1;

        private static readonly Queue<Action> _executionQueue = new Queue<Action>();

        public static void Initialize()
        {
            if (_instance == null)
            {
                var go = new GameObject("UnityMainThreadDispatcher");
                _instance = go.AddComponent<UnityMainThreadDispatcher>();
            }
        }

        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("UnityMainThreadDispatcher is not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }

        private void Awake()
        {
            // Record the main thread id when this component awakens.
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        private void Update()
        {
            // Snapshot under the lock, then run outside it so a main-thread action
            // can't deadlock against a background enqueue.
            Action[] batch;
            lock (_lock)
            {
                if (_executionQueue.Count == 0) return;
                batch = _executionQueue.ToArray();
                _executionQueue.Clear();
            }
            foreach (var action in batch)
            {
                action();
            }
        }

        public void Enqueue(Action action)
        {
            lock (_lock)
            {
                _executionQueue.Enqueue(action);
            }
        }

        public static void RunOnMainThread(Action action)
        {
            Instance.Enqueue(action);
        }

        // Run `action` on the main thread. If the caller is ALREADY on the main thread
        // the action runs INLINE and a completed task is returned, so a caller may
        // safely .Wait() on the result from either thread: an on-main .Wait() would
        // otherwise deadlock against Update() (the only pump for the queue).
        public static Task RunOnMainThreadAsync(Action action)
        {
            if (IsMainThread)
            {
                try { action(); return Task.CompletedTask; }
                catch (Exception ex) { return Task.FromException(ex); }
            }
            var tcs = new TaskCompletionSource<bool>();
            Instance.Enqueue(() =>
            {
                try { action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public static Task<T> RunOnMainThreadAsync<T>(Func<T> func)
        {
            if (IsMainThread)
            {
                try { return Task.FromResult(func()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }
            var tcs = new TaskCompletionSource<T>();
            Instance.Enqueue(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public static bool IsMainThread
        {
            get { return _mainThreadId != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        public static void RunOnMainThreadIfNeeded(Action action)
        {
            if (IsMainThread)
            {
                action();
            }
            else
            {
                RunOnMainThread(action);
            }
        }
    }
}
