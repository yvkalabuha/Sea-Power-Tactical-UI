using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-180 stream C: which tracks give a danger circle for the buoys (HazardOf), the circle names in the log (asw skip / asw drop), air circles of receding tracks.
    internal static class AswT180Filter
    {
        private sealed class Threats : IAirThreats
        {
            public AirGuardParams P { get; } = new AirGuardParams();
            public int Revision { get { return 0; } }
            public List<AirTrack> Tr = new List<AirTrack>();
            public IList<AirTrack> Tracks() { return Tr; }
            public AirUnitThreat ForUnit(int uid) { return null; }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { return AirStage.None; }
            public AirCover CoverFor(int uid) { return null; }
            public bool Ignored(string window, string group) { return false; }
            public void Ignore(string window, string group, double now) { }
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        public static string Run(string section)
        {
            string e;
            if ((section == null || section == "pure") && (e = Pure()) != null) return "pure: " + e;
            if ((section == null || section == "skip") && (e = Skip()) != null) return "skip: " + e;
            if ((section == null || section == "drop") && (e = Drop()) != null) return "drop: " + e;
            if ((section == null || section == "air") && (e = Air()) != null) return "air: " + e;
            // T-205: the order of the labels does not matter
            var fwd = new List<string> { "t3:Surface:5", "t1:air:50", "t2:Surface:5" };
            var rev = new List<string> { "t2:Surface:5", "t3:Surface:5", "t1:air:50" };
            if (AswRouteGuard.CirclesNote(fwd) != AswRouteGuard.CirclesNote(rev)) return "note order";
            if (fwd[0] != "t3:Surface:5") return "note must not reorder the caller list";
            return null;
        }

        private static string Pure()
        {
            double r;
            if (AswRouteGuard.HazardOf("Unknown", true, 12, out r) || AswRouteGuard.HazardOf("", false, 0, out r) || AswRouteGuard.HazardOf(null, true, 5, out r)) return "Unknown / empty kind must give no circle";
            if (AswRouteGuard.HazardOf("Land", false, 0, out r) || AswRouteGuard.HazardOf("Building", true, 0, out r) || AswRouteGuard.HazardOf("Land", false, 12, out r)) return "Land / Building without a known air defence";
            if (!AswRouteGuard.HazardOf("Land", true, 12, out r) || Math.Abs(r - 12) > 1e-9) return "Land identified, SAM 12 -> r=" + r;
            if (!AswRouteGuard.HazardOf("Surface", false, 0, out r) || r != 0) return "Surface unidentified -> default radius (r=0), got r=" + r;
            if (!AswRouteGuard.HazardOf("Ground", true, 0, out r) || r != 0) return "Ground identified without SAM";
            if (!AswRouteGuard.HazardOf("Surface", true, 8, out r) || Math.Abs(r - 8) > 1e-9) return "Surface identified r=" + r;
            // Classify gives the index of the circle that fired
            var hz = new List<AswHazard> { new AswHazard(new GeoPt(35, 21), 3), new AswHazard(new GeoPt(35, 20), 3) };
            int idx;
            var w = AswRouteGuard.Classify(new GeoPt(35, 20.01), false, -100, 0, hz, 5, null, out idx);
            if (w != AswSkip.Hazard || idx != 1) return "Classify idx " + w + "/" + idx;
            w = AswRouteGuard.Classify(new GeoPt(40, 20), false, -100, 0, hz, 5, null, out idx);
            if (w != AswSkip.None || idx != -1) return "Classify none idx " + idx;
            // the old overload still works
            if (AswRouteGuard.Classify(new GeoPt(35, 20.01), false, -100, 0, hz, 5, null) != AswSkip.Hazard) return "old overload";
            // the note: at most 6, then +N
            var many = Enumerable.Range(1, 8).Select(i => "t" + i + ":Surface:5").ToList();
            string note = AswRouteGuard.CirclesNote(many);
            if (note != "t1:Surface:5,t2:Surface:5,t3:Surface:5,t4:Surface:5,t5:Surface:5,t6:Surface:5,+2") return "note " + note;
            if (AswRouteGuard.CirclesNote(new List<string>()) != "" || AswRouteGuard.CirclesNote(null) != "") return "empty note";
            if (AswRouteGuard.CirclesNote(new List<string> { "t12:Surface:5" }) != "t12:Surface:5") return "single note";
            return null;
        }

        private static AswController Start(AswFakeGame f)
        {
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            return c;
        }

        private static string Skip()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Start(f);
            var inside = new GeoPt(35, 20.3);
            f.HazardList.Add(new AswHazard(inside, 5) { TrackId = 12, Kind = "Surface" });
            int land, sh, hz, air, def;
            var kept = c.GuardPoints("G", new List<GeoPt> { inside }, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 0 || hz != 1) return "hazard not hit " + kept.Count + "/" + hz;
            if (!f.Logs.Exists(l => l.StartsWith("asw skip G") && l.Contains("circles=t12:Surface:5"))) return "no circles in asw skip: " + string.Join(" | ", f.Logs.Where(l => l.StartsWith("asw skip")));
            return null;
        }

        private static string Drop()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            var h = AswFakeGame.Helo(1, "air", passive: 400); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; h.SpeedKn = 250;   // T-334: fast enough to lay the whole first row
            f.Mem.Add(h);
            // T-229: the first barrier row stands 5 nm east of the group (35, 20); a hostile ship sits on it
            f.HazardList.Add(new AswHazard(new GeoPt(35, 20 + 5.0 / 60), 3) { TrackId = 12, Kind = "Surface" });
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"barrier\"]}"));
            for (int i = 0; i < 6; i++) { f.Advance(1); f.Tick(c); }
            var line = f.Logs.Find(l => l.StartsWith("asw drop G") && l.Contains(" pts="));
            if (line == null) return "no drop line: " + string.Join(" | ", f.Logs.Where(l => l.StartsWith("asw")).Select(l => l.Length > 120 ? l.Substring(0, 120) : l));
            if (!line.Contains("circles=t12:Surface:3")) return "no circles= in the drop line: " + line;
            return null;
        }

        private static string Air()
        {
            // helicopter at (35, 20); a hostile plane 30 nm north
            Func<double, AswFakeGame> game = course =>
            {
                var f = new AswFakeGame();
                f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
                f.Mem[0].Lat = 35; f.Mem[0].Lon = 20.0;
                return f;
            };
            foreach (bool approaching in new[] { false, true })
            {
                var f = game(0);
                var c = Start(f);
                var th = new Threats();
                th.Tr.Add(new AirTrack { Id = 40, Cls = "MiG-21bis", Hostile = true, Lat = 35 + 30.0 / 60, Lon = 20, CourseDeg = approaching ? 180 : 0, SpeedKn = 400, RNm = 10, RSrc = "class" });
                c.AirGuard = th;
                int land, sh, hz, air, def;
                var legs = new List<GeoPt> { new GeoPt(35, 20 + 20.0 / 60) };   // 20 nm east of the helicopter: 36 nm from the plane
                var kept = c.GuardPoints("G", legs, "buoy", 0, 1, out land, out sh, out hz, out air, out def);
                if (approaching && (air != 1 || kept.Count != 0)) return "approaching track must defer the point (air=" + air + " kept=" + kept.Count + ")";
                if (!approaching && (air != 0 || kept.Count != 1)) return "a receding track 36 nm away must not defer the point (air=" + air + " kept=" + kept.Count + ")";
                if (approaching && !f.Logs.Exists(l => l.StartsWith("asw skip") && l.Contains("circles=t40:air:50"))) return "air circle name: " + string.Join(" | ", f.Logs.Where(l => l.StartsWith("asw skip")));
            }
            // a receding track but the point is inside its radius + margin (15 nm): still deferred
            {
                var f = game(0);
                var c = Start(f);
                var th = new Threats();
                th.Tr.Add(new AirTrack { Id = 41, Cls = "MiG-21bis", Hostile = true, Lat = 35 + 10.0 / 60, Lon = 20 + 20.0 / 60, CourseDeg = 0, SpeedKn = 400, RNm = 10, RSrc = "class" });
                c.AirGuard = th;
                int land, sh, hz, air, def;
                var kept = c.GuardPoints("G", new List<GeoPt> { new GeoPt(35, 20 + 20.0 / 60) }, "buoy", 0, 1, out land, out sh, out hz, out air, out def);
                if (air != 1) return "point inside R + margin must be deferred (air=" + air + ")";
            }
            return null;
        }
    }
}
