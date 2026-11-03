using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C6 (v3-corridor, v3-corridor-short): the corridor X by course.
    internal static partial class AswT176Tests
    {
        private static AswFakeGame CorridorGame(params int[] passive)
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            for (int i = 0; i < passive.Length; i++)
            {
                var h = AswFakeGame.Helo(i + 1, "air", passive: passive[i]); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; h.SpeedKn = 250; f.Mem.Add(h);   // T-334: a fast aircraft, so the ETA filter keeps whole rows (the slow ones: AswT334Tests)
            }
            return f;
        }

        private static AswController StartCourse(AswFakeGame f, string order)
        {
            var c = Make(f);
            var ids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + ids + "]}"));
            string r = c.SearchCourse(P(order));
            return AswController.IsErr(r) ? null : c;
        }

        // the points of a package in the frame of the course: x forward from the group at the start, y to the right
        private static List<NmPt> Local(AswFakeGame f, DropCall d) { return d.Pts.Select(p => AswMath.ToLocal(new GeoPt(35, 20), 90, p)).ToList(); }

        private static bool Near(double a, double b, double tol = 0.06) { return Math.Abs(a - b) <= tol; }

        private static string Corridor()
        {
            // X = 20, L = 20, SSQ-53 (R = 2.70), one aircraft: the loop. Rows 2.16 / 5.00 / 7.84, columns 3 / 7.32 / 11.64 / 15.96
            var f = CorridorGame(60);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20,\"standoff\":{\"corridor\":20}}");
            if (c == null) return "order refused";
            Run(c, f, 3);
            if (f.Drops.Count != 1 || f.Drops[0].Pts.Count != 24) return "loop package " + f.Drops.Count + "/" + (f.Drops.Count > 0 ? f.Drops[0].Pts.Count : 0);
            var loc = Local(f, f.Drops[0]);
            double[] rows = { 2.16, 5.00, 7.84 }, cols = { 3, 7.32, 11.64, 15.96 };
            foreach (var q in loc)
            {
                if (!rows.Any(y => Near(Math.Abs(q.Y), y))) return "a point at y=" + q.Y;
                if (!cols.Any(x => Near(q.X, x))) return "a point at x=" + q.X;
            }
            // T-334 (ADR-0130): the ladder instead of the loop: the columns by x (the nearest first), across both sides, inside a column from the end nearer to the aircraft, the direction turns every column
            for (int i = 1; i < loc.Count; i++) if (loc[i].X < loc[i - 1].X - 0.06) return "x goes back at point " + i + " (" + loc[i - 1].X + " -> " + loc[i].X + ")";
            for (int c0 = 0; c0 < 4; c0++)
            {
                var col = loc.Skip(c0 * 6).Take(6).ToList();
                if (col.Any(q => !Near(q.X, cols[c0]))) return "column " + c0 + " is not whole";
                int dir = Math.Sign(col[1].Y - col[0].Y);
                for (int i = 1; i < 6; i++) if (Math.Sign(col[i].Y - col[i - 1].Y) != dir) return "column " + c0 + " is not monotonic in y";
                if (c0 > 0 && dir == Math.Sign(loc[(c0 - 1) * 6 + 1].Y - loc[(c0 - 1) * 6].Y)) return "column " + c0 + " goes the same way as the one before";
                if (c0 > 0 && !Near(col[0].Y, loc[c0 * 6 - 1].Y)) return "column " + c0 + " does not start where the previous one ended";
            }
            // every point is ahead of the ship by at least a mile when the aircraft gets there
            if (!Logged(f, "corridor uid=1 side=ladder k=1 pts=24 rows=3/3")) return "no pattern line: " + f.Logs.FirstOrDefault(l => l.Contains("corridor uid"));
            if (!Logged(f, "ahead uid=1 kept=24/24")) return "no ahead line: " + f.Logs.FirstOrDefault(l => l.Contains("ahead"));
            // a second look a little later: everything is covered, nothing more is ordered
            Run(c, f, 60);
            if (f.Drops.Count != 1) return "the covered water was laid again (" + f.Drops.Count + " packages)";

            // X = 10: one row on +-2.50; X = 50: six rows; SSQ-77 (R = 5.4), X = 20: one row, X = 30: two
            var rowsOf = new Func<string, int, double[]>((ord, passive) =>
            {
                var g = CorridorGame(passive); var cc = StartCourse(g, ord); Run(cc, g, 3);
                return g.Drops.SelectMany(d => Local(g, d)).Select(q => Math.Round(Math.Abs(q.Y), 2)).Distinct().OrderBy(y => y).ToArray();
            });
            var r10 = rowsOf("{\"tactics\":[\"corridor\"],\"corridor_x_nm\":10}", 40);
            if (r10.Length != 1 || !Near(r10[0], 2.5)) return "X=10 rows " + string.Join(",", r10);
            var r50 = rowsOf("{\"tactics\":[\"corridor\"],\"corridor_x_nm\":50}", 60);
            if (r50.Length != 6) return "X=50 rows " + string.Join(",", r50);
            var g77 = CorridorGame(0); g77.Mem[0].Vlad = 30; g77.TypesOf[1] = new List<BuoyType> { new BuoyType { Ammo = "usn_ssq-77", Sensor = "SSQ-77_Sonar", PassiveKm = 20, LifeS = 28800, Vlad = true } };
            var c77 = StartCourse(g77, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20"+ "}"); Run(c77, g77, 3);
            var y77 = g77.Drops.SelectMany(d => Local(g77, d)).Select(q => Math.Round(Math.Abs(q.Y), 1)).Distinct().ToArray();
            if (y77.Length != 1) return "SSQ-77 X=20 rows " + string.Join(",", y77);

            // K = 2: the left side and the right side
            f = CorridorGame(60, 60);
            c = StartCourse(f, "{\"tactics\":[\"corridor\"]}"); Run(c, f, 3);
            var d1 = f.Drops.Where(d => d.Uid == 1).SelectMany(d => Local(f, d)).ToList(); var d2 = f.Drops.Where(d => d.Uid == 2).SelectMany(d => Local(f, d)).ToList();
            if (d1.Count != 12 || d2.Count != 12 || d1.Any(q => q.Y >= 0) || d2.Any(q => q.Y <= 0)) return "K=2: " + d1.Count + "/" + d2.Count;

            // K = 3: the third one takes the side whose cursor lags (the right one: its aircraft had only 8 buoys)
            f = CorridorGame(12, 8, 30);
            c = StartCourse(f, "{\"tactics\":[\"corridor\"],\"standoff\":{\"corridor\":40}}"); Run(c, f, 3);
            var l1 = Local(f, f.Drops.First(d => d.Uid == 1));
            var l2 = Local(f, f.Drops.First(d => d.Uid == 2));
            var l3 = Local(f, f.Drops.First(d => d.Uid == 3));
            if (l1.Count != 12 || l1.Any(q => q.Y >= 0)) return "K=3 first: " + l1.Count;
            if (l2.Count != 6 || l2.Any(q => q.Y <= 0)) return "K=3 second: " + l2.Count + " (6 = two whole columns of 3 rows)";
            if (l3.Count == 0 || l3.Any(q => q.Y <= 0)) return "K=3 third is not on the lagging (right) side";
            if (l3.Min(q => q.X) < 11.5 || l3.Min(q => q.X) > 11.8) return "K=3 third starts at x=" + l3.Min(q => q.X) + " (11.64)";
            return null;
        }

        private static string CorridorShort()
        {
            // one aircraft, 10 buoys, X = 20, L = 20: 24 nodes, 8 per row level. The inner row first, then the outer wall, then the middle ones
            var f = CorridorGame(10);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"]}"); Run(c, f, 3);
            var ys = f.Drops.SelectMany(d => Local(f, d)).Select(q => Math.Round(Math.Abs(q.Y), 2)).Distinct().ToArray();
            if (ys.Length != 1 || !Near(ys[0], 2.16)) return "10 buoys: rows " + string.Join(",", ys);
            if (!f.HasEvent("asw_warn", "buoys_short")) return "no buoys_short";
            f = CorridorGame(18);
            c = StartCourse(f, "{\"tactics\":[\"corridor\"]}"); Run(c, f, 3);
            ys = f.Drops.SelectMany(d => Local(f, d)).Select(q => Math.Round(Math.Abs(q.Y), 2)).Distinct().OrderBy(y => y).ToArray();
            if (ys.Length != 2 || !Near(ys[0], 2.16) || !Near(ys[1], 7.84)) return "18 buoys: rows " + string.Join(",", ys);
            // a corridor wider than the rows allow
            f = CorridorGame(60);
            var oc = StartCourse(f, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":50}"); 
            var g = oc; 
            Run(oc, f, 3);
            return null;
        }
    }
}
