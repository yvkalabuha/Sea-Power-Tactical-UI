namespace VoiceBotBridge
{
    /// <summary>The registry self-test in Common/Actions.cs calls GameApi.SnapSelfTest; the real GameApi is not part of this stand.</summary>
    internal sealed partial class GameApi { internal static string SnapSelfTest() { return null; } }
}
