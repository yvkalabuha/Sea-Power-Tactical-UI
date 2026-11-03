using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;


namespace VoiceBotBridge
{
    // T-121: part of the ASW controller (see AswController.cs).

    internal sealed partial class AswController
    {
        // ---- contacts and attack -----------------------------------------------------------------------------------

        private bool InZone(Grp g, AswTarget t, AswContact c) { return InZonePt(g, t, new GeoPt(c.Lat, c.Lon)); }

        /// <summary>Own buoys of the members that were already in the water when the order was given and lie in the zone are counted in.</summary>
        private void ScanPre(Grp g, AswTarget t)
        {
            if (g.PreScanned) return;
            g.PreScanned = true;
            int n = 0;
            foreach (var b in _game.OwnBuoys())
            {
                if (!g.Members.Contains(b.OwnerUid) || g.PreSeen.Contains(b.Id)) continue;
                var p = new GeoPt(b.Lat, b.Lon);
                bool known = false;
                foreach (var x in g.Buoys) if (AswMath.DistNm(new GeoPt(x.Lat, x.Lon), p) < 1.0) { known = true; break; }
                if (known || !InZonePt(g, t, p)) continue;
                g.PreSeen.Add(b.Id);
                double dropped = b.DroppedS >= 0 ? b.DroppedS : _now;
                // T-172 R6: the life the game reports (Bomb.RemainingLife) wins over the nominal one of the type; 0 = already dead, covers nothing
                double life = b.RemainingS >= 0 ? (_now - dropped) + b.RemainingS : b.LifeS;
                g.Buoys.Add(new BuoyRec { Lat = b.Lat, Lon = b.Lon, DroppedS = dropped, LifeS = life, Ammo = b.Ammo, Pre = true });
                n++;
            }
            if (n > 0) _game.Log("asw pre-order buoys taken into " + g.Name + ": " + I(n));
        }

        private bool InZonePt(Grp g, AswTarget t, GeoPt cg)
        {
            double r = Math.Max(1.0, g.RNm > 0 ? g.RNm : 2.7);
            if (g.Order == "area" && g.Rect != null)
            {
                GeoPt ctr; double north, east;
                AswMath.RectSpans(g.Rect, out ctr, out north, out east);
                var l = AswMath.ToLocal(ctr, 0, cg);
                return Math.Abs(l.X) <= north / 2 + r && Math.Abs(l.Y) <= east / 2 + r;
            }
            if (t == null) return false;
            if (AswMath.DistNm(Pos(t), cg) < g.P.DMinNm) return true;
            var loc = AswMath.ToLocal(Pos(t), g.Axis, cg);
            // T-176: the zone of a by-course order: up to the farthest standoff ahead, across by the widest of the corridor / barrier (at least the old 2.8 R + 2)
            var spec = g.Plan.Course;
            double reach = Math.Max(spec.DipLNm, Math.Max(g.Tactics.Contains("corridor") ? spec.CorridorLNm : 0, g.Tactics.Contains("barrier") ? spec.BarrierRows * spec.BarrierGapNm : 0)) + 3.0;
            double half = Math.Max(Math.Max(g.Tactics.Contains("corridor") ? spec.CorridorXNm / 2 : 0, g.Tactics.Contains("barrier") ? spec.BarrierXNm / 2 : 0), 2.8 * r + 2.0);
            return loc.X >= -r && loc.X <= reach + r && Math.Abs(loc.Y) <= half + r;
        }

        private AswMember Nearest(Grp g, GeoPt p, Func<AswMember, bool> ok)
        {
            AswMember best = null; double bd = double.MaxValue;
            foreach (int u in g.Members)
            {
                AswMember m; if (!_live.TryGetValue(u, out m)) continue;   // waiting to be adopted
                if (m.Where != "air" || AswMath.IsLeaving(m) || !ok(m)) continue;   // T-172 B10: an aircraft the game sends home takes no part
                double d = AswMath.DistNm(new GeoPt(m.Lat, m.Lon), p);
                if (d < bd) { bd = d; best = m; }
            }
            return best;
        }

        private readonly HashSet<string> _decoyLogged = new HashSet<string>();   // "group/id" of decoys already reported as ignored

