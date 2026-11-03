namespace VoiceBotBridge.Ui
{
    /// <summary>What the Harmony module needs from the StandingOrders section.</summary>
    internal sealed class DoctrineSettings
    {
        public bool Enabled = true, ForceFallback;
    }

    /// <summary>Standing Orders settings of the mod (config section StandingOrders) and the doctrine pump. The patches are installed by the harmony module.</summary>
    internal sealed class DoctrineFeature : UiFeatureBase
    {
        private readonly DoctrineSettings _settings = new DoctrineSettings();

        public DoctrineFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "doctrine"; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(DoctrineSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            _settings.Enabled = cfg.Bind("StandingOrders", "Enabled", true, "Add the mod's settings to the game's Standing Orders window (false = the 'Modes' submenu and in-memory settings only)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart, Section = "menu" }).Value;
            _settings.ForceFallback = cfg.Bind("StandingOrders", "ForceFallback", false, "true = keep the settings in memory and show the 'Modes' submenu even when the Standing Orders patches work", UiKeyClass.Advanced, new UiSettingMeta { Section = "menu" }).Value;
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<DoctrineSettings>(_settings);
        }

        public override void Update(UiFrame frame)
        {
            Ctx.Api.PumpDoctrine(frame.RealTime);
        }
    }
}
