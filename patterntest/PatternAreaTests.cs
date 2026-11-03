using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 PA: v3-area-sketch, v3-area-split, v3-area-dip (pure part).
    internal static class PatternAreaTests
    {
        public static string Run()
        {
            int nx, ny; double sn, se;
            var lat = AswPatternMath.Lattice(21.6, 12.96, 2.70, out nx, out ny, out sn, out se);
            // PA1: sketch
            if (nx != 5 || ny != 3 || lat.Count != 15) return "sketch size " + nx + "x" + ny;
            if (Math.Abs(sn - 4.32) > 1e-6 || Math.Abs(se - 4.32) > 1e-6) return "sketch step " + sn + "/" + se;
            if (Math.Abs(lat.Max(n => n.P.X) - (21.6 / 2 - 0.8 * 2.7)) > 1e-6 || Math.Abs(lat.Max(n => n.P.Y) - (12.96 / 2 - 0.8 * 2.7)) > 1e-6) return "edge nodes";
            if (lat.Count(n => Math.Abs(n.P.X) < 1e-9 && Math.Abs(n.P.Y) < 1e-9) != 1) return "centre node";
            if (AswPatternMath.AxisCount(4.0, 2.7) != 1 || AswPatternMath.AxisCount(4.32, 2.7) != 1 || AswPatternMath.AxisCount(4.4, 2.7) != 2 || AswPatternMath.AxisCount(4.33, 2.7) != 1) return "axis count";
            int n1; var one = AswPatternMath.Lattice(4, 4, 2.7, out nx, out n1, out sn, out se);
            if (one.Count != 1 || one[0].Ring != 0) return "single node";
            // PA2: rings
            if (lat.Count(n => n.Ring == 0) != 1 || lat.Count(n => n.Ring == 1) != 8 || lat.Count(n => n.Ring == 2) != 6) return "rings";
            var even = AswPatternMath.Lattice(17.28, 17.28, 2.70, out nx, out ny, out sn, out se);   // 4x4
            if (nx != 4 || ny != 4) return "even size " + nx + "x" + ny;
            if (even.Count(n => n.Ring == 0) != 4) return "even centre " + even.Count(n => n.Ring == 0);
            var even2 = AswPatternMath.Lattice(17.28, 12.96, 2.70, out nx, out ny, out sn, out se);  // 4x3
            if (even2.Count(n => n.Ring == 0) != 2) return "even/odd centre " + even2.Count(n => n.Ring == 0);
            // PA3: route from the south
            var all = Enumerable.Range(0, lat.Count).ToList();
            var from = new NmPt(-20, 0);
            var route = AswPatternMath.RingRoute(lat, all, from);
            if (route.Count != 15 || route.Distinct().Count() != 15) return "route count";
            if (lat[route[0]].Ring != 0) return "route start not the centre";
            for (int i = 1; i < 9; i++) if (lat[route[i]].Ring != 1) return "route ring 1 at " + i;
            for (int i = 9; i < 15; i++) if (lat[route[i]].Ring != 2) return "route ring 2 at " + i;
            var pts = route.Select(i => lat[i].P).ToList();
            double len = AswPatternMath.PathNm(pts[0], pts.Skip(1).ToList());
            if (len > 1.25 * 14 * 4.32 + 1e-6) return "route length " + len;
            var again = AswPatternMath.RingRoute(lat, all, from);
            if (!again.SequenceEqual(route)) return "route not deterministic";
            // PA4: capacity and sectors
            if (AswPatternMath.Capacity(56, 300, 7200, 4.32) != 56) return "capacity p3";
            if (AswPatternMath.Capacity(12, 90, 3600, 4.32) != 12) return "capacity sh3 buoys";
            if (AswPatternMath.Capacity(50, 90, 3600, 4.32) != 20) return "capacity sh3 fuel " + AswPatternMath.Capacity(50, 90, 3600, 4.32);
            int cp = AswPatternMath.Capacity(56, 300, 7200, 4.32), cs = AswPatternMath.Capacity(12, 90, 1800, 4.32);
            var sec = AswPatternMath.Sectors(lat, 180, new[] { cp, cs }, new[] { 300.0, 120.0 });
            if (sec.Count != 2 || sec[0].Count + sec[1].Count != 15) return "sectors total";
            if (Math.Abs(sec[0].Count - 12) > 1 || Math.Abs(sec[1].Count - 3) > 1) return "sectors split " + sec[0].Count + "/" + sec[1].Count;
            if (sec[1].Contains(all.First(i => lat[i].Ring == 0)) == false) return "centre goes to the earliest (SH-3H)";
            foreach (var s in sec)   // continuity by the angle from the group bearing (centre excluded)
            {
                var keys = s.Where(i => lat[i].Ring > 0).Select(i => AswMath.Norm360(lat[i].AngDeg - 180)).OrderBy(x => x).ToList();
                var rest = all.Where(i => lat[i].Ring > 0).Select(i => AswMath.Norm360(lat[i].AngDeg - 180)).OrderBy(x => x).ToList();
                if (keys.Count == 0) continue;
                bool ok = false;   // a tie of equal angles may be cut between two sectors
                for (int at = rest.IndexOf(keys[0]); at <= rest.LastIndexOf(keys[0]) && !ok; at++)
                {
                    ok = at + keys.Count <= rest.Count;
                    for (int k = 0; ok && k < keys.Count; k++) if (Math.Abs(rest[at + k] - keys[k]) > 1e-9) ok = false;
                }
                if (!ok) return "sector not contiguous";
            }
            var three = AswPatternMath.Sectors(lat, 0, new[] { 10, 10, 10 }, new[] { 3.0, 2.0, 1.0 });
            if (three.Sum(x => x.Count) != 15 || three.Any(x => x.Count < 4)) return "three sectors";
            if (!three[2].Contains(all.First(i => lat[i].Ring == 0))) return "three: centre to eta min";
            var zero = AswPatternMath.Sectors(lat, 0, new[] { 0, 5 }, new[] { 1.0, 2.0 });
            if (zero[0].Count != 0 || zero[1].Count != 15) return "zero capacity";
            if (AswPatternMath.Sectors(lat, 0, new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, null).Sum(x => x.Count) != 15) return "K > nodes";
            // PA5: dip leapfrog by rings
            int nx2, ny2; double a, b;
            var dl = AswPatternMath.Lattice(21.6, 12.96, 2.70, out nx2, out ny2, out a, out b);
            var visited = new HashSet<int>(); var taken = new HashSet<int>();
            var heloPos = new[] { new NmPt(-20, 0), new NmPt(-20, 0) }; double total = 0; int order = 0;
            var seq = new List<int>();
            while (visited.Count < dl.Count)
            {
                int h = order++ % 2;
                int idx = AswPatternMath.NextAreaDip(dl, visited, taken, heloPos[h]);
                if (idx < 0) return "dip none early";
                if (visited.Contains(idx)) return "dip repeat";
                seq.Add(idx); visited.Add(idx);
                if (seq.Count > 2) total += AswPatternMath.PathNm(heloPos[h], new List<NmPt> { dl[idx].P });
                heloPos[h] = dl[idx].P;
            }
            if (AswPatternMath.NextAreaDip(dl, visited, taken, heloPos[0]) != -1) return "dip not exhausted";
            for (int i = 1; i < seq.Count; i++) if (dl[seq[i]].Ring < dl[seq[i - 1]].Ring) return "dip rings go back";
            if (total > 1.3 * AswPatternMath.LowerBoundNm(dl.Count - 2, 4.32) + 4.32 * 2) return "dip path " + total;
            taken.Add(seq[0]);
            if (AswPatternMath.NextAreaDip(dl, new HashSet<int>(), taken, new NmPt(0, 0)) == seq[0]) return "dip ignores taken";
            // PA6: MAD strips
            List<int> ord;
            var strips = AswPatternMath.MadStripsFromCentre(21.6, 12.96, new[] { 1.0, 2.0, 1.0 }, 1.6, out ord);
            if (strips.Count != 3) return "mad strips " + strips.Count;
            if (Math.Abs(strips[0][1] - strips[0][0] - 5.4) > 1e-6 || Math.Abs(strips[1][1] - strips[1][0] - 10.8) > 1e-6) return "mad widths";
            if (ord[0] != 1) return "mad first strip has the centre: " + ord[0];
            if (Math.Abs(strips[0][0] + 10.8) > 1e-6 || Math.Abs(strips[2][1] - 10.8) > 1e-6) return "mad centred";
            var even3 = AswPatternMath.MadStripsFromCentre(20, 10, new[] { 1.0, 1.0, 1.0, 1.0 }, 1.6, out ord);
            if (even3.Count != 4 || ord.Count != 4 || (ord[0] != 1 && ord[0] != 2)) return "mad even strips";
            // PA7: edges
            var tiny = AswPatternMath.Sectors(new List<LatNode>(), 0, new[] { 3 }, new[] { 1.0 });
            if (tiny.Count != 1 || tiny[0].Count != 0) return "sectors empty";
            var line = AswPatternMath.Lattice(30, 3, 2.7, out nx, out ny, out sn, out se);
            if (ny != 1 || nx != 7 || line.Any(n => Math.Abs(n.P.Y) > 1e-9)) return "n = 1 on one axis " + nx + "x" + ny;
            if (AswPatternMath.RingRoute(lat, new List<int>(), from).Count != 0) return "route empty subset";
            var sub = AswPatternMath.RingRoute(lat, new List<int> { 0, 14, 7 }, from);
            if (sub.Count != 3 || sub[0] != 7) return "route subset " + string.Join(",", sub);
            return null;
        }
    }
}
