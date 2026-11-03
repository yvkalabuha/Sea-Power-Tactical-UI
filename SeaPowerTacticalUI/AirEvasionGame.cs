using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-100 / ADR-0060: the game side of the aircraft evasion. Main thread, through GameApi helpers and the descriptors of AirEvasionSpecs.
    // Orders: the forced native state, a speed step, an altitude preset, a waypoint, one chaff salvo. Never fire control, never the rules of engagement.
    // Reads: only the own unit, the missile that the game itself declared as threatening, and the PLAYER'S plotting table for hostile air (ADR-0038).
    internal sealed partial class GameApi
    {
        internal Ui.AirEvasionController AirEvasion;
        internal bool AirEvadeEnabled = true;   // [AirEvade] Enabled (the module sets it in Bind; AirEvasionSpecs.Derive reads it)

        private AirEvasionGameImpl _aeImpl;

        internal Ui.AirEvasionController CreateAirEvasionController(Ui.AirEvasionParams p)
        {
            _aeImpl = new AirEvasionGameImpl(this);
            AirEvasion = new Ui.AirEvasionController(_aeImpl, p);
            return AirEvasion;
        }

        // ---------- after-launch turn (Common/Tactics.cs stays untouched: its _plRuns is visible to this partial) ----------

        private bool AeRouteIsPlaced(object air, double[] placed)
        {
            var wps = Waypoints(air);
            return placed != null && wps.Count > 0 && WorldDistanceNm(wps[0][0], wps[0][1], placed[0], placed[1]) <= 0.2;
        }

        private void AeRestoreRoute(object air, List<double[]> route)
        {
            if (route == null) return;
            if (route.Count == 0) { Call(air, AirOpsSpecs.ObjRemoveWaypoints); return; }
            for (int i = 0; i < route.Count; i++) PlaceWaypoint("0", air, route[i][0], route[i][1], i > 0, "");
        }

        /// <summary>The module takes over: the after-launch turn of this aircraft ends as "superseded". Returns the player's route from before that turn (null = there was none).</summary>
        internal List<double[]> PostLaunchSupersede(int uid, bool restoreRoute)
        {
            try
            {
                var air = FindOwnUnit(uid);
                PlRun run;
                if (air == null || !_plRuns.TryGetValue(air, out run)) return null;
                _plRuns.Remove(air);
                EvLine("post_launch", PlEvent(air, run.Mode, "end", "superseded", run.Guidance, run.TargetTrack, null, null));
                var saved = run.Saved != null ? new List<double[]>(run.Saved) : new List<double[]>();
                if (restoreRoute && AeRouteIsPlaced(air, run.Placed)) AeRestoreRoute(air, saved);
                return saved;
            }
            catch { return null; }
        }

        /// <summary>Every frame, after TacticsFeature: an after-launch turn that started while the aircraft evades is dropped at once ("skip_evading").</summary>
        internal void PostLaunchGuard()
        {
            var ctl = AirEvasion;
            if (ctl == null || _plRuns.Count == 0) return;
            foreach (var air in new List<object>(_plRuns.Keys))
            {
                try
                {
                    int uid = UidOf(air);
                    if (!ctl.Holds(uid)) continue;
                    var run = _plRuns[air];
                    _plRuns.Remove(air);
                    EvLine("post_launch", PlEvent(air, run.Mode, "skip", "skip_evading", run.Guidance, run.TargetTrack, null, null));
                    if (ctl.ModeOf(uid) == "native")
                    {
                        if (AeRouteIsPlaced(air, run.Placed)) AeRestoreRoute(air, run.Saved);
                    }
                    else ctl.RequestReplace(uid);
                }
                catch { }
            }
        }

        // ---------- commands ----------

        /// <summary>The ring / menu item: {"ok":true,"result":"ok","mode":..} | {"ok":true,"result":"noop","not_done":["air_&lt;reason&gt;"]} | {"ok":false,"error":"unsupported"}.</summary>
        internal string AirEvadeFromMenu(object unit)
        {
            var c = AirEvasion;
            if (c == null || !Capabilities.Contains("air_evasion")) return "{\"ok\":false,\"error\":\"unsupported\"}";
            string mode;
            string why = c.Button(UidOf(unit), AeNow(), out mode);
            Log("menu evade_attack uid=" + UidOf(unit) + " -> " + (why ?? mode));
            if (why == null) return "{\"ok\":true,\"result\":\"ok\",\"mode\":" + Json.Str(mode) + "}";
            return "{\"ok\":true,\"result\":\"noop\",\"not_done\":[" + Json.Str("air_" + why) + "]}";
        }

        private double AeNow()
        {
            var t = Frame.MissionTimeS;
            return t.HasValue ? t.Value : 0;
        }

        /// <summary>air_evade {uid} | air_evade_status {uid?}. Main thread. Null = not ours.</summary>
        private string AirEvadeCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "air_evade" && name != "air_evade_status") return null;
            var c = AirEvasion;
            if (c == null || !Capabilities.Contains("air_evasion")) return Err(id, "unsupported", "air_evasion capability is off");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            object o;
            double d = 0;
            bool hasUid = cmd != null && cmd.TryGetValue("uid", out o) && o is double && (d = (double)o) > 0 && d == Math.Floor(d) && d < int.MaxValue;
            if (name == "air_evade")
            {
                if (!hasUid) return Err(id, "bad_args", "uid is required");
                string mode;
                string why = c.Button((int)d, AeNow(), out mode);
                if (why != null) return Err(id, "skipped", why);
                return Ok(id, "\"uid\":" + (int)d + ",\"mode\":" + Json.Str(mode));
            }
            return Ok(id, "\"air_evasion\":" + c.DiagJson(32));
        }

        // ---------- the game adapter ----------

        private sealed class AirEvasionGameImpl : Ui.IAirEvasionGame
        {
            private readonly GameApi _a;
            public AirEvasionGameImpl(GameApi a) { _a = a; }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }

            private static bool B(object o) { return o is bool && (bool)o; }

            private static double? D(object o) { return GameApi.D(o); }

            private object Unit(int uid) { return _a.FindOwnUnit(uid); }

            private static bool Alive(object o) { return IsAlive(o); }

            // ---------- reads ----------

            public Ui.AeUnit Own(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return null;
                    var r = new Ui.AeUnit { Uid = uid, Alive = Alive(u) && !B(Get(u, AirOpsSpecs.ObjIsDestroyed)) };
                    r.IsAircraft = _a.IsAirUnit(u) && !_a.IsHelicopter(u);
                    if (!r.Alive || !r.IsAircraft) return r;
                    r.InFlight = B(Get(u, CapSpecs.AircraftInFlight));
                    var ap = Get(u, CapSpecs.AircraftAp);
                    r.IsLarge = B(Get(ap, AirEvasionSpecs.ApIsLarge));
                    var speeds = Get(ap, AirEvasionSpecs.ApSpeedValuesInMach) as Array;
                    r.MaxTelegraph = speeds != null ? speeds.Length : 0;
                    r.AttackRun = B(Get(u, AirEvasionSpecs.AcInLevelAttack)) || B(Get(u, AirEvasionSpecs.AcInDiveAttack));
                    r.PlayerControl = B(Get(u, AirEvasionSpecs.ObjPlayerCommandOverride));
                    double lat, lon, h;
                    if (_a.OwnPos(u, out lat, out lon, out h)) { r.Lat = lat; r.Lon = lon; r.Globe = _a.WorldGlobeAt(lat, lon); }
                    r.HeadingDeg = _a.GridHeadingTrue(u, EvasionSpecs.ObjHeading) ?? 0;
                    r.Kn = Math.Abs(D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0);
                    r.Telegraph = Step(u);
                    r.Roe = _a.WeaponStatusName(u);
                    r.StateName = Get(Get(u, CapSpecs.ObjStateMachine), CapSpecs.MachineStateName) as string;
                    r.ChaffReady = ChaffReady(u);
                    return r;
                }
                catch { return null; }
            }

            private int Step(object u)
            {
                var t = D(_a.Call(u, CapSpecs.ObjGetTelegraph));   // getTelegraph() = step - 1
                return t.HasValue ? (int)t.Value + 1 : 0;
            }

            private bool ChaffReady(object u)
            {
                try
                {
                    if (!_a.Present(AirEvasionSpecs.ObjLaunchChaff) || !_a.Present(AirEvasionSpecs.ChaffReadyToLaunch)) return false;
                    var list = Get(Get(u, AirOpsSpecs.ObjObp), AirEvasionSpecs.ObpChaffWeapons) as IEnumerable;
                    if (list == null) return false;
                    foreach (var w in list) if (w != null && B(_a.Call(w, AirEvasionSpecs.ChaffReadyToLaunch))) return true;
                }
                catch { }
                return false;
            }

            public Ui.AeMissile Missile(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return null;
                    var m = Get(u, AirEvasionSpecs.ObjThreateningMissile);
                    if (m == null || !Alive(m)) return null;
                    var r = new Ui.AeMissile { Key = "m" + _a.UidOf(m) };
                    double lat, lon, h;
                    bool pos = false;
                    int? tid = _a.TrackIdOfObject(m);   // the player's own track of it first (ADR-0038), the declared object as the fallback
                    if (tid.HasValue)
                    {
                        var v = _a.FindContact(tid.Value);
                        var tp = v != null ? _a.TrackPos(v) : null;
                        if (tp != null) { r.Lat = tp[0]; r.Lon = tp[1]; pos = true; }
                    }
                    if (!pos)
                    {
                        if (!_a.OwnPos(m, out lat, out lon, out h)) return null;
                        r.Lat = lat; r.Lon = lon;
                    }
                    r.Kn = Math.Abs(D(_a.Call(m, AirOpsSpecs.ObjVelocityKn)) ?? 0);
                    r.CourseDeg = _a.GridHeadingTrue(m, EvasionSpecs.ObjHeading) ?? 0;
                    r.TargetsUnit = ReferenceEquals(Get(m, AirEvasionSpecs.WeaponCurrentTarget), u);
                    r.Lost = B(Get(m, AirEvasionSpecs.WeaponTargetLost)) || B(_a.RxGet(m, AirEvasionSpecs.WeaponConnectionLost)) || B(Get(m, AirOpsSpecs.ObjIsDestroyed));
                    var ign = Get(u, AirEvasionSpecs.AcIgnoredMissiles) as IEnumerable;
                    if (ign != null) foreach (var x in ign) if (ReferenceEquals(x, m)) { r.IgnoredByGame = true; break; }
                    return r;
                }
                catch { return null; }
            }

            public Ui.AeThreat NearestHostileAir(int uid, double maxNm)
            {
                try
                {
                    var u = Unit(uid);
                    double lat, lon, h;
                    if (u == null || !_a.OwnPos(u, out lat, out lon, out h)) return null;
                    var vehicles = _a.Vehicles();
                    if (vehicles == null) return null;
                    Ui.AeThreat best = null;
                    foreach (var v in vehicles)
                    {
                        if (_a.Relation(v) != "hostile" || _a.Kind(v) != "Air" || _a.IsGone(v)) continue;
                        var p = _a.TrackPos(v);
                        if (p == null) continue;
                        double d = WorldDistanceNm(lat, lon, p[0], p[1]);
                        if (d > maxNm || (best != null && d >= best.RangeNm)) continue;
                        var id = D(Get(v, CoreSpecs.VehId));
                        best = new Ui.AeThreat { TrackId = id.HasValue ? (int)id.Value : -1, RangeNm = d, BearingDeg = WorldBearing(lat, lon, p[0], p[1]) };
                    }
                    return best;
                }
                catch { return null; }
            }

            public IList<int> AutoUnits()
            {
                var res = new List<int>();
                try
                {
                    var store = _a.FlagStore;
                    if (store == null) return res;
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!_a.IsAirUnit(u) || _a.IsHelicopter(u) || !Alive(u) || !B(Get(u, CapSpecs.AircraftInFlight))) continue;
                        if (store.Resolve(u, VbKeys.AutoEvadeAir)) res.Add(_a.UidOf(u));
                    }
                }
                catch { }
                return res;
            }

            public bool NativeOk { get { return _a.Capabilities.Contains("air_evasion_native"); } }

            public bool? OptionEvasionOn
            {
                get
                {
                    try
                    {
                        if (!_a.Present(AirEvasionSpecs.OptionsAircraftMissileEvasion)) return null;
                        var v = Get(_a.SingletonInstance(AirEvasionSpecs.OptionsType), AirEvasionSpecs.OptionsAircraftMissileEvasion);
                        return v is bool ? (bool?)(bool)v : null;
                    }
                    catch { return null; }
                }
            }

            public bool RouteChangedByOthers(int uid, double[] placed)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || placed == null) return false;
                    if (_a.Waypoints(u).Count == 0) return false;   // the leg end was reached (the game drops it): not an order of the player
                    return !_a.AeRouteIsPlaced(u, placed);
                }
                catch { return false; }
            }

            // ---------- orders ----------

            public bool ForceNative(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    var m = Get(u, AirEvasionSpecs.ObjThreateningMissile);   // the native state needs a missile: without one it ends at once
                    if (m == null || !Alive(m)) return false;
                    var sm = Get(u, AirEvasionSpecs.ObjStateMachine);
                    var st = Get(u, AirEvasionSpecs.AcStateAdvEvasion);
                    if (sm == null || st == null) return false;
                    _a.Call(sm, AirEvasionSpecs.SmSetState, st);
                    return true;
                }
                catch { return false; }
            }

            public bool FinishNative(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !_a.Present(AirEvasionSpecs.StateIsFinished)) return false;
                    var st = Get(u, AirEvasionSpecs.AcStateAdvEvasion);
                    return st != null && _a.Set(st, AirEvasionSpecs.StateIsFinished, true);
                }
                catch { return false; }
            }

            public bool SetTelegraph(int uid, int step)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || step <= 0) return false;
                    _a.Call(u, AirOpsSpecs.ObjSetTelegraph, step);
                    return true;
                }
                catch { return false; }
            }

            public bool SetAltPreset(int uid, int preset)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    return _a.SetPresetHeight(u, preset, true) == null;   // T-180: the helper picks Aircraft or Helicopter
                }
                catch { return false; }
            }

            public double[] PlaceCourse(int uid, double courseDeg, double legNm)
            {
                try
                {
                    var u = Unit(uid);
                    double lat0, lon0, h, lat, lon;
                    if (u == null || !_a.OwnPos(u, out lat0, out lon0, out h)) return null;
                    WorldDestination(lat0, lon0, courseDeg, legNm, out lat, out lon);
                    string ack = _a.PlaceWaypoint("0", u, lat, lon, false, "");
                    return ack != null && ack.Contains("\"ok\":true") ? new[] { lat, lon } : null;
                }
                catch { return null; }
            }

            public bool LaunchChaff(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !ChaffReady(u)) return false;
                    _a.Call(u, AirEvasionSpecs.ObjLaunchChaff, true);
                    return true;
                }
                catch { return false; }
            }

            public List<double[]> PostLaunchSupersede(int uid, bool restoreRoute) { return _a.PostLaunchSupersede(uid, restoreRoute); }

            // ---------- own memory of "Return control" (the common control memory does not keep altitude) ----------

            public Ui.AeSaved Remember(int uid, List<double[]> routeOverride)
            {
                var s = new Ui.AeSaved();
                try
                {
                    var u = Unit(uid);
                    if (u == null) return s;
                    s.Route = routeOverride != null ? new List<double[]>(routeOverride) : _a.Waypoints(u);
                    if (s.Route.Count == 0) s.CourseDeg = _a.DemandedCourse(u);
                    int st = Step(u);
                    if (st > 0) s.Telegraph = st;
                    s.AltUnits = D(_a.RxGet(u, CapSpecs.ObjDesiredAltitude));
                }
                catch { }
                return s;
            }

            public string Restore(int uid, Ui.AeSaved s)
            {
                string first = null;
                try
                {
                    var u = Unit(uid);
                    if (u == null || s == null) return "unit_not_found";
                    try
                    {
                        if (s.Route.Count > 0) _a.AeRestoreRoute(u, s.Route);
                        else if (s.CourseDeg.HasValue)
                        {
                            double lat0, lon0, h, lat, lon;
                            if (_a.OwnPos(u, out lat0, out lon0, out h))
                            {
                                WorldDestination(lat0, lon0, s.CourseDeg.Value, 75, out lat, out lon);
                                _a.PlaceWaypoint("0", u, lat, lon, false, "");
                            }
                        }
                        else _a.AeRestoreRoute(u, s.Route);
                    }
                    catch (Exception e) { first = first ?? "route:" + e.GetType().Name; }
                    try { if (s.Telegraph.HasValue) _a.Call(u, AirOpsSpecs.ObjSetTelegraph, s.Telegraph.Value); }
                    catch (Exception e) { first = first ?? "telegraph:" + e.GetType().Name; }
                    try
                    {
                        if (s.AltUnits.HasValue && s.AltUnits.Value > 0)
                        {
                            var ac = new Dictionary<string, object> { { "uid", (double)uid }, { "feet", Math.Round(s.AltUnits.Value * FeetPerUnit) } };
                            string ack = _a.SetAltitudeCmd("0", ac);
                            if (ack == null || !ack.Contains("\"ok\":true")) first = first ?? "altitude";
                        }
                    }
                    catch (Exception e) { first = first ?? "altitude:" + e.GetType().Name; }
                }
                catch (Exception e) { first = first ?? e.GetType().Name; }
                return first;
            }

            // ---------- text / journal ----------

            public void Hint(string key)
            {
                try
                {
                    string text = key == "option_off" ? UiText.Get("air_evade.hint.option_off") : key == "chaff_hold" ? UiText.Get("air_evade.hint.chaff_hold") : key;
                    if (!_a.RaiseGameAlert(text)) _a.Log("[AirEvade] hint " + key + ": " + text);
                }
                catch { }
            }

            public void Log(string line) { try { _a.Log(line); } catch { } }

            public void Event(string name, string json)
            {
                try { if (_a.EventsAvailable) _a.EvLine(name, json); } catch { }
            }
        }
    }
}
