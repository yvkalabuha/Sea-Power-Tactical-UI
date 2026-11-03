using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-138 / ADR-0054: the game side of the intercept course. Main thread, through GameApi helpers and the descriptors of InterceptSpecs.
    // Reads: the own unit (position, telegraph, waypoints, speed steps) and the PLAYER'S track of the target (Vehicle of the plotting table,
    // via CiReadTrack and BearingFan); never a position or a member of the real foreign object. Orders: goto + add_waypoint + set_telegraph only.
    internal sealed partial class GameApi
    {
        internal InterceptController Intercept;

        private InterceptGameAdapter _icAdapter;
        private TerrainHeightCache _icHeights;

        /// <summary>Creates the adapter and the controller. route = the shared queue of A* jobs (its heights come from <see cref="InterceptHeightM"/>).</summary>
        internal InterceptController CreateInterceptController(Ui.UnitSteering steering, IcParams p, Action<Action> background)
        {
            WireOwnership();   // idempotent: GetOwn (the own taskforce) answers only with the guard in place, also when contact_info is off
            _icAdapter = new InterceptGameAdapter(this);
            var route = new InterceptRoute(InterceptHeightM, background);
            Intercept = new InterceptController(_icAdapter, steering, route, p);
            return Intercept;
        }

        // ---------- commands (T-138 I2; movement only through InterceptController: goto / add_waypoint / set_telegraph, never attack or fire) ----------

        private static bool IcInt(Dictionary<string, object> c, string k, out int v, out bool present)
        {
            v = 0; present = false;
            object o;
            if (c == null || !c.TryGetValue(k, out o) || o == null) return true;
            present = true;
            if (!(o is double)) return false;
            double d = (double)o;
            if (d != Math.Floor(d) || Math.Abs(d) > int.MaxValue) return false;
            v = (int)d;
            return true;
        }

        /// <summary>intercept {uid, target_id} | intercept_cancel {uid} | intercept_status {uid?}. Main thread. Null = not ours.</summary>
        internal string InterceptCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "intercept" && name != "intercept_cancel" && name != "intercept_status") return null;
            var c = Intercept;
            if (c == null || !Capabilities.Contains("intercept")) return Err(id, "unsupported", "intercept capability is off");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            int uid, tid; bool hasUid, hasTid;
            if (!IcInt(cmd, "uid", out uid, out hasUid) || !IcInt(cmd, "target_id", out tid, out hasTid)) return Err(id, "bad_args", null);
            if (hasUid && uid <= 0) return Err(id, "bad_args", null);
            switch (name)
            {
                case "intercept":
                {
                    if (!hasUid || !hasTid) return Err(id, "bad_args", "uid and target_id are required");
                    string ack;
                    string code = c.Start(uid, tid, out ack);
                    return code != null ? Err(id, code, null) : SmartAck(id, ack);
                }
                case "intercept_cancel":
                {
                    if (!hasUid) return Err(id, "bad_args", "uid is required");
                    string r = c.Cancel(uid, "command");
                    return r != null ? Err(id, r, null) : SmartAck(id, "{\"result\":\"cancelled\"}");
                }
                default:
                    return SmartAck(id, c.StatusJson(hasUid ? uid : 0));
            }
        }

        /// <summary>T-130: the mission was left; the cached heights and the track objects belong to it.</summary>
        internal void InterceptLeaveMission()
        {
            if (_icHeights != null) _icHeights.Clear();
            _icHeights = null;
            if (_icAdapter != null) _icAdapter.ClearThreats();
        }

        private double InterceptHeightM(GeoPt g)
        {
            if (_icHeights == null)
            {
                var sampler = TerrainSampler(false);
                if (sampler == null) return TerrainHeightCache.OpenSeaM;   // no terrain data yet: open sea, tried again at the next call
                _icHeights = new TerrainHeightCache(sampler, null, m => Log("[Intercept] " + m));   // T-275: always logged, rate-limited
            }
            return _icHeights.HeightM(g);
        }

        /// <summary>Contact menu: the selected own ship / submarine and the player's track of the clicked foreign unit. False = no entry.</summary>
        internal bool InterceptMenuTarget(object targetObject, out int ownUid, out int trackId)
        {
            trackId = -1;
            ownUid = CiSelectedOwnUid();
            if (ownUid <= 0 || targetObject == null) return false;
            if (BoolOf(Get(targetObject, ContactInfoSpecs.ObjIsPlayer))) return false;   // an own unit has the XO menu, not a contact entry
            trackId = InterceptTrackOf(targetObject, ownUid);
            return trackId >= 0;
        }

        /// <summary>Contact menu: the object that was clicked -> Vehicle.Id of the player's track of it (the game resolves the track, we never read the
        /// object); -1 = the object is no unit or the player has no track of it. ownUid = the selected own unit (its taskforce is the player's).</summary>
        internal int InterceptTrackOf(object targetObject, int ownUid)
        {
            try
            {
                var own = FindOwnUnit(ownUid);
                if (own == null || targetObject == null) return -1;
                if (!BoolOf(Call(targetObject, InterceptSpecs.ObjIsUnit))) return -1;
                var tf = GetOwn(own, InterceptSpecs.ObjTaskforce);
                if (tf == null) return -1;
                var v = Call(targetObject, InterceptSpecs.ObjMapVehicle, tf);   // leak-ok: the game resolves the player's own track of the unit
                if (v == null) return -1;
                var id = D(Get(v, CoreSpecs.VehId));
                if (!id.HasValue) return -1;
                int k = (int)id.Value;
                _ciVehicles[k] = v;   // CiReadTrack reads from this cache; the menu may open before the contact_info snapshot knows the track
                return k;
            }
            catch { return -1; }
        }

        internal sealed class InterceptGameAdapter : IInterceptGame
        {
            private readonly GameApi _a;
            public InterceptGameAdapter(GameApi a) { _a = a; }

            public double NowS
            {
                get
                {
                    if (!_a.Frame.InMission) return double.NaN;
                    var t = _a.Frame.MissionTimeS;
                    return t.HasValue ? t.Value : double.NaN;
                }
            }

            private int _ownWhyUid; private string _ownWhy;

            /// <summary>Own() = null: the reason goes to the log once per change (not_surface alone said nothing in the live QA).</summary>
            private IcOwn OwnNo(int uid, string why)
            {
                if (_ownWhyUid != uid || _ownWhy != why) _a.Log("[Intercept] own uid=" + uid + " refused: " + why);
                _ownWhyUid = uid; _ownWhy = why;
                return null;
            }

            public IcOwn Own(int uid)
            {
                string stage = "find";
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return OwnNo(uid, "no_unit");
                    stage = "alive"; if (!IsAlive(u)) return OwnNo(uid, "dead");
                    stage = "kind"; if (_a.IsAirUnit(u)) return OwnNo(uid, "air:" + u.GetType().Name);
                    if (_a.UnitKindOfObj(u) != "ship") return OwnNo(uid, "kind:" + u.GetType().Name);
                    var o = new IcOwn { Uid = uid, Alive = true };
                    double lat, lon, h;
                    stage = "pos"; if (!_a.OwnPos(u, out lat, out lon, out h)) return OwnNo(uid, "no_pos");
                    o.Lat = lat; o.Lon = lon;
                    // the rest is not worth losing the unit over: a failed read keeps its default (a missing step table is refused later as no_steps)
                    stage = "speed"; try { o.SpeedKn = Math.Abs(D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0); } catch (Exception e) { _a.Log("[Intercept] own uid=" + uid + " speed read failed: " + e.GetType().Name); }
                    stage = "telegraph"; o.Telegraph = _a.Telegraph(u) ?? 0;
                    stage = "sub";
                    var subT = _a.TypeOf(SarSpecs.SubmarineType);
                    o.IsSub = subT != null && subT.IsInstanceOfType(u);
                    if (o.IsSub)
                    {
                        var sm = _a.RxGet(u, SarSpecs.SubIsSubmerged);
                        o.Submerged = !(sm is bool) || (bool)sm;
                    }
                    stage = "steps"; o.StepsKn = Steps(u, o.IsSub);
                    stage = "waypoints";
                    try { foreach (var w in _a.Waypoints(u)) o.Waypoints.Add(new GeoPt(w[0], w[1])); } catch (Exception e) { _a.Log("[Intercept] own uid=" + uid + " waypoints read failed: " + e.GetType().Name); }
                    _ownWhyUid = 0; _ownWhy = null;
                    return o;
                }
                catch (Exception e) { return OwnNo(uid, "error@" + stage + ":" + e.GetType().Name + ":" + e.Message); }
            }

            private double[] Steps(object u, bool sub)
            {
                var pars = sub ? _a.Get(u, InterceptSpecs.SubParams) : _a.Get(u, InterceptSpecs.VesselParams);
                var arr = (sub ? _a.Get(pars, InterceptSpecs.SubSteps) : _a.Get(pars, InterceptSpecs.VesselSteps)) as Array;
                if (arr == null || arr.Length == 0) return null;
                var res = new double[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    var d = D(arr.GetValue(i));
                    if (!d.HasValue) return null;
                    res[i] = d.Value;
                }
                return res;
            }

            public IcTrack Track(int trackId)
            {
                try
                {
                    object v;
                    if (!_a._ciVehicles.TryGetValue(trackId, out v) || v == null) return null;
                    var obj = _a.Get(v, CoreSpecs.VehObject);   // leak-ok: only "is it still there" (null / destroyed), no member is read
                    var ct = _a.CiReadTrack(trackId);
                    if (ct == null) return null;
                    var t = new IcTrack { Id = trackId, Kind = ct.Kind, Relation = _a.Relation(v), Live = ct.Live, Lat = ct.Lat, Lon = ct.Lon };
                    t.Gone = !IsAlive(obj) || BoolOf2(_a.Get(obj, AirOpsSpecs.ObjIsDestroyed));
                    t.BearingOnly = !ct.Live && _a.Get(v, InterceptSpecs.VehBearingFan) != null;
                    if (ct.HasVel) { t.HasVel = true; t.VxKn = ct.VxKn; t.VyKn = ct.VzKn; }
                    var now = _a.Frame.MissionTimeS;
                    t.AgeS = now.HasValue ? Ui.ContactInfoMath.Age(now.Value, ct.EpochS) : double.NaN;
                    if (double.IsNaN(t.AgeS) && ct.Live) t.AgeS = 0;
                    return t;
                }
                catch { return null; }
            }

            public double HeightM(GeoPt g) { return _a.InterceptHeightM(g); }

            private static string AckError(string ack)
            {
                try
                {
                    var d = Json.Parse(ack) as Dictionary<string, object>;
                    if (d == null) return "bad_ack";
                    object ok;
                    if (d.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return null;
                    object e;
                    return d.TryGetValue("error", out e) && e is string ? (string)e : "failed";
                }
                catch { return "bad_ack"; }
            }

            public string Route(int uid, IList<GeoPt> pts, bool first)
            {
                var u = _a.FindOwnUnit(uid);
                if (u == null) return "unit_not_found";
                if (pts == null || pts.Count == 0) return "bad_args";
                try
                {
                    _a.TacticsEnsureMission();
                    if (first) _a._ctlMem.RememberRoute(u, _a.PlayerWaypoints(u), _a.DemandedCourse(u));   // the player's route that "Return control" gives back
                    string err = AckError(_a.PlaceWaypoint("0", u, pts[0].Lat, pts[0].Lon, false, ""));   // goto: replaces the route
                    if (err != null) return err;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        err = AckError(_a.PlaceWaypoint("0", u, pts[i].Lat, pts[i].Lon, true, ""));   // add_waypoint
                        if (err != null) return err;
                    }
                    _a._ctlMem.NoteRoutePlaced(u, pts[0].Lat, pts[0].Lon);   // "Return control" compares the FIRST waypoint with this point
                    return null;
                }
                catch (Exception e) { return "waypoint_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public void NoteFirstWaypoint(int uid, GeoPt firstPt)
            {
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u != null) _a._ctlMem.NoteRoutePlaced(u, firstPt.Lat, firstPt.Lon);
                }
                catch { }
            }

            public string SetTelegraph(int uid, int telegraph)
            {
                var u = _a.FindOwnUnit(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    _a.TacticsEnsureMission();
                    _a.RememberFields(u, new[] { "telegraph" });   // the first value stays; "Return control" puts it back
                    string ack = _a.TelegraphCmd("0", new Dictionary<string, object> { { "uid", (double)uid }, { "telegraph", (double)telegraph } });
                    string err = AckError(ack);
                    if (err == null) _a.NoteFields(u, new[] { "telegraph" });
                    return err;
                }
                catch (Exception e) { return "telegraph_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public void Log(string line) { try { _a.Log(line); } catch { } }

            // ---------- T-169 / ADR-0067: enemy weapon zones (what the player knows: identified hostile tracks, class reference) ----------

            private sealed class ClassThreat { public string Name; public List<IcWeapon> Weapons = new List<IcWeapon>(); }

            private readonly Dictionary<string, ClassThreat> _icClasses = new Dictionary<string, ClassThreat>();   // ini name -> class (null = no entry)
            private List<IcThreat> _icThreats;
            private double _icThreatsAt = double.NaN;
            private bool _icThreatsSub, _icNoClassLogged;

            internal void ClearThreats() { _icClasses.Clear(); _icThreats = null; _icThreatsAt = double.NaN; }

            private static string MapKindOk(string kind)
            {
                return kind == null || kind == "Air" || kind == "Missile" || kind == "Torpedo" || kind == "Biologic" ? null : kind;
            }

            private double LenNm(object length)
            {
                var d = length == null ? null : D(_a.Get(length, RangeRingSpecs.LengthNm));
                return d.HasValue && !double.IsNaN(d.Value) ? d.Value : 0;
            }

            /// <summary>The class reference (encyclopedia entry IniRef == ini, VariantIndex 0: the class without a load-out), cached per mission.</summary>
            private ClassThreat ClassOf(string ini)
            {
                ClassThreat c;
                if (_icClasses.TryGetValue(ini, out c)) return c;
                var enc = _a.SingletonInstance(RangeRingSpecs.EncyclopediaType);
                if (enc == null || !BoolOf2(_a.Get(enc, RangeRingSpecs.EncUnitsLoaded))) return null;   // not built yet: asked again later, not cached
                var entries = _a.Get(enc, RangeRingSpecs.EncEntries) as IEnumerable;
                object entry = null;
                if (entries != null)
                    foreach (var e in entries)
                    {
                        if (e == null) continue;
                        if ((_a.Get(e, RangeRingSpecs.EntryIniRef) as string) == ini && D(_a.Get(e, RangeRingSpecs.EntryVariantIndex)) == 0) { entry = e; break; }
                    }
                if (entry != null)
                {
                    c = new ClassThreat { Name = _a.Get(entry, InterceptSpecs.EntryDisplayName) as string };
                    var ws = _a.Get(entry, RangeRingSpecs.EntryWeapons) as IEnumerable;
                    if (ws != null)
                        foreach (var w in ws)
                        {
                            if (w == null) continue;
                            var prim = _a.Get(w, RangeRingSpecs.WeaponTargetType);
                            var sec = _a.Get(w, RangeRingSpecs.WeaponSecondaryTargetType);
                            var typ = _a.Get(w, RangeRingSpecs.WeaponType);
                            c.Weapons.Add(new IcWeapon
                            {
                                Name = _a.Get(w, InterceptSpecs.EntryDisplayName) as string,
                                Primary = prim == null ? null : prim.ToString(),
                                Secondary = sec == null ? null : sec.ToString(),
                                HasSecondary = BoolOf2(_a.Get(w, RangeRingSpecs.WeaponHasSecondary)),
                                Type = typ == null ? null : typ.ToString(),
                                MaxNm = LenNm(_a.Get(w, RangeRingSpecs.WeaponMaxRange)),
                            });
                        }
                }
                _icClasses[ini] = c;
                return c;
            }

            public IList<IcThreat> Threats(bool submerged)
            {
                double now = NowS;
                if (_icThreats != null && _icThreatsSub == submerged && !double.IsNaN(now) && now >= _icThreatsAt && now - _icThreatsAt < InterceptThreatMath.CheckEveryS)
                    return _icThreats;
                var res = new List<IcThreat>();
                try
                {
                    if (!_a.Capabilities.Contains("range_rings_class"))
                    {
                        if (!_icNoClassLogged) _a.Log("[Intercept] zone check off: the class reference of the game is missing (range_rings_class)");
                        _icNoClassLogged = true;
                        return res;
                    }
                    var vehicles = _a.Vehicles();
                    if (vehicles == null) return res;
                    foreach (var v in vehicles)
                    {
                        try
                        {
                            if (v == null || MapKindOk(_a.Kind(v)) == null) continue;
                            if (_a.Relation(v) != "hostile") continue;
                            if (!BoolOf2(_a.RxGet(v, OverviewSpecs.VehIdentified))) continue;   // the class is known only after the identification
                            var id = D(_a.Get(v, OverviewSpecs.VehId));
                            if (!id.HasValue) continue;
                            var o = _a.Get(v, OverviewSpecs.VehObject);   // leak-ok: the class name of an identified track, as the range rings (ADR-0055)
                            if (o == null || !IsAlive(o)) continue;
                            var ini = _a.Call(o, OverviewSpecs.ObjIniName) as string;
                            if (string.IsNullOrEmpty(ini)) continue;
                            ClassThreat cls = ClassOf(ini);
                            if (cls == null) continue;
                            double range; string wname, wkind;
                            if (!InterceptThreatMath.Fold(cls.Weapons, submerged, out range, out wname, out wkind)) continue;
                            int k = (int)id.Value;
                            _a._ciVehicles[k] = v;   // CiReadTrack reads from this cache (the position and the velocity of the player's track)
                            var ct = _a.CiReadTrack(k);
                            if (ct == null) continue;
                            var t = new IcThreat { TrackId = k, Name = string.IsNullOrEmpty(cls.Name) ? ini : cls.Name, Weapon = wname, WeaponKind = wkind, RangeNm = range, Lat = ct.Lat, Lon = ct.Lon };
                            if (ct.HasVel) { t.HasVel = true; t.VxKn = ct.VxKn; t.VyKn = ct.VzKn; }
                            res.Add(t);
                        }
                        catch { }
                    }
                }
                catch (Exception e) { _a.Log("[Intercept] zone threats failed: " + e.GetType().Name); }
                _icThreats = res; _icThreatsAt = now; _icThreatsSub = submerged;
                return res;
            }

            public void Event(string name, string json)
            {
                try { if (_a.EventsAvailable) _a.EvLine(name, json); } catch { }
            }
        }
    }
}
