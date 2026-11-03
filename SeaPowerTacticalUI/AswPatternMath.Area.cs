using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 PA / ADR-0076 s.2: area lattice from the centre, rings, route, sectors, dip and MAD of an area. Frame: X north, Y east, centre (0,0).
    internal static partial class AswPatternMath
    {
        private const double AxisTol = 0.01;

        /// <summary>Lattice nodes along one side S: 1 + ceil((S - 1.6R)/1.6R); S &lt;= 1.6R -> 1.</summary>
        public static int AxisCount(double sideNm, double r)
        {
            double d = 1.6 * r;
            if (d <= 0 || sideNm <= d + 1e-9) return 1;
            return 1 + (int)Math.Ceiling((sideNm - d) / d - AxisTol);   // up to 1 % wider spacing is taken instead of a whole extra row (R_eff = 2.6998 vs 2.70 must not add a row)
        }

        /// <summary>Square lattice (step &lt;= 1.6R) of a north x east rectangle; edge nodes sit S/2 - 0.8R from the centre. Row-major (i north, j east).</summary>
        public static List<LatNode> Lattice(double northNm, double eastNm, double r, out int nx, out int ny, out double stepN, out double stepE)
        {
            nx = AxisCount(northNm, r); ny = AxisCount(eastNm, r);
            stepN = nx > 1 ? (northNm - 1.6 * r) / (nx - 1) : 1.6 * r;
            stepE = ny > 1 ? (eastNm - 1.6 * r) / (ny - 1) : 1.6 * r;
            double ic = (nx - 1) / 2.0, jc = (ny - 1) / 2.0;
            var res = new List<LatNode>(nx * ny);
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    double di = i - ic, dj = j - jc;
                    var n = new LatNode { I = i, J = j };
                    n.P = new NmPt(di * stepN, dj * stepE);
                    // half-integer offsets (even n) belong to ring 0 (2-4 centre nodes), integers to their own ring
                    n.Ring = Math.Max(0, (int)Math.Ceiling(Math.Max(Math.Abs(di), Math.Abs(dj)) - 0.5 - 1e-9));
                    n.AngDeg = (Math.Abs(n.P.X) < 1e-9 && Math.Abs(n.P.Y) < 1e-9) ? 0 : AswMath.Norm360(Math.Atan2(n.P.Y, n.P.X) * 180.0 / Math.PI);
                    res.Add(n);
                }
            return res;
        }

        public static double PathNm(NmPt from, IList<NmPt> pts)
        {
            double len = 0; NmPt cur = from;
            if (pts == null) return 0;
            foreach (var p in pts) { len += Dist(cur, p); cur = p; }
            return len;
        }

        private static double Dist(NmPt a, NmPt b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

        /// <summary>
        /// Route over the subset (node indices): ring 0 first (entry at the node nearest to <paramref name="from"/>), rings outward.
        /// Inside a ring: by angle in one direction from the node nearest to the previous point, then 2-opt (start fixed). Deterministic.
        /// </summary>
        public static List<int> RingRoute(IList<LatNode> nodes, IList<int> subset, NmPt from)
        {
            var res = new List<int>();
            if (nodes == null || subset == null || subset.Count == 0) return res;
            var rings = new SortedDictionary<int, List<int>>();
            foreach (int idx in subset)
            {
                List<int> l;
                if (!rings.TryGetValue(nodes[idx].Ring, out l)) { l = new List<int>(); rings[nodes[idx].Ring] = l; }
                l.Add(idx);
            }
            NmPt cur = from;
            foreach (var kv in rings)
            {
                var ring = kv.Value;
                int start = ring[0]; double bd = double.MaxValue;
                foreach (int idx in ring)
                {
                    double d = Dist(cur, nodes[idx].P);
                    if (d < bd - 1e-9 || (Math.Abs(d - bd) <= 1e-9 && idx < start)) { bd = d; start = idx; }
                }
                double a0 = nodes[start].AngDeg;
                var ordered = new List<int>(ring);
                ordered.Sort((x, y) =>
                {
                    double ax = AswMath.Norm360(nodes[x].AngDeg - a0), ay = AswMath.Norm360(nodes[y].AngDeg - a0);
                    int c = ax.CompareTo(ay);
                    if (c != 0) return c;
                    c = Dist(nodes[x].P, new NmPt(0, 0)).CompareTo(Dist(nodes[y].P, new NmPt(0, 0)));
                    return c != 0 ? c : x.CompareTo(y);
                });
                var pts = new List<NmPt>();
                foreach (int idx in ordered) pts.Add(nodes[idx].P);
                var perm = TwoOpt(cur, pts);
                foreach (int k in perm) res.Add(ordered[k]);
                cur = nodes[res[res.Count - 1]].P;
            }
            return res;
        }

        /// <summary>2-opt of an open path that starts at the fixed point <paramref name="start"/>; returns the new order as indices of <paramref name="pts"/>.</summary>
        internal static List<int> TwoOpt(NmPt start, IList<NmPt> pts)
        {
            int n = pts.Count;
            var ord = new List<int>(n);
            for (int i = 0; i < n; i++) ord.Add(i);
            if (n < 3) return ord;
            bool improved = true; int guard = 0;
            while (improved && guard++ < 200)
            {
                improved = false;
                for (int i = 0; i < n - 1; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        NmPt a = i == 0 ? start : pts[ord[i - 1]], b = pts[ord[i]], c = pts[ord[j]];
                        double before = Dist(a, b), after = Dist(a, c);
                        if (j < n - 1) { NmPt d = pts[ord[j + 1]]; before += Dist(c, d); after += Dist(b, d); }
                        if (after < before - 1e-9) { ord.Reverse(i, j - i + 1); improved = true; }
                    }
            }
            return ord;
        }

        /// <summary>min(buoys, floor(v * remain / 3600 / d)).</summary>
        public static int Capacity(int buoys, double speedKn, double remainS, double stepNm)
        {
            if (buoys <= 0) return 0;
            if (stepNm <= 0) return buoys;
            double byFuel = Math.Max(0, speedKn) * Math.Max(0, remainS) / 3600.0 / stepNm;
            return Math.Min(buoys, (int)Math.Floor(byFuel + 1e-9));
        }

        /// <summary>
        /// K angular sectors (K = capacity.Count) counted from the bearing to the group; the node count of a sector is proportional to its capacity
        /// (largest remainder). Ring 0 goes to the earliest member (min etaS), the rest of its quota is its angular chunk; chunks are handed out in etaS order.
        /// The result is indexed like <paramref name="capacity"/>.
        /// </summary>
        public static List<List<int>> Sectors(IList<LatNode> nodes, double groupBrgDeg, IList<int> capacity, IList<double> etaS)
        {
            int k = capacity == null ? 0 : capacity.Count;
            var res = new List<List<int>>();
            for (int i = 0; i < k; i++) res.Add(new List<int>());
            if (k == 0 || nodes == null || nodes.Count == 0) return res;
            int n = nodes.Count;
            double sum = 0; for (int i = 0; i < k; i++) sum += Math.Max(0, capacity[i]);
            var w = new double[k];
            for (int i = 0; i < k; i++) w[i] = sum > 0 ? Math.Max(0, capacity[i]) : 1.0;
            double wsum = 0; foreach (double x in w) wsum += x;
            var quota = new int[k]; var rem = new double[k]; int given = 0;
            for (int i = 0; i < k; i++) { double q = n * w[i] / wsum; quota[i] = (int)Math.Floor(q + 1e-9); rem[i] = q - quota[i]; given += quota[i]; }
            while (given < n)
            {
                int bi = -1;
                for (int i = 0; i < k; i++) if (w[i] > 0 && (bi < 0 || rem[i] > rem[bi] + 1e-9)) bi = i;
                quota[bi]++; rem[bi] = -1; given++;
            }
            var rank = new List<int>();   // members by arrival time, earliest first
            for (int i = 0; i < k; i++) if (quota[i] > 0 || w[i] > 0) rank.Add(i);
            rank.Sort((a, b) => { double ea = etaS != null && a < etaS.Count ? etaS[a] : 0, eb = etaS != null && b < etaS.Count ? etaS[b] : 0; int c = ea.CompareTo(eb); return c != 0 ? c : a.CompareTo(b); });
            var centre = new List<int>(); var rest = new List<int>();
            for (int i = 0; i < n; i++) (nodes[i].Ring == 0 ? centre : rest).Add(i);
            rest.Sort((x, y) =>
            {
                double ax = AswMath.Norm360(nodes[x].AngDeg - groupBrgDeg), ay = AswMath.Norm360(nodes[y].AngDeg - groupBrgDeg);
                int c = ax.CompareTo(ay);
                if (c != 0) return c;
                c = nodes[x].Ring.CompareTo(nodes[y].Ring);
                return c != 0 ? c : x.CompareTo(y);
            });
            // the earliest member with a quota takes the centre
            int first = -1;
            foreach (int m in rank) if (quota[m] > 0) { first = m; break; }
            if (first < 0) return res;
            var left = new int[k];
            for (int i = 0; i < k; i++) left[i] = quota[i];
            foreach (int c in centre) { res[first].Add(c); left[first]--; }
            int pos = 0;
            foreach (int m in rank)
            {
                int take = Math.Max(0, left[m]);
                for (int t = 0; t < take && pos < rest.Count; t++) res[m].Add(rest[pos++]);
            }
            while (pos < rest.Count) res[first].Add(rest[pos++]);
            return res;
        }

        /// <summary>Leapfrog dip in an area: the lowest ring not visited / taken, then the nearest to <paramref name="from"/>; -1 = none.</summary>
        public static int NextAreaDip(IList<LatNode> nodes, ISet<int> visited, ISet<int> taken, NmPt from)
        {
            int best = -1; double bd = 0;
            if (nodes == null) return -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if ((visited != null && visited.Contains(i)) || (taken != null && taken.Contains(i))) continue;
                double d = Dist(from, nodes[i].P);
                if (best < 0 || nodes[i].Ring < nodes[best].Ring || (nodes[i].Ring == nodes[best].Ring && d < bd - 1e-9)) { best = i; bd = d; }
            }
            return best;
        }

        /// <summary>
        /// MAD strips of an area: SplitRect along the long side (weights w), coordinates centred (-long/2..+long/2).
        /// <paramref name="order"/>: strip indices, the one with the centre first, then outward by distance of the strip middle from the centre.
        /// </summary>
        public static List<double[]> MadStripsFromCentre(double northNm, double eastNm, IList<double> w, double legNm, out List<int> order)
        {
            double lng = Math.Max(northNm, eastNm), sht = Math.Min(northNm, eastNm);
            var strips = SplitRect(lng, sht, w, Math.Max(legNm, 0));
            var res = new List<double[]>();
            foreach (var s in strips) res.Add(new[] { s[0] - lng / 2, s[1] - lng / 2 });
            order = new List<int>();
            for (int i = 0; i < res.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                double ma = (res[a][0] + res[a][1]) / 2, mb = (res[b][0] + res[b][1]) / 2;
                bool ca = res[a][0] <= 1e-9 && res[a][1] >= -1e-9, cb = res[b][0] <= 1e-9 && res[b][1] >= -1e-9;
                if (ca != cb) return ca ? -1 : 1;
                int c = Math.Abs(ma).CompareTo(Math.Abs(mb));
                return c != 0 ? c : ma.CompareTo(mb);
            });
            return res;
        }

        private static string SelfTestArea()
        {
            int nx, ny; double sn, se;
            var l = Lattice(21.6, 12.96, 2.70, out nx, out ny, out sn, out se);
            if (nx != 5 || ny != 3 || l.Count != 15 || Math.Abs(sn - 4.32) > 1e-6 || Math.Abs(se - 4.32) > 1e-6) return "lattice " + nx + "x" + ny + " " + sn;
            int r0 = 0, r1 = 0, r2 = 0;
            foreach (var n in l) { if (n.Ring == 0) r0++; else if (n.Ring == 1) r1++; else if (n.Ring == 2) r2++; }
            if (r0 != 1 || r1 != 8 || r2 != 6) return "rings " + r0 + "/" + r1 + "/" + r2;
            var all = new List<int>(); for (int i = 0; i < l.Count; i++) all.Add(i);
            var route = RingRoute(l, all, new NmPt(-30, 0));
            if (route.Count != 15 || l[route[0]].Ring != 0) return "route start";
            return null;
        }
    }
}
