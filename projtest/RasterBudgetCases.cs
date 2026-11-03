// T-311 B1/B2: CoverageRasterPx - visible window, culling, coarsening; N2 regression (coverage must not eat the ring budget).
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge.ProjTest
{
    internal sealed class CountQuads : IQuadSink
    {
        public int Quads, Alphas;
        public readonly List<float[]> List;
        public CountQuads(bool keep) { if (keep) List = new List<float[]>(); }
        public void Alpha(float a) { Alphas++; }
        public void Quad(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
        {
            Quads++;
            if (List != null) List.Add(new[] { x0, y0, x1, y1, x2, y2, x3, y3 });
        }
    }

    internal static class RasterBudgetCases
    {
        private static readonly double[] Id = { 1, 0, 0, 0, 1, 0 };

        private static MapPx View(double lat, double lon, double scale, double rot = 0, int w = 2560, int h = 1440)
        {
            MapViewSnap s = MapViewSnap.Ortho(lat, lon, scale, rot, lat, lon, w / 2.0, h / 2.0, w, h);
            return MapPx.Create(in s, Id, w, h);
        }

        private static double[] Rect(int w = 2560, int h = 1440) { return new[] { 0.0, 0.0, (double)w, (double)h }; }

        private static GeoBox Box(double lat, double lon, double halfNm)
        {
            double dl = halfNm / 60.0, dn = dl / Math.Cos(lat * Math.PI / 180.0);
            return new GeoBox { LatMin = lat - dl, LatMax = lat + dl, LonMin = lon - dn, LonMax = lon + dn };
        }

        /// <summary>Alternating alpha every 'step' cells in a row, shifted per row.</summary>
        private static List<CoverageMath.RasterRun> Motley(int rows, int cols, int step)
        {
            var l = new List<CoverageMath.RasterRun>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c += step)
                    l.Add(new CoverageMath.RasterRun { Row = r, X0 = c, X1 = Math.Min(cols - 1, c + step - 1), Alpha = 0.2f + 0.2f * (((c / step) + r / 5) % 3) });
            return l;
        }

        private static List<CoverageMath.RasterRun> Disks(int rows, int cols, int n)
        {
            var l = new List<CoverageMath.RasterRun>();
            for (int r = 0; r < rows; r++)
            {
                for (int d = 0; d < n; d++)
                {
                    double cr = rows * (0.2 + 0.6 * ((d * 5) % 8) / 8.0), cc = cols * (0.15 + 0.7 * ((d * 3) % 8) / 8.0), rad = rows * 0.09;
                    double dy = r - cr;
                    if (Math.Abs(dy) >= rad) continue;
                    double half = Math.Sqrt(rad * rad - dy * dy);
                    int x0 = (int)Math.Max(0, cc - half), x1 = (int)Math.Min(cols - 1, cc + half);
                    if (x0 <= x1) l.Add(new CoverageMath.RasterRun { Row = r, X0 = x0, X1 = x1, Alpha = 0.15f + 0.15f * (d % 3) });
                }
            }
            return l;
        }

        private static string Key(float[] q)
        {
            return string.Join(",", Array.ConvertAll(q, v => v.ToString("R")));
        }

        private static bool Hits(float[] q, double[] rect)
        {
            float minx = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), maxx = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
            float miny = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), maxy = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
            return maxx >= rect[0] && minx <= rect[0] + rect[2] && maxy >= rect[1] && miny <= rect[1] + rect[3];
        }

        public static string Run()
        {
            const double Tact = 2560.0 / 5000.0;
            // ---- B1: VisibleWindow ----
            {
                MapPx px = View(12.94, 43.11, Tact);
                GeoBox b = Box(12.94, 43.11, 100);
                int r0, r1, c0, c1;
                if (!CoverageRasterPx.VisibleWindow(px, Rect(), b, 512, 512, out r0, out r1, out c0, out c1)) return "tactical window not found";
                if (r1 - r0 + 1 > 30 || c1 - c0 + 1 > 30 || r0 > 256 || r1 < 256 || c0 > 256 || c1 < 256) return "tactical window " + r0 + ".." + r1 + " x " + c0 + ".." + c1;
                // the whole globe: the rect is wider than the disc
                double sc = T.FullDiscScale(1080) * 0.9;
                MapViewSnap sg = MapViewSnap.Ortho(0, 0, sc, 0, 0, 0, 960, 540, 1920, 1080);
                MapPx pg = MapPx.Create(in sg, Id, 1920, 1080);
                if (CoverageRasterPx.VisibleWindow(pg, Rect(1920, 1080), b, 512, 512, out r0, out r1, out c0, out c1)) return "globe window must be false";
                // the pole in the frame
                MapPx pp = View(85, 20, 5e-4, 0, 1920, 1080);
                var bp = new GeoBox { LatMin = 60, LatMax = 90, LonMin = -180, LonMax = 180 };
                if (!CoverageRasterPx.VisibleWindow(pp, Rect(1920, 1080), bp, 90, 360, out r0, out r1, out c0, out c1)) return "pole window not found";
                if (r0 != 0 || c0 != 0 || c1 != 359) return "pole window " + r0 + ".." + r1 + " x " + c0 + ".." + c1;
                // the antimeridian
                var ba = new GeoBox { LatMin = 10, LatMax = 20, LonMin = 175, LonMax = 185 };
                foreach (double lon in new[] { 179.9, -179.9 })
                {
                    MapPx pa = View(15, lon, 0.02, 0, 1920, 1080);
                    if (!CoverageRasterPx.VisibleWindow(pa, Rect(1920, 1080), ba, 100, 100, out r0, out r1, out c0, out c1)) return "antimeridian window not found";
                    if (c0 > 44 || c1 < 53 || c1 - c0 > 25 || r0 > 50 || r1 < 50) return "antimeridian window lon " + lon + ": " + r0 + ".." + r1 + " x " + c0 + ".." + c1;
                }
                // the view is outside the box
                MapPx po = View(15, 100, 0.02, 0, 1920, 1080);
                if (!CoverageRasterPx.VisibleWindow(po, Rect(1920, 1080), ba, 100, 100, out r0, out r1, out c0, out c1)) return "outside window must exist";
                if (r0 <= r1 && c0 <= c1) return "outside window not empty " + r0 + ".." + r1 + " x " + c0 + ".." + c1;
                // Affine: no window
                MapPx pf = MapPx.FromAffine(new double[] { 10, 0, 0, 0, 10, 0 }, 100, 100);
                if (CoverageRasterPx.VisibleWindow(pf, Rect(100, 100), ba, 10, 10, out r0, out r1, out c0, out c1)) return "affine window";
            }
            // ---- B2: Emit ----
            // N2 regression
            {
                MapPx px = View(12.94, 43.11, Tact);
                GeoBox b = Box(12.94, 43.11, 100);
                var runs = Motley(512, 512, 3);
                var sink = new CountQuads(false);
                px.BeginSection(px.Share(CoverageRasterPx.CoverageShare));
                var sw = Stopwatch.StartNew();
                RasterEmitStats st = CoverageRasterPx.Emit(px, runs, b, 512, 512, Rect(), sink);
                double tTact = sw.Elapsed.TotalMilliseconds;
                if (px.Verts > 36000 || st.Truncated || st.Quads <= 0 || !st.Windowed) return "N2: verts " + px.Verts + " trunc " + st.Truncated + " quads " + st.Quads;
                if (st.RunsCulled < runs.Count * 0.99) return "N2: runs culled " + st.RunsCulled + " of " + runs.Count;
                if (st.Pieces > 2000) return "N2: pieces " + st.Pieces;
                if (px.Verts != st.Quads * 4 || sink.Quads != st.Quads) return "N2: accounting";
                px.EndSection();
                int cut0 = px.Cut;
                var rs = new RecSink();
                px.Arc(12.94, 43.11, 9.7, 0, 360, 24, rs);
                if (rs.Lines.Count <= 0 || px.Cut != cut0) return "N2: ring lines " + rs.Lines.Count + " cut " + (px.Cut - cut0);

                // completeness against the reference without a window and without a budget
                foreach (var cfg in new[] { new double[] { 12.94, 43.11, 0 }, new double[] { 15, 179.97, 30 } })
                {
                    MapPx pw = View(cfg[0], cfg[1], Tact, cfg[2]);
                    MapPx pr = View(cfg[0], cfg[1], Tact, cfg[2]);
                    pr.VertexBudget = int.MaxValue;
                    GeoBox bc = Box(cfg[0], cfg[1], 100);
                    if (cfg[1] > 179) { bc.LonMin = cfg[1] - 1.7; bc.LonMax = cfg[1] + 1.7; }
                    var kw = new CountQuads(true); var kr = new CountQuads(true);
                    RasterEmitStats sw1 = CoverageRasterPx.Emit(pw, runs, bc, 512, 512, Rect(), kw);
                    RasterEmitStats sr1 = CoverageRasterPx.Emit(pr, runs, bc, 512, 512, Rect(), kr, false);
                    if (sr1.Windowed || !sw1.Windowed) return "complete: modes";
                    var have = new HashSet<string>();
                    foreach (var q in kw.List) have.Add(Key(q));
                    int need = 0;
                    foreach (var q in kr.List) if (Hits(q, Rect())) { need++; if (!have.Contains(Key(q))) return "complete: a visible quad is missing (lon " + cfg[1] + ")"; }
                    if (need == 0) return "complete: no visible quads in the reference (lon " + cfg[1] + ")";
                }

                // operational: the whole box on the screen
                double sOp = 1920.0 / (2 * 100 * 1852.0) * 0.95;
                MapPx po = View(12.94, 43.11, sOp, 0, 1920, 1080);
                var disks = Disks(512, 512, 8);
                po.BeginSection(po.Share(CoverageRasterPx.CoverageShare));
                var ko = new CountQuads(false);
                sw.Restart();
                RasterEmitStats so = CoverageRasterPx.Emit(po, disks, b, 512, 512, Rect(1920, 1080), ko);
                double tOp = sw.Elapsed.TotalMilliseconds;
                MapPx pref = View(12.94, 43.11, sOp, 0, 1920, 1080);
                pref.VertexBudget = int.MaxValue;
                var kref = new CountQuads(false);
                RasterEmitStats sref = CoverageRasterPx.Emit(pref, disks, b, 512, 512, Rect(1920, 1080), kref, false);
                if (so.Coarsen != 1 || so.Truncated || so.Quads != sref.Quads || po.Verts > 36000 || so.Quads == 0) return "operational: coarsen " + so.Coarsen + " trunc " + so.Truncated + " quads " + so.Quads + " vs " + sref.Quads;

                // global stress: a chequered raster
                double scg = T.FullDiscScale(1080) * 0.9;
                MapViewSnap sgl = MapViewSnap.Ortho(0, 0, scg, 0, 0, 0, 960, 540, 1920, 1080);
                MapPx pgl = MapPx.Create(in sgl, Id, 1920, 1080);
                var bg = new GeoBox { LatMin = -40, LatMax = 40, LonMin = -60, LonMax = 60 };
                var cheq = new List<CoverageMath.RasterRun>();
                for (int r = 0; r < 512; r++)
                    for (int c = r % 2; c < 512; c += 2) cheq.Add(new CoverageMath.RasterRun { Row = r, X0 = c, X1 = c, Alpha = 0.3f });
                pgl.BeginSection(pgl.Share(CoverageRasterPx.CoverageShare));
                sw.Restart();
                RasterEmitStats sg2 = CoverageRasterPx.Emit(pgl, cheq, bg, 512, 512, Rect(1920, 1080), new CountQuads(false));
                double tGl = sw.Elapsed.TotalMilliseconds;
                if (pgl.Verts > 36000 || !(sg2.Truncated || sg2.Coarsen > 1)) return "stress: verts " + pgl.Verts + " trunc " + sg2.Truncated + " coarsen " + sg2.Coarsen;
                pgl.EndSection();
                var rg = new RecSink();
                pgl.Arc(0, 0, 600, 0, 360, 24, rg);
                if (rg.Lines.Count == 0) return "stress: ring got no lines";
                Console.WriteLine("  raster perf (net8): tactical " + tTact.ToString("F2") + " ms, operational " + tOp.ToString("F2") + " ms, global-stress " + tGl.ToString("F2") + " ms");
            }
            // coarsening: few long runs, small budget
            {
                MapPx px = View(12.94, 43.11, Tact);
                px.VertexBudget = 8000;
                GeoBox b = Box(12.94, 43.11, 100);
                var runs = new List<CoverageMath.RasterRun>();
                for (int r = 100; r < 200; r++) runs.Add(new CoverageMath.RasterRun { Row = r, X0 = 0, X1 = 511, Alpha = 0.3f });
                RasterEmitStats st = CoverageRasterPx.Emit(px, runs, b, 512, 512, Rect(), new CountQuads(false), false);
                if (st.Coarsen <= 1 || st.Truncated || px.Verts > 8000) return "coarsen: k " + st.Coarsen + " trunc " + st.Truncated + " verts " + px.Verts;
            }
            return null;
        }
    }
}
