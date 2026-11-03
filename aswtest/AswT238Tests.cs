using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-238 / ADR-0098: the barrier axis by the player's route, the anchor axis after a load, the journal lines of the barrier, the sticky deck_down warning. `aswtest t238 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names238 =
        {
            "route-math", "evade-keeps", "route-turn", "route-end", "rbu-turn", "player-route-during-evasion", "band-start-route", "axis-log", "load-seed", "log-always", "deck-sticky", "deck-load",
        };

        public static string RunT238(string only = null)
        {
            string e;
            foreach (string n in Names238)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "route-math": e = AswRouteGuard.SelfTest(); break;
                    case "evade-keeps": e = EvadeKeeps(); break;
                    case "route-turn": e = RouteTurn(); break;
                    case "route-end": e = RouteEnd(); break;
                    case "rbu-turn": e = RbuTurn(); break;
                    case "player-route-during-evasion": e = PlayerRouteDuringEvasion(); break;
                    case "band-start-route": e = BandStartRoute(); break;
                    case "axis-log": e = AxisLog(); break;
                    case "load-seed": e = LoadSeed(); break;
                    case "log-always": e = LogAlways238(); break;
                    case "deck-sticky": e = AswT238Deck.Sticky(); break;
                    case "deck-load": e = AswT238Deck.Load(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names238, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static int Count238(AswFakeGame f, string sub) { return f.Logs.Count(l => l.Contains(sub)); }

        /// <summary>Runs until the log has the line or the seconds are over; returns the seconds it took, -1 = never.</summary>
        private static int RunUntil(AswController c, AswFakeGame f, string sub, int maxS)
        {
            for (int i = 1; i <= maxS; i++) { f.Advance(1); f.Tick(c); if (Logged(f, sub)) return i; }
            return -1;
        }

        private static string EvadeKeeps()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);
            if (!Near(c.BandAxisOf("G"), 90, 0.1)) return "start axis " + c.BandAxisOf("G");
            f.Tgt.ModSteered = true; f.Tgt.SavedCourseDeg = 90;
            double[] turns = { 150, 30, 200, 120, 330, 60 };
            for (int k = 0; k < 6; k++) { f.Tgt.CourseDeg = turns[k]; Run(c, f, 40 + 10 * k); }
            Run(c, f, 60);
            if (Logged(f, "barrier reset")) return "reset while the mod steers the ship";
            if (!Near(c.BandAxisOf("G"), 90, 0.1)) return "band axis " + c.BandAxisOf("G");
            if (!Logged(f, "barrier axis=90 src=held")) return "no held line";
            return null;
        }

        private static string RouteTurn()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);
            f.Tgt.RouteBrg = 140;
            Run(c, f, 59);
            if (Logged(f, "barrier reset")) return "reset before 60 s";
            int s = RunUntil(c, f, "barrier reset reason=turn", 10);
            if (s < 0) return "no reset 61 s after the route turned";
            Run(c, f, 2);
            if (!Near(c.BandAxisOf("G"), 140, 0.1)) return "the new band axis " + c.BandAxisOf("G");
            return null;
        }

        private static string RouteEnd()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);
            f.Tgt.RouteBrg = null;
            Run(c, f, 180);
            if (Logged(f, "barrier reset")) return "reset when the route ended on a steady course";
            if (!Logged(f, "barrier axis=90 src=course")) return "no course source line";
            f.Tgt.CourseDeg = 160;
            if (RunUntil(c, f, "barrier reset reason=turn", 600) < 0) return "no reset on a steady turn without a route";
            return null;
        }

        private static string RbuTurn()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);
            f.Tgt.ModSteered = true; f.Tgt.SavedCourseDeg = 90; f.Tgt.CourseDeg = 60;
            Run(c, f, 90);
            if (Logged(f, "barrier reset")) return "reset on the RBU turn";
            return null;
        }

        private static string PlayerRouteDuringEvasion()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 100);
            f.Tgt.ModSteered = true; f.Tgt.CourseDeg = 60;
            Run(c, f, 30);
            f.Tgt.ModSteered = false; f.Tgt.RouteBrg = 200;   // the player gave a new route during the evasion
            Run(c, f, 50);
            if (Logged(f, "barrier reset")) return "reset before 60 s";
            if (RunUntil(c, f, "barrier reset reason=turn", 30) < 0) return "no reset after the player's new route";
            return null;
        }

        private static string BandStartRoute()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 120;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 3);
            if (!Near(c.BandAxisOf("G"), 120, 0.1)) return "band axis " + c.BandAxisOf("G");
            return null;
        }

        private static string AxisLog()
        {
            var f = CorridorGame(400);
            f.Tgt.RouteBrg = 90;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            f.Tgt.ModSteered = true; Run(c, f, 120);
            f.Tgt.ModSteered = false; Run(c, f, 120);
            var lines = f.Logs.Where(l => l.Contains("barrier axis=")).ToList();
            if (lines.Count != 3) return "axis lines " + lines.Count + ": " + string.Join(" | ", lines.ToArray());
            if (!lines[0].EndsWith("src=route") || !lines[1].EndsWith("src=held") || !lines[2].EndsWith("src=route")) return "sources " + string.Join(" | ", lines.ToArray());
            return null;
        }

        private static string LogAlways238()
        {
            string[] yes =
            {
                "asw pattern ASW-1 barrier row seq=0 passed unlaid=0",
                "asw pattern ASW-1 barrier row seq=2 x=15 buoys=10 step=3.96 X=40 I=5",
                "asw pattern ASW-1 barrier reset reason=turn rows=2",
                "asw pattern ASW-1 barrier axis=90 src=route",
                "asw pattern ASW-1 frame hold end reason=timeout",   // T-259
            };
            string[] no = { "asw frame ASW-1 axis=0 src=heading" };   // T-245: anchor / reanchor lines are always written now
            foreach (string l in yes) if (!AswMath.LogAlways(l)) return "not always: " + l;
            foreach (string l in no) if (AswMath.LogAlways(l)) return "always: " + l;
            return null;
        }

        private static string LoadSeed()
        {
            foreach (bool strip in new[] { false, true })
            {
                string e = LoadSeedCase(strip, 0);
                if (e != null) return (strip ? "no frame_axis_deg: " : "") + e;
            }
            // R-3: a package still in flight when the game is saved
            string e2 = LoadSeedCase(false, 300);
            if (e2 != null) return "package in flight: " + e2;
            return null;
        }

        private static string LoadSeedCase(bool stripFrameAxis, double dropDelayS)
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw238-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                f.SaveDir = dir; f.LastSavePath = "/s/a.sav";
                f.Tgt.CourseDeg = 167; f.DropDelayS = dropDelayS;
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                c.SearchCourse(P(Barrier229));
                Run(c, f, 90);
                if (!Logged(f, "anchor axis=167")) return "the first anchor is not 167";
                if (!Near(c.BandAxisOf("G"), 167, 0.1)) return "band axis before the save " + c.BandAxisOf("G");
                var stamp = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
                c.OnGameSaved("/s/b.sav", stamp);
                if (stripFrameAxis)
                    foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string txt = File.ReadAllText(file);
                        if (!txt.Contains("frame_axis_deg")) continue;
                        File.WriteAllText(file, Regex.Replace(txt, ",\"frame_axis_deg\":[-0-9.eE]+", ""));
                    }
                var g = CorridorGame(400);
                g.SaveDir = dir; g.LastSavePath = "/s/b.sav"; g.Now = 2000;
                g.Tgt.Lat = f.Tgt.Lat; g.Tgt.Lon = f.Tgt.Lon;
                g.Tgt.CourseDeg = 0;   // the game reads the heading of the ship as 0 right after a load
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                for (int i = 0; i < 4; i++) g.Tick(c2);   // ticks with no time passing: the heading still reads 0
                if (!Logged(g, "anchor axis=167") || !Logged(g, "src=save")) return "the anchor after the load is not from the save: " + string.Join(" | ", g.Logs.Where(l => l.Contains("anchor") || l.Contains("frame")).ToArray());
                g.Tgt.CourseDeg = 167;
                Run(c2, g, 120);
                if (Logged(g, "reanchor reason=turn")) return "reanchor turn after the load";
                if (Logged(g, "packet=cancelled")) return "a packet was cancelled";
                if (Logged(g, "barrier reset")) return "barrier reset after the load";
                // R-3: no package is given twice for the same points (a repeat after the load would show as equal first points)
                var firsts = g.Drops.Select(d => Math.Round(d.Pts[0].Lat, 4) + "," + Math.Round(d.Pts[0].Lon, 4)).ToList();
                if (firsts.Count != firsts.Distinct().Count()) return "a package was issued twice after the load: " + string.Join(" ", firsts.ToArray());
                if (dropDelayS == 0 && g.Drops.Count > 0) return "packages for rows already laid before the save: " + g.Drops.Count;
                if (!Near(c2.BandAxisOf("G"), 167, 0.1)) return "band axis after the load " + c2.BandAxisOf("G");
                return null;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }
    }
}
