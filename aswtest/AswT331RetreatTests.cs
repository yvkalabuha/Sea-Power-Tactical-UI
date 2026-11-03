using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-331: the air retreat gives one route per move of the cover point (point + orbit in one Goto), no second "Orbit" order on the spot.
    internal static class AswT331RetreatTests
    {
        private sealed class Game : IAirSteerGame
        {
            public List<string> Calls = new List<string>();
            public List<int> Pts = new List<int>();
            public GeoPt Where;
            public GeoPt? Pos(int uid) { return Where; }
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto"); Pts.Add(pts.Count); return null; }
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack) { Calls.Add("orbit"); return null; }
            public int Throttle(int uid) { return 2; }
            public string SetThrottle(int uid, int step) { return null; }
            public IList<CapStep> ThrottleSteps(int uid) { return new List<CapStep>(); }
            public string SetLowPreset(int uid) { return "unsupported"; }
            public string Remember(int uid) { return null; }
            public string RestoreSpeedAlt(int uid) { return null; }
            public bool Evading(int uid) { return false; }
        }

        public static string Run()
        {
            var g = new Game { Where = new GeoPt(0, 0) };
            var s = new AirRetreatSteer(g, null);
            var pt = new GeoPt(0, 10.0 / 60.0);
            s.Steer(1, new AirCover { Mode = "cover", Point = pt }, false, 2000, 2000, 0);
            for (int i = 1; i <= 5; i++)
            {
                g.Where = pt;                                    // the aircraft is on the point and flies its circle
                s.Steer(1, new AirCover { Mode = "cover", Point = pt }, false, 2000, 2000, i * 100);
                pt = AswMath.Offset(pt, 90, 1.2);                // the cover ship moved 1.2 nm
                s.Steer(1, new AirCover { Mode = "cover", Point = pt }, false, 2000, 2000, i * 100 + 50);
            }
            int gotos = g.Calls.Count(c => c == "goto"), orbits = g.Calls.Count(c => c == "orbit");
            if (gotos != 6) return "6 routes expected (the first + 5 moves), got " + gotos + ": " + string.Join(",", g.Calls.ToArray());
            if (orbits != 0) return "the orbit order is a second route: " + orbits;
            if (g.Pts.Any(n => n < 6)) return "a route without the orbit: " + string.Join(",", g.Pts.Select(n => n.ToString()).ToArray());
            return null;
        }
    }
}
