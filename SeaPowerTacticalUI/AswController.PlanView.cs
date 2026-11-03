using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-176 C12 / ADR-0076 s.8: what the window and the overlay get of the v3 plan: the summary (AswPlanView), the line kinds, the preview of a draft.
    internal sealed partial class AswController
    {
        /// <summary>Summary of the plan for the window and link_state (the numbers of the lattice / rows / nodes).</summary>
        private AswPlanView PlanViewOf(Grp g)
        {
            var pl = g.Plan; var spec = pl.Course;
            var pv = new AswPlanView { Warn = pl.Warn };
            if (g.Order == "course")
            {
                pv.Tactic = AswPatternMath.ComboOf(g.Tactics);
                bool cor = g.Tactics.Contains("corridor"), bar = g.Tactics.Contains("barrier");
                pv.XNm = cor ? spec.CorridorXNm : bar ? spec.BarrierXNm : 0;
                pv.StandoffNm = cor ? spec.CorridorLNm : bar ? spec.BarrierRows * spec.BarrierGapNm : spec.DipLNm;
                pv.RowsL = pv.RowsR = pl.RowsLaid; pv.RowsMax = pl.RowsTotal;
                pv.NodesTotal = pl.RowsTotal; pv.NodesCovered = pl.RowsLaid;
                if (bar)   // T-180: a barrier order has lines, not corridor rows; T-229: the rows of the sliding barrier ahead of the ship
                {
                    var ahead = RowsAhead(g);
                    int laidRows = 0, total = 0, open = 0;
                    foreach (var row in ahead) { total += row.Buoys; open += row.Open.Count; if (row.Open.Count == 0) laidRows++; }
                    pv.Lines = ahead.Count; pv.LinesLaid = laidRows; pv.GapNm = spec.BarrierGapNm;
                    if (!cor)
                    {
                        pv.StandoffNm = spec.BarrierRows * spec.BarrierGapNm;
                        pv.RowsMax = spec.BarrierRows; pv.RowsL = pv.RowsR = laidRows;
                        pv.NodesTotal = total; pv.NodesCovered = Math.Max(0, total - open);
                    }
                }
            }
            else if (g.Order == "area")
            {
                pv.Tactic = pl.AreaPattern;
                pv.NodesTotal = pl.Nodes == null ? 0 : pl.Nodes.Count; pv.NodesCovered = pl.NodesDone.Count;
            }
            pv.Deferred = pl.Deferred.Count;
            return pv;
        }

        private static string PlanJson(AswPlanView p)
        {
            return Obj("tactic", p.Tactic, "x_nm", p.XNm, "standoff_nm", p.StandoffNm, "rows", p.RowsMax, "rows_laid", p.RowsL, "nodes_total", p.NodesTotal, "nodes_covered", p.NodesCovered, "deferred", p.Deferred, "warn", p.Warn, "lines", p.Lines, "lines_laid", p.LinesLaid, "gap_nm", p.GapNm);
        }

        private static AswCourseSpec CopySpec(AswCourseSpec s)
        {
            return new AswCourseSpec { Tactics = new List<string>(s.Tactics), CorridorXNm = s.CorridorXNm, BarrierXNm = s.BarrierXNm, DipLNm = s.DipLNm, CorridorLNm = s.CorridorLNm, BarrierLNm = s.BarrierLNm, BarrierRows = s.BarrierRows, BarrierGapNm = s.BarrierGapNm, Depth = s.Depth };
        }

        private static AswPatternParams CopyPat(AswPatternParams p)
        {
            return p.Clone();
        }

        private static AswLineView L(string kind, string state, string tactic, params GeoPt[] pts)
        {
            var l = new AswLineView { Kind = kind, State = state, Tactic = tactic };
            l.Points.AddRange(pts);
            return l;
        }

        /// <summary>
        /// The plan of a course order as lines (ADR-0076 s.8): corridor rows and the standoff mark, barrier lines (laid: done, the next: plan), the dip chain, deferred points.
        /// Built from the anchor, no game calls. Throttled: the caller keeps the result for a few seconds.
        /// </summary>
        private List<AswLineView> CoursePlanLines(Grp g)
        {
            var res = new List<AswLineView>();
            var pl = g.Plan; var spec = pl.Course; var a = pl.Anchor;
            if (!a.Set) return res;
            string src;
            double r = g.RNm > 0 ? g.RNm : 2.7, v = AnchorKn(a), s = AlongNm(g);
            if (g.Tactics.Contains("corridor"))
            {
                bool tw;
                var rows = AswPatternMath.CorridorRows(spec.CorridorXNm, r, _defaults.Pat.CorridorMaxRows, out tw);
                double x0 = a.Stand ? 0 : s + 3, x1 = a.Stand ? 0 : s + spec.CorridorLNm;
                if (a.Stand) res.Add(RingLine("corridor_row", a.O, Math.Max(spec.CorridorXNm / 2, 1.6 * r), 1.6 * r));
                else
                    foreach (double y in rows)
                        for (int side = -1; side <= 1; side += 2)
                            res.Add(L("corridor_row", "plan", "corridor", AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x0, side * y)), AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x1, side * y))));
                if (!a.Stand) res.Add(L("standoff", "plan", "corridor", AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x1, -spec.CorridorXNm / 2)), AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x1, spec.CorridorXNm / 2))));
            }
            if (g.Tactics.Contains("barrier"))
            {
                if (a.Stand) { var ringL = RingLine("barrier_line", a.O, Math.Max(spec.BarrierXNm / 2, 1.6 * r), 1.6 * r); ringL.BandId = -2; res.Add(ringL); }   // T-322
                else if (pl.Band.Set)
                {
                    // T-229: the rows of the band: a passed row and a laid one are "done", a row with open points is "plan"; a staggered row is shorter
                    foreach (var b in pl.Band.Rows)
                    {
                        var rl = L("barrier_line", b.Passed || b.Open.Count == 0 && b.Skipped < b.Buoys ? "done" : "plan", "barrier", AswMath.ToGeo(b.Center, b.AxisDeg, new NmPt(0, -b.HalfNm)), AswMath.ToGeo(b.Center, b.AxisDeg, new NmPt(0, b.HalfNm)));
                        rl.BandId = pl.Band.Id; rl.Seq = b.Seq;   // T-322
                        res.Add(rl);
                    }
                }
                else
                {
                    // no band yet (a draft preview, the first tick): N planned rows one interval apart from the ship
                    for (int k = 0; k < Math.Max(1, spec.BarrierRows); k++)
                    {
                        double step, x = s + AswPatternMath.BarrierRowX(spec.BarrierGapNm, spec.BarrierGapNm, k), half = 0;
                        foreach (double y in AswPatternMath.BarrierRowYs(spec.BarrierXNm, r, k, _defaults.Pat.BarrierStagger, out step)) half = Math.Max(half, Math.Abs(y));
                        if (half <= 0) half = spec.BarrierXNm / 2;
                        res.Add(L("barrier_line", "plan", "barrier", AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x, -half)), AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x, half))));
                    }
                }
            }
            if (g.Tactics.Contains("dip"))
            {
                var done = L("dip_chain", "done", "dip");
                foreach (double x in pl.DipTaken) done.Points.Add(AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(x, 0)));
                if (done.Points.Count > 0) res.Add(done);
                if (!a.Stand)
                {
                    res.Add(L("dip_chain", "plan", "dip", AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(s + 2, 0)), AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(s + spec.DipLNm, 0))));
                    res.Add(L("standoff", "plan", "dip", AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(s + spec.DipLNm, -r)), AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(s + spec.DipLNm, r))));
                }
            }
            foreach (var d in pl.Deferred) res.Add(L("deferred", "deferred", null, d));
            return res;
        }

        private static AswLineView RingLine(string kind, GeoPt centre, double radiusNm, double stepNm)
        {
            var l = new AswLineView { Kind = kind, State = "plan" };
            foreach (var p in AswPatternMath.StandingRing(radiusNm, stepNm)) l.Points.Add(AswMath.ToGeo(centre, 0, p));
            if (l.Points.Count > 0) l.Points.Add(l.Points[0]);
            return l;
        }

        /// <summary>The lines of an area order: the open buoy nodes, the sector paths of the aircraft (by lane), deferred points.</summary>
        private List<AswLineView> AreaPlanLines(Grp g)
        {
            var res = new List<AswLineView>();
            var pl = g.Plan;
            if (pl.Nodes == null) return res;
            int lane = 0;
            foreach (var m in GridHolders(g))
            {
                List<int> sec;
                if (pl.Sectors.TryGetValue(m.Uid, out sec))
                {
                    var open = new List<int>(); foreach (int i in sec) if (!pl.NodesDone.Contains(i)) open.Add(i);
                    if (open.Count > 0)
                    {
                        NmPt from = (m.Lat != 0 || m.Lon != 0) ? LocalOf(g, new GeoPt(m.Lat, m.Lon)) : new NmPt(0, 0);
                        var route = new AswLineView { Kind = "route", State = "plan", Tactic = "grid", Lane = lane };
                        foreach (int i in AswPatternMath.RingRoute(pl.Nodes, open, from)) route.Points.Add(GeoOf(g, pl.Nodes[i].P));
                        res.Add(route);
                    }
                }
                lane++;
            }
            foreach (var d in pl.Deferred) res.Add(L("deferred", "deferred", null, d));
            return res;
        }

        private readonly Dictionary<Grp, KeyValuePair<double, List<AswLineView>>> _planLines = new Dictionary<Grp, KeyValuePair<double, List<AswLineView>>>();

        /// <summary>The lines of the plan, rebuilt at most every 5 s.</summary>
        private List<AswLineView> PlanLinesOf(Grp g)
        {
            KeyValuePair<double, List<AswLineView>> c;
            if (_planLines.TryGetValue(g, out c) && _now - c.Key < 5 && c.Key <= _now) return c.Value;
            List<AswLineView> l;
            try { l = g.Order == "course" ? CoursePlanLines(g) : g.Order == "area" ? AreaPlanLines(g) : new List<AswLineView>(); }
            catch (Exception e) { _game.Log("asw plan lines error: " + e.Message); l = new List<AswLineView>(); }
            _planLines[g] = new KeyValuePair<double, List<AswLineView>>(_now, l);
            return l;
        }

        /// <summary>The v3 part of the group view (called from RebuildView).</summary>
        private void PlanView(Grp g, AswGroupView gv)
        {
            gv.Plan = PlanViewOf(g);
            gv.Course = CopySpec(g.Plan.Course);
            gv.AreaPattern = g.Plan.AreaPattern;
            if (g.Order == "course" || g.Order == "area")
            {
                var lines = new List<AswLineView>(g.Lines);
                lines.AddRange(PlanLinesOf(g));
                gv.Lines = FadeStale(g, lines);   // T-322
            }
            gv.Course.Depth = g.Depth;
        }

        // ---- the preview of a draft: a dry run on a throw-away group (nothing goes to the game) -----------------------

        private Grp PreviewGroup(string name, List<string> tactics, string order)
        {
            var src = name != null ? Find(name) : _groups.Count == 1 ? _groups[0] : null;
            var pg = new Grp { Name = "preview", P = Clone(src != null ? src.P : _defaults), Order = order };
            if (src != null) pg.Members.AddRange(src.Members);
            else foreach (var m in _live.Values) if (Capable(m) && AswMath.Usable(m)) pg.Members.Add(m.Uid);
            pg.Tactics = tactics;
            return pg;
        }

        private void AddPreview(string tactic, IEnumerable<AswLineView> lines)
        {
            foreach (var l in lines)
            {
                if (l.Points.Count == 0 || l.Kind == "deferred") continue;
                var p = new AswLineView { Kind = "preview", Tactic = tactic ?? l.Tactic, State = l.State, Lane = l.Lane };
                p.Points.AddRange(l.Points);
                _preview.Add(p);
            }
        }

        private void PreviewCourse(Dictionary<string, object> c)
        {
            var cmd = AswCommandsParse.ParseCourse(c);
            if (cmd.Err != null || AswPatternMath.TacticsConflict(NormTactics("course", cmd.Tactics))) return;   // T-334: corridor + barrier = no preview
            AswTarget t = cmd.TargetUid != 0 ? _game.Target(cmd.TargetUid) : _game.SelectedShip();
            if (t == null) return;
            var g = PreviewGroup(cmd.Group, new List<string>(), "course");
            bool removed;
            g.Plan.Course = CourseSpecOf(cmd, g, out removed);
            g.Tactics = CourseTactics(g.Plan.Course);
            string src;
            double axis = AswMath.AxisDeg(t.NextWpBrg, t.CourseDeg, t.Track, out src);
            AswPatternMath.Reanchor(ref g.Plan.Anchor, axis, t.SpeedKn, Pos(t), _now, 30, 60, 0);
            g.Axis = axis; g.V = t.SpeedKn;
            GroupR(g, out src);
            AddPreview(null, CoursePlanLines(g));
        }

        private void PreviewArea(Dictionary<string, object> c)
        {
            var cmd = AswCommandsParse.ParseArea(c);
            if (cmd.Err != null) return;
            string tname, rerr; int tuid;
            double[] rect = AreaRectOf(cmd, out tname, out tuid, out rerr);
            if (rerr != null || rect == null) return;
            var g = PreviewGroup(cmd.Group, cmd.Tactics.Count > 0 ? new List<string>(cmd.Tactics) : new List<string>(AutoArea), "area");
            g.Rect = rect;
            List<GeoPt> all, dip, mad;
            AreaGeometry(g, out all, out dip, out mad);
            foreach (string tac in g.Tactics)
            {
                if (tac == "grid" || tac == "rows" || tac == "cross") AddPreview("area", new[] { L("preview", "plan", "area", all.ToArray()) });
                else if (tac == "dip") AddPreview("dip", new[] { L("preview", "plan", "dip", dip.ToArray()) });
                else if (tac == "mad") AddPreview("mad", new[] { L("preview", "plan", "mad", mad.ToArray()) });
            }
        }
    }
}
