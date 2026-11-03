namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): read-only view of the retreat steering for the "air_guard" labels.</summary>
    internal sealed partial class AirRetreatSteer
    {
        /// <summary>True while the unit is steered; mode cover | away | land and whether it already orbits at the cover.</summary>
        internal bool TryState(int uid, out string mode, out bool orbiting)
        {
            St s;
            if (!_st.TryGetValue(uid, out s)) { mode = null; orbiting = false; return false; }
            mode = s.Mode; orbiting = s.Orbiting;
            return true;
        }
    }
}
