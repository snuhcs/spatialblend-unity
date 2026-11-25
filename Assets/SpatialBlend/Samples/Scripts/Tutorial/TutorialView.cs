// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>What the card's frame-specific control does; set by the controller.</summary>
    public sealed class TutorialCardActions
    {
        /// <summary>The glowing practice button on the first task was pressed.</summary>
        public Action PressButton;
        public Action<bool> ShowAnchors;
        public Action FindRoom;
        /// <summary>"Try again" after a failed scan: back to pressing Capture on the box.</summary>
        public Action RetryScan;
        public Action<string> JoinSampleRoom;
        public Action HostSampleRoom;
    }

    /// <summary>
    /// The tutorial card: an overlay on the control panel's canvas (so it is carried on the left controller and reached by the
    /// same Interaction SDK ray and poke as the panel), built from the panel's own kit and the Graphite tokens. It covers the lobby
    /// panel while the tutorial runs. It holds no logic: the controller feeds it a <see cref="TutorialRunner"/> and the actions.
    /// </summary>
    public sealed class TutorialView : MonoBehaviour
    {
        public const string RootName = "TutorialCard";

        private TutorialRunner _runner;
        private TutorialCardActions _actions;
        private RectTransform _root;
        private TMP_Text _header, _chapter, _title, _subtitle, _notice, _hint, _problem, _attribution, _detail, _successText, _settingsCaption;
        private Image _successIcon, _glow;
        private RectTransform _successRow, _stepsRoot;
        private Image[] _stepIcons;
        private TMP_Text[] _stepLabels;
        private RectTransform[] _stepRows;
        private const int MaxSteps = 8;
        private Image[] _dots;
        private Button _back, _skip, _next, _exit;
        private TMP_Text _nextLabel;
        private ToggleRow _anchorsRow;
        private Button _action;
        private Image _busyBar;
        private StatusRow _status;
        private RectTransform _practiceSlot, _confirm, _statusCard, _sampleRooms;
        private Button _keep, _leave;
        private Button[] _sampleRoomButtons;
        private Button _sampleHost;
        private float _fitted = -1f;

        public RectTransform Root => _root;
        public Button Back => _back;
        public Button Skip => _skip;
        public Button Next => _next;
        public Button Exit => _exit;
        /// <summary>The bar row (Back, Skip this step, Next).</summary>
        public RectTransform BarRow => _root.Find("Bar") as RectTransform;
        public Button KeepGoing => _keep;
        public Button LeaveTutorial => _leave;
        public Button Action => _action;
        public string HintText => _hint.gameObject.activeSelf ? _hint.text : null;
        public string SuccessLine => _successRow.gameObject.activeSelf ? _successText.text : null;
        public bool GlowVisible => _glow != null && _glow.gameObject.activeInHierarchy;
        /// <summary>The checklist rows that are shown: their labels and whether each is ticked.</summary>
        public IReadOnlyList<(string label, bool done)> StepRows
        {
            get
            {
                var rows = new List<(string, bool)>();
                for (int i = 0; i < MaxSteps; i++)
                    if (_stepRows[i].gameObject.activeSelf) rows.Add((_stepLabels[i].text, _stepIcons[i].sprite == TutorialFeedback.CheckSprite));
                return rows;
            }
        }
        public ToggleRow AnchorsRow => _anchorsRow;
        public bool ConfirmVisible => _confirm != null && _confirm.gameObject.activeSelf;
        public string HeaderText => _header.text;
        public string TitleText => _title.text;
        public string SubtitleText => _subtitle.text;
        public string NextLabel => _nextLabel.text;
        public string NoticeText => _notice.gameObject.activeSelf ? _notice.text : null;
        public string ProblemText => _problem.gameObject.activeSelf ? _problem.text : null;
        public int DotsOn { get; private set; }

        /// <summary>Builds the card over <paramref name="canvas"/> (the control panel's canvas) and hides it until <see cref="Bind"/>.</summary>
        public static TutorialView Create(RectTransform canvas)
        {
            var p = UiTheme.Palette;
            var rect = ControlPanelKit.Stack(RootName, canvas, UiTheme.Space3);
            rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 1f);
            var layout = rect.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 22, 18);
            layout.childAlignment = TextAnchor.UpperCenter;
            // Opaque, so the lobby panel below does not show through.
            var surface = ControlPanelKit.AddSurface(rect, UiTheme.RadiusPanel, p.PanelBottom, 2, true, null, true);
            surface.GetComponent<UiVerticalGradient>().Set(new Color(p.PanelTop.r, p.PanelTop.g, p.PanelTop.b, 1f),
                new Color(p.PanelBottom.r, p.PanelBottom.g, p.PanelBottom.b, 1f));

            var view = rect.gameObject.AddComponent<TutorialView>();
            view._root = rect;
            view.Build();
            foreach (var t in rect.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = canvas.gameObject.layer;
            rect.gameObject.SetActive(false);
            return view;
        }

        private static LayoutElement Size(Component c, float min, float? preferred = null, float? flexible = null)
        {
            var le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            le.minHeight = min;
            if (preferred.HasValue) le.preferredHeight = preferred.Value;
            if (flexible.HasValue) le.flexibleHeight = flexible.Value;
            return le;
        }

        private void Build()
        {
            var p = UiTheme.Palette;
            var root = _root;

            // The product title (the single constant) heads the card, as it heads the lobby panel.
            _header = ControlPanelKit.Label(root, UiTheme.ProductTitle + "  \u00B7  Tutorial", UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextDisabled, true);
            Size(_header, 28f);
            _chapter = ControlPanelKit.Label(root, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, true);
            Size(_chapter, 30f);
            _title = ControlPanelKit.Label(root, string.Empty, UiTheme.FontTitle, TextAlignmentOptions.Left, p.Text, true, true);
            Size(_title, 44f);
            BuildChecklist(root);

            // The task's own control: the practice button, Show anchors under a Settings caption, the room buttons, or the demo status.
            _practiceSlot = ControlPanelKit.Stack("PracticeSlot", root, UiTheme.Space3);
            Size(_practiceSlot, 0f);
            _settingsCaption = ControlPanelKit.Label(_practiceSlot, "Settings", UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, true);
            Size(_settingsCaption, 30f);
            _anchorsRow = ControlPanelKit.AddToggleRow(_practiceSlot, "Show anchors", false, v => { if (_actions != null && _actions.ShowAnchors != null) _actions.ShowAnchors(v); });
            _action = ControlPanelKit.AddButtonRow(_practiceSlot, "Action", () => OnActionPressed(), true);
            BuildGlow();
            // The sample rooms (when the real list cannot be read) sit in one row, so the card stays inside the panel's input surface.
            _sampleRooms = ControlPanelKit.NewRect("SampleRooms", _practiceSlot);
            Size(_sampleRooms, ControlPanelKit.RowHeight, ControlPanelKit.RowHeight);
            var roomsRow = _sampleRooms.gameObject.AddComponent<HorizontalLayoutGroup>();
            roomsRow.spacing = UiTheme.Space3;
            roomsRow.childControlWidth = true;
            roomsRow.childControlHeight = true;
            roomsRow.childForceExpandWidth = true;
            roomsRow.childForceExpandHeight = true;
            _sampleRoomButtons = new Button[TutorialSimulatedRoom.SampleRooms.Count];
            for (int i = 0; i < _sampleRoomButtons.Length; i++)
            {
                string room = TutorialSimulatedRoom.SampleRooms[i];
                _sampleRoomButtons[i] = ControlPanelKit.AddButtonRow(_sampleRooms, room, () => { if (_actions != null && _actions.JoinSampleRoom != null) _actions.JoinSampleRoom(room); }, false);
            }
            _sampleHost = ControlPanelKit.AddButtonRow(_sampleRooms, "New room", () => { if (_actions != null && _actions.HostSampleRoom != null) _actions.HostSampleRoom(); }, true);

            _statusCard = ControlPanelKit.Stack("DemoStatus", _practiceSlot, 2f);
            _statusCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 14, 16);
            ControlPanelKit.AddSurface(_statusCard, UiTheme.RadiusCard, p.Surface, 1, true);
            _status = ControlPanelKit.AddStatusRow(_statusCard, "Not in a room", UiTheme.FontBody, true);
            _detail = ControlPanelKit.Label(_statusCard, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, false, true);
            var barRect = ControlPanelKit.NewRect("BusyBar", _statusCard);
            var barLayout = barRect.gameObject.AddComponent<LayoutElement>();
            barLayout.minHeight = barLayout.preferredHeight = 6f;
            _busyBar = barRect.gameObject.AddComponent<Image>();
            _busyBar.color = p.Accent;
            _busyBar.type = Image.Type.Filled;
            _busyBar.fillMethod = Image.FillMethod.Horizontal;
            _busyBar.raycastTarget = false;

            _subtitle = ControlPanelKit.Label(root, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.TextMuted, false, true);
            _subtitle.fontStyle = FontStyles.Italic;
            Size(_subtitle, 64f);
            BuildSuccess(root);
            _hint = ControlPanelKit.Label(root, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.Accent, false, true);
            _notice = ControlPanelKit.Label(root, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.Warning, true, true);
            _problem = ControlPanelKit.Label(root, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.Error, true, true);

            // Chapter dots.
            var dotRow = ControlPanelKit.NewRect("ChapterDots", root);
            Size(dotRow, 20f, 20f);
            var dotLayout = dotRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            dotLayout.spacing = UiTheme.Space3;
            dotLayout.childAlignment = TextAnchor.MiddleCenter;
            dotLayout.childControlWidth = false;
            dotLayout.childControlHeight = false;
            dotLayout.childForceExpandWidth = false;
            dotLayout.childForceExpandHeight = false;
            _dots = new Image[TutorialScript.ChapterNames.Length];
            for (int i = 0; i < _dots.Length; i++)
            {
                var dot = ControlPanelKit.NewRect("Dot" + (i + 1), dotRow);
                dot.sizeDelta = new Vector2(16f, 16f);
                _dots[i] = dot.gameObject.AddComponent<Image>();
                _dots[i].raycastTarget = false;
                UiSprites.Apply(_dots[i], UiSprites.RoundedRect(8f));
            }

            // Anything left over in a tall card goes to this spacer, never to the bar.
            var gap = ControlPanelKit.NewRect("FlexibleGap", root);
            var gapLayout = gap.gameObject.AddComponent<LayoutElement>();
            gapLayout.minHeight = 0f;
            gapLayout.flexibleHeight = 1f;

            // The per-frame bar, one row of the theme's row height across the card: Back on the left, Skip this step and the primary
            // (Next / Start / Finish) on the right. X is not in the bar; it sits in the card's top-right corner.
            var bar = ControlPanelKit.NewRect("Bar", root);
            var barRowLayout = bar.gameObject.AddComponent<LayoutElement>();
            barRowLayout.minHeight = barRowLayout.preferredHeight = ControlPanelKit.RowHeight;
            barRowLayout.flexibleHeight = 0f;
            var barGroup = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            barGroup.spacing = UiTheme.Space3;
            barGroup.childAlignment = TextAnchor.MiddleLeft;
            barGroup.childControlWidth = true;
            barGroup.childControlHeight = true;
            barGroup.childForceExpandWidth = false;
            barGroup.childForceExpandHeight = false;
            _back = Bar(bar, "Back", () => _runner?.Back(), false, 150f);
            var spacer = ControlPanelKit.NewRect("Spacer", bar);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            _skip = Bar(bar, "Skip this step", () => _runner?.Skip(), false, 230f);
            _next = Bar(bar, "Next", () => _runner?.Next(), true, 170f);
            _nextLabel = _next.GetComponentInChildren<TMP_Text>(true);

            // X: top-right corner of the card, outside the layout, with the row height as its hit target.
            _exit = ControlPanelKit.AddButtonRow(root, "X", () => _runner?.RequestExit(), false);
            var exitRect = (RectTransform)_exit.transform;
            var exitLayout = _exit.GetComponent<LayoutElement>();
            exitLayout.ignoreLayout = true;
            exitRect.anchorMin = exitRect.anchorMax = exitRect.pivot = new Vector2(1f, 1f);
            exitRect.anchoredPosition = new Vector2(-UiTheme.Space4, -UiTheme.Space3);
            exitRect.sizeDelta = new Vector2(ControlPanelKit.RowHeight, ControlPanelKit.RowHeight);

            if (TutorialAssets.HasNarration)
            {
                _attribution = ControlPanelKit.Label(root, TutorialScript.AttributionLine, UiTheme.FontFine, TextAlignmentOptions.Left, p.TextDisabled);
                Size(_attribution, 24f);
            }

            BuildConfirm();
        }

        private void BuildChecklist(RectTransform root)
        {
            var p = UiTheme.Palette;
            _stepsRoot = ControlPanelKit.Stack("Steps", root, UiTheme.Space2);
            Size(_stepsRoot, 0f);
            _stepRows = new RectTransform[MaxSteps];
            _stepIcons = new Image[MaxSteps];
            _stepLabels = new TMP_Text[MaxSteps];
            for (int i = 0; i < MaxSteps; i++)
            {
                var row = ControlPanelKit.NewRect("Step" + (i + 1), _stepsRoot);
                Size(row, 34f, 34f);
                var group = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                group.spacing = UiTheme.Space3;
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                var icon = ControlPanelKit.NewRect("Icon", row);
                var iconLayout = icon.gameObject.AddComponent<LayoutElement>();
                iconLayout.minWidth = iconLayout.preferredWidth = 28f;
                iconLayout.minHeight = iconLayout.preferredHeight = 28f;
                var image = icon.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
                var label = ControlPanelKit.Label(row, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Text, false, true);
                var labelLayout = label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>();
                labelLayout.flexibleWidth = 1f;
                _stepRows[i] = row;
                _stepIcons[i] = image;
                _stepLabels[i] = label;
            }
        }

        private void BuildSuccess(RectTransform root)
        {
            var p = UiTheme.Palette;
            _successRow = ControlPanelKit.NewRect("Success", root);
            Size(_successRow, 44f, 44f);
            var group = _successRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = UiTheme.Space3;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var icon = ControlPanelKit.NewRect("CheckIcon", _successRow);
            var iconLayout = icon.gameObject.AddComponent<LayoutElement>();
            iconLayout.minWidth = iconLayout.preferredWidth = 38f;
            iconLayout.minHeight = iconLayout.preferredHeight = 38f;
            _successIcon = icon.gameObject.AddComponent<Image>();
            _successIcon.raycastTarget = false;
            _successIcon.sprite = TutorialFeedback.CheckSprite;
            _successIcon.color = p.Success;
            _successText = ControlPanelKit.Label(_successRow, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Success, true, true);
            var textLayout = _successText.GetComponent<LayoutElement>() ?? _successText.gameObject.AddComponent<LayoutElement>();
            textLayout.flexibleWidth = 1f;
            _successRow.gameObject.SetActive(false);
        }

        // A soft accent halo around the practice button, pulsed in Tick.
        private void BuildGlow()
        {
            var halo = ControlPanelKit.NewRect("Glow", (RectTransform)_action.transform);
            halo.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            halo.anchorMin = Vector2.zero;
            halo.anchorMax = Vector2.one;
            halo.offsetMin = new Vector2(-10f, -10f);
            halo.offsetMax = new Vector2(10f, 10f);
            _glow = halo.gameObject.AddComponent<Image>();
            _glow.raycastTarget = false;
            UiSprites.Apply(_glow, UiSprites.Ring(UiTheme.RadiusCard + 8f, 5f));
            _glow.color = UiTheme.Palette.Accent;
            halo.SetAsFirstSibling();
        }

        private static Button Bar(RectTransform parent, string label, Action onClick, bool primary, float width)
        {
            var button = ControlPanelKit.AddButtonRow(parent, label, onClick, primary);
            var le = button.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = width;
            le.flexibleWidth = 0f;
            le.flexibleHeight = 0f;
            return button;
        }

        private void BuildConfirm()
        {
            var p = UiTheme.Palette;
            _confirm = ControlPanelKit.Stack("ConfirmExit", _root, UiTheme.Space4);
            _confirm.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _confirm.anchorMin = Vector2.zero;
            _confirm.anchorMax = Vector2.one;
            _confirm.offsetMin = _confirm.offsetMax = Vector2.zero;
            var group = _confirm.GetComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(40, 40, 40, 40);
            group.childAlignment = TextAnchor.MiddleCenter;
            var fill = ControlPanelKit.AddSurface(_confirm, UiTheme.RadiusPanel, p.PanelBottom, 3, true, null, true);
            fill.GetComponent<UiVerticalGradient>().Set(new Color(p.PanelTop.r, p.PanelTop.g, p.PanelTop.b, 1f), new Color(p.PanelBottom.r, p.PanelBottom.g, p.PanelBottom.b, 1f));
            ControlPanelKit.Label(_confirm, "Leave the tutorial?", UiTheme.FontTitle, TextAlignmentOptions.Center, p.Text, true, true);
            ControlPanelKit.Label(_confirm, "You can start it again from the lobby.", UiTheme.FontBody, TextAlignmentOptions.Center, p.TextMuted, false, true);
            _keep = ControlPanelKit.AddButtonRow(_confirm, "Keep going", () => _runner?.CancelExit(), true);
            _leave = ControlPanelKit.AddButtonRow(_confirm, "Leave", () => _runner?.ConfirmExit(), false);
            _confirm.gameObject.SetActive(false);
        }

        private void OnActionPressed()
        {
            if (_runner == null || _actions == null) return;
            switch (_runner.Flow.Current.Id)
            {
                case "1.1":
                    if (_actions.PressButton != null) _actions.PressButton();
                    break;
                case "3.1":
                    if (_actions.RetryScan != null) _actions.RetryScan();
                    break;
                case "4.1":
                case "4.2":
                    if (_actions.FindRoom != null) _actions.FindRoom();
                    break;
            }
        }

        public void Bind(TutorialRunner runner, TutorialCardActions actions)
        {
            if (_runner != null) _runner.StateChanged -= Refresh;
            _runner = runner;
            _actions = actions;
            if (_runner != null) _runner.StateChanged += Refresh;
            gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Refresh();
        }

        public void Unbind()
        {
            if (_runner != null) _runner.StateChanged -= Refresh;
            _runner = null;
            if (this != null) gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_runner != null) _runner.StateChanged -= Refresh;
        }

        /// <summary>Redraws the card from the runner's state.</summary>
        public void Refresh()
        {
            if (_runner == null) return;
            var flow = _runner.Flow;
            var frame = flow.Current;
            var p = UiTheme.Palette;

            _chapter.text = "Chapter " + frame.Chapter + " of " + TutorialScript.ChapterNames.Length + "   " + TutorialScript.ChapterNames[frame.Chapter - 1];
            _title.text = frame.TaskLine;
            _subtitle.text = _runner.Narrator.Subtitle;

            RefreshChecklist(frame, flow);
            bool done = flow.ActionDone;
            _successRow.gameObject.SetActive(done && !string.IsNullOrEmpty(frame.SuccessText));
            _successText.text = frame.SuccessText ?? string.Empty;

            string hint = _runner.Hint;
            _hint.gameObject.SetActive(!string.IsNullOrEmpty(hint));
            _hint.text = hint ?? string.Empty;
            string notice = _runner.Notice ?? _runner.RoomSetupNotice ?? _runner.DemoChoiceNotice;
            _notice.gameObject.SetActive(!string.IsNullOrEmpty(notice));
            _notice.text = notice ?? string.Empty;
            string problem = _runner.ScanProblem;
            _problem.gameObject.SetActive(!string.IsNullOrEmpty(problem));
            _problem.text = problem ?? string.Empty;

            DotsOn = frame.Chapter;
            for (int i = 0; i < _dots.Length; i++)
            {
                bool past = i + 1 < frame.Chapter, current = i + 1 == frame.Chapter;
                _dots[i].color = current ? p.Accent : past ? new Color(p.Accent.r, p.Accent.g, p.Accent.b, 0.55f) : p.Track;
                _dots[i].rectTransform.sizeDelta = current ? new Vector2(20f, 20f) : new Vector2(14f, 14f);
            }

            _back.interactable = flow.CanBack;
            _skip.gameObject.SetActive(!frame.InfoOnly && flow.CanSkip);
            _next.interactable = flow.CanNext;
            ControlPanelKit.SetButtonLabel(_next, flow.IsLast ? "Finish" : "Next");
            UiInteractableTint.For(_next).Refresh();
            UiInteractableTint.For(_back).Refresh();
            _exit.interactable = !flow.ExitPending;
            _confirm.gameObject.SetActive(flow.ExitPending);
            if (flow.ExitPending) _confirm.SetAsLastSibling();

            RefreshPracticeSlot(frame);
            Fit();
        }

        // The checklist shows when a task has more than one step: ticked rows are done, the first unticked one is current.
        private void RefreshChecklist(TutorialFrame frame, TutorialFlow flow)
        {
            var p = UiTheme.Palette;
            bool show = frame.Steps.Length > 1;
            _stepsRoot.gameObject.SetActive(show);
            for (int i = 0; i < MaxSteps; i++)
            {
                bool used = show && i < frame.Steps.Length;
                _stepRows[i].gameObject.SetActive(used);
                if (!used) continue;
                bool stepDone = i < flow.StepIndex || flow.ActionDone;
                bool current = !stepDone && i == flow.StepIndex;
                _stepLabels[i].text = frame.Steps[i].Label;
                _stepLabels[i].color = stepDone ? p.TextMuted : current ? p.Text : p.TextDisabled;
                _stepIcons[i].sprite = stepDone ? TutorialFeedback.CheckSprite : UiSprites.Ring(14f, 3f);
                _stepIcons[i].color = stepDone ? p.Success : current ? p.Accent : p.Track;
            }
        }

        private void RefreshPracticeSlot(TutorialFrame frame)
        {
            bool done = _runner.Flow.ActionDone;
            bool showAnchors = frame.Id == "2.1";
            string actionLabel = null;
            bool showStatus = frame.Id == "4.2" && _runner.Room.State != SimulatedRoomState.Browsing || (frame.Id == "3.1" && _runner.ScanInFlight && !done);
            bool showRooms = false;
            switch (frame.Id)
            {
                case "1.1": actionLabel = "Press me"; break;
                case "3.1": actionLabel = _runner.ScanProblem != null ? "Try again" : null; break;
                case "4.1": actionLabel = "Find a room"; break;
                case "4.2": actionLabel = _runner.RoomListShown || done ? null : "Find a room"; break;
            }
            if (frame.Id == "4.1" || frame.Id == "4.2")
            {
                showRooms = _runner.RoomListShown && _runner.RoomListIsSample && !(frame.Id == "4.2" && done);
                if (showRooms) actionLabel = null;   // the rooms replace the Find a room button
                if (frame.Id == "4.1" && _runner.RoomListShown) actionLabel = null;
            }
            _settingsCaption.gameObject.SetActive(showAnchors);
            _anchorsRow.Toggle.gameObject.SetActive(showAnchors);
            _action.gameObject.SetActive(actionLabel != null);
            if (actionLabel != null) ControlPanelKit.SetButtonLabel(_action, actionLabel);
            _glow.gameObject.SetActive(actionLabel != null && frame.Id == "1.1" && !done);
            _sampleRooms.gameObject.SetActive(showRooms);
            _statusCard.gameObject.SetActive(showStatus);
            _practiceSlot.gameObject.SetActive(showAnchors || actionLabel != null || showRooms || showStatus);
            if (showStatus) RefreshStatus();
        }

        private void RefreshStatus()
        {
            var runner = _runner;
            if (runner.Flow.Current.Id == "3.1")
            {
                _status.Set("Building your 3D copy...", StatusTone.Busy);
                _detail.text = "This takes a moment.";
                _detail.gameObject.SetActive(true);
                _busyBar.gameObject.SetActive(true);
                _busyBar.fillAmount = Mathf.PingPong(Time.realtimeSinceStartup * 0.8f, 1f);
                return;
            }
            var room = runner.Room;
            var tone = room.State == SimulatedRoomState.Connected ? StatusTone.Good : room.State == SimulatedRoomState.Connecting ? StatusTone.Busy : StatusTone.Neutral;
            _status.Set(room.Headline, tone);
            _detail.text = room.Detail;
            _detail.gameObject.SetActive(!string.IsNullOrEmpty(room.Detail));
            _busyBar.gameObject.SetActive(room.State == SimulatedRoomState.Connecting);
            _busyBar.fillAmount = (float)room.Progress;
        }

        /// <summary>Call every frame: the demo's busy bar and the subtitle follow the clock.</summary>
        public void Tick()
        {
            if (_runner == null) return;
            if (_statusCard.gameObject.activeInHierarchy) RefreshStatus();
            if (_glow != null && _glow.gameObject.activeInHierarchy)
            {
                var c = UiTheme.Palette.Accent;
                _glow.color = new Color(c.r, c.g, c.b, 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 3.2f)));
            }
            string subtitle = _runner.Narrator.Subtitle;
            if (_subtitle.text != subtitle) _subtitle.text = subtitle;
        }

        // The card covers the canvas and grows downward when its content is taller (a notice, a long line), as the room list does.
        private void Fit()
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
            if (_root.parent is not RectTransform parent) return;
            float extra = LayoutUtility.GetPreferredHeight(_root) - parent.rect.height;
            _root.offsetMin = new Vector2(0f, Mathf.Min(0f, -extra));
            _fitted = extra;
        }
    }
}
