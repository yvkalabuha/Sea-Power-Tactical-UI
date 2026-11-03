// T-309 A1-A3: reference round trip, forward/inverse against the beta formulas, sphere helpers.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.ProjTest
{
    internal static class T
    {
        public static double Ulp(double v)
        {
            v = Math.Abs(v);
            if (!(v >= 1e-30)) return 1e-45;
            return Math.Pow(2.0, Math.Floor(Math.Log(v, 2.0)) - 23);
        }

        public static double WrapDeg(double d) { d %= 360.0; if (d > 180) d -= 360; if (d <= -180) d += 360; return d; }

        public static double FullDiscScale(double minSide) { return minSide / (2.0 * GameRef.R); }

        public static string Lines(List<string> bad) { return bad.Count == 0 ? null : string.Join("; ", bad.GetRange(0, Math.Min(bad.Count, 6))) + " (" + bad.Count + " bad)"; }
    }

    internal static class GameRefCases
    {
        public static string Run()
        {
            var f = GameRef.FromCenter(37, 59);
            double worst = 0;
            var rnd = new Random(1);
            for (int i = 0; i < 2000; i++)
            {
                double lat = rnd.NextDouble() * 160 - 80, lon = rnd.NextDouble() * 360 - 180;
                D3 v = GameRef.GeoToViewFrame(lat, lon, f);
                D3 u = GameRef.ViewFrameToUnit(v, f);
                double la, lo; GameRef.UnitToGeo(u, out la, out lo);
                worst = Math.Max(worst, Math.Max(Math.Abs(la - lat), Math.Abs(T.WrapDeg(lo - lon))));
                D3 u2 = GameRef.GeoToUnit(lat, lon); D3 v2 = GameRef.UnitToViewFrame(u2, f);
                if (Math.Abs(v2.X - v.X) + Math.Abs(v2.Y - v.Y) + Math.Abs(v2.Z - v.Z) > 1e-12) return "frame vs unit path";
            }
            if (worst >= 1e-9) return "round trip " + worst;
            // MapViewport / Inverse round trip through the double ECS path
            GameRef.View view = GameRef.View.Make(0, 0, 0, 0, 0.5, 37, 960f, 540f);
            double x, y; bool vis;
            view.EcsView(0.01, 0.02, out x, out y, out vis);
            double ilat, ilon;
            if (!view.EcsInverse(x, y, out ilat, out ilon) || Math.Abs(ilat - 0.01) > 1e-9 || Math.Abs(ilon - 0.02) > 1e-9) return "ecs inverse " + ilat + "," + ilon;
            Console.WriteLine("  gameref round trip worst deg " + worst);
            return null;
        }
    }

    internal static class ProjectorCases
    {
        private static readonly double[] Rots = { 0, 37, 180 };
        private static readonly double[][] Centers = { new[] { 0.0, 0.0 }, new[] { 60.0, 179.9 }, new[] { -75.0, 0.0 }, new[] { 0.0, 179.9 }, new[] { 60.0, 0.0 } };

        public static string Run()
        {
            float zFull = GameRef.ScaleToZoomLevel(T.FullDiscScale(1080));
            float[] zooms = { zFull, 4, 8, 12, 15, 18, 20 };
            var bad = new List<string>();
            double worstEcs = 0, worstFloat = 0, worstInv = 0, worstFloatRel = 0;
            int n = 0, offDisc = 0;
            var rnd = new Random(7);
            for (int pass = 0; pass < 2; pass++)
                foreach (double[] c in Centers)
                    foreach (double rot in Rots)
                        foreach (float zoom in zooms)
                        {
                            double scale = GameRef.ZoomLevelToScale(zoom);
                            // pass 1: the view centre differs from the projection centre
                            double clat = c[0], clon = c[1];
                            if (pass == 1) { clat = Math.Max(-85, Math.Min(85, c[0] + 0.2)); clon = c[1] - 0.3; }
                            GameRef.View rv = GameRef.View.Make(c[0], c[1], clat, clon, scale, rot, 960f, 540f);
                            MapViewSnap s = MapViewSnap.Ortho(c[0], c[1], scale, rot, clat, clon, rv.ViewRefX, rv.ViewRefY, 1920, 1080);
                            if (!s.Valid) { bad.Add("invalid snap"); continue; }
                            double mapMag = Math.Abs(rv.MapRefX) + Math.Abs(rv.MapRefY) + 1.0;
                            // the float Noesis matrix: terms of size S * |map| cancel to view pixels, so its noise is a few ulp of S * |map|; plus the float map point
                            double tolF = 1.0 + 4.0 * T.Ulp(scale * mapMag) + 2.0 * scale * T.Ulp(mapMag);
                            for (int k = 0; k < 81 + 40; k++)
                            {
                                double px = k < 81 ? (k % 9) / 8.0 * 1920 : (rnd.NextDouble() * 1.4 - 0.2) * 1920;
                                double py = k < 81 ? (k / 9) / 8.0 * 1080 : (rnd.NextDouble() * 1.4 - 0.2) * 1080;
                                double lat, lon;
                                bool on = rv.EcsInverse(px, py, out lat, out lon);
                                double ilat, ilon;
                                bool ours = MapProjector.ViewToGeo(in s, px, py, out ilat, out ilon);
                                if (on != ours) { bad.Add("disc flag z=" + zoom + " px=" + px + "," + py + " ref=" + on + " ours=" + ours); continue; }
                                if (!on) { offDisc++; continue; }
                                n++;
                                double dInv = Math.Max(Math.Abs(ilat - lat), Math.Abs(T.WrapDeg(ilon - lon)) * Math.Max(0.0, Math.Cos(lat * Math.PI / 180)));
                                if (Math.Abs(lat) > 89.999) dInv = Math.Abs(ilat - lat);
                                worstInv = Math.Max(worstInv, dInv);
                                if (dInv > 1e-6) bad.Add("inverse " + dInv + " z=" + zoom);
                                double ex, ey, ox, oy; bool evis;
                                rv.EcsView(lat, lon, out ex, out ey, out evis);
                                bool ovis = MapProjector.GeoToView(in s, lat, lon, out ox, out oy);
                                if (!evis || !ovis) continue;   // exactly on the limb
                                double dE = Math.Max(Math.Abs(ox - ex), Math.Abs(oy - ey));
                                worstEcs = Math.Max(worstEcs, dE);
                                if (dE > 1e-6) bad.Add("ecs " + dE + " z=" + zoom + " rot=" + rot + " c=" + c[0] + "," + c[1]);
                                float fx, fy; bool fvis;
                                rv.LocationToView(lat, lon, out fx, out fy, out fvis);
                                double dF = Math.Max(Math.Abs(ox - fx), Math.Abs(oy - fy));
                                worstFloat = Math.Max(worstFloat, dF);
                                worstFloatRel = Math.Max(worstFloatRel, dF / tolF);
                                if (dF > tolF) bad.Add("float " + dF + " > " + tolF + " z=" + zoom + " rot=" + rot);
                                // round trip of ours
                                double rlat, rlon;
                                if (!MapProjector.ViewToGeo(in s, ox, oy, out rlat, out rlon)) continue;
                                double dRt = Math.Max(Math.Abs(rlat - lat), Math.Abs(T.WrapDeg(rlon - lon)) * Math.Max(0.0, Math.Cos(lat * Math.PI / 180)));
                                if (Math.Abs(lat) > 89.999) dRt = Math.Abs(rlat - lat);
                                if (dRt > 1e-6) bad.Add("round trip " + dRt);
                            }
                        }
            // mapRef != 0 when the projection centre differs from the view centre
            MapViewSnap sc = MapViewSnap.Ortho(0, 0, 0.01, 0, 1, 1, 960, 540, 1920, 1080);
            if (Math.Abs(sc.MapRefX) < 1 || Math.Abs(sc.MapRefY) < 1) return "mapRef is zero for centre != projection centre";
            MapViewSnap s0 = MapViewSnap.Ortho(10, 20, 0.01, 0, 10, 20, 960, 540, 1920, 1080);
            if (Math.Abs(s0.MapRefX) > 1e-6 || Math.Abs(s0.MapRefY) > 1e-6) return "mapRef not zero for centre = projection centre";
            if (MapViewSnap.Ortho(0, 0, 0, 0, 0, 0, 0, 0, 100, 100).Valid || MapViewSnap.Ortho(0, 0, 1, 0, 0, 0, 0, 0, 1, 1).Valid) return "Valid accepts junk";
            if (MapViewSnap.Ortho(0, 0, 1, double.NaN, 0, 0, 0, 0, 100, 100).Valid) return "Valid accepts NaN";
            string r = T.Lines(bad);
            Console.WriteLine("  projector points " + n + " offdisc " + offDisc + " max |ours-ecs| px " + worstEcs.ToString("E2") + ", |ours-float| px " + worstFloat.ToString("F3")
                + " (ratio to tolerance " + worstFloatRel.ToString("F3") + "), inverse deg " + worstInv.ToString("E2"));
            return r;
        }
    }

    internal static class SphereCases
    {
        private static D3 SlerpRef(D3 a, D3 b, double t, double th)
        {
            double sn = Math.Sin(th);
            double k0 = Math.Sin((1 - t) * th) / sn, k1 = Math.Sin(t * th) / sn;
            return new D3(k0 * a.X + k1 * b.X, k0 * a.Y + k1 * b.Y, k0 * a.Z + k1 * b.Z);
        }

        private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2)) : 0;
            double cx = ax + t * dx, cy = ay + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        public static string Run()
        {
            // Slerp / Angle / OffsetOnSphere
            V3 a = MapProjector.GeoToUnit(10, 20), b = MapProjector.GeoToUnit(-30, 100);
            double th = MapProjector.Angle(a, b);
            D3 ra = GameRef.GeoToUnit(10, 20), rb = GameRef.GeoToUnit(-30, 100);
            for (double t = 0; t <= 1.0001; t += 0.125)
            {
                V3 m = MapProjector.Slerp(a, b, t, th); D3 r = SlerpRef(ra, rb, t, th);
                if (Math.Abs(m.X - r.X) + Math.Abs(m.Y - r.Y) + Math.Abs(m.Z - r.Z) > 1e-12) return "slerp t=" + t;
            }
            double lat, lon;
            for (int i = 0; i < 12; i++)
            {
                double la0 = -80 + i * 14, lo0 = -170 + i * 29, brg = i * 31;
                V3 u = MapProjector.OffsetOnSphere(la0, lo0, brg, 60 * 1852.0 / GameRef.R);
                MapProjector.UnitToGeo(u, out lat, out lon);
                double dist = GameRef.SphericalDistanceM(la0, lo0, lat, lon);
                if (Math.Abs(dist - 60 * 1852.0) > 1e-3) return "offset distance " + dist;
            }
            V3 north = MapProjector.OffsetOnSphere(50, 10, 0, 60 * 1852.0 / GameRef.R);
            MapProjector.UnitToGeo(north, out lat, out lon);
            if (Math.Abs(lat - (50 + 60 * 1852.0 / GameRef.R * 180 / Math.PI)) > 1e-9 || Math.Abs(lon - 10) > 1e-9) return "offset north " + lat + "," + lon;
            V3 east = MapProjector.OffsetOnSphere(0, 10, 90, 0.01);
            MapProjector.UnitToGeo(east, out lat, out lon);
            if (Math.Abs(lat) > 1e-9 || Math.Abs(lon - (10 + 0.01 * 180 / Math.PI)) > 1e-9) return "offset east " + lat + "," + lon;
            // HorizonCrossing
            MapViewSnap s0 = MapViewSnap.Ortho(0, 0, 1e-4, 0, 0, 0, 960, 540, 1920, 1080);
            V3 fa = MapProjector.GeoToFrame(in s0, 0, 60), fb = MapProjector.GeoToFrame(in s0, 20, 130);
            V3 c;
            if (!(fa.Z > 0) || !(fb.Z < 0) || !MapProjector.HorizonCrossing(fa, fb, out c)) return "horizon setup";
            if (Math.Abs(c.Z) > 1e-12 || Math.Abs(c.X * c.X + c.Y * c.Y - 1) > 1e-12) return "horizon crossing not on the limb " + c.Z;
            V3 cr = new V3(fa.Y * fb.Z - fa.Z * fb.Y, fa.Z * fb.X - fa.X * fb.Z, fa.X * fb.Y - fa.Y * fb.X);
            if (Math.Abs(c.X * cr.X + c.Y * cr.Y + c.Z * cr.Z) > 1e-9) return "crossing not on the arc plane";
            V3 c2; if (!MapProjector.HorizonCrossing(fb, fa, out c2) || Math.Abs(c2.X - c.X) > 1e-12) return "crossing order";
            if (MapProjector.HorizonCrossing(new V3(0, 0, 1), new V3(0, 0, 1), out c2)) return "crossing of equal points";
            V3 lb = MapProjector.ClampToLimb(new V3(3, 4, -0.5));
            if (Math.Abs(lb.X - 0.6) > 1e-12 || Math.Abs(lb.Y - 0.8) > 1e-12 || lb.Z != 0) return "clamp";
            // StepsFor: the middle of every piece is within 0.5 px of the chord; at most 256
            double[] scales = { T.FullDiscScale(1080), GameRef.ZoomLevelToScale(8), GameRef.ZoomLevelToScale(12), GameRef.ZoomLevelToScale(14) };
            double[] thetas = { 0.001, 0.05, 0.2, 0.5 };
            double worstSag = 0; int maxN = 0;
            foreach (double sc in scales)
            {
                MapViewSnap s = MapViewSnap.Ortho(0, 0, sc, 0, 0, 0, 960, 540, 1920, 1080);
                foreach (double tt in thetas)
                {
                    V3 p0 = MapProjector.GeoToUnit(40, 0), p1 = MapProjector.OffsetOnSphere(40, 0, 100, tt);
                    int n = MapProjector.StepsFor(tt, sc);
                    maxN = Math.Max(maxN, n);
                    if (n < 1 || n > 256) return "steps range " + n;
                    if (n == 256) continue;   // capped: the tolerance is not promised
                    for (int i = 0; i < n; i++)
                    {
                        V3 q0 = MapProjector.Slerp(p0, p1, (double)i / n, tt), q1 = MapProjector.Slerp(p0, p1, (i + 1.0) / n, tt), qm = MapProjector.Slerp(p0, p1, (i + 0.5) / n, tt);
                        double x0, y0, x1, y1, xm, ym;
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, q0), out x0, out y0);
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, q1), out x1, out y1);
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, qm), out xm, out ym);
                        double sag = SegDist(xm, ym, x0, y0, x1, y1);
                        worstSag = Math.Max(worstSag, sag);
                        if (sag > 0.5 + 1e-9) return "great-circle sag " + sag + " scale " + sc + " theta " + tt + " n " + n;
                    }
                }
            }
            if (MapProjector.StepsFor(0, 1) != 1 || MapProjector.StepsFor(1.0, 1e-9) != 1 || MapProjector.StepsFor(double.NaN, 1) != 1 || MapProjector.StepsFor(3.0, 100) != 256) return "StepsFor edge";
            // StepsForArc: azimuth sweep of a circle, the middle of each piece is within 0.5 px
            double worstArc = 0;
            foreach (double sc in scales)
            {
                MapViewSnap s = MapViewSnap.Ortho(0, 0, sc, 0, 0, 0, 960, 540, 1920, 1080);
                foreach (double nm in new[] { 5.0, 60.0, 600.0 })
                {
                    double ang = nm * 1852.0 / GameRef.R;
                    int n = MapProjector.StepsForArc(2 * Math.PI, ang, sc);
                    if (n < 1 || n > 256) return "arc steps range";
                    if (n == 256) continue;
                    for (int i = 0; i < n; i++)
                    {
                        double x0, y0, x1, y1, xm, ym;
                        V3 q0 = MapProjector.OffsetOnSphere(0, 0, 360.0 * i / n, ang), q1 = MapProjector.OffsetOnSphere(0, 0, 360.0 * (i + 1) / n, ang), qm = MapProjector.OffsetOnSphere(0, 0, 360.0 * (i + 0.5) / n, ang);
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, q0), out x0, out y0);
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, q1), out x1, out y1);
                        MapProjector.FrameToView(in s, MapProjector.UnitToFrame(in s, qm), out xm, out ym);
                        double sag = SegDist(xm, ym, x0, y0, x1, y1);
                        worstArc = Math.Max(worstArc, sag);
                        if (sag > 0.5 + 1e-9) return "arc sag " + sag + " nm " + nm + " scale " + sc + " n " + n;
                    }
                }
            }
            Console.WriteLine("  sphere: worst great-circle sag " + worstSag.ToString("F3") + " px, worst arc sag " + worstArc.ToString("F3") + " px, max steps " + maxN);
            return null;
        }
    }
}
