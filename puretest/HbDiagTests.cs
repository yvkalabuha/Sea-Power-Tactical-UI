// T-303: test code of ProcTable/HbLoop (fake /proc trees, allocation probe); lives in the stand, not in the public DLL.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace VoiceBotBridge.Ui
{
    internal static class ProcTableTests
    {
        /// <summary>GC.GetAllocatedBytesForCurrentThread as a delegate (absent in netstandard2.1 at compile time); null when unavailable.</summary>
        private const string MethodName = "GetAllocatedBytes" + "ForCurrentThread";   // a const: not a game member literal
        internal static Func<long> AllocProbe()
        {
            try
            {
                var mi = typeof(GC).GetMethod(MethodName, Type.EmptyTypes);
                return mi == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
            }
            catch { return null; }
        }

        internal static void MakeFake(string root, int gamePid, int tasks, bool withExtras)
        {
            string wp = Path.Combine(root, "100");
            Directory.CreateDirectory(Path.Combine(wp, "task", "100")); Directory.CreateDirectory(Path.Combine(wp, "task", "101"));
            File.WriteAllText(Path.Combine(wp, "comm"), "wineserver\n");
            File.WriteAllText(Path.Combine(wp, "task", "100", "stat"), "100 (wineserver) S 1 2 3 4 5 6 7 8 9 10 1 1 0\n");
            File.WriteAllText(Path.Combine(wp, "task", "101", "stat"), "101 (wineserver) S 1 2 3 4 5 6 7 8 9 10 1 1 0\n");
            string gp = Path.Combine(root, gamePid.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(gp).FullName, "comm"), "Sea Power.exe\n");
            for (int i = 0; i < tasks; i++)
            {
                int tid = gamePid + i;
                string d = Path.Combine(gp, "task", tid.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(d);
                string comm = i == 0 ? "Sea Power.exe" : i == 1 ? "sptui-hb" : "Thread " + i;
                File.WriteAllText(Path.Combine(d, "stat"), tid + " (" + comm + ") S 1 2 3 4 5 6 7 8 9 10 " + (tid * 10) + " " + tid + " 0\n");
                File.WriteAllText(Path.Combine(d, "comm"), comm + "\n");
                if (withExtras && i != 2) File.WriteAllText(Path.Combine(d, "wchan"), (i % 2 == 0 ? "0" : "futex_wait_queue") + "\n");
                if (withExtras && i != 3) File.WriteAllText(Path.Combine(d, "status"), "Name:\tx\nvoluntary_ctxt_switches:\t" + (i + 5) + "\nnonvoluntary_ctxt_switches:\t" + (i + 1) + "\n");
            }
        }

        public static string SelfTest()
        {
            byte[] bs = System.Text.Encoding.ASCII.GetBytes("5 (a) b) S 1 2 3 4 5 6 7 8 9 10 77 88 0\n");
            int cs, cl; byte st; long u, s;
            if (!ProcTable.ParseStat(bs, bs.Length, out cs, out cl, out st, out u, out s) || st != (byte)'S' || u != 77 || s != 88 || System.Text.Encoding.ASCII.GetString(bs, cs, cl) != "a) b") return "ParseStat paren";
            byte[] bad = System.Text.Encoding.ASCII.GetBytes("1 (x) R 1 2");
            if (ProcTable.ParseStat(bad, bad.Length, out cs, out cl, out st, out u, out s)) return "ParseStat short";
            byte[] bad2 = System.Text.Encoding.ASCII.GetBytes("1 (x) R 1 2 3 4 5 6 7 8 9 10 aa bb 0");
            if (ProcTable.ParseStat(bad2, bad2.Length, out cs, out cl, out st, out u, out s)) return "ParseStat non numeric";
            byte[] stat = System.Text.Encoding.ASCII.GetBytes("Name:\tx\nvoluntary_ctxt_switches:\t12\nnonvoluntary_ctxt_switches:\t3\n");
            if (ProcTable.FindField(stat, stat.Length, ProcTable.KeyVcs) != 12 || ProcTable.FindField(stat, stat.Length, ProcTable.KeyNvcs) != 3) return "FindField";
            byte[] none = System.Text.Encoding.ASCII.GetBytes("Name:\tx\n");
            if (ProcTable.FindField(none, none.Length, ProcTable.KeyVcs) != -1) return "FindField absent";
            int tl; byte[] w = System.Text.Encoding.ASCII.GetBytes("futex_wait_queue\n");
            int ts = ProcTable.FirstToken(w, w.Length, out tl);
            if (ts != 0 || tl != 16) return "FirstToken wchan";
            w = System.Text.Encoding.ASCII.GetBytes("0\n");
            if (ProcTable.FirstToken(w, w.Length, out tl) != 0 || tl != 1) return "FirstToken zero";
            if (ProcTable.FirstToken(w, 0, out tl) != -1) return "FirstToken empty";

            string reason;
            var tnull = new ProcTable(null);
            if (tnull.Snapshot("t", 1, 3, 8, 1, "s", out reason) || reason != "no_root") return "null root: " + reason;
            string tmp = Path.Combine(Path.GetTempPath(), "proc_table_" + Guid.NewGuid().ToString("N"));
            string gc0 = ProcThreads.GameComm;
            try
            {
                Directory.CreateDirectory(tmp);
                ProcThreads.ResetCache();
                var tnone = new ProcTable(tmp);
                if (tnone.Snapshot("t", 1, 3, 8, 1, "s", out reason) || reason != "no_game_dir") return "no game: " + reason;
                MakeFake(tmp, 200, 4, true);
                ProcThreads.ResetCache();
                var t = new ProcTable(tmp);
                if (!t.Snapshot("12:00:00.000", 1, 3, 8.4, 528, "drain/gui", out reason)) return "snapshot: " + reason;
                string text = System.Text.Encoding.ASCII.GetString(t.Buffer, 0, t.Length);
                if (!text.StartsWith("=== 12:00:00.000 stall snap=1/3 stuck_s=8 frame=528 stage=drain/gui pid=200 root=")) return "header: " + text;
                if (!text.Contains(" threads=4\n")) return "threads count: " + text;
                if (!text.Contains("=== end snap=1 ms=")) return "end line";
                if (text.Contains("101 wineserver") || text.Contains("wineserver")) return "wineserver leaked";
                if (!text.Contains("200 Sea_Power.exe S 0 2000 200 5 1 -")) return "row 0: " + text;
                if (!text.Contains("201 sptui-hb S futex_wait_queue 2010 201 6 2 -")) return "row 1: " + text;
                if (!text.Contains("202 Thread_2 S - 2020 202 7 3 -")) return "row 2 (no wchan): " + text;
                if (!text.Contains("203 Thread_3 S futex_wait_queue 2030 203 - - -")) return "row 3 (no status): " + text;
                if (t.LastTruncated || t.LastReads < 4) return "reads " + t.LastReads;
                // header sink: the header reaches the file before the body
                string hf = Path.Combine(tmp, "hdr.txt");
                using (var fs = new FileStream(hf, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    t.HeaderSink = fs;
                    t.Snapshot("x", 2, 3, 30, 5, "s", out reason);
                    t.HeaderSink = null;
                    if (t.HeaderWritten <= 0 || t.HeaderWritten >= t.Length) return "header written " + t.HeaderWritten;
                }
                // overflow
                MakeFake(tmp, 300, 40, true);
                Directory.Delete(Path.Combine(tmp, "200"), true);
                ProcThreads.ResetCache();
                var small = new ProcTable(tmp, 1024);
                if (!small.Snapshot("t", 1, 3, 8, 1, "s", out reason)) return "small: " + reason;
                string st2 = System.Text.Encoding.ASCII.GetString(small.Buffer, 0, small.Length);
                if (!small.LastTruncated || !st2.Contains("truncated") || !st2.Contains("=== end")) return "truncation: " + st2;
                if (small.Length > 1024) return "buffer overrun";
                // allocations: numeric helpers are free, the snapshot is bounded
                var probe = AllocProbe();
                if (probe != null)
                {
                    var big = new ProcTable(tmp);
                    big.Snapshot("t", 1, 3, 8, 1, "s", out reason);
                    long a0 = probe(), b0 = probe(); long baseline = b0 - a0;
                    long x = 0;
                    for (int i = 0; i < 1000; i++)
                    {
                        x += ProcTable.FindField(stat, stat.Length, ProcTable.KeyVcs);
                        ProcTable.ParseStat(bs, bs.Length, out cs, out cl, out st, out u, out s);
                        x += ProcTable.FirstToken(w, w.Length, out tl);
                    }
                    long a1 = probe();
                    if (a1 - b0 - baseline > 0 || x == 0) return "helpers allocate " + (a1 - b0 - baseline);
                    long c0 = probe();
                    big.Snapshot("t", 1, 3, 8, 1, "s", out reason);
                    long used = probe() - c0;
                    if (used > 200 * 1024) return "snapshot of 40 tasks allocated " + used;
                }
                // the real /proc, when there is one
                if (File.Exists("/proc/self/comm"))
                {
                    ProcThreads.GameComm = File.ReadAllText("/proc/self/comm").Trim();
                    ProcThreads.ResetCache();
                    var real = new ProcTable("/proc");
                    if (!real.Snapshot("t", 1, 3, 8, 1, "s", out reason)) return "real /proc: " + reason;
                    string rt = System.Text.Encoding.ASCII.GetString(real.Buffer, 0, real.Length);
                    if (rt.Split('\n').Length < 5) return "real /proc rows: " + rt;
                }
            }
            catch (Exception e) { return "proc table: " + e.GetType().Name + ": " + e.Message; }
            finally { ProcThreads.GameComm = gc0; ProcThreads.ResetCache(); try { Directory.Delete(tmp, true); } catch { } }
            return null;
        }
    }

    internal static class HbLoopTests
    {
        private static string Slurp(string p) { try { return File.Exists(p) ? File.ReadAllText(p) : ""; } catch { return ""; } }

        private static int Count(string text, string what)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
            return n;
        }

        /// <summary>T-305: the "main thread stuck" line must survive the next heartbeat lines (one writer, no overwrite).</summary>
        internal static string StuckSurvives()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "hb_stuck_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                string text = "x" + new string('y', 200);
                // scenarios 1 (old wiring, no HbWrite) and 3 (HbWrite = hb.WriteNow)
                for (int mode = 0; mode < 2; mode++)
                {
                    string hbp = Path.Combine(tmp, "hb" + mode + ".log"), st = Path.Combine(tmp, "stall" + mode + ".txt");
                    long frame = 1; double clock = 0;
                    long opens0 = VoiceBotBridge.Heartbeat.Opens;
                    var hb = new VoiceBotBridge.Heartbeat(hbp, () => text, 256 * 1024, 2.0, true) { ExternalDrain = true };
                    var o = new HbLoopOptions { Frame = () => frame, Stage = () => "s", Drains = new Action[] { hb.DrainNow }, HbPath = hbp, StallPath = st, ProcRoot = Path.Combine(tmp, "nothing"), Clock = () => clock };
                    if (mode == 1) o.HbWrite = hb.WriteNow;
                    using (var loop = new HbLoop(o))
                    {
                        hb.Tick(0); loop.Step(0);
                        for (int t = 1; t <= 8; t++) { clock = t; loop.Step(clock); }
                        frame = 2;
                        clock = 10; hb.Tick(10); loop.Step(clock);
                        clock = 12; hb.Tick(12); loop.Step(clock);
                    }
                    hb.WaitIdle(500); hb.Dispose();
                    string all = Slurp(hbp);
                    if (Count(all, "main thread stuck") != 1) return "mode " + mode + ": stuck line lost: " + all;
                    if (Count(all, " xyyyy") != 3) return "mode " + mode + ": heartbeat lines: " + all;
                    string[] lines = all.Split('\n');
                    if (lines.Length != 5 || lines[4].Length != 0) return "mode " + mode + ": line count " + lines.Length;
                    for (int i = 0; i < 4; i++)
                    {
                        bool stuckLine = lines[i].Contains("main thread stuck");
                        if (stuckLine ? !lines[i].EndsWith("stage=s") : lines[i].Length != 13 + text.Length) return "mode " + mode + ": broken line " + i + ": " + lines[i];
                    }
                    if (mode == 1 && VoiceBotBridge.Heartbeat.Opens - opens0 != 1) return "second FileStream on the heartbeat file: opens " + (VoiceBotBridge.Heartbeat.Opens - opens0);
                }
                // scenario 2: a foreign appender between two lines
                {
                    string hbp = Path.Combine(tmp, "hbf.log");
                    var hb = new VoiceBotBridge.Heartbeat(hbp, () => text, 256 * 1024, 2.0, true) { ExternalDrain = true };
                    hb.Tick(0); hb.DrainNow();
                    using (var fs = new FileStream(hbp, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { var b = System.Text.Encoding.UTF8.GetBytes("foreign\n"); fs.Write(b, 0, b.Length); }
                    hb.Tick(2); hb.DrainNow(); hb.Tick(4); hb.DrainNow();
                    hb.WaitIdle(500); hb.Dispose();
                    string all = Slurp(hbp);
                    if (Count(all, "foreign\n") != 1 || Count(all, " xyyyy") != 3) return "foreign line lost: " + all;
                }
                // scenario 4: a custom HbWrite takes the line, nothing is written to HbPath
                {
                    string hbp = Path.Combine(tmp, "hbc.log");
                    long frame = 1; double clock = 0;
                    var got = new List<string>();
                    var o = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hbp, StallPath = Path.Combine(tmp, "stallc.txt"), ProcRoot = Path.Combine(tmp, "nothing"), Clock = () => clock, HbWrite = s2 => got.Add(s2) };
                    using (var loop = new HbLoop(o)) { for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); } }
                    if (got.Count != 1 || !got[0].Contains("main thread stuck") || File.Exists(hbp)) return "custom HbWrite: " + got.Count + " exists=" + File.Exists(hbp);
                }
            }
            catch (Exception e) { return "hb_stuck: " + e.GetType().Name + ": " + e.Message; }
            finally { ProcThreads.ResetCache(); try { Directory.Delete(tmp, true); } catch { } }
            return null;
        }

        /// <summary>T-303: a missing /proc (Windows without Z:\proc) never throws, writes one "unavailable" line, the stuck lines go on.</summary>
        internal static string NoProcSafe()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "hb_noproc_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                string reason;
                if (new ProcTable(null).Snapshot("t", 1, 3, 8, 1, "s", out reason) || reason != "no_root") return "null root: " + reason;
                var roots = new[] { Path.Combine(tmp, "nothing"), "Z:\\proc-none-" + Guid.NewGuid().ToString("N") };
                for (int r = 0; r < roots.Length; r++)
                {
                    string hb = Path.Combine(tmp, "hb" + r + ".log"), st = Path.Combine(tmp, "stall" + r + ".txt");
                    long frame = 1; double clock = 0;
                    var o = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hb, StallPath = st, ProcRoot = roots[r], Clock = () => clock };
                    using (var loop = new HbLoop(o))
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            frame++; clock += 1; loop.Step(clock);
                            for (int t = 0; t < 9; t++) { clock += 1; loop.Step(clock); }
                        }
                        string u = Slurp(st);
                        if (Count(u, "stall threads unavailable") != 1) return "unavailable lines (root " + r + "): " + u;
                        if (!loop.ProcUnavailable || loop.Snapshots != 0) return "flags (root " + r + ")";
                        if (Count(Slurp(hb), "main thread stuck") != 3) return "stuck lines (root " + r + "): " + Slurp(hb);
                    }
                    if (ProcThreads.Read(roots[r]).Count != 0) return "Read of a missing root must be empty";
                }
                if (!ModThreads.Json(new List<ProcThread>(), null, 1, 3).Contains("\"hot\":[]")) return "Json without /proc";
                // an unwritable stall path is swallowed
                var o2 = new HbLoopOptions { Frame = () => 1, HbPath = Path.Combine(tmp, "h.log"), StallPath = Path.Combine(tmp, "no", "such", "dir", "stall.txt"), ProcRoot = Path.Combine(tmp, "nothing"), Clock = () => 0 };
                using (var loop = new HbLoop(o2)) { for (int t = 0; t < 12; t++) loop.Step(t); }
            }
            catch (Exception e) { return "no proc: " + e.GetType().Name + ": " + e.Message; }
            finally { ProcThreads.ResetCache(); try { Directory.Delete(tmp, true); } catch { } }
            return null;
        }

        public static string SelfTest()
        {
            // StallSeries
            var s = new StallSeries(new[] { 8.0, 30.0, 120.0 });
            if (s.Due(1, 0) != -1) return "series first";
            int[] seen = new int[3];
            for (int t = 1; t <= 200; t++) { int d = s.Due(1, t); if (d >= 0) seen[d]++; }
            if (seen[0] != 1 || seen[1] != 1 || seen[2] != 1) return "series counts " + seen[0] + seen[1] + seen[2];
            if (s.Due(2, 201) != -1) return "series reset";
            for (int t = 202; t < 209; t++) if (s.Due(2, t) != -1) return "series early after reset";
            if (s.Due(2, 209) != 0) return "series second stall";
            if (s.Due(2, 1000) != 2) return "series jump gives the highest";
            if (HbLoop.StuckLine(new DateTime(2026, 1, 1, 12, 0, 0), 9.0, 528, "drain/gui") != "12:00:00.000 main thread stuck for 9 s frame=528 stage=drain/gui\n") return "stuck line format";

            string tmp = Path.Combine(Path.GetTempPath(), "hb_loop_" + Guid.NewGuid().ToString("N"));
            string gc0 = ProcThreads.GameComm;
            try
            {
                Directory.CreateDirectory(tmp);
                string proc = Path.Combine(tmp, "proc"); Directory.CreateDirectory(proc);
                ProcTableTests.MakeFake(proc, 200, 4, true);
                ProcThreads.ResetCache();
                string hb = Path.Combine(tmp, "hb.log"), st = Path.Combine(tmp, "stall.txt");
                long frame = 1; double clock = 0; int drains = 0;
                var o = new HbLoopOptions
                {
                    Frame = () => frame, Stage = () => "drain/gui", Drains = new Action[] { () => drains++ },
                    HbPath = hb, StallPath = st, ProcRoot = proc, Clock = () => clock, Now = () => new DateTime(2026, 1, 1, 12, 0, 0)
                };
                using (var loop = new HbLoop(o))
                {
                    for (int t = 0; t < 8; t++) { clock = t; loop.Step(clock); }
                    if (File.Exists(st) || File.Exists(hb)) return "files before a stall";
                    clock = 8; loop.Step(clock);
                    if (Count(Slurp(hb), "main thread stuck for 8 s frame=1 stage=drain/gui") != 1) return "stuck line: " + Slurp(hb);
                    if (Count(Slurp(st), "snap=1/3") != 1 || Count(Slurp(st), "=== end snap=1") != 1) return "snap 1: " + Slurp(st);
                    for (int t = 9; t <= 29; t++) { clock = t; loop.Step(clock); }
                    if (Count(Slurp(st), "snap=2/3") != 0) return "snap 2 early";
                    clock = 30; loop.Step(clock);
                    if (Count(Slurp(st), "snap=2/3") != 1) return "snap 2";
                    clock = 120; loop.Step(clock);
                    if (Count(Slurp(st), "snap=3/3") != 1) return "snap 3";
                    clock = 200; loop.Step(clock); clock = 201; loop.Step(clock);
                    if (Count(Slurp(st), "stall snap=") != 3 || Count(Slurp(hb), "main thread stuck") != 1) return "extra output after 200 s";
                    if (loop.Snapshots != 3) return "Snapshots " + loop.Snapshots;
                    if (drains < 10) return "drains " + drains;
                    // the next stall is a new series
                    frame = 2; clock = 202; loop.Step(clock);
                    clock = 210; loop.Step(clock);
                    if (Count(Slurp(hb), "main thread stuck") != 2 || Count(Slurp(st), "snap=1/3") != 2) return "second stall";
                }

                // FirstFlushIndex = 1: the first snapshot is held
                File.Delete(st); File.Delete(hb);
                frame = 10; clock = 0;
                var o2 = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hb, StallPath = st, ProcRoot = proc, Clock = () => clock, FirstFlushIndex = 1 };
                using (var loop = new HbLoop(o2))
                {
                    for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); }
                    if (File.Exists(st)) return "held snapshot written at 8 s";
                    clock = 30; loop.Step(clock);
                    string h = Slurp(st);
                    if (Count(h, "snap=1/3") != 1 || Count(h, "snap=2/3") != 1) return "held flush: " + h;
                }
                File.Delete(st);
                using (var loop = new HbLoop(o2))
                {
                    frame = 20; clock = 0;
                    for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); }
                    frame = 21; clock = 9; loop.Step(clock);
                    for (int t = 10; t <= 31; t++) { clock = t; loop.Step(clock); }
                    if (File.Exists(st) && Count(Slurp(st), "snap=1/3") != 0) return "held snapshot of a finished stall leaked";
                }

                // T-305 Q1: before the first mission only a 120 s stall reaches the stall file
                {
                    bool armed = false;
                    File.Delete(st); File.Delete(hb);
                    frame = 50; clock = 0;
                    var o6 = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hb, StallPath = st, ProcRoot = proc, Clock = () => clock, Armed = () => armed };
                    using (var loop = new HbLoop(o6))
                    {
                        for (int t = 0; t <= 60; t++) { clock = t; loop.Step(clock); }
                        if (Count(Slurp(hb), "main thread stuck") != 1) return "Q1: stuck line missing: " + Slurp(hb);
                        if (File.Exists(st)) return "Q1: stall file at 60 s before the first mission";
                        frame = 51; clock = 61; loop.Step(clock);
                        for (int t = 62; t <= 80; t++) { clock = t; loop.Step(clock); }
                        if (File.Exists(st)) return "Q1: held snapshot leaked";
                        frame = 52; clock = 100; loop.Step(clock);
                        for (int t = 101; t <= 220; t++) { clock = t; loop.Step(clock); }
                        string q = Slurp(st);
                        if (Count(q, "snap=2/3") != 1 || Count(q, "snap=3/3") != 1 || Count(q, "snap=1/3") != 0) return "Q1: 120 s stall: " + q;
                    }
                    File.Delete(st); File.Delete(hb);
                    armed = true; frame = 60; clock = 0;
                    using (var loop = new HbLoop(o6))
                    {
                        for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); }
                        if (Count(Slurp(st), "snap=1/3") != 1) return "Q1: armed must write snap 1 at 8 s: " + Slurp(st);
                    }
                    File.Delete(st); File.Delete(hb);
                    o6.Armed = null; frame = 70; clock = 0;
                    using (var loop = new HbLoop(o6))
                    {
                        for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); }
                        if (Count(Slurp(st), "snap=1/3") != 1) return "Q1: Armed=null must write snap 1 at 8 s";
                    }
                }

                // /proc unavailable: one line, the stuck line goes on
                File.Delete(st); File.Delete(hb);
                frame = 30; clock = 0;
                var o3 = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hb, StallPath = st, ProcRoot = Path.Combine(tmp, "nothing"), Clock = () => clock };
                using (var loop = new HbLoop(o3))
                {
                    for (int t = 0; t <= 8; t++) { clock = t; loop.Step(clock); }
                    frame = 31; clock = 9; loop.Step(clock);
                    for (int t = 10; t <= 18; t++) { clock = t; loop.Step(clock); }
                    string u = Slurp(st);
                    if (Count(u, "stall threads unavailable") != 1 || !u.Contains("reason=no_root") || !loop.ProcUnavailable) return "unavailable: " + u;
                    if (Count(Slurp(hb), "main thread stuck") != 2) return "stuck lines when unavailable";
                }

                // rotation
                File.Delete(st); File.Delete(hb);
                frame = 40; clock = 0;
                var o4 = new HbLoopOptions { Frame = () => frame, Stage = () => "s", HbPath = hb, StallPath = st, ProcRoot = proc, Clock = () => clock, StallMaxBytes = 600 };
                using (var loop = new HbLoop(o4))
                {
                    for (int round = 0; round < 4; round++)
                    {
                        frame = 41 + round; clock = round * 100;
                        loop.Step(clock);
                        clock += 8; loop.Step(clock);
                    }
                }
                if (!File.Exists(st + ".1")) return "no rotation";

                // allocations without a stall
                var probe = ProcTableTests.AllocProbe();
                if (probe != null)
                {
                    var o5 = new HbLoopOptions { Frame = FrameFn, Stage = null, HbPath = hb, StallPath = st, ProcRoot = proc, Clock = ClockFn, Drains = new Action[] { NoopDrain } };
                    using (var loop = new HbLoop(o5))
                    {
                        for (int i = 0; i < 100; i++) loop.Step(i);
                        long a0 = probe(), b0 = probe(); long baseline = b0 - a0;
                        for (int i = 0; i < 1000; i++) { _ticker += 0.01; loop.Step(_ticker); _tickFrame++; }
                        long a1 = probe();
                        if (a1 - b0 - baseline > 0) return "Step allocates " + (a1 - b0 - baseline);
                    }
                }

                // the real thread
                var live = new HbLoop(new HbLoopOptions { Frame = FrameFn, HbPath = hb, StallPath = st, ProcRoot = proc });
                if (!live.Start()) return "thread did not start";
                bool seenAlive = false;
                for (int i = 0; i < 40 && !seenAlive; i++) { seenAlive = ModThreads.AliveDedicated().Contains(HbLoop.ThreadName); if (!seenAlive) Thread.Sleep(50); }
                if (!seenAlive) return "sptui-hb not registered";
                if (Directory.Exists("/proc/self/task"))
                {
                    bool comm = false;
                    for (int i = 0; i < 20 && !comm; i++)
                    {
                        foreach (string d in Directory.GetDirectories("/proc/self/task"))
                            try { if (File.ReadAllText(Path.Combine(d, "comm")).Trim() == HbLoop.ThreadName) comm = true; } catch { }
                        if (!comm) Thread.Sleep(50);
                    }
                    if (!comm) return "no task with comm sptui-hb";
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                live.Dispose();
                if (sw.ElapsedMilliseconds > 700) return "Dispose took " + sw.ElapsedMilliseconds;
                if (ModThreads.AliveDedicated().Contains(HbLoop.ThreadName)) return "thread alive after Dispose";
            }
            catch (Exception e) { return "hb loop: " + e.GetType().Name + ": " + e.Message; }
            finally { ProcThreads.GameComm = gc0; ProcThreads.ResetCache(); try { Directory.Delete(tmp, true); } catch { } }
            return null;
        }

        private static double _ticker; private static long _tickFrame;
        private static long FrameFn() { return _tickFrame; }
        private static double ClockFn() { return _ticker; }
        private static void NoopDrain() { }
    }
}
