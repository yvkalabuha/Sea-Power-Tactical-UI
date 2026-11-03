// T-321 / ADR-0125: pure math of "the ring as the game counts it". No Unity, no game. The verdict of the launcher (too close / too far), the incremental
// search of the kinematic range threshold (one game call per probe), the metric of the game world (nm per degree) and the call queue of the frame budget.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    internal enum KinVerdict { Unknown, InRange, TooFar, TooClose }

    /// <summary>Nautical miles of the game world per degree at a point (the game compares launch ranges with Vector3.Distance in world units, not in
    /// true miles). !Valid = geodesic fallback (60 / 60 * cos lat).</summary>
    internal struct RingGeoMetric { public double LatNmPerDeg, LonNmPerDeg; public bool Valid; }

    /// <summary>What the game knows about one own ammunition for the range prediction: the kind of range formula and the typical target.</summary>
    internal sealed class KinAmmoInfo
    {
        public string Name; public RingAmmoType Type;
        public bool Kinematic, AirLaunch, EvasiveTypical;
        public double NominalNm, MinNm, AltFt, SpeedKn;
    }

    /// <summary>Incremental bisection of the game's "in range" predicate, one probe per call. Invariant: loNm is in range (assumed), hiNm is not (unless the
    /// first probe at hiNm passes: then the result is hiNm).</summary>
    internal sealed class KinSearch
    {
        private double _lo, _hi; private readonly double _tol; private readonly int _max;
        private bool _first = true, _done;
        private int _probes;

        public KinSearch(double loNm, double hiNm, double tolNm = 0.5, int maxProbes = 12)
        {
            _lo = loNm; _hi = hiNm; _tol = tolNm > 0 ? tolNm : 0.5; _max = maxProbes < 1 ? 1 : maxProbes;
            if (!(hiNm > loNm) || double.IsNaN(loNm) || double.IsNaN(hiNm)) _done = true;
        }

        public bool Done { get { return _done; } }
        public double ResultNm { get { return _lo; } }
        public int Probes { get { return _probes; } }

        public double Next()
        {
            if (_done) return double.NaN;
            return _first ? _hi : (_lo + _hi) / 2.0;
        }

        public void Report(double dNm, bool inRange)
        {
            if (_done) return;
            _probes++;
            if (_first)
            {
                _first = false;
                if (inRange) { _lo = _hi; _done = true; return; }
            }
            else if (inRange) _lo = dNm;
            else _hi = dNm;
            if (_hi - _lo <= _tol || _probes >= _max) _done = true;
        }
    }

    /// <summary>The frame budget: queued game calls, at most one step per Pump. A task returns true when it is finished (false = needs another step later).
    /// A task that throws is dropped. Keys are unique: a queued key is not queued twice.</summary>
    internal sealed class KinQueue
    {
        private readonly List<KeyValuePair<string, Func<bool>>> _q = new List<KeyValuePair<string, Func<bool>>>();
        public const int MaxTasks = 64;
        public int Steps { get; private set; }

        public int Count { get { return _q.Count; } }
        public bool Has(string key) { return _q.Exists(e => e.Key == key); }
        public void Clear() { _q.Clear(); }

        public bool Enqueue(string key, Func<bool> step)
        {
            if (key == null || step == null || Has(key) || _q.Count >= MaxTasks) return false;
            _q.Add(new KeyValuePair<string, Func<bool>>(key, step));
            return true;
        }

        /// <summary>One step of the head task (one game call at most). True = a task finished (or was dropped): the caller rebuilds. Unfinished tasks go to the back.</summary>
        public bool Pump()
        {
            if (_q.Count == 0) return false;
            var e = _q[0];
            _q.RemoveAt(0);
            Steps++;
            bool finished;
            try { finished = e.Value(); } catch { finished = true; }
            if (!finished) _q.Add(e);
            return finished;
        }
    }

    internal static class RangeRingKin
    {
        public const double SampleDeg = 0.05, CellDeg = 0.5, MaxBracketK = 1.5;
        private const double MinCosLat = 0.05;

        /// <summary>As the launcher decides: d &lt;= min is too close, d &gt; max is too far; any NaN = unknown.</summary>
        public static KinVerdict Verdict(double distNm, double maxNm, double minNm)
        {
            if (double.IsNaN(distNm) || double.IsNaN(maxNm)) return KinVerdict.Unknown;
            if (!double.IsNaN(minNm) && distNm <= minNm) return KinVerdict.TooClose;
            if (distNm > maxNm) return KinVerdict.TooFar;
            return KinVerdict.InRange;
        }

        /// <summary>The metric from the world positions (nm) of the two samples taken over a span of spanDeg degrees along lon and along lat:
        /// |d/dlon| and |d/dlat| in nm per degree. Invalid when non-finite or &lt;= 0 (a rotated grid keeps its length: only the magnitude counts).</summary>
        public static RingGeoMetric MetricFromSamples(double dxLonNm, double dzLonNm, double dxLatNm, double dzLatNm, double spanDeg)
        {
            var m = new RingGeoMetric();
            if (!(spanDeg > 0) || double.IsInfinity(spanDeg)) return m;
            double lon = Math.Sqrt(dxLonNm * dxLonNm + dzLonNm * dzLonNm) / spanDeg;
            double lat = Math.Sqrt(dxLatNm * dxLatNm + dzLatNm * dzLatNm) / spanDeg;
            if (double.IsNaN(lon) || double.IsInfinity(lon) || double.IsNaN(lat) || double.IsInfinity(lat) || !(lon > 0) || !(lat > 0)) return m;
            m.LatNmPerDeg = lat; m.LonNmPerDeg = lon; m.Valid = true;
            return m;
        }

        /// <summary>The point at nm "game miles" from a point along a bearing (clockwise from north). !Valid = the geodesic fallback RangeRingMath.Offset.</summary>
        public static void OffsetMetric(double lat0, double lon0, double bearingDeg, double nm, RingGeoMetric m, out double lat, out double lon)
        {
            if (!m.Valid) { RangeRingMath.Offset(lat0, lon0, bearingDeg, nm, out lat, out lon); return; }
            double b = bearingDeg * Math.PI / 180.0;
            lat = lat0 + nm * Math.Cos(b) / m.LatNmPerDeg;
            lon = lon0 + nm * Math.Sin(b) / m.LonNmPerDeg;
        }

        /// <summary>The geodesic radius (true miles) of the circle that encloses the metric ring of nm game miles: the larger semi-axis of its ellipse.</summary>
        public static double CullNm(double nm, double lat0, RingGeoMetric m)
        {
            if (!m.Valid) return nm;
            double cl = Math.Cos(lat0 * Math.PI / 180.0);
            if (!(cl >= MinCosLat)) cl = MinCosLat;
            double ns = nm * 60.0 / m.LatNmPerDeg, ew = nm * 60.0 * cl / m.LonNmPerDeg;
            return Math.Max(ns, ew);
        }

        /// <summary>The 0.5 degree cell of a point (the metric is cached per cell).</summary>
        public static long CellKey(double lat, double lon)
        {
            long a = (long)Math.Floor(lat / CellDeg), b = (long)Math.Floor(lon / CellDeg);
            return a * 100000L + b;
        }

        /// <summary>Cache key of a range threshold: a ship / submarine launches at speed 0, so only the ammunition counts; an aircraft also by launch altitude
        /// (2000 ft steps) and speed (50 kn steps).</summary>
        public static string KinKey(string ammo, bool air, double altFt, double speedKn)
        {
            if (!air) return ammo + "|s";
            return ammo + "|a|" + (long)Math.Floor(altFt / 2000.0) + "|" + (long)Math.Floor(speedKn / 50.0);
        }

        public static string SelfTest()
        {
            // A1: the launcher's verdict
            if (Verdict(250, 270, 30) != KinVerdict.InRange || Verdict(284, 270, 30) != KinVerdict.TooFar || Verdict(30, 270, 30) != KinVerdict.TooClose) return "verdict basics";
            if (Verdict(270, 270, 30) != KinVerdict.InRange) return "verdict: d == max is in range (the game says too far only for d > max)";
            if (Verdict(double.NaN, 270, 30) != KinVerdict.Unknown || Verdict(10, double.NaN, 30) != KinVerdict.Unknown) return "verdict NaN";
            if (Verdict(230, 212, 0) != KinVerdict.TooFar) return "target outside the kinematic range but inside the nominal one must be too far";
            // A2: bisection
            var s = new KinSearch(10, 405);
            while (!s.Done) { double d = s.Next(); s.Report(d, d <= 212.3); }
            if (s.ResultNm > 212.3 || s.ResultNm < 212.3 - 0.5 || s.Probes > 12) return "KinSearch result " + s.ResultNm + " probes " + s.Probes;
            if (!double.IsNaN(s.Next())) return "KinSearch Next after Done";
            s.Report(100, true);
            if (s.Probes > 12) return "KinSearch Report after Done";
            var none = new KinSearch(10, 405);
            while (!none.Done) none.Report(none.Next(), false);
            if (none.ResultNm != 10) return "KinSearch nothing in range: " + none.ResultNm;
            var all = new KinSearch(10, 405);
            while (!all.Done) all.Report(all.Next(), true);
            if (all.ResultNm != 405 || all.Probes != 1) return "KinSearch all in range: " + all.ResultNm;
            if (!new KinSearch(5, 5).Done || !new KinSearch(double.NaN, 5).Done) return "KinSearch empty bracket";
            // A3: the metric
            var m = new RingGeoMetric { LatNmPerDeg = 60, LonNmPerDeg = 60, Valid = true };
            double lat, lon;
            OffsetMetric(36, -3, 90, 270, m, out lat, out lon);
            if (Math.Abs(lon - (-3 + 4.5)) > 1e-9 || Math.Abs(lat - 36) > 1e-9) return "OffsetMetric east " + lat + "," + lon;
            double glat, glon;
            RangeRingMath.Offset(36, -3, 90, 270, out glat, out glon);
            if (Math.Abs((glon + 3) - 5.56) > 0.01) return "geodesic reference " + (glon + 3);
            OffsetMetric(36, -3, 33, 100, new RingGeoMetric(), out lat, out lon);
            RangeRingMath.Offset(36, -3, 33, 100, out glat, out glon);
            if (lat != glat || lon != glon) return "!Valid = RangeRingMath.Offset";
            for (int b = 0; b < 360; b += 15)
            {
                OffsetMetric(36, -3, b, 270, m, out lat, out lon);
                double dn = (lat - 36) * 60.0, de = (lon + 3) * 60.0 * Math.Cos(36 * Math.PI / 180.0);
                if (Math.Sqrt(dn * dn + de * de) > CullNm(270, 36, m) + 1e-6) return "CullNm does not enclose the ring at " + b;
            }
            if (Math.Abs(CullNm(270, 36, m) - 270) > 1e-9 || CullNm(270, 36, new RingGeoMetric()) != 270) return "CullNm values";
            var ms = MetricFromSamples(0.1 * 60, 0, 0, -0.1 * 60, 0.1);
            if (!ms.Valid || Math.Abs(ms.LonNmPerDeg - 60) > 1e-9 || Math.Abs(ms.LatNmPerDeg - 60) > 1e-9) return "MetricFromSamples";
            if (MetricFromSamples(0, 0, 6, 0, 0.1).Valid || MetricFromSamples(double.NaN, 0, 6, 0, 0.1).Valid || MetricFromSamples(6, 0, 6, 0, 0).Valid) return "MetricFromSamples must drop 0 / NaN";
            if (CellKey(36.2, -3.1) != CellKey(36.4, -3.4) || CellKey(36.2, -3.1) == CellKey(36.6, -3.1) || CellKey(36.2, -3.1) == CellKey(36.2, -2.9)) return "CellKey";
            if (KinKey("ss-n-19", false, 5000, 400) != "ss-n-19|s" || KinKey("aim-7", true, 9000, 480) != KinKey("aim-7", true, 9400, 490) || KinKey("aim-7", true, 9000, 480) == KinKey("aim-7", true, 14000, 480)) return "KinKey";
            // C3: the frame budget
            var q = new KinQueue();
            int a = 0, bb = 0;
            q.Enqueue("a", () => ++a >= 3);
            q.Enqueue("b", () => { bb++; return true; });
            if (q.Enqueue("a", () => true)) return "KinQueue duplicate key";
            if (q.Pump() || a != 1 || bb != 0) return "KinQueue: one step per pump";
            if (!q.Pump() || bb != 1) return "KinQueue: b finished";
            q.Pump(); if (!q.Pump() || a != 3 || q.Count != 0 || q.Steps != 4) return "KinQueue: a finished after 3 steps";
            q.Enqueue("x", () => { throw new InvalidOperationException(); });
            if (!q.Pump() || q.Count != 0) return "KinQueue drops a throwing task";
            if (q.Pump()) return "KinQueue empty";
            return null;
        }
    }
}
