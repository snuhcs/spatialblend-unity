// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.AI;
using Meta.XR.MRUtilityKit;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class NavMeshVisualizer : MonoBehaviour
    {
        [SerializeField] private SceneNavigation _sceneNavigation;
        [SerializeField] private Material _material;
        [SerializeField] private Color _color = new Color(0f, 1f, 1f, 0.25f);
        [SerializeField] private bool _visible;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            if (_material == null)
            {
                // Fallback material if none assigned
                var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                _material = new Material(shader);
                TrySetColor(_material, _color);
            }

            _meshRenderer.sharedMaterial = _material;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.enabled = _visible;

            if (_sceneNavigation != null)
            {
                _sceneNavigation.OnNavMeshInitialized.AddListener(Rebuild);
            }
        }

        private void OnDestroy()
        {
            if (_sceneNavigation != null)
            {
                _sceneNavigation.OnNavMeshInitialized.RemoveListener(Rebuild);
            }
            if (_mesh != null)
            {
                Destroy(_mesh);
            }
            // Do not destroy shared material if user assigned one via inspector
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_meshRenderer != null)
            {
                _meshRenderer.enabled = visible;
            }
            if (visible)
            {
                Rebuild();
            }
        }

        public void Rebuild()
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                ClearMesh();
                return;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "RuntimeNavMeshVisualizer" };
#if UNITY_2018_1_OR_NEWER
                _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
#endif
                _meshFilter.sharedMesh = _mesh;
            }

            _mesh.Clear();
            _mesh.vertices = tri.vertices;
            _mesh.triangles = tri.indices;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void ClearMesh()
        {
            if (_mesh != null)
            {
                _mesh.Clear();
            }
        }

        private static void TrySetColor(Material mat, Color c)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }
    }
}