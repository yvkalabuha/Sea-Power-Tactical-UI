using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace VoiceBotBridge
{
    /// <summary>
    /// Hang triage: the watchdog breadcrumb in a small memory-mapped file (BepInEx/voicebot_ui_stage.bin). The page is shared with the OS page
    /// cache, so the last frame/stage stays readable from Linux (tools/qa/hangwatch.py) even when the whole Mono runtime is frozen and no managed
    /// thread can write a log line. Main thread only; no allocation per write (stage strings are cached by the callers).
    /// Layout v1, little-endian, 256 bytes:
    ///   0 "SPTB"  4 i32 version  8 i64 seq  16 i64 frame  24 i64 unix_ms (this write)  32 i64 frame_start_unix_ms  40 i64 update_end_unix_ms
    ///   48 i64 qpc ticks (this write)  56 i64 frame_start qpc ticks  64 i32 kind (1 frame 2 stage 3 update_end 4 gui_end 5 closed)
    ///   68 i32 step (stage changes in this frame)  72 i64 seq again (== seq when the record is whole)  80 i32 stage length
    ///   84 i32 Windows pid  88 i64 qpc frequency  96.. stage ASCII (max 160 bytes)
    /// </summary>
    internal sealed class StageCrumb : IStageSink, IDisposable
    {
        public const int Size = 256, StageOff = 96, StageMax = Size - StageOff, Version = 1;
        public const string FileName = "voicebot_ui_stage.bin";

        private readonly byte[] _buf = new byte[Size];
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private long _seq, _frame, _frameStartMs, _frameStartTicks, _updateEndMs;
        private int _step, _stageLen, _busy;
        private string _last;

        /// <summary>Test seam: a writer without a file (Buffer holds the record).</summary>
        internal StageCrumb(int pid)
        {
            _buf[0] = (byte)'S'; _buf[1] = (byte)'P'; _buf[2] = (byte)'T'; _buf[3] = (byte)'B';
            Put32(4, Version);
            Put32(84, pid);
            Put64(88, Stopwatch.Frequency);
        }

        internal byte[] Buffer { get { return _buf; } }

        /// <summary>Opens (creates) the fixed-size file and maps it. Throws on failure (the caller logs and goes on without the crumb).</summary>
        public static StageCrumb Open(string path)
        {
            int pid = 0;
            try { pid = Process.GetCurrentProcess().Id; } catch { }
            var c = new StageCrumb(pid);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (fs.Length != Size) fs.SetLength(Size);
                c._mmf = MemoryMappedFile.CreateFromFile(fs, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                c._view = c._mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            }
            catch { fs.Dispose(); throw; }
            c._last = null;
            c.Commit(DrawGuard.MarkStage);
            return c;
        }

        public void Stage(string stage)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                if (!ReferenceEquals(stage, _last)) { _last = stage; Encode(stage); }
                _step++;
                Commit(DrawGuard.MarkStage);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        public void Frame(long frameNo)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                _frame = frameNo; _step = 0;
                _frameStartMs = UnixMs(); _frameStartTicks = Stopwatch.GetTimestamp();
                Commit(DrawGuard.MarkFrame);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        public void Mark(int kind)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                if (kind == DrawGuard.MarkUpdateEnd) _updateEndMs = UnixMs();
                Commit(kind);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        public void Dispose()
        {
            try { Mark(DrawGuard.MarkClosed); } catch { }
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null; _mmf = null;
        }

        private void Encode(string s)
        {
            int n = s == null ? 0 : Math.Min(s.Length, StageMax);
            for (int i = 0; i < n; i++) { char ch = s[i]; _buf[StageOff + i] = ch >= 32 && ch < 127 ? (byte)ch : (byte)'?'; }
            _stageLen = n;
            Put32(80, n);
        }

        private void Commit(int kind)
        {
            _seq++;
            Put64(8, _seq);
            Put64(16, _frame);
            Put64(24, UnixMs());
            Put64(32, _frameStartMs);
            Put64(40, _updateEndMs);
            Put64(48, Stopwatch.GetTimestamp());
            Put64(56, _frameStartTicks);
            Put32(64, kind);
            Put32(68, _step);
            Put64(72, _seq);
            var v = _view;
            if (v != null) v.WriteArray(0, _buf, 0, StageOff + _stageLen);
        }

        private static long UnixMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L; }

        private void Put32(int at, int v)
        {
            _buf[at] = (byte)v; _buf[at + 1] = (byte)(v >> 8); _buf[at + 2] = (byte)(v >> 16); _buf[at + 3] = (byte)(v >> 24);
        }

        private void Put64(int at, long v)
        {
            for (int i = 0; i < 8; i++) _buf[at + i] = (byte)(v >> (8 * i));
        }

        private static long Get64(byte[] b, int at) { long v = 0; for (int i = 7; i >= 0; i--) v = (v << 8) | b[at + i]; return v; }
        private static int Get32(byte[] b, int at) { return b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24); }

        public static string SelfTest()
        {
            var c = new StageCrumb(42);
            var b = c.Buffer;
            if (b[0] != 'S' || b[3] != 'B' || Get32(b, 4) != Version || Get32(b, 84) != 42) return "crumb: header";
            c.Frame(7);
            if (Get64(b, 16) != 7 || Get32(b, 64) != DrawGuard.MarkFrame || Get32(b, 68) != 0) return "crumb: frame";
            if (Get64(b, 8) != Get64(b, 72)) return "crumb: seq pair";
            string st = "radar/update";
            c.Stage(st);
            c.Stage(st);
            if (Get32(b, 80) != st.Length || Get32(b, 68) != 2 || Get32(b, 64) != DrawGuard.MarkStage) return "crumb: stage";
            if (System.Text.Encoding.ASCII.GetString(b, StageOff, Get32(b, 80)) != st) return "crumb: stage text";
            c.Stage("abé");
            if (System.Text.Encoding.ASCII.GetString(b, StageOff, Get32(b, 80)) != "ab?") return "crumb: non-ascii";
            c.Stage(new string('x', 400));
            if (Get32(b, 80) != StageMax) return "crumb: cap";
            c.Mark(DrawGuard.MarkUpdateEnd);
            if (Get32(b, 64) != DrawGuard.MarkUpdateEnd || Get64(b, 40) <= 0) return "crumb: update end";
            c.Frame(8);
            if (Get32(b, 68) != 0 || Get64(b, 16) != 8) return "crumb: step reset";
            long seq = Get64(b, 8);
            DrawGuard.Sink = c;
            DrawGuard.Stage = "sink/test";
            DrawGuard.Mark(DrawGuard.MarkGuiEnd);
            DrawGuard.Sink = null;
            DrawGuard.Stage = "";
            if (Get64(b, 8) != seq + 2 || Get32(b, 64) != DrawGuard.MarkGuiEnd) return "crumb: DrawGuard sink";
            if (System.Text.Encoding.ASCII.GetString(b, StageOff, Get32(b, 80)) != "sink/test") return "crumb: sink stage";
            string path = Path.Combine(Path.GetTempPath(), "sptui_crumb_selftest_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var f = Open(path);
                f.Frame(99);
                f.Stage("file/stage");
                f.Dispose();
                var raw = File.ReadAllBytes(path);
                if (raw.Length != Size) return "crumb: file size " + raw.Length;
                if (Get64(raw, 16) != 99 || Get32(raw, 64) != DrawGuard.MarkClosed) return "crumb: file record";
                if (System.Text.Encoding.ASCII.GetString(raw, StageOff, Get32(raw, 80)) != "file/stage") return "crumb: file stage";
            }
            finally { try { File.Delete(path); } catch { } }
            return null;
        }
    }
}
