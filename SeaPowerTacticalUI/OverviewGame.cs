using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Fields the Overview window (Overview.cs) writes for state.overview.</summary>
    internal sealed class OverviewPanelInfo
    {
        public bool Collapsed = false;
        public string Sort = "dist:asc";
        public int SelectedTrack = -1;
        public double DrawMs = 0;
        public string IconSource = "none";
        public int IconsDrawn;
        public string Hotkey;                    // null = hotkey off
        public string HotkeyState = "unknown";
        public string Tip;          // T-183: the tip shown on the last repaint (null = none)
        public string DrawError;    // T-183: the first exception of DrawWindow (ErrLine), null = none
        public string Rect;         // T-183: the last rect spec written by Save (null = not written in this session)
        public string Tab = "contacts";   // T-242: the active tab (contacts | own)
        public int OwnRows, SelectedUid;  // T-242: rows of the own tab, UniqueID of the selected own unit (0 = none)
        public double OwnBuildMs;
        public List<string> IconsMissing = new List<string>();
    }

    /// <summary>
    /// Game side of the Overview panel: contacts snapshot, rows, centre-map / select-contact, mouse block, "last own selected unit".
    /// Reflection only (no Unity drawing), so the offline selfcheck covers every member it touches.
    /// </summary>
    internal sealed partial class GameApi
    {
        internal readonly OverviewPanelInfo Panel = new OverviewPanelInfo();
        /// <summary>Config switches written by Plugin (Overview section).</summary>
        internal bool OverviewCenterSecondMap = true;
        /// <summary>T-092: [Overview] Debug, one log line per contact row when its key changes, plus a summary every 60 s (T-107 W1).</summary>
        internal bool OverviewDebug;
        private readonly OverviewDebugGate _ovDebugGate = new OverviewDebugGate();
        /// <summary>Last frame built by the window (Overview.cs); read for state.overview.</summary>
        internal OverviewFrame OverviewLastFrame = null;
        internal double OverviewRefUid = -1;

        private bool _overviewOn;

        internal bool OverviewOn { get { return _overviewOn; } }

        // self-check: OverviewSpecs (registry)

        private double _loadCheckAt;
        private bool _loadingCached;

        /// <summary>True while the game shows a loading screen (SeaPower.LoadScreen percent 0..100). The mod windows hide then; read at most 4 times a second.</summary>
        internal bool LoadScreenActive()
        {
            double now = Environment.TickCount / 1000.0;
            if (now - _loadCheckAt < 0.25 && now >= _loadCheckAt) return _loadingCached;
            _loadCheckAt = now;
            try { _loadingCached = LoadScreenShown(); } catch { _loadingCached = false; }
            return _loadingCached;
        }

        /// <summary>T-199: LoadScreen.cameraObject.enabled (the game's own sign); the percent rule of Scene.IsLoading is the fallback when the member is missing.</summary>
        private bool LoadScreenShown()
        {
            double pc; string tx;
            IsLoading(out pc, out tx);   // fills the percent and the text; its own verdict is the old rule
            return LoadScreenMath.Active(LoadCamera(), pc, tx);
        }

        private bool? LoadCamera()
        {
            bool? cam = null;
            try
            {
                if (Present(CoreSpecs.LoadCamera) && Present(CoreSpecs.LoadScreenType))
                {
                    var b = Get(SingletonInstance(CoreSpecs.LoadScreenType), CoreSpecs.LoadCamera) as UnityEngine.Behaviour;
                    if (b != null) cam = b.enabled;
                }
            }
            catch { cam = null; }
            return cam;
        }

        private double _loadDetailAt;
        private string _loadDetail = "cam=? pct=? text=";

        /// <summary>T-232: "cam=1|0|? pct=NN text=&lt;up to 40 chars&gt;" for the status line of the post-load gate (what the load-screen sign sees); read at most 4 times a second.</summary>
        internal string LoadScreenDetail()
        {
            double now = Environment.TickCount / 1000.0;
            if (now - _loadDetailAt < 0.25 && now >= _loadDetailAt) return _loadDetail;
            _loadDetailAt = now;
            try
            {
                double pc; string tx;
                IsLoading(out pc, out tx);
                bool? cam = LoadCamera();
                string t = (tx ?? "").Replace('\n', ' ').Replace('\r', ' ');
                if (t.Length > 40) t = t.Substring(0, 40);
                _loadDetail = "cam=" + (cam.HasValue ? (cam.Value ? "1" : "0") : "?") + " pct=" + Math.Round(pc).ToString(System.Globalization.CultureInfo.InvariantCulture) + " text=" + t;
            }
            catch { _loadDetail = "cam=? pct=? text="; }
            return _loadDetail;
        }

        private double _menuCheckAt = -1;

        /// <summary>The game's own menu over the mission: GameMenuMath.None | Menu (pause, options, confirm, briefing) | Debrief (After Action Report)
        /// | Message (a mission message popup).</summary>
        internal string GameMenu { get; private set; } = GameMenuMath.None;

        /// <summary>A game menu is open: the mod windows hide, the mouse block is lifted, the map render texture and overlay pumps pause.</summary>
        internal bool GameMenuHidden { get { return GameMenuMath.Hides(GameMenu); } }

        /// <summary>Reads InputHandler.menuOpen and MenuSystemViewModel.CurrentWindow at most 10 times a second (CoreSpecs, all optional:
        /// a missing member keeps the old behaviour). Returns the log line of a change, null when nothing changed.</summary>
        internal string PumpGameMenu(double realNow)
        {
            if (realNow - _menuCheckAt < 0.1 && realNow >= _menuCheckAt) return null;
            _menuCheckAt = realNow;
            string cur = GameMenuMath.None;
            try
            {
                bool open = BoolOf(Get(SingletonInstance(CoreSpecs.InputType), CoreSpecs.GameMenuOpen));
                bool debrief = false;
                if (Present(CoreSpecs.MenuCurrentWindow) && Present(CoreSpecs.DebriefViewType))
                    debrief = IsOfType(Get(SingletonInstance(CoreSpecs.MenuSystemType), CoreSpecs.MenuCurrentWindow), CoreSpecs.DebriefViewType);
                cur = GameMenuMath.State(open, debrief, !open && !debrief && MessagePopupOpen());
            }
            catch { cur = GameMenuMath.None; }
            string line = GameMenuMath.LogLine(GameMenu, cur);
            GameMenu = cur;
            return line;
        }

        /// <summary>A mission message popup is up: the message pause of the InputHandler, or a SeapowerUI.Modal in the game's window host.</summary>
        private bool MessagePopupOpen()
        {
            try { if (Present(CoreSpecs.GameMessagePause) && BoolOf(Get(SingletonInstance(CoreSpecs.InputType), CoreSpecs.GameMessagePause))) return true; }
            catch { }
            try
            {
                if (!Present(CoreSpecs.WindowHostWindows) || !Present(CoreSpecs.ModalType)) return false;
                var host = Get(SingletonInstance(CoreSpecs.WindowHostManagerType), CoreSpecs.WindowHostCurrent);
                var ws = host == null ? null : Get(host, CoreSpecs.WindowHostWindows) as System.Collections.IEnumerable;
                if (ws == null) return false;
                int n = 0;
                foreach (var w in ws) { if (IsOfType(w, CoreSpecs.ModalType)) return true; if (++n > 64) break; }
            }
            catch { }
            return false;
        }

        /// <summary>True when the game's UI is hidden or a modal state is on (HUD off, Esc menu, message pause): the panel is not drawn then.</summary>
        internal bool OverviewUiHidden()
        {
            try
            {
                if (Present(OverviewSpecs.MainVmHudVisible) && !BoolOf(CallStatic(OverviewSpecs.MainVmHudVisible))) return true;
                var ih = SingletonInstance(CoreSpecs.InputType);
                return BoolOf(Get(ih, OverviewSpecs.InputMenuOpen)) || BoolOf(Get(ih, OverviewSpecs.InputMessagePause));
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- rows

        /// <summary>One pass -> rows (unsorted). refUnit = the unit the distances are measured from (SelectedUnit, last own when a contact is selected).</summary>
        internal OverviewFrame BuildOverview(ClosingFilter f, string lang, bool showWeapons, out object refUnit)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string src;
            refUnit = SelectedUnit(out src);
            if (refUnit != null && (IsFalse(Get(refUnit, OverviewSpecs.ObjIsPlayer)))) refUnit = null;
            var snap = Snapshot(true);
            double lat0, lon0, h; double? rl = null, ro = null, vox = null, voz = null;
            if (refUnit != null && OwnPos(refUnit, out lat0, out lon0, out h))
            {
                rl = lat0; ro = lon0;
                double gvx, gvz;   // T-308: true axes at the own unit
                if (GridVelTrue(refUnit, OverviewSpecs.ObjVelocity, OverviewSpecs.Vec3X, OverviewSpecs.Vec3Z, lat0, lon0, out gvx, out gvz)) { vox = gvx; voz = gvz; }
            }
            var fr = new OverviewFrame { RefName = refUnit == null ? null : RxGet(refUnit, OverviewSpecs.ObjName) as string };
            var alive = new List<int>();
            HashSet<object> mainVms = OverviewDebug ? MainMapViewModels() : null;
            foreach (var t in snap.Tracks)
            {
                if (!OverviewMath.Include(t, showWeapons)) continue;
                double seen; double? age = null;
                if (t.Live) age = 0; else if (_liveSeen.TryGetValue(t.Id, out seen)) age = Math.Max(0, snap.T - seen);
                var idr = Identify.Get(t.Id);
                var row = OverviewMath.MakeRow(t, rl, ro, vox, voz, f, snap.T, age, lang, IdentifyText.Label(idr, true), IdentifyText.Rank(idr));
                fr.Rows.Add(row);
                if (OverviewDebug) OvDebugRow(t, row, snap.T, mainVms, snap.VehicleById);
                alive.Add(t.Id);
                if (t.Side == "hostile") fr.Hostile++; else fr.Unknown++;
            }
            if (f != null) f.Forget(alive);
            if (OverviewDebug)
            {
                _ovDebugGate.Forget(alive);
                int changed;
                if (_ovDebugGate.SummaryDue(snap.T, out changed)) Log("overview debug: rows=" + fr.Rows.Count + " hostile=" + fr.Hostile + " unknown=" + fr.Unknown + " changed=" + changed + " in " + (int)OverviewDebugGate.SummarySeconds + " s");
            }
            if (_liveSeen.Count > 4 * (alive.Count + 16)) { var keep = new HashSet<int>(alive); foreach (int k in new List<int>(_liveSeen.Keys)) if (!keep.Contains(k)) _liveSeen.Remove(k); }
            fr.BuildMs = sw.Elapsed.TotalMilliseconds;
            OverviewRefUid = refUnit == null ? -1 : UidOf(refUnit);
            return fr;
        }

        /// <summary>The main map's view model of a vehicle (the beta has no Vehicle.ViewModel): MapKnownUnits.AllUnits entry whose Unit is this vehicle. null on any error.</summary>
        private object MainMapUnitVm(object vehicle)
        {
            try
            {
                if (vehicle == null) return null;
                var mgvm = GetStatic(OverviewSpecs.MainVm);
                var all = Get(Get(Get(mgvm, OverviewSpecs.MainVmMap), OverviewSpecs.SceneKnownUnits), OverviewSpecs.KnownAllUnits) as System.Collections.IEnumerable;
                if (all == null) return null;
                foreach (var u in all) if (u != null && ReferenceEquals(Get(u, OverviewSpecs.UnitVehicle), vehicle)) return u;
            }
            catch { }
            return null;
        }

        private HashSet<object> MainMapViewModels()
        {
            try
            {
                var mgvm = GetStatic(OverviewSpecs.MainVm);
                var all = Get(Get(Get(mgvm, OverviewSpecs.MainVmMap), OverviewSpecs.SceneKnownUnits), OverviewSpecs.KnownAllUnits) as System.Collections.IEnumerable;
                if (all == null) return null;
                var set = new HashSet<object>(new RefEq());
                foreach (var u in all) if (u != null) set.Add(u);
                return set;
            }
            catch { return null; }
        }

        private void OvDebugRow(OvTrack t, OvRow row, double now, HashSet<object> mainVms, Dictionary<int, object> vehicles)
        {
            try
            {
                object v; object vm = null;
                if (vehicles.TryGetValue(t.Id, out v)) vm = MainMapUnitVm(v);
                if (vm != null) t.VmMain = true;
                string key = t.Side + "|" + t.Posture + "|" + t.Colour + "|" + t.Live + "|" + t.Dormant + "|" + t.Kind + "|" + t.Class;
                if (!_ovDebugGate.Due(t.Id, key)) return;
                Log(OverviewMath.DebugLine(t, row));
            }
            catch { }
        }

        // ---------------------------------------------------------------- centre / select

        private bool GeoOf(object pos, out double lat, out double lon)
        {
            lat = lon = 0;
            var la = D(Get(pos, CoreSpecs.GeoLat)); var lo = D(Get(pos, CoreSpecs.GeoLon));
            if (!la.HasValue || !lo.HasValue) return false;
            lat = la.Value; lon = lo.Value;
            return true;
        }

        /// <summary>Sets a property by descriptor; a missing or read-only member is an error (the callers catch it).</summary>
        internal void SetMember(object o, MemberSpec s, object value)
        {
            if (!Set(o, s, value)) throw new MissingMemberException(s.Type, s.Member);
        }

        internal static void SetProp(object o, string name, object value)
        {
            var p = o.GetType().GetProperty(name, AllInst);
            if (p == null || !p.CanWrite) throw new MissingMemberException(o.GetType().Name, name);
            p.SetValue(o, value, null);
        }

        /// <summary>Moves the tactical map(s) to a track / own unit / point. Returns the reply fragment, or null with err = protocol error code.</summary>
        internal string CenterMap(int? trackId, int? uid, double? lat, double? lon, string source, out string err)
        {
            err = null;
            double la = 0, lo = 0;
            if (trackId.HasValue)
            {
                var v = FindContact(trackId.Value);
                if (v == null || !GeoOf(RxGet(v, OverviewSpecs.VehPosition), out la, out lo)) { err = "unknown_target"; return null; }
            }
            else if (uid.HasValue)
            {
                var u = FindOwnUnit(uid.Value);
                double h;
                if (u == null || !OwnPos(u, out la, out lo, out h)) { err = "unknown_target"; return null; }
            }
            else if (lat.HasValue && lon.HasValue) { la = lat.Value; lo = lon.Value; }
            else { err = "bad_args"; return null; }
            var mgvm = GetStatic(OverviewSpecs.MainVm);
            var map = Get(mgvm, OverviewSpecs.MainVmMap);
            var disp = Get(map, OverviewSpecs.SceneDisplayMap);
            if (disp == null) { err = "unsupported"; return null; }
            var geo = MakeGeo(la, lo, 0);
            SetMember(disp, OverviewSpecs.MapCenter, geo);
            if (OverviewCenterSecondMap)
            {
                var second = Get(Get(mgvm, OverviewSpecs.MainVmSecondMap), OverviewSpecs.SceneDisplayMap);
                if (second != null) { try { SetMember(second, OverviewSpecs.MapCenter, geo); } catch { } }
            }
            bool open = BoolOf(Get(mgvm, OverviewSpecs.MainVmMapVisible));
            if (EventsAvailable)
                EvLine("overview_action", "{\"action\":\"center\",\"track_id\":" + (trackId.HasValue ? trackId.Value.ToString() : "null") + ",\"source\":" + Json.Str(source) + "}");
            return "\"lat\":" + Json.Num(Math.Round(la, 5)) + ",\"lon\":" + Json.Num(Math.Round(lo, 5)) + ",\"map_open\":" + Json.Bool(open);
        }

        /// <summary>Selects a track the way a click on the map does. inGame=false only marks it in the window. Returns the reply fragment, or null with err.</summary>
        internal string SelectContact(int trackId, bool inGame, string source, out string err)
        {
            err = null;
            var v = FindContact(trackId);
            if (v == null) { err = "unknown_target"; return null; }
            bool switched = false;
            if (inGame)
            {
                var rp = RenderPositionInstance();
                object before = null;
                try { if (rp != null) before = Call(rp, OverviewSpecs.RenderSelected); } catch { }
                var vm = MainMapUnitVm(v);
                bool done = false;
                if (vm != null)
                {
                    try { SetMember(vm, OverviewSpecs.UnitSelected, true); done = true; } catch { }
                }
                if (!done)
                {
                    var map = Get(GetStatic(OverviewSpecs.MainVm), OverviewSpecs.MainVmMap);
                    RxSet(map, OverviewSpecs.SceneSelectedObject, v);
                }
                try
                {
                    object after = rp == null ? null : Call(rp, OverviewSpecs.RenderSelected);
                    switched = IsAlive(after) && !ReferenceEquals(after, before) && ReferenceEquals(after, Get(v, OverviewSpecs.VehObject));
                }
                catch { }
            }
            Panel.SelectedTrack = trackId;
            if (EventsAvailable)
                EvLine("overview_action", "{\"action\":\"select\",\"track_id\":" + trackId + ",\"source\":" + Json.Str(source) + "}");
            return "\"track_id\":" + trackId + ",\"camera_switched\":" + Json.Bool(switched);
        }

        // ---------------------------------------------------------------- mouse block, hotkey

        /// <summary>T-154 A3: the game flag setMouseIsOverUIWindow is the OR of the owners (UiMouseBlock); the game is called only on an edge. false is safe without the game.</summary>
        internal void SetMouseBlock(string owner, bool on)
        {
            if (UiMouseBlock.Game == null) UiMouseBlock.Game = ApplyMouseFlag;
            UiMouseBlock.Set(owner, on);
        }

        private void ApplyMouseFlag(bool on)
        {
            if (!on && !Frame.InMission) return;
            var mcs = SingletonInstance(CoreSpecs.MouseStateType);
            if (mcs != null) Call(mcs, OverviewSpecs.MouseOverUi, on);
        }

        internal void SetOverviewBlock(bool on) { SetMouseBlock("overview", on); }

        /// <summary>Game key bindings (codes + action names, same order). false when InputHandler/KeyBindings are not there yet (before a mission) or on any exception.</summary>
        private bool ReadKeyBindings(out List<string[]> codes, out List<string> names)
        {
            codes = null; names = null;
            try
            {
                var ih = SingletonInstance(CoreSpecs.InputType);
                var list = Get(ih, OverviewSpecs.KeyBindings) as IEnumerable;
                if (list == null) return false;
                var all = new List<string[]>();
                var nm = new List<string>();
                foreach (var kb in list)
                {
                    var kc = Get(kb, OverviewSpecs.BindingKeyCodes) as IEnumerable;
                    if (kc == null) continue;
                    var one = new List<string>();
                    foreach (var c in kc) one.Add(c.ToString());
                    all.Add(one.ToArray());
                    object act = Get(kb, OverviewSpecs.BindingAction) ?? Get(kb, OverviewSpecs.BindingKeyName);
                    nm.Add(act == null ? null : act.ToString());
                }
                codes = all; names = nm;
                return true;
            }
            catch { return false; }
        }

        /// <summary>"free" | "taken" | "unknown" (InputHandler/KeyBindings not there yet); conflict names the binding (rule: OverviewMath.HotkeyVerdict).</summary>
        internal string OverviewKeyCheck(string[] combo, out string conflict)
        {
            conflict = null;
            try
            {
                List<string[]> all; List<string> names;
                if (!ReadKeyBindings(out all, out names)) return "unknown";
                return OverviewMath.HotkeyVerdict(combo, all, names, out conflict);
            }
            catch { return "unknown"; }
        }

        // ---------------------------------------------------------------- state / commands

        internal void SetOverview(bool on, string source)
        {
            if (_overviewOn == on) return;
            _overviewOn = on;
            Log("overview panel " + (on ? "on" : "off") + " (" + source + ")");
        }

        internal string OverviewStateJson()
        {
            var f = OverviewLastFrame;
            var sb = new StringBuilder("{\"open\":").Append(Json.Bool(_overviewOn));
            sb.Append(",\"collapsed\":").Append(Json.Bool(Panel.Collapsed));
            sb.Append(",\"rows\":").Append(f == null || !_overviewOn ? 0 : f.Rows.Count);
            sb.Append(",\"hostile\":").Append(f == null || !_overviewOn ? 0 : f.Hostile);
            sb.Append(",\"unknown\":").Append(f == null || !_overviewOn ? 0 : f.Unknown);
            sb.Append(",\"identify_count\":").Append(Identify.All.Count);
            sb.Append(",\"sort\":").Append(Json.Str(Panel.Sort));
            sb.Append(",\"selected_track\":").Append(Panel.SelectedTrack >= 0 ? Panel.SelectedTrack.ToString() : "null");
            sb.Append(",\"ref_uid\":").Append(OverviewRefUid >= 0 ? Json.Num(OverviewRefUid) : "null");
            sb.Append(",\"draw_ms\":").Append(Json.Num(Math.Round(Panel.DrawMs, 2)));
            sb.Append(",\"build_ms\":").Append(Json.Num(f == null ? 0 : Math.Round(f.BuildMs, 2)));
            sb.Append(",\"icon_source\":").Append(Json.Str(Panel.IconSource));
            sb.Append(",\"icons_drawn\":").Append(Panel.IconsDrawn);
            sb.Append(",\"hotkey\":").Append(Panel.Hotkey == null ? "null" : Json.Str(Panel.Hotkey));
            sb.Append(",\"hotkey_state\":").Append(Json.Str(Panel.HotkeyState));
            sb.Append(",\"tip\":").Append(Json.Str(Panel.Tip));
            sb.Append(",\"draw_error\":").Append(Json.Str(Panel.DrawError));
            sb.Append(",\"rect\":").Append(Json.Str(Panel.Rect));
            sb.Append(",\"tab\":").Append(Json.Str(Panel.Tab));
            sb.Append(",\"own_rows\":").Append(Panel.OwnRows);
            sb.Append(",\"own_build_ms\":").Append(Json.Num(Math.Round(Panel.OwnBuildMs, 2)));
            sb.Append(",\"selected_uid\":").Append(Panel.SelectedUid > 0 ? Panel.SelectedUid.ToString() : "null");
            sb.Append(",\"icons_missing\":[");
            for (int i = 0; i < Panel.IconsMissing.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(Panel.IconsMissing[i])); }
            return sb.Append("]}").ToString();
        }

        private string OverviewCmd(string id, Dictionary<string, object> cmd)
        {
            bool on = !_overviewOn;
            if (cmd.ContainsKey("on") && cmd["on"] != null)
            {
                if (!(cmd["on"] is bool)) return Err(id, "bad_args", "on (true/false) optional");
                on = (bool)cmd["on"];
            }
            Need("overview");
            bool previous = _overviewOn;
            SetOverview(on, "bot");
            return Ok(id, "\"on\":" + Json.Bool(_overviewOn) + ",\"previous\":" + Json.Bool(previous));
        }

        private static int? IntArg(Dictionary<string, object> cmd, string key, out bool bad)
        {
            bad = false;
            object o;
            if (!cmd.TryGetValue(key, out o) || o == null) return null;
            var d = D(o);
            if (!(o is double || o is int || o is long) || !d.HasValue || d.Value != Math.Floor(d.Value) || d.Value < 0 || d.Value > 1e9) { bad = true; return null; }
            return (int)d.Value;
        }

        private string CenterMapCmd(string id, Dictionary<string, object> cmd)
        {
            bool bad1, bad2;
            int? track = IntArg(cmd, "track_id", out bad1), uid = IntArg(cmd, "uid", out bad2);
            double? lat = cmd.ContainsKey("lat") && cmd["lat"] != null ? D(cmd["lat"]) : null;
            double? lon = cmd.ContainsKey("lon") && cmd["lon"] != null ? D(cmd["lon"]) : null;
            if (bad1 || bad2) return Err(id, "bad_args", "track_id/uid: non-negative integer");
            if (lat.HasValue != lon.HasValue) return Err(id, "bad_args", "lat and lon go together");
            if (lat.HasValue && (lat.Value < -90 || lat.Value > 90 || lon.Value < -180 || lon.Value > 180)) return Err(id, "bad_args", "lat -90..90, lon -180..180");
            if (!track.HasValue && !uid.HasValue && !lat.HasValue) return Err(id, "bad_args", "track_id, uid or lat+lon required");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            Need("overview_center");
            string err;
            var r = CenterMap(track, uid, lat, lon, "bot", out err);
            return r == null ? Err(id, err, null) : Ok(id, r);
        }

        private string SelectContactCmd(string id, Dictionary<string, object> cmd)
        {
            bool bad;
            int? track = IntArg(cmd, "track_id", out bad);
            if (bad || !track.HasValue) return Err(id, "bad_args", "track_id: non-negative integer required");
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            Need("overview_select");
            string err;
            var r = SelectContact(track.Value, true, "bot", out err);
            return r == null ? Err(id, err, null) : Ok(id, r);
        }
    }
}
