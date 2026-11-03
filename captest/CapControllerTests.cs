// T-112 D1-D6: CapController on CapFakeGame. Each scenario returns null (ok) or the name of the first failed check.
using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    internal static class CapControllerTests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";   // 20 x 40 nm, long side east-west, 75 nm from the deck
        private const string ZB = "\"rect\":[12,11,12.33333,11.66667]";

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) { Err = n; Dump(); } }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P;
            public CapController C;
            public static Rig Last;
            public Rig(Action<CapParams> cfg = null)
            {
                Last = this;
                P = new CapParams { Debug = true, FuelFloorPct = 0.05 };   // ADR-0131: explicit floor (default off)
                if (cfg != null) cfg(P);
                C = new CapController(G, P, Own);
            }
            public void Step(int n, CapController c = null)
            {
                for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(c ?? C); }
            }
            public string Create(string uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}")); }
            public string Ord(string group, string rect, int n, int k, string extra = "")
            {
                return C.Order(J("{\"group\":\"" + group + "\"," + rect + ",\"on_station\":" + n + ",\"ready\":" + k + extra + "}"));
            }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public bool InZone(int uid, string rect = null)
            {
                var b = G.Bird(uid);
                var z = new CapZone { Rect = new[] { 10.5, 11, 10.83333, 11.66667 } };
                return CapMath.DistToZoneNm(new GeoPt(b.Lat, b.Lon), z) <= 0;
            }
            public CapGroupView Gv(int i = 0) { return C.View().Groups[i]; }
        }

        private static string AllEv(CapFakeGame g, string name) { string r = ""; foreach (var e in g.Events) if (e.Key == name) r += e.Value; return r; }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        // ---------------- C2: group names follow the UI language, numbering survives any prefix ----------------
        private static string GroupNames()
        {
            var r = new Rig(); var k = new Chk();
            var d = new CapSaveData();
            d.Groups.Add(new CapGroupDto { Group = "ПВО-2", Seq = 1 });
            d.Groups.Add(new CapGroupDto { Group = "CAP-4", Seq = 2 });
            r.C.Load(d, r.G.Now);
            string a = r.Create("1,2");
            k.Is("next after ПВО-2/CAP-4 is 5 (ru) " + a, a.Contains("\"group\":\"ПВО-5\""));
            using (UiText.Push("en"))
            {
                a = r.Create("3,4");
                k.Is("en prefix CAP, number 6 " + a, a.Contains("\"group\":\"CAP-6\""));
            }
            return k.Err;
        }

        // ---------------- D1 ----------------
        private static string Group()
        {
            var r = new Rig(); var k = new Chk();
            string a = r.Create("1,2,3,4,9"), w;
            k.Is("create ack " + a, a.Contains("\"group\":\"ПВО-1\"") && a.Contains("\"added\":[1,2,3,4]") && a.Contains("\"reason\":\"hidden\""));
            k.Is("owned", r.Own.Owner(1, out w) == "ПВО-1" && w == "cap");
            k.Is("hidden log", r.G.LogHas("hidden self_defence uid=9"));
            k.Is("busy", r.C.Busy(1) == "in_group:ПВО-1" && r.C.Busy(9) == null);
            k.Is("only hidden", r.Create("9") == "!hidden");

            var r2 = new Rig();
            r2.Own.Register("asw", u => u == 3 ? "ПЛО-1" : null);
            a = r2.Create("1,3");
            k.Is("asw owned " + a, a.Contains("in_group:ПЛО-1") && a.Contains("\"added\":[1]"));
            r2.G.Bird(2).InMissionZone = true; r2.G.Bird(4).Taken = true;
            a = r2.C.Group(J("{\"op\":\"add\",\"group\":\"ПВО-1\",\"uids\":[2,4,5]}"));
            k.Is("add rejects " + a, a.Contains("game_cap_zone") && a.Contains("\"reason\":\"player\"") && a.Contains("\"added\":[5]"));
            r2.C.Group(J("{\"op\":\"remove\",\"group\":\"ПВО-1\",\"uids\":[5]}"));
            k.Is("remove frees", r2.Own.Owner(5, out w) == null);
            r2.C.Group(J("{\"op\":\"disband\",\"group\":\"ПВО-1\"}"));
            k.Is("disband frees", r2.Own.Owner(1, out w) == null && r2.C.Busy(1) == null);
            k.Is("unknown group", r2.C.Order(J("{\"group\":\"X\"," + ZA + "}")) == "!unknown_group");
            return k.Err;
        }

        // ---------------- live finding 37: cap_order by bearing without an origin takes the group's carrier (as the window) ----------------
        private static string PolarDefaultOrigin()
        {
            var r = new Rig(); var k = new Chk();
            r.Create("1,2");
            string a = r.C.Order(J("{\"group\":\"ПВО-1\",\"bearing\":0,\"dist_nm\":60,\"size_nm\":20}"));
            k.Is("default origin ack " + a, !a.StartsWith("!"));
            var z = r.Gv().Rect;
            k.Is("zone centred 60 nm north of the deck", z != null && Math.Abs((z[0] + z[2]) / 2 - 11.0) < 1e-6 && Math.Abs((z[1] + z[3]) / 2 - 10.0) < 1e-6);
            a = r.C.Order(J("{\"group\":\"ПВО-1\",\"bearing\":0,\"dist_nm\":60,\"size_nm\":20,\"origin_uid\":" + CapFakeGame.ShipUid + "}"));
            z = r.Gv().Rect;
            k.Is("explicit origin_uid kept " + a, !a.StartsWith("!") && z != null && Math.Abs((z[1] + z[3]) / 2 - 11.0) < 1e-6);
            k.Is("unknown origin_uid", r.C.Order(J("{\"group\":\"ПВО-1\",\"bearing\":0,\"dist_nm\":60,\"size_nm\":20,\"origin_uid\":999}")) == "!no_origin");
            return k.Err;
        }

        // ---------------- D2 ----------------
        private static string OrderFlow()
        {
            var r = new Rig(); var k = new Chk();
            r.Create("1,2,3,4");
            string a = r.Ord("ПВО-1", ZA, 2, 1);
            k.Is("order ack " + a, !a.StartsWith("!"));
            r.Step(4);
            k.Is("2 allowed orders", r.Jn("order 100 F-14A AirToAirLongRange none allow") == 2);
            k.Is("1 reserve order", r.Jn("order 100 F-14A AirToAirLongRange none hold") == 1);
            k.Is("reserve is ready on deck", r.G.Bird(3).State == "Ready" && r.G.Bird(4).State == "Hangar");
            k.Is("roe tight, prev kept", r.Jn("roe 1 tight prev=free") == 1 && r.G.Bird(1).Roe == "tight");
            k.Is("racetrack 4 pts + the closing point (T-235)", r.Jn("patrol 1 5") >= 1);
            bool inside = r.G.Bird(1).Route.Count == 5;
            foreach (var p in r.G.Bird(1).Route) inside &= CapMath.DistToZoneNm(p, new CapZone { Rect = new[] { 10.5, 11, 10.83333, 11.66667 } }) <= 0;
            k.Is("route inside the zone", inside);
            k.Is("lanes differ", r.G.Bird(1).Route[0].Lat != r.G.Bird(2).Route[0].Lat);
            k.Is("started event", r.G.EventCount("cap_started") == 1);
            k.Is("ready count in view", r.Gv().Line.Ready == 1);
            r.Step(900);
            k.Is("on station events", r.G.EventCount("cap_on_station") == 2 && r.Gv().Line.OnStation == 2);
            int before = r.G.Patrols;
            r.Step(1500);
            k.Is("route re-issued at <=1 point left", r.G.Patrols > before);

            // unit anchor
            var r2 = new Rig();
            r2.Create("1,2");
            r2.Ord("ПВО-1", ZA, 1, 0, ",\"anchor_uid\":200");
            r2.Step(30);
            double lon0 = r2.G.Bird(1).Route[0].Lon;
            r2.G.Ship = new GeoPt(10.0, 11.0 + 10.0 / 60);
            r2.Step(2);
            double lon1 = r2.G.Bird(1).Route[0].Lon;
            k.Is("anchored zone moved " + (lon1 - lon0), Math.Abs(lon1 - lon0 - 10.0 / 60) < 1e-3);
            r2.G.Ship = null;
            r2.Step(2);
            k.Is("anchor lost", AllEv(r2.G, "cap_warn").Contains("anchor_lost") && Math.Abs(r2.G.Bird(1).Route[0].Lon - lon1) < 1e-3);
            return k.Err;
        }

        // ---------------- D3 ----------------
        private static string Relief()
        {
            var k = new Chk();
            // planned relief by fuel: the reliever is in the zone before the leaver goes, cruise speed
            var r = new Rig();
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            k.Is("bird 1 on station", r.Gv().Line.OnStation == 1);
            r.G.Bird(1).ReserveNm = 230;
            int gapsBefore = r.G.Logs.FindAll(x => x.Contains("continuity_gap")).Count;
            int relEvt = -1, rtbEvt = -1; bool relIn = false, gapSeen = false;
            for (int t = 0; t < 2500 && rtbEvt < 0; t++)
            {
                r.Step(1);
                if (relEvt < 0 && r.G.EventCount("cap_relief") > 0) relEvt = t;
                if (r.G.EventCount("cap_rtb") > 0) { rtbEvt = t; relIn = r.G.Bird(2).State == "Air" && r.InZone(2); }
                if (r.G.Logs.FindAll(x => x.Contains("continuity_gap")).Count > gapsBefore) gapSeen = true;
            }
            k.Is("relief before leave", relEvt >= 0 && rtbEvt > relEvt);
            k.Is("reliever in zone at leave", relIn);
            k.Is("relief event", (r.G.LastEvent("cap_relief") ?? "").Contains("\"reason\":\"fuel\"") && (r.G.LastEvent("cap_relief") ?? "").Contains("\"out\":1") && (r.G.LastEvent("cap_relief") ?? "").Contains("\"in\":2"));
            k.Is("fuel leave: cruise, no throttle", (r.G.LastEvent("cap_rtb") ?? "").Contains("\"reason\":\"fuel\"") && (r.G.LastEvent("cap_rtb") ?? "").Contains("\"speed\":\"cruise\"") && r.Jn("throttle") == 0);
            k.Is("no gap", !gapSeen);

            // missiles: immediate leave and relief, fast return, throttle restored after landing
            var m = new Rig();
            m.Create("1,2"); m.Ord("ПВО-1", ZA, 1, 1);
            m.Step(900);
            m.G.Bird(1).AamNow = 1;
            m.Step(1);
            string rtb = m.G.LastEvent("cap_rtb") ?? "";
            k.Is("missiles rtb " + rtb, rtb.Contains("\"reason\":\"missiles\"") && rtb.Contains("\"speed\":\"max\""));
            k.Is("rtb + max throttle", m.Jn("rtb 1") == 1 && m.Jn("throttle 1 4") == 1 && m.G.Bird(1).SpeedKn == 620);
            m.Step(3);
            k.Is("immediate relief launch", m.G.Bird(2).State == "Air");
            for (int t = 0; t < 900 && m.G.Bird(1).State == "Air"; t++) m.Step(1);
            k.Is("landed", m.Jn("landed 1") == 1);
            k.Is("throttle restored after landing", m.Jn("throttle 1 2") == 1 && m.G.Bird(1).Throttle == 2);

            // fuel just enough for a middle step: compromise
            var c = new Rig(x => x.FuelFloorPct = 0.15);
            c.Create("1,2"); c.Ord("ПВО-1", ZA, 1, 1);
            c.Step(900);
            var b1 = c.G.Bird(1); b1.Lat = 10.0; b1.Lon = 11.25; b1.ReserveNm = 360; b1.AamNow = 1;   // D = 75 nm
            c.Step(1);
            k.Is("compromise step 3 " + (c.G.LastEvent("cap_rtb") ?? ""), (c.G.LastEvent("cap_rtb") ?? "").Contains("\"speed\":\"3\"") && c.G.Bird(1).Throttle == 3);
            // not enough for any faster step
            var n = new Rig(x => x.FuelFloorPct = 0.15);
            n.Create("1,2"); n.Ord("ПВО-1", ZA, 1, 1);
            n.Step(900);
            b1 = n.G.Bird(1); b1.Lat = 10.0; b1.Lon = 11.25; b1.ReserveNm = 320; b1.AamNow = 1;
            n.Step(1);
            k.Is("low fuel -> cruise", (n.G.LastEvent("cap_rtb") ?? "").Contains("\"speed\":\"cruise\"") && n.Jn("throttle") == 0);
            // dash_down in flight, never up
            var d = new Rig(x => x.FuelFloorPct = 0.15);
            d.Create("1,2"); d.Ord("ПВО-1", ZA, 1, 1);
            d.Step(900);
            d.G.Bird(1).AamNow = 1; d.Step(5);
            k.Is("dash at max", d.G.Bird(1).Throttle == 4);
            d.G.Bird(1).ReserveNm = 330;
            d.Step(2);
            k.Is("dash down", d.G.Bird(1).Throttle == 2 && d.G.LogHas("dash_down"));
            d.G.Bird(1).ReserveNm = 5000;
            d.Step(3);
            k.Is("never up", d.G.Bird(1).Throttle == 2);
            // steps unavailable during the dash: fall back to the saved cruise step and stop the dash
            var ns = new Rig(x => x.FuelFloorPct = 0.15);
            ns.Create("1,2"); ns.Ord("ПВО-1", ZA, 1, 1);
            ns.Step(900);
            ns.G.Bird(1).AamNow = 1; ns.Step(5);
            k.Is("ns dash at max", ns.G.Bird(1).Throttle == 4);
            ns.G.NoSteps = true;
            ns.Step(3);
            k.Is("no steps -> cruise step", ns.G.Bird(1).Throttle == 2 && ns.G.LogHas("dash_down"));
            // external throttle change = player took
            var e = new Rig();
            e.Create("1,2"); e.Ord("ПВО-1", ZA, 1, 1);
            e.Step(900);
            e.G.Bird(1).AamNow = 1; e.Step(3);
            int th = e.Jn("throttle");
            e.G.Bird(1).Throttle = 3;
            e.Step(2);
            k.Is("external throttle -> player_took", e.G.EventCount("cap_warn") >= 1 && AllEv(e.G, "cap_warn").Contains("player_took") && e.Jn("throttle") == th && e.Own.Owner(1, out _) == null);
            // dash off by config
            var o = new Rig(p => p.DashOnMissileRtb = false);
            o.Create("1,2"); o.Ord("ПВО-1", ZA, 1, 1);
            o.Step(900); o.G.Bird(1).AamNow = 1; o.Step(1);
            k.Is("DashOnMissileRtb=false", o.Jn("throttle") == 0 && (o.G.LastEvent("cap_rtb") ?? "").Contains("cruise"));
            // return control during dash restores the throttle
            var q = new Rig();
            q.Create("1,2"); q.Ord("ПВО-1", ZA, 1, 1);
            q.Step(900); q.G.Bird(1).AamNow = 1; q.Step(2);
            q.C.ReleaseUid(1);
            k.Is("release restores throttle + control", q.G.Bird(1).Throttle == 2 && q.Jn("restore 1") == 1);

            // rotation without reserve: gap
            var g = new Rig();
            g.Create("1"); g.Ord("ПВО-1", ZA, 1, 0);
            g.Step(900);
            g.G.Bird(1).ReserveNm = 120;
            g.Step(2);
            k.Is("continuity gap", AllEv(g.G, "cap_warn").Contains("continuity_gap") && g.Gv().Line.GapS > 0 && g.Gv().Line.Warns.Contains("continuity_gap"));

            // no_ammo -> next loadout
            var a = new Rig();
            a.G.NoAmmo.Add("AirToAirLongRange");
            a.Create("1,2"); a.Ord("ПВО-1", ZA, 1, 0);
            a.Step(3);
            k.Is("fallback loadout", a.Jn("order 100 F-14A AirToAir none allow") == 1 && AllEv(a.G, "cap_warn").Contains("loadout_fallback") && a.G.Bird(1).Loadout == "AirToAir");

            // foreign request is never cancelled; own surplus is
            var f = new Rig();
            var ft = f.G.AddForeignTask("F-14A", "AirToAirLongRange");
            f.Create("1,2,3"); f.Ord("ПВО-1", ZA, 1, 1);
            f.Step(5);
            f.Ord("ПВО-1", ZA, 1, 0);
            f.Step(3);
            bool foreignAborted = false; foreach (string j in f.G.Journal) if (j == "abort " + ft.Key) foreignAborted = true;
            k.Is("own surplus aborted", f.Jn("abort ") >= 1);
            k.Is("foreign kept", !foreignAborted && ft.State != "Done");
            return k.Err;
        }

        // ---------------- D4 ----------------
        private static Rig OnStation(string uids, int n, int kk, int steps = 900)
        {
            var r = new Rig();
            r.Create(uids); r.Ord("ПВО-1", ZA, n, kk);
            r.Step(steps);
            return r;
        }

        private static string Combat()
        {
            var k = new Chk();
            var r = OnStation("1,2", 1, 1);
            r.G.Bird(1).AamNow = 1; r.G.Bird(1).Engaged = true;
            r.Step(2);
            k.Is("postponed combat", (r.G.LastEvent("cap_postponed") ?? "").Contains("combat") && (r.G.LastEvent("cap_postponed") ?? "").Contains("\"on\":true") && r.G.EventCount("cap_rtb") == 0);
            r.G.Bird(1).Engaged = false;
            r.Step(2);
            k.Is("postpone ends, leaves", r.G.EventCount("cap_rtb") == 1 && (r.G.LastEvent("cap_postponed") ?? "").Contains("\"on\":false"));

            var h = OnStation("1,2", 1, 1);
            var b = h.G.Bird(1); b.AamNow = 1;
            h.G.Contacts.Add(new CapContact { Id = 50, Lat = b.Lat + 5.0 / 60, Lon = b.Lon, Hostile = true });
            h.Step(1);
            k.Is("postponed hostile_near", (h.G.LastEvent("cap_postponed") ?? "").Contains("hostile_near") && h.G.EventCount("cap_rtb") == 0);
            h.G.Contacts.Clear();
            h.Step(2);
            k.Is("hostile gone, leaves", h.G.EventCount("cap_rtb") == 1);

            var z = OnStation("1,2", 1, 1);
            z.G.Bird(1).AamNow = 0; z.G.Bird(1).Engaged = true;
            z.Step(1);
            k.Is("no AAM -> no postpone", z.G.EventCount("cap_rtb") == 1 && z.G.EventCount("cap_postponed") == 0);

            var bi = OnStation("1,2", 1, 1);
            bi.G.Bird(1).GameBingo = true;
            bi.Step(1);
            k.Is("bingo", (bi.G.LastEvent("cap_rtb") ?? "").Contains("game_bingo") && bi.G.EventCount("cap_warn") >= 1 && AllEv(bi.G, "cap_warn").Contains("bingo_fuel")
                          && bi.Jn("rtb 1") == 0 && bi.Jn("throttle") == 0);

            var w = OnStation("1,2,3,4", 2, 1);
            w.G.Bird(1).GameWinchester = true; w.G.Bird(2).GameRtb = true;
            w.Step(1);
            string ev = ""; foreach (var e in w.G.Events) if (e.Key == "cap_rtb") ev += e.Value;
            k.Is("winchester + game rtb", ev.Contains("game_winchester") && ev.Contains("game_rtb") && w.Jn("rtb ") == 0);
            k.Is("winchester dash (controllable)", w.Jn("throttle 1") == 1 && w.Jn("throttle 2") == 0);
            var slots = new List<int>(); foreach (var m in w.Gv().Members) slots.Add(m.Slot);
            k.Is("slots freed", slots[0] == -1 && slots[1] == -1);

            var l = OnStation("1,2", 1, 1);
            int p0 = l.Jn("patrol 1 ");
            l.G.Bird(1).Lat = 10.83333 + 30.0 / 60; l.G.Bird(1).Lon = 11.3;
            l.Step(1);
            k.Is("leash", l.Jn("break 1") == 1 && l.G.EventCount("cap_leash") == 1 && l.Jn("patrol 1 ") > p0);

            var ev2 = new Rig();
            ev2.Create("1,2"); ev2.Ord("ПВО-1", ZA, 1, 0);
            ev2.G.Bird(1).Evading = true;
            ev2.Step(5);
            k.Is("evading: no route", ev2.G.Bird(1).State == "Air" && ev2.Jn("patrol 1 ") == 0);
            ev2.G.Bird(1).Evading = false;
            ev2.Step(2);
            k.Is("route after evasion", ev2.Jn("patrol 1 ") >= 1);

            var tc = new Rig();
            tc.Create("1,2"); tc.Ord("ПВО-1", ZA, 1, 0);
            tc.G.Scale = 4; tc.G.DropOrdersAtTc = true;
            tc.Step(6);
            k.Is("tc_blocked", AllEv(tc.G, "cap_warn").Contains("tc_blocked") && tc.Gv().Line.Warns.Contains("tc_blocked"));
            tc.G.Scale = 1;
            tc.Step(3);
            k.Is("tc_blocked cleared", !tc.Gv().Line.Warns.Contains("tc_blocked") && tc.G.Bird(1).Route.Count == 5);
            return k.Err;
        }

        // ---------------- fuel data fallback (coordinator) ----------------
        private static string NoFuelData()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Bird(1).MaxRangeNm = 0;                      // no fuel data at all
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            string w = ""; foreach (var e in r.G.Events) if (e.Key == "cap_warn") w += e.Value;
            k.Is("no_fuel_data warning", w.Contains("no_fuel_data") && w.Contains("fixed_time"));
            r.Step(2100);
            k.Is("still on station at 50 min", r.G.EventCount("cap_rtb") == 0 && r.Gv().Line.OnStation == 1);
            r.Step(3000);   // T-330: the fixed-time model leaves at the 5 % floor (about 85 min), was 15 % (about 76 min)
            k.Is("fixed time -> fuel leave", (r.G.LastEvent("cap_rtb") ?? "").Contains("\"reason\":\"fuel\""));

            var f = new Rig();
            f.G.Bird(1).MaxRangeNm = 0; f.G.Bird(1).FuelFrac = 0.5;
            f.Create("1,2"); f.Ord("ПВО-1", ZA, 1, 1);
            f.Step(900);
            string w2 = ""; foreach (var e in f.G.Events) if (e.Key == "cap_warn") w2 += e.Value;
            k.Is("fuel fraction fallback", w2.Contains("fuel_fraction") && f.G.EventCount("cap_rtb") == 0);
            f.G.Bird(1).FuelFrac = 0.04;
            f.Step(2);
            k.Is("fraction under the floor -> leave", (f.G.LastEvent("cap_rtb") ?? "").Contains("\"reason\":\"fuel\""));
            return k.Err;
        }

        // ---------------- launch task uid (coordinator follow-up) ----------------
        private static string TaskUid()
        {
            var k = new Chk();
            var r = new Rig();
            string ow; r.G.RemapOnOrder = true;                         // the game shows the launch task under another uid
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 0);
            r.Step(900);
            k.Is("one order only", r.Jn("order ") == 1);
            k.Is("member followed the task uid", r.Jn("launched 1001") == 1 && r.Own.Owner(1001, out ow) == "ПВО-1");
            k.Is("on station", r.Gv().Line.OnStation == 1);
            r.Step(60);
            k.Is("still one order", r.Jn("order ") == 1);
            return k.Err;
        }

        // coordinator item 2: deck task uid -> airborne unit uid after take-off
        private static string Takeoff()
        {
            var k = new Chk();
            var r = new Rig();
            string ow; r.G.RemapOnOrder = true; r.G.RenumberOnTakeoff = true;
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 0);
            r.Step(900);
            k.Is("one order only", r.Jn("order ") == 1);
            k.Is("member follows the airborne unit " + r.G.Journal.Count, r.Jn("launched 3001") == 1 && r.Own.Owner(3001, out ow) == "ПВО-1" && r.Own.Owner(1001, out ow) == null);
            k.Is("not lost", !r.G.LogHas("(lost)"));
            k.Is("on station", r.Gv().Line.OnStation == 1);
            k.Is("roe and route given to the new uid", r.G.Bird(3001).Roe == "tight" && r.G.Bird(3001).Route.Count > 0);
            // a foreign airborne unit of the same type must not be taken
            var r2 = new Rig();
            r2.G.RenumberOnTakeoff = true;
            r2.G.All.Add(new FakeBird { Uid = 77, Name = "Foreign", State = "Air", Lat = 10.0, Lon = 10.0 });
            r2.Create("1,2"); r2.Ord("ПВО-1", ZA, 1, 0);
            r2.Step(900);
            string ow2;
            k.Is("foreign unit untouched", r2.Own.Owner(77, out ow2) == null);
            return k.Err;
        }

        // 0.27.1 live: the pending task became a LaunchTask (machine spawned with its final uid, taxiing for minutes, not in flight); the member must follow it
        private static string TaxiTakeoff()
        {
            var k = new Chk();
            var r = new Rig();
            string ow; r.G.RemapOnOrder = true; r.G.RenumberOnTakeoff = true; r.G.TaxiS = 240;
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 0);
            int tick = 0; bool sawLaunch = false;
            while (tick++ < 1200 && r.G.Journal.IndexOf("airborne 3001") < 0)
            {
                r.Step(1);
                if (r.G.Bird(3001) != null && r.G.Bird(3001).State == "Taxi")
                {
                    var gv = r.Gv(); var mv = gv.Members.Find(x => x.Uid == 3001);
                    if (mv != null && mv.Where == "deck_launch") sawLaunch = true;
                }
            }
            k.Is("machine took off " + tick, r.G.Journal.IndexOf("airborne 3001") >= 0);
            k.Is("taxi phase seen as deck_launch member", sawLaunch);
            k.Is("not lost while taxiing", !r.G.LogHas("(lost)"));
            k.Is("member follows the unit", r.Own.Owner(3001, out ow) == "ПВО-1" && r.Own.Owner(1001, out ow) == null);
            r.Step(900);
            k.Is("on station and orders given to the airborne uid", r.G.Bird(3001).Roe == "tight" && r.G.Bird(3001).Route.Count > 0 && r.Gv().Line.OnStation == 1);
            k.Is("one order only", r.Jn("order ") == 1);
            // a foreign machine taxiing out of the same deck must not be taken, and the old gap (no row at all) still ends as lost without a crash
            var r2 = new Rig();
            r2.G.RenumberOnTakeoff = true; r2.G.TaxiS = 240; r2.G.ListTaxi = false;
            r2.Create("1,2"); r2.Ord("ПВО-1", ZA, 1, 0);
            r2.Step(1200);
            k.Is("no row, no crash: lost after the grace", r2.G.LogHas("(lost)"));
            return k.Err;
        }

        // member view of a pending launch: time to ready and to take-off (deck_prep: ready_in + launch delay)
        private static string DeckTimes()
        {
            var k = new Chk();
            var r = new Rig(p => p.LaunchDelayS = 180);
            r.G.Mode = "Realistic";
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 0);
            r.Step(5);
            MemberTimes(k, r);
            return k.Err;
        }

        // live finding (AAR list): a hangar row without a CAP loadout has only a guessed readiness; the view must not show it as a real time
        private static string HangarGuess()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Bird(2).ReadyGuess = true;
            r.Create("1,2");
            r.Step(2);
            CapMemberView known = null, guess = null;
            foreach (var m in r.Gv().Members) { if (m.Uid == 1) known = m; if (m.Uid == 2) guess = m; }
            k.Is("both hangar rows in the view", known != null && guess != null && known.Where == "hangar" && guess.Where == "hangar");
            if (known == null || guess == null) return k.Err;
            k.Is("a real readiness stays known", known.ReadyKnown);
            k.Is("a guessed readiness is unknown in the view", !guess.ReadyKnown && guess.ReadyInS == 900);
            k.Is("the window shows no time for a guess", CapWindow.In("ru", () => CapWindowModel.MemberCells(guess, null))[CapWindowModel.CWhere] == "в ангаре");
            return k.Err;
        }

        private static void MemberTimes(Chk k, Rig r)
        {
            bool any = false;
            foreach (var m in r.Gv().Members)
                if (m.Where == "deck_prep" && m.TakeoffInS >= 0) { any = true; k.Is("takeoff = ready + 180 s", Math.Abs(m.TakeoffInS - (m.ReadyInS + 180)) < 1 && m.ReadyInS > 1); }
            k.Is("a preparing member shows ready and take-off times", any);
        }

        // ---------------- D5 ----------------
        private static string Priority()
        {
            var k = new Chk();
            var r = new Rig();
            r.Own.GroupSeq("asw", "ПЛО-1");
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 2, 0);
            Func<string> win = () =>
            {
                var l = new List<AirGroupPrio> { new AirGroupPrio { Name = "ПЛО-1", Order = "asw", HasContact = true, WindowIndex = 1 } };
                l.AddRange(r.C.DeckPrio());
                var first = AirOpsMath.DeckOrder(l)[0];   // who gets the deck first
                foreach (var x in l) if (x.Name == first) return x.Order == "cap" ? "cap" : "asw";
                return "asw";
            };
            r.Step(3);
            k.Is("no threat: asw first", win() == "asw" && !r.C.DeckPrio()[0].CapGapUnderThreat);
            var c = new CapContact { Id = 77, Lat = 10.83333 + 70.0 / 60, Lon = 11.3, Hostile = true };
            r.G.Contacts.Add(c);
            r.Step(1);
            string pe = r.G.LastEvent("cap_priority") ?? "";
            k.Is("threat 70: cap first " + pe, win() == "cap" && pe.Contains("\"on\":true") && pe.Contains("\"threat_uid\":77") && r.G.LogHas("deck priority on") && r.Gv().Line.DeckPriority);
            c.Lat = 10.83333 + 85.0 / 60;
            r.Step(100);
            k.Is("85 nm holds", win() == "cap");
            c.Lat = 10.83333 + 95.0 / 60;
            r.Step(59);
            k.Is("59 s still on", win() == "cap");
            r.Step(2);
            pe = r.G.LastEvent("cap_priority") ?? "";
            k.Is("60 s off", win() == "asw" && pe.Contains("\"on\":false") && r.G.LogHas("deck priority off"));

            // own groups follow DeckOrder
            var t = new Rig(); t.C.TraceOrder = true;
            t.Create("1,2"); t.Create("5,6");
            t.Ord("ПВО-1", ZA, 1, 0); t.Ord("ПВО-2", ZB, 1, 0);
            t.G.Contacts.Add(new CapContact { Id = 90, Lat = 12.33333 + 70.0 / 60, Lon = 11.3, Hostile = true });
            t.Step(2);
            t.G.Logs.Clear();
            t.Step(1);
            int i1 = t.G.Logs.FindIndex(x => x.Contains("tick ПВО-1")), i2 = t.G.Logs.FindIndex(x => x.Contains("tick ПВО-2"));
            k.Is("group with threat ticks first", i2 >= 0 && i1 > i2);
            return k.Err;
        }

        // ---------------- T-119 C: the controller talks to the deck through the broker ----------------
        private sealed class RecDeck : IAirDeck
        {
            public readonly List<DeckRequest> Reqs = new List<DeckRequest>();
            public readonly List<string> Decl = new List<string>();
            public readonly List<AirGroupPrio> DeclPrio = new List<AirGroupPrio>();
            public bool Answer;     // false: requests stay unanswered
            public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids)
            {
                Decl.Add(window + "|" + prio.Name + "|" + string.Join(",", deckUids));
                DeclPrio.Add(prio);
            }
            public int Submit(DeckRequest r, Action<DeckResult> done)
            {
                r.Ticket = Reqs.Count + 1;
                Reqs.Add(r);
                if (Answer && done != null) done(new DeckResult { Ticket = r.Ticket, Key = "task:r" + r.Ticket });
                return r.Ticket;
            }
            public double HeldS(string window, string group) { return -1; }
            public bool DeckDown(int d) { return false; }
        }

        private static string DeckBroker()
        {
            var k = new Chk();
            // the reserve K is ordered in advance with allow=false; a relief only allows the prepared key (ADR-0042 item 5)
            var r = new Rig(); r.Create("1,2,3,4"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            k.Is("reserve ordered with allow=false", r.Jn("order 100 F-14A AirToAirLongRange none hold") == 1 && r.Jn("allow ") == 0);
            int orders = r.Jn("order ");
            r.G.Bird(1).AamNow = 1; r.Step(10);
            k.Is("relief allows the prepared key", r.Jn("allow ") >= 1 && r.Jn("order ") <= orders + 1);

            // facts: window, group number from the shared ownership, decks of the members that are not in the air
            var rec = new RecDeck();
            var d = new Rig(); d.Create("1,2"); d.Ord("ПВО-1", ZA, 1, 1);
            d.C.Deck = rec;
            d.G.Advance(1); d.C.Tick(d.G.Now);
            k.Is("declared", rec.Decl.Count >= 1 && rec.Decl[0].StartsWith("cap|ПВО-1|"));
            k.Is("declared number", rec.DeclPrio.Count >= 1 && rec.DeclPrio[0].WindowIndex == d.Own.GroupSeq("cap", "ПВО-1") && rec.DeclPrio[0].Window == "cap" && rec.DeclPrio[0].Order == "cap");
            k.Is("shared ownership", ReferenceEquals(d.C.Ownership, d.Own));

            // requests: Window cap, Prio, no second request while the first one is unanswered
            var u = new Rig(); u.Create("1,2,3"); u.Ord("ПВО-1", ZA, 1, 1);
            var un = new RecDeck();
            u.C.Deck = un;
            for (int i = 0; i < 12; i++) { u.G.Advance(1); u.C.Tick(u.G.Now); }
            int ord = 0; foreach (var q in un.Reqs) if (q.Kind == DeckKind.Order) { ord++; k.Is("request window/group", q.Window == "cap" && q.Group == "ПВО-1" && q.Prio != null && q.Prio.Window == "cap"); }
            k.Is("one order per member, none repeated: " + ord, ord == 2);
            k.Is("nothing reached the game without an answer", u.Jn("order ") == 0);

            // no broker: nothing is submitted, one line in the log
            var nb = new Rig(); nb.Create("1,2"); nb.Ord("ПВО-1", ZA, 1, 0);
            for (int i = 0; i < 5; i++) { nb.G.Advance(1); nb.C.Tick(nb.G.Now); }
            k.Is("no broker: no orders", nb.Jn("order ") == 0 && nb.G.LogHas("deck broker is off"));

            // GroupSeq from the save survives a load (the new group takes max + 1)
            var l = new Rig();
            var sd = new CapSaveData();
            sd.Groups.Add(new CapGroupDto { Group = "ПВО-7", Seq = 7 });
            sd.Groups.Add(new CapGroupDto { Group = "ПВО-9", Seq = 9 });
            l.C.Load(sd, l.G.Now);
            k.Is("restored seq", l.Own.GroupSeq("cap", "ПВО-7") == 7 && l.Own.GroupSeq("cap", "ПВО-9") == 9);
            string made = l.Create("1,2");
            k.Is("new group after the restored ones", made.Contains("ПВО-10") && l.Own.GroupSeq("cap", "ПВО-10") == 10);
            return k.Err;
        }

        // ---------------- D6 ----------------
        private static string Control()
        {
            var k = new Chk();
            string w;
            var r = OnStation("1,2,3,4", 2, 1);
            // player takes bird 1
            r.G.Bird(1).Taken = true;
            int patrols = r.Jn("patrol 1 ");
            r.Step(3);
            k.Is("player_took", AllEv(r.G, "cap_warn").Contains("player_took") && r.Own.Owner(1, out w) == null && r.Jn("patrol 1 ") == patrols && r.Jn("rtb 1") == 0);
            // return control
            r.C.ReleaseUid(2);
            k.Is("release", r.Jn("restore 2") == 1 && r.Own.Owner(2, out w) == null && r.C.Busy(2) == null);

            // save / load
            var s = OnStation("1,2,3,4", 2, 1);
            s.G.AddForeignTask("F-14A", "AirToAirLongRange");
            var d0 = s.C.Save();
            string err;
            var d = CapSave.Parse(CapSave.ToJson(d0), out err);
            k.Is("save parse", err == null && d.Groups.Count == 1 && d.Groups[0].Members.Count == 4);
            k.Is("save slots/aam", d.Groups[0].Members[0].Slot == 0 && d.Groups[0].Members[0].AamStart == 8 && d.Groups[0].Members[1].Slot == 1);
            k.Is("save keys", d.Groups[0].Keys.Count == 1 && d.Memory.Count == 2);
            d.Groups[0].Keys.Add("4|100|bogus");
            var own2 = new AirOwnership();
            var c2 = new CapController(s.G, s.P, own2);
            int orders = s.Jn("order ");
            c2.Load(d, s.G.Now);
            for (int i = 0; i < 5; i++) { s.G.Advance(1); s.G.Tick(c2); }
            var v = c2.View().Groups[0];
            k.Is("load: slots", v.Members[0].Slot == 0 && v.Members[1].Slot == 1 && v.Ordered && v.N == 2);
            k.Is("load: no duplicate orders", s.Jn("order ") == orders);
            k.Is("load: ownership", own2.Owner(1, out w) == "ПВО-1" && own2.GroupSeq("cap", "ПВО-1") >= 1);
            k.Is("load: bogus key dropped, foreign untouched", s.Jn("abort ") == 0);
            int pt = s.Jn("patrol ");
            for (int i = 0; i < 5; i++) { s.G.Advance(1); s.G.Tick(c2); }
            k.Is("load: still alive", s.G.Bird(1).Route.Count == 5);

            // mission change / persisted file
            string dir = Path.Combine(Path.GetTempPath(), "captest-" + Guid.NewGuid().ToString("N"));
            try
            {
                s.G.SavePath = "/x/A.sav"; s.G.SaveDirectory = dir;
                k.Is("persist", s.C.PersistNow() && Directory.GetFiles(Path.Combine(dir, "cap")).Length == 1);
                double t0 = s.C.Save().MissionTimeS;
                var o1 = new Rig(); o1.G.SavePath = "/x/a.sav"; o1.G.SaveDirectory = dir;
                o1.C.OnMissionChanged("/x/a.sav", t0);
                k.Is("restore auto", o1.C.View().Groups.Count == 1);
                var o2 = new Rig(p => p.RestoreOnLoad = "ask"); o2.G.SavePath = "/x/a.sav"; o2.G.SaveDirectory = dir;
                o2.C.OnMissionChanged("/x/a.sav", t0);
                bool pend = o2.C.View().RestorePending && o2.C.View().Groups.Count == 0;
                o2.C.ResolveRestore(true);
                k.Is("restore ask", pend && o2.C.View().Groups.Count == 1);
                var o3 = new Rig(p => p.RestoreOnLoad = "off"); o3.G.SavePath = "/x/a.sav"; o3.G.SaveDirectory = dir;
                o3.C.OnMissionChanged("/x/a.sav", t0);
                k.Is("restore off", o3.C.View().Groups.Count == 0);
                var o4 = new Rig(); o4.G.SavePath = "/x/a.sav"; o4.G.SaveDirectory = dir;
                o4.C.OnMissionChanged("/x/a.sav", t0 + 100);
                k.Is("restore stale", o4.C.View().Groups.Count == 0);
                { string w0; o1.C.OnMissionLeft(); k.Is("t130 left clears groups, birds and ownership", o1.C.View().Groups.Count == 0 && o1.C.ViewLight().Groups.Count == 0 && o1.Own.Owner(1, out w0) == null); }
                o1.C.OnMissionChanged(null, 0);
                k.Is("new mission clears", o1.C.View().Groups.Count == 0 && o1.Own.Owner(1, out w) == null);
                // T-122: path kept (cap/cap), second write leaves .bak, a broken main falls back to .bak, ClearSlot removes all
                string capFile = Path.Combine(dir, "cap", SaveSlotStore.Key("/x/A.sav") + ".json");
                k.Is("t122 path", Directory.GetFiles(Path.Combine(dir, "cap"))[0] == capFile);
                k.Is("t122 second write", s.C.PersistNow() && File.Exists(capFile + ".bak") && !File.Exists(capFile + ".tmp"));
                File.WriteAllText(capFile, "{broken");
                var o5 = new Rig(); o5.G.SavePath = "/x/a.sav"; o5.G.SaveDirectory = dir;
                o5.C.OnMissionChanged("/x/a.sav", t0);
                k.Is("t122 restore from .bak", o5.C.View().Groups.Count == 1);
                k.Is("t122 clear slot", s.C.ClearSlot("/x/A.sav") && !File.Exists(capFile) && !File.Exists(capFile + ".bak") && !s.C.ClearSlot("/x/A.sav"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

            // dash flag survives save/load
            var ds = OnStation("1,2", 1, 1);
            ds.G.Bird(1).AamNow = 1; ds.Step(3);
            var dd = ds.C.Save();
            k.Is("save dash", dd.Memory.Count >= 1 && dd.Memory[0].Dash && dd.Memory[0].Throttle == 2 && dd.Groups[0].Members[0].Slot == -2);
            var c3 = new CapController(ds.G, ds.P, new AirOwnership());
            c3.Load(CapSave.Parse(CapSave.ToJson(dd), out err), ds.G.Now);
            for (int i = 0; i < 900 && ds.G.Bird(1).State == "Air"; i++) { ds.G.Advance(1); ds.G.Tick(c3); }
            k.Is("loaded dash: not sent back to the zone, throttle restored", ds.Jn("landed 1") == 1 && ds.G.Bird(1).Throttle == 2);

            // state json and view
            var sv = OnStation("1,2", 1, 1);
            var st = (Dictionary<string, object>)Json.Parse(sv.C.StateJson());
            var gl = (List<object>)st["groups"];
            var g0 = (Dictionary<string, object>)gl[0];
            k.Is("state json", gl.Count == 1 && ((List<object>)g0["members"]).Count == 2 && (string)g0["group"] == "ПВО-1" && (double)g0["n"] == 1 && g0.ContainsKey("deck_priority") && st.ContainsKey("candidates"));
            string line;
            using (UiText.Push("en")) line = CapWindow.TaskLine(sv.Gv().Line);
            k.Is("view line " + line, line.Contains("on station 1/1") && line.Contains("40") );
            k.Is("view hides A-7E", sv.C.View().Candidates.TrueForAll(x => x.Uid != 9));

            // overlay path: no groups -> the light view never scans the game; the full view is cached for a tick second
            var nv = new Rig();
            nv.Step(3);
            int c0 = nv.G.BirdsCalls;
            for (int i = 0; i < 20; i++) { nv.C.ViewLight(); }
            k.Is("light view no Birds()", nv.G.BirdsCalls == c0 && nv.C.ViewLight().Candidates.Count == 0);
            nv.C.View(); nv.C.View(); nv.C.View();
            k.Is("full view cached", nv.G.BirdsCalls <= c0 + 1);
            int c1 = nv.G.BirdsCalls;
            nv.Step(2); nv.C.View();
            k.Is("full view refreshed after a second, no groups", nv.G.BirdsCalls == c1 + 1);
            return k.Err;
        }

        // ---------------- T-146: game-like data (VehicleType Plane/Helicopter, usn_* types, SAR capacity) ----------------
        private static string GameData()
        {
            var r = new Rig(); var k = new Chk();
            var g = r.G;
            g.All.Clear();
            g.VehicleClass["usn_f-14a"] = "Plane"; g.VehicleClass["usn_e-2c"] = "Plane"; g.VehicleClass["usn_sh-3h"] = "Helicopter";
            g.ExtraAmmo["aim-54a"] = new CapAmmo { Name = "aim-54a", Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 100 };
            g.ExtraAmmo["aim-7f"] = new CapAmmo { Name = "aim-7f", Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 24 };
            g.ExtraAmmo["aim-9l"] = new CapAmmo { Name = "aim-9l", Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 10 };
            g.ExtraAmmo["fuel-tank"] = new CapAmmo { Name = "fuel-tank", Type = "Fueltank", TargetType = "None", FuelKg = 1500 };
            Func<string, double, double, Dictionary<string, int>, LoadoutOption> lo = (n, up, cool, w) =>
                new LoadoutOption { Name = n, Display = n, Capacity = 0, ReadyUpMin = up, CoolDownMin = cool, Available = true, Weapons = w };
            g.ExtraLoadouts["usn_f-14a"] = new List<LoadoutOption>
            {
                lo("AirToAirLongRange", 15, 120, new Dictionary<string, int> { { "aim-54a", 4 }, { "aim-7f", 2 }, { "aim-9l", 2 }, { "fuel-tank", 2 } }),
                lo("AirToAir", 15, 120, new Dictionary<string, int> { { "aim-7f", 4 }, { "aim-9l", 4 } }),
                lo("AirToAirIntercept", 30, 120, new Dictionary<string, int> { { "aim-7f", 2 }, { "aim-9l", 2 } }),
                lo("Empty", 15, 120, new Dictionary<string, int>()),
                lo("Ferry", 15, 120, new Dictionary<string, int> { { "fuel-tank", 2 } }),
            };
            g.ExtraLoadouts["usn_e-2c"] = new List<LoadoutOption> { lo("AEW", 30, 120, new Dictionary<string, int>()) };
            var sh = lo("ASW", 20, 60, new Dictionary<string, int>()); sh.Capacity = 26;
            g.ExtraLoadouts["usn_sh-3h"] = new List<LoadoutOption> { sh };
            for (int i = 1; i <= 4; i++) g.All.Add(new FakeBird { Uid = i, Name = "F-14 " + i, Type = "usn_f-14a", Lat = g.Deck.Lat, Lon = g.Deck.Lon });
            g.All.Add(new FakeBird { Uid = 5, Name = "E-2 5", Type = "usn_e-2c", Lat = g.Deck.Lat, Lon = g.Deck.Lon });
            for (int i = 6; i <= 7; i++) g.All.Add(new FakeBird { Uid = i, Name = "SH-3 " + i, Type = "usn_sh-3h", Lat = g.Deck.Lat, Lon = g.Deck.Lon });
            k.Is("f14 admitted", g.TypeAdmission("usn_f-14a") == null);
            k.Is("e2c no_aam", g.TypeAdmission("usn_e-2c") == "no_aam");
            k.Is("sh3 helicopter", g.TypeAdmission("usn_sh-3h") == "helicopter");
            k.Is("capacity 0 is not a filter", g.Loadouts(CapFakeGame.DeckUid, "usn_f-14a")[0].Capacity == 0);
            r.Step(2);
            var v = r.C.View();
            int f14 = 0, other = 0;
            foreach (var c in v.Candidates) { if (c.Type == "usn_f-14a") f14++; else other++; }
            k.Is("4 f-14 in candidates, no e-2c/sh-3h (" + f14 + "/" + other + ")", f14 == 4 && other == 0);
            string a = r.Create("1,2");
            k.Is("create ack " + a, a.Contains("\"added\":[1,2]"));
            return k.Err;
        }

        private static void Dump()
        {
            var r = Rig.Last; if (r == null) return;
            int a = Math.Max(0, r.G.Journal.Count - 25);
            for (int i = a; i < r.G.Journal.Count; i++) Console.WriteLine("  J " + r.G.Journal[i]);
            a = Math.Max(0, r.G.Logs.Count - 25);
            for (int i = a; i < r.G.Logs.Count; i++) Console.WriteLine("  L " + r.G.Logs[i]);
        }

        // ---------------- 0.27.8 live fixes ----------------
        // our own RTB (the game's deck return route puts weapons on hold) is not the player; the leaving member is tracked until it lands
        private static string RtbNotPlayer()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.RoeDetector = true; r.G.GameRtbHold = true;
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            k.Is("on station", r.Gv().Line.OnStation == 1);
            int holder = r.G.Bird(1).State == "Air" ? 1 : 2;
            r.G.Bird(holder).ReserveNm = 60;
            bool sawLeaving = false;
            for (int t = 0; t < 1500 && r.Jn("landed " + holder) == 0; t++)
            {
                r.Step(1);
                var mv = r.Gv().Members.Find(x => x.Uid == holder);
                if (mv != null && mv.Where == "leaving") sawLeaving = true;
            }
            string ow;
            k.Is("fuel leave issued", r.Jn("rtb " + holder) == 1 && r.G.Bird(holder).Roe == "hold");
            k.Is("no player_took", !AllEv(r.G, "cap_warn").Contains("player_took") && !r.G.LogHas("(player_took)"));
            k.Is("tracked as leaving", sawLeaving);
            k.Is("landed and still a member", r.Jn("landed " + holder) == 1 && r.Own.Owner(holder, out ow) == "ПВО-1");
            // a real player change of ROE while on station is still detected
            var p = new Rig();
            p.G.RoeDetector = true;
            p.Create("1,2"); p.Ord("ПВО-1", ZA, 1, 1);
            p.Step(900);
            int h2 = p.G.Bird(1).State == "Air" ? 1 : 2;
            p.G.Bird(h2).Roe = "free";
            p.Step(2);
            k.Is("player ROE change -> player_took", AllEv(p.G, "cap_warn").Contains("player_took") && p.Own.Owner(h2, out ow) == null);
            return k.Err;
        }

        // the reserve's pending task (launch not allowed) stays on the deck while Birds() misses its row; when the task really vanishes the
        // member takes a free airframe of its type on the deck instead of being dropped as lost
        private static string DeckTaskKept()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            FakeTask held = r.G.Tasks.Find(t => t.Ours && !t.Allow && t.State != "Done");
            k.Is("reserve task held on deck", held != null);
            if (held == null) return k.Err;
            int uid = held.BirdUid;
            string ow;
            r.G.HideUids.Add(uid);
            r.Step(40);
            k.Is("row missing, task present: kept", !r.G.LogHas("(lost)") && r.Own.Owner(uid, out ow) == "ПВО-1" && r.G.LogHas("deck row missing, task present"));
            r.G.HideUids.Clear();
            r.Step(5);
            k.Is("same task, no new order", r.Gv().Members.Exists(x => x.Uid == uid) && r.G.Tasks.Contains(held) && held.State != "Done");
            int orders = r.Jn("order ");
            // the game drops the task without a launch and without giving the airframe back
            held.State = "Done"; r.G.Bird(uid).State = "Gone";
            r.Step(40);
            k.Is("not lost", !r.G.LogHas("(lost)"));
            k.Is("re-adopted a hangar airframe", r.G.LogHas("readopt (task gone)") && r.Own.Owner(3, out ow) == "ПВО-1" && r.Own.Owner(uid, out ow) == null);
            k.Is("reserve ordered again", r.Jn("order ") == orders + 1 && r.G.Bird(3).State != "Hangar");
            return k.Err;
        }

        // live 0.28.3: an AS-4 hit knocked the carrier's flight deck out; the game cleared every task (KillAircraft) and runs none until repaired.
        // The members take hangar airframes (journal: "task gone" with the deck list) but no launch is ordered on the dead deck; after the repair it is.
        private static string DeckKnockedOut()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic";
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(5);
            k.Is("two launch tasks", r.G.Tasks.FindAll(t => t.Ours && t.State != "Done").Count == 2);
            int orders = r.Jn("order ");
            r.G.KnockOutDeck();
            r.Step(20);   // past the 15 s adopt grace
            string ow;
            k.Is("task gone logged with the deck state", r.G.LogHas("task gone uid=") && r.G.LogHas("inoperable=True"));
            k.Is("hangar airframes re-adopted, not lost", !r.G.LogHas("(lost)") && r.Own.Owner(3, out ow) == "ПВО-1" && r.Own.Owner(4, out ow) == "ПВО-1");
            k.Is("no order on the dead deck", r.Jn("order ") == orders && !r.G.LogHas("order launch failed"));
            var gv = r.Gv();
            k.Is("members wait as deck_down", gv.Members.TrueForAll(m => m.Where == "deck_down"));
            k.Is("warn deck_down", gv.Line.Warns != null && gv.Line.Warns.Contains("deck_down") && AllEv(r.G, "cap_warn").Contains("deck_down"));
            r.Step(30);
            k.Is("still no order", r.Jn("order ") == orders);
            r.G.DeckOut = false;   // damage control repaired the deck
            r.Step(5);
            k.Is("ordered again after the repair", r.Jn("order ") == orders + 2);
            k.Is("warn cleared", !r.Gv().Line.Warns.Contains("deck_down") && r.G.LogHas("deck back in service"));
            return k.Err;
        }

        // fuel from the game's numbers (F-14A live: _maxRangeInKm 3902, RangeOnMap radius 807 nm while ActualRangeInKm is the path)
        private static string GameFuelMath()
        {
            var k = new Chk();
            const double mu = 1000.0 * 0.0148809375;
            double res, max, frac;
            CapMath.GameFuel(3000, 1494 * mu, mu, 3300, 3902, out res, out max, out frac);
            k.Is("path from ActualRangeInKm", Math.Abs(res - 3000 / 1.852) < 0.1 && Math.Abs(max - 3902 / 1.852) < 0.1 && Math.Abs(frac - 3300 / 3902.0) < 1e-6);
            CapMath.GameFuel(null, 1494 * mu, mu, null, 3902, out res, out max, out frac);
            k.Is("fallback RangeOnMap", Math.Abs(res - 1494 / 1.852) < 0.1 && frac == -1);
            CapMath.GameFuel(null, 2755922, mu, null, 3902, out res, out max, out frac);
            k.Is("unlimited fuel / no data", res == 0 && max == 0);
            CapMath.GameFuel(-50, null, mu, 100, 3902, out res, out max, out frac);
            k.Is("cannot make it: reserve 0, floor model", res == 0 && max > 0);
            // the old pairing (radius vs 15 % of the full path) left at 316 nm radius = about 630 nm of path still in the tanks
            var p = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: explicit floor (default off)
            var b = new CapBird { Where = "zone", AamNow = 8, AamStart = 8, LongNow = 6, LongStart = 6, PatrolKn = 420, MaxRangeNm = 3902 / 1.852, ReserveNm = 2 * 316 };
            string pp;
            k.Is("632 nm of path is not a fuel leave", CapMath.LeaveReason(b, 0, 99, p, out pp) == null);
            b.ReserveNm = 100;
            k.Is("below the floor leaves", CapMath.LeaveReason(b, 0, 99, p, out pp) == "fuel");
            return k.Err;
        }

        // 0.27.8 live: a leaving aircraft in the landing circuit (the game clears _isInFlight) is not listed for a while - it stays a member while
        // the unit exists; a cancelled return gets the RTB again; the fuel column of a leaving member: reserve-based %, no leave time
        private static string LeavingKept()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 1, 1);
            r.Step(900);
            int h = r.G.Bird(1).State == "Air" ? 1 : 2;
            r.G.Bird(h).ReserveNm = 60;
            r.Step(2);
            k.Is("leaving", r.Jn("rtb " + h) == 1);
            var mv = r.Gv().Members.Find(x => x.Uid == h);
            k.Is("fuel column of a leaving member " + (mv == null ? "-" : mv.FuelPct + "/" + mv.LeaveInS),
                 mv != null && mv.LeaveInS < 0 && Math.Abs(mv.FuelPct - 100.0 * mv.ReserveNm / r.G.Bird(h).MaxRangeNm) < 0.01 && mv.FuelPct < 15);
            r.G.HideUids.Add(h);
            r.Step(60);
            string ow;
            k.Is("unlisted but alive: kept", !r.G.LogHas("(gone while leaving)") && !r.G.LogHas("(lost)") && r.Own.Owner(h, out ow) == "ПВО-1" && r.G.LogHas("alive but not listed"));
            r.G.HideUids.Clear();
            // the deck cancels the return: no order, the aircraft flies on
            r.G.Bird(h).Rtb = false;
            r.Step(35);
            k.Is("T-332: no repeat before the gap", r.Jn("rtb " + h) == 1);
            // T-332: the AI fights (it overrides the order): no repeat while it does, one held line
            r.G.Bird(h).AiState = "MoveToEngage";
            r.Step(300);
            k.Is("T-332: no repeat while fighting", r.Jn("rtb " + h) == 1 && !r.G.Bird(h).Rtb && r.G.LogHas("rtb again held uid=" + h + " "));
            r.G.Bird(h).AiState = null;
            r.Step(5);
            k.Is("rtb again", r.Jn("rtb " + h) >= 2 && r.G.Bird(h).Rtb && r.G.LogHas("rtb again uid=" + h));
            for (int t = 0; t < 1500 && r.Jn("landed " + h) == 0; t++) r.Step(1);
            k.Is("landed, still a member", r.Jn("landed " + h) == 1 && r.Own.Owner(h, out ow) == "ПВО-1");
            // a unit that is really gone while leaving is dropped
            var g = new Rig();
            g.Create("1,2"); g.Ord("ПВО-1", ZA, 1, 1);
            g.Step(900);
            int h2 = g.G.Bird(1).State == "Air" ? 1 : 2;
            g.G.Bird(h2).ReserveNm = 60;
            g.Step(2);
            g.G.Bird(h2).State = "Gone";
            g.Step(20);
            k.Is("gone while leaving", g.G.LogHas("(gone while leaving)") && g.Own.Owner(h2, out ow) == null);
            return k.Err;
        }

        // live 0.28.9: the game removes a landing unit and cools the airframe down under another uid. ADR-0040 §3.4: the landed member stays in the
        // group on its airframe (cool-down row, then hangar), is relaunched with the group loadout when ready, and N is kept by rotation
        private static string LandedRearm()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.RemoveOnLand = true; r.G.CoolS = 120;
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 2, 0);
            r.Step(900);
            k.Is("both on station", r.Gv().Line.OnStation == 2);
            int a = r.Gv().Members[0].Uid, b = r.Gv().Members[1].Uid;
            r.G.Bird(a).ReserveNm = 60; r.G.Bird(b).ReserveNm = 60;
            for (int t = 0; t < 1500 && (r.Jn("landed " + a) == 0 || r.Jn("landed " + b) == 0); t++) r.Step(1);
            r.Step(2);
            string ow;
            var gv = r.Gv();
            k.Is("both landed", r.Jn("landed " + a) == 1 && r.Jn("landed " + b) == 1);
            k.Is("not dropped", !r.G.LogHas("member out") && gv.Members.Count == 2);
            k.Is("landed log", r.G.LogHas("landed uid=" + a + " group=ПВО-1 -> deck 100 cool-down F-14A") && r.G.LogHas("landed uid=" + b + " group=ПВО-1 -> deck 100 cool-down F-14A"));
            int ca = gv.Members[0].Uid, cb = gv.Members[1].Uid;
            k.Is("own cool-down airframes, one each", ca != cb && ca > 7000 && cb > 7000 && r.G.Bird(ca).State == "Cool" && r.G.Bird(cb).State == "Cool"
                 && r.Own.Owner(ca, out ow) == "ПВО-1" && r.Own.Owner(cb, out ow) == "ПВО-1" && r.Own.Owner(a, out ow) == null && r.Own.Owner(b, out ow) == null);
            k.Is("free hangar airframes untouched", r.Own.Owner(3, out ow) == null && r.Own.Owner(4, out ow) == null);
            k.Is("shown as servicing", gv.Members.TrueForAll(m => m.Where == "deck_cool"));
            int orders = r.Jn("order "), launched = r.Jn("launched ");
            r.Step(100);
            k.Is("no order during the cool-down", r.Jn("order ") == orders && !r.G.LogHas("order launch failed"));
            for (int t = 0; t < 600 && r.Jn("launched ") < launched + 2; t++) r.Step(1);
            k.Is("relaunched with the group loadout", r.Jn("launched ") == launched + 2 && r.Jn("order ") == orders + 2 && r.Jn("order 100 F-14A AirToAirLongRange") >= orders + 2);
            k.Is("no member lost", !r.G.LogHas("(lost)") && !r.G.LogHas("member out") && r.Gv().Members.Count == 2);
            r.Step(900);
            gv = r.Gv();
            k.Is("back on station", gv.Line.OnStation == 2);
            var owners = new HashSet<int>();
            foreach (var m in gv.Members) owners.Add(m.Uid);
            k.Is("distinct airframes", owners.Count == 2);

            // no cool-down row (straight to the hangar) and the other hangar airframes belong to another window: the member takes its own one
            var h = new Rig();
            h.G.RemoveOnLand = true; h.G.CoolS = 0;
            h.Create("1"); h.Ord("ПВО-1", ZA, 1, 0);
            h.Step(900);
            k.Is("h on station", h.Gv().Line.OnStation == 1);
            h.Own.Claim(2, "asw", "ПЛО-1"); h.Own.Claim(3, "asw", "ПЛО-1"); h.Own.Claim(4, "asw", "ПЛО-1");
            h.G.Bird(1).ReserveNm = 60;
            for (int t = 0; t < 1500 && h.Jn("landed 1") == 0; t++) h.Step(1);
            h.Step(2);
            int nu = h.Gv().Members.Count == 1 ? h.Gv().Members[0].Uid : 0;
            k.Is("hangar re-arm " + nu, h.G.LogHas("landed uid=1 group=ПВО-1 -> deck 100 hangar F-14A") && nu > 7000 && h.Own.Owner(nu, out ow) == "ПВО-1");
            k.Is("foreign airframes kept", h.Own.Owner(2, out ow) == "ПЛО-1" && h.Own.Owner(3, out ow) == "ПЛО-1" && h.Own.Owner(4, out ow) == "ПЛО-1");
            h.Step(30);
            k.Is("relaunched from the hangar", h.G.Bird(nu) != null && h.G.Bird(nu).State == "Air");

            // landed with no free airframe of its type on the deck (the row never comes): dropped after the grace, not before
            var n = new Rig();
            n.G.RemoveOnLand = true; n.G.LandNoAirframe = true;
            n.Create("1"); n.Ord("ПВО-1", ZA, 1, 0);
            n.Step(900);
            n.Own.Claim(2, "asw", "ПЛО-1"); n.Own.Claim(3, "asw", "ПЛО-1"); n.Own.Claim(4, "asw", "ПЛО-1");
            n.G.Bird(1).ReserveNm = 60;
            for (int t = 0; t < 1500 && n.Jn("landed 1") == 0; t++) n.Step(1);
            n.Step(5);
            k.Is("grace: kept", n.Gv().Members.Count == 1 && !n.G.LogHas("member out"));
            n.Step(20);
            k.Is("no airframe: dropped as landed", n.G.LogHas("(landed, no airframe on deck 100)") && n.Gv().Members.Count == 0);
            return k.Err;
        }

        // live 0.28.10/11: with a heavy loadout the path at take-off (~290 nm) is under 15 % of the game's clean MaxRange (2000 nm here): fresh
        // machines left for fuel before the zone. The floor of a machine the group launched comes from its own full path at take-off.
        private static string HeavyLoadFuel()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.LaunchReserveNm = 290;
            r.Create("1,2"); r.Ord("ПВО-1", ZA, 2, 0);
            bool earlyLeave = false; int onAt = -1;
            for (int t = 0; t < 900; t++)
            {
                r.Step(1);
                if (r.G.EventCount("cap_rtb") > 0) earlyLeave = true;
                if (onAt < 0 && r.Gv().Line.OnStation == 2) onAt = t;
            }
            k.Is("fresh launch reaches the station", onAt >= 0);
            k.Is("no fuel leave with reserve over the threshold", !earlyLeave && !r.G.LogHas("reason=fuel"));
            var mv = r.Gv().Members[0];
            k.Is("fuel % of the own full path " + mv.FuelPct, mv.FuelPct > 50 && mv.FuelPct <= 100);
            int h = mv.Uid;
            double thr = Math.Max(420 * 300 / 3600.0, 0.05 * 290);
            for (int t = 0; t < 4000 && r.Jn("rtb " + h) == 0; t++)
            {
                r.Step(1);
                if (r.Jn("rtb " + h) == 0 && r.G.Bird(h).ReserveNm < thr - 2) { k.Is("late leave", false); break; }
            }
            var fb = r.G.Bird(h);
            k.Is("fuel leave at the own floor", r.Jn("rtb " + h) == 1 && fb.ReserveNm <= thr && fb.ReserveNm > thr - 2);
            k.Is("leave line with thresholds", r.G.LogHas("thr_nm=35 max_nm=289.9 game_max_nm=2000 fuel_pct=12.1 "));
            return k.Err;
        }

        public static string Run()
        {
            var tests = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("group", Group),
                new KeyValuePair<string, Func<string>>("groupnames", GroupNames),
                new KeyValuePair<string, Func<string>>("order", OrderFlow),
                new KeyValuePair<string, Func<string>>("polar default origin", PolarDefaultOrigin),
                new KeyValuePair<string, Func<string>>("relief", Relief),
                new KeyValuePair<string, Func<string>>("combat", Combat),
                new KeyValuePair<string, Func<string>>("nofuel", NoFuelData),
                new KeyValuePair<string, Func<string>>("taskuid", TaskUid),
                new KeyValuePair<string, Func<string>>("takeoff", Takeoff),
                new KeyValuePair<string, Func<string>>("taxi takeoff", TaxiTakeoff),
                new KeyValuePair<string, Func<string>>("deck times", DeckTimes),
                new KeyValuePair<string, Func<string>>("hangar guess", HangarGuess),
                new KeyValuePair<string, Func<string>>("priority", Priority),
                new KeyValuePair<string, Func<string>>("deck", DeckBroker),
                new KeyValuePair<string, Func<string>>("control", Control),
                new KeyValuePair<string, Func<string>>("gamedata", GameData),
                new KeyValuePair<string, Func<string>>("rtb not player", RtbNotPlayer),
                new KeyValuePair<string, Func<string>>("deck task kept", DeckTaskKept),
                new KeyValuePair<string, Func<string>>("deck knocked out", DeckKnockedOut),
                new KeyValuePair<string, Func<string>>("game fuel", GameFuelMath),
                new KeyValuePair<string, Func<string>>("leaving kept", LeavingKept),
                new KeyValuePair<string, Func<string>>("landed rearm", LandedRearm),
                new KeyValuePair<string, Func<string>>("heavy load fuel", HeavyLoadFuel),
            };
            foreach (var t in tests)
            {
                try { string e = t.Value(); if (e != null) { return t.Key + ": " + e; } }
                catch (Exception ex) { return t.Key + ": " + ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? "").Split('\n')[0]; }
            }
            return null;
        }
    }
}
