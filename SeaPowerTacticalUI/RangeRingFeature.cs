using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-137 / ADR-0055: range rings, fire sectors and sonar zones on the tactical map (config section RangeRings): the scene is built
    /// from the facts of own units and, for an identified foreign unit, from the encyclopedia class only; drawn by RangeRingOverlay in the radar layer.</summary>
    internal sealed class RangeRingFeature : UiFeatureBase, IUiDiag, IUiSettingsSection, IRingLegend
    {
        private readonly GameApi _api;
        private readonly RangeRingParams _p = new RangeRingParams();
        private IUiSetting<bool> _showSet;
        private IUiSetting<string> _scopeSet;
        private RangeRingOverlay _ov;
        private GameRingFacts _facts;
        private IMapExtraLabels _labelSvc;
        private IKeyInput _input;
        private IHotkeyGame _keyGame;
        private HotkeyBinding _hk, _hkGroup;
        private WindowLayerSpec _spec;
        private CoverageLayer _layer;
        private bool _degradedLogged;
        private bool _btm, _ownActive = true;
        private readonly IModProbe _probe = new ModProbe();
        private const string GateOwn = "range_rings.own", GateEnemyOnly = "range_rings.enemy_only";
        private int _slot;
        private double _nextBuild;
        private bool _force;
        private LegendSwitch _legend = new LegendSwitch("rings", true);   // T-228
        private int _legendVer;
        private int _selUid, _selTrack, _goneSel = -1;
        private int _version;
        private readonly RangeRingPins _pins = new RangeRingPins();
        private int _pinsVer;
        private int _labelCount;
        private string _toast = "";
        private double _toastUntil;
        private static GUIStyle _toastStyle;

        public RangeRingFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "range_rings"; } }
        public int SettingsOrder { get { return 75; } }
        public override string[] DependsOn { get { return new[] { "text", "radar", "contact_info" }; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(RangeRingSpecs.Set); }

        public override void MemProbe(IDictionary<string, long> into)
        {
            if (_facts == null) return;
            into["range_rings.class"] = _facts.ClassCacheCount;
            into["range_rings.own"] = _facts.OwnCacheCount;
            into["range_rings.pins"] = _pins.Count;
            into["range_rings.pin_tracks"] = _pins.TrackCount;
            into["range_rings.rings"] = _ov != null && _ov.Scene != null ? _ov.Scene.Units.Count : 0;
        }

        private void Changed() { _force = true; }

        /// <summary>T-228: the legend switch for the map menu; null while the overlay is not there.</summary>
        public LegendSwitch Legend { get { return _ov != null ? _legend : null; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "RangeRings";
            var p = _p;
            p.Enabled = cfg.Bind(S, "Enabled", true, "Range rings of the checked weapons and sonar zones on the tactical map (needs the range_rings capability)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart, Gate = GateEnemyOnly, GateSoft = true }).Value;
            _showSet = cfg.Bind(S, "Show", false, "Range rings are shown on the map (the map layer entry Range rings): the checked weapons and the sonar rings", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Gate = GateEnemyOnly, GateSoft = true });
            p.Show = _showSet.Value;
            OnChanged(_showSet, v => { p.Show = v; SyncOverlayShow(); Changed(); });
            var hk = cfg.Bind(S, "Hotkey", "LeftAlt+G|LeftAlt+B", "Turns the range rings on or off: Unity KeyCode names joined by + (e.g. LeftAlt+G); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            p.Hotkey = hk.Value;
            OnChanged(hk, v => { p.Hotkey = v; if (_hk != null) _hk.Respec(v); });
            var hkg = cfg.Bind(S, "Hotkey.Group", "LeftAlt+LeftShift+B|LeftAlt+LeftShift+H", "Switches the range rings between the selected unit and the whole group (turns them on when off): Unity KeyCode names joined by + (e.g. LeftAlt+LeftShift+B); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live, Gate = GateOwn });
            p.HotkeyGroup = hkg.Value;
            OnChanged(hkg, v => { p.HotkeyGroup = v; if (_hkGroup != null) _hkGroup.Respec(v); });
            var own = cfg.Bind(S, "Own", "auto", "auto | on | off: own and allied rings; auto = off when the Better TacMap mod is found (it draws them)", UiKeyClass.Basic,
                new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "auto", "on", "off" } });
            p.Own = RangeRingMath.NormOwn(own.Value); OnChanged(own, v => { p.Own = RangeRingMath.NormOwn(v); RecomputeOwn(false); });
            _scopeSet = cfg.Bind(S, "Scope", "selected", "selected | group: the selected own unit or its whole group (formation)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "selected", "group" }, Gate = GateOwn });
            p.Scope = _scopeSet.Value == "group" ? "group" : "selected";
            OnChanged(_scopeSet, v => { p.Scope = v == "group" ? "group" : "selected"; Changed(); });
            var so = cfg.Bind(S, "Show.Sonar", false, "Sonar rings of the selected units (and their group): active and passive range; the passive range of the selected or pinned foreign contact", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Gate = GateOwn });
            p.Sonar = so.Value; OnChanged(so, v => { p.Sonar = v; Changed(); });
            var sa = cfg.Bind(S, "ShowAllies", false, "Rings of allied units as well (the selected or the group of the player only)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live, Gate = GateOwn });
            p.Allies = sa.Value; OnChanged(sa, v => { p.Allies = v; if (_facts != null) _facts.Allies = v; Changed(); });
            var sl = cfg.Bind(S, "Labels", true, "Range labels on the rings", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            p.Labels = sl.Value; OnChanged(sl, v => { p.Labels = v; Changed(); });
            var lg = cfg.Bind(S, "Legend", true, "Colour legend of the range rings in the top-right corner of the map, under the time panel, above the radar legend", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            p.Legend = lg.Value; _legend = new LegendSwitch("rings", lg.Value);
            OnChanged(lg, v => { p.Legend = v; _legend.SetCfg(v); Changed(); });
            var op = cfg.Bind(S, "Opacity", 0.85, "Opacity of the rings 0.3..1", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.3, Max = 1, Step = 0.05 });
            p.Opacity = Math.Max(0.3, Math.Min(1, op.Value)); OnChanged(op, v => { p.Opacity = Math.Max(0.3, Math.Min(1, v)); if (_ov != null) _ov.Style.Opacity = p.Opacity; Changed(); });
            var fa = cfg.Bind(S, "FillAlpha", 0.12, "Fill of the weapon rings and sectors 0..0.4 (0 = outline only); overlapping fills are capped", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 0.4, Step = 0.02 });
            p.FillAlpha = RangeRingMath.ClampFill(fa.Value); OnChanged(fa, v => { p.FillAlpha = RangeRingMath.ClampFill(v); if (_ov != null) _ov.Style.FillAlpha = p.FillAlpha; Changed(); });
            var mr = cfg.Bind(S, "MaxRings", 24, "Rings and sectors at most per redraw", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.MaxRings = Math.Max(1, mr.Value); OnChanged(mr, v => { p.MaxRings = Math.Max(1, v); Changed(); });
            var ms = cfg.Bind(S, "MaxSegments", 3000, "Line segments at most per redraw", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.MaxSegments = Math.Max(100, ms.Value); OnChanged(ms, v => { p.MaxSegments = Math.Max(100, v); if (_ov != null) _ov.Style.MaxSegments = p.MaxSegments; Changed(); });
            var rs = cfg.Bind(S, "RefreshS", 1.0, "Redraw period at rest (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.RefreshS = Math.Max(0.25, rs.Value); OnChanged(rs, v => p.RefreshS = Math.Max(0.25, v));
            var ar = cfg.Bind(S, "AirRefreshS", 0.25, "Redraw period while an aircraft is in the scene (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.AirRefreshS = Math.Max(0.1, ar.Value); OnChanged(ar, v => p.AirRefreshS = Math.Max(0.1, v));
            p.MinRadiusPx = Math.Max(1.0, cfg.Bind(S, "MinRadiusPx", 8.0, "Rings smaller than this many pixels are not drawn", UiKeyClass.Internal).Value);
            p.FactsRefreshS = Math.Max(0.5, cfg.Bind(S, "FactsRefreshS", 2.0, "Period of reading the armament of a unit (s)", UiKeyClass.Internal).Value);
            p.WeaponListMax = Math.Max(1, cfg.Bind(S, "WeaponListMax", 10, "Weapons at most in the weapon list of a unit", UiKeyClass.Internal).Value);
            p.LabelMinPx = Math.Max(0.0, cfg.Bind(S, "LabelMinPx", 40.0, "Rings smaller than this many pixels get no label", UiKeyClass.Internal).Value);
        }

        private sealed class RrKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public RrKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            _slot = ctx.Guard.Register("range_rings.tick", ctx.Guard.OwnerOf(Id));
            ctx.Provide<IRingLegend>(this);   // T-228
            ctx.Provide<RangeRingParams>(_p);   // T-162: the menu patch is installed also when only the rings are on
            try
            {
                if (!_p.Enabled) { ctx.Host.LogInfo("range_rings off ([RangeRings] Enabled=false)"); return; }
                if (!_api.Capabilities.Contains("range_rings")) { ctx.Host.LogWarning("range_rings disabled: members of the game are missing"); return; }
                var radar = ctx.Get<IRadarView>();
                _layer = radar == null ? null : radar.Layer;
                _facts = new GameRingFacts(_api, m => ctx.Host.LogInfo(m)) { Allies = _p.Allies, RefreshS = _p.FactsRefreshS };
                _ov = new RangeRingOverlay(m => ctx.Host.LogInfo(m)) { Show = _p.Show };
                _ov.Style.Opacity = _p.Opacity; _ov.Style.FillAlpha = _p.FillAlpha; _ov.Style.MaxSegments = _p.MaxSegments; _ov.Style.MinRadiusPx = _p.MinRadiusPx;
                _labelSvc = ctx.Get<IContactInfo>() as IMapExtraLabels;
                if (_layer != null) _layer.Extra.Add(_ov);
                else LogDegraded();
                _input = new UnityKeyInput(_api);
                _keyGame = new RrKeys(_api);
                _hk = new HotkeyBinding("range_rings", "RangeRings.Hotkey", _p.Hotkey, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
                _hkGroup = new HotkeyBinding("range_rings", "RangeRings.Hotkey.Group", _p.HotkeyGroup, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
                int number = ContactInfoMath.PickLayerNumbers(new[] { 217 }, _api.CiTakenLayerNumbers(), 217, 229)[0];
                if (number < 0) ctx.Host.LogWarning("range_rings: no free map layer number");
                else
                {
                    if (number != 217) ctx.Host.LogInfo("range_rings: layer moved to number " + number);
                    _spec = new WindowLayerSpec { Name = "VOICEBOT_RANGE_RINGS", Number = number, IsOpen = () => _p.Show, SetOpen = on => SetShow(on, false) };
                }
                RecomputeOwn(true);
                ctx.Settings.SetGate(GateOwn, () => _ownActive ? null : (_p.Own == "off" ? "settings.rangerings.own.note_off" : "settings.rangerings.own.btm"));
                ctx.Settings.SetGate(GateEnemyOnly, () => _ownActive ? null : "settings.rangerings.own.enemy_only");
                ctx.Mission.Subscribe(80, "range_rings", e => RecomputeOwn(true), why => ClearAll());   // contact_info (65) has loaded Better TacMap by now
                var svc = new RingMenuService(this);
                _api.RingMenu = svc;
                _api.RangeRingsSwitch = new LayerSwitch(on => { SetShow(on ?? !_p.Show, false); return _p.Show; });
                ctx.Provide<IRingMenu>(svc);
            }
            catch (Exception e)
            {
                ctx.Host.LogError("range_rings init crashed: " + e);
                RemoveOverlay(); _ov = null; _facts = null; _spec = null; _api.RingMenu = null; _api.RangeRingsSwitch = null;
            }
        }

        /// <summary>T-164 / ADR-0066: own and allied rings are off when Own=off, or Own=auto and Better TacMap (which draws them) is found.</summary>
        private void RecomputeOwn(bool probe)
        {
            try
            {
                if (probe) { try { _btm = _probe.HarmonyHas(ContactInfoMath.BtmHarmonyId); } catch (Exception) { _btm = false; } }
                bool now = RangeRingMath.OwnActive(_p.Own, _btm);
                if (now == _ownActive) return;
                _ownActive = now;
                if (!now) _pins.ClearOwn();   // Update sees the Version and syncs the overlay
                _force = true;
                string m;
                if (!now && _btm && _p.Own == "auto") m = "rings: Better TacMap detected — own rings off (RangeRings.Own=on to force)";
                else if (now && _btm && _p.Own == "on") m = "rings: Better TacMap detected — RangeRings.Own=on, own rings stay on";
                else m = "rings: own rings " + (now ? "on" : "off") + " (RangeRings.Own=" + _p.Own + ")";
                Ctx.Host.LogInfo(m);
            }
            catch (Exception e) { Ctx.Host.LogWarning("range_rings: own recompute failed: " + e.Message); }
        }

        private void LogDegraded()
        {
            if (_degradedLogged) return;
            _degradedLogged = true;
            Ctx.Host.LogWarning("range_rings: " + UiText.Get("rr.degraded"));
        }

        private void RemoveOverlay()
        {
            try { if (_layer != null && _ov != null) _layer.Extra.Remove(_ov); } catch (Exception) { }
        }

        private void ClearAll()
        {
            try
            {
                _pins.Clear();
                if (_ov != null) { _ov.Scene = null; }
                if (_facts != null) _facts.Clear();
                _api.RrLeaveMission();
                SetLabels(null);
                _force = true; _selUid = 0; _selTrack = -1; _goneSel = -1; _nextBuild = 0;
            }
            catch (Exception e) { Ctx.Host.LogWarning("range_rings clear failed: " + e.Message); }
        }

        private void SetShow(bool on, bool persist)
        {
            if (_p.Show == on) return;
            _p.Show = on;
            SyncOverlayShow();
            _force = true;
            if (persist) { try { _showSet.Value = on; } catch (Exception) { } }
        }

        /// <summary>2026-10-22: the layer switch (map layer entry / hotkey) shows or hides every ring, the checked weapons included.</summary>
        private void SyncOverlayShow() { if (_ov != null) _ov.Show = _p.Show; }

        private void Toast(string key)
        {
            _toast = UiText.Get(key);
            _toastUntil = Time.unscaledTime + 2.0;
            Ctx.Host.LogInfo("range_rings: " + _toast);
        }

        public override void Update(UiFrame f)
        {
            if (_ov == null) return;
            try
            {
                bool inMission = _api.Frame.InMission;
                if (_layer == null) { var r = Ctx.Get<IRadarView>(); var l = r == null ? null : r.Layer; if (l != null) { _layer = l; l.Extra.Add(_ov); } }
                if (inMission) Keys(f.RealTime);
                if (_spec != null) _api.PumpWindowLayer(_spec);
                if (_pins.Version != _pinsVer) { _pinsVer = _pins.Version; SyncOverlayShow(); _force = true; }
                if (!inMission || !_p.Show)
                {
                    if (_ov.Scene != null) { _ov.Scene = null; _ov.RequestDraw(f.RealTime); }
                    SetLabels(null);
                    return;
                }
                if (_layer == null && _p.Show) LogDegraded();
                int uid = _api.RrSelectedOwnUid(), tr = _api.RrSelectedTrack();
                if (uid != _selUid || tr != _selTrack) { _selUid = uid; _selTrack = tr; _facts.Invalidate(); _force = true; }
                if (_legend.Version != _legendVer) { _legendVer = _legend.Version; _force = true; }   // T-228
                if (_facts.KinPump(f.RealTime)) _force = true;   // T-321: at most one game call per frame (the range of own weapons, the verdict)
                if (!_force && f.RealTime < _nextBuild) return;
                _force = false;
                Rebuild(f.RealTime);
            }
            catch (Exception e) { Ctx.Guard.Fail(_slot, e); }
        }

        private void Keys(double now)
        {
            _hk.Resolve(now, true, _keyGame);
            _hkGroup.Resolve(now, true, _keyGame);
            bool grp = _hkGroup.Pressed(_input);
            bool one = !grp && _hk.Pressed(_input);   // the single key also matches while LeftShift is held: the group key wins
            if (one)
            {
                SetShow(!_p.Show, true);
                // 2026-10-22: nothing checked anywhere and no sonar rings -> the layer draws nothing; the toast says how to pick weapons
                string tk = !_p.Show ? "rr.toast.off" : (_pins.WeaponCount > 0 || _p.Sonar || _pins.OverrideCount > 0) ? "rr.toast.on" : "rr.toast.on_empty";
                Toast(tk);
            }
            else if (grp && _ownActive)
            {
                string scope;
                if (!_p.Show) { SetShow(true, true); scope = "group"; }
                else scope = _p.Scope == "group" ? "selected" : "group";
                _p.Scope = scope;
                try { _scopeSet.Value = scope; } catch (Exception) { }
                _force = true;
                string gk = scope == "group" ? "rr.toast.scope_group" : "rr.toast.scope_selected";
                Toast(gk);
            }
        }

        private RingOptions Options()
        {
            var o = new RingOptions
            {
                Scope = _p.Scope, Sonar = _p.Sonar, Allies = _p.Allies, MaxRings = _p.MaxRings,
                Pins = _pins, OwnActive = _ownActive
            };
            return o;
        }

        private void Rebuild(double now)
        {
            RingOptions opts = Options();
            _facts.CourseMinKn = opts.CourseMinKn;
            _facts.BeginTick(now);
            // a destroyed own unit is Other for SideOf (RrSideOf reads IsDestroyed): its pin and overrides go here
            var unitsGone = new List<int>();
            if (_pins.Prune(uid => { if (!_ownActive) return false; var s = _facts.SideOf(uid); bool keep = s == UnitSide.Player || (s == UnitSide.Ally && _p.Allies); if (!keep) unitsGone.Add(uid); return keep; }) > 0) { _pinsVer = _pins.Version; SyncOverlayShow(); }
            foreach (int uid in unitsGone) Ctx.Host.LogInfo("rings: unit gone uid=" + uid + " reason=" + (_ownActive ? "destroyed_or_side" : "own_off"));
            // 2026-10-22: a destroyed / lost foreign track loses its pin and weapons at once; the selected contact draws nothing (the model asks TrackGone)
            foreach (string gone in RangeRingModel.PruneGoneTracks(_facts, _pins)) Ctx.Host.LogInfo("rings: track gone " + gone);
            int selTrack = _api.RrSelectedTrack();
            if (selTrack >= 0 && selTrack != _goneSel)
            {
                string why = _facts.TrackGone(selTrack);
                if (why != null) { _goneSel = selTrack; Ctx.Host.LogInfo("rings: track gone track=" + selTrack + " reason=" + why + " (selected contact)"); }
            }
            foreach (string gone in RangeRingModel.PruneWeapons(_facts, _pins, opts)) Ctx.Host.LogInfo("rings: weapon gone " + gone);
            _pinsVer = _pins.Version;
            SyncOverlayShow();
            RingScene scene = RangeRingModel.Build(_facts, opts);
            scene.Version = ++_version;
            _ov.Scene = scene;
            _ov.RequestDraw(now);
            _nextBuild = RangeRingMath.NextDue(now, scene.AnyAir, _p.RefreshS, _p.AirRefreshS);
            PublishLabels(scene);
            PublishLegend(scene);
        }

        /// <summary>ADR-0068 p. 6: the colour legend of the kinds present in the scene; the style line only when a class ring is there.</summary>
        private void PublishLegend(RingScene scene)
        {
            if (_ov == null) return;
            if (!_legend.Visible) { _ov.Legend = null; return; }
            bool own, cls;
            List<KeyValuePair<RingKind, RingVia>> keys = RangeRingReach.LegendKeys(scene, out own, out cls);
            if (!own && !cls) { _ov.Legend = null; return; }
            var lines = new List<RtLegendLine> { new RtLegendLine { Text = UiText.Get("rr.legend.title"), Bold = true } };
            foreach (var k in keys)
            {
                string text = UiText.Get(NounKey(new RingLabel { Kind = k.Key, Via = k.Value }));
                uint argb = RangeRingMath.Pack(k.Key, k.Value, 1.0);
                // two delivery kinds with the same noun and colour (ASROC ring + Mk-46 sector: "ASW") make one line
                if (lines.Exists(l => l.Text == text && l.Argb == argb)) continue;
                lines.Add(new RtLegendLine { Text = text, Argb = argb });
            }
            // ADR-0068 supplement 2026-10-22: every checked weapon by its name in its own colour / shade
            foreach (RingLabel w in RangeRingReach.LegendWeapons(scene))
            {
                uint argb = RangeRingMath.Pack(w.Kind, w.Via, 1.0, w.Tint);
                if (lines.Exists(l => l.Text == w.Weapon && l.Argb == argb)) continue;
                lines.Add(new RtLegendLine { Text = w.Weapon, Argb = argb });
            }
            if (cls) lines.Add(new RtLegendLine { Text = UiText.Get("rr.legend.style") });
            _ov.Legend = lines;
        }

        // ---- labels (IMapExtraLabels, owner "range_rings"): up to 24, each kind at its own bearing, a wedge-only reach at the middle of its widest wedge ----
        private void SetLabels(IList<RtTextItem> items)
        {
            _labelCount = items == null ? 0 : items.Count;
            if (_ov != null) _ov.Texts = items;
        }

        /// <summary>The noun of a ring without a weapon name: the sonar kind (every weapon ring carries its weapon's name).</summary>
        internal static string NounKey(RingLabel l)
        {
            return l.Kind == RingKind.SonarActive ? "rr.kind.sonar_active" : "rr.kind.sonar_passive";
        }

        private string Dist(double nm)
        {
            return _labelSvc != null ? _labelSvc.FmtDistance(nm) : nm.ToString("0.#", CultureInfo.InvariantCulture) + " nm";
        }

        private void PublishLabels(RingScene scene)
        {
            if (!_p.Labels) { SetLabels(null); return; }
            var items = new List<RtTextItem>();
            foreach (RingLabel l in RangeRingModel.Labels(scene, _ov == null ? 0 : _ov.LastPxPerNm, _p.LabelMinPx, 24))
            {
                string dist = Dist(l.Nm);
                if (l.UpTo) dist = UiText.Fmt("rr.upto", dist);
                string text = UiText.Fmt("rr.label", string.IsNullOrEmpty(l.Weapon) ? UiText.Get(NounKey(l)) : l.Weapon, dist);
                items.Add(new RtTextItem { Lat = l.Lat, Lon = l.Lon, Text = text, Argb = RangeRingMath.Pack(l.Kind, l.Via, 1.0, l.Tint) });
            }
            PublishVerdicts(scene, items);
            SetLabels(items.Count == 0 ? null : items);
        }

        /// <summary>T-321: up to MaxVerdicts lines at the selected contact: what the launcher of the game says about each checked weapon (miles of the game).</summary>
        private void PublishVerdicts(RingScene scene, List<RtTextItem> items)
        {
            int line = 0;
            foreach (RingVerdict v in scene.Verdicts)
            {
                if (items.Count >= 24) break;
                string key = v.Verdict == KinVerdict.TooFar ? "rr.verdict.too_far" : v.Verdict == KinVerdict.TooClose ? "rr.verdict.too_close" : "rr.verdict.in";
                string a = Dist(v.DistNm), b = Dist(v.Verdict == KinVerdict.TooClose ? v.MinNm : v.MaxNm);
                items.Add(new RtTextItem { Lat = v.At.Lat, Lon = v.At.Lon, Text = UiText.Fmt(key, v.Weapon, a, b), Argb = VerdictColor(scene, v), DyPx = 14f * (line + 1) });
                line++;
            }
        }

        private static uint VerdictColor(RingScene scene, RingVerdict v)
        {
            foreach (UnitRings u in scene.Units)
            {
                if (u.Key != v.Uid) continue;
                foreach (RingSpec r in u.Rings) if (r.Weapon == v.Weapon && !r.IsMin) return RangeRingMath.Pack(r.Kind, r.Via, 1.0, r.Tint);
                foreach (SectorSpec c in u.Sectors) if (c.Weapon == v.Weapon && !c.IsMin) return RangeRingMath.Pack(c.Kind, c.Via, 1.0, c.Tint);
            }
            return 0xFFFFFFFFu;
        }

        public override void OnGUI()
        {
            if (_toastUntil <= 0 || Time.unscaledTime > _toastUntil || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_toastStyle == null) _toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUI.Box(new Rect(Screen.width * 0.5f - 160, Screen.height * 0.12f, 320, 30), _toast, _toastStyle);
        }

        public override void Dispose()
        {
            try { RemoveOverlay(); if (_ov != null) _ov.Dispose(); } catch (Exception) { }
            _api.RingMenu = null; _api.RangeRingsSwitch = null;
        }

        /// <summary>T-160 / 2026-10-22: the range-ring items of the XO menu (the weapon check boxes and the sonar rings of one unit) and the weapon list of
        /// an identified foreign contact.</summary>
        private sealed class RingMenuService : IRingMenu
        {
            private readonly RangeRingFeature _f;
            public RingMenuService(RangeRingFeature f) { _f = f; }

            private string Dist(double nm) { return _f.Dist(nm); }

            /// <summary>Checking something turns the layer on (the layer switch shows or hides every ring).</summary>
            private void Changed(bool on)
            {
                var f = _f;
                if (on && !f._p.Show) f.SetShow(true, true);
                f.SyncOverlayShow();
                f._force = true;
            }

            public RingMenuView View(int uid)
            {
                var f = _f;
                if (!f._ownActive || f._ov == null || f._facts == null || uid == 0 || !f._api.Frame.InMission || f._api.RrSideOf(uid) != UnitSide.Player) return null;
                OwnArmament arm = f._api.RrOwn(uid, false);
                var v = new RingMenuView { Sonar = f._pins.SonarOn(uid, f._p.Sonar), HasSonar = RangeRingModel.HasSonar(arm) };
                v.Checked.AddRange(f._pins.Weapons(uid));
                foreach (WeaponOpt w in RangeRingReach.OwnWeapons(arm, f._p.WeaponListMax))
                {
                    double reach = f._facts.ReachNm(uid, new AmmoFact { Name = w.Key });   // T-321: the game's radius when known, else the nominal one
                    v.Weapons.Add(new RingWeaponChoice { Key = w.Key, Label = UiText.Fmt("menu.rings_weapon_item", w.Name, Dist(!double.IsNaN(reach) && reach > 0 ? reach : w.MaxNm)) });
                }
                return v;
            }

            public RingContactItem ContactItem(object targetObject)
            {
                var f = _f;
                if (f._ov == null || f._facts == null || targetObject == null || !f._api.Frame.InMission) return null;
                int track = f._api.RrMenuTrack(targetObject);
                if (RangeRingModel.PinTarget(f._facts, track) != RingPinTarget.Track) return null;
                try
                {
                    var cw = new RingContactWeapons();
                    cw.Checked.AddRange(f._pins.TrackWeapons(track));
                    foreach (WeaponOpt w in RangeRingReach.ClassWeapons(f._facts.Class(track), f._p.WeaponListMax))
                        cw.Weapons.Add(new RingWeaponChoice { Key = w.Key, Label = UiText.Fmt("menu.rings_contact_weapon_item", w.Name, Dist(w.MaxNm)) });
                    if (cw.Weapons.Count == 0) return null;   // nothing to choose: no item
                    return new RingContactItem { Weapons = cw, ChooseWeapon = key => ChooseTrackWeapon(track, key) };
                }
                catch (Exception e) { f.Ctx.Host.LogWarning("range_rings: weapon list failed: " + e.GetType().Name); return null; }
            }

            /// <summary>A weapon of a foreign track: toggles it (several at once); "" = clear all. A check is re-validated at the click: still an identified
            /// foreign track and the weapon is in its class (an uncheck is always allowed).</summary>
            private void ChooseTrackWeapon(int track, string key)
            {
                var f = _f;
                if (f._facts == null) return;
                bool on = false;
                if (string.IsNullOrEmpty(key))
                {
                    if (f._pins.TrackWeapons(track).Count == 0) return;
                    f._pins.ClearTrackWeapons(track);
                }
                else if (f._pins.HasTrackWeapon(track, key)) f._pins.ToggleTrackWeapon(track, key);
                else
                {
                    if (RangeRingModel.PinTarget(f._facts, track) != RingPinTarget.Track) return;
                    bool found = false;
                    foreach (WeaponOpt w in RangeRingReach.ClassWeapons(f._facts.Class(track), int.MaxValue)) if (w.Key == key) { found = true; break; }
                    if (!found) return;
                    on = f._pins.ToggleTrackWeapon(track, key);
                }
                Changed(on);
                f.Ctx.Host.LogInfo("range_rings: menu weapon track=" + track + " " + (string.IsNullOrEmpty(key) ? "-> cleared" : key + " -> " + (on ? "on" : "off")));
            }

            public bool ChooseWeapon(int uid, string key)
            {
                var f = _f;
                if (uid == 0 || !f._ownActive || f._facts == null) return false;
                // defence in depth: only the player's own unit, and the weapon must be one it can fire now
                if (f._api.RrSideOf(uid) != UnitSide.Player) return false;
                bool on = false;
                if (string.IsNullOrEmpty(key)) f._pins.ClearWeapons(uid);
                else if (f._pins.HasWeapon(uid, key)) f._pins.ToggleWeapon(uid, key);   // uncheck: always allowed
                else
                {
                    bool found = false;
                    foreach (WeaponOpt w in RangeRingReach.OwnWeapons(f._api.RrOwn(uid, false), int.MaxValue)) if (w.Key == key) { found = true; break; }
                    if (!found) return false;
                    on = f._pins.ToggleWeapon(uid, key);
                }
                Changed(on);
                f.Ctx.Host.LogInfo("range_rings: menu weapon uid=" + uid + " " + (string.IsNullOrEmpty(key) ? "-> cleared" : key + " -> " + (on ? "on" : "off")));
                return on;
            }

            public bool ToggleSonar(int uid)
            {
                var f = _f;
                // defence in depth: an override is set only on the player's own unit (an existing pin may always be removed)
                if (uid == 0) return false;
                if (!f._pins.IsPinned(uid) && (!f._ownActive || f._api.RrSideOf(uid) != UnitSide.Player)) return false;
                bool on = f._pins.ToggleSonar(uid, f._p.Sonar);
                Changed(on);
                f.Ctx.Host.LogInfo("range_rings: menu sonar uid=" + uid + " -> " + (on ? "on" : "off"));
                return on;
            }
        }

        // ---- IUiDiag (ADR-0046): section "rings"
        public string DiagSection { get { return "rings"; } }

        public string DiagJson(int limit)
        {
            var sb = new StringBuilder("{");
            var inv = CultureInfo.InvariantCulture;
            sb.Append("\"show\":").Append(_p.Show ? "true" : "false").Append(",\"scope\":\"").Append(_p.Scope).Append('"');
            var sc = _ov == null ? null : _ov.Scene;
            int rings = 0;
            if (sc != null) foreach (var u in sc.Units) rings += u.Rings.Count + u.Sectors.Count;
            sb.Append(",\"units\":").Append(sc == null ? 0 : sc.Units.Count).Append(",\"rings\":").Append(rings);
            sb.Append(",\"segs\":").Append(_ov == null ? 0 : _ov.LastStats.Segments).Append(",\"cut\":").Append(sc == null ? 0 : sc.RingsCut + (_ov == null ? 0 : _ov.LastStats.SegmentsCut));
            sb.Append(",\"tris\":").Append(_ov == null ? 0 : _ov.LastStats.Tris).Append(",\"fill_alpha\":").Append((_ov == null ? 0 : _ov.LastStats.FillAlpha).ToString("0.###", inv));
            sb.Append(",\"ms_avg\":").Append((_ov == null ? 0 : _ov.MsAvg).ToString("0.###", inv)).Append(",\"ms_max\":").Append((_ov == null ? 0 : _ov.MsMax).ToString("0.###", inv));
            sb.Append(",\"pins\":").Append(_pins.Count).Append(",\"pin_tracks\":").Append(_pins.TrackCount).Append(",\"overrides\":").Append(_pins.OverrideCount).Append(",\"weapons\":").Append(_pins.WeaponCount).Append(",\"labels\":").Append(_labelCount);
            sb.Append(",\"legend\":").Append(_legend.DiagJson());
            sb.Append(",\"own\":\"").Append(_p.Own).Append("\",\"own_active\":").Append(_ownActive ? "true" : "false").Append(",\"btm\":").Append(_btm ? "true" : "false");
            sb.Append(",\"class_cache\":").Append(_facts == null ? 0 : _facts.ClassCacheCount);
            sb.Append(",\"class_weapons\":").Append(_facts == null ? "[]" : _facts.ClassDiagJson());
            sb.Append(",\"kin\":").Append(_facts == null ? "null" : _facts.KinDiagJson());   // T-321: cached numbers only
            return sb.Append('}').ToString();
        }
    }

    /// <summary>IRingFacts over GameApi. Per-tick caches (side, pose, group: the game looks a unit up linearly by UniqueID), the armament of a unit every
    /// FactsRefreshS (and at once when the selection changes), the class numbers by IniRef (a missing class is cached as null). The contract is guarded
    /// here as well: Own() throws for a foreign side, Class() throws before IsIdentified.</summary>
    internal sealed partial class GameRingFacts : IRingFacts
    {
        private sealed class Armed { public OwnArmament A; public double At; }
        private struct Pose { public bool Ok; public RingPt P; public double H; public bool Air; }

        private readonly GameApi _api;
        private readonly Action<string> _log;
        private readonly Dictionary<int, UnitSide> _side = new Dictionary<int, UnitSide>();
        private readonly Dictionary<int, Pose> _pose = new Dictionary<int, Pose>();
        private readonly Dictionary<int, IList<int>> _group = new Dictionary<int, IList<int>>();
        private readonly Dictionary<int, Armed> _own = new Dictionary<int, Armed>();
        private readonly Dictionary<string, ClassRanges> _class = new Dictionary<string, ClassRanges>();
        private readonly Dictionary<int, string> _ini = new Dictionary<int, string>();
        private readonly HashSet<int> _identified = new HashSet<int>();
        private readonly Dictionary<int, double?> _course = new Dictionary<int, double?>();
        private sealed class ClassSrc { public List<ClassWeapon> Ws; public List<double> Passive; public double At; }
        private readonly Dictionary<string, ClassSrc> _classSrc = new Dictionary<string, ClassSrc>();
        private readonly HashSet<string> _arcsLogged = new HashSet<string>(), _weaponsLogged = new HashSet<string>();
        private double _now;
        private bool _allies;

        public double RefreshS = 2.0;
        /// <summary>Below this speed (kn) a track has no known course.</summary>
        public double CourseMinKn = 1.0;
        public bool Allies { get { return _allies; } set { if (_allies != value) { _allies = value; _own.Clear(); } } }
        public int ClassCacheCount { get { return _class.Count; } }
        public int OwnCacheCount { get { return _own.Count; } }

        public GameRingFacts(GameApi api, Action<string> log) { _api = api; _log = log ?? (m => { }); }

        public void BeginTick(double now) { _now = now; _side.Clear(); _pose.Clear(); _group.Clear(); _identified.Clear(); _course.Clear(); _gone.Clear(); _rescanned = false; }
        public void Invalidate() { _own.Clear(); }

        private bool _rescanned;
        /// <summary>T-162: is the track still in the player's plotting table (one rescan per tick).</summary>
        public bool TrackLive(int trackId)
        {
            if (!_rescanned) { _rescanned = true; _api.RrRescanTracks(); }
            return _api.RrHasTrack(trackId);
        }
        private readonly Dictionary<int, string> _gone = new Dictionary<int, string>();
        /// <summary>2026-10-22: null = the track is there, else "lost" / "destroyed" (one rescan of the table per tick, cached for the tick).</summary>
        public string TrackGone(int trackId)
        {
            string why;
            if (_gone.TryGetValue(trackId, out why)) return why;
            why = TrackLive(trackId) ? _api.RrTrackGone(trackId) : "lost";
            _gone[trackId] = why;
            return why;
        }
        public void Clear() { _gone.Clear(); _side.Clear(); _pose.Clear(); _group.Clear(); _own.Clear(); _class.Clear(); _ini.Clear(); _identified.Clear(); _course.Clear(); _classSrc.Clear(); _arcsLogged.Clear(); _weaponsLogged.Clear(); ClearKin(); }

        public IList<int> SelectedOwn()
        {
            int uid = _api.RrSelectedOwnUid();
            return uid == 0 ? new int[0] : new[] { uid };
        }

        public IList<int> GroupOf(int uid)
        {
            IList<int> g;
            if (_group.TryGetValue(uid, out g)) return g;
            g = _api.RrGroupOf(uid);
            _group[uid] = g;
            return g;
        }

        public UnitSide SideOf(int uid)
        {
            UnitSide s;
            if (_side.TryGetValue(uid, out s)) return s;
            s = _api.RrSideOf(uid);
            _side[uid] = s;
            return s;
        }

        public OwnArmament Own(int uid)
        {
            UnitSide s = SideOf(uid);
            if (s != UnitSide.Player && !(s == UnitSide.Ally && _allies)) throw new InvalidOperationException("Own() of a foreign unit");
            Armed a;
            if (_own.TryGetValue(uid, out a) && _now - a.At < RefreshS * (1.0 + (uid & 3) * 0.1)) return a.A;
            var arm = _api.RrOwn(uid, _allies);
            _own[uid] = new Armed { A = arm, At = _now };
            return arm;
        }

        public bool OwnPose(int uid, out RingPt p, out double headingDeg, out bool air)
        {
            Pose ps;
            if (!_pose.TryGetValue(uid, out ps))
            {
                ps = new Pose();
                ps.Ok = _api.RrOwnPose(uid, _allies, out ps.P, out ps.H, out ps.Air);
                _pose[uid] = ps;
            }
            p = ps.P; headingDeg = ps.H; air = ps.Air;
            return ps.Ok;
        }

        public int SelectedContact() { return _api.RrSelectedTrack(); }
        public UnitSide TrackSide(int trackId) { return _api.RrTrackSide(trackId); }

        public bool IsIdentified(int trackId)
        {
            if (_identified.Contains(trackId)) return true;   // cached for one tick only (BeginTick clears it): a lost identification is seen (T-162)
            bool r = _api.RrIsIdentified(trackId);
            if (r) _identified.Add(trackId);
            return r;
        }

        public bool TrackPose(int trackId, out RingPt p, out bool air) { return _api.RrTrackPose(trackId, out p, out air); }

        /// <summary>The course of the player's track (cached for a tick). Only for an identified track, like Class().</summary>
        public bool TrackCourse(int trackId, out double courseDeg)
        {
            if (!IsIdentified(trackId)) throw new InvalidOperationException("TrackCourse() before identification");
            double? c;
            if (!_course.TryGetValue(trackId, out c))
            {
                double d;
                c = _api.RrTrackCourse(trackId, CourseMinKn, out d) ? (double?)d : null;
                _course[trackId] = c;
            }
            courseDeg = c ?? 0;
            return c.HasValue;
        }

        /// <summary>A class whose ini was not read is asked again after this many seconds (the loader may not have been ready).</summary>
        private const double ArcsRetryS = 10.0;

        public ClassRanges Class(int trackId)
        {
            if (!IsIdentified(trackId)) throw new InvalidOperationException("Class() before identification");
            string ini;
            if (!_ini.TryGetValue(trackId, out ini))
            {
                ini = _api.RrClassIniName(trackId);
                if (string.IsNullOrEmpty(ini)) return null;
                _ini[trackId] = ini;
            }
            ClassRanges cr;
            ClassSrc src;
            if (_class.TryGetValue(ini, out cr))
            {
                if (cr == null || cr.ArcsKnown || !_classSrc.TryGetValue(ini, out src) || _now - src.At < ArcsRetryS) return cr;
                return _class[ini] = Build(ini, src);   // retry the class ini only
            }
            if (!_api.RrEncyclopediaReady()) return null;   // not cached: asked again later
            var ws = new List<ClassWeapon>();
            var passive = new List<double>();
            if (_api.RrClassEntry(ini, ws, passive)) cr = Build(ini, _classSrc[ini] = new ClassSrc { Ws = ws, Passive = passive });
            else _log("range_rings: rr.class_missing " + ini);
            _class[ini] = cr;
            return cr;
        }

        /// <summary>Diagnostics: the classes read so far with every weapon and why it is in the weapon list or not.</summary>
        public string ClassDiagJson()
        {
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (var kv in _class)
            {
                if (kv.Value == null) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"ini\":\"").Append(kv.Key).Append("\",\"arcs\":").Append(kv.Value.ArcsKnown ? "true" : "false").Append(",\"weapons\":[");
                int m = 0;
                foreach (string line in RangeRingReach.ClassWeaponWhy(kv.Value))
                {
                    if (m++ > 0) sb.Append(',');
                    sb.Append('"').Append(line.Replace("\\", "/").Replace("\"", "'")).Append('"');
                    if (m >= 24) break;
                }
                sb.Append("]}");
                if (n >= 6) break;
            }
            return sb.Append(']').ToString();
        }

        private ClassRanges Build(string ini, ClassSrc src)
        {
            var mounts = new List<ClassMount>();
            bool known = _api.RrClassMounts(ini, mounts);
            src.At = _now;
            if (!known && _arcsLogged.Add(ini)) _log("rings: class arcs unknown " + ini);
            ClassRanges built = RangeRingModel.BuildClass(ini, src.Ws, src.Passive, mounts, known);
            if (_weaponsLogged.Add(ini)) _log("rings: class " + ini + " weapons: " + string.Join("; ", RangeRingReach.ClassWeaponWhy(built).ToArray()));
            return built;
        }
    }

    internal sealed class RangeRingParams
    {
        // 2026-10-22 (customer): no category rings and no fire-sector layer; weapon rings only for the checked weapons, sonar rings off by default
        public bool Enabled = true, Show, Sonar, Allies, Labels = true, Legend = true;
        public string Own = "auto";
        public string Scope = "selected", Hotkey = "LeftAlt+G|LeftAlt+B", HotkeyGroup = "LeftAlt+LeftShift+B|LeftAlt+LeftShift+H";
        public double Opacity = 0.85, FillAlpha = 0.12, RefreshS = 1.0, AirRefreshS = 0.25, MinRadiusPx = 8.0, FactsRefreshS = 2.0, LabelMinPx = 40.0;
        public int MaxRings = 24, MaxSegments = 3000, WeaponListMax = 10;
    }
}
