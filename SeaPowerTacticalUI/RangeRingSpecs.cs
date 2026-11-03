using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-137 (ADR-0055, ADR-0043): the game members of the range rings. range_rings_own = what the game holds about the player's own (and allied)
    /// units: ammunition groups, the remaining ammunition, mounts and their arcs, sonars (RangeRingGame.Own.cs). range_rings_class = the encyclopedia
    /// of the game, the only source for an identified foreign unit (RangeRingGame.Class.cs). range_rings = the module: it needs the radar layer
    /// (radar_noesis) and at least one of the two parts. The members shared with other modules are referenced from their *Specs, not repeated:
    /// RadarSpecs.ObjObp, OverviewSpecs.VehObject / VehPosition / VehIdentified / ObjType / ObjIniName, EvasionSpecs.ObjHeading.
    /// </summary>
    internal static class RangeRingSpecs
    {
        private const string Own = "range_rings_own";
        private const string Cls = "range_rings_class";
        private const string Kin = "range_rings_kin";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Obp = "SeaPower.ObjectBaseParameters";
        private const string Ap = "SeaPower.AmmunitionParameters";
        private const string Ws = "SeaPower.WeaponSystem";
        private const string Sonar = "SeaPower.SensorSystemSonar";
        private const string Mwe = "SeaPower.MuseumWeaponEntry";

        // ---------- own units, required ----------
        public static readonly MemberSpec ParLauncherGroups = MemberSpec.Field(Own, Obp, "_launcherAmmunitionGroups");
        public static readonly MemberSpec ParGunGroups = MemberSpec.Field(Own, Obp, "_gunAmmunitionGroups");
        public static readonly MemberSpec GroupAp = MemberSpec.Prop(Own, "SeaPower.AmmunitionGroup", "Ap");
        public static readonly MemberSpec AmmoTargetType = MemberSpec.Field(Own, Ap, "_targetType");
        public static readonly MemberSpec AmmoSecondaryTargetType = MemberSpec.Field(Own, Ap, "_secondaryTargetType");
        public static readonly MemberSpec AmmoLaunchRanges = MemberSpec.Field(Own, Ap, "_launchRangesInUnity");
        public static readonly MemberSpec AmmoType = MemberSpec.Field(Own, Ap, "_type");
        public static readonly MemberSpec AmmoFileName = MemberSpec.Field(Own, Ap, "_ammunitionFileName");
        public static readonly MemberSpec VecX = MemberSpec.DuckMember(Own, "x", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecY = MemberSpec.DuckMember(Own, "y", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec ObjTaskforce = MemberSpec.Prop(Own, Ob, "_taskforce");
        public static readonly MemberSpec TaskforceSide = MemberSpec.Prop(Own, "SeaPower.Taskforce", "Side");

        // ---------- own units, optional (without them only that part of the function is missing) ----------
        public static readonly MemberSpec ObjAmmoAmountByName = MemberSpec.Method(Own, Ob, "getAmmunitionAmountByName", new[] { "string" }, "int", false);
        public static readonly MemberSpec AmmoAntiTorpedo = MemberSpec.Field(Own, Ap, "_antiTorpedo", false);
        public static readonly MemberSpec AmmoSubType = MemberSpec.Field(Own, Ap, "_subType", false);   // 2026-10-22: the real kind of a buoy / fuel tank / MOSS (_type is Bomb / Torpedo)
        public static readonly MemberSpec ParGunWeapons = MemberSpec.Field(Own, Obp, "_gunWeapons", false);
        public static readonly MemberSpec ParCiwsWeapons = MemberSpec.Field(Own, Obp, "_ciwsWeapons", false);
        public static readonly MemberSpec ParLauncherWeapons = MemberSpec.Field(Own, Obp, "_launcherWeapons", false);
        public static readonly MemberSpec ParHardpointWeapons = MemberSpec.Field(Own, Obp, "_hardpointWeapons", false);
        public static readonly MemberSpec ParSonarSystems = MemberSpec.Field(Own, Obp, "_sonarSystems", false);
        public static readonly MemberSpec ParActiveSonarSystems = MemberSpec.Field(Own, Obp, "_activeSonarSystems", false);
        public static readonly MemberSpec ParTowedSonarSystems = MemberSpec.Field(Own, Obp, "_towedSonarSystems", false);
        public static readonly MemberSpec WsParameters = MemberSpec.Field(Own, Ws, "_vwp", false);
        public static readonly MemberSpec WsAmmoNames = MemberSpec.Field(Own, Ws, "_associatedAmmunitionNames", false);
        public static readonly MemberSpec WpFiringArcs = MemberSpec.Field(Own, "SeaPower.WeaponParameters", "_firingArcs", false);
        public static readonly MemberSpec SonarSystemSonar = MemberSpec.Field(Own, Sonar, "_sonar", false);
        public static readonly MemberSpec SonarParams = MemberSpec.Field(Own, "SeaPower.Sonar", "_sp", false);
        public static readonly MemberSpec SpActiveRangeKm = MemberSpec.Field(Own, "SeaPower.SonarParameters", "_activeRangeInKm", false);
        public static readonly MemberSpec SpPassiveRangeKm = MemberSpec.Field(Own, "SeaPower.SonarParameters", "_passiveRangeInKm", false);
        public static readonly MemberSpec ObjFormation = MemberSpec.Prop(Own, Ob, "Formation", false);
        public static readonly MemberSpec FormationStations = MemberSpec.Prop(Own, "SeaPower.UnitFormation", "Stations", false);
        public static readonly MemberSpec StationUnit = MemberSpec.Prop(Own, "SeaPower.Station", "UnitObject", false);

        // unit lookup by UniqueID in the player's and the allied taskforce (the units of an ally are not in GameApi.FindOwnUnit)
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Own, Ob, "UniqueID", false);
        public static readonly MemberSpec GlobalsPlayerTaskforce = MemberSpec.Field(Own, "SeaPower.Globals", "_playerTaskforce", false, true);
        public static readonly MemberSpec GlobalsAlliedTaskforce = MemberSpec.Field(Own, "SeaPower.Globals", "_alliedTaskforce", false, true);
        public static readonly MemberSpec TfVessels = MemberSpec.Field(Own, "SeaPower.Taskforce", "_taskforceVessels", false);
        public static readonly MemberSpec TfSubmarines = MemberSpec.Field(Own, "SeaPower.Taskforce", "_taskforceSubmarines", false);
        public static readonly MemberSpec TfAircraft = MemberSpec.Field(Own, "SeaPower.Taskforce", "_taskforceAircraft", false);
        public static readonly MemberSpec TfHelicopters = MemberSpec.Field(Own, "SeaPower.Taskforce", "_taskforceHelicopters", false);

        // ---------- the class reference (encyclopedia) of an identified foreign unit ----------
        public static readonly MemberSpec EncyclopediaType = MemberSpec.TypeOf(Cls, "SeaPower.EncyclopedicDataManager");
        public static readonly MemberSpec EncUnitsLoaded = MemberSpec.Prop(Cls, "SeaPower.EncyclopedicDataManager", "UnitsLoaded");
        public static readonly MemberSpec EncEntries = MemberSpec.Prop(Cls, "SeaPower.EncyclopedicDataManager", "MuseumObjectEntries");
        public static readonly MemberSpec EntryIniRef = MemberSpec.Prop(Cls, "SeaPower.MuseumEntry", "IniRef");
        public static readonly MemberSpec EntryVariantIndex = MemberSpec.Prop(Cls, "SeaPower.MuseumEntry", "VariantIndex");
        public static readonly MemberSpec EntryWeapons = MemberSpec.Prop(Cls, "SeaPower.MuseumObjectEntry", "WeaponEntriesOffenses");
        public static readonly MemberSpec EntrySensors = MemberSpec.Prop(Cls, "SeaPower.MuseumObjectEntry", "SensorEntries");
        public static readonly MemberSpec WeaponTargetType = MemberSpec.Prop(Cls, Mwe, "TargetType");
        public static readonly MemberSpec WeaponSecondaryTargetType = MemberSpec.Prop(Cls, Mwe, "SecondaryTargetType");
        public static readonly MemberSpec WeaponHasSecondary = MemberSpec.Prop(Cls, Mwe, "HasSecondaryTargetType");
        public static readonly MemberSpec WeaponType = MemberSpec.Prop(Cls, Mwe, "Type");
        public static readonly MemberSpec WeaponMinRange = MemberSpec.Prop(Cls, Mwe, "MinLaunchRange");
        public static readonly MemberSpec WeaponMaxRange = MemberSpec.Prop(Cls, Mwe, "MaxLaunchRange");
        public static readonly MemberSpec WeaponGunAaRange = MemberSpec.Prop(Cls, Mwe, "GunAaRange");
        public static readonly MemberSpec SonarEntryType = MemberSpec.TypeOf(Cls, "SeaPower.MuseumSonarEntry");
        public static readonly MemberSpec SonarEntryPassive = MemberSpec.Prop(Cls, "SeaPower.MuseumSonarEntry", "PassiveRangeCalculated");
        public static readonly MemberSpec LengthNm = MemberSpec.Prop(Cls, "Seapower.Length", "InNauticalMiles");
        // T-171 / ADR-0068 (0.8.7-261021), optional: the air-launched flag of a weapon entry; the date-based ammunition of a class mount
        public static readonly MemberSpec WeaponAirLaunched = MemberSpec.Prop(Cls, Mwe, "IsAirLaunched", false);
        public static readonly MemberSpec UtilsResolveDateAmmo = MemberSpec.Method(Cls, "SeaPower.Utils", "ResolveDateBasedAmmo", new[] { "IniHandler", "string" }, "string", false, true);

        // ---------- T-321 / ADR-0125, all optional: the range of an own weapon as the game counts it (RangeRingGame.Kin.cs); without them the nominal range stays ----------
        public static readonly MemberSpec ApMaxRangeRough = MemberSpec.Method(Kin, Ap, "MaxRangeRough", new[] { "ObjectBase", "Vector3", "Vector3", "bool" }, "float", false);
        public static readonly MemberSpec ApEffectiveMax = MemberSpec.Method(Kin, Ap, "EffectiveMaxLaunchRange", new[] { "ObjectBase" }, "float", false);
        public static readonly MemberSpec ApKinematics = MemberSpec.Prop(Kin, Ap, "Kinematics", false);
        public static readonly MemberSpec ApTypicalTargetAlt = MemberSpec.Prop(Kin, Ap, "TypicalTargetAlt", false);
        public static readonly MemberSpec ApTypicalTargetVelocity = MemberSpec.Prop(Kin, Ap, "TypicalTargetVelocity", false);
        public static readonly MemberSpec GeoNew = MemberSpec.Ctor(Kin, "SeaPower.GeoPosition", new[] { "double", "double", "double" }, false);
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Field(Kin, Ob, "_velocityInKnots", false);

        public static void Derive(GameApi api, ISet<string> caps)
        {
            var radar = caps.Contains("radar_noesis") || api.Capabilities.Contains("radar_noesis");
            if (!radar || (!caps.Contains(Own) && !caps.Contains(Cls))) caps.Remove("range_rings");
            else caps.Add("range_rings");
            // the kinematic range is a part of the own-weapon rings
            if (!caps.Contains(Own)) caps.Remove(Kin);
            // the mounts of a class come from the ini loader of hit_estimate_class; without it the foreign rings stay circles
            if (caps.Contains(Cls) && (caps.Contains("hit_estimate_class") || api.Capabilities.Contains("hit_estimate_class"))) caps.Add("range_rings_arcs");
            else caps.Remove("range_rings_arcs");
        }

        public static readonly SpecSet Set = SpecSet.Of("range_rings", typeof(RangeRingSpecs));
    }
}
