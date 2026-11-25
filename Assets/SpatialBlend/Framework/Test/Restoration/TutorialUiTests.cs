// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.Scanning;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>The tutorial card, the controller over a real lobby panel, the practice props and the lobby wiring.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialUiTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private FakeKeyValueStore _store;

        [SetUp]
        public void SetUp()
        {
            _store = new FakeKeyValueStore();
            TutorialDoneMark.ReplaceShared(new TutorialDoneMark(_store));
            _incomingWait = TutorialScript.IncomingShareSeconds;
            TutorialScript.IncomingShareSeconds = 0;
        }

        private double _incomingWait;

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
            // SessionManager.EnsureInstance makes one when the scene has none; outside play mode its Awake never runs, so nothing else tracks it.
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SessionManager>(FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(manager.gameObject);
            TutorialScript.IncomingShareSeconds = _incomingWait;
            TutorialDoneMark.ReplaceShared(null);
            TutorialModeAssistBridge.Register(null);
            _rig = null;
            var suspenders = typeof(SpatialBlend.Interactions.ModeSwitchAssist).GetField("s_Suspenders", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            suspenders.GetType().GetMethod("Clear").Invoke(suspenders, null);
            if (_eyeCamera != null) { _eyeCamera.transform.SetPositionAndRotation(_eyePosition, _eyeRotation); _eyeCamera = null; }
        }

        private sealed class PanelHost
        {
            public GameObject Canvas, PanelObject;
            public ControlPanel Panel;
            public SpatialBlendSettings Settings;
        }

        private PanelHost NewPanel()
        {
            var host = new PanelHost
            {
                Canvas = new GameObject("panel-canvas", typeof(RectTransform), typeof(Canvas)),
                Settings = ScriptableObject.CreateInstance<SpatialBlendSettings>(),
            };
            ((RectTransform)host.Canvas.transform).sizeDelta = new Vector2(406f, 300f);
            var legacy = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            legacy.transform.SetParent(host.Canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(legacy.transform, false);
            host.PanelObject = new GameObject("control-panel");
            host.PanelObject.SetActive(false);
            host.Panel = host.PanelObject.AddComponent<ControlPanel>();
            Set(host.Panel, "_connect", legacy.GetComponent<Button>());
            Set(host.Panel, "_settings", host.Settings);
            typeof(ControlPanel).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host.Panel, null);
            Assert.That(host.Panel.BuildGeneratedLayout(), Is.True);
            _objects.Add(host.Canvas); _objects.Add(host.PanelObject); _objects.Add(host.Settings);
            return host;
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static object Get(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private TutorialController Begin(PanelHost host)
        {
            var controller = TutorialController.Begin(host.Panel);
            Assert.That(controller, Is.Not.Null);
            _objects.Add(controller.gameObject);
            return controller;
        }

        private static void Go(TutorialRunner runner, string id)
        {
            while (runner.Flow.Current.Id != id) Assert.That(runner.Skip(), Is.True);
        }

        /// <summary>The scan task needs the user in Editing (the tutorial does not switch there for them): put them in it, then go to the task.</summary>
        private void GoScan(TutorialRunner runner)
        {
            if (_rig == null) _rig = NewModeRig(SpatialBlend.Interactions.InteractionMode.SceneEdit);
            Go(runner, "3.1");
            _rig.Mode.SetMode(SpatialBlend.Interactions.InteractionMode.SceneEdit);   // passing the mode task left them in Interaction; they switch with the chip
        }

        // ---- the real mode manager and assist, for the tasks about modes

        private ModeRig _rig;

        private sealed class ModeRig
        {
            public GameObject Host;
            public SpatialBlend.Interactions.InteractionModeManager Mode;
            public SpatialBlend.Interactions.ModeSwitchAssist Assist;
            public float Now;
            public Oculus.Interaction.RayInteractable Hovered;

            public void Step(float now)
            {
                Now = now;
                Assist.Step(now);
            }
        }

        private ModeRig NewModeRig(SpatialBlend.Interactions.InteractionMode start)
        {
            // The assist's suspenders are static; start each test from "nobody suspends it".
            var owners = typeof(SpatialBlend.Interactions.ModeSwitchAssist).GetField("s_Suspenders", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            owners.GetType().GetMethod("Clear").Invoke(owners, null);
            var rig = new ModeRig { Host = new GameObject("mode-rig") };
            _objects.Add(rig.Host);
            rig.Mode = rig.Host.AddComponent<SpatialBlend.Interactions.InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            typeof(SpatialBlend.Interactions.InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig.Mode, null);
            rig.Mode.SetMode(start);
            rig.Assist = rig.Host.AddComponent<SpatialBlend.Interactions.ModeSwitchAssist>();
            typeof(SpatialBlend.Interactions.ModeSwitchAssist).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig.Assist, null);
            typeof(SpatialBlend.Interactions.ModeSwitchAssist).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig.Assist, null);
            rig.Assist.Clock = () => rig.Now;
            rig.Assist.HoveredSource = () => rig.Hovered;
            return rig;
        }

        private static void Tap(SpatialBlend.Interactions.UI.ModeSwitchClickTarget target)
        {
            var change = typeof(SpatialBlend.Interactions.UI.ModeSwitchClickTarget).GetMethod("OnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            change.Invoke(target, new object[] { new Oculus.Interaction.InteractableStateChangeArgs(Oculus.Interaction.InteractableState.Hover, Oculus.Interaction.InteractableState.Select) });
            change.Invoke(target, new object[] { new Oculus.Interaction.InteractableStateChangeArgs(Oculus.Interaction.InteractableState.Select, Oculus.Interaction.InteractableState.Hover) });
        }

        // ---- the lobby

        [Test]
        public void TutorialButton_StartsTheTutorialOverTheLobbyPanel()
        {
            var host = NewPanel();
            Assert.That(host.Panel.Tutorial, Is.Null);
            host.Panel.View.Tutorial.onClick.Invoke();
            var controller = host.Panel.Tutorial;
            Assert.That(controller, Is.Not.Null, "the stub is wired");
            _objects.Add(controller.gameObject);
            Assert.That(host.Panel.TutorialActive, Is.True);
            Assert.That(controller.Running, Is.True);
            host.Panel.View.Tutorial.onClick.Invoke();   // a second press does not start a second run
            Assert.That(host.Panel.Tutorial, Is.SameAs(controller));
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialController>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
        }

        [Test]
        public void WhileItRuns_TheLobbyPanelIsHiddenAndInert_AndConnectCreatesNothing()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var group = host.Panel.View.Root.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(0f));
            Assert.That(group.blocksRaycasts, Is.False);
            Assert.That(group.interactable, Is.False);
            Assert.That(controller.View.Root.parent, Is.SameAs(host.Panel.PanelCanvas), "the card is an overlay on the panel's canvas, not a second scene");
            Assert.That(controller.View.Root.GetComponent<LayoutElement>().ignoreLayout, Is.True);
            host.Panel.View.Connect.onClick.Invoke();
            Assert.That(Get(host.Panel, "_sessionListPanel"), Is.Null, "Find a room is inert under the tutorial");
            controller.Runner.Abort();
        }

        [Test]
        public void Exit_PutsTheLobbyBack_AndLeavesNoControllerNoCardNoPracticeObject()
        {
            var host = NewPanel();
            var controller = Begin(host);
            Go(controller.Runner, "2.2");
            Assert.That(controller.Props.Cube, Is.Not.Null, "the mode task's cube is in");
            Assert.That(controller.Props.TargetCount, Is.GreaterThan(0), "and its outline");
            controller.Runner.RequestExit();
            controller.Runner.ConfirmExit();
            var group = host.Panel.View.Root.GetComponent<CanvasGroup>();
            Assert.That(group.alpha, Is.EqualTo(1f));
            Assert.That(group.blocksRaycasts, Is.True);
            Assert.That(host.Panel.TutorialActive, Is.False);
            Assert.That(host.Panel.Tutorial, Is.Null);
            Assert.That(host.Panel.PanelCanvas.Find(TutorialView.RootName), Is.Null, "the card is gone");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialController>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(_store.Values.ContainsKey(TutorialDoneMark.Key), Is.False, "exiting is not completing");
        }

        [Test]
        public void Finishing_SetsTheDoneMark_AndTheHintDotHides()
        {
            var host = NewPanel();
            Assert.That(host.Panel.View.TutorialHint.gameObject.activeSelf, Is.True, "a dot until the first completion");
            var controller = Begin(host);
            var runner = controller.Runner;
            while (!runner.Flow.IsLast) runner.Skip();
            runner.Skip();
            Assert.That(runner.Flow.End, Is.EqualTo(TutorialEnd.Finished));
            Assert.That(TutorialDoneMark.Shared.IsDone, Is.True);
            Assert.That(host.Panel.View.TutorialHint.gameObject.activeSelf, Is.False);
            host.Panel.RefreshTutorialHint();
            Assert.That(host.Panel.View.TutorialHint.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TheHintDot_IsDrawnOnTheTutorialButtonAndTakesNoRays()
        {
            var host = NewPanel();
            var dot = host.Panel.View.TutorialHint;
            Assert.That(dot.transform.parent, Is.SameAs(host.Panel.View.Tutorial.transform));
            Assert.That(dot.raycastTarget, Is.False);
            Assert.That(dot.GetComponent<LayoutElement>().ignoreLayout, Is.True);
            Assert.That(dot.color, Is.EqualTo(UiTheme.Palette.Accent));
            host.Panel.View.SetTutorialHint(false);
            Assert.That(dot.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void ADoneMarkThatCannotBeRead_StillShowsTheTutorialAndTheDot()
        {
            var store = new FakeKeyValueStore { ThrowOnRead = true };
            TutorialDoneMark.ReplaceShared(new TutorialDoneMark(store));
            var host = NewPanel();   // building the panel reads the mark
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Could not read the done mark"));
            host.Panel.RefreshTutorialHint();
            Assert.That(host.Panel.View.TutorialHint.gameObject.activeSelf, Is.True);
            Assert.That(host.Panel.View.Tutorial.gameObject.activeSelf, Is.True);
        }

        // ---- the card

        private static string[] Shown(TutorialView view) => view.Root.GetComponentsInChildren<TMP_Text>(false).Select(t => t.text).ToArray();

        [Test]
        public void Card_ShowsEachTasksLineAndTheProductTitle_InTheGraphiteTheme()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            Assert.That(view.HeaderText, Does.StartWith(UiTheme.ProductTitle), "the single constant");
            foreach (var frame in TutorialScript.Frames)
            {
                Go(runner, frame.Id);
                view.Refresh();
                Assert.That(view.TitleText, Is.EqualTo(frame.TaskLine), frame.Id);
                Assert.That(view.Skip.gameObject.activeSelf, Is.True, frame.Id + ": Skip this step stays on every task");
                Assert.That(view.Next.interactable, Is.False, frame.Id + ": Next is off until the task is done");
                Assert.That(view.DotsOn, Is.EqualTo(frame.Chapter), frame.Id);
                Assert.That(view.SuccessLine, Is.Null, frame.Id + ": no feedback before the event");
                Assert.That(view.StepRows.Count, Is.EqualTo(frame.Steps.Length > 1 ? frame.Steps.Length : 0), frame.Id);
                if (frame.Steps.Length > 1) Assert.That(view.StepRows.Select(r => r.label), Is.EqualTo(frame.Steps.Select(s => s.Label)), frame.Id);
            }
            foreach (var text in view.Root.GetComponentsInChildren<TMP_Text>(true))
                Assert.That(text.color.a, Is.GreaterThan(0f));
            Assert.That(view.Root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == "Skip this step"), Is.True);
            Assert.That(view.Root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == TutorialScript.AttributionLine), Is.EqualTo(TutorialAssets.HasNarration),
                "the voice credit only when narration clips are bundled (the public repository ships none)");
            runner.Abort();
        }

        private static float LocalX(RectTransform space, RectTransform of, int corner)
        {
            var corners = new Vector3[4];
            of.GetWorldCorners(corners);
            return space.InverseTransformPoint(corners[corner]).x;
        }

        [Test]
        public void Card_BarIsOneRowHighAndSpansTheCard_OnEveryTask_AndXSitsInTheTopRightCorner()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            foreach (var frame in TutorialScript.Frames)
            {
                Go(runner, frame.Id);
                view.Refresh();
                var bar = view.BarRow;
                Assert.That(bar, Is.Not.Null, frame.Id);
                Assert.That(bar.rect.height, Is.EqualTo(UiTheme.RowHeight).Within(0.5f), frame.Id + ": the bar is exactly one row high");
                Assert.That(bar.rect.width, Is.EqualTo(view.Root.rect.width - 56f).Within(0.5f), frame.Id + ": the bar spans the card inside its padding");
                foreach (var button in new[] { view.Back, view.Skip, view.Next })
                {
                    if (!button.gameObject.activeSelf) continue;
                    var rect = (RectTransform)button.transform;
                    Assert.That(rect.rect.height, Is.EqualTo(UiTheme.RowHeight).Within(0.5f), frame.Id + " " + button.name);
                }
                Assert.That(LocalX(bar, (RectTransform)view.Back.transform, 0), Is.EqualTo(bar.rect.xMin).Within(0.5f), frame.Id + ": Back at the left edge");
                Assert.That(LocalX(bar, (RectTransform)view.Next.transform, 2), Is.EqualTo(bar.rect.xMax).Within(0.5f), frame.Id + ": the primary at the right edge");
                Assert.That(LocalX(bar, (RectTransform)view.Skip.transform, 2), Is.LessThan(LocalX(bar, (RectTransform)view.Next.transform, 0)), frame.Id + ": Skip left of the primary");
                // X is not in the bar: it is the card's top-right corner, one row square.
                Assert.That(view.Exit.transform.IsChildOf(bar), Is.False, frame.Id);
                var exit = (RectTransform)view.Exit.transform;
                Assert.That(exit.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
                Assert.That(exit.rect.width, Is.EqualTo(UiTheme.RowHeight).Within(0.5f));
                Assert.That(exit.rect.height, Is.EqualTo(UiTheme.RowHeight).Within(0.5f));
                Assert.That(LocalX(view.Root, exit, 2), Is.LessThanOrEqualTo(view.Root.rect.xMax + 0.5f), frame.Id + ": inside the card");
            }
            runner.Abort();
        }

        [Test]
        public void Card_NextLabelIsNextThenFinish_AndBackIsOffTheFirstTask()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            Assert.That(view.NextLabel, Is.EqualTo("Next"));
            Assert.That(view.Back.interactable, Is.False);
            runner.Skip();
            Assert.That(view.Back.interactable, Is.True);
            Go(runner, "4.2");
            Assert.That(view.NextLabel, Is.EqualTo("Finish"));
            runner.Abort();
        }

        [Test]
        public void Card_XAsksBeforeLeaving_AndBackSkipAndNextButtonsDriveTheRunner()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            view.Next.onClick.Invoke();
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("1.1"), "Next does nothing before the task is done");
            view.Skip.onClick.Invoke();
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("1.2"));
            view.Back.onClick.Invoke();
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("1.1"));
            runner.Report(TutorialSignal.ButtonPressed);
            view.Next.onClick.Invoke();
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("1.2"));

            view.Exit.onClick.Invoke();
            Assert.That(view.ConfirmVisible, Is.True);
            Assert.That(runner.Flow.Running, Is.True, "X alone does not leave");
            view.KeepGoing.onClick.Invoke();
            Assert.That(view.ConfirmVisible, Is.False);
            view.Exit.onClick.Invoke();
            view.LeaveTutorial.onClick.Invoke();
            Assert.That(runner.Flow.End, Is.EqualTo(TutorialEnd.Exited));
        }

        [Test]
        public void Task1_TheGlowingPracticeButton_IsPressedOnTheCard_AndTheCheckAppears()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            Assert.That(view.Action.gameObject.activeInHierarchy, Is.True);
            Assert.That(Shown(view), Does.Contain("Press me"));
            Assert.That(view.GlowVisible, Is.True, "highlighted in the accent");
            Assert.That(view.Next.interactable, Is.False);
            view.Action.onClick.Invoke();
            Assert.That(runner.Flow.ActionDone, Is.True);
            Assert.That(view.SuccessLine, Is.EqualTo("Button pressed"));
            Assert.That(view.GlowVisible, Is.False, "the highlight goes after success");
            Assert.That(view.Next.interactable, Is.True);
            runner.Abort();
        }

        [Test]
        public void Task2_2_AnchorsStartOff_TheTaskAdvancesOnlyWhenTheSettingChanges_AndTheUsersSettingComesBack()
        {
            var host = NewPanel();
            host.Panel.ScenePresenter.SetAnchorsVisible(true);       // the user had the anchors on
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            Assert.That(controller.AnchorsVisible, Is.True, "untouched by the first tasks");
            Go(runner, "2.1");
            view.Refresh();
            Assert.That(controller.AnchorsVisible, Is.False, "forced off when the task begins");
            Assert.That(host.Panel.ScenePresenter.AnchorsVisible, Is.False, "the real setting, not a stand-in");
            Assert.That(view.AnchorsRow.Toggle.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.AnchorsRow.Toggle.isOn, Is.False);
            Assert.That(Shown(view), Does.Contain("Settings"));
            Assert.That(view.TitleText, Is.EqualTo("Open Settings and turn on Show anchors"));
            Assert.That(runner.Flow.ActionDone, Is.False, "nothing advances until the toggle changes");
            view.AnchorsRow.Toggle.isOn = true;
            Assert.That(host.Panel.ScenePresenter.AnchorsVisible, Is.True);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "then the shared real-object box");
            controller.Runner.Narrator.Stop();
            runner.Report(TutorialSignal.SharedBoxPointed);
            Assert.That(runner.Flow.ActionDone, Is.True);
            Assert.That(view.SuccessLine, Is.EqualTo("Now you can see your real furniture, and a shared one"));
            runner.Abort();
            Assert.That(host.Panel.ScenePresenter.AnchorsVisible, Is.True, "restored to the user's own setting on exit");
        }

        // The anchors task shows the box of a shared real object and is done when the user points at it.
        private static void ToTheSharedBoxStep(TutorialController controller)
        {
            Go(controller.Runner, "2.1");
            controller.View.AnchorsRow.Toggle.isOn = true;
            controller.Runner.Narrator.Stop();
            controller.Runner.Tick();                        // the next step's line starts: the example box is shown
        }

        [Test]
        public void Task2_1_TheSharedBoxExample_IsTheSharedLook_AndPointingAtItCompletesTheTask()
        {
            var eyePos = new Vector3(0f, 1.6f, 0f);
            NewEye(eyePos, Quaternion.identity);
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            ToTheSharedBoxStep(controller);
            GameObject example = controller.SharedBoxExample;
            Assert.That(example, Is.Not.Null, "with no room setup, a demo box in front");
            Assert.That(example.name, Is.EqualTo(TutorialController.SharedBoxExampleName));
            Assert.That(example.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(9), "the shared box: edges and eight corner brackets");
            var violet = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.SharedReal;
            Color edge = example.transform.Find("Edges").GetComponent<LineRenderer>().startColor;
            Assert.That(new[] { edge.r, edge.g, edge.b }, Is.EqualTo(new[] { violet.r, violet.g, violet.b }).Within(1e-3f));
            Assert.That(runner.Hint, Does.Contain("shared with your partner's room"));

            Vector3 centre = example.transform.Find("Fill").GetComponent<Renderer>().bounds.center;
            controller.PointerRay = () => new Ray(eyePos, Vector3.left);          // pointing elsewhere
            Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.False, "not pointed at yet");
            controller.PointerRay = () => new Ray(eyePos, (centre - eyePos).normalized);
            Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.True, "pointed at it");
            Assert.That(example == null, Is.True, "the example goes with the step");
            runner.Abort();
        }

        [Test]
        public void Task2_1_TheSharedBoxExample_StandsOnTheUsersLargestFurniture_AndSkipRemovesIt()
        {
            var catalog = new ListSceneAnchorCatalog();
            var table = new GameObject("TABLE-t1");
            var chair = new GameObject("CHAIR-c1");
            _objects.Add(table); _objects.Add(chair);
            table.transform.SetPositionAndRotation(new Vector3(0f, 0.75f, 2f), Quaternion.Euler(-90f, 0f, 0f));
            chair.transform.SetPositionAndRotation(new Vector3(1.5f, 0.45f, 2f), Quaternion.Euler(-90f, 0f, 0f));
            catalog.Add(new SceneAnchorRecord { Label = "TABLE", Anchor = table, AnchorId = "t1", SourceKey = table.GetInstanceID(), Size = 1.2f * 0.8f * 0.75f,
                LocalCenter = new Vector3(0f, 0f, -0.375f), LocalSize = new Vector3(1.2f, 0.8f, 0.75f) });
            catalog.Add(new SceneAnchorRecord { Label = "CHAIR", Anchor = chair, AnchorId = "c1", SourceKey = chair.GetInstanceID(), Size = 0.5f * 0.5f * 0.45f,
                LocalCenter = new Vector3(0f, 0f, -0.225f), LocalSize = new Vector3(0.5f, 0.5f, 0.45f) });
            var appObject = new GameObject("app");
            _objects.Add(appObject);
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            var host = NewPanel();
            var controller = Begin(host);
            ToTheSharedBoxStep(controller);
            GameObject example = controller.SharedBoxExample;
            Assert.That(example, Is.Not.Null);
            Assert.That(example.transform.parent, Is.SameAs(table.transform), "on the largest piece of furniture in the room");
            Assert.That(table.GetComponent<SbEntity>(), Is.Null, "an example only: nothing is shared");
            controller.Runner.Skip();
            Assert.That(example == null, Is.True, "skipped: the example goes");
            controller.Runner.Abort();
        }

        [Test]
        public void Task2_2_TurningTheToggleOffAgainDoesNotCount_AndSkipRestoresAnOffSetting()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            Go(runner, "2.1");
            controller.View.AnchorsRow.Toggle.isOn = false;
            Assert.That(runner.Flow.ActionDone, Is.False);
            controller.View.AnchorsRow.Toggle.isOn = true;
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1));
            runner.Skip();                                  // on: they stay on because the user turned them on
            Assert.That(host.Panel.ScenePresenter.AnchorsVisible, Is.True);
            runner.Abort();
            Assert.That(host.Panel.ScenePresenter.AnchorsVisible, Is.False, "the user had them off");
        }

        [Test]
        public void Task3_TheChecklistTicksOffEachStep_AndOnlyTheNextOneCounts()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            GoScan(runner);
            view.Refresh();
            Assert.That(view.StepRows.Count, Is.EqualTo(8));
            Assert.That(view.StepRows.Count(r => r.done), Is.EqualTo(0));
            runner.Report(TutorialSignal.PhotoCaptured);                 // out of order: nothing
            Assert.That(view.StepRows.Count(r => r.done), Is.EqualTo(0));
            runner.Report(TutorialSignal.BoxCompleted);
            Assert.That(view.StepRows.Count(r => r.done), Is.EqualTo(1));
            runner.Report(TutorialSignal.ReconstructChosen);
            Assert.That(view.StepRows.Count(r => r.done), Is.EqualTo(2));
            Assert.That(view.StepRows[0].done && view.StepRows[1].done && !view.StepRows[2].done, Is.True);
            Assert.That(view.SuccessLine, Is.Null, "the task's check comes with its last step");
            runner.Abort();
        }

        [Test]
        public void Card_ShowsTheScanProblemAndOffersTryAgain_WhichGoesBackToTheMenuStep()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            GoScan(runner);
            foreach (var s in new[] { TutorialSignal.BoxCompleted, TutorialSignal.ReconstructChosen, TutorialSignal.PhotoCaptured, TutorialSignal.ScanSubmitted })
                runner.Report(s);
            runner.ReportScanFailure(ScanFailureText.Fallback);
            Assert.That(view.ProblemText, Is.EqualTo(ScanFailureText.Fallback));
            Assert.That(view.Action.gameObject.activeInHierarchy, Is.True, "Try again");
            Assert.That(Shown(view), Does.Contain("Try again"));
            view.Action.onClick.Invoke();
            Assert.That(view.ProblemText, Is.Null);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "back to pressing Capture");
            Assert.That(Get(controller, "_submitted"), Is.EqualTo(false));
            runner.Abort();
        }

        [Test]
        public void Card_Rooms_PressFindARoom_ThenPickOne_SampleRoomsOffline_AndTheDemoStatus()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var view = controller.View;
            var runner = controller.Runner;
            Go(runner, "4.1");
            Assert.That(view.Action.gameObject.activeInHierarchy, Is.True);
            view.Action.onClick.Invoke();     // no network stack here: the sample rooms show instead
            Assert.That(runner.RoomListShown, Is.True);
            Assert.That(runner.RoomListIsSample, Is.True);
            Assert.That(runner.Flow.ActionDone, Is.True, "pressing Find a room is the task");
            Assert.That(runner.Next(), Is.True);
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("4.2"));
            Assert.That(runner.Flow.ActionDone, Is.False, "picking is a separate task");
            view.Refresh();
            var rooms = view.Root.GetComponentsInChildren<Button>(true).Where(b => b.name == "Room 1" || b.name == "Room 2").ToArray();
            Assert.That(rooms.Length, Is.EqualTo(2));
            Assert.That(rooms[0].gameObject.activeInHierarchy, Is.True);
            rooms[1].onClick.Invoke();
            Assert.That(runner.Flow.ActionDone, Is.True);
            Assert.That(view.NoticeText, Is.EqualTo(TutorialRunner.DemoChoiceText));
            Assert.That(UnityEngine.Object.FindObjectsByType<FusionSessionBrowser>(FindObjectsSortMode.None), Is.Empty, "no network object was made");
            Assert.That(runner.Room.State, Is.EqualTo(SimulatedRoomState.Connecting));
            Assert.That(runner.Room.RoomName, Is.EqualTo("Room 2"));
            view.Refresh();
            Assert.That(Shown(view).Any(t => t.StartsWith("Joining Room 2")), Is.True);
            runner.Abort();
        }

        // ---- practice props

        private Camera _eyeCamera;
        private Vector3 _eyePosition;
        private Quaternion _eyeRotation;

        // The tutorial looks through the main camera. The Editor test scene has its own; borrow it and put it back in TearDown.
        private Camera NewEye(Vector3 position, Quaternion rotation)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var go = new GameObject("test-eye", typeof(Camera));
                go.tag = "MainCamera";
                _objects.Add(go);
                camera = go.GetComponent<Camera>();
            }
            else if (_eyeCamera == null)
            {
                _eyeCamera = camera;
                _eyePosition = camera.transform.position;
                _eyeRotation = camera.transform.rotation;
            }
            camera.transform.SetPositionAndRotation(position, rotation);
            return camera;
        }

        private static void Poll(TutorialController controller) =>
            typeof(TutorialController).GetMethod("PollSignals", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);

        private static void Grab(TutorialGrabbable grabbable) => grabbable.HandleSelected(grabbable.Interactable);

        private static void Drop(TutorialGrabbable grabbable, Vector3 position)
        {
            grabbable.transform.position = position;
            grabbable.HandleUnselected(grabbable.Interactable);
        }

        [Test]
        public void PracticeProps_AreLocalOnly_NoEntityNoRegistration_AndTargetsHideOnRequest()
        {
            var props = new TutorialPracticeProps();
            var cube = props.EnsureCube(null);
            var copy = props.EnsureCopy(Vector3.up);
            props.Highlight(cube.gameObject);
            Assert.That(props.EnsureCube(null), Is.SameAs(cube), "one cube");
            foreach (var go in new[] { cube.gameObject, copy.gameObject })
            {
                Assert.That(go.GetComponent<TutorialPracticeObject>(), Is.Not.Null, go.name);
                Assert.That(go.GetComponent<SbEntity>(), Is.Null, go.name + ": never an SbEntity, so never registered");
                Assert.That(go.GetComponent<Oculus.Interaction.RayInteractable>(), Is.Not.Null, go.name + ": selectable with a ray or a pinch");
            }
            Assert.That(UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(props.TargetCount, Is.EqualTo(1), "an outline");
            props.ClearTargets();
            Assert.That(props.TargetCount, Is.EqualTo(0));
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialOutline>(FindObjectsSortMode.None), Is.Empty, "hidden after success");
            Assert.That(cube != null, Is.True, "the object itself stays");
            props.DestroyAll();
            Assert.That(props.OwnedCount, Is.EqualTo(0));
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None), Is.Empty);
        }

        [Test]
        public void TheOutline_FollowsTheObjectsBounds_InTheAccent()
        {
            var props = new TutorialPracticeProps();
            var cube = props.EnsureCube(null);
            var outline = props.Highlight(cube.gameObject);
            var line = outline.GetComponent<LineRenderer>();
            Assert.That(line.positionCount, Is.EqualTo(16), "the 12 edges in one path");
            cube.transform.position = new Vector3(2f, 1f, 0f);
            outline.Follow();
            var min = line.GetPosition(0);
            var max = line.GetPosition(9);
            float half = TutorialPracticeProps.CubeSize * 0.5f + TutorialOutline.Margin;
            Assert.That(min.x, Is.EqualTo(2f - half).Within(1e-4f));
            Assert.That(max.x, Is.EqualTo(2f + half).Within(1e-4f));
            Assert.That(line.sharedMaterial.color.r, Is.EqualTo(UiTheme.Palette.Accent.r).Within(1e-4f));
            props.DestroyAll();
        }

        [Test]
        public void SpotAhead_IsInFrontAtChestHeight()
        {
            var eye = new GameObject("eye").transform;
            _objects.Add(eye.gameObject);
            eye.position = new Vector3(1f, 1.6f, 2f);
            eye.rotation = Quaternion.Euler(30f, 90f, 0f);    // looking east and down: only the heading counts
            var spot = TutorialPracticeProps.SpotAhead(eye, 1.0f);
            Assert.That(spot.x, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(spot.z, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(spot.y, Is.EqualTo(1.6f - 0.35f).Within(1e-4f));
        }

        [Test]
        public void TheDirectionHint_SaysWhichWayToTurn_UntilTheThingIsInView()
        {
            var eyePos = new Vector3(0f, 1.6f, 0f);
            var camera = NewEye(eyePos, Quaternion.identity);
            // 1.8 m away, 110 degrees to the right of the gaze, below eye height (a board out of the initial view).
            var spot = eyePos + Quaternion.AngleAxis(110f, Vector3.up) * Vector3.forward * 1.8f + Vector3.down * 0.6f;
            Assert.That(TutorialPracticeProps.IsInView(eyePos, camera.transform.forward, spot), Is.False, "out of the initial view");
            Assert.That(TutorialPracticeProps.YawTo(eyePos, camera.transform.forward, spot), Is.EqualTo(110f).Within(0.1f));
            Assert.That(TutorialPracticeProps.DirectionHint(eyePos, camera.transform.forward, spot), Is.EqualTo("Try looking to your right"));
            Assert.That(TutorialPracticeProps.DirectionHint(eyePos, camera.transform.forward, new Vector3(-3f, 1f, 0f)), Is.EqualTo("Try looking to your left"));
            Assert.That(TutorialPracticeProps.DirectionHint(eyePos, camera.transform.forward, new Vector3(0f, 1f, -3f)), Is.EqualTo("Try turning around"));
            camera.transform.rotation = Quaternion.LookRotation(spot - eyePos);
            Assert.That(TutorialPracticeProps.IsInView(eyePos, camera.transform.forward, spot), Is.True);
            Assert.That(TutorialPracticeProps.DirectionHint(eyePos, camera.transform.forward, spot), Is.Null, "no hint once it is in view");
            // The angle is on the gaze, in every direction: looking up at the ceiling does not find a thing on the floor side.
            camera.transform.rotation = Quaternion.LookRotation(spot - eyePos) * Quaternion.Euler(-40f, 0f, 0f);
            Assert.That(TutorialPracticeProps.IsInView(eyePos, camera.transform.forward, spot), Is.False);
        }

        [Test]
        public void TheGrabbable_ReportsGrabAndWhereItWasLetGo_AndIgnoresOtherInteractables()
        {
            var props = new TutorialPracticeProps();
            var cube = props.EnsureCube(null);
            int grabbed = 0;
            var released = new List<Vector3>();
            cube.Grabbed += () => grabbed++;
            cube.Released += released.Add;
            cube.HandleUnselected(cube.Interactable);
            Assert.That(released, Is.Empty, "a release with no grab");
            Grab(cube);
            Assert.That(cube.Held, Is.True);
            Drop(cube, new Vector3(0.4f, 1f, 1f));
            Assert.That(cube.Held, Is.False);
            Assert.That(grabbed, Is.EqualTo(1));
            Assert.That(released, Is.EqualTo(new[] { new Vector3(0.4f, 1f, 1f) }));
            var other = new GameObject("other-interactable");
            _objects.Add(other);
            var foreign = other.AddComponent<Oculus.Interaction.RayInteractable>();
            cube.HandleSelected(foreign);
            Assert.That(cube.Held, Is.False);
            props.DestroyAll();
        }

        // ---- the tasks over a real controller

        [Test]
        public void Task1_2_TheRealBoardsArePlacedByTheirDrops_TheWhiteboardOnAWall()
        {
            var room = BoardRoom.Create();
            try
            {
                var host = NewPanel();
                var controller = Begin(host);
                var runner = controller.Runner;
                Go(runner, "1.2");
                Assert.That(controller.Props.Cube, Is.Null, "no practice cube: the real boards are placed");
                Assert.That(controller.Props.TargetCount, Is.EqualTo(1), "the chessboard is outlined");
                Assert.That(runner.Flow.StepIndex, Is.EqualTo(0));

                // The whiteboard first does not count: the chessboard is the step.
                room.Hold(room.WhiteboardMotif, room.WallPose(-1.5f), true);
                room.Release(room.WhiteboardMotif);
                Assert.That(runner.Flow.StepIndex, Is.EqualTo(0));

                // Let go outside the room (no floor under it, no hologram): it goes back where it was and does not count.
                room.Hold(room.ChessMotif, new Pose(new Vector3(10f, 1.5f, 0f), Quaternion.identity));
                room.Release(room.ChessMotif);
                Assert.That(runner.Flow.StepIndex, Is.EqualTo(0), "no floor under it: not placed");
                Assert.That(runner.Hint, Is.EqualTo(TutorialController.LowerItText), "it says to point at the floor, not that nothing fits");

                room.Hold(room.ChessMotif, new Pose(new Vector3(0f, 0.6f, 1.4f), Quaternion.identity));
                PlacementDropCheck(room.Release(room.ChessMotif), placed: true);
                Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "dropped over the furniture, pulled to a valid spot: placed");
                Assert.That(controller.Props.TargetCount, Is.EqualTo(0), "its outline goes with the step");

                room.Hold(room.WhiteboardMotif, new Pose(new Vector3(-1f, 1f, -1f), Quaternion.Euler(0f, 180f, 0f)), false);
                room.Release(room.WhiteboardMotif);
                Assert.That(runner.Flow.ActionDone, Is.False, "on the floor while a wall fits it");
                Assert.That(runner.Hint, Is.EqualTo(TutorialController.HangItText));

                room.Hold(room.WhiteboardMotif, room.WallPose(-1.5f), true);
                room.Release(room.WhiteboardMotif);
                Assert.That(runner.Flow.ActionDone, Is.True, "hung on a wall");
                Assert.That(controller.View.SuccessLine, Is.EqualTo("The chessboard and the whiteboard are in place"));
                runner.Abort();
                Assert.That(room.Chess != null && room.Whiteboard != null, Is.True, "the boards are the user's and stay");
            }
            finally { room.Dispose(); }
        }

        [Test]
        public void Task1_2_TheDirectionHintComesBackAfterADropsHint_AndNeverSticks()
        {
            var room = BoardRoom.Create();
            try
            {
                var eyePos = new Vector3(0f, 1.6f, -2.5f);
                var camera = NewEye(eyePos, Quaternion.identity);
                var host = NewPanel();
                var controller = Begin(host);
                var runner = controller.Runner;
                Go(runner, "1.2");
                string note = runner.Flow.Current.Steps[0].Note;
                Poll(controller);
                Assert.That(runner.Hint, Is.EqualTo("Try looking to your left"), "the chessboard starts out of view");

                // A drop that is not this step's (the whiteboard) changes nothing, and the direction still clears once the board is seen.
                room.Hold(room.WhiteboardMotif, room.WallPose(-1.5f), true);
                room.Release(room.WhiteboardMotif);
                camera.transform.rotation = Quaternion.LookRotation(room.Chess.transform.position - eyePos);
                Poll(controller);
                Assert.That(runner.Hint, Is.EqualTo(note), "in view: the step's note, not a stale direction");

                // A drop with no floor under it (outside the room): the drop's hint; then looking away brings the direction back over it,
                // and looking back clears it.
                room.Hold(room.ChessMotif, new Pose(new Vector3(10f, 1.5f, 0f), Quaternion.identity));
                room.Release(room.ChessMotif);
                room.Chess.transform.position = new Vector3(-2f, 0.5f, -2f);   // where its release returns it (the settle runs in play mode)
                camera.transform.rotation = Quaternion.LookRotation(room.Chess.transform.position - eyePos);
                Poll(controller);
                Assert.That(runner.Hint, Is.EqualTo(TutorialController.LowerItText));
                camera.transform.rotation = Quaternion.LookRotation(-(room.Chess.transform.position - eyePos));
                Poll(controller);
                Assert.That(runner.Hint, Is.EqualTo("Try turning around"), "the direction is not blocked by the drop's hint");
                camera.transform.rotation = Quaternion.LookRotation(room.Chess.transform.position - eyePos);
                Poll(controller);
                Assert.That(runner.Hint, Is.EqualTo(note));
                runner.Abort();
            }
            finally { room.Dispose(); }
        }

        [Test]
        public void Task1_2_WithoutTheBoards_SaysSoAndCanBeSkipped()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            Go(runner, "1.2");
            Assert.That(runner.Hint, Is.EqualTo(TutorialController.NoBoardText));
            Assert.That(runner.Skip(), Is.True);
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("2.1"));
            runner.Abort();
        }

        private static void PlacementDropCheck(SpatialBlend.Interactions.PlacementDrop drop, bool placed) =>
            Assert.That(drop.Settles && drop.Placed, Is.EqualTo(placed));

        [Test]
        public void BoxCompletion_ReachesTheFlow_OnlyForADrawnBox()
        {
            var managerObject = new GameObject("box-manager");
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<SpatialBlend.Interactions.BoxAnnotationManager>();
            var host = NewPanel();
            var controller = Begin(host);
            GoScan(controller.Runner);
            manager.HandleAnnotationComplete(null, false);
            Assert.That(controller.Runner.Flow.StepIndex, Is.EqualTo(0), "a cancelled box (B undo) is not a box");
            Assert.That(controller.Runner.Flow.Running, Is.True, "and B never exits the tutorial");
            manager.HandleAnnotationComplete(null, true);
            Assert.That(controller.Runner.Flow.StepIndex, Is.EqualTo(1));
            controller.Runner.Abort();
        }

        [Test]
        public void ChoosingReconstruct3DInTheObjectMenu_IsTheMenuStep()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            GoScan(runner);
            runner.Report(TutorialSignal.BoxCompleted);
            var go = new GameObject("popup", typeof(RectTransform));
            _objects.Add(go);
            var popup = go.AddComponent<SpatialBlend.Interactions.UI.ManipulationPopUp>();
            // The same private handler Capture calls when it is switched on (the reconstruct toggle, relabeled on a box).
            typeof(SpatialBlend.Interactions.UI.ManipulationPopUp).GetMethod("OnReconstructToggleChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(popup, new object[] { false });
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "switching it off is not choosing it");
            typeof(SpatialBlend.Interactions.UI.ManipulationPopUp).GetMethod("OnReconstructToggleChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(popup, new object[] { true });
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(2));
            runner.Abort();
        }

        [Test]
        public void ASegmentationRequest_IsNotTheReconstructRequest()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            var onSubmitted = typeof(TutorialController).GetMethod("OnSubmitted", BindingFlags.Instance | BindingFlags.NonPublic);
            onSubmitted.Invoke(controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanSubmission(11, SpatialBlend.Freeform.Scanning.ScanKind.Segment, "mask", false, default) });
            Assert.That(Get(controller, "_submitted"), Is.EqualTo(false), "a tap's mask request does not complete 'press Reconstruct 3D'");
            Assert.That(runner.ScanInFlight, Is.False);
            onSubmitted.Invoke(controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanSubmission(12, SpatialBlend.Freeform.Scanning.ScanKind.Reconstruct, "out", false, default) });
            Assert.That(Get(controller, "_submitted"), Is.EqualTo(true));
            Assert.That(runner.ScanInFlight, Is.True);
            runner.Abort();
        }

        /// <summary>Drives the whole of task 3.1 over the canned transport; returns the controller at the last step, "Place the virtual object", with the scanned copy and the virtual object both in the room.</summary>
        private TutorialController RunScanUpToThePlacement(PanelHost host, out ScanCard scanPanel)
        {
            var managerObject = new GameObject("box-manager");
            _objects.Add(managerObject);
            var boxes = managerObject.AddComponent<SpatialBlend.Interactions.BoxAnnotationManager>();
            var controller = Begin(host);
            var runner = controller.Runner;
            GoScan(runner);
            Assert.That(controller.Scan.Mode, Is.EqualTo(TutorialScanMode.Canned), "no scan server answers: the canned transport");
            controller.Scan.Canned.DelaySeconds = 0.0;

            // 1. draw the box
            boxes.HandleAnnotationComplete(null, true);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1));
            // 2. Capture on the box menu. That opens the card; this step is still ReconstructChosen.
            runner.Report(TutorialSignal.ReconstructChosen);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(2));
            // 3. the card is open; capture the vase. There is no Confirm between the menu and the photo.
            scanPanel = ScanCard.Open(new Vector3(0f, 0.95f, 1.2f), new Vector3(0.38f, 0.5f, 0.38f), Quaternion.identity, null);
            _objects.Add(scanPanel.gameObject);
            Assert.That(scanPanel.FrameSource, Is.InstanceOf<TutorialHologramFrameSource>(), "a render of the practice vase stands in for the camera");
            Assert.That(controller.Props.Hologram, Is.Not.Null, "the practice vase stands in the user's box");
            Assert.That(Vector3.Distance(controller.Props.Hologram.transform.position, new Vector3(0f, 0.7f, 1.2f)), Is.LessThan(0.01f), "on the box's base");
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(2), "opening the card is not capturing");
            var manager = (SessionManager)Get(controller, "_manager");
            Assert.That(manager, Is.Not.Null, "the controller prepared the session manager");
            Assert.That(manager.Service, Is.Not.Null, "the canned service runs");
            Assert.That(manager.Service.ActiveWorkflow, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanWorkflow.SegmentThenReconstruct));
            controller.Scan.Canned.SegmentDelaySeconds = 0.0;
            Assert.That(scanPanel.PromptWithBox, Is.False, "the practice server's mask comes from the user's point");
            Assert.That(scanPanel.Capture(), Is.True, "the photo is taken, and the mask waits for a point");
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(3), "captured");
            Assert.That(scanPanel.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Selecting));
            Assert.That(scanPanel.Flow.Refining, Is.False, "nothing is asked before a point");
            Assert.That(scanPanel.AddPoint(VasePoint(controller.Scan.Canned.Segmenter)), Is.True);
            Assert.That(scanPanel.Flow.Refining, Is.True);
            controller.Scan.Tick();
            manager.Service.Update(0.05);
            Assert.That(scanPanel.Flow.Mask, Is.Not.Null, "the point covers the vase: " + scanPanel.Flow.Error + " silhouette=" + ((TutorialHologramFrameSource)scanPanel.FrameSource).LastSilhouettePixels);
            Assert.That(scanPanel.Flow.Mask.Count(b => b != 0), Is.GreaterThan(100), "the vase's silhouette from the same pose");
            Assert.That(scanPanel.Flow.MaskFromBox, Is.False, "a point, not the box");
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(3), "a mask is not the step: the user confirms it");
            Assert.That(scanPanel.PrimaryLabel, Is.EqualTo(ScanCardText.Confirm));
            scanPanel.PressPrimary();   // Confirm
            Assert.That(scanPanel.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Confirming), "it does not build by itself");
            Assert.That(scanPanel.PrimaryLabel, Is.EqualTo(ScanCardText.ThreeDCopy));
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(4), "confirmed: press 3D copy");
            scanPanel.PressPrimary();
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(5), "sent");
            Assert.That(runner.ScanInFlight, Is.True);
            Assert.That(controller.CopyReady, Is.False);
            // 5. the copy is built
            controller.Scan.Tick();
            manager.Service.Update(0.1);
            Poll(controller);
            Assert.That(controller.CopyReady, Is.True, "the canned result arrived");
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(6), "the copy is built, and the share line is next");
            Assert.That(controller.Props.Shared, Is.Null, "someone else's object has not arrived yet");
            runner.Narrator.Stop();
            Assert.That(runner.Report(TutorialSignal.LineHeard), Is.True);
            runner.Tick();
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(7), "the place step");
            Assert.That(controller.Props.Shared, Is.Not.Null);
            Assert.That(runner.ScanInFlight, Is.False);
            return controller;
        }

        // On the practice server a first point beside the vase finds nothing. The photo and the step stay; the
        // card says so, no scan problem is reported, and a point on the vase then gives the mask.
        [Test]
        public void Task3_APointBesideTheVaseFindsNothing_TheStepStays_AndAPointOnItGivesTheMask()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            GoScan(runner);
            Assert.That(controller.Scan.Mode, Is.EqualTo(TutorialScanMode.Canned));
            controller.Scan.Canned.DelaySeconds = 0.0;
            controller.Scan.Canned.SegmentDelaySeconds = 0.0;
            runner.Report(TutorialSignal.BoxCompleted);
            runner.Report(TutorialSignal.ReconstructChosen);
            var card = ScanCard.Open(new Vector3(0f, 0.95f, 1.2f), new Vector3(0.38f, 0.5f, 0.38f), Quaternion.identity, null);
            _objects.Add(card.gameObject);
            Assert.That(card.Capture(), Is.True);
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(3));
            var segmenter = controller.Scan.Canned.Segmenter;
            var miss = new SpatialBlend.Freeform.Scanning.ScanPoint(2, 2);
            Assert.That(segmenter.IsInside(miss), Is.False, "the photo's corner is not the vase");
            var manager = (SessionManager)Get(controller, "_manager");
            Assert.That(card.AddPoint(miss), Is.True);
            controller.Scan.Tick();
            manager.Service.Update(0.05);
            Poll(controller);
            Assert.That(card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Selecting), "the photo stays");
            Assert.That(card.Flow.RefineError.IsError, Is.True);
            Assert.That(runner.ScanProblem, Is.Null, "a missed point is not the step failing");
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(3));
            Assert.That(card.UndoPoint(), Is.True);
            Assert.That(card.AddPoint(VasePoint(segmenter)), Is.True);
            controller.Scan.Tick();
            manager.Service.Update(0.1);
            Assert.That(card.Flow.Mask, Is.Not.Null);
            Assert.That(card.Confirm(), Is.True);
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(4));
            runner.Abort();
        }

        private static SpatialBlend.Freeform.Scanning.ScanPoint VasePoint(CannedSegmenter segmenter)
        {
            for (int y = 0; y < segmenter.Height; y += 2)
                for (int x = 0; x < segmenter.Width; x += 2)
                {
                    var point = new SpatialBlend.Freeform.Scanning.ScanPoint(x, y);
                    if (segmenter.IsInside(point)) return point;
                }
            Assert.Fail("the practice vase has no mask pixel");
            return default;
        }

        [Test]
        public void Task3_ASecondCardOnAnotherBox_MovesThePracticeVaseToIt()
        {
            var host = NewPanel();
            var managerObject = new GameObject("box-manager");
            _objects.Add(managerObject);
            managerObject.AddComponent<SpatialBlend.Interactions.BoxAnnotationManager>();
            var controller = Begin(host);
            GoScan(controller.Runner);
            var first = ScanCard.Open(new Vector3(0f, 0.95f, 1.2f), new Vector3(0.38f, 0.5f, 0.38f), Quaternion.identity, null);
            _objects.Add(first.gameObject);
            first.Close();
            var second = ScanCard.Open(new Vector3(0.6f, 0.45f, 1.5f), new Vector3(0.3f, 0.3f, 0.3f), Quaternion.identity, null);
            _objects.Add(second.gameObject);
            Assert.That(controller.Props.Hologram, Is.Not.Null);
            Assert.That(Vector3.Distance(controller.Props.Hologram.transform.position, new Vector3(0.6f, 0.3f, 1.5f)), Is.LessThan(0.01f), "on the second box's base");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None)
                .Count(o => o != null && o.name == "TutorialHologramVase"), Is.EqualTo(1), "one practice vase, not one per card");
            second.Close();
            controller.Runner.Abort();
        }

        [Test]
        public void Task3_ALaterCardDoesNotMoveTheCopyOutOfTheBoxItWasMadeFor()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var copy = controller.Props.Copy;
            scanPanel.Close();
            var later = ScanCard.Open(new Vector3(4f, 2f, 5f), new Vector3(0.2f, 0.2f, 0.2f), Quaternion.identity, null);
            _objects.Add(later.gameObject);
            var live = (ScanAnchor)Get(controller, "_copyBox");
            Assert.That(live.Center.z, Is.EqualTo(5f).Within(1e-3f), "the live box follows the new card");
            object seat = Get(controller, "_copySeat");
            var captured = (ScanAnchor)seat.GetType().GetField("Box").GetValue(seat);
            Assert.That(captured.Center.z, Is.EqualTo(1.2f).Within(1e-3f), "the copy kept the box it was created in");
            copy.transform.position = new Vector3(9f, 9f, 9f);
            typeof(TutorialController).GetMethod("FitCopyToBox", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new[] { copy.gameObject, seat });
            Bounds shown = copy.GetComponentInChildren<Renderer>().bounds;
            Assert.That(shown.center.x, Is.EqualTo(0f).Within(1e-2f), "the load seats the captured box");
            Assert.That(shown.center.z, Is.EqualTo(1.2f).Within(1e-2f), "the load seats the captured box");
            later.Close();
            controller.Runner.Abort();
        }

        [Test]
        public void Task3_AHeldCopyIsNotPulledBackIntoTheBox()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var copy = controller.Props.Copy;
            Grab(copy);
            Assert.That(copy.Held, Is.True);
            var carried = new Vector3(9f, 1.4f, 3f);
            copy.transform.position = carried;
            object seat = Get(controller, "_copySeat");
            var fit = typeof(TutorialController).GetMethod("FitCopyToBox", BindingFlags.Instance | BindingFlags.NonPublic);
            fit.Invoke(controller, new[] { copy.gameObject, seat });
            Assert.That(copy.transform.position, Is.EqualTo(carried), "a grab keeps the copy where the user holds it");
            Drop(copy, carried);
            Assert.That(copy.Held, Is.False);
            fit.Invoke(controller, new[] { copy.gameObject, seat });
            Bounds shown = copy.GetComponentInChildren<Renderer>().bounds;
            Assert.That(shown.center.x, Is.EqualTo(scanPanel.ScannedBox.Center.x).Within(1e-2f), "once let go, a later fit uses the captured box");
            Assert.That(shown.center.z, Is.EqualTo(scanPanel.ScannedBox.Center.z).Within(1e-2f), "once let go, a later fit uses the captured box");
            scanPanel.Close();
            controller.Runner.Abort();
        }

        [Test]
        public void Task3_TheCopyTakesThePracticeVasesPlace_AndTheVaseReturnsWhenTheCopyIsGone()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var hologram = controller.Props.Hologram;
            var copy = controller.Props.Copy;
            Assert.That(hologram, Is.Not.Null);
            Assert.That(copy, Is.Not.Null);
            Assert.That(hologram.gameObject.activeSelf, Is.False, "the practice vase hides once the copy appears");
            Vector3 vaseAt = hologram.transform.position;
            Bounds copyBounds = copy.GetComponentInChildren<Renderer>().bounds;
            Assert.That(copyBounds.center.x, Is.EqualTo(vaseAt.x).Within(1e-2f), "the copy stays on the vase's footprint");
            Assert.That(copyBounds.center.z, Is.EqualTo(vaseAt.z).Within(1e-2f), "the copy stays on the vase's footprint");
            Assert.That(copyBounds.min.y, Is.EqualTo(vaseAt.y).Within(1e-2f), "and on the box base, where the vase stood");
            float boxTop = scanPanel.ScannedBox.Center.y + scanPanel.ScannedBox.Size.y * 0.5f;
            Assert.That(copyBounds.max.y, Is.LessThanOrEqualTo(boxTop + 1e-3f), "inside the box");

            controller.Props.DestroyCopy();
            Poll(controller);
            Assert.That(controller.Props.Copy, Is.Null);
            Assert.That(hologram.gameObject.activeSelf, Is.True, "deleting the copy shows the vase again");
            Assert.That(Vector3.Distance(hologram.transform.position, vaseAt), Is.LessThan(0.01f));

            hologram.gameObject.SetActive(false);
            typeof(TutorialController).GetMethod("OnRetryScan", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);
            Assert.That(controller.Props.Hologram, Is.SameAs(hologram), "a reset keeps the same vase");
            Assert.That(hologram.gameObject.activeSelf, Is.True, "a reset shows the vase again");
            Assert.That(Vector3.Distance(hologram.transform.position, vaseAt), Is.LessThan(0.01f), "still in the box");
            controller.Runner.Abort();
        }

        [Test]
        public void Task3_TheWholeScanIsOneSequence_FromTheBoxToThePlacedCopy_OverTheCannedTransport()
        {
            var host = NewPanel();
            var appSink = new RecordingScanSink();
            var sessionManager = SessionManager.EnsureInstance();
            sessionManager.ResultSink = appSink;
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var runner = controller.Runner;
            var copy = controller.Props.Copy;
            Assert.That(copy, Is.Not.Null, "the user's own scanned object appears");
            Assert.That(copy.GetComponent<SbEntity>(), Is.Null, "a practice object, never shared");
            Assert.That(appSink.Results, Is.Empty, "and never the app's");
            Assert.That(scanPanel.Anchor.Center, Is.EqualTo(scanPanel.ScannedBox.Center));
            Renderer shown = copy.GetComponentInChildren<Renderer>();
            Assert.That(shown, Is.Not.Null);
            Bounds copyBounds = shown.bounds;
            Assert.That(copyBounds.center.x, Is.EqualTo(scanPanel.ScannedBox.Center.x).Within(1e-3f), "centered in the box");
            Assert.That(copyBounds.center.z, Is.EqualTo(scanPanel.ScannedBox.Center.z).Within(1e-3f), "centered in the box");
            Assert.That(copyBounds.min.y, Is.EqualTo(scanPanel.ScannedBox.Center.y - scanPanel.ScannedBox.Size.y * 0.5f).Within(1e-3f), "rests on the bottom face");
            Assert.That(copyBounds.max.y, Is.LessThanOrEqualTo(scanPanel.ScannedBox.Center.y + scanPanel.ScannedBox.Size.y * 0.5f + 1e-3f), "and stays inside the box");
            // The stand-in is a unit cylinder (height 1, footprint 0.8) scaled by the box, not a fixed 0.3 m.
            float fitted = ScanBoxRest.UniformFit(new Vector3(0.8f, 1f, 0.8f), scanPanel.ScannedBox.Size);
            Assert.That(copyBounds.size.y, Is.EqualTo(fitted).Within(0.02f), "height follows the box");
            Assert.That(copyBounds.size.x, Is.LessThanOrEqualTo(scanPanel.ScannedBox.Size.x + 0.02f));
            Assert.That(copyBounds.size.z, Is.LessThanOrEqualTo(scanPanel.ScannedBox.Size.z + 0.02f));

            // The last step's object arrives separately: a DIFFERENT object (the lamp), not in the user's room, created as a virtual object.
            var shared = controller.Props.Shared;
            Assert.That(shared, Is.Not.Null, "someone else's object arrived after the scan was shared");
            Assert.That(shared, Is.Not.SameAs(copy));
            Assert.That(shared.gameObject.name, Is.EqualTo("TutorialVirtualObject"));
            Assert.That(shared.GetComponent<SbEntity>(), Is.Null, "a practice object, never shared");
            Assert.That(TutorialAssets.LoadSharedModel(), Is.Not.EqualTo(TutorialAssets.LoadSampleModel()), "a different model from the vase");
            Assert.That(Vector3.Distance(shared.transform.position, TutorialPracticeProps.SpotAhead(Camera.main != null ? Camera.main.transform : null, 1.0f, 0.35f, 0.05f)), Is.LessThan(0.01f));
            Assert.That(controller.Props.TargetCount, Is.EqualTo(1), "the add-as-new box for the virtual object (no pad: it goes anywhere), none for the scanned copy");
            var box = shared.transform.Find("TutorialRealAnchorBox");
            Assert.That(box, Is.Not.Null, "the pending box, in its own frame");
            var edges = box.GetComponentsInChildren<LineRenderer>();
            Assert.That(edges.Length, Is.EqualTo(9), "the shared real-object box, as the add-as-new box: its edges and eight corner brackets");
            var violet = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.SharedReal;
            var drawn = box.Find("Box/Edges").GetComponent<LineRenderer>().startColor;
            Assert.That(new[] { drawn.r, drawn.g, drawn.b }, Is.EqualTo(new[] { violet.r, violet.g, violet.b }).Within(1e-3f), "the shared real-object colour");
            Assert.That(edges.All(l => l.sharedMaterial == SpatialBlend.Samples.FreeformCollaboration.UI.SceneAnchorHighlight.LineMaterial), Is.True, "the one shared material");
            Assert.That(copy.transform.Find("TutorialRealAnchorBox"), Is.Null);
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialOutline>(FindObjectsSortMode.None), Is.Empty, "no mint outline: the box is the outline");
            Assert.That(runner.Hint, Does.Contain("connects it to the interaction"), "the note says why it is placed");
            DeliverResult(controller);                       // a second result of the same request
            Assert.That(controller.Props.TargetCount, Is.EqualTo(1), "a duplicate result adds no second box");
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("3.1"), "still the same task, no frame between");
            Assert.That(runner.Flow.ActionDone, Is.False);

            Grab(copy);                                      // moving the scanned copy is not the step
            Drop(copy, copy.transform.position + new Vector3(0.3f, 0f, 0f));
            Assert.That(runner.Flow.ActionDone, Is.False, "only the virtual object counts");
            Drop(shared, shared.transform.position + new Vector3(0.6f, 0f, 0.4f));   // a release that was never a grab
            Assert.That(runner.Flow.ActionDone, Is.False, "letting go without a grab is not a placement");
            Grab(shared);
            Drop(shared, shared.transform.position + new Vector3(0.6f, 0f, 0.4f));
            Assert.That(runner.Flow.ActionDone, Is.True, "let go where the user wants it (no room known here: nothing to check against)");
            Physics.SyncTransforms();
            Assert.That(shared.GetComponent<Collider>().bounds.min.y, Is.EqualTo(0f).Within(1e-3f), "set down on the floor even with no room known");
            Assert.That(controller.View.SuccessLine, Is.EqualTo("Placed. It's now part of the shared space"));
            Assert.That(controller.Props.TargetCount, Is.EqualTo(0), "the pending box goes once it is placed");
            Assert.That(controller.Props.Copy, Is.Not.Null, "the scanned copy stays in the room until the task is left");
            Assert.That(controller.Props.Shared, Is.Not.Null);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(8));
            runner.Abort();
            Assert.That(sessionManager.ResultSink, Is.SameAs(appSink), "the app's sink is back");
        }

        [Test]
        public void ThePartnersObject_DroppedOverFurniture_IsPulledToAValidSpotByTheOneRule_AndCounts()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out _);
            var runner = controller.Runner;
            // The lobby's room (an app with a room and its furniture) is there when the object is let go.
            var room = BoardRoom.Create();
            try
            {
                var shared = controller.Props.Shared;
                Assert.That(shared, Is.Not.Null);
                Grab(shared);
                Drop(shared, new Vector3(0f, 0.5f, 1.4f));      // in the middle of the room's furniture
                Assert.That(runner.Flow.ActionDone, Is.True, "a drop anywhere in the room counts");
                Vector3 p = shared.transform.position;
                bool overFurniture = p.x > -0.5f && p.x < 0.5f && p.z > 1.0f && p.z < 1.8f;
                Assert.That(overFurniture, Is.False, "pulled off the furniture, as any drop is");
                Physics.SyncTransforms();
                Assert.That(shared.GetComponent<Collider>().bounds.min.y, Is.EqualTo(0f).Within(1e-3f), "set down on the floor, never left floating");
                runner.Abort();
            }
            finally { room.Dispose(); }
        }

        [Test]
        public void ThePartnersObject_WhereNoSpotInTheRoomFits_SaysSo_AndDoesNotCount()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out _);
            var runner = controller.Runner;
            // A room whose floor is all furniture: no free spot anywhere for the object.
            var room = BoardRoom.Create(BoardRoom.SquareRoom(1f, BoardRoom.Box(-1f, -1f, 1f, 1f)));
            try
            {
                var shared = controller.Props.Shared;
                Grab(shared);
                Drop(shared, new Vector3(0f, 1.2f, 0f));
                Assert.That(runner.Flow.ActionDone, Is.False);
                Assert.That(runner.Hint, Is.EqualTo(TutorialController.NoSpotText));
                runner.Abort();
            }
            finally { room.Dispose(); }
        }

        [Test]
        public void TryAgainAtTheLastStep_RemovesTheVirtualObjectItsBoxAndItsWatch_AndASecondScanMakesExactlyOne()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out _);
            var runner = controller.Runner;
            Assert.That(controller.Props.Shared, Is.Not.Null);
            Assert.That(controller.Props.TargetCount, Is.EqualTo(1));
            runner.ReportScanFailure(ScanFailureText.Fallback);          // a tap's failed mask request in the still-open panel, say
            controller.View.Refresh();
            controller.View.Action.onClick.Invoke();                     // Try again
            Assert.That(controller.Props.Shared, Is.Null);
            Assert.That(controller.Props.Copy, Is.Null);
            Assert.That(controller.Props.TargetCount, Is.EqualTo(0), "no orphaned box");
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1));
            // The same task again, to the last step: exactly one virtual object and one box.
            runner.Report(TutorialSignal.ReconstructChosen);
            runner.Report(TutorialSignal.PhotoCaptured);
            runner.Report(TutorialSignal.MaskReady);
            runner.Report(TutorialSignal.ScanSubmitted);
            runner.Report(TutorialSignal.ScanCopyReady);
            runner.Narrator.Stop();
            runner.Report(TutorialSignal.LineHeard);
            runner.Tick();
            Assert.That(controller.Props.Shared, Is.Not.Null);
            Assert.That(controller.Props.TargetCount, Is.EqualTo(1));
            runner.Abort();
        }

        [Test]
        public void TheVirtualObjectsBox_IsRefittedToTheLoadedModel_NotToItsOwnWire_WhateverScaleTheHolderGot()
        {
            var props = new TutorialPracticeProps();
            var holder = new GameObject("holder");
            _objects.Add(holder);
            holder.transform.position = new Vector3(1f, 1f, 1f);
            holder.transform.localScale = Vector3.one * 0.5f;       // FitToSize scaled the holder to fit the model
            var model = GameObject.CreatePrimitive(PrimitiveType.Cube);
            model.transform.SetParent(holder.transform, false);
            model.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            model.transform.localScale = new Vector3(0.4f, 0.8f, 0.4f);
            holder.AddComponent<BoxCollider>();
            var box = props.AddRealAnchorBox(holder, TutorialPracticeProps.SharedSize);
            TutorialPracticeProps.FitCollider(holder);
            TutorialPracticeProps.FitRealAnchorBox(holder);
            Bounds world = model.GetComponent<Renderer>().bounds;
            Assert.That(Vector3.Distance(box.transform.position, world.center), Is.LessThan(1e-3f), "centred on the model");
            Assert.That(box.transform.lossyScale.x, Is.EqualTo(1f).Within(1e-4f), "built in metres, not a scaled unit box (the brackets keep their lengths)");
            Vector3 size = box.transform.Find("Box/Fill").lossyScale;
            var bracket = box.transform.Find("Box/Bracket0").GetComponent<LineRenderer>();
            float arm = Vector3.Distance(bracket.transform.TransformPoint(bracket.GetPosition(0)), bracket.transform.TransformPoint(bracket.GetPosition(1)));
            Assert.That(arm, Is.EqualTo(Mathf.Min(0.09f, size.x * 0.25f)).Within(1e-3f), "the scan box's bracket: a quarter of the edge, at most 9 cm, in metres");
            Assert.That(size.x, Is.EqualTo(world.size.x + 0.02f).Within(1e-3f));
            Assert.That(size.y, Is.EqualTo(world.size.y + 0.02f).Within(1e-3f), "its own wire did not enlarge the box");
            var collider = holder.GetComponent<BoxCollider>();
            Assert.That(collider.size.y * holder.transform.lossyScale.y, Is.EqualTo(world.size.y).Within(1e-3f), "nor the collider");
            props.DestroyAll();
        }

        [Test]
        public void ExitingInTheMiddleOfTheSequence_RemovesEverythingTheTaskMade()
        {
            var host = NewPanel();
            var appSink = new RecordingScanSink();
            var sessionManager = SessionManager.EnsureInstance();
            sessionManager.ResultSink = appSink;
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var scanPanelObject = scanPanel.gameObject;
            Assert.That(controller.Props.Copy, Is.Not.Null);
            Assert.That(controller.Props.Shared, Is.Not.Null);
            controller.Runner.RequestExit();
            controller.Runner.ConfirmExit();
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None), Is.Empty, "copy, box and outline are gone");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialOutline>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsSortMode.None), Is.Empty, "nothing was ever registered");
            Assert.That(scanPanelObject == null, Is.True, "the open scan panel is closed");
            Assert.That(sessionManager.ResultSink, Is.SameAs(appSink));
            Assert.That(sessionManager.Service, Is.Null, "the canned service is dropped, as the tutorial found it");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialController>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(host.Panel.TutorialActive, Is.False);
        }

        [Test]
        public void SkippingTheScan_ClosesItsPanel_DropsTheCopy_AndALateResultNeverAppears()
        {
            var host = NewPanel();
            var controller = RunScanUpToThePlacement(host, out var scanPanel);
            var runner = controller.Runner;
            var panelObject = scanPanel.gameObject;
            runner.Skip();                                  // on to Find a room
            Assert.That(panelObject == null, Is.True, "closed when leaving the task");
            Assert.That(controller.Props.Copy, Is.Null);
            Assert.That(controller.Props.Shared, Is.Null, "the virtual object goes with its task");
            Assert.That(controller.CopyReady, Is.False, "the task starts over if it is entered again");

            string prefix = Path.Combine(Path.GetTempPath(), "late-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix, new byte[] { 1, 2, 3 });
            typeof(TutorialController).GetMethod("OnScanResult", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanReconstructionResult { OutputPrefix = prefix } });
            Assert.That(File.Exists(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix), Is.False, "the stray file is removed");
            Assert.That(controller.CopyReady, Is.False, "a result after the task is dropped");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None), Is.Empty);
            runner.Abort();
        }

        private static void DeliverResult(TutorialController controller)
        {
            string prefix = Path.Combine(Path.GetTempPath(), "stale-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix, new byte[] { 1, 2, 3 });
            typeof(TutorialController).GetMethod("OnScanResult", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanReconstructionResult { OutputPrefix = prefix } });
            Assert.That(File.Exists(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix), Is.False, "the result file is always consumed");
        }

        [Test]
        public void AResultOfAnEarlierVisit_IsDropped_WhenTheTaskIsEnteredAgain()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            GoScan(runner);
            typeof(TutorialController).GetMethod("OnSubmitted", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanSubmission(12, SpatialBlend.Freeform.Scanning.ScanKind.Reconstruct, "out", false, default) });
            Assert.That(Get(controller, "_submitted"), Is.EqualTo(true));
            runner.Back();                                   // 2.2: the request is cancelled and the flags are reset
            runner.Skip();                                   // 3.1 again, nothing sent in this visit
            DeliverResult(controller);                       // the old request's result lands now
            Assert.That(controller.CopyReady, Is.False, "not this visit's copy");
            Assert.That(controller.Props.Copy, Is.Null);
            Assert.That(controller.Props.Shared, Is.Null);
            runner.Abort();
        }

        [Test]
        public void GoingBackFromTheScan_StartsItOver()
        {
            var host = NewPanel();
            var controller = Begin(host);
            var runner = controller.Runner;
            GoScan(runner);
            Set(controller, "_submitted", true);
            Set(controller, "_copyReady", true);
            runner.NoteScanInFlight(true);
            runner.Back();                                   // 2.2
            Assert.That(Get(controller, "_submitted"), Is.EqualTo(false), "a new Reconstruct 3D is needed");
            Assert.That(Get(controller, "_copyReady"), Is.EqualTo(false));
            Assert.That(runner.ScanInFlight, Is.False);
            runner.Abort();
        }

        // ---- feedback

        [Test]
        public void TheCheckMark_IsACircleWithTheCheckCutOut_AndTheChimeIsShort()
        {
            Assert.That(TutorialFeedback.Coverage(8f, 32f), Is.GreaterThan(0.95f), "inside the circle, away from the check");
            Assert.That(TutorialFeedback.Coverage(28f, 22f), Is.LessThan(0.05f), "on the check stroke");
            Assert.That(TutorialFeedback.Coverage(1f, 1f), Is.EqualTo(0f).Within(0.01f), "outside the circle");
            var sprite = TutorialFeedback.CheckSprite;
            Assert.That(sprite, Is.Not.Null);
            Assert.That(sprite.texture.width, Is.EqualTo(TutorialFeedback.SpriteSize));
            var chime = TutorialFeedback.BuildChime();
            _objects.Add(chime);
            Assert.That(chime.length, Is.EqualTo(TutorialFeedback.ChimeSeconds).Within(0.01f));
            var samples = new float[chime.samples];
            chime.GetData(samples, 0);
            Assert.That(samples.Max(Mathf.Abs), Is.InRange(0.05f, 0.5f), "audible but not loud");
        }

        // ---- the mode task, over the real mode manager and the real mode-switch assist

        private static SpatialBlend.Interactions.InteractionMode Edit => SpatialBlend.Interactions.InteractionMode.SceneEdit;
        private static SpatialBlend.Interactions.InteractionMode Interaction => SpatialBlend.Interactions.InteractionMode.Interaction;

        private static SpatialBlend.Interactions.UI.ModeSwitchClickTarget EditButton(ModeRig rig) =>
            rig.Assist.Ui.PromptObject.GetComponentInChildren<SpatialBlend.Interactions.UI.ModeSwitchClickTarget>(true);

        private static SpatialBlend.Interactions.UI.ModeSwitchClickTarget Chip(ModeRig rig) =>
            rig.Assist.Ui.ChipObject.GetComponent<SpatialBlend.Interactions.UI.ModeSwitchClickTarget>();

        /// <summary>Starts the tutorial with the user in <paramref name="start"/>, goes to the mode task, and makes the eye see the cube.</summary>
        private TutorialController BeginModeTask(PanelHost host, SpatialBlend.Interactions.InteractionMode start, out ModeRig rig)
        {
            rig = _rig = NewModeRig(start);
            rig.Assist.ConfigureHoverDwell(1f);   // the default 1 s
            var controller = Begin(host);
            Go(controller.Runner, "2.2");
            return controller;
        }

        [Test]
        public void ModeTask_StartsInInteraction_WithTheAssistLive_AndNothingAdvancesWithoutTheEvent()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Edit, out var rig);     // they were in Editing before the task
            var runner = controller.Runner;
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction), "the task starts in Interaction");
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False, "the assist is live for this task");
            Assert.That(controller.Props.Cube, Is.Not.Null, "a cube to point at");
            Assert.That(controller.Props.Cube.GetComponent<SpatialBlend.Interactions.ModeSwitchEditableMarker>(), Is.Not.Null);
            Assert.That(controller.Props.Cube.GetComponent<SbEntity>(), Is.Null, "still a local prop");

            for (int i = 0; i < 5; i++) { Poll(controller); rig.Step(i * 3f); }
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(0), "no event, no progress, however long it waits");
            Assert.That(runner.Flow.ActionDone, Is.False);
            Assert.That(runner.Flow.CanNext, Is.False);
            Assert.That(rig.Assist.Ui.PromptVisible, Is.False, "and the assist shows nothing until the ray rests on the cube");
            runner.Abort();
        }

        [Test]
        public void ModeTask_PointingAtTheCubeThenPressingEdit_ThroughTheAssist_IsStepOne_AndTheChipSaysEditing()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Interaction, out var rig);
            var runner = controller.Runner;
            var cube = controller.Props.Cube;

            rig.Hovered = cube.Interactable;
            rig.Step(0f);
            Assert.That(rig.Assist.Ui.PromptVisible, Is.False, "a moment first: the 1 s dwell");
            rig.Step(1.05f);
            Assert.That(rig.Assist.Ui.PromptVisible, Is.True, "'Edit this object?' floats above the cube");
            Assert.That(rig.Assist.Ui.PromptRoot, Is.SameAs(cube.transform));
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(0), "the prompt alone is not the step");

            Tap(EditButton(rig));                                        // the real [Edit] button
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit));
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "verified by the mode becoming Scene Edit");
            Assert.That(runner.Flow.ActionDone, Is.False, "one step of two");
            rig.Step(1.2f);
            Assert.That(rig.Assist.Ui.ChipVisible, Is.True);
            Assert.That(rig.Assist.Ui.ChipText, Does.Contain("Edit"), "the chip shows Editing");
            Assert.That(rig.Assist.Ui.PromptVisible, Is.False);
            runner.Abort();
        }

        [Test]
        public void ModeTask_TappingTheChipGoesBack_StepTwo_AndTheTaskIsDone()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Interaction, out var rig);
            var runner = controller.Runner;
            rig.Hovered = controller.Props.Cube.Interactable;
            rig.Step(0f); rig.Step(1.05f);
            Tap(EditButton(rig)); Poll(controller);
            rig.Step(1.2f);
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1), "still in Editing: the way back has not happened");

            Tap(Chip(rig));                                              // the real chip
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction));
            Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.True);
            controller.View.Refresh();
            Assert.That(controller.View.SuccessLine, Is.EqualTo("Back in Interaction. The chip switches modes any time"));
            runner.Abort();
        }

        [Test]
        public void ModeTask_WaitingForTheAutoReturn_IsStepTwoToo()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Interaction, out var rig);
            var runner = controller.Runner;
            rig.Hovered = controller.Props.Cube.Interactable;
            rig.Step(0f); rig.Step(1.05f);
            Tap(EditButton(rig)); Poll(controller);
            rig.Hovered = null;
            rig.Step(1.2f);
            rig.Step(8f);
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit), "idle for less than 10 s");
            rig.Step(12f);
            Assert.That(rig.Assist.Ui.ToastVisible, Is.True, "'Back to Interaction in 5...'");
            Poll(controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1));
            rig.Step(18f);                                               // the countdown ran out
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction));
            Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.True, "the automatic return counts as well");
            runner.Abort();
        }

        [Test]
        public void ModeTask_AfterIt_TheAssistIsSuspendedAgain_ForTheTasksThatSetModesThemselves()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Interaction, out var rig);
            var runner = controller.Runner;
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
            rig.Hovered = controller.Props.Cube.Interactable;
            rig.Step(0f); rig.Step(1.05f);
            Tap(EditButton(rig)); Poll(controller);
            Tap(Chip(rig)); Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.True);

            Assert.That(runner.Next(), Is.True);                        // on to the scan
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("3.1"));
            Assert.That(controller.Props.Cube, Is.Null, "the cube went with its task");
            // In the scan task the user is in Interaction, so the chip is still there for the switch ...
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
            Tap(Chip(rig));
            runner.Tick();
            // ... and once they are in Editing the assist is suspended again.
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit));
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.True);
            rig.Step(5f); rig.Step(30f);
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit), "no idle auto-return in the middle of the scan");
            Assert.That(rig.Assist.Ui.ToastVisible, Is.False);
            runner.Abort();
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False, "released at the end");
        }

        [Test]
        public void ModeTask_ExitingMidStep_RestoresTheUsersModeAndTheAssist_AndRemovesTheCube()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Interaction, out var rig);
            var runner = controller.Runner;
            rig.Hovered = controller.Props.Cube.Interactable;
            rig.Step(0f); rig.Step(1.05f);
            Tap(EditButton(rig)); Poll(controller);
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit));

            runner.RequestExit(); runner.ConfirmExit();

            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction), "the mode they had before the tutorial");
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False, "the assist is as they had it");
            Assert.That(UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(UnityEngine.Object.FindObjectsByType<SpatialBlend.Interactions.ModeSwitchEditableMarker>(FindObjectsSortMode.None), Is.Empty);
        }

        [Test]
        public void ModeTask_ExitingMidStep_PutsBackEditingToo_WhenThatIsWhatTheUserHad()
        {
            var host = NewPanel();
            var controller = BeginModeTask(host, Edit, out var rig);
            var runner = controller.Runner;
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction));
            runner.Abort();
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit));
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
        }


        [Test]
        public void ModeTask_InTheLobby_WhereTheAssistIsNotActiveYet_StartsItForTheTaskAndGivesItBack()
        {
            var host = NewPanel();
            _rig = NewModeRig(Interaction);
            _rig.Assist.SetActive(false);          // before a session is ready the app keeps the assist off (ControlPanel starts it)
            var controller = Begin(host);
            var runner = controller.Runner;
            Assert.That(_rig.Assist.IsActive, Is.False, "not started by the tutorial's first tasks");
            Go(runner, "2.2");
            Assert.That(_rig.Assist.IsActive, Is.True, "started for the task, so the chip and the Edit prompt exist");
            _rig.Hovered = controller.Props.Cube.Interactable;
            _rig.Step(0f); _rig.Step(1.05f);
            Assert.That(_rig.Assist.Ui.PromptVisible, Is.True);
            Tap(EditButton(_rig)); Poll(controller);
            Tap(Chip(_rig)); Poll(controller);
            Assert.That(runner.Flow.ActionDone, Is.True);
            runner.Next();                          // the scan: still Interaction, so the assist stays for the switch
            Assert.That(_rig.Assist.IsActive, Is.True);
            Tap(Chip(_rig));
            runner.Tick();
            Assert.That(_rig.Assist.IsActive, Is.False, "in Editing for the scan: suspended and the lobby's state is back");
            runner.Abort();
            Assert.That(_rig.Assist.IsActive, Is.False, "as the app had it");
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
        }

        [Test]
        public void ModeTask_InARoom_WhereTheAssistIsAlreadyActive_LeavesItActiveAtTheEnd()
        {
            var host = NewPanel();
            _rig = NewModeRig(Interaction);
            Assert.That(_rig.Assist.IsActive, Is.True);
            var controller = Begin(host);
            Go(controller.Runner, "2.2");
            controller.Runner.Abort();
            Assert.That(_rig.Assist.IsActive, Is.True, "the tutorial only stops an assist that it started");
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
        }

        [Test]
        public void ScanBoxStep_RequiresEditing_TheCardSaysToSwitchFirst_AndTheSwitchIsVerified()
        {
            var managerObject = new GameObject("box-manager");
            _objects.Add(managerObject);
            var boxes = managerObject.AddComponent<SpatialBlend.Interactions.BoxAnnotationManager>();
            var host = NewPanel();
            var rig = _rig = NewModeRig(Interaction);
            var controller = Begin(host);
            var runner = controller.Runner;
            Go(runner, "3.1");
            controller.View.Refresh();

            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Interaction), "the scan no longer switches to Editing behind the user's back");
            Assert.That(controller.View.HintText, Is.EqualTo(TutorialScript.SwitchToEditingText));
            boxes.HandleAnnotationComplete(null, true);                  // a box event in Interaction
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(0), "needs Editing");
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False, "the assist is live for the switch");
            rig.Step(0f);
            Assert.That(rig.Assist.Ui.ChipVisible, Is.True, "and the chip is there to switch with");
            Assert.That(rig.Assist.Ui.ChipText, Does.Contain("Interaction"));
            Tap(Chip(rig));
            runner.Tick();
            controller.View.Refresh();
            Assert.That(rig.Mode.CurrentMode, Is.EqualTo(Edit));
            Assert.That(controller.View.HintText, Is.Null, "the switch is seen: the hint goes");
            boxes.HandleAnnotationComplete(null, true);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(1));
            runner.Abort();
        }

        // ---- the tutorial never depends on the scan server

        private sealed class RealServerRig
        {
            public TutorialController Controller;
            public FakeScanTransport Server;
            public SessionManager Manager;
            public ScanCard Card;
            public FakeScanFiles Files;
        }

        /// <summary>The app's own scan service is connected to a (fake) server, as on the device with spatialblend-local.json; the scan card is open and aimed.</summary>
        private RealServerRig OpenRealServerCard()
        {
            // a controller left running by an earlier test would answer the card's Opening event too
            foreach (var stale in UnityEngine.Object.FindObjectsByType<TutorialController>(FindObjectsSortMode.None)) stale.Runner.Abort();
            var host = NewPanel();
            var managerObject = new GameObject("session-manager");
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<SessionManager>();
            var server = new FakeScanTransport();
            var files = new FakeScanFiles();
            string dir = Path.Combine(Path.GetTempPath(), "tutorial-real-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var config = new SpatialBlend.Freeform.Scanning.ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = dir, OutputDirectory = dir };
            Assert.That(manager.InitializeWith(server, files, config), Is.True);
            typeof(SessionManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetSetMethod(true)?.Invoke(null, new object[] { manager });
            var controller = Begin(host);
            GoScan(controller.Runner);
            Assert.That(controller.Scan.Mode, Is.EqualTo(TutorialScanMode.Real), "a server is configured and connected: the real path, same as a normal scan");
            var card = ScanCard.Open(new Vector3(0f, 0.95f, 1.2f), new Vector3(0.38f, 0.5f, 0.38f), Quaternion.identity, null);
            _objects.Add(card.gameObject);
            Assert.That(card.FrameSource, Is.Not.InstanceOf<TutorialHologramFrameSource>(), "the real path scans what the camera sees");
            // a still photo with a box stands in for the passthrough camera, which the editor does not have
            var photo = new Texture2D(64, 64, TextureFormat.RGB24, false);
            _objects.Add(photo);
            card.FrameSource = new StillFrameSource(photo, new SpatialBlend.Freeform.Scanning.ScanPixelBox(10, 10, 50, 50));
            return new RealServerRig { Controller = controller, Server = server, Manager = manager, Card = card, Files = files };
        }

        private static void Recover(TutorialController controller) =>
            typeof(TutorialController).GetMethod("RecoverWithCanned", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);

        /// <summary>
        /// The real path's Capture and the user's first point: the photo is held with nothing asked, and the point alone is sent
        /// (point-based segmentation, no box prompt, no box-only request after the photo).
        /// </summary>
        private static void CaptureAndPoint(RealServerRig rig, string where = null)
        {
            int before = rig.Server.Submitted.Count;
            Assert.That(rig.Card.Capture(), Is.True, where);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Selecting), where);
            Assert.That(rig.Server.Submitted.Count, Is.EqualTo(before), where + ": nothing is asked before a point");
            Assert.That(rig.Card.AddPoint(new SpatialBlend.Freeform.Scanning.ScanPoint(30, 30)), Is.True, where);
            Assert.That(rig.Server.Submitted.Count, Is.EqualTo(before + 1), where + ": the point asks for its mask");
            Assert.That(rig.Card.Flow.Refining, Is.True, where);
        }

        [Test]
        public void Scan_WithAReachableServer_UsesTheServer_AndNothingIsCanned()
        {
            var rig = OpenRealServerCard();
            Assert.That(rig.Controller.Scan.FellBack, Is.False);
            Assert.That(rig.Controller.Runner.PracticeObjectShown, Is.False, "the user's own object: no practice-object note");
            CaptureAndPoint(rig);
            Assert.That(rig.Server.Submitted.Count, Is.EqualTo(1), "the mask request went to the server");
            Assert.That(rig.Server.Submitted[0].Points.Select(p => p.Label), Is.EqualTo(new[] { SpatialBlend.Freeform.Scanning.ScanPointLabel.Positive }), "the user's point alone, no box");
            Assert.That(rig.Controller.Scan.ActiveService, Is.SameAs(rig.Manager.Service));
            rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Segment, rig.Server.Handles[0], true, "Segment OK | box:1");
            rig.Controller.Runner.Abort();
        }

        [Test]
        public void Scan_TheServerFailsTheMask_FallsBackToCanned_AndTheCaptureCanBeRepeated()
        {
            var rig = OpenRealServerCard();
            CaptureAndPoint(rig);
            rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Segment, rig.Server.Handles[0], false, "no object found");
            rig.Manager.Service.Update(0.1);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Selecting), "a failed refinement keeps the photo");
            Assert.That(rig.Card.Flow.RefineError.IsError, Is.True);
            Assert.That(rig.Controller.Runner.ScanProblem, Is.Null, "a failing server is not shown as a failure: the tutorial moves to the canned one");
            Recover(rig.Controller);
            Assert.That(rig.Controller.Scan.FellBack, Is.True);
            Assert.That(rig.Controller.Scan.Mode, Is.EqualTo(TutorialScanMode.Canned));
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Aiming), "ready to capture again");
            Assert.That(rig.Card.FrameSource, Is.InstanceOf<TutorialHologramFrameSource>());
            Assert.That(rig.Controller.Props.Hologram, Is.Not.Null);
            Assert.That(rig.Controller.Runner.ScanProblem, Is.Null);
            int serverRequests = rig.Server.Submitted.Count;

            rig.Controller.Scan.Canned.SegmentDelaySeconds = 0.0;
            rig.Controller.Scan.Canned.DelaySeconds = 0.0;
            Assert.That(rig.Card.Capture(), Is.True);
            Assert.That(rig.Card.PromptWithBox, Is.False, "the practice server: points alone");
            Assert.That(rig.Card.AddPoint(VasePoint(rig.Controller.Scan.Canned.Segmenter)), Is.True);
            rig.Controller.Scan.Tick(0.05);
            rig.Controller.Scan.Tick(0.06);
            Assert.That(rig.Card.Flow.Mask, Is.Not.Null, "the canned server gave the vase's mask");
            Assert.That(rig.Server.Submitted.Count, Is.EqualTo(serverRequests), "no further request reached the real server");
            rig.Controller.Runner.Abort();
        }

        [Test]
        public void Scan_TheServerAcceptsTheJobThenTheResultIsUnusable_FallsBackToCanned_AndStepsBackToTheCapture()
        {
            var rig = OpenRealServerCard();
            var runner = rig.Controller.Runner;
            CaptureAndPoint(rig);
            // the server accepted the job and answers "ok", but its mask file is not there (the fake file system holds none): a failed result
            rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Segment, rig.Server.Handles[0], true, "Segment OK | box:1");
            rig.Manager.Service.Update(0.1);
            Assert.That(rig.Card.Flow.RefineError.IsError, Is.True);
            Recover(rig.Controller);
            Assert.That(rig.Controller.Scan.FellBack, Is.True, "any failure of the real path falls back");
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Aiming));
            Assert.That(runner.ScanProblem, Is.Null);
            Assert.That(runner.Flow.Current.Id, Is.EqualTo("3.1"), "still in the scan task: the user did not have to leave it");
            runner.Abort();
        }

        /// <summary>Drives the real path to a running reconstruction: capture, the server's mask, the build sent.</summary>
        private void RunRealPathToTheBuild(RealServerRig rig)
        {
            rig.Controller.Runner.Report(TutorialSignal.BoxCompleted);
            rig.Controller.Runner.Report(TutorialSignal.ReconstructChosen);
            CaptureAndPoint(rig);
            var segment = rig.Server.Submitted[0];
            rig.Files.Files[segment.OutputPath] = Enumerable.Repeat((byte)255, 64 * 64).ToArray();
            rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Segment, rig.Server.Handles[0], true, "Segment OK | box:1");
            rig.Manager.Service.Update(0.1);
            Assert.That(rig.Card.Flow.Mask, Is.Not.Null, rig.Card.Flow.Error.ToString());
            Poll(rig.Controller);
            Assert.That(rig.Controller.Runner.Flow.StepIndex, Is.EqualTo(3), "a mask alone is not the step");
            Assert.That(rig.Card.Confirm(), Is.True);
            Assert.That(rig.Card.PrimaryLabel, Is.EqualTo(ScanCardText.ThreeDCopy), "the model waits for the user's 3D copy");
            rig.Card.Flow.Build(0.2);   // the 3D copy button
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Building));
            Poll(rig.Controller);
            Assert.That(rig.Controller.Runner.Flow.StepIndex, Is.EqualTo(5), "the mask was covered, then the build was sent");
        }

        [Test]
        public void Scan_TheServerAcceptsTheBuildThenFails_FallsBackToCanned_AndTheCaptureStepIsNotCountedAgainByItself()
        {
            var rig = OpenRealServerCard();
            var runner = rig.Controller.Runner;
            RunRealPathToTheBuild(rig);
            rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Reconstruct, rig.Server.Handles[1], false, "the build failed");
            rig.Manager.Service.Update(0.3);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Failed));
            Poll(rig.Controller);   // a poll between the failure and the recovery must not matter
            Recover(rig.Controller);
            Assert.That(rig.Controller.Scan.FellBack, Is.True);
            Assert.That(runner.ScanProblem, Is.Null);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Aiming));
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(2), "back at the capture step");
            Assert.That(runner.PracticeObjectShown, Is.True, "the canned server: a practice object stands in");
            Assert.That(runner.Flow.Current.Steps[2].PracticeNote, Is.EqualTo(TutorialScript.PracticeObjectNote));
            Assert.That(runner.Flow.Current.Steps[2].Note, Is.Null, "never shown with a scan server");
            if (!runner.EditingMissing) Assert.That(runner.Hint, Is.EqualTo(TutorialScript.PracticeObjectNote));
            Poll(rig.Controller); Poll(rig.Controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(2), "the old capture is not counted for the user");
            rig.Controller.Scan.Canned.SegmentDelaySeconds = 0.0;
            Assert.That(rig.Card.Capture(), Is.True);
            Poll(rig.Controller);
            Assert.That(runner.Flow.StepIndex, Is.EqualTo(3), "the user's new Capture counts");
            runner.Abort();
        }

        [Test]
        public void Scan_TheServerNeverAnswersTheBuild_ItsOwnRequestTimeoutIsTheSignal_AndTheRunFallsBackToCanned()
        {
            var rig = OpenRealServerCard();
            RunRealPathToTheBuild(rig);
            // The request timeout is the service's own (config, ReconstructTimeoutSeconds): no timer of the tutorial's.
            rig.Manager.Service.Update(0.2 + 100000.0);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Failed));
            Recover(rig.Controller);
            Assert.That(rig.Controller.Scan.FellBack, Is.True);
            Assert.That(rig.Card.Flow.State, Is.EqualTo(SpatialBlend.Freeform.Scanning.ScanFlowState.Aiming));
            rig.Controller.Runner.Abort();
        }

        [Test]
        public void Scan_AUserCancelOnAHealthyServer_IsNotAServerFailure_AndDoesNotFallBack()
        {
            var rig = OpenRealServerCard();
            RunRealPathToTheBuild(rig);
            rig.Controller.Scan.CancelInFlight();   // what Try again and leaving the task do
            rig.Manager.Service.Update(0.3);
            Assert.That(Get(rig.Controller, "_fallbackWanted"), Is.EqualTo(false));
            Assert.That(rig.Controller.Scan.FellBack, Is.False, "the reachable server is still the one used");
            Assert.That(rig.Controller.Scan.Mode, Is.EqualTo(TutorialScanMode.Real));
            rig.Controller.Runner.Abort();
        }

        [Test]
        public void Scan_AnEmptyResultFromTheServer_IsAFailure_AndFallsBackToCanned()
        {
            var rig = OpenRealServerCard();
            string prefix = Path.Combine(Path.GetTempPath(), "empty-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix, new byte[0]);
            Set(rig.Controller, "_submitted", true);
            typeof(TutorialController).GetMethod("OnScanResult", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(rig.Controller, new object[] { new SpatialBlend.Freeform.Scanning.ScanReconstructionResult { OutputPrefix = prefix } });
            try { File.Delete(prefix + SpatialBlend.Freeform.Scanning.ScanResponseParser.ModelSuffix); } catch { /* temp */ }
            Assert.That(Get(rig.Controller, "_fallbackWanted"), Is.EqualTo(true), "an empty model is a failed scan");
            Recover(rig.Controller);
            Assert.That(rig.Controller.Scan.FellBack, Is.True);
            Assert.That(rig.Controller.CopyReady, Is.False);
            rig.Controller.Runner.Abort();
        }

        [Test]
        public void Skip_WorksInEveryCaptureSubStep_EvenWhileTheCardOrRingWaitsForTheServer()
        {
            // 1. aiming, 2. waiting for the mask (the server never answers), 3. failed card
            foreach (string where in new[] { "aiming", "waiting", "failed" })
            {
                var rig = OpenRealServerCard();
                var runner = rig.Controller.Runner;
                var cardObject = rig.Card.gameObject;
                if (where != "aiming")
                {
                    CaptureAndPoint(rig, where);
                    if (where == "failed")
                    {
                        rig.Server.Complete(SpatialBlend.Freeform.Scanning.ScanKind.Segment, rig.Server.Handles[0], false, "boom");
                        rig.Manager.Service.Update(0.1);
                    }
                    else Assert.That(rig.Card.Flow.Refining, Is.True);
                }
                Assert.That(runner.Flow.CanSkip, Is.True, where);
                Assert.That(runner.Skip(), Is.True, where);
                Assert.That(runner.Flow.Current.Id, Is.EqualTo("4.1"), where + ": on to the next task");
                Assert.That(cardObject == null, Is.True, where + ": the card is closed");
                Assert.That(rig.Manager.Service.PendingCount, Is.EqualTo(0), where + ": nothing is left in flight");
                runner.Abort();
            }
        }
    }
}
#endif
