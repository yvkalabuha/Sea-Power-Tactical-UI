using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-331: the game side of the "We're on position" guard (reflection only).
    internal sealed partial class GameApi
    {
        private readonly Dictionary<int, string> _wpqPlacer = new Dictionary<int, string>();

        /// <summary>The unit of a waypoint task (VisualActionTask._baseObject); null when unknown.</summary>
        internal object WpqUnit(object task)
        {
            return task == null || !Present(WaypointQuietSpecs.TaskBase) ? null : Get(task, WaypointQuietSpecs.TaskBase);
        }

        internal int WpqUid(object unit) { return unit == null ? -1 : UidOf(unit); }

        /// <summary>The last mod part that set a route of the aircraft (cap_patrol, asw_goto, sar_goto, retreat): the line of the log only.</summary>
        internal string WpqPlacer(int uid)
        {
            string s;
            return _wpqPlacer.TryGetValue(uid, out s) ? s : null;
        }

        internal void WpqNotePlaced(int uid, string tag)
        {
            if (uid >= 0 && tag != null) _wpqPlacer[uid] = tag;
        }

        internal void WpqClearPlaced() { _wpqPlacer.Clear(); }
    }
}
