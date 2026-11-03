namespace VoiceBotBridge
{
    /// <summary>
    /// T-138 (ADR-0043, ADR-0054): the game members of the intercept course (InterceptGame.cs). The speed steps of the own unit
    /// (Vessel.VP for ships, Submarine.SP for submarines: the field is declared on each parameter class), and the way from the object of a
    /// contact-menu click to the player's own track (ObjectBase.GetMapVehicle(taskforce), as the smart attack does). The track itself is read by
    /// CiReadTrack (ContactInfoGame) and the orders go through PlaceWaypoint / TelegraphCmd (Common); nothing else of a foreign object is read.
    /// </summary>
    internal static class InterceptSpecs
    {
        private const string Cap = "intercept";

        public static readonly MemberSpec VesselParams = MemberSpec.Prop(Cap, "SeaPower.Vessel", "VP");
        public static readonly MemberSpec VesselSteps = MemberSpec.Field(Cap, "SeaPower.VesselParameters", "_telegraphVelocities");
        public static readonly MemberSpec SubParams = MemberSpec.Prop(Cap, "SeaPower.Submarine", "SP");
        public static readonly MemberSpec SubSteps = MemberSpec.Field(Cap, "SeaPower.SubmarineParameters", "_telegraphVelocities");
        public static readonly MemberSpec ObjIsUnit = MemberSpec.Method(Cap, "SeaPower.ObjectBase", "isUnit", new string[0], "bool");
        public static readonly MemberSpec ObjMapVehicle = MemberSpec.Method(Cap, "SeaPower.ObjectBase", "GetMapVehicle", new[] { "SeaPower.Taskforce" }, "SeaPower.Vehicle");
        public static readonly MemberSpec ObjTaskforce = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "_taskforce", true, false, true);   // OwnOnly: our own unit only
        public static readonly MemberSpec VehBearingFan = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "BearingFan", false);
        // T-169 / ADR-0067: the name of a class / weapon entry of the encyclopedia for the enemy zone warning (optional: without it the class ini name
        // and the weapon kind are shown). The rest of the class reference is read through RangeRingSpecs (range_rings_class).
        public static readonly MemberSpec EntryDisplayName = MemberSpec.Prop(Cap, "SeaPower.MuseumEntry", "DisplayName", false);

        public static readonly SpecSet Set = SpecSet.Of("intercept", typeof(InterceptSpecs));
    }
}
