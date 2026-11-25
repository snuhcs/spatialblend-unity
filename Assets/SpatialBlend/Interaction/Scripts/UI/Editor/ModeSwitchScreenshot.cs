// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SpatialBlend.Interactions.UI.Editor
{
    /// <summary>
    /// Batchmode capture of the three mode-switch widgets: the "Edit this object?" prompt over an object, the "Back to Interaction in N..."
    /// toast and the mode chip, each in a small staged room (the headset's view) and as a close-up. Run with
    /// <c>-executeMethod SpatialBlend.Interactions.UI.Editor.ModeSwitchScreenshot.Capture</c> and MODESWITCH_SHOT_DIR set; this needs a graphics
    /// device (no -nographics). It builds the real <see cref="ModeSwitchUi"/>, the one the assist drives, so the pictures are what the headset draws.
    /// </summary>
    public static class ModeSwitchScreenshot
    {
        private const int Width = 1600, Height = 900;

        public static void Capture()
        {
            string dir = System.Environment.GetEnvironmentVariable("MODESWITCH_SHOT_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "modeswitch-shots");
            Directory.CreateDirectory(dir);
            try
            {
                Run(dir);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[ModeSwitchScreenshot] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void Run(string dir)
        {
            Debug.Log("[ModeSwitchScreenshot] TMP default font: " +
                      (TMPro.TMP_Settings.defaultFontAsset != null ? TMPro.TMP_Settings.defaultFontAsset.name : "NONE (labels will be blank)"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // The app's theme and typeface, as the headset build installs them.
            SpatialBlend.Samples.FreeformCollaboration.UI.UiFonts.Register(
                AssetDatabase.LoadAssetAtPath<SpatialBlend.Samples.FreeformCollaboration.UI.UiFontSet>(SpatialBlend.Samples.FreeformCollaboration.UI.UiFonts.SetPath));
            SpatialBlend.Samples.FreeformCollaboration.UI.FreeformModeSwitchTheme.Install();

            var camGo = new GameObject("Eye", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.66f, 0.72f);
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            cam.allowHDR = false;
            cam.allowMSAA = false;

            Transform table = BuildRoom();
            camGo.transform.position = new Vector3(0f, 1.6f, -2.2f);
            camGo.transform.rotation = Quaternion.Euler(8f, 0f, 0f);

            var uiGo = new GameObject("ModeSwitchUi");
            var ui = uiGo.AddComponent<ModeSwitchUi>();
            ui.Build(camGo.transform);
            ui.SnapHud();

            // 1. Interaction mode, the ray rests on the table: the prompt floats above it, the chip says Interaction.
            ui.SetChipVisible(true);
            ui.SetChipMode(InteractionMode.Interaction);
            ui.ShowPrompt(table, new Bounds(new Vector3(0f, 0.55f, 0f), new Vector3(1.4f, 1.1f, 0.8f)), 0.15f);
            Shoot(cam, Path.Combine(dir, "01-hover-prompt.png"));
            CloseUp(cam, ui.PromptObject.transform, 0.62f, Path.Combine(dir, "02-hover-prompt-closeup.png"));
            ui.HidePrompt();

            // 2. Scene Edit, idle for ten seconds: the toast counts down, the chip says Editing.
            ui.SetChipMode(InteractionMode.SceneEdit);
            ui.ShowToast(3, 0.55f);
            Shoot(cam, Path.Combine(dir, "03-idle-countdown.png"));
            CloseUp(cam, ui.ToastObject.transform, 0.50f, Path.Combine(dir, "04-idle-countdown-closeup.png"));
            ui.HideToast();

            // 3. The chip alone in both modes.
            ui.SetChipMode(InteractionMode.Interaction);
            CloseUp(cam, ui.ChipObject.transform, 0.36f, Path.Combine(dir, "05-chip-interaction.png"));
            ui.SetChipMode(InteractionMode.SceneEdit);
            CloseUp(cam, ui.ChipObject.transform, 0.36f, Path.Combine(dir, "06-chip-editing.png"));

            // 4. The mode-change caption under the chip: into Editing, the idle return, and Editing on hands.
            ui.ShowCaption(ModeSwitchAssist.EditingCaption);
            Shoot(cam, Path.Combine(dir, "07-caption-editing.png"));
            CloseUp(cam, ui.CaptionObject.transform, 0.62f, Path.Combine(dir, "08-caption-editing-closeup.png"));
            ui.SetChipMode(InteractionMode.Interaction);
            ui.ShowCaption(ModeSwitchAssist.IdleReturnCaption);
            CloseUp(cam, ui.CaptionObject.transform, 0.62f, Path.Combine(dir, "09-caption-idle-return-closeup.png"));
            ui.SetChipMode(InteractionMode.SceneEdit);
            ui.ShowCaption(ModeSwitchAssist.HandsEditingCaption);
            Shoot(cam, Path.Combine(dir, "10-caption-hands.png"));
            CloseUp(cam, ui.CaptionObject.transform, 0.62f, Path.Combine(dir, "11-caption-hands-closeup.png"));
            ui.HideCaption();
        }

        private static Transform BuildRoom()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.position = new Vector3(0f, -0.05f, 2f);
            floor.transform.localScale = new Vector3(10f, 0.1f, 10f);
            Paint(floor, new Color(0.36f, 0.38f, 0.42f));
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.position = new Vector3(0f, 1.5f, 5.5f);
            wall.transform.localScale = new Vector3(10f, 3f, 0.1f);
            Paint(wall, new Color(0.78f, 0.80f, 0.84f));

            var table = new GameObject("Table").transform;
            var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
            top.transform.SetParent(table, false);
            top.transform.localPosition = new Vector3(0f, 1.05f, 0f);
            top.transform.localScale = new Vector3(1.4f, 0.06f, 0.8f);
            Paint(top, new Color(0.78f, 0.62f, 0.44f));
            foreach (var corner in new[] { new Vector2(-0.62f, -0.32f), new Vector2(0.62f, -0.32f), new Vector2(-0.62f, 0.32f), new Vector2(0.62f, 0.32f) })
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leg.transform.SetParent(table, false);
                leg.transform.localPosition = new Vector3(corner.x, 0.51f, corner.y);
                leg.transform.localScale = new Vector3(0.06f, 1.02f, 0.06f);
                Paint(leg, new Color(0.60f, 0.46f, 0.32f));
            }
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.transform.SetParent(table, false);
            board.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            board.transform.localScale = new Vector3(0.5f, 0.04f, 0.5f);
            Paint(board, new Color(0.22f, 0.26f, 0.30f));
            table.position = Vector3.zero;
            return table;
        }

        private static void Paint(GameObject go, Color color)
        {
            Shader shader = Shader.Find("Unlit/Color");
            var material = new Material(shader) { color = color };
            go.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(go.GetComponent<Collider>());
        }

        private static void CloseUp(Camera cam, Transform widget, float distance, string path)
        {
            Vector3 position = cam.transform.position;
            Quaternion rotation = cam.transform.rotation;
            float fov = cam.fieldOfView;
            Vector3 toCamera = (cam.transform.position - widget.position).normalized;
            cam.transform.position = widget.position + toCamera * distance * Mathf.Max(1f, widget.lossyScale.x * 1000f);
            cam.transform.rotation = Quaternion.LookRotation(widget.position - cam.transform.position, Vector3.up);
            cam.fieldOfView = 40f;
            Shoot(cam, path);
            cam.transform.SetPositionAndRotation(position, rotation);
            cam.fieldOfView = fov;
        }

        private static void Shoot(Camera cam, string path)
        {
            Canvas.ForceUpdateCanvases();
            RenderTexture original = RenderTexture.active;
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = target;
            cam.aspect = Width / (float)Height;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[ModeSwitchScreenshot] -> " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
