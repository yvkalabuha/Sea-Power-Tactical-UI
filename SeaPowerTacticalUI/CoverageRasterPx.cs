// T-311 / ADR-0119: coverage raster -> quads in target pixels for the beta globe projector. Pure (no Unity, no game):
// only the part of the raster that can be seen is projected (window by the inverse projection of the target rectangle),
// pieces outside the rectangle are dropped, and when the quads do not fit the budget the pieces are coarsened instead of cut.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal interface IQuadSink
    {
        /// <summary>Called before the quads of a run when alpha changes.</summary>
        void Alpha(float a);
        void Quad(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3);
    }

    internal struct RasterEmitStats
    {
        public int Runs, RunsCulled, Pieces, PiecesCulled, Quads, Coarsen;   // Coarsen = 1 normally, k > 1 when maxCols was multiplied
        public bool Windowed, Truncated;
    }

    internal static class CoverageRasterPx
    {
        public const double CoverageShare = 0.6;   // of MapPx.VertexBudget: Ortho 36 000, Reflect 2 400
        public const double MarginPx = 16;
        public const int EdgeSamples = 32;         // per side of the target rect

        /// <summary>Visible row/col window of the grid (inclusive, with a 2-cell margin). false = no window (Affine, a rect corner/edge beyond the disk, NaN)
        /// -> the caller uses the whole grid. true with r0 &gt; r1 or c0 &gt; c1 = nothing visible.</summary>
        public static bool VisibleWindow(MapPx px, double[] rect, GeoBox box, int rows, int cols,
                                         out int r0, out int r1, out int c0, out int c1)
        {
            r0 = 0; r1 = rows - 1; c0 = 0; c1 = cols - 1;
            if (px == null || rect == null || rect.Length < 4 || px.Kind == MapProjKind.Affine || rows < 1 || cols < 1) return false;
            double xa = rect[0] - MarginPx, ya = rect[1] - MarginPx, xb = rect[0] + rect[2] + MarginPx, yb = rect[1] + rect[3] + MarginPx;
            if (double.IsNaN(xa + ya + xb + yb)) return false;
            double refLon = (box.LonMin + box.LonMax) * 0.5;
            double latLo = double.MaxValue, latHi = double.MinValue, lonLo = double.MaxValue, lonHi = double.MinValue;
            int n = EdgeSamples;
            for (int side = 0; side < 4; side++)
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / n, x, y;
                    switch (side)
                    {
                        case 0: x = xa + (xb - xa) * t; y = ya; break;
                        case 1: x = xb; y = ya + (yb - ya) * t; break;
                        case 2: x = xb - (xb - xa) * t; y = yb; break;
                        default: x = xa; y = yb - (yb - ya) * t; break;
                    }
                    double la, lo;
                    if (!px.TryInverse(x, y, out la, out lo)) return false;
                    lo += 360.0 * Math.Round((refLon - lo) / 360.0);
                    if (la < latLo) latLo = la;
                    if (la > latHi) latHi = la;
                    if (lo < lonLo) lonLo = lo;
                    if (lo > lonHi) lonHi = lo;
                }
            if (double.IsNaN(latLo + latHi + lonLo + lonHi)) return false;
            bool allCols = false;
            foreach (double pole in new[] { 90.0, -90.0 })
            {
                double x, y;
                if (px.Pt(pole, 0, out x, out y) && x >= xa && x <= xb && y >= ya && y <= yb)
                {
                    if (pole > 0) latHi = 90; else latLo = -90;
                    allCols = true;
                }
            }
            double dLat = (box.LatMax - box.LatMin) / rows, dLon = (box.LonMax - box.LonMin) / cols;
            if (!(dLat > 0) || !(dLon > 0)) return false;
            r0 = (int)Math.Max(-1.0, Math.Floor((box.LatMax - latHi) / dLat) - 2);
            r1 = (int)Math.Min(rows, Math.Ceiling((box.LatMax - latLo) / dLat) + 2);
            if (r0 < 0) r0 = 0;
            if (r1 > rows - 1) r1 = rows - 1;
            if (allCols) { c0 = 0; c1 = cols - 1; }
            else
            {
                double a = Math.Floor((lonLo - box.LonMin) / dLon) - 2, b = Math.Ceiling((lonHi - box.LonMin) / dLon) + 2;
                c0 = (int)Math.Max(0.0, Math.Min(a, cols)); c1 = (int)Math.Min(cols - 1.0, Math.Max(b, -1.0));
                if (b < 0 || a > cols - 1) { c0 = 0; c1 = -1; }
            }
            return true;
        }

        private static int MaxCols(double thetaMax, double dLon, double latRad)
        {
            double cellRad = Math.Max(1e-9, dLon * Math.PI / 180.0 * Math.Max(0.05, Math.Cos(latRad)));
            return Math.Max(1, (int)Math.Floor(thetaMax / cellRad));
        }

        /// <summary>Raster runs -> quads in target px within px.Remaining. Calls px.PrepareGrid itself; counts 4 per emitted quad (AddVerts).
        /// window = false: reference mode for tests (no VisibleWindow; the piece rect test stays on).</summary>
        public static RasterEmitStats Emit(MapPx px, List<CoverageMath.RasterRun> runs, GeoBox box, int rows, int cols,
                                           double[] rect, IQuadSink sink, bool window = true)
        {
            var st = new RasterEmitStats { Coarsen = 1 };
            if (px == null || runs == null || sink == null || rows < 1 || cols < 1) return st;
            double dLat = (box.LatMax - box.LatMin) / rows, dLon = (box.LonMax - box.LonMin) / cols;
            px.PrepareGrid(box.LatMax, dLat, rows, box.LonMin, dLon, cols);
            double pxPerM = px.PxPerNm(px.Snap.Lat0, px.Snap.Lon0) / 1852.0;
            double thetaMax = Math.Sqrt(8.0 * MapProjector.TolPx / (MapProjector.R * Math.Max(pxPerM, 1e-9)));
            int wr0 = 0, wr1 = rows - 1, wc0 = 0, wc1 = cols - 1;
            if (window && VisibleWindow(px, rect, box, rows, cols, out wr0, out wr1, out wc0, out wc1)) st.Windowed = true;
            else { wr0 = 0; wr1 = rows - 1; wc0 = 0; wc1 = cols - 1; }
            st.Runs = runs.Count;
            // pass 1: how many pieces would be drawn
            long pieces = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                CoverageMath.RasterRun run = runs[i];
                int x0 = Math.Max(run.X0, wc0), x1 = Math.Min(run.X1, wc1);
                if (run.Row < wr0 || run.Row > wr1 || x0 > x1) continue;
                int mc = MaxCols(thetaMax, dLon, (box.LatMax - (run.Row + 0.5) * dLat) * Math.PI / 180.0);
                pieces += (x1 - run.X0) / mc - (x0 - run.X0) / mc + 1;
            }
            int remaining = px.Remaining;
            int k = 1;
            if (pieces * 4 > remaining && remaining >= 4)
            {
                long kk = (pieces * 4 + remaining - 1) / remaining;
                k = (int)Math.Min(kk, 1 << 20);
            }
            st.Coarsen = k;
            // pass 2
            float lastAlpha = float.NaN;
            double xa = 0, ya = 0, xb = 0, yb = 0;
            bool haveRect = rect != null && rect.Length >= 4;
            if (haveRect) { xa = rect[0] - MarginPx; ya = rect[1] - MarginPx; xb = rect[0] + rect[2] + MarginPx; yb = rect[1] + rect[3] + MarginPx; }
            for (int i = 0; i < runs.Count && !st.Truncated; i++)
            {
                CoverageMath.RasterRun run = runs[i];
                int x0 = Math.Max(run.X0, wc0), x1 = Math.Min(run.X1, wc1);
                if (run.Row < wr0 || run.Row > wr1 || x0 > x1) { st.RunsCulled++; continue; }
                long mcL = (long)MaxCols(thetaMax, dLon, (box.LatMax - (run.Row + 0.5) * dLat) * Math.PI / 180.0) * k;
                int mc = (int)Math.Min(mcL, int.MaxValue / 2);
                int pc0 = (x0 - run.X0) / mc, pc1 = (x1 - run.X0) / mc;
                for (int pc = pc0; pc <= pc1; pc++)
                {
                    st.Pieces++;
                    int c0 = run.X0 + pc * mc, c1 = (int)Math.Min((long)run.X1 + 1, (long)c0 + mc);
                    double ax, ay, bx, by, cx, cy, dx, dy;
                    bool v0 = px.GridCorner(run.Row, c0, out ax, out ay), v1 = px.GridCorner(run.Row, c1, out bx, out by);
                    bool v2 = px.GridCorner(run.Row + 1, c1, out cx, out cy), v3 = px.GridCorner(run.Row + 1, c0, out dx, out dy);
                    if (!(v0 || v1 || v2 || v3)) continue;
                    if (haveRect && (Outside(ax < xa, bx < xa, cx < xa, dx < xa) || Outside(ax > xb, bx > xb, cx > xb, dx > xb)
                                     || Outside(ay < ya, by < ya, cy < ya, dy < ya) || Outside(ay > yb, by > yb, cy > yb, dy > yb)))
                    { st.PiecesCulled++; continue; }
                    if (px.Remaining < 4) { st.Truncated = true; break; }
                    if (run.Alpha != lastAlpha) { sink.Alpha(run.Alpha); lastAlpha = run.Alpha; }
                    sink.Quad((float)ax, (float)ay, (float)bx, (float)by, (float)cx, (float)cy, (float)dx, (float)dy);
                    px.AddVerts(4);
                    st.Quads++;
                }
            }
            return st;
        }

        private static bool Outside(bool a, bool b, bool c, bool d) { return a && b && c && d; }
    }
}
