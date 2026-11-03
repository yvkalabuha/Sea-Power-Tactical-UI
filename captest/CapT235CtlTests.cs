using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-235 (ADR-0097): the controller flies and shows the lanes it computed. Scenarios of `captest t235ctl <name>`.
    internal static class CapT235CtlTests
    {
        private static readonly string[] Names = { "flown_route", "flown_serpentine", "view_lanes", "cover_from_members", "deck_cover", "barrage_flown" };
        private const string Z30 = "\"rect\":[10.5,11,11,11.5]";   // 30 x 30 nm, 75 nm from the deck

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P;
            public CapController C;
            public Rig(Action<CapParams> cfg = null)
            {
                P = new CapParams();
                if (cfg != null) cfg(P);
                C = new CapController(G, P, Own);
            }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Order(int n = 1, int k = 0) { return C.Order(CapT179Tests.J("{\"group\":\"ПВО-1\"," + Z30 + ",\"on_station\":" + n + ",\"ready\":" + k + "}")); }
            public CapGroupView Gv() { return C.View().Groups[0]; }
        }

        public static string Run(string only = null)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "flown_route": e = Flown(null); break;
                    case "flown_serpentine": e = Flown(10); break;
                    case "view_lanes": e = ViewLanes(); break;
                    case "cover_from_members": e = CoverFromMembers(); break;
                    case "deck_cover": e = DeckCover(); break;
                    default: e = BarrageFlown(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario " + only;
        }

        private static double Nm(GeoPt a, GeoPt b) { return AswMath.DistNm(a, b); }

        /// <summary>Distance (nm) from a point to a polyline.</summary>
        private static double DistToLine(GeoPt g, IList<GeoPt> r)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < r.Count; i++)
            {
                double ax = r[i].Lon * 60, ay = r[i].Lat * 60, vx = r[i + 1].Lon * 60 - ax, vy = r[i + 1].Lat * 60 - ay, l2 = vx * vx + vy * vy;
                double px = g.Lon * 60, py = g.Lat * 60;
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * vx + (py - ay) * vy) / l2));
                double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
            }
            return best;
        }

        // The bird on station flies every vertex of the lane and never leaves the closed polyline (before: the 2nd leg was skipped - a diagonal).
        // aam: the longest AAM range of the airborne bird (nm); null = the fake gives none (fallback 10 nm).
        private static string Flown(double? aam)
        {
            var k = new Chk(); var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1]}"));
            string a = r.Order(1, 0);
            k.Is("order ok " + a, !a.StartsWith("!"));
            if (aam.HasValue) r.G.Bird(1).AamMaxNm = aam.Value;
            r.Step(60);
            var b = r.G.Bird(1);
            // fly: wait for the take-off, the climb is the fake's taxi time
            for (int i = 0; i < 1500 && !(b.State == "Air" && r.Gv().Lanes != null && r.Gv().Members.Count > 0 && r.Gv().Members[0].Where == "zone"); i++) { r.Step(1); b.ReserveNm = 100000; }
            k.Is("on station", b.State == "Air" && r.Gv().Members[0].Where == "zone");
            var lane = r.Gv().Lanes[0];
            var route = CapMath.PatrolRoute(lane);
            if (aam.HasValue) k.Is("serpentine (q >= 2)", lane.Legs >= 4);
            else k.Is("racetrack", lane.Legs == 2 && lane.Track.Count == 4);
            var visited = new bool[lane.Track.Count];
            double worst = 0; int steps = 0;
            for (int i = 0; i < 6000 && steps < 6000; i++)
            {
                r.Step(1); steps++; b.ReserveNm = 100000;
                if (b.Rtb) break;
                var pos = new GeoPt(b.Lat, b.Lon);
                for (int v = 0; v < visited.Length; v++) if (Nm(pos, lane.Track[v]) < 0.3) visited[v] = true;
                if (visited[0]) worst = Math.Max(worst, DistToLine(pos, route));   // the approach from the deck to the first point is not the lane
                bool all = true; foreach (bool x in visited) all &= x;
                if (all && i > 100) { /* keep flying one more lap below */ }
                if (all && steps > 3500) break;
            }
            for (int v = 0; v < visited.Length; v++) k.Is("vertex " + v + " flown", visited[v]);
            k.Is("never farther than 0.3 nm from the route: " + worst.ToString("0.00"), worst < 0.3);
            return k.Err;
        }

        private static string ViewLanes()
        {
            var k = new Chk(); var r = new Rig(p => p.CoverAamFrac = 0.2);
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1]}"));
            r.Order(1, 0);
            r.G.Bird(1).AamMaxNm = 25;
            var b = r.G.Bird(1);
            for (int i = 0; i < 1500 && !(b.State == "Air" && r.Gv().Members[0].Where == "zone"); i++) { r.Step(1); b.ReserveNm = 100000; }
            r.Step(5);
            var gv = r.Gv();
            k.Is("cover 5 nm " + gv.CoverNm, Math.Abs(gv.CoverNm - 5) < 1e-9);
            var zone = new CapZone { Rect = gv.Rect };
            var want = CapMath.Lanes(zone, 1, r.P, 5);
            k.Is("view lanes = Lanes(zone, N, P, Rc)", gv.Lanes != null && gv.Lanes.Count == want.Count && gv.Lanes[0].Legs == want[0].Legs && gv.Lanes[0].Track.Count == want[0].Track.Count);
            bool same = gv.Lanes != null && gv.Lanes.Count == 1;
            for (int i = 0; same && i < want[0].Track.Count; i++) same = gv.Lanes[0].Track[i].Lat == want[0].Track[i].Lat && gv.Lanes[0].Track[i].Lon == want[0].Track[i].Lon;
            k.Is("same points", same);
            var ov = CapWindowModel.LanesOf(gv, new CapParams());
            k.Is("overlay takes the view lanes (not the defaults)", ReferenceEquals(ov, gv.Lanes));
            k.Is("state json has cover", r.C.StateJson().Contains("\"cover_nm\":5"));
            return k.Err;
        }

        private static string CoverFromMembers()
        {
            var k = new Chk(); var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1]}"));
            r.Order(1, 0);
            var b = r.G.Bird(1);
            for (int i = 0; i < 1500 && !(b.State == "Air" && r.Gv().Members[0].Where == "zone"); i++) { r.Step(1); b.ReserveNm = 100000; }
            r.Step(3);
            k.Is("airborne without AAM data: fallback 10", r.G.LogHas("src=fallback") && Math.Abs(r.Gv().CoverNm - 10) < 1e-9);
            k.Is("lanes line", r.G.LogHas("lanes group=ПВО-1 n=1 legs=2 spacing_nm=15 cover_nm=10 src=fallback"));
            int patrols = r.G.Patrols;
            b.AamMaxNm = 25;
            r.Step(5);
            k.Is("airborne with 25 nm: Rc 12.5", Math.Abs(r.Gv().CoverNm - 12.5) < 1e-9 && r.G.LogHas("cover_nm=12.5 src=aam"));
            k.Is("same q: the route is not re-issued", r.G.Patrols == patrols);
            return k.Err;
        }

        // Q2: on the deck the cover is by the AAMs of the loadout of the group order (else of the row); airborne - by what it carries
        private static string DeckCover()
        {
            var k = new Chk(); var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1]}"));
            r.Order(1, 0);
            r.Step(1);
            k.Is("auto loadout (AIM-54, 100 nm) x 0.5", Math.Abs(r.Gv().CoverNm - 50) < 1e-9 && r.G.LogHas("cover_nm=50 src=aam"));
            var r2 = new Rig();
            r2.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1]}"));
            string a = r2.C.Loadout(CapT179Tests.J("{\"group\":\"ПВО-1\",\"type\":\"F-14A\",\"loadout\":\"AirToAir\"}"));
            k.Is("loadout ok " + a, !a.StartsWith("!"));
            r2.Order(1, 0);
            r2.Step(1);
            k.Is("loadout AirToAir (AIM-7, 25 nm) x 0.5", Math.Abs(r2.Gv().CoverNm - 12.5) < 1e-9 && r2.G.LogHas("cover_nm=12.5 src=aam"));
            return k.Err;
        }

        // 0.30.12 follow-up: the barrage circle had 4 points, the re-issue at <= 1 left cut the 4th leg. Now the 5th point closes it.
        private static string BarrageFlown()
        {
            var k = new Chk();
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"after\":\"alert\"");
            r.Step(900);
            string a = r.C.ScrambleNow(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            k.Is("barrage ok " + a, a != null && a.Contains("\"ok\":true"));
            var ship = r.G.Ship.Value;
            var pts = new List<GeoPt>();
            for (int i = 0; i < 4; i++) pts.Add(AswMath.Offset(ship, i * 90.0, 10));
            var visited = new bool[4]; int fiveSeen = 0;
            for (int i = 0; i < 400; i++)
            {
                r.Step(1);
                foreach (var x in r.G.All)
                {
                    if (x.State != "Air") continue;
                    x.SpeedKn = 3000;   // a lap inside the 120 s the barrage waits before the clear
                    if (x.Route.Count == 5) fiveSeen++;
                    for (int v = 0; v < 4; v++) if (Nm(new GeoPt(x.Lat, x.Lon), pts[v]) < 0.5) visited[v] = true;
                }
            }
            k.Is("the route has 5 points (closed)", fiveSeen > 0);
            for (int v = 0; v < 4; v++) k.Is("circle point " + v + " flown", visited[v]);
            return k.Err;
        }
    }
}
