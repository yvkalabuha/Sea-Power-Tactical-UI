// T-148 U3/U4: the shared control set of the mod windows (ADR-0049, ui-mod-window-layout.md §4). Pure C#: no Unity types.
// The window body calls UiKit; UiKit lays the controls out in a flow (wrap instead of growing, "..." truncation with a tooltip,
// sections, form rows, tables) and hands every element to an IUiPainter (UnityPainter in the game, RecordPainter on the stands).
// The window width never grows from the controls by itself; a table with content-fit columns (UiColumn.MaxEm) reports the width it would need
// (WantWidth) and the frame may widen the window up to its maximum (live finding 0.27.2: cut ship names and take-off times in the CAP window).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal struct UiRect
    {
        public float X, Y, W, H;
        public UiRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public bool Overlaps(UiRect o)
        {
            const float eps = 0.5f;
            return X + eps < o.X + o.W && o.X + eps < X + W && Y + eps < o.Y + o.H && o.Y + eps < Y + H;
        }
    }

    internal enum UiTone { Normal, Dim, Accent, Warn, Error, Ok }
    internal enum UiFont { Body, Small, Title, Section }
    internal enum UiColorRole { WindowBg, TitleBar, SectionBar, Selected, Separator, Accent, Divider, Warn, Error }   // T-178 K0: only appended

    internal struct UiAction
    {
        public string Id, Text, Tip;
        public bool Enabled;
        public UiTone Tone;
        public UiAction(string id, string text, string tip = null, bool enabled = true, UiTone tone = UiTone.Normal)
        {
            Id = id; Text = text; Tip = tip; Enabled = enabled; Tone = tone;
        }
    }

    /// <summary>Draws and reads one element. Coordinates are window-local (inside a scroll view: relative to the view).</summary>
    internal interface IUiPainter
    {
        float Measure(string text, UiFont font);
        void Label(UiRect r, string text, UiFont f, UiTone t, string tip);
        /// <summary>true = pressed in this event (never for a disabled button; see DisabledPressed).</summary>
        bool Button(UiRect r, string id, string text, string tip, bool enabled, bool selected, UiTone tone = UiTone.Normal);
        /// <summary>true = a disabled button with this id was clicked in this event (the kit shows its tip).</summary>
        bool DisabledPressed(string id);
        /// <summary>A transparent click area (section bars).</summary>
        bool Hit(UiRect r, string id, string tip);
        /// <summary>T-155 K3: a text link (table cells "ship", "aircraft"): true = clicked. The painter underlines it and uses the accent tone while the mouse is over it.</summary>
        bool Link(UiRect r, string id, string text, UiFont f, UiTone t, string tip);
        bool Check(UiRect r, string id, bool on, string text, string tip);
        string Field(UiRect r, string id, string text);
        void Fill(UiRect r, UiColorRole c);
        void BeginScroll(string id, UiRect view, float contentH);
        void EndScroll();
        string Tooltip { get; }
        // ---- T-178 K0 (ADR-0077): primitives of the compass, the lists and the hit zones (only additions)
        void Ring(UiRect r, UiColorRole c, float width);
        void Line(float x1, float y1, float x2, float y2, UiColorRole c, float width);
        /// <summary>true while LMB is held after a press inside r (also outside r afterwards); mx/my window-local.</summary>
        bool Drag(UiRect r, string id, out float mx, out float my);
        /// <summary>LMB went down in this event anywhere in the window (closing an open list by a click outside).</summary>
        bool MouseDown(out float mx, out float my);
        bool EscapePressed();
        /// <summary>NTDS / glyph icon by key ("ntds:Surface:Own", "glyph:⚓").</summary>
        void Icon(UiRect r, string icon, UiTone t);
        /// <summary>Hit zones of the previous pass (UiHit.Inflate), by control id; the painter tests the mouse against them.</summary>
        void SetHitRects(IReadOnlyDictionary<string, UiRect> hits);
        /// <summary>Deviation from the plan (K0, needed by the Dropdown list): layer = false (Begin): `blocked` is the open list of the previous pass, the controls
        /// under it do not react to the mouse (W = 0: none). layer = true (End): what is drawn next is the overlay layer (the open list itself) and reacts.</summary>
        void SetOverlay(UiRect blocked, bool layer);
    }

    /// <summary>Optional value memory of a window (UiWindowStateStore implements it, step I1). UiKit uses `_state as IUiWindowValues`.</summary>
    internal interface IUiWindowValues { string Value(string id, string dflt); void SetValue(string id, string v); }

    internal sealed class UiColumn
    {
        public string Header;
        public float Em;
        public bool Flex, Right;
        /// <summary>T-155 K3: a cell with text other than "—" is a link (Table.linkClick).</summary>
        public bool Link;
        /// <summary>Content fit (live finding 0.27.2): &gt; 0 = the column is as wide as its widest cell (header included) in this frame, at least Em and at most
        /// MaxEm font sizes. For the flex column it is the width it asks for (WantWidth); the flex column still takes the rest of the row.</summary>
        public float MaxEm;
        /// <summary>Content-fit limits shared by the windows: a ship name ("Dwight D. Eisenhower CVN-69") and a state / place cell with its times.</summary>
        public const float ShipMaxEm = 18f, StateMaxEm = 22f;
        public UiColumn(string header, float em, bool flex = false, bool right = false, bool link = false) { Header = header; Em = em; Flex = flex; Right = right; Link = link; }
    }

    internal sealed class UiTable
    {
        public string Id;
        public UiColumn[] Columns;
        public bool Checkboxes = true;
    }

    internal sealed partial class UiKit
    {
        private const string Ell = "…";
        private readonly IUiPainter _p;
        private readonly UiTheme _t;
        private readonly IUiWindowState _state;
        private readonly string _win;
        private readonly Dictionary<string, float>[] _measure = { new Dictionary<string, float>(), new Dictionary<string, float>(), new Dictionary<string, float>(), new Dictionary<string, float>() };
        private readonly HashSet<string> _ovSeen = new HashSet<string>();
        // 0.28.1 log of cut columns: only a cut that stays OverflowStablePasses passes in a row (the first frames of a window, before
        // UiFrameMath.AutoGrow widens it, cut the content-fit columns for a moment), and one line per window per session
        private readonly Dictionary<string, string> _ovPass = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _ovStreak = new Dictionary<string, int>();
        internal const int OverflowStablePasses = 120;
        private static readonly HashSet<string> _ovLoggedWin = new HashSet<string>();
        internal static void ResetOverflowLog() { lock (_ovLoggedWin) _ovLoggedWin.Clear(); }
        private int _rev = -1;
        private const int MeasureCap = 4096;   // T-151: dynamic captions (distances, times) must not grow the cache without bound
        private static int _measureTotal, _measureClears;
        internal static int MeasureTotal { get { return _measureTotal; } }
        internal static int MeasureClears { get { return _measureClears; } }
        internal int MeasureEntries { get { int n = 0; foreach (var d in _measure) n += d.Count; return n; } }

        // T-178 Kd3: the rectangles of the controls of this pass (painter coordinates) -> inflated zones for the next pass
        private readonly Dictionary<string, UiRect> _hitNow = new Dictionary<string, UiRect>();
        private Dictionary<string, UiRect> _hitPrev = new Dictionary<string, UiRect>();
        private Dictionary<string, UiRect> _hitSpare = new Dictionary<string, UiRect>();   // T-239: swapped with _hitPrev, no new table per pass
        private readonly List<string> _hitIds = new List<string>();
        private readonly List<UiRect> _hitRects = new List<UiRect>();
        private UiRect[] _hitBuf = new UiRect[0];
        private void NoteHit(string id, UiRect painterRect) { if (!string.IsNullOrEmpty(id)) _hitNow[id] = painterRect; }

        private string _scope, _scopePrefix;
        /// <summary>T-291: a prefix for the ids of the controls drawn while it is set (the cards of one stack pass). null/"" = none.
        /// Applied to every id the kit hands to the painter and to the hit zones; not to the window state keys, overflow lines, DisabledId or the id returned by Actions.</summary>
        public string IdScope
        {
            get { return _scope; }
            set { _scope = string.IsNullOrEmpty(value) ? null : value; _scopePrefix = _scope == null ? null : _scope + "/"; }
        }

        public static string ScopedId(string scope, string id) { return string.IsNullOrEmpty(scope) || id == null ? id : scope + "/" + id; }

        private string Sid(string id) { return _scopePrefix == null || id == null ? id : Cat(_scopePrefix, id); }

        private void EndHits()
        {
            var next = _hitSpare;
            next.Clear();
            if (_hitNow.Count > 0)
            {
                _hitIds.Clear(); _hitRects.Clear();
                foreach (var kv in _hitNow) { _hitIds.Add(kv.Key); _hitRects.Add(kv.Value); }
                if (_hitBuf.Length < _hitIds.Count) _hitBuf = new UiRect[_hitIds.Count + 64];
                UiHit.InflateInto(_hitRects, UiHit.Slop(_t.S), _hitBuf);
                for (int i = 0; i < _hitIds.Count; i++) next[_hitIds[i]] = _hitBuf[i];
            }
            _hitSpare = _hitPrev;
            _hitPrev = next;
            _hitNow.Clear();
        }

        // T-239: memoised concatenations (ids "x.dec", "x.3", tips "a\nb"): the hot path of a window must not build the same strings every pass
        private readonly Dictionary<(string, string), string> _cat = new Dictionary<(string, string), string>();
        private readonly Dictionary<(string, int), string> _catIdx = new Dictionary<(string, int), string>();
        private readonly Dictionary<(string, string), string> _defTip = new Dictionary<(string, string), string>();

        internal string Cat(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return a;
            if (string.IsNullOrEmpty(a)) return b;
            string r;
            if (!_cat.TryGetValue((a, b), out r))
            {
                if (_cat.Count >= MeasureCap) _cat.Clear();
                r = a + b; _cat[(a, b)] = r;
            }
            return r;
        }

        /// <summary>id + "." + i</summary>
        internal string IdxId(string id, int i)
        {
            string r;
            if (!_catIdx.TryGetValue((id, i), out r))
            {
                if (_catIdx.Count >= MeasureCap) _catIdx.Clear();
                r = id + "." + i.ToString(System.Globalization.CultureInfo.InvariantCulture); _catIdx[(id, i)] = r;
            }
            return r;
        }

        /// <summary>(tip + "\n") + UiText.Fmt("ui.default.tip", what), memoised (cleared with the text revision).</summary>
        internal string DefaultTip(string tip, string what)
        {
            string r;
            var k = (tip ?? "", what ?? "");
            if (!_defTip.TryGetValue(k, out r))
            {
                if (_defTip.Count >= MeasureCap) _defTip.Clear();
                string add = UiText.Fmt("ui.default.tip", what);
                // T-259: a settings row tip already ends with "Default: <what>" (settings.ui.default_is); say it once
                string had = UiText.Fmt("settings.ui.default_is", what);
                if (!string.IsNullOrEmpty(tip) && (tip.EndsWith(add, StringComparison.OrdinalIgnoreCase) || tip.EndsWith(had, StringComparison.OrdinalIgnoreCase))) r = tip;
                else r = (string.IsNullOrEmpty(tip) ? "" : tip + "\n") + add;
                _defTip[k] = r;
            }
            return r;
        }

        private float[] _fitTw = new float[0], _fitW = new float[0];

        private float _width, _left, _right, _x, _y, _lineH, _lineStart, _labelW, _labelBottom, _ox, _oy;
        private bool _secPending;
        private UiRect _secBar;
        private string _secKey, _secFull, _secSummary, _secHit;
        private readonly Dictionary<string, string[]> _secStr = new Dictionary<string, string[]>(), _hdrStr = new Dictionary<string, string[]>();   // T-239: id -> {key, "sec."+key}; header -> {"v "+h, "> "+h}
        private bool _secOpen;
        private float _secBtnRight;
        private bool _anySection;

        public UiKit(IUiPainter p, UiTheme t, IUiWindowState state, string windowId)
        {
            _p = p; _t = t; _state = state; _win = windowId ?? "";
            _labelW = t.LabelMin;
        }

        public UiTheme Theme { get { return _t; } }
        public IUiPainter Painter { get { return _p; } }
        public List<string> Overflows { get; } = new List<string>();
        public Action<string> Log { get; set; }
        public string DisabledClick { get; private set; }
        /// <summary>The id of the last disabled button pressed in this pass (also without a tip). Reset by Begin.</summary>
        public string DisabledId { get; private set; }
        /// <summary>The body width the content of this pass would need to show its content-fit columns uncut (0 = no wish). Reset by Begin.</summary>
        public float WantWidth { get; private set; }

        // ---------------------------------------------------------------- frame

        public void Begin(float width)
        {
            if (_rev != UiText.Revision) { _wrap.Clear(); _defTip.Clear(); _secStr.Clear(); _hdrStr.Clear(); foreach (var d in _measure) { System.Threading.Interlocked.Add(ref _measureTotal, -d.Count); d.Clear(); } _rev = UiText.Revision; }
            _width = width;
            _left = _t.Pad; _right = Math.Max(_left, width - _t.Pad);
            _x = _left; _y = _t.Pad; _lineH = 0; _lineStart = _left; _labelBottom = 0; _ox = 0; _oy = 0;
            _secPending = false; _anySection = false;
            DisabledClick = null; DisabledId = null;
            WantWidth = 0;
            BeginOverlay();
            _hitNow.Clear();
            _p.SetHitRects(_hitPrev);
        }

        public float End()
        {
            IdScope = null;
            FlushSection();
            EndLine();
            OverflowPass();
            EndOverlay();
            EndHits();
            var ov = OverlayRect;   // an open list is never cut by the bottom of the window: the content grows to its bottom edge
            return ov.HasValue ? Math.Max(_y + _t.Pad, ov.Value.Y + ov.Value.H + _t.Pad) : _y + _t.Pad;
        }

        private void OverflowPass()
        {
            if (_ovStreak.Count > 0)
            {
                var gone = new List<string>();
                foreach (var k in _ovStreak.Keys) if (!_ovPass.ContainsKey(k)) gone.Add(k);
                foreach (var k in gone) _ovStreak.Remove(k);
            }
            if (_ovPass.Count == 0) return;
            string first = null; int stable = 0;
            foreach (var kv in _ovPass)
            {
                int n; _ovStreak.TryGetValue(kv.Key, out n); _ovStreak[kv.Key] = ++n;
                if (n >= OverflowStablePasses) { stable++; if (first == null) first = kv.Value; }
            }
            _ovPass.Clear();
            if (first == null || Log == null) return;
            lock (_ovLoggedWin) { if (!_ovLoggedWin.Add(_win)) return; }
            try { Log("ui overflow " + first + (stable > 1 ? " (+" + (stable - 1) + " more columns cut; logged once per window)" : "")); } catch { }
        }

        // ---------------------------------------------------------------- measuring and wrapping

        private float M(string text, UiFont f)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var d = _measure[(int)f];
            float w;
            if (!d.TryGetValue(text, out w))
            {
                w = _p.Measure(text, f);
                if (d.Count >= MeasureCap) { System.Threading.Interlocked.Add(ref _measureTotal, -d.Count); System.Threading.Interlocked.Increment(ref _measureClears); d.Clear(); }
                d[text] = w;
                System.Threading.Interlocked.Increment(ref _measureTotal);
            }
            return w;
        }

        private string Cut(string text, UiFont f, float maxW)
        {
            if (string.IsNullOrEmpty(text) || M(text, f) <= maxW + 0.02f) return text;
            if (maxW < M(Ell, f)) return "";
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (M(text.Substring(0, mid).TrimEnd() + Ell, f) <= maxW) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + Ell;
        }

        // T-239: the wrapped lines of a caption depend only on (text, font, width, max lines): cached, the result is shared and read-only
        private sealed class Wrapped { public List<string> Lines; public bool Truncated; }
        private readonly Dictionary<(string, int, float, int), Wrapped> _wrap = new Dictionary<(string, int, float, int), Wrapped>();

        private List<string> WrapText(string text, UiFont f, float maxW, int maxLines, out bool truncated)
        {
            if (string.IsNullOrEmpty(text) || maxW <= 0) { truncated = false; return new List<string>(); }
            var key = (text, (int)f, maxW, maxLines);
            Wrapped w;
            if (!_wrap.TryGetValue(key, out w))
            {
                if (_wrap.Count >= MeasureCap) _wrap.Clear();
                w = new Wrapped();
                w.Lines = WrapUncached(text, f, maxW, maxLines, out w.Truncated);
                _wrap[key] = w;
            }
            truncated = w.Truncated;
            return w.Lines;
        }

        private List<string> WrapUncached(string text, UiFont f, float maxW, int maxLines, out bool truncated)
        {
            truncated = false;
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text) || maxW <= 0) return lines;
            foreach (string para in text.Replace("\r", "").Split('\n'))
            {
                string cur = "";
                foreach (string word in para.Split(' '))
                {
                    if (word.Length == 0) continue;
                    string cand = cur.Length == 0 ? word : cur + " " + word;
                    if (M(cand, f) <= maxW) { cur = cand; continue; }
                    if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                    string rest = word;
                    while (M(rest, f) > maxW && rest.Length > 1)
                    {
                        int lo = 1, hi = rest.Length - 1;
                        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (M(rest.Substring(0, mid), f) <= maxW) lo = mid; else hi = mid - 1; }
                        lines.Add(rest.Substring(0, lo));
                        rest = rest.Substring(lo);
                    }
                    cur = rest;
                }
                if (cur.Length > 0 || para.Length == 0) lines.Add(cur);
            }
            if (lines.Count > maxLines)
            {
                truncated = true;
                string tail = lines[maxLines - 1];
                for (int i = maxLines; i < lines.Count; i++) tail += " " + lines[i];
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                lines[maxLines - 1] = Cut(tail, f, maxW);
                if (!lines[maxLines - 1].EndsWith(Ell)) lines[maxLines - 1] = Cut(lines[maxLines - 1] + Ell, f, maxW);
            }
            return lines;
        }

        private string FullTip(string full, string tip, bool truncated)
        {
            if (!truncated) return tip;
            return string.IsNullOrEmpty(tip) ? full : Cat(Cat(full, "\n"), tip);   // T-239: memoised, a cut caption rebuilds it every pass otherwise
        }

        // ---------------------------------------------------------------- flow

        private UiRect Loc(UiRect r) { return new UiRect(r.X - _ox, r.Y - _oy, r.W, r.H); }

        private void NextLine() { _y += _lineH; _x = _lineStart; _lineH = 0; }

        private void EndLine()
        {
            float adv = _lineH;
            if (_labelBottom > _y + adv) adv = _labelBottom - _y;
            _y += adv; _x = _lineStart; _lineH = 0; _labelBottom = 0;
        }

        /// <summary>Ends the current line and goes back to the left edge (blocks that do not belong to a form row).</summary>
        private void Block()
        {
            EndLine();
            _lineStart = _left; _x = _left;
        }

        private UiRect Place(float w)
        {
            float avail = _right - _x;
            if (w > avail + 0.01f && _x > _lineStart + 0.01f) { NextLine(); avail = _right - _x; }
            if (w > avail) w = Math.Max(0f, avail);
            var r = new UiRect(_x, _y, w, _t.RowH);
            _x += w + _t.Gap;
            if (_t.RowH > _lineH) _lineH = _t.RowH;
            return r;
        }

        private UiRect CtlRect(UiRect slot) { return new UiRect(slot.X, slot.Y + (_t.RowH - _t.CtlH) / 2f, slot.W, _t.CtlH); }
        private UiRect TextRect(UiRect slot) { return new UiRect(slot.X, slot.Y + (_t.RowH - _t.TableRowH) / 2f, slot.W, _t.TableRowH); }

        private bool Btn(UiRect r, string id, string text, string tip, bool enabled, bool selected, UiTone tone)
        {
            string sid = Sid(id);
            NoteHit(sid, Loc(r));
            bool hit = _p.Button(Loc(r), sid, text, tip, enabled, selected, tone);
            if (!enabled)
            {
                if (_p.DisabledPressed(sid))
                {
                    DisabledId = id;
                    if (!string.IsNullOrEmpty(tip)) DisabledClick = tip;
                }
                return false;
            }
            return hit;
        }

        private float BtnWidth(string text, float maxW)
        {
            float w = Math.Max(_t.BtnMin, M(text, UiFont.Body) + 2 * _t.BtnPad);
            return Math.Min(w, Math.Max(_t.BtnMin, maxW));
        }

        // ---------------------------------------------------------------- label column and rows

        /// <summary>A form label wraps to this many lines before it is cut (with the full text in the tip). 3, not 2: the English
        /// "Light smoke of missiles and explosions" was cut to "...and expl…" in the compact theme (live check 2026-10-21).</summary>
        public const int RowLabelLines = 3;

        public float LabelColumn(params string[] labels)
        {
            float w = 0;
            if (labels != null) foreach (string l in labels) w = Math.Max(w, M(l, UiFont.Body));
            _labelW = Math.Max(_t.LabelMin, Math.Min(_t.LabelMax, w));
            return _labelW;
        }

        public void Row(string label, string tip = null)
        {
            EndLine();
            _lineStart = _left; _x = _left;
            if (label != null)
            {
                bool tr;
                var lines = WrapText(label, UiFont.Body, _labelW, RowLabelLines, out tr);
                float y0 = _y + (_t.RowH - _t.TableRowH) / 2f;
                for (int i = 0; i < lines.Count; i++)
                    _p.Label(Loc(new UiRect(_left, y0 + i * _t.TableRowH, _labelW, _t.TableRowH)), lines[i], UiFont.Body, UiTone.Dim, FullTip(label, tip, tr));
                _labelBottom = y0 + lines.Count * _t.TableRowH;
            }
            _lineStart = _left + _labelW + _t.Gap;
            _x = _lineStart;
        }

        public void Wrap() { if (_x > _lineStart + 0.01f || _lineH > 0) NextLine(); }

        /// <summary>T-178 C6: extra space (px at scale 1) before the next control of the line (the gap of [+] [−] is at least 8 px x s).</summary>
        public void Space(float px) { _x += px * _t.S; }

        /// <summary>The value memory of the window (UiWindowStateStore), or null.</summary>
        public IUiWindowValues Values { get { return _state as IUiWindowValues; } }

        /// <summary>Ends the form row: the next element starts at the left edge, not in the value column of the last Row().</summary>
        public void Break() { Block(); }

        // ---------------------------------------------------------------- sections

        // The header text is drawn when the section is flushed, after its bar buttons took their room on the right.
        private void FlushSection()
        {
            if (!_secPending) return;
            _secPending = false;
            float hw = _secBtnRight - (_secBar.X + _t.Pad);
            string shown = Cut(_secFull, UiFont.Section, hw);
            bool hasSum = !string.IsNullOrEmpty(_secSummary);
            float hdrW = Math.Max(0f, hw);
            if (hasSum) hdrW = Math.Min(hdrW, M(shown, UiFont.Section));   // the summary follows the header: its own rectangle must not overlap
            _p.Label(Loc(new UiRect(_secBar.X + _t.Pad, _secBar.Y, hdrW, _secBar.H)), shown, UiFont.Section, UiTone.Accent, shown == _secFull ? null : _secFull);
            if (hasSum)
            {
                float rest = hw - hdrW - _t.Gap;   // truncated first: the header keeps its room
                string sum = Cut(_secSummary, UiFont.Small, rest);
                if (rest > 0f && !string.IsNullOrEmpty(sum))
                    _p.Label(Loc(new UiRect(_secBar.X + _t.Pad + hdrW + _t.Gap, _secBar.Y, Math.Min(rest, M(sum, UiFont.Small)), _secBar.H)), sum, UiFont.Small, UiTone.Dim, sum == _secSummary ? null : _secSummary);
            }
            if (_p.Hit(Loc(_secBar), Sid(_secHit), UiText.Get(_secOpen ? "ui.kit.collapse" : "ui.kit.expand")) && _state != null) _state.SetSectionOpen(_secKey, !_secOpen);
        }

        /// <summary>T-155 K2: summary = a dim text in the bar of a COLLAPSED section ("F-14A: auto"); it is cut before the header and the counter.</summary>
        public bool Section(string id, string header, string counter = null, bool defaultOpen = true, string summary = null)
        {
            FlushSection();
            Block();
            if (_anySection) _y += _t.SectionGap;
            _anySection = true;
            string[] ks;
            if (!_secStr.TryGetValue(id, out ks)) { if (_secStr.Count >= MeasureCap) _secStr.Clear(); ks = new[] { _win + "." + id, "sec." + _win + "." + id }; _secStr[id] = ks; }
            string key = ks[0];
            bool open = _state == null ? defaultOpen : _state.SectionOpen(key, defaultOpen);
            var bar = new UiRect(_left, _y, _right - _left, _t.SectionH);
            _p.Fill(Loc(bar), UiColorRole.SectionBar);
            float cw = string.IsNullOrEmpty(counter) ? 0f : M(counter, UiFont.Section);
            if (cw > 0)
                _p.Label(Loc(new UiRect(bar.X + bar.W - _t.Pad - cw, bar.Y, cw, bar.H)), counter, UiFont.Section, UiTone.Dim, null);
            string[] hs;
            if (header == null) header = "";
            if (!_hdrStr.TryGetValue(header, out hs)) { if (_hdrStr.Count >= MeasureCap) _hdrStr.Clear(); hs = new[] { "\u25bc " + header, "\u25b6 " + header }; _hdrStr[header] = hs; }
            _secFull = open ? hs[0] : hs[1];
            _secHit = ks[1];
            _secPending = true; _secBar = bar; _secKey = key; _secOpen = open; _secSummary = open ? null : summary;
            _secBtnRight = bar.X + bar.W - _t.Pad - (cw > 0 ? cw + _t.Gap : 0);
            _y += _t.SectionH + _t.Gap;
            _x = _left; _lineStart = _left;
            return open;
        }

        /// <summary>A small button on the right of the bar of the section just opened ("Stop all").</summary>
        public bool SectionButton(string id, string text, string tip = null, bool enabled = true)
        {
            if (!_secPending) return false;
            float w = M(text, UiFont.Small) + _t.BtnPad;
            float bh = Math.Min(_t.CtlH, _t.SectionH);   // T-178 C6: never lower than 15 px (lint); the bar is as high
            var r = new UiRect(_secBtnRight - w, _secBar.Y + (_secBar.H - bh) / 2f, w, bh);
            _secBtnRight -= w + _t.Gap;
            return Btn(r, id, text, tip, enabled, false, UiTone.Normal);
        }

        // ---------------------------------------------------------------- text

        public void Text(string text, UiTone tone = UiTone.Normal, bool wrap = false, int maxLines = 3, string tip = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!wrap)
            {
                float w = M(text, UiFont.Body);
                var slot = Place(w);
                string shown = Cut(text, UiFont.Body, slot.W);
                _p.Label(Loc(TextRect(slot)), shown, UiFont.Body, tone, FullTip(text, tip, shown != text));
                return;
            }
            if (_x > _lineStart + 0.01f) NextLine();
            bool tr;
            var lines = WrapText(text, UiFont.Body, _right - _lineStart, Math.Max(1, maxLines), out tr);
            float y0 = _y + (_t.RowH - _t.TableRowH) / 2f;
            for (int i = 0; i < lines.Count; i++)
                _p.Label(Loc(new UiRect(_lineStart, y0 + i * _t.TableRowH, _right - _lineStart, _t.TableRowH)), lines[i], UiFont.Body, tone, FullTip(text, tip, tr));
            _y += lines.Count * _t.TableRowH + (_t.RowH - _t.TableRowH);
            _x = _lineStart; _lineH = 0;
        }

        public void Unit(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var slot = Place(M(text, UiFont.Body));
            string shown = Cut(text, UiFont.Body, slot.W);
            _p.Label(Loc(TextRect(slot)), shown, UiFont.Body, UiTone.Dim, FullTip(text, null, shown != text));
        }

        public void Hint(string text, UiTone tone = UiTone.Dim)
        {
            if (string.IsNullOrEmpty(text)) return;
            Block();
            bool tr;
            var lines = WrapText(text, UiFont.Small, _right - _left, 3, out tr);
            for (int i = 0; i < lines.Count; i++)
                _p.Label(Loc(new UiRect(_left, _y + i * _t.TableRowH, _right - _left, _t.TableRowH)), lines[i], UiFont.Small, tone, FullTip(text, null, tr));
            _y += lines.Count * _t.TableRowH + 2f;
        }

        /// <summary>A hint line with permanently reserved room: always advances by `lines` rows, empty or not, so what follows
        /// (the action buttons) never moves when "Changed: ... press Apply" appears (as the settings window since 0.27.1).
        /// A longer text is cut on the last line with the full text in the tip.</summary>
        public void HintSlot(string text, UiTone tone = UiTone.Dim, int lines = 2)
        {
            Block();
            lines = Math.Max(1, lines);
            if (!string.IsNullOrEmpty(text))
            {
                bool tr;
                var ls = WrapText(text, UiFont.Small, _right - _left, lines, out tr);
                for (int i = 0; i < ls.Count; i++)
                    _p.Label(Loc(new UiRect(_left, _y + i * _t.TableRowH, _right - _left, _t.TableRowH)), ls[i], UiFont.Small, tone, FullTip(text, null, tr));
            }
            _y += lines * _t.TableRowH + 2f;
        }

        public void Separator()
        {
            Block();
            _p.Fill(Loc(new UiRect(_left, _y + _t.Gap / 2f, _right - _left, 1f)), UiColorRole.Separator);
            _y += _t.Gap + 1f;
        }

        // ---------------------------------------------------------------- controls

        public string Number(string id, string text, int chars = 4)
        {
            var slot = Place(_t.NumW * Math.Max(4, chars) / 4f);
            NoteHit(Sid(id), Loc(CtlRect(slot)));
            return _p.Field(Loc(CtlRect(slot)), Sid(id), text ?? "");
        }

        /// <summary>T-144: a key row: the spec field, "Record", "x" (clear) and a status text. Returns the field text; record / clear = pressed in this event.
        /// Ids: id + ".f" (field), id + ".rec", id + ".clr". While recording the "Record" button is selected.</summary>
        public string Hotkey(string id, string spec, string status, UiTone statusTone, bool recording, out bool record, out bool clear)
        {
            var slot = Place(Math.Min(_t.NumW * 4.5f, Math.Max(_t.NumW, _right - _lineStart)));
            string text = _p.Field(Loc(CtlRect(slot)), Sid(Cat(id, ".f")), spec ?? "");
            string rec = UiText.Get("settings.ui.record");
            var rs = Place(BtnWidth(rec, 0.5f * (_right - _lineStart)));
            string shown = Cut(rec, UiFont.Body, Math.Max(0f, rs.W - 2 * _t.BtnPad));
            record = Btn(CtlRect(rs), Cat(id, ".rec"), shown, FullTip(rec, UiText.Get("settings.ui.record.tip"), shown != rec), true, recording, UiTone.Normal);
            var cs = Place(_t.CtlH);
            clear = Btn(CtlRect(cs), Cat(id, ".clr"), "\u00d7", UiText.Get("settings.ui.clear"), true, false, UiTone.Normal);
            if (!string.IsNullOrEmpty(status)) Text(status, statusTone);
            return text;
        }

        public bool Check(string id, bool on, string text, string tip = null)
        {
            float full = M(text, UiFont.Body) + _t.CtlH + _t.Gap;
            var slot = Place(full);
            string shown = Cut(text, UiFont.Body, Math.Max(0f, slot.W - _t.CtlH - _t.Gap));
            NoteHit(Sid(id), Loc(CtlRect(slot)));
            return _p.Check(Loc(CtlRect(slot)), Sid(id), on, shown, FullTip(text, tip, shown != text));
        }

        public bool Button(string id, string text, string tip = null, bool enabled = true, UiTone tone = UiTone.Normal)
        {
            var slot = Place(BtnWidth(text, 0.6f * (_right - _lineStart)));
            string shown = Cut(text, UiFont.Body, Math.Max(0f, slot.W - 2 * _t.BtnPad));
            return Btn(CtlRect(slot), id, shown, FullTip(text, tip, shown != text), enabled, false, tone);
        }

        /// <summary>T-155 K2: a square glyph button (CtlH high, IconW wide; "+", "−", "…", "■", "▶"); wraps like a button. The tip says what it does.</summary>
        public bool IconButton(string id, string glyph, string tip, bool enabled = true, UiTone tone = UiTone.Normal)
        {
            var slot = Place(_t.IconW);
            var r = CtlRect(slot);
            r.W = slot.W;
            return Btn(r, id, glyph, tip, enabled, false, tone);
        }

        /// <summary>Segments; returns the new index. A click on a grey segment changes nothing and sets DisabledClick to its tip.</summary>
        public int Radio(string id, IList<string> labels, int selected, IList<string> tips = null, IList<bool> enabled = null)
        {
            int result = selected;
            float fitPad;
            float[] fit = RadioFit(labels, out fitPad);
            for (int i = 0; labels != null && i < labels.Count; i++)
            {
                bool en = enabled == null || i >= enabled.Count || enabled[i];
                string tip = tips != null && i < tips.Count ? tips[i] : null;
                var slot = Place(fit != null ? fit[i] : BtnWidth(labels[i], 0.6f * (_right - _lineStart)));
                string shown = Cut(labels[i], UiFont.Body, Math.Max(0f, slot.W - 2 * (fit != null ? fitPad : _t.BtnPad) + (fit != null ? 0.05f : 0f)));
                if (Btn(CtlRect(slot), IdxId(id, i), shown, FullTip(labels[i], tip, shown != labels[i]), en, i == selected && en, UiTone.Normal)) result = i;
            }
            return result;
        }

        /// <summary>T-223: a row of radio buttons that would wrap at its natural widths but fits in one line when the width is shared by the text
        /// (the minimum width dropped, then the side padding thinned down to a quarter at most): the widths, else null (the old wrap).</summary>
        private float[] RadioFit(IList<string> labels, out float fitPad)
        {
            fitPad = _t.BtnPad;
            if (labels == null || labels.Count < 2 || _x > _lineStart + 0.01f) return null;
            int n = labels.Count;
            float avail = _right - _lineStart - (n - 1) * _t.Gap, nat = 0, txt = 0;
            if (_fitTw.Length < n) { _fitTw = new float[n + 8]; _fitW = new float[n + 8]; }
            var tw = _fitTw;
            for (int i = 0; i < n; i++) { tw[i] = M(labels[i], UiFont.Body); nat += BtnWidth(labels[i], 0.6f * (_right - _lineStart)); txt += tw[i]; }
            if (nat <= avail) return null;
            float pad = (avail - txt) / (2f * n);
            if (pad < _t.BtnPad * 0.25f) return null;
            pad = Math.Min(pad, _t.BtnPad);
            fitPad = pad;
            var w = _fitW;
            for (int i = 0; i < n; i++) w[i] = tw[i] + 2 * pad;
            return w;
        }

        /// <summary>"&lt; value &gt;": -1 / 0 / +1.</summary>
        public int Cycle(string id, string value, string tip = null)
        {
            float bw = Math.Max(_t.CtlH, M("◀", UiFont.Body) + _t.BtnPad);
            float vw = Math.Max(M(value, UiFont.Body), 3 * _t.CtlH);
            float total = 2 * bw + vw + 2 * _t.Gap;
            if (total > _right - _x && _x > _lineStart + 0.01f) NextLine();
            vw = Math.Max(0f, Math.Min(vw, _right - _x - 2 * bw - 2 * _t.Gap));
            int r = 0;
            var s1 = Place(bw);
            if (Btn(CtlRect(s1), id + ".prev", "◀", tip, true, false, UiTone.Normal)) r = -1;
            var s2 = Place(vw);
            string shown = Cut(value, UiFont.Body, s2.W);
            _p.Label(Loc(TextRect(s2)), shown, UiFont.Body, UiTone.Normal, FullTip(value, tip, shown != value));
            var s3 = Place(bw);
            if (Btn(CtlRect(s3), id + ".next", "▶", tip, true, false, UiTone.Normal)) r = 1;
            return r;
        }

        /// <summary>Group buttons that wrap; returns the index pressed or -1.</summary>
        public int Chips(string id, IList<string> labels, int selected, IList<UiTone> tones = null, IList<string> tips = null)
        {
            int hit = -1;
            for (int i = 0; labels != null && i < labels.Count; i++)
            {
                var slot = Place(BtnWidth(labels[i], 0.4f * (_right - _lineStart)));
                string shown = Cut(labels[i], UiFont.Body, Math.Max(0f, slot.W - 2 * _t.BtnPad));
                UiTone tone = tones != null && i < tones.Count ? tones[i] : UiTone.Normal;
                if (Btn(CtlRect(slot), IdxId(id, i), shown, FullTip(labels[i], tips != null && i < tips.Count ? tips[i] : null, shown != labels[i]), true, i == selected, tone)) hit = i;
            }
            return hit;
        }

        /// <summary>A row of buttons pressed to the right, the last one is the main one; returns the id pressed or null.</summary>
        public string Actions(params UiAction[] buttons)
        {
            Block();
            if (buttons == null || buttons.Length == 0) return null;
            float avail = _right - _left;
            var widths = new float[buttons.Length];
            for (int i = 0; i < buttons.Length; i++) widths[i] = BtnWidth(buttons[i].Text, 0.6f * avail);
            string pressed = null;
            int start = 0;
            while (start < buttons.Length)
            {
                int end = start;
                float total = 0;
                while (end < buttons.Length && (end == start || total + _t.Gap + widths[end] <= avail + 0.01f))
                {
                    total += (end == start ? 0 : _t.Gap) + widths[end];
                    end++;
                }
                float x = _right - total;
                for (int i = start; i < end; i++)
                {
                    var a = buttons[i];
                    var slot = new UiRect(x, _y, Math.Min(widths[i], avail), _t.RowH);
                    string shown = Cut(a.Text, UiFont.Body, Math.Max(0f, slot.W - 2 * _t.BtnPad));
                    UiTone tone = a.Tone == UiTone.Normal && i == buttons.Length - 1 ? UiTone.Accent : a.Tone;
                    if (Btn(CtlRect(slot), a.Id, shown, FullTip(a.Text, a.Tip, shown != a.Text), a.Enabled, false, tone)) pressed = a.Id;
                    x += widths[i] + _t.Gap;
                }
                _y += _t.RowH;
                start = end;
            }
            return pressed;
        }

        // ---------------------------------------------------------------- table

        private void Overflow(string id, float need, float have)
        {
            string line = _win + " " + id + " need=" + (int)Math.Ceiling(need) + " have=" + (int)Math.Floor(have);
            if (_ovSeen.Add(id)) Overflows.Add(line);
            if (!_ovPass.ContainsKey(id)) _ovPass[id] = line;   // logged by End() only when it stays (OverflowPass)
        }

        /// <summary>Content fit of one column: the widest cell (and the header) clamped to [baseW, MaxEm * em]; baseW when the column does not fit.</summary>
        private float FitWidth(UiColumn col, int c, int nc, int rows, string[] all, float baseW, float em)
        {
            if (col.MaxEm <= 0) return baseW;
            float w = M(col.Header, UiFont.Small);
            for (int r = 0; r < rows; r++) { float tw = M(all[r * nc + c], UiFont.Body); if (tw > w) w = tw; }
            float cap = Math.Max(baseW, col.MaxEm * em);
            return Math.Min(cap, Math.Max(baseW, (float)Math.Ceiling(w)));
        }

        public void Table(UiTable spec, int rows, Func<int, int, string> cell, Func<int, UiTone> rowTone, ISet<int> picked, Func<int, int> uidOf, int maxRows, string emptyHint = null,
            Action<int> rowClick = null, string rowTip = null, Action<int, int> linkClick = null, Func<int, int, string> linkTip = null)
        {
            Block();
            if (rows <= 0) { Hint(string.IsNullOrEmpty(emptyHint) ? UiText.Get("ui.kit.empty") : emptyHint); return; }
            int nc = spec.Columns.Length;
            bool scroll = rows > maxRows;
            float sb = scroll ? _t.ScrollW : 0f;
            float em = _t.FontBody;
            float avail = _right - _left - sb;
            bool chk = spec.Checkboxes && picked != null;
            float chkW = chk ? _t.CtlH : 0f;
            // all cells first (cell() is called once per cell): the content-fit columns need the widest text before the layout
            var all = new string[rows * nc];
            for (int r = 0; r < rows; r++) for (int c = 0; c < nc; c++) all[r * nc + c] = cell(r, c) ?? "";
            var cw = new float[nc];
            var extra = new float[nc];   // content fit: width above the base that may be given back when the row is too narrow
            float fixedSum = 0; int flex = -1;
            for (int c = 0; c < nc; c++)
            {
                if (spec.Columns[c].Flex && flex < 0) { flex = c; continue; }
                float b = spec.Columns[c].Em * em;
                cw[c] = FitWidth(spec.Columns[c], c, nc, rows, all, b, em);
                extra[c] = cw[c] - b;
                fixedSum += cw[c];
            }
            float gaps = (nc - 1 + (chk ? 1 : 0)) * _t.Gap;
            float minFlex = flex >= 0 ? 4f * em : 0f;
            float flexWant = flex >= 0 ? FitWidth(spec.Columns[flex], flex, nc, rows, all, minFlex, em) : 0f;
            float room = avail - chkW - gaps;
            float deficit = fixedSum + flexWant - room;
            float want = _width + deficit;   // the body width at which every fitted column gets its full width
            if (want > WantWidth) WantWidth = want;
            float extraSum = flexWant - minFlex;
            for (int c = 0; c < nc; c++) extraSum += extra[c];
            if (deficit > 0 && extraSum > 0)
            {
                // too narrow for all fitted texts: the fitted columns give back their extra width in proportion (never below their base)
                float share = Math.Min(1f, deficit / extraSum);
                for (int c = 0; c < nc; c++) if (extra[c] > 0) { cw[c] -= extra[c] * share; }
                fixedSum = 0; for (int c = 0; c < nc; c++) if (c != flex) fixedSum += cw[c];
            }
            if (fixedSum + minFlex > room && fixedSum > 0)
            {
                float k = Math.Max(0.3f, (room - minFlex) / fixedSum);
                for (int c = 0; c < nc; c++) if (c != flex) cw[c] *= k;
                fixedSum = 0; for (int c = 0; c < nc; c++) if (c != flex) fixedSum += cw[c];
            }
            if (flex >= 0) cw[flex] = Math.Max(0f, room - fixedSum);
            // header
            float x = _left + (chk ? chkW + _t.Gap : 0f);
            for (int c = 0; c < nc; c++)
            {
                string h = spec.Columns[c].Header;
                if (!string.IsNullOrEmpty(h))
                {
                    string shown = Cut(h, UiFont.Small, cw[c]);
                    float hx = spec.Columns[c].Right ? x + Math.Max(0f, cw[c] - M(shown, UiFont.Small)) : x;
                    _p.Label(Loc(new UiRect(hx, _y, Math.Min(cw[c], Math.Max(M(shown, UiFont.Small), 1f)), _t.TableRowH)), shown, UiFont.Small, UiTone.Dim, shown == h ? null : h);
                }
                x += cw[c] + _t.Gap;
            }
            _y += _t.TableRowH;
            float viewY = _y;
            if (scroll)
            {
                var view = new UiRect(_left, _y, _right - _left, maxRows * _t.TableRowH);
                _p.BeginScroll(Sid(spec.Id), view, rows * _t.TableRowH);
                _ox = view.X; _oy = view.Y;
            }
            var texts = new string[nc];
            var linkX = new float[nc];
            var linkW = new float[nc];
            var isLink = new bool[nc];
            for (int r = 0; r < rows; r++)
            {
                float ry = viewY + r * _t.TableRowH;
                UiTone tone = rowTone == null ? UiTone.Normal : rowTone(r);
                float cx = _left;
                if (chk) cx += chkW + _t.Gap;
                // links need their rectangles before the row Hit is cut around them
                float cellX = cx;
                for (int c = 0; c < nc; c++)
                {
                    string text = all[r * nc + c];
                    texts[c] = text;
                    isLink[c] = false;
                    if (text.Length > 0 && text != "\u2014" && spec.Columns[c].Link && linkClick != null)
                    {
                        string shown = Cut(text, UiFont.Body, cw[c]);
                        float sw = Math.Max(1f, Math.Min(cw[c], M(shown, UiFont.Body)));
                        linkX[c] = spec.Columns[c].Right ? cellX + Math.Max(0f, cw[c] - sw) : cellX;
                        linkW[c] = sw;
                        isLink[c] = true;
                    }
                    cellX += cw[c] + _t.Gap;
                }
                if (rowClick != null)
                {
                    // a click on the row (not on its checkbox, not on a link) is reported by index; drawn first so the labels' own tooltips win
                    float hx = cx;
                    float end = _left + avail;
                    float from = hx;
                    int seg = 0;
                    for (int c = 0; c <= nc; c++)
                    {
                        if (c < nc && !isLink[c]) continue;
                        float to = c < nc ? linkX[c] : end;
                        if (to - from > 1f)
                        {
                            string hid = spec.Id + ".row." + r + (seg == 0 ? "" : "." + seg);
                            seg++;
                            if (_p.Hit(Loc(new UiRect(from, ry, to - from, _t.TableRowH)), Sid(hid), rowTip)) rowClick(r);
                        }
                        if (c < nc) from = Math.Max(from, linkX[c] + linkW[c]);
                    }
                }
                if (chk)
                {
                    int uid = uidOf != null ? uidOf(r) : r;
                    bool on = picked.Contains(uid);
                    bool now = _p.Check(Loc(new UiRect(_left, ry, chkW, _t.TableRowH)), Sid(spec.Id + ".chk." + uid), on, "", rowTip);   // T-178 C6: the tick has the tip of the row
                    if (now != on) { if (now) picked.Add(uid); else picked.Remove(uid); }
                }
                for (int c = 0; c < nc; c++)
                {
                    string text = texts[c];
                    if (text.Length > 0)
                    {
                        float tw = M(text, UiFont.Body);
                        string shown = Cut(text, UiFont.Body, cw[c]);
                        if (shown != text && c != flex) Overflow(spec.Id + "." + c, tw, cw[c]);
                        float sw = Math.Min(cw[c], M(shown, UiFont.Body));
                        float tx = spec.Columns[c].Right ? cx + Math.Max(0f, cw[c] - sw) : cx;
                        if (isLink[c])
                        {
                            string lt = linkTip == null ? null : linkTip(r, c);
                            if (_p.Link(Loc(new UiRect(linkX[c], ry, linkW[c], _t.TableRowH)), Sid(spec.Id + ".link." + r + "." + c), shown, UiFont.Body, tone, FullTip(text, lt, shown != text))) linkClick(r, c);
                        }
                        else _p.Label(Loc(new UiRect(tx, ry, Math.Max(sw, 1f), _t.TableRowH)), shown, UiFont.Body, tone, shown == text ? null : text);
                    }
                    cx += cw[c] + _t.Gap;
                }
            }
            if (scroll) { _p.EndScroll(); _ox = 0; _oy = 0; _y = viewY + maxRows * _t.TableRowH; }
            else _y = viewY + rows * _t.TableRowH;
            _y += _t.Gap;
        }
    }
}
