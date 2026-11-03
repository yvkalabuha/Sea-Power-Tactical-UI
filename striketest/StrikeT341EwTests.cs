using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-341 phase D stand sections "t341 <scenario>": the jammer aircraft of a strike on the fake world (member, plan, execution, guard, cancel, air guard, view, deck, save) and the
    // findings of the review of phase C (confirm time, an active attack task, the weapon status of a saved game).
    internal static class StrikeT341EwTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "ew-member", "ew-plan-ctl", "ew-exec", "ew-guard", "ew-cancel", "ew-airguard", "view-ew", "ew-save", "ew-deck", "ew-jam-refused", "ew-guard-jamming", "exec-confirm45", "exec-active-task", "roe-save", "ew-exp2" };

        public static string Dispatch(string n)
        {
            switch (n)
            {
                case "ew-member": return EwMember();
                case "ew-plan-ctl": return EwPlanCtl();
                case "ew-exec": return EwExec();
                case "ew-guard": return EwGuard();
                case "ew-cancel": return EwCancel();
                case "ew-airguard": return EwAirGuard();
                case "view-ew": return ViewEw();
                case "ew-save": return EwSave();
                case "ew-deck": return EwDeck();
                case "exec-confirm45": return ExecConfirm45();
                case "ew-jam-refused": return EwJamRefused();
                case "ew-guard-jamming": return EwGuardJamming();
                case "exec-active-task": return ExecActiveTask();
                case "roe-save": return RoeSave();
                case "ew-exp2": return EwExp2();
            }
            return StrikeT341CalibTests.Dispatch(n);
        }

        private static readonly bool Trace = Environment.GetEnvironmentVariable("STRIKE_TRACE") != null;

        private static StrikeFakeGame World(int ews = 1, double ewLat = 38.9, int planes = 2)
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            for (int e = 0; e < ews; e++) g.AddEw(5 + e, ewLat, 10.2 + 0.1 * e);
            return g;
        }

        private static StrikeController Ctl(StrikeFakeGame g, List<string> log, StrikeParams p = null)
        {
            var c = new StrikeController(g, p ?? new StrikeParams(), l => { log.Add(l); if (Trace) Console.WriteLine("  t=" + g.Time.ToString("0", CultureInfo.InvariantCulture) + " " + l); });
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Where(a => a.ObjType == "Aircraft").Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
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
        private static StrikeEwState Ew(StrikeController c, int uid = 5) { return G(c).EwExec.FirstOrDefault(x => x.Uid == uid); }
        private static string St(StrikeController c, int uid = 5) { var s = Ew(c, uid); return s == null ? "none" : s.State; }
        private static double T(string call) { return double.Parse(call.Split(' ')[0], CultureInfo.InvariantCulture); }

        private static string Go(StrikeController c)
        {
            string r = c.Plan(C("group", "A"));
            return StrikeController.IsErr(r) ? "plan " + r : null;
        }

        private static string EwMember()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            string w;
            if (!c.IsMember(5, out w) || w != "A") return "the jammer is a member";
            if (c.AdmitWhy(5, "A", null, null) != null) return "the ring admits the jammer: " + c.AdmitWhy(5, "A", null, null);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            foreach (var v in gr.Variants)
            {
                if (v.Assignments.Any(a => a.Uid == 5)) return "the jammer gets no missiles";
                if (v.Ew.Count != 1 || v.Ew[0].Uid != 5 || v.Ew[0].Why != "ok") return "variant ew " + (v.Ew.Count == 0 ? "none" : v.Ew[0].Why);
                if (!v.Ew[0].CoverIds.Contains(100)) return "the main target is covered";
            }
            // a jammer that cannot work is refused, not taken
            var broken = game.AddEw(9, 38.9, 10.5); broken.Ew.Operable = false; StrikeAirMath.Classify(new[] { broken });
            string r = c.Group(C("op", "add", "group", "A", "uids", new[] { 9 }));
            if (r != "!not_striker|ew_inoperable") return "inoperable " + r;
            if (c.AdmitWhy(9, "A", null, null) != "ew_inoperable") return "ring: inoperable";
            var noApi = game.AddEw(10, 38.9, 10.6); noApi.Ew.Capable = false; StrikeAirMath.Classify(new[] { noApi });
            if (c.Group(C("op", "add", "group", "A", "uids", new[] { 10 })) != "!not_striker|ew_no_api") return "no api";
            // the jammer never flies to its station before the launch and never switches on
            Run(game, c, 120);
            if (game.JamCalls.Count != 0) return "no Jam before the launch " + string.Join("|", game.JamCalls);
            if (game.GotoAlts.Any(x => x.StartsWith("5:25000:", StringComparison.Ordinal))) return "no station route before the launch";
            // removing a jammer that is on: the station goes off (the plan is counted again: after 120 s it is late and every row would be skipped, the strike over before the jammer works)
            if (Go(c) != null) return "replan";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => Ew(c) != null && Ew(c).On);
            if (Ew(c) == null || !Ew(c).On) return "the jammer got on " + St(c);
            c.Group(C("op", "remove", "group", "A", "uids", new[] { 5 }));
            if (!game.JamCalls.Last().Contains(" 5 off ") || game.JamOn[5]) return "remove switches the station off " + game.JamCalls.Last();
            return null;
        }

        private static string EwPlanCtl()
        {
            var game = World(1, 35.0); var log = new List<string>();   // far: 270 nm from the area
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var v = G(c).Variants[0];
            var late = v.Warnings.FirstOrDefault(x => x.StartsWith("ew_late:5:", StringComparison.Ordinal));
            if (late == null) return "ew_late for a far jammer: " + string.Join(";", v.Warnings) + " late_s=" + (v.Ew.Count > 0 ? v.Ew[0].LateS.ToString("0") : "?");
            double t0 = v.T, shift = v.Ew[0].ShiftS;
            if (shift <= 0 || Math.Abs(shift % 60) > 1e-6) return "shift in whole minutes " + shift;
            string r = c.Plan(C("group", "A", "shift_s", shift));
            if (StrikeController.IsErr(r)) return "shift plan " + r;
            var v2 = G(c).Variants[0];
            if (v2.T < t0 + shift - 1e-6) return "T moved by the shift " + (v2.T - t0) + " < " + shift;
            if (v2.Warnings.Any(x => x.StartsWith("ew_late:", StringComparison.Ordinal))) return "ew_late is gone after the shift: " + string.Join(";", v2.Warnings);
            if (c.Plan(C("group", "A", "shift_s", -5)) != "!bad_args|shift_s") return "bad shift";
            // without a jammer: nothing
            var g2 = World(0); var c2 = Ctl(g2, new List<string>());
            if (Go(c2) != null) return "plan 2";
            if (G(c2).Variants[0].Ew.Count != 0 || G(c2).Variants[0].Warnings.Any(x => x.StartsWith("ew_", StringComparison.Ordinal))) return "no jammer, no ew plan";
            return null;
        }

        private static string EwExec()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            var pl = gr.Variants[0].Ew[0];
            double t0 = game.Time;
            c.Launch(C("group", "A"));
            if (St(c) != "wait") return "state after the launch " + St(c);
            var seen = new List<string>(); string last = null;
            double minKidd = double.MaxValue, routeAt = -1;
            Run(game, c, 3500, () => gr.ExecStatus != null && (St(c) == "back" || St(c) == "none") && game.JamCalls.Any(x => x.Contains(" 5 off ")), () =>
            {
                string st = St(c);
                if (st != last) { seen.Add(st); last = st; }
                var ac = game.Air.First(a => a.Uid == 5);
                if (st != "wait" && st != "back") minKidd = Math.Min(minKidd, AswMath.DistNm(new GeoPt(ac.Lat, ac.Lon), new GeoPt(game.Fleet[0].Lat, game.Fleet[0].Lon)));
                if (routeAt < 0 && game.GotoAlts.Any(x => x.StartsWith("5:25000:", StringComparison.Ordinal))) routeAt = game.Time;
            });
            string path = string.Join(">", seen).Replace(">none", ">back");   // a jammer of a group with no missiles left goes home (leaves the group) in the tick it is done
            if (path != "wait>to_station>on_station>jamming>back") return "states " + string.Join(">", seen);
            if (routeAt < 0) return "no route to the station";
            if (pl.TDepart > t0 + 10 && routeAt < pl.TDepart - 2) return "left before TDepart " + (pl.TDepart - routeAt).ToString("0.0");
            var ons = game.JamCalls.Where(x => x.Contains(" 5 on ")).ToList(); var offs = game.JamCalls.Where(x => x.Contains(" 5 off ")).ToList();
            if (ons.Count != 1 || offs.Count != 1) return "one on, one off " + string.Join("|", game.JamCalls);
            double on = T(ons[0]), off = T(offs[0]);
            double firstArr = game.Arrivals.Count == 0 ? double.MaxValue : game.Arrivals.Min(a => a.t), lastArr = game.Arrivals.Count == 0 ? 0 : game.Arrivals.Max(a => a.t);
            if (game.Arrivals.Count == 0) return "no arrivals";
            if (on > firstArr - 20) return "on before the first arrival: " + (firstArr - on).ToString("0") + " s";
            if (on > pl.TOn + 3 && on > ArriveAtStation(log) + 3) return "on by TOn: on=" + (on - t0).ToString("0") + " t_on=" + (pl.TOn - t0).ToString("0");
            if (off < lastArr + 14 || off > lastArr + 25) return "off = last arrival + 15 s: off - last = " + (off - lastArr).ToString("0.0");
            if (minKidd < 90 + 5 - 0.5) return "never inside the SAM zone: " + minKidd.ToString("0.0");
            if (!log.Any(l => l.Contains("strike ew plan A uid=5 ")) || !log.Any(l => l.Contains("strike ew on uid=5")) || !log.Any(l => l.Contains("strike ew off uid=5"))) return "ew log lines";
            Run(game, c, 5);
            if (!(game.Calls.Any(x => x == "rtb 5") || game.Calls.Count(x => x.StartsWith("goto 5 ", StringComparison.Ordinal)) >= 2)) return "after the strike the jammer goes to the wait point or home";
            return null;
        }

        private static double ArriveAtStation(List<string> log) { return 0; }

        /// <summary>Phase E1 (AF1): the switch-on of the jammer against the real plan (TDetect, TOn). (a) the margin rule switched off (EwOnMarginNm very negative): ON in [TOn - 1, TOn + 2]; (b) the default margin: a striker inside DetNm + margin switches ON before TOn (here and in Decide); (c) negative control: the band check fails for TOn - 120.</summary>
        private static string EwExp2()
        {
            double[] ons = new double[2], tons = new double[2];
            for (int pass = 0; pass < 2; pass++)
            {
                var game = new StrikeFakeGame(); var log = new List<string>();
                for (int i = 1; i <= 2; i++) game.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 36.0 - 0.02 * i, 10 + 0.02 * i);   // far: 300 nm from the target
                game.AddKidd(100, 41.0, 10.0); game.AddEw(5, 38.9, 10.2);
                var par = new StrikeParams(); if (pass == 0) par.EwOnMarginNm = -500;
                var c = Ctl(game, log, par);
                if (Go(c) != null) return "plan";
                var pl = G(c).Variants[0].Ew[0];
                if (!(pl.TDetect > 0) || !(pl.TOn > 0) || Math.Abs(pl.TOn - (pl.TDetect - par.EwOnLeadS)) > 1e-6) return "plan TDetect/TOn " + pl.TDetect + " " + pl.TOn;
                c.Launch(C("group", "A"));
                Run(game, c, 3500, () => game.JamCalls.Any(x => x.Contains(" 5 on ")));
                var l = game.JamCalls.Where(x => x.Contains(" 5 on ")).ToList();
                if (l.Count != 1) return "pass " + pass + ": one on " + string.Join("|", game.JamCalls);
                ons[pass] = T(l[0]); tons[pass] = pl.TOn;
            }
            Func<double, double, bool> inBand = (o, tOn) => o >= tOn - 1 && o <= tOn + 2;
            if (!inBand(ons[0], tons[0])) return "(a) on=" + ons[0].ToString("0") + " t_on=" + tons[0].ToString("0");
            if (inBand(ons[0], tons[0] - 120)) return "(c) the control must fail for TOn - 120";
            if (!(ons[1] < tons[1] - 5)) return "(b) the margin rule switches on before TOn: on=" + ons[1].ToString("0") + " t_on=" + tons[1].ToString("0");
            var p = new StrikeParams();
            // (b) the state machine: a striker inside the detection range + margin switches the jammer on before TOn
            var s = new StrikeEwState { State = "on_station" };
            var plan = new StrikeEwPlan { Uid = 5, TOn = 1000, TDetect = 1060, TOff = 1200 };
            string far = StrikeEwMath.Decide(s, plan, 900, 0, 200, 100, false, 0, -1, false, false, p);
            string near = StrikeEwMath.Decide(s, plan, 900, 0, 100 + p.EwOnMarginNm - 1, 100, false, 0, -1, false, false, p);
            string onTime = StrikeEwMath.Decide(s, plan, 1000, 0, 200, 100, false, 0, -1, false, false, p);
            if (far != "on_station" || near != "jamming" || onTime != "jamming") return "(b) decide far=" + far + " near=" + near + " ontime=" + onTime;
            return null;
        }

        private static string EwGuard()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => St(c) == "to_station");
            if (St(c) != "to_station") return "to_station " + St(c);
            // a hostile ship appears on the jammer's way
            var ac = game.Air.First(a => a.Uid == 5);
            var ship = game.AddKidd(102, ac.Lat + 0.3, ac.Lon);
            game.Calls.Clear();
            Run(game, c, 3);
            if (St(c) != "guard" && St(c) != "no_station") return "guard " + St(c);
            if (!log.Any(l => l.Contains("strike ew guard uid=5 why=sam_zone ship=102"))) return "guard log " + string.Join(" | ", log.Where(l => l.Contains("ew")).Take(8));
            if (!game.Calls.Any(x => x.StartsWith("goto 5 ", StringComparison.Ordinal))) return "the jammer is sent to the wait point";
            if (game.JamCalls.Any(x => x.Contains(" on "))) return "never switched on inside a zone";
            return null;
        }

        private static string EwCancel()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => St(c) == "jamming");
            if (St(c) != "jamming" || !game.JamOn[5]) return "jamming " + St(c);
            double t = game.Time;
            string r = c.Cancel(C("group", "A"));
            if (StrikeController.IsErr(r)) return "cancel " + r;
            if (game.JamOn[5] || T(game.JamCalls.Last()) != t || !game.JamCalls.Last().Contains(" 5 off ")) return "off in the same tick " + game.JamCalls.Last();
            if (St(c) != "back") return "back " + St(c);
            return null;
        }

        // review of D: a refused switch on is followed by a switch off (IsOn is set before the task), and a ship that appears while the jammer jams switches it off in the same tick
        private static string EwJamRefused()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            game.JamErr = "failed:not_targeted";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => St(c) == "no_station");
            if (St(c) != "no_station") return "three refusals end in no_station: " + St(c);
            int ons = game.JamCalls.Count(x => x.Contains(" 5 on ")), offs = game.JamCalls.Count(x => x.Contains(" 5 off "));
            if (ons < 3 || offs < ons) return "every refused on is followed by an off: on=" + ons + " off=" + offs;
            if (!game.JamCalls.Last().Contains(" 5 off ")) return "last call is off " + game.JamCalls.Last();
            return null;
        }

        private static string EwGuardJamming()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => St(c) == "jamming");
            if (St(c) != "jamming" || !game.JamOn[5]) return "jamming " + St(c);
            var ac = game.Air.First(a => a.Uid == 5);
            game.AddKidd(102, ac.Lat + 0.2, ac.Lon);
            Run(game, c, 2);
            if (St(c) != "guard" && St(c) != "no_station") return "guard " + St(c);
            if (game.JamOn[5]) return "the station is off when the jammer is guarded";
            return null;
        }

        private static string EwAirGuard()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log, new StrikeParams { EwReturnClearS = 0 });   // T-342: 0 = the return of v1 (the new rule waits EwReturnClearS: t342 ew-clear)
            bool retreat = false; int steers = 0, releases = 0;
            c.AirRetreatNow = (u, gn) => retreat && u == 5 && gn == "A";
            c.AirSteerEw = (u, r, m) => { steers++; return null; };
            c.AirReleaseEw = u => { releases++; };
            var groups = c.AirGuardGroups();
            if (groups.Count != 1 || groups[0].Window != "strike" || groups[0].Name != "A" || groups[0].Kind != "ew" || groups[0].Uids.Count != 1 || groups[0].Uids[0] != 5) return "air guard groups: only the jammer " + groups.Count;
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => St(c) == "to_station" || St(c) == "on_station");
            string before = St(c);
            if (before != "to_station" && before != "on_station") return "flying " + before;
            retreat = true;
            Run(game, c, 3);
            if (St(c) != "retreat" || steers == 0) return "retreat " + St(c) + " steers=" + steers;
            if (game.JamCalls.Any(x => x.Contains(" on "))) return "no jam while retreating";
            if (game.IssueLog.Count != 0 && game.IssueLog.Any(x => x.Contains(" 5 "))) return "the strikers' orders are not touched";
            retreat = false;
            Run(game, c, 3);
            if (St(c) != "to_station" && St(c) != "on_station" && St(c) != "jamming") return "back to the station " + St(c);
            if (releases == 0) return "the steer is released";
            return null;
        }

        private static string ViewEw()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var root = Obj(c.ViewJson(20));
            if (!(root["ew_capable"] is bool) || !(bool)root["ew_capable"]) return "ew_capable";
            var g0 = (Dictionary<string, object>)Arr(root["groups"])[0];
            var plan = (Dictionary<string, object>)g0["plan"];
            var v0 = (Dictionary<string, object>)Arr(plan["variants"])[0];
            var pe = Arr(v0["ew"]);
            if (pe.Count != 1) return "plan.variants[0].ew";
            var e0 = (Dictionary<string, object>)pe[0];
            foreach (string k in new[] { "uid", "station", "aim", "cone_deg", "bearing_deg", "cover", "uncovered", "bands_missing", "t_depart", "t_on", "t_off", "late_s", "why" }) if (!e0.ContainsKey(k)) return "plan ew key " + k;
            if (!(((Dictionary<string, object>)e0["station"])["lat"] is double) || !(e0["cover"] is List<object>) || !(e0["why"] is string)) return "plan ew types";
            if (!plan.ContainsKey("t_detect")) return "t_detect";
            if (Arr(g0["ew"]).Count != 0) return "no state before the launch";
            c.Launch(C("group", "A"));
            Run(game, c, 5);
            root = Obj(c.ViewJson(20)); g0 = (Dictionary<string, object>)Arr(root["groups"])[0];
            var ge = Arr(g0["ew"]);
            if (ge.Count != 1) return "groups[].ew after the launch";
            var s0 = (Dictionary<string, object>)ge[0];
            foreach (string k in new[] { "uid", "type", "state", "on", "station", "aim", "cone_deg", "bearing_deg", "cover", "uncovered", "bands_missing", "t_depart", "t_on", "t_off", "late_s", "on_t", "off_t", "why" }) if (!s0.ContainsKey(k)) return "state key " + k;
            if (!(s0["state"] is string) || !(s0["on"] is bool)) return "state types";
            // the window view
            var wv = c.View();
            if (wv.Groups[0].Ew.Count != 1 || wv.Groups[0].Ew[0].Station == null || wv.Groups[0].Cards.Count == 0 || wv.Groups[0].Cards[0].Ew.Count != 1) return "window view of the jammer";
            // a group without a jammer: an empty array
            var g2 = World(0); var c2 = Ctl(g2, new List<string>());
            Go(c2);
            var r2 = Obj(c2.ViewJson(20));
            if (Arr(((Dictionary<string, object>)Arr(r2["groups"])[0])["ew"]).Count != 0) return "empty ew array";
            return null;
        }

        private static string EwSave()
        {
            var game = World(); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            Run(game, c, 3000, () => Ew(c) != null && Ew(c).On);
            if (Ew(c) == null || !Ew(c).On) return "on " + St(c);
            string json = c.Export();
            if (json == null || !json.Contains("\"ew_on\":[5]")) return "save has ew_on " + json;
            var game2 = World(); var log2 = new List<string>();
            var c2 = new StrikeController(game2, new StrikeParams(), log2.Add);
            string err; c2.Import(json, out err);
            if (err != null) return "import " + err;
            if (game2.JamCalls.Count != 0) return "nothing before the first tick";
            c2.Tick(game2.Time + 1);
            if (game2.JamCalls.Count != 1 || !game2.JamCalls[0].Contains(" 5 off ") || game2.JamOn[5]) return "switched off in the first tick " + string.Join("|", game2.JamCalls);
            c2.Tick(game2.Time + 3);
            if (game2.JamCalls.Count != 1) return "only once";
            return null;
        }

        private static string EwDeck()
        {
            var game = World(0); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            var deck = new FakeDeck(); c.Deck = deck;
            game.Deck.Add(new StrikeDeckItem { DeckUid = 700, Key = "task:9", Type = "usn_ea-6b", Loadout = "EW", State = "ready", DeckName = "CV", Lat = 39.0, Lon = 10.0, RemainS = 0, Ew = new StrikeEw { RangeNm = 200, ConeDeg = 30, Bands = new[] { "X" }, Capable = true } });
            string r = c.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:9" }));
            if (StrikeController.IsErr(r)) return "a jammer task of the deck is taken " + r;
            c.Target(C("group", "A", "target_id", 100));
            game.AddKidd(100);
            c.Tick(1000);
            if (deck.Reqs.Count != 1 || deck.Reqs[0].Key != "task:9" || deck.Reqs[0].Window != "strike") return "allow request";
            game.Deck.Clear();
            game.AddEw(60, 39.0, 10.01);
            game.AddPlane(61, "usn_a-6e", "usn_agm-84c", 4, 39.0, 10.0);
            game.Time = 1001;
            c.Tick(1001);
            string w;
            if (!c.IsMember(60, out w) || w != "A") return "the jammer is adopted by its type";
            if (c.IsMember(61, out w)) return "a striker is not adopted for a jammer task";
            return null;
        }

        private static string ExecConfirm45()
        {
            var game = World(0); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            game.NoLaunch = true;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            foreach (int uid in new[] { 1, 2 })
            {
                var ts = game.IssueLog.Where(x => x.Split(' ')[1] == uid.ToString(CultureInfo.InvariantCulture)).Select(T).ToList();
                if (ts.Count < 2) continue;
                if (ts[1] - ts[0] < 45) return "no repeat before 45 s: uid " + uid + " " + (ts[1] - ts[0]);
                if (ts[1] - ts[0] > 52) return "the repeat after 46 s: uid " + uid + " " + (ts[1] - ts[0]);
            }
            if (game.IssueLog.Count == 0) return "orders were given";
            return null;
        }

        private static string ExecActiveTask()
        {
            var game = World(0); var log = new List<string>();
            var c = Ctl(game, log);
            if (Go(c) != null) return "plan";
            var gr = G(c);
            c.Launch(C("group", "A"));
            game.NoLaunch = true;
            game.AttackTask["1:100"] = true; game.AttackTask["2:100"] = true;
            Run(game, c, 1500, () => gr.ExecStatus != null);
            if (game.IssueCalls.Values.Any(n => n != 1)) return "no repeat while the game holds an attack task " + string.Join(",", game.IssueCalls.Select(kv => kv.Key + ":" + kv.Value));
            if (gr.LastExec.Any(x => x.State != "failed" || x.Why != "no_launch")) return "failed no_launch: " + string.Join(",", gr.LastExec.Select(x => x.State + ":" + x.Why));
            var fail = log.FirstOrDefault(l => l.Contains("state=issued->failed") || l.Contains("state=run_in->failed") || l.Contains("->failed"));
            return null;
        }

        private static string RoeSave()
        {
            var game = World(0); var log = new List<string>();
            var p = new StrikeParams { RoeMode = "hold" };
            var c = Ctl(game, log, p);
            if (Go(c) != null) return "plan";
            c.Launch(C("group", "A"));
            if (!game.RoeNow.ContainsKey(1) || game.RoeNow[1] != "hold") return "hold on launch";
            string json = c.Export();
            if (json == null || !json.Contains("\"roe_prev\"") || !json.Contains("\"roe\":\"hold\"")) return "the previous status is saved " + json;
            // the loaded game: the aircraft are on hold, the controller knows what to give back
            var g2 = World(0); g2.RoeNow[1] = "hold"; g2.RoeNow[2] = "hold"; var log2 = new List<string>();
            var c2 = new StrikeController(g2, new StrikeParams { RoeMode = "hold" }, log2.Add);
            string err; c2.Import(json, out err);
            c2.Group(C("op", "remove", "group", "A", "uids", new[] { 1 }));
            if (!g2.RoeCalls.Any(x => x == "1 free (was hold)")) return "the saved status comes back " + string.Join("|", g2.RoeCalls);
            // an old save without roe_prev: free, said in the log
            string old = Regex.Replace(json, ",\"roe_prev\":\\{[^}]*\\}", "");
            if (old == json) return "old save built";
            var g3 = World(0); g3.RoeNow[1] = "hold"; g3.RoeNow[2] = "hold"; var log3 = new List<string>();
            var c3 = new StrikeController(g3, new StrikeParams { RoeMode = "hold" }, log3.Add);
            c3.Import(old, out err);
            if (!log3.Any(l => l == "[Strike] strike roe restored_default uid=1")) return "restored_default log " + string.Join("|", log3);
            c3.Group(C("op", "remove", "group", "A", "uids", new[] { 1 }));
            if (!g3.RoeCalls.Any(x => x == "1 free (was hold)")) return "default free " + string.Join("|", g3.RoeCalls);
            return null;
        }
    }
}
