// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// EVERY Delete asks "Delete this object?" before it deletes. On the real
    /// <c>ManipulationPopUpUI</c> prefab: a Delete press alone deletes nothing, Delete? confirms, Cancel, Close, closing the popup and a change of target all abort with no side effect.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ManipulationPopupDeleteConfirmTests
    {
        private const string PrefabPath = "Assets/SpatialBlend/Interaction/Prefabs/UI/ManipulationPopUpUI.prefab";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private GameObject _contextObject;
        private ManipulationContext _context;
        private GameObject _menu;
        private ManipulationPopUp _popup;
        private Toggle _delete;
        private Toggle _exit;
        private Toggle _confirm;
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly List<(int mode, bool success)> _executed = new List<(int, bool)>();

        [SetUp]
        public void SetUp()
        {
            _executed.Clear();
            _made.Clear();

            _contextObject = new GameObject("context");
            _context = _contextObject.AddComponent<ManipulationContext>();
            _context.OnModeExecuted += (c, mode, success) => _executed.Add((mode, success));

            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            _menu = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            _made.Add(_menu);
            _popup = _menu.GetComponentInChildren<ManipulationPopUp>(true);
            _delete = Field<Toggle>(_popup, "_deleteToggle");
            _exit = Field<Toggle>(_popup, "_exitToggle");
            _confirm = Field<Toggle>(_popup, "_confirmToggle");
            Field(_popup, "_context", _context);
            Invoke(_popup, "OnEnable"); // EditMode does not run it
            // The context wires the popup's events to its mode selection, as ShowPanel does.
            typeof(ManipulationContext).GetMethod("WireUpPanelEvents", Any).Invoke(_context, new object[] { _popup });
        }

        [TearDown]
        public void TearDown()
        {
            Invoke(_popup, "OnDisable");
            foreach (GameObject g in _made) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            UnityEngine.Object.DestroyImmediate(_contextObject);
        }

        // --- helpers ---

        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Any).GetValue(target);
        private static void Field(object target, string name, object value) => target.GetType().GetField(name, Any).SetValue(target, value);
        private static void Invoke(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, Any).Invoke(target, args.Length == 0 ? null : args);

        private GameObject OwnScan()
        {
            var go = new GameObject("scan");
            _made.Add(go);
            var entity = go.AddComponent<SbNativeEntity>();
            entity.SetContentData(new byte[] { 1, 2, 3 });
            ScannedObjectOrigin.Tag(go);
            return go;
        }

        private GameObject Box()
        {
            var go = new GameObject("box");
            _made.Add(go);
            go.AddComponent<BoxAnnotationInstance>();
            return go;
        }

        private void Target(GameObject go)
        {
            Field(_context, "_targetObject", go);
            _popup.RefreshMenu();
        }

        private static GameObject FindByName(GameObject root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t.gameObject;
            return null;
        }

        private static List<string> VisibleLabels(Toggle toggle)
        {
            var labels = new List<string>();
            foreach (var t in toggle.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (t.gameObject.activeInHierarchy) labels.Add(t.text);
            return labels;
        }

        private static void Press(Toggle toggle) => toggle.isOn = true;

        private static void ExpectDestroyRefusedInEditMode() =>
            LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));

        private void AssertNothingHappened(GameObject target)
        {
            Assert.That(_executed, Is.Empty, "no mode ran");
            Assert.That(_context.SelectedModeIndex, Is.EqualTo(-1), "Delete was not even selected");
            Assert.That(target != null, Is.True, "the object is still there");
        }

        // --- the cases ---

        [Test]
        public void DeleteWithoutAConfirmDeletesNothing()
        {
            GameObject scan = OwnScan();
            Target(scan);

            Press(_delete);

            Assert.That(_popup.IsConfirmingDelete, Is.True, "the press asks");
            AssertNothingHappened(scan);
            Assert.That(VisibleLabels(_delete), Is.All.EqualTo(ManipulationPopUp.ConfirmDeleteLabel), "the button turned into Delete?");
            Assert.That(_popup.CancelDeleteToggle.gameObject.activeInHierarchy, Is.True, "with a Cancel beside it");
            Assert.That(VisibleLabels(_popup.CancelDeleteToggle), Is.All.EqualTo(ManipulationPopUp.CancelDeleteLabel));
        }

        [Test]
        public void NoTimerAndNoAutoConfirmTheQuestionStaysOpenUntilAnswered()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            _popup.RefreshMenu(); // the menu relaid out, as it is whenever something changes
            Invoke(_popup, "OnEnable");

            Assert.That(_popup.IsConfirmingDelete, Is.True);
            AssertNothingHappened(scan);
        }

        [Test]
        public void ConfirmDeletesAScan()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            ExpectDestroyRefusedInEditMode(); // the deferred Destroy() is refused in edit mode
            Press(_delete); // Delete? is the answer yes

            Assert.That(_executed, Is.EqualTo(new List<(int, bool)> { (3, true) }), "the Delete mode ran, once");
            Assert.That(_popup.IsConfirmingDelete, Is.False);
        }

        [Test]
        public void CancelAbortsWithNoSideEffects()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            Press(_popup.CancelDeleteToggle);

            Assert.That(_popup.IsConfirmingDelete, Is.False);
            AssertNothingHappened(scan);
            Assert.That(VisibleLabels(_delete), Is.All.EqualTo(ManipulationPopUp.DeleteLabel), "Delete is Delete again");
            Assert.That(_popup.CancelDeleteToggle.gameObject.activeSelf, Is.False);
            Assert.That(_delete.gameObject.activeInHierarchy, Is.True, "the scan's menu is back");
            Assert.That(_exit.gameObject.activeInHierarchy, Is.True);

            // And Delete asks again: nothing was remembered as a yes.
            Press(_delete);
            Assert.That(_popup.IsConfirmingDelete, Is.True);
            AssertNothingHappened(scan);
        }

        [Test]
        public void CloseAbortsWithNoSideEffects()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            Press(_exit); // the menu's Close

            Assert.That(_popup.IsConfirmingDelete, Is.False);
            AssertNothingHappened(scan);
            Assert.That(VisibleLabels(_delete), Is.All.EqualTo(ManipulationPopUp.DeleteLabel));
        }

        [Test]
        public void ClosingThePopupAbortsAndItOpensAsAMenuAgain()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            Invoke(_popup, "OnDisable"); // the panel was hidden
            Invoke(_popup, "OnEnable");  // and shown again

            Assert.That(_popup.IsConfirmingDelete, Is.False);
            AssertNothingHappened(scan);
            Assert.That(VisibleLabels(_delete), Is.All.EqualTo(ManipulationPopUp.DeleteLabel));
            Assert.That(_popup.CancelDeleteToggle.gameObject.activeSelf, Is.False);
            Assert.That(_popup.DeletePromptText, Is.Null);
        }

        [Test]
        public void ResettingTheSelectionAsShowPanelDoesAbortsToo()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);

            _popup.ResetSelection();

            Assert.That(_popup.IsConfirmingDelete, Is.False);
            AssertNothingHappened(scan);
        }

        [Test]
        public void AChangeOfTargetAborts_AndTheOldYesCannotDeleteTheNewTarget()
        {
            GameObject first = OwnScan();
            Target(first);
            Press(_delete);

            var otherObject = new GameObject("other-context");
            _made.Add(otherObject);
            var other = otherObject.AddComponent<ManipulationContext>();
            GameObject second = OwnScan();
            Field(other, "_targetObject", second);
            _popup.SetContext(other);

            Assert.That(_popup.IsConfirmingDelete, Is.False, "the question was for the first object");
            Assert.That(first != null && second != null, Is.True);
            Assert.That(_executed, Is.Empty);
            Assert.That(VisibleLabels(_delete), Is.All.EqualTo(ManipulationPopUp.DeleteLabel));

            // The same, when the target changes under the same context and the answer arrives.
            _popup.SetContext(_context);
            Target(first);
            Press(_delete);
            Field(_context, "_targetObject", second);
            Press(_delete); // the yes of the first question
            Assert.That(_executed, Is.Empty, "a yes for one object does not delete another");
            Assert.That(second != null, Is.True);
        }

        [Test]
        public void TheQuestionIsDeleteThisObject_OnScreen()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);
            Assert.That(_popup.DeletePromptText, Is.EqualTo("Delete this object?"));
            Assert.That(_popup.DeletePromptText, Is.EqualTo(ManipulationPopUp.DeletePrompt));

            // The text is on screen, not just in a property.
            string shown = null;
            foreach (var t in _menu.GetComponentsInChildren<TMPro.TMP_Text>(false))
                if (t.gameObject.name == "DeletePrompt") shown = t.text;
            Assert.That(shown, Is.EqualTo(ManipulationPopUp.DeletePrompt));
        }

        [Test]
        public void WhileAskingOnlyTheQuestionItsAnswersAndCloseAreOffered()
        {
            GameObject box = Box();
            Target(box);
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False, "the box menu has no Confirm");
            Assert.That(Field<Toggle>(_popup, "_reconstructToggle").gameObject.activeInHierarchy, Is.True, "Capture stays");
            Assert.That(VisibleLabels(Field<Toggle>(_popup, "_reconstructToggle")), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CaptureLabel));
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.ExitLabel));

            Press(_delete);

            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False, "the box menu's Confirm is not the answer");
            Assert.That(Field<Toggle>(_popup, "_affordanceVolumeToggle").gameObject.activeInHierarchy, Is.False);
            Assert.That(Field<Toggle>(_popup, "_reconstructToggle").gameObject.activeInHierarchy, Is.False);
            Assert.That(_delete.gameObject.activeInHierarchy, Is.True);
            Assert.That(_popup.CancelDeleteToggle.gameObject.activeInHierarchy, Is.True);
            Assert.That(_exit.gameObject.activeInHierarchy, Is.True, "Close is always there");
            Assert.That(VisibleLabels(_exit), Is.All.EqualTo(ManipulationPopUp.CloseLabel), "so Cancel is not on screen twice");

            _popup.CancelDeleteConfirm();
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False, "the box menu is back as it was: still no Confirm");
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.ExitLabel), "and Exit is Exit again");
            Assert.That(Field<Toggle>(_popup, "_affordanceVolumeToggle").gameObject.activeInHierarchy, Is.False);
            Assert.That(VisibleLabels(Field<Toggle>(_popup, "_reconstructToggle")), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CaptureLabel));
        }

        [Test]
        public void TheAnswersAreAsTallAsDeleteSoTheHitTargetIsTheThemesRow()
        {
            GameObject scan = OwnScan();
            Target(scan);
            Press(_delete);
            foreach (var group in _menu.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
            Canvas.ForceUpdateCanvases();

            float delete = PreferredHeight(_delete);
            float cancel = PreferredHeight(_popup.CancelDeleteToggle);
            Assert.That(cancel, Is.EqualTo(delete).Within(0.01f), "Cancel is a copy of the Delete row");
            Assert.That(delete, Is.GreaterThanOrEqualTo(UiTheme.RowHeight - 0.01f), "at least the theme's row height");
        }

        private static float PreferredHeight(Toggle toggle)
        {
            var rect = (RectTransform)toggle.transform;
            float h = LayoutUtility.GetPreferredHeight(rect);
            return h > 0f ? h : rect.rect.height;
        }

        // --- menus per target ---

        [Test]
        public void ADrawnBoxOffersCaptureDeleteAndExit_OtherContextsKeepTheirMenus()
        {
            Toggle region = Field<Toggle>(_popup, "_affordanceVolumeToggle");
            Toggle capture = Field<Toggle>(_popup, "_reconstructToggle");
            Toggle gizmo = Field<Toggle>(_popup, "_gizmoToggle");

            GameObject plain = new GameObject("plain");
            _made.Add(plain);
            Target(plain);
            Assert.That(region.gameObject.activeInHierarchy, Is.True, "an ordinary object keeps Affordance volume");
            Assert.That(capture.gameObject.activeInHierarchy, Is.True, "and Reconstruct 3D");
            Assert.That(VisibleLabels(capture), Is.Not.Empty.And.All.EqualTo("Reconstruct 3D"));
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.True, "and Confirm");
            Assert.That(_delete.gameObject.activeInHierarchy, Is.True);
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo("Cancel"));
            Assert.That(gizmo.gameObject.activeInHierarchy, Is.False, "Gizmo stays unimplemented");

            GameObject box = Box();
            Target(box);
            Assert.That(region.gameObject.activeInHierarchy, Is.False, "a drawn box has no affordance volume");
            Assert.That(gizmo.gameObject.activeInHierarchy, Is.False);
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False, "and no Confirm");
            Assert.That(capture.gameObject.activeInHierarchy, Is.True);
            Assert.That(_delete.gameObject.activeInHierarchy, Is.True);
            Assert.That(_exit.gameObject.activeInHierarchy, Is.True);
            Assert.That(VisibleLabels(capture), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CaptureLabel));
            Assert.That(VisibleLabels(_delete), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.DeleteLabel));
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.ExitLabel));

            GameObject scan = OwnScan();
            Target(scan);
            Assert.That(region.gameObject.activeInHierarchy, Is.False);
            Assert.That(capture.gameObject.activeInHierarchy, Is.False, "a finished scan is not scanned again from this menu");
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False);
            Assert.That(_delete.gameObject.activeInHierarchy, Is.True);
            Assert.That(VisibleLabels(_delete), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.DeleteLabel), "a scan's menu is Delete and Close");
            Assert.That(FindByName(_menu, "KeepToggle"), Is.Null, "no Keep in room");
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CloseLabel));

            var received = new GameObject("received");
            _made.Add(received);
            received.AddComponent<ReceivedCopy>().SetOwner("Jiwon");
            Target(received);
            Assert.That(region.gameObject.activeInHierarchy, Is.False);
            Assert.That(capture.gameObject.activeInHierarchy, Is.False);
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False);
            Assert.That(VisibleLabels(_delete), Is.Not.Empty.And.All.EqualTo(ReceivedCopy.HideForMeLabel));
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CloseLabel));

            Target(plain);
            Assert.That(region.gameObject.activeInHierarchy, Is.True, "leaving the box restores the ordinary menu");
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.True);
            Assert.That(VisibleLabels(capture), Is.Not.Empty.And.All.EqualTo("Reconstruct 3D"));
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo("Cancel"));
            Assert.That(VisibleLabels(_delete), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.DeleteLabel));

            Target(box);
            Assert.That(region.gameObject.activeInHierarchy, Is.False);
            Assert.That(_confirm.gameObject.activeInHierarchy, Is.False);
            Assert.That(VisibleLabels(capture), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.CaptureLabel));
            Assert.That(VisibleLabels(_exit), Is.Not.Empty.And.All.EqualTo(ManipulationPopUp.ExitLabel));
        }

        [Test]
        public void CaptureOnADrawnBoxOpensTheScanCard_AnOrdinaryObjectStillWaitsForConfirm()
        {
            Toggle capture = Field<Toggle>(_popup, "_reconstructToggle");
            GameObject box = Box();
            box.AddComponent<BoxCollider>().size = new Vector3(0.4f, 0.5f, 0.4f);
            Invoke(_context, "Initialize", box);
            _popup.RefreshMenu();

            try
            {
                Press(capture);

                Assert.That(_executed, Is.EqualTo(new List<(int, bool)> { (1, true) }), "Capture runs Reconstruct without Confirm");
                Assert.That(_context.SelectedModeIndex, Is.EqualTo(1));
                Assert.That(ScanCard.Current, Is.Not.Null, "the scan card is open");
            }
            finally
            {
                if (ScanCard.Current != null) ScanCard.Current.Close();
            }

            _executed.Clear();
            _popup.ResetSelection();
            GameObject plain = new GameObject("plain");
            _made.Add(plain);
            Invoke(_context, "Initialize", plain);
            _popup.RefreshMenu();
            Press(capture);
            Assert.That(_executed, Is.Empty, "Reconstruct on an ordinary object only selects the mode");
            Assert.That(_context.SelectedModeIndex, Is.EqualTo(1));
            Assert.That(ScanCard.Current, Is.Null);

            LogAssert.Expect(LogType.Error, new Regex("Reconstruct 3D needs a drawn box"));
            Press(_confirm);
            Assert.That(_executed, Is.EqualTo(new List<(int, bool)> { (1, false) }), "Confirm is what runs it");
        }

        // --- the box annotation ---

        [Test]
        public void TheBoxAnnotationDeleteAsksToo_AndConfirmDeletesIt()
        {
            GameObject box = Box();
            Target(box);

            Press(_delete);
            Assert.That(_popup.IsConfirmingDelete, Is.True);
            Assert.That(_popup.DeletePromptText, Is.EqualTo(ManipulationPopUp.DeletePrompt));
            AssertNothingHappened(box);

            Press(_popup.CancelDeleteToggle);
            AssertNothingHappened(box);

            Press(_delete);
            ExpectDestroyRefusedInEditMode();
            Press(_delete);
            Assert.That(_executed, Is.EqualTo(new List<(int, bool)> { (3, true) }));
        }

        [Test]
        public void TheBoxsOwnDeleteButtonAndUndoOpenTheQuestionAndDeleteNothing()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            // The scene's mode manager is where the default object menu comes from; EditMode does not run its Awake.
            var modeHost = new GameObject("mode");
            _made.Add(modeHost);
            var mode = modeHost.AddComponent<InteractionModeManager>();
            typeof(InteractionModeManager).GetField("_defaultManipulationPanelPrefab", Any).SetValue(mode, prefab);
            typeof(InteractionModeManager).GetProperty("Instance").GetSetMethod(true).Invoke(null, new object[] { mode });
            try
            {
                GameObject box = Box();
                // A DeleteButton on the box itself, as the box prefab has, wired to OnDeleteValueChanged.
                var button = new GameObject("DeleteButton");
                button.transform.SetParent(box.transform, false);
                var buttonToggle = button.AddComponent<Toggle>();
                var instance = box.GetComponent<BoxAnnotationInstance>();

                buttonToggle.isOn = true;
                instance.OnDeleteValueChanged(true); // the prefab's persistent call

                ManipulationContext context = ManipulationContext.FindContextForTarget(box);
                Assert.That(context, Is.Not.Null, "the box's own Delete opened the object menu");
                _made.Add(context.gameObject);
                _made.Add(context.PanelInstance);
                context.OnModeExecuted += (c, m, success) => _executed.Add((m, success));
                var popup = context.PanelInstance.GetComponentInChildren<ManipulationPopUp>(true);
                Assert.That(popup.IsConfirmingDelete, Is.True, "on the question");
                Assert.That(box != null, Is.True, "nothing was deleted by asking");
                Assert.That(_executed, Is.Empty);
                Assert.That(context.SelectedModeIndex, Is.EqualTo(-1));
                Assert.That(buttonToggle.isOn, Is.False, "the box's button is a press, so it reads off and can be pressed again");

                popup.CancelDeleteConfirm();
                Assert.That(box != null, Is.True);

                // The undo gesture's path is the same call.
                instance.OnDeletePressed();
                Assert.That(popup.IsConfirmingDelete, Is.True);
                Assert.That(box != null, Is.True);
            }
            finally
            {
                typeof(InteractionModeManager).GetProperty("Instance").GetSetMethod(true).Invoke(null, new object[] { null });
            }
        }
    }
}
#endif
