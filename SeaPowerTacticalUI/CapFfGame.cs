// T-333 (ADR-0129 section 9) stream E2: the game side of the friendly-fire guard (IFfGame), GameApi partial, reflection only.
// Called from the Harmony prefix of ObjectBase.AddEngageTask on the game main thread: no method throws, every unclear read means "not ours" (fire stays allowed).
// Positions of other aircraft are the PLAYER'S TRACKS (RrMenuTrack, no true positions of foreign objects); own aircraft are read directly.
using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        /// <summary>The game side of the guard; one per mod instance (the caches live in it).</summary>
        internal IFfGame NewFfGame() { return new CapFfGameImpl(this); }
        /// <summary>The event line of the friendly-fire guard (EvLine is private to GameApi).</summary>
        internal void CapEvent(string name, string json) { try { EvLine(name, json); } catch { } }

        private sealed class CapFfGameImpl : IFfGame
        {
            private const double CacheS = 0.5, OthersNm = 60;
            private readonly GameApi _a;
            private readonly Dictionary<string, string> _seeker = new Dictionary<string, string>();
            private double _friendsAt = -1e9, _hostilesAt = -1e9;
            private List<FfFriend> _friends = new List<FfFriend>();
            private readonly List<KeyValuePair<int, GeoPt>> _hostiles = new List<KeyValuePair<int, GeoPt>>();

            public CapFfGameImpl(GameApi a) { _a = a; }

            public double Now { get { try { return _a.WatchNow(); } catch (Exception) { return 0; } } }

            private static bool IsAirType(object o)
            {
                for (var t = o == null ? null : o.GetType(); t != null; t = t.BaseType)
                    if (t.Name == "Aircraft" || t.Name == "Helicopter") return true;
                return false;
            }

            public bool ReadShot(object task, out FfShot shot)
            {
                shot = null;
                try
                {
                    if (task == null) return false;
                    var shooter = _a.Get(task, CapFfSpecs.TaskBase);
                    var target = _a.Get(task, CapFfSpecs.TaskTarget);
                    if (shooter == null || target == null || !IsAlive(shooter) || !IsAlive(target)) return false;
                    bool auto = BoolOf(_a.Get(task, CapFfSpecs.TaskAuto));
                    if (!auto) { shot = new FfShot { Shooter = _a.UidOf(shooter), Auto = false }; return true; }
                    if (!IsAirType(target)) { shot = new FfShot { Shooter = _a.UidOf(shooter), Auto = true, TargetAir = false }; return true; }
                    int track = _a.RrMenuTrack(target);
                    object tv;
                    if (track < 0 || !_a._rrVehicles.TryGetValue(track, out tv)) return false;   // no track of the player: allow
                    var tp = _a.TrackPos(tv);
                    double la, lo, h;
                    if (tp == null || !_a.OwnPos(shooter, out la, out lo, out h)) return false;
                    string ammo = _a.Get(task, CapFfSpecs.TaskAmmoId) as string;
                    string seeker = SeekerOfAmmo(ammo);
                    var s = new FfShot
                    {
                        Shooter = _a.UidOf(shooter), Auto = true, TargetAir = true, TargetKey = track,
                        From = new GeoPt(la, lo), To = new GeoPt(tp[0], tp[1]), Ammo = ammo, Seeker = seeker
                    };
                    if (seeker == "ir" || seeker == "arh") s.Others = OthersNear(s.From, track);
                    shot = s;
                    return true;
                }
                catch (Exception) { shot = null; return false; }
            }

            /// <summary>ir|arh|sarh|cmd|null by the ammunition ini (AmmunitionParameters via GetShared), cached per name.</summary>
            private string SeekerOfAmmo(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                string s;
                if (_seeker.TryGetValue(name, out s)) return s;
                try
                {
                    var ap = _a.CallStatic(CapSpecs.AmmoGetShared, name);
                    var g = ap == null ? null : _a.Get(ap, CapFfSpecs.AmmoGuidance);
                    var m = ap == null ? null : _a.Get(ap, CapFfSpecs.AmmoMidCourse);
                    s = CapFfMath.SeekerOf(g == null ? null : g.ToString(), m == null ? null : m.ToString());
                }
                catch (Exception) { s = null; }
                _seeker[name] = s;
                return s;
            }

            /// <summary>Q3: hostile air tracks (not the target) within OthersNm of the shooter; the track list is read at most every 0.5 s.</summary>
            private IList<GeoPt> OthersNear(GeoPt from, int targetTrack)
            {
                double now = Now;
                if (now - _hostilesAt >= CacheS || now < _hostilesAt)
                {
                    _hostilesAt = now; _hostiles.Clear();
                    var vehicles = _a.Vehicles();
                    if (vehicles != null)
                        foreach (var v in vehicles)
                        {
                            string k = _a.Kind(v);
                            if ((k != "Air" && k != "Helicopter") || _a.Relation(v) != "hostile" || _a.IsGone(v)) continue;
                            var p = _a.TrackPos(v); var id = D(_a.Get(v, CoreSpecs.VehId));
                            if (p == null || !id.HasValue) continue;
                            _hostiles.Add(new KeyValuePair<int, GeoPt>((int)id.Value, new GeoPt(p[0], p[1])));
                        }
                }
                var l = new List<GeoPt>();
                foreach (var h in _hostiles) if (h.Key != targetTrack && AswMath.DistNm(from, h.Value) <= OthersNm) l.Add(h.Value);
                return l;
            }

            public IList<FfFriend> Friends(double now)
            {
                if (now - _friendsAt < CacheS && now >= _friendsAt) return _friends;
                _friendsAt = now;
                var l = new List<FfFriend>();
                try
                {
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!IsAirType(u) || !IsAlive(u) || !_a.InFlight(u)) continue;
                        double la, lo, h;
                        if (_a.OwnPos(u, out la, out lo, out h)) l.Add(new FfFriend { Uid = _a.UidOf(u), Pos = new GeoPt(la, lo) });
                    }
                    var vehicles = _a.Vehicles();
                    if (vehicles != null)
                        foreach (var v in vehicles)
                        {
                            string k = _a.Kind(v);
                            if ((k != "Air" && k != "Helicopter") || _a.Relation(v) != "friendly" || _a.IsGone(v)) continue;
                            var p = _a.TrackPos(v); var id = D(_a.Get(v, CoreSpecs.VehId));
                            if (p != null && id.HasValue) l.Add(new FfFriend { Uid = -2 - (int)id.Value, Pos = new GeoPt(p[0], p[1]) });   // allied track: negative pseudo uid
                        }
                }
                catch (Exception) { }
                _friends = l;
                return l;
            }

            public IList<FfAlt> Alts(int shooterUid)
            {
                var l = new List<FfAlt>();
                try
                {
                    var u = _a.FindOwnUnit(shooterUid);
                    if (u == null) return l;
                    foreach (var r in _a.WeaponRows(u))
                    {
                        // [file, display, type, target, category, count, rangeNm, canEngage, ...]
                        if (r[2] != "Missile" || r[3] != "AAW" || r[7] != "true") continue;
                        double nm; int left;
                        if (!double.TryParse(r[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out nm)) continue;
                        if (!int.TryParse(r[5], out left) || left < 1) continue;
                        string seeker = SeekerOfAmmo(r[0]);
                        if (seeker == null) continue;
                        l.Add(new FfAlt { Ammo = r[0], Seeker = seeker, MaxNm = nm, Left = left });
                    }
                }
                catch (Exception) { }
                return l;
            }

            /// <summary>No guiding alignment: a missile already guided (command / SARH illumination) must not be exchanged under the AI.</summary>
            public bool CanSwap(object task)
            {
                try
                {
                    var shooter = _a.Get(task, CapFfSpecs.TaskBase);
                    if (shooter == null) return false;
                    if (!_a.Present(CapFfSpecs.TaskAmmunition) || !_a.Present(CapFfSpecs.TaskShots) || !_a.Present(CapFfSpecs.TaskAmmoId) || !_a.Present(CapFfSpecs.ObjGetAmmoByName)) return false;
                    var al = _a.Get(shooter, CapFfSpecs.ObjAlignTasks) as IEnumerable;
                    if (al != null) foreach (var t in al) if (t != null && BoolOf(_a.Get(t, CapFfSpecs.AlignGuiding))) return false;
                    return true;
                }
                catch (Exception) { return false; }
            }

            /// <summary>Both fields (_ammoId and _ammunition) and _shotsToFire together, or nothing (rolled back). null = ok.</summary>
            public string Swap(object task, string ammo)
            {
                try
                {
                    var shooter = _a.Get(task, CapFfSpecs.TaskBase);
                    if (shooter == null || string.IsNullOrEmpty(ammo)) return "no_shooter";
                    var am = _a.Call(shooter, CapFfSpecs.ObjGetAmmoByName, ammo);
                    if (am == null) return "no_ammo";
                    int left = 0;
                    foreach (var alt in Alts(_a.UidOf(shooter))) if (alt.Ammo == ammo) left = alt.Left;
                    if (left < 1) return "none_left";
                    var oldId = _a.Get(task, CapFfSpecs.TaskAmmoId); var oldAm = _a.Get(task, CapFfSpecs.TaskAmmunition); var oldShots = _a.Get(task, CapFfSpecs.TaskShots);
                    int shots = oldShots == null ? 1 : Math.Max(1, Math.Min(Convert.ToInt32(oldShots), left));
                    if (_a.Set(task, CapFfSpecs.TaskAmmunition, am) && _a.Set(task, CapFfSpecs.TaskAmmoId, ammo) && _a.Set(task, CapFfSpecs.TaskShots, shots)) return null;
                    _a.Set(task, CapFfSpecs.TaskAmmunition, oldAm); _a.Set(task, CapFfSpecs.TaskAmmoId, oldId); _a.Set(task, CapFfSpecs.TaskShots, oldShots);
                    return "set_failed";
                }
                catch (Exception e) { return "exception:" + e.GetType().Name; }
            }
        }
    }
}
