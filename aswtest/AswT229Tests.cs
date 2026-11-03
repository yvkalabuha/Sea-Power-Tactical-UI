using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-229 / ADR-0094: the sliding barrier. N rows (default 2) every I miles (default 5) ahead of the ship, a new row when the ship reaches the nearest one,
    // a reset of the band on a steady turn, buoys live on, N and I and the band in the save. Scenarios run through AswT176Tests.RunT229.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names229 =
        {
            "band-start", "slide", "turn-reset", "zigzag", "drift-keeps", "two-aircraft", "short-buoys", "late", "trim-row", "save-load", "view",
        };

        public static string RunT229(string only = null)
        {
            string e;
            foreach (string n in Names229)
            {
                if (only != null && only != n) continue;
                if ((e = Scenario229(n)) != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names229, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static string Scenario229(string name)
        {
            switch (name)
            {
                case "band-start": return BandStart();
                case "slide": return Slide();
                case "turn-reset": return TurnReset();
                case "zigzag": return Zigzag();
                case "drift-keeps": return DriftKeeps();
                case "two-aircraft": return TwoAircraft();
                case "short-buoys": return ShortBuoys();
                case "late": return Late();
                case "trim-row": return TrimRow();
                case "save-load": return SaveLoad();
                case "view": return View229();
                default: return null;
            }
        }

        private const string Barrier229 = "{\"tactics\":[\"barrier\"]}";

        private static double AvgX(AswFakeGame f, DropCall d) { return Local(f, d).Average(q => q.X); }

        private static string BandStart()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            if (c == null) return "order refused";
            var spec = c.SpecOf("G");
            if (spec.BarrierRows != 2 || spec.BarrierGapNm != 5) return "defaults " + spec.BarrierRows + "/" + spec.BarrierGapNm;
            Run(c, f, 1);
            if (f.Drops.Count != 1 || f.Drops[0].Pts.Count != 10) return "first package " + f.Drops.Count + "/" + (f.Drops.Count > 0 ? f.Drops[0].Pts.Count : 0);
            // the nearest row first (Q-T229-3): x = I = 5 from the ship, 10 buoys, edges +-17.84, step 3.96
            var loc = Local(f, f.Drops[0]);
            foreach (var q in loc) if (!Near(q.X, 5, 0.1)) return "a buoy of row 0 at x=" + q.X;
            var ys = loc.Select(q => q.Y).OrderBy(y => y).ToList();
            if (!Near(ys[0], -17.84, 0.05) || !Near(ys[9], 17.84, 0.05)) return "row 0 edges " + ys[0] + " .. " + ys[9];
            for (int i = 1; i < ys.Count; i++) if (!Near(ys[i] - ys[i - 1], 3.96, 0.05)) return "row 0 step " + (ys[i] - ys[i - 1]);
            Run(c, f, 3);   // the aircraft is free again: the next row
            if (f.Drops.Count != 2 || f.Drops[1].Pts.Count != 9) return "second package " + f.Drops.Count + "/" + (f.Drops.Count > 1 ? f.Drops[1].Pts.Count : 0);
            // row 1 at 2I = 10 nm, staggered: 9 buoys on the midpoints of row 0, inside +-X/2
            var loc1 = Local(f, f.Drops[1]);
            foreach (var q in loc1) if (!Near(q.X, 10, 0.1)) return "a buoy of row 1 at x=" + q.X;
            var ys1 = loc1.Select(q => q.Y).OrderBy(y => y).ToList();
            if (!Near(ys1[0], -15.86, 0.05) || !Near(ys1[8], 15.86, 0.05)) return "row 1 edges " + ys1[0] + " .. " + ys1[8];
            for (int i = 0; i < 9; i++) if (!Near(ys1[i], (ys[i] + ys[i + 1]) / 2, 0.05)) return "row 1 is not the midpoints at " + i;
            if (c.BarrierLines("G") != 2) return "rows ahead " + c.BarrierLines("G");
            if (c.BarrierRowsInfo("G") != "0:5:0/10:0;1:10:0/9:0") return "info " + c.BarrierRowsInfo("G");
            if (f.Drops.Any(d => d.Ammo == "usn_ssq-62")) return "an active buoy was dropped";
            // N = 4, I = 8: the rows stand at 8, 16, 24, 32
            var f4 = CorridorGame(400);
            var c4 = StartCourse(f4, "{\"tactics\":[\"barrier\"],\"barrier_rows\":4,\"barrier_gap_nm\":8}");
            Run(c4, f4, 80);
            if (c4.BarrierLines("G") != 4 || c4.BarrierRowsInfo("G") != "0:8:0/10:0;1:16:0/9:0;2:24:0/10:0;3:32:0/9:0") return "4 rows / 8 nm: " + c4.BarrierRowsInfo("G");
            return null;
        }

        private static string Slide()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 1190);   // 19 min 50 s at 15 kn: 4.96 nm, row 0 is not reached yet
            if (f.Drops.Count != 2) return "packages before the first row is reached: " + f.Drops.Count;
            Run(c, f, 70);   // 21 min
            if (f.Drops.Count != 3) return "no package for the new row after the ship reached x=5 (" + f.Drops.Count + ")";
            if (!Near(AvgX(f, f.Drops[2]), 15, 0.3) || f.Drops[2].Pts.Count != 10) return "row 2 at x=" + AvgX(f, f.Drops[2]) + " buoys " + f.Drops[2].Pts.Count;
            if (c.BarrierLines("G") != 2) return "rows ahead " + c.BarrierLines("G");
            if (!Logged(f, "barrier row seq=0 passed")) return "row 0 not marked passed";
            // 3 h: exactly 2 rows ahead, the passed rows are forgotten, the odd rows have 9 buoys
            Run(c, f, 3 * 3600 - 1260);
            if (c.BarrierLines("G") != 2) return "after 3 h rows ahead " + c.BarrierLines("G");
            var info = c.BarrierRowsInfo("G").Split(';');
            if (info.Length > 3) return "passed rows are not forgotten: " + c.BarrierRowsInfo("G");
            double shipAlong = AswMath.ToLocal(new GeoPt(35, 20), 90, new GeoPt(f.Tgt.Lat, f.Tgt.Lon)).X;
            if (shipAlong < 44) return "the ship went " + shipAlong;
            foreach (var d in f.Drops)
            {
                int seq = (int)Math.Round(AvgX(f, d) / 5.0) - 1;
                if (d.Pts.Count != ((seq & 1) == 0 ? 10 : 9)) return "row seq " + seq + " has " + d.Pts.Count + " buoys";
            }
            // one row per I miles of the way: 45 nm = 9 rows passed + the rows ahead
            if (f.Drops.Count < 10 || f.Drops.Count > 12) return "rows laid in 3 h: " + f.Drops.Count;
            return null;
        }

        private static string TurnReset()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            if (c.BarrierRowsInfo("G") != "0:5:0/10:0;1:10:0/9:0") return "start " + c.BarrierRowsInfo("G");
            int buoysBefore = c.GroupBuoys("G"), dropsBefore = f.Drops.Count;
            f.Tgt.CourseDeg = 150;   // a 60 degree turn: the track axis follows it within ~1.5 min, then 60 s must pass
            int seen = -1, reset = -1;
            GeoPt atReset = new GeoPt(0, 0);
            for (int i = 0; i < 600 && reset < 0; i++)
            {
                f.Advance(1); f.Tick(c);
                if (seen < 0 && AxisLogged(f) >= 119) seen = i;
                if (reset < 0 && Logged(f, "barrier reset reason=turn")) { reset = i; atReset = new GeoPt(f.Tgt.Lat, f.Tgt.Lon); }
            }
            if (reset < 0) return "no reset after a steady 60 degree turn";
            if (seen < 0 || reset - seen < 45 || reset - seen > 80) return "the reset came " + (reset - seen) + " s after the axis was 30 degrees off (60 expected)";
            if (!f.Calls.Contains("clear:1")) return "the barrier route was not cancelled";
            // the buoys of the old rows stay in the list (they live on)
            if (c.GroupBuoys("G") < buoysBefore) return "buoys dropped from the list " + buoysBefore + " -> " + c.GroupBuoys("G");
            Run(c, f, 20);
            if (c.BarrierLines("G") != 2) return "rows ahead after the reset " + c.BarrierLines("G");
            if (c.BarrierRowsInfo("G").Substring(0, 4) != "0:5:") return "the new band does not start at 5: " + c.BarrierRowsInfo("G");
            if (f.Drops.Count <= dropsBefore) return "no package for the new rows";
            // the new rows stand on the new heading, 5 and 10 nm from the ship at the reset; all the points are offered (planned=10); only the merge gap of ADR-0063 (a live buoy practically on the point) thins them
            var first = f.Drops[dropsBefore];
            var loc = first.Pts.Select(p => AswMath.ToLocal(atReset, 150, p)).ToList();
            if (!Near(loc.Average(q => q.X), 5, 0.6)) return "new row 0 at x=" + loc.Average(q => q.X);
            if (!Logged(f, "planned=10")) return "the new row was cut by the old buoys before the filter";
            return null;
        }

        private static double AxisLogged(AswFakeGame f)
        {
            double best = -1;
            foreach (var l in f.Logs)
            {
                int i = l.IndexOf("asw frame G axis=", StringComparison.Ordinal);
                if (i < 0) continue;
                double a; string num = new string(l.Substring(i + 17).TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
                if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a)) best = a;
            }
            return best;
        }

        private static string Zigzag()
        {
            // 25 degrees every 5 min (below the 30 degree threshold) and 60 degree legs of 40 s (shorter than the 60 s of holding): the band stays
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 30);
            for (int k = 0; k < 6; k++) { f.Tgt.CourseDeg = (k & 1) == 0 ? 115 : 65; Run(c, f, 300); }
            if (Logged(f, "barrier reset")) return "reset on a 25 degree zigzag";
            f.Tgt.CourseDeg = 90; Run(c, f, 400);
            for (int k = 0; k < 12; k++) { f.Tgt.CourseDeg = (k & 1) == 0 ? 150 : 90; Run(c, f, 40); }
            if (Logged(f, "barrier reset")) return "reset on 60 degree legs of 40 s";
            return null;
        }

        private static string DriftKeeps()
        {
            // a speed change of the ship re-anchors the corridor / dip frame (T-142 K) but the rows stand where they are
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            string rows = c.BarrierRowsInfo("G");
            int clears = f.Calls.Count(x => x == "clear:1");
            f.Tgt.SpeedKn = 8;
            Run(c, f, 400);
            if (!Logged(f, "reanchor reason=speed")) return "the anchor did not re-anchor on the speed change";
            if (Logged(f, "barrier reset")) return "the band was reset by a speed change";
            if (c.BarrierRowsInfo("G") != rows) return "rows changed " + rows + " -> " + c.BarrierRowsInfo("G");
            if (f.Calls.Count(x => x == "clear:1") != clears) return "a barrier route was cancelled by the re-anchor";
            return null;
        }

        private static string TwoAircraft()
        {
            var f = CorridorGame(40, 40);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 3);
            var a = f.Drops.Where(d => d.Uid == 1).ToList(); var b = f.Drops.Where(d => d.Uid == 2).ToList();
            if (a.Count != 1 || b.Count != 1) return "packages " + a.Count + "/" + b.Count;
            if (!Near(AvgX(f, a[0]), 5, 0.1) || a[0].Pts.Count != 10) return "the first aircraft: the nearest row first (x=" + AvgX(f, a[0]) + ", " + a[0].Pts.Count + ")";
            if (!Near(AvgX(f, b[0]), 10, 0.1) || b[0].Pts.Count != 9) return "the second aircraft takes the next row (x=" + AvgX(f, b[0]) + ", " + b[0].Pts.Count + ")";
            return null;
        }

        private static string ShortBuoys()
        {
            var f = CorridorGame(12);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 1);
            if (f.Drops.Count != 1 || f.Drops[0].Pts.Count != 10 || !Near(AvgX(f, f.Drops[0]), 5, 0.1)) return "the nearest row is laid whole first";
            if (!f.HasEvent("asw_warn", "barrier_rows_short")) return "no barrier_rows_short";
            if (f.HasEvent("asw_warn", "buoys_low")) return "buoys_low with 12 buoys";
            Run(c, f, 3);
            if (f.Drops.Count != 2 || f.Drops[1].Pts.Count != 2 || !Near(AvgX(f, f.Drops[1]), 10, 0.1)) return "the rest goes to row 1: " + f.Drops.Count + "/" + (f.Drops.Count > 1 ? f.Drops[1].Pts.Count : 0);
            if (c.BarrierRowsInfo("G") != "0:5:0/10:0;1:10:7/9:0") return "info " + c.BarrierRowsInfo("G");
            return null;
        }

        private static string Late()
        {
            // 4 buoys only: row 0 stays open (6 of 10), the ship reaches it: passed, barrier_late, the rest is dropped from the plan
            var f = CorridorGame(4);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 1230);
            if (!f.HasEvent("asw_warn", "barrier_late")) return "no barrier_late";
            if (!c.BarrierRowsInfo("G").StartsWith("0:5:0/10:1;", StringComparison.Ordinal)) return "info " + c.BarrierRowsInfo("G");
            // an aircraft still flying to the passed row is given the next one
            var g = CorridorGame(40);
            g.DropDelayS = 1500;
            var c2 = StartCourse(g, Barrier229);
            Run(c2, g, 1250);
            if (g.Drops.Count < 2) return "no second package for the aircraft that flew to the passed row (" + g.Drops.Count + ")";
            // T-334: the check of a package in flight cancels it as soon as its points become late (the shrinking packages of row 0), the last package is the next row
            if (!Near(AvgX(g, g.Drops[g.Drops.Count - 1]), 10, 0.3)) return "the new package is at x=" + AvgX(g, g.Drops[g.Drops.Count - 1]);
            return null;
        }

        private static string TrimRow()
        {
            // the points of a cancelled package go back to their own row (not the nearest one)
            var f = CorridorGame(400);
            f.DropDelayS = 30; f.DropEveryS = 30;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);   // 3 buoys fell (31, 61, 91 s)
            if (f.Drops.Count != 1) return "packages " + f.Drops.Count;
            c.CancelRouteOf("G", 1);
            string info = c.BarrierRowsInfo("G");
            if (!info.StartsWith("0:5:7/10:0;", StringComparison.Ordinal)) return "after the cut " + info;
            Run(c, f, 15);
            if (f.Drops.Count != 2 || f.Drops[1].Pts.Count != 7 || !Near(AvgX(f, f.Drops[1]), 5, 0.1)) return "the 7 points are not laid again on row 0: " + f.Drops.Count;
            return null;
        }

        private static string SaveLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw229-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                f.SaveDir = dir; f.LastSavePath = "/s/a.sav";
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                c.SearchCourse(P("{\"tactics\":[\"barrier\"],\"barrier_rows\":3,\"barrier_gap_nm\":8}"));
                Run(c, f, 2200);   // 36 min: row 0 (8 nm, 32 min) is passed, rows 1..3 stand
                string info = c.BarrierRowsInfo("G");
                if (!info.Contains("3:32:") || c.BarrierLines("G") != 3) return "before the save " + info;
                double t0 = c.BandT0("G"), savedAt = f.Now;
                var stamp = new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
                c.OnGameSaved("/s/b.sav", stamp);
                // the game does not restore the mission clock: the new clock says 2000
                var g = CorridorGame(400);
                g.SaveDir = dir; g.LastSavePath = "/s/b.sav"; g.Now = 2000;
                g.Tgt.Lat = f.Tgt.Lat; g.Tgt.Lon = f.Tgt.Lon;
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                var spec = c2.SpecOf("G");
                if (spec == null || spec.BarrierRows != 3 || spec.BarrierGapNm != 8) return "N / I lost: " + (spec == null ? "no group" : spec.BarrierRows + "/" + spec.BarrierGapNm);
                Run(c2, g, 6);
                string back = c2.BarrierRowsInfo("G");
                if (c2.BarrierLines("G") != 3 || !back.Contains("3:32:")) return "rows after the load " + back + " (was " + info + ")";
                Run(c2, g, 60);
                if (g.Drops.Count != 0) return "packages for rows that were already laid: " + g.Drops.Count;
                if (Math.Abs((c2.BandT0("G") - t0) - (2000 - savedAt)) > 1) return "T0 not shifted: " + t0 + " -> " + c2.BandT0("G");
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string View229()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            // the preview of a draft: N planned rows, the odd one shorter
            var pv = c.Preview(false, P("{\"tactics\":[\"barrier\"],\"barrier_rows\":3}"));
            var pl = pv.Preview.Where(l => l.Points.Count == 2).ToList();
            if (pl.Count != 3) return "preview rows " + pl.Count;
            double L0 = AswMath.DistNm(pl[0].Points[0], pl[0].Points[1]), L1 = AswMath.DistNm(pl[1].Points[0], pl[1].Points[1]);
            if (!(L1 < L0 - 1)) return "the odd row is not shorter: " + L0 + " / " + L1;
            c.ClearPreview();
            Run(c, f, 30);
            var gv = c.View().Groups[0];
            if (gv.Plan.RowsMax != 2 || !Near(gv.Plan.GapNm, 5) || gv.Plan.Lines != 2 || gv.Plan.LinesLaid != 2) return "plan " + gv.Plan.RowsMax + "/" + gv.Plan.GapNm + "/" + gv.Plan.Lines + "/" + gv.Plan.LinesLaid;
            string js = c.StateJson();
            if (!js.Contains("\"gap_nm\":5") || !js.Contains("\"rows\":2")) return "plan json: gap_nm / rows missing";
            var lines = gv.Lines.Where(l => l.Kind == "barrier_line" && l.Tactic == "barrier").ToList();
            if (lines.Count != 2 || lines.Any(l => l.State != "done")) return "map lines " + lines.Count;
            if (gv.Lines.Any(l => l.Kind == "standoff" && l.Tactic == "barrier")) return "the barrier has no standoff mark";
            // after the ship passes row 0: that row is "done" and drawn until it is behind, two rows ahead
            Run(c, f, 1300);
            gv = c.View().Groups[0];
            if (gv.Plan.Lines != 2) return "rows ahead " + gv.Plan.Lines;
            // after a reset there are no old lines
            f.Tgt.CourseDeg = 200;
            Run(c, f, 400);
            if (!Logged(f, "barrier reset")) return "no reset";
            gv = c.View().Groups[0];
            if (gv.Plan.Lines != 2) return "rows ahead after the reset " + gv.Plan.Lines;
            return null;
        }
    }
}
