using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 stream C1: air-only CAP (postpone-surface, surface-filter), one order with the loadouts (single-order). The alert pair scenarios moved to CapT179Tests (ADR-0079).
    internal static class CapT175Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";   // 20 x 40 nm, 75 nm from the deck

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
                P = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: explicit floor (default off)
                if (cfg != null) cfg(P);
                C = new CapController(G, P, Own);
            }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Create(string uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}")); }
            public string Ord(string group, string rect, int n, int k, string extra = "") { return C.Order(J("{\"group\":\"" + group + "\"," + rect + ",\"on_station\":" + n + ",\"ready\":" + k + extra + "}")); }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public string Last(string prefix) { string r = null; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) r = j; return r; }
        }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static Rig OnStation(string uids, int n, int kk, int steps = 900, Action<CapParams> cfg = null)
        {
            var r = new Rig(cfg);
            r.Create(uids); r.Ord("ПВО-1", ZA, n, kk);
            r.Step(steps);
            return r;
        }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("postpone-surface") && (e = PostponeSurface()) != null) return "postpone-surface: " + e;
            if (want("surface-filter") && (e = SurfaceFilter()) != null) return "surface-filter: " + e;
            if (want("single-order") && (e = SingleOrder()) != null) return "single-order: " + e;
            if (want("reorder") && (e = Reorder()) != null) return "reorder: " + e;
            if (want("ring-fuel") && (e = RingFuel()) != null) return "ring-fuel: " + e;
            return null;
        }

        // F-14 with 2/8 AAM shoots at a burning ship: the surface target does not hold it, it goes home with weapons hold
        private static string PostponeSurface()
        {
            var k = new Chk();
            var r = OnStation("1,2", 1, 1);
            var b = r.G.Bird(1);
            k.Is("on station with free roe set by the group", b.State == "Air" && b.Roe == "tight");
            b.AamNow = 1; b.TargetKind = 2; b.Engaged = true;
            r.Step(3);
            k.Is("leaves at once (surface target)", r.G.EventCount("cap_rtb") == 1 && b.Rtb);
            k.Is("no postponement", r.G.EventCount("cap_postponed") == 0);
            k.Is("ROE hold on the way home", b.Roe == "hold");
            k.Is("cap_roe event", (r.G.LastEvent("cap_roe") ?? "").Contains("\"roe\":\"hold\"") && (r.G.LastEvent("cap_roe") ?? "").Contains("\"reason\":\"rtb\""));
            k.Is("journal", r.G.LogHas("[Cap] roe uid=1 hold reason=rtb"));
            // an air target keeps it in the fight (postponed combat)
            var q = OnStation("1,2", 1, 1);
            var c = q.G.Bird(1);
            c.AamNow = 1; c.TargetKind = 1; c.Engaged = true;
            q.Step(3);
            k.Is("air target: postponed", q.G.EventCount("cap_postponed") == 1 && q.G.EventCount("cap_rtb") == 0 && !c.Rtb);
            c.Engaged = false; c.TargetKind = 0;
            q.Step(3);
            k.Is("then it leaves", q.G.EventCount("cap_rtb") == 1);
            // AirOnly=false: the old behaviour (any target holds, hold on RTB is a separate switch)
            var o = OnStation("1,2", 1, 1, 900, p => { p.AirOnly = false; p.RtbRoeHold = false; });
            var d = o.G.Bird(1);
            d.AamNow = 1; d.TargetKind = 2; d.Engaged = true;
            o.Step(3);
            k.Is("AirOnly=false: surface target postpones", o.G.EventCount("cap_postponed") == 1 && !d.Rtb);
            d.Engaged = false; d.TargetKind = 0;
            o.Step(3);
            k.Is("RtbRoeHold=false: no hold", d.Rtb && d.Roe == "tight");
            // "Return control" gives the ROE back (the previous one is in the memory of the game layer)
            r.C.ReleaseUid(1);
            k.Is("restore called", r.Jn("restore 1") == 1);
            return k.Err;
        }

        // a patrol bird with a surface target: the task is cleared; without the member of the game the ROE is hold, until an air enemy is near
        private static string SurfaceFilter()
        {
            var k = new Chk();
            var r = OnStation("1,2", 1, 1);
            var b = r.G.Bird(1);
            b.TargetKind = 2; b.Engaged = true;
            r.Step(3);
            k.Is("task cleared", r.Jn("clear_surface 1") >= 1 && b.TargetKind == 0 && b.Roe == "tight");
            k.Is("journal", r.G.LogHas("[Cap] roe uid=1 reason=surface_target clear -> ok"));
            // the member is missing: the fallback is ROE hold
            var q = OnStation("1,2", 1, 1);
            q.G.ClearSurfaceError = "unsupported";
            var c = q.G.Bird(1);
            c.TargetKind = 2; c.Engaged = true;
            q.Step(3);
            k.Is("fallback hold", c.Roe == "hold" && (q.G.LastEvent("cap_roe") ?? "").Contains("surface_target"));
            q.Step(10);
            k.Is("stays on hold while the target is a ship", c.Roe == "hold");
            // an air enemy within CombatHostileNm: the group's ROE comes back
            q.G.Contacts.Add(new CapContact { Id = 77, Lat = c.Lat + 3.0 / 60.0, Lon = c.Lon, Hostile = true });
            q.Step(3);
            k.Is("air enemy near: ROE of the group", c.Roe == "tight");
            q.G.Contacts.Clear();
            // the target is an air one: nothing is touched
            var t = OnStation("1,2", 1, 1);
            var d = t.G.Bird(1);
            d.TargetKind = 1; d.Engaged = true;
            t.Step(5);
            k.Is("air target untouched", t.Jn("clear_surface") == 0 && d.Roe == "tight");
            return k.Err;
        }

        private static List<LoadoutOption> F14(double longMin, double interceptMin)
        {
            Func<string, double, int, LoadoutOption> lo = (n, m, c) => new LoadoutOption { Name = n, Display = n, Capacity = 8, Available = true, ReadyUpMin = m, Weapons = new Dictionary<string, int> { { n == "AirToAirLongRange" ? "AIM-54" : "AIM-7", c }, { "AIM-9", 2 } } };
            return new List<LoadoutOption> { lo("AirToAirLongRange", longMin, 4), lo("AirToAirIntercept", interceptMin, 4) };
        }

        // one order: the loadout of the order is the one of the first deck request; "auto" under an air threat = the fastest one
        private static string SingleOrder()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic";
            r.G.ExtraLoadouts["F-14A"] = F14(30, 15);
            r.Create("1,2,3,4");
            string a = r.C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":0,\"loadouts\":{\"F-14A\":\"AirToAirIntercept\"}}"));
            k.Is("order ok " + a, a != null && a.Contains("\"ok\":true"));
            r.Step(3);
            k.Is("first request has the chosen loadout", (r.Last("order ") ?? "").Contains("F-14A AirToAirIntercept"));
            k.Is("one request only", r.Jn("order ") == 1);
            k.Is("view keeps the choice", r.C.View().Groups[0].Loadouts["F-14A"] == "AirToAirIntercept");
            // without loadouts the planner takes its usual pick; "auto" with a threat takes the fastest
            var q = new Rig();
            q.G.Mode = "Realistic";
            q.G.ExtraLoadouts["F-14A"] = F14(30, 15);
            q.Create("1,2,3,4");
            q.G.Contacts.Add(new CapContact { Id = 50, Lat = 10.5 + 40.0 / 60.0, Lon = 11.3, Hostile = true });   // 40 nm north of the zone: a threat
            q.C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":0,\"loadouts\":{\"F-14A\":\"auto\"}}"));
            q.Step(3);
            k.Is("auto under a threat: the fastest loadout", (q.Last("order ") ?? "").Contains("F-14A AirToAirIntercept"));
            k.Is("pick logged", q.G.LogHas("loadout pick type=F-14A loadout=AirToAirIntercept") && q.G.LogHas("why=threat"));
            var n = new Rig();
            n.G.Mode = "Realistic";
            n.G.ExtraLoadouts["F-14A"] = F14(30, 15);
            n.Create("1,2,3,4"); n.Ord("ПВО-1", ZA, 1, 0);
            n.Step(3);
            k.Is("no threat: the usual pick (long range)", (n.Last("order ") ?? "").Contains("F-14A AirToAirLongRange"));
            return k.Err;
        }

        // the prepared task of the mod is ordered again only when the new loadout is ready sooner by more than 60 s; others' tasks stay
        private static string Reorder()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic";
            r.G.ExtraLoadouts["F-14A"] = F14(30, 15);
            r.Create("1,2,3,4"); r.Ord("ПВО-1", ZA, 1, 0);
            r.Step(5);
            k.Is("first order: long range", (r.Last("order ") ?? "").Contains("AirToAirLongRange") && r.Jn("order ") == 1);
            r.C.Loadout(J("{\"group\":\"ПВО-1\",\"type\":\"F-14A\",\"loadout\":\"AirToAirIntercept\"}"));
            r.Step(5);
            k.Is("30 min -> 15 min: aborted and ordered again", r.Jn("abort ") == 1 && r.Jn("order ") == 2 && (r.Last("order ") ?? "").Contains("AirToAirIntercept"));
            k.Is("reorder logged", r.G.LogHas("(planner:loadout_changed)") || r.G.LogHas("(loadout_changed)"));
            // gain under 60 s: nothing
            var q = new Rig();
            q.G.Mode = "Realistic";
            q.G.ExtraLoadouts["F-14A"] = F14(30, 29.5);
            q.Create("1,2,3,4"); q.Ord("ПВО-1", ZA, 1, 0);
            q.Step(5);
            q.C.Loadout(J("{\"group\":\"ПВО-1\",\"type\":\"F-14A\",\"loadout\":\"AirToAirIntercept\"}"));
            q.Step(10);
            k.Is("gain under 60 s: no reorder " + string.Join(" | ", q.G.Journal.ToArray()) + " || " + string.Join(" | ", q.G.Logs.FindAll(x => x.Contains("abort") || x.Contains("order launch") || x.Contains("loadout")).ToArray()), q.Jn("abort ") == 0 && q.Jn("order ") == 1);
            // a foreign task is never touched
            var f = new Rig();
            f.G.Mode = "Realistic";
            f.G.ExtraLoadouts["F-14A"] = F14(30, 15);
            var ft = f.G.AddForeignTask("F-14A", "AirToAirLongRange");
            f.Create("1,2,3,4"); f.Ord("ПВО-1", ZA, 1, 0);
            f.Step(5);
            f.C.Loadout(J("{\"group\":\"ПВО-1\",\"type\":\"F-14A\",\"loadout\":\"AirToAirIntercept\"}"));
            f.Step(10);
            bool foreignAborted = false; foreach (string j in f.G.Journal) if (j == "abort " + ft.Key) foreignAborted = true;
            k.Is("foreign task kept", !foreignAborted && ft.State != "Done");
            return k.Err;
        }

        // correction 2026-10-22 (ADR-0040 D5): the reserve is the game's map ring (RangeOnMap) out and back; a burst under the threshold shorter than
        // FuelHoldS does not send the aircraft home and does not call a relief, a ring held under it does; the leave line shows game_ring_nm
        private static string RingFuel()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2");
            r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            var b = r.G.Bird(1);
            k.Is("on station", b != null && b.State == "Air" && !b.Rtb);
            if (k.Err != null) return k.Err;
            b.RingNm = 800;   // cruise: path 1600
            r.Step(5);
            int allows = r.Jn("allow ");
            // afterburner burst: the ring collapses to 30 nm (path 60 < floor 100, T-330) for 30 s
            b.RingNm = 30;
            r.Step(30);
            k.Is("burst: logged, no leave", r.G.LogHas("fuel low uid=1 by=ring game_ring_nm=30") && !r.G.LogHas("reason=fuel") && !b.Rtb);
            k.Is("burst: no early relief", r.Jn("allow ") == allows);
            b.RingNm = 780;
            r.Step(5);
            k.Is("ring back: still on station", !b.Rtb);
            // really low: the ring stays under the threshold -> leave after FuelHoldS
            b.RingNm = 30;
            r.Step(50);
            k.Is("not before the hold", !r.G.LogHas("reason=fuel"));
            r.Step(15);
            k.Is("held low -> fuel leave with the game ring", r.G.LogHas("leave uid=1 reason=fuel") && r.G.LogHas("game_ring_nm=30"));
            return k.Err;
        }


    }
}
