// T-175 (ADR-0075 item 7): the shared stack of non-modal cards. The model (UiToastStackModel.cs) is pure; this is the IMGUI part on the shared window frame:
// id "toast", width 360 px x scale (min 320), dragged by the title, the place is kept in [Ui] WindowState, hidden under the game menu and the after-action
// report like the other windows (UiWindowPrefs.Hidden). Generic: the cards bring their own bodies. Unity-only.
using System;
using UE = UnityEngine;

namespace VoiceBotBridge
{
    internal sealed class UiToastStack
    {
        private const int WindowId = 0x564200A2;
        private readonly UiToastStackModel _m;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        internal static readonly string[] AvoidSkip = { "toast", "asw_dialog", "smart_attack" };   // T-233: temporary zones and the stack itself are not obstacles
        private UiWindowFrame _frame;
        private int _titleRev = -1;
        private bool _errLogged;

        public UiToastStack(UiToastStackModel m, Action<bool> block, Action<string> log)
        {
            _m = m; _block = block ?? (b => { }); _log = log ?? (s => { });
        }

        private void EnsureFrame()
        {
            if (_frame != null) return;
            _frame = new UiWindowFrame(WindowId, "toast", UiText.Get("toast.title"), 360f, 320f, 520f, _block, _log);
            _titleRev = UiText.Revision;
            _frame.AvoidOnShow = () =>
            {
                var l = new System.Collections.Generic.List<double[]>();
                foreach (var kv in ModUiRects.Named()) if (Array.IndexOf(AvoidSkip, kv.Key) < 0) l.Add(kv.Value);
                foreach (var pv in ModUiRects.Passive().Values) l.Add(pv);   // T-240: the radar legend is no window but must stay readable
                return l;
            };
        }

        /// <summary>Per frame (before OnGUI): expiry, the frame's mouse block and ModUiRects.</summary>
        public void Update()
        {
            try
            {
                _m.Tick(UE.Time.unscaledTime);
                EnsureFrame();
                _frame.Visible = _m.Any;
                if (!_m.Any) { _frame.Release(); return; }
                _frame.Update();
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("toast update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        public void OnGUI()
        {
            if (!_m.Any) return;
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) { _m.CloseAll(); return; }   // [x] of the frame closes the visible cards
                if (_titleRev != UiText.Revision) { _titleRev = UiText.Revision; _frame.Title = UiText.Get("toast.title"); }
                _frame.Visible = true;
                _frame.OnGUI(Draw, () => UiText.Fmt("toast.summary", _m.Visible().Count + _m.Folded), null);
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("toast OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void Draw(UiKit k)
        {
            UiToastCards.Draw(k, _m, _log);   // T-291: each body under its own id scope
        }

        public void Release()
        {
            if (_frame != null) _frame.Release();
        }
    }
}
