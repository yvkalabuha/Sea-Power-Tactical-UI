using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>What the Harmony module needs from the SmartAttack section.</summary>
    internal sealed class SmartAttackSettings
    {
        public bool ConsumePickClick = true;
    }

    /// <summary>ADR-0038 / T-102: smart torpedo attack (config section SmartAttack): controller, dialog, target pick, and the ISmartPick / ISmartBusy services.</summary>
    internal sealed class SmartAttackFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private readonly SmartAttackSettings _settings = new SmartAttackSettings();
        private SmartParams _smartP;
        private bool _smartEnabled = true, _smartDebug;
        private SmartAttackDialog _smartDlg;
        private SmartAttackPick _smartPick;

        public SmartAttackFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "smart_attack"; } }
        public int SettingsOrder { get { return 110; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(SmartAttackSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "SmartAttack";
            var p = new SmartParams();
            _smartEnabled = cfg.Bind(S, "Enabled", true, "Smart torpedo attack of a submarine (ring sector NW): wire-guided approach from the side, with a veto near neutrals", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var smartDebug = cfg.Bind(S, "Debug", false, "Log every step of the smart attack", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            _smartDebug = smartDebug.Value;
            OnChanged(smartDebug, v => { _smartDebug = v; if (_api?.SmartAttack != null) _api.SmartAttack.Debug = v; });
            // NextOrder: the controller keeps this very SmartParams object and reads it when it plans an attack, so a change reaches the next attack
            var offsetDeg = cfg.Bind(S, "OffsetDeg", p.OffsetDeg, "Aim offset from the target's bearing (deg)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.OffsetDeg = offsetDeg.Value;
            OnChanged(offsetDeg, v => _smartP.OffsetDeg = v);
            var legNm = cfg.Bind(S, "LegNm", p.LegNm, "Length of the first leg (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LegNm = legNm.Value;
            OnChanged(legNm, v => _smartP.LegNm = v);
            var fuelReserve = cfg.Bind(S, "FuelReservePct", p.FuelReservePct, "Torpedo range reserve (percent)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.FuelReservePct = fuelReserve.Value;
            OnChanged(fuelReserve, v => _smartP.FuelReservePct = v);
            var coneDeg = cfg.Bind(S, "ConeDeg", p.ConeDeg, "Half-angle of the neutral check cone (deg)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ConeDeg = coneDeg.Value;
            OnChanged(coneDeg, v => _smartP.ConeDeg = v);
            var vetoRadius = cfg.Bind(S, "VetoRadiusNm", p.VetoRadiusNm, "Neutral track closer than this to the path asks for confirmation (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.VetoRadiusNm = vetoRadius.Value;
            OnChanged(vetoRadius, v => _smartP.VetoRadiusNm = v);
            var restore = cfg.Bind(S, "RestoreOnLoad", "auto", "off | ask | auto: continue the attack after a save is loaded", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "auto", "ask", "off" } });
            string r = (restore.Value ?? "auto").Trim().ToLowerInvariant();
            OnChanged(restore, v => { string x = (v ?? "auto").Trim().ToLowerInvariant(); _smartP.RestoreOnLoad = x == "off" || x == "ask" ? x : "auto"; });   // read at the next save load
            _settings.ConsumePickClick = cfg.Bind(S, "ConsumePickClick", true, "While a target is being picked on the map / in 3D the click does not reach the game (no unit selection change); needs two small Harmony patches", UiKeyClass.Advanced).Value;
            var pickTol = cfg.Bind(S, "PickTolPx", 16.0, "Map pick: how close to a unit icon centre the click must be when it hits a mission zone shape (px at 1080p)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.PickTolPx = Math.Max(4.0, pickTol.Value);
            OnChanged(pickTol, v => Ctx.Api.PickTolPx = Math.Max(4.0, v));
            p.RestoreOnLoad = r == "off" || r == "ask" ? r : "auto";
            _smartP = p;
        }


        private sealed class PickService : ISmartPick
        {
            private readonly SmartAttackPick _p;
            public PickService(SmartAttackPick p) { _p = p; }
            public void Begin(int uid) { _p.Begin(uid); }
        }

        private sealed class BusyService : ISmartBusy
        {
            private readonly GameApi _a;
            public BusyService(GameApi a) { _a = a; }
            public bool Busy(int uid) { return _a.SmartAttack != null && _a.SmartAttack.Busy(uid); }
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<SmartAttackSettings>(_settings);   // read by the Harmony module
            var busy = new BusyService(_api);
            ctx.Provide<ISmartBusy>(busy);
            _api.SmartBusy = busy;   // the SAR controller asks whether the smart attack owns a unit
            try
            {
                if (!_smartEnabled) { _api.Capabilities.Remove("smart_attack"); return; }
                if (!_api.Capabilities.Contains("smart_attack")) { ctx.Host.LogWarning("smart attack disabled: members of the game are missing"); return; }
                _api.SmartLogSink = m => ctx.Host.LogInfo(m);
                _api.SmartAttackSaveDir = ctx.DataDir("smart_attack");
                _api.CreateSmartAttackController(_smartP, _smartDebug);
                _api.SmartAttack.HeldByEvasion = uid => ctx.Steering.Owner(uid) == UnitSteering.Evasion;   // T-101 / ADR-0071: a submarine that evades a torpedo is not positioned or restored
                _smartDlg = new SmartAttackDialog(() => _api.SmartAttack, () => _api.Frame.MissionTimeS ?? 0, b => _api.SetSmartBlock(b), m => ctx.Host.LogInfo(m));
                _smartPick = new SmartAttackPick(_api, m => ctx.Host.LogInfo(m));
                var pick = new PickService(_smartPick);
                ctx.Provide<ISmartPick>(pick);
                _api.SmartPick = pick;
            }
            catch (Exception e)
            {
                ctx.Host.LogError("smart attack init crashed: " + e);
                _api.SmartAttack = null; _api.SmartPick = null; _api.Capabilities.Remove("smart_attack");
            }
        }

        public override void Update(UiFrame frame)
        {
            _api.PumpSmartAttack();
            if (_smartDlg != null) _smartDlg.Update();
            if (_smartPick != null) _smartPick.Update();
        }

        public override void OnGUI()
        {
            if (_smartDlg != null) _smartDlg.OnGUI();
            if (_smartPick != null) _smartPick.OnGUI();
        }

        public override void Dispose()
        {
            try { if (_smartDlg != null) _smartDlg.Dispose(); _api.SetSmartBlock(false); } catch { }
        }
    }
}
