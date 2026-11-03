// T-174 W3 (ADR-0073 section 2): what the overlay draws for a group, and the rest of an area plan. Pure C#: no Unity, no game.
// Area order: the outline and the contacts are always drawn; the buoys, the lines and the remaining plan only while the "Route" flag of the window is on.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal struct AswDrawSet { public bool Outline, Buoys, Lines, AreaPlan, Contacts; }

    internal static partial class AswDrawPlan
    {
        /// <summary>T-180: the two barbs of an arrow head at (x1, y1), in pixels: {lx, ly, rx, ry}; null when the segment is shorter than 2 px.</summary>
        public static float[] ArrowHead(float x0, float y0, float x1, float y1, float len = 12f, float halfDeg = 25f)
        {
            float dx = x1 - x0, dy = y1 - y0, l = (float)Math.Sqrt(dx * dx + dy * dy);
            if (l < 2f) return null;
            float bx = -dx / l, by = -dy / l, a = halfDeg * (float)Math.PI / 180f, c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new[] { x1 + len * (bx * c - by * s), y1 + len * (bx * s + by * c), x1 + len * (bx * c + by * s), y1 + len * (-bx * s + by * c) };
        }

        public const string AreaRouteId = "asw.area_route", CourseRouteId = "asw.course_route";

        /// <summary>
        /// T-176 W6 (ADR-0076 s.8): the same for the course and the area order. The outline and the contacts are always drawn; with the "Route" flag off only the buoys in the water
        /// (course) / nothing more (area: the buoys are part of the plan); with it on the plan too: lines, the rest of the plan. sar / none / strike: everything as before.
        /// Lines of the kind "contact" / "lost_ring" go with the contacts (always drawn).
        /// </summary>
        public static AswDrawSet For(AswGroupView g, bool routeOn)
        {
            bool area = g != null && g.Order == "area", course = g != null && g.Order == "course";
            if (course) return new AswDrawSet { Outline = true, Contacts = true, Buoys = true, Lines = routeOn, AreaPlan = routeOn };
            return new AswDrawSet { Outline = true, Contacts = true, Buoys = !area || routeOn, Lines = !area || routeOn, AreaPlan = !area || routeOn };
        }

        /// <summary>Is the line drawn with the contacts (always) rather than with the plan?</summary>
        public static bool WithContacts(AswLineView l) { return l.Kind == "contact" || l.Kind == "lost_ring"; }

        public const int BudgetSegments = 400;

        /// <summary>
        /// The lines within the budget of segments per group (ADR-0076 s.8): when they are too many, the dashed routes of the aircraft go first, then the planned rows / lines;
        /// what was laid (State done) and the contacts stay. The order of the rest is kept.
        /// </summary>
        public static List<AswLineView> Budget(IList<AswLineView> lines, int max = BudgetSegments)
        {
            var res = new List<AswLineView>();
            if (lines == null) return res;
            int total = 0;
            foreach (var l in lines) total += Math.Max(0, l.Points.Count - 1);
            res.AddRange(lines);
            foreach (string drop in new[] { "route", "corridor_row", "dip_chain", "standoff" })
            {
                for (int i = res.Count - 1; i >= 0 && total > max; i--)
                    if (res[i].Kind == drop && res[i].State != "done") { total -= Math.Max(0, res[i].Points.Count - 1); res.RemoveAt(i); }
            }
            return res;
        }

        /// <summary>The remaining plan by the tactics of the group: rows | cross -> all[idx..] (Tactic "area"), dip -> dip[dipIdx..], mad -> mad[madIdx..]. Kind "plan"; an empty rest has no line.</summary>
        public static List<AswLineView> AreaPlan(IList<string> tactics, IList<GeoPt> all, int idx, IList<GeoPt> dip, int dipIdx, IList<GeoPt> mad, int madIdx)
        {
            var res = new List<AswLineView>();
            if (tactics == null) return res;
            foreach (string t in tactics)
            {
                IList<GeoPt> src = t == "rows" || t == "cross" || t == "grid" ? all : t == "dip" ? dip : t == "mad" ? mad : null;
                int from = t == "rows" || t == "cross" || t == "grid" ? idx : t == "dip" ? dipIdx : madIdx;
                if (src == null || from < 0) continue;
                var line = new AswLineView { Kind = "plan", Tactic = t == "rows" || t == "cross" || t == "grid" ? "area" : t };
                for (int i = from; i < src.Count; i++) line.Points.Add(src[i]);
                if (line.Points.Count > 0) res.Add(line);
            }
            return res;
        }

        public static string SelfTest()
        {
            var area = new AswGroupView { Order = "area" };
            var d = For(area, false);
            if (!d.Outline || !d.Contacts || d.Buoys || d.Lines || d.AreaPlan) return "area off";
            d = For(area, true);
            if (!d.Outline || !d.Contacts || !d.Buoys || !d.Lines || !d.AreaPlan) return "area on";
            foreach (string o in new[] { "sar", "none" })
                foreach (bool f in new[] { false, true })
                {
                    d = For(new AswGroupView { Order = o }, f);
                    if (!d.Outline || !d.Contacts || !d.Buoys || !d.Lines || !d.AreaPlan) return o + " always all";
                }
            // T-176: the course order with the "Route" flag off: only the buoys in the water and the contacts; on: the plan too
            d = For(new AswGroupView { Order = "course" }, false);
            if (!d.Outline || !d.Contacts || !d.Buoys || d.Lines || d.AreaPlan) return "course route off";
            d = For(new AswGroupView { Order = "course" }, true);
            if (!d.Outline || !d.Contacts || !d.Buoys || !d.Lines || !d.AreaPlan) return "course route on";
            if (!WithContacts(new AswLineView { Kind = "lost_ring" }) || WithContacts(new AswLineView { Kind = "route" })) return "with contacts";
            // the budget: 400 segments, the dashed routes go first, what was laid stays
            var many = new List<AswLineView>();
            for (int i = 0; i < 6; i++) { var rl = new AswLineView { Kind = "route", State = "plan" }; for (int j = 0; j < 100; j++) rl.Points.Add(new GeoPt(i, j)); many.Add(rl); }
            var laid = new AswLineView { Kind = "corridor_row", State = "done" }; for (int j = 0; j < 50; j++) laid.Points.Add(new GeoPt(9, j)); many.Add(laid);
            var kept = Budget(many);
            if (kept.Count < 1 || !kept.Contains(laid)) return "budget kept the laid line";
            int seg = 0; foreach (var l in kept) seg += l.Points.Count - 1;
            if (seg > BudgetSegments) return "budget " + seg;
            if (Budget(new List<AswLineView> { laid }).Count != 1) return "budget small";
            if (!For(null, false).Buoys) return "null group";
            var all = new List<GeoPt>(); for (int i = 0; i < 6; i++) all.Add(new GeoPt(i, i));
            var dip = new List<GeoPt> { new GeoPt(10, 10), new GeoPt(11, 11), new GeoPt(12, 12) };
            var mad = new List<GeoPt> { new GeoPt(20, 20), new GeoPt(21, 21) };
            var p = AreaPlan(new[] { "rows" }, all, 2, dip, 1, mad, 1);
            if (p.Count != 1 || p[0].Points.Count != 4 || p[0].Points[0].Lat != 2 || p[0].Kind != "plan" || p[0].Tactic != "area") return "rows rest";
            p = AreaPlan(new[] { "dip" }, all, 2, dip, 1, mad, 1);
            if (p.Count != 1 || p[0].Points.Count != 2 || p[0].Points[0].Lat != 11 || p[0].Tactic != "dip") return "dip rest";
            p = AreaPlan(new[] { "mad" }, all, 2, dip, 1, mad, 1);
            if (p.Count != 1 || p[0].Points.Count != 1 || p[0].Points[0].Lat != 21 || p[0].Tactic != "mad") return "mad rest";
            p = AreaPlan(new[] { "cross", "mad" }, all, 6, dip, 0, mad, 2);
            if (p.Count != 0) return "empty rest has no line";
            p = AreaPlan(new[] { "rows" }, null, 0, null, 0, null, 0);
            if (p.Count != 0) return "null lists";
            if (AreaPlan(null, all, 0, dip, 0, mad, 0).Count != 0) return "null tactics";
            return null;
        }
    }

    /// <summary>T-322 (ADR-0126): what the controller knows about one barrier line when it decides whether the line is still current.</summary>
    internal struct AswBarrierFacts
    {
        public bool Barrier;      // the group has a course order with the tactic "barrier"
        public bool InPlan;       // the line belongs to the current band / the ring while the target stands
        public bool Passed;       // the row is passed by the ship or already forgotten
        public bool Laid;         // State "done"
        public int Matched, Live; // buoys near the line: records of the group / alive (records + own game buoys)
        public double NoLiveS;    // seconds the line has had no live buoy (0 = has one now)
        public bool TrackPassed;  // every held contact crossed the row towards the ship and moves away
    }

    /// <summary>T-322: pure rules of a stale barrier line (view only; the plan, the window and the save do not change).</summary>
    internal static class AswBarrierStale
    {
        public const double FadeS = 90, NoBuoysGraceS = 300, BuoyTolNm = 0.5, TrackClearNm = 2, TrackAwayKn = 0.5;
        public const double GoneMatchNm = 0.6, GoneReadS = 15, GoneMinAgeS = 180;

        /// <summary>order > reset > passed > buoys > track_passed; null = current. Not laid: only order / reset / passed.</summary>
        public static string Why(AswBarrierFacts f)
        {
            if (!f.Barrier) return "order";
            if (!f.InPlan) return "reset";
            if (f.Passed) return "passed";
            if (!f.Laid) return null;
            if (f.Live <= 0 && (f.Matched > 0 || f.NoLiveS >= NoBuoysGraceS)) return "buoys";
            if (f.TrackPassed) return "track_passed";
            return null;
        }

        /// <summary>T-335: the line counts as laid once a buoy of it was seen (records near it, or live ones); sticky. A line "done" only because its points were deferred has none.</summary>
        public static bool LaidByFact(bool stateDone, int matched, int live, bool seenBefore)
        {
            return stateDone && (seenBefore || matched > 0 || live > 0);
        }

        /// <summary>1 at since, 0 at since + fadeS; since &lt; 0 = current (1).</summary>
        public static float Alpha(double sinceS, double nowS, double fadeS = FadeS)
        {
            if (sinceS < 0) return 1f;
            double a = 1 - (nowS - sinceS) / Math.Max(1e-6, fadeS);
            return (float)Math.Max(0, Math.Min(1, a));
        }

        private static void Local(GeoPt o, GeoPt p, out double x, out double y)
        {
            x = (p.Lon - o.Lon) * 60 * (AswMath.WorldGlobe ? Math.Cos(o.Lat * Math.PI / 180) : 1.0); y = (p.Lat - o.Lat) * 60;   // T-352
        }

        /// <summary>p within tolNm of any segment of pts (one point = the distance to it); bbox prefilter.</summary>
        public static bool NearLine(IList<GeoPt> pts, GeoPt p, double tolNm)
        {
            if (pts == null || pts.Count == 0) return false;
            double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
            foreach (var q in pts) { minLat = Math.Min(minLat, q.Lat); maxLat = Math.Max(maxLat, q.Lat); minLon = Math.Min(minLon, q.Lon); maxLon = Math.Max(maxLon, q.Lon); }
            double dLat = tolNm / 60, dLon = tolNm / (60 * Math.Max(0.05, AswMath.WorldGlobe ? Math.Cos(p.Lat * Math.PI / 180) : 1.0));
            if (p.Lat < minLat - dLat || p.Lat > maxLat + dLat || p.Lon < minLon - dLon || p.Lon > maxLon + dLon) return false;
            if (pts.Count == 1) { double x0, y0; Local(p, pts[0], out x0, out y0); return Math.Sqrt(x0 * x0 + y0 * y0) <= tolNm; }
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double ax, ay, bx, by;
                Local(p, pts[i], out ax, out ay); Local(p, pts[i + 1], out bx, out by);
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / l2));
                double cx = ax + t * dx, cy = ay + t * dy;
                if (Math.Sqrt(cx * cx + cy * cy) <= tolNm) return true;
            }
            return false;
        }

        /// <summary>contactX &lt;= rowX - clearNm and speed*cos(course - axis) &lt; -awayKn; NaN course / speed = false.</summary>
        public static bool TrackPassed(double rowXNm, double contactXNm, double courseDeg, double speedKn, double axisDeg, double clearNm = TrackClearNm, double awayKn = TrackAwayKn)
        {
            if (double.IsNaN(courseDeg) || double.IsNaN(speedKn) || double.IsNaN(contactXNm) || double.IsNaN(rowXNm)) return false;
            if (contactXNm > rowXNm - clearNm) return false;
            return speedKn * Math.Cos((courseDeg - axisDeg) * Math.PI / 180) < -awayKn;
        }

        /// <summary>A copy (Points copied) with Alpha / Stale set; the source line (cached plan / g.Lines) is never changed.</summary>
        public static AswLineView Faded(AswLineView l, float alpha, string why)
        {
            var c = new AswLineView { Kind = l.Kind, Tactic = l.Tactic, Lane = l.Lane, State = l.State, BandId = l.BandId, Seq = l.Seq, Alpha = alpha, Stale = why };
            c.Points.AddRange(l.Points);
            return c;
        }

        public static string SelfTest()
        {
            var f = new AswBarrierFacts { Barrier = true, InPlan = true, Laid = true };
            if (Why(f) != null) return "current";
            var g = f; g.Barrier = false; g.InPlan = false; g.Passed = true;
            if (Why(g) != "order") return "order first";
            g.Barrier = true; if (Why(g) != "reset") return "reset second";
            g.InPlan = true; if (Why(g) != "passed") return "passed third";
            g = f; g.Matched = 3; g.Live = 0; if (Why(g) != "buoys") return "buoys matched";
            g = f; g.Matched = 0; g.Live = 0; g.NoLiveS = 299; if (Why(g) != null) return "grace 299";
            g.NoLiveS = 300; if (Why(g) != "buoys") return "grace 300";
            g = f; g.Laid = false; if (Why(g) != null) return "not laid no buoys";
            g.Passed = true; if (Why(g) != "passed") return "not laid passed";
            g = f; g.Live = 2; g.Matched = 2; g.TrackPassed = true; if (Why(g) != "track_passed") return "track passed";
            if (!TrackPassed(10, 6, 180, 8, 0)) return "track against axis";
            if (TrackPassed(10, 6, 0, 8, 0)) return "track along axis";
            if (TrackPassed(10, 6, double.NaN, 8, 0) || TrackPassed(10, 6, 180, double.NaN, 0)) return "track NaN";
            if (TrackPassed(10, 9, 180, 8, 0)) return "track 1 nm";
            if (Alpha(-1, 50) != 1f) return "alpha current";
            if (Math.Abs(Alpha(100, 145) - 0.5f) > 1e-4) return "alpha half";
            if (Alpha(100, 400) != 0f) return "alpha gone";
            var seg = new List<GeoPt> { new GeoPt(0, 0), new GeoPt(0, 0.2) };   // 12 nm along the equator
            {
                var mer = new List<GeoPt> { new GeoPt(60, 10), new GeoPt(60.1, 10) };   // T-352: a meridian segment at 60N
                foreach (bool globe in new[] { false, true })
                    using (AswMath.WorldScope(globe))
                    {
                        if (!NearLine(mer, new GeoPt(60.05, 10.15), 10)) return "near T-352 0.15 globe=" + globe;   // 9 nm flat, 4.5 nm globe
                        if (NearLine(mer, new GeoPt(60.05, 10.2), 10) != globe) return "near T-352 0.2 globe=" + globe;   // 12 nm flat, 6 nm globe
                    }
            }
            if (!NearLine(seg, new GeoPt(0, 0.1), 0.5)) return "near on";
            if (!NearLine(seg, new GeoPt(0.4 / 60, 0.1), 0.5)) return "near 0.4";
            if (NearLine(seg, new GeoPt(0.6 / 60, 0.1), 0.5)) return "near 0.6";
            if (NearLine(seg, new GeoPt(0, 0.2 + 1.0 / 60), 0.5)) return "near past end";
            if (!NearLine(new List<GeoPt> { new GeoPt(1, 1) }, new GeoPt(1, 1), 0.5) || NearLine(null, new GeoPt(1, 1), 0.5)) return "near single";
            var src = new AswLineView { Kind = "barrier_line", State = "done", BandId = 2, Seq = 3 }; src.Points.Add(new GeoPt(1, 2));
            var fc = Faded(src, 0.5f, "passed");
            fc.Points.Add(new GeoPt(3, 4));
            if (src.Alpha != 1f || src.Stale != null || src.Points.Count != 1 || fc.Points.Count != 2 || fc.Stale != "passed" || fc.BandId != 2 || fc.Seq != 3) return "faded copy";
            if (LaidByFact(true, 0, 0, false) || LaidByFact(false, 3, 3, true) || !LaidByFact(true, 1, 0, false) || !LaidByFact(true, 0, 1, false) || !LaidByFact(true, 0, 0, true)) return "laid by fact";   // T-335
            return null;
        }
    }
}
