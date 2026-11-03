using System;
using System.Collections.Generic;
namespace VoiceBotBridge.Ui
{
    internal struct UiFrame
    {
        public double RealTime;    // Time.unscaledTime
        public double DeltaTime;   // Time.unscaledDeltaTime
    }

    /// <summary>T-120: a module hands over its specification set (one *Specs.cs class): c.Specs(<Module>Specs.Set).</summary>
    internal interface ISelfCheck
    {
        void Specs(SpecSet set);
    }

    /// <summary>One function of the UI mod (ADR-0044). UiCore drives the life cycle.</summary>
    internal interface IUiFeature
    {
        string Id { get; }                 // unique: "radar", "asw", ...
        string[] DependsOn { get; }        // ids whose Init must run first; empty = none
        void Bind(IUiConfig config);       // Awake, before GameApi.SelfCheck; may write to ctx.Api
        void Init(UiContext ctx);          // first Update after Ownership.Plan, topological order
        void Update(UiFrame frame);        // every frame, registration order
        void OnGUI();                      // registration order
        void Dispose();                    // OnDestroy, registration order
        void SelfCheck(ISelfCheck check);  // T-120: c.Specs(<Module>Specs.Set); no specs = nothing to say
    }

    /// <summary>T-151: a module that reports sizes of its own caches into the memory line (key prefix = module id). Main thread, must not throw.</summary>
    internal interface IUiMemProbe
    {
        void MemProbe(System.Collections.Generic.IDictionary<string, long> into);
    }

    /// <summary>T-144: a module that wants one call per Apply of the settings window, with the changed "Section.Key" list.</summary>
    internal interface IUiSettingsApplied { void SettingsApplied(System.Collections.Generic.ICollection<string> keys); }

    /// <summary>T-144: a module with its own section in the settings window (section id = feature Id).</summary>
    internal interface IUiSettingsSection { int SettingsOrder { get; } }

    internal abstract class UiFeatureBase : IUiFeature, IUiMemProbe
    {
        private static readonly string[] Empty = new string[0];

        protected UiFeatureBase(UiContext ctx) { Ctx = ctx; }

        /// <summary>ctx.Api and ctx.Host are usable already in Bind; ctx.Plan is null until Init.</summary>
        protected readonly UiContext Ctx;

        public abstract string Id { get; }
        public virtual string[] DependsOn { get { return Empty; } }
        public virtual void Bind(IUiConfig config) { }

        /// <summary>T-144: run handler when the setting changes (settings window, ConfigurationManager, a file reload). Dropped on Dispose.</summary>
        protected void OnChanged<T>(IUiSetting<T> s, System.Action<T> handler) { Ctx.Settings.OnChanged(Id, s, handler); }
        public virtual void Init(UiContext ctx) { }
        public virtual void Update(UiFrame frame) { }
        public virtual void OnGUI() { }
        public virtual void Dispose() { }
        public virtual void SelfCheck(ISelfCheck check) { }
        public virtual void MemProbe(System.Collections.Generic.IDictionary<string, long> into) { }
    }
    /// <summary>
    /// T-129 (R6): counts the exceptions of the frame loop per slot ("radar.pump"), writes them to the log with a rate limit and tells
    /// UiCore when a module has failed for DisableFrames frames in a row and DisableMinS seconds. Pure: no Unity, no game.
    /// The hot path (a frame without errors) touches no dictionary and builds no string.
    /// </summary>
    internal sealed class UiGuard
    {
        public const int DisableFrames = 300;
        public const double DisableMinS = 10, RepeatS = 60;

        private readonly Func<double> _clock;
        private readonly Action<string> _warn;
        private readonly List<string> _names = new List<string>();
        private readonly List<int> _owners = new List<int>();
        private readonly List<long> _count = new List<long>();
        private readonly List<long> _sinceLog = new List<long>();
        private readonly List<double> _lastLog = new List<double>();
        private string[] _ownerIds = new string[0];
        private bool[] _failedNow = new bool[0];
        private int[,] _streak = new int[0, 2];
        private double[,] _streakT = new double[0, 2];
        private bool[,] _fired = new bool[0, 2];
        private long[] _guiFrame = new long[0];     // T-120 (T-129 note): OnGUI runs several times per frame; the series counts frames
        private bool[] _guiFailed = new bool[0];
        private long _total;

        public UiGuard(Func<double> clock, Action<string> warn) { _clock = clock ?? (() => 0); _warn = warn ?? (s => { }); }

        /// <summary>UiCore: the module ids in registration order; an index is the owner of a slot.</summary>
        public void SetOwners(string[] ids)
        {
            _ownerIds = ids ?? new string[0];
            int n = _ownerIds.Length;
            _failedNow = new bool[n]; _streak = new int[n, 2]; _streakT = new double[n, 2]; _fired = new bool[n, 2];
            _guiFrame = new long[n]; _guiFailed = new bool[n];
            for (int i = 0; i < n; i++) _guiFrame[i] = long.MinValue;
        }

