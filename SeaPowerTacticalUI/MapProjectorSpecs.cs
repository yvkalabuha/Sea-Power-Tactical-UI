using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-309 (ADR-0119): the game members that MapSnapReader reads to build the exact projector snapshot of the beta map (all optional: on 261021
    /// the map is equirectangular, the overlays stay on the affine fit and the projector reports "affine"). Registered as the set "map_projector" by the integration step.
    /// </summary>
    internal static class MapProjectorSpecs
    {
        private const string Cap = "map_projector";
        private const string Map = "MapControl.MapBase";
        private const string Proj = "MapControl.MapProjection";
        private const string Loc = "MapControl.Location";
        private const string View = "MapControl.ViewTransform";

        public static readonly SpecSet Set = SpecSet.Of(Cap, typeof(MapProjectorSpecs));

        public static readonly MemberSpec MapProjection = MemberSpec.Prop(Cap, Map, "MapProjection", false);
        public static readonly MemberSpec ViewTransform = MemberSpec.Prop(Cap, Map, "ViewTransform", false);
        public static readonly MemberSpec Center = MemberSpec.Prop(Cap, Map, "Center", false);
        public static readonly MemberSpec ProjectionCenter = MemberSpec.Prop(Cap, Proj, "Center", false);
        public static readonly MemberSpec CrsId = MemberSpec.Prop(Cap, Proj, "CrsId", false);
        public static readonly MemberSpec LocLat = MemberSpec.Prop(Cap, Loc, "Latitude", false);
        public static readonly MemberSpec LocLon = MemberSpec.Prop(Cap, Loc, "Longitude", false);
        public static readonly MemberSpec Scale = MemberSpec.Prop(Cap, View, "Scale", false);
        public static readonly MemberSpec Rotation = MemberSpec.Prop(Cap, View, "Rotation", false);
        /// <summary>MapBase.TryLocationToView(GeoPosition, out Point): the game's own answer, false = behind the horizon.</summary>
        public static readonly MemberSpec TryLocationToView = MemberSpec.Method(Cap, Map, "TryLocationToView", new[] { "SeaPower.GeoPosition", "Noesis.Point&" }, "bool", false);
        public static readonly MemberSpec OrthoType = MemberSpec.TypeOf(Cap, "MapControl.OrthographicProjection", false);
    }
}
