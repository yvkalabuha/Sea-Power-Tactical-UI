using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-197: alert group fixes of the 0.30.5 run. Scenarios of `captest t197 <name>`.
    internal static class CapT197Tests
    {
        private static readonly string[] Names = { "leash-far-deck", "leash-real", "rtb-alert-throttle", "leash-throttle", "stop-throttle", "raid-one-key", "raid-no-guard", "fuel-pct", "leash-never-in", "raid-guard-off", "raid-key-late-twin" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static CapT179Tests.Rig NewRig() { return new CapT179Tests.Rig(null, "Cheater"); }
        /// <summary>The ship 70 nm east of the deck (the deck stands outside the 60 nm leash).</summary>
        private static void FarShip(CapFakeGame g) { g.Ship = new GeoPt(10.0, 10.0 + 70.0 / 60.0); }

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "leash-far-deck": e = LeashFarDeck(); break;
                    case "leash-real": e = LeashReal(); break;
                    case "rtb-alert-throttle": e = RtbAlertThrottle(); break;
                    case "leash-throttle": e = LeashThrottle(); break;
                    case "stop-throttle": e = StopThrottle(); break;
                    case "raid-one-key": e = RaidOneKey(); break;
                    case "raid-no-guard": e = RaidNoGuard(); break;
                    case "leash-never-in": e = LeashNeverIn(); break;
                    case "raid-guard-off": e = RaidGuardOff(); break;
                    case "raid-key-late-twin": e = RaidKeyLateTwin(); break;
                    default: e = FuelPct(); break;
                }
                if (e != null)
                {
                    if (Environment.GetEnvironmentVariable("CAPT197_DUMP") == "1" && CapT179Tests.Rig.All.Count > 0) foreach (string l in CapT179Tests.Rig.All[CapT179Tests.Rig.All.Count - 1].G.Logs) Console.WriteLine(l);
                    return n + ": " + e;
                }
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        // a deck 70 nm from the protected ship: nothing leaves on the leash on take-off (barrage and raid)
        private static string LeashFarDeck()
        {
            var k = new Chk();
            var r = NewRig(); FarShip(r.G);
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"after\":\"alert\"");
            r.Step(900);
            string a = r.C.ScrambleNow(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            k.Is("barrage ok " + a, a != null && a.Contains("\"ok\":true"));
            r.Step(120);
            k.Is("barrage: airborne", r.Airborne(1, 2, 3) == 2);
            k.Is("barrage: no alert_leash on take-off", !r.G.LogHas("reason=alert_leash"));
            var q = NewRig(); FarShip(q.G);
            q.Create("1,2,3"); q.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":2");
            q.Step(900);
            q.G.Contacts.Add(CapT179Tests.Raid(50, 0, 10));
            q.Step(130);
            k.Is("raid: airborne", q.Airborne(1, 2, 3) == 2);
            k.Is("raid: no alert_leash on take-off", !q.G.LogHas("reason=alert_leash") && q.G.LogHas("alert scramble group=ПВО-1 raid="));
            return k.Err;
        }

        // the bird was inside the circle (the deck is 30 nm from the ship) and left it: alert_leash
        private static string LeashReal()
        {
            var k = new Chk();
            var r = NewRig(); r.G.Ship = new GeoPt(10.0, 10.0 + 30.0 / 60.0);
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":1,\"radius_nm\":120,\"pursuit_nm\":60");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 110, -30));
            r.Step(400);
            k.Is("alert_leash after the bird left the circle", r.G.LogHas("reason=alert_leash"));
            return k.Err;
        }

        private static bool NoPlayerTook(CapT179Tests.Rig r) { return !r.G.LogHas("throttle changed outside the mod") && !r.G.LogHas("player_took"); }

        // after=alert: the dash of the intercept ends before the way home, no player_took, the bird lands in its group
        private static string RtbAlertThrottle()
        {
            var k = new Chk();
            var r = NewRig(); r.G.RtbStep = 2;
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":1,\"after\":\"alert\"");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            r.Step(40);
            k.Is("dash on", r.G.LogHas("alert speed uid="));
            r.G.Contacts.Clear();
            r.Step(200);
            k.Is("rtb_alert", r.G.LogHas("-> rtb_alert"));
            k.Is("throttle restored", r.G.LogHas("throttle restored uid="));
            r.Step(30);
            k.Is("no player_took " + r.G.Journal.Count, NoPlayerTook(r));
            r.Step(900);
            var v = r.View("ПВО-1");
            k.Is("all three still in the group", v.Members.Count == 3);
            int alerts = 0; foreach (var m in v.Members) if (m.Alert) alerts++;
            k.Is("alert machines " + alerts, alerts == 3 && v.ReadyNow == 2);
            return k.Err;
        }

        private static string LeashThrottle()
        {
            var k = new Chk();
            var r = NewRig(); r.G.RtbStep = 2; r.G.Ship = new GeoPt(10.0, 10.0 + 30.0 / 60.0);
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":1,\"radius_nm\":120,\"pursuit_nm\":60");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 110, -30));
            r.Step(400);
            k.Is("alert_leash", r.G.LogHas("reason=alert_leash"));
            k.Is("dash was on", r.G.LogHas("alert speed uid="));
            k.Is("throttle restored", r.G.LogHas("throttle restored uid="));
            k.Is("no player_took", NoPlayerTook(r));
            return k.Err;
        }

        private static string StopThrottle()
        {
            var k = new Chk();
            var r = NewRig(); r.G.RtbStep = 2;
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":1");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            r.Step(30);
            k.Is("dash on", r.G.LogHas("alert speed uid="));
            r.C.Stop(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            r.Step(30);
            k.Is("throttle restored", r.G.LogHas("throttle restored uid="));
            k.Is("no player_took", NoPlayerTook(r));
            return k.Err;
        }

        // 7039 and 7040 are one raid for the air threat service (key r7039): one reaction, n=2 means two machines
        private static string RaidOneKey()
        {
            var k = new Chk();
            Func<bool, int> run = keyed =>
            {
                var r = NewRig();
                if (keyed) { r.Notes.Keys[7039] = "r7039"; r.Notes.Keys[7040] = "r7039"; }
                r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
                r.Notes.AutoKey = keyed;
                r.Step(900);
                r.G.Contacts.Add(CapT179Tests.Raid(7039, 50));
                r.Step(6);
                r.G.Contacts.Clear();
                r.G.Contacts.Add(CapT179Tests.Raid(7040, 30, 25));
                r.Step(8);
                return r.Airborne(1, 2, 3, 4);
            };
            int keyed1 = run(true), plain = run(false);
            k.Is("one key: two machines " + keyed1, keyed1 == 2);
            k.Is("without a key the second cluster is a new raid (control) " + plain, plain == 4);
            // the key does not exist yet: wait RaidKeyWaitS, then by id
            var w = NewRig(); w.Notes.AutoKey = false;
            w.Create("1,2,3,4"); w.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":2");
            w.Step(900);
            w.G.Contacts.Add(CapT179Tests.Raid(7039, 50));
            w.Step(1);
            k.Is("waits for the key", w.Jn("allow ") == 0);
            w.Step(5);
            k.Is("then goes by id", w.Jn("allow ") >= 2);
            // the key appears within the wait: the second track of the same key is served
            var x = NewRig(); x.Notes.AutoKey = false;
            x.Create("1,2,3,4"); x.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
            x.Step(900);
            x.G.Contacts.Add(CapT179Tests.Raid(7039, 50)); x.Notes.Keys[7039] = "r7039";
            x.Step(3);
            x.G.Contacts.Add(CapT179Tests.Raid(7040, 30, 25));   // separate cluster, no key at first
            x.Step(1);
            x.Notes.Keys[7040] = "r7039";
            x.Step(8);
            k.Is("late key: still two machines " + x.Airborne(1, 2, 3, 4), x.Airborne(1, 2, 3, 4) == 2);
            return k.Err;
        }

        // T-207: the live sequence of the 0.30.6 run: the served birds left and landed, the track vanished, 14 min later its twin with the same AirGuard key: no second scramble
        private static string RaidKeyLateTwin()
        {
            var k = new Chk();
            Func<string, int> run = twinKey =>
            {
                var r = NewRig();
                r.Notes.Keys[7039] = "r7039"; r.Notes.Keys[7040] = twinKey;
                r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
                r.Step(900);
                r.G.Contacts.Add(CapT179Tests.Raid(7039, 50));
                r.Step(130);
                r.G.Contacts.Clear();
                r.Step(840);
                r.G.Contacts.Add(CapT179Tests.Raid(7040, 33));
                r.Step(60);
                int n = 0; foreach (string l in r.G.Logs) if (l.Contains("alert scramble group=ПВО-1 raid=")) n++;
                return n;
            };
            int same = run("r7039"), other = run("r7040");
            k.Is("same key: one scramble " + same, same == 1);
            k.Is("another key (control): a second scramble " + other, other == 2);
            return k.Err;
        }

        // no air threat service (AlertNotes == null): by track id, no wait
        private static string RaidNoGuard()
        {
            var k = new Chk();
            var r = NewRig(); r.C.AlertNotes = null;
            r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(7039, 50));
            r.Step(2);
            k.Is("scrambled at once " + r.Jn("allow "), r.Jn("allow ") >= 2);
            r.G.Contacts.Clear();
            r.G.Contacts.Add(CapT179Tests.Raid(7040, 30, 25));
            r.Step(8);
            k.Is("a new cluster is a new raid as before " + r.Airborne(1, 2, 3, 4), r.Airborne(1, 2, 3, 4) == 4);
            return k.Err;
        }

        // T-201: a bird 70 nm out (leash 60, bound 80) that neither enters the circle nor crosses the bound goes home after LeashNeverInS
        private static string LeashNeverIn()
        {
            var k = new Chk();
            var r = NewRig(); FarShip(r.G);
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"ready\":2,\"after\":\"alert\"");
            r.Step(900);
            string a = r.C.ScrambleNow(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            k.Is("barrage ok " + a, a != null && a.Contains("\"ok\":true"));
            r.Step(60);
            var b = r.G.Bird(1); if (b == null || b.State != "Air") b = r.G.Bird(2);
            k.Is("airborne", b != null && b.State == "Air");
            if (b != null) b.Hover = true;
            foreach (var x in r.G.All) if (x.State == "Air") x.Hover = true;
            r.Step((int)CapAlertMath.LeashNeverInS - 150);
            k.Is("still out there before the cap", !r.G.LogHas("reason=alert_leash"));
            r.Step(250);
            k.Is("alert_leash after LeashNeverInS", r.G.LogHas("reason=alert_leash"));
            return k.Err;
        }

        // T-201: AirGuard Mode=off gives no keys: the first launch of a raid does not wait RaidKeyWaitS
        private static string RaidGuardOff()
        {
            var k = new Chk();
            var r = NewRig(); r.Notes.KeysOn = false; r.Notes.AutoKey = false;
            r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":2,\"per_raid\":2");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(7039, 50));
            r.Step(1);
            k.Is("no wait: allowed at once " + r.Jn("allow "), r.Jn("allow ") >= 2);
            return k.Err;
        }

        // the window and the log show the fuel of the game, not the map ring
        private static string FuelPct()
        {
            var k = new Chk();
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("1"); r.Patrol("ПВО-1", 1, 0);
            r.Step(60);
            var b = r.G.Bird(1);
            b.FuelFrac = 0.95; b.RingNm = 60;
            r.Step(3);
            var m = r.Member("ПВО-1", 1);
            k.Is("view fuel ~95 " + (m == null ? -2 : m.FuelPct), m != null && Math.Abs(m.FuelPct - 95) < 1);
            b.GameRtb = true;
            r.Step(3);
            k.Is("log fuel_pct=95", r.G.LogHas("reason=game_rtb") && r.G.LogHas("fuel_pct=95"));
            return k.Err;
        }
    }
}
