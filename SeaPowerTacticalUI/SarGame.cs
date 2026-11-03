using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: the real ISarGame. Main thread, by reflection through GameApi helpers.
    // Outside a mission every method returns empty or an error code and never throws. Not exercised offline (needs the game).
    internal sealed partial class GameApi
    {
        internal SarController Sar;

        /// <summary>Wave 2 wiring: name of the ASW/SAR group of a unit (AswController), null = none.</summary>
        internal Func<int, string> SarAswGroupOf;

        /// <summary>Wave 2 wiring: true while the smart torpedo attack (ADR-0038) owns the unit.</summary>
        internal Ui.ISmartBusy SmartBusy;

        private SarGameImpl _sarImpl;
        private readonly AirTickClock _sarClock = new AirTickClock();   // T-126: 1 s of game time and at least 0.25 s of real time

        internal SarController CreateSarController(SarParams p, Action<Action> background)
        {
            _sarImpl = new SarGameImpl(this);
            Sar = new SarController(_sarImpl, p, background);
            return Sar;
        }

        /// <summary>Per frame, main thread: the pumps (the mission change comes from MissionLifecycle, T-130). Tick throttles itself to 1 s of game time.</summary>
        internal void PumpSar() { PumpSar(Clock != null ? Clock() : 0); }

        internal void PumpSar(double realT)
        {
            var c = Sar;
            if (c == null || _sarImpl == null || !Frame.InMission) return;
            double t = Frame.MissionTimeS ?? 0;
            NoteTakeSelection(t);   // T-317 (ADR-0124): the selection memory is shared with CAP
            c.FramePump();
            if (_sarClock.Due(t, realT)) { c.Tick(t); }
        }

        /// <summary>True when the workshop mod AutomaticSAR is loaded (message in the log, selfcheck field ext_sar_present).</summary>
        internal bool ExtSarPresent()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("AutomaticSAR.RescueCoordinator", false); } catch { }
                    if (t != null) return true;
                }
            }
            catch { }
            return false;
        }

        private sealed class SarGameImpl : ISarGame
        {
            private readonly GameApi _a;
            private TerrainHeightCache _heights;
            private readonly Dictionary<int, double[]> _placed = new Dictionary<int, double[]>();   // last waypoint the mod placed {lat, lon}
            internal double LastTick = -1e9;

            public SarGameImpl(GameApi a) { _a = a; }

            internal void ResetMission()
            {
                _heights = null; _placed.Clear(); LastTick = -1e9; _a.TakeTracker.Clear();   // T-317
                _scanLog.Reset(); _scan.Begin(-1, -1); _cache.Reset();
            }

            private object Unity(int uid) { try { return uid < 0 ? null : _a.FindOwnUnit(uid); } catch { return null; } }

            public double Now { get { return _a.Frame.MissionTimeS ?? 0; } }

            public double TimeScale
            {
                get
                {
                    try { var v = GameApi.D(_a.GetStatic(CoreSpecs.TimeCompression)); return v ?? 1; }
                    catch { return 1; }
                }
            }

            // ---- terrain ------------------------------------------------------------------------------------------

            public double HeightM(GeoPt g)
            {
                try
                {
                    if (_heights == null)
                    {
                        var sampler = _a.TerrainSampler(false);
                        if (sampler == null) return TerrainHeightCache.OpenSeaM;   // T-275: no sampler yet is asked again at the next call
                        _heights = new TerrainHeightCache(sampler, null, m => Log("[Sar] " + m));   // no data is not cached; the line is always logged
                    }
                    return _heights.HeightM(g);
                }
                catch { return TerrainHeightCache.OpenSeaM; }
            }

            // ---- rafts / threats ----------------------------------------------------------------------------------

            /// <summary>0.27.10: the counts of the last Rafts() call (dev state "raft_scan"); a copy is handed out.</summary>
            internal SarRaftScan LastScan { get { return _scan.Copy(); } }
            private readonly SarRaftScan _scan = new SarRaftScan();
            private readonly SarRaftScanLog _scanLog = new SarRaftScanLog(60);

            /// <summary>0.30.1: one game read per frame (SarRaftCache); every SAR user asks for the rafts, the game list is walked once.</summary>
            private readonly SarRaftCache _cache = new SarRaftCache();

            public IList<SarRaft> Rafts()
            {
                double real = 0;
                try { real = _a.Clock != null ? _a.Clock() : 0; } catch { }
                return _cache.Get(Now, real, ReadRafts);
            }

            private List<SarRaft> ReadRafts()
            {
                var res = new List<SarRaft>();
                var sc = _scan;
                sc.Begin(-1, Now);
                try
                {
                    var om = _a.ObjectsManagerInstance();
                    var list = om == null ? null : _a.Get(om, SarSpecs.Rafts) as IList;
                    if (list == null) { ScanLog(); return res; }
                    sc.List = list.Count;
                    // 0.27.10: a copy first (the game may add a raft while we read), then every raft on its own: one bad raft
                    // used to throw out of the loop into a silent catch and the whole list came back empty
                    var copy = new object[list.Count];
                    list.CopyTo(copy, 0);
                    foreach (var r in copy)
                    {
                        try
                        {
                            if (r == null || !GameApi.IsAlive(r)) { sc.Dead++; continue; }
                            double lat, lon, h;
                            if (!_a.OwnPos(r, out lat, out lon, out h)) { sc.NoPos++; continue; }
                            var obp = _a.Get(r, AirOpsSpecs.ObjObp);
                            var sr = new SarRaft
                            {
                                Id = _a.UidOf(r), Type = _a.Get(obp, AirOpsSpecs.ObpIniName) as string, Lat = lat, Lon = lon,
                                People = (int)(GameApi.D(_a.RxGet(obp, AirOpsSpecs.ObpTransported)) ?? 0),
                                Destroyed = GameApi.BoolOf2(_a.Get(r, AirOpsSpecs.ObjIsDestroyed))
                            };
                            sr.OnLand = HeightM(new GeoPt(lat, lon)) > 0;
                            res.Add(sr);
                            sc.Count(sr);
                        }
                        catch (Exception e) { sc.Error(e); }
                    }
                }
                catch (Exception e) { sc.Error(e); }
                ScanLog();
                return res;
            }

            private void ScanLog()
            {
                var line = _scanLog.Next(_scan, Now);
                if (line != null) Log("[Sar] " + line);
            }

            public IList<SarThreat> Threats()
            {
                var res = new List<SarThreat>();
                try
                {
                    var snap = _a.Snapshot(false);
                    foreach (var t in snap.Tracks)
                    {
                        if (t.Side != "hostile" || !t.Lat.HasValue || !t.Lon.HasValue || t.Gone) continue;
                        string k = t.Kind ?? "";
                        // air defence of a hostile surface or ground object; an unclassified one counts as armed (ADR-0039 D3); only what the player knows
                        bool ground = k == "Surface" || k == "Land" || k == "Ground" || k == "Building" || k == "Unknown" || k == "";
                        bool unclassified = t.Classified.HasValue && !t.Classified.Value;
                        if (ground || unclassified) res.Add(new SarThreat { Id = t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, AirDefense = true });
                    }
                }
                catch { }
                return res;
            }

            // ---- units ----------------------------------------------------------------------------------------------

            private bool IsSubmarine(object o)
            {
                var t = o == null ? null : _a.TypeOf(SarSpecs.SubmarineType);
                return t != null && t.IsInstanceOfType(o);
            }

            private bool ExtSar(object u)
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType("AutomaticSAR.RescueCoordinator", false); } catch { }
                        if (t == null) continue;
                        var m = t.GetMethod("IsActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m == null || m.GetParameters().Length != 1) return false;
                        var r = m.Invoke(null, new[] { u });
                        return r is bool && (bool)r;
                    }
                }
                catch { }
                return false;
            }

            private bool InSarZone(object u)
            {
                try
                {
                    var z = _a.Get(u, AirOpsSpecs.ObjPatrolZone);
                    if (z == null) return false;
                    var zt = _a.Get(z, AirOpsSpecs.PzZoneType);
                    return zt != null && zt.ToString() == "SAR";
                }
                catch { return false; }
            }

            /// <summary>T-161: own alive controllable ships and submarines (player taskforce lists, specs from CoreSpecs).</summary>
            public IList<int> OwnShipUids()
            {
                var res = new List<int>();
                try
                {
                    var tf = _a.GetStatic(CoreSpecs.PlayerTaskforce);
                    if (tf == null) return res;
                    foreach (var listSpec in CoreSpecs.OwnShipLists)
                    {
                        var list = _a.Get(tf, listSpec) as IEnumerable;
                        if (list == null) continue;
                        foreach (var o in list)
                        {
                            try
                            {
                                if (o == null || !GameApi.IsAlive(o) || !GameApi.BoolOf2(_a.Get(o, CoreSpecs.ObjIsControllable))) continue;
                                int uid = _a.UidOf(o);
                                if (uid >= 0 && !res.Contains(uid)) res.Add(uid);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                return res;
            }

            public SarUnit Unit(int uid)
            {
                var u = Unity(uid);
                if (u == null) return null;
                var s = new SarUnit { Uid = uid };
                try
                {
                    s.Lost = !GameApi.IsAlive(u) || GameApi.BoolOf2(_a.Get(u, AirOpsSpecs.ObjIsDestroyed));
                    bool sub = IsSubmarine(u);
                    s.Kind = _a.IsHelicopter(u) ? "helo" : sub ? "sub" : "ship";
                    s.Name = _a.NameOf(u);
                    s.Type = _a.Get(_a.Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpIniName) as string;
                    if (s.Lost) return s;
                    double lat, lon, h;
                    if (_a.OwnPos(u, out lat, out lon, out h)) { s.Lat = lat; s.Lon = lon; }
                    s.SpeedKn = Math.Abs(GameApi.D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0);
                    var rp = _a.RxGet(u, SarSpecs.RaftPickUp);
                    s.RaftPickup = rp is bool && (bool)rp;
                    s.Telegraph = _a.Telegraph(u) ?? 0;
                    if (sub)
                    {
                        var sm = _a.RxGet(u, SarSpecs.SubIsSubmerged);
                        s.Submerged = !(sm is bool) || (bool)sm;
                        s.DepthM = -(GameApi.D(_a.Call(u, AirOpsSpecs.ObjHeightFt)) ?? 0) * 0.3048;
                    }
                    var obp = _a.Get(u, AirOpsSpecs.ObjObp);
                    s.Onboard = (int)(GameApi.D(_a.RxGet(obp, AirOpsSpecs.ObpTransported)) ?? 0);
                    s.Capacity = (int)(GameApi.D(_a.Get(obp, AirOpsSpecs.ObpTransportCapacity)) ?? 0);
                    s.InFormation = _a.Get(u, CoreSpecs.ObjFormation) != null;
                    s.ExtSar = ExtSar(u);
                    s.GameSarZone = InSarZone(u);
                    s.Evading = _a.EvasionActive(u);
                    s.AswGroup = _a.SarAswGroupOf == null ? null : _a.SarAswGroupOf(uid);
                    s.SmartAttack = _a.SmartBusy != null && _a.SmartBusy.Busy(uid);
                    s.Waypoints = _a.Waypoints(u).Count;
                }
                catch { }
                return s;
            }

            // ---- orders ---------------------------------------------------------------------------------------------

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

            public void ForgetRoute(int uid) { _placed.Remove(uid); }   // T-318

            public string Goto(int uid, IList<GeoPt> pts, string heightState, int altFt)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                if (pts == null || pts.Count == 0) return "bad_args";
                _a.WpqNotePlaced(uid, "sar_goto");   // T-331
                try
                {
                    bool heli = _a.IsHelicopter(u);
                    double lat0, lon0, h0;
                    if (!_a.OwnPos(u, out lat0, out lon0, out h0)) return "no_position";
                    int? tgBefore = _a.Telegraph(u);
                    var list = new List<GeoPt>(pts);
                    // the game's own SAR flies two transit points when the raft is far; one transit point short of the raft keeps the cruise height
                    if (heli && heightState == "very_low" && list.Count == 1)
                    {
                        var last = list[0];
                        double lk = AswMath.LonK(lat0, last.Lat);   // flat on stable, x LonK on the beta globe (T-348)
                        double dn = (last.Lat - lat0) * 60.0, de = GeoLon.DLon(lon0, last.Lon) * 60.0 * lk, d = Math.Sqrt(dn * dn + de * de);
                        if (d > 5.0) list.Insert(0, new GeoPt(last.Lat - dn / d * 1.5 / 60.0, last.Lon - de / d * 1.5 / (60.0 * lk)));
                    }
                    _placed.Remove(uid);   // T-318: a failed Goto leaves no stale base
                    _a.Call(u, AirOpsSpecs.ObjRemoveWaypoints);
                    if (!_a.Present(AirOpsSpecs.ObjSetWaypoint) || !_a.Present(SarSpecs.HeightStateType)) return "unsupported";
                    object firstGeo = null;
                    for (int i = 0; i < list.Count; i++)
                    {
                        bool final = i == list.Count - 1;
                        double height = h0;
                        object state = _a.GetStatic(SarSpecs.HeightStateNumber);
                        if (heli)
                        {
                            if (final && heightState == "very_low") { height = 0.18142840266227722; state = _a.GetStatic(SarSpecs.HeightStateVeryLow); }
                            else height = altFt / FeetPerUnit;
                        }
                        else if (!IsSubmarine(u)) height = 0;
                        var geo = _a.MakeGeo(list[i].Lat, list[i].Lon, height);
                        if (firstGeo == null) firstGeo = geo;
                        var task = _a.Call(u, AirOpsSpecs.ObjSetWaypoint, geo, "SAR", state);
                        if (final && !heli && task != null) { try { _a.Set(task, SarSpecs.GoToReachable, false); } catch { } }
                    }
                    try
                    {
                        if (firstGeo != null) _a.Set(u, AirOpsSpecs.ObjCommandPosition, firstGeo);
                    }
                    catch { }
                    int? tgAfter = _a.Telegraph(u);
                    if (tgBefore.HasValue && tgAfter.HasValue && tgBefore.Value != tgAfter.Value) _a.Call(u, AirOpsSpecs.ObjSetTelegraph, tgBefore.Value);
                    var lastPt = list[list.Count - 1];
                    _placed[uid] = new[] { lastPt.Lat, lastPt.Lon };
                    return null;
                }
                catch (Exception e) { return "waypoint_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public string SetTelegraph(int uid, int telegraph)
            {
                if (Unity(uid) == null) return "unit_not_found";
                try { return AckError(_a.Execute(new Dictionary<string, object> { { "cmd", "set_telegraph" }, { "uid", (double)uid }, { "telegraph", (double)telegraph } })); }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string Surface(int uid)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    var cur = GameApi.D(_a.Get(u, SarSpecs.SubPresetDepth));
                    if (cur.HasValue) _a._ctlMem.RememberField(u, "sar_depth", ((int)cur.Value).ToString());
                    _a.CallOpt(u, SarSpecs.SubSetPresetDepth, 0, false);
                    _a.CallOpt(u, SarSpecs.SetPlayerHeightControl, false);
                    _a._ctlMem.NoteFieldSet(u, "sar_depth", "0");
                    return null;
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            private string RestoreDepthOf(object u)
            {
                var e = _a._ctlMem.Peek(u);
                FieldMem f;
                if (e == null || !e.Fields.TryGetValue("sar_depth", out f) || f.Saved == null) return "nothing_saved";
                var cur = GameApi.D(_a.Get(u, SarSpecs.SubPresetDepth));
                if (f.SetByMod != null && cur.HasValue && ((int)cur.Value).ToString() != f.SetByMod) { e.Fields.Remove("sar_depth"); return "player_changed"; }
                int preset;
                if (!int.TryParse(f.Saved, out preset)) return "bad_value";
                try { _a.CallOpt(u, SarSpecs.SubSetPresetDepth, preset, false); } catch (Exception ex) { return ex.GetType().Name; }
                e.Fields.Remove("sar_depth");
                return null;
            }

            public string RestoreDepth(int uid)
            {
                var u = Unity(uid);
                return u == null ? "unit_not_found" : RestoreDepthOf(u);
            }

            private void RestoreRaftPickup(object u)
            {
                var e = _a._ctlMem.Peek(u);
                FieldMem f;
                if (e == null || !e.Fields.TryGetValue("raft_pickup", out f) || f.Saved == null) return;
                try
                {
                    _a.RxSet(u, SarSpecs.RaftPickUp, f.Saved == "true");
                }
                catch { }
                e.Fields.Remove("raft_pickup");
            }

            public string SetRaftPickup(int uid, bool on)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    var cur = _a.RxGet(u, SarSpecs.RaftPickUp);
                    if (cur == null) return "unsupported";
                    _a._ctlMem.RememberField(u, "raft_pickup", cur is bool && (bool)cur ? "true" : "false");
                    _a.RxSet(u, SarSpecs.RaftPickUp, on);
                    return null;
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string Rtb(int uid)
            {
                if (Unity(uid) == null) return "unit_not_found";
                try { return AckError(_a.ReturnToBaseCmd("0", new Dictionary<string, object> { { "uid", (double)uid } })); }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string Hover(int uid)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                try { _a.Call(u, AirOpsSpecs.ObjRemoveWaypoints); _placed.Remove(uid); return null; }
                catch (Exception e) { return e.GetType().Name; }
            }

            /// <summary>T-147 stream H: a helicopter without work waits over its ship: a single waypoint at the ship position (it hovers there once it arrives).</summary>
            public string HoverOver(int uid, GeoPt ship)
            {
                return Goto(uid, new List<GeoPt> { ship }, "transit", HoverOverAltFt);
            }

            private const int HoverOverAltFt = 300;

            public string StopEngines(int uid)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                try { _a.Call(u, AirOpsSpecs.ObjRemoveWaypoints); _a.Call(u, AirOpsSpecs.ObjSetTelegraph, 0); _placed.Remove(uid); return null; }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string Remember(int uid)
            {
                var u = Unity(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    _a.TacticsEnsureMission();
                    _a._ctlMem.RememberRoute(u, _a.PlayerWaypoints(u), _a.DemandedCourse(u));   // T-278
                    _a.RememberFields(u, new[] { "telegraph" });
                    var rp = _a.RxGet(u, SarSpecs.RaftPickUp);
                    _a._ctlMem.RememberField(u, "raft_pickup", rp is bool && (bool)rp ? "true" : "false");
                    return null;
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string Restore(int uid)
            {
                var u = Unity(uid);
                _placed.Remove(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    RestoreRaftPickup(u);
                    RestoreDepthOf(u);
                    // the same path as "Return control" (route, telegraph, ROE); the SAR controller's own flags were taken above
                    return AckError(_a.RestoreControlCmd("0", new Dictionary<string, object> { { "uid", (double)uid } }));
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            public bool TakenByPlayer(int uid)
            {
                double[] placed;
                if (!_placed.TryGetValue(uid, out placed)) return false;
                var u = Unity(uid);
                if (u == null) return false;
                try
                {
                    var wps = _a.Waypoints(u);
                    if (wps.Count == 0) return false;   // route finished or hover: nothing to compare
                    var last = wps[wps.Count - 1];
                    // T-317 (ADR-0124): a deviation inside the AI combat window of a helicopter the player did not select is the game (ai drift)
                    var p = new TakeProbe { State = _a.StateName(u, "CurrentStateName"), Wps = wps.Count, DevNm = WorldDistanceNm(last[0], last[1], placed[0], placed[1]) };
                    var tasks = _a.Get(u, CapSpecs.ObjEngageTasks) as System.Collections.ICollection;
                    p.EngageTasks = tasks == null ? 0 : tasks.Count;
                    var al = _a.Get(u, CapSpecs.ObjAlignmentTasks) as System.Collections.ICollection;
                    p.Aligning = al != null && al.Count > 0;
                    double now = Now;
                    if (CapTakeoverMath.Deviation(p) != null) _a.TakeInheritLeader(u, uid);
                    string why;
                    var v = _a.TakeTracker.Check(uid, p, now, out why);
                    if (v == TakeVerdict.None) return false;
                    if (v == TakeVerdict.AiDrift)
                    {
                        _placed[uid] = new[] { last[0], last[1] };
                        if (!_a.TakeTracker.DriftLogged(uid))
                        {
                            _a.TakeTracker.NoteDriftLogged(uid);
                            Log("[Sar] ai drift uid=" + uid + " why=" + why + " " + CapTakeoverMath.Detail(p) + " since_ai_s=" + _a.TakeTracker.SinceAiS(uid, now).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        return false;
                    }
                    Log("[Sar] player took uid=" + uid + " why=" + why + " " + CapTakeoverMath.Detail(p));
                    return true;
                }
                catch { return false; }
            }

            public void Event(string name, string dataJson)
            {
                try { _a.EvLine(name, dataJson); } catch { }
            }

            public void Log(string line)
            {
                try { var s = _a.AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
            }
        }
    }
}
