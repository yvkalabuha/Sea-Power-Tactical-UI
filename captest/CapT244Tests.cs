using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-244 (ADR-0102): a small CAP zone is a real circle shared by the birds of the group (phase and altitude steps), not a one-point orbit.
    // Scenarios of `captest t244 <name>`.
    internal static class CapT244Tests
    {
        private static readonly string[] Names = { "turn_radius", "circle_shape", "phase", "altitude", "big_zone", "flown_circle", "k_auto_log" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static CapZone Zone(double latNm, double lonNm)
        {
            return new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + latNm / 60.0, 20.0 + lonNm / 60.0 } };
        }

        private static double Nm(GeoPt a, GeoPt b) { return AswMath.DistNm(a, b); }

        public static string Run(string only = null)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = Scenario(n);
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario " + only;
        }

        private static string Scenario(string n)
        {
            var k = new Chk(); var p = new CapParams();
            switch (n)
            {
                case "turn_radius":
                {
                    double r = CapMath.TurnRadiusNm(300, 30);
                    k.Is("300 kn / 30 deg bank about 2.2 nm: " + r, r > 2.0 && r < 2.5);
                    k.Is("faster -> wider", CapMath.TurnRadiusNm(450, 30) > r);
                    k.Is("steeper -> tighter", CapMath.TurnRadiusNm(300, 45) < r);
                    k.Is("bad input is safe", CapMath.TurnRadiusNm(0, 30) > 0 && CapMath.TurnRadiusNm(300, 0) > 0);
                    break;
                }
                case "circle_shape":
                {
                    var z = Zone(1, 1);
                    var ls = CapMath.Lanes(z, 3, p, 19);
                    double rt = CapMath.TurnRadiusNm(p.CircleSpeedKn, p.CircleBankDeg);
                    k.Is("3 lanes", ls.Count == 3);
                    var c = new GeoPt((z.Rect[0] + z.Rect[2]) / 2, (z.Rect[1] + z.Rect[3]) / 2);
                    foreach (var l in ls)
                    {
                        k.Is("circle, not an orbit point", l.Circle && !l.Orbit && l.Track.Count >= 4 && l.Track.Count <= 8);
                        k.Is("radius >= turn radius " + l.RadiusNm, l.RadiusNm >= rt - 1e-9);
                        foreach (var pt in l.Track) k.Is("point on the circle", Math.Abs(Nm(pt, c) - l.RadiusNm) < 0.01);
                        var route = CapMath.PatrolRoute(l);
                        k.Is("closed route", route.Count > l.Track.Count && Nm(route[0], route[route.Count - 1]) < 1e-9);
                        k.Is("adjacent points apart", Nm(l.Track[0], l.Track[1]) > 1);
                    }
                    break;
                }
                case "phase":
                {
                    var z = Zone(1, 1);
                    foreach (int cnt in new[] { 2, 3, 4 })
                    {
                        var ls = CapMath.Lanes(z, cnt, p, 19);
                        var c = new GeoPt((z.Rect[0] + z.Rect[2]) / 2, (z.Rect[1] + z.Rect[3]) / 2);
                        var angs = new List<double>();
                        foreach (var l in ls)
                        {
                            double a = Math.Atan2((l.Track[0].Lon - c.Lon), (l.Track[0].Lat - c.Lat)) * 180 / Math.PI;
                            angs.Add((a + 360) % 360);
                        }
                        double minStart = double.MaxValue;
                        for (int i = 0; i < cnt; i++) for (int j = i + 1; j < cnt; j++) minStart = Math.Min(minStart, Nm(ls[i].Track[0], ls[j].Track[0]));
                        k.Is("N=" + cnt + " start points apart: " + minStart, minStart >= 2 * ls[0].RadiusNm * Math.Sin(Math.PI / cnt) - 0.01);
                        for (int i = 1; i < cnt; i++)
                        {
                            double d = ((angs[i] - angs[0]) % 360 + 360) % 360;
                            k.Is("N=" + cnt + " lane " + i + " is " + i * 360.0 / cnt + " deg after lane 0: " + d, Math.Abs(d - i * 360.0 / cnt) < 0.5);
                        }
                    }
                    break;
                }
                case "altitude":
                {
                    var z = Zone(1, 1);
                    var ls = CapMath.Lanes(z, 3, p, 19);
                    k.Is("step 1000 ft by default", p.CircleAltStepFt == 1000 && ls[1].AltFt - ls[0].AltFt == 1000 && ls[2].AltFt - ls[1].AltFt == 1000);
                    k.Is("base is a real altitude", ls[0].AltFt > 0);
                    var p2 = new CapParams { PatrolAltFt = 12000, CircleAltStepFt = 500 };
                    var l2 = CapMath.Lanes(z, 3, p2, 19);
                    k.Is("base = PatrolAltFt, step from the key", l2[0].AltFt == 12000 && l2[1].AltFt == 12500 && l2[2].AltFt == 13000);
                    var p3 = new CapParams { CircleAltStepFt = 0 };
                    var l3 = CapMath.Lanes(z, 2, p3, 19);
                    k.Is("step 0 = one altitude", l3[0].AltFt == l3[1].AltFt);
                    break;
                }
                case "big_zone":
                {
                    var ls = CapMath.Lanes(Zone(30, 30), 2, p, 19);
                    k.Is("a large zone keeps racetracks", !ls[0].Circle && !ls[0].Orbit && ls[0].Track.Count == 4 && ls[0].AltFt == 0);
                    var thin = CapMath.Lanes(Zone(30, 3), 1, p, 50);
                    k.Is("a thin covered zone is a circle", thin[0].Circle);
                    var thin2 = CapMath.Lanes(Zone(60, 3), 1, p, 5);
                    k.Is("thin but not covered: racetrack", !thin2[0].Circle);
                    break;
                }
                case "flown_circle": return FlownCircle();
                case "k_auto_log": return KAutoLog();
            }
            return k.Err;
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public Rig() { C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Order(int n, int kk)
            {
                return C.Order(CapT179Tests.J("{\"group\":\"ПВО-1\",\"rect\":[10.5,11,10.516667,11.016667],\"on_station\":" + n + (kk >= 0 ? ",\"ready\":" + kk : "") + "}"));
            }
            public CapGroupView Gv() { return C.View().Groups[0]; }
        }

        private static string FlownCircle()
        {
            var k = new Chk(); var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1,2,3]}"));
            string a = r.Order(3, 0);
            k.Is("order ok " + a, !a.StartsWith("!"));
            var birds = new[] { r.G.Bird(1), r.G.Bird(2), r.G.Bird(3) };
            for (int i = 0; i < 3000; i++)
            {
                r.Step(1);
                foreach (var b in birds) b.ReserveNm = 100000;
                bool all = true;
                foreach (var b in birds) all &= b.State == "Air";
                if (all && r.Gv().Members.Count == 3 && r.Gv().Members.TrueForAll(m => m.Where == "zone")) break;
            }
            var gv = r.Gv();
            k.Is("all three on station", gv.Members.Count == 3 && gv.Members.TrueForAll(m => m.Where == "zone"));
            k.Is("lanes are circles", gv.Lanes != null && gv.Lanes.Count == 3 && gv.Lanes[0].Circle);
            var zc = new GeoPt(10.5 + 1 / 120.0, 11 + 1 / 120.0);
            double rad = gv.Lanes[0].RadiusNm;
            int patrols0 = r.G.Patrols, log0 = r.G.Logs.Count;
            var visited = new bool[3, gv.Lanes[0].Track.Count];
            double minD = double.MaxValue, maxR = 0, minAlt = double.MaxValue; int steps = 0;
            for (int i = 0; i < 1500; i++)
            {
                r.Step(1); steps++;
                foreach (var b in birds) b.ReserveNm = 100000;
                for (int u = 0; u < 3; u++)
                {
                    var pos = new GeoPt(birds[u].Lat, birds[u].Lon);
                    if (i >= 400) maxR = Math.Max(maxR, Nm(pos, zc));   // after the first approach to the start point
                    for (int v = 0; v < visited.GetLength(1); v++) if (Nm(pos, gv.Lanes[u].Track[v]) < 0.3) visited[u, v] = true;
                    for (int w = u + 1; w < 3; w++) minD = Math.Min(minD, Nm(pos, new GeoPt(birds[w].Lat, birds[w].Lon)));
                }
            }
            for (int u = 0; u < 3; u++) for (int v = 0; v < visited.GetLength(1); v++) k.Is("bird " + u + " flew point " + v, visited[u, v]);
            // the phase held in flight depends on when each bird arrives at its start point (the altitude step keeps them apart), so only a sanity bound here
            k.Is("birds never on top of each other " + minD.ToString("0.00"), minD > 0.2);
            k.Is("stays on the circle " + maxR.ToString("0.00"), maxR < rad + 0.3);
            double lapS = 2 * Math.PI * rad / 420.0 * 3600.0;
            double laps = steps / lapS;
            int reissues = r.G.Patrols - patrols0;
            k.Is("re-issues " + reissues + " <= 1 per bird per lap (laps " + laps.ToString("0.0") + ")", reissues <= 3 * Math.Ceiling(laps));
            k.Is("altitudes differ by 1000 ft", r.G.Bird(2).AltFt - r.G.Bird(1).AltFt == 1000 && r.G.Bird(3).AltFt - r.G.Bird(2).AltFt == 1000);
            bool gapAfter = false;
            for (int i = log0; i < r.G.Logs.Count; i++) gapAfter |= r.G.Logs[i].Contains("warn continuity_gap");
            k.Is("no continuity_gap once the full group is on the circle", !gapAfter);
            k.Is("lanes log says circle", r.G.LogHas("circle r_nm="));
            return k.Err;
        }

        // The customer's answer: K=auto never lifts the relief from free birds that are not in the group; the log says so.
        private static string KAutoLog()
        {
            var k = new Chk(); var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1,2,3]}"));
            string a = r.Order(2, -1);
            k.Is("order ok " + a, !a.StartsWith("!"));
            r.Step(5);
            k.Is("log line about own birds only", r.G.LogHas("relief k=auto") && r.G.LogHas("own birds only"));
            return k.Err;
        }
    }
}
