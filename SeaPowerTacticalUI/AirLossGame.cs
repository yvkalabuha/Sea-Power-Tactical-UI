using System;
using System.Collections.Generic;
using AgTrack = VoiceBotBridge.AirTrack;   // GameApi has a private nested AirTrack (Common/Aviation.cs)

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 item 6: the game side of the loss tracker. Main thread. Reads: own aircraft (the whole side of the player), IsDestroyed, the landing mark
    // (_spawnTime >= 1e300, GameApi.IsRecoveredLanding), height, fuel, the threatening missile; the hits on own aircraft come from the RecordWeaponHitUnit patch
    // (HitEstimatePatches, any shooter). Nothing of foreign units beyond what the player knows (the class of the shooter only from his own identified track).
    internal sealed partial class GameApi
    {
        internal AirLossTracker AirLoss;
        private AlGameImpl _alImpl;

        internal AirLossTracker CreateAirLossTracker()
        {
            _alImpl = new AlGameImpl(this);
            AirLoss = new AirLossTracker(_alImpl);
            return AirLoss;
        }

        /// <summary>The mission was left or changed: the cache of game objects of the tracker is dropped (no object outlives its mission).</summary>
        internal void AlLeaveMission() { if (_alImpl != null) _alImpl.ResetMission(); }

        /// <summary>One step of the tracker (1 s of game time), called by the air tick right after the threat service.</summary>
        internal void AirTickLoss(double t)
        {
            var c = AirLoss;
            if (c == null || _alImpl == null || !Frame.InMission) return;
            c.Tick(t);
        }

        /// <summary>Prefix-free body of the patch: a hit on an own aircraft by a weapon of any side. Never throws.</summary>
        internal void AlOnHit(object taskforce, object weapon, object hitObject)
        {
            try
            {
                var c = AirLoss;
                if (c == null || weapon == null || hitObject == null || !Frame.InMission) return;
                if (!RrIsOwnSide(hitObject, false) || !IsAirUnit(hitObject)) return;
                int uid = UidOf(hitObject);
                string name = null;
                var a = HeAmmoOf(Get(weapon, HitEstimateSpecs.WeaponAp));
                if (a != null) name = a.Name;
                string cls = null;
                var shooter = Get(weapon, HitEstimateSpecs.WeaponLaunchPlatform);
                if (shooter != null && !RrIsOwnSide(shooter, true))
                {
                    var player = GetStatic(CoreSpecs.PlayerTaskforce);
                    var veh = player == null ? null : Call(shooter, InterceptSpecs.ObjMapVehicle, player);   // the game resolves the player's own track of the shooter
                    var id = veh == null ? null : D(Get(veh, OverviewSpecs.VehId));
                    if (id.HasValue && RrIsIdentified((int)id.Value)) cls = RrClassIniName((int)id.Value) == null ? null : ClassDisplayOf((int)id.Value);
                }
                c.OnHit(uid, name, cls, Frame.MissionTimeS ?? 0);
            }
            catch { }
        }

        private string ClassDisplayOf(int trackId)
        {
            var snap = Snapshot(true);
            foreach (var t in snap.Tracks) if (t.Id == trackId) return t.ClassName ?? t.Class;
            return null;
        }

        private sealed class AlGameImpl : IAirLossGame
        {
            private sealed class Seen
            {
                public object Obj; public AirOwn Own; public double Fuel = -1, MissileAt = -1; public int Aboard; public string Roe;
            }

            private readonly GameApi _a;
            private readonly Dictionary<int, Seen> _seen = new Dictionary<int, Seen>();
            public AlGameImpl(GameApi a) { _a = a; }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }
            private object Get(object o, string n) { return _a.Get(o, n); }

            public void ResetMission()
            {
                foreach (var s in _seen.Values) s.Obj = null;
                _seen.Clear();
            }

            public double Now { get { var t = _a.Frame.MissionTimeS; return t.HasValue ? t.Value : 0; } }
            public double RealNow { get { return Environment.TickCount / 1000.0; } }

            private double Fuel(object u)
            {
                try
                {
                    var ap = Get(u, CapSpecs.AircraftAp);
                    var cur = D(Get(ap, CapSpecs.ObpCurrentFuel)); var max = D(Get(ap, CapSpecs.ObpMaxFuel));
                    if (cur.HasValue && max.HasValue && max.Value > 0 && !double.IsNaN(cur.Value)) return Math.Max(0, Math.Min(1, cur.Value / max.Value));
                }
                catch { }
                return -1;
            }

            public IList<AirOwn> OwnAirborne()
            {
                var res = new List<AirOwn>();
                try
                {
                    double now = Now;
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!_a.IsAirUnit(u) || !IsAlive(u) || !_a.InFlight(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) continue;
                        double lat, lon, h;
                        if (!_a.OwnPos(u, out lat, out lon, out h)) continue;
                        int uid = _a.UidOf(u);
                        var o = new AirOwn { Uid = uid, Name = _a.NameOf(u), TypeLabel = Get(u, CapSpecs.ObjClassName) as string, Helicopter = _a.IsHelicopter(u), Lat = lat, Lon = lon, AltM = h };
                        try { o.SpeedKn = Math.Abs(D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0); } catch { }
                        string win; var own = _a.AgOwnership;
                        if (own != null) { o.Group = own.Owner(uid, out win); if (o.Group != null) o.Window = win; }
                        Seen s;
                        if (!_seen.TryGetValue(uid, out s)) { s = new Seen(); _seen[uid] = s; }
                        s.Obj = u; s.Own = o; s.Fuel = Fuel(u);
                        try { var obp = Get(u, AirOpsSpecs.ObjObp); s.Aboard = obp == null ? 0 : (int)(D(_a.RxGet(obp, AirOpsSpecs.ObpTransported)) ?? 0); } catch { }
                        try { s.Roe = _a.WeaponStatusName(u); } catch { }
                        try
                        {
                            if (_a.Present(AirEvasionSpecs.ObjThreateningMissile))
                            {
                                var m = Get(u, AirEvasionSpecs.ObjThreateningMissile);
                                if (m != null && IsAlive(m)) s.MissileAt = now;
                            }
                        }
                        catch { }
                        res.Add(o);
                    }
                    // the cache keeps the objects of the aircraft that left the list until the tracker has judged them
                    if (_seen.Count > 400) { var keep = new HashSet<int>(); foreach (var o in res) keep.Add(o.Uid); foreach (int k in new List<int>(_seen.Keys)) if (!keep.Contains(k)) _seen.Remove(k); }
                }
                catch { }
                return res;
            }

            public AirLossFacts Facts(int uid)
            {
                var f = new AirLossFacts();
                Seen s;
                if (!_seen.TryGetValue(uid, out s) || s.Obj == null) return f;
                try
                {
                    var u = s.Obj;
                    bool gone = !IsAlive(u);
                    f.Destroyed = gone || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed));
                    f.Landed = _a.Present(AirLossSpecs.ObjSpawnTime) ? _a.IsRecoveredLanding(u) : true;   // without the landing mark nothing is reported as a loss (ADR-0075 item 10)
                    f.Alive = !f.Destroyed;
                    f.AltM = s.Own.AltM;
                    f.FuelFrac = s.Fuel;
                    if (s.MissileAt >= 0) f.MissileGoneAgoS = Math.Max(0, Now - s.MissileAt);
                }
                catch { }
                return f;
            }

            public int Aboard(int uid) { Seen s; return _seen.TryGetValue(uid, out s) ? s.Aboard : 0; }
            public string Roe(int uid) { Seen s; return _seen.TryGetValue(uid, out s) ? s.Roe : null; }

            public IList<string> NearHostiles(int uid, int max)
            {
                var res = new List<string>();
                Seen s;
                if (!_seen.TryGetValue(uid, out s) || s.Own == null) return res;
                try
                {
                    var ctl = _a.AirGuard;
                    if (ctl == null) return res;
                    var rows = new List<KeyValuePair<double, string>>();
                    foreach (var t in ctl.Tracks())
                    {
                        if (!t.Hostile) continue;
                        double d = AswMath.DistNm(new GeoPt(s.Own.Lat, s.Own.Lon), new GeoPt(t.Lat, t.Lon));
                        rows.Add(new KeyValuePair<double, string>(d, (t.Cls ?? "?") + "@" + d.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    rows.Sort((x, y) => x.Key.CompareTo(y.Key));
                    for (int i = 0; i < rows.Count && i < max; i++) res.Add(rows[i].Value);
                }
                catch { }
                return res;
            }

            public void Event(string name, string dataJson) { try { _a.EvLine(name, dataJson); } catch { } }

            public void Log(string line)
            {
                try { var sink = _a.AswLogSink; if (sink != null) sink(line); else UnityEngine.Debug.Log(line); } catch { }
            }
        }
    }
}
