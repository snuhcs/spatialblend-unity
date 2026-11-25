// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// What a drop does (or, for the preview, would do), from the placement motif's own drop code: where the object is let go, where
    /// it lands after the snap and the room rule (<see cref="SpatialBlend.RoomFit"/>), and whether that is a valid pose.
    /// </summary>
    public readonly struct PlacementDrop
    {
        public PlacementDrop(PlacementMotif motif, Pose candidate, Pose landed, bool settles, bool placed, bool onWall, bool approximate = false,
                             bool reverted = false, bool offFloor = false)
        {
            Approximate = approximate;
            Reverted = reverted;
            OffFloor = offFloor;
            Motif = motif;
            Candidate = candidate;
            Landed = landed;
            Settles = settles;
            Placed = placed;
            OnWall = onWall;
        }

        public readonly PlacementMotif Motif;
        /// <summary>The pose the drop starts from: where it was let go, snapped (to the floor or a wall) before the room rule.</summary>
        public readonly Pose Candidate;
        /// <summary>Where the object comes to rest.</summary>
        public readonly Pose Landed;
        /// <summary>False when the drop leaves the object where it was let go (a snapping object let go too far above the floor, or no floor found).</summary>
        public readonly bool Settles;
        /// <summary>The room rule placed it: <see cref="Landed"/> is a valid pose (pulled to one when needed).</summary>
        public readonly bool Placed;
        /// <summary>A wall-mounted object that lands on a wall (<see cref="SpatialBlend.RoomFit.IsOnWall"/>: at a wall, facing into the room).</summary>
        public readonly bool OnWall;
        /// <summary>
        /// A preview only, from its bounded search: with <see cref="Placed"/>, <see cref="Landed"/> is the coarse spot (within 10 cm of the
        /// drop's, which refines it); without, no spot was found within the preview's reach and the drop searches farther (and turns).
        /// </summary>
        public readonly bool Approximate;
        /// <summary>A release the rule placed nowhere: the object goes back where it was before the grab (<see cref="Landed"/>).</summary>
        public readonly bool Reverted;
        /// <summary>With <see cref="Reverted"/>: it was let go with no floor under it (outside the room).</summary>
        public readonly bool OffFloor;

        /// <summary>The room rule moved it away from <see cref="Candidate"/> (more than a centimetre or a degree).</summary>
        public bool Pulled => Settles && Placed &&
                              ((Landed.position - Candidate.position).sqrMagnitude > 1e-4f || Quaternion.Angle(Landed.rotation, Candidate.rotation) > 1f);
    }

    /// <summary>
    /// The drop preview of a held object: a hologram of the object where the drop will land (a
    /// visual copy of its renderers, the shared meshes under one translucent rim-lit material: <c>Sb/PlacementHologram</c>) and a
    /// thin, faint outline of its region on the floor. Soft cyan: it lands where it is let go (or on the wall spot the slide finds).
    /// Amber: it will be moved, and the hologram is where it goes (to within the coarse grid); with no hologram, it goes farther than
    /// the preview looks. Nothing fits in this room: no hologram, a faint red outline. Visual only; the motif computes it at most ten
    /// times a second from the drop's own code. The copy is built once per grab, has no collider and no behaviour, and is destroyed
    /// on the release; the lines and the colour block are reused (no allocation per frame).
    /// </summary>
    [AddComponentMenu("")]
    public sealed class PlacementPreview : MonoBehaviour
    {
        public static readonly Color ValidColor = new Color(0.45f, 0.95f, 0.85f, 0.9f);
        public static readonly Color PulledColor = new Color(1f, 0.74f, 0.3f, 0.9f);
        public static readonly Color UnfitColor = new Color(0.95f, 0.38f, 0.32f, 0.9f);

        /// <summary>The region outline: thin and faint (the hologram is what is looked at).</summary>
        public const float LineWidth = 0.003f;
        public const float RegionAlpha = 0.35f;
        /// <summary>The region outline is drawn this far above the floor, so it is not lost in it.</summary>
        public const float FloorLift = 0.01f;
        public const string HologramShader = "Sb/PlacementHologram";

        private static Material s_lineMaterial;
        private static Material s_hologramMaterial;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private LineRenderer _region;
        private Vector3[] _ring = new Vector3[16];
        private Transform _ghost;
        private Renderer[] _ghostRenderers;
        private MaterialPropertyBlock _block;
        private Color _blockColour;

        /// <summary>The hologram is shown.</summary>
        public bool Visible => _ghost != null && _ghost.gameObject.activeSelf;
        /// <summary>The region outline is drawn.</summary>
        public bool RegionVisible => _region != null && _region.enabled;
        /// <summary>The hologram copy (null before the first preview of a grab and after the release).</summary>
        public Transform Ghost => _ghost;
        /// <summary>Where the hologram stands (the drop's landing pose) while visible.</summary>
        public Pose GhostPose { get; private set; }
        public Color Colour { get; private set; }
        /// <summary>The region outline's points while visible (the first <see cref="RegionPointCount"/>).</summary>
        public int RegionPointCount => _region != null && _region.enabled ? _region.positionCount : 0;

        public static PlacementPreview Create(string owner)
        {
            var go = new GameObject("PlacementPreview " + owner);
            var preview = go.AddComponent<PlacementPreview>();
            var lineObject = new GameObject("Region");
            lineObject.transform.SetParent(go.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.widthMultiplier = LineWidth;
            line.numCornerVertices = 0;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = LineMaterial();
            preview._region = line;
            preview._block = new MaterialPropertyBlock();
            preview.Hide();
            return preview;
        }

        // One material for every region outline (the colour is the line's vertex colour), kept for the app's lifetime.
        private static Material LineMaterial()
        {
            if (s_lineMaterial == null)
                s_lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "PlacementPreviewLine", hideFlags = HideFlags.DontSave };
            return s_lineMaterial;
        }

        /// <summary>The one hologram material every preview shares (the tint is set per renderer through a property block).</summary>
        public static Material HologramMaterial()
        {
            if (s_hologramMaterial == null)
            {
                Shader shader = Shader.Find(HologramShader) ?? Shader.Find("Sprites/Default");
                s_hologramMaterial = new Material(shader) { name = "PlacementHologram", hideFlags = HideFlags.DontSave };
            }
            return s_hologramMaterial;
        }

        public static Color ColourOf(in PlacementDrop drop) => drop.Approximate ? PulledColor : !drop.Placed ? UnfitColor : drop.Pulled ? PulledColor : ValidColor;

        /// <summary>
        /// Shows the drop: <paramref name="region"/> (world x, z at the landing pose; null for none) on the floor at
        /// <paramref name="floorY"/>, and the hologram of <paramref name="source"/> (built on the first call of a grab) at the landing pose.
        /// </summary>
        public void Show(in PlacementDrop drop, Vector2[] region, Transform source, float floorY)
        {
            if (_region == null) return;
            Colour = ColourOf(drop);
            GhostPose = drop.Landed;
            var faint = new Color(Colour.r, Colour.g, Colour.b, Colour.a * RegionAlpha);
            _region.startColor = _region.endColor = faint;

            // A hologram only where the drop has a landing to show: not where nothing fits, not where it goes farther than the preview looks.
            bool ghost = drop.Placed && source != null;
            if (ghost)
            {
                if (_ghost == null) BuildGhost(source);
                _ghost.SetPositionAndRotation(drop.Landed.position, drop.Landed.rotation);
                _ghost.localScale = source.lossyScale;
                if (_blockColour != Colour)
                {
                    _blockColour = Colour;
                    _block.SetColor(ColorId, Colour);
                    foreach (Renderer r in _ghostRenderers) if (r != null) r.SetPropertyBlock(_block);
                }
            }
            if (_ghost != null) _ghost.gameObject.SetActive(ghost);

            if (region != null && region.Length >= 3)
            {
                if (_ring.Length < region.Length) _ring = new Vector3[region.Length * 2];
                _region.positionCount = region.Length;
                for (int i = 0; i < region.Length; ++i)
                {
                    _ring[i] = new Vector3(region[i].x, floorY + FloorLift, region[i].y);
                    _region.SetPosition(i, _ring[i]);
                }
                _region.enabled = true;
            }
            else _region.enabled = false;
        }

        // The copy: every visible mesh of the object, at its place relative to the object's root, with the hologram material on every
        // sub-mesh. Shared meshes, no collider, no behaviour, no network: only Transforms, MeshFilters and MeshRenderers.
        private void BuildGhost(Transform source)
        {
            var root = new GameObject("Hologram");
            root.transform.SetParent(transform, false);
            _ghost = root.transform;
            var renderers = new System.Collections.Generic.List<Renderer>();
            Matrix4x4 toRoot = Matrix4x4.TRS(source.position, source.rotation, source.lossyScale).inverse;
            Material material = HologramMaterial();
            // Of a LOD group, only the nearest level (the hologram is near the user); the others would double the copy.
            var otherLods = new System.Collections.Generic.HashSet<Renderer>();
            foreach (LODGroup group in source.GetComponentsInChildren<LODGroup>())
            {
                LOD[] lods = group.GetLODs();
                for (int l = 1; l < lods.Length; ++l)
                    foreach (Renderer lr in lods[l].renderers) if (lr != null) otherLods.Add(lr);
            }
            foreach (Renderer r in source.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || otherLods.Contains(r)) continue;
                // A skinned mesh is copied in its bind pose (its shared mesh, not the current skinning): the boards have none.
                Mesh mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                          : r is MeshRenderer && r.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh == null) continue;
                Matrix4x4 local = toRoot * r.transform.localToWorldMatrix;
                var part = new GameObject(r.name);
                part.transform.SetParent(_ghost, false);
                part.transform.localPosition = local.GetColumn(3);
                part.transform.localRotation = local.rotation;
                part.transform.localScale = local.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = part.AddComponent<MeshRenderer>();
                var materials = new Material[Mathf.Max(1, mesh.subMeshCount)];
                for (int i = 0; i < materials.Length; ++i) materials[i] = material;
                copy.sharedMaterials = materials;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                copy.receiveShadows = false;
                copy.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                copy.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                renderers.Add(copy);
            }
            _ghostRenderers = renderers.ToArray();
            _blockColour = default;
        }

        /// <summary>Hides the preview and destroys the hologram (the grab is over; the next grab builds its own).</summary>
        public void Hide()
        {
            if (_region != null) _region.enabled = false;
            DestroyGhost();
        }

        /// <summary>Hides the hologram and the outline for now, keeping the copy for the rest of the grab.</summary>
        public void Conceal()
        {
            if (_region != null) _region.enabled = false;
            if (_ghost != null) _ghost.gameObject.SetActive(false);
        }

        private void DestroyGhost()
        {
            if (_ghost == null) return;
            if (Application.isPlaying) Destroy(_ghost.gameObject);
            else DestroyImmediate(_ghost.gameObject);
            _ghost = null;
            _ghostRenderers = null;
        }

        private void OnDestroy() { _ghost = null; _ghostRenderers = null; }
    }
}
