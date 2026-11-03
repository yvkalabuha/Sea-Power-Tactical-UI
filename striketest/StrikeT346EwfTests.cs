using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-346 stand sections "t346 ewf-*": D3 (the hysteresis of the fighter threat in the controller over the fake game). STRIKE_TRACE=1 prints the journal.
    internal static class StrikeT346EwfTests
    {
        public static readonly string[] Names = { "ewf-hyst", "ewf-lap" };
        private static readonly bool Trace = Environment.GetEnvironmentVariable("STRIKE_TRACE") != null;

        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "breach";
            try
            {
                switch (n)
                {
                    case "ewf-hyst": return Hyst();
                    case "ewf-lap": return Lap();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private static string Lap()
        {
            var game = new StrikeFakeGame();
            for (int i = 1; i <= 2; i++) game.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            game.AddKidd(100, 41.0, 10.0);
            game.AddEw(5, 38.9, 10.2); game.AddEw(6, 38.9, 10.3);
            var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), l => { log.Add(l); if (Trace) Console.WriteLine("  t=" + game.Time.ToString("0", CultureInfo.InvariantCulture) + " " + l); });
            c.Group(C("op", "create", "group", "A", "uids", game.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            c.SetGoal(C("group", "A", "goal", "breach"));
            c.AirRetreatNow = (u, gn) => false; c.AirThreatNow = (u, gn) => false;
            c.AirSteerEw = (u, r, m) => null; c.AirReleaseEw = u => { };
            if (StrikeController.IsErr(c.Plan(C("group", "A"))) || StrikeController.IsErr(c.Launch(C("group", "A")))) return "plan / launch";
            var movers = new List<StrikeFighter>();
            Action<int> step = secs =>
            {
                for (int s = 0; s < secs; s++)
                {
                    game.Advance(1);
                    foreach (var f in movers) { var p = AswMath.Offset(new GeoPt(f.Lat, f.Lon), f.CourseDeg, f.SpeedKn / 3600.0); f.Lat = p.Lat; f.Lon = p.Lon; }
                    c.Tick(game.Time);
                }
            };
            var strikers = game.Air.Where(a => a.Ew == null).ToList();
            var ctr = new GeoPt(strikers.Average(a => a.Lat), strikers.Average(a => a.Lon));
            for (int i = 0; i < 400; i++) { step(1); var ew = c.Groups[0].EwExec.FirstOrDefault(x => x.Uid == 6); if (ew != null && (ew.State == "to_station" || ew.State == "on_station")) break; }
            var jam = new GeoPt(game.Air.First(a => a.Uid == 5).Lat, game.Air.First(a => a.Uid == 5).Lon);
            ctr = new GeoPt(game.Air.Where(a => a.Ew == null).Average(a => a.Lat), game.Air.Where(a => a.Ew == null).Average(a => a.Lon));
            double brg = AswMath.BearingDeg(jam, ctr);
            var at = AswMath.Offset(ctr, brg, 38 + 6.3);
            double toward = AswMath.BearingDeg(at, ctr);
            var fi = new StrikeFighter { TrackId = 4021, ClassIni = "usn_f-14a", ClassName = "MiG-23", Lat = at.Lat, Lon = at.Lon, CourseDeg = toward, SpeedKn = 40, RNm = 38, RSrc = "class", Identified = true, Hostile = true, IsFighter = true, FighterSrc = "role" };
            game.FighterList.Add(fi); movers.Add(fi);
            for (int k = 0; k < 3; k++)
            {
                fi.CourseDeg = toward; step(20);
                fi.CourseDeg = (toward + 180) % 360; step(40);
            }
            // reviewer (T-346): live QA 10 pattern - the CAP fighter turns away for 40 s and comes back: the jam must not cycle
            int ons = log.Count(l => l.Contains("fighter on uid=6 tgt=4021")), clears = log.Count(l => l.Contains("fighter off") && l.Contains("why=clear"));
            if (ons != 1 || clears != 0) return "lap: jam on=" + ons + " off_clear=" + clears + " (want 1/0): " + string.Join(" | ", log.Where(l => l.Contains("ew fighter")));
            return null;
        }

        private static string Hyst()
        {
            var game = new StrikeFakeGame();
            for (int i = 1; i <= 2; i++) game.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            game.AddKidd(100, 41.0, 10.0);
            game.AddEw(5, 38.9, 10.2); game.AddEw(6, 38.9, 10.3);
            var log = new List<string>();
            var c = new StrikeController(game, new StrikeParams(), l => { log.Add(l); if (Trace) Console.WriteLine("  t=" + game.Time.ToString("0", CultureInfo.InvariantCulture) + " " + l); });
            c.Group(C("op", "create", "group", "A", "uids", game.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            c.SetGoal(C("group", "A", "goal", "breach"));
            c.AirRetreatNow = (u, gn) => false; c.AirThreatNow = (u, gn) => false;
            c.AirSteerEw = (u, r, m) => null; c.AirReleaseEw = u => { };
            if (StrikeController.IsErr(c.Plan(C("group", "A"))) || StrikeController.IsErr(c.Launch(C("group", "A")))) return "plan / launch";
            var movers = new List<StrikeFighter>();
            Action<int> step = secs =>
            {
                for (int s = 0; s < secs; s++)
                {
                    game.Advance(1);
                    foreach (var f in movers) { var p = AswMath.Offset(new GeoPt(f.Lat, f.Lon), f.CourseDeg, f.SpeedKn / 3600.0); f.Lat = p.Lat; f.Lon = p.Lon; }
                    c.Tick(game.Time);
                }
            };
            var strikers = game.Air.Where(a => a.Ew == null).ToList();
            var ctr = new GeoPt(strikers.Average(a => a.Lat), strikers.Average(a => a.Lon));
            for (int i = 0; i < 400; i++) { step(1); var ew = c.Groups[0].EwExec.FirstOrDefault(x => x.Uid == 6); if (ew != null && (ew.State == "to_station" || ew.State == "on_station")) break; }
            // a CAP fighter on a loop: 10 s towards the group, 10 s away, for two minutes
            var jam = new GeoPt(game.Air.First(a => a.Uid == 5).Lat, game.Air.First(a => a.Uid == 5).Lon);
            ctr = new GeoPt(game.Air.Where(a => a.Ew == null).Average(a => a.Lat), game.Air.Where(a => a.Ew == null).Average(a => a.Lon));
            double brg = AswMath.BearingDeg(jam, ctr);
            var at = AswMath.Offset(ctr, brg, 70);
            double toward = AswMath.BearingDeg(at, ctr);
            var fi = new StrikeFighter { TrackId = 4021, ClassIni = "usn_f-14a", ClassName = "MiG-23", Lat = at.Lat, Lon = at.Lon, CourseDeg = toward, SpeedKn = 120, RNm = 38, RSrc = "class", Identified = true, Hostile = true, IsFighter = true, FighterSrc = "role" };
            game.FighterList.Add(fi); movers.Add(fi);
            for (int k = 0; k < 12; k++)
            {
                fi.CourseDeg = k % 2 == 0 ? toward : (toward + 180) % 360;
                step(10);
            }
            if (log.Count(l => l.Contains("strike ew fighter threat grp=A tgt=4021")) != 1) return "one threat line: " + string.Join(" | ", log.Where(l => l.Contains("fighter")));
            if (log.Any(l => l.Contains("strike ew fighter gone"))) return "no gone line while it circles: " + string.Join(" | ", log.Where(l => l.Contains("fighter gone")));
            if (log.Any(l => l.Contains("strike ew fighter off") && l.Contains("why=clear"))) return "no clear off while it circles";
            if (log.Count(l => l.Contains("strike ew fighter on uid=6 tgt=4021")) != 1) return "one jam on";
            // the log and the view agree: eta_s of the threat line is eta_in_s of the view
            string raw = c.ViewJson(20);
            var grp = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)Json.Parse(raw))["groups"])[0];
            var th = (Dictionary<string, object>)((List<object>)grp["fighter_threats"])[0];
            string line = log.First(l => l.Contains("strike ew fighter threat grp=A tgt=4021"));
            int ix = line.IndexOf("eta_s=", StringComparison.Ordinal);
            double logEta = double.Parse(line.Substring(ix + 6).Split(' ')[0], CultureInfo.InvariantCulture);
            double viewIn = (double)th["eta_in_s"];
            if (Math.Abs(logEta - viewIn) > 0.06) return "eta_in_s " + viewIn + " vs the log " + logEta;
            if (!th.ContainsKey("since_t") || !th.ContainsKey("eta_s")) return "view fields";
            // the fighter goes away for good: the gone line (after the hold time), then the clear
            fi.CourseDeg = (toward + 180) % 360; fi.SpeedKn = 400;
            step(12);
            if (log.Any(l => l.Contains("fighter gone"))) return "gone too early";
            step(35);
            if (!log.Any(l => l.Contains("strike ew fighter gone grp=A tgt=4021") && l.Contains("held_s=") && (l.Contains("why=far") || l.Contains("why=receding")))) return "gone line " + string.Join(" | ", log.Where(l => l.Contains("fighter")));
            step(40);
            if (!log.Any(l => l.Contains("strike ew fighter off uid=6") && l.Contains("why=clear"))) return "off clear after the departure";
            return null;
        }
    }
}
