using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace VoiceBotBridge
{
    /// <summary>T-202: phases of the radar coverage worker thread (written into the worker breadcrumb).</summary>
    internal static class WorkerPhase
    {
        public const int Idle = 0, Wait = 1, Build = 2, Profile = 3, Terrain = 4, Raster = 5, Outline = 6, Jam = 7, Pace = 8, Done = 9, TerrainWait = 10;   // T-225: the worker waits for the main thread to answer a batch of terrain points
    }

    /// <summary>Lock-free, no allocation per call; called only from the coverage worker thread.</summary>
    internal interface ICoverageProbe
    {
        void Phase(int phase, int radar, int tier, int ray);
        void BuildStart(long buildSeq, int gc0);
        void BuildEnd(long buildSeq, int gc0, double busyMs);
        void TerrainCalls(long total);
    }

    /// <summary>
    /// T-202 hang triage: the coverage worker breadcrumb in a memory-mapped file (BepInEx/voicebot_ui_worker.bin), readable from Linux
    /// (tools/qa/hangwatch.py) when the Mono runtime is frozen: which phase / radar / tier / ray the worker was in and its Windows thread id
    /// (matches the thread id in the Proton log). Layout v1, little-endian, 128 bytes:
    ///  0 "SPTW" 4 i32 version 8 i64 seq 16 i64 unix_ms 24 i32 win_tid 28 i32 phase 32 i32 radar 36 i32 tier 40 i32 ray
    ///  44 i32 gc0_build_start 48 i32 gc0_now 52 i32 reserved 56 i64 build_seq 64 i64 build_start_unix_ms 72 i64 terrain_calls 80 i64 seq again 88..127 reserved
    /// </summary>
    internal sealed class WorkerCrumb : ICoverageProbe, IDisposable
    {
        public const int Size = 128, Version = 1;
        public const string FileName = "voicebot_ui_worker.bin";

        private readonly byte[] _buf = new byte[Size];
        private readonly Func<int> _gc;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private long _seq, _buildSeq, _buildStartMs, _terrainCalls;
        private int _tid, _phase, _radar, _tier, _ray, _gcStart;

        /// <summary>Test seam: a writer without a file (Buffer holds the record).</summary>
        internal WorkerCrumb(Func<int> gcCount = null)
        {
            _gc = gcCount;
            _buf[0] = (byte)'S'; _buf[1] = (byte)'P'; _buf[2] = (byte)'T'; _buf[3] = (byte)'W';
            Put32(4, Version);
        }

        internal byte[] Buffer { get { return _buf; } }

        /// <summary>Opens (creates) the fixed-size file and maps it. Throws on failure (the caller logs and goes on without the crumb).</summary>
        public static WorkerCrumb Open(string path, Func<int> gcCount)
        {
            var c = new WorkerCrumb(gcCount);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (fs.Length != Size) fs.SetLength(Size);
                c._mmf = MemoryMappedFile.CreateFromFile(fs, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                c._view = c._mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            }
            catch { fs.Dispose(); throw; }
            c.Commit(false);   // the opening thread is not the worker: win_tid stays 0 until the worker writes
            return c;
        }

        public void Phase(int phase, int radar, int tier, int ray)
        {
            _phase = phase; _radar = radar; _tier = tier; _ray = ray;
            Commit();
        }

        public void BuildStart(long buildSeq, int gc0)
        {
            _buildSeq = buildSeq; _gcStart = gc0; _buildStartMs = UnixMs();
            _phase = WorkerPhase.Build; _radar = _tier = _ray = 0;
            Commit();
        }

        public void BuildEnd(long buildSeq, int gc0, double busyMs)
        {
            _buildSeq = buildSeq;
            _phase = WorkerPhase.Done; _radar = _tier = _ray = 0;
            Commit();
        }

        public void TerrainCalls(long total) { _terrainCalls = total; }

        public void Dispose()
        {
            try { _phase = WorkerPhase.Done; Commit(false); } catch { }
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null; _mmf = null;
        }

        private void Commit(bool ownThread = true)
        {
#pragma warning disable 618
            if (ownThread && _tid == 0) { try { _tid = AppDomain.GetCurrentThreadId(); } catch { _tid = -1; } }
#pragma warning restore 618
            int gcNow = 0;
            if (_gc != null) { try { gcNow = _gc(); } catch { } }
            _seq++;
            Put64(8, _seq);
            Put64(16, UnixMs());
            Put32(24, _tid);
            Put32(28, _phase);
            Put32(32, _radar);
            Put32(36, _tier);
            Put32(40, _ray);
            Put32(44, _gcStart);
            Put32(48, gcNow);
            Put64(56, _buildSeq);
            Put64(64, _buildStartMs);
            Put64(72, _terrainCalls);
            Put64(80, _seq);
            var v = _view;
            if (v != null) v.WriteArray(0, _buf, 0, Size);
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
            int gc = 5;
            var c = new WorkerCrumb(() => gc);
            var b = c.Buffer;
            if (b[0] != 'S' || b[3] != 'W' || Get32(b, 4) != Version) return "workercrumb: header";
            c.BuildStart(12, 3);
            if (Get64(b, 56) != 12 || Get32(b, 44) != 3 || Get32(b, 48) != 5 || Get32(b, 28) != WorkerPhase.Build) return "workercrumb: build start";
            if (Get64(b, 64) <= 0 || Get64(b, 16) <= 0) return "workercrumb: times";
            gc = 9;
            c.Phase(WorkerPhase.Terrain, 3, 1, 211);
            if (Get32(b, 28) != WorkerPhase.Terrain || Get32(b, 32) != 3 || Get32(b, 36) != 1 || Get32(b, 40) != 211) return "workercrumb: offsets";
            if (Get32(b, 48) != 9 || Get32(b, 44) != 3) return "workercrumb: gc";
            if (Get64(b, 8) != 2 || Get64(b, 8) != Get64(b, 80)) return "workercrumb: seq pair";
            if (Get32(b, 24) == 0) return "workercrumb: win_tid";
            c.TerrainCalls(777);
            c.Phase(WorkerPhase.Raster, 0, 0, 0);
            if (Get64(b, 72) != 777) return "workercrumb: terrain calls";
            c.BuildEnd(12, 9, 5.0);
            if (Get32(b, 28) != WorkerPhase.Done || Get64(b, 56) != 12) return "workercrumb: build end";
            string path = Path.Combine(Path.GetTempPath(), "sptui_wcrumb_selftest_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var f = Open(path, () => 1);
                using (var rs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[Size]; rs.Read(head, 0, Size);
                    if (Get32(head, 24) != 0) return "workercrumb: win_tid taken from the opening thread";
                }
                f.Phase(WorkerPhase.Jam, 4, 2, 17);
                f.Dispose();
                var raw = File.ReadAllBytes(path);
                if (raw.Length != Size) return "workercrumb: file size " + raw.Length;
                if (raw[3] != 'W' || Get32(raw, 28) != WorkerPhase.Done || Get32(raw, 32) != 4 || Get64(raw, 8) != Get64(raw, 80)) return "workercrumb: file record";
            }
            finally { try { File.Delete(path); } catch { } }
            return null;
        }
    }
}
