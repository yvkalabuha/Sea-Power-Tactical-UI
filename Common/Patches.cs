using System;
using System.Reflection;
using HarmonyLib;

namespace VoiceBotBridge
{
    /// <summary>
    /// Harmony life cycle and the fire veto (shared by both mods, never part of the offline selfcheck: references HarmonyLib).
    ///  - prefix ObjectBase.AddEngageTask(EngageTask) -> fire veto near neutrals (GameApi.ShouldVetoEngage).
    /// The menu and radial-ring patches live in the UI mod (PatchesUi.cs) and reuse Patches.Instance.
    /// A patch is installed only if its target exists with the expected signature. An unverified game version does NOT
    /// disable the patches (customer decision Q4): it is logged as a warning and reported as a mod event.
    /// </summary>
    internal static class Patches
    {
        internal const BindingFlags AllInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static GameApi _api;
        private static Harmony _harmony;

        /// <summary>The Harmony instance of this mod after Apply (null before / after Remove / on error).</summary>
        internal static Harmony Instance { get { return _harmony; } }
        internal static GameApi Api { get { return _api; } }

        /// <summary>Returns "on" (at least one patch installed), "off" (nothing to patch) or "error". veto=false skips the fire veto.</summary>
        public static string Apply(GameApi api, string harmonyId, string gameVersion, string knownVersions, bool veto, Action<string> log)
        {
            _api = api;
            api.PatchesApplied.Clear();
            try
            {
                api.HarmonyVersionKnown = TacticsMath.VersionKnown(gameVersion, knownVersions);
                if (!api.HarmonyVersionKnown)
                {
                    log("WARN game version '" + gameVersion + "' is not in Harmony.GameVersions ('" + knownVersions + "'): patches are applied anyway");
                    api.EmitHarmonyWarning(gameVersion, knownVersions);
                }
                _harmony = new Harmony(harmonyId);

                var ob = api.FindType("SeaPower.ObjectBase");
                var et = api.FindType("SeaPower.EngageTask");
                MethodInfo engage = ob == null || et == null ? null : ob.GetMethod("AddEngageTask", AllInst, null, new[] { et }, null);
                if (veto && engage != null && engage.ReturnType == typeof(void))
                {
                    var owners = Harmony.GetPatchInfo(engage);
                    if (!Ownership.MayPatch(harmonyId, owners == null ? null : owners.Owners))
                    {
                        log("patch fire_veto skipped (another VoiceBot mod already patched AddEngageTask)");
                        return Status(api);
                    }
                    _harmony.Patch(engage, prefix: new HarmonyMethod(typeof(Patches).GetMethod("EngagePrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                    api.PatchesApplied.Add(ModManifest.PatchFireVeto);
                    api.Capabilities.Add("fire_veto");
                }
                else log("patch fire_veto skipped (" + (veto ? "AddEngageTask(EngageTask) not found" : "not the tactics owner") + ")");

                return Status(api);
            }
            catch (Exception e)
            {
                log("Harmony failed: " + e.GetType().Name + ": " + e.Message);
                Remove();
                api.PatchesApplied.Clear();
                api.Capabilities.Remove("harmony"); api.Capabilities.Remove("bot_menu"); api.Capabilities.Remove("fire_veto");
                return "error";
            }
        }

        /// <summary>Sets the "harmony" capability by the installed patches and returns "on" or "off".</summary>
        internal static string Status(GameApi api)
        {
            if (api.PatchesApplied.Count > 0) api.Capabilities.Add("harmony");
            return api.PatchesApplied.Count > 0 ? "on" : "off";
        }

        public static void Remove()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            _harmony = null;
        }

        // Prefix: false = the task is not added. Any problem = fire is allowed.
        private static bool EngagePrefix(object[] __args)
        {
            try { return _api == null || __args == null || __args.Length == 0 || !_api.ShouldVetoEngage(__args[0]); }
            catch { return true; }
        }
    }
}
