using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-098 D1 (ADR-0037 section 2): bodies of the Harmony patches of the game's Standing Orders window and its save / load / reset / text lookup.
    /// The targets are the PatchSpecs of DoctrineSpecs; HarmonyFeature installs them all or none (T-120 M19): a missing one removes the `standing_orders`
    /// capability (the settings then live in memory and in the "Modes" fallback submenu). Bodies never throw and never change the game's
    /// own result except the label lookup of our own VB_* keys. All work is done by GameApi.Doctrine* (DoctrineGame.cs).
    /// </summary>
    internal static class PatchesDoctrine
    {
        // The three window builders run in the constructor and at every selection change; our items are re-added to the fresh tabs.
        internal static void BuildGlobalPostfix(object __instance) { try { if (Patches.Api != null) Patches.Api.DoctrineAppend(__instance, DoctrineLevel.Global); } catch { } }
        internal static void BuildFormationPostfix(object __instance) { try { if (Patches.Api != null) Patches.Api.DoctrineAppend(__instance, DoctrineLevel.Group); } catch { } }
        internal static void BuildUnitPostfix(object __instance) { try { if (Patches.Api != null) Patches.Api.DoctrineAppend(__instance, DoctrineLevel.Unit); } catch { } }

        internal static void SavePostfix(object __instance, object[] __args)
        {
            try { if (Patches.Api != null && __args != null && __args.Length > 1) Patches.Api.DoctrineSaved(__instance, __args[0], __args[1] as string); } catch { }
        }

        internal static void LoadPostfix(object __instance, object[] __args)
        {
            try { if (Patches.Api != null && __args != null && __args.Length > 1) Patches.Api.DoctrineLoaded(__instance, __args[0], __args[1] as string); } catch { }
        }

        internal static void GlobalPostfix(object __instance, bool __result)
        {
            try { if (Patches.Api != null) Patches.Api.DoctrineGlobalSaved(__instance, __result); } catch { }
        }

        internal static void ResetPostfix(object __instance)
        {
            try { if (Patches.Api != null) Patches.Api.DoctrineReset(__instance); } catch { }
        }

        // Hot path: the cheap prefix test is inside DoctrineTextOverride; only our VB_* keys ever return false (result filled).
        internal static bool TextPrefix(string section, string key, ref string __result)
        {
            try
            {
                string r = null;
                if (!GameApi.DoctrineTextOverride(section, key, ref r)) return true;
                __result = r;
                return false;
            }
            catch { return true; }
        }
    }
}
