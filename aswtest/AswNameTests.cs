using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-150 A7: group names by task (ПЛО-N / ПСО-N), rename, auto_name in the save.
    internal static class AswNameTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswFakeGame Game()
        {
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(1, "air", dip: true);
            h.Helicopter = true; h.BestCapacity = h.Capacity = 6;
            f.Mem.Add(h);
            return f;
        }

        private static string Names(AswController c)
        {
            var l = new List<string>();
            foreach (var g in c.View().Groups) l.Add(g.Name);
            return string.Join(",", l.ToArray());
        }

        public static string Run()
        {
            string asw = UiText.Get("asw.group.prefix"), sar = UiText.Get("sar.group.prefix");
            var f = Game();
            var own = new AirOwnership();
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own };
            c.Sar = new SarController(f.SarWorld, new SarParams(), null);
            // a new group is named by the task
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            if (Names(c) != asw + "-1") return "new group name " + Names(c);
            int seq = own.GroupSeq("asw", asw + "-1");
            if (!c.View().Groups[0].AutoName) return "AutoName of a new group";
            // the rescue order renames it, the creation number stays
            string r = c.SarOrder(P("{\"group\":\"" + asw + "-1\"}"));
            if (AswController.IsErr(r)) return "sar order " + r;
            if (Names(c) != sar + "-1") return "after sar_order " + Names(c);
            if (own.GroupSeq("asw", sar + "-1") != seq) return "creation number lost";
            // and back
            r = c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            if (AswController.IsErr(r)) return "course " + r;
            if (Names(c) != asw + "-1") return "after the search order " + Names(c);
            // a typed name is kept
            r = c.Group(P("{\"op\":\"rename\",\"group\":\"" + asw + "-1\",\"name\":\"Конвой\"}"));
            if (AswController.IsErr(r)) return "rename " + r;
            if (Names(c) != "Конвой" || c.View().Groups[0].AutoName) return "rename result " + Names(c);
            if (!f.HasEvent("asw_group_renamed")) return "no rename event";
            r = c.SarOrder(P("{\"group\":\"Конвой\"}"));
            if (AswController.IsErr(r) || Names(c) != "Конвой") return "manual name changed by the order: " + Names(c);
            // a name that is taken / bad
            c.Group(P("{\"op\":\"create\",\"group\":\"Второй\",\"uids\":[]}"));
            if (c.Group(P("{\"op\":\"rename\",\"group\":\"Второй\",\"name\":\"Конвой\"}")) != "!name_taken") return "taken name accepted";
            if (c.Group(P("{\"op\":\"rename\",\"group\":\"Второй\",\"name\":\"  \"}")) != "!bad_name") return "blank name accepted";
            if (c.Group(P("{\"op\":\"rename\",\"group\":\"Нет\",\"name\":\"X\"}")) != "!no_group") return "unknown group";
            // save and load keep the flag; an old file decides from the name
            var d = new AswSaveData();
            d.Groups.Add(new AswGroupDto { Name = "ASW-3", Order = "none" });
            d.Groups.Add(new AswGroupDto { Name = "Конвой", Order = "none" });
            d.Groups.Add(new AswGroupDto { Name = "X-5", Order = "none", AutoName = true });
            string err;
            var back = AswSave.Parse(AswSave.ToJson(d), out err);
            if (back == null) return "parse " + err;
            var f2 = Game();
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aswname-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                d.MissionTimeS = f2.Now;
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AswSave.Key("/s/a.sav") + ".json"), AswSave.ToJson(d));
                f2.SaveDir = dir;
                var c2 = new AswController(f2, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/a.sav", f2.Now);
            var gv = c2.View().Groups;
            if (gv.Count != 3 || !gv[0].AutoName || gv[1].AutoName || !gv[2].AutoName) return "restored AutoName " + gv.Count;
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }
    }
}
