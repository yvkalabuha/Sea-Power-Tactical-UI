using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// Composition root of the UI mod (T-118, ADR-0044): creates GameApi and the modules, binds config, runs the owner decision
    /// and the module life cycle. No BepInEx / Unity types: the host comes in through IUiHost.
    /// </summary>
    internal sealed class UiCore
    {
        private readonly IUiHost _host;
        private readonly UiContext _ctx;
        private readonly IList<IUiFeature> _features;
        private readonly bool[] _alive;
        private readonly bool[] _disposed;                                   // T-129: already disposed by DisableModule
        private readonly int _slotMission;                                  // T-130: UiGuard slot of the mission detector / block watchdog
        private readonly int[] _slotUpdate, _slotGui;                        // T-129: UiGuard slots of the frame loop
        private readonly List<string> _degraded = new List<string>();        // T-129: modules the frame loop switched off
        private readonly Action _unregister;
        private readonly Func<UiContext, DiagSources> _diag;
        private IList<string> _initOrder;
        private bool _started;
        private readonly HashSet<string> _failed = new HashSet<string>();    // modules whose Init crashed
        private readonly HashSet<string> _skipped = new HashSet<string>();   // modules without Init: every function of theirs is off (T-120)

        private UiCore(IUiHost host, UiContext ctx, IList<IUiFeature> features, Action unregister, Func<UiContext, DiagSources> diag = null)
        {
            _diag = diag; _host = host; _ctx = ctx; _features = features; _unregister = unregister;
            _alive = new bool[features.Count];
            _disposed = new bool[features.Count];
            var ids = new string[features.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = features[i].Id;
            ctx.Guard.SetOwners(ids);
            ctx.Timing.SetOwners(ids);
            _slotMission = ctx.Guard.Register("mission.tick", -1);
            _slotUpdate = new int[ids.Length]; _slotGui = new int[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                _slotUpdate[i] = ctx.Guard.Register(ids[i] + ".update", i);
                _slotGui[i] = ctx.Guard.Register(ids[i] + ".gui", i);
            }
            for (int i = 0; i < _alive.Length; i++) _alive[i] = true;
            string err;
            var order = InitOrderOf(features, out err);
            if (err != null)
            {
                host.LogError("module order: " + err + "; falling back to registration order");
                order = new List<string>();
                foreach (var f in features) order.Add(f.Id);
            }
            _initOrder = order;
        }

        /// <summary>T-144: what the modules see as their config; every Bind lands in the settings registry first.</summary>
        private sealed class RegistryConfig : IUiConfig
        {
            private readonly IUiHost _host; private readonly UiSettingsRegistry _reg;
            public RegistryConfig(IUiHost host, UiSettingsRegistry reg) { _host = host; _reg = reg; }
            public IUiSetting<T> Bind<T>(string section, string key, T defaultValue, string description, UiKeyClass cls = UiKeyClass.Basic, UiSettingMeta meta = null)
            {
                return _reg.Track(section, key, description, cls, meta, _host.Bind(section, key, defaultValue, description, cls, meta));
            }
        }

        public bool Enabled { get; private set; }
        public IList<string> InitOrder { get { return _initOrder; } }
        public UiContext Context { get { return _ctx; } }

        /// <summary>T-120: what is off / reduced after a game update; set by EnsureStarted. The module "notice" shows UiDegradation.PlayerLine of it.</summary>
        public DegradeResult Degradation { get; private set; } = new DegradeResult();

        /// <summary>Collects the spec sets of the modules (IUiFeature.SelfCheck) for GameApi.UiSpecs.</summary>
        private sealed class SpecCollector : ISelfCheck
        {
            public readonly List<SpecSet> Sets = new List<SpecSet>();
            public void Specs(SpecSet set) { if (set != null && !Sets.Exists(x => x.Module == set.Module)) Sets.Add(set); }
        }

        /// <summary>Awake: GameApi + context, core keys, modules, Bind, GameApi.SelfCheck, link slot.</summary>
        public static UiCore Start(IUiHost host, Func<UiContext, IList<IUiFeature>> compose, Func<UiContext, DiagSources> diag = null)
        {
            var api = new GameApi();
            var ctx = new UiContext(api, host);
            // T-123: upgrade the stored file before the first Bind (Config.Reload is only safe while no entry is bound)
            try { foreach (var line in host.PrepareConfig() ?? new List<string>()) host.LogInfo("config: " + line); }
            catch (Exception e) { host.LogError("config prepare crashed: " + e); }
            // T-144: every Bind goes through the settings registry; the core keys belong to the owner "core", section "general"
            var registry = ctx.Settings;
            registry.CurrentOwner = "core";
            IUiConfig cfg = new RegistryConfig(host, registry);
            var allowAttack = cfg.Bind("Attack", "AllowAttack", true, "false = the mod refuses weapon commands (attack)",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Section = "general" });
            api.AllowAttack = allowAttack.Value;
            registry.OnChanged("core", allowAttack, v => api.AllowAttack = v);
            var leg = cfg.Bind("Attack", "NeutralSeekerLegNm", 10.0, "Attack guard: how many miles before the target the missile seeker is assumed to be active; neutrals near that corridor need confirm_neutral (1..40)", UiKeyClass.Advanced,
                new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Section = "general", Min = 1, Max = 40 });
            api.NeutralSeekerLegNm = Math.Max(1.0, Math.Min(40.0, leg.Value));
            registry.OnChanged("core", leg, v => api.NeutralSeekerLegNm = Math.Max(1.0, Math.Min(40.0, v)));
            api.Clock = () => host.RealTime;
            api.Log = m => host.LogInfo(m);
            cfg.Bind("Ui", "ConfigVersion", UiConfigMigrator.Current, "Config schema version, written by the mod (do not edit)", UiKeyClass.Internal);
            var pumpTiming = cfg.Bind("Ui", "PumpTiming", true, "Heartbeat: add ms=<avg>/<max> top=<module>:<avg>/<max> (time of the mod's modules per frame)", UiKeyClass.Advanced,
                new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Section = "general" });
            ctx.Timing.Enabled = pumpTiming.Value;
            registry.OnChanged("core", pumpTiming, v => ctx.Timing.Enabled = v);
            api.UiRelease = ctx.Release;
            var features = compose(ctx) ?? new List<IUiFeature>();
            var core = new UiCore(host, ctx, features, UiLinkHost.Unregister, diag);
            for (int i = 0; i < features.Count; i++)
            {
                registry.CurrentOwner = features[i].Id;
                try { features[i].Bind(cfg); }
                catch (Exception e) { core._alive[i] = false; core._failed.Add(features[i].Id); host.LogError(features[i].Id + " bind crashed: " + e); }
                var sec = features[i] as IUiSettingsSection;
                if (sec != null) { try { registry.SetSectionOrder(features[i].Id, sec.SettingsOrder); } catch { } }
                var app = features[i] as IUiSettingsApplied;
                if (app != null) registry.SetApplied(features[i].Id, app);
            }
            registry.CurrentOwner = null;
            bool allBound = true;
            for (int i = 0; i < core._alive.Length; i++) if (!core._alive[i]) allBound = false;
            try { foreach (var line in host.FinishConfig(allBound) ?? new List<string>()) host.LogInfo("config: removed unused key " + line); }
            catch (Exception e) { host.LogError("config finish crashed: " + e); }
            try
            {
                var collector = new SpecCollector();
                core.SelfCheck(collector);
                if (collector.Sets.Count > 0) api.UiSpecs = collector.Sets;
            }
            catch (Exception e) { host.LogError("spec collection crashed: " + e); }
            try { api.SelfCheck(); }
            catch (Exception e) { host.LogError("self-check crashed: " + e); }
            // The link slot exists from Awake; the owner decision (Ownership.Plan) is taken in the first Update
            // (or by the first link call), when Chainloader.PluginInfos is complete.
            UiLinkHost.Register(api, () => UiLinkHost.CapsInner(api), core.EnsureStarted);
            return core;
        }

        /// <summary>First-Update decision: who owns tactics / presses Ignore, or "voice mod too old, stay off". Idempotent.</summary>
        public void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            Version voiceVer = _host.FindPluginVersion(Ownership.VoiceGuid);
            _ctx.VoicePresent = voiceVer != null;
            var plan = Ownership.Plan(ModSide.Ui, voiceVer, null, false);
            if (plan.Warning != null) _host.LogWarning(plan.Warning);
            if (!plan.Enabled)
            {
                Enabled = false;
                _unregister();
                return;
            }
            Enabled = true;
            _ctx.Plan = plan;
            _host.LogInfo("owner: tactics=ui, voice=" + (voiceVer == null ? "none" : voiceVer.ToString()));
            _ctx.Provide<DegradeView>(new DegradeView(() => Degradation));   // T-120 M21: the module "notice" shows the line once
            var api = _ctx.Api;
            Degradation = UiDegradation.Evaluate(api.Registry == null ? null : api.Registry.MissingCaps, _failed);
            UiDegradation.Apply(api.Capabilities, Degradation);
            foreach (var id in _initOrder)
            {
                int i = IndexOf(id);
                if (i < 0 || !_alive[i]) continue;
                if (Degradation.SkipInit.Contains(id))
                {
                    _alive[i] = false; _skipped.Add(id);
                    var caps = new List<string>();
                    foreach (var row in Degradation.Off) if (row.Module == id) foreach (var n in row.Needs) if (!caps.Contains(n)) caps.Add(n);
                    _host.LogWarning(id + ": skipped (degraded: " + string.Join(",", caps.ToArray()) + ")");
                    continue;
                }
                try { _features[i].Init(_ctx); }
                catch (Exception e)
                {
                    _alive[i] = false; _failed.Add(id);
                    _host.LogError(id + " init crashed: " + e);
                    // the function is dead although its capability is there: take the capability away through the table
                    Degradation = UiDegradation.Evaluate(api.Registry == null ? null : api.Registry.MissingCaps, _failed);
                    UiDegradation.Apply(api.Capabilities, Degradation);
                }
            }
            foreach (var c in api.Checks)
            {
                if (!c.Present) _host.LogWarning("MISSING " + c.Member + " (" + c.Capability + (c.Required ? ", required" : ", optional") + ")");
            }
            foreach (var line in Degradation.LogLines) _host.LogWarning(line);
            _host.LogInfo("game " + _host.GameVersion + " unity " + _host.UnityVersion +
                          " capabilities: " + string.Join(",", new List<string>(_ctx.Api.Capabilities).ToArray()));
            try { api.WireMission(_ctx.Mission); }
            catch (Exception e) { _host.LogError("mission wiring failed: " + e); }
            RegisterDiag();
        }

        /// <summary>ADR-0046: the read-only diag slot exists only when the dev mod is installed (and the UI is enabled).</summary>
        private void RegisterDiag()
        {
            if (_diag == null || _host.FindPluginVersion(DiagHost.DevGuid) == null) return;
            try
            {
                var src = _diag(_ctx) ?? new DiagSources();
                src.Modules = ModulesJson;
                var live = new List<IUiDiag>();
                for (int i = 0; i < _features.Count; i++)
                {
                    var d = _features[i] as IUiDiag;
                    if (d != null && _alive[i]) live.Add(d);
                }
                DiagHost.Register(src, live, ModInfo.Version);
                _host.LogInfo("diag slot: on");
            }
            catch (Exception e) { _host.LogError("diag slot failed: " + e); }
        }

        internal string ModulesJson()
        {
            var sb = new StringBuilder("{\"enabled\":").Append(Json.Bool(Enabled)).Append(",\"init_order\":[");
            sb.Append(string.Join(",", _initOrder.Select(x => Json.Str(x)).ToArray())).Append("],\"failed\":[");
            var failed = new List<string>();
            var skipped = new List<string>();
            for (int i = 0; i < _features.Count; i++)
            {
                if (_skipped.Contains(_features[i].Id)) skipped.Add(Json.Str(_features[i].Id));
                else if (!_alive[i]) failed.Add(Json.Str(_features[i].Id));
            }
            return sb.Append(string.Join(",", failed.ToArray())).Append("],\"skipped\":[").Append(string.Join(",", skipped.ToArray())).Append("],\"degraded\":[")
                     .Append(string.Join(",", _degraded.Select(x => Json.Str(x)).ToArray())).Append("],\"errors\":{")
                     .Append(string.Join(",", _ctx.Guard.Counts().Select(kv => Json.Str(kv.Key) + ":" + kv.Value).ToArray())).Append("}}").ToString();
        }

        private int IndexOf(string id)
        {
            for (int i = 0; i < _features.Count; i++) if (_features[i].Id == id) return i;
            return -1;
        }

        private long _frameNo;   // Update frames; OnGUI is called several times per frame and counts failures per frame (UiGuard.EndGui)
        private string[] _stageUpdate, _stageGui;

        /// <summary>Watchdog breadcrumb (T-154 F2); strings are built once per module, no per-frame garbage.</summary>
        private string StageOf(int i, bool gui)
        {
            if (_stageUpdate == null || _stageUpdate.Length != _features.Count) { _stageUpdate = new string[_features.Count]; _stageGui = new string[_features.Count]; }
            var a = gui ? _stageGui : _stageUpdate;
            return a[i] ?? (a[i] = _features[i].Id + (gui ? "/gui" : "/update"));
        }

        /// <summary>EnsureStarted, then the modules in registration order. A module that throws is skipped for this frame; the error goes to UiGuard
        /// (log, counter), and a module that fails for UiGuard.DisableFrames frames in a row is switched off like one whose Init crashed (T-129).</summary>
        public void Update(UiFrame frame)
        {
            EnsureStarted();
            if (!Enabled) return;
            var guard = _ctx.Guard;
            _frameNo++;
            DrawGuard.FrameStart(_frameNo);
            guard.BeginFrame(_frameNo);
            var timing = _ctx.Timing;
            timing.BeginFrame();
            long tm0 = timing.Start();
            TickMission(frame);
            timing.StopMission(tm0);
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_alive[i]) continue;
                DrawGuard.Stage = StageOf(i, false);
                guard.ClearFailed(i);
                long t0 = timing.Start();
                try { _features[i].Update(frame); }
                catch (Exception e) { guard.Fail(_slotUpdate[i], e); }
                finally { timing.StopUpdate(i, t0); }
                string why;
                if (guard.EndModule(i, guard.FailedThisFrame(i), out why)) DisableModule(i, "update", why);
            }
            DrawGuard.Mark(DrawGuard.MarkUpdateEnd);
        }

        /// <summary>T-130: the mission detector and the input-block watchdog run before the modules, so Left/Entered land before any pump of the frame.</summary>
        private void TickMission(UiFrame frame)
        {
            var api = _ctx.Api;
            try
            {
                _ctx.Mission.Tick(api.MissionRef(), () => api.LoadedSavePath(), () => api.MissionTimeS(), frame.RealTime);   // T-198: the loaded file, not the last saved one
                api.WorldPrime(frame.RealTime);   // T-348: the world flag of AswMath
                if (_ctx.BlockWatch.Tick(api.InputBlocked(), _host.PointerBusy || api.InputArmed(), frame.RealTime, api.InputBlockOwners())) api.ReleaseInputBlocks();
            }
            catch (Exception e) { _ctx.Guard.Fail(_slotMission, e); }
        }

        /// <summary>A game menu is open (pause / options / confirm / briefing / After Action Report): no module draws (set each frame by SettingsFeature).</summary>
        internal static bool GuiHidden;

        public void OnGUI()
        {
            if (!Enabled || GuiHidden) return;
            var guard = _ctx.Guard;
            var timing = _ctx.Timing;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_alive[i]) continue;
                DrawGuard.Stage = StageOf(i, true);
                guard.ClearFailed(i);
                long t0 = timing.Start();
                try { _features[i].OnGUI(); }
                catch (Exception e) { guard.Fail(_slotGui[i], e); }
                finally { timing.StopGui(i, t0); }
                string why;
                if (guard.EndGui(i, guard.FailedThisFrame(i), _frameNo, out why)) DisableModule(i, "gui", why);
            }
            DrawGuard.Mark(DrawGuard.MarkGuiEnd);
        }

        /// <summary>T-129: the same way as a crashed Init: the module is dead, its capability goes through the UiDegradation table.</summary>
        private void DisableModule(int i, string phase, string why)
        {
            string id = _features[i].Id;
            _alive[i] = false; _failed.Add(id); _degraded.Add(id);
            var api = _ctx.Api;
            Degradation = UiDegradation.Evaluate(api.Registry == null ? null : api.Registry.MissingCaps, _failed);
            UiDegradation.Apply(api.Capabilities, Degradation);
            _host.LogError(id + ": turned off after " + why + " (" + phase + ")");
            if (!_disposed[i])
            {
                _disposed[i] = true;
                try { _features[i].Dispose(); }   // releases window mouse blocks and subscriptions
                catch (Exception e) { _host.LogError(id + " dispose crashed: " + e); }
                _ctx.Settings.DropOwner(id);
            }
            try { api.UiEvent("feature_degraded", "{\"id\":" + Json.Str(id) + ",\"phase\":" + Json.Str(phase) + ",\"reason\":" + Json.Str(why) + "}"); }
            catch (Exception e) { _host.LogError("feature_degraded event failed: " + e.Message); }
        }

        /// <summary>OnDestroy: unregister the link slot, then Dispose of every module in registration order.</summary>
        public void Dispose()
        {
            _unregister();
            DiagHost.Unregister();
            for (int i = 0; i < _features.Count; i++)
            {
                if (_disposed[i]) continue;
                try { _features[i].Dispose(); }
                catch (Exception e) { _host.LogError(_features[i].Id + " dispose crashed: " + e); }
                _ctx.Settings.DropOwner(_features[i].Id);
            }
        }

        /// <summary>Entry point of T-120: every module reports its members.</summary>
        public void SelfCheck(ISelfCheck check)
        {
            foreach (var f in _features) f.SelfCheck(check);
        }

        /// <summary>Stable topological order (Kahn, ties by registration index). error != null: duplicate id, unknown id, cycle.</summary>
        public static List<string> InitOrderOf(IList<IUiFeature> features, out string error)
        {
            error = null;
            int n = features.Count;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                if (index.ContainsKey(features[i].Id)) { error = "duplicate module id '" + features[i].Id + "'"; return null; }
                index[features[i].Id] = i;
            }
            var indeg = new int[n];
            var users = new List<int>[n];
            for (int i = 0; i < n; i++) users[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                foreach (var d in features[i].DependsOn ?? new string[0])
                {
                    int j;
                    if (!index.TryGetValue(d, out j)) { error = "module '" + features[i].Id + "' depends on unknown id '" + d + "'"; return null; }
                    users[j].Add(i); indeg[i]++;
                }
            }
            var done = new bool[n];
            var res = new List<string>();
            for (int step = 0; step < n; step++)
            {
                int pick = -1;
                for (int i = 0; i < n; i++) if (!done[i] && indeg[i] == 0) { pick = i; break; }
                if (pick < 0)
                {
                    var left = new List<string>();
                    for (int i = 0; i < n; i++) if (!done[i]) left.Add(features[i].Id);
                    error = "dependency cycle among: " + string.Join(", ", left.ToArray());
                    return null;
                }
                done[pick] = true;
                res.Add(features[pick].Id);
                foreach (int u in users[pick]) indeg[u]--;
            }
            return res;
        }

        // ------------------------------------------------------------------ self-test

        private sealed class Fake : UiFeatureBase
        {
            private readonly string _id; private readonly string[] _deps; private readonly List<string> _log;
            public bool ThrowInit, ThrowUpdate;
            public Fake(UiContext ctx, string id, List<string> log, params string[] deps) : base(ctx) { _id = id; _deps = deps; _log = log; }
            public override string Id { get { return _id; } }
            public override string[] DependsOn { get { return _deps; } }
            public override void Bind(IUiConfig config) { _log.Add("bind:" + _id); }
            public override void Init(UiContext ctx) { _log.Add("init:" + _id); if (ThrowInit) throw new InvalidOperationException("init boom"); }
            public override void Update(UiFrame frame) { _log.Add("update:" + _id); if (ThrowUpdate) throw new InvalidOperationException("update boom"); }
            public override void OnGUI() { _log.Add("gui:" + _id); }
            public override void Dispose() { _log.Add("dispose:" + _id); }
        }

        private static IList<IUiFeature> Make(params string[] specs)
        {
            // "a" or "a>b,c" (a depends on b and c)
            var l = new List<IUiFeature>();
            var log = new List<string>();
            foreach (var s in specs)
            {
                int gt = s.IndexOf('>');
                string id = gt < 0 ? s : s.Substring(0, gt);
                string[] deps = gt < 0 ? new string[0] : s.Substring(gt + 1).Split(',');
                l.Add(new Fake(null, id, log, deps));
            }
            return l;
        }

        private static string Order(params string[] specs)
        {
            string e;
            var o = InitOrderOf(Make(specs), out e);
            return e != null ? "ERR " + e : string.Join(",", o.ToArray());
        }

        /// <summary>The module table of plan T-118 section 4.3: registration order and dependencies.</summary>
        private static readonly string[] Table =
        {
            "dialogs", "tactics", "doctrine", "text", "selection", "radar", "overview", "radial", "menu", "evasion",
            "asw>radar,sar", "cap>radar,asw", "air_tick>asw,cap", "radar_pump>radar", "map_layer", "map_menu>asw,cap",
            "identify", "sar", "smart_attack", "heartbeat>radar,map_menu", "harmony>menu,radial,doctrine,identify,smart_attack", "drain",
        };

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                if (Order("asw>sar", "sar") != "sar,asw") return "core: asw>sar = " + Order("asw>sar", "sar");
                if (Order("a", "b", "c") != "a,b,c") return "core: no deps = " + Order("a", "b", "c");
                if (Order("a>c", "b", "c") != "b,c,a") return "core: tie by registration = " + Order("a>c", "b", "c");
                string t = Order(Table);
                const string want = "dialogs,tactics,doctrine,text,selection,radar,overview,radial,menu,evasion,radar_pump,map_layer,identify,sar,asw,cap,air_tick,map_menu,smart_attack,heartbeat,harmony,drain";
                if (t != want) return "core: module table order = " + t;
                string c = Order("a>b", "b>a");
                if (!c.StartsWith("ERR") || !c.Contains("a") || !c.Contains("b") || !c.Contains("cycle")) return "core: cycle = " + c;
                string u = Order("a>nope");
                if (!u.StartsWith("ERR") || !u.Contains("nope")) return "core: unknown id = " + u;
                string d = Order("a", "a");
                if (!d.StartsWith("ERR") || !d.Contains("duplicate")) return "core: duplicate = " + d;

                string r = LifecycleTest();
                if (r != null) return r;
                r = ConfigOrderTest();
                if (r != null) return r;
                r = SettingsWiringTest();
                if (r != null) return r;
                r = DegradationTest();
                if (r != null) return r;
                r = GuardTest();
                if (r != null) return r;
                r = TimingTest();
                if (r != null) return r;
