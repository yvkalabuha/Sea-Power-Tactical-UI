using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075: pure core of the air threat service (radius, ETA, stages, cover point, raids). No Unity, no reflection.
    // Flat on stable, x LonK on the beta globe (T-348, see AswMath).

    internal enum AirStage { None = 0, Warn = 1, Retreat = 2 }

    internal sealed class AirGuardParams
    {
        public string Mode = "retreat";          // retreat | warn | off
        public double WarnNm = 60, WarnNmHelo = 80, RetreatNm = 40, RetreatNmHelo = 50;
        public double RetreatEtaS = 360, RetreatEtaSHelo = 480;
        public string Unknown = "warn";          // off | warn | retreat
        public string TimeX1 = "all";            // all | stage2 | off
        public bool WarnUngrouped = true, Toast = true;
        public double ToastS = 20, DefaultRadiusNm = 10, MinRadiusNm = 3, UnknownMinKn = 350, GhostS = 180, ClearS = 60,
                      MinSamNm = 10, UnloadExtraS = 120, IgnoreS = 600, TcMinGapS = 30;
        public double ClearMarginNm = 10, ClearEtaMarginS = 120, RaidNm = 10, RaidCourseDeg = 30,
                      CoverRecalcS = 30, CoverBearingDeg = 30, PathMarginNm = 5;   // ADR constants (not config keys)
        public double CoverKeepS = 60, CoverKeepFrac = 0.25,   // T-192: the cover ship changes only if the new one is faster by max(CoverKeepS, CoverKeepFrac * t)
                      CoverMoveNm = 2,                         // the point of the same ship closer than this is the same command
                      CoverTrackKeepNm = 5;                    // the cover track is kept until a new "worst" one is closer by this
        public double HeloRecedeKn = 0;          // T-180: a track whose own radial speed to the point is <= this does not keep a helicopter in the retreat
        public double HeloResumeHoldS = 60,      // T-207: after a soft resume the released tracks do not hold the helicopter again (except inside R_t + PathMarginNm)
                      HeloReholdKn = 50,         // T-207: a released track holds again only when its own radial speed to the helicopter is above this ...
                      HeloReholdS = 20;          // T-207: ... for this long without a break
        internal AirGuardParams Clone() { return (AirGuardParams)MemberwiseClone(); }
    }

    /// <summary>A track of the player (the plotting table only).</summary>
    internal sealed class AirTrack
    {
        public int Id; public string Cls;    // Cls = class when identified, else null
        public double Lat, Lon, CourseDeg, SpeedKn, LastSeenS;
        public bool Hostile, Unknown, Ghost, Destroyed, Helicopter;
        public bool Globe;                   // T-347: world metric at the track (AswMath.GlobeFromMetric); false = flat (stable)
        public double RNm; public string RSrc;   // radius + "class" | "default" | "no_aaw"
    }

    /// <summary>An own aircraft in the air.</summary>
    internal sealed class AirOwn
    {
        public int Uid; public string Name, TypeLabel, Window, Group;   // Window/Group = null outside the mod's groups
        public bool Helicopter; public double Lat, Lon, CourseDeg, SpeedKn, AltM;
        public int MissileId;                // ThreateningMissile (AAM) on the bird, 0 = none
    }

    internal sealed class AirSam { public int Uid; public string Name; public double Lat, Lon, RSamNm; public bool RadarOff, Deck, HeloDeck; }

    internal sealed class AirUnitThreat
    {
        public int Uid, TrackId; public AirStage Stage; public double Nm, EtaS, RNm; public string RSrc, Raid, Why;   // Why: dist | eta | missile | unknown
        public bool Receding;   // T-207: the last drop of the stage was a soft one (the targets stopped approaching)
    }

    internal sealed class AirCover
    {
        public string Mode;                  // cover | land | away
        public int ShipUid; public string ShipName; public GeoPt Point;
        public string Note;                  // null | cover_path_risk | cover_radar_off | no_cover
    }

    internal sealed class AirRaid { public string Key; public List<int> TrackIds = new List<int>(); public string Cls; public int Count; public double CourseDeg, SpeedKn, CenterLat, CenterLon; }

    /// <summary>Data of the "Air threat" card (drawn by AirThreatToastBody).</summary>
    internal sealed class AirThreatCard
    {
        public string RaidKey, Cls, Side; public int Count; public double CourseDeg, SpeedKn, CenterLat, CenterLon;
        public List<AirThreatCardLine> Groups = new List<AirThreatCardLine>();
        public string TcLine;                // "x14 -> x1: <reason>" or null
        public bool Resumed;                 // the threat is over, the tasks go on (re-show of the card)
    }
    internal sealed class AirThreatCardLine
    {
        public string Window;   // phase D: the window the group belongs to (asw | cap | strike); null = by the action
        public string Group; public double Nm, EtaS; public string Action; /* warn|cover|land|no_cover|scramble|not_ready|preparing (T-179: the alert group of the CAP) */ public string ShipName; public int Aboard;
        public int Count; public string TypeLabel;   // T-179: scramble - how many and which type
    }

    /// <summary>Service in UiContext (ctx.Provide&lt;IAirThreats&gt;) and in the controllers.</summary>
    internal interface IAirThreats
    {
        AirGuardParams P { get; }
        int Revision { get; }                                  // grows on every stage / track change
        IList<AirTrack> Tracks();                              // current (ghosts included), no destroyed
        AirUnitThreat ForUnit(int uid);                        // null = no threat
        AirUnitThreat AtPoint(GeoPt p, bool helo);             // stage of a point of an area / deck
        AirStage GroupStage(string window, string group);      // with Ignore and Mode; Mode=warn -> at most Warn
        AirCover CoverFor(int uid);                            // cache: recalculated every 30 s / bearing change > 30 deg; null outside stage 2
        bool Ignored(string window, string group);
        void Ignore(string window, string group, double now);  // "Continue tasks"
    }

    /// <summary>The service (and AirLossTracker) offers the reasons for x1 here; implemented by AirTimeX1.</summary>
    internal interface IAirTimeX1 { void Offer(string kind, string key, string reason); }   // kind: raid1 | stage2 | missile | loss | alert_scramble (T-179)

    internal static class AirGuardMath
    {
        private const double Deg = Math.PI / 180.0;

        // ---- radius, closing, ETA, stage ----

        /// <summary>R_t: the largest AAW MaxLaunchRange over the loadouts of an identified class (null list = no data -> default; empty = no AAW at all -> no_aaw).</summary>
        public static double ThreatRadiusNm(bool identified, IList<double> aawMaxNmByLoadout, AirGuardParams p, out string src)
        {
            if (!identified || aawMaxNmByLoadout == null) { src = "default"; return Math.Max(p.MinRadiusNm, p.DefaultRadiusNm); }
            double best = 0;
            foreach (double d in aawMaxNmByLoadout) if (d > best) best = d;
            if (best <= 0) { src = "no_aaw"; return p.MinRadiusNm; }
            src = "class";
            return Math.Max(p.MinRadiusNm, best);
        }

        private static void Vel(double courseDeg, double kn, out double north, out double east)
        {
            north = kn * Math.Cos(courseDeg * Deg); east = kn * Math.Sin(courseDeg * Deg);
        }

        /// <summary>Closing speed of the track on the bird, kn (&gt; 0 = approaching).</summary>
        public static double ClosingKn(AirTrack t, AirOwn o)
        {
            double tn, te, on, oe;
            Vel(t.CourseDeg, t.SpeedKn, out tn, out te); Vel(o.CourseDeg, o.SpeedKn, out on, out oe);
            return AswMath.RadialClosingKn(new GeoPt(t.Lat, t.Lon), new GeoPt(o.Lat, o.Lon), tn - on, te - oe, t.Globe, Math.Max(t.SpeedKn, 1));
        }

        /// <summary>T-180: radial speed of the track itself towards the point, kn (&gt; 0 = approaching). The own speed is ignored: a helicopter flying back to its task does not make the target "approaching".</summary>
        public static double TrackClosingKn(AirTrack t, double lat, double lon)
        {
            double tn, te;
            Vel(t.CourseDeg, t.SpeedKn, out tn, out te);
            return AswMath.RadialClosingKn(new GeoPt(t.Lat, t.Lon), new GeoPt(lat, lon), tn, te, t.Globe, Math.Max(t.SpeedKn, 1));
        }

        /// <summary>T-180: true = the track keeps the helicopter in the retreat: it approaches, or the helicopter is inside R_t + PathMarginNm.</summary>
        public static bool HeloHolds(AirTrack t, double dNm, double trackClosingKn, AirGuardParams p)
        {
            return trackClosingKn > p.HeloRecedeKn || dNm <= t.RNm + p.PathMarginNm;
        }

        /// <summary>T-207: true = a released track holds the helicopter again. Inside R_t + PathMarginNm at once; otherwise not before HeloResumeHoldS after the soft resume
        /// and only after HeloReholdS of approach faster than HeloReholdKn. closeSince: -1 = not approaching.</summary>
        public static bool HeloReholds(double dNm, double rNm, double trackClosingKn, double now, double softAt, ref double closeSince, AirGuardParams p)
        {
            if (dNm <= rNm + p.PathMarginNm) return true;
            if (now - softAt < p.HeloResumeHoldS || trackClosingKn <= p.HeloReholdKn) { closeSince = -1; return false; }
            if (closeSince < 0) closeSince = now;
            return now - closeSince >= p.HeloReholdS;
        }

        public static double EtaS(double dNm, double rNm, double closingKn)
        {
            if (closingKn <= 0) return double.PositiveInfinity;
            if (dNm <= rNm) return 0;
            return (dNm - rNm) / closingKn * 3600.0;
        }

        public static AirStage RawStage(double dNm, double etaS, bool helo, AirGuardParams p, out string why)
        {
            double rn = helo ? p.RetreatNmHelo : p.RetreatNm, re = helo ? p.RetreatEtaSHelo : p.RetreatEtaS, wn = helo ? p.WarnNmHelo : p.WarnNm;
            why = null;
            if (dNm <= rn) { why = "dist"; return AirStage.Retreat; }
            if (etaS <= re) { why = "eta"; return AirStage.Retreat; }
            if (dNm <= wn) { why = "dist"; return AirStage.Warn; }
            return AirStage.None;
        }

        /// <summary>Hysteresis: a lower stage is taken only after clearOk (all threats beyond threshold + margin) lasted ClearS.</summary>
        public static AirStage Hysteresis(AirStage prev, AirStage raw, bool clearOk, ref double clearSince, double now, AirGuardParams p)
        {
            if (raw >= prev) { clearSince = -1; return raw; }
            if (!clearOk) { clearSince = -1; return prev; }
            if (clearSince < 0) clearSince = now;
            return now - clearSince >= p.ClearS ? raw : prev;
        }

        // ---- threats, raids ----

        public static bool ThreatEligible(AirTrack t, AirGuardParams p, out bool warnOnly)
        {
            warnOnly = false;
            if (t == null || t.Destroyed) return false;
            if (t.Hostile) return true;
            if (!t.Unknown || p.Unknown == "off") return false;
            if (t.SpeedKn < p.UnknownMinKn) return false;
            warnOnly = p.Unknown != "retreat";
            return true;
        }

        public static List<AirRaid> Raids(IList<AirTrack> tracks, AirGuardParams p)
        {
            int n = tracks == null ? 0 : tracks.Count;
            var par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;
            Func<int, int> find = null;
            find = x => par[x] == x ? x : (par[x] = find(par[x]));
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = tracks[i]; var b = tracks[j];
                    double dn = (a.Lat - b.Lat) * 60, de = (a.Lon - b.Lon) * 60 * AswMath.LonK(a.Lat, b.Lat);
                    if (Math.Sqrt(dn * dn + de * de) <= p.RaidNm && AswMath.AngleDiff(a.CourseDeg, b.CourseDeg) <= p.RaidCourseDeg) par[find(i)] = find(j);
                }
            var map = new Dictionary<int, AirRaid>();
            var res = new List<AirRaid>();
            var sn = new Dictionary<int, double[]>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                AirRaid rd;
                if (!map.TryGetValue(r, out rd)) { rd = new AirRaid(); map[r] = rd; res.Add(rd); sn[r] = new double[5]; }
                var t = tracks[i];
                rd.TrackIds.Add(t.Id); rd.Count++;
                if (rd.Cls == null && t.Cls != null) rd.Cls = t.Cls;
                var s = sn[r];
                double vn, ve; Vel(t.CourseDeg, 1, out vn, out ve);
                s[0] += vn; s[1] += ve; s[2] += t.SpeedKn; s[3] += t.Lat; s[4] += t.Lon;
            }
            foreach (var kv in map)
            {
                var rd = kv.Value; var s = sn[kv.Key];
                rd.TrackIds.Sort();
                rd.Key = "r" + rd.TrackIds[0];
                rd.CourseDeg = AswMath.Norm360(Math.Atan2(s[1], s[0]) / Deg);
                rd.SpeedKn = s[2] / rd.Count; rd.CenterLat = s[3] / rd.Count; rd.CenterLon = s[4] / rd.Count;
            }
            res.Sort((a, b) => a.TrackIds[0].CompareTo(b.TrackIds[0]));
            return res;
        }

        // ---- cover point ----

        private static double SegDistNm(GeoPt a, GeoPt b, GeoPt p)
        {
            double lk = AswMath.LonK(a.Lat, b.Lat);
            double abn = (b.Lat - a.Lat) * 60, abe = (b.Lon - a.Lon) * 60 * lk, apn = (p.Lat - a.Lat) * 60, ape = (p.Lon - a.Lon) * 60 * lk;
            double l2 = abn * abn + abe * abe;
            double k = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (apn * abn + ape * abe) / l2));
            double dn = apn - k * abn, de = ape - k * abe;
            return Math.Sqrt(dn * dn + de * de);
        }

        public static double CoverNm(double rSamNm) { return Math.Max(2, Math.Min(6, 0.25 * rSamNm)); }

        private static GeoPt AwayPoint(AirOwn o, AirTrack threat)
        {
            var op = new GeoPt(o.Lat, o.Lon);
            return AswMath.Offset(op, AswMath.BearingDeg(new GeoPt(threat.Lat, threat.Lon), op), 20);
        }

        public static AirCover Cover(AirOwn o, AirTrack threat, IList<AirSam> sams, double ownKn, AirGuardParams p, int keepShipUid = 0)
        {
            var op = new GeoPt(o.Lat, o.Lon); var tp = new GeoPt(threat.Lat, threat.Lon);
            double kn = ownKn > 1 ? ownKn : 100;
            AirCover best = null, bestSafe = null; double bestT = double.MaxValue, bestSafeT = double.MaxValue;
            if (sams != null)
                foreach (var s in sams)
                {
                    if (s.RSamNm < p.MinSamNm) continue;
                    var sp = new GeoPt(s.Lat, s.Lon);
                    double cn = CoverNm(s.RSamNm);
                    var pt = AswMath.Offset(sp, AswMath.BearingDeg(tp, sp), cn);
                    double t = AswMath.DistNm(op, pt) / kn * 3600.0;
                    // threat position when the bird gets to the point; a path closer than R_t + margin to it is dropped
                    double vn, ve; Vel(threat.CourseDeg, threat.SpeedKn, out vn, out ve);
                    var pred = new GeoPt(threat.Lat + vn * t / 3600.0 / 60.0, threat.Lon + ve * t / 3600.0 / (60.0 * AswMath.LonK(threat.Lat)));
                    bool risky = Math.Min(SegDistNm(op, pt, pred), SegDistNm(op, pt, tp)) < threat.RNm + p.PathMarginNm;
                    var c = new AirCover { Mode = "cover", ShipUid = s.Uid, ShipName = s.Name, Point = pt, Note = s.RadarOff ? "cover_radar_off" : null };
                    double tk = keepShipUid != 0 && s.Uid == keepShipUid ? t - Math.Max(p.CoverKeepS, p.CoverKeepFrac * t) : t;   // T-192: hysteresis in favour of the held ship
                    if (tk < bestT) { bestT = tk; best = c; if (risky) c.Note = "cover_path_risk"; }
                    if (!risky && tk < bestSafeT) { bestSafeT = tk; bestSafe = c; }
                }
            if (bestSafe != null) return bestSafe;
            if (best != null) { best.Note = "cover_path_risk"; return best; }
            return new AirCover { Mode = "away", Point = AwayPoint(o, threat), Note = "no_cover" };
        }

        /// <summary>SAR with people aboard (ADR-0075 2.4): a deck in a SAM ring (its own or another ship's) reachable not later than the cover point + UnloadExtraS -> land.</summary>
        public static AirCover LandOrCover(AirOwn o, AirTrack threat, IList<AirSam> sams, double ownKn, int aboard, AirGuardParams p, int keepShipUid = 0)
        {
            var cov = Cover(o, threat, sams, ownKn, p, keepShipUid);
            if (aboard <= 0 || sams == null) return cov;
            var op = new GeoPt(o.Lat, o.Lon);
            double kn = ownKn > 1 ? ownKn : 100;
            double coverT = cov.Mode == "cover" ? AswMath.DistNm(op, cov.Point) / kn * 3600.0 : double.MaxValue / 4;
            AirSam pick = null; double pickT = double.MaxValue;
            foreach (var s in sams)
            {
                if (!(o.Helicopter ? s.HeloDeck : s.Deck)) continue;
                var sp = new GeoPt(s.Lat, s.Lon);
                bool ringed = s.RSamNm >= p.MinSamNm;
                if (!ringed)
                    foreach (var q in sams)
                        if (q != s && q.RSamNm >= p.MinSamNm && AswMath.DistNm(new GeoPt(q.Lat, q.Lon), sp) <= q.RSamNm) { ringed = true; break; }
                if (!ringed) continue;
                double t = AswMath.DistNm(op, sp) / kn * 3600.0;
                if (t < pickT) { pickT = t; pick = s; }
            }
            if (pick == null || pickT > coverT + p.UnloadExtraS) return cov;
            return new AirCover { Mode = "land", ShipUid = pick.Uid, ShipName = pick.Name, Point = new GeoPt(pick.Lat, pick.Lon), Note = cov.Note == "cover_radar_off" || pick.RadarOff ? "cover_radar_off" : null };
        }

        /// <summary>T-192: true = the same retreat command (mode, ship and note equal, the point moved not more than CoverMoveNm).</summary>
        public static bool SameCover(AirCover a, AirCover b, AirGuardParams p)
        {
            if (a == null || b == null) return false;
            if (a.Mode != b.Mode || a.ShipUid != b.ShipUid || a.Note != b.Note) return false;
            return AswMath.DistNm(a.Point, b.Point) <= p.CoverMoveNm;
        }

        /// <summary>T-192: true = keep the cover track: the new "worst" track is not closer to the bird than the old one by CoverTrackKeepNm.</summary>
        public static bool KeepCoverTrack(double oldNm, double newNm, AirGuardParams p)
        {
            return newNm > oldNm - p.CoverTrackKeepNm;
        }

        // ---- selftest ----

        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static void Near(string n, double a, double b, double eps = 1e-6)
        {
            if (double.IsPositiveInfinity(a) && double.IsPositiveInfinity(b)) return;
            if (!(Math.Abs(a - b) <= eps) && _err == null) _err = n + ": " + a.ToString(CultureInfo.InvariantCulture) + " != " + b.ToString(CultureInfo.InvariantCulture);
        }

        private static AirTrack Trk(int id, double lat, double lon, double course, double kn, bool hostile = true)
        {
            return new AirTrack { Id = id, Lat = lat, Lon = lon, CourseDeg = course, SpeedKn = kn, Hostile = hostile, Cls = "MiG-21bis", RNm = 10, RSrc = "class" };
        }

        /// <summary>part: radius | eta | stage | cover | raid | all. null = ok, otherwise the error text.</summary>
        public static string SelfTest(string part)
        {
            _err = null;
            try
            {
                if (part == "radius" || part == "all") Radius();
                if (part == "eta" || part == "all") Eta();
                if (part == "stage" || part == "all") Stage();
                if (part == "cover" || part == "all") CoverTest();
                if (part == "raid" || part == "all") RaidTest();
                if (part == "soft" || part == "all") SoftTest();
                if (part == "rehold" || part == "all") ReholdTest();
                if (part == "lat" || part == "all") LatTest();
            }
            catch (Exception e) { return part + ": " + e.GetType().Name + ": " + e.Message; }
            return _err;
        }

        // T-348: Raids merges two tracks 3 true nm apart east-west, SegDistNm and Cover on the globe
        private static void LatGeomTest()
        {
            foreach (double lat in new[] { 0.0, 45, 60, 70 })
                using (AswMath.WorldScope(true))
                {
                    string tag = " @" + lat;
                    var P = new GeoPt(lat, 10);
                    GeoPt q = AswMath.Offset(P, 90, 3);
                    var a = Trk(1, P.Lat, P.Lon, 180, 540); var b = Trk(2, q.Lat, q.Lon, 180, 540);
                    var pr = new AirGuardParams { RaidNm = 3.2, RaidCourseDeg = 20 };
                    var raids = Raids(new List<AirTrack> { a, b }, pr);
                    True("lat raids merge" + tag, raids.Count == 1);
                    GeoPt n = AswMath.Offset(P, 0, 20), e2 = AswMath.Offset(P, 90, 2);
                    Near("lat segdist" + tag, SegDistNm(P, n, e2), 2, 0.03);
                }
            using (AswMath.WorldScope(false))
            {
                var P = new GeoPt(60, 10);
                var a = Trk(1, P.Lat, P.Lon, 180, 540); var b = Trk(2, P.Lat, P.Lon + 3 / 60.0, 180, 540);
                True("lat raids flat", Raids(new List<AirTrack> { a, b }, new AirGuardParams { RaidNm = 3.2, RaidCourseDeg = 20 }).Count == 1);
            }
        }

        // T-347: closing speed and ETA on the beta globe (cos of the latitude) and the flat stable world, at 0 / 45 / 60 / 70 deg
        private static void LatTest()
        {
            LatGeomTest();
            foreach (double lat in new[] { 0.0, 45, 60, 70 })
            {
                var P = new GeoPt(lat, 0);
                var at = AswMath.OffsetNm(P, 60 * Math.Cos(Math.PI / 4), 60 * Math.Sin(Math.PI / 4), true);
                var t = Trk(1, at.Lat, at.Lon, 225, 600); t.Globe = true;
                var o = new AirOwn { Lat = lat, Lon = 0, CourseDeg = 0, SpeedKn = 0 };
                string tag = " @" + lat;
                double cl = ClosingKn(t, o);
                Near("lat closing" + tag, cl, 600, 6);
                Near("lat eta" + tag, EtaS(AswMath.DistNm(new GeoPt(t.Lat, t.Lon), P, true), 10, cl), 300, 4.5);
                var oc = new AirOwn { Lat = lat, Lon = 0, CourseDeg = 225 + 180, SpeedKn = 100 };
                var t2 = Trk(2, at.Lat, at.Lon, 225, 600); t2.Globe = true;
                Near("lat closing own speed" + tag, ClosingKn(t2, oc), 700, 7);
                Near("lat eta own speed" + tag, EtaS(AswMath.DistNm(new GeoPt(t2.Lat, t2.Lon), P, true), 10, ClosingKn(t2, oc)), 257, 4);
                var tg = Trk(3, at.Lat, at.Lon, 315, 600); tg.Globe = true;
                True("lat tangent" + tag, Math.Abs(TrackClosingKn(tg, lat, 0)) < 5);
                if (lat == 60)
                {
                    var old = Trk(4, at.Lat, at.Lon, 315, 600);   // the flat rule on the beta globe
                    True("lat flat rule is wrong on the globe", Math.Abs(TrackClosingKn(old, lat, 0)) > 100);
                }
            }
        }

        private static void ReholdTest()
        {
            var p = new AirGuardParams();
            double cs = -1;
            True("rehold: inside R+5 at once (in the pause)", AirGuardMath.HeloReholds(14, 10, 0, 105, 100, ref cs, p));
            cs = -1;
            True("rehold: pause 30 s -> no", !AirGuardMath.HeloReholds(40, 10, 400, 130, 100, ref cs, p) && cs < 0);
            cs = -1;
            bool r = false;
            for (double t = 160; t < 160 + 19; t++) r = AirGuardMath.HeloReholds(40, 10, 400, t, 100, ref cs, p);
            True("rehold: 19 s after the pause -> no", !r && cs >= 0);
            True("rehold: 20 s -> yes", AirGuardMath.HeloReholds(40, 10, 400, 160 + 20, 100, ref cs, p));
            cs = -1;
            for (double t = 160; t < 280; t++) r = AirGuardMath.HeloReholds(40, 10, 40, t, 100, ref cs, p);
            True("rehold: 40 kn for 120 s -> no, counter reset", !r && cs < 0);
            cs = -1;
            for (double t = 160; t < 170; t++) AirGuardMath.HeloReholds(40, 10, 400, t, 100, ref cs, p);
            AirGuardMath.HeloReholds(40, 10, -20, 170, 100, ref cs, p);
            True("rehold: a break resets the count", cs < 0 && !AirGuardMath.HeloReholds(40, 10, 400, 171, 100, ref cs, p));
        }

        private static void SoftTest()
        {
            var p = new AirGuardParams();
            double lat = 0, lon = 0;
            var inbound = Trk(1, 0.5, 0, 180, 400);       // 30 nm north, flying south
            var outbound = Trk(2, 0.5, 0, 0, 400);
            var tangent = Trk(3, 0.5, 0, 90, 400);
            Near("closing inbound", TrackClosingKn(inbound, lat, lon), 400, 1e-3);
            Near("closing outbound", TrackClosingKn(outbound, lat, lon), -400, 1e-3);
            Near("closing tangent", TrackClosingKn(tangent, lat, lon), 0, 1e-3);
            True("inbound holds", HeloHolds(inbound, 30, TrackClosingKn(inbound, lat, lon), p));
            True("outbound does not hold", !HeloHolds(outbound, 30, TrackClosingKn(outbound, lat, lon), p));
            True("tangent does not hold", !HeloHolds(tangent, 30, TrackClosingKn(tangent, lat, lon), p));
            // receding but the helicopter is inside R_t + PathMarginNm (10 + 5): holds
            var near = Trk(4, 0.2, 0, 0, 400);            // 12 nm north, flying north
            True("inside the radius holds", HeloHolds(near, 12, TrackClosingKn(near, lat, lon), p));
            var far = Trk(5, 0.3, 0, 0, 400);             // 18 nm: outside 15
            True("outside the radius does not hold", !HeloHolds(far, 18, TrackClosingKn(far, lat, lon), p));
            // the own speed does not matter: only the track is read
            Near("own speed ignored", TrackClosingKn(outbound, lat, lon), TrackClosingKn(outbound, 0, 0.001), 1e-1);
        }

        private static void Radius()
        {
            var p = new AirGuardParams(); string src;
            Near("radius class max", ThreatRadiusNm(true, new List<double> { 8, 25, 14 }, p, out src), 25); True("src class", src == "class");
            Near("radius no data", ThreatRadiusNm(true, null, p, out src), 10); True("src default (no data)", src == "default");
            Near("radius unidentified", ThreatRadiusNm(false, new List<double> { 25 }, p, out src), 10); True("src default (unid)", src == "default");
            Near("radius floor", ThreatRadiusNm(true, new List<double> { 1.5 }, p, out src), 3);
            Near("radius no aaw", ThreatRadiusNm(true, new List<double>(), p, out src), 3); True("src no_aaw", src == "no_aaw");
            Near("radius no aaw zeros", ThreatRadiusNm(true, new List<double> { 0, 0 }, p, out src), 3); True("src no_aaw 2", src == "no_aaw");
        }

        private static void Eta()
        {
            var o = new AirOwn { Lat = 0, Lon = 0, CourseDeg = 0, SpeedKn = 0 };
            // threat 60 nm north, flying south at 600 kn: closing 600
            var t = Trk(1, 1.0, 0, 180, 600);
            Near("closing head-on", ClosingKn(t, o), 600, 1e-3);
            Near("eta 50 nm / 600", EtaS(60, 10, 600), 300, 1e-6);
            // moving away
            var away = Trk(2, 1.0, 0, 0, 600);
            True("closing away < 0", ClosingKn(away, o) < 0);
            Near("eta away = inf", EtaS(60, 10, ClosingKn(away, o)), double.PositiveInfinity);
            // own bird flying toward the threat adds to the closing speed
            o.CourseDeg = 0; o.SpeedKn = 100;
            Near("closing with own speed", ClosingKn(t, o), 700, 1e-3);
            Near("eta inside radius", EtaS(8, 10, 600), 0);
            // beam threat: no closing
            var beam = Trk(3, 0, 1.0, 90, 600);   // east of us, moving east
            True("closing beam away", ClosingKn(beam, new AirOwn { Lat = 0, Lon = 0 }) < 0);
        }

        private static void Stage()
        {
            var p = new AirGuardParams(); string why;
            True("59 nm plane warn", RawStage(59, double.PositiveInfinity, false, p, out why) == AirStage.Warn);
            True("61 nm plane none", RawStage(61, double.PositiveInfinity, false, p, out why) == AirStage.None);
            True("40 nm plane retreat", RawStage(40, double.PositiveInfinity, false, p, out why) == AirStage.Retreat && why == "dist");
            True("41 nm plane warn", RawStage(41, double.PositiveInfinity, false, p, out why) == AirStage.Warn);
            True("helo 50 retreat", RawStage(50, double.PositiveInfinity, true, p, out why) == AirStage.Retreat);
            True("helo 51 warn", RawStage(51, double.PositiveInfinity, true, p, out why) == AirStage.Warn);
            True("helo 79 warn", RawStage(79, double.PositiveInfinity, true, p, out why) == AirStage.Warn);
            True("helo 81 none", RawStage(81, double.PositiveInfinity, true, p, out why) == AirStage.None);
            True("eta 350 retreat", RawStage(70, 350, false, p, out why) == AirStage.Retreat && why == "eta");
            True("eta 370 none", RawStage(70, 370, false, p, out why) == AirStage.None);
            True("helo eta 470 retreat", RawStage(90, 470, true, p, out why) == AirStage.Retreat);
            // hysteresis: the stage drops only after ClearS of clear conditions
            double cs = -1;
            True("hyst rises at once", Hysteresis(AirStage.None, AirStage.Retreat, false, ref cs, 0, p) == AirStage.Retreat);
            True("hyst holds when not clear", Hysteresis(AirStage.Retreat, AirStage.Warn, false, ref cs, 10, p) == AirStage.Retreat && cs < 0);
            True("hyst clear starts", Hysteresis(AirStage.Retreat, AirStage.Warn, true, ref cs, 20, p) == AirStage.Retreat && cs == 20);
            True("hyst clear 59 s", Hysteresis(AirStage.Retreat, AirStage.Warn, true, ref cs, 79, p) == AirStage.Retreat);
            True("hyst clear 60 s", Hysteresis(AirStage.Retreat, AirStage.Warn, true, ref cs, 80, p) == AirStage.Warn);
            cs = 5;
            True("hyst interrupted resets", Hysteresis(AirStage.Retreat, AirStage.Warn, false, ref cs, 30, p) == AirStage.Retreat && cs < 0);
            bool w;
            True("eligible hostile", ThreatEligible(Trk(1, 0, 0, 0, 100), p, out w) && !w);
            var unk = Trk(2, 0, 0, 0, 450, false); unk.Unknown = true;
            True("unknown 450 warn only", ThreatEligible(unk, p, out w) && w);
            unk.SpeedKn = 300;
            True("unknown 300 none", !ThreatEligible(unk, p, out w));
            unk.SpeedKn = 450; p.Unknown = "retreat";
            True("unknown retreat", ThreatEligible(unk, p, out w) && !w);
            p.Unknown = "off";
            True("unknown off", !ThreatEligible(unk, p, out w));
            var dead = Trk(3, 0, 0, 0, 100); dead.Destroyed = true;
            True("destroyed not eligible", !ThreatEligible(dead, new AirGuardParams(), out w));
        }

        private static void CoverTest()
        {
            var p = new AirGuardParams();
            // threat 36.8 nm north of the ship, bird 10 nm east of the ship; ship ring 40 nm
            var ship = new AirSam { Uid = 100, Name = "Yorktown", Lat = 0, Lon = 0, RSamNm = 40 };
            var thr = Trk(1, 36.8 / 60.0, 0, 180, 540); thr.RNm = 10;
            var bird = new AirOwn { Uid = 7, Lat = 0, Lon = 10.0 / 60.0, SpeedKn = 250, Helicopter = false };
            var c = Cover(bird, thr, new List<AirSam> { ship }, 250, p);
            True("cover mode", c.Mode == "cover" && c.ShipUid == 100);
            Near("cover nm = 6 (0.25*40 clamp 6)", AswMath.DistNm(new GeoPt(0, 0), c.Point), 6, 1e-6);
            True("cover is south of the ship (ship between threat and bird)", c.Point.Lat < 0);
            True("no note", c.Note == null);
            Near("CoverNm small ring", CoverNm(10), 2.5); Near("CoverNm floor", CoverNm(4), 2); Near("CoverNm cap", CoverNm(100), 6);
            // small ring is not a candidate
            var small = new AirSam { Uid = 101, Name = "Tug", Lat = 0, Lon = 0, RSamNm = 9 };
            c = Cover(bird, thr, new List<AirSam> { small }, 250, p);
            True("small ring -> away/no_cover", c.Mode == "away" && c.Note == "no_cover");
            c = Cover(bird, thr, null, 250, p);
            True("no ships -> no_cover", c.Mode == "away" && c.Note == "no_cover");
            // radar off
            var off = new AirSam { Uid = 102, Name = "Silent", Lat = 0, Lon = 0, RSamNm = 40, RadarOff = true };
            c = Cover(bird, thr, new List<AirSam> { off }, 250, p);
            True("radar off note", c.Mode == "cover" && c.Note == "cover_radar_off");
            // path through the threat: a ship beyond the threat is dropped when a safe one exists
            var behind = new AirSam { Uid = 103, Name = "Far", Lat = 80.0 / 60.0, Lon = 10.0 / 60.0, RSamNm = 40 };
            var nearBird = new AirOwn { Uid = 8, Lat = 20.0 / 60.0, Lon = 10.0 / 60.0, SpeedKn = 250 };
            var thr2 = Trk(2, 40.0 / 60.0, 10.0 / 60.0, 180, 540); thr2.RNm = 10;
            c = Cover(nearBird, thr2, new List<AirSam> { behind, ship }, 250, p);
            True("path through threat dropped", c.ShipUid == 100);
            c = Cover(nearBird, thr2, new List<AirSam> { behind }, 250, p);
            True("only risky -> best with cover_path_risk", c.ShipUid == 103 && c.Note == "cover_path_risk");
            // land (SAR with 16 aboard)
            var deck = new AirSam { Uid = 100, Name = "Yorktown", Lat = 0, Lon = 0, RSamNm = 40, Deck = true, HeloDeck = true };
            var helo = new AirOwn { Uid = 9, Lat = 0, Lon = 10.0 / 60.0, SpeedKn = 100, Helicopter = true };
            c = LandOrCover(helo, thr, new List<AirSam> { deck }, 100, 16, p);
            True("land with people", c.Mode == "land" && c.ShipUid == 100);
            c = LandOrCover(helo, thr, new List<AirSam> { deck }, 100, 0, p);
            True("empty -> cover", c.Mode == "cover");
            var farDeck = new AirSam { Uid = 104, Name = "Carrier", Lat = 0, Lon = -60.0 / 60.0, RSamNm = 40, Deck = true, HeloDeck = true };
            c = LandOrCover(helo, thr, new List<AirSam> { deck, farDeck }, 100, 16, p);
            True("nearest deck wins", c.Mode == "land" && c.ShipUid == 100);
            // a deck without its own ring inside the ring of another ship
            var cruiser = new AirSam { Uid = 105, Name = "Cruiser", Lat = 2.0 / 60.0, Lon = 0, RSamNm = 40 };
            var hull = new AirSam { Uid = 106, Name = "Tanker", Lat = 0, Lon = 4.0 / 60.0, RSamNm = 0, Deck = true, HeloDeck = true };
            c = LandOrCover(helo, thr, new List<AirSam> { cruiser, hull }, 100, 5, p);
            True("deck in another ring", c.Mode == "land" && c.ShipUid == 106);
            // a deck too far (later than cover + 120 s) -> cover
            var distant = new AirSam { Uid = 107, Name = "Distant", Lat = 0, Lon = 30.0 / 60.0, RSamNm = 40, Deck = true, HeloDeck = true };
            c = LandOrCover(helo, thr, new List<AirSam> { ship, distant }, 100, 5, p);
            True("late deck -> cover", c.Mode == "cover" && c.ShipUid == 100);
        }

        private static void RaidTest()
        {
            var p = new AirGuardParams();
            var a = Trk(5, 0, 0, 180, 540); var b = Trk(3, 3.0 / 60.0, 0, 190, 540); var c = Trk(9, 0, 30.0 / 60.0, 180, 540);
            var rs = Raids(new List<AirTrack> { a, b, c }, p);
            True("two raids", rs.Count == 2);
            True("raid 1 key min id", rs[0].Key == "r3" && rs[0].Count == 2 && rs[0].Cls == "MiG-21bis");
            True("raid 2 single", rs[1].Key == "r9" && rs[1].Count == 1);
            Near("raid course ~185", rs[0].CourseDeg, 185, 0.5);
            var d = Trk(6, 3.0 / 60.0, 0, 270, 540);   // close but crossing course
            rs = Raids(new List<AirTrack> { a, d }, p);
            True("different course -> two raids", rs.Count == 2);
            True("empty", Raids(null, p).Count == 0);
        }
    }
}
