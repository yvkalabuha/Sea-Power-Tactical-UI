using System;
using System.Collections.Generic;
using System.Threading;

namespace VoiceBotBridge
{
    /// <summary>Config section RadarCoverage (Plugin fills it from BepInEx config; defaults are used by selfcheck).</summary>
    internal sealed class CoverageConfig
    {
        public bool Enabled = true, ShowOnStart = false, TerrainShadows = true, IncludeTargeting = false, ShowOffRadars = true, Debug = false;
        /// <summary>T-182 (ADR-0081): cut the zone by the jamming of jammers known to the player. False = the frame is exactly as before.</summary>
        public bool Jamming = true;
        public string Hotkey = "LeftAlt+V|LeftAlt+Y";
        public string Tiers = CoverageMath.DefaultTiers;
        public double VisibleHeightM = 3.0, FogAlpha = 0.35, ScaleOverride = 0.0;
        /// <summary>T-090 (ADR-0034): motion recompute period, state poll period, RT scale; RenderMode auto|noesis|imgui; LayerIndex -1 = auto; FlipY auto|true|false.</summary>
        public double RecomputeSeconds = 10.0, StateCheckSeconds = 1.0, RtScale = 1.0;
        public string RenderMode = "auto";
        public int LayerIndex = -1;
        public string FlipY = "auto";
        public int Rays = 360, Bins = 128, MaxSegments = 6000;
        // colours (RGBA hex), customer Q2: 15 m red, 150 m yellow, 3000 m green, surface target cyan, off/EMCON grey
        public string Colors = "E03030,E8C020,30C050,30B0E0,909090";
    }

    /// <summary>Immutable result of one coverage calculation; swapped atomically.</summary>
    internal sealed class CoverageFrame
    {
        public int Version;
        public List<RayProfile> Profiles;
        public GeoBox Box;
        public int W, H;
        public byte[] Raster;
        public List<GeoSeg> Segs;
        public double BuildMs;
        public int[] RebuildWhy;        // T-320: copy of the worker counters of this build (by CoverageReuse.Why*)
        public int ProfilesBuilt, ProfilesShifted;
        public int Radars, Reused;      // Reused = profiles taken from the worker cache (as they are or moved to the new centre)
        public string Error;
        /// <summary>T-182: label candidates (one per jammed unit), legend flags, radars with a jamming state, effective ranges per tier by "OwnerUid|Name|ordinal".</summary>
        public List<JamMark> JamMarks = new List<JamMark>();
        public bool AnyJamCut, AnyJamPartial, AnyJamApprox;
        public int JamRadars;
        public Dictionary<string, double[]> JamRangesM = new Dictionary<string, double[]>();
    }

    /// <summary>
    /// T-202 / T-216 / T-225: fixed-size terrain height cache, two arrays (+ one for ages) allocated once, linear probe of at most 8 slots, no growth, no allocation per call.
    /// Cells of a power-of-two grid (level L = BaseDeg x 2^L, at least one profile bin), shared by every tier and radar; the slot with the oldest stamp is evicted (approximate LRU);
    /// NaN (the game has no DEM chunk there) is remembered per NanTileDeg tile and retried after NanRetryBuilds builds.
    /// T-225: the worker asks Peek and sends the misses to the main thread (TerrainBroker); the answers come back through Store. Get = Peek + inner + Store (tests).
    /// Not thread-safe: one per worker thread.
    /// </summary>
    internal sealed class TerrainMemo
    {
        private const long Empty = long.MinValue;
        private readonly long[] _keys;
        private readonly float[] _vals;
        private readonly int[] _stamp;
        private readonly int _mask, _shift;
        private int _count, _now;
        private readonly Dictionary<long, int> _nanTiles = new Dictionary<long, int>();
        public long Hits, Misses, Evictions, NanMisses, NanTileHits;

        public const double BaseDeg = 1.0 / 1024;      // level 0 cell, ~108 m of latitude
        public const int MaxLevel = 8;
        public const int NanRetryBuilds = 6;           // ~60 s at RecomputeSeconds = 10
        public const double NanTileDeg = 0.25;         // negative cache tile (geography only)
        /// <summary>T-225 (T-216-Q1): 2^21 slots = 16 bytes x 2 097 152 = 32 MB, allocated once.</summary>
        public const int DefaultCapacityPow2 = 1 << 21;
        public enum Probe { Hit, NanTile, Miss }

        public TerrainMemo(int capacityPow2 = DefaultCapacityPow2)
        {
            int cap = 16, bits = 4;
            while (cap < capacityPow2 && cap < (1 << 26)) { cap <<= 1; bits++; }
            _keys = new long[cap];
            _vals = new float[cap];
            _stamp = new int[cap];
            _mask = cap - 1;
            _shift = 64 - bits;
            Clear();
        }

        public int Count { get { return _count; } }
        public int Capacity { get { return _keys.Length; } }

        /// <summary>Smallest level whose cell is at least one profile bin (0..MaxLevel).</summary>
        public static int LevelFor(double binM)
        {
            int l = 0;
            while (l < MaxLevel && LevelDeg(l) * 111320.0 < binM) l++;
            return l;
        }

        public static double LevelDeg(int level) { return BaseDeg * (1 << level); }

        /// <summary>Starts the next build: the LRU age and the NaN retry clock tick.</summary>
        public void NextBuild() { _now++; }

        public void Clear()
        {
            for (int i = 0; i < _keys.Length; i++) _keys[i] = Empty;
            _count = 0;
            _nanTiles.Clear();
        }

        private static long TileKey(double lat, double lon)
        {
            return (long)Math.Floor(lat / NanTileDeg) * 100003L + (long)Math.Floor(lon / NanTileDeg);
        }

