using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-068: radial menu of the selected own unit (config section Radial).</summary>
    internal sealed class RadialFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private RadialConfig _rad;
        private RadialMenu _radial;

        public RadialFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "radial"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(RadialSpecs.Set); }
        public int SettingsOrder { get { return 100; } }

        public override void Bind(IUiConfig cfg)
        {
            var c = new RadialConfig();
            const string S = "Radial";
            c.Enabled = cfg.Bind(S, "Enabled", true, "Radial menu of the selected own unit: hold the left mouse button on it", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sHoldMs = cfg.Bind(S, "HoldMs", 300f, "Hold time that opens the ring (ms); a plain click selects as before", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 100, Max = 2000, Step = 50, UnitKey = "ms" }); c.HoldMs = sHoldMs.Value;
            var sMoveTolerancePx = cfg.Bind(S, "MoveTolerancePx", 6f, "Cursor travel (px at 1080p) that cancels the hold (it is a drag then)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live }); c.MoveTolerancePx = sMoveTolerancePx.Value;
            var sPickRadiusPx = cfg.Bind(S, "PickRadiusPx", 20f, "3D: how close to the unit's screen point the cursor must be (px at 1080p)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live }); c.PickRadiusPx = sPickRadiusPx.Value;
            var sSubmenuDelayMs = cfg.Bind(S, "SubmenuDelayMs", 180f, "Hover time on a group sector before its arc opens (ms)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live }); c.SubmenuDelayMs = sSubmenuDelayMs.Value;
            c.OnMap = cfg.Bind(S, "OnMap", true, "Ring also on the tactical map (on the selected unit's icon)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sHotkey = cfg.Bind(S, "Hotkey", "", "Hold to open the ring on the selected unit (fallback): Unity KeyCode names joined by + (e.g. Q); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live, HoldKey = true }); c.Hotkey = sHotkey.Value;
            var sScale = cfg.Bind(S, "Scale", 0f, "Ring size factor; 0 = automatic from the game's UI scale", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.5, Max = 3, ZeroIsAuto = true, Step = 0.1 }); c.Scale = sScale.Value;
            var sShowVoiceHint = cfg.Bind(S, "ShowVoiceHint", true, "Show the voice phrase of the hovered item", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live }); c.ShowVoiceHint = sShowVoiceHint.Value;
            c.InputPatches = cfg.Bind(S, "InputPatches", false, "Harmony patches that stop camera rotation / map panning while the ring is open (enable only after a live check)", UiKeyClass.Advanced).Value;
            var sIcons = cfg.Bind(S, "Icons", true, "Draw glyphs on the ring sectors (the caption stays in the hint line under the ring); false = text labels as before", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live }); c.Icons = sIcons.Value;
            var sDebug = cfg.Bind(S, "Debug", false, "Log one line per failed ring pick (cursor, unit point, model rect, camera, ray, map)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live }); c.Debug = sDebug.Value;
            _rad = c;
            OnChanged(sHoldMs, v => { c.HoldMs = v; });   // the gesture takes HoldMs / SubmenuDelayMs while the ring is closed (RadialMenu.Update)
            OnChanged(sSubmenuDelayMs, v => { c.SubmenuDelayMs = v; });
            OnChanged(sMoveTolerancePx, v => { c.MoveTolerancePx = v; });
            OnChanged(sPickRadiusPx, v => { c.PickRadiusPx = v; });
            OnChanged(sScale, v => { c.Scale = v; });
            OnChanged(sIcons, v => { c.Icons = v; });
            OnChanged(sShowVoiceHint, v => { c.ShowVoiceHint = v; });
            OnChanged(sDebug, v => { c.Debug = v; });
            OnChanged(sHotkey, v => { c.Hotkey = v; if (_radial != null) _radial.Respec(v); });
        }


        public override void Init(UiContext ctx)
        {
            ctx.Provide<RadialConfig>(_rad);   // read by the Harmony module (InputPatches, OnMap)
            InitRing();
        }

        private void InitRing()
        {
            try
            {
                if (!_rad.Enabled) { _api.Capabilities.Remove("radial_lmb"); _api.Capabilities.Remove("radial_map"); _api.Capabilities.Remove("radial_menu"); return; }
                var r = new RadialMenu(_api, _rad, m => Ctx.Host.LogInfo(m),
                    new HotkeyBinding("radial", "Radial.Hotkey", _rad.Hotkey, Ctx.Hotkeys, m => Ctx.Host.LogInfo(m), m => { if (!m.Contains(": empty ")) Ctx.Host.LogWarning(m); }),   // always present: Live Hotkey can turn on later
                    new UnityKeyInput(_api));
                string err;
                if (!r.Init(out err))
                {
                    Ctx.Host.LogWarning("radial menu disabled: " + err);
                    _api.Capabilities.Remove("radial_lmb"); _api.Capabilities.Remove("radial_map"); _api.Capabilities.Remove("radial_menu");
                    return;
                }
                _radial = r;
                if (Ctx.Mission != null) Ctx.Mission.Subscribe(80, "radial", null, why => r.OnMissionLeft());   // T-130: drop the unit of the left mission
                if (!_rad.OnMap) _api.Capabilities.Remove("radial_map");
            }
            catch (Exception e) { Ctx.Host.LogError("radial init crashed: " + e); }
        }

        public override void MemProbe(System.Collections.Generic.IDictionary<string, long> into)
        {
            if (_radial != null) into["rad.glyphs"] = _radial.GlyphTexCount;
        }

        public override void Update(UiFrame frame)
        {
            if (_radial != null) _radial.Update();
        }

        public override void OnGUI()
        {
            if (_radial != null) _radial.OnGUI();
        }

        public override void Dispose()
        {
            try { if (_radial != null) { _radial.Dispose(); _api.SetRadialBlock(false); } } catch { }
        }
    }
}
