// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;

namespace SpatialBlend
{
    /// <summary>
    /// Retired v1 component, kept only so old content still loads. No runtime code reads it (T3.6).
    /// An object's region is declared by <see cref="SbSpatialAuthoring"/>, and the Editor-only
    /// AffordanceVolumeMigration converts this component into that declaration.
    ///
    /// Defined an affordance volume around an object.
    /// Can be attached to any GameObject - SbEntity is optional.
    /// For FRONT/SURROUND types, a Collider is required for auto-generation.
    /// For PRESET type, the polygon is defined in the editor via _customPoints.
    /// For CUSTOM type, the polygon is set externally at runtime.
    /// </summary>
    [ExecuteAlways]
    public class AffordanceVolume : MonoBehaviour
    {
        public enum AffordanceVolumeType
        {
            NONE,
            FRONT,
            SURROUND,
            PRESET,  // Editor-defined custom points
            CUSTOM   // Runtime-defined externally
        }

        public AffordanceVolumeType type = AffordanceVolumeType.NONE;

        [SerializeField]
        private Collider _sourceCollider;

        [SerializeField]
        private Vector2[] _customPoints = new Vector2[0];

        [SerializeField]
        private Vector2 _customCenterOffset = Vector2.zero;

        public Vector2[] CustomPoints
        {
            get { return _customPoints; }
            set
            {
                _customPoints = value;
                if (type == AffordanceVolumeType.PRESET)
                {
                    GenerateVolume();
                }
            }
        }

        public Vector2 CustomCenterOffset
        {
            get { return _customCenterOffset; }
            set
            {
                _customCenterOffset = value;
                if (type == AffordanceVolumeType.PRESET)
                {
                    GenerateVolume();
                }
            }
        }

		public Collider SourceCollider
		{
			get { return _sourceCollider; }
			set
			{
				_sourceCollider = value;
			}
		}

        private SbEntity sbEntity;
        private PolygonCollider2D polygonCollider;
        public PolygonCollider2D PolygonCollider => polygonCollider;

        public bool HasValidVolume => type != AffordanceVolumeType.NONE && polygonCollider != null && polygonCollider.pathCount > 0;

        public Vector3 EntityToAffordanceCenterTransform
        {
            get
            {
                Vector3 position;
                if (_sourceCollider is BoxCollider boxCollider)
                {
                    position = Vector3.zero;
                }
                else if (type == AffordanceVolumeType.PRESET)
                {
                    // For preset type, use the custom center offset
                    position = new Vector3(_customCenterOffset.x, 0, _customCenterOffset.y);
                }
                else
                {
                    // For other collider types, calculate from bounds center relative to transform
                    Collider refCollider = _sourceCollider != null ? _sourceCollider : GetComponent<Collider>();
                    if (refCollider != null)
                    {
                        // bounds.center is in world space, convert to local
                        position = transform.InverseTransformPoint(refCollider.bounds.center);
                    }
                    else
                    {
                        position = Vector3.zero;
                    }
                }

                return position;
            }
        }

        void Awake()
        {
            EnsureColliderSetup();
        }

        void Start()
        {
        }

        void OnValidate()
        {
            EnsureColliderSetup();
            GenerateVolume();
        }

        private void EnsureColliderSetup()
        {
            if (sbEntity == null)
            {
                sbEntity = GetComponent<SbEntity>();
            }

            if (_sourceCollider == null)
            {
                _sourceCollider = GetComponent<Collider>();
            }

            if (polygonCollider != null)
            {
                return;
            }

            Transform existingChild = transform.Find("AffordanceVolume2D");
            if (existingChild != null)
            {
                polygonCollider = existingChild.GetComponent<PolygonCollider2D>();
            }

            if (polygonCollider == null)
            {
                GameObject volume2D = new GameObject("AffordanceVolume2D");
                volume2D.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
                volume2D.transform.SetParent(transform, false);
                volume2D.transform.localPosition = Vector3.zero;
                volume2D.transform.localRotation = Quaternion.identity;
                volume2D.transform.localScale = Vector3.one;
                polygonCollider = volume2D.AddComponent<PolygonCollider2D>();
                polygonCollider.isTrigger = true;
            }
        }

