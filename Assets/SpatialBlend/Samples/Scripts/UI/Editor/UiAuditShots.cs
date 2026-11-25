// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using SpatialBlend.Samples;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders for a UI audit of the surfaces <see cref="ControlPanelScreenshot"/> does not draw: the desktop
    /// spectator window (the reconstructed-object preview has no outline). Run with
    /// <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.UiAuditShots.Capture -shotDir DIR</c>; it needs a graphics device.
    /// </summary>
    public static class UiAuditShots
    {
        private const int W = 1600, H = 900;

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ui-audit-shots");
            Directory.CreateDirectory(dir);
            try
            {
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                SpectatorWindow(dir);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[UiAuditShots] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void SpectatorWindow(string dir)
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 540f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f);   // the spectator scene's camera
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            var host = new GameObject("Session");
            var session = host.AddComponent<FreeformSpectatorSession>();
            var overlay = SpectatorOverlay.Create(session, UiFonts.Set);
            var canvas = overlay.Canvas;
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            rect.sizeDelta = SpectatorOverlay.ReferenceResolution;
            rect.position = Vector3.zero;
            rect.localScale = Vector3.one;

            (string name, SpectatorPhase phase, string typed, string notice, string error)[] states =
            {
                ("spectator-idle", SpectatorPhase.Idle, "", null, null),
                ("spectator-idle-typed", SpectatorPhase.Idle, "Room 3", null, null),
                ("spectator-searching", SpectatorPhase.Searching, "Room 3", "Waiting for the room's participants to start.", null),
                ("spectator-in-room", SpectatorPhase.Spectating, "Room 3", null, null),
                ("spectator-failed", SpectatorPhase.Failed, "Room 3", null, "Joining the room took too long."),
            };
            foreach (var state in states)
            {
                Set(session, "Phase", state.phase);
                Set(session, "Notice", state.notice);
                Set(session, "Error", state.error);
                overlay.RoomField.text = state.typed;
                overlay.Refresh();
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(overlay.Card);
                Shoot(cam, Path.Combine(dir, state.name + ".png"));
            }

            // A real status (room, phase, core, carrier, players, entities) wraps to three lines: the row grows and Leave stays beside it.
            typeof(FreeformSpectatorSession).GetField("_room", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(session, "design-review studio B, third floor, west wing, the long room name a user typed");
            Set(session, "Phase", SpectatorPhase.Spectating);
            Set(session, "Notice", null);
            Set(session, "Error", null);
            overlay.Refresh();
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(overlay.Card);
            Shoot(cam, Path.Combine(dir, "spectator-in-room-long-status.png"));
        }

        private static void Set(object target, string property, object value)
        {
            target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .GetSetMethod(true).Invoke(target, new[] { value });
        }

        private static void Shoot(Camera cam, string path)
        {
            Canvas.ForceUpdateCanvases();
            var original = RenderTexture.active;
            var target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = target;
            cam.aspect = W / (float)H;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[UiAuditShots] -> " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
