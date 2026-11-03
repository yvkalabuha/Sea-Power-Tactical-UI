using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Dev
{
    /// <summary>Ring of the last 2000 log lines (UI mod, this mod, any Error/Fatal). Thread safe.</summary>
    internal sealed class DevLogRing
    {
        public const int Capacity = 2000;
        public const int MaxText = 300;
        private static readonly string[] Levels = { "debug", "info", "message", "warning", "error", "fatal" };
        private readonly object _lock = new object();
        private readonly Queue<KeyValuePair<int, string>> _q = new Queue<KeyValuePair<int, string>>();

        public static int Rank(string level)
        {
            int i = level == null ? -1 : Array.IndexOf(Levels, level.ToLowerInvariant());
            return i < 0 ? 0 : i;
        }

        public int Count { get { lock (_lock) return _q.Count; } }

        public void Add(string level, string source, string text, DateTime utc)
        {
            string t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (t.Length > MaxText) t = t.Substring(0, MaxText - 3) + "...";
            string line = "[" + (level ?? "Info") + ":" + source + "] " + t;
            lock (_lock)
            {
                _q.Enqueue(new KeyValuePair<int, string>(Rank(level), line));
                while (_q.Count > Capacity) _q.Dequeue();
            }
        }

        /// <summary>Newest `lines` (1..500) lines, oldest first, optionally filtered by substring and minimal level.</summary>
        public List<string> Tail(int lines, string contains, string minLevel)
        {
            lines = Math.Max(1, Math.Min(500, lines));
            int floor = string.IsNullOrEmpty(minLevel) ? 0 : Rank(minLevel);
            var all = new List<string>();
            lock (_lock)
            {
                foreach (var kv in _q)
                    if (kv.Key >= floor && (string.IsNullOrEmpty(contains) || kv.Value.IndexOf(contains, StringComparison.Ordinal) >= 0)) all.Add(kv.Value);
            }
            return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
        }

        public static string SelfTest()
        {
            var r = new DevLogRing();
            for (int i = 0; i < 2100; i++) r.Add(i % 10 == 0 ? "Warning" : "Info", "S", "line " + i, DateTime.UtcNow);
            if (r.Count != Capacity) return "ring capacity";
            var all = r.Tail(500, null, null);
            if (all.Count != 500 || !all[499].EndsWith("line 2099")) return "tail order";
            if (r.Tail(9999, null, null).Count != 500) return "lines cap 500";
            if (r.Tail(1, null, null).Count != 1) return "lines min";
            var w = r.Tail(500, null, "warning");
            if (w.Count != 200 || w.Exists(x => !x.StartsWith("[Warning"))) return "min level";
            if (r.Tail(5, "line 1999", null).Count != 1) return "contains";
            if (r.Tail(500, "] line 0", null).Count != 0) return "oldest dropped";
            r.Add("Info", "S", new string('x', 1000) + "\nmore", DateTime.UtcNow);
            var last = r.Tail(1, null, null)[0];
            if (last.Length > MaxText + 20 || last.Contains("\n")) return "truncate";
            return null;
        }
    }
}
