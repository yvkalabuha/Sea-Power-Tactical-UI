// T-144 / ADR-0050: the Unity shell of the settings window. The frame (title, drag, collapse, width, hint line, mouse block) is UiWindowFrame, the whole body is
// SettingsWindowBody (pure, UiKit only), the draft is SettingsDraft (pure). This class wires the game: key recording through Event.current, the hotkey check,
// the title mark of an unapplied draft. Texts are UiText keys (settings.ui.*); no other module's section or key is named here.
using System;
using System.Collections.Generic;
using UE = UnityEngine;

namespace VoiceBotBridge.Ui
{
    internal sealed class SettingsWindowGui : ISettingsWindow
    {
        private const int WindowId = 0x56420097;
        private readonly UiSettingsRegistry _reg;
        private readonly SettingsDraft _draft;
        private readonly IUiHost _host;
        private readonly HotkeyRegistry _hotkeys;
        private readonly IHotkeyGame _game;
        private readonly Func<bool> _inMission;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        private readonly SettingsView _view;
        private UiWindowFrame _frame;
        private bool _visible, _errLogged;
        private int _titleRev = -1;
        private bool _titleDirty;
        private double _nextKeyCheck;
        private Func<string> _titleFn;   // T-239: one delegate, not a new closure per OnGUI
        // T-239: the cost of the window body + footer per IMGUI pass, logged every 30 s while the window is open
        private const double PerfLogEveryS = 30.0;
        private double _nextPerfLog;
        private int _perfPasses; private long _perfTicks, _perfMaxTicks, _perfBytes, _curTicks, _curBytes0; private bool _curOpen;
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        private static readonly string[] Mods = { "LeftAlt", "RightAlt", "LeftControl", "RightControl", "LeftShift", "RightShift" };

        public SettingsWindowGui(UiSettingsRegistry reg, SettingsDraft draft, IUiHost host, HotkeyRegistry hotkeys, IHotkeyGame game, Func<bool> inMission, Action<bool> block, Action<string> log)
        {
            _reg = reg; _draft = draft; _host = host; _hotkeys = hotkeys; _game = game; _inMission = inMission ?? (() => true);
            _block = block ?? (b => { }); _log = log ?? (m => { });
            _view = new SettingsView { Reg = reg, Draft = draft, Rec = new HotkeyRecorder(), Keys = new List<HotkeyDraftResult>() };
        }

        private static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }

        public bool Visible
        {
            get { return _visible; }
            set
            {
                _draft.Prune();   // T-239: Changed / ChangedCount no longer prune on their own
                if (_visible && !value && _draft.ChangedCount > 0) _log("settings: closed with " + _draft.ChangedCount + " unapplied: " + string.Join(",", _draft.ChangedKeys().ToArray()));
                _visible = value; if (_frame != null) _frame.Visible = value; if (!value) Release();
            }
        }

        /// <summary>An unapplied draft exists (the title carries the mark).</summary>
        public bool Dirty { get { _draft.Prune(); return _draft.ChangedCount > 0; } }

        private void EnsureFrame()
        {
            if (_frame != null) return;
            _titleFn = Title;
            _frame = new UiWindowFrame(WindowId, "settings", Title(), 560f, 480f, 760f, _block, _log);
            _frame.Visible = _visible;
            _titleRev = UiText.Revision;
            _titleDirty = Dirty;
        }

        private string Title() { return Tx("settings.ui.title") + (_draft.ChangedCount > 0 ? " •" : ""); }

        private void RefreshTitle()
        {
            bool d = Dirty;
            if (_titleRev == UiText.Revision && _titleDirty == d) return;
            _titleRev = UiText.Revision; _titleDirty = d;
            _frame.Title = Title();
        }

        private void Release()
        {
            StopRecording();
            if (_frame != null) _frame.Release();
        }

        public void Dispose() { Release(); }

        private void StopRecording() { if (_hotkeys != null) _hotkeys.Suspended = false; UiEscGuard.Set("settings.rec", false, UE.Time.frameCount); }   // T-199

