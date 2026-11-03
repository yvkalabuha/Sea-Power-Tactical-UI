namespace VoiceBotBridge
{
    /// <summary>
    /// T-331: game members of the "We're on position" guard. The game raises [OrderLog] ReachedLastWaypoint (text + voice OnStation) from
    /// GoToWaypointTask.OnFinished / GoToRelativeToStationPoint.OnFinished when ObjectBase.isLastWaypoint() holds; the patch answers false for a unit of
    /// the mod inside that call (isLastWaypoint is a hot method of the helicopter: the scope keeps it untouched elsewhere). All optional.
    /// </summary>
    internal static class WaypointQuietSpecs
    {
        private const string Cap = "wp_reached_quiet";

        public static readonly PatchSpec PatchFinishWp = new PatchSpec("wpq_finish_wp", Cap, "SeaPower.GoToWaypointTask", "OnFinished", new string[0], "void", PatchKind.PrefixPostfixFinalizer, "always", false);
        public static readonly PatchSpec PatchFinishStation = new PatchSpec("wpq_finish_station", Cap, "SeaPower.GoToRelativeToStationPoint", "OnFinished", new string[0], "void", PatchKind.PrefixPostfixFinalizer, "always", false);
        public static readonly PatchSpec PatchIsLast = new PatchSpec("wpq_is_last", Cap, "SeaPower.ObjectBase", "isLastWaypoint", new string[0], "bool", PatchKind.PrefixPostfix, "always", false);
        public static readonly MemberSpec TaskBase = MemberSpec.Field(Cap, "SeaPower.VisualActionTask", "_baseObject", false);

        public static readonly SpecSet Set = SpecSet.Of("wp_quiet", typeof(WaypointQuietSpecs));
    }
}