        /// <summary>Index of a module in UiCore, -1 = unknown.</summary>
        public int OwnerOf(string id)
        {
            for (int i = 0; i < _ownerIds.Length; i++) if (_ownerIds[i] == id) return i;
            return -1;
        }

        public int Register(string name, int owner)
        {
            for (int i = 0; i < _names.Count; i++) if (_names[i] == name && _owners[i] == owner) return i;
            _names.Add(name); _owners.Add(owner); _count.Add(0); _sinceLog.Add(0); _lastLog.Add(double.NegativeInfinity);
            return _names.Count - 1;
        }

        public void BeginFrame(long frame) { for (int i = 0; i < _failedNow.Length; i++) _failedNow[i] = false; }
        public bool FailedThisFrame(int owner) { return owner >= 0 && owner < _failedNow.Length && _failedNow[owner]; }
        /// <summary>UiCore: before it calls a module, so the flag tells about this call only.</summary>
        public void ClearFailed(int owner) { if (owner >= 0 && owner < _failedNow.Length) _failedNow[owner] = false; }

        public void Fail(int slot, Exception e)
        {
            if (slot < 0 || slot >= _names.Count) return;
            _total++;
            _count[slot]++; _sinceLog[slot]++;
            int owner = _owners[slot];
            if (owner >= 0 && owner < _failedNow.Length) _failedNow[owner] = true;
            double now = _clock();
            string what = e == null ? "error" : e.GetType().Name + ": " + e.Message;
            if (_count[slot] == 1)
            {
                _lastLog[slot] = now; _sinceLog[slot] = 0;
                _warn(_names[slot] + " failed: " + what + " at " + Where(e));
            }
            else if (now - _lastLog[slot] >= RepeatS)
            {
                _warn(_names[slot] + " failed x" + _sinceLog[slot] + " in " + (int)(now - _lastLog[slot]) + " s (last: " + what + ")");
                _lastLog[slot] = now; _sinceLog[slot] = 0;
            }
        }

        private static string Where(Exception e)
        {
            string st = e == null ? null : e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "?";
            int nl = st.IndexOf('\n');
            return (nl < 0 ? st : st.Substring(0, nl)).Trim();
        }

        /// <summary>Call after each call of a module; phase 0 = Update, 1 = OnGUI. true once when the series is long enough.</summary>
        public bool EndModule(int owner, bool failedNow, out string why, bool gui = false)
        {
            why = null;
            if (owner < 0 || owner >= _failedNow.Length) return false;
            int ph = gui ? 1 : 0;
            if (!failedNow) { _streak[owner, ph] = 0; return false; }
            return Advance(owner, ph, out why);
        }

        /// <summary>
        /// The OnGUI phase of a module. Unity calls OnGUI several times per frame (Layout, Repaint, input events): a series counts FRAMES with at least one failure,
        /// so a good Layout call before a failing Repaint call does not reset it. The first call of a frame closes the previous one: a frame without a failure resets the series.
        /// </summary>
        public bool EndGui(int owner, bool failedNow, long frame, out string why)
        {
            why = null;
            if (owner < 0 || owner >= _failedNow.Length) return false;
            if (_guiFrame[owner] != frame)
            {
                if (!_guiFailed[owner]) _streak[owner, 1] = 0;
                _guiFrame[owner] = frame; _guiFailed[owner] = false;
            }
            if (!failedNow || _guiFailed[owner]) return false;
            _guiFailed[owner] = true;
            return Advance(owner, 1, out why);
        }

        private bool Advance(int owner, int ph, out string why)
        {
            why = null;
            if (_streak[owner, ph] == 0) _streakT[owner, ph] = _clock();
            _streak[owner, ph]++;
            if (_fired[owner, ph] || _streak[owner, ph] < DisableFrames || _clock() - _streakT[owner, ph] < DisableMinS) return false;
            _fired[owner, ph] = true;
            why = _streak[owner, ph] + " errors in a row";
            return true;
        }

        public long Total { get { return _total; } }

        public string HeartbeatText()
        {
            if (_total == 0) return "0";
            var idx = new List<int>();
            for (int i = 0; i < _names.Count; i++) if (_count[i] > 0) idx.Add(i);
            idx.Sort((a, b) => _count[a] != _count[b] ? _count[b].CompareTo(_count[a]) : string.CompareOrdinal(_names[a], _names[b]));
            var sb = new System.Text.StringBuilder().Append(_total).Append('(');
            for (int k = 0; k < idx.Count && k < 3; k++) { if (k > 0) sb.Append(','); sb.Append(_names[idx[k]]).Append(':').Append(_count[idx[k]]); }
            return sb.Append(')').ToString();
        }