        private int HashOf(long key) { return (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15UL)) >> _shift); }

        /// <summary>
        /// Looks the point up. Hit: value is the cached height; NanTile: the tile is known to have no data (value NaN); Miss: the caller asks the game for (cellLat, cellLon) and calls Store(key, ...).
        /// count = false does not touch the Hits / NanTileHits counters (the build pass re-reads what the prefetch already counted); the age of a hit is refreshed either way.
        /// </summary>
        public Probe Peek(double lat, double lon, int level, out double value, out double cellLat, out double cellLon, out long key, bool count = true)
        {
            double cell = LevelDeg(level);
            long qa = (long)Math.Round(lat / cell), qo = (long)Math.Round(lon / cell);
            cellLat = qa * cell; cellLon = qo * cell;
            key = (qa * 4000003L + qo) * 16 + level;
            if (key == Empty) key++;
            int h = HashOf(key);
            for (int i = 0; i < 8; i++)
            {
                int s = (h + i) & _mask;
                long k = _keys[s];
                if (k == key) { if (count) Hits++; _stamp[s] = _now; value = _vals[s]; return Probe.Hit; }
                if (k == Empty) break;
            }
            int nanAt;
            if (_nanTiles.TryGetValue(TileKey(cellLat, cellLon), out nanAt) && _now - nanAt < NanRetryBuilds) { if (count) NanTileHits++; value = double.NaN; return Probe.NanTile; }
            value = double.NaN;
            return Probe.Miss;
        }

        /// <summary>The game's answer for a Miss of Peek (one game call): NaN marks the tile, anything else takes a slot (a free one, else the oldest of the window).</summary>
        public void Store(long key, int level, double cellLat, double cellLon, double value)
        {
            Misses++;
            long tile = TileKey(cellLat, cellLon);
            if (double.IsNaN(value)) { NanMisses++; _nanTiles[tile] = _now; return; }
            _nanTiles.Remove(tile);   // the chunk came alive (or was never marked)
            float f = (float)value;
            int h = HashOf(key);
            int free = -1, oldest = h & _mask;
            for (int i = 0; i < 8; i++)
            {
                int s = (h + i) & _mask;
                long k = _keys[s];
                if (k == key) { _vals[s] = f; _stamp[s] = _now; return; }
                if (k == Empty) { free = s; break; }
                if (_stamp[s] < _stamp[oldest]) oldest = s;
            }
            if (free < 0) { free = oldest; Evictions++; }
            else _count++;
            _keys[free] = key;
            _vals[free] = f;
            _stamp[free] = _now;
        }

        /// <summary>Height of the grid cell (level) that holds the point; inner is called on a miss with the cell coordinates. Shared by every profile.</summary>
        public double Get(double lat, double lon, int level, Func<double, double, double> inner)
        {
            double v, cla, clo;
            long key;
            var pr = Peek(lat, lon, level, out v, out cla, out clo, out key);
            if (pr != Probe.Miss) return v;
            v = inner(cla, clo);
            Store(key, level, cla, clo, v);
            return double.IsNaN(v) ? v : (float)v;
        }
    }

    /// <summary>Production tuning of the worker (test seam for the memo size).</summary>
    internal sealed class WorkerTuning
    {
        public int MemoCapacityPow2 = TerrainMemo.DefaultCapacityPow2;
    }

    /// <summary>T-202: when a cached profile can be moved to the radar's new position instead of being rebuilt.</summary>
    internal static class CoverageReuse
    {
        public const double ShiftBinFrac = 0.5;
        /// <summary>A profile without terrain shadows is moved as long as the radar stays within this share of its range (open sea around it stays open).</summary>
        public const double OpenSeaRangeFrac = 0.1;

        public const int WhyNone = 0, WhyState = 1, WhyRange = 2, WhyAxis = 3, WhyDist = 4, WhyNew = 5;

        /// <summary>
        /// T-216: 0 = the cached profile can be moved to the new position, else the reason it has to be rebuilt. The antenna is judged by what it does to the profile, the range
        /// (the horizon bound): if the range moves by more than half a bin the cells no longer line up. A helicopter at 30 m changing by 3 m moves the horizon of the lowest tier
        /// by ~1 km (3 bins), a fighter at 7600 m changing by 300 m by ~7 km, while a 300 m climb of a high-flying aircraft with a capped range does not matter.
        /// </summary>
        public static int ShiftWhy(RayProfile built, RadarSpec builtSpec, RadarSpec now, CoverageTier tier, double minPowerKw, int bins)
        {
            if (built == null || builtSpec == null || now == null || built.Visible == null) return WhyNew;
            if (CoverageMath.RadarStateChanged(builtSpec, now)) return WhyState;
            double nowRange = CoverageMath.ProfileBinM(now, tier, minPowerKw, bins) * Math.Max(1, bins);
            if (Math.Abs(nowRange - built.RangeM) > ShiftBinFrac * built.BinM + 1e-9) return WhyRange;
            if (!builtSpec.FullCircle && Math.Abs(CoverageMath.Wrap180(builtSpec.AxisDeg - now.AxisDeg)) > CoverageMath.SectorTurnDeg) return WhyAxis;
            if (!(built.RangeM > 0)) return built.Terrain ? WhyDist : WhyNone;
            double bearing, dist;
            CoverageMath.BearingDistance(builtSpec.Lat, builtSpec.Lon, now.Lat, now.Lon, builtSpec.Globe, out bearing, out dist);
            bool ok = built.Terrain ? dist <= ShiftBinFrac * built.BinM : dist <= OpenSeaRangeFrac * built.RangeM;
            return ok ? WhyNone : WhyDist;
        }

        /// <summary>Shallow copy with the new centre; shares Visible (immutable after the build).</summary>
        public static RayProfile Shift(RayProfile p, double lat, double lon)
        {
            return new RayProfile
            {
                Lat = lat, Lon = lon, RangeM = p.RangeM, BinM = p.BinM, Rays = p.Rays, Bins = p.Bins, Tier = p.Tier, Active = p.Active, Airborne = p.Airborne,
                Terrain = p.Terrain, Visible = p.Visible, ShadowPct = p.ShadowPct, OwnerUid = p.OwnerUid, Radar = p.Radar, Globe = p.Globe
            };
        }
    }

    /// <summary>T-202: limits the worker's share of the CPU. AfterWork returns the sleep that keeps busy / (busy + sleep) at most maxDuty; short work is collected until sliceMs.</summary>
    internal sealed class WorkerPacer
    {
        public const double MaxDuty = 0.15;
        private readonly double _duty, _slice;
        private double _sliceBusy;
        public double BusyMs { get; private set; }

        public WorkerPacer(double maxDuty = MaxDuty, double sliceMs = 20)
        {
            _duty = maxDuty < 0.01 ? 0.01 : (maxDuty > 1.0 ? 1.0 : maxDuty);
            _slice = sliceMs < 0 ? 0 : sliceMs;
        }

        public int AfterWork(double busyMs)
        {
            if (!(busyMs > 0)) return 0;
            BusyMs += busyMs;
            _sliceBusy += busyMs;
            if (_sliceBusy < _slice) return 0;
            double sleep = _sliceBusy * (1.0 - _duty) / _duty;
            _sliceBusy = 0;
            return (int)Math.Min(60000.0, Math.Ceiling(sleep));
        }
    }

    /// <summary>T-225: where the worker gets terrain heights from. Fetch answers every point (float NaN = unknown); false-free: Done / Cancelled / Stopped.</summary>
    internal interface ITerrainSource
    {
        TerrainBroker.FetchResult Fetch(double[] latLon, int n, float[] heights, bool burst);
    }

    /// <summary>
    /// One background thread; the latest submitted job wins. Never touches game objects and holds no game delegate (T-225, ADR-0092): the terrain cells it needs go in batches to the
    /// TerrainBroker, the main thread answers them within its frame budget, the answers land in the worker's geographic memo; profiles are then built from the memo only.
    /// The "terrain enabled" flag is read by the main thread. Profiles that only moved a little are shifted instead of rebuilt; the load is paced to WorkerPacer.MaxDuty.
    /// </summary>
    internal sealed class CoverageWorker : IDisposable
    {
        private sealed class Job
        {
            public List<RadarSpec> Specs; public List<CoverageTier> Tiers; public double MinPowerKw; public CoverageConfig Cfg;
            public bool TerrainOn;
        }

        /// <summary>T-220: thrown on the worker thread when Dispose was called mid-build; ends the build without a frame (not an error).</summary>
        internal sealed class StopBuild : Exception { }

        /// <summary>T-225: thrown on the worker thread when the build was cancelled (a newer job, layer off, left the mission); ends the build without a frame (not an error).</summary>
        internal sealed class SupersededBuild : Exception { }

        private sealed class Entry { public RadarSpec Spec; public RayProfile Prof; }

        /// <summary>One delegate for all profiles, memo only: the grid level is set before each profile. A miss is NaN and counts as late (the prefetch should have fetched it).</summary>
        private sealed class MemoSampler
        {
            public TerrainMemo Memo; public int Level; public Func<bool> Stopped; public long Late;
            public readonly Func<double, double, double> Delegate;
            public MemoSampler() { Delegate = Sample; }
            private double Sample(double lat, double lon)
            {
                if (Stopped != null && Stopped()) throw new StopBuild();
                double v, cla, clo; long key;
                if (Memo.Peek(lat, lon, Level, out v, out cla, out clo, out key, false) == TerrainMemo.Probe.Miss) Late++;
                return v;
            }
        }

        /// <summary>The product source: the broker (answered by the main thread).</summary>
        private sealed class BrokerSource : ITerrainSource
        {
            private readonly CoverageWorker _w;
            public BrokerSource(CoverageWorker w) { _w = w; }
            public TerrainBroker.FetchResult Fetch(double[] latLon, int n, float[] heights, bool burst)
            {
                return _w._broker.Fetch(latLon, n, heights, burst, () => _w._stop, () => _w.HasPending());
            }
        }

        /// <summary>Tests and the stand only (BuildNow): answers on the calling thread.</summary>
        private sealed class DirectSource : ITerrainSource
        {
            private readonly Func<double, double, double> _game;
            public DirectSource(Func<double, double, double> game) { _game = game; }
            public TerrainBroker.FetchResult Fetch(double[] latLon, int n, float[] heights, bool burst)
            {
                for (int i = 0; i < n; i++)
                {
                    double v;
                    try { v = _game(latLon[2 * i], latLon[2 * i + 1]); } catch (Exception) { v = double.NaN; }
                    heights[i] = (float)v;
                }
                return TerrainBroker.FetchResult.Done;
            }
        }

        /// <summary>A profile the build has to compute (planned before the prefetch).</summary>
        private struct ToBuild { public RadarSpec Spec; public int Tier, Level, Idx; public double Bound; }

        private readonly object _lock = new object();
        private readonly Thread _thread;
        private readonly ManualResetEvent _stopEvt = new ManualResetEvent(false);
        private Job _pending;
        private CoverageFrame _result;
        private volatile bool _stop;
        private int _version;
        private long _buildSeq;
        private readonly Dictionary<long, Entry> _cache = new Dictionary<long, Entry>();
        private string _cacheTag = "";
        private TerrainMemo _memo;
        private readonly MemoSampler _sampler = new MemoSampler();
        private readonly TerrainBroker _broker;
        private readonly CoverageStats _stats;
        private readonly ICoverageProbe _probe;
        private readonly Func<int> _gc;
        private readonly Action<int> _sleep, _onMemoSize;
        internal WorkerTuning Tuning = new WorkerTuning();
        internal TerrainBroker Broker { get { return _broker; } }
        internal TerrainMemo Memo { get { return _memo; } }          // test counters
        internal WorkerPacer Pacer = new WorkerPacer();   // test seam: a slice of 0 ms sleeps after every profile
        private readonly Action<int> _rayHook;
        private System.Diagnostics.Stopwatch _sw;
        private double _sleptMs, _markMs, _waitMs, _paceMs;   // T-232: _sleptMs = _waitMs (terrain wait) + _paceMs (pacer sleep)
        private int _curRadar, _curTier;
        private bool _burst;
        // T-225: the batch of the prefetch (reused between builds)
        private double[] _fxy = new double[2048];
        private long[] _fkey = new long[1024];
        private int[] _flevel = new int[1024];
        private float[] _fh = new float[1024];
        private int _fn, _fcurLevel;
        private readonly HashSet<long> _fseen = new HashSet<long>();
        private readonly Action<double, double> _collect;
        private readonly List<ToBuild> _plan = new List<ToBuild>();
        private ITerrainSource _source;
        internal long TerrainLate;          // T-225: memo misses of the last build pass (must stay 0)
        internal int FetchRounds;           // T-225: rounds of the last build, 0..2

        internal int ProfilesBuilt, ProfilesShifted, ProfilesReused;   // test counters
        internal List<long> RebuiltLog;                                // T-216 test seam: keys of the profiles rebuilt by the next builds (null = off)
        internal int[] RebuildWhy = new int[6];                        // T-216: rebuilds of the last build by CoverageReuse.Why* (WhyNew = nothing cached)
        private double[] _terrScratch;
        private int _gcSleep;
        internal int ProbeCalls;                                       // test counter: probe.Phase calls of the last build

        public CoverageWorker(CoverageStats stats = null, ICoverageProbe probe = null, Func<int> gcCount = null,
                              Action<int> sleepMs = null, Action<int> onMemoSize = null, bool startThread = true, TerrainBroker broker = null)
        {
            _stats = stats;
            _broker = broker;
            _collect = CollectPoint;
            _probe = probe;
            _gc = gcCount;
            _sleep = sleepMs ?? DefaultSleep;
            _onMemoSize = onMemoSize;
            _rayHook = ray => { if (_stop) throw new StopBuild(); Phase(WorkerPhase.Terrain, _curRadar, _curTier, ray); };
            _sampler.Stopped = () => _stop;
            if (startThread)
            {
                _thread = new Thread(LoopTagged) { IsBackground = true, Name = "VoiceBot.Coverage", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }

        /// <summary>terrainEnabled = the main thread read "terrain on" for this job; the terrain itself comes through the broker (no game delegate here).</summary>
        public void Submit(List<RadarSpec> specs, List<CoverageTier> tiers, double minPowerKw, CoverageConfig cfg, bool terrainEnabled)
        {
            lock (_lock)
            {
                _pending = new Job { Specs = specs, Tiers = tiers, MinPowerKw = minPowerKw, Cfg = cfg, TerrainOn = terrainEnabled && _broker != null };
                Monitor.Pulse(_lock);
            }
            if (_broker != null) _broker.Wake();   // a build waiting for terrain notices the newer job
        }

        private bool HasPending() { lock (_lock) return _pending != null; }

        /// <summary>The newest finished frame not taken yet, or null.</summary>
        public CoverageFrame TakeResult()
        {
            lock (_lock) { var r = _result; _result = null; return r; }
        }

        public void Dispose() { Dispose(500); }

        /// <summary>T-220: stops the worker (also in the middle of a build) and waits up to joinMs; true = the thread ended (or never existed).</summary>
        public bool Dispose(int joinMs)
        {
            lock (_lock) { _stop = true; _pending = null; _result = null; Monitor.Pulse(_lock); }
            try { _stopEvt.Set(); } catch { }
            if (_broker != null) _broker.Wake();
            try { if (_thread != null) { _thread.Join(joinMs); return !_thread.IsAlive; } } catch { }
            return _thread == null;
        }

        private void DefaultSleep(int ms)
        {
            try { _stopEvt.WaitOne(ms); } catch { }
        }

        private void Phase(int phase, int radar, int tier, int ray)
        {
            if (_probe == null) return;
            ProbeCalls++;
            try { _probe.Phase(phase, radar, tier, ray); } catch { }
        }

        private void LoopTagged() { VoiceBotBridge.Ui.ModThreads.Enter("sptui-coverage"); try { Loop(); } finally { VoiceBotBridge.Ui.ModThreads.Exit(); } }   // T-212

        private void Loop()
        {
            while (true)
            {
                Job job;
                lock (_lock)
                {
                    while (_pending == null && !_stop)
                    {
                        Phase(WorkerPhase.Wait, 0, 0, 0);
                        Monitor.Wait(_lock);
                    }
                    if (_stop) return;
                    job = _pending; _pending = null;
                }
                CoverageFrame f;
                try { f = RunJob(job, new BrokerSource(this)); }
                catch (StopBuild) { return; }
                if (f == null) { Phase(WorkerPhase.Idle, 0, 0, 0); continue; }   // T-225: the build was cancelled, no frame
                lock (_lock) { if (_stop) return; _result = f; }
                try { PaceNow(0, 0); }   // the owed sleep of the tail of the build, after the frame is published
                catch (StopBuild) { return; }   // Dispose raced in after publishing: not an error, no unhandled exception on the thread
                Phase(WorkerPhase.Idle, 0, 0, 0);
            }
        }

        private CoverageFrame RunJob(Job job, ITerrainSource source)
        {
            _source = source;
            long seq = ++_buildSeq;
            int gc0 = ReadGc();
            _gcSleep = 0;
            ProbeCalls = 0;
            if (_probe != null) { try { _probe.BuildStart(seq, gc0); } catch { } }
            CoverageFrame f;
            try { f = Build(job); }
            catch (StopBuild) { throw; }
            catch (SupersededBuild) { return null; }
            catch (Exception e) { f = new CoverageFrame { Version = ++_version, Profiles = new List<RayProfile>(), Segs = new List<GeoSeg>(), Raster = new byte[0], Error = e.GetType().Name + ": " + e.Message }; }
            double busy = BusySoFar(), wall = _sw == null ? busy : _sw.Elapsed.TotalMilliseconds;
            int gc1 = ReadGc();
            f.BuildMs = busy;
            f.RebuildWhy = (int[])RebuildWhy.Clone(); f.ProfilesBuilt = ProfilesBuilt; f.ProfilesShifted = ProfilesShifted;
            if (_stats != null) _stats.AddBuild(busy, wall, Math.Max(0, gc1 - gc0 - _gcSleep), _burst, _waitMs, _paceMs);   // T-216: collections that happened while the pacer slept are not the build's
            if (_probe != null)
            {
                try { _probe.TerrainCalls(_memo == null ? 0 : _memo.Misses); _probe.BuildEnd(seq, gc1, busy); } catch { }
            }
            return f;
        }

        private int ReadGc()
        {
            if (_gc == null) return 0;
            try { return _gc(); } catch { return 0; }
        }

        private double BusySoFar() { return _sw == null ? 0 : _sw.Elapsed.TotalMilliseconds - _sleptMs; }

        /// <summary>Hands the work done since the last call to the pacer and sleeps the returned time (never under the lock).</summary>
        private void PaceNow(int radar, int tier)
        {
            if (_sw == null) return;
            if (_stop) throw new StopBuild();
            double busy = BusySoFar() - _markMs;
            _markMs += busy;
            if (_burst) return;
            int ms = Pacer.AfterWork(busy);
            if (ms <= 0) return;
            Phase(WorkerPhase.Pace, radar, tier, 0);
            double t0 = _sw.Elapsed.TotalMilliseconds;
            int g0 = ReadGc();
            _sleep(ms);
            _gcSleep += Math.Max(0, ReadGc() - g0);
            double slept = _sw.Elapsed.TotalMilliseconds - t0;
            _sleptMs += slept; _paceMs += slept;
        }

        /// <summary>T-216: key of a cached profile that does not depend on the position of the radar in the list: owner, name (16-bit hash; Entry.Spec.Name is compared as well), ordinal among the owner's radars of that name, tier.</summary>
        internal static long ProfileKey(int ownerUid, string name, int ordinal, int tier)
        {
            uint hash = 2166136261u;
            if (name != null) foreach (char c in name) hash = (hash ^ c) * 16777619u;
            long h16 = (hash ^ (hash >> 16)) & 0xFFFF;
            return ((long)ownerUid << 32) | (h16 << 16) | ((long)(ordinal & 0xFFF) << 4) | (long)(tier & 0xF);
        }

        /// <summary>T-216: per spec, the index among the specs of the same OwnerUid and Name, counted over the whole list BEFORE the Inoperable / ShowOffRadars filters.</summary>
        internal static int[] Ordinals(List<RadarSpec> specs)
        {
            var res = new int[specs.Count];
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < specs.Count; i++)
            {
                string k = specs[i].OwnerUid + "|" + specs[i].Name;
                int n;
                seen.TryGetValue(k, out n);
                res[i] = n;
                seen[k] = n + 1;
            }
            return res;
        }

        /// <summary>Synchronous build for tests and the stand (no thread, no result slot): the terrain delegate answers the fetches inline (DirectSource); null = no terrain.</summary>
        internal CoverageFrame BuildNow(List<RadarSpec> specs, List<CoverageTier> tiers, double minPowerKw, CoverageConfig cfg,
                                        Func<double, double, double> terrain, bool terrainEnabled)
        {
            bool on = terrainEnabled && terrain != null;
            return RunJob(new Job { Specs = specs, Tiers = tiers, MinPowerKw = minPowerKw, Cfg = cfg, TerrainOn = on }, on ? new DirectSource(terrain) : null);
        }

        // ------------------------------------------------------------------------------------------------ T-225 prefetch

        private void CollectPoint(double lat, double lon)
        {
            double v, cla, clo; long key;
            if (_memo.Peek(lat, lon, _fcurLevel, out v, out cla, out clo, out key) != TerrainMemo.Probe.Miss || !_fseen.Add(key)) return;
            if (2 * _fn + 2 > _fxy.Length)
            {
                Array.Resize(ref _fxy, _fxy.Length * 2); Array.Resize(ref _fkey, _fkey.Length * 2); Array.Resize(ref _flevel, _flevel.Length * 2); Array.Resize(ref _fh, _fh.Length * 2);
            }
            _fxy[2 * _fn] = cla; _fxy[2 * _fn + 1] = clo; _fkey[_fn] = key; _flevel[_fn] = _fcurLevel; _fn++;
        }

        /// <summary>Sends the collected cells to the terrain source (the worker waits, TerrainWait) and stores the answers in the memo.</summary>
        private void FetchCollected()
        {
            if (_fn == 0) return;
            FetchRounds++;
            Phase(WorkerPhase.TerrainWait, _curRadar, _curTier, 0);
            double t0 = _sw.Elapsed.TotalMilliseconds;
            TerrainBroker.FetchResult res;
            try { res = _source.Fetch(_fxy, _fn, _fh, _burst); }
            finally { double waited = _sw.Elapsed.TotalMilliseconds - t0; _sleptMs += waited; _waitMs += waited; }   // the wait is not the worker's work (worker_cpu_pct, the pacer)
            if (res == TerrainBroker.FetchResult.Stopped || _stop) throw new StopBuild();
            if (res == TerrainBroker.FetchResult.Cancelled) throw new SupersededBuild();
            for (int i = 0; i < _fn; i++) _memo.Store(_fkey[i], _flevel[i], _fxy[2 * i], _fxy[2 * i + 1], _fh[i]);
            _fn = 0;
            _fseen.Clear();
            // T-232: a newer job arrived while the answers were coming in (Fetch saw "all answered" before it saw the replacement): this build is stale; the answers stay in the memo for the new one
            if (HasPending()) throw new SupersededBuild();
        }

        /// <summary>
        /// Round 1: the open-sea check points of every profile that has to be rebuilt; round 2: the rays of the profiles that are not open sea (decided from the memo).
        /// Afterwards the build pass reads the memo only. At most two fetches per build.
        /// </summary>
        private void Prefetch(CoverageConfig cfg)
        {
            FetchRounds = 0;
            _fn = 0; _fseen.Clear();
            for (int i = 0; i < _plan.Count; i++)
            {
                if (_stop) throw new StopBuild();
                var it = _plan[i];
                _fcurLevel = it.Level;
                CoverageMath.ForEachOpenSeaPoint(it.Spec, it.Bound, _collect);
            }
            FetchCollected();
            for (int i = 0; i < _plan.Count; i++)
            {
                if (_stop) throw new StopBuild();
                var it = _plan[i];
                _sampler.Level = it.Level;
                if (CoverageMath.OpenSea(it.Spec, _sampler.Delegate, it.Bound)) continue;
                _fcurLevel = it.Level; _curRadar = it.Idx; _curTier = it.Tier;
                CoverageMath.ForEachRayPoint(it.Spec, it.Bound / Math.Max(1, cfg.Bins), cfg.Rays, cfg.Bins, _collect);
                PaceNow(it.Idx, it.Tier);
            }
            FetchCollected();
        }

        private CoverageFrame Build(Job job)
        {
            _sw = System.Diagnostics.Stopwatch.StartNew();
            _sleptMs = 0; _markMs = 0; _waitMs = 0; _paceMs = 0;
            var cfg = job.Cfg;
            string tag = job.Tiers.Count + "/" + cfg.Rays + "/" + cfg.Bins + "/" + job.TerrainOn + "/" + job.MinPowerKw;
            foreach (var t in job.Tiers) tag += "/" + t.HeightM + ":" + t.RcsM2 + ":" + t.Air + t.Surface;
            if (tag != _cacheTag) { _cache.Clear(); _cacheTag = tag; }
            // the first build after the layer was switched on (nothing cached) runs without sleeps: the player waits for it
            _burst = _cache.Count == 0;
            Func<double, double, double> terrain = null;
            long hits0 = 0, miss0 = 0, nan0 = 0;
            FetchRounds = 0; TerrainLate = 0;
            if (job.TerrainOn)
            {
                if (_memo == null) _memo = new TerrainMemo(Tuning.MemoCapacityPow2);
                _memo.NextBuild();
                _sampler.Memo = _memo; _sampler.Late = 0;
                terrain = _sampler.Delegate;
                hits0 = _memo.Hits + _memo.NanTileHits; miss0 = _memo.Misses; nan0 = _memo.NanMisses;
            }
            var profiles = new List<RayProfile>();
            var used = new HashSet<long>();
            int idx = 0, reused = 0, si = -1;
            int[] ordinals = Ordinals(job.Specs);
            ProfilesBuilt = ProfilesShifted = ProfilesReused = 0;
            Array.Clear(RebuildWhy, 0, RebuildWhy.Length);
            if (_terrScratch == null || _terrScratch.Length < cfg.Bins) _terrScratch = new double[Math.Max(16, cfg.Bins)];
            if (terrain != null)
            {
                // T-225: which profiles will be rebuilt (same rules as the pass below), then one or two batches of terrain cells to the main thread
                _plan.Clear();
                int pi = 0, psi = -1;
                foreach (var spec in job.Specs)
                {
                    psi++;
                    if (spec.Inoperable) continue;
                    if (!CoverageMath.Emitting(spec) && !cfg.ShowOffRadars) continue;
                    for (int ti = 0; ti < job.Tiers.Count; ti++)
                    {
                        Entry o;
                        if (_cache.TryGetValue(ProfileKey(spec.OwnerUid, spec.Name, ordinals[psi], ti), out o) && o.Spec.Name == spec.Name)
                        {
                            if (!CoverageMath.SpecChanged(o.Spec, spec)) continue;
                            if (CoverageReuse.ShiftWhy(o.Prof, o.Spec, spec, job.Tiers[ti], job.MinPowerKw, cfg.Bins) == CoverageReuse.WhyNone) continue;
                        }
                        double bound = CoverageMath.ProfileBoundM(spec, job.Tiers[ti], job.MinPowerKw);
                        if (!(bound > 0)) continue;
                        _plan.Add(new ToBuild { Spec = spec, Tier = ti, Idx = pi, Bound = bound, Level = TerrainMemo.LevelFor(bound / Math.Max(1, cfg.Bins)) });
                    }
                    pi++;
                }
                if (_plan.Count > 0) Prefetch(cfg);
            }
            var dashed = new List<GeoSeg>();
            var jamByOwner = new Dictionary<int, KeyValuePair<List<RadarSpec>, List<JamSpecResult>>>();
            var jamRanges = new Dictionary<string, double[]>();
            int jamRadars = 0;
            bool anyCut = false;
            foreach (var spec in job.Specs)
            {
                si++;
                if (spec.Inoperable) continue;
                bool inactive = !CoverageMath.Emitting(spec);
                if (inactive && !cfg.ShowOffRadars) continue;
                var specProfiles = new List<RayProfile>(job.Tiers.Count);
                for (int ti = 0; ti < job.Tiers.Count; ti++)
                {
                    if (_stop) throw new StopBuild();
                    long key = ProfileKey(spec.OwnerUid, spec.Name, ordinals[si], ti);
                    used.Add(key);
                    Entry old;
                    RayProfile p;
                    if (_cache.TryGetValue(key, out old) && old.Spec.Name == spec.Name && !CoverageMath.SpecChanged(old.Spec, spec)) { p = old.Prof; reused++; ProfilesReused++; }
                    else
                    {
                        int why = CoverageReuse.WhyNew;
                        if (old != null && old.Spec.Name == spec.Name)
                        {
                            why = CoverageReuse.ShiftWhy(old.Prof, old.Spec, spec, job.Tiers[ti], job.MinPowerKw, cfg.Bins);
                        }
                        if (why == CoverageReuse.WhyNone) { p = CoverageReuse.Shift(old.Prof, spec.Lat, spec.Lon); reused++; ProfilesShifted++; }
                        else
                        {
                        RebuildWhy[why]++;
                        if (RebuiltLog != null) RebuiltLog.Add(key);
                        _curRadar = idx; _curTier = ti;
                        Phase(WorkerPhase.Profile, idx, ti, 0);
                        if (terrain != null)
                        {
                            double binM = CoverageMath.ProfileBinM(spec, job.Tiers[ti], job.MinPowerKw, cfg.Bins);
                            _sampler.Level = TerrainMemo.LevelFor(binM);
                        }
                        p = CoverageMath.BuildProfile(spec, job.Tiers[ti], ti, job.MinPowerKw, terrain, cfg.Rays, cfg.Bins, _probe == null ? null : _rayHook, _terrScratch);
                        if (old == null) _cache[key] = old = new Entry();
                        old.Spec = spec; old.Prof = p;
                        ProfilesBuilt++;
                        PaceNow(idx, ti);
                        }
                    }
                    specProfiles.Add(p);
                }
                if (cfg.Jamming && spec.Jam != null)
                {
                    // T-182: the cache holds the uncut profile; the cut is a cheap step after it
                    Phase(WorkerPhase.Jam, idx, 0, 0);
                    var jr = CoverageJamMath.ApplySpec(spec, job.Tiers, specProfiles, job.MinPowerKw);
                    profiles.AddRange(jr.Profiles);
                    dashed.AddRange(jr.Dashed);
                    if (spec.Jam.State != CoverageJamMath.StateNone && CoverageMath.Emitting(spec)) jamRadars++;
                    if (jr.Cut) anyCut = true;
                    jamRanges[spec.OwnerUid + "|" + spec.Name + "|" + ordinals[si]] = jr.RangeM;
                    KeyValuePair<List<RadarSpec>, List<JamSpecResult>> grp;
                    if (!jamByOwner.TryGetValue(spec.OwnerUid, out grp)) jamByOwner[spec.OwnerUid] = grp = new KeyValuePair<List<RadarSpec>, List<JamSpecResult>>(new List<RadarSpec>(), new List<JamSpecResult>());
                    grp.Key.Add(spec); grp.Value.Add(jr);
                }
                else profiles.AddRange(specProfiles);
                idx++;
            }
            if (used.Count != _cache.Count)
            {
                var drop = new List<long>();
                foreach (var k in _cache.Keys) if (!used.Contains(k)) drop.Add(k);
                foreach (var k in drop) _cache.Remove(k);
            }
            var box = CoverageMath.Bounds(profiles, 0.1);
            bool globe = profiles.Count > 0 && profiles[0].Globe;   // T-352: all profiles of a frame come from specs of one tick (one world)
            double mPerDeg = globe ? Math.PI / 180.0 * CoverageMath.EarthRadiusM : CoverageMath.FlatMPerDeg;
            double latM = (box.LatMax - box.LatMin) * mPerDeg;
            double lonM = globe
                ? (box.LonMax - box.LonMin) * Math.PI / 180.0 * CoverageMath.EarthRadiusM * Math.Max(0.05, Math.Cos((box.LatMax + box.LatMin) * 0.5 * Math.PI / 180.0))
                : (box.LonMax - box.LonMin) * mPerDeg;
            int w = (int)Math.Max(8, Math.Min(512, Math.Ceiling(lonM / (0.25 * CoverageMath.MPerNm))));
            int h = (int)Math.Max(8, Math.Min(512, Math.Ceiling(latM / (0.25 * CoverageMath.MPerNm))));
            Phase(WorkerPhase.Raster, idx, 0, 0);
            var frame = new CoverageFrame
            {
                Version = ++_version, Profiles = profiles, Box = box, W = w, H = h, Radars = idx, Reused = reused,
                Raster = profiles.Count == 0 ? new byte[0] : CoverageMath.Rasterize(profiles, box, w, h)
            };
            Phase(WorkerPhase.Outline, idx, 0, 0);
            frame.Segs = CoverageMath.Outlines(profiles);
            if (cfg.Jamming)
            {
                frame.Segs.AddRange(dashed);
                frame.JamRangesM = jamRanges;
                frame.JamRadars = jamRadars;
                frame.AnyJamCut = anyCut;
                foreach (var g in jamByOwner.Values)
                {
                    var mark = CoverageJamMath.MarkFor(g.Key, g.Value, job.Tiers, job.MinPowerKw);
                    if (mark == null) continue;
                    frame.JamMarks.Add(mark);
                    if (mark.State != CoverageJamMath.StateKnown) frame.AnyJamPartial = true;
                    if (mark.Approx) frame.AnyJamApprox = true;   // T-225
                }
            }
            if (_stats != null) _stats.AddProfiles(ProfilesBuilt, ProfilesShifted, ProfilesReused, RebuildWhy);
            if (_memo != null)
            {
                TerrainLate = _sampler.Late;
                if (_stats != null) _stats.SetFetchRounds(FetchRounds);
                if (_stats != null) _stats.AddTerrain(_memo.Misses - miss0, _memo.Hits + _memo.NanTileHits - hits0, _memo.Misses - miss0, _memo.NanMisses - nan0, _memo.Count, _memo.Capacity);
                if (_onMemoSize != null) { try { _onMemoSize(_memo.Count); } catch { } }
            }
            return frame;
        }

        // ------------------------------------------------------------------------------------------------ self test
        private static RadarSpec TestRadar(int i, double lat, double lon, bool air)
        {
            return new RadarSpec
            {
                Globe = true,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                OwnerUid = i, OwnerName = "U" + i, Name = "SPS-49", Type = "Search", Air = true, Surface = i % 2 == 0, On = true, Emcon = false, Airborne = air,
                Lat = lat, Lon = lon, AntennaM = air ? 7600 : 30, MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000,
                PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, FullCircle = true, VisibleHeightM = 3, TerrainUnderAntennaM = -10
            };
        }

        private static RadarSpec Clone(RadarSpec s)
        {
            return new RadarSpec
            {
                OwnerUid = s.OwnerUid, OwnerName = s.OwnerName, Name = s.Name, Type = s.Type, Air = s.Air, Surface = s.Surface, On = s.On, Emcon = s.Emcon, Airborne = s.Airborne,
                Lat = s.Lat, Lon = s.Lon, AntennaM = s.AntennaM, MinRangeM = s.MinRangeM, MaxRangeM = s.MaxRangeM, MinAltM = s.MinAltM, MaxAltM = s.MaxAltM,
                PeakPowerKw = s.PeakPowerKw, GainFactor = s.GainFactor, WaveLengthM = s.WaveLengthM, FullCircle = s.FullCircle, VisibleHeightM = s.VisibleHeightM,
                TerrainUnderAntennaM = s.TerrainUnderAntennaM, Globe = s.Globe
            };
        }

        private static RadarSpec DeadClone(RadarSpec s) { var c = Clone(s); c.Inoperable = true; return c; }

        private static void MoveM(RadarSpec s, double bearingDeg, double distM)
        {
            double la, lo;
            CoverageMath.Destination(s.Lat, s.Lon, bearingDeg, distM, s.Globe, out la, out lo);
            s.Lat = la; s.Lon = lo;
        }

        private sealed class ProbeLog : ICoverageProbe
        {
            public int Phases, Starts, Ends; public long Terrain; public bool SawTerrain, SawRaster, SawOutline, SawProfile, SawWait;
            public void Phase(int phase, int radar, int tier, int ray)
            {
                Phases++;
                if (phase == WorkerPhase.Terrain) SawTerrain = true;
                if (phase == WorkerPhase.Raster) SawRaster = true;
                if (phase == WorkerPhase.Outline) SawOutline = true;
                if (phase == WorkerPhase.Profile) SawProfile = true;
                if (phase == WorkerPhase.TerrainWait) SawWait = true;
            }
            public void BuildStart(long buildSeq, int gc0) { Starts++; }
            public void BuildEnd(long buildSeq, int gc0, double busyMs) { Ends++; }
            public void TerrainCalls(long total) { Terrain = total; }
        }

        private static bool SameBits(RayProfile a, RayProfile b)
        {
            if (a.Visible == null || b.Visible == null || a.Visible.Length != b.Visible.Length) return false;
            for (int i = 0; i < a.Visible.Length; i++) if (a.Visible[i] != b.Visible[i]) return false;
            return true;
        }

        /// <summary>puretest section "coverageworker".</summary>
        public static string SelfTest()
        {
            try
            {
                string err;
                var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
                if (tiers == null || tiers.Count != 4) return "tiers: " + err;
                var cfg = new CoverageConfig { Rays = 72, Bins = 48, Jamming = false };

                // ---- TerrainMemo
                {
                    var m = new TerrainMemo(1 << 12);
                    int calls = 0;
                    Func<double, double, double> inner = (la, lo) => { calls++; return la * 10 + lo; };
                    int cap = m.Capacity;
                    for (int i = 0; i < 1000000; i++) m.Get(i * 0.001, (i % 977) * 0.002, 0, inner);
                    if (m.Count > m.Capacity || m.Capacity != cap) return "memo count " + m.Count + "/" + m.Capacity;
                    if (m.Evictions == 0) return "memo evictions 0";
                    var m2 = new TerrainMemo(1 << 12);
                    calls = 0;
                    double v1 = m2.Get(10.0001, 20.0001, 0, inner), v2 = m2.Get(10.0002, 20.0002, 0, inner);
                    if (calls != 1 || m2.Hits != 1 || Math.Abs(v1 - v2) > 1e-6) return "memo hit " + calls + " " + m2.Hits;
                    int nan = 0;
                    Func<double, double, double> nanFn = (la, lo) => { nan++; return double.NaN; };
                    m2.Get(1, 1, 0, nanFn); m2.Get(1, 1, 0, nanFn);
                    if (nan != 1 || m2.Count != 1 || m2.NanTileHits != 1) return "memo NaN tile " + nan + " " + m2.Count;   // T-216: NaN is remembered per tile
                    m2.Get(2, 2, 0, inner);
                    long before = AllocBytes();
                    for (int i = 0; i < 100000; i++) m2.Get(2, 2, 0, inner);
                    if (before >= 0 && AllocBytes() - before > 1024) return "memo alloc " + (AllocBytes() - before);
                    m2.Clear();
                    if (m2.Count != 0) return "memo clear";
                    // the same point at two levels is two entries
                    calls = 0; m2.Get(5, 5, 0, inner); m2.Get(5, 5, 1, inner);
                    if (calls != 2) return "memo level key " + calls;
                    // T-225: production default size, allocated once
                    if (TerrainMemo.DefaultCapacityPow2 != (1 << 21) || new WorkerTuning().MemoCapacityPow2 != (1 << 21)) return "T-225 memo default size";
                    // T-225: Peek + Store give the same counters as Get
                    var pa = new TerrainMemo(1 << 12); var pb = new TerrainMemo(1 << 12);
                    Func<double, double, double> innerP = (la, lo) => lo > 20.5 ? double.NaN : la * 10 + lo;
                    for (int i = 0; i < 3000; i++)
                    {
                        double la = 10 + (i % 211) * 0.002, lo = 20 + (i % 97) * 0.01;
                        double va = pa.Get(la, lo, 1, innerP), vb, cla, clo; long key;
                        var pr = pb.Peek(la, lo, 1, out vb, out cla, out clo, out key);
                        if (pr == TerrainMemo.Probe.Miss) { vb = innerP(cla, clo); pb.Store(key, 1, cla, clo, vb); if (!double.IsNaN(vb)) vb = (float)vb; }
                        if (!(va == vb || (double.IsNaN(va) && double.IsNaN(vb)))) return "T-225 Peek/Store value " + i;
                    }
                    if (pa.Hits != pb.Hits || pa.Misses != pb.Misses || pa.Evictions != pb.Evictions || pa.NanMisses != pb.NanMisses || pa.NanTileHits != pb.NanTileHits || pa.Count != pb.Count) return "T-225 Peek/Store counters";
                    double pv, pcla, pclo; long pkey;
                    long h0 = pb.Hits;
                    pb.Peek(10.0, 20.0, 1, out pv, out pcla, out pclo, out pkey, false);
                    if (pb.Hits != h0) return "T-225 Peek(count: false) counted";
                }

                // ---- BinM helper agrees with BuildProfile
                {
                    var r = TestRadar(1, 15, 42, false);
                    for (int ti = 0; ti < tiers.Count; ti++)
                    {
                        var p = CoverageMath.BuildProfile(r, tiers[ti], ti, 2e-14, null, 72, 48);
                        double b = CoverageMath.ProfileBinM(r, tiers[ti], 2e-14, 48);
                        if (Math.Abs(p.BinM - b) > 1e-6) return "ProfileBinM " + p.BinM + " vs " + b;
                    }
                }

                // ---- reference: the worker builds the same bits as BuildProfile (synthetic island), probe phases
                var island = new Func<double, double, double>((la, lo) => (la > 15.3 && la < 15.5 && lo > 42.1 && lo < 42.3) ? 300.0 : -10.0);
                {
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 8; i++) specs.Add(TestRadar(i, 15.0 + i * 0.03, 42.0 + i * 0.03, i == 7));
                    var log = new ProbeLog();
                    var w = new CoverageWorker(null, log, null, ms => { }, null, false);
                    var f = w.BuildNow(specs, tiers, 2e-14, cfg, island, true);
                    if (f.Error != null) return "worker error " + f.Error;
                    if (f.Profiles.Count != 8 * tiers.Count || f.Radars != 8 || f.Segs.Count == 0) return "worker frame " + f.Profiles.Count;
                    for (int i = 0; i < 8; i++)
                        for (int ti = 0; ti < tiers.Count; ti++)
                        {
                            var q = f.Profiles[i * tiers.Count + ti];
                            if (q.Tier != ti || q.OwnerUid != i) return "worker order";
                        }
                    if (log.Starts != 1 || log.Ends != 1 || !log.SawProfile || !log.SawRaster || !log.SawOutline) return "probe phases";
                    // open sea: bit identical to the direct call
                    var sea = new Func<double, double, double>((la, lo) => -10.0);
                    var w2 = new CoverageWorker(null, null, null, ms => { }, null, false);
                    var f2 = w2.BuildNow(specs, tiers, 2e-14, cfg, sea, true);
                    for (int i = 0; i < 8; i++)
                        for (int ti = 0; ti < tiers.Count; ti++)
                        {
                            var direct = CoverageMath.BuildProfile(specs[i], tiers[ti], ti, 2e-14, null, cfg.Rays, cfg.Bins);
                            if (!SameBits(direct, f2.Profiles[i * tiers.Count + ti])) return "open sea bits differ radar " + i + " tier " + ti;
                        }
                }

                // ---- A2: terrain disabled = no game calls
                {
                    int calls = 0;
                    Func<double, double, double> counting = (la, lo) => { calls++; return -10.0; };
                    var specs = new List<RadarSpec> { TestRadar(1, 15, 42, false), TestRadar(2, 15.1, 42.1, false) };
                    var w = new CoverageWorker(null, null, null, ms => { }, null, false);
                    w.BuildNow(specs, tiers, 2e-14, cfg, counting, false);
                    if (calls != 0) return "terrain calls with the flag off: " + calls;
                    w.BuildNow(specs, tiers, 2e-14, cfg, counting, true);
                    if (calls == 0) return "terrain not called with the flag on";
                }

                // ---- T-352: the raster cell (0.25 nm) follows the world of the profiles
                foreach (bool wg in new[] { false, true })
                {
                    var sp = TestRadar(1, 60, 10, false); sp.Globe = wg;
                    var ww = new CoverageWorker(null, null, null, ms => { }, null, false);
                    var fw = ww.BuildNow(new List<RadarSpec> { sp }, tiers, 2e-14, cfg, null, false);
                    double mDeg = wg ? Math.PI / 180.0 * CoverageMath.EarthRadiusM : CoverageMath.FlatMPerDeg;
                    double wantLonM = (fw.Box.LonMax - fw.Box.LonMin) * mDeg * (wg ? Math.Cos((fw.Box.LatMax + fw.Box.LatMin) * 0.5 * Math.PI / 180.0) : 1.0);
                    int wantW = (int)Math.Max(8, Math.Min(512, Math.Ceiling(wantLonM / (0.25 * CoverageMath.MPerNm))));
                    int wantH = (int)Math.Max(8, Math.Min(512, Math.Ceiling((fw.Box.LatMax - fw.Box.LatMin) * mDeg / (0.25 * CoverageMath.MPerNm))));
                    if (fw.W != wantW || fw.H != wantH) return "T-352 raster size globe=" + wg + ": " + fw.W + "x" + fw.H + " want " + wantW + "x" + wantH;
                    if (!wg && Math.Abs(fw.W - fw.H) > 2) return "T-352 flat raster must be square in degrees: " + fw.W + "x" + fw.H;
                }

                // ---- A3: shift instead of rebuild
                {
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 24; i++) specs.Add(TestRadar(i, 14.0 + i * 0.05, 41.0 + (i % 5) * 0.05, false));
                    var sea = new Func<double, double, double>((la, lo) => -10.0);
                    var w = new CoverageWorker(null, null, null, ms => { }, null, false);
                    var f1 = w.BuildNow(specs, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt != 96) return "A3 first build " + w.ProfilesBuilt;
                    var moved = new List<RadarSpec>();
                    foreach (var s in specs) { var c = Clone(s); MoveM(c, 90, 50); moved.Add(c); }
                    var f2 = w.BuildNow(moved, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt != 0 || w.ProfilesShifted != 96) return "A3 shifted " + w.ProfilesShifted + " built " + w.ProfilesBuilt;
                    for (int k = 0; k < f2.Profiles.Count; k++)
                    {
                        var a = f1.Profiles[k]; var b = f2.Profiles[k];
                        if (b.Lat != moved[k / tiers.Count].Lat || b.Lon != moved[k / tiers.Count].Lon || !SameBits(a, b) || ReferenceEquals(a, b)) return "A3 profile " + k;
                    }
                    if (f2.Reused != 96) return "A3 reused " + f2.Reused;
                    // state changes always rebuild
                    var off = new List<RadarSpec>();
                    foreach (var s in moved) { var c = Clone(s); c.Emcon = true; off.Add(c); }
                    w.BuildNow(off, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt != 96) return "A3 emcon rebuilt " + w.ProfilesBuilt;
                    var ant = new List<RadarSpec>();
                    foreach (var s in off) { var c = Clone(s); c.Emcon = true; c.AntennaM = 300; ant.Add(c); }
                    var fant = w.BuildNow(ant, tiers, 2e-14, cfg, sea, true);
                    // T-216: an antenna change rebuilds what it changes in the range (horizon); a profile limited by the equation or MaxRange is the same
                    if (w.ProfilesBuilt == 0 || w.ProfilesBuilt + w.ProfilesShifted != 96) return "A3 antenna rebuilt " + w.ProfilesBuilt;
                    for (int k = 0; k < fant.Profiles.Count; k++)
                    {
                        var direct = CoverageMath.BuildProfile(ant[k / tiers.Count], tiers[k % tiers.Count], k % tiers.Count, 2e-14, null, cfg.Rays, cfg.Bins);
                        if (Math.Abs(direct.RangeM - fant.Profiles[k].RangeM) > 0.5 * direct.BinM + 1e-6) return "A3 antenna range " + k;
                    }
                    // a long way over open sea is not a shift (more than 10 % of the range)
                    var far = new List<RadarSpec>();
                    foreach (var s in ant) { var c = Clone(s); c.Emcon = true; c.AntennaM = 300; MoveM(c, 0, 80000); far.Add(c); }
                    var ff = w.BuildNow(far, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt == 0) return "A3 far not rebuilt";
                    int emptyCount = 0;
                    foreach (var q in ff.Profiles) if (!(q.RangeM > 0)) emptyCount++;
                    if (w.ProfilesShifted > emptyCount) return "A3 far shift " + w.ProfilesShifted + " empty " + emptyCount;   // only empty profiles (no kind fits) may move any distance
                    // coast: terrain profiles move only within half a bin
                    var coast = new Func<double, double, double>((la, lo) => lo > 42.05 ? 100.0 : -10.0);
                    var cs = new List<RadarSpec> { TestRadar(1, 15.0, 42.0, false) };
                    var wc = new CoverageWorker(null, null, null, ms => { }, null, false);
                    var fc = wc.BuildNow(cs, tiers, 2e-14, cfg, coast, true);
                    bool anyTerrain = false; double binM = 0;
                    foreach (var p in fc.Profiles) if (p.Terrain) { anyTerrain = true; binM = p.BinM; }
                    if (!anyTerrain) return "A3 coast profile has no terrain";
                    var near = new List<RadarSpec>(); var c1 = Clone(cs[0]); MoveM(c1, 90, 0.3 * binM); near.Add(c1);
                    wc.BuildNow(near, tiers, 2e-14, cfg, coast, true);
                    if (wc.ProfilesShifted == 0) return "A3 coast near not shifted";
                    int shiftedNear = wc.ProfilesShifted;
                    var farC = new List<RadarSpec>(); var c2 = Clone(cs[0]); MoveM(c2, 90, 0.8 * binM); farC.Add(c2);
                    wc.BuildNow(farC, tiers, 2e-14, cfg, coast, true);
                    if (wc.ProfilesBuilt == 0) return "A3 coast far not rebuilt";
                    if (wc.ProfilesShifted >= shiftedNear + 1 && wc.ProfilesBuilt == 0) return "A3 coast far shifted";
                }

                // ---- T-216 A1: the profile key does not depend on the order of the radars
                {
                    var ord = Ordinals(new List<RadarSpec> { TestRadar(1, 15, 42, false), TestRadar(1, 15, 42.1, false), TestRadar(2, 15, 42.2, false) });
                    if (ord[0] != 0 || ord[1] != 1 || ord[2] != 0) return "T-216 ordinals " + ord[0] + ord[1] + ord[2];
                    if (ProfileKey(1, "SPS-49", 0, 0) == ProfileKey(1, "SPS-49", 1, 0) || ProfileKey(1, "SPS-49", 0, 0) == ProfileKey(1, "SPS-49", 0, 1) || ProfileKey(1, "SPS-49", 0, 0) == ProfileKey(2, "SPS-49", 0, 0)) return "T-216 key collision";
                    var sea = new Func<double, double, double>((la, lo) => -10.0);
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 24; i++) specs.Add(TestRadar(i, 14.0 + i * 0.05, 41.0 + (i % 5) * 0.05, false));
                    var w = new CoverageWorker(null, null, null, ms => { }, null, false);
                    w.Tuning = new WorkerTuning { MemoCapacityPow2 = 1 << 14 };
                    w.BuildNow(specs, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt != 96) return "T-216 A1 first build " + w.ProfilesBuilt;
                    var re = new List<RadarSpec>();
                    for (int i = specs.Count - 1; i >= 0; i--) if (i != 3) re.Add(i == 10 ? DeadClone(specs[i]) : specs[i]);
                    w.BuildNow(re, tiers, 2e-14, cfg, sea, true);
                    if (w.ProfilesBuilt != 0 || w.ProfilesReused != 4 * 22) return "T-216 A1 permutation: built " + w.ProfilesBuilt + " reused " + w.ProfilesReused;
                    // two radars with the same name of one owner are not mixed up
                    var a = TestRadar(1, 15.0, 42.0, false); var b = TestRadar(1, 15.0, 42.5, false); b.PeakPowerKw = 50;
                    var twin = new CoverageWorker(null, null, null, ms => { }, null, false);
                    twin.Tuning = new WorkerTuning();
                    var f1 = twin.BuildNow(new List<RadarSpec> { a, b }, tiers, 2e-14, cfg, sea, true);
                    var other = TestRadar(5, 14.0, 41.0, false);
                    var f2 = twin.BuildNow(new List<RadarSpec> { other, a, b }, tiers, 2e-14, cfg, sea, true);
                    if (twin.ProfilesBuilt != 4 || twin.ProfilesReused != 8) return "T-216 A1 twins built " + twin.ProfilesBuilt + " reused " + twin.ProfilesReused;
                    for (int ti = 0; ti < tiers.Count; ti++)
                    {
                        if (f2.Profiles[4 + ti].Lon != a.Lon || f2.Profiles[8 + ti].Lon != b.Lon) return "T-216 A1 twins mixed up";
                        if (!SameBits(f2.Profiles[8 + ti], CoverageMath.BuildProfile(b, tiers[ti], ti, 2e-14, null, cfg.Rays, cfg.Bins))) return "T-216 A1 twin bits";
                    }
                }

                // ---- T-216 A2: geographic terrain cache
                {
                    if (TerrainMemo.LevelFor(312) != 2 || TerrainMemo.LevelFor(2600) != 5 || TerrainMemo.LevelFor(0) != 0 || TerrainMemo.LevelFor(1e9) != TerrainMemo.MaxLevel) return "T-216 LevelFor " + TerrainMemo.LevelFor(312) + " " + TerrainMemo.LevelFor(2600);
                    int calls = 0;
                    Func<double, double, double> inner = (la, lo) => { calls++; return la * 10 + lo; };
                    var m = new TerrainMemo(1 << 12);
                    m.Get(10.0001, 20.0001, 3, inner); m.Get(10.0003, 20.0002, 3, inner);   // two profiles of different tiers at the same level
                    if (calls != 1 || m.Hits != 1) return "T-216 shared cell: calls " + calls;
                    m.Get(10.0001, 20.0001, 4, inner);
                    if (calls != 2) return "T-216 another level is another cell";
                    // approximate LRU: the points of the last builds survive 2x capacity of other points
                    var lru = new TerrainMemo(1 << 13);
                    lru.NextBuild();
                    for (int i = 0; i < 2 * lru.Capacity; i++) lru.Get(i * 0.01, 5.0, 0, inner);
                    lru.NextBuild();
                    for (int i = 0; i < 1000; i++) lru.Get(40 + i * 0.01, 7.0, 0, inner);
                    lru.NextBuild();
                    for (int i = 0; i < 1000; i++) lru.Get(70 + i * 0.01, 9.0, 0, inner);
                    long h0 = lru.Hits;
                    for (int i = 0; i < 1000; i++) lru.Get(40 + i * 0.01, 7.0, 0, inner);
                    if (lru.Hits - h0 < 900) return "T-216 LRU kept " + (lru.Hits - h0) + " of 1000";
                    // NaN tile: no game call until NanRetryBuilds builds passed, then one probe; an alive tile is cached
                    var nm = new TerrainMemo(1 << 12);
                    int nanCalls = 0; bool alive = false;
                    Func<double, double, double> nanFn = (la, lo) => { nanCalls++; return alive ? 50.0 : double.NaN; };
                    nm.Get(10.10, 20.10, 3, nanFn); nm.Get(10.15, 20.15, 3, nanFn); nm.Get(10.2, 20.05, 5, nanFn);
                    if (nanCalls != 1 || nm.NanTileHits != 2 || nm.NanMisses != 1) return "T-216 NaN tile: calls " + nanCalls;
                    for (int i = 0; i < TerrainMemo.NanRetryBuilds - 1; i++) nm.NextBuild();
                    nm.Get(10.12, 20.12, 3, nanFn);
                    if (nanCalls != 1) return "T-216 NaN retried too early";
                    nm.NextBuild();
                    nm.Get(10.12, 20.12, 3, nanFn); nm.Get(10.14, 20.14, 3, nanFn);
                    if (nanCalls != 2) return "T-216 NaN probe: calls " + nanCalls;
                    for (int i = 0; i < TerrainMemo.NanRetryBuilds; i++) nm.NextBuild();
                    alive = true;
                    double av = nm.Get(10.12, 20.12, 3, nanFn);
                    if (nanCalls != 3 || Math.Abs(av - 50.0) > 1e-3) return "T-216 NaN alive: " + nanCalls + " " + av;
                    nm.Get(10.12, 20.12, 3, nanFn); nm.Get(10.20, 20.20, 3, nanFn);
                    if (nanCalls != 4) return "T-216 NaN alive tile cached: " + nanCalls;
                    long before = AllocBytes();
                    for (int i = 0; i < 100000; i++) nm.Get(10.12, 20.12, 3, nanFn);
                    if (before >= 0 && AllocBytes() - before > 1024) return "T-216 geo memo alloc " + (AllocBytes() - before);
                }

                // ---- T-216 A3: shift by the change of the range, reasons of the rebuilds
                {
                    var tier0 = tiers[0];
                    const int nb = 128;
                    var coast = new Func<double, double, double>((la, lo) => lo > 42.2 ? 100.0 + 200.0 * Math.Sin(la * 300.0) : -10.0);
                    // helicopter at 30 m, +3 m: the horizon of the lowest tier moves by ~1 km, several bins
                    var heli = TestRadar(1, 15.0, 42.0, true); heli.AntennaM = 30;
                    var hb = CoverageMath.BuildProfile(heli, tier0, 0, 2e-14, null, 72, nb);
                    var heli2 = Clone(heli); heli2.AntennaM = 33;
                    if (CoverageReuse.ShiftWhy(hb, heli, heli2, tier0, 2e-14, nb) != CoverageReuse.WhyRange) return "T-216 A3 helicopter 3 m";
                    // F-14 at 7600 m, +300 m: ~7 km of horizon (the 5 % rule of 0.30.8 allowed it)
                    var f14 = TestRadar(2, 15.0, 41.0, true); f14.PeakPowerKw = 1e6; f14.MaxRangeM = 800000;   // horizon-limited range
                    var fb = CoverageMath.BuildProfile(f14, tier0, 0, 2e-14, coast, 72, nb);
                    var f14b = Clone(f14); f14b.AntennaM = 7900;
                    if (CoverageReuse.ShiftWhy(fb, f14, f14b, tier0, 2e-14, nb) != CoverageReuse.WhyRange) return "T-216 A3 F-14 300 m";
                    // level flight, 0.3 bin, with terrain around: moves
                    var f14c = Clone(f14); MoveM(f14c, 90, 0.3 * fb.BinM);
                    if (!fb.Terrain) return "T-216 A3 F-14 profile has no terrain";
                    if (CoverageReuse.ShiftWhy(fb, f14, f14c, tier0, 2e-14, nb) != CoverageReuse.WhyNone) return "T-216 A3 F-14 0.3 bin";
                    var f14d = Clone(f14); MoveM(f14d, 90, 7000);
                    if (CoverageReuse.ShiftWhy(fb, f14, f14d, tier0, 2e-14, nb) != CoverageReuse.WhyDist) return "T-216 A3 F-14 7 km over land";
                    var f14e = Clone(f14); f14e.Emcon = true;
                    if (CoverageReuse.ShiftWhy(fb, f14, f14e, tier0, 2e-14, nb) != CoverageReuse.WhyState) return "T-216 A3 state";
                    var sect = TestRadar(3, 15.0, 41.0, false); sect.FullCircle = false; sect.AxisDeg = 10; sect.HArcMin = -60; sect.HArcMax = 60;
                    var sb = CoverageMath.BuildProfile(sect, tier0, 0, 2e-14, null, 72, nb);
                    var sect2 = Clone(sect); sect2.FullCircle = false; sect2.AxisDeg = 25; sect2.HArcMin = -60; sect2.HArcMax = 60;
                    if (CoverageReuse.ShiftWhy(sb, sect, sect2, tier0, 2e-14, nb) != CoverageReuse.WhyAxis) return "T-216 A3 axis";
                    // ship in the open sea, 300 m
                    var ship = TestRadar(4, 14.0, 40.0, false);
                    var shb = CoverageMath.BuildProfile(ship, tiers[2], 2, 2e-14, null, 72, nb);
                    var ship2 = Clone(ship); MoveM(ship2, 45, 300);
                    if (CoverageReuse.ShiftWhy(shb, ship, ship2, tiers[2], 2e-14, nb) != CoverageReuse.WhyNone) return "T-216 A3 ship 300 m";
                    // the counters of the worker add up to the rebuilds
                    var w = new CoverageWorker(null, null, null, ms => { }, null, false);
                    w.Tuning = new WorkerTuning();
                    var specs = new List<RadarSpec> { ship, f14, heli, sect };
                    w.BuildNow(specs, tiers, 2e-14, cfg, coast, true);
                    int sum0 = 0; foreach (var x in w.RebuildWhy) sum0 += x;
                    if (sum0 != w.ProfilesBuilt || w.RebuildWhy[CoverageReuse.WhyNew] != w.ProfilesBuilt) return "T-216 A3 first build why " + sum0 + "/" + w.ProfilesBuilt;
                    var next = new List<RadarSpec> { ship2, f14d, heli2, sect2 };
                    var lastF = w.BuildNow(next, tiers, 2e-14, cfg, coast, true);
                    int sum1 = 0; foreach (var x in w.RebuildWhy) sum1 += x;
                    if (sum1 != w.ProfilesBuilt || w.ProfilesBuilt == 0 || w.RebuildWhy[CoverageReuse.WhyNew] != 0) return "T-216 A3 why sum " + sum1 + "/" + w.ProfilesBuilt;
                    if (w.RebuildWhy[CoverageReuse.WhyDist] == 0 && w.RebuildWhy[CoverageReuse.WhyRange] == 0) return "T-216 A3 no reasons";
                    if (lastF == null || lastF.RebuildWhy == null || lastF.ProfilesBuilt != w.ProfilesBuilt || lastF.ProfilesShifted != w.ProfilesShifted) return "T-320 frame counters";
                    for (int k = 0; k < 6; k++) if (lastF.RebuildWhy[k] != w.RebuildWhy[k]) return "T-320 frame why " + k;
                    if (lastF.RebuildWhy[CoverageReuse.WhyNew] != 0) return "T-320 frame why new";
                    // T-320: Emcon does not gate emission; an airborne radar with Emcon=true is in the coverage, a radar that is off is not (ShowOffRadars=false)
                    var cfg320 = new CoverageConfig { Rays = 72, Bins = 48, Jamming = false, ShowOffRadars = false };
                    var ship320 = TestRadar(1, 15.0, 42.0, false);
                    var air320 = TestRadar(2, 15.5, 42.5, true); air320.Emcon = true; air320.AntennaM = 6000;
                    var w320 = new CoverageWorker(null, null, null, ms => { }, null, false);
                    w320.Tuning = new WorkerTuning();
                    var f320 = w320.BuildNow(new List<RadarSpec> { ship320, air320 }, tiers, 2e-14, cfg320, null, false);
                    if (f320.Error != null || f320.Radars != 2) return "T-320 radars " + f320.Radars + " " + f320.Error;
                    if (!f320.Profiles.Exists(p => p.OwnerUid == air320.OwnerUid && p.Active)) return "T-320 airborne Emcon=true not in coverage";
                    air320.On = false;
                    var f320b = w320.BuildNow(new List<RadarSpec> { ship320, air320 }, tiers, 2e-14, cfg320, null, false);
                    if (f320b.Profiles.Exists(p => p.OwnerUid == air320.OwnerUid)) return "T-320 radar off still drawn";
                }

                // ---- T-216 M1: honest GC counters
                {
                    int gc = 0;
                    var st = new CoverageStats();
                    st.GcCount = () => gc;
                    st.Due(0);
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 6; i++) specs.Add(TestRadar(i, 15.0 + i * 0.4, 42.0, false));
                    var heavy = new CoverageConfig { Rays = 72, Bins = 48, Jamming = false };
                    var w = new CoverageWorker(st, null, () => gc, ms => gc += 3, null, false);   // the "game" collects only while the worker sleeps
                    w.Pacer = new WorkerPacer(WorkerPacer.MaxDuty, 0);
                    w.Tuning = new WorkerTuning();
                    w.BuildNow(specs, tiers, 2e-14, heavy, null, true);
                    var moved = new List<RadarSpec>();
                    foreach (var x in specs) { var c = Clone(x); c.Emcon = true; moved.Add(c); }
                    int gcBefore = gc;
                    w.BuildNow(moved, tiers, 2e-14, heavy, null, true);
                    if (gc == gcBefore) return "T-216 M1: the worker did not sleep";
                    string l0 = st.TakeLine(60, "x", 6);
                    if (!l0.Contains("gc_in_build=0 ")) return "T-216 M1 sleep GC counted: " + l0;
                    // a collection inside the terrain delegate belongs to the build
                    bool bumped = false;
                    Func<double, double, double> bump = (la, lo) => { if (!bumped) { bumped = true; gc++; } return -10.0; };
                    w.BuildNow(specs, tiers, 2e-14, heavy, bump, true);
                    string l1 = st.TakeLine(120, "x", 6);
                    if (!l1.Contains("gc_in_build=1 ")) return "T-216 M1 build GC not counted: " + l1;
                    // gc_build_exp = background collections expected during the busy time
                    var s2 = new CoverageStats();
                    int g2 = 0; s2.GcCount = () => g2;
                    s2.Due(0);
                    g2 = 20; s2.AddBuild(6000, 6500, 0);
                    string l2 = s2.TakeLine(60, "x", 1);
                    if (!l2.Contains(" gc_build_exp=2 ") || !l2.Contains(" gc_per_min=20 ")) return "T-216 M1 gc_build_exp: " + l2;
                    s2.AddProfiles(5, 3, 90, new[] { 0, 1, 2, 3, 4, 5 });
                    s2.AddTerrain(100, 300, 100, 25, 262144, 1048576);
                    string l3 = s2.TakeLine(120, "x", 1);
                    string[] want = { "prof_built=5 ", "prof_shift=3 ", "prof_reuse=90 ", "why_new=5 ", "why_state=1 ", "why_range=2 ", "why_axis=3 ", "why_dist=4 ", "terrain_nan_pct=25 ", "memo_fill_pct=25" };
                    foreach (var x in want) if (!l3.Contains(x)) return "T-216 M1 stats field " + x + ": " + l3;
                }

                // ---- T-216 M2: the ray buffer is reused
                {
                    var coast = new Func<double, double, double>((la, lo) => lo > 42.2 ? 100.0 : -10.0);
                    var r = TestRadar(1, 15.0, 42.0, false);
                    var scratch = new double[48];
                    var p0 = CoverageMath.BuildProfile(r, tiers[2], 2, 2e-14, coast, 72, 48);
                    var p1 = CoverageMath.BuildProfile(r, tiers[2], 2, 2e-14, coast, 72, 48, null, scratch);
                    var p2 = CoverageMath.BuildProfile(r, tiers[2], 2, 2e-14, coast, 72, 48, null, new double[8]);   // too short: ignored
                    if (!p0.Terrain || !SameBits(p0, p1) || !SameBits(p0, p2)) return "T-216 M2 bits";
                    long a0 = AllocBytes();
                    CoverageMath.BuildProfile(r, tiers[2], 2, 2e-14, coast, 72, 48);
                    long a1 = AllocBytes();
                    CoverageMath.BuildProfile(r, tiers[2], 2, 2e-14, coast, 72, 48, null, scratch);
                    long a2 = AllocBytes();
                    if (a0 >= 0 && (a1 - a0) - (a2 - a1) < 48 * 8) return "T-216 M2 alloc saved " + ((a1 - a0) - (a2 - a1));
                }

                // ---- A4: the found mechanism (hammering the game's shared lock), scenario "QA 01"
                {
                    string r = Qa01Scenario(tiers);
                    if (r != null) return r;
                }

                // ---- A5: pacer, worker sleeps between profiles
                {
                    var p = new WorkerPacer();
                    if (p.AfterWork(30) < 170) return "pacer 30 ms -> " + p.AfterWork(0);
                    if (p.AfterWork(5) != 0) return "pacer short slice";
                    var p2 = new WorkerPacer();
                    double busy = 0, slept = 0;
                    var rnd = new Random(7);
                    for (int i = 0; i < 50; i++)
                    {
                        double b = 1 + rnd.NextDouble() * 60;
                        busy += b; slept += p2.AfterWork(b);
                    }
                    if (busy / (busy + slept) > WorkerPacer.MaxDuty + 0.01) return "pacer duty " + busy / (busy + slept);
                    if (Math.Abs(p2.BusyMs - busy) > 1e-6) return "pacer BusyMs";
                    var sleeps = new List<int>();
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 6; i++) specs.Add(TestRadar(i, 15.0 + i * 0.4, 42.0, false));
                    var heavy = new CoverageConfig { Rays = 360, Bins = 128, Jamming = false };
                    var warm = new CoverageWorker(null, null, null, ms => sleeps.Add(ms), null, false);
                    warm.Pacer = new WorkerPacer(WorkerPacer.MaxDuty, 0);
                    warm.BuildNow(specs, tiers, 2e-14, heavy, null, true);
                    // burst: nothing cached yet, no sleeps
                    if (sleeps.Count != 0) return "burst slept " + sleeps.Count;
                    var moved = new List<RadarSpec>();
                    foreach (var s in specs) { var c = Clone(s); c.Emcon = true; moved.Add(c); }
                    warm.BuildNow(moved, tiers, 2e-14, heavy, null, true);
                    if (sleeps.Count == 0) return "worker never slept after the burst build";
                    var st = new CoverageStats();
                    st.AddBuild(100, 700, 2); st.AddTerrain(50, 150, 50);
                    string line = st.TakeLine(60, "x", 1);
                    if (!line.Contains("worker_cpu_pct=0.2 ") || !line.Contains("build_wall_ms_avg=700") || !line.Contains("gc_in_build=2") || !line.Contains("memo_hit_pct=75") || !line.Contains("jam_gc=0") || !line.Contains("terrain_calls_per_s=0.8") || !line.Contains("gc_per_min=0")) return "stats line: " + line;
                }

                // ---- A6: probe calls per build
                {
                    var specs = new List<RadarSpec>();
                    for (int i = 0; i < 24; i++) specs.Add(TestRadar(i, 15.0 + i * 0.05, 42.0, false));
                    var coast = new Func<double, double, double>((la, lo) => lo > 42.2 ? 100.0 : -10.0);
                    var log = new ProbeLog();
                    var w = new CoverageWorker(null, log, null, ms => { }, null, false);
                    var tcfg = new CoverageConfig { Jamming = false };
                    w.BuildNow(specs, tiers, 2e-14, tcfg, coast, true);
                    if (w.ProbeCalls > 2000 || w.ProbeCalls < 10) return "probe calls " + w.ProbeCalls;
                    if (!log.SawTerrain) return "probe saw no terrain phase";
                    if (log.Terrain <= 0) return "probe terrain calls total " + log.Terrain;
                }

                // ---- T-225 A3: the broker on a fake clock
                {
                    string br = TerrainBroker.SelfTest();
                    if (br != null) return br;
                }

                // ---- T-225 A4: the worker only talks to the broker; the "main" thread (this one) answers
                {
                    string r = BrokerScenarios(tiers, cfg, island);
                    if (r != null) return r;
                }

                return null;
            }
            catch (Exception e) { return "coverageworker selftest exception: " + e; }
        }

        private static bool WaitUntil(Func<bool> cond, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(2);
            return cond();
        }

        /// <summary>Serves the broker on the calling thread (the stand-in of the main thread) until the worker has a frame; null on timeout.</summary>
        private static CoverageFrame ServeUntilFrame(CoverageWorker w, Func<double, double, double> game, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CoverageFrame f;
            double spent;
            while ((f = w.TakeResult()) == null && sw.ElapsedMilliseconds < timeoutMs)
            {
                if (w.Broker.Service(game, out spent) == 0) Thread.Sleep(2);
            }
            return f;
        }

        /// <summary>T-225 A4: equality with the direct source, threads, open sea, repeat, Dispose, replacement, Cancel, phase.</summary>
        private static string BrokerScenarios(List<CoverageTier> tiers, CoverageConfig cfg, Func<double, double, double> island)
        {
            int mainTid = Thread.CurrentThread.ManagedThreadId;
            int wrongThread = 0, calls = 0;
            Func<double, double, double> game = (la, lo) => { calls++; if (Thread.CurrentThread.ManagedThreadId != mainTid) wrongThread++; return island(la, lo); };
            var specs = new List<RadarSpec>();
            for (int i = 0; i < 8; i++) specs.Add(TestRadar(i, 15.0 + i * 0.03, 42.0 + i * 0.03, i == 7));
            var tune = new WorkerTuning { MemoCapacityPow2 = 1 << 20 };

            // 1-2, 4: equal bits; every game call on the main thread; the repeat asks nothing
            var direct = new CoverageWorker(null, null, null, ms => { }, null, false) { Tuning = tune };
            var fd = direct.BuildNow(specs, tiers, 2e-14, cfg, island, true);
            if (fd.Error != null || direct.TerrainLate != 0 || direct.FetchRounds != 2) return "T-225 direct: " + fd.Error + " late " + direct.TerrainLate + " rounds " + direct.FetchRounds;
            var log = new ProbeLog();
            var broker = new TerrainBroker();
            using (var w = new CoverageWorker(null, log, null, null, null, true, broker) { Tuning = tune })
            {
                w.Submit(specs, tiers, 2e-14, cfg, true);
                var fb = ServeUntilFrame(w, game, 20000);
                if (fb == null) return "T-225 broker build timed out";
                if (fb.Error != null) return "T-225 broker build: " + fb.Error;
                if (fb.Profiles.Count != fd.Profiles.Count) return "T-225 profile count";
                for (int k = 0; k < fd.Profiles.Count; k++) if (!SameBits(fd.Profiles[k], fb.Profiles[k])) return "T-225 bits differ at profile " + k;
                if (w.TerrainLate != 0 || w.FetchRounds < 1 || w.FetchRounds > 2) return "T-225 late " + w.TerrainLate + " rounds " + w.FetchRounds;
                if (wrongThread != 0 || calls == 0 || broker.Points != calls) return "T-225 game calls: wrong thread " + wrongThread + ", calls " + calls + ", points " + broker.Points;
                if (!log.SawWait) return "T-225 the probe never saw TerrainWait";
                long req = broker.Requests;
                w.Submit(specs, tiers, 2e-14, cfg, true);
                if (ServeUntilFrame(w, game, 20000) == null) return "T-225 repeat timed out";
                if (broker.Requests != req || w.FetchRounds != 0) return "T-225 repeat asked the broker: " + (broker.Requests - req) + " rounds " + w.FetchRounds;
            }

            // 3: open sea = one round, the second asks nothing
            {
                var sea = new Func<double, double, double>((la, lo) => -10.0);
                var wsea = new CoverageWorker(null, null, null, ms => { }, null, false);   // default tuning: the 2^21 memo
                var fs = wsea.BuildNow(new List<RadarSpec> { specs[0], specs[1] }, tiers, 2e-14, cfg, sea, true);
                if (fs.Error != null || wsea.FetchRounds != 1) return "T-225 open sea rounds " + wsea.FetchRounds;
                if (wsea.Memo == null || wsea.Memo.Capacity != (1 << 21)) return "T-225 memo capacity after the first build";
            }

            // 5-7: Dispose, replacement and Cancel while the worker waits (nobody serves)
            var big = new List<RadarSpec>();
            for (int i = 0; i < 4; i++) big.Add(TestRadar(i, 15.0 + i * 0.03, 42.0 + i * 0.03, false));
            var one = new List<RadarSpec> { TestRadar(40, 14.0, 41.0, false) };
            {
                var b5 = new TerrainBroker();
                var w5 = new CoverageWorker(null, null, null, null, null, true, b5) { Tuning = tune };
                w5.Submit(big, tiers, 2e-14, cfg, true);
                if (!WaitUntil(() => b5.Pending, 5000)) return "T-225 Dispose: the worker never posted";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ended = w5.Dispose(500);
                if (!ended || sw.ElapsedMilliseconds > TerrainBroker.WaitTickMs + 300) return "T-225 Dispose while waiting: " + ended + " after " + sw.ElapsedMilliseconds + " ms";
                if (w5.TakeResult() != null) return "T-225 Dispose: a frame was published";
            }
            for (int rep6 = 0; rep6 < 20; rep6++)   // T-232: 20 times, so that the race cannot pass by luck
            {
                var b6 = new TerrainBroker();
                var w6 = new CoverageWorker(null, null, null, null, null, true, b6) { Tuning = tune };
                w6.Submit(big, tiers, 2e-14, cfg, true);
                if (!WaitUntil(() => b6.Pending, 5000)) return "T-225 replace: the worker never posted";
                w6.Submit(one, tiers, 2e-14, cfg, true);
                // T-232: the worst order, always: the old request is answered in full before the worker notices the replacement
                // (it used to happen only when the worker thread was slow to wake, e.g. under -n 8)
                double spent6;
                int guard6 = 0;
                while (b6.Pending && guard6++ < 1000) b6.Service(island, out spent6);
                var f6 = ServeUntilFrame(w6, island, 20000);
                bool ok = f6 != null && f6.Error == null && f6.Radars == 1;
                if (!ok) return "T-225 replace #" + rep6 + ": frame " + (f6 == null ? "none" : "radars " + f6.Radars + " " + f6.Error);
                if (w6.TakeResult() != null) return "T-225 replace #" + rep6 + ": a second frame";
                w6.Dispose();
            }
            {
                var b7 = new TerrainBroker();
                var w7 = new CoverageWorker(null, null, null, null, null, true, b7) { Tuning = tune };
                w7.Submit(big, tiers, 2e-14, cfg, true);
                if (!WaitUntil(() => b7.Pending, 5000)) return "T-225 Cancel: the worker never posted";
                b7.Cancel();
                Thread.Sleep(250);
                if (b7.Pending || w7.TakeResult() != null) return "T-225 Cancel: the build went on or published";
                b7.Arm();
                w7.Submit(one, tiers, 2e-14, cfg, true);
                var f7 = ServeUntilFrame(w7, island, 20000);
                if (f7 == null || f7.Error != null || f7.Radars != 1) return "T-225 Cancel: the next Submit did not work";
                w7.Dispose();
            }
            return null;
        }

        private static long AllocBytes()
        {
#if NET8_0_OR_GREATER
            return GC.GetAllocatedBytesForCurrentThread();
#else
            return -1;
#endif
        }

        private static void SpinMicros(double us)
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(us * System.Diagnostics.Stopwatch.Frequency / 1e6);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
        }

        /// <summary>
        /// QA 01 like load: 24 radars (3 near a coast), 4 tiers, 360 x 128, 6 builds with movement. The game costs 1 us per call (SpinWait); this thread plays the main thread and
        /// serves the broker back to back. T-225: the main-thread time per Service call stays within the frame budget.
        /// </summary>
        private static string Qa01Scenario(List<CoverageTier> tiers)
        {
            var cfg = new CoverageConfig { Rays = 360, Bins = 128, Jamming = false };
            long oldCalls = 0;
            bool countOld = false;
            Func<double, double, double> game = (la, lo) =>
            {
                SpinMicros(1.0);
                if (countOld) oldCalls++;
                return (lo > 43.0 && la > 14.5 && la < 15.5) ? 120.0 + 200.0 * Math.Sin(la * 300.0) : -10.0;
            };
            var specs = new List<RadarSpec>();
            for (int i = 0; i < 24; i++)
            {
                bool air = i % 6 == 5;
                double lat = i < 3 ? 15.0 + i * 0.05 : 12.0 + i * 0.02;
                double lon = i < 3 ? 42.95 + i * 0.03 : 40.0 + (i % 7) * 0.05;
                specs.Add(TestRadar(i, lat, lon, air));
            }
            var broker = new TerrainBroker();
            var worker = new CoverageWorker(null, null, null, ms => { }, null, true, broker);
            long firstCalls = 0;
            int frames = 0;
            double maxFirst = 0, maxSteady = 0;
            // T-232 (flake): wall-clock frame times jump when the test process is starved (-n 8). Keep (spent, points, limit) and judge the overrun by the cost per point afterwards
            var samples = new List<double[]>();
            try
            {
                for (int b = 0; b < 6; b++)
                {
                    var cur = new List<RadarSpec>();
                    foreach (var s in specs)
                    {
                        var c = Clone(s);
                        MoveM(c, 90, (s.Airborne ? 300 * 0.514444 : 15 * 0.514444) * 10.0 * b);
                        cur.Add(c);
                    }
                    long c0 = broker.Points;
                    worker.Submit(cur, tiers, 2e-14, cfg, true);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    CoverageFrame f;
                    while ((f = worker.TakeResult()) == null && sw.ElapsedMilliseconds < 60000)
                    {
                        double spent;
                        int served = broker.Service(game, out spent);
                        if (served == 0) { Thread.Sleep(1); continue; }
                        frames++;
                        if (b == 0) { if (spent > maxFirst) maxFirst = spent; samples.Add(new[] { spent, served, TerrainBroker.BurstBudgetMs + 0.5 }); }
                        else { if (spent > maxSteady) maxSteady = spent; samples.Add(new[] { spent, served, TerrainBroker.BudgetMs + 0.5 }); }
                    }
                    if (f == null) return "qa01 build " + b + " timed out";
                    if (f.Error != null) return "qa01 error " + f.Error;
                    if (worker.TerrainLate != 0) return "qa01 terrain_late " + worker.TerrainLate;
                    if (b == 0) firstCalls = broker.Points - c0;
                }
            }
            finally { worker.Dispose(); }
            long newSteady = broker.Points - firstCalls;
            // the old path: build every profile again at each step (MoveThresholdM = 1 m) through the 200 000 point memo
            countOld = true;
            var oldSampler = CoverageMath.CachedSampler(game, 0.0005);
            long oldFirst = 0;
            for (int b = 0; b < 6; b++)
            {
                foreach (var s in specs)
                {
                    var c = Clone(s);
                    MoveM(c, 90, (s.Airborne ? 300 * 0.514444 : 15 * 0.514444) * 10.0 * b);
                    for (int ti = 0; ti < tiers.Count; ti++) CoverageMath.BuildProfile(c, tiers[ti], ti, 2e-14, oldSampler, cfg.Rays, cfg.Bins);
                }
                if (b == 0) oldFirst = oldCalls;
            }
            if (oldCalls <= 0 || oldFirst <= 0) return "qa01 old path made no calls";
            long oldSteady = oldCalls - oldFirst;
            // T-216 measured: first build 117 k game calls, builds 2-6: 2.3 k (0.1 % of the old path); T-225 adds the full open-sea check of the coastal profiles (576 points instead of the first land)
            if ((double)firstCalls / oldFirst > Qa01MaxFirstRatio) return "qa01 first build game calls " + firstCalls + " vs old " + oldFirst;
            if ((double)newSteady / oldSteady > Qa01MaxCallRatio) return "qa01 later builds: game calls " + newSteady + " vs old " + oldSteady + " (" + Math.Round(100.0 * newSteady / oldSteady, 1) + " %)";
            // a frame is over the budget when it is long AND each point cost about the usual: a frame stretched by preemption (CPU starvation) has an inflated cost per point
            var unit = new List<double>();
            foreach (var sm in samples) unit.Add(sm[0] / sm[1]);
            unit.Sort();
            double medUnit = unit.Count == 0 ? 0 : unit[unit.Count / 2];
            // T-277: robust statistic instead of an outlier count: p95 of (frame time / budget) over the frames not stretched by preemption; wall-time spikes under load (<= 5 % of frames) cannot fail it, a really slow worker (most frames over) does
            var ratio = new List<double>();
            int over = 0;
            foreach (var sm in samples)
            {
                if (sm[0] / sm[1] > 2.0 * medUnit) continue;
                ratio.Add(sm[0] / sm[2]);
                if (sm[0] > sm[2]) over++;
            }
            ratio.Sort();
            double p95 = ratio.Count == 0 ? 0 : ratio[(int)Math.Floor(0.95 * (ratio.Count - 1))];
            if (frames == 0 || p95 > 1.0) return "qa01 main thread: p95 " + Math.Round(p95, 2) + " of the budget, " + over + " of " + frames + " service frames over the budget (max " + Math.Round(maxFirst, 2) + " / " + Math.Round(maxSteady, 2) + " ms)";
            return null;
        }

        // T-216 A4: measured 0.1 % (steady) and 28 % (first build: the geographic cache serves the tiers from the same cells); limits ~5x and ~2x of that
        internal const double Qa01MaxCallRatio = 0.005, Qa01MaxFirstRatio = 0.5;
    }
}
