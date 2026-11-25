// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Freeform;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public sealed class WhiteboardSurfaceChartTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int index = _created.Count - 1; index >= 0; --index)
                if (_created[index] != null)
                    Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void ExplicitFrame_UsesRendererPlaneForOrderedCornersAndUvOnMovedTiltedScaledBoard()
        {
            Assert.That(WhiteboardSurfaceChart.TryCreate(WhiteboardSurfaceChart.CurrentVersion,
                new Vector2(-2f, -1f), new Vector2(3f, 4f), out WhiteboardSurfaceChart chart,
                out string error), Is.True, error);
            GameObject root = Create("root");
            root.transform.SetPositionAndRotation(new Vector3(4f, -3f, 2f),
                Quaternion.Euler(12f, 41f, -18f));
            // The reflected, non-uniform parent plus tilted child produces a
            // complete affine with shear. A normal cannot be checked through
            // TransformDirection, which drops that affine information.
            root.transform.localScale = new Vector3(-1.5f, .75f, 2f);
            GameObject surface = Create("vertical-tilted-surface");
            surface.transform.SetParent(root.transform, false);
            surface.transform.localPosition = new Vector3(.25f, 1.5f, -.4f);
            surface.transform.localRotation = Quaternion.Euler(90f, 22f, 17f);
            surface.transform.localScale = new Vector3(2f, .5f, 1.25f);

            Assert.That(chart.TryCreateFrame(surface.transform, out WhiteboardSurfaceFrame frame,
                out error), Is.True, error);
            Vector3[] actual = frame.GetWorldCorners();
            Vector3[] local = chart.GetLocalCorners();
            Assert.That(actual, Has.Length.EqualTo(4));
            for (int index = 0; index < actual.Length; ++index)
            {
                // Independent numerical oracle: the complete renderer affine,
                // not root yaw, bounds, or an enclosing collider.
                AssertVector(actual[index], surface.transform.localToWorldMatrix.MultiplyPoint3x4(local[index]));
                Assert.That(frame.TryWorldToUv(actual[index], out Vector2 uv), Is.True);
                AssertVector(uv, new Vector2(index == 1 || index == 2 ? 1f : 0f,
                    index >= 2 ? 1f : 0f));
            }
            Vector3 boundaryNormal = Vector3.Cross(actual[1] - actual[0], actual[3] - actual[0]).normalized;
            Matrix4x4 affine = surface.transform.localToWorldMatrix;
            // Cross(Mx, My) is the determinant-scaled inverse-transpose normal.
            // It preserves both shear and handedness from the negative scale.
            Vector3 inverseTransposeNormal = Vector3.Cross(affine.MultiplyVector(Vector3.right),
                affine.MultiplyVector(Vector3.up)).normalized;
            AssertVector(boundaryNormal, inverseTransposeNormal);
            Assert.That(affine.determinant, Is.LessThan(0f),
                "The oracle must exercise reflected winding rather than silently normalizing scale.");
        }

        [Test]
        public void ExplicitViewFrame_DoesNotUseOffComponentViewTransform_AndLegacyPathRemainsUnchanged()
        {
            GameObject surface = Create("renderer-plane");
            surface.transform.SetPositionAndRotation(new Vector3(7f, 2f, -5f), Quaternion.Euler(0f, 90f, 0f));
            surface.transform.localScale = new Vector3(3f, 2f, 1f);
            MeshRenderer renderer = surface.AddComponent<MeshRenderer>();
            Assert.That(WhiteboardSurfaceChart.CenteredUnitQuad.TryCreateFrame(surface.transform,
                out WhiteboardSurfaceFrame frame, out string error), Is.True, error);

            GameObject explicitViewObject = Create("off-component-explicit-view");
            explicitViewObject.transform.position = new Vector3(-100f, 20f, 10f);
            WhiteboardContentView explicitView = explicitViewObject.AddComponent<WhiteboardContentView>();
            Assert.That(explicitView.Configure(renderer, frame, out error), Is.True, error);
            AssertVector(explicitView.WorldToUv(surface.transform.TransformPoint(new Vector3(.5f, -.5f, 0f))),
                new Vector2(1f, 0f));

            GameObject legacyViewObject = Create("off-component-legacy-view");
            legacyViewObject.transform.position = new Vector3(10f, 20f, 30f);
            WhiteboardContentView legacyView = legacyViewObject.AddComponent<WhiteboardContentView>();
            Assert.That(legacyView.Configure(renderer, out error), Is.True, error);
            AssertVector(legacyView.WorldToUv(legacyViewObject.transform.TransformPoint(Vector3.zero)),
                new Vector2(.5f, .5f));
        }

        [Test]
        public void Chart_RejectsUnknownVersionDegenerateBoundsAndSingularPlane()
        {
            Assert.That(WhiteboardSurfaceChart.TryCreate(99, Vector2.zero, Vector2.one,
                out _, out _), Is.False);
            Assert.That(WhiteboardSurfaceChart.TryCreate(WhiteboardSurfaceChart.CurrentVersion,
                Vector2.zero, new Vector2(0f, 1f), out _, out _), Is.False);
            GameObject singular = Create("singular-surface");
            singular.transform.localScale = new Vector3(0f, 1f, 1f);
            Assert.That(WhiteboardSurfaceChart.CenteredUnitQuad.TryCreateFrame(singular.transform,
                out _, out _), Is.False);
        }

        [Test]
        public void ReceiverSupportAttachment_IsRendererPlaneChildAndBackendDestroyRemovesOnlyItsOwnChild()
        {
            GameObject whiteboardRoot = Create("authored-whiteboard-root");
            FreeformSceneWhiteboardDefinition whiteboard =
                whiteboardRoot.AddComponent<FreeformSceneWhiteboardDefinition>();
            GameObject surface = Create("authored-whiteboard-renderer");
            surface.transform.SetParent(whiteboardRoot.transform, false);
            MeshRenderer renderer = surface.AddComponent<MeshRenderer>();
            SetField(whiteboard, "_movableRoot", whiteboardRoot.transform);
            SetField(whiteboard, "_surfaceRenderer", renderer);

            GameObject chessRoot = Create("authored-chess-root");
            FreeformSceneChessDefinition chess = chessRoot.AddComponent<FreeformSceneChessDefinition>();
            SetField(chess, "_movableRoot", chessRoot.transform);
            SetField(chess, "_board", chessRoot.transform);
            GameObject backendRoot = Create("backend");
            FreeformSceneLocalBackend backend = backendRoot.AddComponent<FreeformSceneLocalBackend>();
            SetField(backend, "_whiteboard", whiteboard);
            SetField(backend, "_chess", chess);

            Invoke(backend, "EnsureOwnedAttachments");
            SpatialBlendObject support = backend.WhiteboardSupport;
            Assert.That(support, Is.Not.Null);
            Assert.That(support.transform.parent, Is.SameAs(renderer.transform));
            Assert.That(support.transform.IsChildOf(whiteboardRoot.transform), Is.True);
            Assert.That(support.GetComponent<Collider>(), Is.Null,
                "Native support must not repurpose authored input/physics colliders.");
            GameObject supportRoot = support.gameObject;
            // Isolated EditMode objects do not reliably dispatch destruction
            // callbacks, so invoke the production cleanup callback directly.
            Invoke(backend, "OnDestroy");
            Assert.That(supportRoot == null, Is.True);
            Object.DestroyImmediate(backendRoot);
            _created.Remove(backendRoot);
            Assert.That(renderer, Is.Not.Null,
                "Backend cleanup must retain authored rendering rather than removing it.");
        }

        private GameObject Create(string name)
        {
            var value = new GameObject(name);
            _created.Add(value);
            return value;
        }

        private static void AssertVector(Vector2 actual, Vector2 expected) =>
            Assert.That(Vector2.Distance(actual, expected), Is.LessThan(1e-4f));

        private static void AssertVector(Vector3 actual, Vector3 expected) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-4f));

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);

        private static void Invoke(object target, string name) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
    }
}
#endif
