using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-100 / ADR-0060: aircraft evasion (config section AirEvade): the ring item "Evade attack" and the Standing Orders flag VB_AutoEvadeAir.
    /// Orders only: the native state, a speed step, an altitude preset, a waypoint, one chaff salvo. The module stands after TacticsFeature in the list, so the
    /// guard of the after-launch turn sees a new turn in the same frame.</summary>
    internal sealed class AirEvasionFeature : UiFeatureBase, IUiDiag, IUiSettingsSection
    {
        private readonly GameApi _api;
        private readonly AirEvasionParams _p = new AirEvasionParams();
        private AirEvasionController _ctl;
        private double _nextTick = double.NegativeInfinity;

        public AirEvasionFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "air_evasion"; } }
        public override string[] DependsOn { get { return new[] { "tactics", "doctrine" }; } }
        public int SettingsOrder { get { return 82; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(AirEvasionSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "AirEvade";
            _p.Enabled = cfg.Bind(S, "Enabled", true, "Aircraft evasion: the ring item Evade attack and the Standing Orders flag Auto missile evasion", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            _api.AirEvadeEnabled = _p.Enabled;
            var tti = cfg.Bind(S, "AutoTtiS", 20.0, "Auto missile evasion starts when the time to impact is at most this (s, 5..60)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 5, Max = 60, Step = 5, UnitKey = "s" });
            var minNm = cfg.Bind(S, "AutoMinNm", 3.0, "Auto missile evasion also starts when the missile is this close (nm, 1..10)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 1, Max = 10, Step = 0.5, UnitKey = "nm" });
            var cooldown = cfg.Bind(S, "AutoCooldownS", 10.0, "Pause (s) before auto evasion starts again against the same missile (0..60)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 60, Step = 5, UnitKey = "s" });
            var threat = cfg.Bind(S, "ThreatNm", 40.0, "The button without a missile turns away from a hostile air track within this range (nm, 10..100)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 100, Step = 5, UnitKey = "nm" });
            var beam = cfg.Bind(S, "BeamMaxS", 180.0, "The longest turn away from an air threat without a missile (s, 30..600)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 30, Max = 600, Step = 30, UnitKey = "s" });
            var hint = cfg.Bind(S, "OptionHint", true, "Tell once per mission when the game option for aircraft missile evasion is off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var debug = cfg.Bind(S, "Debug", false, "Log every evasion tick (mode, course)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            _p.AutoTtiS = Clamp(tti.Value, 5, 60); _p.AutoMinNm = Clamp(minNm.Value, 1, 10); _p.AutoCooldownS = Clamp(cooldown.Value, 0, 60);
            _p.ThreatNm = Clamp(threat.Value, 10, 100); _p.BeamMaxS = Clamp(beam.Value, 30, 600); _p.OptionHint = hint.Value; _p.Debug = debug.Value;
            OnChanged(tti, v => _p.AutoTtiS = Clamp(v, 5, 60));
            OnChanged(minNm, v => _p.AutoMinNm = Clamp(v, 1, 10));
            OnChanged(cooldown, v => _p.AutoCooldownS = Clamp(v, 0, 60));
            OnChanged(threat, v => _p.ThreatNm = Clamp(v, 10, 100));
            OnChanged(beam, v => _p.BeamMaxS = Clamp(v, 30, 600));
            OnChanged(hint, v => _p.OptionHint = v);
            OnChanged(debug, v => _p.Debug = v);
        }

        private static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        // ---- IUiDiag (ADR-0046): runs, cooldowns, muted missiles
        public string DiagSection { get { return "air_evasion"; } }

        public string DiagJson(int limit)
        {
            var c = _ctl;
            return c == null ? "{\"runs\":[],\"run_count\":0,\"active\":false}" : c.DiagJson(limit);
        }

        public override void Init(UiContext ctx)
        {
            try
            {
                if (!_p.Enabled) { ctx.Host.LogInfo("air_evasion off ([AirEvade] Enabled=false)"); return; }
                if (!_api.Capabilities.Contains("air_evasion")) { ctx.Host.LogWarning("air_evasion disabled: members of the game are missing"); return; }
                _api.UiSteering = ctx.Steering;
                _ctl = _api.CreateAirEvasionController(_p);
                ctx.Steering.RegisterPreempt(UnitSteering.AirEvasion, _ctl.Holds);
                ctx.Release.Add(new ReleaseParticipant("air_evasion", _ctl.Release), ReleaseRegistry.OrderAirEvasion);
                ctx.Mission.Subscribe(69, "air_evasion", null, why => { _ctl.LeaveMission(); _nextTick = double.NegativeInfinity; });
            }
            catch (Exception e)
            {
                ctx.Host.LogError("air_evasion init crashed: " + e);
                _ctl = null; _api.AirEvasion = null;
            }
        }

        public override void Update(UiFrame frame)
        {
            if (_ctl == null || !_api.Frame.InMission) return;
            _api.PostLaunchGuard();   // every frame: a turn that Tactics started a moment ago is dropped before the aircraft follows it
            var t = _api.Frame.MissionTimeS;
            if (!t.HasValue) return;
            if (t.Value < _nextTick - 5) _nextTick = double.NegativeInfinity;   // the clock went back (a loaded save)
            if (t.Value < _nextTick) return;
            _nextTick = t.Value + 1.0;
            _ctl.Tick(t.Value);
        }
    }
}
