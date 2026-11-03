// T-178 Ka2 (ADR-0077 §2.3, ADR-0076 §7): UiKit.Dropdown. A button "value ▾" opens a list over the body (drawn by End(), after everything else, so it covers what lies
// under it; UiWindowFrame adds OverlayRect to the mouse block). One list per window (UiDropdownModel). A click outside the list and Esc close it; a grey item gives its tip
// (the reason) through DisabledClick. The pick is delivered by the NEXT Dropdown(id) call: the list is drawn and clicked in End(), after the caller got its result
// (one IMGUI pass later, invisible in the game; the stands run one more frame).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal struct UiItem
    {
        public string Text, Right, Icon, Tip;
        public bool Enabled, IsDefault;
        /// <summary>An enabled item (the default value of the bool field of a struct is "grey").</summary>
        public static UiItem Of(string text, string right = null, string icon = null, string tip = null, bool isDefault = false)
        {
            return new UiItem { Text = text, Right = right, Icon = icon, Tip = tip, Enabled = true, IsDefault = isDefault };
        }
    }

    internal sealed partial class UiKit
    {
        private readonly UiDropdownModel _dd = new UiDropdownModel();
        private readonly Dictionary<string, int> _ddPick = new Dictionary<string, int>();
        private List<UiItem> _ddItems = new List<UiItem>();
        private readonly List<UiItem> _ddScratch = new List<UiItem>(), _ddEmpty = new List<UiItem>();
        private int _ddSel;
        private bool _ddSeen;
        private readonly HashSet<string> _ddPickNew = new HashSet<string>();   // picks made in the last End(): delivered by the next pass or dropped

        /// <summary>The height the open list may use (the window body); 0 = unknown (the list opens below the button). UiWindowFrame sets it.</summary>
        public float ViewH { get; set; }

        /// <summary>The open list (window-local) or null. UiWindowFrame adds it to the rectangle of the mouse block and does not close the window on Esc while it is set.</summary>
        public UiRect? OverlayRect { get { return _dd.OpenId == null ? (UiRect?)null : _dd.ListRect; } }

        public int Dropdown(string id, IList<string> items, int selected, string tip, int columns = 1, IList<bool> enabled = null, IList<string> tips = null, int defaultIndex = -1)
        {
            var list = _ddScratch; list.Clear();   // T-239: scratch, copied only when the list opens
            for (int i = 0; items != null && i < items.Count; i++)
                list.Add(new UiItem
                {
                    Text = items[i], Enabled = enabled == null || i >= enabled.Count || enabled[i],
                    Tip = tips != null && i < tips.Count ? tips[i] : null, IsDefault = i == defaultIndex,
                });
            return DropdownCore(id, list, selected, tip, columns);
        }

        public int Dropdown(string id, IList<UiItem> items, int selected, string tip, int columns = 1)
        {
            return DropdownCore(id, items ?? _ddEmpty, selected, tip, columns);
        }

        private string RightText(UiItem it)
        {
            string mark = it.IsDefault ? UiText.Get("ui.default.mark") : null;
            if (string.IsNullOrEmpty(it.Right)) return mark;
            return string.IsNullOrEmpty(mark) ? it.Right : it.Right + " " + mark;
        }

        private float ItemWidth(UiItem it)
        {
            float w = M(it.Text, UiFont.Body) + 2 * _t.BtnPad;
            if (!string.IsNullOrEmpty(it.Icon)) w += _t.CtlH;
            string r = RightText(it);
            if (!string.IsNullOrEmpty(r)) w += M(r, UiFont.Small) + 2 * _t.Gap;
            return w;
        }

        private int DropdownCore(string id, IList<UiItem> items, int selected, string tip, int columns)
        {
            int result = selected, n = items.Count;
            int pk;
            if (_ddPick.TryGetValue(id, out pk)) { result = pk; _ddPick.Remove(id); }
            bool open = _dd.IsOpen(id);
            if (open) { _ddSeen = true; if (_p.EscapePressed()) { _dd.Close(); open = false; } }
            float widest = 0;
            for (int wi = 0; wi < n; wi++) widest = Math.Max(widest, ItemWidth(items[wi]));
            selected = result;   // the pick of the list that was just closed is shown at once
            string cur = selected >= 0 && selected < n ? items[selected].Text : (n == 0 ? "—" : "");
            string arrow = " ▾";
            float want = Math.Max(_t.BtnMin, Math.Max(M(cur, UiFont.Body), widest - 2 * _t.BtnPad) + M(arrow, UiFont.Body) + 2 * _t.BtnPad);
            var slot = Place(Math.Min(want, Math.Max(_t.BtnMin, _right - _lineStart)));
            string curArrow = Cat(cur, arrow);
            string shown = Cut(curArrow, UiFont.Body, Math.Max(0f, slot.W - 2 * _t.BtnPad));
            var r = CtlRect(slot);
            string btnTip = FullTip(cur, tip, shown != curArrow);
            if (Btn(r, id, shown, string.IsNullOrEmpty(btnTip) ? UiText.Get("ui.dd.open") : btnTip, n > 0, open, UiTone.Normal))
            {
                if (open) { _dd.Close(); }
                else
                {
                    _ddItems = new List<UiItem>(items); _ddSel = selected; _ddSeen = true;
                    float itemH = Math.Max(_t.CtlH, _t.TableRowH + 2f);
                    _dd.Open(id, r, n, Math.Max(1, columns), Math.Max(r.W, widest) / 1f, itemH, ViewH, _width);
                }
            }
            return result;
        }

        /// <summary>Begin(): the previous pass's list blocks what lies under it. Also forgets a list nobody asked for in the last pass (see EndOverlay).</summary>
        private void BeginOverlay()
        {
            _ddSeen = false;
            // while a list is open the whole window body is blocked for the controls: a click outside the list only closes it (it is not passed on to the control beneath)
            _p.SetOverlay(_dd.OpenId == null ? default(UiRect) : new UiRect(0, 0, Math.Max(_width, 1f), 100000f), false);
        }

        /// <summary>End(): draws the open list over the body and reads its clicks.</summary>
        private void EndOverlay()
        {
            _p.SetOverlay(default(UiRect), true);
            if (_ddPick.Count > 0)
            {
                // a pick nobody asked for in the pass after it (the window changed mode / closed) must not come back at the next show
                var stale = new List<string>();
                foreach (var k in _ddPick.Keys) if (!_ddPickNew.Contains(k)) stale.Add(k);
                foreach (var k in stale) _ddPick.Remove(k);
            }
            _ddPickNew.Clear();
            if (_dd.OpenId == null) return;
            if (!_ddSeen) { _dd.Close(); return; }   // the window no longer shows this drop-down (mode changed, group switched)
            string id = _dd.OpenId;
            var list = _dd.ListRect;
            _p.Fill(Loc(list), UiColorRole.SectionBar);
            int picked = -1;
            for (int i = 0; i < _ddItems.Count; i++)
            {
                var it = _ddItems[i];
                var ir = _dd.ItemRect(i);
                string itip = it.Tip;
                if (Btn(ir, id + ".i." + i, "", itip, it.Enabled, i == _ddSel && it.Enabled, UiTone.Normal)) picked = i;
                float x = ir.X + _t.BtnPad;
                UiTone tone = it.Enabled ? UiTone.Normal : UiTone.Dim;
                if (!string.IsNullOrEmpty(it.Icon))
                {
                    _p.Icon(Loc(new UiRect(x, ir.Y + 1, _t.CtlH - 2, ir.H - 2)), it.Icon, tone);
                    x += _t.CtlH;
                }
                string right = RightText(it);
                float rw = string.IsNullOrEmpty(right) ? 0f : Math.Min(M(right, UiFont.Small), Math.Max(0f, ir.W * 0.5f));
                float tw = Math.Max(0f, ir.X + ir.W - _t.BtnPad - x - (rw > 0 ? rw + _t.Gap : 0f));
                string txt = Cut(it.Text, UiFont.Body, tw);
                _p.Label(Loc(new UiRect(x, ir.Y, tw, ir.H)), txt, UiFont.Body, tone, txt == it.Text ? null : it.Text);
                if (rw > 0) _p.Label(Loc(new UiRect(ir.X + ir.W - _t.BtnPad - rw, ir.Y, rw, ir.H)), right, UiFont.Small, UiTone.Dim, null);
            }
            Outline(list, UiColorRole.Divider, 1f);
            if (picked >= 0)
            {
                _ddPick[id] = picked; _ddPickNew.Add(id);
                _dd.Close();
                return;
            }
            float mx, my;
            if (_p.MouseDown(out mx, out my) && !_dd.Contains(mx, my)) _dd.Close();   // also on the button itself: it is blocked like the rest, so it cannot toggle
        }
    }
}
