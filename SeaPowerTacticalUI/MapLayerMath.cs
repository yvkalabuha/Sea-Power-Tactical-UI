using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-107 / stream A: pure math and policies of the Noesis map layer (no Unity, no game). The layer draws in LOCAL MAP UNITS of the
    /// MapBase (the same space as LocationToView and Image.Width/Height), the render texture is only a sharper copy of that space.
    /// </summary>
    internal static class MapLayerMath
    {
        /// <summary>Local map units -> RT pixels: x*sx, y*sy (sx = rtW/mapW). y runs down, unless flipY (then y' = rtH - y*sy).</summary>
        public static Affine2 LocalToRt(Affine2 geoToLocal, double mapW, double mapH, int rtW, int rtH, bool flipY)
        {
            double sx = rtW / Math.Max(1e-6, mapW), sy = rtH / Math.Max(1e-6, mapH);
            var r = new Affine2
            {
                A = geoToLocal.A * sx, B = geoToLocal.B * sx, C = geoToLocal.C * sx,
                D = geoToLocal.D * sy, E = geoToLocal.E * sy, F = geoToLocal.F * sy
            };
            if (flipY) { r.D = -r.D; r.E = -r.E; r.F = rtH - r.F; }
            return r;
        }

        /// <summary>T-309: local map units -> RT pixels as the 2x3 {sx, 0, 0, 0, sy, 0} (the flip is done by the GL matrix as a whole).</summary>
        public static double[] LocalToOut(double mapW, double mapH, int rtW, int rtH)
        {
            return new[] { rtW / Math.Max(1e-6, mapW), 0, 0, 0, rtH / Math.Max(1e-6, mapH), 0 };
        }

        /// <summary>RT size for a map: ceil(map * rtScale * pxScale), clamped 16..4096. False when the current RT already fits (growth only, no shrink, no recreate).</summary>
        public static bool NeedNewRt(int curW, int curH, double mapW, double mapH, double rtScale, double pxScale, out int w, out int h)
        {
            double k = Math.Max(0.1, Math.Min(2.0, rtScale)) * Math.Max(0.1, pxScale);
            w = (int)Math.Max(16, Math.Min(4096, Math.Ceiling(mapW * k)));
            h = (int)Math.Max(16, Math.Min(4096, Math.Ceiling(mapH * k)));
            if (curW >= w && curH >= h && curW > 0 && curH > 0) { w = curW; h = curH; return false; }
            if (curW > w) w = curW;   // never smaller than what exists in either axis
            if (curH > h) h = curH;
            return true;
        }

        /// <summary>"main" | "minimap" | "pip" | "other". Only "main" gets the layer: the largest MapBase of MainGameView that the user really sees and that is not inside a SeapowerUI.Window.</summary>
        public static string MapRole(bool insideWindow, bool isLargest, bool hitSeen)
        {
            if (!hitSeen) return "other";
            if (insideWindow) return isLargest ? "pip" : "minimap";
            return isLargest ? "main" : "minimap";
        }

        public const int OurFirst = 200, OurLast = 229;

        /// <summary>T-154 D1: where to insert our layer so it stays right after the game's own layers (numbers below 200), before other mods (500+).
        /// Our layers (200..229) keep ascending number order, so the Radar layer (200) is first among ours. Existing order is never changed.</summary>
        public static int InsertIndex(IList<int> numbers, int newNumber)
        {
            if (numbers == null) return 0;
            int idx = 0;
            for (int i = 0; i < numbers.Count; i++)
            {
                int n = numbers[i];
                if (n < OurFirst || (n >= OurFirst && n <= OurLast && n < newNumber)) idx = i + 1;
            }
            return idx;
        }

        public static int InsertIndex(IList<int> numbers) { return InsertIndex(numbers, OurFirst); }

        /// <summary>T-190 A1: frames without a suitable screen camera before the layer draws at the end of the frame.</summary>
        public const int LayerCamGraceFrames = 30;

        /// <summary>T-190 A1: true = draw in onPreRender of this camera. Only a screen camera (targetTexture == null) that is enabled; it is Camera.main,
        /// or Camera.main is unusable (null / disabled / renders to a texture, as on the full-screen map). PiP and mini-map cameras never.</summary>
        public static bool AcceptLayerCamera(bool screenTarget, bool enabled, bool isMain, bool mainUsable)
        {
            if (!screenTarget || !enabled) return false;
            return isMain || !mainUsable;
        }

        /// <summary>T-190 A1: true = no camera drew the layer in this frame and no suitable camera was seen for grace frames
        /// (lastCamFrame &lt; 0: never seen, counted from the frame the layer was first wanted).</summary>
        public static bool EndOfFrameDue(int frame, int lastDrawFrame, int lastCamFrame, int wantedSinceFrame, int grace = LayerCamGraceFrames)
        {
            if (lastDrawFrame == frame) return false;
            if (wantedSinceFrame < 0) return false;
            return lastCamFrame < 0 ? frame - wantedSinceFrame >= grace : frame - lastCamFrame >= grace;
        }

        public static bool FlipDefault(string cfg)
        {
            string v = (cfg ?? "").Trim().ToLowerInvariant();
            return v == "true";   // "auto" | "" | "false": Noesis TextureSource reads the RT top-down already
        }

        /// <summary>Reads a file that a Heartbeat still holds open (Mono emulates share modes in-process: File.ReadAllText would fail).</summary>
        private static string ReadShared(string p)
        {
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
                return sr.ReadToEnd();
        }

        public static string SelfTest()
        {
            var errs = new List<string>();
            Action<bool, string> chk = (ok, m) => { if (!ok) errs.Add(m); };
            // geo (lat,lon) -> local: x = 100*lon, y = -200*lat + 500; map 1920x1028, RT 2560x1371 (x1.3333)
            var g = new Affine2 { A = 0, B = 100, C = 0, D = -200, E = 0, F = 500 };
            var r = LocalToRt(g, 1920, 1028, 2560, 1371, false);
            double x, y;
            r.Apply(0, 0, out x, out y);
            chk(Math.Abs(x - 0) < 1e-9 && Math.Abs(y - 500.0 * 1371 / 1028) < 1e-9, "LocalToRt origin");
            // the corners of the map map to the corners of the RT
            var id = new Affine2 { A = 1, B = 0, C = 0, D = 0, E = 1, F = 0 };   // lat->x, lon->y
            var c = LocalToRt(id, 1920, 1028, 2560, 1371, false);
            c.Apply(1920, 1028, out x, out y);
            chk(Math.Abs(x - 2560) < 1e-9 && Math.Abs(y - 1371) < 1e-9, "LocalToRt far corner");
            c.Apply(0, 0, out x, out y);
            chk(Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9, "LocalToRt near corner (y down)");
            // a pan by d local units shifts the RT by d*rtW/mapW, not by d*1.3333 more
            var p0 = LocalToRt(new Affine2 { A = 0, B = 1, C = 0, D = 1, E = 0, F = 0 }, 1000, 500, 1500, 750, false);
            var p1 = LocalToRt(new Affine2 { A = 0, B = 1, C = 40, D = 1, E = 0, F = -20 }, 1000, 500, 1500, 750, false);
            double x0, y0, x1, y1;
            p0.Apply(10, 10, out x0, out y0); p1.Apply(10, 10, out x1, out y1);
            chk(Math.Abs((x1 - x0) - 40 * 1.5) < 1e-9 && Math.Abs((y1 - y0) + 20 * 1.5) < 1e-9, "LocalToRt pan");
            var lo = LocalToOut(1920, 1028, 2560, 1371);
            var lr = LocalToRt(g, 1920, 1028, 2560, 1371, false);
            double lrx, lry; lr.Apply(3, 4, out lrx, out lry);
            double gx = g.A * 3 + g.B * 4 + g.C, gy = g.D * 3 + g.E * 4 + g.F;
            chk(Math.Abs(lo[0] * gx + lo[1] * gy + lo[2] - lrx) < 1e-9 && Math.Abs(lo[3] * gx + lo[4] * gy + lo[5] - lry) < 1e-9, "LocalToOut = LocalToRt");
            var f = LocalToRt(id, 1920, 1028, 2560, 1371, true);
            f.Apply(0, 0, out x, out y);
            chk(Math.Abs(y - 1371) < 1e-9, "LocalToRt flipY");
            int w, h;
            chk(NeedNewRt(0, 0, 1920, 1028, 1.0, 1.3333, out w, out h) && w == 2560 && h == 1371, "NeedNewRt first " + w + "x" + h);
            chk(NeedNewRt(2560, 1371, 1800, 1000, 1.0, 1.3333, out w, out h) == false && w == 2560 && h == 1371, "NeedNewRt shrink keeps");
            chk(NeedNewRt(1280, 686, 1920, 1028, 1.0, 1.3333, out w, out h) && w == 2560, "NeedNewRt grow");
            chk(NeedNewRt(0, 0, 100000, 10, 1.0, 1.0, out w, out h) && w == 4096 && h == 16, "NeedNewRt clamp");
            chk(MapRole(true, false, true) == "minimap", "role minimap in window");
            chk(MapRole(false, true, true) == "main", "role main");
            chk(MapRole(false, false, true) == "minimap", "role small not in window");
            chk(MapRole(true, true, true) == "pip", "role pip");
            chk(MapRole(false, true, false) == "other", "role unseen");
            chk(!FlipDefault("auto") && !FlipDefault("") && !FlipDefault(null) && !FlipDefault("false") && FlipDefault("true") && FlipDefault(" TRUE "), "FlipDefault");
            // T-190 A1: camera policy
            chk(AcceptLayerCamera(true, true, true, true), "cam main+screen");
            chk(!AcceptLayerCamera(false, true, true, true) && !AcceptLayerCamera(false, true, false, false), "cam pip never");
            chk(!AcceptLayerCamera(true, true, false, true), "cam non-main with usable main");
            chk(AcceptLayerCamera(true, true, false, false), "cam non-main with unusable main");
            chk(!AcceptLayerCamera(true, false, true, true) && !AcceptLayerCamera(true, false, false, false), "cam disabled");
            chk(!EndOfFrameDue(100, 100, 100, 0), "eof: camera drew this frame");
            chk(!EndOfFrameDue(110, 99, 100, 0), "eof: camera 10 frames ago");
            chk(EndOfFrameDue(130, 99, 100, 0), "eof: camera 30 frames ago");
            chk(!EndOfFrameDue(129, 99, -1, 100) && EndOfFrameDue(130, 99, -1, 100), "eof: never seen, grace 30");
            chk(!EndOfFrameDue(500, -1, -1, -1), "eof: not wanted");
            // T-154 D1: insertion index
            var game = new List<int>(); for (int i = 0; i <= 12; i++) game.Add(i);
            chk(InsertIndex(game) == 13, "InsertIndex game only");
            var withMods = new List<int>(game) { 510, 511 };
            chk(InsertIndex(withMods) == 13, "InsertIndex before other mods");
            chk(InsertIndex(new List<int>()) == 0 && InsertIndex(null) == 0, "InsertIndex empty");
            var withCi = new List<int>(withMods); withCi.Insert(13, 210);
            chk(InsertIndex(withCi, 200) == 13, "InsertIndex radar before contact info");
            var withRadar = new List<int>(withMods); withRadar.Insert(13, 200);
            chk(InsertIndex(withRadar, 210) == 14, "InsertIndex contact info after radar");
            withRadar.Insert(14, 210);
            chk(InsertIndex(withRadar, 202) == 14, "InsertIndex keeps number order among ours");
            // deferred destroy: due exactly 5 frames after Add
            var q = new FrameDeferQueue<string>(5);
            q.Add("rt1");
            int due = 0;
            for (int i = 0; i < 4; i++) due += q.Tick().Count;
            chk(due == 0 && q.Count == 1, "defer: not due before 5 frames");
            chk(q.Tick().Count == 1 && q.Count == 0, "defer: due on frame 5");
            // fail gate: 5 s
            var gate = new FailGate(5.0);
            chk(gate.Allowed(100), "gate open");
            gate.Fail(100);
            chk(!gate.Allowed(104.9) && gate.Allowed(105.0) && gate.Failures == 1, "gate 5 s");
            // heartbeat rotation
            string dir = Path.Combine(Path.GetTempPath(), "vb_hb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "hb.log");
                int n = 0;
                long opens0 = System.Threading.Interlocked.Read(ref Heartbeat.Opens);
                var hb = new Heartbeat(path, () => "line" + (n++) + " " + new string('x', 200), 1024);
                hb.Tick(0.0);                                           // first tick writes
                chk(File.Exists(path) && ReadShared(path).Contains("line0"), "heartbeat first");
                hb.Tick(1.0);                                           // < 2 s: nothing
                chk(n == 1, "heartbeat period");
                for (int i = 1; i < 40; i++) hb.Tick(i * 2.0);
                chk(new FileInfo(path).Length <= 1024 + 400, "heartbeat size bounded " + new FileInfo(path).Length);
                chk(File.Exists(path + ".1"), "heartbeat rotated");
                // T-227: one FileStream for the file plus one per rotation, not one per line
                int rotations = 0; { string rp = path + ".1"; rotations = File.Exists(rp) ? 1 : 0; }
                long opened = System.Threading.Interlocked.Read(ref Heartbeat.Opens) - opens0;
                chk(opened >= 2 && opened <= 12 && opened < 40, "heartbeat opens: " + opened + " for 40 lines (rotations seen " + rotations + ")");
                hb.Dispose();
                long afterDispose = System.Threading.Interlocked.Read(ref Heartbeat.Opens);
                int nBefore = n; hb.Tick(1000.0);
                chk(n == nBefore + 1 && System.Threading.Interlocked.Read(ref Heartbeat.Opens) == afterDispose, "heartbeat after Dispose does not write or open");
                // T-151: the memory log uses a 60 s period
                string p2 = Path.Combine(dir, "mem.log");
                int m = 0;
                var hb2 = new Heartbeat(p2, () => "m" + (m++), 1024, 60.0);
                hb2.Tick(0.0); hb2.Tick(30.0); hb2.Tick(59.9);
                chk(m == 1, "memory heartbeat period 60 s");
                hb2.Tick(60.0);
                chk(m == 2, "memory heartbeat second line");
                // background writer: the line is built at once, the file appears after WaitIdle, order is kept
                string p3 = Path.Combine(dir, "bg.log");
                int k = 0;
                var hb3 = new Heartbeat(p3, () => "bg" + (k++), 1024 * 1024, 2.0, true);
                for (int i = 0; i < 5; i++) hb3.Tick(i * 2.0);
                chk(k == 5 && hb3.WaitIdle(5000), "heartbeat background: lines built and written");
                string[] bgLines = ReadShared(p3).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                // sequence numbers, not wall-clock stamps, decide: every line is there once and in Tick order
                var seq = new List<int>();
                foreach (string bl in bgLines) { int bi = bl.LastIndexOf(" bg", StringComparison.Ordinal); int sv; if (bi >= 0 && int.TryParse(bl.Substring(bi + 3), out sv)) seq.Add(sv); }
                chk(seq.Count == 5 && new HashSet<int>(seq).Count == 5 && seq.TrueForAll(v => v >= 0 && v <= 4), "heartbeat background: all lines " + string.Join("|", bgLines));
                bool inOrder = true; for (int si = 1; si < seq.Count; si++) if (seq[si] < seq[si - 1]) inOrder = false;
                chk(inOrder, "heartbeat background: order " + string.Join("|", bgLines));
                // T-302: external drain: Tick only enqueues, DrainNow writes in order, no pool item
                string p4 = Path.Combine(dir, "ext.log");
                int e4 = 0;
                var hb4 = new Heartbeat(p4, () => "ex" + (e4++), 1024 * 1024, 2.0, true) { ExternalDrain = true };
                for (int i = 0; i < 5; i++) hb4.Tick(i * 2.0);
                chk(e4 == 5 && !hb4.WaitIdle(50) && (!File.Exists(p4) || ReadShared(p4).Length == 0), "heartbeat external: nothing written before DrainNow");
                hb4.DrainNow();
                chk(hb4.WaitIdle(50), "heartbeat external: idle after DrainNow");
                string[] exLines = ReadShared(p4).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool exOrder = exLines.Length == 5;
                for (int xi = 0; xi < exLines.Length && exOrder; xi++) exOrder = exLines[xi].EndsWith(" ex" + xi, StringComparison.Ordinal);
                chk(exOrder, "heartbeat external: five lines in order " + string.Join("|", exLines));
                // T-305: WriteNow drains the queue first, then writes its own line; nothing after Dispose
                string p5 = Path.Combine(dir, "now.log");
                int w5 = 0;
                var hb5 = new Heartbeat(p5, () => "ex" + (w5++), 1024 * 1024, 2.0, true) { ExternalDrain = true };
                hb5.Tick(0); hb5.Tick(2);
                hb5.WriteNow("stuck\n");
                string[] nowLines = ReadShared(p5).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                chk(nowLines.Length == 3 && nowLines[0].EndsWith(" ex0") && nowLines[1].EndsWith(" ex1") && nowLines[2] == "stuck", "heartbeat WriteNow: order " + string.Join("|", nowLines));
                chk(hb5.WaitIdle(50), "heartbeat WriteNow: pending settled");
                hb5.Dispose();
                long opens5 = System.Threading.Interlocked.Read(ref Heartbeat.Opens);
                hb5.WriteNow("late\n");
                chk(!ReadShared(p5).Contains("late") && System.Threading.Interlocked.Read(ref Heartbeat.Opens) == opens5, "heartbeat WriteNow after Dispose does nothing");
                hb2.Dispose(); hb3.Dispose(); hb4.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return errs.Count == 0 ? null : string.Join("; ", errs.ToArray());
        }
    }

    /// <summary>Items released N frames after Add (a render texture is destroyed only when Noesis has certainly let go of it).</summary>
    internal sealed class FrameDeferQueue<T>
    {
        private readonly int _frames;
        private readonly List<KeyValuePair<int, T>> _items = new List<KeyValuePair<int, T>>();
        public FrameDeferQueue(int frames) { _frames = frames; }
        public int Count { get { return _items.Count; } }
        public void Add(T item) { _items.Add(new KeyValuePair<int, T>(_frames, item)); }
        public List<T> Drain()
        {
            var all = new List<T>();
            foreach (var kv in _items) all.Add(kv.Value);
            _items.Clear();
            return all;
        }

        /// <summary>Call once per frame: returns the items whose time is up.</summary>
        public List<T> Tick()
        {
            var due = new List<T>();
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                int left = _items[i].Key - 1;
                if (left <= 0) { due.Insert(0, _items[i].Value); _items.RemoveAt(i); }
                else _items[i] = new KeyValuePair<int, T>(left, _items[i].Value);
            }
            return due;
        }
    }

    /// <summary>After an exception the layer stays off for a while (the game keeps running, the log gets one line).</summary>
    internal sealed class FailGate
    {
        private readonly double _holdS;
        private double _until = double.NegativeInfinity;
        public int Failures { get; private set; }
        public FailGate(double holdS) { _holdS = holdS; }
        public void Fail(double now) { Failures++; _until = now + _holdS; }
        public bool Allowed(double now) { return now >= _until; }
    }

    /// <summary>
    /// Crash breadcrumb: one line every 2 s appended and closed (flushed to the OS), so the last line before a native crash is on disk.
    /// The file is rotated at maxBytes (path -> path.1). Never throws.
    /// </summary>
    internal sealed class Heartbeat
    {
        private readonly string _path;
        private readonly Func<string> _line;
        private readonly long _maxBytes;
        private readonly double _periodS;
        private double _next = double.NegativeInfinity;
        private readonly bool _background;
        private readonly object _io = new object();
        private int _pending;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _queue = new System.Collections.Concurrent.ConcurrentQueue<string>();

        /// <summary>background = the line is built on the caller's thread, the file work (rotation, append, fsync) runs on the thread pool
        /// (live check 0.8.7: the fsync of the 2 s line cost up to 7 ms on the main thread). Lines keep the Tick order (one FIFO queue drained under one lock).</summary>
        public Heartbeat(string path, Func<string> line, long maxBytes = 256 * 1024, double periodS = 2.0, bool background = false)
        {
            _path = path; _line = line; _maxBytes = maxBytes; _periodS = periodS; _background = background;
        }

        /// <summary>T-302: true = Tick only enqueues; the owner thread calls DrainNow (no thread pool item per line).</summary>
        public bool ExternalDrain { get; set; }

        /// <summary>T-302: drains the queue under the io lock on the caller's thread and releases the pending slots it wrote.</summary>
        public void DrainNow()
        {
            int n = 0;
            lock (_io)
            {
                string line;
                while (_queue.TryDequeue(out line)) { Write(line); n++; }
            }
            if (n > 0) System.Threading.Interlocked.Add(ref _pending, -n);
        }

        /// T-305: one synchronous line through the writer's own stream (under _io, after the queued lines). stamped = full line with "\n".
        /// Never throws; after Dispose does nothing.
        public void WriteNow(string stamped)
        {
            try
            {
                int n = 0;
                lock (_io)
                {
                    string line;
                    while (_queue.TryDequeue(out line)) { Write(line); n++; }
                    Write(stamped);
                }
                if (n > 0) System.Threading.Interlocked.Add(ref _pending, -n);
            }
            catch { }
        }

        public void Tick(double now)
        {
            if (now < _next) return;
            _next = now + _periodS;
            try
            {
                string text = _line == null ? "" : _line();
                string stamped = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + "\n";
                if (!_background) { Write(stamped); return; }
                if (System.Threading.Interlocked.Increment(ref _pending) > 8) { System.Threading.Interlocked.Decrement(ref _pending); return; }   // the disk is stuck: drop, never grow
                _queue.Enqueue(stamped);                                // FIFO on the caller's thread: the order of Tick calls
                if (ExternalDrain) return;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { using (VoiceBotBridge.Ui.ModThreads.Work("hb.drain")) { try { Drain(); } finally { System.Threading.Interlocked.Decrement(ref _pending); } } });
            }
            catch { }
        }

        /// <summary>Waits until the queued writes are done (tests, shutdown).</summary>
        public bool WaitIdle(int timeoutMs)
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Volatile.Read(ref _pending) > 0)
            {
                if (t0.ElapsedMilliseconds > timeoutMs) return false;
                System.Threading.Thread.Sleep(1);
            }
            return true;
        }

        /// <summary>Pool items may run in any order under load; each one writes every queued line in FIFO order under the io lock,
        /// so the file order is the Tick order (selftest "heartbeat background: order" was flaky with one line per pool item).</summary>
        private void Drain()
        {
            lock (_io)
            {
                string line;
                while (_queue.TryDequeue(out line)) Write(line);
            }
        }

        /// <summary>How many times a heartbeat FileStream was opened (process-wide; io.hb_opens and tests). T-227: one open per file, plus one per rotation.</summary>
        public static long Opens;
        private System.IO.FileStream _fs;
        private long _len;
        private bool _disposed;

        /// <summary>Closes the long-lived stream (after WaitIdle); later Tick/Write do nothing.</summary>
        public void Dispose()
        {
            lock (_io)
            {
                _disposed = true;
                CloseStream(true);
            }
        }

        private void CloseStream(bool flush)
        {
            var fs = _fs; _fs = null;
            if (fs == null) return;
            try { if (flush) fs.Flush(true); } catch { }
            try { fs.Dispose(); } catch { }
        }

        private void OpenStream()
        {
            _fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            System.Threading.Interlocked.Increment(ref Opens);
            _len = _fs.Length;
        }

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private byte[] _bytes = new byte[512];

        private void Write(string stamped)
        {
            try
            {
                lock (_io)
                {
                    if (_disposed) return;
                    int need = Utf8.GetMaxByteCount(stamped.Length);
                    if (_bytes.Length < need) _bytes = new byte[need];
                    int count = Utf8.GetBytes(stamped, 0, stamped.Length, _bytes, 0);
                    var bytes = _bytes;
                    if (_fs == null) OpenStream();
                    else _len = _fs.Seek(0, SeekOrigin.End);            // T-305: other appenders' bytes: write at the real end, rotate by the real size
                    if (_len > 0 && _len + count >= _maxBytes)
                    {
                        CloseStream(false);                             // close before the move: the file is rotated while no handle is open
                        string old = _path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                        OpenStream();
                    }
                    try
                    {
                        _fs.Write(bytes, 0, count);
                        _fs.Flush(true);                                // fsync stays: the last line before a crash
                        _len += count;
                    }
                    catch { CloseStream(false); }                       // reopened on the next line
                }
            }
            catch { CloseStream(false); }
        }
    }
}
