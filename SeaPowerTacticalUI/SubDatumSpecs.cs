namespace VoiceBotBridge
{
    /// <summary>
    /// T-288 (ADR-0115): the game members of the sub launch area (SubDatumGame.cs, SubDatumFeature.cs). Only the player's own track of a torpedo is read
    /// (Vehicle: position through the shared accessors, velocity, "identified", class); the run of an identified torpedo comes from the game's ammunition
    /// parameters, as the evasion and the smart attack read them. All optional except the track velocity: without the rest the sector gets the default run.
    /// </summary>
    internal static class SubDatumSpecs
    {
        private const string Cap = "sub_datum";
        private const string Veh = "SeaPower.Vehicle";
        private const string Ap = "SeaPower.AmmunitionParameters";

        public static readonly MemberSpec VehVelocity = MemberSpec.Prop(Cap, Veh, "UnityVelocityVector");
        public static readonly MemberSpec VehIdentified = MemberSpec.Prop(Cap, Veh, "Identified", false);
        public static readonly MemberSpec VehObject = MemberSpec.Field(Cap, Veh, "Object", false);
        public static readonly MemberSpec VehClass = MemberSpec.Field(Cap, Veh, "Class", false);   // Nullable<SourcedProperty<string>>
        public static readonly MemberSpec SourcedValue = MemberSpec.DuckMember(Cap, "Value", MemberKind.Property, null, "SourcedProperty<T>.Value: T differs per property");
        public static readonly MemberSpec WpnAp = MemberSpec.Field(Cap, "SeaPower.WeaponBase", "_ap", false);
        public static readonly MemberSpec ApSpeedSettings = MemberSpec.Field(Cap, Ap, "_torpedoSpeedSettings", false);   // List<Vector2>: x = knots, y = range in Unity units
        public static readonly MemberSpec ApMaxLaunchRange = MemberSpec.Field(Cap, Ap, "_maxLaunchRangeInMiles", false);
        public static readonly MemberSpec VecX = MemberSpec.DuckMember(Cap, "x", MemberKind.Field, null, "UnityEngine.Vector2/Vector3 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecY = MemberSpec.DuckMember(Cap, "y", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecZ = MemberSpec.DuckMember(Cap, "z", MemberKind.Field, null, "UnityEngine.Vector3 component (a Unity struct, not loaded by the offline selfcheck)");

        public static readonly SpecSet Set = SpecSet.Of("sub_datum", typeof(SubDatumSpecs));
    }
}
