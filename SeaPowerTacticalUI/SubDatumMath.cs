using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: pure geometry of the "sub launch area" sector (no Unity, no game). Flat earth around the first
    // detection point P0: miles, x = east, y = north; bearings in degrees from north, clockwise.

    internal struct DxY
    {
        public double X, Y;
        public DxY(double x, double y) { X = x; Y = y; }
    }

    internal sealed class SubDatumParams
    {
        public bool Enabled = true, Map = true;
        public string Auto = "off";                       // off | idle
        public double TriggerNm = 12, UnknownRunNm = 12, MaxLenNm = 15, CoreNm = 6, SubKn = 5;
        public double TtlS = 1800, MergeS = 600, RefineS = 60, FoundCloseS = 120, AreaMaxNm = 10;
        public double AirSkipNm = 3, AirSkipS = 60;
        public int MaxLive = 3;
    }

    internal sealed class SubDatumAxisIn
    {
        public double VelKn = -1, VelCourseDeg;             // track UnityVelocityVector; VelKn < 0 = unknown
        public double FixDtS = -1, FixDistNm, FixCourseDeg; // two own fixes of the track (first -> last); FixDtS < 0 = none
        public double LosBrgDeg = double.NaN;               // bearing observer -> P0; NaN = no observer
    }

    internal sealed class SubDatumGroupIn
    {
        public string Name, Order;                          // none | area | course | strike | sar
        public int Usable;                                  // members Capable && Usable
        public double EtaS = double.PositiveInfinity;       // min ETA of a usable member to the core centre
        public double[] Rect; public List<string> Tactics = new List<string>(); public string Depth;   // snapshot of an "area" order (for "Undo")
    }

    internal static class SubDatumMath
    {
        public const double SigmaFineDeg = 15, SigmaLosDeg = 30, VelMinKn = 5, FixMinS = 10, FixMinNm = 0.15, MinLenNm = 2;
        public const int ArcPts = 16, CirclePts = 16, MaxPolyPts = 64;
        private const double Eps = 1e-9;

        /// <summary>T-352: globe = the beta world (1 deg of longitude = 60 cos(lat) nm), otherwise the flat world of stable (60 nm on both axes).</summary>
        public static DxY ToLocal(double lat0, double lon0, double lat, double lon, bool globe)
        {
            double c = globe ? Math.Cos(lat0 * Math.PI / 180) : 1.0;
            return new DxY((lon - lon0) * 60 * c, (lat - lat0) * 60);
        }

        public static void ToGeo(double lat0, double lon0, DxY p, bool globe, out double lat, out double lon)
        {
            double c = globe ? Math.Cos(lat0 * Math.PI / 180) : 1.0;
            lat = lat0 + p.Y / 60;
            lon = lon0 + (c > 1e-9 ? p.X / (60 * c) : 0);
        }

        private static double Norm360(double a)
        {
            a %= 360;
            return a < 0 ? a + 360 : a;
        }

        private static DxY Polar(double brgDeg, double r)
        {
            double a = brgDeg * Math.PI / 180;
            return new DxY(r * Math.Sin(a), r * Math.Cos(a));
        }

        /// <summary>Axis theta = torpedo course + 180 deg: velocity (>= 5 kn), else two fixes (>= 10 s, >= 0.15 nm), else the line of sight.</summary>
        public static bool Axis(SubDatumAxisIn a, out double thetaDeg, out double sigmaDeg, out string src)
        {
            thetaDeg = 0; sigmaDeg = 0; src = null;
            if (a == null) return false;
            if (a.VelKn >= VelMinKn) { thetaDeg = Norm360(a.VelCourseDeg + 180); sigmaDeg = SigmaFineDeg; src = "vel"; return true; }
            if (a.FixDtS >= FixMinS && a.FixDistNm >= FixMinNm) { thetaDeg = Norm360(a.FixCourseDeg + 180); sigmaDeg = SigmaFineDeg; src = "fixes"; return true; }
            if (!double.IsNaN(a.LosBrgDeg)) { thetaDeg = Norm360(a.LosBrgDeg); sigmaDeg = SigmaLosDeg; src = "los"; return true; }
            return false;
        }

        /// <summary>identified: max(stageRangesNm), else maxLaunchNm (> 0), else unknownRunNm; not identified: unknownRunNm.</summary>
        public static double RunNm(bool identified, IList<double> stageRangesNm, double maxLaunchNm, double unknownRunNm)
        {
            if (!identified) return unknownRunNm;
            double best = 0;
            if (stageRangesNm != null) foreach (double r in stageRangesNm) if (r > best) best = r;
            if (best > 0) return best;
            return maxLaunchNm > 0 ? maxLaunchNm : unknownRunNm;
        }

        /// <summary>Distance from P0 to the nearest own ship ahead of the torpedo (|bearing - course| &lt;= 30 deg); 0 = none.</summary>
        public static double DTgtNm(double torpCourseDeg, IList<DxY> ownShipsFromP0)
        {
            double best = 0;
            if (ownShipsFromP0 == null) return 0;
            foreach (DxY s in ownShipsFromP0)
            {
                double d = Math.Sqrt(s.X * s.X + s.Y * s.Y);
                if (d < Eps) continue;
                double brg = Math.Atan2(s.X, s.Y) * 180 / Math.PI;
                double diff = Math.Abs(Norm360(brg - torpCourseDeg + 180) - 180);
                if (diff > 30) continue;
                if (best == 0 || d < best) best = d;
            }
            return best;
        }

        public static double Rmax(double runNm, double dTgtNm, double maxLenNm)
        {
            double r = runNm - dTgtNm;
            if (r > maxLenNm) r = maxLenNm;
            if (r < MinLenNm) r = MinLenNm;
            return r;
        }

        /// <summary>Convex sector theta +- sigma between r0 and r1 (r0 = 0: apex at P0 + the arc), ArcPts per arc; counter-clockwise.</summary>
        public static List<DxY> Sector(double thetaDeg, double sigmaDeg, double r0Nm, double r1Nm)
        {
            var pts = new List<DxY>();
            if (r0Nm <= Eps) pts.Add(new DxY(0, 0));
            for (int i = 0; i < ArcPts; i++)
                pts.Add(Polar(thetaDeg + sigmaDeg - 2 * sigmaDeg * i / (ArcPts - 1), r1Nm));
            if (r0Nm > Eps)
            {
                for (int i = 0; i < ArcPts; i++)
                    pts.Add(Polar(thetaDeg - sigmaDeg + 2 * sigmaDeg * i / (ArcPts - 1), r0Nm));
                return Hull(pts);   // the ring is not convex: take its hull
            }
            return pts;
        }

        private static double Cross(DxY o, DxY a, DxY b) { return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X); }

        /// <summary>Monotone chain, counter-clockwise, no duplicates.</summary>
        public static List<DxY> Hull(IList<DxY> pts)
        {
            var p = new List<DxY>(pts);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            var u = new List<DxY>();
            foreach (DxY q in p) if (u.Count == 0 || Math.Abs(u[u.Count - 1].X - q.X) > Eps || Math.Abs(u[u.Count - 1].Y - q.Y) > Eps) u.Add(q);
            if (u.Count < 3) return u;
            var h = new List<DxY>();
            for (int i = 0; i < u.Count; i++)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], u[i]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(u[i]);
            }
            int lower = h.Count + 1;
            for (int i = u.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], u[i]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(u[i]);
            }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        /// <summary>Hull of each vertex +- a circle of radius bufNm (CirclePts points, axis aligned), at most MaxPolyPts points.</summary>
        public static List<DxY> Inflate(IList<DxY> convex, double bufNm)
        {
            if (convex == null || convex.Count == 0) return new List<DxY>();
            if (bufNm <= Eps) return new List<DxY>(convex);
            var all = new List<DxY>(convex.Count * CirclePts);
            foreach (DxY v in convex)
                for (int k = 0; k < CirclePts; k++)
                {
                    double a = 2 * Math.PI * k / CirclePts;
                    all.Add(new DxY(v.X + bufNm * Math.Cos(a), v.Y + bufNm * Math.Sin(a)));
                }
            var h = Hull(all);
            if (h.Count <= MaxPolyPts) return h;
            var d = new List<DxY>(MaxPolyPts);
            for (int i = 0; i < MaxPolyPts; i++) d.Add(h[(int)((long)i * h.Count / MaxPolyPts)]);
            return d;
        }

        /// <summary>Sutherland-Hodgman against a convex counter-clockwise clip polygon; empty = no intersection.</summary>
        public static List<DxY> Clip(IList<DxY> subject, IList<DxY> clipConvex)
        {
            var output = new List<DxY>(subject);
            int n = clipConvex.Count;
            for (int i = 0; i < n && output.Count > 0; i++)
            {
                DxY a = clipConvex[i], b = clipConvex[(i + 1) % n];
                var input = output; output = new List<DxY>();
                for (int j = 0; j < input.Count; j++)
                {
                    DxY cur = input[j], prev = input[(j + input.Count - 1) % input.Count];
                    double dc = Cross(a, b, cur), dp = Cross(a, b, prev);
                    bool inC = dc >= -Eps, inP = dp >= -Eps;
                    if (inC != inP)
                    {
                        double t = dp / (dp - dc);
                        output.Add(new DxY(prev.X + (cur.X - prev.X) * t, prev.Y + (cur.Y - prev.Y) * t));
                    }
                    if (inC) output.Add(cur);
                }
            }
            if (output.Count < 3 || Area(output) < 1e-9) return new List<DxY>();
            return output;
        }

        public static bool Contains(IList<DxY> convex, DxY p)
        {
            int n = convex == null ? 0 : convex.Count;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
                if (Cross(convex[i], convex[(i + 1) % n], p) < -1e-9) return false;
            return true;
        }

        public static double Area(IList<DxY> poly)
        {
            double s = 0;
            int n = poly == null ? 0 : poly.Count;
            for (int i = 0; i < n; i++) { DxY a = poly[i], b = poly[(i + 1) % n]; s += a.X * b.Y - b.X * a.Y; }
            return Math.Abs(s) / 2;
        }

        public static DxY Centroid(IList<DxY> poly)
        {
            int n = poly == null ? 0 : poly.Count;
            if (n == 0) return new DxY(0, 0);
            double a2 = 0, cx = 0, cy = 0;
            for (int i = 0; i < n; i++)
            {
                DxY a = poly[i], b = poly[(i + 1) % n];
                double w = a.X * b.Y - b.X * a.Y;
                a2 += w; cx += (a.X + b.X) * w; cy += (a.Y + b.Y) * w;
            }
            if (Math.Abs(a2) < 1e-12)
            {
                double mx = 0, my = 0;
                foreach (DxY p in poly) { mx += p.X; my += p.Y; }
                return new DxY(mx / n, my / n);
            }
            return new DxY(cx / (3 * a2), cy / (3 * a2));
        }

        /// <summary>Axis-aligned box of the polygon; a side > maxSideNm -> a square of maxSideNm around the centroid.</summary>
        public static void AreaRect(IList<DxY> poly, double maxSideNm, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            foreach (DxY p in poly)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            if (poly.Count == 0) { minX = minY = maxX = maxY = 0; return; }
            if (maxX - minX > maxSideNm || maxY - minY > maxSideNm)
            {
                DxY c = Centroid(poly);
                minX = c.X - maxSideNm / 2; maxX = c.X + maxSideNm / 2;
                minY = c.Y - maxSideNm / 2; maxY = c.Y + maxSideNm / 2;
            }
        }

        /// <summary>The 8 numbers lat/lon of asw_search_area: NW, NE, SE, SW.</summary>
        public static double[] RectGeo(double lat0, double lon0, double minX, double minY, double maxX, double maxY, bool globe)
        {
            double latN, lonW, latS, lonE;
            ToGeo(lat0, lon0, new DxY(minX, maxY), globe, out latN, out lonW);
            ToGeo(lat0, lon0, new DxY(maxX, minY), globe, out latS, out lonE);
            return new[] { latN, lonW, latN, lonE, latS, lonE, latS, lonW };
        }

        /// <summary>Best group for the button: Order none|area, Usable >= 1; none before area, then EtaS, then Name. -1 = none.</summary>
        public static int PickGroup(IList<SubDatumGroupIn> groups)
        {
            int best = -1;
            if (groups == null) return -1;
            for (int i = 0; i < groups.Count; i++)
            {
                SubDatumGroupIn g = groups[i];
                if (g == null || g.Usable < 1 || (g.Order != "none" && g.Order != "area")) continue;
                if (best < 0 || Better(g, groups[best])) best = i;
            }
            return best;
        }

        private static bool Better(SubDatumGroupIn a, SubDatumGroupIn b)
        {
            int ra = a.Order == "none" ? 0 : 1, rb = b.Order == "none" ? 0 : 1;
            if (ra != rb) return ra < rb;
            if (a.EtaS != b.EtaS) return a.EtaS < b.EtaS;
            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "") < 0;
        }

        /// <summary>Short smoke for the mod selfcheck (sector, clip, pick); the full cases live in datumtest.</summary>
        public static string SelfTest()
        {
            var s = Sector(0, 15, 0, 6);
            if (s.Count != ArcPts + 1 || Math.Abs(Area(s) - Math.PI * 36 * 30 / 360) > 0.2) return "sector area " + Area(s);
            var shifted = new List<DxY>();
            foreach (DxY p in Sector(0, 15, 0, 6)) shifted.Add(new DxY(p.X, p.Y - 2));
            var c = Clip(shifted, s);
            if (c.Count < 3 || Area(c) >= Area(s)) return "clip";
            var g = new List<SubDatumGroupIn>
            {
                new SubDatumGroupIn { Name = "A", Order = "area", Usable = 1, EtaS = 10 },
                new SubDatumGroupIn { Name = "B", Order = "none", Usable = 1, EtaS = 99 },
                new SubDatumGroupIn { Name = "C", Order = "strike", Usable = 1, EtaS = 1 },
            };
            if (PickGroup(g) != 1) return "pick";
            return null;
        }
    }
}
