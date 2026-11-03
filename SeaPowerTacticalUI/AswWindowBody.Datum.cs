using System;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: the sub datum card opens the window in the "In area" mode with the rectangle filled in; the player picks the group and presses Start.
    internal sealed partial class AswWindowBody
    {
        private double[] _datumRect;

        /// <summary>Mode "area", draft source "rect", the 8 numbers lat/lon of the corners; applied by the next frame (the draft of the selected group is built there).</summary>
        internal void DraftArea(double[] rect)
        {
            if (rect == null || rect.Length < 8) return;
            _mode = "area"; WantInput = null;
            _datumRect = (double[])rect.Clone();
            if (_draft != null && _draft.Mode == "area") ApplyDatumRect();
        }

        private void ApplyDatumRect()
        {
            if (_datumRect == null || _draft == null || _draft.Mode != "area") return;
            _draft.AreaSource = "rect"; _draft.Rect = _datumRect; _draft.GeoDirty = true;
            _datumRect = null;
        }
    }
}
