using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (ADR-0096): what the ASW controller drives right now, as labels for the "Orders:" field. One source "asw" gives every kind of its units:
    /// sub attacks (asw_attack), strike group members (asw_strike), SAR helicopters (sar) and course / area members (asw). The same unit gets the strongest one
    /// (UaPrio: a sub attack beats the tactic). Reads the controller state only; never touches the game.
    /// </summary>
    internal sealed partial class AswController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "asw"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            var seen = new HashSet<int>();
            foreach (var r in _targets.Values)
            {
                if (r.State == "done" || r.State == "idle") continue;
                AddAttack(into, seen, r.Shooter, r);
                AddAttack(into, seen, r.Second, r);
            }
            foreach (var g in _groups)
            {
                if (g.Order == "none") continue;
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (!_live.TryGetValue(u, out m) || m.Where != "air" || m.Evading) continue;   // deck rows are not units on the map; evasion has its own source
                    UnitActionLabel l = null;
                    if (g.Order == "strike")
                    {
                        if (seen.Contains(u)) continue;
                        l = UnitActionMap.AswStrike(u, g.St.Rtb.Contains(u) ? "rtb" : "idle", 0);
                    }
                    else if (g.Order == "sar") l = SarLabel(g, m);
                    else if (g.Order == "course" || g.Order == "area") l = MemberLabel(g, m);
                    if (l != null) into.Add(l);
                }
            }
        }

        private void AddAttack(IList<UnitActionLabel> into, HashSet<int> seen, int uid, TargetRec r)
        {
            if (uid == 0 || !seen.Add(uid)) return;
            AswMember m;
            if (!_live.TryGetValue(uid, out m) || m.Where != "air") { seen.Remove(uid); return; }
            var l = UnitActionMap.AswAttack(uid, r.State, r.Id);
            if (l == null) { seen.Remove(uid); return; }
            into.Add(l);
        }

        private UnitActionLabel MemberLabel(Grp g, AswMember m)
        {
            string tac, why = null;
            if (!g.Assign.TryGetValue(m.Uid, out tac)) tac = null;
            g.Leaving.TryGetValue(m.Uid, out why);
            Job job;
            g.Jobs.TryGetValue(m.Uid, out job);
            bool drop = job != null && job.IsDrop;
            bool reserve = tac == "reserve";
            bool dip = tac == "dip" && !drop;
            int row = -1, rows = 0;   // place among the rows ahead of the ship (nearest = 1), not the running number from the start of the band
            if (job != null && job.BarrierSeq >= 0 && (!drop || _now < job.FirstAt - 60 || _now < job.DoneAt && LeftOf(m, job) >= job.BuoysBefore))   // T-236 / T-245: the row number is shown while the aircraft flies to the row (before the first drop point: by the estimate, or while no buoy has fallen - the estimate is short when the position is unknown), not only after the reset
            {
                var ahead = RowsAhead(g);
                rows = ahead.Count;
                for (int i = 0; i < ahead.Count; i++) if (ahead[i].Seq == job.BarrierSeq) { row = i; break; }
            }
            bool standby = reserve || g.StandbyAt.ContainsKey(m.Uid) && tac == null;
            string show = null;   // the tactic / pattern named as in the ASW window (barrier, corridor, dipping sonar; grid, MAD in an area); none = plain "ASW"
            if (g.Order == "course")
            {
                if (tac == "barrier" || tac == "corridor" || tac == "dip") show = tac;
                else show = g.Tactics.Contains("barrier") ? "barrier" : g.Tactics.Contains("corridor") ? "corridor" : g.Tactics.Contains("dip") ? "dip" : null;
            }
            else if (g.Order == "area")
            {
                if (tac == "grid" || tac == "dip" || tac == "mad") show = tac;
                else if (g.Tactics.Count == 1) show = g.Tactics[0];
            }
            return UnitActionMap.AswMember(m.Uid, g.Order, show, drop, dip, why, row, standby, rows);
        }

        private UnitActionLabel SarLabel(Grp g, AswMember m)
        {
            double[] tg;
            int raft = g.Targets.TryGetValue(m.Uid, out tg) ? (int)tg[0] : 0;
            bool hover = g.Hovering.Contains(m.Uid);
            bool returning = g.Leaving.ContainsKey(m.Uid) || (m.Onboard > 0 && raft == 0 && !hover);
            return UnitActionMap.SarHelo(m.Uid, raft, hover, m.Onboard, m.Capacity, returning);
        }
    }
}
