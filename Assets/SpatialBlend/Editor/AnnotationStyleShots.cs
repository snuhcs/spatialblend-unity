// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Renders the annotation graphics in every flow state, over a neutral grey and over a room photo, in Play mode so the real
    /// components (BoxAnnotationMotif, BoxAnnotationInstance, AnnotationVisual) run their own Awake/LateUpdate. Needs a graphics device.
    /// Run: -executeMethod SpatialBlend.Editor.AnnotationStyleShots.Run -shotDir DIR [-photo FILE] [-states a,b] [-bgs grey,photo]
    /// (not -batchmode -nographics). The editor enters Play mode; the stepper below drives the frames and then exits.
    /// </summary>
    public static class AnnotationStyleShots
    {
        private const string Flag = "-annotationShots";
        private const int Width = 1320, Height = 800;

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        public static void Run()
        {
            Debug.Log("[AnnotationStyleShots] entering Play mode");
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-executeMethod") < 0) return;
            if (Arg("-executeMethod") != "SpatialBlend.Editor.AnnotationStyleShots.Run") return;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) Start();
            };
            if (EditorApplication.isPlaying) Start();
        }

        private static IEnumerator _routine;
        private static bool _started;

        private static void Start()
        {
            if (_started) return;
            _started = true;
            _routine = Main();
            EditorApplication.update += Step;
        }

        private static void Step()
        {
            try
            {
                if (_routine == null || !_routine.MoveNext())
                {
                    EditorApplication.update -= Step;
                    _routine = null;
                    Debug.Log("[AnnotationStyleShots] done");
                    EditorApplication.Exit(0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AnnotationStyleShots] " + e);
                EditorApplication.update -= Step;
                EditorApplication.Exit(1);
            }
        }

        // ---- scene state ----------------------------------------------------------------------------------------

        private static Camera _cam;
        private static readonly List<GameObject> _spawned = new List<GameObject>();
        private static Transform _controller;
        private static BoxAnnotationMotif _motif;
        private static string _dir;

        private static readonly Vector3 BoxSize = new Vector3(0.9f, 0.7f, 0.6f);
        private static readonly Vector3 BoxPos = new Vector3(0f, 0f, 4.3f);
        private static readonly Quaternion BoxRot = Quaternion.identity;   // the box annotation is axis-aligned (AABB)

        private static readonly string[] AllStates =
        {
            "1-aim", "2-first-point", "3-floor-rect", "4-height", "5-finished", "6-hover-box", "7-hover-corner", "8-selected-edit", "9-region", "10-real-anchor-and-box",
        };

        private static IEnumerator Main()
        {
            _dir = Arg("-shotDir") ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "annotation-shots");
            Directory.CreateDirectory(_dir);
            string photoPath = Arg("-photo");
            var stateFilter = Arg("-states");
            var bgFilter = Arg("-bgs");

            Texture2D photo = null;
            if (!string.IsNullOrEmpty(photoPath) && File.Exists(photoPath))
            {
                photo = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                photo.LoadImage(File.ReadAllBytes(photoPath));
                photo.wrapMode = TextureWrapMode.Clamp;
            }

            var states = new List<string>();
            foreach (var st in AllStates)
                if (stateFilter == null || Array.IndexOf(stateFilter.Split(','), st) >= 0) states.Add(st);
            var bgs = new List<string>();
            foreach (var bg in new[] { "grey", "photo" })
                if ((bgFilter == null || Array.IndexOf(bgFilter.Split(','), bg) >= 0) && (bg != "photo" || photo != null)) bgs.Add(bg);

            foreach (var bg in bgs)
            {
                BuildBackdrop(bg, photo);
                yield return null;
                foreach (var state in states)
                {
                    ClearSpawned();
                    BuildState(state);
                    for (int i = 0; i < 4; i++) yield return null;     // Start/LateUpdate of the real components
                    string folder = Path.Combine(_dir, bg);
                    Directory.CreateDirectory(folder);
                    Shoot(Path.Combine(folder, state + ".png"));
                }
                ClearSpawned();
                DestroyBackdrop();
            }
        }

        // ---- backdrop -------------------------------------------------------------------------------------------

        private static readonly List<GameObject> _backdrop = new List<GameObject>();

        private static void BuildBackdrop(string bg, Texture2D photo)
        {
            DestroyBackdrop();
            var camGo = new GameObject("ShotCam", typeof(Camera));
            camGo.tag = "MainCamera";
            _backdrop.Add(camGo);
            _cam = camGo.GetComponent<Camera>();
            _cam.enabled = false;
            _cam.fieldOfView = 27f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 80f;
            _cam.allowHDR = false;
            _cam.allowMSAA = true;
            _cam.transform.position = new Vector3(-1.5f, 1.45f, 0f);
            _cam.transform.LookAt(new Vector3(0f, 0.55f, 4.3f));
            AnnotationVisual.ViewCameraOverride = _cam;

            var lightGo = new GameObject("Light", typeof(Light));
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            _backdrop.Add(lightGo);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);

            if (bg == "grey")
            {
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0.50f, 0.51f, 0.53f, 1f);
                var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                floor.name = "Floor";
                UnityEngine.Object.Destroy(floor.GetComponent<Collider>());
                floor.transform.position = new Vector3(0f, -0.002f, 20f);
                floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                floor.transform.localScale = new Vector3(80f, 80f, 1f);
                var m = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.44f, 0.45f, 0.47f, 1f) };
                floor.GetComponent<Renderer>().sharedMaterial = m;
                _backdrop.Add(floor);
            }
            else
            {
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Color.black;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Photo";
                UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
                quad.transform.SetParent(_cam.transform, false);
                float dist = 60f;
                float h = 2f * dist * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                quad.transform.localPosition = new Vector3(0f, 0f, dist);
                quad.transform.localScale = new Vector3(h * Width / Height, h, 1f);
                var m = new Material(Shader.Find("Unlit/Texture")) { mainTexture = photo };
                // crop: the lower middle of the photo, where the herringbone floor is
                float cropH = 0.62f, cropW = cropH * (photo.height / (float)photo.width) * ((float)Width / Height);
                m.mainTextureScale = new Vector2(cropW, cropH);
                m.mainTextureOffset = new Vector2(0.22f, 0.0f);
                quad.GetComponent<Renderer>().sharedMaterial = m;
                _backdrop.Add(quad);
            }
        }

        private static void DestroyBackdrop()
        {
            foreach (var go in _backdrop) if (go != null) UnityEngine.Object.Destroy(go);
            _backdrop.Clear();
        }

        private static void ClearSpawned()
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
            _motif = null;
            _controller = null;
        }

        private static T Spawn<T>(T go) where T : UnityEngine.Object
        {
            if (go is GameObject g) _spawned.Add(g);
            else if (go is Component c) _spawned.Add(c.gameObject);
            return go;
        }

        // ---- reflection helpers into the real motif ----------------------------------------------------------------

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static void Set(object o, string name, object value)
        {
            var f = FindField(o.GetType(), name) ?? throw new MissingFieldException(o.GetType().Name, name);
            if (f.FieldType.IsEnum) value = Enum.ToObject(f.FieldType, value);
            f.SetValue(o, value);
        }

        private static object Get(object o, string name) => (FindField(o.GetType(), name) ?? throw new MissingFieldException(name)).GetValue(o);

        private static object Call(object o, string name, params object[] args)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(o, args);
            }
            throw new MissingMethodException(name);
        }

        private static BoxAnnotationMotif NewMotif()
        {
            var go = Spawn(new GameObject("BoxAnnotationMotif_Active"));
            var motif = go.AddComponent<BoxAnnotationMotif>();
            motif.enabled = false;   // its Update would chase the (absent) controller; the states are driven by hand
            _controller = Spawn(new GameObject("Controller")).transform;
            _controller.position = new Vector3(-1.0f, 1.15f, 0.9f);
            Set(motif, "_raycastAnchor", _controller);
            Set(motif, "_centerEyeAnchor", _cam.transform);
            return motif;
        }

        private static object NewPlane(BoxAnnotationMotif motif, Vector3 origin)
        {
            var t = typeof(BoxAnnotationMotif).GetNestedType("PlaneFrame", BindingFlags.NonPublic | BindingFlags.Public);
            return t.GetMethod("Build").Invoke(null, new object[] { origin, Vector3.up, _cam.transform });
        }

        private static Vector3 Corner(int i, Vector3 centre, Vector3 size, Quaternion rot)
        {
            float sx = (i == 1 || i == 2 || i == 5 || i == 6) ? 1f : -1f;
            float sz = (i == 2 || i == 3 || i == 6 || i == 7) ? 1f : -1f;
            return centre + rot * new Vector3(sx * size.x * 0.5f, i >= 4 ? size.y : 0f, sz * size.z * 0.5f);
        }

        private static BoxAnnotationInstance NewInstance(Vector3 pos, Quaternion rot, Vector3 size, bool register = true)
        {
            // The same fallback the motif builds when no prefab is assigned: an empty object with a cube "Visual".
            var go = Spawn(new GameObject("BoxAnnotation_0"));
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual";
            visual.transform.SetParent(go.transform, false);
            UnityEngine.Object.Destroy(visual.GetComponent<Collider>());
            var inst = go.AddComponent<BoxAnnotationInstance>();
            Set(inst, "_addManipulationPanelMotif", false);
            Set(inst, "_addBoxAnnotationPlacementMotif", false);
            inst.Initialize();
            inst.SetData(new Pose(pos, rot), size, Vector3.up);
            if (register && _motif != null) ((List<BoxAnnotationInstance>)Get(_motif, "_instances")).Add(inst);
            return inst;
        }

        // ---- the states ---------------------------------------------------------------------------------------------

        private static void BuildState(string state)
        {
            _motif = NewMotif();
            var p0 = BoxPos + BoxRot * new Vector3(-BoxSize.x * 0.5f, 0, -BoxSize.z * 0.5f);
            var p1 = BoxPos + BoxRot * new Vector3(BoxSize.x * 0.5f, 0, -BoxSize.z * 0.5f);
            var p2 = BoxPos + BoxRot * new Vector3(-BoxSize.x * 0.5f, 0, BoxSize.z * 0.5f);
            var p2b = BoxPos + BoxRot * new Vector3(BoxSize.x * 0.5f, 0, BoxSize.z * 0.5f);   // the far corner of the rectangle
            var centre = BoxPos;

            switch (state)
            {
                case "1-aim":
                {
                    var ground = BoxPos + new Vector3(0.05f, 0f, -0.35f);
                    {
                        var v = AnnotationVisual.Create(_motif.transform, "AimVisual");
                        v.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                        v.SetAim(_controller.position, ground, Vector3.right, Vector3.forward);
                    }
                    break;
                }
                case "2-first-point":
                {
                    Set(_motif, "_boxMode", 1);
                    Set(_motif, "_currentPointIndex", 1);
                    Set(_motif, "_plane", NewPlane(_motif, p0));
                    Set(_motif, "_p0", p0); Set(_motif, "_p1", p1); Set(_motif, "_p2", p1); Set(_motif, "_p3", p1);
                    Call(_motif, "RenderCurrentState"); Call(_motif, "RenderCurrentState");   // twice: the first call moves the motif transform
                    break;
                }
                case "3-floor-rect":
                {
                    Set(_motif, "_boxMode", 1);
                    Set(_motif, "_currentPointIndex", 2);
                    Set(_motif, "_plane", NewPlane(_motif, p0));
                    Set(_motif, "_p0", p0); Set(_motif, "_p1", p1); Set(_motif, "_p2", p2); Set(_motif, "_p3", p2);
                    Call(_motif, "RenderCurrentState"); Call(_motif, "RenderCurrentState");   // twice: the first call moves the motif transform
                    break;
                }
                case "4-height":
                {
                    Set(_motif, "_boxMode", 1);
                    Set(_motif, "_currentPointIndex", 3);
                    Set(_motif, "_plane", NewPlane(_motif, p0));
                    Set(_motif, "_p0", p0); Set(_motif, "_p1", p1); Set(_motif, "_p2", p2); Set(_motif, "_p3", p0 + Vector3.up * BoxSize.y);
                    Call(_motif, "RenderCurrentState"); Call(_motif, "RenderCurrentState");   // twice: the first call moves the motif transform
                    break;
                }
                case "5-finished":
                    NewInstance(BoxPos, BoxRot, BoxSize);
                    Set(_motif, "_boxMode", 0);
                    break;
                case "6-hover-box":
                case "7-hover-corner":
                {
                    NewInstance(BoxPos, BoxRot, BoxSize);
                    // aim the controller ray at the box centre, or at its top front corner
                    Vector3 target = state == "6-hover-box" ? BoxPos + Vector3.up * BoxSize.y * 0.5f : Corner(4, BoxPos, BoxSize, BoxRot);
                    var cp = _controller.position;
                    _controller.rotation = Quaternion.LookRotation(target - cp);
                    Set(_motif, "_boxMode", 0);
                    Call(_motif, "RenderCurrentState"); Call(_motif, "RenderCurrentState");   // twice: the first call moves the motif transform
                    break;
                }
                case "8-selected-edit":
                {
                    var inst = NewInstance(BoxPos, BoxRot, BoxSize);
                    Call(_motif, "SelectInstanceForEdit", inst, BoxPos);
                    Set(_motif, "_activePointIndex", 6);
                    Set(_motif, "_boxMode", 3);
                    Call(_motif, "RenderCurrentState"); Call(_motif, "RenderCurrentState");   // twice: the first call moves the motif transform
                    break;
                }
                case "9-region":
                    BuildRegion();
                    break;
                case "10-real-anchor-and-box":
                    BuildRealAnchorAndBox();
                    break;
            }
        }

        private static readonly Vector3[] RegionPoly =
        {
            new Vector3(-0.55f, 0, -0.35f), new Vector3(0.55f, 0, -0.35f), new Vector3(0.55f, 0, 0.1f), new Vector3(0.15f, 0, 0.1f), new Vector3(0.15f, 0, 0.42f), new Vector3(-0.55f, 0, 0.42f),
        };

        private static void BuildRegion()
        {
            var root = Spawn(new GameObject("Region"));
            root.transform.SetPositionAndRotation(BoxPos, BoxRot);
            var v = AnnotationVisual.Create(root.transform, "AnnotationVisual_Region");
            v.SetPrism(RegionPoly, Vector3.up, 0.45f, AnnotationEmphasis.Normal);
        }

        private static void BuildRealAnchorAndBox()
        {
            // An MRUK-style real-object anchor (a table volume) in the UiTheme RealAnchor colour, beside the annotation box.
            var tableSize = new Vector3(1.0f, 0.72f, 0.55f);
            var tablePos = BoxPos + new Vector3(1.25f, 0f, 0.35f);
            var amber = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor;   // the tutorial's real-anchor outline colour
            var look = AnnotationLook.Graphite();
            look.Edge = amber; look.EdgeHover = amber; look.FaceFill = amber;
            var root = Spawn(new GameObject("RealAnchor"));
            var v = AnnotationVisual.Create(root.transform, "RealAnchorVisual");
            v.Look = look;
            v.transform.SetPositionAndRotation(tablePos, Quaternion.identity);
            v.SetBox(Vector3.zero, Quaternion.identity, tableSize, AnnotationEmphasis.Normal);
            NewInstance(BoxPos + new Vector3(-0.35f, 0, 0), BoxRot, BoxSize, register: false);
        }

        // ---- capture ----------------------------------------------------------------------------------------------

        private static void Shoot(string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prev = RenderTexture.active;
            _cam.targetTexture = rt;
            _cam.Render();
            var resolved = RenderTexture.GetTemporary(Width, Height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(rt, resolved);
            RenderTexture.active = resolved;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[AnnotationStyleShots] wrote " + path);
            _cam.targetTexture = null;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(resolved);
            UnityEngine.Object.Destroy(tex);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }
    }
}
#endif
