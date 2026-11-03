using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>Reference target of one coverage tier: height ASL (m), RCS (m2) and which target kinds it stands for.</summary>
    internal sealed class CoverageTier
    {
        public double HeightM, RcsM2;
        public bool Air, Surface;
    }

    /// <summary>Snapshot of one search radar, SI units (m, degrees), filled on the main thread. T-066.</summary>
    internal sealed class RadarSpec
    {
        public int OwnerUid;
        public string OwnerName, Name, Type;            // Type: Search|DirectedSearch
        public bool Air, Surface, Airborne, DataLink, On, Inoperable, Emcon;
        public double Lat, Lon, AntennaM;                // antenna ASL (SensorPosition.y in m)
        public double MinRangeM, MaxRangeM, MinAltM, MaxAltM;   // MinAltM/MaxAltM < 0 = not set
        public double PeakPowerKw, GainFactor, WaveLengthM, LookDownMult = 1.0, LookDownRangeM;   // LookDownRangeM <= 0 -> MaxRange * mult (as the game)
        public bool FullCircle, Cone;
        public double AxisDeg, HArcMin, HArcMax, VArcMin, VArcMax;
        public int SeaState;
        public double SeaWaveM, TerrainUnderAntennaM, VisibleHeightM;
        /// <summary>T-352: false = the flat world of stable 261021 (1 deg = 60 nm on both axes), true = the beta globe. Set on the main thread (Coverage.cs) from AswMath.WorldGlobe; the worker only reads this snapshot.</summary>
        public bool Globe;
        /// <summary>T-182 (ADR-0081): jamming input of this radar (null = no jam data / Jamming=false). NOT part of RadarStateChanged / Moved / SpecChanged: the profile cache stays untouched.</summary>
        public RadarJam Jam;
    }

    /// <summary>T-182: SI snapshot of one jammer KNOWN to the player (main thread, ADR-0081 sections 4-5). Uid = Vehicle.Id of the PLAYER'S track (never the unit's own uid).</summary>
    internal sealed class JamSource
    {
        public int Uid;
        public double Lat, Lon, AltM = double.NaN;        // estimate of the track; AltM NaN = height not measured -> 2D distance
        public double PeakKw, GainLin, Effectiveness;
        public int[] Frequencies = new int[0];            // (int)Globals.Frequency
        public int Sensors;                               // T-206: Vehicle.DetectingSensors of the track (SensorTypeSet flags)
        public double? SigmaM;                            // T-206: PositionEstimate.Item2.MajorStdDev, metres
        public bool RangeApprox;                          // T-225: range class is RangeApprox (ESM bearings only): the source cuts the zone, the label gets "≈"
        public double BearingDeg, DistM;                  // from the radar antenna to the estimate (CoverageJamMath.Locate)

        public JamSource Clone() { return (JamSource)MemberwiseClone(); }
    }

    /// <summary>T-182: jamming input of one radar. State: none|known|partial|unknown (CoverageJamMath).</summary>
    internal sealed class RadarJam
    {
        public string State = "none";
        public double[] BearingsDeg = new double[0];                     // game bearings of the owner (latched union)
        public List<JamSource> Known = new List<JamSource>();            // known jammers of the owner (all bands), located for THIS radar
        public int Frequency;
        public double JamResistance, CrewFactor = 1.0, J2S = 30.0;
        public int SkipIdent, SkipParams, SkipFix, SkipRange, ApproxRange;            // T-206: player tracks of jammers that did not become known, by reason (dev diagnostics only)
    }

    internal sealed class TierLimit
    {
        public double RangeM;
        public string Limit;    // horizon|equation|max_range|lookdown|altitude|role|off
    }

    /// <summary>Visibility of one radar for one tier on a polar grid: Rays azimuths (0 = north, clockwise) x Bins range cells.</summary>
    internal sealed class RayProfile
    {
        public double Lat, Lon, RangeM, BinM;
        public int Rays, Bins, Tier;
        public bool Active, Airborne;
        /// <summary>T-352: the frame of the rays (= RadarSpec.Globe at the build): false = flat degrees (stable), true = the sphere (beta).</summary>
        public bool Globe;
        /// <summary>T-202: the terrain shadow was sampled for this profile (false = open sea or no terrain: the profile can be moved to a new centre as is).</summary>
        public bool Terrain;
        public ulong[] Visible;                         // Rays * ceil(Bins/64) words
        public double ShadowPct;
        public int OwnerUid;
        public string Radar;
    }

    internal struct GeoBox { public double LatMin, LatMax, LonMin, LonMax; }

    /// <summary>Outline segment in geographic coordinates. Style: 0 active, 1 off/EMCON (grey dashed), 2 airborne, 3 jammed: outline without jamming (dimmed dashed, T-182).</summary>
    internal struct GeoSeg { public double Lat1, Lon1, Lat2, Lon2; public byte Tier, Style; }

    /// <summary>x = A*lat + B*lon + C; y = D*lat + E*lon + F.</summary>
    internal struct Affine2
    {
        public double A, B, C, D, E, F;
        public void Apply(double lat, double lon, out double x, out double y)
        {
            x = A * lat + B * lon + C;
            y = D * lat + E * lon + F;
        }
    }

    internal enum CoverageTrigger { None, Force, State, Motion }

    /// <summary>
    /// Recalculation policy (T-090, ADR-0034): radar state changes run at once (>= MinRecomputeGapS after the last run), movement only
    /// once per RecomputeSeconds, a still fleet with unchanged radars never. The caller polls state every StateCheckSeconds.
    /// </summary>
    internal sealed class CoverageSchedule
    {
        public double RecomputeSeconds { get; private set; }
        public double StateCheckSeconds { get; private set; }
        private double _nextPoll = double.NegativeInfinity, _motionAt = double.NegativeInfinity, _lastRun = double.NegativeInfinity, _motionDueNow = double.NaN;
        private bool _pendingForce, _pendingState;

        public CoverageSchedule(double recomputeSeconds, double stateCheckSeconds)
        {
            RecomputeSeconds = CoverageMath.ClampRecompute(recomputeSeconds);
            StateCheckSeconds = CoverageMath.ClampStateCheck(stateCheckSeconds);
        }

        /// <summary>True once per StateCheckSeconds; advances the poll timer.</summary>
        public bool PollDue(double now)
        {
            if (now < _nextPoll) return false;
            _nextPoll = now + StateCheckSeconds;
            return true;
        }

        /// <summary>True when RecomputeSeconds passed since the last motion check or run; advances the motion timer.</summary>
        public bool MotionDue(double now)
        {
            if (double.IsNegativeInfinity(_motionAt)) { _motionAt = now + RecomputeSeconds; return false; }
            if (now < _motionAt) return false;
            _motionAt = now + RecomputeSeconds;
            _motionDueNow = now;
            return true;
        }

        /// <summary>Decides one submission. A force/state request blocked by the minimum gap is remembered and runs later.</summary>
        public CoverageTrigger Decide(double now, bool force, bool stateChanged, bool moved)
        {
            if (force) _pendingForce = true;
            if (stateChanged) _pendingState = true;
            if (_pendingForce || _pendingState)
            {
                if (now - _lastRun < CoverageMath.MinRecomputeGapS - 1e-9) return CoverageTrigger.None;
                return _pendingForce ? CoverageTrigger.Force : CoverageTrigger.State;
            }
            if (moved && _motionDueNow == now) return CoverageTrigger.Motion;
            return CoverageTrigger.None;
        }

        public void Submitted(double now, CoverageTrigger t)
        {
            if (t == CoverageTrigger.None) return;
            _lastRun = now;
            _motionAt = now + RecomputeSeconds;
            _pendingForce = _pendingState = false;
        }

        /// <summary>Overlay off / mission left: forget everything; the next Decide must be forced by the caller.</summary>
        public void Reset()
        {
            _nextPoll = _motionAt = _lastRun = double.NegativeInfinity;
            _motionDueNow = double.NaN;
            _pendingForce = _pendingState = false;
        }
    }

    internal sealed class LegendLine { public string Text; public int Swatch = -1; /* tier 0..3, 4 = grey dashed, 5 = dimmed dashed (jamming, T-182), -1 none */ public bool Bold; public uint Argb; /* T-171: not 0 = the sample has this colour (Swatch >= 0 reserves its place) */ public bool Dashed; }

    internal struct LegendBox { public bool Visible; public double X, Y, W, H, FontPx, LineH, Pad; }

    /// <summary>Thread-safe accumulator of the "coverage stats" log line (worker adds builds, the main thread the rest).</summary>
    internal sealed class CoverageStats
    {
        private const int HistMs = 200;
        private readonly object _lock = new object();
        private double _start = double.NaN;
        private int _builds, _polls, _snaps, _redraws, _frames, _jams, _jamRadars, _gcInBuild, _jamGc, _gcStart;
        private long _terrCalls, _terrHits, _terrMisses, _terrNan;
        private int _profBuilt, _profShift, _profReuse, _memoCount, _memoCap;
        private long _brokerPoints, _lateMisses, _offMain;
        private int _brokerFrames, _fetchRounds;
        private double _brokerSumMs, _brokerMaxMs, _brokerWaitMs;
        private readonly int[] _why = new int[6];
        private double _wallSum;
        private Func<int> _gcCount;
        private bool _gcBaseSet;
        private bool _firstSeen;
        private double _firstWall, _firstBusy, _firstWait, _firstPace;
        /// <summary>T-202: GC.CollectionCount(0) reader for gc_per_min (null = 0). T-232: set after the window has started and before a base was taken, it takes the base at once (the first window no longer counts from 0).</summary>
        public Func<int> GcCount
        {
            get { lock (_lock) return _gcCount; }
            set
            {
                lock (_lock)
                {
                    _gcCount = value;
                    if (value != null && !double.IsNaN(_start) && !_gcBaseSet) { try { _gcStart = value(); _gcBaseSet = true; } catch { } }
                }
            }
        }
        private double _buildSum, _buildMax, _pollSum, _pollMax, _snapSum, _snapMax, _redrawSum, _frameSum, _jamSum, _jamMax;
        private readonly int[] _hist = new int[HistMs + 1];

        public void AddBuild(double ms) { AddBuild(ms, ms, 0); }
        /// <summary>T-202: busyMs = work time without sleeps (worker_cpu_pct), wallMs = start to end, gcDelta = GC.CollectionCount(0) change during the work of the build (T-216: the sleeps of the pacer are excluded by the caller).</summary>
        public void AddBuild(double busyMs, double wallMs, int gcDelta, bool first = false, double waitMs = 0, double paceMs = 0)
        {
            lock (_lock)
            {
                _builds++; _buildSum += busyMs; _wallSum += wallMs; if (busyMs > _buildMax) _buildMax = busyMs; if (gcDelta > 0) _gcInBuild += gcDelta;
                // T-232: the first build of the layer (nothing cached): its wall time split into work, terrain wait and pacer sleep
                if (first) { _firstSeen = true; _firstWall = wallMs; _firstBusy = busyMs; _firstWait = waitMs; _firstPace = paceMs; }
            }
        }
        /// <summary>T-202: game terrain calls of a build, memo hits and misses.</summary>
        public void AddTerrain(long calls, long hits, long misses) { lock (_lock) { _terrCalls += calls; _terrHits += hits; _terrMisses += misses; } }
        /// <summary>T-216: as above plus the game calls that returned NaN (no DEM chunk) and the memo fill (entries / slots).</summary>
        public void AddTerrain(long calls, long hits, long misses, long nanMisses, int memoCount, int memoCap)
        {
            lock (_lock) { _terrCalls += calls; _terrHits += hits; _terrMisses += misses; _terrNan += nanMisses; _memoCount = memoCount; _memoCap = memoCap; }
        }
        /// <summary>T-225: terrain served on the main thread in the window (points, frames, sum / max of ms per frame), the worker's wait for it, memo misses of the build pass (must stay 0) and terrain calls from a wrong thread (must stay 0).</summary>
        public void AddBroker(long points, int frames, double sumMs, double maxMs, double waitMs, long late, long offMain)
        {
            lock (_lock)
            {
                _brokerPoints += points; _brokerFrames += frames; _brokerSumMs += sumMs; if (maxMs > _brokerMaxMs) _brokerMaxMs = maxMs;
                _brokerWaitMs += waitMs; _lateMisses += late; _offMain += offMain;
            }
        }
        /// <summary>T-225: fetch rounds of the last build (0..2).</summary>
        public void SetFetchRounds(int rounds) { lock (_lock) _fetchRounds = rounds; }
        /// <summary>T-216: profiles of a build: rebuilt, moved to a new centre, taken as they are; why[] = rebuilds by reason (CoverageReuse.Why*, index 0 unused).</summary>
        public void AddProfiles(int built, int shifted, int reused, int[] why)
        {
            lock (_lock)
            {
                _profBuilt += built; _profShift += shifted; _profReuse += reused;
                if (why != null) for (int i = 0; i < _why.Length && i < why.Length; i++) _why[i] += why[i];
            }
        }
        public void AddPoll(double ms, bool gcDuring = false) { lock (_lock) { _polls++; _pollSum += ms; if (ms > _pollMax) _pollMax = ms; if (gcDuring) _jamGc++; } }
        public void AddSnapshot(double ms, bool gcDuring = false) { lock (_lock) { _snaps++; _snapSum += ms; if (ms > _snapMax) _snapMax = ms; if (gcDuring) _jamGc++; } }
        /// <summary>T-182: main-thread time of the jamming snapshot of one submission and the number of radars with a jamming state other than none.</summary>
        public void AddJam(double ms, int radars, bool gcDuring = false) { lock (_lock) { _jams++; _jamSum += ms; if (ms > _jamMax) _jamMax = ms; _jamRadars = radars; if (gcDuring) _jamGc++; } }
        public void AddRedraw(double ms) { lock (_lock) { _redraws++; _redrawSum += ms; } }
        public void AddFrame(double dtSec)
        {
            if (!(dtSec > 0) || double.IsInfinity(dtSec)) return;
            lock (_lock)
            {
                _frames++; _frameSum += dtSec;
                int b = (int)Math.Ceiling(dtSec * 1000.0 - 1e-6);
                _hist[Math.Max(0, Math.Min(HistMs, b))]++;
            }
        }

        /// <summary>First call starts the window; true once the window (60 s) is over.</summary>
        public bool Due(double now)
        {
            lock (_lock)
            {
                if (double.IsNaN(_start)) { _start = now; if (_gcCount != null) { try { _gcStart = _gcCount(); _gcBaseSet = true; } catch { } } return false; }
                return now - _start >= 60.0;
            }
        }

        private static string N(double v) { return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture); }

        /// <summary>Formats the line and resets the counters (the new window starts now).</summary>
        public string TakeLine(double now, string mode, int radars)
        {
            lock (_lock)
            {
                double window = double.IsNaN(_start) ? 60.0 : Math.Max(1.0, now - _start);
                double p95 = 0;
                if (_frames > 0)
                {
                    int need = (int)Math.Ceiling(0.95 * _frames), acc = 0;
                    for (int i = 0; i <= HistMs; i++) { acc += _hist[i]; if (acc >= need) { p95 = i; break; } }
                }
                string line = "coverage stats: window_s=" + N(window) + " mode=" + mode + " radars=" + radars
                    + " builds=" + _builds + " build_ms_avg=" + N(_builds == 0 ? 0 : _buildSum / _builds) + " build_ms_max=" + N(_buildMax)
                    + " worker_cpu_pct=" + N(_buildSum / (window * 1000.0) * 100.0)
                    + " poll_ms_avg=" + N(_polls == 0 ? 0 : _pollSum / _polls) + " poll_ms_max=" + N(_pollMax)
                    + " snapshot_ms_avg=" + N(_snaps == 0 ? 0 : _snapSum / _snaps) + " snapshot_ms_max=" + N(_snapMax)
                    + " jam_ms_avg=" + N(_jams == 0 ? 0 : _jamSum / _jams) + " jam_ms_max=" + N(_jamMax) + " jam_radars=" + _jamRadars
                    + " rt_redraws=" + _redraws + " rt_ms_avg=" + N(_redraws == 0 ? 0 : _redrawSum / _redraws)
                    + " fps_avg=" + N(_frameSum > 0 ? _frames / _frameSum : 0) + " frame_ms_p95=" + N(p95);
                int gcNow = 0;
                if (_gcCount != null) { try { gcNow = _gcCount(); } catch { } }
                int gcDelta = _gcCount == null || !_gcBaseSet ? 0 : Math.Max(0, gcNow - _gcStart);
                long lookups = _terrHits + _terrMisses;
                line += " build_wall_ms_avg=" + N(_builds == 0 ? 0 : _wallSum / _builds) + " gc_in_build=" + _gcInBuild + " gc_per_min=" + N(gcDelta * 60.0 / window) + " gc_build_exp=" + N(gcDelta * _buildSum / (window * 1000.0))
                    + " terrain_calls_per_s=" + N(_terrCalls / window) + " memo_hit_pct=" + N(lookups == 0 ? 0 : 100.0 * _terrHits / lookups) + " jam_gc=" + _jamGc
                    + " prof_built=" + _profBuilt + " prof_shift=" + _profShift + " prof_reuse=" + _profReuse
                    + " why_new=" + _why[5] + " why_state=" + _why[1] + " why_range=" + _why[2] + " why_axis=" + _why[3] + " why_dist=" + _why[4]
                    + " terrain_nan_pct=" + N(_terrCalls == 0 ? 0 : 100.0 * _terrNan / _terrCalls) + " memo_fill_pct=" + N(_memoCap == 0 ? 0 : 100.0 * _memoCount / _memoCap)
                    + " memo_cap=" + _memoCap + " terrain_main_ms_avg=" + N(_brokerFrames == 0 ? 0 : _brokerSumMs / _brokerFrames) + " terrain_main_ms_max=" + N(_brokerMaxMs)
                    + " terrain_main_pts=" + _brokerPoints + " terrain_wait_ms=" + N(_brokerWaitMs) + " terrain_late=" + _lateMisses + " terrain_offmain=" + _offMain + " fetch_rounds=" + _fetchRounds;
                if (_firstSeen)
                    line += " first_build_wall_ms=" + N(_firstWall) + " first_build_busy_ms=" + N(_firstBusy) + " first_build_wait_ms=" + N(_firstWait) + " first_build_pace_ms=" + N(_firstPace);
                _firstSeen = false; _firstWall = _firstBusy = _firstWait = _firstPace = 0;
                _gcStart = gcNow; _gcBaseSet = _gcCount != null;
                _start = now;
                _builds = _polls = _snaps = _redraws = _frames = _jams = 0;
                _gcInBuild = _jamGc = 0; _terrCalls = _terrHits = _terrMisses = _terrNan = 0; _wallSum = 0;
                _profBuilt = _profShift = _profReuse = 0; Array.Clear(_why, 0, _why.Length);
                _brokerPoints = _lateMisses = _offMain = 0; _brokerFrames = 0; _brokerSumMs = _brokerMaxMs = _brokerWaitMs = 0;
                _buildSum = _buildMax = _pollSum = _pollMax = _snapSum = _snapMax = _redrawSum = _frameSum = _jamSum = _jamMax = 0;
                Array.Clear(_hist, 0, _hist.Length);
                return line;
            }
        }
    }

    /// <summary>
    /// Pure radar-coverage math for T-066 (no Unity, no game access): game formulas (GetRangeInternal,
    /// Radar.IsDetectableTarget_Conditional, IsInLookDown, getRadarHorizon), polar visibility profiles with terrain
    /// shadows, raster/outline of the union, screen-projection helpers. Checked offline by SelfTest().
    /// </summary>
    internal static class CoverageMath
    {
        public const double MPerUnity = 67.200066, UnityPerM = 0.0148809375, MPerNm = 1852.0;
        public const string DefaultTiers = "15:1:air,150:1:air,3000:10:air,15:50:surface";
        public const int MaxTiers = 4;
        public const double EarthRadiusM = 6371008.8;
        private const double Rad = Math.PI / 180.0, Deg = 180.0 / Math.PI;

        // ---------------------------------------------------------------- parsing

        /// <summary>"15:1:air+surface,150:1:air" -> tiers (height m : RCS m2 : kinds). 1..4 tiers; null + error otherwise.</summary>
        public static List<CoverageTier> ParseTiers(string spec, out string error)
        {
            error = null;
            if (spec == null || spec.Trim().Length == 0) { error = "tiers: empty"; return null; }
            string[] parts = spec.Split(',');
            if (parts.Length < 1 || parts.Length > MaxTiers) { error = "tiers: 1.." + MaxTiers + " tiers expected"; return null; }
            var res = new List<CoverageTier>();
            foreach (string raw in parts)
            {
                string[] f = raw.Trim().Split(':');
                if (f.Length != 3) { error = "tiers: '" + raw.Trim() + "' is not height:rcs:kinds"; return null; }
                double h, s;
                if (!double.TryParse(f[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h) || double.IsNaN(h) || h < 0 || h > 50000)
                { error = "tiers: bad height '" + f[0].Trim() + "'"; return null; }
                if (!double.TryParse(f[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out s) || double.IsNaN(s) || s <= 0 || s > 1e6)
                { error = "tiers: bad rcs '" + f[1].Trim() + "'"; return null; }
                var t = new CoverageTier { HeightM = h, RcsM2 = s };
                foreach (string k in f[2].Split('+'))
                {
                    string kk = k.Trim().ToLowerInvariant();
                    if (kk == "air") t.Air = true;
                    else if (kk == "surface") t.Surface = true;
                    else { error = "tiers: unknown kind '" + k.Trim() + "'"; return null; }
                }
                if (!t.Air && !t.Surface) { error = "tiers: no kinds"; return null; }
                res.Add(t);
            }
            return res;
        }

        private static readonly string[] Modifiers = { "LeftAlt", "RightAlt", "LeftControl", "RightControl", "LeftShift", "RightShift" };

        /// <summary>"LeftAlt+R" -> ["LeftAlt","R"] (Unity KeyCode names; last = key, the others = modifiers). Null + error if bad.</summary>
        public static string[] ParseHotkey(string spec, out string error)
        {
            error = null;
            if (spec == null || spec.Trim().Length == 0) { error = "hotkey: empty"; return null; }
            string[] parts = spec.Split('+');
            if (parts.Length > 4) { error = "hotkey: too many parts"; return null; }
            var res = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0 || p.Length > 24) { error = "hotkey: empty or long part"; return null; }
                foreach (char c in p)
                    if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9')) { error = "hotkey: bad char in '" + p + "'"; return null; }
                bool mod = Array.IndexOf(Modifiers, p) >= 0;
                if (i < parts.Length - 1 && !mod) { error = "hotkey: '" + p + "' is not a modifier"; return null; }
                if (i == parts.Length - 1 && mod) { error = "hotkey: no key after modifiers"; return null; }
                res[i] = p;
            }
            return res;
        }

        // ---------------------------------------------------------------- game formulas

        /// <summary>RadarCalculator.getRadarHorizon in metres: heights are taken above terrainM, negatives clamp to 0; 4/3 Earth (visual: no 4/3).</summary>
        public static double HorizonM(double antennaM, double targetM, double terrainM, bool visual)
        {
            double ha = Math.Max(0.0, antennaM - terrainM) / 1000.0;
            double ht = Math.Max(0.0, targetM - terrainM) / 1000.0;
            double k = visual ? 12742.018 : 16989.357;
            return (Math.Sqrt(k * ha) + Math.Sqrt(k * ht)) * 1000.0;
        }

        /// <summary>RadarCalculator.BaseMaxDetectionRangeMeter. gainFactor is linear (10^(dB/10)).</summary>
        public static double EquationRangeM(double peakKw, double gainFactor, double waveM, double rcsM2, double minPowerKw)
        {
            double num = peakKw * 1000.0 * gainFactor * gainFactor * waveM * waveM * rcsM2;
            double den = minPowerKw * 1000.0 * Math.Pow(4.0 * Math.PI, 3.0);
            if (den <= 0 || num <= 0) return 0;
            return Math.Pow(num / den, 0.25);
        }

        /// <summary>SensorSystem.IsInLookDown verbatim (antenna/horizon in one unit, ratio is unit-free; target = visible altitude).</summary>
        public static bool InLookDown(double antennaM, double horizonM, double distM, double targetM)
        {
            if (!(horizonM > 0)) return false;
            double theta = Math.Atan(Math.Tan(antennaM / horizonM)) * Deg;
            double limit = 90.0 - theta;
            double fromDown = Math.Atan2(distM, antennaM - targetM) * Deg;   // angle between "down" and the vector to the target
            return fromDown + 1.5 < limit;
        }

        /// <summary>Ground distance below which InLookDown is true (0 = never). Closed form of InLookDown.</summary>
        private static double LookDownEdgeM(double antennaM, double horizonM, double targetM)
        {
            if (!(horizonM > 0)) return 0;
            double theta = Math.Atan(Math.Tan(antennaM / horizonM)) * Deg;
            double lim = 88.5 - theta;
            double a = antennaM - targetM;
            if (lim <= 0 || a <= 0 || lim >= 90) return 0;
            return a * Math.Tan(lim * Rad);
        }

        /// <summary>Utils.TerrainAltForHorizon: min(antenna terrain, cell terrain) when over land; negative (sea) -> wave height.</summary>
        private static double TerrainRefM(RadarSpec r, double cellTerrM)
        {
            double t = r.TerrainUnderAntennaM;
            if (t > 0 && !double.IsNaN(cellTerrM)) t = Math.Min(t, cellTerrM);
            if (t < 0) t = r.SeaWaveM;
            return t;
        }

        private static double SeaFactor(RadarSpec r, bool surfaceKind)
        {
            return surfaceKind && r.SeaState > 1 ? Math.Max(0.0, 1.0 - 0.05 * r.SeaState) : 1.0;
        }

        private static double CapRange(RadarSpec r) { return r.MaxRangeM > 0 ? r.MaxRangeM : double.MaxValue; }

        private static double LookDownCap(RadarSpec r) { return r.LookDownRangeM > 0 ? r.LookDownRangeM : CapRange(r) * r.LookDownMult; }

        /// <summary>Altitude gate of GetRangeInternal for an air target (MinAlt by height over clutter, MaxAlt by y).</summary>
        private static bool AltitudeOk(RadarSpec r, double heightM)
        {
            if (r.MinAltM >= 0 && heightM < r.MinAltM) return false;
            if (r.MaxAltM >= 0 && heightM > r.MaxAltM) return false;
            return true;
        }

        /// <summary>Which kinds of the tier this radar can use: bit0 air, bit1 surface. 0 = none. Sets altitudeBlocked if air was excluded by altitude.</summary>
        private static int UsableKinds(RadarSpec r, CoverageTier t, out bool altitudeBlocked)
        {
            altitudeBlocked = false;
            int k = 0;
            if (t.Air && r.Air)
            {
                if (AltitudeOk(r, t.HeightM)) k |= 1; else altitudeBlocked = true;
            }
            if (t.Surface && r.Surface) k |= 2;
            return k;
        }

        /// <summary>
        /// Maximum range of the radar against the tier target without terrain and arcs (as SensorSystemRadar.GetRange / the sensor window).
        /// The look-down multiplier applies; MinRange and the look-down range cap do not (they are per-cell in BuildProfile).
        /// </summary>
        public static TierLimit RangeLimit(RadarSpec r, CoverageTier t, double minPowerKw)
        {
            if (r.Inoperable) return new TierLimit { RangeM = 0, Limit = "off" };
            bool altBlocked;
            int kinds = UsableKinds(r, t, out altBlocked);
            if (kinds == 0) return new TierLimit { RangeM = 0, Limit = altBlocked ? "altitude" : "role" };
            double tgt = t.HeightM + r.VisibleHeightM;
            double horizon = HorizonM(r.AntennaM, tgt, TerrainRefM(r, double.NaN), false);
            TierLimit best = null;
            for (int kind = 0; kind < 2; kind++)
            {
                if ((kinds & (1 << kind)) == 0) continue;
                double eq = EquationRangeM(r.PeakPowerKw, r.GainFactor, r.WaveLengthM, t.RcsM2, minPowerKw) * SeaFactor(r, kind == 1);
                double cap = CapRange(r);
                double burn = Math.Min(eq, cap);
                double baseRange = Math.Min(burn, horizon);
                double range = baseRange;
                string limit = horizon < burn ? "horizon" : (eq <= cap ? "equation" : "max_range");
                if (r.LookDownMult < 1.0)
                {
                    double edge = LookDownEdgeM(r.AntennaM, horizon, tgt);
                    double ld = Math.Min(burn * r.LookDownMult, horizon);
                    if (!(baseRange > edge) && ld < baseRange) { range = ld; limit = "lookdown"; }
                }
                if (best == null || range > best.RangeM) best = new TierLimit { RangeM = range, Limit = limit };
            }
            return best;
        }

        // ---------------------------------------------------------------- geodesy

        /// <summary>Point at distance/bearing (deg, 0 = north, clockwise) from lat/lon on a sphere.</summary>
        public static void Destination(double lat, double lon, double bearingDeg, double distM, out double lat2, out double lon2)
        {
            double d = distM / EarthRadiusM, b = bearingDeg * Rad, p1 = lat * Rad, l1 = lon * Rad;
            double sinP2 = Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b);
            double p2 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sinP2)));
            double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * sinP2);
            lat2 = p2 * Deg;
            lon2 = ((l2 * Deg + 540.0) % 360.0) - 180.0;
        }

        /// <summary>T-352: the flat world of stable 261021: 1 deg = 60 nm on both axes (GeoPosition), in metres.</summary>
        public const double FlatMPerDeg = 60.0 * MPerNm;

        /// <summary>T-352: globe = the spherical Destination above (the same call); flat = the metric of the game (1 deg = FlatMPerDeg on both axes).</summary>
        public static void Destination(double lat, double lon, double bearingDeg, double distM, bool globe, out double lat2, out double lon2)
        {
            if (globe) { Destination(lat, lon, bearingDeg, distM, out lat2, out lon2); return; }
            double b = bearingDeg * Rad;
            lat2 = lat + distM * Math.Cos(b) / FlatMPerDeg;
            double l2 = lon + distM * Math.Sin(b) / FlatMPerDeg;
            lon2 = ((l2 + 540.0) % 360.0) - 180.0;
        }

        /// <summary>T-352: globe = the haversine BearingDistance below (the same call); flat = north/east in degrees * FlatMPerDeg.</summary>
        public static void BearingDistance(double lat1, double lon1, double lat2, double lon2, bool globe, out double bearingDeg, out double distM)
        {
            if (globe) { BearingDistance(lat1, lon1, lat2, lon2, out bearingDeg, out distM); return; }
            double n = (lat2 - lat1) * FlatMPerDeg, e = (lon2 - lon1) * FlatMPerDeg;
            distM = Math.Sqrt(n * n + e * e);
            bearingDeg = (Math.Atan2(e, n) * Deg + 360.0) % 360.0;
        }

        /// <summary>Initial bearing (0..360) and distance in metres between two points (haversine).</summary>
        public static void BearingDistance(double lat1, double lon1, double lat2, double lon2, out double bearingDeg, out double distM)
        {
            double p1 = lat1 * Rad, p2 = lat2 * Rad, dl = (lon2 - lon1) * Rad, dp = p2 - p1;
            double sdp = Math.Sin(dp / 2), sdl = Math.Sin(dl / 2);
            double a = sdp * sdp + Math.Cos(p1) * Math.Cos(p2) * sdl * sdl;
            distM = 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            bearingDeg = (Math.Atan2(y, x) * Deg + 360.0) % 360.0;
        }

        // ---------------------------------------------------------------- sector

        internal static double Wrap180(double a)
        {
            a %= 360.0;
            if (a > 180.0) a -= 360.0;
            else if (a < -180.0) a += 360.0;
            return a;
        }

        /// <summary>
        /// SensorSystem.IsObjectInFOV for a flat antenna axis: relative bearing positive to the right (clockwise); vertical angle =
        /// elevation of the target above the antenna. Cone (single ViewArcs number) also accepts the 3D angle to the axis.
        /// </summary>
        public static bool InSector(RadarSpec r, double bearingDeg, double distM, double targetM)
        {
            if (r.FullCircle) return true;
            if (r.VArcMin < -90 && r.VArcMax > 90 && r.HArcMin < -180 && r.HArcMax > 180) return true;
            double rel = Wrap180(bearingDeg - r.AxisDeg);
            double elev = Math.Atan2(targetM - r.AntennaM, Math.Max(distM, 1e-9)) * Deg;
            if (r.Cone)
            {
                double cosAng = Math.Cos(elev * Rad) * Math.Cos(rel * Rad);
                double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosAng))) * Deg;
                if (ang < r.HArcMax) return true;
            }
            if (r.VArcMin > elev || elev > r.VArcMax) return false;
            if (r.HArcMin < r.HArcMax) return r.HArcMin < rel && rel < r.HArcMax;
            return r.HArcMax >= rel || rel >= r.HArcMin;
        }

        // ---------------------------------------------------------------- profile

        private static bool BitSet(RayProfile p, int ray, int bin)
        {
            int words = (p.Bins + 63) / 64;
            return (p.Visible[ray * words + bin / 64] >> (bin % 64) & 1UL) != 0;
        }

        private static void SetBit(RayProfile p, int ray, int bin)
        {
            int words = (p.Bins + 63) / 64;
            p.Visible[ray * words + bin / 64] |= 1UL << (bin % 64);
        }

        /// <summary>
        /// Visibility of the reference target on a polar grid around the antenna. Per cell: role, altitude, MinRange..MaxRange/equation/
        /// horizon (slant distance), sector, look-down and (if terrainM != null) terrain shadow: straight line antenna->target, no curvature,
        /// like the game's physics ray. terrainM(lat, lon) = metres ASL, NaN = unknown (not an obstacle).
        /// </summary>
        /// <summary>T-320: as in the game (SensorSystem.CanProduceContacts) a radar emits when it is on and operable; ObjectBase.Emcon is derived from the radars and does not gate emission.</summary>
        public static bool Emitting(RadarSpec r) { return r != null && r.On && !r.Inoperable; }

        public static RayProfile BuildProfile(RadarSpec r, CoverageTier t, int tierIndex, double minPowerKw,
                                              Func<double, double, double> terrainM, int rays, int bins, Action<int> rayHook = null, double[] terrScratch = null)
        {
            if (rays < 4) rays = 4;
            if (bins < 1) bins = 1;
            var p = new RayProfile
            {
                Lat = r.Lat, Lon = r.Lon, Rays = rays, Bins = bins, Tier = tierIndex,
                Active = Emitting(r), Airborne = r.Airborne, Globe = r.Globe,
                OwnerUid = r.OwnerUid, Radar = r.Name,
                Visible = new ulong[rays * ((bins + 63) / 64)]
            };
            bool altBlocked;
            int kinds = r.Inoperable ? 0 : UsableKinds(r, t, out altBlocked);
            if (kinds == 0) return p;

            double tgt = t.HeightM + r.VisibleHeightM;
            double dy = tgt - r.AntennaM;
            double[] kindMax = new double[2];
            double bound = 0;
            double horizonMax = HorizonM(r.AntennaM, tgt, 0.0, false);
            for (int kind = 0; kind < 2; kind++)
            {
                if ((kinds & (1 << kind)) == 0) continue;
                double eq = EquationRangeM(r.PeakPowerKw, r.GainFactor, r.WaveLengthM, t.RcsM2, minPowerKw) * SeaFactor(r, kind == 1);
                kindMax[kind] = Math.Min(eq, CapRange(r));
                bound = Math.Max(bound, Math.Min(kindMax[kind], horizonMax));
            }
            if (!(bound > 0)) return p;
            p.RangeM = bound;
            p.BinM = bound / bins;

            bool useTerrain = terrainM != null && !OpenSea(r, terrainM, bound);
            p.Terrain = useTerrain;
            double[] terr = !useTerrain ? null : terrScratch != null && terrScratch.Length >= bins ? terrScratch : new double[bins];   // T-216: reusable buffer; every ray with terrain overwrites the first `bins` cells
            double stepDeg = 360.0 / rays;
            long shadowed = 0, seen = 0;
            for (int ray = 0; ray < rays; ray++)
            {
                double bearing = ray * stepDeg;
                if (useTerrain)
                {
                    if (rayHook != null && (ray & 31) == 0) rayHook(ray);
                    for (int k = 0; k < bins; k++)
                    {
                        double la, lo;
                        CellPoint(r, bearing, p.BinM, k, out la, out lo);
                        double h;
                        try { h = terrainM(la, lo); } catch (Exception) { h = double.NaN; }
                        terr[k] = (double.IsNaN(h) || h <= -9999.0) ? double.NaN : h;
                    }
                }
                double maxSlope = double.NegativeInfinity;
                for (int k = 0; k < bins; k++)
                {
                    double d = (k + 0.5) * p.BinM;
                    double cellTerr = useTerrain ? terr[k] : double.NaN;
                    bool visible = CellVisible(r, t, kinds, kindMax, d, dy, tgt, bearing, cellTerr);
                    if (visible)
                    {
                        seen++;
                        if (useTerrain && maxSlope > dy / d)   // terrain between antenna and target above the line of sight
                        {
                            shadowed++;
                            visible = false;
                        }
                    }
                    if (visible) SetBit(p, ray, k);
                    if (useTerrain && !double.IsNaN(cellTerr))
                    {
                        double slope = (cellTerr - r.AntennaM) / d;
                        if (slope > maxSlope) maxSlope = slope;
                    }
                }
            }
            p.ShadowPct = seen > 0 ? 100.0 * shadowed / seen : 0.0;
            return p;
        }

        /// <summary>T-202: bin size (m) BuildProfile will use for this radar and tier (0 = empty profile). Lets the worker pick the terrain memo quantisation up front.</summary>
        public static double ProfileBinM(RadarSpec r, CoverageTier t, double minPowerKw, int bins)
        {
            if (bins < 1) bins = 1;
            double bound = ProfileBoundM(r, t, minPowerKw);
            return bound > 0 ? bound / bins : 0;
        }

        /// <summary>T-225: the range (m) of the profile BuildProfile will build, bit for bit the same expression (0 = empty profile); the worker prefetches the points of the open-sea check with it.</summary>
        public static double ProfileBoundM(RadarSpec r, CoverageTier t, double minPowerKw)
        {
            bool altBlocked;
            int kinds = r.Inoperable ? 0 : UsableKinds(r, t, out altBlocked);
            if (kinds == 0) return 0;
            double tgt = t.HeightM + r.VisibleHeightM;
            double horizonMax = HorizonM(r.AntennaM, tgt, 0.0, false);
            double bound = 0;
            for (int kind = 0; kind < 2; kind++)
            {
                if ((kinds & (1 << kind)) == 0) continue;
                double eq = EquationRangeM(r.PeakPowerKw, r.GainFactor, r.WaveLengthM, t.RcsM2, minPowerKw) * SeaFactor(r, kind == 1);
                bound = Math.Max(bound, Math.Min(Math.Min(eq, CapRange(r)), horizonMax));
            }
            return bound > 0 ? bound : 0;
        }

        /// <summary>T-225: the one formula of the terrain point of cell k on the ray with this bearing; BuildProfile and the worker's prefetch both use it.</summary>
        private static void CellPoint(RadarSpec r, double bearingDeg, double binM, int k, out double lat, out double lon)
        {
            Destination(r.Lat, r.Lon, bearingDeg, (k + 0.5) * binM, r.Globe, out lat, out lon);
        }

        /// <summary>T-225: every terrain point BuildProfile reads for a profile with terrain (rays x bins, in its order). binM = ProfileBoundM / bins.</summary>
        internal static void ForEachRayPoint(RadarSpec r, double binM, int rays, int bins, Action<double, double> visit)
        {
            if (rays < 4) rays = 4;
            if (bins < 1) bins = 1;
            double stepDeg = 360.0 / rays;
            for (int ray = 0; ray < rays; ray++)
            {
                double bearing = ray * stepDeg;
                for (int k = 0; k < bins; k++)
                {
                    double la, lo;
                    CellPoint(r, bearing, binM, k, out la, out lo);
                    visit(la, lo);
                }
            }
        }

        /// <summary>T-225: every point the open-sea check (OpenSea) may read.</summary>
        internal static void ForEachOpenSeaPoint(RadarSpec r, double rangeM, Action<double, double> visit)
        {
            for (int a = 0; a < 36; a++)
                for (int i = 1; i <= 16; i++)
                {
                    double la, lo;
                    Destination(r.Lat, r.Lon, a * 10.0, rangeM * i / 16.0, r.Globe, out la, out lo);
                    visit(la, lo);
                }
        }

        private static bool CellVisible(RadarSpec r, CoverageTier t, int kinds, double[] kindMax, double d, double dy, double tgt, double bearing, double cellTerr)
        {
            double s = Math.Sqrt(d * d + dy * dy);
            if (r.MinRangeM > 0 && s < r.MinRangeM) return false;
            double horizon = HorizonM(r.AntennaM, tgt, TerrainRefM(r, cellTerr), false);
            if (s > horizon) return false;
            if (!InSector(r, bearing, d, tgt)) return false;
            bool lookDown = r.LookDownMult < 1.0 && InLookDown(r.AntennaM, horizon, d, tgt);
            if (lookDown && s > LookDownCap(r)) return false;
            for (int kind = 0; kind < 2; kind++)
            {
                if ((kinds & (1 << kind)) == 0) continue;
                double lim = kindMax[kind];
                if (lookDown) lim *= r.LookDownMult;
                if (s <= lim) return true;
            }
            return false;
        }

        /// <summary>Coarse 36 x 16 sample: true if nothing above sea level within the range (terrain shadows then cannot occur).</summary>
        internal static bool OpenSea(RadarSpec r, Func<double, double, double> terrainM, double rangeM)
        {
            for (int a = 0; a < 36; a++)
                for (int i = 1; i <= 16; i++)
                {
                    double la, lo;
                    Destination(r.Lat, r.Lon, a * 10.0, rangeM * i / 16.0, r.Globe, out la, out lo);
                    double h;
                    try { h = terrainM(la, lo); } catch (Exception) { h = double.NaN; }
                    if (!double.IsNaN(h) && h > 0 && h > -9999.0) return false;
                }
            return true;
        }

        /// <summary>Memoizing wrapper for a terrain sampler (quantised to quantDeg); not thread-safe: one per worker thread.</summary>
        public const int MemoMax = 200000;   // ~6 MB; the memo is cleared when it grows past this (T-151)

        public static Func<double, double, double> CachedSampler(Func<double, double, double> inner, double quantDeg, int maxEntries = MemoMax, Action<int> onSize = null)
        {
            if (inner == null) return null;
            var memo = new Dictionary<long, double>();
            return (lat, lon) =>
            {
                long qa = (long)Math.Round(lat / quantDeg), qo = (long)Math.Round(lon / quantDeg);
                long key = qa * 4000003L + qo;
                double v;
                if (memo.TryGetValue(key, out v)) return v;
                v = inner(qa * quantDeg, qo * quantDeg);
                if (!double.IsNaN(v))   // unknown (chunk not loaded yet) is retried later
                {
                    if (memo.Count >= maxEntries) { memo.Clear(); if (onSize != null) onSize(0); }
                    memo[key] = v;
                    if (onSize != null) onSize(memo.Count);
                }
                return v;
            };
        }

        private static bool Lookup(RayProfile p, double bearingDeg, double distM)
        {
            if (p.Visible == null || !(p.RangeM > 0) || distM >= p.RangeM) return false;
            int bin = (int)(distM / p.BinM);
            if (bin >= p.Bins) return false;
            int ray = (int)Math.Round(bearingDeg / (360.0 / p.Rays)) % p.Rays;
            if (ray < 0) ray += p.Rays;
            return BitSet(p, ray, bin);
        }

        /// <summary>True if the profile's radar (active or not) covers the point.</summary>
        public static bool Covered(RayProfile p, double lat, double lon)
        {
            double b, d;
            BearingDistance(p.Lat, p.Lon, lat, lon, p.Globe, out b, out d);
            return Lookup(p, b, d);
        }

        /// <summary>Same as Covered with a cheap flat-earth prefilter (used for many points).</summary>
        private static bool CoveredQuick(RayProfile p, double lat, double lon)
        {
            if (p.Visible == null || !(p.RangeM > 0)) return false;
            double dyM, dxM;
            if (p.Globe)
            {
                dyM = (lat - p.Lat) * Rad * EarthRadiusM;
                dxM = (lon - p.Lon) * Rad * EarthRadiusM * Math.Cos((lat + p.Lat) * 0.5 * Rad);
            }
            else { dyM = (lat - p.Lat) * FlatMPerDeg; dxM = (lon - p.Lon) * FlatMPerDeg; }   // T-352: the flat world of stable
            double d2 = dxM * dxM + dyM * dyM;
            if (d2 >= p.RangeM * p.RangeM) return false;
            double b = (Math.Atan2(dxM, dyM) * Deg + 360.0) % 360.0;
            return Lookup(p, b, Math.Sqrt(d2));
        }

        // ---------------------------------------------------------------- bounds / raster / outlines

        /// <summary>Bounding box of all visible cells of all profiles (+ centres), enlarged by marginFrac of its size.</summary>
        public static GeoBox Bounds(IList<RayProfile> profiles, double marginFrac)
        {
            double latMin = double.MaxValue, latMax = double.MinValue, lonMin = double.MaxValue, lonMax = double.MinValue;
            bool any = false;
            if (profiles != null)
                foreach (RayProfile p in profiles)
                {
                    Extend(ref latMin, ref latMax, ref lonMin, ref lonMax, p.Lat, p.Lon);
                    any = true;
                    if (p.Visible == null || !(p.RangeM > 0)) continue;
                    double step = 360.0 / p.Rays;
                    for (int ray = 0; ray < p.Rays; ray++)
                    {
                        int far = -1;
                        for (int k = p.Bins - 1; k >= 0; k--)
                            if (BitSet(p, ray, k)) { far = k; break; }
                        if (far < 0) continue;
                        double la, lo;
                        Destination(p.Lat, p.Lon, ray * step, (far + 1) * p.BinM, p.Globe, out la, out lo);
                        Extend(ref latMin, ref latMax, ref lonMin, ref lonMax, la, lo);
                    }
                }
            if (!any) return new GeoBox();
            double dLat = latMax - latMin, dLon = lonMax - lonMin;
            double mLat = Math.Max(dLat * marginFrac, 1e-5), mLon = Math.Max(dLon * marginFrac, 1e-5);
            return new GeoBox { LatMin = latMin - mLat, LatMax = latMax + mLat, LonMin = lonMin - mLon, LonMax = lonMax + mLon };
        }

        private static void Extend(ref double latMin, ref double latMax, ref double lonMin, ref double lonMax, double lat, double lon)
        {
            if (lat < latMin) latMin = lat;
            if (lat > latMax) latMax = lat;
            if (lon < lonMin) lonMin = lon;
            if (lon > lonMax) lonMax = lon;
        }

        /// <summary>
        /// w x h grid over the box; row 0 = NORTH edge (LatMax), column 0 = west. Bits 0..3: tier covered by an active radar;
        /// bits 4..7: tier covered only by an inactive (off/EMCON) radar.
        /// </summary>
        public static byte[] Rasterize(IList<RayProfile> profiles, GeoBox box, int w, int h)
        {
            var res = new byte[Math.Max(0, w) * Math.Max(0, h)];
            if (profiles == null || w <= 0 || h <= 0) return res;
            double dLat = (box.LatMax - box.LatMin) / h, dLon = (box.LonMax - box.LonMin) / w;
            if (!(dLat > 0) || !(dLon > 0)) return res;
            foreach (RayProfile p in profiles)
            {
                if (p.Visible == null || !(p.RangeM > 0) || p.Tier < 0 || p.Tier >= MaxTiers) continue;
                byte bit = (byte)(1 << (p.Active ? p.Tier : 4 + p.Tier));
                double latSpan = p.Globe ? p.RangeM / EarthRadiusM * Deg : p.RangeM / FlatMPerDeg;
                double lonSpan = p.Globe ? latSpan / Math.Max(0.01, Math.Cos(p.Lat * Rad)) : latSpan;   // T-352: flat = the same span in degrees
                int y0 = Math.Max(0, (int)Math.Floor((box.LatMax - (p.Lat + latSpan)) / dLat));
                int y1 = Math.Min(h - 1, (int)Math.Ceiling((box.LatMax - (p.Lat - latSpan)) / dLat));
                int x0 = Math.Max(0, (int)Math.Floor((p.Lon - lonSpan - box.LonMin) / dLon));
                int x1 = Math.Min(w - 1, (int)Math.Ceiling((p.Lon + lonSpan - box.LonMin) / dLon));
                for (int y = y0; y <= y1; y++)
                {
                    double lat = box.LatMax - (y + 0.5) * dLat;
                    for (int x = x0; x <= x1; x++)
                    {
                        double lon = box.LonMin + (x + 0.5) * dLon;
                        if (CoveredQuick(p, lat, lon)) res[y * w + x] |= bit;
                    }
                }
            }
            return res;
        }

        /// <summary>
        /// Outline of each profile: outer edge of the first contiguous visible run of every ray, radial edges where the sector ends, inner
        /// (MinRange) ring. Active segments hidden by another active profile of the same tier are dropped, so the union outline remains;
        /// inactive profiles keep their own outline (Style 1).
        /// </summary>
        public static List<GeoSeg> Outlines(IList<RayProfile> profiles)
        {
            var res = new List<GeoSeg>();
            if (profiles == null) return res;
            bool[] has = null;
            double[] inner = null, outer = null, oLat = null, oLon = null, iLat = null, iLon = null;
            for (int pi = 0; pi < profiles.Count; pi++)
            {
                RayProfile p = profiles[pi];
                if (p.Visible == null || !(p.RangeM > 0) || p.Tier < 0 || p.Tier >= MaxTiers) continue;
                byte style = (byte)(!p.Active ? 1 : (p.Airborne ? 2 : 0));
                int n = p.Rays;
                double step = 360.0 / n;
                if (has == null || has.Length != n)   // T-216: one set of buffers for all profiles (they were 17 KB per profile)
                {
                    has = new bool[n]; inner = new double[n]; outer = new double[n];
                    oLat = new double[n]; oLon = new double[n]; iLat = new double[n]; iLon = new double[n];
                }
                else Array.Clear(has, 0, n);
                bool anyRay = false;
                for (int ray = 0; ray < n; ray++)
                {
                    int a = -1;
                    for (int k = 0; k < p.Bins; k++) if (BitSet(p, ray, k)) { a = k; break; }
                    if (a < 0) continue;
                    int b = a;
                    while (b < p.Bins && BitSet(p, ray, b)) b++;
                    has[ray] = true; anyRay = true;
                    inner[ray] = a * p.BinM; outer[ray] = b * p.BinM;
                    Destination(p.Lat, p.Lon, ray * step, outer[ray], p.Globe, out oLat[ray], out oLon[ray]);
                    Destination(p.Lat, p.Lon, ray * step, inner[ray], p.Globe, out iLat[ray], out iLon[ray]);
                }
                if (!anyRay) continue;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    double midB = (i + 0.5) * step;
                    if (has[i] && has[j])
                    {
                        double dm = (outer[i] + outer[j]) * 0.5 + 0.75 * p.BinM;
                        Emit(res, profiles, pi, p, style, oLat[i], oLon[i], oLat[j], oLon[j], midB, dm);
                        if (inner[i] > 0.5 * p.BinM && inner[j] > 0.5 * p.BinM)
                        {
                            double di = Math.Max(0.0, (inner[i] + inner[j]) * 0.5 - 0.75 * p.BinM);
                            Emit(res, profiles, pi, p, style, iLat[i], iLon[i], iLat[j], iLon[j], midB, di);
                        }
                    }
                    else if (has[i] != has[j])
                    {
                        int c = has[i] ? i : j;
                        double sign = has[i] ? 1.0 : -1.0;
                        double testB = c * step + sign * 0.75 * step;
                        double testD = (inner[c] + outer[c]) * 0.5;
                        Emit(res, profiles, pi, p, style, iLat[c], iLon[c], oLat[c], oLon[c], testB, testD);
                    }
                }
            }
            return res;
        }

        private static void Emit(List<GeoSeg> res, IList<RayProfile> profiles, int self, RayProfile p, byte style,
                                 double la1, double lo1, double la2, double lo2, double testBearing, double testDist)
        {
            if (la1 == la2 && lo1 == lo2) return;
            if (p.Active)
            {
                double tl, to;
                Destination(p.Lat, p.Lon, testBearing, testDist, p.Globe, out tl, out to);
                for (int q = 0; q < profiles.Count; q++)
                {
                    RayProfile o = profiles[q];
                    if (q == self || !o.Active || o.Tier != p.Tier) continue;
                    if (CoveredQuick(o, tl, to)) return;
                }
            }
            res.Add(new GeoSeg { Lat1 = la1, Lon1 = lo1, Lat2 = la2, Lon2 = lo2, Tier = (byte)p.Tier, Style = style });
        }

        // ---------------------------------------------------------------- projection helpers

        /// <summary>Affine (lat,lon) -> (x,y) from three point pairs; ok=false if the points are (nearly) collinear.</summary>
        public static Affine2 FitAffine(double[] lat3, double[] lon3, double[] x3, double[] y3, out bool ok)
        {
            ok = false;
            var res = new Affine2();
            if (lat3 == null || lon3 == null || x3 == null || y3 == null || lat3.Length < 3 || lon3.Length < 3 || x3.Length < 3 || y3.Length < 3) return res;
            double u1 = lat3[1] - lat3[0], v1 = lon3[1] - lon3[0], u2 = lat3[2] - lat3[0], v2 = lon3[2] - lon3[0];
            double det = u1 * v2 - u2 * v1;
            double scale = Math.Max(Math.Abs(u1) + Math.Abs(u2), Math.Abs(v1) + Math.Abs(v2));
            if (double.IsNaN(det) || Math.Abs(det) <= 1e-9 * scale * scale || scale <= 0) return res;
            double dx1 = x3[1] - x3[0], dx2 = x3[2] - x3[0], dy1 = y3[1] - y3[0], dy2 = y3[2] - y3[0];
            res.A = (dx1 * v2 - dx2 * v1) / det;
            res.B = (u1 * dx2 - u2 * dx1) / det;
            res.D = (dy1 * v2 - dy2 * v1) / det;
            res.E = (u1 * dy2 - u2 * dy1) / det;
            res.C = x3[0] - res.A * lat3[0] - res.B * lon3[0];
            res.F = y3[0] - res.D * lat3[0] - res.E * lon3[0];
            ok = true;
            return res;
        }

        private static List<double[]> ClipHalfPlane(List<double[]> poly, double ax, double ay, double bx, double by, bool keepLeft)
        {
            var res = new List<double[]>();
            if (poly.Count == 0) return res;
            double sgn = keepLeft ? 1.0 : -1.0;
            Func<double[], double> f = q => sgn * ((bx - ax) * (q[1] - ay) - (by - ay) * (q[0] - ax));
            for (int i = 0; i < poly.Count; i++)
            {
                double[] c = poly[i], nx = poly[(i + 1) % poly.Count];
                double fc = f(c), fn = f(nx);
                if (fc >= 0) res.Add(c);
                if ((fc >= 0) != (fn >= 0))
                {
                    double tt = fc / (fc - fn);
                    res.Add(new[] { c[0] + tt * (nx[0] - c[0]), c[1] + tt * (nx[1] - c[1]) });
                }
            }
            return res;
        }

        private static double PolyArea(List<double[]> poly)
        {
            double s = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                double[] a = poly[i], b = poly[(i + 1) % poly.Count];
                s += a[0] * b[1] - b[0] * a[1];
            }
            return s * 0.5;
        }

        private static void FanTriangles(List<double[]> poly, List<double[]> res)
        {
            for (int i = 1; i + 1 < poly.Count; i++)
            {
                double[] a = poly[0], b = poly[i], c = poly[i + 1];
                if (Math.Abs((b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])) < 1e-9) continue;
                res.Add(new[] { a[0], a[1], b[0], b[1], c[0], c[1] });
            }
        }

        /// <summary>
        /// Triangles (x1,y1,x2,y2,x3,y3) covering the rectangle (x,y,w,h) minus the convex quadrilateral; disjoint, so alpha does not stack.
        /// Degenerate or invalid quad -> the whole rectangle.
        /// </summary>
        public static List<double[]> FogOutside(double[] rectXYWH, double[][] quad4)
        {
            var res = new List<double[]>();
            if (rectXYWH == null || rectXYWH.Length < 4 || !(rectXYWH[2] > 0) || !(rectXYWH[3] > 0)) return res;
            double x = rectXYWH[0], y = rectXYWH[1], w = rectXYWH[2], h = rectXYWH[3];
            var rem = new List<double[]> { new[] { x, y }, new[] { x + w, y }, new[] { x + w, y + h }, new[] { x, y + h } };
            var quad = new List<double[]>();
            bool valid = quad4 != null && quad4.Length == 4;
            if (valid)
                foreach (double[] q in quad4)
                {
                    if (q == null || q.Length < 2 || double.IsNaN(q[0]) || double.IsNaN(q[1]) || double.IsInfinity(q[0]) || double.IsInfinity(q[1])) { valid = false; break; }
                    quad.Add(new[] { q[0], q[1] });
                }
            if (valid)
            {
                double area = PolyArea(quad);
                if (Math.Abs(area) < 1e-9) valid = false;
                else if (area < 0) quad.Reverse();
            }
            if (!valid) { FanTriangles(rem, res); return res; }
            for (int i = 0; i < 4 && rem.Count >= 3; i++)
            {
                double[] a = quad[i], b = quad[(i + 1) % 4];
                FanTriangles(ClipHalfPlane(rem, a[0], a[1], b[0], b[1], false), res);   // outside this edge
                rem = ClipHalfPlane(rem, a[0], a[1], b[0], b[1], true);                 // keep going inside it
            }
            return res;
        }

        private static double Cross(double[] o, double[] a, double[] b) { return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]); }

        /// <summary>Convex hull (counter-clockwise in the y-up sense, Andrew's monotone chain) of finite points; fewer than 3 hull points -> empty.</summary>
        internal static List<double[]> ConvexHull(IList<double[]> pts)
        {
            var p = new List<double[]>();
            if (pts != null)
                foreach (double[] q in pts)
                    if (q != null && q.Length >= 2 && !double.IsNaN(q[0]) && !double.IsNaN(q[1]) && !double.IsInfinity(q[0]) && !double.IsInfinity(q[1])) p.Add(q);
            p.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
            if (p.Count < 3) return new List<double[]>();
            var h = new List<double[]>();
            for (int i = 0; i < p.Count; i++)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            h.RemoveAt(h.Count - 1);
            return h.Count < 3 ? new List<double[]>() : h;
        }

        /// <summary>T-309: like FogOutside for a polygon of any vertex count (the box edge subdivided for the globe): the fog covers the rectangle minus
        /// the convex hull of the polygon (a concave outline is fogged by its hull). Degenerate / empty polygon -> the whole rectangle.</summary>
        public static List<double[]> FogOutsidePoly(double[] rectXYWH, IList<double[]> poly)
        {
            var res = new List<double[]>();
            if (rectXYWH == null || rectXYWH.Length < 4 || !(rectXYWH[2] > 0) || !(rectXYWH[3] > 0)) return res;
            double x = rectXYWH[0], y = rectXYWH[1], w = rectXYWH[2], h = rectXYWH[3];
            var rem = new List<double[]> { new[] { x, y }, new[] { x + w, y }, new[] { x + w, y + h }, new[] { x, y + h } };
            var hull = ConvexHull(poly);
            if (hull.Count < 3 || Math.Abs(PolyArea(hull)) < 1e-9) { FanTriangles(rem, res); return res; }
            if (PolyArea(hull) < 0) hull.Reverse();   // same orientation FogOutside expects
            for (int i = 0; i < hull.Count && rem.Count >= 3; i++)
            {
                double[] a = hull[i], b = hull[(i + 1) % hull.Count];
                FanTriangles(ClipHalfPlane(rem, a[0], a[1], b[0], b[1], false), res);
                rem = ClipHalfPlane(rem, a[0], a[1], b[0], b[1], true);
            }
            return res;
        }

        // ---------------------------------------------------------------- game values -> spec, change detection, map helpers

        /// <summary>
        /// Builds a RadarSpec from raw game values (unity units: 1 unity = 67.2 m). role: 0 undefined, 1 surface, 2 air, 3 surface+air.
        /// A rotating antenna sees the full horizon over one turn (only its vertical arc still applies). Arcs: a single ViewArcs number
        /// gives equal symmetric pairs = cone. Axis: unity forward vector (x east, z north) -> compass bearing.
        /// </summary>
        public static RadarSpec SpecFromRaw(string name, string type, int role, bool rotatable, double minRangeU, double maxRangeU,
                                            double minAltU, double maxAltU, double peakKw, double gainFactor, double waveM,
                                            double lookDownMult, double lookDownRangeU, double hArcMin, double hArcMax, double vArcMin, double vArcMax,
                                            double fwdX, double fwdZ, double antennaYU, double visibleHeightU)
        {
            var r = new RadarSpec
            {
                Name = name, Type = type,
                Air = role == 2 || role == 3, Surface = role == 1 || role == 3,
                MinRangeM = minRangeU * MPerUnity, MaxRangeM = maxRangeU * MPerUnity,
                MinAltM = minAltU < 0 ? -1 : minAltU * MPerUnity, MaxAltM = maxAltU < 0 ? -1 : maxAltU * MPerUnity,
                PeakPowerKw = peakKw, GainFactor = gainFactor, WaveLengthM = waveM,
                LookDownMult = lookDownMult, LookDownRangeM = lookDownRangeU * MPerUnity,
                HArcMin = hArcMin, HArcMax = hArcMax, VArcMin = vArcMin, VArcMax = vArcMax,
                AxisDeg = (Math.Atan2(fwdX, fwdZ) * Deg + 360.0) % 360.0,
                AntennaM = antennaYU * MPerUnity, VisibleHeightM = visibleHeightU * MPerUnity
            };
            bool vFull = vArcMin < -90 && vArcMax > 90;
            bool hFull = hArcMin < -180 && hArcMax > 180;
            if (rotatable || hFull)
            {
                r.HArcMin = -181; r.HArcMax = 181;
                if (vFull) r.FullCircle = true;
            }
            else
            {
                r.Cone = hArcMin == vArcMin && hArcMax == vArcMax && hArcMin == -hArcMax && hArcMax > 0;
            }
            return r;
        }

        public const double MoveThresholdM = 1.0, SectorTurnDeg = 2.0, MinRecomputeGapS = 1.0;

        /// <summary>Non-motion fields: power state, EMCON, kind, sea state, role, arcs, ranges, altitudes, power, gain, wave, look-down.</summary>
        public static bool RadarStateChanged(RadarSpec a, RadarSpec b)
        {
            if (a == null || b == null) return a != b;
            if (a.On != b.On || a.Inoperable != b.Inoperable || a.Emcon != b.Emcon || a.Airborne != b.Airborne || a.SeaState != b.SeaState) return true;
            if (a.Air != b.Air || a.Surface != b.Surface || a.FullCircle != b.FullCircle || a.Cone != b.Cone) return true;
            if (a.MinRangeM != b.MinRangeM || a.MaxRangeM != b.MaxRangeM || a.MinAltM != b.MinAltM || a.MaxAltM != b.MaxAltM) return true;
            if (a.PeakPowerKw != b.PeakPowerKw || a.GainFactor != b.GainFactor || a.WaveLengthM != b.WaveLengthM || a.LookDownMult != b.LookDownMult) return true;
            if (a.LookDownRangeM != b.LookDownRangeM) return true;
            if (a.HArcMin != b.HArcMin || a.HArcMax != b.HArcMax || a.VArcMin != b.VArcMin || a.VArcMax != b.VArcMax) return true;
            return false;
        }

        /// <summary>Position beyond MoveThresholdM, antenna height (max(2 m, 5 %)), sector axis beyond SectorTurnDeg (only !FullCircle).</summary>
        public static bool Moved(RadarSpec a, RadarSpec b)
        {
            if (a == null || b == null) return a != b;
            double bearing, dist;
            if (a.Globe != b.Globe) return true;   // T-352: the world changed, the cached profiles are in another frame
            BearingDistance(a.Lat, a.Lon, b.Lat, b.Lon, a.Globe, out bearing, out dist);
            if (dist > MoveThresholdM) return true;
            if (Math.Abs(a.AntennaM - b.AntennaM) > Math.Max(2.0, 0.05 * Math.Max(a.AntennaM, b.AntennaM))) return true;
            if (!a.FullCircle && Math.Abs(Wrap180(a.AxisDeg - b.AxisDeg)) > SectorTurnDeg) return true;
            return false;
        }

        /// <summary>True if the change between two snapshots of the same radar needs a new coverage calculation (worker cache key).</summary>
        public static bool SpecChanged(RadarSpec a, RadarSpec b)
        {
            if (a == null || b == null) return a != b;
            return RadarStateChanged(a, b) || Moved(a, b);
        }

        // ---------------------------------------------------------------- T-090: scheduling helpers, map layer, legend

        public static double ClampRecompute(double s) { return double.IsNaN(s) || s <= 0 ? 10.0 : Math.Max(2.0, Math.Min(120.0, s)); }
        public static double ClampStateCheck(double s) { return double.IsNaN(s) || s <= 0 ? 1.0 : Math.Max(0.2, Math.Min(1.0, s)); }
        public static double ClampRtScale(double s) { return double.IsNaN(s) ? 1.0 : Math.Max(0.5, Math.Min(1.0, s)); }

        /// <summary>"auto"|"noesis"|"imgui" (case-insensitive); anything else -> "auto".</summary>
        public static string ParseRenderMode(string s)
        {
            string v = (s ?? "").Trim().ToLowerInvariant();
            return v == "noesis" || v == "imgui" ? v : "auto";
        }

        /// <summary>Map-local positions of the reference points (x0,y0,x1,y1,...) of two frames: true if any moved > tolPx or the count differs.</summary>
        public static bool ProjectionChanged(double[] prev, double[] cur, double tolPx)
        {
            if (prev == null || cur == null) return prev != cur;
            if (prev.Length != cur.Length) return true;
            for (int i = 0; i < prev.Length; i++)
                if (double.IsNaN(cur[i]) || double.IsNaN(prev[i]) || Math.Abs(prev[i] - cur[i]) > tolPx) return true;
            return false;
        }

        /// <summary>
        /// Insert index in MapBase.Children: right above the last base layer (type name contains "TileLayer" or "ImageLayer", or is "MapImage");
        /// no base layer -> 1; override >= 0 -> min(override, count). zIndex = ZIndex of that base layer (0 if none).
        /// </summary>
        public static int MapChildInsertIndex(IList<string> typeNames, IList<int> zIndexes, int layerIndexOverride, out int zIndex)
        {
            int count = typeNames == null ? 0 : typeNames.Count;
            int baseIdx = -1;
            for (int i = 0; i < count; i++)
            {
                string n = typeNames[i] ?? "";
                if (n.Contains("TileLayer") || n.Contains("ImageLayer") || n == "MapImage") baseIdx = i;
            }
            zIndex = baseIdx >= 0 && zIndexes != null && baseIdx < zIndexes.Count ? zIndexes[baseIdx] : 0;
            if (layerIndexOverride >= 0) return Math.Min(layerIndexOverride, count);
            return baseIdx >= 0 ? baseIdx + 1 : Math.Min(1, count);
        }

        /// <summary>Legend texts (same as the IMGUI legend of T-066), without rich-text tags.</summary>
        public static List<LegendLine> LegendLines(IList<CoverageTier> tiers, bool clutter, string hotkeyLabel, bool jamDashed = false, bool jamPartial = false, bool jamApprox = false)
        {
            var res = new List<LegendLine>();
            // T-178 O4 (ADR-0077 section 6): no key in the title; hotkeyLabel stays in the signature for the callers (the key is shown in the tips)
            res.Add(new LegendLine { Text = UiText.Get("cov.legend.title"), Bold = true });
            if (tiers != null)
                for (int i = 0; i < tiers.Count; i++)
                {
                    var t = tiers[i];
                    string kind = t.Surface && !t.Air ? UiText.Get("cov.legend.surface") : UiText.Get("cov.legend.air");
                    res.Add(new LegendLine
                    {
                        Swatch = Math.Min(3, i),
                        Text = t.HeightM.ToString("0", CultureInfo.InvariantCulture) + " m, " + t.RcsM2.ToString("0.#", CultureInfo.InvariantCulture) + " m\u00B2, " + kind
                    });
                }
            res.Add(new LegendLine { Swatch = 4, Text = UiText.Get("cov.legend.off") });
            if (jamDashed) res.Add(new LegendLine { Swatch = 5, Text = UiText.Get("cov.legend.jam_dashed") });   // T-182
            if (jamPartial) res.Add(new LegendLine { Text = UiText.Get("cov.legend.jam_partial") });
            if (jamApprox) res.Add(new LegendLine { Text = UiText.Get("cov.legend.jam_approx") });   // T-225
            res.Add(new LegendLine { Text = UiText.Get("cov.legend.shadows") });
            if (clutter) res.Add(new LegendLine { Text = UiText.Get("cov.legend.clutter") });
            return res;
        }

        /// <summary>
        /// Legend box in the map's local pixels, in the TOP-RIGHT corner (customer 2026-10-22: under the game's time panel; the bottom-left corner was
        /// covered by World View, the bottom-right one holds the game's message lines). mapW/mapH in screen pixels, scale = RT pixels per screen pixel,
        /// textW = widest line in screen px (0 = unknown). Small map (w &lt; 900 or h &lt; 500): font 10 / line 13 / min width 215, else 12 / 16 / 270.
        /// stackPx = the legend boxes above this one (the ring legend stands over the radar legend; counts toward the 45 %), panelPx = the offset under
        /// the time panel (LegendPanelOffset; does not count). Hidden if it covers > 60 % of the width, if it with the boxes above covers > 45 % of the
        /// height, or if it would leave the map at the bottom (8 px margin).
        /// </summary>
        public static LegendBox LegendLayout(double mapW, double mapH, int lines, double textW, double scale, double stackPx = 0, double panelPx = 0)
        {
            bool small = mapW < 900 || mapH < 500;
            double font = small ? 10 : 12, lh = small ? 13 : 16, minW = small ? 215 : 270;
            double w = Math.Max(minW, textW > 0 ? textW + 12 : 0), h = lines * lh + 8;
            double stack = Math.Max(0, stackPx), top = 8 + Math.Max(0, panelPx) + stack;
            var b = new LegendBox { Visible = !(w > mapW * 0.6 || h + stack > mapH * 0.45 || top + h + 8 > mapH) };
            b.X = (mapW - w - 8) * scale; b.Y = top * scale; b.W = w * scale; b.H = h * scale;
            b.FontPx = font * scale; b.LineH = lh * scale; b.Pad = 6 * scale;
            return b;
        }

        /// <summary>T-233: share of the screen the map must cover for the legends to be drawn on an un-maximised map (the player stretched the window).</summary>
        public const double SmallMapScreenShare = 0.5;

        /// <summary>
        /// T-233 (Q1 a): may the legends (radar + range rings) be drawn at all. maximized = the map window is maximised (CoverageOverlay.MapMaximized) -> true, why "none".
        /// Otherwise (the small Tactical Display in the 3D view) true only if the map covers &gt;= 50 % of the screen (the player stretched it); else false, why "small_map".
        /// A zero / NaN size or screen -> false, "small_map" on a small map.
        /// </summary>
        public static bool LegendAllowed(bool maximized, double mapW, double mapH, double screenW, double screenH, out string why)
        {
            why = "none";
            if (maximized) return true;
            if (mapW > 0 && mapH > 0 && screenW > 0 && screenH > 0 && mapW * mapH >= SmallMapScreenShare * screenW * screenH) return true;
            why = "small_map";
            return false;
        }

        /// <summary>
        /// Offset (map-local screen px, whole, &gt;= 0) that puts the top-right legends gap px under the game's time-compression panel. map / panel =
        /// {x, y, w, h} in the same screen pixels (y from the top). legendW = the widest legend box in screen px (the legends stand 8 px from the map's
        /// right edge). 0 when the panel is not plausible (PlausiblePanel), lies left of the legend column or ends above the legend's normal top.
        /// </summary>
        public static double LegendPanelOffset(double[] map, double[] panel, double legendW, double gap)
        {
            if (!PlausiblePanel(map, panel)) return 0;
            double lx1 = map[0] + map[2] - 8, lx0 = lx1 - Math.Max(1, legendW);
            if (panel[0] + panel[2] <= lx0 || panel[0] >= lx1) return 0;
            double off = Math.Ceiling(panel[1] + panel[3] + Math.Max(0, gap) - (map[1] + 8));
            return off > 0 ? off : 0;
        }

        /// <summary>T-190 A4: index of the plausible (PlausiblePanel against the screen rectangle) rectangle with the largest area; -1 = none.
        /// The panel Grid (209x62) beats the Button inside it (199x20) that shares its DataContext.</summary>
        public static int PickPanel(IList<double[]> rects, double[] screen)
        {
            int best = -1; double bestArea = -1;
            if (rects == null) return -1;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                if (!PlausiblePanel(screen, r)) continue;
                double a = r[2] * r[3];
                if (a > bestArea) { bestArea = a; best = i; }
            }
            return best;
        }

        public static double[] Shift(double[] r, double dx, double dy)
        {
            return new[] { r[0] + dx, r[1] + dy, r[2], r[3] };
        }

        private static bool Overlaps(double[] a, double[] b)
        {
            return a[0] < b[0] + b[2] && b[0] < a[0] + a[2] && a[1] < b[1] + b[3] && b[1] < a[1] + a[3];
        }

        /// <summary>T-190 A5: shift of the legend column col = {x, y, w, h} (map-local screen px) so it does not cover the obstacles (the time panel, the
        /// Overview window). Order: stays (why "none"); down under the lowest intersecting obstacle + gap while it fits above mapH - 8 ("below");
        /// left of the leftmost intersecting obstacle at the same height while x &gt;= 8 ("left"); else {0, 0} ("blocked").</summary>
        public static double[] LegendAvoid(double[] col, IList<double[]> obstacles, double mapW, double mapH, double gap, out string why)
        {
            why = "none";
            if (col == null || col.Length < 4 || obstacles == null || obstacles.Count == 0) return new[] { 0.0, 0.0 };
            var obs = new List<double[]>();
            foreach (var o in obstacles) if (o != null && o.Length >= 4 && !double.IsNaN(o[0] + o[1] + o[2] + o[3])) obs.Add(o);
            var cur = Shift(col, 0, 0);
            bool any = false;
            foreach (var o in obs) if (Overlaps(cur, o)) { any = true; break; }
            if (!any) return new[] { 0.0, 0.0 };
            // down
            for (int guard = 0; guard < 16; guard++)
            {
                double bottom = double.NegativeInfinity;
                foreach (var o in obs) if (Overlaps(cur, o)) bottom = Math.Max(bottom, o[1] + o[3]);
                if (double.IsNegativeInfinity(bottom)) { if (cur[1] + cur[3] <= mapH - 8) { why = "below"; return new[] { 0.0, cur[1] - col[1] }; } break; }
                cur[1] = bottom + gap;
                if (cur[1] + cur[3] > mapH - 8) break;
            }
            // left
            cur = Shift(col, 0, 0);
            for (int guard = 0; guard < 16; guard++)
            {
                double left = double.PositiveInfinity;
                foreach (var o in obs) if (Overlaps(cur, o)) left = Math.Min(left, o[0]);
                if (double.IsPositiveInfinity(left)) { why = "left"; return new[] { cur[0] - col[0], 0.0 }; }
                cur[0] = left - gap - cur[2];
                if (cur[0] < 8) break;
            }
            why = "blocked";
            return new[] { 0.0, 0.0 };
        }

        /// <summary>A rectangle that can be the time panel over the map (screen px): finite, larger than 1x1, at most half the map's width and 30 % of its
        /// height. Rejects the full-screen ContentControl / Canvas that host the panel and wrong coordinate frames.</summary>
        public static bool PlausiblePanel(double[] map, double[] r)
        {
            if (map == null || r == null || map.Length < 4 || r.Length < 4) return false;
            for (int i = 0; i < 4; i++) if (double.IsNaN(map[i]) || double.IsInfinity(map[i]) || double.IsNaN(r[i]) || double.IsInfinity(r[i])) return false;
            if (map[2] <= 1 || map[3] <= 1 || r[2] <= 1 || r[3] <= 1) return false;
            return r[2] <= 0.5 * map[2] && r[3] <= 0.3 * map[3];
        }

        /// <summary>The time panel when the game's element cannot be read: at 1920x1080 it occupies x 1710..1920, y 20..80 (the customer's screenshot,
        /// a maximized map window moves it 20 px down); scaled with the screen height and kept at the right edge.</summary>
        public static double[] TimePanelFallback(double screenW, double screenH)
        {
            double k = screenH > 1 ? screenH / 1080.0 : 1.0;
            return new[] { screenW - 210 * k, 20 * k, 210 * k, 60 * k };
        }

        internal struct RasterRun { public int Row, X0, X1; public float Alpha; }

        /// <summary>Runs of equal (quantised to 1/255) non-zero alpha along each raster row; row 0 = north.</summary>
        public static List<RasterRun> RasterRuns(byte[] raster, int w, int h, Func<byte, float> alpha)
        {
            var res = new List<RasterRun>();
            if (raster == null || w <= 0 || h <= 0 || raster.Length < w * h) return res;
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = (float)Math.Round(Math.Max(0f, Math.Min(1f, alpha((byte)i))) * 255f) / 255f;
            for (int r = 0; r < h; r++)
            {
                int x = 0;
                while (x < w)
                {
                    float a = lut[raster[r * w + x]];
                    int x1 = x;
                    while (x1 + 1 < w && lut[raster[r * w + x1 + 1]] == a) x1++;
                    if (a > 0) res.Add(new RasterRun { Row = r, X0 = x, X1 = x1, Alpha = a });
                    x = x1 + 1;
                }
            }
            return res;
        }

        /// <summary>Rectangle r (x,y,w,h) minus the occluders: disjoint pieces, those thinner than minSize dropped, at most maxPieces
        /// (the largest kept).</summary>
        public static List<double[]> SubtractRects(double[] r, IList<double[]> occ, double minSize, int maxPieces)
        {
            var cur = new List<double[]> { new[] { r[0], r[1], r[2], r[3] } };
            if (occ != null)
                foreach (var o in occ)
                {
                    if (o == null || o.Length < 4 || !(o[2] > 0) || !(o[3] > 0)) continue;
                    var next = new List<double[]>();
                    foreach (var q in cur)
                    {
                        double qx1 = q[0] + q[2], qy1 = q[1] + q[3], ox1 = o[0] + o[2], oy1 = o[1] + o[3];
                        double ix0 = Math.Max(q[0], o[0]), iy0 = Math.Max(q[1], o[1]), ix1 = Math.Min(qx1, ox1), iy1 = Math.Min(qy1, oy1);
                        if (ix0 >= ix1 || iy0 >= iy1) { next.Add(q); continue; }
                        if (iy0 > q[1]) next.Add(new[] { q[0], q[1], q[2], iy0 - q[1] });          // above
                        if (iy1 < qy1) next.Add(new[] { q[0], iy1, q[2], qy1 - iy1 });             // below
                        if (ix0 > q[0]) next.Add(new[] { q[0], iy0, ix0 - q[0], iy1 - iy0 });      // left
                        if (ix1 < qx1) next.Add(new[] { ix1, iy0, qx1 - ix1, iy1 - iy0 });         // right
                    }
                    cur = next;
                }
            cur.RemoveAll(q => q[2] < minSize || q[3] < minSize);
            cur.Sort((x, y) => (y[2] * y[3]).CompareTo(x[2] * x[3]));
            if (cur.Count > maxPieces) cur.RemoveRange(maxPieces, cur.Count - maxPieces);
            return cur;
        }

        /// <summary>Index of the rectangle (x,y,w,h) with the largest positive area, -1 if none. Invisible maps: pass null or w/h 0.</summary>
        public static int PickLargest(IList<double[]> rects)
        {
            int best = -1; double bestArea = 0;
            if (rects == null) return -1;
            for (int i = 0; i < rects.Count; i++)
            {
                double[] q = rects[i];
                if (q == null || q.Length < 4) continue;
                double area = q[2] * q[3];
                if (double.IsNaN(area) || area <= bestArea) continue;
                best = i; bestArea = area;
            }
            return best;
        }

        /// <summary>
        /// T-113: rank of a MapBase candidate. host: "none" (no SeapowerUI.Window above the map), "main" (the nearest window's
        /// DataContext is MainGameViewModel.Map), "other" (any other window: SecondMap, PIP). -1 reject, 0 bare map,
        /// 1 main map window not maximised (the 3D mini view), 2 maximised main map window.
        /// </summary>
        public static int MapRank(bool seen, string host, bool maximized, bool allowSmallWindow)
        {
            if (!seen) return -1;
            if (host == "none") return 0;
            if (host != "main") return -1;
            if (maximized) return 2;
            return allowSmallWindow ? 1 : -1;
        }

        /// <summary>Index with the highest rank (&gt;= 0), ties by largest area; -1 when none qualifies.</summary>
        public static int PickBest(IList<int> ranks, IList<double[]> rects)
        {
            int best = -1, bestRank = -1; double bestArea = 0;
            if (ranks == null || rects == null) return -1;
            for (int i = 0; i < ranks.Count && i < rects.Count; i++)
            {
                double[] q = rects[i];
                if (ranks[i] < 0 || q == null || q.Length < 4) continue;
                double area = q[2] * q[3];
                if (double.IsNaN(area) || area <= 0) continue;
                if (ranks[i] > bestRank || (ranks[i] == bestRank && area > bestArea)) { best = i; bestRank = ranks[i]; bestArea = area; }
            }
            return best;
        }

        /// <summary>Delay before the next full-tree scan after <paramref name="misses"/> failed scans: 2, 4, 8, 8... s.</summary>
        public static double NextScanDelayS(int misses)
        {
            if (misses <= 0) return 2;
            if (misses == 1) return 4;
            return 8;
        }

        /// <summary>
        /// Noesis "screen" coordinates -> IMGUI pixels. The game maps Unity pixels to Noesis by x * k (k = UIScale * Screen.height / 1080),
        /// y measured from the top (MouseOverManager), so the inverse is a division and IMGUI y (also from the top) needs no flip.
        /// scaleOverride &gt; 0 replaces k.
        /// </summary>
        public static void NoesisToPixels(double x, double y, double uiScale, double screenH, double scaleOverride, out double px, out double py)
        {
            double k = scaleOverride > 0 ? scaleOverride : (uiScale > 0 ? uiScale : 1.0) * screenH / 1080.0;
            if (!(k > 0)) k = 1.0;
            px = x / k;
            py = y / k;
        }

        /// <summary>Inverse of <see cref="NoesisToPixels"/>: IMGUI pixels -> Noesis screen units (T-142 W4).</summary>
        public static void PixelsToNoesis(double px, double py, double uiScale, double screenH, double scaleOverride, out double x, out double y)
        {
            double k = scaleOverride > 0 ? scaleOverride : (uiScale > 0 ? uiScale : 1.0) * screenH / 1080.0;
            if (!(k > 0)) k = 1.0;
            x = px * k;
            y = py * k;
        }

        /// <summary>Noesis screen units per pixel from the root view's far corner (sx, sy) and the screen size; 0 = implausible.</summary>
        public static double CalibratePixelScale(double sx, double sy, double screenW, double screenH)
        {
            if (!(screenW > 0) || !(screenH > 0) || !(sx > 0) || !(sy > 0)) return 0;
            double kx = sx / screenW, ky = sy / screenH;
            if (Math.Abs(kx - ky) > 0.05 * Math.Max(kx, ky)) return 0;
            double k = (kx + ky) * 0.5;
            return k >= 0.2 && k <= 5.0 ? k : 0;
        }

        // ---------------------------------------------------------------- self test

        private static string _fail;

        private static void Check(bool ok, string what)
        {
            if (!ok && _fail == null) _fail = what;
        }

        private static void Near(double got, double want, double relTol, string what)
        {
            if (_fail != null) return;
            double tol = Math.Abs(want) * relTol + 1e-9;
            if (double.IsNaN(got) || Math.Abs(got - want) > tol) _fail = what + ": got " + got.ToString("R", CultureInfo.InvariantCulture) + ", want " + want.ToString("R", CultureInfo.InvariantCulture);
        }

        private const double MinPowerKw = 2e-14;

        private static RadarSpec TestRadar(string type)
        {
            // SPS-49-like: UHF, 280 kW, 39 dB, 0.6..463 km, 3..45000 m, air only
            return new RadarSpec
            {
                Globe = true,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                OwnerUid = 1, OwnerName = "Test", Name = "SPS-49", Type = type, Air = true, Surface = false, On = true,
                Lat = 15.0, Lon = 42.0, AntennaM = 30, MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000,
                PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, LookDownMult = 1.0,
                FullCircle = true, HArcMin = -181, HArcMax = 181, VArcMin = -91, VArcMax = 91, VisibleHeightM = 3
            };
        }

        private static CoverageTier T(double h, double rcs, bool air, bool surface)
        {
            return new CoverageTier { HeightM = h, RcsM2 = rcs, Air = air, Surface = surface };
        }

        private static double SignedArea(List<double[]> tris)
        {
            double a = 0;
            foreach (double[] t in tris) a += Math.Abs(((t[2] - t[0]) * (t[5] - t[1]) - (t[4] - t[0]) * (t[3] - t[1])) * 0.5);
            return a;
        }

        /// <summary>Offline checks (tables from the plan, T-066). Returns null if everything is fine, else a description of the first failure.</summary>
        public static string SelfTest()
        {
            _fail = null;
            try { Run(); }
            catch (Exception e) { return "coverage selftest exception: " + e.Message; }
            return _fail == null ? null : "coverage selftest: " + _fail;
        }

        // ---------------------------------------------------------------- T-352 worldflat

        private static bool OldQuick(RayProfile p, double lat, double lon)
        {
            if (p.Visible == null || !(p.RangeM > 0)) return false;
            double dyM = (lat - p.Lat) * Rad * EarthRadiusM;
            double dxM = (lon - p.Lon) * Rad * EarthRadiusM * Math.Cos((lat + p.Lat) * 0.5 * Rad);
            double d2 = dxM * dxM + dyM * dyM;
            if (d2 >= p.RangeM * p.RangeM) return false;
            double b = (Math.Atan2(dxM, dyM) * Deg + 360.0) % 360.0;
            return Lookup(p, b, Math.Sqrt(d2));
        }

        /// <summary>The raster of the code before T-352 (spherical span and prefilter); the globe result must equal it.</summary>
        private static byte[] OldRasterize(IList<RayProfile> profiles, GeoBox box, int w, int h)
        {
            var res = new byte[w * h];
            double dLat = (box.LatMax - box.LatMin) / h, dLon = (box.LonMax - box.LonMin) / w;
            foreach (RayProfile p in profiles)
            {
                if (p.Visible == null || !(p.RangeM > 0) || p.Tier < 0 || p.Tier >= MaxTiers) continue;
                byte bit = (byte)(1 << (p.Active ? p.Tier : 4 + p.Tier));
                double latSpan = p.RangeM / EarthRadiusM * Deg;
                double lonSpan = latSpan / Math.Max(0.01, Math.Cos(p.Lat * Rad));
                int y0 = Math.Max(0, (int)Math.Floor((box.LatMax - (p.Lat + latSpan)) / dLat));
                int y1 = Math.Min(h - 1, (int)Math.Ceiling((box.LatMax - (p.Lat - latSpan)) / dLat));
                int x0 = Math.Max(0, (int)Math.Floor((p.Lon - lonSpan - box.LonMin) / dLon));
                int x1 = Math.Min(w - 1, (int)Math.Ceiling((p.Lon + lonSpan - box.LonMin) / dLon));
                for (int y = y0; y <= y1; y++)
                {
                    double lat = box.LatMax - (y + 0.5) * dLat;
                    for (int x = x0; x <= x1; x++)
                        if (OldQuick(p, lat, box.LonMin + (x + 0.5) * dLon)) res[y * w + x] |= bit;
                }
            }
            return res;
        }

        /// <summary>T-352: the geodesy, the profile, the prefilter and the raster follow the world flag (flat = the game of stable, globe = the beta sphere, bit-identical to the old code).</summary>
        private static void WorldFlat()
        {
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                foreach (double b in new[] { 0.0, 45.0, 90.0, 135.0 })
                    foreach (double d in new[] { 10000.0, 100000.0 })
                    {
                        double a1, o1, a2, o2;
                        Destination(phi, 10, b, d, out a1, out o1); Destination(phi, 10, b, d, true, out a2, out o2);
                        Check(a1 == a2 && o1 == o2, "worldflat Destination globe == old " + phi + "/" + b + "/" + d);
                        double b1, d1, b2, d2;
                        BearingDistance(phi, 10, a1, o1, out b1, out d1); BearingDistance(phi, 10, a1, o1, true, out b2, out d2);
                        Check(b1 == b2 && d1 == d2, "worldflat BearingDistance globe == old " + phi + "/" + b + "/" + d);
                        foreach (bool globe in new[] { false, true })
                        {
                            double la, lo, bb, dd;
                            Destination(phi, 10, b, d, globe, out la, out lo);
                            BearingDistance(phi, 10, la, lo, globe, out bb, out dd);
                            Check(Math.Abs(dd - d) < 1e-6 * Math.Max(1.0, d) && Math.Abs(Wrap180(bb - b)) < 1e-6, "worldflat inverse globe=" + globe + " " + phi + "/" + b + "/" + d + ": " + bb + "," + dd);
                        }
                    }
                double fla, flo;
                Destination(phi, 10, 90, 60 * MPerNm, false, out fla, out flo);
                Near(flo - 10.0, 1.0, 1e-12, "worldflat flat 60 nm east = 1 deg at " + phi);
                double brg, dist;
                BearingDistance(phi, 10, phi + 0.1, 10.1, false, out brg, out dist);
                Near(dist / MPerNm, 8.4853, 1e-5, "worldflat flat dist at " + phi);
                Near(brg, 45.0, 1e-9, "worldflat flat bearing at " + phi);
            }
            // a profile on the open sea at 60N
            foreach (bool globe in new[] { false, true })
            {
                RadarSpec r = TestRadar("Search"); r.Lat = 60; r.Lon = 10; r.Globe = globe;
                RayProfile p = BuildProfile(r, T(15, 1, true, false), 0, MinPowerKw, null, 180, 64);
                Check(p.Globe == globe && p.RangeM > 0, "worldflat profile frame");
                double la, lo;
                Destination(60, 10, 90, 0.9 * p.RangeM, globe, out la, out lo);
                Check(Covered(p, la, lo), "worldflat 0.9 R east covered globe=" + globe);
                Destination(60, 10, 90, 1.1 * p.RangeM, globe, out la, out lo);
                Check(!Covered(p, la, lo), "worldflat 1.1 R east not covered globe=" + globe);
                if (!globe)
                {   // the same point on the sphere (0.9 R true) is 1.8 R away on the flat map
                    Destination(60, 10, 90, 0.9 * p.RangeM, true, out la, out lo);
                    Check(!Covered(p, la, lo), "worldflat flat profile ignores the sphere");
                }
                // the prefilter agrees with the exact test
                int mism = 0;
                for (int iy = 0; iy < 40; iy++)
                    for (int ix = 0; ix < 40; ix++)
                    {
                        double sx = (ix + 0.5) / 40.0 * 2.4 - 1.2, sy = (iy + 0.5) / 40.0 * 2.4 - 1.2;
                        double dla = sy * p.RangeM / (globe ? EarthRadiusM * Rad : FlatMPerDeg);
                        double dlo = sx * p.RangeM / (globe ? EarthRadiusM * Rad * Math.Cos(60 * Rad) : FlatMPerDeg);
                        if (Covered(p, 60 + dla, 10 + dlo) != CoveredQuick(p, 60 + dla, 10 + dlo)) mism++;
                    }
                Check(globe ? mism <= 8 : mism == 0, "worldflat CoveredQuick == Covered globe=" + globe + " mismatches " + mism);
                // the raster
                var list = new List<RayProfile> { p };
                GeoBox box = Bounds(list, 0.1);
                byte[] ras = Rasterize(list, box, 128, 128);
                if (globe) Check(ArraysEqual(ras, OldRasterize(list, box, 128, 128)), "worldflat raster globe == old code");
                else
                {
                    int xMin = 128, xMax = -1, yMin = 128, yMax = -1;
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 128; x++)
                            if (ras[y * 128 + x] != 0) { xMin = Math.Min(xMin, x); xMax = Math.Max(xMax, x); yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y); }
                    double wDeg = (xMax - xMin + 1) * (box.LonMax - box.LonMin) / 128, hDeg = (yMax - yMin + 1) * (box.LatMax - box.LatMin) / 128;
                    Check(xMax >= 0 && Math.Abs(wDeg - hDeg) <= 2.0 * Math.Max((box.LonMax - box.LonMin) / 128, (box.LatMax - box.LatMin) / 128), "worldflat flat raster is square in degrees: " + wDeg + " x " + hDeg);
                }
            }
            RadarSpec ma = TestRadar("Search"), mb = TestRadar("Search");
            ma.Lat = mb.Lat = 60; ma.Lon = mb.Lon = 10; ma.Globe = false; mb.Globe = false;
            Check(!Moved(ma, mb), "worldflat Moved same world");
            mb.Globe = true;
            Check(Moved(ma, mb), "worldflat Moved other world");
        }

        private static bool ArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Run()
        {
            string err;
            WorldFlat();
            // --- R1 horizon / equation / look-down
            Near(HorizonM(30, 15, 0, false), 38540, 0.005, "horizon 30+15");
            Near(HorizonM(30, 18, 0, false), 40070, 0.005, "horizon 30+18");
            Near(HorizonM(30, 153, 0, false), 73560, 0.005, "horizon 30+153");
            Near(HorizonM(30, 3003, 0, false), 248500, 0.005, "horizon 30+3003");
            Near(HorizonM(30, 18, 0, true), (Math.Sqrt(12742.018 * 0.030) + Math.Sqrt(12742.018 * 0.018)) * 1000, 1e-9, "horizon visual");
            Near(HorizonM(-5, -3, 0, false), 0, 0, "horizon negative");
            Near(HorizonM(30, 18, 10, false), (Math.Sqrt(16989.357 * 0.020) + Math.Sqrt(16989.357 * 0.008)) * 1000, 1e-9, "horizon terrain");
            Near(EquationRangeM(280, Math.Pow(10, 3.9), 0.461, 10, 2e-14), 175000, 0.01, "equation s=10");
            Near(EquationRangeM(280, Math.Pow(10, 3.9), 0.461, 100, 2e-14), 312000, 0.01, "equation s=100");
            Check(InLookDown(7600, HorizonM(7600, 153, 0, false), 50000, 153), "lookdown aircraft");
            Check(!InLookDown(30, HorizonM(30, 153, 0, false), 50000, 153), "lookdown ship");
            {
                double hz = HorizonM(7600, 153, 0, false), edge = LookDownEdgeM(7600, hz, 153);
                Check(edge > 0 && InLookDown(7600, hz, edge * 0.99, 153) && !InLookDown(7600, hz, edge * 1.01, 153), "lookdown edge");
            }
            var tiers = ParseTiers(DefaultTiers, out err);
            Check(tiers != null && err == null && tiers.Count == 4, "default tiers");
            if (tiers != null && tiers.Count == 4)
            {
                Check(tiers[0].HeightM == 15 && tiers[0].RcsM2 == 1 && tiers[0].Air && !tiers[0].Surface, "tier 0");
                Check(tiers[2].HeightM == 3000 && tiers[2].RcsM2 == 10, "tier 2");
                Check(tiers[3].Surface && !tiers[3].Air && tiers[3].RcsM2 == 50, "tier 3 (surface target)");
            }
            var mixed = ParseTiers(" 15 : 1 : air+surface , 150:0.5:air ", out err);
            Check(mixed != null && mixed.Count == 2 && mixed[0].Air && mixed[0].Surface && mixed[1].RcsM2 == 0.5, "tiers whitespace/mixed");
            foreach (string bad in new[] { "", "   ", "15:1:air,150:1:air,3000:10:air,15:50:surface,1:1:air", "x:1:air", "15:-1:air", "15:0:air", "15:1:sub", "15:1", "15:1:", "-5:1:air" })
            {
                Check(ParseTiers(bad, out err) == null && err != null, "bad tiers '" + bad + "'");
            }
            string[] hk = ParseHotkey("LeftAlt+R", out err);
            Check(hk != null && hk.Length == 2 && hk[0] == "LeftAlt" && hk[1] == "R", "hotkey LeftAlt+R");
            hk = ParseHotkey("F8", out err);
            Check(hk != null && hk.Length == 1 && hk[0] == "F8", "hotkey F8");
            foreach (string bad in new[] { "", " ", "R+LeftAlt", "LeftAlt+", "LeftAlt", "A+B", "Left Alt+R", "LeftAlt+R+Q+W+E" })
                Check(ParseHotkey(bad, out err) == null && err != null, "bad hotkey '" + bad + "'");

            // --- R2 RangeLimit / InSector
            RadarSpec sps49 = TestRadar("Search");
            TierLimit lim;
            lim = RangeLimit(sps49, T(15, 1, true, false), MinPowerKw);
            Check(lim.Limit == "horizon", "sps49 15m horizon: " + lim.Limit);
            Near(lim.RangeM, HorizonM(30, 18, 0, false), 1e-9, "sps49 15m range");
            lim = RangeLimit(sps49, T(150, 1, true, false), MinPowerKw);
            Check(lim.Limit == "horizon", "sps49 150m horizon");
            Near(lim.RangeM / MPerNm, 39.5 * (HorizonM(30, 153, 0, false) / HorizonM(30, 150, 0, false)), 0.02, "sps49 150m nm");
            lim = RangeLimit(sps49, T(3000, 10, true, false), MinPowerKw);
            Check(lim.Limit == "equation", "sps49 3000m equation: " + lim.Limit);
            Near(lim.RangeM / MPerNm, 95.0, 0.01, "sps49 3000m nm");
            lim = RangeLimit(sps49, T(15, 50, false, true), MinPowerKw);
            Check(lim.Limit == "role" && lim.RangeM == 0, "sps49 on surface tier = role");
            RadarSpec hi = TestRadar("Search"); hi.MinAltM = 30;
            lim = RangeLimit(hi, T(15, 1, true, false), MinPowerKw);
            Check(lim.Limit == "altitude" && lim.RangeM == 0, "min altitude blocks 15m");
            lim = RangeLimit(hi, T(150, 1, true, false), MinPowerKw);
            Check(lim.RangeM > 0, "min altitude passes 150m");
            RadarSpec hiMax = TestRadar("Search"); hiMax.MaxAltM = 2000;
            Check(RangeLimit(hiMax, T(3000, 10, true, false), MinPowerKw).Limit == "altitude", "max altitude blocks 3000m");
            // SPS-10: surface, C-band 500 kW 35 dB, max 74.1 km
            RadarSpec sps10 = new RadarSpec
            {
                Name = "SPS-10", Type = "Search", Air = false, Surface = true, On = true, Lat = 15, Lon = 42, AntennaM = 30, MinRangeM = 400, MaxRangeM = 74100,
                MinAltM = 0, MaxAltM = 0, PeakPowerKw = 500, GainFactor = Math.Pow(10, 3.5), WaveLengthM = 0.049965408, FullCircle = true, VisibleHeightM = 3
            };
            lim = RangeLimit(sps10, T(15, 50, false, true), MinPowerKw);
            Check(lim.Limit != "role" && lim.RangeM > 0, "sps10 surface tier");
            Check(RangeLimit(sps10, T(3000, 10, true, false), MinPowerKw).Limit == "role", "sps10 air tier = role");
            RadarSpec strong = TestRadar("Search"); strong.Air = false; strong.Surface = true; strong.PeakPowerKw = 5000; strong.MinAltM = 0; strong.MaxAltM = 0; strong.AntennaM = 300;
            strong.MaxRangeM = 74100;
            Check(RangeLimit(strong, T(15, 50, false, true), MinPowerKw).Limit == "max_range", "max_range limit");
            Near(RangeLimit(strong, T(15, 50, false, true), MinPowerKw).RangeM, 74100, 1e-9, "max_range value");
            // sea state shrinks surface range only
            RadarSpec rough = TestRadar("Search"); rough.Air = true; rough.Surface = true; rough.AntennaM = 300; rough.PeakPowerKw = 20; rough.MaxRangeM = 463000;
            double calm = RangeLimit(rough, T(15, 1, false, true), MinPowerKw).RangeM;
            rough.SeaState = 4;
            double choppy = RangeLimit(rough, T(15, 1, false, true), MinPowerKw).RangeM;
            Check(choppy < calm, "sea state shrinks surface range");
            Near(RangeLimit(rough, T(15, 1, true, false), MinPowerKw).RangeM, RangeLimit(TestRadarLike(rough, 0), T(15, 1, true, false), MinPowerKw).RangeM, 1e-9, "sea state ignores air");
            Check(RangeLimit(new RadarSpec { Air = true, Inoperable = true }, T(15, 1, true, false), MinPowerKw).Limit == "off", "inoperable = off");
            // look-down fighter radar: multiplier 0.2
            RadarSpec fighter = TestRadar("DirectedSearch"); fighter.AntennaM = 7600; fighter.LookDownMult = 0.2; fighter.MaxRangeM = 30000; fighter.MinAltM = 10; fighter.PeakPowerKw = 800;
            lim = RangeLimit(fighter, T(150, 1, true, false), MinPowerKw);
            Check(lim.Limit == "lookdown" && lim.RangeM <= 0.2 * 30000 + 1e-6, "fighter look-down");
            // sector
            RadarSpec sec = TestRadar("DirectedSearch"); sec.FullCircle = false; sec.AxisDeg = 90; sec.HArcMin = -30; sec.HArcMax = 30; sec.VArcMin = -90; sec.VArcMax = 90;
            Check(InSector(sec, 100, 10000, 30), "sector 100 in");
            Check(!InSector(sec, 130, 10000, 30), "sector 130 out");
            Check(InSector(sec, 61, 10000, 30) && !InSector(sec, 59, 10000, 30), "sector edge");
            sec.AxisDeg = 350; Check(InSector(sec, 10, 10000, 30) && !InSector(sec, 40, 10000, 30), "sector wrap axis");
            RadarSpec cone = TestRadar("DirectedSearch"); cone.FullCircle = false; cone.Cone = true; cone.AxisDeg = 0; cone.HArcMin = -130; cone.HArcMax = 130; cone.VArcMin = -130; cone.VArcMax = 130;
            Check(InSector(cone, 120, 10000, 30) && !InSector(cone, 180, 10000, 30), "cone 130");
            Check(InSector(TestRadar("Search"), 200, 10000, 30), "full circle");
            RadarSpec vert = TestRadar("DirectedSearch"); vert.AntennaM = 7600; vert.FullCircle = false; vert.HArcMin = -60; vert.HArcMax = 60; vert.VArcMin = -12; vert.VArcMax = 12;
            Check(!InSector(vert, 0, 2000, 18), "low target under 12 deg vertical arc");
            Check(InSector(vert, 0, 50000, 18), "same target far inside vertical arc");
            RadarSpec rear = TestRadar("DirectedSearch"); rear.FullCircle = false; rear.HArcMin = 150; rear.HArcMax = -150; rear.VArcMin = -90; rear.VArcMax = 90;
            Check(InSector(rear, 180, 10000, 30) && !InSector(rear, 0, 10000, 30), "wrapped arc (min > max)");

            // --- R3 profile + terrain
            RadarSpec ship = TestRadar("Search"); ship.Air = true; ship.Surface = true; ship.MinRangeM = 5000;
            double isLat, isLon; Destination(ship.Lat, ship.Lon, 0, 10000, out isLat, out isLon);
            Func<double, double, double> island = (la, lo) =>
            {
                double b, d; BearingDistance(isLat, isLon, la, lo, out b, out d);
                return d <= 2000 ? 200.0 : -50.0;
            };
            RayProfile low = BuildProfile(ship, T(15, 1, true, true), 0, MinPowerKw, island, 180, 64);
            RayProfile lowFree = BuildProfile(ship, T(15, 1, true, true), 0, MinPowerKw, null, 180, 64);
            RayProfile high = BuildProfile(ship, T(3000, 10, true, false), 2, MinPowerKw, island, 180, 64);
            double la1, lo1;
            Destination(ship.Lat, ship.Lon, 0, 25000, out la1, out lo1);
            Check(!Covered(low, la1, lo1), "island shadows 15m behind it");
            Check(Covered(lowFree, la1, lo1), "no terrain: no shadow");
            Destination(ship.Lat, ship.Lon, 90, 25000, out la1, out lo1);
            Check(Covered(low, la1, lo1), "15m visible off the island axis");
            Destination(ship.Lat, ship.Lon, 0, 40000, out la1, out lo1);
            Check(Covered(high, la1, lo1), "3000m not shadowed behind island");
            Destination(ship.Lat, ship.Lon, 0, 25000, out la1, out lo1);
            Check(Covered(high, la1, lo1), "3000m behind island");
            Destination(ship.Lat, ship.Lon, 90, 2500, out la1, out lo1);
            Check(!Covered(low, la1, lo1), "MinRange dead zone");
            Destination(ship.Lat, ship.Lon, 90, 8000, out la1, out lo1);
            Check(Covered(low, la1, lo1), "outside MinRange");
            Check(low.ShadowPct > 0 && lowFree.ShadowPct == 0 && high.ShadowPct < 1.0 && high.ShadowPct < low.ShadowPct, "shadow pct: " + low.ShadowPct + "/" + high.ShadowPct);
            RayProfile nanProf = BuildProfile(ship, T(15, 1, true, true), 0, MinPowerKw, (la, lo) => double.NaN, 180, 64);
            Destination(ship.Lat, ship.Lon, 0, 25000, out la1, out lo1);
            Check(Covered(nanProf, la1, lo1) && nanProf.ShadowPct == 0, "NaN terrain is not an obstacle");
            RayProfile sea = BuildProfile(ship, T(15, 1, true, true), 0, MinPowerKw, (la, lo) => -100.0, 180, 64);
            Check(Covered(sea, la1, lo1) && sea.ShadowPct == 0, "open sea has no shadow");
            // a sector radar covers only its sector
            RadarSpec fwd = TestRadar("DirectedSearch"); fwd.FullCircle = false; fwd.AxisDeg = 90; fwd.HArcMin = -30; fwd.HArcMax = 30; fwd.VArcMin = -90; fwd.VArcMax = 90; fwd.MinRangeM = 0; fwd.MinAltM = -1;
            RayProfile fp = BuildProfile(fwd, T(150, 1, true, false), 1, MinPowerKw, null, 180, 64);
            Destination(fwd.Lat, fwd.Lon, 100, 15000, out la1, out lo1); Check(Covered(fp, la1, lo1), "sector profile inside");
            Destination(fwd.Lat, fwd.Lon, 130, 15000, out la1, out lo1); Check(!Covered(fp, la1, lo1), "sector profile outside");
            // inactive / role-mismatched
            RadarSpec off = TestRadar("Search"); off.Emcon = true;
            Check(BuildProfile(off, T(150, 1, true, false), 1, MinPowerKw, null, 90, 32).Active, "Emcon as the game: an emitting radar is active");
            off.On = false;
            Check(!BuildProfile(off, T(150, 1, true, false), 1, MinPowerKw, null, 90, 32).Active, "radar off -> profile inactive");
            Check(IsEmpty(BuildProfile(sps49, T(15, 50, false, true), 3, MinPowerKw, null, 90, 32)), "role mismatch gives empty profile");

            // --- R4 bounds / raster / outlines
            RadarSpec a = TestRadar("Search"), b = TestRadar("Search"); b.Name = "SPS-49b"; b.OwnerUid = 2;
            Destination(a.Lat, a.Lon, 90, 10 * MPerNm, out b.Lat, out b.Lon);
            var tier = T(150, 1, true, false);
            var pa = BuildProfile(a, tier, 1, MinPowerKw, null, 180, 64);
            var pb = BuildProfile(b, tier, 1, MinPowerKw, null, 180, 64);
            var plist = new List<RayProfile> { pa, pb };
            GeoBox box = Bounds(plist, 0.1);
            Check(box.LatMax > a.Lat && box.LatMin < a.Lat && box.LonMax > b.Lon && box.LonMin < a.Lon, "bounds contain centres");
            byte[] ras = Rasterize(plist, box, 64, 64);
            double mLat = a.Lat, mLon = (a.Lon + b.Lon) * 0.5;
            int mx = (int)((mLon - box.LonMin) / (box.LonMax - box.LonMin) * 64), my = (int)((box.LatMax - mLat) / (box.LatMax - box.LatMin) * 64);
            Check((ras[my * 64 + mx] & 2) != 0, "raster middle has tier 1 bit");
            Check((ras[0] & 0xFF) == 0, "raster corner empty");
            var segs = Outlines(plist);
            Check(segs.Count > 100, "outline has segments");
            bool innerSeg = false;
            foreach (GeoSeg sg in segs)
            {
                double ml = (sg.Lat1 + sg.Lat2) * 0.5, mo = (sg.Lon1 + sg.Lon2) * 0.5;
                double bb, dd;
                BearingDistance(pa.Lat, pa.Lon, ml, mo, out bb, out dd); if (dd < pa.RangeM - 2.5 * pa.BinM && dd > 3 * pa.BinM) { BearingDistance(pb.Lat, pb.Lon, ml, mo, out bb, out dd); if (dd < pb.RangeM - 2.5 * pb.BinM) innerSeg = true; }
                BearingDistance(pb.Lat, pb.Lon, ml, mo, out bb, out dd); if (dd < pb.RangeM - 2.5 * pb.BinM && dd > 3 * pb.BinM) { BearingDistance(pa.Lat, pa.Lon, ml, mo, out bb, out dd); if (dd < pa.RangeM - 2.5 * pa.BinM) innerSeg = true; }
                if (sg.Style != 0 || sg.Tier != 1) innerSeg = true;
            }
            Check(!innerSeg, "union outline has no segment inside the other circle");
            var segsAlone = Outlines(new List<RayProfile> { pa });
            Check(segsAlone.Count > segs.Count / 2 && segsAlone.Count <= 360 + 4, "single outline size " + segsAlone.Count);
            RadarSpec c = TestRadar("Search"); c.On = false; c.Name = "off"; c.Airborne = false;
            var pc = BuildProfile(c, tier, 1, MinPowerKw, null, 90, 32);
            var rasC = Rasterize(new List<RayProfile> { pc }, Bounds(new List<RayProfile> { pc }, 0.1), 32, 32);
            Check(rasC[16 * 32 + 16] == (1 << 5), "inactive raster bit 4+tier");
            var sgC = Outlines(new List<RayProfile> { pc });
            Check(sgC.Count > 0 && sgC[0].Style == 1, "inactive outline style 1");
            RadarSpec air = TestRadar("Search"); air.Airborne = true;
            var sgA = Outlines(new List<RayProfile> { BuildProfile(air, tier, 1, MinPowerKw, null, 90, 32) });
            Check(sgA.Count > 0 && sgA[0].Style == 2, "airborne outline style 2");
            // MinRange ring + sector edges
            RayProfile ring = BuildProfile(ship, T(150, 1, true, false), 1, MinPowerKw, null, 90, 64);
            Check(Outlines(new List<RayProfile> { ring }).Count > 90, "min range ring adds segments");
            var fsegs = Outlines(new List<RayProfile> { fp });
            Check(fsegs.Count > 0 && fsegs.Count < 100, "sector outline");


            // --- G2/G3/O1 helpers
            {
                RadarSpec sp = SpecFromRaw("SPS-49", "Search", 2, true, 0.6 * 1000 * UnityPerM, 463 * 1000 * UnityPerM, 3 * UnityPerM, 45000 * UnityPerM, 280, 7943, 0.461,
                                           1.0, 0.0, -181, 181, -91, 91, 0, 1, 30 * UnityPerM, 3 * UnityPerM);
                Near(sp.MinRangeM, 600, 1e-5, "raw min range"); Near(sp.MaxRangeM, 463000, 1e-5, "raw max range");
                Near(sp.AntennaM, 30, 1e-5, "raw antenna"); Near(sp.VisibleHeightM, 3, 1e-5, "raw visible");
                Check(sp.Air && !sp.Surface && sp.FullCircle && Math.Abs(sp.AxisDeg) < 1e-9, "raw role/full circle/axis north");
                Near(SpecFromRaw("x", "Search", 1, false, 0, 1, -1, -1, 1, 1, 1, 1, 0, -181, 181, -91, 91, 1, 0, 0, 0).AxisDeg, 90, 1e-9, "raw axis east");
                RadarSpec rot = SpecFromRaw("x", "Search", 3, true, 0, 1, 0, 0, 1, 1, 1, 1, 0, -30, 30, -12, 12, 0, 1, 0, 0);
                Check(!rot.FullCircle && rot.HArcMin == -181 && rot.VArcMax == 12 && rot.Air && rot.Surface, "rotating antenna: full horizon, vertical arc kept");
                RadarSpec cn = SpecFromRaw("x", "DirectedSearch", 1, false, 0, 1, 0, 0, 1, 1, 1, 1, 0, -130, 130, -130, 130, 0, 1, 0, 0);
                Check(cn.Cone && !cn.FullCircle, "single ViewArcs number = cone");
                RadarSpec wedge = SpecFromRaw("x", "DirectedSearch", 2, false, 0, 1, -1, -1, 1, 1, 1, 0.2, 0, -30, 30, -12, 12, 0, 1, 0, 0);
                Check(!wedge.Cone && !wedge.FullCircle && wedge.MinAltM < 0 && wedge.MaxAltM < 0, "directed arcs, altitude not set");
                Check(SpecFromRaw("x", "Search", 0, true, 0, 1, 0, 0, 1, 1, 1, 1, 0, -181, 181, -91, 91, 0, 1, 0, 0).Air == false, "undefined role sees nothing");
            }
            {
                RadarSpec s1 = TestRadar("Search"), s2 = TestRadar("Search");
                Check(!SpecChanged(s1, s2), "identical specs");
                Destination(s1.Lat, s1.Lon, 45, 0.5, out s2.Lat, out s2.Lon);
                Check(!SpecChanged(s1, s2), "0.5 m shift is not a change");
                Destination(s1.Lat, s1.Lon, 45, 1.5, out s2.Lat, out s2.Lon);
                Check(SpecChanged(s1, s2) && Moved(s1, s2) && !RadarStateChanged(s1, s2), "1.5 m shift is a motion change");
                Destination(s1.Lat, s1.Lon, 45, 0.1 * MPerNm, out s2.Lat, out s2.Lon);
                Check(SpecChanged(s1, s2), "0.1 nm shift is a change");
                s2 = TestRadar("Search"); s2.AxisDeg = 90;
                Check(!SpecChanged(s1, s2), "axis turn of a full-circle radar is not a change");
                s1.FullCircle = false; s2.FullCircle = false; s1.HArcMin = -30; s1.HArcMax = 30; s2.HArcMin = -30; s2.HArcMax = 30;
                Check(SpecChanged(s1, s2), "axis turn of a sector radar is a change");
                s2.AxisDeg = 1; Check(!SpecChanged(s1, s2), "1 degree is not a change");
                s2.AxisDeg = 3; Check(Moved(s1, s2) && !RadarStateChanged(s1, s2), "3 degrees of a sector radar is motion");
                s2 = TestRadar("Search"); s2.On = false; s1 = TestRadar("Search");
                Check(SpecChanged(s1, s2) && RadarStateChanged(s1, s2) && !Moved(s1, s2), "IsOn is a state change");
                s2 = TestRadar("Search"); s2.Emcon = true; Check(RadarStateChanged(s1, s2) && !Moved(s1, s2), "EMCON is a state change");
                s2 = TestRadar("Search"); s2.AntennaM = 31; Check(!SpecChanged(s1, s2), "1 m antenna drift is not a change");
                s2 = TestRadar("Search"); s2.AntennaM = 7000; Check(SpecChanged(s1, s2), "antenna height jump is a change");
                s2 = TestRadar("Search"); s2.SeaState = 3; Check(SpecChanged(s1, s2), "sea state is a change");
                Check(SpecChanged(null, s2) && !SpecChanged(null, null), "null handling");
            }
            {
                var rects = new List<double[]> { new[] { 0.0, 0, 100, 50 }, null, new[] { 10.0, 10, 400, 300 }, new[] { 0.0, 0, 0, 0 }, new[] { 0.0, 0, double.NaN, 4 } };
                Check(PickLargest(rects) == 2, "PickLargest");
                var runs = RasterRuns(new byte[] { 0, 0, 1, 1, 0, 2, 2, 2 }, 4, 2, v => v == 1 ? 0f : (v == 2 ? 0.5f : 0.35f));
                Check(runs.Count == 3 && runs[0].X0 == 0 && runs[0].X1 == 1 && runs[1].Row == 1 && runs[1].X1 == 0 && runs[2].X0 == 1 && runs[2].X1 == 3, "RasterRuns");
                var pcs = SubtractRects(new[] { 0.0, 0, 100, 100 }, new List<double[]> { new[] { 0.0, 0, 30, 30 } }, 1, 8);
                double area = 0; foreach (var q in pcs) area += q[2] * q[3];
                Near(area, 100 * 100 - 30 * 30, 1e-9, "SubtractRects corner area");
                Check(pcs.Count == 2, "SubtractRects corner pieces");
                Check(SubtractRects(new[] { 0.0, 0, 100, 100 }, new List<double[]> { new[] { -10.0, -10, 200, 200 } }, 1, 8).Count == 0, "SubtractRects covered");
                pcs = SubtractRects(new[] { 0.0, 0, 100, 100 }, new List<double[]> { new[] { 40.0, 40, 20, 20 }, null, new[] { 200.0, 0, 5, 5 } }, 1, 8);
                area = 0; foreach (var q in pcs) area += q[2] * q[3];
                Near(area, 100 * 100 - 20 * 20, 1e-9, "SubtractRects hole area");
                Check(SubtractRects(new[] { 0.0, 0, 100, 100 }, new List<double[]> { new[] { 0.0, 0, 97, 100 } }, 8, 8).Count == 0, "SubtractRects thin dropped");
                Check(PickLargest(new List<double[]>()) == -1 && PickLargest(null) == -1 && PickLargest(new List<double[]> { new[] { 0.0, 0, 0, 0 } }) == -1, "PickLargest none");
                double px, py;
                NoesisToPixels(1920, 1080, 1.0, 1080, 0, out px, out py);
                Near(px, 1920, 1e-9, "noesis 1080p x"); Near(py, 1080, 1e-9, "noesis 1080p y");
                NoesisToPixels(3840, 2160, 1.0, 1440, 0, out px, out py);
                Near(px, 3840 / (1440.0 / 1080.0), 1e-9, "noesis 1440p x"); Near(py, 2160 / (1440.0 / 1080.0), 1e-9, "noesis 1440p y");
                NoesisToPixels(1000, 500, 1.5, 1080, 0, out px, out py);
                Near(px, 1000 / 1.5, 1e-9, "noesis UIScale 1.5");
                NoesisToPixels(1000, 500, 1.5, 1080, 2.0, out px, out py);
                Near(px, 500, 1e-9, "noesis override");
                foreach (var cs in new[] { new[] { 1.0, 1080, 0 }, new[] { 1.0, 1440, 0 }, new[] { 1.5, 1080, 0 }, new[] { 1.0, 1080, 2.0 } })
                {
                    double nx, ny, bx, by;
                    PixelsToNoesis(812.5, 311.25, cs[0], cs[1], cs[2], out nx, out ny);
                    NoesisToPixels(nx, ny, cs[0], cs[1], cs[2], out bx, out by);
                    Near(bx, 812.5, 1e-9, "pixels->noesis->pixels x"); Near(by, 311.25, 1e-9, "pixels->noesis->pixels y");
                }
                Near(CalibratePixelScale(2560, 1440, 2560, 1440), 1.0, 1e-9, "calibrate pixels");
                Near(CalibratePixelScale(1920, 1080, 2560, 1440), 0.75, 1e-9, "calibrate units");
                Check(CalibratePixelScale(2560, 300, 2560, 1440) == 0 && CalibratePixelScale(0, 0, 2560, 1440) == 0 && CalibratePixelScale(1e5, 5.6e4, 2560, 1440) == 0, "calibrate implausible");
            }

            // --- R5 affine / fog / geodesy
            {
                double ang = 30 * Rad, s = 2.5;
                double[] lat3 = { 10.0, 10.5, 10.2 }, lon3 = { 40.0, 40.1, 40.7 };
                double[] x3 = new double[3], y3 = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    double u = (lon3[i] - 40.0) * s * 1000, v = (lat3[i] - 10.0) * s * 1000;
                    x3[i] = 300 + Math.Cos(ang) * u - Math.Sin(ang) * v;
                    y3[i] = 200 - (Math.Sin(ang) * u + Math.Cos(ang) * v);
                }
                bool ok;
                Affine2 af = FitAffine(lat3, lon3, x3, y3, out ok);
                Check(ok, "affine ok");
                double qx, qy, la4 = 10.33, lo4 = 40.4;
                af.Apply(la4, lo4, out qx, out qy);
                double u4 = (lo4 - 40.0) * s * 1000, v4 = (la4 - 10.0) * s * 1000;
                Near(qx, 300 + Math.Cos(ang) * u4 - Math.Sin(ang) * v4, 1e-6, "affine x");
                Near(qy, 200 - (Math.Sin(ang) * u4 + Math.Cos(ang) * v4), 1e-6, "affine y");
                FitAffine(new[] { 10.0, 10.1, 10.2 }, new[] { 40.0, 40.1, 40.2 }, x3, y3, out ok);
                Check(!ok, "collinear points rejected");
                FitAffine(new[] { 10.0, 10.0, 10.0 }, new[] { 40.0, 40.0, 40.0 }, x3, y3, out ok);
                Check(!ok, "coincident points rejected");
            }
            {
                double[] rect = { 0, 0, 100, 100 };
                var sq = new List<double[]> { new[] { 20.0, 20 }, new[] { 60.0, 20 }, new[] { 60.0, 60 }, new[] { 20.0, 60 } };
                Near(SignedArea(FogOutsidePoly(rect, sq)), SignedArea(FogOutside(rect, Quad(20, 20, 60, 20, 60, 60, 20, 60))), 1e-9, "fog poly = quad");
                var oct = new List<double[]>();
                for (int k = 0; k < 8; k++) oct.Add(new[] { 50 + 30 * Math.Cos(k * Math.PI / 4), 50 + 30 * Math.Sin(k * Math.PI / 4) });
                Near(SignedArea(FogOutsidePoly(rect, oct)), 10000 - 0.5 * 8 * 900 * Math.Sin(Math.PI / 4), 1e-6, "fog octagon");
                var conc = new List<double[]> { new[] { 20.0, 20 }, new[] { 80.0, 20 }, new[] { 50.0, 40 }, new[] { 80.0, 80 }, new[] { 20.0, 80 } };
                Near(SignedArea(FogOutsidePoly(rect, conc)), 10000 - 60 * 60, 1e-6, "fog concave = hull");
                Near(SignedArea(FogOutsidePoly(rect, null)), 10000, 1e-9, "fog poly null");
                Near(SignedArea(FogOutside(rect, Quad(20, 20, 60, 20, 60, 60, 20, 60))), 10000 - 1600, 1e-9, "fog quad inside");
                Near(SignedArea(FogOutside(rect, Quad(60, 20, 140, 20, 140, 60, 60, 60))), 10000 - 40 * 40, 1e-9, "fog quad partly outside");
                Near(SignedArea(FogOutside(rect, Quad(200, 200, 300, 200, 300, 300, 200, 300))), 10000, 1e-9, "fog quad outside");
                Near(SignedArea(FogOutside(rect, Quad(-50, -50, 150, -50, 150, 150, -50, 150))), 0, 1e-9, "fog quad covers all");
                // rotated quad (diamond) around the centre, CW order too
                double[][] dia = Quad(50, 10, 90, 50, 50, 90, 10, 50);
                Near(SignedArea(FogOutside(rect, dia)), 10000 - 3200, 1e-9, "fog diamond");
                var rev = new[] { dia[3], dia[2], dia[1], dia[0] };
                Near(SignedArea(FogOutside(rect, rev)), 10000 - 3200, 1e-9, "fog diamond reversed");
                var rot = Quad(80, 50, 130, 100, 80, 150, 30, 100);   // diamond centred on (80,100), half outside the bottom
                var fog = FogOutside(rect, rot);
                // diamond (half-diagonal 50, centre (80,100)) clipped by y<=100 (2500) and x<=100 (minus triangle 30*30/2 = 450) = 2050
                Near(SignedArea(fog), 10000 - 2050, 1e-9, "fog rotated half outside");
                // no overlap: sample grid
                int overlap = 0, miss = 0;
                for (int ix = 0; ix < 50; ix++)
                    for (int iy = 0; iy < 50; iy++)
                    {
                        double px = (ix + 0.5) * 2 + 0.013, py = (iy + 0.5) * 2 + 0.007;
                        int n = 0;
                        foreach (double[] tr in fog) if (InTri(tr, px, py)) n++;
                        bool inQuad = Math.Abs(px - 80) + Math.Abs(py - 100) < 50;
                        if (n > 1) overlap++;
                        if (!inQuad && n == 0 && Math.Abs(Math.Abs(px - 80) + Math.Abs(py - 100) - 50) > 0.1) miss++;
                        if (inQuad && n != 0 && Math.Abs(Math.Abs(px - 80) + Math.Abs(py - 100) - 50) > 0.1) miss++;
                    }
                Check(overlap == 0 && miss == 0, "fog triangles disjoint and exact: overlap=" + overlap + " miss=" + miss);
                Near(SignedArea(FogOutside(rect, null)), 10000, 1e-9, "fog without quad");
                Near(SignedArea(FogOutside(rect, Quad(10, 10, 10, 10, 10, 10, 10, 10))), 10000, 1e-9, "fog degenerate quad");
            }
            {
                double la2, lo2, brg, dist;
                Destination(0, 0, 0, 60 * MPerNm, out la2, out lo2);
                Near(la2, 1.0, 0.005, "1 deg lat = 60 nm");
                Near(lo2, 0.0, 0, "north keeps lon");
                BearingDistance(10, 20, 11, 20, out brg, out dist);
                Near(dist / MPerNm, 60.0, 0.005, "distance 1 deg");
                Check(brg < 0.01 || brg > 359.99, "bearing north");
                Destination(15, 42, 123, 37000, out la2, out lo2);
                BearingDistance(15, 42, la2, lo2, out brg, out dist);
                Near(brg, 123, 1e-6, "round trip bearing");
                Near(dist, 37000, 1e-6, "round trip distance");
                BearingDistance(15, 42, 15, 43, out brg, out dist);
                Near(brg, 90, 0.01, "bearing east");
            }
            RunSchedule();
        }

        /// <summary>Fake-clock simulation (tick 0.1 s). world(i) = (state version, moving). Returns the run times.</summary>
        private static List<double> Simulate(double recompute, double stateCheck, int ticks, Func<int, int> stateAt, Func<int, bool> movingAt)
        {
            var sch = new CoverageSchedule(recompute, stateCheck);
            var runs = new List<double>();
            int seen = stateAt(0);
            bool first = true;
            for (int i = 0; i < ticks; i++)
            {
                double t = i * 0.1;
                bool state = false, moved = false;
                if (sch.PollDue(t)) { int v = stateAt(i); if (v != seen) { seen = v; state = true; } }
                if (sch.MotionDue(t)) moved = movingAt(i);
                var trig = sch.Decide(t, first, state, moved);
                if (trig != CoverageTrigger.None) { runs.Add(t); sch.Submitted(t, trig); first = false; }
            }
            return runs;
        }

        /// <summary>Legend texts in ru and en through UiText.Push (T-103 V1). Named *SelfTest* so the literal guard skips the Russian expectations.</summary>
        private static void LegendSelfTestChecks()
        {
            var tiers = ParseTiers(DefaultTiers, out string err);
            using (UiText.Push("ru"))
            {
                var l = LegendLines(tiers, false, "Alt+R");
                Check(l.Count == 7 && l[0].Text == "Зона радаров" && l[0].Bold, "legend ru title / count");
                Check(l[1].Swatch == 0 && l[1].Text.Contains("15 m, 1 m², воздушная цель"), "legend tier 1 line: " + l[1].Text);
                Check(l[4].Text.Contains("надводная цель"), "legend surface tier");
                bool grey = false; foreach (var x in l) if (x.Swatch == 4) grey = true;
                Check(grey, "legend grey swatch");
                string lw;
                Check(LegendAllowed(true, 1920, 1028, 1920, 1080, out lw) && lw == "none", "t233 legend allowed maximized");
                Check(!LegendAllowed(false, 400, 380, 1920, 1080, out lw) && lw == "small_map", "t233 legend hidden on the small map");
                Check(LegendAllowed(false, 1400, 900, 1920, 1080, out lw) && lw == "none", "t233 legend allowed on a stretched window");
                Check(!LegendAllowed(false, 0, 0, 1920, 1080, out lw) && lw == "small_map" && !LegendAllowed(false, double.NaN, 380, 1920, 1080, out lw) && lw == "small_map" && !LegendAllowed(false, 400, 380, 0, 0, out lw), "t233 legend zero / NaN size");
                Check(LegendAllowed(true, 0, 0, 0, 0, out lw), "t233 legend allowed maximized without sizes");
                Check(l[5].Text == "РЛС выключена / EMCON" && l[6].Text == "тени рельефа приблизительно", "legend ru tail");
                var c = LegendLines(tiers, true, "Alt+R");
                Check(c.Count == 8 && c[7].Text == "клаттер игры включён: зона у воды завышена", "legend ru clutter line");
                var jd = LegendLines(tiers, false, "Alt+R", true, false);
                Check(jd.Count == 8 && jd[6].Swatch == 5 && jd[6].Text == "пунктир — дальность без помех", "legend ru jam dashed");
                var jp = LegendLines(tiers, false, "Alt+R", true, true);
                Check(jp.Count == 9 && jp[7].Text.StartsWith("≤ — есть неизвестные"), "legend ru jam partial");
                var ja = LegendLines(tiers, false, "Alt+R", true, false, true);   // T-225
                Check(ja.Count == 9 && ja[7].Text.StartsWith("≈ — дальность постановщика"), "legend ru jam approx");
                Check(LegendLines(tiers, false, "Alt+R", true, true, true).Count == 10, "legend ru partial + approx lines");
            }
            using (UiText.Push("en"))
            {
                var e = LegendLines(tiers, false, "Alt+R");
                Check(e.Count == 7 && e[0].Text == "Radar coverage" && e[0].Bold, "legend en title / count");
                Check(e[1].Swatch == 0 && e[1].Text.Contains("15 m, 1 m², air target"), "legend en tier 1 line: " + e[1].Text);
                Check(e[4].Text.Contains("surface target"), "legend en surface tier");
                Check(e[5].Text == "radar off / EMCON" && e[6].Text == "terrain shadows are approximate", "legend en tail");
                var c = LegendLines(tiers, true, "");
                Check(c.Count == 8 && c[7].Text == "game clutter is on: low zone overestimated", "legend en clutter line");
                var jd = LegendLines(tiers, false, "", true, true);
                Check(jd.Count == 9 && jd[6].Text == "dashed — range without jamming" && jd[7].Text.StartsWith("≤ — unknown jammers"), "legend en jam lines");
                Check(LegendLines(tiers, false, "").Count == 7, "legend: no jam lines without flags");
                var ea = LegendLines(tiers, false, "", true, false, true);   // T-225
                Check(ea.Count == 9 && ea[7].Text.StartsWith("≈ — jammer range from bearings"), "legend en jam approx");
            }
        }

        /// <summary>Legend checks for the offline stand (layertest, T-103 V1). Null = all checks pass.</summary>
        public static string LegendSelfTest()
        {
            _fail = null;
            LegendSelfTestChecks();
            return _fail == null ? null : "coverage legend selftest: " + _fail;
        }

        private static void RunSchedule()
        {
            {
                var r = Simulate(10, 1, 600, i => i >= 31 ? 1 : 0, i => false);
                Check(r.Count == 2 && r[0] == 0 && r[1] > 3.05 && r[1] <= 4.05 + 1e-9, "schedule: state change at 3.05 runs within 1 s");
            }
            {
                var r = Simulate(10, 1, 600, i => 0, i => true);
                bool ok = r.Count == 6;
                for (int k = 0; ok && k < r.Count; k++) if (Math.Abs(r[k] - 10.0 * k) > 0.1) ok = false;
                Check(ok, "schedule: continuous motion runs every 10 s and nowhere between (" + string.Join(",", r.ConvertAll(x => x.ToString("0.0", CultureInfo.InvariantCulture)).ToArray()) + ")");
            }
            Check(Simulate(10, 1, 600, i => 0, i => false).Count == 1, "schedule: still fleet runs only the first time");
            {
                var r = Simulate(10, 0.2, 600, i => i / 2, i => false);   // EMCON flips every 0.2 s
                bool ok = r.Count > 3;
                for (int k = 1; k < r.Count; k++) if (r[k] - r[k - 1] < 1.0 - 1e-6) ok = false;
                Check(ok, "schedule: blinking EMCON keeps the 1 s floor (runs " + r.Count + ")");
            }
            {
                var r = Simulate(10, 0.5, 400, i => i >= 95 ? 1 : 0, i => true);
                bool ok = r.Count >= 3 && Math.Abs(r[1] - 9.5) < 0.06 && Math.Abs(r[2] - 19.5) < 0.1;
                for (int k = 0; k < r.Count; k++) if (r[k] > 9.6 && r[k] < 19.4) ok = false;
                Check(ok && r.Count == 5, "schedule: state at 9.5 restarts the motion period (" + string.Join(",", r.ConvertAll(x => x.ToString("0.0", CultureInfo.InvariantCulture)).ToArray()) + ")");
            }
            Near(ClampRecompute(0), 10, 0, "ClampRecompute(0)"); Near(ClampRecompute(1), 2, 0, "ClampRecompute(1)");
            Near(ClampRecompute(500), 120, 0, "ClampRecompute(500)"); Near(ClampRecompute(double.NaN), 10, 0, "ClampRecompute(NaN)");
            Near(ClampStateCheck(5), 1, 0, "ClampStateCheck(5)"); Near(ClampStateCheck(0.05), 0.2, 1e-12, "ClampStateCheck(0.05)"); Near(ClampStateCheck(-1), 1, 0, "ClampStateCheck(-1)");
            Check(ParseRenderMode("NOESIS") == "noesis" && ParseRenderMode("imgui") == "imgui" && ParseRenderMode("x") == "auto" && ParseRenderMode(null) == "auto", "ParseRenderMode");
            Near(ClampRtScale(0.1), 0.5, 0, "ClampRtScale low"); Near(ClampRtScale(3), 1, 0, "ClampRtScale high"); Near(ClampRtScale(double.NaN), 1, 0, "ClampRtScale NaN");
            {
                var sch = new CoverageSchedule(10, 1);
                Check(sch.Decide(0, true, false, false) == CoverageTrigger.Force, "Decide force");
                sch.Submitted(0, CoverageTrigger.Force);
                Check(sch.Decide(0.4, false, true, false) == CoverageTrigger.None, "Decide state inside the gap waits");
                Check(sch.Decide(1.0, false, false, false) == CoverageTrigger.State, "Decide remembered state runs after the gap");
                sch.Submitted(1.0, CoverageTrigger.State);
                sch.Reset();
                Check(sch.PollDue(5) && sch.Decide(5, true, false, false) == CoverageTrigger.Force, "Reset clears the timers");
            }

            // --- projection, insert index
            Check(!ProjectionChanged(new[] { 0.0, 0, 100, 100 }, new[] { 0.4, 0, 100, 100 }, 0.5), "projection 0.4 px");
            Check(ProjectionChanged(new[] { 0.0, 0, 100, 100 }, new[] { 0.6, 0, 100, 100 }, 0.5), "projection 0.6 px");
            Check(ProjectionChanged(new[] { 0.0, 0 }, new[] { 0.0, 0, 1, 1 }, 0.5), "projection count differs");
            {
                int z;
                Check(MapChildInsertIndex(new[] { "MapTileLayer", "MapItemsControl", "MapPolyline" }, new[] { 0, 5, 7 }, -1, out z) == 1 && z == 0, "insert index tile layer first");
                Check(MapChildInsertIndex(new[] { "Border", "MapImage", "MapTileLayer", "X" }, new[] { 1, 2, 3, 4 }, -1, out z) == 3 && z == 3, "insert index above last base layer");
                Check(MapChildInsertIndex(new[] { "A", "B" }, new[] { 0, 0 }, -1, out z) == 1 && z == 0, "insert index without base layer");
                Check(MapChildInsertIndex(new[] { "MapTileLayer", "B", "C" }, new[] { 0, 0, 0 }, 0, out z) == 0, "insert index override 0");
                Check(MapChildInsertIndex(new[] { "MapTileLayer", "B", "C" }, new[] { 0, 0, 0 }, 99, out z) == 3, "insert index override above count");
                Check(MapChildInsertIndex(new[] { "MapImageLayer", "B" }, new[] { 4, 0 }, -1, out z) == 1 && z == 4, "insert index image layer z");
            }

            // --- legend
            {
                LegendSelfTestChecks();
                var b = LegendLayout(1920, 1028, 7, 0, 1);
                Check(b.Visible && b.FontPx == 12 && b.W == 270 && b.X == 1920 - 270 - 8 && b.Y == 8 && b.H == 7 * 16 + 8, "legend layout large: top-right");
                Check(LegendLayout(800, 450, 7, 0, 1).FontPx == 10, "legend layout small font");
                Check(!LegendLayout(300, 200, 7, 0, 1).Visible, "legend layout hidden");
                var h = LegendLayout(1920, 1028, 7, 0, 0.5);
                Near(h.W, 135, 1e-12, "legend scale W"); Near(h.FontPx, 6, 1e-12, "legend scale font"); Near(h.X, (1920 - 270 - 8) * 0.5, 1e-12, "legend scale X"); Near(h.Y, 4, 1e-12, "legend scale Y");
                Near(LegendLayout(1920, 1028, 7, 300, 1).W, 312, 1e-12, "legend text width");
                Near(LegendLayout(1920, 1028, 7, 300, 1).X, 1920 - 312 - 8, 1e-12, "legend right-aligned by its width");
                // stacked: the radar legend under the ring legend; hidden when both together would cover too much of the map
                var st = LegendLayout(1920, 1028, 4, 0, 1, 100);
                Check(st.Visible && st.Y == 8 + 100, "legend stacked under another one");
                Near(LegendLayout(1920, 1028, 4, 0, 0.5, 100).Y, (8 + 100) * 0.5, 1e-12, "legend stack scale");
                Check(!LegendLayout(1920, 1028, 20, 0, 1, 200).Visible, "legend stack hides when too tall");
                // under the time panel: the offset does not count toward the 45 %, but the box must stay inside the map
                var under = LegendLayout(1920, 1028, 7, 0, 1, 0, 40);
                Check(under.Visible && under.Y == 48, "legend under the panel");
                Check(LegendLayout(1920, 1028, 7, 0, 1, 0, -50).Y == 8, "negative panel offset ignored");
                Check(!LegendLayout(1280, 720, 7, 0, 1, 0, 600).Visible, "legend pushed past the bottom is hidden");
                // live 0.29.0 (1920x1080): map 1920x1028 at y 52, the time panel x 1710..1920, y 20..80
                double[] m1028 = { 0, 52, 1920, 1028 };
                double[] panel = { 1710, 20, 210, 60 };
                double off = LegendPanelOffset(m1028, panel, 270, 6);
                Check(off == 80 + 6 - 52 - 8, "panel offset live geometry " + off);
                var lv = LegendLayout(1920, 1028, 7, 0, 1, 0, off);
                Check(lv.Visible && 52 + lv.Y >= 80 + 6 && lv.X + lv.W <= 1920 - 8 + 1e-9, "legend below the panel, inside the map");
                Check(lv.X + 0 < 1710 + 210 && lv.X + lv.W > 1710, "the legend column is under the panel");
                Check(LegendPanelOffset(m1028, new double[] { 1710, 20.4, 210, 60 }, 270, 6) == 27, "offset rounded up");
                Check(LegendPanelOffset(m1028, null, 270, 6) == 0, "no panel: no offset");
                Check(LegendPanelOffset(m1028, new double[] { 0, 20, 210, 60 }, 270, 6) == 0, "panel on the left: no offset");
                Check(LegendPanelOffset(m1028, new double[] { 1710, 0, 210, 30 }, 270, 6) == 0, "panel above the map: no offset");
                Check(LegendPanelOffset(m1028, new double[] { 0, 0, 1920, 1080 }, 270, 6) == 0, "full-screen host rejected");
                Check(LegendPanelOffset(m1028, new double[] { 1710, double.NaN, 210, 60 }, 270, 6) == 0, "NaN panel rejected");
                Check(LegendPanelOffset(new double[] { 100, 0, 1000, 600 }, new double[] { 950, 0, 150, 40 }, 270, 6) == 40 + 6 - 8, "offset map at x 100");
                Check(PlausiblePanel(m1028, panel) && !PlausiblePanel(m1028, new double[] { 1000, 20, 1000, 60 }) && !PlausiblePanel(null, panel), "plausible panel");
                var fb = TimePanelFallback(1920, 1080);
                Check(fb[0] == 1710 && fb[1] == 20 && fb[2] == 210 && fb[3] == 60, "fallback panel 1920x1080");
                var fb2 = TimePanelFallback(2560, 1440);
                Near(fb2[1] + fb2[3], 80 * 1440 / 1080.0, 1e-9, "fallback panel scales with the height"); Near(fb2[0] + fb2[2], 2560, 1e-9, "fallback panel at the right edge");
            }

            // --- T-190 A4/A5: panel pick and legend avoidance
            {
                double[] scr = { 0, 0, 1920, 1080 };
                Check(PlausiblePanel(scr, new double[] { 1711, 20, 209, 62 }), "t190 plausible Grid vs screen");
                var cands = new List<double[]> { new double[] { 1716, 5, 199, 20 }, new double[] { 1711, 20, 209, 62 }, new double[] { 0, 0, 1920, 1080 } };
                Check(PickPanel(cands, scr) == 1, "t190 PickPanel takes the Grid");
                Check(PickPanel(new List<double[]>(), scr) == -1 && PickPanel(new List<double[]> { new double[] { 0, 0, 1920, 1080 } }, scr) == -1 && PickPanel(null, scr) == -1, "t190 PickPanel none");
                double[] col = { 1642, 51, 270, 136 }; string why;
                var ov = new double[] { 1344, 66, 445, 443 };
                var d = LegendAvoid(col, new List<double[]> { ov }, 1920, 1028, 6, out why);
                Check(why == "below" && d[0] == 0 && Math.Abs(51 + d[1] - (509 + 6)) < 1e-9, "t190 avoid below " + why + " " + d[1]);
                var ovTall = new double[] { 1344, 66, 445, 960 };
                d = LegendAvoid(col, new List<double[]> { ovTall }, 1920, 1028, 6, out why);
                Check(why == "left" && d[1] == 0 && Math.Abs(1642 + d[0] - (1344 - 6 - 270)) < 1e-9, "t190 avoid left " + why + " " + d[0]);
                d = LegendAvoid(col, new List<double[]> { ovTall, new double[] { 0, 40, 1343, 200 } }, 1920, 1028, 6, out why);
                Check(why == "blocked" && d[0] == 0 && d[1] == 0, "t190 avoid blocked " + why);
                d = LegendAvoid(col, new List<double[]> { new double[] { 100, 100, 200, 200 } }, 1920, 1028, 6, out why);
                Check(why == "none" && d[0] == 0 && d[1] == 0, "t190 avoid none");
                d = LegendAvoid(col, new List<double[]> { new double[] { 1344, 66, 445, 200 }, new double[] { 1344, 272, 445, 200 } }, 1920, 1028, 6, out why);
                Check(why == "below" && Math.Abs(51 + d[1] - (472 + 6)) < 1e-9, "t190 avoid two stacked " + why + " " + d[1]);
                d = LegendAvoid(col, new List<double[]> { new double[] { -500, -300, 400, 200 } }, 1920, 1028, 6, out why);
                Check(why == "none" && d[0] == 0 && d[1] == 0, "t233 avoid obstacle outside the map " + why);
                d = LegendAvoid(col, new List<double[]> { new double[] { 1691, 5, 209, 62 } }, 1920, 1028, 6, out why);
                Check(why == "below" && Math.Abs(51 + d[1] - (67 + 6)) < 1e-9, "t190 avoid time panel " + why + " " + d[1]);
            }

            // --- stats
            {
                var st = new CoverageStats();
                Check(!st.Due(0), "stats first Due starts the window");
                st.AddBuild(100); st.AddBuild(200); st.AddBuild(300);
                for (int i = 0; i < 600; i++) st.AddFrame(0.016);
                for (int i = 0; i < 30; i++) st.AddFrame(0.040);
                st.AddPoll(0.5); st.AddSnapshot(2); st.AddRedraw(1.5);
                Check(!st.Due(59) && st.Due(60), "stats window is 60 s");
                string line = st.TakeLine(60, "noesis", 4);
                Check(line.Contains("builds=3 build_ms_avg=200 build_ms_max=300 worker_cpu_pct=1 "), "stats builds: " + line);
                Check(line.Contains("mode=noesis radars=4") && line.Contains("rt_redraws=1 rt_ms_avg=1.5") && line.Contains("poll_ms_max=0.5"), "stats misc: " + line);
                Check(line.Contains("fps_avg=58.3 frame_ms_p95=16"), "stats fps: " + line);
                string again = st.TakeLine(120, "noesis", 4);
                Check(again.Contains("builds=0") && again.Contains("rt_redraws=0") && again.Contains("fps_avg=0 "), "stats reset: " + again);
                var mt = new CoverageStats();
                var th = new System.Threading.Thread[2];
                for (int k = 0; k < 2; k++) { th[k] = new System.Threading.Thread(() => { for (int i = 0; i < 1000; i++) mt.AddBuild(1); }); th[k].Start(); }
                foreach (var t in th) t.Join();
                Check(mt.TakeLine(60, "imgui", 0).Contains("builds=2000"), "stats thread safety");
            }

            // --- T-225: the prefetch enumerates exactly the points BuildProfile reads; stats keys of the main-thread terrain
            {
                var tiers225 = ParseTiers(DefaultTiers, out string err225);
                for (int ri = 0; ri < 3; ri++)
                {
                    var rad = new RadarSpec
                    {
                        Globe = true,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                        OwnerUid = ri, Name = "SPS-49", Type = "Search", Air = true, Surface = true, On = true, Lat = 15.0 + ri * 0.7, Lon = 42.0 - ri * 0.3, AntennaM = 20 + ri * 10,
                        MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000, PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916,
                        FullCircle = true, VisibleHeightM = 3, TerrainUnderAntennaM = -10
                    };
                    var rec = new List<double>();
                    Func<double, double, double> land = (la, lo) => { rec.Add(la); rec.Add(lo); return 100.0; };
                    int rays = 36 + ri * 12, bins = 24 + ri * 8;
                    var prof = BuildProfile(rad, tiers225[1], 1, MinPowerKw, land, rays, bins);
                    Check(prof.Terrain && rec.Count == 2 * (1 + rays * bins), "t225 terrain points read " + rec.Count);
                    double bound = ProfileBoundM(rad, tiers225[1], MinPowerKw);
                    Check(bound > 0 && bound == prof.RangeM && Math.Abs(ProfileBinM(rad, tiers225[1], MinPowerKw, bins) - prof.BinM) == 0, "t225 ProfileBoundM equals the profile range");
                    int at = 2;   // [0..1] = the first point of the open-sea check
                    bool same = true; int visited = 0;
                    ForEachRayPoint(rad, bound / bins, rays, bins, (la, lo) => { if (at + 1 < rec.Count && (rec[at] != la || rec[at + 1] != lo)) same = false; at += 2; visited++; });
                    Check(same && visited == rays * bins && at == rec.Count, "t225 ForEachRayPoint order and values");
                    var seaPts = new List<double>();
                    ForEachOpenSeaPoint(rad, bound, (la, lo) => { seaPts.Add(la); seaPts.Add(lo); });
                    Check(seaPts.Count == 2 * 36 * 16 && seaPts[0] == rec[0] && seaPts[1] == rec[1], "t225 open-sea points");
                }
                var st225 = new CoverageStats();
                st225.AddBroker(3500, 2, 3.6, 2.1, 40, 0, 0); st225.AddBroker(100, 1, 0.5, 0.5, 10, 2, 3); st225.SetFetchRounds(2);
                string l225 = st225.TakeLine(60, "x", 1);
                foreach (string key in new[] { "terrain_main_ms_avg=1.4 ", "terrain_main_ms_max=2.1 ", "terrain_wait_ms=50 ", "terrain_late=2 ", "terrain_offmain=3 ", "fetch_rounds=2" })
                    Check(l225.Contains(key), "t225 stats key " + key + ": " + l225);
                Check(st225.TakeLine(120, "x", 1).Contains("terrain_late=0 terrain_offmain=0 "), "t225 stats reset");
                // T-232: GC base of the first window
                {
                    var g1 = new CoverageStats(); int gc = 1000;
                    g1.Due(0); g1.GcCount = () => gc; gc += 9;
                    Check(g1.TakeLine(60, "x", 1).Contains("gc_per_min=9 "), "t232 gc base set after Due: " + g1.TakeLine(60, "x", 1));
                    var g2 = new CoverageStats(); int gc2 = 1000;
                    g2.GcCount = () => gc2; g2.Due(0); gc2 += 6;
                    Check(g2.TakeLine(60, "x", 1).Contains("gc_per_min=6 "), "t232 gc base set before Due");
                    gc2 += 3;
                    Check(g2.TakeLine(120, "x", 1).Contains("gc_per_min=3 "), "t232 gc base carried over");
                    // first build metrics
                    var fb = new CoverageStats();
                    fb.AddBuild(1300, 4000, 0, first: true, waitMs: 1500, paceMs: 1200);
                    string lf = fb.TakeLine(60, "x", 1);
                    Check(lf.Contains("first_build_wall_ms=4000 first_build_busy_ms=1300 first_build_wait_ms=1500 first_build_pace_ms=1200"), "t232 first build fields: " + lf);
                    fb.AddBuild(100, 200, 0);
                    Check(!fb.TakeLine(120, "x", 1).Contains("first_build"), "t232 first build fields reset / absent");
                }
                // legend, ru/en
            }

            // --- T-128 terrain call builder
            { string te = TerrainCall.SelfTest(); Check(te == null, te ?? "terrain call"); }
        }

        private static RadarSpec TestRadarLike(RadarSpec r, int seaState)
        {
            return new RadarSpec
            {
                Globe = r.Globe,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                Air = r.Air, Surface = r.Surface, On = r.On, AntennaM = r.AntennaM, MinRangeM = r.MinRangeM, MaxRangeM = r.MaxRangeM, MinAltM = r.MinAltM, MaxAltM = r.MaxAltM,
                PeakPowerKw = r.PeakPowerKw, GainFactor = r.GainFactor, WaveLengthM = r.WaveLengthM, LookDownMult = r.LookDownMult, FullCircle = r.FullCircle, SeaState = seaState,
                VisibleHeightM = r.VisibleHeightM
            };
        }

        private static bool IsEmpty(RayProfile p)
        {
            foreach (ulong w in p.Visible) if (w != 0) return false;
            return true;
        }

        private static double[][] Quad(double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4)
        {
            return new[] { new[] { x1, y1 }, new[] { x2, y2 }, new[] { x3, y3 }, new[] { x4, y4 } };
        }

        private static bool InTri(double[] t, double px, double py)
        {
            double d1 = (px - t[2]) * (t[1] - t[3]) - (t[0] - t[2]) * (py - t[3]);
            double d2 = (px - t[4]) * (t[3] - t[5]) - (t[2] - t[4]) * (py - t[5]);
            double d3 = (px - t[0]) * (t[5] - t[1]) - (t[4] - t[0]) * (py - t[1]);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }

    /// <summary>T-128: builds a delegate (lat, lon) -> metres around a static game method taking a GeoPosition-like struct.
    /// No per-call allocations: compiled expression, or (fallback) one boxed struct and one object[] owned by the delegate.
    /// The delegate must be used from one thread only (reflect mode mutates its boxed struct).</summary>
    internal static class TerrainCall
    {
        /// <summary>Test hook: force the reflection fallback.</summary>
        internal static bool ForceReflect;
        /// <summary>Boxed struct instances created by the reflection fallback (test diagnostics).</summary>
        internal static int Boxes;

        /// <summary>(lat, lon) -> metres ASL, NaN when the raw result is &lt;= -9999 or the call throws.
        /// heightFn: static method with one parameter of type geoType (fields latF/lonF/heightF), optionally followed by a bool (extraArg), returning float or double.
        /// toMetres: optional static float(float) applied to the raw result; enabled: optional gate (false -> NaN).</summary>
        public static Func<double, double, double> Build(MethodInfo heightFn, Type geoType, FieldInfo latF, FieldInfo lonF, FieldInfo heightF,
                                                         MethodInfo toMetres, bool? extraArg, Func<bool> enabled, out string mode)
        {
            mode = null;
            if (heightFn == null || geoType == null || latF == null || lonF == null) return null;
            Func<double, double, double> raw = null;
            if (!ForceReflect)
            {
                try { raw = Compile(heightFn, geoType, latF, lonF, heightF, toMetres, extraArg); mode = "compiled"; }
                catch { raw = null; }
            }
            if (raw == null) { raw = Reflect(heightFn, geoType, latF, lonF, heightF, toMetres, extraArg); mode = "reflect"; }
            var inner = raw;
            return (lat, lon) =>
            {
                try
                {
                    if (enabled != null && !enabled()) return double.NaN;
                    double v = inner(lat, lon);
                    return double.IsNaN(v) || v <= -9999.0 ? double.NaN : v;
                }
                catch { return double.NaN; }
            };
        }

        /// <summary>T-202: reads a static bool field without boxing (compiled expression); reflection only as a fallback. Null when f is not a static bool field.</summary>
        public static Func<bool> StaticBool(FieldInfo f)
        {
            if (f == null || f.FieldType != typeof(bool) || !f.IsStatic) return null;
            if (!ForceReflect)
            {
                try { return Expression.Lambda<Func<bool>>(Expression.Field(null, f)).Compile(); }
                catch { }
            }
            return () => (bool)f.GetValue(null);
        }

        private static Func<double, double, double> Compile(MethodInfo fn, Type gt, FieldInfo la, FieldInfo lo, FieldInfo h, MethodInfo tm, bool? extra)
        {
            var pLat = Expression.Parameter(typeof(double), "lat");
            var pLon = Expression.Parameter(typeof(double), "lon");
            var g = Expression.Variable(gt, "g");
            var r = Expression.Variable(typeof(double), "r");
            var body = new List<Expression>
            {
                Expression.Assign(g, Expression.New(gt)),
                Expression.Assign(Expression.Field(g, la), Expression.Convert(pLat, la.FieldType)),
                Expression.Assign(Expression.Field(g, lo), Expression.Convert(pLon, lo.FieldType))
            };
            if (h != null) body.Add(Expression.Assign(Expression.Field(g, h), Expression.Convert(Expression.Constant(0.0), h.FieldType)));
            var args = new List<Expression> { g };
            if (extra.HasValue) args.Add(Expression.Constant(extra.Value));
            body.Add(Expression.Assign(r, Expression.Convert(Expression.Call(fn, args), typeof(double))));
            Expression res = r;
            if (tm != null)
            {
                Expression conv = Expression.Convert(Expression.Call(tm, Expression.Convert(r, typeof(float))), typeof(double));
                res = Expression.Condition(Expression.LessThanOrEqual(r, Expression.Constant(-9999.0)), Expression.Constant(double.NaN), conv);
            }
            body.Add(res);
            var lambda = Expression.Lambda<Func<double, double, double>>(Expression.Block(typeof(double), new[] { g, r }, body), pLat, pLon);
            return lambda.Compile();
        }

        private static Func<double, double, double> Reflect(MethodInfo fn, Type gt, FieldInfo la, FieldInfo lo, FieldInfo h, MethodInfo tm, bool? extra)
        {
            object box = Activator.CreateInstance(gt);
            Boxes++;
            var call = new object[extra.HasValue ? 2 : 1];
            if (extra.HasValue) call[1] = extra.Value;
            var one = new object[1];
            call[0] = box;                       // the same boxed struct each call: SetValue mutates it in place
            return (lat, lon) =>
            {
                la.SetValue(box, Convert.ChangeType(lat, la.FieldType, CultureInfo.InvariantCulture));
                lo.SetValue(box, Convert.ChangeType(lon, lo.FieldType, CultureInfo.InvariantCulture));
                double v = Convert.ToDouble(fn.Invoke(null, call), CultureInfo.InvariantCulture);
                if (tm != null)
                {
                    if (v <= -9999.0) return double.NaN;
                    one[0] = (float)v;
                    v = Convert.ToDouble(tm.Invoke(null, one), CultureInfo.InvariantCulture);
                }
                return v;
            };
        }

        // ---- self test fakes ----
        internal struct FakeGeo { public double _latitude, _longitude, _height; }
        internal static bool LastJob;
        internal static bool FlagFake;
        internal static float FakeDem(FakeGeo g) { return g._latitude > 80 ? -10000f : (float)(g._latitude * 10 + g._longitude); }
        internal static float FakeReal(FakeGeo g, bool job) { LastJob = job; return g._latitude > 80 ? -10000f : (float)(g._latitude * 10 + g._longitude); }
        internal static float FakeThrow(FakeGeo g) { throw new InvalidOperationException("fake"); }
        internal static float FakeMetres(float u) { return u * 2f; }

        public static string SelfTest()
        {
            try
            {
                var t = typeof(TerrainCall);
                var gt = typeof(FakeGeo);
                FieldInfo la = gt.GetField("_latitude"), lo = gt.GetField("_longitude"), h = gt.GetField("_height");
                MethodInfo dem = t.GetMethod("FakeDem", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo real = t.GetMethod("FakeReal", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo thr = t.GetMethod("FakeThrow", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo met = t.GetMethod("FakeMetres", BindingFlags.NonPublic | BindingFlags.Static);
                bool saved = ForceReflect;
                try
                {
                    foreach (bool reflect in new[] { false, true })
                    {
                        ForceReflect = reflect;
                        string want = reflect ? "reflect" : "compiled", mode;
                        string tag = want + ": ";
                        var f = Build(dem, gt, la, lo, h, null, null, null, out mode);
                        if (f == null || mode != want) return tag + "mode " + mode;
                        if (Math.Abs(f(5, 7) - 57) > 1e-9) return tag + "value " + f(5, 7);
                        if (!double.IsNaN(f(81, 0))) return tag + "no-data is not NaN";
                        var fm = Build(dem, gt, la, lo, h, met, null, null, out mode);
                        if (Math.Abs(fm(5, 7) - 114) > 1e-9) return tag + "toMetres " + fm(5, 7);
                        if (!double.IsNaN(fm(81, 0))) return tag + "toMetres no-data";
                        var fr = Build(real, gt, la, lo, h, null, true, null, out mode);
                        LastJob = false; fr(1, 1);
                        if (!LastJob) return tag + "extraArg true not passed";
                        fr = Build(real, gt, la, lo, h, null, false, null, out mode);
                        LastJob = true; fr(1, 1);
                        if (LastJob) return tag + "extraArg false not passed";
                        bool on = false;
                        var fe = Build(dem, gt, la, lo, h, null, null, () => on, out mode);
                        if (!double.IsNaN(fe(5, 7))) return tag + "disabled is not NaN";
                        on = true;
                        if (double.IsNaN(fe(5, 7))) return tag + "enabled is NaN";
                        var ft = Build(thr, gt, la, lo, h, null, null, null, out mode);
                        if (!double.IsNaN(ft(1, 1))) return tag + "exception is not NaN";
                        if (Build(null, gt, la, lo, h, null, null, null, out mode) != null) return tag + "null method";
                    }
                    foreach (bool reflect in new[] { false, true })
                    {
                        ForceReflect = reflect;
                        var sb = StaticBool(t.GetField("FlagFake", BindingFlags.NonPublic | BindingFlags.Static));
                        if (sb == null) return "StaticBool null";
                        FlagFake = true; if (!sb()) return "StaticBool true " + reflect;
                        FlagFake = false; if (sb()) return "StaticBool false " + reflect;
                    }
                    if (StaticBool(t.GetField("Boxes", BindingFlags.NonPublic | BindingFlags.Static)) != null) return "StaticBool accepts int";
                    ForceReflect = true;
                    string m2;
                    int before = Boxes;
                    var fb = Build(dem, gt, la, lo, h, null, null, null, out m2);
                    for (int i = 0; i < 1000; i++) fb(i % 50, i % 7);
                    if (Boxes - before > 1) return "reflect boxes " + (Boxes - before);
                }
                finally { ForceReflect = saved; }
                return null;
            }
            catch (Exception e) { return "terrain call selftest exception: " + e.Message; }
        }
    }
}
