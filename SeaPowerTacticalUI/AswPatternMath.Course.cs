using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 PC / ADR-0076 s.3: by-course patterns in the course frame (X forward along the axis, Y to the right): corridor, barrier, dip chain.
    internal static partial class AswPatternMath
    {
        private static readonly double[] DipStepsV3 = { 1, 2, 4, 6, 8 };

        /// <summary>
        /// Rows of one side, inner -> outer (nm from the axis). a = 0.8R, outer = max(a, X/2 - 0.8R); a gap &lt;= 0.9R -> one row at the middle, else
        /// 1 + ceil(gap/1.8R) evenly. More than maxRows -> maxRows rows evenly over the same span and tooWide = true.
        /// </summary>
        public static double[] CorridorRows(double xNm, double r, int maxRows, out bool tooWide)
        {
            tooWide = false;
            double a = 0.8 * r, outer = Math.Max(a, xNm / 2 - 0.8 * r), gap = outer - a;
            int n = gap <= 0.9 * r + 1e-9 ? 1 : 1 + (int)Math.Ceiling(gap / (1.8 * r) - 1e-9);
            if (maxRows > 0 && n > maxRows) { n = maxRows; tooWide = true; }
            var rows = new double[n];
            if (n == 1) { rows[0] = (a + outer) / 2; return rows; }
            for (int i = 0; i < n; i++) rows[i] = a + gap * i / (n - 1);
            return rows;
        }

        /// <summary>Columns x_j = x0 + j * 1.6R while x_j &lt;= L.</summary>
        public static List<double> Columns(double r, double lNm, double x0 = 3)
        {
            var res = new List<double>();
            double d = 1.6 * r;
            if (d <= 0) return res;
            for (int j = 0; x0 + j * d <= lNm + 1e-9 && j < 1000; j++) res.Add(x0 + j * d);
            return res;
        }

        /// <summary>
        /// Meander of one side (-1 left / +1 right) from column fromCol: an even column goes inner -> outer, an odd one outer -> inner, so the
        /// buoys stand in the corners of the turns. skip holds node keys (col * 16 + row) that are already covered.
        /// </summary>
        public static List<NmPt> Meander(IList<double> rowsY, IList<double> colsX, int side, int fromCol, ISet<long> skip)
        {
            var res = new List<NmPt>();
            if (rowsY == null || colsX == null) return res;
            int rows = rowsY.Count, sgn = side < 0 ? -1 : 1;
            for (int j = Math.Max(0, fromCol); j < colsX.Count; j++)
                for (int t = 0; t < rows; t++)
                {
                    int row = (j % 2 == 0) ? t : rows - 1 - t;
                    if (skip != null && skip.Contains((long)j * 16 + row)) continue;
                    res.Add(new NmPt(colsX[j], sgn * rowsY[row]));
                }
            return res;
        }

        /// <summary>Row order when buoys are short: inner, outer, then the middle ones outer -> inner.</summary>
        public static List<int> RowPriority(int n)
        {
            var res = new List<int>();
            if (n <= 0) return res;
            res.Add(0);
            if (n > 1) res.Add(n - 1);
            for (int i = n - 2; i >= 1; i--) res.Add(i);
            return res;
        }

        /// <summary>Rows (in priority order) that fit: min(nRows, floor(buoys / cols)); cols = nodes of one row level.</summary>
        public static int RowsAffordable(int nRows, int buoys, int cols)
        {
            if (nRows <= 0 || buoys <= 0) return 0;
            if (cols <= 0) return nRows;
            return Math.Min(nRows, buoys / cols);
        }

        /// <summary>A node is placed only while x &lt;= 0.8 v (T - fly - loiter).</summary>
        public static bool LifeOk(double xAbsNm, double vKn, double lifeS, double flyS, double loiterS)
        {
            return xAbsNm <= 0.8 * Math.Max(0, vKn) * Math.Max(0, lifeS - flyS - loiterS) / 3600.0 + 1e-9;
        }

        /// <summary>Y of the buoys of a barrier line (left -> right): edges at +-(X/2 - 0.8R), m = 1 + ceil((X - 1.6R)/1.6R) evenly; stepNm = 0 for one buoy.</summary>
        public static List<double> BarrierLine(double xNm, double r, out double stepNm)
        {
            var res = new List<double>();
            int m = AxisCount(xNm, r);
            if (m <= 1) { stepNm = 0; res.Add(0); return res; }
            double half = xNm / 2 - 0.8 * r;
            stepNm = 2 * half / (m - 1);
            for (int i = 0; i < m; i++) res.Add(-half + stepNm * i);
            return res;
        }

        /// <summary>
        /// T-229 / ADR-0094: Y of the buoys of a barrier row (left -> right). An even seq is the full line (BarrierLine); an odd seq with stagger is the m-1 midpoints
        /// between the buoys of the even row (every buoy inside +-X/2). stepNm = the spacing of the row.
        /// </summary>
        public static List<double> BarrierRowYs(double xNm, double r, int seq, bool stagger, out double stepNm)
        {
            var even = BarrierLine(xNm, r, out stepNm);
            if (!stagger || (seq & 1) == 0 || even.Count < 2) return even;
            var res = new List<double>();
            for (int i = 0; i + 1 < even.Count; i++) res.Add((even[i] + even[i + 1]) / 2);
            return res;
        }

        /// <summary>X of row k in the band frame: firstX + k * gap (the first row is one interval from the ship).</summary>
        public static double BarrierRowX(double firstXNm, double gapNm, int k) { return firstXNm + k * gapNm; }

        /// <summary>The ship reached the row: its projection on the band axis is not behind the row.</summary>
        public static bool BarrierReached(double xShipNm, double xRowNm) { return xShipNm >= xRowNm - 1e-9; }

        /// <summary>
        /// Reset of the barrier band: the ship axis differs from the band axis by deg or more for holdS in a row -> "turn"; the ship left the band sideways
        /// (|y| &gt; X/2 - r) -> "off_band"; else null. sinceS = the start of the deviation (&lt;= 0 = none), kept by the caller between calls.
        /// </summary>
        public static string BarrierReset(double bandAxisDeg, double axisNowDeg, double yShipNm, double halfXNm, double r, double now, double deg, double holdS, ref double sinceS)
        {
            string res = null;
            if (Math.Abs(AswMath.AngleDiff(bandAxisDeg, axisNowDeg)) >= deg - 1e-9)
            {
                if (sinceS <= 0) sinceS = Math.Max(now, 1e-6);
                if (now - sinceS >= holdS - 1e-9) res = "turn";
            }
            else sinceS = 0;
            if (res == null && Math.Abs(yShipNm) > halfXNm - r + 1e-9) res = "off_band";
            return res;
        }

        /// <summary>Rows of the plan the buoys cover: whole rows, the nearest first (seq firstSeq, firstSeq + 1 ...; even rows take `even` buoys, odd rows `odd`).</summary>
        public static int BarrierRowsAffordable(int rows, int buoys, int even, int odd, int firstSeq)
        {
            int k = 0, left = Math.Max(0, buoys);
            for (; k < rows; k++)
            {
                int need = ((firstSeq + k) & 1) == 0 ? even : odd;
                if (need > left) break;
                left -= need;
            }
            return k;
        }

        /// <summary>The row may be laid now: the ship reaches it and the buoy lives on (aheadNm / v + fly &lt;= 0.8 life); an unknown speed or life = yes.</summary>
        public static bool BarrierLifeOk(double aheadNm, double vKn, double lifeS, double flyS)
        {
            if (vKn <= 0 || lifeS <= 0) return true;
            return Math.Max(0, aheadNm) / vKn * 3600.0 + flyS <= 0.8 * lifeS + 1e-9;
        }

        /// <summary>1.6 R_dip rounded down to {1, 2, 4, 6, 8} nm.</summary>
        public static double DipStepV3(double rDipNm)
        {
            double s = 1.6 * rDipNm, best = DipStepsV3[0];
            foreach (double c in DipStepsV3) if (c <= s + 1e-9) best = c;
            return best;
        }

        /// <summary>Next dip x: the farthest taken + step, not closer than xMin; beyond xMax -> hold = true and the farthest taken x (repeat in place).</summary>
        public static double LeapfrogNext(IList<double> takenX, double stepNm, double xMin, double xMax, out bool hold)
        {
            hold = false;
            bool any = takenX != null && takenX.Count > 0;
            double far = double.NegativeInfinity;
            if (any) foreach (double x in takenX) far = Math.Max(far, x);
            double next = any ? Math.Max(far + stepNm, xMin) : xMin;
            if (next > xMax + 1e-9) { hold = true; return any ? far : xMax; }
            return next;
        }

        /// <summary>Ring of a fixed target (v &lt; 3 kn): points every <= stepNm along the circle, starting north.</summary>
        public static List<NmPt> StandingRing(double radiusNm, double stepNm)
        {
            var res = new List<NmPt>();
            if (radiusNm <= 0) return res;
            int n = stepNm > 0 ? Math.Max(3, (int)Math.Ceiling(2 * Math.PI * radiusNm / stepNm - 1e-9)) : 8;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                res.Add(new NmPt(radiusNm * Math.Cos(a), radiusNm * Math.Sin(a)));
            }
            return res;
        }

        /// <summary>Depth "auto": corridor / barrier -> below the layer; area, cross, water &lt; 500 ft (no layer) and dip only -> alternate.</summary>
        public static string DepthAuto(IList<string> tactics, string order, double waterFt)
        {
            if (order == "area" || order == "cross") return "alternate";
            if (waterFt > 0 && waterFt < 500) return "alternate";
            if (tactics != null && (tactics.Contains("corridor") || tactics.Contains("barrier"))) return "below";
            return "alternate";
        }

        // T-334 / ADR-0130: ETA filter and near-first order.
        public const double AheadMarginNm = 1.0;   // a point is laid only while the ship is still >= 1 nm short of it at the drop
        public const double DropPointS = 5.0;      // allowance per drop point (s) on top of the flight time
        public const double ColumnTolNm = 0.1;     // points within this along-track distance form one column / row

        /// <summary>Speed of the ship along the axis: v cos(course - axis), never below 0.</summary>
        public static double AlongKn(double speedKn, double courseDeg, double axisDeg)
        {
            double a = AswMath.AngleDiff(axisDeg, courseDeg) * Math.PI / 180.0;
            return Math.Max(0, speedKn * Math.Cos(a));
        }

        /// <summary>Slack of a point at the drop: xPt - (xShip + vAlong * etaS / 3600) (nm).</summary>
        public static double SlackNm(double xPtNm, double xShipNm, double vAlongKn, double etaS)
        {
            return xPtNm - (xShipNm + vAlongKn * etaS / 3600.0);
        }

        /// <summary>
        /// Near-first order (indices into pts): columns by ascending X (|dX| &lt;= colTolNm = one column); inside a column by Y, starting from the end nearer to
        /// the current position (from, then the last point of the previous column). Deterministic (ties: the smaller Y first).
        /// </summary>
        public static List<int> NearFirstOrder(NmPt from, IList<NmPt> pts, double colTolNm = ColumnTolNm)
        {
            var res = new List<int>();
            if (pts == null || pts.Count == 0) return res;
            var idx = new List<int>();
            for (int i = 0; i < pts.Count; i++) idx.Add(i);
            idx.Sort((a, b) => { int c = pts[a].X.CompareTo(pts[b].X); return c != 0 ? c : (pts[a].Y.CompareTo(pts[b].Y) != 0 ? pts[a].Y.CompareTo(pts[b].Y) : a.CompareTo(b)); });
            NmPt cur = from;
            int k = 0;
            while (k < idx.Count)
            {
                double x0 = pts[idx[k]].X;
                var col = new List<int>();
                while (k < idx.Count && pts[idx[k]].X - x0 <= colTolNm + 1e-9) col.Add(idx[k++]);
                col.Sort((a, b) => { int c = pts[a].Y.CompareTo(pts[b].Y); return c != 0 ? c : a.CompareTo(b); });
                double dLow = Dist(cur, pts[col[0]]), dHigh = Dist(cur, pts[col[col.Count - 1]]);
                if (dHigh < dLow - 1e-9) col.Reverse();
                res.AddRange(col);
                cur = pts[col[col.Count - 1]];
            }
            return res;
        }

        /// <summary>
        /// Greedy pass along the given path: a point is kept when its slack at the ETA (flight from the last kept point at airKn + perPointS each) is >= marginNm;
        /// a skipped point does not move the aircraft. Returns the kept indices in path order; endS = ETA of the last kept point (0 = none).
        /// </summary>
        public static List<int> AheadKeep(NmPt from, IList<NmPt> path, double airKn, double perPointS, double xShipNm, double vAlongKn, double marginNm, out double endS)
        {
            var kept = new List<int>();
            endS = 0;
            if (path == null) return kept;
            NmPt cur = from; double t = 0;
            for (int i = 0; i < path.Count; i++)
            {
                double eta = t + (airKn > 0 ? Dist(cur, path[i]) / airKn * 3600.0 : 0) + perPointS;
                if (SlackNm(path[i].X, xShipNm, vAlongKn, eta) < marginNm - 1e-9) continue;
                kept.Add(i); cur = path[i]; t = eta; endS = eta;
            }
            return kept;
        }

        /// <summary>Row take rule of the barrier: kept > 0 and 2 * kept >= taken.</summary>
        public static bool RowTakeOk(int kept, int taken) { return kept > 0 && 2 * kept >= taken; }

        private static string SelfTestCourse()
        {
            bool tw;
            var rows = CorridorRows(20, 2.70, 6, out tw);
            if (tw || rows.Length != 3 || Math.Abs(rows[0] - 2.16) > 1e-6 || Math.Abs(rows[1] - 5.0) > 1e-6 || Math.Abs(rows[2] - 7.84) > 1e-6) return "rows x20";
            double step; var line = BarrierLine(40, 2.70, out step);
            if (line.Count != 10 || Math.Abs(step - 3.9644) > 1e-3) return "barrier line " + line.Count + " " + step;
            if (DipStepV3(2.1) != 2) return "dip step";
            var rp = RowPriority(3);
            if (rp.Count != 3 || rp[0] != 0 || rp[1] != 2 || rp[2] != 1) return "row priority";
            if (Math.Abs(AlongKn(15, 150, 90) - 7.5) > 1e-6 || AlongKn(15, 180, 90) > 1e-9) return "along";
            var lad = new List<NmPt> { new NmPt(3, 0), new NmPt(3, 2) };
            var ord = NearFirstOrder(new NmPt(0, 3), lad);
            if (ord.Count != 2 || ord[0] != 1) return "near first";
            double ends; if (AheadKeep(new NmPt(0, 0), lad, 100, DropPointS, 0, 15, AheadMarginNm, out ends).Count != 2 || ends <= 0) return "ahead keep";
            if (!RowTakeOk(5, 10) || RowTakeOk(4, 10) || RowTakeOk(0, 0)) return "row take";
            return null;
        }
    }
}
