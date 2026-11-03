using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-257: a patrol bird on station outside combat drops to the patrol step (the game AI leaves the combat step behind). Scenarios of `captest t257 <name>`.
    internal static class CapT257Tests
    {
        private static readonly string[] Names = { "step_pick", "after_combat", "short_combat", "restored", "no_step_up", "skip_states", "lanes_big", "converge_fast", "patrol_keeps", "flip_flop_log", "alert_dash" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

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

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public Rig() { C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public CapGroupView Gv() { return C.View().Groups[0]; }
        }

        private const string Small = "[10.5,11,10.516667,11.016667]";
        private const string Big = "[10.5,11,11.0,11.5]";

        private static Rig OnStation(int n, string rect, Action<Rig> pre, out FakeBird[] birds)
        {
            var r = new Rig();
            if (pre != null) pre(r);
            var ids = new List<string>(); for (int i = 1; i <= n; i++) ids.Add(i.ToString());
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[" + string.Join(",", ids) + "]}"));
            r.C.Order(CapT179Tests.J("{\"group\":\"ПВО-1\",\"rect\":" + rect + ",\"on_station\":" + n + ",\"ready\":0}"));
            birds = new FakeBird[n];
            for (int i = 0; i < n; i++) birds[i] = r.G.Bird(i + 1);
            for (int i = 0; i < 3000; i++)
            {
                r.Step(1);
                foreach (var b in birds) b.ReserveNm = 100000;
                bool all = true;
                foreach (var b in birds) all &= b.State == "Air";
                if (all && r.Gv().Members.Count == n && r.Gv().Members.TrueForAll(m => m.Where == "zone")) break;
            }
            return r;
        }

        private static void Run(Rig r, FakeBird[] birds, int s) { for (int i = 0; i < s; i++) { r.Step(1); foreach (var b in birds) b.ReserveNm = 100000; } }
        private static void SetStep(FakeBird b, int step) { b.Throttle = step; b.SpeedKn = CapFakeGame.StepKn[step - 1]; }

        private static int Lines(Rig r, string uid = null)
        {
            int n = 0;
            foreach (var l in r.G.Logs) if (l.Contains("station speed uid=" + (uid ?? ""))) n++;
            return n;
        }

        private static string Scenario(string n)
        {
            var k = new Chk(); FakeBird[] birds;
            switch (n)
            {
                case "step_pick": return CapMath.SelfTest("math");
                case "after_combat":
                {
                    var r = OnStation(3, Small, null, out birds);
                    SetStep(birds[2], 4); birds[2].Engaged = true;
                    Run(r, birds, 60);
                    k.Is("combat: step stays 4", birds[2].Throttle == 4);
                    k.Is("combat: no line", Lines(r) == 0);
                    birds[2].Engaged = false;
                    Run(r, birds, 31);
                    k.Is("after combat: step 2, got " + birds[2].Throttle, birds[2].Throttle == 2);
                    k.Is("one line, got " + Lines(r), Lines(r) == 1);
                    string line = r.G.Logs.Find(l => l.Contains("station speed uid=")) ?? "";
                    k.Is("line text: " + line, line.Contains("station speed uid=3 step 4->2 ") && line.Contains("reason=after_combat") && line.Contains("src=name") && line.Contains("group=ПВО-1"));
                    k.Is("others untouched", birds[0].Throttle == 2 && birds[1].Throttle == 2);
                    Run(r, birds, 120);
                    k.Is("no new lines", Lines(r) == 1);
                    break;
                }
                case "short_combat":
                {
                    // T-263 (N5): a fight shorter than the check interval still gives after_combat
                    var r = OnStation(3, Small, null, out birds);
                    Run(r, birds, 7);
                    SetStep(birds[2], 4); birds[2].Engaged = true;
                    Run(r, birds, 2);
                    birds[2].Engaged = false;
                    Run(r, birds, 45);
                    k.Is("step 2, got " + birds[2].Throttle, birds[2].Throttle == 2);
                    string line = r.G.Logs.Find(l => l.Contains("station speed uid=3")) ?? "";
                    k.Is("reason after_combat: " + line, line.Contains("reason=after_combat"));
                    break;
                }
                case "restored":
                {
                    var r = OnStation(3, Small, null, out birds);
                    var data = r.C.Save();
                    string err; var back = CapSave.Parse(CapSave.ToJson(data), out err);
                    k.Is("round trip", err == null);
                    SetStep(birds[2], 4);
                    var c2 = new CapController(r.G, new CapParams(), new AirOwnership());
                    c2.Load(back, r.G.NowS);
                    for (int i = 0; i < 90; i++) { r.G.Advance(1); r.G.Tick(c2); foreach (var b in birds) b.ReserveNm = 100000; }
                    k.Is("step 2 after load, got " + birds[2].Throttle, birds[2].Throttle == 2);
                    string line = r.G.Logs.Find(l => l.Contains("station speed uid=")) ?? "";
                    k.Is("restored line: " + line, line.Contains("station speed uid=3 step 4->2 ") && line.Contains("reason=restored"));
                    break;
                }
                case "no_step_up":
                {
                    var r = OnStation(3, Small, null, out birds);
                    SetStep(birds[1], 1);
                    Run(r, birds, 120);
                    k.Is("slower step kept", birds[1].Throttle == 1);
                    k.Is("no line", Lines(r) == 0);
                    break;
                }
                case "skip_states":
                {
                    var r = OnStation(3, Small, x => x.P.StationSpeed = "keep", out birds);
                    SetStep(birds[2], 4);
                    Run(r, birds, 120);
                    k.Is("keep: step untouched", birds[2].Throttle == 4 && Lines(r) == 0);
                    var q = OnStation(3, Small, null, out birds);
                    SetStep(birds[2], 4); birds[2].Evading = true;
                    Run(q, birds, 120);
                    k.Is("evading: step untouched", birds[2].Throttle == 4 && Lines(q) == 0);
                    var h = OnStation(3, Small, null, out birds);
                    SetStep(birds[2], 4); birds[2].Engaged = true;
                    Run(h, birds, 120);
                    k.Is("engaged: step untouched", birds[2].Throttle == 4 && Lines(h) == 0);
                    break;
                }
                case "lanes_big":
                {
                    var r = OnStation(3, Big, null, out birds);
                    k.Is("racetracks, not a circle", !r.Gv().Lanes[0].Circle);
                    SetStep(birds[2], 4);
                    Run(r, birds, 70);
                    k.Is("racetrack bird dropped to 2, got " + birds[2].Throttle, birds[2].Throttle == 2 && Lines(r) == 1);
                    break;
                }
                case "converge_fast":
                {
                    var r = OnStation(3, Small, null, out birds);
                    var gv = r.Gv();
                    var zc = new GeoPt(10.5 + 1 / 120.0, 11 + 1 / 120.0);
                    double rad = gv.Lanes[0].RadiusNm;
                    double[] ph0 = { 0, 25, 50 };
                    SetStep(birds[2], 4);
                    for (int u = 0; u < 3; u++)
                    {
                        double a = ph0[u] * Math.PI / 180;
                        birds[u].Lat = zc.Lat + rad * Math.Cos(a) / 60; birds[u].Lon = zc.Lon + rad * Math.Sin(a) / 60;
                        birds[u].Idx = birds[u].Route.Count;
                    }
                    double lap = CapMath.LapS(rad, 420, r.P);
                    int total = (int)(6.5 * lap), lastTwo = (int)(total - 2 * lap), samples = 0, bad = 0;
                    for (int i = 0; i < total; i++)
                    {
                        Run(r, birds, 1);
                        if (i < lastTwo) continue;
                        var ph = new List<double>(); foreach (var b in birds) ph.Add(CapMath.CirclePhaseDeg(new GeoPt(b.Lat, b.Lon), zc));
                        double md = 360;
                        for (int a = 0; a < 3; a++) for (int b = a + 1; b < 3; b++) md = Math.Min(md, Math.Abs(CapMath.Wrap180(ph[a] - ph[b])));
                        samples++; if (md < 90) bad++;
                    }
                    k.Is("step lowered", birds[2].Throttle == 2);
                    k.Is("min neighbour angle >= 90 in 95% of the last 2 laps: bad " + bad + "/" + samples, bad <= 0.05 * samples);
                    break;
                }
                case "patrol_keeps":
                {
                    var r = OnStation(3, Small, null, out birds);
                    r.G.StaleTelegraph = 4;   // a stale read: the real Patrol would put step 4 back
                    SetStep(birds[2], 4);
                    int j0 = r.G.Journal.Count;
                    for (int i = 0; i < 400; i++)
                    {
                        Run(r, birds, 1);
                        if (r.G.NowS - r.G.LastSetAt < 2 && birds[2].Throttle == 2)
                            birds[2].Idx = birds[2].Route.Count;   // force a re-issue of the route right after the step was set
                    }
                    int sets = 0, keeps = 0, stale = 0;
                    for (int i = j0; i < r.G.Journal.Count; i++)
                    {
                        string l = r.G.Journal[i];
                        if (l == "throttle 3 2") sets++;
                        if (l.StartsWith("patrol_keep 3")) keeps++;
                        if (l.StartsWith("patrol_stale")) stale++;
                    }
                    k.Is("step 2 at the end, got " + birds[2].Throttle, birds[2].Throttle == 2);
                    k.Is("set once, got " + sets, sets == 1);
                    k.Is("Patrol got the keep step, got " + keeps, keeps >= 1);
                    k.Is("no stale put-back, got " + stale, stale == 0);
                    k.Is("one line", Lines(r) == 1);
                    break;
                }
                case "flip_flop_log":
                {
                    var r = OnStation(3, Small, null, out birds);
                    int j0 = r.G.Journal.Count;
                    for (int i = 0; i < 600; i++)
                    {
                        Run(r, birds, 1);
                        if (i % 40 == 5) SetStep(birds[2], 4);   // something keeps putting the old step back
                    }
                    int sets = 0; for (int i = j0; i < r.G.Journal.Count; i++) if (r.G.Journal[i] == "throttle 3 2") sets++;
                    k.Is("the step is lowered again each time, got " + sets, sets >= 3);
                    k.Is("log lines rate-limited (<= 3 in 600 s), got " + Lines(r), Lines(r) >= 1 && Lines(r) <= 3);
                    break;
                }
                case "alert_dash":
                {
                    // the dash of an alert intercept (m.Alert / m.Dash) is never lowered by the station rule
                    var a = new CapT179Tests.Rig(null, "Cheater");
                    a.Create("1,2,3"); a.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":1,\"after\":\"alert\"");
                    a.Step(900);
                    a.G.Contacts.Add(CapT179Tests.Raid(50, 70));
                    for (int i = 0; i < 120 && !a.G.LogHas("alert speed uid="); i++) a.Step(1);
                    k.Is("dash on", a.G.LogHas("alert speed uid="));
                    a.Step(25);   // under one check period + the raid still on
                    k.Is("no station speed line during the intercept", !a.G.LogHas("station speed uid="));
                    break;
                }
            }
            return k.Err;
        }
    }
}
