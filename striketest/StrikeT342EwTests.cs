using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase A stand sections "t342 <name>" (Ew): the return of a jammer after an air retreat.
    internal static class StrikeT342EwTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "ew-clear" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "ew-clear": return EwClear();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private static StrikeFakeGame World()
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= 2; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            g.AddEw(5, 38.9, 10.2);
            return g;
        }

        private static string St(StrikeController c) { var s = c.Groups[0].EwExec.FirstOrDefault(x => x.Uid == 5); return s == null ? "none" : s.State; }

        /// <summary>D2, the scenario (g) of E1b: a Retreat threat, then Warn for a while; the jammer returns to the station not earlier than EwReturnClearS after the last threat (any stage).</summary>
        private static string EwClear()
        {
            var game = World(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", game.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            c.SetGoal(C("group", "A", "goal", "breach"));
            double retreatFrom = double.MaxValue, retreatTo = 0, warnTo = 0;
            int steers = 0;
            c.AirRetreatNow = (u, gn) => u == 5 && gn == "A" && game.Time >= retreatFrom && game.Time < retreatTo;
            c.AirThreatNow = (u, gn) => u == 5 && gn == "A" && game.Time >= retreatFrom && game.Time < warnTo;
            c.AirSteerEw = (u, r, m) => { steers++; return "no_cover"; };   // the cover is gone with the threat
            c.AirReleaseEw = u => { };
            if (StrikeController.IsErr(c.Plan(C("group", "A")))) return "plan";
            if (StrikeController.IsErr(c.Launch(C("group", "A")))) return "launch";
            for (int i = 0; i < 3000 && St(c) != "to_station" && St(c) != "on_station"; i++) { game.Advance(1); c.Tick(game.Time); }
            string before = St(c);
            if (before != "to_station" && before != "on_station") return "flying " + before;
            double t0 = game.Time + 1;
            retreatFrom = t0; retreatTo = t0 + 40; warnTo = t0 + 80;
            var gr = c.Groups[0];
            string stateAt = null; bool sawClearWait = false, sawJsonWhy = false; double backAt = -1;
            for (int s = 0; s < 400 && backAt < 0; s++)
            {
                game.Advance(1); c.Tick(game.Time);
                double rel = game.Time - t0;
                string st = St(c);
                if (rel >= 5 && rel < 40 && st != "retreat") return "retreat while the threat stands, t=" + rel + " " + st;
                if (rel >= 45 && rel < 138 && st != "retreat") return "clear_wait must keep the retreat state, t=" + rel + " " + st;
                if (st == "retreat" && gr.EwExec[0].Why == "clear_wait") sawClearWait = true;
                if (rel > 50 && rel < 60 && !sawJsonWhy)
                {
                    var g0 = (Dictionary<string, object>)Arr(Obj(c.ViewJson(20))["groups"])[0];
                    var e0 = (Dictionary<string, object>)Arr(g0["ew"])[0];
                    sawJsonWhy = e0["why"] as string == "clear_wait";
                }
                if (st == "to_station" || st == "on_station" || st == "jamming") backAt = rel;
                stateAt = st;
            }
            if (backAt < 0) return "the jammer never returned (" + stateAt + ")";
            if (backAt < 139 || backAt > 140) return "back to the station at " + backAt + " s after the start; expected 139 (the last threat tick at 79 s + 60 s)";
            if (!sawClearWait || !sawJsonWhy) return "clear_wait is visible " + sawClearWait + " " + sawJsonWhy;
            if (!log.Any(l => l.Contains("strike ew clear_wait uid=5 left_s="))) return "clear_wait log " + string.Join(" | ", log.Where(l => l.Contains(" ew ")));
            if (!log.Any(l => l.Contains("strike ew to_station uid=5") && l.Contains(" from=retreat clear_s=6"))) return "to_station from retreat log " + string.Join(" | ", log.Where(l => l.Contains("to_station")));
            if (log.Count(l => l.Contains("strike ew clear_wait route uid=5")) != 1) return "one order to the wait point " + log.Count(l => l.Contains("clear_wait route"));
            if (steers == 0) return "the steer was asked";
            return null;
        }
    }
}
