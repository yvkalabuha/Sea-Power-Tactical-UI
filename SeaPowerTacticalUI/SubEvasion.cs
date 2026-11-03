using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-101 / ADR-0071: the game side of the submarine torpedo evasion. Main thread, through GameApi helpers and the descriptors of SubEvasionSpecs.
    // Orders: the game's own state, a waypoint, a telegraph step, a depth preset, the cavitation limit, one noisemaker, time compression x1.
    // Never a weapon, never the rules of engagement, never a counter-launch. Reads: the own submarine, the torpedo that the game itself declared as
    // threatening, and the PLAYER'S plotting table (NearestKnownTorpedo): depth data of that torpedo is read only when the player knows it.
    internal sealed partial class GameApi
    {
        internal Ui.SubEvasionController SubEvasion;
        /// <summary>[AutoEvade] Sub* values (EvasionFeature.Bind writes them; the controller reads them at every tick).</summary>
        internal Ui.SubEvasionParams SubEvasionP = new Ui.SubEvasionParams();

        private SubEvasionGameImpl _seImpl;

        internal Ui.SubEvasionController CreateSubEvasionController()
        {
            _seImpl = new SubEvasionGameImpl(this);
            SubEvasion = new Ui.SubEvasionController(_seImpl, SubEvasionP);
            return SubEvasion;
        }

        private double SeNow()
        {
            var t = Frame.MissionTimeS;
            return t.HasValue ? t.Value : 0;
        }

        /// <summary>From AutoEvadeHook (about 2 Hz of real time) for one submarine; the controller paces itself (1 s of mission time).</summary>
        internal void SubEvasionTick(object u)
        {
            var c = SubEvasion;
            var t = Frame.MissionTimeS;
            if (c == null || !t.HasValue) return;
            c.Tick(UidOf(u), t.Value);
        }

        /// <summary>The ring item: {"ok":true,"result":"ok","detail":"started|armed"} | {"ok":true,"result":"noop","not_done":["sub_&lt;why&gt;"]} | {"ok":false,"error":"unsupported"}.</summary>
        internal string SubTorpedoAlertFromMenu(object unit)
        {
            var c = SubEvasion;
            if (c == null || !Capabilities.Contains("sub_evasion")) return "{\"ok\":false,\"error\":\"unsupported\"}";
            string r = c.Button(UidOf(unit), SeNow());
            Log("menu sub_torpedo_alert uid=" + UidOf(unit) + " -> " + r);
            if (r == "started" || r == "armed") return "{\"ok\":true,\"result\":\"ok\",\"detail\":" + Json.Str(r) + "}";
            string why = r.StartsWith("skipped:", StringComparison.Ordinal) ? r.Substring(8) : r;
            return "{\"ok\":true,\"result\":\"noop\",\"not_done\":[" + Json.Str("sub_" + why) + "]}";
        }

        /// <summary>sub_evade {uid} | sub_evade_status {uid?}. Main thread. Null = not ours.</summary>
        private string SubEvadeCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "sub_evade" && name != "sub_evade_status") return null;
            var c = SubEvasion;
            if (c == null || !Capabilities.Contains("sub_evasion")) return Err(id, "unsupported", "sub_evasion capability is off");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            object o;
            double d = 0;
            bool hasUid = cmd != null && cmd.TryGetValue("uid", out o) && o is double && (d = (double)o) > 0 && d == Math.Floor(d) && d < int.MaxValue;
            if (name == "sub_evade")
            {
                if (!hasUid) return Err(id, "bad_args", "uid is required");
                string r = c.Button((int)d, SeNow());
                if (r.StartsWith("skipped:", StringComparison.Ordinal)) return Err(id, "skipped", r.Substring(8));
                return Ok(id, "\"uid\":" + (int)d + ",\"result\":" + Json.Str(r));
            }
            return Ok(id, "\"sub_evasion\":" + c.DiagJson(32));
        }

        // ---------- the game adapter ----------

        private sealed class SubEvasionGameImpl : Ui.ISubEvasionGame
        {
            private readonly GameApi _a;
            public SubEvasionGameImpl(GameApi a) { _a = a; }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }

            private static bool B(object o) { return o is bool && (bool)o; }

            private static double? D(object o) { return GameApi.D(o); }

            private object Unit(int uid) { return uid < 0 ? null : _a.FindOwnUnit(uid); }

            

            // ---------- reads ----------

            public bool AutoEvadeOn(int uid)
            {
                try { var u = Unit(uid); return u != null && _a.ResolveFlag(u, "auto_evade"); }
                catch { return false; }
            }

            public bool NativeOk { get { return _a.Capabilities.Contains("sub_evasion_native"); } }

            public bool OwnOk { get { return _a.Capabilities.Contains("set_telegraph"); } }

            private string StateNameOf(object u)
            {
                var sm = Get(u, SubEvasionSpecs.ObjStateMachine);
                if (sm == null) return null;
                string n = Get(sm, SubEvasionSpecs.SmCurrentStateName) as string;
                if (n != null) return n;
                var st = Get(sm, SubEvasionSpecs.SmCurrentState);
                return st == null ? null : st.GetType().Name;
            }

            public Ui.SubUnit Sub(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || _a.UnitKind4(u) != "sub") return null;
                    var r = new Ui.SubUnit { Uid = uid, Alive = IsAlive(u) && !B(Get(u, SmartAttackSpecs.ObjIsDestroyed)) };
                    if (!r.Alive) return r;
                    var sm = Get(u, SubEvasionSpecs.ObjStateMachine);
                    r.StateName = StateNameOf(u);
                    var cur = sm == null ? null : Get(sm, SubEvasionSpecs.SmCurrentState);
                    var prio = cur == null ? null : D(Get(cur, SubEvasionSpecs.StatePriority));
                    if (prio.HasValue) r.StatePriority = (int)prio.Value;
                    var po = Get(u, SubEvasionSpecs.ObjPlayerCommandOverride);
                    if (po is bool) r.PlayerOverride = (bool)po;
                    r.HeadingDeg = _a.GridHeadingTrue(u, SubEvasionSpecs.ObjHeading) ?? 0;
                    r.Kn = Math.Abs(D(_a.Call(u, SmartAttackSpecs.ObjVelocityKn)) ?? 0);
                    r.DepthFt = -(D(_a.Call(u, SmartAttackSpecs.ObjHeightFt)) ?? 0);
                    var pr = D(Get(u, SmartAttackSpecs.SubCurrentPreset));
                    if (pr.HasValue) r.Preset = (int)pr.Value;
                    r.Telegraph = _a.Telegraph(u);
                    var st = Get(u, SubEvasionSpecs.SubStateTorpedoEvasion);
                    var panic = st == null ? null : Get(st, SubEvasionSpecs.StatePanicMode);
                    if (panic is bool) r.Panic = (bool)panic;
                    var cav = Get(u, SubEvasionSpecs.ObjIsCavitating);
                    if (cav is bool) r.Cavitating = (bool)cav;
                    var cf = _a.Present(SubEvasionSpecs.ObjCavitationForbidden) ? _a.RxGet(u, SubEvasionSpecs.ObjCavitationForbidden) : null;
                    if (cf is bool) r.CavForbidden = (bool)cf;
                    var hid = Get(Get(u, SubEvasionSpecs.ObjAi), SubEvasionSpecs.AiHidingState);
                    if (hid != null) r.Hiding = hid.ToString();
                    r.NoisemakersLeft = NoisemakersLeft(u);
                    return r;
                }
                catch { return null; }
            }

            /// <summary>All Noisemaker ammunition left (chaff is not counted); -1 = the rows cannot be read.</summary>
            private int NoisemakersLeft(object u)
            {
                try
                {
                    int n = 0;
                    foreach (var d in _a.DecoyRows(u)) if (d.Type == "Noisemaker") n += d.Count;
                    return n;
                }
                catch { return -1; }
            }

            public Ui.SubThreat Threat(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return null;
                    var thr = Get(u, SubEvasionSpecs.ObjThreatingTorpedo);
                    if (thr == null || !IsAlive(thr)) return null;
                    double lat0, lon0, h;
                    if (!_a.OwnPos(u, out lat0, out lon0, out h)) return null;
                    EvTorp tp; double[] tpos;
                    bool known = _a.NearestKnownTorpedo(u, lat0, lon0, out tp, out tpos);   // the player's plot, else the announcement of the game
                    var r = new Ui.SubThreat { Known = known, TrackId = -1, DistNm = double.MaxValue };
                    if (!known) return r;
                    r.TrackId = tp.TrackId; r.BrgDeg = tp.BrgDeg; r.DistNm = tp.DistNm;
                    try
                    {
                        r.TorpMaxDepthUnity = D(Get(Get(thr, SmartAttackSpecs.WbAp), SmartAttackSpecs.ApMaxDepth));
                        if (_a.Present(SmartAttackSpecs.SubPresetDepthFt)) r.Preset6Ft = D(_a.CallOpt(u, SmartAttackSpecs.SubPresetDepthFt, 6));
                        r.SeabedUnity = _a.SaTerrainHeight(_a.RxGet(u, SmartAttackSpecs.ObjPosition));
                    }
                    catch { }
                    return r;
                }
                catch { return null; }
            }

            // ---------- orders ----------

            public bool StartNative(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    var sm = Get(u, SubEvasionSpecs.ObjStateMachine);
                    var st = Get(u, SubEvasionSpecs.SubStateTorpedoEvasion);
                    if (sm == null || st == null) return false;
                    // the flag of a standing order of the player pulls the machine back into PlayerOverride at the next update (ADR-0071 supplement D1)
                    if (B(Get(u, SubEvasionSpecs.ObjPlayerCommandOverride)) && _a.Present(SubEvasionSpecs.ObjSetPlayerCommandOverride))
                        _a.Call(u, SubEvasionSpecs.ObjSetPlayerCommandOverride, false);
                    _a.Call(sm, SubEvasionSpecs.SmSetState, st);
                    return true;
                }
                catch { return false; }
            }

            public bool FinishNative(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !_a.Present(SubEvasionSpecs.StateIsFinished)) return false;
                    var st = Get(u, SubEvasionSpecs.SubStateTorpedoEvasion);
                    return st != null && _a.Set(st, SubEvasionSpecs.StateIsFinished, true);
                }
                catch { return false; }
            }

            public bool PlaceCourse(int uid, double courseDeg)
            {
                try
                {
                    var u = Unit(uid);
                    double lat0, lon0, h, lat, lon;
                    if (u == null || !_a.OwnPos(u, out lat0, out lon0, out h)) return false;
                    WorldDestination(lat0, lon0, courseDeg, EvasionLegNm, out lat, out lon);
                    string ack = _a.PlaceWaypoint("0", u, lat, lon, false, "");
                    return ack != null && ack.Contains("\"ok\":true");
                }
                catch { return false; }
            }

            public bool SetTelegraph(int uid, int step)
            {
                try
                {
                    string ack = _a.TelegraphCmd("0", new Dictionary<string, object> { { "uid", (double)uid }, { "telegraph", (double)step } });
                    return ack != null && ack.Contains("\"ok\":true");
                }
                catch { return false; }
            }

            public bool SetPreset(int uid, int preset)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !_a.Present(SmartAttackSpecs.SubSetPresetDepth)) return false;
                    _a.CallOpt(u, SmartAttackSpecs.SubSetPresetDepth, preset, false);
                    return true;
                }
                catch { return false; }
            }

            public bool SetCavitationForbidden(int uid, bool on)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !_a.Present(SubEvasionSpecs.ObjCavitationForbidden)) return false;
                    _a.RxSet(u, SubEvasionSpecs.ObjCavitationForbidden, on);
                    return true;
                }
                catch { return false; }
            }

            public bool LaunchNoisemaker(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null || !_a.Present(SubEvasionSpecs.ObjLaunchNoisemaker)) return false;
                    foreach (var d in _a.DecoyRows(u))
                        if (d.Type == "Noisemaker" && d.Count >= 1) { _a.Call(u, SubEvasionSpecs.ObjLaunchNoisemaker, d.Name); return true; }
                }
                catch { }
                return false;
            }

            public void SetUnitAutoEvade(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return;
                    UnitFlags f;
                    if (_a._flags.TryGetValue(u, out f)) f.AutoEvade = true;
                    _a.FlagStoreSetUnit(u, true, null, null);
                }
                catch { }
            }

            public void TimeToNormal()
            {
                try
                {
                    if (!_a.Capabilities.Contains("set_time_compression")) return;
                    _a.Execute(new Dictionary<string, object> { { "cmd", "set_time_compression" }, { "value", 1.0 } });
                }
                catch { }
            }

            // ---------- own memory of the run (ControlMemory is not used: the native state places no waypoints) ----------

            public Ui.SubSnapshot Remember(int uid)
            {
                var s = new Ui.SubSnapshot();
                try
                {
                    var u = Unit(uid);
                    if (u == null) return s;
                    s.Route = _a.Waypoints(u);
                    if (s.Route.Count == 0) s.CourseDeg = _a.DemandedCourse(u);
                    s.Telegraph = _a.Telegraph(u);
                    var pr = D(Get(u, SmartAttackSpecs.SubCurrentPreset));
                    if (pr.HasValue) s.Preset = (int)pr.Value;
                    var alt = D(_a.RxGet(u, CapSpecs.ObjDesiredAltitude));
                    if (alt.HasValue && alt.Value < 0) s.DepthFt = -alt.Value * SaFtPerUnity;
                    var cf = _a.Present(SubEvasionSpecs.ObjCavitationForbidden) ? _a.RxGet(u, SubEvasionSpecs.ObjCavitationForbidden) : null;
                    if (cf is bool) s.CavForbidden = (bool)cf;
                    var po = Get(u, SubEvasionSpecs.ObjPlayerCommandOverride);
                    if (po is bool) s.PlayerOverride = (bool)po;
                }
                catch { }
                return s;
            }

            public string Restore(int uid, Ui.SubSnapshot s, bool full)
            {
                var parts = new List<string>();
                try
                {
                    var u = Unit(uid);
                    if (u == null || s == null) return "unit_not_found";
                    if (full) parts.Add(RestoreRoute(u, s));
                    if (full) parts.Add(RestoreTelegraph(u, uid, s));
                    if (full) parts.Add(RestoreDepth(u, uid, s));
                    if (s.CavForbidden.HasValue)
                    {
                        try { if (_a.Present(SubEvasionSpecs.ObjCavitationForbidden)) { _a.RxSet(u, SubEvasionSpecs.ObjCavitationForbidden, s.CavForbidden.Value); parts.Add("cav=" + (s.CavForbidden.Value ? "on" : "off")); } }
                        catch (Exception e) { parts.Add("cav=" + e.GetType().Name); }
                    }
                    // the standing order of the player comes back last: the orders above set the flag themselves, idempotent (ADR-0071 supplement D1)
                    if (s.PlayerOverride == true && _a.Present(SubEvasionSpecs.ObjSetPlayerCommandOverride))
                    {
                        try { _a.Call(u, SubEvasionSpecs.ObjSetPlayerCommandOverride, true); }
                        catch (Exception e) { parts.Add("override=" + e.GetType().Name); }
                    }
                }
                catch (Exception e) { parts.Add("error=" + e.GetType().Name); }
                return parts.Count == 0 ? "partial" : string.Join(" ", parts.ToArray());
            }

            private string RestoreRoute(object u, Ui.SubSnapshot s)
            {
                try
                {
                    var now = _a.Waypoints(u);
                    bool same = s.Route.Count > 0 && now.Count > 0 && WorldDistanceNm(now[0][0], now[0][1], s.Route[0][0], s.Route[0][1]) <= 0.2;
                    string act = Ui.SubEvasionMath.RouteAction(s.Route.Count, same, s.CourseDeg.HasValue);
                    if (act == "replaced")
                        for (int i = 0; i < s.Route.Count; i++) _a.PlaceWaypoint("0", u, s.Route[i][0], s.Route[i][1], i > 0, "");
                    else if (act == "course")
                    {
                        double lat0, lon0, h, lat, lon;
                        if (_a.OwnPos(u, out lat0, out lon0, out h))
                        {
                            WorldDestination(lat0, lon0, s.CourseDeg.Value, EvasionLegNm, out lat, out lon);
                            _a.PlaceWaypoint("0", u, lat, lon, false, "");
                        }
                    }
                    return "route=" + act;
                }
                catch (Exception e) { return "route=" + e.GetType().Name; }
            }

            private string RestoreTelegraph(object u, int uid, Ui.SubSnapshot s)
            {
                try
                {
                    int? now = _a.Telegraph(u);
                    if (!s.Telegraph.HasValue) return "tg=skipped";
                    if (now != s.Telegraph) SetTelegraph(uid, s.Telegraph.Value);
                    return "tg=" + (now.HasValue ? now.Value.ToString() : "?") + "->" + s.Telegraph.Value;
                }
                catch (Exception e) { return "tg=" + e.GetType().Name; }
            }

            private string RestoreDepth(object u, int uid, Ui.SubSnapshot s)
            {
                try
                {
                    var cur = D(Get(u, SmartAttackSpecs.SubCurrentPreset));
                    string from = cur.HasValue ? ((int)cur.Value).ToString() : "?";
                    if (s.Preset.HasValue && _a.Present(SmartAttackSpecs.SubSetPresetDepth))
                    {
                        if (!cur.HasValue || (int)cur.Value != s.Preset.Value) SetPreset(uid, s.Preset.Value);
                        return "depth=" + from + "->" + s.Preset.Value;
                    }
                    if (s.DepthFt.HasValue && _a.Present(SmartAttackSpecs.SubSetDepth))
                    {
                        _a.Call(u, SmartAttackSpecs.SubSetDepth, (float)(s.DepthFt.Value / SaFtPerUnity));
                        return "depth=" + from + "->" + Math.Round(s.DepthFt.Value) + "ft";
                    }
                    return "depth=skipped";
                }
                catch (Exception e) { return "depth=" + e.GetType().Name; }
            }

            public void Log(string line) { try { _a.Log(line); } catch { } }

            public void Event(string name, string json)
            {
                try { if (_a.EventsAvailable) _a.EvLine(name, json); } catch { }
            }
        }
    }
}
