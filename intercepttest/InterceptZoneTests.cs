using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-169 / ADR-0067: the enemy weapon zones of the intercept (InterceptThreatMath, the controller on the fake game).</summary>
    internal static class InterceptZoneTests
    {
        private sealed class Rig
        {
            public InterceptFakeGame G = new InterceptFakeGame();
            public UnitSteering S = new UnitSteering();
            public IcParams P = new IcParams();
            public InterceptRoute R;
            public InterceptController C;

            public Rig(FakeLand land = null)
            {
                if (land != null) G.Height = land.Height;
                R = new InterceptRoute(g => G.Height(g), a => a());
                C = new InterceptController(G, S, R, P);
            }

            public string Start(int target, out string ack) { return C.Start(InterceptFakeGame.OwnUid, target, out ack); }
            public void Pump(int frames = 120) { for (int i = 0; i < frames; i++) C.FramePump(); }
            public void Run(int seconds) { for (int i = 0; i < seconds; i++) { G.Advance(1); C.FramePump(); C.Tick(); } }
            public int Count(string prefix) { int n = 0; foreach (string c in G.Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public bool Logged(string s) { foreach (string l in G.Logs) if (l.Contains(s)) return true; return false; }
            public bool Event(string s) { foreach (string e in G.Events) if (e.Contains(s)) return true; return false; }
            public string Dump() { return "calls=[" + string.Join(",", G.Calls) + "] logs=[" + string.Join(" | ", G.Logs) + "]"; }
        }

        private static void Req(bool ok, string msg) { if (!ok) throw new Exception(msg); }

        private static double OwnN(Rig r) { return (r.G.OwnU.Lat - FakeLand.Lat0) * 60.0; }

        public static string Math_() { return InterceptThreatMath.SelfTest(); }

        public static string Menu()
        {
            var r = new Rig();
            r.G.AddTrack(1, 30, 0, 0, 0);
            r.G.AddThreat(50, 15, 0, 5);
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && m.Enabled, "enabled item expected");
            Req(m.Label.Contains("⚠") && m.Label.Contains("Osa") && m.Label.Contains("SS-N-2") && m.Label.Contains("43"), "zone in the label (10 nm at 14 kn = 43 min): " + m.Label);
            Req(r.Logged("phase=menu threat=50") && r.Logged("range_nm=5"), "menu zone log: " + r.Dump());
            Req(r.G.Calls.Count == 0, "the menu orders nothing");
            // a zone abeam of the route is no warning
            r.G.ThreatList[0].Lon = FakeLand.Lon0 + 8 / 60.0;
            m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(!m.Label.Contains("⚠"), "abeam: " + m.Label);
            // off: the game is not even asked
            r.G.ThreatList[0].Lon = FakeLand.Lon0;
            r.P.Avoid = "off"; r.G.ThreatCalls = 0;
            m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(!m.Label.Contains("⚠") && r.G.ThreatCalls == 0, "off: " + m.Label);
            // the target's own zone is shown, marked as the target
            r.P.Avoid = "warn";
            r.G.ThreatList.Clear(); r.G.AddThreat(1, 30, 0, 25, 0, 0, "Petya", null);
            m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m.Label.Contains("Petya") && m.Label.Contains("⚠") && r.Logged("is_target=true"), "target zone: " + m.Label);
            return null;
        }

        public static string PlanWarn()
        {
            var r = new Rig();
            r.G.AddTrack(1, 18, 0, 0, 0);   // 18 nm: a straight leg, ordered at once
            r.G.AddThreat(50, 10, 0, 3);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            Req(r.Count("Route:first:1") == 1, "straight route in warn mode: " + r.Dump());
            Req(ack.Contains("\"enemy_zone\"") && ack.Contains("\"zones\":[{\"track_id\":50") && ack.Contains("\"weapon\":\"SS-N-2\""), "ack: " + ack);
            Req(r.Logged("phase=plan threat=50") && r.Event("intercept_zone"), "plan log / event: " + r.Dump());
            Req(r.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"zones\":[{\"track_id\":50"), "status zones");
            IList<IcView> v = r.C.View();
            Req(v.Count == 1 && v[0].Label.Contains("⚠"), "line label: " + (v.Count > 0 ? v[0].Label : "none"));
            // warn never stops the unit
            r.Run(1200);
            Req(r.Count("Telegraph:0") == 0, "warn mode stopped the unit: " + r.Dump());
            return null;
        }

        public static string Detour()
        {
            var r = new Rig();
            r.P.Avoid = "detour";
            r.G.AddTrack(1, 30, 0, 0, 0);
            r.G.AddThreat(50, 15, 0, 3);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Pump(300);
            Req(r.Logged("[Intercept] detour uid=11 zones=50"), "detour log: " + r.Dump());
            Req(r.Count("Route:") == 1, "one route: " + r.Dump());
            var pts = r.G.OwnU.Waypoints;
            Req(pts.Count >= 2, "a detour has turns: " + pts.Count);
            var zone = new List<IcThreat> { r.G.ThreatList[0] };
            GeoPt a = new GeoPt(r.G.OwnU.Lat, r.G.OwnU.Lon);
            foreach (GeoPt b in pts) { Req(!InterceptThreatMath.LegCrosses(a, b, zone, 1.5), "a leg crosses the zone"); a = b; }
            Req(!r.Logged("phase=plan threat=50"), "the planned route must not enter the zone: " + r.Dump());
            // a zone that covers the own unit is not routed around (warning only)
            var r2 = new Rig(); r2.P.Avoid = "detour";
            r2.G.AddTrack(1, 18, 0, 0, 0); r2.G.AddThreat(51, 1, 0, 4);
            Req(r2.Start(1, out ack) == null, "start 2");
            Req(r2.Logged("detour skips uid=11 zones=51") && r2.Count("Route:first:1") == 1 && ack.Contains("\"enemy_zone\""), "inside: " + r2.Dump());
            return null;
        }

        public static string DetourNone()
        {
            // a channel of 6 nm between two land masses, the zone fills it: no way around -> the route around land only + the warning
            var land = new FakeLand().Rect(10, 3, 50, 100).Rect(10, -100, 50, -3);
            var r = new Rig(land);
            r.P.Avoid = "detour";
            r.G.AddTrack(1, 60, 0, 0, 0);
            r.G.AddThreat(50, 30, 0, 1);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Pump(600);
            Req(r.Logged("detour none uid=11"), "detour none: " + r.Dump());
            Req(r.Count("Route:first") == 1, "routed through the channel: " + r.Dump());
            Req(r.Logged("phase=plan threat=50"), "the warning stays: " + r.Dump());
            Req(r.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"state\":\"active\""), "active");
            return null;
        }

        public static string Hold()
        {
            var r = new Rig();
            r.P.OnEntry = "hold";
            r.G.AddTrack(1, 18, 0, 0, 0);
            r.G.AddThreat(50, 9, 0, 5);   // the edge at 4 nm: 14 kn -> ~1030 s
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Run(1100);
            Req(r.Count("Telegraph:0") == 1 && r.Logged("zone stop uid=11 mode=hold threat=50"), "hold: " + r.Dump());
            Req(r.Event("\"state\":\"held\""), "held event");
            Req(r.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"held\":true"), "status held");
            double n = OwnN(r);
            Req(n < 4 && n > 3.5, "stopped before the zone: " + n);
            IList<IcView> v = r.C.View();
            Req(v.Count == 1 && v[0].Style == "grey" && v[0].Label.Contains("Osa"), "held line: " + (v.Count > 0 ? v[0].Label + "/" + v[0].Style : "none"));
            r.Run(60);
            Req(System.Math.Abs(OwnN(r) - n) < 1e-9 && r.Count("Telegraph:") == 1, "stays stopped: " + r.Dump());
            // the zone leaves: the step comes back, the route is planned again
            r.G.ThreatList[0].Lon = FakeLand.Lon0 + 30 / 60.0;
            r.Run(10);
            Req(r.Count("Telegraph:3") == 1 && r.Logged("zone resume uid=11 tg=3") && r.Event("zone_clear"), "resume: " + r.Dump());
            Req(r.Logged("replan uid=11 reason=zone_clear"), "replan after resume: " + r.Dump());
            r.Run(600);
            Req(OwnN(r) > n + 1, "moves on: " + OwnN(r));
            return null;
        }

        public static string HoldPlayer()
        {
            var r = new Rig();
            r.P.OnEntry = "hold";
            r.G.AddTrack(1, 18, 0, 0, 0);
            r.G.AddThreat(50, 9, 0, 5);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Run(1100);
            Req(r.Count("Telegraph:0") == 1, "held");
            r.G.OwnU.Telegraph = 2;   // the player orders a step himself
            r.Run(3);
            Req(r.C.ActiveCount == 0 && r.Event("player_changed"), "player_changed: " + r.Dump());
            return null;
        }

        public static string Abort()
        {
            var r = new Rig();
            r.P.OnEntry = "abort";
            r.G.AddTrack(1, 18, 0, 0, 0);
            r.G.AddThreat(50, 9, 0, 5);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Run(1100);
            Req(r.Count("Telegraph:0") == 1 && r.C.ActiveCount == 0 && r.Event("\"state\":\"aborted\",\"reason\":\"enemy_zone\""), "abort: " + r.Dump());
            Req(r.Logged("result uid=11 state=aborted reason=enemy_zone"), "result log");
            return null;
        }

        public static string TargetAndInside()
        {
            // only the target is armed: the intercept goes on (entering the target's zone is the intercept itself)
            var r = new Rig();
            r.P.OnEntry = "hold";
            r.G.AddTrack(1, 18, 0, 0, 0);
            r.G.AddThreat(1, 18, 0, 10, 0, 0, "Petya", null);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            Req(ack.Contains("\"target\":true"), "the target zone is listed: " + ack);
            r.Run(900);
            Req(r.Count("Telegraph:0") == 0, "the target zone stopped the unit: " + r.Dump());
            // the unit is in a zone at the start: no stop for it (stopping inside does not help)
            var r2 = new Rig();
            r2.P.OnEntry = "hold";
            r2.G.AddTrack(1, 18, 0, 0, 0);
            r2.G.AddThreat(50, 0, 1, 4);
            Req(r2.Start(1, out ack) == null, "start 2");
            Req(ack.Contains("\"inside\":true"), "inside listed: " + ack);
            r2.Run(120);
            Req(r2.Count("Telegraph:0") == 0, "inside at start stopped the unit: " + r2.Dump());
            return null;
        }

        public static string Track()
        {
            // a zone that turns up after the plan is reported by the 5 s check; a replan in detour mode routes around it
            var r = new Rig();
            r.P.Avoid = "detour";
            r.G.AddTrack(1, 30, 0, 0, 0);
            string ack;
            Req(r.Start(1, out ack) == null, "start");
            r.Pump(200);
            int routes = r.Count("Route:");
            r.G.AddThreat(50, 18, 0, 3);
            r.Run(6);
            Req(r.Logged("phase=track threat=50"), "track phase: " + r.Dump());
            r.Run(30);
            r.G.Tracks[1].Lon = FakeLand.Lon0 + 3 / 60.0;   // the target jumps 3 nm east: the intercept point moves -> replan
            r.Run(40);
            r.Pump(300);
            Req(r.Logged("replan uid=11 reason=ip_shift") && r.Count("Route:") > routes, "replan: " + r.Dump());
            Req(r.Logged("detour uid=11 zones=50"), "the replan routes around the new zone: " + r.Dump());
            var zone = new List<IcThreat> { r.G.ThreatList[0] };
            GeoPt a = new GeoPt(r.G.OwnU.Lat, r.G.OwnU.Lon);
            foreach (GeoPt b in r.G.OwnU.Waypoints) { Req(!InterceptThreatMath.LegCrosses(a, b, zone, 1.5), "a new leg crosses the zone"); a = b; }
            Req(r.Logged("zone clear uid=11"), "zone clear after the detour: " + r.Dump());
            return null;
        }

        public static string Submerged()
        {
            var r = new Rig();
            r.G.OwnU.IsSub = true; r.G.OwnU.Submerged = true;
            r.G.AddTrack(1, 18, 0, 0, 0);
            r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(r.G.LastSubmerged == true, "submerged domain");
            r.G.OwnU.Submerged = false;
            r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(r.G.LastSubmerged == false, "surfaced domain");
            return null;
        }
    }
}
