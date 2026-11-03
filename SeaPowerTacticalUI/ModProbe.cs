using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-136 / ADR-0045 6, 6a: asks whether another mod (Better TacMap, Vessel Noise Display) has Harmony patches installed.
    /// Read-only: it never patches anything. The controller calls it at mission entry / save load only.
    /// </summary>
    internal sealed class ModProbe : IModProbe
    {
        public bool HarmonyHas(string harmonyId)
        {
            try { return !string.IsNullOrEmpty(harmonyId) && HarmonyLib.Harmony.HasAnyPatches(harmonyId); }
            catch (Exception) { return false; }
        }

        public bool AssemblyLoaded(string name)
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(a.GetName().Name, name, StringComparison.Ordinal)) return true;
            }
            catch (Exception) { }
            return false;
        }
    }
}
