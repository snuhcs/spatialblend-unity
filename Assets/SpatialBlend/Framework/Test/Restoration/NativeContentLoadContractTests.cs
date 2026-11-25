// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Objects;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class NativeContentLoadContractTests
    {
        [UnityTest]
        public IEnumerator NativeEntityPath_PromotesBeforeContentReadyAndRetainsImportLifetime()
        {
            var target = new GameObject("gltf-target");
            var entity = target.AddComponent<SbNativeEntity>();
            bool contentReady = false;
            bool promotedWhenReady = false;
            try
            {
                UnityMainThreadDispatcher.Initialize();
                SetPrivate(entity, "_contentBytes", MinimalGlb());
                entity.ContentReady += _ =>
                {
                    promotedWhenReady = target.transform.Find("GLTF") != null;
                    contentReady = true;
                };
                entity.LoadContentAsync();
                yield return AwaitSignal(() => contentReady, "Native GLTF content did not become ready.");
                Assert.That(promotedWhenReady, Is.True,
                    "ContentReady is emitted only after the GLTF root is promoted.");
                Transform promoted = target.transform.Find("GLTF");
                Assert.That(promoted, Is.Not.Null);
                Assert.That(promoted.GetComponent<GltfImportLifetime>(), Is.Not.Null,
                    "The promoted root owns GLTFast import assets for its live lifetime.");
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator GlbBytesTravelAsSharedUserDataAndTheReceiverInstantiatesThem()
        {
            // T3.4, parity row O3: the sender's bytes are written by the content
            // block, and the receiver's block restores them and loads the model.
            var senderObject = new GameObject("glb-sender");
            var receiverObject = new GameObject("glb-receiver");
            var sender = senderObject.AddComponent<SbNativeEntity>();
            var receiver = receiverObject.AddComponent<SbNativeEntity>();
            byte[] glb = MinimalGlb();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                UnityMainThreadDispatcher.Initialize();
                sender.SetContentData(glb);
                SerializationBlock senderBlock = ObjectSerializer.CreateContentBlock(sender);
                SerializationBlock receiverBlock = ObjectSerializer.CreateContentBlock(receiver);
                ulong length = senderBlock.getLength();
                Assert.That(length, Is.EqualTo((ulong)(8 + glb.Length)), "[id][size][bytes]");
                buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
                senderBlock.serialize(buffer);
                receiverBlock.deserialize(buffer, length);
                Assert.That(receiver.GetContentData(), Is.EqualTo(glb));
                yield return AwaitSignal(() => receiverObject.transform.Find("GLTF") != null,
                    "The receiver did not instantiate the shared GLB.");
            }
            finally
            {
                if (buffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(senderObject);
                UnityEngine.Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void EmptyContentWritesNoBlockSoANamelessPlaceholderIsNeverShared()
        {
            var target = new GameObject("empty-glb");
            var entity = target.AddComponent<SbNativeEntity>();
            try
            {
                Assert.That(ObjectSerializer.CreateContentBlock(entity).getLength(), Is.EqualTo(0UL));
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator CancellationAfterParseOrInstantiation_ReturnsFalseAndLeavesNoStaging()
        {
            var afterParse = new GameObject("cancel-after-parse");
            var afterInstantiate = new GameObject("cancel-after-instantiate");
            bool parsedResult = true;
            bool instantiatedResult = true;
            int parsedChecks = 0;
            int instantiatedChecks = 0;
            try
            {
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(afterParse, MinimalGlb(),
                    () => ++parsedChecks < 2), value => parsedResult = value);
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(afterInstantiate, MinimalGlb(),
                    () => ++instantiatedChecks < 3), value => instantiatedResult = value);
                Assert.That(parsedResult, Is.False);
                Assert.That(instantiatedResult, Is.False);
                Assert.That(afterParse.transform.Find("__SpatialBlendGltfStaging"), Is.Null);
                Assert.That(afterInstantiate.transform.Find("__SpatialBlendGltfStaging"), Is.Null);
                Assert.That(afterInstantiate.transform.Find("GLTF"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(afterInstantiate);
                UnityEngine.Object.DestroyImmediate(afterParse);
            }
        }

        [UnityTest]
        public IEnumerator RepeatedLoad_RetiresOnlyPriorOwnedContentAndPreservesAuthoredChildren()
        {
            var target = new GameObject("repeat-target");
            var authored = new GameObject("authored-child");
            authored.transform.SetParent(target.transform, false);
            bool first = false;
            bool second = false;
            try
            {
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(target, MinimalGlb()), value => first = value);
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(target, MinimalGlb()), value => second = value);
                Assert.That(first && second, Is.True);
                Assert.That(target.transform.Find("authored-child"), Is.SameAs(authored.transform));
                int ownedRoots = 0;
                for (int i = 0; i < target.transform.childCount; ++i)
                    if (target.transform.GetChild(i).GetComponent<GltfImportLifetime>() != null) ++ownedRoots;
                Assert.That(ownedRoots, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator InvalidGlb_ThroughNativeEntityPathDoesNotEmitContentReady()
        {
            var target = new GameObject("invalid-gltf-target");
            var entity = target.AddComponent<SbNativeEntity>();
            int contentReady = 0;
            entity.ContentReady += _ => ++contentReady;
            try
            {
                UnityMainThreadDispatcher.Initialize();
                SetPrivate(entity, "_contentBytes", new byte[] { 1, 2, 3 });
                LogAssert.Expect(LogType.Error, new Regex("\\[GltfRuntimeLoader\\] Failed to parse GLTF binary"));
                entity.LoadContentAsync();
                PumpDispatcher();
                yield return null;
                Assert.That(contentReady, Is.EqualTo(0));
                Assert.That(target.transform.Find("GLTF"), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [UnityTest]
        public IEnumerator RetiredNativeEntity_DoesNotEmitContentReadyOrMutateItsTarget()
        {
            var target = new GameObject("retired-target");
            var authored = new GameObject("authored-child");
            authored.transform.SetParent(target.transform, false);
            var entity = target.AddComponent<SbNativeEntity>();
            int contentReady = 0;
            try
            {
                UnityMainThreadDispatcher.Initialize();
                SetPrivate(entity, "_contentBytes", MinimalGlb());
                SetPrivate(entity, "_retiredReconstructedReplica", true);
                entity.ContentReady += _ => ++contentReady;
                entity.LoadContentAsync();
                yield return null;
                Assert.That(contentReady, Is.EqualTo(0));
                Assert.That(target.transform.childCount, Is.EqualTo(1));
                Assert.That(target.transform.GetChild(0), Is.SameAs(authored.transform));
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        private static IEnumerator Await(Task<bool> task, Action<bool> complete)
        {
            const int maxFrames = 300;
            for (int frame = 0; !task.IsCompleted && frame < maxFrames; ++frame)
                yield return null;
            Assert.That(task.IsCompleted, Is.True, "GLTFast operation exceeded the bounded test deadline.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            complete(task.Result);
        }

        private static IEnumerator AwaitSignal(Func<bool> predicate, string timeoutMessage)
        {
            const int maxFrames = 300;
            for (int frame = 0; !predicate() && frame < maxFrames; ++frame)
            {
                PumpDispatcher();
                yield return null;
            }
            PumpDispatcher();
            Assert.That(predicate(), Is.True, timeoutMessage);
        }

        // EditMode coroutine tests do not guarantee MonoBehaviour.Update dispatch
        // for a pre-existing singleton. Pump the production dispatcher queue rather
        // than bypassing SbNativeEntity's actual asynchronous call path.
        private static void PumpDispatcher()
        {
            var update = typeof(UnityMainThreadDispatcher).GetMethod("Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            update.Invoke(UnityMainThreadDispatcher.Instance, null);
        }

        private static void SetPrivate(object target, string name, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                if (field == null) continue;
                field.SetValue(target, value);
                return;
            }
            throw new MissingFieldException(target.GetType().FullName, name);
        }

        private static byte[] MinimalGlb()
        {
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}");
            int paddedLength = (json.Length + 3) & ~3;
            byte[] glb = new byte[12 + 8 + paddedLength];
            WriteUInt32(glb, 0, 0x46546C67); // glTF
            WriteUInt32(glb, 4, 2);
            WriteUInt32(glb, 8, (uint)glb.Length);
            WriteUInt32(glb, 12, (uint)paddedLength);
            WriteUInt32(glb, 16, 0x4E4F534A); // JSON
            Buffer.BlockCopy(json, 0, glb, 20, json.Length);
            for (int i = 20 + json.Length; i < glb.Length; ++i) glb[i] = 0x20;
            return glb;
        }

        private static void WriteUInt32(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
#endif
