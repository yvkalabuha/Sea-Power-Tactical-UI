using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-138 / ADR-0054: intercept course of a contact (config section Intercept): the entry of the contact context menu, the controller
    /// (one tick per frame), the line overlay and the "Return control" participant. Orders only goto / add_waypoint / set_telegraph.</summary>
    internal sealed class InterceptFeature : UiFeatureBase, IUiSettingsSection
    {
        private IcParams _p = new IcParams();
        private readonly GameApi _api;
        private InterceptController _ctl;
        private InterceptOverlay _ov;

        public InterceptFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override void SelfCheck(ISelfCheck check) { check.Specs(InterceptSpecs.Set); }
        public override string[] DependsOn { get { return new[] { "text", "selection", "contact_info", "menu", "smart_attack", "sar", "evasion" }; } }

        public override string Id { get { return "intercept"; } }
        public int SettingsOrder { get { return 115; } }

        /// <summary>The live parameters: the controller keeps this very object and reads it on every plan.</summary>
        internal IcParams Params { get { return _p; } }

        internal static string ModeOf(string v) { return (v ?? "track").Trim().ToLowerInvariant() == "once" ? "once" : "track"; }
        internal static string SpeedOf(string v) { return (v ?? "auto").Trim().ToLowerInvariant() == "current" ? "current" : "auto"; }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Intercept";
            var p = new IcParams();
            p.Enabled = cfg.Bind(S, "Enabled", true, "Intercept course in the contact context menu: the point and the time of the intercept from the track, a route on request", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sMode = cfg.Bind(S, "Mode", "track", "track | once: track = the route follows the target (replanned by the rules), once = one plan", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "track", "once" } });
            p.Mode = ModeOf(sMode.Value);
            var sSpeed = cfg.Bind(S, "Speed", "auto", "auto | current: auto = the smallest telegraph step that catches the target, current = keep the telegraph", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "current" } });
            p.Speed = SpeedOf(sSpeed.Value);
            var sShowLine = cfg.Bind(S, "ShowLine", true, "Draw the intercept line and the intercept point on the tactical map", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            p.ShowLine = sShowLine.Value;
            var sAvoid = cfg.Bind(S, "AvoidEnemyRanges", "warn", "warn | detour | off: the route is checked against the weapon ranges of identified hostile tracks (class data, what the player knows); warn = warning only, detour = route around the circles (A*), else warning", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "warn", "detour", "off" } });
            p.Avoid = InterceptThreatMath.AvoidOf(sAvoid.Value);
            var sOnEntry = cfg.Bind(S, "OnZoneEntry", "warn", "warn | hold | abort: before the unit enters an enemy weapon zone (not the target's): warning only, stop and wait until the zone leaves the route, or stop and end the intercept", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "warn", "hold", "abort" } });
            p.OnEntry = InterceptThreatMath.OnEntryOf(sOnEntry.Value);
            var sZoneLeadS = cfg.Bind(S, "ZoneLeadS", 120.0, "OnZoneEntry hold/abort: stop when the entry into an enemy zone is this close (seconds)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.ZoneLeadS = Math.Max(10, sZoneLeadS.Value);
            var sZoneMarginNm = cfg.Bind(S, "ZoneMarginNm", 2.0, "AvoidEnemyRanges detour: the route keeps this far (nm) outside an enemy weapon circle", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ZoneMarginNm = Math.Max(0, Math.Min(20, sZoneMarginNm.Value));
            var sRefreshS = cfg.Bind(S, "RefreshS", 30.0, "Mode track: the least seconds between two plans", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.RefreshS = Math.Max(5, sRefreshS.Value);
            var sMinShiftNm = cfg.Bind(S, "MinShiftNm", 0.5, "Mode track: the intercept point must move this far (nm) to make a new plan", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.MinShiftNm = Math.Max(0.05, sMinShiftNm.Value);
            var sArriveNm = cfg.Bind(S, "ArriveNm", 1.0, "The intercept is done when the unit is this close (nm) to the target", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.ArriveNm = Math.Max(0.1, sArriveNm.Value);
            var sStaleS = cfg.Bind(S, "StaleS", 60.0, "Track older than this (s): the line turns grey and no new orders are given", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.StaleS = Math.Max(5, sStaleS.Value);
            var sLostS = cfg.Bind(S, "LostS", 180.0, "Track older than this (s): the intercept ends (track lost)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.LostS = Math.Max(p.StaleS, sLostS.Value);
            var sSpeedUncertPct = cfg.Bind(S, "SpeedUncertPct", 20.0, "Uncertainty of the target speed (percent): the width of the ETA band", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.SpeedUncertPct = Math.Max(0, Math.Min(100, sSpeedUncertPct.Value));
            var sProbeBudget = cfg.Bind(S, "ProbeBudget", 500, "Land check: terrain probes per frame for the route around land", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.ProbeBudget = Math.Max(50, Math.Min(5000, sProbeBudget.Value));
            p.MaxRangeNm = Math.Max(10, cfg.Bind(S, "MaxRangeNm", 150.0, "No intercept farther than this (nm): the flat plane is no longer exact", UiKeyClass.Internal).Value);
            p.MaxEtaH = Math.Max(1, cfg.Bind(S, "MaxEtaH", 6.0, "No intercept later than this (hours)", UiKeyClass.Internal).Value);
            OnChanged(sMode, v => p.Mode = ModeOf(v));
            OnChanged(sSpeed, v => p.Speed = SpeedOf(v));
            OnChanged(sShowLine, v => p.ShowLine = v);
            OnChanged(sAvoid, v => p.Avoid = InterceptThreatMath.AvoidOf(v));
            OnChanged(sOnEntry, v => p.OnEntry = InterceptThreatMath.OnEntryOf(v));
            OnChanged(sZoneLeadS, v => p.ZoneLeadS = Math.Max(10, v));
            OnChanged(sZoneMarginNm, v => p.ZoneMarginNm = Math.Max(0, Math.Min(20, v)));
            OnChanged(sRefreshS, v => p.RefreshS = Math.Max(5, v));
            OnChanged(sMinShiftNm, v => p.MinShiftNm = Math.Max(0.05, v));
            OnChanged(sArriveNm, v => p.ArriveNm = Math.Max(0.1, v));
            OnChanged(sStaleS, v => { p.StaleS = Math.Max(5, v); if (p.LostS < p.StaleS) p.LostS = p.StaleS; });
            OnChanged(sLostS, v => p.LostS = Math.Max(p.StaleS, v));
            OnChanged(sSpeedUncertPct, v => p.SpeedUncertPct = Math.Max(0, Math.Min(100, v)));
            OnChanged(sProbeBudget, v => p.ProbeBudget = Math.Max(50, Math.Min(5000, v)));
            _p = p;
        }

        private sealed class MenuService : IInterceptMenu
        {
            private readonly GameApi _a;
            private readonly InterceptController _c;
            public MenuService(GameApi a, InterceptController c) { _a = a; _c = c; }

            public IcMenu Item(object targetObject, out Action click)
            {
                click = null;
                int own, track;
                if (!_a.InterceptMenuTarget(targetObject, out own, out track)) return null;
                var m = _c.Menu(own, track);
                if (m == null) return null;
                if (m.Enabled)
                {
                    bool cancel = m.IsCancel;
                    click = () =>
                    {
                        string ack, err;
                        err = cancel ? _c.Cancel(own, "menu") : _c.Start(own, track, out ack);
                        if (err != null) _a.Log("[Intercept] menu click: " + err);
                    };
                }
                return m;
            }

            /// <summary>T-160: the "Cancel intercept" item of the XO menu.</summary>
            public bool UnitActive(int ownUid, out string eta) { return _c.UnitActive(ownUid, out eta); }
            public string CancelUnit(int ownUid) { return _c.Cancel(ownUid, "xo_menu"); }
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<IcParams>(_p);   // read by the harmony module: the menu patch is also needed when the XO menu is off
            try
            {
                if (!_p.Enabled) { ctx.Host.LogInfo("intercept off ([Intercept] Enabled=false)"); return; }
                if (!_api.Capabilities.Contains("intercept")) { ctx.Host.LogWarning("intercept disabled: members of the game are missing"); return; }
                var sar = ctx.Get<SarParams>();   // the land check uses the SAR depth / shore options (one place for the A* parameters)
                if (sar != null) { _p.MinDepthM = sar.MinDepthM; _p.SubTransitDepthM = sar.SubTransitDepthM; _p.ShoreMarginNm = sar.ShoreMarginNm; _p.MaxCells = sar.MaxCells; }
                var steering = ctx.Steering;
                var busy = ctx.Get<ISmartBusy>();
                var ships = ctx.Get<ISarShips>();
                if (busy != null) steering.Register(UnitSteering.SmartAttack, uid => busy.Busy(uid));
                if (ships != null) steering.Register(UnitSteering.Sar, uid => ships.ShipActive(uid));
                steering.Register(UnitSteering.Evasion, uid => { var u = _api.FindOwnUnit(uid); return u != null && _api.EvasionActive(u); });
                _ctl = _api.CreateInterceptController(steering, _p, ModThreads.PoolRunner("ic.solve"));
                ctx.Release.Add(new ReleaseParticipant("intercept", _ctl.ReleaseUnit), ReleaseRegistry.OrderIntercept);
                var radar = ctx.Get<IRadarView>();
                IAswMap map = new AswMapProvider(radar == null ? null : radar.Overlay);
                _ov = new InterceptOverlay(() => _ctl.View(), map, m => ctx.Host.LogInfo(m))
                {
                    NoesisActive = () => radar != null && radar.Layer != null && radar.Layer.Active,
                    ExtraLabels = ctx.Get<IContactInfo>() as IMapExtraLabels
                };
                if (radar != null && radar.Layer != null) radar.Layer.Extra.Add(_ov);
                ctx.Mission.Subscribe(64, "intercept", null, why =>
                {
                    _ctl.OnMissionLeft(); ctx.Steering.ClearClaims(); _api.InterceptLeaveMission();
                    if (_ov != null) _ov.ClearLabels();
                });
                var ov = _ov;
                ctx.Mission.Subscribe(66, "intercept_labels", e => ov.ClearLabels(), null);   // T-335 review: contact_info (65) empties the extra labels on entry too; the next Tick publishes again
                _api.InterceptMenu = new MenuService(_api, _ctl);
            }
            catch (Exception e)
            {
                ctx.Host.LogError("intercept init crashed: " + e);
                _ctl = null; _api.Intercept = null; _api.InterceptMenu = null; _ov = null;
            }
        }

        public override void Update(UiFrame frame)
        {
            if (_ctl == null) return;
            _ctl.FramePump();
            _ctl.Tick();
            if (_ov != null) _ov.Tick();
        }

        public override void OnGUI()
        {
            if (_ov != null) _ov.OnGUI();
        }

        public override void Dispose()
        {
            try { if (_ov != null) _ov.ClearLabels(); } catch { }
        }
    }
}
