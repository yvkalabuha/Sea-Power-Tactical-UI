using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-348 F: pattern geometry and the route guard at latitudes 0/45/60/70 in both worlds.
    internal static class PatternLatTests
    {
        private static readonly double[] Lats = { 0, 45, 60, 70 };

        private static string Pts(GeoPt o, double axis, IList<NmPt> pts, string what, double tol)
        {
            for (int i = 1; i < pts.Count; i++)
            {
                double want = Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
                if (want < 0.5) continue;
                double got = AswMath.GreatCircleNm(AswMath.ToGeo(o, axis, pts[i - 1]), AswMath.ToGeo(o, axis, pts[i]));
                if (Math.Abs(got / want - 1) > tol) return what + " step " + i + " " + got + " vs " + want;
            }
            return null;
        }

        public static string Run()
        {
            foreach (double lat in Lats)
            {
                var o = new GeoPt(lat, 10);
                foreach (double axis in new[] { 0.0, 45.0, 90.0 })
                {
                    string err;
                    using (AswMath.WorldScope(true))
                    {
                        int dropped;
                        var rows = AswMath.Rows(5, 30, 100, 3600, 8, out dropped);
                        if ((err = Pts(o, axis, rows, "rows " + lat + "/" + axis, 0.005)) != null) return err;
                        var ring = AswMath.StandingRing(8, 1);
                        foreach (var p in ring)
                        {
                            double d = AswMath.GreatCircleNm(o, AswMath.ToGeo(o, axis, p));
                            double want = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                            if (want > 0.5 && Math.Abs(d / want - 1) > 0.005) return "ring " + lat + "/" + axis + " " + d + " vs " + want;
                        }
                        if ((err = Pts(o, axis, AswMath.CreepingLine(20, 10, 4), "creeping " + lat + "/" + axis, 0.005)) != null) return err;
                        if ((err = Pts(o, axis, AswMath.ExpandingSquare(new NmPt(0, 0), 2, 6), "square " + lat + "/" + axis, 0.005)) != null) return err;
                    }
                }
                // T-362: radius from the origin, round trip, corridor and barrier rows
                using (AswMath.WorldScope(true))
                {
                    for (double x = -40; x <= 40; x += 10)
                        for (double y = -40; y <= 40; y += 10)
                        {
                            var pt = new NmPt(x, y);
                            var g = AswMath.ToGeo(o, 45, pt);
                            double want = Math.Sqrt(x * x + y * y);
                            if (want > 0.5 && Math.Abs(AswMath.GreatCircleNm(o, g) / want - 1) > 0.005) return "radius " + lat + " " + x + "," + y;
                            var back = AswMath.ToLocal(o, 45, g);
                            if (Math.Abs(back.X - x) > 1e-6 || Math.Abs(back.Y - y) > 1e-6) return "round trip " + lat + " " + x + "," + y;
                        }
                    bool wide;
                    foreach (double ax in new[] { 0.0, 45.0 })
                    {
                        var cr = AswPatternMath.CorridorRows(60, 5, 8, out wide);
                        for (int i = 1; i < cr.Length; i++)
                        {
                            double step = cr[i] - cr[i - 1];
                            if (Math.Abs(step) < 0.5) continue;
                            double got = AswMath.GreatCircleNm(AswMath.ToGeo(o, ax, new NmPt(0, cr[i - 1])), AswMath.ToGeo(o, ax, new NmPt(0, cr[i])));
                            if (Math.Abs(got / Math.Abs(step) - 1) > 0.005) return "corridor " + lat + "/" + ax + " " + got + " vs " + step;
                        }
                        double stepNm;
                        var ys = AswPatternMath.BarrierRowYs(60, 5, 0, false, out stepNm);
                        for (int i = 1; i < ys.Count; i++)
                        {
                            double got = AswMath.GreatCircleNm(AswMath.ToGeo(o, ax, new NmPt(0, ys[i - 1])), AswMath.ToGeo(o, ax, new NmPt(0, ys[i])));
                            double want = Math.Abs(ys[i] - ys[i - 1]);
                            if (want > 0.5 && Math.Abs(got / want - 1) > 0.005) return "barrier " + lat + "/" + ax + " " + got + " vs " + want;
                        }
                    }
                }
                // route guard: a point 2 true nm east of a meridian segment
                var a = new GeoPt(lat, 10);
                var b = AswMath.Offset(a, 0, 20);
                using (AswMath.WorldScope(true))
                {
                    var c = AswMath.Offset(AswMath.Offset(a, 0, 10), 90, 2);
                    double d = AswRouteGuard.SegDistNm(a, b, c);
                    if (Math.Abs(d - 2) > 0.03) return "guard globe " + lat + " " + d;
                }
                using (AswMath.WorldScope(false))
                {
                    var c = AswMath.Offset(AswMath.Offset(a, 0, 10), 90, 2);
                    double d = AswRouteGuard.SegDistNm(a, b, c);
                    if (Math.Abs(d - 2) > 1e-6) return "guard flat " + lat + " " + d;
                    // bit-identical to the old flat formula
                    double bx = (b.Lat - a.Lat) * 60.0, by = (b.Lon - a.Lon) * 60.0, cx = (c.Lat - a.Lat) * 60.0, cy = (c.Lon - a.Lon) * 60.0;
                    double len2 = bx * bx + by * by;
                    double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (cx * bx + cy * by) / len2));
                    double dx = cx - t * bx, dy = cy - t * by;
                    if (Math.Sqrt(dx * dx + dy * dy) != d) return "guard flat not bit-identical " + lat;
                }
            }
            return null;
        }
    }
}
