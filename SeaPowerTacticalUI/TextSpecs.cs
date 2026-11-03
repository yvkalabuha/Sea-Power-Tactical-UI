namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M10 (ADR-0043): the game members of the mod's text module: the game's language table (LanguageResourceHandler) is the first source of
    /// UiText and the game language for [Ui] Language=auto. The instance comes from GameApi.Singleton(CoreSpecs.LanguageHandler).
    /// </summary>
    internal static class TextSpecs
    {
        private const string Cap = "text";
        private const string Lrh = "SeaPower.LanguageResourceHandler";

        public static readonly MemberSpec GetText = MemberSpec.Method(Cap, Lrh, "getText", new[] { "string", "string", "bool", "bool" }, "string");

        /// <summary>The game language code ("en", "ru"); optional: the mod's tables then fall back to English.</summary>
        public static readonly MemberSpec Language = MemberSpec.Prop(Cap, Lrh, "Language", false);

        /// <summary>System.EventHandler: the language changed (without it the plugin polls at cheap moments).</summary>
        public static readonly MemberSpec LanguageChanged = MemberSpec.Event(Cap, Lrh, "LanguageChanged", false);

        public static readonly SpecSet Set = SpecSet.Of("text", typeof(TextSpecs));
    }
}
