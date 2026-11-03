namespace VoiceBotBridge
{
    /// <summary>
    /// T-241 (ADR-0099): the game members of the aircraft radar ledger (AirRadarGame.cs): the search radar lists of a unit, the operable and on state of a
    /// radar, the derived EMCON. Switching goes through RadarCmd (capability set_radar of Common/Commands2.cs). Nothing here is required: without these
    /// members the ledger answers "unknown" and the aircraft keep the radar the game gave them.
    /// </summary>
    internal static class AirRadarSpecs
    {
        private const string Cap = "air_radar";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Obp = "SeaPower.ObjectBaseParameters";
        private const string Sys = "SeaPower.SensorSystem";
        private const string SysRadar = "SeaPower.SensorSystemRadar";

        public static readonly SpecSet Set = SpecSet.Of("air_radar", typeof(AirRadarSpecs));   // T-241: checked by CapFeature.SelfCheck
        public static readonly MemberSpec ObjIsAirSearchOn = MemberSpec.Prop(Cap, Ob, "IsAirSearchRadarsOn", false);
        public static readonly MemberSpec ObjIsSurfaceSearchOn = MemberSpec.Prop(Cap, Ob, "IsSurfaceSearchRadarsOn", false);
        public static readonly MemberSpec ObjEmcon = MemberSpec.Prop(Cap, Ob, "Emcon", false);
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp", false);
        public static readonly MemberSpec ObpAirList = MemberSpec.Field(Cap, Obp, "_airSearchRadars", false);
        public static readonly MemberSpec ObpSurfaceList = MemberSpec.Field(Cap, Obp, "_surfaceSearchRadars", false);
        public static readonly MemberSpec RadarInoperable = MemberSpec.Prop(Cap, SysRadar, "Inoperable", false);
        public static readonly MemberSpec RadarIsOn = MemberSpec.Prop(Cap, SysRadar, "IsOn", false);
        public static readonly MemberSpec SysAllowTurnOn = MemberSpec.Field(Cap, Sys, "_allowTurnOn", false);
        public static readonly MemberSpec RadarRadar = MemberSpec.Field(Cap, SysRadar, "_radar", false);
        public static readonly MemberSpec RadarRp = MemberSpec.Field(Cap, "SeaPower.Radar", "_rp", false);
        public static readonly MemberSpec RpRole = MemberSpec.Field(Cap, "SeaPower.RadarParameters", "_role", false);
    }
}
