using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M11 (ADR-0043): the game members of the SAR module (SarGame.cs). The members that the aviation family shares (_obp, telegraph, waypoints,
    /// patrol zone) are in AirOpsSpecs (capability air_ops). ext_sar_present: the workshop mod AutomaticSAR is loaded (derived; its own type is not a game member).
    /// </summary>
    internal static class SarSpecs
    {
        private const string Cap = "sar";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Hs = "SeaPower.WaypointData+WaypointHeightState";

        public static readonly MemberSpec Rafts = MemberSpec.Field(Cap, "SeaPower.ObjectsManager", "_listOfAllRafts");
        public static readonly MemberSpec RaftPickUp = MemberSpec.Prop(Cap, Ob, "RaftPickUpAllowed");   // ReactiveProperty<bool>
        public static readonly MemberSpec SetPlayerHeightControl = MemberSpec.Method(Cap, Ob, "setPlayerHeightControl", new[] { "bool" }, "void", false);
        public static readonly MemberSpec SubmarineType = MemberSpec.TypeOf(Cap, "SeaPower.Submarine");
        public static readonly MemberSpec SubSetPresetDepth = MemberSpec.Method(Cap, "SeaPower.Submarine", "setPresetDepth", new[] { "int", "bool" }, "int");
        public static readonly MemberSpec SubIsSubmerged = MemberSpec.Prop(Cap, "SeaPower.Submarine", "IsSubmerged");   // ReactiveProperty<bool>
        public static readonly MemberSpec SubPresetDepth = MemberSpec.Field(Cap, "SeaPower.Submarine", "_currentPresetDepth");
        public static readonly MemberSpec TerrainHeight = MemberSpec.Method(Cap, "SeaPower.TerrainUtils", "GetRealHeightInMetersAt", new[] { "GeoPosition", "bool" }, "float", true, true);
        public static readonly MemberSpec GoToReachable = MemberSpec.Field(Cap, "SeaPower.GoToWaypointTask", "_isReachable");
        public static readonly MemberSpec HeightStateType = MemberSpec.TypeOf(Cap, Hs);
        public static readonly MemberSpec HeightStateVeryLow = MemberSpec.Field(Cap, Hs, "VeryLow", true, true);
        public static readonly MemberSpec HeightStateNumber = MemberSpec.Field(Cap, Hs, "Number", true, true);

        /// <summary>The workshop mod AutomaticSAR is loaded (informational: the units it works with are left alone).</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (api != null && api.ExtSarPresent()) caps.Add("ext_sar_present");
        }

        public static readonly SpecSet Set = SpecSet.Of("sar", typeof(SarSpecs));
    }
}
