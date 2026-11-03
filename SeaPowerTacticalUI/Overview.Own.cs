using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-242 (ADR-0101): the "Own" tab of the Overview window. The rows are built once per RefreshSeconds (OverviewOwnGame.cs); OnGUI only draws ready texts.
    /// Hot path rules (lessons of T-239): equal row height with top = i * rh, no lists or LINQ in DrawOwnBody, the column layout and the "..." texts are cached,
    /// icon textures are looked up by the pair string without a new key, short clicks are caught by MouseDown in our own zone.
    /// </summary>
    internal sealed partial class OverviewPanel
    {
        private string _tab = Ui.OverviewTabs.Contacts;
        private string[] _ownCols = { "icon", "type", "name", "task", "alt", "spd", "load", "grp" };
        private string _ownSortCol = "grp"; private bool _ownSortAsc = true, _ownForceSort;
        private Ui.OwnFrame _ownFrame; private List<Ui.OwnRow> _ownShown = new List<Ui.OwnRow>();
        private int _ownHoverUid = -1, _selUid;
        private int _ownColsRev, _ownStamp;                                     // column spec revision, layout revision (invalidates the cell texts)
        private double[] _ownColX = new double[0], _ownColW = new double[0]; private double _ownContentW;
        private float _ownLayoutW = -1; private double _ownLayoutScale; private int _ownLayoutRev = -1;
        private string[] _ownFit = new string[0]; private int _ownFitStamp = -1, _ownFitFont = -1;   // row * columns -> text cut with "..." (null = not computed)
        private readonly Dictionary<string, Texture2D> _ownIconTex = new Dictionary<string, Texture2D>(); private int _ownIconPx = -1;
        private string[] _ownHeads = new string[0]; private int _ownHeadsRev = -1, _ownHeadsText = -1; private bool _ownHeadsAsc; private string _ownHeadsSort;
        private Func<string, float> _ownMeasure;
        private bool _ownColourSet; private Color _ownIconColour = Color.white;
        private static readonly Color OwnDim = new Color(0.62f, 0.66f, 0.72f), OwnMod = new Color(0.7f, 0.9f, 1f), OwnText = new Color(0.95f, 0.96f, 1f);

        // ---------------------------------------------------------------- init, tab

        private void InitOwn()
        {
            string e;
            var cols = Ui.OverviewOwnColumns.ParseColumns(_cfg.OwnColumns, out e);
            if (cols != null) _ownCols = cols; else _log("overview: own " + e + ", default columns");
            string col; bool asc;
            if (Ui.OverviewOwnColumns.ParseSort(_cfg.OwnSort, out col, out asc)) { _ownSortCol = col; _ownSortAsc = asc; } else _log("overview: bad Overview.OwnSort '" + _cfg.OwnSort + "', grp:asc");
            _tab = Ui.OverviewTabs.Parse(_cfg.Tab);
            if (_tab == Ui.OverviewTabs.Own && !OwnAvailable()) _tab = Ui.OverviewTabs.Contacts;
            _api.Panel.Tab = _tab;
            _ownMeasure = t => _label.CalcSize(new GUIContent(t)).x - _label.padding.horizontal;
        }

        /// <summary>Overview.OwnColumns changed in the settings window. A bad spec keeps the current columns.</summary>
        public void ApplyOwnColumns()
        {
            string e;
            var cols = Ui.OverviewOwnColumns.ParseColumns(_cfg.OwnColumns, out e);
            if (cols == null) { _log("overview: own " + e + ", columns unchanged"); return; }
            _ownCols = cols; _ownColsRev++;
        }

        private bool OwnAvailable() { return _api.Capabilities.Contains("overview_own"); }

        private string OwnSortSpec() { return _ownSortCol + ":" + (_ownSortAsc ? "asc" : "desc"); }

        private void SaveOwn()
        {
            if (_cfg.SaveTab == null) return;
            try { _cfg.SaveTab(_tab, OwnSortSpec()); } catch { }
        }

        private void SetTab(string tab)
        {
            tab = Ui.OverviewTabs.Parse(tab);
            if (tab == _tab) return;
            if (tab == Ui.OverviewTabs.Own && !OwnAvailable()) { Note(UiText.Get("overview.tab.own_off")); return; }
            _tab = tab; _nextBuild = 0; _scroll = Vector2.zero; _hoverSinceId = -1; _hoverId = -1; _hoverIndex = -1; _ownHoverUid = -1;
            ClosePick();
            _api.Panel.Tab = _tab;
            SaveOwn();
        }

        private int OwnRowCount() { return _ownShown.Count; }

        // ---------------------------------------------------------------- scan

        private const int OwnRowsLimit = 200;

        private void RebuildOwn(bool frozen)
        {
            var fr = _api.BuildOwnUnits(_lang);
            Ui.OverviewOwnColumns.Sort(fr.Rows, _ownSortCol, _ownSortAsc);
            var rows = frozen && !_ownForceSort ? Ui.OverviewOwnMath.ApplyFrozen(fr.Rows, _ownShown) : fr.Rows;
            _ownForceSort = false;
            if (rows.Count > OwnRowsLimit) rows = rows.GetRange(0, OwnRowsLimit);   // not MaxRows (30, the contacts cap): the list scrolls and draws only visible rows
            fr.Rows = rows;
            fr.Version = _ownFrame == null ? 1 : _ownFrame.Version + 1;
            _ownFrame = fr; _ownShown = rows;
            _ownStamp++;   // new texts: the cut texts are computed again for the visible rows
            _selUid = _api.OverviewSelectedOwnUid();
            _api.Panel.OwnRows = rows.Count; _api.Panel.OwnBuildMs = fr.BuildMs; _api.Panel.SelectedUid = _selUid; _api.Panel.Tab = _tab;
        }

        // ---------------------------------------------------------------- title bar tabs

        private float TabW(double s) { return (float)(74 * s); }

        /// <summary>Left edge of the tabs in the title bar: they sit left of the collapse button.</summary>
        private float TabsLeft(float w, float hh) { return w - hh * 1.6f - 2 * TabW(_scale) - 6; }

        private void DrawTabs(Event ev, float w, float hh)
        {
            float tw = TabW(_scale), x0 = TabsLeft(w, hh);
            bool repaint = ev.type == EventType.Repaint;
            for (int i = 0; i < 2; i++)
            {
                bool own = i == 1;
                var r = new Rect(x0 + i * (tw + 2), 3, tw, hh - 6);
                bool active = own == (_tab == Ui.OverviewTabs.Own), off = own && !OwnAvailable();
                bool hot = r.Contains(ev.mousePosition);
                if (repaint)
                {
                    var old = GUI.color;
                    GUI.color = active ? new Color(0.3f, 0.5f, 0.9f, 0.55f) : hot ? new Color(1, 1, 1, 0.16f) : new Color(1, 1, 1, 0.07f);
                    GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = old;
                    var st = active ? _head : _label;
                    st.alignment = TextAnchor.MiddleCenter;
                    st.normal.textColor = off ? OwnDim : OwnText;
                    GUI.Label(r, UiText.Get(own ? "overview.tab.own" : "overview.tab.contacts"), st);
                    if (hot) SetTip(r, ev.mousePosition, off ? UiText.Get("overview.tab.own_off") : null);
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && hot)
                {
                    SetTab(own ? Ui.OverviewTabs.Own : Ui.OverviewTabs.Contacts);
                    ev.Use();
                }
            }
        }

        // ---------------------------------------------------------------- layout

        private double OwnColWidth(string c, double flexW)
        {
            double bw = Ui.OverviewOwnColumns.BaseWidth(c);
            return double.IsNaN(bw) ? flexW : bw * _scale;
        }

        /// <summary>Column x / width for the window width. Cached by (width, scale, column revision); name and task share what the fixed columns leave, 1:2.</summary>
        private void EnsureOwnLayout(float w)
        {
            if (w == _ownLayoutW && _scale == _ownLayoutScale && _ownColsRev == _ownLayoutRev && _ownColX.Length == _ownCols.Length) return;
            int n = _ownCols.Length;
            _ownColX = new double[n]; _ownColW = new double[n];
            double fixedW = 0; bool hasName = false, hasTask = false;
            for (int i = 0; i < n; i++)
            {
                string c = _ownCols[i];
                if (c == "name") hasName = true; else if (c == "task") hasTask = true;
                else fixedW += OwnColWidth(c, 0);
            }
            double rest = Math.Max(0, w - fixedW - 18 * _scale);
            double nameW, taskW;
            if (hasName && hasTask) { nameW = Math.Max(100 * _scale, rest / 3); taskW = Math.Max(150 * _scale, rest - rest / 3); }
            else { nameW = Math.Max(110 * _scale, rest); taskW = nameW; }
            double x = 0;
            for (int i = 0; i < n; i++)
            {
                string c = _ownCols[i];
                _ownColX[i] = x;
                _ownColW[i] = c == "name" ? nameW : c == "task" ? taskW : OwnColWidth(c, 0);
                x += _ownColW[i];
            }
            _ownContentW = x;
            _ownLayoutW = w; _ownLayoutScale = _scale; _ownLayoutRev = _ownColsRev; _ownStamp++;
        }

        private double OwnFitHeight()
        {
            EnsureOwnLayout(_rect.width);
            return Ui.OverviewExpand.FitWithBar(OverviewMath.FitHeight(OwnRowCount(), _scale), _ownContentW, _rect.width, _hbarH, _scale);
        }

        private string OwnCell(Ui.OwnRow r, string c)
        {
            switch (c)
            {
                case "type": return r.Type;
                case "name": return r.Name;
                case "task": return r.Task;
                case "alt": return r.Alt;
                case "spd": return r.Spd;
                case "load": return r.Load;
                case "grp": return r.Grp;
            }
            return "";
        }

        private string OwnHead(int k)
        {
            string c = _ownCols[k];
            string key = Ui.OverviewOwnColumns.HeadKey(c);
            string t = key == null ? "" : UiText.Get(key);
            if (c == _ownSortCol) t += _ownSortAsc ? " ▲" : " ▼";
            return t;
        }

        private void EnsureOwnHeads()
        {
            if (_ownHeadsRev == _ownColsRev && _ownHeadsText == UiText.Revision && _ownHeadsSort == _ownSortCol && _ownHeadsAsc == _ownSortAsc && _ownHeads.Length == _ownCols.Length) return;
            _ownHeads = new string[_ownCols.Length];
            for (int k = 0; k < _ownCols.Length; k++) _ownHeads[k] = OwnHead(k);
            _ownHeadsRev = _ownColsRev; _ownHeadsText = UiText.Revision; _ownHeadsSort = _ownSortCol; _ownHeadsAsc = _ownSortAsc;
        }

        private string OwnColTip(int k)
        {
            string c = _ownCols[k];
            string full = UiText.Has(UiText.DefaultSection, "settings.overview.owncolumns.opt." + c) ? UiText.Get("settings.overview.owncolumns.opt." + c) : null;
            return Ui.OverviewExpand.ColTip(full, _ownHeads[k], UiText.Get("overview.tip.col_sort"));
        }

        private Texture2D OwnIconTexture(string pair, int px)
        {
            if (px != _ownIconPx) { _ownIconTex.Clear(); _ownIconPx = px; }
            Texture2D t;
            if (_ownIconTex.TryGetValue(pair, out t)) return t;
            var ic = IconFor(pair);
            t = ic.Source == "resources" ? ic.Tex : MaskTexture("i:" + ic.Pair + ":" + px, () => NtdsIcons.RasterMask(ic.Paths, ic.Dot, px, Math.Max(1.0, _thickness * 0.5 * px / 11.25)), px);
            _ownIconTex[pair] = t;
            return t;
        }

        // ---------------------------------------------------------------- body

        private void DrawOwnBody(Event ev, float w, float hh)
        {
            double s = _scale;
            float ch = (float)(ColHeadH * s), rh = (float)(RowH * s);
            EnsureOwnLayout(w);
            EnsureOwnHeads();
            _contentW = (float)_ownContentW;
            _hbarH = BarThickness(GUI.skin.horizontalScrollbar.fixedHeight, GUI.skin.horizontalScrollbar.margin.top);
            float totalH = (float)CurrentHeight();
            // the size corner is checked before the scroll bars and the rows, which would take the click (T-183)
            var grip = Ui.OverviewExpand.GripRect(w, totalH, (float)(Grab * s));
            if (ev.type == EventType.MouseDown && ev.button == 0 && Ui.OverviewExpand.InRect(grip, ev.mousePosition.x, ev.mousePosition.y))
            {
                _resizing = true;
                _resizeGrab = new Vector2(_rect.width - ev.mousePosition.x, totalH - ev.mousePosition.y);
                ev.Use();
            }
            var view = new Rect(0, hh + ch, w, totalH - hh - ch);
            _viewTop = view.y;
            int n = _ownShown.Count, nc = _ownCols.Length;
            float listH = n * rh;
            float vbarW = BarThickness(GUI.skin.verticalScrollbar.fixedWidth, GUI.skin.verticalScrollbar.margin.left);
            var sb = Ui.OverviewExpand.ScrollBars(_contentW, listH, w, view.height, vbarW, _hbarH);
            _clipW = sb[2]; _clipH = sb[3];
            var content = new Rect(0, 0, Math.Max(_contentW, _clipW), listH);
            _scroll = GUI.BeginScrollView(view, _scroll, content);
            _inScroll = true;
            bool repaint = ev.type == EventType.Repaint;
            if (repaint)
            {
                if (_ownFitStamp != _ownStamp || _ownFitFont != _label.fontSize || _ownFit.Length != n * nc)
                {
                    if (_ownFit.Length != n * nc) _ownFit = new string[n * nc]; else Array.Clear(_ownFit, 0, _ownFit.Length);
                    _ownFitStamp = _ownStamp; _ownFitFont = _label.fontSize;
                }
                if (!_ownColourSet) { _ownIconColour = ToColor(NtdsIcons.PostureColour(_set, "Allied"), false); _ownColourSet = true; }
            }
            int first = Math.Max(0, (int)(_scroll.y / rh)), last = Math.Min(n - 1, (int)((_scroll.y + view.height) / rh));
            int newHover = -1, iconsDrawn = 0;
            int px = Math.Max(4, (int)Math.Round(14 * s));
            for (int i = first; i <= last; i++)
            {
                var row = _ownShown[i];
                float y = i * rh;
                var rr = new Rect(0, y, content.width, rh);
                bool inView = Ui.OverviewExpand.RowHitAllowed(ev.mousePosition.x - _scroll.x, ev.mousePosition.y - _scroll.y + view.y, view.y, _clipW, _clipH, grip);
                bool over = inView && rr.Contains(ev.mousePosition);
                _inViewNow = inView;
                if (over) newHover = row.Uid;
                if (repaint)
                {
                    if (row.Uid == _selUid || over)
                    {
                        var oldc = GUI.color;
                        GUI.color = row.Uid == _selUid ? new Color(0.3f, 0.5f, 0.9f, 0.35f) : new Color(1, 1, 1, 0.12f);
                        GUI.DrawTexture(rr, Texture2D.whiteTexture);
                        GUI.color = oldc;
                    }
                    for (int k = 0; k < nc; k++)
                    {
                        string c = _ownCols[k];
                        var cr = new Rect((float)_ownColX[k], y, (float)_ownColW[k], rh);
                        if (c == "icon")
                        {
                            var tex = OwnIconTexture(row.Icon, px);
                            if (tex != null)
                            {
                                var old = GUI.color; GUI.color = _ownIconColour;
                                GUI.DrawTexture(new Rect(cr.x + (cr.width - px) / 2f, cr.y + (rh - px) / 2f, px, px), tex);
                                GUI.color = old; iconsDrawn++;
                            }
                            continue;
                        }
                        string text = OwnCell(row, c);
                        if (Ui.OverviewOwnColumns.LeftAligned(c))
                        {
                            int fi = i * nc + k;
                            string fit = _ownFit[fi];
                            if (fit == null)
                            {
                                fit = string.IsNullOrEmpty(text) ? "" : Ui.OverviewExpand.Ellipsize(text, cr.width - _label.padding.horizontal, _ownMeasure);
                                _ownFit[fi] = fit;
                            }
                            text = fit;
                            _label.alignment = TextAnchor.MiddleLeft;
                        }
                        else _label.alignment = TextAnchor.MiddleRight;
                        _label.normal.textColor = c == "grp" && row.ModGrp ? OwnMod : text == "—" ? OwnDim : OwnText;
                        GUI.Label(cr, text, _label);
                    }
                    if (over)
                    {
                        if (_hoverSinceId != row.Uid) { _hoverSinceId = row.Uid; _hoverSince = Time.unscaledTime; }
                        if (OwnCellTip(row, ev.mousePosition.x) == null && Time.unscaledTime - _hoverSince > 0.7f) _tip = UiText.Get("overview.own.tip.row");
                    }
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && over)
                {
                    DoSelectOwn(row.Uid);   // a double click acts as a single one (the game's repeated switchToObject is a no-op)
                    ev.Use();
                }
            }
            GUI.EndScrollView();
            _inScroll = false;
            if (repaint)
            {
                _api.Panel.IconsDrawn = iconsDrawn;
                _ownHoverUid = newHover;
                if (newHover < 0) _hoverSinceId = -1;
            }
            // column heads travel with the data (a group of the visible width, columns shifted by the scroll); the click and the tip use the same shift
            if (repaint)
            {
                GUI.BeginGroup(new Rect(0, hh, _clipW, ch));
                _inGroup = true;
                for (int i = 0; i < nc; i++)
                {
                    if (_ownCols[i] == "icon") continue;
                    var hr = Ui.OverviewExpand.HeaderRect(_ownColX[i], _ownColW[i], _scroll.x, ch);
                    _head.alignment = Ui.OverviewOwnColumns.LeftAligned(_ownCols[i]) ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                    GUI.Label(new Rect(hr[0], hr[1], hr[2], hr[3]), _ownHeads[i], _head);
                }
                GUI.EndGroup();
                _inGroup = false;
            }
            if (ev.mousePosition.y >= hh && ev.mousePosition.y < hh + ch)
            {
                int hi = Ui.OverviewExpand.HeaderAt(_ownColX, _ownColW, _scroll.x, _clipW, ev.mousePosition.x);
                if (hi >= 0 && _ownCols[hi] != "icon")
                {
                    if (repaint) SetTip(new Rect(0, hh, _clipW, ch), ev.mousePosition, OwnColTip(hi));
                    if (ev.type == EventType.MouseDown && ev.button == 0)
                    {
                        if (_ownSortCol == _ownCols[hi]) _ownSortAsc = !_ownSortAsc; else { _ownSortCol = _ownCols[hi]; _ownSortAsc = true; }
                        _ownForceSort = true; _nextBuild = 0;
                        SaveOwn();
                        ev.Use();
                    }
                }
            }
            DrawOwnGrip(ev, grip);
            if (repaint) _api.Panel.Tip = _tip;
            DrawTip(ev, w, totalH);
        }

        /// <summary>The tip of the cell under the mouse (full task text, leader mark); null = none (the row tip follows after a pause).</summary>
        private string OwnCellTip(Ui.OwnRow row, float mx)
        {
            if (_tip != null) return _tip;
            for (int k = 0; k < _ownCols.Length; k++)
            {
                if (mx < _ownColX[k] || mx >= _ownColX[k] + _ownColW[k]) continue;
                string c = _ownCols[k];
                if (c == "task" && row.Task != "—") { _tip = row.Task; return _tip; }
                if (c == "grp" && row.Leader) { _tip = UiText.Get("overview.own.tip.leader"); return _tip; }
                if (c == "name" || c == "type") { string t = OwnCell(row, c); if (t != "—") { _tip = t; return _tip; } }
                break;
            }
            return null;
        }

        private void DrawOwnGrip(Event ev, float[] grip)
        {
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
        }

        // ---------------------------------------------------------------- click

        /// <summary>A click on a row selects the unit like F5 does (when ClickSelectsInGame) and centres the map on it.</summary>
        private void DoSelectOwn(int uid)
        {
            string err;
            if (_cfg.ClickSelectsInGame)
            {
                var r = _api.SelectOwnUnit(uid, "overview", out err);
                if (r == null) Note(OwnErrText(err)); else _selUid = uid;
            }
            else _selUid = uid;
            var c = _api.CenterMap(null, uid, null, null, "overview", out err);
            if (c == null) { Note(OwnErrText(err)); return; }
            if (c.Contains("\"map_open\":false")) Note(UiText.Get("overview.note.map_closed"));
        }

        private static string OwnErrText(string err) { return err == "unknown_target" ? UiText.Get("overview.own.note.none") : err; }
    }
}
