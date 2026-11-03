// T-178 Ka5 (ADR-0077 §1): UiKit.Segments = Radio + the default mark; when the segments do not fit the row of the value column, the same choice is a Dropdown with the same id.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class UiKit
    {
        private readonly List<string> _segTips = new List<string>();   // T-239: scratch, consumed inside the call

        public int Segments(string id, IList<string> labels, int selected, IList<string> tips, IList<bool> enabled = null, int defaultIndex = -1)
        {
            int n = labels == null ? 0 : labels.Count;
            float need = 0;
            for (int i = 0; i < n; i++) need += BtnWidth(labels[i], float.MaxValue) + (i > 0 ? _t.Gap : 0f);
            float avail = _right - _lineStart;
            var tp = _segTips; tp.Clear();
            for (int i = 0; i < n; i++)
            {
                string t = tips != null && i < tips.Count ? tips[i] : null;
                if (i == defaultIndex) t = DefaultTip(t, labels[i]);
                tp.Add(t);
            }
            if (need > avail + 0.01f && n > 0)
                return Dropdown(id, labels, selected, selected >= 0 && selected < n ? tp[selected] : null, 1, enabled, tips, defaultIndex);
            return Radio(id, labels, selected, tp, enabled);
        }
    }
}
