// T-220: orderly shutdown helpers of the mod (pure, no Unity): a joint-budget join of threads and the one-line "shutdown" log record.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VoiceBotBridge.Ui
{
    internal static class ModShutdown
    {
        /// <summary>Joins each thread within what is left of the shared budget; returns the names of the threads still alive. Never throws.</summary>
        public static List<string> JoinAll(IList<Thread> threads, int budgetMs, Func<double> clockMs = null)
        {
            var alive = new List<string>();
            if (threads == null) return alive;
            Func<double> clock = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            double start = clock();
            foreach (var t in threads)
            {
                if (t == null) continue;
                try
                {
                    int left = (int)Math.Max(0, budgetMs - (clock() - start));
                    if (t.IsAlive && !t.Join(left) && t.IsAlive) alive.Add(t.Name ?? "?");
                }
                catch { }
            }
            return alive;
        }

        internal static List<string> AliveOf(List<ModThreadInfo> snapshot)
        {
            var l = new List<string>();
            if (snapshot != null) foreach (var s in snapshot) if (s != null && s.Alive && s.Name != "pool") l.Add(s.Name);
            return l;
        }

        internal static List<string> BusyOf(List<ModThreadInfo> snapshot)
        {
            var l = new List<string>();
            if (snapshot != null) foreach (var s in snapshot) if (s != null && s.Alive && s.Name == "pool" && s.Work != null) l.Add("pool/" + s.Work);
            return l;
        }

        private static void Names(StringBuilder sb, List<string> names)
        {
            sb.Append(names.Count.ToString(CultureInfo.InvariantCulture));
            if (names.Count > 0) sb.Append(" [").Append(string.Join(", ", names.ToArray())).Append(']');
        }

        /// <summary>"shutdown (bepinex, quit): dispose 12.3 ms, mod threads alive 0, pool busy 0". Never throws.</summary>
        public static string Line(string entry, string why, double disposeMs, List<ModThreadInfo> snapshot)
        {
            try
            {
                var sb = new StringBuilder("shutdown (").Append(entry ?? "?").Append(", ").Append(why ?? "?").Append("): dispose ");
                if (double.IsNaN(disposeMs) || double.IsInfinity(disposeMs)) sb.Append('?');
                else sb.Append(disposeMs.ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(" ms, mod threads alive ");
                Names(sb, AliveOf(snapshot));
                sb.Append(", pool busy ");
                Names(sb, BusyOf(snapshot));
                return sb.ToString();
            }
            catch (Exception e) { return "shutdown: line failed " + e.GetType().Name; }
        }

        public const string UiFile = "voicebot_ui_shutdown.log", DevFile = "voicebot_ui_dev_shutdown.log";

        /// <summary>T-232: overwrites the file with one line "&lt;utc ISO-8601&gt; &lt;line&gt;\n" (UTF-8, no BOM). Never throws; false = not written.</summary>
        public static bool WriteRecord(string dir, string fileName, string line, DateTime utcNow)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName) || !Directory.Exists(dir)) return false;
                string text = (line ?? "").Replace('\n', ' ').Replace('\r', ' ');
                File.WriteAllText(Path.Combine(dir, fileName), utcNow.ToString("o", CultureInfo.InvariantCulture) + " " + text + "\n", new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        /// <summary>T-232: the line of the previous exit or null (no file / empty / error). Never throws.</summary>
        public static string ReadPrevious(string dir, string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return null;
                string path = Path.Combine(dir, fileName);
                if (!File.Exists(path)) return null;
                string t = File.ReadAllText(path, Encoding.UTF8).Trim();
                return t.Length == 0 ? null : t;
            }
            catch { return null; }
        }

        private static string RecordSelfTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sptui-t232-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (WriteRecord(dir, UiFile, "x", DateTime.UtcNow)) return "WriteRecord into a missing dir returned true";
                if (ReadPrevious(dir, UiFile) != null) return "ReadPrevious in a missing dir";
                Directory.CreateDirectory(dir);
                if (ReadPrevious(dir, UiFile) != null) return "ReadPrevious without file";
                File.WriteAllText(Path.Combine(dir, DevFile), "");
                if (ReadPrevious(dir, DevFile) != null) return "ReadPrevious of an empty file";
                var t = new DateTime(2026, 10, 23, 15, 0, 59, DateTimeKind.Utc);
                if (!WriteRecord(dir, UiFile, "shutdown (a, quit): dispose 1 ms", t)) return "WriteRecord failed";
                string r = ReadPrevious(dir, UiFile);
                if (r == null || !r.EndsWith(" shutdown (a, quit): dispose 1 ms") || !r.StartsWith("2026-10-23T15:00:59")) return "read back: " + r;
                if (!WriteRecord(dir, UiFile, "second\nline", t.AddSeconds(1))) return "second WriteRecord failed";
                string[] lines = File.ReadAllLines(Path.Combine(dir, UiFile));
                if (lines.Length != 1 || !lines[0].EndsWith(" second line")) return "overwrite: " + string.Join("|", lines);
                if (File.ReadAllBytes(Path.Combine(dir, UiFile))[0] == 0xEF) return "BOM written";
                return null;
            }
            catch (Exception e) { return "record: " + e.GetType().Name + ": " + e.Message; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        public static string SelfTest()
        {
            try
            {
                var quick = new Thread(() => Thread.Sleep(50)) { IsBackground = true, Name = "t-quick" };
                var hang = new Thread(() => { ModThreads.Enter("t-hang"); try { Thread.Sleep(5000); } finally { ModThreads.Exit(); } }) { IsBackground = true, Name = "t-hang" };
                quick.Start(); hang.Start();
                var sw = Stopwatch.StartNew();
                var left = JoinAll(new[] { quick, hang }, 300);
                sw.Stop();
                if (sw.ElapsedMilliseconds > 400) return "JoinAll took " + sw.ElapsedMilliseconds + " ms";
                if (left.Count != 1 || left[0] != "t-hang") return "JoinAll left: " + string.Join(",", left.ToArray());
                if (JoinAll(null, 10).Count != 0) return "JoinAll(null)";

                string empty = Line("bepinex", "quit", 8.123, new List<ModThreadInfo>());
                if (empty != "shutdown (bepinex, quit): dispose 8.1 ms, mod threads alive 0, pool busy 0") return "empty line: " + empty;
                var snap = new List<ModThreadInfo>
                {
                    new ModThreadInfo { Name = "sptui-coverage", Alive = true },
                    new ModThreadInfo { Name = "pool", Alive = true, Work = "sar.solve" },
                    new ModThreadInfo { Name = "pool", Alive = true },
                    new ModThreadInfo { Name = "sptui-dead", Alive = false },
                };
                string full = Line("bepinex", "quit", 1, snap);
                if (full != "shutdown (bepinex, quit): dispose 1 ms, mod threads alive 1 [sptui-coverage], pool busy 1 [pool/sar.solve]") return "full line: " + full;
                if (Line(null, null, double.NaN, null) != "shutdown (?, ?): dispose ? ms, mod threads alive 0, pool busy 0") return "null line: " + Line(null, null, double.NaN, null);
                return RecordSelfTest();
            }
            catch (Exception e) { return "mod shutdown: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
