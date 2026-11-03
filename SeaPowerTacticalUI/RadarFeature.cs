using System;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>What the other modules need from the radar coverage (ASW / CAP overlays draw into the layer, the heartbeat reports it). Null parts = off.</summary>
    internal interface IRadarView : IRadarLegend
    {
        CoverageOverlay Overlay { get; }
        CoverageLayer Layer { get; }
    }

    /// <summary>T-066 / T-107: radar coverage overlay (config section RadarCoverage) and its Noesis layer.</summary>
    internal sealed class RadarFeature : UiFeatureBase, IRadarView, IUiSettingsSection
    {
        private readonly GameApi _api;
        private CoverageConfig _cov;
        private CoverageOverlay _overlay;
        private CoverageLayer _layer;
        private bool _layerHooked;
        private UnityEngine.GameObject _eofGo;   // T-190: end-of-frame driver of the Noesis layer
        private bool _smallMapWindow = true;
        private LegendSwitch _legendSw = new LegendSwitch("radar", true);   // T-228
        private int _legendVer;

        public RadarFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "radar"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(RadarSpecs.Set); }
        public int SettingsOrder { get { return 70; } }
        public CoverageOverlay Overlay { get { return _overlay; } }
        public CoverageLayer Layer { get { return _layer; } }
        internal CoverageConfig Config { get { return _cov; } }
        public LegendSwitch Legend { get { return _overlay != null ? _legendSw : null; } }

        public override void Bind(IUiConfig cfg)
        {
            var c = new CoverageConfig();
            const string S = "RadarCoverage";
            c.Enabled = cfg.Bind(S, "Enabled", true, "Radar coverage overlay on the tactical map (needs the radar_coverage capability)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sShowOnStart = cfg.Bind(S, "ShowOnStart", false, "Overlay is on right after the game starts", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.ShowOnStart = sShowOnStart.Value;
            var sHotkey = cfg.Bind(S, "Hotkey", "LeftAlt+V|LeftAlt+Y", "Turns the radar coverage overlay on or off: Unity KeyCode names joined by + (e.g. LeftAlt+V); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.Hotkey = sHotkey.Value;
            var sTiers = cfg.Bind(S, "Tiers", CoverageMath.DefaultTiers, "Reference targets height_m:rcs_m2:kinds (air|surface joined by +), 1..4 tiers", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.Tiers = sTiers.Value;
            var sVisibleHeightM = cfg.Bind(S, "VisibleHeightM", 3.0f, "Height added to the target (game default _visibleHeight is 0.046 unity)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.VisibleHeightM = sVisibleHeightM.Value;
            var sStateCheckSeconds = cfg.Bind(S, "StateCheckSeconds", 1.0f, "Period of the cheap radar state poll (on/off, EMCON, sea state, new units), 0.2..1.0 s; a state change recalculates at once (not more often than 1 s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.StateCheckSeconds = CoverageMath.ClampStateCheck(sStateCheckSeconds.Value);
            var sRecomputeSeconds = cfg.Bind(S, "RecomputeSeconds", 10.0f, "Recalculation period while units move, 2..120 s (nothing moves: no recalculation)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.RecomputeSeconds = CoverageMath.ClampRecompute(sRecomputeSeconds.Value);
            c.RenderMode = CoverageMath.ParseRenderMode(cfg.Bind(S, "RenderMode", "auto", "auto|noesis|imgui: noesis = zone and legend inside the game map, under the game windows; imgui = drawn over everything; auto = noesis, imgui if the layer is unavailable", UiKeyClass.Advanced).Value);
            c.LayerIndex = cfg.Bind(S, "LayerIndex", -1, "Index in MapBase.Children for the noesis layer; -1 = right above the base map layer", UiKeyClass.Advanced).Value;
            var sFlipY = cfg.Bind(S, "FlipY", "auto", "auto|true|false: flip the texture vertically (set it if the zone is mirrored)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.FlipY = sFlipY.Value;
            c.RtScale = CoverageMath.ClampRtScale(cfg.Bind(S, "RtScale", 1.0f, "Render texture pixels per screen pixel, 0.5..1", UiKeyClass.Advanced).Value);
            var sTerrainShadows = cfg.Bind(S, "TerrainShadows", true, "Terrain shadows from DEM heights (approximate)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.TerrainShadows = sTerrainShadows.Value;
            c.Rays = Math.Max(36, Math.Min(720, cfg.Bind(S, "Rays", 360, "Azimuth rays per radar 36..720", UiKeyClass.Advanced).Value));
            c.Bins = Math.Max(32, Math.Min(256, cfg.Bind(S, "Bins", 128, "Range cells per ray 32..256", UiKeyClass.Advanced).Value));
            var sIncludeTargeting = cfg.Bind(S, "IncludeTargeting", false, "Also draw fire-control radars", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.IncludeTargeting = sIncludeTargeting.Value;
            var sShowOffRadars = cfg.Bind(S, "ShowOffRadars", true, "Grey dashed outline for radars that are off or under EMCON", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.ShowOffRadars = sShowOffRadars.Value;
            var sJamming = cfg.Bind(S, "Jamming", true, "Cut the radar zone by jamming of jammers known to you (tracks you have identified)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });   // T-182 (ADR-0081)
            c.Jamming = sJamming.Value;
            var sFogAlpha = cfg.Bind(S, "FogAlpha", 0.35f, "Darkness of the area nothing covers (0..0.9)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 0.9, Step = 0.05 });
            c.FogAlpha = sFogAlpha.Value;
            c.ScaleOverride = cfg.Bind(S, "ScaleOverride", 0.0f, "Noesis screen units per pixel; 0 = automatic (calibrated on the main view)", UiKeyClass.Advanced).Value;
            var sDebug = cfg.Bind(S, "Debug", false, "Log the overlay projection (radar positions in screen pixels) every 10 s, the layer insertion (map children) and a coverage stats line every 60 s (builds, build_ms, worker_cpu_pct, poll/snapshot ms, rt redraws, fps_avg, frame_ms_p95)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.Debug = sDebug.Value;
            var sSmallMapWindow = cfg.Bind(S, "SmallMapWindow", true, "Also draw the zone on the main map while it is not maximised (the small Tactical Display window in the 3D view); false = only on the maximised map", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            _smallMapWindow = sSmallMapWindow.Value;
            var sLegend = cfg.Bind(S, "Legend", true, "Radar coverage legend in the top-right corner of the map, under the range ring legend; off = the map menu item is inactive", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });   // T-228
            _legendSw = new LegendSwitch("radar", sLegend.Value);
            var sMaxSegments = cfg.Bind(S, "MaxSegments", 6000, "Outline segments drawn per frame at most", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.MaxSegments = sMaxSegments.Value;
            c.Colors = cfg.Bind(S, "Colors", c.Colors, "RRGGBB for tiers 1..4 and for off/EMCON, comma separated", UiKeyClass.Advanced).Value;
            _cov = c;
            _api.CovCfg = c;
            // T-144 L3: the worker, the overlay and the layer read these fields from the shared config object; the game side only needs a nudge
            OnChanged(sShowOnStart, v => { c.ShowOnStart = v; });   // read at the next start only
            OnChanged(sTerrainShadows, v => { c.TerrainShadows = v; _api.CoverageConfigChanged(true); });
            OnChanged(sIncludeTargeting, v => { c.IncludeTargeting = v; _api.CoverageConfigChanged(true); });
            OnChanged(sShowOffRadars, v => { c.ShowOffRadars = v; _api.CoverageConfigChanged(true); });
            OnChanged(sJamming, v => { c.Jamming = v; _api.CoverageConfigChanged(true); });
            OnChanged(sTiers, v => { c.Tiers = v; _api.CoverageConfigChanged(true); });
            OnChanged(sVisibleHeightM, v => { c.VisibleHeightM = v; _api.CoverageConfigChanged(true); });
            OnChanged(sStateCheckSeconds, v => { c.StateCheckSeconds = CoverageMath.ClampStateCheck(v); _api.CoverageConfigChanged(true); });
            OnChanged(sRecomputeSeconds, v => { c.RecomputeSeconds = CoverageMath.ClampRecompute(v); _api.CoverageConfigChanged(true); });
            OnChanged(sFogAlpha, v => { c.FogAlpha = v; _api.CoverageConfigChanged(false); });
            OnChanged(sFlipY, v => { c.FlipY = v; _api.CoverageConfigChanged(false); });
            OnChanged(sMaxSegments, v => { c.MaxSegments = v; _api.CoverageConfigChanged(false); });
            OnChanged(sDebug, v => { c.Debug = v; });
            OnChanged(sSmallMapWindow, v => { _smallMapWindow = v; if (_overlay != null) _overlay.AllowSmallWindow = v; _api.CoverageConfigChanged(false); });
            OnChanged(sLegend, v => { _legendSw.SetCfg(v); });
            OnChanged(sHotkey, v => { c.Hotkey = v; if (_overlay != null) _overlay.Respec(v); });
        }


        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            InitOverlay();
            ctx.Provide<IRadarView>(this);
            ctx.Provide<IRadarLegend>(this);   // T-228: the map menu does not know the radar view
        }

        private void InitOverlay()
        {
            try
            {
                if (!_cov.Enabled) return;
                var ov = new CoverageOverlay(_api, _cov, m => Ctx.Host.LogInfo(m));
                string err;
                if (!ov.Init(out err)) { Ctx.Host.LogWarning("radar overlay disabled: " + err); return; }
                ov.AllowSmallWindow = _smallMapWindow;
                ov.Legend = _legendSw;
                ov.BindHotkey(new HotkeyBinding("radar", "RadarCoverage.Hotkey", _cov.Hotkey, Ctx.Hotkeys, m => Ctx.Host.LogInfo(m), m => Ctx.Host.LogWarning(m)),
                    new UnityKeyInput(_api), new GameKeys(_api));
                _overlay = ov;
                if (Ctx.Mission != null) Ctx.Mission.Subscribe(68, "radar_scan", null, why => { if (_overlay != null) _overlay.ResetScan(); });   // T-127: the next mission searches the map at once
                _api.MapFound = () => _overlay != null && _overlay.MapFound;
                _api.Capabilities.Add("radar_overlay");
                string mode = CoverageMath.ParseRenderMode(_cov.RenderMode);
                _api.CoverageRenderMode = "imgui";
                if (mode != "imgui")
                {
                    string lerr = null;
                    CoverageLayer layer = null;
                    if (!_api.Capabilities.Contains("radar_noesis")) lerr = "capability radar_noesis missing";
                    else
                    {
                        var legend = new CoverageLegendRt(ov.TierColors, ov.GreyColor, m => Ctx.Host.LogInfo(m));
                        layer = new CoverageLayer(_api, _cov, ov, legend, m => Ctx.Host.LogInfo(m));
                        if (!layer.TryInit(out lerr)) { layer.Dispose(); layer = null; }
                    }
                    if (layer != null)
                    {
                        _layer = layer;
                        Camera.onPreRender += _layer.OnPreRender;
                        _layerHooked = true;
                        StartEofDriver();
                        _api.CoverageLayerDiag = _layer;
                        _api.CoverageRenderMode = "noesis";
                        _api.NoesisAttached = () => _layer != null && _layer.Attached;
                    }
                    else if (mode == "auto") Ctx.Host.LogWarning("radar overlay: noesis layer unavailable (" + lerr + "), using imgui");
                    else { Ctx.Host.LogWarning("radar overlay disabled: noesis layer unavailable (" + lerr + ")"); _overlay = null; _api.MapFound = () => false; _api.Capabilities.Remove("radar_overlay"); ov.Dispose(); return; }
                }
                if (_cov.ShowOnStart) _api.SetOverlay(true, "config");
            }
            catch (Exception e) { Ctx.Host.LogError("radar overlay init crashed: " + e); }
        }


        public override void Update(UiFrame frame)
        {
            if (_overlay != null)
            {
                _overlay.Update();
                if (_legendSw.Version != _legendVer) { _legendVer = _legendSw.Version; _api.CoverageConfigChanged(false); }   // T-228: redraw the layer without / with the legend
            }
        }

        public override void OnGUI()
        {
            if (_overlay != null && _layer == null) _overlay.OnGUI();
        }

        // auto mode: 3 failed insertions in a row -> give the zone back to the IMGUI path
        public override void MemProbe(System.Collections.Generic.IDictionary<string, long> into)
        {
            if (_layer != null) into["rt.count"] = _layer.RtCount;
        }

        internal void CheckLayerFallback()
        {
            if (_layer == null || _layer.FailStreak < 3 || CoverageMath.ParseRenderMode(_cov.RenderMode) != "auto") return;
            Ctx.Host.LogWarning("radar overlay: noesis layer lost 3 times in a row, using imgui");
            try { if (_layerHooked) Camera.onPreRender -= _layer.OnPreRender; _layerHooked = false; StopEofDriver(); _layer.Dispose(); } catch { }
            _layer = null;
            _api.CoverageLayerDiag = null;
            _api.CoverageRenderMode = "imgui";
            _api.NoesisAttached = () => false;
        }


        /// <summary>T-190: the end-of-frame driver of the layer (fallback path when no screen camera renders it).</summary>
        private void StartEofDriver()
        {
            try
            {
                StopEofDriver();
                var go = new GameObject("VoiceBot.RadarLayerEof") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<CoverageLayerDriver>().Layer = _layer;
                _eofGo = go;
            }
            catch (Exception e) { Ctx.Host.LogWarning("radar layer: end-of-frame driver failed: " + e.Message); }
        }

        private void StopEofDriver()
        {
            try { if (_eofGo != null) UnityEngine.Object.Destroy(_eofGo); } catch { }
            _eofGo = null;
        }

        /// <summary>Layer update of the frame (called by the radar_pump module).</summary>
        internal void LayerTick()
        {
            if (_layer != null) { _layer.Update(); CheckLayerFallback(); }
        }

        public override void Dispose()
        {
            try { if (_layer != null) { if (_layerHooked) Camera.onPreRender -= _layer.OnPreRender; _layerHooked = false; StopEofDriver(); _layer.Dispose(); _layer = null; } } catch { }
            try { _api.DisposeCoverage(); if (_overlay != null) _overlay.Dispose(); } catch { }
        }
    }

    /// <summary>The per-frame coverage work that runs late in the frame: PumpCoverage, the layer (+ fallback), the frame statistics.</summary>
    internal sealed class RadarPumpFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private RadarFeature _radar;
        private int _slotPump, _slotLayer, _slotStats;

        public RadarPumpFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "radar_pump"; } }
        public override string[] DependsOn { get { return new[] { "radar" }; } }

        public override void Init(UiContext ctx)
        {
            int owner = ctx.Guard.OwnerOf(Id);
            _slotPump = ctx.Guard.Register("radar.pump", owner);
            _slotLayer = ctx.Guard.Register("radar.layer", owner);
            _slotStats = ctx.Guard.Register("radar.stats", owner);
            _radar = ctx.Get<IRadarView>() as RadarFeature;
        }

        public override void Update(UiFrame frame)
        {
            if (_radar == null) return;
            try { _api.PumpCoverage(frame.RealTime); } catch (Exception e) { Ctx.Guard.Fail(_slotPump, e); }
            try { _radar.LayerTick(); } catch (Exception e) { Ctx.Guard.Fail(_slotLayer, e); }
            try { if (_api.OverlayOn) _api.CovStats.AddFrame((float)frame.DeltaTime); } catch (Exception e) { Ctx.Guard.Fail(_slotStats, e); }
        }
    }
}
