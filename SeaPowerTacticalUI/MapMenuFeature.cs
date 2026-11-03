using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-104: Overview / ASW / CAP items in the map menu, with the map layer list as fallback (config keys Ui.ForceMapMenuFallback, Ui.Debug, Ui.MapMenuMaxTries).</summary>
    internal sealed class MapMenuFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private bool _forceMapMenuFallback, _uiDebug;
        private int _mapMenuMaxTries = 6;
        private MapMenuWindows _mapMenu;

        public MapMenuFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "map_menu"; } }
        public override string[] DependsOn { get { return new[] { "asw", "cap", "settings", "radar", "range_rings" }; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(MapMenuSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Ui";
            _forceMapMenuFallback = cfg.Bind(S, "ForceMapMenuFallback", false, "true = do not insert the Overview / ASW items into the map menu; use the map layer list instead", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" }).Value;
            var uiDebug = cfg.Bind(S, "Debug", false, "Log the map menu wiring", UiKeyClass.Advanced, new UiSettingMeta { Section = "general", Apply = UiApply.Live });
            _uiDebug = uiDebug.Value;
            OnChanged(uiDebug, v => { _uiDebug = v; if (_mapMenu != null) _mapMenu.Debug = v; });
            _mapMenuMaxTries = Math.Max(1, cfg.Bind(S, "MapMenuMaxTries", 6, "Failed tries to insert the Overview / ASW items into the map menu before the layer list fallback takes over", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" }).Value);
        }

        public override void Init(UiContext ctx)
        {
            try
            {
                var aswW = ctx.Get<IAswWindow>();
                var capW = ctx.Get<ICapWindow>();
                var setW = ctx.Get<ISettingsWindow>();
                _mapMenu = new MapMenuWindows(_api, aswW == null ? null : (Func<bool>)(() => aswW.Visible), aswW == null ? null : (Action<bool>)(v => aswW.Visible = v),
                    capW == null ? null : (Func<bool>)(() => capW.Visible), capW == null ? null : (Action<bool>)(v => capW.Visible = v),
                    setW == null ? null : (Func<bool>)(() => setW.Visible), setW == null ? null : (Action<bool>)(v => setW.Visible = v))
                {
                    ForceFallback = _forceMapMenuFallback,
                    MaxTries = _mapMenuMaxTries,
                    Debug = _uiDebug
                };
                // T-228: legend checks, only for the modules that are up
                var rl = ctx.Get<IRingLegend>(); var rv = ctx.Get<IRadarLegend>();
                var rings = rl == null ? null : rl.Legend; var radar = rv == null ? null : rv.Legend;
                if (rings != null)
                    _mapMenu.AddToggle(MapMenuWindows.TagLegendRings, "mapmenu.legend.rings", "mapmenu.legend.hint", "mapmenu.legend.off",
                        () => rings.Visible, v => rings.SetSession(v), () => rings.MenuEnabled, 205);
                if (radar != null)
                    _mapMenu.AddToggle(MapMenuWindows.TagLegendRadar, "mapmenu.legend.radar", "mapmenu.legend.hint", "mapmenu.legend.off",
                        () => radar.Visible, v => radar.SetSession(v), () => radar.MenuEnabled, 206);
            }
            catch (Exception e) { ctx.Host.LogError("map menu init crashed: " + e); _mapMenu = null; }
        }

        public override void Update(UiFrame frame)
        {
            if (_mapMenu != null) _mapMenu.Update();
            PumpPopup();
        }

        /// <summary>T-240: the game's open popups (map hamburger, context menu) for the mod windows: they yield under them.</summary>
        private void PumpPopup()
        {
            try
            {
                double[] a = null, b = null;
                bool mm = _mapMenu != null && _mapMenu.OpenMenu(out a);
                bool cm = _api.ContextMenuOpen(out b, UnityEngine.Time.unscaledTime);
                var cur = new GamePopupState { Open = mm || cm };
                if (cur.Open)
                {
                    cur.Source = mm && cm ? "map_menu+context_menu" : (mm ? "map_menu" : "context_menu");
                    if ((mm && a == null) || (cm && b == null)) cur.Rect = null;
                    else cur.Rect = GamePopupMath.Union(new List<double[]> { mm ? a : null, cm ? b : null });
                }
                var prev = UiWindowPrefs.Popup;
                UiWindowPrefs.Popup = cur;
                string line = GamePopupMath.LogLine(prev, cur);
                if (line != null && !cur.Open && prev != null && prev.Source != null && prev.Source.Contains("map_menu") && _mapMenu != null)
                    line = GamePopupMath.ClosedLine(_mapMenu.LastDropReason, UnityEngine.Time.unscaledTime - _mapMenu.LastDropAt);
                if (line != null) _api.Log("[MapMenu] " + line);
            }
            catch { UiWindowPrefs.Popup = new GamePopupState(); }
        }
    }
}
