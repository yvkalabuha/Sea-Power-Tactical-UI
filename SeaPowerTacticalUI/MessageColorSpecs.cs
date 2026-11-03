namespace VoiceBotBridge
{
    /// <summary>
    /// T-329: the game members of the red hostile messages. The message window shows Alert.AlertContent, converted from Unity rich text inside
    /// MainGameViewModel.RaiseAlert; a prefix wraps the text of a hostile message in a colour tag.
    /// msg_alert_text + msg_classified: RaiseAlert(string) texts, only while Taskforce.CheckContactClassifiedMessage runs (the classification of contacts;
    /// the text is recognised by the game's own templates). The stable line has no RaiseContactAlert: a new contact is never coloured.
    /// </summary>
    internal static class MessageColorSpecs
    {
        private const string Cap = "msg_hostile_red";
        private const string Gvm = "SeapowerUI.ViewModels.MainGameViewModel";

        public static readonly PatchSpec PatchAlertText = new PatchSpec("msg_alert_text", Cap, Gvm, "RaiseAlert", new[] { "string" }, "void", PatchKind.Prefix, "always");
        public static readonly PatchSpec PatchClassified = new PatchSpec("msg_classified", Cap, "SeaPower.Taskforce", "CheckContactClassifiedMessage", new string[0], "void", PatchKind.PrefixPostfixFinalizer, "always");

        public static readonly SpecSet Set = SpecSet.Of("messages", typeof(MessageColorSpecs));
    }
}
