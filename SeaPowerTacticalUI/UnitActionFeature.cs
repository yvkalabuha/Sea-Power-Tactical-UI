using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-234 (ADR-0096): module unit_action. Every second it asks the controllers of the mod what each unit is doing for the mod (registry: strongest label per unit),
    /// builds "command: phase" and puts it into the game's OrderFeedbackText, so the "Orders:" field of the selected unit shows it instead of "moving to the destination".
    /// Writes only on change; when the mod lets a unit go the field is cleared and shows the game's text again. Key [Ui] UnitActionText = smart | replace | off.
    /// The sources are the controllers' IUnitActionSource implementations (partial *.ActionLabels.cs); a controller that has none yet is skipped.
    /// </summary>
    internal sealed class UnitActionFeature : UiFeatureBase
    {
        private static readonly string[] Deps = { "asw", "cap", "air_guard", "evasion", "air_evasion", "sar", "smart_attack", "intercept" };
        public const double TickS = 1.0;

        private IUiSetting<string> _mode;
        private readonly UnitActionRegistry _reg = new UnitActionRegistry();
        private readonly UnitActionWriter _writer = new UnitActionWriter();
        private readonly Dictionary<int, string> _wanted = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _logged = new Dictionary<int, string>();
        private GameApi _api;
        private IUiHost _host;
        private double _last = double.NegativeInfinity;
        private bool _active;

        public UnitActionFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "unit_action"; } }
        public override string[] DependsOn { get { return Deps; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(UnitActionSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            _mode = cfg.Bind("Ui", "UnitActionText", "smart",
                "smart | replace | off: what the Orders field of a unit shows while the mod drives it. smart = the mod label, plus the game text after a dot when the game text says more than moving or waiting; replace = the mod label only; off = the game text only",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Section = "general", Apply = UiApply.Live, Options = new[] { "smart", "replace", "off" } });
        }

        public override void Init(UiContext ctx)
        {
            _api = ctx.Api; _host = ctx.Host;
            if (_api == null || !_api.Capabilities.Contains("unit_action"))
            {
                if (_host != null) _host.LogInfo("[UnitAction] unavailable (capability unit_action is missing: OrderFeedbackText not found); the Orders field keeps the game text");
                return;
            }
            Add(_api.Asw); Add(_api.Cap); Add(_api.AirGuard); Add(_api.SubEvasion); Add(_api.AirEvasion);
            Add(_api.SmartAttack); Add(_api.Intercept); Add(_api.Sar); Add(_api.UaEvasionSource);   // T-230: ships in the torpedo evasion
            _active = true;
            ctx.Mission.Subscribe(90, "unit_action", null, why => { _writer.Forget(); _logged.Clear(); _wanted.Clear(); _api.UaLeaveMission(); });
        }

        /// <summary>A controller joins the registry when it implements IUnitActionSource (done by its partial *.ActionLabels.cs).</summary>
        private void Add(object controller)
        {
            var s = controller as IUnitActionSource;
            if (s != null) _reg.Add(s);
        }

        public override void Update(UiFrame frame)
        {
            if (!_active || frame.RealTime - _last < TickS) return;
            _last = frame.RealTime;
            string mode = _mode == null ? "smart" : _mode.Value;
            if (_api.AirGuard != null && _api.Asw != null) _api.AirGuard.ActionSteer = _api.Asw.AirSteer;   // the steering is created by the air_guard module after our Init
            _api.UaBeginTick();
            if (mode == "off")
            {
                _api.UaHeldUids.Clear();
                if (_writer.Owned > 0) Apply(_writer.ReleaseAll(_api.UaFeedback));
                return;
            }
            double now = _api.Frame.MissionTimeS ?? 0;
            var labels = _reg.Resolve(now, m => _host.LogWarning(m));
            _api.UaHeldUids.Clear();   // T-243: "the mod drives it" for the ring (no allocation per tick)
            foreach (var kv0 in labels) _api.UaHeldUids.Add(kv0.Key);
            var plain = mode == "smart" ? _api.UaPlainTexts() : null;
            _wanted.Clear();
            foreach (var kv in labels)
            {
                string text = UnitActionText.Compose(UnitActionText.Format(kv.Value), mode == "smart" ? _api.UaGameText(kv.Key) : "", plain, mode);
                if (text.Length > 0) _wanted[kv.Key] = text;
            }
            var writes = _writer.Plan(_wanted, _api.UaFeedback);
            Apply(writes);
            foreach (var w in writes)
            {
                if (w.Text.Length == 0) continue;
                UnitActionLabel l;
                string src = labels.TryGetValue(w.Uid, out l) ? l.Source : "?";
                _host.LogInfo("[UnitAction] set uid=" + w.Uid + " src=" + src + " text=\"" + w.Text + "\"");
            }
        }

        private void Apply(IList<UnitActionWriter.Write> writes)
        {
            foreach (var w in writes)
            {
                if (!_api.UaSetFeedback(w.Uid, w.Text)) { _writer.Failed(w.Uid); continue; }
                if (w.Text.Length == 0) { _logged.Remove(w.Uid); _host.LogInfo("[UnitAction] clear uid=" + w.Uid); }
                else _logged[w.Uid] = w.Text;
            }
        }

        public override void Dispose()
        {
            if (!_active || _api == null) return;
            try
            {
                _api.UaBeginTick();
                foreach (var w in _writer.ReleaseAll(_api.UaFeedback)) _api.UaSetFeedback(w.Uid, w.Text);
            }
            catch (Exception) { }
            _active = false;
        }

        public override void MemProbe(IDictionary<string, long> into) { into["unit_action.owned"] = _writer.Owned; }
    }
}
