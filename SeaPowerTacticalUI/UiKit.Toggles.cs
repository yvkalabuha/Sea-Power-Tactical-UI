// T-178 Kb1 (ADR-0077 §2.1): UiKit.Toggles: chips of a multi-choice (ToggleSet rules). A lit chip = the Selected fill + a 1 px Accent outline + "■"; a grey chip
// gives its tip (the reason) like Radio.
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class UiKit
    {
        /// <summary>A rectangular outline from four thin fills (Ring is a circle).</summary>
        private void Outline(UiRect r, UiColorRole c, float w)
        {
            _p.Fill(Loc(new UiRect(r.X, r.Y, r.W, w)), c);
            _p.Fill(Loc(new UiRect(r.X, r.Y + r.H - w, r.W, w)), c);
            _p.Fill(Loc(new UiRect(r.X, r.Y + w, w, r.H - 2 * w)), c);
            _p.Fill(Loc(new UiRect(r.X + r.W - w, r.Y + w, w, r.H - 2 * w)), c);
        }

        public const string ChipOn = "■ ";

        /// <summary>Draws the chips and writes the new state into `on`; returns true when it changed. Ids: id + "." + index.</summary>
        public bool Toggles(string id, IList<string> labels, bool[] on, IList<string> tips, IList<bool> enabled = null, int autoIndex = -1)
        {
            bool changed = false;
            for (int i = 0; labels != null && i < labels.Count; i++)
            {
                bool en = enabled == null || i >= enabled.Count || enabled[i];
                bool lit = on != null && i < on.Length && on[i];
                string tip = tips != null && i < tips.Count ? tips[i] : null;
                string text = lit ? ChipOn + labels[i] : labels[i];
                var slot = Place(BtnWidth(ChipOn + labels[i], 0.6f * (_right - _lineStart)));
                string shown = Cut(text, UiFont.Body, System.Math.Max(0f, slot.W - 2 * _t.BtnPad));
                var r = CtlRect(slot);
                bool hit = Btn(r, id + "." + i, shown, FullTip(text, tip, shown != text), en, lit && en, UiTone.Normal);
                if (lit && en) Outline(r, UiColorRole.Accent, 1f);
                if (hit && on != null)
                {
                    var now = ToggleSet.Click(on, i, autoIndex, enabled);
                    for (int j = 0; j < on.Length && j < now.Length; j++) if (on[j] != now[j]) { on[j] = now[j]; changed = true; }
                }
            }
            return changed;
        }
    }
}
