using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase A stand sections "t342 <name>" (Plan).
    internal static class StrikeT342PlanTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "plan-log" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "plan-log": return PlanLog();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private static StrikeController Ready(StrikeFakeGame game, List<string> log, int[] uids)
        {
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", uids));
            c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        /// <summary>B4: the plan of a group with the goal sink writes a strike killplan line for every target of every variant; breach writes mode=breach with h=-1.</summary>
        private static string PlanLog()
        {
            var game = StrikeT341CtlTests.Game(); game.AddPlane(5, "usn_a-6e", "usn_agm-84c", 4, 39.35, 10); game.AddPlane(6, "usn_a-6e", "usn_agm-84c", 4, 39.3, 10);
            var log = new List<string>();
            var c = Ready(game, log, new[] { 1, 2, 4, 5, 6 });
            if (c.Groups[0].Goal != "sink") return "a new group has the goal sink: " + c.Groups[0].Goal;
            string r = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(r)) return "plan " + r;
            var kp = log.Where(l => l.StartsWith("[Strike] strike killplan ", StringComparison.Ordinal)).ToList();
            if (kp.Count == 0) return "no killplan " + string.Join(" | ", log);
            string first = kp[0];
            foreach (string k in new[] { " grp=A", " plan=1", " var=0", " tgt=100", " cls=usn_ddg_kidd", " ammo=usn_agm-84c", " mode=sink", " h=3", " h1=4", " M=", " n=", " sigma=", " p_sunk=", " p1=", " B=318.8", " B1=318.8", " dmg_bonus=0.25", " bonus_src=game" })
                if (!first.Contains(k)) return "killplan field " + k + ": " + first;
            var v0 = (Dictionary<string, object>)Arr(Obj(r)["variants"])[0];
            if (v0["kind"] as string != "min_missiles") return "green plan " + v0["kind"];
            // breach: the same group, the v1 model
            if (StrikeController.IsErr(c.SetGoal(C("group", "A", "goal", "breach")))) return "set goal";
            log.Clear();
            c.Plan(C("group", "A"));
            var kb = log.Where(l => l.StartsWith("[Strike] strike killplan ", StringComparison.Ordinal)).ToList();
            if (kb.Count == 0 || !kb[0].Contains(" mode=breach") || !kb[0].Contains(" h=-1") || !kb[0].Contains(" M=-1") || !kb[0].Contains(" p_sunk=-1")) return "breach killplan " + (kb.Count > 0 ? kb[0] : "none");
            // the bonus the game could not give is marked
            game.BonusKnown = false; log.Clear();
            c.SetGoal(C("group", "A", "goal", "sink")); c.Plan(C("group", "A"));
            if (!log.Any(l => l.Contains("strike killplan") && l.Contains(" bonus_src=default") && l.Contains(" dmg_bonus=0.25"))) return "bonus_src default";
            return null;
        }

    }
}
