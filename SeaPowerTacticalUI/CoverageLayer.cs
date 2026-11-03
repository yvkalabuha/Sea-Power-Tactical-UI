using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// The coverage layer inside the game's map (T-090, ADR-0034): a Noesis.Image whose source is a RenderTexture of the mod, inserted into
    /// MapControl.MapBase.Children right above the base layer, so every menu and window of the game ends up over it. The zone and the legend are
    /// drawn into the texture (CoverageOverlay.DrawGeo + CoverageLegendRt) from Camera.onPreRender, only when something changed.
    /// Noesis types are reached by reflection (resolved on the main thread). Not part of the offline selfcheck (Unity). Never throws.
    /// </summary>
    internal sealed class CoverageLayer : IDisposable, Ui.ICoverageLayerDiag
    {
        private readonly GameApi _api;
        private readonly CoverageConfig _cfg;
        private readonly CoverageOverlay _overlay;
        private readonly CoverageLegendRt _legend;
        private readonly Action<string> _log;

        private Type _tImage, _tTextureSource, _tPanel, _tCollection;
        private PropertyInfo _pSource, _pStretch, _pHit, _pVisibility, _pChildren, _pCount, _pWidth, _pHeight, _pHAlign, _pVAlign;
        private MethodInfo _mSetZ, _mGetZ, _mInsert, _mRemove, _mIndexOf, _mItem;
        private object _image, _textureSource, _parentMap;
        private object _visVisible, _visCollapsed;
        private RenderTexture _rt;
        private double[] _pts = new double[10], _ptsSpare = new double[10];   // T-151: reused every OnPreRender instead of a new array
        private bool _everAttached;
        private bool _inited, _visShown, _forceDirty = true, _drawnFull;
        private int _lastFrame = -1, _drawnVersion = -1, _failStreak;
        private double[] _prevPts;
        private MapPxKey _prevKey; private bool _haveKey;            // T-309: the last drawn key of an exact (globe) projection
        private readonly double[] _o2o = new double[6];
        private int _lastVerts, _lastCut;
        private object _curMap;
        private float _lastWarn = -1e9f;
        private double _mapW, _mapH;                       // local map units (Image.Width/Height follow them)
        private double[] _curRect;                         // the map in screen pixels (legend layout only)
        private readonly FailGate _gate = new FailGate(5.0);
        internal int RtCount { get { return (_rt != null ? 1 : 0) + _defer.Count; } }
        private readonly FrameDeferQueue<RenderTexture> _defer = new FrameDeferQueue<RenderTexture>(5);
        private string _lastSizeLine = "";
        private int _lastCamFrame = -1, _lastDrawFrame = -1, _wantedSince = -1;   // T-190: frame numbers (-1 = never)
        private int _draws;
        private double _lastDrawTime = -1;
        private string _drawPath = "none", _camName = "";
        private string _legendWhy = "none";
        private bool _legendsOk = true;                    // T-233: false on the small (un-maximised) map: no legend is drawn, _legendWhy = "small_map"
        private double[] _obstacleRect;                    // T-233: the map rectangle the _obstacles were computed for
        private double[] _legendRect;                      // the legend column of the last redraw, map-local screen px (DiagJson)
        private double[] _panelDiag;
        private string _obstacleSig = "";
        private readonly List<double[]> _obsScratch = new List<double[]>(3);
        private readonly StringBuilder _obsSb = new StringBuilder();
        private double _nextObstacle;
        private readonly List<double[]> _obstacles = new List<double[]>();
        private static readonly string[] LegendAvoidOwners = { "overview" };
        private double _nextAsw;
        private int _lastCovVerts;                          // T-311: vertices of the coverage section of the last redraw
        private RasterEmitStats _lastCovStats;
        private double _lastTruncLog = -1e9;
        private bool _aswDrawn;
        // customer 2026-10-22: the legends stand in the top-right corner of the map, under the game's time panel (replaces the 0.28.10 World View lift)
        private const double PanelGap = 6;                 // screen px between the time panel's bottom edge and the first legend
        private readonly TimePanelProbe _timePanel;
        private double _panelOffset;                       // map-local screen px the legends move down to clear the time panel
        private double _legendW;                           // widest legend box of the last redraw in screen px (0 = none drawn yet)

        /// <summary>ASW geometry drawn into the same texture while the layer owns the map (T-107 A4). Set by UiPlugin.</summary>
        public AswOverlay Asw;
        /// <summary>v0.18 (T-112): CAP zones/lanes drawn into the same render texture.</summary>
        public CapOverlay Cap;
        /// <summary>T-137 C: further consumers of the render texture (range rings, T-138 intercept course). Main thread only.</summary>
        public readonly List<IRtOverlay> Extra = new List<IRtOverlay>();
        private float _lastExtraWarn = -1e9f;

        public CoverageLayer(GameApi api, CoverageConfig cfg, CoverageOverlay overlay, CoverageLegendRt legend, Action<string> log)
        {
            _api = api; _cfg = cfg; _overlay = overlay; _legend = legend; _log = log ?? (m => { });
            _timePanel = new TimePanelProbe(api, overlay, _log);
        }

        /// <summary>The element sits inside the current MapBase.</summary>
        public bool Attached { get; private set; }

        /// <summary>Consecutive failed insertions (UiPlugin: 3 in a row in "auto" mode -> back to imgui).</summary>
        public int FailStreak { get { return _failStreak; } }

        private void Warn(string m)
        {
            if (Time.unscaledTime - _lastWarn < 60f) return;
            _lastWarn = Time.unscaledTime;
            _log("radar layer: " + m);
        }

        /// <summary>Resolves Noesis.Image, TextureSource(Texture), Source/Stretch/IsHitTestVisible/Visibility, Panel.Children, SetZIndex; creates the (hidden) element.</summary>
        public bool TryInit(out string error)
        {
            error = null;
            try
            {
                _tImage = _api.TypeOf(RadarSpecs.ImageType);
                _tTextureSource = _api.TypeOf(RadarSpecs.TextureSourceType);
                _tPanel = _api.TypeOf(RadarSpecs.PanelType);
                _tCollection = _api.TypeOf(RadarSpecs.CollectionType);
                if (_tImage == null || _tTextureSource == null || _tPanel == null || _tCollection == null) { error = "Noesis types not found"; return false; }
                _pSource = _api.Member(RadarSpecs.ImgSource) as PropertyInfo; _pStretch = _api.Member(RadarSpecs.ImgStretch) as PropertyInfo;
                _pHit = _api.Member(RadarSpecs.ImgHitTestVisible) as PropertyInfo; _pVisibility = _api.Member(RadarSpecs.ImgVisibility) as PropertyInfo;
                _pWidth = _api.Member(RadarSpecs.ImgWidth) as PropertyInfo; _pHeight = _api.Member(RadarSpecs.ImgHeight) as PropertyInfo;
                _pHAlign = _api.Member(RadarSpecs.ImgHAlign) as PropertyInfo; _pVAlign = _api.Member(RadarSpecs.ImgVAlign) as PropertyInfo;
                _pChildren = _api.Member(RadarSpecs.PanelChildren) as PropertyInfo; _pCount = _api.Member(RadarSpecs.CollCount) as PropertyInfo;
                _mSetZ = _api.Member(RadarSpecs.PanelSetZ) as MethodInfo;
                _mGetZ = _api.Member(RadarSpecs.PanelGetZ) as MethodInfo;
                _mInsert = _api.Member(RadarSpecs.CollInsert) as MethodInfo; _mRemove = _api.Member(RadarSpecs.CollRemove) as MethodInfo; _mIndexOf = _api.Member(RadarSpecs.CollIndexOf) as MethodInfo;
                _mItem = _api.Member(RadarSpecs.CollItem) as MethodInfo;
                if (_pSource == null || _pStretch == null || _pHit == null || _pVisibility == null || _pChildren == null || _pCount == null ||
                    _mSetZ == null || _mGetZ == null || _mInsert == null || _mRemove == null || _mIndexOf == null || _mItem == null)
                { error = "Noesis members not found"; return false; }
                if (_pWidth == null || _pHeight == null) { error = "FrameworkElement.Width/Height not found"; return false; }
                var ctor = _api.Member(RadarSpecs.TextureSourceCtor) as ConstructorInfo;
                if (ctor == null) { error = "TextureSource(Texture) constructor not found"; return false; }
                _visVisible = Enum.Parse(_pVisibility.PropertyType, "Visible");
                _visCollapsed = Enum.Parse(_pVisibility.PropertyType, "Collapsed");
                _image = Activator.CreateInstance(_tImage);
                _pHit.SetValue(_image, false, null);
                _pStretch.SetValue(_image, Enum.Parse(_pStretch.PropertyType, "Fill"), null);
                _pVisibility.SetValue(_image, _visCollapsed, null);
                SetEnum(_pHAlign, "Left"); SetEnum(_pVAlign, "Top");   // explicit size from (0,0): no centring of an image of another size
                _inited = true;
                return true;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
        }

        private void SetEnum(PropertyInfo p, string name)
        {
            try { if (p != null && p.PropertyType.IsEnum) p.SetValue(_image, Enum.Parse(p.PropertyType, name), null); } catch { }
        }

        private void SetSize(double w, double h)
        {
            if (Math.Abs(w - _mapW) < 0.5 && Math.Abs(h - _mapH) < 0.5) return;
            _pWidth.SetValue(_image, Convert.ChangeType(w, _pWidth.PropertyType, System.Globalization.CultureInfo.InvariantCulture), null);
            _pHeight.SetValue(_image, Convert.ChangeType(h, _pHeight.PropertyType, System.Globalization.CultureInfo.InvariantCulture), null);
            _mapW = w; _mapH = h;
            _forceDirty = true;
        }

        private object Children(object map) { return map == null ? null : _pChildren.GetValue(map, null); }

        private int Count(object children) { return Convert.ToInt32(_pCount.GetValue(children, null)); }

        private void SetVisible(bool on)
        {
            if (_image == null || _visShown == on) return;
            _pVisibility.SetValue(_image, on ? _visVisible : _visCollapsed, null);
            if (!on) ModUiRects.ClearPassive("map_legend");   // T-240
            _visShown = on;
            _forceDirty = true;
        }

        private void Detach()
        {
            try
            {
                var ch = Children(_parentMap);
                if (ch != null && _image != null) _mRemove.Invoke(ch, new[] { _image });
            }
            catch { }
            _parentMap = null;
            Attached = false;
            ModUiRects.ClearPassive("map_legend");   // T-240
        }

        private bool Insert(object map)
        {
            try
            {
                var ch = Children(map);
                if (ch == null) return false;
                int n = Count(ch);
                var names = new List<string>(n); var zs = new List<int>(n);
                for (int i = 0; i < n; i++)
                {
                    var c = _mItem.Invoke(ch, new object[] { i });
                    names.Add(c == null ? "null" : c.GetType().Name);
                    zs.Add(c == null ? 0 : Convert.ToInt32(_mGetZ.Invoke(null, new[] { c })));
                }
                int z;
                int at = CoverageMath.MapChildInsertIndex(names, zs, _cfg.LayerIndex, out z);
                _mInsert.Invoke(ch, new[] { (object)at, _image });
                _mSetZ.Invoke(null, new[] { _image, (object)z });
                if (_cfg.Debug)
                {
                    var sb = new StringBuilder("radar layer: map children [");
                    for (int i = 0; i < n; i++) sb.Append(i > 0 ? "; " : "").Append(i).Append(':').Append(names[i]).Append(" z=").Append(zs[i]);
                    _log(sb.Append("], insert at ").Append(at).Append(" z=").Append(z).ToString());
                }
                bool moved = _parentMap != null || _everAttached;
                _parentMap = map;
                _everAttached = true;
                _log("radar layer: attach to map " + Math.Round(_api.D2(_api.Get(map, RadarSpecs.ElementWidth))) + "x" + Math.Round(_api.D2(_api.Get(map, RadarSpecs.ElementHeight))) + (moved ? " (re-attach)" : " (first)"));
                Attached = true;
                if (_overlay != null && _overlay.Snaps != null) _overlay.Snaps.OnMapChanged(map);   // T-309
                _haveKey = false;
                _failStreak = 0;
                _forceDirty = true;
                return true;
            }
            catch (Exception e)
            {
                Attached = false; _parentMap = null;
                _failStreak++;
                Warn("insert failed (" + (e.InnerException ?? e).Message + ")");
                return false;
            }
        }

        private bool StillInside()
        {
            try
            {
                var ch = Children(_parentMap);
                return ch != null && Convert.ToInt32(_mIndexOf.Invoke(ch, new[] { _image })) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// The texture only grows (NeedNewRt): a shrinking or resized map never recreates it on the fly. The old one is released 5 frames after
        /// Noesis has been given the new source, never in the same frame (T-107 A3, hypothesis H1).
        /// </summary>
        private void EnsureTexture(double mapW, double mapH, double pxScale)
        {
            int w, h;
            int cw = _rt == null ? 0 : _rt.width, ch = _rt == null ? 0 : _rt.height;
            if (!MapLayerMath.NeedNewRt(cw, ch, mapW, mapH, CoverageMath.ClampRtScale(_cfg.RtScale), pxScale, out w, out h)) return;
            try
            {
                var old = _rt;
                var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "VoiceBot.RadarCoverage", useMipMap = false, filterMode = FilterMode.Bilinear };
                rt.Create();
                var src = Activator.CreateInstance(_tTextureSource, new object[] { rt });
                _pSource.SetValue(_image, src, null);
                _rt = rt; _textureSource = src;
                if (old != null) _defer.Add(old);
                _log("radar layer: rt recreate " + cw + "x" + ch + " -> " + w + "x" + h + (old != null ? " (old released in 5 frames)" : ""));
                _forceDirty = true;
                _prevPts = null; _haveKey = false;
            }
            catch (Exception e) { Warn("render texture failed (" + (e.InnerException ?? e).Message + ")"); }
        }

        private void ReleaseDue()
        {
            foreach (var rt in _defer.Tick())
            {
                try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            }
        }

        /// <summary>The layer is drawn (or can be) now: attached, visible and not switched off after a failure.</summary>
        public bool Active { get { return _inited && Attached && _visShown && _rt != null && _gate.Allowed(Time.unscaledTime); } }

        /// <summary>Heartbeat part: "layer=on|off|collapsed rt=WxH".</summary>
        public string StateText()
        {
            string st = !_inited || !Attached ? "off" : (_visShown ? "on" : "collapsed");
            return "layer=" + st + " rt=" + (_rt == null ? "0x0" : _rt.width + "x" + _rt.height) + " path=" + _drawPath;
        }

        private bool WantShown()
        {
            return _api.OverlayOn || (Asw != null && Asw.HasContent()) || (Cap != null && Cap.HasContent()) || RtOverlays.AnyContent(Extra);
        }

        /// <summary>Main thread (UiPlugin.Update): find the main map, (re)insert the element once, show/hide it, grow the texture, keep Image.Width/Height = the map.</summary>
        public void Update()
        {
            if (!_inited) return;
            try
            {
                ReleaseDue();
                // a game menu (pause / options / After Action Report) covers the map: collapse the layer, OnPreRender stops drawing (_visShown false)
                if (_api.GameMenuHidden || !WantShown() || !_gate.Allowed(Time.unscaledTime))
                {
                    if (Attached) SetVisible(false);
                    _curMap = null; _wantedSince = -1;
                    return;
                }
                object map; double[] rect;
                if (!_overlay.TryMapPixels(out map, out rect))
                {
                    // 3D view (or the map is covered): the element stays where it is, just collapsed; no re-parenting
                    if (Attached) SetVisible(false);
                    _curMap = null;
                    return;
                }
                _curMap = map; _curRect = rect;
                if (Attached && (!ReferenceEquals(map, _parentMap) || !StillInside())) Detach();
                if (!Attached && !Insert(map)) return;
                double mw = _api.D2(_api.Get(map, RadarSpecs.ElementWidth)), mh = _api.D2(_api.Get(map, RadarSpecs.ElementHeight));
                if (mw < 2 || mh < 2) { SetVisible(false); return; }
                EnsureTexture(mw, mh, rect[2] / mw);
                SetSize(mw, mh);
                SetVisible(_rt != null);
                PumpPanel(rect);
                PumpLegendAllowed(rect);
                PumpObstacles(rect);
                if (_cfg.Debug) LogSize();
                if (_wantedSince < 0) _wantedSince = Time.frameCount;
            }
            catch (Exception e) { Warn("update: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Re-reads the time panel (TimePanelProbe, once a second; the scaled fixed rectangle when the element is not found) and the offset of the
        /// legends under it; a change redraws the texture and is logged once.</summary>
        private void PumpPanel(double[] rect)
        {
            if (!_timePanel.Pump(rect, Time.unscaledTime)) return;
            double[] panel = _timePanel.Rect ?? CoverageMath.TimePanelFallback(Screen.width, Screen.height);
            double off = CoverageMath.LegendPanelOffset(rect, panel, _legendW > 0 ? _legendW : 270, PanelGap);
            if (Math.Abs(off - _panelOffset) < 0.5) return;
            _log("radar layer: legends under the time panel, offset " + off.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " px map=" +
                TimePanelProbe.RectText(rect) + " panel=" + TimePanelProbe.RectText(panel) + " src=" + (_timePanel.Rect != null ? _timePanel.ElementType : "fallback"));
            _panelOffset = off;
            _forceDirty = true;
        }

        /// <summary>T-233: legends only on a maximised map (or one stretched over half the screen). A change redraws the texture at once and is logged once.</summary>
        private void PumpLegendAllowed(double[] rect)
        {
            string why;
            bool ok = CoverageMath.LegendAllowed(_overlay == null || _overlay.MapMaximized, rect[2], rect[3], Screen.width, Screen.height, out why);
            if (ok == _legendsOk) return;
            _legendsOk = ok;
            _forceDirty = true;
            if (!ok) _legendWhy = why;
            else if (_legendWhy == "small_map") _legendWhy = "none";
            _log(ok ? "radar layer: legends shown" : "radar layer: legends hidden on the small map");
        }

        /// <summary>T-190 A6: the places the legend column must not cover (the Overview window, the time panel) in map-local screen px; a changed
        /// signature (rounded to 4 px, polled at most 4 times a second) redraws the texture.</summary>
        private void PumpObstacles(double[] mapRect)
        {
            double now = Time.unscaledTime;
            // T-233: the obstacles are in map-local px; when the map moved or was resized they belong to the old rectangle (live: dy=448 on a 380 px map right after a switch)
            if (_obstacleRect == null || Math.Abs(_obstacleRect[0] - mapRect[0]) > 0.5 || Math.Abs(_obstacleRect[1] - mapRect[1]) > 0.5 || Math.Abs(_obstacleRect[2] - mapRect[2]) > 0.5 || Math.Abs(_obstacleRect[3] - mapRect[3]) > 0.5)
            {
                _obstacleRect = new[] { mapRect[0], mapRect[1], mapRect[2], mapRect[3] };
                _obstacles.Clear(); _obstacleSig = ""; _nextObstacle = 0;
                _forceDirty = true;
            }
            if (now < _nextObstacle) return;
            _nextObstacle = now + 0.25;
            // T-195: the buffers are reused (no per-poll List / StringBuilder / string); the signature string is built only when it changed.
            var list = _obsScratch; list.Clear();
            var sb = _obsSb; sb.Length = 0;
            var named = ModUiRects.Named();
            foreach (string owner in LegendAvoidOwners)
            {
                double[] r;
                if (!named.TryGetValue(owner, out r)) continue;
                list.Add(new[] { r[0] - mapRect[0], r[1] - mapRect[1], r[2], r[3] });
            }
            double[] panel = _timePanel.Rect;
            if (panel != null) list.Add(new[] { panel[0] - mapRect[0], panel[1] - mapRect[1], panel[2], panel[3] });
            foreach (var o in list)
                sb.Append(Math.Round(o[0] / 4)).Append(',').Append(Math.Round(o[1] / 4)).Append(',').Append(Math.Round(o[2] / 4)).Append(',').Append(Math.Round(o[3] / 4)).Append(';');
            if (SameSig(sb, _obstacleSig)) return;
            _obstacleSig = sb.ToString();
            _obstacles.Clear(); _obstacles.AddRange(list);
            _forceDirty = true;
        }

        private static bool SameSig(StringBuilder sb, string s)
        {
            if (sb.Length != s.Length) return false;
            for (int i = 0; i < s.Length; i++) if (sb[i] != s[i]) return false;
            return true;
        }

        /// <summary>Dev channel (ui_state radar.layer): the state of the layer, the draw path and the legend place.</summary>
        public string DiagJson()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string st = !_inited || !Attached ? "off" : (_visShown ? "on" : "collapsed");
            string cam = string.IsNullOrEmpty(_camName) ? "null" : "\"" + _camName.Replace("\\", "/").Replace("\"", "'") + "\"";
            string age = _lastDrawTime < 0 ? "null" : Math.Round(Time.unscaledTime - _lastDrawTime, 1).ToString(ci);
            Func<double[], string> arr = r => r == null ? "null" : "[" + Math.Round(r[0]).ToString(ci) + "," + Math.Round(r[1]).ToString(ci) + "," + Math.Round(r[2]).ToString(ci) + "," + Math.Round(r[3]).ToString(ci) + "]";
            return "{\"state\":\"" + st + "\",\"path\":\"" + _drawPath + "\",\"cam\":" + cam + ",\"draws\":" + _draws.ToString(ci) +
                ",\"last_draw_age_s\":" + age + ",\"legend\":" + arr(_legendRect) + ",\"legend_screen\":" + arr(_legendRect == null || _curRect == null ? null : new[] { _curRect[0] + _legendRect[0], _curRect[1] + _legendRect[1], _legendRect[2], _legendRect[3] }) + ",\"legend_on\":" + (_overlay == null || _overlay.LegendVisible ? "true" : "false") + ",\"legend_why\":\"" + _legendWhy + "\",\"panel\":" + arr(_timePanel.Rect) +
                ",\"panel_src\":\"" + (_timePanel.Rect != null ? "Grid" : "fallback") + "\"" +
                (_overlay != null && _overlay.Snaps != null ? _overlay.Snaps.DiagJson() : "") + ",\"projector_verts\":" + _lastVerts.ToString(ci) + ",\"projector_cut\":" + _lastCut.ToString(ci) +
                ",\"projector_cov_verts\":" + _lastCovVerts.ToString(ci) + ",\"projector_cov_quads\":" + _lastCovStats.Quads.ToString(ci) +
                ",\"projector_cov_culled\":" + (_lastCovStats.RunsCulled + _lastCovStats.PiecesCulled).ToString(ci) + ",\"projector_cov_coarsen\":" + Math.Max(1, _lastCovStats.Coarsen).ToString(ci) +
                ",\"projector_cov_trunc\":" + (_lastCovStats.Truncated ? "true" : "false") + "}";
        }

        private void LogSize()
        {
            string iw = Math.Round(_api.D2(_api.Get(_image, RadarSpecs.ElementWidth))) + "x" + Math.Round(_api.D2(_api.Get(_image, RadarSpecs.ElementHeight)));
            string line = "image " + iw + " map " + Math.Round(_mapW) + "x" + Math.Round(_mapH) + " rt " + (_rt == null ? "0x0" : _rt.width + "x" + _rt.height);
            if (line == _lastSizeLine) return;
            _lastSizeLine = line;
            _log("radar layer size: " + line);
        }

        private bool FlipY()
        {
            return MapLayerMath.FlipDefault(_cfg.FlipY);
        }

        private void ClearTexture()
        {
            var prev = RenderTexture.active;
            try { RenderTexture.active = _rt; GL.Clear(true, true, new Color(0, 0, 0, 0)); }
            finally { RenderTexture.active = prev; }
        }

        /// <summary>The draw path of the last frame: "camera" | "end_of_frame" | "none".</summary>
        public string DrawPath { get { return _drawPath; } }

        private static bool MainUsable()
        {
            var m = Camera.main;
            return m != null && m.enabled && m.targetTexture == null;
        }

        /// <summary>Camera.onPreRender (T-190): a screen camera (not a PIP / mini-map one) that is Camera.main, or any screen camera while Camera.main is unusable
        /// (the full-screen map has none); once per frame, only when something changed.</summary>
        public void OnPreRender(Camera cam)
        {
            if (!_inited || !Attached || _rt == null || !_visShown) return;
            if (cam == null) return;
            if (!MapLayerMath.AcceptLayerCamera(cam.targetTexture == null, cam.enabled, cam == Camera.main, MainUsable())) return;
            _lastCamFrame = Time.frameCount;
            if (_camName != cam.name) _camName = cam.name;
            if (!_gate.Allowed(Time.unscaledTime)) return; // T-195: no path switch / log while the crash guard blocks drawing
            if (_drawPath == "end_of_frame") { _drawPath = "camera"; _log("radar layer: drawing from camera " + cam.name); }
            DrawFrame("camera");
        }

        /// <summary>End of the frame (CoverageLayerDriver, ADR-0034 item 3 fallback): draws when no suitable camera has rendered the layer for 30 frames.</summary>
        public void OnEndOfFrame()
        {
            if (!_inited || !Attached || _rt == null || !_visShown) return;
            if (!MapLayerMath.EndOfFrameDue(Time.frameCount, _lastDrawFrame, _lastCamFrame, _wantedSince, MapLayerMath.LayerCamGraceFrames)) return;
            if (!_gate.Allowed(Time.unscaledTime)) return; // T-195: no path switch / log while the crash guard blocks drawing
            if (_drawPath != "end_of_frame")
            {
                _drawPath = "end_of_frame";
                _log("radar layer: no screen camera for " + MapLayerMath.LayerCamGraceFrames + " frames (main=" + (Camera.main == null ? "none" : Camera.main.name) + ", cams=[" + CamList() + "]), drawing at the end of the frame");
            }
            DrawFrame("end_of_frame");
        }

        private static string CamList()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var c in Camera.allCameras)
                {
                    if (c == null) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(c.name).Append(':').Append(c.enabled ? 1 : 0).Append(':').Append(c.targetTexture != null ? 1 : 0).Append(':').Append(c.depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private void DrawFrame(string path)
        {
            double now = Time.unscaledTime;
            if (!_gate.Allowed(now)) return;
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;
            try
            {
                var frame = _api.LatestFrame;
                bool cov = _api.OverlayOn && frame != null && frame.Profiles != null && frame.Profiles.Count > 0;
                bool aswOnly = Asw != null && Asw.HasContent(), capOnly = Cap != null && Cap.HasContent();
                bool extraOn = RtOverlays.AnyContent(Extra);
                bool asw = aswOnly || capOnly || extraOn;
                if (!cov && !asw)
                {
                    if (_drawnFull || _forceDirty) { ClearTexture(); _drawnFull = false; _forceDirty = false; _prevPts = null; _drawnVersion = -1; _aswDrawn = false; _haveKey = false; }
                    return;
                }
                var map = _curMap;
                if (map == null || _mapW < 2) return;
                double refLat, refLon;
                if (cov) { var b0 = frame.Box; refLat = (b0.LatMin + b0.LatMax) * 0.5; refLon = (b0.LonMin + b0.LonMax) * 0.5; }
                else if (!((aswOnly && Asw.RefPoint(out refLat, out refLon)) || (capOnly && Cap.RefPoint(out refLat, out refLon)) || RtOverlays.FirstRef(Extra, out refLat, out refLon))) return;
                MapViewSnap snap;
                if (!_overlay.TrySnap(map, refLat, refLon, out snap)) return;
                double mw = snap.MapW, mh = snap.MapH;
                bool exact = snap.Kind != MapProjKind.Affine;
                int rtw = _rt.width, rth = _rt.height;
                MapPx px = null; MapPxKey key = default(MapPxKey);
                bool moved;
                var pts = _pts;
                if (exact)
                {
                    _overlay.Snaps.Verify(map, snap, now);
                    var o = _o2o; var lo = MapLayerMath.LocalToOut(mw, mh, rtw, rth);
                    for (int i = 0; i < 6; i++) o[i] = lo[i];
                    px = _overlay.Snaps.Reflect ? MapPx.Reflect(snap, _overlay.Snaps.ReflectFn(map), o, rtw, rth) : MapPx.Create(snap, o, rtw, rth);
                    key = px.Key;
                    moved = !_haveKey || MapPx.KeyShiftPx(in _prevKey, in key) > 0.5;
                }
                else
                {
                    Affine2 geoLocal = snap.GeoToLocal;
                    Array.Clear(pts, 0, pts.Length);
                    if (cov)
                    {
                        var b = frame.Box;
                        double[] la = { b.LatMax, b.LatMax, b.LatMin, b.LatMin }, lo = { b.LonMin, b.LonMax, b.LonMax, b.LonMin };
                        for (int i = 0; i < 4; i++) geoLocal.Apply(la[i], lo[i], out pts[2 * i], out pts[2 * i + 1]);
                    }
                    else
                    {
                        double x, y; geoLocal.Apply(refLat, refLon, out x, out y);
                        pts[0] = x; pts[1] = y; pts[2] = geoLocal.A; pts[3] = geoLocal.B; pts[4] = geoLocal.D; pts[5] = geoLocal.E;
                    }
                    pts[8] = mw; pts[9] = mh;
                    moved = CoverageMath.ProjectionChanged(_prevPts, pts, 0.5);
                }
                int ver = _api.CoverageDirtyVersion;
                bool aswDue = (aswOnly || capOnly) && now >= _nextAsw;
                bool dirty = _forceDirty || (cov && ver != _drawnVersion) || moved || aswDue || (extraOn && now >= RtOverlays.MinDue(Extra)) || (_aswDrawn && !asw);
                if (!dirty) return;
                if (aswDue) _nextAsw = now + 1.0;
                if (px == null) px = MapPx.Create(snap, MapLayerMath.LocalToOut(mw, mh, rtw, rth), rtw, rth);   // flat map: the old affine composed with LocalToOut
                Redraw(cov ? frame : null, asw, px, mw, mh);
                _lastVerts = px.Verts; _lastCut = px.Cut;
                if (cov && px.Kind != MapProjKind.Affine) { _lastCovVerts = _overlay.LastCovVerts; _lastCovStats = _overlay.LastRaster; }
                else { _lastCovVerts = 0; _lastCovStats = default(RasterEmitStats); }
                if (_lastCovStats.Truncated && now - _lastTruncLog >= 60.0)
                {
                    _lastTruncLog = now;
                    _log("radar layer: coverage raster truncated quads=" + _lastCovStats.Quads + " share=" + _lastCovVerts + " coarsen=" + _lastCovStats.Coarsen);
                }
                _lastDrawFrame = Time.frameCount; _lastDrawTime = now; _draws++;
                if (_drawPath == "none") _drawPath = path;
                if (exact) { _prevKey = key; _haveKey = true; _prevPts = null; }
                else { _prevPts = pts; _pts = _ptsSpare; _ptsSpare = pts; _haveKey = false; }
                _drawnVersion = ver; _forceDirty = false; _drawnFull = true; _aswDrawn = asw;
            }
            catch (Exception e)
            {
                _gate.Fail(Time.unscaledTime);
                _forceDirty = true; _prevPts = null; _haveKey = false;
                _log("radar layer: draw failed (" + e.GetType().Name + ": " + e.Message + "), layer off for 5 s [" + _gate.Failures + "]");
            }
        }

        /// <summary>T-190 A6: the union of the visible legend boxes (screen px) is moved by CoverageMath.LegendAvoid away from the Overview window / time panel; both boxes move together.</summary>
        private void AvoidObstacles(ref LegendBox extraBox, bool extraLegend, ref LegendBox radarBox, bool radarLegend, double k0, double[] mapRect)
        {
            bool e = extraLegend && extraBox.Visible, r = radarLegend && radarBox.Visible;
            if (!e && !r) { _legendRect = null; ModUiRects.ClearPassive("map_legend"); return; }
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            if (e) { x0 = Math.Min(x0, extraBox.X); y0 = Math.Min(y0, extraBox.Y); x1 = Math.Max(x1, extraBox.X + extraBox.W); y1 = Math.Max(y1, extraBox.Y + extraBox.H); }
            if (r) { x0 = Math.Min(x0, radarBox.X); y0 = Math.Min(y0, radarBox.Y); x1 = Math.Max(x1, radarBox.X + radarBox.W); y1 = Math.Max(y1, radarBox.Y + radarBox.H); }
            var col = new[] { x0 / k0, y0 / k0, (x1 - x0) / k0, (y1 - y0) / k0 };
            string why = "none";
            double[] d = mapRect == null ? new[] { 0.0, 0.0 } : CoverageMath.LegendAvoid(col, _obstacles, mapRect[2], mapRect[3], PanelGap, out why);
            if (d[0] != 0 || d[1] != 0)
            {
                if (e) { extraBox.X += d[0] * k0; extraBox.Y += d[1] * k0; }
                if (r) { radarBox.X += d[0] * k0; radarBox.Y += d[1] * k0; }
                col[0] += d[0]; col[1] += d[1];
            }
            _legendRect = col;
            // T-240: the legend is a passive obstacle for the "Messages" stack (not a mod window: no mouse block, the radial menu still opens there)
            if (mapRect != null) ModUiRects.SetPassive("map_legend", mapRect[0] + col[0], mapRect[1] + col[1], col[2], col[3]);
            else ModUiRects.ClearPassive("map_legend");
            if (why != _legendWhy)
            {
                _legendWhy = why;
                _log("radar layer: legend avoid " + why + " dx=" + Math.Round(d[0]) + " dy=" + Math.Round(d[1]) + " obstacles=" + _obstacles.Count);
            }
        }

        private void Redraw(CoverageFrame frame, bool asw, MapPx px, double mapW, double mapH)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var prev = RenderTexture.active;
            bool pushed = false;
            try
            {
                RenderTexture.active = _rt;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                GL.PushMatrix(); pushed = true;
                int rw = _rt.width, rh = _rt.height;
                bool flip = FlipY();
                // the legend of the external layers (range rings) stands first under the time panel, the radar legend right under it
                List<LegendLine> extraLines = asw && _legendsOk ? ExtraLegendLines() : null;   // T-233: no legend at all on the small map
                LegendBox extraBox;
                bool extraLegend = LayoutLegend(extraLines, 0, out extraBox);
                double k0 = rw / Math.Max(1.0, _curRect != null ? _curRect[2] : rw);
                double stack = extraLegend && extraBox.Visible ? extraBox.H / k0 + 8 : 0;   // screen px taken by the ring legend
                _legendW = extraLegend ? extraBox.W / k0 : 0;
                List<LegendLine> lines = null;
                LegendBox radarBox = default(LegendBox);
                bool radarLegend = false;
                if (frame != null && _overlay.LegendVisible && _legendsOk)   // T-228, T-233
                {
                    lines = CoverageMath.LegendLines(_api.CoverageTiers(), GameApi.BoolOf2(_api.GetStatic(RadarSpecs.LowAltClutter)), _overlay == null ? "" : _overlay.HotkeyLabel, frame.AnyJamCut, frame.AnyJamPartial, frame.AnyJamApprox);
                    radarLegend = LayoutLegend(lines, stack, out radarBox);
                    if (radarLegend) _legendW = Math.Max(_legendW, radarBox.W / k0);
                }
                AvoidObstacles(ref extraBox, extraLegend, ref radarBox, radarLegend, k0, mapW > 0 && _curRect != null ? _curRect : null);
                if (flip) GL.LoadPixelMatrix(0, rw, 0, rh); else GL.LoadPixelMatrix(0, rw, rh, 0);
                // a flip, when asked for, is done by the matrix above (the whole picture); px maps geo -> RT pixels (MapPx, T-309)
                if (frame != null)
                {
                    _overlay.DrawGeo(frame, px, rw, rh, flip);
                    if (radarLegend) _legend.Draw(lines, radarBox, flip);
                    DrawJamTexts(frame, px, rw);
                }
                if (asw)
                {
                    if (Asw != null && Asw.HasContent()) Asw.DrawGeo(px);
                    if (Cap != null && Cap.HasContent()) Cap.DrawGeo(px);
                    for (int i = 0; i < Extra.Count; i++)
                    {
                        var o = Extra[i];
                        if (o == null) continue;
                        try
                        {
                            if (!o.HasContent()) continue;
                            DrawGuard.Stage = "rt/" + o.Id;
                            o.DrawGeo(px, rw, rh);
                        }
                        catch (Exception e)
                        {
                            if (Time.unscaledTime - _lastExtraWarn >= 60f)
                            {
                                _lastExtraWarn = Time.unscaledTime;
                                _log("radar layer: overlay " + o.Id + " failed: " + e.GetType().Name + ": " + e.Message);
                            }
                        }
                        finally { DrawGuard.Stage = ""; }
                    }
                    DrawExtraTexts(px, rw, flip);
                    if (extraLegend) DrawExtraLegend(extraLines, extraBox, flip);
                }
            }
            finally
            {
                if (pushed) GL.PopMatrix();
                RenderTexture.active = prev;
                _api.CovStats.AddRedraw(sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>T-171: texts of the external layers (range-ring labels) at their geo points, drawn with the legend font; off-screen ones are skipped.</summary>
        private void DrawExtraTexts(MapPx px, int rw, bool flip)
        {
            try
            {
                if (_legend == null || !_legend.Ready || _curRect == null) return;
                var pos = new List<float[]>(); var texts = new List<string>(); var cols = new List<uint>();
                for (int i = 0; i < Extra.Count; i++)
                {
                    var tx = Extra[i] as IRtTexts;
                    if (tx == null || !Extra[i].HasContent()) continue;
                    var got = tx.TextItems();
                    if (got == null) continue;
                    foreach (var t in got)
                    {
                        if (string.IsNullOrEmpty(t.Text)) continue;
                        double x, y;
                        if (!px.Pt(t.Lat, t.Lon, out x, out y)) continue;   // behind the horizon: no text
                        if (double.IsNaN(x) || double.IsNaN(y) || x < -50 || y < -50 || x > _rt.width + 50 || y > _rt.height + 50) continue;
                        pos.Add(new[] { (float)x + 4f, (float)y + t.DyPx }); texts.Add(t.Text); cols.Add(t.Argb);
                    }
                }
                if (texts.Count == 0) return;
                double k = rw / Math.Max(1.0, _curRect[2]);
                bool small = _curRect[2] < 900 || _curRect[3] < 500;
                _legend.DrawTexts(pos, texts, cols, (int)Math.Round((small ? 10 : 12) * k));
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastExtraWarn >= 60f)
                {
                    _lastExtraWarn = Time.unscaledTime;
                    _log("radar layer: overlay texts failed: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>T-182 (ADR-0081): one label per jammed unit ("jamming: R nm", "jamming: &lt;= R nm", "jamming: bearing N"), at most JamLabelsMax, the selected unit first, then the nearest to the map centre.
        /// The texts are formatted here (language changes show at once); off-screen marks are skipped. An error is logged once a minute and never stops the frame.</summary>
        private void DrawJamTexts(CoverageFrame frame, MapPx px, int rw)
        {
            try
            {
                if (frame.JamMarks == null || frame.JamMarks.Count == 0 || _legend == null || !_legend.Ready || _curRect == null || _rt == null) return;
                var xy = new List<double[]>(frame.JamMarks.Count);
                foreach (var m in frame.JamMarks)
                {
                    double x, y;
                    bool shown = px.Pt(m.Lat, m.Lon, out x, out y);
                    bool off = !shown || double.IsNaN(x) || double.IsNaN(y) || x < -50 || y < -50 || x > _rt.width + 50 || y > _rt.height + 50;
                    xy.Add(off ? null : new[] { x, y });
                }
                var pick = CoverageJamMath.PickLabels(frame.JamMarks, xy, _api.CiSelectedOwnUid(), _rt.width / 2.0, _rt.height / 2.0);
                if (pick.Count == 0) return;
                var pos = new List<float[]>(); var texts = new List<string>(); var cols = new List<uint>();
                var tc = _overlay == null ? null : _overlay.TierColors;
                Color c = tc != null && tc.Length > 2 ? tc[2] : Color.white;
                uint argb = 0xFF000000u | ((uint)Mathf.RoundToInt(c.r * 255f) << 16) | ((uint)Mathf.RoundToInt(c.g * 255f) << 8) | (uint)Mathf.RoundToInt(c.b * 255f);
                foreach (int i in pick)
                {
                    pos.Add(new[] { (float)xy[i][0] + 4f, (float)xy[i][1] });
                    texts.Add(CoverageJamMath.LabelText(frame.JamMarks[i]));
                    cols.Add(argb);
                }
                double k = rw / Math.Max(1.0, _curRect[2]);
                bool small = _curRect[2] < 900 || _curRect[3] < 500;
                _legend.DrawTexts(pos, texts, cols, (int)Math.Round((small ? 10 : 12) * k));
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastExtraWarn >= 60f)
                {
                    _lastExtraWarn = Time.unscaledTime;
                    _log("radar layer: jamming labels failed: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>The box of a legend in the top-right corner: stackPx = screen px of the legends above it, under the time panel (_panelOffset).
        /// false = nothing to lay out (no lines, the font is not ready, no map rectangle).</summary>
        private bool LayoutLegend(List<LegendLine> lines, double stackPx, out LegendBox box)
        {
            box = default(LegendBox);
            if (lines == null || lines.Count == 0 || _legend == null || !_legend.Ready || _curRect == null || _rt == null) return false;
            double k = _rt.width / Math.Max(1.0, _curRect[2]);   // texture pixels per screen pixel
            var first = CoverageMath.LegendLayout(_curRect[2], _curRect[3], lines.Count, 0, 1, stackPx, _panelOffset);
            double tw = _legend.MeasureWidth(lines, (int)Math.Round(first.FontPx));
            box = CoverageMath.LegendLayout(_curRect[2], _curRect[3], lines.Count, tw, k, stackPx, _panelOffset);
            return true;
        }

        /// <summary>T-171: the lines of the external layers' legends (range rings); null = none. Its own error is logged once a minute.</summary>
        private List<LegendLine> ExtraLegendLines()
        {
            try
            {
                var lines = new List<LegendLine>();
                for (int i = 0; i < Extra.Count; i++)
                {
                    var lg = Extra[i] as IRtLegend;
                    if (lg == null || !Extra[i].HasContent()) continue;
                    var got = lg.LegendLines();
                    if (got == null) continue;
                    foreach (var g in got)
                        lines.Add(new LegendLine { Text = g.Text, Bold = g.Bold, Argb = g.Argb, Dashed = g.Dashed, Swatch = g.Argb != 0 ? 0 : -1 });
                }
                return lines.Count == 0 ? null : lines;
            }
            catch (Exception e) { ExtraLegendFailed(e); return null; }
        }

        /// <summary>T-171: the legend of the external layers (range rings): one box in the top-right corner under the time panel, over the radar legend.</summary>
        private void DrawExtraLegend(List<LegendLine> lines, LegendBox box, bool flip)
        {
            try { _legend.Draw(lines, box, flip); }
            catch (Exception e) { ExtraLegendFailed(e); }
        }

        private void ExtraLegendFailed(Exception e)
        {
            if (Time.unscaledTime - _lastExtraWarn < 60f) return;
            _lastExtraWarn = Time.unscaledTime;
            _log("radar layer: overlay legend failed: " + e.GetType().Name + ": " + e.Message);
        }

        public void Dispose()
        {
            try
            {
                Detach();
                if (_image != null && _pSource != null) _pSource.SetValue(_image, null, null);
                if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
                foreach (var rt in _defer.Drain()) { try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { } }
                if (_legend != null) _legend.Dispose();
            }
            catch { }
            ModUiRects.ClearPassive("map_legend");   // T-240
            _inited = false;
        }
    }

    /// <summary>
    /// Customer 2026-10-22: the game's time-compression panel (date / time, x0, rewind, play/pause, fast forward) in the top-right corner, so the
    /// legends can stand right under it. In MainGameView.xaml a ContentControl (Content = MainGameViewModel.TimeCompression, the whole screen) shows the
    /// DataTemplate of TimeCompressionViewModel: a full-screen Canvas with the panel Grid at Canvas.Top=0 / Canvas.Right=0 (moved 20 px down while the
    /// map window is maximized). The panel is the first element (breadth-first from MainGameView.Instance, at most MaxNodes) whose DataContext is a
    /// TimeCompressionViewModel and whose screen rectangle is panel-sized (CoverageMath.PlausiblePanel); the full-screen hosts are skipped. The pick is
    /// cached and its rectangle re-read once a second; a miss searches again every 5 s. Any missing member or failure = no rectangle (the layer then uses
    /// CoverageMath.TimePanelFallback). Main thread only.
    /// </summary>
    internal sealed class TimePanelProbe
    {
        private const double Period = 1.0, MissPeriod = 5.0;
        private const int MaxNodes = 1500, MaxChildren = 256;
        private readonly GameApi _api;
        private readonly CoverageOverlay _overlay;
        private readonly Action<string> _log;
        private readonly object[] _a1 = new object[1], _a2 = new object[2];
        private object _panel;
        private double _next = -1;
        private bool _missLogged;
        private string _lastPickLine = "";

        public TimePanelProbe(GameApi api, CoverageOverlay overlay, Action<string> log) { _api = api; _overlay = overlay; _log = log ?? (m => { }); }

        /// <summary>The panel's rectangle in screen pixels (CoverageOverlay.TryElementPixels, the frame of the map rect), null when not found.</summary>
        public double[] Rect { get; private set; }

        /// <summary>Type name of the picked element (diagnostics in the log line).</summary>
        public string ElementType { get; private set; } = "none";

        internal static string RectText(double[] r)
        {
            if (r == null || r.Length < 4) return "[]";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "[" + r[0].ToString("0", ci) + "," + r[1].ToString("0", ci) + "," + r[2].ToString("0", ci) + "," + r[3].ToString("0", ci) + "]";
        }

        /// <summary>Re-reads the panel when its second is due; true when Rect was re-evaluated. mapRect = the map in screen pixels.</summary>
        public bool Pump(double[] mapRect, double now)
        {
            if (now < _next) return false;
            _next = now + Period;
            Rect = null;
            try
            {
                // T-190 A4: plausibility against the screen (a small map window made the real panel "too big" and picked a 20 px button)
                double[] screen = { 0, 0, Screen.width, Screen.height };
                double[] r = null;
                if (_panel != null && !(Visible(_panel) && RectOf(_panel, out r) && CoverageMath.PlausiblePanel(screen, r))) Drop();
                if (_panel == null)
                {
                    _panel = Find(screen, out r);
                    if (_panel == null)
                    {
                        _next = now + MissPeriod;
                        if (!_missLogged) { _missLogged = true; _log("radar layer: time panel not found (map=" + RectText(mapRect) + "), the legends use the fixed place under it"); }
                        return true;
                    }
                    _missLogged = false;
                    ElementType = _panel.GetType().Name;
                    string line = "radar layer: time panel element " + ElementType + " " + RectText(r) + " map=" + RectText(mapRect);
                    if (line != _lastPickLine) { _lastPickLine = line; _log(line); }
                }
                Rect = r;
            }
            catch { Drop(); Rect = null; }
            finally { _a1[0] = null; _a2[0] = null; _a2[1] = null; }
            return true;
        }

        private void Drop() { _panel = null; ElementType = "none"; }

        private bool RectOf(object el, out double[] r)
        {
            r = null;
            return _overlay != null && _overlay.TryElementPixels(el, out r, "time panel rect: ");
        }

        private bool Visible(object el) { return GameApi.BoolOf2(_api.Get(el, RadarSpecs.ElementIsVisible)); }

        private bool IsPanelNode(object node)
        {
            return node != null && _api.IsOfType(_api.Get(node, RadarSpecs.ElementDataContext), RadarSpecs.TimeCompressionVmType);
        }

        /// <summary>Breadth-first from MainGameView.Instance (at most MaxNodes): every visible element of a TimeCompressionViewModel with a panel-sized
        /// rectangle is a candidate, the largest one wins (CoverageMath.PickPanel: the panel Grid, not a button inside it).</summary>
        private object Find(double[] screen, out double[] rect)
        {
            rect = null;
            if (!_api.Present(RadarSpecs.TimeCompressionVmType)) return null;
            var count = _api.Member(RadarSpecs.TreeChildCount) as System.Reflection.MethodInfo;
            var child = _api.Member(RadarSpecs.TreeChild) as System.Reflection.MethodInfo;
            object root = _api.GetStatic(RadarSpecs.MainViewInstance);
            if (count == null || child == null || root == null) return null;
            var queue = new Queue<object>();
            var nodes = new List<object>(); var rects = new List<double[]>();
            queue.Enqueue(root);
            for (int seen = 0; queue.Count > 0 && seen < MaxNodes; seen++)
            {
                var o = queue.Dequeue();
                double[] r;
                if (IsPanelNode(o) && Visible(o) && RectOf(o, out r) && CoverageMath.PlausiblePanel(screen, r)) { nodes.Add(o); rects.Add(r); }
                _a1[0] = o; int n = Convert.ToInt32(count.Invoke(null, _a1));
                for (int i = 0; i < n && i < MaxChildren; i++)
                {
                    _a2[0] = o; _a2[1] = i; var c = child.Invoke(null, _a2);
                    if (c != null) queue.Enqueue(c);
                }
            }
            int pick = CoverageMath.PickPanel(rects, screen);
            if (pick < 0) return null;
            rect = rects[pick];
            return nodes[pick];
        }
    }
}
