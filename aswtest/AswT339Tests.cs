using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-339: Ka-29 (Helicopter, roles Attack/Transport/SAR, 16 seats, loadouts Strike/CAS/AntiArmor/Empty, no ASW gear) may serve in a SAR group,
    // never in an ASW search or strike group; ASW helicopters (Ka-27, SH-3H) keep their rules. `aswtest t339 <name>`.
    internal static class AswT339Tests
    {
        private static readonly string[] Names = { "math", "lists", "admit", "add" };
        private const string Ka29 = "wp_ka-29", Ka27 = "wp_ka-27";
        private const int Deck = 61;

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "math": e = Math(); break;
                    case "lists": e = Lists(); break;
                    case "admit": e = Admit(); break;
                    default: e = Add(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        /// <summary>A hangar slot of the Ka-29 as AswGame.Deck builds it (first loadout Strike: FAB-250), the seats as DeckSeats fills them.</summary>
        internal static AswMember Ka29Slot(int k)
        {
            var d = new AswMath.DeckEntry
            {
                ShipUid = Deck, ShipName = "Kuznetsov", Type = Ka29, Label = "Ka-29", Loadout = "Strike", Roles = new[] { "Attack", "Transport", "SAR" },
                Count = 2, Slot = k, State = "hangar", ReadyInS = 900, Uid = AswMath.HangarSlotUid(Deck, Ka29, k), Key = AswMath.SlotKey(Ka29, k),
                Ammo = new Dictionary<string, int> { { "wp_fab-250", 5 } }, HangarPrepS = 900, Helicopter = true, AswCapableType = false, TypeDipping = false
            };
            var m = AswMath.DeckToMember(d);
            m.BestCapacity = 16; m.Capacity = 16;   // PatrolZone.GetSARTransportCapacity of every Ka-29 loadout ([Physics] TransportCapacity=16)
            return m;
        }

        internal static AswMember Ka27Slot(int k)
        {
            var d = new AswMath.DeckEntry
            {
                ShipUid = Deck, ShipName = "Kuznetsov", Type = Ka27, Label = "Ka-27", Loadout = "ASW", Roles = new[] { "ASW", "SAR" },
                Count = 2, Slot = k, State = "hangar", ReadyInS = 1800, Uid = AswMath.HangarSlotUid(Deck, Ka27, k), Key = AswMath.SlotKey(Ka27, k),
                Ammo = new Dictionary<string, int> { { "rgb-nm", 18 }, { "apr-2", 1 } }, HangarPrepS = 1800, Helicopter = true, AswCapableType = true, TypeDipping = true
            };
            var m = AswMath.DeckToMember(d);
            m.BestCapacity = 10; m.Capacity = 10;
            return m;
        }

        internal static AswMember Ka29Air(int uid)
        {
            return new AswMember { Uid = uid, Name = "Ka-29 " + uid, Type = Ka29, TypeLabel = "Ka-29", Where = "air", Helicopter = true, Base = "Kuznetsov", ShipUid = Deck,
                Loadout = "Strike", Roles = new[] { "Attack", "Transport", "SAR" }, Capacity = 16, BestCapacity = 16, FuelFrac = 0.9, EnduranceS = 9000, TransitS = 600, LaunchS = 120,
                Lat = 35, Lon = 20, DipSrc = "live" };
        }

        private static string Math()
        {
            var s = Ka29Slot(1);
            string r;
            if ((r = AswMath.SarExclusionReason(s)) != null) return "Ka-29 hangar slot SAR: " + r;
            if ((r = AswMath.ExclusionReason(s)) != "no_asw_load") return "Ka-29 hangar slot ASW search: " + r;
            if ((r = AswStrikeMath.StrikeExclusion(s, false)) == null) return "Ka-29 hangar slot ASW strike admitted";
            if (s.Dipping || s.Mad || s.Torpedoes != 0 || s.Passive + s.ActiveBuoys + s.Vlad != 0) return "Ka-29 got nominal ASW gear";
            var a = Ka29Air(5001);
            if ((r = AswMath.SarExclusionReason(a)) != null) return "Ka-29 airborne SAR: " + r;
            if ((r = AswMath.ExclusionReason(a)) != "no_asw_load") return "Ka-29 airborne ASW search: " + r;
            if ((r = AswStrikeMath.StrikeExclusion(a, false)) == null) return "Ka-29 airborne ASW strike admitted";
            // seats only from the game data: no seats = no SAR (unchanged)
            var none = Ka29Slot(2); none.BestCapacity = 0; none.Capacity = 0;
            if ((r = AswMath.SarExclusionReason(none)) != "no_capacity") return "no seats: " + r;
            // Ka-27 unchanged: ASW search, ASW strike (torpedo), SAR
            var k27 = Ka27Slot(1);
            if ((r = AswMath.ExclusionReason(k27)) != null) return "Ka-27 ASW search: " + r;
            if ((r = AswStrikeMath.StrikeExclusion(k27, true)) != null) return "Ka-27 strike: " + r;
            if ((r = AswMath.SarExclusionReason(k27)) != null) return "Ka-27 SAR: " + r;
            var sh3 = AswFakeGame.Helo(7, "air"); sh3.Capacity = 8; sh3.BestCapacity = 8;
            if (AswMath.ExclusionReason(sh3) != null || AswStrikeMath.StrikeExclusion(sh3, true) != null || AswMath.SarExclusionReason(sh3) != null) return "SH-3H changed";
            return null;
        }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public AswController C;
            public World()
            {
                F.Mem.Add(Ka29Slot(1)); F.Mem.Add(Ka29Slot(2)); F.Mem.Add(Ka29Air(5001));
                F.Mem.Add(Ka27Slot(1)); F.Mem.Add(Ka27Slot(2));
                var sh3 = AswFakeGame.Helo(2, "air"); sh3.Capacity = 8; sh3.BestCapacity = 8; F.Mem.Add(sh3);   // torpedoes: the strike group's first member
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                C.Group(P("{\"op\":\"create\",\"group\":\"SAR-1\",\"uids\":[" + Ka27Slot(1).Uid + "]}"));
                SetOrder("SAR-1", "sar");
                C.Group(P("{\"op\":\"create\",\"group\":\"УДАР-1\",\"uids\":[2]}"));
                C.Order(P("{\"group\":\"УДАР-1\",\"order\":\"strike\",\"radius_nm\":25,\"targets\":\"hostile\",\"airborne\":1,\"ref_uid\":500}"));
                C.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"uids\":[" + Ka27Slot(2).Uid + "]}"));
                F.Advance(1); F.Tick(C); C.EnsureLive();
            }
            public void SetOrder(string name, string order)
            {
                var groups = (System.Collections.IList)typeof(AswController).GetField("_groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(C);
                foreach (var g in groups) { var t = g.GetType(); if ((string)t.GetField("Name").GetValue(g) == name) t.GetField("Order").SetValue(g, order); }
            }
            public string Why(int uid, string group) { C.EnsureLive(); return C.AdmitWhy(uid, group, null, null); }
        }

        private static readonly int[] Ka29Uids = { AswMath.HangarSlotUid(Deck, Ka29, 1), AswMath.HangarSlotUid(Deck, Ka29, 2), 5001 };

        // the window lists: SAR "Available" has every Ka-29; ASW search and strike list them under "excluded" with a grey reason
        private static string Lists()
        {
            var w = new World();
            var v = w.C.View();
            foreach (int u in Ka29Uids)
            {
                if (!v.AvailableSar.Any(r => r.Uid == u)) return "Ka-29 " + u + " not in available_sar (excluded_sar: " + string.Join(",", v.ExcludedSar.Where(x => x.Row.Uid == u).Select(x => x.Reason)) + ")";
                if (v.Available.Any(r => r.Uid == u)) return "Ka-29 " + u + " in the ASW search list";
                if (!v.Excluded.Any(x => x.Row.Uid == u && x.Reason == "no_asw_load")) return "Ka-29 " + u + " search reason";
                if (v.AvailableStrike.Any(r => r.Uid == u)) return "Ka-29 " + u + " in the strike list";
                var ex = v.ExcludedStrike.FirstOrDefault(x => x.Row.Uid == u);
                if (ex == null || string.IsNullOrEmpty(AswWindowModel.ExcludedText(ex.Reason)) || ex.Reason == AswWindowModel.ExcludedText(ex.Reason)) return "Ka-29 " + u + " strike reason " + (ex == null ? "-" : ex.Reason);
            }
            var sarRow = v.AvailableSar.First(r => r.Uid == Ka29Uids[0]);
            if (sarRow.Capacity != 16) return "seats column " + sarRow.Capacity;
            // Ka-27: search list (the free slot) and SAR list, unchanged
            if (!v.AvailableSar.Any(r => r.Uid == AswMath.HangarSlotUid(Deck, Ka27, 2)) && !v.ExcludedSar.Any(x => x.Row.Uid == AswMath.HangarSlotUid(Deck, Ka27, 2) && x.Reason.StartsWith("in_group:", StringComparison.Ordinal))) return "Ka-27 sar list";
            return null;
        }

        // the ring "+": SAR green, search and strike grey with a reason
        private static string Admit()
        {
            var w = new World();
            foreach (int u in Ka29Uids)
            {
                string r;
                if ((r = w.Why(u, "SAR-1")) != null) return "Ka-29 " + u + " SAR-1: " + r;
                if ((r = w.Why(u, "ПЛО-1")) != "no_asw_load") return "Ka-29 " + u + " ПЛО-1: " + r;
                if ((r = w.Why(u, "УДАР-1")) != "no_asw_load") return "Ka-29 " + u + " УДАР-1: " + r;
            }
            if (w.Why(AswMath.HangarSlotUid(Deck, Ka27, 2), "ПЛО-1") != null && w.C.RingGroupOf(AswMath.HangarSlotUid(Deck, Ka27, 2)) != "ПЛО-1") return "Ka-27 search";
            if (w.Why(2, "SAR-1") != null) return "SH-3H SAR-1";
            return null;
        }

        // "Add to SAR-1" of the window / the ring's add: the Ka-29 slot and the airborne Ka-29 become members; the strike group refuses them
        private static string Add()
        {
            var w = new World();
            string res = w.C.Group(P("{\"op\":\"add\",\"group\":\"SAR-1\",\"uids\":[" + Ka29Uids[0] + ",5001]}"));
            if (AswController.IsErr(res)) return "add: " + res;
            if (w.C.RingGroupOf(Ka29Uids[0]) != "SAR-1") return "slot not in SAR-1: " + res;
            if (w.C.RingGroupOf(5001) != "SAR-1") return "airborne not in SAR-1: " + res;
            w.C.Group(P("{\"op\":\"add\",\"group\":\"УДАР-1\",\"uids\":[" + Ka29Uids[1] + "]}"));
            if (w.C.RingGroupOf(Ka29Uids[1]) == "УДАР-1") return "strike took a Ka-29";
            return null;
        }
    }
}
