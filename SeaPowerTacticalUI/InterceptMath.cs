using System;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    // T-138 / ADR-0054: pure intercept math (no Unity, no reflection, no game). Local flat plane in nm,
    // x = east, y = north, origin = the own unit (ContactInfoMath.ToLocalNm). Speeds in knots, times in seconds unless named "H".

    /// <summary>Solver input: target relative to the own unit, target velocity, own speed.</summary>
    internal sealed class IcInput
    {
        public double DxNm, DyNm, VxKn, VyKn, OwnKn;
        public double MaxRangeNm = 150, MaxEtaH = 6;
    }

    /// <summary>Kind: ok | cpa | opening | too_far | bad_input. IP = intercept point (ok) or own position at the closest approach (cpa).</summary>
    internal sealed class IcSolution
    {
        public string Kind;
        public double CourseDeg, EtaS, IpX, IpY, CpaNm = double.NaN, TcpaS = double.NaN;
        public double EtaLoS = double.NaN, EtaHiS = double.NaN;
    }

    /// <summary>Inputs of the "plan again?" decision (time in mission seconds).</summary>
    internal sealed class IcReplan
    {
        public double NowS, PlanAtS, RefreshS = 30, MinShiftNm = 0.5, IpShiftNm, DistNm, OffRouteNm;
        public bool KindChanged, NearShore;
    }

    internal static class InterceptMath
    {
        public const double StillKn = 0.5, CpaTieNm = 0.05, BandCourseDeg = 10, BandWidePct = 10, ArriveEtaS = 30,
                            ShiftPct = 5, OffRouteNm = 1, NearShoreReplanS = 120, ProbeStepNm = 1;
        public const int MaxRefine = 2, MaxProbes = 150;
        private const double Rad = Math.PI / 180.0;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v); }

        /// <summary>Smallest t &gt; 0 (hours) of |d + v t| = s t; NaN = no solution. d = 0 gives 0.</summary>
        public static double LeadTime(double dx, double dy, double vx, double vy, double ownKn)
        {
            if (Bad(dx) || Bad(dy) || Bad(vx) || Bad(vy) || Bad(ownKn)) return double.NaN;
            double a = vx * vx + vy * vy - ownKn * ownKn, b = 2 * (dx * vx + dy * vy), c = dx * dx + dy * dy;
            if (c < 1e-12) return 0;
            if (Math.Abs(a) < 1e-6)
            {
                if (!(b < 0)) return double.NaN;
                double t0 = -c / b;
                return t0 > 0 ? t0 : double.NaN;
            }
            double disc = b * b - 4 * a * c;
            if (disc < 0) return double.NaN;
            double sq = Math.Sqrt(disc);
            double t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
            double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
            double t = lo > 0 ? lo : hi;
            return t > 0 ? t : double.NaN;
        }

        private static double Brg(double x, double y)
        {
            double b = Math.Atan2(x, y) / Rad;
            return (b % 360.0 + 360.0) % 360.0;
        }

        /// <summary>Lead first; no lead -> the course of the smallest CPA (cpa) or opening.</summary>
        public static IcSolution Solve(IcInput i)
        {
            if (i == null || Bad(i.DxNm) || Bad(i.DyNm) || Bad(i.VxKn) || Bad(i.VyKn) || Bad(i.OwnKn) || !(i.OwnKn > 0))
                return new IcSolution { Kind = "bad_input" };
            double dist = Math.Sqrt(i.DxNm * i.DxNm + i.DyNm * i.DyNm);
            if (dist > i.MaxRangeNm) return new IcSolution { Kind = "too_far" };
            double vx = i.VxKn, vy = i.VyKn;
            if (Math.Sqrt(vx * vx + vy * vy) < StillKn) { vx = 0; vy = 0; }
            double t = LeadTime(i.DxNm, i.DyNm, vx, vy, i.OwnKn);
            if (!double.IsNaN(t))
            {
                if (t > i.MaxEtaH) return new IcSolution { Kind = "too_far" };
                double ix = i.DxNm + vx * t, iy = i.DyNm + vy * t;
                return new IcSolution { Kind = "ok", CourseDeg = dist < 1e-9 ? 0 : Brg(ix, iy), EtaS = t * 3600.0, IpX = ix, IpY = iy };
            }
            return BestCpa(i);
        }

        private static double CpaAt(IcInput i, double courseDeg, double dist, out double tcpa)
        {
            double ox = i.OwnKn * Math.Sin(courseDeg * Rad), oy = i.OwnKn * Math.Cos(courseDeg * Rad);
            double c;
            if (ContactInfoMath.Cpa(i.DxNm, i.DyNm, i.VxKn - ox, i.VyKn - oy, i.MaxEtaH * 3600.0, out c, out tcpa)) return c;
            tcpa = 0;
            return dist;   // never closer than now
        }

        /// <summary>Own course 0..359 step 1, then +-1 step 0.1. cpa: closes in; opening: no course gets closer than now.</summary>
        public static IcSolution BestCpa(IcInput i)
        {
            if (i == null || Bad(i.DxNm) || Bad(i.DyNm) || Bad(i.VxKn) || Bad(i.VyKn) || Bad(i.OwnKn) || !(i.OwnKn > 0))
                return new IcSolution { Kind = "bad_input" };
            double dist = Math.Sqrt(i.DxNm * i.DxNm + i.DyNm * i.DyNm);
            // pass 1: the smallest CPA; pass 2: among the courses within CpaTieNm of it, the one with the smallest TCPA
            var courses = new List<double>(); var cpas = new List<double>(); var tcpas = new List<double>();
            for (int k = 0; k < 360; k++) { double t, c = CpaAt(i, k, dist, out t); courses.Add(k); cpas.Add(c); tcpas.Add(t); }
            int bi = 0;
            for (int k = 1; k < cpas.Count; k++) if (cpas[k] < cpas[bi]) bi = k;
            double centre = courses[bi];
            for (int k = -10; k <= 10; k++)
            {
                if (k == 0) continue;
                double course = ((centre + k * 0.1) % 360.0 + 360.0) % 360.0, t, c = CpaAt(i, course, dist, out t);
                courses.Add(course); cpas.Add(c); tcpas.Add(t);
            }
            double minC = double.MaxValue;
            foreach (double c in cpas) if (c < minC) minC = c;
            int best = -1;
            for (int k = 0; k < cpas.Count; k++)
                if (cpas[k] <= minC + CpaTieNm && (best < 0 || tcpas[k] < tcpas[best])) best = k;
            double bestC = cpas[best], bestT = tcpas[best], bestCourse = courses[best];
            if (bestC >= dist - CpaTieNm)
                return new IcSolution { Kind = "opening", CourseDeg = Brg(i.DxNm, i.DyNm), CpaNm = dist, TcpaS = 0 };
            double th = bestT / 3600.0;
            return new IcSolution
            {
                Kind = "cpa", CourseDeg = bestCourse, CpaNm = bestC, TcpaS = bestT, EtaS = bestT,
                IpX = i.OwnKn * Math.Sin(bestCourse * Rad) * th, IpY = i.OwnKn * Math.Cos(bestCourse * Rad) * th
            };
        }

        /// <summary>Fills EtaLoS/EtaHiS: target speed x (1 +- pct/100) and target course +-10 deg; min and max over the results of the same kind as s.</summary>
        public static void Band(IcInput i, double uncertPct, IcSolution s)
        {
            if (s == null) return;
            s.EtaLoS = s.EtaHiS = double.NaN;
            if (i == null || (s.Kind != "ok" && s.Kind != "cpa")) return;
            double lo = s.EtaS, hi = s.EtaS, p = Math.Max(0, uncertPct) / 100.0;
            foreach (double f in new[] { 1 - p, 1, 1 + p })
                foreach (double dc in new[] { -BandCourseDeg, 0, BandCourseDeg })
                {
                    double r = dc * Rad, cs = Math.Cos(r), sn = Math.Sin(r);
                    var j = new IcInput
                    {
                        DxNm = i.DxNm, DyNm = i.DyNm, OwnKn = i.OwnKn, MaxRangeNm = i.MaxRangeNm, MaxEtaH = i.MaxEtaH,
                        VxKn = f * (i.VxKn * cs + i.VyKn * sn), VyKn = f * (-i.VxKn * sn + i.VyKn * cs)
                    };
                    IcSolution q = Solve(j);
                    if (q.Kind != s.Kind) continue;
                    if (q.EtaS < lo) lo = q.EtaS;
                    if (q.EtaS > hi) hi = q.EtaS;
                }
            s.EtaLoS = lo; s.EtaHiS = hi;
        }

        public static bool BandIsWide(IcSolution s)
        {
            if (s == null || Bad(s.EtaLoS) || Bad(s.EtaHiS) || !(s.EtaS > 0)) return false;
            return (s.EtaHiS - s.EtaLoS) > BandWidePct / 100.0 * s.EtaS;
        }

        /// <summary>fresh | stale | lost | bearing_only. An unknown age counts as fresh.</summary>
        public static string Quality(double ageS, bool bearingOnly, double staleS, double lostS)
        {
            if (bearingOnly) return "bearing_only";
            if (Bad(ageS) || ageS <= staleS) return "fresh";
            return ageS <= lostS ? "stale" : "lost";
        }

        /// <summary>Telegraph scale -1..5 -> index telegraph + 1. Returns the telegraph. "auto": the current step when it gives ok, else the lowest
        /// step ahead of it that does, else the highest; never lower than now; from a stop (or reverse) the first step ahead. "current": the current step only.</summary>
        public static int PickStep(double[] stepsKn, int telegraph, string speedMode, Func<double, IcSolution> solve, out IcSolution sol)
        {
            sol = new IcSolution { Kind = "bad_input" };
            if (stepsKn == null || stepsKn.Length == 0 || solve == null) return telegraph;
            int cur = Math.Max(0, Math.Min(stepsKn.Length - 1, telegraph + 1));
            if (speedMode == "current")
            {
                sol = solve(stepsKn[cur]);
                return telegraph;
            }
            int start = cur;
            if (!(stepsKn[start] > 0))
            {
                start = -1;
                for (int k = cur; k < stepsKn.Length; k++) if (stepsKn[k] > 0) { start = k; break; }
                if (start < 0) return telegraph;
            }
            int last = -1;
            IcSolution lastSol = null;
            for (int k = start; k < stepsKn.Length; k++)
            {
                if (!(stepsKn[k] > 0)) continue;
                IcSolution q = solve(stepsKn[k]);
                if (q != null && q.Kind == "ok") { sol = q; return k - 1; }
                last = k; lastSol = q;
            }
            if (last < 0) return telegraph;
            sol = lastSol ?? sol;
            return last - 1;
        }

        /// <summary>Plan again? reason: too_soon | ip_shift | off_route | kind | near_shore (false = keep the route).</summary>
        public static bool NeedReplan(IcReplan r, out string reason)
        {
            reason = "too_soon";
            if (r == null) return false;
            double since = r.NowS - r.PlanAtS;
            if (!(since >= r.RefreshS)) return false;
            if (r.KindChanged) { reason = "kind"; return true; }
            if (r.IpShiftNm >= Math.Max(r.MinShiftNm, ShiftPct / 100.0 * r.DistNm)) { reason = "ip_shift"; return true; }
            if (r.OffRouteNm > OffRouteNm) { reason = "off_route"; return true; }
            if (r.NearShore && since > NearShoreReplanS) { reason = "near_shore"; return true; }
            reason = "too_soon";
            return false;
        }

        /// <summary>Solves again at the effective speed OwnKn / k, k = route length / straight line (k &lt; 1 counts as 1).</summary>
        public static IcSolution RefineForRoute(IcInput i, double routeFactorK)
        {
            double k = (Bad(routeFactorK) || routeFactorK < 1) ? 1 : routeFactorK;
            var j = new IcInput { DxNm = i.DxNm, DyNm = i.DyNm, VxKn = i.VxKn, VyKn = i.VyKn, OwnKn = i.OwnKn / k, MaxRangeNm = i.MaxRangeNm, MaxEtaH = i.MaxEtaH };
            return Solve(j);
        }

        /// <summary>null = allowed. kind = the game kind of the track: Surface and Submarine only; relation friendly/own -> own_target.</summary>
        public static string Allow(string kind, string relation, bool live, bool bearingOnly)
        {
            if (relation == "friendly" || relation == "own") return "own_target";
            if (string.IsNullOrEmpty(kind)) return "no_track";
            if (kind != "Surface" && kind != "Submarine") return "target_domain";
            if (bearingOnly) return "bearing_only";
            if (!live) return "no_track";
            return null;
        }

        public static bool Arrived(double distNm, double ipDistNm, double etaS, double arriveNm)
        {
            return distNm <= arriveNm || (ipDistNm <= arriveNm && etaS < ArriveEtaS);
        }

        /// <summary>h:mm ("0:42"; "12:05" from 10 h). NaN -> "".</summary>
        public static string FmtEta(double s)
        {
            if (Bad(s)) return "";
            long m = (long)Math.Round(Math.Max(0, s) / 60.0);
            return (m / 60).ToString(Inv) + ":" + (m % 60).ToString("00", Inv);
        }

        // ---------------------------------------------------------------- self test
        private static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        private static IcInput In(double dx, double dy, double vx, double vy, double own)
        {
            return new IcInput { DxNm = dx, DyNm = dy, VxKn = vx, VyKn = vy, OwnKn = own };
        }

        public static string SelfTest()
        {
            try { return SelfTestCore(); }
            catch (Exception e) { return "intercept: " + e.GetType().Name + ": " + e.Message; }
        }

        private static string SelfTestCore()
        {
            IcSolution s;
            // head-on: closing 20 kn over 10 nm -> 0.5 h, IP halfway
            s = Solve(In(0, 10, 0, -10, 10));
            if (s.Kind != "ok" || !Near(s.EtaS, 1800, 1e-6) || !Near(s.IpY, 5, 1e-9) || !Near(s.CourseDeg, 0, 1e-6)) return "head-on: " + s.Kind + " eta=" + s.EtaS;
            // pursuit of a slower target
            s = Solve(In(0, 10, 0, 10, 20));
            if (s.Kind != "ok" || !Near(s.EtaS, 3600, 1e-6)) return "pursuit faster: " + s.Kind + " eta=" + s.EtaS;
            // 90 deg: t = sqrt(100 / 75) h
            s = Solve(In(10, 0, 0, 5, 10));
            double t90 = Math.Sqrt(100.0 / 75.0);
            if (s.Kind != "ok" || !Near(s.EtaS, t90 * 3600, 1e-6) || !Near(s.IpY, 5 * t90, 1e-9) || !Near(s.IpX, 10, 1e-9)) return "90deg: " + s.Kind + " eta=" + s.EtaS;
            if (!Near(s.CourseDeg, Brg(s.IpX, s.IpY), 1e-9)) return "90deg course";
            // slower, target crosses fast: no lead -> cpa with a course that closes in
            s = Solve(In(10, 0, 0, 10, 5));
            if (s.Kind != "cpa" || !(s.CpaNm < 10 - CpaTieNm) || !(s.TcpaS > 0) || double.IsNaN(s.CourseDeg)) return "cpa: " + s.Kind + " cpa=" + s.CpaNm;
            if (!Near(s.EtaS, s.TcpaS, 1e-9)) return "cpa eta = tcpa";
            // target stands (jitter below 0.5 kn counts as standing)
            s = Solve(In(3, 4, 0.2, 0.2, 10));
            if (s.Kind != "ok" || !Near(s.EtaS, 1800, 1e-6) || !Near(s.IpX, 3, 1e-9) || !Near(s.IpY, 4, 1e-9) || !Near(s.CourseDeg, Math.Atan2(3, 4) / Rad, 1e-9)) return "still: " + s.Kind + " eta=" + s.EtaS;
            // equal speeds: converging -> ok, diverging -> opening
            s = Solve(In(0, 10, 0, -10, 10));
            if (s.Kind != "ok" || !Near(s.EtaS, 1800, 1e-6)) return "equal converging: " + s.Kind;
            s = Solve(In(0, 10, 0, 10, 10));
            if (s.Kind != "opening") return "equal diverging: " + s.Kind;
            // a fast target running away
            s = Solve(In(0, 10, 0, 20, 10));
            if (s.Kind != "opening") return "opening: " + s.Kind;
            // bad input / too far
            if (Solve(In(0, 10, 0, 0, 0)).Kind != "bad_input") return "own 0";
            if (Solve(In(0, 10, 0, 0, double.NaN)).Kind != "bad_input") return "own NaN";
            if (Solve(In(double.NaN, 10, 0, 0, 10)).Kind != "bad_input") return "dx NaN";
            if (Solve(In(0, 200, 0, 0, 30)).Kind != "too_far") return "range > 150";
            if (Solve(In(0, 100, 0, -1, 10)).Kind != "too_far") return "eta > 6 h";
            if (!double.IsNaN(LeadTime(0, 10, 0, 10, 10))) return "LeadTime diverging";
            if (!Near(LeadTime(0, 0, 3, 3, 10), 0, 1e-12)) return "LeadTime d = 0";

            // band and wide band
            var ib = In(0, 10, 0, -10, 10);
            s = Solve(ib);
            Band(ib, 20, s);
            if (!(s.EtaLoS < s.EtaS && s.EtaS < s.EtaHiS) || !BandIsWide(s)) return "band: " + s.EtaLoS + ".." + s.EtaHiS;
            var ist = In(3, 4, 0, 0, 10);
            s = Solve(ist);
            Band(ist, 20, s);
            if (BandIsWide(s) || !Near(s.EtaLoS, s.EtaHiS, 1e-6)) return "band still: " + s.EtaLoS + ".." + s.EtaHiS;
            s = Solve(In(0, 10, 0, 20, 10)); Band(In(0, 10, 0, 20, 10), 20, s);
            if (!double.IsNaN(s.EtaLoS) || BandIsWide(s)) return "band opening";

            // quality
            if (Quality(10, false, 60, 180) != "fresh" || Quality(60, false, 60, 180) != "fresh" || Quality(61, false, 60, 180) != "stale"
                || Quality(180, false, 60, 180) != "stale" || Quality(181, false, 60, 180) != "lost" || Quality(5, true, 60, 180) != "bearing_only"
                || Quality(double.NaN, false, 60, 180) != "fresh") return "quality";

            // PickStep: scale -1..5 = indices 0..6
            double[] steps = { -5, 0, 5, 10, 15, 20, 25 };
            Func<double, double, double, double, Func<double, IcSolution>> mk = (dx, dy, vx, vy) => kn => Solve(In(dx, dy, vx, vy, kn));
            IcSolution ps;
            int tg = PickStep(steps, 3, "auto", mk(0, 10, 0, 5), out ps);   // 15 kn is enough
            if (tg != 3 || ps.Kind != "ok") return "PickStep enough: " + tg;
            tg = PickStep(steps, 1, "auto", mk(0, 10, 0, 5), out ps);       // 5 kn is not (lead needs > 5), 10 kn is
            if (tg != 2 || ps.Kind != "ok") return "PickStep lowest: " + tg + " " + ps.Kind;
            tg = PickStep(steps, 1, "auto", mk(0, 10, 0, 40), out ps);      // nobody catches 40 kn
            if (tg != 5 || ps.Kind == "ok") return "PickStep highest: " + tg + " " + ps.Kind;
            tg = PickStep(steps, 5, "auto", mk(0, 10, 0, 0), out ps);       // never lower
            if (tg != 5 || ps.Kind != "ok") return "PickStep never lower: " + tg;
            tg = PickStep(steps, 0, "auto", mk(0, 10, 0, 0), out ps);       // from a stop: first step ahead
            if (tg != 1 || ps.Kind != "ok") return "PickStep from stop: " + tg;
            tg = PickStep(steps, -1, "auto", mk(0, 10, 0, 0), out ps);      // from reverse too
            if (tg != 1 || ps.Kind != "ok") return "PickStep from reverse: " + tg;
            tg = PickStep(steps, 2, "current", mk(0, 10, 0, 5), out ps);    // current: no change of step, opening/cpa result of 10 kn is ok
            if (tg != 2) return "PickStep current: " + tg;
            tg = PickStep(steps, 1, "current", mk(0, 10, 0, 5), out ps);
            if (tg != 1 || ps.Kind == "ok") return "PickStep current weak: " + tg + " " + ps.Kind;
            tg = PickStep(new double[0], 1, "auto", mk(0, 10, 0, 0), out ps);
            if (tg != 1 || ps.Kind != "bad_input") return "PickStep no steps";

            // NeedReplan
            var rp = new IcReplan { NowS = 100, PlanAtS = 80, IpShiftNm = 3, DistNm = 10 };
            string why;
            if (NeedReplan(rp, out why) || why != "too_soon") return "replan < 30 s";
            rp.PlanAtS = 60; rp.IpShiftNm = 0.3;
            if (NeedReplan(rp, out why)) return "replan shift 0.3";
            rp.IpShiftNm = 0.6;
            if (!NeedReplan(rp, out why) || why != "ip_shift") return "replan shift 0.6: " + why;
            rp.IpShiftNm = 0.6; rp.DistNm = 40;   // 5 % of 40 nm = 2 nm
            if (NeedReplan(rp, out why)) return "replan shift 5 % of the distance";
            rp.IpShiftNm = 0; rp.OffRouteNm = 1.2;
            if (!NeedReplan(rp, out why) || why != "off_route") return "replan off route: " + why;
            rp.OffRouteNm = 0.5; rp.KindChanged = true;
            if (!NeedReplan(rp, out why) || why != "kind") return "replan kind: " + why;
            rp.KindChanged = false; rp.NearShore = true; rp.PlanAtS = 60;
            if (NeedReplan(rp, out why)) return "replan near shore 40 s";
            rp.PlanAtS = -30;   // 130 s
            if (!NeedReplan(rp, out why) || why != "near_shore") return "replan near shore: " + why;

            // refine for a detour: k = 1.3 shifts ETA and IP
            var ir = In(0, 10, 5, 0, 13);
            IcSolution a = Solve(ir), b = RefineForRoute(ir, 1.3);
            if (a.Kind != "ok" || b.Kind != "ok" || !(b.EtaS > a.EtaS) || Near(b.IpX, a.IpX, 1e-6)) return "refine: " + a.EtaS + " -> " + b.EtaS;
            IcSolution still = RefineForRoute(In(0, 10, 0, 0, 13), 1.3);
            if (!Near(still.EtaS, 3600, 1e-6)) return "refine still eta=" + still.EtaS;
            if (!Near(RefineForRoute(In(0, 10, 0, 0, 10), 0.5).EtaS, 3600, 1e-6)) return "refine k < 1";
            if (MaxRefine != 2 || MaxProbes != 150) return "constants";

            // Allow
            if (Allow("Surface", "hostile", true, false) != null || Allow("Submarine", "unknown", true, false) != null || Allow("Surface", "neutral", true, false) != null) return "Allow ok";
            if (Allow("Air", "hostile", true, false) != "target_domain" || Allow("Missile", "hostile", true, false) != "target_domain" || Allow("Torpedo", "hostile", true, false) != "target_domain") return "Allow domain";
            if (Allow("Surface", "friendly", true, false) != "own_target") return "Allow own";
            if (Allow("Surface", "hostile", false, true) != "bearing_only") return "Allow bearing";
            if (Allow("Surface", "hostile", false, false) != "no_track" || Allow(null, "hostile", true, false) != "no_track") return "Allow no track";

            // Arrived
            if (!Arrived(0.9, 5, 600, 1) || Arrived(1.5, 5, 600, 1)) return "Arrived dist";
            if (!Arrived(3, 0.8, 20, 1) || Arrived(3, 0.8, 40, 1)) return "Arrived ip";

            // FmtEta
            if (FmtEta(42 * 60) != "0:42" || FmtEta(3600 + 5 * 60) != "1:05" || FmtEta(12 * 3600 + 5 * 60) != "12:05" || FmtEta(double.NaN) != "" || FmtEta(0) != "0:00") return "FmtEta " + FmtEta(42 * 60) + " " + FmtEta(12 * 3600 + 300);
            return null;
        }
    }
}
