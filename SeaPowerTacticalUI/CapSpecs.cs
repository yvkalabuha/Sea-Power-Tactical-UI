namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M14 (ADR-0043): the game members of the CAP module (CapGame.cs): aircraft state and orders, fuel and speed steps, the game's own CAP
    /// zone, the ammunition data. Every member CapGame reads is declared here (capability cap, as before); what SAR/ASW/CAP share (unit parameters,
    /// waypoints, the flight deck) is in AirOpsSpecs.
    /// </summary>
    internal static class CapSpecs
    {
        private const string Cap = "cap";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Obp = "SeaPower.ObjectBaseParameters";
        private const string Ap = "SeaPower.AircraftParameters";
        private const string Amm = "SeaPower.AmmunitionParameters";

        // ---- the unit ----
        public static readonly MemberSpec ObjRangeKm = MemberSpec.Prop(Cap, Ob, "RangeInKm");
        public static readonly MemberSpec ObjRangeOnMap = MemberSpec.Prop(Cap, Ob, "RangeOnMap");   // ReactiveProperty<float>
        public static readonly MemberSpec ObjHomeBase = MemberSpec.Field(Cap, Ob, "_homeBase");
        public static readonly MemberSpec ObjClassName = MemberSpec.Prop(Cap, Ob, "ClassName");
        public static readonly MemberSpec ObjDesiredAltitude = MemberSpec.Prop(Cap, Ob, "DesiredAltitude");
        public static readonly MemberSpec ObjSetOrder = MemberSpec.Method(Cap, Ob, "setOrder", new[] { "Type", "ObjectBase", "bool" }, "void");
        public static readonly MemberSpec ObjWeaponStatus = MemberSpec.Field(Cap, Ob, "_weaponStatus");
        public static readonly MemberSpec ObjSetWeaponStatus = MemberSpec.Method(Cap, Ob, "SetWeaponStatus", new[] { "WeaponStatus", "bool" }, "void");
        public static readonly MemberSpec ObjCeaseFire = MemberSpec.Method(Cap, Ob, "CeaseFire", new[] { "bool", "bool", "bool", "bool", "bool", "bool" }, "void");
        public static readonly MemberSpec ObjAi = MemberSpec.Field(Cap, Ob, "_ai");
        public static readonly MemberSpec AiTarget = MemberSpec.Field(Cap, "SeaPower.AI", "_objectToDestroy");
        public static readonly MemberSpec ObjEngageTasks = MemberSpec.Field(Cap, Ob, "_currentEngageTasks");
        public static readonly MemberSpec ObjAlignmentTasks = MemberSpec.Field(Cap, Ob, "_alignmentTasks");   // T-317: List<AlignUnitToTarget>, non-empty while the AI aligns for an attack
        public static readonly MemberSpec ObjCurrentOrder = MemberSpec.Prop(Cap, Ob, "CurrentOrder");   // ReactiveProperty<Order>
        public static readonly MemberSpec OrderType = MemberSpec.Prop(Cap, "SeaPower.Order", "OrderType");
        public static readonly MemberSpec ObjStateMachine = MemberSpec.Prop(Cap, Ob, "StateMachine");
        public static readonly MemberSpec MachineStateName = MemberSpec.Prop(Cap, "SeaPower.StateMachine", "CurrentStateName");
        public static readonly MemberSpec ObjIsControllable = MemberSpec.Prop(Cap, Ob, "IsControllable");
        public static readonly MemberSpec ObjAcceptsOrders = MemberSpec.Prop(Cap, Ob, "AcceptsOrdersFromPlayer");
        public static readonly MemberSpec ObjGetTelegraph = MemberSpec.Method(Cap, Ob, "getTelegraph", new string[0], "int");
        public static readonly MemberSpec ObpLoadoutVariant = MemberSpec.Field(Cap, Obp, "_loadoutVariant");
        public static readonly MemberSpec ObpMaxRangeKm = MemberSpec.Field(Cap, Obp, "_maxRangeInKm");
        public static readonly MemberSpec ObpCurrentFuel = MemberSpec.Field(Cap, Obp, "_currentFuelMass");   // declared in ObjectBaseParameters; AircraftParameters inherits it
        public static readonly MemberSpec ObpMaxFuel = MemberSpec.Field(Cap, Obp, "_maxFuel");

        // ---- the aircraft: speed steps (Aircraft.setTelegraph(n): n = 1..Ap._speedValuesInMach.Length; getTelegraph() = n - 1; the ObjectBase methods are virtual) ----
        public static readonly MemberSpec AircraftAp = MemberSpec.Prop(Cap, "SeaPower.Aircraft", "Ap");
        public static readonly MemberSpec AircraftInFlight = MemberSpec.Field(Cap, "SeaPower.Aircraft", "_isInFlight");
        public static readonly MemberSpec AircraftActualRangeKm = MemberSpec.Prop(Cap, "SeaPower.Aircraft", "ActualRangeInKm", false);   // ReactiveProperty<float>: path left before the return reserve (0.27.8)
        public static readonly MemberSpec AircraftEndurance = MemberSpec.Prop(Cap, "SeaPower.Aircraft", "EnduranceInSec");
        public static readonly MemberSpec AircraftSetTelegraph = MemberSpec.Method(Cap, "SeaPower.Aircraft", "setTelegraph", new[] { "int" }, "void");
        public static readonly MemberSpec AircraftGetTelegraph = MemberSpec.Method(Cap, "SeaPower.Aircraft", "getTelegraph", new string[0], "int");
        public static readonly MemberSpec ApSpeedMach = MemberSpec.Field(Cap, Ap, "_speedValuesInMach");
        public static readonly MemberSpec ApSpeedNames = MemberSpec.Field(Cap, Ap, "_speedValueNames");
        public static readonly MemberSpec ApRangeCurve = MemberSpec.Field(Cap, Ap, "_rangeMultiplierCurve");
        public static readonly MemberSpec CurveEvaluate = MemberSpec.DuckMember(Cap, "Evaluate", MemberKind.Method, new[] { "float" }, "UnityEngine.AnimationCurve (a Unity type that the offline selfcheck does not load)");

        // ---- the game's own CAP zone ----
        public static readonly MemberSpec ZoneAttached = MemberSpec.Field(Cap, "SeaPower.PatrolZone", "_attachedUnits");

        // ---- ammunition data (GetShared reads the game ini with the Workshop overlay applied) ----
        public static readonly MemberSpec AmmoGetShared = MemberSpec.Method(Cap, Amm, "GetShared", new[] { "string" }, "AmmunitionParameters", true, true);
        public static readonly MemberSpec AmmoType = MemberSpec.Field(Cap, Amm, "_type");
        public static readonly MemberSpec AmmoTargetType = MemberSpec.Field(Cap, Amm, "_targetType");
        public static readonly MemberSpec AmmoSecondaryTarget = MemberSpec.Field(Cap, Amm, "_secondaryTargetType");
        public static readonly MemberSpec AmmoMaxLaunchRange = MemberSpec.Field(Cap, Amm, "_maxLaunchRangeInMiles");
        public static readonly MemberSpec AmmoFuelKg = MemberSpec.Field(Cap, Amm, "_fuelMassInKg");
        public static readonly MemberSpec AmmoDisplayName = MemberSpec.Prop(Cap, Amm, "_displayedName", false);   // T-325: optional, the name shown in the loadout text

        // ---- the deck rows the CAP window lists ----
        public static readonly MemberSpec VtFileName = MemberSpec.Field(Cap, "SeaPower.VehicleTypeOnBoard", "_fileName");
        public static readonly MemberSpec VtType = MemberSpec.Field(Cap, "SeaPower.VehicleTypeOnBoard", "_type");
        public static readonly MemberSpec VtNumbers = MemberSpec.Prop(Cap, "SeaPower.VehicleTypeOnBoard", "Numbers");
        public static readonly MemberSpec VtDisplayName = MemberSpec.Prop(Cap, "SeaPower.VehicleTypeOnBoard", "DisplayName", false);

        public static readonly SpecSet Set = SpecSet.Of("cap", typeof(CapSpecs));
    }
}
