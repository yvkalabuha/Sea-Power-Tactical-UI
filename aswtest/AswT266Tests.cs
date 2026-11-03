using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-266 / ADR-0106: the barrier of a ship that cannot move (hit, engines out) keeps its anchor, frame axis and band axis. `aswtest t266 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names266 = { "hit-no-route", "hit-route", "hit-in-hold", "hit-save", "repaired", "player-stop" };

        public static string RunT266(string only = null)
        {
            string e;
            foreach (string n in Names266)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "hit-no-route": e = Hit266(false); break;
                    case "hit-route": e = Hit266(true); break;
                    case "hit-in-hold": e = HitInHold266(); break;
                    case "hit-save": e = HitSave266(); break;
                    case "repaired": e = Repaired266(); break;
                    case "player-stop": e = PlayerStop266(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names266, only) < 0) return "unknown scenario " + only;
            return null;
        }

        /// <summary>A barrier on an evasion that ends (course 37, speed 12); returns once the post-evasion hold is over (returned) when untilReturned.</summary>
        private static AswController Barrier266(AswFakeGame f, string dir, bool route, bool untilReturned)
        {
            var c = EvadingBarrier(f, dir);
            f.Tgt.CourseDeg = 20; f.Tgt.SpeedKn = 20;
            Run(c, f, 120);
            if (untilReturned)
            {
                var t = TurnAfterEvasion260(f, c, 1.5, 100, !route, "frame hold end reason=returned");
                if (t[0] < 0) return null;
            }
            else
            {
                f.Tgt.ModSteered = false; f.Tgt.SavedCourseDeg = null;
                f.Tgt.RouteBrg = route ? (double?)167 : null; f.Tgt.NextWpBrg = 167; f.Tgt.SpeedKn = 12; f.Tgt.CourseDeg = 37;
            }
            return c;
        }

        /// <summary>The ship is hit: speed falls to 0 in 25 s, the heading drifts to driftTo over driftS s.</summary>
        private static void Drift266(AswFakeGame f, AswController c, int seconds, double driftTo, double driftS)
        {
            double c0 = f.Tgt.CourseDeg, v0 = f.Tgt.SpeedKn;
            for (int i = 1; i <= seconds; i++)
            {
                f.Tgt.SpeedKn = Math.Max(0, v0 * (1 - i / 25.0));
                double k = Math.Min(1.0, i / driftS);
                f.Tgt.CourseDeg = AswMath.Norm360(c0 + Sd266(driftTo - c0) * k);
                f.Advance(1); f.Tick(c);
            }
        }

        private static double Sd266(double d) { d = ((d % 360) + 540) % 360 - 180; return d; }

        private static string Ctx266(AswFakeGame f, int mark)
        {
            return string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("frozen") || l.Contains("reanchor") || l.Contains("reset") || l.Contains("hold")).ToArray());
        }

        private static string Hit266(bool route)
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, route, true);
            if (c == null) return "no hold end reason=returned";
            double axis0 = c.BandAxisOf("G");
            int mark = f.Logs.Count;
            f.Tgt.ImmobileWhy = "engine";
            Drift266(f, c, 400, 60, 120);
            string u = Unrest260(f, mark); if (u != null) return u;
            int frozen = f.Logs.Skip(mark).Count(l => l.Contains("barrier frozen reason=immobile why=engine"));
            if (frozen != 1) return "frozen lines " + frozen + ": " + Ctx266(f, mark);
            if (!Near(c.BandAxisOf("G"), axis0, 0.01)) return "band axis " + axis0 + " -> " + c.BandAxisOf("G");
            if (f.Logs.Skip(mark).Any(l => l.Contains("asw frame G axis="))) return "the frame axis moved: " + string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("asw frame")).ToArray());
            return null;
        }

        /// <summary>Hit right in the post-evasion hold (the heading never comes back): no stand re-anchor after the hold times out (00:55:15 / 01:11:56).</summary>
        private static string HitInHold266()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, false, false);
            double axis0 = c.BandAxisOf("G");
            int mark = f.Logs.Count;
            f.Tgt.ImmobileWhy = "sinking";
            Drift266(f, c, 400, 90, 100);
            string u = Unrest260(f, mark); if (u != null) return u;
            if (!f.Logs.Skip(mark).Any(l => l.Contains("barrier frozen reason=immobile why=sinking"))) return "no frozen line: " + Ctx266(f, mark);
            if (!Near(c.BandAxisOf("G"), axis0, 0.01)) return "band axis " + axis0 + " -> " + c.BandAxisOf("G");
            return null;
        }

        private static string HitSave266()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw266-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                var c = Barrier266(f, dir, true, true);
                if (c == null) return "no hold end reason=returned";
                double axis0 = c.BandAxisOf("G");
                f.Tgt.ImmobileWhy = "engine";
                Drift266(f, c, 200, 60, 120);
                c.OnGameSaved("/s/b.sav", new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" });
                bool found = false;
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var m = Regex.Match(File.ReadAllText(file), "\"frame_axis_deg\":([-0-9.eE]+)");
                    if (!m.Success) continue;
                    found = true;
                    double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (AswMath.AngleDiff(v, axis0) > 1) return "frame_axis_deg " + v + " (axis before the hit " + axis0 + ", drift course 60)";
                }
                return found ? null : "no frame_axis_deg in the save";
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        private static string Repaired266()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, false, true);
            if (c == null) return "no hold end reason=returned";
            f.Tgt.CourseDeg = 167; f.Tgt.NextWpBrg = 167;
            int mark = f.Logs.Count;
            f.Tgt.ImmobileWhy = "engine";
            Drift266(f, c, 120, 167, 1);
            if (!f.Logs.Skip(mark).Any(l => l.Contains("barrier frozen reason=immobile"))) return "no frozen line: " + Ctx266(f, mark);
            f.Tgt.ImmobileWhy = null; f.Tgt.SpeedKn = 12;
            int mark2 = f.Logs.Count;
            Run(c, f, 60);
            if (!f.Logs.Skip(mark2).Any(l => l.Contains("barrier frozen end"))) return "no frozen end: " + Ctx266(f, mark2);
            if (f.Logs.Skip(mark2).Any(l => l.Contains("barrier reset"))) return "reset after the repair: " + Ctx266(f, mark2);
            f.Tgt.CourseDeg = 60; f.Tgt.NextWpBrg = 60; f.Tgt.RouteBrg = null;
            int mark3 = f.Logs.Count, seen = mark3, resetAt = -1;
            for (int i = 1; i <= 200; i++)
            {
                f.Advance(1); f.Tick(c);
                for (; seen < f.Logs.Count; seen++)
                    if (resetAt < 0 && f.Logs[seen].Contains("barrier reset reason=turn")) resetAt = i;
            }
            if (resetAt < 0 || resetAt > 150) return "turn reset at " + resetAt + ": " + Ctx266(f, mark3);
            return null;
        }

        /// <summary>The player stopped the ship (not immobile): ADR-0076 s.5 is as before, the ring / stand re-anchor happens.</summary>
        private static string PlayerStop266()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, false, true);
            if (c == null) return "no hold end reason=returned";
            int mark = f.Logs.Count;
            f.Tgt.ImmobileWhy = null;
            Drift266(f, c, 300, 167, 1);
            if (f.Logs.Skip(mark).Any(l => l.Contains("frozen"))) return "frozen for a ship that can move";
            if (!f.Logs.Skip(mark).Any(l => l.Contains("reason=stand"))) return "no stand re-anchor / reset: " + Ctx266(f, mark);
            return null;
        }
    }
}
