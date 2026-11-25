// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.IO;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// How a reconstructed object is normalised, scaled to the user's box and posed. Everything here is the
    /// v1 arithmetic of <c>GltfLoader</c>, moved
    /// out of the loader so it can be tested without an addressable template or a device.
    ///
    /// The paper (SpatialBlend section 6, "Arbitrary physical objects", footnote): "we recover metric scale from
    /// a bounding box the user draws around the object." The server's mesh has no metric scale. The model is
    /// normalised to a longest side of 1 about its centre, and the root takes the scale that makes its height
    /// the box height.
    /// </summary>
    public static class ScanObjectFit
    {
        /// <summary>The node the reconstruction puts its mesh on (v1 NormalizeModel).</summary>
        public const string GeometryNodeName = "geometry_0";

        /// <summary>v1 NormalizeModel skips a mesh whose longest side is below this.</summary>
        public const float MinimumMeshExtent = 1e-6f;

        /// <summary>v1 ApplyTransform used the box scale only above this normalised height.</summary>
        public const float MinimumNormalizedHeight = 0.0001f;

        public struct Normalization
        {
            /// <summary>The mesh bounds centre before normalisation.</summary>
            public Vector3 MeshCenter;
            /// <summary>The longest side of the mesh bounds before normalisation.</summary>
            public float MaxExtent;

            public static Normalization Identity => new Normalization { MeshCenter = Vector3.zero, MaxExtent = 1f };
        }

        /// <summary>The geometry node of a loaded object: a direct child, else the first below it.</summary>
        public static Transform FindGeometry(Transform root)
        {
            if (root == null) return null;
            Transform direct = root.Find(GeometryNodeName);
            if (direct != null) return direct;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != root && child.name == GeometryNodeName) return child;
            }
            return null;
        }

        /// <summary>
        /// v1 NormalizeModel: the bounding-box centre goes to the root origin and the longest side becomes 1.
        /// Only geometry_0's local transform changes. A missing mesh or a degenerate one leaves the model alone
        /// and reports the identity.
        /// </summary>
        public static Normalization Normalize(GameObject modelRoot)
        {
            Normalization info = Normalization.Identity;
            Transform geometry = FindGeometry(modelRoot == null ? null : modelRoot.transform);
            if (geometry == null)
            {
                Debug.LogWarning("[ScanObjectFit] Normalize: geometry_0 not found");
                return info;
            }
            var meshFilter = geometry.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning("[ScanObjectFit] Normalize: no MeshFilter/sharedMesh");
                return info;
            }

            Bounds bounds = meshFilter.sharedMesh.bounds;
            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (maxExtent < MinimumMeshExtent)
            {
                Debug.LogWarning("[ScanObjectFit] Normalize: maxExtent near zero, skipping");
                return info;
            }

            info.MeshCenter = bounds.center;
            info.MaxExtent = maxExtent;
            float s = 1f / maxExtent;
            geometry.localPosition = -bounds.center * s;
            geometry.localScale = Vector3.one * s;
            geometry.localRotation = Quaternion.identity;
            return info;
        }

        /// <summary>
        /// v1 FitCollidersToMesh: the one box collider follows geometry_0, so the collider (on the root) matches what is drawn. The
        /// mesh bounds are taken into the root's frame through the geometry's whole transform (v1 read only its local position and
        /// scale, which is the same while the geometry is an unturned direct child).
        /// </summary>
        public static void FitCollider(GameObject modelRoot)
        {
            if (modelRoot == null) return;
            BoxCollider[] colliders = modelRoot.GetComponentsInChildren<BoxCollider>();
            if (colliders.Length != 1) return;
            Transform geometry = FindGeometry(modelRoot.transform);
            if (geometry == null) return;
            var meshFilter = geometry.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return;
            Bounds bounds = TransformBounds(modelRoot.transform.worldToLocalMatrix * geometry.localToWorldMatrix,
                meshFilter.sharedMesh.bounds);
            BoxCollider collider = colliders[0];
            collider.center = bounds.center;
            collider.size = bounds.size;
        }

        /// <summary>The world-space box around geometry_0's mesh as it is now; false when there is no geometry mesh.</summary>
        public static bool TryGeometryWorldBounds(GameObject modelRoot, out Bounds bounds)
        {
            bounds = default;
            Transform geometry = FindGeometry(modelRoot == null ? null : modelRoot.transform);
            var meshFilter = geometry != null ? geometry.GetComponent<MeshFilter>() : null;
            if (meshFilter == null || meshFilter.sharedMesh == null) return false;
            bounds = TransformBounds(geometry.localToWorldMatrix, meshFilter.sharedMesh.bounds);
            return true;
        }

        // The axis-aligned box around the eight corners of `local` mapped by `m` (its centre is m applied to local's centre).
        private static Bounds TransformBounds(Matrix4x4 m, Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            var result = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(m.MultiplyPoint3x4(c + corner));
            }
            return result;
        }

        /// <summary>v1 GetMeshWorldHeight: the world height of the first renderer's bounds.</summary>
        public static float MeshWorldHeight(GameObject modelRoot)
        {
            Renderer renderer = modelRoot == null ? null : modelRoot.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.bounds.size.y : 0f;
        }

        /// <summary>v1 fullScale = anchorHeight / normalizedHeight, and 1 when the height is degenerate.</summary>
        public static float BoxFitScale(float boxHeight, float normalizedHeight) =>
            normalizedHeight > MinimumNormalizedHeight ? boxHeight / normalizedHeight : 1f;

        /// <summary>
        /// v1 ApplyTransform, anchored branch: the root takes the box centre, the given rotation and the box-fit scale.
        /// The height is the model's world height once it is turned (v1 measured it unturned, so a server pose that stands a Z-up
        /// reconstruction upright fitted the box height to the model's depth). Returns the scale, which is the one the peers are told.
        /// </summary>
        public static float PlaceInBox(GameObject modelRoot, ScanRegionHint box, Quaternion rotation)
        {
            modelRoot.transform.SetPositionAndRotation(box.BoxCenter, rotation);
            // Measured in the world as the root is now and scaled from there: the world height grows with the root's local
            // scale, so the fit holds whatever the root's current scale or its parent's.
            float worldHeight = TryGeometryWorldBounds(modelRoot, out Bounds turned) ? turned.size.y : MeshWorldHeight(modelRoot);
            float current = Mathf.Abs(modelRoot.transform.localScale.y);
            float fullScale = worldHeight > MinimumNormalizedHeight && current > 1e-6f
                ? BoxFitScale(box.BoxSize.y, worldHeight) * current
                : 1f;
            modelRoot.transform.localScale = Vector3.one * fullScale;
            return fullScale;
        }

        /// <summary>
        /// Leaves the root turned only about the vertical, keeping the model where and how it is drawn: the rest of the rotation (the
        /// server's upright correction, a Z-up reconstruction stood up) moves onto geometry_0. The root is the object's
        /// frame (its declared region, the pose the core and the peers see), so it must stay level; the correction is what the peers
        /// are told (<see cref="SbNativeEntity.SetUprightRotation"/>). Returns the correction: geometry_0's rotation in the root.
        /// </summary>
        public static Quaternion LevelRoot(GameObject modelRoot)
        {
            if (modelRoot == null) return Quaternion.identity;
            Transform root = modelRoot.transform;
            Transform geometry = FindGeometry(root);
            var level = Quaternion.Euler(0f, root.rotation.eulerAngles.y, 0f);   // Unity's Euler order: R = Ry * Rx * Rz
            if (geometry == null)
            {
                root.rotation = level;
                return Quaternion.identity;
            }
            Vector3 at = geometry.position;
            Quaternion turned = geometry.rotation;
            root.rotation = level;
            geometry.SetPositionAndRotation(at, turned);
            return Quaternion.Inverse(root.rotation) * geometry.rotation;
        }

        /// <summary>
        /// A peer's copy takes the source's upright correction (<see cref="LevelRoot"/>): geometry_0, normalised, is turned by it about
        /// its mesh centre, which stays at its parent's origin, so the copy stands as the source's does and its fit matches.
        /// </summary>
        public static void ApplyUpright(GameObject contentRoot, Quaternion upright)
        {
            Transform geometry = FindGeometry(contentRoot == null ? null : contentRoot.transform);
            var meshFilter = geometry != null ? geometry.GetComponent<MeshFilter>() : null;
            if (meshFilter == null || meshFilter.sharedMesh == null) return;
            geometry.localRotation = upright;
            geometry.localPosition = -(upright * Vector3.Scale(geometry.localScale, meshFilter.sharedMesh.bounds.center));
        }

        /// <summary>v1 kPreviewFactor: the scan is shown at this fraction of its box-fit size.</summary>
        public const float PreviewFactor = 0.3f;

        /// <summary>v1 gap: the preview floats above the box top by this fraction of the box height.</summary>
        public const float PreviewGapOfBoxHeight = 0.2f;

        /// <summary>
        /// The scan preview (as v1): only the visual geometry
        /// shrinks to <see cref="PreviewFactor"/>, about its own centre, and hovers centred over the box, its underside a gap of
        /// <see cref="PreviewGapOfBoxHeight"/> box heights above the box top. The root keeps the box-fit scale, so the peers' copy and
        /// the declared region are at full size. Placed in world space, from the mesh's world bounds: v1 added the lift along the
        /// root's own up axis, which a server rotation that is not a pure turn (a reconstruction stood upright) points sideways. Call
        /// it right after <see cref="PlaceInBox"/>. The collider is fitted again to the lifted geometry. A root without a geometry
        /// mesh is left as it is.
        /// </summary>
        public static void ApplyPreview(GameObject modelRoot, ScanRegionHint box)
        {
            Transform visual = FindGeometry(modelRoot == null ? null : modelRoot.transform);
            if (visual == null || !TryGeometryWorldBounds(modelRoot, out _)) return;
            visual.localScale *= PreviewFactor;
            TryGeometryWorldBounds(modelRoot, out Bounds shown);
            var centre = new Vector3(box.BoxCenter.x, box.YTop + box.BoxSize.y * PreviewGapOfBoxHeight + shown.size.y * 0.5f,
                box.BoxCenter.z);
            visual.position += centre - shown.center;
            FitCollider(modelRoot);
        }

        /// <summary>The mesh centre of geometry_0 in its own frame: the point the preview turns about (it stays over the box).</summary>
        public static Vector3 GeometryPivot(Transform geometry)
        {
            var meshFilter = geometry != null ? geometry.GetComponent<MeshFilter>() : null;
            return meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds.center : Vector3.zero;
        }

        /// <summary>
        /// v1 ApplyTransform, unanchored branch: the server pose composed with the inverse of the
        /// normalisation, so the normalised mesh lands where the server's mesh was.
        /// </summary>
        public static void PlaceAtServerPose(GameObject modelRoot, Matrix4x4 objectToWorld, Normalization normalization)
        {
            Matrix4x4 inverse = Matrix4x4.TRS(normalization.MeshCenter, Quaternion.identity,
                Vector3.one * normalization.MaxExtent);
            Matrix4x4 root = objectToWorld * inverse;
            modelRoot.transform.position = (Vector3)root.GetColumn(3);
            modelRoot.transform.rotation = root.rotation;
            modelRoot.transform.localScale = root.lossyScale;
        }

        /// <summary>
        /// What a peer does to the content it received (v1 LoadModelFromBytes): the same
        /// normalisation the source made, the source's upright correction, then the scale the source told it. Content that came
        /// without a shared scale is left as it is.
        /// </summary>
        public static void FitReplica(GameObject target)
        {
            if (target == null) return;
            var entity = target.GetComponent<SbNativeEntity>();
            if (entity == null || !entity.TryGetSharedScale(out Vector3 sharedScale)) return;
            Transform content = target.transform.Find("GLTF");
            GameObject contentRoot = content != null ? content.gameObject : target;
            LiftWorldNode(contentRoot);
            Normalize(contentRoot);
            if (entity.TryGetUprightRotation(out Quaternion upright)) ApplyUpright(contentRoot, upright);
            FitCollider(target);
            target.transform.localScale = sharedScale;
        }

        /// <summary>
        /// v1 CleanUpWorldNode: the children of the "world" node move up to the root, keeping their
        /// world pose, and the node is removed. A model without one is left alone.
        /// </summary>
        public static void LiftWorldNode(GameObject modelRoot)
        {
            if (modelRoot == null) return;
            Transform world = modelRoot.transform.Find("world");
            if (world == null)
            {
                foreach (Transform child in modelRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (child != modelRoot.transform && child.name == "world") { world = child; break; }
                }
            }
            if (world == null) return;
            while (world.childCount > 0) world.GetChild(0).SetParent(modelRoot.transform, true);
            if (Application.isPlaying) Object.Destroy(world.gameObject);
            else Object.DestroyImmediate(world.gameObject);
        }

        // info files

        /// <summary>"{prefix}_caminfo.json": the camera pose at capture, copied beside the output by SessionManager.</summary>
        public static string CameraInfoPath(string modelPath) => SiblingPath(modelPath, ScanResponseParser.CameraInfoSuffix);

        /// <summary>"{prefix}_objinfo.json": the server's object pose, written by the session library.</summary>
        public static string ObjectInfoPath(string modelPath) => SiblingPath(modelPath, ScanResponseParser.InfoSuffix);

        private static string SiblingPath(string modelPath, string suffix)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            string prefix = modelPath.EndsWith(ScanResponseParser.ModelSuffix, System.StringComparison.Ordinal)
                ? modelPath.Substring(0, modelPath.Length - ScanResponseParser.ModelSuffix.Length)
                : Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(modelPath));
            return prefix + suffix;
        }

        [System.Serializable]
        private class ObjectRoot { public ObjectTransform obj_2_pt3d_transform; }

        [System.Serializable]
        private class ObjectTransform { public float[] matrix; }

        [System.Serializable]
        private class CameraRoot { public CameraTransform c2w; }

        [System.Serializable]
        private class CameraTransform { public float[] matrix; }

        /// <summary>
        /// v1 ApplyTransform: reads both files and composes the object-to-world matrix,
        /// <c>cam_2_world * rhs_2_lhs * obj_2_pt3d * lhs_2_rhs</c>, where the two handedness matrices flip X.
        /// False when a file is missing or unreadable.
        /// </summary>
        public static bool TryReadObjectToWorld(string cameraInfoPath, string objectInfoPath, out Matrix4x4 objectToWorld)
        {
            objectToWorld = Matrix4x4.identity;
            if (string.IsNullOrEmpty(cameraInfoPath) || !File.Exists(cameraInfoPath)) return false;
            if (string.IsNullOrEmpty(objectInfoPath) || !File.Exists(objectInfoPath)) return false;
            try
            {
                ObjectRoot obj = JsonUtility.FromJson<ObjectRoot>(File.ReadAllText(objectInfoPath));
                CameraRoot cam = JsonUtility.FromJson<CameraRoot>(File.ReadAllText(cameraInfoPath));
                if (obj?.obj_2_pt3d_transform?.matrix == null || cam?.c2w?.matrix == null) return false;
                objectToWorld = ObjectToWorld(obj.obj_2_pt3d_transform.matrix, cam.c2w.matrix);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ScanObjectFit] Transform error: {e.Message}");
                return false;
            }
        }

        public static Matrix4x4 ObjectToWorld(float[] objectToPytorch3d, float[] cameraToWorld)
        {
            Matrix4x4 obj2pt3d = ArrayToMatrix(objectToPytorch3d);
            Matrix4x4 cam2world = ArrayToMatrix(cameraToWorld);
            // Pytorch3D (right-handed) to Unity (left-handed).
            Matrix4x4 lhs2rhs = Matrix4x4.Scale(new Vector3(-1, 1, 1));
            Matrix4x4 rhs2lhs = lhs2rhs.transpose;
            return cam2world * rhs2lhs * obj2pt3d * lhs2rhs;
        }

        /// <summary>Row-major, as v1 ArrayToMatrix read it. Anything shorter than 16 is the identity.</summary>
        public static Matrix4x4 ArrayToMatrix(float[] values)
        {
            Matrix4x4 m = Matrix4x4.identity;
            if (values == null || values.Length < 16) return m;
            for (int i = 0; i < 16; i++) m[i / 4, i % 4] = values[i];
            return m;
        }
    }
}
