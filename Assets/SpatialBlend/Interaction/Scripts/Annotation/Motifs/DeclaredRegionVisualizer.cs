// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using SpatialBlend.Interactions;
using Oculus.Interaction;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Visualizes a declared region (the declared extent of an SbSpatialAuthoring) and implements
    /// IEditableAnnotation for editing. Attached to the same GameObject as the SbSpatialAuthoring.
    /// Automatically shows on hover, hides when not hovered.
    /// </summary>
    [RequireComponent(typeof(SbSpatialAuthoring))]
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public class DeclaredRegionVisualizer : MonoBehaviour, IEditableAnnotation
    {
        [Header("Hover Settings")]
        [SerializeField, Tooltip("Show visualization only when hovered")]
        private bool _showOnHoverOnly = true;
        [SerializeField, Tooltip("Time in seconds to wait after construction before fading out")]
        private float _constructionWaitDuration = 1f;
        [SerializeField, Tooltip("Duration for fade out after construction")]
        private float _constructionFadeOutDuration = 4f;
        [SerializeField, Tooltip("Duration for hover fade in/out transitions")]
        private float _hoverFadeDuration = 0.3f;

        private SbSpatialAuthoring _authoring;
        private AnnotationVisual _visual;        // the region prism: outlines, vertical edges, corner brackets

        private float _height = 2.0f;            // Volume height for 3D visualization

        private bool _isEditMode = false;
        private readonly List<Vector3> _polygon = new List<Vector3>();
        private bool _isHovered = false;
        private bool _isVisualizationVisible = false;

        // Hover detection
        private RayInteractable _rayInteractable;
        private bool _subscribedToInteractor = false;

        // Auto-hide after construction
        private bool _wasJustConstructed = false;

        // Smooth visibility transitions
        private Coroutine _fadeCoroutine;
        private Coroutine _constructionCoroutine;
        private float _currentAlpha = 0f;

        // Cached vertices in local space (relative to transform) for rendering
        private List<Vector3> _localVertices = new List<Vector3>();

        public void Initialize(SbSpatialAuthoring authoring, Color color, float lineWidth, Material lineMaterial = null, float height = 2.0f)
        {
            // color, lineWidth and lineMaterial are kept for source compatibility: the look is AnnotationLook's.
            _authoring = authoring;
            _height = height;

            // Clean up the existing graphics before creating new ones
            DestroyVisual();

            // Read the vertices even when the outlines are off, so the data stays current (as v1)
            ReadVerticesFromRegion();
            if (!InteractionModeManager.VisualizeAffordanceVolumes)
            {
                return;
            }

            EnsureVisual();
            UpdateVisualization();

            // Show immediately after construction, then auto-hide after duration
            if (_showOnHoverOnly && (_constructionWaitDuration > 0 || _constructionFadeOutDuration > 0))
            {
                _wasJustConstructed = true;
                if (_constructionCoroutine != null) StopCoroutine(_constructionCoroutine);
                _constructionCoroutine = StartCoroutine(ConstructionVisibilityCoroutine());
            }
        }

        private void Awake()
        {
            if (_authoring == null)
            {
                _authoring = GetComponent<SbSpatialAuthoring>();
            }
        }

        private void Start()
        {
            if (!InteractionModeManager.VisualizeAffordanceVolumes)
            {
                DestroyVisual();
                return;
            }

            EnsureVisual();

            // Find RayInteractable on this object or parent
            _rayInteractable = GetComponentInParent<RayInteractable>();

            // Subscribe to hover events
            SubscribeToInteractor();

            // Skip hiding if Initialize() already started construction visibility
            if (_showOnHoverOnly && !_wasJustConstructed)
            {
                _currentAlpha = 0f;
                ApplyAlpha(0f);
                SetVisualEnabled(false);
            }
            else if (!_showOnHoverOnly)
            {
                _currentAlpha = 1f;
                UpdateVisualization();
            }
        }

        private void Update()
        {
            if (!InteractionModeManager.VisualizeAffordanceVolumes)
            {
                if (_visual != null)
                {
                    DestroyVisual();
                }
                _isVisualizationVisible = false;
                return;
            }

            // Retry subscription if not yet subscribed
            if (_showOnHoverOnly && !_subscribedToInteractor)
            {
                SubscribeToInteractor();
            }
        }

        private void SubscribeToInteractor()
        {
            if (_subscribedToInteractor) return;
            if (_rayInteractable == null) return;

            // The active editing input's ray: the right controller's or the right hand's.
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            _subscribedToInteractor = true;
        }

        private void UnsubscribeFromInteractor()
        {
            if (!_subscribedToInteractor) return;

            EditInput.Hovered -= OnInteractorHover;
            EditInput.Unhovered -= OnInteractorUnhover;
            _subscribedToInteractor = false;
        }

        internal void OnInteractorHover(EditPointer pointer, RayInteractable interactable)
        {
            if (!InteractionModeManager.VisualizeAffordanceVolumes) return;

            if (interactable != _rayInteractable) return;
            // The outline is an authoring aid: only SceneEdit shows it on hover (as v1).
            if (!InteractionModeManager.IsMode(InteractionMode.SceneEdit)) return;
            _isHovered = true;
            if (_showOnHoverOnly)
            {
                FadeIn();
            }
        }

        internal void OnInteractorUnhover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            _isHovered = false;
            // Don't hide if still within construction show window
            if (_showOnHoverOnly && !_wasJustConstructed)
            {
                FadeOut();
            }
        }

        // === Smooth Visibility Transitions ===

        private void FadeIn()
        {
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(FadeCoroutine(1f, _hoverFadeDuration));
        }

        private void FadeOut()
        {
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(FadeCoroutine(0f, _hoverFadeDuration));
        }

        private System.Collections.IEnumerator FadeCoroutine(float targetAlpha, float duration)
        {
            if (targetAlpha > 0)
            {
                UpdateVisualization();
                SetVisualEnabled(true);
            }

            float startAlpha = _currentAlpha;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                _currentAlpha = Mathf.Lerp(startAlpha, targetAlpha, t);
                ApplyAlpha(_currentAlpha);
                yield return null;
            }

            _currentAlpha = targetAlpha;
            ApplyAlpha(_currentAlpha);

            if (targetAlpha <= 0)
            {
                SetVisualEnabled(false);
            }

            _isVisualizationVisible = targetAlpha > 0;
            _fadeCoroutine = null;
        }

        private System.Collections.IEnumerator ConstructionVisibilityCoroutine()
        {
            if (!InteractionModeManager.VisualizeAffordanceVolumes)
            {
                _constructionCoroutine = null;
                yield break;
            }

            // Show immediately at full visibility
            UpdateVisualization();
            SetVisualEnabled(true);
            _currentAlpha = 1f;
            ApplyAlpha(1f);
            _isVisualizationVisible = true;

            // Wait before fading out
            yield return new WaitForSeconds(_constructionWaitDuration);

            // Fade out only if not hovered
            _wasJustConstructed = false;
            if (!_isHovered && _showOnHoverOnly)
            {
                yield return FadeCoroutine(0f, _constructionFadeOutDuration);
            }

            _constructionCoroutine = null;
        }

        private void SetVisualEnabled(bool enabled)
        {
            if (_visual != null) _visual.gameObject.SetActive(enabled && _localVertices.Count >= 3);
        }

        private void ApplyAlpha(float alpha)
        {
            if (_visual != null) _visual.Alpha = alpha;
        }

        private void OnDestroy()
        {
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            if (_constructionCoroutine != null) StopCoroutine(_constructionCoroutine);
            UnsubscribeFromInteractor();
        }

        // === Local/World Coordinate Conversion ===

        /// <summary>
        /// Convert a local vertex position to world space.
        /// </summary>
        private Vector3 LocalToWorld(Vector3 localPos)
        {
            return transform.TransformPoint(localPos);
        }

        /// <summary>
        /// Convert a world position to local vertex space.
        /// </summary>
        private Vector3 WorldToLocal(Vector3 worldPos)
        {
            return transform.InverseTransformPoint(worldPos);
        }

        // === IEditableAnnotation ===

        public int VertexCount => _localVertices.Count;

        public bool SupportsAddRemove => true;

        public Vector3 GetVertex(int index)
        {
            if (index < 0 || index >= _localVertices.Count) return Vector3.zero;
            // Return world position for editing
            return LocalToWorld(_localVertices[index]);
        }

        public void SetVertex(int index, Vector3 worldPos)
        {
            if (index < 0 || index >= _localVertices.Count) return;

            // Convert world position to local and store
            var localPos = WorldToLocal(worldPos);
            _localVertices[index] = new Vector3(localPos.x, 0, localPos.z); // Keep Y=0 in local space
            ApplyVerticesToRegion();
            UpdateVisualization();
        }

        public void InsertVertex(int afterIndex, Vector3 worldPos)
        {
            if (afterIndex < 0 || afterIndex >= _localVertices.Count) return;

            // Convert world position to local and insert
            var localPos = WorldToLocal(worldPos);
            _localVertices.Insert(afterIndex + 1, new Vector3(localPos.x, 0, localPos.z));
            ApplyVerticesToRegion();
            UpdateVisualization();
        }

        public void RemoveVertex(int index)
        {
            if (index < 0 || index >= _localVertices.Count) return;
            if (_localVertices.Count <= 3) return; // Keep minimum triangle

            _localVertices.RemoveAt(index);
            ApplyVerticesToRegion();
            UpdateVisualization();
        }

        public int FindClosestVertex(Vector3 worldPos, float maxDistance)
        {
            int closest = -1;
            float closestDist = maxDistance;

            for (int i = 0; i < _localVertices.Count; i++)
            {
                // Convert local to world for distance comparison
                var worldVertex = LocalToWorld(_localVertices[i]);
                float dist = Vector3.Distance(worldVertex, worldPos);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = i;
                }
            }
            return closest;
        }

        public int FindClosestEdge(Vector3 worldPos, float maxDistance)
        {
            int closest = -1;
            float closestDist = maxDistance;

            for (int i = 0; i < _localVertices.Count; i++)
            {
                // Convert local vertices to world for distance comparison
                var a = LocalToWorld(_localVertices[i]);
                var b = LocalToWorld(_localVertices[(i + 1) % _localVertices.Count]);
                var pointOnEdge = ClosestPointOnSegment(a, b, worldPos);
                float dist = Vector3.Distance(pointOnEdge, worldPos);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = i;
                }
            }
            return closest;
        }

        // === Raycast ===

        public bool Raycast(Ray ray, out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;

            if (_localVertices.Count < 3) return false;

            // Convert ray to local space for polygon check
            var localRayOrigin = transform.InverseTransformPoint(ray.origin);
            var localRayDir = transform.InverseTransformDirection(ray.direction).normalized;
            var localRay = new Ray(localRayOrigin, localRayDir);

            // Create a plane from the local polygon (at Y=0 in local space)
            var plane = new Plane(Vector3.up, Vector3.zero);

            if (plane.Raycast(localRay, out float enter))
            {
                var localHitPoint = localRay.GetPoint(enter);

                // Check if point is inside polygon (2D check in local XZ plane)
                if (IsPointInPolygonLocal(localHitPoint))
                {
                    // Convert hit point back to world space
                    hitPoint = transform.TransformPoint(localHitPoint);
                    return true;
                }

                // Also check if close to any edge (use world space for this)
                hitPoint = transform.TransformPoint(localHitPoint);
                int edge = FindClosestEdge(hitPoint, 0.15f);
                if (edge >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if a local point is inside the polygon (XZ plane check).
        /// </summary>
        private bool IsPointInPolygonLocal(Vector3 localPoint)
        {
            if (_localVertices.Count < 3) return false;

            // Project to 2D (XZ plane) using local coordinates
            var point2D = new Vector2(localPoint.x, localPoint.z);

            // Ray casting algorithm using local XZ coordinates
            bool inside = false;
            int j = _localVertices.Count - 1;

            for (int i = 0; i < _localVertices.Count; i++)
            {
                var vi = new Vector2(_localVertices[i].x, _localVertices[i].z);
                var vj = new Vector2(_localVertices[j].x, _localVertices[j].z);

                if ((vi.y > point2D.y) != (vj.y > point2D.y) &&
                    point2D.x < (vj.x - vi.x) * (point2D.y - vi.y) / (vj.y - vi.y) + vi.x)
                {
                    inside = !inside;
                }
                j = i;
            }

            return inside;
        }

        // === Visualization ===

        public void SetEditMode(bool editMode, Color color)
        {
            // color is kept for source compatibility: editing is shown by the selected emphasis of the same look.
            _isEditMode = editMode;
            UpdateVisualization();
        }

        private void UpdateVisualization()
        {
            // Read vertices from the declared region
            ReadVerticesFromRegion();

            if (!InteractionModeManager.VisualizeAffordanceVolumes) return;

            UpdateVisual();
        }

        // === The graphics ===

        private void EnsureVisual()
        {
            if (_visual != null) return;
            _visual = AnnotationVisual.Create(transform, "AnnotationVisual_Region");
            _visual.transform.localPosition = Vector3.zero;
            _visual.transform.localRotation = Quaternion.identity;
        }

        private void DestroyVisual()
        {
            if (_visual == null) return;
            if (Application.isPlaying) Destroy(_visual.gameObject); else DestroyImmediate(_visual.gameObject);
            _visual = null;
        }

        private void UpdateVisual()
        {
            EnsureVisual();
            _visual.Clear();
            if (_localVertices.Count >= 3)
            {
                // When the parent's transform.position is at the object centre (e.g. BoxAnnotationInstance), the prism is
                // offset down so it starts at the bottom.
                var bottomOffset = GetBottomOffset();
                _polygon.Clear();
                for (int i = 0; i < _localVertices.Count; i++) _polygon.Add(_localVertices[i] + bottomOffset);
                var emphasis = _isEditMode ? AnnotationEmphasis.Selected : _isHovered ? AnnotationEmphasis.Hovered : AnnotationEmphasis.Normal;
                _visual.SetPrism(_polygon, Vector3.up, _height, emphasis);
            }
            _visual.Alpha = _currentAlpha;
        }

        private void ReadVerticesFromRegion()
        {
            _localVertices.Clear();

            if (_authoring == null)
            {
                Debug.LogWarning("[DeclaredRegionVisualizer] Cannot read vertices: no SbSpatialAuthoring");
                return;
            }

            // The declared extent is shown as authored. With none declared, the resolved region is shown.
            Vector2[] path = _authoring.declaredExtent;
            if ((path == null || path.Length < 3) && !_authoring.TryGetRegionPolygon(out path))
            {
                Debug.LogWarning("[DeclaredRegionVisualizer] The authoring has no region to show");
                return;
            }

            // The region is in the authoring frame; the graphics use the transform's local space.
            Quaternion frame = Quaternion.Euler(_authoring.frameEulerAngles);
            for (int i = 0; i < path.Length; i++)
            {
                _localVertices.Add(frame * new Vector3(path[i].x, 0, path[i].y));
            }
        }

        private void ApplyVerticesToRegion()
        {
            if (_authoring == null) return;

            // _localVertices stores transform-local (X, 0, Z); the declared extent is in the authoring frame.
            Quaternion toFrame = Quaternion.Inverse(Quaternion.Euler(_authoring.frameEulerAngles));
            Vector2[] polygon = new Vector2[_localVertices.Count];
            for (int i = 0; i < _localVertices.Count; i++)
            {
                Vector3 inFrame = toFrame * _localVertices[i];
                polygon[i] = new Vector2(inFrame.x, inFrame.z);
            }

            _authoring.declaredExtent = polygon;
            _authoring.InvalidateRegion();
        }

        // === Helpers ===

        /// <summary>
        /// Calculate the vertical offset to align visualization with the bottom of the parent object.
        /// Uses world-space bounds to automatically handle any positioning convention.
        /// </summary>
        private Vector3 GetBottomOffset()
        {
            var boxCollider = GetComponentInParent<BoxCollider>();
            if (boxCollider != null)
            {
                // Use world-space bounds.min.y to get actual bottom
                float bottomY = boxCollider.bounds.min.y;
                float positionY = transform.position.y;
                return Vector3.down * (positionY - bottomY);
            }
            return Vector3.zero;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            if (ab.sqrMagnitude < 0.0001f) return a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return a + t * ab;
        }
    }
}
