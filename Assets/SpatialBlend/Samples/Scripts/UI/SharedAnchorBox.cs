// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The box of a real object shared with the other room: drawn like the scan box (thin edges, L-shaped corner
    /// brackets a quarter of each edge and at most 9 cm, as <c>AnnotationLook</c>, and a faint fill) in its own colour,
    /// <see cref="UiPalette.SharedReal"/> (violet), so a shared real object is told apart from a scan box (mint), the placement
    /// hologram (cyan, amber) and unshared furniture (orange) by its hue. Built once when its object is registered: one polyline for
    /// the twelve edges, one per corner bracket, and a fill of a unit cube shared by every box; every part draws with the one shared
    /// outline material (<see cref="SceneAnchorHighlight.LineMaterial"/>, vertex-coloured). A box still to place and a highlighted row
    /// add an emphasis over the calm box (<see cref="BuildEmphasis"/>: thicker, brighter brackets, a stronger fill), never a second
    /// box. Nothing is rebuilt or animated per frame.
    /// </summary>
    public static class SharedAnchorBox
    {
        public const float EdgeWidth = 0.006f;
        public const float BracketWidth = 0.014f;
        public const float EdgeAlpha = 0.6f;
        public const float BracketAlpha = 0.85f;
        public const float FillAlpha = 0.07f;
        // The emphasis (a box to place, a highlighted row): thicker, brighter brackets and a stronger fill over the calm base.
        public const float EmphasisBracketWidth = 0.026f;
        public const float EmphasisWhiten = 0.35f;
        public const float EmphasisFillAlpha = 0.2f;
        // The scan box's corner brackets (AnnotationLook): a quarter of the edge, between 1.2 and 9 cm.
        public const float BracketFraction = 0.25f;
        public const float BracketMax = 0.09f;
        public const float BracketMin = 0.012f;

        public static Color Colour => UiTheme.Palette.SharedReal;

        private static Mesh s_fill, s_emphasisFill;

        /// <summary>The unit cube every box's fill shares (centred, 1 m, the fill colour in its vertices), made once.</summary>
        public static Mesh FillMesh => s_fill != null ? s_fill : s_fill = Cube("SharedAnchorBoxFill (shared)", FillAlpha);

        /// <summary>The stronger fill of an emphasis, shared the same way.</summary>
        public static Mesh EmphasisFillMesh => s_emphasisFill != null ? s_emphasisFill : s_emphasisFill = Cube("SharedAnchorBoxEmphasisFill (shared)", EmphasisFillAlpha);

        private static Mesh Cube(string name, float alpha)
        {
            var v = new Vector3[8];
            for (int i = 0; i < 8; ++i) v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            Color c = Colour;
            var colours = new Color[8];
            for (int i = 0; i < 8; ++i) colours[i] = new Color(c.r, c.g, c.b, alpha);
            int[] t =
            {
                0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,   0, 1, 4, 1, 5, 4,
                2, 6, 3, 3, 6, 7,   0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,
            };
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave, vertices = v, colors = colours, triangles = t };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The calm box of a shared real object under <paramref name="parent"/> around <paramref name="center"/> and <paramref name="size"/>
        /// (in the parent's space, which should be unscaled: the bracket arms and widths are metres), named <paramref name="name"/>.
        /// </summary>
        public static GameObject Build(Transform parent, Vector3 center, Vector3 size, string name)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            Color c = Colour;
            Edges(root.transform, center, size, new Color(c.r, c.g, c.b, EdgeAlpha));
            Brackets(root.transform, center, size, BracketWidth, new Color(c.r, c.g, c.b, BracketAlpha));
            Fill(root.transform, center, size, FillMesh);
            return root;
        }

        /// <summary>
        /// An emphasis drawn over the calm box (a box still to place, a highlighted row): thicker, brighter corner brackets and a stronger
        /// fill. With <paramref name="withEdges"/> (no calm box under it) it carries the edges too. Never a second full box.
        /// </summary>
        public static GameObject BuildEmphasis(Transform parent, Vector3 center, Vector3 size, string name, bool withEdges)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            Color c = Colour;
            if (withEdges) Edges(root.transform, center, size, new Color(c.r, c.g, c.b, EdgeAlpha));
            Color bright = Color.Lerp(c, Color.white, EmphasisWhiten);
            Brackets(root.transform, center, size, EmphasisBracketWidth, new Color(bright.r, bright.g, bright.b, 1f));
            Fill(root.transform, center, size, EmphasisFillMesh);
            return root;
        }

        private static void Edges(Transform root, Vector3 center, Vector3 size, Color colour)
        {
            Vector3 e = size * 0.5f, n = center - e, x = center + e;
            // One path over all 12 edges (three are walked twice).
            Vector3[] path =
            {
                new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, n.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, n.y, n.z),
                new Vector3(n.x, x.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, x.y, n.z),
                new Vector3(x.x, x.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z),
                new Vector3(n.x, x.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, x.y, x.z), new Vector3(n.x, x.y, n.z),
            };
            Line(root, "Edges", path, EdgeWidth, colour);
        }

        private static void Brackets(Transform root, Vector3 center, Vector3 size, float width, Color colour)
        {
            Vector3 e = size * 0.5f;
            Vector3 arm = new Vector3(Arm(size.x), Arm(size.y), Arm(size.z));
            for (int i = 0; i < 8; ++i)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                Vector3 corner = center + Vector3.Scale(e, sign);
                // Toward the box along each axis: x arm, the corner, y arm, back to the corner, z arm.
                Vector3[] l =
                {
                    corner - new Vector3(sign.x * arm.x, 0f, 0f), corner, corner - new Vector3(0f, sign.y * arm.y, 0f), corner,
                    corner - new Vector3(0f, 0f, sign.z * arm.z),
                };
                Line(root, "Bracket" + i, l, width, colour);
            }
        }

        private static void Fill(Transform root, Vector3 center, Vector3 size, Mesh mesh)
        {
            var fill = new GameObject("Fill");
            fill.transform.SetParent(root, false);
            fill.transform.localPosition = center;
            fill.transform.localScale = size;
            fill.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = fill.AddComponent<MeshRenderer>();
            Material material = SceneAnchorHighlight.LineMaterial;
            if (material != null) r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static float Arm(float edge) => Mathf.Clamp(edge * BracketFraction, Mathf.Min(BracketMin, edge * 0.5f), BracketMax);

        private static void Line(Transform parent, string name, Vector3[] points, float width, Color colour)
        {
            Material material = SceneAnchorHighlight.LineMaterial;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.widthMultiplier = width;
            line.numCornerVertices = 0;
            line.numCapVertices = 0;
            line.startColor = line.endColor = colour;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (material != null) line.sharedMaterial = material;
            line.positionCount = points.Length;
            line.SetPositions(points);
        }
    }
}
