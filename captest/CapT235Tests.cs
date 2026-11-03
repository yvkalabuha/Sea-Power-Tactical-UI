using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-235 (ADR-0097): the CAP patrol area is covered by the weapon range. Scenarios of `captest t235 <name>`: every point of the zone is within Rc of the route,
    // the legs are evenly spaced, no extra legs; the route given to the game is closed.
    internal static class CapT235Tests
    {
        private static readonly string[] Names = { "live_30x30", "shapes", "minimal", "serpentine", "orbit", "cover_nm" };

        private static CapZone Zone(double latNm, double lonNm)
        {
            return new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + latNm / 60.0, 20.0 + lonNm / 60.0 } };
        }

        /// <summary>Distance (nm, flat 1/60 deg) from a point to the closed route of a lane; an orbit is a circle of OrbitBelowNm / 2 around its centre.</summary>
        internal static double DistToRoute(GeoPt g, CapLane lane, CapParams p)
        {
            double y = g.Lat * 60, x = g.Lon * 60;
            if (lane.Orbit)
            {
                double dy = y - lane.Center.Lat * 60, dx = x - lane.Center.Lon * 60;
                return Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - p.OrbitBelowNm / 2);
            }
            var r = CapMath.PatrolRoute(lane);
            double best = double.MaxValue;
            for (int i = 0; i + 1 < r.Count; i++)
                best = Math.Min(best, Seg(x, y, r[i].Lon * 60, r[i].Lat * 60, r[i + 1].Lon * 60, r[i + 1].Lat * 60));
            return best;
        }

        private static double Seg(double px, double py, double ax, double ay, double bx, double by)
        {
            double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * vx + (py - ay) * vy) / l2));
            double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Grid points of the zone (step nm, edges included) farther than rc + 1e-6 from every lane.</summary>
        internal static int Holes(CapZone z, IList<CapLane> lanes, CapParams p, double rc, double step = 0.25)
        {
            double h = (z.Rect[2] - z.Rect[0]) * 60, w = (z.Rect[3] - z.Rect[1]) * 60;
            int holes = 0;
            for (int iy = 0; iy <= (int)Math.Round(h / step); iy++)
                for (int ix = 0; ix <= (int)Math.Round(w / step); ix++)
                {
                    var g = new GeoPt(z.Rect[0] + Math.Min(h, iy * step) / 60, z.Rect[1] + Math.Min(w, ix * step) / 60);
                    double best = double.MaxValue;
                    foreach (var l in lanes) best = Math.Min(best, DistToRoute(g, l, p));
                    if (best > rc + 1e-6) holes++;
                }
            return holes;
        }

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
            public void Near(string n, double a, double b, double eps = 1e-6) { Is(n + " (" + a.ToString("0.####") + " vs " + b.ToString("0.####") + ")", Math.Abs(a - b) <= eps); }
        }

        /// <summary>Cross-distances (nm from the min edge of the zone) of the legs of every lane.</summary>
        private static List<double> LegPositions(CapZone z, IList<CapLane> lanes)
        {
            bool ns = (z.Rect[2] - z.Rect[0]) >= (z.Rect[3] - z.Rect[1]);
            var res = new List<double>();
            foreach (var l in lanes)
                for (int k = 0; k < l.Track.Count; k += 2)
                    res.Add(ns ? (l.Track[k].Lon - z.Rect[1]) * 60 : (l.Track[k].Lat - z.Rect[0]) * 60);
            return res;
        }

        public static string Run(string only = null)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = One(n);
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static string One(string name)
        {
            var c = new Chk(); var p = new CapParams();
            switch (name)
            {
                case "live_30x30":
                {
                    var z = Zone(30, 30);
                    var ls = CapMath.Lanes(z, 2, p, 12.5);
                    c.Is("two lanes", ls.Count == 2);
                    var pos = LegPositions(z, ls);
                    double[] want = { 3.75, 11.25, 18.75, 26.25 };
                    c.Is("four legs", pos.Count == 4);
                    for (int i = 0; i < want.Length && i < pos.Count; i++) c.Near("leg " + i, pos[i], want[i]);
                    c.Is("lane fields", ls[0].Legs == 2 && Math.Abs(ls[0].LegSpacingNm - 7.5) < 1e-9 && Math.Abs(ls[0].CoverNm - 12.5) < 1e-9);
                    c.Is("holes rc 12.5", Holes(z, ls, p, 12.5) == 0);
                    c.Is("holes rc 5", Holes(z, CapMath.Lanes(z, 2, p, 5), p, 5) == 0);
                    break;
                }
                case "shapes":
                {
                    double[][] zones = { new[] { 30.0, 30 }, new[] { 40.0, 20 }, new[] { 20.0, 40 }, new[] { 100.0, 30 }, new[] { 20.5, 6.1 }, new[] { 10.0, 10 }, new[] { 8.0, 60 }, new[] { 200.0, 200 } };
                    foreach (var zz in zones)
                    {
                        var z = Zone(zz[0], zz[1]);
                        bool ns = zz[0] >= zz[1];
                        double wid = ns ? zz[1] : zz[0];
                        foreach (int n in new[] { 1, 2, 3, 4 })
                            foreach (double rc in new[] { 2.0, 5.0, 12.5, 50.0 })
                            {
                                string tag = zz[0] + "x" + zz[1] + " n=" + n + " rc=" + rc;
                                if (zz[0] >= 200 && rc <= 2) continue;   // 200 x 200 at Rc 2: thousands of legs, the grid test is only for speed
                                var ls = CapMath.Lanes(z, n, p, rc);
                                c.Is(tag + " count", ls.Count == n);
                                // a thin zone (side < OrbitBelowNm) may be an orbit that covers it; the grid check holds for those too
                                c.Is(tag + " holes", Holes(z, ls, p, rc, zz[0] * zz[1] > 12000 ? 1.0 : 0.25) == 0);
                                double strip = wid / n;
                                double dAll = -1;
                                foreach (var l in ls)
                                {
                                    if (l.Orbit || l.Circle) continue;   // T-244: a circle is centred in the zone and may stick out of a small one
                                    foreach (var pt in l.Track) c.Is(tag + " inside", CapMath.DistToZoneNm(pt, z) < 1e-9);
                                    var own = LegPositions(z, new[] { l });
                                    foreach (var x in own) c.Is(tag + " own strip", x > l.Slot * strip - 1e-9 && x < (l.Slot + 1) * strip + 1e-9);
                                    if (dAll < 0) dAll = l.LegSpacingNm;
                                    c.Near(tag + " spacing", l.LegSpacingNm, dAll);
                                    c.Near(tag + " edge", own[0] - l.Slot * strip, l.LegSpacingNm / 2);
                                    c.Is(tag + " d/2 <= rc", l.LegSpacingNm / 2 <= rc + 1e-9);
                                }
                                if (c.Err != null) return c.Err;
                            }
                    }
                    break;
                }
                case "minimal":
                {
                    var z = Zone(30, 30);
                    foreach (double rc in new[] { 1.2, 1.5, 2.0, 3.0 })
                    {
                        var ls = CapMath.Lanes(z, 2, p, rc);
                        int q = ls[0].Legs / 2;
                        c.Is("rc " + rc + " q >= 2", q >= 2);
                        c.Is("rc " + rc + " covers", Holes(z, ls, p, rc) == 0);
                        // the radius at which q - 1 pairs would be the answer is a bit larger; with q - 1 pairs at the radius rc just below it there are holes
                        double rcBorder = 15.0 / (4 * (q - 1));
                        double rcTest = rcBorder - 0.1;
                        var needs = CapMath.Lanes(z, 2, p, rcTest);
                        c.Is("rc " + rc + " same q at the border - 0.1", needs[0].Legs / 2 == q);
                        var fewer = CapMath.Lanes(z, 2, p, rcBorder + 1e-3);
                        c.Is("rc " + rc + " fewer legs when allowed", fewer[0].Legs == 2 * (q - 1));
                        c.Is("rc " + rc + " q-1 pairs leave holes at the lower radius", Holes(z, fewer, p, rcTest) > 0);
                    }
                    c.Is("rc 50 strip 15: two legs", CapMath.Lanes(z, 2, p, 50)[0].Legs == 2);
                    break;
                }
                case "serpentine":
                {
                    var z = Zone(40, 30);
                    var ls = CapMath.Lanes(z, 1, p, 3);
                    var l = ls[0];
                    int m = l.Legs;
                    c.Is("q >= 2", m >= 4 && m % 2 == 0);
                    c.Is("track 2m points", l.Track.Count == 2 * m);
                    for (int j = 0; j < m; j++)
                    {
                        double s = l.Track[2 * j].Lat, e = l.Track[2 * j + 1].Lat;
                        c.Is("leg direction " + j, j % 2 == 0 ? e > s : e < s);
                        c.Is("leg straight " + j, Math.Abs(l.Track[2 * j].Lon - l.Track[2 * j + 1].Lon) < 1e-12);
                    }
                    foreach (var pt in l.Track) c.Is("inside", CapMath.DistToZoneNm(pt, z) < 1e-9);
                    var r = CapMath.PatrolRoute(l);
                    c.Is("closed", r.Count == 2 * m + 1 && r[r.Count - 1].Lat == r[0].Lat && r[r.Count - 1].Lon == r[0].Lon);
                    var orb = CapMath.Lanes(Zone(30, 3.5), 1, p, 50)[0];
                    c.Is("circle route closed (T-244)", orb.Circle && CapMath.PatrolRoute(orb).Count == 2 * orb.Track.Count + 1);
                    break;
                }
                case "orbit":
                {
                    var o = CapMath.Lanes(Zone(30, 3.5), 1, p, 50);
                    c.Is("30 x 3.5 Rc 50: circle (T-244)", o[0].Circle && o[0].Track.Count >= 4);
                    c.Is("orbit holes", Holes(Zone(30, 3.5), o, p, 50) == 0);
                    var t = CapMath.Lanes(Zone(60, 3), 1, p, 5);
                    c.Is("60 x 3 Rc 5: narrow racetrack", !t[0].Circle && t[0].Track.Count >= 4);
                    c.Is("narrow holes", Holes(Zone(60, 3), t, p, 5) == 0);
                    break;
                }
                case "cover_nm":
                {
                    c.Near("min of known", CapMath.CoverNm(new[] { 25.0, 10.0, 0.0 }, p), 5);
                    c.Near("none -> fallback", CapMath.CoverNm(new double[0], p), 10);
                    c.Near("only zeros -> fallback", CapMath.CoverNm(new[] { 0.0 }, p), 10);
                    c.Near("floor 1", CapMath.CoverNm(new[] { 1.0 }, p), 1);
                    var p2 = new CapParams { CoverAamFrac = 0.2, CoverFallbackNm = 7 };
                    c.Near("frac", CapMath.CoverNm(new[] { 25.0 }, p2), 5);
                    c.Near("fallback key", CapMath.CoverNm(null, p2), 7);
                    break;
                }
            }
            return c.Err;
        }
    }
}
