using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>The mod's own texts (T-103): language setting, the game's getText binding and the language poll. Config: Ui.Language.</summary>
    internal sealed class TextFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private IUiSetting<string> _uiLanguage, _uiWindowState;
        private bool _textBound;
        private double _nextTextBind;
        private int _langRev;
        private bool _prevInMission, _prevWinOpen;

        public TextFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "text"; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(TextSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            string dll = Ctx.Host.PluginDir;
            string langDir = dll == null ? null : System.IO.Path.Combine(dll, "lang");
            _uiLanguage = cfg.Bind("Ui", "Language", "auto", "auto | <language code>: language of the mod's own texts (auto = the game's language; any lang/language_xx folder works; fallback en)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Section = "general", Options = UiText.Languages(langDir) });
            UiText.Configure(langDir, _uiLanguage.Value);
            // T-148: shared look of the mod windows: scale, remembered positions / widths / collapsed sections
            var scale = cfg.Bind("Ui", "Scale", 0.0, "Scale of the mod windows (0 = auto: game UI scale x screen height / 1080)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Section = "general", Min = 0.85, Max = 2, ZeroIsAuto = true, Step = 0.05 });
            // T-155: compact (default) | normal layout metrics; the window frames rebuild the theme when it changes
            var density = cfg.Bind("Ui", "Density", "compact", "compact | normal: density of the mod windows (compact = dense rows and paddings)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Section = "general", Options = new[] { "compact", "normal" } });
            _uiWindowState = cfg.Bind("Ui", "WindowState", "", "Positions, widths and collapsed sections of the mod windows (written by the mod)", UiKeyClass.Internal);
            UiWindowPrefs.ScaleOverride = scale.Value;
            UiWindowPrefs.Density = ParseDensity(density.Value);
            // L1: the window frames compare the scale every frame (EnsureTheme) and rebuild the theme/kit by themselves
            OnChanged(_uiLanguage, v => UiText.SetLanguage(v));
            OnChanged(scale, v => UiWindowPrefs.ScaleOverride = v);
            OnChanged(density, v => UiWindowPrefs.Density = ParseDensity(v));
            UiWindowPrefs.GameUiScale = () => _api.UiScale();
            UiWindowPrefs.State = new UiWindowStateStore(_uiWindowState.Value, text => { _uiWindowState.Value = text; });
        }

        private static UiDensity ParseDensity(string v)
        {
            return string.Equals((v ?? "").Trim(), "normal", StringComparison.OrdinalIgnoreCase) ? UiDensity.Normal : UiDensity.Compact;
        }

        private int _slotBind, _slotLang;

        public override void Init(UiContext ctx)
        {
            int owner = ctx.Guard.OwnerOf(Id);
            _slotBind = ctx.Guard.Register("text.bind", owner);
            _slotLang = ctx.Guard.Register("text.lang", owner);
        }

        public override void Update(UiFrame frame)
        {
            try { BindGameText(frame.RealTime); } catch (Exception e) { Ctx.Guard.Fail(_slotBind, e); }
            try { PollLanguage(); } catch (Exception e) { Ctx.Guard.Fail(_slotLang, e); }
        }

        // the game's language table is a singleton that appears a bit after the plugin: bind its getText lazily, once
        private void BindGameText(double now)
        {
            if (_textBound || now < _nextTextBind) return;
            _nextTextBind = now + 1f;
            _textBound = _api.BindUiText();
        }

        // T-103: the game's LanguageChanged event does the work (UiCommands.BindUiText). Without the event: UiText.Poll() only at cheap
        // moments (mission load / return to the main menu / a mod window opens) - never on a timer (customer decision).
        private void PollLanguage()
        {
            if (_langRev != UiText.Revision)
            {
                _langRev = UiText.Revision;
                Ctx.Host.LogInfo("ui language -> " + UiText.Lang);
            }
            if (!_textBound || _api.LanguageEventBound) return;
            bool inMission = _api.Frame.InMission;
            var asw = Ctx.Get<IAswWindow>();
            var cap = Ctx.Get<ICapWindow>();
            bool win = (asw != null && asw.Visible) || (cap != null && cap.Visible);
            bool moment = inMission != _prevInMission || (win && !_prevWinOpen);
            _prevInMission = inMission; _prevWinOpen = win;
            if (moment) UiText.Poll();
        }

        public override void Dispose()
        {
            try { _api.UnbindUiText(); } catch { }
        }
    }
}
