// T-212: a pure reader of the per-thread view of /proc (Linux TID, comm, state, CPU ticks). No Unity, no game types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VoiceBotBridge.Ui
{
    internal struct ProcThread { public int Tid; public string Comm; public char State; public long Utime, Stime; }

    internal static class ProcThreads
    {
        /// <summary>Under Proton "/proc" is reached as "Z:\proc"; null when neither exists.</summary>
        public static string DefaultRoot()
        {
            try { if (Directory.Exists("Z:\\proc")) return "Z:\\proc"; } catch { }
            try { if (Directory.Exists("/proc")) return "/proc"; } catch { }
            return null;
        }

        /// <summary>"pid (comm) S ppid ... utime stime ..." where comm may contain spaces and ')': parse after the LAST ')'.</summary>
        public static bool TryParseStat(string stat, out char state, out long utime, out long stime)
        {
            state = '?'; utime = 0; stime = 0;
            if (string.IsNullOrEmpty(stat)) return false;
            int close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 >= stat.Length) return false;
            string[] f = stat.Substring(close + 2).Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // f[0]=state(3) ... utime is field 14 -> f[11], stime field 15 -> f[12]
            if (f.Length < 13 || f[0].Length != 1) return false;
            long u, s;
            if (!long.TryParse(f[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out u)) return false;
            if (!long.TryParse(f[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out s)) return false;
            state = f[0][0]; utime = u; stime = s;
            return true;
        }

        /// <summary>Same, plus comm = the text between the first '(' and the last ')' (T-227: one file per thread instead of stat + comm).</summary>
        public static bool TryParseStat(string stat, out string comm, out char state, out long utime, out long stime)
        {
            comm = null;
            if (!TryParseStat(stat, out state, out utime, out stime)) return false;
            int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            comm = stat.Substring(open + 1, close - open - 1);
            return true;
        }

        /// <summary>Files read by Read/FindGameDir (stat + comm), process-wide; the memory line shows it as io.proc_reads (T-227).</summary>
        public static long FileReads;

        /// <summary>Comm of the game process (a Wine process shows the exe name; the wineserver is "wineserver"). Test seam.</summary>
        public static string GameComm = "Sea Power.exe";
        public const string ModPrefix = "sptui-";
        private static string _cacheRoot; private static string _cacheDir;

        public static void ResetCache() { _cacheRoot = null; _cacheDir = null; }

        /// <summary>Linux comm is cut to 15 chars.</summary>
        public static string CommOf(string name) { return name != null && name.Length > 15 ? name.Substring(0, 15) : name; }

        private static string ReadComm(string dir)
        {
            System.Threading.Interlocked.Increment(ref FileReads);
            try { return File.ReadAllText(Path.Combine(dir, "comm")).Trim(); } catch { return null; }
        }

        private static bool IsGameComm(string comm)
        {
            return comm != null && string.Equals(comm, CommOf(GameComm), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Directory root/&lt;pid&gt; of the game process. NOT root/self: under Wine "Z:\proc\self" is opened by the wineserver,
        /// so self (and thread-self) describe the wineserver. The game is the process whose comm is GameComm; among several
        /// candidates the one with an "sptui-*" task wins, then the one with the most tasks. null when none. Cached, re-validated by comm.
        /// </summary>
        public static string FindGameDir(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                if (_cacheRoot == root && _cacheDir != null && IsGameComm(ReadComm(_cacheDir))) return _cacheDir;
                string best = null; int bestScore = -1;
                foreach (string d in Directory.GetDirectories(root))
                {
                    try
                    {
                        int pid;
                        if (!int.TryParse(Path.GetFileName(d.TrimEnd('/', '\\')), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) continue;
                        if (!IsGameComm(ReadComm(d))) continue;
                        int tasks = 0; bool ours = false;
                        string td = Path.Combine(d, "task");
                        if (Directory.Exists(td))
                            foreach (string t in Directory.GetDirectories(td))
                            {
                                tasks++;
                                string c = ReadComm(t);
                                if (c != null && c.StartsWith(ModPrefix, StringComparison.Ordinal)) ours = true;
                            }
                        int score = (ours ? 1000000 : 0) + tasks;
                        if (score > bestScore) { bestScore = score; best = d; }
                    }
                    catch { }
                }
                _cacheRoot = root; _cacheDir = best;
                return best;
            }
            catch { return null; }
        }

        /// <summary>Threads of the GAME process (empty when it is not found).</summary>
        public static List<ProcThread> Read(string root)
        {
            var list = new List<ProcThread>();
            try
            {
                string game = FindGameDir(root);
                if (game == null) return list;
                string dir = Path.Combine(game, "task");
                if (!Directory.Exists(dir)) return list;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    try
                    {
                        string name = Path.GetFileName(d.TrimEnd('/', '\\'));
                        int tid;
                        if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out tid)) continue;
                        char st; long ut, sti;
                        string comm;
                        System.Threading.Interlocked.Increment(ref FileReads);
                        if (!TryParseStat(File.ReadAllText(Path.Combine(d, "stat")), out comm, out st, out ut, out sti)) continue;
                        list.Add(new ProcThread { Tid = tid, Comm = comm ?? "", State = st, Utime = ut, Stime = sti });
                    }
                    catch { }
                }
            }
            catch { list.Clear(); }
            return list;
        }

        /// <summary>TID of the thread named <paramref name="name"/> (by comm) among <paramref name="threads"/>, skipping <paramref name="claimed"/>; -1 when none.</summary>
        public static int MatchByComm(List<ProcThread> threads, string name, ICollection<int> claimed)
        {
            if (threads == null || string.IsNullOrEmpty(name)) return -1;
            string c = CommOf(name);
            int best = -1;
            foreach (var t in threads)
            {
                if (t.Comm != c || (claimed != null && claimed.Contains(t.Tid))) continue;
                if (best < 0 || t.Tid > best) best = t.Tid;   // a fresh thread has the highest TID
            }
            return best;
        }
    }
}
