// T-148 U5: the window frame shared by the mod windows (ADR-0049): GUI.Window, drag by the title, [-] collapse to one line, [x] close,
// width by the right edge, the height follows the content (UiKit.End of the previous pass), a scrolling body above 85 % of the screen,
// a hint line at the bottom, the mouse block and ModUiRects. Position, width and "collapsed" are remembered in [Ui] WindowState (UiWindowPrefs.State).
// Unity-only. Texts come from UiText (ui.kit.*); nothing player-visible is hardcoded here.
using System;
using System.Collections.Generic;
using UE = UnityEngine;

namespace VoiceBotBridge
{
    internal sealed class UiWindowFrame
    {
        private static int _slots;
        private readonly int _id;
        private readonly string _stateId;
        private string _title;
        private readonly float _defW, _minW, _maxW;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        private readonly int _slot;
        private UE.Rect _rect;
        private float _avoidFromX, _avoidFromY, _avoidToX, _avoidToY;   // T-233 (review): the place before / after the shift, to undo it on Release if the player did not move the window
        private bool _avoided;
        private bool _shownPrev;   // T-233: the window was drawn in the previous pass (AvoidOnShow runs on the first one only)
        private bool _placed, _collapsed, _blockOn, _errLogged, _resizing, _prevDown;
        private float _contentH = 200f, _resizeStartW, _resizeStartMx;
        private float _wantW, _wantAtResize;   // live finding 0.27.2: the body width the tables ask for (UiKit.WantWidth) and that wish when the user last resized
        private UE.Vector2 _scroll;
        private double _scale;
        private UiDensity _density = UiDensity.Compact;
        private UiTheme _theme;
        private UnityPainter _painter;
        private UiKit _kit;
        private UE.GUIStyle _win;
        private UE.Texture2D _bg;
        private string _note;
        private UiTone _noteTone;
        private double _noteUntil;
        private Action<UiKit> _body, _footer;
        private UiKit _footKit;
        private float _footH;   // T-190: height of the sticky footer of the last pass (0 = the window has none)
        private Func<string> _summary;
        private IList<string> _warnings;
        private readonly UE.GUI.WindowFunction _draw;

        public UiWindowFrame(int windowId, string stateId, string title, float defW, float minW, float maxW, Action<bool> block, Action<string> log)
        {
            _id = windowId; _stateId = stateId; _title = title; _defW = defW; _minW = minW; _maxW = maxW;
            _block = block ?? (b => { }); _log = log ?? (m => { });
            _slot = _slots++;
            _draw = DrawWindow;
        }

        /// <summary>The caption; a window sets it again when UiText.Revision changes (language switch).</summary>
        public string Title { get { return _title; } set { _title = value ?? ""; } }
        public bool Visible { get; set; }
        /// <summary>T-233: obstacles to move off when the window appears (null = no avoiding). Called once per appearance (Visible false -> true), the shift is not saved.</summary>
        public Func<IList<double[]>> AvoidOnShow { get; set; }
        /// <summary>"none" | "below" | "left" | "right" | "blocked" of the last appearance.</summary>
        public string LastAvoid { get; private set; } = "none";
        /// <summary>T-154 A1: [x] only raises this flag. The window function runs AFTER the owner's OnGUI returned, so the frame must not touch Visible: the owner
        /// would write its own state back in the next event. The owner takes the request at the start of Update / OnGUI and closes the window itself.</summary>
        public bool CloseRequested { get; private set; }
        public bool TakeCloseRequest() { bool r = CloseRequested; CloseRequested = false; return r; }
        public UE.Rect Rect { get { return _rect; } }
        public UiKit Kit { get { return _kit; } }
        /// <summary>T-178 Ka3: a drop-down list of the window is open (owners must not treat Esc as "close the window / leave the mode" then).</summary>
        public bool ListOpen { get { return _kit != null && _kit.OverlayRect.HasValue; } }

