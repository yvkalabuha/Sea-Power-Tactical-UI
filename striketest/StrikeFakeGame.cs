using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-341 stand: a scripted game for the strike controller (no Unity, no reflection).</summary>
    internal sealed class StrikeFakeGame : IStrikeGame
    {
        public double Time = 1000;
        public List<StrikeAircraft> Air = new List<StrikeAircraft>();
        public List<StrikeShip> Fleet = new List<StrikeShip>();
        public bool SimTof;                 // Tof answers true (EstimateShot present)
        public double SimTofS = 400, SimTofPerNm;   // SimTofPerNm > 0: the simulated flight is proportional to the range
        public string Calib = "cache";
        public int TofCalls;
        public List<string> TofArgs = new List<string>();               // T-342: "altFt:kn" of every Tof call
        public List<string> Calls = new List<string>();                 // Goto / Orbit / Rtb log: "goto uid lat,lon" ...
        public Func<GeoPt, bool> Land;                                  // null = all sea
        public List<StrikeDeckItem> Deck = new List<StrikeDeckItem>();
        public string GotoErr, RtbErr;

        public string Goto(int uid, IList<GeoPt> pts, double altFt)
        {
            Calls.Add("goto " + uid + " " + pts[pts.Count - 1].Lat.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + pts[pts.Count - 1].Lon.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            GotoAlts.Add(uid + ":" + (int)altFt + ":" + pts.Count);
            if (GotoErr != null) return GotoErr;
            _route[uid] = new List<GeoPt>(pts);
            return null;
        }
        public string Orbit(int uid, GeoPt center, double radiusNm) { Calls.Add("orbit " + uid); if (GotoErr == null) _route.Remove(uid); return GotoErr; }
        public List<string> GotoAlts = new List<string>();     // "uid:alt:points" of every Goto
        private readonly Dictionary<int, List<GeoPt>> _route = new Dictionary<int, List<GeoPt>>();
        public string Rtb(int uid) { Calls.Add("rtb " + uid); return RtbErr; }
        public bool IsLand(GeoPt p) { return Land != null && Land(p); }
        public IList<StrikeDeckItem> DeckItems() { return Deck; }

        // ---- phase C: the orders, the weapon status, the fate, the missiles in flight, and a simple world that moves (Advance)
        public Dictionary<int, string> IssueErr = new Dictionary<int, string>();      // per aircraft: the code Issue answers (every call)
        public Func<int, int, string> IssueHook;                                       // (uid, call number of the uid) -> code or null; overrides IssueErr
        public List<string> IssueLog = new List<string>();                             // "t uid tgt ammo salvo -> ok|code"
        public Dictionary<int, int> IssueCalls = new Dictionary<int, int>();
        public Dictionary<int, double> IssueT = new Dictionary<int, double>();         // first successful Issue per uid (game time)
        public bool NoLaunch;                                                          // the order is accepted, the missile never leaves
        public double TofScale = 1;                                                    // the world's time of flight against the rough formula
        public Dictionary<int, double> ShootDownFrac = new Dictionary<int, double>();  // target -> the missiles die at this share of the flight
        public Dictionary<int, string> FateMap = new Dictionary<int, string>();
        public Dictionary<int, int> FateHits = new Dictionary<int, int>();
        public List<string> RoeCalls = new List<string>();
        public Dictionary<int, string> RoeNow = new Dictionary<int, string>();
        public List<(double t, int tgt, int uid, double dist)> Arrivals = new List<(double, int, int, double)>();
        public int FateCalls, InFlightCalls;
        public HashSet<int> NeverArrive = new HashSet<int>();                          // aircraft whose missiles stay in flight for ever (a missile the game never removes)
        private sealed class Pending { public double T; public int Uid, Tgt, Salvo; public string Ammo; }
        private sealed class Msl { public int Id, Uid, Tgt; public double T0, T1, Lat0, Lon0, Frac; public bool Down; }
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<Msl> _msl = new List<Msl>();
        private int _mslSeq = 1000;
        // ---- T-353: a carrier shot down (its launched list is gone, the missiles fly on - like the game) and single missiles shot down
        public HashSet<int> LostCarriers = new HashSet<int>();
        public HashSet<int> ShootDownSeq = new HashSet<int>();                         // 1-based number of the missile in the order of launch (all targets): this one dies
        public double ShootDownSeqFrac = 0.5;                                          // ... at this share of its flight
        public bool NoMslCache;                                                        // control: the adapter of before T-353 (a lost carrier lists nothing)
        private readonly StrikeMslCache _mslCache = new StrikeMslCache();
        /// <summary>The carrier dies: it leaves the world, its orders are gone, the missiles it launched keep flying.</summary>
        public void LoseCarrier(int uid)
        {
            LostCarriers.Add(uid);
            Air.RemoveAll(x => x.Uid == uid);
            _route.Remove(uid);
            _pending.RemoveAll(x => x.Uid == uid);
        }

        // ---- phase D: the jammer and the active-task read
        public bool EwOk = true;                                                       // EwCapable
        public string JamErr;                                                          // the code Jam answers (null = ok)
        public List<string> JamCalls = new List<string>();                             // "t uid on|off lat,lon"
        public Dictionary<int, bool> JamOn = new Dictionary<int, bool>();              // the state of the stations the mod set
        public Dictionary<string, bool?> AttackTask = new Dictionary<string, bool?>(); // "uid:target" -> HasAttackTask
        public bool EwCapable { get { return EwOk; } }
        public string Jam(int uid, GeoPt pt, bool on)
        {
            JamCalls.Add(Time.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + uid + " " + (on ? "on" : "off") + " " + pt.Lat.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + pt.Lon.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (JamErr != null) return JamErr;
            JamOn[uid] = on;
            return null;
        }
        // ---- T-342 phase D: the hostile fighters and the jam by object
        public List<StrikeFighter> FighterList = new List<StrikeFighter>();
        public int FightersCalls;
        public string JamObjErr;                                                       // the code JamObject answers (null = ok)
        public List<string> JamObjCalls = new List<string>();                          // "t uid on|off track"
        public Dictionary<int, int> JamObjOn = new Dictionary<int, int>();             // uid -> the track jammed now (0 = none)
        public IList<StrikeFighter> Fighters() { FightersCalls++; return FighterList; }
        public string JamObject(int uid, int trackId, bool on)
        {
            JamObjCalls.Add(Time.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + uid + " " + (on ? "on" : "off") + " " + trackId);
            if (on && JamObjErr != null) return JamObjErr;
            if (on) { JamOn[uid] = true; JamObjOn[uid] = trackId; } else { JamOn[uid] = false; JamObjOn[uid] = 0; }
            return null;
        }
        public bool? HasAttackTask(int uid, int targetId) { bool? r; return AttackTask.TryGetValue(uid + ":" + targetId, out r) ? r : null; }

        public string Issue(int uid, int targetId, string ammo, int salvo)
        {
            int n; IssueCalls.TryGetValue(uid, out n); n++; IssueCalls[uid] = n;
            string err = IssueHook != null ? IssueHook(uid, n) : (IssueErr.ContainsKey(uid) ? IssueErr[uid] : null);
            IssueLog.Add(Time.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + uid + " " + targetId + " " + ammo + " " + salvo + " -> " + (err ?? "ok"));
            if (err != null) return err;
            if (!IssueT.ContainsKey(uid)) IssueT[uid] = Time;
            var a = Air.Find(x => x.Uid == uid); var s = Fleet.Find(x => x.TrackId == targetId);
            if (a == null || s == null) return "unit_not_found";
            if (NoLaunch) return null;
            double brg = AswMath.BearingDeg(new GeoPt(a.Lat, a.Lon), new GeoPt(s.Lat, s.Lon));
            double delay = 4 + StrikeTotMath.DeltaPsi(a.HeadingDeg, brg) / 8.0;   // the live strikes at 200 ft of 2026-10-31
            _pending.Add(new Pending { T = Time + delay, Uid = uid, Tgt = targetId, Ammo = ammo, Salvo = salvo });
            return null;
        }

        public string SetRoe(int uid, string roe, out string previous)
        {
            string prev; RoeNow.TryGetValue(uid, out prev);
            previous = prev ?? "free";
            RoeNow[uid] = roe;
            RoeCalls.Add(uid + " " + roe + " (was " + previous + ")");
            return null;
        }

        public string Fate(int trackId, out int hits)
        {
            FateCalls++;
            hits = FateHits.ContainsKey(trackId) ? FateHits[trackId] : 0;
            return FateMap.ContainsKey(trackId) ? FateMap[trackId] : "unknown";
        }

        public IList<StrikeMslSeen> InFlight(int uid)
        {
            InFlightCalls++;
            var res = new List<StrikeMslSeen>();
            if (NoMslCache && LostCarriers.Contains(uid)) return res;
            // like the adapter of the game: a living carrier lists its missiles, the cache follows them after its loss
            if (!LostCarriers.Contains(uid)) foreach (var m in _msl) if (m.Uid == uid) _mslCache.Note(uid, m.Id, m);
            _mslCache.Prune(h => _msl.Contains((Msl)h));
            foreach (var kv in _mslCache.Live(uid, h => _msl.Contains((Msl)h)))
            {
                var m = (Msl)kv.Value;
                var s = Fleet.Find(x => x.TrackId == m.Tgt);
                double f = m.T1 > m.T0 ? Math.Max(0, Math.Min(1, (Time - m.T0) / (m.T1 - m.T0))) : 1;
                if (m.Down) f *= m.Frac;   // T-353: a missile shot down dies where it is, short of the target
                double la = s == null ? m.Lat0 : m.Lat0 + (s.Lat - m.Lat0) * f, lo = s == null ? m.Lon0 : m.Lon0 + (s.Lon - m.Lon0) * f;
                res.Add(new StrikeMslSeen { Id = m.Id, Uid = uid, TargetId = m.Tgt, Lat = la, Lon = lo, T = Time });
            }
            return res;
        }

        /// <summary>Moves the world by dt seconds in steps of one second: ships on their course, aircraft along the last route, orders turn into launches, missiles arrive.</summary>
        public void Advance(double dt, double step = 1)
        {
            for (double done = 0; done < dt - 1e-9; done += step)
            {
                Time += step;
                foreach (var sh in Fleet) if (sh.SpeedKn > 0) { var p = AswMath.Offset(new GeoPt(sh.Lat, sh.Lon), sh.CourseDeg, sh.SpeedKn * step / 3600.0); sh.Lat = p.Lat; sh.Lon = p.Lon; }
                foreach (var a in Air)
                {
                    List<GeoPt> r;
                    if (!a.Airborne || !_route.TryGetValue(a.Uid, out r) || r.Count == 0) continue;
                    double left = a.CruiseKn * step / 3600.0;
                    while (left > 0 && r.Count > 0)
                    {
                        var pos = new GeoPt(a.Lat, a.Lon);
                        double d = AswMath.DistNm(pos, r[0]);
                        a.HeadingDeg = d > 1e-6 ? AswMath.BearingDeg(pos, r[0]) : a.HeadingDeg;
                        if (d <= left) { a.Lat = r[0].Lat; a.Lon = r[0].Lon; left -= d; r.RemoveAt(0); }
                        else { var q = AswMath.Offset(pos, a.HeadingDeg, left); a.Lat = q.Lat; a.Lon = q.Lon; left = 0; }
                    }
                    if (r.Count == 0) _route.Remove(a.Uid);
                }
                foreach (var pd in _pending.ToArray())
                {
                    if (Time < pd.T) continue;
                    _pending.Remove(pd);
                    var a = Air.Find(x => x.Uid == pd.Uid); var s = Fleet.Find(x => x.TrackId == pd.Tgt);
                    var m = a == null ? null : a.Missiles.Find(x => x.Ammo == pd.Ammo);
                    if (m == null || s == null) continue;
                    int n = Math.Min(pd.Salvo, m.Count);
                    m.Count -= n;
                    for (int k = 0; k < n; k++)
                    {
                        double dist = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), new GeoPt(s.Lat, s.Lon));
                        double tof = StrikeTotMath.RoughTofS(dist, m.SpeedKn) * TofScale * (m.SpeedKn >= StrikeCalib.SupersonicKn ? StrikeCalib.TofScaleSup : 1.0);   // T-342: a supersonic missile of the real game flies TofScaleSup of the plan (the fit of E1 / E1b)
                        double frac;
                        bool down = ShootDownFrac.TryGetValue(pd.Tgt, out frac);
                        int seq = ++_mslSeq;
                        if (!down && ShootDownSeq.Contains(seq - 1000)) { down = true; frac = ShootDownSeqFrac; }
                        _msl.Add(new Msl { Id = seq, Uid = pd.Uid, Tgt = pd.Tgt, T0 = Time, T1 = Time + (down ? tof * frac : tof) + k, Lat0 = a.Lat, Lon0 = a.Lon, Down = down, Frac = frac });
                    }
                }
                foreach (var m in _msl.ToArray())
                {
                    if (Time < m.T1 || NeverArrive.Contains(m.Uid)) continue;
                    _msl.Remove(m);
                    var s = Fleet.Find(x => x.TrackId == m.Tgt);
                    double d = 0;
                    if (s != null && m.Down) { double f = m.Frac; d = AswMath.DistNm(new GeoPt(m.Lat0, m.Lon0), new GeoPt(s.Lat, s.Lon)) * (1 - f); }
                    Arrivals.Add((Time, m.Tgt, m.Uid, d));
                }
            }
        }

        // ---- T-342 phase B: the decks
        public List<StrikeDeckInfo> DeckInfos = new List<StrikeDeckInfo>();
        public string Mode = "Casual";
        public int DecksCalls;
        public IList<StrikeDeckInfo> Decks() { DecksCalls++; return DeckInfos; }
        public string DeckTimingsMode() { return Mode; }
        public Dictionary<string, int> TaskVehicles = new Dictionary<string, int>();
        public int TaskVehicleUid(string key) { int u; return TaskVehicles.TryGetValue(key ?? "", out u) ? u : 0; }

        // ---- T-342 phase C: the sensor of the clear_pause (destroyed | alive | gone | unknown); the default is alive
        public Dictionary<int, string> DestroyedMap = new Dictionary<int, string>();
        public int DestroyedCalls;
        public string Destroyed(int trackId) { DestroyedCalls++; string s; return DestroyedMap.TryGetValue(trackId, out s) ? s : "alive"; }

        // ---- T-342 phase A: the damage bonus, the own hits, the speed
        public double Bonus = 0.25; public bool BonusKnown = true;
        public List<StrikeHitSeen> HitQueue = new List<StrikeHitSeen>();               // NewHits drains this
        public bool HitsOn = true;
        public List<string> SpeedCalls = new List<string>();                           // "set uid kn" | "restore uid"
        public string SpeedErr;
        private readonly HashSet<int> _speedSet = new HashSet<int>();
        public double DamageBonus(out bool known) { known = BonusKnown; return BonusKnown ? Bonus : StrikeCalib.BonusFallback; }
        public bool HitsAvailable { get { return HitsOn; } }
        public IList<StrikeHitSeen> NewHits() { var r = new List<StrikeHitSeen>(HitQueue); HitQueue.Clear(); return HitsOn ? r : new List<StrikeHitSeen>(); }
        public string SetSpeed(int uid, double kn) { SpeedCalls.Add("set " + uid + " " + kn.ToString("0", System.Globalization.CultureInfo.InvariantCulture)); if (SpeedErr != null) return SpeedErr; _speedSet.Add(uid); return null; }
        public string RestoreSpeed(int uid) { SpeedCalls.Add("restore " + uid); return _speedSet.Remove(uid) ? null : "none"; }

        public double Now { get { return Time; } }
        public IList<StrikeAircraft> Aircraft() { return Air; }
        public IList<StrikeShip> Ships() { return Fleet; }
        public string CalibSource { get { return Calib; } }
        public double DifficultyValue = 1;   // phase E1: settable
        public string DiffSrcValue = "default";
        public Dictionary<int, int> GameIds = new Dictionary<int, int>();
        public double Difficulty() { return DifficultyValue; }
        public string DiffSrc { get { return DiffSrcValue; } }
        public int GameId(int uid) { int g; return GameIds.TryGetValue(uid, out g) ? g : -1; }

        public bool Tof(int uid, StrikeMissile m, double fromLat, double fromLon, double altFt, double kn, StrikeShip target, double aimLat, double aimLon, out double tofS)
        {
            TofCalls++; TofArgs.Add(((int)altFt).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ((int)kn).ToString(System.Globalization.CultureInfo.InvariantCulture));
            tofS = SimTof ? (SimTofPerNm > 0 ? SimTofPerNm * AswMath.DistNm(new GeoPt(fromLat, fromLon), new GeoPt(aimLat, aimLon)) : SimTofS) : 0;
            return SimTof;
        }

        public StrikeAircraft AddPlane(int uid, string type, string ammo, int count, double lat = 39.5, double lon = 10, bool airborne = true, string objType = "Aircraft")
        {
            var a = new StrikeAircraft { Uid = uid, Type = type, Name = type + "-" + uid, ObjType = objType, Airborne = airborne, Lat = lat, Lon = lon, HeadingDeg = 0, SpeedKn = 450, CruiseKn = 450, CruiseAltFt = 25000, AltFt = 25000, FuelFrac = 1, EnduranceS = 18000 };
            a.Missiles.Add(new StrikeMissile { Ammo = ammo, Name = ammo, Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = count, MinNm = 3.5, MaxNm = 65, SpeedKn = 540, CruiseAltFt = 50, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true, WarheadPower = 45 });
            StrikeAirMath.Classify(new[] { a });
            Air.Add(a);
            return a;
        }

        /// <summary>A jammer aircraft (phase D): no missiles, a station Universal with the bands of the ALQ-99.</summary>
        public StrikeAircraft AddEw(int uid, double lat, double lon, double cone = 30, string type = "usn_ea-6b", bool airborne = true)
        {
            var a = new StrikeAircraft { Uid = uid, Type = type, Name = type + "-" + uid, ObjType = "Aircraft", Airborne = airborne, Lat = lat, Lon = lon, HeadingDeg = 0, SpeedKn = 450, CruiseKn = 450, CruiseAltFt = 25000, AltFt = 25000, FuelFrac = 1, EnduranceS = 18000 };
            a.Ew = new StrikeEw { RangeNm = 200, ConeDeg = cone, Bands = new[] { "VHF", "UHF", "L", "S", "C", "X" }, Type = "Universal", StationName = "AN/ALQ-99", Stations = 1 };
            StrikeAirMath.Classify(new[] { a });
            Air.Add(a);
            return a;
        }

        public StrikeShip AddKidd(int id, double lat = 41, double lon = 10, string side = "hostile", bool identified = true, bool civilian = false, string role = "AAW")
        {
            var s = new StrikeShip
            {
                TrackId = id, Lat = lat, Lon = lon, CourseDeg = 90, SpeedKn = 15, DisplacementT = 8300, SamMaxNm = 90, RadarFt = 100, Side = side, Identified = identified, Hostile = side == "hostile",
                Civilian = civilian, Role = role, ClassName = "Kidd", ClassIni = "usn_ddg_kidd", KillBudget = 318.798, Ad = new StrikeAd { Ksub = 8.86082554, Ssub = 27.714283, Ksup = 5.15813732, Ssup = 12.2857141, Source = "cache" },
            };
            Fleet.Add(s);
            return s;
        }
    }
}
