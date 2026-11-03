using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-136 / ADR-0045 3: the contact information labels. Noesis path: a Canvas with a pool of TextBlocks inserted into the same
    /// MapControl.MapBase.Children as the radar layer (so game windows stay over it); IMGUI path: GUI.Label over everything when the
    /// Canvas cannot be attached 3 times in a row. The map projection never depends on the radar overlay being enabled: when the radar overlay does
    /// not exist, a projection-only CoverageOverlay (no shader, no drawing) is used. Noesis types are reached by reflection.
    /// Not part of the offline selfcheck (Unity). Never throws.
    /// </summary>
    internal sealed class ContactLabelLayer : IContactLabels, IDisposable
    {
        private readonly GameApi _api;
        private readonly Func<IRadarView> _radar;
        private readonly int _max;
        private readonly Action<string> _log;
        private CoverageOverlay _own;                       // projection-only helper, created when the radar overlay is missing

        private Type _tCanvas, _tText, _tBrush, _tColor, _tPanel, _tCollection;
        private PropertyInfo _pChildren, _pCount, _pVisibility, _pHit, _pWidth, _pHeight, _pHAlign, _pVAlign;
        private PropertyInfo _pText, _pFg, _pBg, _pFontSize;
        private MethodInfo _mSetLeft, _mSetTop, _mSetZ, _mGetZ, _mInsert, _mRemove, _mIndexOf, _mItem, _mAdd, _mFromArgb;
        private ConstructorInfo _cBrush;
        private object _canvas, _parentMap, _visVisible, _visCollapsed;
        internal int PoolCount { get { return _pool.Count; } }
        internal int BrushCount { get { return _brushes.Count; } }
        private readonly List<object> _pool = new List<object>();
        private readonly List<string> _poolText = new List<string>();
        private readonly List<uint> _poolColor = new List<uint>();
        private readonly Dictionary<uint, object> _brushes = new Dictionary<uint, object>();
        private object _bgBrush;

        private IList<LabelItem> _items = new List<LabelItem>();
        private int _version = -1;
        private bool _dirty, _wanted, _inited, _visShown, _attached, _initTried;
        private double _refLat, _refLon;
        private readonly double[] _lastAff = new double[6], _curAff = new double[6];
        private bool _haveAff;
        private MapPxKey _lastKey; private bool _haveKey;   // T-309: the key of the last layout on an exact (globe) map
        private int _failStreak;
        private string _mode = "off";
        private float _lastWarn = -1e9f;
        private GUIStyle _style;
        private const double FontSize = 13, IconGap = 14;   // IconGap: map units between a LeftOfIcon label and the icon centre

        public ContactLabelLayer(GameApi api, Func<IRadarView> radar, int maxLabels, Action<string> log)
        {
            _api = api; _radar = radar; _max = Math.Max(1, maxLabels); _log = log ?? (m => { });
        }

        public bool Ready { get { return _mode != "off"; } }
        public string Mode { get { return _mode; } }
        public int FailStreak { get { return _failStreak; } }

        private CoverageOverlay Projection()
        {
            IRadarView rv = null;
            try { rv = _radar == null ? null : _radar(); } catch (Exception) { }
            if (rv != null && rv.Overlay != null) return rv.Overlay;
            if (_own == null)
            {
                _own = new CoverageOverlay(_api, new CoverageConfig(), m => _log(m));
                _log("contact_info: map labels use their own map projection (radar overlay not available)");
            }
            return _own;
        }

        private void Warn(string m)
        {
            if (Time.unscaledTime - _lastWarn < 60f) return;
            _lastWarn = Time.unscaledTime;
            _log("contact_info: " + m);
        }

        /// <summary>Resolves Canvas, TextBlock, SolidColorBrush, Color and the panel members; creates the (hidden) canvas. false: IMGUI mode.</summary>
        public bool TryInit(out string error)
        {
            error = null;
            try
            {
                _tCanvas = _api.TypeOf(ContactInfoSpecs.CanvasType); _tText = _api.TypeOf(ContactInfoSpecs.TextType); _tBrush = _api.TypeOf(ContactInfoSpecs.BrushType);
                _tColor = _api.TypeOf(ContactInfoSpecs.ColorType); _tPanel = _api.TypeOf(ContactInfoSpecs.PanelType); _tCollection = _api.TypeOf(ContactInfoSpecs.CollectionType);
                if (_tCanvas == null || _tText == null || _tBrush == null || _tColor == null || _tPanel == null || _tCollection == null) { error = "Noesis types not found"; return false; }
                _pChildren = Member(ContactInfoSpecs.PanelChildren) as PropertyInfo; _pCount = Member(ContactInfoSpecs.CollectionCount) as PropertyInfo;
                _pVisibility = Member(ContactInfoSpecs.ElementVisibility) as PropertyInfo; _pHit = Member(ContactInfoSpecs.ElementHitTest) as PropertyInfo;
                _pWidth = Member(ContactInfoSpecs.ElementWidth) as PropertyInfo; _pHeight = Member(ContactInfoSpecs.ElementHeight) as PropertyInfo;
                _pHAlign = Member(ContactInfoSpecs.ElementHAlign) as PropertyInfo; _pVAlign = Member(ContactInfoSpecs.ElementVAlign) as PropertyInfo;
                _pText = Member(ContactInfoSpecs.TextText) as PropertyInfo; _pFg = Member(ContactInfoSpecs.TextForeground) as PropertyInfo;
                _pBg = Member(ContactInfoSpecs.TextBackground) as PropertyInfo; _pFontSize = Member(ContactInfoSpecs.TextFontSize) as PropertyInfo;
                _mSetLeft = Member(ContactInfoSpecs.CanvasSetLeft) as MethodInfo; _mSetTop = Member(ContactInfoSpecs.CanvasSetTop) as MethodInfo;
                _mSetZ = Member(ContactInfoSpecs.PanelSetZ) as MethodInfo; _mGetZ = Member(ContactInfoSpecs.PanelGetZ) as MethodInfo;
                _mInsert = Member(ContactInfoSpecs.ListInsert) as MethodInfo; _mRemove = Member(ContactInfoSpecs.ListRemove) as MethodInfo; _mIndexOf = Member(ContactInfoSpecs.ListIndexOf) as MethodInfo;
                _mItem = Member(ContactInfoSpecs.ListItem) as MethodInfo; _mAdd = Member(ContactInfoSpecs.ListAdd) as MethodInfo;
                if (_pChildren == null || _pCount == null || _pVisibility == null || _pHit == null || _pText == null || _pFg == null || _pWidth == null || _pHeight == null ||
                    _mSetLeft == null || _mSetTop == null || _mSetZ == null || _mGetZ == null || _mInsert == null || _mRemove == null || _mIndexOf == null || _mItem == null || _mAdd == null)
                { error = "Noesis members not found"; return false; }
                _mFromArgb = Member(ContactInfoSpecs.ColorFromArgb) as MethodInfo;
                if (_mFromArgb == null) { error = "Noesis.Color factory not found"; return false; }
                _cBrush = Member(ContactInfoSpecs.BrushCtor) as ConstructorInfo;
                if (_cBrush == null) { error = "SolidColorBrush(Color) constructor not found"; return false; }
                _visVisible = Enum.Parse(_pVisibility.PropertyType, "Visible");
                _visCollapsed = Enum.Parse(_pVisibility.PropertyType, "Collapsed");
                _canvas = _api.New(ContactInfoSpecs.CanvasCtor);
                _pHit.SetValue(_canvas, false, null);
                _pVisibility.SetValue(_canvas, _visCollapsed, null);
                SetEnum(_canvas, _pHAlign, "Left"); SetEnum(_canvas, _pVAlign, "Top");
                _bgBrush = Brush(0x66000000u);
                _inited = true; _mode = "noesis";
                return true;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>Noesis could not be set up: draw with IMGUI only.</summary>
        public void UseImgui(string why)
        {
            _mode = "imgui";
            _log("contact_info: map labels use imgui (" + why + ")");
        }

        private static void SetEnum(object target, PropertyInfo p, string name)
        {
            try { if (p != null && p.PropertyType.IsEnum) p.SetValue(target, Enum.Parse(p.PropertyType, name), null); } catch { }
        }

        private MemberInfo Member(MemberSpec s) { return _api.Member(s); }

        private static object Conv(object v, Type t) { return Convert.ChangeType(v, t, CultureInfo.InvariantCulture); }

        private object Brush(uint argb)
        {
            object b;
            if (_brushes.TryGetValue(argb, out b)) return b;
            byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), bl = (byte)argb;
            object color = _mFromArgb.Invoke(null, new object[] { a, r, g, bl });
            b = _cBrush.Invoke(new[] { color });
            _brushes[argb] = b;
            return b;
        }

        // ---------------------------------------------------------------- IContactLabels

        public void Show(IList<LabelItem> items, int version)
        {
            _items = items ?? new List<LabelItem>();
            _wanted = _items.Count > 0;
            if (version != _version) { _version = version; _dirty = true; }
            if (_items.Count > 0)
            {
                double la = 0, lo = 0;
                foreach (var i in _items) { la += i.Lat; lo += i.Lon; }
                _refLat = la / _items.Count; _refLon = lo / _items.Count;
            }
        }

        public void Hide()
        {
            _items = new List<LabelItem>(); _wanted = false; _version = -1;
            if (_inited && _attached) SetVisible(false);
        }

        // ---------------------------------------------------------------- Noesis path

        private object Children(object map) { return map == null ? null : _pChildren.GetValue(map, null); }

        private void SetVisible(bool on)
        {
            if (_canvas == null || _visShown == on) return;
            _pVisibility.SetValue(_canvas, on ? _visVisible : _visCollapsed, null);
            _visShown = on;
        }

        private void Detach()
        {
            try { var ch = Children(_parentMap); if (ch != null && _canvas != null) _mRemove.Invoke(ch, new[] { _canvas }); } catch { }
            _parentMap = null; _attached = false;
        }

        private bool StillInside()
        {
            try { var ch = Children(_parentMap); return ch != null && Convert.ToInt32(_mIndexOf.Invoke(ch, new[] { _canvas })) >= 0; }
            catch { return false; }
        }

        private bool Insert(object map)
        {
            try
            {
                var ch = Children(map);
                if (ch == null) return false;
                int n = Convert.ToInt32(_pCount.GetValue(ch, null));
                var names = new List<string>(n); var zs = new List<int>(n);
                for (int i = 0; i < n; i++)
                {
                    var c = _mItem.Invoke(ch, new object[] { i });
                    names.Add(c == null ? "null" : c.GetType().Name);
                    zs.Add(c == null ? 0 : Convert.ToInt32(_mGetZ.Invoke(null, new[] { c })));
                }
                int z;
                int at = CoverageMath.MapChildInsertIndex(names, zs, -1, out z);
                _mInsert.Invoke(ch, new[] { (object)at, _canvas });
                _mSetZ.Invoke(null, new[] { _canvas, (object)(z + 1) });
                _parentMap = map; _attached = true; _failStreak = 0; _dirty = true; _haveAff = false; _haveKey = false;
                _log("contact_info: label canvas attached to the map");
                return true;
            }
            catch (Exception e)
            {
                _attached = false; _parentMap = null; _failStreak++;
                Warn("canvas insert failed (" + (e.InnerException ?? e).Message + ")");
                return false;
            }
        }

        private object TextBlockAt(int i)
        {
            while (_pool.Count <= i)
            {
                var tb = _api.New(ContactInfoSpecs.TextCtor);
                _pHit.SetValue(tb, false, null);
                if (_pFontSize != null) { try { _pFontSize.SetValue(tb, Conv(FontSize, _pFontSize.PropertyType), null); } catch { } }
                if (_pBg != null && _bgBrush != null) { try { _pBg.SetValue(tb, _bgBrush, null); } catch { } }
                _pVisibility.SetValue(tb, _visCollapsed, null);
                _mAdd.Invoke(Children(_canvas), new[] { tb });
                _pool.Add(tb); _poolText.Add(null); _poolColor.Add(0);
            }
            return _pool[i];
        }

        /// <summary>Main thread, every frame. Cheap while nothing changed: the projection is fitted again, positions only when it or the snapshot changed.</summary>
        public void Update()
        {
            if (_mode == "imgui") return;
            if (!_inited)
            {
                // T-337 (N1): nobody ever called TryInit, so no map label was drawn; set up lazily on the first labels wanted in a mission (no UI work at startup, T-336)
                if (!_wanted || _initTried) return;
                _initTried = true;
                string ierr;
                if (!TryInit(out ierr)) { UseImgui(ierr ?? "init failed"); return; }
                _log("contact_info: map labels use the noesis canvas");
            }
            try
            {
                var proj = Projection();
                if (!_wanted) { if (_attached) SetVisible(false); return; }
                object map; double[] rect;
                if (!proj.TryMapPixels(out map, out rect)) { if (_attached) SetVisible(false); return; }
                if (_attached && (!ReferenceEquals(map, _parentMap) || !StillInside())) Detach();
                if (!_attached && !Insert(map))
                {
                    if (_failStreak >= 3) UseImgui("canvas lost " + _failStreak + " times in a row");
                    return;
                }
                MapViewSnap snap;
                if (!proj.TrySnap(map, _refLat, _refLon, out snap)) { SetVisible(false); return; }
                Affine2 aff = snap.GeoToLocal; double mw = snap.MapW, mh = snap.MapH;
                MapPx px = null;
                if (snap.Kind != MapProjKind.Affine && !proj.TryLocalPx(map, out px)) { SetVisible(false); return; }
                _pWidth.SetValue(_canvas, Conv(mw, _pWidth.PropertyType), null);
                _pHeight.SetValue(_canvas, Conv(mh, _pHeight.PropertyType), null);
                var cur = _curAff;   // reused buffers: no per-frame allocation
                bool moved; MapPxKey key = default(MapPxKey);
                if (px != null)
                {
                    key = px.Key;
                    moved = !_haveKey || MapPx.KeyShiftPx(in _lastKey, in key) > 0.5;   // exact map: labels follow the key, not the affine fit
                }
                else
                {
                    cur[0] = aff.A; cur[1] = aff.B; cur[2] = aff.C; cur[3] = aff.D; cur[4] = aff.E; cur[5] = aff.F;
                    moved = !_haveAff || CoverageMath.ProjectionChanged(_lastAff, cur, 0.5);
                }
                if (!_dirty && !moved) { SetVisible(true); return; }
                Layout(aff, px, mw, mh);
                if (px != null) { _lastKey = key; _haveKey = true; _haveAff = false; }
                else { Array.Copy(cur, _lastAff, 6); _haveAff = true; _haveKey = false; }
                _dirty = false;
                SetVisible(true);
            }
            catch (Exception e) { Warn("update: " + e.GetType().Name + ": " + e.Message); }
        }

        private void Layout(Affine2 aff, MapPx px, double mw, double mh)
        {
            int shown = 0;
            for (int k = 0; k < _items.Count && shown < _max; k++)
            {
                var it = _items[k];
                double x, y;
                if (px != null) { if (!px.Pt(it.Lat, it.Lon, out x, out y)) continue; }   // behind the horizon: no label
                else aff.Apply(it.Lat, it.Lon, out x, out y);
                if (x < -40 || y < -40 || x > mw + 40 || y > mh + 40) continue;
                var tb = TextBlockAt(shown);
                if (_poolText[shown] != it.Text) { _pText.SetValue(tb, it.Text, null); _poolText[shown] = it.Text; }
                if (_poolColor[shown] != it.Argb) { _pFg.SetValue(tb, Brush(it.Argb), null); _poolColor[shown] = it.Argb; }
                double left = it.LeftOfIcon ? x - IconGap - ContactInfoMath.EstWidth(it.Text, FontSize) : x + 8;   // own line: left of the icon, the game's name block is on the right
                _mSetLeft.Invoke(null, new[] { tb, Conv(left, _mSetLeft.GetParameters()[1].ParameterType) });
                _mSetTop.Invoke(null, new[] { tb, Conv(y - 8, _mSetTop.GetParameters()[1].ParameterType) });
                _pVisibility.SetValue(tb, _visVisible, null);
                shown++;
            }
            for (int i = shown; i < _pool.Count; i++) _pVisibility.SetValue(_pool[i], _visCollapsed, null);
        }

        // ---------------------------------------------------------------- IMGUI fallback

        /// <summary>Only used while Mode == "imgui": labels over everything (game windows are not cut out).</summary>
        public void OnGUI()
        {
            if (_mode != "imgui" || !_wanted) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                var proj = Projection();
                object map; Affine2 aff; double[] rect;
                if (!proj.TryFindMap(out map) || !proj.TryProjection(map, _refLat, _refLon, out aff, out rect)) return;
                if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false };
                var saved = GUI.color;
                int shown = 0;
                for (int k = 0; k < _items.Count && shown < _max; k++)
                {
                    var it = _items[k];
                    double x, y;
                    aff.Apply(it.Lat, it.Lon, out x, out y);
                    if (x < rect[0] || y < rect[1] || x > rect[0] + rect[2] || y > rect[1] + rect[3]) continue;
                    GUI.color = new Color(((it.Argb >> 16) & 255) / 255f, ((it.Argb >> 8) & 255) / 255f, (it.Argb & 255) / 255f, ((it.Argb >> 24) & 255) / 255f);
                    float left = it.LeftOfIcon ? (float)(x - IconGap - ContactInfoMath.EstWidth(it.Text, 12)) : (float)x + 8f;
                    var box = new Rect(left, (float)y - 8f, 260f, 20f + 15f * Lines(it.Text));
                    GUI.Label(box, it.Text, _style);
                    shown++;
                }
                GUI.color = saved;
            }
            catch (Exception e) { Warn("imgui: " + e.GetType().Name + ": " + e.Message); }
        }

        private static int Lines(string s)
        {
            int n = 0;
            if (s != null) foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        public void Dispose()
        {
            try { Detach(); } catch { }
            _pool.Clear(); _poolText.Clear(); _poolColor.Clear(); _brushes.Clear();
            _inited = false; _mode = "off";
        }
    }
}
