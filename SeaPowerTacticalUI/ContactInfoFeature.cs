using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-136 / ADR-0045: contact information on the tactical map (config section ContactInfo): controller, label layer, 7 map layers, IContactInfo service.</summary>
    internal sealed class ContactInfoFeature : UiFeatureBase, IUiDiag, IUiSettingsSection
    {
        private readonly GameApi _api;
        private readonly ContactInfoSettings _s = new ContactInfoSettings();
        private readonly IUiSetting<bool>[] _showSet = new IUiSetting<bool>[7];
        private ContactInfoController _ctl;
        private ContactLabelLayer _labels;

        public override void MemProbe(System.Collections.Generic.IDictionary<string, long> into)
        {
            if (_labels != null) { into["ci.pool"] = _labels.PoolCount; into["ci.brushes"] = _labels.BrushCount; }
        }
        private IKeyInput _input;
        private IHotkeyGame _keyGame;
        private WindowLayerSpec[] _specs;

        public ContactInfoFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "contact_info"; } }
        public int SettingsOrder { get { return 90; } }
        public override string[] DependsOn { get { return new[] { "text", "selection", "map_layer" }; } }

                private static int ClampLabels(int v) { return Math.Max(10, Math.Min(300, v)); }

        private void Refresh() { if (_ctl != null) _ctl.Refresh(); }

        private void LayerHotkey(int i, string v)
        {
            if (_ctl != null) _ctl.RespecLayer((CiLayer)i, v); else _s.LayerHotkeys[i] = v;
        }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "ContactInfo";
            var c = _s;
            c.Enabled = cfg.Bind(S, "Enabled", true, "Contact information on the map: depth and layer, speed, TTG, CPA, age, own noise, waypoints (needs the contact_info capability)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var period = cfg.Bind(S, "PeriodS", 1.0, "Period of a full snapshot of the contacts (real time)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.PeriodS = Math.Max(0.25, period.Value);
            OnChanged(period, v => { _s.PeriodS = Math.Max(0.25, v); });
            var maxLabels = cfg.Bind(S, "MaxLabels", 80, "Labels on the map at most 10..300 (nearest first)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.MaxLabels = ClampLabels(maxLabels.Value);
            OnChanged(maxLabels, v => { _s.MaxLabels = ClampLabels(v); Refresh(); });
            var units = cfg.Bind(S, "Units", "nm,kn,ft", "Units: nm|km, kn|kmh, ft|m joined by commas", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Text, Apply = UiApply.Live, Section = "general", Parts = new[] { new[] { "nm", "km" }, new[] { "kn", "kmh" }, new[] { "ft", "m" } } });
            c.Units = units.Value;
            OnChanged(units, v => { if (_ctl != null) _ctl.SetUnits(v); else _s.Units = v; });
            var ageShowS = cfg.Bind(S, "AgeShowS", 15.0, "The age of a contact is shown above this many seconds", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.AgeShowS = ageShowS.Value;
            OnChanged(ageShowS, v => { _s.AgeShowS = v; Refresh(); });
            var staleS = cfg.Bind(S, "StaleS", 30.0, "A contact older than this is dimmed and its speed is marked as an estimate", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.StaleS = staleS.Value;
            OnChanged(staleS, v => { _s.StaleS = v; Refresh(); });
            var cpaTarget = cfg.Bind(S, "CpaTarget", "selected", "selected | group: CPA to the selected own unit or the best one of the player's group", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "selected", "group" } });
            c.CpaTarget = cpaTarget.Value;
            OnChanged(cpaTarget, v => { _s.CpaTarget = v; Refresh(); });
            var cpaMaxMin = cfg.Bind(S, "CpaMaxMin", 30.0, "CPA is shown only when it happens within this many minutes", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.CpaMaxMin = cpaMaxMin.Value;
            OnChanged(cpaMaxMin, v => { _s.CpaMaxMin = v; Refresh(); });
            var ttgHitNm = cfg.Bind(S, "TtgHitNm", 1.0, "A foreign weapon counts as heading for an own unit when its CPA is below this (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.TtgHitNm = ttgHitNm.Value;
            OnChanged(ttgHitNm, v => { _s.TtgHitNm = v; Refresh(); });
            var cavMarginKn = cfg.Bind(S, "CavMarginKn", 1.0, "Cavitation warning margin (kn) below the onset speed", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            c.CavMarginKn = cavMarginKn.Value;
            OnChanged(cavMarginKn, v => { _s.CavMarginKn = v; Refresh(); });
            var waypoint = cfg.Bind(S, "Waypoint", "dist", "Waypoint label: dist | eta | ttg | off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "dist", "eta", "ttg", "off" } });
            c.Waypoint = waypoint.Value;
            OnChanged(waypoint, v => { _s.Waypoint = v; Refresh(); });
            var ownSpeedNoise = cfg.Bind(S, "OwnSpeedNoise", true, "Own ships and submarines: speed and noise (dB) on one line next to the icon, coloured by the margin to cavitation", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            c.OwnSpeedNoise = ownSpeedNoise.Value;
            OnChanged(ownSpeedNoise, v => { if (_ctl != null) _ctl.SetOwnSpeedNoise(v); else _s.OwnSpeedNoise = v; });
            c.BetterTacMap = cfg.Bind(S, "BetterTacMap", "auto", "auto | on: auto = our duplicate labels are off when the Better TacMap mod is found", UiKeyClass.Advanced).Value;
            c.NoiseDisplay = cfg.Bind(S, "NoiseDisplay", "auto", "auto | on: auto = our dB label is off when the Vessel Noise Display mod is found", UiKeyClass.Advanced).Value;
            var hotkey = cfg.Bind(S, "Hotkey", "LeftAlt+N|LeftAlt+E", "Turns all contact labels on or off: Unity KeyCode names joined by + (e.g. LeftAlt+N); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.Hotkey = hotkey.Value;
            OnChanged(hotkey, v => { if (_ctl != null) _ctl.RespecMaster(v); else _s.Hotkey = v; });
            _showSet[0] = cfg.Bind(S, "Show.Depth", true, "Label layer Depth is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk0 = cfg.Bind(S, "Hotkey.Depth", "LeftAlt+LeftShift+Keypad1", "Turns the Depth label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad1); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[0] = hk0.Value;
            OnChanged(hk0, v => LayerHotkey(0, v));
            _showSet[1] = cfg.Bind(S, "Show.Speed", false, "Label layer Speed is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk1 = cfg.Bind(S, "Hotkey.Speed", "LeftAlt+LeftShift+Keypad2", "Turns the Speed label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad2); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[1] = hk1.Value;
            OnChanged(hk1, v => LayerHotkey(1, v));
            _showSet[2] = cfg.Bind(S, "Show.Ttg", true, "Label layer Ttg is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk2 = cfg.Bind(S, "Hotkey.Ttg", "LeftAlt+LeftShift+Keypad3", "Turns the Ttg label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad3); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[2] = hk2.Value;
            OnChanged(hk2, v => LayerHotkey(2, v));
            _showSet[3] = cfg.Bind(S, "Show.Cpa", true, "Label layer Cpa is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk3 = cfg.Bind(S, "Hotkey.Cpa", "LeftAlt+LeftShift+Keypad4", "Turns the Cpa label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad4); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[3] = hk3.Value;
            OnChanged(hk3, v => LayerHotkey(3, v));
            _showSet[4] = cfg.Bind(S, "Show.Age", true, "Label layer Age is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk4 = cfg.Bind(S, "Hotkey.Age", "LeftAlt+LeftShift+Keypad5", "Turns the Age label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad5); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[4] = hk4.Value;
            OnChanged(hk4, v => LayerHotkey(4, v));
            _showSet[5] = cfg.Bind(S, "Show.Noise", true, "Label layer Noise is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk5 = cfg.Bind(S, "Hotkey.Noise", "LeftAlt+LeftShift+Keypad6", "Turns the Noise label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad6); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[5] = hk5.Value;
            OnChanged(hk5, v => LayerHotkey(5, v));
            _showSet[6] = cfg.Bind(S, "Show.Waypoint", true, "Label layer Waypoint is on", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var hk6 = cfg.Bind(S, "Hotkey.Waypoint", "LeftAlt+LeftShift+Keypad7", "Turns the Waypoint label layer on or off: Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+Keypad7); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            c.LayerHotkeys[6] = hk6.Value;
            OnChanged(hk6, v => LayerHotkey(6, v));
            for (int i = 0; i < 7; i++)
            {
                int layer = i;
                c.Show[i] = _showSet[i].Value;
                OnChanged(_showSet[i], v => { if (_ctl != null) _ctl.ApplyShow((CiLayer)layer, v); else _s.Show[layer] = v; });
            }
        }

        private int _slotTick;

        public override void SelfCheck(ISelfCheck check) { check.Specs(ContactInfoSpecs.Set); }

        public override void Init(UiContext ctx)
        {
            _api.WireOwnership();   // T-120 M18: the OwnOnly members of the registry answer for the player's own objects only
            _slotTick = ctx.Guard.Register("contact_info.tick", ctx.Guard.OwnerOf(Id));
            try
            {
                if (!_s.Enabled) return;
                if (!_api.Capabilities.Contains("contact_info")) { ctx.Host.LogWarning("contact_info disabled: members of the game are missing"); return; }
                var radar = ctx.Get<IRadarView>();
                _labels = new ContactLabelLayer(_api, () => ctx.Get<IRadarView>(), 300, m => ctx.Host.LogInfo(m));
                var ctl = new ContactInfoController(_s, new GameContactSource(_api), _labels, new ModProbe(), ctx.Hotkeys,
                    m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
                ctl.PersistShow = (l, on) => { try { _showSet[(int)l].Value = on; } catch (Exception) { } };
                _ctl = ctl;
                ctx.Provide<IContactInfo>(ctl);
                _api.ContactInfo = ctl;
                _api.ContactInfoSwitch = new LayerSwitch(on => { ctl.Master = on ?? !ctl.Master; return ctl.Master; });
                _input = new UnityKeyInput(_api);
                _keyGame = new GameKeys(_api);
                BuildSpecs(ctl, ctx);
                if (radar == null) ctx.Host.LogInfo("contact_info: radar module is off, labels use their own map projection");
            }
            catch (Exception ex)
            {
                ctx.Host.LogError("contact_info init crashed: " + ex);
                _api.ContactInfo = null; _api.ContactInfoSwitch = null; _ctl = null; _specs = null;
            }
        }

        /// <summary>T-154 / ADR-0051: one map-layer entry "Contact info" = the master switch (see ContactInfo.Hotkey). Individual labels: Show.* / Hotkey.* / Settings window.</summary>
        private void BuildSpecs(ContactInfoController ctl, UiContext ctx)
        {
            int number = ctl.LayerNumbers[0];
            if (number < 0) { ctx.Host.LogWarning("contact_info: no free map layer number"); _specs = new WindowLayerSpec[0]; return; }
            if (number != ContactInfoMath.LayerPreferred[0]) ctx.Host.LogInfo("contact_info: layer moved to number " + number);
            _specs = new[]
            {
                new WindowLayerSpec
                {
                    Name = ContactInfoMath.LayerNames[0],
                    Number = number,
                    IsOpen = () => ctl.Master,
                    SetOpen = on => ctl.Master = on
                }
            };
        }

        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Update(UiFrame f)
        {
            if (_ctl == null) return;
            try
            {
                _ctl.Tick(f.RealTime, f.DeltaTime, _input, _keyGame);
                if (_specs != null) foreach (var s in _specs) _api.PumpWindowLayer(s);
                if (_labels != null && !_api.GameMenuHidden) _labels.Update();   // a game menu covers the map: no label work until it closes
            }
            catch (Exception e) { Ctx.Guard.Fail(_slotTick, e); }
        }

        public override void OnGUI()
        {
            if (_labels != null) _labels.OnGUI();
        }

        public override void Dispose()
        {
            try { _api.ContactInfo = null; _api.ContactInfoSwitch = null; if (_labels != null) _labels.Dispose(); } catch (Exception) { }
        }

        // ---- IUiDiag (ADR-0046): read-only snapshot of the published rows (tracks only)
        public string DiagSection { get { return "contacts"; } }

        public string DiagJson(int limit)
        {
            var sb = new StringBuilder("{\"contacts\":[");
            if (_ctl != null)
            {
                var keys = _ctl.TrackKeys;
                int n = 0;
                foreach (int k in keys)
                {
                    if (n >= limit) break;
                    var r = _ctl.Get(k);
                    if (r == null) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(k.ToString(CultureInfo.InvariantCulture));
                    Num(sb, "age_s", r.AgeS); Num(sb, "depth_ft", r.DepthFt); Num(sb, "speed_kn", r.SpeedKn);
                    Num(sb, "cpa_nm", r.CpaNm); Num(sb, "tcpa_s", r.TcpaS); Num(sb, "ttg_s", r.TtgS);
                    sb.Append('}');
                }
            }
            return sb.Append("]}").ToString();
        }

        private static void Num(StringBuilder sb, string name, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            sb.Append(",\"").Append(name).Append("\":").Append(v.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }
}
