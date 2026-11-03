namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-331: module wp_quiet, [Messages] QuietModWaypoints / QuietRepeatS: the game's "We're on position, out." (text + voice) of an aircraft under the
    /// control of the mod comes at most once per QuietRepeatS seconds. The patches are installed by HarmonyFeature; this module owns the switch and the book.
    /// </summary>
    internal sealed class WaypointQuietFeature : UiFeatureBase
    {
        private IUiSetting<bool> _on;
        private IUiSetting<int> _repeat;

        public WaypointQuietFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "wp_quiet"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(WaypointQuietSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            _on = cfg.Bind("Messages", "QuietModWaypoints", true, "Do not repeat the message We're on position for the aircraft that the mod controls (CAP, ASW, SAR groups): at most one message per QuietRepeatS seconds per aircraft. Aircraft you order yourself are not touched. false = as in the game",
                UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Section = "general", Capability = "wp_reached_quiet" });
            _repeat = cfg.Bind("Messages", "QuietRepeatS", 300, "Seconds of the mission time between two We're on position messages of one aircraft of the mod (0 = never for them)",
                UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Section = "general", Min = 0, Max = 3600, Step = 30, UnitKey = "s" });
            OnChanged(_on, v => WaypointQuietPatches.On = v);
            OnChanged(_repeat, v => WaypointQuietPatches.Book.RepeatS = System.Math.Max(0, System.Math.Min(3600, v)));
        }

        public override void Init(UiContext ctx)
        {
            WaypointQuietPatches.On = _on.Value;
            WaypointQuietPatches.Book.RepeatS = System.Math.Max(0, System.Math.Min(3600, _repeat.Value));
        }

        public override void Dispose() { WaypointQuietPatches.On = false; WaypointQuietPatches.Book.Reset(); }
    }
}
