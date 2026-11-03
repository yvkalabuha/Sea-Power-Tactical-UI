using System;
using HarmonyLib;

namespace VoiceBotBridge
{
    /// <summary>
    /// Bodies of the Harmony patches of the interface part (HarmonyLib: not part of the offline selfcheck). Which patch goes where is described by the
    /// PatchSpecs (MenuSpecs, RadialSpecs, SmartAttackSpecs) and installed by HarmonyFeature (T-120 M19, ADR-0043); this class only holds the handlers.
    ///  - postfix ObjectBaseViewModel.GetContextMenuItems -> "XO" (Старпом) submenu (GameApi.AppendBotMenu),
    ///  - radial ring input blocks (camera rotation, map pan / drag / click), behind Radial.InputPatches,
    ///  - the pick click of the smart attack (T-113 X): while GameApi.SmartPickBlocking the click does not reach the game.
    /// Bodies never throw and let the game run on any problem.
    /// </summary>
    internal static class PatchesUi
    {
        // Prefix for InputHandler.handleMouseInputs: the 3D click of the pick does not select a unit.
        internal static bool SmartPickInputPrefix()
        {
            try { return !GameApi.SmartPickBlocking; } catch { return true; }
        }

        // T-199: prefix for InputHandler.OnKeyAction_Cancel (the game's Esc: closes the layers, opens the pause menu). Skipped while a mod window holds a transient
        // element (an open list, a pick / draw mode, a key recording) and the mod windows are shown (no game menu open); UiEscGuard keeps the owners.
        internal static bool EscCancelPrefix()
        {
            try
            {
                if (UiWindowPrefs.Hidden || !UiEscGuard.Swallow(UnityEngine.Time.frameCount)) return true;
                if (Patches.Api != null) Patches.Api.Log("[Ui] esc swallowed owners=" + UiEscGuard.OwnersText);   // i18n-allow
                return false;
            }
            catch { return true; }
        }

        // Sets MouseEventArgs.Handled through the registry (the member is cached by its spec).
        private static void MarkHandled(object args)
        {
            if (args == null || Patches.Api == null) return;
            Patches.Api.Set(args, RadialSpecs.EventHandled, true);
        }

        // Prefix for the map's mouse broker: the pick click is marked handled and skipped.
        internal static bool SmartPickMousePrefix(object __1)
        {
            try
            {
                if (!GameApi.SmartPickBlocking) return true;
                MarkHandled(__1);
                return false;
            }
            catch { return true; }
        }

        // Prefix for rotation / map panning / icon dragging: skipped while the ring blocks input.
        internal static bool RadialPrefix()
        {
            try { return !GameApi.RadialBlocking; } catch { return true; }
        }

        // Prefix for the map's mouse broker: marks the event handled and skips the game's handler while the ring is open.
        internal static bool RadialMousePrefix(object __1)
        {
            try
            {
                if (!GameApi.RadialBlocking) return true;
                MarkHandled(__1);
                return false;
            }
            catch { return true; }
        }

        internal static void ContactMenuPostfix(object __instance, object __result)
        {
            try { if (Patches.Api != null) Patches.Api.AppendContactMenu(__instance, __result); } catch { }
        }

        // Postfix: never lets an exception out (a broken menu must not break the game's own menu).
        internal static void MenuPostfix(object __instance, object __result)
        {
            try { if (Patches.Api != null) Patches.Api.AppendBotMenu(__instance, __result); } catch { }
        }
    }
}
