using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-346 D4: asw_group create|add with kind=asw|sar refuses the aircraft that the ring refuses (the same exclusion source), with the reason in `rejected`. `aswtest t346 <name>`.
    internal static class AswT346Tests
    {
        private static readonly string[] Names = { "kind-asw", "kind-sar", "kind-none", "kind-bad", "same-as-ring" };

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "kind-asw": e = KindAsw(); break;
                    case "kind-sar": e = KindSar(); break;
                    case "kind-none": e = KindNone(); break;
                    case "kind-bad": e = KindBad(); break;
                    default: e = SameAsRing(); break;
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
            public World()
            {
                var ka27 = AswFakeGame.Helo(1, "air"); ka27.Type = "Ka-27"; F.Mem.Add(ka27);                               // ASW loadout: buoys and torpedoes
                var ka29 = Bare(2, "Ka-29"); F.Mem.Add(ka29);                                                              // Strike loadout: nothing to search with
                var ka25 = Bare(3, "Ka-25Ts"); ka25.Roles = new[] { "AEW", "MPA", "SAR" }; F.Mem.Add(ka25);                // AEW loadout
                var player = AswFakeGame.Helo(4, "player"); F.Mem.Add(player);
                var landed = AswFakeGame.Helo(5, "landed"); F.Mem.Add(landed);
                var sar = AswFakeGame.Helo(6, "air"); sar.Capacity = 8; sar.BestCapacity = 8; sar.Torpedoes = 0; sar.Passive = 0; sar.ActiveBuoys = 0; sar.Vlad = 0; F.Mem.Add(sar);   // a SAR machine
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                F.Advance(1); F.Tick(C); C.EnsureLive();
            }
            private static AswMember Bare(int uid, string type)
            {
                var m = AswFakeGame.Helo(uid, "air"); m.Type = type; m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0; m.Dipping = false; m.Mad = false; m.Loadout = "Strike";
                return m;
            }
            public string Cmd(string json) { F.Advance(1); F.Tick(C); C.EnsureLive(); return C.Group(P(json)); }
        }

        private static bool Has(string json, string uid, string reason) { return json.Contains("\"uid\":" + uid + ",\"reason\":\"" + reason + "\""); }

        private static string KindAsw()
        {
            var w = new World();
            string r = w.Cmd("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"kind\":\"asw\",\"uids\":[1,2,3,4,5]}");
            if (AswController.IsErr(r)) return "create " + r;
            if (!r.Contains("\"members\":1")) return "only the Ka-27 joins " + r;
            if (!Has(r, "2", "no_asw_load") || !Has(r, "3", "no_asw_load") && !Has(r, "3", "aew") || !Has(r, "4", "player") || !Has(r, "5", "landed")) return "reasons " + r;
            if (w.C.RingGroupOf(2) != null || w.C.RingGroupOf(1) != "ПЛО-1") return "membership";
            r = w.Cmd("{\"op\":\"add\",\"group\":\"ПЛО-1\",\"kind\":\"asw\",\"uids\":[2]}");
            if (!r.Contains("\"members\":1") || !Has(r, "2", "no_asw_load")) return "add " + r;
            return null;
        }

        private static string KindSar()
        {
            var w = new World();
            string r = w.Cmd("{\"op\":\"create\",\"group\":\"SAR-1\",\"kind\":\"sar\",\"uids\":[1,6,4]}");
            if (AswController.IsErr(r)) return "create " + r;
            if (!r.Contains("\"members\":1") || w.C.RingGroupOf(6) != "SAR-1") return "only the SAR machine joins " + r;
            if (!Has(r, "1", "no_capacity") || !Has(r, "4", "player")) return "sar reasons " + r;
            return null;
        }

        private static string KindNone()
        {
            var w = new World();
            string r = w.Cmd("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"uids\":[1,2,3]}");
            if (AswController.IsErr(r) || !r.Contains("\"members\":3") || r.Contains("rejected")) return "without kind every aircraft joins as before: " + r;
            return null;
        }

        private static string KindBad()
        {
            var w = new World();
            string r = w.Cmd("{\"op\":\"create\",\"group\":\"X\",\"kind\":\"cap\",\"uids\":[1]}");
            if (r != "!bad_args") return "kind=cap " + r;
            return null;
        }

        // the ring and the command with kind=asw give the same verdict for the ASW search group
        private static string SameAsRing()
        {
            foreach (int uid in new[] { 1, 2, 3, 4, 5, 6 })
            {
                var w = new World();
                w.Cmd("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"kind\":\"asw\",\"uids\":[1]}");
                var w2 = new World();
                w2.Cmd("{\"op\":\"create\",\"group\":\"ПЛО-1\",\"kind\":\"asw\",\"uids\":[1]}");
                w.C.EnsureLive();
                string why = w.C.AdmitWhy(uid, "ПЛО-1", null, null);
                w2.Cmd("{\"op\":\"add\",\"group\":\"ПЛО-1\",\"kind\":\"asw\",\"uids\":[" + uid + "]}");
                bool member = w2.C.RingGroupOf(uid) == "ПЛО-1";
                if ((why == null) != member) return "uid " + uid + ": ring " + (why ?? "ok") + " but member=" + member;
            }
            return null;
        }
    }
}
