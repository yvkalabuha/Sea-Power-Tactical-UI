namespace VoiceBotBridge.Dev
{
    /// <summary>Identity of the developer test channel (ADR-0046). No reference to the UI assembly: slots are plain strings.</summary>
    internal static class DevInfo
    {
        public const string Guid = "ru.voicebot.seapower.ui.dev";
        public const string UiGuid = "ru.voicebot.seapower.ui";
        public const string Version = "0.1.0";
        public const string Protocol = "sptui-dev/1";
        public const string DiagSlotKey = "ru.voicebot.seapower.ui/diag";
        public const string LinkSlotKey = "ru.voicebot.seapower.ui/link";
    }
}
