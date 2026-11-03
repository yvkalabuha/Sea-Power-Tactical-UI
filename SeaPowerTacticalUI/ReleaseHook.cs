using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// Interface mod side of the "Release control" border hook (declared in Common/RestoreControl.cs, ADR-0044):
    /// every participant (asw, sar, cap, evasion) registered in the ReleaseRegistry lets go of the unit, in the registry order.
    /// </summary>
    internal sealed partial class GameApi
    {
        internal Ui.ReleaseRegistry UiRelease;   // set by UiCore
        internal Ui.UnitSteering UiSteering;     // T-100: set by the air_evasion module
        internal bool SteeredByAirEvasion(int uid)
        {
            var s = UiSteering;
            return s != null && s.Owner(uid) == Ui.UnitSteering.AirEvasion;
        }

        partial void ReleaseParticipants(object u)
        {
            var r = UiRelease;
            if (r != null && u != null) r.ReleaseAll(UidOf(u), Log);
        }
    }
}

namespace VoiceBotBridge.Ui
{
    /// <summary>A release participant made of a delegate (id for the log line of a failure).</summary>
    internal sealed class ReleaseParticipant : IReleaseParticipant
    {
        private readonly Action<int> _release;
        public ReleaseParticipant(string id, Action<int> release) { Id = id; _release = release; }
        public string Id { get; private set; }
        public void Release(int unitUid) { _release(unitUid); }
    }
}
