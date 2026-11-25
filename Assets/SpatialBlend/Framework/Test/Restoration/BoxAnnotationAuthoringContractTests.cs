// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Objects;
using SpatialBlend.Samples.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T5.2: an annotated box becomes an SbSpatialAuthoring declaration, never a volume component.
    /// The box declares itself while it exists, a deleted box declares nothing, the scan result binds the
    /// loaded object to the box, and the declaration travels with the shared content so the receiver writes
    /// the same authoring. EditMode: no scene, no native library, no Photon.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class BoxAnnotationAuthoringContractTests
    {
        private const string Root = "Assets/SpatialBlend/";

        private static readonly string[] AnnotationPath =
        {
            Root + "Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs",
            Root + "Interaction/Scripts/Placement/BoxAnnotationPlacementMotif.cs",
            Root + "Framework/Scripts/BoxAnnotationAuthoring.cs",
            Root + "Freeform/Scanning/ScanBoxRegion.cs",
            Root + "Samples/Scanning/Scripts/GltfScanResultSink.cs",
        };

        private GameObject _spawned;

        [TearDown]
        public void Destroy()
        {
            if (_spawned != null) UnityEngine.Object.DestroyImmediate(_spawned);
            _spawned = null;
        }

        private GameObject Make(string name)
        {
            if (_spawned != null) UnityEngine.Object.DestroyImmediate(_spawned);
            _spawned = new GameObject(name);
            return _spawned;
        }

        private static BoxCollider AnnotationCollider(GameObject box, Vector3 size)
        {
            // BoxAnnotationInstance.SetData: the pose is at the BOTTOM of the box, so the collider centre is half
            // the height up.
            var collider = box.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, size.y * 0.5f, 0f);
            collider.size = size;
            return collider;
        }

        private static SpatialAuthoringPlan Compile(GameObject target)
        {
            SbSpatialAuthoring authoring = target.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring, Is.Not.Null, "The box has no spatial authoring.");
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            return plan;
        }

        private static void AssertBounds(Vector2[] polygon, float minX, float maxX, float minZ, float maxZ)
        {
            Assert.That(polygon.Min(p => p.x), Is.EqualTo(minX).Within(1e-4f));
            Assert.That(polygon.Max(p => p.x), Is.EqualTo(maxX).Within(1e-4f));
            Assert.That(polygon.Min(p => p.y), Is.EqualTo(minZ).Within(1e-4f));
            Assert.That(polygon.Max(p => p.y), Is.EqualTo(maxZ).Within(1e-4f));
        }

        // box -> authoring declaration

        [Test]
        public void ABoxBecomesARegionAFootprintAndATaggedTopInOneDeclaration()
        {
            GameObject box = Make("box-annotation");
            BoxCollider collider = AnnotationCollider(box, new Vector3(1f, 0.6f, 0.4f));

            SbSpatialAuthoring authoring = BoxAnnotationAuthoring.Declare(box, collider);

            Assert.That(authoring, Is.Not.Null);
            Assert.That(BoxAnnotationAuthoring.HasDeclaration(box), Is.True);
            SpatialAuthoringPlan plan = Compile(box);
            Assert.That(plan.ExtentIsDeclared, Is.True, "The region is declared, not a margin around the footprint.");
            Assert.That(plan.AppliedMarginMeters, Is.EqualTo(0f));
            Assert.That(plan.Mappings, Has.Length.EqualTo(3));

            SpatialElementMapping region = plan.Mappings[0];
            SpatialElementMapping footprint = plan.Mappings[1];
            SpatialElementMapping top = plan.Mappings[2];
            Assert.That(region.kind, Is.EqualTo(SpatialHierarchyNodeKind.Region));
            Assert.That(region.role, Is.EqualTo(SpatialElementRole.Containment));
            AssertBounds(region.polygon, -0.5f, 0.5f, -0.2f, 0.2f);
            Assert.That(footprint.kind, Is.EqualTo(SpatialHierarchyNodeKind.Object));
            Assert.That(footprint.role, Is.EqualTo(SpatialElementRole.ColliderEnvelope));
            AssertBounds(footprint.polygon, -0.5f, 0.5f, -0.2f, 0.2f);
            Assert.That(region.polygon, Is.EqualTo(footprint.polygon),
                "The region is the footprint extent. It adds no margin of its own.");

            Assert.That(top.kind, Is.EqualTo(SpatialHierarchyNodeKind.Affordance));
            Assert.That(top.tag, Is.EqualTo(ScanBoxRegion.TopSurfaceTag));
            Assert.That(top.surface.All(v => Mathf.Abs(v.y - 0.6f) < 1e-4f), Is.True, "The top is at the box height.");
            Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(top.surface).y, Is.GreaterThan(0.99f));
        }

        [Test]
        public void AFlatBoxKeepsItsRegionAndDeclaresNoTop()
        {
            GameObject box = Make("flat-box");
            BoxCollider collider = AnnotationCollider(box, new Vector3(1f, 0.005f, 1f));

            BoxAnnotationAuthoring.Declare(box, collider);

            SpatialAuthoringPlan plan = Compile(box);
            Assert.That(plan.Mappings, Has.Length.EqualTo(2));
            Assert.That(plan.Mappings.Any(m => m.kind == SpatialHierarchyNodeKind.Affordance), Is.False);
        }

        [Test]
        public void ResizingTheBoxReplacesTheDeclarationInPlace()
        {
            GameObject box = Make("resized-box");
            BoxCollider collider = AnnotationCollider(box, new Vector3(1f, 0.6f, 0.4f));
            SbSpatialAuthoring first = BoxAnnotationAuthoring.Declare(box, collider);

            collider.center = new Vector3(0f, 0.5f, 0f);
            collider.size = new Vector3(2f, 1f, 1f);
            SbSpatialAuthoring second = BoxAnnotationAuthoring.Declare(box, collider);

            Assert.That(second, Is.SameAs(first), "One component, rewritten.");
            Assert.That(box.GetComponents<SbSpatialAuthoring>(), Has.Length.EqualTo(1));
            AssertBounds(Compile(box).Mappings[0].polygon, -1f, 1f, -0.5f, 0.5f);
        }

        // delete removes it

        [Test]
        public void DeletingTheBoxRemovesItsDeclaration()
        {
            GameObject box = Make("deleted-box");
            BoxAnnotationAuthoring.Declare(box, AnnotationCollider(box, Vector3.one));
            Assert.That(BoxAnnotationAuthoring.HasDeclaration(box), Is.True);

            Assert.That(BoxAnnotationAuthoring.Withdraw(box), Is.True);

            Assert.That(box.GetComponent<SbSpatialAuthoring>(), Is.Null);
            Assert.That(BoxAnnotationAuthoring.HasDeclaration(box), Is.False);
            Assert.That(BoxAnnotationAuthoring.Withdraw(box), Is.False, "Nothing left to withdraw.");
        }

        [Test]
        public void ADegenerateBoxWithdrawsWhateverItDeclaredBefore()
        {
            GameObject box = Make("collapsed-box");
            BoxCollider collider = AnnotationCollider(box, Vector3.one);
            BoxAnnotationAuthoring.Declare(box, collider);

            collider.size = new Vector3(0f, 1f, 1f);

            Assert.That(BoxAnnotationAuthoring.Declare(box, collider), Is.Null);
            Assert.That(box.GetComponent<SbSpatialAuthoring>(), Is.Null);
        }

        [Test]
        public void TheAnnotationInstanceDeclaresWhenSizedAndWithdrawsWhenDestroyedAndDeleteDestroysTheBox()
        {
            string instance = Source(Root + "Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs");
            int setData = instance.IndexOf("public void SetData", StringComparison.Ordinal);
            int declare = instance.IndexOf("BoxAnnotationAuthoring.Declare(gameObject, _collider)", StringComparison.Ordinal);
            int onDestroy = instance.IndexOf("private void OnDestroy()", StringComparison.Ordinal);
            int withdraw = instance.IndexOf("BoxAnnotationAuthoring.Withdraw(gameObject)", StringComparison.Ordinal);
            Assert.That(setData, Is.GreaterThanOrEqualTo(0));
            Assert.That(declare, Is.GreaterThan(setData), "SetData sizes the collider and then declares it.");
            Assert.That(instance.IndexOf("_collider.size = size;", StringComparison.Ordinal), Is.LessThan(declare));
            Assert.That(withdraw, Is.GreaterThan(onDestroy), "OnDestroy withdraws the declaration.");

            string context = Source(Root + "Interaction/Scripts/Manipulation/ManipulationContext.cs");
            Assert.That(context, Does.Contain("case 3: // Delete"));
            // Delete unregisters (the entity detaches, a box withdraws its
            // declaration) before it destroys the object, instead of relying on the deferred OnDestroy.
            Match delete = Regex.Match(context, @"private void ExecuteDeleteMode\(\)\s*\{(?<body>[^}]*(\{[^}]*\}[^}]*)*)\}");
            Assert.That(delete.Success, Is.True);
            string body = delete.Groups["body"].Value;
            int detach = body.IndexOf("_targetSbEntity.Detach();", StringComparison.Ordinal);
            int withdrawBox = body.IndexOf("BoxAnnotationAuthoring.Withdraw(_targetObject);", StringComparison.Ordinal);
            int destroy = body.IndexOf("Destroy(_targetObject);", StringComparison.Ordinal);
            Assert.That(detach, Is.GreaterThanOrEqualTo(0), "the entity detaches");
            Assert.That(withdrawBox, Is.GreaterThanOrEqualTo(0), "the box declaration is withdrawn");
            Assert.That(destroy, Is.GreaterThan(Math.Max(detach, withdrawBox)),
                "The box delete button destroys the box after withdrawing it, and its declaration goes with it.");
        }

        // the capture card on the selected box (scan UI Concept A; the floating panel is out of this flow)

        [Test]
        public void TheReconstructButtonOpensTheCaptureCardOnTheSelectedBox()
        {
            string context = Source(Root + "Interaction/Scripts/Manipulation/ManipulationContext.cs");
            Assert.That(context, Does.Contain("ScanCard.Open(_targetBoxAnnotation, ResolveCenterEyeAnchor());"));
            Assert.That(context, Does.Not.Contain("InteractionModeManager.DefaultPassthroughPanelPrefab"), "the floating panel is out of the menu flow");
            Assert.That(context, Does.Not.Contain("manager.OpenPanel();"));

            string manager = Source(Root + "Interaction/Scripts/Core/InteractionModeManager.cs");
            Assert.That(manager, Does.Contain("DefaultPassthroughPanelPrefab"));

            string scene = Source(Root + "Samples/Scenes/FreeformCollaboration.unity");
            string panelGuid = Regex.Match(Source(Root + "Samples/Scanning/Prefabs/PassthroughPanel.prefab.meta"),
                @"guid: ([0-9a-f]{32})").Groups[1].Value;
            Assert.That(panelGuid, Has.Length.EqualTo(32));
            Assert.That(scene, Does.Contain("_defaultPassthroughPanelPrefab: {fileID: 270712986787701689, guid: " + panelGuid));

            string instance = Source(Root + "Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs");
            Assert.That(instance, Does.Contain("AddComponent<BoxAnnotationPlacementMotif>()"));
            Assert.That(Source(Root + "Interaction/Scripts/Placement/BoxAnnotationPlacementMotif.cs"),
                Does.Contain("class BoxAnnotationPlacementMotif : PlacementMotif"));
        }

        // scan result binds to the box

        [Test]
        public void TheScanResultBindsTheLoadedObjectToItsBox()
        {
            // The loaded object sits at (2, 0, 3) turned a quarter. The box was drawn around it: 0.4 m along
            // world X, 1.0 m along world Z, 0.6 m tall, standing on the floor.
            GameObject loaded = Make("scan-object");
            loaded.transform.SetPositionAndRotation(new Vector3(2f, 0f, 3f), Quaternion.Euler(0f, 90f, 0f));
            string requestedPath = null;
            string requestedName = null;
            bool configuredBeforeReturn = false;
            var sink = new GltfScanResultSink((path, name, configure) =>
            {
                requestedPath = path;
                requestedName = name;
                configure(loaded);
                configuredBeforeReturn = loaded.GetComponent<SbSpatialAuthoring>() != null;
            });
            ScanReconstructionResult result = Result("scan-object", new Vector3(2f, 0.3f, 3f), new Vector3(0.4f, 0.6f, 1f));

            sink.OnScanReconstructed(result);

            Assert.That(requestedPath, Is.EqualTo(result.ModelPath));
            Assert.That(requestedName, Is.EqualTo("scan-object"));
            Assert.That(configuredBeforeReturn, Is.True,
                "The declaration is written when the object is loaded, before it is registered for sharing.");
            SpatialAuthoringPlan plan = Compile(loaded);
            Assert.That(plan.ExtentIsDeclared, Is.True);
            // A quarter turn: world X is local Z and world Z is local X.
            AssertBounds(plan.Mappings[0].polygon, -0.5f, 0.5f, -0.2f, 0.2f);
            AssertBounds(plan.Mappings[1].polygon, -0.5f, 0.5f, -0.2f, 0.2f);
            Assert.That(plan.Mappings[2].tag, Is.EqualTo(ScanBoxRegion.TopSurfaceTag));
            Assert.That(plan.Mappings[2].surface.All(v => Mathf.Abs(v.y - 0.6f) < 1e-4f), Is.True);
            // The sink marks it as the user's own scan, with its object menu (there is no Save/Keep).
            Assert.That(loaded.GetComponent<ScannedObjectOrigin>(), Is.Not.Null);
            Assert.That(loaded.GetComponent<SpatialBlend.Interactions.ManipulationPanelMotif>(), Is.Not.Null);
        }

        // Save is removed: the old library folder is deleted once at startup, best effort, never throwing.
        [Test]
        public void TheRemovedSaveFeaturesLibraryIsDeletedOnce_AndNothingThrows()
        {
            string root = Path.Combine(Path.GetTempPath(), "legacy-saved-" + Guid.NewGuid().ToString("N"));
            string library = Path.Combine(root, LegacySavedObjects.FolderName, "room-1", "obj-1");
            Directory.CreateDirectory(library);
            File.WriteAllBytes(Path.Combine(library, "model.glb"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(root, "keep-me.json"), "{}");
            try
            {
                UnityEngine.TestTools.LogAssert.Expect(LogType.Log, new Regex("Removed the saved-object library"));
                Assert.That(LegacySavedObjects.DeleteIfPresent(root), Is.True);
                Assert.That(Directory.Exists(Path.Combine(root, LegacySavedObjects.FolderName)), Is.False);
                Assert.That(File.Exists(Path.Combine(root, "keep-me.json")), Is.True, "only the library goes");
                Assert.That(LegacySavedObjects.DeleteIfPresent(root), Is.False, "once: nothing to do the next time");
                Assert.That(LegacySavedObjects.DeleteIfPresent(null), Is.False);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void TheScanBoxReplacesWhatTheContentPrefabDeclaredForItsDefaultCollider()
        {
            GameObject loaded = Make("scan-prefab");
            var preset = loaded.AddComponent<SbSpatialAuthoring>();
            preset.footprintPolygon = new[]
            {
                new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f),
            };
            preset.fallbackMarginMeters = 0.15f;
            preset.affordances = new[]
            {
                new AuthoredAffordanceSurface
                {
                    id = "Top", tag = "Placeable", supportId = "top",
                    surfacePolygon = new[]
                    {
                        new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f),
                        new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
                    },
                },
            };
            Assert.That(Compile(loaded).ExtentIsDeclared, Is.False, "The preset declares only a margin.");

            GltfScanResultSink.Bind(Result("scan-prefab", new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 1f)), loaded);

            SpatialAuthoringPlan plan = Compile(loaded);
            Assert.That(loaded.GetComponents<SbSpatialAuthoring>(), Has.Length.EqualTo(1));
            Assert.That(plan.ExtentIsDeclared, Is.True);
            AssertBounds(plan.Mappings[0].polygon, -1f, 1f, -0.5f, 0.5f);
            Assert.That(plan.Mappings.Count(m => m.kind == SpatialHierarchyNodeKind.Affordance), Is.EqualTo(1),
                "The box top is the one surface.");
            Assert.That(plan.Mappings[2].surface.All(v => Mathf.Abs(v.y - 2f) < 1e-4f), Is.True);
        }

        [Test]
        public void AScanWithoutABoxLeavesTheObjectsAuthoringAlone()
        {
            GameObject loaded = Make("scan-no-box");
            var preset = loaded.AddComponent<SbSpatialAuthoring>();
            preset.fallbackMarginMeters = 0.15f;
            var result = new ScanReconstructionResult
            {
                ObjectName = "scan-no-box", OutputPrefix = "scan/out", Kind = ScanKind.Reconstruct, HasRegionHint = false,
            };

            Assert.That(GltfScanResultSink.Bind(result, loaded), Is.Null);

            Assert.That(loaded.GetComponent<SbSpatialAuthoring>(), Is.SameAs(preset));
            Assert.That(preset.declaredExtent, Is.Null.Or.Empty);
            Assert.That(preset.fallbackMarginMeters, Is.EqualTo(0.15f));
        }

        [Test]
        public void ADegenerateScanBoxDeclaresNothing()
        {
            GameObject loaded = Make("scan-degenerate");
            Assert.That(GltfScanResultSink.Bind(
                Result("scan-degenerate", Vector3.zero, new Vector3(0f, 1f, 1f)), loaded), Is.Null);
            Assert.That(loaded.GetComponent<SbSpatialAuthoring>(), Is.Null);
        }

        // shared like other content

        [Test]
        public void TheBoxTravelsWithTheSharedContentSoTheReceiverWritesIdenticalAuthoring()
        {
            // Each space puts its copy of the object somewhere else. Only the object-local box crosses.
            GameObject sourceObject = Make("shared-scan");
            sourceObject.transform.SetPositionAndRotation(new Vector3(2f, 0f, 3f), Quaternion.Euler(0f, 90f, 0f));
            sourceObject.AddComponent<SbNativeEntity>();
            GltfScanResultSink.Bind(Result("shared-scan", new Vector3(2f, 0.3f, 3f), new Vector3(0.4f, 0.6f, 1f)),
                sourceObject);
            SbNativeEntity source = sourceObject.GetComponent<SbNativeEntity>();
            Assert.That(source.AuthoredBox, Is.Not.Null, "The source records the box next to its content.");

            var receiverObject = new GameObject("shared-scan");
            receiverObject.transform.SetPositionAndRotation(new Vector3(-7f, 0f, 1f), Quaternion.Euler(0f, 200f, 0f));
            SbNativeEntity receiver = receiverObject.AddComponent<SbNativeEntity>();
            var preset = receiverObject.AddComponent<SbSpatialAuthoring>();
            preset.fallbackMarginMeters = 0.15f;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                SerializationBlock sendBlock = ObjectSerializer.CreateBoxRegionBlock(source);
                SerializationBlock receiveBlock = ObjectSerializer.CreateBoxRegionBlock(receiver);
                ulong length = sendBlock.getLength();
                Assert.That(length, Is.EqualTo((ulong)(8 + source.AuthoredBox.Encode().Length)), "[id][size][box]");
                buffer = Marshal.AllocHGlobal((int)length);
                sendBlock.serialize(buffer);
                Assert.That(Marshal.ReadInt32(buffer, 0), Is.EqualTo(ObjectSerializer.HEADER_ID_BOX_REGION));

                receiveBlock.deserialize(buffer, length);

                Assert.That(receiver.AuthoredBox, Is.Not.Null);
                SpatialAuthoringPlan sent = Compile(sourceObject);
                SpatialAuthoringPlan received = Compile(receiverObject);
                Assert.That(received.ExtentIsDeclared, Is.True);
                Assert.That(received.LocalGeometrySignature, Is.EqualTo(sent.LocalGeometrySignature),
                    "Identical local geometry on both sides.");
                Assert.That(received.Mappings[0].polygon, Is.EqualTo(sent.Mappings[0].polygon));
                Assert.That(received.Mappings[2].tag, Is.EqualTo(sent.Mappings[2].tag));
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void ContentScannedWithoutABoxWritesAnEmptyBlockAndTheReceiverKeepsItsPrefab()
        {
            GameObject sourceObject = Make("plain-scan");
            SbNativeEntity source = sourceObject.AddComponent<SbNativeEntity>();
            Assert.That(ObjectSerializer.CreateBoxRegionBlock(source).getLength(), Is.EqualTo(0UL));

            var receiverObject = new GameObject("plain-scan");
            SbNativeEntity receiver = receiverObject.AddComponent<SbNativeEntity>();
            var preset = receiverObject.AddComponent<SbSpatialAuthoring>();
            IntPtr buffer = Marshal.AllocHGlobal(8);
            try
            {
                ObjectSerializer.CreateBoxRegionBlock(receiver).deserialize(buffer, 0UL);
                Assert.That(receiver.AuthoredBox, Is.Null);
                Assert.That(preset.declaredExtent, Is.Null.Or.Empty);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void AReceiverIgnoresABoxPayloadThatDoesNotDecode()
        {
            var receiverObject = new GameObject("bad-payload");
            SbNativeEntity receiver = receiverObject.AddComponent<SbNativeEntity>();
            var preset = receiverObject.AddComponent<SbSpatialAuthoring>();
            byte[] garbage = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            IntPtr buffer = Marshal.AllocHGlobal(8 + garbage.Length);
            try
            {
                Marshal.WriteInt32(buffer, 0, ObjectSerializer.HEADER_ID_BOX_REGION);
                Marshal.WriteInt32(buffer, 4, garbage.Length);
                Marshal.Copy(garbage, 0, IntPtr.Add(buffer, 8), garbage.Length);
                ObjectSerializer.CreateBoxRegionBlock(receiver).deserialize(buffer, (ulong)(8 + garbage.Length));

                Marshal.WriteInt32(buffer, 4, garbage.Length + 100);
                ObjectSerializer.CreateBoxRegionBlock(receiver).deserialize(buffer, (ulong)(8 + garbage.Length));

                Assert.That(receiver.AuthoredBox, Is.Null);
                Assert.That(preset.declaredExtent, Is.Null.Or.Empty);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void EveryNativeEntityWritesTheBoxBlockSecondSoBothPeersAgreeOnTheLayout()
        {
            string entity = Source(Root + "Framework/Scripts/SbNativeEntity.cs");
            int content = entity.IndexOf("AddSerializationBlock(ObjectSerializer.CreateContentBlock(this));", StringComparison.Ordinal);
            int box = entity.IndexOf("AddSerializationBlock(ObjectSerializer.CreateBoxRegionBlock(this));",
                StringComparison.Ordinal);
            Assert.That(content, Is.GreaterThanOrEqualTo(0));
            Assert.That(box, Is.GreaterThan(content));
        }

        // an annotation is never an AffordanceVolume

        [Test]
        public void AnAnnotationIsNeverAnAffordanceVolume()
        {
            GameObject box = Make("never-a-volume");
            BoxAnnotationAuthoring.Declare(box, AnnotationCollider(box, Vector3.one));
            GltfScanResultSink.Bind(Result("never-a-volume", new Vector3(0f, 0.5f, 0f), Vector3.one), box);

            string[] components = box.GetComponents<Component>().Select(c => c.GetType().Name).ToArray();
            Assert.That(components, Does.Not.Contain("AffordanceVolume"));
            Assert.That(components, Does.Contain(nameof(SbSpatialAuthoring)));

            foreach (string path in AnnotationPath)
            {
                Assert.That(CodeOnly(Source(path)), Does.Not.Contain("AffordanceVolume"), path);
            }
        }

        [Test]
        public void TheFloorPolygonModeNeverRunsOnABox()
        {
            string context = Source(Root + "Interaction/Scripts/Manipulation/ManipulationContext.cs");
            int method = context.IndexOf("private void ExecuteAffordanceVolumeMode()", StringComparison.Ordinal);
            int guard = context.IndexOf("_targetType == ManipulationTargetType.BoxAnnotationInstance", method,
                StringComparison.Ordinal);
            int motif = context.IndexOf("AddComponent<AffordanceAnnotationMotif>()", method, StringComparison.Ordinal);
            Assert.That(method, Is.GreaterThanOrEqualTo(0));
            Assert.That(guard, Is.GreaterThan(method));
            Assert.That(motif, Is.GreaterThan(guard), "A box returns before the floor motif can add a volume.");
        }

        private static ScanReconstructionResult Result(string name, Vector3 boxCenter, Vector3 boxSize) =>
            new ScanReconstructionResult
            {
                ObjectName = name,
                OutputPrefix = "scan/" + name,
                Kind = ScanKind.Reconstruct,
                HasAnchor = true,
                Anchor = new ScanAnchor(boxCenter, boxSize),
                HasRegionHint = true,
                RegionHint = new ScanRegionHint(boxCenter, boxSize),
            };

        private static string Source(string path) => File.ReadAllText(path);

        private static string CodeOnly(string text) =>
            string.Join("\n", text.Split('\n').Select(line =>
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line.Substring(0, comment);
            }));
    }
}
#endif
