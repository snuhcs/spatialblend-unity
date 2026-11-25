// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Objects;
using SpatialBlend.Samples.Objects;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The partner's scan that arrives on the other headset must be grabbable after it arrives, or it cannot be
    /// placed. A bare object made by the Framework loader (the model and an <see cref="SbNativeEntity"/>: no
    /// collider, no ray interactable, no placement motif) cannot be. The copy is made from the scan template (ObjectPreset, as v1), through the
    /// loader's template hook the app registers; the receiver grabs it and places it by the virtual rule (hologram, release). The user's
    /// own scan stays a fixed physical object that cannot be grabbed. The lobby room is <see cref="BoardRoom"/>.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ReceivedScanPlacementTests
    {
        private const string ScanTemplate = "Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab";

        private BoardRoom _room;
        private bool _ignoreFailing;
        private System.Func<string, GameObject> _factoryWas;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;   // no controller or camera rig in an EditMode run
            InteractionMotif.FocusOverride = () => true;
            _factoryWas = SpatialBlend.Objects.GltfNativeLoader.TemplateFactory;
            _room = BoardRoom.Create();
        }

        [TearDown]
        public void TearDown()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = _factoryWas;
            _room.Dispose();
            InteractionMotif.FocusOverride = null;
            LocomotionHold.ResetStatics();
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        // The template the app gives (the scan template; EditMode has no Addressables, so the prefab is loaded directly).
        private GameObject Template(string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScanTemplate);
            GameObject go = Object.Instantiate(prefab);
            go.name = name;
            _room.Objects.Add(go);
            return go;
        }

        // What the receiver does with a peer's scan: the loader's template, marked a replica, its model under "GLTF" fitted
        // (FitReplica, the source's scale), and the receiver's menu (ReceivedCopy).
        private GameObject ReceiveScan()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = Template;
            GameObject copy = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("partner-scan");
            _room.Objects.Add(copy);
            var entity = copy.GetComponent<SbNativeEntity>();
            typeof(SbNativeEntity).GetMethod("MarkRemoteReplica", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
            entity.SetSharedScale(Vector3.one * 0.6f);
            var gltf = new GameObject("GLTF");
            gltf.transform.SetParent(copy.transform, false);
            var geometry = new GameObject(ScanObjectFit.GeometryNodeName);
            geometry.transform.SetParent(gltf.transform, false);
            geometry.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            geometry.AddComponent<MeshRenderer>();
            ScanObjectFit.FitReplica(copy);
            ReceivedCopy.Attach(copy);
            copy.transform.position = new Vector3(-1.5f, 0.3f, -1.5f);   // where the share dropped it, on the open floor
            return copy;
        }

        private ObjectPlacementMotif Wire(GameObject root)
        {
            var motif = (ObjectPlacementMotif)_room.Wire(root);
            BoardRoom.SetField(typeof(ObjectPlacementMotif), motif, "_rayInteractable", root.GetComponentInChildren<RayInteractable>(true));
            return motif;
        }

        private static object Field(System.Type type, object target, string name) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        [Test]
        public void TheAppRegistersTheScanTemplateForReceivedCopies()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = null;
            SpatialBlend.Samples.Objects.GltfNativeLoader.RegisterReceivedCopyTemplate();
            Assert.That(SpatialBlend.Objects.GltfNativeLoader.TemplateFactory, Is.Not.Null);
            Assert.That(SpatialBlend.Objects.GltfNativeLoader.TemplateFactory.Method.Name,
                Is.EqualTo(nameof(SpatialBlend.Samples.Objects.GltfNativeLoader.ReceivedCopyTemplate)));
        }

        [Test]
        public void WithNoTemplateTheCopyIsBareAndATemplateWithoutAnEntityIsRefused()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = null;
            GameObject bare = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("bare");
            _room.Objects.Add(bare);
            Assert.That(bare.GetComponent<SbNativeEntity>(), Is.Not.Null);

            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = name => new GameObject(name);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("has no SbNativeEntity; a bare object is used"));
            GameObject fallback = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("no-entity");
            _room.Objects.Add(fallback);
            Assert.That(fallback.GetComponent<SbNativeEntity>(), Is.Not.Null, "a template with no entity falls back to the bare object");
        }

        [Test]
        public void TheReceivedScanHasACollidedRayInteractableAndAPlacementMotif()
        {
            GameObject copy = ReceiveScan();
            Assert.That(copy.GetComponent<SbNativeEntity>().IsRemoteReplica, Is.True);
            Assert.That(copy.GetComponent<PhysicalCounterpart>(), Is.Null, "a received copy is a virtual object");
            Assert.That(copy.GetComponent<UnplacedSharedScan>(), Is.Not.Null, "it waits to be placed");
            BoxCollider[] colliders = copy.GetComponentsInChildren<BoxCollider>(true);
            Assert.That(colliders.Length, Is.EqualTo(1));
            Assert.That(colliders[0].enabled && !colliders[0].isTrigger, Is.True);
            var interactable = copy.GetComponentInChildren<RayInteractable>(true);
            Assert.That(interactable, Is.Not.Null);
            var surface = (Component)Field(typeof(RayInteractable), interactable, "_surface");
            Assert.That(Field(surface.GetType(), surface, "_collider"), Is.SameAs(colliders[0]), "the ray hits the copy's collider");
            Assert.That(copy.GetComponentInChildren<ObjectPlacementMotif>(true), Is.Not.Null);

            // The collider is fitted to the model at the source's scale (FitReplica through the GLTF node).
            Assert.That(ScanObjectFit.TryGeometryWorldBounds(copy, out Bounds shown), Is.True);
            Assert.That(Vector3.Distance(copy.transform.TransformPoint(colliders[0].center), shown.center), Is.LessThan(1e-3f));
            Assert.That(colliders[0].size.y * copy.transform.lossyScale.y, Is.EqualTo(shown.size.y).Within(1e-3f));
        }

        [Test]
        public void TheReceiverGrabsTheScanSeesItsHologramAndPlacesIt()
        {
            GameObject copy = ReceiveScan();
            ObjectPlacementMotif motif = Wire(copy);
            var interactable = copy.GetComponentInChildren<RayInteractable>(true);
            Assert.That(motif.AcceptsSelection(true), Is.True);
            var pointer = new EditPointer(EditInputSource.Controller)
            {
                OriginSource = () => _room.Eye.transform.position,
                ForwardSource = () => (copy.transform.position - _room.Eye.transform.position).normalized,
            };
            motif.OnInteractorSelect(pointer, interactable);
            Assert.That(motif.IsHeld, Is.True, "the grab is taken");

            // Carried to the open floor: the normal virtual flow shows the landing.
            _room.Hold(motif, new Pose(new Vector3(-1.2f, 0.6f, -0.8f), Quaternion.identity));
            PlacementDrop? preview = motif.RefreshPreview();
            Assert.That(motif.PreviewInputs, Is.Not.Null, "judged by the room rule (a virtual object with a region)");
            Assert.That(preview.HasValue && preview.Value.Settles && preview.Value.Placed, Is.True);
            Assert.That(motif.Preview.Visible, Is.True, "the hologram is shown");

            PlacementDrop drop = _room.Release(motif);
            Assert.That(motif.IsHeld, Is.False);
            Assert.That(drop.Settles && drop.Placed && !drop.Reverted, Is.True, "placed");
            Assert.That(copy.GetComponent<UnplacedSharedScan>().PositionSpecified, Is.True, "its place is specified");
        }

        private static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        // The copy carries the template's GroundingShadowMotif, whose shadow lives at the scene root.
        // EditMode runs no lifecycle message, so Awake, OnDisable and OnDestroy are invoked as the engine would.
        [Test]
        public void TheGroundingShadowIsRemeasuredOnTheModelHiddenWithTheCopyAndDestroyedWithIt()
        {
            GameObject copy = ReceiveScanBeforeItsModel();
            var motif = copy.GetComponent<GroundingShadowMotif>();
            Assert.That(motif, Is.Not.Null, "the scan template has one");
            Invoke(motif, "Awake");
            Transform shadow = motif.Shadow;
            Assert.That(shadow, Is.Not.Null);
            var sprite = shadow.GetComponent<SpriteRenderer>();
            Assert.That(sprite.size.x, Is.EqualTo(0.6f).Within(1e-3f), "before the model: the template's 1 m collider at the told scale");

            // The model arrives and is fitted; the entity says so (ContentReady): the shadow takes the model's footprint.
            FitModel(copy);
            copy.GetComponent<SbNativeEntity>().SetContentData(new byte[] { 1 });
            var collider = copy.GetComponent<BoxCollider>();
            Assert.That(sprite.size.x, Is.EqualTo(collider.size.x * copy.transform.lossyScale.x).Within(1e-3f), "measured again");
            Assert.That(sprite.size.x, Is.LessThan(0.6f));

            // Hide for me hides the shadow too, and it stays hidden.
            ReceivedCopy.Attach(copy).HideForMe();
            Assert.That(shadow.gameObject.activeSelf, Is.False, "hidden with the copy");
            Invoke(motif, "OnEnable");
            Assert.That(shadow.gameObject.activeSelf, Is.False, "still hidden");

            // Destroyed with the copy: nothing is left at the scene root.
            Invoke(motif, "OnDestroy");
            Assert.That(shadow == null, Is.True, "the shadow is destroyed with its object");
        }

        [Test]
        public void TheShadowFollowsTheObjectBeingDisabledAndEnabled()
        {
            GameObject copy = ReceiveScanBeforeItsModel();
            var motif = copy.GetComponent<GroundingShadowMotif>();
            Invoke(motif, "Awake");
            Transform shadow = motif.Shadow;
            try
            {
                Invoke(motif, "OnDisable");
                Assert.That(shadow.gameObject.activeSelf, Is.False);
                Invoke(motif, "OnEnable");
                Assert.That(shadow.gameObject.activeSelf, Is.True);
            }
            finally { Invoke(motif, "OnDestroy"); }
        }

        // An unboxed received scan's declaration reads its collider, the template's 1 m default until the
        // model is fitted: its region waits for the model. A boxed one declares the box and needs no model.
        [Test]
        public void AnUnboxedReceivedScansRegionWaitsForItsModel()
        {
            GameObject copy = ReceiveScanBeforeItsModel();
            var entity = copy.GetComponent<SbNativeEntity>();
            var authoring = copy.GetComponent<SbSpatialAuthoring>();
            Assert.That(SpatialBlendManager.AwaitsReceivedModel(entity, authoring), Is.True, "no model yet: the collider is a default");

            FitModel(copy);
            typeof(SbNativeEntity).GetProperty("ModelLoaded").GetSetMethod(true).Invoke(entity, new object[] { true });
            Assert.That(SpatialBlendManager.AwaitsReceivedModel(entity, authoring), Is.False, "the model is fitted");

            GameObject boxed = ReceiveScanBeforeItsModel();
            Assert.That(ScanBoxRegion.TryFromLocalBox(Vector3.zero, new Vector3(0.4f, 0.6f, 0.4f), out ScanBoxRegion region), Is.True);
            boxed.GetComponent<SbNativeEntity>().SetAuthoredBox(region);
            Assert.That(SpatialBlendManager.AwaitsReceivedModel(boxed.GetComponent<SbNativeEntity>(),
                boxed.GetComponent<SbSpatialAuthoring>()), Is.False, "a box declares itself");
        }

        // The copy as SyncRemote makes it, before its model loads (the content has not arrived yet).
        private GameObject ReceiveScanBeforeItsModel()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = Template;
            GameObject copy = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("partner-scan");
            _room.Objects.Add(copy);
            var entity = copy.GetComponent<SbNativeEntity>();
            typeof(SbNativeEntity).GetMethod("MarkRemoteReplica", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
            entity.SetSharedScale(Vector3.one * 0.6f);
            copy.transform.localScale = Vector3.one * 0.6f;
            return copy;
        }

        private static void FitModel(GameObject copy)
        {
            var gltf = new GameObject("GLTF");
            gltf.transform.SetParent(copy.transform, false);
            var geometry = new GameObject(ScanObjectFit.GeometryNodeName);
            geometry.transform.SetParent(gltf.transform, false);
            geometry.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            geometry.AddComponent<MeshRenderer>();
            geometry.transform.localScale = new Vector3(0.5f, 1f, 0.5f);
            ScanObjectFit.FitReplica(copy);
        }

        [Test]
        public void TheUsersOwnScanStillCannotBeGrabbed()
        {
            GameObject own = Template("own-scan");
            own.AddComponent<PhysicalCounterpart>();
            ObjectPlacementMotif motif = Wire(own);
            Assert.That(motif.AcceptsSelection(true), Is.False, "a physical counterpart stays where the real object is");
            motif.OnInteractorSelect(new EditPointer(EditInputSource.Controller), own.GetComponentInChildren<RayInteractable>(true));
            Assert.That(motif.IsHeld, Is.False);
        }
    }
}
#endif
