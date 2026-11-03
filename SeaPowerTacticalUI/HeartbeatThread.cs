// T-302: one dedicated thread "sptui-hb" for the heartbeat/memory file writes and the main-thread watchdog; at a stall it also
// dumps the thread table of the game process (ProcTable) to voicebot_ui_stall.txt. Pure (no Unity); the thread is our own, the pool is not used.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VoiceBotBridge.Ui
{
    internal sealed class HbLoopOptions
    {
        public Func<long> Frame;              // Interlocked.Read(ref _frameNo)
        public Func<string> Stage;            // () => DrawGuard.Stage
        public Action[] Drains;               // Heartbeat.DrainNow of the heartbeat and memory files
        public string HbPath;                 // voicebot_ui_heartbeat.log (the stuck line)
        public string StallPath;              // voicebot_ui_stall.txt
        public string ProcRoot;               // null = ProcThreads.DefaultRoot() at construction
        public double LimitS = 8.0;
        public double[] SnapAtS = { 8.0, 30.0, 120.0 };
        public int FirstFlushIndex = 0;       // 0 = write the first snapshot at once; 1 = keep it in memory until the next one
        public int PeriodMs = 250;
        public long StallMaxBytes = 512 * 1024;
        public Func<double> Clock;            // test seam; null = Stopwatch seconds
        public Action<string> HbWrite;        // T-305: the heartbeat writer (Heartbeat.WriteNow); null = own FileStream on HbPath (stands, fallback)
        public Func<bool> Armed;              // T-305 Q1: true after the first mission frame; null = always armed
        public int StartupFlushIndex = 2;     // T-305 Q1: while !Armed, snapshots with index < this are held in memory (2 = only a 120 s stall reaches the file)
        public Func<DateTime> Now;            // test seam; null = DateTime.Now
    }

    /// <summary>Pure: which snapshot of the current stall is due (-1 = none). Starts over when the frame moves.</summary>
    internal sealed class StallSeries
    {
        private readonly double[] _at;
        private long _frame = long.MinValue;
        private double _since;
        private int _next;

        public StallSeries(double[] atS) { _at = atS; }
        public int StallId { get; private set; }

        /// <summary>The highest index whose time has passed and was not reported yet (earlier ones are skipped); each index once per stall.</summary>
        public int Due(long frame, double nowS)
        {
            if (frame != _frame) { _frame = frame; _since = nowS; _next = 0; StallId++; return -1; }
            int due = -1;
            while (_next < _at.Length && nowS - _since >= _at[_next]) { due = _next; _next++; }
            return due;
        }

        public double StuckFor(double nowS) { return nowS - _since; }
    }

    /// <summary>Lazy append-only stall file (opened at the first write), fsync after every write, rotation to ".1".</summary>
    internal sealed class StallFile : IDisposable
    {
        private readonly string _path;
        private readonly long _max;
        private FileStream _fs;

        public StallFile(string path, long maxBytes) { _path = path; _max = maxBytes; }
        public bool Opened { get { return _fs != null; } }

        public FileStream Stream()
        {
            if (_fs != null && _fs.Length >= _max)
            {
                try { _fs.Dispose(); } catch { }
                _fs = null;
                string old = _path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
            if (_fs == null) _fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return _fs;
        }

        public void Write(byte[] b, int off, int n)
        {
            var fs = Stream();
            fs.Write(b, off, n);
            fs.Flush(true);
        }

        public void Dispose() { var fs = _fs; _fs = null; if (fs != null) { try { fs.Dispose(); } catch { } } }
    }

    internal sealed class HbLoop : IDisposable
    {
        public const string ThreadName = "sptui-hb";
        private readonly HbLoopOptions _o;
        private readonly StallDetector _stall;
        private readonly StallSeries _series;
        private readonly ProcTable _table;
        private readonly StallFile _file;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly long _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        private Thread _thread;
        private double _lastWatch = double.NegativeInfinity;
        private byte[] _held; private int _heldLen; private int _heldStall = -1;
        private int _snapshots;
        private bool _procUnavailable;

        public HbLoop(HbLoopOptions o)
        {
            _o = o;
            _stall = new StallDetector(o.LimitS);
            _series = new StallSeries(o.SnapAtS);
            string root = o.ProcRoot ?? ProcThreads.DefaultRoot();
            _table = new ProcTable(root);
            _file = new StallFile(o.StallPath, o.StallMaxBytes);
        }

        public int Snapshots { get { return _snapshots; } }
        public bool ProcUnavailable { get { return _procUnavailable; } }

        private double NowS()
        {
            if (_o.Clock != null) return _o.Clock();
            return (System.Diagnostics.Stopwatch.GetTimestamp() - _t0) / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>false = the thread could not start (the caller falls back to the old path).</summary>
        public bool Start()
        {
            try
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = ThreadName };
                _thread.Start();
                return true;
            }
            catch { _thread = null; return false; }
        }

        private void Loop()
        {
            ModThreads.Enter(ThreadName);
            try
            {
                while (!_stop.WaitOne(_o.PeriodMs)) Step(NowS());
            }
            finally { ModThreads.Exit(); }
        }

        /// <summary>One iteration: drains, the watchdog (once a second), snapshots. Never throws; allocation-free without a stall.</summary>
        public void Step(double nowS)
        {
            try
            {
                var d = _o.Drains;
                if (d != null) for (int i = 0; i < d.Length; i++) { try { d[i](); } catch { } }
                if (nowS - _lastWatch < 1.0) return;
                _lastWatch = nowS;
                long frame = _o.Frame();
                bool stuck = _stall.Tick(frame, nowS);
                int due = _series.Due(frame, nowS);
                if (stuck) WriteStuck(nowS, frame);
                if (due >= 0) Snapshot(due, nowS, frame);
            }
            catch { }
        }

        private void WriteStuck(double nowS, long frame)
        {
            try
            {
                DateTime t = _o.Now != null ? _o.Now() : DateTime.Now;
                string line = StuckLine(t, _stall.StuckFor(nowS), frame, _o.Stage == null ? null : _o.Stage());
                if (_o.HbWrite != null) { _o.HbWrite(line); return; }
                var bytes = new UTF8Encoding(false).GetBytes(line);
                using (var fs = new FileStream(_o.HbPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
            }
            catch { }
        }

        internal static string StuckLine(DateTime t, double stuckS, long frame, string stage)
        {
            return t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " main thread stuck for " +
                   stuckS.ToString("F0", CultureInfo.InvariantCulture) + " s frame=" + frame + " stage=" + (stage ?? "") + "\n";
        }

        private void Snapshot(int index, double nowS, long frame)
        {
            if (_procUnavailable) return;
            try
            {
                DateTime t = _o.Now != null ? _o.Now() : DateTime.Now;
                string stamp = t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                int flush = (_o.Armed == null || _o.Armed()) ? _o.FirstFlushIndex : Math.Max(_o.FirstFlushIndex, _o.StartupFlushIndex);
                bool hold = index < flush;
                string reason;
                _table.HeaderSink = hold ? null : EarlySink();
                bool ok = _table.Snapshot(stamp, index + 1, _o.SnapAtS.Length, _series.StuckFor(nowS), frame, _o.Stage == null ? null : _o.Stage(), out reason);
                _table.HeaderSink = null;
                if (!ok)
                {
                    if (reason == "no_root" || reason == "no_game_dir") _procUnavailable = true;
                    string line = "=== " + stamp + " stall threads unavailable root=" + (_table.Root ?? "none") + " reason=" + reason + "\n";
                    var b = new UTF8Encoding(false).GetBytes(line);
                    _file.Write(b, 0, b.Length);
                    return;
                }
                if (hold)
                {
                    if (_held == null || _held.Length < _table.Length) _held = new byte[Math.Max(_table.Length, 1024)];
                    Buffer.BlockCopy(_table.Buffer, 0, _held, 0, _table.Length);
                    _heldLen = _table.Length; _heldStall = _series.StallId;
                    return;
                }
                if (_held != null && _heldLen > 0 && _heldStall == _series.StallId) _file.Write(_held, 0, _heldLen);
                _heldLen = 0;
                int from = _table.HeaderWritten;
                _file.Write(_table.Buffer, from, _table.Length - from);
                _snapshots++;
            }
            catch { }
        }

        private FileStream EarlySink()
        {
            try { return _file.Stream(); } catch { return null; }
        }

        public void Dispose()
        {
            try { _stop.Set(); } catch { }
            var th = _thread;
            if (th != null) { try { th.Join(500); } catch { } }
            var d = _o.Drains;
            if (d != null) for (int i = 0; i < d.Length; i++) { try { d[i](); } catch { } }
            _file.Dispose();
        }
    }
}
