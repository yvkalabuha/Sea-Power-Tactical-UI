using System;
using AgTrack = VoiceBotBridge.AirTrack;   // GameApi has a private nested AirTrack (Common/Aviation.cs)
using System.Collections;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075: the game side of the air threat service. Main thread, through GameApi helpers and the descriptors of AirGuardSpecs.
    // Reads: the player's plotting table (ADR-0038), own units (position, heading, speed, ThreateningMissile), the encyclopedia of the CLASS of an
    // identified track (largest AAW range over the loadouts), the own ships' armament as the ring layer reads it (ADR-0055). Never the true state of a foreign unit.
    internal sealed partial class GameApi
    {
        internal AirGuardController AirGuard;
        internal AirGuardParams AgParams = new AirGuardParams();
        internal AirOwnership AgOwnership { get { var a = Asw; var c = Cap; return a != null && a.Ownership != null ? a.Ownership : (c != null ? c.Ownership : null); } }
        private AgGameImpl _agImpl;

        internal AirGuardController CreateAirGuardController(AirGuardParams p)
        {
            _agImpl = new AgGameImpl(this);
            AgParams = p;
            AirGuard = new AirGuardController(_agImpl, p);
            return AirGuard;
        }

        /// <summary>An event line of the air protection (air_tc ...) for the modules outside GameApi.</summary>
        internal void AgEvent(string name, string dataJson) { try { EvLine(name, dataJson); } catch { } }

        /// <summary>One step of the service (1 s of game time), called by the air tick before the ASW and CAP controllers.</summary>
        internal void AirTickGuard(double t)
        {
            var c = AirGuard;
            if (c == null || _agImpl == null || !Frame.InMission) return;
            c.Tick(t);
        }

        /// <summary>Largest AAW missile range per loadout of an identified class (entries with the same IniRef, every variant); null = no data (entry or weapons missing).</summary>
        private readonly Dictionary<string, List<double>> _agClassAaw = new Dictionary<string, List<double>>();

        internal List<double> AgClassAawNm(string iniName)
        {
            if (string.IsNullOrEmpty(iniName)) return null;
            List<double> cached;
            if (_agClassAaw.TryGetValue(iniName, out cached)) return cached;
            try
            {
                var enc = RrEncyclopediaReadyObject();
                var entries = enc == null ? null : Get(enc, RangeRingSpecs.EncEntries) as IEnumerable;
                if (entries == null) return null;   // not cached: the encyclopedia is not ready yet
                var res = new List<double>();
                bool any = false, anyWeapons = false;
                foreach (var e in entries)
                {
                    if (e == null || (Get(e, RangeRingSpecs.EntryIniRef) as string) != iniName) continue;
                    any = true;
                    double best = 0;
                    var ws = Get(e, RangeRingSpecs.EntryWeapons) as IEnumerable;
                    if (ws != null)
                        foreach (var w in ws)
                        {
                            if (w == null) continue;
                            anyWeapons = true;
                            if (RrAmmoType(Get(w, RangeRingSpecs.WeaponType)) != RingAmmoType.Missile) continue;
                            if (RrTarget(Get(w, RangeRingSpecs.WeaponTargetType)) != RingTarget.Aaw && RrTarget(Get(w, RangeRingSpecs.WeaponSecondaryTargetType)) != RingTarget.Aaw) continue;
                            best = Math.Max(best, RrNm(this, Get(w, RangeRingSpecs.WeaponMaxRange)));
                        }
                    res.Add(best);
                }
                var r = any && anyWeapons ? res : null;
                _agClassAaw[iniName] = r;
                return r;
            }
            catch { return null; }
        }

        private object RrEncyclopediaReadyObject() { return RrEncyclopediaReady() ? SingletonInstance(RangeRingSpecs.EncyclopediaType) : null; }

        /// <summary>The same list CapGame.AirContacts always returned (live tracks, Air / Helicopter / Missile, not friendly / neutral, not gone) plus course, speed and "unknown".</summary>
        internal IList<CapContact> AgAirContacts()
        {
            var res = new List<CapContact>();
            try
            {
                var snap = Snapshot(true);
                foreach (var t in snap.Tracks)
                {
                    if (!t.HasId || !t.Lat.HasValue || !t.Lon.HasValue || !t.Live) continue;   // live tracks only: a stale hostile track must not hold the threat / postponement
                    string k = t.Kind ?? "";
                    bool missile = k == "Missile";
                    if (!missile && k != "Air" && k != "Helicopter") continue;
                    if (t.Side == "friendly" || t.Side == "neutral") continue;
                    object v;
                    if (snap.VehicleById.TryGetValue(t.Id, out v) && IsGone(v)) continue;
                    // an unknown-side missile is a threat for the player as well (his own are friendly); an unknown aircraft is not
                    bool hostile = t.Side == "hostile" || (missile && t.Side == "unknown");
                    double course, kn;
                    AgVelocity(t, out course, out kn);
                    res.Add(new CapContact { Id = t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, Hostile = hostile, Missile = missile, CourseDeg = course, SpeedKn = kn, Unknown = t.Side == "unknown", Helo = k == "Helicopter" });
                }
            }
            catch { }
            return res;
        }

        private static void AgVelocity(OvTrack t, out double courseDeg, out double kn)
        {
            courseDeg = 0; kn = 0;
            if (!t.Vx.HasValue || !t.Vz.HasValue) return;
            kn = Math.Sqrt(t.Vx.Value * t.Vx.Value + t.Vz.Value * t.Vz.Value) * OverviewMath.KnPerUnity;
            courseDeg = kn < 1e-6 ? 0 : (Math.Atan2(t.Vx.Value, t.Vz.Value) * 180.0 / Math.PI + 360.0) % 360.0;
        }

        private sealed class AgGameImpl : IAirGuardGame
        {
            private readonly GameApi _a;
            public AgGameImpl(GameApi a) { _a = a; }
            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }
            private object Get(object o, string n) { return _a.Get(o, n); }

            public double Now { get { var t = _a.Frame.MissionTimeS; return t.HasValue ? t.Value : 0; } }

            public IList<AgTrack> AirTracks()
            {
                var res = new List<AgTrack>();
                try
                {
                    var snap = _a.Snapshot(true);
                    double now = Now;
                    foreach (var t in snap.Tracks)
                    {
                        if (!t.HasId || !t.Lat.HasValue || !t.Lon.HasValue || !t.Live) continue;
                        string k = t.Kind ?? "";
                        if (k != "Air" && k != "Helicopter") continue;
                        if (t.Side == "friendly" || t.Side == "neutral") continue;
                        object v;
                        if (snap.VehicleById.TryGetValue(t.Id, out v) && _a.IsGone(v)) continue;
                        double course, kn;
                        AgVelocity(t, out course, out kn);
                        var tr = new AgTrack { Id = t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, CourseDeg = course, SpeedKn = kn, LastSeenS = now, Hostile = t.Side == "hostile", Unknown = t.Side == "unknown", Helicopter = k == "Helicopter" };
                        tr.Globe = _a.WorldGlobeAt(tr.Lat, tr.Lon);
                        string ini = _a.RrClassIniName(t.Id);
                        if (ini != null) tr.Cls = t.ClassName ?? ini;
                        string src;
                        tr.RNm = AirGuardMath.ThreatRadiusNm(ini != null, ini == null ? null : _a.AgClassAawNm(ini), _a.AgParams, out src);
                        tr.RSrc = src;
                        res.Add(tr);
                    }
                }
                catch { }
                return res;
            }

            public IList<AirOwn> OwnAirborne()
            {
                var res = new List<AirOwn>();
                try
                {
                    var own = _a.AgOwnership;
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!_a.IsAirUnit(u) || !IsAlive(u) || !_a.InFlight(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) continue;
                        double lat, lon, h;
                        if (!_a.OwnPos(u, out lat, out lon, out h)) continue;
                        int uid = _a.UidOf(u);
                        var o = new AirOwn { Uid = uid, Name = _a.NameOf(u), TypeLabel = Get(u, CapSpecs.ObjClassName) as string, Helicopter = _a.IsHelicopter(u), Lat = lat, Lon = lon, AltM = h };
                        o.CourseDeg = D(_a.RxGet(u, EvasionSpecs.ObjHeading)) ?? 0;
                        try { o.SpeedKn = Math.Abs(D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0); } catch { }
                        if (_a.Present(AirEvasionSpecs.ObjThreateningMissile))
                        {
                            var m = Get(u, AirEvasionSpecs.ObjThreateningMissile);
                            if (m != null && IsAlive(m)) o.MissileId = _a.UidOf(m);
                        }
                        string win;
                        if (own != null) { o.Group = own.Owner(uid, out win); if (o.Group != null) o.Window = win; }
                        res.Add(o);
                    }
                }
                catch { }
                return res;
            }

            public IList<AirSam> OwnSams()
            {
                var res = new List<AirSam>();
                try
                {
                    var tf = _a.GetStatic(RangeRingSpecs.GlobalsPlayerTaskforce);
                    var list = tf == null ? null : Get(tf, RangeRingSpecs.TfVessels) as IEnumerable;
                    if (list == null) return res;
                    foreach (var u in list)
                    {
                        if (u == null || !IsAlive(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) continue;
                        int uid = _a.UidOf(u);
                        double lat, lon, h;
                        if (!_a.OwnPos(u, out lat, out lon, out h)) continue;
                        double r = 0;
                        var arm = _a.RrOwn(uid, false);
                        if (arm != null)
                            foreach (var am in arm.Ammo)
                            {
                                if (am.Type != RingAmmoType.Missile || (am.Primary != RingTarget.Aaw && am.Secondary != RingTarget.Aaw) || am.Left == 0 || double.IsNaN(am.MaxNm)) continue;
                                r = Math.Max(r, am.MaxNm);
                            }
                        bool deck = false;
                        try { deck = _a.DeckOf(u) != null && !_a.AirOps.DeckInoperable(uid); } catch { }
                        if (r <= 0 && !deck) continue;
                        bool radarOff = false;
                        try { if (_a.Present(AirGuardSpecs.ObjAirRadarsOn)) { var on = _a.RxGet(u, AirGuardSpecs.ObjAirRadarsOn); radarOff = on is bool && !(bool)on; } } catch { }
                        res.Add(new AirSam { Uid = uid, Name = _a.NameOf(u), Lat = lat, Lon = lon, RSamNm = r, RadarOff = radarOff, Deck = deck, HeloDeck = deck });
                    }
                }
                catch { }
                return res;
            }

            public bool TrackDestroyed(int trackId) { try { return _a.RrTrackGone(trackId) == "destroyed"; } catch { return false; } }

            public GeoPt? UnitPos(int uid)
            {
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    double lat, lon, h;
                    if (u != null && IsAlive(u) && _a.OwnPos(u, out lat, out lon, out h)) return new GeoPt(lat, lon);
                }
                catch { }
                return null;
            }

            public int Aboard(int uid)
            {
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    var obp = u == null ? null : Get(u, AirOpsSpecs.ObjObp);
                    return obp == null ? 0 : (int)(D(_a.RxGet(obp, AirOpsSpecs.ObpTransported)) ?? 0);
                }
                catch { return 0; }
            }

            public void Event(string name, string dataJson) { try { _a.EvLine(name, dataJson); } catch { } }

            public void Log(string line)
            {
                try { var s = _a.AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
            }
        }

        // ---------- commands ----------

        /// <summary>air_guard_ignore {group, window?} | air_guard_state. Main thread. Null = not ours.</summary>
        private string AirGuardCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "air_guard_ignore" && name != "air_guard_state") return null;
            var c = AirGuard;
            if (c == null) return Err(id, "unsupported", "air_guard is off");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            if (name == "air_guard_state") return Ok(id, "\"air_guard\":" + c.StateJson());
            object g, w;
            string group = cmd != null && cmd.TryGetValue("group", out g) ? g as string : null;
            if (string.IsNullOrEmpty(group)) return Err(id, "bad_args", "group is required");
            string window = cmd.TryGetValue("window", out w) ? w as string : null;
            c.Ignore(window ?? "asw", group, Frame.MissionTimeS ?? 0);
            return Ok(id, "\"group\":" + Json.Str(group));
        }
    }
}
