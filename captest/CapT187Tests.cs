using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-187: the CAP window takes the ready tasks the game made on the deck as its own (allow by key, no new order) and pauses a refused order.
    // Scenarios of `captest t187 <name>`.
    internal static class CapT187Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private const string F14 = "F-14A";
        private static readonly string[] Names = { "foreign_patrol", "foreign_alert", "refuse_patrol", "refuse_other", "refuse_alert", "refuse_reset", "save_foreign", "fill_foreign" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame { SlotHangar = true };
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams { Debug = true };
            public CapController C;
            public Rig() { C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Create(params int[] uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + string.Join(",", uids) + "]}")); }
            public string Ord(string group, int n, int k) { return C.Order(J("{\"group\":\"" + group + "\"," + ZA + ",\"on_station\":" + n + ",\"ready\":" + k + "}")); }
            public string Alert(string group, string extra) { return C.Order(J("{\"group\":\"" + group + "\",\"kind\":\"alert\",\"ref_uid\":" + CapFakeGame.ShipUid + extra + "}")); }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public int Warns(string code) { int n = 0; foreach (var e in G.Events) if (e.Key == "cap_warn" && e.Value.Contains("\"code\":\"" + code + "\"")) n++; return n; }
            public int LogN(string sub) { int n = 0; foreach (string l in G.Logs) if (l.Contains(sub)) n++; return n; }
            /// <summary>No hangar machine of the type is left (the foreign ready ones are the only F-14A).</summary>
            public void EmptyHangar() { while (G.TakeOut(F14)) { } }
            public FakeBird Foreign() { return G.AddForeignReady(F14, "AirToAirLongRange"); }
        }

        private static int Uid(FakeBird b) { return AirOpsMath.TaskUid(b.Key); }
        private static int S(int k) { return AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, F14, k); }
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "foreign_patrol": e = ForeignPatrol(); break;
                    case "foreign_alert": e = ForeignAlert(); break;
                    case "refuse_patrol": e = RefusePatrol(); break;
                    case "refuse_other": e = RefuseOther(); break;
                    case "refuse_alert": e = RefuseAlert(); break;
                    case "refuse_reset": e = RefuseReset(); break;
                    case "fill_foreign": e = FillForeign(); break;
                    default: e = SaveForeign(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        // "fill from the deck" takes the game's ready tasks (the live scenario: 12 ready F-14, hangar empty), no order is made
        private static string FillForeign()
        {
            var k = new Chk();
            var r = new Rig();
            r.EmptyHangar();
            var f1 = r.Foreign(); var f2 = r.Foreign(); var f3 = r.Foreign();
            string a = r.Create(Uid(f1));
            k.Is("create " + a, a.Contains("\"ok\":true"));
            r.Alert("ПВО-1", ",\"level\":\"deck\",\"ready\":3,\"fill\":true");
            r.Step(10);
            k.Is("filled the foreign ready ones", r.G.LogHas("alert fill uid=" + Uid(f2) + " ") && r.G.LogHas("alert fill uid=" + Uid(f3) + " "));
            k.Is("no order", r.Jn("order ") == 0);
            k.Is("no not_ready warning", r.Warns("alert_not_ready") == 0);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 60));
            r.Step(5);
            k.Is("raid: allow", r.Jn("allow task:f") >= 2);
            k.Is("raid: no F-14A order", r.Jn("order 100 F-14A") == 0);   // the refill of the sent ones may order other types
            return k.Err;
        }

        // two ready tasks of the game: the patrol allows them by key, no order, nothing aborted
        private static string ForeignPatrol()
        {
            var k = new Chk();
            var r = new Rig();
            r.EmptyHangar();
            var f1 = r.Foreign(); var f2 = r.Foreign();
            string a = r.Create(Uid(f1), Uid(f2));
            k.Is("create " + a, a.Contains("\"ok\":true"));
            r.Ord("ПВО-1", 1, 1);
            r.Step(30);
            k.Is("adopted", r.LogN("adopt deck task") >= 1);
            k.Is("no order", r.Jn("order ") == 0);
            k.Is("allow by key", r.Jn("allow task:f") >= 1);
            k.Is("launched", r.Jn("launched ") >= 1);
            k.Is("never aborted", r.Jn("abort ") == 0);
            return k.Err;
        }

        // the alert group: the foreign ready tasks count as prepared, a raid sends them by Allow
        private static string ForeignAlert()
        {
            var k = new Chk();
            foreach (string level in new[] { "deck", "hangar" })
            {
                var r = new Rig();
                r.EmptyHangar();
                var f1 = r.Foreign(); var f2 = r.Foreign();
                r.Create(Uid(f1), Uid(f2));
                string a = r.Alert("ПВО-1", ",\"level\":\"" + level + "\",\"ready\":2");
                k.Is(level + " order " + a, a != null && a.Contains("\"ok\":true"));
                r.Step(10);
                k.Is(level + " no order before the raid", r.Jn("order ") == 0);
                k.Is(level + " no abort", r.Jn("abort ") == 0);
                k.Is(level + " prepared counts the foreign ones", r.Warns("alert_not_ready") == 0);
                r.G.Contacts.Add(CapT179Tests.Raid(50, 60));
                r.Step(5);
                k.Is(level + " raid: allow", r.Jn("allow task:f") >= 1);
                k.Is(level + " raid: still no order", r.Jn("order ") == 0);
            }
            return k.Err;
        }

        // an empty hangar: an order every 600 s at most, one warning, one failure line
        private static string RefusePatrol()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.OrderErr = "no_airframes";
            r.Create(S(1));
            r.Ord("ПВО-1", 1, 0);
            r.Step(1100);
            k.Is("two orders " + r.Jn("order "), r.Jn("order ") == 2);
            k.Is("one warning " + r.Warns("no_reserve_deck"), r.Warns("no_reserve_deck") == 1);
            k.Is("no launch_failed warning", r.Warns("launch_failed") == 0);
            k.Is("log not flooded " + r.LogN("order launch failed"), r.LogN("order launch failed") <= 1);
            return k.Err;
        }

        // another refusal: 60 / 120 / 240 s
        private static string RefuseOther()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.OrderErr = "unknown_loadout";
            r.Create(S(1));
            r.Ord("ПВО-1", 1, 0);
            r.Step(50);
            k.Is("1 order at 50 s " + r.Jn("order "), r.Jn("order ") == 1);
            r.Step(50);
            k.Is("2 orders at 100 s " + r.Jn("order "), r.Jn("order ") == 2);
            r.Step(150);
            k.Is("3 orders at 250 s " + r.Jn("order "), r.Jn("order ") == 3);
            k.Is("one warning " + r.Warns("launch_failed"), r.Warns("launch_failed") == 1 && r.Warns("no_reserve_deck") == 0);
            return k.Err;
        }

        // the alert group, levels deck and hangar: same limit, one warning
        private static string RefuseAlert()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.OrderErr = "no_airframes";
            r.Create(S(1), S(2));
            r.Alert("ПВО-1", ",\"level\":\"deck\",\"ready\":1");
            r.Step(1100);
            k.Is("deck: two orders " + r.Jn("order "), r.Jn("order ") == 2);
            k.Is("deck: one warning " + r.Warns("no_reserve_deck"), r.Warns("no_reserve_deck") == 1);
            var h = new Rig();
            h.G.OrderErr = "no_airframes";
            h.Create(S(1), S(2));
            h.Alert("ПВО-1", ",\"level\":\"hangar\",\"ready\":2");
            h.Step(5);
            h.G.Contacts.Add(CapT179Tests.Raid(50, 60));
            h.Step(1100);
            k.Is("hangar: two rounds of two orders (both machines go in one round) " + h.Jn("order "), h.Jn("order ") == 4);
            k.Is("hangar: one warning " + h.Warns("no_reserve_deck"), h.Warns("no_reserve_deck") == 1);
            return k.Err;
        }

        // a machine comes back to the hangar: the next order at once; a success clears the warning
        private static string RefuseReset()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.OrderErr = "no_airframes";
            r.Create(S(1));
            r.Ord("ПВО-1", 1, 0);
            r.Step(20);
            k.Is("refused once " + r.Jn("order "), r.Jn("order ") == 1 && r.Warns("no_reserve_deck") == 1);
            r.Step(20);
            k.Is("paused " + r.Jn("order "), r.Jn("order ") == 1);
            r.G.OrderErr = null;
            r.G.All.Add(new FakeBird { Uid = 20, Name = "Tomcat 20", Lat = r.G.Deck.Lat, Lon = r.G.Deck.Lon });
            r.Step(5);
            k.Is("ordered again without waiting " + r.Jn("order "), r.Jn("order ") == 2);
            r.Step(5);
            k.Is("warning cleared", !r.C.StateJson().Contains("no_reserve_deck"));
            return k.Err;
        }

        // save and load: the adopted key is not saved, the loaded group takes it again, no order
        private static string SaveForeign()
        {
            var k = new Chk();
            var r = new Rig();
            r.EmptyHangar();
            var f1 = r.Foreign(); var f2 = r.Foreign();
            r.Create(Uid(f1), Uid(f2));
            r.Ord("ПВО-1", 0, 2);
            r.Step(5);
            string json = CapSave.ToJson(r.C.Save());
            k.Is("key not in the file", !json.Contains("task:f"));
            string err;
            var d = CapSave.Parse(json, out err);
            var r2 = new Rig();
            r2.EmptyHangar();
            r2.Foreign(); r2.Foreign();
            r2.C.Load(d, r2.G.Now);
            r2.Step(10);
            k.Is("adopted again " + err, r2.LogN("adopt deck task") >= 2);
            k.Is("no order", r2.Jn("order ") == 0);
            return k.Err;
        }
    }
}
