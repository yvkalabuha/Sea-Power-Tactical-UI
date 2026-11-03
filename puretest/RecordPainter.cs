// T-148 U1: the painter of the offline stands. Records every element UiKit draws, measures text with a conservative width table and "presses"
// buttons by id on a scenario (Click/Type/Hover are applied by the next NextFrame). Shared by puretest, capwintest and aswwintest.
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class UiRec
    {
        public string Kind, Id, Text, Tip;
        public UiRect R;
        public bool Enabled = true, Selected, Truncated, Hidden, Overlay;
        public UiColorRole Role; public float Width, X2, Y2;   // ring / line (Line: R.X, R.Y = start, X2, Y2 = end)
        public UiFont Font;
        public UiTone Tone;
        public override string ToString() { return Kind + " " + Id + " '" + Text + "' @" + (int)R.X + "," + (int)R.Y + " " + (int)R.W + "x" + (int)R.H; }
    }

    internal sealed class RecordPainter : IUiPainter
    {
        private readonly UiTheme _t;
        private readonly HashSet<string> _pendingClicks = new HashSet<string>(), _clicks = new HashSet<string>();
        private readonly Dictionary<string, string> _pendingTyped = new Dictionary<string, string>(), _typed = new Dictionary<string, string>();
        private readonly List<UiRect> _views = new List<UiRect>();
        private string _pendingHover, _hover;
        private float _ox, _oy;
        // T-178 K0: scenario of the mouse (ClickAt / DragTo / Release / Esc), applied by the next NextFrame
        private bool _pendingDown, _down, _downTaken, _pendingEsc, _esc, _overlayLayer;
        private float _pdx, _pdy, _dx, _dy;
        private string _holdId; private float _holdX, _holdY;
        private UiRect _blocked;

        public RecordPainter(double s) { _t = UiTheme.For(s); }

        public List<UiRec> Items { get; } = new List<UiRec>();
        public int ScrollViews { get; private set; }

        public void NextFrame()
        {
            Items.Clear();
            _views.Clear();
            _ox = 0; _oy = 0; ScrollViews = 0;
            _clicks.Clear(); foreach (string c in _pendingClicks) _clicks.Add(c);
            _pendingClicks.Clear();
            _typed.Clear(); foreach (var kv in _pendingTyped) _typed[kv.Key] = kv.Value;
            _pendingTyped.Clear();
            _hover = _pendingHover; _pendingHover = null;
            _down = _pendingDown; _dx = _pdx; _dy = _pdy; _pendingDown = false; _downTaken = false;
            _esc = _pendingEsc; _pendingEsc = false;
            _overlayLayer = false; _blocked = default(UiRect);
        }

        /// <summary>The next frame sees LMB pressed at (x, y) (window-local): the first control under the point takes it (as IMGUI), MouseDown reports it.</summary>
        public void ClickAt(float x, float y) { _pendingDown = true; _pdx = x; _pdy = y; }
        /// <summary>LMB is held and dragged to (x, y): Drag(id) returns true with these coordinates until Release.</summary>
        public void DragTo(string id, float x, float y) { _holdId = id; _holdX = x; _holdY = y; }
        public void Release() { _holdId = null; }
        public void Esc() { _pendingEsc = true; }
        /// <summary>The hit zones the kit handed over (UiHit.Inflate), by control id.</summary>
        public IReadOnlyDictionary<string, UiRect> HitRects { get; private set; }

        /// <summary>The pending ClickAt point as a click on this element (first come, as IMGUI); the overlay covers what lies under the open list.</summary>
        private bool PointClick(UiRect abs)
        {
            if (!_down || _downTaken) return false;
            if (_dx < abs.X || _dx > abs.X + abs.W || _dy < abs.Y || _dy > abs.Y + abs.H) return false;
            if (!_overlayLayer && _blocked.W > 0 && _dx >= _blocked.X && _dx <= _blocked.X + _blocked.W && _dy >= _blocked.Y && _dy <= _blocked.Y + _blocked.H) return false;
            _downTaken = true;
            return true;
        }

        public void Click(string id) { _pendingClicks.Add(id); }
        public void Type(string id, string text) { _pendingTyped[id] = text; }
        public void Hover(string id) { _pendingHover = id; }

        public UiRec Find(string id)
        {
            foreach (var r in Items) if (r.Id == id) return r;
            return null;
        }

        public List<UiRec> Of(string kind)
        {
            var l = new List<UiRec>();
            foreach (var r in Items) if (r.Kind == kind) l.Add(r);
            return l;
        }

        public UiRec FindText(string text)
        {
            foreach (var r in Items) if (r.Text == text) return r;
            return null;
        }

        // ---------------------------------------------------------------- IUiPainter

        public float Measure(string text, UiFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            int px = font == UiFont.Body ? _t.FontBody : font == UiFont.Small ? _t.FontSmall : font == UiFont.Title ? _t.FontTitle : _t.FontSection;
            double em = 0;
            foreach (char c in text)
            {
                if (c == ' ') em += 0.30;
                else if (char.IsDigit(c)) em += 0.56;
                else if (char.IsLetter(c)) em += char.IsUpper(c) ? 0.70 : 0.55;
                else if (c == '▼' || c == '▶' || c == '◀' || c == '…') em += 0.80;
                else em += 0.45;
            }
            double bold = font == UiFont.Title || font == UiFont.Section ? 1.08 : 1.0;
            return (float)(em * px * 1.10 * bold);
        }

        private UiRec Add(string kind, UiRect local, string id, string text, string tip)
        {
            var r = new UiRec { Kind = kind, Id = id, Text = text, Tip = tip, Overlay = _overlayLayer };
            r.R = new UiRect(local.X + _ox, local.Y + _oy, local.W, local.H);
            if (_views.Count > 0)
            {
                var v = _views[_views.Count - 1];
                r.Hidden = r.R.Y + r.R.H > v.Y + v.H + 0.5f || r.R.Y < v.Y - 0.5f;
            }
            r.Truncated = text != null && text.EndsWith("…");
            Items.Add(r);
            return r;
        }

        public void Label(UiRect r, string text, UiFont f, UiTone t, string tip)
        {
            var it = Add("Label", r, null, text, tip);
            it.Font = f; it.Tone = t;
        }

        public bool Button(UiRect r, string id, string text, string tip, bool enabled, bool selected, UiTone tone = UiTone.Normal)
        {
            var it = Add("Button", r, id, text, tip);
            it.Enabled = enabled; it.Selected = selected; it.Tone = tone;
            return enabled && !it.Hidden && (_clicks.Remove(id) || PointClick(it.R));
        }

        public bool DisabledPressed(string id)
        {
            foreach (var it in Items) if (it.Id == id && it.Kind == "Button" && !it.Enabled) return _clicks.Remove(id) || PointClick(it.R);
            return false;
        }

        public bool Hit(UiRect r, string id, string tip)
        {
            var it = Add("Hit", r, id, null, tip);
            return _clicks.Remove(id) || PointClick(it.R);
        }

        public bool Link(UiRect r, string id, string text, UiFont f, UiTone t, string tip)
        {
            var it = Add("Link", r, id, text, tip);
            it.Font = f; it.Tone = t;
            return !it.Hidden && (_clicks.Remove(id) || PointClick(it.R));
        }

        public bool Check(UiRect r, string id, bool on, string text, string tip)
        {
            var it = Add("Check", r, id, text, tip);
            it.Selected = on;
            return !it.Hidden && (_clicks.Remove(id) || PointClick(it.R)) ? !on : on;
        }

        public string Field(UiRect r, string id, string text)
        {
            Add("Field", r, id, text, null);
            string t;
            return _typed.TryGetValue(id, out t) ? t : text;
        }

        public void Fill(UiRect r, UiColorRole c) { Add("Fill", r, c.ToString(), null, null); }

        public void BeginScroll(string id, UiRect view, float contentH)
        {
            var abs = new UiRect(view.X + _ox, view.Y + _oy, view.W, view.H);
            Add("Scroll", view, id, null, null);
            _views.Add(abs);
            ScrollViews++;
            _ox = abs.X; _oy = abs.Y;
        }

        public void EndScroll()
        {
            if (_views.Count > 0) _views.RemoveAt(_views.Count - 1);
            _ox = 0; _oy = 0;
        }

        public void Ring(UiRect r, UiColorRole c, float width)
        {
            var it = Add("ring", r, null, null, null);
            it.Role = c; it.Width = width;
        }

        public void Line(float x1, float y1, float x2, float y2, UiColorRole c, float width)
        {
            var it = Add("line", new UiRect(x1, y1, Math.Abs(x2 - x1), Math.Abs(y2 - y1)), null, null, null);
            it.R = new UiRect(x1 + _ox, y1 + _oy, 0, 0); it.X2 = x2 + _ox; it.Y2 = y2 + _oy;
            it.Role = c; it.Width = width;
        }

        public bool Drag(UiRect r, string id, out float mx, out float my)
        {
            var it = Add("Drag", r, id, null, null);
            mx = my = 0;
            if (it.Hidden) return false;
            if (_holdId == id) { mx = _holdX; my = _holdY; return true; }
            if (PointClick(it.R)) { mx = _dx - _ox; my = _dy - _oy; return true; }
            return false;
        }

        public bool MouseDown(out float mx, out float my)
        {
            mx = _down ? _dx : 0; my = _down ? _dy : 0;
            return _down;
        }

        public bool EscapePressed() { return _esc; }

        public void Icon(UiRect r, string icon, UiTone t)
        {
            var it = Add("icon", r, null, icon, null);
            it.Tone = t;
        }

        public void SetHitRects(IReadOnlyDictionary<string, UiRect> hits) { HitRects = hits; }

        public void SetOverlay(UiRect blocked, bool layer)
        {
            if (layer) { _overlayLayer = true; _blocked = default(UiRect); }
            else { _blocked = blocked; }
        }

        public string Tooltip
        {
            get
            {
                if (_hover == null) return null;
                var it = Find(_hover);
                return it == null || string.IsNullOrEmpty(it.Tip) ? null : it.Tip;
            }
        }

        // ---------------------------------------------------------------- checks

        /// <summary>T-178 Kd1 (ADR-0077 §2.6, Проверка): the rules of the windows on the last frame. A list of errors, empty = clean.
        /// requireTips: every interactive control has a tip, a grey one has the reason; heights >= 15 px; hit zones (SetHitRects) do not overlap;
        /// a text field only with an id from allowedFields (the group name, the key record).</summary>
        public List<string> Lint(UiTheme t, bool requireTips, IList<string> allowedFields = null)
        {
            var bad = new List<string>();
            foreach (var it in Items)
            {
                if (it.Hidden || it.Overlay && it.Kind == "Fill") continue;
                bool interactive = it.Kind == "Button" || it.Kind == "Check" || it.Kind == "Link";
                if (interactive)
                {
                    if (requireTips && string.IsNullOrEmpty(it.Tip))
                        bad.Add((it.Kind == "Button" && !it.Enabled ? "grey without a reason tip: " : "no tip: ") + it.Id);
                    if (it.R.H + 0.01f < 15f && it.Id != null && !it.Id.StartsWith("frame.")) bad.Add("height " + it.R.H + " < 15: " + it.Id);
                }
                if (it.Kind == "Field")
                {
                    bool ok = false;
                    if (allowedFields != null) foreach (string a in allowedFields) if (a == it.Id) ok = true;
                    if (!ok) bad.Add("text field not allowed: " + it.Id);
                }
            }
            if (HitRects != null)
            {
                var ids = new List<string>(HitRects.Keys);
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                        if (HitRects[ids[i]].Overlaps(HitRects[ids[j]])) bad.Add("hit zones overlap: " + ids[i] + " x " + ids[j]);
            }
            return bad;
        }

        /// <summary>null = ok. Otherwise a text: element past the width, overlapping elements, truncated text without a tooltip, height.</summary>
        public string CheckLayout(float maxW, float maxH)
        {
            var bad = new StringBuilder();
            var vis = new List<UiRec>();
            foreach (var it in Items)
            {
                if (it.Hidden || it.Kind == "Fill" || it.Kind == "Hit" || it.Kind == "Scroll" || it.Kind == "Drag" || it.Kind == "ring" || it.Kind == "line" || it.Kind == "icon" || (it.Kind == "Label" && string.IsNullOrEmpty(it.Text))) continue;   // an empty label is only a tooltip area
                vis.Add(it);
            }
            foreach (var it in vis)
            {
                if (it.R.X < -0.01f || it.R.X + it.R.W > maxW + 0.01f) bad.Append("width: ").Append(it).Append(" (max ").Append((int)maxW).Append("); ");
                if (it.R.Y + it.R.H > maxH + 0.01f) bad.Append("height: ").Append(it).Append(" (max ").Append((int)maxH).Append("); ");
                if (it.Truncated && string.IsNullOrEmpty(it.Tip)) bad.Append("truncated without tip: ").Append(it).Append("; ");
            }
            for (int i = 0; i < vis.Count; i++)
                for (int j = i + 1; j < vis.Count; j++)
                    if (!vis[i].Overlay && !vis[j].Overlay && vis[i].R.Overlaps(vis[j].R)) bad.Append("overlap: ").Append(vis[i]).Append(" x ").Append(vis[j]).Append("; ");
            return bad.Length == 0 ? null : bad.ToString();
        }
    }
}
