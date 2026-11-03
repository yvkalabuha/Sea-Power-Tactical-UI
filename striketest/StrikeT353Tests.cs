using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-353 stand sections "t353 <name>": the missiles of a carrier that was shot down keep flying and hit (r3 of T-341: a false "arrive kind=shot_down" at the loss of the carrier and the result before the hits).
    internal static class StrikeT353Tests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static readonly string[] Names = { "msl-cache", "late-result-math", "carrier-lost-r3", "carrier-lost-old", "alive-carrier-same", "late-result" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "breach";
            try
            {
                switch (n)
                {
                    case "msl-cache": return StrikeMslCache.SelfTest();
                    case "late-result-math": return StrikeExecMath.SelfTest();
                    case "carrier-lost-r3": return CarrierLost();
                    case "carrier-lost-old": return CarrierLostOld();
                    case "alive-carrier-same": return AliveSame();
                    case "late-result": return LateResult();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private sealed class Outcome
        {
            public int Arrived, ShotDown, Fired, Hits; public string State; public List<string> Log; public double DoneT = -1, LastArrT = -1, FirstShotDownT = -1; public string Err;
            public List<(double t, int tgt, int uid, double dist)> Arr;
        }

        private static int Cnt(List<string> l, string sub) { return l.Count(x => x.Contains(sub)); }

        /// <summary>Two carriers, one target; loseAt[uid] = seconds after the order at which the carrier dies (absent = lives); shootSeq = the missiles (by launch number) the defence shoots down; lateBy: the game refuses the order until this long after the normal one.</summary>
        private static Outcome Scenario(Dictionary<int, double> loseAt, int[] shootSeq, bool oldAdapter = false, double refuseUntilAfterFirst = 0, double firstIssue = 0)
        {
            var game = new StrikeFakeGame(); var log = new List<string>();
            for (int i = 1; i <= 2; i++) game.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            game.AddKidd(100, 41.0, 10.0);
            game.NoMslCache = oldAdapter;
            foreach (int q in shootSeq) game.ShootDownSeq.Add(q);
            var o = new Outcome { Log = log, Arr = game.Arrivals };
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            if (StrikeController.IsErr(c.Plan(C("group", "A")))) { o.Err = "plan"; return o; }
            if (refuseUntilAfterFirst > 0) game.IssueHook = (uid, n) => game.Time < firstIssue + refuseUntilAfterFirst ? "neutral_near" : null;
            if (StrikeController.IsErr(c.Launch(C("group", "A")))) { o.Err = "launch"; return o; }
            game.FateMap[100] = "damaged"; game.FateHits[100] = 4;
            var gr = c.Groups[0];
            var lost = new HashSet<int>();
            for (int s = 0; s < 2500 && gr.ExecStatus == null; s++)
            {
                game.Advance(1); c.Tick(game.Time);
                foreach (var kv in loseAt)
                    if (!lost.Contains(kv.Key) && game.IssueT.ContainsKey(kv.Key) && game.Time >= game.IssueT[kv.Key] + kv.Value) { game.LoseCarrier(kv.Key); lost.Add(kv.Key); }
            }
            if (gr.ExecStatus == null) { o.Err = "no result"; return o; }
            foreach (var tr in gr.Results) { o.Arrived += tr.Arrived; o.ShotDown += tr.ShotDown; o.Fired += tr.Fired; o.Hits = tr.Hits; o.State = tr.State; }
            o.DoneT = gr.ResultT;
            o.LastArrT = game.Arrivals.Count == 0 ? -1 : game.Arrivals.Max(a => a.t);
            return o;
        }

        private static string CarrierLost()
        {
            var lose = new Dictionary<int, double> { { 1, 30 }, { 2, 50 } };
            var o = Scenario(lose, new[] { 5 });
            if (o.Err != null) return o.Err;
            int total = o.Arr.Count;
            if (total < 4) return "too few missiles " + total;
            if (o.ShotDown != 1 || o.Arrived != total - 1) return "arrived " + o.Arrived + " shot_down " + o.ShotDown + " of " + total + ", expected shot_down 1";
            if (Cnt(o.Log, "kind=shot_down") != 1) return "shot_down lines " + Cnt(o.Log, "kind=shot_down");
            if (Cnt(o.Log, "kind=at_target") != total - 1) return "at_target lines " + Cnt(o.Log, "kind=at_target");
            if (o.State != "damaged" || o.Hits != 4) return "fate " + o.State + " " + o.Hits;
            if (o.DoneT < o.LastArrT) return "the result (" + o.DoneT + ") is before the last arrival (" + o.LastArrT + ")";
            // no arrival is logged at the moment of the loss: the first arrival is at its flight time
            double firstArr = o.Arr.Min(a => a.t);
            foreach (string l in o.Log.Where(x => x.Contains("strike arrive ")))
            {
                double t = double.Parse(Field(l, " t="), CultureInfo.InvariantCulture);
                if (t < firstArr - 0.5 && !l.Contains("shot_down")) return "arrival before the first real one: " + l;
            }
            return null;
        }

        private static string CarrierLostOld()
        {
            var o = Scenario(new Dictionary<int, double> { { 1, 30 }, { 2, 50 } }, new[] { 5 }, true);
            if (o.Err != null) return o.Err;
            // the control proves the scenario reproduces r3: without the cache the missiles of the lost carriers are all "shot down" at the loss and the result is early
            if (o.ShotDown < 3) return "control: the old adapter should lose the missiles, shot_down " + o.ShotDown;
            return null;
        }

        private static string AliveSame()
        {
            var o = Scenario(new Dictionary<int, double>(), new[] { 5 });
            var l = Scenario(new Dictionary<int, double> { { 1, 30 }, { 2, 50 } }, new[] { 5 });
            if (o.Err != null) return o.Err;
            if (l.Err != null) return l.Err;
            if (o.Arrived != l.Arrived || o.ShotDown != l.ShotDown || o.Fired != l.Fired || o.Hits != l.Hits || o.State != l.State) return "alive " + o.Arrived + "/" + o.ShotDown + " lost " + l.Arrived + "/" + l.ShotDown;
            if (o.ShotDown != 1) return "alive carriers: shot_down " + o.ShotDown;
            return null;
        }

        private static string LateResult()
        {
            var normal = Scenario(new Dictionary<int, double>(), new int[0]);
            if (normal.Err != null) return normal.Err;
            // the game refuses the order for 60 s after the normal one: the rows are late, the missiles arrive after TArrive + ResultDelayS
            double firstGameT = FirstIssueT();
            var o = Scenario(new Dictionary<int, double>(), new int[0], false, 60, firstGameT);
            if (o.Err != null) return o.Err;
            int total = o.Arr.Count;
            if (total < 4) return "too few missiles " + total;
            if (o.Arrived != total || o.ShotDown != 0) return "late: arrived " + o.Arrived + " shot_down " + o.ShotDown + " of " + total;
            if (o.DoneT < o.LastArrT) return "late: the result (" + o.DoneT + ") before the last arrival (" + o.LastArrT + ")";
            if (o.State != "damaged" || o.Hits != 4) return "late: fate " + o.State + " " + o.Hits;
            if (!o.Log.Any(l => l.Contains(" late_s=") && !l.EndsWith("late_s=0.0", StringComparison.Ordinal))) return "the scenario is not late";
            return null;
        }

        private static double FirstIssueT()
        {
            var game = new StrikeFakeGame(); var log = new List<string>();
            for (int i = 1; i <= 2; i++) game.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            game.AddKidd(100, 41.0, 10.0);
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            c.Plan(C("group", "A")); c.Launch(C("group", "A"));
            for (int s = 0; s < 2500 && game.IssueT.Count == 0; s++) { game.Advance(1); c.Tick(game.Time); }
            return game.IssueT.Count == 0 ? 0 : game.IssueT.Values.Min();
        }

        private static string Field(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
            int j = i;
            while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' || line[j] == '-')) j++;
            return line.Substring(i, j - i);
        }
    }
}
