using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>One divergence episode of one evasion run (T-279, ADR-0112).</summary>
    internal sealed class EvHelmWatch
    {
        public double BestErr = double.NaN;      // smallest |cmd - hdg| since SinceS
        public double SinceS = double.NaN;       // start of the no-progress window
        public double CmdDeg = double.NaN;       // the last command seen
        public bool Logged;                      // "stuck" line written for this episode
    }

    /// <summary>
    /// T-279 (ADR-0112): pure rules of the evasion helm. The game state PerformingAirOps steers by CourseCommand only (waypoints are ignored); the helm writes its own
    /// SetCourse there, and notices "the ship does not follow the command". No Unity, no Harmony, no reflection.
    /// </summary>
    internal static class EvasionHelmMath
    {
        public const double DivergeDeg = 20, DivergeS = 15, ProgressDeg = 5, CmdResetDeg = 20, WriteEpsDeg = 1;
        public const double FollowRecheckS = 5, FollowReachNm = 0.3, FollowMaxS = 1800;

        /// <summary>The game state steers by CourseCommand only (waypoints ignored).</summary>
        public static bool AirOps(string stateName, bool? performingAirOpsFlag)
        {
            return stateName == "PerformingAirOps" || performingAirOpsFlag == true;
        }

        /// <summary>Feeds one sample; true once |Diff(cmd,hdg)| > DivergeDeg has not shrunk by ProgressDeg for DivergeS.
        /// Reset: err &lt;= DivergeDeg, or the command moved by >= CmdResetDeg between two samples (a new turn).</summary>
        public static bool Diverged(EvHelmWatch w, double nowS, double cmdDeg, double hdgDeg)
        {
            double err = Math.Abs(EvasionMath.Diff(hdgDeg, cmdDeg));
            bool jump = !double.IsNaN(w.CmdDeg) && Math.Abs(EvasionMath.Diff(w.CmdDeg, cmdDeg)) >= CmdResetDeg;
            w.CmdDeg = cmdDeg;
            if (err <= DivergeDeg || jump)
            {
                w.SinceS = double.NaN; w.BestErr = double.NaN; w.Logged = false;
                if (err <= DivergeDeg) return false;
            }
            if (double.IsNaN(w.SinceS)) { w.SinceS = nowS; w.BestErr = err; return false; }
            if (err <= w.BestErr - ProgressDeg) { w.BestErr = err; w.SinceS = nowS; return false; }
            return nowS - w.SinceS >= DivergeS;
        }

        /// <summary>"airops" | "course_cmd" | null. airops -> always; course_cmd -> diverged && a foreign CourseCommand while by == "player"
        /// (Default/Loitering: a stale course overrides the waypoint); else null (log only).</summary>
        public static string Mode(bool airOps, string maneuverBy, bool foreignCourseCmd, bool diverged)
        {
            if (airOps) return "airops";
            if (diverged && foreignCourseCmd && maneuverBy == "player") return "course_cmd";
            return null;
        }

        /// <summary>Write needed: the current command is not ours, or ours differs from course by >= WriteEpsDeg.</summary>
        public static bool NeedWrite(bool ours, double? currentCmdDeg, double courseDeg)
        {
            if (!ours || !currentCmdDeg.HasValue) return true;
            return Math.Abs(EvasionMath.Diff(currentCmdDeg.Value, courseDeg)) >= WriteEpsDeg;
        }

        /// <summary>After the evasion: "follow" (Air Ops on, ours current, route has points) | "game" (Air Ops on, no route: null, the game holds the heading) |
        /// "clear" (no Air Ops, ours current: null so the waypoints steer) | "none" (not ours: someone else commands, untouched).</summary>
        public static string After(bool airOps, bool ours, int routePoints)
        {
            if (!ours) return "none";
            if (!airOps) return "clear";
            return routePoints > 0 ? "follow" : "game";
        }

        /// <summary>T-283: while Air Ops last, a telegraph that changed since the last look is the player's new wish (the game keeps its own until onExit).</summary>
        public static int? TgObserve(int? player, ref int? seen, int? now)
        {
            if (now.HasValue && now != seen) { seen = now; return now; }
            return player;
        }

        /// <summary>T-283: Air Ops are over; the step to put back (the player's) when the game's onExit changed it, else null.</summary>
        public static int? ExitFix(int? player, int? now)
        {
            if (!player.HasValue || !now.HasValue || player.Value == now.Value) return null;
            return player;
        }

        /// <summary>T-293: Air Ops ended inside the evasion (airSeen && !airNow); live is 5 or slowTg and differs from noted -> true (game's onExit or an unnoted re-set, not the player).</summary>
        public static bool ExitInsideIsMod(bool airSeen, bool airNow, int? live, int? noted, int? slowTg)
        {
            if (!airSeen || airNow || !live.HasValue) return false;
            if (noted.HasValue && noted.Value == live.Value) return false;
            return live.Value == 5 || (slowTg.HasValue && live.Value == slowTg.Value);
        }

        /// <summary>Follower target: first index >= passed whose distance > FollowReachNm; -1 = none left. distNm[i] = own -> waypoint i.</summary>
        public static int FollowIndex(IList<double> distNm, int passed)
        {
            if (distNm == null) return -1;
            for (int i = Math.Max(0, passed); i < distNm.Count; i++)
                if (distNm[i] > FollowReachNm) return i;
            return -1;
        }

        public static string SelfTest()
        {
            // stuck: command 020, heading 093 for good -> true on the 15th second, not earlier
            var w = new EvHelmWatch();
            for (int t = 0; t <= 16; t++)
            {
                bool d = Diverged(w, t, 20, 93);
                if (t < 15 && d) return "stuck too early at " + t;
                if (t == 15 && !d) return "stuck not flagged at 15";
            }
            // a 180 deg turn at 3 deg/s never diverges
            w = new EvHelmWatch();
            for (int t = 0; t <= 60; t++)
            {
                double hdg = Math.Max(20, 200 - 3.0 * t);
                if (Diverged(w, t, 20, hdg)) return "turn flagged at " + t;
            }
            // the command moving by 10 deg (020 -> 010 -> 000 -> 350) does not reset the window; a jump of 20+ does
            w = new EvHelmWatch();
            double[] cmds = { 20, 10, 0, 350 };
            bool any = false;
            for (int t = 0; t <= 16; t++) any |= Diverged(w, t, cmds[Math.Min(3, t / 4)], 93);
            if (!any) return "10 deg command drift reset the window";
            w = new EvHelmWatch();
            for (int t = 0; t <= 10; t++) Diverged(w, t, 20, 93);
            Diverged(w, 11, 150, 93);   // a new turn: the window restarts
            if (!double.IsNaN(w.SinceS) && w.SinceS < 11) return "jump did not restart";
            if (Diverged(w, 20, 150, 93)) return "diverged right after a jump";
            // err <= 20 resets
            w = new EvHelmWatch();
            for (int t = 0; t <= 10; t++) Diverged(w, t, 20, 93);
            if (Diverged(w, 11, 20, 30) || !double.IsNaN(w.SinceS)) return "err<=20 did not reset";
            if (!AirOps("PerformingAirOps", null)) return "AirOps by state";
            if (!AirOps("Default", true)) return "AirOps by flag";
            if (AirOps(null, null) || AirOps("Default", false)) return "AirOps false";
            // Mode
            if (Mode(true, "air_ops", false, false) != "airops") return "Mode airops";
            if (Mode(false, "player", true, true) != "course_cmd") return "Mode course_cmd";
            if (Mode(false, "player_override", true, true) != null) return "Mode player_override";
            if (Mode(false, "player", false, true) != null) return "Mode no foreign cmd";
            if (Mode(false, "player", true, false) != null) return "Mode not diverged";
            // NeedWrite
            if (!NeedWrite(false, 93, 20)) return "NeedWrite foreign";
            if (NeedWrite(true, 20.4, 20)) return "NeedWrite same";
            if (!NeedWrite(true, 25, 20)) return "NeedWrite moved";
            if (!NeedWrite(true, null, 20)) return "NeedWrite null";
            // After
            if (After(true, true, 2) != "follow" || After(true, true, 0) != "game" || After(false, true, 3) != "clear" || After(true, false, 3) != "none" || After(false, false, 0) != "none")
                return "After table";
            // T-283 telegraph: the player's 3 survives the game's exit-time 5; a change during Air Ops is the player's
            int? seen = 5;
            if (TgObserve(3, ref seen, 5) != 3 || seen != 5) return "TgObserve unchanged";
            if (TgObserve(3, ref seen, 2) != 2 || seen != 2) return "TgObserve player change";
            if (TgObserve(3, ref seen, null) != 3) return "TgObserve null";
            if (ExitFix(3, 5) != 3 || ExitFix(3, 3).HasValue || ExitFix(null, 5).HasValue || ExitFix(3, null).HasValue) return "ExitFix";
            // T-293 ExitInsideIsMod
            if (!ExitInsideIsMod(true, false, 1, 2, 1) || !ExitInsideIsMod(true, false, 5, 2, null)) return "ExitInsideIsMod mod";
            if (ExitInsideIsMod(true, false, 3, 2, 1) || ExitInsideIsMod(true, false, 2, 2, 1) || ExitInsideIsMod(false, false, 1, 2, 1)
                || ExitInsideIsMod(true, true, 1, 2, 1) || ExitInsideIsMod(true, false, null, 2, 1)) return "ExitInsideIsMod not mod";
            // FollowIndex
            if (FollowIndex(new List<double> { 0.1, 2.0 }, 0) != 1) return "FollowIndex 1";
            if (FollowIndex(new List<double> { 0.1 }, 0) != -1) return "FollowIndex -1";
            if (FollowIndex(new List<double> { 5, 6 }, 1) != 1 || FollowIndex(new List<double> { 5, 6 }, 2) != -1 || FollowIndex(null, 0) != -1) return "FollowIndex passed";
            return null;
        }
    }
}
