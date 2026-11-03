using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: pure route planning around land (A*) on the game's flat grid
    // (1 degree = 60 nm both ways on stable, longitude x LonK on the beta globe, T-348; as AswMath.FlatNm). No Unity, no reflection.
    internal sealed class RouteParams { public double MinDepthM = 8, ShoreMarginNm = 0.4, StepNm = 0.1; public int MaxCells = 40000, MaxPoints = 12; }
    internal sealed class RouteResult { public string Kind; /* direct|astar|none */ public List<GeoPt> Points; public int Probes, Cells; public double Ms; public string Why; public bool NearShore; }

    /// <summary>Regular grid anchored at the start point (the start is a cell centre), so a bigger frame re-uses the cached cells.</summary>
    internal sealed class RouteGrid
    {
        public GeoPt Origin;
        public double C;                 // cell size, nm
        public double LonK = 1.0;        // T-348: K of the origin row (old tests); = AswMath.LonK at the origin latitude; 1.0 on stable
        public double[] RowK;            // T-356 (R4): AswMath.LonK(Lat(i)) snapshot per row (1.0 exactly in the flat world); the background Solve reads only this snapshot
        public double KMax = 1.0;        // max RowK (the step count of GridClear)
        public int RowMin, ColMin, Rows, Cols;
        public int Cells { get { return Rows * Cols; } }
        public double Lat(int i) { return Origin.Lat + (RowMin + i) * C / 60.0; }
        public double Lon(int i, int j) { return Origin.Lon + (ColMin + j) * C / (60.0 * RowK[i]); }
        public GeoPt Center(int i, int j) { return new GeoPt(Lat(i), Lon(i, j)); }
        /// <summary>Snapshot of the east-west scale per row (call after Rows/RowMin/C are final).</summary>
        public void SnapK()
        {
            RowK = new double[Rows]; KMax = 1.0;
            for (int i = 0; i < Rows; i++) { RowK[i] = AswMath.LonK(Lat(i)); if (i == 0 || RowK[i] > KMax) KMax = RowK[i]; }
        }
        public void Locate(GeoPt g, out int i, out int j)
        {
            i = (int)Math.Round((g.Lat - Origin.Lat) * 60.0 / C) - RowMin;
            if (i < 0) i = 0; else if (i >= Rows) i = Rows - 1;
            j = (int)Math.Round(GeoLon.DLon(Origin.Lon, g.Lon) * 60.0 * RowK[i] / C) - ColMin;
            if (j < 0) j = 0; else if (j >= Cols) j = Cols - 1;
        }
    }

    /// <summary>Main thread: Step() asks probes in budget; when the grid is full, Solve() runs on a background thread (pure, no Unity).</summary>
    internal sealed class RouteJob
    {
        private readonly GeoPt _from, _to;
        private readonly RouteParams _p;
        private readonly Dictionary<long, double> _cache = new Dictionary<long, double>();
        private readonly object _gate = new object();
        private string _state = "probe_line";
        private RouteResult _result;
        private List<GeoPt> _line;
        private int _lineIdx;
        private RouteGrid _grid;
        private bool[,] _land;
        private int _gridIdx;
        private int _factor = 1;
        private int _probes;

        public RouteJob(GeoPt from, GeoPt to, RouteParams p)
        {
            _from = from; _to = to; _p = p ?? new RouteParams();
            _line = new List<GeoPt>();
            _line.Add(to);                               // the raft itself first: on land -> none at once
            _line.AddRange(SarRouteMath.LinePattern(from, to, _p));
        }

        public string State { get { lock (_gate) return _state; } }
        public RouteResult Result { get { lock (_gate) return _result; } }
        public int Probes { get { return _probes; } }
        public int FrameFactor { get { return _factor; } }

        private double Get(GeoPt g, Func<GeoPt, double> heightM, ref int used)
        {
            double n = (g.Lat - _from.Lat) * 60.0, e = GeoLon.DLon(_from.Lon, g.Lon) * 60.0 * AswMath.LonK(_from.Lat, g.Lat);
            long key = (((long)Math.Round(n / 0.05)) << 32) ^ (((long)Math.Round(e / 0.05)) & 0xffffffffL);
            double h;
            if (_cache.TryGetValue(key, out h)) return h;
            h = heightM(g);
            _cache[key] = h;
            _probes++; used++;
            return h;
        }

        private void SetState(string s) { lock (_gate) _state = s; }

        private void Finish(RouteResult r, double ms)
        {
            r.Probes = _probes; r.Ms = ms;
            lock (_gate) { _result = r; _state = "done"; }
        }

        private void BeginGrid()
        {
            _grid = SarRouteMath.MakeGrid(_from, _to, _factor, _p.MaxCells);
            _land = new bool[_grid.Rows, _grid.Cols];
            _gridIdx = 0;
            SetState("probe_grid");
        }

        public int Step(Func<GeoPt, double> heightM, int budget)
        {
            int used = 0;
            while (used < budget)
            {
                string st = State;
                if (st == "probe_line")
                {
                    double h = Get(_line[_lineIdx], heightM, ref used);
                    if (!SarRouteMath.Passable(h, _p.MinDepthM))
                    {
                        if (_lineIdx == 0) { Finish(new RouteResult { Kind = "none", Why = "land", Points = new List<GeoPt>() }, 0); break; }
                        BeginGrid();
                        continue;
                    }
                    _lineIdx++;
                    if (_lineIdx >= _line.Count)
                    {
                        Finish(new RouteResult { Kind = "direct", Points = new List<GeoPt> { _to } }, 0);
                        break;
                    }
                }
                else if (st == "probe_grid")
                {
                    int cols = _grid.Cols;
                    int i = _gridIdx / cols, j = _gridIdx % cols;
                    double h = Get(_grid.Center(i, j), heightM, ref used);
                    _land[i, j] = !SarRouteMath.Passable(h, _p.MinDepthM);
                    _gridIdx++;
                    if (_gridIdx >= _grid.Cells) SetState("ready_to_solve");
                }
                else break;                              // ready_to_solve | solving | done
            }
            return used;
        }

        /// <summary>Background-safe. Success -> done. No path -> frame x2 (back to probe_grid), then none/no_path.</summary>
        public void Solve()
        {
            try { SolveCore(); }
            catch (Exception)
            {
                // a background exception must not strand the job in "solving" (the ship would wait for ever)
                lock (_gate) { if (_state == "solving") { _result = new RouteResult { Kind = "none", Why = "error", Points = new List<GeoPt>(), Probes = _probes }; _state = "done"; } }
            }
        }

        private void SolveCore()
        {
            lock (_gate) { if (_state != "ready_to_solve") return; _state = "solving"; }
            var sw = Stopwatch.StartNew();
            RouteGrid g = _grid;
            bool[,] land = _land;
            double c = g.C;
            int k = (int)Math.Ceiling(_p.ShoreMarginNm / c - 1e-9);
            if (k < 1) k = 1;
            bool[,] blocked = SarRouteMath.Inflate(land, k);
            int si, sj, gi, gj;
            g.Locate(_from, out si, out sj);
            g.Locate(_to, out gi, out gj);
            bool nearShore = blocked[si, sj] || blocked[gi, gj];
            if (blocked[si, sj]) Unblock(blocked, land, si, sj, k + 1);
            if (blocked[gi, gj] || nearShore) Unblock(blocked, land, gi, gj, k + 1);
            blocked[si, sj] = false; blocked[gi, gj] = false;
            List<int[]> cells = SarRouteMath.AStar(blocked, new[] { si, sj }, new[] { gi, gj });
            if (cells == null)
            {
                sw.Stop();
                if (_factor < 2) { _factor = 2; BeginGrid(); return; }
                Finish(new RouteResult { Kind = "none", Why = "no_path", Points = new List<GeoPt>(), Cells = g.Cells }, sw.Elapsed.TotalMilliseconds);
                return;
            }
            var path = new List<GeoPt>();
            path.Add(_from);
            for (int n = 1; n < cells.Count - 1; n++) path.Add(g.Center(cells[n][0], cells[n][1]));
            path.Add(_to);
            Func<GeoPt, GeoPt, bool> clear = (a, b) => GridClear(g, blocked, a, b);
            List<GeoPt> sm = SarRouteMath.Smooth(path, clear, _p.MaxPoints);
            sm.RemoveAt(0);
            sw.Stop();
            Finish(new RouteResult { Kind = "astar", Points = sm, Cells = g.Cells, NearShore = nearShore }, sw.Elapsed.TotalMilliseconds);
        }

        private static void Unblock(bool[,] blocked, bool[,] land, int ci, int cj, int r)
        {
            int rows = blocked.GetLength(0), cols = blocked.GetLength(1);
            for (int i = Math.Max(0, ci - r); i <= Math.Min(rows - 1, ci + r); i++)
                for (int j = Math.Max(0, cj - r); j <= Math.Min(cols - 1, cj + r); j++)
                    if (!land[i, j]) blocked[i, j] = false;
        }

        private static bool GridClear(RouteGrid g, bool[,] blocked, GeoPt a, GeoPt b)
        {
            double dn = (b.Lat - a.Lat) * 60.0, de = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * g.KMax;
            double len = Math.Sqrt(dn * dn + de * de);
            int steps = Math.Max(1, (int)Math.Ceiling(len / (g.C * 0.25)));
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                int i, j;
                g.Locate(new GeoPt(a.Lat + (b.Lat - a.Lat) * t, a.Lon + GeoLon.DLon(a.Lon, b.Lon) * t), out i, out j);
                if (blocked[i, j]) return false;
            }
            return true;
        }
    }

    internal static class SarRouteMath
    {
        private const double Q = 0.05;

        public static double CellNm(double distNm)
        {
            double c = distNm / 120.0;
            return c < 0.1 ? 0.1 : (c > 0.5 ? 0.5 : c);
        }

        internal static double Nm(GeoPt a, GeoPt b)
        {
            double dn = (b.Lat - a.Lat) * 60.0, de = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * AswMath.LonK(a.Lat, b.Lat);
            return Math.Sqrt(dn * dn + de * de);
        }

        internal static RouteGrid MakeGrid(GeoPt a, GeoPt b, double factor, int maxCells)
        {
            double dn = (b.Lat - a.Lat) * 60.0, de = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * AswMath.LonK(a.Lat);
            double d = Math.Sqrt(dn * dn + de * de);
            double m = Math.Max(5.0, 0.5 * d) * factor;
            double c = CellNm(d);
            var g = new RouteGrid { Origin = a, LonK = AswMath.LonK(a.Lat) };
            for (int it = 0; it < 40; it++)
            {
                g.C = c;
                g.RowMin = (int)Math.Floor((Math.Min(0, dn) - m) / c);
                int rowMax = (int)Math.Ceiling((Math.Max(0, dn) + m) / c);
                g.ColMin = (int)Math.Floor((Math.Min(0, de) - m) / c);
                int colMax = (int)Math.Ceiling((Math.Max(0, de) + m) / c);
                g.Rows = rowMax - g.RowMin + 1;
                g.Cols = colMax - g.ColMin + 1;
                long cells = (long)g.Rows * g.Cols;
                if (cells <= maxCells) break;
                c *= Math.Sqrt((double)cells / maxCells) * 1.002;
            }
            g.SnapK();
            return g;
        }

        /// <summary>Segment + max(5 nm; 0.5 D) * factor, snapped to the grid (lat0, lon0, lat1, lon1). Cells &lt;= 40000 (cell grows).</summary>
        public static double[] Frame(GeoPt a, GeoPt b, double factor)
        {
            RouteGrid g = MakeGrid(a, b, factor, new RouteParams().MaxCells);
            double west = double.MaxValue, east = double.MinValue;   // T-356: the extremes over the rows (every row has its own K)
            for (int i = 0; i < g.Rows; i++)
            {
                west = Math.Min(west, g.Lon(i, 0) - g.C / (120.0 * g.RowK[i]));
                east = Math.Max(east, g.Lon(i, g.Cols - 1) + g.C / (120.0 * g.RowK[i]));
            }
            return new[] { g.Lat(0) - g.C / 120.0, west, g.Lat(g.Rows - 1) + g.C / 120.0, east };
        }

        public static double FrameCellNm(GeoPt a, GeoPt b, double factor, int maxCells, out int cells)
        {
            RouteGrid g = MakeGrid(a, b, factor, maxCells);
            cells = g.Cells;
            return g.C;
        }

        public static bool Passable(double heightM, double minDepthM) { return heightM <= -minDepthM; }

        /// <summary>Chebyshev dilation of the land by `cells` cells (separable).</summary>
        public static bool[,] Inflate(bool[,] land, int cells)
        {
            int rows = land.GetLength(0), cols = land.GetLength(1);
            if (cells <= 0) return (bool[,])land.Clone();
            var tmp = new bool[rows, cols];
            for (int i = 0; i < rows; i++)
            {
                int last = -1000000;
                var fwd = new int[cols];
                for (int j = 0; j < cols; j++) { if (land[i, j]) last = j; fwd[j] = last; }
                int next = 1000000;
                for (int j = cols - 1; j >= 0; j--)
                {
                    if (land[i, j]) next = j;
                    tmp[i, j] = (j - fwd[j] <= cells) || (next - j <= cells);
                }
            }
            var res = new bool[rows, cols];
            for (int j = 0; j < cols; j++)
            {
                int last = -1000000;
                var fwd = new int[rows];
                for (int i = 0; i < rows; i++) { if (tmp[i, j]) last = i; fwd[i] = last; }
                int next = 1000000;
                for (int i = rows - 1; i >= 0; i--)
                {
                    if (tmp[i, j]) next = i;
                    res[i, j] = (i - fwd[i] <= cells) || (next - i <= cells);
                }
            }
            return res;
        }

        /// <summary>8-connected A*, euclidean heuristic, no corner cutting. null = no path. Cells are {row, col}; first = start, last = goal.</summary>
        public static List<int[]> AStar(bool[,] blocked, int[] start, int[] goal)
        {
            int rows = blocked.GetLength(0), cols = blocked.GetLength(1);
            int n = rows * cols;
            int s = start[0] * cols + start[1], t = goal[0] * cols + goal[1];
            var gc = new double[n];
            var from = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { gc[i] = double.MaxValue; from[i] = -1; }
            var heap = new MinHeap(n);
            gc[s] = 0;
            heap.Push(0, s);
            int[] di = { -1, -1, -1, 0, 0, 1, 1, 1 };
            int[] dj = { -1, 0, 1, -1, 1, -1, 0, 1 };
            while (heap.Count > 0)
            {
                int cur = heap.Pop();
                if (closed[cur]) continue;
                closed[cur] = true;
                if (cur == t) break;
                int ci = cur / cols, cj = cur % cols;
                for (int k = 0; k < 8; k++)
                {
                    int ni = ci + di[k], nj = cj + dj[k];
                    if (ni < 0 || nj < 0 || ni >= rows || nj >= cols) continue;
                    if (blocked[ni, nj]) continue;
                    bool diag = di[k] != 0 && dj[k] != 0;
                    if (diag && (blocked[ci + di[k], cj] || blocked[ci, cj + dj[k]])) continue;
                    int nb = ni * cols + nj;
                    if (closed[nb]) continue;
                    double ng = gc[cur] + (diag ? 1.4142135623730951 : 1.0);
                    if (ng < gc[nb])
                    {
                        gc[nb] = ng; from[nb] = cur;
                        double hi = ni - goal[0], hj = nj - goal[1];
                        heap.Push(ng + Math.Sqrt(hi * hi + hj * hj), nb);
                    }
                }
            }
            if (!closed[t]) return null;
            var path = new List<int[]>();
            for (int cur = t; cur != -1; cur = from[cur]) path.Add(new[] { cur / cols, cur % cols });
            path.Reverse();
            return path;
        }

        private sealed class MinHeap
        {
            private double[] _k; private int[] _v; private int _n;
            public MinHeap(int cap) { _k = new double[Math.Max(16, cap / 8)]; _v = new int[_k.Length]; }
            public int Count { get { return _n; } }
            public void Push(double key, int val)
            {
                if (_n == _k.Length) { Array.Resize(ref _k, _n * 2); Array.Resize(ref _v, _n * 2); }
                int i = _n++;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_k[p] <= key) break;
                    _k[i] = _k[p]; _v[i] = _v[p]; i = p;
                }
                _k[i] = key; _v[i] = val;
            }
            public int Pop()
            {
                int top = _v[0];
                _n--;
                if (_n > 0)
                {
                    double key = _k[_n]; int val = _v[_n];
                    int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1;
                        if (l >= _n) break;
                        int r = l + 1;
                        int m = (r < _n && _k[r] < _k[l]) ? r : l;
                        if (_k[m] >= key) break;
                        _k[i] = _k[m]; _v[i] = _v[m]; i = m;
                    }
                    _k[i] = key; _v[i] = val;
                }
                return top;
            }
        }

        private static readonly double[] BandOffsets = { 0, -1, 1, -0.5, 0.5 };

        /// <summary>Probe points of a segment: every StepNm along it, across the band +-ShoreMarginNm (5 lanes).</summary>
        internal static List<GeoPt> LinePattern(GeoPt a, GeoPt b, RouteParams p)
        {
            var res = new List<GeoPt>();
            double lk = AswMath.LonK(a.Lat, b.Lat);
            double dn = (b.Lat - a.Lat) * 60.0, de = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * lk;
            double len = Math.Sqrt(dn * dn + de * de);
            int steps = Math.Max(1, (int)Math.Ceiling(len / p.StepNm));
            double un = len > 1e-9 ? dn / len : 1, ue = len > 1e-9 ? de / len : 0;
            double pn = -ue, pe = un;                    // perpendicular
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                double bn = dn * t, be = de * t;
                for (int k = 0; k < BandOffsets.Length; k++)
                {
                    double o = BandOffsets[k] * p.ShoreMarginNm;
                    res.Add(new GeoPt(a.Lat + (bn + pn * o) / 60.0, a.Lon + (be + pe * o) / (60.0 * lk)));
                }
            }
            return res;
        }

        public static bool SegmentClear(GeoPt a, GeoPt b, Func<GeoPt, double> heightM, RouteParams p, out int probes)
        {
            probes = 0;
            foreach (GeoPt g in LinePattern(a, b, p))
            {
                probes++;
                if (!Passable(heightM(g), p.MinDepthM)) return false;
            }
            return true;
        }

        /// <summary>Greedy line-of-sight shortening; then drops the least useful corners until at most maxPts points remain (start and end kept).</summary>
        public static List<GeoPt> Smooth(List<GeoPt> path, Func<GeoPt, GeoPt, bool> clear, int maxPts)
        {
            int n = path.Count;
            if (n <= 2) return new List<GeoPt>(path);
            var res = new List<GeoPt>();
            res.Add(path[0]);
            int i = 0;
            while (i < n - 1)
            {
                int j = n - 1;
                while (j > i + 1 && !clear(path[i], path[j])) j--;
                res.Add(path[j]);
                i = j;
            }
            int limit = Math.Max(2, maxPts);
            while (res.Count > limit)
            {
                int best = -1; double bestCost = double.MaxValue; bool bestClear = false;
                for (int k = 1; k < res.Count - 1; k++)
                {
                    double cost = Nm(res[k - 1], res[k]) + Nm(res[k], res[k + 1]) - Nm(res[k - 1], res[k + 1]);
                    bool ok = clear(res[k - 1], res[k + 1]);
                    if ((ok && !bestClear) || (ok == bestClear && cost < bestCost))
                    { best = k; bestCost = cost; bestClear = ok; }
                }
                res.RemoveAt(best);
            }
            return res;
        }

        /// <summary>Test/helper: whole plan in one call (probes unbudgeted): direct -> A* -> frame x2 -> none.</summary>
        public static RouteResult PlanSync(GeoPt from, GeoPt to, Func<GeoPt, double> heightM, RouteParams p)
        {
            var job = new RouteJob(from, to, p);
            for (int guard = 0; guard < 8 && job.State != "done"; guard++)
            {
                job.Step(heightM, int.MaxValue);
                if (job.State == "ready_to_solve") job.Solve();
            }
            return job.Result;
        }

        private static double PointSegNm(GeoPt p, GeoPt a, GeoPt b)
        {
            double ax = 0, ay = 0;
            double k = AswMath.LonK(a.Lat, b.Lat);
            double bx = (b.Lat - a.Lat) * 60.0, by = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * k;
            double px = (p.Lat - a.Lat) * 60.0, py = GeoLon.DLon(a.Lon, p.Lon) * 60.0 * k;
            double l2 = bx * bx + by * by;
            double t = l2 < 1e-12 ? 0 : ((px - ax) * bx + (py - ay) * by) / l2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double dx = px - (ax + bx * t), dy = py - (ay + by * t);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>route = unit position at plan time followed by the waypoints. True: raft moved &gt; 0.3 nm, unit &gt; 1 nm off the route, or &gt; 120 s since the plan near shore.</summary>
        public static bool NeedReroute(GeoPt raftNow, GeoPt raftAtPlan, GeoPt unit, IList<GeoPt> route, bool nearShore, double sincePlanS)
        {
            if (Nm(raftNow, raftAtPlan) > 0.3) return true;
            if (route != null && route.Count >= 2)
            {
                double best = double.MaxValue;
                for (int i = 0; i + 1 < route.Count; i++) best = Math.Min(best, PointSegNm(unit, route[i], route[i + 1]));
                if (best > 1.0) return true;
            }
            return nearShore && sincePlanS > 120.0;
        }

        // ------------------------------------------------------------------ self test

        private sealed class Land
        {
            private readonly List<double[]> _rects = new List<double[]>();   // n0, e0, n1, e1
            private readonly List<double[]> _discs = new List<double[]>();   // n, e, r
            public Land Rect(double n0, double e0, double n1, double e1) { _rects.Add(new[] { n0, e0, n1, e1 }); return this; }
            public Land Disc(double n, double e, double r) { _discs.Add(new[] { n, e, r }); return this; }
            public Land Ring(double n, double e, double rIn, double rOut)
            {
                // ring built from small discs is awkward; use 4 thick rect walls
                Rect(n - rOut, e - rOut, n - rIn, e + rOut); Rect(n + rIn, e - rOut, n + rOut, e + rOut);
                Rect(n - rOut, e - rOut, n + rOut, e - rIn); Rect(n - rOut, e + rIn, n + rOut, e + rOut);
                return this;
            }
            public double Dist(double n, double e)
            {
                double best = 1e9;
                foreach (double[] r in _rects)
                {
                    double dn = Math.Max(Math.Max(r[0] - n, 0), n - r[2]), de = Math.Max(Math.Max(r[1] - e, 0), e - r[3]);
                    best = Math.Min(best, Math.Sqrt(dn * dn + de * de));
                }
                foreach (double[] d in _discs)
                    best = Math.Min(best, Math.Max(0, Math.Sqrt((n - d[0]) * (n - d[0]) + (e - d[1]) * (e - d[1])) - d[2]));
                return best;
            }
            public double Dist(GeoPt g) { return Dist((g.Lat - Lat0) * 60.0, (g.Lon - Lon0) * 60.0); }
            public Func<GeoPt, double> Height(int[] counter)
            {
                return g => { if (counter != null) counter[0]++; return Dist(g) <= 0 ? 12.0 : -500.0; };
            }
        }

        private const double Lat0 = 36.0, Lon0 = 20.0;
        private static GeoPt At(double n, double e) { return new GeoPt(Lat0 + n / 60.0, Lon0 + e / 60.0); }

        private static string CheckPath(string name, Land land, RouteResult r, GeoPt from, RouteParams p)
        {
            if (r.Points == null || r.Points.Count == 0) return name + ": no points";
            if (r.Points.Count > p.MaxPoints) return name + ": too many points " + r.Points.Count;
            if (r.Cells > p.MaxCells) return name + ": cells " + r.Cells;
            GeoPt prev = from;
            for (int i = 0; i < r.Points.Count; i++)
            {
                GeoPt q = r.Points[i];
                bool last = i == r.Points.Count - 1;
                if (!last && land.Dist(q) < p.ShoreMarginNm - 1e-6)
                    return name + ": point " + i + " too close to land " + land.Dist(q).ToString("0.000");
                int steps = Math.Max(1, (int)(Nm(prev, q) / 0.02));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    var m = new GeoPt(prev.Lat + (q.Lat - prev.Lat) * t, prev.Lon + (q.Lon - prev.Lon) * t);
                    if (land.Dist(m) <= 0) return name + ": segment " + i + " crosses land";
                    // away from the (possibly near-shore) endpoints the margin must hold along segments too
                    if (Nm(m, from) > 1.0 && Nm(m, r.Points[r.Points.Count - 1]) > 1.0 && land.Dist(m) < p.ShoreMarginNm - 0.35 * 0.5)
                        return name + ": segment " + i + " hugs the shore " + land.Dist(m).ToString("0.000");
                }
                prev = q;
            }
            return null;
        }

        /// <summary>T-348 stand `lat`: the grid step, the line pattern and the order rectangle in true nm on the beta globe at 0/45/60/70 deg; the flat world stays bit-identical.</summary>
        public static string SelfTestLat()
        {
            try
            {
                foreach (double lat in new[] { 0.0, 45.0, 60.0, 70.0 })
                {
                    var a = new GeoPt(lat, 10.0);
                    // globe: the grid step along longitude is C true nm
                    using (AswMath.WorldScope(true))
                    {
                        var b = AswMath.Offset(a, 90.0, 12.0);
                        RouteGrid g = MakeGrid(a, b, 1.0, 40000);
                        double step = AswMath.GreatCircleNm(g.Center(0, 0), g.Center(0, 1)) / g.C;
                        if (Math.Abs(step - 1.0) > 0.015) return "lat " + lat + ": grid lon step " + step.ToString("0.0000");
                        int ci, cj; g.Locate(g.Center(2, 3), out ci, out cj);
                        if (ci != 2 || cj != 3) return "lat " + lat + ": Locate round trip " + ci + "," + cj;
                        // line pattern across a diagonal: the two band edges (+-1 margin) are 2 * ShoreMarginNm apart
                        var p = new RouteParams();
                        var c = AswMath.Offset(a, 45.0, 20.0);
                        List<GeoPt> pat = LinePattern(a, c, p);
                        double band = AswMath.GreatCircleNm(pat[1], pat[2]) / (2 * p.ShoreMarginNm);
                        if (Math.Abs(band - 1.0) > 0.015) return "lat " + lat + ": band width " + band.ToString("0.0000");
                        double len = AswMath.GreatCircleNm(pat[0], pat[pat.Count - 5]) / 20.0;
                        if (Math.Abs(len - 1.0) > 0.015) return "lat " + lat + ": line length " + len.ToString("0.0000");
                        if (Math.Abs(Nm(a, c) - 20.0) > 1e-9) return "lat " + lat + ": Nm " + Nm(a, c);
                        // PointSegNm: a point 2 true nm east of a meridian segment
                        var s1 = new GeoPt(lat, 10.0); var s2 = new GeoPt(lat + 0.2, 10.0);
                        var q = AswMath.Offset(new GeoPt(lat + 0.1, 10.0), 90.0, 2.0);
                        if (Math.Abs(PointSegNm(q, s1, s2) - 2.0) > 0.03) return "lat " + lat + ": PointSegNm " + PointSegNm(q, s1, s2);
                    }
                    // T-356 R4: an 80 nm grid keeps true miles on every row (K per row); the corners against the great circle, east part within 1.5 %
                    using (AswMath.WorldScope(true))
                    {
                        RouteGrid g = MakeGrid(a, AswMath.Offset(a, 90.0, 80.0), 1.0, 40000);
                        foreach (int ci in new[] { 0, g.Rows - 1 })
                            foreach (int cj in new[] { 0, g.Cols - 1 })
                            {
                                GeoPt corner = g.Center(ci, cj), onMeridian = new GeoPt(corner.Lat, a.Lon);
                                double want = Math.Abs((g.ColMin + cj) * g.C), got = AswMath.GreatCircleNm(onMeridian, corner);
                                if (Math.Abs(got - want) > 0.015 * want) return "lat " + lat + ": corner " + ci + "," + cj + " east " + got.ToString("0.00") + " vs " + want.ToString("0.00");
                            }
                    }
                    // T-356 R3: the grid across the antimeridian = the grid of the copy shifted by -10 deg
                    foreach (bool globe in new[] { false, true })
                        using (AswMath.WorldScope(globe))
                        {
                            var wa = new GeoPt(lat, 179.8); var wb = new GeoPt(lat + 0.05, -179.89);
                            var sa = new GeoPt(lat, 169.8); var sb = new GeoPt(lat + 0.05, 170.11);
                            RouteGrid gw = MakeGrid(wa, wb, 1.0, 40000), gs = MakeGrid(sa, sb, 1.0, 40000);
                            if (gw.Rows != gs.Rows || gw.Cols != gs.Cols || gw.ColMin != gs.ColMin || gw.C != gs.C) return "lat " + lat + ": antimeridian grid shape " + globe;
                            int wi, wj, si, sj; gw.Locate(wb, out wi, out wj); gs.Locate(sb, out si, out sj);
                            if (wi != si || wj != sj) return "lat " + lat + ": antimeridian Locate " + globe;
                            GeoPt cw = gw.Center(gw.Rows / 2, gw.Cols - 1), cs = gs.Center(gs.Rows / 2, gs.Cols - 1);
                            if (Math.Abs(GeoLon.DLon(cs.Lon + 10.0, cw.Lon)) > 1e-9) return "lat " + lat + ": antimeridian Center " + globe;
                            if (Math.Abs(Nm(wa, wb) - Nm(sa, sb)) > 1e-9) return "lat " + lat + ": antimeridian Nm " + globe;
                            RouteResult rw = PlanSync(wa, wb, h => -500.0, new RouteParams()), rs = PlanSync(sa, sb, h => -500.0, new RouteParams());
                            if (rw.Kind != "direct" || rs.Kind != "direct" || rw.Probes != rs.Probes) return "lat " + lat + ": antimeridian route " + rw.Kind + "/" + rs.Kind + " " + rw.Probes + "/" + rs.Probes;
                        }
                    // flat: bit-identical to the old formulas
                    using (AswMath.WorldScope(false))
                    {
                        var b = new GeoPt(lat + 0.1, 10.3);
                        RouteGrid g = MakeGrid(a, b, 1.0, 40000);
                        if (g.LonK != 1.0) return "lat " + lat + ": flat grid K";
                        for (int i = 0; i < g.Rows; i += 5)
                        {
                            if (g.RowK[i] != 1.0) return "lat " + lat + ": flat RowK";
                            for (int j = 0; j < g.Cols; j += 7)
                                if (g.Lon(i, j) != g.Origin.Lon + (g.ColMin + j) * g.C / 60.0) return "lat " + lat + ": flat Lon(i,j)";
                        }
                        double[] ff = Frame(a, b, 1.0);
                        if (ff[1] != g.Lon(0, 0) - g.C / (120.0 * g.LonK) || ff[3] != g.Lon(0, g.Cols - 1) + g.C / (120.0 * g.LonK)) return "lat " + lat + ": flat Frame";
                        int li, lj; g.Locate(b, out li, out lj);
                        if (lj != (int)Math.Round((b.Lon - a.Lon) * 60.0 * 1.0 / g.C) - g.ColMin) return "lat " + lat + ": flat Locate";
                        double dn = (b.Lat - a.Lat) * 60.0, de = (b.Lon - a.Lon) * 60.0;
                        if (Nm(a, b) != Math.Sqrt(dn * dn + de * de)) return "lat " + lat + ": flat Nm";
                        double bx = (b.Lat - a.Lat) * 60.0, by = (b.Lon - a.Lon) * 60.0;
                        var q = new GeoPt(lat + 0.05, 10.2);
                        double px = (q.Lat - a.Lat) * 60.0, py = (q.Lon - a.Lon) * 60.0;
                        double l2 = bx * bx + by * by, t = ((px) * bx + (py) * by) / l2;
                        t = t < 0 ? 0 : (t > 1 ? 1 : t);
                        double ex = px - bx * t, ey = py - by * t;
                        if (PointSegNm(q, a, b) != Math.Sqrt(ex * ex + ey * ey)) return "lat " + lat + ": flat PointSegNm";
                    }
                }
                return null;
            }
            catch (Exception ex) { return "lat: " + ex; }
        }

        public static string SelfTest()
        {
            try
            {
                var p = new RouteParams();
                // --- B1 basics
                if (Math.Abs(CellNm(6) - 0.1) > 1e-9 || Math.Abs(CellNm(30) - 0.25) > 1e-9 || Math.Abs(CellNm(120) - 0.5) > 1e-9) return "basics: CellNm";
                if (CellNm(1) != 0.1 || CellNm(900) != 0.5) return "basics: CellNm clamp";
                if (!Passable(-8, 8) || Passable(-7.9, 8) || Passable(0, 8)) return "basics: Passable";
                double[] f = Frame(At(0, 0), At(0, 6), 1.0);
                if ((f[2] - f[0]) * 60.0 < 10.0 - 1e-6 || (f[3] - f[1]) * 60.0 < 16.0 - 1e-6) return "basics: Frame margin " + (f[2] - f[0]) * 60 + "x" + (f[3] - f[1]) * 60;
                if (f[0] > Lat0 - 5.0 / 60.0 + 1e-9 || f[1] > Lon0 - 5.0 / 60.0 + 1e-9) return "basics: Frame must reach 5 nm";
                int cells;
                double cc = FrameCellNm(At(0, 0), At(0, 200), 2.0, 40000, out cells);
                if (cells > 40000 || cc <= CellNm(200)) return "basics: Frame cell budget " + cells + " c=" + cc;
                FrameCellNm(At(0, 0), At(0, 6), 1.0, 40000, out cells);
                if (cells > 40000) return "basics: Frame small cells";
                var one = new bool[7, 7]; one[3, 3] = true;
                bool[,] inf = Inflate(one, 1);
                for (int i = 0; i < 7; i++)
                    for (int j = 0; j < 7; j++)
                        if (inf[i, j] != (Math.Abs(i - 3) <= 1 && Math.Abs(j - 3) <= 1)) return "basics: Inflate 1 at " + i + "," + j;
                var corner = new bool[4, 4]; corner[0, 0] = true;
                if (!Inflate(corner, 2)[2, 2] || Inflate(corner, 2)[3, 3]) return "basics: Inflate corner";
                // A* basics: wall with a gap
                var grid = new bool[10, 10];
                for (int i = 0; i < 10; i++) grid[i, 5] = true;
                grid[8, 5] = false;
                List<int[]> ap = AStar(grid, new[] { 1, 1 }, new[] { 1, 9 });
                if (ap == null || ap.Count < 8) return "astar: gap path";
                bool through = false;
                foreach (int[] c in ap) if (c[1] == 5 && c[0] == 8) through = true;
                if (!through) return "astar: must pass the gap";
                for (int i = 1; i < ap.Count; i++)
                    if (Math.Abs(ap[i][0] - ap[i - 1][0]) > 1 || Math.Abs(ap[i][1] - ap[i - 1][1]) > 1) return "astar: not 8-connected";
                grid[8, 5] = true;
                if (AStar(grid, new[] { 1, 1 }, new[] { 1, 9 }) != null) return "astar: closed wall must be null";

                // --- B2 direct
                var sea = new Land();
                RouteResult rd = PlanSync(At(0, 0), At(0, 20), sea.Height(null), p);
                if (rd.Kind != "direct" || rd.Points.Count != 1) return "direct: open sea " + rd.Kind;
                int pr;
                if (!SegmentClear(At(0, 0), At(0, 20), sea.Height(null), p, out pr) || pr < 200) return "direct: SegmentClear probes " + pr;
                var edge03 = new Land().Rect(0.2, 8, 3, 10);        // land edge 0.2 nm off the line
                if (PlanSync(At(0, 0), At(0, 20), edge03.Height(null), p).Kind == "direct") return "direct: land in the 0.3 band must not be direct";
                var edge06 = new Land().Rect(0.6, 8, 3, 10);
                if (PlanSync(At(0, 0), At(0, 20), edge06.Height(null), p).Kind != "direct") return "direct: land 0.6 nm away is fine";
                var onLine = new Land().Rect(-1, 8, 1, 10);
                if (SegmentClear(At(0, 0), At(0, 20), onLine.Height(null), p, out pr)) return "direct: SegmentClear through land";

                // --- B3 maps
                string err;
                var strait = new Land().Rect(-60, 18, 10, 22).Rect(12, 18, 60, 22);   // gap n in [10, 12]
                RouteResult rs = PlanSync(At(0, 0), At(0, 40), strait.Height(null), p);
                if (rs.Kind != "astar") return "strait: " + rs.Kind + " " + rs.Why;
                if ((err = CheckPath("strait", strait, rs, At(0, 0), p)) != null) return err;
                bool gap = false;
                for (int i = 0; i + 1 < rs.Points.Count + 1; i++)
                {
                    GeoPt a = i == 0 ? At(0, 0) : rs.Points[i - 1];
                    GeoPt b = i < rs.Points.Count ? rs.Points[i] : At(0, 40);
                    if (i < rs.Points.Count && a.Lon <= Lon0 + 20.0 / 60.0 && b.Lon >= Lon0 + 20.0 / 60.0)
                    {
                        double t = (Lon0 + 20.0 / 60.0 - a.Lon) / (b.Lon - a.Lon);
                        double nAt = ((a.Lat + (b.Lat - a.Lat) * t) - Lat0) * 60.0;
                        if (nAt > 10 && nAt < 12) gap = true;
                    }
                }
                if (!gap) return "strait: path does not cross at the gap";

                var cape = new Land().Rect(-2, 8, 12, 10);
                RouteResult rc = PlanSync(At(0, 0), At(0, 20), cape.Height(null), p);
                if (rc.Kind != "astar") return "cape: " + rc.Kind + " " + rc.Why;
                if ((err = CheckPath("cape", cape, rc, At(0, 0), p)) != null) return err;

                var island = new Land().Disc(0, 10, 3);
                RouteResult ri = PlanSync(At(0, 0), At(0, 20), island.Height(null), p);
                if (ri.Kind != "astar") return "island: " + ri.Kind + " " + ri.Why;
                if ((err = CheckPath("island", island, ri, At(0, 0), p)) != null) return err;
                if (ri.Points.Count > 4) return "island: expected a short path, got " + ri.Points.Count;

                var bay = new Land().Ring(0, 20, 2.5, 4);
                RouteResult rb = PlanSync(At(0, 0), At(0, 20), bay.Height(null), p);
                if (rb.Kind != "none" || rb.Why != "no_path") return "bay: " + rb.Kind + " " + rb.Why;

                var onLand = new Land().Disc(0, 20, 2);
                RouteResult rl = PlanSync(At(0, 0), At(0, 20), onLand.Height(null), p);
                if (rl.Kind != "none" || rl.Why != "land") return "raft on land: " + rl.Kind + " " + rl.Why;

                // raft close to the shore (inside the margin): still reachable, flagged near shore
                var shore = new Land().Rect(-30, 20.3, 30, 40);
                RouteResult rn = PlanSync(At(0, 0), At(0, 20), shore.Height(null), p);
                if (rn.Kind == "none") return "near shore raft: " + rn.Why;
                if (rn.Kind == "astar" && !rn.NearShore) return "near shore raft: NearShore flag";

                // long haul: cell budget
                var wide = new Land().Disc(0, 100, 15);
                RouteResult rw = PlanSync(At(0, 0), At(0, 200), wide.Height(null), p);
                if (rw.Kind != "astar" || rw.Cells > p.MaxCells || rw.Cells < 1000) return "wide: " + rw.Kind + " cells=" + rw.Cells;
                if ((err = CheckPath("wide", wide, rw, At(0, 0), p)) != null) return err;

                // many-corner coast: points limit
                var comb = new Land();
                for (int k = 0; k < 20; k++) comb.Rect(k % 2 == 0 ? -30 : -6, 5 + k * 4, k % 2 == 0 ? 6 : 30, 6.5 + k * 4);
                RouteResult rcomb = PlanSync(At(0, 0), At(0, 90), comb.Height(null), p);
                if (rcomb.Kind == "astar" && rcomb.Points.Count > p.MaxPoints) return "comb: points " + rcomb.Points.Count;

                // --- B4 RouteJob
                var counter = new int[1];
                var job = new RouteJob(At(0, 0), At(0, 20), p);
                Func<GeoPt, double> hf = island.Height(counter);
                int calls = 0, sum = 0;
                while (job.State == "probe_line" || job.State == "probe_grid")
                {
                    int used = job.Step(hf, 100);
                    sum += used; calls++;
                    if (used > 100) return "job: budget exceeded";
                    if (calls > 100000) return "job: no progress";
                }
                if (job.State != "ready_to_solve") return "job: state " + job.State;
                if (sum != counter[0]) return "job: probe count " + sum + " vs " + counter[0];
                if (calls != (sum + 99) / 100) return "job: calls " + calls + " for " + sum + " probes";
                if (job.Step(hf, 100) != 0) return "job: Step after ready must be a no-op";
                RouteResult viaTask = null;
                var th = new System.Threading.Thread(() => job.Solve());
                th.Start(); th.Join();
                viaTask = job.Result;
                RouteResult sync = PlanSync(At(0, 0), At(0, 20), island.Height(null), p);
                if (job.State != "done" || viaTask == null || viaTask.Kind != "astar" || viaTask.Points.Count != sync.Points.Count) return "job: thread result differs";
                for (int i = 0; i < sync.Points.Count; i++)
                    if (Math.Abs(sync.Points[i].Lat - viaTask.Points[i].Lat) > 1e-12 || Math.Abs(sync.Points[i].Lon - viaTask.Points[i].Lon) > 1e-12) return "job: thread points differ";
                // failure -> frame x2 -> probe_grid with cached cells
                var c2 = new int[1];
                var job2 = new RouteJob(At(0, 0), At(0, 20), p);
                Func<GeoPt, double> hb = bay.Height(c2);
                int guard = 0;
                while (job2.State != "ready_to_solve" && guard++ < 100000) job2.Step(hb, 500);
                int first = c2[0];
                job2.Solve();
                if (job2.State != "probe_grid" || job2.FrameFactor != 2) return "job: after failure expected probe_grid x2, got " + job2.State;
                guard = 0;
                while (job2.State == "probe_grid" && guard++ < 100000) job2.Step(hb, 500);
                int second = c2[0] - first;
                RouteGrid g2 = MakeGrid(At(0, 0), At(0, 20), 2, p.MaxCells);
                if (second >= g2.Cells) return "job: cache not used, second pass " + second + " of " + g2.Cells;
                job2.Solve();
                if (job2.State != "done" || job2.Result.Kind != "none" || job2.Result.Why != "no_path") return "job: bay final " + job2.State;
                // repeated probe of the same cell is served from the cache
                var c3 = new int[1];
                var job3 = new RouteJob(At(0, 0), At(0, 20), p);
                job3.Step(sea.Height(c3), 3);
                if (job3.Probes != c3[0]) return "job: Probes property";

                // --- B5 NeedReroute
                GeoPt r0 = At(0, 20);
                var route = new List<GeoPt> { At(0, 0), At(0, 10), r0 };
                if (!NeedReroute(At(0.31, 20), r0, At(0, 5), route, false, 10)) return "reroute: raft moved 0.31";
                if (NeedReroute(At(0.29, 20), r0, At(0, 5), route, false, 10)) return "reroute: raft moved 0.29";
                if (!NeedReroute(r0, r0, At(1.1, 5), route, false, 10)) return "reroute: unit 1.1 off";
                if (NeedReroute(r0, r0, At(0.9, 5), route, false, 10)) return "reroute: unit 0.9 off";
                if (!NeedReroute(r0, r0, At(0, 5), route, true, 121)) return "reroute: near shore 121 s";
                if (NeedReroute(r0, r0, At(0, 5), route, true, 119)) return "reroute: near shore 119 s";
                if (NeedReroute(r0, r0, At(0, 5), route, false, 500)) return "reroute: open sea 500 s";
                if (NeedReroute(r0, r0, At(5, 5), new List<GeoPt> { r0 }, false, 500)) return "reroute: single point has no corridor";
                return null;
            }
            catch (Exception e)
            {
                return "exception: " + e;
            }
        }
    }
}
