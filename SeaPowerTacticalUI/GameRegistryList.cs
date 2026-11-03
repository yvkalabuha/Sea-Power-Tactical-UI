using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static partial class UiRegistry
    {
        /// <summary>Every *Specs.cs class of the UI mod, one line each (the registry test matches the regular expression SpecSet.Of("module", typeof(XSpecs))).</summary>
        static partial void AddSets(List<SpecSet> sets)
        {
            sets.Add(SpecSet.Of("core", typeof(CoreSpecs)));   // M1: shared members, handed over by DialogsFeature (not a module of its own)
            sets.Add(SpecSet.Of("map_layer", typeof(MapLayerSpecs)));   // M2
            sets.Add(SpecSet.Of("radar", typeof(RadarSpecs)));   // M3
            sets.Add(SpecSet.Of("overview", typeof(OverviewSpecs)));   // M4
            sets.Add(SpecSet.Of("overview_own", typeof(OverviewOwnSpecs)));   // T-242
            sets.Add(SpecSet.Of("formation", typeof(FormationSpecs)));   // T-242 (shared with T-243)
            sets.Add(SpecSet.Of("radial", typeof(RadialSpecs)));   // M5
            sets.Add(SpecSet.Of("menu", typeof(MenuSpecs)));   // M6
            sets.Add(SpecSet.Of("evasion", typeof(EvasionSpecs)));   // M7
            sets.Add(SpecSet.Of("doctrine", typeof(DoctrineSpecs)));   // M8
            sets.Add(SpecSet.Of("identify", typeof(IdentifySpecs)));   // M9
            sets.Add(SpecSet.Of("text", typeof(TextSpecs)));   // M10
            sets.Add(SpecSet.Of("sar", typeof(SarSpecs)));   // M11
            sets.Add(SpecSet.Of("air_tick", typeof(AirOpsSpecs)));   // M11/M12: shared aviation members
            sets.Add(SpecSet.Of("asw", typeof(AswSpecs)));   // M13
            sets.Add(SpecSet.Of("cap", typeof(CapSpecs)));   // M14
            sets.Add(SpecSet.Of("smart_attack", typeof(SmartAttackSpecs)));   // M15
            sets.Add(SpecSet.Of("map_menu", typeof(MapMenuSpecs)));   // M16
            sets.Add(SpecSet.Of("game_toolbar", typeof(ToolbarSpecs)));   // T-174
            sets.Add(SpecSet.Of("contact_info", typeof(ContactInfoSpecs)));   // M18
            sets.Add(SpecSet.Of("intercept", typeof(InterceptSpecs)));   // T-138
            sets.Add(SpecSet.Of("range_rings", typeof(RangeRingSpecs)));   // T-137
            sets.Add(SpecSet.Of("hit_estimate", typeof(HitEstimateSpecs)));   // T-168
            sets.Add(SpecSet.Of("air_evasion", typeof(AirEvasionSpecs)));   // T-100
            sets.Add(SpecSet.Of("air_guard", typeof(AirGuardSpecs)));   // T-175
            sets.Add(SpecSet.Of("air_radar", typeof(AirRadarSpecs)));   // T-241
            sets.Add(SpecSet.Of("cap_ff", typeof(CapFfSpecs)));   // T-333: friendly-fire guard of the CAP (all optional)
            sets.Add(SpecSet.Of("strike", typeof(StrikeSpecs)));   // T-341
            sets.Add(SpecSet.Of("air_guard_tc", typeof(AirTimeSpecs)));   // T-175
            sets.Add(SpecSet.Of("air_loss", typeof(AirLossSpecs)));   // T-175
            sets.Add(SpecSet.Of("sub_evasion", typeof(SubEvasionSpecs)));   // T-101
            sets.Add(SpecSet.Of("perf", typeof(PerfSpecs)));   // T-165
            sets.Add(SpecSet.Of("messages", typeof(MessageColorSpecs)));   // T-329
            sets.Add(SpecSet.Of("wp_quiet", typeof(WaypointQuietSpecs)));   // T-331
            sets.Add(SpecSet.Of("unit_action", typeof(UnitActionSpecs)));   // T-234
            sets.Add(SpecSet.Of("harmony", typeof(HarmonySpecs)));   // M19
            sets.Add(SpecSet.Of("save_hook", typeof(SaveHookSpecs)));   // T-198
            sets.Add(SpecSet.Of("buoy_revive", typeof(BuoyReviveSpecs)));   // T-094
            sets.Add(SpecSet.Of("sub_datum", typeof(SubDatumSpecs)));   // T-288
            sets.Add(SpecSet.Of("map_projector", typeof(MapProjectorSpecs)));   // T-309
            sets.Add(SpecSet.Of("grid", typeof(GridSpecs)));   // T-308
        }
    }
}
