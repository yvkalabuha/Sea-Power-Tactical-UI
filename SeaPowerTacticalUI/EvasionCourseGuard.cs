using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-272 / ADR-0108: the evasion course is checked against land and shoals (pure, no Unity, no reflection).
    // The terrain function is given by the caller (main thread only, ADR-0092): TerrainHeightCache over TerrainSampler(false).

    /// <summary>T-272: memory of the terrain guard of one evasion run.</summary>
    internal sealed class EvGuardState
    {
        public double? GuardDeg;                 // the course chosen instead of the wanted one (null = the wanted one is clear)
        public double LastWantDeg = double.NaN;  // the wanted course of the last check
        public double NextCheckS = double.NegativeInfinity;
        public string LastWhy = "clear";
        public EvGuardPick Last;                 // the last result (returned between the checks)
    }

    internal sealed class EvGuardPick
    {
        public double CourseDeg;
        public string Why = "clear";    // clear | nearest | beam | best_effort
        public double BlockNm = double.PositiveInfinity;   // of the WANTED course (+inf = clear)
        public double BlockAltM = double.NaN;              // height of its first bad probe, NaN = none
        public double PickBlockNm = double.PositiveInfinity;   // of the chosen course
        public double RelDeg;           // |Diff(course, torpedo bearing)|
        public int Probes;              // terrain samples asked in this check
        public bool Checked;            // false = cached result, no terrain read this tick
        public bool Changed;            // course or Why differs from the previous result (log once)
    }

    internal static class EvasionCourseGuard
    {
        public const double CheckNm = 5, StepNm = 0.2, ShoreMarginNm = 0.4, MinDepthM = 15;
        public const int CoarseStride = 3;   // T-278 (N3): the first pass of Steer probes every 3rd step
        public const double StepDeg = 10, Tier1RelDeg = 120, Tier2RelDeg = 90, HystDeg = 10, RecheckS = 5, WantMoveDeg = 3;

        public static RouteParams Params()
        {
            return new RouteParams { MinDepthM = MinDepthM, ShoreMarginNm = ShoreMarginNm, StepNm = StepNm };
        }

        /// <summary>Distance along the course to the first probe that is land or shallower than MinDepthM; +inf = clear. altM = its height (NaN when clear).</summary>
        public static double BlockNm(GeoPt own, double courseDeg, Func<GeoPt, double> heightM, out double altM, out int probes, int stride = 1, bool onlyRest = false)
        {
            altM = double.NaN; probes = 0;
            var p = Params();
            GeoPt a = AswMath.Offset(own, courseDeg, StepNm), b = AswMath.Offset(own, courseDeg, CheckNm);
            List<GeoPt> pts = SarRouteMath.LinePattern(a, b, p);
            int perStep = 5;   // the band lines of one step (SarRouteMath.BandOffsets)
            int steps = Math.Max(1, pts.Count / perStep - 1);
            double len = CheckNm - StepNm;
            if (stride < 1) stride = 1;
            for (int s = 0; s <= steps; s++)
            {
                bool coarse = s % stride == 0 || s == steps;
                if (onlyRest ? coarse : !coarse) continue;   // T-278: a coarse pass probes a subset of the full probes; onlyRest probes the other steps
                for (int k = 0; k < perStep; k++)
                {
                    double h = heightM(pts[s * perStep + k]);
                    probes++;
                    if (!SarRouteMath.Passable(h, p.MinDepthM))
                    {
                        altM = h;
                        return StepNm + len * s / steps;
                    }
                }
            }
            return double.PositiveInfinity;
        }

        private sealed class Cand { public double C; public int Tier, Grp; public double Dw; }

        /// <summary>Section 3.2: the wanted course when clear, else the nearest clear stern / beam course, else the longest clear run.</summary>
        public static EvGuardPick Pick(double wantDeg, double headingDeg, double torpBrgDeg, double? torpCourseDeg, IList<double> otherBrgs,
                                       double? prevGuardDeg, Func<double, double> blockNmOf)
        {
            var cache = new Dictionary<long, double>();
            Func<double, double> bn = c =>
            {
                long key = (long)Math.Round(EvasionMath.Norm360(c) * 10);
                double v;
                if (!cache.TryGetValue(key, out v)) { v = blockNmOf(EvasionMath.Norm360(c)); cache[key] = v; }
                return v;
            };
            double want = EvasionMath.Norm360(wantDeg);
            var res = new EvGuardPick { BlockNm = bn(want) };
            if (double.IsPositiveInfinity(res.BlockNm))
            {
                res.CourseDeg = want; res.Why = "clear"; res.PickBlockNm = res.BlockNm; res.RelDeg = Math.Abs(EvasionMath.Diff(want, torpBrgDeg));
                return res;
            }
            var cands = new List<Cand>();
            var seen = new HashSet<long>();
            Action<double> add = c =>
            {
                c = EvasionMath.Norm360(c);
                if (!seen.Add((long)Math.Round(c * 10))) return;
                double rel = Math.Abs(EvasionMath.Diff(c, torpBrgDeg));
                int tier = rel >= Tier1RelDeg ? 1 : rel >= Tier2RelDeg ? 2 : 0;
                if (tier == 0) return;
                bool bad = EvasionMath.Crosses(headingDeg, c, torpBrgDeg, torpCourseDeg);
                if (!bad && otherBrgs != null)
                    foreach (double ob in otherBrgs) if (Math.Abs(EvasionMath.Diff(c, ob)) <= EvasionMath.OtherConeDeg) { bad = true; break; }
                cands.Add(new Cand { C = c, Tier = tier, Grp = bad ? 1 : 0, Dw = Math.Abs(EvasionMath.Diff(c, want)) });
            };
            for (double c = 0; c < 360 - 1e-9; c += StepDeg) add(c);
            if (prevGuardDeg.HasValue) add(prevGuardDeg.Value);
            var order = cands.OrderBy(x => x.Tier).ThenBy(x => x.Grp).ThenBy(x => x.Dw).ToList();
            Cand best = null;
            foreach (var c in order) if (double.IsPositiveInfinity(bn(c.C))) { best = c; break; }
            if (best != null)
            {
                Cand keep = null;
                if (prevGuardDeg.HasValue)
                {
                    foreach (var c in order)
                        if (Math.Abs(EvasionMath.Diff(c.C, prevGuardDeg.Value)) < 0.05) { keep = c; break; }
                    if (keep != null && !(double.IsPositiveInfinity(bn(keep.C)) && keep.Tier <= best.Tier && keep.Grp <= best.Grp && keep.Dw - best.Dw <= HystDeg)) keep = null;
                }
                if (keep != null) best = keep;
                res.CourseDeg = best.C; res.Why = best.Tier == 1 ? "nearest" : "beam"; res.PickBlockNm = double.PositiveInfinity;
            }
            else
            {
                double bc = want, bb = res.BlockNm;
                foreach (var c in order) { double v = bn(c.C); if (v > bb) { bb = v; bc = c.C; } }
                res.CourseDeg = bc; res.Why = "best_effort"; res.PickBlockNm = bb;
            }
            res.RelDeg = Math.Abs(EvasionMath.Diff(res.CourseDeg, torpBrgDeg));
            return res;
        }

        /// <summary>Section 3.3: re-check timing + Pick + state update; heightM is read only when a check is due.</summary>
        public static EvGuardPick Steer(EvGuardState g, double nowS, GeoPt own, double wantDeg, double headingDeg, double torpBrgDeg,
                                        double? torpCourseDeg, IList<double> otherBrgs, Func<GeoPt, double> heightM, int coarseStride = CoarseStride)
        {
            bool due = g.Last == null || double.IsNaN(g.LastWantDeg) || nowS >= g.NextCheckS || Math.Abs(EvasionMath.Diff(g.LastWantDeg, wantDeg)) >= WantMoveDeg;
            if (!due)
            {
                var l = g.Last;
                double course = g.GuardDeg ?? EvasionMath.Norm360(wantDeg);
                return new EvGuardPick
                {
                    CourseDeg = course, Why = g.LastWhy, BlockNm = l.BlockNm, BlockAltM = l.BlockAltM, PickBlockNm = l.PickBlockNm,
                    RelDeg = Math.Abs(EvasionMath.Diff(course, torpBrgDeg)), Probes = 0, Checked = false, Changed = false
                };
            }
            int probes = 0;
            var alts = new Dictionary<long, double>();
            // T-278 (N3): a coarse pass first (a subset of the full probes: blocked there means blocked); only a coarse-clear course is probed in full.
            // "Clear" is therefore exactly the old verdict; a blocked length is the coarse one (>= the full one), the numbers of the log are redone in full below.
            Func<double, double> bn = c =>
            {
                double alt = double.NaN; int pr;
                double v = double.PositiveInfinity;
                if (coarseStride > 1)
                {
                    v = BlockNm(own, c, heightM, out alt, out pr, coarseStride);
                    probes += pr;
                }
                if (coarseStride <= 1)
                {
                    v = BlockNm(own, c, heightM, out alt, out pr);
                    probes += pr;
                }
                else if (double.IsPositiveInfinity(v))
                {
                    v = BlockNm(own, c, heightM, out alt, out pr, coarseStride, true);   // the steps the coarse pass skipped: together a full check
                    probes += pr;
                }
                alts[(long)Math.Round(c * 10)] = alt;
                return v;
            };
            EvGuardPick r = Pick(wantDeg, headingDeg, torpBrgDeg, torpCourseDeg, otherBrgs, g.GuardDeg, bn);
            if (coarseStride > 1)
            {
                double fa; int fp;
                if (!double.IsPositiveInfinity(r.BlockNm))
                {
                    r.BlockNm = BlockNm(own, wantDeg, heightM, out fa, out fp); probes += fp;
                    alts[(long)Math.Round(EvasionMath.Norm360(wantDeg) * 10)] = fa;
                }
                if (!double.IsPositiveInfinity(r.PickBlockNm))
                {
                    r.PickBlockNm = BlockNm(own, r.CourseDeg, heightM, out fa, out fp); probes += fp;
                }
            }
            double wa;
            r.BlockAltM = alts.TryGetValue((long)Math.Round(EvasionMath.Norm360(wantDeg) * 10), out wa) ? wa : double.NaN;
            r.Probes = probes; r.Checked = true;
            r.Changed = r.Why != g.LastWhy || (r.Why != "clear" && g.GuardDeg.HasValue && Math.Abs(EvasionMath.Diff(g.GuardDeg.Value, r.CourseDeg)) >= 1)
                        || (r.Why != "clear" && !g.GuardDeg.HasValue);
            g.GuardDeg = r.Why == "clear" ? (double?)null : r.CourseDeg;
            g.LastWantDeg = EvasionMath.Norm360(wantDeg);
            g.NextCheckS = nowS + RecheckS;
            g.LastWhy = r.Why;
            g.Last = r;
            return r;
        }

        // --- self test ---------------------------------------------------------------------------------------------------

        private static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        public static string SelfTest()
        {
            double alt; int pr;
            var own = new GeoPt(0, 0);
            Func<GeoPt, double> sea = g => -200;
            if (!double.IsPositiveInfinity(BlockNm(own, 0, sea, out alt, out pr)) || pr < 125 || pr > 130 || !double.IsNaN(alt)) return "open sea: expected clear with 125 probes, got " + pr;
            // a land strip across the course (north) at 3.0-3.5 nm
            Func<GeoPt, double> strip = g => { double y = g.Lat * 60; return y >= 3.0 && y <= 3.5 ? 5 : -200; };
            double b = BlockNm(own, 0, strip, out alt, out pr);
            if (Math.Abs(b - 3.0) > 0.2 || alt != 5) return "strip: block " + F(b) + " alt " + F(alt);
            // a shoal -10 m blocks, -20 m is passable
            Func<GeoPt, double> shoal10 = g => { double y = g.Lat * 60; return y >= 3.0 ? -10 : -200; };
            Func<GeoPt, double> shoal20 = g => { double y = g.Lat * 60; return y >= 3.0 ? -20 : -200; };
            if (double.IsPositiveInfinity(BlockNm(own, 0, shoal10, out alt, out pr)) || alt != -10) return "shoal -10 must block";
            if (!double.IsPositiveInfinity(BlockNm(own, 0, shoal20, out alt, out pr))) return "-20 m must be passable";
            // land beside the course: 0.3 nm east of the line blocks (band 0.4), 0.6 nm does not
            Func<GeoPt, double> side3 = g => { double x = g.Lon * 60, y = g.Lat * 60; return x >= 0.3 && x <= 0.5 && y > 1 && y < 4 ? 5 : -200; };
            Func<GeoPt, double> side6 = g => { double x = g.Lon * 60, y = g.Lat * 60; return x >= 0.6 && x <= 0.8 && y > 1 && y < 4 ? 5 : -200; };
            if (double.IsPositiveInfinity(BlockNm(own, 0, side3, out alt, out pr))) return "land 0.3 nm beside must block";
            if (!double.IsPositiveInfinity(BlockNm(own, 0, side6, out alt, out pr))) return "land 0.6 nm beside must not block";
            // land under the ship itself does not block
            Func<GeoPt, double> under = g => { double x = g.Lon * 60, y = g.Lat * 60; return Math.Sqrt(x * x + y * y) < 0.1 ? 5 : -200; };
            if (!double.IsPositiveInfinity(BlockNm(own, 90, under, out alt, out pr))) return "land under the ship must not block";

            string e = PickTests();
            if (e != null) return e;
            e = SteerTests();
            if (e != null) return e;
            return CoarseTests();
        }

        // T-278 (N3): the coarse pass changes the probe cost, not the decisions
        private static string CoarseTests()
        {
            var own = new GeoPt(0, 0);
            double s45 = Math.Sin(Math.PI / 4);
            var terrains = new List<Func<double, double, double>>
            {
                (x, y) => (x + y) * s45 >= 3 ? 20 : -200,                                            // a coast to the north-east
                (x, y) => Math.Sqrt((x - 2.1) * (x - 2.1) + (y - 2.1) * (y - 2.1)) <= 0.5 ? 5 : -200,   // an island on the 045 course
                (x, y) => y >= 3.0 && y <= 3.5 ? 5 : -200,                                           // a strip to the north
                (x, y) => (x + y) * s45 >= 6 ? 20 : (x + y) * s45 >= 4.5 ? -8 : -200,               // a shoal then a coast
                (x, y) => { double r = Math.Sqrt(x * x + y * y); return r >= 4 && r <= 5 ? -5 : -200; }   // a shoal ring in every direction (best effort)
            };
            int ti = 0;
            foreach (var t in terrains)
            {
                ti++;
                foreach (double want in new[] { 45.0, 0.0, 100.0, 225.0, 310.0 })
                {
                    int callsOld = 0, callsNew = 0;
                    var gOld = new EvGuardState(); var gNew = new EvGuardState();
                    var rOld = Steer(gOld, 0, own, want, 100, 225, null, null, g => { callsOld++; return t(g.Lon * 60, g.Lat * 60); }, 1);
                    var rNew = Steer(gNew, 0, own, want, 100, 225, null, null, g => { callsNew++; return t(g.Lon * 60, g.Lat * 60); });
                    // best_effort compares blocked lengths: the coarse ones are up to 3 steps (0.6 nm) longer, a near-tie may go the other way; the run of the chosen course stays within that
                    if (rOld.Why == "best_effort" && rNew.Why == "best_effort")
                    {
                        if (rNew.PickBlockNm < rOld.PickBlockNm - 3 * StepNm - 1e-9) return "coarse t" + ti + " want " + F(want) + ": best effort run " + F(rNew.PickBlockNm) + " vs " + F(rOld.PickBlockNm);
                    }
                    else if (rOld.Why != rNew.Why || Math.Abs(EvasionMath.Diff(rOld.CourseDeg, rNew.CourseDeg)) > 1e-6)
                        return "coarse t" + ti + " want " + F(want) + ": " + rOld.Why + " " + F(rOld.CourseDeg) + " vs " + rNew.Why + " " + F(rNew.CourseDeg);
                    if (double.IsPositiveInfinity(rOld.BlockNm) != double.IsPositiveInfinity(rNew.BlockNm) || (!double.IsInfinity(rOld.BlockNm) && Math.Abs(rOld.BlockNm - rNew.BlockNm) > 1e-9))
                        return "coarse t" + ti + " want " + F(want) + ": block " + F(rOld.BlockNm) + " vs " + F(rNew.BlockNm);
                    if (ti == 5 && want == 45.0)
                    {
                        if (callsNew * 2 > callsOld) return "coarse ring: probes " + callsNew + " vs " + callsOld + " (expected at least 2x fewer)";
                    }
                }
            }
            return null;
        }

        // fake rays: blocked sector [lo, hi] (deg) gives 2 nm, others clear
        private static Func<double, double> Sector(double lo, double hi, Action onCall = null)
        {
            return c => { if (onCall != null) onCall(); return Math.Abs(EvasionMath.Diff((lo + hi) / 2, c)) <= (hi - lo) / 2 + 1e-6 ? 2.0 : double.PositiveInfinity; };
        }

        private static string PickTests()
        {
            int calls = 0;
            // a) the wanted course is clear
            var r = Pick(75, 120, 225, null, null, null, c => { calls++; return double.PositiveInfinity; });
            if (r.Why != "clear" || Math.Abs(r.CourseDeg - 75) > 1e-6 || calls != 1) return "pick a: clear expected, calls " + calls;
            // b) torpedo 225, want 075 (rel 150), sector 030-110 blocked -> 020 (tier 1), not 130 (rel 95)
            r = Pick(75, 120, 225, null, null, null, Sector(30, 110));
            if (r.Why != "nearest" || Math.Abs(r.CourseDeg - 20) > 1e-6) return "pick b: expected 020 nearest, got " + F(r.CourseDeg) + " " + r.Why;
            // c) tier 1 fully blocked (345..105 arc) -> tier 2 (beam)
            Func<double, double> noStern = c => Math.Abs(EvasionMath.Diff(225, c)) >= 120 ? 1.0 : double.PositiveInfinity;
            r = Pick(75, 120, 225, null, null, null, noStern);
            if (r.Why != "beam") return "pick c: beam expected, got " + r.Why + " " + F(r.CourseDeg);
            double rel = Math.Abs(EvasionMath.Diff(r.CourseDeg, 225));
            if (rel < 90 || rel >= 120) return "pick c: beam course out of tier 2: " + F(r.CourseDeg);
            // d) everything blocked: the longest run
            r = Pick(75, 120, 225, null, null, null, c => Math.Abs(EvasionMath.Diff(c, 10)) < 1e-6 ? 4.0 : 1.0);
            if (r.Why != "best_effort" || Math.Abs(r.CourseDeg - 10) > 1e-6) return "pick d: best_effort 010 expected, got " + F(r.CourseDeg) + " " + r.Why;
            // e) hysteresis: want 075, blocked 030-110; best is 020 (dw 55); prev 010 is 5 deg worse -> stays, prev 350 is 15 deg worse -> best
            r = Pick(75, 120, 225, null, null, 10, Sector(30, 110));
            if (Math.Abs(r.CourseDeg - 10) > 1e-6) return "pick e: prev 010 must stay, got " + F(r.CourseDeg);
            r = Pick(75, 120, 225, null, null, 350, Sector(30, 110));
            if (Math.Abs(r.CourseDeg - 20) > 1e-6) return "pick e: prev 350 must give way to 020, got " + F(r.CourseDeg);
            // f) the bow sweeps the torpedo bearing for one candidate: the equal one without a crossing wins
            // heading 100, torpedo 225 (bearing), want 160 blocked (sector 150-170): 140 (dw 20) and 180 (dw 20); the turn 100 -> 180 passes 225? no; 100 -> 140 no.
            // use heading 200: 140 is a turn to port (no crossing), 180 is a turn to port too; use heading 250: 180 sweeps 225? from 250 turning to port 70 deg passes 225 -> crossing
            r = Pick(160, 250, 300, null, null, null, Sector(150, 170));
            double rc = Math.Abs(EvasionMath.Diff(r.CourseDeg, 300));
            if (r.Why == "clear" || rc < 120) return "pick f: tier 1 expected, got " + F(r.CourseDeg);
            var cross = EvasionMath.Crosses(250, r.CourseDeg, 300, null);
            if (cross) return "pick f: a crossing course " + F(r.CourseDeg) + " was chosen while a non-crossing one exists";
            // g) another torpedo 30 deg off the bow of a candidate: that candidate goes to the second group
            r = Pick(75, 120, 225, null, new List<double> { 50 }, null, Sector(30, 110));
            if (Math.Abs(EvasionMath.Diff(r.CourseDeg, 50)) <= EvasionMath.OtherConeDeg) return "pick g: the course " + F(r.CourseDeg) + " is inside the cone of the other torpedo";
            return null;
        }

        private static string SteerTests()
        {
            int calls = 0;
            var own = new GeoPt(0, 0);
            Func<GeoPt, double> sea = g => { calls++; return -200; };
            var st = new EvGuardState();
            var r = Steer(st, 100, own, 75, 120, 225, null, null, sea);
            if (!r.Checked || r.Probes != calls || r.Probes < 125 || r.Probes > 130 || r.Why != "clear" || r.Changed) return "steer: first check " + r.Checked + " probes " + r.Probes;
            calls = 0;
            r = Steer(st, 102, own, 77, 120, 225, null, null, sea);
            if (r.Checked || calls != 0 || Math.Abs(r.CourseDeg - 77) > 1e-6) return "steer: cached result expected, calls " + calls;
            r = Steer(st, 105, own, 77, 120, 225, null, null, sea);
            if (!r.Checked || calls < 125) return "steer: a check is due after 5 s, calls " + calls;
            calls = 0;
            r = Steer(st, 106, own, 83, 120, 225, null, null, sea);   // 6 deg from the last checked 77
            if (!r.Checked || calls < 125) return "steer: a want shift of 3 deg or more checks at once, calls " + calls;
            // land to the north-east at 3 nm: the wanted course 045 is blocked -> a guard course; a repeat does not report a change
            Func<GeoPt, double> coast = g => { calls++; double x = g.Lon * 60, y = g.Lat * 60; return (x + y) / Math.Sqrt(2) >= 3 ? 20 : -200; };
            var st2 = new EvGuardState();
            r = Steer(st2, 0, own, 45, 100, 225, null, null, coast);
            if (r.Why == "clear" || !r.Changed || !st2.GuardDeg.HasValue) return "steer: the coast must block 045, why " + r.Why;
            if (double.IsNaN(r.BlockAltM) || r.BlockAltM != 20 || double.IsInfinity(r.BlockNm)) return "steer: block data missing";
            double first = r.CourseDeg;
            calls = 0;
            r = Steer(st2, 5, own, 45, 100, 225, null, null, coast);
            if (r.Changed || Math.Abs(r.CourseDeg - first) > 1e-6) return "steer: repeat check must keep the course silently, got " + F(r.CourseDeg);
            r = Steer(st2, 6, own, 45, 100, 225, null, null, coast);
            if (r.Checked || Math.Abs(r.CourseDeg - first) > 1e-6) return "steer: cached guard course expected";
            return null;
        }
    }
}
