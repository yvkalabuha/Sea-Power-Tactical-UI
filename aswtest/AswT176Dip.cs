using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C8 (v3-dip): the dipping chain on the axis, the leapfrog, the hold at the standoff.
    internal static partial class AswT176Tests
    {
        private static string DipScenario()
        {
            // R_dip = 2.1: s = 2. Two helicopters; each stands over its point after it was ordered (they are teleported)
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1 };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", dip: true, passive: 0); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); f.FixPos.Add(u); }
            var c = StartCourse(f, "{\"tactics\":[\"dip\"],\"standoff\":{\"dip\":20}}");
            if (c == null) return "order refused";
            int seen = 0; var xs = new List<double>(); var uids = new List<int>(); var times = new List<double>();
            for (int i = 0; i < 4000; i++)
            {
                f.Advance(1); f.Tick(c);
                while (seen < f.Dips.Count)
                {
                    var p = f.Dips[seen][0]; var q = AswMath.ToLocal(new GeoPt(35, 20), 90, p);
                    if (Math.Abs(q.Y) > 0.05) return "a dip point off the axis: y=" + q.Y;
                    xs.Add(q.X); uids.Add(f.DipUids[seen]); times.Add(f.Now);
                    var m = f.Mem.First(x => x.Uid == f.DipUids[seen]); m.Lat = p.Lat; m.Lon = p.Lon;
                    seen++;
                }
            }
            if (xs.Count < 8) return "dip orders " + xs.Count;
            // 2, 4, 6 ... each one step beyond the farthest taken
            for (int i = 0; i < 4; i++) if (!Near(xs[i], 2.0 * (i + 1), 0.1)) return "point " + i + " at x=" + xs[i] + " (" + 2 * (i + 1) + ")";
            if (uids[0] == uids[1]) return "the first two points belong to one helicopter";
            // at most one point ahead per helicopter: never two orders of one helicopter in a row at a larger x without the other one moving on
            // the chain never goes beyond O + L (the group has moved on 15 kn * time)
            for (int i = 0; i < xs.Count; i++)
            {
                double limit = 20 + 15 * (times[i] - 1000) / 3600.0 + 0.2;
                if (xs[i] > limit) return "point " + i + " at x=" + xs[i] + " beyond the standoff " + limit;
            }
            // at the standoff the dip is repeated in place
            int repeats = 0; for (int i = 1; i < xs.Count; i++) if (Near(xs[i], xs[i - 1], 0.05)) repeats++;
            if (repeats == 0) return "no repeat in place at the standoff";
            if (!Logged(f, " hold")) return "no hold line";
            // one helicopter: the same without the leapfrog, and the dip_single warning stays
            var f1 = new AswFakeGame { DropDelayS = 0, DipRange = 2.1 };
            var h1 = AswFakeGame.Helo(1, "air", dip: true, passive: 0); h1.EnduranceS = 1e7; h1.FuelFrac = 1; f1.Mem.Add(h1);
            var c1 = StartCourse(f1, "{\"tactics\":[\"dip\"]}");
            Run(c1, f1, 30);
            if (f1.Dips.Count == 0) return "one helicopter: nothing";
            return null;
        }
    }
}