#if CORETEST
                r = NoAutoIgnoreTest();
                if (r != null) return r;
                r = Ownership.SelfTest();   // T-306: the UI side never presses Ignore
                if (r != null) return "ownership: " + r;
                r = MissionCoreTest();   // needs the fake GameApi of the coretest stand
                if (r != null) return r;
#endif
                return null;
            }
            catch (Exception e) { return "core: " + e.GetType().Name + ": " + e.Message; }
        }

        private sealed class DegSpecs
        {
            public static readonly MemberSpec Evasion = MemberSpec.Field("evasion_own", "T", "x");
            public static readonly MemberSpec SubEvasion = MemberSpec.Field("sub_evasion", "T", "w");   // T-101: the evasion module also serves submarines
            public static readonly MemberSpec Standing = MemberSpec.Field("standing_orders", "T", "y");
            public static readonly MemberSpec Doctrine = MemberSpec.Field("air_defense_ready", "T", "z");
        }

        // T-306: the UI mod binds no [Mission] key and its owner plan never presses Ignore
#if CORETEST
        private static string NoAutoIgnoreTest()
        {
            var host = new UiContext.TestHost();
            UiCore.Start(host, ctx => new List<IUiFeature>());
            if (host.Bound.Exists(b => b.Section == "Mission")) return "core: the UI mod must not bind a [Mission] key";
            var host2 = new UiContext.TestHost();
            var ctx2 = new UiContext(new GameApi(), host2);
            var core = new UiCore(host2, ctx2, new List<IUiFeature>(), () => { });
            core.EnsureStarted();
            if (ctx2.Plan == null || !ctx2.Plan.TacticsOwner || ctx2.Plan.PressIgnore) return "core: no voice mod, the UI plan must not press Ignore";
            if (host2.Info.Exists(x => x.Contains("press_ignore"))) return "core: owner line must not mention press_ignore";
            return null;
        }
