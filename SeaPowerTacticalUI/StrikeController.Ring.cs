using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B (T-328 ring "+/-"): the strike groups as a window of the ring. Window "strike", Kind "asuw". No side effects here; the changes go through strike_group.
    internal sealed partial class StrikeController
    {
        public const string RingWindow = "strike", RingKind = "asuw";

        public bool RingReady { get { return Ready; } }

        public IList<RingGroupOption> RingGroups()
        {
            var res = new List<RingGroupOption>();
            foreach (var g in _groups) res.Add(new RingGroupOption { Window = RingWindow, Name = g.Name, Kind = RingKind, Members = g.Members.Count });
            return res;
        }

        public string RingGroupOf(int uid) { var g = GroupOf(uid); return g == null ? null : g.Name; }

        /// <summary>The admission of an aircraft to a group of this window, no side effects. null = ok; hidden = the type is not for a strike (the ring does not show the group); else the reason code.</summary>
        public string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup)
        {
            var g = Find(group);
            if (g == null) return "unknown_group";
            var a = Air(uid);
            if (a == null) return "not_found";
            string why = StrikeAirMath.RoleOf(a) == "ew" ? null : StrikeAirMath.AircraftWhy(a);
            if (a.Ew != null && (a.Why == "ew_no_api" || a.Why == "ew_inoperable")) return a.Why;   // phase D: a jammer that cannot work is listed, not hidden
            if (why != null) return why == "helicopter" || why == "no_asm" || why == "tv_only" ? "hidden" : why;
            var cur = GroupOf(uid);
            if (cur != null && cur != g && !(ignoreWindow == RingWindow && ignoreGroup == cur.Name)) return "in_group:" + cur.Name;
            if (cur == null)
            {
                string owner = OwnerOf == null ? null : OwnerOf(uid);
                if (owner != null && owner != ignoreWindow + ":" + ignoreGroup)
                {
                    int i = owner.IndexOf(':');
                    return "in_group:" + (i >= 0 ? owner.Substring(i + 1) : owner);
                }
            }
            return null;
        }
    }
}
