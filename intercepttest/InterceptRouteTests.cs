using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Tests of TerrainHeightCache and InterceptRoute on synthetic land (shapes copied from the SarRouteMath selftest).</summary>
    internal static class InterceptRouteTests
    {
        private const double Lat0 = 36.0, Lon0 = 20.0;
        private static GeoPt At(double n, double e) { return new GeoPt(Lat0 + n / 60.0, Lon0 + e / 60.0); }

        private sealed class Land
        {
            private readonly List<double[]> _rects = new List<double[]>(), _discs = new List<double[]>();
            public Land Rect(double n0, double e0, double n1, double e1) { _rects.Add(new[] { n0, e0, n1, e1 }); return this; }
            public Land Disc(double n, double e, double r) { _discs.Add(new[] { n, e, r }); return this; }
            public Land Ring(double n, double e, double rIn, double rOut)
            {
                Rect(n - rOut, e - rOut, n - rIn, e + rOut); Rect(n + rIn, e - rOut, n + rOut, e + rOut);
                Rect(n - rOut, e - rOut, n + rOut, e - rIn); Rect(n - rOut, e + rIn, n + rOut, e + rOut);
                return this;
            }
            public bool IsLand(double n, double e)
            {
                foreach (double[] r in _rects) if (n >= r[0] && n <= r[2] && e >= r[1] && e <= r[3]) return true;
                foreach (double[] d in _discs) if ((n - d[0]) * (n - d[0]) + (e - d[1]) * (e - d[1]) <= d[2] * d[2]) return true;
                return false;
            }
            public double Height(GeoPt g) { return IsLand((g.Lat - Lat0) * 60.0, (g.Lon - Lon0) * 60.0) ? 12.0 : -500.0; }
        }

        private static InterceptRoute Make(Func<GeoPt, double> h, out List<string> bgLog)
        {
            var log = new List<string>();
            bgLog = log;
            return new InterceptRoute(h, a => { log.Add("bg"); a(); });   // the stand runs Solve synchronously
        }

        /// <summary>Pumps frames until the uid has a result (max 400 frames).</summary>
        private static RouteResult Wait(InterceptRoute r, int uid, out int frames)
        {
            frames = 0;
            while (frames < 400)
            {
                r.FramePump(); frames++;
                RouteResult res = r.Poll(uid);
                if (res != null) return res;
            }
            return null;
        }

        public static string Cache()
        {
            int calls = 0;
            var c = new TerrainHeightCache((la, lo) => { calls++; return -100; });
            if (c.HeightM(At(0, 0)) != -100 || calls != 1 || c.Count != 1) return "first call";
            c.HeightM(At(0.01, 0.01));                         // same 0.05 nm cell
            if (calls != 1) return "same cell must be cached, calls=" + calls;
            c.HeightM(At(0.2, 0));                             // another cell
            if (calls != 2 || c.Count != 2) return "other cell, calls=" + calls;
            c.Clear();
            if (c.Count != 0) return "Clear";
            c.HeightM(At(0, 0));
            if (calls != 3) return "after Clear the sampler is asked again";
            if (c.Misses != calls) return "T-278 Misses must count the sampler asks, got " + c.Misses + " vs " + calls;
            var nan = new TerrainHeightCache((la, lo) => double.NaN);
            if (nan.HeightM(At(1, 1)) != -200) return "NaN -> -200";
            if (nan.Count != 0) return "NaN must not be cached";
            var none = new TerrainHeightCache(null);
            if (none.HeightM(At(1, 1)) != -200) return "null sampler -> -200";
            var inf = new TerrainHeightCache((la, lo) => double.PositiveInfinity);
            if (inf.HeightM(At(1, 1)) != -200) return "infinity -> -200";
            // the cache is bounded
            var big = new TerrainHeightCache((la, lo) => -10);
            for (int i = 0; i < TerrainHeightCache.MaxEntries + 50; i++) big.HeightM(new GeoPt(Lat0, Lon0 + i * 0.01));
            if (big.Count > TerrainHeightCache.MaxEntries) return "cache grew past MaxEntries: " + big.Count;
            return null;
        }

        /// <summary>T-275: no data is not cached; asked again after the retry interval; one rate-limited log line.</summary>
        public static string CacheNoData()
        {
            double now = 100; int calls = 0; double val = double.NaN; var lines = new List<string>();
            var c = new TerrainHeightCache((la, lo) => { calls++; return val; }, () => now, lines.Add);
            if (c.HeightM(At(0, 0)) != -200 || calls != 1) return "no data -> sea, calls=" + calls;
            if (c.Count != 0 || c.NoDataCount != 1) return "no data cached: " + c.Count + "/" + c.NoDataCount;
            c.HeightM(At(0, 0)); now += 1;
            c.HeightM(At(0, 0));
            if (calls != 1) return "retry before the interval, calls=" + calls;
            now += TerrainHeightCache.RetryS;
            val = 250;                                              // the chunk has loaded
            if (c.HeightM(At(0, 0)) != 250 || calls != 2) return "retry after the interval must read the land, calls=" + calls;
            if (c.NoDataCount != 0 || c.Count != 1) return "loaded cell must be cached";
            c.HeightM(At(0, 0));
            if (calls != 2) return "loaded cell is cached for good";
            // log: one line, then silence for LogEveryS
            val = double.NaN; lines.Clear(); now = 1000;
            var d = new TerrainHeightCache((la, lo) => double.NaN, () => now, lines.Add);
            for (int i = 0; i < 10; i++) { d.HeightM(At(i, 0)); now += 1; }
            if (lines.Count != 1 || !lines[0].StartsWith("terrain no data")) return "log lines in 10 s: " + lines.Count;
            now += TerrainHeightCache.LogEveryS;
            d.HeightM(At(50, 0));
            if (lines.Count != 2) return "second line after the interval: " + lines.Count;
            if (new TerrainHeightCache(null, () => now, lines.Add).HeightM(At(1, 1)) != -200) return "null sampler";
            return null;
        }

        public static string Queue()
        {
            var land = new Land().Disc(0, 10, 3);
            List<string> bg;
            var r = Make(land.Height, out bg);
            var p = new RouteParams();
            r.Request(1, At(0, 0), At(0, 20), p);
            r.Request(2, At(0, 0), At(0, 20), p);
            r.Request(3, At(0, 0), At(0, 20), p);
            if (r.ActiveJobs != 2 || r.WaitingJobs != 1) return "2 active + 1 waiting expected, got " + r.ActiveJobs + "+" + r.WaitingJobs;
            if (!r.Busy(1) || !r.Busy(2) || !r.Busy(3)) return "Busy";
            if (r.Busy(4)) return "Busy of a stranger";
            var done = new HashSet<int>();
            int maxActive = 0;
            for (int f = 0; f < 600 && done.Count < 3; f++)
            {
                r.FramePump();
                maxActive = Math.Max(maxActive, r.ActiveJobs);
                if (r.ActiveJobs > InterceptRoute.MaxJobs) return "more than MaxJobs active: " + r.ActiveJobs;
                for (int u = 1; u <= 3; u++)
                {
                    RouteResult res = r.Poll(u);
                    if (res == null) continue;
                    if (res.Kind != "astar") return "uid " + u + " kind " + res.Kind;
                    if (r.Poll(u) != null) return "a result must come once";
                    done.Add(u);
                }
            }
            if (done.Count != 3) return "not all jobs finished: " + done.Count;
            if (maxActive != 2) return "two jobs must run together, max=" + maxActive;
            if (r.Busy(1) || r.Busy(2) || r.Busy(3)) return "Busy after the results";
            if (bg.Count != 3) return "Solve must go to the background once per job, got " + bg.Count;
            return null;
        }

        public static string Budget()
        {
            int probes = 0;
            var land = new Land().Disc(0, 10, 3);
            var r = new InterceptRoute(g => { probes++; return land.Height(g); }, a => a());
            r.ProbeBudget = 120;
            r.Request(1, At(0, 0), At(0, 20), new RouteParams());
            r.Request(2, At(0, 0), At(0, 20), new RouteParams());
            int frames = 0, total = 0;
            while (frames < 600 && (r.Busy(1) || r.Busy(2)))
            {
                int before = probes;
                int used = r.FramePump(); frames++;
                if (probes - before > r.ProbeBudget) return "frame " + frames + " used " + (probes - before) + " probes > budget";
                if (used != probes - before) return "FramePump reported " + used + " but probes=" + (probes - before);
                total += used;
                r.Poll(1); r.Poll(2);
            }
            if (r.Busy(1) || r.Busy(2)) return "jobs never finished";
            if (frames < 3) return "budget 120 must spread the work over several frames, frames=" + frames;
            r.ProbeBudget = 0;
            r.Request(3, At(0, 0), At(0, 20), new RouteParams());
            if (r.FramePump() != 0) return "budget 0 must probe nothing";
            return null;
        }

        public static string ReplaceCancel()
        {
            var land = new Land().Disc(0, 10, 3);
            List<string> bg;
            var r = Make(land.Height, out bg);
            r.Request(1, At(0, 0), At(0, 20), new RouteParams());
            r.FramePump();
            r.Request(1, At(0, 0), At(5, 20), new RouteParams());          // replaces the pending job of the uid
            if (r.ActiveJobs + r.WaitingJobs != 1) return "replace must keep one job of the uid, got " + (r.ActiveJobs + r.WaitingJobs);
            RouteResult res;
            int frames;
            res = Wait(r, 1, out frames);
            if (res == null || res.Kind == "none") return "replaced job result " + (res == null ? "null" : res.Kind + " " + res.Why);
            // the end point is the new one
            GeoPt last = res.Points[res.Points.Count - 1];
            if (Math.Abs(last.Lat - At(5, 20).Lat) > 1e-9) return "the result belongs to the old request";
            r.Request(2, At(0, 0), At(0, 20), new RouteParams());
            r.Request(3, At(0, 0), At(0, 20), new RouteParams());
            r.Request(4, At(0, 0), At(0, 20), new RouteParams());
            r.Cancel(2);
            if (r.Busy(2)) return "Cancel";
            if (r.ActiveJobs != 2 || r.WaitingJobs != 0) return "the waiting job must take the freed slot, got " + r.ActiveJobs + "+" + r.WaitingJobs;
            r.Clear();
            if (r.Busy(3) || r.Busy(4) || r.ActiveJobs != 0) return "Clear";
            if (r.Poll(3) != null) return "Poll after Clear";
            r.Cancel(99);                                                   // unknown uid: no crash
            return null;
        }

        public static string LegClear()
        {
            var p = new RouteParams();
            var sea = new Land();
            List<string> bg;
            var r = Make(sea.Height, out bg);
            if (!r.LegClear(At(0, 0), At(0, 20), p)) return "open sea must be clear";
            var island = new Land().Disc(0, 10, 3);
            r = Make(island.Height, out bg);
            if (r.LegClear(At(0, 0), At(0, 20), p)) return "through an island";
            var edge = new Land().Rect(0.2, 8, 3, 10);                      // 0.2 nm off the line: inside the shore margin
            r = Make(edge.Height, out bg);
            if (r.LegClear(At(0, 0), At(0, 20), p)) return "shore margin";
            return null;
        }

        public static string LineHitsLand()
        {
            var p = new RouteParams();
            int probes = 0;
            var island = new Land().Disc(0, 10, 3);
            var r = new InterceptRoute(g => { probes++; return island.Height(g); }, a => a());
            if (!r.LineHitsLand(At(0, 0), At(0, 20), p)) return "island on the line not seen";
            if (r.LineHitsLand(At(10, 0), At(10, 20), p)) return "a clear line reported as land";
            probes = 0;
            var sea = new Land();
            var r2 = new InterceptRoute(g => { probes++; return sea.Height(g); }, a => a());
            r2.LineHitsLand(At(0, 0), At(0, 20), p);
            if (probes < 19 || probes > 25) return "step 1 nm over 20 nm: probes " + probes;
            probes = 0;
            r2.LineHitsLand(At(0, 0), At(0, 400), p);
            if (probes > InterceptMath.MaxProbes) return "probes " + probes + " > " + InterceptMath.MaxProbes;
            // the tiny land strip between two sample points is allowed to be missed, but a wall is not
            var wall = new Land().Rect(-5, 9, 5, 10.5);
            var r3 = new InterceptRoute(g => wall.Height(g), a => a());
            if (!r3.LineHitsLand(At(0, 0), At(0, 20), p)) return "wall";
            return null;
        }

        public static string Island()
        {
            var land = new Land().Disc(0, 10, 3);
            List<string> bg;
            var r = Make(land.Height, out bg);
            r.Request(7, At(0, 0), At(0, 20), new RouteParams());
            int frames;
            RouteResult res = Wait(r, 7, out frames);
            if (res == null) return "no result";
            if (res.Kind != "astar" || res.Points.Count < 1 || res.Points.Count > 12) return "island: " + res.Kind + " pts=" + (res.Points == null ? 0 : res.Points.Count);
            // an open-sea request is direct
            r.Request(8, At(0, 0), At(10, 20), new RouteParams());
            res = Wait(r, 8, out frames);
            if (res == null || res.Kind != "direct" || res.Points.Count != 1) return "open sea: " + (res == null ? "null" : res.Kind);
            return null;
        }

        public static string Bay()
        {
            var land = new Land().Ring(0, 20, 2.5, 4);
            List<string> bg;
            var r = Make(land.Height, out bg);
            r.Request(1, At(0, 0), At(0, 20), new RouteParams());
            int frames;
            RouteResult res = Wait(r, 1, out frames);
            if (res == null || res.Kind != "none" || res.Why != "no_path") return "bay: " + (res == null ? "null" : res.Kind + " " + res.Why);
            var onLand = new Land().Disc(0, 20, 2);
            r = Make(onLand.Height, out bg);
            r.Request(2, At(0, 0), At(0, 20), new RouteParams());
            res = Wait(r, 2, out frames);
            if (res == null || res.Kind != "none" || res.Why != "land") return "target on land: " + (res == null ? "null" : res.Kind + " " + res.Why);
            return null;
        }
    }
}
