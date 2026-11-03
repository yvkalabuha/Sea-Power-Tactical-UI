using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-107 E (docs/research/seapower-crashes-and-evasion.md part B4): pure maths of the interface mod's own torpedo evasion.
    // No game, no Unity: compiled by the doctrinetest stand and the selfcheck-ui.

    /// <summary>A torpedo as the player knows it (plotting table / game announcement).</summary>
    internal sealed class EvTorp
    {
        public int TrackId;
        public double BrgDeg, DistNm, AgeS;
        /// <summary>T-193: robust range rate to the ship (kn, positive = closing); null = not enough samples.</summary>
        public double? ClosingKn;
        /// <summary>T-193: the track position has not changed for TrackFreshness.StaleS of mission time.</summary>
        public bool Stale;
        /// <summary>T-193: identified track whose ammo guidance is wake homing (no decoys, no beam turn).</summary>
        public bool WakeHoming;
        /// <summary>T-268 (ADR-0107): frozen track: distance to the pursuit point (the torpedo homing on the ship at its last speed); NaN = none (use DistNm).</summary>
        public double ThreatNm = double.NaN;
        /// <summary>T-297: frozen track: EvasionDecoyMath.FrozenTti of its last moving tti; +inf = none.</summary>
        public double FrozenTtiS = double.PositiveInfinity;
        /// <summary>T-268 (R1): frozen track whose pursuit has expired (the predicted intercept passed by ExpireAfterInterceptS, or the run endurance is exceeded): it no longer holds the evasion.</summary>
        public bool PursuitExpired;
    }

    /// <summary>T-215: memory of an ended evasion of one unit: the distance of every live track at the end.</summary>
    internal sealed class EvRearm
    {
        public double EndedS;
        public readonly Dictionary<int, double> EndDistNm = new Dictionary<int, double>();
    }

    /// <summary>
    /// T-193: robust range rate of one (protected uid, track) pair: Theil-Sen median of pairwise slopes over the last WindowS of mission time.
    /// Plotting-table tracks of wake-homing torpedoes jump by up to 2 nm between samples, a two-sample difference is useless.
    /// </summary>
    internal sealed class TrackClosing
    {
        public const double WindowS = 8, MinSpanS = 3, MinDtS = 0.5;
        public const int MinSamples = 3, MaxSamples = 12;
        private sealed class Series { public readonly List<double> T = new List<double>(), D = new List<double>(); }
        private readonly Dictionary<long, Series> _s = new Dictionary<long, Series>();

        private static long Key(int uid, int trackId) { return ((long)uid << 32) | (uint)trackId; }

        public int Count { get { return _s.Count; } }

        public void Update(int uid, int trackId, double distNm, double nowS)
        {
            long k = Key(uid, trackId);
            Series s;
            if (!_s.TryGetValue(k, out s)) { s = new Series(); _s[k] = s; }
            int n = s.T.Count;
            if (n > 0 && nowS < s.T[n - 1]) { s.T.Clear(); s.D.Clear(); n = 0; }   // the clock went back
            if (n > 0 && nowS - s.T[n - 1] < MinDtS) return;
            s.T.Add(nowS); s.D.Add(distNm);
            while (s.T.Count > 0 && (nowS - s.T[0] > WindowS || s.T.Count > MaxSamples)) { s.T.RemoveAt(0); s.D.RemoveAt(0); }
        }

        /// <summary>Median of (d_i - d_j) / (t_j - t_i) * 3600 over all sample pairs, knots (positive = closing); null = fewer than MinSamples or span under MinSpanS.</summary>
        public double? ClosingKn(int uid, int trackId)
        {
            Series s;
            if (!_s.TryGetValue(Key(uid, trackId), out s)) return null;
            int n = s.T.Count;
            if (n < MinSamples || s.T[n - 1] - s.T[0] < MinSpanS) return null;
            var slopes = new List<double>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double dt = s.T[j] - s.T[i];
                    if (dt > 0) slopes.Add((s.D[i] - s.D[j]) / dt * 3600.0);
                }
            if (slopes.Count == 0) return null;
            slopes.Sort();
            int m = slopes.Count;
            return m % 2 == 1 ? slopes[m / 2] : (slopes[m / 2 - 1] + slopes[m / 2]) / 2.0;
        }

        /// <summary>Smallest distance in the window; null = no samples.</summary>
        public double? MinDistNm(int uid, int trackId)
        {
            Series s;
            if (!_s.TryGetValue(Key(uid, trackId), out s) || s.D.Count == 0) return null;
            double m = double.MaxValue;
            foreach (double d in s.D) if (d < m) m = d;
            return m;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            List<long> dead = null;
            foreach (long k in _s.Keys)
                if (liveTrackIds == null || !liveTrackIds.Contains((int)(uint)(k & 0xFFFFFFFFL))) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) _s.Remove(k);
        }

        public void Clear() { _s.Clear(); }
    }

    /// <summary>T-193: a track whose position stays the same (within 0.005 nm) for StaleS of mission time is frozen (lost contact), not a threat.</summary>
    internal sealed class TrackFreshness
    {
        public const double StaleS = 30, SameNm = 0.005;
        /// <summary>T-352: false = the flat world of stable (1 deg = 60 nm on both axes), true = the beta globe (cos lat). The owner sets it from AswMath.WorldGlobe every tick.</summary>
        public bool Globe;
        private sealed class Rec { public double Lat, Lon, SinceS, LastS, MovedS = double.NaN; }
        private readonly Dictionary<int, Rec> _r = new Dictionary<int, Rec>();

        public void Update(int trackId, double lat, double lon, double nowS)
        {
            Rec r;
            if (!_r.TryGetValue(trackId, out r) || nowS < r.LastS) { _r[trackId] = new Rec { Lat = lat, Lon = lon, SinceS = nowS, LastS = nowS }; return; }
            r.LastS = nowS;
            double dy = (lat - r.Lat) * 60.0, dx = (lon - r.Lon) * 60.0 * (Globe ? Math.Cos(lat * Math.PI / 180.0) : 1.0);
            if (Math.Sqrt(dx * dx + dy * dy) > SameNm) { r.Lat = lat; r.Lon = lon; r.SinceS = nowS; r.MovedS = nowS; }
        }

        /// <summary>T-297: the mark moved (beyond SameNm) at most s ago; false = never moved / unknown.</summary>
        public bool MovedWithin(int trackId, double nowS, double s)
        {
            Rec r;
            return _r.TryGetValue(trackId, out r) && !double.IsNaN(r.MovedS) && nowS >= r.MovedS && nowS - r.MovedS <= s;
        }

        public bool Stale(int trackId, double nowS)
        {
            Rec r;
            return _r.TryGetValue(trackId, out r) && nowS - r.SinceS >= StaleS;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            List<int> dead = null;
            foreach (int k in _r.Keys) if (liveTrackIds == null || !liveTrackIds.Contains(k)) (dead ?? (dead = new List<int>())).Add(k);
            if (dead != null) foreach (int k in dead) _r.Remove(k);
        }

        public void Clear() { _r.Clear(); }
    }

    /// <summary>
    /// T-268 (ADR-0107): per (own uid, track) the point where a torpedo whose track froze may be now: it homes on the ship at the median
    /// speed of the last fresh plot moves (clamped MinKn..MaxKn; default DefaultKn / DefaultWakeKn). Flat-earth nm, no game.
    /// The pursuit expires (R1): ExpireAfterInterceptS after the point reached the ship, or when the run endurance (MaxRunNm at the speed) is exceeded.
    /// </summary>
    internal sealed class TrackPursuit
    {
        public const double WindowS = 60, MinDtS = 2, MinKn = 15, MaxKn = 50, DefaultKn = 40, DefaultWakeKn = 45, SameNm = 0.005;
        public const int MaxSamples = 12;
        /// <summary>T-268 R1: the track expires this long after the predicted intercept time (the point reached the ship) with the ship alive.</summary>
        public const double ExpireAfterInterceptS = 60;
        /// <summary>T-268 R1: longest run of a torpedo from the first sight of its track, nm (the endurance in seconds = MaxRunNm / speed).</summary>
        public const double MaxRunNm = 15, InterceptNm = 0.02;

        private sealed class Rec
        {
            public double Lat, Lon, TS, FirstS, InterceptS = double.NaN;
            public bool Wake;
            public readonly List<double> T = new List<double>(), V = new List<double>();
        }
        private readonly Dictionary<long, Rec> _r = new Dictionary<long, Rec>();

        private static long Key(int uid, int trackId) { return ((long)uid << 32) | (uint)trackId; }
        /// <summary>T-352: false = the flat world of stable, true = the beta globe; set by the owner every tick.</summary>
        public bool Globe;
        private static double DistNm(double lat1, double lon1, double lat2, double lon2, bool globe)
        {
            double dy = (lat2 - lat1) * 60.0, dx = (lon2 - lon1) * 60.0 * (globe ? Math.Cos(lat1 * Math.PI / 180.0) : 1.0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public int Count { get { return _r.Count; } }

        /// <summary>One tick of a live track. fresh = not Stale; plotted lat/lon of the track; own lat/lon of the protected ship.</summary>
        public void Update(int uid, int trackId, double lat, double lon, bool fresh, bool wake, double ownLat, double ownLon, double nowS)
        {
            long k = Key(uid, trackId);
            Rec r;
            if (!_r.TryGetValue(k, out r) || nowS < r.TS)
            {
                _r[k] = new Rec { Lat = lat, Lon = lon, TS = nowS, FirstS = nowS, Wake = wake };
                return;
            }
            r.Wake = wake;
            if (fresh)
            {
                double moved = DistNm(r.Lat, r.Lon, lat, lon, Globe);
                double dt = nowS - r.TS;
                if (moved <= SameNm || dt < MinDtS) return;
                r.T.Add(nowS); r.V.Add(moved / dt * 3600.0);
                while (r.T.Count > 0 && (nowS - r.T[0] > WindowS || r.T.Count > MaxSamples)) { r.T.RemoveAt(0); r.V.RemoveAt(0); }
                r.Lat = lat; r.Lon = lon; r.TS = nowS; r.InterceptS = double.NaN;
                return;
            }
            double step = Speed(r) * (nowS - r.TS) / 3600.0;
            double dist = DistNm(r.Lat, r.Lon, ownLat, ownLon, Globe);
            if (step >= dist) { r.Lat = ownLat; r.Lon = ownLon; }
            else if (dist > 0)
            {
                double f = step / dist;
                r.Lat += (ownLat - r.Lat) * f; r.Lon += (ownLon - r.Lon) * f;
            }
            r.TS = nowS;
            if (double.IsNaN(r.InterceptS) && DistNm(r.Lat, r.Lon, ownLat, ownLon, Globe) <= InterceptNm) r.InterceptS = nowS;
        }

        private static double Speed(Rec r)
        {
            int n = r.V.Count;
            if (n < 2) return r.Wake ? DefaultWakeKn : DefaultKn;
            var v = new List<double>(r.V);
            v.Sort();
            double m = n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
            return Math.Max(MinKn, Math.Min(MaxKn, m));
        }

        /// <summary>Distance own ship -> pursuit point (nm); NaN = unknown pair.</summary>
        public double PursuitNm(int uid, int trackId, double ownLat, double ownLon)
        {
            Rec r;
            return _r.TryGetValue(Key(uid, trackId), out r) ? DistNm(ownLat, ownLon, r.Lat, r.Lon, Globe) : double.NaN;
        }

        /// <summary>T-287: the current pursuit point of the pair; false = unknown pair.</summary>
        public bool Point(int uid, int trackId, out double lat, out double lon)
        {
            Rec r;
            if (_r.TryGetValue(Key(uid, trackId), out r)) { lat = r.Lat; lon = r.Lon; return true; }
            lat = 0; lon = 0;
            return false;
        }

        /// <summary>The speed used for the pair (median, clamped, or the default); null = unknown pair.</summary>
        public double? SpeedKn(int uid, int trackId)
        {
            Rec r;
            if (!_r.TryGetValue(Key(uid, trackId), out r)) return null;
            return Speed(r);
        }

        /// <summary>T-268 R1: the pursuit of a frozen track is over: ExpireAfterInterceptS after the point reached the ship, or the run endurance (MaxRunNm at the speed) plus that margin is exceeded since the first sight.</summary>
        public bool Expired(int uid, int trackId, double nowS)
        {
            Rec r;
            if (!_r.TryGetValue(Key(uid, trackId), out r)) return false;
            if (!double.IsNaN(r.InterceptS) && nowS - r.InterceptS >= ExpireAfterInterceptS) return true;
            return nowS - r.FirstS >= MaxRunNm / Speed(r) * 3600.0 + ExpireAfterInterceptS;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            List<long> dead = null;
            foreach (long k in _r.Keys)
                if (liveTrackIds == null || !liveTrackIds.Contains((int)(uint)(k & 0xFFFFFFFFL))) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) _r.Remove(k);
        }

        public void Clear() { _r.Clear(); }
    }

    /// <summary>T-203: the one "live torpedo track" rule of the evasion and the anti-torpedo fire: not gone, hostile or unknown, has a plotted position.</summary>
    internal static class TorpedoTrack
    {
        public static bool Live(bool gone, string relation, bool hasPos)
        {
            return !gone && (relation == "hostile" || relation == "unknown") && hasPos;
        }
    }

    internal sealed class EvState
    {
        public string Phase = "idle";                 // idle | evading | restoring
        public double StartedS, LastDecoyS = -1e9, LastSeenS;
        public double? CourseDeg;
        /// <summary>Remembered side of the escape course: +1 = bearing + offset, -1 = bearing - offset, 0 = none yet (T-113 E).</summary>
        public int Side;
        /// <summary>T-193: the track the escape course is built against (see EvasionMath.PickPrimary).</summary>
        public int PrimaryId = -1;
        /// <summary>T-193: mission time since which the nearest fresh torpedo has not been closing (NaN = it is closing / unknown).</summary>
        public double RecedingSinceS = double.NaN;
        /// <summary>T-203: distance of the last live torpedo track (fresh or frozen, last known position; double.MaxValue = no live track); set by the caller for Done().</summary>
        public double LastDistNm = double.MaxValue;
        /// <summary>T-280 (ADR-0113): mission time of the last tick with a live threat within LostNearNm (NaN = none / cleared).</summary>
        public double NearSeenS = double.NaN;
        /// <summary>T-280: that threat (ThreatDist) and its track id.</summary>
        public double NearNm = double.PositiveInfinity;
        public int NearTrackId = -1;
        /// <summary>T-280: bearing of that track's mark at NearSeenS (for the terrain guard of the held course).</summary>
        public double NearBrgDeg = double.NaN;
    }

    internal static class EvasionMath
    {
        public const double SternDeg = 150, BeamDeg = 90, CloseNm = 0.5;
        /// <summary>A remembered side is dropped for the other one only when the other turn is this much shorter (or the remembered one sweeps the bow over the torpedo).</summary>
        public const double HystDeg = 20;
        /// <summary>Largest change of the commanded course between two ticks (not applied when the torpedo is closer than CloseNm or the side flips).</summary>
        public const double MaxStepDeg = 25;
        public const double DecoyEveryS = 30, DecoyRangeNm = 6, DoneGoneS = 30, DoneFarNm = 8, MaxS = 300;
        /// <summary>T-193 (ADR-0083): start by own track (StartNm + closing >= StartMinClosingKn, or within StartCloseNm); a fresh torpedo within HoldNm keeps the evasion past MaxS up to HardMaxS;
        /// RecedeS of steady receding ends it; a second torpedo within OtherNm must stay OtherConeDeg off the bow; the primary track changes only for PrimaryHystS of TTI.</summary>
        public const double StartNm = 5, StartCloseNm = 2, StartMinClosingKn = 5, HoldNm = 3, HardMaxS = 1800, RecedeS = 30, PrimaryHystS = 20, OtherNm = 1.5, OtherConeDeg = 45;
        /// <summary>T-203 (ADR-0087): a frozen live track within HoldNm holds the evasion this long after the last fresh sight (then the old rules).</summary>
        public const double HoldStaleMaxS = 120;
        /// <summary>T-224 (ADR-0091): only frozen tracks for SonarSlowAfterS: run slow for the sonar; not with the nearest live track within SonarSlowMinNm; speed up to min(SonarSlowMaxKn, cavitation onset - margin).</summary>
        public const double SonarSlowAfterS = 20, SonarSlowMinNm = 3.0, SonarSlowMaxKn = 12, SonarSlowCavMarginKn = 1;
        public const int SonarSlowDefaultTelegraph = 2;
        /// <summary>T-268 (ADR-0107): a live track (fresh distance, frozen ThreatDist) within this distance holds the evasion up to HardMaxS; the slow run needs every threat beyond SonarSlowMinNm.
        /// HardMaxS 900 -> 1800 (Q2: a 71M at 25 kn runs 13.5 nm in about 1950 s; hits came 884-1007 s after the first sight).</summary>
        public const double NearHoldNm = 2.0;
        /// <summary>T-273 (ADR-0109): a frozen live track (ThreatDist) or a closing fresh one within this distance holds the evasion; the slow run starts only beyond SonarSlowStartNm (= HoldReleaseNm) and lasts down to SonarSlowMinNm.</summary>
        public const double HoldReleaseNm = 3.5, SonarSlowStartNm = 3.5;
        /// <summary>T-280 (ADR-0113): a threat within LostNearNm keeps the evasion LostNearHoldS after it was last known (the track left the plot).</summary>
        public const double LostNearNm = 2.0, LostNearHoldS = 120;

        /// <summary>T-280: call every tick with all live tracks (fresh and frozen) before Done. The nearest ThreatDist &lt;= LostNearNm refreshes the memory;
        /// otherwise, if the remembered track is still listed as Stale and PursuitExpired, the memory is cleared. A track that left the list keeps it.</summary>
        public static void NoteThreat(EvState s, double nowS, IList<EvTorp> live)
        {
            if (live == null) return;
            EvTorp best = null; double bestD = double.PositiveInfinity;
            foreach (var t in live)
            {
                double d = ThreatDist(t);
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best != null && bestD <= LostNearNm)
            {
                s.NearSeenS = nowS; s.NearNm = bestD; s.NearTrackId = best.TrackId; s.NearBrgDeg = best.BrgDeg;
                return;
            }
            if (double.IsNaN(s.NearSeenS)) return;
            foreach (var t in live)
                if (t.TrackId == s.NearTrackId && t.Stale && t.PursuitExpired) { s.NearSeenS = double.NaN; return; }
        }

        /// <summary>T-280: !NaN(NearSeenS) &amp;&amp; nowS - NearSeenS &lt; LostNearHoldS.</summary>
        public static bool LostNearHolds(EvState s, double nowS)
        {
            return !double.IsNaN(s.NearSeenS) && nowS - s.NearSeenS < LostNearHoldS;
        }

        /// <summary>T-268: Stale and ThreatNm known -> min(DistNm, ThreatNm); an expired frozen track -> +inf (no threat); else DistNm. null -> +inf.</summary>
        public static double ThreatDist(EvTorp t)
        {
            if (t == null) return double.PositiveInfinity;
            if (!t.Stale) return t.DistNm;
            if (t.PursuitExpired) return double.PositiveInfinity;
            return double.IsNaN(t.ThreatNm) ? t.DistNm : Math.Min(t.DistNm, t.ThreatNm);
        }

        public static double Norm360(double a) { a %= 360; return a < 0 ? a + 360 : a; }
        /// <summary>Signed shortest turn from a to b, -180..180 (positive = to starboard).</summary>
        public static double Diff(double a, double b) { double d = Norm360(b - a); return d > 180 ? d - 360 : d; }

        /// <summary>
        /// Course away from the torpedo: the torpedo ends up 150 deg off the bow (stern quarter), or 90 deg when it is closer than 0.5 nm.
        /// The side with the smaller turn wins, but a turn whose arc sweeps the bow over the torpedo bearing (or its track) is refused.
        /// A ship that already has the torpedo at least that far off the bow keeps its course.
        /// </summary>
        public static double CourseAway(double headingDeg, EvTorp t, double? torpCourseDeg)
        {
            double off = t.DistNm < CloseNm && !t.WakeHoming ? BeamDeg : SternDeg;
            double rel = Math.Abs(Diff(headingDeg, t.BrgDeg));
            if (rel >= off) return Norm360(headingDeg);
            double a = Norm360(t.BrgDeg + off), b = Norm360(t.BrgDeg - off);
            bool ca = Crosses(headingDeg, a, t.BrgDeg, torpCourseDeg), cb = Crosses(headingDeg, b, t.BrgDeg, torpCourseDeg);
            double ta = Math.Abs(Diff(headingDeg, a)), tb = Math.Abs(Diff(headingDeg, b));
            if (ca != cb) return ca ? b : a;
            return ta <= tb ? a : b;
        }

        /// <summary>
        /// CourseAway with memory (T-113 E): the side chosen first is kept while it stays safe (the bow does not sweep the torpedo bearing/track)
        /// and not worse than the other side by HystDeg; the commanded course moves at most MaxStepDeg per tick from the previous command.
        /// Updates s.Side and s.CourseDeg. A torpedo already at least the offset off the bow keeps the heading.
        /// </summary>
        public static double CourseAwaySticky(double headingDeg, EvTorp t, double? torpCourseDeg, EvState s)
        {
            return Sticky(headingDeg, t, torpCourseDeg, s, null);
        }

        /// <summary>
        /// T-193: CourseAwaySticky against the primary torpedo, plus every other fresh torpedo within OtherNm: the commanded course keeps them
        /// OtherConeDeg off the bow when one of the two sides allows it; when both sides do, the side with the larger smallest angle wins (until a side is remembered).
        /// A change of the primary track resets the remembered side (the course stays, the step limit applies). No others: same as CourseAwaySticky.
        /// </summary>
        public static double CourseAwayMulti(double headingDeg, EvTorp primary, double? torpCourseDeg, IList<EvTorp> others, EvState s)
        {
            if (s.PrimaryId != primary.TrackId) { s.Side = 0; s.PrimaryId = primary.TrackId; }
            List<EvTorp> near = null;
            if (others != null)
                foreach (var o in others)
                    if (o != null && o.TrackId != primary.TrackId && !o.Stale && o.DistNm <= OtherNm && (!o.ClosingKn.HasValue || o.ClosingKn.Value > 0)) (near ?? (near = new List<EvTorp>())).Add(o);
            return Sticky(headingDeg, primary, torpCourseDeg, s, near);
        }

        // smallest angle between the course and the bearing of any of the torpedoes
        private static double MinBowAngle(double courseDeg, IList<EvTorp> ts)
        {
            double m = 180;
            foreach (var o in ts) m = Math.Min(m, Math.Abs(Diff(courseDeg, o.BrgDeg)));
            return m;
        }

        private static double Sticky(double headingDeg, EvTorp t, double? torpCourseDeg, EvState s, IList<EvTorp> others)
        {
            bool multi = others != null && others.Count > 0;
            bool close = t.DistNm < CloseNm && !t.WakeHoming;   // a wake-homing torpedo follows the wake: the beam turn gains nothing
            double off = close ? BeamDeg : SternDeg;
            double rel = Math.Abs(Diff(headingDeg, t.BrgDeg));
            if (rel >= off && (!multi || MinBowAngle(headingDeg, others) > OtherConeDeg)) { s.CourseDeg = Norm360(headingDeg); return s.CourseDeg.Value; }
            double a = Norm360(t.BrgDeg + off), b = Norm360(t.BrgDeg - off);
            bool ca = Crosses(headingDeg, a, t.BrgDeg, torpCourseDeg), cb = Crosses(headingDeg, b, t.BrgDeg, torpCourseDeg);
            double ta = Math.Abs(Diff(headingDeg, a)), tb = Math.Abs(Diff(headingDeg, b));
            int side;
            bool flipped = false;
            if (s.Side == 0)
            {
                side = ca != cb ? (ca ? -1 : 1) : (ta <= tb ? 1 : -1);
            }
            else
            {
                side = s.Side;
                bool cc = side > 0 ? ca : cb, co = side > 0 ? cb : ca;
                double tc = side > 0 ? ta : tb, to = side > 0 ? tb : ta;
                if ((cc && !co) || (cc == co && tc > to + HystDeg)) { side = -side; flipped = true; }
            }
            if (multi)
            {
                double ma = MinBowAngle(a, others), mb = MinBowAngle(b, others);
                bool okA = ma > OtherConeDeg, okB = mb > OtherConeDeg;
                int pick = side;
                if (okA != okB) pick = okA ? 1 : -1;
                else if (s.Side == 0 || !okA) pick = ma >= mb ? 1 : -1;
                else if (side > 0 ? ma + HystDeg < mb : mb + HystDeg < ma) pick = -side;
                if (pick != side) { flipped = flipped || s.Side != 0; side = pick; }
            }
            s.Side = side;
            double target = side > 0 ? a : b;
            double prev = s.CourseDeg ?? double.NaN;
            double res = target;
            if (!double.IsNaN(prev) && !close && !flipped)
            {
                double d = Diff(prev, target);
                res = Norm360(prev + Math.Max(-MaxStepDeg, Math.Min(MaxStepDeg, d)));
            }
            s.CourseDeg = res;
            return res;
        }

        // the bow arc from heading to cand passes the torpedo bearing (or the line of its track when the course is known)
        internal static bool Crosses(double heading, double cand, double brg, double? torpCourse)
        {
            double d = Diff(heading, cand);
            if (Sweeps(heading, d, brg)) return true;
            if (torpCourse.HasValue)
            {
                // the track line through the ship's position points along torpCourse+180 (towards where it came from) and torpCourse
                double inc = Norm360(torpCourse.Value + 180);
                if (Sweeps(heading, d, inc)) return true;
            }
            return false;
        }

        private static bool Sweeps(double from, double signedTurn, double dir)
        {
            double x = Diff(from, dir);
            if (signedTurn >= 0) return x > 0 && x < signedTurn;
            return x < 0 && x > signedTurn;
        }

        /// <summary>Noisemaker: the first at once, then every 30 s, only while the torpedo is closer than 6 nm.</summary>
        public static bool DecoyNow(EvState s, double now, double distNm)
        {
            if (distNm >= DecoyRangeNm) return false;
            return now - s.LastDecoyS >= DecoyEveryS;
        }

        /// <summary>End: no torpedo on the plot for 30 s and the last distance over 8 nm, or 5 minutes passed.</summary>
        public static bool Done(EvState s, double now, double nearestDistNm, bool anyKnown)
        {
            if (now - s.StartedS >= MaxS) return true;
            return !anyKnown && now - s.LastSeenS >= DoneGoneS && nearestDistNm > DoneFarNm;
        }

        /// <summary>T-193: the decoy rule for a known track: none against a wake-homing torpedo (the wake seeker ignores noisemakers).</summary>
        public static bool DecoyNow(EvState s, double nowS, EvTorp t)
        {
            return t != null && !t.WakeHoming && DecoyNow(s, nowS, t.DistNm);
        }

        /// <summary>T-224 (ADR-0091): the game's ThreatingTorpedo starts the evasion within StartNm; beyond it only with a known closing of at least StartMinClosingKn.</summary>
        public static bool GameFlagStarts(EvTorp t)
        {
            if (t == null || t.Stale) return false;
            if (t.DistNm <= StartNm) return true;
            return t.ClosingKn.HasValue && t.ClosingKn.Value >= StartMinClosingKn;
        }

        /// <summary>
        /// T-193: whether to start the own evasion. The game's ThreatingTorpedo is rarely set for a wake-homing torpedo, so an own fresh track also starts it:
        /// within StartNm and closing at least StartMinClosingKn, or within StartCloseNm. A frozen track never starts it.
        /// </summary>
        public static bool ShouldStart(bool gameThreat, EvTorp t)
        {
            if (t == null || t.Stale) return false;
            if (gameThreat && GameFlagStarts(t)) return true;
            if (t.DistNm <= StartCloseNm && (!t.ClosingKn.HasValue || t.ClosingKn.Value > 0)) return true;
            return t.DistNm <= StartNm && t.ClosingKn.HasValue && t.ClosingKn.Value >= StartMinClosingKn;
        }

        // time to impact on the unit from the robust closing; +inf when unknown or not closing
        internal static double Tti(EvTorp t)
        {
            return t.ClosingKn.HasValue && t.ClosingKn.Value > 0.5 ? t.DistNm / t.ClosingKn.Value * 3600.0 : double.PositiveInfinity;
        }

        /// <summary>
        /// T-193: the torpedo the escape course is built against: the smallest time to impact, the current one kept unless the best is PrimaryHystS earlier
        /// (no TTI: nearer by 0.25 nm). Frozen tracks are ignored. Sets s.PrimaryId; changed = the id differs from the previous one. null = no fresh track.
        /// </summary>
        public static EvTorp PickPrimary(IList<EvTorp> ts, EvState s, out bool changed)
        {
            changed = false;
            EvTorp best = null, cur = null;
            if (ts != null)
                foreach (var t in ts)
                {
                    if (t == null || t.Stale) continue;
                    if (t.TrackId == s.PrimaryId) cur = t;
                    if (best == null || Better(t, best, 0)) best = t;
                }
            if (best == null) return null;
            EvTorp res = best;
            if (cur != null && !ReferenceEquals(cur, best) && !Better(best, cur, 1)) res = cur;
            changed = res.TrackId != s.PrimaryId;
            s.PrimaryId = res.TrackId;
            return res;
        }

        // a is better than b: strictly earlier TTI by margin hyst (seconds); both unknown: nearer by hyst * 0.0125 nm (0 -> any)
        private static bool Better(EvTorp a, EvTorp b, int useHyst)
        {
            double ta = Tti(a), tb = Tti(b);
            bool fa = !double.IsInfinity(ta), fb = !double.IsInfinity(tb);
            if (fa != fb) return fa;
            if (fa) return ta < tb - (useHyst != 0 ? PrimaryHystS : 0);
            return a.DistNm < b.DistNm - (useHyst != 0 ? 0.25 : 0);
        }

        /// <summary>T-193 overload without the frozen-track distance (nearestLiveNm = +inf).</summary>
        public static bool Done(EvState s, double nowS, EvTorp nearestFresh, bool anyKnown)
        {
            return Done(s, nowS, nearestFresh, anyKnown, double.PositiveInfinity);
        }

        /// <summary>T-224: 5-argument form; the frozen-track distance is derived: the nearest live track when it is not the fresh one, else +inf.</summary>
        public static bool Done(EvState s, double nowS, EvTorp nearestFresh, bool anyKnown, double nearestLiveNm)
        {
            bool fresh = anyKnown && nearestFresh != null;
            double frozen = fresh && nearestLiveNm >= nearestFresh.DistNm - 1e-9 ? double.PositiveInfinity : nearestLiveNm;
            return Done(s, nowS, nearestFresh, anyKnown, nearestLiveNm, frozen);
        }

        /// <summary>
        /// T-268 (ADR-0107) adds rule 2 (replaces ADR-0091 section 1, HardMaxS 1800): a fresh track within NearHoldNm, or a frozen one whose ThreatDist
        /// (nearestFrozenNm) is within NearHoldNm, holds up to HardMaxS; without live tracks the evasion ends as before.
        /// T-224 (ADR-0091) end of the ship evasion (replaces ADR-0083 section 4 / ADR-0087 section 2 / T-215 hold).
        /// HardMaxS always. A fresh track within HoldNm that is not receding for RecedeS holds. A frozen live track within HoldNm (nearestFrozenNm) holds
        /// up to HoldStaleMaxS after s.LastSeenS. Otherwise MaxS ends it; beyond HoldNm 30 s of steady receding ends it earlier.
        /// Nothing fresh: MaxS, or DoneGoneS without a fresh track and the last live distance over DoneFarNm.
        /// nearestFresh = nearest non-frozen torpedo (null = none). The caller keeps s.LastSeenS (last fresh tick) and s.LastDistNm (last live distance).
        /// </summary>
        public static bool Done(EvState s, double nowS, EvTorp nearestFresh, bool anyKnown, double nearestLiveNm, double nearestFrozenNm)
        {
            if (nowS - s.StartedS >= HardMaxS) return true;
            bool fresh = anyKnown && nearestFresh != null;
            if (fresh && nearestFresh.ClosingKn.HasValue && nearestFresh.ClosingKn.Value <= 0)
            {
                if (double.IsNaN(s.RecedingSinceS)) s.RecedingSinceS = nowS;
            }
            else s.RecedingSinceS = double.NaN;
            bool receding = !double.IsNaN(s.RecedingSinceS) && nowS - s.RecedingSinceS >= RecedeS;
            if (fresh && nearestFresh.DistNm <= NearHoldNm) return false;   // T-268 (ADR-0107): a fresh track near holds even when receding (Q1)
            if (LostNearHolds(s, nowS)) return false;                       // T-280 (ADR-0113): a near threat left the plot, hold LostNearHoldS
            if (nearestFrozenNm <= HoldReleaseNm) return false;             // T-273 (ADR-0109): a frozen live track whose threat is within HoldReleaseNm holds up to HardMaxS (was NearHoldNm; HoldStaleMaxS rule absorbed)
            if (fresh && nearestFresh.DistNm <= HoldReleaseNm && !receding) return false;   // T-273: was HoldNm
            if (fresh)
            {
                if (receding && nearestFresh.DistNm > HoldNm) return true;
                return nowS - s.StartedS >= MaxS;
            }
            if (nowS - s.StartedS >= MaxS) return true;
            return nowS - s.LastSeenS >= DoneGoneS && s.LastDistNm > DoneFarNm;
        }

        /// <summary>T-278 (N2): the rule that ended the run; call right after Done returned true (same arguments): hardmax | receding | maxs | gone.</summary>
        public static string DoneWhy(EvState s, double nowS, EvTorp nearestFresh, bool anyKnown)
        {
            if (nowS - s.StartedS >= HardMaxS) return "hardmax";
            bool fresh = anyKnown && nearestFresh != null;
            bool receding = !double.IsNaN(s.RecedingSinceS) && nowS - s.RecedingSinceS >= RecedeS;
            if (fresh && receding && nearestFresh.DistNm > HoldNm) return "receding";
            if (nowS - s.StartedS >= MaxS) return "maxs";
            return fresh ? "maxs" : "gone";
        }

        /// <summary>T-224: slow down for the sonar: only frozen tracks, the last fresh sight at least SonarSlowAfterS ago, the nearest live track beyond SonarSlowMinNm, not wake-homing.
        /// T-268 (ADR-0107): the caller passes the minimum ThreatDist of all live tracks and "any live track is wake homing"; the threshold is 2 nm.</summary>
        public static bool SonarSlowNow(EvState s, double nowS, double nearestLiveNm, bool nearestWake)
        {
            return SonarSlowNow(s, nowS, nearestLiveNm, nearestWake, false);
        }

        /// <summary>T-273 (ADR-0109): hysteresis: the slow run starts beyond SonarSlowStartNm and lasts while the threat is beyond SonarSlowMinNm.</summary>
        public static bool SonarSlowNow(EvState s, double nowS, double nearestThreatNm, bool anyWake, bool slowed)
        {
            if (LostNearHolds(s, nowS)) return false;   // T-284 (ADR-0113): a close threat left the plot, ADR-0113 promises full speed for the whole hold
            double lim = slowed ? SonarSlowMinNm : SonarSlowStartNm;
            return nowS - s.LastSeenS >= SonarSlowAfterS && nearestThreatNm > lim && !anyWake;
        }

        /// <summary>
        /// T-224: the telegraph of the slow run. stepsKn index = telegraph + 1 (game _telegraphVelocities, -1..5); cavOnsetKn NaN = unknown.
        /// Highest telegraph 1..4 with stepsKn <= min(SonarSlowMaxKn, cavOnsetKn - SonarSlowCavMarginKn); none -> 1; stepsKn null or shorter than 6 -> SonarSlowDefaultTelegraph.
        /// </summary>
        public static int SonarSlowTelegraph(double[] stepsKn, double cavOnsetKn)
        {
            if (stepsKn == null || stepsKn.Length < 6) return SonarSlowDefaultTelegraph;
            double lim = SonarSlowMaxKn;
            if (!double.IsNaN(cavOnsetKn) && cavOnsetKn - SonarSlowCavMarginKn < lim) lim = cavOnsetKn - SonarSlowCavMarginKn;
            for (int tg = 4; tg >= 1; tg--)
                if (stepsKn[tg + 1] <= lim + 1e-9) return tg;
            return 1;
        }

        /// <summary>T-215: how much closer than at the end of the previous evasion a track must be to start it again (beyond HoldNm).</summary>
        public const double RearmNm = 0.5;

        /// <summary>T-215: memory of the end: every live track (fresh and frozen) -> its distance; several tracks with one id -> the smallest distance.</summary>
        public static EvRearm EndMemory(IList<EvTorp> live, double nowS)
        {
            var r = new EvRearm { EndedS = nowS };
            if (live != null)
                foreach (var t in live)
                {
                    if (t == null) continue;
                    double old;
                    if (!r.EndDistNm.TryGetValue(t.TrackId, out old) || t.DistNm < old) r.EndDistNm[t.TrackId] = t.DistNm;
                }
            return r;
        }

        /// <summary>T-215: the track may start the evasion again: not in the memory, or within HoldNm (safety), or at least RearmNm closer than at the end.</summary>
        public static bool Rearmed(EvRearm r, EvTorp t)
        {
            if (r == null || t == null) return true;
            if (t.DistNm <= HoldNm) return true;
            double end;
            if (!r.EndDistNm.TryGetValue(t.TrackId, out end)) return true;
            return t.DistNm <= end - RearmNm + 1e-9;
        }

        /// <summary>T-221: why the re-arm rule let the track start: none (no memory) | new (id not in the memory) | inside (within HoldNm) | closer (RearmNm closer than at the end).</summary>
        public static string RearmTag(EvRearm r, EvTorp t)
        {
            if (r == null || t == null) return "none";
            double end;
            if (!r.EndDistNm.TryGetValue(t.TrackId, out end)) return "new";
            if (t.DistNm <= HoldNm) return "inside";
            return t.DistNm <= end - RearmNm + 1e-9 ? "closer" : "none";
        }

        /// <summary>T-215: the nearest fresh track that passes ShouldStart(gameThreat, t) and Rearmed(r, t); null = no start. fresh is sorted nearest first or not, both work.</summary>
        public static EvTorp StartTrack(bool gameThreat, IList<EvTorp> fresh, EvRearm r)
        {
            EvTorp best = null;
            if (fresh == null) return null;
            foreach (var t in fresh)
            {
                if (!ShouldStart(gameThreat, t) || !Rearmed(r, t)) continue;
                if (best == null || t.DistNm < best.DistNm) best = t;
            }
            return best;
        }

        /// <summary>T-215: drop the ids that are no longer live (the memory follows the plot).</summary>
        public static void RearmForget(EvRearm r, IList<EvTorp> live)
        {
            if (r == null) return;
            var ids = new HashSet<int>();
            if (live != null) foreach (var t in live) if (t != null) ids.Add(t.TrackId);
            var drop = new List<int>();
            foreach (var k in r.EndDistNm.Keys) if (!ids.Contains(k)) drop.Add(k);
            foreach (var k in drop) r.EndDistNm.Remove(k);
        }

        public static string SelfTest()
        {
            // T-352 worldflat: the track maths follow the world flag (flat on stable, cos(lat) on the beta globe)
            foreach (bool globe in new[] { false, true })
            {
                double cos60 = Math.Cos(60.0 * Math.PI / 180.0);
                var fw = new TrackFreshness { Globe = globe };
                fw.Update(3, 60, 10, 0); fw.Update(3, 60, 10 + 1.4 * TrackFreshness.SameNm / 60.0, 1);   // 1.4 SameNm flat, 0.7 SameNm on the globe
                if (fw.MovedWithin(3, 1, 5) != !globe) return "worldflat freshness globe=" + globe;
                var pw = new TrackPursuit { Globe = globe };
                pw.Update(1, 7, 60, 10.1, true, false, 60, 10, 0);   // 0.1 deg east of the ship
                double d = pw.PursuitNm(1, 7, 60, 10), want = globe ? 6.0 * cos60 : 6.0;
                if (Math.Abs(d - want) > 1e-9) return "worldflat pursuit globe=" + globe + ": " + d;
            }
            // torpedo 30 deg to starboard, 3 nm, heading 0 -> course 240 (torpedo 150 off the bow, smaller turn, bow does not sweep the bearing)
            var t = new EvTorp { TrackId = 1, BrgDeg = 30, DistNm = 3 };
            double c = CourseAway(0, t, null);
            if (Math.Abs(Diff(c, 240)) > 0.5) return "stern course " + c;
            if (Math.Abs(Math.Abs(Diff(c, t.BrgDeg)) - 150) > 0.5) return "stern offset";
            // close: beam
            t = new EvTorp { TrackId = 1, BrgDeg = 30, DistNm = 0.4 };
            c = CourseAway(0, t, null);
            if (Math.Abs(Math.Abs(Diff(c, t.BrgDeg)) - 90) > 0.5 || Math.Abs(Diff(c, 300)) > 0.5) return "beam course " + c;
            // dead astern: keep course
            t = new EvTorp { TrackId = 1, BrgDeg = 180, DistNm = 3 };
            if (Math.Abs(Diff(CourseAway(0, t, null), 0)) > 0.5) return "astern keeps course";
            // torpedo on the bow: turn, never through the bearing
            t = new EvTorp { TrackId = 1, BrgDeg = 10, DistNm = 4 };
            c = CourseAway(0, t, null);
            if (Math.Abs(Diff(c, 220)) > 0.5) return "bow torpedo course " + c;
            // --- T-113 sticky side and step limit
            {
                var es = new EvState();
                double hd = 0, brg = 30, prev = double.NaN; int side0 = 0; double[] drift = { 0, 8, -6, 10, -10, 4, -8, 9, -3, 6 };
                for (int i = 0; i < 10; i++)
                {
                    var tt = new EvTorp { TrackId = 1, BrgDeg = Norm360(brg + drift[i]), DistNm = 3 };
                    double cmd = CourseAwaySticky(hd, tt, null, es);
                    if (i == 0) side0 = es.Side;
                    if (es.Side != side0 || es.Side == 0) return "sticky: side changed at tick " + i;
                    if (!double.IsNaN(prev) && Math.Abs(Diff(prev, cmd)) > MaxStepDeg + 1e-6) return "sticky: step " + Math.Abs(Diff(prev, cmd)) + " at tick " + i;
                    prev = cmd;
                    hd = Norm360(hd + Math.Max(-12, Math.Min(12, Diff(hd, cmd))));   // the ship turns towards the command
                    brg = Norm360(brg - 4);                                           // the torpedo bearing slides as the ship turns
                }
                // the remembered side now sweeps the bow over the bearing: exactly one change of side
                var sw = new EvState { Side = 1, CourseDeg = 180 };
                double c1 = CourseAwaySticky(0, new EvTorp { BrgDeg = 30, DistNm = 3 }, null, sw);
                if (sw.Side != -1 || Math.Abs(Diff(c1, 240)) > 0.5) return "sticky: dangerous side not left " + c1 + " side " + sw.Side;
                CourseAwaySticky(0, new EvTorp { BrgDeg = 32, DistNm = 3 }, null, sw);
                if (sw.Side != -1) return "sticky: side flipped back";
                // close: beam at once, no step limit
                var cl = new EvState { Side = -1, CourseDeg = 240 };
                double c2 = CourseAwaySticky(0, new EvTorp { BrgDeg = 30, DistNm = 0.4 }, null, cl);
                if (Math.Abs(Diff(c2, 300)) > 0.5) return "sticky: close beam " + c2;
                // already far enough off the bow: keep the heading
                double c3 = CourseAwaySticky(10, new EvTorp { BrgDeg = 200, DistNm = 3 }, null, new EvState());
                if (Math.Abs(Diff(c3, 10)) > 0.5) return "sticky: keeps heading " + c3;
            }
            // decoy
            var s = new EvState { Phase = "evading", StartedS = 0 };
            if (!DecoyNow(s, 0, 3)) return "decoy at t=0";
            s.LastDecoyS = 0;
            if (DecoyNow(s, 29, 3)) return "decoy at 29 s";
            if (!DecoyNow(s, 30, 3)) return "decoy at 30 s";
            if (DecoyNow(s, 30, 7)) return "decoy at 7 nm";
            // done
            s = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10 };
            if (Done(s, 39, 9, false)) return "done too early";
            if (!Done(s, 40, 9, false)) return "done after 30 s gone";
            if (Done(s, 40, 5, false)) return "done at 5 nm";
            if (Done(s, 40, 9, true)) return "done while known";
            if (!Done(s, 300, 1, true)) return "done after 5 min";
            // --- T-193 A0: TrackClosing / TrackFreshness
            {
                const double kn45 = 45.0 / 3600.0;
                var tc = new TrackClosing();
                for (int i = 0; i < 4; i++) tc.Update(1, 7, 3.0 - kn45 * i, 10 + i);
                double? ck = tc.ClosingKn(1, 7);
                if (!ck.HasValue || Math.Abs(ck.Value - 45) > 0.5) return "t193 closing 45 kn: " + ck;
                var t2 = new TrackClosing(); t2.Update(1, 7, 3, 10); t2.Update(1, 7, 3 - kn45, 11);
                if (t2.ClosingKn(1, 7).HasValue) return "t193 closing: 2 samples";
                var tj = new TrackClosing();
                for (int i = 0; i < 8; i++) tj.Update(1, 7, 3.0 - kn45 * i + (i == 4 ? 2.0 : 0), 10 + i);
                ck = tj.ClosingKn(1, 7);
                if (!ck.HasValue || Math.Abs(ck.Value - 45) > 10) return "t193 closing with a 2 nm jump: " + ck;
                var tr = new TrackClosing();
                for (int i = 0; i < 5; i++) tr.Update(1, 7, 3.0 + 10.0 / 3600.0 * i, 10 + i);
                ck = tr.ClosingKn(1, 7);
                if (!ck.HasValue || Math.Abs(ck.Value + 10) > 1) return "t193 closing receding: " + ck;
                if (!tr.MinDistNm(1, 7).HasValue || Math.Abs(tr.MinDistNm(1, 7).Value - 3.0) > 1e-9) return "t193 MinDistNm";
                tr.Update(1, 7, 3, 2);   // clock went back
                if (tr.ClosingKn(1, 7).HasValue) return "t193 clock back must reset the pair";
                tc.Update(2, 8, 1, 10);
                tc.Prune(new List<int> { 7 });
                if (tc.Count != 1) return "t193 Prune";
                tc.Clear();
                if (tc.Count != 0) return "t193 Clear";
                var fr = new TrackFreshness();
                fr.Update(5, 10, 20, 0); fr.Update(5, 10, 20, 15); fr.Update(5, 10, 20, 29);
                if (fr.Stale(5, 29)) return "t193 stale too early";
                fr.Update(5, 10, 20, 30);
                if (!fr.Stale(5, 30)) return "t193 frozen 30 s must be stale";
                fr.Update(5, 10.01, 20, 31);
                if (fr.Stale(5, 31)) return "t193 moved track must be fresh";
                fr.Prune(new List<int>());
                if (fr.Stale(5, 100)) return "t193 freshness Prune";
            }
            // --- T-193 A2 Done
            {
                Func<double, double?, EvTorp> mk = (d, c) => new EvTorp { TrackId = 1, DistNm = d, ClosingKn = c };
                var sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (Done(sd, 301, mk(0.4, 3), true)) return "t193 done: torpedo at 0.4 nm after 300 s";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (!Done(sd, 301, mk(3.6, 3), true)) return "t193 done: 3.6 nm after 300 s";   // T-273: 3.5 nm closing now holds (HoldReleaseNm)
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                bool end = false;
                for (int q = 100; q <= 129; q++) end |= Done(sd, q, mk(1.5, -5), true);
                if (end) return "t193 done: receding under 30 s";
                if (Done(sd, 130, mk(1.5, -5), true)) return "t203 done: receding 30 s at 1.5 nm must hold (inside HoldNm)";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                end = false;
                for (int q = 100; q <= 129; q++) end |= Done(sd, q, mk(3.5, -5), true);
                if (end || !Done(sd, 130, mk(3.5, -5), true)) return "t203 done: receding 30 s at 3.5 nm";
                if (DoneWhy(sd, 130, mk(3.5, -5), true) != "receding") return "t278 done_why: receding expected, got " + DoneWhy(sd, 130, mk(3.5, -5), true);
                var sw = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (!Done(sw, 301, mk(3.6, 3), true) || DoneWhy(sw, 301, mk(3.6, 3), true) != "maxs") return "t278 done_why: maxs (fresh) expected";
                sw = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 1799 };
                if (!Done(sw, 1800, mk(1.0, 3), true) || DoneWhy(sw, 1800, mk(1.0, 3), true) != "hardmax") return "t278 done_why: hardmax expected";
                sw = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                if (!Done(sw, 301, null, false, 20, double.PositiveInfinity) || DoneWhy(sw, 301, null, false) != "maxs") return "t278 done_why: maxs (no fresh) expected";
                sw = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100, LastDistNm = 12 };
                if (!Done(sw, 140, null, false, 12, double.PositiveInfinity) || DoneWhy(sw, 140, null, false) != "gone") return "t278 done_why: gone expected";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                Done(sd, 100, mk(0.6, -5), true);
                if (Done(sd, 140, mk(0.6, -5), true)) return "t193 done: receding but inside 1 nm";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 9 };
                if (Done(sd, 39, null, false) || !Done(sd, 40, null, false)) return "t193 done: gone 30 s, 9 nm";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 5 };
                if (Done(sd, 100, null, false)) return "t193 done: gone, last 5 nm";
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = HardMaxS - 1 };
                if (!Done(sd, HardMaxS, mk(0.2, 40), true)) return "t193 done: hard cap";   // T-268: HardMaxS
                sd = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = double.MaxValue };
                if (!Done(sd, 301, null, false)) return "t193 done: only frozen tracks after 300 s";
            }
            // --- T-203 A1 TorpedoTrack.Live
            {
                if (!TorpedoTrack.Live(false, "hostile", true) || !TorpedoTrack.Live(false, "unknown", true)) return "t203 live: hostile/unknown";
                if (TorpedoTrack.Live(true, "hostile", true) || TorpedoTrack.Live(false, "friendly", true) || TorpedoTrack.Live(false, "neutral", true) || TorpedoTrack.Live(false, "hostile", false)) return "t203 live: gone/friendly/neutral/no position";
            }
            // --- T-203 A2 Done: a live track within HoldNm holds the evasion
            {
                Func<double, double?, EvTorp> mk = (d, c) => new EvTorp { TrackId = 1, DistNm = d, ClosingKn = c };
                var sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 2 };
                if (Done(sf, 60, null, false, 2.0)) return "t203 done: only a frozen track at 2 nm, now 60 (the 04:07:31 case)";
                if (Done(sf, 139, null, false, 2.0)) return "t203 done: frozen 2 nm, last sight 129 s ago, old rules: last 2 nm <= DoneFarNm";
                if (!Done(sf, 301, null, false, 3.6)) return "t203 done: frozen 3.6 nm after MaxS";   // T-273: 3.6 nm (inside HoldReleaseNm it holds)
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 5 };
                if (Done(sf, 100, null, false, 5.0)) return "t203 done: frozen 5 nm, last 5 nm <= DoneFarNm";
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 9 };
                if (Done(sf, 39, null, false, double.PositiveInfinity) || !Done(sf, 40, null, false, double.PositiveInfinity)) return "t203 done: no live track, 30 s, 9 nm";
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                bool e1 = false;
                for (int q = 100; q <= 130; q++) e1 |= Done(sf, q, mk(2.6, -5), true, 2.6);
                if (e1) return "t203 done: receding 30 s at 2.6 nm (the 04:11:35 case)";
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                bool e2 = false;
                for (int q = 100; q <= 129; q++) e2 |= Done(sf, q, mk(3.5, -5), true, 3.5);
                if (e2 || !Done(sf, 130, mk(3.5, -5), true, 3.5)) return "t203 done: receding 30 s at 3.5 nm";
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10 };
                if (!Done(sf, HardMaxS, null, false, 0.5)) return "t203 done: HardMaxS with a frozen track at 0.5 nm";
                // a fresh track far away, a frozen one near and a recent sight: holds
                sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 399 };
                if (Done(sf, 400, mk(5, -5), true, 1.0)) return "t203 done: frozen near track + fresh far track";
            }
            // --- T-203 A3 jumping track updates (20..60 s) never end the evasion within HoldNm
            {
                const double kn45 = 45.0 / 3600.0;
                var fr = new TrackFreshness();
                var sj = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0 };
                int[] upd = { 0, 20, 65, 125, 150, 210 };
                double lastUpdDist = 3.0; int ui = 0;
                for (int tm = 0; tm <= 215; tm++)
                {
                    double trueD = 3.0 - kn45 * tm;
                    if (ui < upd.Length && tm == upd[ui]) { lastUpdDist = trueD; ui++; }
                    double plotted = lastUpdDist;
                    fr.Update(1, plotted, 0, tm);   // the plotted position changes only at the update moments
                    bool frozen = fr.Stale(1, tm) || tm == 60;   // t == 60: the game says HasLiveTrack=false
                    EvTorp tj = new EvTorp { TrackId = 1, DistNm = plotted, Stale = frozen, ClosingKn = frozen ? (double?)null : 40 };
                    EvTorp nf = frozen ? null : tj;
                    if (nf != null) { sj.LastSeenS = tm; }
                    sj.LastDistNm = plotted;
                    if (plotted <= HoldNm && Done(sj, tm, nf, nf != null, plotted)) return "t203 jumping track: evasion ended at t=" + tm + " d=" + plotted;
                }
            }
            // --- T-215 end hysteresis and re-arm
            {
                Func<double, double?, EvTorp> mk = (d, c) => new EvTorp { TrackId = 7023, DistNm = d, ClosingKn = c };
                var s1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 300 };
                if (!Done(s1, 301, mk(3.6, 16), true, 3.6)) return "t215 done (T-224): closing torpedo at 3.6 nm ends at MaxS";   // T-273: 3.6 nm (closing within 3.5 holds)
                s1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 300 };
                if (!Done(s1, 301, mk(4.9, 16), true, 4.9)) return "t215 done (T-224): closing torpedo at 4.9 nm ends at MaxS";
                s1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 300 };
                if (!Done(s1, HardMaxS, mk(3.2, 16), true, 3.2)) return "t215 done: HardMaxS must end";
                s1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 300 };
                if (!Done(s1, 301, mk(3.6, 3), true, 3.6)) return "t215 done: slow closing ends at MaxS";   // T-273: 3.6 nm
                s1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 300 };
                if (!Done(s1, 301, mk(6, 16), true, 6)) return "t215 done: outside StartNm ends at MaxS";
                var r = EndMemory(new List<EvTorp> { mk(3.17, 16), new EvTorp { TrackId = 7023, DistNm = 3.5 } }, 300);
                if (Math.Abs(r.EndDistNm[7023] - 3.17) > 1e-9) return "t215 memory: smallest distance";
                if (Rearmed(r, mk(3.10, 16))) return "t215 rearm: 3.10 nm vs 3.17 must wait";
                if (!Rearmed(r, mk(2.99, 16))) return "t215 rearm: inside HoldNm must start (safety)";
                if (!Rearmed(r, mk(2.67, 16))) return "t215 rearm: closed 0.5 nm";
                if (!Rearmed(r, new EvTorp { TrackId = 7099, DistNm = 4, ClosingKn = 16 })) return "t215 rearm: new track";
                if (!Rearmed(null, mk(3.1, 16))) return "t215 rearm: no memory";
                var far = EndMemory(new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 9.8, ClosingKn = 16 } }, 0);
                if (StartTrack(true, new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 9.8, ClosingKn = 16 } }, far) != null) return "t215 start: 9.8 nm repeat";
                var st = StartTrack(true, new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 9.3, ClosingKn = 16 } }, far);
                if (st == null || st.TrackId != 5) return "t215 start: 9.3 nm after 9.8";
                var two = new List<EvTorp> { mk(3.17, 16), new EvTorp { TrackId = 7024, DistNm = 3.4, ClosingKn = 16 } };
                var mem2 = EndMemory(new List<EvTorp> { mk(3.17, 16) }, 0);
                st = StartTrack(true, two, mem2);
                if (st == null || st.TrackId != 7024) return "t215 start: new track must start";
                if (StartTrack(true, new List<EvTorp> { mk(3.17, 16), new EvTorp { TrackId = 7024, DistNm = 3.4, ClosingKn = 16 } }, EndMemory(two, 0)) != null) return "t215 start: both remembered";
                var old = new List<EvTorp> { new EvTorp { TrackId = 1, DistNm = 4, ClosingKn = 12 }, new EvTorp { TrackId = 2, DistNm = 1.5 } };
                st = StartTrack(false, old, null);
                if (st == null || st.TrackId != 2) return "t215 start: no memory = ShouldStart on nearest";
                // chase (T-224): the torpedo comes from 3.2 nm at 16 kn, a 1 s tick; outside HoldNm the evasion ends at MaxS
                var sc = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0 };
                double d0 = 3.2, ends = -1;
                for (int q = 1; q <= (int)HardMaxS + 5; q++)
                {
                    var tq = new EvTorp { TrackId = 7023, DistNm = d0 + 0.5, ClosingKn = 16 };   // the ship runs away: the gap shrinks slowly, stays within StartNm
                    sc.LastSeenS = q; sc.LastDistNm = tq.DistNm;
                    if (Done(sc, q, tq, true, tq.DistNm)) { ends = q; break; }
                }
                if (ends != MaxS) return "t215 chase: ended at " + ends + " instead of MaxS";
                var mem3 = EndMemory(new List<EvTorp> { new EvTorp { TrackId = 7023, DistNm = 3.7, ClosingKn = 16 } }, ends);
                double firstStart = -1;
                for (double dd = 3.7; dd > 0.5; dd -= 0.01)
                    if (StartTrack(false, new List<EvTorp> { new EvTorp { TrackId = 7023, DistNm = dd, ClosingKn = 16 } }, mem3) != null) { firstStart = dd; break; }
                if (firstStart < 0 || firstStart > 3.7 - RearmNm + 0.011) return "t215 chase: restart at " + firstStart;
                var mem4 = EndMemory(new List<EvTorp> { mk(5, 16), mk(4, 16) }, 0);
                RearmForget(mem4, new List<EvTorp> { new EvTorp { TrackId = 9, DistNm = 4 } });
                if (mem4.EndDistNm.Count != 0) return "t215 forget";
            }
            // --- T-221: the log scenario of Yorktown (torpedo 7031 receding at ~7 nm, the game flag still on): no restart cycle with the memory kept
            {
                var tp = new EvTorp { TrackId = 7031, DistNm = 6.87, ClosingKn = -1 };
                var mem = EndMemory(new List<EvTorp> { tp }, 0);
                int starts = 0, starts0 = 0;
                for (int q = 0; q < 600; q++)
                {
                    var t2 = new EvTorp { TrackId = 7031, DistNm = 6.87 + 0.004 * q, ClosingKn = -1 };
                    if (StartTrack(true, new List<EvTorp> { t2 }, mem) != null) starts++;
                    if (StartTrack(true, new List<EvTorp> { t2 }, null) != null) starts0++;
                }
                if (starts != 0) return "t221 flap: restart with the memory " + starts;
                if (starts0 != 0) return "t221 flap (T-224): the flag beyond StartNm needs a closing, no start without the memory either " + starts0;
                if (StartTrack(true, new List<EvTorp> { new EvTorp { TrackId = 7031, DistNm = 6.37, ClosingKn = 5 } }, mem) == null) return "t221 closer: no restart at 6.37";
                if (StartTrack(true, new List<EvTorp> { new EvTorp { TrackId = 7031, DistNm = 2.99, ClosingKn = 5 } }, mem) == null) return "t221 inside: no restart at 2.99";
                if (RearmTag(null, tp) != "none") return "t221 tag none";
                if (RearmTag(mem, new EvTorp { TrackId = 1, DistNm = 6 }) != "new") return "t221 tag new";
                if (RearmTag(mem, new EvTorp { TrackId = 7031, DistNm = 2.9 }) != "inside") return "t221 tag inside";
                if (RearmTag(mem, new EvTorp { TrackId = 7031, DistNm = 6.3 }) != "closer") return "t221 tag closer";
            }
            // --- T-193 A3 ShouldStart
            {
                if (!ShouldStart(true, new EvTorp { DistNm = 9, ClosingKn = 6 })) return "t193 start: game threat";
                if (!ShouldStart(false, new EvTorp { DistNm = 4, ClosingKn = 12 })) return "t193 start: 4 nm closing 12";
                if (ShouldStart(false, new EvTorp { DistNm = 4, ClosingKn = -5 })) return "t193 start: receding";
                if (ShouldStart(false, new EvTorp { DistNm = 6, ClosingKn = 30 })) return "t193 start: 6 nm";
                if (!ShouldStart(false, new EvTorp { DistNm = 1.5 })) return "t193 start: 1.5 nm unknown closing";
                if (ShouldStart(true, new EvTorp { DistNm = 1.5, Stale = true })) return "t193 start: frozen";
            }
            // --- T-193 A4 PickPrimary / CourseAwayMulti
            {
                var sp = new EvState { PrimaryId = 2 };
                var l = new List<EvTorp> { new EvTorp { TrackId = 1, DistNm = 1, ClosingKn = 60 }, new EvTorp { TrackId = 2, DistNm = 1.17, ClosingKn = 60 } };   // 60 s, 70 s
                bool ch;
                var pk = PickPrimary(l, sp, out ch);
                if (pk.TrackId != 2 || ch) return "t193 primary: hysteresis keeps";
                l[1].DistNm = 1.5;   // 90 s
                pk = PickPrimary(l, sp, out ch);
                if (pk.TrackId != 1 || !ch || sp.PrimaryId != 1) return "t193 primary: switch";
                l.RemoveAt(0);
                pk = PickPrimary(l, sp, out ch);
                if (pk.TrackId != 2 || !ch) return "t193 primary: vanished";
                var unk = new List<EvTorp> { new EvTorp { TrackId = 3, DistNm = 2 }, new EvTorp { TrackId = 4, DistNm = 1 } };
                if (PickPrimary(unk, new EvState(), out ch).TrackId != 4) return "t193 primary: by distance";
                unk[1].Stale = true;
                if (PickPrimary(unk, new EvState(), out ch).TrackId != 3) return "t193 primary: frozen ignored";
                // 02:14:28
                var a = new EvTorp { TrackId = 7049, BrgDeg = 106, DistNm = 0.35, ClosingKn = 20 };
                var b = new EvTorp { TrackId = 7051, BrgDeg = 255, DistNm = 0.36, ClosingKn = 20 };
                var sm = new EvState { PrimaryId = 7049 };
                double cm = CourseAwayMulti(47, a, null, new List<EvTorp> { a, b }, sm);
                if (Math.Abs(Diff(cm, b.BrgDeg)) <= OtherConeDeg) return "t193 multi: second torpedo in the bow cone " + cm;
                if (Math.Abs(Diff(cm, a.BrgDeg)) < 90 - 1e-6) return "t193 multi: primary off the bow " + cm;
                var s1 = new EvState(); var s2 = new EvState();
                var tp = new EvTorp { TrackId = 1, BrgDeg = 30, DistNm = 3 };
                if (Math.Abs(Diff(CourseAwayMulti(0, tp, null, new List<EvTorp> { tp }, s1), CourseAwaySticky(0, tp, null, s2))) > 1e-9) return "t193 multi: one torpedo = sticky";
                var sc = new EvState { PrimaryId = 1, Side = 1, CourseDeg = 180 };
                CourseAwayMulti(0, new EvTorp { TrackId = 2, BrgDeg = 30, DistNm = 3 }, null, null, sc);
                if (sc.PrimaryId != 2 || sc.CourseDeg == null) return "t193 multi: primary change keeps the course";
            }
            // --- T-193 A6 wake homing
            {
                var tw = new EvTorp { TrackId = 1, BrgDeg = 90, DistNm = 0.4, WakeHoming = true };
                var sw2 = new EvState();
                double cw = CourseAwaySticky(0, tw, null, sw2);
                if (Math.Abs(Math.Abs(Diff(cw, 90)) - 150) > 0.5) return "t193 wake: stern offset kept " + cw;
                tw = new EvTorp { TrackId = 1, BrgDeg = 160, DistNm = 0.4, WakeHoming = true };
                if (Math.Abs(Diff(CourseAwaySticky(0, tw, null, new EvState()), 0)) > 0.5) return "t193 wake: astern keeps course";
                if (DecoyNow(new EvState(), 0, tw)) return "t193 wake: no decoy";
                tw.WakeHoming = false;
                if (!DecoyNow(new EvState(), 0, tw)) return "t193 plain: decoy";
                var tb = new EvTorp { TrackId = 1, BrgDeg = 90, DistNm = 0.4 };
                if (Math.Abs(Math.Abs(Diff(CourseAwaySticky(0, tb, null, new EvState()), 90)) - 90) > 0.5) return "t193 plain: beam";
            }
            // --- T-224 (ADR-0091) end rule
            {
                Func<double, double?, EvTorp> mk = (d, c) => new EvTorp { TrackId = 1, DistNm = d, ClosingKn = c };
                var e1 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (Done(e1, 301, mk(2.0, 10), true, 2.0, double.PositiveInfinity) || Done(e1, HardMaxS - 1, mk(2.0, 10), true, 2.0, double.PositiveInfinity)) return "t224 end: 2 nm closing must hold to HardMaxS";
                if (!Done(e1, HardMaxS, mk(2.0, 10), true, 2.0, double.PositiveInfinity)) return "t224 end: HardMaxS";
                var e2 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (Done(e2, 301, mk(2.0, null), true, 2.0, double.PositiveInfinity)) return "t224 end: 2 nm unknown closing must hold";
                var e3 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 100 };
                bool ended = false;
                for (int q = 100; q <= 299; q++) ended |= Done(e3, q, mk(1.5, -5), true, 1.5, double.PositiveInfinity);
                if (ended) return "t224 end: receding inside HoldNm must not end before MaxS";
                if (Done(e3, 301, mk(1.5, -5), true, 1.5, double.PositiveInfinity) || !Done(e3, HardMaxS, mk(1.5, -5), true, 1.5, double.PositiveInfinity)) return "t268 end: receding inside NearHoldNm holds to HardMaxS (was: ends at MaxS)";   // T-268
                var e4 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 301 };
                for (int q = 270; q <= 300; q++) Done(e4, q, mk(2.0, -5), true, 1.0, 1.0);
                if (Done(e4, 301, mk(2.0, -5), true, 1.0, 1.0)) return "t224 end: a recent frozen track at 1 nm holds";
                var e5 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 301 };
                for (int q = 270; q <= 300; q++) Done(e5, q, mk(2.5, -5), true, 2.5, double.PositiveInfinity);   // T-268: 2.5 nm
                if (!Done(e5, 301, mk(2.5, -5), true, 2.5, double.PositiveInfinity)) return "t224 end: no frozen track, receding 2 nm ends at MaxS";
                // five-argument form: a receding fresh track inside HoldNm is not a frozen one
                var e6 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 301 };
                for (int q = 270; q <= 300; q++) Done(e6, q, mk(2.5, -5), true, 2.5);   // T-268: 2.5 nm
                if (!Done(e6, 301, mk(2.5, -5), true, 2.5)) return "t224 end: 5-arg form must not treat the fresh track as frozen";
            }
            // --- T-224 start by the game flag
            {
                if (ShouldStart(true, new EvTorp { DistNm = 9 })) return "t224 start: 9 nm, closing unknown";
                if (!ShouldStart(true, new EvTorp { DistNm = 9, ClosingKn = 6 })) return "t224 start: 9 nm closing 6";
                if (ShouldStart(true, new EvTorp { DistNm = 9, ClosingKn = 4 })) return "t224 start: 9 nm closing 4";
                if (!ShouldStart(true, new EvTorp { DistNm = 4, ClosingKn = -5 })) return "t224 start: 4 nm flag";
                if (ShouldStart(true, new EvTorp { DistNm = 1.5, Stale = true })) return "t224 start: frozen";
                if (GameFlagStarts(null)) return "t224 start: null";
            }
            // --- T-224 slow run for the sonar
            {
                var sl = new EvState { LastSeenS = 0 };
                if (SonarSlowNow(sl, 19, 3.6, false)) return "t224 slow: 19 s";
                if (!SonarSlowNow(sl, 20, 3.6, false)) return "t224 slow: 20 s";   // T-273: 3.6 nm (start threshold 3.5 nm)
                if (SonarSlowNow(sl, 20, 0.9, false)) return "t224 slow: 0.9 nm";
                if (SonarSlowNow(sl, 20, 3.6, true)) return "t224 slow: wake homing";
                double[] a = { -5, 0, 6, 10, 15, 20, 25 };
                if (SonarSlowTelegraph(a, double.NaN) != 2) return "t224 tg: 10 kn";
                if (SonarSlowTelegraph(a, 8) != 1) return "t224 tg: cavitation 8 kn";
                if (SonarSlowTelegraph(new double[] { -5, 0, 3, 5, 8, 12, 30 }, double.NaN) != 4) return "t224 tg: 12 kn";
                if (SonarSlowTelegraph(null, double.NaN) != SonarSlowDefaultTelegraph) return "t224 tg: null";
                if (SonarSlowTelegraph(new double[] { 0, 1, 2 }, double.NaN) != SonarSlowDefaultTelegraph) return "t224 tg: short";
                if (SonarSlowTelegraph(new double[] { -5, 0, 14, 16, 18, 20, 25 }, double.NaN) != 1) return "t224 tg: all too fast";
            }
            // --- T-268 pursuit (ADR-0107)
            {
                const double kn2nm = 1.0 / 3600.0;
                double ownLat = 0, ownLon = 0;
                var tp = new TrackPursuit();
                // torpedo 25 kn straight at the ship from 4 nm north, 10 plots 2 s apart
                for (int i = 0; i < 10; i++) tp.Update(1, 7, (4.0 - 25 * kn2nm * 2 * i) / 60.0, 0, true, false, ownLat, ownLon, 2.0 * i);
                double? sp = tp.SpeedKn(1, 7);
                if (!sp.HasValue || Math.Abs(sp.Value - 25) > 1) return "t268 pursuit: speed " + sp;
                // frozen, the ship stands: 60 s later the point is 25 kn * 60 s closer
                double d0 = tp.PursuitNm(1, 7, ownLat, ownLon);
                tp.Update(1, 7, (4.0 - 25 * kn2nm * 18) / 60.0, 0, false, false, ownLat, ownLon, 78);
                double d1 = tp.PursuitNm(1, 7, ownLat, ownLon);
                if (Math.Abs((d0 - d1) - 25 * 60 * kn2nm) > 0.1) return "t268 pursuit: closed " + (d0 - d1);
                // never passes the ship
                tp.Update(1, 7, 0, 0, false, false, ownLat, ownLon, 5000);
                if (tp.PursuitNm(1, 7, ownLat, ownLon) < 0 || tp.PursuitNm(1, 7, ownLat, ownLon) > 1e-6) return "t268 pursuit: overshoot " + tp.PursuitNm(1, 7, ownLat, ownLon);
                // jumps of the plot (wake homing): clamped to MinKn..MaxKn
                var tj = new TrackPursuit();
                for (int i = 0; i < 10; i++) tj.Update(1, 8, (4.0 - 2.0 * i) / 60.0, 0, true, true, ownLat, ownLon, 2.0 * i);
                if (tj.SpeedKn(1, 8).Value > TrackPursuit.MaxKn + 1e-9) return "t268 pursuit: wake speed not clamped " + tj.SpeedKn(1, 8);
                // frozen from the first tick: defaults
                var td = new TrackPursuit();
                td.Update(1, 1, 0.05, 0, false, false, ownLat, ownLon, 10);
                td.Update(1, 2, 0.05, 0, false, true, ownLat, ownLon, 10);
                if (td.SpeedKn(1, 1) != TrackPursuit.DefaultKn || td.SpeedKn(1, 2) != TrackPursuit.DefaultWakeKn) return "t268 pursuit: default speeds";
                if (td.SpeedKn(9, 9).HasValue || !double.IsNaN(td.PursuitNm(9, 9, 0, 0))) return "t268 pursuit: unknown pair";
                td.Prune(new List<int> { 2 });
                if (td.Count != 1 || td.SpeedKn(1, 1).HasValue) return "t268 pursuit: prune";
                td.Clear();
                if (td.Count != 0) return "t268 pursuit: clear";
                // ThreatDist
                if (ThreatDist(new EvTorp { DistNm = 3, ThreatNm = 0.5 }) != 3) return "t268 threat: fresh uses DistNm";
                if (ThreatDist(new EvTorp { DistNm = 3, Stale = true }) != 3) return "t268 threat: NaN uses DistNm";
                if (ThreatDist(new EvTorp { DistNm = 3, Stale = true, ThreatNm = 0.5 }) != 0.5) return "t268 threat: min (pursuit)";
                if (ThreatDist(new EvTorp { DistNm = 0.3, Stale = true, ThreatNm = 2 }) != 0.3) return "t268 threat: min (mark)";
                if (!double.IsPositiveInfinity(ThreatDist(null))) return "t268 threat: null";
            }
            // --- T-268 pursuit expiry (R1): the frozen track must not hold the evasion for ever
            {
                const double kn2nm = 1.0 / 3600.0;
                // the ship stands, the point reaches it, +ExpireAfterInterceptS later the track is expired (not before)
                var te = new TrackPursuit();
                for (int i = 0; i < 6; i++) te.Update(1, 5, (2.0 - 25 * kn2nm * 2 * i) / 60.0, 0, true, false, 0, 0, 2.0 * i);
                te.Update(1, 5, (2.0 - 25 * kn2nm * 10) / 60.0, 0, false, false, 0, 0, 40);
                if (te.Expired(1, 5, 40)) return "t268 expiry: not yet";
                te.Update(1, 5, 0.01, 0, false, false, 0, 0, 40 + 400);   // far past the intercept
                if (te.PursuitNm(1, 5, 0, 0) > 0.02) return "t268 expiry: the point must reach the ship";
                double icpt = 40 + 400;   // the update that reached the ship
                if (te.Expired(1, 5, icpt + TrackPursuit.ExpireAfterInterceptS - 1)) return "t268 expiry: margin";
                if (!te.Expired(1, 5, icpt + TrackPursuit.ExpireAfterInterceptS)) return "t268 expiry: intercept passed";
                // a fresh moving plot revives it
                te.Update(1, 5, 0.02, 0, true, false, 0, 0, icpt + 100);
                te.Update(1, 5, 0.025, 0, true, false, 0, 0, icpt + 102);
                if (te.Expired(1, 5, icpt + 102)) return "t268 expiry: fresh plot revives";
                // the ship outruns the point (never intercepted): the run endurance ends it
                var tr = new TrackPursuit();
                for (int i = 0; i < 10; i++) tr.Update(1, 6, (4.0 + 45 * kn2nm * 2 * i) / 60.0, 0, true, true, 0, 0, 2.0 * i);   // wake torpedo, 45 kn (clamped)
                double endur = TrackPursuit.MaxRunNm / 45.0 * 3600.0;
                tr.Update(1, 6, 4.0 / 60.0, 0, false, true, 0, 0, 30);
                if (tr.Expired(1, 6, endur - 1)) return "t268 expiry: endurance not yet";
                if (!tr.Expired(1, 6, endur + TrackPursuit.ExpireAfterInterceptS + 20)) return "t268 expiry: endurance exceeded";
                if (ThreatDist(new EvTorp { DistNm = 0.3, Stale = true, ThreatNm = 0.3, PursuitExpired = true }) < 1e9) return "t268 expiry: ThreatDist of an expired track";
                if (ThreatDist(new EvTorp { DistNm = 0.3, PursuitExpired = true }) != 0.3) return "t268 expiry: fresh ignores the flag";
            }
            // --- T-268 end rule and slow run (ADR-0107)
            {
                if (NearHoldNm != 2.0 || SonarSlowMinNm != 3.0 || HardMaxS != 1800) return "t268 constants";   // T-273: SonarSlowMinNm 3.0
                double inf = double.PositiveInfinity;
                foreach (double fz in new[] { 0.04, 0.3, 0.47, 0.56, 0.84, 1.12, 2.0 })
                {
                    var s0 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = fz };
                    if (Done(s0, 301, null, false, fz, fz)) return "t268 end: frozen threat " + fz + " must hold at 301";
                    if (Done(s0, HardMaxS - 1, null, false, fz, fz)) return "t268 end: frozen threat " + fz + " must hold to HardMaxS";
                    if (!Done(s0, HardMaxS, null, false, fz, fz)) return "t268 end: HardMaxS with " + fz;
                }
                var sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = 3.6 };
                if (!Done(sf, 301, null, false, 3.6, 3.6)) return "t268 end: far frozen threat ends at MaxS";   // T-273: 3.6 nm
                var sn = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = 3 };
                if (!Done(sn, 301, null, false, inf, inf)) return "t268 end: no tracks ends";
                var sg = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 9 };
                if (!Done(sg, 40, null, false, inf, inf)) return "t268 end: gone 30 s at 9 nm";
                var sr = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                var rc = new EvTorp { TrackId = 1, DistNm = 1.5, ClosingKn = -5 };
                for (int q = 270; q <= 301; q++) if (Done(sr, q, rc, true, 1.5, inf)) return "t268 end: receding fresh inside 2 nm holds (Q1) at " + q;
                if (!Done(sr, HardMaxS, rc, true, 1.5, inf)) return "t268 end: receding fresh ends at HardMaxS";
                var sl = new EvState { LastSeenS = 0 };
                if (SonarSlowNow(sl, 20, 3.5, false)) return "t268 slow: 3.5 nm";   // T-273: start threshold 3.5
                if (!SonarSlowNow(sl, 20, 3.51, false)) return "t268 slow: 3.51 nm";
                if (SonarSlowNow(sl, 20, 3, true)) return "t268 slow: wake";
            }
            // --- T-273 hold/slow hysteresis (ADR-0109)
            {
                if (HoldReleaseNm != 3.5 || SonarSlowStartNm != 3.5 || SonarSlowMinNm != 3.0 || NearHoldNm != 2.0 || HoldNm != 3) return "t273 constants";
                double inf = double.PositiveInfinity;
                // the run-15 cycle: the end at 2.00 / 2.11 / 2.02 / 2.51 nm after 300 s must not happen
                foreach (double fz in new[] { 2.00, 2.11, 2.02, 2.51, 3.49 })
                {
                    var s0 = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = fz };
                    if (Done(s0, 301, null, false, fz, fz)) return "t273 end: frozen threat " + fz + " must hold at 301";
                    if (Done(s0, 1000, null, false, fz, fz)) return "t273 end: frozen threat " + fz + " must hold at 1000";
                    if (!Done(s0, HardMaxS, null, false, fz, fz)) return "t273 end: HardMaxS with " + fz;
                }
                var sa = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = 3.51 };
                if (!Done(sa, 301, null, false, 3.51, 3.51)) return "t273 end: 3.51 nm frozen ends at MaxS";
                if (!Done(sa, 301, null, false, inf, inf)) return "t273 end: expired pursuit ends at MaxS";
                var sc = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 299 };
                if (Done(sc, 301, new EvTorp { TrackId = 1, DistNm = 3.2, ClosingKn = 10 }, true, 3.2, inf)) return "t273 end: fresh closing 3.2 nm holds";
                var sr = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0 };
                bool endedR = false;
                for (int q = 270; q <= 300; q++) endedR |= Done(sr, q, new EvTorp { TrackId = 1, DistNm = 3.2, ClosingKn = -5 }, true, 3.2, inf);
                if (!endedR) return "t273 end: fresh receding 3.2 nm ends";
                // slow hysteresis
                var sl = new EvState { LastSeenS = 0 };
                if (SonarSlowNow(sl, 20, 3.4, false, false)) return "t273 slow: start 3.4";
                if (!SonarSlowNow(sl, 20, 3.51, false, false)) return "t273 slow: start 3.51";
                if (!SonarSlowNow(sl, 20, 3.2, false, true)) return "t273 slow: keep 3.2";
                if (SonarSlowNow(sl, 20, 3.0, false, true)) return "t273 slow: stop 3.0";
                if (SonarSlowNow(sl, 20, 4, true, true)) return "t273 slow: wake";
                if (!SonarSlowNow(sl, 20, 3.51, false) || SonarSlowNow(sl, 20, 3.4, false)) return "t273 slow: 4-arg form";
            }
            // --- T-284: no slow run while the lost-near hold lasts (ADR-0113)
            {
                var sn = new EvState { LastSeenS = 0, NearSeenS = 100 };
                if (SonarSlowNow(sn, 150, 9.8, false, false)) return "t284 slow: start inside lost_near hold";
                if (SonarSlowNow(sn, 219, 9.8, false, true)) return "t284 slow: keep inside lost_near hold";
                if (!SonarSlowNow(sn, 220, 9.8, false, false)) return "t284 slow: allowed after the hold (120 s)";
                var sz = new EvState { LastSeenS = 0 };
                if (!SonarSlowNow(sz, 150, 9.8, false, false)) return "t284 slow: no memory, far track slows";
            }
            // --- T-280 lost-near hold (ADR-0113)
            {
                if (LostNearNm != 2.0 || LostNearHoldS != 120) return "t280 constants";
                double inf = double.PositiveInfinity;
                if (!double.IsNaN(new EvState().NearSeenS)) return "t280 default NearSeenS";
                // the Udaloy case (N2): frozen 7032 (0.30) and 7035 (1.61), then the plot is empty
                var sn = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = 4.44 };
                NoteThreat(sn, 302, new List<EvTorp>
                {
                    new EvTorp { TrackId = 7032, DistNm = 4.44, Stale = true, ThreatNm = 0.28 },
                    new EvTorp { TrackId = 7035, DistNm = 1.61, Stale = true }
                });
                if (sn.NearSeenS != 302 || sn.NearTrackId != 7032) return "t280 note: " + sn.NearSeenS + " id " + sn.NearTrackId;
                NoteThreat(sn, 400, new List<EvTorp>());
                if (sn.NearSeenS != 302) return "t280 note: an empty list must keep the memory";
                if (Done(sn, 413, null, false, inf, inf) || Done(sn, 421, null, false, inf, inf)) return "t280 done: must hold 120 s after the last near threat";
                if (!Done(sn, 422, null, false, inf, inf)) return "t280 done: must end at 422";
                if (DoneWhy(sn, 422, null, false) != "maxs") return "t280 why: " + DoneWhy(sn, 422, null, false);
                if (!LostNearHolds(sn, 421) || LostNearHolds(sn, 422)) return "t280 holds: boundary";
                // a threat beyond the limit leaves no memory
                var sf = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, LastDistNm = 2.5 };
                NoteThreat(sf, 280, new List<EvTorp> { new EvTorp { TrackId = 1, DistNm = 2.5, Stale = true } });
                if (!double.IsNaN(sf.NearSeenS)) return "t280 far: no memory beyond LostNearNm";
                if (!Done(sf, 301, null, false, inf, inf)) return "t280 far: ends at MaxS";
                // boundary 1.9 / 2.1
                var sb = new EvState();
                NoteThreat(sb, 10, new List<EvTorp> { new EvTorp { TrackId = 1, DistNm = 1.9 } });
                if (sb.NearSeenS != 10) return "t280 edge: 1.9";
                sb = new EvState();
                NoteThreat(sb, 10, new List<EvTorp> { new EvTorp { TrackId = 1, DistNm = 2.1 } });
                if (!double.IsNaN(sb.NearSeenS)) return "t280 edge: 2.1";
                // an expired pursuit of the remembered track clears the memory
                var se = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24 };
                NoteThreat(se, 100, new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 0.5, Stale = true, ThreatNm = 0.5 } });
                NoteThreat(se, 150, new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 0.5, Stale = true, ThreatNm = 0.5, PursuitExpired = true } });
                if (!double.IsNaN(se.NearSeenS)) return "t280 expired: memory must be cleared";
                if (!Done(se, 301, null, false, inf, inf)) return "t280 expired: ends at MaxS";
                // another track expired, the remembered one left the list: memory stays
                var so = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24 };
                NoteThreat(so, 250, new List<EvTorp> { new EvTorp { TrackId = 5, DistNm = 0.5, Stale = true } });
                NoteThreat(so, 260, new List<EvTorp> { new EvTorp { TrackId = 6, DistNm = 5, Stale = true, PursuitExpired = true } });
                if (so.NearSeenS != 250) return "t280 other: memory must stay";
                // HardMaxS wins
                var sh = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 24, NearSeenS = HardMaxS - 10 };
                if (!Done(sh, HardMaxS, null, false, inf, inf)) return "t280 hardmax";
                // the gone rule must not end earlier
                var sg = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 10, LastDistNm = 9, NearSeenS = 300 };
                if (Done(sg, 350, null, false, inf, inf)) return "t280 gone: must hold";
                // a receding fresh track beyond HoldNm does not beat the memory
                var sr = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0, NearSeenS = 290 };
                for (int q = 260; q <= 300; q++) Done(sr, q, new EvTorp { TrackId = 1, DistNm = 4, ClosingKn = -5 }, true, 4, inf);
                if (Done(sr, 300, new EvTorp { TrackId = 1, DistNm = 4, ClosingKn = -5 }, true, 4, inf)) return "t280 receding: memory must hold";
                // renewed near threat refreshes
                NoteThreat(sn, 430, new List<EvTorp> { new EvTorp { TrackId = 9, DistNm = 0.8 } });
                if (sn.NearSeenS != 430 || sn.NearTrackId != 9 || sn.NearNm != 0.8) return "t280 refresh";
            }
            return null;
        }
    }
}
