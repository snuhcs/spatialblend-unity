// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>Vertical two-colour gradient over a UI graphic's mesh (works with 9-sliced images).</summary>
    [AddComponentMenu("")]
    public sealed class UiVerticalGradient : BaseMeshEffect
    {
        public Color Top = Color.white;
        public Color Bottom = Color.white;

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount == 0) return;
            var v = new UIVertex();
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                min = Mathf.Min(min, v.position.y);
                max = Mathf.Max(max, v.position.y);
            }
            float span = Mathf.Max(max - min, 0.0001f);
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                var c = Color.Lerp(Bottom, Top, (v.position.y - min) / span);
                v.color = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(c.a * 255f));
                vh.SetUIVertex(v, i);
            }
        }

        public void Set(Color top, Color bottom)
        {
            Top = top;
            Bottom = bottom;
            if (graphic != null) graphic.SetVerticesDirty();
        }
    }
}