        public void Update()
        {
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) Visible = false;
                _frame.Visible = _visible;
                if (!_visible) { Release(); _nextPerfLog = 0; return; }
                if (!_inMission()) { Visible = false; return; }
                _frame.Update();
                double now = UE.Time.unscaledTime;
                if (_view.Rec.Active)
                {
                    bool cancelled;
                    _view.Rec.Feed(null, HeldMods(), now, out cancelled);   // the 5 s timeout
                    if (!_view.Rec.Active) StopRecording();
                }
                if (now >= _nextKeyCheck) { _nextKeyCheck = now + 0.4; _draft.InvalidateChecks(); _view.Keys = _draft.CheckHotkeys(_game, true); }
                LogPerf(now);
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("settings window update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static IList<string> HeldMods()
        {
            var l = new List<string>();
            foreach (var m in Mods) if (UE.Input.GetKey((UE.KeyCode)Enum.Parse(typeof(UE.KeyCode), m))) l.Add(m);
            return l;
        }

        private UiSettingEntry Find(string fullKey) { return _reg.Find(fullKey); }

        /// <summary>T-239: "settings perf passes=N ms_avg=x ms_max=y alloc_b_avg=z" every 30 s (z = -1 when the runtime does not count bytes).</summary>
        private void LogPerf(double now)
        {
            if (_nextPerfLog == 0) { _nextPerfLog = now + PerfLogEveryS; return; }
            if (now < _nextPerfLog) return;
            _nextPerfLog = now + PerfLogEveryS;
            if (_perfPasses == 0) return;
            double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long bytes = _perfBytes > 0 ? _perfBytes / _perfPasses : -1;
            _log("settings perf passes=" + _perfPasses + " ms_avg=" + (_perfTicks * msPerTick / _perfPasses).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " ms_max=" + (_perfMaxTicks * msPerTick).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " alloc_b_avg=" + bytes);
            _perfPasses = 0; _perfTicks = 0; _perfMaxTicks = 0; _perfBytes = 0;
        }

        private static long AllocBytes()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); } catch (Exception) { return 0; }
        }

        private void PerfBegin()
        {
            _curTicks = 0; _curBytes0 = AllocBytes(); _curOpen = true;
            _sw.Reset(); _sw.Start();
        }

        private void PerfEnd(bool finish)
        {
            if (!_curOpen) return;
            _sw.Stop();
            _curTicks += _sw.ElapsedTicks;
            if (!finish) return;
            _curOpen = false;
            _perfPasses++; _perfTicks += _curTicks; if (_curTicks > _perfMaxTicks) _perfMaxTicks = _curTicks;
            long b = AllocBytes() - _curBytes0;
            if (b > 0) _perfBytes += b;
        }

        private IList<string> _warnCache; private int _warnCount = -1, _warnRev = -1;

        /// <summary>T-195: the warning list is rebuilt only when the number of draft changes or the text revision changes (was: every OnGUI).</summary>
        private IList<string> CachedWarnings()
        {
            int n = _view.Draft.ChangedCount, rev = UiText.Revision;
            if (n != _warnCount || rev != _warnRev) { _warnCache = SettingsWindowBody.Warnings(_view); _warnCount = n; _warnRev = rev; }
            return _warnCache;
        }

        public void OnGUI()
        {
            if (!_visible) return;
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) { Visible = false; return; }
                SettingsWindowBody.BeginPass(_view);   // T-239: once per IMGUI event, before the window function runs body and footer
                RefreshTitle();
                _frame.Visible = _visible;
                if (_view.Rec.Active) FeedKeyEvent();
                // Live bug 0.27.6 (Apply did nothing, no log): GUI.Window defers the window function until the owner's OnGUI has returned (GUI.EndWindows),
                // so an action captured into a local and read after _frame.OnGUI was always null. The action is handled inside the body callback.
                _frame.OnGUI(DrawBody, _titleFn, CachedWarnings(), DrawFooter);
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("settings window OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>The body callback: runs inside the window function (after OnGUI returned), draws and handles the action in the same event.</summary>
        private void DrawBody(UiKit k)
        {
            PerfBegin();
            string action = SettingsWindowBody.Draw(k, _view);
            PerfEnd(false);
            if (action == null) return;
            try { Handle(action); }
            catch (Exception e) { _log("settings: action " + action + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>T-190: the sticky footer (Revert / Apply) outside the scrolling body; its action is handled inside the window function too.</summary>
        private void DrawFooter(UiKit k)
        {
            if (_curOpen) { _sw.Reset(); _sw.Start(); }
            string action = SettingsWindowBody.DrawFooter(k, _view);
            PerfEnd(true);
            if (action == null) return;
            try { Handle(action); }
            catch (Exception e) { _log("settings: action " + action + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Recording: the first key-down event of a non-modifier key ends it; Escape cancels. The event is consumed so the game does not see it.</summary>
        private void FeedKeyEvent()
        {
            var ev = UE.Event.current;
            if (ev == null || ev.type != UE.EventType.KeyDown || ev.keyCode == UE.KeyCode.None) return;
            bool cancelled;
            string spec = _view.Rec.Feed(ev.keyCode.ToString(), HeldMods(), UE.Time.unscaledTime, out cancelled);
            ev.Use();
            if (spec != null)
            {
                var e = Find(_view.Rec.RowId);
                if (e != null) _draft.SetText(e, spec);
            }
            if (!_view.Rec.Active) StopRecording();
        }

        private void Handle(string action)
        {
            if (action == "apply") Apply();
            else if (action == "apply.refused") _log("settings: apply refused: " + (SettingsWindowBody.RefuseReason(_view) ?? "disabled"));
            else if (action == "revert") _view.Status = null;   // Draw already reverted the draft; "adv" and "reset.*" are fully done by Draw too
            else if (action.StartsWith("rec.", StringComparison.Ordinal))
            {
                _view.Rec.Start(action.Substring(4), UE.Time.unscaledTime);
                if (_hotkeys != null) _hotkeys.Suspended = true;   // the mod's own hotkeys stay silent while a key is recorded
                UiEscGuard.Set("settings.rec", true, UE.Time.frameCount);   // T-199: Esc cancels the recording, not the game
            }
            _nextKeyCheck = 0;
        }

        private void Apply()
        {
            var rep = SettingsWindowBody.Apply(_view, _host, _log);
            if (rep == null) return;
            if (_frame != null) _frame.Say(_view.Status, rep.Restart > 0 || rep.Failed.Count > 0 ? UiTone.Warn : UiTone.Ok);
            foreach (var e in _reg.All)
                if (e.Control == UiControl.Hotkey && rep.Keys.Contains(e.FullKey)) { _hotkeys?.ResetAll(); break; }
        }
    }
}
