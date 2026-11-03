using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-138: small pure cache of terrain heights for the intercept routes, the evasion guard and SAR (key = 0.05 nm cell). Main thread only.
    /// T-275: a missing height (NaN, infinity) is NOT cached: the cell counts as open sea (-200 m) for now and the sampler is asked again after
    /// <see cref="RetryS"/> seconds of the clock (per cell). A "terrain no data" line goes to the log at most every <see cref="LogEveryS"/> s.</summary>
    internal sealed class TerrainHeightCache
    {
        public const double CellNm = 0.05, OpenSeaM = -200, RetryS = 5, LogEveryS = 30;
        public const int MaxEntries = 200000, MaxNoData = 20000;
        private readonly Func<double, double, double> _sampler;
        private readonly Func<double> _clock;
        private readonly Action<string> _log;
        private readonly Dictionary<long, double> _cache = new Dictionary<long, double>();
        private readonly Dictionary<long, double> _retryAt = new Dictionary<long, double>();
        private double _nextLog = double.NegativeInfinity;
        private int _noDataSinceLog;

        /// <param name="clock">seconds (real time); null = the process clock.</param>
        /// <param name="log">the always-logged line sink, already prefixed by the caller; null = silent.</param>
        public TerrainHeightCache(Func<double, double, double> sampler, Func<double> clock = null, Action<string> log = null)
        {
            _sampler = sampler; _clock = clock ?? DefaultClock; _log = log;
        }

        private static double DefaultClock() { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; }

        public int Count { get { return _cache.Count; } }
        /// <summary>T-278: how many times the sampler was asked (not served from the cache); the caller takes differences.</summary>
        public int Misses { get; private set; }
        /// <summary>Cells that had no data at their last ask.</summary>
        public int NoDataCount { get { return _retryAt.Count; } }

        public void Clear() { _cache.Clear(); _retryAt.Clear(); _noDataSinceLog = 0; _nextLog = double.NegativeInfinity; }

        public double HeightM(GeoPt g)
        {
            long iy = (long)Math.Round(g.Lat * 60.0 / CellNm), ix = (long)Math.Round(g.Lon * 60.0 / CellNm);
            long key = iy * 4000003L + ix;
            double h;
            if (_cache.TryGetValue(key, out h)) return h;
            if (_sampler == null) return OpenSeaM;
            double now = _clock(), at;
            if (_retryAt.TryGetValue(key, out at) && now < at) return OpenSeaM;   // no data a moment ago: passable, asked again later
            Misses++;
            double v = _sampler(g.Lat, g.Lon);
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                if (_retryAt.Count >= MaxNoData) _retryAt.Clear();
                _retryAt[key] = now + RetryS;
                NoteNoData(now);
                return OpenSeaM;
            }
            _retryAt.Remove(key);
            if (_cache.Count >= MaxEntries) _cache.Clear();   // bounded: a long mission must not grow it for ever
            _cache[key] = v;
            return v;
        }

        private void NoteNoData(double now)
        {
            _noDataSinceLog++;
            if (now < _nextLog) return;
            _nextLog = now + LogEveryS;
            int n = _noDataSinceLog; _noDataSinceLog = 0;
            if (_log != null) try { _log("terrain no data: " + n + " ask(s) treated as passable, cells waiting=" + _retryAt.Count + " (asked again every " + RetryS + " s)"); } catch { }
        }
    }
}