        /// <summary>T-178 Ka3: the mouse (screen coordinates, y down) is over the open list; the list is part of the window body (the window grows to its bottom edge).</summary>
        private bool OverlayHit(double mx, double my)
        {
            var ov = _kit == null ? (UiRect?)null : _kit.OverlayRect;
            if (!ov.HasValue || _theme == null) return false;
            double x = _rect.x + ov.Value.X, y = _rect.y + _theme.TitleH + ov.Value.Y - _scroll.y;
            return mx >= x && mx < x + ov.Value.W && my >= y && my < y + ov.Value.H;
        }

        public void Say(string text, UiTone tone)
        {
            _note = text; _noteTone = tone; _noteUntil = UE.Time.unscaledTime + 6.0;
        }

        private void EnsureTheme()
        {
            double s = UiScale.Windows(UiWindowPrefs.ScaleFor(UE.Screen.height));   // T-178 KR B2: the floor 0.85 also for width, clamp and auto-grow
            var dens = UiWindowPrefs.Density;
            if (_theme != null && Math.Abs(s - _scale) < 0.005 && dens == _density) return;
            if (_theme != null && _placed)
            {
                // the scale changed (resolution or [Ui] Scale): keep the relative width
                _rect.width = (float)(_rect.width / _scale * s);
            }
            _scale = s; _density = dens;
            _theme = UiTheme.For(s, dens);
            _painter = new UnityPainter(_theme);
            _kit = new UiKit(_painter, _theme, UiWindowPrefs.State, _stateId);
            _kit.Log = _log;
            _footKit = new UiKit(_painter, _theme, UiWindowPrefs.State, _stateId + ".foot");
            _footKit.Log = _log;
            _win = null;
        }

        private void EnsureStyle()
        {
            // Live bug 0.27.2: the 1x1 background was unloaded by Resources.UnloadUnusedAssets on a mission restart, the window went transparent.
            if (_win != null && _bg) return;
            var dark = new UE.Texture2D(1, 1) { hideFlags = UE.HideFlags.HideAndDontSave };
            _bg = dark;
            dark.SetPixel(0, 0, UnityPainter.Role(UiColorRole.WindowBg)); dark.Apply();
            _win = new UE.GUIStyle(UE.GUI.skin.window);
            _win.normal.background = dark; _win.onNormal.background = dark; _win.focused.background = dark; _win.onFocused.background = dark;
            _win.padding = new UE.RectOffset(0, 0, 0, 0);
        }

        private void PlaceOnce()
        {
            if (_placed) return;
            _placed = true;
            float sw = UE.Screen.width, sh = UE.Screen.height;
            float w = (float)(_defW * _scale);
            float x = sw * (0.04f + 0.06f * _slot), y = sh * (0.08f + 0.04f * _slot);
            UiWinPos p;
            var st = UiWindowPrefs.State;
            if (st != null && st.TryGet(_stateId, out p))
            {
                x = (float)(p.X * sw); y = (float)(p.Y * sh); _collapsed = p.Collapsed;
                if (p.W > 0) w = (float)(p.W * sw);   // W = 0: a v1 state without a width, the default one stays
            }
            w = UiFrameMath.ClampWidth(w, _minW, _maxW, _scale);
            _rect = new UE.Rect(x, y, w, 200f);
        }

        private void Persist()
        {
            var st = UiWindowPrefs.State;
            if (st == null || !_placed) return;
            st.Set(_stateId, new UiWinPos { X = _rect.x / UE.Screen.width, Y = _rect.y / UE.Screen.height, W = _rect.width / UE.Screen.width, Collapsed = _collapsed });
        }

