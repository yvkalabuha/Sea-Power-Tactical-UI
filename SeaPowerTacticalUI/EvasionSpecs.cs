using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the own torpedo evasion (module evasion: Evasion.cs, EvasionAtw.cs). evasion_own = heading, the torpedo
    /// that threatens the unit, the noisemaker, the game's auto_evade flag, and [AutoEvade] Mode=own; evasion_atw = the anti-torpedo salvo on top of it
    /// (needs attack). A Mode=native config is a choice of the player, not a degradation (RegistryReport.OffByConfig).
    /// </summary>
    internal static class EvasionSpecs
    {
        private const string Own = "evasion_own";
        private const string Atw = "evasion_atw";

        public static readonly MemberSpec ObjHeading = MemberSpec.Prop(Own, "SeaPower.ObjectBase", "Heading");
        public static readonly MemberSpec ObjThreatingTorpedo = MemberSpec.Prop(Own, "SeaPower.ObjectBase", "ThreatingTorpedo");
        public static readonly MemberSpec ObjLaunchNoisemaker = MemberSpec.Method(Own, "SeaPower.ObjectBase", "LaunchNoisemaker", new[] { "string" }, "void");
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Method(Own, "SeaPower.ObjectBase", "getVelocityInKnots", new string[0], "float", false);   // T-281: detour timeout

        // T-193 (ADR-0083): optional; without them WakeHoming stays false and frozen tracks are found by position only
        public static readonly MemberSpec VehIdentified = MemberSpec.Prop(Own, "SeaPower.Vehicle", "Identified", false);
        public static readonly MemberSpec VehHasLiveTrack = MemberSpec.Prop(Own, "SeaPower.Vehicle", "HasLiveTrack", false);
        public static readonly MemberSpec WpnAp = MemberSpec.Field(Own, "SeaPower.WeaponBase", "_ap", false);
        public static readonly MemberSpec ApGuidanceType = MemberSpec.Field(Own, "SeaPower.AmmunitionParameters", "_guidanceType", false);

        // T-224 (ADR-0091): optional, for the slow run for the sonar; own copies (the capability groups of intercept / contact_info_noise are independent; the registry allows the same game member in several groups)
        public static readonly MemberSpec VesselParams = MemberSpec.Prop(Own, "SeaPower.Vessel", "VP", false);
        public static readonly MemberSpec VesselSteps = MemberSpec.Field(Own, "SeaPower.VesselParameters", "_telegraphVelocities", false);
        public static readonly MemberSpec ObjCavitationOnset = MemberSpec.Method(Own, "SeaPower.ObjectBase", "CavitationOnsetKts", new string[0], "float", false);
        // T-266 (ADR-0106): optional; without them only IsDestroyed and the no-way rule tell an immobile ship
        public static readonly MemberSpec ObjCompartments = MemberSpec.Prop(Own, "SeaPower.ObjectBase", "Compartments", false);
        public static readonly MemberSpec CompSinking = MemberSpec.Field(Own, "SeaPower.Compartments", "_isSinking", false);
        public static readonly MemberSpec CompImmobile = MemberSpec.Field(Own, "SeaPower.Compartments", "_immobile", false);

        // T-279 (ADR-0112): optional; without them the evasion helm is off (waypoint only, as in 0.30.20)
        public static readonly MemberSpec ObjCourseCommand = MemberSpec.Prop(Own, "SeaPower.ObjectBase", "CourseCommand", false);
        public static readonly MemberSpec ObjPerformingAirOps = MemberSpec.Field(Own, "SeaPower.ObjectBase", "_performingAirOps", false);
        public static readonly MemberSpec SetCourseCtor = MemberSpec.Ctor(Own, "SeaPower.SetCourse", new[] { "float" }, false);
        public static readonly MemberSpec CcGetDemandedCourse = MemberSpec.Method(Own, "SeaPower.ICourseCommand", "GetDemandedCourse", new[] { "SeaPower.ObjectBase" }, "float", false);

        public static readonly MemberSpec AmmoAntiTorpedo = MemberSpec.Field(Atw, "SeaPower.AmmunitionParameters", "_antiTorpedo");
        public static readonly MemberSpec AmmoKillProbability = MemberSpec.Field(Atw, "SeaPower.AmmunitionParameters", "_killProbability");
        public static readonly MemberSpec AmmoMinLaunchRange = MemberSpec.Field(Atw, "SeaPower.AmmunitionParameters", "_minLaunchRangeInMiles");
        public static readonly MemberSpec TaskTargetObject = MemberSpec.Field(Atw, "SeaPower.EngageTask", "_targetObject");
        public static readonly MemberSpec ObjEngageTasks = MemberSpec.Field(Atw, "SeaPower.ObjectBase", "_currentEngageTasks");
        public static readonly MemberSpec AmmoMuzzleVelocity = MemberSpec.Field(Atw, "SeaPower.AmmunitionParameters", "_muzzleVelocityInMeterPerSecond", false);

        // T-145 (ADR-0053): the mounts that can fire at a torpedo now (identity of each WeaponSystem = its free-mount key); absent -> one synthetic mount per ammo
        public static readonly MemberSpec WeaponSystemsForAmmo = MemberSpec.Method(Atw, "SeaPower.WeaponSelector", "GetWeaponSystemsForAmmunition",
            new[] { "SeaPower.ObjectBase", "UnityEngine.Vector3", "string", "SeaPower.ObjectBase" }, null, false, true);
        // T-287 (ADR-0114): optional; the salvo at a position (a frozen torpedo track): EngageTask(ammo, position, shooter, shots, priority, autoAttack, isFormationAttack)
        public static readonly MemberSpec EngagePosCtor = MemberSpec.Ctor(Atw, "SeaPower.EngageTask",
            new[] { "string", "UnityEngine.Vector3", "SeaPower.ObjectBase", "int", "int", "bool", "bool" }, false);
        public static readonly MemberSpec ObjAddEngageTask = MemberSpec.Method(Atw, "SeaPower.ObjectBase", "AddEngageTask", new[] { "SeaPower.EngageTask" }, "void", false);
        public static readonly MemberSpec ObjAmmoByName = MemberSpec.Method(Atw, "SeaPower.ObjectBase", "getAmmunitionByName", new[] { "string" }, "SeaPower.Ammunition", false);
        public static readonly MemberSpec AmmoParamsOf = MemberSpec.Field(Atw, "SeaPower.Ammunition", "_ap", false);
        public static readonly MemberSpec ApDisplayedName = MemberSpec.Field(Atw, "SeaPower.AmmunitionParameters", "_natoDisplayedName", false);
        public static readonly MemberSpec ObjUnityPosition = MemberSpec.Method(Atw, "SeaPower.ObjectBase", "getUnityPosition", new string[0], "UnityEngine.Vector3", false);

        // T-230 (ADR-0095): optional; the order of the salvo is followed from the queue to the launch, the arcs of a mount are checked before the order.
        // Without them the old rule stays (one synthetic mount, result by the ship ammo)
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Atw, "SeaPower.ObjectBase", "_obp", false);
        public static readonly MemberSpec ObpWeaponSystems = MemberSpec.Field(Atw, "SeaPower.ObjectBaseParameters", "_weaponSystems", false);
        public static readonly MemberSpec WsTakenTask = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_takenEngageTask", false);
        public static readonly MemberSpec WsExecuting = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_executingEngageTask", false);
        public static readonly MemberSpec WsTargetObject = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_targetObject", false);
        public static readonly MemberSpec WsEngageState = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_engageState", false);
        public static readonly MemberSpec WsReloading = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_isReloading", false);
        public static readonly MemberSpec WsInFiringArc = MemberSpec.Method(Atw, "SeaPower.WeaponSystem", "targetIsInFiringArc", new[] { "UnityEngine.Vector3" }, "bool", false);
        public static readonly MemberSpec WsLoadedCount = MemberSpec.Method(Atw, "SeaPower.WeaponSystem", "getLoadedAmmoCount", new[] { "string" }, "int", false);
        public static readonly MemberSpec WsEndEngageTask = MemberSpec.Method(Atw, "SeaPower.WeaponSystem", "EndEngageTask", new[] { "bool" }, "void", false);
        public static readonly MemberSpec ObjRemoveEngageTask = MemberSpec.Method(Atw, "SeaPower.ObjectBase", "RemoveEngageTask", new[] { "SeaPower.EngageTask" }, "void", false);
        // T-230 turn onto the salvo: the firing arcs of a mount (Vector2 list, degrees relative to the heading)
        public static readonly MemberSpec WsVwp = MemberSpec.Field(Atw, "SeaPower.WeaponSystem", "_vwp", false);
        public static readonly MemberSpec WpFiringArcs = MemberSpec.Field(Atw, "SeaPower.WeaponParameters", "_firingArcs", false);

        // T-230: components of a firing arc (UnityEngine.Vector2, a Unity struct not loaded by the offline selfcheck)
        public static readonly MemberSpec ArcX = MemberSpec.DuckMember(Atw, "x", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec ArcY = MemberSpec.DuckMember(Atw, "y", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");

        public static void Derive(GameApi api, ISet<string> caps)
        {
            bool own = (api.EvasionMode ?? "own").ToLowerInvariant() == "own";
            if (!api.Capabilities.Contains("auto_evade")) caps.Remove(Own);
            if (!own) { caps.Remove(Own); RegistryReport.OffByConfig.Add(Own); }
            if (!caps.Contains(Own) || !api.Capabilities.Contains("attack"))
            {
                caps.Remove(Atw);
                if (!own) RegistryReport.OffByConfig.Add(Atw);
            }
        }

        public static readonly SpecSet Set = SpecSet.Of("evasion", typeof(EvasionSpecs));
    }
}
