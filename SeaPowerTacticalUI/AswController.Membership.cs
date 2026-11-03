using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-328 (ADR-0127): the data of the command ring about the ASW / Strike / SAR groups: list, membership, a dry run of the admission rules. No side effects.
    internal sealed partial class AswController
    {
        /// <summary>False while a save restore waits for the world (the ring shows no group sectors then).</summary>
        internal bool RingReady { get { return _deferred == null; } }

        /// <summary>The groups of the window in window order. Kind: order "sar" -> "sar", "strike" -> "strike", else "asw". Why is not filled.</summary>
        internal IList<RingGroupOption> RingGroups()
        {
            var res = new List<RingGroupOption>();
            foreach (var g in _groups)
                res.Add(new RingGroupOption { Window = "asw", Name = g.Name, Kind = g.Order == "sar" ? "sar" : g.Order == "strike" ? "strike" : "asw", Members = g.Members.Count });
            return res;
        }

        /// <summary>The ASW / Strike / SAR group the unit is a member of, null = none.</summary>
        internal string RingGroupOf(int uid)
        {
            var g = GroupOf(uid);
            return g == null ? null : g.Name;
        }

        /// <summary>Dry run of "asw_group add" for the ring: null = the unit may join, else the code: restore_waiting, unknown_group, not_found, the ASW / SAR / strike exclusion
        /// (no_asw_load, aew, player, ext_sar, game_patrol, game_sar_zone, landed, lost, no_capacity, no_torpedo, not_aircraft), in_group:X (a CAP group), hangar_taken.
        /// Stricter than the add command (which takes any aircraft into a search group): the ring does not offer a fighter an ASW group. Call EnsureLive() first.
        /// (ignoreWindow, ignoreGroup) = the group the unit leaves in a move: it does not count as a foreign owner.</summary>
        internal string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup)
        {
            if (_deferred != null) return "restore_waiting";
            var g = Find(group);
            if (g == null) return "unknown_group";
            AswMember m;
            if (!_live.TryGetValue(uid, out m)) return "not_found";
            string why;
            why = OrderExclusion(g.Order, m);   // T-346: the same source as the add command with kind
            if (why != null) return why;
            string win;
            string owner = ForeignOwner(uid, out win);
            if (owner != null && !(ignoreGroup != null && win == ignoreWindow && owner == ignoreGroup)) return "in_group:" + owner;
            if (IsSlot(m) && g.Members.Contains(uid) == false && (GroupOf(uid) != null || owner != null) && FreeSlot(m.DeckUid, m.Type, null) == null) return "hangar_taken";
            return null;
        }
    }
}