#endif

        // T-120: a module whose every function is off gets no Init; a module with a live function does; a crashed Init takes its capability away
        private static string DegradationTest()
        {
            var log = new List<string>();
            var host = new UiContext.TestHost();
            var api = new GameApi();
            var ctx = new UiContext(api, host);
            var set = SpecSet.Of("deg", typeof(DegSpecs));
            api.SetRegistry(RegistryReport.Build(new[] { set }, api, s => s != DegSpecs.Evasion && s != DegSpecs.SubEvasion && s != DegSpecs.Standing, p => true));
            foreach (var c in new[] { "evasion_own", "sub_evasion", "identify", "air_defense_ready" }) api.Capabilities.Add(c);
            var evasion = new Fake(ctx, "evasion", log);
            var doctrine = new Fake(ctx, "doctrine", log);
            var identify = new Fake(ctx, "identify", log) { ThrowInit = true };
            var core = new UiCore(host, ctx, new List<IUiFeature> { evasion, doctrine, identify }, () => { });
            core.EnsureStarted();
            if (string.Join(",", log.ToArray()) != "init:doctrine,init:identify") return "core: degraded init calls = " + string.Join(",", log.ToArray());
            if (!host.Warn.Exists(w => w.StartsWith("evasion: skipped (degraded: evasion_own", StringComparison.Ordinal))) return "core: skip line, warnings = " + string.Join("|", host.Warn.ToArray());
            var d = core.Degradation;
            string off = string.Join(",", d.Off.Select(x => x.Id).OrderBy(x => x).ToArray());
            string red = string.Join(",", d.Reduced.Select(x => x.Id).ToArray());
            if (off != "evasion_own,identify,sub_evasion" || red != "standing_orders") return "core: Degradation off=" + off + " reduced=" + red;
            if (api.Capabilities.Contains("evasion_own") || api.Capabilities.Contains("sub_evasion") || api.Capabilities.Contains("identify") || !api.Capabilities.Contains("air_defense_ready")) return "core: capabilities after degradation = " + string.Join(",", api.Capabilities.OrderBy(x => x).ToArray());
            if (!host.Warn.Exists(w => w.StartsWith("degraded: identify off"))) return "core: degradation log lines = " + string.Join("|", host.Warn.ToArray());
            log.Clear();
            core.Update(new UiFrame());
            if (string.Join(",", log.ToArray()) != "update:doctrine") return "core: update of a degraded set = " + string.Join(",", log.ToArray());
            string ms = core.ModulesJsonForTest();
            if (!ms.Contains("\"failed\":[\"identify\"]") || !ms.Contains("\"skipped\":[\"evasion\"]")) return "core: modules json = " + ms;
            // no registry (nothing collected): nothing is degraded
            var ctx2 = new UiContext(new GameApi(), host);
            var core2 = new UiCore(host, ctx2, new List<IUiFeature> { new Fake(ctx2, "evasion", new List<string>()) }, () => { });
            core2.EnsureStarted();
            if (core2.Degradation.Any) return "core: no registry must degrade nothing";
            return null;
        }

        internal string ModulesJsonForTest() { return ModulesJson(); }

        private sealed class BindFake : UiFeatureBase
        {
            private readonly string _id; private readonly bool _throw;
            private const string TestSection = "Fake"; // selftest only; not a real config section
            public BindFake(UiContext ctx, string id, bool thr) : base(ctx) { _id = id; _throw = thr; }
            public override string Id { get { return _id; } }
            public override void Bind(IUiConfig config) { config.Bind(TestSection, _id, 1, "d", UiKeyClass.Advanced); if (_throw) throw new InvalidOperationException("boom"); }
        }

        private sealed class SecFake : UiFeatureBase, IUiSettingsSection, IUiSettingsApplied
        {
            public readonly List<string> Seen = new List<string>();
            public SecFake(UiContext ctx) : base(ctx) { }
            public override string Id { get { return "sec"; } }
            public int SettingsOrder { get { return 55; } }
            public void SettingsApplied(ICollection<string> keys) { Seen.Add("applied:" + string.Join(",", keys.ToArray())); }
            public override void Bind(IUiConfig config)
            {
                string sect = "Sec", key = "Level";
                var s = config.Bind(sect, key, 3, "d", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 1, Max = 9 });
                OnChanged(s, v => Seen.Add("level:" + v));
            }
        }

        // T-144 R3: Bind of a module lands in ctx.Settings, core keys belong to "core"/"general", Dispose drops the subscriptions
        private static string SettingsWiringTest()
        {
            var host = new UiContext.TestHost();
            SecFake sec = null;
            var core = UiCore.Start(host, ctx => { sec = new SecFake(ctx); return new List<IUiFeature> { new BindFake(ctx, "x", false), sec }; });
            var reg = core.Context.Settings;
            UiSettingEntry lvl = null, fake = null, allow = null, ver = null;
            foreach (var e in reg.All)
            {
                if (e.FullKey == "Sec.Level") lvl = e;
                else if (e.FullKey == "Fake.x") fake = e;
                else if (e.FullKey == "Attack.AllowAttack") allow = e;
                else if (e.FullKey == "Ui.ConfigVersion") ver = e;
            }
            if (lvl == null || fake == null || allow == null || ver == null) return "core: registry misses keys, has " + reg.All.Count;
            if (lvl.Owner != "sec" || lvl.Section != "sec" || fake.Owner != "x" || fake.Section != "x") return "core: module owner/section";
            if (allow.Owner != "core" || allow.Section != UiSettingsRegistry.General || allow.Control != UiControl.Check) return "core: core key owner/section";
            if (ver.Meta != null || ver.Class != UiKeyClass.Internal) return "core: ConfigVersion has no meta";
            if (host.Bound.Find(b => b.Key == "Level").Meta == null) return "core: meta must reach the host Bind";
            var secs = string.Join(",", reg.Sections().ToArray());
            if (secs != "general,sec,x") return "core: sections (sec has order 55, x none) = " + secs;
            var rep = reg.Apply(host, new Dictionary<string, object> { { "Sec.Level", 5 } });
            if (rep.Live != 1 || string.Join("|", sec.Seen.ToArray()) != "level:5|applied:Sec.Level") return "core: Apply -> " + string.Join("|", sec.Seen.ToArray()) + " live " + rep.Live;
            core.Dispose();
            sec.Seen.Clear();
            host.Bound.Clear();
            reg.Apply(host, new Dictionary<string, object> { { "Sec.Level", 6 } });
            if (sec.Seen.Count != 0) return "core: Dispose must drop the subscriptions, got " + string.Join("|", sec.Seen.ToArray());
            return null;
        }

        // T-123: PrepareConfig before every Bind, FinishConfig after them; a module that crashes in Bind blocks the orphan cleanup.
        private static string ConfigOrderTest()
        {
            var host = new UiContext.TestHost();
            UiCore.Start(host, ctx => new List<IUiFeature> { new BindFake(ctx, "x", false), new BindFake(ctx, "y", false) });
            var calls = string.Join(",", host.Calls.ToArray());
            if (!calls.StartsWith("prepare,bind:Attack.AllowAttack,")) return "core: config order (start) = " + calls;
            if (!calls.EndsWith("bind:Fake.x,bind:Fake.y,finish:True")) return "core: config order (end) = " + calls;
            UiBound v = host.Bound.Find(b => b.Key == "ConfigVersion");
            if (v == null || v.Section != "Ui" || v.Class != UiKeyClass.Internal || !(v.Default is int) || (int)v.Default != UiConfigMigrator.Current) return "core: Ui.ConfigVersion must be Internal with default Current";
            UiBound leg = host.Bound.Find(b => b.Key == "NeutralSeekerLegNm");
            if (leg == null || leg.Class != UiKeyClass.Advanced) return "core: Attack.NeutralSeekerLegNm must be Advanced";
            foreach (var key in new[] { "AllowAttack" })
            {
                UiBound b = host.Bound.Find(x => x.Key == key);
                if (b == null || b.Class != UiKeyClass.Basic) return "core: " + key + " must be Basic";
            }
            var hostF = new UiContext.TestHost();
            UiCore.Start(hostF, ctx => new List<IUiFeature> { new BindFake(ctx, "x", false), new BindFake(ctx, "y", true) });
            if (!hostF.Calls.Contains("finish:False")) return "core: a crashed Bind must give finish:False, got " + string.Join(",", hostF.Calls.ToArray());
            return null;
        }

        // T-129: a module that throws every frame is switched off after UiGuard.DisableFrames frames and DisableMinS seconds
