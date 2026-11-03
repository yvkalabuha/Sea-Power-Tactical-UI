using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-346 stand sections "t346 *": D2 (the next wave and the plan after the main target is sunk, spread mode). STRIKE_TRACE=1 prints the journal.
    internal static class StrikeT346Tests
    {
        public static readonly string[] Names = { "wave-survivor", "plan-promote" };
        private static readonly bool Trace = Environment.GetEnvironmentVariable("STRIKE_TRACE") != null;

        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "breach";
            try
            {
                switch (n)
                {
                    case "wave-survivor": return WaveSurvivor();
                    case "plan-promote": return PlanPromote();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        // the world of QA 09: the planes, the Kidd (100) and the Perry (101) in the spread set, the main one is 100
        private static StrikeFakeGame World(int planes, out StrikeController c, List<string> log)
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 8, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            var p = g.AddKidd(101, 40.75, 10.0);
            p.Ad = new StrikeAd { Ksub = 3.11741447, Ssub = 9.54761791, Ksup = 1.88876057, Ssup = 4.40476179, Source = "cache" };
            p.DisplacementT = 4000; p.ClassName = "Perry"; p.SamMaxNm = 40;
            c = new StrikeController(g, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Where(a => a.ObjType == "Aircraft").Select(a => a.Uid).ToArray()));
            c.SetGoal(C("group", "A", "goal", "breach"));
            c.Target(C("group", "A", "target_id", 100, "mode", "spread", "track_ids", new[] { 101 }));
            return g;
        }

        private static void Run(StrikeFakeGame g, StrikeController c, double secs, Func<bool> until = null)
        {
            for (double s = 0; s < secs; s++) { g.Advance(1); c.Tick(g.Time); if (until != null && until()) return; }
        }

        private static void Sink(StrikeFakeGame g, int id) { g.FateMap[id] = "sunk"; g.Fleet.RemoveAll(s => s.TrackId == id); }

        private static string WaveSurvivor()
        {
            var log = new List<string>(); StrikeController c;
            var game = World(1, out c, log);
            var gr = c.Groups[0];
            Run(game, c, 5);
            string r = c.Plan(C("group", "A"));
            if (r != null && r.StartsWith("!", StringComparison.Ordinal)) return "plan " + r;
            if (gr.Variants == null || gr.Variants.Count == 0) return "variants";
            c.Launch(C("group", "A"));
            // the main target sinks; the wave never fired at the second ship
            Run(game, c, 300, () => game.IssueLog.Count > 0);
            Sink(game, 100);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 3000, () => log.Any(l => l.Contains("strike wave A")));
            if (Trace) foreach (string l in log.Where(l => l.Contains("strike"))) Console.WriteLine(l);
            if (!log.Any(l => l.Contains("strike wave A wave=2") && l.Contains("main=101"))) return "wave 2 by the survivor: " + string.Join(" | ", log.Where(l => l.Contains("strike wave")));
            if (log.Any(l => l.Contains("strike wave A none"))) return "no 'none'";
            Run(game, c, 30);
            if (gr.Auto.StopWhy == "no_targets") return "auto stopped no_targets";
            if (gr.Target.MainId != 101) return "main " + gr.Target.MainId;
            return null;
        }

        private static string PlanPromote()
        {
            var log = new List<string>(); StrikeController c;
            var game = World(2, out c, log);
            var gr = c.Groups[0];
            Run(game, c, 5);
            Sink(game, 100);   // the main target is sunk outside of a wave
            string r = c.Plan(C("group", "A"));
            if (r != null && r.StartsWith("!", StringComparison.Ordinal)) return "plan after the loss of the main target: " + r;
            if (gr.Target.MainId != 101) return "promoted main " + gr.Target.MainId;
            if (gr.Variants == null || gr.Variants.Count == 0) return "plan by the survivor";
            if (!log.Any(l => l.Contains("strike target A promote main=101 from=100 why=main_gone"))) return "promote line: " + string.Join(" | ", log.Where(l => l.Contains("promote")));
            if (gr.Target.Mode != "single") return "mode " + gr.Target.Mode;
            // single mode: the old behaviour
            var log2 = new List<string>(); StrikeController c2;
            var game2 = World(2, out c2, log2);
            c2.Target(C("group", "A", "target_id", 100, "mode", "single"));
            Run(game2, c2, 5);
            Sink(game2, 100);
            r = c2.Plan(C("group", "A"));
            if (r == null || !r.StartsWith("!no_target", StringComparison.Ordinal)) return "single: no_target " + r;
            // all sunk
            var log3 = new List<string>(); StrikeController c3;
            var game3 = World(2, out c3, log3);
            Run(game3, c3, 5);
            Sink(game3, 100); Sink(game3, 101);
            r = c3.Plan(C("group", "A"));
            if (r == null || !r.StartsWith("!no_target", StringComparison.Ordinal)) return "all sunk: no_target " + r;
            c3.SetAuto(C("group", "A", "on", true));
            Run(game3, c3, 20);
            if (c3.Groups[0].Auto.StopWhy != "no_targets") return "auto no_targets " + c3.Groups[0].Auto.StopWhy;
            return null;
        }
    }
}
