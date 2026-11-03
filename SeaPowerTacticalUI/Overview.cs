using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>Config section [Overview] (T-070, ADR-0029).</summary>
    internal sealed class OverviewConfig
    {
        public bool Debug;   // T-092: log every contact row (side, posture, colour and their sources)
        public bool FitRows = true;   // T-080: window height follows the row count (Rect height = maximum)
        public bool Enabled = true, ShowOnStart, Collapsed, ShowWeapons = true, ClickSelectsInGame = true, CenterSecondMap = true;
        public string Hotkey = "LeftAlt+O", Rect = "0.70,0.08,0.28,0.40", Sort = "dist:asc", Columns = Ui.OverviewColumns.DefaultSpec, IconSource = "auto";
        public int MaxRows = 30;
        public float RefreshSeconds = 1f, ClosingDeadbandKn = 3, FastClosingKn = 300, Scale = 0;
        /// <summary>(rect, collapsed, sort, visible): Plugin writes the config entries. visible is null unless the visibility was changed by the player.</summary>
        /// rect is null when the window was not placed yet (keep the stored value).
        public Action<string, bool, string, string> Save;
        /// <summary>T-242: the Own tab. Tab = contacts | own, OwnColumns / OwnSort like Columns / Sort; SaveTab(tab, ownSort) writes the entries.</summary>
        public string Tab = "contacts", OwnColumns = Ui.OverviewOwnColumns.DefaultSpec, OwnSort = Ui.OverviewOwnColumns.DefaultSort;
        public Action<string, string> SaveTab;
    }

    /// <summary>
    /// The "Overview" window (T-070): hostile and unidentified contacts, EVE-Overview style. IMGUI window + GL icons drawn in OnGUI, input read
    /// from the legacy Input. Not part of the offline selfcheck (Unity types); all rules live in OverviewMath, NtdsIcons and GameApi (OverviewGame.cs).
    /// </summary>
    internal sealed partial class OverviewPanel
    {
        private const int WindowId = 0x56420069;
        private const double HeaderH = OverviewMath.HeaderH, ColHeadH = OverviewMath.ColHeadH, RowH = OverviewMath.RowH, MinH = OverviewMath.MinH, MinW = 320, Grab = 16;   // T-178 O2: the size corner is 16 px x s

        private sealed class IconChoice
        {
            public string Source;                 // map | resources | own
            public List<double[]> Paths;          // polylines in the 22.5 grid (map / own)
            public bool Dot;
            public Texture2D Tex;
            public string Pair;
        }

        private readonly GameApi _api;
        private readonly OverviewConfig _cfg;
        private readonly Action<string> _log;
        private readonly Ui.HotkeyBinding _binding;   // T-123 C2: one hotkey code for the whole mod
        private readonly Ui.IKeyInput _input;
        private readonly Ui.IHotkeyGame _keyGame;
        private string _lastBindingState = "unknown";
        private readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        internal int TexCount { get { return _tex.Count; } }
        internal int IconCount { get { return _icons.Count; } }
        private NtdsSet _set;
        private string[] _cols = { "icon", "type", "dist", "brg", "spd", "alt", "crs" };
        private string _sortCol = "dist";
        private readonly Dictionary<int, Ui.OvCiCells> _ci = new Dictionary<int, Ui.OvCiCells>();   // T-153: contact_info cells by row id
        private bool _hasExtra;                                                                    // T-153: any contact_info column shown
        private bool _hasHit;                                                                      // T-168: hits / dmg column shown
        private readonly Ui.OverviewExpand _expand = new Ui.OverviewExpand();                      // T-168: expanded rows (track ids, session only)
        private readonly Dictionary<int, Ui.HeCell> _hit = new Dictionary<int, Ui.HeCell>();       // T-168: hits / dmg cells by row id
        private readonly UiDropdownModel _pickDd = new UiDropdownModel();                      // T-178 O1: the open list of weapons (window-local rectangles)
        private int _pickRow = -1, _pickCur; private string[] _pickNames;                      // the row the list belongs to, its current choice and the names
        private string _tip; private float _viewTop; private bool _inViewNow;                   // T-178 O2: the tip of the hovered element, the top of the scroll view, the row is visible
        private int _hoverSinceId = -1; private float _hoverSince;
        private readonly Dictionary<int, bool> _canExpand = new Dictionary<int, bool>();           // T-168: the row has a "+" (identified foreign track)
        private readonly Dictionary<int, Ui.HeDetail> _detail = new Dictionary<int, Ui.HeDetail>(); // T-168: the block under an expanded row
        private bool _sortAsc = true;
        private bool _collapsed;
        private ClosingFilter _filter;
        private OverviewFrame _frame;
        private bool? _lastOn;
        private List<OvRow> _shown = new List<OvRow>();
        private float _nextBuild;
        private double _scale = 1;
        private Rect _rect;                       // window in IMGUI pixels (y from the top)
        private bool _rectInit, _rectDirty, _resizing, _blockOn, _errLogged, _drawn;
        private bool _drawErrLogged, _inScroll, _inGroup;   // T-183: DrawWindow guard
        private float _clipW, _clipH;   // T-183: the visible list (ScrollBars of the last frame)
        private Vector2 _resizeGrab;
        private Vector2 _scroll;
        private int _hoverId = -1, _hoverIndex = -1;
        private string _note;
        private float _noteUntil;
        private double _thickness = 1;
        private Ui.IconThicknessReader _thick;
        /// <summary>T-310: UI_Parameters.TryMapValue("ContactIconThickness", false, out v) through reflection; null = no value.</summary>
        private double? ReadIconThickness()
        {
            var a = new object[] { "ContactIconThickness", false, 0f };
            object ok = _api.CallStatic(OverviewSpecs.IconThickness, a);
            return ok is bool && (bool)ok ? GameApi.D(a[2]) : null;
        }
        private GUIStyle _label, _head, _win;
        private readonly Dictionary<string, IconChoice> _icons = new Dictionary<string, IconChoice>();
        private float _drawMsAcc;
        private int _drawN;
        private static string _lang { get { return UiText.Lang; } }   // T-103: one language for the whole mod, read every frame

        private sealed class GameKeys : Ui.IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public OverviewPanel(GameApi api, OverviewConfig cfg, Action<string> log, Ui.HotkeyBinding binding = null, Ui.IKeyInput input = null)
        {
            _api = api; _cfg = cfg; _log = log ?? (m => { });
            _binding = binding; _input = input; _keyGame = new GameKeys(api);
        }

        // ---------------------------------------------------------------- init

        public bool Init(out string error)
        {
            error = null;
            try
            {
                string e;
                // the hotkey is not checked here: the game's InputHandler does not exist yet (Awake); Update resolves it in the mission (T-080)
                var cols = Ui.OverviewColumns.ParseColumns(_cfg.Columns, out e);
                if (cols != null) _cols = cols; else _log("overview: " + e + ", default columns");
                _hasExtra = Array.Exists(_cols, Ui.OverviewColumns.IsExtra);
                _hasHit = Array.Exists(_cols, Ui.OverviewColumns.IsHit);
                string col; bool asc;
                if (Ui.OverviewColumns.ParseSort(_cfg.Sort, out col, out asc)) { _sortCol = col; _sortAsc = asc; } else _log("overview: bad Overview.Sort '" + _cfg.Sort + "', dist:asc");
                InitOwn();   // T-242
                _collapsed = _cfg.Collapsed;
                _api.Panel.Collapsed = _collapsed; _api.Panel.Sort = _sortCol + ":" + (_sortAsc ? "asc" : "desc");
                _api.OverviewCenterSecondMap = _cfg.CenterSecondMap;
                _api.OverviewDebug = _cfg.Debug;
                _filter = new ClosingFilter(3.0, _cfg.ClosingDeadbandKn, 1.0, _cfg.FastClosingKn);
                OverviewMath.Text = k => UiText.Has(UiText.DefaultSection, k) ? UiText.Get(k) : null;   // units, kind captions, summary
                _thick = new Ui.IconThicknessReader(ReadIconThickness);   // T-310: read in a mission, not here

                // vector icons of the game (read only)
                if (_cfg.IconSource != "own" && _cfg.IconSource != "resources")
                {
                    string err;
                    string file = System.IO.Path.Combine(Application.dataPath, "globalgamemanagers.assets");
                    _set = NtdsIcons.LoadFromFile(file, out err);
                    if (_set != null) _log("ntds: " + _set.Paths.Count + " geometries" + (_set.Errors.Count > 0 ? ", " + _set.Errors.Count + " unreadable (" + _set.Errors[0] + ")" : ""));
                    else _log("ntds unavailable: " + err);
                }
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>T-144 L2: Overview.Columns changed in the settings window. A bad spec keeps the current columns.</summary>
        public void ApplyColumns()
        {
            string e;
            var cols = Ui.OverviewColumns.ParseColumns(_cfg.Columns, out e);
            if (cols == null) { _log("overview: " + e + ", columns unchanged"); return; }
            _cols = cols;
            _hasExtra = Array.Exists(_cols, Ui.OverviewColumns.IsExtra);
            _hasHit = Array.Exists(_cols, Ui.OverviewColumns.IsHit);
        }

        /// <summary>T-168: a new mission (OverviewFeature, MissionLifecycle): the expanded rows are ids of the old one.</summary>
        public void ResetExpand() { _expand.Clear(); _detail.Clear(); _hit.Clear(); _canExpand.Clear(); ClosePick(); }

        /// <summary>T-168: the lines of the block under an expanded row (0 = not expanded or no block yet).</summary>
        private int ExtraLines(int id)
        {
            Ui.HeDetail d;
            if (!_expand.IsOpen(id)) return 0;
            return _detail.TryGetValue(id, out d) && d != null ? Math.Max(1, d.Lines) : 1;   // never an empty block: at least the reason line
        }

        private int ExtraTotal()
        {
            int n = 0;
            foreach (var r in _shown) n += ExtraLines(r.Id);
            return n;
        }

        /// <summary>T-144 L2: Debug / CenterSecondMap / closing thresholds changed (the filter restarts its smoothing).</summary>
        public void ApplyRuntime()
        {
            _api.OverviewCenterSecondMap = _cfg.CenterSecondMap;
            _api.OverviewDebug = _cfg.Debug;
            _filter = new ClosingFilter(3.0, _cfg.ClosingDeadbandKn, 1.0, _cfg.FastClosingKn);
        }

        public void Dispose()
        {
            try { _api.SetOverviewBlock(false); } catch { }
            _blockOn = false;
            ModUiRects.Clear("overview");
            ClearTextures();
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>T-240: light release under an open game popup: no drawing, no mouse block; the open pick and the expanded rows stay.</summary>
        private void YieldToPopup()
        {
            if (_blockOn) { try { _api.SetOverviewBlock(false); } catch { } _blockOn = false; }
            ModUiRects.Clear("overview");
            _api.Panel.Tip = null;
            _drawn = false; _hoverId = -1; _hoverIndex = -1; _ownHoverUid = -1; _resizing = false;
        }

        private void Release()
        {
            if (_blockOn) { try { _api.SetOverviewBlock(false); } catch { } _blockOn = false; }
            ModUiRects.Clear("overview");
            _api.Panel.Tip = null;
            _drawn = false; _hoverId = -1; _hoverIndex = -1; _ownHoverUid = -1; ClosePick();
        }

        private void Save(string visible)
        {
            if (_cfg.Save == null) return;
            string spec = Ui.OverviewExpand.RectSpec(_rectInit, _rect.x, _rect.y, _rect.width, _rect.height, Screen.width, Screen.height);   // T-183: null before InitRect
            if (spec != null) _api.Panel.Rect = spec;
            try { _cfg.Save(spec, _collapsed, _sortCol + ":" + (_sortAsc ? "asc" : "desc"), visible); } catch { }
        }

        private void InitRect()
        {
            double x, y, w, h;
            if (!OverviewMath.ParseRect(_cfg.Rect, out x, out y, out w, out h)) OverviewMath.ParseRect("0.70,0.08,0.28,0.40", out x, out y, out w, out h);
            double px = x * Screen.width, py = y * Screen.height, pw = w * Screen.width, ph = h * Screen.height;
            OverviewMath.ClampRect(ref px, ref py, ref pw, ref ph, MinW * _scale, MinH * _scale, Screen.width, Screen.height);
            _rect = new Rect((float)px, (float)py, (float)pw, (float)ph);
            _rectInit = true;
        }

        private double CurrentHeight()
        {
            if (_collapsed) return HeaderH * _scale;
            return _cfg.FitRows ? Math.Min(_rect.height, FitRowsHeight()) : _rect.height;
        }

        // T-191: the table width and the bar height of the last frame (the bar takes part of the list when the table is wider than the window)
        private float _contentW, _hbarH = 16f;

        /// <summary>T-205: what BeginScrollView takes from the view for a bar = the bar size plus the style margin on the list side (the margin was missed before: the last row sat under the bar).</summary>
        private static float BarThickness(float fixedSize, float margin)
        {
            return Ui.OverviewExpand.BarThickness(fixedSize, margin);
        }

        private double FitRowsHeight()
        {
            if (_tab == Ui.OverviewTabs.Own) return OwnFitHeight();   // T-242
            return Ui.OverviewExpand.FitWithBar(OverviewMath.FitHeight(_shown.Count + ExtraTotal(), _scale), _contentW, _rect.width, _hbarH, _scale);
        }

        private void ClearTextures()
        {
            foreach (var t in _tex.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _tex.Clear();
            _ownIconTex.Clear();   // T-242: the textures above are gone
        }

        /// <summary>Deferred hotkey check (T-080): the game's key bindings exist only once a mission is loaded (HotkeyBinding retries every 2 s while unknown).</summary>
        private void CheckHotkey()
        {
            if (_binding == null || _binding.State == "off") return;
            _binding.Resolve(Time.unscaledTime, _api.Frame.InMission, _keyGame);
            if (_binding.State == _lastBindingState) return;
            _lastBindingState = _binding.State;
            _api.Panel.HotkeyState = _binding.State;
            if (_binding.State == "free") { _api.Panel.Hotkey = _binding.Picked; _api.Capabilities.Add("overview_hotkey"); }
        }

        public void Update()
        {
            if (!_cfg.Enabled) return;
            try
            {
                double newScale = UiScale.Windows(RadialMath.Scale(_api.UiScale(), Screen.height, _cfg.Scale));   // T-178 O2: never below 0.85
                if (newScale != _scale) { _scale = newScale; ClearTextures(); }
                CheckHotkey();
                if (_binding != null && _input != null && _binding.Pressed(_input))
                {
                    _api.SetOverview(!_api.OverviewOn, "hotkey");
                    Save(_api.OverviewOn ? "true" : "false");
                    _lastOn = _api.OverviewOn;
                }
                // opened or closed elsewhere (map menu item, layer switch, bot): keep [Overview] Visible in step (T-104)
                if (_lastOn.HasValue && _lastOn.Value != _api.OverviewOn) Save(_api.OverviewOn ? "true" : "false");
                _lastOn = _api.OverviewOn;
                if (!_api.Frame.InMission) ResetExpand();
                if (_thick != null && _thick.Tick(_api.Frame.InMission, Time.realtimeSinceStartup)) { _thickness = _thick.Value; ClearTextures(); }   // T-310
                if (!_api.OverviewOn || !_api.Frame.InMission || UiWindowPrefs.Hidden || _api.OverviewUiHidden()) { Release(); return; }
                if (_rectInit && UiWindowPrefs.PopupCovers(_rect.x, _rect.y, _rect.width, CurrentHeight())) { YieldToPopup(); return; }   // T-240: under an open game menu
                if (!_rectInit) InitRect();
                // keep the window on the screen when the resolution or UI scale changes
                double cx = _rect.x, cy = _rect.y, cw = _rect.width, ch = _rect.height;
                OverviewMath.ClampRect(ref cx, ref cy, ref cw, ref ch, MinW * _scale, MinH * _scale, Screen.width, Screen.height);
                _rect = new Rect((float)cx, (float)cy, (float)cw, (float)ch);

                double mx = Input.mousePosition.x, my = Screen.height - Input.mousePosition.y;
                bool down = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
                if (_resizing)
                {
                    if (!Input.GetMouseButton(0)) { _resizing = false; _rectDirty = true; }
                    else
                    {
                        double nw = mx - _rect.x + _resizeGrab.x, nh = my - _rect.y + _resizeGrab.y;
                        double rx = _rect.x, ry = _rect.y;
                        OverviewMath.ClampRect(ref rx, ref ry, ref nw, ref nh, MinW * _scale, MinH * _scale, Screen.width - _rect.x, Screen.height - _rect.y);
                        _rect = new Rect(_rect.x, _rect.y, (float)nw, (float)nh);
                    }
                }
                if (_rectDirty && !down) { _rectDirty = false; Save(null); }

                bool inside = mx >= _rect.x && mx < _rect.x + _rect.width && my >= _rect.y && my < _rect.y + CurrentHeight();
                if (!inside) { _hoverId = -1; _hoverIndex = -1; _ownHoverUid = -1; }
                bool wantBlock = inside || _resizing;
                if (wantBlock && !_blockOn)
                {
                    if (!down) { _api.SetOverviewBlock(true); _blockOn = true; }   // entering with a button held (e.g. a map pan) does not block
                }
                else if (!wantBlock && _blockOn) { _api.SetOverviewBlock(false); _blockOn = false; }
                ModUiRects.Set("overview", _rect.x, _rect.y, _rect.width, CurrentHeight());
                _drawn = true;

                if (Time.unscaledTime >= _nextBuild)
                {
                    _nextBuild = Time.unscaledTime + Mathf.Max(0.2f, _cfg.RefreshSeconds);
                    if (_tab == Ui.OverviewTabs.Own) RebuildOwn(_ownHoverUid >= 0); else Rebuild(inside);   // T-242
                }
                _api.Panel.Collapsed = _collapsed;
                _api.Panel.Sort = _sortCol + ":" + (_sortAsc ? "asc" : "desc");
                if (_drawN > 0) { _api.Panel.DrawMs = _drawMsAcc / _drawN; _drawMsAcc = 0; _drawN = 0; }
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("overview update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void Rebuild(bool frozen)
        {
            object refUnit;
            var fr = _api.BuildOverview(_filter, _lang, _cfg.ShowWeapons, out refUnit);
            var fresh = fr.Rows;
            _ci.Clear();
            if (_hasExtra || Ui.OverviewColumns.IsExtra(_sortCol))
            {
                var ci = _api.ContactInfo;   // T-153: lazy, the contact_info module registers after the Overview
                foreach (var row in fresh)
                {
                    Ui.ContactInfoRow cr = null;
                    try { if (ci != null) cr = ci.Get(row.Id); } catch { cr = null; }
                    _ci[row.Id] = Ui.OverviewColumns.Cells(cr, _lang);
                }
            }
            // T-168: "+" of an identified foreign track, the cells of the columns hits / dmg (the service registers after the Overview: read lazily)
            var he = _api.HitEstimate;
            _hit.Clear(); _canExpand.Clear(); _detail.Clear();
            if (he != null)
            {
                he.NewPass();
                bool needCells = _hasHit || Ui.OverviewColumns.IsHit(_sortCol);
                foreach (var row in fresh)
                {
                    bool can = false;
                    try { can = he.Expandable(row.Id); } catch { can = false; }
                    _canExpand[row.Id] = can;
                    if (needCells) { try { _hit[row.Id] = he.Cell(row.Id); } catch { _hit[row.Id] = null; } }
                }
            }
            var live = new HashSet<int>();
            foreach (var row in fresh) { bool can; if (_canExpand.TryGetValue(row.Id, out can) && can) live.Add(row.Id); }
            _expand.Prune(live.Contains);
            Ui.OverviewColumns.Sort(fresh, _sortCol, _sortAsc, _ci, _hit);
            var rows = frozen ? OverviewMath.ApplyFrozen(fresh, _shown, _hoverId, _hoverIndex) : fresh;
            if (rows.Count > _cfg.MaxRows) rows = rows.GetRange(0, Math.Max(1, _cfg.MaxRows));
            if (he != null)
                foreach (var row in rows)
                    if (_expand.IsOpen(row.Id)) LoadDetail(he, row.Id);
            fr.Rows = rows;
            fr.Version = _frame == null ? 1 : _frame.Version + 1;
            _frame = fr; _shown = rows;
            _api.OverviewLastFrame = fr;
            if (_cfg.ClickSelectsInGame) _api.Panel.SelectedTrack = _api.SelectedTrackId();
            int nm = 0, nr = 0, no = 0;
            foreach (var row in rows)
            {
                if (row.Icon == null) continue;
                var ic = IconFor(row);
                if (ic.Source == "map") nm++; else if (ic.Source == "resources") nr++; else no++;
            }
            _api.Panel.IconSource = OverviewMath.IconSourceOf(nm, nr, no);
        }

        private void LoadDetail(Ui.IHitEstimateDetail he, int id)
        {
            try { _detail[id] = he.Detail(id); } catch { _detail[id] = null; }
        }

        // ---------------------------------------------------------------- icons

        private IconChoice IconFor(OvRow row) { return IconFor(row.Icon); }

        private IconChoice IconFor(string pair)
        {
            IconChoice c;
            if (_icons.TryGetValue(pair, out c)) return c;
            string type = null, posture = null;
            int sl = pair.IndexOf('/');
            if (sl >= 0) { type = pair.Substring(0, sl); posture = pair.Substring(sl + 1); }
            c = new IconChoice { Pair = pair };
            bool dot;
            string key = NtdsIcons.KeyFor(type, posture, out dot);
            string pref = _cfg.IconSource;
            var order = pref == "own" ? new[] { "own" } : pref == "resources" ? new[] { "resources", "own" } : pref == "map" ? new[] { "map", "resources", "own" } : new[] { "map", "resources", "own" };
            foreach (var src in order)
            {
                if (src == "map" && key != null && _set != null && _set.Paths.ContainsKey(key)) { c.Source = "map"; c.Paths = _set.Paths[key]; c.Dot = dot; break; }
                if (src == "resources")
                {
                    string rn = NtdsIcons.ResourceName(type, posture);
                    Texture2D tex = null;
                    if (rn != null) { try { tex = Resources.Load<Texture2D>("ui/minimap/minimap_icons/" + rn); } catch { tex = null; } }
                    if (tex != null) { c.Source = "resources"; c.Tex = tex; break; }
                }
                if (src == "own") { c.Source = "own"; c.Paths = NtdsIcons.OwnSymbol(type, posture); break; }
            }
            _icons[pair] = c;
            if (c.Source == "own" && !_api.Panel.IconsMissing.Contains(pair)) _api.Panel.IconsMissing.Add(pair);
            return c;
        }

        private Texture2D MaskTexture(string key, Func<byte[]> make, int px)
        {
            Texture2D t;
            if (_tex.TryGetValue(key, out t)) return t;
            var m = make();
            if (m != null)
            {
                t = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var cols = new Color32[px * px];
                for (int y = 0; y < px; y++)
                    for (int x = 0; x < px; x++)
                        cols[(px - 1 - y) * px + x] = new Color32(255, 255, 255, m[y * px + x]);   // texture rows go bottom-up
                t.SetPixels32(cols); t.Apply();
            }
            _tex[key] = t;
            return t;
        }

        private static Color ToColor(uint argb, bool stale)
        {
            if (stale) argb = OverviewMath.StaleTint(argb);   // T-092: side colour stays, only more transparent
            var c = new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);
            if (c.a <= 0.01f) c.a = 1f;
            return c;
        }

        private Color RowColour(OvRow r)
        {
            string posture = null; int sl = r.Icon == null ? -1 : r.Icon.IndexOf('/');
            if (sl >= 0) posture = r.Icon.Substring(sl + 1);
            uint argb = r.Colour != 0 ? r.Colour : NtdsIcons.PostureColour(_set, posture);
            return ToColor(argb, r.Stale);
        }

        // ---------------------------------------------------------------- GUI

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(2, 2, 0, 0) };
            _head = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _win = new GUIStyle(GUI.skin.box);
            var tex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.09f, 0.78f)); tex.Apply();
            _win.normal.background = tex; _win.onNormal.background = tex;
            _win.border = new RectOffset(0, 0, 0, 0);
        }

        private string ColHead(string c)
        {
            string t;
            switch (c)
            {
                case "icon": t = ""; break;
                case "type": t = UiText.Get("overview.col.type"); break;
                case "dist": t = UiText.Get("overview.col.dist"); break;
                case "brg": t = UiText.Get("overview.col.brg"); break;
                case "spd": t = UiText.Get("overview.col.spd"); break;
                case "alt": t = UiText.Get("overview.col.alt"); break;
                case "crs": t = UiText.Get("overview.col.crs"); break;
                case "id": t = "№"; break;
                case "sensor": t = UiText.Get("overview.col.sensor"); break;
                case "age": t = UiText.Get("overview.col.age"); break;
                case "vcl": t = UiText.Get("overview.col.vcl"); break;
                case "idn": t = UiText.Get("overview.identify.col"); break;
                default: t = Ui.OverviewColumns.HeadKey(c) != null ? UiText.Get(Ui.OverviewColumns.HeadKey(c)) : c; break;
            }
            if (c == _sortCol) t += _sortAsc ? " ▲" : " ▼";
            return t;
        }

        /// <summary>T-183: the tip of a column head: the full name of the column and the click action (the arrow of the current sort is not part of the name).</summary>
        private string ColTip(string c)
        {
            string full = UiText.Has(UiText.DefaultSection, "settings.overview.columns.opt." + c) ? UiText.Get("settings.overview.columns.opt." + c) : null;
            return Ui.OverviewExpand.ColTip(full, ColHead(c), UiText.Get("overview.tip.col_sort"));
        }

        private void SetTip(Rect r, Vector2 mouse, string text) { if (_tip == null && r.Contains(mouse)) _tip = text; }

        private void DrawTip(Event ev, float w, float h)
        {
            if (_tip == null || ev.type != EventType.Repaint) return;
            _label.alignment = TextAnchor.MiddleLeft;
            bool wrap = _label.wordWrap; _label.wordWrap = false;   // T-183: the tip may have two lines
            var tsz = _label.CalcSize(new GUIContent(_tip));
            float tw = Math.Min(w - 4, tsz.x + 10), th = Math.Max((float)(RowH * _scale) + 2, tsz.y + 4);
            float x = Math.Max(2, Math.Min(ev.mousePosition.x + 12, w - tw - 2)), y = ev.mousePosition.y + 20;
            if (y + th > h) y = Math.Max(0, ev.mousePosition.y - th - 4);
            var old = GUI.color;
            GUI.color = new Color(0.02f, 0.03f, 0.05f, 0.96f);
            GUI.DrawTexture(new Rect(x, y, tw, th), Texture2D.whiteTexture);
            GUI.color = old;
            _label.normal.textColor = new Color(0.95f, 0.95f, 0.8f);
            GUI.Label(new Rect(x + 3, y, tw - 4, th), _tip, _label);
            _label.wordWrap = wrap;
        }

        private double ColWidth(string c, double typeW)
        {
            switch (c)
            {
                case "icon": return 32 * _scale;   // T-168: room for the "+" / "-" of an expandable row
                case "type": return typeW;
                case "dist": return 72 * _scale;
                case "brg": return 46 * _scale;
                case "spd": return 52 * _scale;
                case "alt": return 70 * _scale;
                case "crs": return 46 * _scale;
                case "id": return 46 * _scale;
                case "sensor": return 52 * _scale;
                case "age": return 56 * _scale;
                case "vcl": return 56 * _scale;
                case "idn": return 58 * _scale;
            }
            double bw = Ui.OverviewColumns.BaseWidth(c);   // T-153
            if (!double.IsNaN(bw)) return bw * _scale;
            return 50 * _scale;
        }

        private string CellText(OvRow r, string c)
        {
            switch (c)
            {
                case "type": return r.Type;
                case "dist": return r.Dist;
                case "brg": return r.Brg;
                case "spd": return r.Spd;
                case "alt": return r.Alt;
                case "crs": return r.Crs;
                case "id": return r.Id.ToString();
                case "sensor": return r.Sensor;
                case "age": return r.Age;
                case "vcl": return r.Closing;
                case "idn": return r.Identify;
            }
            if (Ui.OverviewColumns.IsHit(c))   // T-168
            {
                Ui.HeCell hc;
                return Ui.OverviewColumns.HitCellText(_hit.TryGetValue(r.Id, out hc) ? hc : null, c);
            }
            if (Ui.OverviewColumns.IsExtra(c))   // T-153
            {
                Ui.OvCiCells cc;
                return Ui.OverviewColumns.CellText(_ci.TryGetValue(r.Id, out cc) ? cc : null, c);
            }
            return "";
        }

        public void OnGUI()
        {
            if (!_cfg.Enabled || !_drawn || !_rectInit) return;
            try
            {
                EnsureStyles();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool repaint = Event.current != null && Event.current.type == EventType.Repaint;
                                int fs = Math.Max(9, (int)Math.Round(12 * _scale));
                _label.fontSize = fs; _head.fontSize = fs;
                var before = _rect;
                float h = (float)CurrentHeight();
                int oldDepth = GUI.depth;
                GUI.depth = -1000;
                var r = GUI.Window(WindowId, new Rect(_rect.x, _rect.y, _rect.width, h), DrawWindow, GUIContent.none, _win);
                GUI.depth = oldDepth;
                _rect = new Rect(r.x, r.y, _rect.width, _rect.height);
                if (_rect.x != before.x || _rect.y != before.y) _rectDirty = true;
                if (repaint) { _drawMsAcc += (float)sw.Elapsed.TotalMilliseconds; _drawN++; }
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("overview OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-183: the window function; an exception of the body is journalled once (OnGUI cannot catch it: GUI.Window calls this later) and the open clips are closed.</summary>
        private void DrawWindow(int id)
        {
            _inScroll = false; _inGroup = false;   // stale after an ExitGUIException of the previous frame
            try { DrawWindowBody(id); }
            catch (ExitGUIException) { throw; }
            catch (Exception e)
            {
                if (_inGroup) { _inGroup = false; try { GUI.EndGroup(); } catch { } }
                if (_inScroll) { _inScroll = false; try { GUI.EndScrollView(); } catch { } }
                if (!_drawErrLogged)
                {
                    _drawErrLogged = true;
                    string line = Ui.OverviewExpand.ErrLine(e.GetType().Name, e.Message, e.StackTrace);
                    _api.Panel.DrawError = line;
                    _log("overview window draw failed (once): " + line);
                }
            }
        }

        private void DrawWindowBody(int id)
        {
            double s = _scale;
            float w = _rect.width;
            float hh = (float)(HeaderH * s);
            var ev = Event.current;
            if (ev.type == EventType.Repaint) _tip = null;
            if (_collapsed || (_pickRow >= 0 && !_expand.IsOpen(_pickRow))) ClosePick();
            HandlePickInput(ev);   // T-178 O1: the open weapon list takes its clicks first
            // title bar
            string title = UiText.Get("overview.title");
            if (_frame != null && !string.IsNullOrEmpty(_frame.RefName)) title += " · " + UiText.Fmt("overview.title.from", _frame.RefName);
            if (_collapsed && _tab == Ui.OverviewTabs.Own && _ownFrame != null) title = Ui.OverviewOwnMath.Summary(_ownFrame);
            else if (_collapsed && _frame != null) title = OverviewMath.Summary(_frame, _lang);
            GUI.Label(new Rect(4, 0, _collapsed ? w - hh * 2 : Math.Max(40, TabsLeft(w, hh) - 8), hh), title, _head);
            var collR = new Rect(w - hh * 1.5f, 2, hh * 1.4f, hh - 4);
            if (GUI.Button(collR, _collapsed ? "+" : "–"))
            {
                _collapsed = !_collapsed; _rectDirty = true; Save(null);
            }
            SetTip(collR, ev.mousePosition, UiText.Get(_collapsed ? "overview.tip.expand" : "overview.tip.collapse"));
            if (!string.IsNullOrEmpty(_note) && Time.unscaledTime < _noteUntil && !_collapsed)
                GUI.Label(new Rect(w * 0.3f, 0, Math.Max(40, TabsLeft(w, hh) - w * 0.3f - 4), hh), _note, _label);
            if (!_collapsed) DrawTabs(ev, w, hh);   // T-242: before the drag zone, which would take the click
            GUI.DragWindow(new Rect(0, 0, w - hh * 1.6f, hh));
            if (_collapsed) { if (ev.type == EventType.Repaint) _api.Panel.IconsDrawn = 0; return; }
            if (_tab == Ui.OverviewTabs.Own) { DrawOwnBody(ev, w, hh); return; }   // T-242

            float ch = (float)(ColHeadH * s), rh = (float)(RowH * s);
            // column widths (before the height: FitRows needs the table width, T-191): the type column takes what is left
            double fixedW = 0;
            foreach (var c in _cols) if (c != "type") fixedW += ColWidth(c, 0);
            double typeW = Math.Max(110 * s, w - fixedW - 18 * s);
            var colX = new double[_cols.Length]; var colW = new double[_cols.Length]; double xx = 0;
            for (int i = 0; i < _cols.Length; i++) { colX[i] = xx; colW[i] = ColWidth(_cols[i], typeW); xx += colW[i]; }
            float contentW = (float)xx;
            _contentW = contentW;
            _hbarH = BarThickness(GUI.skin.horizontalScrollbar.fixedHeight, GUI.skin.horizontalScrollbar.margin.top);
            float totalH = (float)CurrentHeight();
            // T-183: the size corner is checked before the scroll bars and the rows, which would take the click
            var grip = Ui.OverviewExpand.GripRect(w, totalH, (float)(Grab * s));
            if (ev.type == EventType.MouseDown && ev.button == 0 && Ui.OverviewExpand.InRect(grip, ev.mousePosition.x, ev.mousePosition.y) && !_pickDd.Contains(ev.mousePosition.x, ev.mousePosition.y))
            {
                _resizing = true;
                _resizeGrab = new Vector2(_rect.width - (ev.mousePosition.x), totalH - (ev.mousePosition.y));
                ev.Use();
            }
            int iconsDrawn = 0;
            // rows (scroll view)
            var view = new Rect(0, hh + ch, w, totalH - hh - ch);
            _viewTop = view.y;
            int n = _shown.Count;
            // T-168: rows of different height (an expanded row carries the block of the hit estimate), the tops come from the pure layout
            var ids = new List<int>(n);
            foreach (var r0 in _shown) ids.Add(r0.Id);
            float listH;
            var tops = Ui.OverviewExpand.RowTops(ids, ExtraLines, rh, rh, out listH);
            float vbarW = BarThickness(GUI.skin.verticalScrollbar.fixedWidth, GUI.skin.verticalScrollbar.margin.left);
            float hbarH = _hbarH;
            var sb = Ui.OverviewExpand.ScrollBars(contentW, listH, w, view.height, vbarW, hbarH);
            _clipW = sb[2]; _clipH = sb[3];
            var content = new Rect(0, 0, Math.Max(contentW, _clipW), listH);
            _scroll = GUI.BeginScrollView(view, _scroll, content);
            _inScroll = true;
            int newHoverId = -1, newHoverIdx = -1;
            for (int i = 0; i < n; i++)
            {
                float y = tops[i];
                float blockH = (i + 1 < n ? tops[i + 1] : listH) - y;
                if (y + blockH < _scroll.y || y > _scroll.y + view.height) continue;   // not visible
                var row = _shown[i];
                var rr = new Rect(0, y, content.width, rh);
                bool canExpand; _canExpand.TryGetValue(row.Id, out canExpand);
                bool isOpen = canExpand && _expand.IsOpen(row.Id);
                // T-183: window coordinates of the mouse; the bars, the grip and what is outside the list do not take the row
                bool inView = Ui.OverviewExpand.RowHitAllowed(ev.mousePosition.x - _scroll.x, ev.mousePosition.y - _scroll.y + view.y, view.y, _clipW, _clipH, grip);
                bool over = inView && rr.Contains(ev.mousePosition) && !OverPickList(ev.mousePosition);
                _inViewNow = inView;
                if (ev.type == EventType.Repaint && (row.Id == _api.Panel.SelectedTrack || over))
                {
                    var oldc = GUI.color;
                    GUI.color = row.Id == _api.Panel.SelectedTrack ? new Color(0.3f, 0.5f, 0.9f, 0.35f) : new Color(1, 1, 1, 0.12f);
                    GUI.DrawTexture(rr, Texture2D.whiteTexture);
                    GUI.color = oldc;
                }
                if (over) { newHoverId = row.Id; newHoverIdx = i; }
                var iconCell = new Rect(0, y, (float)ColWidth("icon", 0), rh);
                var centerR = CenterRect(colX, typeW, y, rh);
                if (ev.type == EventType.Repaint && over)
                {
                    // T-178 O2: the tips of the row (the row itself after a pause, so that it does not flash under a moving mouse)
                    if (_hoverSinceId != row.Id) { _hoverSinceId = row.Id; _hoverSince = Time.unscaledTime; }
                    if (canExpand && iconCell.Contains(ev.mousePosition)) _tip = UiText.Get(isOpen ? "overview.tip.collapse_row" : "overview.tip.expand_row");
                    else if (centerR.width > 0 && centerR.Contains(ev.mousePosition)) _tip = UiText.Get("overview.tip.center");
                    else if (Time.unscaledTime - _hoverSince > 0.7f) _tip = UiText.Get("overview.tip.row");
                }
                var colour = RowColour(row);
                if (ev.type == EventType.Repaint)
                {
                    for (int k = 0; k < _cols.Length; k++)
                    {
                        string c = _cols[k];
                        var cr = new Rect((float)colX[k], y, (float)ColWidth(c, typeW), rh);
                        if (c == "icon")
                        {
                            var ic = IconFor(row);
                            int px = Math.Max(4, (int)Math.Round(14 * s));
                            var ir = new Rect(cr.x + (float)(12 * s) + (cr.width - (float)(12 * s) - px) / 2f, cr.y + (rh - px) / 2f, px, px);
                            if (canExpand)   // T-168: "+" / "-" of an identified foreign track
                            {
                                _label.alignment = TextAnchor.MiddleCenter;
                                _label.normal.textColor = new Color(0.85f, 0.9f, 1f, row.Stale ? 0.6f : 1f);
                                GUI.Label(new Rect(cr.x, cr.y, (float)(12 * s), rh), isOpen ? "\u2212" : "+", _label);
                            }
                            Texture2D tex = ic.Source == "resources" ? ic.Tex : MaskTexture("i:" + ic.Pair + ":" + px, () => NtdsIcons.RasterMask(ic.Paths, ic.Dot, px, Math.Max(1.0, _thickness * 0.5 * px / 11.25)), px);
                            if (tex != null) { var old = GUI.color; GUI.color = colour; GUI.DrawTexture(ir, tex); GUI.color = old; iconsDrawn++; }
                            continue;
                        }
                        if (c == "type" && over && centerR.width > 0)   // T-178 O2: "⌖" shows the contact on the map
                        {
                            var oldb = GUI.color;
                            GUI.color = centerR.Contains(ev.mousePosition) ? new Color(0.3f, 0.5f, 0.9f, 0.85f) : new Color(0.05f, 0.08f, 0.12f, 0.85f);
                            GUI.DrawTexture(centerR, Texture2D.whiteTexture);
                            GUI.color = oldb;
                        }
                        string text = CellText(row, c);
                        _label.alignment = (c == "type" || c == "sensor" || c == "idn" || c == "dmg") ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                        {
                            var tc = row.Hostile ? new Color(1f, 0.85f, 0.85f) : Color.white;
                            if (c == "idn")
                            {
                                // T-111: answered = grey-green, refused/silent = amber, no link = grey
                                switch (row.SortIdentify)
                                {
                                    case 4: tc = new Color(0.65f, 0.85f, 0.7f); break;
                                    case 3: case 2: tc = new Color(1f, 0.75f, 0.3f); break;
                                    case 1: tc = new Color(0.7f, 0.7f, 0.7f); break;
                                }
                            }
                            if (Ui.OverviewColumns.IsHit(c))   // T-168: dmg takes the colour of its status
                            {
                                Ui.HeCell hc;
                                uint? hargb = Ui.OverviewColumns.CellArgb(c, _hit.TryGetValue(row.Id, out hc) ? hc : null);
                                if (hargb.HasValue) tc = ToColor(hargb.Value, false);
                            }
                            if (row.Stale) tc.a *= 0.6f;
                            _label.normal.textColor = tc;
                        }
                        var tr = c == "dist" ? new Rect(cr.x, cr.y, cr.width - (float)(14 * s), cr.height) : cr;
                        GUI.Label(tr, text, _label);
                        if (c == "type" && over && centerR.width > 0)
                        {
                            _label.alignment = TextAnchor.MiddleCenter; _label.normal.textColor = Color.white;
                            GUI.Label(centerR, "\u2316", _label);
                        }
                        if (c == "dist")
                        {
                            int asz = Math.Max(4, (int)Math.Round(8 * s));
                            int arrow = row.Arrow;
                            bool known = !double.IsNaN(row.SortClosing);
                            if (arrow == 0 && !known) arrow = 99;
                            var am = arrow == 99 ? null : MaskTexture("a:" + arrow + ":" + asz, () => NtdsIcons.ArrowMask(arrow, asz), asz);
                            if (am != null)
                            {
                                var old = GUI.color;
                                GUI.color = row.Stale ? new Color(0.6f, 0.6f, 0.6f) : arrow > 0 ? new Color(1f, 0.35f, 0.3f) : arrow < 0 ? new Color(0.45f, 0.85f, 0.5f) : new Color(0.7f, 0.7f, 0.7f);
                                GUI.DrawTexture(new Rect(cr.xMax - (float)(8 * s) - asz / 2f, cr.y + (rh - asz) / 2f, asz, asz), am);
                                GUI.color = old;
                            }
                        }
                    }
                }
                // T-168: the block of an expanded row
                if (isOpen) DrawBlock(row.Id, y, rh, content.width, ev, s);
                // mouse: the "+" / "-" opens or closes the block (the row is not selected); click selects, double click centres the map
                // T-178 O2: the whole 32 px x s cell opens or closes it
                if (canExpand && ev.type == EventType.MouseDown && ev.button == 0 && over && iconCell.Contains(ev.mousePosition))
                {
                    ToggleExpand(row.Id);
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && over && centerR.width > 0 && centerR.Contains(ev.mousePosition))
                {
                    DoCenter(row.Id);   // "⌖"
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && over)
                {
                    if (ev.clickCount >= 2) DoCenter(row.Id); else DoSelect(row.Id);
                    ev.Use();
                }
            }
            GUI.EndScrollView();
            _inScroll = false;
            if (ev.type == EventType.Repaint)
            {
                _api.Panel.IconsDrawn = iconsDrawn;
                _hoverId = newHoverId; _hoverIndex = newHoverIdx;
                if (newHoverId < 0) _hoverSinceId = -1;
            }
            // T-183: column headers travel with the data (a group of the visible width, columns shifted by the scroll); the click and the tip use the same shift
            if (ev.type == EventType.Repaint)
            {
                GUI.BeginGroup(new Rect(0, hh, _clipW, ch));
                _inGroup = true;
                for (int i = 0; i < _cols.Length; i++)
                {
                    if (_cols[i] == "icon") continue;
                    var hr = Ui.OverviewExpand.HeaderRect(colX[i], colW[i], _scroll.x, ch);
                    GUI.Label(new Rect(hr[0], hr[1], hr[2], hr[3]), ColHead(_cols[i]), _head);
                }
                GUI.EndGroup();
                _inGroup = false;
            }
            if (ev.mousePosition.y >= hh && ev.mousePosition.y < hh + ch)
            {
                int hi = Ui.OverviewExpand.HeaderAt(colX, colW, _scroll.x, _clipW, ev.mousePosition.x);
                if (hi >= 0 && _cols[hi] != "icon")
                {
                    SetTip(new Rect(0, hh, _clipW, ch), ev.mousePosition, ColTip(_cols[hi]));
                    if (ev.type == EventType.MouseDown && ev.button == 0)
                    {
                        if (_sortCol == _cols[hi]) _sortAsc = !_sortAsc; else { _sortCol = _cols[hi]; _sortAsc = !(_cols[hi] == "vcl"); }
                        _nextBuild = 0; _rectDirty = true; Save(null);
                        ev.Use();
                    }
                }
            }
            // resize handle (the click itself is taken at the top)
            var gr = new Rect(grip[0], grip[1], grip[2], grip[3]);
            if (ev.type == EventType.Repaint)
            {
                var old = GUI.color; GUI.color = new Color(1, 1, 1, 0.5f);
                GUI.DrawTexture(new Rect(gr.xMax - 4, gr.yMax - 4, 3, 3), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(gr.xMax - 8, gr.yMax - 4, 3, 3), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(gr.xMax - 4, gr.yMax - 8, 3, 3), Texture2D.whiteTexture);
                GUI.color = old;
            }
            SetTip(gr, ev.mousePosition, UiText.Get(Ui.OverviewExpand.ResizeTipKey(_cfg.FitRows, _rect.height, FitRowsHeight())));
            DrawPickList(ev, w);
            if (ev.type == EventType.Repaint) _api.Panel.Tip = _tip;
            DrawTip(ev, w, totalH);
        }

        /// <summary>T-178 O2: the "⌖" button at the right end of the type cell of the hovered row; empty (width 0) without a type column.</summary>
        private Rect CenterRect(double[] colX, double typeW, float y, float rh)
        {
            int ti = Array.IndexOf(_cols, "type");
            if (ti < 0) return new Rect(0, 0, 0, 0);
            float bw = (float)(18 * _scale);
            return new Rect((float)(colX[ti] + typeW) - bw - 2, y + 1, bw, rh - 2);
        }

        // ---------------------------------------------------------------- T-178 O1: the list of weapons

        private void ClosePick() { _pickDd.Close(); _pickRow = -1; _pickNames = null; UiEscGuard.Set("overview.list", false, Time.frameCount); }   // T-199: the Esc that closes the list is not the game's

        private bool OverPickList(Vector2 contentMouse)
        {
            return _pickDd.OpenId != null && _pickDd.Contains(contentMouse.x, contentMouse.y - _scroll.y + _viewTop);
        }

        /// <summary>The open list takes the click on its item (the choice), a click on the list itself is not passed on, a click elsewhere closes it, Esc closes it.</summary>
        private void HandlePickInput(Event ev)
        {
            if (_pickDd.OpenId == null) return;
            var m = ev.mousePosition;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) { ClosePick(); ev.Use(); return; }
            if (ev.type != EventType.MouseDown || ev.button != 0) return;
            int hit = _pickDd.HitItem(m.x, m.y);
            if (hit >= 0) { ChoosePick(hit); ev.Use(); return; }
            if (_pickDd.Contains(m.x, m.y)) { ev.Use(); return; }
            var a = _pickDd.Anchor;
            if (m.x >= a.X && m.x <= a.X + a.W && m.y >= a.Y && m.y <= a.Y + a.H) return;   // the button toggles it itself
            ClosePick();
        }

        private void ChoosePick(int index)
        {
            int row = _pickRow, cur = _pickCur, n = _pickNames == null ? 0 : _pickNames.Length;
            ClosePick();
            var he = _api.HitEstimate;
            if (he == null || n == 0) return;
            try
            {
                for (int i = Ui.OverviewExpand.StepsTo(cur, index, n); i > 0; i--) he.Cycle(row, 1);
                LoadDetail(he, row);
            }
            catch { }
        }

        private void OpenPick(int id, Ui.HeDetail d, UiRect anchorWin, float w, float totalH, float rh, float hh)
        {
            var he = _api.HitEstimate;
            if (he == null) return;
            string[] names;
            try
            {
                names = Ui.OverviewExpand.CollectPicks(d.Choices, d.PickIndex, () => { var dd = he.Detail(id); return dd == null ? "" : dd.Pick; }, dir => he.Cycle(id, dir));
                LoadDetail(he, id);
            }
            catch { return; }
            _pickRow = id; _pickCur = d.PickIndex; _pickNames = names;
            int maxRows = Math.Max(3, (int)((totalH - hh) / rh));
            int cols = Ui.OverviewExpand.PickColumns(names.Length, maxRows);
            float itemW = anchorWin.W;
            foreach (string t in names) itemW = Math.Max(itemW, _label.CalcSize(new GUIContent(t)).x + (float)(14 * _scale));
            itemW = Math.Min(itemW, Math.Max(anchorWin.W, (w - 8) / cols));
            _pickDd.Open(id.ToString(), anchorWin, names.Length, cols, itemW, rh, totalH, w);
            UiEscGuard.Set("overview.list", true, Time.frameCount);   // T-199
        }

        private void DrawPickList(Event ev, float w)
        {
            if (_pickDd.OpenId == null || _pickNames == null || ev.type != EventType.Repaint) return;
            _label.alignment = TextAnchor.MiddleLeft;
            var old = GUI.color;
            var lr = _pickDd.ListRect;
            GUI.color = new Color(0.04f, 0.07f, 0.12f, 0.98f);
            GUI.DrawTexture(new Rect(lr.X, lr.Y, lr.W, lr.H), Texture2D.whiteTexture);
            for (int i = 0; i < _pickNames.Length; i++)
            {
                var ir = _pickDd.ItemRect(i);
                var r = new Rect(ir.X, ir.Y, ir.W, ir.H);
                if (i == _pickCur) { GUI.color = new Color(0.3f, 0.5f, 0.9f, 0.45f); GUI.DrawTexture(r, Texture2D.whiteTexture); }
                else if (r.Contains(ev.mousePosition)) { GUI.color = new Color(1, 1, 1, 0.14f); GUI.DrawTexture(r, Texture2D.whiteTexture); }
                GUI.color = old;
                _label.normal.textColor = new Color(0.95f, 0.95f, 1f);
                GUI.Label(r, FitPick(_pickNames[i], r.width - _label.padding.horizontal), _label);
            }
            GUI.color = new Color(0.6f, 0.7f, 0.9f, 0.9f);
            var ur = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(lr.X, lr.Y, lr.W, 1), ur); GUI.DrawTexture(new Rect(lr.X, lr.Y + lr.H - 1, lr.W, 1), ur);
            GUI.DrawTexture(new Rect(lr.X, lr.Y, 1, lr.H), ur); GUI.DrawTexture(new Rect(lr.X + lr.W - 1, lr.Y, 1, lr.H), ur);
            GUI.color = old;
        }

        private void ToggleExpand(int id)
        {
            bool open = _expand.Toggle(id);
            var he = _api.HitEstimate;
            if (open && he != null) LoadDetail(he, id);
            else _detail.Remove(id);
            _nextBuild = 0;
        }

        private static readonly Color BlockText = new Color(0.82f, 0.86f, 0.92f);

        /// <summary>T-168: the lines under an expanded row: the weapon choice "◂ weapon · where ▸", the estimate, the hits (newest first), the total. Clicks on the arrows cycle the choice.</summary>
        private void DrawBlock(int id, float y, float rh, float width, Event ev, double s)
        {
            Ui.HeDetail d;
            if (!_detail.TryGetValue(id, out d) || d == null || d.Lines == 0)
            {
                if (ev.type == EventType.Repaint) DrawBlockLine(new Ui.HeBlockLine { Text = UiText.Get("he.no_class") }, (float)(ColWidth("icon", 0) + 6 * s), y + rh, width, rh);
                return;
            }
            float x0 = (float)(ColWidth("icon", 0) + 6 * s), ly = y + rh;
            bool repaint = ev.type == EventType.Repaint;
            _label.alignment = TextAnchor.MiddleLeft;
            if (d.Pick != null)
            {
                float aw = (float)(16 * s);
                float textW = (float)(180 * s);   // fixed: the button stays where it is whatever the name length (the name is clipped)
                var pl = Ui.OverviewExpand.PickLayout(x0, aw, textW);   // live finding 35: a fixed place, the same spot opens the list every time
                var btnR = new Rect(pl[0], ly, pl[3] + aw - pl[0], rh);
                var textR = new Rect(pl[1], ly, pl[2], rh);
                var arrowR = new Rect(pl[3], ly, aw, rh);
                bool many = d.Choices > 1;
                if (repaint)
                {
                    if (many && btnR.Contains(ev.mousePosition) && _inViewNow && !OverPickList(ev.mousePosition))
                    {
                        var oldc = GUI.color; GUI.color = new Color(1, 1, 1, 0.14f); GUI.DrawTexture(btnR, Texture2D.whiteTexture); GUI.color = oldc;
                        _tip = UiText.Get("overview.tip.pick");
                    }
                    _label.normal.textColor = new Color(0.95f, 0.95f, 1f);
                    if (many) { _label.alignment = TextAnchor.MiddleCenter; GUI.Label(arrowR, "\u25BE", _label); _label.alignment = TextAnchor.MiddleLeft; }   // i18n-allow: glyph
                    GUI.Label(textR, FitPick(d.Pick, textR.width - _label.padding.horizontal), _label);
                }
                else if (many && ev.type == EventType.MouseDown && ev.button == 0 && btnR.Contains(ev.mousePosition) && !OverPickList(ev.mousePosition))
                {
                    if (_pickRow == id) ClosePick();
                    else
                    {
                        // content-local -> window-local: the scroll view starts at _viewTop
                        var anchor = new UiRect(btnR.x, btnR.y - _scroll.y + _viewTop, btnR.width, btnR.height);
                        OpenPick(id, d, anchor, _rect.width, (float)CurrentHeight(), rh, (float)(HeaderH * s) + (float)(ColHeadH * s));
                    }
                    ev.Use();
                }
                ly += rh;
            }
            if (!repaint) return;
            foreach (var l in d.Estimate) { DrawBlockLine(l, x0, ly, width - x0, rh); ly += rh; }
            foreach (var l in d.Hits) { DrawBlockLine(l, x0, ly, width - x0, rh); ly += rh; }
            if (d.Total != null) DrawBlockLine(d.Total, x0, ly, width - x0, rh);
        }

        private readonly Dictionary<string, string> _pickFit = new Dictionary<string, string>();

        /// <summary>The weapon name cut with "…" to the fixed name field (cached: CalcSize only when the name, width or font changes).</summary>
        private string FitPick(string pick, float maxW)
        {
            if (string.IsNullOrEmpty(pick)) return pick;
            string key = _label.fontSize + "|" + (int)maxW + "|" + pick;
            string v;
            if (_pickFit.TryGetValue(key, out v)) return v;
            if (_pickFit.Count > 64) _pickFit.Clear();
            v = Ui.OverviewExpand.Ellipsize(pick, maxW, t => _label.CalcSize(new GUIContent(t)).x - _label.padding.horizontal);
            _pickFit[key] = v;
            return v;
        }

        private void DrawBlockLine(Ui.HeBlockLine l, float x, float y, float w, float rh)
        {
            _label.alignment = TextAnchor.MiddleLeft;
            _label.normal.textColor = l.Argb != 0 ? ToColor(l.Argb, false) : BlockText;
            GUI.Label(new Rect(x, y, w, rh), l.Text, _label);
        }

        private void DoSelect(int id)
        {
            string err;
            var r = _api.SelectContact(id, _cfg.ClickSelectsInGame, "overview", out err);
            if (r == null) Note(err);
        }

        private void DoCenter(int id)
        {
            string err;
            var r = _api.CenterMap(id, null, null, null, "overview", out err);
            if (r == null) { Note(err); return; }
            if (r.Contains("\"map_open\":false")) Note(UiText.Get("overview.note.map_closed"));
        }

        private void Note(string text) { _note = text; _noteUntil = Time.unscaledTime + 1.5f; }
    }
}
