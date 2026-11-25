// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// Marks everything the tutorial creates in the scene. A practice object is local only: it has no <c>SbEntity</c>, is never
    /// registered with the core and is never shared. The tutorial destroys all of them when it ends.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class TutorialPracticeObject : MonoBehaviour
    {
    }

    /// <summary>A highlight outline around an object the user has to use: the 12 edges of its bounds in the Graphite accent.</summary>
    [AddComponentMenu("")]
    public sealed class TutorialOutline : MonoBehaviour
    {
        public const float Margin = 0.015f;

        private Transform _target;
        private LineRenderer _line;
        private Material _material;

        public Transform Target => _target;

        public static TutorialOutline Create(Transform target, Material material)
        {
            var go = new GameObject("TutorialPracticeOutline");
            go.AddComponent<TutorialPracticeObject>();
            var outline = go.AddComponent<TutorialOutline>();
            outline._target = target;
            outline._material = material;
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.widthMultiplier = 0.01f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = material;
            line.positionCount = 16;
            outline._line = line;
            outline.Follow();
            return outline;
        }

        /// <summary>The world bounds of the target: its renderers, or its collider while it has none yet (a model still loading).</summary>
        public static Bounds BoundsOf(Transform target)
        {
            Bounds? bounds = null;
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (renderer is LineRenderer || renderer is TrailRenderer) continue;
                if (bounds == null) bounds = renderer.bounds; else { var b = bounds.Value; b.Encapsulate(renderer.bounds); bounds = b; }
            }
            if (bounds == null)
            {
                var collider = target.GetComponentInChildren<Collider>();
                bounds = collider != null ? collider.bounds : new Bounds(target.position, Vector3.one * 0.2f);
            }
            return bounds.Value;
        }

        private void LateUpdate()
        {
            Follow();
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 3.2f);
            var accent = UiTheme.Palette.Accent;
            if (_material != null) _material.color = new Color(accent.r, accent.g, accent.b, 0.65f + 0.35f * pulse);
        }

        internal void Follow()
        {
            if (_target == null || _line == null) return;
            var b = BoundsOf(_target);
            b.Expand(Margin * 2f);
            Vector3 min = b.min, max = b.max;
            Vector3 b0 = new Vector3(min.x, min.y, min.z), b1 = new Vector3(max.x, min.y, min.z), b2 = new Vector3(max.x, min.y, max.z), b3 = new Vector3(min.x, min.y, max.z);
            Vector3 t0 = new Vector3(min.x, max.y, min.z), t1 = new Vector3(max.x, max.y, min.z), t2 = new Vector3(max.x, max.y, max.z), t3 = new Vector3(min.x, max.y, max.z);
            // One path over all 12 edges (three edges are walked twice).
            var path = new[] { b0, b1, b2, b3, b0, t0, t1, b1, t1, t2, b2, t2, t3, b3, t3, t0 };
            for (int i = 0; i < path.Length; i++) _line.SetPosition(i, path[i]);
        }
    }

    /// <summary>
    /// A practice object the user can point at and carry along the ray while the trigger (or a pinch) is held. It listens to the
    /// scene ray interactor the way the placement motifs do and reports the grab and where it was let go.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class TutorialGrabbable : MonoBehaviour
    {
        private RayInteractable _ray;
        private EditPointer _pointer;
        private bool _subscribed;
        private float _grabDistance;

        public bool Held { get; private set; }
        public event Action Grabbed;
        /// <summary>Raised on release with the object's position at that moment.</summary>
        public event Action<Vector3> Released;
        /// <summary>The object's own ray interactable (what the scene's interactor reports when it is selected).</summary>
        public RayInteractable Interactable => _ray;

        /// <summary>Makes <paramref name="go"/> (which needs a collider) selectable with a ray or a pinch and carriable.</summary>
        public static TutorialGrabbable Attach(GameObject go)
        {
            var grabbable = go.AddComponent<TutorialGrabbable>();
            var collider = go.GetComponent<Collider>();
            var surface = go.AddComponent<ColliderSurface>();
            surface.InjectCollider(collider);
            grabbable._ray = go.AddComponent<RayInteractable>();
            grabbable._ray.InjectSurface(surface);
            return grabbable;
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();

        private void Update()
        {
            if (!Held || _pointer == null) return;
            var target = _pointer.Origin + _pointer.Forward * _grabDistance;
            transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * 15f);
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            // The active editing input: the right controller's ray or the right hand's pinch.
            EditInput.Selected += OnSelected;
            EditInput.Unselected += OnUnselected;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            EditInput.Selected -= OnSelected;
            EditInput.Unselected -= OnUnselected;
        }

        private void OnSelected(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable == _ray) _pointer = pointer;
            HandleSelected(interactable);
        }

        private void OnUnselected(EditPointer pointer, RayInteractable interactable) => HandleUnselected(interactable);

        internal void HandleSelected(RayInteractable interactable)
        {
            if (interactable != _ray) return;
            var pointer = _pointer ?? EditInput.ActivePointer;
            _grabDistance = pointer != null ? Vector3.Distance(pointer.Origin, transform.position) : 0f;
            Held = true;
            Grabbed?.Invoke();
        }

        internal void HandleUnselected(RayInteractable interactable)
        {
            if (interactable != _ray || !Held) return;
            Held = false;
            Released?.Invoke(transform.position);
        }
    }

    /// <summary>Builds and tracks the practice props. Everything it makes is a <see cref="TutorialPracticeObject"/>.</summary>
    public sealed class TutorialPracticeProps
    {
        public const float CubeSize = 0.22f;
        /// <summary>A thing counts as in view when it is within this angle of the gaze (the boards' direction hint).</summary>
        public const float SeenDegrees = 25f;

        private readonly List<GameObject> _owned = new List<GameObject>();
        private readonly List<GameObject> _targets = new List<GameObject>();
        private readonly List<Material> _materials = new List<Material>();
        private TutorialGrabbable _cube, _copy, _shared, _hologram;
        private Material _glowLine;

        public TutorialGrabbable Cube => _cube;
        public TutorialGrabbable Copy => _copy;
        /// <summary>The object that is not in the user's room, created as a virtual object to place (a lamp: not the scanned vase).</summary>
        public TutorialGrabbable Shared => _shared;
        public int OwnedCount { get { Prune(); return _owned.Count; } }
        public int TargetCount { get { Prune(); return _targets.Count; } }

        /// <summary>A spot ahead of the viewer at chest height, along the horizontal gaze. With no viewer, one metre in front of the origin.</summary>
        public static Vector3 SpotAhead(Transform eye, float distance, float drop = 0.35f, float side = 0f)
        {
            if (eye == null) return new Vector3(side, 1.2f, distance);
            var flat = FlatForward(eye.position, eye.forward);
            var right = Vector3.Cross(Vector3.up, flat);
            return eye.position + flat * distance + right * side + Vector3.down * drop;
        }

        public static Vector3 FlatForward(Vector3 eyePosition, Vector3 forward)
        {
            var flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            return flat.normalized;
        }

        /// <summary>The signed angle (degrees, positive to the right) from the gaze to a point, on the floor plane.</summary>
        public static float YawTo(Vector3 eyePosition, Vector3 eyeForward, Vector3 point)
        {
            var flat = FlatForward(eyePosition, eyeForward);
            var to = new Vector3(point.x - eyePosition.x, 0f, point.z - eyePosition.z);
            if (to.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(flat, to.normalized, Vector3.up);
        }

        /// <summary>True when the point is in front of the viewer, within <paramref name="degrees"/> of the gaze in every direction.</summary>
        public static bool IsInView(Vector3 eyePosition, Vector3 eyeForward, Vector3 point, float degrees = SeenDegrees)
        {
            var to = point - eyePosition;
            if (to.sqrMagnitude < 1e-6f) return true;
            return Vector3.Angle(eyeForward, to) <= degrees;
        }

        /// <summary>A short subtle hint on which way to turn, or null when the point is already in view.</summary>
        public static string DirectionHint(Vector3 eyePosition, Vector3 eyeForward, Vector3 point)
        {
            if (IsInView(eyePosition, eyeForward, point)) return null;
            float yaw = YawTo(eyePosition, eyeForward, point);
            if (Mathf.Abs(yaw) > 140f) return "Try turning around";
            return yaw > 0f ? "Try looking to your right" : "Try looking to your left";
        }

        private Material GlowLine()
        {
            if (_glowLine != null) return _glowLine;
            var material = new Material(Shader.Find("Sprites/Default")) { name = "TutorialGlowLine" };
            material.color = UiTheme.Palette.Accent;
            _materials.Add(material);
            _glowLine = material;
            return material;
        }

        public TutorialGrabbable EnsureCube(Transform eye)
        {
            if (_cube != null) return _cube;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "TutorialPracticeCube";
            go.transform.position = SpotAhead(eye, 1.0f, 0.35f, -0.3f);
            go.transform.localScale = Vector3.one * CubeSize;
            go.AddComponent<TutorialPracticeObject>();
            UseOccludedMaterial(go);
            Tint(go, UiTheme.Palette.Accent);
            _cube = TutorialGrabbable.Attach(go);
            _owned.Add(go);
            return _cube;
        }

        /// <summary>Draws the accent highlight outline around an object, until the targets are cleared.</summary>
        public TutorialOutline Highlight(GameObject target)
        {
            var outline = TutorialOutline.Create(target.transform, GlowLine());
            _targets.Add(outline.gameObject);
            return outline;
        }

        /// <summary>Adds a marker the tutorial drew (an example box) to the targets, so a success or the end of the task removes it.</summary>
        public GameObject AddTarget(GameObject go)
        {
            if (go == null) return null;
            if (go.GetComponent<TutorialPracticeObject>() == null) go.AddComponent<TutorialPracticeObject>();
            _targets.Add(go);
            return go;
        }

        /// <summary>Hides every outline and pending box (success), keeping the objects.</summary>
        public void ClearTargets()
        {
            foreach (var item in _targets.ToArray()) if (item != null) Destroy(item);
            _targets.Clear();
        }

        public void DestroyCube()
        {
            if (_cube == null) return;
            _owned.Remove(_cube.gameObject);
            Destroy(_cube.gameObject);
            _cube = null;
        }

        /// <summary>
        /// The practice vase that stands in for the real object in the user's box during the scan (the canned server cannot
        /// segment the user's own object): a holder on the box's base, sized to the box; the caller loads the model into it.
        /// </summary>
        public TutorialGrabbable EnsureHologram(Vector3 basePosition, Vector3 boxSize)
        {
            if (_hologram != null) return _hologram;
            float side = Mathf.Min(boxSize.x, boxSize.z) * 0.8f, height = boxSize.y * 0.85f;
            // The holder stands on the base (the sample model's origin is its foot); its body and collider rise from there.
            _hologram = MakeModelHolder("TutorialHologramVase", basePosition, new Vector3(side, height, side));
            _hologram.GetComponent<BoxCollider>().center = Vector3.up * height * 0.5f;
            AddPlaceholderBody(_hologram.gameObject, new Vector3(side, height, side)).transform.localPosition = Vector3.up * height * 0.5f;
            return _hologram;
        }

        public TutorialGrabbable Hologram => _hologram;

        public void DestroyHologram()
        {
            if (_hologram == null) return;
            _owned.Remove(_hologram.gameObject);
            Destroy(_hologram.gameObject);
            _hologram = null;
        }

        public void DestroyCopy()
        {
            if (_copy == null) return;
            _owned.Remove(_copy.gameObject);
            Destroy(_copy.gameObject);
            _copy = null;
        }

        /// <summary>The practice 3D copy: the same kind of holder, made where the scan result appears.</summary>
        public TutorialGrabbable EnsureCopy(Vector3 position)
        {
            if (_copy != null) return _copy;
            _copy = MakeModelHolder("TutorialScanCopy", position, new Vector3(0.3f, 0.3f, 0.3f));
            return _copy;
        }

        public void DestroyShared()
        {
            if (_shared == null) return;
            _owned.Remove(_shared.gameObject);
            Destroy(_shared.gameObject);
            _shared = null;
        }

        /// <summary>The virtual object for an object that is not in the user's room: the same kind of holder, with its own name.</summary>
        public TutorialGrabbable EnsureShared(Vector3 position)
        {
            if (_shared != null) return _shared;
            _shared = MakeModelHolder("TutorialVirtualObject", position, new Vector3(SharedSize.x, SharedSize.y, SharedSize.z));
            return _shared;
        }

        /// <summary>The size of the box drawn around the virtual object and of its collider until the model has loaded.</summary>
        public static readonly Vector3 SharedSize = new Vector3(0.26f, 0.3f, 0.26f);

        /// <summary>
        /// The pending box of an object that arrives with no counterpart in the room (the add-as-new look): the box of a shared real
        /// object (<see cref="SharedAnchorBox"/>, <see cref="UiTheme.Palette"/> SharedReal), drawn around the object in its own
        /// frame. It goes with the targets when the object is placed.
        /// </summary>
        public GameObject AddRealAnchorBox(GameObject target, Vector3 size)
        {
            // The box a joiner still has to place, as the app draws it (SharedAnchorBox, the emphasised look), built at its real size
            // (the brackets are metres) and built again around the model once it has loaded (FitRealAnchorBox).
            var frame = new GameObject(RealAnchorBoxName);
            frame.AddComponent<TutorialPracticeObject>();
            frame.transform.SetParent(target.transform, false);
            BuildBoxIn(frame.transform, target.transform.position, size);
            _targets.Add(frame);
            return frame;
        }

        public const string RealAnchorBoxName = "TutorialRealAnchorBox";

        // The box inside `frame` (a child of the object), at `worldCenter` and `worldSize` in metres: the frame is unscaled in the world
        // whatever scale the object was given, so the brackets keep the scan box's lengths.
        private static void BuildBoxIn(Transform frame, Vector3 worldCenter, Vector3 worldSize)
        {
            for (int i = frame.childCount - 1; i >= 0; --i)
            {
                GameObject old = frame.GetChild(i).gameObject;
                if (Application.isPlaying) { old.SetActive(false); UnityEngine.Object.Destroy(old); } else UnityEngine.Object.DestroyImmediate(old);
            }
            Transform holder = frame.parent;
            Vector3 s = holder != null ? holder.lossyScale : Vector3.one;
            frame.SetPositionAndRotation(worldCenter, holder != null ? holder.rotation : Quaternion.identity);
            frame.localScale = new Vector3(1f / Mathf.Max(1e-4f, s.x), 1f / Mathf.Max(1e-4f, s.y), 1f / Mathf.Max(1e-4f, s.z));
            SharedAnchorBox.BuildEmphasis(frame, Vector3.zero, worldSize, "Box", true);
        }

        /// <summary>
        /// Wraps the pending box around the loaded model (what the real box does around a measured object): centred on the model's bounds, in the
        /// holder's frame, with a 1 cm margin, whatever scale the holder was given to fit the model. The wire itself is not part of the bounds.
        /// </summary>
        public static void FitRealAnchorBox(GameObject holder)
        {
            var box = holder.transform.Find(RealAnchorBoxName);
            if (box == null) return;
            Bounds? bounds = null;
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>())
            {
                if (renderer is LineRenderer || renderer is TrailRenderer || renderer is ParticleSystemRenderer || renderer.transform.IsChildOf(box)) continue;
                if (bounds == null) bounds = renderer.bounds; else { var b = bounds.Value; b.Encapsulate(renderer.bounds); bounds = b; }
            }
            if (bounds == null) return;
            BuildBoxIn(box, bounds.Value.center, bounds.Value.size + Vector3.one * 0.02f);
        }

        private static bool IsInRealAnchorBox(Transform t, Transform holder)
        {
            Transform box = holder.Find(RealAnchorBoxName);
            return box != null && t.IsChildOf(box);
        }

        private TutorialGrabbable MakeModelHolder(string name, Vector3 position, Vector3 colliderSize)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            go.AddComponent<TutorialPracticeObject>();
            var box = go.AddComponent<BoxCollider>();
            box.size = colliderSize;
            var grabbable = TutorialGrabbable.Attach(go);
            _owned.Add(go);
            return grabbable;
        }

        /// <summary>Gives a model holder a plain body, for when the model cannot be loaded (and while it loads).</summary>
        public static GameObject AddPlaceholderBody(GameObject holder, Vector3 size)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "PlaceholderBody";
            body.transform.SetParent(holder.transform, false);
            body.transform.localScale = new Vector3(size.x * 0.8f, size.y * 0.5f, size.z * 0.8f);
            var collider = body.GetComponent<Collider>();
            if (Application.isPlaying) UnityEngine.Object.Destroy(collider); else UnityEngine.Object.DestroyImmediate(collider);
            UseOccludedMaterial(body);
            Tint(body, new Color(0.85f, 0.8f, 0.7f));
            return body;
        }

        /// <summary>Fits the holder's collider to the model's renderers once it is loaded.</summary>
        public static void FitCollider(GameObject holder)
        {
            var box = holder.GetComponent<BoxCollider>();
            if (box == null) return;
            Bounds? measured = null;
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>())
            {
                // The box of a virtual object is drawn around the model, it is not part of it.
                if (renderer is LineRenderer || IsInRealAnchorBox(renderer.transform, holder.transform)) continue;
                if (measured == null) measured = renderer.bounds; else { var b = measured.Value; b.Encapsulate(renderer.bounds); measured = b; }
            }
            if (measured == null) return;
            Bounds bounds = measured.Value;
            var scale = holder.transform.lossyScale;
            box.center = holder.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(bounds.size.x / Mathf.Max(1e-4f, scale.x), bounds.size.y / Mathf.Max(1e-4f, scale.y), bounds.size.z / Mathf.Max(1e-4f, scale.z));
        }

        /// <summary>Takes ownership of a scene object the tutorial made, so DestroyAll removes it.</summary>
        public GameObject Adopt(GameObject go)
        {
            if (go != null)
            {
                if (go.GetComponent<TutorialPracticeObject>() == null) go.AddComponent<TutorialPracticeObject>();
                _owned.Add(go);
            }
            return go;
        }

        public void DestroyAll()
        {
            ClearTargets();
            foreach (var item in _owned.ToArray()) if (item != null) Destroy(item);
            _owned.Clear();
            _cube = _copy = _shared = _hologram = null;
            // Anything marked as a practice object that was not tracked (a scan result that finished late) goes as well.
            foreach (var marker in UnityEngine.Object.FindObjectsByType<TutorialPracticeObject>(FindObjectsSortMode.None))
                if (marker != null) Destroy(marker.gameObject);
            foreach (var material in _materials) if (material != null) Destroy(material);
            _materials.Clear();
            _glowLine = null;
        }

        /// <summary>
        /// The practice cube and the vase's placeholder body are virtual objects standing in the user's room, so they use
        /// the Meta occlusion shader and a real table or chair hides them as it does a placed object. (The outlines are
        /// markers and stay visible, like the UI.)
        /// </summary>
        public static void UseOccludedMaterial(GameObject go)
        {
            var renderer = go.GetComponent<Renderer>();
            var material = OccludedMaterial;
            if (renderer != null && material != null) renderer.sharedMaterial = material;
        }

        /// <summary>Meta's built-in-pipeline occlusion shader; the chessboard and table materials reference it, so the build includes it.</summary>
        public const string OccludedShaderName = "Meta/Depth/BiRP/Occlusion Standard";

        private static Material s_occluded;

        /// <summary>One shared material for the practice props (the colour is a per-renderer property block). Null when the shader is not in the build.</summary>
        public static Material OccludedMaterial
        {
            get
            {
                if (s_occluded != null) return s_occluded;
                var shader = Shader.Find(OccludedShaderName);
                if (shader == null) return null;
                s_occluded = new Material(shader) { name = "TutorialPracticeOccluded", hideFlags = HideFlags.DontSave };
                s_occluded.SetFloat("_EnvironmentDepthBias", 0.06f);
                return s_occluded;
            }
        }

        /// <summary>Colours a primitive without instancing its material (a material instance leaks in the Editor): a property block for both the URP and the built-in colour property.</summary>
        public static void Tint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }

        private void Prune()
        {
            _owned.RemoveAll(item => item == null);
            _targets.RemoveAll(item => item == null);
        }

        private static void Destroy(UnityEngine.Object target)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
