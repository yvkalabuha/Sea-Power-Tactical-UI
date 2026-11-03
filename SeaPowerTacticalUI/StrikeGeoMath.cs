using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132: geometry of the strike (axis, lead, radio horizon, chord in a circle, launch point, wait point, risk in the SAM zones). Pure.
    internal static class StrikeGeoMath
    {
        /// <summary>1.23 * (sqrt(h1) + sqrt(h2)), nm, heights in feet (as CoverageMath).</summary>
        public static double HorizonNm(double h1Ft, double h2Ft) { return 1.23 * (Math.Sqrt(Math.Max(0, h1Ft)) + Math.Sqrt(Math.Max(0, h2Ft))); }

        /// <summary>Where the ship is after dtS on its course and speed.</summary>
        public static GeoPt Lead(StrikeShip s, double dtS)
        {
            var p = new GeoPt(s.Lat, s.Lon);
            return dtS <= 0 || s.SpeedKn <= 0 ? p : AswMath.Offset(p, s.CourseDeg, s.SpeedKn * dtS / 3600.0);
        }

        /// <summary>The axis of the attack: the bearing from the group to the target.</summary>
        public static double AxisDeg(GeoPt from, GeoPt target) { return AswMath.BearingDeg(from, target); }

        private static NmPt Local(GeoPt c, GeoPt p)
        {
            double d = AswMath.DistNm(c, p), b = AswMath.BearingDeg(c, p) * Math.PI / 180.0;
            return new NmPt(d * Math.Sin(b), d * Math.Cos(b));   // x east, y north
        }

        /// <summary>Parameters t1 &lt;= t2 in [0,1] of the part of the segment a-b inside the circle (c, r); false = outside.</summary>
        public static bool Clip(GeoPt a, GeoPt b, GeoPt c, double rNm, out double t1, out double t2)
        {
            t1 = t2 = 0;
            if (rNm <= 0) return false;
            var pa = Local(c, a); var pb = Local(c, b);
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
            double qa = dx * dx + dy * dy;
            if (qa < 1e-12) { return pa.X * pa.X + pa.Y * pa.Y <= rNm * rNm; }
            double qb = 2 * (pa.X * dx + pa.Y * dy), qc = pa.X * pa.X + pa.Y * pa.Y - rNm * rNm;
            double disc = qb * qb - 4 * qa * qc;
            if (disc <= 0) return false;
            double sq = Math.Sqrt(disc);
            double s1 = (-qb - sq) / (2 * qa), s2 = (-qb + sq) / (2 * qa);
            t1 = Math.Max(0, s1); t2 = Math.Min(1, s2);
            return t2 > t1;
        }

        /// <summary>The length of the segment a-b inside the circle (c, r), nm.</summary>
        public static double ChordNm(GeoPt a, GeoPt b, GeoPt c, double rNm)
        {
            double t1, t2;
            return Clip(a, b, c, rNm, out t1, out t2) ? (t2 - t1) * AswMath.DistNm(a, b) : 0;
        }

        /// <summary>The launch point: from the target (at the arrival time) back along the axis.</summary>
        public static GeoPt LaunchPt(GeoPt targetAtT, double axisDeg, double rangeNm) { return AswMath.Offset(targetAtT, axisDeg + 180, rangeNm); }

        /// <summary>
        /// The gate (phase C): on the axis behind the launch point GateMinBackNm away, over the sea; a land point goes farther back one nm at a time up to +30 nm (why land_shifted), no sea point = no_gate
        /// (the nominal point is returned; the execution of the aircraft does not start). isLand null = everything is sea.
        /// </summary>
        public static GeoPt GatePt(GeoPt launch, double axisDeg, Func<GeoPt, bool> isLand, StrikeParams p, out string why)
        {
            double back = (p ?? new StrikeParams()).GateMinBackNm;
            var nominal = AswMath.Offset(launch, axisDeg + 180, back);
            why = "ok";
            if (isLand == null || !isLand(nominal)) return nominal;
            for (double d = back + 1; d <= back + 30 + 1e-9; d += 1)
            {
                var pt = AswMath.Offset(launch, axisDeg + 180, d);
                if (!isLand(pt)) { why = "land_shifted"; return pt; }
            }
            why = "no_gate";
            return nominal;
        }

        /// <summary>
        /// The descent point (phase C): the first point of the route from -> gate -> launch from which any known hostile ship is inside the radio horizon of its mast
        /// (HorizonNm(RadarFt, cruise altitude) + GateMarginNm); the aircraft goes down there. Already inside at the start (the usual case at the cruise altitude): the point is the start,
        /// descentAtStart = true. Never inside before the launch point: the gate.
        /// </summary>
        public static GeoPt DescentPt(GeoPt from, GeoPt gate, GeoPt launch, IList<StrikeShip> hostiles, double cruiseAltFt, StrikeParams p, out bool descentAtStart)
        {
            double margin = (p ?? new StrikeParams()).GateMarginNm;
            descentAtStart = false;
            if (hostiles == null || hostiles.Count == 0) return gate;
            Func<GeoPt, bool> inside = pt =>
            {
                foreach (var s in hostiles)
                    if (AswMath.DistNm(pt, new GeoPt(s.Lat, s.Lon)) <= HorizonNm(s.RadarFt > 0 ? s.RadarFt : 100, cruiseAltFt) + margin) return true;
                return false;
            };
            if (inside(from)) { descentAtStart = true; return from; }
            double l1 = AswMath.DistNm(from, gate), l2 = AswMath.DistNm(gate, launch);
            for (double d = 1; d <= l1 + l2; d += 1)
            {
                var pt = d <= l1 ? AswMath.Offset(from, AswMath.BearingDeg(from, gate), d) : AswMath.Offset(gate, AswMath.BearingDeg(gate, launch), d - l1);
                if (inside(pt)) return pt;
            }
            return gate;
        }

        /// <summary>The point where the missile leaves and the first firstNm of the track toward the target (1 nm steps) are over the sea. isLand null = true.</summary>
        public static bool OverSea(GeoPt from, GeoPt to, double firstNm, Func<GeoPt, bool> isLand)
        {
            if (isLand == null) return true;
            if (isLand(from)) return false;
            double total = AswMath.DistNm(from, to), brg = AswMath.BearingDeg(from, to);
            for (double d = 1; d <= Math.Min(firstNm, total) + 1e-9; d += 1)
                if (isLand(AswMath.Offset(from, brg, d))) return false;
            return true;
        }

        /// <summary>The wait point: on the axis behind the ships, farther than every SAM range (from the centre of the ships) plus the standoff.</summary>
        public static GeoPt WaitPt(IList<StrikeShip> ships, GeoPt from, double axisDeg, double standoffNm)
        {
            if (ships == null || ships.Count == 0) return from;
            double la = 0, lo = 0;
            foreach (var s in ships) { la += s.Lat; lo += s.Lon; }
            var c = new GeoPt(la / ships.Count, lo / ships.Count);
            double d = 0;
            foreach (var s in ships) d = Math.Max(d, AswMath.DistNm(c, new GeoPt(s.Lat, s.Lon)) + Math.Max(0, s.SamMaxNm));
            return AswMath.Offset(c, axisDeg + 180, d + Math.Max(0, standoffNm));
        }

        /// <summary>The way along the axis (from 400 nm back to the launch point) inside the SAM zones of the ships: miles and seconds (union of the zones), and whether the aircraft is under the radio horizon of the ship that covers the launch point.</summary>
        public static void SamRisk(GeoPt launch, double axisDeg, double altFt, double kn, IList<StrikeShip> ships, out double inNm, out double inS, out bool underHorizon)
        {
            inNm = 0; inS = 0; underHorizon = false;
            if (ships == null || ships.Count == 0) return;
            const double Back = 400;
            var start = AswMath.Offset(launch, axisDeg + 180, Back);
            var iv = new List<double[]>();
            bool covered = false, allUnder = true;
            foreach (var s in ships)
            {
                if (s.SamMaxNm <= 0) continue;
                var sp = new GeoPt(s.Lat, s.Lon);
                double t1, t2;
                if (!Clip(start, launch, sp, s.SamMaxNm, out t1, out t2)) continue;
                iv.Add(new[] { t1, t2 });
                if (t2 >= 1 - 1e-9)
                {
                    covered = true;
                    double hz = HorizonNm(s.RadarFt > 0 ? s.RadarFt : 100, altFt);
                    if (AswMath.DistNm(launch, sp) <= hz) allUnder = false;
                }
            }
            if (iv.Count == 0) return;
            iv.Sort((x, y) => x[0].CompareTo(y[0]));
            double total = 0, cs = iv[0][0], ce = iv[0][1];
            for (int i = 1; i < iv.Count; i++)
            {
                if (iv[i][0] <= ce) ce = Math.Max(ce, iv[i][1]);
                else { total += ce - cs; cs = iv[i][0]; ce = iv[i][1]; }
            }
            total += ce - cs;
            inNm = total * Back;
            inS = kn > 0 ? inNm / kn * 3600.0 : 0;
            underHorizon = covered && allUnder;
        }

        public static string SelfTest()
        {
            if (Math.Abs(HorizonNm(100, 50) - 21.0) > 0.3 || Math.Abs(HorizonNm(100, 165) - 28.1) > 0.3 || Math.Abs(HorizonNm(100, 33) - 19.4) > 0.3) return "horizon";
            var s = new StrikeShip { Lat = 40, Lon = 10, CourseDeg = 90, SpeedKn = 20 };
            var l = Lead(s, 600);
            if (Math.Abs(AswMath.DistNm(new GeoPt(40, 10), l) - 3.333) > 0.05 || Math.Abs(AswMath.BearingDeg(new GeoPt(40, 10), l) - 90) > 0.5) return "lead";
            var still = Lead(new StrikeShip { Lat = 40, Lon = 10 }, 600);
            if (still.Lat != 40 || still.Lon != 10) return "lead still";
            var a = new GeoPt(40, 10);
            var tg = new GeoPt(41, 10);
            if (Math.Abs(AxisDeg(a, tg)) > 0.5 && Math.Abs(AxisDeg(a, tg) - 360) > 0.5) return "axis";
            // chord: a -> b is 30 nm long, the circle centre is the middle of the segment
            var b = AswMath.Offset(a, 0, 30);
            var mid = AswMath.Offset(a, 0, 15);
            if (Math.Abs(ChordNm(a, b, mid, 10) - 20) > 0.1) return "chord centre " + ChordNm(a, b, mid, 10);
            var side = AswMath.Offset(mid, 90, 10);
            if (ChordNm(a, b, side, 10) > 0.5) return "chord tangent " + ChordNm(a, b, side, 10);
            var far = AswMath.Offset(mid, 90, 20);
            if (ChordNm(a, b, far, 10) != 0) return "chord miss";
            if (Math.Abs(ChordNm(a, b, a, 10) - 10) > 0.1) return "chord from the centre";
            if (Math.Abs(ChordNm(a, b, mid, 100) - 30) > 0.1) return "chord inside";
            // launch point
            var lp = LaunchPt(new GeoPt(41, 10), 0, 60);
            if (Math.Abs(AswMath.DistNm(lp, new GeoPt(41, 10)) - 60) > 0.2 || lp.Lat >= 41) return "launch point";
            // wait point: outside every SAM range plus the standoff
            var ships = new List<StrikeShip> { new StrikeShip { Lat = 41, Lon = 10, SamMaxNm = 90 }, new StrikeShip { Lat = 41, Lon = 10.2, SamMaxNm = 30 } };
            var wp = WaitPt(ships, a, 0, 10);
            foreach (var sh in ships) if (AswMath.DistNm(wp, new GeoPt(sh.Lat, sh.Lon)) < sh.SamMaxNm + 10 - 0.1) return "wait point inside";
            var wps = WaitPt(new List<StrikeShip>(), a, 0, 10);
            if (wps.Lat != a.Lat) return "wait point no ships";
            // risk: launch point 40 nm from a ship with a 90 nm SAM -> a part of the way is inside
            var tship = new StrikeShip { Lat = 41, Lon = 10, SamMaxNm = 90, RadarFt = 100 };
            var lpt = LaunchPt(new GeoPt(41, 10), 0, 60);
            double inNm, inS; bool under;
            SamRisk(lpt, 0, 50, 480, new List<StrikeShip> { tship }, out inNm, out inS, out under);
            if (Math.Abs(inNm - 30) > 0.5 || Math.Abs(inS - 30.0 / 480 * 3600) > 5) return "risk inside " + inNm + " " + inS;
            if (!under) return "60 nm from the ship: the radio horizon is 21 nm, the aircraft is under it";
            var lpt2 = LaunchPt(new GeoPt(41, 10), 0, 15);
            SamRisk(lpt2, 0, 50, 480, new List<StrikeShip> { tship }, out inNm, out inS, out under);
            if (Math.Abs(inNm - 75) > 0.5) return "risk close " + inNm;
            if (under) return "15 nm from the ship is above the horizon";
            SamRisk(lpt, 0, 50, 480, new List<StrikeShip> { new StrikeShip { Lat = 41, Lon = 10, SamMaxNm = 30, RadarFt = 100 } }, out inNm, out inS, out under);
            if (inNm != 0 || under) return "risk outside the zone";
            // phase C: the gate, the descent point, the sea check
            var pp = new StrikeParams();
            string gw;
            var gt = GatePt(lpt, 0, null, pp, out gw);
            if (gw != "ok" || Math.Abs(AswMath.DistNm(gt, lpt) - 15) > 0.2 || gt.Lat >= lpt.Lat) return "gate " + gw;
            var gt1 = GatePt(lpt, 0, pt => AswMath.DistNm(pt, gt) < 3, pp, out gw);
            if (gw != "land_shifted" || AswMath.DistNm(gt1, lpt) < 17.9) return "gate shifted " + gw + " " + AswMath.DistNm(gt1, lpt);
            GatePt(lpt, 0, pt => AswMath.DistNm(pt, lpt) > 10, pp, out gw);
            if (gw != "no_gate") return "no gate " + gw;
            var tgt = new GeoPt(41, 10);
            var hostile = new List<StrikeShip> { new StrikeShip { Lat = 41, Lon = 10, RadarFt = 100, SamMaxNm = 90 } };
            var start = AswMath.Offset(tgt, 180, 120);
            var lp2 = LaunchPt(tgt, 0, 60); var gate2 = AswMath.Offset(lp2, 180, 15);
            bool atStart;
            var dp = DescentPt(start, gate2, lp2, hostile, 5000, pp, out atStart);
            double want = HorizonNm(100, 5000) + 5;
            if (atStart || Math.Abs(AswMath.DistNm(dp, tgt) - want) > 1.01) return "descent point " + AswMath.DistNm(dp, tgt) + " want " + want;
            var near = AswMath.Offset(tgt, 180, 30);
            var dp2 = DescentPt(near, AswMath.Offset(lp2, 180, 15), lp2, hostile, 20000, pp, out atStart);
            if (!atStart || AswMath.DistNm(dp2, near) > 1e-6) return "descent at start";
            // a neighbour off to the side, closer to the route: the descent is earlier (by the neighbour)
            var sidePos = AswMath.Offset(AswMath.Offset(start, 0, 10), 90, 104);
            var sideShip = new StrikeShip { Lat = sidePos.Lat, Lon = sidePos.Lon, RadarFt = 100 };
            var both = new List<StrikeShip>(hostile) { sideShip };
            var dp3 = DescentPt(start, gate2, lp2, both, 5000, pp, out atStart);
            if (atStart || AswMath.DistNm(dp3, start) >= AswMath.DistNm(dp, start) - 5) return "descent by the neighbour " + AswMath.DistNm(dp3, start) + " vs " + AswMath.DistNm(dp, start);
            // the sea check
            var sea0 = new GeoPt(40, 10); var sea1 = new GeoPt(41, 10);
            if (!OverSea(sea0, sea1, 5, null) || !OverSea(sea0, sea1, 5, pt => false)) return "over sea";
            if (OverSea(sea0, sea1, 5, pt => AswMath.DistNm(pt, AswMath.Offset(sea0, 0, 3)) < 0.6)) return "land on the third mile";
            if (!OverSea(sea0, sea1, 5, pt => AswMath.DistNm(pt, AswMath.Offset(sea0, 0, 20)) < 0.6)) return "land beyond the checked miles";
            if (OverSea(sea0, sea1, 5, pt => AswMath.DistNm(pt, sea0) < 0.5)) return "land at the launch point";
            return null;
        }
    }
}
