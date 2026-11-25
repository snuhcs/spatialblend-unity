// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using SpatialBlend.Interactions;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Annotation motif for declaring an object's region on floor surfaces.
    /// Collects 3+ points to create a 2D polygon that becomes the object's declared extent
    /// (<see cref="SbSpatialAuthoring.declaredExtent"/>).
    ///
    /// Features:
    /// - Ring closure: last point snaps to first point to complete polygon
    /// - Height automatically derived from target entity's collider
    /// </summary>
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public class AffordanceAnnotationMotif : FloorVertexCollectionMotif
    {
        [Header("Affordance Settings")]
        [SerializeField] private Transform _annotationRoot;
        [SerializeField, Tooltip("Default height if no target collider is available")]
        private float _defaultHeight = 2.0f;

        [Header("Ring Closure")]
        [SerializeField, Tooltip("Distance threshold for snapping to first point to close the ring")]
        private float _snapDistance = 0.15f;

        // Preview rendering: the region prism (bottom, top and vertical edges, corner brackets) in the annotation look
        private AnnotationVisual _preview;
        private bool _isFirstPointHighlighted = false;
        private readonly List<Vector3> _previewPoints = new List<Vector3>(16);

        // Declarations written by this motif
        private List<SbSpatialAuthoring> _createdRegions = new List<SbSpatialAuthoring>();

        // Target reference for height calculation (set via ManipulationContext)
        private ManipulationContext _context;

        // === Configuration ===

        protected override int MinPoints => 3;
        protected override int MaxPoints => -1; // Unlimited
        protected override bool AutoFinalizeOnMaxPoints => false;

        /// <summary>
        /// Ring closes when cursor is near first point and have enough points.
        /// </summary>
        protected override bool ShouldFinalize()
        {
            return IsNearFirstPoint && Points.Count >= MinPoints;
        }

        // === Unity Lifecycle ===

        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ClearPreview();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ClearPreview();
            if (_preview != null)
            {
                Destroy(_preview.gameObject);
                _preview = null;
            }
        }

        // === Annotation Callbacks ===

        protected override void OnPointAdded(int index, Vector3 point)
        {
            UpdatePreview();
        }

        protected override void OnPointRemoved(int index)
        {
            UpdatePreview();
        }

        protected override void OnPointUpdated(int index, Vector3 point)
        {
            // Show preview of where next point would go; the first point's bracket lights up when the ring would close
            UpdatePreview(includePreview: true, previewPoint: point);
        }

        protected override void OnFinalized(IReadOnlyList<Vector3> points, Plane groundPlane)
        {
            // Get height from target entity's collider or use default
            float height = GetTargetColliderHeight();

            // The annotation is the target's declared extent. Without a target it is a free region.
            GameObject go;
            bool addedToTarget = false;
            if (_context?.TargetObject != null)
            {
                go = _context.TargetObject;
                addedToTarget = true;
            }
            else
            {
                go = new GameObject("DeclaredRegion_Annotation");
                if (_annotationRoot != null)
                {
                    go.transform.SetParent(_annotationRoot, false);
                }
            }

            var authoring = go.GetComponent<SbSpatialAuthoring>();
            bool authoringAdded = false;
            if (authoring == null)
            {
                authoringAdded = true;
                authoring = go.AddComponent<SbSpatialAuthoring>();
                authoring.footprint = go.GetComponentInChildren<Collider>();
            }

            // World points to the authoring frame (the object's local XZ, rotated by frameEulerAngles).
            Quaternion toFrame = Quaternion.Inverse(Quaternion.Euler(authoring.frameEulerAngles));
            Vector2[] polygon = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 local = toFrame * go.transform.InverseTransformPoint(points[i]);
                polygon[i] = new Vector2(local.x, local.z);
            }
            if (authoring.footprint == null)
            {
                authoring.footprintPolygon = polygon;
            }

            // A polygon that does not contain the object's footprint is refused with a visible error:
            // the compiler would drop it silently at share time.
            if (!authoring.TryValidateDeclaredExtent(polygon, out string refusal))
            {
                RefuseRegion(go, authoring, authoringAdded, addedToTarget, refusal);
                _isFirstPointHighlighted = false;
                ClearPreview();
                return;
            }
            authoring.declaredExtent = polygon;
            authoring.InvalidateRegion();

            var visualizer = go.GetComponent<DeclaredRegionVisualizer>();
            if (visualizer == null)
            {
                visualizer = go.AddComponent<DeclaredRegionVisualizer>();
            }
            visualizer.Initialize(authoring, Color.white, 0f, null, height);

            if (!_createdRegions.Contains(authoring))
            {
                _createdRegions.Add(authoring);
            }

            // Clear preview and reset highlight state
            _isFirstPointHighlighted = false;
            ClearPreview();
        }

        /// <summary>The last refusal of a region polygon, and the event that tells the UI (null while none).</summary>
        public static string LastRefusal { get; private set; }
        public static event System.Action<string> RegionRefused;

        private static void DestroyNow(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private void RefuseRegion(GameObject go, SbSpatialAuthoring authoring, bool authoringAdded, bool addedToTarget,
            string reason)
        {
            string message = $"Region not created: {reason}";
            LastRefusal = message;
            Debug.LogError($"[AffordanceAnnotation] {message} ('{go.name}')");
            // Nothing the refused polygon made is kept: a component this motif added, or the free region's object.
            if (authoringAdded && authoring != null) DestroyNow(authoring);
            if (!addedToTarget) DestroyNow(go);
            RegionRefused?.Invoke(message);
        }

        /// <summary>
        /// Get height from target entity's collider.
        /// Falls back to default height if no context or collider is available.
        /// </summary>
        private float GetTargetColliderHeight()
        {
            if (_context?.TargetObject == null)
            {
                return _defaultHeight;
            }

            var collider = _context.TargetObject.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                return collider.bounds.size.y;
            }

            return _defaultHeight;
        }

        protected override void OnCancelled()
        {
            ClearPreview();
        }

        // === Preview Rendering ===

        private void EnsurePreview()
        {
            if (_preview != null) return;
            // World-space content: the preview sits at the world origin.
            _preview = AnnotationVisual.Create(transform, "AnnotationVisual_AffordancePreview");
            _preview.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private void UpdatePreview(bool includePreview = false, Vector3 previewPoint = default)
        {
            EnsurePreview();
            _preview.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _preview.Clear();

            var all = _previewPoints;
            all.Clear();
            all.AddRange(Points);
            if (includePreview) all.Add(previewPoint);
            if (all.Count == 0) { _isFirstPointHighlighted = false; return; }

            // ring closure feedback: the first point's bracket lights up when the cursor is within snap distance
            _isFirstPointHighlighted = IsNearFirstPoint && Points.Count >= MinPoints;

            if (all.Count < 3)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    bool cursor = includePreview && i == all.Count - 1;
                    _preview.AddPointMarker(all[i], Vector3.right, Vector3.forward, cursor ? CornerFocus.Active : CornerFocus.None);
                }
                if (all.Count == 2) _preview.AddSegment(all[0], all[1]);
                return;
            }

            // Get height for 3D preview
            float height = GetTargetColliderHeight();
            int focus = _isFirstPointHighlighted ? 0 : includePreview ? all.Count - 1 : -1;
            _preview.SetPrism(all, Vector3.up, height, AnnotationEmphasis.Normal, focus, CornerFocus.Active);
        }

        private void ClearPreview()
        {
            if (_preview != null) _preview.Clear();
            _isFirstPointHighlighted = false;
        }

        // === Helpers ===

        private Vector3 ComputeCenter(IReadOnlyList<Vector3> points)
        {
            if (points.Count == 0) return Vector3.zero;

            var sum = Vector3.zero;
            foreach (var p in points)
            {
                sum += p;
            }
            return sum / points.Count;
        }

        /// <summary>
        /// Get all declarations created by this motif for external access.
        /// </summary>
        public IReadOnlyList<SbSpatialAuthoring> CreatedRegions => _createdRegions;

        /// <summary>
        /// Delete a declaration created by this motif.
        /// </summary>
        public void DeleteRegion(SbSpatialAuthoring authoring)
        {
            if (authoring == null) return;

            _createdRegions.Remove(authoring);

            // If the declaration is on the target object, only remove the annotation (not the whole GameObject)
            if (_context?.TargetObject == authoring.gameObject)
            {
                var visualizer = authoring.GetComponent<DeclaredRegionVisualizer>();
                if (visualizer != null) Destroy(visualizer);
                authoring.declaredExtent = null;
                authoring.InvalidateRegion();
            }
            else
            {
                Destroy(authoring.gameObject);
            }
        }

        public void SetAnnotationRoot(Transform root)
        {
            _annotationRoot = root;
        }

        /// <summary>
        /// Set the ManipulationContext to get target information (for height calculation).
        /// </summary>
        public void SetContext(ManipulationContext context)
        {
            _context = context;
        }
    }
}
