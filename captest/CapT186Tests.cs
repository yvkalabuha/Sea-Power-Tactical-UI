using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-186 / ADR-0082: hangar machines of the CAP window as shared slots (fake game with SlotHangar). Scenarios of `captest t186 <name>`.
    internal static class CapT186Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private const string F14 = "F-14A";
        private static readonly string[] Names = { "fake", "shared_uid", "cross_taken", "fill", "shrink", "shrink_asw", "move", "order", "order_wait", "alert_order", "abort_back", "land_back", "legacy_save", "format3", "load_conflict" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        /// <summary>The ASW window as the book and the ownership see it: claims on slots, a resolver, the moves it was given.</summary>
        private sealed class AswSide
        {
            public readonly List<AirOpsMath.SlotClaim> Claims = new List<AirOpsMath.SlotClaim>();
            public readonly List<AirOpsMath.SlotMove> Moves = new List<AirOpsMath.SlotMove>();
            private readonly AirOwnership _own;
            public AswSide(AirOwnership own)
            {
                _own = own;
                own.Slots.Register("asw", () => Claims, mv => { Moves.Add(mv); if (mv.Drop) Claims.RemoveAll(c => c.Uid == mv.Uid); }, (d, t) => -1);
                own.Register("asw", uid => { foreach (var c in Claims) if (c.Uid == uid) return c.Group; return null; });
            }
            public int Take(int k, string group = "ПЛО-1")
            {
                int uid = S(k);
                Claims.Add(new AirOpsMath.SlotClaim { Uid = uid, Group = group, Window = "asw", Seq = _own.GroupSeq("asw", group), JoinedAt = _own.Slots.NextJoin(), K = k, DeckUid = CapFakeGame.DeckUid, Type = F14 });
                return uid;
            }
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
            public CapGroupView View(string group) { foreach (var g in C.View().Groups) if (g.Line.Group == group) return g; return null; }
            public string Owner(int uid) { string w; return Own.Owner(uid, out w); }
        }

        private static int S(int k) { return AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, F14, k); }
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static bool Has(Rig r, string sub) { return r.G.LogHas(sub); }

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "fake": e = Fake(); break;
                    case "shared_uid": e = SharedUid(); break;
                    case "cross_taken": e = CrossTaken(); break;
                    case "fill": e = Fill(); break;
                    case "shrink": e = Shrink(); break;
                    case "shrink_asw": e = ShrinkAsw(); break;
                    case "move": e = Move(); break;
                    case "order": e = Order(); break;
                    case "order_wait": e = OrderWait(); break;
                    case "alert_order": e = AlertOrder(); break;
                    case "abort_back": e = AbortBack(); break;
                    case "land_back": e = LandBack(); break;
                    case "legacy_save": e = LegacySave(); break;
                    case "format3": e = Format3(); break;
                    default: e = LoadConflict(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static List<CapBird> Rows(Rig r, string where)
        {
            var l = new List<CapBird>();
            foreach (var b in r.G.Birds()) if (b.Where == where && b.Type == F14) l.Add(b);
            return l;
        }

        // the fake itself: 4 F-14A = 4 slot rows with the shared uid; an order leaves 3 rows and a task row
        private static string Fake()
        {
            var k = new Chk();
            var r = new Rig();
            var rows = Rows(r, "hangar");
            k.Is("4 rows", rows.Count == 4);
            for (int i = 1; i <= rows.Count && i <= 4; i++)
            {
                var b = rows[i - 1];
                k.Is("row " + i, b.Uid == S(i) && b.HangarK == i && b.HangarN == 4 && b.DeckUid == CapFakeGame.DeckUid);
            }
            string key; double rs;
            k.Is("order", r.G.OrderLaunch(100, F14, "AirToAirLongRange", "CAP", false, out key, out rs) == null);
            k.Is("3 rows", Rows(r, "hangar").Count == 3);
            var prep = Rows(r, "deck_prep");
            k.Is("task row", prep.Count == 1 && prep[0].Uid == AirOpsMath.TaskUid(key) && prep[0].PendingKey == key && r.G.TaskBirdUid(100, key) == AirOpsMath.TaskUid(key));
            k.Is("take out", r.G.TakeOut(F14) && r.G.HangarNumbers(F14) == 2 && Rows(r, "hangar").Count == 2);
            return k.Err;
        }

        // one uid for the machine: the row of the window, the member and the state json
        private static string SharedUid()
        {
            var k = new Chk();
            var r = new Rig();
            string a = r.Create(S(2));
            k.Is("create " + a, a.Contains("\"ok\":true"));
            string js = r.C.StateJson();
            k.Is("member: uid + deck key + k", js.Contains("\"uid\":" + S(2)) && js.Contains("\"deck_key\":\"hangar:F-14A#2\"") && js.Contains("\"hangar_k\":2") && js.Contains("\"hangar_n\":4"));
            k.Is("candidate: slot + n", js.Contains("{\"uid\":" + S(3) + ",") && js.Contains("\"slot\":3,\"hangar_n\":4"));
            k.Is("same uid as the book", r.Own.Slots.DiagJson(5).Contains("\"uid\":" + S(2) + ",\"window\":\"cap\""));
            k.Is("owner", r.Owner(S(2)) == "ПВО-1" && r.Owner(S(1)) == null);
            return k.Err;
        }

        // a machine of an ASW group: the row shows in_group + owner_window, add takes a free one, none left -> hangar_taken
        private static string CrossTaken()
        {
            var k = new Chk();
            var r = new Rig();
            var asw = new AswSide(r.Own);
            asw.Take(1);
            string js = r.C.StateJson();
            k.Is("row in_group", js.Contains("{\"uid\":" + S(1) + ",") && js.Contains("\"reason\":\"in_group:ПЛО-1\"") && js.Contains("\"owner_window\":\"asw\""));
            string a = r.Create(S(1));
            var gv = r.View("ПВО-1");
            k.Is("create [k1] takes k2 " + a, gv != null && gv.Members.Count == 1 && gv.Members[0].Uid == S(2) && gv.Members[0].HangarK == 2);
            k.Is("k1 stays with ASW", r.Owner(S(1)) == "ПЛО-1");
            asw.Take(3, "ПЛО-2"); asw.Take(4, "ПЛО-2");
            string b = r.C.Group(J("{\"op\":\"add\",\"group\":\"ПВО-1\",\"uids\":[" + S(1) + "]}"));
            k.Is("none left: hangar_taken " + b, b.Contains("\"reason\":\"hangar_taken\""));
            string c = r.Create(S(3));
            k.Is("create with none free " + c, c == "!hangar_taken");
            return k.Err;
        }

        // the alert group fills from the deck: free slots, the member found by the uid actually taken
        private static string Fill()
        {
            var k = new Chk();
            var r2 = new Rig();
            var asw2 = new AswSide(r2.Own);
            asw2.Take(1);
            r2.Create(S(2));
            r2.Alert("ПВО-1", ",\"ready\":3,\"fill\":true");
            r2.Step(3);
            var g = r2.View("ПВО-1");
            k.Is("two free slots taken " + (g == null ? -1 : g.Members.Count), g != null && g.Members.Count == 3);
            bool ok = true;
            foreach (var m in g.Members) if (m.Uid == S(1)) ok = false;
            k.Is("never the ASW slot", ok && r2.Owner(S(1)) == "ПЛО-1");
            k.Is("filled the free slots by the uid taken", r2.G.LogHas("alert fill uid=" + S(3) + " ") && r2.G.LogHas("alert fill uid=" + S(4) + " "));
            return k.Err;
        }

        // 4 machines: ASW k1 first, then CAP k2 k3; two leave from outside: the CAP member who took last is dropped, hangar_taken, not lost
        private static string Shrink()
        {
            var k = new Chk();
            var r = new Rig();
            var asw = new AswSide(r.Own);
            asw.Take(1);
            r.Create(S(2), S(3));
            r.Step(2);
            r.G.TakeOut(F14); r.G.TakeOut(F14);   // n = 2
            r.Step(2);
            k.Is("not at once", r.View("ПВО-1").Members.Count == 2);
            r.Step(4);
            var gv = r.View("ПВО-1");
            k.Is("one member left", gv.Members.Count == 1 && gv.Members[0].Uid == S(2));
            k.Is("journal", Has(r, "member hangar_taken ПВО-1 uid=" + S(3) + " type=F-14A deck=100") && !Has(r, "(lost)"));
            k.Is("warning", gv.Line.Warns.Contains("hangar_taken"));
            k.Is("event", r.G.EventCount("cap_member_out") == 1 && r.G.LastEvent("cap_member_out").Contains("\"reason\":\"hangar_taken\""));
            k.Is("ASW untouched", asw.Moves.Count == 0 && r.Owner(S(1)) == "ПЛО-1" && r.Owner(S(3)) == null);
            return k.Err;
        }

        // CAP k1 first, ASW k2 k3 later: the ASW window gets the drop
        private static string ShrinkAsw()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create(S(1));
            var asw = new AswSide(r.Own);
            asw.Take(2); asw.Take(3);
            r.Step(2);
            r.G.TakeOut(F14); r.G.TakeOut(F14);   // n = 2
            r.Step(6);
            k.Is("ASW dropped the last taker", asw.Moves.Count == 1 && asw.Moves[0].Drop && asw.Moves[0].Uid == S(3));
            k.Is("CAP kept", r.View("ПВО-1").Members.Count == 1 && !Has(r, "hangar_taken"));
            return k.Err;
        }

        // a machine leaves from the middle: the last slot follows, the claim moves with it
        private static string Move()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create(S(1), S(2), S(4));
            r.Step(2);
            r.G.TakeOut(F14);   // n = 3: k4 -> k3
            r.Step(2);
            var gv = r.View("ПВО-1");
            bool has3 = false; foreach (var m in gv.Members) if (m.Uid == S(3) && m.HangarK == 3) has3 = true;
            k.Is("moved " + gv.Members.Count, gv.Members.Count == 3 && has3);
            k.Is("journal", Has(r, "slot ПВО-1 uid=" + S(4) + " -> " + S(3) + " k=4->3 n=3"));
            k.Is("claims", r.Owner(S(3)) == "ПВО-1" && r.Owner(S(4)) == null);
            return k.Err;
        }

        // an order: the member goes to the uid of the task at once; the row comes late and nobody is lost
        private static string Order()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic"; r.G.TaskRowLag = 10;
            r.Create(S(1), S(2));
            r.Ord("ПВО-1", 1, 0);
            r.Step(6);
            k.Is("ordered", r.Jn("order ") >= 1 && r.G.Tasks.Count >= 1);
            string key = r.G.Tasks.Count > 0 ? r.G.Tasks[0].Key : "?";
            int tu = AirOpsMath.TaskUid(key);
            var gv = r.View("ПВО-1");
            bool onTask = false, slotStays = false;
            foreach (var m in gv.Members) { if (m.Uid == tu) onTask = true; if (m.HangarK > 0) slotStays = true; }
            k.Is("a member on the task uid " + key, onTask && Has(r, "handover ПВО-1 slot=") && Has(r, "key=" + key + " -> uid=" + tu));
            k.Is("the other still on a slot", slotStays);
            r.Step(20);
            k.Is("nobody lost, no readopt", !Has(r, "(lost)") && !Has(r, "readopt") && r.View("ПВО-1").Members.Count == 2);
            k.Is("the claim moved", r.Owner(tu) == "ПВО-1" && r.Owner(S(1)) == null);
            return k.Err;
        }

        // the row never comes: after OwnRowWaitS and the grace the old rule applies (readopt a slot)
        private static string OrderWait()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic"; r.G.TaskRowLag = 1000;
            r.Create(S(1));
            r.Ord("ПВО-1", 1, 0);
            r.Step(20);
            k.Is("waits", r.View("ПВО-1").Members.Count == 1 && !Has(r, "readopt") && !Has(r, "(lost)"));
            r.Step(60);
            k.Is("then readopts a slot", Has(r, "readopt (task gone)") && r.View("ПВО-1").Members.Count == 1 && !Has(r, "(lost)"));
            return k.Err;
        }

        // the alert group of the level "hangar": the orders on a raid hand the members over to their tasks
        private static string AlertOrder()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic";
            r.Create(S(1), S(2), S(3));
            r.Alert("ПВО-1", ",\"level\":\"hangar\"");
            r.Step(10);
            r.G.Contacts.Add(new CapContact { Id = 50, Lat = 10.0 + 70 / 60.0, Lon = 11.0, Hostile = true, CourseDeg = 180, SpeedKn = 500 });
            r.Step(3);
            int hand = 0; foreach (string l in r.G.Logs) if (l.Contains("handover ПВО-1 slot=")) hand++;
            k.Is("two handovers " + hand, hand == 2);
            k.Is("nobody lost", !Has(r, "(lost)") && r.View("ПВО-1").Members.Count == 3);
            return k.Err;
        }

        // the order is taken away in the game: the member takes the lowest free slot of its pair
        private static string AbortBack()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.Mode = "Realistic";
            r.Create(S(3));
            r.Ord("ПВО-1", 1, 0);
            r.Step(3);
            k.Is("ordered", r.G.Tasks.Count == 1);
            string key = r.G.Tasks[0].Key;
            r.G.AbortLaunch(100, key);
            r.Step(70);
            var gv = r.View("ПВО-1");
            k.Is("readopted a slot", Has(r, "readopt (task gone)") && gv.Members.Count >= 1 && !Has(r, "(lost)"));
            k.Is("holds a slot row", gv.Members.Count >= 1 && (gv.Members[0].HangarK > 0 || gv.Members[0].Where != "hangar"));
            return k.Err;
        }

        // a landed member: the cool-down row first, then a slot of the pair
        private static string LandBack()
        {
            var k = new Chk();
            var r = new Rig();
            r.G.RemoveOnLand = true; r.G.CoolS = 60;
            r.Create(S(1)); r.Ord("ПВО-1", 1, 0);
            r.Step(900);
            k.Is("on station", r.View("ПВО-1").Line.OnStation == 1);
            int bu = r.View("ПВО-1").Members[0].Uid;
            r.G.Bird(bu).ReserveNm = 60;
            for (int t = 0; t < 1500 && r.Jn("landed " + bu) == 0; t++) r.Step(1);
            r.Step(2);
            var gv = r.View("ПВО-1");
            k.Is("cool-down airframe " + gv.Members.Count, gv.Members.Count == 1 && gv.Members[0].Where == "deck_cool" && Has(r, "cool-down F-14A"));
            r.Step(80);
            gv = r.View("ПВО-1");
            k.Is("back on a slot", gv.Members.Count == 1 && !Has(r, "(lost)"));
            for (int t = 0; t < 600 && r.Jn("launched ") < 2; t++) r.Step(1);
            k.Is("relaunched", r.Jn("launched ") >= 2);
            return k.Err;
        }

        private static CapSaveData Saved(Rig r) { return r.C.Save(); }

        // a format 2 file: positional uids -> the free slot of the pair
        private static string LegacySave()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create(S(1), S(2));
            var d = Saved(r);
            d.Format = 2;
            d.Groups[0].Members[0].Uid = -(100 * 10000 + 1 * 100 + 0 + 1); d.Groups[0].Members[0].Deck = 0;
            d.Groups[0].Members[1].Uid = -(100 * 10000 + 1 * 100 + 1 + 1); d.Groups[0].Members[1].Deck = 0;
            var r2 = new Rig();
            r2.C.Load(d, r2.G.Now);
            var gv = r2.View("ПВО-1");
            k.Is("restored on slots " + (gv == null ? -1 : gv.Members.Count), gv != null && gv.Members.Count == 2 && gv.Members[0].Uid == S(1) && gv.Members[1].Uid == S(2));
            k.Is("journal", Has(r2, "restore hangar legacy ПВО-1 uid=-1000101 -> " + S(1) + " deck=100 k=1"));
            k.Is("event", r2.G.LastEvent("cap_restored") != null && r2.G.LastEvent("cap_restored").Contains("\"missed\":0"));
            // the ship of the old uid is gone: any deck
            var d2 = Saved(r);
            d2.Groups[0].Members[0].Uid = -(999 * 10000 + 1); d2.Groups[0].Members[0].Deck = 0;
            d2.Groups[0].Members.RemoveAt(1);
            var r3 = new Rig(); r3.C.Load(d2, r3.G.Now);
            k.Is("any deck", r3.View("ПВО-1").Members.Count == 1 && r3.View("ПВО-1").Members[0].Uid == S(1));
            // nothing of the type: missed
            var d3 = Saved(r);
            d3.Groups[0].Members[0].Uid = -(100 * 10000 + 1); d3.Groups[0].Members[0].Deck = 0; d3.Groups[0].Members[0].Type = "F-16";
            d3.Groups[0].Members.RemoveAt(1);
            var r4 = new Rig(); r4.C.Load(d3, r4.G.Now);
            k.Is("missed", r4.View("ПВО-1").Members.Count == 0 && Has(r4, "restore hangar miss") && r4.G.LastEvent("cap_restored").Contains("\"missed\":1"));
            return k.Err;
        }

        // format 3: save and load keep the slots
        private static string Format3()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create(S(2), S(4));
            string json = CapSave.ToJson(Saved(r));
            k.Is("deck in the file", json.Contains("\"deck\":100") && json.Contains("\"format\":3"));
            string err;
            var d = CapSave.Parse(json, out err);
            var r2 = new Rig();
            r2.C.Load(d, r2.G.Now);
            var gv = r2.View("ПВО-1");
            k.Is("same uids " + err, gv != null && gv.Members.Count == 2 && gv.Members[0].Uid == S(2) && gv.Members[1].Uid == S(4) && gv.Members[0].HangarK == 2 && gv.Members[1].HangarK == 4);
            k.Is("owned", r2.Owner(S(2)) == "ПВО-1" && r2.Owner(S(4)) == "ПВО-1");
            return k.Err;
        }

        // the ASW window already holds k1 when the CAP file is read: the member gets k2, it is not dropped silently
        private static string LoadConflict()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create(S(1));
            var d = Saved(r);
            var r2 = new Rig();
            var asw = new AswSide(r2.Own);
            asw.Take(1);
            r2.C.Load(d, r2.G.Now);
            var gv = r2.View("ПВО-1");
            k.Is("got k2 " + (gv == null ? -1 : gv.Members.Count), gv != null && gv.Members.Count == 1 && gv.Members[0].Uid == S(2));
            k.Is("ASW kept k1", r2.Owner(S(1)) == "ПЛО-1" && asw.Moves.Count == 0);
            return k.Err;
        }
    }
}
