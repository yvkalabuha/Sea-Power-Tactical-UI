using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-175 / ADR-0075 (ADR-0043): the game members of the air threat service (AirGuardGame.cs). Everything optional: the service degrades by part
    /// (ADR-0075 item 10). The members that other modules declare (ThreateningMissile, velocity, heading, ring ammunition, encyclopedia) are used from their sets.
    /// </summary>
    internal static class AirGuardSpecs
    {
        private const string Cap = "air_guard";

        /// <summary>The unit's air search radar is on (the cover ship with a silent radar is chosen with a warning cover_radar_off).</summary>
        public static readonly MemberSpec ObjAirRadarsOn = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "IsAirSearchRadarsOn", false);

        /// <summary>T-180: SeaPower.Helicopter is not an Aircraft: its own height preset method and parameters (the lowest preset is the minimum of the array).</summary>
        public static readonly MemberSpec HeliSetPresetHeight = MemberSpec.Method(Cap, "SeaPower.Helicopter", "setPresetHeight", new[] { "int", "bool" }, "int", false);
        public static readonly MemberSpec HeliHp = MemberSpec.Field(Cap, "SeaPower.Helicopter", "_hp", false);
        public static readonly MemberSpec HpPresetAltitudes = MemberSpec.Field(Cap, "SeaPower.HelicopterParameters", "_presetAltitudes", false);

        public static readonly SpecSet Set = SpecSet.Of("air_guard", typeof(AirGuardSpecs));
    }
}
