namespace VoiceBotBridge
{
    /// <summary>
    /// T-361 (ADR-0136): the game members of the dev `force` command (QA scenarios by real game orders). Capability "qa_force", ALL optional: a missing member only makes
    /// the matching operation answer "unsupported" / "no_enemy_*"; the selfcheck prints them and never fails on them. Reached only through the link command qa_force,
    /// which is refused when the dev mod (diag slot) is not installed. The enemy side is the truth of the game (a cheat), never used by any feature of the mod.
    /// </summary>
    internal static class QaForceSpecs
    {
        private const string Cap = "qa_force";
        private const string Tf = "SeaPower.Taskforce";

        public static readonly MemberSpec EnemyTaskforce = MemberSpec.Field(Cap, "SeaPower.Globals", "_enemyTaskforce", false, true);   // leak: the true enemy side (dev cheat, qa_force only)
        public static readonly MemberSpec EnemySubmarines = MemberSpec.Field(Cap, Tf, "_taskforceSubmarines", false);
        public static readonly MemberSpec EnemyAircraft = MemberSpec.Field(Cap, Tf, "_taskforceAircraft", false);
        public static readonly MemberSpec EnemyHelicopters = MemberSpec.Field(Cap, Tf, "_taskforceHelicopters", false);
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "UniqueID", false);
        public static readonly MemberSpec WeaponStatusType = MemberSpec.TypeOf(Cap, "SeaPower.ObjectBase+WeaponStatus", false);

        public static readonly SpecSet Set = SpecSet.Of("qa_force", typeof(QaForceSpecs));
    }
}
