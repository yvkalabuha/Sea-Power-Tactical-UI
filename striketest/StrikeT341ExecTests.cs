using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase C stand sections "t341 <scenario>": the execution of a strike on the fake world (Advance moves the ships, the aircraft along their last route, the orders into launches and the missiles).
    internal static class StrikeT341ExecTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names =
        {
            "exec-launch", "exec-tot", "exec-held", "exec-late", "exec-retry", "exec-mixed", "exec-result", "exec-hitbase", "exec-wave", "exec-cancel", "exec-cancel-retry", "exec-release-issued", "exec-save", "view-exec", "exec-clock", "exec-stuck",
            "roe-veto", "roe-hold", "roe-fallback", "warm-cache", "wait-nosea",
        };

        public static string Dispatch(string n)
        {
            switch (n)
            {
                case "exec-launch": return ExecLaunch();
                case "exec-tot": return ExecTot();
                case "exec-held": return ExecHeld();
                case "exec-late": return ExecLate();
                case "exec-retry": return ExecRetry();
                case "exec-mixed": return ExecMixed();
                case "exec-result": return ExecResult();
                case "exec-hitbase": return ExecHitBase();
                case "exec-wave": return ExecWave();
                case "exec-cancel": return ExecCancel();
                case "exec-cancel-retry": return ExecCancelRetry();
                case "exec-release-issued": return ExecReleaseIssued();
                case "exec-save": return ExecSave();
                case "view-exec": return ViewExec();
                case "exec-clock": return ExecClock();
                case "exec-stuck": return ExecStuck();
                case "roe-veto": return RoeVeto();
                case "roe-hold": return RoeHold();
                case "roe-fallback": return RoeFallback();
                case "warm-cache": return WarmCache();
                case "wait-nosea": return WaitNoSea();
            }
            return StrikeT341EwTests.Dispatch(n);
        }

        private static readonly bool Trace = Environment.GetEnvironmentVariable("STRIKE_TRACE") != null;

        // ---- the world
        private static StrikeFakeGame World(int planes = 4, bool two = true)
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            if (two)
            {
                var perry = g.AddKidd(101, 41.0, 10.4);
                perry.Ad = new StrikeAd { Ksub = 3.11741447, Ssub = 9.54761791, Ksup = 1.88876057, Ssup = 4.40476179, Source = "cache" };
                perry.DisplacementT = 4000; perry.ClassName = "Perry"; perry.SamMaxNm = 40;
            }
            return g;
        }

        private static StrikeController Ctl(StrikeFakeGame g, List<string> log, StrikeParams p = null, bool spread = false)
        {
            var c = new StrikeController(g, p ?? new StrikeParams(), l => { log.Add(l); if (Trace) Console.WriteLine("  t=" + g.Time.ToString("0", CultureInfo.InvariantCulture) + " " + l); });
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Where(a => a.ObjType == "Aircraft").Select(a => a.Uid).ToArray()));
            if (spread) c.Target(C("group", "A", "mode", "spread", "target_id", 100, "track_ids", new[] { 101 }));
            else c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        private static void Run(StrikeFakeGame g, StrikeController c, double secs, Func<bool> until = null, Action each = null)
        {
            for (double s = 0; s < secs; s++)
            {
                g.Advance(1); c.Tick(g.Time);
                if (each != null) each();
                if (until != null && until()) return;
            }
        }

        private static StrikeGroup G(StrikeController c) { return c.Groups[0]; }
        private static int Cnt(List<string> l, string sub) { return l.Count(x => x.Contains(sub)); }

        private static string Go(StrikeController c, string kind = null)
        {
            string r = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(r)) return "plan " + r;
            if (kind != null)
            {
                int i = G(c).Variants.FindIndex(v => v.Kind == kind);
                if (i < 0) return "no variant " + kind + " in " + string.Join(",", G(c).Variants.Select(v => v.Kind));
                c.Plan(C("group", "A", "choose", i));
            }
            return null;
        }

        // ---- Q1
        private static string ExecLaunch()
        {
            var game = World(4, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (c.Launch(C("group", "A")) != "!no_plan") return "no plan";
            if (c.Launch(C("group", "Z")) != "!no_group" || c.Launch(C()) != "!bad_args|group") return "group errors";
            string e = Go(c); if (e != null) return e;
            if (c.Launch(C("group", "A", "variant", 9)) != "!bad_args|variant") return "variant out of range";
            // a stale plan needs the confirmation
            game.Fleet[0].Lat += 0.2;
            Run(game, c, 6);
            game.Calls.Clear();
            if (G(c).Timeline.Status != "stale") return "stale " + G(c).Timeline.Status;
            if (c.Launch(C("group", "A")) != "!stale_confirm") return "stale without confirm";
            if (game.IssueLog.Count != 0) return "no order for a refused launch";
            e = Go(c); if (e != null) return e;
            // a target that turned neutral: nothing is ordered
            game.Fleet[0].Side = "neutral"; game.Fleet[0].Hostile = false;
            string r = c.Launch(C("group", "A"));
            if (r != "!target_excluded|neutral") return "neutral target " + r;
            if (game.Calls.Count != 0) return "no route order for a refused launch";
            game.Fleet[0].Side = "hostile"; game.Fleet[0].Hostile = true;
            // an aircraft back on the deck
            game.Air[0].Airborne = false;
            r = c.Launch(C("group", "A"));
            if (!r.StartsWith("!not_airborne|", StringComparison.Ordinal)) return "deck aircraft " + r;
            game.Air[0].Airborne = true;
            // the wait point over land: no launch
            var gr = G(c); string was = gr.WaitWhy; gr.WaitWhy = "no_sea";
            if (c.Launch(C("group", "A")) != "!wait_no_sea") return "no sea";
            gr.WaitWhy = was;
            // a card with no gate
            var v0 = gr.Variants[0]; v0.Warnings.Add("gate:2");
            if (c.Launch(C("group", "A")) != "!gate|2") return "gate";
            v0.Warnings.Remove("gate:2");
            // ok
            int routes0 = game.GotoAlts.Count;
            r = c.Launch(C("group", "A"));
            if (StrikeController.IsErr(r)) return "launch " + r;
            var o = Obj(r);
            if (gr.Timeline.Status != "executing" || (double)o["rows"] != gr.Timeline.Exec.Count || o["roe"] as string != "veto") return "launch state " + r;
            if (gr.Timeline.Exec.Count != gr.Variants[gr.Chosen].Assignments.Count) return "a row per assignment";
            if (c.Launch(C("group", "A")) != "!executing") return "second launch";
            if (c.Plan(C("group", "A")) != "!executing") return "plan during the strike";
            if (game.GotoAlts.Count - routes0 != gr.Timeline.Exec.Select(x => x.Uid).Distinct().Count()) return "a route order per aircraft " + (game.GotoAlts.Count - routes0);
            if (game.IssueLog.Count != 0) return "no missile at once";
            if (!log.Any(l => l.StartsWith("[Strike] strike launch A plan=", StringComparison.Ordinal) && l.Contains(" roe=veto"))) return "launch log";
            if (Cnt(log, "strike sched A uid=") != gr.Timeline.Exec.Count) return "sched log";
            return null;
        }

        // ---- Q2: the arrival is on time
        private static string ExecTot()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, true);
            string e = Go(c, "max_targets"); if (e != null) return e;
            var gr = G(c);
            int calls = game.TofCalls, during = calls;
            string r = c.Launch(C("group", "A"));
            if (StrikeController.IsErr(r)) return "launch " + r;
            int rows = gr.Timeline.Exec.Count;
            Run(game, c, 1500, () => gr.ExecStatus != null, () => { if (gr.ExecStatus == null) during = game.TofCalls; });
            if (gr.ExecStatus != "done") return "the strike did not finish: " + string.Join(",", gr.Timeline == null ? new string[0] : gr.Timeline.Exec.Select(x => x.Uid + ":" + x.State));
            if (during != calls) return "the tick asked the game for the time of flight: " + (during - calls);
            if (game.IssueLog.Count != rows) return "one order per row: " + game.IssueLog.Count + " vs " + rows + " " + string.Join(" | ", game.IssueLog);
            if (gr.LastExec.Any(x => x.State != "launched")) return "all rows launched: " + string.Join(",", gr.LastExec.Select(x => x.Uid + ":" + x.State + ":" + x.Why));
            // arrivals per target: <= 10 s apart
            foreach (var grp in game.Arrivals.GroupBy(a => a.tgt))
            {
                double spread = grp.Max(a => a.t) - grp.Min(a => a.t);
                if (spread > 10) return "arrival spread at " + grp.Key + ": " + spread;
                if (grp.Any(a => a.dist > 0.01)) return "a missile was shot down in the fake";
            }
            if (game.Arrivals.Count == 0) return "no arrivals";
            var res = gr.Results;
            if (res.Count != 2 || res.Sum(x => x.Arrived) != game.Arrivals.Count || res.Any(x => x.SpreadS > 10)) return "results " + string.Join(";", res.Select(x => x.TrackId + ":" + x.Arrived + ":" + x.SpreadS));
            if (res.Sum(x => x.Fired) != gr.LastExec.Sum(x => x.LaunchedN)) return "fired";
            // the order of the missile of one target: all rows were ordered close to the planned time
            foreach (var row in gr.LastExec) if (Math.Abs(row.DtOrderS - (4 + 0 / 16.0)) > 1.5 && row.DtOrderS > 12) return "dt_order " + row.DtOrderS;
            if (!log.Any(l => l.Contains("strike spread tgt=")) || !log.Any(l => l.Contains("strike arrive tgt="))) return "arrive / spread log";
            if (!game.GotoAlts.Any(x => x.EndsWith(":200:2", StringComparison.Ordinal))) return "the run in goes on past the launch point (two points, the launch altitude): " + string.Join(",", game.GotoAlts.Take(12));
            if (Cnt(log, "strike launched uid=") != rows) return "launched log";
            return null;
        }

        // ---- Q3
        private static string ExecHeld()
        {
            // the contact is lost before the order and comes back after 20 s: the order goes, nothing else moves
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            var ship = game.Fleet[0];
            double lostAt = -1, backAt = -1;
            Run(game, c, 1200, () => gr.ExecStatus != null, () =>
            {
                var rows = gr.Timeline == null ? gr.LastExec : gr.Timeline.Exec;
                bool near = rows.Any(r => r.State == "run_in" && game.Time >= r.TLaunch + 30);
                if (lostAt < 0 && near) { lostAt = game.Time; ship.FixAgeS = 20; }
                else if (lostAt >= 0 && backAt < 0)
                {
                    ship.FixAgeS = 20 + (game.Time - lostAt);
                    if (game.Time - lostAt >= 20) { backAt = game.Time; ship.FixAgeS = 0; }
                }
            });
            if (lostAt < 0) return "never reached the run in";
            if (!log.Any(l => l.Contains("strike held uid=") && l.EndsWith("why=contact_lost", StringComparison.Ordinal))) return "held log " + string.Join(" | ", log.Where(l => l.Contains("held")));
            if (gr.LastExec.Any(x => x.State != "launched")) return "all launched after the contact came back: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            // lost for good: skipped after HoldS
            game = World(2, false); log = new List<string>();
            c = Ctl(game, log);
            Go(c); gr = G(c);
            c.Launch(C("group", "A"));
            ship = game.Fleet[0]; lostAt = -1;
            Run(game, c, 1200, () => gr.ExecStatus != null, () =>
            {
                var rows = gr.Timeline == null ? gr.LastExec : gr.Timeline.Exec;
                if (lostAt < 0 && rows.Any(r => r.State == "run_in" && game.Time >= r.TLaunch + 30)) { lostAt = game.Time; ship.FixAgeS = 20; }
                else if (lostAt >= 0) ship.FixAgeS = 20 + (game.Time - lostAt);
            });
            if (gr.LastExec.Count == 0 || gr.LastExec.Any(x => x.State != "skipped" || (x.Why != "hold_timeout" && x.Why != "late_window"))) return "skipped after the hold: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            if (game.IssueLog.Count != 0) return "no order for a lost contact";
            if (gr.Results.Count != 1 || gr.Results[0].Fired != 0) return "result with nothing fired";
            return null;
        }

        private static string ExecLate()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            // one aircraft is slower than planned: it reaches the gate late; the order goes at once, the lateness is recorded
            game.Air[0].CruiseKn = 330;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            var slow = gr.LastExec.FirstOrDefault(x => x.Uid == game.Air[0].Uid);
            if (slow == null || slow.State != "launched") return "the slow aircraft still launched " + (slow == null ? "none" : slow.State + ":" + slow.Why);
            if (!(slow.LateS > 5)) return "late_s " + slow.LateS;
            if (!log.Any(l => l.Contains("strike issue uid=" + slow.Uid) && l.Contains(" late_s=") && !l.EndsWith("late_s=0.0", StringComparison.Ordinal))) return "late log";
            return null;
        }

        private static string ExecRetry()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            // the game refuses twice (the neutral corridor), then accepts
            game.IssueHook = (uid, n) => n <= 2 ? "neutral_near" : null;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.LastExec.Any(x => x.State != "launched")) return "launched after the refusals: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            if (game.IssueCalls.Values.Any(n => n < 3)) return "retried until accepted " + string.Join(",", game.IssueCalls.Values);
            if (!log.Any(l => l.Contains("strike held uid=") && l.EndsWith("why=neutral_near", StringComparison.Ordinal))) return "held log";
            // refused for good: skipped after T + tolerance
            game = World(2, false); log = new List<string>();
            c = Ctl(game, log); Go(c); gr = G(c);
            c.Launch(C("group", "A"));
            game.IssueHook = (uid, n) => "neutral_near";
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.LastExec.Any(x => x.State != "skipped" || x.Why != "late_window")) return "skipped as late: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            // an order the game takes but never carries out: retried twice, then failed
            game = World(2, false); log = new List<string>();
            c = Ctl(game, log); Go(c); gr = G(c);
            c.Launch(C("group", "A"));
            game.NoLaunch = true;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.LastExec.Any(x => x.State != "failed" || x.Why != "no_launch")) return "failed no_launch: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            if (game.IssueCalls.Values.Any(n => n != 3)) return "an order and two retries " + string.Join(",", game.IssueCalls.Values);
            return null;
        }

        private static string ExecMixed()
        {
            var game = new StrikeFakeGame(); var log = new List<string>();
            var a = game.AddPlane(1, "usn_a-6e", "usn_agm-84d", 4, 39.5, 10);
            a.Missiles[0].MaxNm = 118;
            a.Missiles.Add(new StrikeMissile { Ammo = "wp_as-4", Name = "as-4", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 4, MinNm = 6, MaxNm = 60, SpeedKn = 1500, CruiseAltFt = 50, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true });
            StrikeAirMath.Classify(new[] { a });
            game.AddKidd(100, 41.0, 10.0);
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1 }));
            c.Target(C("group", "A", "target_id", 100));
            if (Go(c) != null) return "plan";
            var gr = G(c);
            var v = gr.Variants[0];
            if (v.Assignments.Count != 2 || v.Assignments.Any(x => x.Uid != 1)) return "two assignments of one aircraft " + v.Assignments.Count;
            if (v.Warnings.Contains("mixed_order:1")) return "the order is possible: " + string.Join(";", v.Warnings);
            c.Launch(C("group", "A"));
            var rows = gr.Timeline.Exec.OrderBy(x => x.Order).ToList();
            if (rows[0].Ammo != "usn_agm-84d" || rows[1].Ammo != "wp_as-4" || rows[0].State != "to_gate" || rows[1].State != "run_in") return "rows " + string.Join(",", rows.Select(x => x.Ammo + ":" + x.State));
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.LastExec.Any(x => x.State != "launched")) return "both launched " + string.Join(",", gr.LastExec.Select(x => x.Ammo + ":" + x.State + ":" + x.Why));
            var first = gr.LastExec.First(x => x.Order == 0); var second = gr.LastExec.First(x => x.Order == 1);
            if (!(first.LaunchedT < second.LaunchedT)) return "the far missile first";
            double spread = game.Arrivals.Max(x => x.t) - game.Arrivals.Min(x => x.t);
            if (game.Arrivals.Count < 8 || spread > 10) return "arrival of both types " + game.Arrivals.Count + " spread " + spread;
            // the second row had its own route order (to its launch point) after the first launch
            if (!log.Any(l => l.Contains("strike route A uid=1 kind=run_in") && l.Contains("late_s="))) return "route log";
            if (log.Count(l => l.Contains("kind=run_in")) != 2) return "two run-in orders " + log.Count(l => l.Contains("kind=run_in"));
            return null;
        }

        // ---- Q4
        private static string ExecResult()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, true);
            if (Go(c, "max_targets") != null) return "plan";
            var gr = G(c);
            game.FateMap[100] = "sunk"; game.FateMap[101] = "damaged";
            // the missiles of one aircraft are all used: it goes home; the others wait at the point
            c.Launch(C("group", "A"));
            game.FateHits[100] = 3; game.FateHits[101] = 1;   // the hits come after the launch (the result counts the hits since the launch)
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.ExecStatus != "done" || gr.Results.Count != 2) return "done " + gr.ExecStatus + " " + gr.Results.Count;
            var r100 = gr.Results.First(x => x.TrackId == 100); var r101 = gr.Results.First(x => x.TrackId == 101);
            if (r100.State != "sunk" || r100.Hits != 3 || r101.State != "damaged") return "fate " + r100.State + " " + r101.State;
            if (!log.Any(l => l.StartsWith("[Strike] strike done A wave=1", StringComparison.Ordinal))) return "done log";
            if (Cnt(log, "strike result tgt=") != 2) return "result log";
            // the aircraft with no missile left go home (the rule of phase B), the rest wait
            Run(game, c, 5);
            foreach (var a in game.Air)
            {
                int left = a.Missiles.Sum(m => m.Count);
                bool home = game.Calls.Contains("rtb " + a.Uid);
                if (left == 0 && !home) return "aircraft " + a.Uid + " has no missiles and does not go home";
            }
            return null;
        }

        /// <summary>Phase E1: the hit ledger of the game is not reset by Restart Mission and counts older waves: the result counts only the hits since the launch.</summary>
        private static string ExecHitBase()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, true);
            if (Go(c, "max_targets") != null) return "plan";
            var gr = G(c);
            game.FateMap[100] = "sunk"; game.FateMap[101] = "damaged"; game.FateHits[100] = 3; game.FateHits[101] = 2;   // the ledger before the launch
            c.Launch(C("group", "A"));
            game.FateHits[100] = 5;   // two new hits on 100, none on 101
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (gr.ExecStatus != "done") return "done " + gr.ExecStatus;
            var r100 = gr.Results.First(x => x.TrackId == 100); var r101 = gr.Results.First(x => x.TrackId == 101);
            if (r100.Hits != 2 || r100.State != "sunk") return "hits since the launch " + r100.Hits + " " + r100.State;
            if (r101.Hits != 0 || r101.State != "intact") return "an old hit is not a hit of this wave " + r101.Hits + " " + r101.State;
            return null;
        }

        private static string ExecWave()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, true);
            if (Go(c, "max_targets") != null) return "plan";
            var gr = G(c);
            game.FateMap[100] = "sunk"; game.FateMap[101] = "intact";
            c.Launch(C("group", "A"));
            Run(game, c, 1500, () => gr.ExecStatus != null);
            int issued = game.IssueLog.Count;
            Run(game, c, 3);
            if (gr.ExecStatus != "done") return "done";
            var left = game.Air.Where(a => a.Missiles.Sum(m => m.Count) > 0).ToList();
            if (gr.Wave != 2 || gr.Timeline == null || gr.Timeline.Status != "draft" || gr.Target.MainId != 101) return "wave 2 " + gr.Wave + " " + (gr.Timeline == null ? "no plan" : gr.Timeline.Status) + " main " + gr.Target.MainId;
            if (gr.Variants.Count == 0) return "the cards of the next wave";
            if (gr.Variants.Any(v => !v.Warnings.Contains("later_wave"))) return "a later wave says that the air defence may be depleted";   // E1
            if (game.IssueLog.Count != issued) return "the next wave is not launched by itself";
            if (!log.Any(l => l.Contains("strike wave A wave=2 main=101"))) return "wave log";
            if (gr.Results.Count != 2) return "the results stay for the window";
            // everything sunk: idle, no plan
            game = World(4, true); log = new List<string>();
            c = Ctl(game, log, null, true); Go(c, "max_targets"); gr = G(c);
            game.FateMap[100] = "sunk"; game.FateMap[101] = "sunk";
            c.Launch(C("group", "A"));
            Run(game, c, 1500, () => gr.ExecStatus != null);
            Run(game, c, 3);
            if (gr.Timeline != null || gr.Variants.Count != 0 || gr.Wave != 1 || gr.ExecStatus != "done") return "all sunk: idle " + (gr.Timeline == null ? "" : gr.Timeline.Status);
            return null;
        }

        // ---- Q5
        private static string ExecCancel()
        {
            var game = World(4, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            // after the first two rows are launched, the player cancels
            Run(game, c, 1500, () => gr.Timeline.Exec.Count(x => x.State == "launched" || x.State == "issued") >= 2);
            var tl = gr.Timeline;
            int done = tl.Exec.Count(x => x.State == "launched" || x.State == "issued");
            if (done < 2) return "two rows launched " + done;
            int rows = tl.Exec.Count;
            if (rows <= done) { return null; }   // the plan used only two aircraft: nothing is left to cancel
            int issuedBefore = game.IssueLog.Count;
            string r = c.Cancel(C("group", "A"));
            if (StrikeController.IsErr(r)) return "cancel " + r;
            if (tl.Exec.Count(x => x.State == "cancelled") != rows - done) return "the rest is cancelled " + string.Join(",", tl.Exec.Select(x => x.State));
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (game.IssueLog.Count != issuedBefore) return "no order after the cancel";
            if (gr.ExecStatus != "cancelled" || gr.Wave != 1) return "cancelled " + gr.ExecStatus;
            if (gr.Results.Sum(x => x.Fired) != gr.LastExec.Sum(x => x.LaunchedN) || gr.Results.Sum(x => x.Fired) == 0) return "what was fired is counted";
            if (game.Arrivals.Count != gr.Results.Sum(x => x.Arrived) + gr.Results.Sum(x => x.ShotDown)) return "the missiles that flew on were counted";
            if (c.Book.Count != 0 && c.Book.Expected(1, 100, "usn_agm-84c", game.Time)) return "the book is cleared";
            if (gr.Variants.Count != 0) return "the next wave is not planned automatically";
            if (!log.Any(l => l.Contains("strike cancel exec A why=player"))) return "cancel log";
            // the aircraft that did not fire go to the wait point (the normal gather takes them)
            if (!game.Calls.Any(x => x.StartsWith("goto ", StringComparison.Ordinal))) return "gather goto";
            return null;
        }

        // review of phase C: after "Cancel" an order that gave no launch is never repeated
        private static string ExecCancelRetry()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            game.NoLaunch = true;
            Run(game, c, 1500, () => gr.Timeline.Exec.Any(x => x.State == "issued"));
            var tl = gr.Timeline;
            if (!tl.Exec.Any(x => x.State == "issued")) return "an order was given";
            int calls = game.IssueLog.Count;
            string r = c.Cancel(C("group", "A"));
            if (StrikeController.IsErr(r)) return "cancel " + r;
            Run(game, c, 600, () => gr.ExecStatus != null);
            if (game.IssueLog.Count != calls) return "no repeated order after the cancel " + calls + " -> " + game.IssueLog.Count;
            if (gr.ExecStatus != "cancelled") return "the cancelled strike finishes " + gr.ExecStatus;
            return null;
        }

        // review of phase C: an aircraft released while its order is out does not keep the strike open
        private static string ExecReleaseIssued()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            game.NoLaunch = true;
            Run(game, c, 1500, () => gr.Timeline.Exec.Any(x => x.State == "issued"));
            var row = gr.Timeline.Exec.FirstOrDefault(x => x.State == "issued");
            if (row == null) return "an order was given";
            c.Release(row.Uid);
            Run(game, c, 900, () => gr.ExecStatus != null);
            if (gr.ExecStatus == null) return "the strike finished after the release " + string.Join(",", gr.Timeline.Exec.Select(x => x.State));
            return null;
        }

        private static string ExecSave()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            string json = c.Export();
            if (json == null) return "export";
            var game2 = World(2, false); var log2 = new List<string>();
            var c2 = new StrikeController(game2, new StrikeParams(), log2.Add);
            string err;
            c2.Import(json, out err);
            if (err != null) return "import " + err;
            var g2 = c2.Groups[0];
            if (g2.Timeline == null || g2.Timeline.Exec.Count != 0 || g2.Timeline.Status != "stale" || g2.Timeline.StaleWhy != "loaded") return "after the load: stale, loaded, no rows";
            Run(game2, c2, 30);
            if (game2.IssueLog.Count != 0) return "no order after the load";
            if (c2.Launch(C("group", "A")) != "!no_plan") return "launching a loaded group needs a new plan";
            return null;
        }

        private static string ViewExec()
        {
            var game = World(4, true); var log = new List<string>();
            var c = Ctl(game, log, null, true);
            if (Go(c, "max_targets") != null) return "plan";
            var gr = G(c);
            game.FateMap[100] = "sunk"; game.FateMap[101] = "damaged";
            string idle = c.ViewJson(20);
            var ig = (Dictionary<string, object>)Arr(((Dictionary<string, object>)Json.Parse(idle))["groups"])[0];
            if (!ig.ContainsKey("exec") || !ig.ContainsKey("results") || !ig.ContainsKey("wave")) return "keys before the launch " + string.Join(",", ig.Keys);
            if (((Dictionary<string, object>)ig["exec"])["status"] as string != "none") return "exec none before the launch";
            c.Launch(C("group", "A"));
            Run(game, c, 1500, () => gr.Timeline != null && gr.Timeline.Exec.Any(x => x.State == "launched"));
            if (gr.Timeline == null) return "launched nothing";
            var v = c.View().Groups[0];
            if (v.Exec != "executing" || v.Roe != "veto" || v.Wave != 1 || v.CanLaunch) return "view while executing " + v.Exec + " " + v.Roe + " " + v.CanLaunch;
            if (v.Timeline.Count != gr.Timeline.Exec.Count || v.Timeline.All(t => t.State == null)) return "timeline rows from the execution";
            string json = c.ViewJson(20);
            var root = (Dictionary<string, object>)Json.Parse(json);
            var g0 = (Dictionary<string, object>)Arr(root["groups"])[0];
            var plan = (Dictionary<string, object>)g0["plan"];
            var tlj = Arr(plan["timeline"]);
            if (tlj.Count != gr.Timeline.Exec.Count) return "timeline count in json";
            var row = (Dictionary<string, object>)tlj[0];
            foreach (string k in new[] { "uid", "tgt", "ammo", "salvo", "state", "why", "t_go", "t_issue", "t_launch", "t_arrive", "fact" }) if (!row.ContainsKey(k)) return "timeline key " + k;
            var fact = (Dictionary<string, object>)row["fact"];
            foreach (string k in new[] { "issue", "launched", "late_s", "dt_order_s" }) if (!fact.ContainsKey(k)) return "fact key " + k;
            var ex = (Dictionary<string, object>)g0["exec"];
            if (ex["status"] as string != "executing" || ex["roe"] as string != "veto" || (double)ex["wave"] != 1 || !ex.ContainsKey("start_t")) return "exec object";
            Run(game, c, 1500, () => gr.ExecStatus != null);
            var after = c.View().Groups[0];
            if (after.Exec != "done" || after.Results.Count != 2 || after.Results[0].State == null) return "view after the strike " + after.Exec + " " + after.Results.Count;
            root = (Dictionary<string, object>)Json.Parse(c.ViewJson(20));
            g0 = (Dictionary<string, object>)Arr(root["groups"])[0];
            var rs = Arr(g0["results"]);
            if (rs.Count != 2) return "results in json";
            foreach (string k in new[] { "id", "state", "fired", "hits", "spread_s" }) if (!((Dictionary<string, object>)rs[0]).ContainsKey(k)) return "result key " + k;
            // the lists are cut by the limit
            root = (Dictionary<string, object>)Json.Parse(c.ViewJson(1));
            if (Arr(((Dictionary<string, object>)Arr(root["groups"])[0])["results"]).Count > 1) return "limit";
            return null;
        }

        // ---- the game clock only
        private static string ExecClock()
        {
            // the world runs on the game clock: the result is the same whatever the wall clock does (the controller never reads one)
            var a = World(4, true); var la = new List<string>(); var ca = Ctl(a, la, null, true);
            Go(ca); ca.Launch(C("group", "A")); Run(a, ca, 1500, () => G(ca).ExecStatus != null);
            var b = World(4, true); var lb = new List<string>(); var cb = Ctl(b, lb, null, true);
            Go(cb); cb.Launch(C("group", "A"));
            for (int i = 0; i < 1500 && G(cb).ExecStatus == null; i++) { System.Threading.Thread.Sleep(0); b.Advance(1); cb.Tick(b.Time); }
            if (a.IssueLog.Count != b.IssueLog.Count || string.Join("|", a.IssueLog) != string.Join("|", b.IssueLog)) return "the orders depend on something else than the game clock";
            return null;
        }

        // a missile that the game never removes must not keep the strike open for ever: the result comes ResultDelayS after the last planned arrival, cancelled or not
        private static string ExecStuck()
        {
            foreach (bool cancel in new[] { false, true })
            {
                var game = World(2, false); var log = new List<string>();
                var c = Ctl(game, log);
                if (Go(c) != null) return "plan";
                var gr = G(c);
                game.NeverArrive.Add(1);
                c.Launch(C("group", "A"));
                if (cancel) Run(game, c, 1500, () => gr.Timeline != null && gr.Timeline.Exec.All(x => x.State == "launched"));
                if (cancel && c.Cancel(C("group", "A")) == null) return "cancel";
                double t0 = game.Time;
                Run(game, c, 2000, () => gr.ExecStatus != null);
                if (gr.ExecStatus == null) return (cancel ? "cancelled " : "") + "strike with a stuck missile never finished: " + (gr.Timeline == null ? "no timeline" : gr.Timeline.Status) + " " + string.Join(" | ", log.Where(l => l.Contains("result wait")).Take(3));
                if (!log.Any(l => l.Contains("strike result wait"))) return "the wait is logged";
            }
            return null;
        }

        // ---- Q6
        private static string RoeVeto()
        {
            var game = World(2, false); var log = new List<string>();
            var c = Ctl(game, log);
            Go(c); c.Launch(C("group", "A"));
            if (game.RoeCalls.Count != 0) return "veto leaves the weapon status alone " + string.Join(",", game.RoeCalls);
            if (c.EffectiveRoe != "veto") return "effective";
            string v = c.ViewJson(20);
            if (!v.Contains("\"veto\":")) return "veto section";
            return null;
        }

        private static string RoeHold()
        {
            var game = World(3, false); var log = new List<string>();
            var p = new StrikeParams { RoeMode = "hold" };
            game.RoeNow[1] = "tight";
            var c = new StrikeController(game, p, log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            if (game.RoeCalls.Count != 2 || game.RoeNow[1] != "hold" || game.RoeNow[2] != "hold") return "hold on entry " + string.Join(",", game.RoeCalls);
            if (!log.Any(l => l.Contains("strike roe A mode=hold uid=1 prev=tight"))) return "roe log";
            c.Group(C("op", "add", "group", "A", "uids", new[] { 3 }));
            if (game.RoeNow[3] != "hold") return "hold for an added aircraft";
            Go(c);
            c.Launch(C("group", "A"));
            if (game.RoeCalls.Count != 3) return "no second call for the same aircraft " + game.RoeCalls.Count;
            // the previous status comes back on removal, on the return of control, on the leave of the tick, on disband; not on the cancel of a strike
            c.Group(C("op", "remove", "group", "A", "uids", new[] { 3 }));
            if (game.RoeNow[3] != "free") return "back on removal " + game.RoeNow[3];
            c.Release(1);
            if (game.RoeNow[1] != "tight") return "back on the return of control " + game.RoeNow[1];
            string cancel = c.Cancel(C("group", "A"));
            if (StrikeController.IsErr(cancel)) return "cancel " + cancel;
            if (game.RoeNow[2] != "hold") return "the cancel keeps the status";
            // a leave of the tick: no missiles
            game.Air.First(a => a.Uid == 2).Missiles[0].Count = 0;
            Run(game, c, 3);
            if (game.RoeNow[2] != "free") return "back on the leave of the tick " + game.RoeNow[2];
            // disband
            var game2 = World(2, false); var c2 = new StrikeController(game2, p, s => { });
            c2.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c2.Group(C("op", "disband", "group", "A"));
            if (game2.RoeNow[1] != "free" || game2.RoeNow[2] != "free") return "back on disband";
            return null;
        }

        private static string RoeFallback()
        {
            var game = World(2, false); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.FireVetoPatched = false;
            if (c.EffectiveRoe != "hold") return "no patch: hold";
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            c.Plan(C("group", "A"));
            c.Launch(C("group", "A"));
            if (game.RoeNow.Count != 2 || game.RoeNow[1] != "hold") return "hold applied";
            if (Cnt(log, "strike roe fallback why=no_fire_veto") != 1) return "the fallback is logged once " + Cnt(log, "strike roe fallback");
            if (G(c).Timeline.Roe != "hold") return "the timeline says hold";
            return null;
        }

        // ---- Q7
        private static string WarmCache()
        {
            var game = World(2, false); var log = new List<string>();
            game.SimTof = true; game.SimTofS = 300; game.SimTofPerNm = 5;
            game.AddPlane(5, "usn_a-6e", "usn_agm-84d", 4, 39.4, 10.1);
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2, 5 }));
            c.Target(C("group", "A", "target_id", 100));
            int t0 = game.TofCalls;
            Run(game, c, 1);   // the first tick pays the JIT of the plan code with a synthetic plan (no game call)
            if (game.TofCalls - t0 != 0 || !log.Any(l => l.Contains("strike warm jit ms="))) return "the first tick is the JIT warm-up " + (game.TofCalls - t0);
            Run(game, c, 1);
            if (game.TofCalls - t0 != 2) return "one missile type per tick, two ranges " + (game.TofCalls - t0);
            Run(game, c, 1);
            if (game.TofCalls - t0 != 4) return "two missile types, two ranges each " + (game.TofCalls - t0);
            Run(game, c, 3);
            if (game.TofCalls - t0 != 4) return "nothing more to warm";
            if (log.Count(l => l.Contains("strike warm ammo=") && l.Contains(" ms=")) != 2) return "warm log";
            int before = game.TofCalls;
            string r = c.Plan(C("group", "A"));
            if (StrikeController.IsErr(r)) return "plan " + r;
            if (game.TofCalls != before) return "after the warm-up the plan asks the game nothing: " + (game.TofCalls - before);
            c.Plan(C("group", "A"));
            if (game.TofCalls != before) return "a repeated plan of the same geometry asks the game nothing: " + (game.TofCalls - before);
            if (c.Tofs.Count == 0 || Math.Abs(c.Tofs.TickTof("usn_agm-84c", 60, 540) - StrikeTotMath.RoughTofS(60, 540) * c.Tofs.Scale("usn_agm-84c")) > 1e-9) return "tick tof is the rough formula times the scale";
            if (!(c.Tofs.Scale("usn_agm-84c") < 1)) return "scale from the simulated 300 s " + c.Tofs.Scale("usn_agm-84c");
            if (!log.Any(l => l.Contains("strike tof_check ammo="))) return "tof_check log";
            // the budget: 8 aircraft x 12 targets after the warm-up
            var big = new StrikeFakeGame { SimTof = true, SimTofS = 400 };
            for (int i = 1; i <= 8; i++) big.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            var ids = new List<int>();
            for (int i = 0; i < 12; i++) { var s = big.AddKidd(200 + i, 41 + 0.03 * (i / 4), 10 + 0.04 * (i % 4)); s.DisplacementT = 5000 + 100 * i; ids.Add(200 + i); }
            var cb = new StrikeController(big, new StrikeParams(), s => { });
            cb.Group(C("op", "create", "group", "A", "uids", Enumerable.Range(1, 8).ToArray()));
            cb.Target(C("group", "A", "mode", "spread", "target_id", 200, "track_ids", ids.ToArray()));
            Run(big, cb, 3);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string rb = cb.Plan(C("group", "A"));
            sw.Stop();
            if (StrikeController.IsErr(rb)) return "big plan " + rb;
            double ms = (double)Obj(rb)["ms"];
            Console.WriteLine("strike warm plan ms=" + ms.ToString("0.0", CultureInfo.InvariantCulture) + " wall=" + sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
            if (ms > 20) return "the plan after the warm-up is over the budget: " + ms;
            return null;
        }

        // ---- P6
        private static string WaitNoSea()
        {
            var game = World(2, false); var log = new List<string>();
            game.Land = p => true;   // everything is land: no sea point
            var c = Ctl(game, log);
            Run(game, c, 12);
            if (G(c).WaitWhy != "no_sea") return "no_sea " + G(c).WaitWhy;
            if (game.Calls.Count != 0) return "no Goto / Orbit without a point: " + string.Join(",", game.Calls);
            if (G(c).Gather != "idle") return "gather idle";
            if (Cnt(log, "strike gather A -> held why=no_sea") != 1) return "the held log once " + Cnt(log, "-> held");
            if (c.View().Groups[0].WaitWhy != "no_sea") return "the view says why";
            // a huge SAM: no point within the search range
            game = World(2, false); log = new List<string>();
            game.Fleet[0].SamMaxNm = 500;
            c = Ctl(game, log);
            Run(game, c, 12);
            if (G(c).WaitWhy != "no_point") return "no_point " + G(c).WaitWhy;
            if (game.Calls.Count != 0) return "no orders for no_point";
            // a launch is refused for both
            c.Plan(C("group", "A"));
            if (c.Launch(C("group", "A")) != "!wait_no_sea") return "no launch without a point";
            return null;
        }
    }
}
