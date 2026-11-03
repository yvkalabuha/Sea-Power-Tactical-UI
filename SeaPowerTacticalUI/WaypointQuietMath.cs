using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-331: pure book of the "We're on position" messages (the game raises [OrderLog] ReachedLastWaypoint when the LAST waypoint of a unit of the player is
    /// finished). A unit under the control of the mod (CAP/ASW/SAR group) gets a message at most once per RepeatS seconds of the mission time; the rest is
    /// quiet. A unit of the player (no owner) is never touched. No game, no Unity.
    /// </summary>
    internal sealed class WaypointQuietBook
    {
        private sealed class Row { public double Last; public int Quiet; }
        private readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();

        public WaypointQuietBook(double repeatS) { RepeatS = repeatS; }

        public double RepeatS { get; set; }

        /// <summary>true = stay quiet: a unit of the mod (owner != null), the switch is on and the previous shown message is younger than RepeatS.</summary>
        public bool Quiet(int uid, string owner, double now, bool on)
        {
            if (!on || owner == null) return false;
            if (RepeatS <= 0) return true;
            Row r;
            if (!_rows.TryGetValue(uid, out r)) return false;
            double dt = now - r.Last;
            return dt >= 0 && dt < RepeatS;   // dt < 0: the clock went back (a loaded save) -> show
        }

        /// <summary>A message was shown: remember the time, return the number of quiet ones since the previous show (and reset it).</summary>
        public int Shown(int uid, double now)
        {
            Row r;
            if (!_rows.TryGetValue(uid, out r)) { r = new Row(); _rows[uid] = r; }
            int q = r.Quiet;
            r.Quiet = 0;
            r.Last = now;
            return q;
        }

        /// <summary>A message was kept quiet.</summary>
        public void Suppressed(int uid)
        {
            Row r;
            if (!_rows.TryGetValue(uid, out r)) { r = new Row { Last = double.NegativeInfinity }; _rows[uid] = r; }
            r.Quiet++;
        }

        public void Reset() { _rows.Clear(); }

        /// <summary>The number of quiet messages not yet reported (the self-test and the log).</summary>
        public int QuietSince(int uid) { Row r; return _rows.TryGetValue(uid, out r) ? r.Quiet : 0; }
    }

    internal static class WaypointQuietMath
    {
        /// <summary>"waypoint reached uid=7 owner=CAP-1 placer=cap_patrol -> shown quiet_since=3" (or "-> quiet").</summary>
        public static string LogLine(int uid, string owner, string placer, bool shown, int quietSince)
        {
            return "waypoint reached uid=" + uid + " owner=" + (owner ?? "-") + " placer=" + (placer ?? "-") + " -> "
                   + (shown ? "shown quiet_since=" + quietSince : "quiet");
        }

        public static string SelfTest()
        {
            var b = new WaypointQuietBook(300);
            if (b.Quiet(1, "CAP-1", 10, true)) return "first_shown: quiet at the first message";
            if (b.Shown(1, 10) != 0) return "first_shown: quiet_since";
            if (!b.Quiet(1, "CAP-1", 70, true)) return "repeat_quiet: 60 s later must be quiet";
            b.Suppressed(1);
            if (b.Quiet(1, "CAP-1", 311, true)) return "repeat_quiet: 301 s later must be shown";
            if (b.QuietSince(1) != 1) return "repeat_quiet: quiet_since=" + b.QuietSince(1);
            if (b.Shown(1, 311) != 1) return "repeat_quiet: Shown returned the wrong count";
            if (b.QuietSince(1) != 0) return "repeat_quiet: count not reset";
            if (b.Quiet(2, null, 0, true) || b.Quiet(2, null, 1, true)) return "player_unit: a unit of the player is never quiet";
            b.Shown(3, 0);
            if (b.Quiet(3, "ASW-1", 5, false)) return "off: switch off must show";
            b.RepeatS = 0;
            if (!b.Quiet(4, "ASW-1", 0, true)) return "zero_repeat: a unit of the mod is always quiet";
            if (b.Quiet(4, null, 0, true)) return "zero_repeat: player unit";
            b.RepeatS = 300;
            b.Shown(5, 100);
            if (b.Quiet(5, "SAR-1", 50, true)) return "clock back: a loaded save must show";
            b.Reset();
            if (b.Quiet(1, "CAP-1", 311.5, true) || b.QuietSince(1) != 0) return "reset";
            string l = LogLine(7, "CAP-1", "cap_patrol", true, 3);
            if (l != "waypoint reached uid=7 owner=CAP-1 placer=cap_patrol -> shown quiet_since=3") return "log line: " + l;
            if (LogLine(7, null, null, false, 0) != "waypoint reached uid=7 owner=- placer=- -> quiet") return "log line quiet";
            return null;
        }
    }
}
