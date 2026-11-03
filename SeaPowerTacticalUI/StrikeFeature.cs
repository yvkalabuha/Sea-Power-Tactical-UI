using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-341 / ADR-0132 (phases A, B): the strike module. It creates the controller over the game layer, registers the participant "strike" in the engage veto chain, reports the section
    /// "strike" of ui_state, runs the dev probe (strike_probe), owns the window (config section Strike), the map layer, the hotkey, the "Return control" participant and the save of the groups.
    /// The air tick (AirTickFeature) gives the controller its ownership object and the deck broker (BindAir) and ticks it once per second of game time.
    /// </summary>
    internal sealed class StrikeFeature : UiFeatureBase, IUiSettingsSection, IUiDiag
    {
        private readonly GameApi _api;
        private StrikeController _ctl;
        private StrikeParams _p = new StrikeParams();
        private bool _enabled = false, _debug;
        private string _hotkeyText = "LeftAlt+H|LeftAlt+Q|LeftAlt+X|F6";
        private StrikeWindowGui _win;
        private StrikeOverlay _ov;
        private HotkeyBinding _hotkey;
        private IKeyInput _input;
        private double _nextProbe, _viewAt = double.NegativeInfinity;
        private bool _vetoChecked;
        private StrikeView _viewCache;
        private int _slotTick, _slotProbe, _slotView;

        public StrikeFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "strike"; } }
        public int SettingsOrder { get { return 62; } }
        public override string[] DependsOn { get { return new[] { "cap", "radar", "toast" }; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(StrikeSpecs.Set); }

        public string DiagSection { get { return "strike"; } }

        public string DiagJson(int limit)
        {
            var c = _ctl;
            return c == null ? "null" : c.ViewJson(limit);
        }

        // [Strike]: ADR-0132 section 9. Enabled is false in the main (beta 0.31.x) tree until the check of the beta data (E2); the stable line ships it true.
        public override void Bind(IUiConfig cfg)
        {
            const string S = "Strike";
            var p = new StrikeParams();
            _enabled = cfg.Bind(S, "Enabled", false, "Strike window: groups of aircraft with anti-ship missiles, plans of a missile strike, the wait point", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sHotkey = cfg.Bind(S, "Hotkey", _hotkeyText, "Opens the Strike window: Unity KeyCode names joined by + (e.g. LeftAlt+H); several candidates joined by | - the first one no game binding and no other hotkey of the mod uses wins; empty = off", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live });
            _hotkeyText = sHotkey.Value;
            var sPk = cfg.Bind(S, "PkThreshold", p.PkThreshold, "Odds of a breakthrough that the plan aims at for every target (0.8..0.9)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0.8, Max = 0.9, Step = 0.05, DisplayFactor = 100, UnitKey = "pct" });
            p.PkThreshold = Math.Max(0.8, Math.Min(0.9, sPk.Value));
            var sKill = cfg.Bind(S, "KillThreshold", p.KillThreshold, "Odds of sinking that a plan with the goal Sink aims at for the main target (0.8..0.9)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0.8, Max = 0.9, Step = 0.05, DisplayFactor = 100, UnitKey = "pct" });
            p.KillThreshold = Math.Max(0.8, Math.Min(0.9, sKill.Value));
            var sTot = cfg.Bind(S, "TotToleranceS", p.TotToleranceS, "Allowed spread of the arrival of the missiles at one target, s (5..30)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 5, Max = 30, Step = 5, UnitKey = "s" });
            p.TotToleranceS = Math.Max(5, Math.Min(30, sTot.Value));
            var sStand = cfg.Bind(S, "StandoffNm", p.StandoffNm, "Margin outside the SAM zones for the wait point and the launch points, nm (0..50)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 50, Step = 5, UnitKey = "nm" });
            p.StandoffNm = Math.Max(0, Math.Min(50, sStand.Value));
            var sFuel = cfg.Bind(S, "FuelMarginNm", p.FuelMarginNm, "An aircraft goes home when its spare path over the way home is at or below this many nm (0..100)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0, Max = 100, Step = 5, UnitKey = "nm" });
            p.FuelMarginNm = Math.Max(0, Math.Min(100, sFuel.Value));
            var sTurn = cfg.Bind(S, "TurnLeadS", p.TurnLeadS, "Reserve on top of the turn time (4 s + turn angle / 8 deg per s) between the order and the missile launch, s (0..30)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.TurnLeadS = Math.Max(0, Math.Min(30, sTurn.Value));
            var sEw = cfg.Bind(S, "EwHoldS", p.EwHoldS, "The jammer stays on this many seconds after the last arrival (0..60)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.EwHoldS = Math.Max(0, Math.Min(60, sEw.Value));
            var sStale = cfg.Bind(S, "StaleNm", p.StaleNm, "A target that left its planned position by more than this many nm makes the plan stale (0.5..20)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.StaleNm = Math.Max(0.5, Math.Min(20, sStale.Value));
            var sHold = cfg.Bind(S, "HoldS", p.HoldS, "How long a launch waits for a lost contact before it is skipped, s (0..600)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.HoldS = Math.Max(0, Math.Min(600, sHold.Value));
            var sRoe = cfg.Bind(S, "RoeMode", p.RoeMode, "veto | hold: how the aircraft of a group are kept from firing on their own (veto holds only anti-ship missiles; hold puts weapons on hold while they are in the group; without the engage veto patch the mode is hold)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "veto", "hold" } });
            p.RoeMode = string.Equals((sRoe.Value ?? "").Trim(), "hold", StringComparison.OrdinalIgnoreCase) ? "hold" : "veto";
            var sMix = cfg.Bind(S, "MixShiftMaxS", p.MixShiftMaxS, "The most T of the second missile type of one aircraft may be moved later; over this the missile is left out of the plan, s (0..300)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.MixShiftMaxS = Math.Max(0, Math.Min(300, sMix.Value));
            var sEwClear = cfg.Bind(S, "EwReturnClearS", p.EwReturnClearS, "A jammer that left an air threat returns to its station after this many seconds without a threat (0..600)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.EwReturnClearS = Math.Max(0, Math.Min(600, sEwClear.Value));
            var sEwFighterEta = cfg.Bind(S, "EwFighterEtaS", p.EwFighterEtaS, "Seconds before the strike group enters the missile radius of a hostile fighter at which a jammer starts to jam it (0..1800, 0 = do not jam fighters)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.EwFighterEtaS = Math.Max(0, Math.Min(1800, sEwFighterEta.Value));
            var sFighterStandoff = cfg.Bind(S, "FighterStandoffNm", p.FighterStandoffNm, "Margin in nm over the missile radius of a hostile fighter that a jammer keeps from it (0..30)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.FighterStandoffNm = Math.Max(0, Math.Min(30, sFighterStandoff.Value));
            var sDeckLate = cfg.Bind(S, "DeckLateS", p.DeckLateS, "A deck order counts as failed when the aircraft is not in the air this many seconds after its ready time (60..3600)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.DeckLateS = Math.Max(60, Math.Min(3600, sDeckLate.Value));
            var sAutoWaves = cfg.Bind(S, "AutoWaves", p.AutoWaves, "How many waves (issued stages) Auto makes against one target or set of targets, then stops (1..3)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 1, Max = 3, Step = 1 });
            p.AutoWaves = Math.Max(1, Math.Min(3, sAutoWaves.Value));
            var sClearTimeout = cfg.Bind(S, "ClearTimeoutS", p.ClearTimeoutS, "How many seconds the stage-2 aircraft of a clearing wait for the neighbour to sink (120..1800)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.ClearTimeoutS = Math.Max(120, Math.Min(1800, sClearTimeout.Value));
            var sDebug = cfg.Bind(S, "Debug", false, "Log every step of the Strike module", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            _debug = sDebug.Value;
            OnChanged(sPk, v => p.PkThreshold = Math.Max(0.8, Math.Min(0.9, v)));
            OnChanged(sTot, v => p.TotToleranceS = Math.Max(5, Math.Min(30, v)));
            OnChanged(sStand, v => p.StandoffNm = Math.Max(0, Math.Min(50, v)));
            OnChanged(sFuel, v => p.FuelMarginNm = Math.Max(0, Math.Min(100, v)));
            OnChanged(sTurn, v => p.TurnLeadS = Math.Max(0, Math.Min(30, v)));
            OnChanged(sEw, v => p.EwHoldS = Math.Max(0, Math.Min(60, v)));
            OnChanged(sStale, v => p.StaleNm = Math.Max(0.5, Math.Min(20, v)));
            OnChanged(sHold, v => p.HoldS = Math.Max(0, Math.Min(600, v)));
            OnChanged(sRoe, v => p.RoeMode = string.Equals((v ?? "").Trim(), "hold", StringComparison.OrdinalIgnoreCase) ? "hold" : "veto");
            OnChanged(sKill, v => p.KillThreshold = Math.Max(0.8, Math.Min(0.9, v)));
            OnChanged(sMix, v => p.MixShiftMaxS = Math.Max(0, Math.Min(300, v)));
            OnChanged(sEwClear, v => p.EwReturnClearS = Math.Max(0, Math.Min(600, v)));
            OnChanged(sEwFighterEta, v => p.EwFighterEtaS = Math.Max(0, Math.Min(1800, v)));
            OnChanged(sFighterStandoff, v => p.FighterStandoffNm = Math.Max(0, Math.Min(30, v)));
            OnChanged(sDeckLate, v => p.DeckLateS = Math.Max(60, Math.Min(3600, v)));
            OnChanged(sAutoWaves, v => p.AutoWaves = Math.Max(1, Math.Min(3, v)));
            OnChanged(sClearTimeout, v => p.ClearTimeoutS = Math.Max(120, Math.Min(1800, v)));
            OnChanged(sDebug, v => _debug = v);
            OnChanged(sHotkey, v => { _hotkeyText = v; if (_hotkey != null) _hotkey.Respec(v); });
            _p = p;
        }

        private sealed class WindowService : IStrikeWindow
        {
            private readonly StrikeWindowGui _w;
            public WindowService(StrikeWindowGui w) { _w = w; }
            public bool Visible { get { return _w.Visible; } set { _w.Visible = value; } }
            public void Open() { _w.Visible = true; }
        }

        private sealed class GameKeys : IHotkeyGame
        {
            private readonly GameApi _a;
            public GameKeys(GameApi a) { _a = a; }
            public string Check(string[] keys, out string conflict) { return _a.OverviewKeyCheck(keys, out conflict); }
        }

        public override void Init(UiContext ctx)
        {
            ctx.Provide<StrikeFeature>(this);
            ctx.Release.Add(new ReleaseParticipant("strike", _api.StrikeRelease), ReleaseRegistry.OrderStrike);
            if (!_enabled) { _api.Capabilities.Remove("strike"); return; }
            if (!_api.Capabilities.Contains("strike")) { ctx.Host.LogInfo("strike unavailable: members of the game are missing"); return; }
            try
            {
                int owner = ctx.Guard.OwnerOf(Id);
                _slotTick = ctx.Guard.Register("strike.tick", owner);
                _slotProbe = ctx.Guard.Register("strike.probe", owner);
                _slotView = ctx.Guard.Register("strike.view", owner);
                _api.StrikeSaveDir = ctx.DataDir("strike");
                var game = _api.NewStrikeGame();
                _ctl = new StrikeController(game, _p, m => ctx.Host.LogInfo(m));
                _ctl.VetoJson = VetoJson;
                _ctl.Book = _api.StrikeBook;   // the launches the mod itself orders: the veto of the game layer lets only these pass
                _api.Strike = _ctl;
                _api.StrikeGameHandle = game;
                _api.EngageVetos.Set(EngageVetoChain.NameStrike, EngageVetoChain.OrderStrike, _api.StrikeVeto);
                ctx.Mission.Subscribe(42, "strike", e => _api.StrikeEnterMission(e.SavePath, e.MissionTimeS, e.Stamp), why => { LeaveMission(); _api.StrikeLeaveMission(); });
                var radar = ctx.Get<IRadarView>();
                IAswMap map = new AswMapProvider(radar == null ? null : radar.Overlay);
                Func<GeoPt?> refPoint = () => RefPoint();
                _win = new StrikeWindowGui(() => View(), (name, cmd) => _api.StrikeRaw(name, cmd), map, b => _api.SetMouseBlock("strike", b), m => ctx.Host.LogInfo("[Strike] " + m), refPoint, b => _api.SetMouseBlock("strike.pick", b));
                _win.FocusUnitFn = uid => { string se; string code = _api.FocusOwnUnit(uid, "strike", out se); if (se != null) ctx.Host.LogInfo("[Strike] focus uid=" + uid + " select=" + se); return code; };
                _ov = new StrikeOverlay(() => _ctl != null && _ctl.Groups.Count > 0 ? View() : null, () => _win == null || !_win.Visible ? null : _win.SelectedGroup, map, m => ctx.Host.LogInfo("[Strike] " + m));
                _ov.NoesisActive = () => radar != null && radar.Layer != null && radar.Layer.Active;
                if (radar != null && radar.Layer != null) radar.Layer.Extra.Add(_ov);
                _api.StrikeWindow = new WindowService(_win);
                _input = new UnityKeyInput(_api);
                _hotkey = new HotkeyBinding("strike", "Strike.Hotkey", _hotkeyText, ctx.Hotkeys, m => ctx.Host.LogInfo(m), m => ctx.Host.LogWarning(m));
            }
            catch (Exception ex)
            {
                ctx.Host.LogError("strike init crashed: " + ex);
                _api.Strike = null; _api.StrikeGameHandle = null; _api.StrikeWindow = null; _ctl = null; _win = null; _ov = null;
            }
        }

        /// <summary>Called by the air tick once the shared ownership object and the deck broker exist: one machine = one automation, the deck through the broker.</summary>
        public void BindAir(AirOwnership own, IAirDeck deck)
        {
            var c = _ctl;
            if (c == null || own == null) return;
            c.Ownership = own;
            c.Deck = deck;
            c.OwnerOf = uid => { string w; string g = own.Owner(uid, out w); return g == null || w == "strike" ? null : w + ":" + g; };
            own.Register("strike", c.GroupNameOf);
        }

        /// <summary>The window's view, rebuilt at most four times a second (the deck tasks are read through reflection).</summary>
        private StrikeView View()
        {
            var c = _ctl;
            if (c == null) return null;
            double now = UnityEngine.Time.unscaledTime;
            if (_viewCache != null && now - _viewAt < 0.25 && now >= _viewAt) return _viewCache;
            try { _viewCache = c.View(); _viewAt = now; }
            catch (Exception e) { Ctx.Guard.Fail(_slotView, e); if (_viewCache == null) _viewCache = new StrikeView { Ready = false }; }
            return _viewCache;
        }

        private GeoPt? RefPoint()
        {
            try
            {
                var v = View();
                if (v == null) return null;
                foreach (var g in v.Groups) if (g.WaitPt.HasValue) return g.WaitPt;
                foreach (var s in v.Ships) if (s.Hostile && (s.Lat != 0 || s.Lon != 0)) return new GeoPt(s.Lat, s.Lon);
                foreach (var a in v.Air) if (a.Airborne && (a.Lat != 0 || a.Lon != 0)) return new GeoPt(a.Lat, a.Lon);
            }
            catch (Exception) { }
            return null;
        }

        private void LeaveMission()
        {
            // the groups and the probe belong to the mission that was left
            var c = _ctl;
            if (c == null) return;
            foreach (var g in new System.Collections.Generic.List<StrikeGroup>(c.Groups)) c.Group(new System.Collections.Generic.Dictionary<string, object> { { "op", "disband" }, { "group", g.Name } });
            _api.StrikeProbeBook.Clear(); _api.StrikeBook.Clear();
            c.ResetMission();
            _viewCache = null;
        }

        private string VetoJson()
        {
            var ch = _api.EngageVetos;
            return "{\"mode\":" + Json.Str(_ctl == null ? _p.RoeMode : _ctl.EffectiveRoe) + ",\"chain\":[" + string.Join(",", Array.ConvertAll(ch.Names, Json.Str)) + "],\"vetoed_n\":" + ch.VetoedN.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + ",\"last\":" + (ch.LastBy == null ? "null" : Json.Str(ch.LastBy)) + "}";
        }

        /// <summary>The frame part (window, layer, hotkey, probe); called once per frame by the air_tick module. The controller itself ticks in the air tick (AirTickStrike).</summary>
        public void Tick()
        {
            if (_api.Strike == null) return;
            if (!_vetoChecked && _api.Frame.InMission)
            {
                _vetoChecked = true;   // the patch is installed by the harmony module; checked once the mission runs
                if (!_api.PatchesApplied.Contains(ModManifest.PatchFireVeto)) { _api.Strike.FireVetoPatched = false; Ctx.Host.LogInfo("[Strike] strike roe fallback why=no_fire_veto (the weapons of the group go on hold)"); }
            }
            if (_win != null) _win.Update();
            if (_ov != null) _ov.Tick();
            if (_hotkey != null && _hotkey.State != "off" && _api.Frame.InMission)
            {
                _hotkey.Resolve(Ctx.Host.RealTime, true, new GameKeys(_api));
                if (_hotkey.Pressed(_input) && _win != null) _win.Toggle();
            }
        }

        public override void Update(UiFrame frame)
        {
            // the dev probe only; nothing is read without an active probe
            var c = _ctl;
            if (c == null || !_api.Frame.InMission) return;
            if (!_api.StrikeProbeBook.Active || frame.RealTime < _nextProbe) return;
            _nextProbe = frame.RealTime + 0.5;
            try { _api.StrikeProbeTick(); } catch (Exception e) { Ctx.Guard.Fail(_slotProbe, e); }
        }

        public override void OnGUI()
        {
            if (_ov != null) _ov.OnGUI();
            if (_win != null) _win.OnGUI();
        }

        public override void Dispose()
        {
            try { _api.EngageVetos.Remove("strike"); } catch (Exception) { }
            try { if (_win != null) { _win.Dispose(); _api.SetMouseBlock("strike", false); } } catch (Exception) { }
            try { _api.Strike = null; _api.StrikeGameHandle = null; _api.StrikeWindow = null; _api.StrikeProbeBook.Clear(); } catch (Exception) { }
        }
    }
}
