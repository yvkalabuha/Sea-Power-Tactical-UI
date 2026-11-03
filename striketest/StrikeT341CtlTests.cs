using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 stand sections "t341 <scenario>": StrikeController on the fake game (groups, target, plan, stale check, view).
    internal static class StrikeT341CtlTests
    {
        private static readonly string[] Names =
        {
            "ctl-group", "ctl-add", "ctl-target", "ctl-owner", "plan-basic", "plan-tof", "plan-none", "plan-errors", "stale", "view", "view-limit",
            "own-claim", "own-release", "wait-gather", "wait-orbit", "wait-sam", "wait-fuel", "wait-leave", "wait-notarget", "deck-allow", "deck-adopt", "deck-gap", "veto-members", "cancel-choose", "view-window", "save-load", "ring-window",
            "exec-launch", "exec-tot", "exec-held", "exec-late", "exec-retry", "exec-mixed", "exec-result", "exec-wave", "exec-cancel", "exec-cancel-retry", "exec-release-issued", "exec-save", "view-exec", "exec-clock", "exec-stuck",
            "roe-veto", "roe-hold", "roe-fallback", "warm-cache", "wait-nosea",
        };
        private static readonly string[] AllNames = Names.Concat(StrikeT341EwTests.Names).Concat(StrikeT341CalibTests.Names).ToArray();

        public static string Run(string only)
        {
            foreach (string n in AllNames)
            {
                if (only != null && only != n) continue;
                string e;
                try { e = Dispatch(n); } catch (Exception ex) { e = "exception " + ex; }
                if (e != null) return n + ": " + e;
            }
            return null;
        }

        private static string Dispatch(string n)
        {
            switch (n)
            {
                case "ctl-group": return CtlGroup();
                case "ctl-add": return CtlAdd();
                case "ctl-target": return CtlTarget();
                case "ctl-owner": return CtlOwner();
                case "plan-basic": return PlanBasic();
                case "plan-tof": return PlanTof();
                case "plan-none": return PlanNone();
                case "plan-errors": return PlanErrors();
                case "stale": return Stale();
                case "view": return View();
                case "view-limit": return ViewLimit();
            }
            return StrikeT341BTests.Dispatch(n);
        }

        internal static Dictionary<string, object> C(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2)
            {
                object v = kv[i + 1];
                if (v is int) v = (double)(int)v;
                if (v is int[]) v = ((int[])v).Select(x => (object)(double)x).ToList();
                if (v is string[]) v = ((string[])v).Select(x => (object)x).ToList();
                d[(string)kv[i]] = v;
            }
            return d;
        }

        internal static Dictionary<string, object> Obj(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        internal static List<object> Arr(object o) { return (List<object>)o; }

        internal static StrikeFakeGame Game()
        {
            var g = new StrikeFakeGame();
            g.AddPlane(1, "usn_a-6e", "usn_agm-84c", 4, 39.5, 10);
            g.AddPlane(2, "usn_a-6e", "usn_agm-84c", 4, 39.45, 10);
            g.AddPlane(3, "sh_helo", "fr_am-39", 2, 39.4, 10, true, "Helicopter");
            g.AddPlane(4, "usn_a-6e", "usn_agm-84c", 4, 39.4, 10);
            g.AddKidd(100);
            return g;
        }

        private static string CtlGroup()
        {
            var game = Game(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            string r = c.Group(C("op", "create"));
            if (StrikeController.IsErr(r) || Obj(r)["group"] as string != "Strike-1" || (double)Obj(r)["seq"] != 1) return "create auto " + r;
            r = c.Group(C("op", "create", "group", "Alpha"));
            if (StrikeController.IsErr(r) || (double)Obj(r)["seq"] != 2) return "create named " + r;
            if (c.Group(C("op", "create", "group", "Alpha")) != "!group_exists") return "duplicate";
            if (!StrikeController.IsErr(c.Group(C("op", "create", "group", "   ")))) return "blank name";
            if (c.Group(C("op", "bogus")) != "!bad_args|op") return "bad op";
            if (c.Group(C("op", "add", "uids", new[] { 1 })) != "!bad_args|group") return "add without group";
            if (c.Group(C("op", "add", "group", "Alpha")) != "!bad_args|uids") return "add without uids";
            if (c.Group(C("op", "add", "group", "Nope", "uids", new[] { 1 })) != "!no_group") return "no group";
            r = c.Group(C("op", "create", "group", "Beta", "uids", new[] { 1, 2 }));
            if (StrikeController.IsErr(r) || Arr(Obj(r)["added"]).Count != 2) return "create with uids " + r;
            string gn;
            if (!c.IsMember(1, out gn) || gn != "Beta" || c.IsMember(4, out gn)) return "is member";
            r = c.Group(C("op", "rename", "group", "Alpha", "name", "Hammer"));
            if (StrikeController.IsErr(r) || c.Groups.All(g => g.Name != "Hammer")) return "rename " + r;
            if (c.Group(C("op", "rename", "group", "Hammer", "name", "Beta")) != "!group_exists") return "rename to a taken name";
            if (c.Group(C("op", "rename", "group", "Hammer", "name", "Hammer")) == "!group_exists") return "rename to the own name";
            r = c.Group(C("op", "rename", "group", "Hammer", "name", ""));
            if (StrikeController.IsErr(r) || Obj(r)["group"] as string != "Strike-2") return "rename to auto " + r;
            r = c.Group(C("op", "remove", "group", "Beta", "uids", new[] { 1 }));
            if (StrikeController.IsErr(r) || c.IsMember(1, out gn)) return "remove " + r;
            if (StrikeController.IsErr(c.Group(C("op", "disband", "group", "Beta"))) || c.Groups.Any(g => g.Name == "Beta")) return "disband";
            if (c.Group(C("op", "disband", "group", "Beta")) != "!no_group") return "disband twice";
            if (c.IsMember(2, out gn)) return "member of a disbanded group";
            if (c.Group(C("op", "create", "group", "Failing", "uids", new[] { 3 })) != "!not_striker|helicopter" || c.Groups.Any(g => g.Name == "Failing")) return "a failed create leaves no group";
            return null;
        }

        private static string CtlAdd()
        {
            var game = Game();
            var ea = game.AddPlane(5, "usn_ea-6b", "usn_agm-88", 2);
            ea.Missiles[0].Guidance = "arm"; ea.Missiles[0].AntiRadiation = true;
            ea.Ew = new StrikeEw { RangeNm = 200, ConeDeg = 30, Bands = new[] { "X" } };
            StrikeAirMath.Classify(new[] { ea });
            game.AddPlane(6, "wp_su-24m", "wp_as-13", 2).Missiles[0].Guidance = "tv";
            StrikeAirMath.Classify(game.Air);
            game.AddPlane(7, "usn_f-14", "usn_aim-9", 2).Missiles[0].TargetType = "AAW";
            StrikeAirMath.Classify(game.Air);
            var c = new StrikeController(game, new StrikeParams(), s => { });
            c.Group(C("op", "create", "group", "A"));
            c.Group(C("op", "create", "group", "B"));
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 3 })) != "!not_striker|helicopter") return "helicopter";
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 6 })) != "!not_striker|tv_only") return "tv only";
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 7 })) != "!not_striker|no_asm") return "no asm";
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 99 })) != "!no_aircraft") return "no aircraft";
            string r = c.Group(C("op", "add", "group", "A", "uids", new[] { 1, 5, 3 }));
            if (StrikeController.IsErr(r)) return "mixed add " + r;
            var o = Obj(r);
            if (Arr(o["added"]).Count != 2 || Arr(o["rejected"]).Count != 1) return "mixed add result " + r;
            if (c.Group(C("op", "add", "group", "B", "uids", new[] { 1 })) != "!in_group|A") return "in another group";
            if (StrikeController.IsErr(c.Group(C("op", "add", "group", "A", "uids", new[] { 1 })))) return "adding a member again is fine";
            if (c.Groups.First(g => g.Name == "A").Members.Count != 2) return "no duplicates";
            // a lost aircraft is not addable
            game.Air.RemoveAll(a => a.Uid == 2);
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 2 })) != "!no_aircraft") return "lost aircraft";
            return null;
        }

        private static string CtlTarget()
        {
            var game = Game();
            game.AddKidd(101, 41.1, 10.1, "neutral");
            game.AddKidd(102, 41.1, 10.2, "unknown", true);
            game.AddKidd(103, 41.1, 10.3, "hostile", false);
            game.AddKidd(104, 41.1, 10.4, "hostile", true, true, "Merchant");
            game.AddKidd(105, 41.1, 10.5, "friendly");
            var perry = game.AddKidd(106, 41.05, 10.6); perry.Ad = new StrikeAd { Ksub = 3.1, Ssub = 9.5, Ksup = 1.9, Ssup = 4.4, Source = "cache" }; perry.DisplacementT = 4000; perry.ClassName = "Perry";
            var c = new StrikeController(game, new StrikeParams(), s => { });
            c.Group(C("op", "create", "group", "A"));
            if (c.Target(C("group", "A", "target_id", 100)) == null) return "null";
            if (c.Target(C("group", "Z", "target_id", 100)) != "!no_group") return "no group";
            if (c.Target(C("group", "A")) != "!bad_args|target_id") return "no target id";
            if (c.Target(C("group", "A", "target_id", 100, "mode", "wide")) != "!bad_args|mode") return "bad mode";
            if (c.Target(C("group", "A", "target_id", 999)) != "!no_target") return "missing target";
            if (c.Target(C("group", "A", "target_id", 101)) != "!target_excluded|neutral") return "neutral";
            if (c.Target(C("group", "A", "target_id", 102)) != "!target_excluded|not_hostile") return "unknown side";
            if (c.Target(C("group", "A", "target_id", 103)) != "!target_excluded|not_identified") return "not identified";
            if (c.Target(C("group", "A", "target_id", 104)) != "!target_excluded|civilian") return "civilian";
            if (c.Target(C("group", "A", "target_id", 105)) != "!target_excluded|friendly") return "friendly";
            string r = c.Target(C("group", "A", "target_id", 100));
            var o = Obj(r);
            if (StrikeController.IsErr(r) || o["mode"] as string != "single" || (double)o["main"] != 100 || Arr(o["ships"]).Count != 1) return "single " + r;
            // spread: the ships in the order of the priority (AA first), the excluded ones are rejected with the reason
            r = c.Target(C("group", "A", "mode", "spread", "target_id", 106, "track_ids", new[] { 100, 101, 104, 106 }));
            o = Obj(r);
            var ships = Arr(o["ships"]).Select(x => (int)(double)x).ToList();
            if (string.Join(",", ships) != "100,106") return "spread order " + r;
            if (Arr(o["rejected"]).Count != 2) return "spread rejected " + r;
            if (c.Groups[0].Target.MainId != 106 || c.Groups[0].Target.Mode != "spread") return "main stays the chosen one";
            return null;
        }

        private static string CtlOwner()
        {
            var game = Game();
            var c = new StrikeController(game, new StrikeParams(), s => { });
            c.OwnerOf = uid => uid == 1 ? "ASW-1" : null;
            c.Group(C("op", "create", "group", "A"));
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 1 })) != "!in_group|ASW-1") return "owned by another automation";
            if (StrikeController.IsErr(c.Group(C("op", "add", "group", "A", "uids", new[] { 2 })))) return "free aircraft";
            return null;
        }

        private static StrikeController Ready(StrikeFakeGame game, List<string> log, out string group)
        {
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            group = "A";
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        private static string PlanBasic()
        {
            var game = Game(); var log = new List<string>(); string gn;
            var c = Ready(game, log, out gn);
            string r = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(r)) return "plan " + r;
            var o = Obj(r);
            var vs = Arr(o["variants"]);
            if (vs.Count < 1 || vs.Count > 3) return "variants " + vs.Count;
            var v0 = (Dictionary<string, object>)vs[0];
            if (v0["kind"] as string != "min_missiles" || (double)v0["missiles"] != 8 || (double)v0["targets"] != 1) return "best variant " + r;
            if ((bool)v0["estimate"] != StrikeCalib.Estimate || !v0.ContainsKey("warnings") || !v0.ContainsKey("eta_s")) return "variant fields";
            if ((double)o["plan_id"] != 1 || o["tof_src"] as string != "rough" || !o.ContainsKey("ms")) return "plan fields " + r;
            if (c.Groups[0].Timeline.Status != "draft" || c.Groups[0].Chosen != 0) return "group state";
            if (!log.Any(l => l.StartsWith("[Strike] strike plan A id=1", StringComparison.Ordinal) && l.Contains(" tof_src=rough") && l.Contains(" ms="))) return "log " + string.Join(" | ", log);
            r = c.Plan(C("group", "A"));
            if ((double)Obj(r)["plan_id"] != 2) return "plan id grows";
            if (game.TofCalls == 0) return "the game was asked for the time of flight";
            return null;
        }

        private static string PlanTof()
        {
            var game = Game(); var log = new List<string>(); string gn;
            game.SimTof = true; game.SimTofS = 300; game.SimTofPerNm = 5;
            var c = Ready(game, log, out gn);
            var o = Obj(c.Plan(C("group", "A")));
            if (o["tof_src"] as string != "sim") return "tof_src sim " + o["tof_src"];
            var sim = (double)((Dictionary<string, object>)Arr(o["variants"])[0])["eta_s"];
            game.SimTof = false;
            if (c.Tofs.Count == 0) return "the simulated values are cached";
            c.Tofs.Clear();   // phase C: the cache answers a repeated plan; a fresh start asks the game again
            o = Obj(c.Plan(C("group", "A")));
            if (o["tof_src"] as string != "rough") return "tof_src rough";
            var rough = (double)((Dictionary<string, object>)Arr(o["variants"])[0])["eta_s"];
            if (!(sim < rough)) return "the simulated flight (300 s) is shorter than the rough one for 58 nm: " + sim + " vs " + rough;
            return null;
        }

        private static string PlanNone()
        {
            var game = Game(); var log = new List<string>();
            foreach (var a in game.Air) a.Airborne = false;
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            var o = Obj(c.Plan(C("group", "A")));
            if (Arr(o["variants"]).Count != 0 || o["why"] as string != "no_airborne") return "deck only " + Json.Num(Arr(o["variants"]).Count) + " " + o["why"];
            return null;
        }

        private static string PlanErrors()
        {
            var game = Game();
            var c = new StrikeController(game, new StrikeParams(), s => { });
            c.Group(C("op", "create", "group", "A"));
            if (c.Plan(C("group", "A")) != "!no_target") return "no target";
            c.Target(C("group", "A", "target_id", 100));
            if (c.Plan(C("group", "A")) != "!no_aircraft") return "no aircraft";
            if (c.Plan(C("group", "Z")) != "!no_group") return "no group";
            if (c.Plan(C()) != "!bad_args|group") return "bad args";
            c.Group(C("op", "add", "group", "A", "uids", new[] { 1 }));
            game.Fleet.Clear();
            if (c.Plan(C("group", "A")) != "!no_target") return "target gone";
            game.AddKidd(100, 41, 10, "neutral");
            if (c.Plan(C("group", "A")) != "!target_excluded|neutral") return "target became neutral";
            return null;
        }

        private static string Stale()
        {
            var game = Game(); var log = new List<string>(); string gn;
            var c = Ready(game, log, out gn);
            c.Plan(C("group", "A"));
            c.Tick(1000);
            if (c.Groups[0].Timeline.Status != "draft") return "fresh plan";
            game.Fleet[0].Lat += 8.0 / 60;   // 8 nm off the lead
            game.Time = 1001;
            c.Tick(1001);
            if (c.Groups[0].Timeline.Status != "draft") return "no check inside 5 s";
            game.Time = 1006;
            c.Tick(1006);
            var tl = c.Groups[0].Timeline;
            if (tl.Status != "stale" || tl.StaleWhy != "target_moved") return "stale " + tl.Status + " " + tl.StaleWhy;
            if (!log.Any(l => l == "[Strike] strike stale A why=target_moved")) return "log " + string.Join(" | ", log);
            int n = log.Count;
            c.Tick(1020);
            if (log.Count != n) return "a stale plan is reported once";
            c.Plan(C("group", "A"));
            if (c.Groups[0].Timeline.Status != "draft") return "a new plan is draft again";
            // an aircraft leaves the group: the plan is stale at once
            c.Group(C("op", "remove", "group", "A", "uids", new[] { 2 }));
            if (c.Groups[0].Timeline.StaleWhy != "aircraft_changed") return "remove marks the plan " + c.Groups[0].Timeline.StaleWhy;
            c.Plan(C("group", "A"));
            c.Target(C("group", "A", "target_id", 100));
            if (c.Groups[0].Timeline.StaleWhy != "target_changed") return "target change marks the plan";
            c.Plan(C("group", "A"));
            game.Air.RemoveAll(a => a.Uid == 1);
            c.Tick(1100);
            if (c.Groups[0].Timeline.StaleWhy != "aircraft_lost") return "aircraft lost " + c.Groups[0].Timeline.StaleWhy;
            // time going backwards (a save load) does not freeze the check
            game.AddPlane(1, "usn_a-6e", "usn_agm-84c", 4);
            game.Time = 500;
            c.Plan(C("group", "A"));
            game.Air.RemoveAll(a => a.Uid == 1);
            c.Tick(505);
            if (c.Groups[0].Timeline.StaleWhy != "aircraft_lost") return "time going back " + c.Groups[0].Timeline.StaleWhy;
            return null;
        }

        private static string View()
        {
            var game = Game(); var log = new List<string>(); string gn;
            var c = Ready(game, log, out gn);
            string empty = new StrikeController(game, new StrikeParams(), s => { }).ViewJson(20);
            var eo = Obj(empty);
            if (Arr(eo["groups"]).Count != 0 || !eo.ContainsKey("veto") || !eo.ContainsKey("calib")) return "empty view " + empty;
            var o = Obj(c.ViewJson(20));
            if (!o.ContainsKey("groups") || !o.ContainsKey("veto") || !o.ContainsKey("calib")) return "keys";
            var calib = (Dictionary<string, object>)o["calib"];
            if (calib["source"] as string != "cache" || (double)calib["formula_version"] != 153 || (bool)calib["estimate"] != StrikeCalib.Estimate) return "calib";
            var veto = (Dictionary<string, object>)o["veto"];
            if (veto["mode"] as string != "veto" || !veto.ContainsKey("chain") || !veto.ContainsKey("vetoed_n") || !veto.ContainsKey("last")) return "veto";
            var g = (Dictionary<string, object>)Arr(o["groups"])[0];
            foreach (string k in new[] { "name", "seq", "status", "members", "target", "plan", "wait_pt", "ew" }) if (!g.ContainsKey(k)) return "group key " + k;
            if (g["plan"] != null || g["status"] as string != "idle") return "no plan yet";
            var m0 = (Dictionary<string, object>)Arr(g["members"])[0];
            if ((double)m0["uid"] != 1 || m0["role"] as string != "striker" || !(bool)m0["airborne"] || m0["why"] != null) return "member " + Json.Str("x");
            var ms = Arr(m0["missiles"]);
            if (ms.Count != 1 || (double)((Dictionary<string, object>)ms[0])["n"] != 4) return "member missiles";
            var tgt = (Dictionary<string, object>)g["target"];
            if ((double)tgt["main"] != 100 || Arr(tgt["ships"]).Count != 1) return "target";
            c.Plan(C("group", "A"));
            c.VetoJson = () => "{\"mode\":\"veto\",\"chain\":[\"cap_ff\",\"strike\"],\"vetoed_n\":2,\"last\":\"strike\"}";
            o = Obj(c.ViewJson(20));
            g = (Dictionary<string, object>)Arr(o["groups"])[0];
            var plan = (Dictionary<string, object>)g["plan"];
            if ((double)plan["id"] != 1 || plan["status"] as string != "draft" || (double)plan["chosen"] != 0 || Arr(plan["variants"]).Count < 1 || plan["stale_why"] != null) return "plan block";
            var s0 = (Dictionary<string, object>)Arr(((Dictionary<string, object>)g["target"])["ships"])[0];
            if ((double)s0["n"] != 8 || (double)s0["p"] < 0.9 || (double)s0["prio"] != 1) return "ship plan figures";
            if (Arr(((Dictionary<string, object>)o["veto"])["chain"]).Count != 2) return "veto chain from the feature";
            game.Fleet[0].Lat += 0.5;
            c.Tick(1100);
            o = Obj(c.ViewJson(20));
            plan = (Dictionary<string, object>)((Dictionary<string, object>)Arr(o["groups"])[0])["plan"];
            if (plan["stale_why"] as string != "target_moved") return "stale in the view";
            return null;
        }

        private static string ViewLimit()
        {
            var game = Game();
            for (int i = 0; i < 5; i++) game.AddPlane(10 + i, "usn_a-6e", "usn_agm-84c", 2);
            var c = new StrikeController(game, new StrikeParams(), s => { });
            for (int i = 0; i < 4; i++) c.Group(C("op", "create", "group", "G" + i));
            c.Group(C("op", "add", "group", "G0", "uids", new[] { 1, 2, 4, 10, 11, 12 }));
            var o = Obj(c.ViewJson(2));
            if (Arr(o["groups"]).Count != 2) return "groups cut " + Arr(o["groups"]).Count;
            var g = (Dictionary<string, object>)Arr(o["groups"])[0];
            if (Arr(g["members"]).Count != 2) return "members cut";
            o = Obj(c.ViewJson(0));
            if (Arr(o["groups"]).Count != 4 || Arr(((Dictionary<string, object>)Arr(o["groups"])[0])["members"]).Count != 6) return "default limit 20";
            return null;
        }
    }
}
