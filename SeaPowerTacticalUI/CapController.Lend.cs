using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-179 / ADR-0079 section 5: an alert bird is lent to a patrol group that misses a bird, and comes back after it has landed.
    // The bird (Mem) moves to the patrol group (AirOwnership too) with LentFrom = the alert group; the alert group keeps a shadow record (Grp.Lent)
    // that is not a ready machine. Whoever ends first (stop, disband, landing) sends it back: BackFromLend / ReleaseLent / OrphanLent.
    internal sealed partial class CapController
    {
        private sealed class LentRec { public Mem M; public string To; }

        /// <summary>The slot a lent bird takes in a patrol group with a continuity gap and no free slot: the one whose holder leaves first.</summary>
        private int GapSlot(Grp p)
        {
            int best = -1; double bl = double.MaxValue;
            foreach (var h in p.Members)
            {
                if (!h.Airborne || h.Leaving != null || h.Slot < 0 || h.Slot >= p.N) continue;
                double l = h.LeaveInS >= 0 ? h.LeaveInS : 1e8;
                if (l < bl) { bl = l; best = h.Slot; }
            }
            return best;
        }

        /// <summary>After the raid: ask CapAlertMath.AfterClear; on "lend" the bird goes to the patrol group. false = the caller sends it home (rtb_alert with res.Why).</summary>
        private bool TryLend(Grp alert, Mem m, CapBird b, double nh, out AfterClearResult res)
        {
            var offers = new List<PatrolOffer>();
            foreach (var p in _groups)
            {
                if (p.Kind != "patrol" || !p.Ordered || p.Zone.Rect == null) continue;
                var lanes = CapMath.Lanes(p.Zone, Math.Max(1, p.N), p.P, CoverOf(p));
                if (lanes == null || lanes.Count == 0) continue;
                int fs = FreeSlot(p, null);
                int slot = fs >= 0 ? fs : (p.Gap ? GapSlot(p) : -1);
                if (p.Gap && slot < 0) slot = 0;
                var pt = lanes[Math.Max(0, Math.Min(lanes.Count - 1, slot < 0 ? 0 : slot))].Center;
                offers.Add(new PatrolOffer
                {
                    Group = p.Name, Accept = p.AcceptAlert, FreeSlot = slot, Gap = p.Gap, SlotPt = pt, MissilePct = p.P.MissilePct,
                    FuelThresholdNm = CapMath.FuelThresholdNm(b.PatrolKn, 0, b.MaxRangeNm, p.P)
                });
            }
            var ab = new AlertBird { Uid = m.Uid, Lat = b.Lat, Lon = b.Lon, ReserveNm = b.ReserveNm, PatrolKn = b.PatrolKn, AamNow = b.AamNow, AamStart = m.AamStart };
            res = CapAlertMath.AfterClear(ab, alert.A.After, offers, alert.P.AlertHandoverMinS);
            if (res.Op != "lend") return false;
            var patrol = Find(res.To);
            if (patrol == null) { res = new AfterClearResult { Op = "rtb_alert", Why = "no_patrol" }; return false; }
            DoLend(alert, patrol, m, b, res, nh);
            return true;
        }

        private void DoLend(Grp alert, Grp patrol, Mem m, CapBird b, AfterClearResult res, double nh)
        {
            string raid = m.AlertRaid ?? "";
            RestoreThrottle(m);   // the dash of the scramble ends: the patrol flies at the speed of its standing orders
            alert.Members.Remove(m);
            alert.Lent.Add(new LentRec { M = m, To = patrol.Name });
            _own.Release(m.Uid); _own.Claim(m.Uid, Win, patrol.Name);
            m.Alert = false; m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.Handed = false; m.FarSince = -1; m.AlertThrottle = -1; m.Barrage = false;
            m.LentFrom = alert.Name; m.Slot = res.Slot; m.RouteSig = null; m.OnStation = false; m.Reason = null; m.Leaving = null; m.PlanSlot = -1;
            patrol.Members.Add(m);
            patrol.Gap = false;   // the gap is filled by this bird: the next one is not lent into the same hole
            string prev;
            if (_g.SetRoe(m.Uid, patrol.Roe, out prev) == null) { if (!m.RoeSet) m.PrevRoe = prev; m.RoeSet = true; }
            Log("alert clear uid=" + m.Uid + " group=" + alert.Name + " -> lend to=" + patrol.Name + " slot=" + res.Slot + " aam=" + b.AamNow + "/" + m.AamStart
                + " reserve_nm=" + F(res.ReserveNm) + " need_nm=" + F(res.NeedNm) + " hostile_nm=" + (nh == double.MaxValue ? "-" : F(nh)));
            Evt("cap_alert", "{\"group\":" + Q(alert.Name) + ",\"op\":\"lend\",\"uids\":[" + m.Uid + "],\"raid\":" + Q(raid) + ",\"to\":" + Q(patrol.Name) + ",\"slot\":" + res.Slot + "}");
        }

        /// <summary>A lent bird landed in the patrol group: it goes back to its alert group (stand alert again) or, when that group is gone / stopped, becomes a free machine.</summary>
        private void BackFromLend(Grp patrol, Mem m)
        {
            var alert = m.LentFrom == null || m.LentOrphan ? null : Find(m.LentFrom);
            string from = m.LentFrom;
            patrol.Members.Remove(m);
            if (alert != null) alert.Lent.RemoveAll(r => r.M == m);
            _own.Release(m.Uid);
            if (m.Airborne) LandedReset(m);
            m.LentFrom = null; m.LentOrphan = false; m.Slot = -1; m.Allowed = false; m.PlanSlot = -1;
            if (alert != null && alert.Kind == "alert" && alert.Ordered)
            {
                _own.Claim(m.Uid, Win, alert.Name);
                m.Alert = true; m.AlertState = null; m.AlertRaid = null; m.RaidIds = null;
                alert.Members.Add(m);
                Log("alert back uid=" + m.Uid + " from=" + patrol.Name + " to=" + alert.Name);
                Evt("cap_alert", "{\"group\":" + Q(alert.Name) + ",\"op\":\"back\",\"uids\":[" + m.Uid + "],\"raid\":\"\",\"to\":" + Q(patrol.Name) + "}");
            }
            else
            {
                AbortKey(m, "lend_end");
                Log("member out uid=" + m.Uid + " group=" + patrol.Name + " (lent from " + (from ?? "-") + ", no alert group: free)");
            }
        }

        /// <summary>Stop / disband of the patrol group: its lent birds go back to their alert group at once (an airborne one flies home on hold first).</summary>
        private void ReleaseLent(Grp patrol, string why)
        {
            foreach (var m in new List<Mem>(patrol.Members))
            {
                if (m.LentFrom == null) continue;
                var alert = m.LentOrphan ? null : Find(m.LentFrom);
                patrol.Members.Remove(m);
                if (alert != null) alert.Lent.RemoveAll(r => r.M == m);
                _own.Release(m.Uid);
                string from = m.LentFrom;
                m.LentFrom = null; m.LentOrphan = false; m.Slot = -1; m.OnStation = false; m.RouteSig = null;
                bool back = alert != null && alert.Kind == "alert" && alert.Ordered;
                if (m.Airborne && m.Leaving == null)
                {
                    try { _g.Rtb(m.Uid); } catch (Exception) { }
                    m.Leaving = "lend_end"; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false;
                    if (patrol.P.RtbRoeHold) RoeHold(m, "rtb");
                }
                if (back)
                {
                    _own.Claim(m.Uid, Win, alert.Name);
                    m.Alert = true; alert.Members.Add(m);
                    Log("alert back uid=" + m.Uid + " from=" + patrol.Name + " to=" + alert.Name + " (" + why + ")");
                    Evt("cap_alert", "{\"group\":" + Q(alert.Name) + ",\"op\":\"back\",\"uids\":[" + m.Uid + "],\"raid\":\"\",\"to\":" + Q(patrol.Name) + "}");
                }
                else Log("member out uid=" + m.Uid + " group=" + patrol.Name + " (" + why + ", lent from " + from + ")");
            }
        }

        /// <summary>The alert group stops or ends: its lent birds stay with the patrol until they land, then they are free machines (BackFromLend finds no alert group).</summary>
        private void OrphanLent(Grp alert)
        {
            foreach (var r in alert.Lent)
            {
                r.M.LentOrphan = true;   // T-195: explicit flag (was the implicit LentFrom = "")
                Log("alert orphan uid=" + r.M.Uid + " group=" + alert.Name + " stays with " + r.To + " until it lands");
            }
            alert.Lent.Clear();
        }
    }
}
