using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C5 (v3-area-sketch, v3-area-split, v3-area-dip): the area from the centre in rings, sectors by capacity, the dipping leapfrog.
    internal static partial class AswT176Tests
    {
        private static string RectJ(double[] r) { return "[" + string.Join(",", r.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"; }

        // an area 18 nm north of the group (the group is to the south)
        private static string AreaCmd(AswFakeGame f, double east, double north, string tactics, out GeoPt ctr)
        {
            ctr = new GeoPt(f.Tgt.Lat + 0.3, f.Tgt.Lon);
            return "{\"group\":\"G\",\"rect\":" + RectJ(AswController.RectAround(ctr, east, north)) + ",\"tactics\":" + tactics + "}";
        }

        private static AswMember Plane(int uid, int passive, double kn)
        {
            var m = AswFakeGame.Helo(uid, "air", passive: passive);
            m.Helicopter = false; m.SpeedKn = kn; m.Type = "P-3C"; m.TypeLabel = "P-3C"; m.EnduranceS = 1e7; m.FuelFrac = 1;
            return m;
        }

        private static string AreaSketch()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            var h = AswFakeGame.Helo(1, "air", passive: 60); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            GeoPt ctr;
            c.SearchArea(P(AreaCmd(f, 21.6, 12.96, "[\"grid\"]", out ctr)));
            Run(c, f, 5);
            if (f.Drops.Count != 1 || f.Drops[0].Pts.Count != 15) return "package " + f.Drops.Count + "/" + (f.Drops.Count > 0 ? f.Drops[0].Pts.Count : 0);
            if (!Logged(f, "lattice nx=3 ny=5 d=4.32")) return "lattice line: " + f.Logs.FirstOrDefault(l => l.Contains("lattice"));
            var pts = f.Drops[0].Pts;
            var loc = pts.Select(p => AswMath.ToLocal(ctr, 0, p)).ToList();
            if (Math.Abs(loc[0].X) > 0.05 || Math.Abs(loc[0].Y) > 0.05) return "the first drop is not the centre";
            for (int i = 1; i <= 8; i++) { double ch = Math.Max(Math.Abs(loc[i].X), Math.Abs(loc[i].Y)) / 4.32; if (Math.Abs(ch - 1) > 0.01) return "ring 1 point " + i + " at " + ch; }
            for (int i = 9; i <= 14; i++) { double ch = Math.Max(Math.Abs(loc[i].X), Math.Abs(loc[i].Y)) / 4.32; if (Math.Abs(ch - 2) > 0.01) return "ring 2 point " + i + " at " + ch; }
            double len = 0; for (int i = 1; i < loc.Count; i++) len += Math.Sqrt(Math.Pow(loc[i].X - loc[i - 1].X, 2) + Math.Pow(loc[i].Y - loc[i - 1].Y, 2));
            if (len > 75.6) return "path " + len;
            foreach (var q in loc) if (Math.Abs(q.X) > 12.96 / 2 - 0.8 * 2.7 + 0.01 || Math.Abs(q.Y) > 21.6 / 2 - 0.8 * 2.7 + 0.01) return "a point outside the edge margin";
            double minPair = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) for (int j = i + 1; j < pts.Count; j++) minPair = Math.Min(minPair, AswMath.DistNm(pts[i], pts[j]));
            if (minPair < 3.02) return "min_pair " + minPair;
            if (!Logged(f, "tactic=grid pts=15") || !Logged(f, "ratio=")) return "no route line";
            // the end of the area: asw_area_done once
            if (!f.HasEvent("asw_area_done")) return "no asw_area_done";
            int n = 0; foreach (var e in f.Events) if (e.StartsWith("asw_area_done ", StringComparison.Ordinal)) n++;
            Run(c, f, 120);
            foreach (var e in f.Events) if (e.StartsWith("asw_area_done ", StringComparison.Ordinal)) n--;
            if (n != 0) { /* counted twice */ }
            int after = 0; foreach (var e in f.Events) if (e.StartsWith("asw_area_done ", StringComparison.Ordinal)) after++;
            if (after != 1) return "asw_area_done " + after + " times";
            // a smaller side than 1.6R is one node
            return null;
        }

        private static string AreaSplit()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            f.Mem.Add(Plane(1, 56, 300));
            var h = AswFakeGame.Helo(2, "air", passive: 12); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
            f.FixPos.Add(1); f.FixPos.Add(2);
            f.Mem[0].Lat = 35.2; f.Mem[0].Lon = 20.0; f.Mem[1].Lat = 34.9; f.Mem[1].Lon = 20.0;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            GeoPt ctr;
            c.SearchArea(P(AreaCmd(f, 21.6, 12.96, "[\"grid\"]", out ctr)));
            Run(c, f, 5);
            var by = new Dictionary<int, List<GeoPt>>();
            foreach (var d in f.Drops) { List<GeoPt> l; if (!by.TryGetValue(d.Uid, out l)) by[d.Uid] = l = new List<GeoPt>(); l.AddRange(d.Pts); }
            if (!by.ContainsKey(1) || !by.ContainsKey(2)) return "one aircraft only";
            if (Math.Abs(by[1].Count - 12) > 1 || Math.Abs(by[2].Count - 3) > 1) return "split " + by[1].Count + "/" + by[2].Count + " (12/3)";
            // the centre goes to the one who arrives first: the aeroplane (300 kn)
            bool centre1 = by[1].Any(p => AswMath.DistNm(p, ctr) < 0.1), centre2 = by[2].Any(p => AswMath.DistNm(p, ctr) < 0.1);
            if (!centre1 || centre2) return "the centre is not with the earliest aircraft";
            // no node twice
            var all = new List<GeoPt>(); all.AddRange(by[1]); all.AddRange(by[2]);
            for (int i = 0; i < all.Count; i++) for (int j = i + 1; j < all.Count; j++) if (AswMath.DistNm(all[i], all[j]) < 1) return "a node twice";
            if (all.Count != 15) return "nodes " + all.Count;
            // each sector is walked from the centre outwards
            foreach (var kv in by)
            {
                double prev = -1;
                var rings = kv.Value.Select(p => { var q = AswMath.ToLocal(ctr, 0, p); return (int)Math.Round(Math.Max(Math.Abs(q.X), Math.Abs(q.Y)) / 4.32); }).ToList();
                foreach (int r in rings) { if (r < prev - 0.5) return "uid " + kv.Key + " goes back to an inner ring"; prev = r; }
            }
            return null;
        }

        private static string AreaDipScenario()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", dip: true, passive: 0); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Mad = false; f.Mem.Add(h); f.FixPos.Add(u); }
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            GeoPt ctr;
            c.SearchArea(P(AreaCmd(f, 21.6, 12.96, "[\"dip\"]", out ctr)));
            // both start south of the area; a dip takes ~2 minutes, the helicopter then stands over its point
            var pos = new Dictionary<int, NmPt>(); pos[1] = pos[2] = new NmPt(-18, 0);
            f.Mem[0].Lat = ctr.Lat - 0.3; f.Mem[1].Lat = ctr.Lat - 0.3;
            var path = new Dictionary<int, double> { { 1, 0 }, { 2, 0 } };
            int seen = 0;
            for (int i = 0; i < 4000; i++)
            {
                f.Advance(1); f.Tick(c);
                while (seen < f.Dips.Count)
                {
                    int uid = f.DipUids[seen]; var p = AswMath.ToLocal(ctr, 0, f.Dips[seen][0]);
                    path[uid] += Math.Sqrt(Math.Pow(p.X - pos[uid].X, 2) + Math.Pow(p.Y - pos[uid].Y, 2));
                    pos[uid] = p;
                    var m = f.Mem.First(x => x.Uid == uid); m.Lat = f.Dips[seen][0].Lat; m.Lon = f.Dips[seen][0].Lon;
                    seen++;
                }
            }
            // s = 4: lattice of 6 x 4 = 24 nodes, every one visited once
            var all = f.Dips.Select(d => d[0]).ToList();
            if (all.Count != 24) return "dip points " + all.Count + " (24)";
            for (int i = 0; i < all.Count; i++) for (int j = i + 1; j < all.Count; j++) if (AswMath.DistNm(all[i], all[j]) < 1) return "a dip node twice";
            // the rings come in order
            int prevRing = 0;
            foreach (var p in all)
            {
                var q = AswMath.ToLocal(ctr, 0, p);
                double ic = Math.Round(q.X / 4 * 2) / 2.0, jc = Math.Round(q.Y / 4 * 2) / 2.0;
                int ring = (int)Math.Max(0, Math.Ceiling(Math.Max(Math.Abs(ic), Math.Abs(jc)) - 0.5 - 1e-9));
                if (ring < prevRing - 1) return "rings out of order " + ring + " after " + prevRing;
                prevRing = Math.Max(prevRing, ring);
            }
            double total = path[1] + path[2];
            // the lower bound of the plan: 23 steps of 4 miles; the entry from the group is not part of the tour
            double entry = 2 * 18 - 2 * 0;
            if (total - entry > 1.3 * 23 * 4 + 1) return "path " + total + " (limit " + (1.3 * 23 * 4 + 1 + entry) + ")";
            return null;
        }
    }
}
