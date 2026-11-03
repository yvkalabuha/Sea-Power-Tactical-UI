// T-178 Kc (ADR-0077 §2.4, §2.5): UiKit.StatusChips (the status line of a group as wrapping chips) and UiKit.Footer (the one bottom row of an order section).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal struct UiChip { public string Text, Glyph, Tip; public UiTone Tone; }
    internal sealed class UiFooter { public UiAction[] Aux; public UiAction? Stop; public UiAction Primary; public bool Changed; public string Reason; }

    internal sealed partial class UiKit
    {
        private readonly List<UiAction> _footActs = new List<UiAction>();
        private float[] _footW = new float[0];

        private static bool Important(UiChip c) { return c.Tone == UiTone.Warn || c.Tone == UiTone.Error; }

        private string ChipText(UiChip c) { return string.IsNullOrEmpty(c.Glyph) ? c.Text : c.Glyph + " " + c.Text; }

        /// <summary>Lays chips out in lines (a chip that does not fit goes to the next line; one wider than a line is cut with its full text in the tip). Over `maxLines` the
        /// last ordinary chips are replaced by "+N" (the full list in its tip); Warn/Error chips are never hidden (the line count may exceed maxLines for them).</summary>
        public void StatusChips(string id, IList<UiChip> chips, int maxLines = 2)
        {
            Block();
            int n = chips == null ? 0 : chips.Count;
            if (n == 0) return;
            float avail = _right - _left, chipH = _t.CtlH, padX = _t.BtnPad, gap = _t.Gap;
            var hidden = new List<int>();
            List<int>[] lines = null; UiChip more = default(UiChip); int hiddenCount = 0;
            for (;;)
            {
                var vis = new List<int>();
                for (int i = 0; i < n; i++) if (!hidden.Contains(i)) vis.Add(i);
                var ls = new List<List<int>>(); var cur = new List<int>(); float x = 0;
                Func<UiChip, float> wOf = c => Math.Min(avail, M(ChipText(c), UiFont.Small) + 2 * padX);
                var items = new List<UiChip>();
                foreach (int i in vis) items.Add(chips[i]);
                if (hidden.Count > 0) items.Add(more = new UiChip { Text = UiText.Fmt("ui.status.more", hidden.Count), Tone = UiTone.Dim });
                var order = new List<int>(vis); if (hidden.Count > 0) order.Add(-1);
                for (int j = 0; j < items.Count; j++)
                {
                    float w = wOf(items[j]);
                    if (cur.Count > 0 && x + gap + w > avail + 0.01f) { ls.Add(cur); cur = new List<int>(); x = 0; }
                    x += (cur.Count > 0 ? gap : 0) + w; cur.Add(order[j]);
                }
                if (cur.Count > 0) ls.Add(cur);
                if (ls.Count <= Math.Max(1, maxLines)) { lines = ls.ToArray(); break; }
                int drop = -1;
                for (int i = n - 1; i >= 0; i--) if (!hidden.Contains(i) && !Important(chips[i]) && i != 0) { drop = i; break; }
                if (drop < 0) { lines = ls.ToArray(); break; }
                hidden.Add(drop);
            }
            hiddenCount = hidden.Count;
            string all = null;
            if (hiddenCount > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++) { if (sb.Length > 0) sb.Append('\n'); sb.Append(ChipText(chips[i])); }
                all = sb.ToString();
            }
            for (int li = 0; li < lines.Length; li++)
            {
                float x = _left, y = _y + li * (chipH + 1f);
                foreach (int idx in lines[li])
                {
                    UiChip c = idx < 0 ? more : chips[idx];
                    string text = idx < 0 ? more.Text : ChipText(c);
                    float w = Math.Min(avail, M(text, UiFont.Small) + 2 * padX);
                    string shown = Cut(text, UiFont.Small, Math.Max(0f, w - 2 * padX));
                    var r = new UiRect(x, y, w, chipH);
                    _p.Fill(Loc(r), c.Tone == UiTone.Warn ? UiColorRole.Warn : c.Tone == UiTone.Error ? UiColorRole.Error : UiColorRole.SectionBar);
                    string tip = idx < 0 ? all : FullTip(text, c.Tip, shown != text);
                    UiTone tone = c.Tone == UiTone.Warn || c.Tone == UiTone.Error ? UiTone.Normal : c.Tone;
                    _p.Label(Loc(new UiRect(x + padX, y, Math.Max(0f, w - 2 * padX), chipH)), shown, UiFont.Small, tone, tip);
                    x += w + gap;
                }
            }
            _y += lines.Length * (chipH + 1f) + 1f;
        }

        /// <summary>One row pressed to the right: [aux] ... [Stop] gap&gt;=12 px x s [Primary]; "changed" on the left; a grey Primary gets its reason in a line below.
        /// Returns the pressed action id or null.</summary>
        public string Footer(UiFooter f)
        {
            Block();
            if (f == null) return null;
            float avail = _right - _left;
            var acts = _footActs; acts.Clear();   // T-239: scratch
            if (f.Aux != null) acts.AddRange(f.Aux);
            int stopAt = -1;
            if (f.Stop.HasValue) { stopAt = acts.Count; acts.Add(f.Stop.Value); }
            acts.Add(f.Primary);
            if (_footW.Length < acts.Count) _footW = new float[acts.Count + 4];
            var w = _footW;
            float big = Math.Max(12f * _t.S, _t.Gap);
            float total = 0;
            for (int i = 0; i < acts.Count; i++)
            {
                w[i] = BtnWidth(acts[i].Text, 0.6f * avail);
                total += w[i];
                if (i > 0) total += i == acts.Count - 1 && stopAt == i - 1 ? big : _t.Gap;
            }
            string changedText = f.Changed ? "● " + UiText.Get("ui.footer.changed") : null;
            float cw = changedText == null ? 0f : M(changedText, UiFont.Small);
            string pressed = null;
            int first = 0;
            if (total > avail + 0.01f && acts.Count > 1)
            {
                // too narrow: the aux buttons go to their own row above, Stop and Primary keep the last row
                int split = stopAt >= 0 ? stopAt : acts.Count - 1;
                if (split > 0) { pressed = FooterRow(acts, w, 0, split, _right, big, -1) ?? pressed; first = split; }
            }
            float rowTotal = 0;
            for (int i = first; i < acts.Count; i++) rowTotal += w[i] + (i > first ? (i == acts.Count - 1 && stopAt == i - 1 ? big : _t.Gap) : 0f);
            if (rowTotal > avail + 0.01f)
            {
                // even Stop + Primary do not fit: the buttons shrink in proportion (their texts are cut, the full text stays in the tip)
                float sum = 0; for (int i = first; i < acts.Count; i++) sum += w[i];
                float gaps = rowTotal - sum, k = Math.Max(0.2f, (avail - gaps) / sum);
                for (int i = first; i < acts.Count; i++) w[i] *= k;
                rowTotal = gaps + sum * k;
            }
            float startX = _right - rowTotal;
            if (changedText != null)
            {
                if (_left + cw + _t.Gap <= startX) _p.Label(Loc(new UiRect(_left, _y, cw, _t.RowH)), changedText, UiFont.Small, UiTone.Warn, UiText.Get("ui.footer.changed"));
                else { _p.Label(Loc(new UiRect(_left, _y, Math.Min(cw, avail), _t.RowH)), changedText, UiFont.Small, UiTone.Warn, UiText.Get("ui.footer.changed")); _y += _t.RowH; }
            }
            string p2 = FooterRow(acts, w, first, acts.Count, _right, big, stopAt, f.Reason);
            if (p2 != null) pressed = p2;
            if (!f.Primary.Enabled && !string.IsNullOrEmpty(f.Reason)) Hint(f.Reason, UiTone.Warn);
            return pressed;
        }

        private string FooterRow(List<UiAction> acts, float[] w, int from, int to, float right, float big, int stopAt, string reason = null)
        {
            float total = 0;
            for (int i = from; i < to; i++) total += w[i] + (i > from ? (i == acts.Count - 1 && stopAt == i - 1 ? big : _t.Gap) : 0f);
            float x = right - total;
            string pressed = null;
            for (int i = from; i < to; i++)
            {
                var a = acts[i];
                var slot = new UiRect(x, _y, w[i], _t.RowH);
                string shown = Cut(a.Text, UiFont.Body, Math.Max(0f, slot.W - 2 * _t.BtnPad));
                UiTone tone = i == acts.Count - 1 ? UiTone.Accent : i == stopAt ? UiTone.Error : a.Tone;
                string tip = a.Tip;
                if (i == acts.Count - 1 && !a.Enabled && string.IsNullOrEmpty(tip)) tip = reason;
                if (Btn(CtlRect(slot), a.Id, shown, FullTip(a.Text, tip, shown != a.Text), a.Enabled, false, tone)) pressed = a.Id;
                x += w[i] + (i + 1 < to ? (i + 1 == acts.Count - 1 && stopAt == i ? big : _t.Gap) : 0f);
            }
            _y += _t.RowH;
            return pressed;
        }
    }
}
