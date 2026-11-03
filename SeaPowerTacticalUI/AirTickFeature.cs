using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// One tick of the aviation (T-119, ADR-0042): once per second of game time the ASW/SAR-helicopter controller and the CAP controller
    /// tick between BeginTick and EndTick of the deck broker; the broker orders their requests per deck (ADR-0041 tiers) and makes ASW/SAR wait
    /// for a CAP group with a gap under an air threat (at most [Cap] DeckYieldMaxS). The frame part of the windows (pump, window, hotkey) runs here too.
    /// </summary>
    internal sealed class AirTickFeature : UiFeatureBase, IUiDiag
    {
        private const string S = "Cap";

        private readonly GameApi _api;
        private AswFeature _asw;
        private CapFeature _cap;
        private StrikeFeature _strike;   // T-341
        private AirDeckBroker _broker;
        private readonly AirTickClock _clock = new AirTickClock();
        private int _slotAsw, _slotCap, _slotAswTick, _slotCapTick, _slotTick, _slotGuardTick, _slotSlots, _slotStrike, _slotStrikeTick;
        private AirOwnership _own;   // T-186: the ownership shared by both windows; its slot book is rebound once per air tick
        private double _yieldMaxS = 120;
        private bool _debug;
        private readonly AirDeckParams _deckP = new AirDeckParams();
        private readonly PostLoadGate _gate = new PostLoadGate();   // T-211 (ADR-0088): no air tick and no restore of the groups before the world after a load has settled
        private double _gateSampleAt = -1e9;
        private bool _armPending;

        public AirTickFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "air_tick"; } }
        public override string[] DependsOn { get { return new[] { "asw", "cap", "strike", "air_guard" }; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(AirOpsSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            var sYield = cfg.Bind(S, "DeckYieldMaxS", 120f, "ASW and helicopter SAR groups on the same deck wait for a CAP group with a gap under an air threat at most this many seconds of game time (0 = do not wait; at most 120)", UiKeyClass.Advanced, new UiSettingMeta { Section = "cap", Apply = UiApply.NextOrder });
            _yieldMaxS = _deckP.YieldMaxS = Math.Max(0, Math.Min(120, sYield.Value));
            // the same keys as the windows' own debug switches (one entry in the config file)
            var sCapDebug = cfg.Bind(S, "Debug", false, "Log every CAP step and the hidden types", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var sAswDebug = cfg.Bind("Asw", "Debug", false, "Log R_eff per sensor, group plans and every ASW step", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            _deckP.Debug = sCapDebug.Value || sAswDebug.Value;
            _debug = _deckP.Debug;
            OnChanged(sYield, v => _yieldMaxS = _deckP.YieldMaxS = Math.Max(0, Math.Min(120, v)));
            OnChanged(sCapDebug, v => { _debug = _deckP.Debug = v || sAswDebug.Value; });
            OnChanged(sAswDebug, v => { _debug = _deckP.Debug = v || sCapDebug.Value; });
        }

        public override void Init(UiContext ctx)
        {
            int owner = ctx.Guard.OwnerOf(Id);
            _slotAsw = ctx.Guard.Register("air.asw", owner);
            _slotCap = ctx.Guard.Register("air.cap", owner);
            _slotAswTick = ctx.Guard.Register("air.asw_tick", owner);
            _slotCapTick = ctx.Guard.Register("air.cap_tick", owner);
            _slotTick = ctx.Guard.Register("air.tick", owner);
            _slotGuardTick = ctx.Guard.Register("air.guard", owner);
            _slotSlots = ctx.Guard.Register("air.slots", owner);
            _slotStrike = ctx.Guard.Register("air.strike", owner);
            _slotStrikeTick = ctx.Guard.Register("air.strike_tick", owner);
            // a new mission (and leaving one): the clock and the broker's queue, holds and key registry start over; order 10 = before every window restores its groups
            _api.PostLoadGateActive = true;
            ctx.Mission.Subscribe(10, "air_tick",
                e => { ResetAir(); _armPending = e.SavePath != null; if (!_armPending) _gate.Disarm(); },
                why => { ResetAir(); _gate.Disarm(); _armPending = false; });
            _asw = ctx.Get<AswFeature>();
            _cap = ctx.Get<CapFeature>();
            _strike = ctx.Get<StrikeFeature>();
            try
            {
                var asw = _api.Asw;
                var cap = _api.Cap;
                if (asw == null && cap == null) return;
                var own = (asw != null ? asw.Ownership : null) ?? (cap != null ? cap.Ownership : null) ?? new AirOwnership();
                if (asw != null) { asw.Ownership = own; own.Register("asw", asw.GroupNameOf); }
                _own = own;
                WaypointQuietPatches.OwnerOf = uid => { string w; return own.Owner(uid, out w); };   // T-331: "We're on position" is quiet for the aircraft of a group of the mod
                own.Slots.Log = line => ctx.Host.LogInfo(line);
                _broker = new AirDeckBroker(_api.AirOps, own, _deckP,
                    line => ctx.Host.LogInfo(line), (name, detail) => _api.AirEvent(name, detail));
                _broker.DeckName = _api.AirDeckName;
                if (asw != null) asw.Deck = _broker;
                if (cap != null) cap.Deck = _broker;
                if (_strike != null) _strike.BindAir(own, _broker);   // T-341: the same ownership and the same deck broker
            }
            catch (Exception e) { ctx.Host.LogError("air tick init failed: " + e); _broker = null; }
        }

        // ---- IUiDiag (T-139 section "deck"): read-only snapshot of the broker
        public string DiagSection { get { return "deck"; } }

        public string DiagJson(int limit)
        {
            var b = _broker;
            if (b == null) return "null";
            string own = b.DiagJson(limit);
            string hangar = _own == null ? "null" : _own.Slots.DiagJson(limit);   // T-186: the shared slot book
            // 0.27.8: the broker lists only the mod's own keys ({"decks":[]} with no order of ours pending); "game" is the deck as the game has it
            string game;
            try { game = _api.AirDeckGameJson(limit); } catch (Exception e) { game = "{\"error\":" + Json.Str(e.GetType().Name) + "}"; }
            string post = ",\"post_load\":{\"state\":" + Json.Str(_gate.State) + ",\"waited_s\":" + Json.Num(Math.Round(_gate.WaitedRealS, 1)) + ",\"paused\":" + Json.Bool(_gate.Paused) + ",\"members\":[" + _gate.MembersFound + "," + _gate.MembersWanted + "]}";
            return own.EndsWith("}", StringComparison.Ordinal) ? own.Substring(0, own.Length - 1) + ",\"game\":" + game + ",\"hangar\":" + hangar + post + "}" : own;
        }

        public override void Dispose()
        {
            // the module goes away: the restore must not wait for a gate that no one steps any more
            try { _api.PostLoadGateActive = false; _gate.Disarm(); _api.ApplyDeferredRestore(_api.Frame.MissionTimeS ?? 0); } catch (Exception) { }
        }

        private void ResetAir()
        {
            _clock.Reset();
            if (_broker != null) _broker.Reset();
            if (_own != null) _own.Slots.Reset();
            WaypointQuietPatches.Book.Reset(); _api.WpqClearPlaced();   // T-331: a new mission starts the quiet clock over
        }

        public override void Update(UiFrame frame)
        {
            // the frame part of the windows: pump (mission change, save marker), window, hotkey
            try { if (_asw != null) _asw.Tick(); }
            catch (Exception e) { Ctx.Guard.Fail(_slotAsw, e); }
            try { if (_cap != null) _cap.Tick(); }
            catch (Exception e) { Ctx.Guard.Fail(_slotCap, e); }
            try { if (_strike != null) _strike.Tick(); }
            catch (Exception e) { Ctx.Guard.Fail(_slotStrike, e); }
            // T-211: the post-load gate. Until the load screen is gone, own units are listed and their count stays the same for a while: no air tick, the groups are not restored
            try
            {
                if (_armPending) { _gate.Arm(frame.RealTime); _armPending = false; }
                if (_gate.Armed)
                {
                    if (!_api.Frame.InMission) return;
                    double gt = _api.Frame.MissionTimeS ?? 0;
                    if (frame.RealTime - _gateSampleAt >= PostLoadGate.SampleRealS)
                    {
                        _gateSampleAt = frame.RealTime;
                        string sig; int n = _api.PostLoadSample(out sig);
                        int mw, mf; _api.PostLoadMembers(out mw, out mf);   // T-217: members wanted/found for the paused gate
                        if (_gate.Step(_api.LoadScreenActive(), n, sig, gt, frame.RealTime, _api.PostLoadPaused(), mw, mf))
                        {
                            Ctx.Host.LogInfo("[Cap] post-load gate " + _gate.State + " wait_s=" + _gate.WaitedRealS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " sig=" + sig + " paused=" + (_gate.Paused ? "1" : "0") + " members=" + _gate.MembersFound + "/" + _gate.MembersWanted);
                            _api.ApplyDeferredRestore(gt);
                        }
                    }
                    if (_gate.Armed)
                    {
                        string wait = _gate.StatusLine(frame.RealTime);   // T-232: why the gate is still closed, once per 5 s
                        if (wait != null) Ctx.Host.LogInfo("[Cap] " + wait + " " + _api.LoadScreenDetail());
                        if (_broker != null) _broker.Flush(gt);
                        return;
                    }   // no air tick (guard, asw, cap) before the world is there
                }
            }
            catch (Exception e) { Ctx.Guard.Fail(_slotTick, e); _gate.Disarm(); try { _api.ApplyDeferredRestore(_api.Frame.MissionTimeS ?? 0); } catch (Exception e2) { Ctx.Host.LogWarning("[Cap] deferred restore failed: " + e2.Message); } }
            if (_broker == null) return;
            // the air tick: one step per second of game time, the broker flushes at the end; requests of window commands leave at the frame end
            try
            {
                if (!_api.Frame.InMission) return;
                double t = _api.Frame.MissionTimeS ?? 0;
                if (!_clock.Due(t, frame.RealTime)) { _broker.Flush(t); return; }
                _broker.BeginTick(t);
                _api.AirScanBegin();
                try
                {
                    try { _api.AirTickGuard(t); _api.AirTickLoss(t); }   // T-175: the threat stages and the losses first; the owners read them in their own tick
                    catch (Exception e) { Ctx.Guard.Fail(_slotGuardTick, e); }
                    try { if (_own != null) _own.Slots.Rebind(t, _api.AirHangarNumbers); }   // T-186 (ADR-0082): one rebinding of the hangar slots of both windows, before their ticks
                    catch (Exception e) { Ctx.Guard.Fail(_slotSlots, e); }
                    try { _api.AirTickAsw(t); }
                    catch (Exception e) { Ctx.Guard.Fail(_slotAswTick, e); }
                    try { _api.AirTickCap(t); }
                    catch (Exception e) { Ctx.Guard.Fail(_slotCapTick, e); }
                    try { _api.AirTickStrike(t); }   // T-341: after the CAP
                    catch (Exception e) { Ctx.Guard.Fail(_slotStrikeTick, e); }
                }
                finally { _api.AirScanEnd(); }
                _broker.EndTick(t);
            }
            catch (Exception e) { Ctx.Guard.Fail(_slotTick, e); }
        }
    }
}
