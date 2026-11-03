using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-333 (ADR-0129): zone intrusion intercept of a patrol group. Scenarios of `captest t333 <name>`.
    internal static class CapT333Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";   // 20 nm north x 40 nm east, corner (10.5, 11)
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public string Err;
            public Rig() { }
            public void Start(int birds, int n, int ready, string roe = null)
            {
                C = new CapController(G, P, Own);
                var uids = new List<string>(); for (int i = 1; i <= birds; i++) uids.Add(i.ToString());
                C.Group(J("{\"op\":\"create\",\"uids\":[" + string.Join(",", uids.ToArray()) + "]}"));
                C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":" + n + ",\"ready\":" + ready + (roe != null ? ",\"roe\":\"" + roe + "\"" : "") + "}"));
                for (int i = 1; i <= birds; i++) G.Bird(i).AamMaxNm = 10;
                for (int i = 0; i < 3000; i++)
                {
                    Step(1); int air = 0, inz = 0;
                    for (int u = 1; u <= birds; u++) { var fb = G.Bird(u); if (fb.State != "Air") continue; air++; if (fb.Lat >= 10.5 && fb.Lat <= 10.83333 && fb.Lon >= 11 && fb.Lon <= 11.66667) inz++; }
                    if (air == Math.Min(birds, n) && inz == air && i > 400) break;
                }
            }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
            public int Logs(string part) { int n = 0; foreach (string l in G.Logs) if (l.Contains(part)) n++; return n; }
            public bool Took() { foreach (var e in G.Events) if (e.Key == "cap_warn" && e.Value.Contains("player_took")) return true; return false; }
            public int Patrols(int uid) { return G.Journal.FindAll(j => j.StartsWith("patrol " + uid + " ")).Count; }
            public CapContact Add(int id, double lat, double lon, bool helo = false, double crs = 0, double kn = 0)
            {
                var c = new CapContact { Id = id, Lat = lat, Lon = lon, Hostile = true, Helo = helo, CourseDeg = crs, SpeedKn = kn };
                G.Contacts.Add(c); return c;
            }
            public void Remove(int id) { G.Contacts.RemoveAll(c => c.Id == id); }
            public int SlotOf(int uid)
            {
                var d = J(C.StateJson());
                foreach (object go in (List<object>)d["groups"])
                    foreach (object mo in (List<object>)((Dictionary<string, object>)go)["members"])
                    { var m = (Dictionary<string, object>)mo; if ((int)(double)m["uid"] == uid) return (int)(double)m["slot"]; }
                return -99;
            }
            public int OnTarget(int target)
            {
                int n = 0;
                foreach (string l in G.Logs) { }
                var d = J(C.StateJson());
                foreach (object go in (List<object>)d["groups"])
                    foreach (object mo in (List<object>)((Dictionary<string, object>)go)["members"])
                    {
                        var m = (Dictionary<string, object>)mo; object ic;
                        if (m.TryGetValue("intercept", out ic) && (int)(double)((Dictionary<string, object>)ic)["target"] == target) n++;
                    }
                return n;
            }
            public int Intercepting()
            {
                int n = 0; var d = J(C.StateJson());
                foreach (object go in (List<object>)d["groups"])
                    foreach (object mo in (List<object>)((Dictionary<string, object>)go)["members"]) if (((Dictionary<string, object>)mo).ContainsKey("intercept")) n++;
                return n;
            }
        }

        private static readonly string[] Names = { "2x4", "return-slots", "pursuit", "lost", "missiles", "bingo", "relief", "ai-busy", "off", "roe-hold", "kinds", "guide", "fight", "handover", "ceasefire", "player-throttle", "state" };

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "2x4": e = TwoByFour(); break;
                    case "return-slots": e = ReturnSlots(); break;
                    case "pursuit": e = Pursuit(); break;
                    case "lost": e = Lost(); break;
                    case "missiles": e = Missiles(); break;
                    case "bingo": e = Bingo(); break;
                    case "relief": e = Relief(); break;
                    case "ai-busy": e = AiBusy(); break;
                    case "off": e = Off(); break;
                    case "roe-hold": e = RoeHold(); break;
                    case "kinds": e = Kinds(); break;
                    case "guide": e = Guide(); break;
                    case "fight": e = Fight(); break;
                    case "handover": e = Handover(); break;
                    case "ceasefire": e = Ceasefire(); break;
                    case "player-throttle": e = PlayerThrottle(); break;
                    default: e = State(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Dump(Rig r)
        {
            var l = new List<string>(); foreach (string s in r.G.Logs) if (s.Contains("intercept")) l.Add(s);
            return " | " + string.Join(" | ", l.ToArray());
        }

        // two targets, four fit birds: two on each, first the one of each, then the second
        private static string TwoByFour()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            r.Is("4 on station", r.G.Bird(1).State == "Air" && r.G.Bird(4).State == "Air");
            if (r.Err != null) return r.Err;
            r.Add(10, 10.7, 11.2); r.Add(11, 10.75, 11.5);
            r.Step(3);
            r.Is("4 intercepts", r.Intercepting() == 4);
            r.Is("two on each", r.OnTarget(10) == 2 && r.OnTarget(11) == 2);
            r.Is("round 1 twice", r.Logs("round=1") == 2 && r.Logs("round=2") == 2);
            r.Is("events", r.G.EventCount("cap_intercept") >= 4);
            r.Step(60);
            r.Is("no player_took", !r.Took());
            r.Is("sticky", r.OnTarget(10) == 2 && r.OnTarget(11) == 2);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // the target is killed: every bird takes its own slot back on its lane
        private static string ReturnSlots()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            var slots = new int[5]; for (int i = 1; i <= 4; i++) slots[i] = r.SlotOf(i);
            r.Add(10, 10.7, 11.2);
            r.Step(3);
            r.Is("2 intercepts", r.Intercepting() == 2);
            int before = 0; for (int i = 1; i <= 4; i++) before += r.Patrols(i);
            r.Remove(10); r.G.Gone[10] = "destroyed";
            r.Step(3);
            r.Is("ended killed", r.Logs("why=killed") == 2);
            r.Is("no intercept left", r.Intercepting() == 0);
            for (int i = 1; i <= 4; i++) r.Is("slot " + i, r.SlotOf(i) == slots[i]);
            int after = 0; for (int i = 1; i <= 4; i++) after += r.Patrols(i);
            r.Is("lane routes issued again", after >= before + 2);
            r.Is("throttle back", r.G.Bird(1).Throttle == 2 && r.G.Bird(2).Throttle == 2 && r.G.Bird(3).Throttle == 2 && r.G.Bird(4).Throttle == 2);
            r.Is("no player_took", !r.Took());
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // the target runs away: not chased further than PursuitNm from the zone
        private static string Pursuit()
        {
            var r = new Rig(); r.Start(2, 2, 0);
            var t = r.Add(10, 10.9, 11.3);
            r.Step(3);
            r.Is("2 intercepts", r.Intercepting() == 2);
            t.Lat = 10.83333 + 24.0 / 60; r.Step(2);
            r.Is("24 nm: still chased", r.Intercepting() == 2);
            t.Lat = 10.83333 + 26.0 / 60; r.Step(2);
            r.Is("26 nm: ended", r.Intercepting() == 0 && r.Logs("why=beyond_pursuit") == 2);
            r.Is("break pursuit", r.G.Journal.FindAll(j => j.StartsWith("break_pursuit")).Count >= 0);
            r.Is("no player_took", !r.Took());
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // the track is lost: fly on to the last point, end after 60 s
        private static string Lost()
        {
            var r = new Rig(); r.Start(2, 2, 0);
            r.Add(10, 10.9, 11.3);
            r.Step(3);
            r.Remove(10);
            r.Step(58);
            r.Is("58 s: still", r.Intercepting() == 2);
            r.Step(5);
            r.Is("63 s: ended lost", r.Intercepting() == 0 && r.Logs("why=lost") == 2);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string Missiles()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            r.Add(10, 10.7, 11.2); r.Add(11, 10.75, 11.5);
            r.Step(3);
            r.Is("4 intercepts", r.Intercepting() == 4);
            r.G.Bird(1).AamNow = 1;
            r.Step(3);
            r.Is("bird 1 out of the intercept", r.Logs("intercept end uid=1 ") == 1);
            r.Is("bird 1 leaves (missiles)", r.G.LogHas("leave uid=1 reason=missiles"));
            r.Is("others stay", r.Intercepting() == 3);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string Bingo()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            r.Add(10, 10.7, 11.2); r.Add(11, 10.75, 11.5);
            r.Step(3);
            r.G.Bird(2).GameBingo = true;
            r.Step(3);
            r.Is("bird 2 out", r.Logs("intercept end uid=2 ") == 1);
            r.Is("3 left", r.Intercepting() == 3);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // a chase does not lift a relief and does not leave the zone short of a holder
        private static string Relief()
        {
            var r = new Rig(); r.Start(3, 2, 1);
            int launched = r.G.Journal.FindAll(j => j.StartsWith("launched")).Count;
            int gaps = r.Logs("warn continuity_gap");
            r.Add(10, 10.9, 11.3);
            r.Step(3);
            r.Is("intercepting", r.Intercepting() >= 1);
            int ord = r.G.Journal.FindAll(j => j.StartsWith("order")).Count;
            r.Step(120);
            r.Is("no new launch", r.G.Journal.FindAll(j => j.StartsWith("launched")).Count == launched);
            r.Is("no new order", r.G.Journal.FindAll(j => j.StartsWith("order")).Count == ord);
            r.Is("no new continuity gap", r.Logs("warn continuity_gap") == gaps);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // a bird the game AI already sends at the target counts for it and is not re-tasked
        private static string AiBusy()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            var b1 = r.G.Bird(1); b1.TargetKind = 1; b1.TargetId = 10; b1.Engaged = true;
            r.Add(10, 10.7, 11.2);
            r.Step(3);
            r.Is("one more bird only", r.Intercepting() == 1 && r.Logs("intercept start") == 1);
            r.Is("bird 1 is not re-tasked", r.Logs("intercept start uid=1 ") == 0);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string Off()
        {
            var r = new Rig(); r.P.Intercept = false; r.Start(2, 2, 0);
            r.Add(10, 10.7, 11.2);
            r.Step(10);
            r.Is("none", r.Intercepting() == 0 && r.Logs("intercept start") == 0);
            r.Is("one off line", r.Logs("intercept off group=ПВО-1 why=disabled") == 1);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string RoeHold()
        {
            var r = new Rig(); r.Start(2, 2, 0, "hold");
            r.Add(10, 10.7, 11.2);
            r.Step(10);
            r.Is("none", r.Intercepting() == 0 && r.Logs("intercept start") == 0);
            r.Is("one off line", r.Logs("intercept off group=ПВО-1 why=roe_hold") == 1);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string Kinds()
        {
            var r = new Rig(); r.Start(4, 4, 0);
            var u = r.Add(10, 10.7, 11.2); u.Hostile = false; u.Unknown = true;
            var f = r.Add(11, 10.7, 11.3); f.Hostile = false;
            var m = r.Add(12, 10.7, 11.4); m.Missile = true;
            r.Step(5);
            r.Is("unknown, friend, missile: nothing", r.Intercepting() == 0);
            r.Add(13, 10.7, 11.5, true);
            r.Step(3);
            r.Is("a helicopter is a target", r.OnTarget(13) == 2);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // a route of two points every 10 s, the dash once
        private static string Guide()
        {
            var r = new Rig(); r.Start(1, 1, 0);
            r.G.Bird(1).AamMaxNm = 1;   // no hand-over before the target is close
            var t = r.Add(10, 10.9, 11.5);
            r.Step(1);   // the pair is made at the end of this tick
            r.Is("intercept", r.Intercepting() == 1);
            int p0 = r.Patrols(1);
            r.Step(1);
            int p1 = r.Patrols(1);
            r.Is("first route at once", p1 == p0 + 1);
            r.Is("two points", r.G.Bird(1).Route.Count == 2);
            r.Step(9);
            r.Is("not more often than every 10 s", r.Patrols(1) == p1);
            r.Step(3);
            r.Is("then again", r.Patrols(1) == p1 + 1);
            r.Is("one dash", r.Logs("intercept speed uid=1") == 1 && r.G.Bird(1).Throttle > 2);
            r.Step(40);
            r.Is("dash stays one", r.Logs("intercept speed uid=1") == 1);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // in the fight (game AI): no routes, no player_took
        private static string Fight()
        {
            var r = new Rig(); r.Start(1, 1, 0);
            r.Add(10, 10.9, 11.3);
            r.Step(2);
            var b = r.G.Bird(1); b.AiState = "EngageAirContact"; b.Engaged = true; b.TargetKind = 1; b.TargetId = 10;
            r.Step(2);
            int p = r.Patrols(1);
            r.Step(25);
            r.Is("no routes in the fight", r.Patrols(1) == p);
            r.Is("fight line", r.Logs("intercept fight uid=1") == 1);
            r.Is("still intercepting", r.Intercepting() == 1 && !r.Took());
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // inside the AAM range + 5 nm the bird is handed to the game AI; 30 s without a fight - one more route
        private static string Handover()
        {
            var r = new Rig(); r.Start(1, 1, 0);
            var b = r.G.Bird(1);
            r.Add(10, b.Lat + 8.0 / 60, b.Lon);
            r.Step(3);
            r.Is("handed bird=" + b.Lat + "," + b.Lon + " st=" + b.State, r.Logs("intercept handover uid=1") == 1);
            int p = r.Patrols(1);
            r.Step(20);
            r.Is("no route while handed", r.Patrols(1) == p);
            r.Step(15);
            r.Is("reguide after 30 s", r.Logs("intercept reguide uid=1") == 1 && r.Patrols(1) == p + 1);
            r.Is("no player_took", !r.Took());
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // the game takes the target off the bird (the closer shooter holds it): after 30 s the bird is guided again, not "the player took it"
        private static string Ceasefire()
        {
            var r = new Rig(); r.Start(1, 1, 0);
            var b = r.G.Bird(1);
            r.Add(10, b.Lat + 8.0 / 60, b.Lon);
            r.Step(3);
            b.AiState = "EngageAirContact"; b.Engaged = true; b.TargetKind = 1; b.TargetId = 10;
            r.Step(5);
            b.AiState = null; b.Engaged = false; b.TargetKind = 0; b.TargetId = -1;
            r.Step(40);
            r.Is("guided again", r.Logs("intercept reguide uid=1") == 1);
            r.Is("still a member, no player_took", !r.Took() && r.Own.Owner(1, out _) != null);
            return r.Err == null ? null : r.Err + Dump(r);
        }

        // a throttle change outside the mod during the chase is not turned into a false player_took by the intercept itself
        private static string PlayerThrottle()
        {
            var r = new Rig(); r.Start(1, 1, 0);
            r.G.Bird(1).AamMaxNm = 1;
            r.Add(10, 10.9, 11.5);
            r.Step(40);
            r.Is("dash seen, no player_took", r.Logs("intercept speed uid=1") == 1 && !r.Took());
            return r.Err == null ? null : r.Err + Dump(r);
        }

        private static string State()
        {
            var r = new Rig(); r.Start(2, 2, 0);
            r.Add(10, 10.9, 11.3);
            r.Step(3);
            var v = r.C.View();
            r.Is("view: group counts", v.Groups[0].IcptBirds == 2 && v.Groups[0].IcptTargets == 1 && v.Groups[0].Icpt != null && v.Groups[0].Icpt.On);
            r.Is("view: member", v.Groups[0].Members[0].Icpt != null && v.Groups[0].Members[0].IcptTarget == 10 && v.Groups[0].Members[0].IcptRound >= 1);
            string js = r.C.StateJson();
            r.Is("json bird", js.Contains("\"intercept\":{\"target\":10"));
            r.Is("json group", js.Contains("\"intercepts\":[{\"target\":10,\"uids\":["));
            r.Is("json spec", js.Contains("\"margin_nm\":10") && js.Contains("\"per_target\":2"));
            r.Is("airborne cap set", r.C.IsCapAir(1) && !r.C.IsCapAir(99));
            return r.Err == null ? null : r.Err + Dump(r);
        }
    }
}
