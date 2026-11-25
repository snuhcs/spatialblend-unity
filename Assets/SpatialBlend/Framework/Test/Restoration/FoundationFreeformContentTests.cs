// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Freeform;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationFreeformContentTests
    {
        private readonly List<Object> _created = new List<Object>();
        private FreeformContentCatalog _catalog;

        [SetUp]
        public void SetUp() => _catalog = new FreeformContentCatalog();

        [TearDown]
        public void TearDown()
        {
            _catalog.Dispose();
            for (int index = _created.Count - 1; index >= 0; --index)
                if (_created[index] != null)
                    Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void WhiteboardDrawsLegacyUvCircleColorAndClearIntoActualTextures()
        {
            SpatialBlendObject owner = CreateOwner("whiteboard-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "whiteboard",
                                                      new WhiteboardContentConfiguration(8, 8,
                                                                                         Color.white),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            WhiteboardContentView first = CreateWhiteboardView("first", new Vector3(2, 3, 4),
                                                                Quaternion.Euler(0, 45, 0));
            Renderer firstRenderer = first.GetComponent<Renderer>();
            Texture authoredMaterialTexture = firstRenderer.sharedMaterial.mainTexture;
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(firstRenderer.sharedMaterial.mainTexture, Is.SameAs(authoredMaterialTexture),
                        "The view must not instantiate or mutate the authored renderer material.");
            Assert.That(first.TryBind(null, out _), Is.False);
            Assert.That(firstRenderer.sharedMaterial.mainTexture, Is.SameAs(authoredMaterialTexture),
                        "A failed bind must leave the authored material untouched.");
            Vector3 center = first.transform.TransformPoint(Vector3.zero);
            Assert.That(first.TryDrawWorldLine(center, center, Color.blue, 1, out error), Is.True,
                        error);
            Assert.That(first.Texture.GetPixel(4, 4), Is.EqualTo(Color.blue));
            Assert.That(first.Texture.GetPixel(6, 4), Is.EqualTo(Color.white),
                        "The legacy brush is a radius-one circle rather than a square.");

            WhiteboardContentView late = CreateWhiteboardView("late", new Vector3(-9, 1, 7),
                                                               Quaternion.Euler(0, -70, 0));
            Assert.That(late.TryBind(model, out error), Is.True, error);
            Assert.That(late.Texture.GetPixel(4, 4), Is.EqualTo(Color.blue),
                        "A late independent view must render the model snapshot.");
            model.Clear();
            Assert.That(first.Texture.GetPixel(4, 4), Is.EqualTo(Color.white));
            Assert.That(late.Texture.GetPixel(4, 4), Is.EqualTo(Color.white));
            first.Unbind();
            Assert.That(firstRenderer.sharedMaterial.mainTexture, Is.SameAs(authoredMaterialTexture));
        }

        [Test]
        public void WhiteboardViewsLeaseSharedRenderersUntilTheirPropertyBlockIsRestored()
        {
            var rendererObject = new GameObject("leased-renderer");
            _created.Add(rendererObject);
            Renderer renderer = rendererObject.AddComponent<MeshRenderer>();
            Shader shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            renderer.sharedMaterial = new Material(shader);
            _created.Add(renderer.sharedMaterial);
            var authoredTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _created.Add(authoredTexture);
            var authoredBlock = new MaterialPropertyBlock();
            int mainTexture = Shader.PropertyToID("_MainTex");
            authoredBlock.SetTexture(mainTexture, authoredTexture);
            renderer.SetPropertyBlock(authoredBlock);

            var firstObject = new GameObject("leased-first-view");
            _created.Add(firstObject);
            WhiteboardContentView first = firstObject.AddComponent<WhiteboardContentView>();
            var secondObject = new GameObject("leased-second-view");
            _created.Add(secondObject);
            WhiteboardContentView second = secondObject.AddComponent<WhiteboardContentView>();
            Assert.That(first.Configure(renderer, out string error), Is.True, error);
            Assert.That(second.Configure(renderer, out error), Is.True, error);

            SpatialBlendObject firstOwner = CreateOwner("leased-first-owner");
            SpatialBlendObject secondOwner = CreateOwner("leased-second-owner");
            Assert.That(_catalog.TryCreateWhiteboard(firstOwner, "leased-first-board",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel firstModel,
                                                      out error), Is.True, error);
            Assert.That(_catalog.TryCreateWhiteboard(secondOwner, "leased-second-board",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel secondModel,
                                                      out error), Is.True, error);
            Assert.That(first.TryBind(firstModel, out error), Is.True, error);
            Assert.That(second.TryBind(secondModel, out _), Is.False);
            Assert.That(first.Model, Is.SameAs(firstModel));
            Assert.That(second.Model, Is.Null);
            Assert.That(second.Texture, Is.Null);
            var activeBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(activeBlock);
            Assert.That(activeBlock.GetTexture(mainTexture), Is.SameAs(first.Texture));
            Assert.That(firstModel.DrawLine(new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                                             Color.red, 0, out error), Is.True, error);
            Assert.That(first.Texture.GetPixel(2, 2), Is.EqualTo(Color.red));
            Assert.That(secondModel.CopyPixels()[2 + 2 * 4], Is.EqualTo((Color32)Color.black));

            first.Unbind();
            renderer.GetPropertyBlock(activeBlock);
            Assert.That(activeBlock.GetTexture(mainTexture), Is.SameAs(authoredTexture));
            Assert.That(second.TryBind(secondModel, out error), Is.True, error);
            renderer.GetPropertyBlock(activeBlock);
            Assert.That(activeBlock.GetTexture(mainTexture), Is.SameAs(second.Texture));
            second.Unbind();
            renderer.GetPropertyBlock(activeBlock);
            Assert.That(activeBlock.GetTexture(mainTexture), Is.SameAs(authoredTexture));
        }

        [Test]
        public void WhiteboardSnapshotAndDetachedRebindPreserveLocalContentWithoutStaleView()
        {
            SpatialBlendObject owner = CreateOwner("snapshot-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "board",
                                                      new WhiteboardContentConfiguration(8, 8,
                                                                                         Color.black),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            WhiteboardContentView first = CreateWhiteboardView("first", Vector3.zero,
                                                                Quaternion.identity);
            WhiteboardContentView second = CreateWhiteboardView("second", new Vector3(4, 0, 0),
                                                                 Quaternion.identity);
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(second.TryBind(model, out error), Is.True, error);
            Assert.That(model.DrawLine(new Vector2(.5f, .5f), new Vector2(.5f, .5f), Color.red, 0,
                                       out error), Is.True, error);
            Assert.That(model.TryCreateSnapshot(out byte[] snapshot, out error), Is.True, error);
            Color32 before = model.CopyPixels()[4 + 4 * model.Width];
            snapshot[0] ^= 0xff;
            Assert.That(model.TryApplySnapshot(snapshot, out _), Is.False);
            Assert.That(model.CopyPixels()[4 + 4 * model.Width], Is.EqualTo(before),
                        "Invalid payload preflight must not mutate the valid model.");

            first.Unbind();
            Assert.That(first.Model, Is.Null);
            Assert.That(model.DrawLine(new Vector2(.25f, .25f), new Vector2(.25f, .25f), Color.green,
                                       0, out error), Is.True, error);
            Assert.That(first.Texture, Is.Null, "Detached views must not retain live textures/listeners.");
            Assert.That(second.Texture.GetPixel(2, 2), Is.EqualTo(Color.green));
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(first.Texture.GetPixel(2, 2), Is.EqualTo(Color.green),
                        "Rebinding gets the latest model snapshot, not a stale texture.");
        }

        [Test]
        public void WhiteboardRejectsExtremeFiniteUvsAndPreservesNormalBoundaryBehavior()
        {
            SpatialBlendObject owner = CreateOwner("bounded-board-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "bounded-board",
                                                      new WhiteboardContentConfiguration(8, 8,
                                                                                         Color.black),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            Color32[] before = model.CopyPixels();
            Assert.That(model.DrawLine(new Vector2(-1000000f, .5f), new Vector2(.5f, .5f),
                                       Color.red, 1, out _), Is.False);
            Assert.That(model.DrawLine(new Vector2(.5f, .5f), new Vector2(1000000f, .5f),
                                       Color.red, 1, out _), Is.False);
            Assert.That(model.DrawLine(new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                                       Color.red, 1000000, out _), Is.False);
            CollectionAssert.AreEqual(before, model.CopyPixels());
            Assert.That(model.DrawLine(new Vector2(1f, .5f), new Vector2(1f, .5f), Color.blue,
                                       0, out error), Is.True, error);
            Assert.That(model.CopyPixels()[7 + 4 * 8], Is.EqualTo((Color32)Color.black),
                        "The legacy exact-one UV is outside the raster.");
            Assert.That(model.DrawLine(new Vector2(.999f, .5f), new Vector2(.999f, .5f),
                                       Color.blue, 0, out error), Is.True, error);
            Assert.That(model.CopyPixels()[7 + 4 * 8], Is.EqualTo((Color32)Color.blue));
        }

        [Test]
        public void RemoveRetiresModelDetachesTwoViewsAndReplacementCannotReviveOldReference()
        {
            SpatialBlendObject owner = CreateOwner("retire-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "reused-id",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel old,
                                                      out string error), Is.True, error);
            WhiteboardContentView first = CreateWhiteboardView("retire-first", Vector3.zero,
                                                                Quaternion.identity);
            WhiteboardContentView second = CreateWhiteboardView("retire-second", Vector3.right,
                                                                 Quaternion.identity);
            Assert.That(first.TryBind(old, out error), Is.True, error);
            Assert.That(second.TryBind(old, out error), Is.True, error);
            Assert.That(_catalog.Remove("reused-id"), Is.True);
            Assert.That(old.IsLive, Is.False);
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.Null);
            Assert.That(first.Texture, Is.Null);
            Assert.That(second.Texture, Is.Null);
            Assert.That(old.DrawLine(Vector2.zero, Vector2.zero, Color.white, 0, out _), Is.False);
            Assert.That(first.TryBind(old, out _), Is.False);
            Assert.That(_catalog.TryCreateWhiteboard(owner, "reused-id",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.white),
                                                      out WhiteboardContentModel replacement,
                                                      out error), Is.True, error);
            Assert.That(replacement, Is.Not.SameAs(old));
            Assert.That(replacement.IsLive, Is.True);
            Assert.That(old.TryClear(out _), Is.False);
            Assert.That(first.TryBind(replacement, out error), Is.True, error);
        }

        [Test]
        public void ThrowingChangedAndRetiredSubscribersDoNotBlockViewsOrRetirementRecreation()
        {
            SpatialBlendObject owner = CreateOwner("callback-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "callback-board",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            bool laterChanged = false;
            model.Changed += _ => throw new InvalidOperationException("changed observer failure");
            model.Retired += _ => throw new InvalidOperationException("retired observer failure");
            WhiteboardContentView first = CreateWhiteboardView("callback-first", Vector3.zero,
                                                                Quaternion.identity);
            WhiteboardContentView second = CreateWhiteboardView("callback-second", Vector3.right,
                                                                 Quaternion.identity);
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(second.TryBind(model, out error), Is.True, error);
            model.Changed += _ => laterChanged = true;
            LogAssert.Expect(LogType.Exception, new Regex("changed observer failure"));
            Assert.That(model.DrawLine(new Vector2(.5f, .5f), new Vector2(.5f, .5f), Color.red, 0,
                                       out error), Is.True, error);
            Assert.That(laterChanged, Is.True);
            Assert.That(first.Texture.GetPixel(2, 2), Is.EqualTo(Color.red));
            Assert.That(second.Texture.GetPixel(2, 2), Is.EqualTo(Color.red));

            bool laterRetired = false;
            bool recreated = false;
            WhiteboardContentModel replacement = null;
            model.Retired += _ => laterRetired = true;
            model.Retired += retired => recreated = _catalog.TryCreateWhiteboard(
                owner, "callback-board", new WhiteboardContentConfiguration(4, 4, Color.white),
                out replacement, out _);
            LogAssert.Expect(LogType.Exception, new Regex("retired observer failure"));
            Assert.That(_catalog.Remove("callback-board"), Is.True);
            Assert.That(laterRetired, Is.True);
            Assert.That(recreated, Is.True);
            Assert.That(replacement, Is.Not.Null);
            Assert.That(replacement.IsLive, Is.True);
            Assert.That(model.IsLive, Is.False);
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.Null);
        }

        [Test]
        public void ChangedCallbackRemovalAndRecreationStopsLaterOldModelNotifications()
        {
            SpatialBlendObject owner = CreateOwner("changed-reentry-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "changed-reentry",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel old,
                                                      out string error), Is.True, error);
            bool recreated = false;
            bool laterObserverCalled = false;
            WhiteboardContentModel replacement = null;
            old.Changed += changed =>
            {
                _catalog.Remove("changed-reentry");
                recreated = _catalog.TryCreateWhiteboard(owner, "changed-reentry",
                    new WhiteboardContentConfiguration(4, 4, Color.white), out replacement, out _);
            };
            old.Changed += _ => laterObserverCalled = true;
            Assert.That(old.DrawLine(Vector2.zero, Vector2.zero, Color.red, 0, out error), Is.True,
                        error);
            Assert.That(recreated, Is.True);
            Assert.That(laterObserverCalled, Is.False);
            Assert.That(old.IsLive, Is.False);
            Assert.That(replacement, Is.Not.Null);
            Assert.That(_catalog.TryGetWhiteboard("changed-reentry", out WhiteboardContentModel found),
                        Is.True);
            Assert.That(found, Is.SameAs(replacement));
        }

        [Test]
        public void DisposeCannotBeReenteredToCreateLiveContent()
        {
            SpatialBlendObject owner = CreateOwner("dispose-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "dispose-board",
                                                      new WhiteboardContentConfiguration(4, 4,
                                                                                         Color.black),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            bool recreated = true;
            model.Retired += retired => recreated = _catalog.TryCreateWhiteboard(
                owner, "dispose-reentry", new WhiteboardContentConfiguration(4, 4, Color.white),
                out _, out _);
            _catalog.Dispose();
            Assert.That(recreated, Is.False);
            Assert.That(model.IsLive, Is.False);
            Assert.That(_catalog.Count, Is.Zero);
            Assert.That(_catalog.TryGet("dispose-reentry", out _), Is.False);
        }

        [Test]
        public void ChessPieceIdsUseStrictUtf8BoundsAndNullLookupsFailClosed()
        {
            SpatialBlendObject owner = CreateOwner("utf8-owner");
            string exactly128Bytes = new string('\u00e9', 64);
            string over128Bytes = new string('\u00e9', 65);
            string unpairedSurrogate = "\ud800";
            var validPiece = new ChessPiecePose(exactly128Bytes, Vector3.zero, Quaternion.identity);
            Assert.That(_catalog.TryCreateChess(owner, "utf8-chess", new[] { validPiece },
                                                 out ChessContentModel model, out string error), Is.True,
                        error);
            ChessPiecePose before = model.CopyPieces()[0];
            Assert.That(model.TrySetPiecePose(over128Bytes, Vector3.one, Quaternion.identity, out _),
                        Is.False);
            Assert.That(model.TryGetPiecePose(null, out _), Is.False);
            Assert.That(_catalog.Remove(null), Is.False);
            Assert.That(ChessSnapshotCodec.TryEncode(new[]
                        { new ChessPiecePose(unpairedSurrogate, Vector3.zero, Quaternion.identity) },
                        out _, out _), Is.False);
            Assert.That(_catalog.TryCreateChess(owner, "unpaired-chess",
                                                 new[] { new ChessPiecePose(unpairedSurrogate,
                                                     Vector3.zero, Quaternion.identity) }, out _, out _),
                        Is.False);
            CollectionAssert.AreEqual(new[] { before }, model.CopyPieces());
            Assert.That(_catalog.Count, Is.EqualTo(1));
        }

        [Test]
        public void ChessUsesBoardLocalPosesAcrossIndependentTransformsAndRejectsBadPayload()
        {
            SpatialBlendObject owner = CreateOwner("chess-owner");
            var initial = new[]
            {
                new ChessPiecePose("pawn-a", new Vector3(.25f, .1f, -.4f),
                                   Quaternion.Euler(0, 30, 0))
            };
            Assert.That(_catalog.TryCreateChess(owner, "chess", initial,
                                                 out ChessContentModel model, out string error), Is.True,
                        error);
            ChessContentView first = CreateChessView("first", new Vector3(4, 2, -1),
                                                      Quaternion.Euler(0, 50, 0), Vector3.one * 2f);
            ChessContentView second = CreateChessView("second", new Vector3(-8, .5f, 3),
                                                       Quaternion.Euler(0, -20, 0), Vector3.one * .5f);
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(second.TryBind(model, out error), Is.True, error);
            AssertBoardLocal(first, "pawn-a", initial[0]);
            AssertBoardLocal(second, "pawn-a", initial[0]);

            var moved = new ChessPiecePose("pawn-a", new Vector3(-.3f, .2f, .45f),
                                           Quaternion.Euler(10, -40, 5));
            Assert.That(model.TrySetPiecePose(moved.PieceId, moved.BoardLocalPosition,
                                              moved.BoardLocalRotation, out error), Is.True, error);
            AssertBoardLocal(first, "pawn-a", moved);
            AssertBoardLocal(second, "pawn-a", moved);

            Assert.That(model.TryCreateSnapshot(out byte[] snapshot, out error), Is.True, error);
            // Header (8), UTF-8 length (2), "pawn-a" (6), then local x float.
            snapshot[16] = 0;
            snapshot[17] = 0;
            snapshot[18] = 0xc0;
            snapshot[19] = 0x7f;
            Assert.That(model.TryApplySnapshot(snapshot, out _), Is.False);
            AssertBoardLocal(first, "pawn-a", moved);
            AssertBoardLocal(second, "pawn-a", moved);
        }

        [Test]
        public void InvalidLateChessBindingAndConfigurationLeaveExistingBindingUntouched()
        {
            SpatialBlendObject owner = CreateOwner("binding-owner");
            var pieces = new[] { new ChessPiecePose("king", Vector3.zero, Quaternion.identity) };
            Assert.That(_catalog.TryCreateChess(owner, "chess", pieces,
                                                 out ChessContentModel model, out string error), Is.True,
                        error);
            ChessContentView valid = CreateChessView("valid", Vector3.zero, Quaternion.identity,
                                                      Vector3.one, "king");
            Assert.That(valid.TryBind(model, out error), Is.True, error);
            ChessContentView invalid = CreateChessView("invalid", Vector3.one, Quaternion.identity,
                                                        Vector3.one, "wrong-piece");
            Assert.That(invalid.TryBind(model, out _), Is.False);
            Assert.That(valid.Model, Is.SameAs(model));
            Assert.That(valid.Configure(valid.BoardTransform,
                                        new[] { new ChessPieceViewBinding("", null) }, out _), Is.False);
            Assert.That(valid.Model, Is.SameAs(model));
            Assert.That(model.TrySetPiecePose("king", Vector3.right, Quaternion.identity, out error),
                        Is.True, error);
            AssertBoardLocal(valid, "king", new ChessPiecePose("king", Vector3.right,
                                                                 Quaternion.identity));
        }

        [Test]
        public void ChessRejectsAliasedBindingsAndKeepsValidConfigurationAndTransforms()
        {
            var board = new GameObject("alias-board");
            _created.Add(board);
            var validPiece = new GameObject("valid-piece");
            _created.Add(validPiece);
            validPiece.transform.position = new Vector3(2, 3, 4);
            Quaternion beforeRotation = validPiece.transform.rotation;
            Vector3 beforePosition = validPiece.transform.position;
            var view = board.AddComponent<ChessContentView>();
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("valid", validPiece.transform) },
                                       out string error), Is.True, error);
            var nested = new GameObject("nested-piece");
            _created.Add(nested);
            nested.transform.SetParent(validPiece.transform);
            var ancestor = new GameObject("board-ancestor");
            _created.Add(ancestor);
            board.transform.SetParent(ancestor.transform);

            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("one", validPiece.transform),
                                               new ChessPieceViewBinding("two", validPiece.transform) },
                                       out _), Is.False);
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("root", board.transform) }, out _),
                        Is.False);
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("ancestor", ancestor.transform) },
                                       out _), Is.False);
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding("parent", validPiece.transform),
                                               new ChessPieceViewBinding("child", nested.transform) }, out _),
                        Is.False);
            Assert.That(view.TryGetPieceTransform("valid", out Transform retained), Is.True);
            Assert.That(retained, Is.SameAs(validPiece.transform));
            Assert.That(view.TryGetPieceTransform(null, out _), Is.False);
            Assert.That(view.TryGetPieceTransform("missing", out _), Is.False);
            Assert.That(validPiece.transform.position, Is.EqualTo(beforePosition));
            Assert.That(validPiece.transform.rotation, Is.EqualTo(beforeRotation));

            SpatialBlendObject owner = CreateOwner("alias-owner");
            Assert.That(_catalog.TryCreateChess(owner, "empty-chess", new ChessPiecePose[0],
                                                 out ChessContentModel empty, out error), Is.True, error);
            FieldInfo serializedBindings = typeof(ChessContentView).GetField("_pieceBindings",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(serializedBindings, Is.Not.Null);
            serializedBindings.SetValue(view, new[]
            {
                new ChessPieceViewBinding("one", validPiece.transform),
                new ChessPieceViewBinding("two", validPiece.transform)
            });
            Assert.That(view.TryBind(empty, out _), Is.False,
                        "Serialized bad bindings must be rejected before replacing a valid configuration.");
            Assert.That(view.TryGetPieceTransform("valid", out retained), Is.True);
            Assert.That(retained, Is.SameAs(validPiece.transform));

            var singularObject = new GameObject("singular-board");
            _created.Add(singularObject);
            singularObject.transform.localScale = new Vector3(0, 1, 1);
            var singularView = singularObject.AddComponent<ChessContentView>();
            Assert.That(singularView.Configure(singularObject.transform,
                                               new[] { new ChessPieceViewBinding("valid", validPiece.transform) },
                                               out _), Is.False);
        }

        [Test]
        public void ContentHostRemovesCatalogEntryWhenItsSpatialBlendObjectIsDestroyed()
        {
            SpatialBlendObject owner = CreateOwner("lifecycle-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "owned-board",
                                                      new WhiteboardContentConfiguration(2, 2,
                                                                                         Color.white),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            Assert.That(owner.GetComponent<FreeformContentHost>(), Is.Not.Null);
            WhiteboardContentView first = CreateWhiteboardView("destroy-first", Vector3.zero,
                                                                Quaternion.identity);
            WhiteboardContentView second = CreateWhiteboardView("destroy-second", Vector3.right,
                                                                 Quaternion.identity);
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(second.TryBind(model, out error), Is.True, error);
            GameObject ownerObject = owner.gameObject;
            // Invoke the runtime hook explicitly in this Edit Mode contract;
            // do not enable editor-time native execution merely for this test.
            InvokeOwnerDestruction(owner);
            Object.DestroyImmediate(ownerObject);
            _created.Remove(ownerObject);
            Assert.That(_catalog.TryGet("owned-board", out _), Is.False);
            Assert.That(model.IsLive, Is.False);
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.Null);
            Assert.That(model.TryClear(out _), Is.False);
        }

        [Test]
        public void ContentHostRetiresViewsWhenOnlyItsOwnerComponentIsDestroyed()
        {
            SpatialBlendObject owner = CreateOwner("component-lifecycle-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "component-owned-board",
                                                      new WhiteboardContentConfiguration(2, 2,
                                                                                         Color.white),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            WhiteboardContentView first = CreateWhiteboardView("component-destroy-first", Vector3.zero,
                                                                Quaternion.identity);
            WhiteboardContentView second = CreateWhiteboardView("component-destroy-second", Vector3.right,
                                                                 Quaternion.identity);
            Assert.That(first.TryBind(model, out error), Is.True, error);
            Assert.That(second.TryBind(model, out error), Is.True, error);
            bool recreatedDuringOwnerDestruction = true;
            model.Retired += retired => recreatedDuringOwnerDestruction = _catalog.TryCreateWhiteboard(
                owner, "component-owned-board", new WhiteboardContentConfiguration(2, 2, Color.black),
                out _, out _);
            InvokeOwnerDestruction(owner);
            Object.DestroyImmediate(owner);
            Assert.That(_catalog.TryGet("component-owned-board", out _), Is.False);
            Assert.That(model.IsLive, Is.False);
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.Null);
            Assert.That(first.Texture, Is.Null);
            Assert.That(second.Texture, Is.Null);
            Assert.That(recreatedDuringOwnerDestruction, Is.False);
        }

        [Test]
        public void WhiteboardWorldDrawingRejectsInvalidPointsAndSingularViewWithoutMutation()
        {
            SpatialBlendObject owner = CreateOwner("world-projection-owner");
            Assert.That(_catalog.TryCreateWhiteboard(owner, "world-projection-board",
                                                      new WhiteboardContentConfiguration(8, 8,
                                                                                         Color.black),
                                                      out WhiteboardContentModel model,
                                                      out string error), Is.True, error);
            WhiteboardContentView view = CreateWhiteboardView("world-projection-view", Vector3.zero,
                                                               Quaternion.identity);
            Assert.That(view.TryBind(model, out error), Is.True, error);
            Color32[] before = model.CopyPixels();
            view.transform.localScale = new Vector3(0f, 1f, 1f);
            Assert.That(view.TryDrawWorldLine(Vector3.zero, Vector3.right, Color.red, 0, out _),
                        Is.False);
            view.transform.localScale = Vector3.one;
            Assert.That(view.TryDrawWorldLine(new Vector3(float.NaN, 0f, 0f), Vector3.right,
                                              Color.red, 0, out _), Is.False);
            CollectionAssert.AreEqual(before, model.CopyPixels());
        }

        [Test]
        public void MonoBehaviourCoordinatorCreatesAndRemovesTypedContent()
        {
            var coordinatorObject = new GameObject("freeform-coordinator");
            _created.Add(coordinatorObject);
            var coordinator = coordinatorObject.AddComponent<FreeformContentCoordinator>();
            SpatialBlendObject owner = CreateOwner("coordinator-owner");
            Assert.That(coordinator.TryCreateWhiteboard(owner, "coordinator-board",
                                                        new WhiteboardContentConfiguration(0, 4,
                                                                                           Color.white),
                                                        out _, out _), Is.False);
            Assert.That(coordinator.Catalog.Count, Is.Zero);
            Assert.That(owner.GetComponent<FreeformContentHost>(), Is.Null,
                        "Rejected configuration must not attach lifecycle state.");
            Assert.That(coordinator.TryCreateWhiteboard(owner, "coordinator-board",
                                                        new WhiteboardContentConfiguration(4, 4,
                                                                                           Color.white),
                                                        out WhiteboardContentModel model,
                                                        out string error), Is.True, error);
            Assert.That(coordinator.Catalog.TryGetWhiteboard("coordinator-board", out var found),
                        Is.True);
            Assert.That(found, Is.SameAs(model));
            Assert.That(coordinator.Remove("coordinator-board"), Is.True);
            Assert.That(coordinator.Catalog.TryGet("coordinator-board", out _), Is.False);
        }

        private static void InvokeOwnerDestruction(SpatialBlendObject owner)
        {
            MethodInfo destroy = typeof(SpatialBlendObject).GetMethod("OnDestroy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(destroy, Is.Not.Null);
            destroy.Invoke(owner, null);
        }

        private SpatialBlendObject CreateOwner(string name)
        {
            var value = new GameObject(name);
            _created.Add(value);
            return value.AddComponent<SpatialBlendObject>();
        }

        private WhiteboardContentView CreateWhiteboardView(string name, Vector3 position,
                                                            Quaternion rotation)
        {
            var value = new GameObject(name);
            _created.Add(value);
            value.transform.SetPositionAndRotation(position, rotation);
            var renderer = value.AddComponent<MeshRenderer>();
            Shader shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            renderer.sharedMaterial = new Material(shader);
            _created.Add(renderer.sharedMaterial);
            var view = value.AddComponent<WhiteboardContentView>();
            Assert.That(view.Configure(renderer, out string error), Is.True, error);
            return view;
        }

        private ChessContentView CreateChessView(string name, Vector3 position, Quaternion rotation,
                                                  Vector3 scale, string pieceId = "pawn-a")
        {
            var board = new GameObject(name + "-board");
            _created.Add(board);
            board.transform.SetPositionAndRotation(position, rotation);
            board.transform.localScale = scale;
            var piece = new GameObject(name + "-" + pieceId);
            _created.Add(piece);
            var view = board.AddComponent<ChessContentView>();
            Assert.That(view.Configure(board.transform,
                                       new[] { new ChessPieceViewBinding(pieceId, piece.transform) },
                                       out string error), Is.True, error);
            return view;
        }

        private static void AssertBoardLocal(ChessContentView view, string pieceId,
                                             ChessPiecePose expected)
        {
            Assert.That(view.TryGetPieceTransform(pieceId, out Transform piece), Is.True);
            Vector3 actualPosition = view.BoardTransform.InverseTransformPoint(piece.position);
            Quaternion actualRotation = Quaternion.Inverse(view.BoardTransform.rotation) *
                                      piece.rotation;
            Assert.That(Vector3.Distance(actualPosition, expected.BoardLocalPosition), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(actualRotation, expected.BoardLocalRotation), Is.LessThan(.01f));
        }
    }

    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationFreeformContentNativeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("freeform-native-world");
        }

        [TearDown]
        public void TearDown()
        {
            _world.Shutdown();
            for (int index = _created.Count - 1; index >= 0; --index)
                Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void ContentCanAttachToAndOutliveNativeObjectRegistrationUntilExplicitRemoval()
        {
            SpatialBlendObject owner = Create<SpatialBlendObject>("native-board");
            owner.Configure(_world, "local", "native-board");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            var catalog = new FreeformContentCatalog();
            try
            {
                Assert.That(catalog.TryCreateWhiteboard(owner, "native-whiteboard",
                                                         new WhiteboardContentConfiguration(4, 4,
                                                                                            Color.white),
                                                         out _, out error), Is.True, error);
                Assert.That(owner.IsRegistered, Is.True);
                owner.Unregister();
                Assert.That(owner.IsRegistered, Is.False,
                            "The actual Foundation native registration must be removable independently of content.");
                Assert.That(catalog.TryGetWhiteboard("native-whiteboard", out _), Is.True,
                            "Local content lifetime follows the Unity object, not native registration.");
                Assert.That(catalog.Remove("native-whiteboard"), Is.True);
            }
            finally
            {
                catalog.Dispose();
            }
        }

        private T Create<T>(string name) where T : Component
        {
            var value = new GameObject(name);
            _created.Add(value);
            return value.AddComponent<T>();
        }
    }
}
#endif
