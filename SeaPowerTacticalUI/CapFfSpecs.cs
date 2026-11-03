namespace VoiceBotBridge
{
    /// <summary>
    /// T-333 (ADR-0129 section 9): the game members of the friendly-fire guard (CapFfGame.cs): the seeker type of an ammunition and the fields of an EngageTask that the
    /// optional missile swap writes. All optional (capability cap_ff): without them the guard still holds / releases, only the seeker is unknown (fire allowed) and no swap.
    /// The members of fire_veto (_baseObject, _targetObject, _isAutoAttack, _ammoId) are checked by SelfCheckVeto.
    /// </summary>
    internal static class CapFfSpecs
    {
        private const string Cap = "cap_ff";
        private const string Amm = "SeaPower.AmmunitionParameters";
        private const string Et = "SeaPower.EngageTask";
        private const string Ob = "SeaPower.ObjectBase";

        public static readonly MemberSpec AmmoGuidance = MemberSpec.Field(Cap, Amm, "_guidanceType", false);          // enum GuidanceType: None, IRHoming, SemiActiveRadarHoming, ActiveRadarHoming, ...
        public static readonly MemberSpec AmmoMidCourse = MemberSpec.Field(Cap, Amm, "_midCourseCorrection", false);   // MidCourseCorrection object: ToString() None / RadioCommand / WireGuided / Datalink
        public static readonly MemberSpec TaskAmmoId = MemberSpec.Field(Cap, Et, "_ammoId", false);
        public static readonly MemberSpec TaskBase = MemberSpec.Field(Cap, Et, "_baseObject", false);
        public static readonly MemberSpec TaskTarget = MemberSpec.Field(Cap, Et, "_targetObject", false);
        public static readonly MemberSpec TaskAuto = MemberSpec.Field(Cap, Et, "_isAutoAttack", false);
        public static readonly MemberSpec TaskAmmunition = MemberSpec.Field(Cap, Et, "_ammunition", false);
        public static readonly MemberSpec TaskShots = MemberSpec.Field(Cap, Et, "_shotsToFire", false);
        public static readonly MemberSpec ObjGetAmmoByName = MemberSpec.Method(Cap, Ob, "getAmmunitionByName", new[] { "string" }, "Ammunition", false);
        public static readonly MemberSpec ObjAlignTasks = MemberSpec.Field(Cap, Ob, "_alignmentTasks", false);   // List<AlignUnitToTarget>
        public static readonly MemberSpec AlignGuiding = MemberSpec.Field(Cap, "SeaPower.AlignUnitToTarget", "_isGuidingWeapon", false);

        public static readonly SpecSet Set = SpecSet.Of("cap_ff", typeof(CapFfSpecs));
    }
}
