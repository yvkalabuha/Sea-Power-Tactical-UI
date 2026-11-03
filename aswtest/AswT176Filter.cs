using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 C4 (v3-filter): land / shallow points go for good, hazard / air points are deferred and tried again; a leg through a circle defers its far point.
    internal static partial class AswT176Tests
    {
        private sealed class AirStub : IAirThreats
        {
            public AirGuardParams P { get; } = new AirGuardParams();
            public int Revision { get { return 0; } }
            public List<AirTrack> Tr = new List<AirTrack>();
            public Func<GeoPt, AirStage> At = p => AirStage.None;
            public IList<AirTrack> Tracks() { return Tr; }
            public Dictionary<int, AirStage> Unit = new Dictionary<int, AirStage>();
            public Dictionary<int, AirCover> Cov = new Dictionary<int, AirCover>();
            public AirUnitThreat ForUnit(int uid) { AirStage st; if (!Unit.TryGetValue(uid, out st) || st == AirStage.None) return null; return new AirUnitThreat { Uid = uid, TrackId = 10, Stage = st, Nm = 30, EtaS = 200, RNm = 10, RSrc = "class", Why = "dist" }; }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { var s = At(p); return s == AirStage.None ? null : new AirUnitThreat { Stage = s, Nm = 10, RNm = 10 }; }
            public AirStage GroupStage(string window, string group) { return AirStage.None; }
            public AirCover CoverFor(int uid) { AirCover cv; AirStage st; return Unit.TryGetValue(uid, out st) && st == AirStage.Retreat && Cov.TryGetValue(uid, out cv) ? cv : null; }
            public bool Ignored(string window, string group) { return false; }
            public void Ignore(string window, string group, double now) { }
        }

        private static string Filter()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            // heights by longitude: 0 land, -12, -25 shallow, -45 water
            var pts = new List<GeoPt> { new GeoPt(35, 20.00), new GeoPt(35, 20.01), new GeoPt(35, 20.02), new GeoPt(35, 20.03) };
            double[] h = { 0, -12, -25, -45 };
            f.Height = p => h[(int)Math.Round((p.Lon - 20.0) / 0.01)];
            int land, sh, hz, air, def;
            var kept = c.GuardPoints("G", pts, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 1 || land != 1 || sh != 2 || hz != 0 || def != 0 || Math.Abs(kept[0].Lon - 20.03) > 1e-9) return "buoy " + kept.Count + " land=" + land + " shallow=" + sh;
            // the dipping sonar needs 30.2 m: -25 is shallow, -45 water
            kept = c.GuardPoints("G", new List<GeoPt> { pts[2], pts[3] }, "dip", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 1 || sh != 1) return "dip -25 " + kept.Count + "/" + sh;
            // a deep sensor needs more water: 3 + 0.3 * 400 ft = 39.6 m
            f.Height = p => -35;
            kept = c.GuardPoints("G", new List<GeoPt> { pts[0] }, "buoy", 400, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 0 || sh != 1) return "deep sensor in 35 m";
            kept = c.GuardPoints("G", new List<GeoPt> { pts[0] }, "fly", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 1) return "a flight over shallow water is fine";
            f.Height = null;
            // a hazard circle: the point is deferred, after the ship is gone it is placed with the next package
            var inside = new GeoPt(35, 20.3);
            f.HazardList.Add(new AswHazard(inside, 3));
            kept = c.GuardPoints("G", new List<GeoPt> { inside }, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 0 || hz != 1 || def != 1) return "hazard not deferred " + kept.Count + "/" + hz + "/" + def;
            f.HazardList.Clear();
            var other = new GeoPt(35, 20.5);
            kept = c.GuardPoints("G", new List<GeoPt> { other }, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 2 || def != 0) return "deferred point not retried " + kept.Count + "/" + def;
            // stage 2 of the air threat: air, deferred, and placed after the stage is lifted
            var th = new AirStub { At = p => AswMath.DistNm(p, inside) < 20 ? AirStage.Retreat : AirStage.None };
            c.AirGuard = th;
            kept = c.GuardPoints("G", new List<GeoPt> { inside }, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 0 || air != 1 || def != 1) return "air not deferred " + kept.Count + "/" + air + "/" + def;
            th.At = p => AirStage.None;
            kept = c.GuardPoints("G", new List<GeoPt>(), "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 1 || def != 0) return "air point not retried " + kept.Count;
            // a warning stage (1) does not block
            th.At = p => AirStage.Warn;
            kept = c.GuardPoints("G", new List<GeoPt> { inside }, "buoy", 0, 0, out land, out sh, out hz, out air, out def);
            if (kept.Count != 1) return "warn stage blocked";
            // a leg through a circle defers the far point (touching is fine)
            th.At = p => AirStage.None;
            f.HazardList.Add(new AswHazard(new GeoPt(35, 20.1), 3));
            f.Mem[0].Lat = 35; f.Mem[0].Lon = 20.0;
            var legs = new List<GeoPt> { new GeoPt(35, 20.2), new GeoPt(35, 20.4) };
            kept = c.GuardPoints("G", legs, "buoy", 0, 1, out land, out sh, out hz, out air, out def);
            if (kept.Count != 0 || hz != 2 || def != 2) return "leg through a circle: kept " + kept.Count + " hazard " + hz + " deferred " + def;
            f.HazardList.Clear();
            f.HazardList.Add(new AswHazard(new GeoPt(35 + 3.0 / 60, 20.1), 3));   // touches the leg along y = 35 only at its edge
            kept = c.GuardPoints("G", legs, "buoy", 0, 1, out land, out sh, out hz, out air, out def);
            if (kept.Count < 2) return "an edge touch blocked the leg " + kept.Count;
            return null;
        }
    }
}
