using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-174 (ADR-0073 section 3): two Noesis ToggleButtons (ASW, CAP) on the game's bottom panel, right of the anchor (UnitStatusWindow). Two Auto columns are
    /// inserted at N, children that reached the right edge get ColumnSpan + 2 (ToolbarMath.Edits), the buttons take the anchor's Style. Click (not the
    /// check events) drives the window, the window drives IsChecked and the lit glyph each frame (no echo). Everything is undone by Remove (columns, spans).
    /// The check runs once per second; failed tries back off like the map menu (MapMenuMath.RetryDelayS) and after MaxTries the module is off until the next mission.
    /// Game members only through ToolbarSpecs (all optional): a missing member is a failed try, never an exception out of Update.
    /// </summary>
    internal sealed class ToolbarButtons
    {
        private sealed class Entry
        {
            public string Tag, Glyph, TipKey;
            public Func<bool> IsOpen;
            public Action<bool> SetOpen;
            public Func<string> Hotkey;
            public ToolbarToggle Toggle;
            public object Button, Image;
            public Texture2D TexNormal, TexLit;
            public object SrcNormal, SrcLit;
            public string TipShown;
        }

        private readonly GameApi _api;
        private readonly Action<string> _log;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<object> _cols = new List<object>();
        private readonly List<object> _editChildren = new List<object>();
        private readonly List<ToolbarCellEdit> _edits = new List<ToolbarCellEdit>();
        private object _grid, _view;
        private string _anchorName, _mode = "pending", _lastReason;
        private bool _inserted, _wasInMission, _notFoundLogged, _styleLogged, _everInserted, _lastHidden;
        private int _failures, _tipRev = -1;
        private double _nextTry, _nextCheck, _reinsertLogAt = -1e9;
        private int _reinserts;
        private string _lastDead;

        public ToolbarButtons(GameApi api, Action<string> log) { _api = api; _log = log ?? (s => { }); }

        public bool Enabled { get; set; }
        public int MaxTries { get; set; }
        public string Mode { get { return _mode; } }

        public void Add(string tag, string glyph, string tipKey, Func<bool> isOpen, Action<bool> setOpen, Func<string> hotkey)
        {
            _entries.Add(new Entry { Tag = tag, Glyph = glyph, TipKey = tipKey, IsOpen = isOpen, SetOpen = setOpen, Hotkey = hotkey, Toggle = new ToolbarToggle(isOpen, setOpen) });
        }

        /// <summary>Plugin.Update: every frame, cheap. The check / insertion is throttled inside.</summary>
        public void Update(double now, bool inMission, bool hidden)
        {
            try
            {
                if (!Enabled)
                {
                    if (_inserted) { Remove("setting"); }
                    return;
                }
                if (inMission && !_wasInMission) { _failures = 0; _lastReason = null; _notFoundLogged = false; _nextTry = 0; _nextCheck = 0; _reinserts = 0; _reinsertLogAt = -1e9; _lastDead = null; _everInserted = false; if (_mode == "off") _mode = "pending"; }
                if (!inMission && _wasInMission) { Forget(); _mode = "pending"; }
                _wasInMission = inMission;
                bool checkDue = _inserted && now >= _nextCheck;
                string dead = checkDue ? DeadReason() : null;
                bool alive = dead == null;
                if (checkDue) _nextCheck = now + ToolbarMath.CheckS;
                var act = ToolbarMath.Next(Enabled, inMission, _mode == "off", _inserted, alive, now, _nextTry, checkDue ? now : now + 1);
                if (_inserted && !alive)
                {
                    // a panel that is still the game's one gets our edits rolled back first, otherwise every reinsert would pile up columns and spans
                    _lastDead = dead; _reinserts++;
                    if (ToolbarMath.RevertBeforeReinsert(dead)) Remove("dead"); else Forget();
                }
                if (act == ToolbarAction.Insert) TryInsert(now);
                if (_inserted) Sync(hidden);
            }
            catch (Exception ex)
            {
                _log("toolbar: off (update failed: " + ex.GetType().Name + ": " + ex.Message + ")");
                try { Remove("error"); } catch { }
                _mode = "off";
            }
        }

        // ------------------------------------------------------------ insertion

        private bool Miss(double now, string reason)
        {
            _failures++;
            _nextTry = now + MapMenuMath.RetryDelayS(_failures);
            if (!_notFoundLogged) { _notFoundLogged = true; _log("toolbar: not found (" + reason + ")"); }
            _lastReason = reason;
            if (MapMenuMath.GiveUp(_failures, MaxTries)) { _mode = "off"; _log("toolbar: off (" + reason + " after " + _failures + " tries)"); }
            return false;
        }

        private object Find(object view, string name)
        {
            try { return _api.Call(view, ToolbarSpecs.FindName, name); } catch { return null; }
        }

        private int IntOf(object o) { try { return Convert.ToInt32(o); } catch { return 0; } }

        private static int CountOf(object collection, GameApi api, MemberSpec count) { try { return Convert.ToInt32(api.Get(collection, count)); } catch { return 0; } }

        private object ChildAt(object children, int i) { try { return _api.Call(children, ToolbarSpecs.ChildItem, i); } catch { return null; } }

        private void Put(object o, MemberSpec s, object value)
        {
            if (!_api.Set(o, s, value)) throw new MissingMemberException(s.Type, s.Member);
        }

        private bool TryInsert(double now)
        {
            if (!_api.Present(ToolbarSpecs.MainViewInstance)) return Miss(now, "no_view");
            object view = _api.GetStatic(ToolbarSpecs.MainViewInstance);
            if (view == null) return Miss(now, "no_view");
            var gridType = _api.TypeOf(ToolbarSpecs.GridType);
            object anchor = null, grid = null;
            string anchorName = null;
            var anchorEls = new List<object>();
            foreach (var name in ToolbarMath.AnchorNames)
            {
                var el = Find(view, name);
                if (el == null) continue;
                if (anchor == null) { anchor = el; anchorName = name; }
                anchorEls.Add(el);
            }
            if (anchor == null) return Miss(now, "no_anchor");
            grid = _api.Get(anchor, ToolbarSpecs.Parent);
            if (grid == null || gridType == null || !gridType.IsInstanceOfType(grid)) return Miss(now, "no_grid");
            var cols = new List<int>();
            foreach (var el in anchorEls)
                if (ReferenceEquals(_api.Get(el, ToolbarSpecs.Parent), grid)) cols.Add(IntOf(_api.CallStatic(ToolbarSpecs.GetColumn, el)));
            int n = ToolbarMath.InsertColumn(cols);
            object defs = _api.Get(grid, ToolbarSpecs.ColumnDefinitions), rows = _api.Get(grid, ToolbarSpecs.RowDefinitions), children = _api.Get(grid, ToolbarSpecs.Children);
            if (defs == null || rows == null || children == null) return Miss(now, "no_grid");
            int row = IntOf(_api.CallStatic(ToolbarSpecs.GetRow, anchor));
            if (!ToolbarMath.AnchorRowOk(row, CountOf(rows, _api, ToolbarSpecs.RowsCount)) || n < 0 || n > CountOf(defs, _api, ToolbarSpecs.ColsCount)) return Miss(now, "bad_row");
            var sw = Stopwatch.StartNew();
            RemoveStale(children);
            try
            {
                EnsureTextures();
                var auto = _api.GetStatic(ToolbarSpecs.GridLengthAuto);
                for (int i = 0; i < ToolbarMath.Added; i++)
                {
                    var def = _api.New(ToolbarSpecs.ColumnDefCtor);
                    if (auto != null) Put(def, ToolbarSpecs.ColumnDefWidth, auto);
                    _api.Call(defs, ToolbarSpecs.ColsInsert, n + i, def);
                    _cols.Add(def);
                }
                var cells = new List<ToolbarCell>();
                var kids = new List<object>();
                int count = CountOf(children, _api, ToolbarSpecs.ChildCount);
                for (int i = 0; i < count; i++)
                {
                    var ch = ChildAt(children, i);
                    kids.Add(ch);
                    cells.Add(new ToolbarCell { Id = i, Column = ch == null ? 0 : IntOf(_api.CallStatic(ToolbarSpecs.GetColumn, ch)), Span = ch == null ? 1 : IntOf(_api.CallStatic(ToolbarSpecs.GetColumnSpan, ch)) });
                }
                int spans = 0;
                foreach (var e in ToolbarMath.Edits(cells, n))
                {
                    var ch = kids[e.Id];
                    if (ch == null) continue;
                    if (e.NewColumn != e.OldColumn) _api.CallStatic(ToolbarSpecs.SetColumn, ch, e.NewColumn);
                    if (e.NewSpan != e.OldSpan) { _api.CallStatic(ToolbarSpecs.SetColumnSpan, ch, e.NewSpan); spans++; }
                    _edits.Add(e); _editChildren.Add(ch);
                }
                var style = _api.Get(anchor, ToolbarSpecs.Style);
                if (style == null && !_styleLogged) { _styleLogged = true; _log("toolbar: style missing, plain button"); }
                var visEnum = (_api.Member(ToolbarSpecs.Visibility) as PropertyInfo);
                var stretchEnum = (_api.Member(ToolbarSpecs.ImageStretch) as PropertyInfo);
                int slot = 0;
                var names = new List<string>();
                foreach (var e in _entries)
                {
                    var btn = _api.New(ToolbarSpecs.ToggleCtor);
                    Put(btn, ToolbarSpecs.Width, 32f);
                    Put(btn, ToolbarSpecs.Height, 32f);
                    Put(btn, ToolbarSpecs.Focusable, false);
                    if (style != null) Put(btn, ToolbarSpecs.Style, style);
                    Put(btn, ToolbarSpecs.Tag, e.Tag);
                    _api.CallStatic(ToolbarSpecs.SetColumn, btn, n + slot);
                    _api.CallStatic(ToolbarSpecs.SetRow, btn, row);
                    var img = _api.New(ToolbarSpecs.ImageCtor);
                    Put(img, ToolbarSpecs.Width, 30f);
                    Put(img, ToolbarSpecs.Height, 30f);
                    if (stretchEnum != null && stretchEnum.PropertyType.IsEnum) Put(img, ToolbarSpecs.ImageStretch, Enum.Parse(stretchEnum.PropertyType, "Uniform"));
                    e.SrcNormal = _api.New(ToolbarSpecs.TextureSourceCtor, e.TexNormal);
                    e.SrcLit = _api.New(ToolbarSpecs.TextureSourceCtor, e.TexLit);
                    bool open = e.IsOpen();
                    e.Toggle = new ToolbarToggle(e.IsOpen, e.SetOpen);
                    Put(img, ToolbarSpecs.ImageSource, open ? e.SrcLit : e.SrcNormal);
                    Put(btn, ToolbarSpecs.Content, img);
                    Put(btn, ToolbarSpecs.IsChecked, (bool?)open);
                    e.TipShown = null;
                    Hook(e, btn);
                    _api.Call(children, ToolbarSpecs.ChildAdd, btn);
                    e.Button = btn; e.Image = img;
                    names.Add(e.Glyph);
                    slot++;
                }
                _grid = grid; _view = view; _anchorName = anchorName;
                _inserted = true; _mode = "on"; _failures = 0; _nextCheck = now + ToolbarMath.CheckS;
                _lastHidden = false; _tipRev = -1;
                bool again = _everInserted;
                if (!again || ToolbarMath.ReinsertLogDue(_reinserts, now, _reinsertLogAt))
                {
                    _log("toolbar: inserted " + string.Join(",", names) + " anchor=" + anchorName + " col=" + n + " spans+2=" + spans + " via=" + (again ? "reinsert" : "first")
                        + (again ? " why=" + (_lastDead ?? "?") + " n=" + _reinserts : "") + " ms=" + sw.ElapsedMilliseconds);
                    if (again) { _reinsertLogAt = now; _reinserts = 0; }
                }
                _everInserted = true;
                return true;
            }
            catch (Exception ex)
            {
                _log("toolbar: off (insert failed: " + ex.GetType().Name + ": " + ex.Message + ")");
                try { Remove("error"); } catch { }
                _mode = "off";
                return false;
            }
        }

        /// <summary>A leftover of our own buttons (same Tag) in the grid, not known to this instance: taken out before a fresh insertion.</summary>
        private void RemoveStale(object children)
        {
            int count = CountOf(children, _api, ToolbarSpecs.ChildCount);
            var stale = new List<object>();
            for (int i = 0; i < count; i++)
            {
                var ch = ChildAt(children, i);
                if (ch == null) continue;
                var tag = _api.Get(ch, ToolbarSpecs.Tag);
                foreach (var e in _entries)
                    if (MapMenuMath.TagIs(tag, e.Tag) && !ReferenceEquals(e.Button, ch)) stale.Add(ch);
            }
            foreach (var s in stale) { try { _api.Call(children, ToolbarSpecs.ChildRemove, s); } catch { } }
        }

        private void Hook(Entry e, object btn)
        {
            var ev = _api.Member(ToolbarSpecs.Click) as EventInfo;
            if (ev == null) throw new MissingMemberException(ToolbarSpecs.Click.Type, ToolbarSpecs.Click.Member);
            Action<object, object> act = (s, a) => OnClick(e, s);
            var h = Delegate.CreateDelegate(ev.EventHandlerType, act.Target, act.Method);
            ev.AddEventHandler(btn, h);
        }

        private void OnClick(Entry e, object sender)
        {
            try
            {
                bool open = e.Toggle.Click();
                _log("toolbar: click " + e.Glyph + " -> " + (open ? "open" : "close"));
                Show(e, open);
            }
            catch (Exception ex) { _log("toolbar: click failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void Show(Entry e, bool open)
        {
            var btn = e.Button;
            if (btn != null) _api.Set(btn, ToolbarSpecs.IsChecked, (bool?)open);
            var img = e.Image;
            if (img != null) _api.Set(img, ToolbarSpecs.ImageSource, open ? e.SrcLit : e.SrcNormal);
        }

        // ------------------------------------------------------------ per frame

        private void Sync(bool hidden)
        {
            foreach (var e in _entries)
            {
                var btn = e.Button;
                if (btn == null) continue;
                bool? v = e.Toggle.Pull();
                if (v.HasValue) Show(e, v.Value);
            }
            if (hidden != _lastHidden || _tipRev == -1)
            {
                var vis = _api.Member(ToolbarSpecs.Visibility) as PropertyInfo;
                if (vis != null && vis.PropertyType.IsEnum)
                    foreach (var e in _entries) { var btn = e.Button; if (btn != null) _api.Set(btn, ToolbarSpecs.Visibility, Enum.Parse(vis.PropertyType, ToolbarMath.VisibilityName(hidden))); }
                _lastHidden = hidden;
            }
            // tip follows the language and the picked hotkey
            foreach (var e in _entries)
            {
                var btn = e.Button;
                if (btn == null) continue;
                string key = null;
                try { key = e.Hotkey == null ? "" : e.Hotkey(); } catch { key = ""; }
                string tip = ToolbarMath.Tip(UiText.Get(UiText.DefaultSection, e.TipKey), key);
                if (tip != e.TipShown) { e.TipShown = tip; _api.Set(btn, ToolbarSpecs.ToolTip, tip); }
            }
            _tipRev = UiText.Revision;
        }

        // ------------------------------------------------------------ check / removal

        /// <summary>null = the panel is alive; otherwise the first failed check (see ToolbarMath.DeadReason).</summary>
        private string DeadReason()
        {
            bool hasEntries = _entries.Count > 0, gridKnown = _grid != null, viewSame = false, anchorIn = false, known = true, inGrid = true, loaded = true;
            if (hasEntries && gridKnown)
            {
                var grid = _grid;
                object view = _api.GetStatic(ToolbarSpecs.MainViewInstance);
                viewSame = view != null && ReferenceEquals(view, _view);
                if (viewSame)
                {
                    var anchor = _anchorName == null ? null : Find(view, _anchorName);
                    anchorIn = anchor != null && ReferenceEquals(_api.Get(anchor, ToolbarSpecs.Parent), grid);
                    if (anchorIn)
                        foreach (var e in _entries)
                        {
                            var btn = e.Button;
                            if (btn == null) { known = false; break; }
                            if (!ReferenceEquals(_api.Get(btn, ToolbarSpecs.Parent), grid)) { inGrid = false; break; }
                            if (!GameApi.BoolOf2(_api.Get(btn, ToolbarSpecs.IsLoaded))) { loaded = false; break; }
                        }
                }
            }
            return ToolbarMath.DeadReason(hasEntries, gridKnown, viewSame, anchorIn, known, inGrid, loaded);
        }

        /// <summary>The view is gone (mission end / recreated): only our references are dropped; nothing is written to the dead tree.</summary>
        private void Forget()
        {
            foreach (var e in _entries) { e.Button = null; e.Image = null; e.SrcNormal = null; e.SrcLit = null; }
            _cols.Clear(); _edits.Clear(); _editChildren.Clear();
            _grid = null; _view = null; _anchorName = null; _inserted = false;
        }

        /// <summary>Takes the buttons and our columns out and puts Column / ColumnSpan back.</summary>
        public void Remove(string why)
        {
            var grid = _grid;
            if (grid != null)
            {
                try
                {
                    var children = _api.Get(grid, ToolbarSpecs.Children);
                    foreach (var e in _entries)
                    {
                        var btn = e.Button;
                        if (btn != null && children != null) { try { _api.Call(children, ToolbarSpecs.ChildRemove, btn); } catch { } }
                    }
                    var back = ToolbarMath.Revert(_edits);
                    for (int i = 0; i < back.Count; i++)
                    {
                        // _edits and _editChildren are parallel; Revert reverses the order
                        var ch = _editChildren[_edits.Count - 1 - i];
                        if (ch == null) continue;
                        var r = back[i];
                        if (r.NewColumn != r.OldColumn) _api.CallStatic(ToolbarSpecs.SetColumn, ch, r.NewColumn);
                        if (r.NewSpan != r.OldSpan) _api.CallStatic(ToolbarSpecs.SetColumnSpan, ch, r.NewSpan);
                    }
                    var defs = _api.Get(grid, ToolbarSpecs.ColumnDefinitions);
                    if (defs != null)
                        foreach (var c in _cols) { var d = c; if (d != null) { try { _api.Call(defs, ToolbarSpecs.ColsRemove, d); } catch { } } }
                }
                catch (Exception ex) { _log("toolbar: remove failed: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (why == "setting") _log("toolbar: removed (setting)");
            Forget();
            if (_mode == "on") _mode = "pending";
            _nextTry = 0; _failures = 0;
        }

        public void Dispose()
        {
            try { Remove("dispose"); } catch { }
            foreach (var e in _entries)
            {
                if (e.TexNormal != null) UnityEngine.Object.Destroy(e.TexNormal);
                if (e.TexLit != null) UnityEngine.Object.Destroy(e.TexLit);
                e.TexNormal = null; e.TexLit = null;
            }
        }

        // ------------------------------------------------------------ textures

        private static Texture2D MakeTexture(string glyph, uint rgba)
        {
            int px = ToolbarGlyphs.TexPx;
            var data = ToolbarGlyphs.Rgba(ToolbarGlyphs.Mask(glyph, px), rgba, true);
            if (data == null) throw new InvalidOperationException("no glyph " + glyph);
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.LoadRawTextureData(data);
            tex.Apply(false, false);
            return tex;
        }

        private void EnsureTextures()
        {
            foreach (var e in _entries)
            {
                if (e.TexNormal == null) e.TexNormal = MakeTexture(e.Glyph, ToolbarGlyphs.Normal);
                if (e.TexLit == null) e.TexLit = MakeTexture(e.Glyph, ToolbarGlyphs.Lit);
            }
        }
    }
}
