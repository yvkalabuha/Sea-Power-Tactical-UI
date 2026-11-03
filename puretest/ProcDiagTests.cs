// T-303: test code of ProcThreads/ModThreads (fake /proc trees); lives in the stand, not in the public DLL.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace VoiceBotBridge.Ui
{
    internal static class ProcThreadsTests
    {
        private static void FakeProc(string root, int pid, string comm, int[] tids, string taskPrefix)
        {
            string p = Path.Combine(root, pid.ToString());
            Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "comm"), comm + "\n");
            foreach (int t in tids)
            {
                string d = Path.Combine(p, "task", t.ToString());
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "stat"), t + " (" + taskPrefix + " " + t + ") S 1 2 3 4 5 6 7 8 9 10 " + (t * 10) + " " + t + " 0\n");
                File.WriteAllText(Path.Combine(d, "comm"), taskPrefix + " " + t + "\n");
            }
        }

        public static string SelfTest()
        {
            char st; long u, s;
            if (!ProcThreads.TryParseStat("123 (Sea Power.exe) R 1 2 3 4 5 6 7 8 9 10 111 222 0 0", out st, out u, out s) || st != 'R' || u != 111 || s != 222) return "parse spaces: " + st + u + s;
            if (!ProcThreads.TryParseStat("5 (a) b) S 1 2 3 4 5 6 7 8 9 10 7 8 0", out st, out u, out s) || st != 'S' || u != 7 || s != 8) return "parse paren";
            string pc;
            if (!ProcThreads.TryParseStat("5 (a) b) S 1 2 3 4 5 6 7 8 9 10 7 8 0", out pc, out st, out u, out s) || pc != "a) b" || st != 'S') return "parse comm: " + pc;
            if (ProcThreads.TryParseStat("garbage", out pc, out st, out u, out s)) return "parse comm garbage";
            if (ProcThreads.TryParseStat("", out st, out u, out s)) return "empty";
            if (ProcThreads.TryParseStat(null, out st, out u, out s)) return "null";
            if (ProcThreads.TryParseStat("garbage", out st, out u, out s)) return "garbage";
            if (ProcThreads.TryParseStat("1 (x) R 1 2", out st, out u, out s)) return "short";
            if (ProcThreads.TryParseStat("1 (x) R 1 2 3 4 5 6 7 8 9 10 aa bb", out st, out u, out s)) return "non numeric";
            if (ProcThreads.Read(null).Count != 0 || ProcThreads.Read("/no/such/dir").Count != 0) return "read of a missing root must be empty";
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "proc_threads_" + Guid.NewGuid().ToString("N"));
                try
                {
                    // decoy: the wineserver (many tasks, no sptui-*), a game launcher, and the game
                    FakeProc(tmp, 100, "wineserver", new[] { 100, 101, 102, 103, 104 }, "wine");
                    FakeProc(tmp, 200, "Sea Power.exe", new[] { 200, 201, 202 }, "g");
                    FakeProc(tmp, 300, "bash", new[] { 300 }, "b");
                    Directory.CreateDirectory(Path.Combine(tmp, "self", "task", "100"));   // self is the wineserver: must be ignored
                    File.WriteAllText(Path.Combine(tmp, "self", "task", "100", "stat"), "100 (x) S 1 2 3 4 5 6 7 8 9 10 1 1 0\n");
                    Directory.CreateDirectory(Path.Combine(tmp, "self", "task", "bad"));
                    ProcThreads.ResetCache();
                    string gd = ProcThreads.FindGameDir(tmp);
                    if (gd == null || Path.GetFileName(gd) != "200") return "game dir " + gd;
                    long reads0 = System.Threading.Interlocked.Read(ref ProcThreads.FileReads);
                    var l = ProcThreads.Read(tmp);
                    long readsDelta = System.Threading.Interlocked.Read(ref ProcThreads.FileReads) - reads0;
                    if (readsDelta != 4) return "reads per Read (1 comm of the cached game dir + 3 stat, no per-thread comm): expected 4, got " + readsDelta;
                    if (l.Count != 3) return "fake dir count " + l.Count;
                    l.Sort((a2, b2) => a2.Tid.CompareTo(b2.Tid));
                    if (l[1].Tid != 201 || l[1].Comm != "g 201" || l[1].Utime != 2010 || l[1].Stime != 201) return "fake dir fields";
                    foreach (var t in l) if (t.Tid < 200) return "a wineserver thread leaked: " + t.Tid;
                    // a game with an sptui-* task wins over a bigger game-named process without one
                    FakeProc(tmp, 400, "Sea Power.exe", new[] { 400, 401, 402, 403, 404, 405 }, "h");
                    ProcThreads.ResetCache();
                    if (Path.GetFileName(ProcThreads.FindGameDir(tmp)) != "400") return "bigger candidate should win without sptui";
                    File.WriteAllText(Path.Combine(tmp, "200", "task", "202", "comm"), "sptui-coverage\n");
                    File.WriteAllText(Path.Combine(tmp, "200", "task", "202", "stat"), "202 (sptui-coverage) S 1 2 3 4 5 6 7 8 9 10 2020 202 0\n");
                    ProcThreads.ResetCache();
                    if (Path.GetFileName(ProcThreads.FindGameDir(tmp)) != "200") return "the process with an sptui thread should win";
                    l = ProcThreads.Read(tmp);
                    if (ProcThreads.MatchByComm(l, "sptui-coverage", null) != 202) return "match by comm";
                    if (ProcThreads.MatchByComm(l, "sptui-coverage", new HashSet<int> { 202 }) != -1) return "claimed tid must be skipped";
                    if (ProcThreads.MatchByComm(l, "sptui-nothing", null) != -1) return "unknown name";
                    if (ProcThreads.CommOf("sptui-a-very-long-thread-name") != "sptui-a-very-lo") return "comm truncation";
                    // no game at all: empty, never the wineserver
                    Directory.Delete(Path.Combine(tmp, "200"), true); Directory.Delete(Path.Combine(tmp, "400"), true);
                    ProcThreads.ResetCache();
                    if (ProcThreads.Read(tmp).Count != 0) return "no game must give an empty list";
                }
                finally { ProcThreads.ResetCache(); try { Directory.Delete(tmp, true); } catch { } }
            }
            catch (Exception e) { return "proc threads: " + e.GetType().Name + ": " + e.Message; }
            return null;
        }
    }

    internal static class ModThreadsTests
    {
        public static string SelfTest()
        {
            try
            {
                // a fake /proc: the wineserver decoy (also has a thread "sptui-t1" as a trap: it must not be matched) and the game
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mod_threads_" + Guid.NewGuid().ToString("N"));
                try { return SelfTestIn(tmp); }
                finally { ModThreads.Root = null; ProcThreads.ResetCache(); try { System.IO.Directory.Delete(tmp, true); } catch { } }
            }
            catch (Exception e) { return "mod threads: " + e.GetType().Name + ": " + e.Message; }
            finally { ModThreads.Clock = null; ModThreads.Root = null; }
        }

        private static void FakeTask(string root, int pid, string pcomm, int tid, string comm, int ticks)
        {
            string d = System.IO.Path.Combine(root, pid.ToString(), "task", tid.ToString());
            System.IO.Directory.CreateDirectory(d);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, pid.ToString(), "comm"), pcomm + "\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "stat"), tid + " (" + comm + ") R 1 2 3 4 5 6 7 8 9 10 " + ticks + " 0 0\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "comm"), comm + "\n");
        }

        private static string SelfTestIn(string tmp)
        {
            {
                ModThreads.Root = tmp; ProcThreads.ResetCache();
                FakeTask(tmp, 100, "wineserver", 100, "wineserver", 5);
                FakeTask(tmp, 100, "wineserver", 101, "sptui-t1", 5);
                FakeTask(tmp, 200, "Sea Power.exe", 200, "Sea Power.exe", 100);
                FakeTask(tmp, 200, "Sea Power.exe", 201, "UnityMain", 50);
                FakeTask(tmp, 200, "Sea Power.exe", 205, "sptui-t1", 10);
                int tidInside = 205; bool exited = false;
                var gate = new ManualResetEvent(false);
                var th = new Thread(() =>
                {
                    ModThreads.Enter("sptui-t1");
                    try { gate.WaitOne(5000); }
                    finally { ModThreads.Exit(); exited = true; }
                });
                var prev = ProcThreads.Read(tmp);
                long readsAtEnter = Interlocked.Read(ref ProcThreads.FileReads);
                th.Start();
                Thread.Sleep(150);
                FakeTask(tmp, 200, "Sea Power.exe", 205, "sptui-t1", 40);
                FakeTask(tmp, 200, "Sea Power.exe", 200, "Sea Power.exe", 400);
                if (Interlocked.Read(ref ProcThreads.FileReads) != readsAtEnter) return "Enter scanned /proc";
                var mid = ProcThreads.Read(tmp);
                // T-227: Enter did not scan /proc, so Tid is still 0; Resolve (inside Json) fills it and writes it into the registry
                ModThreadInfo t0 = null; foreach (var s in ModThreads.Snapshot()) if (s.Name == "sptui-t1" && s.Alive) t0 = s;
                if (t0 == null || t0.Tid != 0) return "Enter must not resolve the tid: " + (t0 == null ? "none" : t0.Tid.ToString());
                string js = ModThreads.Json(mid, prev, 1.0, 3);
                ModThreadInfo t1 = null; foreach (var s in ModThreads.Snapshot()) if (s.Name == "sptui-t1" && s.Alive) t1 = s;
                if (t1 == null || t1.Tid != tidInside) return "t1 registered with tid " + (t1 == null ? "none" : t1.Tid.ToString()) + " vs " + tidInside + " (the wineserver tid is 101)";
                if (!js.StartsWith("{\"mod\":[") || !js.Contains("\"hot\":[{")) return "json shape / no hot rows: " + js;
                if (!js.Contains("\"tid\":205,\"managed\"")) return "mod[] tid is not the game thread: " + js;
                if (!js.Contains("\"mod\":\"sptui-t1\"")) return "t1 is not marked in hot: " + js;
                if (!js.Contains("\"tid\":200,\"comm\":\"Sea Power.exe\",\"cpu\":300")) return "hot lacks the busy game thread: " + js;
                if (js.Contains("\"tid\":101")) return "a wineserver thread leaked into the output: " + js;
                gate.Set();
                th.Join();
                if (!exited) return "thread did not exit";
                foreach (var s in ModThreads.Snapshot()) if (s.Name == "sptui-t1" && s.Alive) return "t1 still alive after Exit";
                string workDuring = null; long count = 0; string workAfter = "x";
                var done = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    using (ModThreads.Work("sar.solve"))
                    {
                        foreach (var s in ModThreads.Snapshot()) if (s.ManagedId == Thread.CurrentThread.ManagedThreadId) workDuring = s.Work;
                    }
                    foreach (var s in ModThreads.Snapshot()) if (s.ManagedId == Thread.CurrentThread.ManagedThreadId) { workAfter = s.Work; count = s.WorkCount; }
                    done.Set();
                });
                if (!done.WaitOne(5000)) return "pool item timeout";
                if (workDuring != "sar.solve" || workAfter != null || count < 1) return "pool work: " + workDuring + "/" + workAfter + "/" + count;
                // no /proc: hot is empty, not an error
                string none = ModThreads.Json(new List<ProcThread>(), null, 1, 3);
                if (!none.Contains("\"hot\":[]")) return "hot without prev: " + none;
                // the dead entry is pruned after ModThreads.KeepDeadS
                double fake = 0; ModThreads.Clock = () => fake;
                var th2 = new Thread(() => { ModThreads.Enter("t2"); ModThreads.Exit(); }); th2.Start(); th2.Join();
                fake = ModThreads.KeepDeadS + 5;
                foreach (var s in ModThreads.Snapshot()) if (s.Name == "t2") return "dead entry not pruned";
                return null;
            }
        }
    }
}
