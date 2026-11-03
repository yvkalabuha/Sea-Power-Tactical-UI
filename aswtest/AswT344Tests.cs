#if SAR_WIP
using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-344: a SAR no_ammo refusal belongs to (deck, type, loadout): the same loadout name of another type or deck is not hidden.
    // `aswtest t344 <name>`.
    internal static class AswT344Tests
    {
        private static readonly string[] Names = { "noammo-other-type", "noammo-same-pair", "noammo-other-deck" };

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static LoadoutOption Lo(string n, int cap)
        {
            return new LoadoutOption { Name = n, Display = n, Capacity = cap, ReadyUpMin = 15, CoolDownMin = 15, Available = true, Weapons = n == "Empty" ? new Dictionary<string, int>() : new Dictionary<string, int> { { "x", 2 } } };
        }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public SarController S;
            public AswController C;

            public World()
            {
                F.Mode = "Arcade";
                S = new SarController(F.SarWorld, new SarParams(), null);
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                C.Sar = S;
            }

            public AswMember Deck(int uid, int deckUid, string type, string loadout, int cap)
            {
                var m = AswFakeGame.Helo(uid, "deck_prep");
                m.Type = type; m.DeckKey = "task:t" + uid; m.Loadout = loadout; m.Capacity = cap; m.BestCapacity = cap; m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0;
                m.DeckUid = deckUid; m.ReadyInS = 600; m.FuelFrac = 1;
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
            public bool Ordered(string prefix) { return F.OrderLog.Exists(o => o.StartsWith(prefix, StringComparison.Ordinal)); }
            public int OrderCount(string prefix) { return F.OrderLog.Count(o => o.StartsWith(prefix, StringComparison.Ordinal)); }
            public string Dump() { return string.Join(" | ", F.OrderLog.ToArray()) + " || " + string.Join(" | ", F.Logs.Where(l => l.StartsWith("[Sar]", StringComparison.Ordinal)).ToArray()); }

            public string Start(params int[] uids)
            {
                string r = C.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[" + string.Join(",", uids) + "]}"));
                if (AswController.IsErr(r)) return r;
                r = C.SarOrder(P("{\"group\":\"SAR-1\"}"));
                return AswController.IsErr(r) ? r : null;
            }

            public string Add(int uid)
            {
                string r = C.Group(P("{\"group\":\"SAR-1\",\"op\":\"add\",\"uids\":[" + uid + "]}"));
                return AswController.IsErr(r) ? r : null;
            }
        }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "noammo-other-type": e = OtherType(); break;
                    case "noammo-same-pair": e = SamePair(); break;
                    default: e = OtherDeck(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static List<LoadoutOption> Ka27() { return new List<LoadoutOption> { Lo("ASW", 10), Lo("Strike", 10), Lo("Empty", 10) }; }
        private static List<LoadoutOption> Ka29() { return new List<LoadoutOption> { Lo("Strike", 16), Lo("Empty", 16) }; }

        // the Ka-29 Empty is refused; the Ka-27 that joins later still orders its own Empty
        private static string OtherType()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-27"] = Ka27();
            w.F.LoadoutsByType["wp_ka-29"] = Ka29();
            w.F.NoAmmo.Add("wp_ka-29/Empty");
            w.Deck(1, 62, "wp_ka-29", "Strike", 16);
            w.F.SarWorld.AddRaft(11, 6, 3, 2);
            string err = w.Start(1); if (err != null) return err;
            w.Run(30);
            if (!w.Ordered("62:wp_ka-29:Empty:")) return "Ka-29 Empty never tried: " + w.Dump();
            if (!w.Ordered("62:wp_ka-29:Strike:")) return "Ka-29 did not fall back to Strike: " + w.Dump();
            w.Deck(2, 61, "wp_ka-27", "Strike", 10);
            w.F.SarWorld.AddRaft(12, 7, 3, 2); w.F.SarWorld.AddRaft(13, 8, 3, 2);
            err = w.Add(2); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("61:wp_ka-27:Empty:")) return "Ka-27 Empty hidden by the Ka-29 refusal: " + w.Dump();
            return null;
        }

        // the refused pair itself is not asked again
        private static string SamePair()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-29"] = Ka29();
            w.F.NoAmmo.Add("wp_ka-29/Empty");
            w.Deck(1, 62, "wp_ka-29", "Strike", 16);
            w.F.SarWorld.AddRaft(11, 6, 3, 2); w.F.SarWorld.AddRaft(12, 7, 3, 2);
            string err = w.Start(1); if (err != null) return err;
            w.Run(120);
            int n = w.OrderCount("62:wp_ka-29:Empty:");
            if (n != 1) return "Empty orders " + n + " (expected 1): " + w.Dump();
            if (!w.Ordered("62:wp_ka-29:Strike:")) return "no Strike order: " + w.Dump();
            return null;
        }

        // the refusal of deck 61 does not hide the same type on deck 63
        private static string OtherDeck()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-27"] = Ka27();
            w.F.NoAmmo.Add("61:wp_ka-27/Empty");
            w.Deck(1, 61, "wp_ka-27", "Strike", 10);
            w.F.SarWorld.AddRaft(11, 6, 3, 2);
            string err = w.Start(1); if (err != null) return err;
            w.Run(30);
            if (!w.Ordered("61:wp_ka-27:Empty:")) return "deck 61 Empty never tried: " + w.Dump();
            w.Deck(2, 63, "wp_ka-27", "Strike", 10);
            w.F.SarWorld.AddRaft(12, 7, 3, 2); w.F.SarWorld.AddRaft(13, 8, 3, 2);
            err = w.Add(2); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("63:wp_ka-27:Empty:")) return "deck 63 Empty hidden by the deck 61 refusal: " + w.Dump();
            return null;
        }
    }
}
#endif
