namespace VoiceBotBridge
{
    /// <summary>T-192 C1: without a SAR order the rafts of the last read are in the view and ui_state, but not drawn (the world is the one of AswSarTests).</summary>
    internal static class AswT192Rafts
    {
        public static string Run() { return AswSarTests.RaftsIdle(); }
    }
}
