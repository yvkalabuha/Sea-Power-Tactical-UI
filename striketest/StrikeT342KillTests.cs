using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase A stand sections "t342 <name>" (Kill).
    internal static class StrikeT342KillTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "hit-log", "kill-log", "goal", "view", "hit-filter" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "hit-log": return HitLog();
                    case "kill-log": return KillLog();
                    case "goal": return Goal();
                    case "view": return View();
                    case "hit-filter": return StrikeHitFilter.SelfTest();   // T-363
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

        private static StrikeHitSeen Hit(int tgt, double t, double ciws = -1)
        {
            return new StrikeHitSeen { TrackId = tgt, WeaponId = 5000 + (int)t, Ammo = "usn_agm-84c", Power = 45, T = t, CiwsDmg = ciws };
        }

        private static StrikeFakeGame Big()
        {
            var game = StrikeT341CtlTests.Game();
            game.AddPlane(5, "usn_a-6e", "usn_agm-84c", 4, 39.35, 10); game.AddPlane(6, "usn_a-6e", "usn_agm-84c", 4, 39.3, 10);
            return game;
        }

        /// <summary>F1: the own hits are logged (strike hit) and become the prior loss of the ship: the next plan counts B1 = B - 0.7 * sum(L) and needs fewer missiles.</summary>
        private static string HitLog()
        {
            var game = Big(); var log = new List<string>();
            var c = Ready(game, log, new[] { 1, 2, 4, 5, 6 });
            c.Tick(1001);
            var fresh = Obj(c.Plan(C("group", "A")));
            int mFresh = (int)(double)((Dictionary<string, object>)Arr(fresh["variants"])[0])["missiles"];
            game.HitQueue.Add(Hit(100, 1002)); game.HitQueue.Add(Hit(100, 1003, 0.4));
            c.Tick(1004);
            var hl = log.Where(l => l.StartsWith("[Strike] strike hit ", StringComparison.Ordinal)).ToList();
            if (hl.Count != 2 || !hl[0].Contains(" tgt=100") || !hl[0].Contains(" msl=6002") || !hl[0].Contains(" ammo=usn_agm-84c") || !hl[0].Contains(" dud=0") || !hl[0].Contains(" ciws=-1") || !hl[0].Contains(" t=1002") || !hl[1].Contains(" ciws=0.4")) return "hit lines " + string.Join(" | ", hl);
            if (game.HitQueue.Count != 0) return "the queue is drained";
            log.Clear();
            var again = Obj(c.Plan(C("group", "A")));
            var kidd = game.Fleet.First(s => s.TrackId == 100);
            double lossOne = 2 * 45 * 1.1 * 1.25;
            if (kidd.PriorHits != 2 || Math.Abs(kidd.PriorLoss - 2 * lossOne) > 1e-6) return "prior " + kidd.PriorHits + " " + kidd.PriorLoss;
            var kp = log.First(l => l.StartsWith("[Strike] strike killplan ", StringComparison.Ordinal));
            double b1 = 318.798 - StrikeCalib.PriorHitWeight * 2 * lossOne;
            if (!kp.Contains(" B1=" + b1.ToString("0.0", CultureInfo.InvariantCulture)) || !kp.Contains(" B=318.8")) return "B1 " + kp;
            int mAfter = (int)(double)((Dictionary<string, object>)Arr(again["variants"])[0])["missiles"];
            if (!(mAfter < mFresh)) return "fewer missiles " + mAfter + " vs " + mFresh;
            // a mission reset forgets the hits
            c.ResetMission();
            c.Plan(C("group", "A"));
            if (game.Fleet.First(s => s.TrackId == 100).PriorHits != 0) return "reset clears the hits";
            // the counter off: no hits arrive, the plan is the plan of the full ship
            game.HitsOn = false; game.HitQueue.Add(Hit(100, 1010)); c.Tick(1011);
            c.Plan(C("group", "A"));
            if (game.Fleet.First(s => s.TrackId == 100).PriorHits != 0) return "ledger off";
            return null;
        }

        /// <summary>F2: the first observation of a sunk target writes one strike kill line (wave, hits before / in the wave, weakened); a counter that is off writes -1 and ledger=off; a ship outside the groups is not written.</summary>
        private static string KillLog()
        {
            var game = Big(); var log = new List<string>();
            var c = Ready(game, log, new[] { 1, 2, 4, 5, 6 });
            var g = c.Groups[0];
            g.Wave = 2;
            g.Timeline = new StrikeTimeline { PlanId = 1, Status = "draft", StartT = 1100 };
            game.HitQueue.Add(Hit(100, 1000)); game.HitQueue.Add(Hit(100, 1010, 0.3)); game.HitQueue.Add(Hit(100, 1020));
            game.HitQueue.Add(Hit(100, 1110)); game.HitQueue.Add(Hit(100, 1120, 0.5));
            game.Time = 1130; c.Tick(1130);
            if (log.Any(l => l.Contains("strike kill "))) return "no kill before the fate";
            game.FateMap[100] = "sunk"; game.Time = 1140; c.Tick(1140);
            var kl = log.Where(l => l.Contains("strike kill ")).ToList();
            if (kl.Count != 1) return "one kill line " + kl.Count;
            foreach (string k in new[] { " tgt=100", " cls=usn_ddg_kidd", " t=1140", " grp=A", " wave=2", " hits_before=3", " hits_wave=2", " weakened=2", " ledger=on" })
                if (!kl[0].Contains(k)) return "kill field " + k + ": " + kl[0];
            game.Time = 1150; c.Tick(1150); game.Time = 1160; c.Tick(1160);
            if (log.Count(l => l.Contains("strike kill ")) != 1) return "not written twice";
            // a ship outside every group and outside every plan is not watched
            game.AddKidd(200); game.FateMap[200] = "sunk"; game.Time = 1170; c.Tick(1170);
            if (log.Any(l => l.Contains("strike kill ") && l.Contains(" tgt=200"))) return "a ship outside the groups";
            // the counter is off: hits are not known
            var game2 = Big(); var log2 = new List<string>();
            var c2 = Ready(game2, log2, new[] { 1, 2 });
            game2.HitsOn = false; game2.FateMap[100] = "sunk"; game2.Time = 1200; c2.Tick(1200);
            var k2 = log2.FirstOrDefault(l => l.Contains("strike kill "));
            if (k2 == null || !k2.Contains(" hits_before=-1") || !k2.Contains(" hits_wave=-1") || !k2.Contains(" weakened=-1") || !k2.Contains(" ledger=off") || !k2.Contains(" wave=1")) return "ledger off line " + k2;
            return null;
        }

        /// <summary>F3: strike_goal. A plan becomes stale (stale_why = goal) without a recount and without a launch; not while executing; the save keeps breach; an old file without goal = sink.</summary>
        private static string Goal()
        {
            var game = Big(); var log = new List<string>();
            var c = Ready(game, log, new[] { 1, 2, 4, 5, 6 });
            if (c.SetGoal(C("goal", "breach")) != "!bad_args|group") return "no group arg";
            if (c.SetGoal(C("group", "Z", "goal", "breach")) != "!no_group") return "no group";
            if (c.SetGoal(C("group", "A", "goal", "wide")) != "!bad_args|goal" || c.SetGoal(C("group", "A")) != "!bad_args|goal") return "bad goal";
            c.Plan(C("group", "A"));
            var g = c.Groups[0];
            int plans = log.Count(l => l.StartsWith("[Strike] strike plan A", StringComparison.Ordinal));
            string r = c.SetGoal(C("group", "A", "goal", "breach"));
            if (StrikeController.IsErr(r) || Obj(r)["goal"] as string != "breach" || Obj(r)["plan_status"] as string != "stale") return "set goal ack " + r;
            if (g.Goal != "breach" || g.Timeline.Status != "stale" || g.Timeline.StaleWhy != "goal") return "stale by goal " + g.Timeline.Status + " " + g.Timeline.StaleWhy;
            if (log.Count(l => l.StartsWith("[Strike] strike plan A", StringComparison.Ordinal)) != plans || game.IssueLog.Count != 0) return "no recount, no launch";
            if (!StrikeController.IsErr(c.Launch(C("group", "A")))) return "no launch of a stale plan";
            // the same goal again changes nothing
            string same = c.SetGoal(C("group", "A", "goal", "breach"));
            if (StrikeController.IsErr(same) || log.Count(l => l.Contains("strike goal A")) != 1) return "same goal";
            // executing: refused
            g.Timeline = new StrikeTimeline { PlanId = 9, Status = "executing" };
            if (c.SetGoal(C("group", "A", "goal", "sink")) != "!executing" || g.Goal != "breach") return "executing";
            g.Timeline = null;
            // the save keeps the goal
            string se = StrikeSave.SelfTest();
            if (se != null) return "save selftest " + se;
            string json = c.Export();
            if (json == null || !json.Contains("\"goal\":\"breach\"")) return "export " + json;
            var c2 = new StrikeController(game, new StrikeParams(), s => { });
            string err;
            if (c2.Import(json, out err) != "n=1" || c2.Groups[0].Goal != "breach") return "import " + err;
            string old = json.Replace("\"goal\":\"breach\",", "");
            var c3 = new StrikeController(game, new StrikeParams(), s => { });
            if (old == json || c3.Import(old, out err) != "n=1" || c3.Groups[0].Goal != "sink") return "an older file = sink " + err;
            return null;
        }

        /// <summary>F4: the ui_state section carries goal, red, p_sunk, h1, m_sink, calib.dmg_bonus and the ew why; it never carries the hidden state of a ship (limit / max_limit).</summary>
        private static string View()
        {
            var game = Big(); var log = new List<string>();
            var c = Ready(game, log, new[] { 1, 2, 4, 5, 6 });
            game.AddEw(7, 39.2, 10);
            c.Group(C("op", "add", "group", "A", "uids", new[] { 7 }));
            c.Plan(C("group", "A"));
            var root = Obj(c.ViewJson(20));
            var grp = (Dictionary<string, object>)Arr(root["groups"])[0];
            if (grp["goal"] as string != "sink") return "group goal";
            var v0 = (Dictionary<string, object>)Arr(((Dictionary<string, object>)grp["plan"])["variants"])[0];
            if (v0["goal"] as string != "sink" || !v0.ContainsKey("red") || (bool)v0["red"]) return "variant goal / red " + v0["kind"];
            var ship = (Dictionary<string, object>)Arr(((Dictionary<string, object>)grp["target"])["ships"])[0];
            foreach (string k in new[] { "h", "h1", "m_sink", "p_sunk", "budget_left" }) if (!ship.ContainsKey(k) || ship[k] == null) return "target ship field " + k;
            if ((double)ship["h"] != 3 || (double)ship["h1"] != 4 || (double)ship["p_sunk"] < 0.8 || (double)ship["budget_left"] < 300) return "target ship values";
            var calib = (Dictionary<string, object>)root["calib"];
            if (Math.Abs((double)calib["dmg_bonus"] - 0.25) > 1e-9 || calib["bonus_src"] as string != "game" || (double)calib["kill_threshold"] != 0.8 || !(bool)calib["kill_estimate"] || calib["ledger"] as string != "on") return "calib fields";
            var ew = Arr(grp["ew"]);
            if (!grp.ContainsKey("ew")) return "ew section";   // the entries (and their why) exist from the launch: t342 ew-clear
            // the red variant and breach
            var game2 = StrikeT341CtlTests.Game(); var c2 = Ready(game2, new List<string>(), new[] { 1, 2 });
            c2.Plan(C("group", "A"));
            var g2 = (Dictionary<string, object>)Arr(Obj(c2.ViewJson(20))["groups"])[0];
            var w0 = (Dictionary<string, object>)Arr(((Dictionary<string, object>)g2["plan"])["variants"])[0];
            if (w0["kind"] as string != "damage_only" || !(bool)w0["red"]) return "red variant " + w0["kind"];
            c2.SetGoal(C("group", "A", "goal", "breach")); c2.Plan(C("group", "A"));
            var b0 = (Dictionary<string, object>)Arr(Obj(c2.ViewJson(20))["groups"])[0];
            var bship = (Dictionary<string, object>)Arr(((Dictionary<string, object>)b0["target"])["ships"])[0];
            if (bship["h"] != null || bship["m_sink"] != null || bship["p_sunk"] != null) return "breach has no kill fields";
            // no hidden state anywhere
            string json = c.ViewJson(20) + c2.ViewJson(20);
            if (json.Contains("limit") || json.Contains("max_limit") || json.Contains("last_dmg")) return "hidden state in ui_state";
            return null;
        }
    }
}
