using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase D (ADR-0132 section 8, design 5.8): the jammer aircraft of a strike. Pure: no Unity, no game objects, no clock (the time comes in as an argument).
    // Admission by the station, the sectors of the targets (one cone per aircraft), the station on the attack axis behind the launch line and outside every SAM zone, the time of the
    // departure / the switch on / the switch off, the check of the bands, and the state machine of the execution (Decide).
    internal static class StrikeEwMath
    {
        private static GeoPt Pos(StrikeShip s) { return new GeoPt(s.Lat, s.Lon); }
        private static string F1(double d) { return d.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        /// <summary>null (a usable jammer) | helicopter | ew_no_api (no way to give the order) | ew_inoperable (no working station Offensive | Universal) | no_ew.</summary>
        public static string EwWhy(StrikeAircraft a)
        {
            if (a == null || a.Ew == null) return "no_ew";
            if (string.Equals(a.ObjType, "Helicopter", StringComparison.OrdinalIgnoreCase)) return "helicopter";
            if (!a.Ew.Capable) return "ew_no_api";
            if (!a.Ew.Operable) return "ew_inoperable";
            return null;
        }

        /// <summary>The search range of the radar of a target against an aircraft at altFt: the radar maximum (unknown = 999) and the radio horizon of the radar height.</summary>
        public static double DetNm(StrikeShip s, double altFt)
        {
            if (s == null) return 0;
            return Math.Min(s.RadarMaxNm > 0 ? s.RadarMaxNm : 999, StrikeGeoMath.HorizonNm(s.RadarFt > 0 ? s.RadarFt : 100, altFt));
        }

        private static double Wrap180(double a) { a = AswMath.Norm360(a); return a > 180 ? a - 360 : a; }

        /// <summary>The target is inside the cone of the station: the angle between the aim and the target seen from the station is at most cone / 2 - margin.</summary>
        public static bool InCone(GeoPt station, GeoPt aim, GeoPt target, double coneDeg, double marginDeg)
        {
            double d = Math.Abs(Wrap180(AswMath.BearingDeg(station, target) - AswMath.BearingDeg(station, aim)));
            return d <= coneDeg / 2 - marginDeg + 1e-9;
        }

        /// <summary>The targets are split into sectors, one per jammer (cones[i] is the cone of the i-th); a sector is as wide as its cone minus the margins at both edges, the main target is in sector 0, the targets that fit no sector are uncovered.</summary>
        public static List<List<StrikeShip>> Sectors(IList<StrikeShip> cover, GeoPt from, IList<double> cones, double marginDeg, int mainId, out List<int> uncovered)
        {
            var res = new List<List<StrikeShip>>();
            uncovered = new List<int>();
            var left = new List<StrikeShip>();
            if (cover != null) foreach (var s in cover) if (s != null && !left.Any(x => x.TrackId == s.TrackId)) left.Add(s);
            var main = left.FirstOrDefault(s => s.TrackId == mainId) ?? (left.Count > 0 ? left[0] : null);
            double refB = main == null ? 0 : AswMath.BearingDeg(from, Pos(main));
            Func<StrikeShip, double> rel = s => Wrap180(AswMath.BearingDeg(from, Pos(s)) - refB);
            for (int i = 0; i < (cones == null ? 0 : cones.Count); i++)
            {
                double width = Math.Max(0, cones[i] - 2 * marginDeg);
                var best = new List<StrikeShip>();
                foreach (var start in left)
                {
                    double r0 = rel(start);
                    var win = left.Where(s => rel(s) >= r0 - 1e-9 && rel(s) <= r0 + width + 1e-9).ToList();
                    if (i == 0 && main != null && left.Contains(main) && !win.Contains(main)) continue;
                    if (win.Count > best.Count) best = win;
                }
                res.Add(best);
                foreach (var s in best) left.Remove(s);
            }
            foreach (var s in left) uncovered.Add(s.TrackId);
            return res;
        }

        /// <summary>No segment of the route comes nearer than SamMaxNm + extra to any hostile ship.</summary>
        public static bool RouteClear(IList<GeoPt> route, IList<StrikeShip> hostiles, double extraNm)
        {
            if (route == null || hostiles == null) return true;
            foreach (var s in hostiles)
            {
                if (s == null) continue;
                double r = Math.Max(0, s.SamMaxNm) + extraNm;
                var c = Pos(s);
                for (int i = 0; i < route.Count; i++)
                {
                    if (AswMath.DistNm(route[i], c) < r) return false;
                    if (i > 0 && StrikeGeoMath.ChordNm(route[i - 1], route[i], c, r) > 1e-6) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The station of a jammer for a sector: on the reverse attack axis from the aim, beyond minBackNm, in 1 nm steps up to 80 nm farther, then the axis turned by +-5..+-30 degrees. A point is good when it is over the sea, at least SamMaxNm
        /// + StandoffNm from EVERY hostile ship, every target of the sector is inside the cone and inside the radio horizon (and the range of the station). why: ok | no_sea | in_sam | cone | horizon.
        /// </summary>
        public static GeoPt? Station(IList<StrikeShip> sector, IList<StrikeShip> hostiles, GeoPt aim, double axisDeg, double minBackNm, StrikeEw ew, StrikeParams p, Func<GeoPt, bool> isLand, out string why)
        {
            why = "ok";
            bool sawSea = false, sawSam = false, sawCone = false;
            double cone = ew == null || ew.ConeDeg <= 0 ? 30 : ew.ConeDeg, range = ew == null || ew.RangeNm <= 0 ? 999 : ew.RangeNm;
            double[] turns = { 0, 5, -5, 10, -10, 15, -15, 20, -20, 25, -25, 30, -30 };
            foreach (double turn in turns)
            {
                double ax = AswMath.Norm360(axisDeg + 180 + turn);
                for (double d = Math.Max(0, minBackNm); d <= Math.Max(0, minBackNm) + 80; d += 1)
                {
                    var pt = AswMath.Offset(aim, ax, d);
                    if (isLand != null && isLand(pt)) continue;
                    sawSea = true;
                    bool samOk = true;
                    if (hostiles != null) foreach (var h in hostiles) if (h != null && AswMath.DistNm(pt, Pos(h)) < Math.Max(0, h.SamMaxNm) + p.StandoffNm) { samOk = false; break; }
                    if (!samOk) continue;
                    sawSam = true;
                    bool coneOk = true;
                    foreach (var t in sector) if (!InCone(pt, aim, Pos(t), cone, p.EwConeMarginDeg)) { coneOk = false; break; }
                    if (!coneOk) continue;
                    sawCone = true;
                    bool hz = true;
                    foreach (var t in sector)
                    {
                        double dist = AswMath.DistNm(pt, Pos(t));
                        if (dist > range || dist > StrikeGeoMath.HorizonNm(t.RadarFt > 0 ? t.RadarFt : 100, p.EwAltFt)) { hz = false; break; }
                    }
                    if (!hz) continue;
                    return pt;
                }
            }
            why = !sawSea ? "no_sea" : !sawSam ? "in_sam" : !sawCone ? "cone" : "horizon";
            return null;
        }

        private static StrikeShip Moved(StrikeShip s, double dtS)
        {
            var l = StrikeGeoMath.Lead(s, dtS);
            return new StrikeShip
            {
                TrackId = s.TrackId, Lat = l.Lat, Lon = l.Lon, CourseDeg = s.CourseDeg, SpeedKn = s.SpeedKn, SamMaxNm = s.SamMaxNm, SamCeilFt = s.SamCeilFt, RadarFt = s.RadarFt, RadarMaxNm = s.RadarMaxNm, RadarBands = s.RadarBands,
                Hostile = s.Hostile, Civilian = s.Civilian, Identified = s.Identified, Side = s.Side, ClassName = s.ClassName, ClassIni = s.ClassIni, Role = s.Role, DisplacementT = s.DisplacementT, Ad = s.Ad, Sams = s.Sams,
            };
        }

        /// <summary>The first moment the strike enters the detection range of the radars of the covered targets (absolute game time): the route of every aircraft (high before the descent point, low after it) and the flight of its missiles; none = the first launch.</summary>
        public static double TDetect(StrikeVariant v, IList<StrikeShip> cover, IDictionary<int, StrikeAircraft> air, StrikeParams p)
        {
            double best = double.MaxValue, firstLaunch = double.MaxValue;
            if (v == null || cover == null || cover.Count == 0) return -1;
            foreach (var a in v.Assignments)
            {
                firstLaunch = Math.Min(firstLaunch, a.TLaunch);
                StrikeAircraft ac = null;
                if (air != null) air.TryGetValue(a.Uid, out ac);
                double kn = ac != null && ac.CruiseKn > 0 ? ac.CruiseKn : 400;
                // the route backward from the launch point: the time of every point; the altitude is the cruise one up to the descent point
                var pts = new List<GeoPt>(); var alts = new List<double>();
                if (ac != null) { pts.Add(new GeoPt(ac.Lat, ac.Lon)); alts.Add(a.CruiseAltFt > 0 ? a.CruiseAltFt : ac.CruiseAltFt); }
                if (!a.DescentAtStart) { pts.Add(a.DescentPt); alts.Add(a.CruiseAltFt > 0 ? a.CruiseAltFt : 25000); }
                pts.Add(a.GatePt); alts.Add(a.LaunchAltFt > 0 ? a.LaunchAltFt : 200);
                pts.Add(a.LaunchPt); alts.Add(a.LaunchAltFt > 0 ? a.LaunchAltFt : 200);
                double total = 0;
                var seg = new List<double>();
                for (int i = 1; i < pts.Count; i++) { double d = AswMath.DistNm(pts[i - 1], pts[i]); seg.Add(d); total += d; }
                double t0 = a.TLaunch - total / kn * 3600.0;   // the time at the first point
                double acc = 0;
                for (int i = 1; i < pts.Count && best > t0; i++)
                {
                    double len = seg[i - 1];
                    for (double s = 0; s <= len + 1e-9; s += 1)
                    {
                        double f = len < 1e-9 ? 0 : Math.Min(1, s / len);
                        var pt = AswMath.Offset(pts[i - 1], AswMath.BearingDeg(pts[i - 1], pts[i]), len * f);
                        double t = t0 + (acc + len * f) / kn * 3600.0;
                        foreach (var c in cover)
                            if (AswMath.DistNm(pt, Pos(c)) <= DetNm(c, alts[i])) { best = Math.Min(best, t); break; }
                        if (best <= t) break;
                    }
                    acc += len;
                }
                // the missiles: from the launch point to the target at their own altitude and speed
                var tgt = cover.FirstOrDefault(c => c.TrackId == a.TargetId);
                if (tgt != null)
                {
                    StrikeMissile m = ac == null ? null : ac.Missiles.FirstOrDefault(x => x.Ammo == a.Ammo);
                    double mkn = m != null && m.SpeedKn > 0 ? m.SpeedKn : 540, malt = m != null && m.CruiseAltFt > 0 ? m.CruiseAltFt : 50;
                    double len = AswMath.DistNm(a.LaunchPt, Pos(tgt)), brg = AswMath.BearingDeg(a.LaunchPt, Pos(tgt));
                    for (double s = 0; s <= len + 1e-9; s += 1)
                    {
                        var pt = AswMath.Offset(a.LaunchPt, brg, Math.Min(s, len));
                        double t = a.TLaunch + s / mkn * 3600.0;
                        bool hit = false;
                        foreach (var c in cover) if (AswMath.DistNm(pt, Pos(c)) <= DetNm(c, malt)) { hit = true; break; }
                        if (hit) { best = Math.Min(best, t); break; }
                    }
                }
            }
            // the aircraft that wait outside the zones are in the range of the radars long before the strike starts: the jammer is needed from the start of the attack run (the first aircraft leaves its gate)
            double runStart = v.Assignments.Count == 0 ? -1 : v.Assignments.Min(a => a.TGo);
            if (best < double.MaxValue) return runStart > 0 ? Math.Max(best, runStart) : best;
            return firstLaunch < double.MaxValue ? firstLaunch : -1;
        }

        /// <summary>"track:band" for every radar band of a target of the sector that no jammer of the sector covers (a target with unknown bands says nothing).</summary>
        public static List<string> BandsMissing(IList<StrikeShip> sector, IEnumerable<StrikeEw> ews)
        {
            var res = new List<string>();
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ews) if (e != null && e.Bands != null) foreach (string b in e.Bands) have.Add(b);
            foreach (var s in sector)
            {
                if (s == null || s.RadarBands == null) continue;
                foreach (string b in s.RadarBands) if (!have.Contains(b)) res.Add(I(s.TrackId) + ":" + b);
            }
            return res;
        }

        /// <summary>The plan of one jammer for its sector in a variant.</summary>
        public static StrikeEwPlan PlanOne(StrikeAircraft ew, IList<StrikeShip> sector, IList<StrikeShip> hostiles, StrikeVariant v, GeoPt? wait, double now, StrikeParams p, Func<GeoPt, bool> isLand, IDictionary<int, StrikeAircraft> air, IList<StrikeShip> uncoveredShips = null)
        {
            var pl = new StrikeEwPlan { Uid = ew.Uid, ConeDeg = ew.Ew == null || ew.Ew.ConeDeg <= 0 ? 30 : ew.Ew.ConeDeg, AltFt = p.EwAltFt, Deck = !ew.Airborne };
            foreach (var s in sector) pl.CoverIds.Add(s.TrackId);
            pl.TOff = v.T + p.EwHoldS;
            if (sector.Count == 0) { pl.Why = "no_targets"; pl.StationPt = wait ?? new GeoPt(ew.Lat, ew.Lon); return pl; }
            double la = sector.Average(s => s.Lat), lo = GeoLon.MeanLon(sector.Select(s => s.Lon));
            pl.AimPt = new GeoPt(la, lo);
            double minBack = 0;
            foreach (var a in v.Assignments) if (sector.Any(s => s.TrackId == a.TargetId)) minBack = Math.Max(minBack, a.LaunchNm);
            if (minBack <= 0) foreach (var a in v.Assignments) minBack = Math.Max(minBack, a.LaunchNm);
            minBack += p.EwBehindNm;
            string why;
            var st = Station(sector, hostiles, pl.AimPt, v.AxisDeg, minBack, ew.Ew, p, isLand, out why);
            pl.TDetect = TDetect(v, sector, air, p);
            if (!st.HasValue) { pl.Why = "no_station:" + why; pl.StationPt = wait ?? new GeoPt(ew.Lat, ew.Lon); return pl; }
            pl.StationPt = st.Value;
            pl.StationNm = AswMath.DistNm(st.Value, pl.AimPt);
            pl.BearingDeg = AswMath.BearingDeg(st.Value, pl.AimPt);
            var from = new GeoPt(ew.Lat, ew.Lon);
            var direct = new List<GeoPt> { from, st.Value };
            double extra = p.StandoffNm * p.EwGuardFrac;
            if (RouteClear(direct, hostiles, extra)) pl.Route = new List<GeoPt> { st.Value };
            else if (wait.HasValue && RouteClear(new List<GeoPt> { from, wait.Value, st.Value }, hostiles, extra)) pl.Route = new List<GeoPt> { wait.Value, st.Value };
            else { pl.Why = "no_station:route_in_sam"; pl.StationPt = wait ?? from; return pl; }
            double len = 0; var prev = from;
            foreach (var rp in pl.Route) { len += AswMath.DistNm(prev, rp); prev = rp; }
            double kn = ew.CruiseKn > 0 ? Math.Max(ew.CruiseKn, 250) : 250;
            pl.TOn = pl.TDetect - p.EwOnLeadS;
            pl.TStation = pl.TOn - p.EwSettleS;
            pl.TDepart = pl.TStation - len / kn * 3600.0;
            double ready = ew.Airborne || ew.ReadyInS < 0 ? 0 : ew.ReadyInS;
            pl.LateS = Math.Max(0, now + ready - pl.TDepart);
            pl.ShiftS = pl.LateS <= 0 ? 0 : Math.Ceiling(pl.LateS / 60.0) * 60.0;
            pl.BandsMissing = BandsMissing(sector, new[] { ew.Ew });
            pl.Why = "ok";
            return pl;
        }

        /// <summary>Fills v.Ew for every jammer: the covered targets (the main one, the targets of the plan, the ships with SAMs whose zones the strike crosses), the sectors, the stations, the warnings ew_late | ew_uncovered | ew_band | ew_no_station.</summary>
        public static void PlanEw(StrikeVariant v, IList<StrikeAircraft> ews, IList<StrikeShip> ships, StrikeTargetSet tgt, GeoPt? wait, double now, StrikeParams p, Func<GeoPt, bool> isLand, IDictionary<int, StrikeAircraft> air)
        {
            v.Ew = new List<StrikeEwPlan>();
            if (ews == null || ews.Count == 0 || v.Assignments.Count == 0) return;
            double dt = Math.Max(0, v.T - now);
            var all = new List<StrikeShip>();
            foreach (var s in ships) if (s != null && s.Hostile && !s.Civilian) all.Add(Moved(s, dt));
            var byId = all.ToDictionary(s => s.TrackId);
            var cover = new List<StrikeShip>();
            Action<int> add = id => { StrikeShip s; if (byId.TryGetValue(id, out s) && !cover.Any(c => c.TrackId == id)) cover.Add(s); };
            if (tgt != null) add(tgt.MainId);
            foreach (var t in v.PerTarget) add(t.TrackId);
            foreach (var a in v.Assignments)
            {
                StrikeShip target;
                if (!byId.TryGetValue(a.TargetId, out target)) continue;
                foreach (var s in all)
                {
                    if (s.SamMaxNm <= 0 || cover.Any(c => c.TrackId == s.TrackId)) continue;
                    if (StrikeGeoMath.ChordNm(a.LaunchPt, Pos(target), Pos(s), s.SamMaxNm) > 0.5) cover.Add(s);
                }
            }
            if (cover.Count == 0) return;
            var hostiles = all;
            var order = ews.OrderByDescending(e => e.Ew == null ? 0 : e.Ew.ConeDeg).ThenBy(e => e.Uid).ToList();
            var cones = order.Select(e => e.Ew == null || e.Ew.ConeDeg <= 0 ? 30.0 : e.Ew.ConeDeg).ToList();
            double fla = v.Assignments.Average(a => a.LaunchPt.Lat), flo = GeoLon.MeanLon(v.Assignments.Select(a => a.LaunchPt.Lon));
            List<int> uncovered;
            var sectors = Sectors(cover, new GeoPt(fla, flo), cones, p.EwConeMarginDeg, tgt == null ? 0 : tgt.MainId, out uncovered);
            int w0 = v.Warnings.Count;
            // T-342 D (43 of the plan): with two or more jammers, when the sectors without the last one still cover everything, the last one is the reserve (Role fighters): it jams the main target like the others
            // (it gets a copy of sector 0) and takes the fighter threats in every working state. Otherwise every jammer is "ships" as in v1.
            if (order.Count >= 2)
            {
                List<int> uncRed;
                var secRed = Sectors(cover, new GeoPt(fla, flo), cones.Take(cones.Count - 1).ToList(), p.EwConeMarginDeg, tgt == null ? 0 : tgt.MainId, out uncRed);
                if (uncRed.Count == 0 && secRed.Count > 0)
                {
                    var secs = new List<List<StrikeShip>>(secRed) { secRed[0] };
                    EmitEw(v, order, secs, order.Count - 1, uncovered, hostiles, wait, now, p, isLand, air);
                    bool good = v.Ew.Count == order.Count && v.Ew[v.Ew.Count - 1].Why == "ok";
                    for (int i = 0; good && i < v.Ew.Count; i++) if (v.Ew[i].CoverIds.Count > 0 && (v.Ew[i].Why != "ok" || v.Ew[i].BandsMissing.Count > 0)) good = false;
                    if (good) { v.Warnings.Add("ew_fighter_reserve:" + I(order[order.Count - 1].Uid)); return; }
                    v.Warnings.RemoveRange(w0, v.Warnings.Count - w0);
                }
            }
            EmitEw(v, order, sectors, -1, uncovered, hostiles, wait, now, p, isLand, air);
        }

        private static void EmitEw(StrikeVariant v, List<StrikeAircraft> order, IList<List<StrikeShip>> sectors, int reserveIdx, List<int> uncoveredIn, IList<StrikeShip> hostiles, GeoPt? wait, double now, StrikeParams p, Func<GeoPt, bool> isLand, IDictionary<int, StrikeAircraft> air)
        {
            var uncovered = new List<int>(uncoveredIn);
            v.Ew = new List<StrikeEwPlan>();
            for (int i = 0; i < order.Count; i++)
            {
                var pl = PlanOne(order[i], i < sectors.Count ? sectors[i] : new List<StrikeShip>(), hostiles, v, wait, now, p, isLand, air);
                if (i == reserveIdx) pl.Role = "fighters";
                pl.Uncovered = new List<int>(uncovered);
                v.Ew.Add(pl);
                if (pl.Why == "ok")
                {
                    if (pl.LateS > 0) v.Warnings.Add("ew_late:" + I(pl.Uid) + ":" + I((int)Math.Ceiling(pl.LateS / 60.0)));
                    foreach (string b in pl.BandsMissing) v.Warnings.Add("ew_band:" + b);
                }
                else if (pl.Why.StartsWith("no_station", StringComparison.Ordinal)) v.Warnings.Add("ew_no_station:" + I(pl.Uid) + ":" + pl.Why.Substring(Math.Min(pl.Why.Length, 11)));
            }
            foreach (int id in uncovered) v.Warnings.Add("ew_uncovered:" + I(id));
            // the targets of a sector that has no station are not covered either
            foreach (var pl in v.Ew) if (pl.Why != "ok" && pl.CoverIds.Count > 0) foreach (int id in pl.CoverIds) if (!uncovered.Contains(id)) { uncovered.Add(id); v.Warnings.Add("ew_uncovered:" + I(id)); }
            foreach (var pl in v.Ew) pl.Uncovered = new List<int>(uncovered);
        }

        /// <summary>The next state of a jammer (no side effects). sam = a hostile ship is closer than SamMaxNm + StandoffNm * EwGuardFrac; airRetreat = the air threat service says retreat; threatClearS = seconds since the last air threat (any stage) for this aircraft (+inf = never); stationThreat (T-349) = the station point has an air threat, the retreat is held while it stands.</summary>
        public static string Decide(StrikeEwState s, StrikeEwPlan pl, double now, double distToStationNm, double minStrikerToCoverNm, double detNm, bool allTerminal, int inFlight, double lastArriveT, bool sam, bool airRetreat, StrikeParams p, double threatClearS = double.PositiveInfinity, bool fighterUnsafe = false, bool fightersFar = true, bool stationThreat = false)
        {
            string st = s.State ?? "wait";
            if (st == "no_station" || st == "lost" || st == "back") return st;
            if (sam && st != "off") return "guard";
            if ((airRetreat || fighterUnsafe) && st != "off") return "retreat";
            if (st == "guard" || st == "retreat")
            {
                if (allTerminal && inFlight == 0) return "back";
                // T-342: from an air retreat the jammer returns to the station only after EwReturnClearS without any air threat for it (a guard before a SAM zone returns as before)
                if (st == "retreat" && (threatClearS < p.EwReturnClearS || !fightersFar || stationThreat)) return "retreat";
                return "to_station";
            }
            // review of D: the strike is over (all rows terminal, nothing in flight) before the jammer has begun to work: it does not fly out / switch on for nothing
            if (allTerminal && inFlight == 0 && (st == "wait" || st == "to_station" || st == "on_station")) return "back";
            switch (st)
            {
                case "jamming":
                    if ((allTerminal && inFlight == 0 && (lastArriveT < 0 || now >= lastArriveT + p.EwHoldS)) || now >= pl.TOff + p.LateSkipS) return "off";
                    return st;
                case "on_station":
                    if (now >= pl.TOn || (minStrikerToCoverNm >= 0 && minStrikerToCoverNm <= detNm + p.EwOnMarginNm)) return "jamming";
                    return st;
                case "to_station":
                    return distToStationNm <= p.EwStationTolNm ? "on_station" : st;
                case "wait":
                    return now >= pl.TDepart ? "to_station" : st;
                case "off":
                    return "back";
            }
            return st;
        }

        // ---------------------------------------------------------------- self test

        private static StrikeShip Tgt(int id, double lat, double lon, double sam = 90, double radarFt = 100, string[] bands = null)
        {
            return new StrikeShip { TrackId = id, Lat = lat, Lon = lon, SamMaxNm = sam, RadarFt = radarFt, RadarMaxNm = 150, Hostile = true, Side = "hostile", Identified = true, RadarBands = bands, ClassName = "Kidd" };
        }

        private static StrikeAircraft Ea(int uid, double lat, double lon, double cone = 30, string[] bands = null, string type = "usn_ea-6b")
        {
            return new StrikeAircraft
            {
                Uid = uid, Type = type, ObjType = "Aircraft", Airborne = true, Lat = lat, Lon = lon, CruiseKn = 400, CruiseAltFt = 25000, AltFt = 25000,
                Ew = new StrikeEw { RangeNm = 200, ConeDeg = cone, Bands = bands ?? new[] { "VHF", "UHF", "L", "S", "C", "X" }, Type = "Universal", StationName = "AN/ALQ-99", Stations = 1 },
            };
        }

        private static StrikeVariant Var(double axis, double tLaunch, double tArrive, params StrikeShip[] targets)
        {
            var v = new StrikeVariant { Kind = "min_missiles", AxisDeg = axis, T = tArrive };
            foreach (var t in targets)
            {
                var la = AswMath.Offset(new GeoPt(t.Lat, t.Lon), axis + 180, 50);
                var gate = AswMath.Offset(la, axis + 180, 15);
                v.PerTarget.Add(new StrikeTargetPlan { TrackId = t.TrackId, N = 4, P = 0.9 });
                v.Assignments.Add(new StrikeAssignment { Uid = 1, TargetId = t.TrackId, Salvo = 4, Ammo = "usn_agm-84c", GatePt = gate, LaunchPt = la, LaunchAltFt = 200, CruiseAltFt = 25000, LaunchNm = 50, TLaunch = tLaunch, TArrive = tArrive, DescentAtStart = false, DescentPt = AswMath.Offset(gate, axis + 180, 40), TofS = tArrive - tLaunch });
            }
            return v;
        }

        public static string SelfTest(string part = "all")
        {
            bool all = part == "all";
            var p = new StrikeParams();
            var aircraft = new List<StrikeAircraft>();
            if (all || part == "eligibility")
            {
                var ea6 = Ea(1, 39, 10);
                var tu = Ea(2, 39, 10, 30, new[] { "VHF", "UHF", "L", "S", "C", "X", "Ku" }, "wp_tu-16p");
                var taifun = Ea(3, 39, 10, 30, null, "wp_taifun-ew");
                var eka = Ea(4, 39, 10, 20, null, "usn_eka-3b"); eka.Ew.Type = "Offensive";
                foreach (var a in new[] { ea6, tu, taifun, eka }) if (StrikeEwMath.EwWhy(a) != null) return "eligibility " + a.Type;
                var list = new List<StrikeAircraft> { ea6, tu, taifun, eka }; StrikeAirMath.Classify(list);
                foreach (var a in list) if (a.Role != "ew" || a.Why != null) return "role " + a.Type + " " + a.Role + " " + a.Why;
                var noApi = Ea(5, 39, 10); noApi.Ew.Capable = false;
                var broken = Ea(6, 39, 10); broken.Ew.Operable = false;
                var heli = Ea(7, 39, 10); heli.ObjType = "Helicopter";
                var l2 = new List<StrikeAircraft> { noApi, broken, heli }; StrikeAirMath.Classify(l2);
                if (noApi.Role != "excluded" || noApi.Why != "ew_no_api" || broken.Why != "ew_inoperable" || heli.Why != "helicopter") return "refusals " + noApi.Why + " " + broken.Why + " " + heli.Why;
                var striker = Ea(8, 39, 10); striker.Missiles.Add(new StrikeMissile { Ammo = "usn_agm-84c", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 2, MinNm = 3.5, MaxNm = 65, Operable = true });
                StrikeAirMath.Classify(new[] { striker });
                if (striker.Role != "striker") return "striker with a station";
                var noStation = new StrikeAircraft { Uid = 9, Type = "usn_a-6e", ObjType = "Aircraft", Airborne = true };
                StrikeAirMath.Classify(new[] { noStation });
                if (noStation.Role != "excluded") return "defensive only";
            }
            if (all || part == "sectors")
            {
                var from = new GeoPt(39, 10);
                List<int> unc;
                // three ships 10 degrees apart at 40 nm: one 30 degree jammer covers all
                var ships = new List<StrikeShip>();
                foreach (double b in new[] { -10.0, 0, 10 }) { var pt = AswMath.Offset(from, b, 40); ships.Add(Tgt(7000 + (int)b, pt.Lat, pt.Lon)); }
                var sec = Sectors(ships, from, new[] { 30.0 }, 2, 7000, out unc);
                if (sec.Count != 1 || sec[0].Count != 3 || unc.Count != 0) return "one cone covers 3: " + sec[0].Count + " unc " + unc.Count;
                // bearings 0 / 40 / 80: one cone covers the main one only
                var wide = new List<StrikeShip>();
                foreach (double b in new[] { 0.0, 40, 80 }) { var pt = AswMath.Offset(from, b, 40); wide.Add(Tgt(7100 + (int)b, pt.Lat, pt.Lon)); }
                sec = Sectors(wide, from, new[] { 30.0 }, 2, 7100, out unc);
                if (sec[0].Count != 1 || sec[0][0].TrackId != 7100 || unc.Count != 2) return "wide: " + sec[0].Count + " unc " + unc.Count;
                sec = Sectors(wide, from, new[] { 30.0, 30.0 }, 2, 7100, out unc);
                if (sec.Count != 2 || sec[1].Count != 1 || unc.Count != 1 || !sec[0].Any(s => s.TrackId == 7100)) return "two jammers: unc " + unc.Count;
                // the narrow cone of an EKA-3B: 20 degrees - 4 = 16 degrees wide
                var close = new List<StrikeShip>();
                foreach (double b in new[] { 0.0, 15, 17 }) { var pt = AswMath.Offset(from, b, 40); close.Add(Tgt(7200 + (int)b, pt.Lat, pt.Lon)); }
                sec = Sectors(close, from, new[] { 20.0 }, 2, 7200, out unc);
                if (sec[0].Count != 2 || unc.Count != 1) return "narrow cone: " + sec[0].Count + " unc " + unc.Count;
                // the edge of the cone
                var stn = new GeoPt(38, 10);
                var aim = new GeoPt(40, 10);
                var edge = AswMath.Offset(stn, 13, 120); var out2 = AswMath.Offset(stn, 13.5, 120);
                if (!InCone(stn, aim, edge, 30, 2) || InCone(stn, aim, out2, 30, 2)) return "cone edge";
            }
            if (all || part == "station")
            {
                var kidd = Tgt(100, 41, 10, 25);
                var neigh = Tgt(101, 41, 10.6, 40);
                p = new StrikeParams();
                var ew = new StrikeEw { RangeNm = 200, ConeDeg = 30 };
                var hostiles = new List<StrikeShip> { kidd, neigh };
                string why;
                var pt = Station(new List<StrikeShip> { kidd }, hostiles, new GeoPt(41, 10), 0, 55, ew, p, null, out why);
                if (!pt.HasValue || why != "ok") return "station " + why;
                if (AswMath.DistNm(pt.Value, new GeoPt(41, 10)) < 35 - 1e-6 || AswMath.DistNm(pt.Value, new GeoPt(41, 10.6)) < 50 - 1e-6) return "station too close " + AswMath.DistNm(pt.Value, new GeoPt(41, 10)).ToString("0.0") + " " + AswMath.DistNm(pt.Value, new GeoPt(41, 10.6)).ToString("0.0");
                if (AswMath.DistNm(pt.Value, new GeoPt(41, 10)) < 55 - 1e-6) return "station not behind the launch line";
                if (pt.Value.Lat >= 41) return "station on the wrong side";
                // land on the axis: the point moves; land everywhere: no_sea
                var land1 = Station(new List<StrikeShip> { kidd }, hostiles, new GeoPt(41, 10), 0, 55, ew, p, q => q.Lat < 40.3 && q.Lat > 39.7, out why);
                if (!land1.HasValue || (land1.Value.Lat < 40.3 && land1.Value.Lat > 39.7)) return "land shift " + why;
                var none = Station(new List<StrikeShip> { kidd }, hostiles, new GeoPt(41, 10), 0, 55, ew, p, q => true, out why);
                if (none.HasValue || why != "no_sea") return "no_sea " + why;
                // the radio horizon: a radar 100 ft high and a jammer at 25 000 ft see each other up to ~207 nm; a jammer with a short range cannot
                var shortEw = new StrikeEw { RangeNm = 60, ConeDeg = 30 };
                var hz = Station(new List<StrikeShip> { kidd }, hostiles, new GeoPt(41, 10), 0, 70, shortEw, p, null, out why);
                if (hz.HasValue || why != "horizon") return "horizon " + why;
                // SAM everywhere: a neighbour with a huge zone
                var huge = Tgt(102, 40.5, 10, 400);
                var inSam = Station(new List<StrikeShip> { kidd }, new List<StrikeShip> { kidd, huge }, new GeoPt(41, 10), 0, 55, ew, p, null, out why);
                if (inSam.HasValue || why != "in_sam") return "in_sam " + why;
                // two targets 40 nm apart across the axis: one 30 degree cone cannot hold them from far (the cone at 100 nm is 2 * 100 * tan(13) = 46 nm wide: it can), a 20 degree one cannot
                var a2 = Tgt(110, 41, 9.5, 25); var b2 = Tgt(111, 41, 10.5, 25);
                var coneOk = Station(new List<StrikeShip> { a2, b2 }, new List<StrikeShip> { a2, b2 }, new GeoPt(41, 10), 0, 55, ew, p, null, out why);
                if (!coneOk.HasValue) return "wide pair 30 " + why;
                var narrow = new StrikeEw { RangeNm = 200, ConeDeg = 20 };
                var coneBad = Station(new List<StrikeShip> { a2, b2 }, new List<StrikeShip> { a2, b2 }, new GeoPt(41, 10), 0, 55, narrow, p, null, out why);
                if (coneBad.HasValue ? false : why != "cone" && why != "horizon") return "wide pair 20 " + why;
                // the route
                var route = new List<GeoPt> { new GeoPt(38, 10), new GeoPt(38, 12) };
                if (!RouteClear(route, new List<StrikeShip> { Tgt(120, 41, 10, 25) }, 5)) return "route clear";
                if (RouteClear(route, new List<StrikeShip> { Tgt(121, 38.3, 11, 40) }, 5)) return "route through a zone";
            }
            if (all || part == "lat")
            {
                // T-348: jammer stations, cones and launch points on the beta globe (true nm, 1.5 %) and on the flat world at 0 / 45 / 60 / 70 deg
                string lw;
                foreach (double lat in new[] { 0.0, 45, 60, 70 })
                {
                    var aim0 = new GeoPt(lat, 10);
                    using (AswMath.WorldScope(true))
                    {
                        var kd = Tgt(200, lat, 10, 25);
                        var nb = Tgt(201, 0, 0, 50);
                        var nbAt = AswMath.Offset(aim0, 135, 30); nb.Lat = nbAt.Lat; nb.Lon = nbAt.Lon;
                        var jam = new StrikeEw { RangeNm = 300, ConeDeg = 30 };
                        var spt = Station(new List<StrikeShip> { kd }, new List<StrikeShip> { kd, nb }, aim0, 0, 55, jam, new StrikeParams(), null, out lw);
                        if (!spt.HasValue) return "lat " + lat + " globe station " + lw;
                        double dk = AswMath.GreatCircleNm(spt.Value, aim0), dn = AswMath.GreatCircleNm(spt.Value, nbAt);
                        if (dk < 55 * 0.985 || dn < (50 + 10) * 0.985 || dk < (25 + 10) * 0.985) return "lat " + lat + " globe station too close " + dk.ToString("0.0") + " " + dn.ToString("0.0");
                        if (dk > 80 + 80 + 1) return "lat " + lat + " globe station far " + dk;
                        var st = new GeoPt(lat, 10);
                        var amb = AswMath.Offset(st, 0, 100);
                        if (!InCone(st, amb, AswMath.Offset(st, 12, 100), 30, 2) || InCone(st, amb, AswMath.Offset(st, 16, 100), 30, 2)) return "lat " + lat + " globe cone";
                        if (!InCone(st, amb, AswMath.Offset(st, -12, 100), 30, 2) || InCone(st, amb, AswMath.Offset(st, -16, 100), 30, 2)) return "lat " + lat + " globe cone west";
                        foreach (double az in new[] { 45.0, 90, 180 })
                        {
                            var lp = StrikeGeoMath.LaunchPt(aim0, az, 50);
                            double gc = AswMath.GreatCircleNm(lp, aim0);
                            if (Math.Abs(gc / 50 - 1) > 0.015) return "lat " + lat + " globe launch range " + gc + " az " + az;
                        }
                    }
                    using (AswMath.WorldScope(false))
                    {
                        var lp = StrikeGeoMath.LaunchPt(aim0, 90, 50);
                        if (Math.Abs(lp.Lon - 10 + 50.0 / 60.0) > 1e-12) return "lat " + lat + " flat launch lon " + lp.Lon;
                        var st = new GeoPt(lat, 10);
                        var amb = AswMath.Offset(st, 0, 100);
                        if (!InCone(st, amb, AswMath.Offset(st, 12, 100), 30, 2) || InCone(st, amb, AswMath.Offset(st, 16, 100), 30, 2)) return "lat " + lat + " flat cone";
                    }
                }
                if (AswMath.WorldGlobe) return "lat: world flag leaked";
            }
            if (all || part == "timing")
            {
                p = new StrikeParams();
                var kidd = Tgt(100, 41, 10, 25, 100);
                var v = Var(0, 2500, 2900, kidd);
                var ac = new StrikeAircraft { Uid = 1, Lat = 38, Lon = 10, CruiseKn = 400, AltFt = 25000, CruiseAltFt = 25000 };
                ac.Missiles.Add(new StrikeMissile { Ammo = "usn_agm-84c", SpeedKn = 540, CruiseAltFt = 50 });
                var air = new Dictionary<int, StrikeAircraft> { { 1, ac } };
                double td = TDetect(v, new List<StrikeShip> { kidd }, air, p);
                if (td <= 0 || td > 2500) return "detect before the launch " + td;
                // a ship with a 30 nm radar horizon sees the 200 ft aircraft only 30 nm out, the 25 000 ft one at ~207 (limited by 150)
                double dn = DetNm(kidd, 200);
                if (Math.Abs(dn - 1.23 * (10 + Math.Sqrt(200))) > 0.01) return "DetNm low " + dn;
                if (Math.Abs(DetNm(kidd, 25000) - 150) > 1e-6) return "DetNm high " + DetNm(kidd, 25000);
                if (Math.Abs(StrikeGeoMath.HorizonNm(100, 50) - 1.23 * (10 + Math.Sqrt(50))) > 1e-9 || Math.Abs(StrikeGeoMath.HorizonNm(100, 50) - 21) > 0.5) return "missile horizon";
                var plan = PlanOne(Ea(5, 36, 10), new List<StrikeShip> { kidd }, new List<StrikeShip> { kidd }, v, null, 1000, p, null, air);
                if (plan.Why != "ok") return "plan " + plan.Why;
                if (Math.Abs(plan.TOn - (plan.TDetect - 60)) > 1e-6 || Math.Abs(plan.TStation - (plan.TOn - 30)) > 1e-6) return "TOn / TStation";
                double len = AswMath.DistNm(new GeoPt(36, 10), plan.StationPt);
                if (Math.Abs(plan.TDepart - (plan.TStation - len / 400 * 3600)) > 1) return "TDepart";
                if (Math.Abs(plan.TOff - (2900 + p.EwHoldS)) > 1e-6) return "TOff";
                // late by 200 s: shift 240 s, 4 minutes
                double nowLate = plan.TDepart + 200;
                var late = PlanOne(Ea(5, 36, 10), new List<StrikeShip> { kidd }, new List<StrikeShip> { kidd }, v, null, nowLate, p, null, air);
                if (Math.Abs(late.LateS - 200) > 1 || Math.Abs(late.ShiftS - 240) > 1e-6) return "late " + late.LateS + " " + late.ShiftS;
                var v2 = Var(0, 2500, 2900, kidd);
                PlanEw(v2, new List<StrikeAircraft> { Ea(5, 36, 10) }, new List<StrikeShip> { kidd }, new StrikeTargetSet { MainId = 100, Ships = { 100 } }, null, nowLate, p, null, air);
                if (!v2.Warnings.Any(w => w.StartsWith("ew_late:5:4", StringComparison.Ordinal))) return "warning ew_late: " + string.Join(";", v2.Warnings);
            }
            if (all || part == "decide")
            {
                p = new StrikeParams();
                var pl = new StrikeEwPlan { TDepart = 1000, TOn = 1200, TOff = 1500 };
                Func<string, double, double, double, bool, int, double, bool, bool, string> d = (st, now, dist, minGap, term, fl, last, sam, air) =>
                    Decide(new StrikeEwState { State = st }, pl, now, dist, minGap, 100, term, fl, last, sam, air, p);
                if (d("wait", 999, 100, 999, false, 0, -1, false, false) != "wait") return "wait early";
                if (d("wait", 1001, 100, 999, false, 0, -1, false, false) != "to_station") return "wait -> to_station";
                if (d("to_station", 1100, 5, 999, false, 0, -1, false, false) != "to_station" || d("to_station", 1100, 2, 999, false, 0, -1, false, false) != "on_station") return "to_station -> on_station";
                if (d("on_station", 1100, 0, 999, false, 0, -1, false, false) != "on_station") return "on_station early";
                if (d("on_station", 1201, 0, 999, false, 0, -1, false, false) != "jamming") return "on_station by time";
                if (d("on_station", 1100, 0, 105, false, 0, -1, false, false) != "jamming") return "on_station by distance (detection + 10 nm)";
                if (d("jamming", 1300, 0, 50, false, 2, -1, false, false) != "jamming") return "jamming holds while missiles fly";
                if (d("jamming", 1300, 0, 50, true, 0, 1290, false, false) != "jamming") return "off not before last + 15";
                if (d("jamming", 1305, 0, 50, true, 0, 1290, false, false) != "off") return "off at last + 15";
                if (d("jamming", 1300, 0, 50, false, 3, -1, false, false) != "jamming" || d("jamming", 1591, 0, 50, false, 3, -1, false, false) != "off") return "emergency off by TOff + LateSkipS";
                if (d("off", 1600, 0, 50, true, 0, 1290, false, false) != "back") return "off -> back";
                if (d("jamming", 1300, 0, 50, false, 2, -1, true, false) != "guard") return "guard over jamming";
                if (d("to_station", 1100, 50, 999, false, 0, -1, true, false) != "guard") return "guard on the way";
                if (d("jamming", 1300, 0, 50, false, 2, -1, false, true) != "retreat" || d("on_station", 1100, 0, 50, false, 0, -1, true, true) != "guard") return "retreat / priority";
                if (d("guard", 1300, 50, 50, false, 2, -1, false, false) != "to_station" || d("retreat", 1300, 50, 50, true, 0, 1200, false, false) != "back") return "guard end";
                if (d("no_station", 1300, 50, 50, false, 2, -1, true, true) != "no_station") return "no_station is final";
                if (d("on_station", 1300, 0, 50, true, 0, -1, false, false) != "back" || d("wait", 900, 100, 999, true, 0, -1, false, false) != "back" || d("to_station", 1100, 50, 999, true, 0, -1, false, false) != "back") return "strike over before the jam: back";
                if (d("jamming", 1300, 0, 50, true, 0, -1, false, false) != "off") return "jamming with nothing launched: off";
            }
            if (all || part == "return")
            {
                p = new StrikeParams();
                var pl = new StrikeEwPlan { TDepart = 1000, TOn = 1200, TOff = 1500 };
                Func<string, double, bool, bool, bool, string> dr = (st, clearS, sam, air, term) => Decide(new StrikeEwState { State = st }, pl, 1300, 50, 50, 100, term, term ? 0 : 2, -1, sam, air, p, clearS);
                if (p.EwReturnClearS != 60) return "default EwReturnClearS " + p.EwReturnClearS;
                if (dr("retreat", 10, false, false, false) != "retreat") return "retreat, the threat was gone 10 s ago: stays";
                if (dr("retreat", 59.9, false, false, false) != "retreat") return "retreat at 59.9 s: stays";
                if (dr("retreat", 60, false, false, false) != "to_station") return "retreat at 60 s: back to the station";
                if (dr("retreat", 600, false, false, false) != "to_station" || dr("retreat", double.PositiveInfinity, false, false, false) != "to_station") return "retreat after a long clear time";
                if (dr("retreat", 100, false, true, false) != "retreat") return "retreat while the threat stands";
                if (dr("guard", 0, false, false, false) != "to_station" || dr("guard", 5, false, false, false) != "to_station") return "a guard (SAM zone) is not an air threat: returns as before";
                if (dr("retreat", 10, true, false, false) != "guard") return "a SAM zone is the guard first";
                if (dr("retreat", 10, false, false, true) != "back" || dr("guard", 0, false, false, true) != "back") return "the strike is over: back";
                var p0 = new StrikeParams { EwReturnClearS = 0 };
                if (Decide(new StrikeEwState { State = "retreat" }, pl, 1300, 50, 50, 100, false, 2, -1, false, false, p0, 0) != "to_station") return "EwReturnClearS 0 = the behaviour of v1";
                if (Decide(new StrikeEwState { State = "retreat" }, pl, 1300, 50, 50, 100, false, 2, -1, false, false, p, 30) != "retreat" || Decide(new StrikeEwState { State = "retreat" }, pl, 1300, 50, 50, 100, false, 2, -1, false, false, new StrikeParams { EwReturnClearS = 120 }, 100) != "retreat") return "the limit is the parameter";
                if (Decide(new StrikeEwState { State = "retreat" }, pl, 1300, 50, 50, 100, false, 2, -1, false, false, p) != "to_station") return "the old call without the clear time is the old rule";
            }
            if (all || part == "plan")
            {
                p = new StrikeParams();
                var kidd = Tgt(100, 41, 10, 90, 100, new[] { "X" });
                var neigh = Tgt(101, 40.9, 10.3, 60, 100, new[] { "Ku", "X" });
                var far = Tgt(102, 41, 14, 40, 100, new[] { "X" });
                var ships = new List<StrikeShip> { kidd, neigh, far };
                var v = Var(0, 2500, 2900, kidd);
                var ac = new StrikeAircraft { Uid = 1, Lat = 38, Lon = 10, CruiseKn = 400, AltFt = 25000, CruiseAltFt = 25000 };
                ac.Missiles.Add(new StrikeMissile { Ammo = "usn_agm-84c", SpeedKn = 540, CruiseAltFt = 50 });
                var air = new Dictionary<int, StrikeAircraft> { { 1, ac } };
                var tgt = new StrikeTargetSet { MainId = 100, Ships = { 100 } };
                var ewAc = Ea(5, 36, 10);
                PlanEw(v, new List<StrikeAircraft> { ewAc }, ships, tgt, null, 1000, p, null, air);
                if (v.Ew.Count != 1 || v.Ew[0].Why != "ok") return "plan ew " + (v.Ew.Count == 0 ? "none" : v.Ew[0].Why);
                if (!v.Ew[0].CoverIds.Contains(100)) return "cover has the main target";
                if (!v.Warnings.Any(w => w == "ew_band:101:Ku")) return "band warning: " + string.Join(";", v.Warnings);
                var v0 = Var(0, 2500, 2900, kidd);
                PlanEw(v0, new List<StrikeAircraft>(), ships, tgt, null, 1000, p, null, air);
                if (v0.Ew.Count != 0 || v0.Warnings.Count != 0) return "no jammer, no plan";
                // 4 strikers' budget: the plan with 2 jammers and 12 targets is within 20 ms
                var many = new List<StrikeShip>();
                for (int i = 0; i < 12; i++) many.Add(Tgt(200 + i, 41 + 0.05 * (i % 4), 10 + 0.1 * i, 30, 100, new[] { "X" }));
                var vm = Var(0, 2500, 2900, many.ToArray());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                PlanEw(vm, new List<StrikeAircraft> { Ea(5, 36, 10), Ea(6, 36, 10.2) }, many, new StrikeTargetSet { MainId = 200 }, null, 1000, p, null, air);
                PlanEw(vm, new List<StrikeAircraft> { Ea(5, 36, 10), Ea(6, 36, 10.2) }, many, new StrikeTargetSet { MainId = 200 }, null, 1000, p, null, air);
                double ms = sw.Elapsed.TotalMilliseconds / 2;
                Console.WriteLine("strike ew plan ms=" + F1(ms));
                if (ms > 20) return "budget " + F1(ms);
            }
            if (all || part == "return-fighter")
            {
                p = new StrikeParams();
                var pl = new StrikeEwPlan { TDepart = 1000, TOn = 1200, TOff = 1500 };
                Func<string, double, bool, bool, string> df = (st, clearS, unsafeF, far) => Decide(new StrikeEwState { State = st }, pl, 1100, 50, 50, 100, false, 2, -1, false, false, p, clearS, unsafeF, far);
                if (df("on_station", double.PositiveInfinity, true, true) != "retreat") return "on_station + fighterUnsafe -> retreat";
                if (df("retreat", 61, false, false) != "retreat") return "retreat, 61 s, fighters near: stays";
                if (df("retreat", 61, false, true) != "to_station") return "retreat, 61 s, fighters far: back";
                if (df("retreat", 30, false, true) != "retreat") return "the clear time still counts";
                if (Decide(new StrikeEwState { State = "on_station" }, pl, 1100, 50, 50, 100, false, 2, -1, false, false, p) != df("on_station", double.PositiveInfinity, false, true)) return "the old call is unchanged";
            }
            if (all || part == "reserve")
            {
                p = new StrikeParams();
                var kidd = Tgt(100, 41, 10, 90, 100, new[] { "X" });
                var ships = new List<StrikeShip> { kidd };
                var v = Var(0, 2500, 2900, kidd);
                var ac = new StrikeAircraft { Uid = 1, Lat = 38, Lon = 10, CruiseKn = 400, AltFt = 25000, CruiseAltFt = 25000 };
                ac.Missiles.Add(new StrikeMissile { Ammo = "usn_agm-84c", SpeedKn = 540, CruiseAltFt = 50 });
                var air = new Dictionary<int, StrikeAircraft> { { 1, ac } };
                var tgt = new StrikeTargetSet { MainId = 100, Ships = { 100 } };
                PlanEw(v, new List<StrikeAircraft> { Ea(5, 36, 10), Ea(6, 36, 10.2) }, ships, tgt, null, 1000, p, null, air);
                if (v.Ew.Count != 2) return "two plans " + v.Ew.Count;
                var res = v.Ew.FirstOrDefault(e => e.Role == "fighters");
                if (res == null || v.Ew.Count(e => e.Role == "fighters") != 1) return "one reserve expected";
                if (res.Why != "ok" || res.CoverIds.Count == 0 || !res.CoverIds.Contains(100) || double.IsInfinity(res.TOn)) return "the reserve jams the main target too: " + res.Why + " " + res.CoverIds.Count;
                if (!v.Warnings.Contains("ew_fighter_reserve:" + I(res.Uid))) return "warning " + string.Join(";", v.Warnings);
                // one jammer: ships
                var v1 = Var(0, 2500, 2900, kidd);
                PlanEw(v1, new List<StrikeAircraft> { Ea(5, 36, 10) }, ships, tgt, null, 1000, p, null, air);
                if (v1.Ew.Count != 1 || v1.Ew[0].Role != "ships") return "one jammer = ships";
                // two targets 90 degrees apart: without one jammer one is uncovered -> both ships (v1)
                var t2 = Tgt(101, 40.6, 10.8, 60, 100, new[] { "X" });
                var ships2 = new List<StrikeShip> { kidd, t2 };
                var v2 = Var(0, 2500, 2900, kidd, t2);
                PlanEw(v2, new List<StrikeAircraft> { Ea(5, 36, 10), Ea(6, 36, 10.2) }, ships2, new StrikeTargetSet { MainId = 100, Ships = { 100, 101 } }, null, 1000, p, null, air);
                if (v2.Ew.Any(e => e.Role == "fighters") && !v2.Ew.All(e => e.Uncovered.Count == 0)) return "no reserve while a target would be uncovered";
                if (v2.Ew.Any(e => e.Role == "fighters") && v2.Ew.Count(e => e.Role == "ships") == 0) return "a reserve needs a ships jammer";
            }
            return null;
        }
    }
}
