using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace VoiceBotBridge.Ui
{
    /// <summary>A short line every 2 s in BepInEx/voicebot_ui_heartbeat.log: the last line before a crash (config key Ui.Heartbeat).</summary>
    internal sealed class HeartbeatFeature : UiFeatureBase, IUiDiag
    {
        private readonly GameApi _api;
        private bool _heartbeatOn;
        private Heartbeat _hb;
        private IRadarView _radar;
        // T-154 F2 / T-302: main-thread watchdog and file writes on the dedicated thread sptui-hb; it never touches the main thread.
        private long _frameNo;
        private HbLoop _loop;
        private StageCrumb _crumb;
        private WorkerCrumb _workerCrumb;   // T-202: phase of the radar coverage worker thread
        // T-151: memory line once a minute in its own file (the heartbeat file rotates in ~96 min and loses the history)
        private Heartbeat _memLog;
        private readonly MemRing _ring = new MemRing(60);
        private MemWarn _warn;
        private MemSample _last;
        private double _warnGb = 2.0;
        private System.Diagnostics.Process _proc;
        // T-212: per-thread CPU of /proc, read at most once per 2 s (the previous reading is the base of the delta)
        private List<ProcThread> _procNow, _procPrev;
        private double _procNowT, _procPrevT;
        private string _procRoot; private bool _procRootKnown;
        private string _hotText = "-";
        // T-336: the heartbeat starts after the gate (first mission / settled main menu); /proc is read on sptui-hb and published as a snapshot
        private readonly StartupGate _gate = new StartupGate();
        private readonly StartupProbe _probe = new StartupProbe();
        private readonly ProcCadence _procCadence = new ProcCadence();
        private bool _sceneHooked, _hbStarted;
        private string _hbPath;
        private volatile ProcView _procView;
        private readonly List<KeyValuePair<string, ProfilerRecorder>> _rec = new List<KeyValuePair<string, ProfilerRecorder>>();
        private static readonly string[] RecorderNames = { "Total Used Memory", "Total Reserved Memory", "System Used Memory", "GC Reserved Memory", "Gfx Used Memory", "Texture Memory", "Mesh Memory" };

        /// <summary>Set by UiFeatures.All: every module is asked for MemProbe once a minute.</summary>
        internal IList<IUiFeature> Features;

        public HeartbeatFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "heartbeat"; } }
        public override string[] DependsOn { get { return new[] { "radar", "map_menu" }; } }
        private sealed class ProcView { public string Json; public string Hot; }

        public override void Bind(IUiConfig cfg)
        {
            _heartbeatOn = cfg.Bind("Ui", "Heartbeat", false, "Test diagnostics: write a short line every 2 s to BepInEx/voicebot_ui_heartbeat.log (the last line before a crash; rotates at 256 KB), the minute memory line and the stall thread table. Off by default", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" }).Value;
            _warnGb = Math.Max(0.1, cfg.Bind("Ui", "MemWarnGb", 2.0f, "[Mem] WARN in the BepInEx log when the process private memory grows by more than this many GiB in 10 minutes (or passes 60% of RAM); the minute line is in BepInEx/voicebot_ui_memory.log", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" }).Value);
        }

        public override void Init(UiContext ctx)
        {
            _radar = ctx.Get<IRadarView>();
            try
            {
                if (_heartbeatOn)
                {
                    string path = System.IO.Path.Combine(ctx.Host.RootDir, "voicebot_ui_heartbeat.log");
                    _hb = new Heartbeat(path, HeartbeatLine, 256 * 1024, 2.0, true);
                    _warn = new MemWarn(_warnGb * 1024.0, 600.0);
                    _memLog = new Heartbeat(System.IO.Path.Combine(ctx.Host.RootDir, "voicebot_ui_memory.log"), MemoryLine, 256 * 1024, 60.0, true);
                    _hbPath = path;
                    OpenCrumb(ctx);
                }
            }
            catch (Exception e) { ctx.Host.LogWarning("heartbeat off: " + e.Message); }
            HookScene();
        }

        private void HookScene()
        {
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _sceneHooked = true;
                for (int i = 1; i <= 2; i++)
                    if (SceneManager.GetSceneByBuildIndex(i).isLoaded) { _gate.OnMenuLoaded(Time.realtimeSinceStartup, Time.frameCount); break; }
            }
            catch (Exception e) { Ctx.Host.LogWarning("startup hook off: " + e.Message); }
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            bool menu = StartupGate.IsMenuScene(s.buildIndex);
            string pt = _probe.OnSceneLoaded(menu);
            if (pt != null) LogStartup(pt);
            if (menu) _gate.OnMenuLoaded(Time.realtimeSinceStartup, Time.frameCount);
        }

        private void UnhookScene()
        {
            if (!_sceneHooked) return;
            _sceneHooked = false;
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
        }

        private void LogStartup(string point)
        {
            try
            {
                double mono = -1;
                try { mono = Mb(Profiler.GetMonoUsedSizeLong()); } catch { }
                Ctx.Host.LogInfo(StartupProbe.Line(point, Time.realtimeSinceStartup, Time.frameCount, GC.CollectionCount(0), mono));
            }
            catch { }
        }

        private void StartHeartbeat()
        {
            _hbStarted = true;
            try
            {
                StartRecorders();
                StartLoop(Ctx, _hbPath);
                Ctx.Host.LogInfo("heartbeat started why=" + _gate.Why + " t=" + Ctx.Host.RealTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e) { Ctx.Host.LogWarning("heartbeat start failed: " + e.Message); }
        }

        /// <summary>Hang triage: the stage breadcrumb in a mapped file (survives a frozen runtime; read by tools/qa/hangwatch.py). Optional.</summary>
        private void OpenCrumb(UiContext ctx)
        {
            try
            {
                _crumb = StageCrumb.Open(System.IO.Path.Combine(ctx.Host.RootDir, StageCrumb.FileName));
                DrawGuard.Sink = _crumb;
            }
            catch (Exception e) { _crumb = null; ctx.Host.LogWarning("stage crumb off: " + e.GetType().Name + ": " + e.Message); }
            try
            {
                _workerCrumb = WorkerCrumb.Open(System.IO.Path.Combine(ctx.Host.RootDir, WorkerCrumb.FileName), () => GC.CollectionCount(0));
                _api.CoverageProbe = _workerCrumb;
            }
            catch (Exception e) { _workerCrumb = null; ctx.Host.LogWarning("worker crumb off: " + e.GetType().Name + ": " + e.Message); }
        }

        private string HeartbeatLine()
        {
            var m = Input.mousePosition;
            var overlay = _radar == null ? null : _radar.Overlay;
            var layer = _radar == null ? null : _radar.Layer;
            string view = overlay != null && overlay.MapFound ? (overlay.MapMaximized ? "map" : "map_small") : "3d";
            string layerText = layer != null ? layer.StateText() : "layer=off rt=0x0";
            return "t=" + Ctx.Host.RealTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " view=" + view + " cursor=" + (int)m.x + "," + (int)m.y +
                   " map_pick=" + (overlay != null && overlay.MapFound ? "found" : "none") + " map_scan=" + (overlay != null ? overlay.ScanText : "off") + " mission=" + (_api.Frame != null && _api.Frame.InMission ? "1" : "0") + " " + layerText + " mode=" + _api.CoverageRenderMode + " " + MemFormat.Short(_last) + " err=" + Ctx.Guard.HeartbeatText() + " ms=" + Ctx.Timing.HeartbeatText();
        }

        private volatile bool _missionSeen;              // T-305 Q1: set on the main thread at the first InMission frame, never reset
        private bool ReadArmed() { return _missionSeen; }

        public override void Update(UiFrame frame)
        {
            if (!_missionSeen && _api.Frame != null && _api.Frame.InMission) _missionSeen = true;
            string pt = _probe.OnUpdate();
            if (pt != null) LogStartup(pt);
            if (_heartbeatOn && !_hbStarted && _gate.Feed(frame.RealTime, _frameNo, _missionSeen)) StartHeartbeat();
            if (_probe.Done && (_hbStarted || !_heartbeatOn)) UnhookScene();
            System.Threading.Interlocked.Increment(ref _frameNo);
            if (_hbStarted)
            {
                if (_hb != null) _hb.Tick(frame.RealTime);
                if (_memLog != null) _memLog.Tick(frame.RealTime);
            }
        }

        /// <summary>T-302: one named thread drains both files and runs the watchdog; if it cannot start, the old pool path stays (no watchdog).</summary>
        private void StartLoop(UiContext ctx, string hbPath)
        {
            _hb.ExternalDrain = true; _memLog.ExternalDrain = true;
            var o = new HbLoopOptions
            {
                Frame = ReadFrame, Stage = ReadStage, Drains = new Action[] { _hb.DrainNow, _memLog.DrainNow, RefreshProc },
                HbWrite = _hb.WriteNow, Armed = ReadArmed,
                HbPath = hbPath, StallPath = System.IO.Path.Combine(ctx.Host.RootDir, "voicebot_ui_stall.txt")
            };
            _loop = new HbLoop(o);
            if (_loop.Start()) return;
            _hb.ExternalDrain = false; _memLog.ExternalDrain = false;
            _loop = null;
            ctx.Host.LogWarning("heartbeat thread off");
        }

        private long ReadFrame() { return System.Threading.Interlocked.Read(ref _frameNo); }
        private static string ReadStage() { return DrawGuard.Stage; }

        public override void Dispose()
        {
            UnhookScene();
            try { if (_loop != null) _loop.Dispose(); } catch { }
            _loop = null;
            try
            {
                if (_hb != null) _hb.WaitIdle(500);
                if (_memLog != null) _memLog.WaitIdle(500);
                if (_hb != null) _hb.Dispose();
                if (_memLog != null) _memLog.Dispose();
            }
            catch { }
            if (_workerCrumb != null) { try { _api.CoverageProbe = null; } catch { } _workerCrumb.Dispose(); _workerCrumb = null; }
            if (_crumb != null) { DrawGuard.Sink = null; _crumb.Dispose(); _crumb = null; }
            foreach (var kv in _rec) { try { kv.Value.Dispose(); } catch { } }
            _rec.Clear();
        }

        private void StartRecorders()
        {
            foreach (var n in RecorderNames)
            {
                try { var r = ProfilerRecorder.StartNew(ProfilerCategory.Memory, n); _rec.Add(new KeyValuePair<string, ProfilerRecorder>(n, r)); }
                catch { }
            }
        }

        private static double Mb(long bytes) { return bytes / 1048576.0; }

        private MemSample Collect()
        {
            var s = new MemSample { T = Ctx.Host.RealTime, Cov = _api.OverlayOn };
            var overlay = _radar == null ? null : _radar.Overlay;
            s.View = overlay != null && overlay.MapFound ? (overlay.MapMaximized ? "map" : "map_small") : "3d";
            try { s.Gc = Mb(GC.GetTotalMemory(false)); s.Gc0 = GC.CollectionCount(0); s.Gc1 = GC.CollectionCount(1); s.Gc2 = GC.CollectionCount(2); } catch { }
            try { s.MonoUsed = Mb(Profiler.GetMonoUsedSizeLong()); s.MonoHeap = Mb(Profiler.GetMonoHeapSizeLong()); } catch { }
            try
            {
                long a = Profiler.GetTotalAllocatedMemoryLong(), r = Profiler.GetTotalReservedMemoryLong();
                if (a > 0) s.UnityAlloc = Mb(a);
                if (r > 0) s.UnityRes = Mb(r);
            }
            catch { }
            foreach (var kv in _rec)
            {
                try
                {
                    if (!kv.Value.Valid) continue;
                    double v = Mb(kv.Value.LastValue);
                    s.Recorders[kv.Key] = v;
                    // the Profiler.* counters may read 0 in a release player: fall back to the recorders
                    if (s.UnityAlloc < 0 && kv.Key == "Total Used Memory" && v > 0) s.UnityAlloc = v;
                    if (s.UnityRes < 0 && kv.Key == "Total Reserved Memory" && v > 0) s.UnityRes = v;
                }
                catch { }
            }
            try
            {
                if (_proc == null) _proc = System.Diagnostics.Process.GetCurrentProcess(); else _proc.Refresh();   // one Process object: no allocation of a new handle per minute
                s.Ws = Mb(_proc.WorkingSet64); s.Priv = Mb(_proc.PrivateMemorySize64);
            }
            catch { }
            if (Features != null)
                foreach (var f in Features)
                {
                    var pr = f as IUiMemProbe;
                    if (pr == null) continue;
                    try
                    {
                        var d = new Dictionary<string, long>();
                        pr.MemProbe(d);
                        foreach (var kv in d) s.Ours[kv.Key] = kv.Value;
                    }
                    catch { }
                }
            try { MemCounters.CopyTo(s.Ours); } catch { }
            s.Ours["terrain.calls"] = System.Threading.Interlocked.Read(ref GameApi.TerrainCalls);
            s.Ours["terrain.ms"] = System.Threading.Interlocked.Read(ref GameApi.TerrainTicks) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            s.Ours["terrain.memo"] = System.Threading.Interlocked.Read(ref GameApi.MemoEntries);
            s.Ours["terrain.memo_clears"] = System.Threading.Interlocked.Read(ref GameApi.MemoClears);
            s.Ours["io.hb_opens"] = System.Threading.Interlocked.Read(ref Heartbeat.Opens);
            s.Ours["io.proc_reads"] = System.Threading.Interlocked.Read(ref ProcThreads.FileReads);
            s.Ours["io.last_save_reads"] = GameApi.LastSaveReads;
            s.Ours["ui.measure"] = UiKit.MeasureTotal;
            s.Ours["ui.measure_clears"] = UiKit.MeasureClears;
            return s;
        }

        /// <summary>T-336: runs on sptui-hb (or inline from DiagJson when no thread): reads /proc at most every 30 s and publishes an immutable snapshot. Never throws.</summary>
        private void RefreshProc()
        {
            try
            {
                double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                if (!_procCadence.Due(now)) return;
                if (!_procRootKnown) { _procRoot = ProcThreads.DefaultRoot(); _procRootKnown = true; }
                _procPrev = _procNow; _procPrevT = _procNowT;
                _procNow = ProcThreads.Read(_procRoot); _procNowT = now;
                double dt = _procPrev == null ? 0 : _procNowT - _procPrevT;
                _procView = new ProcView { Json = ModThreads.Json(_procNow, _procPrev, dt, 5), Hot = ModThreads.HotText(_procNow, _procPrev, dt) };
            }
            catch { }
        }

        /// <summary>T-212/T-336: main thread takes the published /proc snapshot (inline = read synchronously first; DiagJson without a thread only).</summary>
        private void FillThreads(MemSample s, bool inline)
        {
            try
            {
                if (inline) RefreshProc();
                var v = _procView;
                if (v != null) { s.ThreadsJson = v.Json; _hotText = v.Hot ?? "-"; }
            }
            catch { }
            var m = Ctx == null ? null : Ctx.Mission;
            if (m == null) return;
            try
            {
                s.LifecycleJson = m.CountersJson();
                s.Extra = MemFormat.Extra(ModThreads.AliveCount(), _hotText, m.Generation, m.SubscriberCount, m.PrevAlive());
            }
            catch { }
        }

        private string MemoryLine()
        {
            var s = Collect();
            FillThreads(s, false);
            _last = s;
            _ring.Add(s.T, s.Gc, s.Priv);
            int phys = 0;
            try { phys = SystemInfo.systemMemorySize; } catch { }
            string w = _warn == null ? null : _warn.Feed(s.T, s.Priv, phys);
            if (w != null) { try { Ctx.Host.LogWarning(w); } catch { } }
            return MemFormat.Line(s);
        }

        // ---- IUiDiag (T-151): section "memory"; the series is the last 60 minutes, one point per minute
        public string DiagSection { get { return "memory"; } }

        public string DiagJson(int limit)
        {
            var s = _last ?? Collect();
            _procCadence.Request();
            FillThreads(s, _loop == null);
            return MemFormat.Json(s, _ring);
        }
    }
}
