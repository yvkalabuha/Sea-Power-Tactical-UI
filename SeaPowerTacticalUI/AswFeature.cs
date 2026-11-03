using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-095 / ADR-0036: the ASW window (config section Asw): controller, window, overlay, hotkey and the "Return control" participant.</summary>
    internal sealed class AswFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private AswConfig _aswc;
        private AswParams _aswp;
        private AswWindow _aswWin;
        private AswOverlay _aswOv;
        private bool _forgotNoise;
        private AswMapProvider _provider;
        private HotkeyBinding _hotkey;
        private IKeyInput _input;
        private IUiToasts _toasts;                                       // T-173: the shared card stack (T-175); null = no cards
        private readonly AswToastFeed _feed = new AswToastFeed();
        private readonly AswRetreatToastFeed _retreatFeed = new AswRetreatToastFeed();   // T-180

        public AswFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "asw"; } }
        public int SettingsOrder { get { return 40; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(AswSpecs.Set); }
        public override string[] DependsOn { get { return new[] { "radar", "sar", "toast" }; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Asw";
            var c = new AswConfig();
            var p = new AswParams();
            var sHotkey = cfg.Bind(S, "Hotkey", c.Hotkey, "Opens the ASW window: Unity KeyCode names joined by + (e.g. LeftAlt+U); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.Hotkey = sHotkey.Value;
            var sGameEstimateFactor = cfg.Bind(S, "GameEstimateFactor", 1.0, "R_eff = the game's passive range estimate x this factor (calibrate in the live run)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.GameEstimateFactor = sGameEstimateFactor.Value;
            var sIniFactor = cfg.Bind(S, "IniFactor", 0.5, "R_eff fallback = ini passive range x this factor", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.IniFactor = sIniFactor.Value;
            var sActiveFactor = cfg.Bind(S, "ActiveFactor", 0.7, "R_eff for active buoys = active range x this factor", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ActiveFactor = sActiveFactor.Value;
            var sAreaSizeNm = cfg.Bind(S, "AreaSizeNm", 10.0, "Default side of the search area (nm) for 'area' orders given as bearing + distance", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 5, Max = 60, Step = 5, UnitKey = "nm" });
            p.AreaSizeNm = AswPatternMath.Snap(Math.Max(5.0, Math.Min(60.0, sAreaSizeNm.Value)), AswPatternMath.AreaSizeList);   // T-176: a list in the window, the value sticks to the nearest item
            var sStandby = cfg.Bind(S, "Standby", 1, "Ready spare aircraft kept for the relief (0..3)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 3, Step = 1 });
            p.Standby = Math.Max(0, Math.Min(3, sStandby.Value));
            var sLoadouts = cfg.Bind(S, "Loadouts", c.Loadouts, "Loadout names that make an ASW sortie, in order of preference", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            c.Loadouts = sLoadouts.Value;
            var sRestoreOnLoad = cfg.Bind(S, "RestoreOnLoad", "auto", "auto | ask | off: restore the ASW groups after a save is loaded", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "ask", "off" } });
            c.RestoreOnLoad = sRestoreOnLoad.Value;
            p.LostAfterS = Math.Max(30.0, cfg.Bind(S, "LostAfterS", 300.0, "Seconds without contact before the contact is reported lost", UiKeyClass.Advanced).Value);
            var sHazardNm = cfg.Bind(S, "HazardNm", 5.0, "Sonobuoys are not laid closer than this (nm) to a known hostile surface or ground object whose class is not identified; for an identified class its air defence reach is used", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.HazardNm = Math.Max(0.0, Math.Min(30.0, sHazardNm.Value));
            var sAxisMinKn = cfg.Bind(S, "AxisMinKn", 5.0, "Below this speed (kn) the search axis is the ship's heading, not the noisy track", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.AxisMinKn = Math.Max(0.0, sAxisMinKn.Value);
            var sAxisKeepDeg = cfg.Bind(S, "AxisKeepDeg", 15.0, "The search axis changes only when the new one differs by more than this (degrees)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.AxisKeepDeg = Math.Max(0.0, sAxisKeepDeg.Value);
            var sReplanMinS = cfg.Bind(S, "ReplanMinS", 15.0, "By-course order: the route is laid again no more often than every this many seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ReplanMinS = Math.Max(0.0, sReplanMinS.Value);
            var sAdoptGapS = cfg.Bind(S, "AdoptGapS", 300.0, "Seconds a launched deck aircraft may be missing from the lists before the group gives it up", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.AdoptGapS = Math.Max(30.0, sAdoptGapS.Value);
            // T-241 (ADR-0099): the search radar of an ASW aircraft (helicopter or plane) is on while it searches; the previous state comes back after it
            var sAutoRadar = cfg.Bind(S, "AutoRadar", true, "Search radar of an ASW aircraft on while it searches (the previous state comes back after it, a player's change is kept)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.AutoRadar = sAutoRadar.Value;
            var sDebug = cfg.Bind(S, "Debug", false, "Log R_eff per sensor, group plans and every ASW step", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.Debug = sDebug.Value;
            OnChanged(sHotkey, v => { c.Hotkey = v; if (_hotkey != null) _hotkey.Respec(v); });
            OnChanged(sGameEstimateFactor, v => p.GameEstimateFactor = v);
            OnChanged(sIniFactor, v => p.IniFactor = v);
            OnChanged(sActiveFactor, v => p.ActiveFactor = v);
            OnChanged(sAreaSizeNm, v => p.AreaSizeNm = AswPatternMath.Snap(Math.Max(5.0, Math.Min(60.0, v)), AswPatternMath.AreaSizeList));
            OnChanged(sStandby, v => p.Standby = Math.Max(0, Math.Min(3, v)));
            OnChanged(sLoadouts, v => { c.Loadouts = v; p.Loadouts = v; });   // the controller copied the list to its defaults once
            OnChanged(sRestoreOnLoad, v => c.RestoreOnLoad = v);
            OnChanged(sHazardNm, v => p.HazardNm = Math.Max(0.0, Math.Min(30.0, v)));
            OnChanged(sAxisMinKn, v => p.AxisMinKn = Math.Max(0.0, v));
            OnChanged(sAxisKeepDeg, v => p.AxisKeepDeg = Math.Max(0.0, v));
            OnChanged(sReplanMinS, v => p.ReplanMinS = Math.Max(0.0, v));
            OnChanged(sAdoptGapS, v => p.AdoptGapS = Math.Max(30.0, v));
            OnChanged(sDebug, v => { c.Debug = v; if (_provider != null) _provider.Debug = v; });
            OnChanged(sAutoRadar, v => c.AutoRadar = v);   // T-241
            BindPatterns(cfg, p);
            BindStrike(cfg, c);
            _aswc = c; _aswp = p;
        }

        private static readonly string[] CourseCombos = AswPatternMath.CourseCombos;

        private static string OneOf(string v, string[] list, string dflt) { return Array.IndexOf(list, v) >= 0 ? v : dflt; }

        /// <summary>
        /// T-176 (ADR-0076 s.9): the search patterns v3. The defaults are at once the start values of the window fields. Numbers that the window offers as lists
        /// (10..50, 20..100, the area sizes) stick to the nearest item when read. Pat is one object the controller reads live.
        /// </summary>
        private void BindPatterns(IUiConfig cfg, AswParams p)
        {
            const string S = "Asw";
            var pat = p.Pat;
            var sCourse = cfg.Bind(S, "CourseTactic", "auto", "Tactics of a by-course order: auto (by the group) | dip | corridor | barrier | dip+corridor | dip+barrier (corridor and barrier exclude each other)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "dip", "corridor", "barrier", "dip+corridor", "dip+barrier" } });
            pat.CourseTactic = OneOf(AswPatternMath.NormCombo(sCourse.Value), AswPatternMath.CourseCombos, "auto");   // T-334: an old value with both tactics works as barrier
            var sArea = cfg.Bind(S, "AreaTactic", "auto", "Pattern of an area order: auto | grid (buoy lattice) | dip | mad", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "grid", "dip", "mad" } });
            pat.AreaTactic = OneOf(sArea.Value, AswPatternMath.AreaPatterns, "auto");
            var sCw = cfg.Bind(S, "CorridorWidthNm", 20.0, "Width X of the corridor, nm (10..50 step 10): rows on both sides of the course", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 10, Max = 50, Step = 10, UnitKey = "nm" });
            pat.CorridorWidthNm = AswPatternMath.Snap(sCw.Value, AswPatternMath.WidthList);
            var sBw = cfg.Bind(S, "BarrierWidthNm", 40.0, "Width X of the barrier line, nm (10..50 step 10)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 10, Max = 50, Step = 10, UnitKey = "nm" });
            pat.BarrierWidthNm = AswPatternMath.Snap(sBw.Value, AswPatternMath.WidthList);
            var sDs = cfg.Bind(S, "DipStandoffNm", 20.0, "Standoff of the dipping sonar chain ahead of the group, nm (20..100 step 10)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 20, Max = 100, Step = 10, UnitKey = "nm" });
            pat.DipStandoffNm = AswPatternMath.Snap(sDs.Value, AswPatternMath.StandoffList);
            var sCs = cfg.Bind(S, "CorridorStandoffNm", 20.0, "Standoff of the corridor ahead of the group, nm (20..100 step 10)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 20, Max = 100, Step = 10, UnitKey = "nm" });
            pat.CorridorStandoffNm = AswPatternMath.Snap(sCs.Value, AswPatternMath.StandoffList);
            var sBs = cfg.Bind(S, "BarrierStandoffNm", 40.0, "Standoff of the barrier line ahead of the group, nm (20..100 step 10); not used by the sliding barrier (T-229)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 20, Max = 100, Step = 10, UnitKey = "nm" });
            pat.BarrierStandoffNm = AswPatternMath.Snap(sBs.Value, AswPatternMath.StandoffList);
            var sDepth = cfg.Bind(S, "BuoyDepth", "auto", "Depth of the buoys: auto (below the layer for a corridor / barrier, alternating otherwise) | above | below | alternate", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "above", "below", "alternate" } });
            pat.BuoyDepth = OneOf(sDepth.Value, new[] { "auto", "above", "below", "alternate" }, "auto");
            var sWb = cfg.Bind(S, "MinWaterBuoyM", 30.0, "A buoy is not laid in water shallower than this, m (also 3 m + 0.3 of the sensor depth)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 200, Step = 10 });
            pat.MinWaterBuoyM = Math.Max(10.0, Math.Min(200.0, sWb.Value));
            var sWd = cfg.Bind(S, "MinWaterDipM", 30.0, "A dipping sonar is not lowered in water shallower than this, m (at least 15.2 m of the body + 15 m of cable)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 200, Step = 10 });
            pat.MinWaterDipM = Math.Max(10.0, Math.Min(200.0, sWd.Value));
            var sStep = cfg.Bind(S, "BarrierStepNm", 10.0, "obsolete (T-229): the sliding barrier adds a row when the ship reaches the nearest one", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 5, Max = 30, Step = 5, UnitKey = "nm" });
            pat.BarrierStepNm = Math.Max(5.0, Math.Min(30.0, sStep.Value));
            var sBn = cfg.Bind(S, "BarrierRows", 2, "Rows of the sliding barrier ahead of the group (1..5); the first stands one interval ahead, the next every interval", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 1, Max = 5, Step = 1 });
            pat.BarrierRows = Math.Max(1, Math.Min(5, sBn.Value));
            var sBg = cfg.Bind(S, "BarrierGapNm", 5.0, "Interval of the barrier rows, nm (3, 5, 8, 10)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 3, Max = 10, Step = 1, UnitKey = "nm" });
            pat.BarrierGapNm = AswPatternMath.Snap(sBg.Value, AswPatternMath.BarrierGapList);
            var sBst = cfg.Bind(S, "BarrierStagger", true, "Odd barrier rows are shifted half a step (their buoys stand between the buoys of the even rows)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            pat.BarrierStagger = sBst.Value;
            var sBrd = cfg.Bind(S, "BarrierResetDeg", 30.0, "The barrier is built anew when the ship's axis differs from the barrier's by this many degrees (and holds for BarrierResetHoldS)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 15, Max = 90, Step = 5 });
            pat.BarrierResetDeg = Math.Max(15.0, Math.Min(90.0, sBrd.Value));
            var sBrh = cfg.Bind(S, "BarrierResetHoldS", 60.0, "The turn must hold this long (s) before the barrier is built anew", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 30, Max = 600, Step = 30, UnitKey = "s" });
            pat.BarrierResetHoldS = Math.Max(30.0, sBrh.Value);
            var sAct = cfg.Bind(S, "BarrierActive", false, "An active buoy in the middle of every barrier line (it gives the line away to the submarine)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.NextOrder });
            pat.BarrierActive = sAct.Value;
            var sRows = cfg.Bind(S, "CorridorMaxRows", 6, "At most this many rows on each side of a corridor (more: the warning corridor_too_wide)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 1, Max = 8, Step = 1 });
            pat.CorridorMaxRows = Math.Max(1, Math.Min(8, sRows.Value));
            var sRd = cfg.Bind(S, "ReanchorDeg", 30.0, "The pattern is moved to the new course when the escort's course differs from it by this many degrees (and holds for ReanchorHoldS)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 90, Step = 5 });
            pat.ReanchorDeg = Math.Max(10.0, Math.Min(90.0, sRd.Value));
            var sRh = cfg.Bind(S, "ReanchorHoldS", 60.0, "The turn must hold this long (s) before the pattern is moved", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 600, Step = 10, UnitKey = "s" });
            pat.ReanchorHoldS = Math.Max(10.0, sRh.Value);
            var sRl = cfg.Bind(S, "ReseedLeadS", 600.0, "A node whose buoy goes out within this many seconds is laid again", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 3600, Step = 60, UnitKey = "s" });
            pat.ReseedLeadS = Math.Max(0.0, sRl.Value);
            OnChanged(sCourse, v => pat.CourseTactic = OneOf(AswPatternMath.NormCombo(v), AswPatternMath.CourseCombos, "auto"));
            OnChanged(sArea, v => pat.AreaTactic = OneOf(v, AswPatternMath.AreaPatterns, "auto"));
            OnChanged(sCw, v => pat.CorridorWidthNm = AswPatternMath.Snap(v, AswPatternMath.WidthList));
            OnChanged(sBw, v => pat.BarrierWidthNm = AswPatternMath.Snap(v, AswPatternMath.WidthList));
            OnChanged(sDs, v => pat.DipStandoffNm = AswPatternMath.Snap(v, AswPatternMath.StandoffList));
            OnChanged(sCs, v => pat.CorridorStandoffNm = AswPatternMath.Snap(v, AswPatternMath.StandoffList));
            OnChanged(sBs, v => pat.BarrierStandoffNm = AswPatternMath.Snap(v, AswPatternMath.StandoffList));
            OnChanged(sDepth, v => pat.BuoyDepth = OneOf(v, new[] { "auto", "above", "below", "alternate" }, "auto"));
            OnChanged(sWb, v => pat.MinWaterBuoyM = Math.Max(10.0, Math.Min(200.0, v)));
            OnChanged(sWd, v => pat.MinWaterDipM = Math.Max(10.0, Math.Min(200.0, v)));
            OnChanged(sStep, v => pat.BarrierStepNm = Math.Max(5.0, Math.Min(30.0, v)));
            OnChanged(sBn, v => pat.BarrierRows = Math.Max(1, Math.Min(5, v)));
            OnChanged(sBg, v => pat.BarrierGapNm = AswPatternMath.Snap(v, AswPatternMath.BarrierGapList));
            OnChanged(sBst, v => pat.BarrierStagger = v);
            OnChanged(sBrd, v => pat.BarrierResetDeg = Math.Max(15.0, Math.Min(90.0, v)));
            OnChanged(sBrh, v => pat.BarrierResetHoldS = Math.Max(30.0, v));
            OnChanged(sAct, v => pat.BarrierActive = v);
            OnChanged(sRows, v => pat.CorridorMaxRows = Math.Max(1, Math.Min(8, v)));
            OnChanged(sRd, v => pat.ReanchorDeg = Math.Max(10.0, Math.Min(90.0, v)));
            OnChanged(sRh, v => pat.ReanchorHoldS = Math.Max(10.0, v));
            OnChanged(sRl, v => pat.ReseedLeadS = Math.Max(0.0, v));
        }

        private static string StrikeTargetsOf(string v) { return v == "hostile" ? "hostile" : "hostile_unknown"; }

        /// <summary>T-173 (ADR-0074 item 9, ADR-0072): the strike group and the attack run. Live keys are read every tick (the config object is shared with the controller);
        /// the NextOrder keys are the defaults of the next strike order.</summary>
        private void BindStrike(IUiConfig cfg, AswConfig c)
        {
            const string S = "Asw";
            var st = c.Strike; var lc = c.Loc;
            var sStrikeTargets = cfg.Bind(S, "StrikeTargets", "hostile_unknown", "Strike group targets: hostile_unknown (hostile and unidentified submarines; an unidentified one may be neutral) | hostile (only hostile)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "hostile_unknown", "hostile" } });
            st.Targets = StrikeTargetsOf(sStrikeTargets.Value);
            var sOwnSubSafeNm = cfg.Bind(S, "OwnSubSafeNm", 4.0, "Hold fire when an own or allied submarine is closer to the target than this (nm)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 1, Max = 10, Step = 1, UnitKey = "nm" });
            st.OwnSubSafeNm = Math.Max(1.0, Math.Min(10.0, sOwnSubSafeNm.Value));
            var sMaxTorpedoes = cfg.Bind(S, "MaxTorpedoesPerContact", 0, "Torpedoes per target, at most (0 = until the target is destroyed)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 12, Step = 1 });
            st.MaxTorpedoes = Math.Max(0, Math.Min(12, sMaxTorpedoes.Value));
            var sStrikeRadiusNm = cfg.Bind(S, "StrikeRadiusNm", 0.0, "Zone radius of a new strike order around the guarded ship (nm; 0 = no limit)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 150, Step = 10, UnitKey = "nm" });
            st.RadiusNm = Math.Max(0.0, Math.Min(150.0, sStrikeRadiusNm.Value));
            var sStrikeAirborne = cfg.Bind(S, "StrikeAirborne", 0, "Aircraft of a new strike order kept in the air while there is no target (0 = all wait on deck)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 4, Step = 1 });
            st.Airborne = Math.Max(0, Math.Min(4, sStrikeAirborne.Value));
            var sToast = cfg.Bind(S, "Toast", true, "Card \"Submarine detected\"", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            st.Toast = sToast.Value;
            var sToastS = cfg.Bind(S, "ToastS", 20.0, "The card hides after this many seconds (0 = until closed)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 120, Step = 5, UnitKey = "s" });
            st.ToastS = Math.Max(0.0, sToastS.Value);
            var sUnknownConfirmS = cfg.Bind(S, "UnknownConfirmS", 60.0, "An unidentified target must be tracked live for this many seconds before it is attacked", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.UnknownConfirmS = Math.Max(0.0, sUnknownConfirmS.Value);
            var sMissStreak = cfg.Bind(S, "MissStreak", 2, "Misses in a row after which the next drop needs a fresh fix", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.MissStreak = Math.Max(0, sMissStreak.Value);
            var sFreshFixS = cfg.Bind(S, "FreshFixS", 90.0, "A fix counts as fresh when it is not older than this (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.FreshFixS = Math.Max(1.0, sFreshFixS.Value);
            var sReattackGapS = cfg.Bind(S, "ReattackGapS", 20.0, "Pause after a torpedo ended before the next attack (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.ReattackGapS = Math.Max(0.0, sReattackGapS.Value);
            var sOwnSubClearS = cfg.Bind(S, "OwnSubClearS", 20.0, "An own submarine has gone: continue the attack after this many seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.OwnSubClearS = Math.Max(0.0, sOwnSubClearS.Value);
            var sStrikeMaxEtaS = cfg.Bind(S, "StrikeMaxEtaS", 1800.0, "A target is taken when the weapon can be in the water within this many seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.MaxEtaS = Math.Max(60.0, sStrikeMaxEtaS.Value);
            var sStrikeIdleS = cfg.Bind(S, "StrikeIdleS", 600.0, "An aircraft without a target lands after this many seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            st.IdleS = Math.Max(30.0, sStrikeIdleS.Value);
            var sDropNm = cfg.Bind(S, "DropNm", 1.0, "The torpedo is dropped from this distance to the target estimate (nm; kept inside the weapon's launch range)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.DropNm = Math.Max(0.1, Math.Min(5.0, sDropNm.Value));
            var sMadVerify = cfg.Bind(S, "MadVerify", false, "An aircraft with a MAD flies one pass over the datum before the first drop (off by default since T-316: a live track is enough; skipped while the game gives no MAD detection reading)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            lc.MadVerify = sMadVerify.Value;
            var sMadVerifyS = cfg.Bind(S, "MadVerifyS", 240.0, "The MAD pass takes at most this many game seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.MadVerifyS = Math.Max(30.0, sMadVerifyS.Value);
            var sMadPassFt = cfg.Bind(S, "MadPassFt", 100.0, "Altitude of the MAD pass (ft; at most 0.8 of the MAD limit)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.MadPassFt = Math.Max(30.0, sMadPassFt.Value);
            var sDepthCharges = cfg.Bind(S, "DepthCharges", "mad_only", "Depth charges: mad_only (only on a fresh MAD fix, when no torpedo aircraft is in the group) | off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "mad_only", "off" } });
            lc.DepthCharges = sDepthCharges.Value == "off" ? "off" : "mad_only";
            var sApproachMaxS = cfg.Bind(S, "ApproachMaxS", 600.0, "A shooter that has not reached the drop point in this time (s) is changed", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.ApproachMaxS = Math.Max(60.0, sApproachMaxS.Value);
            var sDropTimeoutS = cfg.Bind(S, "DropTimeoutS", 180.0, "An accepted attack order must drop the torpedo within this time (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.DropTimeoutS = Math.Max(30.0, sDropTimeoutS.Value);
            var sSubSpookNm = cfg.Bind(S, "SubSpookNm", 2.0, "A submarine hears an active sonar closer than this (nm): the holder and the waiting aircraft keep outside", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.SubSpookNm = Math.Max(0.5, sSubSpookNm.Value);
            var sSpookMarginNm = cfg.Bind(S, "SpookMarginNm", 0.3, "Margin added to SubSpookNm (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.SpookMarginNm = Math.Max(0.0, sSpookMarginNm.Value);
            var sLostHoldS = cfg.Bind(S, "LostHoldS", 600.0, "A strike target that is not tracked is waited for this many seconds before it is given up", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            lc.LostHoldS = Math.Max(30.0, sLostHoldS.Value);
            OnChanged(sStrikeTargets, v => st.Targets = StrikeTargetsOf(v));
            OnChanged(sOwnSubSafeNm, v => st.OwnSubSafeNm = Math.Max(1.0, Math.Min(10.0, v)));
            OnChanged(sMaxTorpedoes, v => st.MaxTorpedoes = Math.Max(0, Math.Min(12, v)));
            OnChanged(sStrikeRadiusNm, v => st.RadiusNm = Math.Max(0.0, Math.Min(150.0, v)));
            OnChanged(sStrikeAirborne, v => st.Airborne = Math.Max(0, Math.Min(4, v)));
            OnChanged(sToast, v => st.Toast = v);
            OnChanged(sToastS, v => st.ToastS = Math.Max(0.0, v));
            OnChanged(sUnknownConfirmS, v => st.UnknownConfirmS = Math.Max(0.0, v));
            OnChanged(sMissStreak, v => st.MissStreak = Math.Max(0, v));
            OnChanged(sFreshFixS, v => st.FreshFixS = Math.Max(1.0, v));
            OnChanged(sReattackGapS, v => st.ReattackGapS = Math.Max(0.0, v));
            OnChanged(sOwnSubClearS, v => st.OwnSubClearS = Math.Max(0.0, v));
            OnChanged(sStrikeMaxEtaS, v => st.MaxEtaS = Math.Max(60.0, v));
            OnChanged(sStrikeIdleS, v => st.IdleS = Math.Max(30.0, v));
            OnChanged(sDropNm, v => lc.DropNm = Math.Max(0.1, Math.Min(5.0, v)));
            OnChanged(sMadVerify, v => lc.MadVerify = v);
            OnChanged(sMadVerifyS, v => lc.MadVerifyS = Math.Max(30.0, v));
            OnChanged(sMadPassFt, v => lc.MadPassFt = Math.Max(30.0, v));
            OnChanged(sDepthCharges, v => lc.DepthCharges = v == "off" ? "off" : "mad_only");
            OnChanged(sApproachMaxS, v => lc.ApproachMaxS = Math.Max(60.0, v));
            OnChanged(sDropTimeoutS, v => lc.DropTimeoutS = Math.Max(30.0, v));
            OnChanged(sSubSpookNm, v => lc.SubSpookNm = Math.Max(0.5, v));
            OnChanged(sSpookMarginNm, v => lc.SpookMarginNm = Math.Max(0.0, v));
            OnChanged(sLostHoldS, v => lc.LostHoldS = Math.Max(30.0, v));
        }


        private sealed class WindowService : IAswWindow
        {
            private readonly AswWindow _w;
            public WindowService(AswWindow w) { _w = w; }
            public bool Visible { get { return _w.Visible; } set { _w.Visible = value; } }
            public void Open() { _w.Visible = true; }
            public bool SelectByTarget(string name) { return _w.SelectByTarget(name); }
            public string Undo() { return _w.Body.PressUndo(); }
            public void OpenAreaDraft(double[] rect) { _w.Visible = true; _w.Body.DraftArea(rect); }
        }

        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<AswFeature>(this);
            ctx.Release.Add(new ReleaseParticipant("asw", uid => { if (_api.Asw != null) _api.Asw.ReleaseUid(uid); }), ReleaseRegistry.OrderAsw);
            try
            {
                if (!_api.Capabilities.Contains("asw_info")) { ctx.Host.LogWarning("asw disabled: members of the game are missing"); return; }
                _api.AswSaveDir = ctx.DataDir("asw");
                var asw = _api.CreateAswController(_aswp, _aswc);
                _toasts = ctx.Get<IUiToasts>();
                var radar = ctx.Get<IRadarView>();
                var provider = _provider = new AswMapProvider(radar == null ? null : radar.Overlay) { Debug = _aswc.Debug, Log = m => ctx.Host.LogInfo("[Asw] " + m) };
                IAswMap map = provider;
                _aswWin = new AswWindow(() => asw.View(), (name, cmd) => _api.AswCmd("0", name, cmd), map, b => _api.SetAswBlock(b), m => ctx.Host.LogInfo(m));
                // T-324: a click on a list row selects the unit and centres the map (GameApi.FocusOwnUnit)
                _aswWin.FocusUnit = uid => { string se; string code = _api.FocusOwnUnit(uid, "asw", out se); if (se != null) ctx.Host.LogInfo("[Asw] focus uid=" + uid + " select=" + se); return code; };
                _aswWin.Body.ContactInfo = () => _api.ContactInfo;   // T-136 E3: contact lines and own noise in the window (null = the module is off)
                _aswWin.Body.SarShips = () => { var ss = ctx.Get<ISarShips>(); return ss == null ? null : ss.Rows(); };   // T-161: ships / subs of the SAR tab (null = SAR is off)
                _aswOv = new AswOverlay(() => asw.View(), map, m => ctx.Host.LogInfo(m));
                _aswOv.AreaRoute = AswDrawPlan.AreaRouteOn;   // T-174 W5
                _aswOv.CourseRoute = AswDrawPlan.CourseRouteOn;   // T-176 W6
                _api.AswLogSink = m => ctx.Host.LogInfo(m);
                _api.Capabilities.Add("asw");
                var service = new WindowService(_aswWin);
                _api.AswWindow = service;
                ctx.Provide<IAswWindow>(service);
                _input = new UnityKeyInput(_api);
                _hotkey = new HotkeyBinding("asw", "Asw.Hotkey", _aswc.Hotkey, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
                if (_api.Sar != null) asw.SarOptions = ctx.Get<SarParams>();   // the SAR options (ship rescue runs from the ASW groups)
                WireLayer(radar);
            }
            catch (Exception ex) { ctx.Host.LogError("asw init crashed: " + ex); _api.Asw = null; _api.AswWindow = null; _aswWin = null; _aswOv = null; _api.Capabilities.Remove("asw"); }
        }

        // the ASW geometry is drawn into the radar layer's render texture while the Noesis layer is active
        private void WireLayer(IRadarView radar)
        {
            try
            {
                if (_aswOv == null) return;
                _aswOv.NoesisActive = () => radar != null && radar.Layer != null && radar.Layer.Active;
                if (radar != null && radar.Layer != null) radar.Layer.Asw = _aswOv;
            }
            catch (Exception e) { Ctx.Host.LogWarning("asw layer wiring failed: " + e.Message); }
        }

        /// <summary>Window and hotkey (the mission change comes from MissionLifecycle); called once per frame by the air_tick module (the deck order of the frame lives there).</summary>
        public void Tick()
        {
            if (_api.Asw == null) return;
            if (!_forgotNoise && UiWindowPrefs.State != null) { _forgotNoise = true; UiWindowPrefs.State.ForgetSection("asw.noise"); }   // T-174 W7: the section is gone, its saved state goes too
            if (_aswWin != null)   // T-178 A4: a new mission has no snapshot to give back
            {
                if (!_api.Frame.InMission) { if (!_undoDropped) { _undoDropped = true; _aswWin.Body.ResetUndo(); } }
                else _undoDropped = false;
            }
            _api.Asw.ViewWanted = _aswWin != null && _aswWin.Visible;   // T-126: a closed window and no work = no game scan
            if (_aswWin != null) _aswWin.Update();
            try { if (_toasts != null) _feed.Apply(_api.Asw.View(), _toasts, _aswc.Strike, Ctx.Host.RealTime, FocusAlert); }   // T-173: the cards "submarine detected"
            catch (Exception e) { if (!_toastErr) { _toastErr = true; Ctx.Host.LogWarning("asw cards failed (once): " + e.Message); } }
            try { if (_toasts != null) { var ag = Ctx.Get<IAirThreats>(); _retreatFeed.Apply(_api.Asw.View(), _toasts, ag != null && ag.P.Toast, ag != null ? Math.Min(ag.P.ToastS, 10) : 8, Ctx.Host.RealTime); } }   // T-180: the retreat / return notice
            catch (Exception e) { if (!_toastErr) { _toastErr = true; Ctx.Host.LogWarning("asw retreat cards failed (once): " + e.Message); } }
            if (_hotkey == null || _hotkey.State == "off" || !_api.Frame.InMission) return;
            _hotkey.Resolve(Ctx.Host.RealTime, true, new GameKeys(_api));
            if (_hotkey.Pressed(_input) && _aswWin != null) _aswWin.Toggle();
        }

        private bool _toastErr, _undoDropped;

        /// <summary>"Show" on a card: the map is centred on the track (the same helper as the window rows).</summary>
        private void FocusAlert(AswSubAlertView a)
        {
            try { string err; _api.CenterMap(a.ContactId, null, null, null, "asw", out err); } catch { }
        }

        public override void OnGUI()
        {
            if (_aswOv != null) _aswOv.OnGUI();
            if (_aswWin != null) _aswWin.OnGUI();
        }

        public override void Dispose()
        {
            try { if (_aswWin != null) { _aswWin.Dispose(); _api.SetAswBlock(false); } } catch { }
        }
    }
}
