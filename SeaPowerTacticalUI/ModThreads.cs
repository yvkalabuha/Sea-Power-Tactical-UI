// T-212: a registry of the threads and pool work items of the mod, keyed by the Linux TID (so a hot thread seen in top -H can be named).
// Pure (no Unity). Enter/Exit for a dedicated thread body, Work/PoolRunner for pool items. Never throws.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VoiceBotBridge.Ui
{
    internal sealed class ModThreadInfo
    {
        public string Name; public int ManagedId; public int Tid; public double SinceS; public bool Alive;
        public string Work; public double WorkSinceS; public long WorkCount;
        public double ExitS;
    }

    internal static class ModThreads
    {
        public static string Root;                 // test seam; null = ProcThreads.DefaultRoot()
        public static Func<double> Clock;          // test seam; default Stopwatch seconds
        public const double KeepDeadS = 600.0;

        private static readonly object Lock = new object();
        private static readonly Dictionary<int, ModThreadInfo> ByManaged = new Dictionary<int, ModThreadInfo>();
        private static readonly long T0 = Stopwatch.GetTimestamp();
        private static bool _nativeOff;

        [DllImport("kernel32", EntryPoint = "GetCurrentThread")] private static extern IntPtr GetCurrentThreadNative();
        [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "SetThreadDescription")] private static extern int SetThreadDescriptionNative(IntPtr h, string name);

        private static double Now() { var c = Clock; return c != null ? c() : (Stopwatch.GetTimestamp() - T0) / (double)Stopwatch.Frequency; }
        // The TID is found by the comm of the thread (SetThreadDescription -> Linux comm "sptui-*") among the tasks of the GAME process,
        // never via /proc/self (under Wine that is the wineserver). T-227: Enter does NOT scan /proc (a dev client thread per connection
        // meant ~285 file opens each); Tid stays 0 until Resolve matches it against the next snapshot (memory line / memory section).
        // A pool thread is not renamed -> its TID stays 0 (matched by nothing).

        /// <summary>Fills Tid of live named entries that were not resolved at Enter time (the comm was not visible yet), from the given snapshot.</summary>
        private static void Resolve(List<ModThreadInfo> mods, List<ProcThread> now)
        {
            if (now == null || now.Count == 0) return;
            var claimed = new HashSet<int>();
            foreach (var m in mods) if (m.Alive && m.Tid > 0) claimed.Add(m.Tid);
            foreach (var m in mods)
            {
                if (!m.Alive || m.Tid > 0 || m.Name == "pool") continue;
                int t = ProcThreads.MatchByComm(now, m.Name, claimed);
                if (t > 0)
                {
                    m.Tid = t; claimed.Add(t);
                    lock (Lock)
                    {
                        ModThreadInfo reg;   // the TID is stable between snapshots: remember it in the registry
                        if (ByManaged.TryGetValue(m.ManagedId, out reg) && reg.Alive && reg.Name == m.Name && reg.Tid == 0) reg.Tid = t;
                    }
                }
            }
        }

        private static void NativeName(string name)
        {
            if (_nativeOff || Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try { SetThreadDescriptionNative(GetCurrentThreadNative(), name); }
            catch { _nativeOff = true; }
        }

        private static ModThreadInfo Register(string name, bool rename)
        {
            int mid = Thread.CurrentThread.ManagedThreadId;
            if (rename) NativeName(name); 
            double now = Now();
            lock (Lock)
            {
                Prune(now);
                ModThreadInfo i;
                if (!ByManaged.TryGetValue(mid, out i)) { i = new ModThreadInfo { ManagedId = mid, SinceS = now }; ByManaged[mid] = i; }
                i.Name = name; i.Tid = 0; i.Alive = true; i.ExitS = 0;
                return i;
            }
        }

        /// <summary>First line of a dedicated thread body.</summary>
        public static void Enter(string name)
        {
            try { Register(name, true); } catch { }
        }

        /// <summary>Finally of the thread body: Alive=false, the entry stays for KeepDeadS.</summary>
        public static void Exit()
        {
            try
            {
                int mid = Thread.CurrentThread.ManagedThreadId;
                double now = Now();
                lock (Lock)
                {
                    ModThreadInfo i;
                    if (ByManaged.TryGetValue(mid, out i)) { i.Alive = false; i.Work = null; i.ExitS = now; }
                }
            }
            catch { }
        }

        private sealed class WorkScope : IDisposable
        {
            private ModThreadInfo _i; private readonly string _prev;
            public WorkScope(ModThreadInfo i, string prev) { _i = i; _prev = prev; }
            public void Dispose()
            {
                var i = _i; if (i == null) return; _i = null;
                lock (Lock) { i.Work = _prev; i.WorkSinceS = _prev == null ? 0 : i.WorkSinceS; }
            }
        }

        private static readonly IDisposable NoScope = new WorkScope(null, null);

        /// <summary>A pool work item: registers the pool thread once (name "pool"), marks Work=label until Dispose. Never renames the thread.</summary>
        public static IDisposable Work(string label)
        {
            try
            {
                int mid = Thread.CurrentThread.ManagedThreadId;
                ModThreadInfo i;
                bool known;
                lock (Lock) known = ByManaged.TryGetValue(mid, out i) && i.Alive;
                if (!known) i = Register("pool", false);
                else { lock (Lock) { ByManaged.TryGetValue(mid, out i); } }
                double now = Now();
                lock (Lock) { string prev = i.Work; i.Work = label; i.WorkSinceS = now; i.WorkCount++; return new WorkScope(i, prev); }
            }
            catch { return NoScope; }
        }

        /// <summary>For the stop-runtime lambdas of the controllers: runs the action as a pool item tagged with the label.</summary>
        public static Action<Action> PoolRunner(string label)
        {
            return a => ThreadPool.QueueUserWorkItem(_ => { using (Work(label)) { try { a(); } catch { } } });
        }

        private static void Prune(double now)
        {
            List<int> drop = null;
            foreach (var kv in ByManaged)
                if (!kv.Value.Alive && now - kv.Value.ExitS > KeepDeadS) (drop ?? (drop = new List<int>())).Add(kv.Key);
            if (drop != null) foreach (int k in drop) ByManaged.Remove(k);
        }

        public static List<ModThreadInfo> Snapshot()
        {
            var l = new List<ModThreadInfo>();
            double now = Now();
            lock (Lock)
            {
                Prune(now);
                foreach (var kv in ByManaged)
                {
                    var s = kv.Value;
                    l.Add(new ModThreadInfo { Name = s.Name, ManagedId = s.ManagedId, Tid = s.Tid, SinceS = s.SinceS, Alive = s.Alive, Work = s.Work, WorkSinceS = s.WorkSinceS, WorkCount = s.WorkCount, ExitS = s.ExitS });
                }
            }
            l.Sort((a, b) => a.ManagedId.CompareTo(b.ManagedId));
            return l;
        }

        /// <summary>T-220: names of the live dedicated mod threads (everything but the pool).</summary>
        public static List<string> AliveDedicated() { return ModShutdown.AliveOf(Snapshot()); }

        /// <summary>T-220: "pool/&lt;work&gt;" of the pool threads that are inside a work item.</summary>
        public static List<string> BusyPool() { return ModShutdown.BusyOf(Snapshot()); }

        public static int AliveCount()
        {
            int n = 0;
            foreach (var t in Snapshot()) if (t.Alive) n++;
            return n;
        }

        /// <summary>The hottest thread of the delta (tid, cpu percent, name|null) or null.</summary>
        public static string HotText(List<ProcThread> now, List<ProcThread> prev, double dtS)
        {
            var hot = Hot(now, prev, dtS, 1);
            if (hot.Count == 0) return "-";
            var h = hot[0];
            return h.Tid + ":" + ((int)Math.Round(h.Cpu)).ToString(CultureInfo.InvariantCulture) + ":" + (h.Mod ?? "-");
        }

        private static string Q(string v)
        {
            if (v == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in v)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string Nm(double d) { return d.ToString("0.##", CultureInfo.InvariantCulture); }

        private struct HotRow { public int Tid; public string Comm; public double Cpu; public string Mod; }

        private static List<HotRow> Hot(List<ProcThread> now, List<ProcThread> prev, double dtS, int top)
        {
            var rows = new List<HotRow>();
            if (now == null || prev == null || dtS <= 0) return rows;
            var p = new Dictionary<int, long>();
            foreach (var t in prev) p[t.Tid] = t.Utime + t.Stime;
            var mod = new Dictionary<int, string>();
            var mods = Snapshot();
            Resolve(mods, now);
            foreach (var m in mods) if (m.Alive && m.Tid > 0) mod[m.Tid] = m.Work != null ? m.Name + "/" + m.Work : m.Name;
            foreach (var t in now)
            {
                long before;
                if (!p.TryGetValue(t.Tid, out before)) before = 0;
                double cpu = (t.Utime + t.Stime - before) / dtS;   // ticks are 1/100 s: ticks/dt = percent of one core
                string mn; if (!mod.TryGetValue(t.Tid, out mn)) mn = null;
                rows.Add(new HotRow { Tid = t.Tid, Comm = t.Comm, Cpu = cpu, Mod = mn });
            }
            rows.Sort((a, b) => b.Cpu.CompareTo(a.Cpu));
            if (rows.Count > top) rows.RemoveRange(top, rows.Count - top);
            return rows;
        }

        /// <summary>{"mod":[...],"hot":[top N by CPU delta, each with "mod":name|null]}; hot is [] without a previous snapshot.</summary>
        public static string Json(List<ProcThread> now, List<ProcThread> prev, double dtS, int top)
        {
            var sb = new StringBuilder("{\"mod\":[");
            bool first = true;
            var mods = Snapshot();
            Resolve(mods, now);
            foreach (var m in mods)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(Q(m.Name)).Append(",\"tid\":").Append(m.Tid).Append(",\"managed\":").Append(m.ManagedId)
                  .Append(",\"since_s\":").Append(Nm(Math.Round(m.SinceS, 1))).Append(",\"alive\":").Append(m.Alive ? "true" : "false")
                  .Append(",\"work\":").Append(m.Work == null ? "null" : Q(m.Work)).Append(",\"work_count\":").Append(m.WorkCount).Append('}');
            }
            sb.Append("],\"hot\":[");
            first = true;
            foreach (var h in Hot(now, prev, dtS, top))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"tid\":").Append(h.Tid).Append(",\"comm\":").Append(Q(h.Comm)).Append(",\"cpu\":").Append(Nm(Math.Round(h.Cpu, 1)))
                  .Append(",\"mod\":").Append(h.Mod == null ? "null" : Q(h.Mod)).Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
