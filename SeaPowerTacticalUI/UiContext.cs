using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge.Ui
{
    /// <summary>One Bind call recorded by UiContext.TestHost.</summary>
    internal sealed class UiBound
    {
        public readonly string Section, Key; public readonly UiKeyClass Class; public readonly object Default; public readonly UiSettingMeta Meta;
        public UiBound(string section, string key, UiKeyClass cls, object def, UiSettingMeta meta = null) { Section = section; Key = key; Class = cls; Default = def; Meta = meta; }
    }

    /// <summary>Shared state of the mod core: game API, host, owner plan and a tiny service registry (ADR-0044).</summary>
    internal sealed class UiContext
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public UiContext(GameApi api, IUiHost host)
        {
            Api = api;
            Host = host;
            Hotkeys = new HotkeyRegistry();
            Release = new ReleaseRegistry();
            Steering = new UnitSteering();
            Settings = new UiSettingsRegistry(host == null ? (Action<string>)null : host.LogInfo, host == null ? (Action<string>)null : host.LogWarning);
            Settings.CapabilityPresent = c => Api != null && Api.Capabilities != null && Api.Capabilities.Contains(c);
            Mission = new MissionLifecycle(host == null ? (Action<string>)null : host.LogInfo);
            BlockWatch = new InputBlockWatch(host == null ? (Action<string>)null : host.LogWarning);
            Timing = new UiPumpTimer(System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency) { Enabled = true };
            Guard = new UiGuard(() => host == null ? 0 : host.RealTime, host == null ? (Action<string>)null : host.LogWarning);
        }

        /// <summary>T-124: module time of the frame loop; UiCore fills it, the heartbeat prints it.</summary>
        public UiPumpTimer Timing { get; internal set; }

        /// <summary>T-129: error counters of the frame loop; a module registers its slots in Init (ctx.Guard.Register("radar.pump", ctx.Guard.OwnerOf(Id))).</summary>
        public UiGuard Guard { get; private set; }

        /// <summary>T-130: the one mission-change detector; modules subscribe in Init (air_tick) or through GameApi.WireMission.</summary>
        public MissionLifecycle Mission { get; private set; }
        /// <summary>T-130: releases a mouse block that has been stuck for 30 s with an idle pointer.</summary>
        public InputBlockWatch BlockWatch { get; private set; }

        public GameApi Api { get; private set; }
        public IUiHost Host { get; private set; }
        public OwnershipPlan Plan { get; internal set; }       // null until the first Update
        public bool VoicePresent { get; internal set; }
        public ReleaseRegistry Release { get; private set; }
        /// <summary>T-138 / ADR-0054: who steers a unit (smart attack, SAR, evasion, intercept).</summary>
        public UnitSteering Steering { get; private set; }
        public HotkeyRegistry Hotkeys { get; private set; }
        /// <summary>T-144: every bound config key with its settings-window metadata (filled by UiCore around Bind).</summary>
        public UiSettingsRegistry Settings { get; private set; }

        /// <summary>A second Provide of the same T throws.</summary>
        public void Provide<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException("service");
            if (_services.ContainsKey(typeof(T))) throw new InvalidOperationException("service already provided: " + typeof(T).Name);
            _services[typeof(T)] = service;
        }

        /// <summary>null if the module is off or has not run Init yet.</summary>
        public T Get<T>() where T : class
        {
            object o;
            return _services.TryGetValue(typeof(T), out o) ? (T)o : null;
        }

        /// <summary>Path.Combine(Host.ConfigDir, "SeaPowerTacticalUI", sub).</summary>
        public string DataDir(string sub)
        {
            return Path.Combine(Path.Combine(Host.ConfigDir, "SeaPowerTacticalUI"), sub);
        }

        public static string SelfTest()
        {
            try
            {
                var ctx = new UiContext(null, new TestHost());
                if (ctx.Get<string>() != null) return "ctx: Get of an unknown service must be null";
                ctx.Provide<string>("a");
                if (ctx.Get<string>() != "a") return "ctx: Get after Provide";
                bool threw = false;
                try { ctx.Provide<string>("b"); } catch (InvalidOperationException) { threw = true; }
                if (!threw) return "ctx: second Provide must throw";
                if (ctx.Get<string>() != "a") return "ctx: second Provide must not replace";
                if (ctx.Get<object>() != null) return "ctx: Get is by exact type";
                string d = ctx.DataDir("identify");
                if (d != Path.Combine(Path.Combine("/cfg", "SeaPowerTacticalUI"), "identify")) return "ctx: DataDir = " + d;
                if (ctx.Plan != null || ctx.VoicePresent) return "ctx: Plan must start null";
                return null;
            }
            catch (Exception e) { return "ctx: " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>Minimal host for self-tests (also used by UiCore.SelfTest).</summary>
        internal sealed class TestHost : IUiHost
        {
            public readonly List<string> Info = new List<string>(), Warn = new List<string>(), Err = new List<string>();
            public Version Voice;   // returned for Ownership.VoiceGuid
            public Version Dev;     // returned for DiagHost.DevGuid
            public int Saves;          // one per changed Set outside Batch, one per closed Batch (like ConfigFile.SaveOnConfigSet)
            public int InBatch;
            private sealed class Setting<T> : IUiSetting<T>
            {
                private readonly TestHost _h; private T _v;
                public Setting(TestHost h, T v, T def) { _h = h; _v = v; Default = def; }
                public T Default { get; private set; }
                public event Action<T> Changed;
                public T Value
                {
                    get { return _v; }
                    set
                    {
                        if (EqualityComparer<T>.Default.Equals(_v, value)) return;
                        _v = value;
                        if (_h != null && _h.InBatch == 0) _h.Saves++;
                        var c = Changed; if (c != null) c(value);
                    }
                }
            }
            private sealed class BatchScope : IDisposable
            {
                private readonly TestHost _h; private bool _done;
                public BatchScope(TestHost h) { _h = h; h.InBatch++; }
                public void Dispose() { if (_done) return; _done = true; if (--_h.InBatch == 0) _h.Saves++; }
            }
            public IDisposable Batch() { return new BatchScope(this); }
            public static IUiSetting<bool> Flag(bool value) { return new Setting<bool>(null, value, value); }
            public readonly List<string> Calls = new List<string>();
            public readonly List<UiBound> Bound = new List<UiBound>();
            public IUiSetting<T> Bind<T>(string section, string key, T defaultValue, string description, UiKeyClass cls = UiKeyClass.Basic, UiSettingMeta meta = null)
            {
                Calls.Add("bind:" + section + "." + key);
                Bound.Add(new UiBound(section, key, cls, defaultValue, meta));
                return new Setting<T>(this, defaultValue, defaultValue);
            }
            public IList<string> PrepareConfig() { Calls.Add("prepare"); return new List<string>(); }
            public IList<string> FinishConfig(bool allBound) { Calls.Add("finish:" + allBound); return new List<string>(); }
            public void LogInfo(string m) { Info.Add(m); }
            public void LogWarning(string m) { Warn.Add(m); }
            public void LogError(string m) { Err.Add(m); }
            public string ConfigDir { get { return "/cfg"; } }
            public string RootDir { get { return "/root"; } }
            public string PluginDir { get { return null; } }
            public string GameVersion { get { return "g1"; } }
            public string UnityVersion { get { return "u1"; } }
            public double Time;
            public bool Pointer;   // PointerBusy
            public bool PointerBusy { get { return Pointer; } }
            public double RealTime { get { return Time; } }
            public Version FindPluginVersion(string guid) { return guid == Ownership.VoiceGuid ? Voice : guid == DiagHost.DevGuid ? Dev : null; }
        }
    }
}