        /// <summary>Per-frame: mouse block, ModUiRects, width by the right edge, saving after the mouse is released.</summary>
        public void Update()
        {
            try
            {
                if (!Visible || UiWindowPrefs.Hidden) { Release(); return; }
                if (UiWindowPrefs.PopupCovers(_rect.x, _rect.y, _rect.width, _rect.height)) { Yield(); return; }   // T-240: under an open game menu
                Yielding = false;
                SyncEsc();
                if (_theme == null) return;
                double mx = UE.Input.mousePosition.x, my = UE.Screen.height - UE.Input.mousePosition.y;
                bool inside = mx >= _rect.x && mx < _rect.x + _rect.width && my >= _rect.y && my < _rect.y + _rect.height;
                inside = inside || OverlayHit(mx, my);   // T-178 Ka3: the open drop-down list belongs to the mouse block
                bool down = UE.Input.GetMouseButton(0) || UE.Input.GetMouseButton(1) || UE.Input.GetMouseButton(2);
                float edge = Math.Max(6f, 6f * _theme.S);
                if (_resizing)
                {
                    if (UE.Input.GetMouseButton(0))
                        _rect.width = UiFrameMath.ClampWidth(_resizeStartW + (float)mx - _resizeStartMx, _minW, _maxW, _scale);
                    else { _resizing = false; _wantAtResize = _wantW; Persist(); }
                }
                else if (UiFrameMath.AutoGrow(_rect.width, _wantW, _wantAtResize, _minW, _maxW, _scale, out float grown))
                {
                    _rect.width = grown;
                    Persist();
                }
                else if (inside && UE.Input.GetMouseButtonDown(0) && mx >= _rect.x + _rect.width - edge && my > _rect.y + _theme.TitleH)
                {
                    _resizing = true; _resizeStartW = _rect.width; _resizeStartMx = (float)mx;
                }
                if (!down) _titleDrag = false;
                else if (!_prevDown && inside && my < _rect.y + _theme.TitleH) _titleDrag = true;
                if (_prevDown && !down) Persist();
                _prevDown = down;
                bool want = inside || _resizing;
                if (want && !_blockOn) { if (!down) { _block(true); _blockOn = true; } }
                else if (!want && _blockOn) { _block(false); _blockOn = false; }
                ModUiRects.Set(_stateId, _rect.x, _rect.y, _rect.width, _rect.height);
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log(_stateId + " window update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-190: footer (optional) is drawn by a second UiKit between the body and the hint line, outside the scroll view (a sticky footer, ADR-0077 section 2.5).</summary>
        public void OnGUI(Action<UiKit> body, Func<string> collapsedSummary, IList<string> warnings, Action<UiKit> footer = null)
        {
            if (!Visible || UiWindowPrefs.Hidden) return;
            if (UiWindowPrefs.PopupCovers(_rect.x, _rect.y, _rect.width, _rect.height)) return;   // T-240: not drawn under an open game menu (the rect of the last pass)
            try
            {
                EnsureTheme();
                EnsureStyle();
                PlaceOnce();
                _body = body; _summary = collapsedSummary; _warnings = warnings; _footer = footer;
                if (footer == null) _footH = 0f; else if (_footH <= 0f) _footH = 2 * _theme.RowH + _theme.Pad;
                float titleH = _theme.TitleH, hintH = _theme.HintH;
                float bodyH;
                if (_collapsed) bodyH = _theme.RowH;
                else { bool sc; bodyH = BodyH(out sc, titleH, hintH); }
                float h = titleH + bodyH + (_collapsed ? 0f : _footH + hintH);
                float x = _rect.x, y = _rect.y;
                UiFrameMath.ClampOnScreen(ref x, ref y, _rect.width, h, UE.Screen.width, UE.Screen.height);
                if (!_shownPrev && AvoidOnShow != null)
                {
                    string why;
                    float fx = x, fy = y;
                    bool moved = UiFrameMath.AvoidRect(ref x, ref y, _rect.width, h, AvoidOnShow(), UE.Screen.width, UE.Screen.height, (float)(8f * _scale), out why);
                    if (moved) { _avoided = true; _avoidFromX = fx; _avoidFromY = fy; _avoidToX = x; _avoidToY = y; }
                    LastAvoid = why;
                    if (why != "none") _log(_stateId + " placed " + why + " x=" + (int)x + " y=" + (int)y);
                }
                _shownPrev = true;
                _rect = new UE.Rect(x, y, _rect.width, h);
                FocusOnPress();
                int oldDepth = UE.GUI.depth;
                UE.GUI.depth = -1000;
                var r = UE.GUI.Window(_id, _rect, _draw, UE.GUIContent.none, _win);
                _rect = new UE.Rect(r.x, r.y, _rect.width, _rect.height);
                UE.GUI.depth = oldDepth;
                SyncEsc();
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log(_stateId + " window OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-148 live bug (0.19.1): the first click on an unfocused mod window was lost (the button pressed visibly but did nothing).
        /// On a mouse press inside the window: block the game mouse at once (Update runs once per frame, the press can come first) and focus the window in
        /// the same event, so the press reaches the control instead of being spent on the focus change.</summary>
        private void FocusOnPress()
        {
            var e = UE.Event.current;
            if (e == null || e.type != UE.EventType.MouseDown) return;
            if (!_rect.Contains(e.mousePosition)) return;
            if (!ModUiRects.IsTopAt(_stateId, e.mousePosition.x, e.mousePosition.y)) return;   // a window stacked above takes the press
            ModUiRects.Raise(_stateId);
            if (!_blockOn) { try { _block(true); _blockOn = true; } catch { } }
            UE.GUI.FocusWindow(_id);
            UE.GUI.BringWindowToFront(_id);
        }

        /// <summary>T-174 W2: a window that grows from its content keeps its y and scrolls (room below its top); while the title is dragged the old rule applies.</summary>
        private bool _titleDrag;

        private float BodyH(out bool scroll, float titleH, float hintH)
        {
            if (_titleDrag) return UiFrameMath.BodyHeight(_contentH, UE.Screen.height, out scroll, titleH + hintH + _footH);
            return UiFrameMath.BodyHeightAt(_contentH, _rect.y, UE.Screen.height, out scroll, titleH + hintH + _footH, UiFrameMath.MinBodyHeight(_contentH, UE.Screen.height));
        }

        private void DrawWindow(int id)
        {
            var t = _theme; var p = _painter;
            float w = _rect.width, titleH = t.TitleH, hintH = t.HintH;
            p.Fill(new UiRect(0, 0, w, titleH), UiColorRole.TitleBar);
            float bw = t.IconW, by = (titleH - t.IconW) / 2f;
            var rClose = new UiRect(w - t.Pad - bw, by, bw, t.IconW);
            var rMin = new UiRect(rClose.X - t.Gap - bw, by, bw, t.IconW);
            p.Label(new UiRect(t.Pad, 0, Math.Max(0f, rMin.X - t.Pad - t.Gap), titleH), _title, UiFont.Title, UiTone.Normal, null);
            if (p.Button(rMin, "frame.min", _collapsed ? "+" : "–", UiText.Get(_collapsed ? "ui.kit.restore" : "ui.kit.minimize"), true, false)) { _collapsed = !_collapsed; Persist(); }
            if (p.Button(rClose, "frame.close", "x", UiText.Get("ui.kit.close"), true, false)) { CloseRequested = true; Release(); return; }
            UE.GUI.DragWindow(new UE.Rect(0, 0, Math.Max(0f, rMin.X - t.Gap), titleH));

            if (_collapsed)
            {
                string sum = null;
                try { sum = _summary != null ? _summary() : null; } catch { }
                p.Label(new UiRect(t.Pad, titleH, w - 2 * t.Pad, t.RowH), p.Measure(sum, UiFont.Small) > w - 2 * t.Pad ? "" : sum, UiFont.Small, UiTone.Dim, sum);
                return;
            }

            bool scroll;
            float bodyH = BodyH(out scroll, titleH, hintH);
            var area = new UE.Rect(0, titleH, w, bodyH);
            float bodyW = w;
            if (scroll)
            {
                bodyW = w - t.ScrollW;
                _scroll = UE.GUI.BeginScrollView(area, _scroll, new UE.Rect(0, 0, bodyW, _contentH));
            }
            else UE.GUI.BeginGroup(area);
            _kit.ViewH = scroll ? _scroll.y + bodyH : 0f;   // T-178 Ka3: a scrolling window lets the list flip above the button, a growing one fits it
            _kit.Begin(bodyW);
            try { _body(_kit); }
            finally { _contentH = _kit.End(); _wantW = _kit.WantWidth > 0 ? _kit.WantWidth + (w - bodyW) : 0f; if (scroll) UE.GUI.EndScrollView(); else UE.GUI.EndGroup(); }

            float hy = titleH + bodyH;
            if (_footer != null)
            {
                var fa = new UE.Rect(0, hy, w, _footH);
                UE.GUI.BeginGroup(fa);
                _footKit.ViewH = 0f;
                _footKit.Begin(w);
                try { _footer(_footKit); }
                finally { _footH = _footKit.End(); UE.GUI.EndGroup(); }
                hy += _footH;
            }
            p.Fill(new UiRect(0, hy, w, hintH), UiColorRole.TitleBar);
            UiTone tone;
            string line = UiFrameMath.HintLine(UiFrameMath.HintTooltip(p.Tooltip, UE.Event.current != null && new UE.Rect(0, 0, w, _rect.height).Contains(UE.Event.current.mousePosition)), _note, _noteUntil, UE.Time.unscaledTime, _warnings, out tone, _noteTone);
            if (!string.IsNullOrEmpty(line))
            {
                string one = line.Replace('\n', ' ');
                float avail = w - 2 * t.Pad;
                if (p.Measure(one, UiFont.Small) > avail) one = Ellipsize(p, one, avail);
                p.Label(new UiRect(t.Pad, hy, avail, hintH), one, UiFont.Small, tone, null);
            }
            // the right edge is a grip for the width (the drag itself is handled in Update)
            p.Fill(new UiRect(w - Math.Max(2f, 2f * t.S), titleH, Math.Max(2f, 2f * t.S), bodyH), UiColorRole.Separator);
            p.Hit(new UiRect(w - Math.Max(6f, 6f * t.S), titleH, Math.Max(6f, 6f * t.S), bodyH), "frame.resize", UiText.Get("ui.kit.resize"));
        }

        private static string Ellipsize(UnityPainter p, string s, float maxW)
        {
            int lo = 0, hi = s.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (p.Measure(s.Substring(0, mid) + "…", UiFont.Small) <= maxW) lo = mid; else hi = mid - 1;
            }
            return s.Substring(0, lo) + "…";
        }

        /// <summary>T-199: an open drop-down list owns the Esc key (the game's pause menu must not open with it).</summary>
        private void SyncEsc() { UiEscGuard.Set(_stateId + ".list", ListOpen, UE.Time.frameCount); }

        /// <summary>T-240: true while the window stays hidden under an open game popup (diagnostics).</summary>
        public bool Yielding { get; private set; }

        /// <summary>T-240: lifts the mouse block and the ModUiRects entry only; the T-233 shift state stays, so nothing is undone or recomputed when the menu closes.</summary>
        private void Yield()
        {
            Yielding = true;
            UiEscGuard.Set(_stateId + ".list", false, UE.Time.frameCount);
            if (_blockOn) { try { _block(false); } catch { } _blockOn = false; }
            ModUiRects.Clear(_stateId);
            _resizing = false; _titleDrag = false;
        }

        public void Release()
        {
            Yielding = false;
            UiEscGuard.Set(_stateId + ".list", false, UE.Time.frameCount);
            if (_blockOn) { try { _block(false); } catch { } _blockOn = false; }
            ModUiRects.Clear(_stateId);
            _resizing = false;
            // T-233 (review): the shift is for this appearance only; if the window still stands where it was put, it goes back to the saved place (no drift down after the Overview is closed)
            if (_avoided && Math.Abs(_rect.x - _avoidToX) < 0.5f && Math.Abs(_rect.y - _avoidToY) < 0.5f) _rect = new UE.Rect(_avoidFromX, _avoidFromY, _rect.width, _rect.height);
            _avoided = false;
            _shownPrev = false;
        }
    }
}
