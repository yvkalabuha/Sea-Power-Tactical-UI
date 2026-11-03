using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-322 / ADR-0126: a stale barrier line fades and then leaves the view (the plan, the window and the save stay). Scenarios run through AswT176Tests.RunT322.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names322 =
        {
            "math", "tags", "passed", "reset", "order", "fade", "live", "buoys-expired", "buoys-gone", "no-buoys-grace", "other-group-buoy", "track", "state", "all-deferred", "log-always",
        };

        public static string RunT322(string only = null)
        {
            string e;
            foreach (string n in Names322)
            {
                if (only != null && only != n) continue;
                if ((e = Scenario322(n)) != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names322, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static string Scenario322(string name)
        {
            switch (name)
            {
                case "math": return AswBarrierStale.SelfTest();
                case "tags": return Tags322();
                case "passed": return Passed322();
                case "reset": return Reset322();
                case "order": return Order322();
                case "fade": return Fade322();
                case "live": return Live322();
                case "buoys-expired": return Expired322();
                case "buoys-gone": return Gone322();
                case "no-buoys-grace": return Grace322();
                case "other-group-buoy": return OtherGroup322();
                case "track": return Track322();
                case "state": return State322();
                case "all-deferred": return AllDeferred322();
                case "log-always": return LogAlways322();
                default: return null;
            }
        }

        private static List<AswLineView> BL(AswController c) { return c.View().Groups[0].Lines.Where(l => l.Kind == "barrier_line").ToList(); }
        private static bool RowLine(AswLineView l, int seq) { return l.Seq == seq && l.BandId >= 0; }

        private static string Tags322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 30);
            var ls = BL(c);
            if (ls.Any(l => l.BandId < 1)) return "an untagged line: " + string.Join(",", ls.Select(l => l.BandId + "/" + l.Seq).ToArray());
            foreach (int seq in new[] { 0, 1 })
            {
                var row = ls.Where(l => l.Seq == seq).ToList();
                if (row.Count < 2) return "row " + seq + " has " + row.Count + " lines (plan row + drop line expected)";
                if (row.Select(l => l.BandId).Distinct().Count() != 1) return "row " + seq + " lines of different bands";
            }
            if (ls.Any(l => l.Seq > 1)) return "a third row";
            int old = ls.Max(l => l.BandId);
            f.Tgt.CourseDeg = 150;
            for (int i = 0; i < 600 && !Logged(f, "barrier reset reason=turn"); i++) Run(c, f, 1);
            if (!Logged(f, "barrier reset reason=turn")) return "no reset";
            Run(c, f, 20);
            int now = BL(c).Where(l => l.Stale == null).Max(l => l.BandId);
            if (now <= old) return "the new band id " + now + " is not above " + old;
            return null;
        }

        private static string Passed322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            for (int i = 0; i < 1500 && !Logged(f, "barrier row seq=0 passed"); i++) Run(c, f, 1);
            if (!Logged(f, "barrier row seq=0 passed")) return "row 0 never passed";
            Run(c, f, 5);
            var ls = BL(c);
            var old = ls.Where(l => RowLine(l, 0)).ToList();
            if (old.Count < 2) return "row 0 lines " + old.Count;
            if (old.Any(l => l.Stale != "passed" || l.Alpha >= 1f || l.Alpha <= 0f)) return "row 0 is not fading as passed: " + c.StaleInfo("G");
            var ahead = ls.Where(l => l.Seq >= 1).ToList();
            if (ahead.Count == 0 || ahead.Any(l => l.Stale != null || l.Alpha != 1f)) return "rows ahead are not current: " + c.StaleInfo("G");
            int rowsAhead = c.View().Groups[0].Plan.Lines;
            Run(c, f, 90);
            if (BL(c).Any(l => RowLine(l, 0))) return "row 0 is still in the view after the fade";
            if (!c.StaleInfo("G").Contains(":0:passed:0.00")) return "StaleInfo lost the hidden row: " + c.StaleInfo("G");
            if (c.BarrierLines("G") != 2 || c.View().Groups[0].Plan.Lines != rowsAhead) return "the plan changed: " + c.BarrierLines("G");
            return null;
        }

        private static string Reset322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            int old = BL(c).Max(l => l.BandId);
            int buoys = c.GroupBuoys("G");
            f.Tgt.CourseDeg = 150;
            for (int i = 0; i < 600 && !Logged(f, "barrier reset reason=turn"); i++) Run(c, f, 1);
            if (!Logged(f, "barrier reset reason=turn")) return "no reset";
            Run(c, f, 5);
            var olds = BL(c).Where(l => l.BandId == old).ToList();
            if (olds.Count == 0) return "no old line right after the reset";
            if (olds.Any(l => l.Stale != "reset")) return "old lines are not reset: " + c.StaleInfo("G");
            if (BL(c).Where(l => l.BandId > old).Any(l => l.Stale != null)) return "a line of the new band is stale: " + c.StaleInfo("G");
            Run(c, f, 90);
            if (BL(c).Any(l => l.BandId == old)) return "old lines are still in the view";
            if (c.GroupBuoys("G") < buoys) return "buoys of the old band left the list " + buoys + " -> " + c.GroupBuoys("G");
            return null;
        }

        private static string Order322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 30);
            if (BL(c).Count == 0) return "no lines to start";
            c.SearchCourse(P("{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20}"));
            Run(c, f, 2);
            if (BL(c).Any(l => l.Stale == null)) return "a current barrier line without the tactic: " + c.StaleInfo("G");
            var f2 = CorridorGame(400);
            var c2 = StartCourse(f2, Barrier229);
            Run(c2, f2, 30);
            c2.Stop(P("{\"group\":\"G\"}"));
            Run(c2, f2, 2);
            if (c2.View().Groups[0].Lines.Any(l => l.Kind == "barrier_line" && l.Stale == null)) return "a current barrier line without the order";
            return null;
        }

        private static string Fade322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            for (int i = 0; i < 1500 && !Logged(f, "barrier row seq=0 passed"); i++) Run(c, f, 1);
            Run(c, f, 45);
            var row = BL(c).Where(l => RowLine(l, 0)).ToList();
            if (row.Count == 0) return "row 0 gone at 45 s";
            foreach (var l in row) if (Math.Abs(l.Alpha - 0.5) > 0.05) return "alpha at 45 s " + l.Alpha;
            Run(c, f, 60);
            var keys = f.Logs.Where(l => l.Contains("barrier stale key=")).Select(l => l.Substring(l.IndexOf("key=", StringComparison.Ordinal))).ToList();
            if (keys.Count == 0 || keys.Count != keys.Distinct().Count()) return "stale lines in the log: " + keys.Count + " / " + keys.Distinct().Count();
            if (Logged(f, "barrier fresh key=")) return "a fresh line without a reason";
            return null;
        }

        private static string Live322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            var ls = BL(c);
            if (ls.Count < 4) return "lines " + ls.Count;
            if (ls.Any(l => l.Stale != null || l.Alpha != 1f)) return "a fresh barrier is stale: " + c.StaleInfo("G");
            if (Logged(f, "barrier stale")) return "stale logged";
            return null;
        }

        private static List<BuoyType> Types322(double lifeS)
        {
            return new List<BuoyType> { new BuoyType { Ammo = "usn_ssq-53", Sensor = "SSQ-53_Sonar", PassiveKm = 10, LifeS = lifeS } };
        }

        private static string Expired322()
        {
            var f = CorridorGame(400);
            f.TypesOf[1] = Types322(1800);   // 0.8 * 1800 covers row 0 only (1200 s away)
            var c = StartCourse(f, Barrier229);
            Run(c, f, 20);
            f.Tgt.SpeedKn = 3;   // the ship does not reach the row while the buoys die
            Run(c, f, 1650);
            var ls = BL(c).Where(l => l.State == "done").ToList();
            if (ls.Count < 2) return "no laid lines " + ls.Count;
            if (ls.Any(l => l.Stale != null)) return "stale before the life ends: " + c.StaleInfo("G");
            Run(c, f, 250);
            ls = BL(c).Where(l => RowLine(l, 0)).ToList();
            if (ls.Count == 0) { if (!c.StaleInfo("G").Contains("buoys")) return "row 0 hidden for another reason: " + c.StaleInfo("G"); }
            else if (ls.Any(l => l.Stale != "buoys")) return "expired row 0 is not stale: " + c.StaleInfo("G");
            Run(c, f, 100);
            if (BL(c).Any(l => RowLine(l, 0))) return "expired row 0 is still drawn";
            return null;
        }

        private static string Gone322()
        {
            var f = CorridorGame(400);
            f.SimBuoys = true;
            var c = StartCourse(f, Barrier229);
            Run(c, f, 200);
            if (f.Buoys.Count < 19) return "buoys in the game " + f.Buoys.Count;
            if (BL(c).Any(l => l.Stale != null)) return "stale with all buoys in the water: " + c.StaleInfo("G");
            f.Buoys.RemoveAll(b => Math.Abs(AswMath.ToLocal(new GeoPt(35, 20), 90, new GeoPt(b.Lat, b.Lon)).X - 5) < 0.7);
            if (f.Buoys.Count == 0) return "the other row went too";
            Run(c, f, 50);
            var r0 = BL(c).Where(l => RowLine(l, 0)).ToList();
            var r1 = BL(c).Where(l => RowLine(l, 1)).ToList();
            if (r1.Count == 0 || r1.Any(l => l.Stale != null)) return "row 1 lost its fresh state: " + c.StaleInfo("G");
            if (r0.Count == 0 ? !c.StaleInfo("G").Contains("buoys") : r0.Any(l => l.Stale != "buoys")) return "row 0 is not stale after its buoys went: " + c.StaleInfo("G");
            // control: an empty read gives no information
            var g = CorridorGame(400);
            var c2 = StartCourse(g, Barrier229);
            Run(c2, g, 450);
            if (BL(c2).Any(l => l.Stale != null)) return "an empty read made lines stale: " + c2.StaleInfo("G");
            return null;
        }

        private static string Grace322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 30);
            if (c.ForgetBuoys("G") < 19) return "records forgotten";
            Run(c, f, 250);
            if (BL(c).Any(l => l.Stale != null)) return "stale inside the grace: " + c.StaleInfo("G");
            Run(c, f, 100);
            var ls = BL(c).Where(l => l.Seq == 1).ToList();
            if (ls.Count == 0 ? !c.StaleInfo("G").Contains(":buoys:") : ls.Any(l => l.Stale != "buoys")) return "not stale after the grace: " + c.StaleInfo("G");
            return null;
        }

        private static string OtherGroup322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 30);
            var p = f.Drops[0].Pts[0];
            c.ForgetBuoys("G");
            f.Buoys.Add(new AswBuoyObs { Id = 99, OwnerUid = 77, Lat = p.Lat, Lon = p.Lon, DroppedS = f.Now, Ammo = "usn_ssq-53" });
            Run(c, f, 400);
            var r0 = BL(c).Where(l => RowLine(l, 0)).ToList();
            if (r0.Count == 0 || r0.Any(l => l.Stale != null)) return "row 0 covered by a foreign buoy went stale: " + c.StaleInfo("G");
            var r1 = BL(c).Where(l => RowLine(l, 1)).ToList();
            if (r1.Count != 0 && r1.All(l => l.Stale == null)) return "row 1 (no buoys at all) stayed current: " + c.StaleInfo("G");
            return null;
        }

        private static AswContact SubAt(AswFakeGame f, int id, double xNm, double course, double kn)
        {
            var s = SubAhead(f, id, 14, course, kn);
            var q = AswMath.ToGeo(new GeoPt(35, 20), 90, new NmPt(xNm, 0));
            s.Lat = q.Lat; s.Lon = q.Lon;
            return s;
        }

        /// <summary>One contact held by the group, moved behind the rows with the given course; returns the stale state of the two rows (laid lines only).</summary>
        private static string TrackRun(double course, bool unknownSecond, bool ring, out bool bothStale)
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 15);
            var sub = SubAt(f, 7, 14, 90, 8);
            AswContact sub2 = unknownSecond ? SubAt(f, 8, 14, double.NaN, double.NaN) : null;
            Run(c, f, 15);
            if (ring)
            {
                f.Subs.Remove(sub); sub.Live = false;
                Run(c, f, 320);
                if (!c.RingActive(7)) { bothStale = false; return "no ring"; }
                sub2 = SubAt(f, 9, 1, 270, 8);
            }
            else
            {
                var q = AswMath.ToGeo(new GeoPt(35, 20), 90, new NmPt(1, 0));
                sub.Lat = q.Lat; sub.Lon = q.Lon; sub.CourseDeg = course;
                if (sub2 != null) { sub2.Lat = q.Lat; sub2.Lon = q.Lon; }
            }
            Run(c, f, 6);
            var laid = BL(c).Where(l => l.State == "done" && l.BandId >= 1 && l.Seq >= 0).ToList();
            var s0 = laid.Where(l => l.Seq == 0).ToList(); var s1 = laid.Where(l => l.Seq == 1).ToList();
            bothStale = s0.Count > 0 && s1.Count > 0 && s0.All(l => l.Stale == "track_passed") && s1.All(l => l.Stale == "track_passed");
            bool any = laid.Any(l => l.Stale != null);
            return any && !bothStale ? "partly: " + c.StaleInfo("G") : null;
        }

        private static string Track322()
        {
            bool both;
            string e = TrackRun(270, false, false, out both);
            if (e != null || !both) return "against the axis: " + (e ?? "rows are not track_passed");
            e = TrackRun(90, false, false, out both);
            if (both || e != null) return "along the axis made the rows stale " + e;
            e = TrackRun(270, true, false, out both);
            if (both || e != null) return "an unknown second contact made the rows stale " + e;
            e = TrackRun(270, false, true, out both);
            if (both || e != null) return "a lost-contact ring made the rows stale " + e;
            return null;
        }

        private static string State322()
        {
            var f = CorridorGame(400);
            var c = StartCourse(f, Barrier229);
            Run(c, f, 60);
            if (!c.StateJson().Contains("\"barrier_stale\":[]")) return "an empty list expected: " + c.StateJson();
            for (int i = 0; i < 1500 && !Logged(f, "barrier row seq=0 passed"); i++) Run(c, f, 1);
            Run(c, f, 5);
            var gv = c.View().Groups[0];
            int lines = gv.Plan.Lines, laid = gv.Plan.LinesLaid, buoys = c.GroupBuoys("G");
            string js = c.StateJson();
            int i0 = js.IndexOf("\"barrier_stale\":[{", StringComparison.Ordinal);
            if (i0 < 0) return "no barrier_stale entry";
            string tail = js.Substring(i0);
            if (!tail.Contains("\"key\":\"b1:0") || !tail.Contains("\"why\":\"passed\"") || !tail.Contains("\"alpha\":")) return "entry: " + tail.Substring(0, Math.Min(200, tail.Length));
            Run(c, f, 90);
            gv = c.View().Groups[0];
            if (gv.Plan.Lines != lines || c.GroupBuoys("G") < buoys) return "the fade changed the plan / buoys";
            if (laid > gv.Plan.LinesLaid + 1) return "laid rows changed " + laid + " -> " + gv.Plan.LinesLaid;
            return null;
        }
        // T-335 (N3): every point deferred by a threat = nothing dropped yet: the barrier must stay visible (no stale "buoys"), also after the grace
        private static string AllDeferred322()
        {
            var f = CorridorGame(400);
            f.HazardList.Add(new AswHazard(new GeoPt(35, 20), 60));   // the whole band is inside the zone
            var c = StartCourse(f, Barrier229);
            if (c == null) return "order refused";
            f.Tgt.SpeedKn = 3;   // the ship does not reach the rows
            Run(c, f, 700);
            if (!Logged(f, "deferred=")) return "no point was deferred (the scenario does not hold)";
            if (f.Buoys.Count != 0 || f.Drops.Count != 0) return "something was dropped: " + f.Drops.Count;
            var ls = BL(c);
            if (ls.Count == 0) return "no barrier lines at all: " + c.StaleInfo("G");
            if (ls.Any(l => l.Stale != null)) return "a barrier with no buoy dropped went stale: " + c.StaleInfo("G");
            if (Logged(f, "barrier stale")) return "stale logged";
            return null;
        }

        // T-335: the stale / fresh lines of the view reach LogOutput (no Debug needed)
        private static string LogAlways322()
        {
            if (!AswMath.LogAlways("asw view G barrier stale key=b1:0 why=passed")) return "stale line is not always logged";
            if (!AswMath.LogAlways("asw view G barrier fresh key=b1:0")) return "fresh line is not always logged";
            return null;
        }
    }
}
