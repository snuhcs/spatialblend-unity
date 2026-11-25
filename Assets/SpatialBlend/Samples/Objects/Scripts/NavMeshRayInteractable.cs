// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.AI;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using Meta.XR.MRUtilityKit;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Creates a ray-interactable surface from a NavMesh by generating a MeshCollider from NavMesh triangulation,
    /// wrapping it with ColliderSurface, and adding a RayInteractable component.
    /// This component should be attached to the same GameObject as SceneNavigation.
    /// </summary>
    public class NavMeshRayInteractable : MonoBehaviour
    {
        private SceneNavigation _sceneNavigation;
        private MeshCollider _meshCollider;
        private ColliderSurface _colliderSurface;
        private RayInteractable _rayInteractable;
        private Mesh _navMeshMesh;

        private void Awake()
        {
            _sceneNavigation = GetComponent<SceneNavigation>();
            if (_sceneNavigation == null)
            {
                Debug.LogError($"[NavMeshRayInteractable] SceneNavigation component not found on {gameObject.name}. " +
                               "NavMeshRayInteractable must be on the same GameObject as SceneNavigation.");
            }
        }

        private void OnEnable()
        {
            if (_sceneNavigation != null)
            {
                _sceneNavigation.OnNavMeshInitialized.AddListener(InitializeFromNavMesh);
            }
        }

        private void OnDisable()
        {
            if (_sceneNavigation != null)
            {
                _sceneNavigation.OnNavMeshInitialized.RemoveListener(InitializeFromNavMesh);
            }
        }

        /// <summary>
        /// Initializes the ray-interactable surface from the current NavMesh.
        /// This is called automatically when SceneNavigation.OnNavMeshInitialized is triggered.
        /// </summary>
        public void InitializeFromNavMesh()
        {
            // Clean up previous components if they exist
            Cleanup();

            // Get NavMesh triangulation
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();

            if (triangulation.vertices == null || triangulation.vertices.Length == 0 ||
                triangulation.indices == null || triangulation.indices.Length == 0)
            {
                Debug.LogWarning("[NavMeshRayInteractable] NavMesh triangulation is empty. Cannot create interactable surface.");
                return;
            }

            // Create Unity Mesh from triangulation
            _navMeshMesh = CreateMeshFromTriangulation(triangulation);
            if (_navMeshMesh == null)
            {
                Debug.LogError("[NavMeshRayInteractable] Failed to create mesh from NavMesh triangulation.");
                return;
            }

            // Create MeshCollider
            _meshCollider = gameObject.AddComponent<MeshCollider>();
            _meshCollider.sharedMesh = _navMeshMesh;
            _meshCollider.convex = false; // NavMesh surfaces are typically not convex

            // Create ColliderSurface and inject the MeshCollider
            _colliderSurface = gameObject.AddComponent<ColliderSurface>();
            _colliderSurface.InjectCollider(_meshCollider);

            // Create RayInteractable and inject the ColliderSurface as ISurface
            _rayInteractable = gameObject.AddComponent<RayInteractable>();
            _rayInteractable.InjectSurface(_colliderSurface);
        }

        /// <summary>
        /// Creates a Unity Mesh from NavMesh triangulation data.
        /// </summary>
        private Mesh CreateMeshFromTriangulation(NavMeshTriangulation triangulation)
        {
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                return null;
            }

            Mesh mesh = new Mesh();
            mesh.name = "NavMeshSurface";

            // Set vertices
            mesh.vertices = triangulation.vertices;

            // Set triangles (indices)
            mesh.triangles = triangulation.indices;

            // Recalculate normals and bounds
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// Cleans up previously created components and mesh.
        /// </summary>
        private void Cleanup()
        {
            // Remove RayInteractable
            if (_rayInteractable != null)
            {
                DestroyImmediate(_rayInteractable);
                _rayInteractable = null;
            }

            // Remove ColliderSurface
            if (_colliderSurface != null)
            {
                DestroyImmediate(_colliderSurface);
                _colliderSurface = null;
            }

            // Remove MeshCollider
            if (_meshCollider != null)
            {
                DestroyImmediate(_meshCollider);
                _meshCollider = null;
            }

            // Destroy mesh
            if (_navMeshMesh != null)
            {
                DestroyImmediate(_navMeshMesh);
                _navMeshMesh = null;
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}

