using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-168 / ADR-0064 item 20: bodies of the Harmony patch on TaskforceWeaponReportExtensions.RecordWeaponHitUnit(Taskforce, WeaponBase, ObjectBase) (HitEstimateSpecs.PatchHits,
    /// installed by HarmonyFeature when [HitEstimate] Counter is on). The prefix asks whether the game will count the hit (it does not count the same weapon / target pair twice),
    /// the postfix records it. Both only observe; they never throw into the game.
    /// </summary>
    internal static class HitEstimatePatches
    {
        internal static void Prefix(object __0, object __1, object __2, out bool __state)
        {
            __state = false;
            try { var api = Patches.Api; if (api != null) __state = api.HeBeforeHit(__0, __1, __2); }
            catch { __state = false; }
        }

        internal static void Postfix(object __0, object __1, object __2, bool __state)
        {
            // T-175 / ADR-0075 item 6: a hit on an own aircraft by any side is noted for the loss card; BEFORE the check of the player's own hit (that one is unchanged)
            try { var api0 = Patches.Api; if (api0 != null) api0.AlOnHit(__0, __1, __2); }
            catch { }
            if (!__state) return;
            try { var api = Patches.Api; if (api != null) api.HeOnOwnHit(__0, __1, __2); }
            catch { }
        }
    }
}
