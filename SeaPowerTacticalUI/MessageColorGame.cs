using System;

namespace VoiceBotBridge
{
    // T-329: the game side of the red hostile messages (reflection only).
    internal sealed partial class GameApi
    {
        /// <summary>The game's getText(section, key); null without the text handler.</summary>
        internal Func<string, string, string> MessageTextLookup()
        {
            var h = Singleton(CoreSpecs.LanguageHandler);
            return h == null ? null : GameTextLookup(h);
        }

    }
}
