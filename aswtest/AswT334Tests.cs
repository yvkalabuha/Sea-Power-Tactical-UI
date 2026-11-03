using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-334 / ADR-0130: the order of the buoys of the corridor and the barrier by course: near first, never behind the ship at the moment of the drop, a package in flight is checked again.
    // The stand flies the packages by path (AswFakeGame.DropByPathKn): a point falls when the aircraft gets to it, so the ship's place at the fall is known. `aswtest t334 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names334 = { "corridor-one", "corridor-two", "barrier-order", "barrier-skip", "speed-up", "turn", "two-aircraft-barrier" };

        public static string RunT334(string only = null)
        {
            string e;
            foreach (string n in Names334)
            {
                if (only != null && only != n) continue;
                if ((e = Scenario334(n)) != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names334, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static string Scenario334(string name)
        {
            switch (name)
            {
                case "corridor-one": return CorridorOne334();
                case "corridor-two": return CorridorTwo334();
                case "barrier-order": return BarrierOrder334();
                case "barrier-skip": return BarrierSkip334();
                case "speed-up": return SpeedUp334();
                case "turn": return Turn334();
                case "two-aircraft-barrier": return TwoBarrier334();
                default: return null;
            }
        }

        private static AswFakeGame Slow334(double kn, params int[] passive)
        {
            var f = CorridorGame(passive);
            foreach (var m in f.Mem) m.SpeedKn = kn;
            f.DropByPathKn = kn;
            return f;
        }

        /// <summary>x of a point in the frame of the start of the course (origin (35, 20), axis 90).</summary>
        private static double X334(GeoPt p) { return AswMath.ToLocal(new GeoPt(35, 20), 90, p).X; }

        /// <summary>Least distance (nm) a fallen buoy had ahead of the ship along the axis at the moment of the fall (the ship goes along 90 until a test turns it).</summary>
        private static double MinAhead334(AswFakeGame f, double from = 0)
        {
            double min = double.MaxValue;
            foreach (var r in f.FallLog) if (r.At >= from) min = Math.Min(min, X334(r.P) - X334(r.Ship));
            return min;
        }

        private static string CorridorOne334()
        {
            // X = 20, L = 20, one SH-3H (100 kn) behind a 15 kn ship: the old loop put 6 of 24 buoys astern; now the ladder, the points that would be astern are not laid
            var f = Slow334(100, 60);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20,\"standoff\":{\"corridor\":20}}");
            if (c == null) return "order refused";
            Run(c, f, 3000);
            if (f.Drops.Count < 1) return "no package";
            var loc = Local(f, f.Drops[0]);
            if (loc.Count != 20) return "first package " + loc.Count + " points (20 expected: the 4 unreachable nodes of the nearest column are left out)";
            for (int i = 1; i < loc.Count; i++) if (loc[i].X < loc[i - 1].X - 0.06) return "x goes back at point " + i + " (" + loc[i - 1].X + " -> " + loc[i].X + ")";
            // never a far column before a near one in the whole run (per package)
            foreach (var d in f.Drops)
            {
                var l = Local(f, d);
                for (int i = 1; i < l.Count; i++) if (l[i].X < l[i - 1].X - 0.06) return "a far column before a near one";
            }
            if (f.FallLog.Count < 20) return "only " + f.FallLog.Count + " buoys fell";
            double ahead = MinAhead334(f);
            if (ahead < 0.95) return "a buoy fell " + ahead + " nm ahead of the ship (>= 1 expected)";
            if (!Logged(f, "ahead uid=1 kept=20/24")) return "no ahead line: " + string.Join(" | ", f.Logs.Where(l => l.Contains("ahead")).ToArray());
            if (!Logged(f, "side=ladder")) return "no side=ladder";
            return null;
        }

        private static string CorridorTwo334()
        {
            var f = Slow334(100, 60, 60);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"]}");
            Run(c, f, 2500);
            var d1 = f.Drops.Where(d => d.Uid == 1).SelectMany(d => Local(f, d)).ToList();
            var d2 = f.Drops.Where(d => d.Uid == 2).SelectMany(d => Local(f, d)).ToList();
            if (d1.Count == 0 || d2.Count == 0) return "packages " + d1.Count + "/" + d2.Count;
            if (d1.Any(q => q.Y >= 0) || d2.Any(q => q.Y <= 0)) return "the sides are mixed";
            foreach (int uid in new[] { 1, 2 })
            {
                var l = f.Drops.Where(d => d.Uid == uid).SelectMany(d => Local(f, d)).ToList();
                for (int i = 1; i < l.Count; i++) if (l[i].X < l[i - 1].X - 0.06) return "uid " + uid + ": x goes back at " + i;
            }
            double ahead = MinAhead334(f);
            if (ahead < 0.95) return "a buoy fell " + ahead + " nm ahead of the ship";
            return null;
        }

        private const string Barrier4 = "{\"tactics\":[\"barrier\"],\"barrier_rows\":4,\"barrier_gap_nm\":5}";

        private static string BarrierOrder334()
        {
            // 250 kn: all four rows stand ahead of the ship in time: 5 -> 10 -> 15 -> 20
            var f = Slow334(250, 400);
            f.Mem[0].Helicopter = false;
            var c = StartCourse(f, Barrier4);
            if (c == null) return "order refused";
            Run(c, f, 3600);
            if (f.Drops.Count < 4) return "packages " + f.Drops.Count;
            double[] want = { 5, 10, 15, 20 };
            for (int i = 0; i < 4; i++) if (!Near(AvgX(f, f.Drops[i]), want[i], 0.3)) return "package " + i + " at x=" + AvgX(f, f.Drops[i]) + " (" + want[i] + " expected)";
            double ahead = MinAhead334(f);
            if (ahead < 0.95) return "a buoy fell " + ahead + " nm ahead of the ship";
            return null;
        }

        private static string BarrierSkip334()
        {
            // 100 kn: row 5 (10 buoys, 3 reachable at best) is not taken, the first package is row 10
            var f = Slow334(100, 400);
            var c = StartCourse(f, Barrier4);
            Run(c, f, 20);
            if (f.Drops.Count < 1) return "no package";
            if (!Near(AvgX(f, f.Drops[0]), 10, 0.3)) return "the first package is at x=" + AvgX(f, f.Drops[0]) + " (row 10 expected)";
            Run(c, f, 1100);   // the ship gets within a mile of x = 5 at 4 nm = 16 min
            if (!Logged(f, "barrier row seq=0 closed skipped=")) return "row 0 was not closed: " + string.Join(" | ", f.Logs.Where(l => l.Contains("barrier row")).ToArray());
            if (!f.HasEvent("asw_warn", "barrier_late")) return "no barrier_late";
            double ahead = MinAhead334(f);
            if (ahead < 0.95) return "a buoy fell " + ahead + " nm ahead of the ship";
            // T-322: the row that was taken off whole fades like a passed one
            c.View();
            if (!c.StaleInfo("G").Contains(":0:passed:")) return "row 0 is not stale: " + c.StaleInfo("G");
            return null;
        }

        private static string SpeedUp334()
        {
            var f = Slow334(100, 60);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20,\"standoff\":{\"corridor\":20}}");
            Run(c, f, 300);
            if (f.Drops.Count != 1) return "packages before " + f.Drops.Count;
            int fellBefore = f.FallLog.Count;
            f.Tgt.SpeedKn = 30;
            Run(c, f, 600);
            if (!Logged(f, "ahead late uid=1")) return "no ahead late: " + string.Join(" | ", f.Logs.Where(l => l.Contains("ahead") || l.Contains("reanchor")).ToArray());
            if (!Logged(f, "-> replan")) return "no replan";
            if (f.Drops.Count < 2) return "no new package (" + f.Drops.Count + ")";
            // what fell stays and is not laid again: no two buoys closer than 0.3 nm
            for (int i = 0; i < f.FallLog.Count; i++)
                for (int j = i + 1; j < f.FallLog.Count; j++)
                    if (AswMath.DistNm(f.FallLog[i].P, f.FallLog[j].P) < 0.3) return "buoys " + i + " and " + j + " are " + AswMath.DistNm(f.FallLog[i].P, f.FallLog[j].P) + " nm apart";
            if (c.GroupBuoys("G") < f.FallLog.Count) return "the group lost buoys";
            // after the change every new package is ahead of the faster ship
            var last = f.Drops[f.Drops.Count - 1];
            double minAfter = MinAhead334(f, last.At);
            if (f.FallLog.Any(r => r.At >= last.At) && minAfter < 0.9) return "a buoy of the new package fell " + minAfter + " nm ahead";
            return null;
        }

        private static string Turn334()
        {
            // the ship turns 90 degrees and holds the new course: no cancel while the band still stands (no way along the old axis), then a new band along the new heading
            var f = Slow334(250, 400);
            f.Mem[0].Helicopter = false;
            var c = StartCourse(f, Barrier4);
            Run(c, f, 60);
            f.Tgt.CourseDeg = 180;
            double resetAt = -1;
            for (int i = 0; i < 900 && resetAt < 0; i++) { Run(c, f, 1); if (Logged(f, "barrier reset reason=turn")) resetAt = f.Now; }
            if (resetAt < 0) return "no band reset after a held 90 degree turn";
            if (Logged(f, "ahead late")) return "a package was cancelled by the turn alone: " + string.Join(" | ", f.Logs.Where(l => l.Contains("ahead late")).ToArray());
            Run(c, f, 600);
            var after = f.FallLog.Where(r => r.At > resetAt).ToList();
            if (after.Count == 0) return "no buoys after the reset";
            // along the new axis (south): the buoy is south of the ship by >= ~1 nm
            foreach (var r in after)
            {
                double ahead = (r.Ship.Lat - r.P.Lat) * 60.0;
                if (ahead < 0.9) return "a buoy of the new band fell " + ahead + " nm ahead (south) of the ship";
            }
            return null;
        }

        private static string TwoBarrier334()
        {
            var f = Slow334(100, 400, 400);
            var c = StartCourse(f, "{\"tactics\":[\"barrier\"],\"barrier_rows\":3,\"barrier_gap_nm\":5}");
            Run(c, f, 30);
            var a = f.Drops.Where(d => d.Uid == 1).ToList(); var b = f.Drops.Where(d => d.Uid == 2).ToList();
            if (a.Count < 1 || b.Count < 1) return "packages " + a.Count + "/" + b.Count;
            if (!Near(AvgX(f, a[0]), 10, 0.3)) return "the first aircraft at x=" + AvgX(f, a[0]) + " (row 10: row 5 cannot be reached in time)";
            if (!Near(AvgX(f, b[0]), 15, 0.3)) return "the second aircraft at x=" + AvgX(f, b[0]) + " (row 15 expected)";
            Run(c, f, 1100);
            if (!Logged(f, "barrier row seq=0 closed skipped=10")) return "row 0 was not closed: " + string.Join(" | ", f.Logs.Where(l => l.Contains("barrier row")).ToArray());
            double ahead = MinAhead334(f);
            if (ahead < 0.95) return "a buoy fell " + ahead + " nm ahead of the ship";
            return null;
        }
    }
}
