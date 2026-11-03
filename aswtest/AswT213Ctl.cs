using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-213: the controller side (ctl_air_prep, cycle_timeline).</summary>
    internal static class AswT213Ctl
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Step(AswController c, AswFakeGame f) { f.Advance(1); f.Tick(c); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) Step(c, f); }

        public static string Run(string name)
        {
            return name == "ctl_air_prep" ? AirPrep() : name == "ctl_air_prep_sticky" ? AirPrepSticky() : Timeline();
        }

        private static double GapAfter(bool withHangarRow)
        {
            var f = new AswFakeGame { TimeScale = 4 };
            var air = AswFakeGame.Helo(1, "air", dip: true);
            f.Mem.Add(air);
            if (withHangarRow)
            {
                var h = AswFakeGame.Helo(2, "deck_ready"); h.DeckKey = "hangar:SH-3H#1"; h.DeckUid = air.DeckUid; h.Slot = 1; h.HangarPrepS = 1800; h.DeckCount = 1;
                f.Mem.Add(h);
            }
            var c = Make(f);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}")))) return -4;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"corridor\"]}")))) return -2;
            Run(c, f, 5);
            if (c.View().Groups.Count == 0) return -3;
            return c.View().Groups[0].GapMin;
        }

        private static string AirPrep()
        {
            double g = GapAfter(true);
            if (g != 80) return "with a hangar row of the type: gap " + g;
            g = GapAfter(false);
            if (g != 50) return "no hangar row: gap " + g;
            return null;
        }

        /// <summary>T-221: the flying machine has DeckUid 0 (the game), the preparation is kept by its home deck and type when the hangar row disappears.</summary>
        private static string AirPrepSticky()
        {
            var f = new AswFakeGame { TimeScale = 4 };
            var air = AswFakeGame.Helo(1, "air", dip: true);
            air.DeckUid = 0; air.ShipUid = 9001;
            f.Mem.Add(air);
            var h = AswFakeGame.Helo(2, "deck_ready"); h.DeckKey = "hangar:SH-3H#1"; h.DeckUid = 9001; h.ShipUid = 9001; h.Slot = 1; h.HangarPrepS = 1800; h.DeckCount = 1;
            f.Mem.Add(h);
            var c = Make(f);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}")))) return "group";
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"corridor\"]}")))) return "course";
            Run(c, f, 5);
            double g = c.View().Groups.Count == 0 ? -3 : c.View().Groups[0].GapMin;
            if (g != 80) return "with the row: gap " + g;
            f.Mem.Remove(h);
            Run(c, f, 5);
            g = c.View().Groups[0].GapMin;
            if (g != 80) return "row gone: gap " + g;
            // a row of the same type on ANOTHER deck does not replace the known value of the own deck
            var o = AswFakeGame.Helo(3, "deck_ready"); o.DeckKey = "hangar:SH-3H#2"; o.DeckUid = 9500; o.ShipUid = 9500; o.Slot = 1; o.HangarPrepS = 600; o.DeckCount = 1;
            f.Mem.Add(o);
            Run(c, f, 5);
            g = c.View().Groups[0].GapMin;
            if (g != 80) return "other deck row: gap " + g;
            // never seen a row: no data, the old 50
            var f2 = new AswFakeGame { TimeScale = 4 };
            var a2 = AswFakeGame.Helo(1, "air", dip: true); a2.DeckUid = 0; a2.ShipUid = 9001;
            f2.Mem.Add(a2);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));
            Run(c2, f2, 5);
            g = c2.View().Groups[0].GapMin;
            if (g != 50) return "no data: gap " + g;
            return null;
        }

        /// <summary>One SH-3H flies twice: land, cool down, hangar, an order again; each gap includes the preparation.</summary>
        private static string Timeline()
        {
            var f = new AswFakeGame();
            var d = new AswFakeDeck { PrepS = 5, HangarReady = true, HangarPrepS = 1800, CoolS = 60, FuelBurnS = 1500 };
            d.AddHangar(Sh3, "SH-3H", 1);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            var c = Make(f);
            Step(c, f);
            int slot = AswMath.HangarSlotUid(77, Sh3, 1);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"ASW-3\",\"uids\":[" + slot + "]}")))) return "create";
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"corridor\"]}")))) return "order";
            double minGap = double.MaxValue; int gaps = 0;
            for (int i = 0; i < 12000; i++)
            {
                Step(c, f);
                if (c.View().Groups.Count == 0) continue;
                double g = c.View().Groups[0].GapMin;
                bool taxi = false; foreach (var u in d.Units) if (u.Phase == "launching") taxi = true;
                if (g > 0 && (taxi || c.View().Groups[0].MemberRows.Count == 0)) continue;   // the hand-over slot -> task -> taxi: no member in the rotation for a few ticks (the old 50 min stays)
                if (g > 0) { gaps++; minGap = Math.Min(minGap, g); }
                if (d.Orders.Count >= 3 && d.Air.Count > 0) break;
            }
            foreach (var l in f.Logs) if (l.Contains("tick error")) return l;
            if (d.Orders.Count < 2) return "orders " + d.Orders.Count + " | " + string.Join(" | ", f.Logs.ToArray());
            if (gaps == 0) return "no continuity_gap";
            if (minGap < 80) return "gap " + minGap + " min, no preparation in it";
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            int tasks = d.Tasks.Count;
            if (tasks > 1) return "tasks pile up " + tasks;
            return null;
        }
    }
}
