using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase A stand sections "t342 <name>" (Exec): the warm-up at the plan's altitude and speed, the range bands and the rewarm, the speed of the run in.
    internal static class StrikeT342ExecTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static readonly string[] Names = { "warm-alt", "rewarm", "speed" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "warm-alt": return WarmAlt();
                    case "rewarm": return Rewarm();
                    case "speed": return Speed();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private static StrikeFakeGame World(int planes)
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            return g;
        }

        private static StrikeController Ctl(StrikeFakeGame g, List<string> log, bool breach = true)
        {
            var c = new StrikeController(g, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            if (breach) c.SetGoal(C("group", "A", "goal", "breach"));
            return c;
        }

        private static void Run(StrikeFakeGame g, StrikeController c, double secs, Func<bool> until = null)
        {
            for (double s = 0; s < secs; s++) { g.Advance(1); c.Tick(g.Time); if (until != null && until()) return; }
        }

        /// <summary>C1: the first warm-up asks the game at the launch altitude and speed of the plan (a high-altitude Tu-22M2 AS-4), not at 500 ft.</summary>
        private static string WarmAlt()
        {
            var game = World(1); var log = new List<string>();
            var a = game.Air[0]; a.Type = "wp_tu-22m2"; a.CruiseKn = 552;
            var m = a.Missiles[0]; m.Ammo = "wp_as-4"; m.MinLaunchAltFt = 30000; m.SpeedKn = 1100; m.MaxNm = 200;
            game.SimTof = true; game.SimTofPerNm = 4;
            var c = Ctl(game, log);
            Run(game, c, 4);
            if (game.TofArgs.Count == 0) return "the warm-up asked the game";
            foreach (string s in game.TofArgs) if (s != "30000:552") return "warm-up at " + s;
            // the plan finds the warmed value in the cache or the band (it does not ask the game for the ranges that were warmed)
            int calls = game.TofCalls;
            c.Plan(C("group", "A"));
            if (game.TofCalls - calls > 2) return "the plan used the warm values: " + (game.TofCalls - calls);
            return null;
        }

        /// <summary>C2: a held row whose target went far from the planned range asks for a warm-up at the range it is at; the step itself never calls the game; the band of that range is used afterwards.</summary>
        private static string Rewarm()
        {
            var game = World(2); var log = new List<string>();
            game.SimTof = true; game.SimTofPerNm = 6.7;   // the rough formula of the stand is 6.67 s per nm: scale 1
            var c = Ctl(game, log);
            Run(game, c, 4);   // the first warm-up
            string ammo = "usn_agm-84c";
            double b0 = c.Tofs.Scale(ammo, 90);
            if (c.Tofs.NeedRewarm(ammo, 22) || Math.Abs(b0 - 1.0) > 0.05) return "the first warm-up: scale " + b0;
            game.SimTofPerNm = 7.5;   // the flight at the new range is 12 % longer than the rough formula
            string e = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(e)) return "plan " + e;
            if (StrikeController.IsErr(c.Launch(C("group", "A")))) return "launch";
            var gr = c.Groups[0];
            foreach (var r in gr.Timeline.Exec) { r.State = "held"; r.Why = "test"; r.NextTryT = 1e9; r.Sent = "run_in"; }
            double dist = AswMath.DistNm(new GeoPt(game.Air[0].Lat, game.Air[0].Lon), new GeoPt(41.0, 10.0));
            if (Math.Abs(dist - gr.Timeline.Exec[0].LaunchNm) < 15) return "the stand: the aircraft is far from the launch range " + dist;
            if (!c.Tofs.NeedRewarm(ammo, dist)) return "no ratio near the range yet";
            int calls = game.TofCalls;
            game.Time += 1; c.Tick(game.Time);
            var rw = log.Where(l => l.Contains("strike tof_rewarm ")).ToList();
            if (rw.Count == 0 || !rw[0].Contains(" ammo=" + ammo) || !rw[0].Contains(" nm=" + ((int)dist).ToString(CultureInfo.InvariantCulture))) return "tof_rewarm line " + (rw.Count > 0 ? rw[0] : "none");
            if (game.TofCalls - calls != rw.Count) return "one game call per warm-up " + (game.TofCalls - calls) + " vs " + rw.Count;
            if (c.Tofs.NeedRewarm(ammo, dist)) return "the range is warmed";
            double band = c.Tofs.Scale(ammo, dist);
            if (Math.Abs(band / b0 - 7.5 / 6.7) > 0.03 || Math.Abs(c.Tofs.Scale(ammo, 30) - b0) > 0.01) return "bands " + band + " " + c.Tofs.Scale(ammo, 30) + " b0 " + b0;
            // the tick does not ask again for the same bucket
            int calls2 = game.TofCalls;
            Run(game, c, 5);
            if (game.TofCalls != calls2) return "asked again " + (game.TofCalls - calls2);
            return null;
        }

        /// <summary>C3: one SetSpeed per aircraft at the run in (the planned speed), RestoreSpeed after the last row and after Cancel; an error is logged, the strike goes on.</summary>
        private static string Speed()
        {
            var game = World(2); var log = new List<string>();
            var c = Ctl(game, log);
            string e = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(e)) return "plan " + e;
            if (StrikeController.IsErr(c.Launch(C("group", "A")))) return "launch";
            var gr = c.Groups[0];
            foreach (var r in gr.Timeline.Exec) if (r.LaunchKn != 450) return "row LaunchKn " + r.LaunchKn;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.ExecStatus != "done") return "the strike did not finish";
            var sets = game.SpeedCalls.Where(s => s.StartsWith("set ", StringComparison.Ordinal)).ToList();
            var rest = game.SpeedCalls.Where(s => s.StartsWith("restore ", StringComparison.Ordinal)).ToList();
            int aircraft = gr.LastExec.Select(r => r.Uid).Distinct().Count();
            if (sets.Count != aircraft || sets.Any(s => !s.EndsWith(" 450", StringComparison.Ordinal))) return "one SetSpeed per aircraft: " + string.Join(",", sets);
            if (rest.Count != aircraft) return "one RestoreSpeed per aircraft: " + string.Join(",", game.SpeedCalls);
            if (game.SpeedCalls.IndexOf(sets[0]) > game.SpeedCalls.IndexOf(rest[0])) return "set before restore";
            if (!log.Any(l => l.Contains("strike speed uid=1 kn=450") && l.Contains("-> ok")) || !log.Any(l => l.Contains("strike speed uid=1 restore -> ok"))) return "speed log " + string.Join(" | ", log.Where(l => l.Contains("strike speed")));
            if (gr.LastExec.Any(r => r.SpeedSent != "restored")) return "SpeedSent " + string.Join(",", gr.LastExec.Select(r => r.SpeedSent));
            // an error: logged, the strike runs on with the own speed model
            var game2 = World(2); var log2 = new List<string>(); game2.SpeedErr = "unsupported";
            var c2 = Ctl(game2, log2);
            c2.Plan(C("group", "A")); c2.Launch(C("group", "A"));
            var g2 = c2.Groups[0];
            Run(game2, c2, 1500, () => g2.ExecStatus != null);
            if (g2.ExecStatus != "done") return "an error of SetSpeed must not stop the strike " + g2.ExecStatus;
            if (!log2.Any(l => l.Contains("strike speed uid=1 kn=450") && l.Contains("-> unsupported"))) return "error logged";
            if (game2.SpeedCalls.Count(s => s.StartsWith("set 1", StringComparison.Ordinal)) != 3 || !log2.Any(l => l.Contains("gave up"))) return "three tries, then the own speed " + string.Join(",", game2.SpeedCalls);
            if (game2.SpeedCalls.Any(s => s.StartsWith("restore", StringComparison.Ordinal))) return "nothing to restore after an error";
            // Cancel after the run in began: the throttle goes back at once
            var game3 = World(2); var log3 = new List<string>();
            var c3 = Ctl(game3, log3);
            c3.Plan(C("group", "A")); c3.Launch(C("group", "A"));
            Run(game3, c3, 1500, () => game3.SpeedCalls.Count > 0);
            if (game3.SpeedCalls.Count == 0) return "no run in in the cancel scenario";
            string cr = c3.Cancel(C("group", "A"));
            if (StrikeController.IsErr(cr)) return "cancel " + cr;
            if (!game3.SpeedCalls.Any(s => s.StartsWith("restore ", StringComparison.Ordinal))) return "cancel restores the speed " + string.Join(",", game3.SpeedCalls);
            // Release / disband of a flying member: its throttle comes back at once (review A)
            var game4 = World(2); var log4 = new List<string>();
            var c4 = Ctl(game4, log4);
            c4.Plan(C("group", "A")); c4.Launch(C("group", "A"));
            Run(game4, c4, 1500, () => game4.SpeedCalls.Any(s => s.StartsWith("set ", StringComparison.Ordinal)));
            string setUid = game4.SpeedCalls.FirstOrDefault(s => s.StartsWith("set ", StringComparison.Ordinal));
            if (setUid == null) return "no run in in the release scenario";
            int ruid = int.Parse(setUid.Split(' ')[1], CultureInfo.InvariantCulture);
            c4.Release(ruid);
            if (!game4.SpeedCalls.Contains("restore " + ruid.ToString(CultureInfo.InvariantCulture))) return "release restores the speed " + string.Join(",", game4.SpeedCalls);
            return null;
        }
    }
}
