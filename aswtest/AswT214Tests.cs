using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-214: the goto spam after a takeover, the shooter release, the game's own return home (game_rtb). `aswtest t214 <name>`.
    internal static class AswT214Tests
    {
        private static readonly string[] Names = { "takeover-pure", "game-rtb-keeps", "remove-releases", "one-shooter", "goto-gate", "warn-text" };

        private static Dictionary<string, object> P(string json) { return AswT173AttackTests.P(json); }
        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }
        private static bool Log(AswFakeGame f, string needle) { return AswT173AttackTests.Log(f, needle); }
        private static int Count(AswFakeGame f, string needle) { return f.Logs.Count(l => l.Contains(needle)); }
        private static int Calls(AswFakeGame f, string call) { return f.Calls.Count(x => x == call); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "takeover-pure": e = TakeoverPure(); break;
                    case "game-rtb-keeps": e = GameRtbKeeps(); break;
                    case "remove-releases": e = RemoveReleases(); break;
                    case "one-shooter": e = OneShooter(); break;
                    case "goto-gate": e = GotoGate(); break;
                    default: e = WarnText(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string TakeoverPure()
        {
            if (AswDipTasks.Takeover("player", "Default", false, false, false) != "player") return "player where";
            foreach (string st in new[] { "Winchester", "BingoFuel", "ReturnToBase" })
                if (AswDipTasks.Takeover("air", st, false, true, false) != "game_rtb") return st + " + changed is not game_rtb";
            if (AswDipTasks.Takeover("air", "Winchester", false, true, true) != null) return "own rtb is a takeover";
            if (AswDipTasks.Takeover("air", "Default", false, true, true) != null) return "own rtb with Default is a takeover";
            if (AswDipTasks.Takeover("air", "Default", true, true, false) != null) return "evading is a takeover";
            if (AswDipTasks.Takeover("air", "Winchester", false, false, false) != null) return "no change is a takeover";
            if (AswDipTasks.Takeover("air", "Default", false, true, false) != "player") return "Default + changed";
            if (AswDipTasks.Takeover("air", null, false, true, false) != "player") return "null state + changed";
            return null;
        }

        private static AswController CourseGroup(AswFakeGame f, int n)
        {
            for (int i = 1; i <= n; i++) f.Mem.Add(AswFakeGame.Helo(i, "air"));
            var c = AswT173AttackTests.Make(f);
            string uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            c.SearchCourse(P("{\"group\":\"G\",\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            return c;
        }

        private static int Members(AswController c) { int n = 0; foreach (var g in c.View().Groups) n += g.Members.Count; return n; }

        private static string GameRtbKeeps()
        {
            // the game sends one helicopter home (Winchester): it stays, one line, no player_took, no new orders
            var f = new AswFakeGame();
            var c = CourseGroup(f, 2);
            f.Mem[0].AiState = "Winchester"; f.Changed.Add(1);
            Run(c, f, 3);
            if (Members(c) != 2) return "member removed: " + Members(c);
            if (f.Events.Exists(e => e.Contains("player_took"))) return "player_took event";
            if (!f.Events.Exists(e => e.StartsWith("asw_warn") && e.Contains("game_rtb"))) return "no asw_warn game_rtb";
            if (Count(f, "asw member game_rtb G uid=1") != 1) return "game_rtb lines " + Count(f, "asw member game_rtb");
            int d0 = f.Drops.Count(x => x.Uid == 1), p0 = f.DipUids.Count(x => x == 1), g0 = Calls(f, "goto:1");
            Run(c, f, 60);
            if (Count(f, "asw member game_rtb G uid=1") != 1) return "repeated: " + Count(f, "asw member game_rtb");
            if (f.Drops.Count(x => x.Uid == 1) != d0 || f.DipUids.Count(x => x == 1) != p0 || Calls(f, "goto:1") != g0) return "the tactic still orders the leaving helicopter";
            // it lands: the group follows it (adopt) instead of losing it
            f.Mem.RemoveAll(m => m.Uid == 1);
            Run(c, f, 3);
            if (!Log(f, "asw adopt") && !Log(f, "adopt")) return "no landing follow: " + string.Join(" | ", f.Logs.Skip(Math.Max(0, f.Logs.Count - 6)).ToArray());

            // a Default state with a changed task list is a real takeover, with the evidence in the line
            var f2 = new AswFakeGame();
            var c2 = CourseGroup(f2, 2);
            f2.Mem[0].AiState = "Default"; f2.Changed.Add(1);
            Run(c2, f2, 3);
            if (Members(c2) != 1) return "player takeover: members " + Members(c2);
            if (!Log(f2, "asw member player_took G uid=1") || !Log(f2, "ai=Default")) return "no player_took line with ai=";
            if (!f2.Events.Exists(e => e.StartsWith("asw_warn") && e.Contains("player_took"))) return "no player_took warn";

            // the mod's own return home of an idle strike helicopter is neither
            var f3 = new AswFakeGame();
            var h = AswFakeGame.Helo(1, "air"); h.Torpedoes = 2; f3.Mem.Add(h); f3.FixPos.Add(1);
            var c3 = AswT173AttackTests.Make(f3, cf => cf.Strike.IdleS = 30);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c3.Order(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            Run(c3, f3, 60);
            if (!f3.Calls.Contains("rtb:1")) return "setup: no own rtb";
            f3.Mem[0].AiState = "ReturnToBase"; f3.Changed.Add(1);
            Run(c3, f3, 5);
            if (Members(c3) != 1) return "own rtb: member removed";
            if (f3.Events.Exists(e => e.Contains("player_took") || e.Contains("game_rtb"))) return "own rtb: a takeover event";
            return null;
        }

        // ---- strike helpers -------------------------------------------------------------------------------------------

        private static AswContact Sub(AswFakeGame f, int id, double eastNm, double northNm = 0)
        {
            var p = AswMath.Offset(AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, eastNm), 0, northNm);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = "hostile", Live = true, ClassName = "Kilo", Classified = true, MapPosture = "Enemy" };
            f.Subs.Add(s);
            return s;
        }

        private static void Helo(AswFakeGame f, int uid)
        {
            var m = AswFakeGame.Helo(uid, "air"); m.Torpedoes = 2; m.Lat = f.Tgt.Lat; m.Lon = f.Tgt.Lon;
            f.Mem.Add(m); f.FixPos.Add(uid);
        }

        private static AswController Strike(AswFakeGame f)
        {
            var c = AswT173AttackTests.Make(f);
            string uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            string r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        private static string Dump(AswFakeGame f) { return AswT173AttackTests.Dump(f); }

        private static string RemoveReleases()
        {
            // 1: a strike group; the track fades (reacquire); the player takes the shooter -> it leaves the run, no goto goes to it
            var f = new AswFakeGame();
            Helo(f, 1); Helo(f, 2);
            var sub = Sub(f, 11, 6);
            var c = Strike(f);
            Run(c, f, 30);
            int sh = c.RunShooter(11);
            if (sh == 0) return "no shooter assigned: " + Dump(f);
            sub.Live = false;
            Run(c, f, 3);
            if (c.RunState(11) != "reacquire") return "not in reacquire: " + c.RunState(11);
            f.Changed.Add(sh);
            int g0 = Calls(f, "goto:" + sh);
            Run(c, f, 30);
            if (c.RunShooter(11) == sh) return "the intercepted shooter still holds the run";
            if (c.RunState(11) != "reacquire") return "the state changed: " + c.RunState(11);
            if (Calls(f, "goto:" + sh) > g0 + 1) return "goto still goes to the intercepted aircraft: " + (Calls(f, "goto:" + sh) - g0);

            // 2: group remove gives the same
            var f2 = new AswFakeGame();
            var s2 = Sub(f2, 7, 6);
            var m2 = AswT173AttackTests.Shooter(f2, 1, 6, 2, s2);
            var c2 = AswT173AttackTests.Group(f2);
            c2.StartStrikeRun("S", 7, 1, "shooter");
            Run(c2, f2, 3);
            s2.Live = false;
            Run(c2, f2, 3);
            if (c2.RunState(7) != "reacquire" || c2.RunShooter(7) != 1) return "setup2: " + c2.RunState(7) + " " + c2.RunShooter(7);
            c2.Group(P("{\"op\":\"remove\",\"group\":\"S\",\"uids\":[1]}"));
            if (c2.RunShooter(7) != 0) return "group remove keeps the shooter";
            int g2 = Calls(f2, "goto:1");
            Run(c2, f2, 30);
            if (Calls(f2, "goto:1") != g2) return "goto after group remove";

            // 3: in the water the id is kept for the torpedo; after the assessment the shooter is replaced
            var f3 = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 40 };
            var s3 = AswT173AttackTests.AddSub(f3);
            AswT173AttackTests.Shooter(f3, 1, 1.0, 3, s3);
            var c3 = AswT173AttackTests.Group(f3);
            c3.StartStrikeRun("S", s3.Id, 1, "shooter");
            Run(c3, f3, 8);
            if (c3.RunState(s3.Id) != "in_water") return "setup3: " + c3.RunState(s3.Id);
            c3.Group(P("{\"op\":\"remove\",\"group\":\"S\",\"uids\":[1]}"));
            if (c3.RunShooter(s3.Id) != 1) return "in_water: the id must stay for the torpedo";
            Run(c3, f3, 120);
            if (c3.RunShooter(s3.Id) == 1) return "after the assessment the removed shooter still holds the run: " + c3.RunState(s3.Id);
            return null;
        }

        private static string OneShooter()
        {
            var f = new AswFakeGame();
            Helo(f, 1);
            var a = Sub(f, 11, 6);
            var c = Strike(f);
            Run(c, f, 30);
            if (c.RunShooter(11) != 1) return "setup: A has no shooter: " + c.RunState(11) + " " + Dump(f);
            a.Live = false;
            Run(c, f, 3);
            if (c.RunState(11) != "reacquire") return "A is not in reacquire: " + c.RunState(11);
            Sub(f, 12, 8, 3);
            Run(c, f, 40);
            if (c.RunShooter(12) != 1) return "B has no shooter: " + c.RunState(12) + " " + Dump(f);
            if (c.RunShooter(11) != 0) return "A keeps the shooter of B: " + c.RunShooter(11);
            if (c.RunState(11) != "reacquire") return "A state " + c.RunState(11);
            // no alternation: the waiting points go only to the run that holds the aircraft
            int g0 = Calls(f, "goto:1");
            Run(c, f, 60);
            if (Calls(f, "goto:1") - g0 > 3) return "goto alternation: " + (Calls(f, "goto:1") - g0);
            return null;
        }

        private static string GotoGate()
        {
            foreach (double scale in new[] { 4.0, 1.0 })
            {
                var f = new AswFakeGame { TimeScale = scale };
                var sub = Sub(f, 7, 6);
                AswT173AttackTests.Shooter(f, 1, 6, 2, sub);
                var c = AswT173AttackTests.Group(f);
                f.GotoErrors[1] = "not_controllable";
                c.StartStrikeRun("S", 7, 1, "shooter");
                sub.Live = false;
                int ticks = scale > 1 ? 240 : 120;
                Run(c, f, ticks);
                int lines = Count(f, "uid=1 goto failed: not_controllable");
                if (lines < 1 || lines > 1) return "x" + scale + ": goto failed lines " + lines;
                if (Calls(f, "goto:1") > 2) return "x" + scale + ": goto calls " + Calls(f, "goto:1");
                if (c.RunShooter(7) != 0) return "x" + scale + ": the aircraft still holds the run";
                if (scale > 1)
                {
                    // the error is gone and the pause is over: the goto goes again
                    f.GotoErrors.Clear();
                    Run(c, f, 10);
                    int g0 = Calls(f, "goto:1");
                    c.StartStrikeRun("S", 7, 1, "shooter");
                    Run(c, f, 5);
                    if (Calls(f, "goto:1") <= g0) return "no goto after the pause: " + Dump(f);
                    if (Count(f, "uid=1 goto failed") != lines) return "new failure lines after the error was gone";
                }
            }
            // another error code: the aircraft stays on the run, but the goto is held for the same pause (the throttle alone repeated it every 60 s)
            {
                var f = new AswFakeGame { TimeScale = 4 };
                var sub = Sub(f, 7, 6);
                AswT173AttackTests.Shooter(f, 1, 6, 2, sub);
                var c = AswT173AttackTests.Group(f);
                f.GotoErrors[1] = "no_route";
                c.StartStrikeRun("S", 7, 1, "shooter");
                sub.Live = false;
                Run(c, f, 230);
                if (Count(f, "uid=1 goto failed: no_route") != 1 || Calls(f, "goto:1") > 1) return "other error: lines " + Count(f, "uid=1 goto failed: no_route") + " calls " + Calls(f, "goto:1");
                if (c.RunShooter(7) != 1) return "other error: the aircraft was released";
                Run(c, f, 30);
                if (Count(f, "uid=1 goto failed: no_route") != 2 || !Log(f, "repeats=1")) return "other error: no repeat after the pause: " + Count(f, "uid=1 goto failed: no_route");
            }
            return null;
        }

        private static string WarnText()
        {
            string t = AswWindowModel.WarningText("game_rtb Helo 1");
            if (t.StartsWith("game_rtb", StringComparison.Ordinal)) return "raw code: " + t;
            if (!t.StartsWith(UiText.Get("asw.warn.game_rtb"), StringComparison.Ordinal) || !t.EndsWith("Helo 1", StringComparison.Ordinal)) return "text: " + t;
            return null;
        }
    }
}
