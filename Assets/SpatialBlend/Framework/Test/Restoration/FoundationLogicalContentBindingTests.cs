// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;

namespace SpatialBlend.Tests
{
    [Category("FoundationLogicalContentBinding")]
    public sealed class FoundationLogicalContentBindingTests
    {
        [Test]
        public void SnapshotTailRaceReorderAndDuplicateRecoverOneExactProjection()
        {
            using (var bindings = NewBindings(out SharedToLocalBindingReceipt revisionThree))
            using (var reducer = new LogicalContentBindingReducer(bindings))
            {
                var sink = new RecordingSink();
                VerifiedLogicalContentRecovery recovered = BoardRecovery(revisionThree, 1,
                    new[] { Entry("stroke-a", "a") },
                    Mutation(1, 2, LogicalContentOperationKind.Upsert, "stroke-b", "b"),
                    Mutation(2, 3, LogicalContentOperationKind.Erase, "stroke-a", ""));
                Assert.That(reducer.TryRecoverAndApply(recovered, sink, out string error), Is.True, error);
                Assert.That(sink.Applied, Has.Count.EqualTo(1));
                Assert.That(sink.Applied[0].CommittedRevision, Is.EqualTo(3));
                Assert.That(sink.Applied[0].Entries, Has.Count.EqualTo(1));
                Assert.That(sink.Applied[0].Entries[0].Key, Is.EqualTo("stroke-b"));

                Assert.That(reducer.TryRecoverAndApply(recovered, sink, out error), Is.True, error);
                Assert.That(sink.Applied, Has.Count.EqualTo(1), "Exact recovery is idempotent locally.");

                VerifiedLogicalContentRecovery stale = BoardRecovery(revisionThree, 1,
                    new[] { Entry("stroke-a", "a") },
                    Mutation(1, 2, LogicalContentOperationKind.Upsert, "stroke-b", "b"));
                Assert.That(reducer.TryRecoverAndApply(stale, sink, out error), Is.False);
                Assert.That(error, Does.Contain("current local binding"));

                VerifiedLogicalContentRecovery reordered = BoardRecovery(revisionThree, 1,
                    new[] { Entry("stroke-a", "a") },
                    Mutation(2, 3, LogicalContentOperationKind.Upsert, "stroke-c", "c"),
                    Mutation(1, 2, LogicalContentOperationKind.Upsert, "stroke-b", "b"));
                Assert.That(reducer.TryRecoverAndApply(reordered, sink, out error), Is.False);
                Assert.That(sink.Applied, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void BoardStrokeAndKeyedGameSchemasRemainLogicalAndBounded()
        {
            using (var bindings = NewBindings(out SharedToLocalBindingReceipt boardReceipt))
            using (var reducer = new LogicalContentBindingReducer(bindings))
            {
                var sink = new RecordingSink();
                Assert.That(reducer.TryRecoverAndApply(BoardRecovery(boardReceipt, 3,
                    new[] { Entry("stroke-1", "local-curve") }), sink, out string error), Is.True, error);
                Assert.That(sink.Applied[0].Schema, Is.EqualTo(LogicalContentSchema.BoardStrokesV1));
                Assert.That(sink.Applied[0].Entries[0].Value, Is.EqualTo("local-curve"));
                foreach (PropertyInfo property in typeof(LogicalContentProjection).GetProperties())
                {
                    Assert.That(property.PropertyType.FullName, Does.Not.Contain("UnityEngine.Transform"));
                    Assert.That(property.PropertyType.FullName, Does.Not.Contain("UnityEngine.GameObject"));
                    Assert.That(property.PropertyType.FullName, Does.Not.Contain("Fusion"));
                    Assert.That(property.PropertyType.FullName, Does.Not.Contain("Socket"));
                }
            }

            using (var bindings = new SharedToLocalBindingRegistry())
            {
                Register(bindings, "room", "game", "keyed-values/v1", "receiver/game");
                Assert.That(bindings.TryResolve(Reference("room", "game", "keyed-values/v1", 2),
                    out SharedToLocalBindingReceipt receipt, out string error), Is.True, error);
                using (var reducer = new LogicalContentBindingReducer(bindings))
                {
                    var sink = new RecordingSink();
                    var snapshot = new VerifiedLogicalContentSnapshot("room", 1, "game", 1,
                        "keyed-values/v1", LogicalContentSchema.KeyedValuesV1, 1, 1, 0, false,
                        new[] { Entry("piece-e2", "pawn:white:e2") });
                    var tail = new VerifiedLogicalContentTail("room", 1, "game", 1, "keyed-values/v1", 1, 2,
                        new[] { Mutation(1, 2, LogicalContentOperationKind.Upsert, "piece-e2", "pawn:white:e4") });
                    Assert.That(reducer.TryRecoverAndApply(new VerifiedLogicalContentRecovery(receipt, snapshot, tail),
                        sink, out error), Is.True, error);
                    Assert.That(sink.Applied[0].Entries[0].Value, Is.EqualTo("pawn:white:e4"));
                }
            }
        }

        [Test]
        public void StaleBindingCrossRoomAndCompactedPrefixFailBeforeSink()
        {
            using (var bindings = NewBindings(out SharedToLocalBindingReceipt receipt))
            using (var reducer = new LogicalContentBindingReducer(bindings))
            {
                var sink = new RecordingSink();
                Register(bindings, "room", "board", "board-strokes/v1", "receiver/board", map: 2);
                Assert.That(reducer.TryRecoverAndApply(BoardRecovery(receipt, 3,
                    new[] { Entry("a", "one") }), sink, out string error), Is.False);
                Assert.That(error, Does.Contain("current local binding"));

                Assert.That(bindings.TryResolve(Reference("room", "board", "board-strokes/v1", 3),
                    out SharedToLocalBindingReceipt current, out error), Is.True, error);
                var crossRoomSnapshot = new VerifiedLogicalContentSnapshot("other-room", 1, "board", 1,
                    "board-strokes/v1", LogicalContentSchema.BoardStrokesV1, 3, 3, 0, false,
                    new[] { Entry("a", "one") });
                var tail = new VerifiedLogicalContentTail("other-room", 1, "board", 1,
                    "board-strokes/v1", 3, 3, Array.Empty<VerifiedLogicalContentMutation>());
                Assert.That(reducer.TryRecoverAndApply(new VerifiedLogicalContentRecovery(current, crossRoomSnapshot, tail),
                    sink, out error), Is.False);
                Assert.That(error, Does.Contain("identity"));

                var compacted = new VerifiedLogicalContentSnapshot("room", 1, "board", 1,
                    "board-strokes/v1", LogicalContentSchema.BoardStrokesV1, 2, 2, 3, false,
                    new[] { Entry("a", "one") });
                var compactedTail = new VerifiedLogicalContentTail("room", 1, "board", 1,
                    "board-strokes/v1", 2, 3, new[] { Mutation(2, 3, LogicalContentOperationKind.Upsert, "b", "two") });
                Assert.That(reducer.TryRecoverAndApply(new VerifiedLogicalContentRecovery(current, compacted, compactedTail),
                    sink, out error), Is.False);
                Assert.That(sink.Applied, Is.Empty);
            }
        }

        [Test]
        public void TombstoneAndLocalInvalidationCannotLeakOldContentToRecreatedBinding()
        {
            using (var bindings = new SharedToLocalBindingRegistry())
            {
                Register(bindings, "room", "board", "board-strokes/v1", "receiver/board");
                Assert.That(bindings.TryResolve(Reference("room", "board", "board-strokes/v1", 2),
                    out SharedToLocalBindingReceipt active, out string error), Is.True, error);
                using (var reducer = new LogicalContentBindingReducer(bindings))
                {
                    var sink = new RecordingSink();
                    Assert.That(reducer.TryRecoverAndApply(BoardRecovery(active, 2, new[] { Entry("a", "one") }),
                        sink, out error), Is.True, error);
                    // U1 receives the terminal durable reference first and retires
                    // active resolution.  U2 may only use the old exact local
                    // generation to erase the content it had already applied.
                    Assert.That(bindings.TryResolve(Reference("room", "board", "board-strokes/v1", 3,
                        SharedLogicalObjectLifecycle.Tombstoned), out _, out _), Is.False);
                    var tombstone = new VerifiedLogicalContentSnapshot("room", 1, "board", 1,
                        "board-strokes/v1", LogicalContentSchema.BoardStrokesV1, 3, 3, 0, true,
                        Array.Empty<LogicalContentEntry>());
                    var terminal = new VerifiedLogicalContentTail("room", 1, "board", 1,
                        "board-strokes/v1", 3, 3, Array.Empty<VerifiedLogicalContentMutation>());
                    Assert.That(reducer.TryRecoverAndApply(new VerifiedLogicalContentRecovery(active, tombstone, terminal),
                        sink, out error), Is.True, error);
                    Assert.That(sink.Applied[1].Tombstoned, Is.True);
                    Assert.That(sink.Applied[1].Entries, Is.Empty);

                    reducer.InvalidateLocalBinding();
                    Assert.That(reducer.TryRecoverAndApply(new VerifiedLogicalContentRecovery(active, tombstone, terminal),
                        sink, out error), Is.False, "A detached old terminal record cannot clear a new local binding.");
                }
            }
        }

        [Test]
        public void SinkFailureAndReentryCannotPublishPartialOrReplacementContent()
        {
            using (var bindings = NewBindings(out SharedToLocalBindingReceipt receipt))
            using (var reducer = new LogicalContentBindingReducer(bindings))
            {
                VerifiedLogicalContentRecovery recovery = BoardRecovery(receipt, 3,
                    new[] { Entry("a", "one") });
                var failing = new RecordingSink { Accept = false };
                Assert.That(reducer.TryRecoverAndApply(recovery, failing, out string error), Is.False);
                Assert.That(failing.Applied, Is.Empty);

                var reentrant = new RecordingSink();
                reentrant.BeforeApply = () => reentrant.ReentryAccepted = reducer.TryRecoverAndApply(recovery,
                    new RecordingSink(), out _);
                Assert.That(reducer.TryRecoverAndApply(recovery, reentrant, out error), Is.True, error);
                Assert.That(reentrant.ReentryAccepted, Is.False);
                Assert.That(reentrant.Applied, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void NativeIngressCopyKeepsN2BytesDetachedBeforeTheU1ReducerRuns()
        {
            using (var bindings = NewBindings(out SharedToLocalBindingReceipt receipt))
            using (var reducer = new LogicalContentBindingReducer(bindings))
            using (var ingress = new LogicalContentNativeIngressAdapter(reducer, 1))
            {
                byte[] room = Utf8("room"), objectId = Utf8("board"), schema = Utf8("board-strokes/v1");
                byte[] entryValue = Utf8("curve-a"), mutationValue = Utf8("curve-b");
                var copied = new NativeLogicalContentRecoveryCopy(room, 1, objectId, 1, schema, 1,
                    1, 1, 0, false,
                    new[] { new NativeLogicalContentEntryCopy(Utf8("stroke-a"), entryValue) }, 1, 3,
                    new[] { new NativeLogicalContentMutationCopy(1, 2, 1, Utf8("stroke-b"), mutationValue),
                            new NativeLogicalContentMutationCopy(2, 3, 1, Utf8("stroke-c"), Utf8("curve-c")) });
                // The caller's arrays model C callback memory.  Mutating them
                // after the constructor cannot change the eventual projection.
                room[0] = (byte)'x'; entryValue[0] = (byte)'x'; mutationValue[0] = (byte)'x';
                var sink = new RecordingSink();
                Assert.That(ingress.TryApplyCopied(copied, receipt, sink, out string error), Is.True, error);
                Assert.That(sink.Applied, Has.Count.EqualTo(1));
                Assert.That(sink.Applied[0].RoomId, Is.EqualTo("room"));
                Assert.That(sink.Applied[0].CommittedRevision, Is.EqualTo(3));
                Assert.That(sink.Applied[0].Entries, Has.Count.EqualTo(3));
                Assert.That(HasEntry(sink.Applied[0].Entries, "stroke-a", "curve-a"), Is.True);
                Assert.That(HasEntry(sink.Applied[0].Entries, "stroke-b", "curve-b"), Is.True);
            }
        }

        [Test]
        public void NativeMailboxPollCopiesThroughTheU1ReducerWithoutGrantingManagedSubmission()
        {
            Assert.That(CreateNativeMailbox(out ulong mailbox), Is.EqualTo(1));
            try
            {
                using (var bindings = NewBindings(out SharedToLocalBindingReceipt receipt))
                using (var reducer = new LogicalContentBindingReducer(bindings))
                using (var ingress = new LogicalContentNativeIngressAdapter(reducer, mailbox))
                using (var submitted = new NativeMailboxRecovery("room", "board", "board-strokes/v1"))
                {
                    Assert.That(SubmitNativeMailboxRecovery(mailbox, ref submitted.Snapshot, ref submitted.Tail),
                        Is.EqualTo(1), "The C fixture is the only mailbox submission authority in this test.");
                    var sink = new RecordingSink();
                    Assert.That(ingress.TryPollAndApply(receipt, sink, out string error), Is.True, error);
                    Assert.That(sink.Applied, Has.Count.EqualTo(1));
                    Assert.That(sink.Applied[0].CommittedRevision, Is.EqualTo(3));
                    Assert.That(HasEntry(sink.Applied[0].Entries, "stroke-a", "curve-a"), Is.True);
                    Assert.That(HasEntry(sink.Applied[0].Entries, "stroke-b", "curve-b"), Is.True);
                    Assert.That(HasEntry(sink.Applied[0].Entries, "stroke-c", "curve-c"), Is.True);
                    Assert.That(ingress.TryPollAndApply(receipt, sink, out error), Is.False);
                    Assert.That(error, Does.Contain("No copied native logical-content recovery"));
                }
            }
            finally
            {
                Assert.That(DestroyNativeMailbox(mailbox), Is.EqualTo(1));
            }
        }

        private static SharedToLocalBindingRegistry NewBindings(out SharedToLocalBindingReceipt receipt)
        {
            var bindings = new SharedToLocalBindingRegistry();
            Register(bindings, "room", "board", "board-strokes/v1", "receiver/board");
            Assert.That(bindings.TryResolve(Reference("room", "board", "board-strokes/v1", 3), out receipt,
                out string error), Is.True, error);
            return bindings;
        }

        private static VerifiedLogicalContentRecovery BoardRecovery(SharedToLocalBindingReceipt receipt,
            ulong snapshotRevision, IEnumerable<LogicalContentEntry> entries,
            params VerifiedLogicalContentMutation[] mutations)
        {
            ulong terminal = snapshotRevision + (ulong)mutations.Length;
            return new VerifiedLogicalContentRecovery(receipt,
                new VerifiedLogicalContentSnapshot("room", 1, "board", 1, "board-strokes/v1",
                    LogicalContentSchema.BoardStrokesV1, snapshotRevision, snapshotRevision, 0, false, entries),
                new VerifiedLogicalContentTail("room", 1, "board", 1, "board-strokes/v1", snapshotRevision,
                    terminal, mutations));
        }

        private static LogicalContentEntry Entry(string key, string value) => new LogicalContentEntry(key, value);
        private static byte[] Utf8(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        private static bool HasEntry(IReadOnlyList<LogicalContentEntry> entries, string key, string value)
        {
            foreach (LogicalContentEntry entry in entries)
                if (entry.Key == key && entry.Value == value) return true;
            return false;
        }
        private static VerifiedLogicalContentMutation Mutation(ulong predecessor, ulong committed,
            LogicalContentOperationKind kind, string key, string value) =>
            new VerifiedLogicalContentMutation(predecessor, committed, kind, key, value);

        private static void Register(SharedToLocalBindingRegistry bindings, string room, string objectId, string schema,
            string endpoint, ulong map = 1)
        {
            var evidence = new LocalBindingEvidence(endpoint, endpoint + "/chart", endpoint + "/support",
                endpoint + "/calibration", map, 1, 1, true);
            Assert.That(bindings.TryRegister(new SharedToLocalBindingDeclaration(room, 1, objectId, 1, 1, schema,
                evidence), out string error), Is.True, error);
        }

        private static AuthenticatedLogicalObjectReference Reference(string room, string objectId, string schema,
            ulong revision, SharedLogicalObjectLifecycle lifecycle = SharedLogicalObjectLifecycle.Active) =>
            new AuthenticatedLogicalObjectReference(room, 1, objectId, 1, revision, 1, schema, lifecycle);

        [StructLayout(LayoutKind.Sequential)] private struct NativeBytes { internal IntPtr Pointer; internal uint Length; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeEntry { internal NativeBytes Key, Value; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeMutation { internal ulong Predecessor, Committed; internal uint Kind, Reserved; internal NativeBytes Key, Value; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSnapshot {
            internal uint StructSize, FeatureVersion; internal NativeBytes Room; internal ulong RoomEpoch; internal NativeBytes Object;
            internal ulong ObjectGeneration; internal NativeBytes SchemaId; internal uint Schema, Tombstoned; internal ulong Committed, Watermark, Compacted;
            internal IntPtr Entries; internal uint EntryCount, EntrySize;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeTail {
            internal uint StructSize, FeatureVersion; internal NativeBytes Room; internal ulong RoomEpoch; internal NativeBytes Object;
            internal ulong ObjectGeneration; internal NativeBytes SchemaId; internal uint Reserved; internal ulong Watermark, Terminal;
            internal IntPtr Mutations; internal uint MutationCount, MutationSize;
        }

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_create_logical_content_ingress")]
        private static extern int CreateNativeMailbox(out ulong mailbox);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_destroy_logical_content_ingress")]
        private static extern int DestroyNativeMailbox(ulong mailbox);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_submit_logical_content_recovery")]
        private static extern int SubmitNativeMailboxRecovery(ulong mailbox, ref NativeSnapshot snapshot, ref NativeTail tail);

        private sealed class NativeMailboxRecovery : IDisposable
        {
            private readonly List<IntPtr> _allocations = new List<IntPtr>();
            internal NativeSnapshot Snapshot;
            internal NativeTail Tail;

            internal NativeMailboxRecovery(string room, string objectId, string schema)
            {
                NativeBytes nativeRoom = Allocate(room), nativeObject = Allocate(objectId), nativeSchema = Allocate(schema);
                var entry = new NativeEntry { Key = Allocate("stroke-a"), Value = Allocate("curve-a") };
                var firstMutation = new NativeMutation { Predecessor = 1, Committed = 2, Kind = 1, Key = Allocate("stroke-b"), Value = Allocate("curve-b") };
                var secondMutation = new NativeMutation { Predecessor = 2, Committed = 3, Kind = 1, Key = Allocate("stroke-c"), Value = Allocate("curve-c") };
                IntPtr entries = AllocateStruct(entry), mutations = AllocateStructs(firstMutation, secondMutation);
                Snapshot = new NativeSnapshot { StructSize = (uint)Marshal.SizeOf<NativeSnapshot>(), FeatureVersion = 1,
                    Room = nativeRoom, RoomEpoch = 1, Object = nativeObject, ObjectGeneration = 1, SchemaId = nativeSchema,
                    Schema = 1, Committed = 1, Watermark = 1, Entries = entries, EntryCount = 1, EntrySize = (uint)Marshal.SizeOf<NativeEntry>() };
                Tail = new NativeTail { StructSize = (uint)Marshal.SizeOf<NativeTail>(), FeatureVersion = 1,
                    Room = nativeRoom, RoomEpoch = 1, Object = nativeObject, ObjectGeneration = 1, SchemaId = nativeSchema,
                    Watermark = 1, Terminal = 3, Mutations = mutations, MutationCount = 2, MutationSize = (uint)Marshal.SizeOf<NativeMutation>() };
            }

            public void Dispose()
            {
                foreach (IntPtr pointer in _allocations) Marshal.FreeHGlobal(pointer);
                _allocations.Clear();
            }

            private NativeBytes Allocate(string value)
            {
                byte[] bytes = Utf8(value); IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, pointer, bytes.Length); _allocations.Add(pointer);
                return new NativeBytes { Pointer = pointer, Length = (uint)bytes.Length };
            }
            private IntPtr AllocateStruct<T>(T value) where T : struct
            {
                IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                Marshal.StructureToPtr(value, pointer, false); _allocations.Add(pointer); return pointer;
            }
            private IntPtr AllocateStructs<T>(T first, T second) where T : struct
            {
                int size = Marshal.SizeOf<T>(); IntPtr pointer = Marshal.AllocHGlobal(size * 2);
                Marshal.StructureToPtr(first, pointer, false);
                Marshal.StructureToPtr(second, IntPtr.Add(pointer, size), false);
                _allocations.Add(pointer); return pointer;
            }
        }

        private sealed class RecordingSink : ILogicalContentSink
        {
            internal readonly List<LogicalContentProjection> Applied = new List<LogicalContentProjection>();
            internal Action BeforeApply;
            internal bool Accept = true;
            internal bool ReentryAccepted;

            public bool TryApply(LogicalContentProjection projection, out string error)
            {
                BeforeApply?.Invoke();
                error = Accept ? null : "fixture sink failure";
                if (Accept) Applied.Add(projection);
                return Accept;
            }
        }
    }
}
#endif
