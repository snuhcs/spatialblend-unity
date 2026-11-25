// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Plain-words text for a refused region outline. The authoring layer reports the technical reason
    /// (<c>SbSpatialAuthoring.TryValidateDeclaredExtent</c>, <c>SpatialAuthoringCompiler.TryResolveRegionPolygon</c>,
    /// <c>SbSpatialAuthoring.TryResolveFootprint</c>); the user gets one short sentence with an action. The technical text goes
    /// to the log. Every reason those functions produce is mapped; anything else gets the generic sentence.
    /// </summary>
    public static class RegionRefusalText
    {
        public const string Title = "Couldn't create the area";
        public const string Fallback = "This outline didn't work. Draw it again.";
        private const string Prefix = "Region not created: ";

        public static string ToUserMessage(string technical)
        {
            if (string.IsNullOrWhiteSpace(technical)) return Fallback;
            string reason = technical.StartsWith(Prefix) ? technical.Substring(Prefix.Length) : technical;
            if (reason.Contains("must contain the object footprint")) return "The outline must surround the whole object. Draw it again.";
            if (reason.Contains("must be a simple polygon")) return "This outline crosses itself. Draw it again.";
            if (reason.Contains("at least three points")) return "An outline needs at least three corners. Draw it again.";
            if (reason.Contains("fallback margin")) return "This object's area couldn't be set up. Try another object.";
            if (reason.Contains("requires a footprint polygon or a collider") || reason.Contains("collider is required") ||
                reason.Contains("has no local box")) return "This object can't be outlined. Try another object.";
            if (reason.Contains("footprint is degenerate")) return "This object is too thin to outline. Try another object.";
            return Fallback;
        }
    }
}
