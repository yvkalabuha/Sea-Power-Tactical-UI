using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-142 K: the target of a "by course" order (ship / formation), the replan limit (degrees + interval), the loadout per vehicle type.
    internal static class AswT142Tests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }
        // the fake target builds a track from its own motion; the replan cases steer by the heading only (a ship that turns at once)
        private static void RunHeading(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tgt.Track.Clear(); f.Tick(c); } }
        private static int Replans(AswFakeGame f) { int n = 0; foreach (var l in f.Logs) if (l.Contains(" reanchor reason=")) n++; return n; }   // T-176: the anchor of the pattern

        private sealed class StubDeck : IAirDeck
        {
            public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { }
            public int Submit(DeckRequest r, Action<DeckResult> done) { if (done != null) done(new DeckResult { Ticket = 1, Err = "stub" }); return 1; }
            public double HeldS(string window, string group) { return -1; }
            public bool DeckDown(int d) { return false; }
        }

        public static string Run()
        {
            string e;
            if ((e = Target()) != null) return "target: " + e;
            if ((e = Replan()) != null) return "replan: " + e;
            if ((e = Loadout()) != null) return "loadout: " + e;
            if ((e = SaveKind()) != null) return "save kind: " + e;
            return null;
        }

        private static string Target()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var other = new AswTarget { Uid = 600, Name = "Other", Lat = 35.2, Lon = 20, SpeedKn = 10, CourseDeg = 0, WidthNm = 4 };
            f.Decks[600] = other;
            f.TargetList.Add(new AswTargetOption { Uid = 500, Name = "Convoy", Kind = "formation", WidthNm = 9, Selected = true });
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            f.Selected = other;   // the player selects another ship afterwards
            Run(c, f, 20);
            var gv = c.View().Groups[0];
            if (gv.TargetUid != 500 || gv.TargetKind != "ship") return "target kept " + gv.TargetUid + "/" + gv.TargetKind;
            if (c.View().Groups[0].CourseTargets.Count != 1 || c.View().CourseTargets.Count != 1) return "course targets in the view";
            // a formation: the width is the formation's own unless the player gives one
            if (AswController.IsErr(c.SearchCourse(P("{\"group\":\"G\",\"target_uid\":500,\"target_kind\":\"formation\",\"tactics\":[\"cross\"]}")))) return "formation order";
            if (c.View().Groups[0].TargetKind != "formation" || c.ParamsOf("G").WidthNm != 9) return "formation width " + c.ParamsOf("G").WidthNm;
            if (AswController.IsErr(c.SearchCourse(P("{\"group\":\"G\",\"target_uid\":500,\"target_kind\":\"formation\",\"width_nm\":6}")))) return "formation order 2";
            if (c.ParamsOf("G").WidthNm != 6) return "given width wins " + c.ParamsOf("G").WidthNm;
            return null;
        }

        // T-176 (ADR-0076 s.5): 20 degrees never moves the anchor; 40 degrees held for 60 s does (once); the axis of the plan changes only then; 10 degrees never
        private static string Replan()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: false));
            f.Tgt.SpeedKn = 15; f.Tgt.CourseDeg = 90;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            RunHeading(c, f, 40);
            int base0 = Replans(f);
            if (Math.Abs(c.View().Groups[0].AxisDeg - 90) > 1) return "start axis " + c.View().Groups[0].AxisDeg;
            f.Tgt.CourseDeg = 110;
            RunHeading(c, f, 120);
            if (Replans(f) != base0) return "20 degrees: re-anchors " + (Replans(f) - base0);
            if (Math.Abs(c.View().Groups[0].AxisDeg - 90) > 1) return "axis moved without a re-anchor " + c.View().Groups[0].AxisDeg;
            f.Tgt.CourseDeg = 130;
            RunHeading(c, f, 50);
            if (Replans(f) != base0) return "40 degrees re-anchored before the 60 s hold";
            RunHeading(c, f, 20);
            if (Replans(f) != base0 + 1) return "40 degrees held 60 s: re-anchors " + (Replans(f) - base0);
            if (Math.Abs(c.View().Groups[0].AxisDeg - 130) > 1) return "axis after the re-anchor " + c.View().Groups[0].AxisDeg;
            f.Tgt.CourseDeg = 140;
            RunHeading(c, f, 120);
            if (Replans(f) != base0 + 1) return "10 degrees re-anchored";
            return null;
        }

        private static string Loadout()
        {
            var f = new AswFakeGame();
            var m = AswFakeGame.Helo(-12, "deck_ready"); m.DeckKey = "hangar:SH-3H"; m.DeckUid = 77; m.Dipping = true;
            f.Mem.Add(m);
            f.LoadoutList.Add(new LoadoutOption { Name = "ASWHunter", Display = "ASWHunter", Capacity = 2, Available = true });
            f.LoadoutList.Add(new LoadoutOption { Name = "Transport", Display = "Transport", Capacity = 8, Available = true });
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = new AirOwnership(), Deck = new StubDeck() };
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[-12]}"));
            string type = m.Type;
            if (AswController.IsErr(c.Loadout(P("{\"group\":\"G\",\"type\":\"" + type + "\",\"loadout\":\"ASWKiller\"}")))) return "loadout cmd";
            if (!AswController.IsErr(c.Loadout(P("{\"group\":\"G\",\"type\":\"" + type + "\"}")))) return "bad loadout cmd accepted";
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            f.Advance(1); c.Tick(f.Now);
            string last = f.ResolveLog.Count == 0 ? null : f.ResolveLog[f.ResolveLog.Count - 1];
            if (last == null || !last.EndsWith(":" + type + ":ASWKiller", StringComparison.Ordinal)) return "chosen loadout not resolved: " + last;
            var row = c.View().Groups[0].LoadoutRows;
            if (row.Count != 1 || row[0].Chosen != "ASWKiller" || row[0].Names.Count != 1 || row[0].Names[0] != "ASWHunter") return "loadout rows " + row.Count + (row.Count > 0 ? row[0].Chosen + "/" + string.Join(",", row[0].Names) : "");
            // auto: the configured list
            f.ResolveLog.Clear();
            c.Loadout(P("{\"group\":\"G\",\"type\":\"" + type + "\",\"loadout\":\"auto\"}"));
            f.Advance(200); c.Tick(f.Now);
            last = f.ResolveLog.Count == 0 ? null : f.ResolveLog[f.ResolveLog.Count - 1];
            if (last == null || !last.EndsWith(":" + type + ":ASWHunter,ASW,ASWKiller", StringComparison.Ordinal)) return "auto not the configured list: " + last;
            // no ammunition for the chosen one: the configured list takes over and the window is told
            f.ResolveLog.Clear(); f.ResolveError = "no_ammo";
            c.Loadout(P("{\"group\":\"G\",\"type\":\"" + type + "\",\"loadout\":\"ASWKiller\"}"));
            f.Advance(200); c.Tick(f.Now);
            if (f.ResolveLog.Count != 2 || !f.ResolveLog[1].EndsWith("ASWHunter,ASW,ASWKiller", StringComparison.Ordinal)) return "no fallback call: " + f.ResolveLog.Count;
            if (!f.HasEvent("asw_warn", "loadout_fallback")) return "no loadout_fallback warning";
            return null;
        }

        // review: target kind and loadouts go through the controller's save file and come back after loading
        private static string SaveKind()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aswtest-t142-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                f.Mem.Add(AswFakeGame.Helo(1, "air"));
                f.TargetList.Add(new AswTargetOption { Uid = 500, Name = "Convoy", Kind = "formation", WidthNm = 9 });
                var c = Make(f);
                c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                string type = f.Mem[0].Type;
                if (AswController.IsErr(c.SearchCourse(P("{\"group\":\"G\",\"target_uid\":500,\"target_kind\":\"formation\",\"tactics\":[\"rows\"]}")))) return "order";
                if (AswController.IsErr(c.Loadout(P("{\"group\":\"G\",\"type\":\"" + type + "\",\"loadout\":\"ASWKiller\"}")))) return "loadout";
                Run(c, f, 20);
                f.LastSavePath = "/s/B.sav";
                Run(c, f, 1);
                var g = new AswFakeGame { SaveDir = dir, Now = f.Now };
                var x = AswFakeGame.Helo(11, "air"); x.Name = f.Mem[0].Name;
                g.Mem.Add(x);
                g.TargetList.Add(new AswTargetOption { Uid = 500, Name = "Convoy", Kind = "formation", WidthNm = 9 });
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", f.Now + 2);
                var v = c2.View();
                if (v.Groups.Count != 1) return "not restored " + v.Groups.Count;
                if (v.Groups[0].TargetKind != "formation" || v.Groups[0].TargetUid != 500) return "target " + v.Groups[0].TargetKind + "/" + v.Groups[0].TargetUid;
                if (v.Groups[0].LoadoutRows.Count != 1 || v.Groups[0].LoadoutRows[0].Chosen != "ASWKiller") return "loadout rows after load";
                return null;
            }
            finally { try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
        }
    }
}
