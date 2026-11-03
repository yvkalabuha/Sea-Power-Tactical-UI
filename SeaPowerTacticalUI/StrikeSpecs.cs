using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-341 / ADR-0132 section 2: the game members of the strike window. Capability "strike" (required: the ammunition fields of an aircraft's missiles) is what the window needs;
    /// capability "strike_info" (optional) only improves the plan: the simulator of the game for the time of flight (EstimateShot, else the rough formula), the points cache of
    /// the game (aa_k / aa_s per class, else AAW_Capability, else the role), the SAM parameters (the port of the interception chance, else no correction).
    /// Members that other modules already declare for their own capability (aircraft, the player's tracks, the class encyclopedia) are used through those specs
    /// (AirOpsSpecs, OverviewSpecs, RangeRingSpecs, CoreSpecs, CapFfSpecs); "strike" needs those capabilities too (Derive).
    /// One file for both lines: a signature that differs in the beta is a WithAltParams, never a second copy.
    /// </summary>
    internal static class StrikeSpecs
    {
        private const string Cap = "strike";
        private const string Info = "strike_info";
        private const string Probe = "strike_probe";
        private const string Amm = "SeaPower.AmmunitionParameters";

        // ---- required: the ammunition fields that the missile classification reads ----
        public static readonly MemberSpec AmmoGuidance = MemberSpec.Field(Cap, Amm, "_guidanceType");              // enum: None, IRHoming, SemiActiveRadarHoming, ActiveRadarHoming, AntiRadiationHoming, LaserHoming, TVHoming ...
        public static readonly MemberSpec AmmoMidCourse = MemberSpec.Field(Cap, Amm, "_midCourseCorrection");      // object: ToString() None / RadioCommand / WireGuided / Datalink
        public static readonly MemberSpec AmmoLaunchAlts = MemberSpec.Field(Cap, Amm, "_launchAltitudesInUnity");  // Vector2: x = min, y = max launch altitude (Unity units)
        public static readonly MemberSpec AmmoMaxVelocity = MemberSpec.Field(Cap, Amm, "_maxVelocityInKnots");
        public static readonly MemberSpec AmmoTargetRequired = MemberSpec.Field(Cap, Amm, "_targetRequired");
        public static readonly MemberSpec AmmoType = MemberSpec.Field(Cap, Amm, "_type");
        public static readonly MemberSpec AmmoTargetType = MemberSpec.Field(Cap, Amm, "_targetType");
        public static readonly MemberSpec AmmoLaunchRanges = MemberSpec.Field(Cap, Amm, "_launchRangesInUnity");   // Vector2: x = min, y = max launch range (Unity units)
        public static readonly MemberSpec AmmoDisplayName = MemberSpec.Field(Cap, Amm, "_natoDisplayedName");
        public static readonly MemberSpec AmmoOfAmmunition = MemberSpec.Field(Cap, "SeaPower.Ammunition", "_ap");
        public static readonly MemberSpec ObjGetAmmoByName = MemberSpec.Method(Cap, "SeaPower.ObjectBase", "getAmmunitionByName", new[] { "string" }, "SeaPower.Ammunition");
        public static readonly MemberSpec ObjAmmoCounts = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "AmmunitionAmountDictionary");

        // ---- optional: the plan gets better with them ----
        public static readonly MemberSpec AmmoFrontalArea = MemberSpec.Field(Info, Amm, "_frontalAreaM2", false);
        public static readonly MemberSpec AmmoMass = MemberSpec.Field(Info, Amm, "_massInKg", false);
        public static readonly MemberSpec AmmoDefenceBonus = MemberSpec.Field(Info, Amm, "_samDefenceBonus", false);
        public static readonly MemberSpec AmmoTerminalAlt = MemberSpec.Field(Info, Amm, "_terminalAltUnity", false);                // phase E1: < 0 = not set
        public static readonly MemberSpec AmmoFinalAlt = MemberSpec.Field(Info, Amm, "_finalFlightPhaseAltUnity", false);
        public static readonly MemberSpec AmmoTerminalVel = MemberSpec.Field(Info, Amm, "_terminalVelocityInKnots", false);
        public static readonly MemberSpec AmmoSeaSkimFt = MemberSpec.Field(Info, Amm, "_seaSkimmingAltFeet", false);
        public static readonly MemberSpec AmmoLoftAlt = MemberSpec.Field(Info, Amm, "_maxLoftAltUnity", false);
        public static readonly MemberSpec AmmoKillProb = MemberSpec.Field(Info, Amm, "_killProbability", false);                // SAM: the base chance of a kill
        public static readonly MemberSpec AmmoAttackAlts = MemberSpec.Field(Info, Amm, "_attackAltitudesInUnity", false);      // SAM: Vector2 min / max target altitude
        public static readonly MemberSpec AmmoMaxAttackVel = MemberSpec.Field(Info, Amm, "_maxAttackVelocity", false);         // SAM: knots (float.MaxValue = none)
        public static readonly MemberSpec AmmoOoaPenalty = MemberSpec.Field(Info, Amm, "_outOfAltitudePenalty", false);
        public static readonly MemberSpec AmmoSpeedMult = MemberSpec.Field(Info, Amm, "_speedPenaltyMultiplier", false);
        public static readonly MemberSpec AmmoSizeMult = MemberSpec.Field(Info, Amm, "_sizePenaltyMultiplier", false);
        public static readonly MemberSpec AmmoGetShared = MemberSpec.Method(Info, Amm, "GetShared", new[] { "string" }, "AmmunitionParameters", false, true);
        public static readonly MemberSpec SopAawCapability = MemberSpec.Field(Info, "SeaPower.SimpleObjectParameters", "_aawCapability", false);   // 0 = no key in the class ini (unknown)
        public static readonly MemberSpec CacheType = MemberSpec.TypeOf(Info, "SeaPower.PointValueCache", false);
        public static readonly MemberSpec CacheData = MemberSpec.Field(Info, "SeaPower.PointValueCache", "_cache", false, true);   // private static CacheFile
        public static readonly MemberSpec CacheUnits = MemberSpec.Prop(Info, "SeaPower.PointValueCache+CacheFile", "Units", false);
        public static readonly MemberSpec CacheRoles = MemberSpec.Prop(Info, "SeaPower.PointValueCache+CacheEntry", "Roles", false);
        public static readonly MemberSpec SimEstimateShot = MemberSpec.Method(Info, "SeaPower.MissileSimulator", "EstimateShot",
            new[] { "AmmunitionParameters", "Vector3", "float", "Vector3", "Vector3", "bool", "float", "float", "float", "bool", "List`1", "List`1", "List`1", "bool" }, null, false, true);   // returns KinematicRangeResult (class on 0.30, struct on 0.31: not checked)
        public static readonly MemberSpec SimInterceptTime = MemberSpec.Field(Info, "SeaPower.MissileSimulator+KinematicRangeResult", "InterceptTime", false);
        public static readonly MemberSpec SimTargetTooClose = MemberSpec.Field(Info, "SeaPower.MissileSimulator+KinematicRangeResult", "TargetTooClose", false);

        // ---- optional (phase E1): the difficulty of the game (the SAM chance against the player's missiles is multiplied by 1 - reduction) ----
        public static readonly MemberSpec GlobalMissileReduction = MemberSpec.Field(Info, "SeaPower.Globals", "_missileInterceptChanceReduction", false, true);

        // ---- optional: the dev probe (strike_probe): the veto of an auto attack, the missiles in flight and what their seeker holds ----
        public static readonly MemberSpec TaskBase = MemberSpec.Field(Probe, "SeaPower.EngageTask", "_baseObject", false);
        public static readonly MemberSpec TaskTarget = MemberSpec.Field(Probe, "SeaPower.EngageTask", "_targetObject", false);
        public static readonly MemberSpec TaskAmmoId = MemberSpec.Field(Probe, "SeaPower.EngageTask", "_ammoId", false);
        public static readonly MemberSpec ObpWeaponSystems = MemberSpec.Field(Probe, "SeaPower.ObjectBaseParameters", "_weaponSystems", false);   // List<WeaponSystem>
        public static readonly MemberSpec WsLaunched = MemberSpec.Field(Probe, "SeaPower.WeaponSystem", "_launchedWeapons", false);              // List<WeaponBase>
        public static readonly MemberSpec WbInitTarget = MemberSpec.Field(Probe, "SeaPower.WeaponBase", "_initialSetTargetObject", false);
        public static readonly MemberSpec WbLastTarget = MemberSpec.Field(Probe, "SeaPower.WeaponBase", "_lastTargetObject", false);
        // T-342 (E1 calibration, DEV PROBE ONLY: strike_probe op=limit): the sinking state of a foreign ship. Never read outside StrikeGame.Probe.cs; not in the window and not in ui_state.
        public static readonly MemberSpec CompLimit = MemberSpec.Field(Probe, "SeaPower.Compartments", "_limitIntegrity", false);
        public static readonly MemberSpec CompMaxLimit = MemberSpec.Field(Probe, "SeaPower.Compartments", "_maxLimitIntegrity", false);
        public static readonly MemberSpec CompLastDamage = MemberSpec.Field(Probe, "SeaPower.Compartments", "_lastInflictedDamage", false);

        // ---- optional (T-342 phase B, capability strike_deck): the magazine of a flight deck, per accountable category, and what one aircraft of a loadout takes from it; the launches the mission forbids (beta only: missing on 0.30 is no error) ----
        private const string DeckCap = "strike_deck";
        public static readonly MemberSpec DeckMagazine = MemberSpec.Field(DeckCap, "SeaPower.FlightDeck", "_accountableAmmunition", false);
        public static readonly MemberSpec DeckMagazineMax = MemberSpec.Field(DeckCap, "SeaPower.FlightDeck", "_accountableAmmunitionCapacity", false);
        public static readonly MemberSpec LoCategories = MemberSpec.Field(DeckCap, "SeaPower.Loadout", "_accountableAmmunition", false);
        public static readonly MemberSpec DeckLaunchesDisabled = MemberSpec.Prop(DeckCap, "SeaPower.FlightDeck", "LaunchesDisabled", false);
        public static readonly MemberSpec DeckLaunchesDisabledField = MemberSpec.Field(DeckCap, "SeaPower.FlightDeck", "LaunchesDisabled", false);

        // ---- optional (phase D, capability strike_ew): the jammer aircraft. Own stations are read through the specs of the radar jam module (RadarSpecs.Ecm*, Ep*: own units only, no leak);
        // the order is a JamTask by a point (its constructor runs the order, as Order.Type.Jam does), the end is ECM.ceaseOffensiveJam + IsOn = false (JamTask of a point cannot be repeated to stop) ----
        private const string Ew = "strike_ew";
        public static readonly MemberSpec ObpEcmSystems = MemberSpec.Field(Ew, "SeaPower.ObjectBaseParameters", "_ecmSystems", false);               // List<SensorSystemECM>
        public static readonly MemberSpec SysSystemName = MemberSpec.Field(Ew, "SeaPower.SensorSystem", "_systemName", false);
        public static readonly MemberSpec EpConeFov = MemberSpec.Field(Ew, "SeaPower.ECMParameters", "_jamConeFov", false);                          // degrees, the full cone
        public static readonly MemberSpec EpMaxRange = MemberSpec.Field(Ew, "SeaPower.ECMParameters", "_maxRange", false);                          // Unity units
        public static readonly MemberSpec JamTaskCtor = MemberSpec.Ctor(Ew, "SeaPower.JamTask", new[] { "SeaPower.ObjectBase", "SeaPower.ObjectBase", "SeaPower.GeoPosition", "bool", "bool" }, false);
        public static readonly MemberSpec EcmCease = MemberSpec.Method(Ew, "SeaPower.ECM", "ceaseOffensiveJam", new string[0], "void", false);
        public static readonly MemberSpec EcmGeoTargeted = MemberSpec.Field(Ew, "SeaPower.SensorSystemECM", "_geoPositionTargeted", false);
        public static readonly MemberSpec ObjJammersOfMe = MemberSpec.Field(Ew, "SeaPower.ObjectBase", "_ECMSystemsTryingToJamMe", false);           // DEV PROBE ONLY (strike_probe op=ew status): never read outside StrikeGame.Probe.cs / the dev status
        public static readonly MemberSpec RadarEntryType = MemberSpec.TypeOf(Ew, "SeaPower.MuseumRadarEntry", false);
        public static readonly MemberSpec RadarEntryFreq = MemberSpec.Prop(Ew, "SeaPower.MuseumRadarEntry", "Frequency", false);
        public static readonly MemberSpec RadarEntryMax = MemberSpec.Prop(Ew, "SeaPower.MuseumRadarEntry", "MaxRange", false);
        public static readonly MemberSpec EcmEntryType = MemberSpec.TypeOf(Ew, "SeaPower.MuseumEcmEntry", false);
        public static readonly MemberSpec EcmEntryKind = MemberSpec.Prop(Ew, "SeaPower.MuseumSensorEntry", "Type", false);          // "OffensiveECM" | "DefensiveECM" | "UniversalECM"
        public static readonly MemberSpec EcmEntryChannels = MemberSpec.Prop(Ew, "SeaPower.MuseumEcmEntry", "JamChannels", false);
        public static readonly MemberSpec EcmEntryFreqs = MemberSpec.Prop(Ew, "SeaPower.MuseumEcmEntry", "Frequencies", false);

        /// <summary>The window needs the aircraft (air_ops), the class encyclopedia and ranges (range_rings_class) and the player's tracks (overview).</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            foreach (string need in new[] { "air_ops", "overview", "range_rings_class" })
                if (!(caps.Contains(need) || api.Capabilities.Contains(need))) { caps.Remove(Cap); return; }
        }

        public static readonly SpecSet Set = SpecSet.Of("strike", typeof(StrikeSpecs));
    }
}
