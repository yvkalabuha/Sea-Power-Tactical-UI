using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-349 stand sections "t349 <name>": the jammer holds the retreat while the station point is under an air threat.
    internal static class StrikeT349Tests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "station-hold", "station-neg", "station-flap", "station-why" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "station-hold": return StationHoldUnit() ?? StationHold();
                    case "station-neg": return StationNeg();
                    case "station-flap": return StationFlap();
                    case "station-why": return StationWhy();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private static StrikeFakeGame World()
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= 2; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            g.AddEw(5, 38.9, 10.2);
            return g;
        }

        private static string St(StrikeController c) { var s = c.Groups[0].EwExec.FirstOrDefault(x => x.Uid == 5); return s == null ? "none" : s.State; }
        private static string Why(StrikeController c) { var s = c.Groups[0].EwExec.FirstOrDefault(x => x.Uid == 5); return s == null ? null : s.Why; }

        private static string StationHoldUnit()
        {
            var p = new StrikeParams();
            var pl = new StrikeEwPlan();
            Func<string, bool, bool, bool, bool, int, string> d = (st, thr, sam, term, air, fl) =>
                StrikeEwMath.Decide(new StrikeEwState { State = st }, pl, 1300, 50, 50, 100, term, fl, -1, sam, air, p, 1e9, false, true, thr);
            if (d("retreat", true, false, false, false, 2) != "retreat") return "unit: retreat is held by the station threat";
            if (d("retreat", false, false, false, false, 2) != "to_station") return "unit: negative control (T-342 behaviour)";
            if (d("retreat", true, false, true, false, 0) != "back") return "unit: the strike is over -> back";
            if (d("retreat", true, true, false, false, 2) != "guard") return "unit: sam -> guard";
            if (d("guard", true, false, false, false, 2) != "to_station") return "unit: guard is not held";
            return null;
        }

        /// <summary>Starts the scenario: the jammer on the station, then returns the controller and the t0.</summary>
        private static string Start(StrikeFakeGame game, StrikeController c, List<string> log)
        {
            c.Group(C("op", "create", "group", "A", "uids", game.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            c.SetGoal(C("group", "A", "goal", "breach"));
            if (StrikeController.IsErr(c.Plan(C("group", "A")))) return "plan";
            if (StrikeController.IsErr(c.Launch(C("group", "A")))) return "launch";
            for (int i = 0; i < 3000 && St(c) != "to_station" && St(c) != "on_station"; i++) { game.Advance(1); c.Tick(game.Time); }
            string before = St(c);
            return before != "to_station" && before != "on_station" ? "flying " + before : null;
        }

        private static string StationHold()
        {
            var game = World(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            double retreatFrom = double.MaxValue, stationTo = 0;
            c.AirRetreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 40;
            c.AirThreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 80;
            c.AirThreatAtStation = (pt, gn) => game.Time >= retreatFrom && game.Time < stationTo;
            c.AirSteerEw = (u, r, m) => "no_cover";
            c.AirReleaseEw = u => { };
            string e = Start(game, c, log); if (e != null) return e;
            double t0 = game.Time + 1; retreatFrom = t0; stationTo = t0 + 400;
            bool sawClear = false, sawStation = false, wrongOrder = false; double backAt = -1;
            for (int s = 0; s < 600 && backAt < 0; s++)
            {
                game.Advance(1); c.Tick(game.Time);
                double rel = game.Time - t0; string st = St(c), why = Why(c);
                if (rel >= 5 && rel < 398 && st != "retreat") return "retreat must be held at t=" + rel + " " + st;
                if (st == "retreat" && why == "clear_wait") { sawClear = true; if (sawStation) wrongOrder = true; if (rel > 145) return "clear_wait after the timer, t=" + rel; }
                if (st == "retreat" && why == "station_threat") { sawStation = true; if (rel < 138) return "station_threat before the timer expired, t=" + rel; }
                if (st == "to_station" || st == "on_station" || st == "jamming") backAt = rel;
            }
            if (backAt < 0) return "never returned";
            if (backAt < 399 || backAt > 402) return "returned at " + backAt + ", expected right after 400";
            if (!sawClear || !sawStation || wrongOrder) return "why sequence " + sawClear + " " + sawStation + " " + wrongOrder;
            var hold = log.Where(l => l.Contains("strike ew station_hold uid=5")).ToList();
            if (hold.Count == 0) return "no station_hold log";
            if (hold.Count > 30) return "station_hold log flood " + hold.Count;
            if (hold.Count > (400 - 130) / 10 + 2) return "station_hold log too often " + hold.Count;
            return null;
        }

        private static string StationNeg()
        {
            var game = World(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            double retreatFrom = double.MaxValue;
            c.AirRetreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 40;
            c.AirThreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 80;
            c.AirThreatAtStation = null;
            c.AirSteerEw = (u, r, m) => "no_cover";
            c.AirReleaseEw = u => { };
            string e = Start(game, c, log); if (e != null) return e;
            double t0 = game.Time + 1; retreatFrom = t0; double backAt = -1;
            for (int s = 0; s < 400 && backAt < 0; s++)
            {
                game.Advance(1); c.Tick(game.Time);
                string st = St(c);
                if (st == "to_station" || st == "on_station" || st == "jamming") backAt = game.Time - t0;
            }
            if (backAt < 139 || backAt > 140) return "without the callback the return is at " + backAt + ", expected 139 (T-342)";
            if (log.Any(l => l.Contains("station_hold"))) return "station_hold without the callback";
            return null;
        }

        private static int Flap(bool wired)
        {
            var game = World(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            // a fixed track 35 nm from the station; the jammer is 30 nm from it on the station and 70 nm when it retreats
            bool active = false;
            Func<double> jamD = () => St(c) == "retreat" ? 70 : 30;
            c.AirRetreatNow = (u, gn) => active && u == 5 && jamD() < 40;
            c.AirThreatNow = (u, gn) => active && u == 5 && jamD() < 60;
            c.AirThreatAtStation = wired ? (Func<GeoPt, string, bool>)((pt, gn) => active && 35 < 60) : null;
            c.AirSteerEw = (u, r, m) => "no_cover";
            c.AirReleaseEw = u => { };
            string e = Start(game, c, log); if (e != null) return -1;
            active = true; int flips = 0; string prev = St(c);
            for (int s = 0; s < 1500; s++)
            {
                game.Advance(1); c.Tick(game.Time);
                string st = St(c);
                if (prev == "retreat" && st == "to_station") flips++;
                prev = st;
            }
            return flips;
        }

        private static string StationFlap()
        {
            int w = Flap(true), n = Flap(false);
            if (w < 0 || n < 0) return "setup";
            if (w != 0) return "wired: retreat->to_station transitions " + w + ", expected 0";
            if (n < 2) return "control (callback null): transitions " + n + ", expected >= 2";
            return null;
        }

        private static string StationWhy()
        {
            var game = World(); var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            double retreatFrom = double.MaxValue;
            c.AirRetreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 10;
            c.AirThreatNow = (u, gn) => u == 5 && game.Time >= retreatFrom && game.Time < retreatFrom + 10;
            c.AirThreatAtStation = (pt, gn) => game.Time >= retreatFrom;
            c.AirSteerEw = (u, r, m) => "no_cover";
            c.AirReleaseEw = u => { };
            string e = Start(game, c, log); if (e != null) return e;
            retreatFrom = game.Time + 1;
            for (int s = 0; s < 120; s++) { game.Advance(1); c.Tick(game.Time); }
            if (St(c) != "retreat" || Why(c) != "station_threat") return "state " + St(c) + " why " + Why(c);
            var g0 = (Dictionary<string, object>)Arr(Obj(c.ViewJson(20))["groups"])[0];
            var e0 = (Dictionary<string, object>)Arr(g0["ew"])[0];
            if (e0["why"] as string != "station_threat") return "view why " + e0["why"];
            return null;
        }
    }
}
