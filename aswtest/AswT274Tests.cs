using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-274 / ADR-0110: the barrier of a grounded ship (stopped under a telegraph) keeps its band until no_way freezes it; the journal says why it is not frozen. `aswtest t274 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names274 = { "log-always", "math", "grounded-route", "grounded-hold", "stand-timeout", "player-stop", "load-grounded", "freeze-wait" };

        public static string RunT274(string only = null)
        {
            string e;
            foreach (string n in Names274)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "log-always": e = LogAlways274(); break;
                    case "math": e = Math274(); break;
                    case "grounded-route": e = GroundedRoute274(true); break;
                    case "grounded-hold": e = GroundedRoute274(false); break;
                    case "stand-timeout": e = StandTimeout274(); break;
                    case "player-stop": e = PlayerStop274(); break;
                    case "load-grounded": e = LoadGrounded274(); break;
                    case "freeze-wait": e = FreezeWait274(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names274, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static string LogAlways274()
        {
            string[] yes = {
                "asw pattern ASW-1 barrier frozen reason=immobile why=no_way axis=219",
                "asw pattern ASW-1 barrier frozen end axis=219",
                "asw pattern ASW-1 barrier freeze wait reason=pending kn=0 tg=2 imm=-",
                "asw pattern ASW-1 barrier stand defer end reason=timeout s=180" };
            foreach (string l in yes) if (!AswMath.LogAlways(l)) return "not always logged: " + l;
            if (AswMath.LogAlways("asw frame ASW-1 axis=0 src=heading")) return "asw frame must stay behind Debug";
            return null;
        }

        private static string Math274()
        {
            double since = -1; bool to;
            if (AswRouteGuard.StandDefer(false, true, 0, 180, ref since, out to) || to) return "no band";
            since = 7;
            if (AswRouteGuard.StandDefer(true, false, 10, 180, ref since, out to) || since != -1) return "not pending";
            since = -1;
            if (!AswRouteGuard.StandDefer(true, true, 100, 180, ref since, out to) || since != 100) return "first call";
            if (!AswRouteGuard.StandDefer(true, true, 279, 180, ref since, out to) || to) return "179 s";
            if (AswRouteGuard.StandDefer(true, true, 280, 180, ref since, out to) || !to) return "180 s";
            since = 500;
            if (!AswRouteGuard.StandDefer(true, true, 100, 180, ref since, out to) || since != 100) return "time rewind";
            if (AswRouteGuard.FreezeWait(true, true, true, null, true, 3) != null) return "fast ship";
            if (AswRouteGuard.FreezeWait(false, true, true, null, true, 0) != null) return "not a barrier";
            if (AswRouteGuard.FreezeWait(true, true, true, "no_way", true, 0) != null) return "frozen";
            if (AswRouteGuard.FreezeWait(true, false, true, null, true, 0) != "no_band") return "no_band";
            if (AswRouteGuard.FreezeWait(true, true, false, null, true, 0) != "no_frame") return "no_frame";
            if (AswRouteGuard.FreezeWait(true, true, true, null, true, 0) != "pending") return "pending";
            if (AswRouteGuard.FreezeWait(true, true, true, null, false, 0) != "player_stop") return "player_stop";
            return null;
        }

        /// <summary>The ship slows from its speed to 0 in 25 s; WayPending / Telegraph / ImmobileWhy as the game gives them (no_way 60 s after the way fell below 0.5 kn, if noWay).</summary>
        private static void Ground274(AswFakeGame f, AswController c, int seconds, bool noWay, int telegraph, ref double slowSince, Action<int> after = null)
        {
            double v0 = f.Tgt.SpeedKn > 0 ? f.Tgt.SpeedKn : 20;
            for (int i = 1; i <= seconds; i++)
            {
                double v = Math.Max(0, v0 * (1 - i / 25.0));
                f.Tgt.SpeedKn = v;
                f.Tgt.Telegraph = telegraph;
                f.Tgt.WayPending = v < 3 && telegraph != 0;
                if (telegraph != 0 && v < 0.5) { if (slowSince < 0) slowSince = f.Now; } else slowSince = -1;
                f.Tgt.ImmobileWhy = noWay && slowSince >= 0 && f.Now - slowSince >= 60 ? "no_way" : null;
                f.Advance(1); f.Tick(c);
                if (after != null) after(i);
            }
        }

        private static string Ctx274(AswFakeGame f, int mark)
        {
            return string.Join(" | ", f.Logs.Skip(mark).Where(l => l.Contains("frozen") || l.Contains("reanchor") || l.Contains("reset") || l.Contains("freeze") || l.Contains("stand")).ToArray());
        }

        private static string GroundedRoute274(bool untilReturned)
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, untilReturned, untilReturned);
            if (c == null) return "no hold end reason=returned";
            double axis0 = c.BandAxisOf("G");
            int mark = f.Logs.Count; double slow = -1;
            Ground274(f, c, 400, true, 2, ref slow);
            // in the hold the heading may still end the band by a turn (T-260); the stand reset is what T-274 removes
            if (f.Logs.Skip(mark).Any(l => untilReturned ? l.Contains("barrier reset") : l.Contains("barrier reset reason=stand"))) return "barrier reset: " + Ctx274(f, mark);
            int frozen = f.Logs.Skip(mark).Count(l => l.Contains("barrier frozen reason=immobile why=no_way"));
            if (frozen != 1) return "frozen lines " + frozen + ": " + Ctx274(f, mark);
            if (untilReturned && !Near(c.BandAxisOf("G"), axis0, 0.01)) return "band axis " + axis0 + " -> " + c.BandAxisOf("G");
            return null;
        }

        /// <summary>The way fell, the telegraph asks for it, but no_way never comes: the band is reset after StandDeferMaxS with a timeout line.</summary>
        private static string StandTimeout274()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, true, true);
            if (c == null) return "no hold end reason=returned";
            int mark = f.Logs.Count, seen = mark, slowAt = -1, endAt = -1, resetAt = -1; double slow = -1;
            Ground274(f, c, 400, false, 2, ref slow, i =>
            {
                if (slowAt < 0 && f.Tgt.SpeedKn < 3) slowAt = i;
                for (; seen < f.Logs.Count; seen++)
                {
                    if (endAt < 0 && f.Logs[seen].Contains("barrier stand defer end reason=timeout")) endAt = i;
                    if (resetAt < 0 && f.Logs[seen].Contains("barrier reset reason=stand")) resetAt = i;
                }
            });
            if (endAt < 0 || resetAt < 0) return "no timeout line / reset: " + Ctx274(f, mark);
            if (resetAt < endAt) return "reset before the timeout line";
            int waited = resetAt - slowAt;
            if (waited < 178 || waited > 195) return "reset after " + waited + " s below 3 kn: " + Ctx274(f, mark);
            return null;
        }

        private static string PlayerStop274()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, true, true);
            if (c == null) return "no hold end reason=returned";
            int mark = f.Logs.Count; double slow = -1;
            Ground274(f, c, 300, true, 0, ref slow);
            if (f.Logs.Skip(mark).Any(l => l.Contains("barrier frozen"))) return "frozen for a ship the player stopped";
            if (!f.Logs.Skip(mark).Any(l => l.Contains("barrier reset reason=stand"))) return "no stand reset: " + Ctx274(f, mark);
            if (f.Logs.Skip(mark).Any(l => l.Contains("stand defer"))) return "defer line for a stopped ship";
            return null;
        }

        /// <summary>A save on the way, loaded with the ship aground (speed 0, telegraph 2): no stand reset, then frozen at no_way, the band axis of the save.</summary>
        private static string LoadGrounded274()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw274-" + Guid.NewGuid().ToString("N"));
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
                g.Tgt.CourseDeg = 167; g.Tgt.NextWpBrg = 167; g.Tgt.RouteBrg = 167; g.Tgt.ModSteered = false; g.Tgt.Track.Clear();
                g.Tgt.SpeedKn = 0; g.Tgt.Telegraph = 2; g.Tgt.WayPending = true;
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                double t0 = g.Now;
                for (int i = 0; i < 200; i++)
                {
                    g.Tgt.ImmobileWhy = g.Now - t0 >= 60 ? "no_way" : null;
                    g.Advance(1); g.Tick(c2);
                }
                if (!Logged(g, "anchor axis=167") || !Logged(g, "src=save")) return "anchor not from the save: " + string.Join(" | ", g.Logs.Where(l => l.Contains("anchor")).ToArray());
                if (Logged(g, "barrier reset")) return "barrier reset: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                if (!Logged(g, "barrier frozen reason=immobile why=no_way")) return "no frozen line: " + string.Join(" | ", g.Logs.Where(l => l.Contains("barrier")).ToArray());
                if (!Near(c2.BandAxisOf("G"), 167, 1.0)) return "band axis " + c2.BandAxisOf("G");
                return null;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        private static string FreezeWait274()
        {
            var f = CorridorGame(400);
            var c = Barrier266(f, null, true, true);
            if (c == null) return "no hold end reason=returned";
            int mark = f.Logs.Count; double slow = -1;
            Ground274(f, c, 400, true, 2, ref slow);
            int pend = f.Logs.Skip(mark).Count(l => l.Contains("barrier freeze wait reason=pending"));
            int all = f.Logs.Skip(mark).Count(l => l.Contains("barrier freeze wait"));
            if (pend != 1 || all != 1) return "pending lines " + pend + " of " + all + ": " + Ctx274(f, mark);
            int iPend = f.Logs.FindIndex(mark, l => l.Contains("freeze wait reason=pending")), iFrz = f.Logs.FindIndex(mark, l => l.Contains("barrier frozen reason"));
            if (iPend < 0 || iFrz < iPend) return "frozen before the wait line: " + Ctx274(f, mark);
            var g = CorridorGame(400);
            var d = Barrier266(g, null, true, true);
            int mark2 = g.Logs.Count; slow = -1;
            Ground274(g, d, 300, true, 0, ref slow);
            int stop = g.Logs.Skip(mark2).Count(l => l.Contains("barrier freeze wait reason=player_stop"));
            int nob = g.Logs.Skip(mark2).Count(l => l.Contains("barrier freeze wait reason=no_band"));
            int all2 = g.Logs.Skip(mark2).Count(l => l.Contains("barrier freeze wait"));
            if (stop != 1 || nob != 1 || all2 > 3) return "player stop lines " + stop + "/" + nob + " of " + all2 + ": " + Ctx274(g, mark2);
            return null;
        }
    }
}
