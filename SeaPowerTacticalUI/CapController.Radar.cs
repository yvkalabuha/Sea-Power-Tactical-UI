namespace VoiceBotBridge
{
    // T-241 (ADR-0099): the search radar of a CAP aircraft is on while it flies its task; the shared ledger remembers the previous state.
    internal sealed partial class CapController
    {
        private AirRadarLedger _radar;

        /// <summary>The shared radar ledger. Null = the feature is off (stands without a radar keep working).</summary>
        public AirRadarLedger Radar { get { return _radar; } set { _radar = value; if (value != null) value.Sink(Win, Log); } }

        /// <summary>[Cap] AutoRadar (live): false gives every radar back on the next tick.</summary>
        public bool AutoRadar = true;

        private void RadarTick(Mem m)
        {
            if (_radar == null) return;
            _radar.Debug = _p.Debug;   // the "confirmed" line only with [Cap] Debug (set per holder right before its Tick)
            _radar.Tick(m.Uid, Win, "air", AirRadarRules.CapWants(AutoRadar, m.Airborne, m.Leaving, m.AlertState));
        }

        private void RadarRelease(Mem m, bool restore)
        {
            if (_radar != null) _radar.Release(m.Uid, Win, restore);
        }

        private string RadarState(int uid) { return _radar != null ? _radar.StateOf(uid) : null; }
    }
}
