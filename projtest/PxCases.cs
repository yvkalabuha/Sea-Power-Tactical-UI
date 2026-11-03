// T-309 A4-A7: MapPx in Affine, Ortho and Reflect, the redraw key, a timing probe.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge.ProjTest
{
    internal sealed class RecSink : IGeoSink
    {
        public readonly List<float[]> Lines = new List<float[]>();
        public int Breaks;
        public void Line(float x0, float y0, float x1, float y1) { Lines.Add(new[] { x0, y0, x1, y1 }); }
        public void Break() { Breaks++; }
    }

    internal static class PxAffineCases
    {
        private static double[] RandAff(Random r)
        {
            return new[] { r.NextDouble() * 400 - 200, r.NextDouble() * 400 - 200, r.NextDouble() * 1000, r.NextDouble() * 400 - 200, r.NextDouble() * 400 - 200, r.NextDouble() * 1000 };
        }

        public static string Run()
        {
            var r = new Random(3);
            for (int it = 0; it < 50; it++)
            {
                double[] a = RandAff(r);
                MapPx px = MapPx.FromAffine(a, 1000, 800);
                if (px.Kind != MapProjKind.Affine || px.AffineGeo[0] != a[0]) return "kind";
                double lat = r.NextDouble() * 160 - 80, lon = r.NextDouble() * 360 - 180, x, y;
                if (!px.Pt(lat, lon, out x, out y) || (float)x != (float)(a[0] * lat + a[1] * lon + a[2]) || (float)y != (float)(a[3] * lat + a[4] * lon + a[5])) return "Pt";
                if (!px.PtClamped(lat, lon, out x, out y) || (float)x != (float)(a[0] * lat + a[1] * lon + a[2])) return "PtClamped";
                // PxPerNm: the old Ctx formula (float steps of 1' of latitude)
                float x0 = (float)(a[0] * lat + a[1] * lon + a[2]), y0 = (float)(a[3] * lat + a[4] * lon + a[5]);
                float x1 = (float)(a[0] * (lat + 1.0 / 60.0) + a[1] * lon + a[2]), y1 = (float)(a[3] * (lat + 1.0 / 60.0) + a[4] * lon + a[5]);
                double dx = (double)x1 - x0, dy = (double)y1 - y0;
                if (px.PxPerNm(lat, lon) != Math.Sqrt(dx * dx + dy * dy)) return "PxPerNm";
                // Line
                double lat1 = lat + 0.3, lon1 = lon - 0.4;
                var s = new RecSink(); px.Line(lat, lon, lat1, lon1, s);
                if (s.Lines.Count != 1 || s.Breaks != 0 || s.Lines[0][0] != x0 || s.Lines[0][1] != y0
                    || s.Lines[0][2] != (float)(a[0] * lat1 + a[1] * lon1 + a[2]) || s.Lines[0][3] != (float)(a[3] * lat1 + a[4] * lon1 + a[5])) return "Line";
                // Arc: the old ring loop with RangeRingMath.Offset (copy)
                double nm = 5 + r.NextDouble() * 100, b0 = r.NextDouble() * 360, w = 30 + r.NextDouble() * 330; int n = 24 + it % 40;
                var sa = new RecSink(); px.Arc(lat, lon, nm, b0, w, n, sa);
                if (sa.Lines.Count != n) return "Arc count " + sa.Lines.Count + " vs " + n;
                float px0 = 0, py0 = 0;
                for (int i = 0; i <= n; i++)
                {
                    double la, lo, bb = b0 + w * i / n, cl = Math.Cos(lat * Math.PI / 180.0);
                    if (!(cl >= 0.05)) cl = 0.05;
                    la = lat + nm / 60.0 * Math.Cos(bb * Math.PI / 180.0); lo = lon + nm / 60.0 * Math.Sin(bb * Math.PI / 180.0) / cl;
                    float fx = (float)(a[0] * la + a[1] * lo + a[2]), fy = (float)(a[3] * la + a[4] * lo + a[5]);
                    if (i > 0) { float[] l = sa.Lines[i - 1]; if (l[0] != px0 || l[1] != py0 || l[2] != fx || l[3] != fy) return "Arc line " + i; }
                    px0 = fx; py0 = fy;
                }
                // ArcPoints
                var xs = new float[257]; var ys = new float[257]; var vs = new bool[257];
                int m = px.ArcPoints(lat, lon, nm, b0, w, n, xs, ys, vs);
                if (m != n + 1 || xs[0] != sa.Lines[0][0] || xs[n] != sa.Lines[n - 1][2] || !vs[0] || !vs[n]) return "ArcPoints";
                // inverse and the rest
                if (!px.TryInverse(x, y, out double ilat, out double ilon) || Math.Abs(ilat - lat) > 1e-6 * (1 + Math.Abs(lat)) || Math.Abs(ilon - lon) > 1e-6 * (1 + Math.Abs(lon))) return "TryInverse " + ilat + "," + ilon;
                if (px.Culled(lat, lon, 50)) return "affine is never culled";
                px.PrepareGrid(10, 1, 5, -5, 1, 5);
                double gx, gy;
                if (!px.GridPt(2, 3, out gx, out gy)) return "GridPt visible";
                px.Pt(8, -2, out x, out y);
                if (Math.Abs(gx - x) > 1e-9 || Math.Abs(gy - y) > 1e-9) return "GridPt affine";
            }
            // Create with an Affine snapshot = the composition geo -> local -> target
            var g = new Affine2 { A = 100, B = 5, C = 20, D = -3, E = 90, F = 30 };
            MapViewSnap sn = MapViewSnap.Affine(g, 800, 600);
            MapPx c = MapPx.Create(in sn, new[] { 0.5, 0.0, 0.0, 0.0, 0.5, 0.0 }, 400, 300);
            double cx, cy; c.Pt(1.5, 2.5, out cx, out cy);
            double lx, ly; g.Apply(1.5, 2.5, out lx, out ly);
            if (c.Kind != MapProjKind.Affine || Math.Abs(cx - lx * 0.5) > 1e-9 || Math.Abs(cy - ly * 0.5) > 1e-9) return "Create(Affine) " + cx + "," + cy;
            if (c.VertexBudget != 60000) return "budget";
            return null;
        }
    }

    internal static class PxOrthoCases
    {
        private static GeoToLocalFn StubFor(MapViewSnap s)
        {
            return delegate (double lat, double lon, out double x, out double y)
            {
                return MapProjector.GeoToView(in s, lat, lon, out x, out y);
            };
        }

        private static double Dist(double x, double y, double cx, double cy) { return Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)); }

        public static string Run()
        {
            // whole disc, 1920 x 1080, view centre (960, 540), identity local -> target
            double sc = T.FullDiscScale(1080) * 0.9;
            double rLimb = MapProjector.R * sc;
            MapViewSnap s = MapViewSnap.Ortho(0, 0, sc, 0, 0, 0, 960, 540, 1920, 1080);
            double[] id = { 1, 0, 0, 0, 1, 0 };
            MapPx px = MapPx.Create(in s, id, 1920, 1080);
            if (px.Kind != MapProjKind.Ortho || px.VertexBudget != 60000) return "kind/budget";
            double x, y;
            if (!px.Pt(0, 0, out x, out y) || Dist(x, y, 960, 540) > 1e-6) return "Pt centre";
            if (px.Pt(0, 120, out x, out y)) return "Pt behind the horizon must be false";
            if (px.Pt(double.NaN, 0, out x, out y)) return "Pt NaN";
            // segment through the limb: one line up to the limb + Break
            var sink = new RecSink();
            px.Line(0, 30, 0, 150, sink);
            if (sink.Lines.Count < 1 || sink.Breaks != 1) return "limb line: lines " + sink.Lines.Count + " breaks " + sink.Breaks;
            float[] last = sink.Lines[sink.Lines.Count - 1];
            if (Math.Abs(Dist(last[2], last[3], 960, 540) - rLimb) > 1e-2) return "limb line does not end on the limb " + Dist(last[2], last[3], 960, 540) + " vs " + rLimb;
            // both ends behind: nothing
            sink = new RecSink(); px.Line(0, 120, 10, 150, sink);
            if (sink.Lines.Count != 0) return "hidden line drew";
            // hidden -> visible gives a line from the limb
            sink = new RecSink(); px.Line(0, 150, 0, 30, sink);
            if (sink.Lines.Count < 1 || Math.Abs(Dist(sink.Lines[0][0], sink.Lines[0][1], 960, 540) - rLimb) > 1e-2) return "line from behind";
            // fill: a hidden vertex is pressed to the limb
            if (px.PtClamped(0, 120, out x, out y) || Math.Abs(Dist(x, y, 960, 540) - rLimb) > 1e-6) return "PtClamped limb " + Dist(x, y, 960, 540);
            if (!px.PtClamped(0, 10, out x, out y)) return "PtClamped visible";
            // inverse and off-disc
            double lat, lon;
            px.Pt(20, 30, out x, out y);
            if (!px.TryInverse(x, y, out lat, out lon) || Math.Abs(lat - 20) > 1e-6 || Math.Abs(lon - 30) > 1e-6) return "TryInverse " + lat + "," + lon;
            if (px.TryInverse(5, 5, out lat, out lon)) return "off-disc click must fail";
            // a target at half size (RT = local / 2)
            MapPx half = MapPx.Create(in s, new[] { 0.5, 0, 0, 0, 0.5, 0 }, 960, 540);
            double hx, hy; half.Pt(20, 30, out hx, out hy);
            if (Math.Abs(hx * 2 - x) > 1e-9 || Math.Abs(hy * 2 - y) > 1e-9) return "half target";
            if (!half.TryInverse(hx, hy, out lat, out lon) || Math.Abs(lat - 20) > 1e-6) return "half inverse";
            // PxPerNm: on the centre S * 1852; falls off to the limb
            if (Math.Abs(px.PxPerNm(0, 0) - sc * 1852.0) > 1e-9 || !(px.PxPerNm(0, 85) < px.PxPerNm(0, 0))) return "PxPerNm";

            // ring 60 nm at phi = 0 and 70: every vertex at 60 nm geodesic distance (tolerance 0.1 px)
            double zs = GameRef.ZoomLevelToScale(11);
            foreach (double plat in new[] { 0.0, 70.0 })
            {
                MapViewSnap sr = MapViewSnap.Ortho(plat, 10, zs, 25, plat, 10, 960, 540, 1920, 1080);
                MapPx pr = MapPx.Create(in sr, id, 1920, 1080);
                var xs = new float[257]; var ys = new float[257]; var vs = new bool[257];
                int m = pr.ArcPoints(plat, 10, 60, 0, 360, 24, xs, ys, vs);
                if (m < 25) return "ArcPoints count " + m;
                double ppm = zs;
                for (int i = 0; i < m; i++)
                {
                    if (!vs[i]) return "ring vertex hidden";
                    if (!pr.TryInverse(xs[i], ys[i], out lat, out lon)) return "ring vertex off disc";
                    double d = GameRef.SphericalDistanceM(plat, 10, lat, lon);
                    if (Math.Abs(d - 60 * 1852.0) * ppm > 0.1) return "ring distance " + d + " at phi " + plat;
                }
                var sk = new RecSink(); pr.Arc(plat, 10, 60, 0, 360, 24, sk);
                if (sk.Lines.Count != m - 1 || sk.Breaks != 0) return "full ring lines " + sk.Lines.Count + " vs " + (m - 1);
                // the middle of each piece is within 0.5 px of the true circle
                double worst = 0;
                for (int i = 0; i < m - 1; i++)
                {
                    double bm = 360.0 * (i + 0.5) / (m - 1), tx, ty;
                    V3 u = MapProjector.OffsetOnSphere(plat, 10, bm, 60 * 1852.0 / MapProjector.R);
                    MapProjector.FrameToView(in sr, MapProjector.UnitToFrame(in sr, u), out tx, out ty);
                    double mx = (sk.Lines[i][0] + sk.Lines[i][2]) / 2.0, my = (sk.Lines[i][1] + sk.Lines[i][3]) / 2.0;
                    worst = Math.Max(worst, Dist(mx, my, tx, ty));
                }
                if (worst > 0.5 + 1e-3) return "ring chord sag " + worst;
            }
            // ring through the limb: cut lines, a Break, the fill pressed to the limb
            {
                MapPx pl = px;
                var sk = new RecSink(); pl.Arc(0, 80, 1500, 0, 360, 24, sk);
                if (sk.Breaks < 1 || sk.Lines.Count < 5) return "limb ring lines " + sk.Lines.Count + " breaks " + sk.Breaks;
                var xs = new float[257]; var ys = new float[257]; var vs = new bool[257];
                int m = pl.ArcPoints(0, 80, 1500, 0, 360, 24, xs, ys, vs);
                int hidden = 0;
                for (int i = 0; i < m; i++)
                    if (!vs[i]) { hidden++; if (Math.Abs(Dist(xs[i], ys[i], 960, 540) - rLimb) > 1e-2) return "fill vertex not on the limb"; }
                if (hidden == 0) return "limb ring has no hidden vertices";
                if (pl.Culled(0, 80, 1500)) return "limb ring culled";
            }
            // Culled: behind the horizon, outside the target, in view
            {
                if (!px.Culled(0, 170, 60)) return "behind not culled";
                if (px.Culled(0, 0, 60)) return "centre culled";
                MapViewSnap sz = MapViewSnap.Ortho(0, 0, GameRef.ZoomLevelToScale(12), 0, 0, 0, 960, 540, 1920, 1080);
                MapPx pz = MapPx.Create(in sz, id, 1920, 1080);
                if (!pz.Culled(0, 5, 20)) return "far outside not culled";
                if (pz.Culled(0, 0.2, 20, 16)) return "near ring culled";
            }
            // GridPt = Pt in the nodes (1e-9 px), hidden ones pressed
            {
                MapViewSnap sg = MapViewSnap.Ortho(10, 20, GameRef.ZoomLevelToScale(8), 33, 10, 20, 960, 540, 1920, 1080);
                MapPx pg = MapPx.Create(in sg, id, 1920, 1080);
                pg.PrepareGrid(15, 0.5, 20, 15, 0.5, 20);
                for (int r = 0; r <= 20; r++)
                    for (int c = 0; c <= 20; c++)
                    {
                        double gx, gy, ax, ay;
                        bool gv = pg.GridPt(r, c, out gx, out gy), pv = pg.Pt(15 - r * 0.5, 15 + c * 0.5, out ax, out ay);
                        if (gv != pv || (pv && (Math.Abs(gx - ax) > 1e-9 || Math.Abs(gy - ay) > 1e-9))) return "GridPt " + r + "," + c;
                    }
                // a grid over the limb: hidden corners pressed
                MapPx pw = MapPx.Create(in s, id, 1920, 1080);
                pw.PrepareGrid(10, 10, 2, 70, 10, 4);   // lon 70..110
                int hid = 0;
                for (int c = 0; c <= 4; c++)
                {
                    double gx, gy;
                    if (!pw.GridPt(1, c, out gx, out gy)) { hid++; if (Math.Abs(Dist(gx, gy, 960, 540) - rLimb) > 1e-6) return "grid corner not on the limb"; }
                }
                if (hid == 0 || pw.GridPt(5, 0, out double ox, out double oy)) return "grid hidden/out of range";
            }
            // budget: lines are cut (Cut > 0) and counted
            {
                MapPx pb = MapPx.Create(in s, id, 1920, 1080);
                pb.VertexBudget = 10;
                var sk = new RecSink();
                for (int i = 0; i < 10; i++) pb.Line(0, -60, 0, 60, sk);
                if (pb.Cut == 0 || pb.Verts <= 10) return "budget cut " + pb.Cut + " verts " + pb.Verts;
                pb.ResetCounters();
                if (pb.Cut != 0 || pb.Verts != 0) return "ResetCounters";
            }
            // Reflect through a stub = Ortho
            {
                MapPx pr = MapPx.Reflect(in s, StubFor(s), id, 1920, 1080);
                if (pr.Kind != MapProjKind.Reflect || pr.VertexBudget != 4000) return "reflect kind/budget";
                for (int i = 0; i < 30; i++)
                {
                    double la = i * 5 - 70, lo = i * 7 - 100, ax, ay, bx, by;
                    bool a = px.Pt(la, lo, out ax, out ay), b = pr.Pt(la, lo, out bx, out by);
                    if (a != b || (a && (Math.Abs(ax - bx) > 1e-9 || Math.Abs(ay - by) > 1e-9))) return "reflect Pt " + la + "," + lo;
                }
                var s1 = new RecSink(); var s2 = new RecSink();
                px.Arc(10, 10, 300, 0, 360, 24, s1); pr.Arc(10, 10, 300, 0, 360, 24, s2);
                if (s1.Lines.Count != s2.Lines.Count || s1.Breaks != s2.Breaks) return "reflect arc count";
                for (int i = 0; i < s1.Lines.Count; i++)
                    for (int k = 0; k < 4; k++) if (Math.Abs(s1.Lines[i][k] - s2.Lines[i][k]) > 1e-3) return "reflect arc line " + i;
                // the game refuses a point -> Pt false
                MapPx pf = MapPx.Reflect(in s, delegate (double la, double lo, out double ox, out double oy) { ox = 0; oy = 0; return false; }, id, 1920, 1080);
                if (pf.Pt(0, 0, out x, out y)) return "reflect refusal";
            }
            return null;
        }
    }

    internal static class PxKeyCases
    {
        private static MapPxKey K(double zoom, double rot, double clat, double clon, double dvx, double dvy, int ow = 1920, double[] o = null)
        {
            double scale = GameRef.ZoomLevelToScale((float)zoom);
            MapViewSnap s = MapViewSnap.Ortho(clat, clon, scale, rot, clat, clon, 960 + dvx, 540 + dvy, 1920, 1080);
            return MapPx.Create(in s, o ?? new double[] { 1, 0, 0, 0, 1, 0 }, ow, 1080).Key;
        }

        public static string Run()
        {
            MapPxKey a = K(12, 20, 30, 40, 0, 0);
            if (MapPx.KeyShiftPx(in a, in a) != 0) return "same key";
            double d04 = MapPx.KeyShiftPx(in a, K(12, 20, 30, 40, 0.4, 0));
            double d06 = MapPx.KeyShiftPx(in a, K(12, 20, 30, 40, 0.6, 0));
            if (!(d04 <= 0.5) || !(d06 > 0.5)) return "viewRef shift 0.4 -> " + d04 + ", 0.6 -> " + d06;
            // rotation: the corner at 1100 px moves 0.6 px
            double dr = 0.6 / 1101.0 * 180.0 / Math.PI;
            double dRot = MapPx.KeyShiftPx(in a, K(12, 20 + dr, 30, 40, 0, 0));
            if (!(dRot > 0.5) || dRot > 0.8) return "rotation shift " + dRot;
            double dRotS = MapPx.KeyShiftPx(in a, K(12, 20 + dr * 0.6, 30, 40, 0, 0));
            if (!(dRotS <= 0.5)) return "small rotation shift " + dRotS;
            // zoom
            double zs = Math.Log(1 + 0.6 / 1101.0, 2.0);
            double dZ = MapPx.KeyShiftPx(in a, K(12 + zs, 20, 30, 40, 0, 0));
            if (!(dZ > 0.5) || dZ > 0.8) return "zoom shift " + dZ;
            // centre: 0.6 px of panning
            double sc = GameRef.ZoomLevelToScale(12);
            double dLat = 0.6 / sc / 111319.49;
            double dC = MapPx.KeyShiftPx(in a, K(12, 20, 30 + dLat, 40, 0, 0));
            if (!(dC > 0.45) || dC > 0.8) return "pan shift " + dC;
            double dC2 = MapPx.KeyShiftPx(in a, K(12, 20, 30 + dLat * 0.5, 40, 0, 0));
            if (!(dC2 <= 0.5)) return "small pan shift " + dC2;
            // another target size or Kind: infinite
            if (!double.IsPositiveInfinity(MapPx.KeyShiftPx(in a, K(12, 20, 30, 40, 0, 0, 960)))) return "OutW";
            MapPxKey aff = MapPx.FromAffine(new double[] { 1, 0, 0, 0, 1, 0 }, 1920, 1080).Key;
            if (!double.IsPositiveInfinity(MapPx.KeyShiftPx(in a, in aff))) return "Kind change";
            if (MapPx.KeyShiftPx(in aff, in aff) != 0) return "affine same";
            if (!double.IsPositiveInfinity(MapPx.KeyShiftPx(in aff, MapPx.FromAffine(new double[] { 1, 0, 0.1, 0, 1, 0 }, 1920, 1080).Key))) return "affine changed";
            if (!double.IsPositiveInfinity(MapPx.KeyShiftPx(in a, K(12, 20, 30, 40, 0, 0, 1920, new double[] { 0.5, 0, 0, 0, 0.5, 0 })))) return "out changed";
            Console.WriteLine("  key shifts: viewRef 0.4 -> " + d04.ToString("F3") + ", 0.6 -> " + d06.ToString("F3") + "; rot " + dRot.ToString("F3") + " zoom " + dZ.ToString("F3") + " pan " + dC.ToString("F3"));
            return null;
        }
    }

    internal static class PxPerfCases
    {
        private sealed class NullSink : IGeoSink { public int N; public void Line(float a, float b, float c, float d) { N++; } public void Break() { } }

        public static string Run()
        {
            MapViewSnap s = MapViewSnap.Ortho(30, 20, GameRef.ZoomLevelToScale(9), 15, 30, 20, 960, 540, 1920, 1080);
            MapPx px = MapPx.Create(in s, new double[] { 1, 0, 0, 0, 1, 0 }, 1920, 1080);
            var sink = new NullSink();
            px.Arc(30, 20, 100, 0, 360, 24, sink);   // warm up
            px.ResetCounters();
            var sw = Stopwatch.StartNew();
            int iters = 0;
            while (px.Verts < 60000 && iters < 100000) { px.Arc(30, 20, 20 + iters % 150, 0, 360, 24, sink); iters++; }
            sw.Stop();
            Console.WriteLine("  perf (net8, not Mono): " + px.Verts + " vertices of Arc in " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms (" + sink.N + " lines)");
            return null;
        }
    }
}
