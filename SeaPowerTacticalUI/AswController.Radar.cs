using System.Text;

namespace VoiceBotBridge
{
    // T-241 (ADR-0099): the search radar of an ASW aircraft (helicopter or plane) is on while it searches; the shared ledger remembers the previous state.
    internal sealed partial class AswController
    {
        private AirRadarLedger _radar;

        /// <summary>The shared radar ledger. Null = the feature is off (stands without a radar keep working).</summary>
        public AirRadarLedger Radar { get { return _radar; } set { _radar = value; if (value != null) value.Sink("asw", s => _game.Log("asw " + s)); } }

        private void RadarTick(Grp g, int uid, AswMember m)
        {
            if (_radar == null) return;
            string assign; g.Assign.TryGetValue(uid, out assign);
            bool? wish = AirRadarRules.AswWants(_cfg.AutoRadar, m.Where, g.Order, assign, AswMath.IsLeaving(m), _airRet.ContainsKey(uid), Attacking(uid), m.Evading);
            if (wish == true && assign == null) wish = null;   // not assigned yet (a reassign is pending): keep as it is
            _radar.Debug = _cfg.Debug;   // the "confirmed" line only with [Asw] Debug
            _radar.Tick(uid, "asw", "search_surface", wish);
        }

        private void RadarRelease(int uid, bool restore)
        {
            if (_radar != null) _radar.Release(uid, "asw", restore);
        }

        private string RadarJson(Grp g)
        {
            var sb = new StringBuilder("{");
            if (_radar != null)
                foreach (int u in g.Members)
                {
                    string s = _radar.StateOf(u); if (s == null) continue;
                    if (sb.Length > 1) sb.Append(',');
                    sb.Append('"').Append(u).Append("\":\"").Append(s).Append('"');
                }
            return sb.Append('}').ToString();
        }
    }
}
