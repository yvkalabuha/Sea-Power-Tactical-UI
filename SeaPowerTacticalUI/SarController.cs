using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: SAR controller over ISarGame (tested with a fake).
    internal sealed class SarUnit
    {
        public int Uid; public string Kind;
        public string Name, Type; public double Lat, Lon, SpeedKn, HeadingDeg, DepthM;
        public bool Submerged, RaftPickup, InFormation, ExtSar, GameSarZone, Evading, SmartAttack, Lost;
        public string AswGroup;
        public int Onboard, Capacity, Telegraph;
        public int Waypoints = -1;           // helo: waypoints left (0 = route finished, -1 = unknown)
    }

    internal interface ISarGame
    {
        double Now { get; }
        double TimeScale { get; }
        IList<SarRaft> Rafts();
        IList<SarThreat> Threats();
        SarUnit Unit(int uid);
        /// <summary>T-161: uids of the player's own alive controllable ships and submarines (taskforce vessels + submarines).</summary>
        IList<int> OwnShipUids();
        double HeightM(GeoPt g);
        string Goto(int uid, IList<GeoPt> pts, string heightState, int altFt);
        string SetTelegraph(int uid, int telegraph);
        string Surface(int uid);
        string RestoreDepth(int uid);
        string SetRaftPickup(int uid, bool on);
        string Rtb(int uid);
        string Hover(int uid);
        string HoverOver(int uid, GeoPt ship);   // T-147 H: hold position over the own ship (one waypoint at the ship); Hover = where it is
        string StopEngines(int uid);
        string Remember(int uid);
        string Restore(int uid);
        bool TakenByPlayer(int uid);
        /// <summary>T-318: SAR no longer owns the route of the unit: forget the base of TakenByPlayer (the last waypoint SAR placed). No game command.</summary>
        void ForgetRoute(int uid);
        void Event(string name, string dataJson);
        void Log(string line);
    }

    internal sealed partial class SarController
    {
        public const int TgSmall = 2, TgMedium = 3;   // game telegraph scale -1..5: "Small" ~ 2/3 ahead (<= 13 kn), "Medium" 3

        private sealed class ShipRun
        {
            public int Uid, RaftId, Done;
            public string Name, Type, OnComplete = "restore", State = "plan";
            public double RadiusNm, PlanAt, NoRaftsSince = -1;
            public GeoPt Origin, RaftAtPlan;
            public RouteJob Job;
            public bool SolveQueued, Final, Surfaced, Paused, NearShore, NextNear, SlowedOnce;
            public List<GeoPt> Route = new List<GeoPt>();
        }

        private sealed class HeloRun
        {
            public int RaftId, LastOnboard, SeenGen;
            public double GotoAt, NoRaftsSince = -1;
            public GeoPt RaftAt;
            public bool TcWarned;
        }

        private readonly ISarGame _g;
        private readonly SarParams _p;
        private readonly Action<Action> _bg;
        private readonly SarRegistry _reg = new SarRegistry();
        private readonly List<ShipRun> _ships = new List<ShipRun>();
        private readonly Dictionary<int, HeloRun> _helos = new Dictionary<int, HeloRun>();

        /// <summary>Recent helicopter pickups (where and when): a raft may leave the list after the helicopter that emptied it flew on.</summary>
        private sealed class Pick { public int Uid, People; public GeoPt At; public double T; }
        private readonly List<Pick> _picks = new List<Pick>();
        /// <summary>A raft that left the list counts as a helicopter pickup within this distance (nm) and time (s) of a recorded pickup.</summary>
        public const double HeloPickNm = 0.5, HeloPickS = 180;
        private readonly Dictionary<int, int> _people = new Dictionary<int, int>();
        private List<SarGroupOrderDto> _loadedGroups = new List<SarGroupOrderDto>();
        private List<SarMemDto> _loadedMemory = new List<SarMemDto>();

        public SarController(ISarGame g, SarParams p, Action<Action> background)
        {
            _g = g; _p = p ?? new SarParams();
            _bg = background ?? (a => a());
        }

        public SarRegistry Registry { get { return _reg; } }

        /// <summary>0.30.1: grows by one on every Tick that found a raft id not seen before (a new raft in the water); helicopters on their way
        /// to a far raft look again then (<see cref="BetterRaft"/>).</summary>
        public int RaftGen { get; private set; }

        /// <summary>0.30.1: a helicopter on its way switches to a new raft only when that saves at least this much travel (s) and halves it.</summary>
        public const double SwitchMinGainS = 120;
        public int RescuedByMod { get; private set; }

        // ------------------------------------------------------------------ helpers

        private static double Nm(GeoPt a, GeoPt b)
        {
            double x = (b.Lat - a.Lat) * 60.0, y = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * AswMath.LonK(a.Lat, b.Lat);
            return Math.Sqrt(x * x + y * y);
        }

        private static GeoPt Pos(SarUnit u) { return new GeoPt(u.Lat, u.Lon); }
        private static GeoPt Pos(SarRaft r) { return new GeoPt(r.Lat, r.Lon); }

        private static SarRaft Find(IList<SarRaft> l, int id)
        {
            if (l != null) foreach (var r in l) if (r.Id == id) return r;
            return null;
        }

        private ShipRun Ship(int uid)
        {
            foreach (var s in _ships) if (s.Uid == uid) return s;
            return null;
        }

        private void Ev(string name, string json) { _g.Event(name, json); }
        private static string Q(string s) { return Json.Str(s); }
        private void Log(string line) { _g.Log("[Sar] " + line); }
        private bool IsShip(int uid) { return Ship(uid) != null; }

        private RouteParams RP(SarUnit u)
        {
            return new RouteParams { MinDepthM = u.Kind == "sub" && u.Submerged ? _p.SubTransitDepthM : _p.MinDepthM, ShoreMarginNm = _p.ShoreMarginNm, MaxCells = _p.MaxCells };
        }

        private static SarArea AreaOf(ShipRun s)
        {
            if (s.RadiusNm > 0) return new SarArea { Kind = "circle", Center = s.Origin, RadiusNm = s.RadiusNm };
            return new SarArea();
        }

        // ------------------------------------------------------------------ busy / ships

        public string Busy(int uid)
        {
            if (IsShip(uid)) return "busy:sar_ship";
            var u = _g.Unit(uid);
            if (u == null) return null;
            if (u.AswGroup != null) return "busy:asw_group";
            if (u.SmartAttack) return "busy:smart_attack";
            if (u.ExtSar) return "ext_sar";
            if (u.GameSarZone) return "game_sar_zone";
            return null;
        }

        public bool ShipActive(int uid) { return IsShip(uid); }

        /// <summary>T-161: every own ship / submarine with its rescue state. Reads the game (one Unit() per ship): call it at most once a second.</summary>
        public List<SarShipRow> ShipRows()
        {
            var res = new List<SarShipRow>();
            var uids = _g.OwnShipUids();
            if (uids == null) return res;
            IList<SarRaft> rafts = null;
            foreach (int uid in uids)
            {
                var u = _g.Unit(uid);
                if (u == null || u.Lost || (u.Kind != "ship" && u.Kind != "sub")) continue;
                var row = new SarShipRow { Uid = uid, Name = u.Name, Type = u.Type, Kind = u.Kind };
                var run = Ship(uid);
                if (run != null)
                {
                    if (rafts == null) rafts = _g.Rafts();
                    row.Active = true; row.Surfaced = run.Surfaced; row.Done = run.Done; row.Total = Total(run, rafts); row.RadiusNm = run.RadiusNm;
                }
                else if (u.AswGroup != null) { row.Busy = "busy:asw_group"; row.Group = u.AswGroup; }
                else if (u.SmartAttack) row.Busy = "busy:smart_attack";
                else if (u.ExtSar) row.Busy = "ext_sar";
                else if (u.GameSarZone) row.Busy = "game_sar_zone";
                res.Add(row);
            }
            return res;
        }

        public string ShipCmd(Dictionary<string, object> c)
        {
            if (c == null) return "!bad_args";
            object o;
            if (!c.TryGetValue("uid", out o) || !(o is double)) return "!bad_args";
            int uid = (int)(double)o;
            string op = c.TryGetValue("op", out o) ? o as string : null;
            if (op != "start" && op != "stop") return "!bad_args";
            if (op == "stop")
            {
                var run = Ship(uid);
                if (run == null) return "!not_active";
                EndShip(run, "stopped", true, false);
                return "{\"uid\":" + uid + ",\"op\":\"stop\"}";
            }
            double radius = c.TryGetValue("radius_nm", out o) && o is double ? (double)o : 0;
            if (radius < 0) return "!bad_args";
            var u = _g.Unit(uid);
            if (u == null || u.Lost) return "!unknown_unit";
            if (u.Kind != "ship" && u.Kind != "sub") return "!bad_unit";
            string busy = Busy(uid);
            if (busy != null) return "!" + busy;
            _g.Remember(uid);
            var s = new ShipRun { Uid = uid, Name = u.Name, Type = u.Type, RadiusNm = radius, Origin = Pos(u), OnComplete = _p.ShipOnComplete == "stop" ? "stop" : "restore" };
            _ships.Add(s);
            Ev("sar_started", "{\"unit\":" + uid + ",\"name\":" + Q(u.Name) + ",\"radius_nm\":" + Json.Num(radius) + "}");
            Log("ship start uid=" + uid + " r=" + Json.Num(radius) + (u.Kind == "sub" ? " sub" : ""));
            return "{\"uid\":" + uid + ",\"op\":\"start\",\"radius_nm\":" + Json.Num(radius) + "}";
        }

        private int Total(ShipRun s, IList<SarRaft> rafts)
        {
            int n = s.Done;
            var area = AreaOf(s);
            if (rafts != null)
                foreach (var r in rafts)
                {
                    string w;
                    if (SarMath.Candidate(r, out w) && area.Contains(Pos(r))) n++;
                }
            return n;
        }

        /// <summary>Menu caption of a ship SAR run; the language is UiText.Lang.</summary>
        public string MenuCaption(int uid)
        {
            var s = Ship(uid);
            if (s == null) return null;
            int total = Total(s, _g.Rafts());
            string cap = UiText.Fmt("sar.caption", s.Done, total);
            if (s.Surfaced) cap += " " + UiText.Get("sar.caption.surfaced");
            return cap;
        }

        // ------------------------------------------------------------------ helicopters

        public string HeloAssign(int uid, SarArea area, double now, out int raftId)
        {
            raftId = 0;
            var u = _g.Unit(uid);
            if (u == null || u.Lost) return "no_unit";
            if (u.Capacity <= 0) return "no_capacity";
            int seats = u.Capacity - u.Onboard;
            if (seats <= 0) return "full";
            _reg.Release(uid, "reassign");
            var me = new SarRescuer { Uid = uid, Cls = "air", Lat = u.Lat, Lon = u.Lon, SpeedKn = u.SpeedKn, Seats = seats };
            double kn = u.SpeedKn > 30 ? u.SpeedKn : 100;
            var rafts = _g.Rafts();
            string why;
            var raft = SarMath.PickRaft(me, rafts, _reg, area, _g.Threats(), r => Nm(Pos(u), Pos(r)) / kn * 3600.0, now, _p, out why);
            if (raft == null) return why ?? "no_rafts";
            double eta = Nm(Pos(u), Pos(raft)) / kn * 3600.0;
            string code = _reg.TryClaim(raft, me, now, eta);
            if (code != null) return code;
            HeloRun h;
            if (!_helos.TryGetValue(uid, out h)) { h = new HeloRun(); _helos[uid] = h; }
            h.LastOnboard = u.Onboard; h.SeenGen = RaftGen;
            string err = GotoRaft(u, raft, h, now);
            if (err != null) { _reg.Release(uid, "goto_failed"); return err; }
            raftId = raft.Id;
            Log("helo assign uid=" + uid + " raft=" + raft.Id + " eta=" + (int)eta + "s");
            return null;
        }

        private string GotoRaft(SarUnit u, SarRaft raft, HeloRun h, double now)
        {
            _g.SetRaftPickup(u.Uid, true);
            string err = _g.Goto(u.Uid, new List<GeoPt> { Pos(raft) }, "very_low", (int)_p.TransitAltFt);
            h.RaftId = raft.Id; h.RaftAt = Pos(raft); h.GotoAt = now;
            return err;
        }

        public string HeloTick(int uid, SarArea area, double now)
        {
            var u = _g.Unit(uid);
            if (u == null || u.Lost) { ReleaseUnit(uid, "lost"); return null; }
            HeloRun h;
            _helos.TryGetValue(uid, out h);
            // T-231: only a helicopter the mod sent to a raft can be "taken": after ReleaseUnit (an air-threat retreat) the old raft point must not be compared with the new route
            if (h != null && h.RaftId != 0 && _g.TakenByPlayer(uid))
            {
                ReleaseUnit(uid, "player_took");
                Ev("sar_warn", "{\"code\":\"player_took\",\"unit\":" + uid + "}");
                return "player_took";
            }
            if (h == null) { h = new HeloRun { LastOnboard = u.Onboard }; _helos[uid] = h; }
            if (u.Onboard > h.LastOnboard)
            {
                int d = u.Onboard - h.LastOnboard;
                RescuedByMod += d;
                _picks.Add(new Pick { Uid = uid, People = d, At = Pos(u), T = _g.Now });
                if (_picks.Count > 64) _picks.RemoveAt(0);
                Log("helo uid=" + uid + " picked people=" + d + " onboard=" + u.Onboard + "/" + u.Capacity);
                Ev("sar_pickup", "{\"unit\":" + uid + ",\"people\":" + d + ",\"onboard\":" + u.Onboard + ",\"capacity\":" + u.Capacity + "}");
                h.LastOnboard = u.Onboard;
                h.TcWarned = false;
            }
            else if (u.Onboard < h.LastOnboard) h.LastOnboard = u.Onboard;
            if (u.Capacity > 0 && u.Onboard >= u.Capacity)
            {
                _reg.Release(uid, "full");
                Ev("sar_full", "{\"unit\":" + uid + ",\"onboard\":" + u.Onboard + ",\"capacity\":" + u.Capacity + "}");
                return "full";
            }
            var claim = _reg.ClaimOf(uid);
            var rafts = _g.Rafts();
            if (claim != null)
            {
                var raft = Find(rafts, claim.RaftId);
                if (raft == null || raft.Destroyed || raft.People <= 0) { _reg.Release(uid, "picked"); claim = null; }
                else if (h.SeenGen != RaftGen && BetterRaft(u, raft, area, rafts, now, h))
                {
                    _reg.Release(uid, "better_raft");   // the assignment below takes the nearer raft
                    claim = null;
                }
                else
                {
                    double dist = Nm(Pos(u), Pos(raft));
                    if (u.Waypoints == 0 && dist > 0.15 && now - h.GotoAt >= 5)
                    {
                        bool dropped = _reg.NoteMiss(uid, now, _p);
                        var cl = _reg.ClaimOf(uid);
                        int tries = dropped ? _p.MaxReacquire : (cl != null ? cl.Attempts : 0);
                        if (_g.TimeScale > 1 && tries >= 2 && !h.TcWarned)
                        {
                            h.TcWarned = true;
                            Ev("sar_warn", "{\"code\":\"tc_blocked\",\"unit\":" + uid + "}");
                        }
                        if (dropped)
                        {
                            Ev("sar_warn", "{\"code\":\"unreachable\",\"unit\":" + uid + ",\"raft\":" + raft.Id + ",\"class\":\"air\"}");
                            Log("helo uid=" + uid + " raft=" + raft.Id + " unreachable(air)");
                            claim = null;
                        }
                        else GotoRaft(u, raft, h, now);
                    }
                    else if (Nm(h.RaftAt, Pos(raft)) > 0.3) GotoRaft(u, raft, h, now);
                }
            }
            if (claim == null || _reg.ClaimOf(uid) == null)
            {
                int rid;
                string code = HeloAssign(uid, area, now, out rid);
                if (code == null) { h.NoRaftsSince = -1; return null; }
                if (code == "full") return "full";
                if (h.NoRaftsSince < 0) h.NoRaftsSince = now;
                var sh = new SarHelo { Uid = uid, Onboard = u.Onboard, Capacity = u.Capacity, FuelFrac = 1, EnduranceS = 1e9, HasTarget = false };
                return SarMath.LeaveReason(sh, new SarDemand { NoRaftsSince = h.NoRaftsSince }, now, _p);
            }
            return null;
        }

        /// <summary>0.30.1: new rafts appeared since the helicopter took its raft: a new one that is much nearer (score of SarMath.PickRaft: travel +
        /// priority class + shared) is worth a switch - before, the helicopter flew on to the far raft and the new ones waited for another machine.</summary>
        private bool BetterRaft(SarUnit u, SarRaft cur, SarArea area, IList<SarRaft> rafts, double now, HeloRun h)
        {
            h.SeenGen = RaftGen;
            int seats = u.Capacity - u.Onboard;
            if (seats <= 0) return false;
            double kn = u.SpeedKn > 30 ? u.SpeedKn : 100;
            Func<SarRaft, double> travel = r => Nm(Pos(u), Pos(r)) / kn * 3600.0;
            var me = new SarRescuer { Uid = u.Uid, Cls = "air", Lat = u.Lat, Lon = u.Lon, SpeedKn = u.SpeedKn, Seats = seats };
            string why;
            var best = SarMath.PickRaft(me, rafts, _reg, area, _g.Threats(), travel, now, _p, out why);
            if (best == null || best.Id == cur.Id) return false;
            double curT = travel(cur), bestT = travel(best);
            double curScore = curT + SarMath.PriorityClass(cur.Type, _p.PriorityRafts) * _p.PriorityBonusS;
            double bestScore = bestT + SarMath.PriorityClass(best.Type, _p.PriorityRafts) * _p.PriorityBonusS + (_reg.SeatsOn(best.Id) > 0 ? _p.SharedPenaltyS : 0);
            if (bestScore + SwitchMinGainS > curScore || bestT > curT * 0.5) return false;
            Log("helo uid=" + u.Uid + " switch raft=" + cur.Id + " -> " + best.Id + " eta=" + (int)curT + "s -> " + (int)bestT + "s (new raft)");
            return true;
        }

        public void ReleaseUnit(int uid, string reason)
        {
            _g.ForgetRoute(uid);   // T-318: SAR lets go of the unit, the base of TakenByPlayer goes with it
            _reg.Release(uid, reason);
            _helos.Remove(uid);
            var s = Ship(uid);
            if (s != null) EndShip(s, reason, reason != "player_took" && reason != "lost", false);
        }

        // ------------------------------------------------------------------ ship machine

        private void EndShip(ShipRun s, string reason, bool restore, bool complete)
        {
            _reg.Release(s.Uid, reason);
            _ships.Remove(s);
            if (restore)
            {
                if (s.Surfaced) _g.RestoreDepth(s.Uid);
                if (complete && s.OnComplete == "stop") _g.StopEngines(s.Uid);
                else _g.Restore(s.Uid);
            }
            Ev("sar_complete", "{\"unit\":" + s.Uid + ",\"reason\":" + Q(reason) + ",\"rescued_rafts\":" + s.Done + "}");
            Log("ship end uid=" + s.Uid + " reason=" + reason);
        }

        private GeoPt? PosOf(int uid)
        {
            var u = _g.Unit(uid);
            return u == null ? (GeoPt?)null : Pos(u);
        }

        public void Tick(double now)
        {
            var rafts = _g.Rafts();
            var threats = _g.Threats();
            if (rafts != null) foreach (var r in rafts) if (r.People > 0) { _people[r.Id] = r.People; _lastPos[r.Id] = Pos(r); }
            var shipPicked = new HashSet<int>();
            foreach (var kv in _reg.Sweep(rafts, threats, PosOf, now, _p))
            {
                var c = kv.Key;
                var s = Ship(c.Rescuer);
                if (kv.Value == "stalled") _reg.MarkUnreachable(c.RaftId, c.Cls, now, _p.UnreachableS);
                if (s == null) { Log("claim end uid=" + c.Rescuer + " raft=" + c.RaftId + " " + kv.Value); continue; }
                if (kv.Value == "picked")
                {
                    // 0.28.1: the game destroys a raft when its last person is picked up, so the raft is usually gone from the list here;
                    // its last known position decides (the old test needed the raft still in the list: ship pickups were never counted)
                    var r = Find(rafts, c.RaftId);
                    var u = _g.Unit(s.Uid);
                    GeoPt rp; bool known;
                    if (r != null) { rp = Pos(r); known = true; } else known = _lastPos.TryGetValue(c.RaftId, out rp);
                    if (known && u != null && Nm(Pos(u), rp) <= ShipPickNm)
                    {
                        int people; _people.TryGetValue(c.RaftId, out people);
                        s.Done++;
                        RescuedByMod += people;
                        shipPicked.Add(c.RaftId);
                        Log("ship uid=" + s.Uid + " picked raft=" + c.RaftId + " people=" + people);
                        Ev("sar_pickup", "{\"unit\":" + s.Uid + ",\"people\":" + people + ",\"onboard\":0,\"capacity\":0}");
                    }
                }
                s.State = "plan"; s.Job = null; s.Final = false; s.SolveQueued = false;
            }
            RaftsGone(rafts, shipPicked);
            foreach (var s in _ships.ToArray()) StepShip(s, rafts, threats, now);
        }

        /// <summary>A ship counts a raft as its pickup within this distance of the raft's last known position (nm).</summary>
        public const double ShipPickNm = 1.0;

        private readonly Dictionary<int, GeoPt> _lastPos = new Dictionary<int, GeoPt>();
        private HashSet<int> _seen;

        /// <summary>0.28.1: every raft that left the game's list is logged once with what took it: a ship of a rescue run, a helicopter
        /// (counted by its onboard), another unit near it, or nothing near (sank / removed by the game).</summary>
        private void RaftsGone(IList<SarRaft> rafts, HashSet<int> shipPicked)
        {
            if (rafts == null) return;
            var now = new HashSet<int>();
            foreach (var r in rafts) now.Add(r.Id);
            if (_seen != null)
            {
                bool fresh = false;
                foreach (var r in rafts)
                {
                    if (_seen.Contains(r.Id)) continue;
                    fresh = true;
                    // 0.30.1: the moment the mod sees a raft, with its people (the scan line alone is rate-limited)
                    Log("raft new id=" + r.Id + " people=" + r.People + " type=" + (r.Type ?? "-") + (r.OnLand ? " on_land" : ""));
                    Ev("sar_raft_new", "{\"raft\":" + r.Id + ",\"people\":" + r.People + ",\"lat\":" + Json.Num(r.Lat) + ",\"lon\":" + Json.Num(r.Lon) + "}");
                }
                if (fresh) RaftGen++;
            }
            if (_seen != null)
                foreach (int id in _seen)
                {
                    if (now.Contains(id)) continue;
                    int people; _people.TryGetValue(id, out people);
                    GeoPt p; bool known = _lastPos.TryGetValue(id, out p);
                    string why;
                    if (shipPicked.Contains(id)) why = "ship_pickup";
                    else
                    {
                        why = known ? "no_rescuer_near" : "unknown";
                        if (known)
                        {
                            foreach (var kv in _helos) { var u = _g.Unit(kv.Key); if (u != null && !u.Lost && Nm(Pos(u), p) <= HeloPickNm) { why = "helo uid=" + kv.Key; break; } }
                            if (why == "no_rescuer_near")
                                foreach (var pk in _picks) if (_g.Now - pk.T <= HeloPickS && Nm(pk.At, p) <= HeloPickNm) { why = "helo uid=" + pk.Uid + " (picked " + pk.People + ")"; break; }
                            if (why == "no_rescuer_near")
                                foreach (var sr in _ships) { var u = _g.Unit(sr.Uid); if (u != null && !u.Lost && Nm(Pos(u), p) <= ShipPickNm) { why = "ship uid=" + sr.Uid + " (not its raft)"; break; } }
                        }
                    }
                    Log("raft gone id=" + id + " people=" + people + " reason=" + why);
                    Ev("sar_raft_gone", "{\"raft\":" + id + ",\"people\":" + people + ",\"reason\":" + Q(why) + "}");
                    _lastPos.Remove(id); _people.Remove(id);
                }
            _seen = now;
        }

        private void StepShip(ShipRun s, IList<SarRaft> rafts, IList<SarThreat> threats, double now)
        {
            var u = _g.Unit(s.Uid);
            if (u == null || u.Lost) { EndShip(s, "lost", false, false); return; }
            if (_g.TakenByPlayer(s.Uid))
            {
                Ev("sar_warn", "{\"code\":\"player_took\",\"unit\":" + s.Uid + "}");
                EndShip(s, "player_took", false, false);
                return;
            }
            if (u.Evading)
            {
                if (!s.Paused) { s.Paused = true; Log("ship uid=" + s.Uid + " paused (evading)"); }
                var pc = _reg.ClaimOf(s.Uid);
                if (pc != null) pc.LastProgressS = now;
                return;
            }
            if (s.Paused) { s.Paused = false; s.State = "plan"; s.Job = null; s.Final = false; s.SolveQueued = false; Log("ship uid=" + s.Uid + " resumed"); }

            if (s.State == "plan") { PlanShip(s, u, rafts, threats, now); return; }
            if (s.State == "route") { RouteShip(s, u, now); return; }

            var claim = _reg.ClaimOf(s.Uid);
            var raft = claim == null ? null : Find(rafts, claim.RaftId);
            if (raft == null) { s.State = "plan"; s.Job = null; return; }
            double d = Nm(Pos(u), Pos(raft));
            if (u.Kind == "sub" && u.Submerged && !s.Surfaced && d <= _p.SubSurfaceNm)
            {
                string e = _g.Surface(s.Uid);
                s.Surfaced = true;
                Log("sub uid=" + s.Uid + " surfacing d=" + Json.Num(Math.Round(d, 2)) + (e != null ? " err=" + e : ""));
            }
            if (SarRouteMath.NeedReroute(Pos(raft), s.RaftAtPlan, Pos(u), s.Route, s.NearShore, now - s.PlanAt))
            {
                s.State = "plan"; s.Job = null; s.SolveQueued = false;
                Log("ship uid=" + s.Uid + " reroute");
                return;
            }
            if (s.State == "transit" && d <= _p.ShipFinalNm)
            {
                _g.SetTelegraph(s.Uid, TgSmall);
                s.Final = true; s.State = "final";
            }
            else if (s.State == "final" && u.SpeedKn > 12 && !s.SlowedOnce)
            {
                s.SlowedOnce = true;
                _g.SetTelegraph(s.Uid, TgSmall - 1);
            }
        }

        private void PlanShip(ShipRun s, SarUnit u, IList<SarRaft> rafts, IList<SarThreat> threats, double now)
        {
            var claim = _reg.ClaimOf(s.Uid);
            SarRaft raft = claim == null ? null : Find(rafts, claim.RaftId);
            if (raft == null)
            {
                if (claim != null) _reg.Release(s.Uid, "gone");
                var me = new SarRescuer { Uid = s.Uid, Cls = "water", Lat = u.Lat, Lon = u.Lon, SpeedKn = u.SpeedKn, Seats = int.MaxValue };
                double kn = u.SpeedKn > 5 ? u.SpeedKn : 10;
                string why;
                raft = SarMath.PickRaft(me, rafts, _reg, AreaOf(s), threats, r => Nm(Pos(u), Pos(r)) / kn * 3600.0, now, _p, out why);
                if (raft == null)
                {
                    if (s.NoRaftsSince < 0) { s.NoRaftsSince = now; Ev("sar_no_rafts", "{\"unit\":" + s.Uid + "}"); }
                    if (now - s.NoRaftsSince >= _p.NoRaftsGraceS) EndShip(s, "no_rafts", true, true);
                    return;
                }
                double eta = Nm(Pos(u), Pos(raft)) / kn * 3600.0;
                if (_reg.TryClaim(raft, me, now, eta) != null) return;
                s.NoRaftsSince = -1;
                s.NextNear = s.Done > 0 && Nm(Pos(u), Pos(raft)) <= _p.NextRaftNearNm;
            }
            s.RaftId = raft.Id;
            s.RaftAtPlan = Pos(raft);
            s.PlanAt = now;
            s.Job = new RouteJob(Pos(u), Pos(raft), RP(u));
            s.SolveQueued = false;
            s.State = "route";
        }

        private void RouteShip(ShipRun s, SarUnit u, double now)
        {
            var job = s.Job;
            if (job == null) { s.State = "plan"; return; }
            string st = job.State;
            if (st == "probe_line" || st == "probe_grid") s.SolveQueued = false;
            else if (st == "ready_to_solve" && !s.SolveQueued)
            {
                s.SolveQueued = true;
                _bg(job.Solve);
            }
            if (job.State != "done") return;
            var res = job.Result;
            Log("route uid=" + s.Uid + " probes=" + res.Probes + " cells=" + res.Cells + " ms=" + Json.Num(Math.Round(res.Ms, 1)) +
                " pts=" + (res.Points == null ? 0 : res.Points.Count) + " kind=" + res.Kind + (res.Why != null ? " why=" + res.Why : ""));
            s.Job = null;
            if (res.Kind == "none")
            {
                _reg.MarkUnreachable(s.RaftId, "water", now, _p.UnreachableS);
                _reg.Release(s.Uid, "unreachable");
                Ev("sar_warn", "{\"code\":\"" + (res.Why == "land" ? "land_raft" : "unreachable") + "\",\"unit\":" + s.Uid + ",\"raft\":" + s.RaftId + "}");
                s.State = "plan";
                return;
            }
            s.NearShore = res.NearShore;
            s.Route = new List<GeoPt> { Pos(u) };
            s.Route.AddRange(res.Points);
            string err = _g.Goto(s.Uid, res.Points, "transit", 0);
            if (err != null) Log("goto failed uid=" + s.Uid + " " + err);
            if (s.NextNear)
            {
                _g.SetTelegraph(s.Uid, TgSmall);
                s.Final = true; s.State = "final";
            }
            else
            {
                int t = u.Telegraph;
                if (_p.ShipTransitTelegraph != "current")
                {
                    int v; if (int.TryParse(_p.ShipTransitTelegraph, out v)) t = v;
                }
                if (t < TgMedium) t = TgMedium;
                if (t != u.Telegraph) _g.SetTelegraph(s.Uid, t);
                s.Final = false; s.State = "transit";
            }
            s.SlowedOnce = false;
        }

        public void FramePump()
        {
            int left = _p.ProbeBudget;
            foreach (var s in _ships)
            {
                if (left <= 0) break;
                if (s.State != "route" || s.Job == null || s.Paused) continue;
                string st = s.Job.State;
                if (st != "probe_line" && st != "probe_grid") continue;
                left -= s.Job.Step(_g.HeightM, left);
            }
        }

        // ------------------------------------------------------------------ state / save

        public string ShipsJson()
        {
            var sb = new System.Text.StringBuilder("[");
            var rafts = _g.Rafts();
            for (int i = 0; i < _ships.Count; i++)
            {
                var s = _ships[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"uid\":").Append(s.Uid).Append(",\"name\":").Append(Q(s.Name)).Append(",\"state\":").Append(Q(s.Paused ? "paused" : s.State))
                  .Append(",\"raft\":").Append(s.RaftId).Append(",\"done\":").Append(s.Done).Append(",\"total\":").Append(Total(s, rafts))
                  .Append(",\"surfaced\":").Append(Json.Bool(s.Surfaced));
                var u = _g.Unit(s.Uid);
                if (u != null && !u.Lost) sb.Append(",\"lat\":").Append(Json.Num(u.Lat)).Append(",\"lon\":").Append(Json.Num(u.Lon));
                if (s.State != "plan" && s.Route.Count > 1)
                {
                    sb.Append(",\"route\":[");
                    for (int k = 0; k < s.Route.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append('[').Append(Json.Num(s.Route[k].Lat)).Append(',').Append(Json.Num(s.Route[k].Lon)).Append(']');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        public string RaftsJson()
        {
            var sb = new System.Text.StringBuilder("[");
            var rafts = _g.Rafts();
            var threats = _g.Threats();
            double now = _g.Now;
            bool first = true;
            if (rafts != null)
                foreach (var r in rafts)
                {
                    string w;
                    if (!SarMath.Candidate(r, out w)) continue;
                    string state = SarMath.UnderThreat(r, threats, _p.ThreatStandoffNm) ? "threat"
                        : _reg.Unreachable(r.Id, "air", now) && _reg.Unreachable(r.Id, "water", now) ? "unreachable"
                        : _reg.SeatsOn(r.Id) > 0 ? (_reg.Covered(r) ? "covered" : "claimed") : "free";
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(r.Id).Append(",\"type\":").Append(Q(r.Type)).Append(",\"lat\":").Append(Json.Num(r.Lat))
                      .Append(",\"lon\":").Append(Json.Num(r.Lon)).Append(",\"people\":").Append(r.People).Append(",\"state\":").Append(Q(state)).Append('}');
                }
            return sb.Append(']').ToString();
        }

        /// <summary>Ships on a rescue run (cheap; no save snapshot).</summary>
        public int ActiveShips { get { return _ships.Count; } }

        public SarSaveData Save()
        {
            var d = new SarSaveData { RescuedByMod = RescuedByMod };
            d.Groups.AddRange(_loadedGroups);
            d.Memory.AddRange(_loadedMemory);
            foreach (var s in _ships) d.Ships.Add(new SarShipDto { Uid = s.Uid, Name = s.Name, Type = s.Type, RadiusNm = s.RadiusNm, OnComplete = s.OnComplete });
            return d;
        }

        public void Load(SarSaveData d, double now)
        {
            ClearAll();
            if (d == null) return;
            RescuedByMod = d.RescuedByMod;
            _loadedGroups = new List<SarGroupOrderDto>(d.Groups);
            _loadedMemory = new List<SarMemDto>(d.Memory);
            foreach (var x in d.Ships)
            {
                var u = _g.Unit(x.Uid);
                if (u == null || u.Lost || (x.Name != null && u.Name != null && x.Name != u.Name)) { Log("load: ship uid=" + x.Uid + " not found"); continue; }
                _ships.Add(new ShipRun { Uid = x.Uid, Name = u.Name, Type = u.Type, RadiusNm = x.RadiusNm, OnComplete = x.OnComplete == "stop" ? "stop" : "restore", Origin = Pos(u) });
            }
        }

        private void ClearAll()
        {
            _reg.Clear(); _ships.Clear(); _helos.Clear(); _picks.Clear(); _people.Clear(); _lastPos.Clear(); _seen = null;
            _loadedGroups = new List<SarGroupOrderDto>(); _loadedMemory = new List<SarMemDto>();
            RescuedByMod = 0;
        }

        public void OnMissionChanged() { ClearAll(); }

        /// <summary>T-130: the mission was left.</summary>
        public void OnMissionLeft() { ClearAll(); }
    }
}
