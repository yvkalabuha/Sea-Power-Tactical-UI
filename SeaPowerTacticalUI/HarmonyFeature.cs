using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// One patch of the UI mod: its PatchSpec (target, signature) and the bodies. The bodies are named by nameof on the static handler class; a PatchHandler
    /// without a host is a description only (fire_veto: Patches.Apply of Common installs it for both mods).
    /// </summary>
    internal sealed class PatchHandler
    {
        public readonly PatchSpec Spec;
        public readonly MethodInfo Prefix, Postfix, Finalizer;

        public PatchHandler(PatchSpec spec, Type host, string prefix, string postfix, string finalizer = null)
        {
            Spec = spec;
            if (host == null) return;
            const BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (prefix != null) Prefix = Own(host, prefix, f);
            if (postfix != null) Postfix = Own(host, postfix, f);
            if (finalizer != null) Finalizer = Own(host, finalizer, f);
        }

        private static MethodInfo Own(Type host, string name, BindingFlags f)
        {
            var m = host.GetMethod(name, f);
            if (m == null) throw new MissingMethodException(host.Name, name);
            return m;
        }
    }

    /// <summary>Installs the Harmony patches (config section Harmony) after all modules have run Init, then prepares the Standing Orders flag store.</summary>
    internal sealed class HarmonyFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        /// <summary>Game versions the patches were verified on (ADR-0043 7): the developer edits this on a game update; the config value only adds to it.</summary>
        internal const string VerifiedGameVersions = "0.8.6-261014,0.8.7-261021,0.8.7-261026";

        // Every patch of the UI mod (the plan T-120 M19; a test keeps one handler per PatchSpec id). The groups below install them all or none.
        internal static readonly PatchHandler[] Handlers =
        {
            new PatchHandler(MenuSpecs.PatchBotMenu, typeof(PatchesUi), null, nameof(PatchesUi.MenuPostfix)),
            new PatchHandler(MenuSpecs.PatchContactMenu, typeof(PatchesUi), null, nameof(PatchesUi.ContactMenuPostfix)),
            new PatchHandler(RadialSpecs.PatchCamera, typeof(PatchesUi), nameof(PatchesUi.RadialPrefix), null),
            new PatchHandler(RadialSpecs.PatchMapPan, typeof(PatchesUi), nameof(PatchesUi.RadialPrefix), null),
            new PatchHandler(RadialSpecs.PatchMapDrag, typeof(PatchesUi), nameof(PatchesUi.RadialPrefix), null),
            new PatchHandler(RadialSpecs.PatchMapClick, typeof(PatchesUi), nameof(PatchesUi.RadialMousePrefix), null),
            new PatchHandler(SmartAttackSpecs.PatchPick3d, typeof(PatchesUi), nameof(PatchesUi.SmartPickInputPrefix), null),
            new PatchHandler(SmartAttackSpecs.PatchPickMap, typeof(PatchesUi), nameof(PatchesUi.SmartPickMousePrefix), null),
            new PatchHandler(DoctrineSpecs.PatchBuildGlobal, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.BuildGlobalPostfix)),
            new PatchHandler(DoctrineSpecs.PatchBuildFormation, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.BuildFormationPostfix)),
            new PatchHandler(DoctrineSpecs.PatchBuildUnit, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.BuildUnitPostfix)),
            new PatchHandler(DoctrineSpecs.PatchSave, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.SavePostfix)),
            new PatchHandler(DoctrineSpecs.PatchLoad, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.LoadPostfix)),
            new PatchHandler(DoctrineSpecs.PatchGlobal, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.GlobalPostfix)),
            new PatchHandler(DoctrineSpecs.PatchReset, typeof(PatchesDoctrine), null, nameof(PatchesDoctrine.ResetPostfix)),
            new PatchHandler(DoctrineSpecs.PatchText, typeof(PatchesDoctrine), nameof(PatchesDoctrine.TextPrefix), null),
            new PatchHandler(IdentifySpecs.PatchRequest, typeof(IdentifyPatches), nameof(IdentifyPatches.Prefix), nameof(IdentifyPatches.Postfix), nameof(IdentifyPatches.IdentifyFinalizer)),
            new PatchHandler(IdentifySpecs.PatchAlert, typeof(IdentifyPatches), null, nameof(IdentifyPatches.AlertPostfix)),
            new PatchHandler(PerfSpecs.PatchSpawnEffect, typeof(LightSmokePatches), null, nameof(LightSmokePatches.SpawnPostfix)),
            new PatchHandler(PerfSpecs.PatchDirect, typeof(LightSmokePatches), null, nameof(LightSmokePatches.DirectPostfix)),
            new PatchHandler(PerfSpecs.PatchChaff, typeof(LightSmokePatches), nameof(LightSmokePatches.ChaffPrefix), nameof(LightSmokePatches.ChaffPostfix)),
            new PatchHandler(MessageColorSpecs.PatchAlertText, typeof(MessageColorPatches), nameof(MessageColorPatches.AlertTextPrefix), null),   // T-329
            new PatchHandler(MessageColorSpecs.PatchClassified, typeof(MessageColorPatches), nameof(MessageColorPatches.ClassifiedPrefix), null, nameof(MessageColorPatches.ClassifiedFinalizer)),
            new PatchHandler(MessageColorSpecs.PatchContactAlert, typeof(MessageColorPatches), nameof(MessageColorPatches.ContactAlertPrefix), null),
            new PatchHandler(WaypointQuietSpecs.PatchFinishWp, typeof(WaypointQuietPatches), nameof(WaypointQuietPatches.FinishPrefix), nameof(WaypointQuietPatches.FinishPostfix), nameof(WaypointQuietPatches.FinishFinalizer)),   // T-331
            new PatchHandler(WaypointQuietSpecs.PatchFinishStation, typeof(WaypointQuietPatches), nameof(WaypointQuietPatches.FinishPrefix), nameof(WaypointQuietPatches.FinishPostfix), nameof(WaypointQuietPatches.FinishFinalizer)),
            new PatchHandler(WaypointQuietSpecs.PatchIsLast, typeof(WaypointQuietPatches), nameof(WaypointQuietPatches.IsLastPrefix), nameof(WaypointQuietPatches.IsLastPostfix)),
            new PatchHandler(HitEstimateSpecs.PatchHits, typeof(HitEstimatePatches), nameof(HitEstimatePatches.Prefix), nameof(HitEstimatePatches.Postfix)),   // T-168
            new PatchHandler(HarmonySpecs.PatchEscCancel, typeof(PatchesUi), nameof(PatchesUi.EscCancelPrefix), null),   // T-199
            new PatchHandler(SaveHookSpecs.PatchSaveGame, typeof(SaveHookPatches), nameof(SaveHookPatches.SaveGamePrefix), nameof(SaveHookPatches.SaveGamePostfix)),   // T-198, T-217
            new PatchHandler(BuoyReviveSpecs.PatchLaunchWeapons, typeof(BuoyRevivePatches), null, nameof(BuoyRevivePatches.LaunchWeaponsPostfix)),   // T-094
            new PatchHandler(BuoyReviveSpecs.PatchSonobuoyStatus, typeof(BuoyRevivePatches), nameof(BuoyRevivePatches.SonobuoyStatusPrefix), null),   // T-296
            new PatchHandler(HarmonySpecs.PatchFireVeto, null, null, null),   // installed by Patches.Apply (Common)
        };

        private static PatchHandler[] Of(params PatchSpec[] specs)
        {
            var res = new List<PatchHandler>();
            foreach (var sp in specs)
                foreach (var h in Handlers) if (h.Spec == sp) res.Add(h);
            return res.ToArray();
        }

        private IUiSetting<bool> _harmonyEnabled;
        private IUiSetting<bool> _buoyRevive;
        private IUiSetting<string> _harmonyVersions;

        public HarmonyFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "harmony"; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(HarmonySpecs.Set); check.Specs(SaveHookSpecs.Set); check.Specs(BuoyReviveSpecs.Set); }
        public override string[] DependsOn { get { return new[] { "menu", "radial", "doctrine", "identify", "smart_attack", "intercept", "range_rings", "perf", "hit_estimate", "wp_quiet", "messages" }; } }

        public override void Bind(IUiConfig cfg)
        {
            _harmonyEnabled = cfg.Bind("Harmony", "Enabled", true, "Install the Harmony patches (Bot context submenu, fire veto near neutrals). false = they are never installed", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" });
            _buoyRevive = cfg.Bind("Harmony", "BuoyRevive", true, "Launch again the sonobuoys of a loaded save whose aircraft is gone (T-094/T-296, on by default since T-299). false = as in the game. Applies after a restart", UiKeyClass.Advanced, new UiSettingMeta { Section = "general" });
            _harmonyVersions = cfg.Bind("Harmony", "GameVersions", "0.8.6-261014", "Extra game versions the patches are known to work on, comma-separated (the mod's own list is built in). Another version only produces a warning; the patches stay on", UiKeyClass.Internal);
        }

        public override void Init(UiContext ctx)
        {
            var host = ctx.Host;
            var doc = ctx.Get<DoctrineSettings>();
            var rad = ctx.Get<RadialConfig>();
            var icp = ctx.Get<IcParams>();
            bool icMenu = icp != null && icp.Enabled;   // T-138: the contact entry needs the menu patch also when the XO menu is off
            var hes = ctx.Get<HeSettings>();   // T-168: the weapon-report hook is installed only while the counter is on
            bool heCounter = hes != null && hes.Enabled && hes.Counter;
            bool alLoss = _api.AgParams != null && _api.AgParams.Mode != "off";   // T-175: the same hook notes the hits on own aircraft for the loss card
            var rrp = ctx.Get<RangeRingParams>();
            bool rrMenu = rrp != null && rrp.Enabled;   // T-162: the pin item of a contact needs the menu patch also without the XO menu
            try
            {
                if (_harmonyEnabled.Value)
                {
                    var hs = Patches.Apply(_api, ModManifest.HarmonyId, host.GameVersion, UiConfigMigrator.UnionCsv(VerifiedGameVersions, _harmonyVersions.Value), ctx.Plan.TacticsOwner, m => host.LogInfo(m));
                    _api.DoctrineForceFallback = doc.ForceFallback;
                    var log = (Action<string>)(m => host.LogInfo(m));
                    // the capability stays only for a group that is on and installed; "drop" = the capability goes when the group is off by config as well
                    Install("menu", "bot_menu", ctx.Get<MenuSettings>().Enabled || icMenu || rrMenu, "Menu.Enabled=false, Intercept.Enabled=false and RangeRings.Enabled=false", true, Of(MenuSpecs.PatchBotMenu, MenuSpecs.PatchContactMenu), log);
                    Install("radial camera", null, rad.InputPatches, "Radial.InputPatches=false", false, Of(RadialSpecs.PatchCamera), log);
                    Install("radial map", "radial_map", rad.InputPatches && rad.OnMap, rad.InputPatches ? "Radial.OnMap=false" : "Radial.InputPatches=false", false, Of(RadialSpecs.PatchMapPan, RadialSpecs.PatchMapDrag, RadialSpecs.PatchMapClick), log);
                    Install("smart pick", "smart_pick_block", ctx.Get<SmartAttackSettings>().ConsumePickClick, "SmartAttack.ConsumePickClick=false", true, Of(SmartAttackSpecs.PatchPick3d, SmartAttackSpecs.PatchPickMap), log);
                    Install("standing orders", "standing_orders", doc.Enabled, "[StandingOrders] Enabled=false", true,
                        Of(DoctrineSpecs.PatchBuildGlobal, DoctrineSpecs.PatchBuildFormation, DoctrineSpecs.PatchBuildUnit, DoctrineSpecs.PatchSave, DoctrineSpecs.PatchLoad, DoctrineSpecs.PatchGlobal, DoctrineSpecs.PatchReset, DoctrineSpecs.PatchText), log);
                    _api.IdentifyActive = Install("identify", "identify", true, null, true, Of(IdentifySpecs.PatchRequest, IdentifySpecs.PatchAlert), log);
                    Install("hit estimate", "hit_estimate_hits", heCounter || alLoss, "HitEstimate.Counter=false and [AirGuard] Mode=off", false, Of(HitEstimateSpecs.PatchHits), log);
                    Install("light smoke", "light_smoke", true, null, true, Of(PerfSpecs.PatchSpawnEffect), log);   // T-165: always; the option is a run-time switch
                    Install("light smoke direct", "light_smoke_direct", true, null, false, Of(PerfSpecs.PatchDirect), log);
                    Install("message color", "msg_hostile_red", true, null, true, Of(MessageColorSpecs.PatchAlertText, MessageColorSpecs.PatchClassified), log);   // T-329: always; the option is a run-time switch
                    Install("message color contact", "msg_hostile_red", true, null, false, Of(MessageColorSpecs.PatchContactAlert), log);   // T-329: beta line only (optional)
                    Install("waypoint quiet", "wp_reached_quiet", true, null, false, Of(WaypointQuietSpecs.PatchFinishWp, WaypointQuietSpecs.PatchFinishStation, WaypointQuietSpecs.PatchIsLast), log);   // T-331: always; the option is a run-time switch, a missing target only switches the guard off
                    Install("esc", null, true, null, false, Of(HarmonySpecs.PatchEscCancel), log);   // T-199: Esc closes the list of a mod window, not the game
                    _api.SaveHookActive = Install("save hook", "save_hook", true, null, false, Of(SaveHookSpecs.PatchSaveGame), log);   // T-198 (ADR-0086): mod files are written by the game save, not by watching last_save
                    bool revive = _buoyRevive.Value;   // T-094/T-296: orphan sonobuoys of a loaded save are launched again, together with the status prefix
                    const string reviveOff = "[Harmony] BuoyRevive=false";
                    bool status = Install("buoy status", "buoy_revive", revive, reviveOff, false, Of(BuoyReviveSpecs.PatchSonobuoyStatus), log)
                                  && _api.PatchesApplied.Contains(BuoyReviveSpecs.PatchSonobuoyStatus.Id);   // Install is true also when an optional target is missing
                    if (revive && !status) { log("buoy revive patches skipped: patch buoy_status is not installed"); _api.Capabilities.Remove("buoy_revive"); _api.BuoyReviveActive = false; }
                    else _api.BuoyReviveActive = Install("buoy revive", "buoy_revive", revive, reviveOff, false, Of(BuoyReviveSpecs.PatchLaunchWeapons), log);
                    Install("light smoke chaff", "light_smoke_chaff", true, null, false, Of(PerfSpecs.PatchChaff), log);
                    _api.HarmonyStatus = hs == "error" ? hs : Patches.Status(_api);
                    host.LogInfo("harmony " + _api.HarmonyStatus + ", patches: " + string.Join(",", _api.PatchesApplied.ToArray()));
                }
            }
            catch (Exception e) { _api.HarmonyStatus = "error"; host.LogError("harmony crashed: " + e); }
            // Standing Orders store: the cells of the game's window or memory (safe also without Harmony: the hooks go the old way until it exists)
            try { _api.DoctrineForceFallback = doc.ForceFallback; _api.EnsureFlagStore(); }
            catch (Exception e) { host.LogError("flag store crashed: " + e); }
        }

        /// <summary>
        /// Resolves every target of the group by signature (the same resolver as the selfcheck); installs all of them or, when a required one is
        /// missing, none and takes the capability away. enabled=false installs nothing (the capability goes only when dropWhenOff). true = installed.
        /// </summary>
        private bool Install(string name, string cap, bool enabled, string offWhy, bool dropWhenOff, PatchHandler[] group, Action<string> log)
        {
            Action drop = () => { if (cap != null) _api.Capabilities.Remove(cap); };
            if (!enabled) { log(name + " patches off (" + offWhy + ")"); if (dropWhenOff) drop(); return false; }
            if (Patches.Instance == null) { log(name + " patches skipped: Harmony is off"); if (dropWhenOff) drop(); return false; }
            if (cap != null && !_api.Capabilities.Contains(cap)) { log(name + " patches skipped: capability " + cap + " is missing"); return false; }
            var targets = new MethodInfo[group.Length];
            bool all = true;
            for (int i = 0; i < group.Length; i++)
            {
                targets[i] = _api.PatchTarget(group[i].Spec);
                if (targets[i] == null && group[i].Spec.Required) all = false;
                if (targets[i] == null) log("patch " + group[i].Spec.Id + " skipped (target not found: " + group[i].Spec.Key + ")");
            }
            if (!all) { log(name + " patches: a required target is missing, none installed"); drop(); return false; }
            try
            {
                for (int i = 0; i < group.Length; i++)
                {
                    if (targets[i] == null) continue;
                    var h = group[i];
                    Patches.Instance.Patch(targets[i],
                        prefix: h.Prefix == null ? null : new HarmonyMethod(h.Prefix),
                        postfix: h.Postfix == null ? null : new HarmonyMethod(h.Postfix),
                        finalizer: h.Finalizer == null ? null : new HarmonyMethod(h.Finalizer));
                    if (!_api.PatchesApplied.Contains(h.Spec.Id)) _api.PatchesApplied.Add(h.Spec.Id);
                }
                return true;
            }
            catch (Exception e) { log(name + " patches failed: " + e.GetType().Name + ": " + e.Message); drop(); return false; }
        }

        public override void Dispose()
        {
            try { Patches.Remove(); } catch { }
        }
    }
}
