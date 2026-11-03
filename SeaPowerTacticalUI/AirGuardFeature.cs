using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-175 / ADR-0075: the air threat service (config section AirGuard). The service decides the stages; the owners of the aircraft (ASW, SAR, CAP) apply them.
    /// The tick comes from the air tick (AirTickFeature) before the ASW / CAP controllers. The wiring to the owners, x1 and the cards is made in Init.</summary>
    internal sealed class AirGuardFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private readonly AirGuardParams _p = new AirGuardParams();
        private AirGuardController _ctl;

        public AirGuardFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "air_guard"; } }
        public override string[] DependsOn { get { return new[] { "asw", "cap", "toast" }; } }
        public int SettingsOrder { get { return 65; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(AirGuardSpecs.Set); check.Specs(AirTimeSpecs.Set); check.Specs(AirLossSpecs.Set); }

        private static string Pick(string v, string dflt, params string[] allowed)
        {
            string t = (v ?? dflt).Trim().ToLowerInvariant();
            foreach (string a in allowed) if (a == t) return t;
            return dflt;
        }

        private static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "AirGuard";
            var mode = cfg.Bind(S, "Mode", "retreat", "retreat | warn | off: air asset protection - own ASW / SAR aircraft retreat under the cover of a ship with SAMs when a hostile aircraft comes close; warn = only the warning; off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "retreat", "warn", "off" } });
            var warn = cfg.Bind(S, "WarnNm", 60.0, "Warn when an air threat is closer than this (nm), aircraft", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 200, Step = 5, UnitKey = "nm" });
            var warnH = cfg.Bind(S, "WarnNmHelo", 80.0, "Warn when an air threat is closer than this (nm), helicopters", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 200, Step = 5, UnitKey = "nm" });
            var ret = cfg.Bind(S, "RetreatNm", 40.0, "Retreat when an air threat is closer than this (nm), aircraft", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 5, Max = 150, Step = 5, UnitKey = "nm" });
            var retH = cfg.Bind(S, "RetreatNmHelo", 50.0, "Retreat when an air threat is closer than this (nm), helicopters", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 5, Max = 150, Step = 5, UnitKey = "nm" });
            var eta = cfg.Bind(S, "RetreatEtaS", 360.0, "Retreat when the threat reaches its weapon range of the aircraft in less than this (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var etaH = cfg.Bind(S, "RetreatEtaSHelo", 480.0, "The same for helicopters (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var unk = cfg.Bind(S, "Unknown", "warn", "off | warn | retreat: unidentified fast air tracks (Unknown posture, speed at or above UnknownMinKn)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "off", "warn", "retreat" } });
            var tc = cfg.Bind(S, "TimeX1", "all", "all | stage2 | off: reset the time acceleration to x1 on danger (all = new raid, retreat, missile, loss; stage2 = retreat, missile, loss)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "all", "stage2", "off" } });
            var ungrouped = cfg.Bind(S, "WarnUngrouped", true, "Warn about aircraft outside the mod's groups as well (warning only, they are not moved)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var toast = cfg.Bind(S, "Toast", true, "Cards: air threat and aircraft lost", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var toastS = cfg.Bind(S, "ToastS", 20.0, "The cards hide after this many seconds (0 = until closed)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 120, Step = 5, UnitKey = "s" });
            var defR = cfg.Bind(S, "DefaultRadiusNm", 10.0, "Weapon range assumed for an unidentified track or a class with no data (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var minR = cfg.Bind(S, "MinRadiusNm", 3.0, "Lower limit of the assumed weapon range (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var unkKn = cfg.Bind(S, "UnknownMinKn", 350.0, "An unidentified track is a threat only at or above this speed (kn)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var ghost = cfg.Bind(S, "GhostS", 180.0, "A lost track stays as a dead-reckoned ghost this long (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var clear = cfg.Bind(S, "ClearS", 60.0, "The stage is lifted only after the threats stayed beyond the thresholds this long (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var minSam = cfg.Bind(S, "MinSamNm", 10.0, "A ship is a cover candidate only with an AAW ring of at least this range (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var unload = cfg.Bind(S, "UnloadExtraS", 120.0, "A SAR helicopter with people lands instead of covering when the deck is at most this much later (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var ignore = cfg.Bind(S, "IgnoreS", 600.0, "Continue tasks: the group ignores the current tracks this long (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var gap = cfg.Bind(S, "TcMinGapS", 30.0, "Not more often than this (real s) the time acceleration is reset because of a stage", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            _api.AgParams = _p;   // the Harmony feature reads the mode before the service is created (the hit hook for the loss card)
            Apply(_p, mode.Value, warn.Value, warnH.Value, ret.Value, retH.Value, eta.Value, etaH.Value, unk.Value, tc.Value, ungrouped.Value, toast.Value, toastS.Value,
                  defR.Value, minR.Value, unkKn.Value, ghost.Value, clear.Value, minSam.Value, unload.Value, ignore.Value, gap.Value);
            Action any = () => Apply(_p, mode.Value, warn.Value, warnH.Value, ret.Value, retH.Value, eta.Value, etaH.Value, unk.Value, tc.Value, ungrouped.Value, toast.Value, toastS.Value,
                  defR.Value, minR.Value, unkKn.Value, ghost.Value, clear.Value, minSam.Value, unload.Value, ignore.Value, gap.Value);
            OnChanged(mode, v => any()); OnChanged(warn, v => any()); OnChanged(warnH, v => any()); OnChanged(ret, v => any()); OnChanged(retH, v => any());
            OnChanged(eta, v => any()); OnChanged(etaH, v => any()); OnChanged(unk, v => any()); OnChanged(tc, v => any()); OnChanged(ungrouped, v => any());
            OnChanged(toast, v => any()); OnChanged(toastS, v => any()); OnChanged(defR, v => any()); OnChanged(minR, v => any()); OnChanged(unkKn, v => any());
            OnChanged(ghost, v => any()); OnChanged(clear, v => any()); OnChanged(minSam, v => any()); OnChanged(unload, v => any()); OnChanged(ignore, v => any()); OnChanged(gap, v => any());
        }

        private static void Apply(AirGuardParams p, string mode, double warn, double warnH, double ret, double retH, double eta, double etaH, string unk, string tc, bool ungrouped, bool toast, double toastS,
                                  double defR, double minR, double unkKn, double ghost, double clear, double minSam, double unload, double ignore, double gap)
        {
            p.Mode = Pick(mode, "retreat", "retreat", "warn", "off");
            p.WarnNm = Clamp(warn, 10, 200); p.WarnNmHelo = Clamp(warnH, 10, 200);
            p.RetreatNm = Clamp(ret, 5, 150); p.RetreatNmHelo = Clamp(retH, 5, 150);
            p.RetreatEtaS = Math.Max(0, eta); p.RetreatEtaSHelo = Math.Max(0, etaH);
            p.Unknown = Pick(unk, "warn", "off", "warn", "retreat");
            p.TimeX1 = Pick(tc, "all", "all", "stage2", "off");
            p.WarnUngrouped = ungrouped; p.Toast = toast; p.ToastS = Math.Max(0, toastS);
            p.DefaultRadiusNm = Math.Max(1, defR); p.MinRadiusNm = Math.Max(0, minR); p.UnknownMinKn = Math.Max(0, unkKn);
            p.GhostS = Math.Max(0, ghost); p.ClearS = Math.Max(0, clear); p.MinSamNm = Math.Max(0, minSam);
            p.UnloadExtraS = Math.Max(0, unload); p.IgnoreS = Math.Max(0, ignore); p.TcMinGapS = Math.Max(0, gap);
        }

        private void Restart()
        {
            if (_ctl != null) _ctl.Reset();
            if (_x1 != null) _x1.Reset();
            if (_ctl != null && _api.Cap != null) _api.Cap.AlertNotes = _ctl;
        }

        /// <summary>"Show": the map goes to the raid (its centre).</summary>
        private void ShowRaid(AirThreatCard c)
        {
            string err;
            try { _api.CenterMap(null, null, c.CenterLat, c.CenterLon, "air_guard", out err); } catch (Exception e) { Ctx.Host.LogWarning("air_guard show: " + e.Message); }
        }

        /// <summary>"Continue tasks": every group of the card ignores the current tracks (air_guard_ignore).</summary>
        private void ResumeTasks(AirThreatCard c)
        {
            if (_ctl == null) return;
            double now = _api.Frame.MissionTimeS ?? 0;
            foreach (var l in c.Groups) _ctl.Ignore(l.Window == "strike" ? "strike" : (l.Action == "scramble" || l.Action == "not_ready" || l.Action == "preparing" ? "cap" : "asw"), l.Group, now);   // T-179: a line of the CAP alert group belongs to the CAP window
        }

        private AirTimeX1 _x1;

        public override void Init(UiContext ctx)
        {
            try
            {
                var host = ctx.Host;
                _ctl = _api.CreateAirGuardController(_p);
                ctx.Provide<IAirThreats>(_ctl);
                ctx.Mission.Subscribe(68, "air_guard", e => Restart(), why => Restart());
                var toasts = ctx.Get<IUiToasts>();
                // ASW / SAR groups: the owner of the aircraft applies the stages (ADR-0075 item 2); the strike group (T-173) registers its own window the same way
                var asw = _api.Asw;
                if (asw != null)
                {
                    _ctl.RegisterGroups("asw", asw.AirGuardGroups);
                    asw.AirGuard = _ctl;
                    asw.AirSteer = new AirRetreatSteer(_api.AgSteerGame(), m => host.LogInfo("[AirGuard] " + m));
                }
                // T-341 phase D (plan 34.3): the jammer aircraft of the strike groups are watched like the ASW ones (window "strike", kind "ew"); the strikers are not
                var strike = _api.Strike;
                if (strike != null)
                {
                    _ctl.RegisterGroups("strike", () => _api.Strike == null ? null : _api.Strike.AirGuardGroups());
                    var strikeSteer = new AirRetreatSteer(_api.AgSteerGame(), m => host.LogInfo("[AirGuard] " + m));
                    strike.AirRetreatNow = (uid, group) => { var th = _ctl.ForUnit(uid); return th != null && th.Stage == AirStage.Retreat && _ctl.GroupStage("strike", group) == AirStage.Retreat; };
                    strike.AirThreatNow = (uid, group) => { var th = _ctl.ForUnit(uid); return th != null && th.Stage >= AirStage.Warn; };   // T-342: any stage restarts the clear time of the jammer's return
                    strike.AirThreatAtStation = (pt, group) => !_ctl.Ignored("strike", group) && _ctl.AtPoint(pt, false) != null;   // T-349
                    strike.AirSteerEw = (uid, reserve, max) => { var cov = _ctl.CoverFor(uid); if (cov == null) return "no_cover"; return strikeSteer.Steer(uid, cov, false, reserve, max, _api.Frame.MissionTimeS ?? 0); };
                    strike.AirReleaseEw = uid => strikeSteer.Release(uid);
                }
                // T-179 (ADR-0079 section 8): the CAP "Alert" groups; their reaction to a raid is a line of the card
                if (_api.Cap != null)
                {
                    _ctl.RegisterGroups("cap", () => _api.Cap == null ? null : _api.Cap.AirGuardGroups());
                    _api.Cap.AlertNotes = _ctl;
                    _api.Cap.UnknownMinKn = () => _p.UnknownMinKn;   // the "Unidentified" target of the alert group uses the same speed limit
                }
                // time x1 on danger (item 3): the reasons come from the service and from the loss tracker
                var tracker = _api.CreateAirLossTracker();
                _x1 = new AirTimeX1(_api.AgTimeCompression, _api.AgPausedOrMessage, _api.AgSetX1, () => host.RealTime, _p,
                    (n, j) => _api.AgEvent(n, j), m => host.LogInfo("[AirGuard] " + m), t => { if (!_api.RaiseGameAlert(t)) host.LogInfo("[AirGuard] " + t); });
                _ctl.TimeX1 = tracker.TimeX1 = _x1;
                // cards: a change of the stage and the resume re-show the card of the raid (ADR-0075 item 7)
                _ctl.CardChanged = card =>
                {
                    if (toasts == null || !_p.Toast || card == null) return;
                    if (!card.Resumed) card.TcLine = _x1 == null ? null : _x1.LastLine;
                    var c = card;
                    string key = "air_threat:" + (card.Resumed ? "resume:" + (card.Groups.Count > 0 ? card.Groups[0].Group : "") : card.RaidKey);
                    toasts.Show(key, "air_threat", k => AirThreatToastBody.Draw(k, c, () => ShowRaid(c), () => ResumeTasks(c)), host.RealTime, _p.ToastS, true);
                };
                tracker.Lost = info =>
                {
                    try { if (asw != null) asw.AirLost(info.Uid, info.Cause, info.Aboard); } catch (Exception e) { host.LogError("air lost handler: " + e.Message); }
                    if (toasts == null || !_p.Toast) return;
                    var i = info;
                    toasts.Show("air_lost:" + info.Uid, "air_lost", k => AirLossToastBody.Draw(k, i), host.RealTime, _p.ToastS, true);
                };
                ctx.Mission.Subscribe(67, "air_loss", e => { tracker.Reset(); _api.AlLeaveMission(); }, why => { tracker.Reset(); _api.AlLeaveMission(); });
            }
            catch (Exception e)
            {
                ctx.Host.LogError("air_guard init crashed: " + e);
                _ctl = null; _api.AirGuard = null;
            }
        }
    }
}
