#if SAR_WIP
using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-343: a SAR group of mixed helicopter types orders every deck with the loadout of the TYPE of that deck's helicopters, never the anchor's.
    // `aswtest t343 <name>`.
    internal static class AswT343Tests
    {
        private static readonly string[] Names = { "ka25ts-ka27", "ka27-ka29", "sh2f-ch46", "own-empty", "no-own-loadout", "same-type" };

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static LoadoutOption Lo(string n, int cap, bool avail = true, double ready = 15)
        {
            return new LoadoutOption { Name = n, Display = n, Capacity = cap, ReadyUpMin = ready, CoolDownMin = 15, Available = avail, Weapons = n == "Empty" || n == "AEW" || n == "Transport" ? new Dictionary<string, int>() : new Dictionary<string, int> { { "x", 2 } } };
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
            public int LogCount(string sub) { return F.Logs.Count(l => l.Contains(sub)); }
            public bool Ordered(string prefix) { return F.OrderLog.Exists(o => o.StartsWith(prefix, StringComparison.Ordinal)); }
            public string Dump() { return string.Join(" | ", F.OrderLog.ToArray()) + " || " + string.Join(" | ", F.Logs.Where(l => l.StartsWith("[Sar]", StringComparison.Ordinal)).ToArray()); }

            public string Start(params int[] uids)
            {
                string r = C.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[" + string.Join(",", uids) + "]}"));
                if (AswController.IsErr(r)) return r;
                r = C.SarOrder(P("{\"group\":\"SAR-1\"}"));
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
                    case "ka25ts-ka27": e = Ka25tsKa27(); break;
                    case "ka27-ka29": e = Ka27Ka29(); break;
                    case "sh2f-ch46": e = Sh2fCh46(); break;
                    case "own-empty": e = OwnEmpty(); break;
                    case "no-own-loadout": e = NoOwn(); break;
                    default: e = SameType(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static readonly Func<List<LoadoutOption>> Ka27 = () => new List<LoadoutOption> { Lo("ASW", 10), Lo("Strike", 10), Lo("Empty", 10) };
        private static readonly Func<List<LoadoutOption>> Ka29 = () => new List<LoadoutOption> { Lo("Strike", 16), Lo("CAS", 16), Lo("AntiArmor", 16), Lo("Empty", 16) };

        private static void Rafts(World w) { w.F.SarWorld.AddRaft(11, 6, 3, 2); w.F.SarWorld.AddRaft(12, 7, 3, 2); w.F.SarWorld.AddRaft(13, 8, 3, 2); }

        // the customer's fight: anchor Ka-25Ts (only AEW) + Ka-27 -> "wp_ka-27/AEW -> unknown_loadout"
        private static string Ka25tsKa27()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-25ts"] = new List<LoadoutOption> { Lo("AEW", 10) };
            w.F.LoadoutsByType["wp_ka-27"] = Ka27();
            w.Deck(1, 1, "wp_ka-25ts", "AEW", 10); w.Deck(2, 61, "wp_ka-27", "Empty", 10);
            Rafts(w);
            string err = w.Start(1, 2); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("61:wp_ka-27:Empty:")) return "no Ka-27 Empty order: " + w.Dump();
            if (!w.Ordered("1:wp_ka-25ts:AEW:")) return "no Ka-25Ts AEW order: " + w.Dump();
            if (w.LogCount("unknown_loadout") != 0) return "unknown_loadout: " + w.Dump();
            return null;
        }

        private static string Ka27Ka29()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-27"] = Ka27();
            w.F.LoadoutsByType["wp_ka-29"] = Ka29();
            w.Deck(1, 61, "wp_ka-27", "Empty", 10); w.Deck(2, 62, "wp_ka-29", "Strike", 16);
            Rafts(w);
            string err = w.Start(1, 2); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("62:wp_ka-29:Empty:")) return "no Ka-29 Empty order: " + w.Dump();
            if (!w.Ordered("61:wp_ka-27:Empty:")) return "no Ka-27 Empty order: " + w.Dump();
            if (w.LogCount("unknown_loadout") != 0) return "unknown_loadout: " + w.Dump();
            if (w.LogCount("rearm") != 0) return "rearm: " + w.Dump();
            return null;
        }

        // CH-46 has no Empty at all: its Transport (no weapons) is the loadout
        private static string Sh2fCh46()
        {
            var w = new World();
            w.F.LoadoutsByType["usn_sh-2f"] = new List<LoadoutOption> { Lo("ASW", 2), Lo("Empty", 8) };
            w.F.LoadoutsByType["usn_ch-46"] = new List<LoadoutOption> { Lo("Transport", 22) };
            w.Deck(1, 1, "usn_sh-2f", "Empty", 8); w.Deck(2, 62, "usn_ch-46", "Transport", 22);
            Rafts(w);
            string err = w.Start(1, 2); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("62:usn_ch-46:Transport:")) return "no CH-46 Transport order: " + w.Dump();
            if (!w.Ordered("1:usn_sh-2f:Empty:")) return "no SH-2F Empty order: " + w.Dump();
            if (w.LogCount("unknown_loadout") != 0) return "unknown_loadout: " + w.Dump();
            return null;
        }

        // no valid loadout (the game refuses Strike, Empty is unavailable): the type's own Empty is ordered, noted as why=own_empty
        private static string OwnEmpty()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-29"] = new List<LoadoutOption> { Lo("Strike", 16), Lo("Empty", 16, false) };
            w.F.NoAmmo.Add("Strike");
            w.Deck(1, 62, "wp_ka-29", "Strike", 16);
            Rafts(w);
            string err = w.Start(1); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("62:wp_ka-29:Empty:")) return "no own Empty order: " + w.Dump();
            if (!w.F.Logs.Exists(l => l.StartsWith("[Sar] order SAR-1 deck=62", StringComparison.Ordinal) && l.Contains("wp_ka-29/Empty") && l.Contains("why=own_empty"))) return "no why=own_empty line: " + w.Dump();
            return null;
        }

        // the only loadout of the CH-46 is refused: nothing else to take, the anchor's loadout is never ordered
        private static string NoOwn()
        {
            var w = new World();
            w.F.LoadoutsByType["wp_ka-27"] = Ka27();
            w.F.LoadoutsByType["usn_ch-46"] = new List<LoadoutOption> { Lo("Transport", 22) };
            w.F.NoAmmo.Add("Transport");
            w.Deck(1, 61, "wp_ka-27", "Empty", 10); w.Deck(2, 62, "usn_ch-46", "Transport", 22);
            Rafts(w);
            string err = w.Start(1, 2); if (err != null) return err;
            w.Run(120);
            int ch = w.F.OrderLog.Count(o => o.StartsWith("62:", StringComparison.Ordinal));
            if (ch != 1) return "CH-46 orders " + ch + " (expected the one refused): " + w.Dump();
            if (!w.F.OrderLog.TrueForAll(o => !o.StartsWith("62:usn_ch-46:Empty", StringComparison.Ordinal))) return "anchor loadout ordered: " + w.Dump();
            if (w.LogCount("deck=62 usn_ch-46 -> no_own_loadout") != 1) return "no_own_loadout lines: " + w.LogCount("no_own_loadout") + " " + w.Dump();
            if (!w.Ordered("61:wp_ka-27:Empty:")) return "Ka-27 not ordered: " + w.Dump();
            return null;
        }

        private static string SameType()
        {
            var w = new World();
            w.F.LoadoutList.AddRange(new[] { Lo("ASW", 2), Lo("Empty", 8) });
            w.Deck(1, 9000, "SH-2F", "Empty", 8);
            Rafts(w);
            string err = w.Start(1); if (err != null) return err;
            w.Run(60);
            if (!w.Ordered("9000:SH-2F:Empty:")) return "no SH-2F Empty order: " + w.Dump();
            if (w.LogCount("no_own_loadout") != 0 || w.LogCount("unknown_loadout") != 0) return "noise: " + w.Dump();
            return null;
        }
    }
}
#endif
