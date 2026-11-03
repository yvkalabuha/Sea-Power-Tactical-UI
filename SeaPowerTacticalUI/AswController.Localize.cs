using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-176 C11 / ADR-0072 s.4.2, s.4.7: the localization of a contact: the cross outside the spook radius (the submarine flees from a buoy it hears closer than ~2 nm),
    // the active buoy ahead, the holder's dipping sonar abeam; the lost-contact ring (AswLocalizeMath).
    internal sealed partial class AswController
    {
        private AswMember Dropper(Grp g, GeoPt cg) { return Nearest(g, cg, m => BuoyCount(m) > 0); }

        private double FlyS(AswMember m, GeoPt p)
        {
            return (m.Lat == 0 && m.Lon == 0) ? 0 : AswMath.DistNm(new GeoPt(m.Lat, m.Lon), p) / SpeedOf(m) * 3600;
        }

        /// <summary>The attack on this contact is about to start (a shooter flies to it, the torpedo is not in the water yet): the cross waits, a buoy there would warn the submarine.</summary>
        private bool CrossDeferred(ContactRec rec)
        {
            TargetRec r;
            if (!_targets.TryGetValue(rec.Id, out r) || r.State == "done") return false;
            if (r.State == "in_water" || r.State == "assess") return false;   // the weapon is in the water: the buoys help to keep the contact
            AswMember sh = LiveOf(r.Shooter);
            if (sh == null) return false;
            return FlyS(sh, new GeoPt(rec.Lat, rec.Lon)) <= _cfg.Loc.AttackFirstS || r.State == "engaged" || r.State == "verify";
        }

        private void Localize(Grp g, ContactRec rec)
        {
            if (CrossDeferred(rec)) { rec.CrossPending = true; _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " cross waits for the attack"); }
            else LayCross(g, rec);
            DipOnContact(g, rec, true);
        }

        /// <summary>The cross: four passive buoys at r >= the spook radius around the estimate shifted along the course by what the submarine covers while the buoys fall, an active one ahead.</summary>
        private void LayCross(Grp g, ContactRec rec)
        {
            rec.CrossPending = false;
            var cg = new GeoPt(rec.Lat, rec.Lon);
            var dropper = Dropper(g, cg);
            if (dropper == null) return;
            string ammo; BuoyType bt;
            if (!PickFor(dropper, out ammo, out bt)) return;
            double rr = AswMath.REff(bt, g.P).Nm;
            var shift = AswLocalizeMath.Lead(rec.CourseDeg, rec.SpeedKn, FlyS(dropper, cg) + 120);
            double r; bool spook;
            var cross = AswLocalizeMath.SpookCross(SpookR(_cfg.Loc), rr, shift, out r, out spook);
            if (spook) _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " cross inside the spook radius: R=" + N(Math.Round(rr, 2)) + " r=" + N(Math.Round(r, 2)));
            int avail = AvailFor(dropper, bt);
            if (cross.Count > avail) cross.RemoveRange(avail, cross.Count - avail);
            var pts = ToGeoList(cg, 0, cross);
            _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " cross r=" + N(Math.Round(r, 2)) + " shift=" + N(Math.Round(Math.Sqrt(shift.X * shift.X + shift.Y * shift.Y), 2)));
            IssueDrop(g, dropper, ammo, bt, pts, "contact", null, "contact", AswMath.LocalizeGapNm(rr));   // ADR-0063: denser by design, gap from the type's range
            if (dropper.ActiveBuoys > 0)
            {
                var ahead = AswLocalizeMath.ActiveAhead(r, rec.CourseDeg);
                IssueActive(g, dropper, AswMath.ToGeo(cg, 0, new NmPt(shift.X + ahead.X, shift.Y + ahead.Y)), "contact");
            }
        }

        /// <summary>The holder dips abeam of the estimate at the spook radius, the sides alternate with every dip (first = true: the first dip; the holder is chosen).</summary>
        private void DipOnContact(Grp g, ContactRec rec, bool first)
        {
            var cg = new GeoPt(rec.Lat, rec.Lon);
            AswMember dipper;
            if (first) dipper = Nearest(g, cg, m => m.Dipping);
            else { dipper = null; AswMember h; if (_live.TryGetValue(rec.Holder, out h) && h.Where == "air" && h.Dipping) dipper = h; }
            if (dipper == null) { if (first) { var dr = Dropper(g, cg); if (dr != null) rec.Holder = dr.Uid; } return; }
            var shift = AswLocalizeMath.Lead(rec.CourseDeg, rec.SpeedKn, FlyS(dipper, cg) + g.P.DipHoldS / 2);
            var off = AswLocalizeMath.DipOffset(SpookR(_cfg.Loc), rec.CourseDeg, rec.Side);
            rec.Side = -rec.Side;
            var pt = AswMath.ToGeo(cg, 0, new NmPt(shift.X + off.X, shift.Y + off.Y));
            if (Guard(g, new List<GeoPt> { pt }, "dip", 0, dipper).Count == 0)
            {
                // T-172 C3: the point lies over land / shallow water / inside an air-defence zone: the dipper is not sent there
                _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " dip skipped: land, shallow water or air-defence zone");
                return;
            }
            CancelRoute(g, dipper.Uid);   // T-172 B7: the screen line of the tactic goes, then the dip on the contact
            _game.Dip(dipper.Uid, new List<GeoPt> { pt });
            g.Jobs[dipper.Uid] = new Job { Uid = dipper.Uid, Tactic = "contact", Count = 1, DoneAt = _now + g.P.DipHoldS + 60 };
            rec.Holder = dipper.Uid;
            rec.NextDipS = _now + g.P.DipHoldS;
        }

        private void HoldContact(Grp g, ContactRec rec)
        {
            if (rec.CrossPending && !CrossDeferred(rec)) LayCross(g, rec);
            if (Attacking(rec.Holder)) return;   // 0.27.10: the holder is on its attack run
            AswMember h;
            if (!_live.TryGetValue(rec.Holder, out h) || h.Where != "air" || !h.Dipping || _now < rec.NextDipS) return;
            if (AirRetreatOf(h.Uid)) return;
            DipOnContact(g, rec, false);
        }

        // ---- the lost-contact ring -----------------------------------------------------------------------------------

        /// <summary>True while a lost-contact ring of this contact works in any group (the attack run waits for it, T-173).</summary>
        internal bool RingActive(int contactId)
        {
            foreach (var g in _groups) foreach (var rec in g.Contacts) if (rec.Id == contactId && rec.Ring) return true;
            return false;
        }

        private void Lost(Grp g, ContactRec rec)
        {
            // the contact is not on the list any more: a ring of radius v (lost time + flight) around the last place, shifted along the course (it grows every LostExpandS)
            _game.Event("asw_contact_lost", Obj("group", g.Name, "contact", rec.Id, "track", rec.Name, "lat", rec.Lat, "lon", rec.Lon));
            NoteAlert(rec.Id, "lost");
            rec.Ring = true; rec.RingFromS = rec.LastSeenS; rec.RingNextS = _now; rec.RingLat = rec.Lat; rec.RingLon = rec.Lon; rec.RingDipped.Clear();
            _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " lost: ring around " + rec.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + rec.Lon.ToString("0.0000", CultureInfo.InvariantCulture));
            RingStep(g, rec);
        }

        private void EndRing(Grp g, ContactRec rec, string why)
        {
            rec.Ring = false;
            g.Lines.RemoveAll(l => l.Kind == "lost_ring");
            _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " ring ends (" + why + ")");
            _game.Event("asw_contact_ring_end", Obj("group", g.Name, "contact", rec.Id, "track", rec.Name, "reason", why));
            if (why != "found") g.Contacts.Remove(rec);
        }

        private void RingStep(Grp g, ContactRec rec)
        {
            var cg = new GeoPt(rec.RingLat, rec.RingLon);
            if (_now >= rec.RingNextS)
            {
                var dropper = Dropper(g, cg);
                double fly = dropper == null ? 0 : FlyS(dropper, cg);
                double r = AswLocalizeMath.FocR(rec.SpeedKn, _now - rec.RingFromS, fly, _cfg.Loc.LostSubKn);
                if (r > _cfg.Loc.LostMaxNm + 1e-9) { EndRing(g, rec, "max"); return; }
                rec.RingNextS = _now + _cfg.Loc.LostExpandS;
                if (dropper == null) { return; }
                string ammo; BuoyType bt;
                if (!PickFor(dropper, out ammo, out bt)) return;
                double rr = AswMath.REff(bt, g.P).Nm, d = AswMath.Spacing(rr);
                double lostS = _now - rec.RingFromS;
                var shiftV = AswLocalizeMath.Lead(rec.CourseDeg, double.IsNaN(rec.SpeedKn) ? _cfg.Loc.LostSubKn : rec.SpeedKn, lostS / 2);
                double shiftNm = Math.Sqrt(shiftV.X * shiftV.X + shiftV.Y * shiftV.Y);
                NmPt cs;
                var ring = AswLocalizeMath.FocRing(Math.Max(r, 0.5), d, rec.CourseDeg, out cs, shiftNm);
                var pts = ToGeoList(cg, 0, ring);
                rec.RingR = r; rec.RingPts = new List<GeoPt>(pts); rec.RingDipped.Clear();
                var line = new AswLineView { Kind = "lost_ring" }; line.Points.AddRange(pts);
                g.Lines.RemoveAll(l => l.Kind == "lost_ring"); g.Lines.Add(line);
                int avail = AvailFor(dropper, bt);
                var lay = new List<GeoPt>(pts);
                if (lay.Count > avail) lay.RemoveRange(avail, lay.Count - avail);
                _game.Log("asw contact " + g.Name + " id=" + I(rec.Id) + " ring r=" + N(Math.Round(r, 2)) + " pts=" + I(pts.Count) + " buoys=" + I(lay.Count));
                if (lay.Count > 0) IssueDrop(g, dropper, ammo, bt, lay, "lost_ring", null, "contact", AswMath.LocalizeGapNm(rr));
            }
            // the dipping sonar goes over the ring points: the nearest one not visited yet (leapfrog)
            AswMember dipper = Nearest(g, cg, m => m.Dipping);
            Job j;
            if (dipper == null || rec.RingPts.Count == 0 || AirRetreatOf(dipper.Uid) || (g.Jobs.TryGetValue(dipper.Uid, out j) && _now < j.DoneAt)) return;
            int best = -1; double bd = 0;
            for (int i = 0; i < rec.RingPts.Count; i++)
            {
                if (rec.RingDipped.Contains(i)) continue;
                double dd = AswMath.DistNm(new GeoPt(dipper.Lat, dipper.Lon), rec.RingPts[i]);
                if (best < 0 || dd < bd) { best = i; bd = dd; }
            }
            if (best < 0) return;
            rec.RingDipped.Add(best);
            if (Guard(g, new List<GeoPt> { rec.RingPts[best] }, "dip", 0, dipper).Count == 0) return;
            CancelRoute(g, dipper.Uid);
            _game.Dip(dipper.Uid, new List<GeoPt> { rec.RingPts[best] });
            g.Jobs[dipper.Uid] = new Job { Uid = dipper.Uid, Tactic = "contact", Count = 1, DoneAt = _now + g.P.DipHoldS + 60 };
        }
    }
}