        public IDictionary<string, long> Counts()
        {
            var d = new Dictionary<string, long>();
            for (int i = 0; i < _names.Count; i++) if (_count[i] > 0) d[_names[i]] = _count[i];
            return d;
        }

        public static string SelfTest()
        {
            double t = 0; var log = new List<string>();
            var g = new UiGuard(() => t, log.Add);
            g.SetOwners(new[] { "m0", "m1" });
            if (g.OwnerOf("m1") != 1 || g.OwnerOf("zz") != -1) return "guard: OwnerOf";
            if (g.HeartbeatText() != "0") return "guard: empty heartbeat = " + g.HeartbeatText();
            int s = g.Register("a.x", 0), s2 = g.Register("a.y", 0), core = g.Register("core.z", -1);
            if (g.Register("a.x", 0) != s) return "guard: Register must be idempotent";
            var ex = new InvalidOperationException("boom");
            g.Fail(s, ex);
            if (log.Count != 1 || !log[0].StartsWith("a.x failed: InvalidOperationException: boom at ")) return "guard: first line = " + string.Join("|", log.ToArray());
            for (int i = 0; i < 99; i++) { t += 0.3; g.Fail(s, ex); }
            if (log.Count != 1) return "guard: 100 errors in 30 s must give one line, got " + log.Count;
            t = 61; g.Fail(s, ex);
            if (log.Count != 2 || !log[1].StartsWith("a.x failed x100 in 61 s (last: InvalidOperationException: boom)")) return "guard: repeat line = " + string.Join("|", log.ToArray());
            if (!g.FailedThisFrame(0) || g.FailedThisFrame(1)) return "guard: FailedThisFrame";
            g.BeginFrame(1);
            if (g.FailedThisFrame(0)) return "guard: BeginFrame must clear";
            g.Fail(s2, ex); g.Fail(s2, ex); g.Fail(core, ex);
            if (g.HeartbeatText() != "104(a.x:101,a.y:2,core.z:1)") return "guard: heartbeat = " + g.HeartbeatText();
            if (g.Counts().Count != 3 || g.Counts()["a.y"] != 2) return "guard: Counts";
            // the series: 300 frames and 10 s
            var g2 = new UiGuard(() => t, log.Add); g2.SetOwners(new[] { "m" });
            string why; int fired = 0;
            t = 0;
            for (int i = 0; i < 300; i++) { t += 0.01; if (g2.EndModule(0, true, out why)) fired++; }
            if (fired != 0) return "guard: 300 fast frames in 3 s must not switch off";
            t = 20;
            for (int i = 0; i < 5; i++) if (g2.EndModule(0, true, out why)) { fired++; if (why == null) return "guard: why"; }
            if (fired != 1) return "guard: must fire exactly once, got " + fired;
            // a good frame resets the series
            var g3 = new UiGuard(() => t, log.Add); g3.SetOwners(new[] { "m" });
            t = 0;
            for (int i = 0; i < 299; i++) { t += 0.1; g3.EndModule(0, true, out why); }
            g3.EndModule(0, false, out why);
            t += 20;
            if (g3.EndModule(0, true, out why)) return "guard: a good frame must reset the series";
            // a core slot (owner -1) never switches anything off
            if (g3.EndModule(-1, true, out why)) return "guard: owner -1";
            // OnGUI: Layout is good, Repaint fails, in the same frame - the series still counts frames (T-129 note)
            var g4 = new UiGuard(() => t, log.Add); g4.SetOwners(new[] { "m" });
            t = 0; fired = 0;
            for (long f = 0; f < 400; f++)
            {
                t += 0.05;
                if (g4.EndGui(0, false, f, out why)) fired++;   // Layout
                if (g4.EndGui(0, true, f, out why)) fired++;    // Repaint
                if (g4.EndGui(0, false, f, out why)) fired++;   // another good event after it
            }
            if (fired != 1) return "guard: OnGUI failing in every frame must fire once, got " + fired;
            var g5 = new UiGuard(() => t, log.Add); g5.SetOwners(new[] { "m" });
            t = 0; fired = 0;
            for (long f = 0; f < 600; f++)
            {
                t += 0.05;
                g5.EndGui(0, false, f, out why);
                if (g5.EndGui(0, f % 100 != 99, f, out why)) fired++;   // a frame without a failure every 100 frames
            }
            if (fired != 0) return "guard: a frame without a failure must reset the OnGUI series";
            if (g5.EndGui(-1, true, 1, out why)) return "guard: OnGUI owner -1";
            return null;
        }
    }
}
