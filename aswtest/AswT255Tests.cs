using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-255: after a load two aircraft with one name and changed uids are not matched to one live aircraft twice. `aswtest t255 <name>`.
    internal static class AswT255Tests
    {
        public static readonly string[] Names = { "uid_changed_two_same_name" };

        private static AswFakeGame Fleet(string dir, string last, int uid1, int uid2)
        {
            var f = AswT180Retreat.Helos(2); f.SaveDir = dir; f.LastSavePath = last;
            f.Mem[0].Uid = uid1; f.Mem[1].Uid = uid2;
            foreach (var m in f.Mem) { m.Name = "Ka-27PL"; m.Type = "wp_ka-27"; }
            return f;
        }

        private static AswController Make(AswFakeGame f)
        {
            return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
        }

        private static void Run(AswFakeGame f, AswController c, int n) { for (int i = 0; i < n; i++) { f.Advance(1); f.Tick(c); } }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only = null)
        {
            bool known = only == null;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                known = true;
                string e;
                string dir = Path.Combine(Path.GetTempPath(), "asw255-" + Guid.NewGuid().ToString("N"));
                try { e = UidChanged(dir); }
                finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
                if (e != null) return n + ": " + e;
            }
            return known ? null : "unknown scenario " + only;
        }

        private static string Load(string dir, SavStamp st, bool both, out AswController c2, out AswFakeGame g)
        {
            g = Fleet(dir, "/s/b.sav", 11, 12); g.Now = 2000;
            if (!both) g.Mem.RemoveAt(1);
            c2 = Make(g);
            c2.OnMissionChanged("/s/b.sav", g.Now, st);
            Run(g, c2, 40);
            return null;
        }

        private static string UidChanged(string dir)
        {
            var f = Fleet(dir, "/s/a.sav", 1, 2);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"]}")); Run(f, c, 60);
            var st = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
            c.OnGameSaved("/s/b.sav", st);

            AswController c2; AswFakeGame g;
            Load(dir, st, true, out c2, out g);
            var uids = new List<int>(); foreach (var gv in c2.View().Groups) uids.AddRange(gv.Members);
            uids.Sort();
            if (string.Join(",", uids.Select(u => u.ToString()).ToArray()) != "11,12") return "logs " + string.Join(" / ", g.Logs.ToArray()) + " groups " + c2.View().Groups.Count + " files " + Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length + " members " + string.Join(",", uids.Select(u => u.ToString()).ToArray());
            string ev = g.Events.LastOrDefault(x => x.StartsWith("asw_warn") && x.Contains("restored"));
            if (ev == null || !ev.Contains("\"missing\":0")) return "restored event " + ev;

            Load(dir, st, false, out c2, out g);
            uids.Clear(); foreach (var gv in c2.View().Groups) uids.AddRange(gv.Members);
            if (uids.Count != 1 || uids[0] != 11) return "only 11 live: members " + string.Join(",", uids.Select(u => u.ToString()).ToArray());
            ev = g.Events.LastOrDefault(x => x.StartsWith("asw_warn") && x.Contains("restored"));
            if (ev == null || !ev.Contains("\"missing\":1")) return "only 11 live: restored event " + ev;
            return null;
        }
    }
}
