// T-178 U2 (ADR-0077 §8): the body of the "Undo" card (IUiToasts.Show key "undo:<window>", kind "undo", hideAfterS 10, reshow true):
// the title, the [Undo] button with "N s left", and after the press the result line. The button works once.
using System;

namespace VoiceBotBridge
{
    internal static class UiUndoCard
    {
        public const string ButtonId = "undo.btn";

        /// <summary>leftS = seconds left; onUndo runs the restore (once); result = the line to show after it ("Restored 2 of 3; ..."), null before.</summary>
        public static Action<UiKit> Body(string title, Func<double> leftS, Action onUndo, Func<string> result)
        {
            bool pressed = false;
            return k =>
            {
                k.Text(title, UiTone.Normal, true, 2);
                string res = result == null ? null : result();
                if (!pressed && res == null)
                {
                    k.Break();
                    if (k.Button(ButtonId, UiText.Get("ui.undo.btn"), UiText.Get("ui.undo.tip"), true, UiTone.Accent) && !pressed)
                    {
                        pressed = true;
                        if (onUndo != null) onUndo();
                        res = result == null ? null : result();
                    }
                    if (!pressed)
                    {
                        double left = leftS == null ? 0 : leftS();
                        k.Text(UiText.Fmt("ui.undo.left", (int)Math.Ceiling(Math.Max(0, left))), UiTone.Dim);
                    }
                }
                if (res != null) { k.Break(); k.Text(res, UiTone.Normal, true, 3); }
            };
        }
    }

    /// <summary>T-199 B (ADR-0084): the undo strip inside a window body (instead of a card of the shared stack): the title, [Undo] with "N s left" before
    /// the press, the result line for ResultS seconds after it. One strip per window; a new Show replaces the previous one.</summary>
    internal sealed class UiUndoBanner
    {
        public const double ResultS = 6;
        private Action<UiKit> _body;
        private Func<double> _leftS;
        private Func<string> _result;
        private double _resultAt = -1;
        private bool _has;

        public void Show(string title, Func<double> leftS, Action onUndo, Func<string> result, double now)
        {
            _leftS = leftS; _result = result; _resultAt = -1; _has = true;
            _body = UiUndoCard.Body(title, leftS, onUndo, result);
        }

        public void Hide() { _has = false; _body = null; _resultAt = -1; }

        public bool Visible(double now)
        {
            if (!_has) return false;
            string res = null;
            try { res = _result == null ? null : _result(); } catch { }
            if (res != null)
            {
                if (_resultAt < 0) _resultAt = now;
                return now - _resultAt < ResultS;
            }
            double left = 0;
            try { left = _leftS == null ? 0 : _leftS(); } catch { }
            return left > 0;
        }

        public void Draw(UiKit k, double now)
        {
            if (!Visible(now) || _body == null) return;
            _body(k);
            Visible(now);   // the press inside the body made the result: the 6 s start here
        }
    }
}
