// T-227: re-reads a small text file only when its (mtime, length) changed. Pure (BCL only): the stat is one syscall, the read is open+read+close.
using System;
using System.IO;

namespace VoiceBotBridge.Ui
{
    internal sealed class StatGatedText
    {
        private readonly Func<string, long[]> _stat;
        private readonly Func<string, string[]> _readLines;
        private readonly object _lock = new object();
        private string _path; private long _mtime = long.MinValue, _len = long.MinValue; private string[] _lines;
        private long _reads;

        /// <summary>Test seams: stat returns {mtime ticks or -1 when the file is missing, length}.</summary>
        public StatGatedText(Func<string, long[]> stat = null, Func<string, string[]> readLines = null)
        {
            _stat = stat ?? DefaultStat;
            _readLines = readLines ?? File.ReadAllLines;
        }

        private static long[] DefaultStat(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new[] { fi.LastWriteTimeUtc.Ticks, fi.Length } : new long[] { -1, 0 };
        }

        /// <summary>Real reads (tests, io.last_save_reads).</summary>
        public long Reads { get { return System.Threading.Interlocked.Read(ref _reads); } }

        /// <summary>null = no file. Read errors give null and are retried on the next call.</summary>
        public string[] Lines(string path)
        {
            lock (_lock)
            {
                long[] st = _stat(path);
                if (st == null || st.Length < 2 || st[0] < 0) { _path = null; _lines = null; _mtime = _len = long.MinValue; return null; }
                if (_path == path && _mtime == st[0] && _len == st[1]) return _lines;
                System.Threading.Interlocked.Increment(ref _reads);
                string[] l;
                try { l = _readLines(path); }
                catch { _path = null; _lines = null; _mtime = _len = long.MinValue; return null; }
                _path = path; _mtime = st[0]; _len = st[1]; _lines = l;
                return l;
            }
        }

        public static string SelfTest()
        {
            long[] st = { 5, 10 }; bool missing = false; string[] content = { "a", "b" };
            var g = new StatGatedText(p => missing ? new long[] { -1, 0 } : new[] { st[0], st[1] }, p => content);
            for (int i = 0; i < 100; i++) g.Lines("/x");
            if (g.Reads != 1) return "100 calls without a stat change: reads " + g.Reads;
            st[0] = 6; g.Lines("/x"); if (g.Reads != 2) return "mtime change: reads " + g.Reads;
            st[1] = 11; content = new[] { "c" }; var l = g.Lines("/x");
            if (g.Reads != 3 || l.Length != 1 || l[0] != "c") return "length change: reads " + g.Reads;
            g.Lines("/y"); if (g.Reads != 4) return "other path: reads " + g.Reads;
            missing = true;
            if (g.Lines("/y") != null || g.Reads != 4) return "missing file must give null without a read";
            missing = false;
            if (g.Lines("/y") == null || g.Reads != 5) return "reappeared file must be read: " + g.Reads;
            var bad = new StatGatedText(p => new long[] { 1, 1 }, p => { throw new IOException("x"); });
            if (bad.Lines("/z") != null || bad.Lines("/z") != null || bad.Reads != 2) return "read error: null and retry";
            // a real file: write, then change the mtime
            string tmp = Path.Combine(Path.GetTempPath(), "statgated_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(tmp, "one\ntwo\n");
                var r = new StatGatedText();
                var a = r.Lines(tmp); r.Lines(tmp);
                if (a == null || a.Length != 2 || a[1] != "two" || r.Reads != 1) return "real file first read";
                File.WriteAllText(tmp, "one\nTWO\n");                           // same length
                File.SetLastWriteTimeUtc(tmp, new DateTime(2020, 1, 1, 0, 0, 1, DateTimeKind.Utc));
                a = r.Lines(tmp);
                if (a[1] != "TWO" || r.Reads != 2) return "real file mtime change: " + a[1] + " " + r.Reads;
                File.Delete(tmp);
                if (r.Lines(tmp) != null || r.Reads != 2) return "real file deleted";
            }
            finally { try { File.Delete(tmp); } catch { } }
            return null;
        }
    }
}
