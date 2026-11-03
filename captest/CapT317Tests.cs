using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-317 / ADR-0124: the air combat of the game AI is not "the player took the aircraft"; the same deviation without combat, or with the aircraft selected, still is.
    internal static class CapT317Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private const string Eng = "EngageAirContact";

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) { Err = n; if (Rig.Last != null) foreach (string l in Rig.Last.G.Logs) if (l.Contains("drift") || l.Contains("member out") || l.Contains("dash")) Err += " | " + l; } }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public static Rig Last;
            public Rig() { Last = this; G.RoeDetector = true; C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public bool Member(int uid) { string w; return Own.Owner(uid, out w) == "ПВО-1"; }
            public bool Took() { foreach (var e in G.Events) if (e.Key == "cap_warn" && e.Value.Contains("player_took")) return true; return false; }
        }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static Rig OnStation(out FakeBird b)
        {
            var r = new Rig();
            r.C.Group(J("{\"op\":\"create\",\"uids\":[1,2]}"));
            r.C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":1}"));
            r.Step(900);
            b = r.G.Bird(1);
            return r;
        }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("math") && (e = CapTakeoverMath.SelfTest()) != null) return "math: " + e;
            if (want("ai-route") && (e = AiRoute()) != null) return "ai-route: " + e;
            if (want("ai-roe") && (e = AiRoe()) != null) return "ai-roe: " + e;
            if (want("ai-grace") && (e = AiGrace()) != null) return "ai-grace: " + e;
            if (want("player-in-combat") && (e = PlayerInCombat()) != null) return "player-in-combat: " + e;
            if (want("player-calm") && (e = PlayerCalm()) != null) return "player-calm: " + e;
            if (want("dash-ai") && (e = DashAi()) != null) return "dash-ai: " + e;
            return null;
        }

        private static string AiRoute()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            k.Is("on station", b != null && b.State == "Air" && r.Member(1));
            if (k.Err != null) return k.Err;
            b.AiState = Eng;
            r.Step(1);
            b.RouteDevNm = 2;
            r.Step(3);
            k.Is("stays", r.Member(1) && !r.Took() && !r.G.LogHas("(player_took)"));
            k.Is("drift line", r.G.LogHas("ai drift uid=1 why=route"));
            return k.Err;
        }

        private static string AiRoe()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.AiState = Eng;
            r.Step(1);
            b.Roe = b.Roe == "tight" ? "free" : "tight";   // as Winchester / CheckForPlayerAbort: the ROE moved off what the mod set
            r.Step(3);
            k.Is("stays", r.Member(1) && !r.Took() && !r.G.LogHas("(player_took)"));
            k.Is("drift line", r.G.LogHas("ai drift uid=1 why=roe"));
            return k.Err;
        }

        private static string AiGrace()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.AiState = Eng; r.Step(2);
            b.AiState = null;
            r.Step(20);
            b.RouteDevNm = 2; r.Step(1);
            k.Is("20 s after the combat: stays", r.Member(1) && !r.Took() && r.G.LogHas("ai drift uid=1 why=route"));
            r.Step(12);
            b.RouteDevNm = 2; r.Step(1);
            k.Is("31+ s after the combat: player_took", !r.Member(1) && r.Took() && r.G.LogHas("(player_took) why=route"));
            return k.Err;
        }

        private static string PlayerInCombat()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.Selected = true;
            b.AiState = Eng;
            r.Step(2);
            b.RouteDevNm = 2;
            r.Step(2);
            k.Is("player_took", !r.Member(1) && r.Took());
            k.Is("reason in the log", r.G.LogHas("member out uid=1 group=ПВО-1 (player_took) why=route") && r.G.LogHas("sel=1"));
            return k.Err;
        }

        private static string PlayerCalm()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.RouteDevNm = 2;
            r.Step(2);
            k.Is("player_took", !r.Member(1) && r.Took() && r.G.LogHas("(player_took) why=route"));
            var q = OnStation(out var b2);
            b2.Roe = b2.Roe == "tight" ? "free" : "tight";
            q.Step(2);
            k.Is("ROE change: player_took", !q.Member(1) && q.Took() && q.G.LogHas("(player_took) why=roe"));
            return k.Err;
        }

        private static string DashAi()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.AamNow = 1; r.Step(3);
            k.Is("dash running", b.Throttle == 4 && r.G.Journal.Contains("throttle 1 4"));
            b.AiState = Eng; b.Throttle = 3;
            r.Step(2);
            k.Is("ai telegraph: stays", r.Member(1) && !r.Took() && r.G.LogHas("dash throttle ai uid=1"));
            // out of the window: the old rule
            var q = OnStation(out var b2);
            q.G.Bird(1).AamNow = 1; q.Step(3);
            b2.Throttle = 3;
            q.Step(2);
            k.Is("no window: player_took", !q.Member(1) && q.Took());
            // selected in combat: the player
            var s = OnStation(out var b3);
            s.G.Bird(1).AamNow = 1; s.Step(3);
            b3.Selected = true; b3.AiState = Eng; s.Step(1);
            b3.Throttle = 3;
            s.Step(2);
            k.Is("selected: player_took", !s.Member(1) && s.Took());
            return k.Err;
        }
    }
}
