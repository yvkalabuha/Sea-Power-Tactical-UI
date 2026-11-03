using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-288 A1-A3: pure geometry / axis / run / rect / group choice cases. Each returns null (ok) or an error text.</summary>
    internal static class SubDatumMathTests
    {
        internal static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        private static List<DxY> Wedge(double th, double sg, double r) { return SubDatumMath.Sector(th, sg, 0, r); }

        public static string GeoRoundtrip()
        {
            foreach (double lat0 in new[] { 0.0, 60.0, -45.0 })
            {
                double lon0 = 20;
                foreach (bool globe in new[] { false, true })
                {
                    DxY p = SubDatumMath.ToLocal(lat0, lon0, lat0 + 0.05, lon0 - 0.07, globe);
                    double la, lo;
                    SubDatumMath.ToGeo(lat0, lon0, p, globe, out la, out lo);
                    double mLat = Math.Abs(la - (lat0 + 0.05)) * 60 * 1852, mLon = Math.Abs(lo - (lon0 - 0.07)) * 60 * 1852 * (globe ? Math.Cos(lat0 * Math.PI / 180) : 1.0);
                    if (mLat > 1 || mLon > 1 || Math.Abs(la - (lat0 + 0.05)) > 1e-12 || Math.Abs(lo - (lon0 - 0.07)) > 1e-12) return "roundtrip lat0=" + lat0 + " globe=" + globe + " off " + mLat + "/" + mLon + " m";
                }
            }
            DxY n = SubDatumMath.ToLocal(10, 10, 10.1, 10, true);
            if (!Near(n.Y, 6, 1e-6) || !Near(n.X, 0, 1e-9)) return "north 0.1 deg must be 6 nm: " + n.X + "," + n.Y;
            DxY e = SubDatumMath.ToLocal(60, 10, 60, 10.1, true);
            if (!Near(e.X, 3, 0.01)) return "east at 60N 0.1 deg must be 3 nm on the globe: " + e.X;
            return null;
        }

        /// <summary>T-352: the frame of the datum follows the world; the globe stays bit-identical to the old formula, the flat world is 60 nm per degree on both axes.</summary>
        public static string WorldFlat()
        {
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                DxY g = SubDatumMath.ToLocal(phi, 10, phi + 0.1, 10.1, true), f = SubDatumMath.ToLocal(phi, 10, phi + 0.1, 10.1, false);
                if (g.X != (10.1 - 10) * 60 * Math.Cos(phi * Math.PI / 180) || g.Y != (phi + 0.1 - phi) * 60) return "globe == old at " + phi;
                if (f.X != (10.1 - 10) * 60 || f.Y != g.Y) return "flat at " + phi + ": " + f.X;
                if (phi == 60 && !Near(Math.Sqrt(f.X * f.X + f.Y * f.Y), 8.4853, 1e-4)) return "flat distance at 60N: " + Math.Sqrt(f.X * f.X + f.Y * f.Y);
                if (phi == 0 && f.X != g.X) return "the worlds agree on the equator";
                foreach (bool globe in new[] { false, true })
                {
                    DxY p = SubDatumMath.ToLocal(phi, 10, phi + 0.1, 10.1, globe);
                    double la, lo;
                    SubDatumMath.ToGeo(phi, 10, p, globe, out la, out lo);
                    if (Math.Abs(la - (phi + 0.1)) > 1e-12 || Math.Abs(lo - 10.1) > 1e-12) return "ToGeo(ToLocal) at " + phi + " globe=" + globe;
                    double[] r = SubDatumMath.RectGeo(phi, 10, -3, -3, 3, 3, globe);
                    double k = globe ? Math.Cos(phi * Math.PI / 180) : 1.0;
                    if (Math.Abs(r[3] - (10 + 3 / (60 * k))) > 1e-12) return "RectGeo east at " + phi + " globe=" + globe;
                }
            }
            return null;
        }

        public static string SectorShape()
        {
            var s = SubDatumMath.Sector(0, 15, 0, 6);
            if (s.Count != 17) return "points " + s.Count;
            if (s[0].X != 0 || s[0].Y != 0) return "apex not first";
            foreach (var p in s)
            {
                double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                if (r > 6 + 1e-9) return "outside radius " + r;
                if (r > 1e-9 && Math.Abs(Math.Atan2(p.X, p.Y)) * 180 / Math.PI > 15 + 1e-6) return "outside wedge";
            }
            double want = Math.PI * 36 * 30 / 360;
            double a = SubDatumMath.Area(s);
            if (Math.Abs(a - want) / want > 0.02) return "area " + a + " want " + want;
            if (!IsConvexCcw(s)) return "not convex ccw";
            var east = SubDatumMath.Sector(90, 15, 0, 6);
            if (east[1].X < 5.7 || Math.Abs(east[1].Y) > 1.7) return "east axis wrong " + east[1].X + "," + east[1].Y;
            var ring = SubDatumMath.Sector(0, 15, 2, 6);
            if (!IsConvexCcw(ring)) return "ring hull not convex";
            if (ring.Count < SubDatumMath.ArcPts + 2 || ring.Count > 2 * SubDatumMath.ArcPts) return "ring points " + ring.Count;
            return null;
        }

        internal static bool IsConvexCcw(IList<DxY> p)
        {
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                DxY a = p[i], b = p[(i + 1) % n], c = p[(i + 2) % n];
                double cr = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (cr < -1e-9) return false;
            }
            return true;
        }

        public static string HullConvex()
        {
            var rnd = new Random(7);
            var pts = new List<DxY>();
            for (int i = 0; i < 200; i++) pts.Add(new DxY(rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 10 - 5));
            var h = SubDatumMath.Hull(pts);
            if (h.Count < 3 || h.Count > 200) return "hull size " + h.Count;
            if (!IsConvexCcw(h)) return "hull not convex ccw";
            foreach (var p in pts) if (!SubDatumMath.Contains(h, p)) return "point outside hull";
            if (SubDatumMath.Hull(new List<DxY> { new DxY(1, 1) }).Count != 1) return "single point hull";
            return null;
        }

        public static string InflateSquare()
        {
            var sq = new List<DxY> { new DxY(-1, -1), new DxY(1, -1), new DxY(1, 1), new DxY(-1, 1) };
            var f = SubDatumMath.Inflate(sq, 1);
            double a = SubDatumMath.Area(f);
            if (Math.Abs(a - (4 + 8 + Math.PI)) > 0.15) return "area " + a;
            if (f.Count > SubDatumMath.MaxPolyPts) return "too many points " + f.Count;
            if (!IsConvexCcw(f)) return "not convex";
            if (!SubDatumMath.Contains(f, new DxY(2, 0)) || SubDatumMath.Contains(f, new DxY(2.2, 0))) return "edge offset wrong";
            var big = SubDatumMath.Inflate(Wedge(0, 30, 15), 2);
            if (big.Count > SubDatumMath.MaxPolyPts) return "wedge inflate " + big.Count;
            if (SubDatumMath.Inflate(sq, 0).Count != 4) return "zero buffer must keep polygon";
            return null;
        }

        public static string ClipSectors()
        {
            var a = Wedge(0, 15, 8);
            var b = new List<DxY>();
            foreach (var p in Wedge(20, 15, 8)) b.Add(new DxY(p.X + 1.0, p.Y - 1.0));
            var c = SubDatumMath.Clip(b, a);
            if (c.Count < 3) return "must intersect";
            if (!IsConvexCcw(c)) return "clip not convex";
            double ac = SubDatumMath.Area(c);
            if (ac > SubDatumMath.Area(a) + 1e-9 || ac > SubDatumMath.Area(b) + 1e-9) return "clip bigger than input";
            var far = new List<DxY>();
            foreach (var p in Wedge(0, 15, 3)) far.Add(new DxY(p.X + 50, p.Y));
            if (SubDatumMath.Clip(far, a).Count != 0) return "disjoint must be empty";
            var same = SubDatumMath.Clip(a, a);
            if (Math.Abs(SubDatumMath.Area(same) - SubDatumMath.Area(a)) > 1e-6) return "self clip area";
            return null;
        }

        public static string ContainsCases()
        {
            var sq = new List<DxY> { new DxY(0, 0), new DxY(2, 0), new DxY(2, 2), new DxY(0, 2) };
            if (!SubDatumMath.Contains(sq, new DxY(1, 1))) return "inside";
            if (SubDatumMath.Contains(sq, new DxY(3, 1))) return "outside";
            if (!SubDatumMath.Contains(sq, new DxY(2, 1))) return "edge counts as inside";
            if (SubDatumMath.Contains(new List<DxY>(), new DxY(0, 0))) return "empty";
            if (!Near(SubDatumMath.Area(sq), 4, 1e-9)) return "area";
            DxY c = SubDatumMath.Centroid(sq);
            if (!Near(c.X, 1, 1e-9) || !Near(c.Y, 1, 1e-9)) return "centroid";
            return null;
        }

        private static string AxisCheck(SubDatumAxisIn a, bool wantOk, double th, double sg, string src)
        {
            double t, s; string r;
            bool ok = SubDatumMath.Axis(a, out t, out s, out r);
            if (ok != wantOk) return "ok=" + ok;
            if (!wantOk) return null;
            if (!Near(t, th, 1e-6) || !Near(s, sg, 1e-9) || r != src) return "got " + t + "/" + s + "/" + r + " want " + th + "/" + sg + "/" + src;
            return null;
        }

        public static string AxisVel() { return AxisCheck(new SubDatumAxisIn { VelKn = 30, VelCourseDeg = 170 }, true, 350, 15, "vel"); }

        public static string AxisFixes()
        {
            string e = AxisCheck(new SubDatumAxisIn { VelKn = 3, VelCourseDeg = 10, FixDtS = 12, FixDistNm = 0.2, FixCourseDeg = 90 }, true, 270, 15, "fixes");
            if (e != null) return "fixes: " + e;
            e = AxisCheck(new SubDatumAxisIn { VelKn = -1, FixDtS = 8, FixDistNm = 0.5, FixCourseDeg = 90, LosBrgDeg = 123 }, true, 123, 30, "los");
            if (e != null) return "short fixes -> los: " + e;
            e = AxisCheck(new SubDatumAxisIn { VelKn = 0, FixDtS = 30, FixDistNm = 0.05, FixCourseDeg = 90, LosBrgDeg = 40 }, true, 40, 30, "los");
            return e == null ? null : "tiny fix distance -> los: " + e;
        }

        public static string AxisLos() { return AxisCheck(new SubDatumAxisIn { LosBrgDeg = 201.5 }, true, 201.5, 30, "los"); }

        public static string AxisNone() { return AxisCheck(new SubDatumAxisIn(), false, 0, 0, null); }

        public static string RunIdentified()
        {
            double r = SubDatumMath.RunNm(true, new List<double> { 8, 13.5 }, 10.26, 12);
            return Near(r, 13.5, 1e-9) ? null : "run " + r;
        }

        public static string RunMaxLaunch()
        {
            double r = SubDatumMath.RunNm(true, new List<double>(), 10.26, 12);
            if (!Near(r, 10.26, 1e-9)) return "empty stages " + r;
            r = SubDatumMath.RunNm(true, null, 10.26, 12);
            return Near(r, 10.26, 1e-9) ? null : "null stages " + r;
        }

        public static string RunUnknown()
        {
            if (!Near(SubDatumMath.RunNm(false, new List<double> { 13.5 }, 10.26, 12), 12, 1e-9)) return "not identified";
            if (!Near(SubDatumMath.RunNm(true, null, 0, 12), 12, 1e-9)) return "identified without data";
            return null;
        }

        public static string DtgtAhead()
        {
            var ships = new List<DxY> { new DxY(0, -3) };      // torpedo course 180 -> ahead is south
            double d = SubDatumMath.DTgtNm(180, ships);
            if (!Near(d, 3, 1e-9)) return "ahead " + d;
            ships.Add(new DxY(0.5, -7));
            if (!Near(SubDatumMath.DTgtNm(180, ships), 3, 1e-9)) return "nearest wins";
            return null;
        }

        public static string DtgtBehind() { return SubDatumMath.DTgtNm(180, new List<DxY> { new DxY(0, 3) }) == 0 ? null : "behind must be 0"; }

        public static string DtgtAside()
        {
            double a = 40 * Math.PI / 180;
            return SubDatumMath.DTgtNm(0, new List<DxY> { new DxY(3 * Math.Sin(a), 3 * Math.Cos(a)) }) == 0 ? null : "40 deg aside must be 0";
        }

        public static string RmaxClamp()
        {
            if (!Near(SubDatumMath.Rmax(12, 3, 15), 9, 1e-9)) return "12-3";
            if (!Near(SubDatumMath.Rmax(12, 11, 15), 2, 1e-9)) return "min";
            if (!Near(SubDatumMath.Rmax(20, 0, 15), 15, 1e-9)) return "max";
            return null;
        }

        public static string RectCore()
        {
            var core = Wedge(0, 15, 6);
            double x0, y0, x1, y1;
            SubDatumMath.AreaRect(core, 10, out x0, out y0, out x1, out y1);
            if (x1 - x0 > 10 || y1 - y0 > 10) return "side > 10";
            if (!Near(y1, 6, 0.01) || !Near(y0, 0, 1e-9)) return "box y " + y0 + ".." + y1;
            foreach (var p in core) if (p.X < x0 - 1e-9 || p.X > x1 + 1e-9) return "core outside box";
            return null;
        }

        public static string RectBig()
        {
            var big = SubDatumMath.Inflate(Wedge(0, 30, 12), 6);
            double x0, y0, x1, y1;
            SubDatumMath.AreaRect(big, 10, out x0, out y0, out x1, out y1);
            if (!Near(x1 - x0, 10, 1e-9) || !Near(y1 - y0, 10, 1e-9)) return "must be square 10: " + (x1 - x0) + "x" + (y1 - y0);
            DxY c = SubDatumMath.Centroid(big);
            if (!Near((x0 + x1) / 2, c.X, 1e-9) || !Near((y0 + y1) / 2, c.Y, 1e-9)) return "not around centroid";
            return null;
        }

        public static string RectGeoOrder()
        {
            double[] r = SubDatumMath.RectGeo(35, 20, -1, -2, 3, 4, true);
            if (r == null || r.Length != 8) return "length";
            // NW, NE, SE, SW; lat = 35 + y/60
            if (!Near(r[0], 35 + 4 / 60.0, 1e-9) || !Near(r[2], r[0], 1e-12) || !Near(r[4], 35 - 2 / 60.0, 1e-9) || !Near(r[6], r[4], 1e-12)) return "lat order";
            if (!(r[1] < r[3]) || !Near(r[1], r[7], 1e-12) || !Near(r[3], r[5], 1e-12)) return "lon order";
            double cos = Math.Cos(35 * Math.PI / 180);
            if (!Near(r[1], 20 - 1 / (60 * cos), 1e-9) || !Near(r[3], 20 + 3 / (60 * cos), 1e-9)) return "lon values";
            return null;
        }

        private static SubDatumGroupIn G(string name, string order, int usable, double eta) { return new SubDatumGroupIn { Name = name, Order = order, Usable = usable, EtaS = eta }; }

        public static string PickSkip()
        {
            var g = new List<SubDatumGroupIn> { G("A", "course", 2, 0), G("B", "strike", 2, 0), G("C", "sar", 2, 0) };
            if (SubDatumMath.PickGroup(g) != -1) return "course/strike/sar must be skipped";
            if (SubDatumMath.PickGroup(new List<SubDatumGroupIn>()) != -1) return "empty";
            return null;
        }

        public static string PickOrder()
        {
            var g = new List<SubDatumGroupIn> { G("A", "area", 1, 100), G("B", "none", 1, 500) };
            if (SubDatumMath.PickGroup(g) != 1) return "none before area";
            g = new List<SubDatumGroupIn> { G("A", "none", 1, 500), G("B", "none", 1, 200), G("C", "none", 1, 200) };
            if (SubDatumMath.PickGroup(g) != 1) return "smaller eta, then name";
            return null;
        }

        public static string PickUsable()
        {
            var g = new List<SubDatumGroupIn> { G("A", "none", 0, 1), G("B", "area", 1, 900) };
            return SubDatumMath.PickGroup(g) == 1 ? null : "Usable=0 must be excluded";
        }


    }
}
