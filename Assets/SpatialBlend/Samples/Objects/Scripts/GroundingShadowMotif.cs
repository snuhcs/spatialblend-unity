// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using Meta.XR;
using Meta.XR.EnvironmentDepth;

namespace SpatialBlend.Samples.Objects
{
    /// <summary>
    /// Projects a realistic shadow of a target object onto detected surfaces beneath it.
    /// The shadow adjusts its position, size, and opacity based on the target's proximity to the surface.
    /// </summary>
    public class GroundingShadowMotif : MonoBehaviour
    {
        [Tooltip("Prefab for the grounding shadow.")]
        [SerializeField]
        private GameObject groundingShadowPrefab;

        private bool m_enableShadowUpdates = true;

        private const float SMOOTH_TIME = 0.05f;
        private const float SHADOW_SCALER = 1.6f;
        private const float MAX_SHADOW_TINT = 0.8f;
        private const float SHADOW_MAX_DISTANCE = 2.0f;
        private const float SHADOW_MIN_DISTANCE = 0.05f;

        private Vector2 m_initialShadowSize;
        private Vector3 m_shadowPositionVelocity;

        private Transform m_trackedObject;
        private Transform m_groundingShadow;
        private Collider m_trackedObjectCollider;
        private SpriteRenderer m_groundingShadowRenderer;
        private EnvironmentRaycastManager m_raycastManager;
        private EnvironmentDepthManager m_depthManager;

        private SbNativeEntity m_entity;
        private bool m_hidden;

        /// <summary>The shadow this motif draws (a scene-root object it owns), or null before Awake or without a prefab.</summary>
        internal Transform Shadow => m_groundingShadow;

        private void Awake()
        {
            m_trackedObject = transform;
            m_trackedObjectCollider = m_trackedObject.GetComponent<Collider>();
            m_raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>();
            m_depthManager = FindAnyObjectByType<EnvironmentDepthManager>();
            InstantiateGroundingShadow();
            // A scan's collider is the template's default until its model is loaded and fitted: the shadow is measured again then.
            m_entity = GetComponent<SbNativeEntity>();
            if (m_entity != null) m_entity.ContentReady += OnContentReady;
        }

        private void InstantiateGroundingShadow()
        {
            if (groundingShadowPrefab == null) return;
            // At the scene root, not under the object: the shadow lies on the floor, flat and at 1:1, whatever the object's scale
            // and tilt. So this motif owns it: destroyed with the object (OnDestroy), hidden with it (OnDisable, SetHidden).
            m_groundingShadow = Instantiate(groundingShadowPrefab).transform;
            m_groundingShadow.name = name + " (grounding shadow)";
            m_groundingShadowRenderer = m_groundingShadow.GetComponent<SpriteRenderer>();
            Remeasure();
            ApplyVisibility();
        }

        /// <summary>Sizes the shadow from the object's collider as it is now (its world footprint).</summary>
        public void Remeasure()
        {
            if (m_groundingShadowRenderer == null) return;
            Bounds bounds = WorldColliderBounds(m_trackedObjectCollider);
            m_initialShadowSize = new Vector2(bounds.size.x, bounds.size.z);
            m_groundingShadowRenderer.size = m_initialShadowSize;
        }

        /// <summary>Hides the shadow with its object ("Hide for me" on a received copy) or shows it again.</summary>
        public void SetHidden(bool hidden)
        {
            m_hidden = hidden;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            if (m_groundingShadow != null) m_groundingShadow.gameObject.SetActive(!m_hidden && enabled && gameObject.activeInHierarchy);
        }

        private void OnContentReady(SbNativeEntity entity) => Remeasure();

        private void OnEnable() => ApplyVisibility();

        private void OnDisable()
        {
            if (m_groundingShadow != null) m_groundingShadow.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (m_entity != null) m_entity.ContentReady -= OnContentReady;
            if (m_groundingShadow == null) return;
            if (Application.isPlaying) Destroy(m_groundingShadow.gameObject);
            else DestroyImmediate(m_groundingShadow.gameObject);
            m_groundingShadow = null;
        }

        // The collider's world box from its own shape and transform (Collider.bounds needs the physics scene to be synced).
        private static Bounds WorldColliderBounds(Collider collider)
        {
            if (collider is BoxCollider box)
            {
                Matrix4x4 m = box.transform.localToWorldMatrix;
                Vector3 c = box.center, e = box.size * 0.5f;
                var result = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
                for (int i = 0; i < 8; i++)
                    result.Encapsulate(m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z)));
                return result;
            }
            return collider != null ? collider.bounds : new Bounds(Vector3.zero, Vector3.zero);
        }

        private void Update()
        {
            if (!m_enableShadowUpdates || m_hidden || m_groundingShadow == null)
            {
                return;
            }

            UpdateGroundingShadow();
        }

        private void UpdateGroundingShadow()
        {
            if (m_raycastManager == null) return;
            // Check if depth manager is ready to avoid error spam
            if (m_depthManager == null || !m_depthManager.enabled || !m_depthManager.gameObject.activeInHierarchy) return;

            var downwardRay = new Ray(m_trackedObject.position, Vector3.down);
            if (!m_raycastManager.Raycast(downwardRay, out var shadowHitInfo)) return;

            var distanceToSurface = Vector3.Distance(m_trackedObject.position, shadowHitInfo.point);

            if (distanceToSurface < SHADOW_MIN_DISTANCE)
            {
                m_groundingShadowRenderer.enabled = false;
                return;
            }

            m_groundingShadowRenderer.enabled = true;

            var targetShadowPosition = new Vector3(
                m_trackedObject.position.x, shadowHitInfo.point.y, m_trackedObject.position.z);
            m_groundingShadow.position = Vector3.SmoothDamp(
                m_groundingShadow.position, targetShadowPosition, ref m_shadowPositionVelocity, SMOOTH_TIME);
            m_groundingShadow.rotation = Quaternion.Euler(90, m_trackedObject.eulerAngles.y, 0);

            var distanceFactor = Mathf.InverseLerp(SHADOW_MAX_DISTANCE, 0, distanceToSurface);
            var targetSize = Vector2.Lerp(m_initialShadowSize * SHADOW_SCALER, m_initialShadowSize, distanceFactor);
            m_groundingShadowRenderer.size = targetSize;

            var alpha = Mathf.Clamp01(distanceFactor) * MAX_SHADOW_TINT;
            var currentColor = m_groundingShadowRenderer.color;

            currentColor.a = alpha;
            m_groundingShadowRenderer.color = currentColor;
        }

        /// <summary>
        /// When enabled, the shadow position and opacity will be updated in real-time based on the object's proximity to surfaces.
        /// When disabled, the shadow remains static in its last position.
        /// </summary>
        /// <param name="enable">If true, enables shadow updates; if false, disables them.</param>
        public void EnableShadowUpdates(bool enable)
        {
            m_enableShadowUpdates = enable;
        }
    }
}
