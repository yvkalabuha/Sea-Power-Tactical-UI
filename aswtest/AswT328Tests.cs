using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-328 (ADR-0127): the ASW side of the ring group sectors: AdmitWhy (dry run), the group list with kinds, membership, a move. `aswtest t328 <name>`.
    internal static class AswT328Tests
    {
        private static readonly string[] Names = { "admit", "ignore", "no_effects", "same_as_add", "kinds", "move", "hangar", "waiting" };
        private const string Sh3 = "usn_sh-3h";

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static void Step(AswController c, AswFakeGame f) { f.Advance(1); f.Tick(c); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "admit": e = Admit(); break;
                    case "ignore": e = Ignore(); break;
                    case "no_effects": e = NoEffects(); break;
                    case "same_as_add": e = SameAsAdd(); break;
                    case "kinds": e = Kinds(); break;
                    case "move": e = Move(); break;
                    case "hangar": e = Hangar(); break;
                    default: e = Waiting(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public AswController C;
            public AirOwnership Own = new AirOwnership();
            public World()
            {
                F.Mem.Add(AswFakeGame.Helo(1, "air")); F.Mem.Add(AswFakeGame.Helo(2, "air"));   // helicopters with buoys and torpedoes
                var fighter = AswFakeGame.Helo(4, "air"); fighter.Name = "Tomcat 4"; fighter.Type = "F-14A"; fighter.Helicopter = false;
                fighter.Passive = 0; fighter.ActiveBuoys = 0; fighter.Vlad = 0; fighter.Torpedoes = 0; fighter.Loadout = "AirToAir";
                F.Mem.Add(fighter);
                var awacs = AswFakeGame.Helo(5, "air"); awacs.Name = "Hawkeye 5"; awacs.Type = "E-2C"; awacs.Helicopter = false; awacs.Roles = new[] { "AEW" };
                awacs.Passive = 0; awacs.ActiveBuoys = 0; awacs.Vlad = 0; awacs.Torpedoes = 0;
                F.Mem.Add(awacs);
                var player = AswFakeGame.Helo(6, "player");
                F.Mem.Add(player);
                var nosar = AswFakeGame.Helo(7, "air"); nosar.Capacity = 0; nosar.BestCapacity = 0;   // no seats
                F.Mem.Add(nosar);
                var seats = AswFakeGame.Helo(8, "air"); seats.Capacity = 6; seats.BestCapacity = 6; seats.Torpedoes = 0;   // a SAR machine without a torpedo
                F.Mem.Add(seats);
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = Own };
                Own.Register("cap", null);
                C.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"uids\":[1]}"));       // search group
                C.Group(P("{\"op\":\"create\",\"group\":\"УДАР-1\",\"uids\":[2]}"));
                C.Order(P("{\"group\":\"УДАР-1\",\"order\":\"strike\",\"radius_nm\":25,\"targets\":\"hostile\",\"airborne\":1,\"ref_uid\":500}"));
                C.Group(P("{\"op\":\"create\",\"group\":\"SAR-1\",\"uids\":[8]}"));
                MakeSar("SAR-1");
                C.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-2\",\"uids\":[4]}"));   // an empty group: created with one unit, then emptied
                C.Group(P("{\"op\":\"remove\",\"group\":\"ПЛО-2\",\"uids\":[4]}"));
                Step(C, F); C.EnsureLive();
            }
            /// <summary>A SAR group by the order field (the SAR controller itself is not part of this stand).</summary>
            public void MakeSar(string name)
            {
                var groups = (System.Collections.IList)typeof(AswController).GetField("_groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(C);
                foreach (var g in groups)
                {
                    var t = g.GetType();
                    if ((string)t.GetField("Name").GetValue(g) == name) t.GetField("Order").SetValue(g, "sar");
                }
            }
            public string Why(int uid, string group, string iw = null, string ig = null) { C.EnsureLive(); return C.AdmitWhy(uid, group, iw, ig); }
            public string Add(string group, int uid) { return C.Group(P("{\"op\":\"add\",\"group\":\"" + group + "\",\"uids\":[" + uid + "]}")); }
        }

        private static string Admit()
        {
            var w = new World();
            string r;
            if ((r = w.Why(2, "ПЛО-2")) != null) return "a helicopter with an ASW load may join a search group: " + r;
            if ((r = w.Why(4, "ПЛО-2")) != "no_asw_load") return "fighter: " + r;
            if ((r = w.Why(5, "ПЛО-2")) != "aew") return "AEW: " + r;
            if ((r = w.Why(6, "ПЛО-2")) != "player") return "player: " + r;
            if ((r = w.Why(999, "ПЛО-2")) != "not_found") return "not found: " + r;
            if ((r = w.Why(2, "Нет")) != "unknown_group") return "unknown group: " + r;
            if ((r = w.Why(7, "SAR-1")) != "no_capacity") return "SAR without seats: " + r;
            if ((r = w.Why(8, "SAR-1")) != null) return "SAR with seats: " + r;
            if ((r = w.Why(2, "УДАР-1")) != null) return "strike with a torpedo: " + r;
            if ((r = w.Why(8, "УДАР-1")) != "no_torpedo") return "strike without a torpedo: " + r;
            w.Own.Claim(2, "cap", "ПВО-1");
            if ((r = w.Why(2, "ПЛО-2")) != "in_group:ПВО-1") return "in a CAP group: " + r;
            return null;
        }

        private static string Ignore()
        {
            var w = new World();
            w.Own.Claim(2, "cap", "ПВО-1");
            if (w.Why(2, "ПЛО-2", "cap", "ПВО-1") != null) return "ignore pair";
            if (w.Why(2, "ПЛО-2", "cap", "ПВО-2") != "in_group:ПВО-1") return "other group";
            if (w.Why(2, "ПЛО-2", "asw", "ПВО-1") != "in_group:ПВО-1") return "other window";
            // another ASW group does not block: the add command moves the unit out of it
            if (w.Why(1, "ПЛО-2") != null) return "from another ASW group: " + w.Why(1, "ПЛО-2");
            return null;
        }

        private static string NoEffects()
        {
            var w = new World();
            string before = w.C.StateJson();
            int calls = w.F.MembersCalls;
            foreach (int u in new[] { 1, 2, 4, 5, 6, 7, 8, 999 })
                foreach (string g in new[] { "ПЛО-1", "ПЛО-2", "УДАР-1", "SAR-1", "Нет" }) w.C.AdmitWhy(u, g, null, null);
            if (w.C.StateJson() != before) return "state changed";
            if (w.C.RingGroupOf(1) != "ПЛО-1" || w.C.RingGroupOf(2) != "УДАР-1" || w.C.RingGroupOf(4) != null) return "members changed";
            if (w.F.MembersCalls != calls) return "AdmitWhy scans the game";
            string o; if (w.Own.Owner(4, out o) != null) return "an owner was made";
            return null;
        }

        // every green answer is a real add (the ring is stricter than the command: a fighter is refused for a search group, the add command would take it)
        private static string SameAsAdd()
        {
            foreach (int uid in new[] { 1, 2, 4, 5, 6, 7, 8 })
                foreach (string g in new[] { "ПЛО-2", "УДАР-1", "SAR-1" })
                {
                    var w = new World();
                    string why = w.Why(uid, g);
                    w.Add(g, uid);
                    bool member = w.C.RingGroupOf(uid) == g;
                    if (why == null && !member) return "uid " + uid + " -> " + g + ": green but not added";
                }
            return null;
        }

        private static string Kinds()
        {
            var w = new World();
            var l = w.C.RingGroups();
            Func<string, RingGroupOption> by = n => l.FirstOrDefault(x => x.Name == n);
            if (l.Count != 4) return "count " + l.Count;
            if (by("ПЛО-1") == null || by("ПЛО-1").Kind != "asw" || by("ПЛО-1").Window != "asw" || by("ПЛО-1").Members != 1) return "search group";
            if (by("УДАР-1").Kind != "strike") return "strike kind " + by("УДАР-1").Kind;
            if (by("SAR-1").Kind != "sar") return "sar kind " + by("SAR-1").Kind;
            if (by("ПЛО-2").Members != 0) return "empty group";
            if (l[0].Name != "ПЛО-1" || l[3].Name != "ПЛО-2") return "window order";
            if (!w.C.RingReady) return "ready";
            return null;
        }

        // the ring's move: dry run, remove, add; asw -> asw works, asw -> sar of a unit without seats is refused beforehand
        private static string Move()
        {
            var w = new World();
            if (w.Why(1, "ПЛО-2", "asw", "ПЛО-1") != null) return "dry run";
            w.C.Group(P("{\"op\":\"remove\",\"group\":\"ПЛО-1\",\"uids\":[1]}"));
            if (w.C.RingGroupOf(1) != null) return "not removed";
            w.Add("ПЛО-2", 1);
            if (w.C.RingGroupOf(1) != "ПЛО-2") return "not added";
            if (w.Why(1, "SAR-1", "asw", "ПЛО-2") == null) return "a helicopter with no seats must not join a SAR group";
            return null;
        }

        // a hangar machine: the pair has a free one -> ok; none left -> hangar_taken
        private static string Hangar()
        {
            var f = new AswFakeGame();
            var d = new AswFakeDeck { PrepS = 60, HangarReady = true };
            d.AddHangar(Sh3, "SH-3H", 2);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            var own = new AirOwnership();
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own };
            var claims = new List<AirOpsMath.SlotClaim>();
            own.Slots.Register("cap", () => claims, mv => { }, (deck, type) => -1);
            own.Register("cap", null);
            Step(c, f);
            int s1 = AirOpsMath.HangarSlotUid(77, Sh3, 1), s2 = AirOpsMath.HangarSlotUid(77, Sh3, 2);
            c.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"uids\":[" + s2 + "]}"));
            own.Claim(s1, "cap", "ПВО-1");
            claims.Add(new AirOpsMath.SlotClaim { Uid = s1, Group = "ПВО-1", Window = "cap", Seq = 1, JoinedAt = own.Slots.NextJoin(), K = 1, DeckUid = 77, Type = Sh3 });
            c.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-2\",\"uids\":[]}"));
            Step(c, f); c.EnsureLive();
            string r = c.AdmitWhy(s1, "ПЛО-2", null, null);
            if (r != "in_group:ПВО-1" && r != "hangar_taken") return "taken slot, none free: " + r;
            return null;
        }

        private static string Waiting()
        {
            var w = new World();
            typeof(AswController).GetField("_deferred", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(w.C, new AswSaveData());
            if (w.C.AdmitWhy(2, "ПЛО-2", null, null) != "restore_waiting" || w.C.RingReady) return "restore_waiting";
            return null;
        }
    }
}
