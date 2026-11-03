using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-168 / ADR-0064 (ADR-0043): the game members of the hits-to-kill estimate.
    /// hit_estimate_class = the class data of a target (the game's own ini loader, never the object); hit_estimate_own = the own armament (parameters of an ammunition,
    /// hardpoints, the loadouts of the hangars are read through air_ops); hit_estimate_bonus = Globals._damageBonus (optional: 0 without it);
    /// hit_estimate_hits = the hook on the game's weapon report (optional: no list and no hits / dmg cells without it).
    /// hit_estimate = the module: class AND own AND range_rings_own (the units' ammunition groups and the side check are shared with the range rings).
    /// The members shared with other modules are referenced from their *Specs: RangeRingSpecs (groups, file name, type, target types, remaining count, taskforce lists),
    /// AirOpsSpecs (deck vehicles, loadouts), OverviewSpecs.VehId, InterceptSpecs.ObjMapVehicle, CoreSpecs.ObjName.
    /// </summary>
    internal static class HitEstimateSpecs
    {
        private const string Cls = "hit_estimate_class";
        private const string Zones = "hit_estimate_zones";
        private const string Own = "hit_estimate_own";
        private const string Bonus = "hit_estimate_bonus";
        private const string Hits = "hit_estimate_hits";
        private const string Ap = "SeaPower.AmmunitionParameters";
        private const string Sop = "SeaPower.SimpleObjectParameters";
        private const string Ini = "SeaPower.IniHandler";
        private const string Wb = "SeaPower.WeaponBase";

        // ---------- the class of a target (ini of the class, by the game's loader) ----------
        public static readonly MemberSpec FindCached = MemberSpec.Method(Cls, "SeaPower.UnitMetadataProvider", "FindCached", new[] { "string", "string" }, "SimpleObjectParameters", true, true);
        public static readonly MemberSpec SopDisplacement = MemberSpec.Field(Cls, Sop, "_displacement");
        public static readonly MemberSpec SopIsCivilian = MemberSpec.Field(Cls, Sop, "_isCivilian");
        public static readonly MemberSpec SopObjectType = MemberSpec.Field(Cls, Sop, "_objectType");
        public static readonly MemberSpec SopUnitRoles = MemberSpec.Field(Cls, Sop, "_unitRoles", false);
        public static readonly MemberSpec SopIni = MemberSpec.Field(Cls, Sop, "_ini");
        // fallback when the metadata cache is stale (FindCached answers null after any ini reload): the game's own loader of one class
        public static readonly MemberSpec LoadSimple = MemberSpec.Method(Cls, Sop, "LoadSimpleParameters", new[] { "string", "string", "int" }, "SimpleObjectParameters", false, true);
        public static readonly MemberSpec IniReadValue = MemberSpec.Method(Cls, Ini, "readValue", new[] { "string", "string", "string", "bool" }, "string");
        // T-170: the section names of the ini; the capability hit_estimate_zones (zones of large objects, ADR-0065 item 7) needs it, the estimate of ships does not
        public static readonly MemberSpec IniSections = MemberSpec.Method(Zones, Ini, "getNamesOfAllSections", new string[0], null, true);

        // ---------- the own armament ----------
        public static readonly MemberSpec AmmoWarheadPower = MemberSpec.Field(Own, Ap, "_warheadPower");
        public static readonly MemberSpec AmmoWarheadType = MemberSpec.Field(Own, Ap, "_warheadType");
        public static readonly MemberSpec AmmoImpactSize = MemberSpec.Field(Own, Ap, "_impactSizeUnity");
        public static readonly MemberSpec AmmoDudPower = MemberSpec.Field(Own, Ap, "_warheadDudPower", false);
        public static readonly MemberSpec AmmoDamageMult = MemberSpec.Field(Own, Ap, "_damageMultiplier", false);
        public static readonly MemberSpec AmmoPenetration = MemberSpec.Field(Own, Ap, "_penetration", false);
        public static readonly MemberSpec AmmoDisplayedName = MemberSpec.Prop(Own, Ap, "_displayedName", false);
        public static readonly MemberSpec AmmoCannotAttack = MemberSpec.Field(Own, Ap, "_canNotAttackTypes", false);
        public static readonly MemberSpec AmmoLandAttack = MemberSpec.Field(Own, Ap, "_landAttack", false);
        public static readonly MemberSpec AmmoHasSubmunition = MemberSpec.Field(Own, Ap, "_hasSubmunition", false);
        public static readonly MemberSpec AmmoSubmunitionFile = MemberSpec.Field(Own, Ap, "_submunitionFileName", false);
        public static readonly MemberSpec AmmoGetShared = MemberSpec.Method(Own, Ap, "GetShared", new[] { "string" }, "AmmunitionParameters", false, true);
        public static readonly MemberSpec ParHardpointGroups = MemberSpec.Field(Own, "SeaPower.ObjectBaseParameters", "_hardpointAmmunitionGroups", false);

        // ---------- the difficulty bonus (optional) ----------
        public static readonly MemberSpec GlobalsDamageBonus = MemberSpec.Field(Bonus, "SeaPower.Globals", "_damageBonus", false, true);

        // ---------- the game's weapon report (optional) ----------
        public static readonly PatchSpec PatchHits = new PatchSpec("hit_estimate_hits", Hits, "SeaPower.TaskforceWeaponReportExtensions", "RecordWeaponHitUnit", new[] { "Taskforce", "WeaponBase", "ObjectBase" }, "void", PatchKind.PrefixPostfix, "always", false);
        public static readonly MemberSpec WeaponAp = MemberSpec.Field(Hits, Wb, "_ap", false);
        public static readonly MemberSpec WeaponMalfunction = MemberSpec.Field(Hits, Wb, "_warheadMalfunction", false);
        public static readonly MemberSpec WeaponLaunchPlatform = MemberSpec.Field(Hits, Wb, "_launchPlatform", false);
        public static readonly MemberSpec WeaponReportIds = MemberSpec.Field(Hits, Wb, "_weaponReportHitUnitIds", false);
        public static readonly MemberSpec WeaponCiwsDamage = MemberSpec.Field(Hits, Wb, "_ciwsDamageTaken", false);   // T-342: the damage the CIWS did to the missile (optional: the ledger works without it)
        public static readonly MemberSpec EnvironmentType = MemberSpec.TypeOf(Hits, "SeaPower.Environment", false);
        public static readonly MemberSpec EnvironmentDateTime = MemberSpec.Prop(Hits, "SeaPower.Environment", "DateTime", false);
        public static readonly MemberSpec GameTimeSession = MemberSpec.Field(Hits, "SeaPower.GameTime", "missionSessionTime", false, true);

        public static void Derive(GameApi api, ISet<string> caps)
        {
            bool ok = (caps.Contains(Cls) || api.Capabilities.Contains(Cls)) && (caps.Contains(Own) || api.Capabilities.Contains(Own))
                && (caps.Contains("range_rings_own") || api.Capabilities.Contains("range_rings_own"));
            if (ok) caps.Add("hit_estimate"); else caps.Remove("hit_estimate");
            // the reference of large objects lives on the class data
            if (!(caps.Contains(Cls) || api.Capabilities.Contains(Cls))) caps.Remove(Zones);
        }

        public static readonly SpecSet Set = SpecSet.Of("hit_estimate", typeof(HitEstimateSpecs));
    }
}
