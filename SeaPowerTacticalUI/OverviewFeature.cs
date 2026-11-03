using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-070: Overview window of hostile and unidentified contacts (config section Overview).</summary>
    internal sealed class OverviewFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private OverviewConfig _ovc;
        private OverviewPanel _overview;
        private HotkeyBinding _binding;
        private IUiSetting<string> _ovRect, _ovSort, _ovTab, _ovOwnSort;
        private IUiSetting<bool> _ovCollapsed, _ovShowOnStart;

        public OverviewFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "overview"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(OverviewSpecs.Set); check.Specs(OverviewOwnSpecs.Set); check.Specs(FormationSpecs.Set); }   // T-242
        public int SettingsOrder { get { return 30; } }

        public override void Bind(IUiConfig cfg)
        {
            var c = new OverviewConfig();
            const string S = "Overview";
            c.Enabled = cfg.Bind(S, "Enabled", true, "Overview window: hostile and unidentified contacts (needs the overview capability)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            _ovShowOnStart = cfg.Bind(S, "ShowOnStart", false, "Window is open right after a mission loads (updated when you toggle it with the hotkey)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.ShowOnStart = _ovShowOnStart.Value;
            var vHotkey = cfg.Bind(S, "Hotkey", "LeftAlt+O", "Opens the Overview window: Unity KeyCode names joined by + (e.g. LeftAlt+O); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.Hotkey = vHotkey.Value;
            _ovRect = cfg.Bind(S, "Rect", c.Rect, "Window x,y,w,h as fractions of the screen (saved when you move or resize it)", UiKeyClass.Internal);
            c.Rect = _ovRect.Value;
            _ovCollapsed = cfg.Bind(S, "Collapsed", false, "Window is collapsed to one line", UiKeyClass.Internal);
            c.Collapsed = _ovCollapsed.Value;
            _ovSort = cfg.Bind(S, "Sort", c.Sort, "column:asc|desc, columns: icon,type,dist,brg,spd,alt,crs,id,sensor,age,vcl,idn,dep,lyr,cpa,tcpa,ttg,hits,dmg", UiKeyClass.Internal);
            c.Sort = _ovSort.Value;
            var vColumns = cfg.Bind(S, "Columns", c.Columns, "Visible columns (icon and type are required): icon,type,dist,brg,spd,alt,crs,id,sensor,age,vcl,idn,dep,lyr,cpa,tcpa,ttg,hits,dmg (dep..ttg need the contact_info module, hits and dmg the hit_estimate module)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Multi, Apply = UiApply.Live, Options = new[] { "dist", "brg", "spd", "alt", "crs", "id", "sensor", "age", "vcl", "idn", "dep", "lyr", "cpa", "tcpa", "ttg", "hits", "dmg" }, Required = new[] { "icon", "type" } });
            c.Columns = vColumns.Value;
            _ovTab = cfg.Bind(S, "Tab", c.Tab, "Active tab: contacts | own (saved when you switch it)", UiKeyClass.Internal);
            c.Tab = _ovTab.Value;
            _ovOwnSort = cfg.Bind(S, "OwnSort", c.OwnSort, "Own tab: column:asc|desc, columns: icon,type,name,task,alt,spd,load,grp", UiKeyClass.Internal);
            c.OwnSort = _ovOwnSort.Value;
            var vOwnColumns = cfg.Bind(S, "OwnColumns", c.OwnColumns, "Own tab: visible columns (icon and name are required): icon,type,name,task,alt,spd,load,grp", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Multi, Apply = UiApply.Live, Options = new[] { "type", "task", "alt", "spd", "load", "grp" }, Required = new[] { "icon", "name" } });
            c.OwnColumns = vOwnColumns.Value;
            var vMaxRows = cfg.Bind(S, "MaxRows", 30, "Rows shown at most 5..200", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 5, Max = 200 });
            c.MaxRows = Math.Max(5, Math.Min(200, vMaxRows.Value));
            var vRefreshSeconds = cfg.Bind(S, "RefreshSeconds", 1.0f, "Period of the contact scan (real time)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.2, Max = 10, Step = 0.1 });
            c.RefreshSeconds = vRefreshSeconds.Value;
            var vDebug = cfg.Bind(S, "Debug", false, "Log every Overview contact: side and its source, posture, colour and its source, live/dormant, kind/class, icon", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.Debug = vDebug.Value;
            var vShowWeapons = cfg.Bind(S, "ShowWeapons", true, "List enemy missiles and torpedoes too", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.ShowWeapons = vShowWeapons.Value;
            var vClickSelectsInGame = cfg.Bind(S, "ClickSelectsInGame", true, "A click selects the contact in the game like a map click (may switch the 3D camera); false = mark the row only", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.ClickSelectsInGame = vClickSelectsInGame.Value;
            var vFitRows = cfg.Bind(S, "FitRows", true, "Window height follows the number of rows (Rect height = maximum)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.FitRows = vFitRows.Value;
            var vCenterSecondMap = cfg.Bind(S, "CenterSecondMap", true, "Double click also centres the second-monitor map", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.CenterSecondMap = vCenterSecondMap.Value;
            c.IconSource = cfg.Bind(S, "IconSource", "auto", "auto | map | resources | own: where the icons come from (auto = game vector icons, then textures, then own)", UiKeyClass.Advanced).Value;
            var vClosingDeadbandKn = cfg.Bind(S, "ClosingDeadbandKn", 3.0f, "Closing speed (kn) below which the arrow is a dot", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 50, Step = 0.5 });
            c.ClosingDeadbandKn = vClosingDeadbandKn.Value;
            var vFastClosingKn = cfg.Bind(S, "FastClosingKn", 300.0f, "Closing speed (kn) that draws a double arrow", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 10, Max = 2000, Step = 10 });
            c.FastClosingKn = vFastClosingKn.Value;
            var vScale = cfg.Bind(S, "Scale", 0f, "Window size factor; 0 = automatic from the game's UI scale", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.5, Max = 3, ZeroIsAuto = true, Step = 0.1 });
            c.Scale = vScale.Value;
            c.Save = (rect, collapsed, sort, visible) =>
            {
                if (rect != null) { _ovRect.Value = rect; c.Rect = rect; }   // T-183: null = the window was not placed yet
                _ovCollapsed.Value = collapsed; _ovSort.Value = sort;
                if (visible != null) _ovShowOnStart.Value = visible == "true";
            };
            c.SaveTab = (tab, ownSort) => { _ovTab.Value = tab; _ovOwnSort.Value = ownSort; };   // T-242
            _ovc = c;
            OnChanged(_ovShowOnStart, v => c.ShowOnStart = v);   // idempotent: no write back (the panel writes this key itself)
            OnChanged(vHotkey, v => { c.Hotkey = v; if (_binding != null) _binding.Respec(v); });
            OnChanged(vColumns, v => { c.Columns = v; if (_overview != null) _overview.ApplyColumns(); });
            OnChanged(vOwnColumns, v => { c.OwnColumns = v; if (_overview != null) _overview.ApplyOwnColumns(); });   // T-242
            OnChanged(vMaxRows, v => c.MaxRows = Math.Max(5, Math.Min(200, v)));
            OnChanged(vShowWeapons, v => c.ShowWeapons = v);
            OnChanged(vClickSelectsInGame, v => c.ClickSelectsInGame = v);
            OnChanged(vScale, v => c.Scale = v);   // the panel reads it every frame
            OnChanged(vRefreshSeconds, v => c.RefreshSeconds = v);
            OnChanged(vFitRows, v => c.FitRows = v);
            OnChanged(vDebug, v => { c.Debug = v; if (_overview != null) _overview.ApplyRuntime(); });
            OnChanged(vCenterSecondMap, v => { c.CenterSecondMap = v; if (_overview != null) _overview.ApplyRuntime(); });
            OnChanged(vClosingDeadbandKn, v => { c.ClosingDeadbandKn = v; if (_overview != null) _overview.ApplyRuntime(); });
            OnChanged(vFastClosingKn, v => { c.FastClosingKn = v; if (_overview != null) _overview.ApplyRuntime(); });
        }


        public override void Init(UiContext ctx)
        {
            try
            {
                if (!_ovc.Enabled) { RemoveOverviewCaps(); return; }
                if (!_api.Capabilities.Contains("overview")) { Ctx.Host.LogWarning("overview disabled: members of the game are missing"); RemoveOverviewCaps(); return; }
                _binding = new HotkeyBinding("overview", "Overview.Hotkey", _ovc.Hotkey, Ctx.Hotkeys, m => Ctx.Host.LogInfo(m), m => Ctx.Host.LogWarning(m));
                var p = new OverviewPanel(_api, _ovc, m => Ctx.Host.LogInfo(m), _binding, new UnityKeyInput(_api));
                string err;
                if (!p.Init(out err)) { Ctx.Host.LogWarning("overview disabled: " + err); RemoveOverviewCaps(); return; }
                _overview = p;
                Ctx.Mission.Subscribe(79, "overview", null, why => { if (_overview != null) _overview.ResetExpand(); });   // T-168: the expanded rows are ids of the old mission
                if (_ovc.ShowOnStart) _api.SetOverview(true, "config");
            }
            catch (Exception e) { Ctx.Host.LogError("overview init crashed: " + e); RemoveOverviewCaps(); }
        }


        private void RemoveOverviewCaps()
        {
            foreach (var c in new[] { "overview", "overview_center", "overview_select", "overview_own", "overview_own_select", "overview_block", "overview_hotkey" }) _api.Capabilities.Remove(c);
        }

        public override void MemProbe(System.Collections.Generic.IDictionary<string, long> into)
        {
            if (_overview != null) { into["ov.tex"] = _overview.TexCount; into["ov.icons"] = _overview.IconCount; }
        }

        public override void Update(UiFrame frame)
        {
            if (_overview != null) _overview.Update();
        }

        public override void OnGUI()
        {
            if (_overview != null) _overview.OnGUI();
        }

        public override void Dispose()
        {
            try { if (_overview != null) _overview.Dispose(); _api.SetOverviewBlock(false); } catch { }
        }
    }
}