#if CORETEST
        // T-130: the detector and the watchdog run before the modules; WireMission once after Init
        private static string MissionCoreTest()
        {
            var log = new List<string>();
            var host = new UiContext.TestHost();
            var api = new GameApi { Trace = log.Add };
            var ctx = new UiContext(api, host);
            var core = new UiCore(host, ctx, new List<IUiFeature> { new Fake(ctx, "a", log) }, () => { });
            var entered = new List<int>();
            ctx.Mission.Subscribe(1, "t", e => entered.Add(e.Generation), null);
            api.StubMission = new object();
            core.Update(new UiFrame());
            core.Update(new UiFrame());
            if (string.Join(",", log.ToArray()) != "init:a,wire,mission,update:a,mission,update:a") return "core: mission order = " + string.Join(",", log.ToArray());
            if (api.WireCalls != 1) return "core: WireMission once, got " + api.WireCalls;
            if (entered.Count != 1 || entered[0] != 1) return "core: Entered once";
            api.StubBlocked = true; host.Pointer = false;
            core.Update(new UiFrame { RealTime = 100 });
            core.Update(new UiFrame { RealTime = 129 });
            if (api.ReleaseCalls != 0) return "core: 29 s block must stay";
            core.Update(new UiFrame { RealTime = 131 });
            if (api.ReleaseCalls != 1) return "core: 31 s idle block must be released, calls=" + api.ReleaseCalls;
            api.StubBlocked = true; host.Pointer = true;
            core.Update(new UiFrame { RealTime = 200 }); core.Update(new UiFrame { RealTime = 300 });
            if (api.ReleaseCalls != 1) return "core: a busy pointer keeps the block";
            api.StubBlocked = true; host.Pointer = false; api.StubArmed = true;
            core.Update(new UiFrame { RealTime = 400 }); core.Update(new UiFrame { RealTime = 460 });
            if (api.ReleaseCalls != 1) return "core: an armed draw mode keeps the block (30 s idle)";
            return null;
        }

