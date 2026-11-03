using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-181 / ADR-0080: one row per hangar machine (slots), rebinding, hand-over to the task, parallel preparation, save migration.</summary>
    internal static class AswT181Tests
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswFakeGame Game(out AswFakeDeck d, int hangar, bool ready = false, double prepS = 60)
        {
            var f = new AswFakeGame();
            d = new AswFakeDeck { PrepS = prepS, HangarReady = ready };
            d.AddHangar(Sh3, "SH-3H", hangar);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            return f;
        }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Step(AswController c, AswFakeGame f) { f.Advance(1); f.Tick(c); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) Step(c, f); }
        private static bool Logged(AswFakeGame f, string sub) { return CountLog(f, sub) > 0; }
        private static int CountLog(AswFakeGame f, string sub) { int n = 0; foreach (var l in f.Logs) if (l.Contains(sub)) n++; return n; }
        private static string Bad(AswFakeGame f) { foreach (var l in f.Logs) if (l.Contains("tick error")) return l; return null; }
        private static int Slot(int k) { return AswMath.HangarSlotUid(77, Sh3, k); }

        private static string Group(AswController c, string op, string group, params int[] uids)
        {
            var sb = new System.Text.StringBuilder("{\"op\":\"" + op + "\",\"group\":\"" + group + "\",\"uids\":[");
            for (int i = 0; i < uids.Length; i++) { if (i > 0) sb.Append(','); sb.Append(uids[i]); }
            return c.Group(P(sb.Append("]}").ToString()));
        }

        private static string Create(AswController c, params int[] uids) { return Group(c, "create", "ASW-3", uids); }

        private static int Members(AswController c, int g = 0) { return c.View().Groups[g].Members.Count; }

        private static int AvailableSh3(AswController c)
        {
            int n = 0;
            foreach (var r in c.View().Available) if (r.Type != null && r.Type.Contains("SH-3H")) n++;
            return n;
        }

        private static int Air(AswController c)
        {
            int n = 0;
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.State == "air") n++;
            return n;
        }

        public static string Run(string section)
        {
            string e;
            var all = new[] { "fake", "slots5", "taken", "taken_move", "fungible", "handover", "handover_wait", "landback", "abortback", "parallel", "deckfull", "sar", "stack_migrate", "slot_roundtrip" };
            foreach (var s in all)
            {
                if (section != null && section != s) continue;
                switch (s)
                {
                    case "fake": e = Fake(); break;
                    case "slots5": e = Slots5(); break;
                    case "taken": e = Taken(); break;
                    case "taken_move": e = TakenMove(); break;
                    case "fungible": e = Fungible(); break;
                    case "handover": e = Handover(); break;
                    case "handover_wait": e = HandoverWait(); break;
                    case "landback": e = LandBack(); break;
                    case "abortback": e = AbortBack(); break;
                    case "parallel": e = Parallel(); break;
                    case "deckfull": e = DeckFull(); break;
                    case "sar": e = SarSlots(); break;
                    case "stack_migrate": e = AswT181Save.StackMigrate(); break;
                    default: e = AswT181Save.SlotRoundtrip(); break;
                }
                if (e != null) return s + ": " + e;
            }
            return null;
        }

        // ---- the fake deck itself ----
        private static string Fake()
        {
            AswFakeDeck d; Game(out d, 5);
            var rows = d.Rows();
            if (rows.Count != 5) return "rows " + rows.Count;
            for (int k = 1; k <= 5; k++)
            {
                var r = rows[k - 1];
                if (r.DeckKey != "hangar:" + Sh3 + "#" + k || r.Uid != Slot(k) || r.DeckCount != 5 || r.Slot != k || r.Name.Contains("×")) return "row " + k + " " + r.DeckKey + " " + r.Name;
            }
            string key; double rs;
            d.Order(Sh3, true, out key, out rs);
            if (d.UidOfTask(key) != AswMath.TaskUid(key)) return "task uid";
            d.TakeFromHangar(Sh3, 2);
            int n = 0; foreach (var r in d.Rows()) if (r.Slot > 0) n++;
            if (n != 2) return "after take " + n;   // 5 - 1 (order) - 2
            d.SetPhase(key, "await_deck");
            foreach (var r in d.Rows()) if (r.DeckKey == key && r.DeckPhase != "await_deck") return "phase";
            return null;
        }

        // ---- five machines, three added ----
        private static string Slots5()
        {
            AswFakeDeck d; var f = Game(out d, 5);
            var c = Make(f);
            Step(c, f);
            string r = Create(c, Slot(1), Slot(2), Slot(3));
            if (AswController.IsErr(r)) return r;
            if (Members(c) != 3) return "members " + Members(c);
            if (c.View().Groups[0].MemberRows.Count != 3) return "member rows " + c.View().Groups[0].MemberRows.Count;
            if (AvailableSh3(c) != 2) return "available " + AvailableSh3(c);
            foreach (var row in c.View().Available) if (row.Name.Contains("×")) return "x N in " + row.Name;
            foreach (var row in c.View().Groups[0].MemberRows) if (row.Name.Contains("×") || row.Slot < 1 || row.HangarN != 5) return "member row " + row.Name + " slot=" + row.Slot + " n=" + row.HangarN;
            string js = c.StateJson();
            if (!js.Contains("\"slot\":4") || !js.Contains("\"hangar_n\":5") || !js.Contains("\"deck_key\":\"hangar:" + Sh3 + "#4\"")) return "state json";
            return null;
        }

        // ---- machines taken from outside ----
        private static string Taken()
        {
            AswFakeDeck d; var f = Game(out d, 5);
            var c = Make(f);
            Step(c, f);
            if (AswController.IsErr(Create(c, Slot(1), Slot(2), Slot(3)))) return "create";
            d.TakeFromHangar(Sh3, 2);
            Run(c, f, 2);
            if (Logged(f, "asw slot ")) return "slot moved: k <= 3 stay";
            if (Members(c) != 3) return "members " + Members(c);
            d.TakeFromHangar(Sh3, 2);   // n = 1
            Step(c, f);
            if (Members(c) != 3) return "dropped at once " + Members(c);
            Step(c, f);
            if (Members(c) != 3) return "dropped after 2 scans " + Members(c);
            Step(c, f);
            if (Members(c) != 1) return "members after 3 scans " + Members(c);
            if (CountLog(f, "asw member hangar_taken") != 2) return "hangar_taken lines " + CountLog(f, "asw member hangar_taken");
            if (!f.HasEvent("asw_warn", "hangar_taken")) return "no warning";
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "asw member lost")) return "member_lost";
            return Bad(f);
        }

        private static string TakenMove()
        {
            AswFakeDeck d; var f = Game(out d, 5);
            var c = Make(f);
            Step(c, f);
            if (AswController.IsErr(Create(c, Slot(2), Slot(5)))) return "create";
            if (AswController.IsErr(c.SetTactics(P("{\"group\":\"ASW-3\",\"tactics\":[\"dip\",\"corridor\"]}")))) return "tactics";
            string before = TacticOf(c, Slot(5));
            if (before == null || before == "") return "no tactic for k=5";
            d.TakeFromHangar(Sh3, 1);
            Step(c, f);
            if (!Logged(f, "k=5->1 n=4")) return "no slot move: " + string.Join(" | ", f.Logs.ToArray());
            if (TacticOf(c, Slot(1)) != before) return "tactic after move " + TacticOf(c, Slot(1)) + " want " + before;
            if (Members(c) != 2) return "members " + Members(c);
            return null;
        }

        private static string TacticOf(AswController c, int uid)
        {
            foreach (var r in c.View().Groups[0].MemberRows) if (r.Uid == uid) return r.Tactic;
            return null;
        }

        // ---- a slot is any machine of the type ----
        private static string Fungible()
        {
            AswFakeDeck d; var f = Game(out d, 3);
            var c = Make(f);
            Step(c, f);
            if (AswController.IsErr(Group(c, "create", "A", Slot(1)))) return "create A";
            string r = Group(c, "create", "B", Slot(1));
            if (AswController.IsErr(r)) return "create B " + r;
            var views = c.View().Groups;
            var a = views[0].Members; var b = views[1].Members;
            if (a.Count != 1 || a[0] != Slot(1)) return "A lost its slot";
            if (b.Count != 1 || b[0] != Slot(2)) return "B got " + (b.Count > 0 ? b[0].ToString() : "nothing");
            if (AswController.IsErr(Group(c, "add", "B", Slot(1)))) return "add";
            if (Members(c, 1) != 2 || Members(c, 0) != 1) return "add sizes " + Members(c, 0) + "/" + Members(c, 1);
            r = Group(c, "add", "B", Slot(1));
            if (!r.Contains("\"rejected\":[{\"uid\":" + Slot(1) + ",\"reason\":\"hangar_taken\"}]")) return "no rejection: " + r;
            if (Members(c, 1) != 2) return "rejected add changed the group";
            // an old stack uid takes every free slot of the pair
            AswFakeDeck d2; var f2 = Game(out d2, 4);
            var c2 = Make(f2);
            Step(c2, f2);
            if (AswController.IsErr(Group(c2, "create", "S", AswMath.HangarUid(77, Sh3)))) return "stack create";
            if (Members(c2) != 4) return "stack members " + Members(c2);
            if (AswController.IsErr(Group(c2, "remove", "S", Slot(2)))) return "remove";
            Run(c2, f2, 2);
            if (Members(c2) != 3 || Logged(f2, "asw slot ")) return "remove disturbed the rest " + Members(c2);
            // review: two uids in one command, the first one is held by another group: the replacement must not eat the second uid
            AswFakeDeck d3; var f3 = Game(out d3, 4);
            var c3 = Make(f3);
            Step(c3, f3);
            if (AswController.IsErr(Group(c3, "create", "A", Slot(1), Slot(2)))) return "multi A";
            if (AswController.IsErr(Group(c3, "create", "B", Slot(2), Slot(3)))) return "multi B";
            if (Members(c3, 0) != 2 || Members(c3, 1) != 2) return "multi sizes " + Members(c3, 0) + "/" + Members(c3, 1);
            return Bad(f) ?? Bad(f2) ?? Bad(f3);
        }

        // ---- slot -> task at the order (ADR-0080 s.3) ----
        private static string Handover()
        {
            AswFakeDeck d; var f = Game(out d, 3, true);
            var c = Make(f);
            Step(c, f);
            if (AswController.IsErr(Create(c, Slot(1)))) return "create";
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Step(c, f);
            if (d.Orders.Count != 1) return "orders " + d.Orders.Count + " " + string.Join(" | ", f.Logs.ToArray());
            string key = "task:t" + d.Tasks[0].N;
            int tuid = AswMath.TaskUid(key);
            if (!Logged(f, "asw handover ASW-3 slot=" + Slot(1) + "#1 key=" + key + " -> uid=" + tuid + " task=dip")) return "handover line";
            Step(c, f);
            var mem = c.View().Groups[0].Members;
            if (mem.Count != 1 || mem[0] != tuid) return "member is not the task uid: " + (mem.Count > 0 ? mem[0].ToString() : "-");
            int slotLines = CountLog(f, "asw slot ");
            Step(c, f);
            if (CountLog(f, "asw slot ") != slotLines) return "slot moved after the order";
            if (AvailableSh3(c) != 2) return "available " + AvailableSh3(c);
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "hangar_taken")) return "lost";
            return Bad(f);
        }

        private static string HandoverWait()
        {
            // the task row is listed a few scans late: the member waits
            AswFakeDeck d; var f = Game(out d, 3, true);
            var c = Make(f);
            Step(c, f);
            Create(c, Slot(1));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step(c, f);
            string key = "task:t" + d.Tasks[0].N;
            d.HideTask(key, 5);
            Run(c, f, 4);
            if (Members(c) != 1) return "member left while the row was hidden";
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "asw member lost")) return "member_lost while waiting";
            Run(c, f, 3);
            if (!Logged(f, "row seen")) return "row never seen";
            // the task is gone without a trace: the member is dropped after OwnRowWaitS
            AswFakeDeck d2; var f2 = Game(out d2, 3, true);
            var c2 = Make(f2);
            Step(c2, f2);
            Create(c2, Slot(1));
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step(c2, f2);
            string key2 = "task:t" + d2.Tasks[0].N;
            d2.HideTask(key2, 100000);
            Run(c2, f2, 20);
            if (Logged(f2, "asw member lost")) return "gave up too early";
            Run(c2, f2, (int)AswController.OwnRowWaitS);
            if (!Logged(f2, "reason=order_vanished")) return "no order_vanished: " + string.Join(" | ", f2.Logs.ToArray());
            return Bad(f) ?? Bad(f2);
        }

        // ---- the machine flies, lands, cools down and is a free slot again ----
        private static string LandBack()
        {
            AswFakeDeck d; var f = Game(out d, 2, false, 5);
            string ka = d.AddTask(Sh3, "SH-3H", 5, true);
            var c = Make(f);
            Step(c, f);
            if (AswController.IsErr(Create(c, d.UidOfTask(ka)))) return "create";
            Run(c, f, 120);
            if (d.Air.Count != 1) return "not airborne " + d.Air.Count;
            if (Air(c) != 1) return "member not followed into the air";
            d.ForceRecover(d.Air[0].Uid);
            Run(c, f, 800);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (!Logged(f, "via=hangar")) return "no via=hangar: " + string.Join(" | ", f.Logs.ToArray());
            if (Logged(f, "merged into")) return "merged";
            if (Members(c) != 1) return "members " + Members(c);
            foreach (var r in c.View().Groups[0].MemberRows) if (r.Slot < 1) return "member is not a slot: " + r.DeckKey;
            return null;
        }

        private static string AbortBack()
        {
            AswFakeDeck d; var f = Game(out d, 3, true, 3000);
            var c = Make(f);
            Step(c, f);
            Create(c, Slot(1));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 4);
            if (d.Tasks.Count != 1) return "tasks " + d.Tasks.Count;
            string key = "task:t" + d.Tasks[0].N;
            if (!d.AbortTask(key)) return "abort";
            Run(c, f, 10);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (!Logged(f, "aborted: back in the hangar -> slot")) return "no aborted line: " + string.Join(" | ", f.Logs.ToArray());
            return null;
        }

        // ---- N machines prepare as N members ----
        private static void Four(out AswFakeDeck d, out AswFakeGame f, out AswController c, string phase)
        {
            f = Game(out d, 6, false, 60);
            d.NewTaskPhase = phase;
            c = Make(f);
            Step(c, f);
            Create(c, Slot(1), Slot(2), Slot(3), Slot(4));
        }

        private static string Parallel()
        {
            AswFakeDeck d; AswFakeGame f; AswController c;
            Four(out d, out f, out c, "ready_up");
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"corridor\"]}")))) return "order";
            Run(c, f, 2);
            if (d.Orders.Count != 4) return "orders " + d.Orders.Count + " " + string.Join(" | ", f.Logs.ToArray());
            foreach (var o in d.Orders) if (!o.EndsWith("allow")) return "order without allow: " + o;
            if (!Logged(f, "asw lanes ASW-3 corridor k=4")) return "no lanes line";
            Run(c, f, 60 + 20 + 30);
            if (Air(c) != 4) return "air members " + Air(c) + " tasks " + d.Tasks.Count + " units " + d.Units.Count;
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "asw member lost")) return "member_lost";
            if (d.Orders.Count != 4) return "ordered again: " + d.Orders.Count;
            return Bad(f);
        }

        private static string DeckFull()
        {
            AswFakeDeck d; AswFakeGame f; AswController c;
            Four(out d, out f, out c, "await_deck");
            c.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));
            Run(c, f, 3);
            if (d.Orders.Count != 4) return "orders " + d.Orders.Count;
            if (AswController.IsErr(Group(c, "add", "ASW-3", Slot(1)))) return "add";   // 6 - 4 ordered = 2 left: k = 1, 2
            Run(c, f, 5);
            if (d.Orders.Count != 4) return "a new order went to a full deck: " + d.Orders.Count;
            if (CountLog(f, "asw deck full ASW-3 deck=77") != 1) return "deck full lines " + CountLog(f, "asw deck full") + " | " + string.Join(" | ", f.Logs.ToArray());
            Run(c, f, 70);
            if (CountLog(f, "asw deck full ASW-3 deck=77") > 2) return "deck full line repeated " + CountLog(f, "asw deck full");
            foreach (var t in d.Tasks) d.SetPhase("task:t" + t.N, "ready_up");
            Run(c, f, 3);
            if (d.Orders.Count != 5) return "no order after the deck had room: " + d.Orders.Count;
            return Bad(f);
        }

        // SAR group of two SH-2F slots: the order hands the member over to the task uid, no "deck row gone: hangar:" (ADR-0080 s.3)
        private static string SarSlots()
        {
            var f = new AswFakeGame();
            f.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            var sar = new SarController(f.SarWorld, new SarParams(), null);
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c.Sar = sar;
            var uids = new List<int>();
            for (int k = 1; k <= 2; k++)
            {
                var h = AswFakeGame.Helo(AswMath.HangarSlotUid(9000, "usn_sh-2f", k), "deck_service");
                h.Type = "usn_sh-2f"; h.TypeLabel = "SH-2F"; h.Name = "SH-2F"; h.DeckKey = AswMath.SlotKey("usn_sh-2f", k); h.Slot = k; h.DeckCount = 2; h.DeckUid = 9000; h.ShipUid = 9000;
                h.Loadout = "Default"; h.Capacity = 2; h.BestCapacity = 2; h.Passive = 0; h.ActiveBuoys = 0; h.Vlad = 0; h.Torpedoes = 0; h.ReadyInS = 900; h.FuelFrac = 1;
                f.Mem.Add(h); uids.Add(h.Uid);
            }
            f.SarWorld.AddRaft(11, 6, 3, 2);
            f.Advance(1); f.Tick(c);
            string r = c.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[" + uids[0] + "," + uids[1] + "]}"));
            if (AswController.IsErr(r)) return r;
            r = c.SarOrder(P("{\"group\":\"SAR-1\"}"));
            if (AswController.IsErr(r)) return r;
            for (int i = 0; i < 90; i++) { f.SarWorld.Step(1); f.Now = f.SarWorld.Now; f.Tick(c); }
            if (Bad(f) != null) return Bad(f);
            if (!Logged(f, "[Sar] order SAR-1 deck=9000") || !Logged(f, "-> ok key=task:o1")) return "no order: " + string.Join(" | ", f.Logs.ToArray());
            if (!Logged(f, "asw handover SAR-1 slot=")) return "no handover";
            if (Logged(f, "deck row gone: hangar:") || Logged(f, "hangar_taken")) return "the slot was reported as a gone hangar row: " + string.Join(" | ", f.Logs.ToArray());
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "asw member lost")) return "member_lost";
            if (c.View().Groups[0].Members.Count < 2) return "members " + c.View().Groups[0].Members.Count;
            return null;
        }
    }

    internal static class AswT181Save
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static int Slot(int k) { return AswMath.HangarSlotUid(77, Sh3, k); }

        private static AswFakeGame Fresh(int hangar, string dir)
        {
            var f = new AswFakeGame();
            var d = new AswFakeDeck();
            d.AddHangar(Sh3, "SH-3H", hangar);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            f.SaveDir = dir;
            return f;
        }

        private static string Dir() { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asw181-" + Guid.NewGuid().ToString("N")); }

        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        // an old file: one member "SH-3H x5" with the uid of the stack; another group already holds one of the machines
        public static string StackMigrate()
        {
            string dir = Dir();
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var data = new AswSaveData { MissionTimeS = 1000 };
                var other = new AswGroupDto { Name = "Other", Order = "none" };
                other.Members.Add(new AswMemberRef { Uid = Slot(1), Name = "SH-3H", Type = Sh3, Deck = 77 });
                var g = new AswGroupDto { Name = "G", Order = "none" };
                g.Members.Add(new AswMemberRef { Uid = AswMath.HangarUid(77, Sh3), Name = "SH-3H \u00d75", Type = Sh3 });
                data.Groups.Add(other); data.Groups.Add(g);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AswSave.Key("/s/a.sav") + ".json"), AswSave.ToJson(data));
                var f = Fresh(5, dir);
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.OnMissionChanged("/s/a.sav", 1000);
                int n = -1; foreach (var gv in c.View().Groups) if (gv.Name == "G") n = gv.Members.Count;
                if (n != 4) return "stack gave " + n + " machines: " + string.Join(" | ", f.Logs.ToArray());
                if (!Logged(f, "asw restore hangar stack G type=" + Sh3 + " deck=77 -> 4 machines")) return "no stack line";
                if (!f.HasEvent("asw_warn", "\"stacks\":1")) return "no stacks in the event: " + string.Join(" | ", f.Events.ToArray());
                // the second variant of Q2: one machine
                string saved = AswSave.StackRestore;
                AswSave.StackRestore = "one";
                try
                {
                    var f2 = Fresh(5, dir);
                    var c2 = new AswController(f2, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                    c2.OnMissionChanged("/s/a.sav", 1000);
                    n = -1; foreach (var gv in c2.View().Groups) if (gv.Name == "G") n = gv.Members.Count;
                    if (n != 1) return "one: " + n;
                }
                finally { AswSave.StackRestore = saved; }
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }

        // a file with the slots k = 2, 4 (deck 77) is loaded; with 3 machines in the hangar the member of the vanished k = 4 takes the free k = 1 at load
        public static string SlotRoundtrip()
        {
            string dir = Dir();
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var data = new AswSaveData { MissionTimeS = 1000 };
                var g = new AswGroupDto { Name = "G", Order = "course", TargetName = "Yorktown", TargetUid = 500 };
                g.Tactics.Add("dip");
                g.Members.Add(new AswMemberRef { Uid = Slot(2), Name = "SH-3H", Type = Sh3, Deck = 77 });
                g.Members.Add(new AswMemberRef { Uid = Slot(4), Name = "SH-3H", Type = Sh3, Deck = 77 });
                data.Groups.Add(g);
                string sav = "/s/b.sav";
                string raw = AswSave.ToJson(data);
                if (!raw.Contains("\"deck\":77")) return "deck not saved: " + raw;
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AswSave.Key(sav) + ".json"), raw);
                var f = new AswFakeGame();
                // same hangar: the same slots
                var f1 = Fresh(5, dir);
                var c1 = new AswController(f1, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c1.OnMissionChanged(sav, f.Now);
                var m1 = c1.View().Groups[0].Members;
                if (m1.Count != 2 || !m1.Contains(Slot(2)) || !m1.Contains(Slot(4))) return "same hangar: " + string.Join(",", m1.ConvertAll(x => x.ToString()).ToArray());
                // three machines left: k = 2 stays, k = 4 moves
                var f3 = Fresh(3, dir);
                var c3 = new AswController(f3, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c3.OnMissionChanged(sav, f.Now);
                var m3 = c3.View().Groups[0].Members;
                if (m3.Count != 2 || !m3.Contains(Slot(2)) || !m3.Contains(Slot(1))) return "three machines: " + string.Join(",", m3.ConvertAll(x => x.ToString()).ToArray()) + " | " + string.Join(" | ", f3.Logs.ToArray());
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }
    }
}
