using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-100 (ADR-0060, ADR-0043): the game members of the aircraft evasion (module air_evasion: AirEvasionGame.cs, AirEvasionFeature.cs).
    /// air_evasion = the button and the automatics (threatening missile, speed steps, altitude presets, attack-run flags); air_evasion_native = the forced
    /// native state AdvancedMissileEvasion (state machine, setState, the private state field). What other modules already declare (Aircraft.Ap,
    /// setTelegraph, desired altitude, velocity, RemoveWaypoints) is used from CapSpecs / AirOpsSpecs.
    /// </summary>
    internal static class AirEvasionSpecs
    {
        private const string Cap = "air_evasion";
        private const string Nat = "air_evasion_native";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Ac = "SeaPower.Aircraft";
        private const string Ap = "SeaPower.AircraftParameters";
        private const string Wb = "SeaPower.WeaponBase";

        // ---- required: the unit and its threat ----
        public static readonly MemberSpec ObjThreateningMissile = MemberSpec.Prop(Cap, Ob, "ThreateningMissile");
        public static readonly MemberSpec WeaponCurrentTarget = MemberSpec.Prop(Cap, Wb, "CurrentTarget");
        public static readonly MemberSpec AcSetPresetHeight = MemberSpec.Method(Cap, Ac, "setPresetHeight", new[] { "int", "bool" }, "int");
        public static readonly MemberSpec ApSpeedValuesInMach = MemberSpec.Field(Cap, Ap, "_speedValuesInMach");
        public static readonly MemberSpec ApPresetAltitudes = MemberSpec.Field(Cap, Ap, "_presetAltitudes");
        public static readonly MemberSpec ApIsLarge = MemberSpec.Field(Cap, Ap, "_isLarge");
        public static readonly MemberSpec AcInLevelAttack = MemberSpec.Field(Cap, Ac, "_inLevelAttackMode");
        public static readonly MemberSpec AcInDiveAttack = MemberSpec.Field(Cap, Ac, "_inDiveAttackMode");

        // ---- the native state ----
        public static readonly MemberSpec ObjStateMachine = MemberSpec.Prop(Nat, Ob, "StateMachine");
        public static readonly MemberSpec SmSetState = MemberSpec.Method(Nat, "SeaPower.StateMachine", "setState", new[] { "IState" }, "void");
        public static readonly MemberSpec SmCurrentStateName = MemberSpec.Prop(Nat, "SeaPower.StateMachine", "CurrentStateName");
        public static readonly MemberSpec AcStateAdvEvasion = MemberSpec.Field(Nat, Ac, "_state_advancedMissileEvasion");

        // ---- optional: without them the feature degrades (no chaff, no end of the native state by us, the mod alone mutes a missile) ----
        public static readonly MemberSpec StateIsFinished = MemberSpec.Prop(Nat, "AircraftStates.StateTemplate", "IsFinished", false);
        public static readonly MemberSpec ObjLaunchChaff = MemberSpec.Method(Cap, Ob, "LaunchChaff", new[] { "bool" }, "void", false);
        public static readonly MemberSpec ObpChaffWeapons = MemberSpec.Field(Cap, "SeaPower.ObjectBaseParameters", "_chaffWeapons", false);
        public static readonly MemberSpec ChaffReadyToLaunch = MemberSpec.Method(Cap, "SeaPower.WeaponSystemChaff", "readyToLaunch", new string[0], "bool", false);
        public static readonly MemberSpec WeaponConnectionLost = MemberSpec.Prop(Cap, Wb, "ConnectionLostForever", false);   // ReactiveProperty<bool>
        public static readonly MemberSpec WeaponTargetLost = MemberSpec.Field(Cap, Wb, "_targetLost", false);
        public static readonly MemberSpec AcIgnoredMissiles = MemberSpec.Field(Cap, Ac, "_ignoredMissiles", false);
        public static readonly MemberSpec ObjPlayerCommandOverride = MemberSpec.Field(Cap, Ob, "_playerCommandOverride", false);
        public static readonly MemberSpec OptionsType = MemberSpec.TypeOf(Cap, "SeaPower.OptionsManager", false);
        public static readonly MemberSpec OptionsAircraftMissileEvasion = MemberSpec.Prop(Cap, "SeaPower.OptionsManager", "AircraftMissileEvasion", false);

        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (!api.AirEvadeEnabled)
            {
                caps.Remove(Cap); caps.Remove(Nat);
                RegistryReport.OffByConfig.Add(Cap); RegistryReport.OffByConfig.Add(Nat);
                return;
            }
            if (!caps.Contains(Cap)) caps.Remove(Nat);
        }

        public static readonly SpecSet Set = SpecSet.Of("air_evasion", typeof(AirEvasionSpecs));
    }
}
