namespace VoiceBotBridge.Ui
{
    /// <summary>What the Harmony module needs from the Menu section.</summary>
    internal sealed class MenuSettings
    {
        public bool Enabled;
    }

    /// <summary>The "Bot" submenu in the unit context menu (config section Menu). The Harmony patch itself is installed by the harmony module.</summary>
    internal sealed class MenuFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly MenuSettings _settings = new MenuSettings();

        public MenuFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "menu"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(MenuSpecs.Set); }
        public int SettingsOrder { get { return 120; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Menu";
            _settings.Enabled = cfg.Bind(S, "Enabled", true, "Show the Bot submenu in the unit context menu", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            Ctx.Api.MenuEnabled = _settings.Enabled;
            var actionDebug = cfg.Bind(S, "Debug", false, "Log every XO/ring action step by step: ok / skipped / error, unit before and after", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.ActionDebug = actionDebug.Value;
            OnChanged(actionDebug, v => Ctx.Api.ActionDebug = v);
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<MenuSettings>(_settings);
        }
    }
}