        public void GenerateVolume()
        {
            if (!isActiveAndEnabled || polygonCollider == null)
            {
                return;
            }

            // CUSTOM and NONE types don't need a reference collider or SbEntity
            if (type == AffordanceVolumeType.CUSTOM || type == AffordanceVolumeType.NONE)
            {
                if (type == AffordanceVolumeType.NONE)
                {
                    polygonCollider.pathCount = 0;
                }
                // CUSTOM shape is defined externally, nothing to generate
                return;
            }

            // FRONT and SURROUND types need a reference collider
            Collider referenceCollider = _sourceCollider != null ? _sourceCollider : GetComponent<Collider>();
            if (referenceCollider == null)
            {
                Debug.LogWarning($"[AffordanceVolume] No collider found to generate volume on {gameObject.name}");
                polygonCollider.pathCount = 0;
                return;
            }

            switch (type)
            {
                case AffordanceVolumeType.FRONT:
                    {
                        Bounds bounds = referenceCollider.bounds;
                        float width = bounds.size.x;
                        float height = bounds.size.y;

						Vector2[] points = new Vector2[4];
						points[0] = new Vector2(-width / 2, 0);
						points[1] = new Vector2(width / 2, 0);
						points[2] = new Vector2(width / 2, -height);
						points[3] = new Vector2(-width / 2, -height);

						// If a BoxCollider is provided, offset the footprint by its center (XZ)
						if (_sourceCollider is BoxCollider)
						{
                            BoxCollider boxCollider = (BoxCollider)_sourceCollider;
							for (int i = 0; i < points.Length; i++)
							{
								points[i].x += boxCollider.center.x;
								points[i].y += boxCollider.center.y;
							}
						}

						polygonCollider.SetPath(0, points);

                    }
                    break;
                case AffordanceVolumeType.SURROUND:
                    {
                        Bounds bounds = referenceCollider.bounds;
                        float radius = Mathf.Max(bounds.size.x, bounds.size.z);

						Vector2[] points = new Vector2[8];
						for (int i = 0; i < 8; i++)
						{
							float angle = i * Mathf.PI * 2 / 8;
							points[i] = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
						}

						// Offset by BoxCollider center if provided
						if (_sourceCollider is BoxCollider)
						{
                            BoxCollider boxCollider = (BoxCollider)_sourceCollider;
							for (int i = 0; i < points.Length; i++)
							{
								points[i].x += boxCollider.center.x;
								points[i].y += boxCollider.center.z;
							}
						}
						// TODO(Dongho): Don't we have to set the values in world space?
						polygonCollider.SetPath(0, points);

                    }
                    break;
                case AffordanceVolumeType.PRESET:
                    if (_customPoints != null && _customPoints.Length >= 3)
                    {
                        Vector2[] points = new Vector2[_customPoints.Length];
                        for (int i = 0; i < _customPoints.Length; i++)
                        {
                            points[i] = _customPoints[i] + _customCenterOffset;
                        }
                        polygonCollider.SetPath(0, points);
                    }
                    else
                    {
                        polygonCollider.pathCount = 0;
                    }
                    break;
                case AffordanceVolumeType.NONE:
                    polygonCollider.pathCount = 0;
                    break;
            }
        }


        void OnDrawGizmos()
        {
            // Draw with the Transform's space -> world space transformation
            Gizmos.matrix = transform.localToWorldMatrix;
            if (_sourceCollider != null)
            {
                Gizmos.color = Color.green;
                if (_sourceCollider is BoxCollider)
                {
                    BoxCollider boxCollider = (BoxCollider)_sourceCollider;
                    Gizmos.DrawWireCube(boxCollider.center, boxCollider.size);
                }
            }
            if (polygonCollider != null && polygonCollider.pathCount > 0)
            {
                Gizmos.color = Color.red;
			    Vector2[] points = polygonCollider.GetPath(0);
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 from = new Vector3(points[i].x, 0, points[i].y);
                    Vector3 to = new Vector3(points[(i + 1) % points.Length].x, 0, points[(i + 1) % points.Length].y);
                    Gizmos.DrawLine(from, to);
                }
            }
        }
    }
}
