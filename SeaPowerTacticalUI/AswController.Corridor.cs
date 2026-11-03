using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-176 C6 / ADR-0076 s.3.2: the corridor X by course. Rows on both sides of the axis, columns every 1.6R, the meander through the columns.
    // The lattice is fixed on the ground (the anchor of the axis); a node counts as laid when a live buoy that lasts long enough covers it.
    internal sealed partial class AswController
    {
        private sealed class CNode { public int Col, Row, Side; public double X, Y; public GeoPt P; }

        /// <summary>Distance the group has gone along the anchor axis since the anchor was set (nm); a fixed target does not move.</summary>
        private double AlongNm(Grp g)
        {
            var a = g.Plan.Anchor;
            return a.Stand || a.VKn <= 0 ? 0 : a.VKn * Math.Max(0, _now - a.T0) / 3600.0;
        }

        /// <summary>The group's speed for the dead reckoning of the lead point (the anchor's; a fixed target 0).</summary>
        private static double AnchorKn(AswAnchor a) { return a.Stand ? 0 : a.VKn; }

        /// <summary>Seconds the aircraft needs to the lead point of the group.</summary>
        private double FlyToLeadS(Grp g, AswMember m)
        {
            if (m.Lat == 0 && m.Lon == 0) return 0;
            return AswMath.DistNm(new GeoPt(m.Lat, m.Lon), AswPatternMath.Lead(g.Plan.Anchor, _now)) / SpeedOf(m) * 3600;
        }

        /// <summary>Loiter allowance of the life check: the time to fly the package itself.</summary>
        private const double PackageLoiterS = 600;

        /// <summary>
        /// The corridor nodes of the window still open for this aircraft. Window: from 1 nm ahead of the lead point at the drop (the group has gone on by then) to the standoff L ahead of the
        /// group now. A node a live buoy covers (long enough for the group to pass it and a reserve, AswPatternMath.NodeCovered) is skipped; one the buoy could not outlast is left out
        /// (life_short). rowsKept = the rows (by index) that are laid, in priority order (buoys short).
        /// </summary>
        private List<CNode> CorridorNodes(Grp g, AswMember m, double r, BuoyType bt, int avail, out int rowsTotal, out int rowsLaid, out bool tooWide, out bool lifeShort)
        {
            var spec = g.Plan.Course;
            var a = g.Plan.Anchor;
            double d = 1.6 * r, v = AnchorKn(a), s = AlongNm(g), fly = FlyToLeadS(g, m), sDrop = s + v * fly / 3600.0;
            double lo = sDrop + 1.0, hi = s + spec.CorridorLNm;
            var rows = AswPatternMath.CorridorRows(spec.CorridorXNm, r, _defaults.Pat.CorridorMaxRows, out tooWide);
            rowsTotal = rows.Length; lifeShort = false;
            var res = new List<CNode>();
            var cand = new List<CNode>();
            double cap = PatternCapGap(r, rows);
            if (a.Stand)
            {
                // a fixed target: the ring of radius X/2 (one level of nodes), points every d
                var ring = AswPatternMath.StandingRing(Math.Max(spec.CorridorXNm / 2, d), d);
                for (int k = 0; k < ring.Count; k++)
                {
                    var p = AswMath.ToGeo(a.O, 0, ring[k]);
                    if (!CoveredAt(g, p, r, 0, cap)) cand.Add(new CNode { Col = k, Row = 0, Side = 0, X = 0, Y = 0, P = p });
                }
                rowsLaid = 1;
                return cand;
            }
            int jmin = (int)Math.Ceiling((lo - 3.0) / d - 1e-9), jmax = (int)Math.Floor((hi - 3.0) / d + 1e-9);
            if (jmin < 0) jmin = 0;
            for (int j = jmin; j <= jmax; j++)
            {
                double x = 3.0 + j * d;
                if (!AswPatternMath.LifeOk(x - sDrop, v, bt.LifeS > 0 ? bt.LifeS : 28800, fly, PackageLoiterS)) { lifeShort = true; continue; }
                double passInS = v > 0 ? Math.Max(0, (x - s) / v * 3600.0) : 0;
                for (int side = -1; side <= 1; side += 2)
                    for (int row = 0; row < rows.Length; row++)
                    {
                        var p = AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x, side * rows[row]));
                        if (CoveredAt(g, p, r, passInS, cap)) continue;
                        cand.Add(new CNode { Col = j, Row = row, Side = side, X = x, Y = side * rows[row], P = p });
                    }
            }
            // buoys short: the rows are laid in the order inner, outer wall, the middle ones; as many as the buoys of the group pay for
            int budget = 0;
            foreach (int u in g.Members)
            {
                AswMember hm; string t;
                if (_live.TryGetValue(u, out hm) && g.Assign.TryGetValue(u, out t) && t == "corridor") budget += Math.Max(0, AvailFor(hm, bt));
            }
            if (budget < avail) budget = avail;
            var prio = AswPatternMath.RowPriority(rows.Length);
            var perRow = new int[rows.Length];
            foreach (var n in cand) perRow[n.Row]++;
            var keep = new HashSet<int>(); int sum = 0;
            foreach (int row in prio)
            {
                if (sum + perRow[row] > budget && keep.Count > 0) break;
                // the first row is always kept, even when it alone exceeds the budget (the package then lays part of it)
                keep.Add(row); sum += perRow[row];
            }
            rowsLaid = keep.Count;
            foreach (var n in cand) if (keep.Contains(n.Row)) res.Add(n);
            return res;
        }

        /// <summary>The merge gap of a designed pattern: the usual gap of ADR-0063, but never more than 0.9 of the closest neighbours of the pattern (rows can stand closer than the gap).</summary>
        private static double PatternCapGap(double r, double[] rows)
        {
            double step = 1.6 * r;
            for (int i = 1; i < rows.Length; i++) step = Math.Min(step, rows[i] - rows[i - 1]);
            return 0.9 * step;
        }

        /// <summary>Meander order of one column of one side: even columns inner -> outer, odd ones outer -> inner.</summary>
        private static void MeanderSort(List<CNode> col, int j)
        {
            col.Sort((x, y) => (j % 2 == 0) ? x.Row.CompareTo(y.Row) : y.Row.CompareTo(x.Row));
        }

        // ---- T-334 / ADR-0130: the ETA filter ("no buoy behind the ship") and the check of a package in flight ----------------------------------------

        private double _lastMinSlack;
        private readonly Dictionary<int, double> _aheadAt = new Dictionary<int, double>();
        private const double AheadCheckEveryS = 10, AheadCheckAfterS = 30;
        /// <summary>A package in flight is cancelled only when a point is later than this margin (the plan keeps 1 nm): a weaving escort must not make the aircraft thrash.</summary>
        private const double AheadCheckMarginNm = 0.5;

        /// <summary>The aircraft (the ship when its position is unknown) in the frame (o, axisDeg).</summary>
        private NmPt LocalOf(AswMember m, AswTarget t, GeoPt o, double axisDeg)
        {
            var p = m.Lat == 0 && m.Lon == 0 ? Pos(t) : new GeoPt(m.Lat, m.Lon);
            return AswMath.ToLocal(o, axisDeg, p);
        }

        /// <summary>
        /// The points of the path (in flight order) that are still ahead of the ship when the aircraft drops them: the ship goes on along the axis at its present course and speed.
        /// Indices into path; xShip = the ship's x in the frame now; the least slack of the kept points goes to _lastMinSlack.
        /// </summary>
        private List<int> AheadFilter(Grp g, AswMember m, AswTarget t, GeoPt o, double axisDeg, IList<GeoPt> path, out double xShip, double marginNm = AswPatternMath.AheadMarginNm)
        {
            xShip = AswMath.ToLocal(o, axisDeg, Pos(t)).X;
            double v = AswPatternMath.AlongKn(t.SpeedKn, t.CourseDeg, axisDeg), air = SpeedOf(m);
            var loc = new List<NmPt>(path.Count);
            foreach (var p in path) loc.Add(AswMath.ToLocal(o, axisDeg, p));
            var from = LocalOf(m, t, o, axisDeg);
            double end;
            var kept = AswPatternMath.AheadKeep(from, loc, air, AswPatternMath.DropPointS, xShip, v, marginNm, out end);
            double min = double.MaxValue, tt = 0; var cur = from;
            foreach (int i in kept)
            {
                double dx = loc[i].X - cur.X, dy = loc[i].Y - cur.Y;
                tt += Math.Sqrt(dx * dx + dy * dy) / air * 3600.0 + AswPatternMath.DropPointS; cur = loc[i];
                min = Math.Min(min, AswPatternMath.SlackNm(loc[i].X, xShip, v, tt));
            }
            _lastMinSlack = min == double.MaxValue ? 0 : min;
            return kept;
        }

        private void LogAhead(Grp g, AswMember m, int kept, int planned, double xShip)
        {
            _game.Log("asw pattern " + g.Name + " ahead uid=" + I(m.Uid) + " kept=" + I(kept) + "/" + I(planned) + " skipped=" + I(planned - kept) + " min_slack=" + N(Math.Round(_lastMinSlack, 2)));
        }

        /// <summary>
        /// A package of the corridor / barrier in flight: the points that have not fallen yet are checked again with the ship's present course and speed. A point that has become late
        /// -> the route is cancelled (the fallen buoys stay, the rest goes back to its node / row) and the next tick lays a new package. At most every 10 s, not in the first 30 s of a package.
        /// </summary>
        private void AheadCheck(Grp g, AswMember m, AswTarget t, string tac)
        {
            Job j;
            if (t == null || !g.Plan.Anchor.Set || g.Plan.Anchor.Stand || !g.Jobs.TryGetValue(m.Uid, out j) || !j.IsDrop || j.Recs == null || j.Recs.Count == 0 || _now >= j.DoneAt) return;
            if (j.Tactic != tac && j.Tactic != "dump") return;
            if (_game.TimeScale > 1.0) return;   // time compression: the game may not fly the package at all (tc_blocked, JobPending), the check would cut it again and again
            double last;
            if (_aheadAt.TryGetValue(m.Uid, out last) && _now - last < AheadCheckEveryS) return;
            if (_now - j.Recs[0].DroppedS < AheadCheckAfterS) return;
            _aheadAt[m.Uid] = _now;
            GeoPt o; double axis;
            if (tac == "barrier") { if (!g.Plan.Band.Set) return; o = g.Plan.Band.O; axis = g.Plan.Band.AxisDeg; }
            else { o = g.Plan.Anchor.O; axis = g.Plan.Anchor.AxisDeg; }
            int fell = Math.Max(0, Math.Min(j.Recs.Count, j.BuoysBefore - LeftOf(m, j)));
            if (fell >= j.Recs.Count) return;
            var path = new List<GeoPt>();
            for (int i = fell; i < j.Recs.Count; i++) path.Add(new GeoPt(j.Recs[i].Lat, j.Recs[i].Lon));
            double xs;
            var kept = AheadFilter(g, m, t, o, axis, path, out xs, AheadCheckMarginNm);
            if (kept.Count >= path.Count) return;
            _game.Log("asw pattern " + g.Name + " ahead late uid=" + I(m.Uid) + " left=" + I(path.Count) + " late=" + I(path.Count - kept.Count) + " -> replan");
            CancelRoute(g, m.Uid); g.Jobs.Remove(m.Uid);
            g.NextCrossAt = 0;
        }

        private void LayCorridor(Grp g, AswMember m, AswTarget t, int limit = 0, string label = "corridor")
        {
            if (limit == 0 && JobPending(g, m)) return;
            if (limit == 0 && _now < g.NextCrossAt) return;
            var pl = g.Plan; var a = pl.Anchor;
            string ammo; BuoyType bt;
            if (!PickFor(m, out ammo, out bt)) { Warn(g, "buoys_low", m.Name); return; }
            string src;
            double r = GroupR(g, out src), rr = AswMath.REff(bt, g.P).Nm, d = 1.6 * r;
            int avail = Math.Min(AvailFor(m, bt), AswMath.MaxDropPoints);
            if (avail <= 0) { Warn(g, "buoys_low", m.Name); return; }
            int cap = AswPatternMath.Capacity(avail, SpeedOf(m), AswMath.RemainS(m, null), d);
            int n = Math.Min(avail, Math.Max(cap, 0));
            if (limit > 0) n = Math.Min(avail, limit);   // "drop what is left" before leaving: the fuel for the package is not the question
            if (n <= 0) return;
            int rowsTotal, rowsLaid; bool tooWide, lifeShort;
            var nodes = CorridorNodes(g, m, r, bt, avail, out rowsTotal, out rowsLaid, out tooWide, out lifeShort);
            pl.RowsTotal = rowsTotal; pl.RowsLaid = rowsLaid;
            pl.Warn = null;
            if (tooWide) Warn(g, "corridor_too_wide", null, 600);
            if (lifeShort) Warn(g, "life_short", null, 600);
            if (rowsLaid < rowsTotal) Warn(g, "buoys_short", I(rowsLaid) + " of " + I(rowsTotal), 600);
            if (nodes.Count == 0) { g.NextCrossAt = _now + 10; return; }
            // who takes which side: K aircraft hold the corridor
            var holders = HoldersOf(g, "corridor");
            int k = Math.Max(1, holders.Count), index = Math.Max(0, holders.FindIndex(h => h.Uid == m.Uid));
            int side = AswPatternMath.CorridorSide(k, index, pl.CursorL, pl.CursorR);
            var left = new SortedDictionary<int, List<CNode>>(); var right = new SortedDictionary<int, List<CNode>>();
            foreach (var nd in nodes)
            {
                var dict = nd.Side < 0 ? left : right;
                List<CNode> l; if (!dict.TryGetValue(nd.Col, out l)) dict[nd.Col] = l = new List<CNode>();
                l.Add(nd);
            }
            var pts = new List<CNode>();
            if (side == 0)
            {
                // one aircraft: the ladder (T-334 / ADR-0130). Whole columns while the buoys last, both sides of the axis, the nearest column first; inside a column from the end nearer to the aircraft
                var cols = new SortedSet<int>(); foreach (int c in left.Keys) cols.Add(c); foreach (int c in right.Keys) cols.Add(c);
                int used = 0; var take = new List<int>();
                foreach (int c in cols)
                {
                    int cnt = (left.ContainsKey(c) ? left[c].Count : 0) + (right.ContainsKey(c) ? right[c].Count : 0);
                    if (used > 0 && used + cnt > n) break;
                    take.Add(c); used += cnt;
                    if (used >= n) break;
                }
                var cand = new List<CNode>();
                foreach (int c in take) { if (left.ContainsKey(c)) cand.AddRange(left[c]); if (right.ContainsKey(c)) cand.AddRange(right[c]); }
                var np = new List<NmPt>(); foreach (var q in cand) np.Add(new NmPt(q.X, q.Y));
                foreach (int i in AswPatternMath.NearFirstOrder(LocalOf(m, t, a.O, a.AxisDeg), np)) pts.Add(cand[i]);
            }
            else
            {
                var dict = side < 0 ? left : right;
                foreach (var kv in dict)
                {
                    MeanderSort(kv.Value, kv.Key);
                    if (pts.Count > 0 && pts.Count + kv.Value.Count > n) break;
                    pts.AddRange(kv.Value);
                    if (pts.Count >= n) break;
                }
                if (pts.Count == 0) { g.NextCrossAt = _now + 10; return; }
            }
            if (pts.Count > n) pts.RemoveRange(n, pts.Count - n);
            if (pts.Count == 0) { g.NextCrossAt = _now + 10; return; }
            // T-334: a point is laid only while the ship is still short of it when the aircraft gets there
            int planned = pts.Count;
            if (!a.Stand)   // a fixed target: the ring stands around it, nothing is "behind"
            {
                var path = new List<GeoPt>(); foreach (var q in pts) path.Add(q.P);
                double xs;
                var kept = AheadFilter(g, m, t, a.O, a.AxisDeg, path, out xs);
                LogAhead(g, m, kept.Count, planned, xs);
                var keep = new List<CNode>(); foreach (int i in kept) keep.Add(pts[i]);
                pts = keep;
            }
            if (pts.Count == 0) { g.NextCrossAt = _now + 10; return; }
            var geo = new List<GeoPt>(); foreach (var p in pts) geo.Add(p.P);
            double oldL = pl.CursorL, oldR = pl.CursorR;
            foreach (var p in pts) { if (p.Side <= 0) pl.CursorL = Math.Max(pl.CursorL, p.X); if (p.Side >= 0) pl.CursorR = Math.Max(pl.CursorR, p.X); }
            _game.Log("asw pattern " + g.Name + " corridor uid=" + I(m.Uid) + " side=" + (side < 0 ? "left" : side > 0 ? "right" : "ladder") + " k=" + I(k) + " pts=" + I(pts.Count) + " rows=" + I(rowsLaid) + "/" + I(rowsTotal) + " x=" + N(Math.Round(pts[0].X, 2)) + ".." + N(Math.Round(pts[pts.Count - 1].X, 2)));
            IssueDrop(g, m, ammo, bt, geo, label, () => { pl.CursorL = oldL; pl.CursorR = oldR; }, "corridor_row", GapFor(g, bt), true, PatternCapGap(r, AswPatternMath.CorridorRows(pl.Course.CorridorXNm, r, _defaults.Pat.CorridorMaxRows, out tooWide)));
        }
    }
}
