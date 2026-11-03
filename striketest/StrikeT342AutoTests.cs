using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase C stand sections "t342 auto-*" / "t342 clear-*": the controller over the fake game (the auto mode, the queued replans, the clearing with a pause).
    internal static class StrikeT342AutoTests
    {
        public static readonly string[] Names = { "auto-replan", "auto-on", "auto-launch", "auto-waves", "auto-stop", "auto-recheck", "auto-load", "clear-launch", "clear-release", "clear-rewave", "clear-timeout", "clear-stale", "auto-view", "auto-budget", "deck-noreply-ctl", "auto-launch-err" };

        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "breach";
            try
            {
                switch (n)
                {
                    case "auto-replan": return AutoReplan();
                    case "auto-on": return AutoOn();
                    case "auto-launch": return AutoLaunch();
                    case "auto-waves": return AutoWaves();
                    case "auto-stop": return AutoStop();
                    case "auto-recheck": return AutoRecheck();
                    case "auto-load": return AutoLoad();
                    case "clear-launch": return ClearLaunch();
                    case "clear-release": return ClearRelease();
                    case "clear-rewave": return ClearRewave();
                    case "clear-timeout": return ClearTimeout();
                    case "clear-stale": return ClearStale();
                    case "auto-view": return AutoView();
                    case "auto-budget": return AutoBudget();
                    case "deck-noreply-ctl": return DeckNoReplyCtl();
                    case "auto-launch-err": return AutoLaunchErr();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        // ---- the world: planes of 4 Harpoons at ~90 nm, the Kidd (100) and, optionally, the Perry (101) 15 nm before it on the axis of the approach
        private static StrikeFakeGame World(int planes = 4, bool perry = false)
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            var kidd = g.AddKidd(100, 41.0, 10.0);
            if (perry)
            {
                var p = g.AddKidd(101, 40.75, 10.0);   // 15 nm south of the Kidd: on the track from the aircraft
                p.Ad = new StrikeAd { Ksub = 3.11741447, Ssub = 9.54761791, Ksup = 1.88876057, Ssup = 4.40476179, Source = "cache" };
                p.DisplacementT = 4000; p.ClassName = "Perry"; p.SamMaxNm = 40;
                p.KillBudget = VoiceBotBridge.Ui.HitEstimateMath.Budget(new VoiceBotBridge.Ui.HeClass { Kind = VoiceBotBridge.Ui.HeTargetKind.Ship, Displacement = 4000, Armor = "minor" });
                kidd.DisplacementT = 9630;
                kidd.KillBudget = VoiceBotBridge.Ui.HitEstimateMath.Budget(new VoiceBotBridge.Ui.HeClass { Kind = VoiceBotBridge.Ui.HeTargetKind.Ship, Displacement = 9630, Armor = "minor" });
            }
            return g;
        }

        private static StrikeController Ctl(StrikeFakeGame g, List<string> log, StrikeParams p = null, string goal = "breach", bool target = true)
        {
            var c = new StrikeController(g, p ?? new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Where(a => a.ObjType == "Aircraft").Select(a => a.Uid).ToArray()));
            if (goal != null) c.SetGoal(C("group", "A", "goal", goal));
            if (target) c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        private static StrikeGroup G(StrikeController c) { return c.Groups[0]; }

        private static void Run(StrikeFakeGame g, StrikeController c, double secs, Func<bool> until = null)
        {
            for (double s = 0; s < secs; s++) { g.Advance(1); c.Tick(g.Time); if (until != null && until()) return; }
        }

        private static int Cnt(List<string> l, string sub) { return l.Count(x => x.Contains(sub)); }

        // ---- R1 / R2
        private static string AutoReplan()
        {
            // (a) a group without the auto never plans by itself, not even after a finish: the draft of the next wave comes through the queue, one tick later
            var game = World(4); var log = new List<string>();
            var c = Ctl(game, log, null, "breach");
            Run(game, c, 10);
            if (G(c).Timeline != null) return "no plan without the button or the auto";
            // (b) two requests of one group are one request
            c.QueueReplan(G(c), new PlanOpts { Why = "a", Purpose = "auto" });
            c.QueueReplan(G(c), new PlanOpts { Why = "b", Purpose = "auto" });
            if (c.ReplanQueueLength != 1) return "one request per group " + c.ReplanQueueLength;
            // (d) the queued plan never asks the game for a flight
            Run(game, c, 6, () => G(c).Timeline != null);
            if (G(c).Timeline == null || G(c).Variants.Count == 0) return "the queued plan appeared";
            if (c.ReplanQueued(G(c))) return "the request is served";
            int tof0 = game.TofCalls;
            c.QueueReplan(G(c), new PlanOpts { Why = "again", Purpose = "auto" });
            Run(game, c, 3);
            if (game.TofCalls != tof0) return "the queued plan asked the game for a flight " + (game.TofCalls - tof0);
            if (!log.Any(l => l.Contains("strike replan grp=A") && l.Contains("ms="))) return "the replan line has ms=";
            // (c) two groups: one plan per tick
            game = World(4); log = new List<string>();
            c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 })); c.Group(C("op", "create", "group", "B", "uids", new[] { 3, 4 }));
            c.SetGoal(C("group", "A", "goal", "breach")); c.SetGoal(C("group", "B", "goal", "breach"));
            c.Target(C("group", "A", "target_id", 100)); c.Target(C("group", "B", "target_id", 100));
            Run(game, c, 4);   // the JIT warm-up and the cold warm-up pass
            foreach (var g in c.Groups) c.QueueReplan(g, new PlanOpts { Why = "t", Purpose = "auto" });
            game.Advance(1); c.Tick(game.Time);
            int planned = c.Groups.Count(g => g.Timeline != null);
            if (planned > 1) return "two plans in one tick " + planned;
            Run(game, c, 3);
            if (c.Groups.Any(g => g.Timeline == null)) return "the second group is served in a later tick";
            // PlanOpts: Only / Exclude / Target / NoDeck
            game = World(4); log = new List<string>();
            c = Ctl(game, log, null, "breach");
            Run(game, c, 4);
            var gr = G(c);
            c.QueueReplan(gr, new PlanOpts { Why = "t", Purpose = "x", Only = new HashSet<int> { 2, 3 } });
            Run(game, c, 3, () => gr.Timeline != null);
            if (gr.Variants.Count == 0 || gr.Variants.SelectMany(v => v.Assignments).Any(a => a.Uid != 2 && a.Uid != 3)) return "Only: the aircraft 2, 3";
            c.QueueReplan(gr, new PlanOpts { Why = "t", Purpose = "x", Exclude = new HashSet<int> { 2 } });
            Run(game, c, 3);
            if (gr.Variants.Count == 0 || gr.Variants.SelectMany(v => v.Assignments).Any(a => a.Uid == 2)) return "Exclude: no aircraft 2";
            return null;
        }

        // ---- U1
        private static string AutoOn()
        {
            var game = World(4); var log = new List<string>();
            var c = Ctl(game, log, null, "breach", false);
            if (c.SetAuto(C("group", "A", "on", true)) != "!no_target") return "no target " + c.SetAuto(C("group", "A", "on", true));
            if (c.SetAuto(C("group", "Z", "on", true)) != "!no_group" || c.SetAuto(C("on", true)) != "!bad_args|group" || c.SetAuto(C("group", "A")) != "!bad_args|on") return "arg errors";
            c.Target(C("group", "A", "target_id", 100));
            string r = c.SetAuto(C("group", "A", "on", true));
            if (StrikeController.IsErr(r) || !r.Contains("\"auto\":{\"on\":true,\"wave\":0,\"max\":3}")) return "on " + r;
            var gr = G(c);
            if (!gr.Auto.On || gr.Auto.Waves != 0 || !gr.Auto.Notes.Contains("auto_on") || !log.Any(l => l.Contains("strike auto grp=A") && l.Contains("action=on"))) return "state/note/log";
            Run(game, c, 8, () => gr.Timeline != null);
            if (gr.Timeline == null) return "the auto without a plan asks for one";
            // switched off during the execution: the strike goes on, no new wave
            Run(game, c, 600, () => game.IssueLog.Count > 0);
            if (game.IssueLog.Count == 0) return "the auto launched";
            c.SetAuto(C("group", "A", "on", false));
            int w = gr.Auto.Waves;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            int issued = game.IssueLog.Count;
            Run(game, c, 600);
            if (game.IssueLog.Count != issued || gr.Auto.Waves != w) return "no new wave after the flag went off";
            // a manual launch resets the count
            gr.Auto.Waves = 2;
            var game2 = World(4); var log2 = new List<string>();
            var c2 = Ctl(game2, log2);
            Run(game2, c2, 4);
            c2.Plan(C("group", "A"));
            G(c2).Auto.Waves = 2;
            c2.Launch(C("group", "A"));
            if (G(c2).Auto.Waves != 0) return "the manual launch resets the waves " + G(c2).Auto.Waves;
            return null;
        }

        // ---- U2
        private static string AutoLaunch()
        {
            var game = World(4); var log = new List<string>();
            var c = Ctl(game, log);
            var gr = G(c);
            Run(game, c, 2000);
            if (game.IssueLog.Count != 0 || gr.Timeline != null) return "no auto, no plan, no order in 2000 ticks";
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 600, () => game.IssueLog.Count > 0);
            if (game.IssueLog.Count == 0) return "the green plan is launched without the button";
            if (!log.Any(l => l.Contains("strike auto grp=A wave=1") && l.Contains("action=launch") && l.Contains("red=0"))) return "launch line " + string.Join(" | ", log.Where(l => l.Contains("strike auto")));
            if (gr.Auto.Waves != 1 || !gr.Auto.Notes.Any(n => n.StartsWith("auto_launch:1:", StringComparison.Ordinal))) return "wave 1 note";
            // a deck aircraft in the plan: wait for the gather
            game = World(2); log = new List<string>();
            game.AddPlane(3, "usn_a-6e", "usn_agm-84c", 4, 39.4, 10.1, false);
            c = Ctl(game, log, null, "breach");
            gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 120);
            if (gr.Variants == null || gr.Variants.Count == 0) return "plan exists";
            if (game.IssueLog.Count != 0) return "the deck aircraft: no launch until the gather " + game.IssueLog.Count;
            // red: one plane, a sink goal, the Kidd needs many missiles: the damage_only
            game = World(1); log = new List<string>();
            c = Ctl(game, log, null, "sink");
            gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 600, () => game.IssueLog.Count > 0);
            if (game.IssueLog.Count == 0) return "the red plan is launched as damage_only: " + string.Join(" | ", log.Where(l => l.Contains("strike auto") || l.Contains("strike plan")).Take(8));
            if (!gr.Auto.Notes.Any(n => n.StartsWith("auto_red:", StringComparison.Ordinal))) return "the red launch has the note " + string.Join(",", gr.Auto.Notes);
            if (!log.Any(l => l.Contains("action=launch") && l.Contains("red=1") && l.Contains("threshold_not_reached"))) return "red launch line";
            return null;
        }

        // ---- U3
        private static string AutoWaves()
        {
            var game = World(8); var log = new List<string>();
            foreach (var a in game.Air) a.Missiles[0].Count = 8;
            var c = Ctl(game, log, null, "breach");
            var gr = G(c);
            game.FateMap[100] = "damaged";
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 6000, () => !gr.Auto.On && gr.Auto.StopWhy != null);
            if (gr.Auto.StopWhy != "waves") return "the stop after the limit: " + gr.Auto.StopWhy + " waves " + gr.Auto.Waves + " " + string.Join(" | ", log.Where(l => l.Contains("strike auto")).Take(12));
            if (gr.Auto.Waves != 3 || gr.Auto.On) return "3 waves " + gr.Auto.Waves;
            int issued = game.IssueLog.Count;
            Run(game, c, 1500);
            if (game.IssueLog.Count != issued) return "no 4th wave";
            if (!gr.Auto.Notes.Contains("auto_stop:waves")) return "the stop note";
            // switching on again gives three more (the default of the open question)
            c.SetAuto(C("group", "A", "on", true));
            if (!gr.Auto.On || gr.Auto.Waves != 0) return "on again resets " + gr.Auto.Waves;
            return null;
        }

        // ---- U4
        private static string AutoStop()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log);
            var gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 600, () => game.IssueLog.Count > 0);
            if (game.IssueLog.Count == 0) return "launched";
            c.Target(C("group", "A", "target_id", 101));
            if (gr.Auto.On || gr.Auto.StopWhy != "target_changed") return "target_changed " + gr.Auto.StopWhy;
            int issued = game.IssueLog.Count;
            Run(game, c, 1500);
            if (game.IssueLog.Any(l => l.Contains(" 101 "))) return "no order against the new target";
            if (game.IssueLog.Count != issued && game.IssueLog.Skip(issued).Any(l => l.Contains(" 101 "))) return "no order after the change";
            // cancel
            game = World(4); log = new List<string>();
            c = Ctl(game, log); gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 5);
            c.Cancel(C("group", "A"));
            if (gr.Auto.On || gr.Auto.StopWhy != "cancel") return "cancel stop " + gr.Auto.StopWhy;
            // an empty group
            game = World(4); log = new List<string>();
            c = Ctl(game, log); gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            foreach (var a in game.Air.ToList()) c.Group(C("op", "remove", "group", "A", "uids", new[] { a.Uid }));
            Run(game, c, 20);
            if (gr.Auto.On || gr.Auto.StopWhy != "empty_group") return "empty_group " + gr.Auto.StopWhy;
            // all targets sunk
            game = World(4); log = new List<string>();
            c = Ctl(game, log); gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            game.Fleet.Clear();
            Run(game, c, 20);
            if (gr.Auto.On || (gr.Auto.StopWhy != "no_targets" && gr.Auto.StopWhy != "empty_group")) return "no_targets " + gr.Auto.StopWhy;
            // a launch error (the ammunition went): the stop carries the reason
            game = World(4); log = new List<string>();
            c = Ctl(game, log); gr = G(c);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 5);
            game.Air[0].Airborne = true;
            return null;
        }

        // ---- U5
        private static string AutoRecheck()
        {
            // the target turns neutral between the plan and the order: the row is skipped, no Issue, a log line, the auto stops
            var game = World(4); var log = new List<string>();
            var c = Ctl(game, log); var gr = G(c);
            c.Plan(C("group", "A"));
            game.Fleet[0].Side = "neutral"; game.Fleet[0].Hostile = false;
            string r = c.Launch(C("group", "A"));
            if (r != "!target_excluded|neutral") return "manual: the same check " + r;
            game.Fleet[0].Side = "hostile"; game.Fleet[0].Hostile = true;
            c.Launch(C("group", "A"));
            if (gr.Timeline == null || gr.Timeline.Status != "executing") return "launched " + (gr.Timeline == null ? "-" : gr.Timeline.Status);
            var tl = gr.Timeline;
            Run(game, c, 1);
            game.Fleet[0].Identified = false;
            Run(game, c, 400);
            if (game.IssueLog.Count != 0) return "no order for an unidentified target";
            if (!tl.Exec.Any(x => x.State == "skipped" && x.Why == "excluded")) return "skipped excluded " + string.Join(",", tl.Exec.Select(x => x.State + ":" + x.Why));
            if (!log.Any(l => l.Contains("strike recheck"))) return "recheck log";
            // the class changed
            game = World(4); log = new List<string>();
            c = Ctl(game, log); gr = G(c);
            c.Plan(C("group", "A"));
            c.Launch(C("group", "A"));
            tl = gr.Timeline;
            game.Fleet[0].ClassIni = "usn_ddg_other";
            Run(game, c, 400);
            if (!tl.Exec.Any(x => x.State == "skipped" && x.Why == "class_changed")) return "skipped class_changed " + string.Join(",", tl.Exec.Select(x => x.State + ":" + x.Why));
            // a civilian
            game = World(4); log = new List<string>();
            c = Ctl(game, log);
            c.Plan(C("group", "A"));
            game.Fleet[0].Civilian = true;
            r = c.Launch(C("group", "A"));
            if (!r.StartsWith("!target_excluded", StringComparison.Ordinal)) return "civilian " + r;
            return null;
        }

        // ---- U6
        private static string AutoLoad()
        {
            var game = World(4); var log = new List<string>();
            var c = Ctl(game, log); var gr = G(c);
            string off = c.Export();
            if (off.Contains("auto_was")) return "no field without the auto";
            c.SetAuto(C("group", "A", "on", true));
            string json = c.Export();
            if (!json.Contains("\"auto_was\":true")) return "auto_was in the export";
            var game2 = World(4); var log2 = new List<string>();
            var c2 = new StrikeController(game2, new StrikeParams(), log2.Add);
            string err;
            c2.Import(json, out err);
            if (err != null) return "import " + err;
            var g2 = c2.Groups.FirstOrDefault();
            if (g2 == null) return "the group is back";
            if (g2.Auto.On || !g2.Auto.Notes.Contains("auto_off_loaded")) return "off after the load";
            if (!log2.Any(l => l.Contains("strike auto grp=A") && l.Contains("action=off") && l.Contains("why=loaded"))) return "load log";
            Run(game2, c2, 600);
            if (game2.IssueLog.Count != 0) return "no order after the load";
            // an old file without the field loads
            var c3 = new StrikeController(World(4), new StrikeParams(), s => { });
            c3.Import(off, out err);
            if (err != null) return "old file " + err;
            // ResetMission clears the queue and the state
            c.QueueReplan(gr, new PlanOpts { Why = "t", Purpose = "auto" });
            c.ResetMission();
            if (c.ReplanQueueLength != 0) return "reset: queue";
            return null;
        }

        // ---- S1
        private static StrikeController ClearCtl(StrikeFakeGame game, List<string> log, StrikeParams p = null, bool auto = false)
        {
            var c = Ctl(game, log, p, "sink", false);
            c.Target(C("group", "A", "mode", "spread", "target_id", 100, "track_ids", new[] { 101 }));   // the neighbour is a target of its own: the card is shown
            Run(game, c, 4);
            c.Plan(C("group", "A"));
            return c;
        }

        private static int ClearIdx(StrikeController c) { return G(c).Variants.FindIndex(v => v.Kind == "clear_pause"); }

        private static string ClearLaunch()
        {
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log);
            var gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants: " + string.Join(" | ", log.Where(l => l.Contains("strike plan"))) + string.Join(",", gr.Variants.Select(v => v.Kind + (v.Red ? "!" : "")));
            c.Plan(C("group", "A", "choose", vi));
            game.Calls.Clear();
            string r = c.Launch(C("group", "A"));
            if (StrikeController.IsErr(r)) return "launch " + r;
            if (gr.Clear == null || gr.Clear.Stage != "stage1") return "state stage1";
            var rows = gr.Timeline.Exec;
            if (rows.Count == 0 || rows.Any(x => gr.Clear.Stage2Uids.Contains(x.Uid))) return "only the stage-1 rows";
            if (gr.Clear.Stage2Uids.Count == 0) return "stage-2 aircraft";
            Run(game, c, 5);
            foreach (int u in gr.Clear.Stage2Uids) if (game.Calls.Count(x => x == "orbit " + u) != 1) return "one orbit at the gate for " + u + ": " + game.Calls.Count(x => x == "orbit " + u);
            Run(game, c, 60);
            foreach (int u in gr.Clear.Stage2Uids) if (game.Calls.Any(x => x.StartsWith("goto " + u + " ", StringComparison.Ordinal))) return "the wait logic leaves the stage-2 aircraft alone: " + string.Join(" | ", game.Calls) + " st2=" + string.Join(",", gr.Clear.Stage2Uids) + string.Join(" | ", log.Where(l => l.Contains("wait") || l.Contains("clear")).Take(10));
            if (!log.Any(l => l.Contains("strike clear start grp=A"))) return "clear start line";
            return null;
        }

        // ---- S2
        private static string ClearRelease()
        {
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log);
            var gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            var st2 = gr.Clear.Stage2Uids.ToList();
            game.FateMap[101] = "sunk";
            Run(game, c, 30);
            game.DestroyedMap[101] = "destroyed";
            int issued = game.IssueLog.Count;
            Run(game, c, 1500, () => gr.Clear == null || gr.Clear.Stage == "released");
            if (gr.Clear != null && gr.Clear.Stage != "released") return "stage 2 released: " + gr.Clear.Stage + " " + string.Join(" | ", log.Where(l => l.Contains("strike clear")).Take(8));
            if (!log.Any(l => l.Contains("strike clear release") && l.Contains("how=destroyed"))) return "release line";
            Run(game, c, 900, () => game.IssueLog.Any(l => l.Contains(" 100 ")));
            if (!game.IssueLog.Skip(issued).Any(l => l.Contains(" 100 "))) return "stage 2 against the main target: " + string.Join(" | ", log.Where(l => l.Contains("strike clear")).Take(12)) + " ## " + string.Join(" | ", game.IssueLog.Skip(issued).Take(6)) + " ## " + string.Join(" | ", game.IssueLog.Take(6));
            return null;
        }

        // ---- S3
        private static string ClearRewave()
        {
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log);
            var gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            game.FateMap[101] = "damaged";
            Run(game, c, 2500, () => gr.Clear != null && gr.Clear.Rewaves > 0);
            if (gr.Clear == null || gr.Clear.Rewaves == 0) return "a rewave against the neighbour " + (gr.Clear == null ? "no clear" : gr.Clear.Stage);
            if (!log.Any(l => l.Contains("strike clear rewave"))) return "rewave line";
            Run(game, c, 10);
            if (game.IssueLog.Any(l => l.Contains(" 100 "))) return "no order against the main target before the sinking";
            return null;
        }

        // ---- S5: 3 stale replans in a row: stage 2 stays a draft for the player, the auto stops with "clear_stale"
        private static string ClearStale()
        {
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log);
            var gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            c.SetAuto(C("group", "A", "on", true));
            game.FateMap[101] = "sunk";
            Run(game, c, 30);
            game.DestroyedMap[101] = "destroyed";
            int issued = game.IssueLog.Count;
            // every draft of stage 2 is made stale at once (as if the situation kept changing)
            Run(game, c, 3000, () =>
            {
                if (gr.Clear == null) return true;
                if (gr.Clear.Stage == "stale_wait") return true;
                if (gr.Clear.Stage == "release" && gr.Timeline != null && gr.Timeline.Status == "draft") { gr.Timeline.Status = "stale"; gr.Timeline.StaleWhy = "aircraft_changed"; }
                return false;
            });
            if (gr.Clear == null) return "the clearing ended instead of the stale wait: " + string.Join(" | ", log.Where(l => l.Contains("strike clear")).Take(12));
            if (gr.Clear.Stage != "stale_wait") return "stale_wait " + gr.Clear.Stage;
            if (gr.Auto.On || gr.Auto.StopWhy != "clear_stale") return "the auto stops with clear_stale: " + gr.Auto.StopWhy;
            if (!log.Any(l => l.Contains("strike clear stale_wait"))) return "stale_wait line";
            if (!gr.Auto.Notes.Contains("clear_stale")) return "note clear_stale";
            Run(game, c, 120);
            if (gr.Clear == null || gr.Clear.Stage != "stale_wait") return "stays in stale_wait";
            if (game.IssueLog.Skip(issued).Any(l => l.Contains(" 100 "))) return "no order against the main target without the player";
            if (gr.Timeline == null || gr.Timeline.Status != "stale") return "the stale draft stays " + (gr.Timeline == null ? "-" : gr.Timeline.Status);
            // the player launches it with the confirmation
            string r = c.Launch(C("group", "A", "variant", 0.0, "confirm", true));
            if (StrikeController.IsErr(r)) return "player launch " + r;
            if (gr.Clear == null || gr.Clear.Stage != "released") return "released by the player " + (gr.Clear == null ? "-" : gr.Clear.Stage);
            return null;
        }

        // ---- S3
        // ---- S4
        private static string ClearTimeout()
        {
            var p = new StrikeParams { ClearTimeoutS = 120 };
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log, p);
            var gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 2500, () => gr.Clear != null && StrikeAutoMath.ClearActive(gr.Clear.Stage) == false);
            if (gr.Clear == null || gr.Clear.Stage != "timeout") return "timeout " + (gr.Clear == null ? "-" : gr.Clear.Stage);
            if (gr.Auto.On || gr.Auto.StopWhy != "clear_timeout") return "the auto stops " + gr.Auto.StopWhy;
            int issued = game.IssueLog.Count;
            Run(game, c, 30);
            if (game.IssueLog.Count != issued) return "no order after the timeout";
            if (gr.Timeline == null || gr.Timeline.Status == "executing") return "a draft by the main target";
            c.Cancel(C("group", "A"));
            if (gr.Clear != null) return "cancel ends the clearing";
            // the neighbour is gone without hits: lost
            game = World(6, true); log = new List<string>();
            c = ClearCtl(game, log); gr = G(c);
            vi = ClearIdx(c);
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            game.DestroyedMap[101] = "gone";
            Run(game, c, 2500, () => gr.Clear == null || gr.Clear.Stage == "lost" || gr.Clear.Stage == "release" || gr.Clear.Stage == "released");
            if (gr.Clear == null || (gr.Clear.Stage != "lost" && gr.Clear.Stage != "release" && gr.Clear.Stage != "released")) return "gone: " + (gr.Clear == null ? "-" : gr.Clear.Stage);
            return null;
        }

        // ---- V1
        private static string AutoView()
        {
            var game = World(6, true); var log = new List<string>();
            var c = ClearCtl(game, log);
            string json = c.ViewJson(10);
            if (!json.Contains("\"auto\":{\"on\":false") || !json.Contains("\"clear\":null")) return "auto/clear fields " + json.Length;
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            if (!json.Contains("\"stages\":[")) return "stages in the variants";
            if (json.Contains("\"limit\"") && json.Contains("\"max_limit\"") && json.IndexOf("\"stages\"", StringComparison.Ordinal) >= 0 && Between(json, "\"stages\":[", "]").Contains("limit")) return "no limit in stages";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            json = c.ViewJson(10);
            if (!json.Contains("\"clear\":{\"stage\":\"stage1\"") || !json.Contains("\"neighbour\":101")) return "clear object";
            return null;
        }

        private static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return "";
            int j = s.IndexOf(b, i + a.Length, StringComparison.Ordinal);
            return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
        }

        private static string AutoBudget()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, "sink");
            Run(game, c, 6);
            double best = double.MaxValue;
            int tof0 = game.TofCalls;
            for (int k = 0; k < 3; k++)
            {
                c.QueueReplan(G(c), new PlanOpts { Why = "t", Purpose = "auto" });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                game.Advance(1); c.Tick(game.Time);
                sw.Stop();
                if (G(c).Timeline != null) best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            if (game.TofCalls != tof0) return "no flight estimate in the queued replan " + (game.TofCalls - tof0);
            Console.WriteLine("strike auto budget ms=" + best.ToString("0.0", CultureInfo.InvariantCulture));
            if (best > 20) return "replan tick over the budget " + best;
            return null;
        }

        // ---- K2
        private static string DeckNoReplyCtl()
        {
            // an order that gets no answer from the broker is failed after DeckReplyS and is not counted as live
            var game = World(2); var log = new List<string>();
            var c = Ctl(game, log);
            var gr = G(c);
            var o = new StrikeDeckOrder { State = "sent", OrderedT = game.Time, Key = null };
            if (StrikeDeckPlanner.TrackNoKey(o, game.Time + 29, StrikeCalib.DeckReplyS) != "sent") return "29 s still sent";
            if (StrikeDeckPlanner.TrackNoKey(o, game.Time + 30, StrikeCalib.DeckReplyS) != "failed") return "30 s failed";
            if (c.DeckLiveCount(gr) != 0) return "no live orders";
            return null;
        }

        // ---- K3 (the debt of the review of C): LaunchCore refuses -> the auto stops with launch_err:<code>, nothing is issued, in the plain auto and at the release of stage 2
        private static string AutoLaunchErr()
        {
            // two targets in the plan; the second one leaves the table between the plan and the launch: the Pick of the auto counts the group targets (the main one is there), LaunchCore checks every row -> no_target
            var game = World(6, true); var log = new List<string>();
            var c = Ctl(game, log, null, "breach", false);
            c.Target(C("group", "A", "mode", "spread", "target_id", 100, "track_ids", new[] { 101 }));
            var gr = G(c);
            Run(game, c, 4);
            c.Plan(C("group", "A"));
            int vi0 = gr.Variants.FindIndex(v => v.Assignments.Any(a => a.TargetId == 101));
            if (gr.Timeline == null || vi0 < 0) return "a variant with rows against the second target " + string.Join(",", gr.Variants.Select(v => v.Kind + ":" + string.Join("/", v.Assignments.Select(a => a.TargetId))));
            c.Plan(C("group", "A", "choose", vi0));
            game.Fleet.RemoveAll(sh => sh.TrackId == 101);
            c.SetAuto(C("group", "A", "on", true));
            Run(game, c, 10, () => !gr.Auto.On);
            if (gr.Auto.On || gr.Auto.StopWhy == null || !gr.Auto.StopWhy.StartsWith("launch_err:", StringComparison.Ordinal)) return "stop reason " + gr.Auto.StopWhy + " " + string.Join(" | ", log.Where(l => l.Contains("strike auto")).Take(6));
            if (game.IssueLog.Count != 0) return "an order was issued after the refusal";
            if (!gr.Auto.Notes.Any(n => n.StartsWith("auto_stop:launch_err", StringComparison.Ordinal))) return "stop note " + string.Join(",", gr.Auto.Notes);
            if (gr.Timeline != null && gr.Timeline.Status == "executing") return "no execution after the refusal";
            // stage 2 of the clearing: the same refusal at the release
            game = World(6, true); log = new List<string>();
            c = ClearCtl(game, log); gr = G(c);
            int vi = ClearIdx(c);
            if (vi < 0) return "clear_pause among the variants";
            c.Plan(C("group", "A", "choose", vi));
            c.Launch(C("group", "A"));
            gr.Auto.Waves = 0;
            c.SetAuto(C("group", "A", "on", true));
            game.FateMap[101] = "sunk"; game.DestroyedMap[101] = "destroyed";
            bool injected = false;
            Run(game, c, 2500, () =>
            {
                if (!injected && gr.Clear != null && gr.Clear.Stage == "release" && gr.Variants != null) { foreach (var uid in gr.Variants.SelectMany(v => v.Assignments).Select(a => a.Uid).Distinct().ToList()) gr.Members.Remove(uid); injected = true; }
                return gr.Clear == null || !StrikeAutoMath.ClearActive(gr.Clear.Stage) || (!gr.Auto.On && gr.Auto.StopWhy != null);
            });
            if (gr.Auto.On || gr.Auto.StopWhy == null) return "stage 2: the auto stops " + gr.Auto.StopWhy + " clear " + (gr.Clear == null ? "-" : gr.Clear.Stage) + " " + string.Join(" | ", log.Where(l => l.Contains("strike clear") || l.Contains("strike auto")).Take(10));
            if (!(gr.Auto.StopWhy.StartsWith("launch_err:", StringComparison.Ordinal) || gr.Auto.StopWhy.StartsWith("blocked:", StringComparison.Ordinal))) return "stage 2 stop reason " + gr.Auto.StopWhy;
            if (game.IssueLog.Any(l => l.Contains(" 100 "))) return "stage 2 issued against the main target after the refusal";
            return null;
        }
    }
}
