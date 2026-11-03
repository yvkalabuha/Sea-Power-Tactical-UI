using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>T-191: the "asw skip" line is rate limited (signature + 60 s) and the dip chain waits 30 s at a hold point inside a danger circle.</summary>
    internal static class AswT191Tests
    {
        private static readonly string[] Names = { "skip_gate", "skip_rate", "skip_real", "dip_hold_backoff" };

        public static string Run(string only)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = Case(n);
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Case(string n)
        {
            switch (n)
            {
                case "skip_gate": return SkipGate();
                case "skip_rate": return Scene(true, 0, false);
                case "skip_real": return Scene(true, 0.25, true) ?? Scene(true, 1.0, true);   // x4 and x1
                default: return Scene(false, 0, false);
            }
        }

        private static string SkipGate()
        {
            string s = AswController.SkipSig("G", "t7003:Surface:5");
            if (s != AswController.SkipSig("G", "t7003:Surface:5")) return "signature unstable";
            if (s == AswController.SkipSig("G", "t7004:Surface:5")) return "circles not in the signature";
            if (s == AswController.SkipSig("H", "t7003:Surface:5")) return "group not in the signature";
            string s2 = AswController.SkipSig("G", "t7003:Surface:5;t7004:Surface:5");
            if (!AswController.SkipLogDue(null, 0, s, 0, 60)) return "first line not due";
            if (AswController.SkipLogDue(s, 0, s, 30, 60)) return "same picture at 30 s";
            if (!AswController.SkipLogDue(s, 0, s, 60, 60)) return "same picture at 60 s";
            if (!AswController.SkipLogDue(s, 0, s2, 1, 60)) return "changed picture";
            return null;
        }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static int Skips(AswFakeGame f) { return f.Logs.Count(l => l.StartsWith("asw skip G")); }

        // two dipping helicopters on a course; the chain reaches the standoff (hold), then a danger circle covers the hold point
        // realStep > 0: T-215 mode, the real clock advances realStep s per game second and 120 s are watched (2 lines expected)
        private static string Scene(bool rate, double realStep, bool real)
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1 };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", dip: true, passive: 0); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); f.FixPos.Add(u); }
            var c = Make(f);
            double clock = 1000;
            if (real) c.RealClock = () => clock;
            c.Group(Json.Parse("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}") as Dictionary<string, object>);
            if (AswController.IsErr(c.SearchCourse((Dictionary<string, object>)Json.Parse("{\"tactics\":[\"dip\"],\"standoff\":{\"dip\":20}}")))) return "order refused";
            int seen = 0; GeoPt? hold = null;
            for (int i = 0; i < 4000 && hold == null; i++)
            {
                f.Advance(1); f.Tick(c);
                while (seen < f.Dips.Count)
                {
                    var p = f.Dips[seen][0];
                    var m = f.Mem.First(x => x.Uid == f.DipUids[seen]); m.Lat = p.Lat; m.Lon = p.Lon;
                    seen++;
                }
                if (f.Logs.Any(l => l.Contains(" hold")) && f.Dips.Count > 0) hold = f.Dips[f.Dips.Count - 1][0];
            }
            if (hold == null) return "no hold point reached";
            f.HazardList.Add(new AswHazard(hold.Value, 5) { TrackId = 7003, Kind = "Surface" });
            // let the pending jobs of the helicopters run out, then watch
            for (int i = 0; i < 200; i++) { f.Advance(1); f.Tick(c); }
            f.Logs.Clear();
            int calls0 = c.GuardCalls;
            if (real)
            {
                for (int i = 0; i < 480; i++) { f.Advance(1); clock += realStep; f.Tick(c); }
                int lines = Skips(f), expect = (int)Math.Floor(480 * realStep / 60.0) + 1;
                if (lines > expect) return "real step " + realStep + ": " + lines + " lines, expected at most " + expect;
                if (lines < 1) return "real step " + realStep + ": no line";
                return null;
            }
            for (int i = 0; i < 72; i++)
            {
                f.Advance(1); f.Tick(c);
                if (!rate && i == 28 && c.GuardCalls - calls0 > 4) return "guard ran " + (c.GuardCalls - calls0) + " times in 29 s";
            }
            if (rate)
            {
                if (Skips(f) > 2) return "asw skip lines: " + Skips(f);
                int before = Skips(f);
                f.HazardList.Clear();
                f.HazardList.Add(new AswHazard(hold.Value, 5) { TrackId = 7004, Kind = "Surface" });
                for (int i = 0; i < 60; i++) { f.Advance(1); f.Tick(c); }
                if (!f.Logs.Any(l => l.StartsWith("asw skip G") && l.Contains("t7004"))) return "no line after the circle changed (" + before + ")";
                return null;
            }
            int calls1 = c.GuardCalls;
            if (calls1 == calls0) return "the chain never retried";
            return null;
        }
    }
}
