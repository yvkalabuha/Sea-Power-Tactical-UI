using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 PC: v3-corridor, v3-corridor-short, v3-barrier, v3-dip (pure part).
    internal static class PatternCourseTests
    {
        private static bool Near(double a, double b, double e = 1e-2) { return Math.Abs(a - b) <= e; }

        public static string Run()
        {
            bool tw;
            // PC1: rows
            var r20 = AswPatternMath.CorridorRows(20, 2.70, 6, out tw);
            if (tw || r20.Length != 3 || !Near(r20[0], 2.16) || !Near(r20[1], 5.00) || !Near(r20[2], 7.84)) return "rows x20 " + string.Join("/", r20);
            var r10 = AswPatternMath.CorridorRows(10, 2.70, 6, out tw);
            if (r10.Length != 1 || !Near(r10[0], 2.50)) return "rows x10 " + string.Join("/", r10);
            if (AswPatternMath.CorridorRows(30, 2.70, 6, out tw).Length != 4 || AswPatternMath.CorridorRows(40, 2.70, 6, out tw).Length != 5) return "rows x30 / x40";
            var r50 = AswPatternMath.CorridorRows(50, 2.70, 6, out tw);
            if (r50.Length != 6 || tw) return "rows x50 " + r50.Length;
            if (AswPatternMath.CorridorRows(20, 5.4, 6, out tw).Length != 1 || AswPatternMath.CorridorRows(30, 5.4, 6, out tw).Length != 2) return "rows ssq77";
            var cut = AswPatternMath.CorridorRows(50, 2.70, 2, out tw);
            if (!tw || cut.Length != 2 || !Near(cut[0], 2.16) || !Near(cut[1], 22.84)) return "rows too wide " + tw + " " + string.Join("/", cut);
            for (int i = 1; i < r50.Length; i++) if (r50[i] - r50[i - 1] > 1.8 * 2.70 + 1e-6) return "rows gap";
            // PC2: columns and meander
            var cols = AswPatternMath.Columns(2.70, 20);
            if (cols.Count != 4 || !Near(cols[0], 3) || !Near(cols[1], 7.32) || !Near(cols[2], 11.64) || !Near(cols[3], 15.96)) return "columns " + string.Join("/", cols);
            var m = AswPatternMath.Meander(r20, cols, -1, 0, null);
            if (m.Count != 12) return "meander count " + m.Count;
            if (!Near(m[0].Y, -2.16) || !Near(m[2].Y, -7.84) || !Near(m[3].Y, -7.84) || !Near(m[5].Y, -2.16)) return "meander turns";
            if (!Near(m[0].X, 3) || !Near(m[3].X, 7.32) || m.Any(p => p.Y > 0)) return "meander side";
            var skip = new HashSet<long> { 0 * 16 + 1, 1 * 16 + 2 };
            var ms = AswPatternMath.Meander(r20, cols, 1, 0, skip);
            if (ms.Count != 10 || ms.Any(p => p.Y < 0)) return "meander skip " + ms.Count;
            var mf = AswPatternMath.Meander(r20, cols, 1, 2, null);
            if (mf.Count != 6 || !Near(mf[0].X, 11.64) || !Near(mf[0].Y, 2.16)) return "meander from column";
            // PC3: priority, short of buoys
            var pr = AswPatternMath.RowPriority(3);
            if (!pr.SequenceEqual(new[] { 0, 2, 1 })) return "priority 3";
            if (!AswPatternMath.RowPriority(5).SequenceEqual(new[] { 0, 4, 3, 2, 1 }) || AswPatternMath.RowPriority(1).Count != 1 || AswPatternMath.RowPriority(2).Count != 2 || AswPatternMath.RowPriority(0).Count != 0) return "priority n";
            if (AswPatternMath.RowsAffordable(3, 10, 4) != 2) return "affordable 2 of 3";
            if (AswPatternMath.RowsAffordable(3, 100, 4) != 3 || AswPatternMath.RowsAffordable(3, 3, 4) != 0) return "affordable bounds";
            // PC4: life
            if (!AswPatternMath.LifeOk(20, 15, 3 * 3600, 600, 600) ) return "life ok";
            if (AswPatternMath.LifeOk(40, 15, 3600, 600, 0)) return "life too far";   // 0.8 * 15 * 3000 / 3600 = 10
            if (!AswPatternMath.LifeOk(10, 15, 3600, 600, 0)) return "life edge";
            // PC5: barrier
            double step; var line = AswPatternMath.BarrierLine(40, 2.70, out step);
            if (line.Count != 10 || !Near(step, 3.96) || !Near(line[0], -(20 - 2.16)) || !Near(line[9], 17.84)) return "barrier line " + line.Count + " " + step;
            for (int i = 1; i < line.Count; i++) if (!Near(line[i] - line[i - 1], step, 1e-6)) return "barrier even";
            double st1; if (AswPatternMath.BarrierLine(3, 2.70, out st1).Count != 1 || st1 != 0) return "barrier one buoy";
            // T-229: the sliding barrier rows (ADR-0094)
            double stp;
            var ev = AswPatternMath.BarrierRowYs(40, 2.70, 0, true, out stp);
            var od = AswPatternMath.BarrierRowYs(40, 2.70, 1, true, out stp);
            if (ev.Count != 10 || !Near(ev[0], -17.84) || !Near(ev[9], 17.84)) return "row even " + ev.Count;
            if (od.Count != 9 || !Near(od[0], -15.8578, 1e-3) || !Near(od[8], 15.8578, 1e-3)) return "row odd " + od.Count + " " + od[0];
            for (int i = 0; i < od.Count; i++) if (!Near(od[i], (ev[i] + ev[i + 1]) / 2, 1e-9)) return "row odd midpoint " + i;
            if (AswPatternMath.BarrierRowYs(40, 2.70, 1, false, out stp).Count != 10) return "row odd without stagger";
            if (AswPatternMath.BarrierRowYs(40, 2.70, 2, true, out stp).Count != 10 || AswPatternMath.BarrierRowYs(40, 2.70, 3, true, out stp).Count != 9) return "row parity by seq";
            if (AswPatternMath.BarrierRowYs(3, 2.70, 1, true, out stp).Count != 1) return "row one buoy";
            if (!Near(AswPatternMath.BarrierRowX(5, 5, 0), 5) || !Near(AswPatternMath.BarrierRowX(5, 5, 3), 20)) return "row x";
            if (AswPatternMath.BarrierReached(4.99, 5) || !AswPatternMath.BarrierReached(5, 5) || !AswPatternMath.BarrierReached(5.2, 5)) return "reached";
            // reset of the band: 30 deg held for 60 s (Q-T229-2)
            double since = 0; double t0 = 1000;
            if (AswPatternMath.BarrierReset(90, 125, 0, 20, 2.7, t0, 30, 60, ref since) != null || since <= 0) return "reset starts";
            if (AswPatternMath.BarrierReset(90, 125, 0, 20, 2.7, t0 + 59, 30, 60, ref since) != null) return "reset 59 s";
            if (AswPatternMath.BarrierReset(90, 125, 0, 20, 2.7, t0 + 60, 30, 60, ref since) != "turn") return "reset 60 s";
            since = 0;   // a turn to the left counts the same
            if (AswPatternMath.BarrierReset(90, 55, 0, 20, 2.7, t0, 30, 60, ref since) != null || since <= 0 || AswPatternMath.BarrierReset(90, 55, 0, 20, 2.7, t0 + 61, 30, 60, ref since) != "turn") return "reset to the left";
            since = 0;
            if (AswPatternMath.BarrierReset(90, 119, 0, 20, 2.7, t0, 30, 60, ref since) != null || since != 0) return "reset below the threshold";
            since = 0;   // a zigzag: 50 deg for 30 s, back, again: never 60 s in a row
            double tz = t0; bool zig = false;
            for (int i = 0; i < 20; i++)
            {
                double ax = (i & 1) == 0 ? 140 : 90;
                if (AswPatternMath.BarrierReset(90, ax, 0, 20, 2.7, tz, 30, 60, ref since) != null) zig = true;
                tz += 30;
            }
            if (zig) return "zigzag resets";
            since = 0;
            if (AswPatternMath.BarrierReset(90, 90, 17.4, 20, 2.7, t0, 30, 60, ref since) != "off_band" || AswPatternMath.BarrierReset(90, 90, 17.2, 20, 2.7, t0, 30, 60, ref since) != null) return "off band";
            if (AswPatternMath.BarrierRowsAffordable(2, 100, 10, 9, 0) != 2 || AswPatternMath.BarrierRowsAffordable(2, 12, 10, 9, 0) != 1 || AswPatternMath.BarrierRowsAffordable(2, 5, 10, 9, 0) != 0) return "rows affordable";
            if (AswPatternMath.BarrierRowsAffordable(2, 12, 10, 9, 1) != 1 || AswPatternMath.BarrierRowsAffordable(2, 18, 10, 9, 1) != 1 || AswPatternMath.BarrierRowsAffordable(2, 19, 10, 9, 1) != 2) return "rows affordable odd first";
            if (!AswPatternMath.BarrierLifeOk(10, 15, 3600, 0) || AswPatternMath.BarrierLifeOk(10, 15, 1800, 600) || !AswPatternMath.BarrierLifeOk(500, 0, 3600, 0)) return "life ok";
            // PC6: dip
            if (AswPatternMath.DipStepV3(2.1) != 2 || AswPatternMath.DipStepV3(0.3) != 1 || AswPatternMath.DipStepV3(10) != 8 || AswPatternMath.DipStepV3(3.8) != 6) return "dip step";
            var taken = new List<double>(); bool hold;
            for (int k = 0; k < 10; k++)
            {
                double nx = AswPatternMath.LeapfrogNext(taken, 2, 2, 20, out hold);   // two helicopters take the points alternately
                if (hold) return "dip hold too early at " + k;
                if (!Near(nx, 2 + 2 * k)) return "dip point " + k + " = " + nx;
                taken.Add(nx);
            }
            double hx = AswPatternMath.LeapfrogNext(taken, 2, 2, 20, out hold);
            if (!hold || !Near(hx, 20)) return "dip hold at the limit";
            if (!Near(AswPatternMath.LeapfrogNext(new List<double>(), 4, 4, 40, out hold), 4) || hold) return "dip first point";
            if (!Near(AswPatternMath.LeapfrogNext(new List<double> { 1 }, 2, 6, 40, out hold), 6)) return "dip xMin";
            // PC7: fixed target
            var ring = AswPatternMath.StandingRing(10, 4.32);
            if (ring.Count < 15) return "ring count " + ring.Count;
            foreach (var p in ring) if (!Near(Math.Sqrt(p.X * p.X + p.Y * p.Y), 10, 1e-6)) return "ring radius";
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                if (Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) > 4.32 + 1e-6) return "ring step";
            }
            if (AswPatternMath.StandingRing(0, 4).Count != 0) return "ring zero";
            // PC8: depth
            var cb = new List<string> { "corridor" }; var bb = new List<string> { "barrier", "dip" }; var dd = new List<string> { "dip" };
            if (AswPatternMath.DepthAuto(cb, "course", 3000) != "below" || AswPatternMath.DepthAuto(bb, "course", 3000) != "below") return "depth below";
            if (AswPatternMath.DepthAuto(cb, "area", 3000) != "alternate" || AswPatternMath.DepthAuto(cb, "cross", 3000) != "alternate") return "depth area / cross";
            if (AswPatternMath.DepthAuto(cb, "course", 300) != "alternate") return "depth shallow water";
            if (AswPatternMath.DepthAuto(dd, "course", 3000) != "alternate") return "depth dip";
            string t334 = T334();
            if (t334 != null) return t334;
            return AswPatternMath.SelfTest();
        }

        // T-334 / ADR-0130: ETA filter, near-first order, row take rule.
        private static string T334()
        {
            bool tw;
            var rows = AswPatternMath.CorridorRows(20, 2.70, 6, out tw);
            var cols = AswPatternMath.Columns(2.70, 20);
            var left = AswPatternMath.Meander(rows, cols, -1, 0, null);
            var right = AswPatternMath.Meander(rows, cols, 1, 0, null);
            var all = new List<NmPt>(left); all.AddRange(right);
            var origin = new NmPt(0, 0);
            double end;
            // (a) near-first: x never decreases, inside a column Y is monotonic, the direction alternates
            var ord = AswPatternMath.NearFirstOrder(origin, all);
            if (ord.Count != 24 || ord.Distinct().Count() != 24) return "t334 order count " + ord.Count;
            for (int i = 1; i < ord.Count; i++) if (all[ord[i]].X < all[ord[i - 1]].X - 1e-9) return "t334 x decreases at " + i;
            var dirs = new List<int>();
            for (int c = 0; c < 4; c++)
            {
                var col = ord.Skip(c * 6).Take(6).Select(k => all[k]).ToList();
                if (col.Any(p => !Near(p.X, col[0].X))) return "t334 column " + c + " mixed";
                int d = Math.Sign(col[1].Y - col[0].Y);
                for (int i = 1; i < col.Count; i++) if (Math.Sign(col[i].Y - col[i - 1].Y) != d) return "t334 column " + c + " not monotonic";
                dirs.Add(d);
            }
            for (int c = 1; c < 4; c++) if (dirs[c] == dirs[c - 1]) return "t334 direction does not alternate";
            // (b) the old loop: 6 nodes of the right side behind the ship at the drop
            var loop = new List<NmPt>(left); var rr = new List<NmPt>(right); rr.Reverse(); loop.AddRange(rr);
            var keepLoop = AswPatternMath.AheadKeep(origin, loop, 100, AswPatternMath.DropPointS, 0, 15, AswPatternMath.AheadMarginNm, out end);
            if (keepLoop.Count != 18) return "t334 loop kept " + keepLoop.Count;
            // (c) the ladder: 20 of 24, the four nodes of the x = 3 column are dropped
            var lad = ord.Select(k => all[k]).ToList();
            var keepLad = AswPatternMath.AheadKeep(origin, lad, 100, AswPatternMath.DropPointS, 0, 15, AswPatternMath.AheadMarginNm, out end);
            if (keepLad.Count != 20) return "t334 ladder kept " + keepLad.Count;
            foreach (int k in keepLad)
                if (AswPatternMath.SlackNm(lad[k].X, 0, 15, 0) < 0) return "t334 ladder negative";
            if (lad.Where((p, k) => !keepLad.Contains(k)).Any(p => !Near(p.X, 3))) return "t334 ladder dropped not x=3";
            if (end <= 0) return "t334 ladder end";
            // (d) barrier row at 5 nm: 10 buoys
            double st; var ys = AswPatternMath.BarrierLine(40, 2.70, out st);
            var rowPts = ys.Select(y => new NmPt(5, y)).ToList();
            var ordRow = AswPatternMath.NearFirstOrder(origin, rowPts);
            var path = ordRow.Select(k => rowPts[k]).ToList();
            var keep100 = AswPatternMath.AheadKeep(origin, path, 100, AswPatternMath.DropPointS, 0, 15, AswPatternMath.AheadMarginNm, out end);
            if (keep100.Count != 2 || AswPatternMath.RowTakeOk(keep100.Count, 10)) return "t334 row 5 at 100 kn kept " + keep100.Count;
            var keep250 = AswPatternMath.AheadKeep(origin, path, 250, AswPatternMath.DropPointS, 0, 15, AswPatternMath.AheadMarginNm, out end);
            if (keep250.Count != 10 || !AswPatternMath.RowTakeOk(10, 10)) return "t334 row 5 at 250 kn kept " + keep250.Count;
            // (e) along-track speed
            if (!Near(AswPatternMath.AlongKn(15, 90, 90), 15) || AswPatternMath.AlongKn(15, 180, 90) > 1e-9 || !Near(AswPatternMath.AlongKn(15, 150, 90), 7.5)) return "t334 along";
            // (f) slack
            if (!Near(AswPatternMath.SlackNm(10, 2, 15, 1200), 3) || !Near(AswPatternMath.SlackNm(3, 0.55, 15, 132), 1.9)) return "t334 slack";
            return null;
        }
    }
}
