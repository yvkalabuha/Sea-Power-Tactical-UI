using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-334 / ADR-0130: "Corridor" and "Barrier" exclude each other: the commands refuse both (tactics_conflict), a save / old ids give the barrier, no preview for both.
    // `aswtest t334x <name>`.
    internal static class AswT334Excl
    {
        private static readonly string[] Names = { "cmd-course", "cmd-set", "restore", "migrate", "preview" };

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "cmd-course": e = CmdCourse(); break;
                    case "cmd-set": e = CmdSet(); break;
                    case "restore": e = Restore(); break;
                    case "migrate": e = Migrate(); break;
                    default: e = Preview(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }); }
        private static void Step(AswController c, AswFakeGame f, int n) { for (int i = 0; i < n; i++) { f.Advance(1); f.Tick(c); } }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        private static AswFakeGame Two()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: 40));
            f.Mem.Add(AswFakeGame.Helo(2, "air", passive: 40));
            return f;
        }

        private static string TacticOfUid(AswController c, int uid)
        {
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.Uid == uid) return r.Tactic;
            return null;
        }

        private static string CmdCourse()
        {
            var f = Two(); var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            foreach (string t in new[] { "[\"corridor\",\"barrier\"]", "[\"rows\",\"cross\"]", "[\"dip\",\"corridor\",\"barrier\"]", "[\"barrier\",\"corridor\"]" })
            {
                string r = c.SearchCourse(P("{\"tactics\":" + t + "}"));
                if (r != "!tactics_conflict") return t + " -> " + r;
            }
            if (c.SpecOf("G") != null && c.SpecOf("G").Tactics.Count > 0) return "a refused order changed the group";
            foreach (string t in new[] { "[\"corridor\"]", "[\"barrier\"]", "[\"dip\",\"corridor\"]", "[\"dip\",\"barrier\"]", "[]" })
                if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":" + t + "}")))) return "refused a valid " + t;
            return null;
        }

        private static string CmdSet()
        {
            var f = Two(); var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"corridor\"]}"));
            foreach (string t in new[] { "[\"corridor\",\"barrier\"]", "[\"rows\",\"cross\"]", "[\"dip\",\"corridor\",\"barrier\"]" })
            {
                string r = c.SetTactics(P("{\"group\":\"G\",\"tactics\":" + t + "}"));
                if (r != "!tactics_conflict") return t + " -> " + r;
            }
            if (AswPatternMath.ComboOf(c.SpecOf("G").Tactics) != "dip+corridor") return "a refused set changed the tactics: " + AswPatternMath.ComboOf(c.SpecOf("G").Tactics);
            if (AswController.IsErr(c.SetTactics(P("{\"group\":\"G\",\"tactics\":[\"dip\",\"barrier\"]}")))) return "refused dip + barrier";
            if (AswPatternMath.ComboOf(c.SpecOf("G").Tactics) != "dip+barrier") return "set barrier: " + AswPatternMath.ComboOf(c.SpecOf("G").Tactics);
            return null;
        }

        private static string WithFile(Action<AswSaveData> make, Func<AswFakeGame, AswController, string> check)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asw334-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var f = Two();
                var d = new AswSaveData { MissionTimeS = f.Now };
                make(d);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AswSave.Key("/s/a.sav") + ".json"), AswSave.ToJson(d));
                f.SaveDir = dir;
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.OnMissionChanged("/s/a.sav", f.Now);
                return check(f, c);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        private static AswGroupDto GroupDto()
        {
            var g = new AswGroupDto { Name = "G", Order = "course", TargetName = "Yorktown", TargetUid = 500, Params = new AswParams { LeadMadNm = 8, LeadDipNm = 8 } };
            g.Members.Add(new AswMemberRef { Uid = 1, Name = "Helo 1", Type = "SH-3H" }); g.Members.Add(new AswMemberRef { Uid = 2, Name = "Helo 2", Type = "SH-3H" });
            return g;
        }

        private static string Restore()
        {
            return WithFile(d =>
            {
                var g = GroupDto();
                g.Course = new AswCourseSpec();
                g.Course.Tactics.AddRange(new[] { "dip", "corridor", "barrier" });
                d.Groups.Add(g);
            }, (f, c) =>
            {
                var sp = c.SpecOf("G");
                if (sp == null) return "no spec after restore";
                if (AswPatternMath.ComboOf(sp.Tactics) != "dip+barrier") return "restored " + AswPatternMath.ComboOf(sp.Tactics);
                if (!Logged(f, "asw restore G tactics dip,corridor,barrier -> dip+barrier (exclusive)")) return "no restore line: " + string.Join(" | ", f.Logs.Where(l => l.Contains("restore")).ToArray());
                Step(c, f, 5);
                if (TacticOfUid(c, 1) != "dip" || TacticOfUid(c, 2) != "barrier") return "tactics after the restore: " + TacticOfUid(c, 1) + "/" + TacticOfUid(c, 2);
                return null;
            });
        }

        private static string Migrate()
        {
            return WithFile(d =>
            {
                var g = GroupDto();
                g.Tactics.AddRange(new[] { "rows", "cross", "dip" });   // an old file: no "course"
                d.Groups.Add(g);
            }, (f, c) =>
            {
                var sp = c.SpecOf("G");
                if (sp == null) return "no spec after restore";
                if (AswPatternMath.ComboOf(sp.Tactics) != "dip+barrier") return "migrated " + AswPatternMath.ComboOf(sp.Tactics);
                return null;
            });
        }

        private static string Preview()
        {
            var f = Two(); var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.PreviewCmd(P("{\"area\":false,\"tactics\":[\"corridor\",\"barrier\"],\"keep\":true}"));
            if (c.View().Preview.Count != 0) return "a preview of corridor + barrier";
            c.PreviewCmd(P("{\"area\":false,\"tactics\":[\"dip\",\"corridor\"],\"keep\":true}"));
            if (c.View().Preview.Count == 0) return "no preview of dip + corridor";
            c.PreviewCmd(P("{\"area\":false,\"tactics\":[\"rows\",\"cross\"],\"keep\":true}"));
            if (c.View().Preview.Count != 0) return "a preview of rows + cross";
            return null;
        }
    }
}
