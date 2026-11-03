using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>Own torpedo evasion (config section AutoEvade). The values are read by GameApi.SelfCheck (evasion_own), so they are written in Bind.</summary>
    internal sealed class EvasionFeature : UiFeatureBase, IUiDiag, IUiSettingsSection
    {
        public EvasionFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "evasion"; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(EvasionSpecs.Set); check.Specs(SubEvasionSpecs.Set); }
        public int SettingsOrder { get { return 80; } }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "AutoEvade";
            string mode = (cfg.Bind(S, "Mode", "own", "own | native: own = the mod's own torpedo evasion (stern quarter, flank speed, noisemaker); native = the game's TorpedoEvasion as before", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Restart, Options = new[] { "own", "native" } }).Value ?? "own").Trim().ToLowerInvariant();
            Ctx.Api.EvasionMode = mode == "native" ? "native" : "own";
            var evDebug = cfg.Bind(S, "Debug", false, "Log every evasion tick (torpedo bearing, distance, course, phase)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.EvasionDebug = evDebug.Value;
            Ctx.Api.SubEvasionP.Debug = evDebug.Value;
            OnChanged(evDebug, v => { Ctx.Api.EvasionDebug = v; Ctx.Api.SubEvasionP.Debug = v; });
            // T-141 (ADR-0048): anti-torpedo salvo before the turn (EvasionAtw.cs)
            var atwOn = cfg.Bind(S, "AntiTorpedo", true, "Own evasion (Mode=own) first fires anti-torpedo rockets (RBU, Bofors) at torpedo tracks by time to impact (own ship and neighbours) in range and arc, then turns away as before; also under ROE hold (self-defence)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            Ctx.Api.EvasionAtwOn = atwOn.Value;
            OnChanged(atwOn, v => Ctx.Api.EvasionAtwOn = v);   // every salvo decision checks it
            var atwSafety = cfg.Bind(S, "AntiTorpedoSafetyNm", 0.15f, "No anti-torpedo salvo when an own, allied or neutral ship or submarine is this close to the torpedo (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.EvasionAtwSafetyNm = atwSafety.Value;
            OnChanged(atwSafety, v => Ctx.Api.EvasionAtwSafetyNm = v);   // read on every salvo decision
            var atwReaction = cfg.Bind(S, "AntiTorpedoReactionS", 3f, "Assumed delay order -> launch (s) for the 7 s immunity window", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.EvasionAtwReactionS = atwReaction.Value;
            OnChanged(atwReaction, v => Ctx.Api.EvasionAtwReactionS = v);
            var atwMax = cfg.Bind(S, "AntiTorpedoMaxTracks", 4, "Anti-torpedo fire: how many torpedo tracks (within 4 nm, by time to impact) one ship engages, one free mount each (1..8)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 1, Max = 8 });
            Ctx.Api.EvasionAtwMaxTracks = Math.Max(1, Math.Min(8, atwMax.Value));
            OnChanged(atwMax, v => Ctx.Api.EvasionAtwMaxTracks = Math.Max(1, Math.Min(8, v)));
            var atwCover = cfg.Bind(S, "AntiTorpedoCover", true, "Ships without their own torpedo threat fire anti-torpedo salvoes at torpedoes heading for own/allied ships nearby (no course change)", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            Ctx.Api.EvasionAtwCover = atwCover.Value;
            OnChanged(atwCover, v => Ctx.Api.EvasionAtwCover = v);
            var atwCoverNm = cfg.Bind(S, "AntiTorpedoCoverNm", 4f, "Anti-torpedo cover: protect own/allied ships within this distance of the shooter (nm, 0.5..10)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.5, Max = 10, Step = 0.5, UnitKey = "nm" });
            Ctx.Api.EvasionAtwCoverNm = Math.Max(0.5, Math.Min(10, (double)atwCoverNm.Value));
            OnChanged(atwCoverNm, v => Ctx.Api.EvasionAtwCoverNm = Math.Max(0.5, Math.Min(10, (double)v)));
            var atwFireNm = cfg.Bind(S, "AntiTorpedoFireNm", 1.5f, "Anti-torpedo salvo only when the torpedo track is this close to the shooter (nm); the game's AI fires RBU at torpedoes within 1 nm", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 0.5, Max = 3.0, Step = 0.25, UnitKey = "nm" });
            Ctx.Api.EvasionAtwFireNm = Math.Max(0.5, Math.Min(3.0, (double)atwFireNm.Value));
            OnChanged(atwFireNm, v => Ctx.Api.EvasionAtwFireNm = Math.Max(0.5, Math.Min(3.0, (double)v)));
            var atwDebug = cfg.Bind(S, "AntiTorpedoDebug", false, "Log every refused anti-torpedo salvo with its reason", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            Ctx.Api.EvasionAtwDebug = atwDebug.Value;
            OnChanged(atwDebug, v => Ctx.Api.EvasionAtwDebug = v);

            // T-101 / ADR-0071: submarine torpedo evasion (the game's own state wrapped by the mod, or the mod's own fallback)
            var sp = Ctx.Api.SubEvasionP;
            var subMode = cfg.Bind(S, "SubMode", "native", "native | own: submarine torpedo evasion; native = the game's own TorpedoEvasion wrapped by the mod (log, restore, noisemaker backup), own = the mod's own course/speed/depth evasion", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "native", "own" } });
            sp.Mode = SubModeOf(subMode.Value);
            OnChanged(subMode, v => Ctx.Api.SubEvasionP.Mode = SubModeOf(v));   // a new run reads it at its start
            var subDeep = cfg.Bind(S, "SubDeepDive", true, "Submarine dives to 80% of crush depth when the torpedo cannot reach it", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            sp.DeepDive = subDeep.Value;
            OnChanged(subDeep, v => Ctx.Api.SubEvasionP.DeepDive = v);
            var subNm = cfg.Bind(S, "SubNoisemakerAssist", true, "Submarine: launch a noisemaker when the game has not launched one within the interval", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            sp.NoisemakerAssist = subNm.Value;
            OnChanged(subNm, v => Ctx.Api.SubEvasionP.NoisemakerAssist = v);
            var subNmS = cfg.Bind(S, "SubNoisemakerIntervalS", 30.0, "Submarine: at most one noisemaker per this many seconds (game launches count)", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.Live, Min = 15, Max = 120, Step = 5, UnitKey = "s" });
            sp.NoisemakerIntervalS = Math.Max(15, Math.Min(120, subNmS.Value));
            OnChanged(subNmS, v => Ctx.Api.SubEvasionP.NoisemakerIntervalS = Math.Max(15, Math.Min(120, v)));
            var subRestore = cfg.Bind(S, "SubRestore", true, "Submarine: put the route, telegraph and depth back after the evasion", UiKeyClass.Advanced, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            sp.Restore = subRestore.Value;
            OnChanged(subRestore, v => Ctx.Api.SubEvasionP.Restore = v);
        }

        private static string SubModeOf(string v) { return (v ?? "native").Trim().ToLowerInvariant() == "own" ? "own" : "native"; }

        // ---- IUiDiag (ADR-0046): anti-torpedo counters (T-141)
        public string DiagSection { get { return "evasion"; } }

        public string DiagJson(int limit)
        {
            string atw = Ctx.Api.AtwDiagJson();
            var sub = Ctx.Api.SubEvasion;   // T-101: the submarine runs and counters next to the anti-torpedo ones
            if (sub == null || atw == null || !atw.EndsWith("}", StringComparison.Ordinal)) return atw;
            string head = atw.Substring(0, atw.Length - 1);
            return head + (head.TrimEnd().EndsWith("{", StringComparison.Ordinal) ? "" : ",") + "\"sub\":" + sub.DiagJson(limit) + "}";
        }

        public override void Init(UiContext ctx)
        {
            ctx.Api.EvasionSaveDir = ctx.DataDir("evasion");   // T-256 (ADR-0086 pattern): the route before an evasion next to the game save
            ctx.Release.Add(new ReleaseParticipant("evasion", ctx.Api.ReleaseEvasionUid), ReleaseRegistry.OrderEvasion);
            ctx.Mission.Subscribe(67, "evasion_atw", null, why => ctx.Api.AtwLeaveMission());   // T-145: windows, busy mounts, history of the left mission
            InitSub(ctx);
        }

        // ---- T-101 / ADR-0071: submarine torpedo evasion
        private double _subNextSweep = double.NegativeInfinity;

        private void InitSub(UiContext ctx)
        {
            try
            {
                var api = ctx.Api;
                if (!api.Capabilities.Contains("sub_evasion")) { ctx.Host.LogInfo("sub_evasion disabled: members of the game are missing (the submarine keeps the game's own behaviour)"); return; }
                VbKeys.AutoEvadeForSubs = true;   // VB_AutoEvade appears in the Submarines category of Standing Orders
                api.CreateSubEvasionController();
                ctx.Steering.RegisterPreempt(UnitSteering.Evasion, uid => Ctx.Api.SubEvasion != null && Ctx.Api.SubEvasion.Holds(uid));   // smart attack, SAR and intercept send no orders meanwhile
            }
            catch (Exception e)
            {
                ctx.Host.LogError("sub_evasion init crashed: " + e);
                VbKeys.AutoEvadeForSubs = false;
                ctx.Api.SubEvasion = null;
            }
        }

        public override void Update(UiFrame frame)
        {
            var c = Ctx.Api.SubEvasion;
            if (c == null || !Ctx.Api.Frame.InMission) return;
            var t = Ctx.Api.Frame.MissionTimeS;
            if (!t.HasValue) return;
            if (t.Value < _subNextSweep - 5) _subNextSweep = double.NegativeInfinity;   // the clock went back (a loaded save)
            if (t.Value < _subNextSweep) return;
            _subNextSweep = t.Value + 1.0;
            c.Sweep(t.Value);   // runs of submarines that are gone end here
        }

        public override void Dispose()
        {
            VbKeys.AutoEvadeForSubs = false;
        }
    }
}
