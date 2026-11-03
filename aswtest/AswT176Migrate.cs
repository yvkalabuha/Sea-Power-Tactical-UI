using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 C1 / C2 (v3-migrate): old ids and old saves read as v3 orders (ADR-0076 s.9).
    internal static partial class AswT176Tests
    {
        private static AswFakeGame Two(int passive = 40)
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: passive));
            f.Mem.Add(AswFakeGame.Helo(2, "air", passive: passive));
            return f;
        }

        private static string Migrate()
        {
            // commands with old ids: rows -> corridor (L 20 from lead 8), cross -> barrier (X from width_nm, else 40, L 40), dip -> dip (L 20)
            var f = Two(); var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            // T-334 / ADR-0130: rows + cross is the corridor and the barrier together: refused
            if (!AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\",\"cross\",\"dip\"],\"lead_nm\":8}")))) return "rows + cross was not refused";
            string r = c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"],\"lead_nm\":8}"));
            if (AswController.IsErr(r)) return "old ids refused " + r;
            var sp = c.SpecOf("G");
            if (ComboOf(sp) != "dip+corridor" || sp.CorridorLNm != 20 || sp.CorridorXNm != 20 || sp.BarrierXNm != 40 || sp.BarrierLNm != 40 || sp.DipLNm != 20) return "old spec " + Dump(sp);
            c.SearchCourse(P("{\"tactics\":[\"cross\"],\"width_nm\":33}"));
            if (c.SpecOf("G").BarrierXNm != 30) return "cross width 33 -> " + c.SpecOf("G").BarrierXNm;
            // v3 fields
            c.SearchCourse(P("{\"tactics\":[\"corridor\",\"dip\"],\"corridor_x_nm\":30,\"standoff\":{\"dip\":40,\"corridor\":60},\"depth\":\"below\"}"));
            sp = c.SpecOf("G");
            if (ComboOf(sp) != "dip+corridor" || sp.CorridorXNm != 30 || sp.DipLNm != 40 || sp.CorridorLNm != 60 || sp.Depth != "below") return "v3 spec " + Dump(sp);
            // nothing given: the defaults of [Asw]; auto = dip + corridor
            c.SearchCourse(P("{}"));
            sp = c.SpecOf("G");
            if (sp.Tactics.Count != 0 || sp.CorridorXNm != 20 || sp.BarrierXNm != 40 || sp.BarrierLNm != 40) return "default spec " + Dump(sp);
            // mad by course: with buoys -> corridor, without any -> removed with a warning
            c.SearchCourse(P("{\"tactics\":[\"mad\",\"dip\"]}"));
            if (ComboOf(c.SpecOf("G")) != "dip+corridor" || f.HasEvent("asw_warn", "tactic_removed")) return "mad with buoys";
            var f2 = Two(0); foreach (var m in f2.Mem) { m.ActiveBuoys = 0; m.Vlad = 0; }
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c2.SearchCourse(P("{\"tactics\":[\"mad\",\"dip\"]}"));
            if (ComboOf(c2.SpecOf("G")) != "dip" || !f2.HasEvent("asw_warn", "tactic_removed")) return "mad without buoys: " + ComboOf(c2.SpecOf("G"));
            // area: pattern and old tactics
            var f3 = Two(); var c3 = Make(f3);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c3.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"pattern\":\"mad\"}"));
            if (c3.AreaPatternOf("G") != "mad") return "area pattern " + c3.AreaPatternOf("G");
            c3.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"tactics\":[\"rows\"]}"));
            if (c3.AreaPatternOf("G") != "grid") return "area rows -> " + c3.AreaPatternOf("G");
            c3.SearchArea(P("{\"bearing\":0,\"dist_nm\":20}"));
            if (c3.AreaPatternOf("G") != "auto") return "area auto -> " + c3.AreaPatternOf("G");
            string e = SaveMigrate();
            return e;
        }

        private static string ComboOf(AswCourseSpec s) { return AswPatternMath.ComboOf(s.Tactics); }

        private static string Dump(AswCourseSpec s)
        {
            return AswPatternMath.ComboOf(s.Tactics) + " cx=" + s.CorridorXNm + " bx=" + s.BarrierXNm + " dl=" + s.DipLNm + " cl=" + s.CorridorLNm + " bl=" + s.BarrierLNm + " depth=" + s.Depth;
        }

        // an old file (no "course" / "area_pattern") is migrated at load; a new file keeps its spec
        private static string SaveMigrate()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asw176-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var f = Two(0); foreach (var m in f.Mem) { m.ActiveBuoys = 0; m.Vlad = 0; }
                var d = new AswSaveData { MissionTimeS = f.Now };
                var g = new AswGroupDto { Name = "G", Order = "course", TargetName = "Yorktown", TargetUid = 500, Params = new AswParams { LeadMadNm = 8, LeadDipNm = 8 } };
                g.Tactics.AddRange(new[] { "rows", "cross", "mad", "dip" });
                g.Members.Add(new AswMemberRef { Uid = 1, Name = "Helo 1", Type = "SH-3H" }); g.Members.Add(new AswMemberRef { Uid = 2, Name = "Helo 2", Type = "SH-3H" });
                d.Groups.Add(g);
                var ga = new AswGroupDto { Name = "A", Order = "area", Rect = new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 } };
                ga.Tactics.Add("rows");
                d.Groups.Add(ga);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AswSave.Key("/s/a.sav") + ".json"), AswSave.ToJson(d));
                f.SaveDir = dir;
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.OnMissionChanged("/s/a.sav", f.Now);
                var sp = c.SpecOf("G");
                if (sp == null) return "no spec after restore";
                // the members have no buoys: the old mad snake has nothing to lay -> removed
                if (ComboOf(sp) != "dip+barrier" || sp.CorridorLNm != 20 || sp.BarrierXNm != 40 || sp.DipLNm != 20) return "restored spec " + Dump(sp);   // T-334: rows + cross + dip -> dip + barrier
                if (c.AreaPatternOf("A") != "grid") return "restored area pattern " + c.AreaPatternOf("A");
                if (!f.HasEvent("asw_warn", "tactic_removed")) return "no tactic_removed on restore";
                // a new file keeps its spec
                var snap = c.SpecOf("G");
                snap.CorridorXNm = 50;
                c.Tick(f.Now + 1);
                string sav = "/s/b.sav";
                f.LastSavePath = sav; c.Tick(f.Now + 2); c.Tick(f.Now + 3);
                string err;
                var raw = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, AswSave.Key(sav) + ".json"));
                var back = AswSave.Parse(raw, out err);
                if (back == null || back.Groups[0].Course == null || back.Groups[0].Course.CorridorXNm != 50) return "new file spec " + err;
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }
    }
}
