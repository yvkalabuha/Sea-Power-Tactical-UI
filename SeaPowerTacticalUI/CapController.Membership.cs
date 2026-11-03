using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-328 (ADR-0127): the data of the command ring about the CAP groups: list, membership, a dry run of the admission rules. No side effects.
    internal sealed partial class CapController
    {
        /// <summary>False while a save restore waits for the world (the ring shows no group sectors then).</summary>
        internal bool RingReady { get { return _deferred == null; } }

        /// <summary>The groups of the window in window order (patrol and alert): Name, Kind "cap", Members. Why is not filled.</summary>
        internal IList<RingGroupOption> RingGroups()
        {
            var res = new List<RingGroupOption>();
            foreach (var g in _groups) res.Add(new RingGroupOption { Window = Win, Name = g.Name, Kind = "cap", Members = g.Members.Count });
            return res;
        }

        /// <summary>The CAP group the unit is a member of, null = none.</summary>
        internal string RingGroupOf(int uid)
        {
            Mem m;
            var g = GroupOf(uid, out m);
            return g == null ? null : g.Name;
        }

        /// <summary>Forget the cached birds (the next AdmitWhy reads them again). The tick refreshes the cache every second anyway.</summary>
        internal void ForgetRingBirds() { _birdsAt = -1e9; }

        /// <summary>Birds of the last second (the ring opens often); a unit missing in the cache forces one fresh read.</summary>
        private IDictionary<int, CapBird> RingBirds(int uid)
        {
            if (_lastBirds == null || _now - _birdsAt >= 1.0 || _now < _birdsAt) { _lastBirds = _g.Birds(); _birdsAt = _now; }
            var idx = Index(_lastBirds);
            if (!idx.ContainsKey(uid)) { _lastBirds = _g.Birds(); _birdsAt = _now; idx = Index(_lastBirds); }
            return idx;
        }

        /// <summary>Dry run of "cap_group add": null = the unit may join the group, else the code (not_found, hidden, game_cap_zone, player, strike_loadout, hangar_taken, in_group:X,
        /// unknown_group, restore_waiting). (ignoreWindow, ignoreGroup) = the group the unit leaves in a move: it does not count as another owner.</summary>
        internal string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup)
        {
            if (_deferred != null) return "restore_waiting";
            var g = Find(group);
            if (g == null) return "unknown_group";
            int took; CapBird use;
            return AdmitCore(g, uid, RingBirds(uid), ignoreWindow, ignoreGroup, out took, out use);
        }
    }
}
