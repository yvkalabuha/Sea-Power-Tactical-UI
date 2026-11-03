namespace VoiceBotBridge
{
    /// <summary>
    /// T-242 (ADR-0101): the game members that read the formation of an own unit (UnitFormation, Station), shared by the Overview "Own" tab (column "Group")
    /// and the radial "return to formation" sector (T-243). Capability formation_read. Every member is optional: a missing one leaves the part of
    /// FormationFacts it fills at its default (FormationGame.cs). OwnOnly: only the player's own units are read.
    /// </summary>
    internal static class FormationSpecs
    {
        private const string Cap = "formation_read", Ob = "SeaPower.ObjectBase", Uf = "SeaPower.UnitFormation", St = "SeaPower.Station";

        public static readonly MemberSpec ObjFormation = MemberSpec.Prop(Cap, Ob, "Formation", false, false, true);
        public static readonly MemberSpec ObjIsLeader = MemberSpec.Prop(Cap, Ob, "IsFormationLeader", false, false, true);
        public static readonly MemberSpec FormationName = MemberSpec.Prop(Cap, Uf, "Name", false);
        public static readonly MemberSpec FormationLeaderStation = MemberSpec.Prop(Cap, Uf, "LeaderStation", false);
        public static readonly MemberSpec FormationStations = MemberSpec.Prop(Cap, Uf, "Stations", false);
        public static readonly MemberSpec StationUnit = MemberSpec.Prop(Cap, St, "UnitObject", false);
        public static readonly MemberSpec StationIndependent = MemberSpec.Prop(Cap, St, "UnitActsIndependently", false);   // ReactiveProperty<bool>
        public static readonly MemberSpec StationGeo = MemberSpec.Prop(Cap, St, "GeoPosition", false);
        public static readonly MemberSpec FormationStationFor = MemberSpec.Method(Cap, Uf, "GetStationForUnit", new[] { "SeaPower.ObjectBase" }, "SeaPower.Station", false);

        public static readonly MemberSpec ObjPosition = MemberSpec.Prop(Cap, Ob, "Position", false, false, true);   // ReactiveProperty<GeoPosition> (T-243: the unit and the leader point)

        // T-243 formation_return: the game's own Orders menu item "Return to formation" (ObjectBaseViewModel: CheckForPlayerAbort + UnitFormation.ReturnToFormation).
        // Required members: without them the sector is gone (Derive also needs the formation_read members the "on station" rule reads).
        private const string Ret = "formation_return";
        public static readonly MemberSpec FormationReturn = MemberSpec.Method(Ret, Uf, "ReturnToFormation", new[] { "SeaPower.ObjectBase", "bool" }, "void");
        public static readonly MemberSpec ObjCheckForPlayerAbort = MemberSpec.Method(Ret, Ob, "CheckForPlayerAbort", new string[0], "void", false);
        public static readonly MemberSpec ObjAcceptsOrdersFromPlayer = MemberSpec.Prop(Ret, Ob, "AcceptsOrdersFromPlayer", false);

        public static void Derive(GameApi api, System.Collections.Generic.ISet<string> caps)
        {
            if (!caps.Contains(Ret)) return;
            if (!api.Present(ObjFormation) || !api.Present(ObjIsLeader) || !api.Present(FormationStationFor) || !api.Present(StationIndependent)) caps.Remove(Ret);
        }

        public static readonly SpecSet Set = SpecSet.Of("formation", typeof(FormationSpecs));
    }
}
