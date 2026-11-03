using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-174 (ADR-0073 section 3): ASW / CAP buttons on the game's bottom panel (config keys Ui.ToolbarButtons, Ui.ToolbarMaxTries).</summary>
    internal sealed class ToolbarFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private ToolbarButtons _buttons;
        private bool _enabled = true;
        private int _maxTries = 6;

        public ToolbarFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "game_toolbar"; } }
        public override string[] DependsOn { get { return new[] { "asw", "cap" }; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(ToolbarSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Ui";
            var on = cfg.Bind(S, "ToolbarButtons", true, "true = ASW and CAP buttons on the game's bottom panel; false = take them off (the map menu items and the hotkeys stay)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Section = "general", Apply = UiApply.Live });
            _enabled = on.Value;
            OnChanged(on, v => { _enabled = v; if (_buttons != null) _buttons.Enabled = v; });
            _maxTries = Math.Max(1, cfg.Bind(S, "ToolbarMaxTries", 6, "Failed tries to insert the ASW / CAP buttons into the game panel before the module gives up until the next mission", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" }).Value);
        }

        public override void Init(UiContext ctx)
        {
            try
            {
                var aswW = ctx.Get<IAswWindow>();
                var capW = ctx.Get<ICapWindow>();
                var hk = ctx.Hotkeys;
                _buttons = new ToolbarButtons(_api, m => ctx.Host.LogInfo(m)) { Enabled = _enabled, MaxTries = _maxTries };
                if (aswW != null) _buttons.Add(ToolbarMath.TagAsw, ToolbarGlyphs.Asw, "toolbar.asw.tip", () => aswW.Visible, v => aswW.Visible = v, () => hk.LabelOf("asw"));
                if (capW != null) _buttons.Add(ToolbarMath.TagCap, ToolbarGlyphs.Cap, "toolbar.cap.tip", () => capW.Visible, v => capW.Visible = v, () => hk.LabelOf("cap"));
            }
            catch (Exception e) { ctx.Host.LogError("toolbar init crashed: " + e); _buttons = null; }
        }

        public override void Update(UiFrame frame)
        {
            if (_buttons != null) _buttons.Update(frame.RealTime, _api.Frame.InMission, UiWindowPrefs.Hidden);
        }

        public override void Dispose()
        {
            if (_buttons != null) { _buttons.Dispose(); _buttons = null; }
        }
    }
}
