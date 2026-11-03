namespace VoiceBotBridge
{
    /// <summary>
    /// T-198 (ADR-0086, ADR-0043): the game members of the save hook. Every game save (pause menu, quick save, autosave) goes through
    /// SaveLoadManager.SaveGame(string, SaveType, bool, string) on the main thread and returns the path of the .sav; the postfix writes the mod's
    /// state files for that path at once (PatchesUi-independent, bodies in SaveHookPatches.cs). GameTime.missionElapsedTime is the value the game
    /// writes to [GameTime] MissionElapsedTime of the .sav (the stamp), Globals.currentMissionFilePath is the file that was loaded or started.
    /// All optional: without the patch the mod falls back to watching the last_save marker (the first session of a process only).
    /// </summary>
    internal static class SaveHookSpecs
    {
        private const string Cap = "save_hook";

        public static readonly PatchSpec PatchSaveGame = new PatchSpec("save_game", Cap, "SeaPower.SaveLoadManager", "SaveGame", new[] { "string", "SaveType", "bool", "string" }, "string", PatchKind.PrefixPostfix, "always", false);
        public static readonly MemberSpec GameElapsed = MemberSpec.Prop(Cap, "SeaPower.GameTime", "missionElapsedTime", false, true);
        public static readonly MemberSpec CurrentMission = MemberSpec.Field(Cap, "SeaPower.Globals", "currentMissionFilePath", false, true);

        public static readonly SpecSet Set = SpecSet.Of("save_hook", typeof(SaveHookSpecs));
    }
}
