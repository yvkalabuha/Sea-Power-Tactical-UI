using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>The adapter of IHitFacts to the game (HitEstimateGame.*.cs): thin, no logic.</summary>
    internal sealed class GameHitFacts : IHitFacts
    {
        private readonly GameApi _a;
        public GameHitFacts(GameApi a) { _a = a; }
        public bool InMission { get { return _a.Frame.InMission; } }
        public bool IsForeign(int trackId) { return _a.HeIsForeign(trackId); }
        public bool IsIdentified(int trackId) { return _a.HeIsIdentified(trackId); }
        public string ClassIniName(int trackId) { return _a.HeClassIniName(trackId); }
        public HeClass Class(string iniName) { return _a.HeClassOf(iniName); }
        public IList<HeCandidate> Candidates() { return _a.HeCandidates(); }
        public HeAmmo AmmoByFile(string file) { return _a.HeAmmoByFile(file); }
        public double DamageBonus(out bool known) { return _a.HeDamageBonus(out known); }
        public string ClassWhy(string iniName) { return _a.HeClassWhy(iniName); }
    }

    /// <summary>T-168: the service the Overview reads. Answers are cached for one Overview pass (about a second).</summary>
    internal sealed class HitEstimateDetailService : IHitEstimateDetail
    {
        private readonly IHitFacts _facts;
        private readonly HitLedger _ledger;
        private readonly HeSettings _s;
        private readonly HeSelection _sel;
        private readonly Dictionary<int, bool> _exp = new Dictionary<int, bool>();
        private readonly Dictionary<int, HeCell> _cells = new Dictionary<int, HeCell>();
        private readonly Dictionary<int, HeDetail> _details = new Dictionary<int, HeDetail>();

        private readonly Action<string> _log;
        private readonly HashSet<string> _logged = new HashSet<string>();
        private readonly Dictionary<int, HeDetail> _last = new Dictionary<int, HeDetail>();   // diagnostics: the last block built per track

        public HitEstimateDetailService(IHitFacts facts, HitLedger ledger, HeSettings s, HeSelection sel, Action<string> log = null) { _facts = facts; _ledger = ledger; _s = s; _sel = sel; _log = log; }

        internal IDictionary<int, HeDetail> LastDetails { get { return _last; } }

        /// <summary>One log line per (reason, class) when a block carries no estimate.</summary>
        private void Note(int trackId, HeDetail d)
        {
            if (d == null) return;
            _last[trackId] = d;
            if (_log == null || d.Reason == "ok" || d.Reason == null) return;
            string key = d.Reason + "|" + d.ClassName;
            if (_logged.Add(key))
                _log("[HitEstimate] no estimate: " + d.Reason + " class=" + d.ClassName + " (" + d.ClassWhy + ") candidates=" + d.Candidates + " options=" + d.Options);
        }

        public int CacheCount { get { return _exp.Count + _cells.Count + _details.Count; } }

        public void NewPass() { _exp.Clear(); _cells.Clear(); _details.Clear(); _seen = _ledger.Version; }

        private int _seen;

        /// <summary>A hit came in since the caches were filled: drop the answers that depend on the ledger.</summary>
        private void Fresh()
        {
            if (_seen == _ledger.Version) return;
            _cells.Clear(); _details.Clear(); _seen = _ledger.Version;
        }

        public void Reset() { NewPass(); }

        public bool Expandable(int trackId)
        {
            if (!_s.Enabled) return false;
            bool v;
            if (_exp.TryGetValue(trackId, out v)) return v;
            v = HitEstimateModel.Expandable(_facts, trackId);
            _exp[trackId] = v;
            return v;
        }

        public HeCell Cell(int trackId)
        {
            if (!_s.Enabled || !_s.Counter) return null;
            Fresh();
            HeCell c;
            if (_cells.TryGetValue(trackId, out c)) return c;
            c = HitEstimateModel.Cell(_facts, trackId, _ledger);
            _cells[trackId] = c;
            return c;
        }

        public HeDetail Detail(int trackId)
        {
            if (!_s.Enabled) return null;
            Fresh();
            HeDetail d;
            if (_details.TryGetValue(trackId, out d)) return d;
            d = HitEstimateModel.Detail(_facts, trackId, _ledger, _s, _sel);
            _details[trackId] = d;
            Note(trackId, d);
            return d;
        }

        public bool Cycle(int trackId, int dir)
        {
            if (!_s.Enabled) return false;
            var opts = HitEstimateModel.OptionsFor(_facts, trackId, _s);
            if (opts.Count < 2) return false;
            HitEstimateModel.Cycle(opts, _sel, dir);
            _details.Clear();
            return true;
        }
    }

    /// <summary>
    /// T-168 / ADR-0064: module hit_estimate, settings section "hit_estimate": [HitEstimate] Enabled, Counter, Land, Installations. It owns the estimate service and the ledger of the player's hits
    /// (fed by the weapon-report hook that HarmonyFeature installs when Counter is on); the Overview shows everything (expanded rows, the columns hits / dmg).
    /// There is no entry in the contact menu (ADR-0064 item 23).
    /// </summary>
    internal sealed class HitEstimateFeature : UiFeatureBase, IUiSettingsSection, IUiDiag
    {
        private readonly GameApi _api;
        private readonly HeSettings _s = new HeSettings();
        private readonly HeSelection _sel = new HeSelection();
        private readonly HitLedger _ledger = new HitLedger();
        private HitEstimateDetailService _svc;
        private double _nextPrune;

        public HitEstimateFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "hit_estimate"; } }
        public int SettingsOrder { get { return 78; } }
        public override string[] DependsOn { get { return new[] { "text", "overview" }; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(HitEstimateSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "HitEstimate";
            var en = cfg.Bind(S, "Enabled", true, "Hits-to-kill estimate in the Overview: a + on the row of an identified foreign contact opens the estimate of your weapons, the list of your hits and the status (needs the hit_estimate capability)",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Capability = "hit_estimate" });
            _s.Enabled = en.Value;
            OnChanged(en, v => { _s.Enabled = v; if (_svc != null) _svc.Reset(); });
            _s.Counter = cfg.Bind(S, "Counter", true, "Count your hits from the game's weapon report: the list of hits in the expanded row and the columns hits / dmg (the patch is installed at start)",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart, Capability = "hit_estimate_hits" }).Value;
            var land = cfg.Bind(S, "Land", true, "Reference for ground units in the expanded row: what to destroy and the radius of a sure kill of your weapon (no hit count)",
                UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Capability = "hit_estimate" });
            _s.Land = land.Value;
            OnChanged(land, v => { _s.Land = v; if (_svc != null) _svc.Reset(); });
            var inst = cfg.Bind(S, "Installations", true, "Reference for large ground objects (airbases, ports, factories, bridges) in the expanded row: zones, hits per zone and the reference line with perfect aim (no count to destruction)",
                UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Capability = "hit_estimate_zones" });
            _s.Installations = inst.Value;
            OnChanged(inst, v => { _s.Installations = v; if (_svc != null) _svc.Reset(); });
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<HeSettings>(_s);   // HarmonyFeature installs the hook only when Counter is on
            var host = ctx.Host;
            if (!_s.Enabled) { host.LogInfo("hit_estimate off ([HitEstimate] Enabled=false)"); return; }
            if (!_api.Capabilities.Contains("hit_estimate")) { host.LogWarning("hit_estimate disabled: members of the game are missing"); return; }
            _svc = new HitEstimateDetailService(new GameHitFacts(_api), _ledger, _s, _sel, m => host.LogInfo(m));
            _api.HitEstimate = _svc;
            ctx.Provide<IHitEstimateDetail>(_svc);
            _api.HeLedger = _ledger;
            ctx.Mission.Subscribe(81, "hit_estimate", null, why =>
            {
                _ledger.Clear(); _sel.Clear();
                if (_svc != null) _svc.Reset();
                _api.HeLeaveMission();
            });
        }

        public override void Update(UiFrame frame)
        {
            if (_svc == null || frame.RealTime < _nextPrune) return;
            _nextPrune = frame.RealTime + 2.0;
            try
            {
                if (_ledger.Tracks == 0 || !_api.Frame.InMission) return;
                _api.RrRescanTracks();
                _ledger.Prune(id => _api.RrHasTrack(id) && _api.HeIsForeign(id));
            }
            catch (Exception e) { Ctx.Host.LogWarning("hit_estimate prune failed: " + e.GetType().Name + ": " + e.Message); }
        }

        public string DiagSection { get { return "hit_estimate"; } }

        public string DiagJson(int limit)
        {
            var sb = new System.Text.StringBuilder("{");
            sb.Append("\"available\":").Append(Json.Bool(_svc != null)).Append(",\"enabled\":").Append(Json.Bool(_s.Enabled)).Append(",\"counter\":").Append(Json.Bool(_s.Counter)).Append(",\"installations\":").Append(Json.Bool(_s.Installations));
            sb.Append(",\"classes\":").Append(_api.HeClassCount).Append(",\"ammo\":").Append(_api.HeAmmoCount).Append(",\"ledger_tracks\":").Append(_ledger.Tracks);
            sb.Append(",\"choice\":").Append(Json.Str(_sel.Key)).Append(",\"tracks\":[");
            int n = 0;
            if (_svc != null)
                foreach (var kv in _svc.LastDetails)
                {
                    if (limit > 0 && n >= limit) break;
                    var d = kv.Value;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(kv.Key).Append(",\"class\":").Append(Json.Str(d.ClassName)).Append(",\"class_why\":").Append(Json.Str(d.ClassWhy))
                      .Append(",\"reason\":").Append(Json.Str(d.Reason)).Append(",\"candidates\":").Append(d.Candidates).Append(",\"options\":").Append(d.Options)
                      .Append(",\"pick\":").Append(Json.Str(d.Pick)).Append(",\"lines\":").Append(d.Lines).Append(",\"hits\":").Append(_ledger.Count(kv.Key)).Append('}');
                }
            return sb.Append("]}").ToString();
        }

        public override void MemProbe(IDictionary<string, long> into)
        {
            into["hit_estimate.class"] = _api.HeClassCount;
            into["hit_estimate.ammo"] = _api.HeAmmoCount;
            into["hit_estimate.hits"] = _ledger.Tracks;
            into["hit_estimate.cache"] = _svc == null ? 0 : _svc.CacheCount;
        }

        public override void Dispose()
        {
            _api.HeLedger = null;
            _api.HitEstimate = null;
        }
    }
}
