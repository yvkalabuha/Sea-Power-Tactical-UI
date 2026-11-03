using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-175 / ADR-0075 item 3 (ADR-0043): the time acceleration setter for the reset to x1 on danger. Optional: without it only the notice "reduce the acceleration" is shown.
    /// The pause and the message pause come from the core set (CoreSpecs.GamePaused, GameMessagePause), the reading from CoreSpecs.TimeCompression.</summary>
    internal static class AirTimeSpecs
    {
        public static readonly MemberSpec SetTimeCompression = MemberSpec.Method("air_guard_tc", "SeaPower.GameTime", "SetTimeCompression", new[] { "float" }, "void", false, true);

        public static readonly SpecSet Set = SpecSet.Of("air_guard_tc", typeof(AirTimeSpecs));
    }
}
