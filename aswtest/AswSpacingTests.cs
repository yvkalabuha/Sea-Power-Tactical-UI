using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // ADR-0063: buoy spacing from the buoy range, no dense clusters (re-plans, time compression, contacts, land).
    internal static class AswSpacingTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        // SSQ-53 of the fake game: R_eff = 0.5 * 10 km = 2.70 nm (no game estimate) -> spacing 4.32, gap 3.02
        private static readonly double R53 = 0.5 * 10 / AswMath.KmPerNm;

        private static List<GeoPt> ViewBuoys(AswController c)
        {
            var l = new List<GeoPt>();
            var v = c.View();
            if (v != null) foreach (var g in v.Groups) foreach (var b in g.Buoys) l.Add(new GeoPt(b.Lat, b.Lon));
            return l;
        }

        private static List<GeoPt> Planned(AswFakeGame f, string ammo = "usn_ssq-53")
        {
            var l = new List<GeoPt>();
            foreach (var d in f.Drops) if (d.Ammo == ammo) l.AddRange(d.Pts);
            return l;
        }

        private static int Close(IList<GeoPt> pts, double gap)
        {
            int n = 0;
            for (int i = 0; i < pts.Count; i++) for (int j = i + 1; j < pts.Count; j++) if (AswMath.DistNm(pts[i], pts[j]) < gap - 1e-6) n++;
            return n;
        }

        private static string F(double x) { return x.ToString("0.00", CultureInfo.InvariantCulture); }

        /// <summary>Numbers for the report: the two live failure modes of the old code (time compression loop, re-plans).</summary>
        public static string Report()
        {
            double d = AswMath.Spacing(R53), gap = 0.7 * d;
            var f = TcLoop(out AswController c);
            var vb = ViewBuoys(c);
            Console.WriteLine("tc x4, 30 min: drop orders=" + f.Drops.Count + " buoys in the group list=" + vb.Count + " fallen=" + f.Fallen.Count
                + " list min pair=" + F(AswMath.MinPairNm(vb)) + " nm, list pairs < 0.7d=" + Close(vb, gap) + " (d=" + F(d) + ")");
            f = Zigzag(out c);
            Console.WriteLine("zigzag +-20 deg / 10 min, 2 h: drop orders=" + f.Drops.Count + " fallen=" + f.Fallen.Count + " min pair=" + F(AswMath.MinPairNm(f.Fallen))
                + " nm, pairs < 0.7d=" + Close(f.Fallen, gap) + " replans=" + Count(f, "asw replan"));
            return null;
        }

        private static int Count(AswFakeGame f, string prefix) { int n = 0; foreach (var l in f.Logs) if (l.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        // x4: the helicopter needs ~2 min to the first point; the old check gave up after 2 ticks and laid the package again and again
        private static AswFakeGame TcLoop(out AswController c)
        {
            var f = new AswFakeGame { TimeScale = 4, DropDelayS = 150, SimBuoys = true };
            var h = AswFakeGame.Helo(1, "air"); h.SpeedKn = 250;   // T-334: the fake drops faster than a 100 kn model (the ETA check of a package in flight would cancel it)
            f.Mem.Add(h);
            c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 1800);
            return f;
        }

        // the escort zig-zags +-20 degrees every 10 min at 15 kn: each turn is a re-plan
        private static AswFakeGame Zigzag(out AswController c)
        {
            var f = new AswFakeGame { DropDelayS = 60, SimBuoys = true };
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 200));
            c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            for (int i = 0; i < 7200; i++)
            {
                f.Tgt.CourseDeg = ((i / 600) % 2 == 0) ? 70 : 110;
                f.Advance(1); f.Tick(c);
                foreach (var m in f.Mem) m.FuelFrac = 0.9;   // one machine all the time
            }
            return f;
        }

        public static string Run()
        {
            string e;
            if ((e = Math_()) != null) return "Math: " + e;
            if ((e = TcNoPile()) != null) return "TcNoPile: " + e;
            if ((e = ZigzagNoPile()) != null) return "ZigzagNoPile: " + e;
            if ((e = ReplanExtends()) != null) return "ReplanExtends: " + e;
            if ((e = ExistingSkipped()) != null) return "ExistingSkipped: " + e;
            if ((e = ContactTwice()) != null) return "ContactTwice: " + e;
            if ((e = LandNoCollapse()) != null) return "LandNoCollapse: " + e;
            if ((e = PerType()) != null) return "PerType: " + e;
            return null;
        }

        // spacing = 2 R (1 - overlap); neighbours overlap; the merge gap is 0.7 spacing; the thinning and the rows extension are pure
        private static string Math_()
        {
            double r = 2.7, d = AswMath.Spacing(r);
            if (Math.Abs(d - 2 * r * (1 - AswMath.OverlapFrac)) > 1e-9 || Math.Abs(d - 4.32) > 1e-9) return "spacing " + d;
            if (AswMath.OverlapFrac < 0.15 || AswMath.OverlapFrac > 0.25) return "overlap " + AswMath.OverlapFrac;
            if (Math.Abs(AswMath.OverlapNm(r) - 0.4 * r) > 1e-9) return "overlap nm " + AswMath.OverlapNm(r);
            if (Math.Abs(AswMath.MinGapNm(r) - 0.7 * d) > 1e-9) return "gap " + AswMath.MinGapNm(r);
            if (!(AswMath.Spacing(5.4) > AswMath.Spacing(2.7))) return "spacing grows with the range";
            // contact cross: neighbours 0.8 R sqrt2 apart, the merge gap is 0.7 of that
            if (Math.Abs(AswMath.LocalizeGapNm(r) - 0.7 * 0.8 * r * Math.Sqrt(2)) > 1e-9) return "localize gap";
            if (!(AswMath.LocalizeGapNm(r) < AswMath.MinGapNm(r))) return "contact pattern must be denser";
            var o = new GeoPt(35, 20);
            var pts = new List<GeoPt> { AswMath.Offset(o, 0, 1), AswMath.Offset(o, 0, 2), AswMath.Offset(o, 0, 6), AswMath.Offset(o, 0, 6.5), AswMath.Offset(o, 0, 12) };
            var ex = new List<GeoPt> { o };
            int sk;
            var t = AswMath.Thin(pts, ex, 3.0, out sk);
            if (t.Count != 2 || sk != 3) return "thin " + t.Count + "/" + sk;
            if (AswMath.DistNm(t[0], pts[2]) > 1e-9 || AswMath.DistNm(t[1], pts[4]) > 1e-9) return "thin keeps the order";
            if (AswMath.MinPairNm(t) < 3.0) return "thin min pair";
            if (Math.Abs(AswMath.NearestNm(pts[0], ex) - 1) > 1e-6) return "nearest";
            // rows ahead: a covered first segment is skipped and the barrier goes on behind it
            int dropped, skipped; double endX;
            var free = AswMath.RowsAhead(r, 12, 15, 28800, 100, 3, q => false, 3, out dropped, out skipped, out endX);
            var normal = AswMath.Rows(r, 12, 15, 28800, 100, out dropped);
            if (free.Count != normal.Count || skipped != 0) return "rows ahead free " + free.Count + " vs " + normal.Count;
            double maxFree = 0; foreach (var q in free) maxFree = Math.Max(maxFree, q.X);
            var ahead = AswMath.RowsAhead(r, 12, 15, 28800, 100, 3, q => q.X < 3 + 12, 3, out dropped, out skipped, out endX);
            if (ahead.Count != normal.Count || skipped == 0) return "rows ahead covered " + ahead.Count + "/" + skipped;
            foreach (var q in ahead) if (q.X < 15 - 1e-9) return "a point in the covered part " + F(q.X);
            for (int i = 0; i < ahead.Count; i++) for (int j = i + 1; j < ahead.Count; j++)
            {
                double dd = Math.Sqrt(Math.Pow(ahead[i].X - ahead[j].X, 2) + Math.Pow(ahead[i].Y - ahead[j].Y, 2));
                if (dd < 0.7 * d - 1e-9) return "rows ahead pair " + F(dd);
            }
            var none = AswMath.RowsAhead(r, 12, 15, 28800, 100, 3, q => true, 3, out dropped, out skipped, out endX);
            if (none.Count != 0 || endX < 3 + 3 * 12) return "all covered " + none.Count + " end " + F(endX);
            return null;
        }

        /// <summary>T-176: the rows of a corridor stand 2.84 nm apart (X = 20, SSQ-53) by design, closer than the merge gap of ADR-0063 (3.02); the cap is 0.9 of that.</summary>
        private const double PatternGap = 2.5;

        // time compression x4 with a 2.5 min transit: no package laid again and again, the group list holds only what falls (plus the package in the air)
        private static string TcNoPile()
        {
            AswController c;
            var f = TcLoop(out c);
            var vb = ViewBuoys(c);
            if (f.Fallen.Count == 0) return "nothing fell";
            if (f.Drops.Count > 3) return "drop orders " + f.Drops.Count;
            double gap = PatternGap;
            if (Close(vb, gap) > 0) return "group list has " + Close(vb, gap) + " pairs closer than " + F(gap) + " (min " + F(AswMath.MinPairNm(vb)) + ")";
            if (Close(f.Fallen, gap) > 0) return "fallen pairs " + Close(f.Fallen, gap);
            if (vb.Count > f.Fallen.Count + 16) return "list " + vb.Count + " vs fallen " + f.Fallen.Count;
            return null;
        }

        // a zig-zagging escort: the buoys in the water keep the gap however often the plan is made again
        private static string ZigzagNoPile()
        {
            AswController c;
            var f = Zigzag(out c);
            if (f.Fallen.Count < 16) return "fallen " + f.Fallen.Count;   // T-176: +-20 degrees never moves the anchor (30 degrees held for 60 s), the carpet just rolls on
            double gap = PatternGap;
            if (Close(f.Fallen, gap) > 0) return Close(f.Fallen, gap) + " pairs closer than " + F(gap) + ", min " + F(AswMath.MinPairNm(f.Fallen));
            var vb = ViewBuoys(c);
            if (Close(vb, gap) > 0) return "group list pairs " + Close(vb, gap);
            return null;
        }

        // a re-plan right after the first package: the new package does not seed the same water again, it goes on ahead of it
        private static string ReplanExtends()
        {
            var f = new AswFakeGame { SimBuoys = true };
            var hr = AswFakeGame.Helo(1, "air", passive: 100); hr.SpeedKn = 250;   // T-334: the whole first package is laid
            f.Mem.Add(hr);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 30);
            if (f.Drops.Count != 1) return "first package " + f.Drops.Count;
            var first = new List<GeoPt>(f.Drops[0].Pts);
            f.Tgt.CourseDeg = 135;   // 45 degrees held: the anchor moves (T-176)
            Run(c, f, 420);
            if (!f.Logs.Exists(l => l.Contains("reanchor reason=turn"))) return "no re-anchor";
            if (f.Drops.Count < 2) return "no second package";
            var second = f.Drops[f.Drops.Count - 1].Pts;
            double gap = PatternGap;
            foreach (var p in second) if (AswMath.NearestNm(p, first) < gap - 1e-6) return "a re-plan point " + F(AswMath.NearestNm(p, first)) + " nm from the first package";
            if (!f.Logs.Exists(l => l.StartsWith("asw drop G corridor", StringComparison.Ordinal) && l.Contains("spacing=4.32"))) return "no drop log line";
            return null;
        }

        // buoys already in the water (another group / the player) are not seeded over
        private static string ExistingSkipped()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 100));
            var o = new GeoPt(35, 20);
            var c0 = Make(f);
            // where the first package would go: a buoy in the water at every planned point of the inner rows
            var plan = AswMath.Rows(R53, 12, 15, 28800, 100, out int dr);
            foreach (var q in plan) if (Math.Abs(Math.Abs(q.Y) - R53) < 1e-9) f.Buoys.Add(new AswBuoyObs { Id = 1 + f.Buoys.Count, OwnerUid = 77, Lat = AswMath.ToGeo(o, 90, new NmPt(q.X + 3, q.Y)).Lat, Lon = AswMath.ToGeo(o, 90, new NmPt(q.X + 3, q.Y)).Lon });
            f.Tgt.Track.Clear();
            c0.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c0.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            f.Tick(c0);
            if (f.Drops.Count == 0) return "no drop";
            var ex = new List<GeoPt>(); foreach (var b in f.Buoys) ex.Add(new GeoPt(b.Lat, b.Lon));
            double gap = AswMath.MinGapNm(R53);
            foreach (var p in f.Drops[0].Pts) if (AswMath.NearestNm(p, ex) < gap - 1e-6) return "a point " + F(AswMath.NearestNm(p, ex)) + " nm from a buoy in the water";
            return null;
        }

        // the same submarine seen twice (a new contact id at the same place): the second cross is not dropped over the first
        private static string ContactTwice()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 100));   // no dipping sonar: a reserve of the dip order, the dropper of the contact
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 5);
            var sub = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 6);
            f.Subs.Add(new AswContact { Id = 4001, Name = "S1", Lat = sub.Lat, Lon = sub.Lon, Hostile = true, AttackOk = true });
            Run(c, f, 3);
            f.Subs.Clear();
            f.Subs.Add(new AswContact { Id = 4002, Name = "S2", Lat = sub.Lat, Lon = sub.Lon, Hostile = true, AttackOk = true });
            Run(c, f, 3);
            var crosses = new List<List<GeoPt>>();
            foreach (var d in f.Drops) if (d.Ammo == "usn_ssq-53") crosses.Add(d.Pts);
            if (crosses.Count == 0) return "no localisation cross";
            var all = new List<GeoPt>(); foreach (var x in crosses) all.AddRange(x);
            double lgap = AswMath.LocalizeGapNm(R53);
            if (Close(all, lgap) > 0) return "contact buoys " + Close(all, lgap) + " pairs closer than " + F(lgap);
            // the cross is denser than a barrier by design and comes from the buoy's range
            double nb = AswMath.MinPairNm(crosses[0]);
            if (Math.Abs(nb - 2.3 * Math.Sqrt(2)) > 0.01) return "cross neighbours " + F(nb);   // T-176 C11: the cross stands on the spook radius 2.3
            return null;
        }

        // a coast across the barrier: land points are dropped, never moved onto one spot
        private static string LandNoCollapse()
        {
            var f = new AswFakeGame();
            f.Tgt.CourseDeg = 0;
            double coast = 20 + 5.0 / 60;
            f.Height = p => p.Lon > coast ? 80 : -200;
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 100));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 3 * 3600);
            var pts = Planned(f);
            if (pts.Count == 0) return "no drops";
            foreach (var p in pts) if (p.Lon > coast) return "a point over land";
            double gap = PatternGap;
            if (Close(pts, gap) > 0) return Close(pts, gap) + " pairs closer than " + F(gap) + " min " + F(AswMath.MinPairNm(pts));
            return null;
        }

        // two droppers with different buoys: each package is spaced by its own buoy's range
        private static string PerType()
        {
            var f = new AswFakeGame();
            var a = AswFakeGame.Helo(1, "air", passive: 0); a.Vlad = 20;    // only SSQ-77 (20 km): R_eff 5.40 -> spacing 8.64
            f.Mem.Add(a);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            var pts = Planned(f, "usn_ssq-77");
            if (pts.Count < 2) return "no SSQ-77 package " + pts.Count;
            double want = AswMath.Spacing(0.5 * 20 / AswMath.KmPerNm);
            double mp = AswMath.MinPairNm(pts);
            if (mp < want - 0.01) return "SSQ-77 min pair " + F(mp) + " < spacing " + F(want);
            return null;
        }
    }
}
