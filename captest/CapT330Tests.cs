using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-330 / ADR-0128: lower return defaults (missiles 20 %, fuel floor 5 %, ring margin 5 nm over the way home); the game's bingo/winchester still first.
    internal static class CapT330Tests
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
            public CapParams P = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: explicit floor for the floor scenarios
            public CapController C;
            public static Rig Last;
            public Rig() { Last = this; C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
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

        private static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-6; }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("defaults") && (e = Defaults()) != null) return "defaults: " + e;
            if (want("missiles") && (e = Missiles()) != null) return "missiles: " + e;
            if (want("fuel-math") && (e = FuelMath()) != null) return "fuel-math: " + e;
            if (want("backstops") && (e = Backstops()) != null) return "backstops: " + e;
            if (want("ctl-margin") && (e = CtlMargin()) != null) return "ctl-margin: " + e;
            if (want("ctl-floor") && (e = CtlFloor()) != null) return "ctl-floor: " + e;
            if (want("floor-off") && (e = FloorOff()) != null) return "floor-off: " + e;
            if (want("reserve-fallback") && (e = ReserveFallback()) != null) return "reserve-fallback: " + e;
            if (want("ctl-far") && (e = CtlFar()) != null) return "ctl-far: " + e;
            if (want("combat-override") && (e = CombatOverride()) != null) return "combat-override: " + e;
            if (want("ctl-combat-override") && (e = CtlCombatOverride()) != null) return "ctl-combat-override: " + e;
            if (want("margin-hold") && (e = MarginHold()) != null) return "margin-hold: " + e;
            if (want("ctl-margin-r34") && (e = CtlMarginR34()) != null) return "ctl-margin-r34: " + e;
            return null;
        }

        private static string Defaults()
        {
            var p = new CapParams();
            if (!Near(p.MissilePct, 0.20)) return "missile pct";
            if (!Near(p.FuelFloorPct, 0)) return "floor pct (ADR-0131: off)";
            if (!Near(p.RingMarginNm, 5)) return "ring margin";
            if (!Near(p.FuelMarginS, 300)) return "fuel margin s";
            return null;
        }

        // ADR-0131: floor 0 = off: 3 % of the tank with a good reserve does not leave; the reserve rule still does
        private static string FloorOff()
        {
            var p = new CapParams();
            var b = new CapBird { FuelFrac = 0.03, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 100 };
            if (CapMath.FuelLowBy(b, 0, p) != null) return "3 % with reserve 100 is not low";
            b.ReserveNm = 5; if (CapMath.FuelLowBy(b, 0, p) != "margin") return "reserve 5 is margin";
            b.ReserveNm = 6; if (CapMath.FuelLowBy(b, 0, p) != null) return "reserve 6 is fine";
            b.FuelFrac = 0; b.ReserveNm = 100; if (CapMath.FuelLowBy(b, 0, p) != "floor") return "empty tank is floor";
            b.FuelFrac = 0.03; b.ReserveNm = 100;
            double li = CapMath.FuelLeaveInS(b, p);
            if (Math.Abs(li - (100 - 5) / 420.0 * 3600.0) > 1e-6) return "leave in by margin only " + li;
            return null;
        }

        // ADR-0131: the reserve cannot be computed -> hard floor 10 % (by=hard), never flying dry
        private static string ReserveFallback()
        {
            var p = new CapParams();
            foreach (double bad in new[] { -1.0, double.NaN, double.PositiveInfinity })
            {
                var b = new CapBird { FuelFrac = 0.30, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = bad };
                if (CapMath.FuelLowBy(b, 0, p) != null) return "30 % with unknown reserve stays " + bad;
                b.FuelFrac = 0.10; if (CapMath.FuelLowBy(b, 0, p) != "hard") return "10 % hard " + bad;
                b.FuelFrac = 0.05; if (CapMath.FuelLowBy(b, 0, p) != "hard") return "5 % hard " + bad;
                if (CapMath.FuelLeaveInS(b, p) != 0) return "leave now " + bad;
                b.FuelFrac = 0.5; if (CapMath.FuelLeaveInS(b, p) < 1e9) return "unknown leave time " + bad;
            }
            var z = new CapBird { FuelFrac = 0.08, FuelFracLive = true, MaxRangeNm = 0, ReserveNm = 0, PatrolKn = 420 };
            if (CapMath.FuelLowBy(z, 0, p) != "hard") return "range 0 -> hard";
            z.FuelFrac = 0.4; if (CapMath.FuelLowBy(z, 0, p) != null) return "range 0 40 % stays";
            var nf = new CapBird { FuelFrac = 0.08, FuelFracLive = false, MaxRangeNm = 0, ReserveNm = -1, PatrolKn = 420 };
            if (CapMath.FuelLowBy(nf, 0, p) != "hard") return "no live frac, no reserve -> hard";
            var none = new CapBird { PatrolKn = 420 };
            if (CapMath.FuelLowBy(none, 0, p) != null) return "nothing known: no verdict";
            return null;
        }

        private static string Missiles()
        {
            var p = new CapParams();
            if (CapMath.MissileFloor(8, 0.2) != 1) return "floor 8";
            if (CapMath.MissileFloor(4, 0.2) != 0) return "floor 4";
            var b = new CapBird { AamStart = 8, AamNow = 1 };
            if (CapMath.MissileLeave(b, p) != "missiles") return "1 of 8 leaves";
            b.AamNow = 2; if (CapMath.MissileLeave(b, p) != null) return "2 of 8 stays";
            b.AamStart = 4; b.AamNow = 1; if (CapMath.MissileLeave(b, p) != null) return "1 of 4 stays";
            b.AamNow = 0; if (CapMath.MissileLeave(b, p) != "missiles") return "0 of 4 leaves";
            return null;
        }

        private static string FuelMath()
        {
            var p = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: default floor is off; these cases pin an explicit 5 %
            var b = new CapBird { FuelFrac = 0.06, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = CapMath.RingReserveNm(133.1) };
            if (CapMath.FuelLowBy(b, 0, p) != null) return "6 % is not low";
            b.FuelFrac = 0.05; if (CapMath.FuelLowBy(b, 0, p) != "floor") return "5 % is floor";
            b.FuelFrac = 0.35; b.ReserveNm = 6; if (CapMath.FuelLowBy(b, 0, p) != null) return "reserve 6 is not margin";
            b.ReserveNm = 5; if (CapMath.FuelLowBy(b, 0, p) != "margin") return "reserve 5 is margin";
            b.ReserveNm = 30; if (CapMath.FuelLowBy(b, 0, p) == "margin") return "reserve 30 (T-315 case) is not margin";
            // planner: live fraction
            b = new CapBird { FuelFrac = 0.5, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 100 };
            double want = Math.Min((100 - 5) / 420.0 * 3600.0, 100 * (0.45 / 0.5) / 420.0 * 3600.0);
            if (Math.Abs(CapMath.FuelLeaveInS(b, p) - want) > 1e-6) return "leave in live " + CapMath.FuelLeaveInS(b, p) + " vs " + want;
            // no fraction: the old formula with FuelMarginS
            b = new CapBird { MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 100 };
            double old = Math.Min(100 / 420.0 * 3600.0 - p.FuelMarginS, (100 - 0.05 * 1864) / 420.0 * 3600.0);
            if (Math.Abs(CapMath.FuelLeaveInS(b, p) - Math.Max(0, old)) > 1e-6) return "leave in no frac " + CapMath.FuelLeaveInS(b, p);
            return null;
        }

        private static string Backstops()
        {
            var p = new CapParams();
            string pp;
            var b = new CapBird { AamStart = 8, AamNow = 8, FuelFrac = 0.9, FuelFracLive = true, ReserveNm = 1000, MaxRangeNm = 2000, PatrolKn = 420, GameBingo = true };
            if (CapMath.LeaveReason(b, 0, 99, p, out pp) != "game_bingo") return "game_bingo";
            b.GameBingo = false; b.GameWinchester = true;
            if (CapMath.LeaveReason(b, 0, 99, p, out pp) != "game_winchester") return "game_winchester";
            b.GameWinchester = false; b.GameBingo = true; b.Engaged = true; b.TargetKind = 1;
            if (CapMath.LeaveReason(b, 0, 1, p, out pp) != "game_bingo") return "game_bingo in combat";
            return null;
        }

        // 0.35 of the tank, ring 2 nm (reserve 4 <= 5): by=margin, leave after the hold
        private static string CtlMargin()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 2;
            r.Step(40);
            k.Is("fuel low by margin", r.G.LogHas("fuel low uid=1 by=margin") && !r.G.LogHas("leave uid=1 reason=fuel"));
            r.Step(30);
            k.Is("leave by margin", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("low_by=margin"));
            return k.Err;
        }

        private static string CtlFloor()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.405; b.RingNm = 133.1;
            r.Step(5);
            b.FuelFrac = 0.04;
            r.Step(40);
            k.Is("fuel low by floor", r.G.LogHas("fuel low uid=1 by=floor"));
            r.Step(30);
            k.Is("leave by floor", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("low_by=floor"));
            return k.Err;
        }

        // 35 % and a ring of 10 nm (reserve 20 > 5): the old rule would have left; now it stays
        private static string CtlFar()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            b.MaxRangeNm = 1864; b.FuelFrac = 0.35; b.RingNm = 10;
            r.Step(120);
            k.Is("no fuel leave", !r.G.LogHas("leave uid=1 reason=fuel") && !b.Rtb);
            return k.Err;
        }

        // T-337 (ADR-0131 supplement): a fuel leave (margin/floor/hard) is not postponed by combat / hostile_near when the live fraction <= HardFloorPct
        private static string CombatOverride()
        {
            var p = new CapParams();
            string pp;
            var b = new CapBird { FuelFrac = 0.30, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 5, AamNow = 6, Engaged = true, TargetKind = 1, FuelLowS = -1 };
            if (CapMath.LeaveReason(b, 0, 99, p, out pp) != null || pp != "combat") return "30 % margin in combat is postponed";
            if (CapMath.FuelOverridesPostpone(b)) return "30 % does not override";
            b.FuelFrac = 0.10;
            if (CapMath.LeaveReason(b, 0, 99, p, out pp) != "fuel" || pp != null) return "10 % margin in combat leaves";
            if (!CapMath.FuelOverridesPostpone(b)) return "10 % overrides";
            b.Engaged = false; b.TargetKind = 0;
            if (CapMath.LeaveReason(b, 0, 5, p, out pp) != "fuel" || pp != null) return "10 % margin with a hostile near leaves";
            var h = new CapBird { FuelFrac = 0.08, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = -1, AamNow = 6, Engaged = true, TargetKind = 1, FuelLowS = -1 };
            if (CapMath.FuelLowBy(h, 0, p) != "hard" || CapMath.LeaveReason(h, 0, 99, p, out pp) != "fuel") return "hard 8 % in combat leaves";
            var f = new CapBird { FuelFrac = 0.04, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 100, AamNow = 6, Engaged = true, TargetKind = 1, FuelLowS = -1 };
            if (CapMath.LeaveReason(f, 0, 99, new CapParams { FuelFloorPct = 0.05 }, out pp) != "fuel") return "floor 4 % in combat leaves";
            var nl = new CapBird { FuelFrac = 0.05, FuelFracLive = false, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 1, AamNow = 6, Engaged = true, TargetKind = 1, FuelLowS = -1 };
            if (CapMath.FuelOverridesPostpone(nl)) return "no live fraction: no override";
            var m = new CapBird { FuelFrac = 0.05, FuelFracLive = true, AamNow = 1, AamStart = 8, Engaged = true, TargetKind = 1, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 1000 };
            if (CapMath.LeaveReason(m, 0, 99, p, out pp) != null || pp != "combat") return "a missiles leave is still postponed";
            return null;
        }

        // T-337: the controller: a bird in an air fight at 8 % of the tank (margin) goes home, with a log line; the combat no longer restarts the hold
        private static string CtlCombatOverride()
        {
            var k = new Chk();
            var r = OnStation(out var b);
            if (b == null) return "no bird";
            r.P.FuelFloorPct = 0;
            b.MaxRangeNm = 1864; b.FuelFrac = 0.405; b.RingNm = 133.1;
            r.Step(5);
            b.Engaged = true; b.TargetKind = 1;
            b.FuelFrac = 0.30; b.RingNm = 2;
            r.Step(120);
            k.Is("30 % in combat waits", !r.G.LogHas("leave uid=1 reason=fuel"));
            b.FuelFrac = 0.08;
            r.Step(120);
            k.Is("override logged", r.G.LogHas("postpone override uid=1"));
            k.Is("leave at 8 % in combat", r.G.LogHas("leave uid=1 reason=fuel"));
            return k.Err;
        }

        // T-337 E (live r34): by=margin at a live fraction <= 10 % holds FuelMarginHoldS (5 s), not FuelHoldS (60 s); other reasons keep 60 s
        private static string MarginHold()
        {
            var p = new CapParams();
            string pp;
            if (!Near(p.FuelMarginHoldS, 5) || !Near(p.FuelHoldS, 60)) return "constants";
            var b = new CapBird { FuelFrac = 0.032, FuelFracLive = true, MaxRangeNm = 2107.2, PatrolKn = 420, ReserveNm = 4.9, AamNow = 7, AamStart = 8, FuelLowS = 4.9 };
            if (CapMath.FuelHoldFor(b, p) != 5) return "hold at 3 % margin";
            if (CapMath.LeaveReason(b, 0, 99, p, out pp) != null) return "4.9 s: still within the hold";
            b.FuelLowS = 5; if (CapMath.LeaveReason(b, 0, 99, p, out pp) != "fuel") return "5 s: leave";
            b.FuelFrac = 0.35; if (CapMath.FuelHoldFor(b, p) != 60 || CapMath.LeaveReason(b, 0, 99, p, out pp) != null) return "35 % margin keeps 60 s (ring burst guard)";
            b.FuelFracLive = false; b.FuelFrac = 0.032; if (CapMath.FuelHoldFor(b, p) != 60) return "no live fraction keeps 60 s";
            var f = new CapBird { FuelFrac = 0.04, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = 100, FuelLowS = 5 };
            var pf = new CapParams { FuelFloorPct = 0.05 };
            if (CapMath.FuelHoldFor(f, pf) != 60 || CapMath.LeaveReason(f, 0, 99, pf, out pp) != null) return "floor keeps 60 s";
            var h = new CapBird { FuelFrac = 0.08, FuelFracLive = true, MaxRangeNm = 1864, PatrolKn = 420, ReserveNm = -1, FuelLowS = 5 };
            if (CapMath.FuelHoldFor(h, p) != 60) return "hard keeps 60 s";
            return null;
        }

        // T-337 E: the r34 timing - fuel 3.2 %, ring 2.5 nm (reserve 4.9-5): the game's bingo came 20-50 game s later (uid 1387 low_s=24.5);
        // the mod must leave by margin within ~5 s, before it. A ring dip shorter than the hold does not send the bird home.
        private static string CtlMarginR34()
        {
            var k = new Chk();
            var r = new Rig();
            r.P.FuelFloorPct = 0;   // the r34 cfg (v13): floor off, only the reserve rule
            r.C.Group(J("{\"op\":\"create\",\"uids\":[1,2]}"));
            r.C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":1}"));
            r.Step(900);
            var b = r.G.Bird(1);
            if (b == null) return "no bird";
            b.MaxRangeNm = 2107.2; b.FuelFrac = 0.405; b.RingNm = 133.1;
            r.Step(5);
            b.FuelFrac = 0.032; b.RingNm = 2.5;
            r.Step(3);
            b.RingNm = 10;   // noisy ring: back above the threshold before the hold ends
            r.Step(2);
            k.Is("short dip: fuel low logged with the 5 s hold", r.G.LogHas("fuel low uid=1 by=margin") && r.G.LogHas("(leave after 5 s below)"));
            k.Is("short dip: no leave", !r.G.LogHas("leave uid=1"));
            b.RingNm = 2.5;
            r.Step(4);
            k.Is("4 s below: no leave yet", !r.G.LogHas("leave uid=1"));
            r.Step(3);
            k.Is("leave by margin within ~5 s", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("low_by=margin"));
            b.GameBingo = true;   // the game's bingo at ~20 s comes after the mod's own leave
            r.Step(20);
            k.Is("not a game_bingo leave", !r.G.LogHas("reason=game_bingo"));
            return k.Err;
        }
    }
}
