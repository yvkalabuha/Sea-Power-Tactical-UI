using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-102 / ADR-0038: the real ISmartAttackGame. Everything here is main-thread, by reflection through GameApi helpers.
    // Not exercised offline (needs the game): see the live checks in the T-102 plan, section 7.
    // Leak rule (ADR-0038 section 8): decisions use the player's track and the own layer only. The few reads of the game's
    // true objects carry a marker comment (at most 4 in all SmartAttack*.cs and TorpedoWire.cs, checked by a static test).
    internal sealed partial class GameApi
    {
        internal SmartAttackController SmartAttack;

        /// <summary>BepInEx/config/SeaPowerTacticalUI/smart_attack, set by the plugin (BepInEx is not referenced here so the selfcheck can compile this file).</summary>
        internal string SmartAttackSaveDir;

        /// <summary>Opens the target pick mode for a submarine uid (set by the plugin). Null = no pick mode available.</summary>
        internal Ui.ISmartPick SmartPick;

        /// <summary>True while the pick mode waits for a click (state/link information).</summary>
        internal bool SmartAttackPickActive;

        /// <summary>True while the pick mode (and two frames after it) must keep the pick click away from the game (PatchesUi.ApplySmartPick prefixes).</summary>
        internal static volatile bool SmartPickBlocking;
        /// <summary>Last frame (Time.frameCount) at which the pick click is still swallowed after the mode ended.</summary>
        internal static int SmartPickSwallowUntilFrame;

        /// <summary>Icon pick tolerance on the map, pixels ([SmartAttack] PickTolPx, bound by the integrator).</summary>
        internal double PickTolPx = 16;

        /// <summary>Sink of the smart attack journal: the host log (set by SmartAttackFeature).</summary>
        internal System.Action<string> SmartLogSink;

        private SmartAttackGameImpl _smartImpl;
        private string _smartSavePath;
        private double _smartSaveCheckT = -1000;

        internal SmartAttackController CreateSmartAttackController(SmartParams p, bool debug)
        {
            _smartImpl = new SmartAttackGameImpl(this);
            var c = new SmartAttackController(_smartImpl, p, debug);
            SmartAttack = c;
            return c;
        }

        /// <summary>Per frame, main thread: a new last_save -> save marker (the mission change comes from MissionLifecycle, T-130), then the controller pump (it throttles itself to 1 s of game time).</summary>
        internal void PumpSmartAttack()
        {
            var c = SmartAttack;
            if (c == null || _smartImpl == null || !Frame.InMission) return;
            double t = Frame.MissionTimeS ?? 0;
            double w = Clock();
            if (w - _smartSaveCheckT >= 1.0)   // the last_save file is read at most once per second
            {
                _smartSaveCheckT = w;
                string sp = _smartImpl.LastSavePath;
                if (sp != null && sp != _smartSavePath) { _smartSavePath = sp; c.OnSaveMarker(sp, t); }
            }
            c.Pump(t);
        }

        /// <summary>Mouse block for the smart attack dialog: an owner of the shared UiMouseBlock arbiter.</summary>
        internal void SetSmartBlock(bool on)
        {
            SetMouseBlock("smart", on);   // T-154: one arbiter (UiMouseBlock) for the shared game flag
        }

        /// <summary>"Return control" hook (T-091, RestoreControl.cs; wired in integration I1).</summary>
        internal void SmartAttackReleaseUnit(object u)
        {
            if (SmartAttack != null && u != null) SmartAttack.ReleaseUnit(UidOf(u));
        }

        // ---------- commands ----------

        private static string SmartAck(string id, string dataJson)
        {
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true,\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) + "}";
        }

        /// <summary>smart_attack | smart_attack_cancel | smart_attack_status. Main thread. Null = not ours.</summary>
        internal string SmartAttackCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "smart_attack" && name != "smart_attack_cancel" && name != "smart_attack_status") return null;
            var c = SmartAttack;
            if (c == null || !Capabilities.Contains("smart_attack")) return Err(id, "unsupported", "smart_attack capability is off");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            string err;
            switch (name)
            {
                case "smart_attack":
                {
                    var a = SmartAttackCommandsParse.Attack(cmd, out err);
                    if (a == null) return Err(id, err ?? "bad_args", null);
                    if (a.Pick || !a.TargetId.HasValue)
                    {
                        var sub = _smartImpl.Sub(a.Uid);
                        if (sub == null || !sub.Alive) return Err(id, "unit_not_found", null);
                        if (!sub.IsSub) return Err(id, "not_sub", null);
                        if (SmartPick == null) return Err(id, "unsupported", "pick mode is off");
                        SmartAlertUid = a.Uid;
                        SmartPick.Begin(a.Uid);
                        return SmartAck(id, "{\"result\":\"pick\"}");
                    }
                    string ack;
                    string code = c.Start(a, out ack);
                    return code != null ? Err(id, code, null) : SmartAck(id, ack);
                }
                case "smart_attack_cancel":
                {
                    int? uid; string attackId;
                    if (!SmartAttackCommandsParse.Cancel(cmd, out uid, out attackId, out err)) return Err(id, err ?? "bad_args", null);
                    string r = c.Cancel(uid, attackId, "command");
                    if (r == "not_found") return Err(id, "not_found", null);
                    return SmartAck(id, "{\"result\":" + Json.Str(r) + "}");
                }
                default:
                {
                    int? uid = SmartAttackCommandsParse.Status(cmd, out err);
                    if (err != null) return Err(id, err, null);
                    return SmartAck(id, c.StatusJson(uid));
                }
            }
        }

        /// <summary>Fragment "smart_attack" for link_state; null when the controller is off.</summary>
        internal string SmartAttackStateJson()
        {
            if (SmartAttack == null) return null;
            string s = SmartAttack.LinkStateJson();
            if (!SmartAttackPickActive) return s;
            // pick mode is mod-side UI state: add it to the controller fragment
            return s != null && s.EndsWith("}") ? s.Substring(0, s.Length - 1) + (s.Length > 2 ? "," : "") + "\"pick\":true}" : "{\"pick\":true}";
        }

        // ---------- pick support (SmartAttackPick.cs) ----------

        /// <summary>Uid of the submarine the last smart attack action concerned (source of the alert).</summary>
        internal int SmartAlertUid = -1;
        private string _alertLast; private double _alertLastAt = -100;

        /// <summary>The game's own alert (MainGameViewModel.RaiseAlert, as InputHandler uses it): source = the own sub, else RaiseAlert(string). Same text at most once per 3 s. Never throws.</summary>
        internal void SmartRaiseAlert(string text)
        {
            try
            {
                double now = Clock();
                if (text == _alertLast && now - _alertLastAt < 3.0) return;
                _alertLast = text; _alertLastAt = now;
                var vm = GetStatic(CoreSpecs.MainViewModel);
                if (vm == null) return;
                object src = null;
                try { if (SmartAlertUid >= 0) src = FindOwnUnit(SmartAlertUid); } catch { }
                var withSource = Member(CoreSpecs.RaiseAlertFrom) as MethodInfo;
                if (src != null && withSource != null && withSource.GetParameters()[0].ParameterType.IsInstanceOfType(src)) { Call(vm, CoreSpecs.RaiseAlertFrom, src, text); return; }
                if (Present(CoreSpecs.RaiseAlertText)) Call(vm, CoreSpecs.RaiseAlertText, text);
            }
            catch { }
        }

        internal void SmartAttackMessage(string text) { if (_smartImpl != null) _smartImpl.Message(text); }

        internal TrackInfo SmartTrackInfo(object vehicle) { return _smartImpl == null ? null : _smartImpl.TrackInfoOf(vehicle); }

        /// <summary>Why the wire torpedo cannot be used on this track (target_not_allowed|no_wire_torpedo|...), null = can.</summary>
        internal string SmartAllowError(int uid, int trackId)
        {
            if (_smartImpl == null) return "unsupported";
            string code;
            var l = _smartImpl.WireAmmoFor(uid, trackId, out code);
            return l != null && l.Count > 0 ? null : (code ?? "no_wire_torpedo");
        }

        /// <summary>T-154 E2: the pick-click check, the same path as Start without its effects. null = the attack can start; else the refusal code (detail may carry numbers).</summary>
        internal string SmartPickCheck(int uid, int trackId, out string detail)
        {
            detail = null;
            var c = SmartAttack;
            if (c != null) return c.Preflight(uid, trackId, out detail);
            return SmartAllowError(uid, trackId);
        }

        /// <summary>The picked target: the same path as the command with target_id. Result goes to the player as a message; a question opens the dialog.</summary>
        internal void SmartAttackStartPicked(int uid, int trackId)
        {
            var c = SmartAttack;
            if (c == null) return;
            SmartAlertUid = uid;
            string ack;
            string code = c.Start(new SmartAttackArgs { Uid = uid, TargetId = trackId }, out ack);
            if (code != null)
            {
                string text = SmartAttackMath.RefusalText(code, null);
                SmartAttackMessage(text);
                SmartAttackToast = text;   // the pick UI shows it at the cursor for 5 s
            }
        }

        /// <summary>Text for the cursor hint after a refused pick (read and cleared by SmartAttackPick).</summary>
        internal string SmartAttackToast;

        /// <summary>The vehicle of the player's plotting table for a unit hit in 3D (ObjectBase.GetMapVehicle(taskforce)); null = no track of it.</summary>
        internal object VehicleForObject(object hitObject, object sub)
        {
            try
            {
                if (hitObject == null || sub == null) return null;
                if (!BoolOf(Call(hitObject, SmartAttackSpecs.ObjIsUnit))) return null;
                return Call(hitObject, SmartAttackSpecs.ObjGetMapVehicle, Get(sub, SmartAttackSpecs.ObjTaskforce));   // leak-ok: 3D click resolves to the player's track, never to the object itself
            }
            catch { return null; }
        }

        /// <summary>The vehicle (MapUnitViewModel.Unit) under the cursor on the map by one Noesis hit test, as RadialProbe does; null = none. x,y in pixels, y from the top.</summary>
        internal object TrackAtMap(double x, double y, int screenH)
        {
            try
            {
                bool dummy;
                RadialProbe(x, y, screenH, null, out dummy);   // primes _radHit/_radParent
                if (_radHit == null || _radParent == null) return null;
                var root = GetStatic(RadialSpecs.MainViewInstance);
                if (root == null) return null;
                var from = Member(CoreSpecs.PointFromScreen) as MethodInfo;
                if (from == null) return null;
                double k = NoesisPxScale > 0 ? NoesisPxScale : 1.0;   // same calibration as RadialProbe (CoverageOverlay.Calibrate), not the IMGUI ring scale
                var pt = Activator.CreateInstance(from.GetParameters()[0].ParameterType, new object[] { (float)(x * k), (float)(y * k) });
                var res = _radHit.Invoke(null, new[] { root, from.Invoke(root, new[] { pt }) });
                var v = res == null ? null : Get(res, RadialSpecs.HitVisual);
                object unitVm = null;
                for (object o = v; o != null; o = _radParent.Invoke(null, new[] { o }))
                {
                    if (unitVm == null && IsTypeNamed(o, RadialSpecs.MapUnitVmType.Type)) unitVm = o;
                    var dc = Get(o, RadialSpecs.DataContext);
                    if (unitVm == null && dc != null && IsTypeNamed(dc, RadialSpecs.MapUnitVmType.Type)) unitVm = dc;
                    if (IsTypeNamed(o, RadialSpecs.MapBaseType.Type))
                    {
                        if (unitVm == null) unitVm = NearestUnitIcon(o, x * k, y * k);   // a mission-zone shape was on top: take the nearest icon
                        return unitVm == null ? null : Get(unitVm, SmartAttackSpecs.MapUnitOf);
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>The MapUnitViewModel whose icon centre is within PickTolPx of (xN,yN) (Noesis pixels): scans the visual children of the map (budget 20000 nodes).</summary>
        private object NearestUnitIcon(object mapBase, double xN, double yN)
        {
            try
            {
                var from = Member(CoreSpecs.PointFromScreen) as MethodInfo;
                var count = Member(SmartAttackSpecs.TreeChildCount) as MethodInfo;
                var child = Member(SmartAttackSpecs.TreeChild) as MethodInfo;
                if (from == null || count == null || child == null) return null;
                var vms = new List<object>(); var centers = new List<double[]>();
                var stack = new Stack<object>(); stack.Push(mapBase);
                int budget = 20000;
                while (stack.Count > 0 && budget-- > 0)
                {
                    var n = stack.Pop();
                    var dc = Get(n, RadialSpecs.DataContext);
                    if (dc != null && IsTypeNamed(dc, RadialSpecs.MapUnitVmType.Type))
                    {
                        double w = D(Get(n, SmartAttackSpecs.ElementWidth)) ?? 0, h = D(Get(n, SmartAttackSpecs.ElementHeight)) ?? 0;
                        object pt = Activator.CreateInstance(from.GetParameters()[0].ParameterType, new object[] { (float)(w / 2), (float)(h / 2) });
                        var mapped = Call(n, CoreSpecs.PointToScreen, pt);
                        if (mapped != null) { vms.Add(dc); centers.Add(new[] { D(Get(mapped, RadialSpecs.PointX)) ?? 0, D(Get(mapped, RadialSpecs.PointY)) ?? 0 }); }
                    }
                    int cc = (int)count.Invoke(null, new[] { n });
                    for (int i = 0; i < cc; i++) { var ch = child.Invoke(null, new object[] { n, i }); if (ch != null) stack.Push(ch); }
                }
                int idx = SmartAttackMath.NearestIcon(centers, xN, yN, PickTolPx * (NoesisPxScale > 0 ? NoesisPxScale : 1.0));
                return idx < 0 ? null : vms[idx];
            }
            catch { return null; }
        }

        // ---------- neutrals (shared scan lives in Common/NeutralGuard.cs) ----------

        /// <summary>Neutral and unknown tracks of the player's table (surface and subsurface, not gone) except exceptTrackId, in the plane in miles.</summary>
        private List<NeutralTrack> SmartNeutralTracks(int exceptTrackId)
        {
            var res = new List<NeutralTrack>();
            var pos = new List<double[]>(); var veh = new List<object>(); var side = new List<string>(); var domain = new List<string>();
            NeutralTracksDomain(exceptTrackId, pos, veh, side, domain);
            for (int i = 0; i < veh.Count; i++)
            {
                try
                {
                    var v = veh[i];
                    int tid = (int)(D(Get(v, SmartAttackSpecs.VehId)) ?? -1);
                    res.Add(new NeutralTrack { Id = tid, Cls = Get(Get(v, SmartAttackSpecs.VehClass), SmartAttackSpecs.SourcedValue) as string, Side = side[i], Domain = domain[i], Pos = SaPlane(RxGet(v, SmartAttackSpecs.VehPosition)) });
                }
                catch { }
            }
            return res;
        }

        // ---------- helpers ----------

        private string SaTileKey(object geo)
        {
            if (geo == null || !Present(SmartAttackSpecs.TileOf)) return null;
            var r = CallStatic(SmartAttackSpecs.TileOf, geo);
            return (D(Get(r, SmartAttackSpecs.VecX)) ?? 0) + "," + (D(Get(r, SmartAttackSpecs.VecY)) ?? 0);
        }

        /// <summary>Terrain height under the position, Unity units (negative = below the sea level).</summary>
        private double? SaTerrainHeight(object geo)
        {
            if (geo == null || !Present(SmartAttackSpecs.TerrainHeightAt)) return null;
            return D(CallStaticOpt(SmartAttackSpecs.TerrainHeightAt, geo));
        }

        private sealed class SmartAttackGameImpl : ISmartAttackGame
        {
            private readonly GameApi _a;
            private readonly Dictionary<int, object> _torps = new Dictionary<int, object>();
            internal void ResetMission() { _torps.Clear(); }   // T-130
            private string _subTile;

            public SmartAttackGameImpl(GameApi a) { _a = a; }

            public double Now { get { return _a.Frame.MissionTimeS ?? 0; } }

            public bool Paused
            {
                get
                {
                    try { var p = _a.CallStatic(CoreSpecs.GamePaused); return p is bool && (bool)p; } catch { return false; }
                }
            }

            public string LastSavePath
            {
                get
                {
                    return ReadLastSavePath();
                }
            }

            public string SaveDir { get { return _a.SmartAttackSaveDir; } }

            // ---- own submarine ------------------------------------------------------------------------------------

            private object Unit(int uid) { return uid < 0 ? null : _a.FindOwnUnit(uid); }

            public SubSnap Sub(int uid)
            {
                var u = Unit(uid);
                if (u == null) return null;
                var s = new SubSnap { Uid = uid, Name = _a.NameOf(u), Type = _a.Get(u, SmartAttackSpecs.ObjClassName) as string };
                try
                {
                    s.Alive = IsAlive(u) && !BoolOf(_a.Get(u, SmartAttackSpecs.ObjIsDestroyed));
                    s.IsSub = _a.IsOfType(u, SmartAttackSpecs.SubType);
                    if (!s.Alive) return s;
                    var geo = _a.RxGet(u, SmartAttackSpecs.ObjPosition);
                    s.Pos = _a.SaPlane(geo);
                    s.DepthFt = -(D(_a.Call(u, SmartAttackSpecs.ObjHeightFt)) ?? 0);
                    double? h = _a.SaTerrainHeight(geo);
                    s.BottomFt = h.HasValue ? -h.Value * SaFtPerUnity : 99999;
                    s.SpeedKn = Math.Abs(D(_a.Call(u, SmartAttackSpecs.ObjVelocityKn)) ?? 0);
                    s.FreeWire = BoolOf(_a.Call(u, SmartAttackSpecs.ObjHasFreeWire));
                    s.Telegraph = _a.Telegraph(u) ?? 0;
                    s.TileKey = _a.SaTileKey(geo);
                    _subTile = s.TileKey;
                    if (s.IsSub)
                    {
                        var tv = _a.Get(_a.Get(u, SmartAttackSpecs.SubParams), SmartAttackSpecs.SubTelegraphKn) as Array;
                        if (tv != null)
                        {
                            s.TelegraphKn = new double[tv.Length];
                            for (int i = 0; i < tv.Length; i++) s.TelegraphKn[i] = D(tv.GetValue(i)) ?? 0;
                        }
                        var pr = D(_a.Get(u, SmartAttackSpecs.SubCurrentPreset));
                        if (pr.HasValue) s.Preset = (int)pr.Value;
                        var env = _a.SingletonInstance(SmartAttackSpecs.EnvironmentType);
                        double? lh = env == null ? null : D(_a.Call(env, SmartAttackSpecs.EnvLayerHeight, geo));   // leak-ok: the layer at the own position (player knows it from the sonar)
                        if (lh.HasValue && lh.Value < 0) s.LayerFt = -lh.Value * SaFtPerUnity;
                    }
                }
                catch { }
                return s;
            }

            // ---- weapons ------------------------------------------------------------------------------------------

            private int AmmoCount(object u, string file)
            {
                var dict = _a.Get(u, SmartAttackSpecs.ObjAmmoCounts) as IEnumerable;
                if (dict == null) return 0;
                foreach (var kv in dict)
                    if (_a.Get(kv, CoreSpecs.PairKey) as string == file) return (int)(D(_a.Get(kv, CoreSpecs.PairValue)) ?? 0);
                return 0;
            }

            public IList<WireAmmo> WireAmmoFor(int uid, int trackId)
            {
                string code;
                return WireAmmoFor(uid, trackId, out code);
            }

            /// <summary>Wire-guided ammunition with count above zero that the game allows against this track. Empty + code (target_not_allowed|no_wire_torpedo|unit_not_found|unknown_target).</summary>
            public IList<WireAmmo> WireAmmoFor(int uid, int trackId, out string code)
            {
                code = null;
                var res = new List<WireAmmo>();
                var u = Unit(uid);
                if (u == null) { code = "unit_not_found"; return res; }
                var v = _a.FindContact(trackId);
                if (v == null || _a.IsGone(v)) { code = "unknown_target"; return res; }
                try
                {
                    var ai = _a.Get(u, SmartAttackSpecs.ObjAi);
                    object none = _a.GetStatic(SmartAttackSpecs.AmmoTypeNone);
                    var cands = _a.Call(ai, SmartAttackSpecs.AiAmmoForTarget, _a.Get(v, CoreSpecs.VehObject), none) as IEnumerable;   // leak-ok: the game's own check which ammunition can engage this target
                    if (cands != null)
                        foreach (var ammo in cands)
                        {
                            var w = _a.ReadWireAmmo(ammo);
                            if (w == null) continue;
                            w.Count = AmmoCount(u, w.File);
                            if (w.Count > 0) res.Add(w);
                        }
                    if (res.Count == 0) code = AnyWireTorpedo(u) ? "target_not_allowed" : "no_wire_torpedo";
                }
                catch { res.Clear(); code = "no_wire_torpedo"; }
                return res;
            }

            private bool AnyWireTorpedo(object u)
            {
                var dict = _a.Get(u, SmartAttackSpecs.ObjAmmoCounts) as IEnumerable;
                if (dict == null) return false;
                foreach (var kv in dict)
                {
                    try
                    {
                        string file = _a.Get(kv, CoreSpecs.PairKey) as string;
                        if ((int)(D(_a.Get(kv, CoreSpecs.PairValue)) ?? 0) <= 0) continue;
                        if (_a.ReadWireAmmo(_a.Call(u, SmartAttackSpecs.ObjGetAmmoByName, file)) != null) return true;
                    }
                    catch { }
                }
                return false;
            }

            // ---- tracks (the player's plotting table only) ---------------------------------------------------------

            public TrackInfo TrackInfoOf(object v)
            {
                if (v == null) return null;
                try
                {
                    var ti = new TrackInfo { Id = (int)(D(_a.Get(v, SmartAttackSpecs.VehId)) ?? -1), Relation = _a.Relation(v), Cls = _a.Get(_a.Get(v, SmartAttackSpecs.VehClass), SmartAttackSpecs.SourcedValue) as string };
                    string kind = _a.Kind(v);
                    ti.Domain = kind == "Submarine" ? "Subsurface" : kind;
                    bool gone = _a.IsGone(v);
                    bool live = BoolOf(_a.Get(v, SmartAttackSpecs.VehHasLiveTrack));
                    bool fan = _a.Get(v, SmartAttackSpecs.VehBearingFan) != null;
                    ti.State = gone ? "lost" : (live ? "live" : (fan ? "bearing_only" : "lost"));
                    var pos = _a.RxGet(v, SmartAttackSpecs.VehPosition);
                    ti.SameTile = pos != null && _subTile != null && _a.SaTileKey(pos) == _subTile;
                    return ti;
                }
                catch { return null; }
            }

            public TrackInfo Track(int trackId) { return TrackInfoOf(_a.FindContact(trackId)); }

            public TrackSample? Sample(int trackId)
            {
                var v = _a.FindContact(trackId);
                if (v == null) return null;
                try
                {
                    var pos = _a.RxGet(v, SmartAttackSpecs.VehPosition);
                    if (pos == null) return null;
                    var vel = _a.Get(v, SmartAttackSpecs.VehVelocity);   // grid-ok: smart-attack island (grid axes, same plane as SaPlane)
                    var s = new TrackSample
                    {
                        T = Now,
                        Pos = _a.SaPlane(pos),
                        VelKn = new SaVec((D(_a.Get(vel, SmartAttackSpecs.VecX)) ?? 0) * GameApi.SaKnPerUnitPerS, (D(_a.Get(vel, SmartAttackSpecs.VecZ)) ?? 0) * GameApi.SaKnPerUnitPerS)
                    };
                    var alt = _a.Get(v, SmartAttackSpecs.VehAltitude);   // leak-ok: the depth estimate of the player's track (a sonar/TMA product), null when none
                    if (alt != null)
                    {
                        var est = D(_a.Get(_a.Get(alt, SmartAttackSpecs.SourcedValue), SmartAttackSpecs.EstimateOf));
                        if (est.HasValue) s.DepthFt = -est.Value * SaFtPerUnity;
                    }
                    return s;
                }
                catch { return null; }
            }

            public int HitsOn(int trackId) { var l = _a.HeLedger; return l == null ? -1 : l.Count(trackId); }

            public IList<NeutralTrack> Neutrals(int exceptTrackId) { return _a.SmartNeutralTracks(exceptTrackId); }

            public string DirectCorridor(int uid, int trackId, out string hitsJson)
            {
                hitsJson = "[]";
                var u = Unit(uid);
                var v = _a.FindContact(trackId);
                if (u == null || v == null) return "neutral_check_failed";
                string failure;
                var hits = _a.NeutralNearHits(u, v, out failure);
                hitsJson = NeutralHitsJson(hits);
                if (failure != null) return "neutral_check_failed";
                return hits.Count > 0 ? "neutral_near" : null;
            }

            // ---- own submarine orders -----------------------------------------------------------------------------

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

            public string SetTelegraph(int uid, int t)
            {
                if (Unit(uid) == null) return "unit_not_found";
                try { return AckError(_a.Execute(new Dictionary<string, object> { { "cmd", "set_telegraph" }, { "uid", (double)uid }, { "telegraph", (double)t } })); }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string SetOwnDepthPreset(int uid, int preset)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try { _a.CallOpt(u, SmartAttackSpecs.SubSetPresetDepth, preset, false); return null; }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string SetOwnDepthFt(int uid, double ft)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try { _a.Call(u, SmartAttackSpecs.SubSetDepth, (float)(ft / SaFtPerUnity)); return null; }
                catch (Exception e) { return e.GetType().Name; }
            }

            public int GetLaunchSpeed(int uid, string ammo)
            {
                try
                {
                    var d = _a.Get(Unit(uid), SmartAttackSpecs.ObjLaunchSpeeds) as IDictionary;
                    if (d == null || ammo == null || !d.Contains(ammo)) return -1;
                    return (int)(D(_a.Get(d[ammo], SmartAttackSpecs.SourcedValue)) ?? -1);
                }
                catch { return -1; }
            }

            /// <summary>index below zero removes the entry (back to the game's automatic speed).</summary>
            public void SetLaunchSpeed(int uid, string ammo, int index)
            {
                try
                {
                    var d = _a.Get(Unit(uid), SmartAttackSpecs.ObjLaunchSpeeds) as IDictionary;
                    if (d == null || ammo == null) return;
                    if (index < 0) { if (d.Contains(ammo)) d.Remove(ammo); return; }
                    if (d.Contains(ammo))
                    {
                        _a.Set(d[ammo], SmartAttackSpecs.SourcedValue, index);
                        return;
                    }
                    var rpType = d.GetType().GetGenericArguments()[1];
                    var created = Activator.CreateInstance(rpType, new object[] { index });
                    d.Add(ammo, created);
                }
                catch { }
            }

            public string Fire(int uid, int trackId, string ammo, bool confirmNeutral)
            {
                try
                {
                    var cmd = new Dictionary<string, object>
                    {
                        { "cmd", "attack" }, { "uid", (double)uid }, { "target_id", (double)trackId }, { "weapon", ammo }, { "salvo", 1.0 },
                        { "confirm", true }, { "confirm_neutral", confirmNeutral }
                    };
                    return AckError(_a.Execute(cmd));
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            // ---- torpedoes ------------------------------------------------------------------------------------------

            private IEnumerable<object> Launched(int uid)
            {
                var u = Unit(uid);
                var systems = _a.Get(_a.Get(u, SmartAttackSpecs.ObjObp), SmartAttackSpecs.ObpWeaponSystems) as IEnumerable;
                if (systems == null) yield break;
                foreach (var ws in systems)
                {
                    var l = _a.Get(ws, SmartAttackSpecs.WsLaunched) as IEnumerable;
                    if (l == null) continue;
                    foreach (var w in l) if (w != null) yield return w;
                }
            }

            public ISet<int> WeaponIds(int uid)
            {
                var res = new HashSet<int>();
                try { foreach (var w in Launched(uid)) res.Add(_a.UidOf(w)); } catch { }
                return res;
            }

            public TorpSnap FindNewTorpedo(int uid, string ammo, ISet<int> before)
            {
                try
                {
                    foreach (var w in Launched(uid))
                    {
                        int id = _a.UidOf(w);
                        if (before != null && before.Contains(id)) continue;
                        if (!IsAlive(w) || BoolOf(_a.Get(w, SmartAttackSpecs.ObjIsDestroyed))) continue;
                        if (!_a.IsOfType(w, SmartAttackSpecs.TorpType)) continue;
                        if (ammo != null && _a.Get(_a.Get(w, SmartAttackSpecs.WbAp), SmartAttackSpecs.ApFileName) as string != ammo) continue;
                        _torps[id] = w;
                        return _a.ReadTorpedo(w);
                    }
                }
                catch { }
                return null;
            }

            public TorpSnap Torpedo(int torpId)
            {
                object t;
                if (!_torps.TryGetValue(torpId, out t)) return null;
                var s = _a.ReadTorpedo(t);
                if (s != null && !s.Alive) _torps.Remove(torpId);
                return s;
            }

            private object TorpObj(int torpId)
            {
                object t;
                if (!_torps.TryGetValue(torpId, out t) || !IsAlive(t)) throw new InvalidOperationException("torpedo " + torpId + " gone");
                return t;
            }

            public void SetWireDepth(int torpId, double ft) { _a.SetWireDepth(TorpObj(torpId), ft); }
            public void SetWireSpeed(int torpId, int index) { _a.SetWireSpeed(TorpObj(torpId), index); }
            public void SetAimShift(int torpId, SaVec? shiftNm) { _a.SetAimShift(TorpObj(torpId), shiftNm); }

            // ---- control memory (T-091) -------------------------------------------------------------------------------

            public void RememberField(int uid, string key, string current)
            {
                var u = Unit(uid);
                if (u != null) _a._ctlMem.RememberField(u, key, current);
            }

            public void NoteFieldSet(int uid, string key, string value)
            {
                var u = Unit(uid);
                if (u != null) _a._ctlMem.NoteFieldSet(u, key, value);
            }

            public string FieldNow(int uid, string key)
            {
                var u = Unit(uid);
                if (u == null) return null;
                try
                {
                    if (key == "telegraph") { int? t = _a.Telegraph(u); return t.HasValue ? t.Value.ToString() : null; }
                    if (key == "sa_depth") { var p = D(_a.Get(u, SmartAttackSpecs.SubCurrentPreset)); return p.HasValue ? ((int)p.Value).ToString() : null; }
                }
                catch { }
                return null;
            }

            // ---- output ---------------------------------------------------------------------------------------------

            public void Event(string name, string dataJson)
            {
                try { _a.EvLine(name, dataJson); } catch { }
            }

            public void Message(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                _a.LastActionSummary = text;
                Log("message: " + text);
                _a.SmartRaiseAlert(text);
            }

            public void Log(string line)
            {
                // T-154 E3: the BepInEx log of the host (the Unity player log is not written on this setup, only the host log is read)
                try { var s = _a.SmartLogSink; if (s != null) s(SmartAttackMath.LogPrefixed(line)); } catch { }
            }
        }
    }
}
