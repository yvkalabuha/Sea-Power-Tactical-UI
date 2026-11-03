using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>T-192 A3: the controller does not repeat the retreat command while the cover is the same (ship, mode, note, point within CoverMoveNm).</summary>
    internal static class AswT192Retreat
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class FakeThreats : IAirThreats
        {
            public AirGuardParams P { get; } = new AirGuardParams();
            public int Revision { get { return 0; } }
            public AirCover Cover;
            public IList<AirTrack> Tracks() { return new List<AirTrack>(); }
            public AirUnitThreat ForUnit(int uid) { return uid == 1 ? new AirUnitThreat { Uid = uid, TrackId = 10, Stage = AirStage.Retreat, Nm = 30, EtaS = 200, RNm = 10, RSrc = "class", Why = "dist" } : null; }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { return AirStage.Retreat; }
            public AirCover CoverFor(int uid) { return uid == 1 ? Cover : null; }
            public bool Ignored(string window, string group) { return false; }
            public void Ignore(string window, string group, double now) { }
        }

        private sealed class FakeSteer : IAirSteerGame
        {
            private readonly AswFakeGame _f;
            public List<string> Calls = new List<string>();
            public FakeSteer(AswFakeGame f) { _f = f; }
            private AswMember M(int uid) { return _f.Mem.Find(m => m.Uid == uid); }
            public GeoPt? Pos(int uid) { var m = M(uid); return m == null ? (GeoPt?)null : new GeoPt(m.Lat, m.Lon); }
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto:" + uid); return _f.Goto(uid, pts, altFt); }
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack) { Calls.Add("orbit:" + uid); return _f.Goto(uid, new List<GeoPt> { center }, 0); }
            public int Throttle(int uid) { return 2; }
            public string SetThrottle(int uid, int step) { return null; }
            public IList<CapStep> ThrottleSteps(int uid) { return new List<CapStep>(); }
            public string SetLowPreset(int uid) { return null; }
            public string Remember(int uid) { return null; }
            public string RestoreSpeedAlt(int uid) { return null; }
            public bool Evading(int uid) { return false; }
            public int Routes() { return Calls.Count(c => c.StartsWith("goto:", StringComparison.Ordinal) || c.StartsWith("orbit:", StringComparison.Ordinal)); }
        }

        private static AirCover Cov(double n, double e, string ship = "Yorktown", int uid = 500)
        {
            return new AirCover { Mode = "cover", ShipUid = uid, ShipName = ship, Point = new GeoPt(35 + n / 60.0, 20 + e / 60.0) };
        }

        private static int Lines(AswFakeGame f) { return f.Logs.Count(l => l.Contains("[AirGuard] retreat grp=")); }

        public static string Run()
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); }
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            var th = new FakeThreats();
            var st = new FakeSteer(f);
            c.AirGuard = th;
            c.AirSteer = new AirRetreatSteer(st, s => f.Logs.Add("steer " + s));
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"]}"));
            for (int i = 0; i < 100; i++) { f.Advance(1); f.Tick(c); }
            th.Cover = Cov(8, 6);
            for (int i = 0; i < 3; i++) { f.Advance(1); f.Tick(c); }
            if (Lines(f) != 1) return "first command: lines " + Lines(f);
            int routes = st.Routes();
            if (routes < 1) return "no route on the first command";
            for (int i = 0; i < 20; i++)
            {
                th.Cover = i % 2 == 0 ? Cov(9.5, 6) : Cov(8, 6);   // the point jumps by 1.5 nm
                f.Advance(1); f.Tick(c);
            }
            if (Lines(f) != 1) return "the same cover repeated the command: lines " + Lines(f);
            if (st.Routes() != routes) return "the same cover re-routed: " + routes + " -> " + st.Routes();
            th.Cover = Cov(13.5, 6);   // 5.5 nm away
            f.Advance(1); f.Tick(c);
            if (Lines(f) != 2) return "a far point must give a new command: lines " + Lines(f);
            if (st.Routes() != routes + 1) return "a far point must re-route: " + st.Routes();
            th.Cover = Cov(13.5, 6, "Perry", 501);
            f.Advance(1); f.Tick(c);
            if (Lines(f) != 3) return "another ship must give a new command: lines " + Lines(f);
            return null;
        }
    }
}
