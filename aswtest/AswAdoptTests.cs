using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-142 P0: launched deck aircraft are adopted by their group; groups never disappear on their own.
    internal static class AswAdoptTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswController Make(AswFakeGame f)
        {
            return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
        }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        private static AswMember Deck(int uid, int deckUid, string key)
        {
            var m = AswFakeGame.Helo(uid, "deck_ready");
            m.Type = "usn_sh-3h"; m.TypeLabel = "SH-3H Sea King"; m.DeckKey = key; m.DeckUid = deckUid; m.Dipping = true;
            return m;
        }

        private static bool Logged(AswFakeGame f, string sub)
        {
            foreach (var l in f.Logs) if (l.Contains(sub)) return true;
            return false;
        }

        private static string Bad(AswFakeGame f)
        {
            foreach (var l in f.Logs) if (l.Contains("tick error")) return l;
            return null;
        }

        public static string Run()
        {
            string e;
            if ((e = Customer(false, null)) != null) return "customer: " + e;
            if ((e = Customer(true, "Sea King HAS.1")) != null) return "unit with old class type: " + e;
            if ((e = TwoDecks()) != null) return "two decks: " + e;
            if ((e = Pending()) != null) return "pending: " + e;
            if ((e = Empty()) != null) return "empty: " + e;
            if ((e = Label()) != null) return "label: " + e;
            if ((e = AreaReplace()) != null) return "area replaces route: " + e;
            return null;
        }

        private static int AirMembers(AswController c)
        {
            int n = 0;
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.State == "air") n++;
            return n;
        }

        // two task: deck rows -> allow -> (unit: row ->) air with a new uid
        private static string Customer(bool viaUnit, string airType)
        {
            var f = new AswFakeGame { LaunchViaUnit = viaUnit, AirType = airType, AirLabel = "Sea King HAS.1" };
            f.Mem.Add(Deck(-2832, 77, "task:a")); f.Mem.Add(Deck(-1735952917, 77, "task:b"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[-2832,-1735952917]}"));
            if (c.View().Groups[0].Members.Count != 2) return "group size";
            if (!c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}")).StartsWith("{")) return "order";
            Run(c, f, 120);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "group_removed_empty") || Logged(f, "group removed")) return "group removed";
            if (c.View().Groups.Count != 1) return "groups " + c.View().Groups.Count;
            var g = c.View().Groups[0];
            if (g.Members.Count != 2) return "members " + g.Members.Count;
            foreach (int u in g.Members) if (u < 0) return "placeholder uid kept " + u;
            if (AirMembers(c) != 2) return "air members " + AirMembers(c);
            if (viaUnit && !Logged(f, "via=unit")) return "no via=unit log";
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            return null;
        }

        // no unit: row: every aircraft takes the closest air unit of its own deck
        private static string TwoDecks()
        {
            var f = new AswFakeGame();
            f.Decks[77] = new AswTarget { Uid = 77, Lat = 35, Lon = 20 };
            f.Decks[78] = new AswTarget { Uid = 78, Lat = 36, Lon = 21 };
            f.Mem.Add(Deck(-1, 77, "task:a")); f.Mem.Add(Deck(-2, 78, "task:b"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[-1,-2]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            f.Advance(1); f.Tick(c);
            if (f.Allows.Count != 2) return "allows " + f.Allows.Count;
            // spawn order is the reverse of the decks: 7000 is near deck 78, 7001 near deck 77
            foreach (var m in f.Mem)
            {
                if (m.Uid == 7000) { m.Lat = 36; m.Lon = 21; }
                else if (m.Uid == 7001) { m.Lat = 35; m.Lon = 20; }
                f.FixPos.Add(m.Uid);
            }
            Run(c, f, 5);
            if (!Logged(f, "uid=-1 -> 7001 via=type")) return "deck 77 -> " + string.Join(" | ", f.Logs.ToArray());
            if (!Logged(f, "uid=-2 -> 7000 via=type")) return "deck 78 not adopted";
            return null;
        }

        // the member stays while the adoption is pending; after the timeout it is lost
        private static string Pending()
        {
            var f = new AswFakeGame { GhostLaunch = true };
            f.Mem.Add(Deck(-1, 77, "task:a"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[-1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 30);
            if (Bad(f) != null) return Bad(f);
            if (!c.View().Groups[0].Members.Contains(-1)) return "member removed while adopting";
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost too early";
            Run(c, f, 290);
            if (!f.HasEvent("asw_warn", "member_lost")) return "no member_lost after expiry";
            if (!Logged(f, "asw adopt expired")) return "no expired log";
            if (c.View().Groups.Count != 1) return "group removed";
            return null;
        }

        private static string Empty()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"E\",\"uids\":[]}"));
            c.Group(P("{\"op\":\"create\",\"group\":\"L\",\"uids\":[1]}"));
            Run(c, f, 600);
            if (c.View().Groups.Count != 2) return "groups " + c.View().Groups.Count;
            foreach (var g in c.View().Groups) if (g.Name == "E" && g.Status != "empty") return "status " + g.Status;
            f.Mem.RemoveAll(m => m.Uid == 1);
            Run(c, f, 600);
            if (c.View().Groups.Count != 2) return "groups after loss " + c.View().Groups.Count;
            foreach (var g in c.View().Groups) if (g.Status != "empty") return g.Name + " status " + g.Status;
            c.Group(P("{\"op\":\"disband\",\"group\":\"E\"}"));
            if (c.View().Groups.Count != 1) return "disband";
            return null;
        }

        private static string Label()
        {
            var f = new AswFakeGame();
            f.Mem.Add(Deck(-1, 77, "task:a"));
            var c = Make(f);
            Run(c, f, 2);
            foreach (var r in c.View().Aircraft) if (r.Uid == -1) return r.Type == "SH-3H Sea King" ? null : "type " + r.Type;
            return "no row";
        }

        // T-142 (customer, 0.19.0): a rect order after a bearing/distance order must not leave the first order's waypoints queued
        private static string AreaReplace()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[1]}"));
            c.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"tactics\":[\"cross\"]}"));
            Run(c, f, 10);
            if (!f.Route.ContainsKey(1) || f.Route[1].Count == 0) return "first order queued nothing";
            var ctr = new GeoPt(f.Tgt.Lat + 0.05, f.Tgt.Lon + 0.05);
            double[] rect = AswController.RectAround(ctr, 6, 3);
            var sb = new System.Text.StringBuilder("{\"rect\":[");
            for (int i = 0; i < rect.Length; i++) { if (i > 0) sb.Append(','); sb.Append(rect[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
            string r = c.SearchArea(P(sb.Append("],\"tactics\":[\"rows\",\"dip\"]}").ToString()));
            if (AswController.IsErr(r)) return r;
            if (!f.Calls.Contains("clear:1")) return "route not cleared";
            Run(c, f, 30);
            if (Bad(f) != null) return Bad(f);
            List<GeoPt> route;
            if (!f.Route.TryGetValue(1, out route) || route.Count == 0) return "second order queued nothing";
            foreach (var p in route)
            {
                var l = AswMath.ToLocal(ctr, 0, p);
                if (Math.Abs(l.X) > 1.5 + 1 || Math.Abs(l.Y) > 3 + 1) return "waypoint outside the rect: x=" + l.X.ToString("F1") + " y=" + l.Y.ToString("F1");
            }
            return null;
        }
    }
}
