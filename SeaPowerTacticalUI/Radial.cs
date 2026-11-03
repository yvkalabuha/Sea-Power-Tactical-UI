using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>Config section [Radial] (T-068).</summary>
    internal sealed class RadialConfig
    {
        public bool Enabled = true, OnMap = true, ShowVoiceHint = true, InputPatches, Debug, Icons = true;
        public float HoldMs = 300, MoveTolerancePx = 6, PickRadiusPx = 20, SubmenuDelayMs = 180, Scale;
        public string Hotkey = "";
    }

    /// <summary>
    /// Radial menu of the selected own unit (T-068, ADR-0028): hold the left button on it for 300 ms, move towards a sector, release.
    /// Input is read from the legacy Input, the view/target decision comes from GameApi.RadialProbe (one Noesis hit test), drawing is
    /// IMGUI + GL on top of the game UI. Not part of the offline selfcheck (Unity types); logic lives in RadialMath/MenuModel/GameApi.
    /// </summary>
    internal sealed class RadialMenu
    {
        private const double DeadR = 28, InnerR = 40, OuterR = 110, ArcIn = 118, ArcOut = 185, ArcSpan = 120, ResultSeconds = 1.5;
        private readonly GameApi _api;
        private readonly RadialConfig _cfg;
        private readonly Action<string> _log;
        private Material _mat;
        private RadialGesture _g;
        private readonly Ui.HotkeyBinding _binding;   // T-123 C4: the unified hotkey; null = no hotkey configured
        private readonly Ui.IKeyInput _input;
        private readonly Ui.IHotkeyGame _keyGame;
        private bool _hotkeyMode, _blockWasOn, _errLogged;
        private int _arcPage;   // T-328: page of an arc with more cells than fit (0 while the arc is closed)
        private readonly RadialRefusalLog _refusals = new RadialRefusalLog();
        private object _unit;
        private string _kind, _view, _unitName;
        private MenuNode[] _ring = new MenuNode[0];   // T-152: exactly the visible items, no empty sectors
        private double _scale = 1;
        private string _result;
        private float _resultUntil;
        private double _resultX, _resultY;
        private GUIStyle _style;
        // screen rectangle of the model, cached per unit
        private object _rectUnit;
        private float _rectUntil;
        private double[] _rect;
        // diagnostics of the open ring (Radial.Debug)
        private double _openedMs;
        private int _repaints;
        private bool _drawLogged;
        // glyph textures (T-140, ADR-0047): key + ":" + px -> white RGBA texture with the glyph alpha
        private readonly Dictionary<string, Texture2D> _glyphTex = new Dictionary<string, Texture2D>();
        internal int GlyphTexCount { get { return _glyphTex.Count; } }
        private int _glyphRingPx;

        private sealed class GameKeys : Ui.IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict)
            {
                // a single key has its own rule (RadialMath.KeyVerdict); a combo is judged like the Overview one
                return keys.Length == 1 ? _a.RadialKeyCheck(keys[0], out conflict) : _a.OverviewKeyCheck(keys, out conflict);
            }
        }

        public RadialMenu(GameApi api, RadialConfig cfg, Action<string> log, Ui.HotkeyBinding binding = null, Ui.IKeyInput input = null)
        {
            _api = api; _cfg = cfg; _log = log ?? (m => { });
            _binding = binding; _input = input; _keyGame = new GameKeys(api);
        }

        public bool Init(out string error)
        {
            error = null;
            try
            {
                var gerr = RadialGlyphs.Load();
                foreach (var ge in gerr) _log("radial glyphs: failed " + ge);
                _log("radial glyphs: " + (RadialGlyphs.Paths.Count - gerr.Count) + " ok");
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) { error = "shader Hidden/Internal-Colored not found"; return false; }
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _g = new RadialGesture(_cfg.HoldMs, _cfg.MoveTolerancePx, _cfg.SubmenuDelayMs, DeadR, OuterR, 4, 2) { ArcSpanDeg = ArcSpan };
                _g.SpanOf = cells => SpanFor(ArcParent(), cells);   // T-328: adaptive arc width
                if (_binding != null)
                {
                    _api.RadialHotkey = _binding.Candidates.Count > 0 ? _binding.Candidates[0] : null;
                    _api.RadialHotkeyState = _binding.State;   // resolved later, in a mission (T-082)
                }
                return true;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>T-144 Live: a new Radial.Hotkey spec; the pick is forgotten and resolves again in a mission.</summary>
        public void Respec(string spec)
        {
            if (_binding == null) return;
            _binding.Respec(spec);
            _api.RadialHotkey = _binding.Candidates.Count > 0 ? _binding.Candidates[0] : null;
            _api.RadialHotkeyState = _binding.State;
            if (_binding.State == "off") _api.Capabilities.Remove("radial_hotkey");
        }

        public void Dispose()
        {
            try { Close("dispose"); } catch { }
            try { ClearGlyphs(); } catch { }
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
        }

        // ---------- per frame ----------

        /// <summary>Hotkey verdict needs the game's key bindings, which exist only in a mission: HotkeyBinding retries every 2 s while unknown (T-082).</summary>
        private void CheckHotkey()
        {
            if (_binding == null || _binding.State == "off") return;
            string before = _binding.State;
            _binding.Resolve(Time.unscaledTime, _api.Frame.InMission, _keyGame);
            if (_binding.State == before) return;
            _api.RadialHotkeyState = _binding.State;
            if (_binding.State == "free") { _api.RadialHotkey = _binding.Picked; _api.Capabilities.Add("radial_hotkey"); }
        }

        public void Update()
        {
            if (_g == null || !_cfg.Enabled) return;
            try
            {
                CheckHotkey();
                _scale = RadialMath.Scale(_api.UiScale(), Screen.height, _cfg.Scale);
                if (!_g.Open) { _g.HoldMs = _cfg.HoldMs; _g.SubmenuMs = _cfg.SubmenuDelayMs; _g.TolPx = _cfg.MoveTolerancePx * _scale; _g.DeadR = DeadR * _scale; _g.OuterR = OuterR * _scale; }
                double t = Time.unscaledTime * 1000.0;
                double mx = Input.mousePosition.x, my = Screen.height - Input.mousePosition.y;
                bool held;
                if (_hotkeyMode) held = _binding != null && _binding.Held(_input);
                else
                {
                    held = Input.GetMouseButton(0);
                    if (_binding != null && !_g.Open && _binding.Pressed(_input)) StartHotkey(t, mx, my);
                    if (_hotkeyMode) held = true;
                }
                if (!_g.ArcOpen) _arcPage = 0;
                var ev = _g.Step(t, held, mx, my, TryOpen, ChildCount);
                if (ev == GestureEvent.None) TurnPage(t);
                if (ev == GestureEvent.Open) { _openedMs = t; _repaints = 0; _drawLogged = false; _api.RadialOpen = true; _api.RadialViewName = _view; _api.RadialUnitUid = _api.UidOf(_unit); }
                else if (ev == GestureEvent.Execute) Execute();
                else if (ev == GestureEvent.Cancel) Close("cancel");
                if (!_g.Open && _api.RadialOpen) Close("gesture_end");
                if (_g.BlockInput) { _api.SetRadialBlock(true); _blockWasOn = true; }
                else if (_blockWasOn) { _api.SetRadialBlock(false); _blockWasOn = false; }
                if (!_g.Open) _hotkeyMode = false;
            }
            catch (Exception e)
            {
                try { _g.Reset(); Close("error"); _api.SetRadialBlock(false); _blockWasOn = false; _hotkeyMode = false; } catch { }
                if (!_errLogged) { _errLogged = true; _log("radial update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-130: mission left - no reference to the units of the old mission.</summary>
        internal void OnMissionLeft() { _unit = null; _rectUnit = null; _rect = null; }

        private void Close(string reason)
        {
            bool was = _api.RadialOpen;
            _api.RadialOpen = false; _api.RadialViewName = null;
            if (was && _cfg.Debug)
                _log(RadialMath.CloseReasonLine(reason, _view, Time.unscaledTime * 1000.0 - _openedMs, _repaints));
        }

        private int ChildCount(int s)
        {
            var n = s >= 0 && s < _ring.Length ? _ring[s] : null;
            if (n == null || !n.Enabled) return -1;
            return n.Kind == "group" ? ArcCells(n) : 0;
        }

        // ---------- arc paging (T-328) ----------

        /// <summary>The parent of the open arc, else null.</summary>
        private MenuNode ArcParent()
        {
            return _g != null && _g.ArcOpen && _g.Sector >= 0 && _g.Sector < _ring.Length ? _ring[_g.Sector] : null;
        }

        /// <summary>Arc width for a number of visible cells: adaptive (RingGroups.ArcSpan) for nodes that ask for it, else the fixed 120 degrees.</summary>
        private static double SpanFor(MenuNode p, int cells) { return p != null && p.ArcSpan > 0 ? RingGroups.ArcSpan(cells) : ArcSpan; }

        /// <summary>The children of the current page and whether a "More" cell follows them.</summary>
        private void ArcPageOf(MenuNode p, out int start, out int count, out bool more)
        {
            if (p.ArcPage > 0) RadialMath.ArcPage(p.Children.Count, _arcPage, p.ArcPage, out start, out count, out more);
            else { start = 0; count = p.Children.Count; more = false; }
        }

        private int ArcCells(MenuNode p) { int st, ct; bool mo; ArcPageOf(p, out st, out ct, out mo); return ct + (mo ? 1 : 0); }

        /// <summary>Node of one visible arc cell: a child of the page, or the synthetic "More" cell (Kind page, runs nothing).</summary>
        private MenuNode ArcCell(MenuNode p, int k)
        {
            int st, ct; bool mo;
            ArcPageOf(p, out st, out ct, out mo);
            if (k < 0 || k >= ct + (mo ? 1 : 0)) return null;
            if (k < ct) return p.Children[st + k];
            int rest = p.Children.Count - ct;
            string label = UiText.Fmt("radial.group.more", rest);
            return new MenuNode { Id = "arc_more", Kind = "page", Icon = "more", Label = label, Short = label, Caption = UiText.Get("menu.more") + " \u203a", Badge = rest.ToString() };
        }

        /// <summary>The cursor rests on the "More" cell of a paged arc for two submenu delays: show the next page (cyclic).</summary>
        private void TurnPage(double t)
        {
            var p = ArcParent();
            if (p == null || p.ArcPage <= 0 || _g.ArcItem < 0) return;
            var cell = ArcCell(p, _g.ArcItem);
            if (cell == null || cell.Kind != "page" || _g.ArcHoverMs(t) < 2 * _cfg.SubmenuDelayMs) return;
            _arcPage++;
            _g.ResetArc(ArcCells(p), t);
        }

        // ---------- opening ----------

        private Camera Cam()
        {
            var gc = _api.RadialCamera() as Camera;   // CameraManager.Main: the camera the game picks with
            if (gc != null && gc.enabled) return gc;
            var c = Camera.main;   // fallback
            if (c != null && c.enabled) return c;
            foreach (var x in Camera.allCameras) if (x != null && x.enabled && x.targetTexture == null) return x;
            return null;
        }

        private bool UnitScreen(object unit, out double ux, out double uy, out double uz, out double[] rect)
        {
            ux = uy = uz = 0; rect = null;
            var comp = unit as Component;
            var cam = Cam();
            if (comp == null || cam == null) return false;
            var p = cam.WorldToScreenPoint(comp.transform.position);
            CamToScreen(cam, p.x, p.y, out ux, out uy);
            uz = p.z;
            if (uz <= 0) return true;
            if (!ReferenceEquals(_rectUnit, unit) || Time.unscaledTime > _rectUntil)
            {
                _rectUnit = unit; _rectUntil = Time.unscaledTime + 1f; _rect = null;
                try
                {
                    var rs = comp.GetComponentsInChildren<Renderer>();
                    bool any = false; Bounds b = new Bounds();
                    foreach (var r in rs) { if (r == null || !RadialPick.ModelRenderer(r.GetType().Name) || !r.enabled || !r.gameObject.activeInHierarchy) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                    _rect = any ? ProjectBounds(cam, b) : null;
                }
                catch { _rect = null; }
            }
            rect = _rect;
            return true;
        }

        /// <summary>WorldToScreenPoint works in camera pixels (y up, e.g. 1024x576 on a 2560x1440 screen); the ring works in screen pixels, y down (T-107, like Pip3DView.RemapMouse).</summary>
        private static void CamToScreen(Camera cam, double px, double py, out double sx, out double sy)
        {
            var r = cam.pixelRect;
            RadialMath.CameraToScreen(px - r.x, r.height - (py - r.y), Screen.width, Screen.height, 0, 0, r.width, r.height, out sx, out sy);
        }

        private double[] ProjectBounds(Camera cam, Bounds b)
        {
            var xs = new double[8]; var ys = new double[8]; var zs = new double[8];
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z);
                var p = cam.WorldToScreenPoint(c);
                CamToScreen(cam, p.x, p.y, out xs[i], out ys[i]);
                zs[i] = p.z;
            }
            return RadialPick.RectOfCorners(xs, ys, zs);   // corners behind the camera are skipped
        }

        private Type _objectBaseType;

        /// <summary>The game's own pick (InputHandler): Physics.RaycastAll(CameraManager.Main.ScreenPointToRay(mouse), 1000, -131073); true when a hit belongs to the selected unit.</summary>
        private bool RayHitsSelected(Camera cam, double px, double py, object unit, out int hits, out string first)
        {
            hits = 0; first = null;
            try
            {
                if (cam == null) return false;
                if (_objectBaseType == null) _objectBaseType = _api.TypeOf(CoreSpecs.ObjectBaseType);
                if (_objectBaseType == null) return false;
                var r = cam.pixelRect;
                double cx, cy;
                RadialMath.ScreenToCamera(px, py, Screen.width, Screen.height, 0, 0, r.width, r.height, out cx, out cy);
                var ray = cam.ScreenPointToRay(new Vector3((float)(r.x + cx), (float)(r.y + r.height - cy), 0));
                var rh = Physics.RaycastAll(ray, 1000f, -131073);
                bool sel = false;
                foreach (var h in rh)
                {
                    if (h.transform == null) continue;
                    var ob = h.transform.GetComponent(_objectBaseType);
                    if (ob == null) ob = h.transform.root.GetComponent(_objectBaseType);
                    if (ob == null) continue;
                    hits++;
                    if (first == null) first = _api.Get(ob, CoreSpecs.ObjName) as string ?? ob.ToString();
                    if (ReferenceEquals(ob, unit)) sel = true;
                }
                return sel;
            }
            catch { hits = 0; first = null; return false; }
        }

        private void Refuse(string why, RadialPickInfo info = null)
        {
            string line = _refusals.Next(why, Time.unscaledTime, _cfg.Debug);
            if (line == null) return;
            _log(line);
            if (why == "not_target" && info != null) _log(info.Format());
        }

        private double[] TryOpen(double px, double py)
        {
            string kind;
            var unit = _api.RadialTargetUnit(out kind);
            if (unit == null) { Refuse("no own selected unit"); return null; }
            bool onSel;
            object map; string hitVm;
            string view = _api.RadialProbe(px, py, Screen.height, unit, out onSel, out map, out hitVm);
            bool targetOk = false;
            double cx = px, cy = py;
            double R = _cfg.PickRadiusPx * _scale;
            var info = new RadialPickInfo { View = view, Mx = px, My = py, RadiusPx = R, ScreenW = Screen.width, ScreenH = Screen.height, OnSel = onSel, MapHitVm = hitVm, MapType = map == null ? null : map.GetType().Name };
            var cam = Cam();
            if (info != null && cam != null) { info.Camera = cam.name; info.CamW = cam.pixelWidth; info.CamH = cam.pixelHeight; }
            if (view == "3d")
            {
                double ux, uy, uz; double[] rect;
                bool unitOk = UnitScreen(unit, out ux, out uy, out uz, out rect);
                int hits; string first;
                bool rayHit = RayHitsSelected(cam, px, py, unit, out hits, out first);
                if (info != null) { info.UnitOk = unitOk; info.Ux = ux; info.Uy = uy; info.Uz = uz; info.Rect = rect; info.RayHits = hits; info.RayFirst = first; info.RayHitSelected = rayHit; }
                if (unitOk || rayHit)
                {
                    targetOk = RadialPick.Target3D(rayHit, px, py, ux, uy, uz, rect, R);
                    RadialPick.Centre(px, py, ux, uy, unitOk && uz > 0, 3 * R, out cx, out cy);
                }
            }
            else if (view == "map")
            {
                double mx2, my2;
                bool ptOk = _api.RadialMapPoint(map, unit, Screen.height, out mx2, out my2);
                targetOk = RadialPick.TargetMap(onSel, ptOk, px, py, mx2, my2, R);
                RadialPick.Centre(px, py, mx2, my2, ptOk, 3 * R, out cx, out cy);
                if (info != null) { info.MapPointOk = ptOk; info.MapX = mx2; info.MapY = my2; }
            }
            var env = _api.RadialEnvironment(view, targetOk);
            env.Focused = Application.isFocused;
            env.OnMapEnabled = _cfg.OnMap;
            env.OverModWindow = ModUiRects.Contains(px, py);   // T-070: not under the Overview window
            env.Block3DPatched = _api.PatchesApplied.Contains("radial_camera");
            string why = RadialMath.Allowed(env);
            if (why != null) { Refuse(why, info); return null; }
            var ring = MenuModel.Ring(_api.Actions, _api.RadialLayout, _api.RadialView(unit));
            if (_api.ActionDebug) _log("menu build (ring) uid=" + _api.UidOf(unit) + " : " + MenuModel.DescribeForLog(ring));
            bool any = false;
            foreach (var n in ring) if (n != null) any = true;
            if (!any) { Refuse("empty ring"); return null; }
            _refusals.Clear();
            double ox, oy;
            RadialMath.ClampCentre(cx, cy, ArcOut * _scale + 4, Screen.width, Screen.height, out ox, out oy);
            _unit = unit; _kind = _api.UnitKind4(unit) ?? kind; _view = view; _ring = ring; _g.SectorCount = ring.Length;
            _unitName = _api.RxGet(unit, CoreSpecs.ObjName) as string ?? _api.Get(unit, CoreSpecs.ObjName) as string;
            return new[] { ox, oy };
        }

        private void StartHotkey(double t, double mx, double my)
        {
            string kind;
            var unit = _api.RadialTargetUnit(out kind);
            if (unit == null) return;
            double ux, uy, uz; double[] rect;
            double cx = mx, cy = my;
            if (UnitScreen(unit, out ux, out uy, out uz, out rect) && uz > 0) { cx = ux; cy = uy; }
            bool onSel;
            string view = _api.RadialProbe(cx, cy, Screen.height, unit, out onSel);
            if (view == "ui" || view == null) view = "3d";
            var env = _api.RadialEnvironment(view, true);
            env.Focused = Application.isFocused; env.OnMapEnabled = _cfg.OnMap;
            env.OverModWindow = ModUiRects.Contains(cx, cy);
            env.Block3DPatched = _api.PatchesApplied.Contains("radial_camera");
            string why = RadialMath.Allowed(env);
            if (why != null) { Refuse(why); return; }
            var ring = MenuModel.Ring(_api.Actions, _api.RadialLayout, _api.RadialView(unit));
            if (_api.ActionDebug) _log("menu build (ring) uid=" + _api.UidOf(unit) + " : " + MenuModel.DescribeForLog(ring));
            double ox, oy;
            RadialMath.ClampCentre(cx, cy, ArcOut * _scale + 4, Screen.width, Screen.height, out ox, out oy);
            _unit = unit; _kind = _api.UnitKind4(unit) ?? kind; _view = view; _ring = ring; _g.SectorCount = ring.Length; _unitName = _api.RxGet(unit, CoreSpecs.ObjName) as string ?? _api.Get(unit, CoreSpecs.ObjName) as string;
            _g.OpenNow(t, ox, oy);
            _openedMs = t; _repaints = 0; _drawLogged = false;
            _hotkeyMode = true;
            _api.RadialOpen = true; _api.RadialViewName = view; _api.RadialUnitUid = _api.UidOf(unit);
        }

        // ---------- executing ----------

        private MenuNode Chosen()
        {
            int s = _g.Sector;
            if (s < 0 || s >= _ring.Length || _ring[s] == null) return null;
            var n = _ring[s];
            if (_g.ArcItem >= 0) return ArcCell(n, _g.ArcItem);
            return n;
        }

        private void Execute()
        {
            var n = Chosen();
            Close("execute");
            if (n == null || n.Kind == "page") return;   // T-328: release on "More" closes the ring without an action
            if (!n.Enabled) { Say(n.Label + ": " + (n.Reason ?? UiText.Get("radial.err.na"))); return; }
            if (!GameApi_Alive(_unit)) { Say(UiText.Get("radial.err.unit_lost")); return; }
            string r = _api.RadialRun(n, _unit);
            var d = Json.Parse(r) as Dictionary<string, object>;
            if (n.ActionId == "group_join" || n.ActionId == "group_leave") { Say(RadialText.GroupResult(d, n.Caption ?? n.Current)); return; }   // T-328
            bool ok = d != null && d.ContainsKey("ok") && d["ok"] is bool && (bool)d["ok"];
            string err = d != null && d.ContainsKey("error") ? d["error"] as string : null;
            string res = d != null && d.ContainsKey("result") ? d["result"] as string : null;
            string sum = RadialText.RestoreSummary(d);
            bool released = d != null && d.ContainsKey("released") && d["released"] is bool && (bool)d["released"];   // T-243: the mod's control was released first
            if (!ok) Say(n.Label + ": " + RadialText.ErrorText(err));
            else if (released) Say(n.Label + " — " + UiText.Get("radial.result.released"));
            else if (!string.IsNullOrEmpty(sum)) Say(n.Label + " — " + sum);
            else if (res == "partial" || res == "noop") Say(n.Label + " — " + RadialText.NotDone(res, NotDoneOf(d)));
            else Say(n.Label);
        }

        private static List<string> NotDoneOf(Dictionary<string, object> d)
        {
            var res = new List<string>();
            var l = d != null && d.ContainsKey("not_done") ? d["not_done"] as List<object> : null;
            if (l != null) foreach (var o in l) { var s = o as string; if (s != null) res.Add(s); }
            return res;
        }

        private bool GameApi_Alive(object u)
        {
            var c = u as UnityEngine.Object;
            return c != null;
        }

        private void Say(string text)
        {
            _result = text; _resultUntil = Time.unscaledTime + (float)ResultSeconds; _resultX = _g.CentreX; _resultY = _g.CentreY;
        }

        // ---------- drawing ----------

        private static Vector2 Dir(double deg) { double r = deg * Math.PI / 180.0; return new Vector2((float)Math.Sin(r), (float)-Math.Cos(r)); }

        private static void Wedge(Vector2 c, double r0, double r1, double a0, double a1, Color col)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(a1 - a0) / 6.0));
            GL.Color(col);
            for (int i = 0; i < steps; i++)
            {
                double s0 = a0 + (a1 - a0) * i / steps, s1 = a0 + (a1 - a0) * (i + 1) / steps;
                Vector2 p0 = c + Dir(s0) * (float)r0, p1 = c + Dir(s0) * (float)r1, p2 = c + Dir(s1) * (float)r1, p3 = c + Dir(s1) * (float)r0;
                GL.Vertex3(p0.x, p0.y, 0); GL.Vertex3(p1.x, p1.y, 0); GL.Vertex3(p2.x, p2.y, 0);
                GL.Vertex3(p0.x, p0.y, 0); GL.Vertex3(p2.x, p2.y, 0); GL.Vertex3(p3.x, p3.y, 0);
            }
        }

        private static void Dot(Vector2 c, double r, Color col)
        {
            GL.Color(col);
            for (int i = 0; i < 12; i++)
            {
                Vector2 a = c + Dir(i * 30) * (float)r, b = c + Dir((i + 1) * 30) * (float)r;
                GL.Vertex3(c.x, c.y, 0); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0);
            }
        }

        private static readonly Color Base = new Color(0.07f, 0.09f, 0.13f, 0.78f), Hot = new Color(0.16f, 0.42f, 0.74f, 0.92f),
            Grey = new Color(0.25f, 0.25f, 0.25f, 0.45f), OnDot = new Color(0.25f, 0.9f, 0.4f, 1f), OffDot = new Color(0.6f, 0.6f, 0.6f, 0.9f);

        public void OnGUI()
        {
            if (_g == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            bool showResult = _result != null && Time.unscaledTime < _resultUntil;
            if (!_g.Open && !showResult) return;
            try
            {
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = false };
                    _style.normal.textColor = Color.white;
                }
                _style.fontSize = Math.Max(9, (int)Math.Round(14 * _scale));
                if (_g.Open)
                {
                    _repaints++;
                    if (!_drawLogged && _cfg.Debug)
                    {
                        _drawLogged = true;
                        _log("radial draw: open view=" + _view + " centre=" + _g.CentreX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + _g.CentreY.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + " scale=" + _scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " screen=" + Screen.width + "x" + Screen.height + " repaints=" + _repaints + " glyphs=" + GlyphCount() + "/" + RingCount());
                    }
                    DrawGeometry(); DrawLabels();
                }
                if (showResult) Label(new Vector2((float)_resultX, (float)(_resultY - (OuterR + 18) * _scale)), 360 * _scale, _result, Color.white);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("radial draw failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void DrawGeometry()
        {
            var c = new Vector2((float)_g.CentreX, (float)_g.CentreY);
            double s = _scale;
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
                _mat.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                try
                {
                int cnt0 = _ring.Length;
                double w = RadialMath.SectorWidth(cnt0), half = cnt0 <= 1 ? 180.0 : w / 2 - 1.0;   // 2 deg gap between sectors; one sector = the whole ring
                for (int i = 0; i < cnt0; i++)
                {
                    var n = _ring[i];
                    Color col = n == null ? new Color(0.07f, 0.09f, 0.13f, 0.25f) : !n.Enabled ? Grey : (i == _g.Sector && !_g.ArcOpen) ? Hot : Base;
                    if (i == _g.Sector && _g.ArcOpen && n != null) col = new Color(0.12f, 0.3f, 0.55f, 0.9f);
                    double ci = RadialMath.SectorCentre(i, cnt0);
                    Wedge(c, InnerR * s, OuterR * s, ci - half, ci + half, col);
                    if (n != null && n.Checked.HasValue) Dot(c + Dir(ci) * (float)((InnerR + 12) * s), 4 * s, n.Checked.Value ? OnDot : OffDot);
                }
                Dot(c, DeadR * s * 0.5, new Color(0.9f, 0.9f, 0.9f, 0.6f));
                if (_g.ArcOpen && _g.Sector >= 0 && _ring[_g.Sector] != null)
                {
                    var p = _ring[_g.Sector];
                    int cnt = ArcCells(p); double span = SpanFor(p, cnt), cell = span / Math.Max(1, cnt), mid = RadialMath.SectorCentre(_g.Sector, _ring.Length);
                    for (int k = 0; k < cnt; k++)
                    {
                        var ch = ArcCell(p, k);
                        double a0 = mid - span / 2 + k * cell + 1.5, a1 = mid - span / 2 + (k + 1) * cell - 1.5;
                        Wedge(c, ArcIn * s, ArcOut * s, a0, a1, !ch.Enabled ? Grey : k == _g.ArcItem ? Hot : Base);
                        if (ch.Checked.HasValue) Dot(c + Dir(mid - span / 2 + (k + 0.5) * cell) * (float)((ArcIn + 10) * s), 4 * s, ch.Checked.Value ? OnDot : OffDot);
                    }
                }
                }
                finally { GL.End(); }
            }
            finally { GL.PopMatrix(); }
        }

        private void Label(Vector2 centre, double width, string text, Color col, int baseFont = 0)
        {
            int keep = _style.fontSize;
            if (baseFont > 0) _style.fontSize = Math.Max(8, (int)Math.Round(baseFont * _scale));   // T-328: arc captions are smaller than ring labels
            try { LabelAt(centre, width, text, col); } finally { _style.fontSize = keep; }
        }

        private void LabelAt(Vector2 centre, double width, string text, Color col)
        {
            var r = new Rect(centre.x - (float)width / 2, centre.y - (float)(10 * _scale), (float)width, (float)(20 * _scale));
            _style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _style);
            _style.normal.textColor = col;
            GUI.Label(r, text, _style);
        }

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "air": case "helo": case "sub": return UiText.Get("radial.kind." + kind);
                default: return UiText.Get("radial.kind.ship");
            }
        }

        private Texture2D GlyphTexture(string key, int px)
        {
            string ck = key + ":" + px;
            Texture2D t;
            if (_glyphTex.TryGetValue(ck, out t) && t != null) return t;
            byte[] m = RadialGlyphs.Mask(key, px);
            if (m == null) return null;
            t = new Texture2D(px, px, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pix = new Color32[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    pix[(px - 1 - y) * px + x] = new Color32(255, 255, 255, m[y * px + x]);   // mask row 0 = top, texture rows go bottom-up
            t.SetPixels32(pix);
            t.Apply(false, true);
            _glyphTex[ck] = t;
            return t;
        }

        private int GlyphCount() { int k = 0; if (_cfg.Icons) foreach (var n in _ring) if (n != null && RadialGlyphs.KeyFor(n) != null) k++; return k; }
        private int RingCount() { int k = 0; foreach (var n in _ring) if (n != null) k++; return k; }

        private void ClearGlyphs()
        {
            foreach (var t in _glyphTex.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _glyphTex.Clear();
        }

        /// <summary>Glyph centred at <paramref name="centre"/>: black shadow (+1 px), then the state tint (ADR-0047 section 5).</summary>
        private void DrawGlyph(Vector2 centre, string key, int px, GlyphState st)
        {
            int ringPx = RadialGlyphs.RingPx(_scale);
            if (ringPx != _glyphRingPx) { ClearGlyphs(); _glyphRingPx = ringPx; }   // scale changed: drop stale sizes
            var tex = GlyphTexture(key, px);
            if (tex == null) return;
            var r = new Rect(centre.x - px / 2f, centre.y - px / 2f, px, px);
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, (float)RadialGlyphs.ShadowAlpha);
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, r.width, r.height), tex);
            float[] tn = RadialGlyphs.Tint(st);
            GUI.color = new Color(tn[0], tn[1], tn[2], tn[3]);
            GUI.DrawTexture(r, tex);
            GUI.color = old;
        }

        private void DrawLabels()
        {
            var c = new Vector2((float)_g.CentreX, (float)_g.CentreY);
            double s = _scale;
            for (int i = 0; i < _ring.Length; i++)
            {
                var n = _ring[i];
                if (n == null) continue;
                double ang = RadialMath.SectorCentre(i, _ring.Length);
                var col = n.Enabled ? Color.white : new Color(0.7f, 0.7f, 0.7f, 0.8f);
                bool hotW = i == _g.Sector && !_g.ArcOpen;
                string key = _cfg.Icons ? RadialGlyphs.KeyFor(n) : null;
                var pos = c + Dir(ang) * (float)((InnerR + OuterR) / 2 * s + Math.Abs(Math.Sin(ang * Math.PI / 180)) * 8 * s);
                if (key == null)
                {
                    string t = n.Kind == "group" && n.Id != "more" && n.Current == null ? n.Short + " >" : n.Short + (n.Kind == "group" ? " >" : "");
                    Label(pos, 104 * s, t, col);
                    if (n.Current != null) Label(c + Dir(ang) * (float)((InnerR + OuterR) / 2 * s) + new Vector2(0, (float)(13 * s)), 104 * s, n.Current, new Color(0.8f, 0.9f, 1f, 0.9f));
                    continue;
                }
                int px = RadialGlyphs.RingPx(s);
                int dpx = hotW ? RadialGlyphs.HotPx(px) : px;
                DrawGlyph(pos, key, dpx, RadialGlyphs.StateOf(n, hotW));
                if (n.Kind == "group") Label(pos + new Vector2(dpx / 2f + (float)(5 * s), 0), 12 * s, ">", col);
                if (n.Current != null) Label(pos + new Vector2(0, (float)(dpx / 2.0 + 8 * s)), 104 * s, n.Current, new Color(0.8f, 0.9f, 1f, 0.9f));
            }
            MenuNode hover = null;
            if (_g.ArcOpen && _g.Sector >= 0 && _ring[_g.Sector] != null)
            {
                var p = _ring[_g.Sector];
                int cnt = ArcCells(p); double span = SpanFor(p, cnt), cell = span / Math.Max(1, cnt), mid = RadialMath.SectorCentre(_g.Sector, _ring.Length);
                for (int k = 0; k < cnt; k++)
                {
                    var ch = ArcCell(p, k);
                    var acol = ch.Enabled ? Color.white : new Color(0.7f, 0.7f, 0.7f, 0.8f);
                    bool captioned = ch.Caption != null;   // T-328: glyph above a caption, count badge at the upper right
                    var apos = c + Dir(mid - span / 2 + (k + 0.5) * cell) * (float)((ArcIn + ArcOut) / 2 * s - (captioned ? 8 * s : 0));
                    string akey = _cfg.Icons ? RadialGlyphs.KeyFor(ch) : null;
                    if (akey == null) { Label(apos, 100 * s, ch.Caption != null ? RadialMath.Ellipsize(ch.Caption, 10) : ch.Short, acol); continue; }
                    int apx = RadialGlyphs.ArcPx(s);
                    bool ahot = k == _g.ArcItem;
                    int adpx = ahot ? RadialGlyphs.HotPx(apx) : apx;
                    DrawGlyph(apos, akey, adpx, RadialGlyphs.StateOf(ch, ahot));
                    if (ch.Kind == "group") Label(apos + new Vector2(adpx / 2f + (float)(5 * s), 0), 12 * s, ">", acol);
                    if (captioned)
                    {
                        Label(apos + new Vector2(0, (float)(adpx / 2.0 + 9 * s)), 92 * s, RadialMath.Ellipsize(ch.Caption, 10), acol, 12);
                        if (!string.IsNullOrEmpty(ch.Badge)) Label(apos + new Vector2((float)(adpx / 2.0 + 4 * s), (float)(-adpx / 2.0 + 2 * s)), 24 * s, ch.Badge, ch.Enabled ? new Color(0.8f, 0.9f, 1f, 1f) : acol, 11);
                    }
                }
                if (_g.ArcItem >= 0 && _g.ArcItem < cnt) hover = ArcCell(p, _g.ArcItem);
                else if (p.ArcPage > 0) hover = p;   // T-328: nothing under the cursor: the tip shows the parent, not an empty line
            }
            else if (_g.Sector >= 0 && _ring[_g.Sector] != null) hover = _ring[_g.Sector];
            string line = (_unitName ?? "?") + " · " + KindLabel(_kind);
            Label(c + new Vector2(0, (float)((ArcOut + 14) * s)), 420 * s, line, new Color(1, 1, 1, 0.85f));
            if (hover != null)
            {
                string h = hover.Enabled ? hover.Label : hover.Label + ": " + (hover.Reason ?? "");
                if (_cfg.ShowVoiceHint && UiText.Lang == "ru" && hover.Enabled && !string.IsNullOrEmpty(hover.SayRu)) h += " · " + UiText.Fmt("radial.say_hint", hover.SayRu);   // the voice is Russian
                Label(c + new Vector2(0, (float)((ArcOut + 34) * s)), 520 * s, h, new Color(0.8f, 0.95f, 1f, 1f));
            }
        }
    }
}
