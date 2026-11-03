using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C10 (v3-reseed): a node whose buoy goes out is a candidate again; the nodes behind the group are not.
    internal static partial class AswT176Tests
    {
        private static List<BuoyType> Ssq41() { return new List<BuoyType> { new BuoyType { Ammo = "usn_ssq-41", Sensor = "SSQ-41_Sonar", PassiveKm = 10, LifeS = 3600 } }; }

        private static string Reseed()
        {
            // the area: buoys of 1 h, the nodes are laid again when the buoys go out (10 min before)
            var f = new AswFakeGame { DropDelayS = 0 };
            var h = AswFakeGame.Helo(1, "air", passive: 200); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; f.Mem.Add(h); f.TypesOf[1] = Ssq41();
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            GeoPt ctr;
            c.SearchArea(P(AreaCmd(f, 21.6, 12.96, "[\"grid\"]", out ctr)));
            Run(c, f, 60);
            int first = f.Drops.Sum(d => d.Pts.Count);
            if (first < 12) return "first pass " + first;
            for (int i = 0; i < 270; i++) { f.Advance(10); f.Tick(c); }   // 47 min: still 13 min of life
            if (f.Drops.Sum(d => d.Pts.Count) != first) return "re-seeded too early";
            for (int i = 0; i < 130; i++) { f.Advance(10); f.Tick(c); }   // 69 min: the buoys are gone
            int second = f.Drops.Sum(d => d.Pts.Count);
            if (second <= first) return "nothing re-seeded after the buoys went out";
            if (!Logged(f, "asw reseed G nodes=")) return "no reseed line";
            int done = 0; foreach (var e in f.Events) if (e.StartsWith("asw_area_done ", StringComparison.Ordinal)) done++;
            if (done < 2) return "asw_area_done again after the refresh: " + done;

            // by course: SSQ-41 (1 h) 3 h long: the carpet rolls with the group, nothing is laid behind it
            var f2 = new AswFakeGame { DropDelayS = 0 };
            var h2 = AswFakeGame.Helo(1, "air", passive: 400); h2.EnduranceS = 1e7; h2.FuelFrac = 1; h2.Vlad = 0; h2.ActiveBuoys = 0; f2.Mem.Add(h2); f2.TypesOf[1] = Ssq41();
            var c2 = StartCourse(f2, "{\"tactics\":[\"corridor\"]}");
            double t0 = f2.Now;
            for (int i = 0; i < 1080; i++) { f2.Advance(10); f2.Tick(c2); }
            if (f2.Drops.Count < 6) return "course: packages in 3 h " + f2.Drops.Count;
            foreach (var d in f2.Drops)
            {
                double s = 15 * (d.At - t0) / 3600.0;
                foreach (var p in d.Pts)
                {
                    var q = AswMath.ToLocal(new GeoPt(35, 20), 90, p);
                    if (q.X < s - 1.0) return "a node behind the group at x=" + q.X + " (group at " + s + ")";
                    if (q.X > s + 10.0) return "a node beyond the life of the buoy at x=" + q.X;
                }
            }
            return null;
        }
    }
}
