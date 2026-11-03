using System;

namespace VoiceBotBridge
{
    // T-362 (ADR-0135 R5): ToGeo/ToLocal on the globe are azimuthal equidistant about the origin. `aswtest t362 [flat|globe|wrap|pole|rect]`.
    internal static class AswT362Tests
    {
        private const double Deg = Math.PI / 180.0;

        public static string Run(string only)
        {
            string e;
            if ((only == null || only == "flat") && (e = Flat()) != null) return "flat: " + e;
            if ((only == null || only == "globe") && (e = Globe()) != null) return "globe: " + e;
            if ((only == null || only == "wrap") && (e = Wrap()) != null) return "wrap: " + e;
            if ((only == null || only == "pole") && (e = Pole()) != null) return "pole: " + e;
            if ((only == null || only == "rect") && (e = Rect()) != null) return "rect: " + e;
            return null;
        }

        private static bool Same(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }

        // The old code, verbatim (LonK included).
        private static GeoPt OldToGeo(GeoPt origin, double axisDeg, NmPt p)
        {
            double a = axisDeg * Deg;
            double north = p.X * Math.Cos(a) - p.Y * Math.Sin(a);
            double east = p.X * Math.Sin(a) + p.Y * Math.Cos(a);
            double lat = origin.Lat + north / 60.0;
            return new GeoPt(lat, origin.Lon + east / (60.0 * AswMath.LonK(origin.Lat, lat)));
        }

        private static NmPt OldToLocal(GeoPt origin, double axisDeg, GeoPt g)
        {
            double a = axisDeg * Deg;
            double north = (g.Lat - origin.Lat) * 60.0;
            double east = GeoLon.DLon(origin.Lon, g.Lon) * 60.0 * AswMath.LonK(origin.Lat, g.Lat);
            return new NmPt(north * Math.Cos(a) + east * Math.Sin(a), -north * Math.Sin(a) + east * Math.Cos(a));
        }

        private static string Flat()
        {
            var rnd = new Random(3621);
            using (AswMath.WorldScope(false))
                for (int i = 0; i < 10000; i++)
                {
                    var o = new GeoPt(rnd.NextDouble() * 170 - 85, rnd.NextDouble() * 360 - 180);
                    double ax = rnd.NextDouble() * 360;
                    var p = new NmPt(rnd.NextDouble() * 200 - 100, rnd.NextDouble() * 200 - 100);
                    var g1 = AswMath.ToGeo(o, ax, p); var g2 = OldToGeo(o, ax, p);
                    if (!Same(g1.Lat, g2.Lat) || !Same(g1.Lon, g2.Lon)) return "ToGeo bits " + i;
                    var g = new GeoPt(o.Lat + rnd.NextDouble() * 3 - 1.5, o.Lon + rnd.NextDouble() * 3 - 1.5);
                    var l1 = AswMath.ToLocal(o, ax, g); var l2 = OldToLocal(o, ax, g);
                    if (!Same(l1.X, l2.X) || !Same(l1.Y, l2.Y)) return "ToLocal bits " + i;
                }
            return null;
        }

        private static string Globe()
        {
            var rnd = new Random(3622);
            using (AswMath.WorldScope(true))
                foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
                    for (int i = 0; i < 400; i++)
                    {
                        var o = new GeoPt(phi, 10 + rnd.NextDouble() * 5);
                        double ax = rnd.NextDouble() * 360;
                        var p = new NmPt(rnd.NextDouble() * 80 - 40, rnd.NextDouble() * 80 - 40);
                        var q = new NmPt(rnd.NextDouble() * 80 - 40, rnd.NextDouble() * 80 - 40);
                        string w = " phi=" + phi + " ax=" + ax;
                        var g = AswMath.ToGeo(o, ax, p);
                        double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                        if (r > 0.5 && Math.Abs(AswMath.GreatCircleNm(o, g) / r - 1) > 0.00005) return "radius" + w;
                        var gq = AswMath.ToGeo(o, ax, q);
                        double seg = Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
                        if (seg > 5 && Math.Abs(AswMath.GreatCircleNm(g, gq) / seg - 1) > 0.005) return "segment " + AswMath.GreatCircleNm(g, gq) + " vs " + seg + w;
                        var back = AswMath.ToLocal(o, ax, g);
                        if (Math.Abs(back.X - p.X) > 1e-6 || Math.Abs(back.Y - p.Y) > 1e-6) return "round trip" + w;
                    }
            return null;
        }

        private static string Wrap()
        {
            using (AswMath.WorldScope(true))
            {
                var a = AswMath.ToGeo(new GeoPt(60, 179.9), 0, new NmPt(0, 20));
                var b = AswMath.ToGeo(new GeoPt(60, 169.9), 0, new NmPt(0, 20));
                if (Math.Abs(a.Lon - (b.Lon + 10)) > 1e-9 || Math.Abs(a.Lat - b.Lat) > 1e-9) return "continuous lon " + a.Lon + " vs " + (b.Lon + 10);
                if (a.Lon <= 179.9) return "no wrap expected " + a.Lon;
                var back = AswMath.ToLocal(new GeoPt(60, 179.9), 0, new GeoPt(a.Lat, a.Lon - 360));
                if (Math.Abs(back.Y - 20) > 1e-6) return "ToLocal across " + back.Y;
            }
            return null;
        }

        private static string Pole()
        {
            using (AswMath.WorldScope(true))
            {
                var o = new GeoPt(89.5, 20);
                var p = new NmPt(3, 4);
                var g = AswMath.ToGeo(o, 30, p); var old = OldToGeo(o, 30, p);
                if (!Same(g.Lat, old.Lat) || !Same(g.Lon, old.Lon)) return "ToGeo fallback";
                var g2 = new GeoPt(89.6, 21);
                var l = AswMath.ToLocal(o, 30, g2); var ol = OldToLocal(o, 30, g2);
                if (!Same(l.X, ol.X) || !Same(l.Y, ol.Y)) return "ToLocal fallback";
            }
            return null;
        }

        private static string Rect()
        {
            using (AswMath.WorldScope(true))
                foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
                {
                    var c = new GeoPt(phi, 10);
                    var r = AswController.RectAround(c, 60, 60);
                    var pts = new GeoPt[4];
                    for (int i = 0; i < 4; i++) pts[i] = new GeoPt(r[2 * i], r[2 * i + 1]);
                    for (int i = 0; i < 4; i++)
                    {
                        double d = AswMath.GreatCircleNm(pts[i], pts[(i + 1) % 4]);
                        if (Math.Abs(d / 60 - 1) > 0.005) return "side " + i + " " + d + " phi=" + phi;
                    }
                    double n, e; GeoPt cc;
                    AswMath.RectSpans(r, out cc, out n, out e);
                    if (Math.Abs(n / 60 - 1) > 0.005 || Math.Abs(e / 60 - 1) > 0.005) return "spans " + n + " " + e + " phi=" + phi;
                }
            return null;
        }
    }
}
