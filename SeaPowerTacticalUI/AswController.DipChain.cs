using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-176 C8 / ADR-0076 s.3.1: the dipping sonar by course. Points on the axis ahead of the group, the leapfrog: the next point is the farthest taken + s,
    // one point ahead per aircraft; at the standoff the aircraft repeats the dip in place.
    internal sealed partial class AswController
    {
        // T-188: aircraft (and types) the game said have no dipping sonar
        private readonly HashSet<int> _noDipUids = new HashSet<int>();
        private readonly HashSet<string> _noDipTypes = new HashSet<string>();

        private static string DipTypeKey(AswMember m) { return m.Type + "|" + m.Loadout; }

        /// <summary>The game refused the dip order. "no_dipping_sonar": the aircraft leaves the dipping tactic (warned once per uid); anything else: the old pause.</summary>
        private void DipRefused(Grp g, AswMember m, string err)
        {
            if (err != "no_dipping_sonar") { Warn(g, "dip_failed", err); PauseJob(g, m, "dip"); return; }
            if (_noDipUids.Add(m.Uid)) Warn(g, "no_dipping_sonar", "uid=" + I(m.Uid) + " " + m.Name + " (" + m.Type + ")", 0);
            _noDipTypes.Add(DipTypeKey(m));
            m.Dipping = false; m.DipSrc = "refused";
            AswMember lm;
            if (_live.TryGetValue(m.Uid, out lm)) { lm.Dipping = false; lm.DipSrc = "refused"; }
            g.Assign[m.Uid] = "reserve"; g.Jobs.Remove(m.Uid); g.Dirty = true;
            _game.Log("asw dip refused " + g.Name + " uid=" + I(m.Uid) + " type=" + m.Type);
        }

        private void DipChain(Grp g, AswMember m, AswTarget t)
        {
            if (JobPending(g, m)) return;
            var pl = g.Plan; var spec = pl.Course; var a = pl.Anchor;
            double rd = _game.DipRangeNm(m.Uid);
            if (rd <= 0) rd = 2.0;
            double s = AswPatternMath.DipStepV3(rd);
            double v = AnchorKn(a), now = AlongNm(g), fly = FlyToLeadS(g, m), sDrop = now + v * fly / 3600.0;
            // points behind the group are forgotten
            pl.DipTaken.RemoveAll(x => x < now - rd);
            var pts = new List<GeoPt>();
            bool hold = false; double xs = 0;
            for (int tries = 0; tries < 8 && pts.Count == 0; tries++)
            {
                if (a.Stand)
                {
                    // a fixed target: points on the ring min(L, X/2); the nearest unvisited one
                    double rad = Math.Min(spec.DipLNm, Math.Max(spec.CorridorXNm / 2, s));
                    var ring = AswPatternMath.StandingRing(rad, s);
                    int best = -1; double bd = 0;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        if (pl.DipVisited.Contains(i)) continue;
                        double dd = (m.Lat != 0 || m.Lon != 0) ? AswMath.DistNm(new GeoPt(m.Lat, m.Lon), AswMath.ToGeo(a.O, 0, ring[i])) : i;
                        if (best < 0 || dd < bd) { best = i; bd = dd; }
                    }
                    if (best < 0) { pl.DipVisited.Clear(); return; }
                    pl.DipVisited.Add(best);
                    var gp = Guard(g, new List<GeoPt> { AswMath.ToGeo(a.O, 0, ring[best]) }, "dip", 0, m);
                    pts.AddRange(gp);
                    continue;
                }
                xs = AswPatternMath.LeapfrogNext(pl.DipTaken, s, sDrop + Math.Max(2.0, s), now + spec.DipLNm, out hold);
                var p = AswMath.ToGeo(a.O, a.AxisDeg, new NmPt(xs, 0));
                var kept = Guard(g, new List<GeoPt> { p }, "dip", 0, m);
                if (!hold) pl.DipTaken.Add(xs);
                if (kept.Count == 0) { if (hold) { g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = "dip", DoneAt = _now + 30, NoQueue = true }; return; } continue; }   // land / shallow: the chain goes on to the next point
                pts.AddRange(kept);
            }
            if (pts.Count == 0) { g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = "dip", DoneAt = _now + 30, NoQueue = true }; return; }
            CancelRoute(g, m.Uid);
            string err = _game.Dip(m.Uid, pts);
            if (err != null)
            {
                if (!hold && pl.DipTaken.Count > 0) pl.DipTaken.RemoveAt(pl.DipTaken.Count - 1);
                DipRefused(g, m, err); return;
            }
            g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = "dip", Count = pts.Count, DoneAt = _now + AswRouteGuard.JobSeconds(FromOf(m, pts), pts, AircraftKn, g.P.DipHoldS) };
            var line = new AswLineView { Kind = "dip_chain", Tactic = "dip" }; line.Points.AddRange(pts); g.Lines.Add(line);
            if (g.Lines.Count > 12) g.Lines.RemoveAt(0);
            _game.Log("asw pattern " + g.Name + " dip uid=" + I(m.Uid) + " x=" + N(Math.Round(xs, 2)) + " s=" + N(s) + (hold ? " hold" : "") + " taken=" + I(pl.DipTaken.Count));
            int dipCount = 0; foreach (var kv in g.Assign) if (kv.Value == "dip") dipCount++;
            if (dipCount == 1 && AswMath.DipLeapfrog(AswMath.DipScreenKn(s, g.P.DipHoldS), g.V)) Warn(g, "dip_single", null, 600);
        }
    }
}
