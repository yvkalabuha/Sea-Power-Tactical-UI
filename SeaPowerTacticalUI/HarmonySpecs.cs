namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M19 (ADR-0043): the fire veto. It is installed by the shared Patches.Apply (Common, the voice mod uses the same code), so this PatchSpec only
    /// describes it: the selfcheck and the manifest see the target, the signature is checked against Common/Patches.cs by a test. Required = false and a
    /// capability of its own: the capability fire_veto is Common's (set only for the tactics owner).
    /// </summary>
    internal static class HarmonySpecs
    {
        public static readonly PatchSpec PatchFireVeto = new PatchSpec("fire_veto", "fire_veto_aux", "SeaPower.ObjectBase", "AddEngageTask", new[] { "EngageTask" }, "void", PatchKind.Prefix, "tactics_owner", false);

        /// <summary>T-199: the game's Esc (KeyAction.Cancel) is skipped while a transient element of a mod window is open (UiEscGuard). Required = false: without it only the old behaviour.</summary>
        public static readonly PatchSpec PatchEscCancel = new PatchSpec("ui_esc_cancel", "harmony", "SeaPower.InputHandler", "OnKeyAction_Cancel", new string[0], "void", PatchKind.Prefix, "always", false);

        public static readonly SpecSet Set = SpecSet.Of("harmony", typeof(HarmonySpecs));
    }
}
