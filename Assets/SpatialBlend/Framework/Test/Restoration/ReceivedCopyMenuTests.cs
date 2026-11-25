// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples;
using SpatialBlend.Samples.Scanning;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A copy received from a peer has an object menu, so the receiver can hide an unwanted copy and see whose it is. The menu of
    /// a received copy is its owner line, "Hide for me" (local, asks nothing, sends nothing) and Close. Delete stays with the owner: nothing
    /// in this menu removes the object from the room.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ReceivedCopyMenuTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly System.Collections.Generic.List<GameObject> _made = new System.Collections.Generic.List<GameObject>();
        private GameObject _popupObject, _contextObject;
        private ManipulationPopUp _popup;
        private ManipulationContext _context;
        private Toggle _delete, _affordance, _reconstruct, _confirm, _exit;
        private TextMeshProUGUI _title;
        private int _deleteSelected;

        [SetUp]
        public void SetUp()
        {
            _popupObject = new GameObject("popup", typeof(RectTransform));
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(_popupObject.transform, false);
            _title = titleGo.AddComponent<TextMeshProUGUI>();
            _title.text = "Box annotation";
            var row = new GameObject("row", typeof(RectTransform));
            row.transform.SetParent(_popupObject.transform, false);
            _affordance = MakeToggle(row, "Region", "Region");
            _reconstruct = MakeToggle(row, "Reconstruct", "Reconstruct 3D");
            _delete = MakeToggle(row, "DeleteButton", "Delete");
            _confirm = MakeToggle(row, "Confirm", "Confirm");
            _exit = MakeToggle(row, "Exit", "Cancel");
            var exitText = new GameObject("ExitLabel", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();   // the real menu's labels are TMP
            exitText.transform.SetParent(_exit.transform, false);
            exitText.text = "Cancel";
            _popup = _popupObject.AddComponent<ManipulationPopUp>();
            Set(_popup, "_affordanceVolumeToggle", _affordance);
            Set(_popup, "_reconstructToggle", _reconstruct);
            Set(_popup, "_deleteToggle", _delete);
            Set(_popup, "_confirmToggle", _confirm);
            Set(_popup, "_exitToggle", _exit);
            _contextObject = new GameObject("context");
            _context = _contextObject.AddComponent<ManipulationContext>();
            Set(_popup, "_context", _context);
            _deleteSelected = 0;
            _popup.OnDeleteSelected = new UnityEngine.Events.UnityEvent();   // EditMode builds none
            _popup.OnDeleteSelected.AddListener(() => _deleteSelected++);
            typeof(ManipulationPopUp).GetMethod("OnEnable", Any).Invoke(_popup, null);   // EditMode does not run it
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject g in _made) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            UnityEngine.Object.DestroyImmediate(_contextObject);
            UnityEngine.Object.DestroyImmediate(_popupObject);
        }

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Any).SetValue(target, value);

        private static Toggle MakeToggle(GameObject parent, string name, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var toggle = go.AddComponent<Toggle>();
            var icon = new GameObject("Icon");
            icon.transform.SetParent(go.transform, false);
            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            label.AddComponent<Text>().text = text;
            return toggle;
        }

        private static string LabelOf(Toggle toggle) => toggle.GetComponentInChildren<Text>(true).text;

        private GameObject Replica(bool attach = true, string owner = null)
        {
            var go = new GameObject("received");
            _made.Add(go);
            var entity = go.AddComponent<SbNativeEntity>();
            typeof(SbNativeEntity).GetField("_isRemoteReplica", Any).SetValue(entity, true);
            go.AddComponent<MeshRenderer>();
            go.AddComponent<BoxCollider>();
            var child = new GameObject("model");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<MeshRenderer>();
            if (attach) ReceivedCopy.Attach(go, owner);
            return go;
        }

        private void Target(GameObject go)
        {
            typeof(ManipulationContext).GetField("_targetObject", Any).SetValue(_context, go);
            _popup.RefreshMenu();
        }

        [Test]
        public void AReceivedCopyGetsTheOwnerLineHideForMeAndClose()
        {
            GameObject copy = Replica();
            Target(copy);

            Assert.That(_title.text, Is.EqualTo("Received object\n" + ReceivedCopy.UnknownOwnerLine));
            Assert.That(LabelOf(_delete), Is.EqualTo("Hide for me"));
            Assert.That(_popup.HideForMeToggle, Is.SameAs(_delete));
            Assert.That(_delete.gameObject.activeSelf, Is.True);
            Assert.That(_delete.transform.Find("Icon").gameObject.activeSelf, Is.False, "the bin means Delete");
            Assert.That(_affordance.gameObject.activeSelf, Is.False, "Region is for boxes");
            Assert.That(_reconstruct.gameObject.activeSelf, Is.False, "Reconstruct is for boxes");
            Assert.That(_confirm.gameObject.activeSelf, Is.False);
            Assert.That(_exit.GetComponentInChildren<TMP_Text>(true).text, Is.EqualTo(ManipulationPopUp.CloseLabel));
        }

        [Test]
        public void TheOwnerLineNamesTheOwnerOnceTheRoomKnowsWho()
        {
            GameObject copy = Replica(owner: "Jiwon");
            Target(copy);
            Assert.That(_title.text, Is.EqualTo("Received object\nOwner: Jiwon"));
            Assert.That(copy.GetComponent<ReceivedCopy>().OwnerLine, Is.EqualTo("Owner: Jiwon"));

            ReceivedCopy.Attach(copy, "  ");   // a blank name never erases a known one
            Assert.That(copy.GetComponent<ReceivedCopy>().OwnerName, Is.EqualTo("Jiwon"));
        }

        [Test]
        public void HideForMeHidesTheCopyLocallyAsksNothingAndNeverDeletes()
        {
            GameObject copy = Replica();
            Target(copy);
            Assert.That(copy.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(2));

            _delete.isOn = true;   // a press

            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true)) Assert.That(renderer.enabled, Is.False);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) Assert.That(collider.enabled, Is.False, "the ray no longer hits it");
            Assert.That(copy.GetComponent<ReceivedCopy>().HiddenForMe, Is.True);
            Assert.That(_popup.IsConfirmingDelete, Is.False, "hiding is local and asks nothing");
            Assert.That(_deleteSelected, Is.EqualTo(0), "Delete was not selected");
            Assert.That(copy != null && copy.activeSelf, Is.True, "the object is still in the room; the owner's copy is untouched");
            Assert.That(_delete.isOn, Is.False, "the button is a button");
        }

        [Test]
        public void ACopyHiddenForMeStaysHiddenWhenTheOwnersContentLoadsAgain()
        {
            GameObject copy = Replica();
            copy.GetComponent<ReceivedCopy>().HideForMe();
            var late = new GameObject("late-model");
            late.transform.SetParent(copy.transform, false);
            var renderer = late.AddComponent<MeshRenderer>();
            Assert.That(renderer.enabled, Is.True);

            copy.GetComponent<SbNativeEntity>().SetContentData(new byte[] { 1, 2, 3 });   // raises ContentReady

            Assert.That(renderer.enabled, Is.False);
        }

        [Test]
        public void TheMenuOfAnotherTargetIsNotLeftWithHideForMe()
        {
            Target(Replica());
            Assert.That(LabelOf(_delete), Is.EqualTo("Hide for me"));

            var box = new GameObject("box");
            _made.Add(box);
            Target(box);

            Assert.That(LabelOf(_delete), Is.EqualTo("Delete"));
            Assert.That(_popup.HideForMeToggle, Is.Null);
            Assert.That(_delete.transform.Find("Icon").gameObject.activeSelf, Is.True);
            Assert.That(_affordance.gameObject.activeSelf, Is.True);
            Assert.That(_confirm.gameObject.activeSelf, Is.True);
            Assert.That(_title.text, Is.EqualTo("Box annotation"));
        }

        [Test]
        public void AnotherObjectsDeleteStillAsksFirst()
        {
            var box = new GameObject("box");
            _made.Add(box);
            Target(box);

            _delete.isOn = true;

            Assert.That(_popup.IsConfirmingDelete, Is.True);
            Assert.That(_deleteSelected, Is.EqualTo(0));
        }

        [Test]
        public void AttachGivesTheCopyTheAButtonPanelOnceAndOnlyOnce()
        {
            GameObject copy = Replica(attach: false);
            Assert.That(ReceivedCopy.Is(copy), Is.False);
            Assert.That(copy.GetComponent<ManipulationPanelMotif>(), Is.Null);

            ReceivedCopy first = ReceivedCopy.Attach(copy);
            ReceivedCopy second = ReceivedCopy.Attach(copy);

            Assert.That(second, Is.SameAs(first));
            Assert.That(copy.GetComponents<ReceivedCopy>().Length, Is.EqualTo(1));
            Assert.That(copy.GetComponents<ManipulationPanelMotif>().Length, Is.EqualTo(1));
            Assert.That(copy.GetComponent<UnplacedSharedScan>(), Is.Not.Null, "a received scan waits on the floor until it is placed");
            Assert.That(copy.GetComponent<UnplacedSharedScan>().PositionSpecified, Is.False);
            Assert.That(copy.GetComponent<PlaceCalloutView>(), Is.Not.Null, "the arrival card invites a hover until the cursor is close");
            Assert.That(ReceivedCopy.Is(copy), Is.True);
            Assert.That(ReceivedCopy.Attach(null), Is.Null);
        }

        [Test]
        public void ACopyWhoseModelLoadsAfterTheMenuWasAddedFindsItsInteractableWhenTheContentIsReady()
        {
            GameObject copy = Replica();
            // The copy was made before its model: no interactable yet when the panel motif looked for one.
            var motif = copy.GetComponent<ManipulationPanelMotif>();
            var field = typeof(ManipulationPanelMotif).GetField("_rayInteractable", Any);
            Assert.That(field.GetValue(motif), Is.Null);

            var model = new GameObject("model");
            model.transform.SetParent(copy.transform, false);
            var box = model.AddComponent<BoxCollider>();
            var surface = model.AddComponent<Oculus.Interaction.Surfaces.ColliderSurface>();
            surface.InjectCollider(box);
            var interactable = model.AddComponent<Oculus.Interaction.RayInteractable>();
            interactable.InjectSurface(surface);

            copy.GetComponent<SbNativeEntity>().SetContentData(new byte[] { 1, 2, 3 });   // raises ContentReady

            Assert.That(field.GetValue(motif), Is.SameAs(interactable));
        }

        [Test]
        public void TheAppGivesEveryReceivedScanTheMenuAndNothingElse()
        {
            var appObject = new GameObject("freeform-app");
            _made.Add(appObject);
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            GameObject received = Replica(attach: false);

            var own = new GameObject("own-scan");
            _made.Add(own);
            own.AddComponent<SbNativeEntity>();   // not a replica: made here, not by a peer
            var plain = new GameObject("remote-object");
            _made.Add(plain);

            app.OnObjectAdded(received, SbApp.OnObjectAddedType.Added);
            app.OnObjectAdded(own, SbApp.OnObjectAddedType.Added);
            app.OnObjectAdded(plain, SbApp.OnObjectAddedType.Added);

            Assert.That(ReceivedCopy.Is(received), Is.True);
            Assert.That(ReceivedCopy.Is(own), Is.False, "the user's own scan has its own menu");
            Assert.That(ReceivedCopy.Is(plain), Is.False);
        }
    }
}
#endif
