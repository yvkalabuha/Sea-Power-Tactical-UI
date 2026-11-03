using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115 (D1): the groups the sub datum card can send: order none | area, usable members, ETA to the core centre. `aswtest t288 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names288 = { "groups", "area-snapshot", "eta" };

        public static string RunT288(string only = null)
        {
            string e;
            foreach (string n in Names288)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "groups": e = Groups288(); break;
                    case "area-snapshot": e = AreaSnapshot288(); break;
                    case "eta": e = Eta288(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names288, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static AswController Make288(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        private static string Groups288()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "deck_ready")); f.Mem.Add(AswFakeGame.Helo(3, "air"));
            var launching = AswFakeGame.Helo(4, "deck_launching"); launching.ReadyInS = 99999; f.Mem.Add(launching);
            var c = Make288(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
            c.Group(P("{\"op\":\"create\",\"uids\":[3]}"));
            c.Group(P("{\"op\":\"create\",\"uids\":[4]}"));
            Run(c, f, 3);
            var gs = c.DatumGroups(35, 20.1);
            if (gs.Count != 3) return "groups " + gs.Count;
            if (gs[0].Order != "none" || gs[0].Usable != 2) return "group 0: " + gs[0].Order + "/" + gs[0].Usable;
            if (gs[1].Order != "none" || gs[1].Usable != 1) return "group 1: " + gs[1].Order + "/" + gs[1].Usable;
            if (gs[2].Usable != 0) return "a member that is launching is not usable: " + gs[2].Usable;
            if (string.IsNullOrEmpty(gs[0].Name) || gs[0].Name == gs[1].Name) return "names";
            if (gs[0].Rect != null) return "a group without an area carries no rect";
            // the choice of the button: none first, the nearer group first
            int pick = SubDatumMath.PickGroup(gs);
            if (pick < 0 || gs[pick].Usable < 1) return "pick " + pick;
            return null;
        }

        private static string AreaSnapshot288()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make288(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            c.Group(P("{\"op\":\"create\",\"uids\":[2]}"));
            string name = c.View().Groups[1].Name;
            string r = c.SearchArea(P("{\"group\":\"" + name + "\",\"rect\":[35.2,20.0,35.2,20.2,35.1,20.2,35.1,20.0]}"));
            if (r == null || r.Contains("\"ok\":false")) return "search area: " + r;
            Run(c, f, 3);
            var gs = c.DatumGroups(35, 20.1);
            if (gs[0].Order != "none") return "group 0 order " + gs[0].Order;
            var a = gs[1];
            if (a.Order != "area" || a.Rect == null || a.Rect.Length != 8 || Math.Abs(a.Rect[0] - 35.2) > 1e-9) return "area snapshot rect";
            if (a.Tactics == null || a.Depth == null) return "area snapshot tactics/depth";
            a.Rect[0] = 0;
            var again = c.DatumGroups(35, 20.1);
            if (Math.Abs(again[1].Rect[0] - 35.2) > 1e-9) return "the snapshot must be a copy";
            // area groups come after none groups
            int pick = SubDatumMath.PickGroup(gs);
            if (pick != 0) return "none before area: " + pick;
            return null;
        }

        private static string Eta288()
        {
            var f = new AswFakeGame();
            var near = AswFakeGame.Helo(1, "air"); near.Lat = 35; near.Lon = 20.1;
            var far = AswFakeGame.Helo(2, "air"); far.Lat = 35; far.Lon = 21.0;
            var deck = AswFakeGame.Helo(3, "deck_ready"); deck.Lat = 0; deck.Lon = 0;
            f.Mem.Add(near); f.Mem.Add(far); f.Mem.Add(deck);
            f.FixPos.Add(1); f.FixPos.Add(2);
            var c = Make288(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            c.Group(P("{\"op\":\"create\",\"uids\":[2]}"));
            c.Group(P("{\"op\":\"create\",\"uids\":[3]}"));
            Run(c, f, 3);
            var gs = c.DatumGroups(35, 20.1);
            if (!(gs[0].EtaS < 60)) return "overhead helo: " + gs[0].EtaS;
            if (!(gs[1].EtaS > gs[0].EtaS)) return "far helo is later: " + gs[1].EtaS + " vs " + gs[0].EtaS;
            if (double.IsInfinity(gs[2].EtaS) || gs[2].EtaS < 120) return "a deck member needs at least the launch time: " + gs[2].EtaS;
            return null;
        }
    }
}
