using System;

namespace VoiceBotBridge
{
    /// <summary>T-198 (ADR-0086): body of the Harmony postfix on SaveLoadManager.SaveGame (target: SaveHookSpecs.PatchSaveGame). Never throws into the game.</summary>
    internal static class SaveHookPatches
    {
        private static bool _loggedFail;

        private static DateTime? _startedUtc;

        /// <summary>T-217: the start of the save call (the game stamps the .sav at the start; the postfix runs after seconds of serialization).</summary>
        internal static void SaveGamePrefix()
        {
            try { _startedUtc = DateTime.UtcNow; }
            catch { }
        }

        internal static void SaveGamePostfix(string __result)
        {
            try
            {
                var s = _startedUtc; _startedUtc = null;
                var api = Patches.Api;
                if (api != null) api.OnGameSaved(__result, s);
            }
            catch (Exception e)
            {
                if (_loggedFail) return;
                _loggedFail = true;
                try { if (Patches.Api != null) Patches.Api.Log("save hook failed: " + e.GetType().Name + ": " + e.Message); } catch { }
            }
        }
    }
}
