using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-362 (ADR-0135 R5): CAP zones, strips, circles and the intercept point with the K of the row/segment. `captest t362 [ew|ns|cover|dist|circle|icpt|polar|flat]`.
    internal static class CapT362Tests
    {
        private const double Deg = Math.PI / 180.0;
        private static readonly double[] Lats = { 0, 45, 60, 70 };

        public static string Run(string only)
        {
            string e;
            if ((only == null || only == "ew") && (e = Ew()) != null) return "ew: " + e;
            if ((only == null || only == "ns") && (e = Ns()) != null) return "ns: " + e;
            if ((only == null || only == "cover") && (e = Cover()) != null) return "cover: " + e;
            if ((only == null || only == "dist") && (e = Dist()) != null) return "dist: " + e;
            if ((only == null || only == "circle") && (e = Circle()) != null) return "circle: " + e;
            if ((only == null || only == "icpt") && (e = Icpt()) != null) return "icpt: " + e;
            if ((only == null || only == "polar") && (e = Polar()) != null) return "polar: " + e;
            if ((only == null || only == "flat") && (e = Flat()) != null) return "flat: " + e;
            return null;
        }

        private static bool Same(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }

        private static double[] Rect(double lat, double ewNm, double nsNm)
        {
            return CapCommands.PolarRect(new GeoPt(lat, 10), new CapOrderCmd { Polar = true, Bearing = 0, DistNm = 0, SizeNm = ewNm, WNm = nsNm });
        }

        // the inset of a strip layout, as Lanes computes it (zone rect, n strips, coverage radius), for the east-west layout
        private static double ExpectedInset(double[] r, int n, CapParams p, double rc, out double d)
        {
            double hNm = (r[2] - r[0]) * 60.0, wNm = GeoLon.DLon(r[1], r[3]) * 60.0 * AswMath.LonK(r[0], r[2]);
            double strip = hNm / n;
            int q = Math.Max(1, (int)Math.Ceiling(strip / (4 * rc) - 1e-9)), m = 2 * q;
            d = strip / m;
            double reach = Math.Sqrt(Math.Max(0, rc * rc - (d / 2) * (d / 2)));
            return Math.Min(Math.Min(p.LaneEndMaxNm, 0.1 * wNm), reach);
        }

        private static string Ew()
        {
            var p = new CapParams();
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var r = Rect(lat, 80, 30);
                    var z = new CapZone { Rect = r };
                    var lanes = CapMath.Lanes(z, 2, p, 8);
                    double d; double inset = ExpectedInset(r, 2, p, 8, out d);
                    if (lanes.Count != 2) return "lanes " + lat;
                    foreach (var l in lanes)
                    {
                        if (l.Circle || l.Track == null || l.Track.Count < 2) return "unexpected circle/orbit " + lat;
                        for (int j = 0; j + 1 < l.Track.Count; j += 2)
                        {
                            var s = l.Track[j]; var e = l.Track[j + 1];
                            if (!Same(s.Lat, e.Lat)) return "leg not on a parallel " + lat;
                            var west = new GeoPt(s.Lat, r[1]); var east = new GeoPt(s.Lat, r[3]);
                            var a = s.Lon < e.Lon ? s : e; var b = s.Lon < e.Lon ? e : s;
                            double gw = AswMath.GreatCircleNm(west, a), ge = AswMath.GreatCircleNm(b, east);
                            if (Math.Abs(gw - inset) > Math.Max(0.02, 0.005 * inset)) return "west inset " + gw + " vs " + inset + " @" + lat;
                            if (Math.Abs(ge - inset) > Math.Max(0.02, 0.005 * inset)) return "east inset " + ge + " vs " + inset + " @" + lat;
                        }
                    }
                }
            return null;
        }

        private static string Ns()
        {
            var p = new CapParams();
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var r = Rect(lat, 30, 80);
                    var lanes = CapMath.Lanes(new CapZone { Rect = r }, 2, p, 8);
                    if (lanes.Count != 2 || lanes[0].Circle) return "lanes " + lat;
                    double wide = CapMath.WideLat(r);
                    // collect leg longitudes: every leg stays on a meridian
                    var lons = new List<double>();
                    foreach (var l in lanes)
                        for (int j = 0; j + 1 < l.Track.Count; j += 2)
                        {
                            if (!Same(l.Track[j].Lon, l.Track[j + 1].Lon)) return "leg off the meridian " + lat;
                            lons.Add(l.Track[j].Lon);
                        }
                    lons.Sort();
                    double d = lanes[0].LegSpacingNm;
                    for (int i = 1; i < lons.Count; i++)
                    {
                        double widest = AswMath.GreatCircleNm(new GeoPt(wide, lons[i - 1]), new GeoPt(wide, lons[i]));
                        if (Math.Abs(widest / d - 1) > 0.005) return "step on the widest row " + widest + " vs " + d + " @" + lat;
                        foreach (double rl in new[] { r[0], (r[0] + r[2]) / 2, r[2] })
                        {
                            double s = AswMath.GreatCircleNm(new GeoPt(rl, lons[i - 1]), new GeoPt(rl, lons[i]));
                            if (s > d * 1.005) return "step " + s + " > " + d + " on row " + rl + " @" + lat;
                        }
                    }
                }
            return null;
        }

        private static string Cover()
        {
            var p = new CapParams();
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                    foreach (var sz in new[] { new[] { 60.0, 60.0 }, new[] { 80.0, 30.0 }, new[] { 30.0, 80.0 } })
                    {
                        var r = Rect(lat, sz[0], sz[1]);
                        const double rc = 8;
                        var lanes = CapMath.Lanes(new CapZone { Rect = r }, 2, p, rc);
                        var pts = new List<GeoPt>();
                        foreach (var l in lanes)
                        {
                            if (l.Circle) return "circle lane " + lat;
                            var route = CapMath.PatrolRoute(l, p);
                            for (int i = 1; i < route.Count; i++)
                            {
                                double len = AswMath.GreatCircleNm(route[i - 1], route[i]);
                                int steps = Math.Max(1, (int)Math.Ceiling(len / 0.25));
                                for (int s = 0; s <= steps; s++)
                                    pts.Add(new GeoPt(route[i - 1].Lat + (route[i].Lat - route[i - 1].Lat) * s / steps, route[i - 1].Lon + (route[i].Lon - route[i - 1].Lon) * s / steps));
                            }
                        }
                        for (int i = 0; i <= 20; i++)
                            for (int j = 0; j <= 20; j++)
                            {
                                var g = new GeoPt(r[0] + (r[2] - r[0]) * i / 20, r[1] + (r[3] - r[1]) * j / 20);
                                double best = double.MaxValue;
                                foreach (var q in pts) best = Math.Min(best, AswMath.GreatCircleNm(g, q));
                                if (best > rc * 1.005) return "uncovered " + best + " @" + lat + " " + sz[0] + "x" + sz[1] + " i=" + i + " j=" + j;
                            }
                    }
            return null;
        }

        private static double EdgeNm(GeoPt g, double[] r)
        {
            double best = double.MaxValue;
            int n = 4000;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double lat = r[0] + (r[2] - r[0]) * t, lon = r[1] + (r[3] - r[1]) * t;
                best = Math.Min(best, AswMath.GreatCircleNm(g, new GeoPt(lat, r[1])));
                best = Math.Min(best, AswMath.GreatCircleNm(g, new GeoPt(lat, r[3])));
                best = Math.Min(best, AswMath.GreatCircleNm(g, new GeoPt(r[0], lon)));
                best = Math.Min(best, AswMath.GreatCircleNm(g, new GeoPt(r[2], lon)));
            }
            return best;
        }

        private static string Dist()
        {
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var r = Rect(lat, 60, 60);
                    var z = new CapZone { Rect = r };
                    foreach (double off in new[] { 5.0, 20.0 })
                        foreach (double rl in new[] { r[0], r[2] })
                        {
                            double dLon = off / (60.0 * Math.Cos(rl * Deg));
                            var g = new GeoPt(rl, r[3] + dLon);
                            double got = CapMath.DistToZoneNm(g, z), want = EdgeNm(g, r);
                            if (Math.Abs(got / want - 1) > 0.005) return "east " + got + " vs " + want + " @" + lat + " off=" + off + " row=" + rl;
                        }
                    var diag = new GeoPt(r[2] + 15 / 60.0, r[3] + 15 / (60.0 * Math.Cos((r[2] + 0.25) * Deg)));
                    double gd = CapMath.DistToZoneNm(diag, z), wd = EdgeNm(diag, r);
                    if (Math.Abs(gd / wd - 1) > 0.005) return "diag " + gd + " vs " + wd + " @" + lat;
                }
            return null;
        }

        private static string Circle()
        {
            var p = new CapParams();
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var c = new GeoPt(lat, 10);
                    foreach (double rad in new[] { 6.0, 15.0 })
                    {
                        var lane = new CapLane { Center = c, Circle = true, StartDeg = 0, RadiusNm = rad, Track = new List<GeoPt>(new GeoPt[8]) };
                        foreach (var g in CapMath.CircleRouteFrom(lane, 0, rad, 1, p))
                            if (Math.Abs(AswMath.GreatCircleNm(c, g) / rad - 1) > 0.005) return "route radius " + rad + " @" + lat + " " + AswMath.GreatCircleNm(c, g);
                    }
                    var big = new CapLane { Center = c, Circle = true, RadiusNm = 15 };
                    foreach (double az in new[] { 45.0, 135.0 })
                    {
                        if (!CapMath.OnCircle(AswMath.ToGeo(c, az, new NmPt(15 + p.CircleTolNm - 0.05, 0)), big, p)) return "OnCircle inside tol " + az + " @" + lat;
                        if (CapMath.OnCircle(AswMath.ToGeo(c, az, new NmPt(15 + p.CircleTolNm + 0.05, 0)), big, p)) return "OnCircle outside tol " + az + " @" + lat;
                    }
                    // a small zone: the shared circle
                    var zs = Rect(lat, 6, 6);
                    var cl = CapMath.Lanes(new CapZone { Rect = zs }, 1, p, 8);
                    if (cl.Count == 1 && cl[0].Circle)
                        foreach (var g in cl[0].Track)
                            if (Math.Abs(AswMath.GreatCircleNm(cl[0].Center, g) / cl[0].RadiusNm - 1) > 0.005) return "lane circle radius @" + lat;
                }
            return null;
        }

        private static string Icpt()
        {
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var bird = new GeoPt(lat, 10);
                    foreach (double brg in new[] { 30.0, 90.0, 120.0, 270.0 })
                        foreach (double crs in new[] { 0.0, 90.0, 180.0 })
                        {
                            var tq = AswMath.ToGeo(bird, brg, new NmPt(60, 0));
                            var tc = new CapContact { Id = 1, Lat = tq.Lat, Lon = tq.Lon, Hostile = true, CourseDeg = crs, SpeedKn = 400 };
                            GeoPt iq; bool ld;
                            double et = CapInterceptMath.EtaS(bird, 600, tc, out iq, out ld);
                            if (ld && Math.Abs(AswMath.GreatCircleNm(bird, iq) / (600.0 * et / 3600.0) - 1) > 0.005) return "ip distance brg=" + brg + " crs=" + crs + " @" + lat;
                        }
                    var tp = AswMath.ToGeo(bird, 30, new NmPt(60, 0));
                    var t = new CapContact { Id = 1, Lat = tp.Lat, Lon = tp.Lon, Hostile = true, CourseDeg = 0, SpeedKn = 400 };
                    GeoPt ip; bool lead;
                    double eta = CapInterceptMath.EtaS(bird, 600, t, out ip, out lead);
                    if (!lead) return "no lead @" + lat;
                    double want = 600.0 * eta / 3600.0, got = AswMath.GreatCircleNm(bird, ip);
                    if (Math.Abs(got / want - 1) > 0.005) return "ip " + got + " vs " + want + " @" + lat;
                }
            return null;
        }

        private static string Polar()
        {
            using (AswMath.WorldScope(true))
                foreach (double lat in Lats)
                {
                    var r = Rect(lat, 60, 60);
                    double w = AswMath.GreatCircleNm(new GeoPt(lat, r[1]), new GeoPt(lat, r[3]));
                    if (Math.Abs(w / 60 - 1) > 0.005) return "width " + w + " @" + lat;
                }
            return null;
        }

        // ---- the old formulas, verbatim ----
        private static GeoPt OldPt(double[] R, bool ns, double a, double c)
        {
            double k = 60.0 * AswMath.LonK(R[0], R[2]);
            return ns ? new GeoPt(R[0] + a / 60.0, R[1] + c / k) : new GeoPt(R[0] + c / 60.0, R[1] + a / k);
        }

        private static List<GeoPt> OldLanesTrack(double[] R, int n, CapParams p, double coverNm, int i, out bool circle)
        {
            double hNm = (R[2] - R[0]) * 60.0, wNm = GeoLon.DLon(R[1], R[3]) * 60.0 * AswMath.LonK(R[0], R[2]);
            bool ns = hNm >= wNm;
            double len = ns ? hNm : wNm, wid = ns ? wNm : hNm;
            double rc = Math.Max(1, coverNm), strip = wid / n;
            int q = Math.Max(1, (int)Math.Ceiling(strip / (4 * rc) - 1e-9)), m = 2 * q;
            double d = strip / m, reach = Math.Sqrt(Math.Max(0, rc * rc - (d / 2) * (d / 2)));
            double inset = Math.Min(Math.Min(p.LaneEndMaxNm, 0.1 * len), reach);
            double circR = Math.Max(1, CapMath.TurnRadiusNm(p.CircleSpeedKn, p.CircleBankDeg));
            double diag = Math.Sqrt((len / 2) * (len / 2) + (wid / 2) * (wid / 2));
            bool small = (wid < Math.Max(p.OrbitBelowNm, 2 * circR) && (circR <= rc && diag - circR <= rc)) || len - 2 * inset < 1;
            circle = small;
            if (small) return null;
            double a0 = inset, a1 = len - inset;
            var t = new List<GeoPt>();
            for (int j = 0; j < m; j++)
            {
                double cj = i * strip + (j + 0.5) * d;
                double s = j % 2 == 0 ? a0 : a1, e = j % 2 == 0 ? a1 : a0;
                t.Add(OldPt(R, ns, s, cj)); t.Add(OldPt(R, ns, e, cj));
            }
            return t;
        }

        private static string Flat()
        {
            var rnd = new Random(3623);
            var p = new CapParams();
            using (AswMath.WorldScope(false))
                for (int it = 0; it < 500; it++)
                {
                    double lat0 = rnd.NextDouble() * 140 - 70, lon0 = rnd.NextDouble() * 300 - 150;
                    double h = 0.1 + rnd.NextDouble() * 2.5, w = 0.1 + rnd.NextDouble() * 2.5;
                    var R = new[] { lat0, lon0, lat0 + h, lon0 + w };
                    int n = 1 + rnd.Next(3);
                    double rc = 3 + rnd.NextDouble() * 12;
                    var lanes = CapMath.Lanes(new CapZone { Rect = R }, n, p, rc);
                    for (int i = 0; i < lanes.Count; i++)
                    {
                        bool circle;
                        var old = OldLanesTrack(R, n, p, rc, i, out circle);
                        if (circle != lanes[i].Circle) return "circle flag " + it;
                        if (old == null) continue;
                        if (old.Count != lanes[i].Track.Count) return "track count " + it;
                        for (int j = 0; j < old.Count; j++)
                            if (!Same(old[j].Lat, lanes[i].Track[j].Lat) || !Same(old[j].Lon, lanes[i].Track[j].Lon)) return "lane bits " + it + "/" + i + "/" + j;
                    }
                    // DistToZoneNm
                    var g = new GeoPt(lat0 + (rnd.NextDouble() * 2 - 0.5) * h * 2, lon0 + (rnd.NextDouble() * 2 - 0.5) * w * 2);
                    double nn = Math.Max(Math.Max(R[0] - g.Lat, 0), g.Lat - R[2]) * 60.0;
                    double ee = Math.Max(Math.Max(R[1] - g.Lon, 0), g.Lon - R[3]) * 60.0 * AswMath.LonK(R[0], R[2]);
                    if (!Same(CapMath.DistToZoneNm(g, new CapZone { Rect = R }), Math.Sqrt(nn * nn + ee * ee))) return "dist bits " + it;
                    // circle + OnCircle
                    var c = new GeoPt(lat0, lon0);
                    double rad = 1 + rnd.NextDouble() * 15, ph = rnd.NextDouble() * 360;
                    var lane = new CapLane { Center = c, Circle = true, StartDeg = ph, RadiusNm = rad, Track = new List<GeoPt>(new GeoPt[6]) };
                    var route = CapMath.CircleRouteFrom(lane, ph + 10, rad, 2, p);
                    int k = 6; double step = 360.0 / k, best = double.MaxValue; int bm = 0;
                    for (int m = 0; m < k; m++)
                    {
                        double dd = ((lane.StartDeg + m * step - (ph + 10)) % 360 + 360) % 360;
                        if (dd >= step / 2 - 1e-9 && dd < best) { best = dd; bm = m; }
                    }
                    for (int j = 0; j < 2 * k; j++)
                    {
                        double a = (lane.StartDeg + (bm + j) * step) * Math.PI / 180.0;
                        double ol = c.Lat + rad * Math.Cos(a) / 60.0, oo = c.Lon + rad * Math.Sin(a) / (60.0 * AswMath.LonK(c.Lat));
                        if (!Same(route[j].Lat, ol) || !Same(route[j].Lon, oo)) return "circle route bits " + it;
                    }
                    double dy = (g.Lat - c.Lat) * 60.0, dx = GeoLon.DLon(c.Lon, g.Lon) * 60.0 * AswMath.LonK(c.Lat);
                    if (CapMath.OnCircle(g, lane, p) != (Math.Sqrt(dx * dx + dy * dy) <= rad + p.CircleTolNm)) return "OnCircle " + it;
                    // intercept point
                    var bird = new GeoPt(lat0, lon0);
                    var t = new CapContact { Id = 1, Lat = lat0 + rnd.NextDouble() - 0.5, Lon = lon0 + rnd.NextDouble() - 0.5, Hostile = true, CourseDeg = rnd.NextDouble() * 360, SpeedKn = 200 + rnd.NextDouble() * 300 };
                    GeoPt ip; bool lead;
                    double eta = CapInterceptMath.EtaS(bird, 600, t, out ip, out lead);
                    if (lead)
                    {
                        double rad2 = t.CourseDeg * Math.PI / 180.0, vx = t.SpeedKn * Math.Sin(rad2), vy = t.SpeedKn * Math.Cos(rad2), ix, iy;
                        CapMath.LeadPoint(GeoLon.DLon(bird.Lon, t.Lon) * 60.0 * AswMath.LonK(bird.Lat, t.Lat), (t.Lat - bird.Lat) * 60.0, vx, vy, 600, out ix, out iy);
                        var oip = new GeoPt(bird.Lat + iy / 60.0, bird.Lon + ix / (60.0 * AswMath.LonK(bird.Lat, t.Lat)));
                        if (!Same(ip.Lat, oip.Lat) || !Same(ip.Lon, oip.Lon)) return "ip bits " + it;
                        if (!Same(eta, AswMath.DistNm(bird, oip) / 600 * 3600.0)) return "eta bits " + it;
                    }
                }
            return null;
        }
    }
}
