#if SAR_WIP
using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-231 stream B: SAR groups and a knocked-out flight deck / a hangar row that comes back. `aswtest t231sar <name>`.
    internal static class AswT231SarTests
    {
        private static readonly string[] Names = { "deck-down-sar", "reserve-back" };

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public SarController S;
            public AswController C;

            public World()
            {
                F.Mode = "Arcade";
                F.LoadoutList.Add(new LoadoutOption { Name = "ASW", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
                S = new SarController(F.SarWorld, new SarParams(), null);
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                C.Sar = S;
            }

            public AswMember Deck(int uid, int deckUid, string where = "deck_ready", string type = "SH-2F")
            {
                var m = AswFakeGame.Helo(uid, where);
                m.Type = type; m.DeckKey = "task:t" + uid; m.Loadout = "ASW"; m.Capacity = 2; m.BestCapacity = 2; m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0;
                m.DeckUid = deckUid; m.ReadyInS = 0; m.FuelFrac = 1;
                F.Mem.Add(m);
                return m;
            }

            public void Step()
            {
                var sw = F.SarWorld;
                foreach (var m in F.Mem)
                    if (m.Where == "air" && !sw.Units.ContainsKey(m.Uid)) { var u = sw.AddUnit(m.Uid, "helo", 0, 0); u.Capacity = m.Capacity; u.Onboard = m.Onboard; }
                sw.Step(1);
                F.Now = sw.Now;
                F.Tick(C);
            }

            public void Run(int s) { for (int i = 0; i < s; i++) Step(); }
            public int LogCount(string sub) { return F.Logs.Count(l => l.Contains(sub)); }
            public bool Warned(string code) { return C.View().Warnings.Exists(x => x.StartsWith(code, StringComparison.Ordinal)); }
        }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e = n == "deck-down-sar" ? DeckDownSar() : ReserveBack();
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Start(World w, int uid)
        {
            string r = w.C.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[" + uid + "]}"));
            if (AswController.IsErr(r)) return r;
            r = w.C.SarOrder(P("{\"group\":\"SAR-1\"}"));
            return AswController.IsErr(r) ? r : null;
        }

        private static string DeckDownSar()
        {
            var w = new World();
            w.Deck(1, 77);
            w.F.SarWorld.AddRaft(11, 6, 3, 2);
            w.F.DeckOut = true;
            string err = Start(w, 1); if (err != null) return err;
            w.Run(120);
            if (w.LogCount("deck_inoperable") != 0) return "deck_inoperable lines: " + w.LogCount("deck_inoperable");
            if (w.F.OrderLog.Count != 0 || w.F.Allows.Count != 0 || w.F.Calls.Count != 0) return "orders on a dead deck: " + w.F.OrderLog.Count + "/" + w.F.Allows.Count + "/" + w.F.Calls.Count;
            if (w.LogCount("asw deck down SAR-1 deck=77") != 1) return "deck down lines: " + w.LogCount("asw deck down SAR-1 deck=77");
            if (!w.Warned("deck_down")) return "no deck_down warning";
            if (w.LogCount("launch_failed") != 0) return "launch_failed";
            w.F.DeckOut = false;
            w.Run(40);
            if (w.LogCount("asw deck up SAR-1 deck=77") != 1) return "no deck up";
            if (w.Warned("deck_down")) return "warning stays";
            if (w.F.Allows.Count + w.F.OrderLog.Count + w.F.Calls.Count < 1) return "no order after the repair: " + string.Join(" | ", w.F.Logs.Where(l => l.StartsWith("[Sar]", StringComparison.Ordinal)).ToArray());
            return null;
        }

        private static string ReserveBack()
        {
            var w = new World();
            w.Deck(1, 9000);
            w.F.SarWorld.AddRaft(11, 60, 30, 5); w.F.SarWorld.AddRaft(12, 61, 31, 5); w.F.SarWorld.AddRaft(13, 62, 29, 5);
            w.F.NoAirframeDecks.Add(9000);
            string err = Start(w, 1); if (err != null) return err;
            w.Run(120);
            if (!w.Warned("no_reserve_deck")) return "no warning after no_airframes: " + string.Join(" | ", w.F.Logs.Where(l => l.StartsWith("[Sar] order", StringComparison.Ordinal)).ToArray());
            // the machine comes back: a free hangar row of the deck appears, the hangar of the game has it again
            w.F.NoAirframeDecks.Clear();
            var h = AswFakeGame.Helo(-801, "deck_ready");
            h.Type = "SH-2F"; h.DeckKey = "hangar:SH-2F#1"; h.Slot = 1; h.DeckCount = 1; h.Loadout = "ASW"; h.Capacity = 2; h.BestCapacity = 2; h.DeckUid = 9000; h.Passive = 0; h.ActiveBuoys = 0; h.Vlad = 0; h.Torpedoes = 0;
            w.F.Mem.Add(h);
            w.Run(45);
            bool changed = w.F.Logs.Exists(l => l.Contains("retry after no_airframes") && l.Contains("deck changed"));
            if (!changed) return "no retry by 'deck changed': " + string.Join(" | ", w.F.Logs.Where(l => l.StartsWith("[Sar] order", StringComparison.Ordinal)).ToArray());
            bool ok = w.F.Logs.Exists(l => l.StartsWith("[Sar] order SAR-1 deck=9000", StringComparison.Ordinal) && l.Contains("-> ok"));
            if (!ok) return "no order after the retry";
            if (w.Warned("no_reserve_deck")) return "no_reserve_deck stays after the order";
            return null;
        }
    }
}
#endif
