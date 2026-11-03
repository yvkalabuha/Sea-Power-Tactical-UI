using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-150 A6: the route of the buoy packages: replace (not append), no land, no hostile air defence, a steady axis, a package waits for the queue.
    internal static class AswRouteTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        private static int Count(AswFakeGame f, string call) { int n = 0; foreach (var x in f.Calls) if (x == call) n++; return n; }

        private static AswFakeGame OneHelo(bool dip)
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: dip));
            return f;
        }

        public static string Run()
        {
            string e;
            if ((e = Replace()) != null) return "Replace: " + e;
            if ((e = SetTacticsClears()) != null) return "SetTacticsClears: " + e;
            if ((e = NoLand()) != null) return "NoLand: " + e;
            if ((e = NoHazard()) != null) return "NoHazard: " + e;
            if ((e = SlowAxis()) != null) return "SlowAxis: " + e;
            if ((e = JobLong()) != null) return "JobLong: " + e;
            return null;
        }

        // every package replaces the route: a clear before each drop, and the queued points are never more than one package
        private static string Replace()
        {
            var f = OneHelo(false);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            Run(c, f, 4 * 3600);
            if (f.Drops.Count < 2) return "drops " + f.Drops.Count;
            if (Count(f, "clear:1") < f.Drops.Count) return "clears " + Count(f, "clear:1") + " < drops " + f.Drops.Count;
            int max = 0; foreach (var d in f.Drops) max = Math.Max(max, d.Pts.Count);
            if (!f.Route.ContainsKey(1) || f.Route[1].Count > max) return "queued " + (f.Route.ContainsKey(1) ? f.Route[1].Count : 0) + " > one package " + max;
            return null;
        }

        // rows -> dip by asw_set_tactics: the old route is cleared and none of its points stays queued
        private static string SetTacticsClears()
        {
            var f = OneHelo(true);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            Run(c, f, 60);
            if (f.Drops.Count == 0 || !f.Route.ContainsKey(1) || f.Route[1].Count == 0) return "no first route";
            var old = new List<GeoPt>(f.Route[1]);
            int clears = Count(f, "clear:1");
            string r = c.SetTactics(P("{\"group\":\"G\",\"tactics\":[\"dip\"]}"));
            if (AswController.IsErr(r)) return r;
            if (Count(f, "clear:1") <= clears) return "route not cleared";
            Run(c, f, 5);
            if (f.Route.ContainsKey(1)) foreach (var p in f.Route[1]) foreach (var o in old) if (AswMath.DistNm(p, o) < 1e-6) return "an old point is still queued";
            return null;
        }

        // the ship runs along a coast 6 nm to the east: no point over the land, and the window gets the warning
        private static string NoLand()
        {
            var f = OneHelo(false);
            f.Tgt.CourseDeg = 0;
            double coast = 20 + 6.0 / 60;
            f.Height = p => p.Lon > coast ? 80 : -200;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            Run(c, f, 3 * 3600);
            if (f.Drops.Count == 0) return "no drops at all";
            foreach (var d in f.Drops) foreach (var p in d.Pts) if (p.Lon > coast) return "a point over the land: lon " + p.Lon;
            if (!f.HasEvent("asw_warn", "points_skipped")) return "no points_skipped warning";
            return null;
        }

        // a known hostile battery 4 nm beside the line: no point inside 5 nm of it
        private static string NoHazard()
        {
            var f = OneHelo(false);
            f.Tgt.CourseDeg = 0;
            var battery = new GeoPt(35 + 9.0 / 60, 20 + 4.0 / 60);
            f.HazardList.Add(new AswHazard(battery, 0));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            Run(c, f, 3 * 3600);
            if (f.Drops.Count == 0) return "no drops at all";
            foreach (var d in f.Drops) foreach (var p in d.Pts) if (AswMath.DistNm(p, battery) < 5 - 1e-6) return "a point " + AswMath.DistNm(p, battery) + " nm from the battery";
            if (!f.HasEvent("asw_warn", "points_skipped")) return "no points_skipped warning";
            return null;
        }

        // 4 kn with a track that swings +-40 degrees: the axis is the heading, at most one replan, the points stay in the strip of the course line
        private static string SlowAxis()
        {
            var f = OneHelo(false);
            f.Tgt.SpeedKn = 4; f.Tgt.CourseDeg = 90;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            for (int i = 0; i < 1800; i++)
            {
                f.Advance(1);
                double swing = ((i / 180) % 2 == 0) ? 40 : -40;
                var a = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
                var b = AswMath.Offset(a, 90 + swing, 1.0);
                f.Tgt.Track.Clear(); f.Tgt.Track.Add(new[] { a.Lat, a.Lon }); f.Tgt.Track.Add(new[] { b.Lat, b.Lon });
                f.Tick(c);
            }
            int replans = 0; foreach (var l in f.Logs) if (l.StartsWith("asw replan", StringComparison.Ordinal)) replans++;
            if (replans > 1) return "replans " + replans;
            if (f.Drops.Count == 0) return "no drops";
            foreach (var d in f.Drops) foreach (var p in d.Pts) if (Math.Abs(p.Lat - 35) * 60 > 8.6) return "a point off the course strip: " + ((p.Lat - 35) * 60).ToString("F1") + " nm";
            return null;
        }

        // the next dip package is laid only when the game queue of the aircraft is empty (here busy for 900 s after each package)
        private static string JobLong()
        {
            var f = OneHelo(true);
            double busyUntil = 0;
            f.Queue = uid => f.Now < busyUntil ? 3 : 0;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            var at = new List<double>();
            int seen = 0;
            for (int i = 0; i < 3000; i++)
            {
                f.Advance(1);
                f.Tick(c);
                if (f.Dips.Count > seen) { seen = f.Dips.Count; at.Add(f.Now); busyUntil = f.Now + 500; }   // T-176: one dip point per order, the wait for a busy queue is capped at 600 s after the estimate
            }
            if (at.Count < 2) return "dip packages " + at.Count;
            if (at[1] - at[0] < 500) return "the second package came after " + (at[1] - at[0]) + " s, the queue was busy for 500";
            return null;
        }
    }
}