#endif

        /// <summary>T-124: a module whose Update / OnGUI advance the fake clock.</summary>
        private sealed class TimeFake : UiFeatureBase
        {
            private readonly string _id; private readonly long[] _clock; private readonly long _upd, _gui; public bool ThrowUpdate;
            public TimeFake(UiContext ctx, string id, long[] clock, long upd, long gui) : base(ctx) { _id = id; _clock = clock; _upd = upd; _gui = gui; }
            public override string Id { get { return _id; } }
            public override void Update(UiFrame frame) { _clock[0] += _upd; if (ThrowUpdate) throw new InvalidOperationException("time boom"); }
            public override void OnGUI() { _clock[0] += _gui; }
        }

        // T-124: the frame loop times every module (Update + OnGUI of a frame add up); a module that throws is timed too; PumpTiming=false stops it
        private static string TimingTest()
        {
            var clock = new long[] { 100 };
            var host = new UiContext.TestHost();
            var ctx = new UiContext(new GameApi(), host);
            ctx.Timing = new UiPumpTimer(() => clock[0], 1000) { Enabled = true };
            var a = new TimeFake(ctx, "a", clock, 2, 1);
            var b = new TimeFake(ctx, "b", clock, 4, 0) { ThrowUpdate = true };
            var core = new UiCore(host, ctx, new List<IUiFeature> { a, b }, () => { });
            core.EnsureStarted();
            for (int n = 0; n < 3; n++) { core.Update(new UiFrame { RealTime = n }); core.OnGUI(); core.OnGUI(); }
            ctx.Timing.BeginFrame();
            var snap = ctx.Timing.Snapshot();
            if (snap[0].Key != "a" || Math.Abs(snap[0].Value[0] - 4.0) > 1e-9 || snap[0].Value[2] != 3) return "core: timing a = " + snap[0].Value[0] + " frames " + snap[0].Value[2];
            if (snap[1].Key != "b" || Math.Abs(snap[1].Value[0] - 4.0) > 1e-9) return "core: a module that throws must be timed, got " + snap[1].Value[0];
            if (!ctx.Timing.HeartbeatText().StartsWith("8.00/8.00 top=")) return "core: timing text";
            ctx.Timing.Enabled = false;
            core.Update(new UiFrame()); core.OnGUI();
            if (ctx.Timing.HeartbeatText() != "off") return "core: PumpTiming=false must show off";
            var host2 = new UiContext.TestHost();
            UiCore.Start(host2, c2 => new List<IUiFeature>());
            UiBound pt = host2.Bound.Find(x => x.Key == "PumpTiming");
            if (pt == null || pt.Section != "Ui" || pt.Class != UiKeyClass.Advanced || !(pt.Default is bool) || !(bool)pt.Default) return "core: Ui.PumpTiming must be Advanced, default true";
            return null;
        }

        private static string GuardTest()
        {
            var log = new List<string>();
            var host = new UiContext.TestHost();
            var ctx = new UiContext(new GameApi(), host);
            var a = new Fake(ctx, "a", log);
            var c = new Fake(ctx, "c", log) { ThrowUpdate = true };
            var core = new UiCore(host, ctx, new List<IUiFeature> { a, c }, () => { });
            core.EnsureStarted();
            log.Clear();
            core.Update(new UiFrame());
            if (host.Warn.Count(w => w.StartsWith("c.update failed: InvalidOperationException: update boom at ")) != 1) return "core: first error of c must give one line, got " + string.Join("|", host.Warn.ToArray());
            for (int n = 1; n < UiGuard.DisableFrames - 1; n++) { host.Time += 0.05; core.Update(new UiFrame()); }
            if (host.Err.Exists(x => x.Contains("turned off"))) return "core: c switched off too early";
            host.Time += 0.05; core.Update(new UiFrame());
            if (!host.Err.Exists(x => x.StartsWith("c: turned off after 300 errors in a row"))) return "core: c must be switched off, errors = " + string.Join("|", host.Err.ToArray());
            if (!log.Contains("dispose:c") || log.Contains("dispose:a")) return "core: only c is disposed";
            log.Clear();
            core.Update(new UiFrame()); core.OnGUI();
            if (string.Join(",", log.ToArray()) != "update:a,gui:a") return "core: after switch-off = " + string.Join(",", log.ToArray());
            string mj = core.ModulesJson();
            if (!mj.Contains("\"failed\":[\"c\"]") || !mj.Contains("\"degraded\":[\"c\"]") || !mj.Contains("\"c.update\":300")) return "core: modules json = " + mj;
            log.Clear();
            core.Dispose();
            if (string.Join(",", log.ToArray()) != "dispose:a") return "core: no second Dispose for c, got " + string.Join(",", log.ToArray());
            // a crashed Bind counts as failed (its capability goes through the table)
            var host2 = new UiContext.TestHost();
            var c2 = UiCore.Start(host2, ctx2 => new List<IUiFeature> { new BindFake(ctx2, "x", true) });
            c2.EnsureStarted();
            if (!c2.ModulesJson().Contains("\"failed\":[\"x\"]")) return "core: a crashed Bind must be failed";
            return null;
        }

        private static string LifecycleTest()
        {
            var log = new List<string>();
            var host = new UiContext.TestHost();
            var ctx = new UiContext(new GameApi(), host);
            var fs = new List<IUiFeature>();
            var a = new Fake(ctx, "a", log, "b");
            var b = new Fake(ctx, "b", log) { ThrowInit = true };
            var c = new Fake(ctx, "c", log) { ThrowUpdate = true };
            fs.Add(a); fs.Add(b); fs.Add(c);
            int unreg = 0;
            var core = new UiCore(host, ctx, fs, () => unreg++);
            if (string.Join(",", new List<string>(core.InitOrder).ToArray()) != "b,a,c") return "core: lifecycle order";
            core.EnsureStarted();
            core.EnsureStarted();   // idempotent
            if (!core.Enabled || ctx.Plan == null || !ctx.Plan.TacticsOwner) return "core: must be enabled without a voice mod";
            if (string.Join(",", log.ToArray()) != "init:b,init:a,init:c") return "core: init calls = " + string.Join(",", log.ToArray());
            if (host.Err.Count != 1 || !host.Err[0].StartsWith("b init crashed")) return "core: init error line = " + string.Join("|", host.Err.ToArray());
            if (!host.Info.Exists(s => s.StartsWith("owner: tactics=ui, voice=none"))) return "core: owner line";
            log.Clear();
            core.Update(new UiFrame());
            core.Update(new UiFrame());
            core.OnGUI();
            // b failed in Init: no Update/OnGUI for it; c throws in Update but keeps getting called; a is unaffected
            if (string.Join(",", log.ToArray()) != "update:a,update:c,update:a,update:c,gui:a,gui:c") return "core: update/gui calls = " + string.Join(",", log.ToArray());
            log.Clear();
            // a game menu is open: Update keeps running, nothing is drawn
            GuiHidden = true;
            try { core.Update(new UiFrame()); core.OnGUI(); }
            finally { GuiHidden = false; }
            if (string.Join(",", log.ToArray()) != "update:a,update:c") return "core: game menu must stop OnGUI only = " + string.Join(",", log.ToArray());
            core.OnGUI();
            if (string.Join(",", log.ToArray()) != "update:a,update:c,gui:a,gui:c") return "core: OnGUI back after the menu = " + string.Join(",", log.ToArray());
            log.Clear();
            core.Dispose();
            if (string.Join(",", log.ToArray()) != "dispose:a,dispose:b,dispose:c") return "core: dispose order = " + string.Join(",", log.ToArray());
            if (unreg != 1) return "core: Dispose must unregister the link slot";

            // ADR-0046: the diag slot only with the dev mod, and only when the UI is enabled
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) != null) return "core: diag slot without the dev mod";
            var logD = new List<string>();
            var hostD = new UiContext.TestHost { Dev = new Version(0, 1, 0) };
            var ctxD = new UiContext(new GameApi(), hostD);
            var da = new Fake(ctxD, "a", logD);
            var db = new Fake(ctxD, "b", logD) { ThrowInit = true };
            var coreD = new UiCore(hostD, ctxD, new List<IUiFeature> { da, db }, () => { }, c => new DiagSources());
            coreD.EnsureStarted();
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) == null) return "core: diag slot must exist with the dev mod";
            if (!hostD.Info.Contains("diag slot: on")) return "core: diag slot log line";
            string ms = DiagHost.Query("{\"q\":\"state\",\"sections\":[\"modules\"]}");
            var mj = ((Json.Parse(ms) as Dictionary<string, object>)["sections"] as Dictionary<string, object>)["modules"] as Dictionary<string, object>;
            var failedL = mj["failed"] as List<object>;
            if (!(bool)mj["enabled"] || failedL.Count != 1 || (string)failedL[0] != "b" || ((List<object>)mj["init_order"]).Count != 2) return "core: modules json = " + ms;
            coreD.Dispose();
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) != null) return "core: Dispose must remove the diag slot";
            var hostN = new UiContext.TestHost();
            var coreN = new UiCore(hostN, new UiContext(new GameApi(), hostN), new List<IUiFeature>(), () => { }, c => new DiagSources());
            coreN.EnsureStarted();
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) != null) return "core: no dev mod, no diag slot";
            var hostO = new UiContext.TestHost { Dev = new Version(0, 1, 0), Voice = new Version(0, 13, 1) };
            var coreO = new UiCore(hostO, new UiContext(new GameApi(), hostO), new List<IUiFeature>(), () => { }, c => new DiagSources());
            coreO.EnsureStarted();
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) != null) return "core: disabled UI must not open the diag slot";

            // a voice mod older than 0.14: the whole mod stays off
            log.Clear();
            var host2 = new UiContext.TestHost { Voice = new Version(0, 13, 1) };
            var ctx2 = new UiContext(new GameApi(), host2);
            var a2 = new Fake(ctx2, "a", log);
            int unreg2 = 0;
            var core2 = new UiCore(host2, ctx2, new List<IUiFeature> { a2 }, () => unreg2++);
            core2.Update(new UiFrame());
            core2.OnGUI();
            if (core2.Enabled || log.Count != 0 || unreg2 != 1) return "core: old voice mod must keep the UI off";
            if (!ctx2.VoicePresent || host2.Warn.Count != 1) return "core: old voice mod warning";

            // voice 0.14+: enabled, VoicePresent
            var host3 = new UiContext.TestHost { Voice = new Version(0, 14, 0) };
            var ctx3 = new UiContext(new GameApi(), host3);
            var core3 = new UiCore(host3, ctx3, new List<IUiFeature>(), () => { });
            core3.EnsureStarted();
            if (!core3.Enabled || !ctx3.VoicePresent || ctx3.Plan.PressIgnore) return "core: voice 0.14 plan";
            return null;
        }
    }
}
