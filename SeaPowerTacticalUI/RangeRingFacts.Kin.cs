using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-321 / ADR-0125: the game-side numbers of the rings (the metric of the world, the effective range of an own weapon, the launcher verdict against the
    /// selected contact). Every call into the game happens in KinPump (RangeRingFeature.Update, the Unity main thread): at most ONE game call per frame, from a
    /// queue of tasks (RangeRingKin.KinQueue); the rest of the code only reads the caches. DiagJson reads the caches only.</summary>
    internal sealed partial class GameRingFacts
    {
        private sealed class Thr { public double Nm = double.NaN, At; }
        private sealed class Vt { public KinCheck C; public double At; }
        private sealed class Ammo { public KinAmmoInfo I; public double At; }
        private struct Met { public RingGeoMetric M; public double At; }

        private readonly KinQueue _kq = new KinQueue();
        private readonly Dictionary<long, Met> _metric = new Dictionary<long, Met>();
        private readonly Dictionary<string, Thr> _thr = new Dictionary<string, Thr>();
        private readonly Dictionary<string, Thr> _eff = new Dictionary<string, Thr>();
        private readonly Dictionary<string, Vt> _vt = new Dictionary<string, Vt>();
        private readonly Dictionary<string, Ammo> _kinAmmo = new Dictionary<string, Ammo>();
        private string _lastVerdict = "";
        private RingGeoMetric _lastMetric;
        private const double MetricRetryS = 10.0;

        /// <summary>How often a verdict, an air-launch threshold or a torpedo range is recomputed (s).</summary>
        public double KinRefreshS = 1.0;

        public int KinPending { get { return _kq.Count; } }
        public int KinCacheCount { get { return _thr.Count + _eff.Count + _vt.Count; } }

        private static string Tk(char kind, string key) { return kind + "|" + key; }

        private void ClearKin()
        {
            _kq.Clear(); _metric.Clear(); _thr.Clear(); _eff.Clear(); _vt.Clear(); _kinAmmo.Clear(); _lastVerdict = ""; _lastMetric = default(RingGeoMetric);
        }

        /// <summary>The frame budget of the game calls: one step of the queue. True = a task finished, so the scene must be rebuilt.</summary>
        public bool KinPump(double now)
        {
            _now = now;
            return _kq.Pump();
        }

        // ---- the metric: a location, not a unit; three cheap ToUnity samples, cached per 0.5 degree cell for the mission ----
        public RingGeoMetric Metric(RingPt at)
        {
            long key = RangeRingKin.CellKey(at.Lat, at.Lon);
            Met m;
            if (_metric.TryGetValue(key, out m) && (m.M.Valid || _now - m.At < MetricRetryS)) return m.M;
            RingGeoMetric g;
            if (!_api.RrGameMetric(at.Lat, at.Lon, out g)) g = default(RingGeoMetric);
            _metric[key] = new Met { M = g, At = _now };
            if (g.Valid) _lastMetric = g;
            return g;
        }

        private KinAmmoInfo KinInfo(int uid, string ammo)
        {
            string k = uid + "|" + ammo;
            Ammo a;
            if (_kinAmmo.TryGetValue(k, out a) && _now - a.At < RefreshS) return a.I;
            KinAmmoInfo i = _api.RrKinAmmo(uid, ammo, _allies);
            _kinAmmo[k] = new Ammo { I = i, At = _now };
            return i;
        }

        // ---- the effective radius of an own weapon ----
        public double ReachNm(int uid, AmmoFact a)
        {
            UnitSide s = SideOf(uid);
            if (s != UnitSide.Player && !(s == UnitSide.Ally && _allies)) throw new InvalidOperationException("ReachNm() of a foreign unit");
            KinAmmoInfo info = KinInfo(uid, a.Name);
            if (info == null) return double.NaN;
            if (info.Type == RingAmmoType.Torpedo) return EffectiveReach(uid, a.Name);
            if (!info.Kinematic) return double.NaN;   // the game uses the nominal range
            return ThresholdReach(uid, info);
        }

        private double EffectiveReach(int uid, string ammo)
        {
            string key = uid + "|" + ammo;
            Thr t;
            if (!_eff.TryGetValue(key, out t)) { t = new Thr { At = double.NegativeInfinity }; _eff[key] = t; }
            if (_now - t.At >= KinRefreshS && !_kq.Has(Tk('e', key)))
            {
                _kq.Enqueue(Tk('e', key), () =>
                {
                    double nm = _api.RrEffectiveNm(uid, ammo, _allies);
                    t.Nm = nm; t.At = _now;
                    return true;
                });
            }
            return t.Nm;
        }

        private double ThresholdReach(int uid, KinAmmoInfo info)
        {
            string key = RangeRingKin.KinKey(info.Name, info.AirLaunch, info.AltFt, info.SpeedKn);
            Thr t;
            if (!_thr.TryGetValue(key, out t)) { t = new Thr { At = double.NegativeInfinity }; _thr[key] = t; }
            // a ship or a submarine launches at speed 0: the threshold does not depend on the position, once per mission; an aircraft by altitude / speed class
            bool stale = info.AirLaunch ? _now - t.At >= KinRefreshS : double.IsNegativeInfinity(t.At) || (double.IsNaN(t.Nm) && _now - t.At >= MetricRetryS);   // review T-321: a failed ship search retries after 10 s
            if (stale && !_kq.Has(Tk('t', key)))
            {
                double lo = Math.Max(0, info.MinNm), hi = info.NominalNm * RangeRingKin.MaxBracketK;
                var search = new KinSearch(lo, hi);
                string ammo = info.Name;
                _kq.Enqueue(Tk('t', key), () =>
                {
                    double d = search.Next();
                    double max, dist;
                    if (double.IsNaN(d) || !_api.RrKinProbe(uid, ammo, d, _allies, out max, out dist)) { t.At = _now; t.Nm = double.NaN; return true; }
                    search.Report(d, max >= dist);
                    if (!search.Done) return false;
                    // nothing in range (the result stays at the minimum) or everything up to the bracket: not a usable threshold -> the nominal range stays
                    double r = search.ResultNm;
                    t.Nm = (r <= lo + 0.5 || r >= hi) ? double.NaN : r;
                    t.At = _now;
                    return true;
                });
            }
            return t.Nm;
        }

        // ---- the verdict against the selected contact ----
        public KinCheck VsTrack(int uid, AmmoFact a, int trackId)
        {
            UnitSide s = SideOf(uid);
            if (s != UnitSide.Player && !(s == UnitSide.Ally && _allies)) throw new InvalidOperationException("VsTrack() of a foreign unit");
            string key = uid + "|" + a.Name + "|" + trackId;
            Vt v;
            if (!_vt.TryGetValue(key, out v)) { v = new Vt { At = double.NegativeInfinity }; _vt[key] = v; }
            if (_now - v.At >= KinRefreshS && !_kq.Has(Tk('v', key)))
            {
                string ammo = a.Name;
                string shown = string.IsNullOrEmpty(a.Display) ? a.Name : a.Display;
                _kq.Enqueue(Tk('v', key), () =>
                {
                    double dist, max, min;
                    if (_api.RrKinVsTrack(uid, ammo, trackId, _allies, out dist, out max, out min))
                    {
                        v.C = new KinCheck { Verdict = RangeRingKin.Verdict(dist, max, min), DistNm = dist, MaxNm = max, MinNm = min };
                        _lastVerdict = "{\"uid\":" + uid + ",\"ammo\":\"" + ammo.Replace("\\", "/").Replace("\"", "'") + "\",\"track\":" + trackId + ",\"verdict\":\"" + v.C.Verdict
                            + "\",\"dist\":" + dist.ToString("0.#", CultureInfo.InvariantCulture) + ",\"max\":" + max.ToString("0.#", CultureInfo.InvariantCulture) + "}";
                    }
                    else v.C = null;
                    v.At = _now;
                    return true;
                });
            }
            return v.C;
        }

        /// <summary>Diagnostics: only numbers already in the caches, never a game call.</summary>
        public string KinDiagJson()
        {
            var inv = CultureInfo.InvariantCulture;
            RrKinStats st = _api.RrKinTotals;
            var sb = new StringBuilder("{");
            sb.Append("\"calls\":").Append(st.Calls).Append(",\"ms_avg\":").Append((st.Calls == 0 ? 0 : st.MsSum / st.Calls).ToString("0.###", inv)).Append(",\"ms_max\":").Append(st.MsMax.ToString("0.###", inv));
            sb.Append(",\"pending\":").Append(_kq.Count).Append(",\"cache\":").Append(KinCacheCount);
            sb.Append(",\"metric\":{\"lat_nm_deg\":").Append(_lastMetric.LatNmPerDeg.ToString("0.###", inv)).Append(",\"lon_nm_deg\":").Append(_lastMetric.LonNmPerDeg.ToString("0.###", inv)).Append("}");
            sb.Append(",\"last\":").Append(string.IsNullOrEmpty(_lastVerdict) ? "null" : _lastVerdict);
            return sb.Append('}').ToString();
        }
    }
}
