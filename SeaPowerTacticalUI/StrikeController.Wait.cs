using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B: the group tick (1 s of the mission clock): the members, the fuel, the way to the wait point and the orbit, the wait point itself.
    internal sealed partial class StrikeController
    {
        private sealed class MState { public GeoPt? Sent; public string Kind; public double SentAt = double.MinValue; }

        private readonly Dictionary<int, MState> _ms = new Dictionary<int, MState>();
        private bool _roeLogged;
        private readonly Dictionary<string, string> _heldWhy = new Dictionary<string, string>();
        private const double RetryS = 30, MovedNm = 2;

        private static string Pt(GeoPt p) { return p.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + p.Lon.ToString("0.0000", CultureInfo.InvariantCulture); }

        private void ReleaseGroupClaims(StrikeGroup g)
        {
            foreach (int uid in g.Members.ToList()) { EwDrop(g, uid); _ms.Remove(uid); RoeBack(uid); Book.Clear(uid); RestoreUidSpeed(uid); }
            foreach (string k in g.DeckKeys) ForgetDeckKey(k);
            if (Ownership != null) Ownership.ReleaseGroup("strike", g.Name);
        }

        /// <summary>"Return control" of one aircraft: out of its group without any order (the route and the ROE come back through the normal restore of the game layer).</summary>
        public void Release(int uid)
        {
            var g = GroupOf(uid);
            if (g == null) return;
            EwDrop(g, uid);
            g.Members.Remove(uid);
            _ms.Remove(uid);
            RoeBack(uid);
            RestoreUidSpeed(uid);
            ClearDrop(g, uid);
            if (Ownership != null) Ownership.Release(uid);
            g.Left.Add(new StrikeLeave { Uid = uid, Why = "released", T = _g.Now });
            _log("[Strike] strike leave " + g.Name + " uid=" + I(uid) + " why=released");
            if (g.Members.Count == 0) DeckAbortAll(g, "release");
            if (g.Timeline != null) MarkStale(g, "aircraft_changed");
        }

        /// <summary>The veto rule of the participant "strike" (ADR-0132 section 6): an automatic anti-ship missile launch of a member of any group is held unless the mod expects it (the launch book).</summary>
        public bool ShouldVeto(int uid, bool isAsm, bool expected)
        {
            string g;
            return IsMember(uid, out g) && StrikeLaunchBook.Veto(true, true, isAsm, expected);
        }

        private void Leave(StrikeGroup g, int uid, string why, double t, bool rtb)
        {
            EwDrop(g, uid);
            string err = rtb ? _g.Rtb(uid) : null;
            g.Members.Remove(uid);
            _ms.Remove(uid);
            RoeBack(uid);
            ClearDrop(g, uid);
            if (Ownership != null) Ownership.Release(uid);
            g.Left.Add(new StrikeLeave { Uid = uid, Why = why, T = t });
            if (g.Left.Count > 20) g.Left.RemoveAt(0);
            _log("[Strike] strike leave " + g.Name + " uid=" + I(uid) + " why=" + why + (err == null ? "" : " rtb=" + err));
            if (g.Timeline != null) MarkStale(g, why == "lost" ? "aircraft_lost" : "aircraft_changed");
        }

        private static GeoPt? Centre(IEnumerable<StrikeAircraft> list)
        {
            double la = 0, lo = 0; int n = 0;
            foreach (var a in list) { la += a.Lat; lo += a.Lon; n++; }
            return n == 0 ? (GeoPt?)null : new GeoPt(la / n, lo / n);
        }

        /// <summary>The hostile ships whose zones the wait point must stay out of: every known hostile surface contact that is not civilian.</summary>
        private List<StrikeShip> Hostiles()
        {
            var res = new List<StrikeShip>();
            var ships = _g.Ships();
            if (ships != null) foreach (var s in ships) if (s != null && s.Hostile && !s.Civilian) res.Add(s);
            return res;
        }

        /// <summary>The wait point of the group: on the reverse axis from the target (nearest hostile with a SAM when there is none) outside every known hostile SAM zone + the standoff + the orbit, over the sea.</summary>
        private void RecomputeWait(StrikeGroup g, string trigger)
        {
            // the axis starts at the group, so a recount while the group flies would drift the point: a point that is still safe stays unless the target or the hostiles changed
            if (g.WaitPt.HasValue && (trigger == "plan" || trigger == "cancel" || trigger == "add" || trigger == "remove" || trigger == "adopt")
                && !StrikeWaitMath.WaitUnsafe(g.WaitPt.Value, Hostiles(), _p)) return;
            var air = new List<StrikeAircraft>();
            foreach (int uid in g.Members) { var a = Air(uid); if (a != null) air.Add(a); }
            var air2 = air.Where(a => a.Airborne).ToList();
            GeoPt? from = Centre(air2.Count > 0 ? air2 : air);
            if (!from.HasValue)
            {
                // only prepared deck tasks: the way starts at the deck
                foreach (string key in g.DeckKeys) { StrikeDeckItem it; if (_deckInfo.TryGetValue(key, out it) && it != null) { from = new GeoPt(it.Lat, it.Lon); break; } }
            }
            if (!from.HasValue) { g.WaitPt = null; g.WaitWhy = null; return; }
            var hostiles = Hostiles();
            if (hostiles.Count == 0)
            {
                if (g.WaitPt.HasValue) _ms.Clear();
                g.WaitPt = null; g.WaitWhy = "no_hostiles";
                return;
            }
            GeoPt? target = null;
            if (g.Target != null && g.Target.MainId != 0)
            {
                var ts = hostiles.FirstOrDefault(s => s.TrackId == g.Target.MainId) ?? Ship(g.Target.MainId);
                if (ts != null) target = new GeoPt(ts.Lat, ts.Lon);
            }
            GeoPt aimAt;
            if (target.HasValue) aimAt = target.Value;
            else
            {
                var pool = hostiles.Where(s => s.SamMaxNm > 0).ToList();
                if (pool.Count == 0) pool = hostiles;
                var near = pool.OrderBy(s => AswMath.DistNm(from.Value, new GeoPt(s.Lat, s.Lon))).First();
                aimAt = new GeoPt(near.Lat, near.Lon);
            }
            double axis = StrikeGeoMath.AxisDeg(from.Value, aimAt);
            string why;
            var pt = StrikeWaitMath.WaitPoint(hostiles, target, from.Value, axis, _p, p => { try { return _g.IsLand(p); } catch (Exception) { return false; } }, out why);
            if (g.WaitPt.HasValue && AswMath.DistNm(g.WaitPt.Value, pt) <= MovedNm && g.WaitWhy == why && trigger != "new_sam") return;   // a stable point stays
            bool moved = !g.WaitPt.HasValue || AswMath.DistNm(g.WaitPt.Value, pt) > MovedNm;
            g.WaitPt = pt; g.WaitR = _p.WaitOrbitNm; g.WaitWhy = why;
            if (moved) foreach (int uid in g.Members) _ms.Remove(uid);
            _log("[Strike] strike wait " + g.Name + " pt=" + Pt(pt) + " why=" + why + " r_nm=" + F1(_p.WaitOrbitNm) + " trigger=" + trigger);
        }

        /// <summary>Every StaleCheckS: no wait point yet (a hostile may have appeared) or a new SAM covers it.</summary>
        private void CheckWait(double now)
        {
            foreach (var g in _groups.ToList())
            {
                if (g.Members.Count == 0) continue;
                if (!g.WaitPt.HasValue) { RecomputeWait(g, "retry"); continue; }
                if (StrikeWaitMath.WaitUnsafe(g.WaitPt.Value, Hostiles(), _p)) RecomputeWait(g, "new_sam");
            }
        }

        private void TickGroups(double t)
        {
            if (!_roeLogged && _groups.Count > 0) { _roeLogged = true; _log("[Strike] strike roe mode=" + _p.RoeMode + " effective=" + EffectiveRoe); }
            foreach (var g in _groups.ToList()) TickGroup(g, t);
        }

        private void TickGroup(StrikeGroup g, double t)
        {
            // members: lost, fuel, missiles
            foreach (int uid in g.Members.ToList())
            {
                var a = Air(uid);
                if (a == null) { Leave(g, uid, "lost", t, false); continue; }
                if (!a.Airborne) continue;
                if (Busy(g, uid)) continue;   // phase C: an aircraft in the middle of a strike is not sent home or to the wait point
                string fuel = StrikeWaitMath.FuelLeave(a, _p);
                if (fuel != null) { Leave(g, uid, fuel, t, true); continue; }
                if (StrikeAirMath.UsableCount(a) <= 0 && StrikeAirMath.RoleOf(a) != "ew") { Leave(g, uid, "no_missiles", t, true); continue; }
            }
            // the way to the wait point, the orbit
            int airborne = 0, waiting = 0;
            foreach (int uid in g.Members)
            {
                var a = Air(uid);
                if (a == null || !a.Airborne) continue;
                airborne++;
                if (Busy(g, uid)) continue;
                if (!g.WaitPt.HasValue || g.WaitWhy == "no_sea" || g.WaitWhy == "no_point") continue;   // review of phase B: no point, no orders
                var wp = g.WaitPt.Value;
                MState st;
                if (!_ms.TryGetValue(uid, out st)) { st = new MState(); _ms[uid] = st; }
                double d = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), wp);
                bool orbiting = st.Kind == "orbit";
                bool near = orbiting ? d <= _p.WaitOrbitNm + _p.WaitArriveNm + 1 : d <= _p.WaitArriveNm;
                if (near)
                {
                    if (orbiting) { waiting++; continue; }
                    if (t - st.SentAt < RetryS && st.Kind == "orbit_err") continue;
                    string err = _g.Orbit(uid, wp, _p.WaitOrbitNm);
                    if (err == null) { st.Sent = wp; st.Kind = "orbit"; st.SentAt = t; waiting++; _log("[Strike] strike gather " + g.Name + " uid=" + I(uid) + " -> orbit"); }
                    else { st.Kind = "orbit_err"; st.SentAt = t; _log("[Strike] strike gather " + g.Name + " uid=" + I(uid) + " -> orbit failed " + err); }
                }
                else
                {
                    bool sent = st.Kind == "goto" && st.Sent.HasValue && AswMath.DistNm(st.Sent.Value, wp) <= MovedNm;
                    if (sent || (st.Kind == "goto_err" && t - st.SentAt < RetryS)) continue;
                    string err = _g.Goto(uid, new List<GeoPt> { wp }, 0);
                    if (err == null) { st.Sent = wp; st.Kind = "goto"; st.SentAt = t; _log("[Strike] strike gather " + g.Name + " uid=" + I(uid) + " -> goto"); }
                    else { st.Kind = "goto_err"; st.SentAt = t; _log("[Strike] strike gather " + g.Name + " uid=" + I(uid) + " -> goto failed " + err); }
                }
            }
            TickDeckOrders(g, t);
            TickDeck(g, t);
            bool noPoint = g.WaitWhy == "no_sea" || g.WaitWhy == "no_point";
            string loggedWhy;
            _heldWhy.TryGetValue(g.Name, out loggedWhy);
            if (noPoint && loggedWhy != g.WaitWhy) { _heldWhy[g.Name] = g.WaitWhy; _log("[Strike] strike gather " + g.Name + " -> held why=" + g.WaitWhy); }
            else if (!noPoint) _heldWhy.Remove(g.Name);
            g.Gather = noPoint || !g.WaitPt.HasValue || g.Members.Count + g.DeckKeys.Count == 0 ? "idle" : (airborne > 0 && waiting == airborne && g.DeckKeys.Count == 0 ? "waiting" : "gather");
        }
    }
}
