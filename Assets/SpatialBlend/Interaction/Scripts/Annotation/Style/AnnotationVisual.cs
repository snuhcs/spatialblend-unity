// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>One line segment the visual draws, in world space (what tests and tools read back).</summary>
    public readonly struct AnnotationSegment
    {
        public readonly Vector3 A, B;
        public readonly Color ColorA, ColorB;
        public readonly float WidthScale;
        public AnnotationSegment(Vector3 a, Vector3 b, Color colorA, Color colorB, float widthScale)
        {
            A = a; B = b; ColorA = colorA; ColorB = colorB; WidthScale = widthScale;
        }
    }

    /// <summary>
    /// Draws the annotation graphics in the <see cref="AnnotationLook"/>: constant-pixel-width anti-aliased lines with a dark
    /// casing and a soft glow (one quad per segment, one draw call), L-shaped corner brackets as the corner and handle marker,
    /// and translucent faces (a scaled cube, two passes). Retained mode: call <see cref="Clear"/> and the Set/Add methods when
    /// the content changes; the mesh is rebuilt in LateUpdate when the content, the transform or the viewer moved, because
    /// hidden-edge classification and the near-plane clip depend on the eye.
    /// All positions are in this component's local space: parent it where the content lives (the box instance root, the region
    /// object), or at the world origin for the motif previews.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnnotationVisual : MonoBehaviour
    {
        public const string LineShaderName = "Sb/Annotation/Line";
        public const string FaceShaderName = "Sb/Annotation/Face";

        private const float NearPlane = 0.06f;

        /// <summary>Optional: the camera the editor tools and tests view from; otherwise Camera.main (the centre eye on the headset).</summary>
        public static Camera ViewCameraOverride;
        public static Camera ViewCamera => ViewCameraOverride != null ? ViewCameraOverride : Camera.main;

        private enum Kind { Segment, PointMarker, FloorRect, Box, Prism }

        private struct Prim
        {
            public Kind kind;
            public Vector3 a, b, c, size;
            public Quaternion rot;
            public AnnotationEmphasis emphasis;
            public CornerFocus focus;
            public int index;
            public float f;
            public Color colorA, colorB;
        }

        private AnnotationLook _look = AnnotationLook.Graphite();
        private readonly List<Prim> _prims = new List<Prim>(16);
        private readonly List<Vector3> _prismPoints = new List<Vector3>(16);
        private readonly List<AnnotationSegment> _segments = new List<AnnotationSegment>(64);
        private bool _dirty = true;
        private float _alpha = 1f;

        private Mesh _lineMesh;
        private MeshRenderer _lineRenderer, _boxFace, _floorFace;
        private Transform _boxFaceT, _floorFaceT;
        private Material _lineMat, _faceMat;
        private MaterialPropertyBlock _mpb;

        private readonly List<Vector3> _vPos = new List<Vector3>(128), _vOther = new List<Vector3>(128);
        private readonly List<Vector2> _vSide = new List<Vector2>(128), _vInfo = new List<Vector2>(128);
        private readonly List<Color> _vCol = new List<Color>(128);
        private readonly List<int> _tri = new List<int>(256);

        private Vector3 _lastEye;
        private Quaternion _lastEyeRot;
        private Camera _cam;
        private Vector3 _eyeWorld;
        private Matrix4x4 _lw;
        private readonly bool[] _facing = new bool[6];
        private readonly Vector3[] _boxLocal = new Vector3[8], _boxWorld = new Vector3[8], _ringBuf = new Vector3[4];
        private Vector3 _segMin, _segMax;

        private static Mesh s_UnitCube;

        public static AnnotationVisual Create(Transform parent, string name = "AnnotationVisual")
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<AnnotationVisual>();
        }

        /// <summary>The look. Replace it to draw another kind of annotation (for example a real-anchor outline in its own colour).</summary>
        public AnnotationLook Look
        {
            get => _look;
            set
            {
                _look = value ?? AnnotationLook.Graphite();
                if (_lineMat != null) DestroyObject(_lineMat);
                if (_faceMat != null) DestroyObject(_faceMat);
                _lineMat = null; _faceMat = null; _dirty = true;
            }
        }

        /// <summary>Overall opacity multiplier (the region volume fades with it).</summary>
        public float Alpha
        {
            get => _alpha;
            set { if (!Mathf.Approximately(_alpha, value)) { _alpha = value; _dirty = true; } }
        }

        /// <summary>The segments drawn at the last <see cref="Refresh"/>, in world space.</summary>
        public IReadOnlyList<AnnotationSegment> Segments => _segments;
        public Material LineMaterial => _lineMat;
        public Material FaceMaterial => _faceMat;
        public bool BoxFaceVisible => _boxFace != null && _boxFace.enabled;
        public bool FloorFaceVisible => _floorFace != null && _floorFace.enabled;
        /// <summary>The fill alpha the box faces were last drawn with (grows with hover and selection).</summary>
        public float BoxFaceFillAlpha { get; private set; }

        // =============================================================================================================
        // Content
        // =============================================================================================================

        public void Clear()
        {
            if (_prims.Count == 0 && _prismPoints.Count == 0) return;
            _prims.Clear();
            _prismPoints.Clear();
            _dirty = true;
        }

        /// <summary>
        /// The aiming marker on a surface (a square of corner brackets lying on it) and, optionally, a soft ray from the controller.
        /// <paramref name="right"/> and <paramref name="forward"/> orient the square in the surface plane.
        /// </summary>
        public void SetAim(Vector3? rayOrigin, Vector3 surfacePoint, Vector3 right, Vector3 forward)
        {
            if (rayOrigin.HasValue && _look.AimRayAlpha > 0f)
            {
                var c0 = _look.Edge; c0.a = 0f;
                var c1 = _look.Edge; c1.a = _look.AimRayAlpha;
                _prims.Add(new Prim { kind = Kind.Segment, a = rayOrigin.Value, b = surfacePoint, colorA = c0, colorB = c1, f = 0.7f });
            }
            AddPointMarker(surfacePoint, right, forward, CornerFocus.Hover, _look.AimMarkerSide);
        }

        /// <summary>A lone point (no edges yet): a small square of corner brackets lying on the surface.</summary>
        public void AddPointMarker(Vector3 point, Vector3 right, Vector3 forward, CornerFocus focus, float side = 0f)
        {
            if (side <= 0f) side = focus == CornerFocus.Active ? _look.ActivePointMarkerSide : _look.PointMarkerSide;
            _prims.Add(new Prim { kind = Kind.PointMarker, a = point, b = right.normalized, c = forward.normalized, f = side, focus = focus });
            _dirty = true;
        }

        /// <summary>A plain segment between two points (for example the first side while the second point is still being placed).</summary>
        public void AddSegment(Vector3 a, Vector3 b, AnnotationEmphasis emphasis = AnnotationEmphasis.Normal)
        {
            var c = _look.EdgeColor(emphasis);
            _prims.Add(new Prim { kind = Kind.Segment, a = a, b = b, colorA = c, colorB = c, f = _look.EdgeScale(emphasis), emphasis = emphasis });
            _dirty = true;
        }

        /// <summary>
        /// The floor rectangle from the first two points and the depth point (p2 is perpendicular to p0-p1), with a bracket at each
        /// corner. Corners are numbered p0, p1, p1+p2-p0, p2; <paramref name="focusCorner"/> brightens one.
        /// </summary>
        public void SetFloorRect(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 up, int focusCorner = -1, CornerFocus focus = CornerFocus.None)
        {
            _prims.Add(new Prim { kind = Kind.FloorRect, a = p0, b = p1, c = p2, size = up, index = focusCorner, focus = focus });
            _dirty = true;
        }

        /// <summary>
        /// A box: <paramref name="bottomCentre"/> is the centre of its base, <paramref name="size"/> is (width, height, depth).
        /// All 12 edges are drawn, with a bracket at every corner; <paramref name="focusCorner"/> (0..7) brightens one.
        /// </summary>
        public void SetBox(Vector3 bottomCentre, Quaternion rotation, Vector3 size, AnnotationEmphasis emphasis, int focusCorner = -1, CornerFocus focus = CornerFocus.None)
        {
            _prims.Add(new Prim { kind = Kind.Box, a = bottomCentre, rot = rotation, size = size, emphasis = emphasis, index = focusCorner, focus = focus });
            _dirty = true;
        }

        /// <summary>A prism over a polygon (the region volume): bottom and top outlines, vertical edges, a bracket at every vertex.</summary>
        public void SetPrism(IList<Vector3> polygon, Vector3 up, float height, AnnotationEmphasis emphasis, int focusVertex = -1, CornerFocus focus = CornerFocus.None)
        {
            _prismPoints.Clear();
            for (int i = 0; i < polygon.Count; i++) _prismPoints.Add(polygon[i]);
            _prims.Add(new Prim { kind = Kind.Prism, a = up.normalized, f = height, emphasis = emphasis, index = focusVertex, focus = focus });
            _dirty = true;
        }

        // =============================================================================================================
        // Unity
        // =============================================================================================================

        private void OnEnable() { _dirty = true; }

        private void OnDisable() { SetRenderersEnabled(false); }

        private void OnDestroy()
        {
            if (_lineRenderer != null) DestroyObject(_lineRenderer.gameObject);
            if (_lineMesh != null) DestroyObject(_lineMesh);
            if (_lineMat != null) DestroyObject(_lineMat);
            if (_faceMat != null) DestroyObject(_faceMat);
        }

        private static void DestroyObject(Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        private void LateUpdate()
        {
            var cam = ViewCamera;
            Vector3 eye = cam != null ? cam.transform.position : transform.position - Vector3.forward;
            Quaternion eyeRot = cam != null ? cam.transform.rotation : Quaternion.identity;
            bool moved = (eye - _lastEye).sqrMagnitude > 1e-6f || Quaternion.Angle(eyeRot, _lastEyeRot) > 0.1f;
            if (!_dirty && !moved && !transform.hasChanged) return;
            Refresh();
        }

        /// <summary>Rebuilds the meshes now (LateUpdate does this when something changed; tests call it directly).</summary>
        public void Refresh()
        {
            _cam = ViewCamera;
            _eyeWorld = _cam != null ? _cam.transform.position : transform.position - Vector3.forward;
            _lastEye = _eyeWorld;
            _lastEyeRot = _cam != null ? _cam.transform.rotation : Quaternion.identity;
            transform.hasChanged = false;
            _dirty = false;
            EnsureObjects();
            if (_lineMat == null || _faceMat == null) return;
            Build();
        }

        // =============================================================================================================
        // Objects
        // =============================================================================================================

        private void EnsureObjects()
        {
            if (_lineMesh == null)
            {
                _lineMesh = new Mesh { name = "AnnotationLines", indexFormat = IndexFormat.UInt16, hideFlags = HideFlags.DontSave };
                _lineMesh.MarkDynamic();
                _mpb = new MaterialPropertyBlock();
                _lineRenderer = MakeRenderer("Lines", _lineMesh);
                // At the scene root with an identity transform: the mesh is in world space, and a scaled or rotated parent would
                // shear its object matrix (which the depth-occlusion keywords read).
                _lineRenderer.transform.SetParent(null, false);
                s_UnitCube = s_UnitCube != null ? s_UnitCube : BuildUnitCube();
                _boxFace = MakeRenderer("BoxFace", s_UnitCube);
                _boxFaceT = _boxFace.transform;
                _floorFace = MakeRenderer("FloorFace", s_UnitCube);
                _floorFaceT = _floorFace.transform;
            }
            if (_lineMat != null && _faceMat != null) return;

            var lineShader = Shader.Find(LineShaderName);
            var faceShader = Shader.Find(FaceShaderName);
            if (lineShader == null || faceShader == null)
            {
                Debug.LogError($"[AnnotationVisual] shader not found ({LineShaderName} / {FaceShaderName}): it is not in Always Included Shaders");
                return;
            }
            _lineMat = new Material(lineShader) { name = "AnnotationLine (runtime)", hideFlags = HideFlags.DontSave };
            _faceMat = new Material(faceShader) { name = "AnnotationFace (runtime)", hideFlags = HideFlags.DontSave };
            _lineRenderer.sharedMaterial = _lineMat;
            _boxFace.sharedMaterial = _faceMat;
            _floorFace.sharedMaterial = _faceMat;

            _lineMat.SetColor("_Color", Color.white);
            _lineMat.SetColor("_CoreColor", _look.Core);
            _lineMat.SetFloat("_CoreMix", _look.CoreMix);
            _lineMat.SetFloat("_WidthPx", _look.LinePx);
            _lineMat.SetFloat("_GlowPx", _look.GlowPx);
            _lineMat.SetFloat("_GlowAlpha", _look.GlowAlpha);
            _lineMat.SetColor("_CasingColor", _look.Casing);
            _lineMat.SetFloat("_CasingPx", _look.CasingPx);

            _faceMat.SetColor("_FillColor", _look.FaceFill);
            _faceMat.SetFloat("_FillAlpha", _look.FaceAlpha);
            _faceMat.SetFloat("_EdgeAlpha", _look.FaceEdgeAlpha);
            _faceMat.SetFloat("_EdgeWidth", _look.FaceEdgeWidth);
            _faceMat.SetFloat("_BackFactor", _look.FaceBackFactor);
            _faceMat.SetFloat("_TopFade", _look.FaceTopFade);
        }

        private MeshRenderer MakeRenderer(string name, Mesh mesh)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        private void SetRenderersEnabled(bool on)
        {
            if (_lineRenderer == null) return;
            _lineRenderer.enabled = on;
            if (!on) { _boxFace.enabled = false; _floorFace.enabled = false; }
        }

        // =============================================================================================================
        // Build
        // =============================================================================================================

        private Vector3 W(Vector3 local) => _lw.MultiplyPoint3x4(local);

        private void Build()
        {
            _lw = transform.localToWorldMatrix;
            _segments.Clear();
            bool boxFace = false, floorFace = false;

            // The line mesh is in world space, on a root-level object.
            _lineRenderer.gameObject.layer = gameObject.layer;

            for (int i = 0; i < _prims.Count; i++)
            {
                var p = _prims[i];
                switch (p.kind)
                {
                    case Kind.Segment: Emit(W(p.a), W(p.b), p.colorA, p.colorB, p.f <= 0f ? 1f : p.f); break;
                    case Kind.PointMarker: EmitPointMarker(p); break;
                    case Kind.FloorRect: EmitFloorRect(p, ref floorFace); break;
                    case Kind.Box: EmitBox(p, ref boxFace); break;
                    case Kind.Prism: EmitPrism(p); break;
                }
            }

            _boxFace.enabled = boxFace;
            _floorFace.enabled = floorFace;
            Upload();
            _lineRenderer.enabled = _segments.Count > 0;
        }

        private void Emit(Vector3 a, Vector3 b, Color ca, Color cb, float widthScale)
        {
            if (_cam != null)
            {
                float t0, t1;
                var a0 = a; var b0 = b;
                if (!AnnotationGeometry.ClipToNear(_cam.worldToCameraMatrix, NearPlane, ref a, ref b, out t0, out t1)) return;
                var c0 = Color.Lerp(ca, cb, t0); var c1 = Color.Lerp(ca, cb, t1);
                ca = c0; cb = c1;
            }
            if ((b - a).sqrMagnitude < 1e-10f) return;
            ca.a *= _alpha; cb.a *= _alpha;
            if (_segments.Count == 0) { _segMin = Vector3.Min(a, b); _segMax = Vector3.Max(a, b); }
            else { _segMin = Vector3.Min(_segMin, Vector3.Min(a, b)); _segMax = Vector3.Max(_segMax, Vector3.Max(a, b)); }
            _segments.Add(new AnnotationSegment(a, b, ca, cb, widthScale));
        }

        private void EmitArm(Vector3 from, Vector3 to, float length, Color color, float widthScale)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            Emit(from, from + d / len * Mathf.Min(length, len), color, color, widthScale);
        }

        private Color Dim(Color c, bool hidden)
        {
            if (hidden) c.a *= _look.HiddenAlpha;
            return c;
        }

        private void EmitPointMarker(Prim p)
        {
            // four corner brackets of a square lying on the surface, centred on the point
            float h = p.f * 0.5f;
            float arm = p.f * 0.38f;
            var color = _look.BracketColor(p.focus, AnnotationEmphasis.Normal);
            float ws = _look.BracketScale(p.focus);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var corner = p.a + p.b * (sx * h) + p.c * (sz * h);
                    Emit(W(corner), W(corner - p.b * (sx * arm)), color, color, ws);
                    Emit(W(corner), W(corner - p.c * (sz * arm)), color, color, ws);
                }
            }
        }

        private void EmitFloorRect(Prim p, ref bool floorFace)
        {
            Vector3 x = p.b - p.a, z = p.c - p.a;
            float sx = x.magnitude, sz = z.magnitude;
            if (sx < 1e-4f || sz < 1e-4f) return;
            Vector3 p3 = p.a + x + z;
            var ring = _ringBuf;
            ring[0] = p.a; ring[1] = p.b; ring[2] = p3; ring[3] = p.c;
            var edge = _look.EdgeColor(AnnotationEmphasis.Normal);
            for (int i = 0; i < 4; i++) Emit(W(ring[i]), W(ring[(i + 1) % 4]), edge, edge, _look.EdgeWidthScale);
            for (int i = 0; i < 4; i++)
            {
                var focus = i == p.index ? p.focus : CornerFocus.None;
                var color = _look.BracketColor(focus, AnnotationEmphasis.Normal);
                float ws = _look.BracketScale(focus);
                var next = ring[(i + 1) % 4]; var prev = ring[(i + 3) % 4];
                EmitArm(W(ring[i]), W(next), AnnotationGeometry.BracketLength((next - ring[i]).magnitude, _look), color, ws);
                EmitArm(W(ring[i]), W(prev), AnnotationGeometry.BracketLength((prev - ring[i]).magnitude, _look), color, ws);
            }

            // the translucent footprint: a very flat box
            var up = p.size.sqrMagnitude > 0.5f ? p.size.normalized : Vector3.up;
            _floorFaceT.localPosition = p.a + x * 0.5f + z * 0.5f + up * 0.001f;
            _floorFaceT.localRotation = Quaternion.LookRotation(z / sz, up);
            _floorFaceT.localScale = new Vector3(sx, 0.002f, sz);
            floorFace = true;
            _mpb.Clear();
            _mpb.SetFloat("_FillAlpha", _look.FaceAlpha * 1.4f * _alpha);
            _mpb.SetFloat("_EdgeAlpha", _look.FaceEdgeAlpha * 1.6f * _alpha);
            _mpb.SetFloat("_TopFade", 1f);
            _floorFace.SetPropertyBlock(_mpb);
        }

        private void EmitBox(Prim p, ref bool boxFace)
        {
            Vector3 size = p.size;
            if (size.x < 1e-4f || size.y < 1e-4f || size.z < 1e-4f) return;
            var local = _boxLocal;
            AnnotationGeometry.BoxCorners(p.a, p.rot, size, local);
            var wc = _boxWorld;
            for (int i = 0; i < 8; i++) wc[i] = W(local[i]);

            Vector3 centreW = W(p.a + p.rot * new Vector3(0f, size.y * 0.5f, 0f));
            AnnotationGeometry.FacesToward(centreW, _lw.rotation * p.rot, size, _eyeWorld, _facing);

            // the 12 edges
            var edgeColor = _look.EdgeColor(p.emphasis);
            float edgeScale = _look.EdgeScale(p.emphasis);
            for (int e = 0; e < 12; e++)
            {
                bool hidden = AnnotationGeometry.EdgeHidden(e, _facing);
                var c = Dim(edgeColor, hidden);
                Emit(wc[AnnotationGeometry.BoxEdges[e, 0]], wc[AnnotationGeometry.BoxEdges[e, 1]], c, c, hidden ? edgeScale * 0.85f : edgeScale);
            }

            // a bracket at every corner: three arms along its three edges
            for (int i = 0; i < 8; i++)
            {
                var focus = i == p.index ? p.focus : CornerFocus.None;
                bool hidden = AnnotationGeometry.CornerHidden(i, _facing);
                var color = Dim(_look.BracketColor(focus, p.emphasis), hidden && focus == CornerFocus.None);
                float ws = _look.BracketScale(focus);
                AnnotationGeometry.CornerNeighbours(i, out int next, out int prev, out int vert);
                float armScale = hidden && focus == CornerFocus.None ? ws * 0.85f : ws;
                EmitArm(wc[i], wc[next], AnnotationGeometry.BracketLength((local[next] - local[i]).magnitude, _look), color, armScale);
                EmitArm(wc[i], wc[prev], AnnotationGeometry.BracketLength((local[prev] - local[i]).magnitude, _look), color, armScale);
                EmitArm(wc[i], wc[vert], AnnotationGeometry.BracketLength((local[vert] - local[i]).magnitude, _look), color, armScale);
            }

            // faces
            _boxFaceT.localPosition = p.a + p.rot * new Vector3(0f, size.y * 0.5f, 0f);
            _boxFaceT.localRotation = p.rot;
            _boxFaceT.localScale = size;
            boxFace = true;
            float k = _look.FaceScale(p.emphasis) * _alpha;
            _mpb.Clear();
            BoxFaceFillAlpha = _look.FaceAlpha * k;
            _mpb.SetFloat("_FillAlpha", BoxFaceFillAlpha);
            _mpb.SetFloat("_EdgeAlpha", _look.FaceEdgeAlpha * k);
            _mpb.SetFloat("_TopFade", _look.FaceTopFade);
            _boxFace.SetPropertyBlock(_mpb);
        }

        private void EmitPrism(Prim p)
        {
            int n = _prismPoints.Count;
            if (n < 3) return;
            var up = p.a * p.f;
            var edge = _look.EdgeColor(p.emphasis);
            float edgeScale = _look.EdgeScale(p.emphasis);
            for (int i = 0; i < n; i++)
            {
                var b0 = _prismPoints[i]; var b1 = _prismPoints[(i + 1) % n];
                Emit(W(b0), W(b1), edge, edge, edgeScale);
                Emit(W(b0 + up), W(b1 + up), edge, edge, edgeScale);
                Emit(W(b0), W(b0 + up), edge, edge, edgeScale);
            }
            for (int i = 0; i < n; i++)
            {
                var focus = i == p.index ? p.focus : CornerFocus.None;
                var color = _look.BracketColor(focus, p.emphasis);
                float ws = _look.BracketScale(focus);
                var cur = _prismPoints[i]; var next = _prismPoints[(i + 1) % n]; var prev = _prismPoints[(i + n - 1) % n];
                float hLen = AnnotationGeometry.BracketLength(p.f, _look);
                // bottom and top corner of this vertex: the two outline edges and the vertical edge
                EmitArm(W(cur), W(next), AnnotationGeometry.BracketLength((next - cur).magnitude, _look), color, ws);
                EmitArm(W(cur), W(prev), AnnotationGeometry.BracketLength((prev - cur).magnitude, _look), color, ws);
                EmitArm(W(cur), W(cur + up), hLen, color, ws);
                EmitArm(W(cur + up), W(next + up), AnnotationGeometry.BracketLength((next - cur).magnitude, _look), color, ws);
                EmitArm(W(cur + up), W(prev + up), AnnotationGeometry.BracketLength((prev - cur).magnitude, _look), color, ws);
                EmitArm(W(cur + up), W(cur), hLen, color, ws);
            }
        }

        // Mesh: 4 vertices per segment. POSITION = this end, NORMAL = the other end, uv0 = (side, end), uv1 = (length m, width scale).
        private void Upload()
        {
            _vPos.Clear(); _vOther.Clear(); _vSide.Clear(); _vInfo.Clear(); _vCol.Clear(); _tri.Clear();
            for (int s = 0; s < _segments.Count; s++)
            {
                var seg = _segments[s];
                float len = (seg.B - seg.A).magnitude;
                int v0 = _vPos.Count;
                for (int end = 0; end < 2; end++)
                {
                    var self = end == 0 ? seg.A : seg.B;
                    var other = end == 0 ? seg.B : seg.A;
                    var col = Lin(end == 0 ? seg.ColorA : seg.ColorB);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        _vPos.Add(self); _vOther.Add(other);
                        _vSide.Add(new Vector2(side, end));
                        _vInfo.Add(new Vector2(len, seg.WidthScale));
                        _vCol.Add(col);
                    }
                }
                _tri.Add(v0); _tri.Add(v0 + 1); _tri.Add(v0 + 2);
                _tri.Add(v0 + 2); _tri.Add(v0 + 1); _tri.Add(v0 + 3);
            }
            _lineMesh.Clear();
            if (_vPos.Count == 0) return;
            _lineMesh.SetVertices(_vPos);
            _lineMesh.SetNormals(_vOther);
            _lineMesh.SetUVs(0, _vSide);
            _lineMesh.SetUVs(1, _vInfo);
            _lineMesh.SetColors(_vCol);
            _lineMesh.SetTriangles(_tri, 0, false);
            // Tight world-space bounds (the shader ignores the object matrix): anything that merges renderer bounds, such as the
            // mode-switch prompt or an outline, must see the box and not a cube around the viewer. Padded for the pixel width.
            var bounds = new Bounds();
            bounds.SetMinMax(_segMin - Vector3.one * 0.05f, _segMax + Vector3.one * 0.05f);
            _lineMesh.bounds = bounds;
        }

        // Mesh vertex colours are not colour-space converted by Unity: do it here so a linear project shows the theme colours.
        private static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        private static Mesh BuildUnitCube()
        {
            var v = new List<Vector3>(24);
            var n = new List<Vector3>(24);
            var t = new List<int>(36);
            void Face(Vector3 normal, Vector3 u, Vector3 w)
            {
                int b = v.Count;
                var c = normal * 0.5f;
                v.Add(c - u * 0.5f - w * 0.5f); v.Add(c + u * 0.5f - w * 0.5f); v.Add(c + u * 0.5f + w * 0.5f); v.Add(c - u * 0.5f + w * 0.5f);
                for (int i = 0; i < 4; i++) n.Add(normal);
                if (Vector3.Dot(Vector3.Cross(u, w), normal) > 0f) t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                else t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            Face(Vector3.up, Vector3.right, Vector3.forward);
            Face(Vector3.down, Vector3.right, Vector3.forward);
            Face(Vector3.right, Vector3.up, Vector3.forward);
            Face(Vector3.left, Vector3.up, Vector3.forward);
            Face(Vector3.forward, Vector3.right, Vector3.up);
            Face(Vector3.back, Vector3.right, Vector3.up);
            var m = new Mesh { name = "AnnotationUnitCube", hideFlags = HideFlags.DontSave };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetTriangles(t, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.one);
            return m;
        }
    }
}
