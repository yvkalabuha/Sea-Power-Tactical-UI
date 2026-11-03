using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-165 / ADR-0062: module perf, settings section "Performance": [Perf] LightSmoke and LightSmokeFactor (both Live). The patches themselves are installed
    /// by HarmonyFeature (always, when the capability is there); this module owns the switch, the factor and the service the patch bodies call.
    /// </summary>
    internal sealed class PerfFeature : UiFeatureBase, IUiDiag, IUiSettingsSection
    {
        private IUiSetting<bool> _on;
        private IUiSetting<string> _factor;
        private LightSmokeService _svc;

        public PerfFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "perf"; } }
        public int SettingsOrder { get { return 130; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(PerfSpecs.Set); }

        private static double Parse(string v)
        {
            double k;
            return LightSmokeMath.TryParseK(v, out k) ? k : LightSmokeMath.DefaultK;
        }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Perf";
            _on = cfg.Bind(S, "LightSmoke", false, "Light smoke of missiles and explosions: fewer particles in new effects (missile trails, explosions, fires, splashes). Less GPU load in combat; FPS did not grow in the measurements",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Capability = "light_smoke" });
            _factor = cfg.Bind(S, "LightSmokeFactor", "0.35", "0.2 | 0.35 | 0.5: share of particles light smoke keeps (emission and bursts x k, max particles x k, lifetime x sqrt k)",
                UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "0.2", "0.35", "0.5" }, Capability = "light_smoke" });
            OnChanged(_on, v => { if (_svc != null) _svc.SetOn(v); });
            OnChanged(_factor, v => { if (_svc != null) _svc.SetK(Parse(v)); });
        }

        public override void Init(UiContext ctx)
        {
            var host = ctx.Host;
            if (ctx.Api == null || !ctx.Api.Capabilities.Contains("light_smoke"))
            {
                host.LogInfo("perf: light smoke unavailable (capability light_smoke is missing)");
                return;
            }
            _svc = new LightSmokeService(new UnityParticleAccess(), () => host.RealTime, m => host.LogInfo("[Perf] " + m));
            _svc.SetK(Parse(_factor.Value));
            _svc.SetOn(_on.Value);
            LightSmokePatches.Service = _svc;
            ctx.Mission.Subscribe(95, "perf", null, why => { if (_svc != null) _svc.LeaveMission(); });
        }

        public override void Update(UiFrame frame) { if (_svc != null) _svc.Tick(); }

        public override void Dispose()
        {
            LightSmokePatches.Service = null;
            try { if (_svc != null) _svc.Registry.RestoreAll(); } catch { }
        }

        public string DiagSection { get { return "perf"; } }

        public string DiagJson(int limit)
        {
            var api = Ctx.Api;
            if (_svc == null) return "{\"available\":false,\"on\":false}";
            string d = _svc.DiagJson();
            return "{\"available\":true,\"direct\":" + Json.Bool(api != null && api.Capabilities.Contains("light_smoke_direct"))
                + ",\"chaff\":" + Json.Bool(api != null && api.Capabilities.Contains("light_smoke_chaff")) + "," + d.Substring(1);
        }

        public override void MemProbe(IDictionary<string, long> into)
        {
            if (_svc != null) into["perf.entries"] = _svc.Registry.Count;
        }
    }
}
