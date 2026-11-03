using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-144 / ADR-0050: the in-game settings window. The module owns the draft (it survives closing the window) and the hotkey; the rows come from ctx.Settings (every module declares its own).</summary>
    internal sealed class SettingsFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private string _hotkeySpec = "LeftAlt+Comma";
        private IUiSetting<string> _hotkeyCfg;
        private HotkeyBinding _hotkey;
        private IKeyInput _input;
        private SettingsDraft _draft;
        private SettingsWindowGui _win;

        public SettingsFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "settings"; } }
        public override string[] DependsOn { get { return new[] { "text" }; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Settings";
            _hotkeyCfg = cfg.Bind(S, "Hotkey", "LeftAlt+Comma", "Opens the settings window: Unity KeyCode names joined by + (e.g. LeftAlt+Comma); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            _hotkeySpec = _hotkeyCfg.Value;
            OnChanged(_hotkeyCfg, v => { _hotkeySpec = v; if (_hotkey != null) _hotkey.Respec(v); });
        }

        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            try
            {
                _draft = new SettingsDraft(ctx.Settings);
                _win = new SettingsWindowGui(ctx.Settings, _draft, ctx.Host, ctx.Hotkeys, new GameKeys(_api), () => _api.Frame.InMission, b => _api.SetMouseBlock("settings", b), m => ctx.Host.LogInfo("[Settings] " + m));
                ctx.Provide<ISettingsWindow>(_win);
                _input = new UnityKeyInput(_api);
                _hotkey = new HotkeyBinding("settings", "Settings.Hotkey", _hotkeySpec, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
            }
            catch (Exception ex)
            {
                ctx.Host.LogError("settings init crashed: " + ex);
                _win = null; _hotkey = null;
            }
        }

        public override void Update(UiFrame frame)
        {
            UiWindowPrefs.LoadingHidden = _api.LoadScreenActive();
            ApplyGameMenu(frame.RealTime);
            if (_win == null) return;
            bool inMission = _api.Frame.InMission;
            if (!inMission) { if (_win.Visible) _win.Visible = false; return; }   // the window lives only in a mission; the draft stays
            _win.Update();
            if (_hotkey == null || _hotkey.State == "off") return;
            _hotkey.Resolve(frame.RealTime, true, new GameKeys(_api));
            if (_hotkey.Pressed(_input)) _win.Visible = !_win.Visible;
        }

        /// <summary>The game's own menu (pause / options / confirm / briefing / After Action Report): no module draws (UiCore.GuiHidden), the window
        /// frames release their mouse block (UiWindowPrefs.MenuHidden), the game flag is lifted (UiMouseBlock.Suppressed); the map layers read GameApi.GameMenuHidden.</summary>
        private void ApplyGameMenu(double realNow)
        {
            string line = _api.PumpGameMenu(realNow);
            bool hide = _api.GameMenuHidden;
            UiWindowPrefs.MenuHidden = hide;
            UiCore.GuiHidden = hide;
            UiMouseBlock.Suppressed = hide;
            if (hide) UiEscGuard.ReleaseAll();   // T-199: a game menu is open: the Esc is the game's
            if (line != null) Ctx.Host.LogInfo(line);
        }

        public override void OnGUI()
        {
            if (_win != null) _win.OnGUI();
        }

        public override void Dispose()
        {
            if (_win != null) { try { _win.Dispose(); } catch { } }
            UiWindowPrefs.MenuHidden = false; UiCore.GuiHidden = false; UiMouseBlock.Suppressed = false;
        }
    }
}
