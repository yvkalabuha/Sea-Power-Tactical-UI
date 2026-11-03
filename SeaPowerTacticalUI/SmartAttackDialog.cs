using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// IMGUI dialog of the smart attack (T-102, ADR-0038 sections 7, 10): shows controller.Pending ("Attack? Yes/No" with 30 s,
    /// "Continue / Cancel" with 20 s) and passes the button to controller.Answer. Own screen rectangle under ModUiRects "smart_attack",
    /// the game's mouse handling is blocked only while the cursor is over it. Never throws.
    /// </summary>
    internal sealed class SmartAttackDialog
    {
        private const int Width = 520, Height = 120, Top = 170;
        private readonly Func<SmartAttackController> _ctl;
        private readonly Func<double> _now;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        private UnityEngine.GUIStyle _label, _win, _warn;
        private bool _blockOn, _errLogged;

        public SmartAttackDialog(Func<SmartAttackController> ctl, Func<double> now, Action<bool> block, Action<string> log)
        {
            _ctl = ctl; _now = now; _block = block ?? (b => { }); _log = log ?? (m => { });
        }

        private static UnityEngine.Rect RectNow()
        {
            return new UnityEngine.Rect(UnityEngine.Screen.width / 2f - Width / 2f, Top, Width, Height);
        }

        private void Release()
        {
            if (_blockOn) { try { _block(false); } catch { } _blockOn = false; }
            ModUiRects.Clear("smart_attack");
        }

        public void Dispose() { Release(); }

        /// <summary>Per frame (Update): the dead zone for the radial menu and the mouse block over the dialog.</summary>
        public void Update()
        {
            try
            {
                var c = _ctl();
                var q = c == null ? null : c.Pending;
                if (q == null) { Release(); return; }
                var r = RectNow();
                ModUiRects.Set("smart_attack", r.x, r.y, r.width, r.height);
                double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
                bool inside = mx >= r.x && mx < r.x + r.width && my >= r.y && my < r.y + r.height;
                bool down = UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2);
                if (inside && !_blockOn) { if (!down) { _block(true); _blockOn = true; } }
                else if (!inside && _blockOn) { _block(false); _blockOn = false; }
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("smart attack dialog update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            var dark = new UnityEngine.Texture2D(1, 1);
            dark.SetPixel(0, 0, new UnityEngine.Color(0.06f, 0.08f, 0.10f, 0.94f)); dark.Apply();
            _win = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.window);
            _win.normal.background = dark; _win.onNormal.background = dark; _win.focused.background = dark; _win.onFocused.background = dark;
            _win.padding = new UnityEngine.RectOffset(0, 0, 0, 0);
            _label = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label) { fontSize = 13, wordWrap = true };
            _label.normal.textColor = new UnityEngine.Color(0.85f, 0.9f, 0.95f);
            _warn = new UnityEngine.GUIStyle(_label);
            _warn.normal.textColor = new UnityEngine.Color(1f, 0.75f, 0.3f);
        }

        public static string OptionText(string option)
        {
            switch (option)
            {
                case "yes": case "no": case "continue": case "cancel": return UiText.Get("smart.dlg." + option);
                default: return option;
            }
        }

        public void OnGUI()
        {
            var c = _ctl();
            var q = c == null ? null : c.Pending;
            if (q == null) return;
            try
            {
                EnsureStyles();
                int oldDepth = UnityEngine.GUI.depth;
                UnityEngine.GUI.depth = -1002;
                var r = RectNow();
                UnityEngine.GUI.Box(r, "", _win);
                double left = Math.Max(0, q.DeadlineT - _now());
                UnityEngine.GUI.Label(new UnityEngine.Rect(r.x + 12, r.y + 8, r.width - 24, 52), q.Text ?? "", _label);
                UnityEngine.GUI.Label(new UnityEngine.Rect(r.x + 12, r.y + 60, r.width - 24, 20), UiText.Fmt("smart.dlg.left", Math.Ceiling(left)), _warn);
                int n = q.Options == null ? 0 : q.Options.Length;
                float bw = 130f, gap = 14f, x0 = r.x + (r.width - (n * bw + (n - 1) * gap)) / 2f;
                for (int i = 0; i < n; i++)
                    if (UnityEngine.GUI.Button(new UnityEngine.Rect(x0 + i * (bw + gap), r.y + 84, bw, 28), OptionText(q.Options[i])))
                    {
                        c.Answer(q.Id, q.Options[i]);
                        break;
                    }
                UnityEngine.GUI.depth = oldDepth;
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("smart attack dialog draw failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }
}
