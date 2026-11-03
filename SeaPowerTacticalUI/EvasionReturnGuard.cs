using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-278 / ADR-0111: the way back to the player's route after an own torpedo evasion is checked against land and shoals with the same ray
    // as the evasion course (ADR-0108: band +-0.4 nm, depth >= 15 m). A blocked segment gets 1-2 detour points. Pure: no Unity, no reflection.
    // The terrain function is given by the caller (main thread only, ADR-0092).

    internal sealed class EvReturnPlan
    {
        public string Why = "clear";                      // clear | detour | best_effort
        public List<GeoPt> Points = new List<GeoPt>();    // 0..2 detour points, in order
        public double BlockNm = double.PositiveInfinity;  // of the direct segment
        public double BlockAltM = double.NaN;
        public double DirectNm, PathNm;                   // direct length, length via the detour
        public int Probes;
        public bool Budget;                               // the probe budget ran out
    }

    /// <summary>One running detour of one ship.</summary>
    internal sealed class EvDetour
    {
        public List<double[]> Points = new List<double[]>();        // {lat, lon} of the mod's detour points still ahead
        public List<double[]> PlayerRoute = new List<double[]>();   // the player's points as restored (placed after the detour)
        public double PlacedS, NextCheckS, NextTickS;
        public double TimeoutS = EvasionReturnGuard.TimeoutS;   // T-281: set from the path length and the ship speed at placement   // NextTickS: the 1 s pace of the game wiring
        public int Uid;
    }

    internal static class EvasionReturnGuard
    {
        public const double ReturnCheckNm = 20, NearWpNm = 0.3, SameWpNm = 0.2, PassNm = 0.3;
        public const double RecheckS = 15, TimeoutS = 900, TimeoutMaxS = 7200, TimeoutSlackS = 120, TimeoutMinKn = 5, TimeoutFactor = 1.5;
        public const int MaxProbes = 20000, CoarseStride = 3;
        public static readonly double[] OffsetsDeg = { 15, 30, 45, 60, 75, 90 };   // both sides
        public static readonly double[] LegsNm = { 1.5, 3, 5, 8 };
        private static readonly double[] Legs2Nm = { 3, 5, 8 };

        /// <summary>T-281: path / max(speed, 5 kn) x 1.5 + slack, at least TimeoutS, at most TimeoutMaxS (7200 s).</summary>
        public static double TimeoutFor(double pathNm, double speedKn)
        {
            double kn = double.IsNaN(speedKn) ? TimeoutMinKn : Math.Max(speedKn, TimeoutMinKn);
            double t = pathNm / kn * 3600.0 * TimeoutFactor + TimeoutSlackS;
            if (double.IsNaN(t) || t < TimeoutS) t = TimeoutS;
            return Math.Min(t, TimeoutMaxS);
        }

        private static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        /// <summary>
        /// Distance along a-&gt;b (capped at ReturnCheckNm) to the first bad probe; +inf = clear. Skips the first StepNm (under the ship).
        /// stride &gt; 1 probes only every stride-th step (and the last one): a subset of the full probes; onlyRest probes the other steps (after a clear coarse pass: together a full check).
        /// </summary>
        public static double SegmentBlockNm(GeoPt a, GeoPt b, Func<GeoPt, double> heightM, out double altM, out int probes, int stride = 1, bool onlyRest = false)
        {
            altM = double.NaN; probes = 0;
            double len = AswMath.DistNm(a, b);
            double step = EvasionCourseGuard.StepNm;
            if (len <= step + 1e-9) return double.PositiveInfinity;
            double brg = AswMath.BearingDeg(a, b);
            double cap = Math.Min(len, ReturnCheckNm);
            var p = EvasionCourseGuard.Params();
            GeoPt a1 = AswMath.Offset(a, brg, step), b1 = AswMath.Offset(a, brg, cap);
            List<GeoPt> pts = SarRouteMath.LinePattern(a1, b1, p);
            const int perStep = 5;
            int steps = Math.Max(1, pts.Count / perStep - 1);
            double span = cap - step;
            if (stride < 1) stride = 1;
            for (int s = 0; s <= steps; s++)
            {
                bool coarse = s % stride == 0 || s == steps;
                if (onlyRest ? coarse : !coarse) continue;
                for (int k = 0; k < perStep; k++)
                {
                    double h = heightM(pts[s * perStep + k]);
                    probes++;
                    if (!SarRouteMath.Passable(h, p.MinDepthM)) { altM = h; return step + span * s / steps; }
                }
            }
            return double.PositiveInfinity;
        }

        /// <summary>The first player point farther than NearWpNm (index into route), -1 = none.</summary>
        public static int TargetIndex(GeoPt own, IList<double[]> route)
        {
            if (route == null) return -1;
            for (int i = 0; i < route.Count; i++)
                if (AswMath.DistNm(own, new GeoPt(route[i][0], route[i][1])) > NearWpNm) return i;
            return -1;
        }

        private sealed class Counter { public int Probes, Max; public bool Over { get { return Probes >= Max; } } }

        // coarse pass = a subset of the full probes: blocked here means blocked; the cheap way to drop most candidates
        private static bool Coarse(GeoPt a, GeoPt b, Func<GeoPt, double> h, Counter c)
        {
            double alt; int pr;
            double v = SegmentBlockNm(a, b, h, out alt, out pr, CoarseStride);
            c.Probes += pr;
            return double.IsPositiveInfinity(v);
        }

        // the steps the coarse pass skipped: after a clear Coarse of the same segment this completes the full check
        private static bool Rest(GeoPt a, GeoPt b, Func<GeoPt, double> h, Counter c)
        {
            double alt; int pr;
            double v = SegmentBlockNm(a, b, h, out alt, out pr, CoarseStride, true);
            c.Probes += pr;
            return double.IsPositiveInfinity(v);
        }

        private sealed class Cand { public GeoPt D; public double Leg, Total; }

        private static List<Cand> Candidates(GeoPt from, GeoPt target, double[] legs)
        {
            var list = new List<Cand>();
            double brg = AswMath.BearingDeg(from, target);
            foreach (double leg in legs)
                foreach (double off in OffsetsDeg)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        GeoPt d = AswMath.Offset(from, brg + sgn * off, leg);
                        list.Add(new Cand { D = d, Leg = leg, Total = leg + AswMath.DistNm(d, target) });
                    }
            list.Sort((x, y) => x.Total.CompareTo(y.Total));
            return list;
        }

        /// <summary>Direct segment clear -&gt; clear; else the shortest 1-point detour (own-&gt;D and D-&gt;T clear), else 2 points; none or budget -&gt; best_effort.</summary>
        public static EvReturnPlan Plan(GeoPt own, GeoPt target, Func<GeoPt, double> heightM, int maxProbes = MaxProbes)
        {
            var res = new EvReturnPlan { DirectNm = AswMath.DistNm(own, target) };
            res.PathNm = res.DirectNm;
            var cnt = new Counter { Max = maxProbes };
            double alt; int pr;
            res.BlockNm = SegmentBlockNm(own, target, heightM, out alt, out pr);
            cnt.Probes += pr; res.BlockAltM = alt;
            if (double.IsPositiveInfinity(res.BlockNm)) { res.Probes = cnt.Probes; return res; }
            res.Why = "best_effort";
            // a target on land: no detour can help
            if (!SarRouteMath.Passable(heightM(target), EvasionCourseGuard.MinDepthM)) { cnt.Probes++; res.Probes = cnt.Probes; return res; }
            cnt.Probes++;

            // one point: the shortest D with both legs clear (both legs coarse first, then both in full)
            var one = Candidates(own, target, LegsNm);
            var coarseFirst = new List<Cand>();   // D with a coarse-clear own->D leg (the 2-point search starts there)
            foreach (var c in one)
            {
                if (cnt.Over) { res.Budget = true; res.Probes = cnt.Probes; return res; }
                if (!Coarse(own, c.D, heightM, cnt)) continue;
                coarseFirst.Add(c);
                if (!Coarse(c.D, target, heightM, cnt)) continue;
                if (!Rest(own, c.D, heightM, cnt) || !Rest(c.D, target, heightM, cnt)) continue;
                res.Why = "detour"; res.Points.Add(c.D); res.PathNm = c.Total; res.Probes = cnt.Probes;
                return res;
            }

            // two points: for every direction (offset, side) the longest coarse-clear first leg, the shortest total first
            var firsts = new List<Cand>();
            double brg0 = AswMath.BearingDeg(own, target);
            var byDir = new Dictionary<long, Cand>();
            foreach (var c in coarseFirst)
            {
                long key = (long)Math.Round(AswMath.Norm360(AswMath.BearingDeg(own, c.D) - brg0) * 10);
                Cand old;
                if (!byDir.TryGetValue(key, out old) || c.Leg > old.Leg) byDir[key] = c;
            }
            foreach (var kv in byDir) firsts.Add(kv.Value);
            firsts.Sort((x, y) => x.Total.CompareTo(y.Total));
            foreach (var d1 in firsts)
            {
                if (cnt.Over) { res.Budget = true; res.Probes = cnt.Probes; return res; }
                if (!Rest(own, d1.D, heightM, cnt)) continue;
                foreach (var c2 in Candidates(d1.D, target, Legs2Nm))
                {
                    if (cnt.Over) { res.Budget = true; res.Probes = cnt.Probes; return res; }
                    if (!Coarse(d1.D, c2.D, heightM, cnt) || !Coarse(c2.D, target, heightM, cnt)) continue;
                    if (!Rest(d1.D, c2.D, heightM, cnt) || !Rest(c2.D, target, heightM, cnt)) continue;
                    res.Why = "detour"; res.Points.Add(d1.D); res.Points.Add(c2.D);
                    res.PathNm = d1.Leg + AswMath.DistNm(d1.D, c2.D) + AswMath.DistNm(c2.D, target);
                    res.Probes = cnt.Probes;
                    return res;
                }
            }
            res.Probes = cnt.Probes;
            return res;
        }

        private static bool Same(double[] w, double[] p) { return AswMath.DistNm(new GeoPt(w[0], w[1]), new GeoPt(p[0], p[1])) <= SameWpNm; }

        /// <summary>The leading detour points removed from the waypoint list (each wp within SameWpNm of the next detour point, in order).</summary>
        public static List<double[]> StripDetour(IList<double[]> wps, EvDetour d)
        {
            var res = new List<double[]>();
            if (wps == null) return res;
            int i = 0, j = 0;
            while (i < wps.Count && j < d.Points.Count)
            {
                int hit = -1;
                for (int jj = j; jj < d.Points.Count; jj++) if (Same(wps[i], d.Points[jj])) { hit = jj; break; }
                if (hit < 0) break;
                i++; j = hit + 1;
            }
            for (; i < wps.Count; i++) res.Add(wps[i]);
            return res;
        }

        /// <summary>
        /// One tick. "keep" | "advance" (the game dropped the first detour point; it is popped here) | "restack" (the first detour point is reached but still listed,
        /// more detour points follow: place the rest again) | "shortcut" (the direct way to the player's route is clear: place the player route) |
        /// "shortcut:passed" (the last detour point is reached but still listed: place the player route) | "end:passed" | "end:player" | "end:lost" | "end:timeout".
        /// </summary>
        public static string Step(EvDetour d, GeoPt own, IList<double[]> wps, double nowS, Func<GeoPt, GeoPt, bool> directClear)
        {
            if (wps == null || wps.Count == 0) return "end:lost";
            if (nowS - d.PlacedS > d.TimeoutS) return "end:timeout";
            double[] first = wps[0];
            if (d.Points.Count > 0 && Same(first, d.Points[0]))
            {
                if (AswMath.DistNm(own, new GeoPt(d.Points[0][0], d.Points[0][1])) <= PassNm)
                    return d.Points.Count > 1 ? "restack" : "shortcut:passed";
                if (nowS >= d.NextCheckS)
                {
                    d.NextCheckS = nowS + RecheckS;
                    int ti = TargetIndex(own, d.PlayerRoute);
                    if (ti < 0) return "shortcut:passed";
                    var t = d.PlayerRoute[ti];
                    if (directClear != null && directClear(own, new GeoPt(t[0], t[1]))) return "shortcut";
                }
                return "keep";
            }
            if (d.Points.Count > 1 && Same(first, d.Points[1])) { d.Points.RemoveAt(0); return "advance"; }
            foreach (var p in d.PlayerRoute) if (Same(first, p)) return "end:passed";
            return "end:player";
        }

        // --- self test ---------------------------------------------------------------------------------------------------

        private static GeoPt G(double xNm, double yNm) { return new GeoPt(yNm / 60.0, xNm / 60.0); }
        private static double[] W(double xNm, double yNm) { return new[] { yNm / 60.0, xNm / 60.0 }; }

        private static Func<GeoPt, double> Island(double cx, double cy, double r)
        {
            return g => { double x = g.Lon * 60 - cx, y = g.Lat * 60 - cy; return Math.Sqrt(x * x + y * y) <= r ? 5 : -200; };
        }

        public static string SelfTest()
        {
            double alt; int pr;
            Func<GeoPt, double> sea = g => -200;
            // open sea: a 4 nm segment is clear with ~100 probes
            double b = SegmentBlockNm(G(0, 0), G(0, 4), sea, out alt, out pr);
            if (!double.IsPositiveInfinity(b) || pr < 95 || pr > 105 || !double.IsNaN(alt)) return "open sea: expected clear with ~100 probes, got " + F(b) + " / " + pr;
            // an island (r = 0.5) in the middle of a 4 nm segment: block = about 2 - 0.5 (the margin is across the line only)
            b = SegmentBlockNm(G(0, 0), G(0, 4), Island(0, 2, 0.5), out alt, out pr);
            if (Math.Abs(b - 1.55) > 0.2 || !(alt > 0)) return "island: block " + F(b) + " alt " + F(alt);
            // land under the ship does not block
            Func<GeoPt, double> under = g => { double x = g.Lon * 60, y = g.Lat * 60; return Math.Sqrt(x * x + y * y) < 0.1 ? 5 : -200; };
            if (!double.IsPositiveInfinity(SegmentBlockNm(G(0, 0), G(4, 0), under, out alt, out pr))) return "land under the ship must not block";
            // 40 nm is cut to ReturnCheckNm
            SegmentBlockNm(G(0, 0), G(0, 40), sea, out alt, out pr);
            if (pr > 20 * 25 + 10 || pr < 400) return "40 nm segment: probes " + pr;
            if (!double.IsPositiveInfinity(SegmentBlockNm(G(0, 0), G(0, 40), Island(0, 30, 1), out alt, out pr))) return "land beyond the check range must not block";
            // stride 3 is a subset: a coarse block is not shorter than the full one; a thin wall can slip through the coarse pass only
            int prFull, prCoarse;
            double full = SegmentBlockNm(G(0, 0), G(0, 6), Island(0, 3, 0.3), out alt, out prFull);
            double coarse = SegmentBlockNm(G(0, 0), G(0, 6), Island(0, 3, 0.3), out alt, out prCoarse, 3);
            if (double.IsPositiveInfinity(full)) return "stride: the full pass must see the island";
            if (!double.IsPositiveInfinity(coarse) && coarse < full - 1e-9) return "stride: coarse block " + F(coarse) + " is shorter than the full one " + F(full);
            SegmentBlockNm(G(0, 0), G(0, 6), sea, out alt, out prFull);
            SegmentBlockNm(G(0, 0), G(0, 6), sea, out alt, out prCoarse, 3);
            if (prCoarse * 2 > prFull) return "stride: coarse probes " + prCoarse + " vs full " + prFull;

            // TargetIndex
            var route = new List<double[]> { W(0, 0.1), W(0, 0.2), W(0, 3), W(0, 6) };
            if (TargetIndex(G(0, 0), route) != 2) return "TargetIndex: near points must be skipped";
            if (TargetIndex(G(0, 0), new List<double[]>()) != -1 || TargetIndex(G(0, 0), new List<double[]> { W(0, 0.1) }) != -1) return "TargetIndex: none expected";

            string e = PlanTests();
            if (e != null) return e;
            return StepTests();
        }

        private static string PlanTests()
        {
            var own = G(0, 0);
            var tgt = G(0, 6);
            // a) clear
            var p = Plan(own, tgt, g => -200);
            if (p.Why != "clear" || p.Points.Count != 0 || !double.IsPositiveInfinity(p.BlockNm)) return "plan a: clear expected, got " + p.Why;
            // b) an island on the way: one point, both legs clear, the shortest among the candidates
            var isl = Island(0, 3, 0.5);
            p = Plan(own, tgt, isl);
            if (p.Why != "detour" || p.Points.Count != 1) return "plan b: one detour point expected, got " + p.Why + " " + p.Points.Count + " budget " + p.Budget + " probes " + p.Probes;
            if (double.IsPositiveInfinity(p.BlockNm) || !(p.BlockAltM > 0)) return "plan b: block data missing";
            double alt; int pr;
            if (!double.IsPositiveInfinity(SegmentBlockNm(own, p.Points[0], isl, out alt, out pr)) || !double.IsPositiveInfinity(SegmentBlockNm(p.Points[0], tgt, isl, out alt, out pr))) return "plan b: a leg is not clear";
            double best = double.PositiveInfinity;
            double brg = AswMath.BearingDeg(own, tgt);
            foreach (double leg in LegsNm)
                foreach (double off in OffsetsDeg)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        GeoPt d = AswMath.Offset(own, brg + sgn * off, leg);
                        double total = leg + AswMath.DistNm(d, tgt);
                        if (total < best && double.IsPositiveInfinity(SegmentBlockNm(own, d, isl, out alt, out pr)) && double.IsPositiveInfinity(SegmentBlockNm(d, tgt, isl, out alt, out pr))) best = total;
                    }
            if (Math.Abs(best - p.PathNm) > 1e-6) return "plan b: path " + F(p.PathNm) + " is not the shortest " + F(best);
            if (p.PathNm < p.DirectNm) return "plan b: path shorter than direct";
            // c) a wall x in [-30, 8], y in [2, 2.5]: the way round its east end needs two legs
            Func<GeoPt, double> wall = g => { double x = g.Lon * 60, y = g.Lat * 60; return x >= -30 && x <= 8 && y >= 2 && y <= 2.5 ? 5 : -200; };
            p = Plan(own, tgt, wall);
            if (p.Why != "detour" || p.Points.Count != 2) return "plan c: two detour points expected, got " + p.Why + " " + p.Points.Count + " budget " + p.Budget + " probes " + p.Probes;
            if (!double.IsPositiveInfinity(SegmentBlockNm(own, p.Points[0], wall, out alt, out pr)) || !double.IsPositiveInfinity(SegmentBlockNm(p.Points[0], p.Points[1], wall, out alt, out pr))
                || !double.IsPositiveInfinity(SegmentBlockNm(p.Points[1], tgt, wall, out alt, out pr))) return "plan c: a leg is not clear";
            // d) a wall across the whole width: best effort
            Func<GeoPt, double> full = g => { double y = g.Lat * 60; return y >= 2 && y <= 2.5 ? 5 : -200; };
            p = Plan(own, tgt, full);
            if (p.Why != "best_effort" || p.Points.Count != 0 || p.Budget) return "plan d: best_effort expected, got " + p.Why + " budget " + p.Budget;
            // e) a probe budget of 50
            p = Plan(own, G(0, 12), Island(0, 6, 0.5), 50);
            if (p.Why != "best_effort" || !p.Budget) return "plan e: budget stop expected, got " + p.Why + " budget " + p.Budget;
            // f) the target itself on land: best effort at once, without the search
            p = Plan(own, tgt, Island(0, 6, 0.8));
            if (p.Why != "best_effort" || p.Probes > 1000) return "plan f: target on land, got " + p.Why + " probes " + p.Probes;
            return null;
        }

        private static string StepTests()
        {
            var own = G(0, 0);
            Func<GeoPt, GeoPt, bool> never = (a, b) => false;
            Func<GeoPt, GeoPt, bool> always = (a, b) => true;
            Func<EvDetour> make = () => new EvDetour
            {
                Points = new List<double[]> { W(3, 1), W(3, 4) }, PlayerRoute = new List<double[]> { W(0, 6), W(0, 9) }, PlacedS = 100, NextCheckS = 115
            };
            // keep: the first waypoint is the first detour point
            var d = make();
            string s = Step(d, own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6), W(0, 9) }, 105, always);
            if (s != "keep") return "step keep: " + s;
            // shortcut: the direct way is clear at the re-check time, not before
            s = Step(d, own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6), W(0, 9) }, 116, always);
            if (s != "shortcut") return "step shortcut: " + s;
            if (d.NextCheckS < 116 + RecheckS - 1e-9) return "step shortcut: next check not moved";
            d = make();
            if (Step(d, own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6) }, 116, never) != "keep") return "step: blocked direct must keep";
            // advance: the game dropped the first detour point
            d = make();
            s = Step(d, G(3, 0.5), new List<double[]> { W(3, 4), W(0, 6), W(0, 9) }, 110, never);
            if (s != "advance" || d.Points.Count != 1) return "step advance: " + s + " " + d.Points.Count;
            // passed: the detour points are gone, the player's route is first
            d = make();
            s = Step(d, G(3, 4), new List<double[]> { W(0, 6), W(0, 9) }, 120, never);
            if (s != "end:passed") return "step passed: " + s;
            s = Step(make(), G(3, 4), new List<double[]> { W(0, 9) }, 120, never);
            if (s != "end:passed") return "step passed (second player point): " + s;
            // player: a foreign first point
            s = Step(make(), own, new List<double[]> { W(-5, -5), W(0, 6) }, 120, never);
            if (s != "end:player") return "step player: " + s;
            // lost: no waypoints
            s = Step(make(), own, new List<double[]>(), 120, never);
            if (s != "end:lost") return "step lost: " + s;
            // timeout
            s = Step(make(), own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6) }, 100 + TimeoutS + 1, never);
            if (s != "end:timeout") return "step timeout: " + s;
            // T-281: slow ship, long path: 3 nm at 10 kn = 1620 + 120 s, no timeout at 1000 s; cap; floor
            if (Math.Abs(TimeoutFor(3, 10) - 1740) > 1e-6 || TimeoutFor(1, 2) != TimeoutFor(1, 5) || TimeoutFor(500, 10) != TimeoutMaxS || TimeoutFor(0.1, 20) != TimeoutS) return "timeout calc";
            var slow = make(); slow.TimeoutS = 1740;
            s = Step(slow, own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6) }, 100 + 1000, never);
            if (s == "end:timeout") return "step timeout: slow ship ended at 1000 s";
            s = Step(slow, own, new List<double[]> { W(3, 1), W(3, 4), W(0, 6) }, 100 + 1741, never);
            if (s != "end:timeout") return "step timeout: " + s;
            // reached but still listed: restack with more points, shortcut:passed with the last one
            s = Step(make(), G(3, 0.9), new List<double[]> { W(3, 1), W(3, 4), W(0, 6) }, 105, never);
            if (s != "restack") return "step restack: " + s;
            var last = new EvDetour { Points = new List<double[]> { W(3, 4) }, PlayerRoute = new List<double[]> { W(0, 6) }, PlacedS = 100, NextCheckS = 115 };
            s = Step(last, G(3, 3.9), new List<double[]> { W(3, 4), W(0, 6) }, 105, never);
            if (s != "shortcut:passed") return "step shortcut:passed: " + s;

            // StripDetour
            d = make();
            var st = StripDetour(new List<double[]> { W(3, 1), W(3, 4), W(0, 6), W(0, 9) }, d);
            if (st.Count != 2 || AswMath.DistNm(new GeoPt(st[0][0], st[0][1]), G(0, 6)) > 1e-6) return "strip: leading detour points must go, got " + st.Count;
            st = StripDetour(new List<double[]> { W(3, 4), W(0, 6) }, d);
            if (st.Count != 1) return "strip: one dropped detour point, got " + st.Count;
            st = StripDetour(new List<double[]> { W(0, 6), W(3.05, 4.05) }, d);
            if (st.Count != 2) return "strip: a point near a detour point that is not first must stay, got " + st.Count;
            st = StripDetour(new List<double[]> { W(0, 6) }, d);
            if (st.Count != 1) return "strip: nothing to strip";
            return null;
        }
    }
}
