// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// How the real room's scene anchors are drawn: the effect mesh's material, tinted with <see cref="UiPalette.RealAnchor"/>, so
    /// real furniture reads differently from virtual objects (the accent) and box annotations (green). The package's own material
    /// asset is never edited: a copy carries the colour.
    /// </summary>
    public static class SceneAnchorStyle
    {
        public static Color OutlineColor => UiTheme.Palette.RealAnchor;

        // themed copy -> the material it was made from, so Restore can put the original back.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Material, Material> Sources =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Material, Material>();

        /// <summary>A copy of <paramref name="source"/> with its colour set to the real-anchor token, or null when there is no source.</summary>
        public static Material Themed(Material source)
        {
            if (source == null) return null;
            var copy = new Material(source) { name = source.name + " (real anchor)" };
            Sources.Add(copy, source);
            var color = OutlineColor;
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", color);
            if (copy.HasProperty("_BaseColor")) copy.SetColor("_BaseColor", color);
            return copy;
        }

        /// <summary>Puts the original material back on the mesh (new meshes and the existing ones) in place of a themed copy, so the copy can be destroyed.</summary>
        public static void Restore(EffectMesh mesh, Material themed)
        {
            if (mesh == null || themed == null || !Sources.TryGetValue(themed, out Material source) || source == null) return;
            if (mesh.MeshMaterial == themed) mesh.MeshMaterial = source;
            foreach (var pair in mesh.EffectMeshObjects)
            {
                var renderer = pair.Value.effectMeshGO != null ? pair.Value.effectMeshGO.GetComponent<MeshRenderer>() : null;
                if (renderer != null && renderer.sharedMaterial == themed) renderer.sharedMaterial = source;
            }
        }

        /// <summary>
        /// Makes the effect mesh draw its anchors in the real-anchor colour: new meshes (its MeshMaterial) and the ones it already
        /// made (their shared material, not an instance). <paramref name="themed"/> is the caller's cached copy, made on first use.
        /// </summary>
        public static void Apply(EffectMesh mesh, ref Material themed)
        {
            if (mesh == null) return;
            if (themed == null)
            {
                Material source = mesh.MeshMaterial;
                if (source == null)
                {
                    foreach (var pair in mesh.EffectMeshObjects)
                    {
                        var renderer = pair.Value.effectMeshGO != null ? pair.Value.effectMeshGO.GetComponent<MeshRenderer>() : null;
                        if (renderer != null && renderer.sharedMaterial != null) { source = renderer.sharedMaterial; break; }
                    }
                }
                themed = Themed(source);
                if (themed == null) return;
            }
            mesh.MeshMaterial = themed;
            foreach (var pair in mesh.EffectMeshObjects)
            {
                var renderer = pair.Value.effectMeshGO != null ? pair.Value.effectMeshGO.GetComponent<MeshRenderer>() : null;
                if (renderer != null) renderer.sharedMaterial = themed;
            }
        }
    }
}
