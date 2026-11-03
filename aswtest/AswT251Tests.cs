using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-251 / ADR-0103: the axis of the save holds the barrier after a load (any src), the first anchor on an evasion takes the player's route,
    // the frame waits for the ship to come back after the evasion. `aswtest t251 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names251 = { "hold-math", "load-evading-wp", "load-wp-confirm", "load-route-consistent", "first-anchor-evading", "evade-end-return", "evade-end-new-axis" };

        public static string RunT251(string only = null)
        {
            string e;
            foreach (string n in Names251)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "hold-math": e = AswRouteGuard.SelfTest() ?? AswPatternMath.SelfTest(); break;
                    case "load-evading-wp": e = Load251(true, 13, 13, true); break;
                    case "load-wp-confirm": e = Load251(false, 13, 13, false); break;
                    case "load-route-consistent": e = Load251(false, 160, 160, false); break;
                    case "first-anchor-evading": e = FirstAnchorEvading(); break;
                    case "evade-end-return": e = EvadeEnd(true); break;
                    case "evade-end-new-axis": e = EvadeEnd(false); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names251, only) < 0) return "unknown scenario " + only;
            return null;
        }

        /// <summary>Time of the first journal line that contains all parts, -1 = none (lines carry no time: the log count is mapped through the ticks).</summary>
        private static int FirstTick251(AswFakeGame f, AswController c, int ticks, params string[] parts)
        {
            for (int i = 0; i < ticks; i++)
            {
                Run(c, f, 1);
                foreach (var l in f.Logs) if (parts.All(p => l.Contains(p))) return i + 1;
            }
            return -1;
        }

        /// <summary>A save made on an evasion (band at 167), loaded: the route of the ship is the evasion point (brg), the mod does not steer yet (steered=false).</summary>
        private static string Load251(bool evadeRestart, double wpBrg, double routeBrg, bool evading)
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw251-" + Guid.NewGuid().ToString("N"));
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
                g.Tgt.CourseDeg = wpBrg; g.Tgt.NextWpBrg = wpBrg; g.Tgt.RouteBrg = routeBrg; g.Tgt.ModSteered = false; g.Tgt.Track.Clear();
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                for (int i = 0; i < 4; i++) g.Tick(c2);
                if (!Logged(g, "anchor axis=167") || !Logged(g, "src=save")) return "anchor not from the save: " + string.Join(" | ", g.Logs.Where(l => l.Contains("anchor") || l.Contains("frame")).ToArray());
                if (evadeRestart)
                {
                    // the evasion restarts 30 s after the load and remembers the evasion point as the course of the player
                    Run(c2, g, 30);
                    g.Tgt.ModSteered = true; g.Tgt.SavedCourseDeg = 13; g.Tgt.RouteBrg = 13; g.Tgt.CourseDeg = 70; g.Tgt.Track.Clear();
                    Run(c2, g, 150);
                    if (Logged(g, "barrier reset")) return "barrier reset while evading: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                    if (Logged(g, "reanchor")) return "reanchor while evading";
                    if (Logged(g, "barrier axis=13 ")) return "band axis followed the evasion point";
                    if (!Near(c2.BandAxisOf("G"), 167, 1.0)) return "band axis " + c2.BandAxisOf("G");
                    return null;
                }
                if (Math.Abs(AswMath.AngleDiff(wpBrg, 167)) >= 30)
                {
                    // the route does not agree with the save: the band holds 60 s, then the reset follows at once
                    int endAt = FirstTick251(g, c2, 100, "barrier hold end reason=confirmed");
                    if (endAt < 56 || endAt > 64) return "hold end at " + endAt + " (expected ~56-64, 4 ticks passed): " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                    int resetAt = FirstTick251(g, c2, 6, "barrier reset reason=turn");
                    if (resetAt < 0 && !Logged(g, "barrier reset reason=turn")) return "no reset after the confirmation: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                    return null;
                }
                Run(c2, g, 2);
                if (!Logged(g, "barrier hold end reason=consistent")) return "no consistent release: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                Run(c2, g, 180);
                if (Logged(g, "barrier reset")) return "barrier reset: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier reset")).ToArray());
                return null;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        private static string FirstAnchorEvading()
        {
            foreach (bool withRoute in new[] { true, false })
            {
                var f = CorridorGame(400);
                f.Tgt.CourseDeg = 1.6; f.Tgt.NextWpBrg = 1.6; f.Tgt.ModSteered = true;
                f.Tgt.RouteBrg = withRoute ? (double?)167 : null; f.Tgt.SavedCourseDeg = withRoute ? (double?)null : 167;
                var c = StartCourse(f, Barrier229);
                if (c == null) return "order refused";
                Run(c, f, 4);
                string want = withRoute ? "src=route" : "src=saved_course";
                if (!Logged(f, "anchor axis=167") || !Logged(f, want)) return want + ": " + string.Join(" | ", f.Logs.Where(l => l.Contains("anchor")).ToArray());
            }
            return null;
        }

        /// <summary>The evasion ends: back on the route (return = true) the axis and the packets stay, to a new course the reset comes by the 60 s rule only (T-260: the frame follows the route at once).</summary>
        private static string EvadeEnd(bool back)
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            f.Tgt.SpeedKn = 30; f.Tgt.Track.Clear();
            Run(c, f, 120);
            f.Tgt.ModSteered = false; f.Tgt.SavedCourseDeg = null; f.Tgt.Track.Clear();
            f.Tgt.CourseDeg = 60; f.Tgt.NextWpBrg = 60; f.Tgt.RouteBrg = back ? (double?)null : 60; f.Tgt.SpeedKn = back ? 25 : 15;
            int mark = f.Logs.Count;
            if (back)
            {
                Run(c, f, 20);
                f.Tgt.CourseDeg = 167; f.Tgt.NextWpBrg = 167; f.Tgt.RouteBrg = 167; f.Tgt.SpeedKn = 15; f.Tgt.Track.Clear();
                Run(c, f, 200);
                var after = f.Logs.Skip(mark).ToList();
                if (after.Any(l => l.Contains("reanchor") || l.Contains("packet=cancelled") || l.Contains("barrier reset"))) return "unrest after the evasion: " + string.Join(" | ", after.Where(l => l.Contains("reanchor") || l.Contains("barrier reset")).ToArray());
                return null;
            }
            // T-260 / ADR-0104: the new route of the player releases the frame at once; the band (ADR-0094) and the frame both turn by the 30 deg / 60 s rule
            Run(c, f, 52);
            var early = f.Logs.Skip(mark).ToList();
            if (early.Any(l => l.Contains("reanchor") || l.Contains("barrier reset"))) return "reset within 55 s of the end: " + string.Join(" | ", early.Where(l => l.Contains("reanchor") || l.Contains("barrier reset")).ToArray());
            Run(c, f, 23);
            var late = f.Logs.Skip(mark).ToList();
            if (!late.Any(l => l.Contains("barrier reset reason=turn"))) return "no turn reset by 75 s: " + string.Join(" | ", late.Where(l => l.Contains("barrier") || l.Contains("reanchor")).ToArray());
            if (!late.Any(l => l.Contains("reanchor reason=turn"))) return "no reanchor by 75 s: " + string.Join(" | ", late.Where(l => l.Contains("barrier") || l.Contains("reanchor")).ToArray());
            if (!late.Any(l => l.Contains("frame hold end reason=new_route"))) return "the hold did not end by new_route: " + string.Join(" | ", late.Where(l => l.Contains("hold")).ToArray());
            return null;
        }
    }
}
