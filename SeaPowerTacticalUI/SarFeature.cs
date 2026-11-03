using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-109 / ADR-0039: search and rescue (config section Sar): the controller, the ship menu service and the pump.</summary>
    internal sealed class SarFeature : UiFeatureBase, IUiSettingsSection
    {
        private readonly GameApi _api;
        private SarParams _sarP;
        private bool _sarEnabled = true;
        private int _slotPump;

        public SarFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "sar"; } }
        public int SettingsOrder { get { return 50; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(SarSpecs.Set); }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "Sar";
            var p = new SarParams();
            _sarEnabled = cfg.Bind(S, "Enabled", true, "Search and rescue: SAR order of helicopter groups, ships and submarines in the SAR tab of the Aviation window", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var sThreatStandoffNm = cfg.Bind(S, "ThreatStandoffNm", p.ThreatStandoffNm, "A raft closer than this to an air defense known to the player is not approached (nm)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 50, UnitKey = "nm" });
            p.ThreatStandoffNm = Math.Max(0, sThreatStandoffNm.Value);
            var sUnreachableS = cfg.Bind(S, "UnreachableS", p.UnreachableS, "A raft a helicopter failed to pick up 3 times is left alone for this long (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.UnreachableS = Math.Max(60, sUnreachableS.Value);
            var sNoRaftsGraceS = cfg.Bind(S, "NoRaftsGraceS", p.NoRaftsGraceS, "No rafts for this long = the unit goes home (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.NoRaftsGraceS = Math.Max(0, sNoRaftsGraceS.Value);
            var sTransitAltFt = cfg.Bind(S, "TransitAltFt", p.TransitAltFt, "Helicopter transit altitude before the very low approach (ft)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.TransitAltFt = Math.Max(100, sTransitAltFt.Value);
            var sShipFinalNm = cfg.Bind(S, "ShipFinalNm", p.ShipFinalNm, "A ship slows to Small at this distance from the raft (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ShipFinalNm = Math.Max(0.2, sShipFinalNm.Value);
            var sSubSurfaceNm = cfg.Bind(S, "SubSurfaceNm", p.SubSurfaceNm, "A submarine surfaces at this distance from the raft (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.SubSurfaceNm = Math.Max(0.5, sSubSurfaceNm.Value);
            var sNextRaftNearNm = cfg.Bind(S, "NextRaftNearNm", p.NextRaftNearNm, "A next raft closer than this is approached at Small speed (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.NextRaftNearNm = Math.Max(0.5, sNextRaftNearNm.Value);
            var sProgressMinS = cfg.Bind(S, "ProgressMinS", p.ProgressMinS, "Minimal time without progress before a claim is dropped (s)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ProgressMinS = Math.Max(30, sProgressMinS.Value);
            var sMinDepthM = cfg.Bind(S, "MinDepthM", p.MinDepthM, "Ship route: shallower than this counts as land (m)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.MinDepthM = Math.Max(1, sMinDepthM.Value);
            var sSubTransitDepthM = cfg.Bind(S, "SubTransitDepthM", p.SubTransitDepthM, "Submarine transit depth (m)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.SubTransitDepthM = Math.Max(10, sSubTransitDepthM.Value);
            var sShoreMarginNm = cfg.Bind(S, "ShoreMarginNm", p.ShoreMarginNm, "Ship route: distance kept from the shore (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ShoreMarginNm = Math.Max(0.1, sShoreMarginNm.Value);
            var sStandby = cfg.Bind(S, "Standby", p.Standby, "Ready spare SAR helicopters kept for the relief (0..3)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 3 });
            p.Standby = Math.Max(0, Math.Min(3, sStandby.Value));
            var sMaxReacquire = cfg.Bind(S, "MaxReacquire", p.MaxReacquire, "Helicopter approach attempts at one raft before it is marked unreachable", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.MaxReacquire = Math.Max(1, Math.Min(10, sMaxReacquire.Value));
            var sProbeBudget = cfg.Bind(S, "ProbeBudget", p.ProbeBudget, "Terrain probes per frame while a ship route is planned", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.ProbeBudget = Math.Max(50, sProbeBudget.Value);
            var sMaxCells = cfg.Bind(S, "MaxCells", p.MaxCells, "Cells of the route grid", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.MaxCells = Math.Max(2000, sMaxCells.Value);
            var sPriorityRafts = cfg.Bind(S, "PriorityRafts", p.PriorityRafts, "Raft types picked first, in order, joined by commas", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.PriorityRafts = sPriorityRafts.Value;
            var sLoadoutTieBreak = cfg.Bind(S, "LoadoutTieBreak", p.LoadoutTieBreak, "Loadouts with the same seats: the first in this list wins", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LoadoutTieBreak = sLoadoutTieBreak.Value;
            var sRearmMaxMin = cfg.Bind(S, "RearmMaxMin", p.RearmMaxMin, "A ready helicopter with fewer seats than the SAR loadout is re-armed on deck when that takes at most this long (game minutes, after the deck timings mode); 0 = launch it as it is", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.RearmMaxMin = Math.Max(0, sRearmMaxMin.Value);
            var sTelegraph = cfg.Bind(S, "ShipTransitTelegraph", p.ShipTransitTelegraph, "current | medium | full: ship speed in transit", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "current", "medium", "full" } });
            p.ShipTransitTelegraph = TelegraphOf(sTelegraph.Value);
            var sOnComplete = cfg.Bind(S, "ShipOnComplete", p.ShipOnComplete, "restore | stop: what a ship or submarine does when no rafts are left", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.NextOrder, Options = new[] { "restore", "stop" } });
            p.ShipOnComplete = OnCompleteOf(sOnComplete.Value);
            var sMission = cfg.Bind(S, "LaunchMissionType", p.LaunchMissionType, "sar | none: mission type of a SAR launch order (none = fallback)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.LaunchMissionType = MissionOf(sMission.Value);
            var sForceApproachSlow = cfg.Bind(S, "ForceApproachSlow", p.ForceApproachSlow, "Fallback: force the helicopter to slow down at the raft (use only if the very low approach does not slow it)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.NextOrder });
            p.ForceApproachSlow = sForceApproachSlow.Value;
            var sDebug = cfg.Bind(S, "Debug", p.Debug, "Log every SAR step", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            p.Debug = sDebug.Value;
            OnChanged(sThreatStandoffNm, v => p.ThreatStandoffNm = Math.Max(0, v));
            OnChanged(sUnreachableS, v => p.UnreachableS = Math.Max(60, v));
            OnChanged(sNoRaftsGraceS, v => p.NoRaftsGraceS = Math.Max(0, v));
            OnChanged(sTransitAltFt, v => p.TransitAltFt = Math.Max(100, v));
            OnChanged(sShipFinalNm, v => p.ShipFinalNm = Math.Max(0.2, v));
            OnChanged(sSubSurfaceNm, v => p.SubSurfaceNm = Math.Max(0.5, v));
            OnChanged(sNextRaftNearNm, v => p.NextRaftNearNm = Math.Max(0.5, v));
            OnChanged(sProgressMinS, v => p.ProgressMinS = Math.Max(30, v));
            OnChanged(sMinDepthM, v => p.MinDepthM = Math.Max(1, v));
            OnChanged(sSubTransitDepthM, v => p.SubTransitDepthM = Math.Max(10, v));
            OnChanged(sShoreMarginNm, v => p.ShoreMarginNm = Math.Max(0.1, v));
            OnChanged(sStandby, v => p.Standby = Math.Max(0, Math.Min(3, v)));
            OnChanged(sMaxReacquire, v => p.MaxReacquire = Math.Max(1, Math.Min(10, v)));
            OnChanged(sProbeBudget, v => p.ProbeBudget = Math.Max(50, v));
            OnChanged(sMaxCells, v => p.MaxCells = Math.Max(2000, v));
            OnChanged(sPriorityRafts, v => p.PriorityRafts = v);
            OnChanged(sLoadoutTieBreak, v => p.LoadoutTieBreak = v);
            OnChanged(sRearmMaxMin, v => p.RearmMaxMin = Math.Max(0, v));
            OnChanged(sForceApproachSlow, v => p.ForceApproachSlow = v);
            OnChanged(sDebug, v => p.Debug = v);
            OnChanged(sTelegraph, v => p.ShipTransitTelegraph = TelegraphOf(v));
            OnChanged(sOnComplete, v => p.ShipOnComplete = OnCompleteOf(v));
            OnChanged(sMission, v => p.LaunchMissionType = MissionOf(v));
            _sarP = p;
        }


        private static string TelegraphOf(string v) { string t = (v ?? "current").Trim().ToLowerInvariant(); return t == "medium" || t == "full" ? t : "current"; }
        private static string OnCompleteOf(string v) { return (v ?? "restore").Trim().ToLowerInvariant() == "stop" ? "stop" : "restore"; }
        private static string MissionOf(string v) { return (v ?? "sar").Trim().ToLowerInvariant() == "none" ? "none" : "sar"; }

        public override void Init(UiContext ctx)
        {
            _slotPump = ctx.Guard.Register("sar.pump", ctx.Guard.OwnerOf(Id));
            ctx.Provide<SarParams>(_sarP);   // the ASW controller reads the SAR options
            ctx.Release.Add(new ReleaseParticipant("sar", ReleaseUid), ReleaseRegistry.OrderSar);
            try
            {
                if (!_sarEnabled) { _api.Capabilities.Remove("sar"); return; }
                if (!_api.Capabilities.Contains("sar")) { ctx.Host.LogWarning("sar disabled: members of the game are missing"); return; }
                _api.CreateSarController(_sarP, ModThreads.PoolRunner("sar.solve"));
                ctx.Provide<ISarShips>(new SarShips(_api));
            }
            catch (Exception e)
            {
                ctx.Host.LogError("sar init crashed: " + e);
                _api.Sar = null; _api.Capabilities.Remove("sar");
            }
        }

        private void ReleaseUid(int uid)
        {
            if (_api.Sar != null) _api.Sar.ReleaseUnit(uid, "released");
        }

        public override void Update(UiFrame frame)
        {
            try { _api.PumpSar(frame.RealTime); }
            catch (Exception e) { Ctx.Guard.Fail(_slotPump, e); }
        }

        private sealed class SarShips : ISarShips
        {
            private readonly GameApi _a;
            public SarShips(GameApi a) { _a = a; }
            public string ShipCmd(Dictionary<string, object> c) { return _a.Sar.ShipCmd(c); }
            public bool ShipActive(int uid) { return _a.Sar.ShipActive(uid); }
            public string Caption(int uid) { return _a.Sar.MenuCaption(uid); }
            public string Busy(int uid) { return _a.Sar.Busy(uid); }
            public IList<SarShipRow> Rows() { return _a.Sar.ShipRows(); }
        }
    }
}
