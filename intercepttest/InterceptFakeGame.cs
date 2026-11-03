using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Synthetic land: rectangles and discs in miles north/east of (Lat0, Lon0); land = +12 m, sea = -500 m.</summary>
    internal sealed class FakeLand
    {
        public const double Lat0 = 36.0, Lon0 = 20.0;
        private readonly List<double[]> _rects = new List<double[]>(), _discs = new List<double[]>();
        public FakeLand Rect(double n0, double e0, double n1, double e1) { _rects.Add(new[] { n0, e0, n1, e1 }); return this; }
        public FakeLand Disc(double n, double e, double r) { _discs.Add(new[] { n, e, r }); return this; }
        public FakeLand Ring(double n, double e, double rIn, double rOut)
        {
            Rect(n - rOut, e - rOut, n - rIn, e + rOut); Rect(n + rIn, e - rOut, n + rOut, e + rOut);
            Rect(n - rOut, e - rOut, n + rOut, e - rIn); Rect(n - rOut, e + rIn, n + rOut, e + rOut);
            return this;
        }
        public double Height(GeoPt g)
        {
            double n = (g.Lat - Lat0) * 60.0, e = (g.Lon - Lon0) * 60.0;
            foreach (double[] r in _rects) if (n >= r[0] && n <= r[2] && e >= r[1] && e <= r[3]) return 12.0;
            foreach (double[] d in _discs) if ((n - d[0]) * (n - d[0]) + (e - d[1]) * (e - d[1]) <= d[2] * d[2]) return 12.0;
            return -500.0;
        }
    }

    /// <summary>The fake game of the controller stand: every game call goes to <see cref="Calls"/>; the interface has no other order, so
    /// "only goto, add_waypoint, set_telegraph" is checked on the whole journal. Time and motion are driven by <see cref="Advance"/>.</summary>
    internal sealed class InterceptFakeGame : IInterceptGame
    {
        public const int OwnUid = 11;
        public static readonly double[] Steps = { -4, 0, 6, 10, 14, 18, 24 };   // telegraph -1..5

        public double Now = 1000;
        public IcOwn OwnU;
        public readonly Dictionary<int, IcTrack> Tracks = new Dictionary<int, IcTrack>();
        public Func<GeoPt, double> Height = g => -500;
        public readonly List<string> Calls = new List<string>(), Logs = new List<string>(), Events = new List<string>();
        public string RouteError;
        // "Return control" memory as ControlMemory keeps it: the player's route before the first mod order, the first point of the mod route
        public bool HasMem; public List<GeoPt> Saved; public GeoPt PlacedByMod;

        public InterceptFakeGame()
        {
            OwnU = new IcOwn { Uid = OwnUid, Alive = true, Lat = FakeLand.Lat0, Lon = FakeLand.Lon0, Telegraph = 3, StepsKn = Steps };
            OwnU.Waypoints = new List<GeoPt>();
        }

        public static GeoPt At(double n, double e) { return new GeoPt(FakeLand.Lat0 + n / 60.0, FakeLand.Lon0 + e / 60.0); }

        public IcTrack AddTrack(int id, double n, double e, double vxEastKn, double vyNorthKn)
        {
            var t = new IcTrack { Id = id, Kind = "Surface", Relation = "hostile", Live = true, HasVel = true, Lat = FakeLand.Lat0 + n / 60.0, Lon = FakeLand.Lon0 + e / 60.0, VxKn = vxEastKn, VyKn = vyNorthKn, AgeS = 5 };
            Tracks[id] = t;
            return t;
        }

        public double NowS { get { return Now; } }
        public IcOwn Own(int uid) { return OwnU != null && OwnU.Uid == uid ? OwnU : null; }
        public IcTrack Track(int id) { IcTrack t; return Tracks.TryGetValue(id, out t) ? t : null; }
        public double HeightM(GeoPt g) { return Height(g); }

        public string Route(int uid, IList<GeoPt> pts, bool first)
        {
            Calls.Add("Route:" + (first ? "first" : "next") + ":" + pts.Count);
            if (RouteError != null) return RouteError;
            if (first && !HasMem) { HasMem = true; Saved = new List<GeoPt>(OwnU.Waypoints); }
            OwnU.Waypoints = new List<GeoPt>(pts);
            PlacedByMod = pts[0];
            return null;
        }

        public void NoteFirstWaypoint(int uid, GeoPt first) { Calls.Add("NoteFirst"); PlacedByMod = first; }

        public string SetTelegraph(int uid, int telegraph) { Calls.Add("Telegraph:" + telegraph); OwnU.Telegraph = telegraph; return null; }

        // T-169: the identified hostile tracks with their class range; they move with their velocity in Advance (a threat with a track id follows the track)
        public readonly List<IcThreat> ThreatList = new List<IcThreat>();
        public int ThreatCalls; public bool? LastSubmerged;
        public IList<IcThreat> Threats(bool submerged) { ThreatCalls++; LastSubmerged = submerged; return ThreatList; }

        public IcThreat AddThreat(int id, double n, double e, double rangeNm, double vxEastKn = 0, double vyNorthKn = 0, string name = "Osa", string weapon = "SS-N-2")
        {
            var t = new IcThreat { TrackId = id, Name = name, Weapon = weapon, WeaponKind = "missile", RangeNm = rangeNm, Lat = FakeLand.Lat0 + n / 60.0, Lon = FakeLand.Lon0 + e / 60.0, VxKn = vxEastKn, VyKn = vyNorthKn, HasVel = vxEastKn != 0 || vyNorthKn != 0 };
            ThreatList.Add(t);
            return t;
        }

        public void Log(string line) { Logs.Add(line); }
        public void Event(string name, string json) { Events.Add(name + " " + json); }

        /// <summary>ControlMemory.DecideRoute: restore when the first current waypoint is within 0.2 nm of the point the mod placed.</summary>
        public string RestoreDecision()
        {
            if (!HasMem || PlacedByMod.Lat == 0) return "nothing_saved";
            if (OwnU.Waypoints.Count == 0) return "restore";
            return AswMath.DistNm(OwnU.Waypoints[0], PlacedByMod) <= 0.2 ? "restore" : "player_changed";
        }

        /// <summary>Moves the clock, the own unit along its waypoints (passed points are dropped) and the tracks.</summary>
        public void Advance(double dt)
        {
            Now += dt;
            double left = Steps[Math.Max(0, Math.Min(Steps.Length - 1, OwnU.Telegraph + 1))] * dt / 3600.0;   // miles
            while (left > 1e-9 && OwnU.Waypoints.Count > 0)
            {
                GeoPt p = new GeoPt(OwnU.Lat, OwnU.Lon), w = OwnU.Waypoints[0];
                double d = AswMath.DistNm(p, w);
                if (d <= left) { OwnU.Lat = w.Lat; OwnU.Lon = w.Lon; OwnU.Waypoints.RemoveAt(0); left -= d; }
                else
                {
                    OwnU.Lat += (w.Lat - p.Lat) * left / d; OwnU.Lon += (w.Lon - p.Lon) * left / d;
                    left = 0;
                }
            }
            foreach (IcTrack t in Tracks.Values)
            {
                t.Lat += t.VyKn * dt / 3600.0 / 60.0;
                t.Lon += t.VxKn * dt / 3600.0 / 60.0;
            }
            foreach (IcThreat z in ThreatList)
            {
                IcTrack tr;
                if (Tracks.TryGetValue(z.TrackId, out tr)) { z.Lat = tr.Lat; z.Lon = tr.Lon; continue; }   // the target itself
                z.Lat += z.VyKn * dt / 3600.0 / 60.0;
                z.Lon += z.VxKn * dt / 3600.0 / 60.0;
            }
        }
    }
}
