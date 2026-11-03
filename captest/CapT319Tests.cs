using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-319: the game AI air fight state (EngageAirContact and sub-states) without engage tasks counts as combat: fuel leave and station throttle wait for its end.
    internal static class CapT319Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) { Err = n; if (Rig.Last != null) foreach (string l in Rig.Last.G.Logs) if (l.Contains("fuel") || l.Contains("leave") || l.Contains("postpone") || l.Contains("station speed")) Err += " | " + l; } }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public static Rig Last;
            public Rig() { Last = this; C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
        }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static Rig OnStation(out FakeBird b, Action<Rig> pre = null, string zone = ZA, int ready = 1)
        {
            var r = new Rig();
            if (pre != null) pre(r);
            r.C.Group(J("{\"op\":\"create\",\"uids\":[1,2]}"));
            r.C.Order(J("{\"group\":\"ПВО-1\"," + zone + ",\"on_station\":1,\"ready\":" + ready + "}"));
            r.Step(900);
            b = r.G.Bird(1);
            return r;
        }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("fuel-hold-air-state") && (e = FuelHold("EngageAirContact")) != null) return "fuel-hold-air-state: " + e;
            if (want("sub-state") && (e = FuelHold("Pursuit")) != null) return "sub-state: " + e;
            if (want("station-throttle") && (e = StationThrottle()) != null) return "station-throttle: " + e;
            if (want("calm") && (e = Calm()) != null) return "calm: " + e;
            return null;
        }

        private static string FuelHold(string state)
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 2;
            b.AiState = state; b.Engaged = false; b.TargetKind = -1;
            r.Step(120);
            k.Is("air fight: no leave", !r.G.LogHas("leave uid=1 reason=fuel") && !b.Rtb);
            b.AiState = "CAP";
            r.Step(90);
            k.Is("after fight: leave", r.G.LogHas("leave uid=1 reason=fuel"));
            return k.Err;
        }

        private static string StationThrottle()
        {
            var k = new Chk();
            var r = OnStation(out var b, x => x.P.StationSpeed = "cruise", "\"rect\":[10.5,11,10.516667,11.016667]", 0);
            if (b == null) return "no bird";
            b.ReserveNm = 100000;
            b.Throttle = 4; b.SpeedKn = CapFakeGame.StepKn[3];
            b.AiState = "EngageAirContact"; b.Engaged = false; b.TargetKind = -1;
            for (int i = 0; i < 90; i++) { r.Step(1); b.ReserveNm = 100000; }
            k.Is("fight: step stays 4, got " + b.Throttle, b.Throttle == 4);
            k.Is("fight: no line", !r.G.LogHas("station speed uid=1"));
            b.AiState = "CAP"; b.TargetKind = 0;   // the fake: TargetKind != 0 is HasTarget
            for (int i = 0; i < 200; i++) { r.Step(1); b.ReserveNm = 100000; }
            k.Is("after fight: step dropped, got " + b.Throttle, b.Throttle < 4);
            k.Is("reason after_combat", r.G.LogHas("station speed uid=1") && r.G.LogHas("reason=after_combat"));
            return k.Err;
        }

        private static string Calm()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 2;
            b.AiState = "CAP"; b.Engaged = false;
            r.Step(120);
            k.Is("calm: leave by fuel", r.G.LogHas("leave uid=1 reason=fuel"));
            return k.Err;
        }
    }
}
