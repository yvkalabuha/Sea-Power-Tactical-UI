using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 PL: lanes, anchor, order, summary, migration (v3-reanchor, v3-migrate pure part).
    internal static class PatternPlanTests
    {
        private static bool Near(double a, double b, double e = 1e-2) { return Math.Abs(a - b) <= e; }
        private static LaneMember M(int uid, bool dip, bool buoys, bool mad = false, int n = 20, double v = 100, double remain = 3600)
        {
            return new LaneMember { Uid = uid, Dip = dip, Buoys = buoys, Mad = mad, BuoyCount = n, SpeedKn = v, RemainS = remain };
        }

        public static string Run()
        {
            // PL1: weights and assignment
            if (AswPatternMath.Weight(0, 7200) != 100 * 3600 || AswPatternMath.Weight(120, 1800) != 120 * 1800 || AswPatternMath.Weight(120, -5) != 0) return "weight";
            var three = new List<LaneMember> { M(1, true, true), M(2, true, true), M(3, false, true) };
            var tac = new List<string> { "barrier", "dip", "corridor" };
            var a3 = AswPatternMath.AssignLanes(three, tac);
            if (a3.Count != 3 || a3.Values.Distinct().Count() != 3) return "assign 3 tactics x 3";
            if (a3[3] == "dip") return "assign: buoy-only aircraft cannot dip";
            var one = AswPatternMath.AssignLanes(new List<LaneMember> { M(1, true, true) }, tac);
            if (one.Count != 1 || one[1] != "dip") return "assign 1 aircraft takes dip first";
            var onlyBuoys = AswPatternMath.AssignLanes(new List<LaneMember> { M(1, false, true) }, tac);
            if (onlyBuoys[1] != "corridor") return "assign 1 buoy aircraft takes corridor";
            var noBuoys = AswPatternMath.AssignLanes(new List<LaneMember> { M(1, true, true, false, 0), M(2, true, false) }, new List<string> { "corridor" });
            if (noBuoys.ContainsKey(1) || noBuoys.ContainsKey(2)) return "no buoys -> no corridor";
            var many = AswPatternMath.AssignLanes(new List<LaneMember> { M(1, true, false), M(2, false, true), M(3, false, true), M(4, false, true, false, 20, 100, 1800) }, new List<string> { "dip", "corridor", "barrier" });
            if (many[1] != "dip" || many.Values.Count(t => t == "corridor") + many.Values.Count(t => t == "barrier") != 3 || !many.Values.Contains("barrier")) return "assign rest by need";
            var area = AswPatternMath.AssignLanes(new List<LaneMember> { M(1, false, false, true), M(2, true, true), M(3, false, true) }, new List<string> { "mad", "grid", "dip" });
            if (area[1] != "mad" || area[2] != "dip" || area[3] != "grid") return "assign area order";
            if (AswPatternMath.AssignLanes(null, tac).Count != 0 || AswPatternMath.AssignLanes(three, new List<string>()).Count != 0) return "assign empty";
            // PL2: splits
            List<int> used;
            var rect = AswPatternMath.SplitRect(30, 10, new[] { 1.0, 2.0, 3.0 }, 2, out used);
            if (rect.Count != 3 || !Near(rect[0][1] - rect[0][0], 5) || !Near(rect[1][1] - rect[1][0], 10) || !Near(rect[2][1], 30) || !Near(rect[0][0], 0)) return "split rect";
            for (int i = 1; i < rect.Count; i++) if (!Near(rect[i][0], rect[i - 1][1], 1e-9)) return "split rect contiguous";
            var narrow = AswPatternMath.SplitRect(10, 10, new[] { 1.0, 1.0, 1.0, 1.0 }, 4, out used);
            if (narrow.Count != 2 || used.Count != 2 || !Near(narrow[1][1], 10)) return "split rect narrow " + narrow.Count;
            var heavy = AswPatternMath.SplitRect(10, 10, new[] { 5.0, 1.0, 5.0 }, 4, out used);
            if (heavy.Count != 2 || used.Contains(1)) return "split rect drops the lightest";
            if (AswPatternMath.SplitRect(1, 1, new[] { 1.0, 1.0 }, 4).Count != 1) return "split rect tiny";
            var sl = AswPatternMath.SplitLine(36, new[] { 1.0, 2.0 });
            if (sl.Count != 2 || !Near(sl[0][1], 12) || !Near(sl[1][0], 12) || !Near(sl[1][1], 36)) return "split line";
            if (AswPatternMath.SplitLine(10, new[] { 0.0, 0.0 }).Count != 2 || !Near(AswPatternMath.SplitLine(10, new[] { 0.0, 0.0 })[0][1], 5)) return "split line zero weights";
            // PL3: corridor sides
            if (AswPatternMath.CorridorSide(1, 0, 0, 0) != 0) return "side K=1";
            if (AswPatternMath.CorridorSide(2, 0, 0, 0) != -1 || AswPatternMath.CorridorSide(2, 1, 0, 0) != 1) return "side K=2";
            if (AswPatternMath.CorridorSide(3, 2, 12, 8) != 1 || AswPatternMath.CorridorSide(3, 2, 5, 9) != -1) return "side K=3 lagging";
            if (AswPatternMath.CorridorSide(3, 0, 0, 0) != -1 || AswPatternMath.CorridorSide(3, 1, 0, 0) != 1) return "side K=3 tie";
            // PL4: anchor
            var o = new GeoPt(35, 20);
            var an = new AswAnchor();
            if (AswPatternMath.Reanchor(ref an, 90, 15, o, 0, 30, 60, 2.7) != null || !an.Set) return "anchor first";
            if (AswPatternMath.Reanchor(ref an, 110, 15, AswMath.Offset(o, 90, 1), 100, 30, 60, 2.7) != null) return "20 deg -> none";
            if (AswPatternMath.Reanchor(ref an, 125, 15, AswMath.Offset(o, 90, 2), 200, 30, 60, 2.7) != null) return "35 deg: timer starts";
            if (AswPatternMath.Reanchor(ref an, 125, 15, AswMath.Offset(o, 90, 2), 230, 30, 60, 2.7) != null) return "35 deg: not held yet";
            if (an.TurnSinceS <= 0) return "turn timer kept";
            if (AswPatternMath.Reanchor(ref an, 125, 15, AswMath.Offset(o, 90, 2), 262, 30, 60, 2.7) != "turn") return "35 deg held 60 s -> turn";
            if (!Near(an.AxisDeg, 125) || an.TurnSinceS != 0) return "anchor rebuilt";
            if (AswPatternMath.KeepPacket(50) || !AswPatternMath.KeepPacket(44.9) || !AswPatternMath.KeepPacket(-30)) return "keep packet";
            var zz = new AswAnchor(); AswPatternMath.Reanchor(ref zz, 90, 15, o, 0, 30, 60, 2.7);
            for (int t = 1; t <= 12; t++)   // +-20 degrees every 10 minutes for 2 hours
                if (AswPatternMath.Reanchor(ref zz, 90 + (t % 2 == 0 ? 20 : -20), 15, AswMath.Offset(o, 90, 0.2), t * 600.0, 30, 60, 2.7) != null) return "zigzag re-anchored at " + t;
            var dr = new AswAnchor(); AswPatternMath.Reanchor(ref dr, 0, 15, o, 0, 30, 60, 2.7);
            if (AswPatternMath.Reanchor(ref dr, 0, 15, AswMath.Offset(o, 90, 2), 60, 30, 60, 2.7) != null) return "2 nm side step is fine";
            if (AswPatternMath.Reanchor(ref dr, 0, 15, AswMath.Offset(o, 90, 4), 120, 30, 60, 2.7) != "drift") return "4 nm side step -> drift";
            var sp = new AswAnchor(); AswPatternMath.Reanchor(ref sp, 0, 15, o, 0, 30, 60, 2.7);
            if (AswPatternMath.Reanchor(ref sp, 0, 19, o, 60, 30, 60, 2.7) != null) return "speed +27 % is within 30";
            sp = new AswAnchor(); AswPatternMath.Reanchor(ref sp, 0, 15, o, 0, 30, 60, 2.7);
            if (AswPatternMath.Reanchor(ref sp, 0, 21, o, 60, 30, 60, 2.7) != "speed") return "speed +40 %";
            var sd = new AswAnchor(); AswPatternMath.Reanchor(ref sd, 0, 15, o, 0, 30, 60, 2.7);
            if (AswPatternMath.Reanchor(ref sd, 0, 2, o, 60, 30, 60, 2.7) != "stand" || !sd.Stand) return "stand";
            if (AswPatternMath.Reanchor(ref sd, 0, 15, o, 120, 30, 60, 2.7) != "stand" || sd.Stand) return "back from stand";
            var le = AswPatternMath.Lead(new AswAnchor { Set = true, O = o, AxisDeg = 90, T0 = 0, VKn = 15 }, 3600);
            if (!Near(AswMath.DistNm(o, le), 15) || !Near(AswMath.BearingDeg(o, le), 90)) return "lead";
            if (AswMath.DistNm(o, AswPatternMath.Lead(new AswAnchor { Set = true, O = o, AxisDeg = 90, T0 = 0, VKn = 2, Stand = true }, 3600)) > 1e-9) return "lead stand";
            // PL5: order of points
            var rnd = new Random(176); var pts = new List<NmPt>();
            for (int i = 0; i < 40; i++) pts.Add(new NmPt(rnd.NextDouble() * 40, rnd.NextDouble() * 40));
            var start = new NmPt(0, 0);
            var ord = AswPatternMath.OrderPoints(start, pts);
            if (ord.Count != 40) return "order count " + ord.Count;
            var left = new List<NmPt>(pts); var nn = new List<NmPt>(); var cur = start;
            while (left.Count > 0) { int bi = 0; double bd = 1e18; for (int i = 0; i < left.Count; i++) { double d = AswPatternMath.PathNm(cur, new List<NmPt> { left[i] }); if (d < bd) { bd = d; bi = i; } } cur = left[bi]; nn.Add(cur); left.RemoveAt(bi); }
            if (AswPatternMath.PathNm(start, ord) > AswPatternMath.PathNm(start, nn) + 1e-9) return "order worse than nearest neighbour";
            if (!AswPatternMath.OrderPoints(start, pts).SequenceEqual(ord)) return "order not deterministic";
            var many60 = new List<NmPt>(); for (int i = 0; i < 60; i++) many60.Add(new NmPt(i, i % 7));
            if (AswPatternMath.OrderPoints(start, many60).Count != 40 || AswPatternMath.OrderPoints(start, many60, 10).Count != 10) return "order max";
            if (AswPatternMath.LowerBoundNm(15, 4.32) != 14 * 4.32 || AswPatternMath.LowerBoundNm(0, 4) != 0) return "lower bound";
            // PL6: coverage
            if (!AswPatternMath.NodeCovered(true, 3000, 1200, 600)) return "covered";
            if (AswPatternMath.NodeCovered(true, 1500, 1200, 600)) return "not covered: life short of pass + lead";
            if (AswPatternMath.NodeCovered(false, 9999, 0, 600)) return "not covered: no buoy";
            if (!AswPatternMath.NodeCovered(true, 700, 0, 600) || AswPatternMath.NodeCovered(true, 500, 0, 600)) return "area: remain >= lead";
            // PL7: summary
            var sc = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "corridor", R = 2.70, XNm = 20, LNm = 20, VKn = 15, Buoys = 100, MaxRows = 6 });
            if (sc.RowsSide != 3 || sc.Warn != null || Math.Abs(sc.BuoysPerHour - 6 * 15 / 4.32) > 1e-6) return "summary corridor " + sc.RowsSide + " " + sc.BuoysPerHour;
            var ss = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "corridor", R = 2.70, XNm = 20, LNm = 20, VKn = 15, Buoys = 10, MaxRows = 6 });
            if (ss.Warn != "buoys_short" || ss.RowsMax != 3 || ss.RowsSide >= 3 || ss.HoursLeft <= 0) return "summary short " + ss.Warn + " " + ss.RowsSide + " " + ss.HoursLeft;
            var sw = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "corridor", R = 2.70, XNm = 50, LNm = 20, VKn = 15, Buoys = 500, MaxRows = 2 });
            if (sw.Warn != "corridor_too_wide") return "summary too wide";
            // T-229: the sliding barrier summary (N = 2, I = 5, v = 15, X = 40: 10 + 9 buoys, ~28.5 buoys per hour)
            var sb = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 2, VKn = 15, Buoys = 56, LifeS = 28800 });
            if (sb.Nodes != 19 || sb.Buoys != 19 || sb.BuoysEven != 10 || sb.BuoysOdd != 9 || sb.RowsPlan != 2 || sb.RowsAffordable != 2 || !Near(sb.BuoysPerHour, 28.5) || sb.Warn != null) return "summary barrier " + sb.Nodes + " " + sb.BuoysPerHour + " " + sb.Warn;
            var sb4 = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 4, VKn = 15, Buoys = 56 });
            if (sb4.Nodes != 38) return "summary barrier 4 rows " + sb4.Nodes;
            var sbs = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 2, VKn = 15, Buoys = 12 });
            if (sbs.Warn != "barrier_rows_short" || sbs.RowsAffordable != 1) return "summary barrier rows short " + sbs.Warn + " " + sbs.RowsAffordable;
            var sbl = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 2, VKn = 15, Buoys = 5 });
            if (sbl.Warn != "buoys_short" || sbl.RowsAffordable != 0) return "summary barrier buoys short " + sbl.Warn;
            var sbn = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 2, VKn = 15, Buoys = 56, Stagger = false });
            if (sbn.Nodes != 20) return "summary barrier without stagger " + sbn.Nodes;
            var sbq = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = 2.70, XNm = 40, GapNm = 5, Rows = 2, VKn = 15, Buoys = 56, LifeS = 1800 });
            if (sbq.Warn != "life_short") return "summary barrier life short " + sbq.Warn;
            var sa = AswPatternMath.Summary(new AswSummaryIn { Order = "area", Tactic = "grid", R = 2.70, AreaNorthNm = 21.6, AreaEastNm = 12.96, VKn = 100, Buoys = 56 });
            if (sa.Nx != 5 || sa.Ny != 3 || !Near(sa.StepNm, 4.32) || sa.Nodes != 15 || sa.Warn != null) return "summary area " + sa.Nx + "x" + sa.Ny;
            var sd2 = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "dip", RDip = 2.1, LNm = 20 });
            if (!Near(sd2.StepNm, 2) || sd2.Nodes != 10) return "summary dip " + sd2.Nodes;
            // PL8: migration
            bool mad;
            var rows = AswPatternMath.MigrateCourse(new List<string> { "rows" }, 8, 8, 0, true, out mad);
            if (mad || rows.Tactics.Count != 1 || rows.Tactics[0] != "corridor" || rows.CorridorXNm != 20 || rows.CorridorLNm != 20) return "migrate rows";
            if (AswPatternMath.MigrateCourse(new List<string> { "rows" }, 33, 8, 0, true, out mad).CorridorLNm != 40) return "migrate rows lead 33";
            var cross = AswPatternMath.MigrateCourse(new List<string> { "cross" }, 8, 8, 33, true, out mad);
            if (cross.Tactics[0] != "barrier" || cross.BarrierXNm != 30 || cross.BarrierLNm != 40) return "migrate cross";
            if (AswPatternMath.MigrateCourse(new List<string> { "cross" }, 8, 8, 0, true, out mad).BarrierXNm != 40) return "migrate cross default width";
            var md = AswPatternMath.MigrateCourse(new List<string> { "mad" }, 8, 8, 0, true, out mad);
            if (mad || md.Tactics.Count != 1 || md.Tactics[0] != "corridor") return "migrate mad with buoys";
            md = AswPatternMath.MigrateCourse(new List<string> { "mad" }, 8, 8, 0, false, out mad);
            if (!mad || md.Tactics.Count != 0) return "migrate mad removed";
            var dp = AswPatternMath.MigrateCourse(new List<string> { "rows", "dip" }, 8, 8, 0, true, out mad);
            if (dp.Tactics.Count != 2 || dp.Tactics[0] != "dip" || dp.DipLNm != 20) return "migrate dip";
            var mix = AswPatternMath.MigrateCourse(new List<string> { "rows", "mad" }, 8, 8, 0, true, out mad);
            if (mix.Tactics.Count != 1) return "migrate rows + mad: one corridor";
            if (AswPatternMath.MigrateArea("rows") != "grid" || AswPatternMath.MigrateArea("cross") != "grid" || AswPatternMath.MigrateArea("buoys") != "grid") return "migrate area grid";
            if (AswPatternMath.MigrateArea("dip") != "dip" || AswPatternMath.MigrateArea("mad") != "mad" || AswPatternMath.MigrateArea(null) != "auto") return "migrate area same";
            return AswPatternMath.SelfTest();
        }
    }
}
