using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-260 / ADR-0104: after the evasion the frame of a barrier with a band waits for the heading to come back to the player's route (<= 180 s)
    // and takes its axis from the route, not from the track (the track still holds the evasion leg). `aswtest t260 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names260 = { "slow-turn-route", "slow-turn-load", "very-slow-turn", "new-route", "no-route", "no-route-turn-later" };

        public static string RunT260(string only = null)
        {
            string e;
            foreach (string n in Names260)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "slow-turn-route": e = SlowTurn260(1.5, 300, false); break;
                    case "slow-turn-load": e = SlowTurnLoad260(); break;
                    case "very-slow-turn": e = VerySlowTurn260(); break;
                    case "new-route": e = NewRoute260(); break;
                    case "no-route": e = SlowTurn260(1.5, 300, true); break;
                    case "no-route-turn-later": e = NoRouteTurnLater264(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names260, only) < 0) return "unknown scenario " + only;
            return null;
        }

        /// <summary>Ends the evasion (course 37, route 167 unless noRoute, speed 12) and turns the heading to 167 at rate deg/s; returns the first tick of each log part (-1 = never).</summary>
        private static int[] TurnAfterEvasion260(AswFakeGame f, AswController c, double rate, int ticks, bool noRoute, params string[] parts)
        {
            f.Tgt.ModSteered = false; f.Tgt.SavedCourseDeg = null;
            f.Tgt.RouteBrg = noRoute ? null : (double?)167; f.Tgt.NextWpBrg = 167; f.Tgt.SpeedKn = 12; f.Tgt.CourseDeg = 37;
            var first = parts.Select(p => -1).ToArray();
            int seen = f.Logs.Count;
            for (int i = 1; i <= ticks; i++)
            {
                double d = 167 - f.Tgt.CourseDeg;
                f.Tgt.CourseDeg += Math.Max(-rate, Math.Min(rate, d));
                f.Advance(1); f.Tick(c);
                for (; seen < f.Logs.Count; seen++)
                    for (int k = 0; k < parts.Length; k++)
                        if (first[k] < 0 && f.Logs[seen].Contains(parts[k])) first[k] = i;
            }
            return first;
        }

        private static string Unrest260(AswFakeGame f, int mark)
        {
            var bad = f.Logs.Skip(mark).Where(l => l.Contains("reanchor") || l.Contains("packet=cancelled") || l.Contains("barrier reset")).ToArray();
            return bad.Length == 0 ? null : "unrest after the evasion: " + string.Join(" | ", bad);
        }

        private static string SlowTurn260(double rate, int ticks, bool noRoute)
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            f.Tgt.CourseDeg = 20; f.Tgt.SpeedKn = 20;
            Run(c, f, 120);
            int mark = f.Logs.Count;
            var t = TurnAfterEvasion260(f, c, rate, ticks, noRoute, "frame hold end reason=returned");
            string u = Unrest260(f, mark); if (u != null) return u;
            if (t[0] < 0) return "no hold end reason=returned: " + string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("hold")).ToArray());
            if (t[0] < 62) return "the hold ended at " + t[0] + " s, before the heading came back (old 60 s timeout)";
            if (t[0] > 180) return "the hold ended at " + t[0] + " s, after the 180 s bound";
            return null;
        }

        /// <summary>T-264: no route; after "returned" a real turn (to 60) within 180 s of the evasion end is not muted: reanchor reason=turn 55..80 s after it.</summary>
        private static string NoRouteTurnLater264()
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            f.Tgt.CourseDeg = 20; f.Tgt.SpeedKn = 20;
            Run(c, f, 120);
            int mark = f.Logs.Count;
            var t = TurnAfterEvasion260(f, c, 1.5, 100, true, "frame hold end reason=returned");
            string u = Unrest260(f, mark); if (u != null) return u;
            if (t[0] < 0) return "no hold end reason=returned: " + string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("hold")).ToArray());
            int mark2 = f.Logs.Count;
            f.Tgt.CourseDeg = 60; f.Tgt.NextWpBrg = 60; f.Tgt.RouteBrg = null;
            int seen = mark2, reAt = -1;
            for (int i = 1; i <= 120; i++)
            {
                f.Advance(1); f.Tick(c);
                for (; seen < f.Logs.Count; seen++)
                    if (reAt < 0 && f.Logs[seen].Contains("reanchor reason=turn")) reAt = i;
            }
            string ctx = string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("hold") || l.Contains("reset") || l.Contains("reanchor")).ToArray());
            if (reAt < 55 || reAt > 80) return "reanchor turn at " + reAt + " (expected 55..80): " + ctx;
            return null;
        }

        private static string VerySlowTurn260()
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            f.Tgt.CourseDeg = 20; f.Tgt.SpeedKn = 20;
            Run(c, f, 120);
            int mark = f.Logs.Count;
            var t = TurnAfterEvasion260(f, c, 0.5, 260, false, "frame hold end reason=timeout", "frame hold end reason=returned");
            if (t[0] < 175 || t[0] > 185) return "timeout at " + t[0] + " (expected ~180): " + string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("hold")).ToArray());
            if (t[1] >= 0) return "returned after timeout";
            var re = f.Logs.Skip(mark).Where(l => l.Contains("reanchor")).ToArray();
            if (re.Length > 1) return "reanchors " + re.Length + ": " + string.Join(" | ", re);
            foreach (string l in re)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, "d_axis=([-0-9.]+)");
                if (m.Success && Math.Abs(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) >= 30) return "reanchor with a large d_axis: " + l;
            }
            if (f.Logs.Skip(mark).Any(l => l.Contains("packet=cancelled"))) return "packet cancelled";
            return null;
        }

        private static string NewRoute260()
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            f.Tgt.CourseDeg = 20; f.Tgt.SpeedKn = 20;
            Run(c, f, 120);
            int mark = f.Logs.Count;
            f.Tgt.ModSteered = false; f.Tgt.SavedCourseDeg = null; f.Tgt.CourseDeg = 60; f.Tgt.NextWpBrg = 60; f.Tgt.RouteBrg = 60; f.Tgt.SpeedKn = 15;
            int seen = f.Logs.Count, endAt = -1, resetAt = -1, reAt = -1;
            for (int i = 1; i <= 100; i++)
            {
                f.Advance(1); f.Tick(c);
                for (; seen < f.Logs.Count; seen++)
                {
                    string l = f.Logs[seen];
                    if (endAt < 0 && l.Contains("frame hold end reason=new_route")) endAt = i;
                    if (resetAt < 0 && l.Contains("barrier reset reason=turn")) resetAt = i;
                    if (reAt < 0 && l.Contains("reanchor reason=turn")) reAt = i;
                }
            }
            string ctx = string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("hold") || l.Contains("reset") || l.Contains("reanchor")).ToArray());
            if (endAt < 1 || endAt > 3) return "hold end new_route at " + endAt + ": " + ctx;
            if (resetAt < 55 || resetAt > 75) return "barrier reset at " + resetAt + ": " + ctx;
            if (reAt < 55 || reAt > 75) return "reanchor at " + reAt + ": " + ctx;
            return null;
        }

        /// <summary>A save made on an evasion, loaded, the evasion resumed and ended; then a slow turn back to the route: no cancelled packets.</summary>
        private static string SlowTurnLoad260()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw260-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                var c = EvadingBarrier(f, dir);
                Run(c, f, 60);
                var stamp = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
                c.OnGameSaved("/s/b.sav", stamp);
                var g = CorridorGame(400);
                g.SaveDir = dir; g.LastSavePath = "/s/b.sav"; g.Now = 2000;
                g.Tgt.Lat = f.Tgt.Lat; g.Tgt.Lon = f.Tgt.Lon;
                g.Tgt.CourseDeg = 13; g.Tgt.NextWpBrg = 13; g.Tgt.RouteBrg = 13; g.Tgt.ModSteered = false; g.Tgt.Track.Clear();
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                for (int i = 0; i < 4; i++) g.Tick(c2);
                Run(c2, g, 30);
                g.Tgt.ModSteered = true; g.Tgt.SavedCourseDeg = 13; g.Tgt.RouteBrg = 13; g.Tgt.CourseDeg = 20; g.Tgt.SpeedKn = 20;
                Run(c2, g, 120);
                int mark = g.Logs.Count;
                var t = TurnAfterEvasion260(g, c2, 1.5, 300, false, "frame hold end reason=returned");
                string u = Unrest260(g, mark); if (u != null) return u;
                if (t[0] < 62) return "hold end at " + t[0] + ": " + string.Join(" | ", g.Logs.Skip(mark).Where(l => l.Contains("hold")).ToArray());
                return null;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }
    }
}