        private void Contacts(Grp g, AswTarget t)
        {
            var subs = SubsTick();
            var seenIds = new HashSet<int>();
            foreach (var c in subs)
            {
                if (!c.Hostile) continue;
                ContactRec rec = null;
                foreach (var x in g.Contacts) if (x.Id == c.Id) rec = x;
                if (c.Killed)
                {
                    // T-172: the submarine was destroyed: no lost-contact square, the attack run ends with it (the event is sent once per track, EmitKilled)
                    if (rec != null) Killed(g, rec, c);
                    continue;
                }
                if (c.Biologic != null) continue;   // T-173: a whale / a school is never a contact
                if (c.Decoy != null)
                {
                    // 0.27.10: a noisemaker / decoy is no contact: dropped when it is reclassified, never added
                    if (rec != null)
                    {
                        g.Contacts.Remove(rec);
                        _game.Log("asw contact " + g.Name + " id=" + I(c.Id) + " reclassified decoy (" + c.Decoy + "): dropped");
                        _game.Event("asw_contact_lost", Obj("group", g.Name, "contact", rec.Id, "track", rec.Name, "lat", rec.Lat, "lon", rec.Lon, "reason", "decoy"));
                    }
                    else if (_decoyLogged.Add(g.Name + "/" + I(c.Id))) _game.Log("asw contact " + g.Name + " id=" + I(c.Id) + " ignored: decoy (" + c.Decoy + ")");
                    continue;
                }
                if (rec == null)
                {
                    if (!c.Live) continue;   // T-172: a stale track (the game does not hold it any more) is no new contact
                    if (!InZone(g, t, c)) continue;
                    rec = new ContactRec { Id = c.Id, Name = c.Name, Lat = c.Lat, Lon = c.Lon, FirstS = _now, LastSeenS = _now, NextDipS = _now };
                    g.Contacts.Add(rec);
                    double brg = 0, dist = 0;
                    if (t != null) { brg = AswMath.BearingDeg(Pos(t), new GeoPt(c.Lat, c.Lon)); dist = AswMath.DistNm(Pos(t), new GeoPt(c.Lat, c.Lon)); }
                    _game.Log("asw contact " + g.Name + " id=" + I(c.Id) + " source=" + (c.Source ?? "-") + " side=" + (c.Side ?? "-"));
                    _game.Event("asw_contact", Obj("group", g.Name, "contact", c.Id, "track", c.Name, "source", c.Source, "bearing", Math.Round(brg), "dist_nm", Math.Round(dist, 1), "lat", c.Lat, "lon", c.Lon));
                    rec.CourseDeg = c.CourseDeg; rec.SpeedKn = c.SpeedKn;
                    Localize(g, rec);
                }
                rec.Lat = c.Lat; rec.Lon = c.Lon;   // the estimate follows the track even when it is stale
                if (!double.IsNaN(c.CourseDeg)) rec.CourseDeg = c.CourseDeg;
                if (!double.IsNaN(c.SpeedKn)) rec.SpeedKn = c.SpeedKn;
                if (!c.Live) continue;   // T-172: a stale track does not keep the contact: it is lost after LostAfterS as if it was not listed
                rec.LastSeenS = _now;
                seenIds.Add(c.Id);
            }
            foreach (var rec in new List<ContactRec>(g.Contacts))
            {
                // a track that vanishes right after a weapon was released at it (no destroyed flag is readable) is taken as destroyed
                double hit;
                if (_hitAt.TryGetValue(rec.Id, out hit) && _now - rec.LastSeenS >= HitVanishS && rec.LastSeenS >= hit - 5 && _now - hit < 400 && !seenIds.Contains(rec.Id))
                {
                    KillRec(g, rec, rec.Lat, rec.Lon, "gone after a hit");
                    continue;
                }
                if (rec.Ring)
                {
                    if (seenIds.Contains(rec.Id)) { EndRing(g, rec, "found"); continue; }   // the track is back: the ring is not needed
                    RingStep(g, rec);
                    continue;
                }
                if (_now - rec.LastSeenS >= g.P.LostAfterS) { Lost(g, rec); continue; }
                if (seenIds.Contains(rec.Id)) HoldContact(g, rec);
            }
        }

        private readonly HashSet<int> _killedIds = new HashSet<int>();   // contacts reported destroyed (an attack run on them ends with "contact_killed")

        private readonly Dictionary<int, double> _hitAt = new Dictionary<int, double>();   // contact id -> time a weapon left an aircraft at it (a track that vanishes right after is taken as destroyed)

        private void Killed(Grp g, ContactRec rec, AswContact c)
        {
            KillRec(g, rec, c.Lat != 0 || c.Lon != 0 ? c.Lat : rec.Lat, c.Lat != 0 || c.Lon != 0 ? c.Lon : rec.Lon, null, true);
        }

        /// <summary>
        /// T-172 (live 0.29.0): the contact is destroyed. It leaves the group's list (the marker goes), its search / localization drawing near the datum goes,
        /// the aircraft that held it is freed (its dip on a dead track is cancelled, its tactic goes on); no square of lost-contact buoys is laid.
        /// </summary>
        private void KillRec(Grp g, ContactRec rec, double lat, double lon, string why, bool quiet = false)
        {
            g.Contacts.Remove(rec);
            _killedIds.Add(rec.Id);
            var datum = new GeoPt(lat, lon);
            g.Lines.RemoveAll(l =>
            {
                if (l.Kind != "contact") return false;
                foreach (var p in l.Points) if (AswMath.DistNm(p, datum) <= KillClearNm) return true;
                return false;
            });
            AswMember h;
            if (rec.Holder != 0 && _live.TryGetValue(rec.Holder, out h) && h.Where == "air")
            {
                Job j;
                if (g.Jobs.TryGetValue(rec.Holder, out j) && j.Tactic == "contact") g.Jobs.Remove(rec.Holder);
                if (h.Dipping) CancelRoute(g, rec.Holder);
            }
            rec.Holder = 0;
            if (quiet || !_killedEmitted.Add(rec.Id)) return;
            _game.Log("asw contact " + g.Name + " killed contact=" + I(rec.Id) + " track=" + rec.Name + (why == null ? "" : " (" + why + ")"));
            _game.Event("asw_contact_killed", Obj("group", g.Name, "contact", rec.Id, "track", rec.Name, "lat", lat, "lon", lon));
            NoteAlert(rec.Id, "killed");
        }

        /// <summary>Lines of a contact (cross, square) within this distance of its datum are cleared when it is destroyed.</summary>
        private const double KillClearNm = 12;

        /// <summary>Seconds a contact must be unseen after a release at it to count as destroyed.</summary>
        internal const double HitVanishS = 30;

        // while no group works the controller does not scan the game; the cards "submarine detected" still need the tracks now and then
        private double _idleScanAt;

        private void IdleScan()
        {
            if (_now < _idleScanAt) return;
            _idleScanAt = _now + 3;
            _tickNo++;
            ScanSubs();
            UpdateAlerts();
            RebuildView();
        }
    }
}
