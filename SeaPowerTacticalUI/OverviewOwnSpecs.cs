using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-242 (ADR-0101): the game members of the Overview "Own" tab (OverviewOwnGame.cs). Capability overview_own: the four lists of the player's taskforce,
    /// the unit id and name (required); everything else is optional (a missing member = the cell shows a dash). overview_own_select: the call the game
    /// itself uses to select an own unit (RenderPosition.switchToObject, F5 and the select command). The formation members are in FormationSpecs.
    /// OwnOnly: only the player's own units are touched.
    /// </summary>
    internal static class OverviewOwnSpecs
    {
        private const string Cap = "overview_own", Ob = "SeaPower.ObjectBase", Tf = "SeaPower.Taskforce", Obp = "SeaPower.ObjectBaseParameters";

        public static readonly MemberSpec PlayerTaskforce = MemberSpec.Field(Cap, "SeaPower.Globals", "_playerTaskforce", true, true);
        public static readonly MemberSpec TfVessels = MemberSpec.Field(Cap, Tf, "_taskforceVessels", false);
        public static readonly MemberSpec TfSubmarines = MemberSpec.Field(Cap, Tf, "_taskforceSubmarines", false);
        public static readonly MemberSpec TfAircraft = MemberSpec.Field(Cap, Tf, "_taskforceAircraft", false);
        public static readonly MemberSpec TfHelicopters = MemberSpec.Field(Cap, Tf, "_taskforceHelicopters", false);
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Cap, Ob, "UniqueID", true);
        public static readonly MemberSpec ObjName = MemberSpec.Prop(Cap, Ob, "Name", true);   // ReactiveProperty<string>

        public static readonly MemberSpec ObjClassNameShort = MemberSpec.Prop(Cap, Ob, "ClassNameShort", false);
        public static readonly MemberSpec ObjAbbreviation = MemberSpec.Prop(Cap, Ob, "TypeAbbreviation", false);
        public static readonly MemberSpec ObjHeightFt = MemberSpec.Method(Cap, Ob, "getHeightInFeet", new string[0], "float", false, false, true);   // negative = depth
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Method(Cap, Ob, "getVelocityInKnots", new string[0], "float", false, false, true);
        public static readonly MemberSpec ObjIsDestroyed = MemberSpec.Prop(Cap, Ob, "IsDestroyed", false);
        public static readonly MemberSpec Feedback = MemberSpec.Prop(Cap, Ob, "OrderFeedbackText", false, false, true);     // ReactiveProperty<string>
        public static readonly MemberSpec OrderText = MemberSpec.Prop(Cap, Ob, "CurrentOrderText", false, false, true);     // ReactiveProperty<string>
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp", false);
        public static readonly MemberSpec ObpLoadoutVariant = MemberSpec.Field(Cap, Obp, "_loadoutVariant", false);
        public static readonly MemberSpec AircraftInFlight = MemberSpec.Field(Cap, "SeaPower.Aircraft", "_isInFlight", false);
        public static readonly MemberSpec HelicopterInFlight = MemberSpec.Field(Cap, "SeaPower.Helicopter", "_isInFlight", false);   // Helicopter does not derive from Aircraft

        // select an own unit the way the game does (F5, select command): switchToObject(unit, smoothMove, keepCamRotation, checkAirLeader)
        public static readonly MemberSpec SwitchTo = MemberSpec.Method("overview_own_select", "SeaPower.RenderPosition", "switchToObject", new[] { "SeaPower.ObjectBase", "bool", "bool", "bool" }, "void");

        public static readonly MemberSpec[] Lists = { TfVessels, TfSubmarines, TfAircraft, TfHelicopters };

        /// <summary>overview_own needs the overview table, the lists of the taskforce (at least one); overview_own_select needs overview_own.</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            bool lists = false;
            foreach (var l in Lists) if (api.Present(l)) { lists = true; break; }
            if (!caps.Contains("overview") && !api.Capabilities.Contains("overview")) lists = false;
            if (!lists) caps.Remove("overview_own");
            if (!caps.Contains("overview_own")) caps.Remove("overview_own_select");
        }

        public static readonly SpecSet Set = SpecSet.Of("overview_own", typeof(OverviewOwnSpecs));
    }
}
