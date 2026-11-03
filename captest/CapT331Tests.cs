using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-331: the orbit of a spare CAP bird (no slot) is a real circle that is re-issued at <= 1 point left; the one-point route of the old orbit was finished
    // at each pass and the game said "We're on position" every time. Scenarios of `captest t331 <name>`.
    internal static class CapT331Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("orbit_route") && (e = OrbitRoute()) != null) return "orbit_route: " + e;
            if (want("racetrack_unchanged") && (e = Racetrack()) != null) return "racetrack_unchanged: " + e;
            if (want("orbit_reissue") && (e = OrbitReissue()) != null) return "orbit_reissue: " + e;
            if (only != null && only != "orbit_route" && only != "racetrack_unchanged" && only != "orbit_reissue") return "unknown case " + only;
            return null;
        }

        private static string OrbitRoute()
        {
            var c = new GeoPt(10.6, 11.3);
            var lane = new CapLane { Slot = -1, Orbit = true, Center = c, Track = new List<GeoPt> { c } };
            var p = new CapParams();
            var r = CapMath.PatrolRoute(lane, p);
            if (r.Count < 5) return "route has " + r.Count + " points";
            double rad = Math.Max(1, CapMath.TurnRadiusNm(p.CircleSpeedKn, p.CircleBankDeg));
            for (int i = 0; i < 4; i++) if (Math.Abs(AswMath.DistNm(c, r[i]) - rad) > 0.05) return "point " + i + " is " + AswMath.DistNm(c, r[i]) + " nm from the centre, radius " + rad;
            if (Math.Abs(AswMath.BearingDeg(c, r[0])) > 0.5 && Math.Abs(AswMath.BearingDeg(c, r[0]) - 360) > 0.5) return "the circle does not start in the north: " + AswMath.BearingDeg(c, r[0]);
            if (Math.Abs(AswMath.BearingDeg(c, r[1]) - 90) > 0.5) return "the circle is not clockwise: " + AswMath.BearingDeg(c, r[1]);
            if (AswMath.DistNm(r[0], r[r.Count - 1]) > 1e-6) return "the route is not closed";
            if (CapMath.PatrolRoute(lane).Count < 5 || AswMath.DistNm(c, CapMath.PatrolRoute(lane)[0]) < 0.9) return "the old overload lost the circle";
            return null;
        }

        private static string Racetrack()
        {
            var p = new CapParams();
            var z = new CapZone { Rect = new[] { 10.5, 11, 10.83333, 11.66667 } };
            var lanes = CapMath.Lanes(z, 2, p);
            foreach (var l in lanes)
            {
                var a = CapMath.PatrolRoute(l); var b = CapMath.PatrolRoute(l, p);
                if (a.Count != b.Count) return "the route of lane " + l.Slot + " changed";
                for (int i = 0; i < a.Count; i++) if (AswMath.DistNm(a[i], b[i]) > 1e-9) return "the route point " + i + " changed";
            }
            return null;
        }

        private static string OrbitReissue()
        {
            // a group with N = 2 on station, then a third bird that is already airborne is added: no free slot, it holds over the zone centre (a lane with Slot < 0)
            var G = new CapFakeGame(); var own = new AirOwnership(); var p = new CapParams();
            var C = new CapController(G, p, own);
            C.Group(J("{\"op\":\"create\",\"uids\":[1,2]}"));
            C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":2,\"ready\":0}"));
            for (int i = 0; i < 1200; i++) { G.Advance(1); G.Tick(C); }
            var third = G.Bird(3); third.State = "Air"; third.Lat = 10.6; third.Lon = 11.3;
            string add = C.Group(J("{\"op\":\"add\",\"group\":\"ПВО-1\",\"uids\":[3]}"));
            for (int i = 0; i < 60; i++) { G.Advance(1); G.Tick(C); }
            int orbiter = 0;
            foreach (var b in new[] { 1, 2, 3 }) { var fb = G.Bird(b); if (fb != null && fb.Route.Count >= 5 && AswMath.DistNm(fb.Route[0], fb.Route[1]) > 0.5 && fb.Route.Count == 5 && IsCircle(fb)) orbiter = b; }
            if (orbiter == 0) return "no spare bird flies a circle: " + string.Join(" | ", G.Journal.FindAll(j => j.StartsWith("patrol")).ToArray());
            var bird = G.Bird(orbiter);
            // left <= 1: re-issued; left == 2: not
            int n0 = Patrols(G, orbiter);
            G.Advance(1); bird.Idx = bird.Route.Count - 2; G.Tick(C);
            if (Patrols(G, orbiter) != n0) return "re-issued with 2 points left";
            G.Advance(1); bird.Idx = bird.Route.Count - 1; G.Tick(C);
            if (Patrols(G, orbiter) <= n0) return "not re-issued with 1 point left: left=" + G.WaypointsLeft(orbiter) + " n0=" + n0 + " state=" + bird.State + " " + string.Join(" | ", G.Journal.FindAll(j => j.StartsWith("patrol " + orbiter)).ToArray());
            return null;
        }

        private static bool IsCircle(FakeBird b)
        {
            var c = new GeoPt((10.5 + 10.83333) / 2, (11 + 11.66667) / 2);   // the centre of ZA
            return b.Route.Count == 5 && AswMath.DistNm(b.Route[0], b.Route[4]) < 1e-6 && Math.Abs(AswMath.DistNm(c, b.Route[0]) - AswMath.DistNm(c, b.Route[2])) < 0.05 && AswMath.DistNm(c, b.Route[0]) < 5;
        }
        private static int Patrols(CapFakeGame g, int uid) { return g.Journal.FindAll(j => j.StartsWith("patrol " + uid + " ")).Count; }
    }
}
