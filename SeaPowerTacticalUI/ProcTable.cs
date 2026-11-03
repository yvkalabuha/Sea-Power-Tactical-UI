// T-302: snapshot of the thread table of the GAME process (tid comm state wchan CPU ticks ctx switches syscall) into a preallocated
// byte buffer. Pure (no Unity). Written for a stalled process: no LINQ, no Split/Substring, numbers and text are copied by hand.
using System;
using System.Globalization;
using System.IO;

namespace VoiceBotBridge.Ui
{
    internal sealed class ProcTable
    {
        public const int MaxThreads = 512;
        private readonly string _root;
        private readonly byte[] _out;
        private readonly byte[] _rd;
        private int _len;

        public ProcTable(string root, int outBytes = 96 * 1024, int readBytes = 4096)
        {
            _root = root; _out = new byte[Math.Max(256, outBytes)]; _rd = new byte[Math.Max(64, readBytes)];
        }

        public string Root { get { return _root; } }
        public byte[] Buffer { get { return _out; } }
        public int Length { get { return _len; } }
        public int LastReads { get; private set; }
        public bool LastTruncated { get; private set; }
        /// <summary>When set, the header line is written to it and fsynced before /proc is read (a stall that freezes us mid-snapshot stays visible).</summary>
        public FileStream HeaderSink;
        /// <summary>Bytes of Buffer already sent to HeaderSink by the last Snapshot.</summary>
        public int HeaderWritten { get; private set; }

        internal static readonly byte[] KeyVcs = System.Text.Encoding.ASCII.GetBytes("voluntary_ctxt_switches:");
        internal static readonly byte[] KeyNvcs = System.Text.Encoding.ASCII.GetBytes("nonvoluntary_ctxt_switches:");

