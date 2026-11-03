#if SAR_WIP
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Fake game for SarController tests: flat sea around (35N, 20E), one island, simple movement and pickups.</summary>
    internal sealed class SarFakeGame : ISarGame
    {
        public const double Lat0 = 35, Lon0 = 20;
        public double Now { get; set; } = 1000;
        public double TimeScale { get; set; } = 1;
        public List<SarRaft> RaftList = new List<SarRaft>();
        public List<SarThreat> ThreatList = new List<SarThreat>();
        public Dictionary<int, SarUnit> Units = new Dictionary<int, SarUnit>();
        public Dictionary<int, List<GeoPt>> Routes = new Dictionary<int, List<GeoPt>>();
        public Dictionary<int, string> HeightStates = new Dictionary<int, string>();
        public HashSet<int> Player = new HashSet<int>();
        public List<string> Calls = new List<string>(), Events = new List<string>(), Logs = new List<string>();
        public int HeightCalls;
        public bool RemoveEmpty;
        public double IslandN = 0, IslandE = 0, IslandR = 3;
        public bool IslandOn = true;
        public bool PlacedModel;                                    // T-318: TakenByPlayer also compares the last route point with the base (off = old behaviour)
        public Dictionary<int, GeoPt> Placed = new Dictionary<int, GeoPt>();
        public List<int> Forgot = new List<int>();                  // NOT in Calls: tests count Calls
        public void ForgetRoute(int uid) { Forgot.Add(uid); Placed.Remove(uid); }

        public static GeoPt At(double n, double e) { return new GeoPt(Lat0 + n / 60.0, Lon0 + e / 60.0); }

        public SarUnit AddUnit(int uid, string kind, double n, double e, int telegraph = 1)
        {
            var g = At(n, e);
            var u = new SarUnit { Uid = uid, Kind = kind, Name = kind + uid, Type = kind, Lat = g.Lat, Lon = g.Lon, Telegraph = telegraph };
            if (kind == "helo") { u.Capacity = 4; u.Waypoints = 0; }
            Units[uid] = u;
            return u;
        }

        public SarRaft AddRaft(int id, double n, double e, int people = 1, string type = "all_life_raft_single")
        {
            var g = At(n, e);
            var r = new SarRaft { Id = id, Type = type, Lat = g.Lat, Lon = g.Lon, People = people };
            RaftList.Add(r);
            return r;
        }

        public int Count(string prefix) { int n = 0; foreach (var c in Calls) if (c.StartsWith(prefix)) n++; return n; }
        public bool Has(string prefix) { return Count(prefix) > 0; }
        public int EventCount(string name) { int n = 0; foreach (var c in Events) if (c.StartsWith(name + " ")) n++; return n; }

        /// <summary>0.30.1: calls of Rafts(); with Cache set the list goes through SarRaftCache like the game layer (RealT = the real clock).</summary>
        public int RaftCalls;
        public SarRaftCache Cache;
        public double RealT;
        public IList<SarRaft> Rafts() { RaftCalls++; return Cache == null ? RaftList : Cache.Get(Now, RealT, () => new List<SarRaft>(RaftList)); }
        public IList<SarThreat> Threats() { return ThreatList; }
        public List<int> OwnShips = new List<int>();
        public IList<int> OwnShipUids() { return OwnShips; }
        public SarUnit Unit(int uid) { SarUnit u; return Units.TryGetValue(uid, out u) ? u : null; }

        public double HeightM(GeoPt g)
        {
            HeightCalls++;
            if (!IslandOn) return -200;
            double n = (g.Lat - Lat0) * 60.0 - IslandN, e = (g.Lon - Lon0) * 60.0 - IslandE;
            return Math.Sqrt(n * n + e * e) < IslandR ? 50 : -200;
        }

        public string Goto(int uid, IList<GeoPt> pts, string heightState, int altFt)
        {
            Calls.Add("goto " + uid + " " + heightState + " " + altFt + " n=" + pts.Count);
            Routes[uid] = new List<GeoPt>(pts);
            Placed[uid] = pts[pts.Count - 1];
            HeightStates[uid] = heightState;
            Units[uid].Waypoints = pts.Count;
            return null;
        }

        public string SetTelegraph(int uid, int t) { Calls.Add("tg " + uid + " " + t); Units[uid].Telegraph = t; return null; }
        public string Surface(int uid) { Calls.Add("surface " + uid); Units[uid].Submerged = false; return null; }
        public string RestoreDepth(int uid) { Calls.Add("restore_depth " + uid); Units[uid].Submerged = true; return null; }
        public string SetRaftPickup(int uid, bool on) { Calls.Add("raft_pickup " + uid + " " + on); Units[uid].RaftPickup = on; return null; }
        public string Rtb(int uid) { Calls.Add("rtb " + uid); return null; }
        public string Hover(int uid) { Calls.Add("hover " + uid); Placed.Remove(uid); return null; }
        public string HoverOver(int uid, GeoPt ship) { Calls.Add("hoverover " + uid); return null; }
        public string StopEngines(int uid) { Calls.Add("stop " + uid); Routes.Remove(uid); Placed.Remove(uid); Units[uid].Telegraph = 0; return null; }
        public string Remember(int uid) { Calls.Add("remember " + uid); return null; }
        public string Restore(int uid) { Calls.Add("restore " + uid); Routes.Remove(uid); Placed.Remove(uid); return null; }
        public bool TakenByPlayer(int uid)
        {
            if (Player.Contains(uid)) return true;
            GeoPt p; List<GeoPt> rt;
            return PlacedModel && Placed.TryGetValue(uid, out p) && Routes.TryGetValue(uid, out rt) && rt.Count > 0
                && Math.Sqrt(Math.Pow((rt[rt.Count - 1].Lat - p.Lat) * 60.0, 2) + Math.Pow((rt[rt.Count - 1].Lon - p.Lon) * 60.0, 2)) > 0.5;
        }
        public void Event(string name, string dataJson) { Events.Add(name + " " + dataJson); }
        public void Log(string line) { Logs.Add(line); }

        /// <summary>Advance the world by dt seconds: movement along routes, then pickups.</summary>
        public void Step(double dt)
        {
            Now += dt;
            foreach (var u in Units.Values)
            {
                if (u.Evading) { u.SpeedKn = 0; continue; }
                List<GeoPt> route;
                double kn = u.Kind == "helo" ? 90 : Math.Max(0, u.Telegraph) * 5;
                if (!Routes.TryGetValue(u.Uid, out route) || route.Count == 0) { u.SpeedKn = 0; if (u.Kind == "helo") u.Waypoints = 0; continue; }
                u.SpeedKn = kn;
                double left = kn * dt / 3600.0;
                while (left > 0 && route.Count > 0)
                {
                    double dn = (route[0].Lat - u.Lat) * 60.0, de = (route[0].Lon - u.Lon) * 60.0;
                    double d = Math.Sqrt(dn * dn + de * de);
                    if (d <= left) { u.Lat = route[0].Lat; u.Lon = route[0].Lon; left -= d; route.RemoveAt(0); }
                    else { u.Lat += dn / d * left / 60.0; u.Lon += de / d * left / 60.0; left = 0; }
                }
                u.Waypoints = route.Count;
            }
            foreach (var u in Units.Values)
            {
                if (u.Evading) continue;
                foreach (var r in RaftList)
                {
                    if (r.Destroyed || r.People <= 0) continue;
                    double dn = (r.Lat - u.Lat) * 60.0, de = (r.Lon - u.Lon) * 60.0;
                    double d = Math.Sqrt(dn * dn + de * de);
                    if (u.Kind == "helo")
                    {
                        if (d <= 0.15 && u.RaftPickup && u.Waypoints == 0 && u.Onboard < u.Capacity)
                        {
                            int take = Math.Min(r.People, u.Capacity - u.Onboard);
                            u.Onboard += take; r.People -= take;
                        }
                    }
                    else if (d <= 0.3 && u.SpeedKn <= 13 && !u.Submerged) r.People = 0;
                }
            }
            if (RemoveEmpty) RaftList.RemoveAll(r => r.People <= 0);   // the real game destroys an emptied raft (0.28.1)
        }
    }
}
#endif
