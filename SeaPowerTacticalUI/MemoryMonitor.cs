using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>One reading of the memory line (T-151). MB unless noted; a negative value = not available.</summary>
    internal sealed class MemSample
    {
        public double T;
        public double Gc = -1, MonoUsed = -1, MonoHeap = -1, UnityAlloc = -1, UnityRes = -1, Ws = -1, Priv = -1;
        public int Gc0, Gc1, Gc2;
        public bool Cov;
        public string View = "";
        public string ThreadsJson, LifecycleJson;   // T-212: ModThreads.Json(...) and MissionLifecycle.CountersJson(); null = not available
        public string Extra = "";                   // T-212: " thr=.. hot=.. gen=.. subs=.. prev_alive=.." tail of the minute line
        public readonly SortedDictionary<string, long> Ours = new SortedDictionary<string, long>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Recorders = new SortedDictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>Fixed-size ring of (t, gc, priv) points, oldest first on read (the last 60 minutes, one point per minute).</summary>
    internal sealed class MemRing
    {
        private readonly double[] _t, _gc, _priv;
        private int _head, _count;

        public MemRing(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _t = new double[capacity]; _gc = new double[capacity]; _priv = new double[capacity];
        }

        public int Capacity { get { return _t.Length; } }
        public int Count { get { return _count; } }

        public void Add(double t, double gc, double priv)
        {
            _t[_head] = t; _gc[_head] = gc; _priv[_head] = priv;
            _head = (_head + 1) % _t.Length;
            if (_count < _t.Length) _count++;
        }

        /// <summary>i = 0 is the oldest kept point.</summary>
        public void At(int i, out double t, out double gc, out double priv)
        {
            int k = (_head - _count + i + _t.Length * 2) % _t.Length;
            t = _t[k]; gc = _gc[k]; priv = _priv[k];
        }
    }

    /// <summary>Early warning: private memory grew by more than thresholdMb inside the window, or passed 60% of physical RAM. At most one warning per window.</summary>
    internal sealed class MemWarn
    {
        private readonly double _thresholdMb, _windowS;
        private readonly List<KeyValuePair<double, double>> _pts = new List<KeyValuePair<double, double>>();
        private double _lastWarn = double.NegativeInfinity;

        public MemWarn(double thresholdMb, double windowS = 600.0) { _thresholdMb = thresholdMb; _windowS = windowS; }

        /// <summary>The warning text, or null. priv &lt;= 0 (not available) is ignored.</summary>
        public string Feed(double t, double privMb, double physMb = 0)
        {
            if (!(privMb > 0)) return null;
            _pts.Add(new KeyValuePair<double, double>(t, privMb));
            while (_pts.Count > 1 && t - _pts[0].Key > _windowS) _pts.RemoveAt(0);
            double min = privMb;
            foreach (var p in _pts) if (p.Value < min) min = p.Value;
            double growth = privMb - min;
            bool grew = growth > _thresholdMb;
            bool ram = physMb > 0 && privMb > 0.6 * physMb;
            if (!grew && !ram) return null;
            if (t - _lastWarn < _windowS) return null;
            _lastWarn = t;
            var ic = CultureInfo.InvariantCulture;
            return "[Mem] WARN priv=" + privMb.ToString("F0", ic) + " MB, +" + growth.ToString("F0", ic) + " MB in " + Math.Min(_windowS, _pts[_pts.Count - 1].Key - _pts[0].Key).ToString("F0", ic) +
                   " s" + (ram ? ", over 60% of RAM (" + physMb.ToString("F0", ic) + " MB)" : "");
        }
    }

    /// <summary>Counters that live outside any feature (hot paths). Thread-safe.</summary>
    internal static class MemCounters
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, long> Values = new Dictionary<string, long>();

        public static void Set(string key, long v) { lock (Lock) Values[key] = v; }
        public static void Add(string key, long d) { lock (Lock) { long c; Values.TryGetValue(key, out c); Values[key] = c + d; } }
        public static void Reset() { lock (Lock) Values.Clear(); }
        public static void CopyTo(IDictionary<string, long> into)
        {
            lock (Lock) foreach (var kv in Values) into[kv.Key] = kv.Value;
        }
    }

    internal static class MemFormat
    {
        private static string F(double v) { return v < 0 ? "na" : v.ToString("F1", CultureInfo.InvariantCulture); }

        /// <summary>One key=value line: t, managed, Unity, process, GC counts, our counters, cov, view.</summary>
        public static string Line(MemSample s)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("t=").Append(((long)s.T).ToString(ic));
            sb.Append(" gc=").Append(F(s.Gc)).Append(" mono_used=").Append(F(s.MonoUsed)).Append(" mono_heap=").Append(F(s.MonoHeap));
            sb.Append(" unity_alloc=").Append(F(s.UnityAlloc)).Append(" unity_res=").Append(F(s.UnityRes));
            sb.Append(" ws=").Append(F(s.Ws)).Append(" priv=").Append(F(s.Priv));
            sb.Append(" gc0=").Append(s.Gc0.ToString(ic)).Append(" gc1=").Append(s.Gc1.ToString(ic)).Append(" gc2=").Append(s.Gc2.ToString(ic));
            foreach (var kv in s.Ours) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.ToString(ic));
            sb.Append(" cov=").Append(s.Cov ? "on" : "off").Append(" view=").Append(string.IsNullOrEmpty(s.View) ? "na" : s.View);
            if (!string.IsNullOrEmpty(s.Extra)) sb.Append(' ').Append(s.Extra);
            return sb.ToString();
        }

        /// <summary>T-212: tail of the minute line: live mod threads, the hottest thread of /proc (tid:cpu%:mod|-), mission generation, subscribers, left missions still alive.</summary>
        public static string Extra(int modAlive, string hot, int gen, int subs, int prevAlive)
        {
            var ic = CultureInfo.InvariantCulture;
            return "thr=" + modAlive.ToString(ic) + " hot=" + (string.IsNullOrEmpty(hot) ? "-" : hot) + " gen=" + gen.ToString(ic) + " subs=" + subs.ToString(ic) + " prev_alive=" + prevAlive.ToString(ic);
        }

        /// <summary>Cached part of the 2 s heartbeat line: mem=&lt;managed MB&gt;/&lt;process MB&gt;.</summary>
        public static string Short(MemSample s)
        {
            if (s == null) return "mem=na";
            return "mem=" + (s.Gc < 0 ? "na" : s.Gc.ToString("F0", CultureInfo.InvariantCulture)) + "/" + (s.Priv < 0 ? "na" : s.Priv.ToString("F0", CultureInfo.InvariantCulture));
        }

        private static string N(double v) { return v < 0 ? "null" : v.ToString("F1", CultureInfo.InvariantCulture); }

        /// <summary>Diag section "memory" (a JSON object); series = the ring, oldest first.</summary>
        public static string Json(MemSample s, MemRing ring)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            if (s == null) return "{\"t\":null,\"series\":[]}";
            sb.Append("{\"t\":").Append(s.T.ToString("F1", ic));
            sb.Append(",\"managed\":{\"gc_mb\":").Append(N(s.Gc)).Append(",\"mono_used_mb\":").Append(N(s.MonoUsed)).Append(",\"mono_heap_mb\":").Append(N(s.MonoHeap))
              .Append(",\"gc_counts\":[").Append(s.Gc0.ToString(ic)).Append(',').Append(s.Gc1.ToString(ic)).Append(',').Append(s.Gc2.ToString(ic)).Append("]}");
            sb.Append(",\"unity\":{\"alloc_mb\":").Append(N(s.UnityAlloc)).Append(",\"reserved_mb\":").Append(N(s.UnityRes)).Append(",\"recorders\":{");
            bool first = true;
            foreach (var kv in s.Recorders) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(kv.Key).Append("\":").Append(N(kv.Value)); }
            sb.Append("}},\"process\":{\"ws_mb\":").Append(N(s.Ws)).Append(",\"private_mb\":").Append(N(s.Priv)).Append('}');
            sb.Append(",\"cov\":").Append(s.Cov ? "true" : "false").Append(",\"ours\":{");
            first = true;
            foreach (var kv in s.Ours) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value.ToString(ic)); }
            sb.Append('}');
            if (s.ThreadsJson != null) sb.Append(",\"threads\":").Append(s.ThreadsJson);
            if (s.LifecycleJson != null) sb.Append(",\"lifecycle\":").Append(s.LifecycleJson);
            sb.Append(",\"series\":[");
            if (ring != null)
                for (int i = 0; i < ring.Count; i++)
                {
                    double t, gc, priv;
                    ring.At(i, out t, out gc, out priv);
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"t\":").Append(t.ToString("F0", ic)).Append(",\"gc\":").Append(N(gc)).Append(",\"priv\":").Append(N(priv)).Append('}');
                }
            return sb.Append("]}").ToString();
        }
    }

    internal static class MemSelfTest
    {
        public static string Run()
        {
            var errs = new List<string>();
            Action<bool, string> chk = (ok, m) => { if (!ok) errs.Add(m); };
            // ring: 60 points, the oldest are overwritten, order oldest first
            var ring = new MemRing(60);
            for (int i = 0; i < 75; i++) ring.Add(i * 60.0, i, 1000 + i);
            double t0, g0, p0, t1, g1, p1;
            ring.At(0, out t0, out g0, out p0); ring.At(59, out t1, out g1, out p1);
            chk(ring.Count == 60 && ring.Capacity == 60, "ring count");
            chk(t0 == 15 * 60.0 && p0 == 1015 && t1 == 74 * 60.0 && p1 == 1074, "ring order " + t0 + "/" + t1);
            var small = new MemRing(3);
            small.Add(1, 1, 1); small.Add(2, 2, 2);
            double a, b, c;
            small.At(0, out a, out b, out c);
            chk(small.Count == 2 && a == 1, "ring partial");
            // warn: +2 GiB inside 10 min, once per window, quiet when flat
            var w = new MemWarn(2048.0, 600.0);
            chk(w.Feed(0, 5000) == null && w.Feed(60, 5100) == null && w.Feed(120, 6000) == null, "warn: slow growth is quiet");
            string msg = w.Feed(180, 7200);
            chk(msg != null && msg.StartsWith("[Mem] WARN") && msg.Contains("priv=7200"), "warn: +2.2 GiB must warn: " + msg);
            chk(w.Feed(240, 8000) == null && w.Feed(500, 9000) == null, "warn: at most once per window");
            chk(w.Feed(800, 12000) != null, "warn: again after the window");
            var flat = new MemWarn(2048.0, 600.0);
            bool quiet = true;
            for (int i = 0; i < 100; i++) quiet &= flat.Feed(i * 60.0, 8000 + (i % 3)) == null;
            chk(quiet, "warn: flat is quiet");
            var slowLeak = new MemWarn(2048.0, 600.0);
            bool slowQuiet = true;
            for (int i = 0; i < 100; i++) slowQuiet &= slowLeak.Feed(i * 60.0, 5000 + i * 100.0) == null;   // 1 GiB per 10 min < 2 GiB
            chk(slowQuiet, "warn: 1 GiB per 10 min is below the limit");
            var ram = new MemWarn(2048.0, 600.0);
            chk(ram.Feed(0, 20000, 31000) != null && new MemWarn(2048.0).Feed(0, 10000, 31000) == null, "warn: 60% of RAM");
            chk(new MemWarn(1, 600).Feed(0, -1) == null && new MemWarn(1, 600).Feed(0, 0) == null, "warn: unavailable value ignored");
            // counters
            MemCounters.Reset();
            string ka = "cnt.a", kb = "cnt.b";
            MemCounters.Add(ka, 2); MemCounters.Add(ka, 3); MemCounters.Set(kb, 7);
            var d = new Dictionary<string, long>();
            MemCounters.CopyTo(d);
            chk(d["cnt.a"] == 5 && d["cnt.b"] == 7, "counters");
            MemCounters.Reset();
            // terrain memo is bounded
            int calls = 0, last = -1, clears = 0;
            var smp = CoverageMath.CachedSampler((la, lo) => { calls++; return 1.0; }, 1.0, 100, n => { last = n; if (n == 0) clears++; });
            for (int i = 0; i < 350; i++) smp(i, 0);
            chk(calls == 350 && last <= 100 && last >= 1 && clears == 3, "memo bounded: last=" + last + " clears=" + clears);
            chk(smp(349, 0) == 1.0 && calls == 350, "memo still hits after a clear");
            // line and json
            var s = new MemSample { T = 2207.4, Gc = 412.34, MonoUsed = 388, MonoHeap = 701.5, UnityAlloc = -1, UnityRes = -1, Ws = 18234.1, Priv = 21020.5, Gc0 = 311, Gc1 = 40, Gc2 = 3, Cov = true, View = "map" };
            s.Ours["ui.measure"] = 1800; s.Ours["ov.tex"] = 24;
            string line = MemFormat.Line(s);
            chk(line.StartsWith("t=2207 gc=412.3 mono_used=388.0 mono_heap=701.5 unity_alloc=na unity_res=na ws=18234.1 priv=21020.5 gc0=311 gc1=40 gc2=3 "), "line head: " + line);
            chk(line.EndsWith(" ov.tex=24 ui.measure=1800 cov=on view=map"), "line tail: " + line);
            chk(MemFormat.Short(s) == "mem=412/21020" && MemFormat.Short(null) == "mem=na", "short: " + MemFormat.Short(s));
            ring.Add(1, 2, 3);
            string json = MemFormat.Json(s, ring);
            try
            {
                var j = VoiceBotBridge.Json.Parse(json) as Dictionary<string, object>;
                chk(j != null && (j["series"] as List<object>).Count == 60, "json series");
                chk(((j["process"] as Dictionary<string, object>)["private_mb"] as double?) == 21020.5, "json private");
                chk((j["unity"] as Dictionary<string, object>)["alloc_mb"] == null, "json null for na");
                chk(((j["ours"] as Dictionary<string, object>)["ui.measure"] as double?) == 1800, "json ours");
            }
            catch (Exception e) { errs.Add("json: " + e.GetType().Name + " " + e.Message); }
            // T-212: threads and lifecycle fields; without /proc the hot list is empty, not an error
            try
            {
                s.ThreadsJson = VoiceBotBridge.Ui.ModThreads.Json(new List<VoiceBotBridge.Ui.ProcThread>(), null, 0, 3);
                s.LifecycleJson = "{\"generation\":4,\"subscribers\":7,\"prev_alive\":0,\"prev_tracked\":3}";
                s.Extra = MemFormat.Extra(2, "123:97:sptui-coverage", 4, 7, 0);
                var j2 = VoiceBotBridge.Json.Parse(MemFormat.Json(s, ring)) as Dictionary<string, object>;
                var thr = j2["threads"] as Dictionary<string, object>;
                chk(thr != null && thr["mod"] is List<object> && (thr["hot"] as List<object>).Count == 0, "json threads.mod/hot");
                chk(((j2["lifecycle"] as Dictionary<string, object>)["subscribers"] as double?) == 7, "json lifecycle");
                chk(j2["series"] is List<object>, "json series after the new fields");
                string l2 = MemFormat.Line(s);
                chk(l2.EndsWith(" cov=on view=map thr=2 hot=123:97:sptui-coverage gen=4 subs=7 prev_alive=0"), "line tail with threads: " + l2);
                s.ThreadsJson = null; s.LifecycleJson = null; s.Extra = "";
                chk(!MemFormat.Json(s, ring).Contains("\"threads\""), "no threads field when unavailable");
            }
            catch (Exception e) { errs.Add("threads json: " + e.GetType().Name + " " + e.Message); }
            return errs.Count == 0 ? null : string.Join("; ", errs.ToArray());
        }
    }
}