        public bool Snapshot(string stamp, int snap, int of, double stuckS, long frame, string stage, out string reason)
        {
            reason = null; _len = 0; _written = 0; LastReads = 0; LastTruncated = false; HeaderWritten = 0;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root)) { reason = "no_root"; return false; }
                string game = ProcThreads.FindGameDir(_root);
                if (game == null) { reason = "no_game_dir"; return false; }
                string[] tasks = Directory.GetDirectories(Path.Combine(game, "task"));
                if (tasks.Length == 0) { reason = "no_tasks"; return false; }
                int cnt = Math.Min(tasks.Length, MaxThreads);
                Ascii("=== "); Ascii(stamp); Ascii(" stall snap="); Long(snap); Ascii("/"); Long(of);
                Ascii(" stuck_s="); Long((long)stuckS); Ascii(" frame="); Long(frame); Ascii(" stage="); Ascii(stage);
                Ascii(" pid="); Ascii(Path.GetFileName(game.TrimEnd('/', '\\'))); Ascii(" root="); Ascii(_root); Ascii(" threads="); Long(tasks.Length); Ascii("\n");
                if (HeaderSink != null)
                {
                    HeaderSink.Write(_out, 0, _len); HeaderSink.Flush(true);
                    HeaderWritten = _len;
                }
                Ascii("tid comm state wchan utime stime vcs nvcs syscall\n");
                for (int i = 0; i < cnt; i++)
                {
                    if (_len + 160 > _out.Length - 160) { LastTruncated = true; break; }
                    OneTask(tasks[i]);
                }
                if (cnt < tasks.Length) LastTruncated = true;
                if (LastTruncated) { Ascii("... truncated at "); Long(_written); Ascii(" threads\n"); }
                long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000L / System.Diagnostics.Stopwatch.Frequency;
                Ascii("=== end snap="); Long(snap); Ascii(" ms="); Long(ms); Ascii(" reads="); Long(LastReads); Ascii(" bytes="); Long(_len); Ascii("\n");
                return true;
            }
            catch (Exception e) { reason = "error:" + e.GetType().Name; return false; }
        }

        private int _written;

        private void OneTask(string dir)
        {
            string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
            int n = Read(Path.Combine(dir, "stat"));
            int cs, cl; byte st; long ut, stm;
            if (n <= 0 || !ParseStat(_rd, n, out cs, out cl, out st, out ut, out stm)) return;
            Ascii(name); Put((byte)' ');
            for (int k = 0; k < cl; k++) { byte c = _rd[cs + k]; Put(c == (byte)' ' || c < 33 ? (byte)'_' : c); }
            Put((byte)' '); Put(st); Put((byte)' ');
            // wchan: the stat buffer is no longer needed, reuse _rd
            n = Read(Path.Combine(dir, "wchan"));
            int tl = 0; int ts = n > 0 ? FirstToken(_rd, n, out tl) : -1;
            if (ts >= 0 && tl > 0) { for (int k = 0; k < tl && k < 40; k++) Put(_rd[ts + k]); } else Put((byte)'-');
            Put((byte)' '); Long(ut); Put((byte)' '); Long(stm); Put((byte)' ');
            n = Read(Path.Combine(dir, "status"));
            long v = n > 0 ? FindField(_rd, n, KeyVcs) : -1;
            long nv = n > 0 ? FindField(_rd, n, KeyNvcs) : -1;
            if (v >= 0) Long(v); else Put((byte)'-');
            Put((byte)' ');
            if (nv >= 0) Long(nv); else Put((byte)'-');
            Put((byte)' ');
            n = Read(Path.Combine(dir, "syscall"));
            ts = n > 0 ? FirstToken(_rd, n, out tl) : -1;
            if (ts >= 0 && tl > 0) { for (int k = 0; k < tl && k < 20; k++) Put(_rd[ts + k]); } else Put((byte)'-');
            Put((byte)'\n');
            _written++;
        }

        private int Read(string path) { LastReads++; return ReadSmall(path, _rd); }

        // ---- output helpers (clamped: an overflow drops bytes, never throws)
        private void Put(byte b) { if (_len < _out.Length) _out[_len++] = b; }

        private void Ascii(string s)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length && _len < _out.Length; i++) { char c = s[i]; _out[_len++] = c < 128 ? (byte)c : (byte)'?'; }
        }

        private void Long(long v)
        {
            if (v < 0) { Put((byte)'-'); v = -v; if (v < 0) return; }
            long d = 1;
            while (v / d >= 10) d *= 10;
            while (d > 0) { Put((byte)('0' + (int)(v / d % 10))); d /= 10; }
        }

        // ---- pure helpers

        /// <summary>Reads a small /proc file until Read == 0 (Wine reports Length 0). -1 on any error. Counts the open in ProcThreads.FileReads.</summary>
        internal static int ReadSmall(string path, byte[] buf)
        {
            System.Threading.Interlocked.Increment(ref ProcThreads.FileReads);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    int n = 0;
                    while (n < buf.Length)
                    {
                        int r = fs.Read(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                    }
                    return n;
                }
            }
            catch { return -1; }
        }

        /// <summary>"pid (comm) S ppid ... utime stime": fields after the LAST ')'.</summary>
        internal static bool ParseStat(byte[] b, int n, out int commStart, out int commLen, out byte state, out long utime, out long stime)
        {
            commStart = 0; commLen = 0; state = (byte)'?'; utime = 0; stime = 0;
            if (b == null || n > b.Length) return false;
            int open = -1, close = -1;
            for (int i = 0; i < n; i++) { if (b[i] == (byte)'(' && open < 0) open = i; if (b[i] == (byte)')') close = i; }
            if (open < 0 || close <= open || close + 2 >= n) return false;
            int p = close + 2;
            state = b[p]; p++;
            // fields: state=f0 ... utime = f11, stime = f12 (counting from the state)
            int field = 0; long u = 0, s = 0; bool haveU = false, haveS = false;
            while (p < n && field <= 12)
            {
                while (p < n && b[p] == (byte)' ') p++;
                if (p >= n || b[p] == (byte)'\n') break;
                field++;                       // 1 = the first field after state
                long v = 0; bool digits = false, bad = false;
                while (p < n && b[p] != (byte)' ' && b[p] != (byte)'\n')
                {
                    if (b[p] >= (byte)'0' && b[p] <= (byte)'9') { v = v * 10 + (b[p] - (byte)'0'); digits = true; } else bad = true;
                    p++;
                }
                if (field == 11) { if (bad || !digits) return false; u = v; haveU = true; }
                else if (field == 12) { if (bad || !digits) return false; s = v; haveS = true; }
            }
            if (!haveU || !haveS) return false;
            commStart = open + 1; commLen = close - open - 1; utime = u; stime = s;
            return true;
        }

        /// <summary>Value of a "key: value" line of /proc/&lt;t&gt;/status; -1 when absent.</summary>
        internal static long FindField(byte[] b, int n, byte[] key)
        {
            if (b == null || key == null) return -1;
            for (int i = 0; i + key.Length <= n; i++)
            {
                if (i > 0 && b[i - 1] != (byte)'\n') continue;
                int k = 0;
                while (k < key.Length && b[i + k] == key[k]) k++;
                if (k != key.Length) continue;
                int p = i + key.Length;
                while (p < n && (b[p] == (byte)' ' || b[p] == (byte)'\t')) p++;
                long v = 0; bool any = false;
                while (p < n && b[p] >= (byte)'0' && b[p] <= (byte)'9') { v = v * 10 + (b[p] - (byte)'0'); p++; any = true; }
                return any ? v : -1;
            }
            return -1;
        }

        /// <summary>Start of the first whitespace-separated token (len = its length); -1 when none.</summary>
        internal static int FirstToken(byte[] b, int n, out int len)
        {
            len = 0;
            int p = 0;
            while (p < n && b[p] <= 32) p++;
            if (p >= n) return -1;
            int s = p;
            while (p < n && b[p] > 32) p++;
            len = p - s;
            return s;
        }
    }
}
