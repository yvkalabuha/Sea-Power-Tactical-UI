using System;

namespace VoiceBotBridge
{
    /// <summary>T-094: body of the Harmony postfix on SceneCreator.LaunchWeapons (target: BuoyReviveSpecs.PatchLaunchWeapons). Never throws into the game.</summary>
    internal static class BuoyRevivePatches
    {
        private static bool _loggedFail;

        /// <summary>T-296: prefix of ObjectSonobouyControlViewModel.SonobouyStatus(Bomb). false = the mod's text is the result (buoy without a launch platform).</summary>
        internal static bool SonobuoyStatusPrefix(object __0, ref string __result)
        {
            try
            {
                var api = Patches.Api;
                if (api == null) return true;
                var s = api.SonobuoyStatusNoPlatform(__0);
                if (s == null) return true;
                __result = s;
                return false;
            }
            catch
            {
                __result = "";   // never the original here: with a null platform it throws
                return false;
            }
        }

        internal static void LaunchWeaponsPostfix(object __instance)
        {
            try
            {
                var api = Patches.Api;
                if (api != null) api.OnWeaponsLaunched(__instance);
            }
            catch (Exception e)
            {
                if (_loggedFail) return;
                _loggedFail = true;
                try { if (Patches.Api != null) Patches.Api.Log("buoy revive failed: " + e.GetType().Name + ": " + e.Message); } catch { }
            }
        }
    }
}
