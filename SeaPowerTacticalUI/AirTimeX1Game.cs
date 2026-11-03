using System;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 item 3: the game side of AirTimeX1. Main thread.
    internal sealed partial class GameApi
    {
        /// <summary>The time acceleration now (x1, x2 ...); 0 = paused as the game reports it; null = unknown.</summary>
        internal double? AgTimeCompression()
        {
            try { return Present(CoreSpecs.TimeCompression) ? D(GetStatic(CoreSpecs.TimeCompression)) : null; }
            catch { return null; }
        }

        /// <summary>SetTimeCompression(1). null = done, "unsupported" = no member, else the error.</summary>
        internal string AgSetX1()
        {
            if (!Present(AirTimeSpecs.SetTimeCompression)) return "unsupported";
            try { CallStatic(AirTimeSpecs.SetTimeCompression, 1f); return null; }
            catch (Exception e) { return "failed:" + (e.InnerException ?? e).GetType().Name; }
        }

        /// <summary>Game paused (x0) or a mission message popup is up (the message pause).</summary>
        internal bool AgPausedOrMessage()
        {
            try { if (Present(CoreSpecs.GamePaused) && BoolOf(CallStatic(CoreSpecs.GamePaused))) return true; } catch { }
            try { return MessagePopupOpen(); } catch { return false; }
        }
    }
}
