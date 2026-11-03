using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-315 / ADR-0122: CAP fuel leave by the live fuel fraction (floor 5 % of the tank (T-330)), the game's ring as the bingo margin, no hold in combat.
    internal static class CapT315Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) { Err = n; if (Rig.Last != null) foreach (string l in Rig.Last.G.Logs) if (l.Contains("fuel") || l.Contains("leave") || l.Contains("postpone")) Err += " | " + l; } }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: explicit floor (default off)
            public CapController C;
            public static Rig Last;
            public Rig() { Last = this; C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
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
            if (want("adopted-heavy") && (e = AdoptedHeavy()) != null) return "adopted-heavy: " + e;
            if (want("real-low") && (e = RealLow()) != null) return "real-low: " + e;
            if (want("ring-bingo") && (e = RingBingo()) != null) return "ring-bingo: " + e;
            if (want("combat-hold") && (e = CombatHold()) != null) return "combat-hold: " + e;
            if (want("no-frac") && (e = NoFrac()) != null) return "no-frac: " + e;
            return null;
        }

        // the log case: 40.5 % of the tank, the ring 133 nm (afterburner, heavy loadout), max 1864: no leave, no early relief
        private static string AdoptedHeavy()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            k.Is("on station", b != null && b.State == "Air" && !b.Rtb);
            if (k.Err != null) return k.Err;
            b.MaxRangeNm = 1864; b.FuelFrac = 0.405; b.RingNm = 133.1;
            int allows = r.Jn("allow ");
            r.Step(600);
            k.Is("no fuel low", !r.G.LogHas("fuel low uid=1"));
            k.Is("no fuel leave", !r.G.LogHas("leave uid=1 reason=fuel") && !r.G.LogHas("relief slot=0 out=1 in=2 reason=fuel") && !b.Rtb);
            k.Is("no early relief", r.Jn("allow ") == allows);
            return k.Err;
        }

        private static string RealLow()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.405; b.RingNm = 133.1;
            r.Step(5);
            b.FuelFrac = 0.04;
            r.Step(40);
            k.Is("not before the hold", !r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("fuel low uid=1 by=floor"));
            r.Step(30);
            k.Is("leave by floor", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("frac_reserve_nm=") && r.G.LogHas("low_by=floor"));
            return k.Err;
        }

        private static string RingBingo()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 2;
            r.Step(40);
            k.Is("hold not over", !r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("fuel low uid=1 by=margin"));
            r.Step(30);
            k.Is("leave by margin", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("low_by=margin"));
            return k.Err;
        }

        // in combat the hold does not run: the leave comes FuelHoldS after the combat ends
        private static string CombatHold()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 2;
            b.Engaged = true; b.TargetKind = 1;
            r.Step(120);
            k.Is("combat: no leave", !r.G.LogHas("leave uid=1 reason=fuel") && !b.Rtb);
            b.Engaged = false; b.TargetKind = 0;
            r.Step(50);
            k.Is("after combat: still holding", !r.G.LogHas("leave uid=1 reason=fuel"));
            r.Step(20);
            k.Is("after combat: leave", r.G.LogHas("leave uid=1 reason=fuel"));
            return k.Err;
        }

        // no live fraction: the old rule (the ring against the clean-range floor)
        private static string NoFrac()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.FuelFrac = -1; b.RingNm = 30;
            r.Step(50);
            k.Is("not before the hold", !r.G.LogHas("leave uid=1 reason=fuel"));
            r.Step(15);
            k.Is("old rule leave", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("game_ring_nm=30"));
            return k.Err;
        }
    }
}
