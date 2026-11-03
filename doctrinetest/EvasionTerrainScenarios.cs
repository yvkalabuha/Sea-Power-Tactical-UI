using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-272 (ADR-0108): synthetic replays of N1 (the 0.30.18 grounding at Bab-el-Mandeb). A 1 s simulator: the ship (flank 24.5 kn, 1.3 deg/s turn),
    /// torpedoes in pure pursuit, the track freezes after FreezeS (the course is held, EvasionTick frozen branch). Terrain is a function in miles
    /// (GeoPt lat = y/60, lon = x/60). Every tick: CourseAwayMulti (fresh) or the held course (frozen) -> EvasionCourseGuard.Steer.
    /// B1: a coast 7 nm away with a 0.5 nm shoal in front of it (the control without the guard grounds); B2: a strait; B3: open sea (the guard changes nothing).
    /// </summary>
    internal static class EvasionTerrainScenarios
    {
        private const double FlankKn = 24.5, TurnDegS = 1.3, HitNm = 0.1, MinDepthM = EvasionCourseGuard.MinDepthM;

        private sealed class Scn
        {
            public string Name;
            public double DistNm, BrgDeg, RouteCourse, FreezeS, VKn, EndS = 1800;
            public Func<double, double, double> Terrain;   // (east nm, north nm) -> height m
        }

        private sealed class Res
        {
            public double HitS = double.NaN, GroundS = double.NaN, MinDepthM = double.PositiveInfinity, MaxLandM = double.NegativeInfinity;
            public int Ticks, TerrainCalls, Shifts, Checks;
            public string Err;
            public readonly List<string> Whys = new List<string>();
            public readonly List<double> Courses = new List<double>();
        }

        private static double Brg(double dx, double dy) { return EvasionMath.Norm360(Math.Atan2(dx, dy) * 180 / Math.PI); }

        private static Res Run(Scn sc, bool guarded)
        {
            var res = new Res();
            double tx = sc.DistNm * Math.Sin(sc.BrgDeg * Math.PI / 180), ty = sc.DistNm * Math.Cos(sc.BrgDeg * Math.PI / 180);
            double mx = 0, my = 0;
            bool frozen = false;
            var st = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0 };
            var guard = new EvGuardState();
            double sx = 0, sy = 0, hdg = sc.RouteCourse, esc = double.NaN;
            double lastWant = double.NaN;
            Func<GeoPt, double> hf = g => { res.TerrainCalls++; return sc.Terrain(g.Lon * 60, g.Lat * 60); };
            for (int sec = 1; sec <= sc.EndS; sec++)
            {
                double now = sec;
                res.Ticks++;
                double v = sc.VKn / 3600.0;
                double dx = sx - tx, dy = sy - ty, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 1e-9) { tx += dx / d * v; ty += dy / d * v; }
                if (!frozen && now >= sc.FreezeS) { frozen = true; mx = tx; my = ty; }
                if (!double.IsNaN(esc)) { double df = EvasionMath.Diff(hdg, esc); hdg = EvasionMath.Norm360(hdg + Math.Max(-TurnDegS, Math.Min(TurnDegS, df))); }
                sx += Math.Sin(hdg * Math.PI / 180) * FlankKn / 3600.0; sy += Math.Cos(hdg * Math.PI / 180) * FlankKn / 3600.0;
                double h = sc.Terrain(sx, sy);
                res.MinDepthM = Math.Min(res.MinDepthM, -h); res.MaxLandM = Math.Max(res.MaxLandM, h);
                if (double.IsNaN(res.GroundS) && h > -MinDepthM) res.GroundS = now;
                if (Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy)) < HitNm) { res.HitS = now; return res; }
                // the plot
                double px = frozen ? mx : tx, py = frozen ? my : ty;
                double pdx = px - sx, pdy = py - sy;
                var e = new EvTorp { TrackId = 101, BrgDeg = Brg(pdx, pdy), DistNm = Math.Sqrt(pdx * pdx + pdy * pdy), Stale = frozen };
                double want = frozen ? (st.CourseDeg ?? hdg) : EvasionMath.CourseAwayMulti(hdg, e, null, new List<EvTorp> { e }, st);
                double course = want;
                var own = new GeoPt(sy / 60.0, sx / 60.0);
                if (guarded)
                {
                    int before = res.TerrainCalls;
                    EvGuardPick gp = EvasionCourseGuard.Steer(guard, now, own, want, hdg, e.BrgDeg, null, null, hf);
                    if (gp.Checked) res.Checks++;
                    course = gp.CourseDeg;
                    if (gp.Why != "clear")
                    {
                        st.Side = EvasionMath.Diff(e.BrgDeg, gp.CourseDeg) >= 0 ? 1 : -1;
                        st.CourseDeg = gp.CourseDeg;
                    }
                    if (gp.Changed) { res.Whys.Add(gp.Why); }
                    if (!double.IsNaN(lastWant) && Math.Abs(EvasionMath.Diff(lastWant, want)) >= EvasionCourseGuard.WantMoveDeg) res.Shifts++;
                    if (double.IsNaN(lastWant) || Math.Abs(EvasionMath.Diff(lastWant, want)) >= EvasionCourseGuard.WantMoveDeg) lastWant = want;
                    if (gp.Checked && res.TerrainCalls - before != gp.Probes) { res.Err = sc.Name + ": probes " + gp.Probes + " vs calls " + (res.TerrainCalls - before); return res; }
                }
                else if (frozen) st.CourseDeg = want;
                res.Courses.Add(course);
                esc = course;
            }
            return res;
        }

        private static string F(double v) { return double.IsNaN(v) ? "na" : v.ToString("0", CultureInfo.InvariantCulture); }

        public static string SelfTest()
        {
            double s45 = Math.Sin(Math.PI / 4);
            // B1: the coast is the half-plane d >= 7 nm in the direction 045 (land), 0.5 nm of shoal in front of it
            var b1 = new Scn
            {
                Name = "B1", DistNm = 3.5, BrgDeg = 225, RouteCourse = 120, FreezeS = 25, VKn = 27,
                Terrain = (x, y) => { double d = (x + y) * s45; return d >= 7 ? 20 : d >= 6.5 ? -8 : -200; }
            };
            var ctl = Run(b1, false);
            var grd = Run(b1, true);
            if (grd.Err != null) return grd.Err;
            Console.WriteLine("t272 scenario B1 min_depth_m=" + F(grd.MinDepthM) + " control_ground_s=" + F(ctl.GroundS) + " control_hit_s=" + F(ctl.HitS) + " hit_s=" + F(grd.HitS)
                + " checks=" + grd.Checks + " terrain_calls=" + grd.TerrainCalls + " whys=" + string.Join(",", grd.Whys));
            if (double.IsNaN(ctl.GroundS) || ctl.GroundS > 1800) return "B1: the control without the guard must ground (N1), ground_s=" + F(ctl.GroundS);
            if (!double.IsNaN(grd.GroundS)) return "B1: the guarded ship touched the shoal at " + grd.GroundS + " s";
            if (grd.MinDepthM < MinDepthM) return "B1: the minimum depth under the guarded ship is " + F(grd.MinDepthM) + " m";
            if (grd.Whys.Count == 0) return "B1: the guard never changed the course";
            if (!double.IsNaN(grd.HitS) && grd.HitS < (double.IsNaN(ctl.HitS) ? double.PositiveInfinity : ctl.HitS) && grd.HitS < ctl.GroundS)
                return "B1: the guarded ship was hit at " + F(grd.HitS) + " s, before the control grounded (" + F(ctl.GroundS) + " s)";

            // B2: a strait: land everywhere beyond 3.5 nm of the start except the lane 030-050
            var b2 = new Scn
            {
                Name = "B2", DistNm = 3.5, BrgDeg = 225, RouteCourse = 120, FreezeS = 25, VKn = 27, EndS = 400,
                Terrain = (x, y) => { double r = Math.Sqrt(x * x + y * y); if (r <= 3.5) return -200; double b = Brg(x, y); return b >= 30 && b <= 50 ? -200 : 20; }
            };
            var r2 = Run(b2, true);
            if (r2.Err != null) return r2.Err;
            Console.WriteLine("t272 scenario B2 max_land_m=" + F(r2.MaxLandM) + " hit_s=" + F(r2.HitS) + " whys=" + string.Join(",", r2.Whys));
            if (r2.MaxLandM > 0) return "B2: the ship went on land";
            if (r2.Whys.Count == 0) return "B2: no guard decision logged";

            // B3: open sea: the guard changes nothing and reads the terrain only on the schedule
            var b3 = new Scn { Name = "B3", DistNm = 3.5, BrgDeg = 171, RouteCourse = 148, FreezeS = 24, VKn = 27, EndS = 300, Terrain = (x, y) => -200 };
            var free = Run(b3, false);
            var g3 = Run(b3, true);
            if (g3.Err != null) return g3.Err;
            int n = Math.Min(free.Courses.Count, g3.Courses.Count);
            for (int i = 0; i < n; i++)
                if (Math.Abs(EvasionMath.Diff(free.Courses[i], g3.Courses[i])) > 1e-6) return "B3: the guard changed the course at tick " + i;
            int limit = (int)(g3.Ticks / EvasionCourseGuard.RecheckS + g3.Shifts + 2) * 130;
            Console.WriteLine("t272 scenario B3 ticks=" + g3.Ticks + " checks=" + g3.Checks + " terrain_calls=" + g3.TerrainCalls + " limit=" + limit);
            if (g3.TerrainCalls > limit) return "B3: " + g3.TerrainCalls + " terrain reads, limit " + limit;
            if (g3.Whys.Count != 0) return "B3: a non-clear decision in the open sea";
            return null;
        }
    }
}
