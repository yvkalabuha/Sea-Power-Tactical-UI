using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-112 / ADR-0040: the CAP window (config section Cap): controller, window, overlay, hotkey and the "Return control" participant.</summary>
    internal sealed class CapFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private CapParams _capP;
        private bool _capEnabled = true, _capDebug, _capAutoRadar = true;   // _capAutoRadar: T-241 [Cap] AutoRadar
        private string _capHotkey = "LeftAlt+P|LeftAlt+K|F8";
        private CapWindowGui _capWin;
        private CapOverlay _capOv;
        private string _lastCircleGroup;   // T-197: the alert group whose circles stay on the map while the window is closed
        private AirOwnership _airOwn;
        private HotkeyBinding _hotkey;
        private IKeyInput _input;
        private bool _ffOn = true, _ffChecked;   // T-333: [Cap] FriendlyFireGuard (live); the subscription is checked once in the mission
        private CapFfGuard _ffGuard;

        public CapFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "cap"; } }
        public int SettingsOrder { get { return 60; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(CapSpecs.Set); check.Specs(AirRadarSpecs.Set); check.Specs(CapFfSpecs.Set); check.Specs(QaForceSpecs.Set); }   // T-241: the radar members are checked with the CAP module
        public override string[] DependsOn { get { return new[] { "radar", "asw", "toast" }; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Cap";
            var p = new CapParams();
            _capEnabled = cfg.Bind(S, "Enabled", true, "Air defence (CAP) window: groups of fighters in a zone with relief from the reserve", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sHotkey = cfg.Bind(S, "Hotkey", _capHotkey, "Opens the CAP window: Unity KeyCode names joined by + (e.g. LeftAlt+P); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            _capHotkey = sHotkey.Value;
            var sMissilePct = cfg.Bind(S, "MissilePct", p.MissilePct, "A bird leaves when its missiles are at or below this share of the start count (0..1)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 1, Step = 0.05, DisplayFactor = 100, UnitKey = "pct" });
            p.MissilePct = Math.Max(0, Math.Min(1, sMissilePct.Value));
            var sFuelMarginS = cfg.Bind(S, "FuelMarginS", p.FuelMarginS, "Fuel: the bird leaves when it has the flight home plus this many seconds left", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.FuelMarginS = Math.Max(0, sFuelMarginS.Value);
            var sFuelFloorPct = cfg.Bind(S, "FuelFloorPct", p.FuelFloorPct, "Fuel floor as a share of the maximum range (0..0.5)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.FuelFloorPct = Math.Max(0, Math.Min(0.5, sFuelFloorPct.Value));
            var sRingMarginNm = cfg.Bind(S, "RingMarginNm", p.RingMarginNm, "Fuel: with the game's fuel share known, the bird leaves when its spare path over the way home is at or below this many nm", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder, UnitKey = "nm" });
            p.RingMarginNm = Math.Max(0, sRingMarginNm.Value);
            var sCombatHostileNm = cfg.Bind(S, "CombatHostileNm", p.CombatHostileNm, "A leaving bird in combat or with a hostile closer than this (nm) postpones its leave", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.CombatHostileNm = Math.Max(0, sCombatHostileNm.Value);
            var sPursuitNm = cfg.Bind(S, "PursuitNm", p.PursuitNm, "Leash: a bird farther than this from the zone (nm) is called back", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.PursuitNm = Math.Max(5, sPursuitNm.Value);
            var sThreatNm = cfg.Bind(S, "ThreatNm", p.ThreatNm, "Air threat radius (nm) to the zone or the deck: a CAP group with a gap then takes the deck first (ADR-0041)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ThreatNm = Math.Max(10, sThreatNm.Value);
            var sThreatClearMarginNm = cfg.Bind(S, "ThreatClearMarginNm", p.ThreatClearMarginNm, "The threat is cleared only beyond ThreatNm + this margin (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ThreatClearMarginNm = Math.Max(0, sThreatClearMarginNm.Value);
            var sThreatClearS = cfg.Bind(S, "ThreatClearS", p.ThreatClearS, "... and after this many seconds without such a contact", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ThreatClearS = Math.Max(0, sThreatClearS.Value);
            var sLaunchDelayS = cfg.Bind(S, "LaunchDelayS", p.LaunchDelayS, "Seconds from the launch order to the take-off (relief planning)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LaunchDelayS = Math.Max(0, sLaunchDelayS.Value);
            var sPatrolAltFt = cfg.Bind(S, "PatrolAltFt", p.PatrolAltFt, "Patrol altitude, ft (0 = the game's default)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 40000, ZeroIsAuto = true, Step = 500, UnitKey = "ft" });
            p.PatrolAltFt = Math.Max(0, sPatrolAltFt.Value);
            var sLaneEndMaxNm = cfg.Bind(S, "LaneEndMaxNm", p.LaneEndMaxNm, "Racetrack: the legs stop this far (nm, at most) from the zone ends", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LaneEndMaxNm = Math.Max(0, sLaneEndMaxNm.Value);
            var sCoverAamFrac = cfg.Bind(S, "CoverAamFrac", p.CoverAamFrac, "CAP cover: share of the longest AAM range of the group's aircraft; every point of the zone stays within it from the route", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.CoverAamFrac = Math.Max(0.1, Math.Min(1, sCoverAamFrac.Value));
            var sCoverFallbackNm = cfg.Bind(S, "CoverFallbackNm", p.CoverFallbackNm, "CAP cover radius (nm) while no aircraft of the group reports its AAM range", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.CoverFallbackNm = Math.Max(1, Math.Min(200, sCoverFallbackNm.Value));
            var sCircleAltStepFt = cfg.Bind(S, "CircleAltStepFt", p.CircleAltStepFt, "Small CAP zone (one circle for the group): altitude step (ft) between the birds on the circle, 0 = one altitude", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.CircleAltStepFt = Math.Max(0, Math.Min(5000, sCircleAltStepFt.Value));
            var sOrbitBelowNm = cfg.Bind(S, "OrbitBelowNm", p.OrbitBelowNm, "Lane narrower than this (nm) becomes an orbit", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.OrbitBelowNm = Math.Max(0, sOrbitBelowNm.Value);
            var sDefaultOnStation = cfg.Bind(S, "DefaultOnStation", p.DefaultOnStation, "Default number of birds on station (N)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 1, Max = 50 });
            p.DefaultOnStation = Math.Max(1, Math.Min(50, sDefaultOnStation.Value));
            CapOrderPanel.DefaultN = p.DefaultOnStation; CapOrderPanel.DefaultAltFt = p.PatrolAltFt; CapOrderPanel.DefaultMissilePct = p.MissilePct; CapOrderPanel.DefaultFuelMarginS = p.FuelMarginS; CapOrderPanel.DefaultFuelFloorPct = p.FuelFloorPct; CapOrderPanel.DefaultRingMarginNm = p.RingMarginNm;
            var sNeedLongRange = cfg.Bind(S, "NeedLongRange", p.NeedLongRange, "Leave when no medium or long-range missile is left", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.NeedLongRange = sNeedLongRange.Value;
            var sRoe = cfg.Bind(S, "Roe", p.Roe, "tight | free | hold: weapons status of the group on patrol", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "tight", "free", "hold" } });
            p.Roe = RoeOf(sRoe.Value);
            var sLoadoutTieBreak = cfg.Bind(S, "LoadoutTieBreak", p.LoadoutTieBreak, "Loadout names in the order of preference when scores are equal", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LoadoutTieBreak = sLoadoutTieBreak.Value;
            var sMission = cfg.Bind(S, "LaunchMissionType", p.LaunchMissionType, "none | cap: mission type of the launch order (none is the verified path)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LaunchMissionType = MissionOf(sMission.Value);
            var sRestore = cfg.Bind(S, "RestoreOnLoad", p.RestoreOnLoad, "auto | ask | off: restore the CAP groups after a save is loaded", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "ask", "off" } });
            p.RestoreOnLoad = RestoreOf(sRestore.Value);
            var sNoFuelDataOnStationS = cfg.Bind(S, "NoFuelDataOnStationS", p.NoFuelDataOnStationS, "A bird without fuel data is assumed to stay this many seconds in the air", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.NoFuelDataOnStationS = Math.Max(600, sNoFuelDataOnStationS.Value);
            var sDashOnMissileRtb = cfg.Bind(S, "DashOnMissileRtb", p.DashOnMissileRtb, "Fast return to the deck after the missiles are gone (the fastest throttle step that still leaves the fuel reserve)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.DashOnMissileRtb = sDashOnMissileRtb.Value;
            var sDashBurnFactor = cfg.Bind(S, "DashBurnFactor", p.DashBurnFactor, "Fuel burn per mile at the top step vs cruise when the game gives no data (1..6)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.DashBurnFactor = Math.Max(1, Math.Min(6, sDashBurnFactor.Value));
            // T-175 / ADR-0075: air-only CAP and the alert pair
            var sAirOnly = cfg.Bind(S, "AirOnly", true, "The CAP fights air targets only: a surface target does not postpone the return, its fire task is dropped, and the way home is on weapons hold", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.AirOnly = sAirOnly.Value;
            var sRtbHold = cfg.Bind(S, "RtbRoeHold", true, "Weapons hold while the bird flies home (the group's ROE comes back with the next sortie)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.RtbRoeHold = sRtbHold.Value;
            // T-179 / ADR-0079: the defaults of an "Alert" group (the window starts a new one from them)
            var sAlertCount = cfg.Bind(S, "AlertCount", 2, "Alert group: machines held ready (1..4)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 1, Max = 4 });
            p.AlertCount = Math.Max(1, Math.Min(4, sAlertCount.Value));
            var sAlertPerRaid = cfg.Bind(S, "AlertPerRaid", 2, "Alert group: machines launched for one raid (0 = every ready one)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 4 });
            p.AlertPerRaid = Math.Max(0, Math.Min(4, sAlertPerRaid.Value));
            var sAlertLevel = cfg.Bind(S, "AlertLevel", "deck", "Alert group: readiness - deck (prepared, launch held) | hangar (preparation starts with the raid)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "deck", "hangar" } });
            p.AlertLevel = Pick(sAlertLevel.Value, "deck", "deck", "hangar");
            var sAlertNm = cfg.Bind(S, "AlertNm", 80.0, "Alert group: reaction radius around the protected ship (nm)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 10, Max = 300, Step = 10, UnitKey = "nm" });
            p.AlertNm = Math.Max(10, sAlertNm.Value);
            var sAlertTargets = cfg.Bind(S, "AlertTargets", "air", "Alert group: targets, a list of air,helo,unknown", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.AlertTargets = TargetsOf(sAlertTargets.Value);
            var sAlertSpeed = cfg.Bind(S, "AlertSpeed", "max", "Alert group: speed to the raid - max (the fastest step the fuel allows) | cruise", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "max", "cruise" } });
            p.AlertSpeed = Pick(sAlertSpeed.Value, "max", "max", "cruise");
            var sAlertAfter = cfg.Bind(S, "AlertAfter", "patrol", "Alert group: after the raid - alert (stand ready again) | patrol (help a patrol that misses an aircraft)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "alert", "patrol" } });
            p.AlertAfter = Pick(sAlertAfter.Value, "patrol", "alert", "patrol");
            var sAlertFill = cfg.Bind(S, "AlertFill", false, "Alert group: take free machines from the deck when fewer than the ready number stand", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.AlertFill = sAlertFill.Value;
            var sAlertHandoverMinS = cfg.Bind(S, "AlertHandoverMinS", 600.0, "Help the patrol: the lent aircraft must stay at least this long in the zone above the fuel threshold (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.AlertHandoverMinS = Math.Max(0, sAlertHandoverMinS.Value);
            var sAcceptAlert = cfg.Bind(S, "AcceptAlert", true, "Patrol: accept alert aircraft into a missing place after a raid", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.AcceptAlert = sAcceptAlert.Value;
            // T-333 (ADR-0129): zone intrusion intercept (the defaults of a new patrol group; the window edits them per group) and the friendly-fire guard
            var sIntercept = cfg.Bind(S, "Intercept", p.Intercept, "Patrol: a hostile aircraft or helicopter in the zone (or within the margin) is intercepted by the patrol birds, then they return to their lanes", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            p.Intercept = sIntercept.Value;
            var sIcptMargin = cfg.Bind(S, "InterceptMarginNm", p.InterceptMarginNm, "Intercept: a target this many nm outside the zone already counts as an intrusion (0..50)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder, UnitKey = "nm" });
            p.InterceptMarginNm = Math.Max(0, Math.Min(50, sIcptMargin.Value));
            var sIcptPer = cfg.Bind(S, "InterceptPerTarget", p.InterceptPerTarget, "Intercept: birds given to one target, one for each target first (1..4)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.InterceptPerTarget = Math.Max(1, Math.Min(4, sIcptPer.Value));
            var sIcptKeep = cfg.Bind(S, "InterceptKeepOnStation", p.InterceptKeepOnStation, "Intercept: birds that always stay on their lanes (0..50)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.InterceptKeepOnStation = Math.Max(0, Math.Min(50, sIcptKeep.Value));
            var sIcptTargets = cfg.Bind(S, "InterceptTargets", p.InterceptTargets, "Intercept: targets, a list of air,helo,unknown", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.InterceptTargets = IcptTargetsOf(sIcptTargets.Value);
            var sIcptSpeed = cfg.Bind(S, "InterceptSpeed", p.InterceptSpeed, "Intercept: speed to the target - max (the fastest step the fuel allows) | cruise", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "max", "cruise" } });
            p.InterceptSpeed = Pick(sIcptSpeed.Value, "max", "max", "cruise");
            var sIcptLostS = cfg.Bind(S, "InterceptLostS", p.InterceptLostS, "Intercept: the bird goes back to its lane when the target track has been lost this long (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.InterceptLostS = Math.Max(5, Math.Min(600, sIcptLostS.Value));
            var sFfGuard = cfg.Bind(S, "FriendlyFireGuard", p.FriendlyFireGuard, "Patrol aircraft hold the auto launch of an IR / active-radar missile for up to 10 s when a friendly aircraft is within 2 nm of the target in the line of fire", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            p.FriendlyFireGuard = _ffOn = sFfGuard.Value;
            Action pushIcptDefaults = () => CapOrderPanel.DefaultIcptSpec = CapInterceptSpec.FromParams(p);
            OnChanged(sIntercept, v => { p.Intercept = v; pushIcptDefaults(); });
            OnChanged(sIcptMargin, v => { p.InterceptMarginNm = Math.Max(0, Math.Min(50, v)); pushIcptDefaults(); });
            OnChanged(sIcptPer, v => { p.InterceptPerTarget = Math.Max(1, Math.Min(4, v)); pushIcptDefaults(); });
            OnChanged(sIcptKeep, v => { p.InterceptKeepOnStation = Math.Max(0, Math.Min(50, v)); pushIcptDefaults(); });
            OnChanged(sIcptTargets, v => { p.InterceptTargets = IcptTargetsOf(v); pushIcptDefaults(); });
            OnChanged(sIcptSpeed, v => { p.InterceptSpeed = Pick(v, "max", "max", "cruise"); pushIcptDefaults(); });
            OnChanged(sIcptLostS, v => { p.InterceptLostS = Math.Max(5, Math.Min(600, v)); pushIcptDefaults(); if (_api.Cap != null) _api.Cap.SetInterceptLostS(p.InterceptLostS); });
            OnChanged(sFfGuard, v => { p.FriendlyFireGuard = _ffOn = v; });
            pushIcptDefaults();
            Action pushAlertDefaults = null;
            var sMaxZone = cfg.Bind(S, "MaxZone", 200.0, "The longest side of a CAP zone the map handles allow (nm, 50..400); the drawing of a new zone is not limited", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 50, Max = 400, Step = 50, UnitKey = "nm" });
            p.MaxZoneNm = Math.Max(50, Math.Min(400, sMaxZone.Value));
            OnChanged(sMaxZone, v => p.MaxZoneNm = Math.Max(50, Math.Min(400, v)));
            var sAlertPursuitNm = cfg.Bind(S, "AlertPursuitNm", 60.0, "Alert group: leash - farther than this from the protected ship (nm) the aircraft is called back", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.AlertPursuitNm = Math.Max(10, sAlertPursuitNm.Value);
            var sReorderGainS = cfg.Bind(S, "ReorderGainS", 60.0, "A prepared task of the mod is ordered again with another loadout only when the new one is ready sooner by more than this (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ReorderGainS = Math.Max(0, sReorderGainS.Value);
            // T-241 (ADR-0099): the search radar of a CAP aircraft is on while it flies its task; the previous state comes back after it
            var sAutoRadar = cfg.Bind(S, "AutoRadar", true, "Search radar of a CAP aircraft on while it flies its task (the previous state comes back after it, a player's change is kept)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            _capAutoRadar = sAutoRadar.Value;
            var sDebug = cfg.Bind(S, "Debug", false, "Log every CAP step and the hidden types", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.Debug = _capDebug = sDebug.Value;
            OnChanged(sMissilePct, v => p.MissilePct = Math.Max(0, Math.Min(1, v)));
            OnChanged(sFuelMarginS, v => p.FuelMarginS = Math.Max(0, v));
            OnChanged(sFuelFloorPct, v => p.FuelFloorPct = Math.Max(0, Math.Min(0.5, v)));
            OnChanged(sRingMarginNm, v => p.RingMarginNm = Math.Max(0, v));
            OnChanged(sCombatHostileNm, v => p.CombatHostileNm = Math.Max(0, v));
            OnChanged(sPursuitNm, v => p.PursuitNm = Math.Max(5, v));
            OnChanged(sThreatNm, v => p.ThreatNm = Math.Max(10, v));
            OnChanged(sThreatClearMarginNm, v => p.ThreatClearMarginNm = Math.Max(0, v));
            OnChanged(sThreatClearS, v => p.ThreatClearS = Math.Max(0, v));
            OnChanged(sLaunchDelayS, v => p.LaunchDelayS = Math.Max(0, v));
            OnChanged(sPatrolAltFt, v => p.PatrolAltFt = Math.Max(0, v));
            OnChanged(sLaneEndMaxNm, v => p.LaneEndMaxNm = Math.Max(0, v));
            OnChanged(sCoverAamFrac, v => p.CoverAamFrac = Math.Max(0.1, Math.Min(1, v)));
            OnChanged(sCoverFallbackNm, v => p.CoverFallbackNm = Math.Max(1, Math.Min(200, v)));
            OnChanged(sCircleAltStepFt, v => p.CircleAltStepFt = Math.Max(0, Math.Min(5000, v)));
            OnChanged(sOrbitBelowNm, v => p.OrbitBelowNm = Math.Max(0, v));
            OnChanged(sDefaultOnStation, v => { p.DefaultOnStation = Math.Max(1, Math.Min(50, v)); CapOrderPanel.DefaultN = p.DefaultOnStation; });
            OnChanged(sNeedLongRange, v => p.NeedLongRange = v);
            OnChanged(sLoadoutTieBreak, v => p.LoadoutTieBreak = v);
            OnChanged(sNoFuelDataOnStationS, v => p.NoFuelDataOnStationS = Math.Max(600, v));
            OnChanged(sDashOnMissileRtb, v => p.DashOnMissileRtb = v);
            OnChanged(sDashBurnFactor, v => p.DashBurnFactor = Math.Max(1, Math.Min(6, v)));
            OnChanged(sAirOnly, v => p.AirOnly = v);
            OnChanged(sRtbHold, v => p.RtbRoeHold = v);
            pushAlertDefaults = () => { CapOrderPanel.DefaultAlertSpec = CapAlertSpec.FromParams(p); CapOrderPanel.DefaultAcceptAlert = p.AcceptAlert; };
            OnChanged(sAlertCount, v => { p.AlertCount = Math.Max(1, Math.Min(4, v)); pushAlertDefaults(); });
            OnChanged(sAlertPerRaid, v => { p.AlertPerRaid = Math.Max(0, Math.Min(4, v)); pushAlertDefaults(); });
            OnChanged(sAlertLevel, v => { p.AlertLevel = Pick(v, "deck", "deck", "hangar"); pushAlertDefaults(); });
            OnChanged(sAlertNm, v => { p.AlertNm = Math.Max(10, v); pushAlertDefaults(); });
            OnChanged(sAlertTargets, v => { p.AlertTargets = TargetsOf(v); pushAlertDefaults(); });
            OnChanged(sAlertSpeed, v => { p.AlertSpeed = Pick(v, "max", "max", "cruise"); pushAlertDefaults(); });
            OnChanged(sAlertAfter, v => { p.AlertAfter = Pick(v, "patrol", "alert", "patrol"); pushAlertDefaults(); });
            OnChanged(sAlertFill, v => { p.AlertFill = v; pushAlertDefaults(); });
            OnChanged(sAlertHandoverMinS, v => p.AlertHandoverMinS = Math.Max(0, v));
            OnChanged(sAcceptAlert, v => { p.AcceptAlert = v; pushAlertDefaults(); });
            OnChanged(sAlertPursuitNm, v => { p.AlertPursuitNm = Math.Max(10, v); pushAlertDefaults(); });
            OnChanged(sReorderGainS, v => p.ReorderGainS = Math.Max(0, v));
            OnChanged(sDebug, v => p.Debug = _capDebug = v);
            OnChanged(sAutoRadar, v => { _capAutoRadar = v; if (_api.Cap != null) _api.Cap.AutoRadar = v; });   // T-241
            OnChanged(sHotkey, v => { _capHotkey = v; if (_hotkey != null) _hotkey.Respec(v); });
            OnChanged(sRoe, v => { p.Roe = RoeOf(v); pushAlertDefaults(); });
            OnChanged(sMission, v => p.LaunchMissionType = MissionOf(v));
            OnChanged(sRestore, v => p.RestoreOnLoad = RestoreOf(v));
            pushAlertDefaults();
            _capP = p;
        }

        private static string Pick(string v, string dflt, params string[] allowed)
        {
            string t = (v ?? dflt).Trim().ToLowerInvariant();
            return Array.IndexOf(allowed, t) >= 0 ? t : dflt;
        }

        /// <summary>A list "air,helo,unknown" (any order / case); an empty or broken value gives "air".</summary>
        private static string TargetsOf(string v)
        {
            bool ok;
            var t = CapAlertMath.ParseTargets(v, out ok);
            return ok ? CapAlertMath.TargetsCsv(t) : "air";
        }


        /// <summary>A list "air,helo,unknown" for the intercept; an empty or broken value gives "air,helo".</summary>
        private static string IcptTargetsOf(string v)
        {
            bool ok;
            var t = CapAlertMath.ParseTargets(v, out ok);
            return ok ? CapAlertMath.TargetsCsv(t) : "air,helo";
        }

        private static string RoeOf(string v) { string t = (v ?? "tight").Trim().ToLowerInvariant(); return t == "free" || t == "hold" ? t : "tight"; }
        private static string MissionOf(string v) { return (v ?? "none").Trim().ToLowerInvariant() == "cap" ? "cap" : "none"; }
        private static string RestoreOf(string v) { string t = (v ?? "auto").Trim().ToLowerInvariant(); return t == "ask" || t == "off" ? t : "auto"; }

        private sealed class WindowService : ICapWindow
        {
            private readonly CapWindowGui _w;
            public WindowService(CapWindowGui w) { _w = w; }
            public bool Visible { get { return _w.Visible; } set { _w.Visible = value; } }
            public void Open() { _w.Visible = true; }
            public string Undo() { return _w.PressUndo(); }
        }

        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<CapFeature>(this);
            ctx.Release.Add(new ReleaseParticipant("cap", _api.CapRelease), ReleaseRegistry.OrderCap);
            try
            {
                if (!_capEnabled) { _api.Capabilities.Remove("cap"); return; }
                if (!_api.Capabilities.Contains("cap")) { ctx.Host.LogWarning("cap disabled: members of the game are missing"); return; }
                _api.CapSaveDir = ctx.DataDir("cap");
                _airOwn = new AirOwnership();
                var asw = _api.Asw;
                if (asw != null) { _airOwn.Register("asw", asw.GroupNameOf); asw.Ownership = _airOwn; }
                var cap = _api.CreateCapController(_capP, _airOwn);
                cap.AutoRadar = _capAutoRadar;   // T-241
                // T-333 (ADR-0129 section 9): the friendly-fire guard rides on the fire_veto gateway (Common/Veto.cs); the subscription is harmless without the patch
                _ffGuard = new CapFfGuard(_api.NewFfGame(), uid => cap.IsCapAir(uid), () => _ffOn, m => ctx.Host.LogInfo(m), (n, j) => _api.CapEvent(n, j));
                cap.FfGuard = _ffGuard;
                _api.EngageVetos.Set(EngageVetoChain.NameCapFf, EngageVetoChain.OrderCapFf, _ffGuard.Veto);   // T-341: the chain owns the veto slot
                var radar = ctx.Get<IRadarView>();
                IAswMap map = new AswMapProvider(radar == null ? null : radar.Overlay);
                Func<GeoPt?> refPoint = () => CapRefPoint(cap);
                Func<int, GeoPt?> unitPos = uid => _api.CapUnitPos(uid);
                _capWin = new CapWindowGui(() => cap.View(), (name, cmd) => _api.CapRaw(name, cmd), map, b => _api.SetCapBlock(b), m => ctx.Host.LogInfo("[Cap] " + m),
                                           refPoint, () => _api.CapSelectedUid(), type => _api.CapLoadoutNames(type), accept => cap.ResolveRestore(accept), uid => _api.AirDeckName(uid), b => _api.SetMouseBlock("cap.draw", b),
                                           (type, name) => _api.CapLoadoutDisplay(type, name), (type, name) => _api.CapLoadoutReadyMin(type, name));
                _capWin.ShipCourseOf = uid => _api.CapShipCourse(uid);
                _capWin.OwnShipsFn = () => _api.Asw == null ? null : _api.Asw.OwnShipTargets();   // T-179: the ships an alert group can protect
                _capWin.FocusUnit = uid => { string se; string code = _api.FocusOwnUnit(uid, "cap", out se); if (se != null) ctx.Host.LogInfo("[Cap] focus uid=" + uid + " select=" + se); return code; };
                _capOv = new CapOverlay(() => cap.ViewLight(), map, refPoint, unitPos, m => ctx.Host.LogInfo("[Cap] " + m));
                _capOv.DraftZone = () => _capWin == null ? null : _capWin.DraftZone;
                _capOv.SelectedGroup = () => CapAlertMath.CircleGroup(_capWin == null ? null : _capWin.SelectedGroup, ref _lastCircleGroup, _api.Frame.InMission);   // T-179 / T-197: the circles of the selected alert group, the last one while the window is closed
                ctx.Mission.Subscribe(65, "cap_circles", e => _lastCircleGroup = null, why => _lastCircleGroup = null);   // T-201: a direct mission switch has no non-mission frame; the lifecycle sees it
                _capOv.ExtraLabels = ctx.Get<IContactInfo>() as IMapExtraLabels;   // group names on the zones also in the Noesis path
                var capOv = _capOv;
                ctx.Mission.Subscribe(66, "cap_labels", e => capOv.ClearLabels(), why => capOv.ClearLabels());   // T-335 review: contact_info (65) empties the extra labels on entry and leave; the next Tick publishes again
                var service = new WindowService(_capWin);
                _api.CapWindow = service;
                ctx.Provide<ICapWindow>(service);
                _input = new UnityKeyInput(_api);
                _hotkey = new HotkeyBinding("cap", "Cap.Hotkey", _capHotkey, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
                WireLayer(radar);
            }
            catch (Exception ex)
            {
                ctx.Host.LogError("cap init crashed: " + ex);
                if (_api.Asw != null) _api.Asw.Ownership = null;
                _api.Cap = null; _api.CapWindow = null; _capWin = null; _capOv = null; _api.Capabilities.Remove("cap");
            }
        }

        // the CAP geometry is drawn into the radar layer's render texture while the Noesis layer is active
        private void WireLayer(IRadarView radar)
        {
            try
            {
                if (_capOv == null) return;
                _capOv.NoesisActive = () => radar != null && radar.Layer != null && radar.Layer.Active;
                if (radar != null && radar.Layer != null) radar.Layer.Cap = _capOv;
            }
            catch (Exception e) { Ctx.Host.LogWarning("cap layer wiring failed: " + e.Message); }
        }

        // projection reference of the CAP window: centre of the first ordered zone, else the selected (or last own) unit
        private GeoPt? CapRefPoint(CapController cap)
        {
            try
            {
                var v = cap.ViewLight();
                foreach (var g in v.Groups)
                    if (g.Ordered && g.Rect != null && g.Rect.Length >= 4) return new GeoPt((g.Rect[0] + g.Rect[2]) * 0.5, GeoLon.MidLon(g.Rect[1], g.Rect[3]));
                int uid = _api.CapSelectedUid();
                return uid != 0 ? _api.CapUnitPos(uid) : null;
            }
            catch { return null; }
        }


        /// <summary>Pump, window and hotkey; called once per frame by the air_tick module.</summary>
        public void Tick()
        {
            if (_api.Cap == null) return;
            _api.PumpCap();
            if (!_ffChecked && _api.Frame.InMission)
            {
                _ffChecked = true;   // the patch is installed by the harmony module; checked once the mission runs
                if (!_api.PatchesApplied.Contains(ModManifest.PatchFireVeto)) Ctx.Host.LogInfo("[Cap] ff guard unavailable why=no_fire_veto");
            }
            if (_capWin != null) { _capWin.MaxZoneNm = _capP.MaxZoneNm; _capWin.Update(); }
            if (_capOv != null) _capOv.Tick();
            if (!_api.Frame.InMission && _capWin != null) _capWin.ResetUndo();   // T-178 C7: a new mission has no snapshot
            if (_hotkey == null || _hotkey.State == "off" || !_api.Frame.InMission) return;
            _hotkey.Resolve(Ctx.Host.RealTime, true, new GameKeys(_api));
            if (_hotkey.Pressed(_input) && _capWin != null) _capWin.Toggle();
        }

        public override void OnGUI()
        {
            if (_capOv != null) _capOv.OnGUI();
            if (_capWin != null) _capWin.OnGUI();
        }

        public override void Dispose()
        {
            try { _api.EngageVetos.Remove("cap_ff"); if (_ffGuard != null) _ffGuard.Book.Clear(); } catch { }   // T-333 (T-341: leaves the chain)
            try { if (_capWin != null) { _capWin.Dispose(); _api.SetCapBlock(false); } } catch { }
            try { if (_capOv != null) _capOv.ClearLabels(); } catch { }
        }
    }
}
