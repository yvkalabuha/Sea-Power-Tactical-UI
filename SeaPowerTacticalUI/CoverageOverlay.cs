using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// Radar coverage over the tactical map (T-066, T-090). The projection comes from MapControl.MapBase through reflection
    /// (LocationToView + PointToScreen, a 3-point affine fit, checked by a 4th point). The geometry is drawn either by OnGUI on top of
    /// everything (imgui fallback) or into the render texture of CoverageLayer (DrawGeo, under the game's windows, ADR-0034).
    /// Needs Unity rendering, so it is NOT part of the offline selfcheck; the pure parts live in CoverageMath. OnGUI never throws.
    /// </summary>
    internal sealed class CoverageOverlay
    {
        private readonly GameApi _api;
        private readonly CoverageConfig _cfg;
        private readonly Action<string> _log;
        private Material _mat;
        private Ui.HotkeyBinding _binding;
        private Ui.IKeyInput _input;
        private Ui.IHotkeyGame _keyGame;
        private List<CoverageMath.RasterRun> _runs;
        private int _runsVersion = -1;
        private object _map;
        private MapTreeWalk _walk;      // T-127: the tree walk of the map search, one step per frame
        private readonly MapScanSchedule _sched = new MapScanSchedule();
        private readonly object[] _a1 = new object[1], _a2 = new object[2];   // Invoke arguments, reused (main thread only)
        private object[] _boxedIdx = new object[0];   // boxed child indexes, grown on demand
        private int _resultFrame = -1; private bool _resultOk;   // TryFindMap: one answer per Time.frameCount
        private int _stepFrame = -1;    // one walk step per frame
        private bool _lastOverlayOn;
        private Type _tPoint;           // Noesis.Point, once
        private object _mapWindow;      // nearest SeapowerUI.Window above the picked map, null for a bare map
        private GUIStyle _style;
        private Color[] _tierColors;
        private Color _greyColor;
        private float _lastWarn = -1e9f;
        private bool _typesReady;
        private Type _tVtc, _tMainView;
        private MethodInfo _mChildCount, _mChild, _mHitTest, _mParent;
        private bool _mapFound;
        private double _pxScale = 1.0;   // Noesis screen units per pixel, calibrated on the root view
        private string _lastPick = "";
        private float _lastDiag = -1e9f;
        private object _root;
        private float _nextAncCheck;
        private bool _premul;   // true while drawing into the render texture: premultiplied alpha (One, OneMinusSrcAlpha)
        private Material _matPm;

        public bool MapFound { get { return _mapFound; } }

        /// <summary>T-127: state of the map search for the heartbeat: idle | wait | scan | parked.</summary>
        internal string ScanText { get { return _sched.Text; } }

        /// <summary>T-113: draw also on the main map when its window is not maximised (the Tactical Display in the 3D view). Bound by the integrator.</summary>
        internal bool AllowSmallWindow = true;
        /// <summary>T-228: radar legend switch (set by RadarFeature); null = always shown.</summary>
        public LegendSwitch Legend;
        internal bool LegendVisible { get { return Legend == null || Legend.Visible; } }

        /// <summary>T-113: the picked map sits in a maximised main map window (full-screen map); false for the small 3D window.</summary>
        internal bool MapMaximized
        {
            get
            {
                try { return _mapWindow == null || GameApi.BoolOf2(_api.Get(_mapWindow, RadarSpecs.WindowMaximized)); }
                catch { return false; }
            }
        }

        /// <summary>MainGameView.Instance.DataContext.Map: the view model of the one main tactical map (SecondMap is another instance).</summary>
        private object MainMapVm()
        {
            try
            {
                var root = MainViewRoot();
                var dc = root == null ? null : _api.Get(root, RadarSpecs.ElementDataContext);
                return dc == null ? null : _api.Get(dc, RadarSpecs.MainVmMap);
            }
            catch { return null; }
        }

        private static bool IsTypeNamed(object o, string fullName)
        {
            if (o == null) return false;
            for (var t = o.GetType(); t != null; t = t.BaseType) if (t.FullName == fullName) return true;
            return false;
        }

        /// <summary>"main" when the window shows the main map view model, "other" otherwise.</summary>
        private string HostOf(object window, object mainVm, ref bool fallbackNoted)
        {
            if (window == null) return "none";
            var dc = _api.Get(window, RadarSpecs.ElementDataContext);
            if (dc == null) return "other";
            if (mainVm != null) return ReferenceEquals(dc, mainVm) ? "main" : "other";
            // no MainGameViewModel.Map: any window of a SceneMapViewModel whose IsFullscreen matches Maximized
            if (!IsTypeNamed(dc, RadarSpecs.SceneMapType.Type) && !dc.GetType().Name.Contains("SceneMapViewModel")) return "other";
            if (!fallbackNoted) { fallbackNoted = true; _log("radar overlay: MainGameViewModel.Map unavailable, matching the window by SceneMapViewModel"); }
            return GameApi.BoolOf2(_api.Get(dc, RadarSpecs.SceneMapFullscreen)) == GameApi.BoolOf2(_api.Get(window, RadarSpecs.WindowMaximized)) ? "main" : "other";
        }

        /// <summary>Calibrated Noesis units per pixel (1.0 on this build).</summary>
        internal double PxScale { get { return _pxScale; } }

        public CoverageOverlay(GameApi api, CoverageConfig cfg, Action<string> log)
        {
            _api = api; _cfg = cfg; _log = log ?? (m => { });
            Snaps = new MapSnapReader(api, _log);
            _fallbackFn = FallbackAffine;
        }

        // ---------- T-309: exact projector snapshot (ADR-0119) ----------

        /// <summary>Reads the live MapBase into a MapViewSnap once a frame and checks it against the game (T-309).</summary>
        internal MapSnapReader Snaps;
        private readonly Func<Affine2?> _fallbackFn;
        private object _fbMap; private double _fbLat = double.NaN, _fbLon = double.NaN;
        private int _lpxFrame = -1; private object _lpxMap; private MapPx _lpx; private readonly double[] _idOut = { 1, 0, 0, 0, 1, 0 };

        private Affine2? FallbackAffine()
        {
            if (double.IsNaN(_fbLat) || double.IsNaN(_fbLon)) return null;
            Affine2 g; double w, h;
            return TryLocalProjection(_fbMap, _fbLat, _fbLon, out g, out w, out h) ? (Affine2?)g : null;
        }

        /// <summary>The snapshot of this frame. refLat/refLon only feed the affine fit of a flat map (Kind = Affine); an exact (Ortho) map ignores them.</summary>
        internal bool TrySnap(object map, double refLat, double refLon, out MapViewSnap s)
        {
            _fbMap = map; _fbLat = refLat; _fbLon = refLon;
            try { return Snaps.Read(map, Time.frameCount, _fallbackFn, out s); }
            catch (Exception e) { Warn("snapshot: " + e.Message); s = default(MapViewSnap); return false; }
        }

        internal bool TrySnap(object map, out MapViewSnap s) { return TrySnap(map, double.NaN, double.NaN, out s); }

        /// <summary>Geo -> LOCAL map units (the output of the pixel is the same space as TryPixelToLocal / LocationToView), once per frame.
        /// A flat map needs refLat/refLon for its fit (NaN = not available: false).</summary>
        internal bool TryLocalPx(object map, out MapPx px, double refLat = double.NaN, double refLon = double.NaN)
        {
            px = null;
            if (map == null) return false;
            int frame = Time.frameCount;
            if (frame == _lpxFrame && ReferenceEquals(map, _lpxMap) && _lpx != null) { px = _lpx; return true; }
            MapViewSnap s;
            if (!TrySnap(map, refLat, refLon, out s)) return false;
            px = Snaps.Reflect && s.Kind == MapProjKind.Ortho ? MapPx.Reflect(s, Snaps.ReflectFn(map), _idOut, (int)s.MapW, (int)s.MapH) : MapPx.Create(s, _idOut, (int)s.MapW, (int)s.MapH);
            _lpx = px; _lpxFrame = frame; _lpxMap = map;
            return true;
        }

        /// <summary>IMGUI: geo -> screen pixels (y from the top) through three PointToScreen samples of the map; rect = the map in screen pixels.</summary>
        internal bool TryScreenPx(object map, out MapPx px, out double[] rect)
        {
            px = null; rect = null;
            try
            {
                MapViewSnap s;
                if (!TrySnap(map, out s) || s.Kind == MapProjKind.Affine) return false;
                var toScreen = FindToScreen(map);
                if (toScreen == null) return false;
                var tPoint = PointType();
                double w = s.MapW, h = s.MapH, x0, y0, x1, y1, x2, y2;
                if (!ScreenPoint(map, tPoint, toScreen, 0, 0, out x0, out y0) || !ScreenPoint(map, tPoint, toScreen, w, 0, out x1, out y1) || !ScreenPoint(map, tPoint, toScreen, 0, h, out x2, out y2)) return false;
                var o = new[] { (x1 - x0) / w, (x2 - x0) / h, x0, (y1 - y0) / w, (y2 - y0) / h, y0 };
                px = Snaps.Reflect ? MapPx.Reflect(s, Snaps.ReflectFn(map), o, Screen.width, Screen.height) : MapPx.Create(s, o, Screen.width, Screen.height);
                double xe, ye;
                if (!ScreenPoint(map, tPoint, toScreen, w, h, out xe, out ye)) return false;
                rect = new[] { Math.Min(x0, xe), Math.Min(y0, ye), Math.Abs(xe - x0), Math.Abs(ye - y0) };
                return rect[2] > 1 && rect[3] > 1;
            }
            catch (Exception e) { Warn("screen projector: " + e.Message); return false; }
        }

        /// <summary>Shader, hotkey and colours. The Noesis types are resolved lazily (their assembly may load later).</summary>
        public bool Init(out string error)
        {
            error = null;
            try
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) { error = "shader Hidden/Internal-Colored not found"; return false; }
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _matPm = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _matPm.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                _matPm.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _matPm.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _matPm.SetInt("_ZWrite", 0);
                _matPm.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                ParseColors();
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        private void ParseColors()
        {
            var parts = (_cfg.Colors ?? "").Split(',');
            var def = new[] { "E03030", "E8C020", "30C050", "30B0E0", "909090" };
            var cols = new Color[5];
            for (int i = 0; i < 5; i++)
            {
                string h = i < parts.Length ? parts[i].Trim().TrimStart('#') : def[i];
                int v;
                if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v))
                    v = int.Parse(def[i], System.Globalization.NumberStyles.HexNumber);
                cols[i] = new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
            }
            _tierColors = new[] { cols[0], cols[1], cols[2], cols[3] };
            _greyColor = cols[4];
        }

        internal Color[] TierColors { get { return _tierColors; } }
        internal Color GreyColor { get { return _greyColor; } }

        // ---------- hotkey ----------

        /// <summary>T-144 L3: a new Hotkey spec at run time; the binding forgets its pick and resolves again.</summary>
        internal void Respec(string spec)
        {
            if (_binding != null) _binding.Respec(spec);
        }

        /// <summary>Resolved hotkey text for the legend ("Alt+V"); "" when the key is off or none is free (no hardcoded key in the legend).</summary>
        internal string HotkeyLabel { get { return _binding == null ? "" : _binding.Label; } }

        /// <summary>The unified hotkey (T-123 C3): candidates, the game's key check and the mod's conflict registry live in HotkeyBinding.</summary>
        internal void BindHotkey(Ui.HotkeyBinding binding, Ui.IKeyInput input, Ui.IHotkeyGame game)
        {
            _binding = binding; _input = input; _keyGame = game;
        }

        public void Update()
        {
            try
            {
                if (_binding == null || _binding.State == "off") return;
                _binding.Resolve(Time.unscaledTime, _api.Frame.InMission, _keyGame);
                if (_binding.Pressed(_input)) _api.SetOverlay(!_api.OverlayOn, "hotkey");
            }
            catch (Exception e) { Warn("hotkey: " + e.Message); }
        }

        // ---------- finding the map and its projection ----------

        private void ResolveTypes()
        {
            if (_typesReady) return;
            _tVtc = _api.TypeOf(RadarSpecs.TreeType);
            _tMainView = _api.TypeOf(RadarSpecs.MainViewType);
            if (_tVtc == null || _tMainView == null) return;
            _mChildCount = _api.Member(RadarSpecs.TreeChildCount) as MethodInfo;
            _mChild = _api.Member(RadarSpecs.TreeChild) as MethodInfo;
            _mParent = _api.Member(RadarSpecs.TreeParent) as MethodInfo;
            _mHitTest = _api.Member(RadarSpecs.TreeHitTest) as MethodInfo;
            _typesReady = _mChildCount != null && _mChild != null;
            if (_typesReady && _walk == null)
                _walk = new MapTreeWalk(WalkChildCount, WalkChild, IsWindow, IsMapBase);
        }

        private int WalkChildCount(object node)
        {
            _a1[0] = node;
            return Convert.ToInt32(_mChildCount.Invoke(null, _a1));
        }

        private object WalkChild(object node, int i)
        {
            if (i >= _boxedIdx.Length)
            {
                int old = _boxedIdx.Length, len = Math.Max(16, Math.Max(i + 1, old * 2));
                Array.Resize(ref _boxedIdx, len);
                for (int k = old; k < len; k++) _boxedIdx[k] = k;
            }
            _a2[0] = node; _a2[1] = _boxedIdx[i];
            return _mChild.Invoke(null, _a2);
        }

        private Type PointType()
        {
            if (_tPoint != null) return _tPoint;
            var toScreen = _api.Member(CoreSpecs.PointToScreen) as MethodInfo;
            if (toScreen == null) return null;
            var ps = toScreen.GetParameters();
            _tPoint = ps.Length > 0 ? ps[0].ParameterType : null;
            return _tPoint;
        }

        private static bool IsWindow(object o)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType) if (t.FullName == RadarSpecs.WindowType.Type) return true;
            return false;
        }

        private static bool IsMapBase(object o)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType) if (t.FullName == RadarSpecs.MapBaseType.Type) return true;
            return false;
        }

        private bool Visible(object map)
        {
            return map != null && GameApi.BoolOf2(_api.Get(map, RadarSpecs.ElementIsVisible));
        }

        private object MainViewRoot()
        {
            if (_tMainView == null) return null;
            return _api.GetStatic(RadarSpecs.MainViewInstance);
        }

        /// <summary>The cached map is still good: alive, visible, and (checked twice a second) its ancestors lead to MainGameView.Instance (at most 400 steps).</summary>
        private bool CachedMapValid()
        {
            if (_map == null) return false;
            try
            {
                if (!Visible(_map)) return false;
                if (Time.unscaledTime < _nextAncCheck) return true;
                _nextAncCheck = Time.unscaledTime + 0.5f;
                ResolveTypes();
                if (!_typesReady || _mParent == null) return true;   // cannot verify: keep it
                if (!AllowSmallWindow && _mapWindow != null && !MapMaximized) return false;   // collapsed to the small window and that is not wanted
                object root = MainViewRoot();
                if (root == null) return false;
                object o = _map;
                for (int i = 0; i < 400 && o != null; i++)
                {
                    if (ReferenceEquals(o, root)) return true;
                    _a1[0] = o; o = _mParent.Invoke(null, _a1);
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Largest visible MapControl.MapBase of the main game view. The cached map is validated every call; when it fails the tree is walked
        /// by frames (T-127: at most MapTreeWalk.FrameBudget nodes a frame), and after MapScanSchedule.ParkAfterMisses misses the walks stop
        /// until the view changes. One answer per frame: the radar layer, the labels and the ASW overlay all ask.
        /// </summary>
        internal bool TryFindMap(out object mapBase)
        {
            int frame = Time.frameCount;
            if (frame != _resultFrame)
            {
                bool ok;
                try { ok = FindMapOnce(frame); }
                catch (Exception e) { Warn("map search: " + e.Message); ok = false; _mapFound = false; }
                _resultFrame = frame; _resultOk = ok;
            }
            mapBase = _resultOk ? _map : null;
            return _resultOk;
        }

        private bool FindMapOnce(int frame)
        {
            double now = Time.unscaledTime;
            bool on = _api.OverlayOn;
            if (on != _lastOverlayOn)
            {
                _lastOverlayOn = on;
                _sched.Reset(now);
                if (_walk != null) _walk.Abort();
            }
            if (CachedMapValid()) { _mapFound = true; return true; }
            _map = null; _mapWindow = null; _mapFound = false;
            _sched.MapLost(now);
            ResolveTypes();
            if (!_typesReady) return false;
            if (_sched.State == MapScanState.Parked && _sched.ShouldCheckSignature(now) && _sched.Signature(ViewSignature(), now))
                _log("radar overlay: map scan woke (" + _sched.WakeReason + ")");
            if (_sched.State != MapScanState.Scanning && _sched.ShouldStart(now))
            {
                bool fromParked = _sched.State == MapScanState.Parked;
                object startRoot = MainViewRoot();
                _sched.Started(now);
                if (fromParked) _log("radar overlay: map scan woke (" + _sched.WakeReason + ")");
                if (startRoot == null) { FinishScan(false, now); return false; }
                _root = startRoot;
                _walk.Start(startRoot);
                _stepFrame = -1;
            }
            if (_sched.State != MapScanState.Scanning || _stepFrame == frame) return false;
            _stepFrame = frame;
            object root = MainViewRoot();
            if (root == null || !ReferenceEquals(root, _walk.Root))
            {
                _walk.Abort(); ClearArgs();
                FinishScan(false, now);
                return false;
            }
            if (!_walk.Step(MapTreeWalk.FrameBudget)) return false;
            ClearArgs();
            bool found = PickMap(root);
            FinishScan(found, now);
            return found;
        }

        private void FinishScan(bool found, double now)
        {
            if (!found && _sched.WillPark) _sched.Finished(false, now, ViewSignature());
            else _sched.Finished(found, now);
            if (!found && _sched.State == MapScanState.Parked && _sched.Misses == MapScanSchedule.ParkAfterMisses)
                _log("radar overlay: map scan parked after " + MapScanSchedule.ParkAfterMisses + " misses");
        }

        /// <summary>Mission left (T-127): forget the map and the parked state so the next mission searches at once.</summary>
        internal void ResetScan()
        {
            if (_walk != null) _walk.Abort();
            ClearArgs();
            _map = null; _mapWindow = null; _mapFound = false; _root = null;
            _sched.Reset(Time.unscaledTime);
            _resultFrame = -1;
        }

        private void ClearArgs() { _a1[0] = null; _a2[0] = null; _a2[1] = null; }

        /// <summary>Selection among the maps of a finished walk (unchanged rules): hit tests, host window, rank.</summary>
        private bool PickMap(object root)
        {
            var found = _walk.Maps; var hostWin = _walk.MapWindows;
            Calibrate(root);
            // A map counts only when the user really sees it: a hit test from the main view root at a few of its points must land
            // inside it. The second-monitor map (SecondMap) is "visible" and full-screen but lives outside this view; a map under
            // a full-screen window is covered.
            var rects = new List<double[]>();
            var ranks = new List<int>();
            var diag = new List<string>();
            object mainVm = MainMapVm();
            bool fbNoted = false;
            for (int fi = 0; fi < found.Count; fi++)
            {
                var m = found[fi];
                double w = _api.D2(_api.Get(m, RadarSpecs.ElementWidth)), h = _api.D2(_api.Get(m, RadarSpecs.ElementHeight));
                int hits = Visible(m) ? SeenPoints(root, m, w, h) : -1;
                bool seen = hits >= 2 || (hits >= 0 && _mHitTest == null);
                // T-113: the main tactical map lives in a SeapowerUI.Window (maximised = full-screen map, else the 3D mini view);
                // SecondMap and other windows are rejected by the window's DataContext, a maximised window wins over a small one
                string host = HostOf(hostWin[fi], mainVm, ref fbNoted);
                bool max = hostWin[fi] != null && GameApi.BoolOf2(_api.Get(hostWin[fi], RadarSpecs.WindowMaximized));
                diag.Add(Math.Round(w) + "x" + Math.Round(h) + (hits < 0 ? " hidden" : " seen " + hits + "/5") + " host=" + host + " max=" + (max ? 1 : 0));
                int rank = CoverageMath.MapRank(seen, host, max, AllowSmallWindow);
                ranks.Add(rank);
                rects.Add(rank >= 0 ? new[] { 0.0, 0.0, w, h } : null);
            }
            int best = CoverageMath.PickBest(ranks, rects);
            string pick = "radar overlay: maps [" + string.Join("; ", diag.ToArray()) + "], picked " + best + ", px scale " + Math.Round(_pxScale, 3);
            if (pick != _lastPick) { _lastPick = pick; _log(pick); }
            if (best < 0) return false;
            _map = found[best]; _mapWindow = hostWin[best]; _mapFound = true;
            return true;
        }

        /// <summary>
        /// Cheap fingerprint of the view for the parked search (T-127): the root, its child count, the main map view model and its
        /// IsFullscreen, Visible/Maximized of the windows the last walk saw, the screen size and the overlay switch. 0 on failure.
        /// </summary>
        private long ViewSignature()
        {
            try
            {
                object root = MainViewRoot();
                long h = 17;
                unchecked
                {
                    h = h * 31 + (root == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(root));
                    if (root != null) h = h * 31 + WalkChildCount(root);
                    object vm = MainMapVm();
                    h = h * 31 + (vm == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm));
                    if (vm != null) h = h * 31 + (GameApi.BoolOf2(_api.Get(vm, RadarSpecs.SceneMapFullscreen)) ? 1 : 2);
                    h = MapScanSchedule.WindowsSignature(h, _walk == null ? null : _walk.Windows,
                        w => (Visible(w) ? 1 : 2) * 3 + (GameApi.BoolOf2(_api.Get(w, RadarSpecs.WindowMaximized)) ? 1 : 2));
                    h = h * 31 + Screen.width; h = h * 31 + Screen.height;
                    h = h * 31 + (_api.OverlayOn ? 1 : 2);
                }
                ClearArgs();
                return h;
            }
            catch { return 0; }
        }

        /// <summary>Noesis screen units per pixel: the root view's far corner in screen units over the screen size in pixels.</summary>
        private void Calibrate(object root)
        {
            try
            {
                var toScreen = _api.Member(CoreSpecs.PointToScreen) as MethodInfo;
                if (toScreen == null) return;
                double w = _api.D2(_api.Get(root, RadarSpecs.ElementWidth)), h = _api.D2(_api.Get(root, RadarSpecs.ElementHeight));
                var tPoint = PointType();
                _a1[0] = Activator.CreateInstance(tPoint, new object[] { (float)w, (float)h });
                var r = toScreen.Invoke(root, _a1);
                double sx = _api.D2(_api.Get(r, RadarSpecs.PointX)), sy = _api.D2(_api.Get(r, RadarSpecs.PointY));
                double k = CoverageMath.CalibratePixelScale(sx, sy, Screen.width, Screen.height);
                if (k > 0) { _pxScale = k; _api.NoesisPxScale = k; }
            }
            catch (Exception e) { Warn("calibrate: " + e.Message); }
        }

        /// <summary>How many of 5 points of the map (centre and 4 inner points) a hit test from the root lands inside the map.</summary>
        private int SeenPoints(object root, object map, double w, double h)
        {
            if (_mHitTest == null || _mParent == null || w <= 1 || h <= 1) return 0;
            int n = 0;
            try
            {
                var toScreen = _api.Member(CoreSpecs.PointToScreen) as MethodInfo;
                var fromScreen = _api.Member(CoreSpecs.PointFromScreen) as MethodInfo;
                if (toScreen == null || fromScreen == null) return 0;
                var tPoint = PointType();
                double[] fx = { 0.5, 0.25, 0.75, 0.25, 0.75 }, fy = { 0.5, 0.25, 0.25, 0.75, 0.75 };
                for (int i = 0; i < 5; i++)
                {
                    _a1[0] = Activator.CreateInstance(tPoint, new object[] { (float)(w * fx[i]), (float)(h * fy[i]) });
                    var sp = toScreen.Invoke(map, _a1);
                    _a1[0] = sp;
                    var rp = fromScreen.Invoke(root, _a1);
                    _a2[0] = root; _a2[1] = rp;
                    var res = _mHitTest.Invoke(null, _a2);
                    var v = res == null ? null : _api.Get(res, RadarSpecs.HitVisual);
                    for (int guard = 0; v != null && guard < 400; guard++)
                    {
                        if (ReferenceEquals(v, map)) { n++; break; }
                        _a1[0] = v; v = _mParent.Invoke(null, _a1);
                    }
                }
            }
            catch (Exception e) { Warn("hit test: " + e.Message); return 0; }
            return n;
        }

        private bool ScreenPoint(object map, Type tPoint, MethodInfo toScreen, double x, double y, out double sx, out double sy)
        {
            sx = sy = 0;
            _a1[0] = Activator.CreateInstance(tPoint, new object[] { (float)x, (float)y });
            var r = toScreen.Invoke(map, _a1);
            double nx = _api.D2(_api.Get(r, RadarSpecs.PointX)), ny = _api.D2(_api.Get(r, RadarSpecs.PointY));
            double ui = _api.UiScale();
            CoverageMath.NoesisToPixels(nx, ny, ui, Screen.height, _cfg.ScaleOverride > 0 ? _cfg.ScaleOverride : _pxScale, out sx, out sy);
            return !double.IsNaN(sx) && !double.IsNaN(sy);
        }

        private bool RawScreen(object map, Type tPoint, MethodInfo toScreen, double x, double y, out double nx, out double ny)
        {
            _a1[0] = Activator.CreateInstance(tPoint, new object[] { (float)x, (float)y });
            var r = toScreen.Invoke(map, _a1);
            nx = _api.D2(_api.Get(r, RadarSpecs.PointX)); ny = _api.D2(_api.Get(r, RadarSpecs.PointY));
            return !double.IsNaN(nx) && !double.IsNaN(ny);
        }

        /// <summary>
        /// IMGUI pixel (y from the top) -> LOCAL map units (T-142 W4). The pixel goes to Noesis screen units (inverse of the drawing conversion), then
        /// the map's own PointToScreen is inverted from three of its samples (Noesis has no PointFromScreen). The result is in the space of
        /// TryLocalProjection, so mouse input lands where the Noesis layer draws.
        /// </summary>
        internal bool TryPixelToLocal(object map, double px, double py, out double lx, out double ly)
        {
            lx = ly = 0;
            try
            {
                var toScreen = FindToScreen(map);
                if (toScreen == null) return false;
                var tPoint = PointType();
                double tx, ty, n0x, n0y, n1x, n1y, n2x, n2y;
                CoverageMath.PixelsToNoesis(px, py, _api.UiScale(), Screen.height, _cfg.ScaleOverride > 0 ? _cfg.ScaleOverride : _pxScale, out tx, out ty);
                const double s = 100;
                if (!RawScreen(map, tPoint, toScreen, 0, 0, out n0x, out n0y) || !RawScreen(map, tPoint, toScreen, s, 0, out n1x, out n1y) || !RawScreen(map, tPoint, toScreen, 0, s, out n2x, out n2y)) return false;
                double ax = (n1x - n0x) / s, ay = (n1y - n0y) / s, bx = (n2x - n0x) / s, by = (n2y - n0y) / s;
                double det = ax * by - bx * ay;
                if (Math.Abs(det) < 1e-12) return false;
                double dx = tx - n0x, dy = ty - n0y;
                lx = (dx * by - bx * dy) / det;
                ly = (ax * dy - dx * ay) / det;
                return !double.IsNaN(lx) && !double.IsNaN(ly);
            }
            catch (Exception e) { Warn("pixel to local: " + e.Message); return false; }
        }

        /// <summary>Debug line "asw proj": the screen projection of a point against the local projection scaled to the map rectangle (confirms a drift).</summary>
        internal string ProjDebugLine(object map, double refLat, double refLon)
        {
            Affine2 sa, ga; double[] rect; double mw, mh;
            if (!TryProjection(map, refLat, refLon, out sa, out rect) || !TryLocalProjection(map, refLat, refLon, out ga, out mw, out mh) || mw < 2 || mh < 2) return null;
            double sx, sy, lx, ly;
            sa.Apply(refLat, refLon, out sx, out sy);
            ga.Apply(refLat, refLon, out lx, out ly);
            double kx = rect[2] / mw, ky = rect[3] / mh;
            double cx = rect[0] + lx * kx, cy = rect[1] + ly * ky;
            double diff = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            return "asw proj screen(P)=" + Math.Round(sx, 1) + "," + Math.Round(sy, 1) + " local(P)*k+rect=" + Math.Round(cx, 1) + "," + Math.Round(cy, 1) + " diff=" + Math.Round(diff, 1) + "px px_scale=" + Math.Round(_pxScale, 3);
        }

        private MethodInfo FindToScreen(object map) { return _api.Member(CoreSpecs.PointToScreen) as MethodInfo; }

        /// <summary>The visible map and its screen rectangle (x,y,w,h) in pixels (same pixels as TryProjection).</summary>
        internal bool TryMapPixels(out object map, out double[] rect)
        {
            rect = null;
            if (!TryFindMap(out map)) return false;
            return TryElementPixels(map, out rect, "map rect: ");
        }

        /// <summary>Any Noesis element's rectangle {x, y, w, h} in the same screen pixels as TryMapPixels (y from the top). 0.28.10: the World View window.</summary>
        internal bool TryElementPixels(object element, out double[] rect, string warnPrefix = "element rect: ")
        {
            rect = null;
            if (element == null) return false;
            try
            {
                var toScreen = FindToScreen(element);
                if (toScreen == null) return false;
                var tPoint = PointType();
                double w = _api.D2(_api.Get(element, RadarSpecs.ElementWidth)), h = _api.D2(_api.Get(element, RadarSpecs.ElementHeight));
                double x0, y0, x1, y1;
                if (!ScreenPoint(element, tPoint, toScreen, 0, 0, out x0, out y0) || !ScreenPoint(element, tPoint, toScreen, w, h, out x1, out y1)) return false;
                rect = new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0) };
                return rect[2] > 1 && rect[3] > 1;
            }
            catch (Exception e) { Warn(warnPrefix + e.Message); return false; }
        }

        /// <summary>Screen-pixel affine moved into the local space of the map rectangle and scaled (RT pixels per screen pixel).</summary>
        internal static Affine2 ToLocal(Affine2 screenAff, double[] rect, double scale)
        {
            return new Affine2
            {
                A = screenAff.A * scale, B = screenAff.B * scale, C = (screenAff.C - rect[0]) * scale,
                D = screenAff.D * scale, E = screenAff.E * scale, F = (screenAff.F - rect[1]) * scale
            };
        }

        /// <summary>Affine (lat,lon) -> IMGUI pixels and the map rectangle (x,y,w,h) in IMGUI pixels.</summary>
        internal bool TryProjection(object map, double refLat, double refLon, out Affine2 aff, out double[] rect)
        {
            aff = new Affine2(); rect = null;
            var toView = _api.Member(CoreSpecs.LocationToView) as MethodInfo;
            var toScreen = _api.Member(CoreSpecs.PointToScreen) as MethodInfo;
            if (toView == null || toScreen == null) return false;
            var tPoint = toView.ReturnType;
            double w = _api.D2(_api.Get(map, RadarSpecs.ElementWidth)), h = _api.D2(_api.Get(map, RadarSpecs.ElementHeight));
            double x0, y0, x1, y1;
            if (!ScreenPoint(map, tPoint, toScreen, 0, 0, out x0, out y0) || !ScreenPoint(map, tPoint, toScreen, w, h, out x1, out y1)) return false;
            rect = new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0) };
            double d = 0.2;
            double[] lat = { refLat, refLat + d, refLat, refLat + d * 0.7 };
            double[] lon = { refLon, refLon, refLon + d, refLon - d * 0.6 };
            var sx = new double[4]; var sy = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var vp = toView.Invoke(map, new[] { _api.GeoObj(lat[i], lon[i]) });
                double nx = _api.D2(_api.Get(vp, RadarSpecs.PointX)), ny = _api.D2(_api.Get(vp, RadarSpecs.PointY));
                // LocationToView is map-local: convert through PointToScreen
                if (!ScreenPoint(map, tPoint, toScreen, nx, ny, out sx[i], out sy[i])) return false;
            }
            bool ok;
            aff = CoverageMath.FitAffine(new[] { lat[0], lat[1], lat[2] }, new[] { lon[0], lon[1], lon[2] }, new[] { sx[0], sx[1], sx[2] }, new[] { sy[0], sy[1], sy[2] }, out ok);
            if (!ok) return false;
            double px, py;
            aff.Apply(lat[3], lon[3], out px, out py);
            if (Math.Abs(px - sx[3]) > 2.0 || Math.Abs(py - sy[3]) > 2.0) { Warn("map projection is not affine (error " + Math.Round(Math.Max(Math.Abs(px - sx[3]), Math.Abs(py - sy[3])), 1) + " px)"); return false; }
            return true;
        }

        /// <summary>
        /// Affine (lat,lon) -> LOCAL map units (the space of LocationToView, MapBase.ActualWidth/Height and Image.Width/Height), no PointToScreen.
        /// The Noesis layer draws in this space, so the zone cannot drift against the map whatever the screen scale is (T-107 A2).
        /// </summary>
        internal bool TryLocalProjection(object map, double refLat, double refLon, out Affine2 geoToLocal, out double mapW, out double mapH)
        {
            geoToLocal = new Affine2(); mapW = mapH = 0;
            var toView = _api.Member(CoreSpecs.LocationToView) as MethodInfo;
            if (toView == null) return false;
            mapW = _api.D2(_api.Get(map, RadarSpecs.ElementWidth)); mapH = _api.D2(_api.Get(map, RadarSpecs.ElementHeight));
            if (mapW < 2 || mapH < 2) return false;
            double d = 0.2;
            double[] lat = { refLat, refLat + d, refLat, refLat + d * 0.7 };
            double[] lon = { refLon, refLon, refLon + d, refLon - d * 0.6 };
            var lx = new double[4]; var ly = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var vp = toView.Invoke(map, new[] { _api.GeoObj(lat[i], lon[i]) });
                lx[i] = _api.D2(_api.Get(vp, RadarSpecs.PointX)); ly[i] = _api.D2(_api.Get(vp, RadarSpecs.PointY));
                if (double.IsNaN(lx[i]) || double.IsNaN(ly[i])) return false;
            }
            bool ok;
            geoToLocal = CoverageMath.FitAffine(new[] { lat[0], lat[1], lat[2] }, new[] { lon[0], lon[1], lon[2] }, new[] { lx[0], lx[1], lx[2] }, new[] { ly[0], ly[1], ly[2] }, out ok);
            if (!ok) return false;
            double px, py;
            geoToLocal.Apply(lat[3], lon[3], out px, out py);
            if (Math.Abs(px - lx[3]) > 2.0 || Math.Abs(py - ly[3]) > 2.0) { Warn("map projection is not affine (local error " + Math.Round(Math.Max(Math.Abs(px - lx[3]), Math.Abs(py - ly[3])), 1) + ")"); return false; }
            return true;
        }

        // ---------- game windows (SeapowerUI.Window) ----------

        private readonly List<object> _windows = new List<object>();
        private float _nextWinScan;
        private float _nextWinRects;
        private List<double[]> _winRects = new List<double[]>();

        /// <summary>
        /// Screen rectangles (pixels, y from the top) of the visible game windows, so the IMGUI overlay does not draw on top of them. The tree is walked
        /// at most once a second (windows are not descended into), the bounds are refreshed twice a second. Never throws; empty on any failure.
        /// </summary>
        internal List<double[]> WindowRects()
        {
            try
            {
                if (Time.unscaledTime < _nextWinRects) return _winRects;
                _nextWinRects = Time.unscaledTime + 0.5f;
                ResolveTypes();
                if (!_typesReady) return _winRects;
                if (Time.unscaledTime >= _nextWinScan)
                {
                    _nextWinScan = Time.unscaledTime + 1f;
                    _windows.Clear();
                    object root = MainViewRoot();
                    if (root != null)
                    {
                        var stack = new Stack<object>();
                        stack.Push(root);
                        int budget = 30000;
                        while (stack.Count > 0 && budget-- > 0)
                        {
                            var node = stack.Pop();
                            if (IsWindow(node)) { _windows.Add(node); continue; }
                            int n;
                            try { n = Convert.ToInt32(_mChildCount.Invoke(null, new[] { node })); } catch { continue; }
                            for (int i = 0; i < n; i++)
                                try { var c = _mChild.Invoke(null, new[] { node, (object)i }); if (c != null) stack.Push(c); } catch { }
                        }
                    }
                }
                var res = new List<double[]>();
                foreach (var w in _windows)
                {
                    if (!Visible(w)) continue;
                    var toScreen = FindToScreen(w);
                    if (toScreen == null) continue;
                    var tPoint = PointType();
                    double ww = _api.D2(_api.Get(w, RadarSpecs.ElementWidth)), wh = _api.D2(_api.Get(w, RadarSpecs.ElementHeight));
                    double x0, y0, x1, y1;
                    if (ww < 2 || wh < 2 || !ScreenPoint(w, tPoint, toScreen, 0, 0, out x0, out y0) || !ScreenPoint(w, tPoint, toScreen, ww, wh, out x1, out y1)) continue;
                    res.Add(new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0) });
                }
                _winRects = res;
            }
            catch (Exception e) { Warn("window rects: " + e.Message); }
            return _winRects;
        }

        /// <summary>Mod windows plus game windows: everything the IMGUI overlays must stay out of.</summary>
        internal List<double[]> OccludedRects()
        {
            var l = ModUiRects.All();
            l.AddRange(WindowRects());
            return l;
        }

        // ---------- drawing ----------

        private void Warn(string m)
        {
            if (Time.unscaledTime - _lastWarn < 60f) return;
            _lastWarn = Time.unscaledTime;
            _log("radar overlay: " + m);
        }

        public void OnGUI()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint) return;
                if (!_api.OverlayOn || _mat == null) return;
                var frame = _api.LatestFrame;
                object map;
                if (!TryFindMap(out map)) return;
                if (frame == null || frame.Profiles == null || frame.Profiles.Count == 0) return;
                double cLat = (frame.Box.LatMin + frame.Box.LatMax) * 0.5, cLon = (frame.Box.LonMin + frame.Box.LonMax) * 0.5;
                MapPx spx; double[] srect;
                if (TryScreenPx(map, out spx, out srect))   // T-309: the exact globe projector for the IMGUI fallback too
                {
                    var spieces = CoverageMath.SubtractRects(srect, OccludedRects(), 8, 8);
                    if (spieces.Count == 0) return;
                    foreach (var piece in spieces) DrawPx(frame, spx, piece);
                    if (LegendVisible) DrawLegend(spieces[0]);
                    return;
                }
                Affine2 aff; double[] rect;
                if (!TryProjection(map, cLat, cLon, out aff, out rect)) return;
                // imgui fallback: on top of everything, so the mod's own windows and the game's windows are cut out (T-070, T-107 A4)
                var pieces = CoverageMath.SubtractRects(rect, OccludedRects(), 8, 8);
                if (pieces.Count == 0) return;
                foreach (var piece in pieces) Draw(frame, aff, piece);
                if (LegendVisible) DrawLegend(pieces[0]);
                if (_cfg.Debug && Time.unscaledTime - _lastDiag > 10f) { _lastDiag = Time.unscaledTime; LogDiag(map, frame, aff, rect); }
            }
            catch (Exception e) { Warn(e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Diagnostics (config Debug, every 10 s while drawn): each radar position through the fitted affine and through the map directly.</summary>
        private void LogDiag(object map, CoverageFrame f, Affine2 aff, double[] rect)
        {
            try
            {
                var sb = new System.Text.StringBuilder("radar overlay diag: rect " + Math.Round(rect[0]) + "," + Math.Round(rect[1]) + " " + Math.Round(rect[2]) + "x" + Math.Round(rect[3])
                    + " screen " + Screen.width + "x" + Screen.height + " box " + Math.Round(f.Box.LatMin, 4) + ".." + Math.Round(f.Box.LatMax, 4) + " / " + Math.Round(f.Box.LonMin, 4) + ".." + Math.Round(f.Box.LonMax, 4));
                var seen = new HashSet<int>();
                var toView = _api.Member(CoreSpecs.LocationToView) as MethodInfo;
                var toScreen = _api.Member(CoreSpecs.PointToScreen) as MethodInfo;
                foreach (var p in f.Profiles)
                {
                    if (!seen.Add(p.OwnerUid)) continue;
                    double ax, ay; aff.Apply(p.Lat, p.Lon, out ax, out ay);
                    sb.Append("; uid " + p.OwnerUid + " " + Math.Round(p.Lat, 5) + "," + Math.Round(p.Lon, 5) + " affine " + Math.Round(ax) + "," + Math.Round(ay));
                    if (toView != null && toScreen != null)
                    {
                        var vp = toView.Invoke(map, new[] { _api.GeoObj(p.Lat, p.Lon) });
                        double dx, dy;
                        if (ScreenPoint(map, toView.ReturnType, toScreen, _api.D2(_api.Get(vp, RadarSpecs.PointX)), _api.D2(_api.Get(vp, RadarSpecs.PointY)), out dx, out dy))
                            sb.Append(" direct " + Math.Round(dx) + "," + Math.Round(dy));
                    }
                }
                _log(sb.ToString());
            }
            catch (Exception e) { Warn("diag: " + e.Message); }
        }

        private float CellAlpha(byte b)
        {
            float fog = (float)Math.Max(0.0, Math.Min(0.9, _cfg.FogAlpha));
            float scale = fog / 0.35f;
            if ((b & 1) != 0) return 0f;                 // 15 m covered by an active radar: clear
            if ((b & 2) != 0) return 0.10f * scale;      // 150 m
            if ((b & 4) != 0) return 0.22f * scale;      // 3000 m
            if ((b & 8) != 0) return 0.10f * scale;      // only the surface-target tier
            return (b & 0xF0) != 0 ? fog * 0.85f : fog;  // nothing: fog (a little lighter where only an inactive radar would see)
        }

        /// <summary>Horizontal runs of equal alpha per raster row (rebuilt when the frame changes).</summary>
        private void RebuildRuns(CoverageFrame f)
        {
            if (_runs != null && _runsVersion == f.Version) return;
            _runs = CoverageMath.RasterRuns(f.Raster, f.W, f.H, CellAlpha);
            _runsVersion = f.Version;
        }

        private static Vector2 P(Affine2 a, double lat, double lon)
        {
            double x, y; a.Apply(lat, lon, out x, out y);
            return new Vector2((float)x, (float)y);
        }

        /// <summary>imgui path: the piece of the map as a GL viewport, hardware clipping.</summary>
        private void Draw(CoverageFrame f, Affine2 aff, double[] rect)
        {
            var vp = new Rect((float)rect[0], Screen.height - (float)(rect[1] + rect[3]), (float)rect[2], (float)rect[3]);
            GL.PushMatrix();
            try
            {
                GL.Viewport(vp);
                GL.LoadPixelMatrix((float)rect[0], (float)(rect[0] + rect[2]), (float)(rect[1] + rect[3]), (float)rect[1]);
                _premul = false;
                DrawGeoRect(f, aff, rect);
            }
            finally
            {
                GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                GL.PopMatrix();
            }
        }

        private void DrawPx(CoverageFrame f, MapPx px, double[] rect)
        {
            var vp = new Rect((float)rect[0], Screen.height - (float)(rect[1] + rect[3]), (float)rect[2], (float)rect[3]);
            GL.PushMatrix();
            try
            {
                GL.Viewport(vp);
                GL.LoadPixelMatrix((float)rect[0], (float)(rect[0] + rect[2]), (float)(rect[1] + rect[3]), (float)rect[1]);
                _premul = false;
                px.ResetCounters();   // IMGUI: every screen piece has its own budget
                DrawGeoPx(f, px, rect);
            }
            finally
            {
                GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                GL.PopMatrix();
            }
        }

        /// <summary>Render-texture path: raster, fog and outlines in the local pixels [0,w]x[0,h] of the map (affLocal from ToLocal). The caller sets the render
        /// target and the pixel matrix; the output is premultiplied alpha. flipY is handled by the caller's matrix (the picture is mirrored as a whole).</summary>
        internal void DrawGeo(CoverageFrame f, Affine2 affLocal, double w, double h, bool flipY)
        {
            _premul = true;
            try { DrawGeoRect(f, affLocal, new[] { 0.0, 0.0, w, h }); }
            finally { _premul = false; }
        }

        /// <summary>T-309: the same through MapPx (target = the render texture, w x h = its size). A flat map (Kind = Affine) takes the old path unchanged.</summary>
        internal void DrawGeo(CoverageFrame f, MapPx px, double w, double h, bool flipY)
        {
            _premul = true;
            try
            {
                if (px.Kind == MapProjKind.Affine)
                {
                    double[] a = px.AffineGeo;
                    DrawGeoRect(f, new Affine2 { A = a[0], B = a[1], C = a[2], D = a[3], E = a[4], F = a[5] }, new[] { 0.0, 0.0, w, h });
                }
                else DrawGeoPx(f, px, new[] { 0.0, 0.0, w, h });
            }
            finally { _premul = false; }
        }

        private readonly List<double[]> _fogPoly = new List<double[]>(1100);

        private void FogEdge(MapPx px, double lat0, double lon0, double lat1, double lon1, double pxPerM)
        {
            double midLat = (lat0 + lat1) * 0.5 * Math.PI / 180.0;
            double theta = Math.Max(Math.Abs(lat1 - lat0), Math.Abs(lon1 - lon0) * Math.Max(0.05, Math.Cos(midLat))) * Math.PI / 180.0;
            int n = MapProjector.StepsFor(theta, pxPerM);
            for (int i = 0; i < n; i++)
            {
                double x, y;
                double t = (double)i / n;
                px.PtClamped(lat0 + (lat1 - lat0) * t, lon0 + (lon1 - lon0) * t, out x, out y);
                _fogPoly.Add(new[] { x, y });
            }
        }

        /// <summary>T-309: raster, fog and outlines through the exact projector (globe). Raster cells come from the grid tables (hidden corners pressed to the limb),
        /// long runs are split to the sag limit, the fog polygon is the box edge subdivided; outline segments are short, hidden ones are skipped.</summary>
        private void DrawGeoPx(CoverageFrame f, MapPx px, double[] rect)
        {
            var b = f.Box;
            RebuildRuns(f);
            int rows = Math.Max(1, f.H), cols = Math.Max(1, f.W);
            if (_quadSink == null) _quadSink = new GlQuadSink(this);
            double pxPerM = px.PxPerNm(px.Snap.Lat0, px.Snap.Lon0) / 1852.0;
            // T-311: the raster gets its share of the frame budget; rings and other overlays keep the rest
            px.BeginSection(px.Share(CoverageRasterPx.CoverageShare));
            try
            {
            (_premul ? _matPm : _mat).SetPass(0);
            GlSafe.Quads(() => { LastRaster = CoverageRasterPx.Emit(px, _runs, b, rows, cols, rect, _quadSink); });
            _fogPoly.Clear();
            FogEdge(px, b.LatMax, b.LonMin, b.LatMax, b.LonMax, pxPerM);
            FogEdge(px, b.LatMax, b.LonMax, b.LatMin, b.LonMax, pxPerM);
            FogEdge(px, b.LatMin, b.LonMax, b.LatMin, b.LonMin, pxPerM);
            FogEdge(px, b.LatMin, b.LonMin, b.LatMax, b.LonMin, pxPerM);
            var tris = CoverageMath.FogOutsidePoly(rect, _fogPoly);
            GlSafe.Tris(() =>
            {
                GL.Color(Col(new Color(0, 0, 0, (float)Math.Max(0.0, Math.Min(0.9, _cfg.FogAlpha)))));
                foreach (var t in tris)
                {
                    GL.Vertex3((float)t[0], (float)t[1], 0); GL.Vertex3((float)t[2], (float)t[3], 0); GL.Vertex3((float)t[4], (float)t[5], 0);
                }
            });
            int stride = Math.Max(1, (int)Math.Ceiling(f.Segs.Count / (double)Math.Max(100, _cfg.MaxSegments)));
            GlSafe.Lines(() =>
            {
                for (int i = 0; i < f.Segs.Count; i += stride)
                {
                    var s = f.Segs[i];
                    if ((s.Style == 1 || s.Style == 3) && ((i / stride) & 1) == 1) continue;
                    double ax, ay, ex, ey;
                    if (!px.Pt(s.Lat1, s.Lon1, out ax, out ay) || !px.Pt(s.Lat2, s.Lon2, out ex, out ey)) continue;
                    Color c = s.Style == 1 ? _greyColor : _tierColors[Math.Min(3, (int)s.Tier)];
                    c.a = s.Style == 1 ? 0.8f : (s.Style == 3 ? 0.45f : 1f);
                    GL.Color(Col(c));
                    var a = new Vector2((float)ax, (float)ay); var e = new Vector2((float)ex, (float)ey);
                    GL.Vertex3(a.x, a.y, 0); GL.Vertex3(e.x, e.y, 0);
                    if (s.Style == 2)
                    {
                        Vector2 d = e - a; var nrm = d.sqrMagnitude > 1e-6f ? new Vector2(-d.y, d.x).normalized : Vector2.up;
                        GL.Vertex3(a.x + nrm.x, a.y + nrm.y, 0); GL.Vertex3(e.x + nrm.x, e.y + nrm.y, 0);
                    }
                }
            });
            }
            finally { LastCovVerts = px.SectionVerts; px.EndSection(); }
        }

        internal RasterEmitStats LastRaster;
        internal int LastCovVerts;
        private IQuadSink _quadSink;

        private sealed class GlQuadSink : IQuadSink
        {
            private readonly CoverageOverlay _o;
            public GlQuadSink(CoverageOverlay o) { _o = o; }
            public void Alpha(float a) { GL.Color(_o.Col(new Color(0, 0, 0, a))); }
            public void Quad(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
            {
                GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y1, 0); GL.Vertex3(x2, y2, 0); GL.Vertex3(x3, y3, 0);
            }
        }

        private Color Col(Color c)
        {
            return _premul ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a) : c;
        }

        private void DrawGeoRect(CoverageFrame f, Affine2 aff, double[] rect)
        {
            var b = f.Box;
            // 1) raster as GL quads (runs of equal alpha; GUI.DrawTexture under GUI.matrix was culled away live)
            RebuildRuns(f);
            double dLat = (b.LatMax - b.LatMin) / Math.Max(1, f.H), dLon = (b.LonMax - b.LonMin) / Math.Max(1, f.W);
            // 2) fog outside the raster box + outlines with GL
            {
                (_premul ? _matPm : _mat).SetPass(0);
                var quad = new[]
                {
                    new[] { P(aff, b.LatMax, b.LonMin).x, P(aff, b.LatMax, b.LonMin).y }, new[] { P(aff, b.LatMax, b.LonMax).x, P(aff, b.LatMax, b.LonMax).y },
                    new[] { P(aff, b.LatMin, b.LonMax).x, P(aff, b.LatMin, b.LonMax).y }, new[] { P(aff, b.LatMin, b.LonMin).x, P(aff, b.LatMin, b.LonMin).y }
                };
                double[][] q = new double[4][];
                for (int i = 0; i < 4; i++) q[i] = new double[] { quad[i][0], quad[i][1] };
                GlSafe.Quads(() =>
                {
                    foreach (var run in _runs)
                    {
                        double la0 = b.LatMax - run.Row * dLat, la1 = la0 - dLat;
                        double lo0 = b.LonMin + run.X0 * dLon, lo1 = b.LonMin + (run.X1 + 1) * dLon;
                        GL.Color(Col(new Color(0, 0, 0, run.Alpha)));
                        var c0 = P(aff, la0, lo0); var c1 = P(aff, la0, lo1); var c2 = P(aff, la1, lo1); var c3 = P(aff, la1, lo0);
                        GL.Vertex3(c0.x, c0.y, 0); GL.Vertex3(c1.x, c1.y, 0); GL.Vertex3(c2.x, c2.y, 0); GL.Vertex3(c3.x, c3.y, 0);
                    }
                });
                var tris = CoverageMath.FogOutside(rect, q);
                GlSafe.Tris(() =>
                {
                    GL.Color(Col(new Color(0, 0, 0, (float)Math.Max(0.0, Math.Min(0.9, _cfg.FogAlpha)))));
                    foreach (var t in tris)
                    {
                        GL.Vertex3((float)t[0], (float)t[1], 0); GL.Vertex3((float)t[2], (float)t[3], 0); GL.Vertex3((float)t[4], (float)t[5], 0);
                    }
                });

                int stride = Math.Max(1, (int)Math.Ceiling(f.Segs.Count / (double)Math.Max(100, _cfg.MaxSegments)));
                GlSafe.Lines(() =>
                {
                for (int i = 0; i < f.Segs.Count; i += stride)
                {
                    var s = f.Segs[i];
                    if ((s.Style == 1 || s.Style == 3) && ((i / stride) & 1) == 1) continue;     // grey dashed / T-182: jammed, the outline without jamming
                    Color c = s.Style == 1 ? _greyColor : _tierColors[Math.Min(3, (int)s.Tier)];
                    c.a = s.Style == 1 ? 0.8f : (s.Style == 3 ? 0.45f : 1f);
                    GL.Color(Col(c));
                    var a = P(aff, s.Lat1, s.Lon1); var e = P(aff, s.Lat2, s.Lon2);
                    GL.Vertex3(a.x, a.y, 0); GL.Vertex3(e.x, e.y, 0);
                    if (s.Style == 2)   // airborne radars (AEW): 2 px
                    {
                        Vector2 d = e - a; var nrm = d.sqrMagnitude > 1e-6f ? new Vector2(-d.y, d.x).normalized : Vector2.up;
                        GL.Vertex3(a.x + nrm.x, a.y + nrm.y, 0); GL.Vertex3(e.x + nrm.x, e.y + nrm.y, 0);
                    }
                }
                });
            }
        }


        /// <summary>imgui legend, same texts and rules as the render-texture one (CoverageMath.LegendLines / LegendLayout). rect = visible part of the map in pixels.</summary>
        private void DrawLegend(double[] rect)
        {
            if (rect == null) return;
            string whySmall;   // T-233: the same rule as the layer: no legend on the small (un-maximised) map
            if (!CoverageMath.LegendAllowed(MapMaximized, rect[2], rect[3], Screen.width, Screen.height, out whySmall)) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { wordWrap = false };
                _style.normal.textColor = Color.white;
            }
            var jf = _api.LatestFrame;
            var lines = CoverageMath.LegendLines(_api.CoverageTiers(), BoolClutter(), HotkeyLabel, jf != null && jf.AnyJamCut, jf != null && jf.AnyJamPartial, jf != null && jf.AnyJamApprox);
            // 2026-10-22: top-right like the layer legend, under the time panel (its fixed place: IMGUI has no Noesis element lookup here)
            double off = CoverageMath.LegendPanelOffset(rect, CoverageMath.TimePanelFallback(Screen.width, Screen.height), 270, 6);
            var box = CoverageMath.LegendLayout(rect[2], rect[3], lines.Count, 0, 1, 0, off);
            if (!box.Visible) return;
            float x = (float)(rect[0] + box.X), y = (float)(rect[1] + box.Y), lh = (float)box.LineH, pad = (float)box.Pad;
            _style.fontSize = (int)box.FontPx;
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(x, y, (float)box.W, (float)box.H), Texture2D.whiteTexture);
            float sw = (float)box.FontPx * 0.8f;
            for (int i = 0; i < lines.Count; i++)
            {
                var ln = lines[i];
                float ly = y + 4f + i * lh, tx = x + pad;
                if (ln.Swatch >= 0)
                {
                    GUI.color = ln.Swatch == 4 ? _greyColor : ln.Swatch == 5 ? new Color(_tierColors[0].r, _tierColors[0].g, _tierColors[0].b, 0.6f) : _tierColors[Math.Min(3, ln.Swatch)];
                    float sy = ly + (lh - sw) * 0.5f;
                    if (ln.Swatch == 4 || ln.Swatch == 5)   // 5 = T-182: dimmed dashed, the outline without jamming
                    {
                        GUI.DrawTexture(new Rect(tx, sy + sw * 0.4f, sw * 0.4f, 2f), Texture2D.whiteTexture);
                        GUI.DrawTexture(new Rect(tx + sw * 0.6f, sy + sw * 0.4f, sw * 0.4f, 2f), Texture2D.whiteTexture);
                    }
                    else GUI.DrawTexture(new Rect(tx, sy, sw, sw), Texture2D.whiteTexture);
                    tx += sw + 4f;
                }
                GUI.color = Color.white;
                _style.fontStyle = ln.Bold ? FontStyle.Bold : FontStyle.Normal;
                GUI.Label(new Rect(tx, ly, (float)box.W - (tx - x), lh), ln.Text, _style);
            }
            GUI.color = old;
        }

        private bool BoolClutter()
        {
            return GameApi.BoolOf2(_api.GetStatic(RadarSpecs.LowAltClutter));
        }

        public void Dispose()
        {
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
            if (_matPm != null) UnityEngine.Object.Destroy(_matPm);
        }
    }
}
