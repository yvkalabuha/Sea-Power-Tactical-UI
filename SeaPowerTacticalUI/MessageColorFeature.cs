namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-329: module messages, [Messages] HostileRed (Live, settings section General): the message window of the game shows the classification of
    /// a hostile contact in red. The patches are installed by HarmonyFeature (when the capability is there);
    /// this module owns the switch the patch bodies read.
    /// </summary>
    internal sealed class MessageColorFeature : UiFeatureBase
    {
        private IUiSetting<bool> _on;

        public MessageColorFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "messages"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(MessageColorSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            _on = cfg.Bind("Messages", "HostileRed", true, "Show messages about a hostile classification of a contact (Bandit!, hostile contacts, track classified as hostile) in red in the game's message window and top alerts. false = the game's colour",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Section = "general", Capability = "msg_hostile_red" });
            OnChanged(_on, v => MessageColorPatches.On = v);
        }

        public override void Init(UiContext ctx) { MessageColorPatches.On = _on.Value; }

        public override void Dispose() { MessageColorPatches.On = false; }
    }
}
