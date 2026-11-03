using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase D (plan 34.3, X5): the jammer aircraft of the strike groups are watched by the air threat service like the aircraft of the ASW groups (window "strike", kind "ew"):
    // the area of a group is its stations (the aircraft sits there), the members are the jammers only. The strikers are not watched (design 5.11: they are not sent away by the service).
    internal sealed partial class StrikeController
    {
        /// <summary>The groups for the air threat service: one per strike group that has a jammer; Uids = the jammers, AreaPts = their stations (the wait point when there is none).</summary>
        internal IList<AirGuardGroup> AirGuardGroups()
        {
            var res = new List<AirGuardGroup>();
            foreach (var g in _groups)
            {
                var d = new AirGuardGroup { Window = "strike", Name = g.Name, Kind = "ew" };
                foreach (int uid in g.Members)
                {
                    var a = Air(uid);
                    if (a == null || StrikeAirMath.RoleOf(a) != "ew") continue;
                    d.Uids.Add(uid);
                    var st = EwOf(g, uid);
                    if (st != null && st.Plan != null && st.Plan.Why == "ok") d.AreaPts.Add(st.Plan.StationPt);
                }
                if (d.Uids.Count == 0) continue;
                if (d.AreaPts.Count == 0 && g.WaitPt.HasValue) d.AreaPts.Add(g.WaitPt.Value);
                res.Add(d);
            }
            return res;
        }
    }
}
