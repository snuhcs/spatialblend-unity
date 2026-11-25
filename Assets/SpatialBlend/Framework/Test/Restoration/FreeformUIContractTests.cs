// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend;
using SpatialBlend.Interactions;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using FusionBootstrap = Fusion.FusionBootstrap;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformUIContractTests
    {
        [Test]
        public void ControlPanel_ChangesCanvasOnlyAfterCompatibleManagerReadiness()
        {
            var managerObject = new GameObject("manager");
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            var panelObject = new GameObject("control");
            panelObject.SetActive(false);
            var controlCanvas = new GameObject("control-canvas");
            var modeCanvas = new GameObject("mode-canvas");
            var panel = panelObject.AddComponent<ControlPanel>();
            try
            {
                SetField(panel, "_spaceManager", manager);
                SetField(panel, "_ControlPanelCanvas", controlCanvas);
                SetField(panel, "_InteractionModeCanvas", modeCanvas);
                Invoke(panel, "OnEnable");
                Invoke(panel, "Start");

                PrepareManagerReadiness(manager, false, false);
                RaiseManagerState(manager, SpatialBlendConnectionState.Disconnected);
                Assert.That(controlCanvas.activeSelf, Is.True);
                Assert.That(modeCanvas.activeSelf, Is.False);

                PrepareManagerReadiness(manager, false, false);
                RaiseManagerState(manager, SpatialBlendConnectionState.Connecting);
                Assert.That(controlCanvas.activeSelf, Is.True,
                    "Connecting is insufficient until the verified v2 library is compatible.");
                Assert.That(modeCanvas.activeSelf, Is.False);

                PrepareManagerReadiness(manager, true, false);
                RaiseManagerState(manager, SpatialBlendConnectionState.Connected);
                Assert.That(controlCanvas.activeSelf, Is.True,
                    "Connected is insufficient until the verified v2 library is compatible.");
                Assert.That(modeCanvas.activeSelf, Is.False);

                PrepareManagerReadiness(manager, false, false);
                RaiseManagerState(manager, SpatialBlendConnectionState.Failed);
                Assert.That(controlCanvas.activeSelf, Is.True);

                PrepareManagerReadiness(manager, true, true);
                RaiseManagerState(manager, SpatialBlendConnectionState.Connected);
                // The control panel stays visible in a room (it is on the left controller and
                // does not block the view); the mode panel is shown as well.
                Assert.That(controlCanvas.activeInHierarchy, Is.True);
                Assert.That(modeCanvas.activeInHierarchy, Is.True);

                PrepareManagerReadiness(manager, false, false);
                RaiseManagerState(manager, SpatialBlendConnectionState.Failed);
                Assert.That(controlCanvas.activeSelf, Is.True,
                    "Failure must restore retry UI rather than hide it.");
                Assert.That(modeCanvas.activeSelf, Is.False);
            }
            finally
            {
                Invoke(panel, "OnDisable");
                UnityEngine.Object.DestroyImmediate(panelObject);
                UnityEngine.Object.DestroyImmediate(controlCanvas);
                UnityEngine.Object.DestroyImmediate(modeCanvas);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void InteractionModePanel_TogglesAndRuntimeModeStayInSync()
        {
            var panelObject = new GameObject("mode-panel");
            panelObject.SetActive(false);
            var sceneToggle = new GameObject("scene").AddComponent<Toggle>();
            var interactionToggle = new GameObject("interaction").AddComponent<Toggle>();
            var existingRuntime = InteractionModeManager.Instance;
            var interactionObject = existingRuntime == null
                ? new GameObject("interaction-manager") : null;
            var runtime = existingRuntime != null
                ? existingRuntime : interactionObject.AddComponent<InteractionModeManager>();
            if (existingRuntime == null)
            {
                Invoke(runtime, "Awake");
            }
            var ui = panelObject.AddComponent<InteractionModeUIManager>();
            InteractionMode previousMode = runtime.CurrentMode;
            try
            {
                SetField(ui, "_sceneEditModeToggle", sceneToggle);
                SetField(ui, "_interactionModeToggle", interactionToggle);
                Invoke(ui, "OnEnable");
                Invoke(ui, "OnEnable");
                Assert.That((bool)GetField(ui, "_isSubscribed"), Is.True);

                runtime.SetMode(InteractionMode.Interaction);
                Assert.That(interactionToggle.isOn, Is.True);
                Assert.That(sceneToggle.isOn, Is.False);

                sceneToggle.isOn = true;
                Assert.That(runtime.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));

                Invoke(ui, "OnDisable");
                runtime.SetMode(InteractionMode.Interaction);
                Assert.That(sceneToggle.isOn, Is.True,
                    "A disabled mode panel must not retain a runtime listener.");
            }
            finally
            {
                Invoke(ui, "OnDisable");
                runtime.SetMode(previousMode);
                UnityEngine.Object.DestroyImmediate(sceneToggle.gameObject);
                UnityEngine.Object.DestroyImmediate(interactionToggle.gameObject);
                UnityEngine.Object.DestroyImmediate(panelObject);
                if (interactionObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(interactionObject);
                }
            }
        }

        [Test]
        public void SerializedAssets_KeepOriginalModePanelGuidsAndSceneHandoff()
        {
            const string prefabPath = "Assets/SpatialBlend/Samples/Prefabs/UI/InteractionModePanel.prefab";
            var modePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(modePrefab, Is.Not.Null);
            Assert.That(modePrefab.GetComponentsInChildren<MonoBehaviour>(true).All(value => value != null),
                Is.True, "Every restored mode-panel script reference must resolve.");
            string scene = File.ReadAllText(
                "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity");
            StringAssert.Contains("guid: ea1f261c3c83b8d4b80b2a2b84a1e852", scene);
            StringAssert.Contains("propertyPath: _InteractionModeCanvas", scene);
            StringAssert.Contains("objectReference: {fileID: 651177875}", scene);

            Scene opened = EditorSceneManager.OpenScene(
                "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity", OpenSceneMode.Additive);
            try
            {
                var panels = opened.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<ControlPanel>(true)).ToArray();
                Assert.That(panels.Count(panel => panel.enabled), Is.EqualTo(1),
                    "Only the prefab-backed controller may be active.");
                var panel = panels.Single(candidate => candidate.enabled);
                var serialized = new SerializedObject(panel);
                var controlCanvas = serialized.FindProperty("_ControlPanelCanvas").objectReferenceValue
                    as GameObject;
                Assert.That(controlCanvas, Is.Not.Null);
                Assert.That(panel.transform.IsChildOf(controlCanvas.transform), Is.False,
                    "Hiding the retry canvas must not disable its state listener.");
                Assert.That(serialized.FindProperty("_InteractionModeCanvas").objectReferenceValue,
                    Is.Not.Null);
                Assert.That(serialized.FindProperty("_fusionConnectionManager").objectReferenceValue,
                    Is.InstanceOf<SpatialBlend.Samples.FusionConnectionManager>());

                var networkEvents = opened.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Where(behaviour => behaviour != null)
                    .Select(behaviour => new { behaviour, serialized = new SerializedObject(behaviour) })
                    .Single(candidate => candidate.serialized.FindProperty("PlayerJoined") != null);
                var events = networkEvents.serialized;
                AssertNetworkEvent(events, "PlayerJoined", "OnPlayerJoined", 0);
                AssertNetworkEvent(events, "PlayerLeft", "OnPlayerLeft", 0);
                AssertNetworkEvent(events, "OnShutdown", "ShutdownConnection", 1);
            }
            finally
            {
                EditorSceneManager.CloseScene(opened, true);
            }
        }

        // ---- Session section presenter ----

        // The panel carries no developer information. Headline and Detail are plain words; the link
        // state, carrier and library text stay on the view as diagnostics (shown only in a development build, and logged).
        [Test]
        public void SessionStatus_DisconnectedOffersTheRoomList()
        {
            var view = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Disconnected,
                Role = CoreParticipantRole.None,
                LibraryCompatible = true,
            });
            Assert.That(view.Headline, Is.EqualTo("Not in a room"));
            Assert.That(view.Detail, Is.Empty);
            Assert.That(view.TutorialVisible, Is.True, "the lobby offers the tutorial");
            Assert.That(view.ConnectLabel, Is.EqualTo("Find a room"));
            Assert.That(view.ConnectEnabled, Is.True);
            Assert.That(view.Busy, Is.False);
            Assert.That(view.Tone, Is.EqualTo(StatusTone.Neutral));
            Assert.That(view.LinkStateText, Is.EqualTo("Core link: disconnected"), "diagnostics keep the technical text");
        }

        [Test]
        public void SessionStatus_InARoomSaysWhereAndWithWhomAndNeverTheRole()
        {
            var host = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Connected,
                Role = CoreParticipantRole.Host,
                LibraryCompatible = true,
                RoomName = "Room 3",
                PeopleCount = 2,
            });
            Assert.That(host.Headline, Is.EqualTo("In Room 3 · 2 people"));
            Assert.That(host.Tone, Is.EqualTo(StatusTone.Good));
            Assert.That(host.ConnectVisible, Is.False, "nothing to start while in a room");
            Assert.That(host.LeaveVisible, Is.True, "Leave room replaces it");
            var client = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Connected,
                Role = CoreParticipantRole.Client,
                LibraryCompatible = true,
            });
            Assert.That(client.Headline, Is.EqualTo("In a room"), "no room name known");
            foreach (var text in new[] { host.Headline, host.Detail, client.Headline, client.Detail })
            {
                StringAssert.DoesNotContain("Host", text);
                StringAssert.DoesNotContain("Core", text);
                StringAssert.DoesNotContain("v2", text);
            }
            var alone = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Connected, LibraryCompatible = true, RoomName = "Studio", PeopleCount = 1,
            });
            Assert.That(alone.Headline, Is.EqualTo("In Studio · just you"));
        }

        [Test]
        public void SessionStatus_ConnectingIsBusy()
        {
            var view = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Connecting,
                Role = CoreParticipantRole.Client,
                LibraryCompatible = true,
            });
            Assert.That(view.Busy, Is.True);
            Assert.That(view.Tone, Is.EqualTo(StatusTone.Busy));
            Assert.That(view.Headline, Is.EqualTo("Connecting…"));
            Assert.That(view.ConnectEnabled, Is.False);
            Assert.That(view.LinkStateText, Is.EqualTo("Core link: connecting"), "diagnostics");
        }

        [Test]
        public void SessionStatus_FailedIsVisibleWithOneSentenceAndAnActionAndOffersRetryOnlyWhenPossible()
        {
            var retry = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Failed,
                Role = CoreParticipantRole.Client,
                CanRetry = true,
                LibraryCompatible = true,
            });
            Assert.That(retry.Tone, Is.EqualTo(StatusTone.Bad));
            Assert.That(retry.Headline, Is.EqualTo("Couldn't connect"));
            Assert.That(retry.Detail, Is.EqualTo("Check your Wi-Fi and try again."));
            Assert.That(retry.ConnectLabel, Is.EqualTo("Try again"));
            Assert.That(retry.ConnectEnabled, Is.True);

            var noRunner = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LinkState = SpatialBlendConnectionState.Failed,
                CanRetry = false,
                LibraryCompatible = true,
            });
            Assert.That(noRunner.ConnectLabel, Is.EqualTo("Find a room"));
        }

        [Test]
        public void SessionStatus_AnIncompatibleLibraryBlocksWithAClearMessageAndDisablesConnect()
        {
            foreach (var state in new[] { SpatialBlendConnectionState.Disconnected, SpatialBlendConnectionState.Connecting,
                                          SpatialBlendConnectionState.Connected, SpatialBlendConnectionState.Failed })
            {
                var view = SessionStatusPresenter.Build(new SessionStatusInput { LinkState = state, LibraryCompatible = false });
                Assert.That(view.Headline, Is.EqualTo("This app needs an update"), state.ToString());
                Assert.That(view.Detail, Is.EqualTo(SessionStatusPresenter.NeedsUpdateDetail));
                Assert.That(view.Tone, Is.EqualTo(StatusTone.Bad));
                Assert.That(view.Blocked, Is.True);
                Assert.That(view.ConnectEnabled, Is.False);
                StringAssert.DoesNotContain("library", view.Headline.ToLowerInvariant() + view.Detail.ToLowerInvariant());
                Assert.That(view.LibraryText, Does.Contain("incompatible"), "the technical detail is kept for the log and diagnostics");
                Assert.That(view.DiagnosticsLine, Does.Contain("missing or incompatible"));
            }
        }

        [Test]
        public void SessionStatus_FoundationLocalIsPlainAndHasNoCoreLink()
        {
            var idle = SessionStatusPresenter.Build(new SessionStatusInput { LocalMode = true });
            Assert.That(idle.Headline, Is.EqualTo("Local content"));
            Assert.That(idle.ConnectLabel, Is.EqualTo("Start"));
            Assert.That(idle.ConnectEnabled, Is.True);
            var running = SessionStatusPresenter.Build(new SessionStatusInput
            {
                LocalMode = true,
                LocalRunning = true,
            });
            Assert.That(running.ConnectEnabled, Is.False);
            Assert.That(running.Tone, Is.EqualTo(StatusTone.Good));
        }

        // ---- Avatar presenter ----

        [Test]
        public void AvatarPresenter_ZoneToggleWritesTheModeAndRefreshesEveryTime()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            int refreshes = 0;
            try
            {
                var presenter = new AvatarSettingsPresenter(() => settings, () => refreshes++);
                Assert.That(presenter.ZoneBased, Is.False);
                Assert.That(presenter.SetZoneBased(true), Is.True);
                Assert.That(settings.avatarDisplayMode, Is.EqualTo(AvatarDisplayMode.ZoneBased));
                Assert.That(refreshes, Is.EqualTo(1));
                Assert.That(presenter.SetZoneBased(false), Is.True);
                Assert.That(settings.avatarDisplayMode, Is.EqualTo(AvatarDisplayMode.Default));
                Assert.That(refreshes, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AvatarPresenter_FadeIsClampedWrittenAndRefreshed()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            int refreshes = 0;
            try
            {
                var presenter = new AvatarSettingsPresenter(() => settings, () => refreshes++);
                presenter.SetFadeSeconds(0.5f);
                Assert.That(settings.fadeAvatarDuration, Is.EqualTo(0.5f));
                presenter.SetFadeSeconds(9f);
                Assert.That(settings.fadeAvatarDuration, Is.EqualTo(AvatarSettingsPresenter.MaxFadeSeconds));
                presenter.SetFadeSeconds(-1f);
                Assert.That(settings.fadeAvatarDuration, Is.EqualTo(0f));
                Assert.That(refreshes, Is.EqualTo(3));
                Assert.That(AvatarSettingsPresenter.FormatFade(0f), Is.EqualTo("Instant"));
                Assert.That(AvatarSettingsPresenter.FormatFade(1.5f), Is.EqualTo("1.5 s"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AvatarPresenter_WithoutSettingsRefusesAndDoesNotRefresh()
        {
            int refreshes = 0;
            var presenter = new AvatarSettingsPresenter(() => null, () => refreshes++);
            Assert.That(presenter.HasSettings, Is.False);
            Assert.That(presenter.SetZoneBased(true), Is.False);
            Assert.That(presenter.SetFadeSeconds(1f), Is.False);
            Assert.That(refreshes, Is.EqualTo(0));
        }

        [Test]
        public void AvatarPresenter_PresetIsRoundedClampedAndDoesNotTouchSettings()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            int refreshes = 0;
            try
            {
                var presenter = new AvatarSettingsPresenter(() => settings, () => refreshes++);
                presenter.SetPresetIndex(2.6f);
                Assert.That(presenter.PresetIndex, Is.EqualTo(3));
                presenter.SetPresetIndex(99f);
                Assert.That(presenter.PresetIndex, Is.EqualTo(AvatarSettingsPresenter.PresetCount - 1));
                presenter.SetPresetIndex(-4f);
                Assert.That(presenter.PresetIndex, Is.EqualTo(0));
                Assert.That(presenter.PresetText, Is.EqualTo("Preset 1 of " + AvatarSettingsPresenter.PresetCount));
                presenter.SetUsePreset(false);
                Assert.That(presenter.UsePreset, Is.False);
                Assert.That(refreshes, Is.EqualTo(0), "Preset choice applies at spawn, not to live avatars.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // ---- Scene presenter ----

        [Test]
        public void ScenePresenter_ForwardsEachControlToItsAction()
        {
            var log = new List<string>();
            var presenter = new SceneControlsPresenter(
                value => log.Add("anchors:" + value), value => log.Add("manipulation:" + value),
                value => log.Add("brightness:" + value), () => log.Add("navmesh"));
            presenter.SetAnchorsVisible(true);
            presenter.SetManipulation(true);
            presenter.SetBrightness(5f);
            presenter.RebuildNavMesh();
            Assert.That(log, Is.EqualTo(new[]
            {
                "anchors:True", "manipulation:True", "brightness:" + SceneControlsPresenter.MaxBrightness, "navmesh",
            }));
            Assert.That(presenter.NavMeshRebuilds, Is.EqualTo(1));
            Assert.That(presenter.AnchorsVisible && presenter.ManipulationEnabled, Is.True);
        }

        // ---- Generated view: every control exists and is wired ----

        // One wide page of two columns instead of tabs, and nothing but user words by default.
        [Test]
        public void GeneratedPanel_HasEveryControlOnOnePageInTwoColumns()
        {
            var fixture = BuildView();
            try
            {
                var view = fixture.View;
                var room = view.Root.Find("Columns/Room");
                var settings = view.Root.Find("Columns/Settings");
                Assert.That(room, Is.Not.Null);
                Assert.That(settings, Is.Not.Null);
                foreach (Component control in new Component[] { view.Connect, view.Headline.Text, view.BusyBar })
                    Assert.That(control.transform.IsChildOf(room), Is.True, control.name + " belongs to the room column");
                foreach (Component control in new Component[] { view.UsePreset.Toggle, view.Preset.Slider, view.Preset.ValueText,
                                                                view.Anchors.Toggle, view.Passthrough.Slider })
                    Assert.That(control.transform.IsChildOf(settings), Is.True, control.name + " belongs to the settings column");
                Assert.That(view.Root.GetComponentsInChildren<Toggle>(true).Length, Is.EqualTo(2));
                Assert.That(view.Root.GetComponentsInChildren<Slider>(true).Length, Is.EqualTo(2));
                Assert.That(view.Tutorial.gameObject.activeSelf, Is.False, "the tutorial slot shows once the tutorial is wired");
                Assert.That(view.Tutorial.transform.parent, Is.EqualTo(view.Connect.transform.parent));
                Assert.That(view.LeaveRoom.transform.GetSiblingIndex(), Is.EqualTo(view.Connect.transform.GetSiblingIndex() + 1));
                Assert.That(view.Tutorial.transform.GetSiblingIndex(), Is.EqualTo(view.LeaveRoom.transform.GetSiblingIndex() + 1),
                    "right under the primary action (Leave room shares its place, only one of them shows)");
                Assert.That(view.LeaveRoom.gameObject.activeSelf, Is.False, "not in a room");
                Assert.That(view.Title.text, Is.EqualTo("SpatialBlend - Freeform Collaboration"));
                Assert.That(UiTheme.ProductTitle, Is.EqualTo(view.Title.text), "one place for the title");
                Assert.That(view.Passthrough.ValueText.text, Does.EndWith("%"), "a percentage, not a raw float");
                Assert.That(view.Root.GetComponentsInChildren<TMP_Text>(true).Any(x => (x.text ?? "").Contains("Edit")), Is.False, "the mode chip replaces the mode row");
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void GeneratedPanel_TutorialButtonShowsOnceItsActionIsGiven()
        {
            var root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            int tutorials = 0;
            try
            {
                var view = ControlPanelView.Build((RectTransform)root.transform, UiTheme.PanelWidth,
                    new ControlPanelActions { Tutorial = () => tutorials++ },
                    new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null));
                Assert.That(view.Tutorial.gameObject.activeSelf, Is.True);
                view.Tutorial.onClick.Invoke();
                Assert.That(tutorials, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void GeneratedPanel_DefaultPanelHasNoDeveloperInformationAndDiagnosticsAreOptIn()
        {
            var root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                var plain = ControlPanelView.Build((RectTransform)root.transform, UiTheme.PanelWidth, new ControlPanelActions(),
                    new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null));
                Assert.That(plain.DiagnosticsBlock, Is.Null);
                Assert.That(plain.LinkState, Is.Null);
                Assert.That(plain.RebuildNavMesh, Is.Null);
                // Every state, with every technical field filled in, shows only plain words (case-insensitive).
                var inputs = new[]
                {
                    new SessionStatusInput { LinkState = SpatialBlendConnectionState.Failed, LibraryCompatible = true, CarrierText = "Photon fallback", CanRetry = true },
                    new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, CarrierText = "TURN relay", RoomName = "Room 3", PeopleCount = 2 },
                    new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connecting, Role = CoreParticipantRole.Client, LibraryCompatible = true, CarrierText = "direct (WebRTC)" },
                    new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = false },
                    new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, Role = CoreParticipantRole.Client, LibraryCompatible = true, HostLeft = true },
                    new SessionStatusInput { LocalMode = true, LocalRunning = true },
                };
                foreach (var input in inputs)
                {
                    plain.ApplySession(SessionStatusPresenter.Build(input));
                    foreach (var text in plain.Root.GetComponentsInChildren<TMP_Text>(true))
                    {
                        string lower = (text.text ?? string.Empty).ToLowerInvariant();
                        foreach (var banned in new[] { "core", "carrier", "webrtc", "turn", "photon", "v2", "library", "navmesh", "host", "client", "sam3d", "not assigned" })
                            StringAssert.DoesNotContain(banned, lower, "developer wording in the default panel: " + text.text);
                    }
                }
                UnityEngine.Object.DestroyImmediate(plain.Root.gameObject);

                var dev = ControlPanelView.Build((RectTransform)root.transform, UiTheme.PanelWidth, new ControlPanelActions(),
                    new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null), true);
                Assert.That(dev.DiagnosticsBlock, Is.Not.Null);
                Assert.That(dev.LinkState, Is.Not.Null);
                Assert.That(dev.RebuildNavMesh, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // The tutorial is entered from the lobby, so its button shows there (lobby only; it runs before connecting); Leave room is
        // offered in a room using the matchmaker's own-leave path.
        // In a room the control panel's own canvas is visible and shows the room headline and "Leave room"
        // (this drives the real ready state and asks the hierarchy).
        [Test]
        public void ControlPanel_InARoomTheCanvasAndItsRoomControlsAreActuallyVisible()
        {
            var managerObject = new GameObject("manager");
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            var host = BuildPanelHost();
            try
            {
                SetField(host.Panel, "_spaceManager", manager);
                SetField(host.Panel, "_ControlPanelCanvas", host.Canvas);
                host.Canvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                Invoke(host.Panel, "OnEnable");
                Invoke(host.Panel, "Start");
                var view = host.Panel.View;
                Assert.That(view, Is.Not.Null);

                PrepareManagerReadiness(manager, true, true);
                RaiseManagerState(manager, SpatialBlendConnectionState.Connected);
                Assert.That(host.Canvas.activeInHierarchy, Is.True, "the canvas is not hidden in a room");
                Assert.That(view.Root.gameObject.activeInHierarchy, Is.True);
                Assert.That(view.LeaveRoom.gameObject.activeInHierarchy, Is.True, "Leave room shows in a room");
                Assert.That(view.Headline.Text.gameObject.activeInHierarchy, Is.True);
                Assert.That(view.Headline.Text.text, Does.StartWith("In "));
                Assert.That(view.Connect.gameObject.activeInHierarchy, Is.False, "nothing to start in a room");
                Assert.That(view.Tutorial.gameObject.activeInHierarchy, Is.False, "lobby only");
                Assert.That(view.JoinAsObserver.gameObject.activeInHierarchy, Is.False, "lobby only");
            }
            finally
            {
                Invoke(host.Panel, "OnDisable");
                host.Dispose();
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        // Toasts are an overlay in front of the user, not on the wrist panel; a refused outline reads as one
        // plain sentence and the technical reason goes to the log.
        [Test]
        public void ControlPanel_ToastsAreAnOverlayInFrontOfTheUserWithPlainRefusalText()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var overlay = (UiToastOverlay)typeof(ControlPanel).GetField("_toastOverlay", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host.Panel);
                Assert.That(overlay, Is.Not.Null);
                Assert.That(overlay.transform.IsChildOf(host.Panel.View.Root), Is.False, "not on the wrist panel");
                Assert.That(overlay.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.WorldSpace));
                Invoke(host.Panel, "OnRegionRefused", "Region not created: The declared extent must be a simple polygon.");
                var texts = overlay.Host.GetComponentsInChildren<TMP_Text>().Select(t => t.text).ToArray();
                Assert.That(texts, Does.Contain("This outline crosses itself. Draw it again."));
                Assert.That(texts.Any(t => t.Contains("polygon")), Is.False, "the technical reason is not shown");
                foreach (var group in overlay.Host.GetComponentsInChildren<CanvasGroup>()) Assert.That(group.blocksRaycasts, Is.False);
                // Where it sits: ahead of the head along the horizontal gaze, below the line of sight, at reading distance.
                var spot = UiToastOverlay.PositionFor(new Vector3(1f, 1.6f, 2f), new Vector3(0f, 0.4f, 1f));
                Assert.That(spot.x, Is.EqualTo(1f + UiToastOverlay.RightMetres).Within(1e-4f), "just right of the gaze, clear of the left-hand panel");
                Assert.That(spot.z - 2f, Is.EqualTo(UiToastOverlay.DistanceMetres).Within(1e-4f));
                Assert.That(spot.y, Is.EqualTo(1.6f - UiToastOverlay.DropMetres).Within(1e-4f));
                float emMm = UiTheme.FontBody * UiToastOverlay.UnitScale * 1000f;
                float overlayDistance = Mathf.Sqrt(UiToastOverlay.DistanceMetres * UiToastOverlay.DistanceMetres + UiToastOverlay.DropMetres * UiToastOverlay.DropMetres
                    + UiToastOverlay.RightMetres * UiToastOverlay.RightMetres);
                Assert.That(emMm / overlayDistance, Is.EqualTo(UiTheme.FontBody).Within(0.3f), "toast body text is 24 dmm, as on the panel (UiMetrics.Toast)");
            }
            finally
            {
                host.Dispose();
            }
        }

        // Scanning works in the shipped scene. The scene has no scan panel instance (the panel destroys itself
        // when closed), so the object menu's Reconstruct 3D spawns the panel prefab the interaction manager carries (the control panel
        // has no Scan button); that prefab must carry the manager component.
        [Test]
        public void FreeformScene_ScanHasAScanPanelPrefabItCanSpawn()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity", OpenSceneMode.Additive);
            try
            {
                var modeManager = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<InteractionModeManager>(true)).FirstOrDefault();
                Assert.That(modeManager, Is.Not.Null, "the scene has the interaction mode manager");
                var prefab = (GameObject)typeof(InteractionModeManager).GetField("_defaultPassthroughPanelPrefab", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(modeManager);
                Assert.That(prefab, Is.Not.Null, "the scan panel prefab is assigned in the shipped scene");
                Assert.That(prefab.GetComponent<SpatialBlend.Samples.Scanning.PassthroughPanelManager>(), Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>Instantiates the shipped ControlPanel prefab, builds the generated panel in its real host canvas and returns the canvas height.</summary>
        private static float MeasureShippedControlCanvasHeight(bool diagnostics, System.Action<GameObject, ControlPanel> inspect = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var panel = instance.GetComponent<ControlPanel>();
                SetField(panel, "_showDiagnostics", diagnostics);
                Assert.That(panel.BuildGeneratedLayout(), Is.True);
                panel.View.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput
                    { LinkState = SpatialBlendConnectionState.Failed, LibraryCompatible = true, CarrierText = "Photon fallback" }));
                panel.View.SetNavMeshNote("NavMesh source not assigned.");
                var canvas = (RectTransform)panel.View.Root.parent;
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(canvas);
                Canvas.ForceUpdateCanvases();
                inspect?.Invoke(instance, panel);
                return canvas.rect.height;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        // The prefab host canvas used to fit to its minimum size, pinning the panel at its minimum height, so in the Editor and in
        // development builds the Diagnostics block hung off the bottom where no ray or poke could reach it.
        [Test]
        public void ShippedControlPanel_EveryControlLiesInsideTheCanvasTheInputSurfaceIsFittedTo()
        {
            float height = MeasureShippedControlCanvasHeight(true, (instance, panel) =>
            {
                var canvas = (RectTransform)panel.View.Root.parent;
                Assert.That(panel.View.DiagnosticsBlock, Is.Not.Null);
                var buttons = instance.GetComponentsInChildren<Button>(false)
                    .Where(b => b.transform.IsChildOf(panel.View.Root) && b.gameObject.activeInHierarchy).ToArray();
                Assert.That(buttons.Length, Is.GreaterThanOrEqualTo(2), "the primary action and Rebuild NavMesh are visible in this state");
                var corners = new Vector3[4];
                foreach (var button in buttons.Concat(new[] { panel.View.RebuildNavMesh }))
                {
                    ((RectTransform)button.transform).GetWorldCorners(corners);
                    foreach (var c in corners)
                    {
                        var local = canvas.InverseTransformPoint(c);
                        Assert.That(local.y, Is.GreaterThanOrEqualTo(canvas.rect.yMin - 0.01f), button.name + " is below the panel");
                        Assert.That(local.y, Is.LessThanOrEqualTo(canvas.rect.yMax + 0.01f), button.name + " is above the panel");
                    }
                }
                Assert.That(canvas.rect.height, Is.EqualTo(panel.View.Root.rect.height).Within(0.01f), "the canvas is exactly the root rect");
            });
            Assert.That(height, Is.GreaterThan(ControlPanelView.MinHeight), "the diagnostics panel is taller than the minimum");
        }

        // The control panel stays on in a room, where the interaction mode panel is on as well. Both
        // prefab instances hang from the same controller anchor, so they must be placed apart: the mode panel above the control panel.
        [Test]
        public void FreeformScene_InARoomTheControlPanelAndTheModePanelDoNotOverlap()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity", OpenSceneMode.Additive);
            try
            {
                ControlPanel panel = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<ControlPanel>(true))
                    .First(p => typeof(ControlPanel).GetField("_connect", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p) != null);
                var controlCanvas = (GameObject)typeof(ControlPanel).GetField("_ControlPanelCanvas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                var modeCanvas = (GameObject)typeof(ControlPanel).GetField("_InteractionModeCanvas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                var control = (RectTransform)controlCanvas.transform;
                var mode = (RectTransform)modeCanvas.transform;
                // The largest panel there is: a development build or the Editor adds the Diagnostics block. Measure the REAL prefab canvas
                // (its fitter and layout as shipped), not a copy.
                float tallest = MeasureShippedControlCanvasHeight(true);
                control.sizeDelta = new Vector2(UiTheme.PanelWidth, tallest);
                Transform frame = mode.parent.parent != null ? mode.parent.parent : mode.parent;   // the controller anchor both hang from
                Assert.That(control.parent.parent, Is.EqualTo(mode.parent.parent), "both panels hang from the same anchor");
                (float lo, float hi) Span(RectTransform rect)
                {
                    var corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    var ys = corners.Select(c => frame.InverseTransformPoint(c).y).ToArray();
                    return (ys.Min(), ys.Max());
                }
                var c = Span(control);
                var m = Span(mode);
                bool apart = m.lo >= c.hi + 0.01f || c.lo >= m.hi + 0.01f;
                Assert.That(apart, Is.True, $"control panel spans y {c.lo:0.000}..{c.hi:0.000}, mode panel y {m.lo:0.000}..{m.hi:0.000} (anchor space, metres)");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void ControlPanel_HasNoScanButton_ScanStartsFromABoxAndTheObjectMenu()
        {
            // Scanning starts in Editing mode by drawing a box around the real object, then the object menu's
            // Reconstruct 3D. The panel carries no scan control, in the lobby or in a room.
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var view = host.Panel.View;
                foreach (var linkState in new[] { SpatialBlendConnectionState.Disconnected, SpatialBlendConnectionState.Connected })
                {
                    view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = linkState, LibraryCompatible = true }));
                    foreach (var label in view.Root.GetComponentsInChildren<TMP_Text>(true))
                        Assert.That(label.text, Does.Not.Contain("Scan"), "no scan control in " + linkState);
                    foreach (var button in view.Root.GetComponentsInChildren<Button>(true))
                        Assert.That(button.name, Does.Not.Contain("Scan"), "no scan button in " + linkState);
                }
                Assert.That(typeof(ControlPanelActions).GetField("ScanObject"), Is.Null, "the action is gone");
                Assert.That(typeof(ControlPanelView).GetProperty("Scan"), Is.Null, "the button is gone");
                Assert.That(typeof(ControlPanelView).GetProperty("ScanAvailable"), Is.Null, "so is its enable gate");
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_ShowsTheTutorialButtonInTheLobbyAndLeaveRoomOnlyInARoom()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var view = host.Panel.View;
                Assert.That(view.Tutorial.gameObject.activeSelf, Is.True, "visible in the lobby");
                view.Tutorial.onClick.Invoke();   // wired: it starts the lobby tutorial (see TutorialUiTests for the run itself)
                Assert.That(host.Panel.Tutorial, Is.Not.Null);
                host.Panel.Tutorial.Abort();
                Assert.That(host.Panel.TutorialActive, Is.False);
                Assert.That(view.LeaveRoom.gameObject.activeSelf, Is.False);
                view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput
                {
                    LinkState = SpatialBlendConnectionState.Connected, LibraryCompatible = true, RoomName = "Room 3", PeopleCount = 2,
                }));
                Assert.That(view.LeaveRoom.gameObject.activeSelf, Is.True);
                Assert.That(view.Connect.gameObject.activeSelf, Is.False);
                Assert.That(view.Tutorial.gameObject.activeSelf, Is.False, "the tutorial runs before connecting, so not in a room");
                foreach (var state in new[] { SpatialBlendConnectionState.Connecting, SpatialBlendConnectionState.Failed })
                {
                    view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = state, LibraryCompatible = true }));
                    Assert.That(view.Tutorial.gameObject.activeSelf, Is.False, state.ToString());
                }
                view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LibraryCompatible = true }));
                Assert.That(view.Tutorial.gameObject.activeSelf, Is.True, "back in the lobby");
            }
            finally
            {
                host.Dispose();
            }
        }

        // "Join as observer" in the lobby on desktop players and the Editor only (the spectator branch wires the
        // action). Hidden on the Quest (Android), without an action, and in every in-room, connecting or error state.
        [Test]
        public void GeneratedPanel_JoinAsObserverIsADesktopLobbyButtonWithAnAction()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            var roots = new List<GameObject>();
            ControlPanelView Make(RuntimePlatform platform, bool withAction)
            {
                var root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
                roots.Add(root);
                return ControlPanelView.Build((RectTransform)root.transform, UiTheme.PanelWidth,
                    new ControlPanelActions { JoinAsObserver = withAction ? () => { } : null },
                    new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null),
                    false, platform);
            }
            try
            {
                Assert.That(ControlPanelView.ObserverPlatform(RuntimePlatform.WindowsPlayer), Is.True);
                Assert.That(ControlPanelView.ObserverPlatform(RuntimePlatform.OSXEditor), Is.True);
                Assert.That(ControlPanelView.ObserverPlatform(RuntimePlatform.Android), Is.False, "the Quest build has no observer join");

                var desktop = Make(RuntimePlatform.WindowsPlayer, true);
                Assert.That(desktop.JoinAsObserver.gameObject.activeSelf, Is.True, "desktop lobby");
                Assert.That(desktop.JoinAsObserver.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Join as observer"));
                foreach (var state in new[] { SpatialBlendConnectionState.Connecting, SpatialBlendConnectionState.Connected, SpatialBlendConnectionState.Failed })
                {
                    desktop.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = state, LibraryCompatible = true }));
                    Assert.That(desktop.JoinAsObserver.gameObject.activeSelf, Is.False, state.ToString());
                }
                desktop.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LibraryCompatible = true }));
                Assert.That(desktop.JoinAsObserver.gameObject.activeSelf, Is.True, "back in the lobby");

                Assert.That(Make(RuntimePlatform.Android, true).JoinAsObserver.gameObject.activeSelf, Is.False, "Quest build");
                Assert.That(Make(RuntimePlatform.WindowsPlayer, false).JoinAsObserver.gameObject.activeSelf, Is.False, "no action wired: no dead button");
            }
            finally
            {
                foreach (var root in roots) UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ControlPanel_LeaveRoomIsAnOwnLeaveNotALostHost()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var connection = host.AddConnection();
                SetBaseField(connection, "_participantRole", CoreParticipantRole.Client);
                host.Panel.OnLeaveClicked();
                Assert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
                Assert.That(connection.HostLeft, Is.False, "leaving is not a host loss");
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_DiagnosticsShowInADevelopmentBuildOrWhenSwitchedOn()
        {
            var host = BuildPanelHost();
            try
            {
                // The rule: a development build or the explicit setting.
                Assert.That(ControlPanel.DiagnosticsVisibleFor(false, false), Is.False, "off in a release build by default");
                Assert.That(ControlPanel.DiagnosticsVisibleFor(true, false), Is.True, "on in a development build");
                Assert.That(ControlPanel.DiagnosticsVisibleFor(false, true), Is.True, "on in a release build when switched on");
                Assert.That(host.Panel.DiagnosticsVisible, Is.EqualTo(Debug.isDebugBuild), "the component uses the rule with the build flag and its setting");
                var field = typeof(ControlPanel).GetField("_showDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
                field.SetValue(host.Panel, true);
                Assert.That(host.Panel.DiagnosticsVisible, Is.True, "the explicit setting turns it on in any build");
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void GeneratedPanel_HitTargetsAreAtLeastFourCentimetres()
        {
            // The panel canvas is scaled to about 0.52 mm per unit.
            const float metresPerUnit = 0.00052328f;
            Assert.That(ControlPanelKit.RowHeight * metresPerUnit, Is.GreaterThanOrEqualTo(0.04f));
            Assert.That(ControlPanelKit.SliderTrackHeight * metresPerUnit, Is.GreaterThanOrEqualTo(0.028f));
            var fixture = BuildView();
            try
            {
                foreach (var button in new[] { fixture.View.Connect, fixture.View.RebuildNavMesh })
                {
                    Assert.That(button.GetComponent<LayoutElement>().minHeight,
                        Is.GreaterThanOrEqualTo(ControlPanelKit.RowHeight));
                }
                foreach (var toggle in new[] { fixture.View.UsePreset, fixture.View.Anchors })
                {
                    Assert.That(toggle.Toggle.GetComponent<LayoutElement>().minHeight,
                        Is.GreaterThanOrEqualTo(ControlPanelKit.RowHeight));
                }
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // The demo runs the Default display mode, so the sample panel does not offer Zone-based display
        // or its fade duration (the fade is used only in ZoneBased; RemoteAvatarVisibilityPolicy.Evaluate ignores it in Default).
        // The framework feature and the presenter setters stay (see AvatarPresenter_* above).
        [Test]
        public void GeneratedPanel_AvatarSettingsOfferOnlyThePresetControls()
        {
            var fixture = BuildView();
            try
            {
                var view = fixture.View;
                var rows = view.Root.GetComponentsInChildren<TMP_Text>(false).Select(t => t.text).ToArray();
                Assert.That(rows.Contains("Preset avatar"), Is.True);
                Assert.That(rows.Contains("Preset"), Is.True);
                Assert.That(rows.Any(r => (r ?? "").Contains("Zone-based")), Is.False);
                Assert.That(rows.Any(r => (r ?? "").Contains("Fade duration")), Is.False);
                Assert.That(view.Root.GetComponentsInChildren<Toggle>(false).Length, Is.EqualTo(2), "preset, anchors");
                Assert.That(view.Root.GetComponentsInChildren<Slider>(false).Length, Is.EqualTo(2), "preset, passthrough");
                Assert.That(fixture.Settings.avatarDisplayMode, Is.EqualTo(AvatarDisplayMode.Default), "the panel never leaves Default");
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void GeneratedPanel_PresetControlsDriveThePresenter()
        {
            var fixture = BuildView();
            try
            {
                var view = fixture.View;
                view.Preset.Slider.value = 4f;
                Assert.That(fixture.Avatars.PresetIndex, Is.EqualTo(4));
                Assert.That(view.Preset.ValueText.text, Is.EqualTo("5 of " + AvatarSettingsPresenter.PresetCount));
                view.UsePreset.Toggle.isOn = false;
                Assert.That(fixture.Avatars.UsePreset, Is.False);
                Assert.That(view.Preset.Slider.interactable, Is.False);
                Assert.That(fixture.Refreshes, Is.EqualTo(0));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void GeneratedPanel_SceneControlsCallTheirActions()
        {
            var fixture = BuildView();
            try
            {
                var view = fixture.View;
                view.Anchors.Toggle.isOn = true;
                view.Passthrough.Slider.value = 0.5f;
                view.RebuildNavMesh.onClick.Invoke();
                Assert.That(fixture.Log, Is.EqualTo(new[]
                {
                    "anchors:True", "brightness:" + 0.5f, "navmesh",
                }));
                view.Connect.onClick.Invoke();
                Assert.That(fixture.Connects, Is.EqualTo(1));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void GeneratedPanel_ApplySessionUpdatesLinkStateAndConnectButton()
        {
            var fixture = BuildView();
            try
            {
                var view = fixture.View;
                view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput
                {
                    LinkState = SpatialBlendConnectionState.Connecting,
                    Role = CoreParticipantRole.Client,
                    LibraryCompatible = true,
                }));
                Assert.That(view.Headline.Text.text, Is.EqualTo("Connecting…"));
                Assert.That(view.LinkState.Text.text, Does.Contain("connecting"), "diagnostics");
                Assert.That(view.BusyBar.gameObject.activeSelf, Is.True);
                Assert.That(view.Connect.interactable, Is.False);

                view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput
                {
                    LinkState = SpatialBlendConnectionState.Failed,
                    CanRetry = true,
                    LibraryCompatible = true,
                }));
                Assert.That(view.Headline.Text.text, Is.EqualTo("Couldn't connect"));
                Assert.That(view.Detail.text, Is.EqualTo("Check your Wi-Fi and try again."));
                Assert.That(view.BusyBar.gameObject.activeSelf, Is.False);
                Assert.That(view.Connect.interactable, Is.True);
                Assert.That(view.Connect.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Try again"));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // ---- ControlPanel component: legacy rows, status polling, room list, refresh ----

        [Test]
        public void ControlPanel_BuildsTheGeneratedLayoutAndHidesLegacyRows()
        {
            var host = BuildPanelHost();
            try
            {
                Assert.That(host.Panel.BuildGeneratedLayout(), Is.True);
                Assert.That(host.Panel.BuildGeneratedLayout(), Is.False, "The layout is built once.");
                Assert.That(host.Legacy.activeSelf, Is.False);
                Assert.That(host.Panel.View.Root.parent, Is.EqualTo(host.Canvas.transform));
                Assert.That(host.Panel.View.Root.gameObject.activeSelf, Is.True);
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_PresetControlsDriveTheAvatarSettingsAndDisplayModeStaysDefault()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                Assert.That(host.Panel.UsePresetAvatar, Is.True);
                host.Panel.View.Preset.Slider.value = 3f;
                Assert.That(host.Panel.SelectedAvatarPreset, Is.EqualTo(3));
                // No Zone-based display row, so the panel cannot change the mode.
                Assert.That(host.Settings.avatarDisplayMode, Is.EqualTo(AvatarDisplayMode.Default));
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_DiagnosticsLinkStateFollowsTheConnectionManager()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var connection = host.AddConnection();
                Invoke(host.Panel, "Start");
                var view = host.Panel.View;
                Assert.That(view.LinkState.Text.text, Is.EqualTo("Core link: disconnected"));

                SetBaseField(connection, "_participantRole", CoreParticipantRole.Host);
                connection.State = SpatialBlendConnectionState.Connecting;
                host.Panel.RefreshSessionStatus();
                Assert.That(view.LinkState.Text.text, Does.Contain("connecting"));

                connection.State = SpatialBlendConnectionState.Failed;
                host.Panel.RefreshSessionStatus();
                Assert.That(view.LinkState.Text.text, Is.EqualTo("Core link: failed"));
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_ConnectOpensTheRoomListInsteadOfJoiningAFixedSession()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var bootstrapObject = new GameObject("bootstrap");
                host.Track(bootstrapObject);
                var bootstrap = bootstrapObject.AddComponent<FusionBootstrap>();
                var browser = bootstrapObject.AddComponent<RoomListTestBrowser>();
                SetField(host.Panel, "_fusionBootstrap", bootstrap);
                SetField(host.Panel, "_sessionBrowser", browser);

                host.Panel.View.Connect.onClick.Invoke();

                Assert.That(bootstrap.CurrentStage, Is.EqualTo(FusionBootstrap.Stage.Disconnected),
                    "Connect must not start the shared client before a room is chosen.");
                Assert.That(browser.Calls, Does.Contain("join-lobby"));
                var list = host.Canvas.GetComponentInChildren<SessionListPanel>(true);
                Assert.That(list, Is.Not.Null);
                Assert.That(list.gameObject.activeSelf, Is.True);
            }
            finally
            {
                host.Dispose();
            }
        }

        // Find a room -> pick an existing room -> the join fails -> Find a room did nothing. The runner start ended
        // without the player in the room and nothing told the room list: the browser stayed in Starting (it refuses to open the lobby and
        // hides the list), the matchmaker kept the player InSession.
        [Test]
        public void ControlPanel_FindARoomAfterAFailedJoin_SearchesAgainAndShowsTheResult()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                var bootstrapObject = new GameObject("bootstrap");
                host.Track(bootstrapObject);
                var bootstrap = bootstrapObject.AddComponent<FusionBootstrap>();
                var browser = bootstrapObject.AddComponent<RoomListTestBrowser>();
                SetField(host.Panel, "_fusionBootstrap", bootstrap);
                SetField(host.Panel, "_sessionBrowser", browser);

                host.Panel.View.Connect.onClick.Invoke();
                var list = host.Canvas.GetComponentInChildren<SessionListPanel>(true);
                browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9) });
                list.RowObjects[0].GetComponent<Button>().onClick.Invoke();
                Assert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Starting));
                Assert.That(list.gameObject.activeSelf, Is.False, "the list closes once the join starts");

                browser.ReportStartFailed(Fusion.ShutdownReason.GameIsFull);

                Assert.That(browser.InSession, Is.False);
                host.Panel.View.Connect.onClick.Invoke();   // Find a room, again
                Assert.That(browser.Calls.Count(c => c == "join-lobby"), Is.EqualTo(2), "a new search started");
                Assert.That(list.gameObject.activeSelf, Is.True, "and the list is open");
                browser.ApplySessions(new[] { new FusionSessionEntry("Room 2", 1, 9) });
                Assert.That(list.RowObjects.Count, Is.EqualTo(1));
                Assert.That(list.RowObjects[0].GetComponentInChildren<TMP_Text>().text, Does.Contain("Room 2"), "its result shows");
            }
            finally
            {
                JoinFailure.Take();
                host.Dispose();
            }
        }

        [Test]
        public void ControlPanel_RebuildNavMeshReportsAMissingSource()
        {
            var host = BuildPanelHost();
            try
            {
                host.Panel.BuildGeneratedLayout();
                host.Panel.View.RebuildNavMesh.onClick.Invoke();
                Assert.That(host.Panel.ScenePresenter.NavMeshRebuilds, Is.EqualTo(1));
                Assert.That(host.Panel.View.NavMeshNote.text, Does.Contain("not assigned"));
            }
            finally
            {
                host.Dispose();
            }
        }

        // The generated panel did not answer controller input. The canvas hosts the Interaction SDK's canvas
        // interaction objects (they draw nothing); hiding "every other child" hid them too, so no ray or poke reached any control. They
        // stay active, and the clipped surfaces are sized to the canvas rectangle as it is after the generated layout.
        [Test]
        public void ControlPanel_KeepsTheInteractionObjectsAndFitsTheirSurfacesToTheGeneratedCanvas()
        {
            var host = BuildPanelHost();
            try
            {
                var canvasRect = (RectTransform)host.Canvas.transform;
                var interaction = new GameObject("ISDK_RayCanvasInteraction", typeof(RectTransform));
                interaction.transform.SetParent(canvasRect, false);
                var surface = new GameObject("Surface", typeof(RectTransform), typeof(Oculus.Interaction.Surfaces.BoundsClipper));
                var surfaceRect = (RectTransform)surface.transform;
                surfaceRect.SetParent(interaction.transform, false);
                surfaceRect.anchorMin = Vector2.zero;
                surfaceRect.anchorMax = Vector2.one;
                surfaceRect.offsetMin = surfaceRect.offsetMax = Vector2.zero;
                var clipper = surface.GetComponent<Oculus.Interaction.Surfaces.BoundsClipper>();
                clipper.Size = new Vector3(406f, 415f, 0.01f);   // authored for the legacy layout

                Assert.That(ControlPanel.IsLegacyRow(host.Legacy.transform), Is.True);
                Assert.That(ControlPanel.IsLegacyRow(interaction.transform), Is.False, "the interaction objects draw nothing and react to nothing");

                Assert.That(host.Panel.BuildGeneratedLayout(), Is.True);
                Assert.That(host.Legacy.activeSelf, Is.False, "the legacy row is hidden");
                Assert.That(interaction.activeSelf, Is.True, "the interaction object is how rays reach the generated controls");

                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);
                Assert.That(host.Panel.FitInteractionSurfaces(true), Is.EqualTo(1));
                Rect rect = surfaceRect.rect;
                Assert.That(clipper.Size.x, Is.EqualTo(rect.width).Within(1e-3f));
                Assert.That(clipper.Size.y, Is.EqualTo(rect.height).Within(1e-3f));
                Assert.That(clipper.Position.x, Is.EqualTo(rect.center.x).Within(1e-3f));
                Assert.That(clipper.Position.y, Is.EqualTo(rect.center.y).Within(1e-3f));
                Assert.That(clipper.Size.z, Is.EqualTo(0.01f).Within(1e-6f), "the depth is the authored one");
                // A size change (a tab with another height) refits on the next poll.
                Assert.That(host.Panel.FitInteractionSurfaces(), Is.EqualTo(0), "nothing changed");
                canvasRect.sizeDelta = new Vector2(406f, 700f);
                Assert.That(host.Panel.FitInteractionSurfaces(), Is.EqualTo(1), "the canvas changed size");
            }
            finally
            {
                host.Dispose();
            }
        }

        // The real scene: the ControlPanel prefab instance in FreeformCollaboration.unity keeps its Interaction SDK canvas objects
        // (ray and poke) active once the generated layout is built, and each is still wired to the canvas that carries the controls.
        [Test]
        public void ControlPanel_InTheRealSceneKeepsItsRayAndPokeCanvasInteraction()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity", OpenSceneMode.Additive);
            try
            {
                ControlPanel panel = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<ControlPanel>(true))
                    .FirstOrDefault(p => typeof(ControlPanel).GetField("_connect", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p) != null);
                Assert.That(panel, Is.Not.Null, "the scene has a ControlPanel wired to its legacy rows");
                var connect = (Button)typeof(ControlPanel).GetField("_connect", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                Canvas canvas = connect.GetComponentInParent<Canvas>(true);
                var interactions = canvas.GetComponentsInChildren<Oculus.Interaction.PointableCanvas>(true);
                Assert.That(interactions, Is.Not.Empty, "the canvas has PointableCanvas objects (ray and poke)");
                foreach (var pointable in interactions) Assert.That(pointable.gameObject.activeSelf, Is.True, pointable.name + " before");
                foreach (Transform child in canvas.transform)
                    if (child.GetComponent<Oculus.Interaction.PointableCanvas>() != null)
                        Assert.That(ControlPanel.IsLegacyRow(child), Is.False, child.name + " would be hidden as a legacy row");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void ControlPanel_ScenePresenterManipulationSwitchesTheInteractionMode()
        {
            var host = BuildPanelHost();
            var existing = InteractionModeManager.Instance;
            var managerObject = existing == null ? new GameObject("mode-manager") : null;
            var runtime = existing != null ? existing : managerObject.AddComponent<InteractionModeManager>();
            if (existing == null) Invoke(runtime, "Awake");
            InteractionMode previous = runtime.CurrentMode;
            try
            {
                host.Panel.BuildGeneratedLayout();
                // The panel has no mode row (the mode chip replaces it); the presenter still drives the mode.
                host.Panel.ScenePresenter.SetManipulation(true);
                Assert.That(runtime.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
                host.Panel.ScenePresenter.SetManipulation(false);
                Assert.That(runtime.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
            }
            finally
            {
                runtime.SetMode(previous);
                if (managerObject != null) UnityEngine.Object.DestroyImmediate(managerObject);
                host.Dispose();
            }
        }

        private sealed class ViewFixture : IDisposable
        {
            public GameObject Root;
            public ControlPanelView View;
            public SpatialBlendSettings Settings;
            public AvatarSettingsPresenter Avatars;
            public SceneControlsPresenter Scene;
            public readonly List<string> Log = new();
            public int Refreshes;
            public int Connects;

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Root);
                UnityEngine.Object.DestroyImmediate(Settings);
            }
        }

        private static ViewFixture BuildView()
        {
            var fixture = new ViewFixture
            {
                Root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas)),
                Settings = ScriptableObject.CreateInstance<SpatialBlendSettings>(),
            };
            fixture.Avatars = new AvatarSettingsPresenter(() => fixture.Settings, () => fixture.Refreshes++);
            fixture.Scene = new SceneControlsPresenter(
                value => fixture.Log.Add("anchors:" + value), value => fixture.Log.Add("manipulation:" + value),
                value => fixture.Log.Add("brightness:" + value), () => fixture.Log.Add("navmesh"));
            fixture.View = ControlPanelView.Build((RectTransform)fixture.Root.transform, UiTheme.PanelWidth,
                new ControlPanelActions { Connect = () => fixture.Connects++ },
                fixture.Avatars, fixture.Scene, true);
            // ControlPanel.EnsurePresenters wires these; the bare view fixture must too.
            fixture.Avatars.Changed += () => fixture.View.SyncAvatars(fixture.Avatars);
            fixture.Scene.Changed += () => fixture.View.SyncScene(fixture.Scene);
            return fixture;
        }

        private sealed class PanelHost : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            public GameObject Canvas;
            public GameObject Legacy;
            public ControlPanel Panel;
            public SpatialBlendSettings Settings;
            public GameObject PanelObject;

            public void Track(GameObject go) => _objects.Add(go);

            public RoomTestConnection AddConnection()
            {
                var go = new GameObject("fusion-connection");
                _objects.Add(go);
                var connection = go.AddComponent<RoomTestConnection>();
                SetField(Panel, "_fusionConnectionManager", connection);
                return connection;
            }

            public void Dispose()
            {
                foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (Canvas != null) UnityEngine.Object.DestroyImmediate(Canvas);
                if (PanelObject != null) UnityEngine.Object.DestroyImmediate(PanelObject);
                if (Settings != null) UnityEngine.Object.DestroyImmediate(Settings);
            }
        }

        private static PanelHost BuildPanelHost()
        {
            var host = new PanelHost
            {
                Canvas = new GameObject("panel-canvas", typeof(RectTransform), typeof(Canvas)),
                Settings = ScriptableObject.CreateInstance<SpatialBlendSettings>(),
            };
            ((RectTransform)host.Canvas.transform).sizeDelta = new Vector2(406f, 300f);
            host.Legacy = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            host.Legacy.transform.SetParent(host.Canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(host.Legacy.transform, false);
            host.PanelObject = new GameObject("control-panel");
            host.PanelObject.SetActive(false);
            host.Panel = host.PanelObject.AddComponent<ControlPanel>();
            SetField(host.Panel, "_connect", host.Legacy.GetComponent<Button>());
            SetField(host.Panel, "_settings", host.Settings);
            Invoke(host.Panel, "OnEnable");
            return host;
        }

        private sealed class RoomTestConnection : FusionConnectionManager
        {
            public SpatialBlendConnectionState State = SpatialBlendConnectionState.Disconnected;
            protected override SpatialBlendConnectionState ConnectionState => State;
        }

        private sealed class RoomListTestBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new();
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override void ResetAfterFailedStart() => Calls.Add("reset");
            protected override Task<string> JoinLobbyAsync()
            {
                Calls.Add("join-lobby");
                return Task.FromResult<string>(null);
            }

            protected override Task CloseLobbyAsync() => Task.CompletedTask;
        }

        private static void SetBaseField(object target, string name, object value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                field.SetValue(target, value);
                return;
            }
            Assert.Fail($"Missing production field {name}");
        }

        private static void RaiseManagerState(SpatialBlendManager manager, SpatialBlendConnectionState state)
        {
            // Reflection does not fill C# optional arguments.
            Invoke(manager, "SetConnectionState", state, string.Empty);
        }

        private static void AssertNetworkEvent(SerializedObject serialized, string eventName,
                                               string methodName, int mode)
        {
            var calls = serialized.FindProperty(eventName + ".m_PersistentCalls.m_Calls");
            Assert.That(calls, Is.Not.Null, $"Missing NetworkEvents.{eventName}");
            Assert.That(calls.arraySize, Is.EqualTo(1), $"NetworkEvents.{eventName}");
            var call = calls.GetArrayElementAtIndex(0);
            Assert.That(call.FindPropertyRelative("m_Target").objectReferenceValue,
                Is.InstanceOf<SpatialBlend.Samples.FusionConnectionManager>());
            Assert.That(call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue,
                Is.EqualTo("SpatialBlend.Samples.FusionConnectionManager, SpatialBlend"));
            Assert.That(call.FindPropertyRelative("m_MethodName").stringValue, Is.EqualTo(methodName));
            Assert.That(call.FindPropertyRelative("m_Mode").intValue, Is.EqualTo(mode));
        }

        private static void PrepareManagerReadiness(SpatialBlendManager manager,
                                                    bool initialized, bool compatible)
        {
            SetMember(manager, "Initialized", initialized);
            SetMember(manager, "NativeLibraryCompatible", compatible);
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing production field {name}");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing production field {name}");
            return field.GetValue(target);
        }

        private static void SetMember(object target, string name, object value)
        {
            var type = target.GetType();
            var backing = type.GetField($"<{name}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (backing != null)
            {
                backing.SetValue(target, value);
                return;
            }
            var field = type.GetField("_" + char.ToLowerInvariant(name[0]) + name.Substring(1),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing production state {name}");
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, $"Missing production seam {method}");
            info.Invoke(target, args);
        }
    }
}
#endif
