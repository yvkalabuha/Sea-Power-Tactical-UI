using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C7 (v3-barrier), rewritten by T-229: the sliding barrier (rows I, 2I ... N*I from the ship; the next row when the ship reaches the nearest).
    internal static partial class AswT176Tests
    {
        private static string Barrier()
        {
            // T-229 (ADR-0094): X = 40, I = 5, N = 2, SSQ-53: the first row of 10 buoys, step 3.96, on x = 5 from the group; the next row when the ship reaches it
            var f = CorridorGame(400);
            var c = StartCourse(f, "{\"tactics\":[\"barrier\"]}");
            if (c == null) return "order refused";
            Run(c, f, 3);
            if (f.Drops.Count < 1 || f.Drops[0].Pts.Count != 10) return "line " + f.Drops.Count + "/" + (f.Drops.Count > 0 ? f.Drops[0].Pts.Count : 0);
            var loc = Local(f, f.Drops[0]);
            foreach (var q in loc) if (!Near(q.X, 5, 0.1)) return "a buoy at x=" + q.X;
            var ys = loc.Select(q => q.Y).OrderBy(y => y).ToList();
            if (!Near(ys[0], -17.84, 0.05) || !Near(ys[9], 17.84, 0.05)) return "edges " + ys[0] + " .. " + ys[9];
            for (int i = 1; i < ys.Count; i++) if (!Near(ys[i] - ys[i - 1], 3.96, 0.05)) return "step " + (ys[i] - ys[i - 1]);
            if (f.Drops[0].Ammo != "usn_ssq-53") return "ammo";
            // 15 kn: row 0 (5 nm) is reached after 20 min, then the row at 15 nm is added
            Run(c, f, 1100);   // the second row (10 nm) was laid right after the first
            if (f.Drops.Count != 2) return "packages after 18 min: " + f.Drops.Count;
            Run(c, f, 200);
            if (f.Drops.Count != 3) return "no third row after 21 min (" + f.Drops.Count + ")";
            if (!Near(Local(f, f.Drops[2]).Average(q => q.X), 15, 0.3)) return "the third row at x=" + Local(f, f.Drops[2]).Average(q => q.X);
            // rows behind the group are forgotten: after 3 h two rows ahead and at most one passed
            for (int i = 0; i < 1000; i++) { f.Advance(10); f.Tick(c); }
            if (c.BarrierLines("G") != 2) return "rows ahead " + c.BarrierLines("G");
            // the active buoy of the centre only on request
            if (f.Drops.Any(d => d.Ammo == "usn_ssq-62")) return "an active buoy was dropped";

            // SSQ-41 (30 min): the row at 10 nm cannot be laid yet (10 nm = 40 min > 0.8 * life): life_short, it is laid when the ship is close enough (x >= 4 nm)
            var f2 = CorridorGame(40);
            f2.TypesOf[1] = new List<BuoyType> { new BuoyType { Ammo = "usn_ssq-41", Sensor = "SSQ-41_Sonar", PassiveKm = 10, LifeS = 1800 } };
            var c2 = StartCourse(f2, "{\"tactics\":[\"barrier\"]}");
            Run(c2, f2, 3);
            if (f2.Drops.Count != 1) return "SSQ-41: no row 0";
            if (!Near(Local(f2, f2.Drops[0]).Average(q => q.X), 5, 0.3)) return "SSQ-41 row 0 at x=" + Local(f2, f2.Drops[0]).Average(q => q.X);
            Run(c2, f2, 600);
            if (f2.Drops.Count != 1) return "SSQ-41: row 1 laid at 10 min (" + f2.Drops.Count + ")";
            if (!f2.HasEvent("asw_warn", "life_short")) return "no life_short";
            Run(c2, f2, 400);
            if (f2.Drops.Count != 2) return "SSQ-41: row 1 not laid at 17 min (" + f2.Drops.Count + ")";
            return null;
        }
    }
}
