using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-278 (ADR-0111): synthetic replay of N1 of run 16 (0.30.19): the ship came out of the evasion at 12.677 / 43.443, the first point of the player's route
    /// lay behind a small island right on the straight way (course 244). A flat 1 s model: 10 kn, 1.3 deg/s turn, the game drops a waypoint within 0.1 nm.
    /// Without a detour the ship touches land; with the detour (Plan, then Step each tick as the game wiring does) the depth stays at 15 m or more
    /// and the ship reaches the player's point.
    /// </summary>
    internal static class EvasionReturnScenarios
    {
        private const double KnS = 10 / 3600.0, TurnDegS = 1.3, ReachNm = 0.1;

        private sealed class Res { public double MinDepthM = double.PositiveInfinity; public bool Ground, Arrived; public int Ticks; public string End = "none"; }

        private static GeoPt G(double xNm, double yNm) { return new GeoPt(yNm / 60.0, xNm / 60.0); }
        private static double[] W(GeoPt g) { return new[] { g.Lat, g.Lon }; }

        private static Res Sail(Func<double, double, double> terrain, GeoPt target, List<GeoPt> detour)
        {
            var res = new Res();
            double x = 0, y = 0, hdg = 100;   // out of the evasion on another course
            var wps = new List<double[]>();
            foreach (var d in detour) wps.Add(W(d));
            wps.Add(W(target));
            EvDetour det = null;
            if (detour.Count > 0)
            {
                det = new EvDetour { PlacedS = 0, NextCheckS = EvasionReturnGuard.RecheckS, PlayerRoute = new List<double[]> { W(target) } };
                foreach (var d in detour) det.Points.Add(W(d));
            }
            Func<GeoPt, double> hf = g => terrain(g.Lon * 60, g.Lat * 60);
            Func<GeoPt, GeoPt, bool> directClear = (a, b) => { double al; int p; return double.IsPositiveInfinity(EvasionReturnGuard.SegmentBlockNm(a, b, hf, out al, out p)); };
            for (int sec = 1; sec <= 3000 && wps.Count > 0; sec++)
            {
                res.Ticks = sec;
                var own = G(x, y);
                var w = wps[0];
                double brg = AswMath.BearingDeg(own, new GeoPt(w[0], w[1]));
                double df = EvasionMath.Diff(hdg, brg);
                hdg = EvasionMath.Norm360(hdg + Math.Max(-TurnDegS, Math.Min(TurnDegS, df)));
                x += Math.Sin(hdg * Math.PI / 180) * KnS; y += Math.Cos(hdg * Math.PI / 180) * KnS;
                double h = terrain(x, y);
                res.MinDepthM = Math.Min(res.MinDepthM, -h);
                if (h > -EvasionCourseGuard.MinDepthM) { res.Ground = true; return res; }
                own = G(x, y);
                if (AswMath.DistNm(own, new GeoPt(w[0], w[1])) <= ReachNm) wps.RemoveAt(0);   // the game drops a reached waypoint
                if (wps.Count == 0) { res.Arrived = true; break; }
                if (det == null) continue;
                string s = EvasionReturnGuard.Step(det, own, wps, sec, directClear);
                if (s == "shortcut" || s == "shortcut:passed") { wps = EvasionReturnGuard.StripDetour(wps, det); if (wps.Count == 0) wps = new List<double[]>(det.PlayerRoute); det = null; res.End = s; }
                else if (s == "restack") { wps.RemoveAt(0); det.Points.RemoveAt(0); }
                else if (s.StartsWith("end:")) { res.End = s; det = null; }
            }
            if (wps.Count == 0) res.Arrived = true;
            return res;
        }

        public static string SelfTest()
        {
            // the island: r = 0.4 nm, 3 nm along 244 from the ship; the player's point 6 nm along 244
            double a = 244 * Math.PI / 180;
            double ix = 3 * Math.Sin(a), iy = 3 * Math.Cos(a);
            var target = G(6 * Math.Sin(a), 6 * Math.Cos(a));
            Func<double, double, double> terrain = (x, y) => Math.Sqrt((x - ix) * (x - ix) + (y - iy) * (y - iy)) <= 0.4 ? 5 : -200;
            Func<GeoPt, double> hf = g => terrain(g.Lon * 60, g.Lat * 60);

            var direct = Sail(terrain, target, new List<GeoPt>());
            var plan = EvasionReturnGuard.Plan(G(0, 0), target, hf);
            if (plan.Why != "detour" || plan.Points.Count < 1) return "N1: a detour expected, got " + plan.Why;
            var det = Sail(terrain, target, plan.Points);
            Console.WriteLine("t278 scenario N1 direct_ground=" + (direct.Ground ? 1 : 0) + " detour_min_depth_m=" + det.MinDepthM.ToString("0", CultureInfo.InvariantCulture)
                + " pts=" + plan.Points.Count + " path_nm=" + plan.PathNm.ToString("0.0", CultureInfo.InvariantCulture) + " probes=" + plan.Probes + " end=" + det.End);
            if (!direct.Ground) return "N1: the control without a detour must touch the island";
            if (det.Ground || det.MinDepthM < EvasionCourseGuard.MinDepthM) return "N1: the detour ship touched ground, min depth " + det.MinDepthM;
            if (!det.Arrived) return "N1: the ship did not reach the player's point";
            return null;
        }
    }
}
