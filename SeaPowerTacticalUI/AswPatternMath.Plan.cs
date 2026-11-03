using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 PL / ADR-0076 s.4-5, ADR-0072 s.3: lanes (weights, assignment, splits), anchor and re-anchor, order of points, coverage, summary, migration.
    internal static partial class AswPatternMath
    {
        /// <summary>Weight of an aircraft: speed (&lt;= 0 -> 100 kn) x min(remain, 3600 s).</summary>
        public static double Weight(double speedKn, double remainS)
        {
            return (speedKn <= 0 ? 100.0 : speedKn) * Math.Min(Math.Max(0, remainS), 3600.0);
        }

        private static int TacticRank(string t)
        {
            switch (t)
            {
                case "dip": return 0;
                case "corridor": case "grid": return 1;
                case "barrier": case "mad": return 2;
                default: return 3;
            }
        }

        private static bool Fits(LaneMember m, string tactic)
        {
            switch (tactic)
            {
                case "dip": return m.Dip;
                case "corridor": case "barrier": case "grid": return m.Buoys && m.BuoyCount > 0;
                case "mad": return m.Mad;
                default: return false;
            }
        }

        private static int Capabilities(LaneMember m) { return (m.Dip ? 1 : 0) + (m.Buoys ? 1 : 0) + (m.Mad ? 1 : 0); }

        /// <summary>
        /// Aircraft -> tactic. Order dip > corridor > barrier (area: dip > grid > mad). Every tactic gets one aircraft first (a specialist if possible),
        /// the rest go to the fitting tactic with the least weight so far. An aircraft that fits nothing is absent from the result.
        /// </summary>
        public static Dictionary<int, string> AssignLanes(IList<LaneMember> ms, IList<string> tactics)
        {
            var res = new Dictionary<int, string>();
            if (ms == null || tactics == null || tactics.Count == 0) return res;
            var order = new List<string>(tactics);
            order.Sort((a, b) => TacticRank(a).CompareTo(TacticRank(b)));
            var load = new Dictionary<string, double>();
            foreach (string t in order) load[t] = 0;
            var used = new bool[ms.Count];
            foreach (string t in order)
            {
                int best = -1;
                for (int i = 0; i < ms.Count; i++)
                {
                    if (used[i] || !Fits(ms[i], t)) continue;
                    if (best < 0) { best = i; continue; }
                    int ci = Capabilities(ms[i]), cb = Capabilities(ms[best]);
                    if (ci < cb || (ci == cb && Weight(ms[i].SpeedKn, ms[i].RemainS) > Weight(ms[best].SpeedKn, ms[best].RemainS) + 1e-9)) best = i;
                }
                if (best < 0) continue;
                used[best] = true; res[ms[best].Uid] = t; load[t] += Weight(ms[best].SpeedKn, ms[best].RemainS);
            }
            for (int i = 0; i < ms.Count; i++)
            {
                if (used[i]) continue;
                string pick = null;
                foreach (string t in order)
                    if (Fits(ms[i], t) && (pick == null || load[t] < load[pick] - 1e-9)) pick = t;
                if (pick == null) continue;
                res[ms[i].Uid] = pick; load[pick] += Weight(ms[i].SpeedKn, ms[i].RemainS);
            }
            return res;
        }

        /// <summary>
        /// Splits the long side into strips {from, to} (0..longNm) proportional to w, each not narrower than minWidthNm; if some would be narrower,
        /// the lightest aircraft is dropped (fewer strips). <paramref name="used"/>: indices into w of the aircraft that got a strip, in w order.
        /// </summary>
        public static List<double[]> SplitRect(double longNm, double shortNm, IList<double> w, double minWidthNm, out List<int> used)
        {
            used = new List<int>();
            var res = new List<double[]>();
            if (w == null || w.Count == 0) return res;
            for (int i = 0; i < w.Count; i++) used.Add(i);
            while (used.Count > 1)
            {
                double sum = 0; foreach (int i in used) sum += Math.Max(0, w[i]);
                bool tooNarrow = false;
                foreach (int i in used)
                {
                    double wd = sum > 0 ? longNm * Math.Max(0, w[i]) / sum : longNm / used.Count;
                    if (wd < minWidthNm - 1e-9) { tooNarrow = true; break; }
                }
                if (!tooNarrow) break;
                int lightest = used[used.Count - 1];
                foreach (int i in used) if (w[i] < w[lightest] - 1e-9) lightest = i;
                used.Remove(lightest);
            }
            double total = 0; foreach (int i in used) total += Math.Max(0, w[i]);
            double at = 0;
            for (int k = 0; k < used.Count; k++)
            {
                double wd = total > 0 ? longNm * Math.Max(0, w[used[k]]) / total : longNm / used.Count;
                double to = k == used.Count - 1 ? longNm : at + wd;
                res.Add(new[] { at, to });
                at = to;
            }
            return res;
        }

        public static List<double[]> SplitRect(double longNm, double shortNm, IList<double> w, double minWidthNm)
        {
            List<int> used;
            return SplitRect(longNm, shortNm, w, minWidthNm, out used);
        }

        /// <summary>Splits a line 0..length into segments {from, to} proportional to w (all weights 0 -> equal).</summary>
        public static List<double[]> SplitLine(double lengthNm, IList<double> w)
        {
            var res = new List<double[]>();
            if (w == null || w.Count == 0) return res;
            double sum = 0; foreach (double x in w) sum += Math.Max(0, x);
            double at = 0;
            for (int i = 0; i < w.Count; i++)
            {
                double wd = sum > 0 ? lengthNm * Math.Max(0, w[i]) / sum : lengthNm / w.Count;
                double to = i == w.Count - 1 ? lengthNm : at + wd;
                res.Add(new[] { at, to });
                at = to;
            }
            return res;
        }

        /// <summary>Corridor side of aircraft #index of K: K = 1 -> 0 (loop), K = 2 -> -1 / +1, K &gt;= 3 -> the side whose cursor lags (a tie: by index parity).</summary>
        public static int CorridorSide(int k, int index, double cursorLeftX, double cursorRightX)
        {
            if (k <= 1) return 0;
            if (k == 2) return index % 2 == 0 ? -1 : 1;
            if (Math.Abs(cursorLeftX - cursorRightX) <= 1e-9) return index % 2 == 0 ? -1 : 1;
            return cursorLeftX < cursorRightX ? -1 : 1;
        }

        /// <summary>Dead-reckoned origin: O + v (now - t0) along axis0 (a fixed target does not move).</summary>
        public static GeoPt Lead(AswAnchor a, double now)
        {
            if (a.Stand || a.VKn <= 0) return a.O;
            return AswMath.Offset(a.O, a.AxisDeg, a.VKn * Math.Max(0, now - a.T0) / 3600.0);
        }

        /// <summary>
        /// Anchor check. First call sets the anchor (null). Otherwise: stand (v &lt; standKn flips), turn (axis off by reanchorDeg for holdS),
        /// speed (+-30 %), drift (cross-track &gt; driftNm). On a reason the anchor is rebuilt at posNow and the reason is returned.
        /// TurnSinceS (when the deviation began) lives in the anchor between calls.
        /// </summary>
        public static string Reanchor(ref AswAnchor a, double axisNow, double vNow, GeoPt posNow, double now, double reanchorDeg, double holdS, double driftNm, double standKn = 3)
        {
            bool standNow = vNow < standKn;
            if (!a.Set)
            {
                a = new AswAnchor { Set = true, Stand = standNow, O = posNow, AxisDeg = axisNow, T0 = now, VKn = vNow, TurnSinceS = 0 };
                return null;
            }
            string why = null;
            if (standNow != a.Stand) why = "stand";
            if (why == null && !standNow)
            {
                double dev = Math.Abs(AswMath.AngleDiff(axisNow, a.AxisDeg));
                if (dev >= reanchorDeg)
                {
                    if (a.TurnSinceS <= 0) a.TurnSinceS = Math.Max(now, 1e-6);
                    else if (now - a.TurnSinceS >= holdS - 1e-9) why = "turn";
                }
                else a.TurnSinceS = 0;
            }
            if (why == null && !standNow && !a.Stand && a.VKn > 0 && Math.Abs(vNow - a.VKn) > 0.3 * a.VKn + 1e-9) why = "speed";
            if (why == null)
            {
                double off = a.Stand ? AswMath.DistNm(a.O, posNow) : Math.Abs(AswMath.ToLocal(a.O, a.AxisDeg, posNow).Y);
                if (off > driftNm + 1e-9) why = "drift";
            }
            if (why != null) a = new AswAnchor { Set = true, Stand = standNow, O = posNow, AxisDeg = axisNow, T0 = now, VKn = vNow, TurnSinceS = 0 };
            return why;
        }

        /// <summary>A package in flight survives a turn of the axis below 45 degrees.</summary>
        public static bool KeepPacket(double dAxisDeg) { return Math.Abs(dAxisDeg) < 45; }

        /// <summary>T-260 / ADR-0104: upper bound of the frame hold after the mod stops steering (s); the effective one is max(PostSteerMaxS, ReanchorHoldS).</summary>
        public const double PostSteerMaxS = 180;

        /// <summary>T-260: what the frame holds after an evasion. HoldAxis / HoldSpeed: use PlanAxis / PlanV; End: null while anything holds, else returned | new_route | timeout.</summary>
        public struct AswPostSteer { public bool HoldAxis, HoldSpeed; public string End; }

        /// <summary>
        /// T-260 / ADR-0104: endS &lt; 0 = no hold (End = null, nothing held). Target axis = routeAxisDeg ?? planAxisDeg; the axis holds while |headingDeg - target| &gt;= reanchorDeg,
        /// the speed while |vKn - planVKn| &gt; 30 % planVKn; both released or now - endS &gt;= max(maxS, holdS) ends it.
        /// </summary>
        public static AswPostSteer PostSteer(double endS, double now, double maxS, double holdS, double headingDeg, double? routeAxisDeg, double planAxisDeg, double vKn, double planVKn, double reanchorDeg)
        {
            var r = new AswPostSteer();
            if (endS < 0) return r;
            double target = routeAxisDeg ?? planAxisDeg;
            r.HoldAxis = Math.Abs(AswMath.AngleDiff(headingDeg, target)) >= reanchorDeg - 1e-9;
            r.HoldSpeed = Math.Abs(vKn - planVKn) > 0.3 * planVKn + 1e-9;
            bool timeout = now - endS >= Math.Max(maxS, holdS) - 1e-9;
            if (timeout) { r.End = "timeout"; r.HoldAxis = false; r.HoldSpeed = false; }
            else if (!r.HoldAxis && !r.HoldSpeed) r.End = Math.Abs(AswMath.AngleDiff(target, planAxisDeg)) < reanchorDeg - 1e-9 ? "returned" : "new_route";
            return r;
        }

        /// <summary>Order of loose points: nearest neighbour from <paramref name="from"/> (at most max points), then 2-opt with the start fixed. Deterministic.</summary>
        public static List<NmPt> OrderPoints(NmPt from, IList<NmPt> pts, int max = 40)
        {
            var left = new List<NmPt>();
            if (pts == null) return left;
            left.AddRange(pts);
            var nn = new List<NmPt>(); NmPt cur = from;
            while (left.Count > 0 && nn.Count < max)
            {
                int bi = 0; double bd = double.MaxValue;
                for (int i = 0; i < left.Count; i++) { double d = Dist(cur, left[i]); if (d < bd - 1e-9) { bd = d; bi = i; } }
                cur = left[bi]; nn.Add(cur); left.RemoveAt(bi);
            }
            var ord = TwoOpt(from, nn);
            var res = new List<NmPt>(nn.Count);
            foreach (int i in ord) res.Add(nn[i]);
            return res;
        }

        /// <summary>Lower bound of a path through n points spaced d apart: (n - 1) d.</summary>
        public static double LowerBoundNm(int n, double d) { return Math.Max(0, n - 1) * d; }

        /// <summary>A node counts as covered when a live buoy stands near and its life lasts for the pass plus the lead (area: passInS = 0).</summary>
        public static bool NodeCovered(bool buoyNear, double remainS, double passInS, double leadS)
        {
            return buoyNear && remainS >= passInS + leadS - 1e-9;
        }

        /// <summary>Order summary for the window: rows, lattice, buoys per hour, how long the buoys last, a warning.</summary>
        public static AswSummary Summary(AswSummaryIn s)
        {
            var o = new AswSummary();
            if (s == null) return o;
            double v = Math.Max(0, s.VKn);
            if (s.Order == "area")
            {
                double r = s.Tactic == "dip" && s.RDip > 0 ? s.RDip : s.R;
                double sn, se;
                var lat = Lattice(s.AreaNorthNm, s.AreaEastNm, r, out o.Nx, out o.Ny, out sn, out se);
                o.Nodes = lat.Count; o.Buoys = lat.Count; o.StepNm = Math.Max(sn, se);
                if (s.Tactic == "mad") { o.Nodes = 0; o.Buoys = 0; return o; }
                if (s.Tactic == "dip") { o.Buoys = 0; return o; }
                o.BuoysPerHour = o.StepNm > 0 ? v / o.StepNm : 0;
                if (s.Buoys > 0 && s.Buoys < o.Nodes)
                {
                    o.Warn = "buoys_short";
                    o.HoursLeft = o.BuoysPerHour > 0 ? s.Buoys / o.BuoysPerHour : -1;
                }
                return o;
            }
            switch (s.Tactic)
            {
                case "corridor":
                {
                    bool tooWide;
                    var rows = CorridorRows(s.XNm, s.R, s.MaxRows, out tooWide);
                    int cols = Columns(s.R, s.LNm).Count;
                    o.RowsMax = rows.Length; o.RowsSide = rows.Length; o.StepNm = 1.6 * s.R;
                    o.Nodes = cols * rows.Length * 2;
                    if (s.Buoys > 0 && s.Buoys < o.Nodes) o.RowsSide = RowsAffordable(rows.Length, s.Buoys, 2 * cols);
                    o.Buoys = o.RowsSide * 2 * cols;
                    o.BuoysPerHour = o.StepNm > 0 ? o.RowsSide * 2 * v / o.StepNm : 0;
                    if (tooWide) o.Warn = "corridor_too_wide";
                    else if (s.Buoys > 0 && s.Buoys < o.Nodes)
                    {
                        o.Warn = "buoys_short";
                        o.HoursLeft = o.BuoysPerHour > 0 ? s.Buoys / (rows.Length * 2 * v / o.StepNm) : -1;
                    }
                    break;
                }
                case "barrier":
                {
                    // T-229: the sliding barrier: Rows rows, the first one Gap from the ship, the buoys per hour = one row per Gap of the way
                    double step;
                    int rows = Math.Max(1, s.Rows), even = BarrierRowYs(s.XNm, s.R, 0, s.Stagger, out step).Count, odd = BarrierRowYs(s.XNm, s.R, 1, s.Stagger, out step).Count;
                    double st0; BarrierLine(s.XNm, s.R, out st0);
                    int total = 0; for (int k2 = 0; k2 < rows; k2++) total += (k2 & 1) == 0 ? even : odd;
                    o.RowsPlan = rows; o.RowsMax = rows; o.RowsSide = rows; o.BuoysEven = even; o.BuoysOdd = odd;
                    o.Nodes = total; o.Buoys = total; o.StepNm = st0;
                    o.BuoysPerHour = s.GapNm > 0 ? (even + odd) / 2.0 * v / s.GapNm : 0;
                    o.RowsAffordable = s.Buoys > 0 ? BarrierRowsAffordable(rows, s.Buoys, even, odd, 0) : rows;
                    bool lifeShort = s.LifeS > 0 && !BarrierLifeOk(rows * s.GapNm, v, s.LifeS, s.FlyS);
                    if (lifeShort) o.Warn = "life_short";
                    else if (s.Buoys > 0 && s.Buoys < total)
                    {
                        o.Warn = o.RowsAffordable >= 1 && rows > 1 ? "barrier_rows_short" : "buoys_short";
                        o.HoursLeft = o.BuoysPerHour > 0 ? s.Buoys / o.BuoysPerHour : -1;
                    }
                    break;
                }
                case "dip":
                {
                    o.StepNm = DipStepV3(s.RDip);
                    o.Nodes = o.StepNm > 0 ? Math.Max(0, (int)Math.Floor((s.LNm - Math.Max(2, o.StepNm)) / o.StepNm + 1e-9) + 1) : 0;
                    break;
                }
            }
            return o;
        }

        private static double CeilTo10(double v) { return Math.Min(100, Math.Max(20, Math.Ceiling(v / 10.0 - 1e-9) * 10)); }

        /// <summary>
        /// ADR-0076 s.9: an old order (rows | cross | mad | dip) -> v3 spec. rows -> corridor (X 20, L from lead), cross -> barrier (X from the old width, L 40),
        /// mad -> corridor with buoys, otherwise removed (madRemoved), dip -> dip (L from leadDip). New ids pass through.
        /// </summary>
        public static AswCourseSpec MigrateCourse(IList<string> oldTactics, double leadNm, double leadDipNm, double widthNm, bool hasBuoys, out bool madRemoved)
        {
            madRemoved = false;
            var spec = new AswCourseSpec();
            var set = new HashSet<string>();
            if (oldTactics != null)
                foreach (string t in oldTactics)
                {
                    switch ((t ?? "").Trim().ToLowerInvariant())
                    {
                        case "rows": case "corridor": set.Add("corridor"); break;
                        case "cross": case "barrier": set.Add("barrier"); break;
                        case "dip": set.Add("dip"); break;
                        case "mad": if (hasBuoys) set.Add("corridor"); else madRemoved = true; break;
                    }
                }
            if (set.Contains("corridor") && set.Contains("barrier")) set.Remove("corridor");   // T-334 / ADR-0130: rows + cross -> barrier
            foreach (string t in CourseOrder) if (set.Contains(t)) spec.Tactics.Add(t);
            if (set.Contains("corridor")) { spec.CorridorXNm = 20; spec.CorridorLNm = CeilTo10(leadNm); }
            if (set.Contains("barrier")) { spec.BarrierXNm = widthNm > 0 ? Snap(widthNm, WidthList) : 40; spec.BarrierLNm = 40; }
            if (set.Contains("dip")) spec.DipLNm = CeilTo10(leadDipNm);
            return spec;
        }

        /// <summary>Old area pattern -> v3: rows | cross | buoys -> grid; dip, mad, grid, auto as is; unknown -> auto.</summary>
        public static string MigrateArea(string oldPattern)
        {
            switch ((oldPattern ?? "").Trim().ToLowerInvariant())
            {
                case "rows": case "cross": case "buoys": case "grid": return "grid";
                case "dip": return "dip";
                case "mad": return "mad";
                default: return "auto";
            }
        }

        private static string SelfTestPlan()
        {
            if (Weight(0, 7200) != 360000) return "weight";
            if (!KeepPacket(44) || KeepPacket(-50)) return "keep packet";
            var ps = PostSteer(100, 130, 180, 60, 37, 167, 167, 12, 12, 30);
            if (!ps.HoldAxis || ps.HoldSpeed || ps.End != null) return "post-steer holds";
            ps = PostSteer(100, 130, 180, 60, 150, 167, 167, 12, 12, 30);
            if (ps.End != "returned") return "post-steer returned " + ps.End;
            ps = PostSteer(100, 130, 180, 60, 60, 60, 167, 12, 12, 30);
            if (ps.End != "new_route") return "post-steer new_route " + ps.End;
            ps = PostSteer(100, 281, 180, 60, 37, 167, 167, 12, 12, 30);
            if (ps.End != "timeout") return "post-steer timeout " + ps.End;
            ps = PostSteer(100, 270, 180, 60, 37, 167, 167, 12, 12, 30);
            if (ps.End != null || !ps.HoldAxis) return "post-steer still holds at 170 s";
            ps = PostSteer(100, 130, 180, 60, 170, null, 167, 12, 12, 30);
            if (ps.End != "returned") return "post-steer no route " + ps.End;
            ps = PostSteer(100, 130, 180, 60, 160, 167, 167, 25, 12, 30);
            if (ps.HoldAxis || !ps.HoldSpeed || ps.End != null) return "post-steer speed";
            ps = PostSteer(-1, 130, 180, 60, 20, 167, 167, 25, 12, 30);
            if (ps.HoldAxis || ps.HoldSpeed || ps.End != null) return "post-steer none";
            if (PostSteer(100, 281, 180, 300, 37, 167, 167, 12, 12, 30).End != null) return "post-steer long holdS early";
            if (PostSteer(100, 401, 180, 300, 37, 167, 167, 12, 12, 30).End != "timeout") return "post-steer long holdS";
            bool mad;
            var sp = MigrateCourse(new List<string> { "rows", "dip" }, 8, 8, 0, true, out mad);
            if (mad || sp.Tactics.Count != 2 || sp.Tactics[0] != "dip" || sp.CorridorLNm != 20 || sp.DipLNm != 20) return "migrate rows+dip";
            if (MigrateArea("rows") != "grid") return "migrate area";
            return null;
        }
    }
}
