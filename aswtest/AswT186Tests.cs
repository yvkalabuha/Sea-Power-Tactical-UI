using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-186 / ADR-0082: the ASW window and a fake CAP holder share one AirOwnership and one hangar slot book.</summary>
    internal static class AswT186Tests
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class FakeCap
        {
            public AirOwnership Own;
            public List<AirOpsMath.SlotClaim> Claims = new List<AirOpsMath.SlotClaim>();
            public List<AirOpsMath.SlotMove> Moves = new List<AirOpsMath.SlotMove>();

            public void Take(int k, string group = "ПВО-1")
            {
                int uid = AirOpsMath.HangarSlotUid(77, Sh3, k);
                Own.Claim(uid, "cap", group);
                Claims.Add(new AirOpsMath.SlotClaim { Uid = uid, Group = group, Window = "cap", Seq = Own.GroupSeq("cap", group), JoinedAt = Own.Slots.NextJoin(), K = k, DeckUid = 77, Type = Sh3 });
            }
        }

        private static AswController Game(out AswFakeGame f, out AswFakeDeck d, out FakeCap cap, int hangar)
        {
            f = new AswFakeGame();
            d = new AswFakeDeck { PrepS = 60, HangarReady = true };
            d.AddHangar(Sh3, "SH-3H", hangar);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            var own = new AirOwnership();
            cap = new FakeCap { Own = own };
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own };
            var holder = cap;
            own.Slots.Register("cap", () => holder.Claims, mv => holder.Moves.Add(mv), (deck, type) => -1);
            own.Register("cap", null);
            return c;
        }

        private static void Step(AswController c, AswFakeGame f) { f.Advance(1); f.Tick(c); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) Step(c, f); }
        private static int Slot(int k) { return AirOpsMath.HangarSlotUid(77, Sh3, k); }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        private static string Group(AswController c, string op, params int[] uids)
        {
            var sb = new System.Text.StringBuilder("{\"op\":\"" + op + "\",\"group\":\"ASW-3\",\"uids\":[");
            for (int i = 0; i < uids.Length; i++) { if (i > 0) sb.Append(','); sb.Append(uids[i]); }
            return c.Group(P(sb.Append("]}").ToString()));
        }

        public static string Run(string section)
        {
            string e;
            var all = new[] { "take_free", "reject", "shrink_cap_last", "shrink_asw_last", "order_middle" };
            foreach (var s in all)
            {
                if (section != null && section != s) continue;
                switch (s)
                {
                    case "take_free": e = TakeFree(); break;
                    case "reject": e = Reject(); break;
                    case "shrink_cap_last": e = ShrinkCapLast(); break;
                    case "shrink_asw_last": e = ShrinkAswLast(); break;
                    default: e = OrderMiddle(); break;
                }
                if (e != null) return s + ": " + e;
            }
            return null;
        }

        // the machine k1 is in a CAP group: the view shows it as taken by the other window, create [k1] takes the next free one
        private static string TakeFree()
        {
            AswFakeGame f; AswFakeDeck d; FakeCap cap;
            var c = Game(out f, out d, out cap, 4);
            Step(c, f);
            cap.Take(1);
            Step(c, f);
            bool shown = false;
            foreach (var x in c.View().Excluded)
                if (x.Row.Uid == Slot(1)) shown = x.Reason == "in_group:ПВО-1" && x.OwnerWindow == "cap";
            if (!shown) return "k1 not shown as in_group:ПВО-1 / cap";
            foreach (var a in c.View().Available) if (a.Uid == Slot(1)) return "k1 is available";
            if (!c.StateJson().Contains("\"owner_window\":\"cap\"")) return "owner_window missing in json";
            string r = Group(c, "create", Slot(1));
            if (AswController.IsErr(r)) return r;
            var mem = c.View().Groups[0].Members;
            if (mem.Count != 1 || mem[0] != Slot(2)) return "took " + (mem.Count > 0 ? mem[0].ToString() : "-") + ", want k2";
            return null;
        }

        // no free machine of the pair left: hangar_taken
        private static string Reject()
        {
            AswFakeGame f; AswFakeDeck d; FakeCap cap;
            var c = Game(out f, out d, out cap, 2);
            Step(c, f);
            cap.Take(1);
            Step(c, f);
            if (AswController.IsErr(Group(c, "create", Slot(2)))) return "create k2";
            string r = Group(c, "add", Slot(1));
            if (!r.Contains("hangar_taken")) return "add of a taken machine: " + r;
            if (c.View().Groups[0].Members.Count != 1) return "members changed";
            return null;
        }

        // 4 machines: ASW k1 k2 k3 first, CAP k4 last; one leaves from outside -> CAP loses (after AbortTicks), ASW keeps all
        private static string ShrinkCapLast()
        {
            AswFakeGame f; AswFakeDeck d; FakeCap cap;
            var c = Game(out f, out d, out cap, 4);
            Step(c, f);
            if (AswController.IsErr(Group(c, "create", Slot(1), Slot(2), Slot(3)))) return "create";
            cap.Take(4);
            Step(c, f);
            d.TakeFromHangar(Sh3, 1);
            Run(c, f, 2);
            if (cap.Moves.Count != 0) return "cap dropped too early";
            Run(c, f, 2);
            if (cap.Moves.Count != 1 || !cap.Moves[0].Drop || cap.Moves[0].Uid != Slot(4)) return "cap moves " + cap.Moves.Count;
            if (c.View().Groups[0].Members.Count != 3 || Logged(f, "asw member hangar_taken")) return "asw lost a member";
            return null;
        }

        // the other way round: CAP k1 first, ASW k2 k3 k4 later -> the last ASW member goes (hangar_taken), CAP untouched
        private static string ShrinkAswLast()
        {
            AswFakeGame f; AswFakeDeck d; FakeCap cap;
            var c = Game(out f, out d, out cap, 4);
            Step(c, f);
            cap.Take(1);
            Step(c, f);
            if (AswController.IsErr(Group(c, "create", Slot(2), Slot(3), Slot(4)))) return "create";
            Step(c, f);
            d.TakeFromHangar(Sh3, 1);
            Run(c, f, 4);
            if (!Logged(f, "asw member hangar_taken ASW-3 uid=" + Slot(4))) return "last taker not dropped: " + string.Join(" | ", f.Logs.ToArray());
            if (c.View().Groups[0].Members.Count != 2) return "members " + c.View().Groups[0].Members.Count;
            if (cap.Moves.Count != 0) return "cap moved";
            return null;
        }

        // the ASW order of a middle slot hands the member over to the task: the CAP slot behind it does not move
        private static string OrderMiddle()
        {
            AswFakeGame f; AswFakeDeck d; FakeCap cap;
            var c = Game(out f, out d, out cap, 4);
            Step(c, f);
            if (AswController.IsErr(Group(c, "create", Slot(2)))) return "create";
            cap.Take(4);
            Step(c, f);
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Run(c, f, 6);
            if (!Logged(f, "asw handover")) return "no handover";
            if (cap.Moves.Count != 0) return "cap moved after the order of k2";
            return null;
        }
    }
}
